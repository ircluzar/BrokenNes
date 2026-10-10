using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-oracle &lt;rom&gt; [--line 232] [--f0 N | --scan shot.png [--scan-to N]] [--script "0:Right,30:Right+B,90:"] [--steps N] [--shots 10,20,30] [--theirs dir] [--out dir] [--dy 9] [--shot-offset 0|1] [--region ntsc|pal]</c>:
/// runs a game the way BlastEm is stepped by hand with its pause (F7) and frame advance (F8) keys, so a scripted play-through can be compared with the oracle frame by frame.
/// <para>BlastEm stops a frame at about scanline 232 (measured with a pad-polling probe ROM: a key pressed while it is paused is first seen by the game at V counter 232 and, running freely, events are taken at
/// 232-233 every frame). This command stops at that line too: it runs boundary after boundary (<see cref="MdSession.RunToLine"/>), changing the pad at the boundary exactly where the oracle's key press falls.
/// <b>Protocol:</b> BlastEm runs from reset without input until it is paused at some boundary F0 (found here with <c>--scan</c>: the boundary whose picture matches BlastEm's screenshot best, or given with
/// <c>--f0</c>); from there both get the same buttons at the same boundaries (<c>--script</c>: "step:buttons", buttons joined by + or empty for none, the pad changes before the advance of that step) and
/// are compared at the steps listed in <c>--shots</c> (the picture after that many advances): with <c>--theirs dir</c> holding BlastEm's screenshots s000.png, s001.png ... in the same order, each pair is diffed
/// (their rows are offset by <c>--dy</c>, 9 for a 224-line picture); with <c>--out dir</c> our pictures are written. Exit code 0 when every compared pair is pixel-identical.</para>
/// </summary>
internal static class MdOracleCli
{
    public static int Run(string[] args)
    {
        string? rom = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        if (rom == null) { Console.WriteLine("usage: --md-oracle <rom> [--line 232] [--f0 N | --scan shot.png [--scan-to N]] [--script \"0:Right,30:\"] [--steps N] [--shots 10,20] [--theirs dir] [--out dir] [--dy 9] [--shot-offset 0|1] [--region ntsc|pal]"); return 2; }
        int line = int.TryParse(M68kTestCli.Arg(args, "--line"), out int l) ? l : 232;
        int dy = int.TryParse(M68kTestCli.Arg(args, "--dy"), out int d) ? d : 9;
        int shotOffset = int.TryParse(M68kTestCli.Arg(args, "--shot-offset"), out int so) ? so : 0;
        string? theirsDir = M68kTestCli.Arg(args, "--theirs"), outDir = M68kTestCli.Arg(args, "--out");
        if (outDir != null) Directory.CreateDirectory(outDir);
        RegionChoice? region = M68kTestCli.Arg(args, "--region")?.ToLowerInvariant() switch { "ntsc" => RegionChoice.Ntsc, "pal" => RegionChoice.Pal, _ => null };
        var script = new SortedDictionary<int, PadButtons>();
        if (M68kTestCli.Arg(args, "--script") is { } sc)
            foreach (var part in sc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colon = part.IndexOf(':');
                if (colon < 0 || !int.TryParse(part[..colon], out int step)) continue;
                PadButtons b = PadButtons.None;
                foreach (var name in part[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Enum.TryParse<PadButtons>(name, true, out var one)) b |= one;
                script[step] = b;
            }
        var shots = (M68kTestCli.Arg(args, "--shots") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
        int steps = int.TryParse(M68kTestCli.Arg(args, "--steps"), out int st) ? st : (shots.Count > 0 ? shots.Max() : 0);

        byte[] data = File.ReadAllBytes(rom);
        var session = new MdSession(data, "M68K", "MD", "MD", region);

        int f0;
        if (M68kTestCli.Arg(args, "--scan") is { } scanPng)
        {
            var target = MdFindCli.Load(scanPng);
            int scanTo = int.TryParse(M68kTestCli.Arg(args, "--scan-to"), out int sto) ? sto : 900;
            long best = long.MaxValue; int bestF = -1; var exact = new List<int>();
            for (int f = 0; f <= scanTo; f++)
            {
                session.SetPad(0, PadButtons.None);
                session.RunToLine(line);
                long bad = MdFindCli.Compare(session.Frame, session.FrameWidth, session.FrameHeight, target.px, target.w, target.h, 0, dy, 0, bad0(best));
                if (bad == 0) exact.Add(f);
                if (bad < best) { best = bad; bestF = f; }
            }
            Console.WriteLine($"scan: the screenshot matches the picture at boundary {bestF} best ({best:N0} differing pixels; boundary n = after n+1 runs to line {line}, no input)");
            if (exact.Count > 1) { Console.WriteLine($"scan: AMBIGUOUS, {exact.Count} boundaries match exactly ({string.Join(",", exact.Take(12))}{(exact.Count > 12 ? ",..." : "")}): the picture is the same for several frames; pause at a moving picture, or pass --f0"); if (M68kTestCli.Arg(args, "--pick") is { } pk) bestF = int.Parse(pk); }
            if (bestF < 0) return 1;
            f0 = bestF + shotOffset;
            // rewind: rebuild the session and run to f0
            session = new MdSession(data, "M68k".ToUpperInvariant(), "MD", "MD", region);
            for (int f = 0; f <= bestF; f++) { session.SetPad(0, PadButtons.None); session.RunToLine(line); }
            Console.WriteLine($"f0 = {bestF}: paused here, the script starts");
        }
        else
        {
            f0 = int.TryParse(M68kTestCli.Arg(args, "--f0"), out int f0v) ? f0v : 0;
            for (int f = 0; f <= f0; f++) { session.SetPad(0, PadButtons.None); session.RunToLine(line); }
        }

        PadButtons pad = PadButtons.None;
        long totalBad = 0; int compared = 0;
        var theirsFiles = theirsDir != null ? Directory.GetFiles(theirsDir, "s*.png").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
        // a history of our pictures (one per boundary since f0): when a shot differs from ours at its expected step, the closest picture of the history says whether the oracle simply stood at another frame
        // (a frame advance that was lost makes it lag: its key tap landed while the previous frame was still running) rather than drew a different picture
        int lagRange = int.TryParse(M68kTestCli.Arg(args, "--lags"), out int lg) ? lg : 400;
        var history = new List<(uint[] px, int w, int h)>();
        for (int s = 0; s < steps; s++)
        {
            if (script.TryGetValue(s, out var nb)) pad = nb;
            session.SetPad(0, pad);
            session.RunToLine(line);
            int n = s + 1;       // advances done
            int shotIdx = shots.IndexOf(n);
            if (lagRange > 0) { var copy = new uint[session.FrameWidth * session.FrameHeight]; Array.Copy(session.Frame, copy, copy.Length); history.Add((copy, session.FrameWidth, session.FrameHeight)); }
            if (shotIdx < 0) continue;
            if (outDir != null) WritePng(session.Frame, session.FrameWidth, session.FrameHeight, Path.Combine(outDir, $"o{n:D5}.png"));
            if (shotIdx < theirsFiles.Count)
            {
                var th = MdFindCli.Load(theirsFiles[shotIdx]);
                long bad = MdFindCli.Compare(session.Frame, session.FrameWidth, session.FrameHeight, th.px, th.w, th.h, 0, dy, 0, long.MaxValue);
                string lagNote = "";
                if (bad != 0 && lagRange > 0)
                {
                    long bestBad = long.MaxValue; int bestN = -1;
                    for (int k = Math.Max(0, history.Count - 1 - lagRange); k < history.Count; k++)
                    {
                        long b2 = MdFindCli.Compare(history[k].px, history[k].w, history[k].h, th.px, th.w, th.h, 0, dy, 0, bestBad);
                        if (b2 < bestBad) { bestBad = b2; bestN = k + 1; }
                    }
                    lagNote = $"  | closest of our last {Math.Min(history.Count, lagRange + 1)} pictures: after {bestN} advances ({bestBad:N0} differing){(bestN != n ? $", i.e. the oracle stands {n - bestN} advance(s) {(bestN < n ? "behind" : "ahead")}" : "")}";
                }
                Console.WriteLine($"after {n,5} advances: {bad,8:N0} differing pixels vs {Path.GetFileName(theirsFiles[shotIdx])}   [ours {session.FrameWidth}x{session.FrameHeight}, pc {session.Board.Cpu.Pc - 4:X6}]{lagNote}");
                totalBad += bad; compared++;
            }
            else Console.WriteLine($"after {n,5} advances: picture hash {Hash(session.Frame, session.FrameWidth * session.FrameHeight):X16}");
        }
        if (M68kTestCli.Arg(args, "--ram-out") is { } ramOut) { File.WriteAllBytes(ramOut, session.Board.Ram); Console.WriteLine($"wrote the work RAM to {ramOut}"); }
        if (compared > 0) Console.WriteLine($"compared {compared} picture(s): {totalBad:N0} differing pixels in all");
        return compared > 0 && totalBad != 0 ? 1 : 0;
    }

    private static long bad0(long best) => best == long.MaxValue ? long.MaxValue : Math.Max(best, 1);      // keep counting past an exact match so a repeated picture is noticed

    private static ulong Hash(uint[] px, int n) { ulong h = 14695981039346656037UL; for (int i = 0; i < n; i++) { h ^= px[i]; h *= 1099511628211UL; } return h; }

    private static void WritePng(uint[] pixels, int width, int height, string path)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var ints = new int[width];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) ints[x] = (int)pixels[y * width + x];
                System.Runtime.InteropServices.Marshal.Copy(ints, 0, data.Scan0 + y * data.Stride, width);
            }
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(path, ImageFormat.Png);
    }
}
