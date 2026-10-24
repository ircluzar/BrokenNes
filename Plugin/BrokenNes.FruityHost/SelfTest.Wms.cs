// WideMess milestone 7a: the WideMess sound chip as an instrument (chip WMS, Direct mode) and WideMess games in ROM mode, through the real DLL.
// Every check measures what comes out (pitch, harmonics, levels, voice counts, pictures against the spec's model) and has a negative control or a deliberate bug that it must catch.
using System.IO.Compression;
using BrokenNes.Fruity;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkWmsDirect = 900, HkWmsRunaway = 901, HkWmsSounding = 902, HkWmsHeld = 903, HkWmsCount = 904, HkWmsInfo = 905, HkWmsLength = 906, HkWmsPlays = 907, HkWmsHash = 908,
        HkWmsRoundTrip = 909, HkWmsPatches = 910, HkWmsChip = 911, HkWmsKind = 912, HkWmsUnlocked = 913, HkWmsDescribe = 914, HkWmsNote = 915, HkWmsMutant = 939;
    private const string WmsChipId = "WMS";
    private const int WmsAuto = 0, WmsSquare = 1, WmsPulse25 = 2, WmsTriangle = 4, WmsSaw = 5, WmsSine = 6, WmsOrgan = 7, WmsNoise = 12, WmsNoisePeriodic = 13;

    private static string WmsRoms => Path.Combine(MidiFixtures.RepoRoot(), @"WideMess\testroms\bin");

    /// <summary>The ratio of harmonic <paramref name="n"/> to the fundamental of a held note (Goertzel over the steady part).</summary>
    private double WmsHarmonic(int patch, int n, double key = 57, int channel = 0, int duty = 1)
    {
        var s = One(key, channel, 0.8); s.Params[11] = patch; s.Params[5] = duty;
        var (audio, mixer, plugin) = Play(s, setup: p => SetChip(p, WmsChipId));
        using (plugin)
        {
            var mono = Mono(audio); int from = mixer.Log[0].StartSample + 8000; double f0 = KeyHz(key);
            return Goertzel(mono, 44100, f0 * n, from, from + 22050) / Goertzel(mono, 44100, f0, from, from + 22050);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // wms: the WideMess sound chip in Direct mode

    private void Wms(T t)
    {
        // ---- the chip list: appended after the Presets chips (append-only), on the SNES console ----
        List<string> chips;
        using (var probe = lib.Create()) chips = Chips(probe);
        t.Expect(chips.Count == 30 && chips.IndexOf(WmsChipId) == 29, $"the {WmsChipId} chip is index {chips.IndexOf(WmsChipId)} of {chips.Count} (expected 29: appended after the six Presets chips)");
        t.Expect(chips.IndexOf("FIX") == 0 && chips.IndexOf("DMG") == 17 && chips.IndexOf("DMGS") == 18 && chips.IndexOf("SNES") == 19 && chips.IndexOf("SMS") == 20 && chips.IndexOf("MD") == 21 && chips.IndexOf("SMSFM") == 22
            && string.Join(",", chips.Skip(23).Take(6)) == "PRESETS,PRE-NES,PRE-GB,PRE-SNES,PRE-SMS,PRE-MD", "an existing chip moved (the chip index is part of saved projects: append only)");
        using (var p = lib.Create())
        {
            t.Expect(p.Test(HkWmsChip) == 29, $"the plugin's own index of the WMS chip is {p.Test(HkWmsChip)}");
            SetChip(p, WmsChipId);
            t.Expect(p.GetParam(PConsole) == 2 && p.Test(HkWmsDirect) == 1, $"picking WMS should put the emulator on the SNES console in WideMess Direct mode (console {p.GetParam(PConsole)}, mode {p.Test(HkWmsDirect)})");
            p.SetParam(PConsole, 2);
            t.Expect(p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == WmsChipId, "choosing the SNES console again (already on it) must keep the WMS chip");
            p.SetParam(PConsole, 0); p.SetParam(PConsole, 2);
            t.Expect(p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == "SNES", "the SNES console still starts with its own chip, SNES (WMS is the second sound chip of that console)");
            t.Expect(p.Test(HkWmsPatches) == 14, $"the WMS chip has {p.Test(HkWmsPatches)} built-in instruments (expected 14: Auto and 13)");
        }
        byte[] blob;
        using (var a = lib.Create()) { SetChip(a, WmsChipId); a.SetParam(PInstrument, WmsOrgan); blob = a.SaveState(); }
        using (var b = lib.Create())
        {
            b.LoadState(blob);
            t.Expect(b.GetParam(PCore) == 29 && b.GetParam(PConsole) == 2 && b.GetParam(PInstrument) == WmsOrgan && b.Test(HkWmsDirect) == 1, $"a project saved on the WMS chip came back as chip {b.GetParam(PCore)}, console {b.GetParam(PConsole)}, instrument {b.GetParam(PInstrument)}");
            t.Expect(b.SaveState().SequenceEqual(blob), "save -> load -> save is not stable on the WMS chip");
        }

        // ---- pitch: every octave from A0 to C8 on the sine (each note starts on the loop whose rate puts its pitch register between $1000 and $1FFF), and on the other channels ----
        double worst = 0; var rows = new List<string>();
        foreach (var (ch, name) in new[] { (0, "pulse 1"), (2, "triangle") })
            foreach (int key in new[] { 21, 33, 36, 45, 57, 60, 69, 72, 81, 84, 96, 108 })
            {
                var s = One(key, ch, 0.7); s.Params[11] = WmsSine;
                var (audio, mixer, plugin) = Play(s, setup: p => SetChip(p, WmsChipId));
                using (plugin)
                {
                    var mono = Mono(audio);
                    double hz = key < 30 ? Audio.Frequency(mono, 44100, mixer.Log[0].StartSample + 8000, mixer.Log[0].EndSample - 4000) : SteadyHz(mono, 44100, mixer.Log[0]);
                    double want = KeyHz(key), err = Audio.Cents(hz, want);
                    t.NearCents($"WMS sine {name} key {key}", hz, want, 1.0);
                    if (!double.IsNaN(err)) worst = Math.Max(worst, Math.Abs(err));
                    if (key == 69) rows.Add($"{name} {hz:0.000} Hz");
                }
            }
        t.Note($"WMS pitch: worst {worst:0.000} cents over 24 notes from 27.5 Hz to 4,186 Hz (the 12-TET frequency itself, not a NES period); A4: {string.Join(", ", rows)}");
        // the deliberate bug: every note's pitch worked out for the widest loop (4,096 samples): the high notes must come out wrong
        {
            using var m = lib.Create();
            m.Test(HkWmsMutant, 0, 2);
            try
            {
                var s = One(81, 0, 0.7); s.Params[11] = WmsSine;
                var (audio, mixer, plugin) = Play(s, setup: p => SetChip(p, WmsChipId));
                using (plugin)
                {
                    double hz = SteadyHz(Mono(audio), 44100, mixer.Log[0]);
                    t.Expect(double.IsNaN(hz) || Math.Abs(Audio.Cents(hz, KeyHz(81))) > 50, $"deliberate bug (pitch for the wrong loop) not caught: A5 still measured {hz:0.00} Hz");
                }
            }
            finally { m.Test(HkWmsMutant, 0, 0); }
        }

        // ---- 16-bit samples: the sine's harmonics are below -60 dB (a 4-bit BRR sine would be near -30); each instrument is its own timbre (harmonics measured against the formulas) ----
        double sine2 = WmsHarmonic(WmsSine, 2), sine3 = WmsHarmonic(WmsSine, 3), sq3 = WmsHarmonic(WmsSquare, 3), sq2 = WmsHarmonic(WmsSquare, 2), saw2 = WmsHarmonic(WmsSaw, 2), tri3 = WmsHarmonic(WmsTriangle, 3),
               p25 = WmsHarmonic(WmsPulse25, 2), org2 = WmsHarmonic(WmsOrgan, 2), auto2 = WmsHarmonic(WmsAuto, 2, duty: 1), autoTri3 = WmsHarmonic(WmsAuto, 3, channel: 2), autoSq2 = WmsHarmonic(WmsAuto, 2, duty: 2);
        t.Expect(sine2 < 0.001 && sine3 < 0.001, $"the 16-bit sine's 2nd and 3rd harmonics are {sine2:0.0000} and {sine3:0.0000} of the fundamental (limit 0.001, -60 dB)");
        t.Expect(Math.Abs(sq3 - 1 / 3.0) < 0.03 && sq2 < 0.01, $"the square's 3rd harmonic is {sq3:0.000} (expect 0.333) and its 2nd {sq2:0.000} (expect 0)");
        t.Expect(Math.Abs(saw2 - 0.5) < 0.04, $"the sawtooth's 2nd harmonic is {saw2:0.000} (expect 0.5)");
        t.Expect(Math.Abs(tri3 - 1 / 9.0) < 0.02, $"the triangle's 3rd harmonic is {tri3:0.000} (expect 0.111)");
        t.Expect(Math.Abs(p25 - 0.707) < 0.05, $"the 25% pulse's 2nd harmonic is {p25:0.000} (expect 0.707)");
        t.Expect(Math.Abs(org2 - 0.8) < 0.05, $"the organ's 2nd drawbar is {org2:0.000} (expect 0.8)");
        t.Expect(Math.Abs(auto2 - p25) < 0.02 && Math.Abs(autoSq2 - sq2) < 0.01 && Math.Abs(autoTri3 - tri3) < 0.02, $"Auto plays the channel's own: pulse 1 at 25% duty {auto2:0.000} (the 25% pulse {p25:0.000}), at 50% {autoSq2:0.000} (the square {sq2:0.000}), the triangle channel {autoTri3:0.000} (the triangle {tri3:0.000})");
        t.Note($"WMS instruments, harmonic / fundamental at A3: sine 2nd {sine2:0.00000} 3rd {sine3:0.00000}; square 3rd {sq3:0.000}; saw 2nd {saw2:0.000}; triangle 3rd {tri3:0.000}; pulse 25% 2nd {p25:0.000}; organ 2nd {org2:0.000}");
        {
            // out of range is the last instrument (the periodic noise): the same sound as instrument 13
            var s1 = One(60, 3, 0.5); s1.Params[11] = 200; var s2 = One(60, 3, 0.5); s2.Params[11] = WmsNoisePeriodic;
            var r1 = Play(s1, setup: p => SetChip(p, WmsChipId)); var r2 = Play(s2, setup: p => SetChip(p, WmsChipId));
            using (r1.Plugin) using (r2.Plugin)
                t.Expect(r1.Audio.Length == r2.Audio.Length && r1.Audio.SequenceEqual(r2.Audio) && Audio.Rms(Mono(r1.Audio), 8000, 20000) > 0.005, "Instrument 200 should play the last instrument (the periodic noise), sample for sample");
            var s3 = One(60, 3, 0.5); s3.Params[11] = WmsNoise;
            var r3 = Play(s3, setup: p => SetChip(p, WmsChipId));
            using (r3.Plugin) t.Expect(Audio.Rms(Mono(r3.Audio), 8000, 20000) > 0.005 && !r3.Audio.SequenceEqual(r1.Audio), "the white noise is audible and is not the periodic one");
        }

        // ---- many voices: one instance plays a 24-note chord, every note at its own frequency in the mix; a 25th takes the oldest's voice; the NES-register chips play one note per channel ----
        {
            int[] keys = Enumerable.Range(0, 24).Select(i => 43 + i * 2).ToArray();       // G2 to B5 in whole tones (98 Hz to 988 Hz)
            Scenario Chord(int n, double extraStart = -1)
            {
                var sc = new Scenario { Name = "wms-chord", Tempo = 60, LengthBeats = 2.2 };
                for (int i = 0; i < n; i++) sc.Notes.Add(new SimNote { StartBeat = i * 0.005, LengthBeats = 2.0 - i * 0.005, Key = keys[i], Channel = 0, Velocity = 0.6 });
                if (extraStart >= 0) sc.Notes.Add(new SimNote { StartBeat = extraStart, LengthBeats = 2.0 - extraStart, Key = 100, Channel = 0, Velocity = 0.6 });
                sc.Params[11] = WmsSine;
                return sc;
            }
            int Found(float[] mono, int from, int to)
            {
                int found = 0;
                foreach (int k in keys) { double f = KeyHz(k), at = Goertzel(mono, 44100, f, from, to), off = Goertzel(mono, 44100, f * Math.Pow(2, 1 / 24.0), from, to); if (at > off * 4 && at > 0.0005) found++; }
                return found;
            }
            long sounding = -1, held = -1;
            var (audio, _, plugin) = Play(Chord(24), setup: p => { SetChip(p, WmsChipId); p.SetParam(PInstrument, WmsSine); }, probe: (pl, sm) => { if (sm >= 44100 && sounding < 0) { sounding = pl.Test(HkWmsSounding); held = pl.Test(HkWmsHeld); } });
            int found;
            using (plugin) found = Found(Mono(audio), 44100 / 2, 44100 * 3 / 2);
            t.Expect(sounding == 24 && held == 24 && found == 24, $"24 notes at once on one instance: {sounding} of the chip's new voices sound, {held} FL notes hold a voice, {found} of the 24 frequencies are in the mix");
            // a 25th note: the oldest note gives its voice to it (24 still sound; the first note's frequency is gone, the new one is there)
            long sounding25 = -1, held25 = -1;
            var (audio2, _, plugin2) = Play(Chord(24, extraStart: 0.5), setup: p => { SetChip(p, WmsChipId); p.SetParam(PInstrument, WmsSine); }, probe: (pl, sm) => { if (sm >= 44100 && sounding25 < 0) { sounding25 = pl.Test(HkWmsSounding); held25 = pl.Test(HkWmsHeld); } });
            using (plugin2)
            {
                var mono = Mono(audio2);
                double first = Goertzel(mono, 44100, KeyHz(keys[0]), 44100, 44100 * 3 / 2), extra = Goertzel(mono, 44100, KeyHz(100), 44100, 44100 * 3 / 2), firstBefore = Goertzel(mono, 44100, KeyHz(keys[0]), 4000, 20000);
                t.Expect(sounding25 == 24 && held25 == 24 && extra > 0.0005 && first < firstBefore * 0.1, $"a 25th note takes the oldest note's voice: {sounding25} sound, {held25} hold a voice, the new note at {extra:0.0000}, the first note {firstBefore:0.0000} -> {first:0.0000}");
            }
            // the deliberate bug: only 8 voices (the S-DSP's count) must be caught by the same measurement
            using (var m = lib.Create())
            {
                m.Test(HkWmsMutant, 0, 1);
                try
                {
                    long s8 = -1;
                    var (a8, _, p8) = Play(Chord(24), setup: p => { SetChip(p, WmsChipId); p.SetParam(PInstrument, WmsSine); }, probe: (pl, sm) => { if (sm >= 44100 && s8 < 0) s8 = pl.Test(HkWmsSounding); });
                    int f8; using (p8) f8 = Found(Mono(a8), 44100 / 2, 44100 * 3 / 2);
                    t.Expect(s8 <= 8 && f8 < 24, $"deliberate bug (8 voices) not caught: {s8} sound, {f8} frequencies found");
                }
                finally { m.Test(HkWmsMutant, 0, 0); }
            }
            // the negative control: the same chord on the SNES chip (a NES-register bridge: one note per channel) has one note, the newest
            var (audioS, _, plugS) = Play(Chord(24), setup: p => SetChip(p, "SNES"));
            int foundS; using (plugS) foundS = Found(Mono(audioS), 44100 / 2, 44100 * 3 / 2);
            t.Expect(foundS <= 2, $"the control: the same chord on the SNES chip should play one note per channel, {foundS} of the 24 frequencies found");
            t.Note($"WMS polyphony: 24 notes on one instance: {found} frequencies in the mix (the SNES chip: {foundS}); a 25th note takes the oldest voice");
        }

        // ---- level: a 25% pulse (Auto on pulse 1) against the NES chip's; velocity falls; zero is silent; the master volume ----
        double RmsOf(string chip, double vel, int ch, int patch, int duty = 1, int master = 1000)
        {
            var s = One(69, ch, 0.7, vel); s.Params[1] = master; s.Params[5] = duty; s.Params[11] = patch;
            var (audio, mixer, plugin) = Play(s, setup: p => SetChip(p, chip));
            using (plugin) return Audio.Rms(Mono(audio), mixer.Log[0].StartSample + 8000, mixer.Log[0].EndSample - 2000);
        }
        double l1 = RmsOf(WmsChipId, 1.0, 0, WmsAuto), l2 = RmsOf(WmsChipId, 0.66, 0, WmsAuto), l3 = RmsOf(WmsChipId, 0.33, 0, WmsAuto), l0 = RmsOf(WmsChipId, 0.0, 0, WmsAuto);
        t.Expect(l1 > l2 && l2 > l3 && l3 > 0, $"WMS level must fall with velocity: {l1:0.000} {l2:0.000} {l3:0.000}");
        t.Expect(l0 < 1e-4, $"WMS velocity 0 is not silent: {l0:0.0000}");
        double lh = RmsOf(WmsChipId, 1.0, 0, WmsAuto, master: 500);
        t.Expect(Math.Abs(lh / l1 - 0.5) < 0.05, $"WMS master volume 50% gives {lh / l1:P0} of full level");
        double fixLevel = RmsOf("FIX", 1.0, 0, 0), db = 20 * Math.Log10(l1 / fixLevel);
        t.Expect(Math.Abs(db) <= 3.0, $"a WMS 25% pulse at full velocity is {db:+0.0;-0.0} dB from the NES chip's (limit 3): {l1:0.0000} vs {fixLevel:0.0000}");
        t.Note($"WMS level: 25% pulse at A4 {db:+0.0;-0.0} dB from FIX; velocity 1/.66/.33: {l1:0.000}/{l2:0.000}/{l3:0.000}");

        // ---- slides and bends: a held note slides an octave up across the loops' ranges and lands on the pitch; absurd bends stay finite ----
        {
            var s = new Scenario { Name = "wms-slide", Tempo = 60, LengthBeats = 2.2 };
            s.Params[11] = WmsSine;
            // (FL's slide notes: the note ends with its last slide; the second one glides from A4 to A4, a hold to measure where the first landed)
            s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1.8, Key = 57, Channel = 0, Slides = new() { new SlideSeg { StartBeat = 0.3, LengthBeats = 0.6, ToKey = 69 }, new SlideSeg { StartBeat = 0.9, LengthBeats = 0.9, ToKey = 69 } } });
            var (audio, _, plugin) = Play(s, setup: p => { SetChip(p, WmsChipId); p.SetParam(PInstrument, WmsSine); });
            using (plugin)
            {
                var mono = Mono(audio);
                double before = Audio.Frequency(mono, 44100, 2000, 12000), after = Audio.Frequency(mono, 44100, 44100 + 4000, 44100 * 17 / 10);
                t.Expect(Math.Abs(Audio.Cents(before, KeyHz(57))) < 2 && Math.Abs(Audio.Cents(after, KeyHz(69))) < 2, $"a slide A3 -> A4 should start at {KeyHz(57):0.00} and land at {KeyHz(69):0.00} Hz: measured {before:0.00} and {after:0.00}");
            }
            var b = One(60, 0, 0.5);
            b.Notes[0].Bend = new() { new[] { 0.0, 100000 }, new[] { 0.25, -100000 }, new[] { 0.5, 0 } };
            var (audio2, _, plugin2) = Play(b, setup: p => SetChip(p, WmsChipId));
            using (plugin2) t.Expect(audio2.All(float.IsFinite), "non-finite output after extreme bends on the WMS chip");
        }

        // ---- real time and no allocation: four instances, each a sliding 6-note chord on the 24-voice chip ----
        {
            var s = new Scenario { Name = "rt-wms", Tempo = 60, LengthBeats = 11 };
            for (int c = 0; c < 4; c++)
                for (int k = 0; k < 6; k++)
                    s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 9, Key = 45 + c * 7 + k * 4, Channel = c, Velocity = 0.5, Slides = new() { new SlideSeg { StartBeat = 1, LengthBeats = 8, ToKey = 50 + c * 7 + k * 4 } } });
            var rack = MakeRack([0, 1, 2, 3]);
            var plugin = rack[0];
            SetChip(plugin, WmsChipId);
            foreach (var r in rack) r.SetParam(PInstrument, WmsSaw);
            long allocBefore = 0, allocAfter = 0, voicesAt = -1;
            var mixer = new Mixer { SampleRate = 48000, BlockSize = 512, AfterBlock = sm => { if (sm >= 48000 * 2 && allocBefore == 0) { allocBefore = AllocStart(plugin); voicesAt = plugin.Test(HkWmsHeld); } if (sm >= 48000 * 9 && allocAfter == 0) allocAfter = plugin.Test(4); } };
            var sw = Pace.Start();
            var audio = mixer.Run(rack, s, 0);
            sw.Stop();
            plugin.Dispose();
            double speed = audio.Length / 2 / 48000.0 / sw.Seconds;
            long alloc = allocAfter - allocBefore;
            t.Expect(voicesAt == 6, $"each instance should hold its 6 notes on its own chip (the first holds {voicesAt})");
            t.Expect(speed >= 5, $"{speed:0.0}x real time with 4 WMS instances of 6 sliding notes each (needs 5x or better: the plugin's floor)");
            t.Expect(alloc == 0, $"{alloc:N0} bytes allocated by the plugin while playing 24 sliding WMS voices for 7 s (must be 0: garbage collection is audible)");
            t.Note($"WMS: {speed:0.0}x real time, 4 instances x 6 sliding notes (24 voices), 48 kHz / 512-sample blocks; steady-state allocation {alloc} B");
        }

        // ---- Reset Console: the held notes end and the chip is made again ----
        {
            var s = One(60, 0, 1.6); s.Params[11] = WmsOrgan;
            int resetAt = 44100 / 2; bool done = false;
            var (audio, _, plugin) = Play(s, setup: p => SetChip(p, WmsChipId), probe: (pl, sm) => { if (!done && sm >= resetAt) { pl.Test(43); done = true; } });
            using (plugin) t.Expect(Audio.Rms(Mono(audio), resetAt + 4000, resetAt + 30000) < 0.002, "after Reset Console the held WMS note should be gone");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // rom-wms: WideMess games in ROM mode, SNES games on the WMS chip, Instrument Runaway on a WideMess game, the project

    /// <summary>A plugin in ROM mode running <paramref name="file"/> on the SNES console's <paramref name="chip"/> (a project that says so: the project's chip is kept, whatever the file).</summary>
    private PluginInstance? WmsGame(string file, string chip = WmsChipId)
    {
        byte[] st;
        using (var seed = lib.Create()) { seed.SetParam(PConsole, 2); SetChip(seed, chip); seed.SetParam(PMode, 1); st = seed.SaveState(); }
        var p = lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        p.LoadState(StateWithRom(st, file));
        if (!WaitConsole(p, 2)) { p.Dispose(); return null; }
        return p;
    }

    /// <summary>The hash the plugin's picture has (Emulator.PictureHash: FNV-1a over the RGBA bytes) for a PNG of the spec's model.</summary>
    private static long PngPictureHash(string png, out int w, out int h)
    {
        var b = File.ReadAllBytes(png);
        int o = 8; w = h = 0; int type = 0; var idat = new MemoryStream();
        while (o < b.Length)
        {
            int len = b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]; string kind = System.Text.Encoding.ASCII.GetString(b, o + 4, 4);
            if (kind == "IHDR") { w = b[o + 8] << 24 | b[o + 9] << 16 | b[o + 10] << 8 | b[o + 11]; h = b[o + 12] << 24 | b[o + 13] << 16 | b[o + 14] << 8 | b[o + 15]; type = b[o + 17]; }
            else if (kind == "IDAT") idat.Write(b, o + 8, len);
            o += 12 + len;
        }
        int bpp = type == 6 ? 4 : 3, stride = w * bpp;
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream(); z.CopyTo(raw); var r = raw.ToArray();
        var px = new byte[h * stride];
        for (int y = 0; y < h; y++)
        {
            int f = r[y * (stride + 1)];
            for (int x = 0; x < stride; x++)
            {
                int l = x >= bpp ? px[y * stride + x - bpp] : 0, u = y > 0 ? px[(y - 1) * stride + x] : 0, ul = y > 0 && x >= bpp ? px[(y - 1) * stride + x - bpp] : 0;
                int v = r[y * (stride + 1) + 1 + x];
                v += f switch { 1 => l, 2 => u, 3 => (l + u) >> 1, 4 => Paeth(l, u, ul), _ => 0 };
                px[y * stride + x] = (byte)v;
            }
        }
        ulong hh = 1469598103934665603UL;
        for (int i = 0; i < w * h; i++)
        {
            hh = (hh ^ px[i * bpp]) * 1099511628211UL; hh = (hh ^ px[i * bpp + 1]) * 1099511628211UL; hh = (hh ^ px[i * bpp + 2]) * 1099511628211UL; hh = (hh ^ 255) * 1099511628211UL;
        }
        return (long)(hh | 1);
        static int Paeth(int a, int b2, int c) { int p = a + b2 - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b2), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b2 : c; }
    }

    private static string WmsDescription(PluginInstance p) { var c = new char[600]; fixed (char* cp = c) { long n = p.Test(HkWmsDescribe, c.Length, (nint)cp); return n <= 0 ? "" : new string(c, 0, (int)Math.Min(n, c.Length)); } }

    private void RomWms(T t)
    {
        string bin = WmsRoms;
        foreach (var f in new[] { "layers.wms", "sprites.wms", "widesnes.wms", "sound.wms", "sound_nounlock.wms", "sprites.expect.png", "widesnes.expect.png" })
            if (!File.Exists(Path.Combine(bin, f))) { t.Expect(false, $"missing {f} in {bin} (the WideMess test ROMs are in the repository)"); return; }

        // ---- a .wms file: the emulator goes to the SNES console with the WMS chip, the game runs on the WideMess console, 400 x 224, the 8-layer picture moves ----
        using (var p = WmsGame(Path.Combine(bin, "layers.wms")))
        {
            if (p == null) { t.Expect(false, "layers.wms did not load"); return; }
            RenderBlocks(p, 1.5);
            t.Expect(p.Test(12) == 0 && p.Test(11) >= 60, $"layers.wms: crashed {p.Test(12)}, {p.Test(11)} frames in 1.5 s");
            t.Expect(p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == WmsChipId && p.Test(HkWmsKind) == 1 && p.Test(17) == 4, $"layers.wms: chip {p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore))}, session {p.Test(HkWmsKind)} (1 = WideMess), channel {p.Test(17)} (4 = Mix)");
            long h1 = p.Test(10); RenderBlocks(p, 0.3); long h2 = p.Test(10);
            nint parent = Win32Host.CreateParent();
            try
            {
                p.ShowEditor(parent);
                for (int k = 0; k < 4; k++) { RenderBlocks(p, 0.1); Win32Host.Pump(60); }
                long dims = p.Test(37);
                t.Expect((int)(dims & 0xFFFF) == 400 && (int)(dims >> 16) == 224 && h1 != h2, $"layers.wms: the editor shows a {(int)(dims & 0xFFFF)} x {(int)(dims >> 16)} picture (expected 400 x 224), moving: {h1 != h2}");
                p.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }
            t.Expect(WmsDescription(p).Contains("unlocked"), $"layers.wms: the game unlocks the WideMess ({WmsDescription(p)})");
        }

        // ---- a .wms file opened from the Game list (or FL's file dialog) takes the WMS chip, from any console; a SNES file opened then stays on it (the WideMess runs SNES games) ----
        using (var q = RomPlugin())
        {
            RenderBlocks(q, 0.3);
            t.Expect(LoadFileInto(q, Path.Combine(bin, "layers.wms")) == 0 && WaitConsole(q, 2), "loading a .wms file into a NES emulator should switch it to the SNES console");
            RenderBlocks(q, 0.5);
            t.Expect(q.GetParam(PConsole) == 2 && q.GetName(Fpn.ParamValue, PCore, q.GetParam(PCore)) == WmsChipId && q.Test(HkWmsKind) == 1, $"a .wms file should take the WMS chip and run on the WideMess console: console {q.GetParam(PConsole)}, chip {q.GetName(Fpn.ParamValue, PCore, q.GetParam(PCore))}, session {q.Test(HkWmsKind)}");
            string tag = Guid.NewGuid().ToString("N"), sfc = Path.Combine(Path.GetTempPath(), $"bn2_{tag}.sfc");
            File.WriteAllBytes(sfc, MakeSnesRom());
            try
            {
                t.Expect(LoadFileInto(q, sfc) == 0, "loading a .sfc file after it should be taken");
                var until = Environment.TickCount64 + 20000;
                while (q.Test(HkWmsKind) != 1 && Environment.TickCount64 < until) RenderBlocks(q, 0.1);
                RenderBlocks(q, 0.3);
                t.Expect(q.GetName(Fpn.ParamValue, PCore, q.GetParam(PCore)) == WmsChipId && q.Test(HkWmsKind) == 1 && WmsDescription(q).StartsWith("WideMess"), $"a SNES file opened on the WMS chip runs on the WideMess console: chip {q.GetName(Fpn.ParamValue, PCore, q.GetParam(PCore))}, session {q.Test(HkWmsKind)}, {WmsDescription(q)}");
            }
            finally { File.Delete(sfc); }
        }

        // ---- the milestone-2 picture ROMs: the plugin's picture is the spec's model's, pixel for pixel (the same hash as the PNG) ----
        foreach (var rom in new[] { "sprites", "widesnes" })
        {
            long want = PngPictureHash(Path.Combine(bin, rom + ".expect.png"), out int ew, out int eh);
            using var p = WmsGame(Path.Combine(bin, rom + ".wms"));
            if (p == null) { t.Expect(false, $"{rom}.wms did not load"); continue; }
            long got = 0;
            for (int k = 0; k < 20 && got != want; k++) { RenderBlocks(p, 0.1); got = p.Test(10); }
            t.Expect(got == want && ew == 400 && eh == 224, $"{rom}.wms: the plugin's picture is {(got == want ? "" : "NOT ")}the spec's model's {rom}.expect.png ({ew} x {eh}) pixel for pixel");
        }

        // ---- the sound: the WideMess voices (the unlocked sound ROM against its locked control), and a .wms game on the SNES chip: the new voices are not heard, and the status says so ----
        double RmsOfGame(string file, string chip, out string describe)
        {
            using var p = WmsGame(Path.Combine(bin, file), chip);
            describe = "";
            if (p == null) return -1;
            RenderBlocks(p, 1.0);
            var a = RenderBlocks(p, 6.0);
            describe = WmsDescription(p);
            return Audio.Rms(a, 0, a.Length);
        }
        double on = RmsOfGame("sound.wms", WmsChipId, out string dOn), off = RmsOfGame("sound_nounlock.wms", WmsChipId, out _), onSnes = RmsOfGame("sound.wms", "SNES", out string dSnes);
        t.Expect(on > 0.01 && on > off * 1.5 && dOn.Contains("APU WMS") && dOn.Contains("| unlocked") && !dOn.Contains("no WideMess sound"), $"sound.wms on the WMS chip plays the WideMess voices: rms {on:0.0000} against the locked control's {off:0.0000} ({dOn})");
        t.Expect(onSnes >= 0 && onSnes < on * 0.67 && dSnes.Contains("no WideMess sound"), $"the control: sound.wms on the SNES chip loses the new voices (rms {onSnes:0.0000}) and says so: {dSnes}");
        t.Note($"sound.wms through Mix: rms {on:0.0000} on the WMS chip, {off:0.0000} for the locked control, {onSnes:0.0000} on the SNES chip");

        // ---- a SNES game on the WMS chip is the SNES game: frame for frame the same pictures as on the SNES chip, and the same sound ----
        {
            string lib0 = @"X:\EMULATION\Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc";
            string? smw = Environment.GetEnvironmentVariable("BROKENNES_TEST_SMW") is { Length: > 0 } e && File.Exists(e) ? e : File.Exists(lib0) ? lib0 : null;
            string tag = Guid.NewGuid().ToString("N");
            string game = smw ?? Path.Combine(Path.GetTempPath(), $"bn2_{tag}.sfc");
            if (smw == null) File.WriteAllBytes(game, MakeSnesRom());
            try
            {
                (List<long> pics, float[] snd, long kind) Run(string chip)
                {
                    using var p = WmsGame(game, chip)!;
                    p.Test(120, 0, 1);                                   // the editor does not run the game: the frames counted are the test's
                    p.Test(43);                                          // from power-on, at a known moment: Reset Console, then the fresh game is taken between frames
                    var until = Environment.TickCount64 + 20000;
                    while (p.Test(83, 0, 0) != 0 && Environment.TickCount64 < until) Thread.Sleep(20);
                    var pics = new List<long>();
                    for (int k = 0; k < 10; k++) { p.Test(83, 0, 60); pics.Add(p.Test(10)); }
                    long kind = p.Test(HkWmsKind);
                    var snd = RenderBlocks(p, 3.0);
                    p.Test(120, 0, 0);
                    return (pics, snd, kind);
                }
                var a = Run("SNES"); var b = Run(WmsChipId);
                bool samePics = a.pics.SequenceEqual(b.pics) && a.pics.Distinct().Count() > (smw != null ? 3 : 0), sameSnd = a.snd.SequenceEqual(b.snd);
                t.Expect(a.kind == 0 && b.kind == 1 && samePics && sameSnd, $"{(smw != null ? "Super Mario World" : "a test-made SNES ROM")} on the WMS chip (the WideMess console, session {b.kind}) against the SNES chip (session {a.kind}): pictures at 10 points over 600 frames {(samePics ? "equal" : "DIFFER")}, 3 s of sound {(sameSnd ? "equal" : "DIFFER")}");
                if (smw == null) t.Warn(false, "Super Mario World not found (X: or BROKENNES_TEST_SMW): a test-made SNES ROM (no picture, no sound) stood in");
                t.Note($"{(smw != null ? "Super Mario World" : "test-made SNES ROM")} on the WMS chip = on the SNES chip: {a.pics.Distinct().Count()} distinct pictures at 10 points of 600 frames, {a.snd.Length:N0} samples of sound (rms {Audio.Rms(a.snd, 0, a.snd.Length):0.0000}), all equal");
            }
            finally { if (smw == null) File.Delete(game); }
        }

        // ---- the sound chip swapped under a running WideMess game: WMS -> SNES keeps the game running (no restart) and says the new voices are gone; back to WMS ----
        using (var p = WmsGame(Path.Combine(bin, "sound.wms")))
        {
            if (p != null)
            {
                RenderBlocks(p, 1.0);
                long f1 = p.Test(11);
                SetChip(p, "SNES");
                RenderBlocks(p, 1.0);
                long f2 = p.Test(11);
                string d = WmsDescription(p);
                t.Expect(f2 > f1 + 40 && p.Test(HkWmsKind) == 1 && d.Contains("APU SFC"), $"a chip swap under a running WideMess game: frames {f1} -> {f2} (no restart), session {p.Test(HkWmsKind)}, {d}");
            }
            else t.Expect(false, "sound.wms did not load (chip swap)");
        }

        // ---- Instrument Runaway on a WideMess game: the WideMess sampler lists its 16-bit PCM and BRR instruments from both directories, and FL's notes play them at their pitch ----
        using (var p = WmsGame(Path.Combine(bin, "sound.wms")))
        {
            if (p == null) { t.Expect(false, "sound.wms did not load (Runaway)"); return; }
            RenderBlocks(p, 2.0);
            p.Test(47, 0, 1);
            RenderBlocks(p, 0.2);
            long count = p.Test(HkWmsCount);
            int pcm = 0, brrWide = 0, snes = 0, pcmSlot8 = -1;
            for (int i = 0; i < count; i++) { long info = p.Test(HkWmsInfo, i); if ((info & 1 << 16) != 0) { if ((info & 1 << 17) != 0) { pcm++; if ((info & 0xFFFF) == 8) pcmSlot8 = i; } else brrWide++; } else snes++; }
            t.Expect(p.Test(HkWmsRunaway) == 1 && p.Test(48) == 0 && count > 0 && pcm >= 12 && brrWide >= 12 && snes >= 1, $"Runaway on sound.wms: the WideMess sampler ({p.Test(HkWmsRunaway)}) lists {count} instruments: {pcm} 16-bit PCM and {brrWide} BRR from the new voices' directory, {snes} from the S-DSP's");
            t.Expect(pcmSlot8 >= 0 && p.Test(HkWmsLength, pcmSlot8) == 32, $"the PCM sample of entry 8 (voice 8's: 32 samples, looped) is listed with its length ({(pcmSlot8 >= 0 ? p.Test(HkWmsLength, pcmSlot8) : -1)})");
            t.Note($"Runaway on sound.wms: {count} instruments ({pcm} 16-bit PCM and {brrWide} BRR from the new voices' directory, {snes} from the S-DSP's)");
            // entry 8 is a 16-bit sine with one cycle in 32 samples (WideMess/testroms/sound/gen_data.py): 1,000 Hz at its own rate. C5 plays it at its own rate, A4 three semitones lower, C6 an octave higher
            p.SetParam(PInstrument, pcmSlot8);
            foreach (var (key, want) in new[] { (60, 1000.0), (57, 1000.0 * Math.Pow(2, -0.25)), (72, 2000.0) })
            {
                var s = One(key, 0, 0.6);
                long snd = -1, hld = -1;
                var mixer = new Mixer { SampleRate = 48000, BlockSize = 512, AfterBlock = sm => { if (sm >= 12000 && snd < 0) { snd = p.Test(HkWmsSounding); hld = p.Test(HkWmsHeld); } } };
                var audio = mixer.Run(new List<PluginInstance> { p }, s);
                var mono = Mono(audio);
                int from = mixer.Log[0].StartSample + 4000, to = mixer.Log[0].EndSample - 2000;
                double at = Goertzel(mono, 48000, want, from, to), lo = Goertzel(mono, 48000, want * Math.Pow(2, -1 / 12.0), from, to), hi = Goertzel(mono, 48000, want * Math.Pow(2, 1 / 12.0), from, to);
                t.Expect(at > lo * 5 && at > hi * 5 && at > 0.002, $"Runaway: the game's PCM sample at key {key} should peak at {want:0.0} Hz: {at:0.0000} there, {lo:0.0000} a semitone below, {hi:0.0000} above (rms {Audio.Rms(mono, from, to):0.0000}, {snd} new voices sounding, {hld} notes holding one, the instrument {p.Test(HkWmsPlays)})");
            }
            // many notes at once from the game's instrument: 6 notes, 6 of the chip's new voices
            {
                var s = new Scenario { Name = "wms-runaway-chord", Tempo = 60, LengthBeats = 1.2 };
                foreach (int k in new[] { 48, 52, 55, 60, 64, 67 }) s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1.0, Key = k, Channel = 0, Velocity = 0.5 });
                long held = -1, alloc0 = 0, alloc1 = -1;
                var mixer = new Mixer { SampleRate = 48000, BlockSize = 512, AfterBlock = sm => { if (sm >= 12000 && held < 0) { held = p.Test(HkWmsHeld); alloc0 = AllocStart(p); } if (sm >= 40000 && alloc1 < 0) alloc1 = p.Test(4); } };
                mixer.Run(new List<PluginInstance> { p }, s);
                t.Expect(held == 6, $"Runaway: a 6-note chord holds {held} of the chip's voices (expected 6)");
                t.Expect(alloc1 - alloc0 == 0, $"Runaway on a WideMess game: {alloc1 - alloc0:N0} bytes allocated on the audio thread while 6 notes play (must be 0)");
            }
            // the project keeps the WideMess instruments (section WMS1): saved, reopened, the same bytes; a damaged section is refused with a note and nothing half used
            long hash = p.Test(HkWmsHash);
            t.Expect(p.Test(HkWmsRoundTrip) == 1 && p.Test(HkWmsHash) == hash && p.Test(HkWmsRunaway) == 1, "the WideMess snapshot's project form (WMS1) should read back to the same bytes");
            byte[] saved = p.SaveState();
            int at8 = IndexOfTag(saved, "WMS1");
            t.Expect(at8 > 0, "a project saved in Runaway on a WideMess game should carry a WMS1 section");
            p.Dispose();
            using (var q = lib.Create())
            {
                q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                q.LoadState(saved);
                var until = Environment.TickCount64 + 20000;
                while (q.Test(HkWmsRunaway) != 1 && Environment.TickCount64 < until) { RenderBlocks(q, 0.1); }
                t.Expect(q.Test(HkWmsRunaway) == 1 && q.Test(HkWmsHash) == hash && q.Test(HkWmsCount) == count, $"the reopened project is frozen with the same WideMess instruments (hash equal: {q.Test(HkWmsHash) == hash}, {q.Test(HkWmsCount)} of {count})");
            }
            if (at8 > 0)
            {
                var bad = (byte[])saved.Clone();
                bad[at8 + 8 + 40] ^= 0x5A;                              // a byte of the deflated memory: the CRC must refuse it
                using var q = lib.Create();
                q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                q.LoadState(bad);
                RenderBlocks(q, 0.5);
                var c = new char[600]; string note; fixed (char* cp = c) { long n = q.Test(HkWmsNote, c.Length, (nint)cp); note = n <= 0 ? "" : new string(c, 0, (int)n); }
                t.Expect(q.Test(HkWmsRunaway) == 0 && note.Contains("WideMess instruments were not restored") && note.Contains("damaged"), $"a damaged WMS1 section must be refused with a note: WideMess sampler {q.Test(HkWmsRunaway)}, note \"{note}\"");
            }
        }

        // ---- the negative control: Runaway on the locked control ROM is the SNES sampler's (its WideMess part never unlocked) ----
        using (var p = WmsGame(Path.Combine(bin, "sound_nounlock.wms")))
        {
            if (p != null)
            {
                RenderBlocks(p, 1.5);
                p.Test(47, 0, 1);
                RenderBlocks(p, 0.2);
                t.Expect(p.Test(HkWmsRunaway) == 0 && p.Test(48) == 1, $"the control: Runaway on the locked sound ROM should be the SNES sampler (WideMess {p.Test(HkWmsRunaway)}, SNES {p.Test(48)})");
            }
            else t.Expect(false, "sound_nounlock.wms did not load");
        }
    }

    private static int IndexOfTag(byte[] b, string tag)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(tag);
        for (int i = 0; i + 4 <= b.Length; i++) if (b[i] == t[0] && b[i + 1] == t[1] && b[i + 2] == t[2] && b[i + 3] == t[3]) return i;
        return -1;
    }
}
