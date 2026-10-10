// Round 3 (track C3b): Instrument Runaway on Sega games. A Master System, Game Gear or Genesis game is frozen, its sound chips are copied (the PSG's registers, the YM2413's, the YM2612's channel patches and the DAC sounds found in its
// writes) and FL's notes play them on chips of the instance's own. Tiny ROMs made by the test stand in for games; what is measured is the pitch, the level, the polyphony, the slides, the saved project and the allocation of the notes.
using BrokenNes.Fruity;
using BrokenNes.SynthHost;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const double SegaMaster = 53693175.0;

    /// <summary>The frequency of a MIDI key (key 69 = A = 440 Hz).</summary>
    private static double KeyHz(double key) => 440.0 * Math.Pow(2.0, (key - 69.0) / 12.0);

    /// <summary>The note the SN76489 plays for a wanted pitch: the nearest 10-bit period of the 3,579,545 Hz clock (the tone is clock / 32 / period).</summary>
    internal static double PsgHz(double hz)
    {
        double clock = SegaMaster / 15;
        int n = (int)Math.Round(clock / (32.0 * hz));
        return clock / (32.0 * n);
    }

    /// <summary>The note the YM2413 plays for a wanted pitch at the Master System's clock: the nearest of its 9-bit F-numbers over the eight blocks (F-number x 2^block x clock / 72 / 2^19).</summary>
    internal static double OpllHz(double hz)
    {
        double unit = SegaMaster / 15 / 72 / 524288, best = 0, bestErr = double.MaxValue;
        for (int blk = 0; blk < 8; blk++)
        {
            int f = (int)Math.Round(hz / unit / (1 << blk));
            if (f < 1 || f > 511) continue;
            double v = f * (1 << blk) * unit, err = Math.Abs(Math.Log(v / hz));
            if (err < bestErr - 1e-12) { bestErr = err; best = v; }
        }
        return best;
    }

    /// <summary>The note the YM2612 plays for a wanted pitch at the Genesis' clock: the nearest of its 11-bit F-numbers over the eight blocks (F-number x 2^block / 2 x clock / 144 / 2^20).</summary>
    internal static double OpnHz(double hz)
    {
        double unit = SegaMaster / 7 / 144 / 1048576 / 2, best = 0, bestErr = double.MaxValue;
        for (int blk = 0; blk < 8; blk++)
        {
            int f = (int)Math.Round(hz / unit / (1 << blk));
            if (f < 1 || f > 2047) continue;
            double v = f * (1 << blk) * unit, err = Math.Abs(Math.Log(v / hz));
            if (err < bestErr - 1e-12) { bestErr = err; best = v; }
        }
        return best;
    }

    /// <summary>The frequency of the strongest component within 3% of a guess (a Goertzel scan, refined by the parabola through the peak): for notes with a rich timbre, where the zero crossings count the harmonics too.</summary>
    private static double ScanHz(float[] x, double guess, int from, int to)
    {
        double step = guess * 0.0005, bestHz = guess, best = -1;
        var amp = new double[121];
        for (int i = 0; i < amp.Length; i++)
        {
            double hz = guess * 0.97 + i * step;
            amp[i] = Goertzel(x, 48000, hz, from, to);
            if (amp[i] > best) { best = amp[i]; bestHz = hz; }
        }
        int k = (int)Math.Round((bestHz - guess * 0.97) / step);
        if (k > 0 && k < amp.Length - 1)
        {
            double d = amp[k - 1] - 2 * amp[k] + amp[k + 1];
            if (Math.Abs(d) > 1e-15) bestHz += 0.5 * (amp[k - 1] - amp[k + 1]) / d * step;
        }
        return bestHz;
    }

    /// <summary>Plays one note of the instance and returns its mono audio and the log of the note.</summary>
    private static (float[] Audio, NoteLog Log) PlayNote(PluginInstance p, double key, double velocity = 0.78, double seconds = 0.5)
    {
        var m = new Mixer { SampleRate = 48000 };
        var a = Mono(m.Run(p, One(key, 0, seconds, velocity)));
        return (a, m.Log[0]);
    }

    private static double NoteRms(PluginInstance p, double key, double velocity, double seconds = 0.5)
    {
        var (a, log) = PlayNote(p, key, velocity, seconds);
        return Audio.Rms(a, log.StartSample + 6000, log.EndSample - 3000);
    }

    private static double NoteZeroCrossHz(PluginInstance p, double key, double velocity = 0.78)
    {
        var (a, log) = PlayNote(p, key, velocity);
        return Audio.Frequency(a, 48000, log.StartSample + 6000, log.EndSample - 3000);
    }

    /// <summary>Notes at the same time, one second long each (the instance's chord).</summary>
    private static float[] PlayChord(PluginInstance p, double velocity, params double[] keys)
    {
        var s = new Scenario { Name = "chord", Tempo = 60, LengthBeats = 1.5 };
        foreach (var k in keys) s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1.0, Key = k, Channel = 0, Velocity = velocity });
        return Mono(new Mixer { SampleRate = 48000 }.Run(p, s));
    }

    /// <summary>The octave's strength over the fundamental's.</summary>
    private static double SecondHarmonic(float[] x, double fundamentalHz, int from, int to) => ToneAmp(x, 2 * fundamentalHz, from, to) / Math.Max(1e-9, ToneAmp(x, fundamentalHz, from, to));

    /// <summary>Removes a temporary game file; a game that is still being read by an emulator that was just closed is given a moment (a leftover temp file is harmless, a test that dies on cleaning up is not).</summary>
    private static void DeleteQuietly(string path)
    {
        for (int i = 0; i < 20; i++)
        {
            try { File.Delete(path); return; }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }

    private static int LastIndexOf(byte[] data, byte[] pattern)
    {
        for (int i = data.Length - pattern.Length; i >= 0; i--)
        {
            int k = 0;
            while (k < pattern.Length && data[i + k] == pattern[k]) k++;
            if (k == pattern.Length) return i;
        }
        return -1;
    }

    private void RunawaySega(T t)
    {
        RunawaySegaPsg(t);
        RunawaySegaOpll(t);
        RunawaySegaOpn(t);
        RunawaySegaCost(t);
        RunawaySegaGames(t);
    }

    // ------------------------------------------------------------------------------------------------
    // real games, when named (BROKENNES_TEST_GAMES = files separated by ';'): informational, a game is not in the repository

    private void RunawaySegaGames(T t)
    {
        string? games = Environment.GetEnvironmentVariable("BROKENNES_TEST_GAMES");
        if (string.IsNullOrEmpty(games)) return;
        foreach (var path in games.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            int console = ext is ".md" or ".gen" or ".smd" or ".bin" ? 4 : ext is ".sms" or ".gg" ? 3 : -1;
            if (console < 0 || !File.Exists(path)) continue;
            string chip = console == 4 ? "MD" : "SMS";
            using var p = SegaGame(path, console, chip);
            RenderBlocks(p, 12.0);
            if (p.Test(12) != 0) { t.Expect(false, $"{Path.GetFileName(path)}: the game crashed"); continue; }
            p.Test(47, 0, 1);
            int kind = (int)p.Test(71), count = (int)p.Test(72);
            t.Expect(p.Test(45) == 1 && kind != 0 && count > 0, $"{Path.GetFileName(path)}: Runaway captured nothing (runaway {p.Test(45)}, kind {kind}, instruments {count})");
            if (kind == 0) continue;
            int dac = (int)p.Test(74);
            var audible = new List<string>(); int finite = 0, silent = 0;
            for (int i = 0; i < count; i++)
            {
                p.SetParam(PInstrument, i + 1);
                var (a, log) = PlayNote(p, 60, 0.9, 0.4);
                if (a.All(float.IsFinite) && Audio.Peak(a, 0, a.Length) < 4) finite++;
                double rms = Audio.Rms(a, log.StartSample + 4000, log.EndSample - 2000);
                if (rms > 0.003) audible.Add(i.ToString()); else silent++;
            }
            t.Expect(finite == count, $"{Path.GetFileName(path)}: {count - finite} of {count} instruments gave non-finite or runaway audio");
            byte[] saved = p.SaveState();
            t.Note($"{Path.GetFileName(path)}: Runaway kind {kind}, {count} instruments ({dac} DAC sounds{(dac > 0 ? $", the first {p.Test(76, 0)} samples looped at {p.Test(75, 0) / 100.0:0.0} Hz" : "")}); {audible.Count} sound at C5, {silent} do not; the project is {saved.Length:N0} bytes");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // the Master System / Game Gear PSG

    private void RunawaySegaPsg(T t)
    {
        string sms = TempGame(".sms", MakeSmsRom2(fm: false));
        try
        {
            byte[] saved;
            using (var p = SegaGame(sms, 3, "SMS"))
            {
                RenderBlocks(p, 0.6);
                t.Expect(p.Test(12) == 0 && p.Test(11) > 20, "the Master System test game did not run");
                p.Test(47, 0, 1);
                t.Expect(p.Test(45) == 1 && p.Test(71) == 1, $"Runaway on a Master System game should capture its PSG (runaway {p.Test(45)}, snapshot kind {p.Test(71)})");
                t.Expect(p.Test(72) == 3, $"the PSG offers {p.Test(72)} instruments, expected 3 (square tones, white noise, periodic noise)");
                t.Expect(p.Test(17) == 5, $"in Sega Runaway the instance should sit on the Sampler channel, not {p.Test(17)}");
                var frozen = RenderBlocks(p, 0.6);
                t.Expect(Audio.Peak(frozen, 0, frozen.Length) < 1e-3, "the frozen game's own tone should be silent");

                // pitch: the chip's nearest period, within a cent
                foreach (double key in new[] { 69.0, 81.0, 57.0, 45.0, 93.0 })
                    t.NearCents($"PSG key {key}", NoteZeroCrossHz(p, key), PsgHz(KeyHz(key)), 1.0);
                // level: 2 dB a step
                double r1 = NoteRms(p, 69, 1.0), r2 = NoteRms(p, 69, 0.5), r4 = NoteRms(p, 69, 0.25);
                double d2 = 20 * Math.Log10(r2 / r1), d4 = 20 * Math.Log10(r4 / r1);
                t.Expect(Math.Abs(d2 + 6.0) < 0.6 && Math.Abs(d4 + 12.0) < 0.6, $"velocity 0.5 and 0.25 should play 6 dB and 12 dB down (2 dB steps): {d2:0.00} and {d4:0.00} dB");
                t.Expect(r1 > 0.02, $"the full-level PSG tone is too quiet (RMS {r1:0.0000})");
                t.Note($"PSG Runaway: level steps {d2:0.00}/{d4:0.00} dB for velocity 0.5/0.25; full level RMS {r1:0.000}");
                // below the chip's range: silent and counted
                var low = PlayNote(p, 33, 1.0);
                t.Expect(Audio.Peak(low.Audio, low.Log.StartSample + 6000, low.Log.EndSample - 3000) < 1e-3 && p.Test(73) >= 1, $"a 55 Hz note is below the PSG's range: it should be silent and counted (dropped {p.Test(73)})");
                // polyphony: three tones at once
                var chord = PlayChord(p, 1.0, 69, 73, 76);
                bool all3 = new[] { 69.0, 73.0, 76.0 }.All(k => HasTone(chord, PsgHz(KeyHz(k)), 12000, 36000, 3).Present);
                t.Expect(all3, "a chord on the PSG should have all three tones");
                var single = PlayChord(p, 1.0, 69);
                t.Expect(Audio.Rms(chord, 12000, 36000) > Audio.Rms(single, 12000, 36000) * 1.3, "a chord should be louder than one note");
                // a fourth note takes over the oldest tone voice: no crash, still sounding
                var four = PlayChord(p, 1.0, 60, 64, 67, 72);
                t.Expect(four.All(float.IsFinite) && Audio.Rms(four, 12000, 36000) > 0.02 && p.Test(77) == 0, $"four notes on three voices: finite and sounding, voices handed back ({p.Test(77)} still hold a slot)");
                // the noise: white (instrument 2 -> parameter 2) and periodic (parameter 3, buzzing at the note)
                p.SetParam(PInstrument, 2);
                var white = PlayNote(p, 60, 1.0);
                t.Expect(Audio.Rms(white.Audio, white.Log.StartSample + 6000, white.Log.EndSample - 3000) > 0.01, "white noise should sound");
                p.SetParam(PInstrument, 3);
                var per = PlayNote(p, 69, 1.0);
                double buzz = SegaMaster / 15 / (512.0 * 16);
                var hasBuzz = HasTone(per.Audio, buzz, per.Log.StartSample + 6000, per.Log.EndSample - 3000, 3);
                t.Expect(hasBuzz.Present, $"periodic noise should buzz near the note: {hasBuzz.Detail}");
                p.SetParam(PInstrument, 0);
                // a slide follows
                var slide = new Scenario { Name = "slide", Tempo = 60, LengthBeats = 2 };
                slide.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1.5, Key = 57, Channel = 0, Velocity = 1.0, Slides = new() { new SlideSeg { StartBeat = 0.2, LengthBeats = 1.0, ToKey = 69 } } });
                var ms = new Mixer { SampleRate = 48000 };
                var sa = Mono(ms.Run(p, slide));
                var track = Audio.Track(sa, 48000, 120, 1500, 40, 40).Where(x => !double.IsNaN(x.Hz)).ToList();
                t.Expect(track.Count > 20 && Audio.Cents(track[0].Hz, 220) is > -45 and < 45 && Audio.Cents(track[^1].Hz, 440) is > -45 and < 45, $"a slide up an octave should be followed: {track.FirstOrDefault().Hz:0} Hz to {track.LastOrDefault().Hz:0} Hz over {track.Count} windows");
                t.Expect(p.Test(3) == 0, "voices were not handed back");

                // the picker through the editor: Auto and the three instruments
                nint parent = Win32Host.CreateParent();
                try
                {
                    p.ShowEditor(parent);
                    for (int k = 0; k < 4; k++) { RenderBlocks(p, 0.1); Win32Host.Pump(40); }
                    if (ClickControl(t, p, 90, "Instrument dropdown"))
                    {
                        t.Expect(p.Test(31) == 90 && p.Test(32) == 4, $"the Instrument dropdown lists {p.Test(32)} entries, expected Auto and the PSG's three");
                        var item = Centre(p, 100 + 2);
                        if (item != null) Win32Host.Click(p.EditorHandle, item.Value.X, item.Value.Y);
                        t.Expect(p.GetParam(PInstrument) == 2, $"picking the second instrument left the parameter at {p.GetParam(PInstrument)} (expected 2: the instrument number + 1)");
                    }
                    p.HideEditor();
                }
                finally { Win32Host.DestroyParent(parent); }
                p.SetParam(PInstrument, 0);
                saved = p.SaveState();
            }
            // the project remembers it: reopened, the game is frozen and the PSG plays
            using (var q = lib.Create())
            {
                q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                q.LoadState(saved);
                t.Expect(q.Test(45) == 1 && q.Test(71) == 1, $"the saved project did not come back in Runaway (runaway {q.Test(45)}, kind {q.Test(71)})");
                t.NearCents("restored PSG key 69", NoteZeroCrossHz(q, 69), PsgHz(440), 1.0);
                q.Test(47, 0, 0);
                RenderBlocks(q, 0.3);
                t.Expect(q.Test(45) == 0 && q.Test(17) == 4, $"after releasing: runaway {q.Test(45)}, channel {q.Test(17)}");
            }
        }
        finally { DeleteQuietly(sms); }
    }

    // ------------------------------------------------------------------------------------------------
    // the Master System's YM2413

    private void RunawaySegaOpll(T t)
    {
        string sms = TempGame(".sms", MakeSmsRom2(fm: true));
        try
        {
            byte[] saved;
            using (var p = SegaGame(sms, 3, "SMSFM"))
            {
                RenderBlocks(p, 0.6);
                p.Test(47, 0, 1);
                t.Expect(p.Test(45) == 1 && p.Test(71) == 2, $"Runaway on a Master System FM game should capture the YM2413 (runaway {p.Test(45)}, snapshot kind {p.Test(71)})");
                t.Expect(p.Test(72) == 19, $"the YM2413 unit offers {p.Test(72)} instruments, expected 19 (user instrument, 15 of the chip's own, PSG tones, white and periodic noise)");
                t.Expect(p.Test(78) == 0, $"the game was playing its user instrument: Auto should be instrument 0, not {p.Test(78)}");
                var frozen = RenderBlocks(p, 0.6);
                t.Expect(Audio.Peak(frozen, 0, frozen.Length) < 1e-3, "the frozen game's own FM should be silent");

                // the game's own (user) instrument, a sine: the chip's nearest F-number, within a cent
                foreach (double key in new[] { 69.0, 57.0, 81.0, 45.0 })
                    t.NearCents($"YM2413 user instrument key {key}", NoteZeroCrossHz(p, key), OpllHz(KeyHz(key)), 1.0);
                // level: 3 dB a step
                double r1 = NoteRms(p, 69, 1.0), r2 = NoteRms(p, 69, 0.5), r4 = NoteRms(p, 69, 0.25);
                double d2 = 20 * Math.Log10(r2 / r1), d4 = 20 * Math.Log10(r4 / r1);
                t.Expect(Math.Abs(d2 + 6.0) < 0.7 && Math.Abs(d4 + 12.0) < 0.7, $"velocity 0.5 and 0.25 should play 6 dB and 12 dB down (3 dB steps): {d2:0.00} and {d4:0.00} dB");
                t.Expect(r1 > 0.02, $"the full-level FM tone is too quiet (RMS {r1:0.0000})");
                t.Note($"YM2413 Runaway: level steps {d2:0.00}/{d4:0.00} dB for velocity 0.5/0.25; full level RMS {r1:0.000}");
                // the user instrument is a pure sine: nothing at the octave
                var userNote = PlayNote(p, 69, 1.0);
                double userH2 = SecondHarmonic(userNote.Audio, OpllHz(440), userNote.Log.StartSample + 6000, userNote.Log.EndSample - 3000);
                t.Expect(userH2 < 0.02, $"the game's user instrument is a sine: its octave should be nearly absent ({userH2:0.000} of the fundamental)");
                // a built-in instrument of the chip: the flute (instrument 4 -> parameter 5), a 1:1 pair, richer than the sine
                p.SetParam(PInstrument, 5);
                var flute = PlayNote(p, 69, 1.0);
                t.NearCents("YM2413 flute key 69", ScanHz(flute.Audio, 440, flute.Log.StartSample + 6000, flute.Log.EndSample - 3000), OpllHz(440), 3.0);
                t.Expect(Audio.Rms(flute.Audio, flute.Log.StartSample + 6000, flute.Log.EndSample - 3000) > 0.01, "the flute should sound");
                double fluteH2 = SecondHarmonic(flute.Audio, OpllHz(440), flute.Log.StartSample + 6000, flute.Log.EndSample - 3000);
                t.Expect(fluteH2 > 0.05, $"the chip's flute is an FM pair, not the game's sine: its octave should be there ({fluteH2:0.000} of the fundamental)");
                t.Note($"YM2413 timbre: octave over fundamental {userH2:0.000} for the user sine, {fluteH2:0.000} for the flute");
                // the synthesizer bass (instrument 12 -> parameter 13) has a carrier at half the base: the note must still be the note
                p.SetParam(PInstrument, 13);
                var bass = PlayNote(p, 45, 1.0);
                t.NearCents("YM2413 synthesizer bass key 45", ScanHz(bass.Audio, 110, bass.Log.StartSample + 6000, bass.Log.EndSample - 3000), OpllHz(110 * 2) / 2, 8.0);
                p.SetParam(PInstrument, 0);
                // polyphony, then more notes than channels
                var chord = PlayChord(p, 1.0, 69, 73, 76);
                bool all3 = new[] { 69.0, 73.0, 76.0 }.All(k => HasTone(chord, OpllHz(KeyHz(k)), 12000, 36000, 3).Present);
                t.Expect(all3, "a chord on the YM2413 should have all three tones");
                var big = PlayChord(p, 0.6, 48, 52, 55, 60, 64, 67, 72, 76, 79, 84, 88);
                t.Expect(big.All(float.IsFinite) && Audio.Rms(big, 12000, 36000) > 0.02 && p.Test(77) == 0, $"eleven notes on nine channels: finite and sounding, voices handed back ({p.Test(77)} still hold a slot)");
                // above the chip's top: silent and counted
                var high = PlayNote(p, 120, 1.0);
                t.Expect(Audio.Peak(high.Audio, high.Log.StartSample + 6000, high.Log.EndSample - 3000) < 1e-3 && p.Test(73) >= 1, $"a 10 kHz note is above the YM2413's range: it should be silent and counted (dropped {p.Test(73)})");
                // the PSG tones of the same unit (instrument 16 -> parameter 17)
                p.SetParam(PInstrument, 17);
                t.NearCents("PSG tones of the FM unit, key 69", NoteZeroCrossHz(p, 69), PsgHz(440), 1.0);
                p.SetParam(PInstrument, 0);
                // a slide on the FM voice follows
                var slide = new Scenario { Name = "slide", Tempo = 60, LengthBeats = 2 };
                slide.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1.5, Key = 57, Channel = 0, Velocity = 1.0, Slides = new() { new SlideSeg { StartBeat = 0.2, LengthBeats = 1.0, ToKey = 69 } } });
                var sa = Mono(new Mixer { SampleRate = 48000 }.Run(p, slide));
                var track = Audio.Track(sa, 48000, 120, 1500, 40, 40).Where(x => !double.IsNaN(x.Hz)).ToList();
                t.Expect(track.Count > 20 && Audio.Cents(track[0].Hz, 220) is > -45 and < 45 && Audio.Cents(track[^1].Hz, 440) is > -45 and < 45, $"a slide up an octave should be followed: {track.FirstOrDefault().Hz:0} Hz to {track.LastOrDefault().Hz:0} Hz over {track.Count} windows");
                // the picker: Auto, the user instrument, the chip's fifteen, the PSG's three
                nint parent = Win32Host.CreateParent();
                try
                {
                    p.ShowEditor(parent);
                    for (int k = 0; k < 4; k++) { RenderBlocks(p, 0.1); Win32Host.Pump(40); }
                    if (ClickControl(t, p, 90, "Instrument dropdown"))
                    {
                        t.Expect(p.Test(31) == 90 && p.Test(32) == 20, $"the Instrument dropdown lists {p.Test(32)} entries, expected 20 (Auto and 19)");
                        Win32Host.Click(p.EditorHandle, 1, 1);
                    }
                    p.HideEditor();
                }
                finally { Win32Host.DestroyParent(parent); }
                p.SetParam(PInstrument, 5);
                saved = p.SaveState();
            }
            using (var q = lib.Create())
            {
                q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                q.LoadState(saved);
                t.Expect(q.Test(45) == 1 && q.Test(71) == 2 && q.GetParam(PInstrument) == 5, $"the saved project did not come back in Runaway on the flute (runaway {q.Test(45)}, kind {q.Test(71)}, instrument {q.GetParam(PInstrument)})");
                var r = PlayNote(q, 69, 1.0);
                t.NearCents("restored YM2413 flute key 69", ScanHz(r.Audio, 440, r.Log.StartSample + 6000, r.Log.EndSample - 3000), OpllHz(440), 3.0);
                t.Expect(SecondHarmonic(r.Audio, OpllHz(440), r.Log.StartSample + 6000, r.Log.EndSample - 3000) > 0.05, "the restored project should play the flute, not the sine");
                q.SetParam(PInstrument, 0);
                t.NearCents("restored YM2413 user instrument key 69", NoteZeroCrossHz(q, 69), OpllHz(440), 1.0);
            }
        }
        finally { DeleteQuietly(sms); }
    }

    // ------------------------------------------------------------------------------------------------
    // the Genesis' YM2612 and DAC

    private void RunawaySegaOpn(T t)
    {
        string md = TempGame(".md", MakeMdRom(fm: true, dac: true));
        try
        {
            byte[] saved;
            int dacCount, instruments;
            using (var p = SegaGame(md, 4, "MD"))
            {
                RenderBlocks(p, 1.2);
                t.Expect(p.Test(12) == 0 && p.Test(11) > 50, "the Genesis test game did not run");
                p.Test(47, 0, 1);
                t.Expect(p.Test(45) == 1 && p.Test(71) == 3, $"Runaway on a Genesis game should capture the YM2612 and its DAC sounds (runaway {p.Test(45)}, snapshot kind {p.Test(71)})");
                dacCount = (int)p.Test(74); instruments = (int)p.Test(72);
                t.Expect(dacCount >= 1 && instruments == 6 + dacCount + 3, $"found {dacCount} DAC sounds and {instruments} instruments (expected 6 + sounds + 3)");
                double cycle = p.Test(75, 0) / 100.0;
                long loop = p.Test(76, 0);
                t.Expect(loop == 32 && cycle is > 300 and < 600, $"the game streams a 32-sample sine to the DAC: the sound found has a loop of {loop} samples at {cycle:0.0} Hz (expected 32 samples, about 415 Hz)");
                t.Expect(p.Test(78) == 0, $"the game was playing FM 1: Auto should be instrument 0, not {p.Test(78)}");
                var frozen = RenderBlocks(p, 0.6);
                t.Expect(Audio.Peak(frozen, 0, frozen.Length) < 1e-3, "the frozen game's own FM and DAC should be silent");
                t.Note($"Genesis Runaway: {dacCount} DAC sound(s) found, loop {loop} samples at {cycle:0.0} Hz, {instruments} instruments");

                // FM 1's patch (a sine on the carrier): the chip's nearest F-number, within a cent
                foreach (double key in new[] { 69.0, 57.0, 81.0, 45.0 })
                    t.NearCents($"YM2612 FM patch key {key}", NoteZeroCrossHz(p, key), OpnHz(KeyHz(key)), 1.0);
                // level: 0.75 dB a step of the total level
                double r1 = NoteRms(p, 69, 1.0), r2 = NoteRms(p, 69, 0.5), r4 = NoteRms(p, 69, 0.25);
                double d2 = 20 * Math.Log10(r2 / r1), d4 = 20 * Math.Log10(r4 / r1);
                t.Expect(Math.Abs(d2 + 6.0) < 0.4 && Math.Abs(d4 + 12.0) < 0.4, $"velocity 0.5 and 0.25 should play 6 dB and 12 dB down (0.75 dB steps): {d2:0.00} and {d4:0.00} dB");
                t.Expect(r1 > 0.02, $"the full-level FM tone is too quiet (RMS {r1:0.0000})");
                t.Note($"YM2612 Runaway: level steps {d2:0.00}/{d4:0.00} dB for velocity 0.5/0.25; full level RMS {r1:0.000}");
                // the patch that plays is the one picked: FM 3 (instrument 2 -> parameter 3) was never set up by the game (its attack rate is 0), so it is silent; FM 2 (parameter 2) is a sine like FM 1
                p.SetParam(PInstrument, 3);
                var never = PlayNote(p, 69, 1.0);
                t.Expect(Audio.Peak(never.Audio, never.Log.StartSample + 6000, never.Log.EndSample - 3000) < 1e-3, "FM 3 was never set up by the game: its patch should be silent");
                p.SetParam(PInstrument, 2);
                t.NearCents("YM2612 FM 2 patch key 69", NoteZeroCrossHz(p, 69), OpnHz(440), 1.0);
                p.SetParam(PInstrument, 0);
                // polyphony, then more notes than channels
                var chord = PlayChord(p, 1.0, 69, 73, 76);
                bool all3 = new[] { 69.0, 73.0, 76.0 }.All(k => HasTone(chord, OpnHz(KeyHz(k)), 12000, 36000, 3).Present);
                t.Expect(all3, "a chord on the YM2612 should have all three tones");
                var big = PlayChord(p, 0.6, 48, 52, 55, 60, 64, 67, 72, 76);
                t.Expect(big.All(float.IsFinite) && Audio.Rms(big, 12000, 36000) > 0.02 && p.Test(77) == 0, $"eight notes on six channels: finite and sounding, voices handed back ({p.Test(77)} still hold a slot)");
                var high = PlayNote(p, 126, 1.0);
                t.Expect(Audio.Peak(high.Audio, high.Log.StartSample + 6000, high.Log.EndSample - 3000) < 1e-3 && p.Test(73) >= 1, $"a 12 kHz note is above the YM2612's range: it should be silent and counted (dropped {p.Test(73)})");

                // the DAC sound (instrument 6 -> parameter 7): a looped sound plays AT the note, whatever rate the game streamed it at
                p.SetParam(PInstrument, 7);
                foreach (double key in new[] { 69.0, 57.0, 81.0, 64.0 })
                    t.NearCents($"DAC loop key {key}", NoteZeroCrossHz(p, key), KeyHz(key), 1.0);
                double q1 = NoteRms(p, 69, 1.0), q2 = NoteRms(p, 69, 0.5);
                t.Expect(q1 > 0.02 && Math.Abs(q2 / q1 - 0.5) < 0.03, $"the DAC sound's level should follow the velocity linearly: {q1:0.0000} and {q2:0.0000}");
                var dchord = PlayChord(p, 1.0, 64, 69, 73);
                t.Expect(new[] { 64.0, 69.0, 73.0 }.All(k => HasTone(dchord, KeyHz(k), 12000, 36000, 3).Present), "a chord of DAC voices should have all three tones");
                var dbig = PlayChord(p, 0.5, 48, 50, 52, 53, 55, 57, 59, 60, 62, 64);
                t.Expect(dbig.All(float.IsFinite) && Audio.Rms(dbig, 12000, 36000) > 0.02 && p.Test(77) == 0, "ten notes on eight DAC voices: finite and sounding, voices handed back");
                var dslide = new Scenario { Name = "slide", Tempo = 60, LengthBeats = 2 };
                dslide.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1.5, Key = 57, Channel = 0, Velocity = 1.0, Slides = new() { new SlideSeg { StartBeat = 0.2, LengthBeats = 1.0, ToKey = 69 } } });
                var dsa = Mono(new Mixer { SampleRate = 48000 }.Run(p, dslide));
                var dtrack = Audio.Track(dsa, 48000, 120, 1500, 40, 40).Where(x => !double.IsNaN(x.Hz)).ToList();
                t.Expect(dtrack.Count > 20 && Audio.Cents(dtrack[0].Hz, 220) is > -20 and < 20 && Audio.Cents(dtrack[^1].Hz, 440) is > -20 and < 20, $"a slide up an octave should be followed by the DAC voice: {dtrack.FirstOrDefault().Hz:0} Hz to {dtrack.LastOrDefault().Hz:0} Hz over {dtrack.Count} windows");
                // the PSG of the same unit (instrument 6 + DAC sounds -> parameter + 1)
                p.SetParam(PInstrument, 6 + dacCount + 1);
                t.NearCents("PSG tones of the Genesis unit, key 69", NoteZeroCrossHz(p, 69), PsgHz(440), 1.0);
                p.SetParam(PInstrument, 0);
                t.Expect(p.Test(3) == 0, "voices were not handed back");

                // the picker through the editor: Auto, six FM patches, the DAC sounds, the PSG's three
                nint parent = Win32Host.CreateParent();
                try
                {
                    p.ShowEditor(parent);
                    for (int k = 0; k < 4; k++) { RenderBlocks(p, 0.1); Win32Host.Pump(40); }
                    if (ClickControl(t, p, 90, "Instrument dropdown"))
                    {
                        t.Expect(p.Test(31) == 90 && p.Test(32) == instruments + 1, $"the Instrument dropdown lists {p.Test(32)} entries, expected {instruments + 1}");
                        var item = Centre(p, 100 + 7);
                        if (item != null) Win32Host.Click(p.EditorHandle, item.Value.X, item.Value.Y);
                        t.Expect(p.GetParam(PInstrument) == 7, $"picking the first DAC sound left the parameter at {p.GetParam(PInstrument)} (expected 7)");
                    }
                    p.HideEditor();
                }
                finally { Win32Host.DestroyParent(parent); }
                p.SetParam(PInstrument, 7);
                saved = p.SaveState();
                t.Note($"Genesis Runaway project state: {saved.Length:N0} bytes");
            }

            // the project remembers it: reopened, the game is frozen, the FM patches and the DAC sound come back
            using (var q = lib.Create())
            {
                q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                q.LoadState(saved);
                t.Expect(q.Test(45) == 1 && q.Test(71) == 3 && q.Test(74) == dacCount && q.Test(72) == instruments, $"the saved project did not come back whole (runaway {q.Test(45)}, kind {q.Test(71)}, DAC sounds {q.Test(74)}/{dacCount}, instruments {q.Test(72)}/{instruments})");
                t.NearCents("restored DAC loop key 69", NoteZeroCrossHz(q, 69), 440, 1.0);
                q.SetParam(PInstrument, 0);
                t.NearCents("restored FM patch key 69", NoteZeroCrossHz(q, 69), OpnHz(440), 1.0);
                // the bytes of the snapshot survive a round trip through the plugin's own reader
                long len = q.Test(79, 0, 1);
                t.Expect(len > 1000 && q.Test(45) == 1 && q.Test(74) == dacCount, $"the snapshot's bytes should read back as the same snapshot (length {len})");
                q.Test(47, 0, 0);
                RenderBlocks(q, 0.3);
                t.Expect(q.Test(45) == 0 && q.Test(17) == 4, $"after releasing: runaway {q.Test(45)}, channel {q.Test(17)}");
            }

            // hostile projects: damaged or foreign snapshot sections are survived, never trusted
            int tagAt = LastIndexOf(saved, "OPN1"u8.ToArray());
            t.Expect(tagAt > 0, "the saved project holds no OPN1 section");
            if (tagAt > 0)
            {
                int survived = 0, restored = 0;
                int Load(byte[] bytes, bool play = true)
                {
                    using var h = lib.Create();
                    h.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                    h.LoadState(bytes);
                    int on = (int)h.Test(45);
                    if (play && on == 1 && h.Test(71) == 3)
                        foreach (int ins in new[] { 0, 3, 7, 6 + dacCount + 1 })
                        {
                            h.SetParam(PInstrument, ins);
                            var (a, log) = PlayNote(h, 60, 0.9, 0.3);
                            if (!a.All(float.IsFinite) || Audio.Peak(a, 0, a.Length) > 4) { t.Expect(false, $"a damaged snapshot made the sampler blow up (instrument {ins}, peak {Audio.Peak(a, 0, a.Length):0.00})"); break; }
                        }
                    survived++;
                    return on;
                }
                var cut = saved[..Math.Min(saved.Length, tagAt + 8 + 40)];
                t.Expect(Load(cut) == 0, "a snapshot cut short should be dropped, not restored");
                var wrongTag = (byte[])saved.Clone(); "PSG1"u8.CopyTo(wrongTag.AsSpan(tagAt));
                t.Expect(Load(wrongTag) == 0, "a section whose tag says PSG but whose bytes are a Genesis snapshot should be dropped");
                var hugeLen = (byte[])saved.Clone(); BitConverter.GetBytes(0x7FFFFFF0).CopyTo(hugeLen, tagAt + 4);
                t.Expect(Load(hugeLen) == 0, "a section that claims two gigabytes should be dropped");
                var zeroLen = (byte[])saved.Clone(); BitConverter.GetBytes(0).CopyTo(zeroLen, tagAt + 4);
                t.Expect(Load(zeroLen) == 0, "an empty section should be dropped");
                var badVersion = (byte[])saved.Clone(); badVersion[tagAt + 8] = 9;
                t.Expect(Load(badVersion) == 0, "a snapshot of a version that does not exist should be dropped");
                var badKind = (byte[])saved.Clone(); badKind[tagAt + 9] = 7;
                t.Expect(Load(badKind) == 0, "a snapshot of a kind that does not exist should be dropped");
                var badClock = (byte[])saved.Clone(); BitConverter.GetBytes(5).CopyTo(badClock, tagAt + 10);
                t.Expect(Load(badClock) == 0, "a snapshot with a chip clock of 5 Hz should be dropped");
                // the first DAC sound's length field: 19 header bytes + 64 YM2413 registers + 5 (clock, LFO) + 6 patches of 31 + the sound count = 275, then its rate (4 bytes), then its length
                int dacLen = tagAt + 8 + 275 + 4;
                if (dacLen + 4 <= saved.Length)
                {
                    var hugeSound = (byte[])saved.Clone(); BitConverter.GetBytes(int.MaxValue).CopyTo(hugeSound, dacLen);
                    t.Expect(Load(hugeSound) == 0, "a DAC sound that claims 2 GB should drop the snapshot");
                }
                // random damage inside a valid snapshot: whatever loads must play without blowing up
                var rng = new Random(2026);
                int playable = 0;
                for (int v = 0; v < 14; v++)
                {
                    var b = (byte[])saved.Clone();
                    for (int k = 0; k < 10; k++) b[tagAt + 8 + 2 + rng.Next(Math.Max(1, Math.Min(300, b.Length - tagAt - 10)))] = (byte)rng.Next(256);
                    if (Load(b) == 1) playable++;
                }
                t.Note($"hostile Genesis snapshots: 8 deliberate breakages dropped, 14 random ones survived ({playable} still loaded and played finite audio), {survived} loads in all");
            }
        }
        finally { DeleteQuietly(md); }
    }

    // ------------------------------------------------------------------------------------------------
    // what it costs: the sampler of each chip with its busiest chord

    private void RunawaySegaCost(T t)
    {
        var cases = new (string Name, string Path, int Console, string Chip, int Instrument, double[] Keys, string Detail)[]
        {
            ("PSG", TempGame(".sms", MakeSmsRom2(fm: false)), 3, "SMS", 0, [60, 64, 67], "3 tones"),
            ("YM2413", TempGame(".sms", MakeSmsRom2(fm: true)), 3, "SMSFM", 5, [48, 52, 55, 60, 64, 67, 72, 76, 79], "9 channels, the flute"),
            ("YM2612", TempGame(".md", MakeMdRom(fm: true, dac: true)), 4, "MD", 0, [48, 52, 55, 60, 64, 67], "6 channels"),
            ("DAC", TempGame(".md", MakeMdRom(fm: true, dac: true)), 4, "MD", 7, [48, 50, 52, 53, 55, 57, 59, 60], "8 voices"),
        };
        try
        {
            foreach (var c in cases)
            {
                using var p = SegaGame(c.Path, c.Console, c.Chip);
                RenderBlocks(p, 1.2);
                p.Test(47, 0, 1);
                if (p.Test(45) != 1) { t.Expect(false, $"{c.Name}: Runaway did not start"); continue; }
                p.SetParam(PInstrument, c.Instrument);
                var s = new Scenario { Name = "cost", Tempo = 60, LengthBeats = 9 };
                foreach (var k in c.Keys) s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 8.5, Key = k, Channel = 0, Velocity = 0.7 });
                long before = 0, after = 0;
                var mixer = new Mixer { SampleRate = 48000, BlockSize = 512, AfterBlock = sm =>
                {
                    if (sm >= 48000 * 2 && before == 0) before = p.Test(4);
                    if (sm >= 48000 * 8 && after == 0) after = p.Test(4);
                } };
                var sw = Pace.Start();
                var audio = mixer.Run(p, s, 0);
                sw.Stop();
                double seconds = audio.Length / 2 / 48000.0, speed = seconds / sw.Seconds;
                long alloc = after - before;
                t.Expect(speed >= 5, $"{c.Name} Runaway: {speed:0.0}x real time with {c.Detail} (needs 5x or better: the plugin's floor)");
                t.Expect(alloc == 0, $"{c.Name} Runaway: {alloc:N0} bytes allocated by the plugin while playing {c.Detail} for 6 s (must be 0: garbage collection is audible)");
                t.Expect(audio.All(float.IsFinite), $"{c.Name} Runaway: non-finite output");
                t.Note($"{c.Name} Runaway: {sw.Describe(seconds)}, {c.Detail}; steady-state allocation {alloc} B");
            }
        }
        finally { foreach (var c in cases) DeleteQuietly(c.Path); }
    }
}
