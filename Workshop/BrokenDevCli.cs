using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokenNes.Dev;
using NesEmulator;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// <c>--brokendev list|verify</c>: the BrokenDev module's command line (developers only; refuses to run unless BrokenDev is switched on, see
/// <see cref="BrokenDevGate"/>). The project manifests (<c>BrokenDev/projects/&lt;id&gt;/project.json</c>) hold a <c>verify</c> recipe; this command
/// runs a built ROM through that recipe on the real BrokenNes cores and prints what it measured and whether every expectation held.
///
///   --brokendev list   [--manifests DIR] [--home DIR]
///   --brokendev verify &lt;id&gt; [--manifests DIR] [--home DIR] [--rom PATH] [--only SCENARIO] [--png-dir DIR] [--out FILE]
///
/// Exit code: 0 every scenario passed | 1 a scenario failed or the game crashed | 2 usage / missing file | 3 BrokenDev is not enabled.
///
/// Everything is deterministic, so a hash recorded by one run can be compared with the next. A scenario is a fresh power-on:
/// <c>frames</c>, a scripted <c>input</c> ("60:Start,66:,..." as --romtest), <c>checkpoints</c> (frame numbers where the picture hash and the
/// number of colours are recorded) and <c>expect</c> (see <see cref="Evaluate"/>). The NES runs on the NES class with the cores named in
/// <c>verify.cores</c> (the app defaults, CPU/PPU/APU FIX, when absent); the SNES runs as the apps run it (ConsoleSessions, its default cores).
/// </summary>
internal static partial class BrokenDevCli
{
    private const string Usage =
        "Usage: --brokendev list   [--manifests DIR] [--home DIR]\n" +
        "       --brokendev verify <id> [--manifests DIR] [--home DIR] [--rom PATH] [--only SCENARIO] [--png-dir DIR] [--out FILE]\n" +
        "       --brokendev dump-chr --rom PATH --out FILE [--frames N] [--input \"f:Buttons,...\"]   (a NES game's CHR RAM after N frames)\n" +
        "       --brokendev run --spec FILE [--ram fceux|zeros|ones] [--cpu ID --ppu ID --apu ID] [--ntsc]   (a compat run: BrokenDev's emulator-neutral spec in, result.txt + pictures out)\n" +
        "BrokenDev is developer-only: set BROKENNES_DEV=1 (or brokenDevEnabled in config.json) first.";

