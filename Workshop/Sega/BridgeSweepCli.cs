using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BrokenNes.Workshop.MixLab;
using NesEmulator;
using NesEmulator.Mix;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--bridge-sweep</c>: the classification of every game x core combination of the picture bridges (and, for completeness, the sound bridges): refused / crashed / hung / dead / frozen / static / alive, with the measured
/// fidelity for the alive ones. Each combination runs in its own child process (a core that loops forever or kills the process cannot take the sweep with it), a few at a time.
/// <code>
///   --bridge-sweep --manifest games.json --out dir [--frames 400] [--jobs 3] [--timeout 300] [--sound] [--only nes,sms,...] [--pictures "sms:NES:FIXS+NES:DMG"]
/// </code>
/// manifest: <c>{ "games": [ { "id": "smb1", "console": "nes", "rom": "C:\\x\\smb1.nes", "input": "100:Start,106:" } ] }</c>. Every game is also run on its console's own cores (the control) so "frozen" and "dead" mean the bridge
/// failed to show what the game itself shows, not that the game was idle. Classes:
/// <list type="bullet">
/// <item><b>refused</b>: the combination was not built (a core id that did not apply, a chip that cannot front the bridge) - said so, never replaced by another core.</item>
/// <item><b>crashed</b>: an exception or the NES core's own crash flag.</item>
/// <item><b>hung</b>: no result inside the time limit (the child is killed).</item>
/// <item><b>dead</b>: the picture is one colour (the control's is not), or, for a sound combination, no sound (the control's is not silent).</item>
/// <item><b>frozen</b>: the picture never changes (the control's does).</item>
/// <item><b>static</b>: the picture never changes and the control's does not either (the game idled: nothing to judge).</item>
/// <item><b>alive</b>: the picture changes; <b>lossy</b> when it is alive but its structural similarity to the source chip's own picture averages under 0.55. Sound: alive when it makes sound where the control does.</item>
/// </list>
/// <c>--bridge-sweep-one</c> is the child (one combination, one line of JSON).
/// </summary>
internal static class BridgeSweepCli
{
    public sealed class Game { public string Id { get; set; } = ""; public string Console { get; set; } = ""; public string Rom { get; set; } = ""; public string Input { get; set; } = ""; }

    public sealed class Measure
    {
        public bool Refused { get; set; }
        public bool Crashed { get; set; }
        public bool Timeout { get; set; }
        public string Message { get; set; } = "";
        public int Frames { get; set; }
        public double Fps { get; set; }
        public int Colours { get; set; }
        public int Changes { get; set; }
        /// <summary>Changes of the picture in the second half of the run (a core that stops drawing part-way is frozen even though the picture changed before).</summary>
        public int ChangesLate { get; set; }
        public int Samples { get; set; }
        public int FidSamples { get; set; }
        public double Ssim { get; set; }
        public double SsimMin { get; set; }
        public double BlockError { get; set; }
        public double ColourError { get; set; }
        public int Recognisable { get; set; }
        public double AudioRms { get; set; }
        public long AudioSamples { get; set; }
        public int AudioChanges { get; set; }
        public Dictionary<string, long> Counters { get; set; } = new();
        public string Description { get; set; } = "";
    }

