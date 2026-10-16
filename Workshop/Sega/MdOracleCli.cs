using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-oracle &lt;rom&gt; [--line 232] [--f0 N | --scan shot.png [--scan-to N] [--pick F]] [--script "0:Right,30:Right+B,90:"] [--steps N] [--shots 10,20,30] [--theirs dir] [--out dir] [--dy 9] [--lags N] [--jobs 4] [--region ntsc|pal]</c>:
/// runs a game the way BlastEm is stepped by hand with its pause (F7) and frame advance (F8) keys, so a scripted play-through can be compared with the oracle frame by frame.
/// <para>BlastEm stops a frame at about scanline 232 (measured with a pad-polling probe ROM: a key pressed while it is paused is first seen by the game at V counter 232 and, running freely, events are taken at
/// 232-233 every frame). This command stops at that line too: it runs boundary after boundary (<see cref="MdSession.RunToLine"/>), changing the pad at the boundary exactly where the oracle's key press falls.
/// <b>Protocol:</b> BlastEm runs from reset without input until it is paused at some boundary F0 (found here with <c>--scan</c>: the boundary whose picture matches BlastEm's first screenshot, or given with
/// <c>--f0</c>); from there both get the same buttons at the same boundaries (<c>--script</c>: "step:buttons", buttons joined by + or empty for none, the pad changes before the advance of that step) and
/// are compared at the steps listed in <c>--shots</c> (the picture after that many advances): with <c>--theirs dir</c> holding BlastEm's screenshots s000.png, s001.png ... in the same order, each pair is diffed
/// (their rows are offset by <c>--dy</c>, 9 for a 224-line picture); with <c>--out dir</c> our pictures are written. A shot that differs is searched in our last <c>--lags</c> pictures: an exact match at another
/// step means the oracle stood at another frame (a frame-advance key tap that was lost), not that the picture differs. Exit code 0 when every compared pair is pixel-identical.</para>
/// <para>When the first screenshot matches several boundaries (a still or repeating picture) the scan reports it as ambiguous; with the later screenshots the candidates are told apart by replaying the script from
/// each of them (<c>--jobs</c> in parallel) and keeping the one whose pictures match. <c>--windows N [--min 40]</c> lists, for a game run without input, where the picture is different on every frame (a good place to
/// pause). <c>--emit-script default --offset 90 --steps N</c> prints the sweep's pad script as a <c>--script</c>.</para>
/// </summary>
internal static class MdOracleCli
{
    private static SortedDictionary<int, PadButtons> ParseScript(string? sc)
    {
        var script = new SortedDictionary<int, PadButtons>();
        if (sc == null) return script;
        foreach (var part in sc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            if (colon < 0 || !int.TryParse(part[..colon], out int step)) continue;
            PadButtons b = PadButtons.None;
            foreach (var name in part[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Enum.TryParse<PadButtons>(name, true, out var one)) b |= one;
            script[step] = b;
        }
        return script;
    }

    /// <summary>--ram-at 100,200 --ram-dir d: at those steps write our work RAM (r00100.bin), the Z80's RAM (z00100.bin) and VRAM (v00100.bin), for comparing with the oracle's saved states (--md-ramcmp).</summary>
    /// <summary>The scanline the replay stops at between two steps (where the pad changes): 232 as measured on BlastEm; <c>--line N</c> to calibrate.</summary>
    private static int ReplayLine = 232;
    private static HashSet<int> RamAt = new();
    private static string? RamDir;

    /// <summary>
    /// The row of the oracle's screenshot that our row 0 sits on. <c>--dy N</c> fixes it; otherwise it follows the two picture heights: BlastEm centres the picture in its frame and its screenshot starts one row before
    /// the centre, so a 224-line picture is at row 9 of a 240-row screenshot (NTSC), at row 17 of a 256-row one (PAL, 224 lines) and a 240-line PAL picture at row 9 of 256. (A wrong fixed value looked like a rendering
    /// difference on PAL games: Gunstar Heroes, SoR3.)
    /// </summary>
    private static int DyFor(int dyArg, int oursH, int theirsH) => dyArg >= 0 ? dyArg : Math.Max(0, (theirsH - oursH) / 2 + 1);

    private static MdSession Boot(byte[] data, RegionChoice? region, int line, int boundaries)
    {
        var session = new MdSession(data, "M68K", "MD", "MD", region);
        for (int f = 0; f < boundaries; f++) { session.SetPad(0, PadButtons.None); session.RunToLine(line); }
        return session;
    }

    /// <summary>One shot of a replay: how many pixels differ from the oracle's screenshot at the expected step, and where our history says it stood.</summary>
    private sealed record ShotResult(int Step, long Bad, int ClosestStep, long ClosestBad);

    /// <summary>
    /// Plays the script from the session's boundary and compares the pictures at the listed steps with the oracle's screenshots. <paramref name="stopAtFirstBad"/> ends the replay at the first shot that differs
    /// (used to try candidate start frames cheaply). <paramref name="lagRange"/> keeps a history of our pictures to say where a differing shot would have matched.
    /// </summary>
    private static List<ShotResult> Replay(MdSession session, SortedDictionary<int, PadButtons> script, int steps, List<int> shots, List<(int w, int h, uint[] px)> theirs, int dy, int lagRange, bool stopAtFirstBad, string? outDir, Action<string>? log)
    {
        var results = new List<ShotResult>();
        PadButtons pad = PadButtons.None;
        var history = new List<(uint[] px, int w, int h)>();
        for (int s = 0; s < steps; s++)
        {
            if (script.TryGetValue(s, out var nb)) pad = nb;
            session.SetPad(0, pad);
            session.RunToLine(ReplayLine);
            int n = s + 1;
            if (lagRange > 0) { var copy = new uint[session.FrameWidth * session.FrameHeight]; Array.Copy(session.Frame, copy, copy.Length); history.Add((copy, session.FrameWidth, session.FrameHeight)); }
            if (outDir != null && RamDir != null && RamAt.Contains(n))       // only the replay that is kept writes (the candidate searches pass no output folder)
            {
                var b = session.Board;
                File.WriteAllBytes(Path.Combine(RamDir, $"r{n:D5}.bin"), b.Ram);
                var z = b.Sound as MdSoundUnit ?? (b.Sound as MdSoundOnNes)?.Inner;
                if (z != null) File.WriteAllBytes(Path.Combine(RamDir, $"z{n:D5}.bin"), z.Ram);
                File.WriteAllBytes(Path.Combine(RamDir, $"v{n:D5}.bin"), b.Vdp.Vram);
            }
            int idx = shots.IndexOf(n);
            if (idx < 0) continue;
            if (outDir != null) WritePng(session.Frame, session.FrameWidth, session.FrameHeight, Path.Combine(outDir, $"o{n:D5}.png"));
            if (idx >= theirs.Count) { log?.Invoke($"after {n,5} advances: picture hash {Hash(session.Frame, session.FrameWidth * session.FrameHeight):X16}"); continue; }
            var th = theirs[idx];
            long bad = MdFindCli.Compare(session.Frame, session.FrameWidth, session.FrameHeight, th.px, th.w, th.h, 0, DyFor(dy, session.FrameHeight, th.h), 0, long.MaxValue);
            int bestN = n; long bestBad = bad;
            if (bad != 0 && lagRange > 0)
            {
                bestBad = long.MaxValue; bestN = -1;
                for (int k = Math.Max(0, history.Count - 1 - lagRange); k < history.Count; k++)
                {
                    long b2 = MdFindCli.Compare(history[k].px, history[k].w, history[k].h, th.px, th.w, th.h, 0, DyFor(dy, history[k].h, th.h), 0, bestBad);
                    if (b2 < bestBad) { bestBad = b2; bestN = k + 1; }
                }
            }
            results.Add(new ShotResult(n, bad, bestN, bestBad));
            if (log != null)
            {
                string note = bad != 0 && lagRange > 0 ? $"  | closest of our last {Math.Min(history.Count, lagRange + 1)} pictures: after {bestN} advances ({bestBad:N0} differing){(bestN != n && bestBad == 0 ? $", i.e. the oracle stands {n - bestN} advance(s) {(bestN < n ? "behind" : "ahead")} (a lost frame-advance tap)" : "")}" : "";
                log($"after {n,5} advances: {bad,8:N0} differing pixels   [ours {session.FrameWidth}x{session.FrameHeight}, pc {session.Board.Cpu.Pc - 4:X6}]{note}");
            }
            if (stopAtFirstBad && bad != 0) break;
        }
        return results;
    }

    public static int Run(string[] args)
    {
        if (M68kTestCli.Arg(args, "--slot-set") is { } slotSet) { MdRunCli.ApplySlotSet(slotSet); Console.WriteLine($"   (experiment settings: {slotSet})"); }
        // --emit-script default [--offset 90] [--steps 1500]: print the sweep's pad script (see MdSweepCli) as "step:buttons" changes, step 0 being frame <offset> of it, for --script and for oraclerun.ps1
        if (M68kTestCli.Arg(args, "--emit-script") is { } emitName)
        {
            int off = int.TryParse(M68kTestCli.Arg(args, "--offset"), out int o) ? o : 90, n = int.TryParse(M68kTestCli.Arg(args, "--steps"), out int ns) ? ns : 1500;
            var parts = new List<string>(); PadButtons last = unchecked((PadButtons)(-1));
            for (int s = 0; s < n; s++)
            {
                var b = MdSweepCli.PadAt(emitName.ToLowerInvariant(), off + s);
                if (b != last) { parts.Add($"{s}:{string.Join("+", Enum.GetValues<PadButtons>().Where(x => x != PadButtons.None && (b & x) == x && System.Numerics.BitOperations.PopCount((uint)x) == 1).Select(x => x.ToString()))}"); last = b; }
            }
            Console.WriteLine(string.Join(",", parts));
            return 0;
        }
        // --ramcmp ours.bin theirs.state [--at 10941] [--len 10000] [--name work-ram]: compare a block of memory of ours (the --ram-dir files) with the same block inside a BlastEm quick-save state (hexadecimal offset: the
        // 68000's work RAM is a plain big-endian byte stream at $10941), as ranges of differing bytes with their values on both sides
        if (M68kTestCli.Arg(args, "--ramcmp") is { } oursPath)
        {
            string? theirsPath = args.SkipWhile(a => a != "--ramcmp").Skip(2).FirstOrDefault(a => !a.StartsWith("--"));
            if (theirsPath == null) { Console.WriteLine("usage: --ramcmp ours.bin theirs.state [--at 10941] [--len 10000]"); return 2; }
            int at = Convert.ToInt32(M68kTestCli.Arg(args, "--at") ?? "10941", 16), len = Convert.ToInt32(M68kTestCli.Arg(args, "--len") ?? "10000", 16);
            byte[] ours = File.ReadAllBytes(oursPath), stBytes = File.ReadAllBytes(theirsPath);
            if (at + len > stBytes.Length || len > ours.Length) { Console.WriteLine($"sizes: ours {ours.Length}, state {stBytes.Length}, wanted {len:X} at {at:X}"); return 2; }
            long baseAddr = Convert.ToInt64(M68kTestCli.Arg(args, "--base") ?? "FF0000", 16);
            var diffs = new List<(int a, int b, int n)>();
            int start = -1, last = -1, count = 0;
            for (int i = 0; i < len; i++)
            {
                if (ours[i] == stBytes[at + i]) continue;
                if (start >= 0 && i - last <= 3) { last = i; count++; continue; }
                if (start >= 0) diffs.Add((start, last, count));
                start = i; last = i; count = 1;
            }
            if (start >= 0) diffs.Add((start, last, count));
            int total = diffs.Sum(d => d.n);
            Console.WriteLine($"{Path.GetFileName(oursPath)} against {Path.GetFileName(theirsPath)}: {total:N0} differing bytes in {diffs.Count:N0} ranges of {len:N0}");
            foreach (var (a, b, n) in diffs.Take(60))
                Console.WriteLine($"   ${baseAddr + a:X6}-${baseAddr + b:X6} ({n} bytes): ours {BitConverter.ToString(ours, a, Math.Min(12, b - a + 1))}  theirs {BitConverter.ToString(stBytes, at + a, Math.Min(12, b - a + 1))}");
            return total == 0 ? 0 : 1;
        }
        string? rom = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        if (rom == null) { Console.WriteLine("usage: --md-oracle <rom> [--line 232] [--f0 N | --scan shot.png [--scan-to N] [--pick F]] [--script \"0:Right,30:\"] [--steps N] [--shots 10,20] [--theirs dir] [--out dir] [--dy 9] [--lags N] [--jobs N] [--windows N [--min 40]] [--region ntsc|pal]"); return 2; }
        int line = int.TryParse(M68kTestCli.Arg(args, "--line"), out int l) ? l : 232;
        ReplayLine = line;
        if (line != 232) Console.WriteLine($"(note: boundaries, the scan and the replay all stop at line {line}; BlastEm's own stop is about 232, see the record)");
        int dy = int.TryParse(M68kTestCli.Arg(args, "--dy"), out int d) ? d : -1;
        string? theirsDir = M68kTestCli.Arg(args, "--theirs"), outDir = M68kTestCli.Arg(args, "--out");
        if (outDir != null) Directory.CreateDirectory(outDir);
        RegionChoice? region = M68kTestCli.Arg(args, "--region")?.ToLowerInvariant() switch { "ntsc" => RegionChoice.Ntsc, "pal" => RegionChoice.Pal, _ => null };
        var script = ParseScript(M68kTestCli.Arg(args, "--script"));
        var shots = (M68kTestCli.Arg(args, "--shots") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
        int steps = int.TryParse(M68kTestCli.Arg(args, "--steps"), out int st) ? st : (shots.Count > 0 ? shots.Max() : 0);
        int lagRange = int.TryParse(M68kTestCli.Arg(args, "--lags"), out int lg) ? lg : 400;
        int jobs = int.TryParse(M68kTestCli.Arg(args, "--jobs"), out int jb) ? Math.Clamp(jb, 1, 6) : 4;
        byte[] data = File.ReadAllBytes(rom);
        RamDir = M68kTestCli.Arg(args, "--ram-dir"); if (RamDir != null) Directory.CreateDirectory(RamDir);
        RamAt = new HashSet<int>((M68kTestCli.Arg(args, "--ram-at") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse));

        // --windows N [--min 40]: with no input, the stretches of the first N boundaries whose pictures are all different from every other boundary's (including each other's): a pause inside one can be pinned to its
        // exact frame by --scan (a still or repeating picture cannot)
        if (int.TryParse(M68kTestCli.Arg(args, "--windows"), out int winN))
        {
            int min = int.TryParse(M68kTestCli.Arg(args, "--min"), out int mn) ? mn : 40;
            var session0 = new MdSession(data, "M68K", "MD", "MD", region);
            var hashes = new ulong[winN + 1];
            for (int f = 0; f <= winN; f++) { session0.SetPad(0, PadButtons.None); session0.RunToLine(line); hashes[f] = Hash(session0.Frame, session0.FrameWidth * session0.FrameHeight) ^ (ulong)session0.FrameWidth; }
            var counts = hashes.GroupBy(h => h).ToDictionary(g => g.Key, g => g.Count());
            var runs = new List<string>(); int start = -1;
            for (int f = 0; f <= winN + 1; f++)
            {
                bool unique = f <= winN && counts[hashes[f]] == 1;
                if (unique && start < 0) start = f;
                if (!unique && start >= 0) { if (f - start >= min) runs.Add($"{start}-{f - 1}"); start = -1; }
            }
            Console.WriteLine($"windows of at least {min} boundaries with pictures seen nowhere else (a pause at frame f lands about f = 60 x seconds since launch): {(runs.Count == 0 ? "none" : string.Join(", ", runs))}");
            return 0;
        }

        var theirsFiles = theirsDir != null ? Directory.GetFiles(theirsDir, "s*.png").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
        var theirs = theirsFiles.Select(MdFindCli.Load).ToList();

        // where the script starts: the boundary the oracle was paused at
        List<int> candidates;
        if (M68kTestCli.Arg(args, "--scan") is { } scanPng)
        {
            var target = MdFindCli.Load(scanPng);
            int scanTo = int.TryParse(M68kTestCli.Arg(args, "--scan-to"), out int sto) ? sto : 900;
            var session0 = new MdSession(data, "M68K", "MD", "MD", region);
            long best = long.MaxValue; int bestF = -1; var exact = new List<int>();
            // --scan-state pause.state: the oracle's state saved at the pause point (the same moment as the screenshot): among the boundaries whose picture matches, the one whose work RAM is closest to it is the start
            byte[]? pauseRam = null;
            if (M68kTestCli.Arg(args, "--scan-state") is { } pausePath && File.Exists(pausePath))
            {
                byte[] pauseState = File.ReadAllBytes(pausePath);
                if (pauseState.Length >= 0x10941 + 0x10000) { pauseRam = new byte[0x10000]; Array.Copy(pauseState, 0x10941, pauseRam, 0, 0x10000); }
            }
            var ramDiff = new Dictionary<int, int>();
            var ramAll = new Dictionary<int, int>();
            bool ramPick = false;
            for (int f = 0; f <= scanTo; f++)
            {
                session0.SetPad(0, PadButtons.None);
                session0.RunToLine(line);
                long bad = MdFindCli.Compare(session0.Frame, session0.FrameWidth, session0.FrameHeight, target.px, target.w, target.h, 0, DyFor(dy, session0.FrameHeight, target.h), 0, best == long.MaxValue ? best : Math.Max(best, 1));
                if (pauseRam != null)
                {
                    var ram = session0.Board.Ram; int n = 0; for (int i = 0; i < pauseRam.Length; i++) if (ram[i] != pauseRam[i]) n++;
                    ramAll[f] = n;
                    if (bad == 0) ramDiff[f] = n;
                }
                if (bad == 0) exact.Add(f);
                if (bad < best) { best = bad; bestF = f; }
            }
            if (pauseRam != null && ramDiff.Count == 0 && ramAll.Count > 0)
            {
                // no boundary shows the pause picture exactly (a fade, a raster effect that differs): the work RAM alone decides, and says so
                var byRamAll = ramAll.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
                Console.WriteLine($"scan: no boundary shows the pause picture exactly; work RAM against the oracle's state, nearest boundaries by differing bytes: {string.Join(", ", byRamAll.Take(6).Select(kv => $"{kv.Key} ({kv.Value:N0})"))}");
                bestF = byRamAll[0].Key; ramPick = true; Console.WriteLine($"scan: the start is boundary {bestF} (the fewest RAM bytes differ; the picture does not match, so this is a guess to be judged by the shots that follow)");
            }
            if (pauseRam != null && ramDiff.Count > 0)
            {
                var byRam = ramDiff.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
                Console.WriteLine($"scan: work RAM against the oracle's state at the pause point, picture-identical boundaries by differing bytes: {string.Join(", ", byRam.Take(6).Select(kv => $"{kv.Key} ({kv.Value:N0})"))}");
                // a start whose RAM is clearly the closest (the next one has at least twice the differing bytes, or exactly zero differ) is taken without replaying
                if (byRam.Count == 1 || byRam[0].Value == 0 || byRam[1].Value >= Math.Max(8, byRam[0].Value * 2)) { exact = new List<int> { byRam[0].Key }; bestF = byRam[0].Key; Console.WriteLine($"scan: the start is boundary {bestF} (RAM)"); }
                else exact = byRam.Take(8).Select(kv => kv.Key).OrderBy(x => x).ToList();
            }
            if (!ramPick) Console.WriteLine($"scan: the screenshot matches the picture at boundary {bestF} best ({best:N0} differing pixels; boundary n = after n+1 runs to line {line}, no input)");
            if (exact.Count > 1)
            {
                Console.WriteLine($"scan: AMBIGUOUS, {exact.Count} boundaries match exactly ({string.Join(",", exact.Take(12))}{(exact.Count > 12 ? ",..." : "")}): the picture is the same for several frames");
                candidates = M68kTestCli.Arg(args, "--pick") is { } pk ? new List<int> { int.Parse(pk) } : exact;
            }
            else candidates = new List<int> { bestF };
            if (bestF < 0) return 1;
        }
        else candidates = new List<int> { int.TryParse(M68kTestCli.Arg(args, "--f0"), out int f0v) ? f0v : 0 };

        int chosen = candidates[0];
        if (candidates.Count > 1)
        {
            // tell the candidates apart by replaying the script from each (first shot that differs ends a candidate): needs the later screenshots
            if (theirs.Count == 0 || shots.Count == 0 || steps == 0) { Console.WriteLine("ambiguous start and nothing to tell the candidates apart with: give --script, --shots and --theirs, or --pick"); return 3; }
            Console.WriteLine($"replaying the script from each of the {candidates.Count} candidates ({jobs} at a time) ...");
            // in batches of --jobs, in boundary order; the search ends at the first batch with a candidate that matches every shot (77 equal candidates of a still title need not all be replayed: the first exact one is as good as any)
            var scoredList = new List<(int f, int ok, long bad)>();
            for (int off = 0; off < candidates.Count; off += jobs)
            {
                var batch = candidates.Skip(off).Take(jobs).ToArray();
                var part = new (int f, int ok, long bad)[batch.Length];
                Parallel.For(0, batch.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, ci =>
                {
                    var s = Boot(data, region, line, batch[ci] + 1);
                    var rs = Replay(s, script, steps, shots, theirs, dy, 0, true, null, null);
                    part[ci] = (batch[ci], rs.Count(r => r.Bad == 0), rs.Sum(r => r.Bad));
                });
                scoredList.AddRange(part);
                if (part.Any(x => x.bad == 0 && x.ok == shots.Count)) break;
            }
            var scored = scoredList.ToArray();
            if (scored.Length < candidates.Count) Console.WriteLine($"   (stopped after {scored.Length} of the {candidates.Count} candidates: one matches every shot)");
            var order = scored.OrderByDescending(x => x.ok).ThenBy(x => x.bad).ToList();
            foreach (var x in order.Take(6)) Console.WriteLine($"   start {x.f,5}: {x.ok} of {shots.Count(sh => true)} shot(s) exact before the first difference, {x.bad:N0} differing pixels there");
            chosen = order[0].f;
            if (order.Count > 1 && order[0].ok == order[1].ok && order[0].bad == order[1].bad) Console.WriteLine("   (the best two candidates are not told apart by these shots)");
        }

        Console.WriteLine($"f0 = {chosen}: the oracle is paused here, the script starts");
        var session = Boot(data, region, line, chosen + 1);
        // --ram-watch FFE11B[,FF0000-FF00FF,...] [--watch-limit N]: print each 68000 write to these work RAM addresses during the replay (the script's steps counted from the start boundary: step = frame - f0 - 1), with the writing instruction's address and the value: for tracing the first differing byte of a --ramcmp back to the code that writes it
        if (M68kTestCli.Arg(args, "--ram-watch") is { } watchSpec)
        {
            var wranges = watchSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => { int dd = s.IndexOf('-'); uint a = Convert.ToUInt32(dd > 0 ? s[..dd] : s, 16) & 0xFFFF, b = dd > 0 ? Convert.ToUInt32(s[(dd + 1)..], 16) & 0xFFFF : a; return (a, b); }).ToArray();
            int wprinted = 0, wlimit = int.TryParse(M68kTestCli.Arg(args, "--watch-limit"), out int wl) ? wl : 200;
            var wboard = session.Board; long wstart = wboard.Frames;
            wboard.RamWriteHook = (off, upper, lower, val) =>
            {
                uint lo = (uint)off + (upper ? 0u : 1u), hi = (uint)off + (lower ? 1u : 0u);
                foreach (var (a, b) in wranges)
                    if (lo <= b && hi >= a)
                    {
                        if (wprinted++ < wlimit) Console.WriteLine($"   ram-watch step {wboard.Frames - wstart + (wboard.Vdp.Line > ReplayLine ? 1 : 0)} l{wboard.Vdp.Line} pc {wboard.Cpu.Pc - 4:X6}: ${0xFF0000 + off:X6}{(upper && lower ? "" : upper ? " (high byte)" : " (low byte)")} = {(upper && lower ? val.ToString("X4") : upper ? (val >> 8).ToString("X2") : (val & 0xFF).ToString("X2"))}");
                        break;
                    }
            };
        }
        // --trace out.txt --trace-steps A,B [--trace-pc]: the bus writes (and the VDP's own events, and with --trace-pc every instruction's address) of the replay's steps A to B-1, each with the step, line and (instruction trace only) the pc
        StreamWriter? replayTrace = null;
        if (M68kTestCli.Arg(args, "--trace") is { } tracePath)
        {
            var tb = (M68kTestCli.Arg(args, "--trace-steps") ?? "0,1000000").Split(',', StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
            replayTrace = new StreamWriter(tracePath, false, new System.Text.UTF8Encoding(false));
            var board = session.Board; long tstart = board.Frames;
            // the replay's step counter (n of "after n advances"): step n ends at the boundary line of frame start+n, so lines past the boundary line belong to the next step
            bool On() { long st = board.Frames - tstart + (board.Vdp.Line > ReplayLine ? 1 : 0); return st >= tb[0] && st < tb[1]; }
            string Tag() => $"s{board.Frames - tstart + (board.Vdp.Line > ReplayLine ? 1 : 0)} l{board.Vdp.Line}";
            board.WriteTrace = s => { if (On()) replayTrace.WriteLine($"{Tag()} {s}"); };
            board.Vdp.Trace = s => { if (On()) replayTrace.WriteLine($"{Tag()}   vdp {s}"); };
            if (M68kTestCli.Flag(args, "--trace-pc")) board.InstructionHook = pc => { if (On()) replayTrace.WriteLine($"{Tag()} pc {pc:X6}"); };
        }
        var results = Replay(session, script, steps, shots, theirs, dy, lagRange, false, outDir, Console.WriteLine);
        replayTrace?.Dispose();
        if (M68kTestCli.Arg(args, "--ram-out") is { } ramOut) { File.WriteAllBytes(ramOut, session.Board.Ram); Console.WriteLine($"wrote the work RAM to {ramOut}"); }
        // --save-out file: our battery image (SRAM or EEPROM) as the session would save it, after the replay: for comparing with the oracle's save file (written when it exits)
        if (M68kTestCli.Arg(args, "--save-out") is { } saveOut) { var img = session.ExportSave(); File.WriteAllBytes(saveOut, img); Console.WriteLine($"wrote the battery image ({img.Length} bytes, {img.Count(x => x != 0 && x != 0xFF)} bytes neither 00 nor FF) to {saveOut}"); }
        long totalBad = results.Sum(r => r.Bad);
        if (results.Count > 0) Console.WriteLine($"compared {results.Count} picture(s): {results.Count(r => r.Bad == 0)} exact, {totalBad:N0} differing pixels in all");
        return results.Count > 0 && totalBad != 0 ? 1 : 0;
    }

    private static ulong Hash(uint[] px, int n) { ulong h = 14695981039346656037UL; for (int i = 0; i < n; i++) { h ^= px[i]; h *= 1099511628211UL; } return h; }

    internal static void WritePng(uint[] pixels, int width, int height, string path)
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