    private const double NtscFps = 60.0988;

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }

        if (!BrokenDevGate.IsEnabled(out string why))
        {
            Console.Error.WriteLine($"BrokenDev is not enabled ({why}). It is a developer-only module: set BROKENNES_DEV=1 to use it.");
            return 3;
        }
        if (args.Length < 2) { Console.Error.WriteLine(Usage); return 2; }
        if (args[1].Equals("dump-chr", StringComparison.OrdinalIgnoreCase))
            return DumpChr(Opt("rom", ""), int.Parse(Opt("frames", "600"), CultureInfo.InvariantCulture), Opt("input", ""), Opt("out", ""));
        if (args[1].Equals("run", StringComparison.OrdinalIgnoreCase))
            return CompatRun(Opt("spec", ""), Opt("ram", "fceux"), Opt("cpu", ""), Opt("ppu", ""), Opt("apu", ""), args.Any(a => a.Equals("--ntsc", StringComparison.OrdinalIgnoreCase)));

        string? manifests = FindManifests(Opt("manifests", ""));
        if (manifests == null)
        {
            Console.Error.WriteLine("Cannot find BrokenDev/projects (pass --manifests, or set BROKENDEV_MANIFESTS, or run from inside the repository).");
            return 2;
        }
        string home = Opt("home", "") is { Length: > 0 } h ? h : Environment.GetEnvironmentVariable("BROKENDEV_HOME") is { Length: > 0 } eh ? eh : @"P:\BrokenDev\env";

        switch (args[1].ToLowerInvariant())
        {
            case "list": return List(manifests, home);
            case "verify":
                if (args.Length < 3 || args[2].StartsWith("--")) { Console.Error.WriteLine(Usage); return 2; }
                return Verify(args[2], manifests, home, Opt("rom", ""), Opt("only", ""), Opt("png-dir", ""), Opt("out", ""));
            default:
                Console.Error.WriteLine($"Unknown BrokenDev command '{args[1]}'.\n{Usage}");
                return 2;
        }
    }

    // ---- manifests -----------------------------------------------------------------------------

    internal static string? FindManifests(string given)
    {
        var candidates = new List<string>();
        if (given.Length > 0) candidates.Add(given);
        if (Environment.GetEnvironmentVariable("BROKENDEV_MANIFESTS") is { Length: > 0 } env) candidates.Add(env);
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                candidates.Add(Path.Combine(d.FullName, "BrokenDev", "projects"));
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static int List(string manifests, string home)
    {
        Console.WriteLine($"BrokenDev projects in {manifests}  (home {home})");
        foreach (string dir in Directory.GetDirectories(manifests).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string mp = Path.Combine(dir, "project.json");
            if (!File.Exists(mp)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(mp));
            var r = doc.RootElement;
            string id = r.GetProperty("id").GetString() ?? "?";
            string built = File.Exists(Path.Combine(home, "out", id, "build.json")) ? "built" : "not built";
            string verified = File.Exists(Path.Combine(home, "out", id, "verify.json")) ? "verified-before" : "";
            Console.WriteLine($"  {id,-22} {r.GetProperty("kind").GetString(),-7} {built,-10} {verified,-16} {r.GetProperty("title").GetString()}");
        }
        return 0;
    }

    // ---- dump-chr ------------------------------------------------------------------------------

    /// <summary>Runs a NES ROM on the app's default cores for N frames and writes its CHR RAM (all banks: 32 KB on mapper 30) to a file.
    /// The Nested enhancement tool wants exactly this: the tile art a game has uploaded, as a flat CHR image.</summary>
    private static int DumpChr(string romPath, int frames, string input, string outFile)
    {
        if (romPath.Length == 0 || outFile.Length == 0) { Console.Error.WriteLine(Usage); return 2; }
        if (!File.Exists(romPath)) { Console.Error.WriteLine($"ROM not found: {romPath}"); return 2; }
        using var empty = JsonDocument.Parse("{}");
        using var runner = new NesRunner(romPath, File.ReadAllBytes(romPath), empty.RootElement, false);
        var steps = ParseInput(input); var held = new List<string>(); int next = 0;
        for (int f = 0; f < frames; f++)
        {
            while (next < steps.Count && steps[next].Frame <= f) { held = steps[next].Names; next++; }
            runner.Step(held);
            if (runner.Crashed) { Console.Error.WriteLine($"the game crashed at frame {f + 1}: {runner.CrashInfo}"); return 1; }
        }
        byte[] chr = runner.DumpChr();
        if (chr.Length == 0) { Console.Error.WriteLine("the ROM has no CHR memory to dump"); return 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllBytes(outFile, chr);
        Console.WriteLine($"wrote {chr.Length} bytes of CHR after {frames} frames ({runner.Cores}); {chr.Count(b => b != 0)} non-zero; fnv {Fnv(chr)}");
        return 0;
    }

    // ---- run (BrokenDev compat) ------------------------------------------------------------------

    /// <summary>
    /// One run of BrokenDev's emulator-neutral compat contract (BrokenDev/lib/Compat.ps1 describes it): the spec names the ROM, the frames, the
    /// pad script, the bytes to record every frame and the frames to picture; this writes <c>result.txt</c> and <c>shot_NNNNN.png</c> into the
    /// spec's out folder, exactly as the Mesen, BizHawk, FCEUX and Snes9x adapters do, so BrokenNes is the reference column of the same table.
    /// The battery save (a NES mapper's flash, a SNES cartridge's SRAM) is read from and written back to the spec's savedir as brokennes.sav:
    /// a second run with the same savedir is a fresh boot that finds the game's save.
    /// Exit: 0 the run reached its last frame, 1 the game crashed, 2 a bad spec or a missing file.
    /// </summary>
    private static int CompatRun(string specPath, string ram, string cpu, string ppu, string apu, bool ntsc)
    {
        if (specPath.Length == 0 || !File.Exists(specPath)) { Console.Error.WriteLine($"spec not found: '{specPath}'\n{Usage}"); return 2; }
        string console = "", rom = "", outDir = "", saveDir = ""; int frames = 0;
        var reads = new List<(string Name, string Domain, int Addr)>();
        var steps = new List<InputStep>();
        var shots = new SortedSet<int>();
        int lineNo = 0;
        foreach (string raw in File.ReadAllLines(specPath))
        {
            lineNo++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sp = line.IndexOf(' ');
            string key = sp < 0 ? line : line[..sp], val = sp < 0 ? "" : line[(sp + 1)..].Trim();
            try
            {
                switch (key)
                {
                    case "console": console = val; break;
                    case "rom": rom = val; break;
                    case "out": outDir = val; break;
                    case "savedir": saveDir = val; break;
                    case "frames": frames = int.Parse(val, CultureInfo.InvariantCulture); break;
                    case "read":
                    {
                        var p = val.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        reads.Add((p[0], p[1], Convert.ToInt32(p[2], 16)));
                        break;
                    }
                    case "input":
                    {
                        var p = val.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var names = p.Length < 2 || p[1] == "-" ? new List<string>() : p[1].Split('+', StringSplitOptions.RemoveEmptyEntries).ToList();
                        steps.Add(new InputStep(int.Parse(p[0], CultureInfo.InvariantCulture), names));
                        break;
                    }
                    case "shot": shots.Add(int.Parse(val, CultureInfo.InvariantCulture)); break;
                    default: throw new FormatException($"unknown directive '{key}'");
                }
            }
            catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
            {
                Console.Error.WriteLine($"spec line {lineNo}: {ex.Message}: {raw}"); return 2;
            }
        }
        if (console is not ("nes" or "snes" or "wms") || rom.Length == 0 || outDir.Length == 0 || frames <= 0) { Console.Error.WriteLine("spec needs console nes|snes|wms, rom, out and frames > 0"); return 2; }
        if (!File.Exists(rom)) { Console.Error.WriteLine($"ROM not found: {rom}"); return 2; }
        foreach (var r in reads)
            if ((console == "nes" && r.Domain != "ram") || (console is "snes" or "wms" && r.Domain != "wram")) { Console.Error.WriteLine($"read {r.Name}: domain {r.Domain} does not belong to the {console}"); return 2; }
        steps = steps.OrderBy(s => s.Frame).ToList();
        Directory.CreateDirectory(outDir);
        string savePath = saveDir.Length > 0 ? Path.Combine(saveDir, "brokennes.sav") : "";

        var coresJson = new JsonObject();
        if (cpu.Length > 0) coresJson["cpu"] = cpu; if (ppu.Length > 0) coresJson["ppu"] = ppu; if (apu.Length > 0) coresJson["apu"] = apu;
        using var verifyDoc = JsonDocument.Parse(new JsonObject { ["cores"] = coresJson }.ToJsonString());
        byte[] romBytes = File.ReadAllBytes(rom);
        using var w = new StreamWriter(Path.Combine(outDir, "result.txt"), false, new System.Text.UTF8Encoding(false));
        w.WriteLine("# bdcompat-result 1");
        int done = 0;
        try
        {
            using IRunner runner = console == "nes" ? new NesRunner(rom, romBytes, verifyDoc.RootElement, ntsc, ram) : new SnesRunner(romBytes, Path.GetFileName(rom), verifyDoc.RootElement, console);
            w.WriteLine($"emu BrokenNes Workshop {runner.Cores}");
            w.WriteLine("reads " + string.Join(' ', reads.Select(r => r.Name)));
            w.WriteLine($"note {runner.Description}");
            w.WriteLine(console == "nes" ? $"note power-on RAM: {ram} (BrokenNes' own fill is the FCEUX pattern 00 00 00 00 FF FF FF FF)" : "note power-on work RAM: zeros");
            if (savePath.Length > 0 && File.Exists(savePath))
            {
                byte[] save = File.ReadAllBytes(savePath);
                w.WriteLine($"note battery: loaded {save.Length} bytes from {savePath}: {runner.ImportBattery(save)}");
            }
            else w.WriteLine("note battery: none loaded (a first boot)");
            var held = new List<string>(); int next = 0;
            var audio = new AudioStats();
            for (int f = 0; f < frames; f++)
            {
                while (next < steps.Count && steps[next].Frame <= f) { held = steps[next].Names; next++; }
                runner.Step(held);
                done = f + 1;
                if (runner.Crashed) { w.WriteLine($"end crashed {done} {runner.CrashInfo}"); w.Flush(); return 1; }
                Accumulate(audio, runner.DrainAudio(), true);
                w.WriteLine($"t {done}" + string.Concat(reads.Select(r => " " + runner.Peek(r.Domain, r.Addr).ToString(CultureInfo.InvariantCulture))));
                if (done % 30 == 0) { w.WriteLine($"audio {done} {(audio.NonSilentFrames > 0 ? 1 : 0)}"); audio = new AudioStats(); }
                if (shots.Contains(done)) { string file = $"shot_{done:D5}.png"; runner.SavePng(Path.Combine(outDir, file)); w.WriteLine($"shot {done} {file}"); }
                if (done % 60 == 0) w.Flush();
            }
            if (savePath.Length > 0)
            {
                byte[]? save = runner.ExportBattery();
                if (save is { Length: > 0 }) { Directory.CreateDirectory(saveDir); File.WriteAllBytes(savePath, save); w.WriteLine($"note battery: wrote {save.Length} bytes to {savePath}"); }
                else w.WriteLine("note battery: the game has nothing to save (no write to its save memory in this run)");
            }
            w.WriteLine($"end done {done}");
            return 0;
        }
        catch (Exception ex)
        {
            w.WriteLine($"end crashed {done} {ex.GetType().Name}: {ex.Message.Replace('\n', ' ')}");
            return 1;
        }
    }

    // ---- verify --------------------------------------------------------------------------------

    private sealed class Checkpoint { public int Frame; public string Hash = ""; public int Colors; public string Png = ""; }

    private sealed class AudioStats
    {
        public long Samples; public double Peak; public double SumSq; public int NonSilentFrames; public int AudioFrames; public ulong Hash = 14695981039346656037UL;
        public double Rms => Samples > 0 ? Math.Sqrt(SumSq / Samples) : 0;
    }

    private sealed class CheckResult { public string Id = ""; public bool Ok; public string Detail = ""; }

    private sealed partial class ScenarioResult
    {
        public string Id = "", Cores = "", Description = "", Result = "ok", CrashInfo = "";
        /// <summary>An information scenario is run and recorded but does not decide the verdict (the manifest says why, e.g. a known project bug it documents).</summary>
        public bool Info;
        public int FramesRun; public double Fps, Realtime; public int Colors; public int Width, Height;
        public List<Checkpoint> Checkpoints = new();
        public AudioStats Audio = new();
        public AudioStats AudioWindow = new();
        public List<CheckResult> Checks = new();
        public bool Ok => Result == "ok" && Checks.All(c => c.Ok);
    }

    private static int Verify(string id, string manifests, string home, string romOpt, string only, string pngDir, string outFile)
    {
        string mp = Path.Combine(manifests, id, "project.json");
        if (!File.Exists(mp)) { Console.Error.WriteLine($"No manifest for '{id}' ({mp})."); return 2; }
        using var doc = JsonDocument.Parse(File.ReadAllText(mp));
        var root = doc.RootElement;
        string console = root.GetProperty("console").GetString() ?? "nes";
        var verify = root.GetProperty("verify");

        string rom = romOpt;
        if (rom.Length == 0)
        {
            string published = "";
            foreach (var o in root.GetProperty("build").GetProperty("outputs").EnumerateArray())
                if (o.GetProperty("id").GetString() == "rom") published = o.GetProperty("publishAs").GetString() ?? "";
            rom = Path.Combine(home, "out", id, published);
        }
        if (!File.Exists(rom)) { Console.Error.WriteLine($"ROM not found: {rom} (build it first: brokendev.ps1 build {id})."); return 2; }
        byte[] romBytes = File.ReadAllBytes(rom);

        var results = new List<ScenarioResult>();
        foreach (var sc in verify.GetProperty("scenarios").EnumerateArray())
        {
            string sid = sc.GetProperty("id").GetString() ?? "?";
            if (only.Length > 0 && !sid.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
            results.Add(RunScenario(console, rom, romBytes, verify, sc, pngDir));
        }
        if (results.Count == 0) { Console.Error.WriteLine("No scenario ran (check --only)."); return 2; }

        // cross-scenario comparisons: "play@1800 differs from idle@1800" proves the input reached the game
        var cross = new List<CheckResult>();
        if (verify.TryGetProperty("compare", out var cmp) && only.Length == 0)
            foreach (var c in cmp.EnumerateArray())
                cross.Add(EvaluateCompare(c, results));

        bool ok = results.Where(r => !r.Info).All(r => r.Ok) && cross.All(c => c.Ok);
        var report = BuildReport(id, rom, romBytes, console, results, cross, ok);
        string text = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (outFile.Length > 0) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!); File.WriteAllText(outFile, text); }
        Console.WriteLine(text);
        Console.Error.WriteLine($"BROKENDEV VERIFY {id}: {(ok ? "PASS" : "FAIL")}");
        foreach (var r in results) foreach (var c in r.Checks.Where(c => !c.Ok)) Console.Error.WriteLine($"  [{r.Id}] FAIL {c.Id}: {c.Detail}");
        foreach (var c in cross.Where(c => !c.Ok)) Console.Error.WriteLine($"  [compare] FAIL {c.Id}: {c.Detail}");
        foreach (var r in results.Where(r => r.Result != "ok")) Console.Error.WriteLine($"  [{r.Id}] {r.Result}: {r.CrashInfo}");
        return ok ? 0 : 1;
    }

    private static ScenarioResult RunScenario(string console, string romPath, byte[] rom, JsonElement verify, JsonElement sc, string pngDir)
    {
        var res = new ScenarioResult { Id = sc.GetProperty("id").GetString() ?? "?", Info = sc.TryGetProperty("info", out var inf) && inf.ValueKind == JsonValueKind.True };
        int frames = sc.GetProperty("frames").GetInt32();
        string input = sc.TryGetProperty("input", out var ip) ? ip.GetString() ?? "" : "";
        var checkpoints = new SortedSet<int>();
        if (sc.TryGetProperty("checkpoints", out var cps)) foreach (var c in cps.EnumerateArray()) checkpoints.Add(c.GetInt32());
        checkpoints.Add(frames);
        bool ntsc = sc.TryGetProperty("ntscFrameTiming", out var nt) && nt.ValueKind == JsonValueKind.True;
        // powerOnRam: "fceux" (BrokenNes' own fill, the app default) | "zeros" | "ones". A NES program may not rely on power-on RAM (it is undefined on
        // hardware), but homebrew written against an emulator set to all-zeros often does without knowing it; this is the lever that finds out.
        string powerOnRam = sc.TryGetProperty("powerOnRam", out var por) ? por.GetString() ?? "fceux" : "fceux";
        JsonElement? expect = sc.TryGetProperty("expect", out var ex) ? ex : null;
        int audioFrom = 0;
        if (expect is { } e0 && e0.TryGetProperty("audio", out var a0) && a0.TryGetProperty("fromFrame", out var af)) audioFrom = af.GetInt32();

        var steps = ParseInput(input);
        if (pngDir.Length > 0) Directory.CreateDirectory(pngDir);

        try
        {
            using IRunner runner = console == "nes" ? new NesRunner(romPath, rom, verify, ntsc, powerOnRam) : new SnesRunner(rom, Path.GetFileName(romPath), verify, console);
            res.Cores = runner.Cores; res.Description = runner.Description;
            var held = new List<string>();
            int next = 0;
            // pokes: [{ "frame": 2, "addr": "0x07FC", "value": 0 }] writes work RAM before that frame runs (NES only): a forced state is a probe, never a proof
            var pokes = new List<(int Frame, int Addr, byte Value)>();
            if (sc.TryGetProperty("pokes", out var pk))
                foreach (var p in pk.EnumerateArray())
                {
                    string a = p.GetProperty("addr").ValueKind == JsonValueKind.String ? p.GetProperty("addr").GetString() ?? "0" : p.GetProperty("addr").GetInt32().ToString(CultureInfo.InvariantCulture);
                    int addr = a.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(a[2..], 16) : int.Parse(a, CultureInfo.InvariantCulture);
                    pokes.Add((p.GetProperty("frame").GetInt32(), addr, (byte)p.GetProperty("value").GetInt32()));
                }
            if (pokes.Count > 0 && runner is not NesRunner) throw new ArgumentException("pokes work on a NES scenario only");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
            {
                foreach (var pk2 in pokes) if (pk2.Frame == f && runner is NesRunner nr) nr.Poke(pk2.Addr, pk2.Value);
                while (next < steps.Count && steps[next].Frame <= f) { held = steps[next].Names; next++; }
                runner.Step(held);
                res.FramesRun = f + 1;
                if (runner.Crashed) { res.Result = "crashed"; res.CrashInfo = runner.CrashInfo; break; }
                var samples = runner.DrainAudio();
                Accumulate(res.Audio, samples, true);
                if (f + 1 > audioFrom) Accumulate(res.AudioWindow, samples, true);
                if (runner is SnesRunner { WideMessSession: { } wmsFrame }) WideMessFrame(res, wmsFrame, f + 1 > audioFrom);
                if (checkpoints.Contains(f + 1))
                {
                    var (hash, colors) = runner.Picture();
                    var cp = new Checkpoint { Frame = f + 1, Hash = hash.ToString("X16"), Colors = colors };
                    if (pngDir.Length > 0) { cp.Png = Path.Combine(pngDir, $"{res.Id}_f{f + 1:D5}.png"); runner.SavePng(cp.Png); }
                    res.Checkpoints.Add(cp);
                }
            }
            sw.Stop();
            res.Fps = res.FramesRun / Math.Max(sw.Elapsed.TotalSeconds, 1e-9);
            res.Realtime = res.Fps / NtscFps;
            res.Width = runner.Width; res.Height = runner.Height;
            res.Colors = res.Checkpoints.Count > 0 ? res.Checkpoints[^1].Colors : 0;
            if (runner is SnesRunner { WideMessSession: { } wmsEnd }) res.Wms = WideMessCli.Measure(wmsEnd);   // after the last picture: measuring redraws
        }
        catch (Exception ex2) when (ex2 is ArgumentException or NotSupportedException or InvalidDataException or Cartridge.UnsupportedMapperException)
        {
            res.Result = "refused"; res.CrashInfo = ex2.GetType().Name + ": " + ex2.Message;
        }
        catch (Exception ex3)
        {
            res.Result = "crashed"; res.CrashInfo = ex3.GetType().Name + ": " + ex3.Message;
        }

        if (res.Result == "ok") { Evaluate(res, expect); EvaluateWideMess(res, expect); }
        return res;
    }

    private static void Accumulate(AudioStats a, float[] s, bool countFrame)
    {
        if (s.Length == 0) { return; }
        double framePeak = 0;
        foreach (float v in s)
        {
            double av = Math.Abs(v);
            if (av > a.Peak) a.Peak = av;
            if (av > framePeak) framePeak = av;
            a.SumSq += (double)v * v;
            // quantise to 16 bit for the hash so the record does not depend on float formatting
            a.Hash = (a.Hash ^ (ushort)(short)Math.Clamp((int)Math.Round(v * 32767.0), -32768, 32767)) * 1099511628211UL;
        }
        a.Samples += s.Length;
        if (countFrame) { a.AudioFrames++; if (framePeak >= 0.002) a.NonSilentFrames++; }
    }

    // ---- expectations --------------------------------------------------------------------------

    /// <summary>
    /// expect keys (all optional; a crash always fails): minDistinctColors (at the last frame) | pictureDiffers [f1,f2] | pictureSame [f1,f2] (both frames must be checkpoints) |
    /// audio { fromFrame, minPeak (0..1), minNonSilentFrames, silent } | minRealtime (frames per second divided by 60.0988).
    /// </summary>
    private static void Evaluate(ScenarioResult r, JsonElement? expect)
    {
        void Add(string id, bool ok, string detail) => r.Checks.Add(new CheckResult { Id = id, Ok = ok, Detail = detail });
        Add("ran-all-frames", r.Result == "ok", $"{r.FramesRun} frames");
        if (expect is not { } e) return;
        Checkpoint? At(int f) => r.Checkpoints.FirstOrDefault(c => c.Frame == f);
        if (e.TryGetProperty("minDistinctColors", out var mc))
            Add("distinct-colors", r.Colors >= mc.GetInt32(), $"{r.Colors} colours at the last frame, need >= {mc.GetInt32()}");
        foreach (string key in new[] { "pictureDiffers", "pictureSame" })
        {
            if (!e.TryGetProperty(key, out var pair)) continue;
            int f1 = pair[0].GetInt32(), f2 = pair[1].GetInt32();
            var c1 = At(f1); var c2 = At(f2);
            if (c1 == null || c2 == null) { Add(key, false, $"frames {f1} and {f2} must both be checkpoints"); continue; }
            bool differ = c1.Hash != c2.Hash;
            Add($"{key}[{f1},{f2}]", key == "pictureDiffers" ? differ : !differ, $"hash@{f1}={c1.Hash} hash@{f2}={c2.Hash}");
        }
        if (e.TryGetProperty("audio", out var au))
        {
            var w = r.AudioWindow;
            if (au.TryGetProperty("silent", out var sil) && sil.ValueKind == JsonValueKind.True)
                Add("audio-silent", w.Peak < 0.002, $"peak {w.Peak:F4}");
            if (au.TryGetProperty("minPeak", out var mp))
                Add("audio-peak", w.Peak >= mp.GetDouble(), $"peak {w.Peak:F4} (window from frame {(au.TryGetProperty("fromFrame", out var ff) ? ff.GetInt32() : 0)}), need >= {mp.GetDouble():F4}");
            if (au.TryGetProperty("minNonSilentFrames", out var mn))
                Add("audio-nonsilent-frames", w.NonSilentFrames >= mn.GetInt32(), $"{w.NonSilentFrames} non-silent frames of {w.AudioFrames}, need >= {mn.GetInt32()}");
        }
        if (e.TryGetProperty("minRealtime", out var mr))
            Add("speed", r.Realtime >= mr.GetDouble(), $"{r.Fps:F0} fps = {r.Realtime:F1}x real time, need >= {mr.GetDouble():F1}x");
    }

    private static CheckResult EvaluateCompare(JsonElement c, List<ScenarioResult> results)
    {
        string a = c.GetProperty("a").GetString() ?? "", b = c.GetProperty("b").GetString() ?? "";
        string want = c.TryGetProperty("expect", out var w) ? w.GetString() ?? "differ" : "differ";
        (ScenarioResult? s, Checkpoint? cp) Find(string spec)
        {
            var parts = spec.Split('@');
            var s = results.FirstOrDefault(x => x.Id == parts[0]);
            int f = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : s?.FramesRun ?? 0;
            return (s, s?.Checkpoints.FirstOrDefault(x => x.Frame == f));
        }
        var (sa, ca) = Find(a); var (sb, cb) = Find(b);
        string id = $"{a} {want} {b}";
        if (ca == null || cb == null) return new CheckResult { Id = id, Ok = false, Detail = "one side has no such checkpoint (scenario missing, crashed or frame not listed)" };
        bool differ = ca.Hash != cb.Hash;
        return new CheckResult { Id = id, Ok = want == "differ" ? differ : !differ, Detail = $"{a}={ca.Hash} {b}={cb.Hash}" };
    }

    // ---- report --------------------------------------------------------------------------------

    private static JsonObject BuildReport(string id, string rom, byte[] romBytes, string console, List<ScenarioResult> results, List<CheckResult> cross, bool ok)
    {
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(romBytes)).ToLowerInvariant();
        var scen = new JsonArray();
        foreach (var r in results)
        {
            scen.Add(new JsonObject
            {
                ["id"] = r.Id, ["ok"] = r.Ok, ["info"] = r.Info, ["result"] = r.Result, ["crashInfo"] = r.CrashInfo,
                ["cores"] = r.Cores, ["description"] = r.Description,
                ["framesRun"] = r.FramesRun, ["width"] = r.Width, ["height"] = r.Height, ["distinctColors"] = r.Colors,
                ["fps"] = Math.Round(r.Fps, 1), ["realtimeFactor"] = Math.Round(r.Realtime, 2),
                ["checkpoints"] = new JsonArray(r.Checkpoints.Select(c => (JsonNode)new JsonObject { ["frame"] = c.Frame, ["hash"] = c.Hash, ["colors"] = c.Colors, ["png"] = c.Png }).ToArray()),
                ["audio"] = new JsonObject
                {
                    ["samples"] = r.Audio.Samples, ["peak"] = Math.Round(r.Audio.Peak, 4), ["rms"] = Math.Round(r.Audio.Rms, 5),
                    ["nonSilentFrames"] = r.Audio.NonSilentFrames, ["framesWithAudio"] = r.Audio.AudioFrames, ["hash"] = r.Audio.Hash.ToString("X16"),
                    ["windowPeak"] = Math.Round(r.AudioWindow.Peak, 4), ["windowNonSilentFrames"] = r.AudioWindow.NonSilentFrames,
                },
                ["checks"] = new JsonArray(r.Checks.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["ok"] = c.Ok, ["detail"] = c.Detail }).ToArray()),
                ["wms"] = WideMessJson(r),
            });
        }
        return new JsonObject
        {
            ["project"] = id, ["ok"] = ok, ["console"] = console,
            ["rom"] = rom, ["romBytes"] = romBytes.Length, ["romSha256"] = sha,
            ["verifiedAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["scenarios"] = scen,
            ["compare"] = new JsonArray(cross.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["ok"] = c.Ok, ["detail"] = c.Detail }).ToArray()),
        };
    }

    // ---- input ---------------------------------------------------------------------------------

    private readonly record struct InputStep(int Frame, List<string> Names);

    private static List<InputStep> ParseInput(string spec)
    {
        var steps = new List<InputStep>();
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            if (colon < 0 || !int.TryParse(part[..colon], out int frame) || frame < 0) throw new ArgumentException($"bad input step '{part}' (expected frame:Buttons)");
            var names = part[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            steps.Add(new InputStep(frame, names));
        }
        return steps.OrderBy(s => s.Frame).ToList();
    }

    // ---- runners -------------------------------------------------------------------------------

    private interface IRunner : IDisposable
    {
        string Cores { get; }
        string Description { get; }
        bool Crashed { get; }
        string CrashInfo { get; }
        int Width { get; }
        int Height { get; }
        void Step(List<string> held);
        float[] DrainAudio();
        (ulong hash, int colors) Picture();
        void SavePng(string path);
        /// <summary>One byte of the game's memory without side effects: domain "ram" (NES 2 KB system RAM) or "wram" (SNES 128 KB work RAM).</summary>
        int Peek(string domain, int addr);
        /// <summary>The battery save as the apps persist it (NES: the mapper's flash or other mapper-side memory; SNES: the cartridge SRAM), or null.</summary>
        byte[]? ExportBattery();
        /// <summary>Puts a battery save back before the first frame; returns what happened, in words.</summary>
        string ImportBattery(byte[] data);
    }

    private static string Fnv(byte[] d) { ulong h = 14695981039346656037UL; foreach (byte b in d) { h ^= b; h *= 1099511628211UL; } return h.ToString("X16"); }

    private sealed class NesRunner : IRunner
    {
        private readonly NES nes;
        private readonly APU_FIX? fix;
        private static readonly Dictionary<string, int> Index = new(StringComparer.OrdinalIgnoreCase)
        { ["A"] = 0, ["B"] = 1, ["Select"] = 2, ["Start"] = 3, ["Up"] = 4, ["Down"] = 5, ["Left"] = 6, ["Right"] = 7 };

        public string Cores { get; }
        public string Description => Cores;
        public bool Crashed => nes.IsCrashed();
        public string CrashInfo => nes.GetCrashInfo();
        public int Width => 256;
        public int Height => 240;

        public NesRunner(string romPath, byte[] rom, JsonElement verify, bool ntsc, string powerOnRam = "fceux")
        {
            // The app's defaults (CPU_FIX / PPU_FIX / APU_FIX, the accuracy-target family) when the recipe names no core. The bare NES class would
            // otherwise start on its own older defaults (SPD / FMC / FMC), which is not what a BrokenNes user runs.
            string? cpu = "FIX", ppu = "FIX", apu = "FIX";
            if (verify.TryGetProperty("cores", out var cores) && cores.ValueKind == JsonValueKind.Object)
            {
                if (cores.TryGetProperty("cpu", out var c) && c.GetString() is { Length: > 0 } cs) cpu = cs;
                if (cores.TryGetProperty("ppu", out var p) && p.GetString() is { Length: > 0 } ps) ppu = ps;
                if (cores.TryGetProperty("apu", out var a) && a.GetString() is { Length: > 0 } az) apu = az;
            }
            nes = new NES { RomName = Path.GetFileName(romPath), RomPath = Path.GetFullPath(romPath) };
            nes.LoadROM(rom);
            var speed = nes.GetSpeedConfig();
            if (speed != null) speed.NtscAccurateFrameRate = ntsc;
            // a refused or silently-replaced core is a failure, never a fallback (the same rule --romtest applies)
            var rc = new[] { RomTestCli.ApplyCore("CPU", cpu, nes.SetCpuCore, nes.GetCpuCoreId), RomTestCli.ApplyCore("PPU", ppu, nes.SetPpuCore, nes.GetPpuCoreId), RomTestCli.ApplyCore("APU", apu, nes.SetApuCore, nes.GetApuCoreId) };
            foreach (var x in rc) if (!x.Applied) throw new ArgumentException($"{x.Kind} core '{x.Requested}' did not apply (effective '{x.Effective}')");
            Cores = $"{rc[0].Effective}/{rc[1].Effective}/{rc[2].Effective}" + (ntsc ? " ntsc-frame-timing" : "");
            switch (powerOnRam.ToLowerInvariant())
            {
                case "fceux": break;
                case "zeros": for (int i = 0; i < 2048; i++) nes.PokeSystemRam(i, 0x00); Cores += " power-on-ram=zeros"; break;
                case "ones": for (int i = 0; i < 2048; i++) nes.PokeSystemRam(i, 0xFF); Cores += " power-on-ram=ones"; break;
                default: throw new ArgumentException($"powerOnRam '{powerOnRam}' is not fceux, zeros or ones");
            }
            fix = nes.GetApuStateProbe() as APU_FIX;
        }

        public void Step(List<string> held)
        {
            var h = new bool[8];
            foreach (string n in held) { if (!Index.TryGetValue(n, out int i)) throw new ArgumentException($"unknown NES button '{n}'"); h[i] = true; }
            nes.SetInputs(h, null);
            nes.RunFrame();
        }

        public float[] DrainAudio()
        {
            if (fix != null) { int q = fix.GetQueuedSampleCount(); return q > 0 ? fix.GetAudioSamples(q) : Array.Empty<float>(); }
            return nes.GetAudioBuffer();
        }

        public (ulong, int) Picture()
        {
            var fb = nes.GetFrameBuffer();
            ulong h = 14695981039346656037UL; foreach (byte b in fb) { h ^= b; h *= 1099511628211UL; }
            var seen = new HashSet<int>();
            for (int i = 0; i + 3 < fb.Length; i += 4) seen.Add((fb[i] << 16) | (fb[i + 1] << 8) | fb[i + 2]);
            return (h, seen.Count);
        }

        public void SavePng(string path)
        {
            var fb = nes.GetFrameBuffer();
            var argb = new uint[256 * 240];
            for (int i = 0; i < argb.Length; i++) argb[i] = 0xFF000000u | ((uint)fb[i * 4] << 16) | ((uint)fb[i * 4 + 1] << 8) | fb[i * 4 + 2];
            MixLab.MixLabCli.SaveArgb(argb, 256, 240, path);
        }

        public void Poke(int addr, byte value) => nes.PokeSystemRam(addr & 0x7FF, value);

        public int Peek(string domain, int addr) => domain == "ram" ? nes.PeekSystemRam(addr & 0x7FF) : throw new ArgumentException($"the NES has no memory domain '{domain}' here (ram)");

        public byte[]? ExportBattery() => nes.ExportMapperBatteryMemory();

        public string ImportBattery(byte[] data) => nes.HasMapperBatteryMemory ? nes.ImportMapperBatteryMemory(data).ToString() : "the cartridge has no mapper-side battery memory";

        public byte[] DumpChr()
        {
            int n = nes.GetChrSize(); var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = nes.PeekChr(i);
            return b;
        }

        public void Dispose() { }
    }

    private sealed class SnesRunner : IRunner
    {
        private readonly IConsoleSession s;
        private readonly short[] buf = new short[65536];
        public string Cores { get; }
        public string Description => s.Description;
        public bool Crashed => false;
        public string CrashInfo => "";
        public int Width => s.FrameWidth;
        public int Height => s.FrameHeight;

        /// <summary>The WideMess console's session (a "wms" project), for the devkit's checks; null on the SNES.</summary>
        public SnesSession? WideMessSession => s is SnesSession ss && ss.WideMess != null ? ss : null;

        public SnesRunner(byte[] rom, string fileName, JsonElement verify, string consoleKey = "snes")
        {
            string cpu = "", ppu = "", apu = "";
            if (verify.TryGetProperty("cores", out var cores) && cores.ValueKind == JsonValueKind.Object)
            {
                cpu = cores.TryGetProperty("cpu", out var c) ? c.GetString() ?? "" : "";
                ppu = cores.TryGetProperty("ppu", out var p) ? p.GetString() ?? "" : "";
                apu = cores.TryGetProperty("apu", out var a) ? a.GetString() ?? "" : "";
            }
            // "wms": a WideMess project (BrokenDev/widemess) runs on the WideMess console, with its own menu's defaults
            var console = consoleKey == "wms" ? ConsoleKind.WideMess : ConsoleKind.Snes;
            if (console == ConsoleKind.WideMess) Consoles.WideMessEnabled = true;
            cpu = cpu == "" ? CoreCatalog.Default(console, CoreSlot.Cpu) : cpu;
            ppu = ppu == "" ? CoreCatalog.Default(console, CoreSlot.Ppu) : ppu;
            apu = apu == "" ? CoreCatalog.Default(console, CoreSlot.Apu) : apu;
            foreach (var (slot, id) in new[] { (CoreSlot.Cpu, cpu), (CoreSlot.Ppu, ppu), (CoreSlot.Apu, apu) })
                if (!CoreCatalog.Options(console, slot).Any(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"the {(console == ConsoleKind.WideMess ? "WideMess" : "SNES")} {slot} menu does not offer {id}");
            Cores = $"{cpu}/{ppu}/{apu}";
            byte[] unwrapped = RomDetect.Unwrap(rom, fileName, out string inner);
            s = ConsoleSessions.Create(console, unwrapped, cpu, ppu, apu, null, inner);
        }

        public void Step(List<string> held)
        {
            PadButtons b = PadButtons.None;
            foreach (string n in held) b |= Enum.Parse<PadButtons>(n, true);
            s.SetPad(0, b);
            s.RunFrame();
        }

        public float[] DrainAudio()
        {
            var all = new List<float>();
            int n;
            while ((n = s.ReadSamples(buf)) > 0)
                for (int i = 0; i + 1 < n; i += 2) all.Add((buf[i] + buf[i + 1]) / 65536f);   // stereo -> mono, -1..1
            return all.ToArray();
        }

        public (ulong, int) Picture()
        {
            int len = s.FrameWidth * s.FrameHeight; var fr = s.Frame;
            ulong h = 14695981039346656037UL; var colours = new HashSet<uint>();
            for (int i = 0; i < len; i++) { h = (h ^ fr[i]) * 1099511628211UL; if (colours.Count <= 4096) colours.Add(fr[i] & 0xFFFFFF); }
            return (h, colours.Count);
        }

        public void SavePng(string path) => MixLab.MixLabCli.SaveArgb(s.Frame, s.FrameWidth, s.FrameHeight, path);

        public int Peek(string domain, int addr) => domain == "wram" && s is SnesSession ss ? ss.Board.Wram[addr & 0x1FFFF] : throw new ArgumentException($"the SNES has no memory domain '{domain}' here (wram)");

        public byte[]? ExportBattery() => s.HasBattery ? s.ExportSave() : null;

        public string ImportBattery(byte[] data)
        {
            if (!s.HasBattery) return "the cartridge has no battery";
            try { s.ImportSave(data); return "Applied"; }
            catch (Exception ex) { return "refused: " + ex.Message; }
        }

        public void Dispose() => s.Dispose();
    }
}
