using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--sega-bench</c>: the speed + determinism gate of the Sega consoles, the Sega counterpart of <c>--snesbench</c>. It builds each session the way the apps do (<c>ConsoleSessions.Create</c>,
/// so the picture-bridge wrapper and every bridge chip are in the path), plays a fixed scripted pad sequence, and measures only the time spent in <c>RunFrame</c> and in reading the audio.
/// Alongside, it hashes every frame's pixels and every audio sample: with <c>--golden</c> it records the hashes (first time or <c>--update</c>) or checks them, so an optimisation that
/// changes one pixel or one sample fails and names the first differing 250-frame checkpoint. It also counts the bytes allocated per frame (the standing requirement is zero).
/// <code>
///   --sega-bench --rom X [--console sms|gg|md] [--cpu id --ppu id --apu id] [--frames N] [--input "f:Btn,..."] [--repeat N] [--golden file.json] [--update] [--ab NAME] [--reference-paths]
///   --sega-bench --suite [--roms dir] [--only substring] [--repeat N] [--golden file.json] [--update] [--ab NAME] [--json out.json]
/// </code>
/// The suite runs the built-in case list (a few games per console, stock chips and each way of crossing to a NES chip); a missing ROM is skipped, never counted as a pass. <c>--ab NAME</c> alternates a
/// switch on and off between runs (even runs on) so both variants see the same machine; the hashes must come out identical either way. Exit: 0 ok, 1 golden mismatch, 2 usage, 5 unexpected.
/// </summary>
internal static class SegaBenchCli
{
    /// <summary>Performance experiments that exist as a toggle in the cores: <c>--ab NAME</c> flips them between runs. Settled experiments are removed once decided.</summary>
    internal static readonly Dictionary<string, Action<bool>> AbSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["md-loop"] = on => NesEmulator.Sega.MdSoundUnit.FastLoops = on,   // the Genesis Z80's polling loops counted instead of ticked vs every T-state ticked
        ["idle-frame"] = on => NesEmulator.Mix.NesPictureSink.IdleMode = on ? 2 : 0,   // the picture bridges' NES: one frame of PPU work from the vertical blank vs two full NES frames with an idle CPU
        ["idle-frame-1"] = on => NesEmulator.Mix.NesPictureSink.IdleMode = on ? 1 : 0,   // two frames of PPU work without the CPU vs two full NES frames
        ["vdp-run"] = on => NesEmulator.Sega.MdVdp.FastRun = on,   // MdVdp.RunTo returns at once for a stretch with nothing due vs every call takes the event loop
        ["vdp-draw"] = on => NesEmulator.Sega.MdVdp.FastDraw = on,   // the Genesis picture's line drawing (tile runs, priority ladder, colour conversion) in its short forms vs the original loops
    };

    private sealed record Case(string Name, string Console, string Rom, string Cpu = "", string Ppu = "", string Apu = "", int Frames = 900, string Note = "");

    // The roster: games chosen to load the cores differently (a plain platformer, heavy sprites and raster, a fast scroller, FM music), each on its stock chips and, for the bridges, on the NES chips.
    private static readonly Case[] Cases =
    {
        new("sms-alexkidd", "sms", @"sms\Alex Kidd in Miracle World (USA, Europe) (v1.1).sms"),
        new("sms-fantasyzone", "sms", @"sms\Fantasy Zone (World) (v1.2).sms"),
        new("sms-shinobi", "sms", @"sms\Shinobi (USA, Europe).sms"),
        new("sms-sonic", "sms", @"sms\Sonic The Hedgehog (USA, Europe).sms"),
        new("sms-alexkidd+fm", "sms", @"sms\Alex Kidd in Miracle World (USA, Europe) (v1.1).sms", Apu: "SMS-FM"),
        new("sms-alexkidd>fixs", "sms", @"sms\Alex Kidd in Miracle World (USA, Europe) (v1.1).sms", Ppu: "NES:FIXS", Note: "picture on PPU_FIXS"),
        new("sms-alexkidd>fix", "sms", @"sms\Alex Kidd in Miracle World (USA, Europe) (v1.1).sms", Ppu: "NES:FIX", Note: "picture on PPU_FIX"),
        new("sms-alexkidd>nesapu", "sms", @"sms\Alex Kidd in Miracle World (USA, Europe) (v1.1).sms", Apu: "NES:FIX", Note: "PSG on APU_FIX"),
        new("gg-columns", "gg", @"gg\Columns (U) [!].gg"),
        new("gg-sonic", "gg", @"gg\Sonic The Hedgehog (U) (V1.0) [!].gg"),
        new("gg-sonicchaos", "gg", @"gg\Sonic Chaos (U) [!].gg"),
        new("gg-shinobi2", "gg", @"gg\Shinobi II - The Silent Fury (U) [!].gg"),
        new("gg-sonic>fixs", "gg", @"gg\Sonic The Hedgehog (U) (V1.0) [!].gg", Ppu: "NES:FIXS", Note: "picture on PPU_FIXS"),
        new("md-sonic", "md", @"md\Sonic the Hedgehog (JUE) [!].bin"),
        new("md-sonic2", "md", @"md\Sonic the Hedgehog 2 (JUE) [!].bin"),
        new("md-streetsofrage", "md", @"md\Streets of Rage (JUE) (REV 00) [!].bin"),
        new("md-goldenaxe", "md", @"md\Golden Axe (JU) (REV 00) [!].bin"),
        new("md-gunstar", "md", @"md\Gunstar Heroes (E) [!].bin"),
        new("md-comixzone", "md", @"md\Comix Zone (1) [!].bin"),
        new("md-sonic>fixs", "md", @"md\Sonic the Hedgehog (JUE) [!].bin", Ppu: "NES:FIXS", Note: "picture on PPU_FIXS"),
        new("md-sonic>fix", "md", @"md\Sonic the Hedgehog (JUE) [!].bin", Ppu: "NES:FIX", Note: "picture on PPU_FIX"),
        new("md-sonic>nesapu", "md", @"md\Sonic the Hedgehog (JUE) [!].bin", Apu: "NES:FIX", Note: "sound on APU_FIX"),
    };

    private sealed class RunResult
    {
        public string Video = "", Audio = "";
        public List<string> Checkpoints = new();
        public double RunMs, AudioMs, CpuMs;          // totals over the frames
        public long AllocBytes;
        public int Frames, Width, Height, SampleRate;
        public string Description = "";
        public double Fps;                              // the console's real frame rate
        public int SizeChanges;
        public List<ulong> StateTrace = new();
        public ulong State = 14695981039346656037UL;    // a hash of the whole Genesis board's state (68000, VDP and its half-drawn line, sound unit, cartridge) taken every --state-every frames
    }

    /// <summary>With --state-every N the whole Genesis board's saved state is hashed every N frames, and the hashes of the runs must agree (the twin checks that a fast path leaves the machine in the same state at every moment it is looked at, not only in what it plays).</summary>
    private static int StateEvery;

    private static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    private static bool Flag(string[] a, string name) => Array.IndexOf(a, name) >= 0;

    /// <summary>The standard pad script: tap Start, tap a button, tap Start again (menus), then walk and jump about in a fixed pattern.</summary>
    internal static string PlayScript(int frames)
    {
        var steps = new List<string> { "90:Start", "96:", "150:A", "156:", "210:Start", "216:", "270:B", "276:" };
        string[] pat = { "Right", "Right+A", "Right", "Right+B", "Down", "Left", "Left+A", "Up", "Right+A+B", "Right" };
        int k = 0;
        for (int f = 330; f < frames; f += 25) steps.Add($"{f}:{pat[k++ % pat.Length]}");
        return string.Join(",", steps);
    }

    private static SortedDictionary<int, PadButtons> ParseInput(string spec)
    {
        var d = new SortedDictionary<int, PadButtons>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int c = part.IndexOf(':');
            if (c < 0 || !int.TryParse(part[..c], out int f)) continue;
            PadButtons b = 0;
            foreach (var n in part[(c + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) b |= Enum.Parse<PadButtons>(n, true);
            d[f] = b;
        }
        return d;
    }

    private static ulong Fnv(ulong h, ReadOnlySpan<uint> px) { foreach (uint v in px) { h ^= v; h *= 1099511628211UL; } return h; }
    private static ulong Fnv(ulong h, ReadOnlySpan<short> s) { foreach (short v in s) { h ^= (ushort)v; h *= 1099511628211UL; } return h; }

    private static RunResult RunOnce(ConsoleKind console, byte[] rom, string file, string cpu, string ppu, string apu, int frames, SortedDictionary<int, PadButtons> script)
    {
        cpu = CoreCatalog.Resolve(console, CoreSlot.Cpu, cpu); ppu = CoreCatalog.Resolve(console, CoreSlot.Ppu, ppu); apu = CoreCatalog.Resolve(console, CoreSlot.Apu, apu);
        using var s = ConsoleSessions.Create(console, rom, cpu, ppu, apu, fileName: file);
        var r = new RunResult { Frames = frames, Description = s.Description, Fps = s.FramesPerSecond, SampleRate = s.SampleRate };
        ulong video = 14695981039346656037UL, audio = 14695981039346656037UL;
        var buf = new short[32768];
        PadButtons held = 0;
        int lastW = -1, lastH = -1;
        long runTicks = 0, audioTicks = 0;
        // warm-up is not separate: a golden covers every frame from the first; the median of several repeats absorbs the JIT
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 1; f <= frames; f++)
        {
            if (script.TryGetValue(f, out var b)) held = b;
            s.SetPad(0, held);
            long t0 = Stopwatch.GetTimestamp();
            s.RunFrame();
            long t1 = Stopwatch.GetTimestamp();
            // the app reads in a loop until the unit has nothing more (a 32768-sample buffer); only the reads are timed, the hashing is not
            long readTicks = 0; int n;
            while (true)
            {
                long a0 = Stopwatch.GetTimestamp();
                n = s.ReadSamples(buf);
                readTicks += Stopwatch.GetTimestamp() - a0;
                if (n <= 0) break;
                audio = Fnv(audio, buf.AsSpan(0, n));
            }
            runTicks += t1 - t0; audioTicks += readTicks;
            int w = s.FrameWidth, h = s.FrameHeight;
            if (w != lastW || h != lastH) { if (lastW >= 0) r.SizeChanges++; lastW = w; lastH = h; }
            video = Fnv(video, s.Frame.AsSpan(0, w * h)); video = (video ^ (uint)(w << 16 | h)) * 1099511628211UL;
            if (StateEvery > 0 && f % StateEvery == 0 && (s as NesEmulator.Mix.SegaPictureSession)?.Inner is NesEmulator.Sega.MdSession dm)
            {
                // the whole Genesis board (68000, VDP with its half-drawn line and both picture buffers, sound unit, cartridge): a fast path must leave all of it as the reference path does
                using var ms = new MemoryStream(); using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) dm.Board.SaveState(bw);
                ulong sh = 14695981039346656037UL; foreach (byte b0 in ms.ToArray()) { sh ^= b0; sh *= 1099511628211UL; }
                r.StateTrace.Add(sh); r.State ^= sh; r.State *= 1099511628211UL;
            }
            if (f % 250 == 0) r.Checkpoints.Add($"{f}:{video:x16}:{audio:x16}");
        }
        r.AllocBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        r.CpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        r.RunMs = runTicks * 1000.0 / Stopwatch.Frequency; r.AudioMs = audioTicks * 1000.0 / Stopwatch.Frequency;
        r.Video = video.ToString("x16"); r.Audio = audio.ToString("x16"); r.Width = lastW; r.Height = lastH;
        return r;
    }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        try
        {
            int repeat = int.Parse(Opt(args, "--repeat") ?? "3");
            string? goldenPath = Opt(args, "--golden"), ab = Opt(args, "--ab"), only = Opt(args, "--only"), jsonOut = Opt(args, "--json");
            bool update = Flag(args, "--update");
            StateEvery = int.Parse(Opt(args, "--state-every") ?? "0");
            if (Flag(args, "--reference-paths")) foreach (var kv in AbSwitches) kv.Value(false);
            if (ab != null && !AbSwitches.ContainsKey(ab)) { Console.Error.WriteLine($"unknown --ab switch '{ab}' ({(AbSwitches.Count == 0 ? "none defined" : string.Join(", ", AbSwitches.Keys))})"); return 2; }

            var cases = new List<(Case c, byte[] rom)>();
            string? romsDirArg = Opt(args, "--roms") ?? Environment.GetEnvironmentVariable("BROKENNES_SEGA_ROMS");
            if (Flag(args, "--suite"))
            {
                // the games are never in the repository: a folder with sms\ gg\ md\ under it, from --roms or the BROKENNES_SEGA_ROMS variable
                if (romsDirArg == null) { Console.Error.WriteLine("--suite needs --roms <folder with sms\\ gg\\ md\\ in it> (or set BROKENNES_SEGA_ROMS)"); return 2; }
                string romsDir = romsDirArg;
                foreach (var c in Cases)
                {
                    if (only != null && !c.Name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                    string path = Path.Combine(romsDir, c.Rom);
                    if (!File.Exists(path)) { Console.WriteLine($"SKIP {c.Name}: no ROM at {path}"); continue; }
                    cases.Add((c, File.ReadAllBytes(path)));
                }
            }
            else
            {
                string? romPath = Opt(args, "--rom");
                if (romPath == null) { Console.Error.WriteLine("usage: --sega-bench (--rom <file> | --suite) [--console sms|gg|md] [--cpu --ppu --apu] [--frames N] [--input ...] [--repeat N] [--golden file] [--update] [--ab NAME] [--json out]"); return 2; }
                byte[] raw = RomDetect.Unwrap(File.ReadAllBytes(romPath), romPath, out string inner);
                var kind = Opt(args, "--console") is { Length: > 0 } ck ? Consoles.FromKey(ck) : RomDetect.Detect(raw, inner) ?? throw new InvalidDataException("unknown ROM type");
                cases.Add((new Case(Path.GetFileNameWithoutExtension(inner), Consoles.Key(kind), romPath, Opt(args, "--cpu") ?? "", Opt(args, "--ppu") ?? "", Opt(args, "--apu") ?? "", int.Parse(Opt(args, "--frames") ?? "900")), raw));
            }
            if (cases.Count == 0) { Console.WriteLine("no cases ran (no ROMs found)"); return 2; }

            JsonObject golden = goldenPath != null && File.Exists(goldenPath) ? JsonNode.Parse(File.ReadAllText(goldenPath))!.AsObject() : new JsonObject();
            var results = new JsonArray();
            int bad = 0, ran = 0;
            Console.WriteLine($"{"case",-24} {"size",-8} {"run ms",7} {"aud ms",7} {"fps",7} {"x real",7} {"B/frame",8}  note");
            foreach (var (c, rom) in cases)
            {
                var console = Consoles.FromKey(c.Console);
                string input = Opt(args, "--input") ?? PlayScript(c.Frames);
                var script = ParseInput(input);
                var runs = new List<RunResult>(); var abRuns = new Dictionary<bool, List<RunResult>> { [true] = new(), [false] = new() };
                RunResult? first = null; bool mismatch = false;
                // the JIT and the tiered compiler need a few frames: a discarded warm run first
                RunOnce(console, rom, Path.GetFileName(c.Rom), c.Cpu, c.Ppu, c.Apu, Math.Min(c.Frames, 300), script);
                for (int i = 0; i < repeat; i++)
                {
                    bool on = i % 2 == 0;
                    if (ab != null) AbSwitches[ab](on);
                    GC.Collect(); GC.WaitForPendingFinalizers();
                    var r = RunOnce(console, rom, Path.GetFileName(c.Rom), c.Cpu, c.Ppu, c.Apu, c.Frames, script);
                    runs.Add(r); abRuns[on].Add(r);
                    first ??= r;
                    if (r.StateTrace.Count > 0 && r.State != first.State) { int k = 0; while (k < r.StateTrace.Count && r.StateTrace[k] == first.StateTrace[k]) k++; Console.WriteLine($"  !! first differing state after frame {(k + 1) * StateEvery}"); }
                    if (r.Video != first.Video || r.Audio != first.Audio || r.State != first.State) { mismatch = true; Console.WriteLine($"  !! {c.Name}: run {i} hashes differ from run 0 (video {r.Video} vs {first.Video}, audio {r.Audio} vs {first.Audio}) {(ab != null ? $"[{ab}={(on ? "on" : "off")}]" : "")}"); }
                }
                if (ab != null) AbSwitches[ab](true);
                double Med(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a.Length == 0 ? 0 : a[a.Length / 2]; }
                double runMs = Med(runs.Select(r => r.RunMs / r.Frames)), audMs = Med(runs.Select(r => r.AudioMs / r.Frames));
                double fps = 1000.0 / (runMs + audMs);
                double perFrameAlloc = Med(runs.Select(r => (double)r.AllocBytes / r.Frames));
                var f0 = runs[0];
                Console.WriteLine($"{c.Name,-24} {f0.Width + "x" + f0.Height,-8} {runMs,7:F3} {audMs,7:F3} {fps,7:F0} {fps / f0.Fps,6:F1}x {perFrameAlloc,8:F0}  {c.Note}");
                if (ab != null)
                {
                    double on = Med(abRuns[true].Select(r => r.CpuMs / r.Frames)), off = Med(abRuns[false].Select(r => r.CpuMs / r.Frames));
                    Console.WriteLine($"  [{ab}] process CPU per frame: on {on:F3} ms, off {off:F3} ms ({(off > 0 ? (off / on - 1) * 100 : 0):+0.0;-0.0}% for on)");
                }
                if (mismatch) bad++;
                // golden: record or compare (video, audio, every 250-frame checkpoint)
                if (goldenPath != null)
                {
                    string key = c.Name + (Opt(args, "--input") != null ? "|" + input : "");
                    var node = new JsonObject { ["frames"] = c.Frames, ["video"] = f0.Video, ["audio"] = f0.Audio, ["checkpoints"] = new JsonArray(f0.Checkpoints.Select(x => (JsonNode)x).ToArray()), ["description"] = f0.Description };
                    if (update || !golden.ContainsKey(key)) { golden[key] = node; Console.WriteLine($"  golden recorded for {key}"); }
                    else
                    {
                        var g = golden[key]!.AsObject();
                        var gc = g["checkpoints"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
                        string? firstBad = null;
                        for (int i = 0; i < Math.Min(gc.Count, f0.Checkpoints.Count) && firstBad == null; i++) if (gc[i] != f0.Checkpoints[i]) firstBad = f0.Checkpoints[i].Split(':')[0];
                        bool same = g["video"]!.GetValue<string>() == f0.Video && g["audio"]!.GetValue<string>() == f0.Audio;
                        Console.WriteLine($"  golden: {(same ? "IDENTICAL" : "DIFFERENT")}{(same ? "" : $" (first differing checkpoint: frame {firstBad ?? "?"}; video {(g["video"]!.GetValue<string>() == f0.Video ? "same" : "differs")}, audio {(g["audio"]!.GetValue<string>() == f0.Audio ? "same" : "differs")})")}");
                        if (!same) bad++;
                    }
                }
                ran++;
                results.Add(new JsonObject { ["case"] = c.Name, ["size"] = f0.Width + "x" + f0.Height, ["runMsPerFrame"] = Math.Round(runMs, 4), ["audioMsPerFrame"] = Math.Round(audMs, 4), ["fps"] = Math.Round(fps, 1), ["xRealTime"] = Math.Round(fps / f0.Fps, 2), ["allocBytesPerFrame"] = Math.Round(perFrameAlloc, 1), ["video"] = f0.Video, ["audio"] = f0.Audio });
            }
            if (goldenPath != null && (update || golden.Count > 0)) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(goldenPath))!); File.WriteAllText(goldenPath, golden.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
            if (jsonOut != null) File.WriteAllText(jsonOut, results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(bad == 0 ? $"{ran} case(s) ran, no hash mismatch" : $"{ran} case(s) ran, {bad} with a hash mismatch");
            return bad == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine("sega-bench failed: " + ex); return 5; }
    }
}
