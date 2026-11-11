// The Nano SMS chip (NANOSMS, Master System console, appended after the harvested chips) through the real DLL: FL's notes on an instance must come out SAMPLE FOR SAMPLE as a
// NanoSmsEngine fed the same notes the way the web library's MIDI player feeds it (Nano/sms/tools/NanoSmsParity FlLike). Made from SelfTest.NanoGb.cs; the same checks.
using System.Text.Json;
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using NesEmulator.Sega;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkSmsDirect = 960, HkSmsChip = 961, HkSmsCount = 962, HkSmsHeld = 964, HkSmsSave = 965, HkSmsText = 966, HkSmsMidiCh = 967, HkSmsPreset = 968, HkSmsMutant = 979;
    private const string NanoSmsChipId = "NANOSMS";

    /// <summary>The script through the DLL on a fresh HostSim: stereo interleaved.</summary>
    private float[] SmsPlugin(int program, NanoStep[] script, int blocks, int mutant = 0, int volume = 1000)
    {
        var h = new HostSim { SampleRate = NanoRate, RootNote = 60 };
        var p = lib.Create(h);
        var outp = new float[blocks * NanoBlock * 2];
        var buf = new float[NanoBlock * 2];
        var held = new Dictionary<int, (SimVoice V, int Key)>();
        try
        {
            p.Dispatcher(Fpd.SetSampleRate, 0, NanoRate);
            SetChip(p, NanoSmsChipId);
            p.SetParam(PInstrument, program);
            p.SetParam(PVolume, volume);
            if (mutant != 0) p.Test(HkSmsMutant, 0, mutant);
            int next = 0;
            fixed (float* bp = buf)
                for (int b = 0; b < blocks; b++)
                {
                    while (next < script.Length && script[next].Block == b)
                    {
                        var s = script[next++];
                        if (s.Kind == 0) { var v = h.FlNoteOn(s.Key, s.Vel, 0); v.Handle = p.TriggerVoice(v.Params, v.Tag); held[s.Id] = (v, s.Key); }
                        else if (s.Kind == 1) { var (v, k) = held[s.Id]; v.Params->FinalLevels.Pitch = (float)((k - h.RootNote) * 100 + s.Cents); }
                        else { p.VoiceRelease(held[s.Id].V.Handle); }
                    }
                    p.NewTick();
                    p.GenRender(bp, NanoBlock);
                    Array.Copy(buf, 0, outp, b * NanoBlock * 2, NanoBlock * 2);
                }
        }
        finally { if (mutant != 0) p.Test(HkSmsMutant, 0, 0); p.Dispose(); h.Dispose(); }
        return outp;
    }

    /// <summary>The same script on a NanoSmsEngine offline, the web player's way: note, velocity / 127, preset = program n, the bend as cents above the note.</summary>
    private static float[] SmsReference(int program, NanoStep[] script, int blocks)
    {
        var e = new NanoSmsEngine(NanoRate);
        var preset = NanoSmsPresets.Program(program);
        var outp = new float[blocks * NanoBlock * 2];
        float[] l = new float[NanoBlock], r = new float[NanoBlock];
        int next = 0;
        for (int b = 0; b < blocks; b++)
        {
            long f = e.Frame;
            while (next < script.Length && script[next].Block == b)
            {
                var s = script[next++];
                if (s.Kind == 0) e.NoteOn(f, s.Id, s.Key, Math.Min(1, s.Vel / 127.0), preset);
                else if (s.Kind == 1) e.BendVoice(f, s.Id, s.Cents);
                else e.NoteOff(f, s.Id);
            }
            e.Render(l, r, 0, NanoBlock);
            for (int i = 0; i < NanoBlock; i++) { outp[(b * NanoBlock + i) * 2] = l[i]; outp[(b * NanoBlock + i) * 2 + 1] = r[i]; }
        }
        return outp;
    }

    private void NanoSms(T t)
    {
        // ---- the chip list: appended after the harvested chips (append-only), on the Master System console ----
        List<string> chips;
        using (var probe = lib.Create()) chips = Chips(probe);
        int at = chips.IndexOf(NanoSmsChipId);
        t.Expect(at == chips.Count - 1 && at > 31, $"the {NanoSmsChipId} chip is index {at} of {chips.Count} (expected the last: appended after the harvested chips)");
        t.Expect(chips.IndexOf("FIX") == 0 && chips.IndexOf("SMS") == 20 && chips.IndexOf("WMS") == 29 && chips.IndexOf("NANO" + "GB") == 30, "an existing chip moved (the chip index is part of saved projects: append only)");
        using (var p = lib.Create())
        {
            t.Expect(p.Test(HkSmsChip) == at, $"the plugin's own index of the Nano SMS chip is {p.Test(HkSmsChip)}");
            t.Expect(p.Test(HkSmsCount) == 24 && NanoSmsPresets.Count == 24, $"the Nano SMS chip has {p.Test(HkSmsCount)} presets (expected 24)");
            SetChip(p, NanoSmsChipId);
            t.Expect(p.GetParam(PConsole) == 3 && p.Test(HkSmsDirect) == 1, $"picking NANOSMS should put the emulator on the Master System console in Nano SMS Direct mode (console {p.GetParam(PConsole)}, mode {p.Test(HkSmsDirect)})");
            p.SetParam(PConsole, 0); p.SetParam(PConsole, 3);
            t.Expect(p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == "SMS", "the Master System console still starts with its own chip, SMS");
            SetChip(p, NanoSmsChipId);
            p.SetParam(PMode, 1);
            t.Expect(p.Test(HkSmsDirect) == 0 && p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == NanoSmsChipId, "ROM mode on NANOSMS is not Nano SMS Direct mode (a game runs on the Master System's own sound unit)");
            p.SetParam(PMode, 0);
            p.SetParam(PInstrument, 30);
            t.Expect(p.Test(HkSmsPreset) == 6, $"program 30 should wrap to preset 6 as the web player's program % 24 does (got {p.Test(HkSmsPreset)})");
            // the Instrument picker lists the 27 presets
            nint parent = Win32Host.CreateParent();
            try
            {
                p.ShowEditor(parent);
                Win32Host.Pump(100);
                ClickControl(t, p, 90, "Instrument dropdown (Nano SMS)");
                t.Expect(p.Test(31) == 90 && p.Test(32) == 24, $"the Instrument list on NANOSMS has {p.Test(32)} rows (open: {p.Test(31)}), expected the 24 presets");
                Win32Host.Click(p.EditorHandle, 20, 20);
                t.Expect(p.Test(30, 61) != 0, "the Export sidecar button is not drawn on the Nano SMS chip");
                p.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }
        }
        byte[] blob;
        using (var a = lib.Create()) { SetChip(a, NanoSmsChipId); a.SetParam(PInstrument, 5); blob = a.SaveState(); }
        using (var b = lib.Create())
        {
            b.LoadState(blob);
            t.Expect(b.GetParam(PCore) == at && b.GetParam(PConsole) == 3 && b.GetParam(PInstrument) == 5 && b.Test(HkSmsDirect) == 1, $"a project saved on NANOSMS came back as chip {b.GetParam(PCore)}, console {b.GetParam(PConsole)}, instrument {b.GetParam(PInstrument)}");
        }

        // ---- sample-exact parity with the engine fed the same notes (several presets: pulse, wave, noise kinds) ----
        var programs = new List<int>();
        foreach (int kind in new[] { 0, 1 }) { int i = Enumerable.Range(0, NanoSmsPresets.Count).FirstOrDefault(k => NanoSmsPresets.All[k].Kind == kind, -1); if (i >= 0) programs.Add(i); }
        if (!programs.Contains(0)) programs.Insert(0, 0);
        var rows = new List<string>();
        foreach (int prog in programs)
        {
            var got = SmsPlugin(prog, NanoScript, NanoBlocks);
            var want = SmsReference(prog, NanoScript, NanoBlocks);
            var c = NanoCompare(got, want);
            t.Expect(c.Rms > 1e-4, $"preset {prog} ({NanoSmsPresets.All[prog].Name}): the reference is silent (RMS {c.Rms:0.000000}): the comparison would prove nothing");
            t.Expect(c.Diffs == 0, $"preset {prog} ({NanoSmsPresets.All[prog].Name}): {c.Diffs} of {got.Length} samples differ from the engine fed the same notes (first at frame {c.First}, worst {c.MaxAbs:0.000000})");
            rows.Add($"{prog} {NanoSmsPresets.All[prog].Name} rms {c.Rms:0.0000} diffs {c.Diffs}");
        }
        t.Note($"Nano SMS parity (4 notes, velocities 17/64/100/127, bends +37.5, +200, -150 cents, {NanoBlocks * NanoBlock} frames at {NanoRate} Hz): {string.Join("; ", rows)}");
        // the controls: the deliberate bugs must break the parity
        foreach (var (mutant, what) in new[] { (1, "the velocity as FL's float (velocity / 128)"), (2, "bends ignored") })
        {
            int caught = 0;
            foreach (int prog in programs) if (NanoCompare(SmsPlugin(prog, NanoScript, NanoBlocks, mutant), SmsReference(prog, NanoScript, NanoBlocks)).Diffs > 0) caught++;
            t.Expect(caught > 0, $"deliberate bug {mutant} ({what}) not caught: every preset still matched the reference sample for sample");
        }
        // Volume: half the parameter is half the output, exactly (the engine's output x Volume / 1000)
        {
            var full = SmsPlugin(programs[0], NanoScript, 120);
            var half = SmsPlugin(programs[0], NanoScript, 120, volume: 500);
            bool ok = true; for (int i = 0; i < full.Length; i++) if (half[i] != full[i] * 0.5f) { ok = false; break; }
            t.Expect(ok, "Volume 50% is not the output x 0.5");
        }

        // ---- bends move the pitch: a held square-ish pulse note, then +200 cents ----
        {
            int sq = NanoSmsPresets.IndexOf("square");
            if (sq < 0) sq = programs[0];
            NanoStep[] bendScript = [new(0, 0, 1, 57, 100), new(200, 1, 1, 57, 0, 200), new(400, 2, 1, 0, 0)];
            var a = SmsPlugin(sq, bendScript, 420);
            var mono = Audio.Mono(a, 2);
            double f1 = Audio.Frequency(mono, NanoRate, 60 * NanoBlock, 190 * NanoBlock), f2 = Audio.Frequency(mono, NanoRate, 260 * NanoBlock, 390 * NanoBlock);
            double moved = Audio.Cents(f2, f1);
            t.Expect(!double.IsNaN(moved) && Math.Abs(moved - 200) < 15, $"a +200 cent bend moved the pitch by {moved:0.0} cents ({f1:0.00} Hz -> {f2:0.00} Hz, preset {NanoSmsPresets.All[sq].Name})");
            t.Expect(Math.Abs(Audio.Cents(f1, 220.0)) < 15, $"A3 on {NanoSmsPresets.All[sq].Name} measured {f1:0.00} Hz (expected 220)");
        }

        // ---- no allocation on the audio thread: 4 instances, notes coming and going and bending, a few seconds ----
        {
            var h = new HostSim { SampleRate = NanoRate, RootNote = 60 };
            var ps = new List<PluginInstance>();
            try
            {
                for (int i = 0; i < 4; i++) { var q = lib.Create(h); q.Dispatcher(Fpd.SetSampleRate, 0, NanoRate); SetChip(q, NanoSmsChipId); q.SetParam(PInstrument, i * 5); ps.Add(q); }
                var buf = new float[NanoBlock * 2];
                var live = new SimVoice?[4];
                int blocks = NanoRate * 4 / NanoBlock;
                long bytes = 0, before = 0;
                int maxHeld = 0;
                var where = new List<string>(8);
                fixed (float* bp = buf)
                {
                    for (int b = 0; b < blocks; b++)
                    {
                        if (b == 300) before = AllocStart(ps[0]);
                        for (int i = 0; i < 4; i++)
                        {
                            var q = ps[i];
                            if ((b + i * 37) % 120 == 0)
                            {
                                var v = h.FlNoteOn(48 + (b / 120 + i * 3) % 30, 40 + i * 20, 0);
                                long t1 = b >= 300 ? ps[0].Test(HkRAlloc) : 0;
                                if (live[i] is { } old) q.VoiceRelease(old.Handle);
                                v.Handle = q.TriggerVoice(v.Params, v.Tag); live[i] = v;
                                if (b >= 300 && where.Count < 6 && ps[0].Test(HkRAlloc) - t1 is long d1 && d1 > 0) where.Add($"block {b} instance {i}: {d1} B in the release + TriggerVoice");
                            }
                            else if (live[i] is { } lv && b % 7 == 0) lv.Params->FinalLevels.Pitch += 3;
                            long t0 = b >= 300 ? ps[0].Test(HkRAlloc) : 0;
                            q.NewTick();
                            q.GenRender(bp, NanoBlock);
                            if (b >= 300 && where.Count < 6 && ps[0].Test(HkRAlloc) - t0 is long d && d > 0) where.Add($"block {b} instance {i}: {d} B{((b + i * 37) % 120 == 0 ? " (a new note)" : "")}");
                        }
                        if (b == 1000) maxHeld = (int)ps.Sum(q => q.Test(HkSmsHeld));
                    }
                    bytes = ps[0].Test(HkRAlloc) - before;
                }
                t.Expect(bytes == 0, $"4 Nano SMS instances allocated {bytes} bytes on the audio thread over {(blocks - 300) * NanoBlock / (double)NanoRate:0.0} s (notes, bends, releases){(where.Count > 0 ? ": " + string.Join(", ", where) : "")}");
                t.Expect(maxHeld >= 4, $"the 4 instances held {maxHeld} engine notes mid-run (expected one each at least)");
            }
            finally { foreach (var q in ps) q.Dispose(); h.Dispose(); }
        }

        // ---- the sidecar: every Nano SMS instance, in the order they were made, MIDI channel 1, 2, 3 ...; the format normalizeSidecar accepts ----
        {
            var made = new List<PluginInstance>();
            try
            {
                for (int i = 0; i < 3; i++) { var q = lib.Create(); SetChip(q, NanoSmsChipId); made.Add(q); }
                made[0].SetParam(PInstrument, 0);
                made[1].SetParam(PInstrument, 7); made[1].SetParam(PVolume, 450); made[1].SetParam(PPan, -80);
                made[2].SetParam(PInstrument, 30); made[2].SetParam(PPan, 70);
                string json = SmsText(made[0]);
                var problems = SmsSidecarProblems(json);
                t.Expect(problems.Count == 0, $"the sidecar is not what normalizeSidecar accepts: {string.Join("; ", problems)}\n{json}");
                if (problems.Count > 0) return;
                using var doc = JsonDocument.Parse(json);
                var chs = doc.RootElement.GetProperty("channels");
                int n = chs.EnumerateObject().Count();
                t.Expect(n == 3, $"the sidecar has {n} channels (expected the 3 Nano SMS instances)");
                if (n == 3)
                {
                    var c1 = chs.GetProperty("1"); var c2 = chs.GetProperty("2"); var c3 = chs.GetProperty("3");
                    t.Expect(c1.GetProperty("preset").GetString() == NanoSmsPresets.All[0].Name && c1.GetProperty("volume").GetDouble() == 1.0 && !c1.TryGetProperty("pan", out _),
                        $"channel 1 (the first instance, defaults): {c1}");
                    t.Expect(c2.GetProperty("preset").GetString() == NanoSmsPresets.All[7].Name && c2.GetProperty("volume").GetDouble() == 0.45 && c2.GetProperty("pan").GetString() == "L", $"channel 2 (preset 7, 45%, pan 80% L): {c2}");
                    t.Expect(c3.GetProperty("preset").GetString() == NanoSmsPresets.All[6].Name && c3.GetProperty("pan").GetString() == "R", $"channel 3 (program 30 = preset 6, pan 70% R): {c3}");
                }
                t.Expect(made[0].Test(HkSmsMidiCh) == 1 && made[1].Test(HkSmsMidiCh) == 2 && made[2].Test(HkSmsMidiCh) == 3, $"the instances' MIDI channels are {made[0].Test(HkSmsMidiCh)}, {made[1].Test(HkSmsMidiCh)}, {made[2].Test(HkSmsMidiCh)} (expected 1, 2, 3: the order they were made)");
                // the file, through the hook and through the editor's button
                string dir = Path.Combine(Path.GetTempPath(), "bn2-nanosms-" + Environment.ProcessId);
                Directory.CreateDirectory(dir);
                string f1 = Path.Combine(dir, "hook.json"), f2 = Path.Combine(dir, "button.json");
                nint s1 = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(f1);
                try { t.Expect(made[1].Test(HkSmsSave, 0, s1) == 1 && File.Exists(f1) && File.ReadAllText(f1) == json, "hook 965 did not write the same sidecar to the file"); }
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
                    t.Expect(File.Exists(f2) && File.ReadAllText(f2) == json, "the editor's Export sidecar button did not write the sidecar");
                    made[2].HideEditor();
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(s2); Win32Host.DestroyParent(parent); }
                try { Directory.Delete(dir, true); } catch { }
                // the checker's control: a sidecar with an unknown setting, a channel 17, a volume 1.5 must be refused by it
                var bad = SmsSidecarProblems("{\"format\":\"nano-sms-sidecar\",\"version\":1,\"channels\":{\"17\":{\"preset\":\"nope\",\"volume\":1.5,\"colour\":3}},\"presets\":{}}");
                t.Expect(bad.Count >= 4, $"the sidecar checker let a bad sidecar through ({bad.Count} problems found: {string.Join("; ", bad)})");
            }
            finally { foreach (var q in made) q.Dispose(); }
        }
    }

    private static string SmsText(PluginInstance p)
    {
        int len = (int)p.Test(HkSmsText, 0, 0);
        var buf = new char[Math.Max(1, len)];
        fixed (char* b = buf) p.Test(HkSmsText, len, (nint)b);
        return new string(buf, 0, len);
    }

    /// <summary>Nano/sms/src/song.js normalizeSidecar's rules, by hand: format, version 1, channel keys integers 1..16, settings only preset / volume / pan / name, a known preset, volume 0..1, pan -1 / 0 / 1 or L / C / R.</summary>
    private static List<string> SmsSidecarProblems(string json)
    {
        var bad = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException e) { bad.Add("not JSON: " + e.Message); return bad; }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("format", out var f) || f.ValueKind != JsonValueKind.String || f.GetString() != "nano-sms-sidecar") bad.Add("format is not nano-sms-sidecar");
            if (!r.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetDouble() != 1) bad.Add("version is not 1");
            if (r.TryGetProperty("presets", out var pr) && pr.ValueKind != JsonValueKind.Object) bad.Add("presets is not an object");
            if (r.TryGetProperty("channels", out var chs))
                foreach (var c in chs.EnumerateObject())
                {
                    if (!int.TryParse(c.Name, out int n) || n < 1 || n > 16 || n.ToString() != c.Name) bad.Add($"channel '{c.Name}' is not 1..16");
                    foreach (var s in c.Value.EnumerateObject())
                        switch (s.Name)
                        {
                            case "preset": if (s.Value.ValueKind != JsonValueKind.String || NanoSmsPresets.IndexOf(s.Value.GetString()!) < 0) bad.Add($"channel {c.Name}: unknown preset {s.Value}"); break;
                            case "volume": if (s.Value.ValueKind != JsonValueKind.Number || s.Value.GetDouble() is < 0 or > 1) bad.Add($"channel {c.Name}: volume {s.Value} is not 0..1"); break;
                            case "pan": if (!(s.Value.ValueKind == JsonValueKind.String ? s.Value.GetString() is "L" or "C" or "R" : s.Value.ValueKind == JsonValueKind.Number && s.Value.GetDouble() is -1 or 0 or 1)) bad.Add($"channel {c.Name}: pan {s.Value}"); break;
                            case "name": break;
                            default: bad.Add($"channel {c.Name}: unknown setting '{s.Name}'"); break;
                        }
                }
        }
        return bad;
    }
}
