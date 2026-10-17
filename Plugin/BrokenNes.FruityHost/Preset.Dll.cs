// Round 6 (presets): the presets through the real plugin DLL (Native AOT): the Presets chips, a preset played in direct mode by FL's notes against Runaway playing the same instrument from its game (bit for bit), the NES player, the project's own copy of a preset
// (a library file gone, a damaged section, a newer one), nothing allocated while playing or while a preset is loaded, and the Presets tab with the real library, driven by real mouse and key messages. Hooks 320-339 (Bn2Plugin.Presets.cs).
using System.Runtime.InteropServices;
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using BrokenNes2;
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkPLoad = 320, HkPText = 321, HkPState = 322, HkPName = 323, HkPCrc = 324, HkPNote = 325, HkPClear = 326, HkPAuditioning = 327, HkPAudition = 328, HkPAlloc = 329, HkPMutant = 330, HkPSection = 331,
        HkPMode = 332, HkPSummary = 333, HkPFolder = 334, HkPRescan = 335, HkPLibId = 336, HkPSega = 337, HkPGb = 338;

    private static int PLoad(PluginInstance p, byte[] file, out string text)
    {
        long r;
        fixed (byte* b = file) r = p.Test(HkPLoad, file.Length, (nint)b);
        text = DllText(p, HkPText);
        return (int)r;
    }

    private static string PNote(PluginInstance p) => p.Test(HkPNote, 0, 0) < 0 ? "" : DllText(p, HkPNote);

    private static bool SamePcm(float[] a, float[] b) => a.Length == b.Length && MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));

    /// <summary>The notes both instances play: two overlapping notes with a glide and a short one, 44.1 kHz.</summary>
    private static Scenario PresetScript()
    {
        var s = new Scenario { Name = "preset", Tempo = 120, LengthBeats = 5 };
        s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1.5, Key = 60, Velocity = 0.8, Channel = 0 });
        s.Notes.Add(new SimNote { StartBeat = 0.5, LengthBeats = 1.75, Key = 67, Velocity = 0.5, Channel = 0, Slides = [new SlideSeg { StartBeat = 0.5, LengthBeats = 1, ToKey = 64 }] });
        s.Notes.Add(new SimNote { StartBeat = 3, LengthBeats = 1, Key = 55, Velocity = 1.0, Channel = 0 });
        return s;
    }

    private static float[] PlayScript(PluginInstance p) => new Mixer { SampleRate = 44100 }.Run(p, PresetScript(), 500);

    private PluginInstance NewOn(int emulator, int console, int rate = 44100)
    {
        var p = lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, rate);
        p.SetParam(PEmulator, emulator);
        p.SetParam(PConsole, console);
        return p;
    }

    /// <summary>The presets as one check of the suite: the pure checks and their deliberate bugs, then the plugin DLL (chips, playback against Runaway, the NES player, projects, allocation, the Presets tab), then the DLL's own deliberate bugs.</summary>
    private void Presets(T t)
    {
        var results = PresetChecks.RunChecks(null);
        foreach (var o in results)
        {
            foreach (var f in o.Report.Failures.Take(3)) t.Expect(false, $"[{o.Id}] {f}");
            if (o.Report.Failures.Count > 3) t.Expect(false, $"[{o.Id}] ... and {o.Report.Failures.Count - 3} more");
            foreach (var w in o.Report.Warnings) t.Warn(false, $"[{o.Id}] {w}");
            foreach (var n in o.Report.Notes.Where(n => n.Contains("render bit-identical") || n.Contains("Runaway's copy as captured") || n.Contains("of that"))) t.Note(n);
        }
        t.Note($"{results.Count} pure checks, {results.Sum(o => o.Report.Assertions)} assertions");
        string? oldDir = Environment.GetEnvironmentVariable("BROKENNES_PRESETS_DIR");
        string scratch = Path.Combine(Path.GetTempPath(), "bn2_presets_dll_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("BROKENNES_PRESETS_DIR", scratch);          // (the plugin's library reads it every time: nothing here touches the user's own presets)
        try
        {
            PresetsChips(t);
            PresetsEqualRunaway(t);
            PresetsNes(t);
            PresetsProject(t);
            PresetsAlloc(t);
            PresetsUiFlow(t);

            // the DLL's own deliberate bugs (switched on inside the DLL through hook 330): the scenario meant to catch each must fail
            foreach (var (id, what, scenario) in new (int, string, Action<T>)[]
            {
                (PresetMutants.ProjectKeepsNothing, "the project keeps no copy of the preset", PresetsProject),
                (PresetMutants.SectionCrcNotChecked, "the project section's checksum is not checked", PresetsProject),
                (PresetMutants.LoadsWrongBytes, "the library hands the plugin a file with its last byte missing", PresetsUiFlow),
            })
            {
                var tt = new T();
                foreach (var f in Directory.EnumerateFiles(scratch, "*.bn2preset", SearchOption.AllDirectories)) File.Delete(f);      // (the scenarios start from an empty library, as they did the first time)
                using (var probe = lib.Create()) probe.Test(HkPMutant, 0, id);
                try { scenario(tt); }
                catch (Exception e) { tt.Failures.Add("exception " + e.Message); }
                finally { using var probe = lib.Create(); probe.Test(HkPMutant, 0, 0); }
                t.Expect(tt.Failures.Count > 0, $"the scenario did not notice the deliberate bug '{what}': it cannot fail on it");
                if (tt.Failures.Count > 0) t.Note($"deliberate bug '{what}' caught: {Trim(tt.Failures[0])}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BROKENNES_PRESETS_DIR", oldDir);
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var (lines, missed) = PresetChecks.RunMutants(null);
        t.Expect(missed == 0, $"{missed} deliberate bugs were not caught: " + string.Join("; ", lines.Where(l => l.Contains("MISSED"))));
        t.Note($"{lines.Count - missed} of {lines.Count} deliberate bugs caught by the pure checks");
    }

    private static string Trim(string s) => s.Length <= 150 ? s : s[..147] + "...";

    private static int ConsoleParam(ConsoleId c) => c switch { ConsoleId.Nes => 0, ConsoleId.GameBoy => 1, ConsoleId.Snes => 2, ConsoleId.MasterSystem => 3, _ => 4 };

    // =====================================================================================================================================================================
    // the chips
    // =====================================================================================================================================================================

    private void PresetsChips(T t)
    {
        List<string> chips;
        using (var probe = lib.Create()) chips = Chips(probe);
        string[] last = ["PRESETS", "PRE-NES", "PRE-GB", "PRE-SNES", "PRE-SMS", "PRE-MD"];
        t.Expect(chips.Count == 29 && chips.Skip(23).SequenceEqual(last) && chips[22] == "SMSFM" && chips[0] == "FIX" && chips.IndexOf("DMG") == 17 && chips.IndexOf("SNES") == 19, $"the Presets chips must be appended after every earlier chip: {string.Join(",", chips.Skip(15))}");
        // each is under its own console, and picking it keeps the console in step
        var home = new[] { 0, 0, 1, 2, 3, 4 };
        using var p = lib.Create();
        for (int i = 0; i < last.Length; i++)
        {
            p.SetParam(PCore, chips.IndexOf(last[i]));
            t.Expect(p.Test(14) == home[i] && p.GetParam(PConsole) == home[i] && p.Test(HkPMode) == 1, $"{last[i]}: console {p.Test(14)} (expected {home[i]}), preset mode {p.Test(HkPMode)}");
        }
        // a game cannot run on a Presets chip: asking for ROM mode puts the console's own chip back, and the chip list of the editor in ROM mode leaves them out
        p.SetParam(PCore, chips.IndexOf("PRE-SNES"));
        p.SetParam(PMode, 1);
        t.Expect(p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == "SNES" && p.Test(HkPMode) == 0 && p.GetParam(PMode) == 1, $"ROM mode on PRE-SNES: chip {p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore))}, preset mode {p.Test(HkPMode)}");
        // ... and a Presets chip asked for while a game's mode is on leaves ROM mode: the mode buttons show it
        p.SetParam(PCore, chips.IndexOf("PRE-SNES"));
        t.Expect(p.GetParam(PMode) == 0 && p.Test(HkPMode) == 1, $"PRE-SNES asked for in ROM mode: mode {p.GetParam(PMode)}, preset mode {p.Test(HkPMode)}");
        // the editor lists them in Direct mode only, under their consoles
        nint parent = Win32Host.CreateParent();
        try
        {
            p.SetParam(PCore, 0);
            p.SetParam(PConsole, 2);
            p.ShowEditor(parent);
            Win32Host.Pump(100);
            ClickControl(t, p, 4, "chip dropdown (SNES, Direct)");
            t.Expect(p.Test(32) == 2, $"the SNES chip list has {p.Test(32)} items in Direct mode, expected SNES and PRE-SNES");
            Win32Host.Click(p.EditorHandle, 20, 20);
            p.SetParam(PMode, 1);
            Win32Host.Pump(100);
            ClickControl(t, p, 4, "chip dropdown (SNES, ROM)");
            t.Expect(p.Test(32) == 1, $"the SNES chip list has {p.Test(32)} items in ROM mode, expected SNES alone");
            Win32Host.Click(p.EditorHandle, 20, 20);
            p.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); }
    }

    // =====================================================================================================================================================================
    // a preset played by FL's notes equals Runaway playing the instrument
    // =====================================================================================================================================================================

    /// <summary>Plays <paramref name="inst"/> (after <paramref name="steps"/> corruptions) from <paramref name="src"/> two ways and returns both: Runaway on a frozen copy of the game's sound memory (what the Instruments tab edits), and a preset of it loaded into
    /// an instance on the Presets chip. Same notes through the same mixer.</summary>
    private (float[] Runaway, float[] Preset, string? Problem) PlayBothWays(PresetSource src, Instrument inst, int steps)
    {
        var h = PresetFixtures.Corrupt(inst, steps, (ulong)inst.Index + 5);
        var file = PresetFile.TryWrite(PresetFixtures.Make(src, inst, steps, (ulong)inst.Index + 5), out string? why);
        if (file == null) return ([], [], "could not write the preset: " + why);
        using var run = NewOn(1, ConsoleParam(inst.Console));
        using var pre = NewOn(2, 0);
        // Runaway's side: the game's sound memory as the preset starts (no old echo, no idle voice pitch), the instrument written into it the way the tab does
        switch (src.Snapshot)
        {
            case SpcSnapshot s:
            {
                var clean = PresetChecks.WithoutLeftovers(s);
                var bytes = clean.ToBytes();
                fixed (byte* b = bytes) if (run.Test(309, bytes.Length, (nint)b) != 0) return ([], [], "the SNES snapshot was not accepted");
                run.SetParam(PInstrument, inst.Index);
                break;
            }
            case SegaSnapshot g:
            {
                var bytes = g.ToBytes();
                fixed (byte* b = bytes) if (run.Test(HkPSega, bytes.Length, (nint)b) != 0) return ([], [], "the Sega snapshot was not accepted");
                run.SetParam(PInstrument, inst.Index + 1);
                break;
            }
            case GbSnapshot gb:
            {
                var bytes = gb.ToBytes();
                fixed (byte* b = bytes) if (run.Test(HkPGb, bytes.Length, (nint)b) != 0) return ([], [], "the Game Boy snapshot was not accepted");
                run.SetParam(PChannel, inst.Index + 1);
                run.SetParam(PNoiseMode, inst.Kind == InstrumentKind.GbNoise ? inst.Get("width") : 0);
                break;
            }
            default: return ([], [], "no Runaway for " + src.Snapshot.GetType().Name);
        }
        if (run.Test(45) != 1) return ([], [], "Runaway did not come on");
        if (steps > 0 && inst.Editable)
        {
            int r = DllApply(run, h.Current, out string text);
            if (r != 0) return ([], [], "the instrument was not written into the frozen game: " + text);
        }
        if (inst.Kind == InstrumentKind.GbNoise) run.SetParam(PNoiseMode, h.Current.Get("width"));
        int pr = PLoad(pre, file, out string ptext);
        if (pr != 0) return ([], [], "the preset was not loaded: " + ptext);
        if (pre.Test(HkPMode) != 1) return ([], [], "the preset instance is not in preset mode");
        return (PlayScript(run), PlayScript(pre), null);
    }

    private void PresetsEqualRunaway(T t)
    {
        // made-up snapshots cover every kind; a few real games, when the library is on this machine, cover real ones
        var all = new List<PresetSource>(PresetFixtures.Synthetic().Where(s => s.Name != "nes" && s.Name != "snes-testsample"));
        var real = PresetFixtures.Real();
        all.AddRange(real.Where(s => s.Name is "smw" or "sonic1" or "zelda-gb" or "addams-gg"));
        if (ImodelFixtures.RealMissing.Count > 0) t.Warn(false, "not tested, game not available: " + string.Join("; ", ImodelFixtures.RealMissing));
        int compared = 0, exact = 0, silent = 0;
        var kinds = new HashSet<string>();
        foreach (var src in all)
        {
            var list = PresetFixtures.Instruments(src);
            // up to eight instruments of each game, spread over the list so that every kind is met (patches, DAC sounds, PSG, user and ROM instruments)
            var pick = list.Count <= 8 ? list.ToList() : Enumerable.Range(0, 8).Select(i => list[i * (list.Count - 1) / 7]).Distinct().ToList();
            foreach (var inst in pick)
                foreach (int steps in new[] { 0, 2 })
                {
                    if (steps > 0 && !inst.Editable) continue;
                    var (a, b, problem) = PlayBothWays(src, inst, steps);
                    string what = $"{src.Name} {inst.Kind} #{inst.Index} after {steps} steps";
                    if (problem != null) { t.Expect(false, $"{what}: {problem}"); continue; }
                    compared++;
                    bool same = SamePcm(a, b);
                    if (same) exact++;
                    t.Expect(same, $"{what}: the preset through the plugin renders other PCM than Runaway (difference rms {ImodelData.DiffRms(a, b):0.000000} over {a.Length} values)");
                    if (ImodelData.Rms(a) < 1e-6) silent++;
                    kinds.Add(inst.Kind.ToString());
                }
        }
        t.Expect(compared >= 30, $"only {compared} instruments were compared");
        t.Expect(silent * 3 < compared, $"{silent} of {compared} comparisons were of silence: they prove nothing");
        t.Note($"{exact} of {compared} instruments (kinds: {string.Join(", ", kinds.OrderBy(k => k))}) rendered bit-identical PCM through the plugin as a preset and as Runaway's frozen game, FL notes with a glide and an overlap; {silent} of them silent");
    }
}
