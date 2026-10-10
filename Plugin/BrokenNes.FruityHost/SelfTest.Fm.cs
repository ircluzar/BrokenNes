// Round 3 (track C3b): the plugin's Master System FM chip (APU_SMSFM: the YM2413 and the PSG), measured like the Genesis chip's `md` test.
using System.Diagnostics;
using BrokenNes.Fruity;
using BrokenNes.SynthHost;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    // ------------------------------------------------------------------------------------------------
    // The Master System FM chip: Direct mode through APU_SMSFM (NES registers to the YM2413 and the PSG), console 3 (SMS). The Instrument parameter picks the instrument.

    private void SmsFm(T t)
    {
        const string Chip = "SMSFM";
        const int Violin = 0, Organ = 7, Sine = 15, Rectified = 16, FedBack = 17, PsgSquare = 21, Count = 22;
        // ---- the chip list: appended last, so every saved project keeps its chip ----
        List<string> chips;
        using (var probe = lib.Create()) chips = Chips(probe);
        t.Expect(chips.Count == 23 && chips.IndexOf(Chip) == 22 && chips.IndexOf("SMS") == 20 && chips.IndexOf("MD") == 21, $"the {Chip} chip is index {chips.IndexOf(Chip)} of {chips.Count} (expected the last, 22)");
        byte[] blob;
        using (var a = lib.Create()) { a.SetParam(PCore, chips.IndexOf(Chip)); a.SetParam(PInstrument, 9); blob = a.SaveState(); }
        using (var b = lib.Create())
        {
            b.LoadState(blob);
            t.Expect(b.GetParam(PCore) == 22 && b.GetParam(PConsole) == 3 && b.GetParam(PInstrument) == 9 && b.GetName(Fpn.ParamValue, PCore, b.GetParam(PCore)) == Chip, $"a project saved on the {Chip} chip came back as chip {b.GetParam(PCore)}, console {b.GetParam(PConsole)}, instrument {b.GetParam(PInstrument)}");
            t.Expect(b.SaveState().SequenceEqual(blob), "save -> load -> save is not stable on the Master System FM chip");
        }
        // picking the chip puts the emulator on the Master System console; the console button lists both of its chips
        {
            using var p = lib.Create();
            p.SetParam(PCore, chips.IndexOf(Chip));
            t.Expect(p.GetParam(PConsole) == 3, $"picking {Chip} left the console at {p.GetParam(PConsole)} (expected the Master System, 3)");
            p.SetParam(PConsole, 4); p.SetParam(PConsole, 3);
            t.Expect(p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == "SMS", "switching to the Master System console starts on its first chip, the PSG");
        }

        // ---- pitch: the designed sine carries every channel; the 9-bit F-number is chosen by nearest (within 3.45 cents) and the audio follows ----
        double worst = 0, worstSet = 0; var rows = new List<string>();
        foreach (var (ch, color, triangle, name) in new[] { (0, 0, false, "pulse1"), (1, 1, false, "pulse2"), (2, 2, true, "triangle") })
            foreach (int key in new[] { 36, 45, 57, 60, 69, 84, 96 })
            {
                int period = Nes.Period((key - 60) * 100.0, triangle);
                double wantHz = Nes.PeriodHz(period, triangle), setHz = -1; long keyed = -1;
                var (audio, mixer, plugin) = Play(One(key, color, 0.7), setup: p => { SetChip(p, Chip); p.SetParam(PInstrument, Sine); },
                    probe: (pl, sample) => { if (sample >= 22050 && setHz < 0) { setHz = pl.Test(65, ch) / 100.0; keyed = pl.Test(64, ch); } });
                using (plugin)
                {
                    double hz = SteadyHz(Mono(audio), 44100, mixer.Log[0]);
                    t.NearCents($"SMSFM {name} key {key}", hz, wantHz, 3.6);
                    if (!double.IsNaN(hz)) worst = Math.Max(worst, Math.Abs(Audio.Cents(hz, wantHz)));
                    double setErr = Audio.Cents(Math.Max(setHz, 1e-9), wantHz); worstSet = Math.Max(worstSet, Math.Abs(setErr));
                    t.Expect(keyed == 1 && Math.Abs(setErr) < 3.45, $"SMSFM {name} key {key}: FM channel {ch + 1} keyed {keyed}, set to {setHz:0.00} Hz, expected {wantHz:0.00} Hz ({setErr:+0.00;-0.00} cents)");
                    if (key == 69) rows.Add($"{name} {hz:0.00} Hz");
                }
            }
        t.Note($"SMSFM pitch: worst audio error {worst:0.00} cent, worst register error {worstSet:0.00} cent over 21 notes (65 Hz to 2 kHz); A4: {string.Join(", ", rows)}");

        // ---- level: what the NES chip would play, in dB; velocity falls; zero is silent ----
        double RmsOf(string chip, double vel, int ch, int patch, int duty = 1, int master = 1000)
        {
            var s = One(69, ch, 0.7, vel); s.Params[1] = master; s.Params[5] = duty; s.Params[11] = patch;
            var (audio, mixer, plugin) = Play(s, setup: p => SetChip(p, chip));
            using (plugin) return Audio.Rms(Mono(audio), mixer.Log[0].StartSample + 8000, mixer.Log[0].EndSample - 2000);
        }
        double r1 = RmsOf(Chip, 1.0, 0, Sine), r2 = RmsOf(Chip, 0.66, 0, Sine), r3 = RmsOf(Chip, 0.33, 0, Sine), r4 = RmsOf(Chip, 0.1, 0, Sine), r0 = RmsOf(Chip, 0.0, 0, Sine);
        t.Expect(r1 > r2 && r2 > r3 && r3 > r4 && r4 > 0, $"SMSFM level must fall with velocity: {r1:0.000} {r2:0.000} {r3:0.000} {r4:0.000}");
        t.Expect(r0 < 1e-4, $"SMSFM velocity 0 is not silent: {r0:0.0000}");
        double half = RmsOf(Chip, 1.0, 0, Sine, master: 500);
        t.Expect(half < r1 * 0.85 && half > r1 * 0.3, $"SMSFM master volume 50% gives {half / r1:P0} of full level");
        var dbRows = new List<string>();
        foreach (var (patch, ch, name, duty) in new[] { (Sine, 0, "sine pulse 25%", 1), (Sine, 0, "sine pulse 50%", 2), (Organ, 0, "organ pulse 25%", 1), (Violin, 1, "violin pulse 2 25%", 1), (Rectified, 0, "rectified 25%", 1), (PsgSquare, 0, "PSG square 25%", 1), (Sine, 2, "triangle", 1) })
        {
            double a = RmsOf(Chip, 1.0, ch, patch, duty), b = RmsOf("FIX", 1.0, ch, patch, duty);
            double d = 20 * Math.Log10(a / b);
            dbRows.Add($"{name} {d:+0.0;-0.0} dB");
            // the volume register is 3 dB a step (up to 1.5 dB off); APU_FIX low-passes its output, which takes about 1.3 dB out of a 25% pulse at A4; the chip's instruments have vibrato and their own wobble
            t.Expect(Math.Abs(d) <= 3.0, $"SMSFM {name} is {d:+0.0;-0.0} dB from the NES chip (limit 3.0): {a:0.0000} vs {b:0.0000}");
        }
        double noise = RmsOf(Chip, 0.8, 3, 0);
        t.Expect(noise > 0.003, $"the Master System FM chip's noise channel (the PSG's) is silent (rms {noise:0.0000})");
        t.Note($"SMSFM level against FIX at the same notes: {string.Join(", ", dbRows)}; velocity 1.0/.66/.33/.1: {r1:0.000}/{r2:0.000}/{r3:0.000}/{r4:0.000}");

        // ---- the Instrument parameter picks the instrument: each is its own timbre at the same pitch, and each instance has its own ----
        double Harmonic(int patch, int n, int ch = 0)
        {
            var s = One(60, ch, 0.8); s.Params[11] = patch;
            var (audio, mixer, plugin) = Play(s, setup: p => SetChip(p, Chip));
            using (plugin)
            {
                var mono = Mono(audio); int from = mixer.Log[0].StartSample + 12000;
                double f0 = Nes.PeriodHz(Nes.Period(0, ch == 2), ch == 2);
                return Goertzel(mono, 44100, f0 * n, from, from + 16000) / Goertzel(mono, 44100, f0, from, from + 16000);
            }
        }
        double sine2 = Harmonic(Sine, 2), rect2 = Harmonic(Rectified, 2), psg3 = Harmonic(PsgSquare, 3), oob3 = Harmonic(200, 3), lead = Math.Sqrt(Enumerable.Range(2, 5).Sum(n => { double h = Harmonic(FedBack, n); return h * h; }));
        t.Expect(sine2 < 0.02, $"the designed sine has a 2nd harmonic at {sine2:0.000} of the fundamental (limit 0.02)");
        t.Expect(Math.Abs(rect2 - 0.41) < 0.1, $"the rectified sine's 2nd harmonic is {rect2:0.000} of the fundamental (expect 0.41: a half-wave rectified sine)");
        t.Expect(Math.Abs(psg3 - 0.333) < 0.06, $"the PSG square's 3rd harmonic is {psg3:0.000} of the fundamental (expect 0.333)");
        t.Expect(lead > 0.2, $"the fed-back lead is bright: its partials 2 to 6 add up to {lead:0.00} of the fundamental");
        t.Expect(Math.Abs(oob3 - psg3) < 0.04, $"Instrument 200 (out of range) should be the last entry (the PSG square): 3rd harmonic {oob3:0.000} vs {psg3:0.000}");
        t.Note($"SMSFM instruments: sine 2nd {sine2:0.000}, rectified 2nd {rect2:0.00}, PSG square 3rd {psg3:0.00}, fed-back lead partials {lead:0.00}");
        {
            // the instrument is the instance's own: two instances on one emulator, one on the violin (chip instrument 1) and one on the sine (user instrument 0), each reads back its own
            var rack = MakeRack([0, 1]);
            SetChip(rack[0], Chip);
            rack[0].SetParam(PInstrument, Violin); rack[1].SetParam(PInstrument, Sine);
            var sc = new Scenario { Name = "fm-patches", Tempo = 60, LengthBeats = 0.8 };
            sc.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 0.6, Key = 60, Channel = 0 }); sc.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 0.6, Key = 67, Channel = 1 });
            long p0 = -1, p1 = -1, i0 = -1, i1 = -1; var m = new Mixer { SampleRate = 44100, BlockSize = 256, AfterBlock = sm => { if (sm >= 12000 && p0 < 0) { p0 = rack[0].Test(63); p1 = rack[1].Test(63); i0 = rack[0].Test(68, 0); i1 = rack[1].Test(68, 1); } } };
            m.Run(rack, sc);
            t.Expect(p0 == Violin && p1 == Sine, $"two instances on one emulator should each keep their own instrument: {p0} and {p1}");
            t.Expect(i0 == 1 && i1 == 0, $"the violin is chip instrument 1 and the designed sine is the user instrument 0 on the chip: {i0} and {i1}");
            rack[0].Dispose();
        }

        // ---- range: the NES's lowest pulse (54.6 Hz) plays; a note above the FM chip's top (6.2 kHz) is silent and counted; absurd bends stay finite ----
        {
            var (audio, mixer, plugin) = Play(One(33, 0, 0.6), setup: p => { SetChip(p, Chip); p.SetParam(PInstrument, Sine); });
            using (plugin) t.Expect(Audio.Rms(Mono(audio), mixer.Log[0].StartSample + 8000, mixer.Log[0].EndSample - 2000) > 0.003, "a pulse note at 55 Hz (the NES pulse's bottom) should sound on the FM chip");
            long above = -1;
            var (audio2, mixer2, plugin2) = Play(One(125, 0, 0.5), setup: p => { SetChip(p, Chip); p.SetParam(PInstrument, Sine); }, probe: (pl, sm) => { if (sm >= 20000 && above < 0) above = pl.Test(67); });
            using (plugin2)
            {
                t.Expect(Audio.Rms(Mono(audio2), mixer2.Log[0].StartSample + 4000, mixer2.Log[0].EndSample) < 0.002 && audio2.All(float.IsFinite), "a note above the FM chip's top (10 kHz) should be silent");
                t.Expect(above == 1, $"... and counted as one note above the FM range (the chip says {above})");
            }
            var (audio3, _, plugin3) = Play(One(140, 0, 0.5), setup: p => SetChip(p, Chip));
            using (plugin3) t.Expect(audio3.All(float.IsFinite) && Audio.Rms(Mono(audio3), 4000, audio3.Length / 2) < 0.002, "an ultrasonic pulse should mute on the Master System FM chip too");
            var s = One(60, 0, 0.5);
            s.Notes[0].Bend = new() { new[] { 0.0, 100000 }, new[] { 0.25, -100000 }, new[] { 0.5, 0 } };
            var (audio4, _, plugin4) = Play(s, setup: p => SetChip(p, Chip));
            using (plugin4) t.Expect(audio4.All(float.IsFinite), "non-finite output after extreme bends on the Master System FM chip");
        }

        // ---- polyphony: pulse 1, pulse 2, the triangle and the noise at once, one instance each, every tone where it belongs in the mix ----
        {
            var sc = new Scenario { Name = "fm-poly", Tempo = 60, LengthBeats = 1.2 };
            int[] keys = [57, 64, 69, 60];
            for (int c = 0; c < 4; c++) sc.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1.0, Key = keys[c], Channel = c, Velocity = c == 3 ? 0.25 : 0.8 });
            var rack = MakeRack([0, 1, 2, 3]);
            var first = rack[0];
            SetChip(first, Chip);
            first.SetParam(PInstrument, Organ);
            var keyed = new long[4]; var psgVol = -1L; bool taken = false;
            var mixer = new Mixer { SampleRate = 44100, BlockSize = 256, AfterBlock = sm => { if (sm >= 22050 && !taken) { taken = true; for (int c = 0; c < 3; c++) keyed[c] = rack[c].Test(64, c); psgVol = rack[3].Test(69, 3); } } };
            var audio = mixer.Run(rack, sc);
            first.Dispose();
            var mono = Mono(audio);
            for (int c = 0; c < 3; c++)
            {
                bool tri = c == 2;
                double hz = Nes.PeriodHz(Nes.Period((keys[c] - 60) * 100.0, tri), tri);
                double at = Goertzel(mono, 44100, hz, 12000, 36000), off = Goertzel(mono, 44100, hz * 1.059, 12000, 36000);
                t.Expect(keyed[c] == 1, $"polyphony: instance {c + 1} has FM channel {c + 1} keyed {keyed[c]}");
                t.Expect(at > off * 3 && at > 0.005, $"polyphony: the mix has {hz:0.0} Hz at {at:0.0000}, only {off:0.0000} a semitone above it");
            }
            t.Expect(psgVol >= 0 && psgVol < 15, $"polyphony: the noise instance has PSG noise volume {psgVol} (15 is off)");
            double all = Audio.Rms(mono, 12000, 36000);
            var one = Play(One(69, 0, 1.0), setup: p => { SetChip(p, Chip); p.SetParam(PInstrument, Organ); });
            double single;
            using (one.Plugin) single = Audio.Rms(Mono(one.Audio), 12000, 36000);
            t.Expect(all > single * 1.5, $"polyphony: four voices ({all:0.000}) are not louder than one ({single:0.000})");
        }

        // ---- the editor, through real mouse messages: the Master System console lists its two chips; the FM chip has an instrument picker for the pulses listing the whole bank, the PSG chip has none ----
        {
            nint parent = Win32Host.CreateParent();
            try
            {
                using var p = lib.Create();
                p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                p.ShowEditor(parent);
                Win32Host.Pump(150);
                if (p.EditorHandle == 0) { t.Expect(false, "no editor"); return; }
                ClickControl(t, p, 53, "SMS segment");
                t.Expect(p.GetParam(PConsole) == 3 && p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == "SMS", "the SMS segment did not switch to the PSG chip");
                t.Expect(p.Test(30, 90) == 0 && p.Test(30, 25) != 0, "on the PSG chip the Pulse duty slider stays (dimmed) and there is no instrument picker");
                ClickControl(t, p, 4, "Sound chip dropdown");
                t.Expect(p.Test(31) == 4 && p.Test(32) == 2, $"the Master System console should list two chips, the list has {p.Test(32)}");
                var chipItem = Centre(p, 100 + 1);
                t.Expect(chipItem != null, "the second chip is not reachable in the open list");
                if (chipItem != null) Win32Host.Click(p.EditorHandle, chipItem.Value.X, chipItem.Value.Y);
                t.Expect(p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == Chip, $"picking the second chip left the Sound chip at {p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore))}");
                t.Expect(p.Test(30, 90) != 0 && p.Test(30, 25) == 0, "on the FM chip the Instrument picker should replace the Pulse duty slider on a pulse channel");
                ClickControl(t, p, 90, "Instrument dropdown");
                t.Expect(p.Test(31) == 90 && p.Test(32) == Count, $"the instrument list has {p.Test(32)} items, expected the bank's {Count}");
                var item = Centre(p, 100 + 3);
                if (item != null) Win32Host.Click(p.EditorHandle, item.Value.X, item.Value.Y);
                else
                {
                    // a long list scrolls: scroll it with the wheel until the fourth item shows
                    t.Expect(false, "instrument 3 is not reachable in the open list");
                }
                t.Expect(p.Test(31) == 0 && p.GetParam(PInstrument) == 3, $"picking the fourth instrument left Instrument at {p.GetParam(PInstrument)}");
                PickItem(t, p, 9, 3, "Channel Triangle");
                t.Expect(p.Test(30, 90) == 0, "the triangle is a sine: no instrument picker on its channel");
                ClickControl(t, p, 13, "noise cell");
                t.Expect(p.Test(30, 90) == 0 && p.Test(30, 27) != 0, "the noise channel keeps its Noise mode slider and has no instrument picker");
                ClickControl(t, p, 50, "NES segment");
                ClickControl(t, p, 10, "pulse 1 cell");
                t.Expect(p.Test(30, 90) == 0 && p.Test(30, 25) != 0, "back on a NES chip the Pulse duty slider returns and the picker goes");
                p.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }
        }

        // ---- real time and no allocation: four sliding voices on the FM chip (each instance has its own YM2413), and the instrument changing under them ----
        {
            var s = new Scenario { Name = "rt-fm", Tempo = 60, LengthBeats = 11 };
            for (int c = 0; c < 4; c++) s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 9, Key = 57 + c * 5, Channel = c, Slides = new() { new SlideSeg { StartBeat = 1, LengthBeats = 8, ToKey = 69 + c } } });
            var rack = MakeRack([0, 1, 2, 3]);
            var plugin = rack[0];
            SetChip(plugin, Chip);
            long allocBefore = 0, allocAfter = 0; int switches = 0;
            var mixer = new Mixer { SampleRate = 48000, BlockSize = 512, AfterBlock = sm =>
            {
                if (sm >= 48000 * 2 && allocBefore == 0) allocBefore = plugin.Test(4);
                if (sm >= 48000 * 9 && allocAfter == 0) allocAfter = plugin.Test(4);
                if (sm / 48000 != (sm - 512) / 48000) { plugin.SetParam(PInstrument, (sm / 48000) % Count); switches++; }      // a new instrument every second
            } };
            var sw = Pace.Start();
            var audio = mixer.Run(rack, s, 0);
            sw.Stop();
            plugin.Dispose();
            double speed = audio.Length / 2 / 48000.0 / sw.Seconds;
            long alloc = allocAfter - allocBefore;
            t.Expect(speed >= 5, $"{speed:0.0}x real time with 4 Master System FM instances (needs 5x or better: the plugin's floor)");
            t.Expect(alloc == 0, $"{alloc:N0} bytes allocated by the plugin while playing 4 sliding FM voices for 7 s with the instrument changing every second (must be 0: garbage collection is audible)");
            t.Expect(audio.All(float.IsFinite), "non-finite output while the instrument changed under four sliding voices");
            t.Note($"SMSFM: {sw.Describe(audio.Length / 2 / 48000.0)}, 4 instances with a sliding voice each, 48 kHz / 512-sample blocks, {switches} instrument changes; steady-state allocation {alloc} B");
        }
    }
}
