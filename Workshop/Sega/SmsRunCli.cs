using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--sms-run &lt;rom&gt; [--frames N] [--input "f:Btn+Btn,f:"] [--input2 ...] [--png-at f1,f2] [--out dir] [--tag name] [--hash] [--hash-at f1,f2] [--region ntsc|pal]
/// [--model sms1|sms2|markiii|sg1000|sc3000] [--console sms|gg] [--wav out.wav] [--trace-z80 N] [--trace-vdp] [--dump-vram file] [--battery file] [--battery-out file]
/// [--mesen dir]</c>: runs a Master System / Game Gear / SG-1000 ROM headless on the real session and reports what it did.
/// <para>Buttons are <see cref="PadButtons"/> names (<c>A</c> = button 1, <c>B</c> = button 2, <c>Select</c> = Pause, <c>Start</c> = Game Gear Start, <c>Mode</c> = Reset). <c>"60:Start,70:"</c> holds Start from frame 60
/// until frame 70 (frames are counted from 0 and an entry holds until the next).</para>
/// <para><c>--mesen dir</c> compares each <c>--png-at</c> frame with <c>dir/&lt;tag&gt;_f&lt;frame&gt;.png</c>, a screenshot the Mesen capture script (<c>Workshop/Sega/mesen/capture.lua</c>) took at the same frame, and prints the differing pixels.</para>
/// </summary>
internal static class SmsRunCli
{
    public static int Run(string[] args)
    {
        string? Opt(string name) { int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        bool Flag(string name) => args.Any(a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
        string? romPath = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Opt("rom");
        if (romPath == null)
        {
            Console.WriteLine("usage: --sms-run <rom> [--frames N] [--input spec] [--input2 spec] [--png-at f1,f2] [--out dir] [--tag name] [--hash] [--hash-at f1,f2] [--region ntsc|pal]");
            Console.WriteLine("                     [--model sms1|sms2|markiii|sg1000|sc3000] [--console sms|gg] [--wav f] [--trace-z80 N] [--trace-vdp] [--dump-vram f] [--battery f] [--battery-out f] [--mesen dir]");
            return 2;
        }
        byte[] rom = SmsRomIo.Load(romPath, out string inner);
        var detected = SegaRomFormat.Detect(rom, inner);
        var console = Opt("console") is { } c ? Consoles.FromKey(c) : detected ?? ConsoleKind.MasterSystem;
        if (console is not (ConsoleKind.MasterSystem or ConsoleKind.GameGear)) { Console.WriteLine($"not a Master System / Game Gear ROM ({detected})"); return 2; }
        int frames = int.Parse(Opt("frames") ?? "600");
        var shots = (Opt("png-at") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        var hashAt = (Opt("hash-at") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        string outDir = Opt("out") ?? ".", tag = Opt("tag") ?? Path.GetFileNameWithoutExtension(inner);
        Directory.CreateDirectory(outDir);
        string? mesenDir = Opt("mesen");
        var script1 = SmsRomIo.ParseInput(Opt("input") ?? ""); var script2 = SmsRomIo.ParseInput(Opt("input2") ?? "");
        RegionChoice? region = Opt("region") is { } r ? Enum.Parse<RegionChoice>(r, true) : null;
        MasterSystemModel? model = Opt("model") is { } m ? Enum.Parse<MasterSystemModel>(m, true) : null;

        var session = console == ConsoleKind.GameGear
            ? (SmsSession)new GgSession(rom, "Z80", "GG", "GG", inner, region)
            : new SmsSession(rom, console, "Z80", "SMS", "SMS", inner, model, region);
        var board = session.Board;
        Console.WriteLine(session.Description);
        if (Opt("battery") is { } bat && File.Exists(bat)) { session.ImportSave(File.ReadAllBytes(bat)); Console.WriteLine($"battery loaded: {bat}"); }

        // tracing
        int traceN = int.Parse(Opt("trace-z80") ?? "0");
        var trace = new Queue<string>();
        if (traceN > 0)
            board.InstructionHook = pc =>
            {
                var c = board.Cpu;
                trace.Enqueue($"{pc:X4} {board.Peek(pc):X2} {board.Peek((ushort)(pc + 1)):X2} {board.Peek((ushort)(pc + 2)):X2}  AF={c.AF:X4} BC={c.BC:X4} DE={c.DE:X4} HL={c.HL:X4} SP={c.SP:X4} IX={c.IX:X4} IY={c.IY:X4} IFF={(c.IFF1 ? 1 : 0)} line={board.Vdp.Line}");
                if (trace.Count > traceN) trace.Dequeue();
            };
        if (Flag("trace-vdp"))
        {
            long f = 0;
            board.Vdp.RegisterWriteHook = (line, mc, reg, value) => Console.WriteLine($"VDP frame {board.Frames} line {line} mc {mc}: R{reg} = ${value:X2}");
        }

        var pcm = new List<short>();
        var abuf = new short[16384];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ulong hash = 0xcbf29ce484222325, hashLast = 0;
        PadButtons p1 = 0, p2 = 0;
        int mesenDiffFrames = 0, mesenFramesCompared = 0;
        for (int f = 0; f < frames; f++)
        {
            if (script1.TryGetValue(f, out var h1)) p1 = h1;
            if (script2.TryGetValue(f, out var h2)) p2 = h2;
            session.SetPad(0, p1); session.SetPad(1, p2);
            session.RunFrame();
            int n; while ((n = session.ReadSamples(abuf)) > 0) if (Opt("wav") != null) for (int i = 0; i + 1 < n; i += 2) pcm.Add((short)((abuf[i] + abuf[i + 1]) / 2));
            int frameNo = f + 1;
            if (Flag("hash") || hashAt.Contains(frameNo))
            {
                hashLast = HashFrame(session);
                if (hashAt.Contains(frameNo)) Console.WriteLine($"frame {frameNo} hash {hashLast:X16}");
            }
            if (shots.Contains(frameNo))
            {
                string path = Path.Combine(outDir, $"{tag}_f{frameNo}.png");
                SmsPng.Save(session.Frame, session.FrameWidth, session.FrameHeight, path);
                if (mesenDir != null)
                {
                    string theirs = Path.Combine(mesenDir, $"{tag}_f{frameNo}.png");
                    mesenFramesCompared++;
                    if (!File.Exists(theirs)) Console.WriteLine($"frame {frameNo}: no Mesen shot at {theirs}");
                    else
                    {
                        int diffs = SmsPng.Compare(session.Frame, session.FrameWidth, session.FrameHeight, theirs, Path.Combine(outDir, $"{tag}_f{frameNo}_diff.png"), out string summary);
                        Console.WriteLine($"frame {frameNo}: {(diffs == 0 ? "IDENTICAL to Mesen" : summary)}");
                        if (diffs != 0) mesenDiffFrames++;
                    }
                }
            }
        }
        double secs = sw.Elapsed.TotalSeconds;
        foreach (var t in trace) Console.WriteLine(t);
        if (Opt("wav") is { } wav) { MixWav(wav, pcm.ToArray(), session.SampleRate); Console.WriteLine($"wrote {wav} ({pcm.Count / (double)session.SampleRate:F1} s)"); }
        if (Opt("dump-vram") is { } dv) { File.WriteAllBytes(dv, board.Vdp.Vram); }
        if (Opt("battery-out") is { } bo && session.HasBattery) { File.WriteAllBytes(bo, session.ExportSave()); Console.WriteLine($"battery saved: {bo}"); }
        var vdp = board.Vdp;
        Console.WriteLine($"frames={frames} {secs * 1000 / frames:F2} ms/frame ({frames / secs:F0} fps, {frames / secs / session.FramesPerSecond:F1}x real time) PC=${board.Cpu.PC:X4} " +
                          $"R0..R10={string.Join(" ", vdp.Regs.Take(11).Select(b => b.ToString("X2")))} height={vdp.FrameHeight} irq={vdp.Irq}");
        if (Flag("hash")) Console.WriteLine($"hash={hashLast:X16}");
        if (mesenDir != null) Console.WriteLine($"mesen compare: {mesenFramesCompared - mesenDiffFrames}/{mesenFramesCompared} frames identical");
        return mesenDir != null && mesenDiffFrames > 0 ? 1 : 0;
    }

    internal static ulong HashFrame(IConsoleSession s)
    {
        ulong h = 0xcbf29ce484222325;
        int n = s.FrameWidth * s.FrameHeight;
        var f = s.Frame;
        for (int i = 0; i < n; i++) { h ^= f[i]; h *= 0x100000001b3; }
        return h;
    }

    internal static void MixWav(string path, short[] mono, int rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + mono.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(mono.Length * 2);
        foreach (var s in mono) w.Write(s);
    }
}

/// <summary>ROM loading and input scripts shared by the Master System tools.</summary>
internal static class SmsRomIo
{
    /// <summary>The ROM bytes of a file: a .zip is searched for its first Master System / Game Gear / SG-1000 file; any other file is read as it is.</summary>
    public static byte[] Load(string path, out string innerName)
    {
        innerName = Path.GetFileName(path);
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(path);
        using var zip = ZipFile.OpenRead(path);
        string[] ext = { ".sms", ".gg", ".sg", ".sc" };
        var e = zip.Entries.FirstOrDefault(x => ext.Any(y => x.Name.EndsWith(y, StringComparison.OrdinalIgnoreCase))) ?? throw new InvalidDataException("no ROM inside " + path);
        innerName = e.Name;
        using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
    }

    /// <summary>"60:Right+A,90:" into a frame -> buttons map (an entry holds until the next).</summary>
    public static Dictionary<int, PadButtons> ParseInput(string spec)
    {
        var d = new Dictionary<int, PadButtons>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            PadButtons b = 0;
            if (kv.Length > 1) foreach (var name in kv[1].Split('+', StringSplitOptions.RemoveEmptyEntries)) b |= Enum.Parse<PadButtons>(name, true);
            d[int.Parse(kv[0])] = b;
        }
        return d;
    }
}

/// <summary>PNG output and the comparison with a Mesen screenshot.</summary>
internal static class SmsPng
{
    public static void Save(uint[] px, int w, int h, string path)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var row = new int[w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++) row[x] = (int)px[y * w + x];
            System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, w);
        }
        bmp.UnlockBits(data);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path, ImageFormat.Png);
    }

    public static uint[] Load(string path, out int w, out int h)
    {
        using var bmp = new Bitmap(path);
        w = bmp.Width; h = bmp.Height;
        var px = new uint[w * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var row = new int[w];
        for (int y = 0; y < h; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w);
            for (int x = 0; x < w; x++) px[y * w + x] = (uint)row[x] | 0xFF000000u;
        }
        bmp.UnlockBits(data);
        return px;
    }

    /// <summary>Compares a frame with a PNG. Returns the number of differing pixels (or -1 for a size mismatch) and describes them; writes a diff image (red where they differ, grey where they agree).</summary>
    public static int Compare(uint[] mine, int w, int h, string theirsPath, string diffPath, out string summary)
    {
        var theirs = Load(theirsPath, out int tw, out int th);
        if (tw != w || th != h) { summary = $"SIZE MISMATCH: mine {w}x{h}, Mesen {tw}x{th}"; return -1; }
        var diff = new uint[w * h];
        int count = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var firsts = new List<string>();
        for (int i = 0; i < w * h; i++)
        {
            if ((mine[i] | 0xFF000000u) == theirs[i]) { uint g = (mine[i] >> 16 & 0xFF) / 3 + 0x40; diff[i] = 0xFF000000u | g << 16 | g << 8 | g; continue; }
            diff[i] = 0xFFFF0000u;
            count++;
            int x = i % w, y = i / w;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            if (firsts.Count < 4) firsts.Add($"({x},{y}) mine {mine[i] & 0xFFFFFF:X6} mesen {theirs[i] & 0xFFFFFF:X6}");
        }
        summary = count == 0 ? "identical" : $"{count} pixels differ in x {minX}-{maxX}, y {minY}-{maxY}; e.g. {string.Join("; ", firsts)}";
        if (count > 0) Save(diff, w, h, diffPath);
        return count;
    }
}
