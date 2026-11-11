// The later Nano chips (NANOMD, then NANOSNES, NANODOS: Bn2Plugin.NanoMore.cs, appended after the x86 PC chips) through the real DLL, one check per chip: FL's notes on an
// instance must come out SAMPLE FOR SAMPLE as the chip's C# engine fed the same notes the way the web library's MIDI player feeds it (the tools' FlLike), the chip index
// pinned (append-only), the Instrument picker, a saved project, two deliberate bugs, Volume, no allocation with 4 instances, the sidecar. Made from SelfTest.NanoSms.cs.
using System.Text.Json;
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using NesEmulator;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkMoreDirect = 1150, HkMoreChip = 1151, HkMoreCount = 1152, HkMoreHeld = 1154, HkMoreSave = 1155, HkMoreText = 1156, HkMoreMidiCh = 1157, HkMorePreset = 1158, HkMoreMutant = 1179;

    /// <summary>What the check knows of each later Nano chip (kept here, not read from the DLL, so a moved index or a lost preset is caught).</summary>
    private sealed record MoreSpec(int Kind, string Chip, int Index, int Console, int Presets, Func<int, INanoEngine> Engine, Func<int, string> PresetName, Func<int, int> PresetKind, string Format, string ToneName);

    private static readonly MoreSpec[] MoreSpecs =
    [
        new(1, "NANOMD", 88, 4, NesEmulator.Sega.NanoGenesisPresets.Count, r => new NesEmulator.Sega.NanoGenesisEngine(r), i => NesEmulator.Sega.NanoGenesisPresets.All[i].Name, i => NesEmulator.Sega.NanoGenesisPresets.All[i].Kind, "nano-genesis-sidecar", "sine"),
        new(2, "NANOSNES", 89, 2, NesEmulator.Snes.NanoSnesPresets.Count, r => new NesEmulator.Snes.NanoSnesEngine(r), i => NesEmulator.Snes.NanoSnesPresets.All[i].Name, i => NesEmulator.Snes.NanoSnesPresets.All[i].Kind, "nano-snes-sidecar", "sine"),
        new(3, "NANODOS", 90, 5, NesEmulator.X86.NanoDosPresets.Count, r => new NesEmulator.X86.NanoDosEngine(r), i => NesEmulator.X86.NanoDosPresets.All[i].Name, i => NesEmulator.X86.NanoDosPresets.All[i].Kind, "nano-dos-sidecar", "svtriangle"),
    ];

    private float[] MorePlugin(MoreSpec s, int program, NanoStep[] script, int blocks, int mutant = 0, int volume = 1000)
    {
        var h = new HostSim { SampleRate = NanoRate, RootNote = 60 };
        var p = lib.Create(h);
        var outp = new float[blocks * NanoBlock * 2];
        var buf = new float[NanoBlock * 2];
        var held = new Dictionary<int, (SimVoice V, int Key)>();
        try
        {
            p.Dispatcher(Fpd.SetSampleRate, 0, NanoRate);
            SetChip(p, s.Chip);
            p.SetParam(PInstrument, program);
            p.SetParam(PVolume, volume);
            if (mutant != 0) p.Test(HkMoreMutant, 0, mutant);
            int next = 0;
            fixed (float* bp = buf)
                for (int b = 0; b < blocks; b++)
                {
                    while (next < script.Length && script[next].Block == b)
                    {
                        var st = script[next++];
                        if (st.Kind == 0) { var v = h.FlNoteOn(st.Key, st.Vel, 0); v.Handle = p.TriggerVoice(v.Params, v.Tag); held[st.Id] = (v, st.Key); }
                        else if (st.Kind == 1) { var (v, k) = held[st.Id]; v.Params->FinalLevels.Pitch = (float)((k - h.RootNote) * 100 + st.Cents); }
                        else { p.VoiceRelease(held[st.Id].V.Handle); }
                    }
                    p.NewTick();
                    p.GenRender(bp, NanoBlock);
                    Array.Copy(buf, 0, outp, b * NanoBlock * 2, NanoBlock * 2);
                }
        }
        finally { if (mutant != 0) p.Test(HkMoreMutant, 0, 0); p.Dispose(); h.Dispose(); }
        return outp;
    }

    private static float[] MoreReference(MoreSpec s, int program, NanoStep[] script, int blocks)
    {
        var e = s.Engine(NanoRate);
        var outp = new float[blocks * NanoBlock * 2];
        float[] l = new float[NanoBlock], r = new float[NanoBlock];
        int next = 0;
        for (int b = 0; b < blocks; b++)
        {
            long f = e.Frame;
            while (next < script.Length && script[next].Block == b)
            {
                var st = script[next++];
                if (st.Kind == 0) e.NoteOnProgram(f, st.Id, st.Key, Math.Min(1, st.Vel / 127.0), program, 0);
                else if (st.Kind == 1) e.BendVoice(f, st.Id, st.Cents);
                else e.NoteOff(f, st.Id);
            }
            e.Render(l, r, 0, NanoBlock);
            for (int i = 0; i < NanoBlock; i++) { outp[(b * NanoBlock + i) * 2] = l[i]; outp[(b * NanoBlock + i) * 2 + 1] = r[i]; }
        }
        return outp;
    }

    private void NanoMoreCheck(T t, MoreSpec s)
    {
        List<string> chips;
        using (var probe = lib.Create()) chips = Chips(probe);
        int at = chips.IndexOf(s.Chip);
        t.Expect(at == s.Index, $"the {s.Chip} chip is index {at} of {chips.Count} (expected {s.Index}: appended after the x86 PC chips, in order)");
        t.Expect(chips.IndexOf("FIX") == 0 && chips.IndexOf("WMS") == 29 && chips.IndexOf("NANO" + "GB") == 30 && chips.IndexOf("NANOSMS") == 76 && chips.IndexOf("PC_COVOX") == 87, "an existing chip moved (the chip index is part of saved projects: append only)");
        using (var p = lib.Create())
        {
            t.Expect(p.Test(HkMoreChip, s.Kind) == at, $"the plugin's own index of {s.Chip} is {p.Test(HkMoreChip, s.Kind)}");
            t.Expect(p.Test(HkMoreCount, s.Kind) == s.Presets, $"{s.Chip} has {p.Test(HkMoreCount, s.Kind)} presets (expected {s.Presets})");
            SetChip(p, s.Chip);
            t.Expect(p.GetParam(PConsole) == s.Console && p.Test(HkMoreDirect) == s.Kind, $"picking {s.Chip} should put the emulator on console {s.Console} in its Direct mode (console {p.GetParam(PConsole)}, kind {p.Test(HkMoreDirect)})");
            if (s.Console == 5) { p.SetParam(PMode, 1); t.Expect(p.GetParam(PMode) == 0 && p.Test(HkMoreDirect) == s.Kind, $"{s.Chip}: ROM mode must be refused on the PC console (mode {p.GetParam(PMode)})"); }
            p.SetParam(PInstrument, s.Presets + 3);
            t.Expect(p.Test(HkMorePreset) == 3, $"program {s.Presets + 3} should wrap to preset 3 as the web player's program % count does (got {p.Test(HkMorePreset)})");
            nint parent = Win32Host.CreateParent();
            try
            {
                p.ShowEditor(parent);
                Win32Host.Pump(100);
                ClickControl(t, p, 90, $"Instrument dropdown ({s.Chip})");
                t.Expect(p.Test(31) == 90 && p.Test(32) == s.Presets, $"the Instrument list on {s.Chip} has {p.Test(32)} rows (open: {p.Test(31)}), expected the {s.Presets} presets");
                Win32Host.Click(p.EditorHandle, 20, 20);
                t.Expect(p.Test(30, 61) != 0, $"the Export sidecar button is not drawn on {s.Chip}");
                p.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }
        }
        byte[] blob;
        using (var a = lib.Create()) { SetChip(a, s.Chip); a.SetParam(PInstrument, 5); blob = a.SaveState(); }
        using (var b = lib.Create())
        {
            b.LoadState(blob);
            t.Expect(b.GetParam(PCore) == at && b.GetParam(PConsole) == s.Console && b.GetParam(PInstrument) == 5 && b.Test(HkMoreDirect) == s.Kind, $"a project saved on {s.Chip} came back as chip {b.GetParam(PCore)}, console {b.GetParam(PConsole)}, instrument {b.GetParam(PInstrument)}");
        }

        // ---- sample-exact parity with the engine fed the same notes: the first preset of every kind ----
        var programs = new List<int>();
        for (int kind = 0; kind < 4; kind++) for (int i = 0; i < s.Presets; i++) if (s.PresetKind(i) == kind) { programs.Add(i); break; }
        var rows = new List<string>();
        foreach (int prog in programs)
        {
            var got = MorePlugin(s, prog, NanoScript, NanoBlocks);
            var want = MoreReference(s, prog, NanoScript, NanoBlocks);
            var c = NanoCompare(got, want);
            t.Expect(c.Rms > 1e-4, $"preset {prog} ({s.PresetName(prog)}): the reference is silent (RMS {c.Rms:0.000000}): the comparison would prove nothing");
            t.Expect(c.Diffs == 0, $"preset {prog} ({s.PresetName(prog)}): {c.Diffs} of {got.Length} samples differ from the engine fed the same notes (first at frame {c.First}, worst {c.MaxAbs:0.000000})");
            rows.Add($"{prog} {s.PresetName(prog)} rms {c.Rms:0.0000} diffs {c.Diffs}");
        }
        t.Note($"{s.Chip} parity ({NanoBlocks * NanoBlock} frames at {NanoRate} Hz): {string.Join("; ", rows)}");
        foreach (var (mutant, what) in new[] { (1, "the velocity as FL's float (velocity / 128)"), (2, "bends ignored") })
        {
            if (mutant == 1 && s.Chip == "NANODOS") continue;   // (the OPL2's level is 6-bit total level, 0.75 dB a step: velocity / 128 instead of / 127, 0.07 dB, lands on the same steps; the speaker has no level)
            int caught = 0;
            foreach (int prog in programs) if (NanoCompare(MorePlugin(s, prog, NanoScript, NanoBlocks, mutant), MoreReference(s, prog, NanoScript, NanoBlocks)).Diffs > 0) caught++;
            t.Expect(caught > 0, $"{s.Chip}: deliberate bug {mutant} ({what}) not caught");
        }
        {
            var full = MorePlugin(s, programs[0], NanoScript, 120);
            var half = MorePlugin(s, programs[0], NanoScript, 120, volume: 500);
            bool ok = true; for (int i = 0; i < full.Length; i++) if (half[i] != full[i] * 0.5f) { ok = false; break; }
            t.Expect(ok, $"{s.Chip}: Volume 50% is not the output x 0.5");
        }
        // ---- a +200 cent bend moves the pitch (the tone preset) ----
        {
            int sq = Enumerable.Range(0, s.Presets).FirstOrDefault(i => s.PresetName(i) == s.ToneName, programs[0]);
            NanoStep[] bendScript = [new(0, 0, 1, 57, 100), new(200, 1, 1, 57, 0, 200), new(400, 2, 1, 0, 0)];
            var mono = Audio.Mono(MorePlugin(s, sq, bendScript, 420), 2);
            double f1 = Audio.Frequency(mono, NanoRate, 60 * NanoBlock, 190 * NanoBlock), f2 = Audio.Frequency(mono, NanoRate, 260 * NanoBlock, 390 * NanoBlock);
            double moved = Audio.Cents(f2, f1);
            t.Expect(!double.IsNaN(moved) && Math.Abs(moved - 200) < 15, $"{s.Chip}: a +200 cent bend moved the pitch by {moved:0.0} cents ({f1:0.00} -> {f2:0.00} Hz, preset {s.PresetName(sq)})");
            t.Expect(Math.Abs(Audio.Cents(f1, 220.0)) < 15, $"{s.Chip}: A3 on {s.PresetName(sq)} measured {f1:0.00} Hz (expected 220)");
        }
        // ---- no allocation on the audio thread: 4 instances, notes coming and going and bending ----
        {
            var h = new HostSim { SampleRate = NanoRate, RootNote = 60 };
            var ps = new List<PluginInstance>();
            try
            {
                for (int i = 0; i < 4; i++) { var q = lib.Create(h); q.Dispatcher(Fpd.SetSampleRate, 0, NanoRate); SetChip(q, s.Chip); q.SetParam(PInstrument, i * 5); ps.Add(q); }
                var buf = new float[NanoBlock * 2];
                var live = new SimVoice?[4];
                int blocks = NanoRate * 4 / NanoBlock;
                long before = 0; int maxHeld = 0;
                fixed (float* bp = buf)
                    for (int b = 0; b < blocks; b++)
                    {
                        if (b == 300) before = AllocStart(ps[0]);
                        for (int i = 0; i < 4; i++)
                        {
                            var q = ps[i];
                            if ((b + i * 37) % 120 == 0)
                            {
                                var v = h.FlNoteOn(48 + (b / 120 + i * 3) % 30, 40 + i * 20, 0);
                                if (live[i] is { } old) q.VoiceRelease(old.Handle);
                                v.Handle = q.TriggerVoice(v.Params, v.Tag); live[i] = v;
                            }
                            else if (live[i] is { } lv && b % 7 == 0) lv.Params->FinalLevels.Pitch += 3;
                            q.NewTick();
                            q.GenRender(bp, NanoBlock);
                        }
                        if (b == 1000) for (int i = 0; i < 4; i++) maxHeld += (int)ps[i].Test(HkMoreHeld);
                    }
                long bytes = ps[0].Test(HkRAlloc) - before;
                t.Expect(bytes == 0, $"4 {s.Chip} instances allocated {bytes} bytes on the audio thread (notes, bends, releases)");
                t.Expect(maxHeld >= 4, $"the 4 {s.Chip} instances held {maxHeld} engine notes mid-run (expected one each at least)");
            }
            finally { foreach (var q in ps) q.Dispose(); h.Dispose(); }
        }
        // ---- the sidecar ----
        {
            var made = new List<PluginInstance>();
            try
            {
                for (int i = 0; i < 3; i++) { var q = lib.Create(); SetChip(q, s.Chip); made.Add(q); }
                made[0].SetParam(PInstrument, 0);
                made[1].SetParam(PInstrument, 7); made[1].SetParam(PVolume, 450); made[1].SetParam(PPan, -80);
                made[2].SetParam(PInstrument, s.Presets + 6); made[2].SetParam(PPan, 70);
                int len = (int)made[0].Test(HkMoreText, 0, 0);
                var cbuf = new char[Math.Max(1, len)];
                fixed (char* cb = cbuf) made[0].Test(HkMoreText, len, (nint)cb);
                string json = new string(cbuf, 0, len);
                using var doc = JsonDocument.Parse(json);
                t.Expect(doc.RootElement.GetProperty("format").GetString() == s.Format && doc.RootElement.GetProperty("version").GetInt32() == 1, $"{s.Chip}: sidecar format: {json}");
                var chs = doc.RootElement.GetProperty("channels");
                t.Expect(chs.EnumerateObject().Count() == 3, $"{s.Chip}: the sidecar has {chs.EnumerateObject().Count()} channels (expected 3)");
                if (chs.EnumerateObject().Count() == 3)
                {
                    var c1 = chs.GetProperty("1"); var c2 = chs.GetProperty("2"); var c3 = chs.GetProperty("3");
                    t.Expect(c1.GetProperty("preset").GetString() == s.PresetName(0) && c1.GetProperty("volume").GetDouble() == 1.0 && !c1.TryGetProperty("pan", out _), $"{s.Chip} channel 1: {c1}");
                    t.Expect(c2.GetProperty("preset").GetString() == s.PresetName(7) && c2.GetProperty("volume").GetDouble() == 0.45 && c2.GetProperty("pan").GetString() == "L", $"{s.Chip} channel 2: {c2}");
                    t.Expect(c3.GetProperty("preset").GetString() == s.PresetName(6) && c3.GetProperty("pan").GetString() == "R", $"{s.Chip} channel 3: {c3}");
                }
                t.Expect(made[0].Test(HkMoreMidiCh) == 1 && made[1].Test(HkMoreMidiCh) == 2 && made[2].Test(HkMoreMidiCh) == 3, $"{s.Chip}: MIDI channels {made[0].Test(HkMoreMidiCh)}, {made[1].Test(HkMoreMidiCh)}, {made[2].Test(HkMoreMidiCh)} (expected 1, 2, 3)");
                string dir = Path.Combine(Path.GetTempPath(), "bn2-nanomore-" + Environment.ProcessId);
                Directory.CreateDirectory(dir);
                string f1 = Path.Combine(dir, "hook.json"), f2 = Path.Combine(dir, "button.json");
                nint s1 = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(f1);
                try { t.Expect(made[1].Test(HkMoreSave, 0, s1) == 1 && File.Exists(f1) && File.ReadAllText(f1) == json, $"{s.Chip}: hook 1155 did not write the same sidecar"); }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(s1); }
                nint parent = Win32Host.CreateParent();
                nint s2 = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(f2);
                try
                {
                    made[2].ShowEditor(parent);
                    Win32Host.Pump(100);
                    made[2].Test(880, 0, s2);
                    ClickControl(t, made[2], 61, "Export sidecar");
                    Win32Host.Pump(50);
                    t.Expect(File.Exists(f2) && File.ReadAllText(f2) == json, $"{s.Chip}: the editor's Export sidecar button did not write the sidecar");
                    made[2].HideEditor();
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(s2); Win32Host.DestroyParent(parent); }
                try { Directory.Delete(dir, true); } catch { }
            }
            finally { foreach (var q in made) q.Dispose(); }
        }
    }

    private void NanoMore(T t) { foreach (var s in MoreSpecs) NanoMoreCheck(t, s); }
}
