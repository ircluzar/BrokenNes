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
        // ---- hot swap (a "swap" cell): the core is changed in the middle of the run; Post* describe the frames after the swap only
        public bool SwapAttempted { get; set; }
        /// <summary>The swap happened in place (TrySwapCore true / the NES core setter accepted the id).</summary>
        public bool SwapInPlace { get; set; }
        public string SwapNote { get; set; } = "";
        public int PostColours { get; set; }
        public int PostChanges { get; set; }
        public double PostAudioRms { get; set; }
        /// <summary>The last sampled picture, as a 64x60 luma thumbnail (and its size and hash), so a run can be compared with its control's: does the game still look like itself?</summary>
        public int PicW { get; set; }
        public int PicH { get; set; }
        public byte[] Thumb { get; set; } = Array.Empty<byte>();
        public string FinalHash { get; set; } = "";
    }

    public sealed class Cell
    {
        public string Game { get; set; } = "";
        public string Console { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Combo { get; set; } = "";
        public string Class { get; set; } = "";
        /// <summary>For the menu and swap sweeps: which slot (cpu, ppu, apu) and the menu's label of the entry.</summary>
        public string Slot { get; set; } = "";
        public string Label { get; set; } = "";
        /// <summary>The sound class of the run (alive, silent, static, ""): reported for every kind, because a CPU or a picture chip that is fine for the picture can still leave the game mute.</summary>
        public string AudioClass { get; set; } = "";
        /// <summary>Mean luma difference (0..1) between this run's last picture and its control's (-1: not comparable), and whether they are identical.</summary>
        public double Diff { get; set; } = -1;
        public bool SameAsControl { get; set; }
        public Measure M { get; set; } = new();
    }

    /// <summary>A CPU, swap or combination run whose picture is alive but differs from the control's by more than this (mean luma, 0..1) is "diverged": it runs, but the game is not where it is on its own cores.</summary>
    internal const double DivergedLuma = 0.05;

    /// <summary>One combination of cores (empty = the console's default) and, for a hot swap, the change made mid-run ("frame:slot:id", slot cpu|ppu|apu).</summary>
    public sealed record Spec(string Cpu = "", string Ppu = "", string Apu = "", string Swap = "");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    /// <summary>A bridge that makes less than this (AC level of its loudest 20 frames, full scale 1.0) is silent.</summary>
    private const double SilentRms = 0.0004;
    /// <summary>A game whose own sound (on its own chips) stays below this is silent for the sweep's purpose: -46 dBFS. The Model 1 Genesis's discrete DAC rests at a level that ripples (0.0033 on two games that play nothing: Strider
    /// and Castle of Illusion measured with --md-run --sound-report: no key-on, no DAC, no PSG tone), the quietest game that plays music in the sweep sets 0.028.</summary>
    private const double GameAudibleRms = 0.005;

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
            bool controlSound = control == null || control.AudioRms >= GameAudibleRms;
            return m.AudioRms < SilentRms ? (controlSound ? "dead" : "static") : "alive";
        }
        if (m.Colours <= 1) return control == null || control.Colours > 1 ? "dead" : "static";
        if (m.Changes == 0) return control != null && control.Changes == 0 ? "static" : "frozen";
        if (m.ChangesLate == 0 && control != null && control.ChangesLate > 0) return "frozen";      // it stopped part-way while the game went on
        if (m.FidSamples > 0 && m.Ssim < 0.55) return "lossy";
        return "alive";
    }

    /// <summary>A hot-swap run: the cells' picture after the swap is judged against the control's second half. <c>restart</c> is not a failure of the picture: it says TrySwapCore refused and the app would restart the game on the new core (the reason is in SwapNote).</summary>
    internal static string ClassifySwap(Measure m, Measure? control)
    {
        if (m.Refused) return "refused";
        if (m.Crashed) return "crashed";
        if (m.Timeout) return "hung";
        if (!m.SwapInPlace) return "restart";
        if (m.PostColours <= 1) return control == null || control.Colours > 1 ? "dead" : "static";
        if (m.PostChanges == 0) return control != null && control.ChangesLate == 0 ? "static" : "frozen";
        return "alive";
    }

    /// <summary>The sound of a run next to its control's: alive when it is audible, silent when the game's own chips make sound and this combination does not, static when the game itself is silent.</summary>
    internal static string ClassifyAudio(double rms, Measure? control)
    {
        if (rms >= SilentRms) return "alive";
        return control == null || control.AudioRms >= GameAudibleRms ? "silent" : "static";
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
        var ids = Opt("ids", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count > 0) games = games.Where(g => ids.Contains(g.Id)).ToList();
        bool menus = args.Contains("--menus", StringComparer.OrdinalIgnoreCase), swaps = args.Contains("--swaps", StringComparer.OrdinalIgnoreCase), cross = args.Contains("--cross", StringComparer.OrdinalIgnoreCase);
        bool includeNative = args.Contains("--include-native", StringComparer.OrdinalIgnoreCase);
        var slotFilter = Opt("slots", "cpu,ppu,apu").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entryFilter = Opt("entries", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);   // only these menu ids (empty: all)
        IEnumerable<(string slot, string id, string label)> Entries(string console) => MenuEntries(console, includeNative).Where(e => slotFilter.Contains(e.slot) && (entryFilter.Count == 0 || entryFilter.Contains(e.id)));
        if (menus || swaps || cross) pictures = false;     // the menu modes replace the older picture / sound lists

        // the jobs: control first, then every combination
        var work = new List<(Game g, string kind, string combo, string slot, string label, Spec spec)>();
        foreach (var g in games)
        {
            work.Add((g, "control", "", "", "", new Spec()));
            if (pictures) foreach (var p in overrides.TryGetValue(g.Console, out var ov) ? ov : DefaultPictures(g.Console)) work.Add((g, "picture", p, "ppu", p, new Spec(Ppu: p)));
            if (sound) foreach (var s in DefaultSounds(g.Console)) work.Add((g, "sound", s, "apu", s, new Spec(Apu: s)));
            if (menus)
                foreach (var e in Entries(g.Console))
                    work.Add((g, e.slot == "cpu" ? "cpu" : e.slot == "ppu" ? "picture" : "sound", e.id, e.slot, e.label, e.slot == "cpu" ? new Spec(Cpu: e.id) : e.slot == "ppu" ? new Spec(Ppu: e.id) : new Spec(Apu: e.id)));
            if (swaps)
                foreach (var e in Entries(g.Console))
                    work.Add((g, "swap", e.slot + ":" + e.id, e.slot, e.label, new Spec(Swap: $"{frames / 2}:{e.slot}:{e.id}")));
            if (cross)
                foreach (var c in CrossEntries(g.Console)) work.Add((g, "combo", c.Label(), "", c.Label(), c));
        }
        Console.WriteLine($"bridge sweep: {games.Count} games, {work.Count} runs of {frames} frames, {jobs} at a time, {timeout} s each");
        var cells = new ConcurrentBag<Cell>();
        var sem = new SemaphoreSlim(jobs); var tasks = new List<Task>(); int done = 0;
        foreach (var (g, kind, combo, slot, label, spec) in work)
        {
            sem.Wait();
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    var m = RunChild(exe, g, spec, frames, timeout);
                    cells.Add(new Cell { Game = g.Id, Console = g.Console, Kind = kind, Combo = combo, Slot = slot, Label = label, M = m });
                    int n = Interlocked.Increment(ref done);
                    Console.WriteLine($"  [{n}/{work.Count}] {g.Id} {kind} {combo}: {(m.Refused ? "refused: " + m.Message : m.Crashed ? "crashed: " + m.Message : m.Timeout ? "hung" : $"{m.Frames} frames, {m.Colours} colours, {m.Changes} changes, ssim {m.Ssim:F2}, audio {m.AudioRms:F4}{(m.SwapAttempted ? $", swap {(m.SwapInPlace ? "in place" : "NOT in place: " + m.SwapNote)}" : "")}")}");
                }
                finally { sem.Release(); }
            }));
        }
        Task.WaitAll(tasks.ToArray());

        // classify against each game's control and write the matrix
        var all = cells.OrderBy(c => c.Console).ThenBy(c => c.Game).ThenBy(c => c.Kind).ThenBy(c => c.Slot).ThenBy(c => c.Combo).ToList();
        foreach (var c in all)
        {
            var control = all.FirstOrDefault(x => x.Game == c.Game && x.Kind == "control")?.M;
            c.Class = c.Kind == "control" ? (c.M.Refused ? "refused" : c.M.Crashed ? "crashed" : c.M.Timeout ? "hung" : "control")
                    : c.Kind == "swap" ? ClassifySwap(c.M, control)
                    : Classify(c.M, control, c.Kind == "sound");
            c.AudioClass = c.M.Refused || c.M.Crashed || c.M.Timeout ? "" : ClassifyAudio(c.Kind == "swap" ? c.M.PostAudioRms : c.M.AudioRms, control);
            // does the game still look like itself? (same size picture as the control's: the mean luma difference of the last sampled frames)
            if (control != null && c.Kind is "cpu" or "swap" or "combo" && c.M.Thumb.Length > 0 && control.Thumb.Length == c.M.Thumb.Length && control.PicW == c.M.PicW && control.PicH == c.M.PicH)
            {
                c.SameAsControl = control.FinalHash == c.M.FinalHash;
                c.Diff = Enumerable.Range(0, c.M.Thumb.Length).Average(i => Math.Abs(c.M.Thumb[i] - control.Thumb[i])) / 255.0;
                if (c.Class == "alive" && !(c.Kind == "swap" && c.Slot == "ppu") && c.Diff > DivergedLuma) c.Class = "diverged";
            }
        }
        File.WriteAllText(Path.Combine(outDir, "sweep.json"), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outDir, "sweep.csv"), Csv(all));
        string md = menus || swaps || cross ? MenuMarkdown(all, frames) : Markdown(all, frames);
        File.WriteAllText(Path.Combine(outDir, "matrix.md"), md);
        Console.WriteLine(); Console.WriteLine(md);
        return 0;
    }

    private sealed class ManifestFile { public List<Game> Games { get; set; } = new(); }

    /// <summary>Every entry of a console's three core menus except the default one (which is the control): slot, id, the menu's label. The NES's own family is left out unless asked (the sweep is about the bridges).</summary>
    internal static IEnumerable<(string slot, string id, string label)> MenuEntries(string consoleKey, bool includeNative)
    {
        var console = Consoles.FromKey(consoleKey);
        foreach (var slot in new[] { CoreSlot.Cpu, CoreSlot.Ppu, CoreSlot.Apu })
        {
            string def = CoreCatalog.Default(console, slot);
            foreach (var o in CoreCatalog.Options(console, slot))
            {
                if (o.Id.Equals(def, StringComparison.OrdinalIgnoreCase)) continue;
                if (console == ConsoleKind.Nes && o.Family == "NES" && !includeNative) continue;
                yield return (slot.ToString().ToLowerInvariant(), o.Id, o.Label);
            }
        }
    }

    /// <summary>The curated cross-product: all three slots at once, for the chips that cross a Sega border. The full Cartesian product of the NES's menus is not run (it is thousands of cells of gimmick cores); these are the combinations in which Sega chips meet each other and the other consoles.</summary>
    internal static IEnumerable<Spec> CrossEntries(string consoleKey)
    {
        string[] cpus, ppus, apus;
        switch (consoleKey)
        {
            case "nes": cpus = new[] { "FIX", "Z80", "M68K" }; ppus = new[] { "FIX", "SMS", "MD" }; apus = new[] { "FIX", "SMS", "SMSFM", "MD" }; break;
            case "sms": cpus = new[] { "" }; ppus = new[] { "", "NES:FIXS", "NES:FIX" }; apus = new[] { "", "SMS-FM", "NES:FIX", "NES:FIXS" }; break;
            case "gg": cpus = new[] { "" }; ppus = new[] { "", "NES:FIXS", "NES:FIX" }; apus = new[] { "", "NES:FIX", "NES:FIXS" }; break;
            case "md": cpus = new[] { "" }; ppus = new[] { "", "NES:FIXS", "NES:FIX" }; apus = new[] { "", "NES:FIX", "NES:FIXS" }; break;
            default: cpus = new[] { "" }; ppus = new[] { "", "NES:SMS", "NES:MD" }; apus = new[] { "", "NES:SMS", "NES:SMSFM", "NES:MD" }; break;
        }
        foreach (var c in cpus) foreach (var p in ppus) foreach (var a in apus)
        {
            if (c is "" or "FIX" && p is "" or "FIX" && a is "" or "FIX") continue;     // the default combination is the control
            yield return new Spec(c is "FIX" ? "" : c, p is "FIX" ? "" : p, a is "FIX" ? "" : a);
        }
    }

    private static string Label(this Spec s) => $"cpu={(s.Cpu == "" ? "own" : s.Cpu)} ppu={(s.Ppu == "" ? "own" : s.Ppu)} apu={(s.Apu == "" ? "own" : s.Apu)}";

    private static Measure RunChild(string exe, Game g, Spec spec, int frames, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "--bridge-sweep-one", "--console", g.Console, "--rom", g.Rom, "--cpu", spec.Cpu, "--ppu", spec.Ppu, "--apu", spec.Apu, "--swap", spec.Swap, "--frames", frames.ToString(), "--input", g.Input }) psi.ArgumentList.Add(a);
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
        var sb = new StringBuilder("game,console,kind,slot,combo,class,audio,frames,fps,colours,changes,post_colours,post_changes,ssim,ssim_min,block_error,colour_error,recognisable,fid_samples,audio_rms,post_audio_rms,swap_in_place,message,label\n");
        foreach (var c in all)
            sb.Append($"{c.Game},{c.Console},{c.Kind},{c.Slot},{c.Combo},{c.Class},{c.AudioClass},{c.M.Frames},{c.M.Fps:F1},{c.M.Colours},{c.M.Changes},{c.M.PostColours},{c.M.PostChanges},{c.M.Ssim:F3},{c.M.SsimMin:F3},{c.M.BlockError:F1},{c.M.ColourError:F1},{c.M.Recognisable},{c.M.FidSamples},{c.M.AudioRms:F4},{c.M.PostAudioRms:F4},{(c.M.SwapAttempted ? c.M.SwapInPlace.ToString() : "")},\"{(c.M.Message + " " + c.M.SwapNote).Trim().Replace('"', '\'')}\",\"{c.Label.Replace('"', '\'')}\"\n");
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

    /// <summary>The menu / swap / cross sweep as a table per console: one row per entry, the class of every game that ran it.</summary>
    internal static string MenuMarkdown(List<Cell> all, int frames)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Menu sweep, {frames} frames per run. Class per game (picture), sound class after a slash when it is not alive; swap rows swap the core at frame {frames / 2}; 'restart' = TrySwapCore refused (the app would restart the game on it).");
        foreach (var kind in new[] { "cpu", "picture", "sound", "swap", "combo" })
        {
            var of = all.Where(c => c.Kind == kind).ToList();
            if (of.Count == 0) continue;
            sb.AppendLine(); sb.AppendLine($"### {kind}");
            foreach (var console in of.Select(c => c.Console).Distinct())
            {
                var rows = of.Where(c => c.Console == console).ToList();
                var gamesHere = rows.Select(c => c.Game).Distinct().ToList();
                sb.AppendLine(); sb.AppendLine($"**{console}**"); sb.AppendLine();
                sb.AppendLine("| slot | entry | " + string.Join(" | ", gamesHere) + " | note |"); sb.AppendLine("|---|---|" + string.Join("|", gamesHere.Select(_ => "---")) + "|---|");
                foreach (var entry in rows.Select(c => (c.Slot, c.Combo)).Distinct())
                {
                    var cs = gamesHere.Select(gm => rows.FirstOrDefault(c => c.Game == gm && c.Slot == entry.Slot && c.Combo == entry.Combo)).ToList();
                    string Show(Cell? c) => c == null ? "" : c.Class + (c.Class is "alive" or "lossy" && kind == "picture" && c.M.FidSamples > 0 ? $" {c.M.Ssim:F2}" : "") + (c.Diff >= 0 ? (c.SameAsControl ? " (=)" : $" (d{c.Diff:F2})") : "") + (c.AudioClass is "silent" ? "/silent" : "");
                    string note = string.Join("; ", cs.Where(c => c != null && (c.M.Refused || c.M.Crashed || c.Class == "restart" || c.Class is "dead" or "frozen")).Select(c => $"{c!.Game}: {(c.M.Message + " " + c.M.SwapNote).Trim()}").Take(2));
                    sb.AppendLine($"| {entry.Slot} | {entry.Combo.Replace("|", "/")} | " + string.Join(" | ", cs.Select(Show)) + $" | {note.Replace("|", "/")} |");
                }
            }
        }
        sb.AppendLine(); sb.AppendLine("### Totals"); sb.AppendLine();
        var classes = new[] { "alive", "lossy", "diverged", "restart", "static", "frozen", "dead", "refused", "crashed", "hung" };
        sb.AppendLine("| kind | " + string.Join(" | ", classes) + " | silent |"); sb.AppendLine("|---|" + string.Join("|", Enumerable.Repeat("---", classes.Length + 1)) + "|");
        foreach (var kind in new[] { "cpu", "picture", "sound", "swap", "combo" })
        {
            var of = all.Where(c => c.Kind == kind).ToList(); if (of.Count == 0) continue;
            sb.AppendLine($"| {kind} | " + string.Join(" | ", classes.Select(k => of.Count(c => c.Class == k))) + $" | {of.Count(c => c.AudioClass == "silent")} |");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------------------------ one combination (child)
    public static int RunOne(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string console = Opt("console", ""), rom = Opt("rom", ""), cpu = Opt("cpu", ""), ppu = Opt("ppu", ""), apu = Opt("apu", ""), input = Opt("input", ""), swap = Opt("swap", "");
        int frames = int.Parse(Opt("frames", "400"));
        Measure m;
        try { m = console == "nes" ? MeasureNes(rom, cpu, ppu, apu, swap, frames, input) : MeasureSession(console, rom, cpu, ppu, apu, swap, frames, input); }
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

        public int PicW, PicH; public byte[] Thumb = Array.Empty<byte>(); public ulong FinalHash;

        public void Picture(ReadOnlySpan<uint> argb, bool late = false, int width = 256)
        {
            ulong h = 14695981039346656037UL; var set = new HashSet<uint>();
            foreach (uint c in argb) { h = (h ^ c) * 1099511628211UL; if (set.Count <= 64) set.Add(c & 0xFFFFFF); }
            FinalHash = h; PicW = width; PicH = argb.Length / Math.Max(1, width); Thumb = MakeThumb(argb, PicW, PicH);
            Hashes.Add(h); Samples++;
            if (!First && h != Last) { Changes++; if (late) ChangesLate++; }
            First = false; Last = h; Colours = Math.Max(Colours, set.Count);
        }

        /// <summary>64 x 60 box-averaged luma (0..255).</summary>
        private static byte[] MakeThumb(ReadOnlySpan<uint> argb, int w, int h)
        {
            const int TW = 64, TH = 60;
            var t = new byte[TW * TH];
            if (w <= 0 || h <= 0) return t;
            for (int ty = 0; ty < TH; ty++)
                for (int tx = 0; tx < TW; tx++)
                {
                    int x0 = tx * w / TW, x1 = Math.Max(x0 + 1, (tx + 1) * w / TW), y0 = ty * h / TH, y1 = Math.Max(y0 + 1, (ty + 1) * h / TH);
                    long sum = 0; int n = 0;
                    for (int y = y0; y < y1 && y < h; y++) for (int x = x0; x < x1 && x < w; x++) { uint c = argb[y * w + x]; sum += (((c >> 16) & 0xFF) * 77 + ((c >> 8) & 0xFF) * 150 + (c & 0xFF) * 29) >> 8; n++; }
                    t[ty * TW + tx] = (byte)(n == 0 ? 0 : sum / n);
                }
            return t;
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

    private static Measure Finish(Sampler s, int frames, double seconds, IPictureBridgeInfo? info, string description, Sampler? post = null, bool swapped = false, bool inPlace = false, string swapNote = "")
    {
        var m = new Measure { Frames = frames, Fps = frames / Math.Max(seconds, 1e-6), Colours = s.Colours, Changes = s.Changes, ChangesLate = s.ChangesLate, Samples = s.Samples, AudioRms = s.AudioMax, AudioSamples = s.AudioSamples, Description = description };
        if (s.Fid.Count > 0)
        {
            m.FidSamples = s.Fid.Count; m.Ssim = s.Fid.Average(r => r.Ssim); m.SsimMin = s.Fid.Min(r => r.Ssim); m.BlockError = s.Fid.Average(r => r.BlockError); m.ColourError = s.Fid.Average(r => r.ColourError); m.Recognisable = s.Fid.Count(r => r.Recognisable);
        }
        m.PicW = s.PicW; m.PicH = s.PicH; m.Thumb = s.Thumb; m.FinalHash = s.FinalHash.ToString("X16");
        if (swapped) { m.SwapAttempted = true; m.SwapInPlace = inPlace; m.SwapNote = swapNote; }
        if (post != null) { post.EndAudioWindow(); m.PostColours = post.Colours; m.PostChanges = post.Changes; m.PostAudioRms = post.AudioMax; }
        if (info != null) foreach (var kv in info.Counters.Totals) m.Counters[kv.Key] = kv.Value;
        return m;
    }

    /// <summary>"frame:slot:id" (the id may itself contain colons: NES:FIXS).</summary>
    private static (int frame, string slot, string id)? ParseSwap(string swap)
    {
        if (swap == "") return null;
        var p = swap.Split(':', 3);
        return (int.Parse(p[0]), p[1].ToLowerInvariant(), p[2]);
    }

    private static Measure MeasureNes(string rom, string cpu, string ppu, string apu, string swap, int frames, string input)
    {
        var script = MixLabCli.ParseNesInput(input);
        var sw0 = ParseSwap(swap);
        var nes = new NES { RomName = Path.GetFileName(rom) };
        nes.LoadROM(File.ReadAllBytes(rom));
        // the app's defaults are the FIX family (a bare NES object starts on the Famiclone cores, which do not run every cartridge: VRUN's unofficial opcodes crash them), so the control and every bridge start from FIX
        string cpuId = cpu == "" ? "FIX" : cpu;
        if (!nes.SetCpuCore(cpuId) || !nes.GetCpuCoreId().EndsWith("_" + cpuId, StringComparison.OrdinalIgnoreCase)) return new Measure { Refused = true, Message = $"CPU {cpuId} not applied (got {nes.GetCpuCoreId()})" };
        if (ppu == "" && !nes.SetPpuCore("FIX")) return new Measure { Refused = true, Message = "PPU FIX not applied" };
        if (apu == "" && !nes.SetApuCore("FIX")) return new Measure { Refused = true, Message = "APU FIX not applied" };
        if (ppu != "" && (!nes.SetPpuCore(ppu) || !nes.GetPpuCoreId().EndsWith("_" + ppu, StringComparison.OrdinalIgnoreCase))) return new Measure { Refused = true, Message = $"PPU {ppu} not applied (got {nes.GetPpuCoreId()})" };
        if (apu != "" && (!nes.SetApuCore(apu) || !nes.GetApuCoreId().EndsWith("_" + apu, StringComparison.OrdinalIgnoreCase))) return new Measure { Refused = true, Message = $"APU {apu} not applied (got {nes.GetApuCoreId()})" };
        var bus = (Bus)typeof(NES).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(nes)!;
        if (ppu != "" && CoreCatalog.NesPpuNeedsPreciseTiming(ppu)) { bus.SpeedConfig.CpuCyclePrecisePpu = true; bus.SpeedConfig.NtscAccurateFrameRate = true; }
        var s = new Sampler(); Sampler? post = null; bool swapped = false, inPlace = false; string swapNote = "";
        var held = new bool[8]; var sw = Stopwatch.StartNew(); int ran = 0;
        var info = nes.GetPictureBridge();
        for (int f = 1; f <= frames; f++)
        {
            if (sw0 is { } sp && f - 1 == sp.frame)
            {
                // the app's hot swap for the NES: the core setters (the state carries over); a bridge picture chip also needs the board's precise timing, as the menu switches it on
                swapped = true;
                if (sp.slot == "ppu" && CoreCatalog.NesPpuNeedsPreciseTiming(sp.id)) { bus.SpeedConfig.CpuCyclePrecisePpu = true; bus.SpeedConfig.NtscAccurateFrameRate = true; }
                inPlace = sp.slot switch
                {
                    "cpu" => nes.SetCpuCore(sp.id) && nes.GetCpuCoreId().EndsWith("_" + sp.id, StringComparison.OrdinalIgnoreCase),
                    "ppu" => nes.SetPpuCore(sp.id) && nes.GetPpuCoreId().EndsWith("_" + sp.id, StringComparison.OrdinalIgnoreCase),
                    _ => nes.SetApuCore(sp.id) && nes.GetApuCoreId().EndsWith("_" + sp.id, StringComparison.OrdinalIgnoreCase),
                };
                if (!inPlace) swapNote = $"the {sp.slot} core {sp.id} was not applied";
                post = new Sampler();
                info = nes.GetPictureBridge();
            }
            if (script.TryGetValue(f - 1, out var h)) held = h;
            nes.SetInputs(held, null);
            nes.RunFrame(); ran = f;
            if (nes.IsCrashed()) return new Measure { Crashed = true, Frames = f, Message = "NES core crashed: " + nes.GetCrashInfo().Split('\n')[0] };
            var audio = nes.GetAudioBuffer();
            s.Audio(audio, audio.Length); post?.Audio(audio, audio.Length);
            if (f % 20 == 0) { s.EndAudioWindow(); post?.EndAudioWindow(); }
            if (f > 30 && f % 10 == 0)
            {
                var fb = nes.GetFrameBuffer(); var argb = new uint[256 * 240];
                for (int i = 0; i < argb.Length; i++) argb[i] = 0xFF000000u | (uint)fb[i * 4] << 16 | (uint)fb[i * 4 + 1] << 8 | fb[i * 4 + 2];
                s.Picture(argb, f > frames / 2); post?.Picture(argb, true);
                if (info != null) s.Fid.Add(PictureMetrics.Compare(info.ReferenceCanvas, info.BridgeCanvas, info.CanvasWidth, 0, 0, 256, 240));
            }
        }
        s.EndAudioWindow();
        return Finish(s, ran, sw.Elapsed.TotalSeconds, info, $"{nes.GetCpuCoreId()}/{nes.GetPpuCoreId()}/{nes.GetApuCoreId()}", post, swapped, inPlace, swapNote);
    }

    private static Measure MeasureSession(string consoleKey, string romPath, string cpu, string ppu, string apu, string swap, int frames, string input)
    {
        var script = PictureBridgeCli.ParseInput(input);
        var sw0 = ParseSwap(swap);
        byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(romPath), romPath, out string inner);
        var console = Consoles.FromKey(consoleKey);
        string cpuId = cpu != "" ? cpu : CoreCatalog.Default(console, CoreSlot.Cpu), ppuId = ppu != "" ? ppu : CoreCatalog.Default(console, CoreSlot.Ppu), apuId = apu != "" ? apu : CoreCatalog.Default(console, CoreSlot.Apu);
        // an id the console's menu does not list is refused here, as the apps refuse it (they would fall back to the default: a rescue we do not make)
        foreach (var (slot, id) in new[] { (CoreSlot.Cpu, cpuId), (CoreSlot.Ppu, ppuId), (CoreSlot.Apu, apuId) })
            if (!CoreCatalog.Options(console, slot).Any(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return new Measure { Refused = true, Message = $"the {Consoles.DisplayName(console)} {slot} menu does not offer {id}" };
        if (sw0 is { } chk && !CoreCatalog.Options(console, chk.slot == "cpu" ? CoreSlot.Cpu : chk.slot == "ppu" ? CoreSlot.Ppu : CoreSlot.Apu).Any(o => o.Id.Equals(chk.id, StringComparison.OrdinalIgnoreCase)))
            return new Measure { Refused = true, Message = $"the {Consoles.DisplayName(console)} {chk.slot} menu does not offer {chk.id}" };
        using var session = ConsoleSessions.Create(console, rom, cpuId, ppuId, apuId, null, inner);
        var info = PictureBridgeCli.FindBridgeInfo(session);
        var s = new Sampler(); Sampler? post = null; bool swapped = false, inPlace = false; string swapNote = "";
        var held = PadButtons.None; var buf = new short[65536]; var sw = Stopwatch.StartNew(); int ran = 0;
        uint[]? prev = null;
        for (int f = 1; f <= frames; f++)
        {
            if (sw0 is { } sp && f - 1 == sp.frame)
            {
                swapped = true;
                var slot = sp.slot == "cpu" ? CoreSlot.Cpu : sp.slot == "ppu" ? CoreSlot.Ppu : CoreSlot.Apu;
                try { inPlace = session.TrySwapCore(slot, sp.id); }
                catch (Exception ex) { return new Measure { Crashed = true, Frames = f, Message = "TrySwapCore threw " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0], SwapAttempted = true }; }
                if (!inPlace) swapNote = session.SwapRefusal(slot, sp.id);
                post = new Sampler();
                info = PictureBridgeCli.FindBridgeInfo(session);
            }
            session.SetPad(0, held = script.TryGetValue(f - 1, out var h) ? h : held);
            session.RunFrame(); ran = f;
            int n; var mono = new List<float>();
            while ((n = session.ReadSamples(buf)) > 0) for (int i = 0; i + 1 < n; i += 2) mono.Add((buf[i] + buf[i + 1]) / 65536f);
            s.Audio(mono, mono.Count); post?.Audio(mono, mono.Count);
            if (f % 20 == 0) { s.EndAudioWindow(); post?.EndAudioWindow(); }
            if (f > 30 && f % 10 == 0)
            {
                var pic = session.Frame.AsSpan(0, session.FrameWidth * session.FrameHeight);
                s.Picture(pic, f > frames / 2, session.FrameWidth); post?.Picture(pic, true, session.FrameWidth);
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
        return Finish(s, ran, sw.Elapsed.TotalSeconds, info, session.Description, post, swapped, inPlace, swapNote);
    }
}
