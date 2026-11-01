using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-census &lt;dir|file.txt&gt; [--frames 1500] [--jobs 2] [--every 3] [--out census.tsv] [--script default|mash|start]</c>: which VDP features the library's games use (Track B4a, to decide what to polish first). Every
/// <c>--every</c>-th ROM of the folder (sorted) runs headless with the sweep's pad script; each frame samples the VDP registers (interlace, V30, H32, shadow/highlight, window, column and line scroll, HINT, display off)
/// and at the end the counters of writes that land while a picture line is drawn (register, colour RAM, vertical scroll RAM, VRAM: the mid-frame tricks) and of interrupts taken. One TSV row per game; a summary of the
/// number of games that use each feature in at least 2 percent of their frames (or at least 20 mid-frame writes). Raw results stay out of git.
/// </summary>
internal static class MdCensusCli
{
    private sealed class Row
    {
        public string File = "", Title = "";
        public int Frames;
        public Dictionary<string, long> F = new();
        public string Note = "";
    }

    private static readonly string[] Feat = { "il1", "il2", "v30", "h32", "shadow", "window", "colvs", "hsline", "hscell", "hint", "dispoff", "leftblank", "pal", "w128", "h128" };

    public static int Run(string[] args)
    {
        string? src = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        if (src == null) { Console.WriteLine("usage: --md-census <dir> [--frames 1500] [--jobs 2] [--every 3] [--out census.tsv] [--script default]"); return 2; }
        int frames = int.TryParse(M68kTestCli.Arg(args, "--frames"), out int f0) ? f0 : 1500;
        int jobs = int.TryParse(M68kTestCli.Arg(args, "--jobs"), out int j0) ? j0 : 2;
        int every = int.TryParse(M68kTestCli.Arg(args, "--every"), out int e0) ? e0 : 3;
        int offset = int.TryParse(M68kTestCli.Arg(args, "--offset"), out int o0) ? o0 : 0;
        string script = M68kTestCli.Arg(args, "--script") ?? "default";
        string? outPath = M68kTestCli.Arg(args, "--out");
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".bin", ".gen", ".md", ".smd" };
        var files = Directory.EnumerateFiles(src).Where(p => exts.Contains(Path.GetExtension(p))).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Where((p, i) => i % every == offset % every).ToList();
        Console.WriteLine($"census of {files.Count} ROMs, {frames} frames each, {jobs} jobs");
        var rows = new Row[files.Count];
        int done = 0;
        var cols = Feat.Concat(new[] { "actReg", "actCram", "actVsram", "actVram", "hintTaken", "vintTaken", "sprOverflowLines", "reg7", "reg11", "reg12", "reg0", "reg1", "reg10", "reg13", "reg2", "reg4", "reg5", "reg16", "reg17", "reg18" }).ToArray();
        string header = "file\ttitle\tframes\t" + string.Join('\t', cols) + "\tnote";
        string RowLine(Row r) => $"{r.File}\t{r.Title}\t{r.Frames}\t" + string.Join('\t', cols.Select(c => r.F.TryGetValue(c, out var v) ? v.ToString() : "0")) + "\t" + r.Note;
        // every finished game is appended to <out>.part at once, so a run that is cut short still leaves its rows
        string? partPath = outPath != null ? outPath + ".part" : null;
        if (partPath != null) File.WriteAllText(partPath, header + "\n");
        var partLock = new object();
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            if (M68kTestCli.Flag(args, "--verbose")) Console.Error.WriteLine($"  start {Path.GetFileName(files[i])}");
            try { rows[i] = RunOne(files[i], frames, script); }
            catch (Exception e) { rows[i] = new Row { File = Path.GetFileName(files[i]), Note = "crash: " + e.GetType().Name }; }
            if (partPath != null) lock (partLock) File.AppendAllText(partPath, RowLine(rows[i]) + "\n");
            int d = Interlocked.Increment(ref done);
            if (d % 20 == 0) Console.WriteLine($"  {d}/{files.Count}");
        });
        var sb = new StringBuilder();
        sb.AppendLine(header);
        foreach (var r in rows) sb.AppendLine(RowLine(r));
        if (outPath != null) { File.WriteAllText(outPath, sb.ToString()); Console.WriteLine($"wrote {outPath}"); }
        var ok = rows.Where(r => r.Frames > 0).ToList();
        Console.WriteLine($"\n{ok.Count} games ran; games using each feature in >= 2% of their frames:");
        foreach (var f in Feat) Console.WriteLine($"  {f,-10} {ok.Count(r => r.F.TryGetValue(f, out var v) && v * 50 >= r.Frames),4}");
        Console.WriteLine("games with mid-frame writes while a picture line is drawn (>= 20 in the run):");
        foreach (var f in new[] { "actReg", "actCram", "actVsram", "actVram" }) Console.WriteLine($"  {f,-10} {ok.Count(r => r.F.TryGetValue(f, out var v) && v >= 20),4}");
        Console.WriteLine($"  HINT taken {ok.Count(r => r.F.TryGetValue("hintTaken", out var v) && v >= 20),4}   sprite overflow lines {ok.Count(r => r.F.TryGetValue("sprOverflowLines", out var v) && v >= 20),4}");
        return 0;
    }

    private static Row RunOne(string path, int frames, string script)
    {
        var row = new Row { File = Path.GetFileName(path) };
        byte[] data = File.ReadAllBytes(path);
        var session = new MdSession(data, "M68K", "MD", "MD");
        var vdp = session.Board.Vdp;
        row.Title = session.Title.Replace('\t', ' ');
        var f = row.F;
        void Inc(string k) => f[k] = f.TryGetValue(k, out var v) ? v + 1 : 1;
        var reg7 = new Dictionary<int, int>(); var seen = new Dictionary<string, HashSet<int>>();
        void See(string k, int v) { if (!seen.TryGetValue(k, out var s)) seen[k] = s = new HashSet<int>(); s.Add(v); }
        int i = 0;
        for (; i < frames; i++)
        {
            session.SetPad(0, MdSweepCli.PadAt(script, i));
            session.RunFrame();
            if (session.Board.Cpu.Halted) { i++; break; }
            if (i < 60) continue;
            var r = vdp.Reg;
            int il = r[12] >> 1 & 3;
            if (il == 1) Inc("il1"); if (il == 3) Inc("il2");
            if (vdp.V30) Inc("v30"); if (!vdp.H40) Inc("h32");
            if ((r[12] & 8) != 0) Inc("shadow");
            if (r[17] != 0 || r[18] != 0) Inc("window");
            if ((r[11] & 4) != 0) Inc("colvs");
            if ((r[11] & 3) == 3) Inc("hsline"); if ((r[11] & 3) == 2) Inc("hscell");
            if ((r[0] & 0x10) != 0) Inc("hint");
            if (!vdp.DisplayEnabled) Inc("dispoff");
            if ((r[0] & 0x20) != 0) Inc("leftblank");
            if (session.Board.Region == SegaRegion.Pal) Inc("pal");
            if ((r[16] & 3) == 3) Inc("w128"); if ((r[16] >> 4 & 3) == 3) Inc("h128");
            foreach (int k in new[] { 0, 1, 2, 4, 5, 7, 10, 11, 12, 13, 16, 17, 18 }) See("reg" + k, r[k]);
        }
        row.Frames = i;
        f["actReg"] = vdp.ActiveRegWrites; f["actCram"] = vdp.ActiveCramWrites; f["actVsram"] = vdp.ActiveVsramWrites; f["actVram"] = vdp.ActiveVramWrites;
        f["hintTaken"] = vdp.HintTaken; f["vintTaken"] = vdp.VintTaken; f["sprOverflowLines"] = vdp.SpriteOverflowLines;
        foreach (var kv in seen) f[kv.Key] = kv.Value.Count;       // how many distinct values the register took (a register that changes during play is a raster or mode trick)
        // the registers written most during active display
        var by = vdp.ActiveRegWritesBy.Select((c, n) => (c, n)).Where(x => x.c >= 20).OrderByDescending(x => x.c).Take(4).Select(x => $"R{x.n}:{x.c}");
        row.Note = string.Join(' ', by);
        return row;
    }
}