    public sealed class Cell
    {
        public string Game { get; set; } = "";
        public string Console { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Combo { get; set; } = "";
        public string Class { get; set; } = "";
        public Measure M { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private const double SilentRms = 0.0004;

    // ------------------------------------------------------------------------------------------------------------------ the combinations
    internal static string[] DefaultPictures(string console) => console switch
    {
        "nes" => new[] { "SMS", "MD", "DMG", "DMGS", "SNES" },
        "gb" or "gbc" or "snes" => new[] { "NES:FIXS", "NES:FIX", "NES:SMS", "NES:MD" },
        "sms" or "gg" => new[] { "NES:FIXS", "NES:FIX", "NES:DMG", "NES:DMGS", "NES:SNES", "NES:MD" },
        "md" => new[] { "NES:FIXS", "NES:FIX", "NES:DMG", "NES:DMGS", "NES:SNES", "NES:SMS" },
        _ => Array.Empty<string>(),
    };

    internal static string[] DefaultSounds(string console) => console switch
    {
        "nes" => new[] { "SMS", "MD" },
        "sms" or "gg" or "md" or "gb" or "gbc" or "snes" => new[] { "NES:FIX" },
        _ => Array.Empty<string>(),
    };

    // ------------------------------------------------------------------------------------------------------------------ the classifier (pure: tested with synthetic measurements)
    internal static string Classify(Measure m, Measure? control, bool sound)
    {
        if (m.Refused) return "refused";
        if (m.Crashed) return "crashed";
        if (m.Timeout) return "hung";
        if (sound)
        {
            bool controlSound = control == null || control.AudioRms >= SilentRms;
            return m.AudioRms < SilentRms ? (controlSound ? "dead" : "static") : "alive";
        }
        if (m.Colours <= 1) return control == null || control.Colours > 1 ? "dead" : "static";
        if (m.Changes == 0) return control != null && control.Changes == 0 ? "static" : "frozen";
        if (m.ChangesLate == 0 && control != null && control.ChangesLate > 0) return "frozen";      // it stopped part-way while the game went on
        if (m.FidSamples > 0 && m.Ssim < 0.55) return "lossy";
        return "alive";
    }

    // ------------------------------------------------------------------------------------------------------------------ the sweep (parent)
    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string outDir = Opt("out", "bridge-sweep"); Directory.CreateDirectory(outDir);
        int frames = int.Parse(Opt("frames", "400")), jobs = int.Parse(Opt("jobs", "3")), timeout = int.Parse(Opt("timeout", "300"));
        bool sound = args.Contains("--sound", StringComparer.OrdinalIgnoreCase), pictures = !args.Contains("--no-pictures", StringComparer.OrdinalIgnoreCase);
        var only = Opt("only", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var games = JsonSerializer.Deserialize<ManifestFile>(File.ReadAllText(Opt("manifest", "games.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Games
            .Where(g => only.Count == 0 || only.Contains(g.Console)).ToList();
        var overrides = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in Opt("pictures", "").Split(';', StringSplitOptions.RemoveEmptyEntries)) { var kv = o.Split(':', 2); overrides[kv[0]] = kv[1].Split('+', StringSplitOptions.RemoveEmptyEntries); }
        string exe = Environment.ProcessPath!;

        // the jobs: control first, then every picture and sound combination
        var work = new List<(Game g, string kind, string combo)>();
        foreach (var g in games)
        {
            work.Add((g, "control", ""));
            if (pictures) foreach (var p in overrides.TryGetValue(g.Console, out var ov) ? ov : DefaultPictures(g.Console)) work.Add((g, "picture", p));
            if (sound) foreach (var s in DefaultSounds(g.Console)) work.Add((g, "sound", s));
        }
        Console.WriteLine($"bridge sweep: {games.Count} games, {work.Count} runs of {frames} frames, {jobs} at a time, {timeout} s each");
        var cells = new ConcurrentBag<Cell>();
        var sem = new SemaphoreSlim(jobs); var tasks = new List<Task>(); int done = 0;
        foreach (var (g, kind, combo) in work)
        {
            sem.Wait();
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    var m = RunChild(exe, g, kind, combo, frames, timeout);
                    cells.Add(new Cell { Game = g.Id, Console = g.Console, Kind = kind, Combo = combo, M = m });
                    int n = Interlocked.Increment(ref done);
                    Console.WriteLine($"  [{n}/{work.Count}] {g.Id} {kind} {combo}: {(m.Refused ? "refused" : m.Crashed ? "crashed" : m.Timeout ? "hung" : $"{m.Frames} frames, {m.Colours} colours, {m.Changes} changes, ssim {m.Ssim:F2}, audio {m.AudioRms:F4}")}");
                }
                finally { sem.Release(); }
            }));
        }
        Task.WaitAll(tasks.ToArray());

        // classify against each game's control and write the matrix
        var all = cells.OrderBy(c => c.Console).ThenBy(c => c.Game).ThenBy(c => c.Kind).ThenBy(c => c.Combo).ToList();
        foreach (var c in all)
        {
            var control = all.FirstOrDefault(x => x.Game == c.Game && x.Kind == "control")?.M;
            c.Class = c.Kind == "control" ? (c.M.Refused ? "refused" : c.M.Crashed ? "crashed" : c.M.Timeout ? "hung" : "control") : Classify(c.M, control, c.Kind == "sound");
        }
        File.WriteAllText(Path.Combine(outDir, "sweep.json"), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outDir, "sweep.csv"), Csv(all));
        string md = Markdown(all, frames);
        File.WriteAllText(Path.Combine(outDir, "matrix.md"), md);
        Console.WriteLine(); Console.WriteLine(md);
        return 0;
    }

    private sealed class ManifestFile { public List<Game> Games { get; set; } = new(); }

    private static Measure RunChild(string exe, Game g, string kind, string combo, int frames, int timeoutSeconds)
    {
        string ppu = kind == "picture" ? combo : "", apu = kind == "sound" ? combo : "";
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "--bridge-sweep-one", "--console", g.Console, "--rom", g.Rom, "--ppu", ppu, "--apu", apu, "--frames", frames.ToString(), "--input", g.Input }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var sbOut = new StringBuilder(); var sbErr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sbOut) sbOut.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sbErr) sbErr.AppendLine(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutSeconds * 1000))
        {
            try { p.Kill(true); } catch { }
            return new Measure { Timeout = true, Message = $"no result in {timeoutSeconds} s" };
        }
        p.WaitForExit();
        string text; lock (sbOut) text = sbOut.ToString();
        var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("RESULT "));
        if (line != null) return JsonSerializer.Deserialize<Measure>(line[7..], new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        string err; lock (sbErr) err = sbErr.ToString();
        return new Measure { Crashed = true, Message = $"exit {p.ExitCode} without a result: {(err.Length > 0 ? err.Split('\n')[0].Trim() : "")}" };
    }

    // ------------------------------------------------------------------------------------------------------------------ the output
    private static string Csv(List<Cell> all)
    {
        var sb = new StringBuilder("game,console,kind,combo,class,frames,fps,colours,changes,ssim,ssim_min,block_error,colour_error,recognisable,fid_samples,audio_rms,message\n");
        foreach (var c in all)
            sb.Append($"{c.Game},{c.Console},{c.Kind},{c.Combo},{c.Class},{c.M.Frames},{c.M.Fps:F1},{c.M.Colours},{c.M.Changes},{c.M.Ssim:F3},{c.M.SsimMin:F3},{c.M.BlockError:F1},{c.M.ColourError:F1},{c.M.Recognisable},{c.M.FidSamples},{c.M.AudioRms:F4},\"{c.M.Message.Replace('"', '\'')}\"\n");
        return sb.ToString();
    }

    private static string Markdown(List<Cell> all, int frames)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Bridge sweep, {frames} frames per run. Cell: class and, for a picture, the structural similarity (SSIM) to the source chip's own picture (blank where the route has no reference).");
        foreach (var kind in new[] { "picture", "sound" })
        {
            var of = all.Where(c => c.Kind == kind).ToList();
            if (of.Count == 0) continue;
            sb.AppendLine(); sb.AppendLine(kind == "picture" ? "### Pictures" : "### Sounds");
            foreach (var console in of.Select(c => c.Console).Distinct())
            {
                var rows = of.Where(c => c.Console == console).ToList();
                var combos = rows.Select(c => c.Combo).Distinct().ToList();
                sb.AppendLine(); sb.AppendLine($"**{console}**"); sb.AppendLine();
                sb.AppendLine("| game | " + string.Join(" | ", combos) + " |"); sb.AppendLine("|---|" + string.Join("|", combos.Select(_ => "---")) + "|");
                foreach (var game in rows.Select(c => c.Game).Distinct())
                {
                    var cs = combos.Select(co => rows.FirstOrDefault(c => c.Game == game && c.Combo == co)).Select(c => c == null ? "" : c.Class + (kind == "picture" && c.Class is "alive" or "lossy" && c.M.FidSamples > 0 ? $" {c.M.Ssim:F2}" : "")).ToList();
                    sb.AppendLine($"| {game} | " + string.Join(" | ", cs) + " |");
                }
            }
        }
        sb.AppendLine(); sb.AppendLine("### Totals"); sb.AppendLine();
        sb.AppendLine("| kind | " + string.Join(" | ", new[] { "alive", "lossy", "static", "frozen", "dead", "refused", "crashed", "hung" }) + " |");
        sb.AppendLine("|---|" + string.Join("|", Enumerable.Repeat("---", 8)) + "|");
        foreach (var kind in new[] { "picture", "sound" })
        {
            var of = all.Where(c => c.Kind == kind).ToList(); if (of.Count == 0) continue;
            sb.AppendLine($"| {kind} | " + string.Join(" | ", new[] { "alive", "lossy", "static", "frozen", "dead", "refused", "crashed", "hung" }.Select(k => of.Count(c => c.Class == k))) + " |");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------------------------ one combination (child)
    public static int RunOne(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string console = Opt("console", ""), rom = Opt("rom", ""), ppu = Opt("ppu", ""), apu = Opt("apu", ""), input = Opt("input", "");
        int frames = int.Parse(Opt("frames", "400"));
        Measure m;
        try { m = console == "nes" ? MeasureNes(rom, ppu, apu, frames, input) : MeasureSession(console, rom, ppu, apu, frames, input); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidDataException)
        {
            m = new Measure { Refused = true, Message = ex.GetType().Name + ": " + ex.Message.Split('\n')[0] };
        }
        catch (Exception ex) { m = new Measure { Crashed = true, Message = ex.GetType().Name + ": " + ex.Message.Split('\n')[0] }; }
        Console.WriteLine("RESULT " + JsonSerializer.Serialize(m, Json));
        Console.Out.Flush();
        return 0;
    }

    private sealed class Sampler
    {
        public readonly HashSet<ulong> Hashes = new(); public ulong Last; public bool First = true; public int Changes, ChangesLate, Colours, Samples;
        public readonly List<PictureMetrics.Result> Fid = new();
        public double AudioMax; public long AudioSamples; public double AudioWindowSum, AudioWindowDc; public int AudioWindowN; public int AudioChanges; public ulong LastAudioHash;

        public void Picture(ReadOnlySpan<uint> argb, bool late = false)
        {
            ulong h = 14695981039346656037UL; var set = new HashSet<uint>();
            foreach (uint c in argb) { h = (h ^ c) * 1099511628211UL; if (set.Count <= 64) set.Add(c & 0xFFFFFF); }
            Hashes.Add(h); Samples++;
            if (!First && h != Last) { Changes++; if (late) ChangesLate++; }
            First = false; Last = h; Colours = Math.Max(Colours, set.Count);
        }

        public void Audio(IEnumerable<float> mono, int count)
        {
            double sum = 0, dc = 0; int n = 0;
            foreach (float f in mono) { sum += (double)f * f; dc += f; n++; }
            AudioSamples += n; AudioWindowSum += sum; AudioWindowDc += dc; AudioWindowN += n;
        }

        /// <summary>The loudest window's AC level (the mean is taken off: an idle DAC, like the Model 1 Genesis's discrete one, rests at a constant offset that is not sound).</summary>
        public void EndAudioWindow()
        {
            if (AudioWindowN == 0) return;
            double mean = AudioWindowDc / AudioWindowN;
            AudioMax = Math.Max(AudioMax, Math.Sqrt(Math.Max(0, AudioWindowSum / AudioWindowN - mean * mean)));
            AudioWindowSum = 0; AudioWindowDc = 0; AudioWindowN = 0;
        }
    }

    private static Measure Finish(Sampler s, int frames, double seconds, IPictureBridgeInfo? info, string description)
    {
        var m = new Measure { Frames = frames, Fps = frames / Math.Max(seconds, 1e-6), Colours = s.Colours, Changes = s.Changes, ChangesLate = s.ChangesLate, Samples = s.Samples, AudioRms = s.AudioMax, AudioSamples = s.AudioSamples, Description = description };
        if (s.Fid.Count > 0)
        {
            m.FidSamples = s.Fid.Count; m.Ssim = s.Fid.Average(r => r.Ssim); m.SsimMin = s.Fid.Min(r => r.Ssim); m.BlockError = s.Fid.Average(r => r.BlockError); m.ColourError = s.Fid.Average(r => r.ColourError); m.Recognisable = s.Fid.Count(r => r.Recognisable);
        }
        if (info != null) foreach (var kv in info.Counters.Totals) m.Counters[kv.Key] = kv.Value;
        return m;
    }

    private static Measure MeasureNes(string rom, string ppu, string apu, int frames, string input)
    {
        var script = MixLabCli.ParseNesInput(input);
        var nes = new NES { RomName = Path.GetFileName(rom) };
        nes.LoadROM(File.ReadAllBytes(rom));
        // the app's defaults are the FIX family (a bare NES object starts on the Famiclone cores, which do not run every cartridge: VRUN's unofficial opcodes crash them), so the control and every bridge start from FIX
        if (!nes.SetCpuCore("FIX")) return new Measure { Refused = true, Message = "CPU FIX not applied" };
        if (ppu == "" && !nes.SetPpuCore("FIX")) return new Measure { Refused = true, Message = "PPU FIX not applied" };
        if (apu == "" && !nes.SetApuCore("FIX")) return new Measure { Refused = true, Message = "APU FIX not applied" };
        if (ppu != "" && (!nes.SetPpuCore(ppu) || !nes.GetPpuCoreId().EndsWith("_" + ppu, StringComparison.OrdinalIgnoreCase))) return new Measure { Refused = true, Message = $"PPU {ppu} not applied (got {nes.GetPpuCoreId()})" };
        if (apu != "" && (!nes.SetApuCore(apu) || !nes.GetApuCoreId().EndsWith("_" + apu, StringComparison.OrdinalIgnoreCase))) return new Measure { Refused = true, Message = $"APU {apu} not applied (got {nes.GetApuCoreId()})" };
        var bus = (Bus)typeof(NES).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(nes)!;
        if (ppu != "" && CoreCatalog.NesPpuNeedsPreciseTiming(ppu)) { bus.SpeedConfig.CpuCyclePrecisePpu = true; bus.SpeedConfig.NtscAccurateFrameRate = true; }
        var s = new Sampler(); var held = new bool[8]; var sw = Stopwatch.StartNew(); int ran = 0;
        var info = nes.GetPictureBridge();
        for (int f = 1; f <= frames; f++)
        {
            if (script.TryGetValue(f - 1, out var h)) held = h;
            nes.SetInputs(held, null);
            nes.RunFrame(); ran = f;
            if (nes.IsCrashed()) return new Measure { Crashed = true, Frames = f, Message = "NES core crashed: " + nes.GetCrashInfo().Split('\n')[0] };
            var audio = nes.GetAudioBuffer();
            s.Audio(audio, audio.Length);
            if (f % 20 == 0) s.EndAudioWindow();
            if (f > 30 && f % 10 == 0)
            {
                var fb = nes.GetFrameBuffer(); var argb = new uint[256 * 240];
                for (int i = 0; i < argb.Length; i++) argb[i] = 0xFF000000u | (uint)fb[i * 4] << 16 | (uint)fb[i * 4 + 1] << 8 | fb[i * 4 + 2];
                s.Picture(argb, f > frames / 2);
                if (info != null) s.Fid.Add(PictureMetrics.Compare(info.ReferenceCanvas, info.BridgeCanvas, info.CanvasWidth, 0, 0, 256, 240));
            }
        }
        s.EndAudioWindow();
        return Finish(s, ran, sw.Elapsed.TotalSeconds, info, $"{nes.GetCpuCoreId()}/{nes.GetPpuCoreId()}/{nes.GetApuCoreId()}");
    }

    private static Measure MeasureSession(string consoleKey, string romPath, string ppu, string apu, int frames, string input)
    {
        var script = PictureBridgeCli.ParseInput(input);
        byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(romPath), romPath, out string inner);
        var console = Consoles.FromKey(consoleKey);
        string cpuId = CoreCatalog.Default(console, CoreSlot.Cpu), ppuId = ppu != "" ? ppu : CoreCatalog.Default(console, CoreSlot.Ppu), apuId = apu != "" ? apu : CoreCatalog.Default(console, CoreSlot.Apu);
        // an id the console's menu does not list is refused here, as the apps refuse it (they would fall back to the default: a rescue we do not make)
        if (!CoreCatalog.Options(console, CoreSlot.Ppu).Any(o => o.Id.Equals(ppuId, StringComparison.OrdinalIgnoreCase))) return new Measure { Refused = true, Message = $"the {Consoles.DisplayName(console)} picture menu does not offer {ppuId}" };
        if (!CoreCatalog.Options(console, CoreSlot.Apu).Any(o => o.Id.Equals(apuId, StringComparison.OrdinalIgnoreCase))) return new Measure { Refused = true, Message = $"the {Consoles.DisplayName(console)} sound menu does not offer {apuId}" };
        using var session = ConsoleSessions.Create(console, rom, cpuId, ppuId, apuId, null, inner);
        var info = PictureBridgeCli.FindBridgeInfo(session);
        var s = new Sampler(); var held = PadButtons.None; var buf = new short[65536]; var sw = Stopwatch.StartNew(); int ran = 0;
        uint[]? prev = null;
        for (int f = 1; f <= frames; f++)
        {
            if (script.TryGetValue(f - 1, out var h)) held = h;
            session.SetPad(0, held);
            session.RunFrame(); ran = f;
            int n; var mono = new List<float>();
            while ((n = session.ReadSamples(buf)) > 0) for (int i = 0; i + 1 < n; i += 2) mono.Add((buf[i] + buf[i + 1]) / 65536f);
            s.Audio(mono, mono.Count);
            if (f % 20 == 0) s.EndAudioWindow();
            if (f > 30 && f % 10 == 0)
            {
                s.Picture(session.Frame.AsSpan(0, session.FrameWidth * session.FrameHeight), f > frames / 2);
                if (info != null)
                {
                    var (rx, ry, rw, rh) = info.Region;
                    var bridge = info.ReferenceIsSameFrame ? info.BridgeCanvas : prev;
                    if (bridge != null) s.Fid.Add(PictureMetrics.Compare(info.ReferenceCanvas, bridge, info.CanvasWidth, rx, ry, rw, rh));
                }
            }
            if (info != null && !info.ReferenceIsSameFrame) prev = (uint[])info.BridgeCanvas.Clone();
        }
        s.EndAudioWindow();
        return Finish(s, ran, sw.Elapsed.TotalSeconds, info, session.Description);
    }
}
