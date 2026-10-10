using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// The YM2413 half of <c>--sega-bridge-test</c> (round 3, track C3b): <c>smsfm</c> (menus, <see cref="OpllPatchBank"/>, and <see cref="APU_SMSFM"/>: NES registers to the Master System's FM chip and its PSG). Every figure is measured in captured
/// audio: pitch in cents against the NES formula, level in decibels against the NES mixer's own formula and APU_FIX, harmonic content for the timbres, and each limit says why it is the limit.
/// <c>--only smsfm --calibrate</c> prints the calibration table to paste into <see cref="OpllPatchBank"/>.
/// </summary>
internal static class SmsFmBridgeTests
{
    private static int passes, failures;
    private static bool verbose;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }
    private static void Guard(Action group, string? name = null)
    {
        try { group(); }
        catch (Exception e) { Check($"{name ?? group.Method.Name} ran to the end", false, e.GetType().Name + ": " + e.Message); }
    }

    private const double NesHz = NesVoiceWriter.NesCpuHz;
    private static double Cents(double hz, double want) => SegaMeasure.Cents(hz, want);
    private static double Db(double a, double b)
    {
        if (!(a > 0 && b > 0) || !double.IsFinite(a) || !double.IsFinite(b)) throw new InvalidOperationException($"a level ratio of {a} to {b} (one side is silent or not a number)");
        return SegaMeasure.Db(a, b);
    }
    private static int TimerFor(double hz) => (int)Math.Round(NesHz / (16.0 * hz)) - 1;
    private static int TriTimerFor(double hz) => (int)Math.Round(NesHz / (32.0 * hz)) - 1;
    private static double PulseHz(int t) => NesHz / (16.0 * (t + 1));
    private static double TriHz(int t) => NesHz / (32.0 * (t + 1));
    private static readonly OpllPatch[] P = OpllPatchBank.Patches;

    public static (int Passes, int Failures) Run(string[] args, Func<string, bool> want, string? nesRom, bool verboseOutput)
    {
        verbose = verboseOutput; passes = failures = 0;
        if (args.Any(a => a.Equals("--calibrate", StringComparison.OrdinalIgnoreCase))) { Calibrate(); return (0, 0); }
        if (want("smsfm"))
        {
            Console.WriteLine("\n== Master System FM: menus and the patch bank");
            Guard(Catalog); Guard(Bank);
            Console.WriteLine("\n== APU_SMSFM: NES registers to the YM2413 and the PSG");
            Guard(Pitch); Guard(Level); Guard(KeyScale); Guard(CalibrationCheck); Guard(Timbre); Guard(Noise); Guard(Dmc); Guard(Switching); Guard(Polyphony); Guard(PsgSquare); Guard(Range); Guard(Behaviour); Guard(Timing); Guard(() => NesGame(nesRom), "NesGame");
        }
        return (passes, failures);
    }

    // ============================================================================================ the raw chip (calibration)

    /// <summary>The YM2413 on its own, one channel playing a patch at the chip's loudest volume: returns the host-rate samples as fractions of full scale (the converter the unit and the bridge use).</summary>
    private static float[] RawCapture(OpllPatch p, double noteHz, double seconds, int channel = 0, int volume = 0)
    {
        var fm = new Ym2413(APU_SMSFM.ClockHz); fm.Reset(); fm.ResetPatch();
        var res = new Ym2413Resampler(APU_SMSFM.ClockHz / Ym2413.ClocksPerSample, 44100);
        if (p.User != null) for (int r = 0; r < 8; r++) fm.WriteRegister(r, p.User[r]);
        if (!OpllPatchBank.TryPitch(noteHz / p.Fundamental, APU_SMSFM.ClockHz, out int blk, out int fnum, out _)) throw new InvalidOperationException("pitch out of range");
        fm.WriteRegister(0x30 + channel, (byte)((p.Rom << 4) | volume));
        fm.WriteRegister(0x10 + channel, (byte)(fnum & 0xFF));
        fm.WriteRegister(0x20 + channel, (byte)(0x10 | (blk << 1) | (fnum >> 8)));
        var list = new List<float>();
        long chipSamples = (long)(seconds * APU_SMSFM.ClockHz / Ym2413.ClocksPerSample);
        for (long i = 0; i < chipSamples; i++)
        {
            res.Push((short)Math.Clamp(fm.Calc(), short.MinValue, short.MaxValue));
            while (res.TryRead(out short o)) list.Add(o / 32768f);
        }
        return list.ToArray();
    }

    /// <summary>The patch's calibrated figure: the AC RMS of a held C4 at volume 0 from 100 to 400 ms after the key went down.</summary>
    private static double MeasureRaw(OpllPatch p)
    {
        var x = RawCapture(p, 261.63, 0.5);
        return SegaMeasure.Rms(x, (int)(0.1 * 44100), (int)(0.4 * 44100));
    }

    private static void Calibrate()
    {
        Console.WriteLine("== calibration: AC RMS (fraction of full scale) of a held C4 at the chip's volume 0, 100 to 400 ms after the key went down");
        var sb = new System.Text.StringBuilder("            ");
        for (int i = 0; i < P.Length; i++)
        {
            double v = P[i].Psg ? 0 : MeasureRaw(P[i]);
            Console.WriteLine($"  {i,2} {P[i].Name,-16} {v:F5}   ({20 * Math.Log10(Math.Max(v, 1e-9) * APU_SMSFM.FmGain / APU_MD.PulseRms(2, 15)):+0.0;-0.0} dB against the loudest NES pulse at gain {APU_SMSFM.FmGain})");
            sb.Append(v.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)).Append(i + 1 < P.Length ? ", " : ",");
        }
        Console.WriteLine("\n" + sb);
    }

    // ============================================================================================ menus and bank

    private static void Catalog()
    {
        Check("CoreRegistry discovers the NES-facing core as APU id SMSFM", CoreRegistry.ApuIds.Contains("SMSFM", StringComparer.OrdinalIgnoreCase) && CoreRegistry.ApuTypes["SMSFM"] == typeof(APU_SMSFM));
        var nes = CoreCatalog.Options(ConsoleKind.Nes, CoreSlot.Apu).FirstOrDefault(o => o.Id == "SMSFM");
        Check("the NES APU menu lists SMSFM under the Master System family ('From the Master System'), with an honest label", nes is { Family: "Master System" } && nes.Label.Contains("DMC is dropped") && nes.Label.Contains("YM2413"), nes?.Label ?? "(missing)");
        Check("... it is not a NES chip: the other consoles' lists of NES sound chips leave it out", CoreCatalog.NesFamilyOf(CoreSlot.Apu, "SMSFM") == "Master System"
            && new[] { ConsoleKind.MasterSystem, ConsoleKind.GameGear, ConsoleKind.Genesis, ConsoleKind.Snes, ConsoleKind.GameBoy }.All(c => CoreCatalog.Options(c, CoreSlot.Apu).All(o => o.Id != "NES:SMSFM")));
        Check("... and the Master System's own sound menu is unchanged (the stock unit, SMS-FM, then NES chips)", CoreCatalog.Options(ConsoleKind.MasterSystem, CoreSlot.Apu).Take(2).Select(o => o.Id).SequenceEqual(new[] { "SMS", "SMS-FM" }));
    }

    private static void Bank()
    {
        Check($"the bank has {P.Length} patches: the chip's 15 ROM instruments (numbers 1 to 15, in order), 6 designed ones, and the PSG square last", P.Length == 22 && P.Take(15).Select((p, i) => p.Rom == i + 1 && p.User == null && !p.Psg).All(b => b)
            && P.Skip(15).Take(6).All(p => p.Rom == 0 && p.User is { Length: 8 } && !p.Psg) && P[^1].Psg && OpllPatchBank.PsgIndex == 21 && !P[OpllPatchBank.SineIndex].Psg && P[OpllPatchBank.SineIndex].User != null);
        Check("names are unique and every patch says what it is", P.Select(p => p.Name).Distinct().Count() == P.Length && P.All(p => p.Name.Length > 0 && p.Description.Length > 20) && OpllPatchBank.Names.SequenceEqual(P.Select(p => p.Name)));
        // the fields of a designed instrument are inside their registers (a byte can hold more than the chip uses)
        bool fields = P.Where(p => p.User != null).All(p => (p.User![3] & 0x20) == 0 && (p.User![2] & 0x3F) <= 63);
        Check("a designed instrument's register bytes use only the bits the chip has (register 3 bit 5 is unused)", fields);
        // the ROM bytes behind the names: the fundamental multiple of each instrument
        var odd = P.Where(p => p.Fundamental != 1.0).Select(p => $"{p.Name} {p.Fundamental}").ToList();
        Check($"every instrument's fundamental is the note's base frequency except the synthesizer bass, whose carrier is at half of it: {string.Join(", ", odd)}", odd.Count == 1 && P[12].Fundamental == 0.5 && P[12].Name == "Synth bass");
        Check("clamping: an index out of range is the last patch (the PSG square), never an exception", OpllPatchBank.Clamp(99) == 21 && OpllPatchBank.Clamp(-3) == 0);
        Check("the calibration table has an entry for every FM patch and none for the PSG square", OpllPatchBank.CalibrationTable.Length == P.Length && Enumerable.Range(0, P.Length).All(i => P[i].Psg ? OpllPatchBank.CalibrationTable[i] == 0 : OpllPatchBank.CalibrationTable[i] > 0.005));

        // the pitch solver against the chip's own frequency formula, and its error over every NES pulse timer the chip can reach
        double worst = 0, worstLow = 0, worstHz = 0; int unreachable = 0, solved = 0;
        for (int t = 8; t <= 2047; t++)
        {
            double hz = PulseHz(t);
            if (!OpllPatchBank.TryPitch(hz, APU_SMSFM.ClockHz, out int b, out int f, out double c)) { unreachable++; continue; }
            solved++;
            double actual = OpllPatchBank.Hz(b, f, APU_SMSFM.ClockHz);
            if (Math.Abs(Cents(actual, hz) - c) > 1e-6) { Check("TryPitch's cents agree with the chip's frequency formula", false, $"timer {t}"); break; }
            if (hz >= 49 && Math.Abs(c) > worst) { worst = Math.Abs(c); worstHz = hz; }
            if (hz < 110) worstLow = Math.Max(worstLow, Math.Abs(c));
        }
        double worstTri = 0; int triUnreachable = 0;
        for (int t = 2; t <= 2047; t++)
        {
            if (!OpllPatchBank.TryPitch(TriHz(t), APU_SMSFM.ClockHz, out _, out _, out double c)) { triUnreachable++; continue; }
            worstTri = Math.Max(worstTri, Math.Abs(c));
        }
        Console.WriteLine($"        frequency registers: worst error {worst:F2} cents from 49 Hz up over {solved} pulse timers (at {worstHz:F1} Hz), {worstLow:F2} below 110 Hz, {worstTri:F2} over the triangle's whole range; {unreachable} pulse and {triUnreachable} triangle timers are above the chip's top (about 6.2 kHz)");
        Check("the nearest block/F-number is within 3.4 cents for every NES pulse note from 49 Hz up the chip can reach (half a step of the 9-bit F-number at its bottom, 256)", solved > 1900 && worst < 3.45);
        Check("... and within 6.1 cents over the triangle's whole range down to 27 Hz (block 0 has only 142 steps there)", worstTri < 6.1);
        Check("... a pitch above the chip's top is refused, not squeezed to a wrong note", !OpllPatchBank.TryPitch(7000, APU_SMSFM.ClockHz, out _, out _, out _) && OpllPatchBank.TryPitch(6000, APU_SMSFM.ClockHz, out _, out _, out _) && !OpllPatchBank.TryPitch(0, APU_SMSFM.ClockHz, out _, out _, out _));
        // the chip's own idea of a channel's frequency agrees with the bank's formula
        {
            var fm = new Ym2413(APU_SMSFM.ClockHz); fm.Reset(); fm.ResetPatch();
            fm.WriteRegister(0x30, 0x10); OpllPatchBank.TryPitch(440, APU_SMSFM.ClockHz, out int b, out int f, out _);
            fm.WriteRegister(0x10, (byte)(f & 0xFF)); fm.WriteRegister(0x20, (byte)(0x10 | (b << 1) | (f >> 8)));
            double chipHz = fm.ChannelHz(0), mine = OpllPatchBank.Hz(b, f, APU_SMSFM.ClockHz);
            Check($"the bank's frequency formula is the chip's (violin carrier x1: the chip says {chipHz:F3} Hz, the bank {mine:F3} Hz)", Math.Abs(Cents(mine, chipHz)) < 0.001);
        }
    }

    // ============================================================================================ the bridge

    private static MdPatchCli.Rig Rig(int patch = 0)
    {
        var r = new MdPatchCli.Rig("SMSFM");
        ((APU_SMSFM)r.Apu).Patch = patch;
        return r;
    }
    private static float[] Capture(MdPatchCli.Rig r, double settle, double seconds) { r.Run(settle, false); r.Run(seconds, true); return r.Take(); }

    private static double RmsOf(float[] x, int rate, double from, double to)
    {
        if ((int)(to * rate) > x.Length + rate / 50 || (int)(from * rate) >= x.Length) throw new InvalidOperationException($"the window {from}-{to} s lies outside the {x.Length / (double)rate:F2} s that was captured");
        double r = SegaMeasure.Rms(x, (int)(from * rate), Math.Min(x.Length, (int)(to * rate)));
        if (!double.IsFinite(r)) throw new InvalidOperationException("non-finite RMS");
        return r;
    }

    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static void Pitch()
    {
        // ---- pulses: every patch, three notes; the fundamental is measured from the audio ----
        double worstAll = 0; int subOk = 0, fmCount = 0; var rows = new List<string>(); var subBad = new List<string>();
        foreach (int note in new[] { 45, 60, 81 })
        {
            double hzWant = 440.0 * Math.Pow(2, (note - 69) / 12.0); int t = TimerFor(hzWant); double want = PulseHz(t);
            double worst = 0; var bad = new List<string>();
            for (int p = 0; p < P.Length; p++)
            {
                var rig = Rig(p); rig.Pulse(0, t, 15);
                var x = Capture(rig, 0.5, 1.7);
                int from = 2000, to = from + 65536;      // 1.5 s: several periods of the instruments' vibrato (about 6.4 Hz), whose pitch it averages
                var (f0, _) = SegaMeasure.Peak(x, rig.Rate, want, from, to, 0.03);
                double err = Cents(f0, want);
                // the mean pitch of a decaying note with vibrato is weighted toward the start of the vibrato cycle: a percussive instrument with vibrato (the synthesizer bass) is measured to 10 cents; the exact figure is the register's (checked below)
                double limit = P[p].Psg ? 0.5 : (!P[p].Sustained && P[p].CarrierPm) ? 10.0 : 3.6;
                if (Math.Abs(err) > limit) bad.Add($"{P[p].Name} {err:+0.00;-0.00}");
                worst = Math.Max(worst, Math.Abs(err));
                double regCents = Math.Abs(((APU_SMSFM)rig.Apu).FmCents(0));
                if (!P[p].Psg && regCents > 3.45) bad.Add($"{P[p].Name} register {regCents:F2} c");
                if (!P[p].Psg)
                {
                    fmCount++;
                    double sub = SegaMeasure.Component(x, rig.Rate, f0 / 2, from, to), top = Enumerable.Range(1, 6).Max(h => SegaMeasure.Component(x, rig.Rate, f0 * h, from, to));
                    if (top <= 1e-6) bad.Add($"{P[p].Name} is silent");
                    else if (Db(Math.Max(sub, 1e-9), top) < -34) subOk++;
                    else subBad.Add($"{P[p].Name} at {NoteNames[note % 12]}{note / 12 - 1} ({Db(Math.Max(sub, 1e-9), top):F0} dB)");
                }
            }
            worstAll = Math.Max(worstAll, worst);
            rows.Add($"{NoteNames[note % 12]}{note / 12 - 1} {want:F1} Hz worst {worst:F2} c");
            Check($"all {P.Length} patches play {NoteNames[note % 12]}{note / 12 - 1} ({want:F2} Hz, NES timer {t}) at the NES pitch: worst {worst:F2} cents (limit 3.6 for FM: the 9-bit F-number's step; 0.5 for the PSG square)", bad.Count == 0, string.Join(", ", bad));
        }
        Check($"... and for every FM patch the fundamental is the lowest strong partial (nothing within 34 dB at half its frequency): {subOk} of {fmCount} note-patch pairs", subOk == fmCount, string.Join(", ", subBad));
        Console.WriteLine($"        pulse pitch through the whole path: worst {worstAll:F2} cents ({string.Join("; ", rows)})");

        // ---- pulse 2 and the triangle across their ranges (the designed sine carries the triangle) ----
        double worstTri = 0, worstP2 = 0; var tbad = new List<string>();
        foreach (double hz in new[] { 27.5, 41.2, 55, 110, 220, 440, 880, 1760, 3520 })
        {
            int t = TriTimerFor(hz); double want = TriHz(t);
            if (t > 2047) continue;
            var rig = Rig(); rig.Triangle(t);
            var x = Capture(rig, 0.8, 1.6);
            var (f0, _) = SegaMeasure.Peak(x, rig.Rate, want, 4000, 4000 + 65536, 0.05);
            double err = Cents(f0, want); worstTri = Math.Max(worstTri, Math.Abs(err));
            double limit = hz < 45 ? 6.2 : 3.6;
            if (Math.Abs(err) > limit) tbad.Add($"{hz} Hz {err:+0.00;-0.00}");
        }
        foreach (double hz in new[] { 55, 110, 440, 1760 })
        {
            int t = TimerFor(hz); double want = PulseHz(t);
            var rig = Rig(OpllPatchBank.SineIndex); rig.Pulse(1, t, 15);
            var x = Capture(rig, 0.8, hz < 100 ? 1.6 : 1.0);
            var (f0, _) = SegaMeasure.Peak(x, rig.Rate, want, 4000, 4000 + (hz < 100 ? 65536 : 32768), 0.05);
            worstP2 = Math.Max(worstP2, Math.Abs(Cents(f0, want)));
            var fmc = (APU_SMSFM)rig.Apu;
            if (!fmc.FmKeyed(1) || fmc.FmKeyed(0)) tbad.Add("pulse 2 is not on FM channel 2");
        }
        Check($"the triangle (FM channel 3, a sine) plays 27 Hz to 3.5 kHz at the NES pitch: worst {worstTri:F2} cents (limit 6.2 below 45 Hz, 3.6 above); pulse 2 on FM channel 2: worst {worstP2:F2} cents", tbad.Count == 0 && worstP2 < 3.6, string.Join(", ", tbad));
        {
            var rig = Rig(OpllPatchBank.SineIndex); var fmc = (APU_SMSFM)rig.Apu; rig.Pulse(0, TimerFor(440), 15); rig.Run(0.1, false);
            Check($"the chip's set frequency is reported ({fmc.FmHz(0):F3} Hz for the NES note {PulseHz(TimerFor(440)):F3} Hz = {fmc.FmCents(0):+0.000;-0.000} cents)", fmc.FmKeyed(0) && Math.Abs(fmc.FmCents(0)) < 3.45 && Math.Abs(Cents(fmc.FmHz(0), PulseHz(TimerFor(440)))) < 3.45);
        }
    }

    private static void Level()
    {
        // ---- every sustained patch at C4 volume 15 against the NES mixer's formula and APU_FIX: the bridge scales each patch from its calibrated level ----
        var fix = new MdPatchCli.Rig("FIX"); fix.Pulse(0, TimerFor(261.63), 15);
        double fixRms = RmsOf(Capture(fix, 0.5, 1.5), fix.Rate, 0.2, 1.5);
        double formula = APU_MD.PulseRms(2, 15);
        var rows = new List<string>(); var bad = new List<string>(); double worst = 0;
        for (int p = 0; p < P.Length; p++)
        {
            var rig = Rig(p); rig.Pulse(0, TimerFor(261.63), 15);
            var x = Capture(rig, 0.1, 0.9); double rms = RmsOf(x, rig.Rate, 0.1, 0.4);
            double d = Db(rms, formula); if (P[p].Sustained || P[p].Psg) worst = Math.Max(worst, Math.Abs(d));
            rows.Add($"{P[p].Name} {d:+0.0;-0.0}");
            if ((P[p].Sustained || P[p].Psg) && Math.Abs(d) > 2.5) bad.Add($"{P[p].Name} {d:+0.0;-0.0} dB");
        }
        Console.WriteLine($"        level against the NES mixer formula (C4, volume 15, 100 to 400 ms after the key went down): {string.Join(", ", rows)} dB; APU_FIX itself {Db(fixRms, formula):+0.0;-0.0} dB");
        Check($"every sustained patch and the PSG square plays the loudest NES pulse step within 2.5 dB of the NES mixer's formula (worst {worst:F1} dB: the volume register's 3 dB steps leave up to 1.5 dB, the rest is the instrument's own wobble)", bad.Count == 0, string.Join(", ", bad));

        // ---- the sine at all NES volumes and duty cycles (110 Hz, so APU_FIX's own low-pass does not touch it) ----
        {
            double worstFormula = 0, worstFix = 0; var fbad = new List<string>();
            foreach (var (duty, dutyName) in new[] { (0, "12.5%"), (1, "25%"), (2, "50%"), (3, "75%") })
                foreach (int v in new[] { 15, 12, 9, 6, 4, 3, 2, 1 })
                {
                    double target = APU_MD.PulseRms(duty, v);
                    foreach (int patch in new[] { OpllPatchBank.SineIndex, 7, 8 })       // the designed sine, the ROM organ and horn (sustained: they hold their level)
                    {
                        var a = Rig(patch); a.Pulse(0, TimerFor(110), v, duty); double ra = RmsOf(Capture(a, 0.0, 0.6), a.Rate, 0.1, 0.4);
                        double d = Db(ra, target); worstFormula = Math.Max(worstFormula, Math.Abs(d));
                        // the loudest step may fall short of the loudest NES levels: that is counted by the bridge (TooQuietSyncs) and allowed here only when the counter says so
                        bool short_ = ((APU_SMSFM)a.Apu).TooQuietSyncs > 0;
                        if (Math.Abs(d) > 2.3 && !(short_ && d < 0)) fbad.Add($"'{P[patch].Name}' {dutyName} v{v}: {d:+0.0;-0.0} dB");
                    }
                    var b = new MdPatchCli.Rig("FIX"); b.Pulse(0, TimerFor(110), v, duty); double rf = RmsOf(Capture(b, 1.0, 1.0), b.Rate, 0.1, 1.0);
                    if (v >= 4) worstFix = Math.Max(worstFix, Math.Abs(Db(rf, target)));
                }
            Check($"the sine, the ROM organ and the ROM horn at all 15 NES volumes and four duty cycles are within 2.3 dB of the NES mixer's formula (worst {worstFormula:F1} dB; a step the chip's loudest volume cannot reach is counted in TooQuietSyncs and may only be quiet); APU_FIX itself is {worstFix:F1} dB off the formula at volumes 4 to 15", fbad.Count == 0, string.Join("; ", fbad));
        }
        {
            var seq = new List<double>();
            foreach (int v in new[] { 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 }) { var a2 = Rig(OpllPatchBank.SineIndex); a2.Pulse(0, TimerFor(110), v); seq.Add(RmsOf(Capture(a2, 0.15, 0.7), a2.Rate, 0.1, 0.5)); }
            bool mono = true; for (int i = 1; i < seq.Count; i++) mono &= seq[i] <= seq[i - 1] * 1.001;
            Check($"the level never rises as the NES volume falls from 15 to 1 and covers {Db(seq[0], seq[^1]):F1} dB (the NES's own range is 22 dB)", mono && Db(seq[0], seq[^1]) > 19);
        }
        {
            var z = Rig(OpllPatchBank.SineIndex); z.Pulse(0, TimerFor(261.63), 0);
            Check("volume 0 is silent", RmsOf(Capture(z, 0.4, 0.4), z.Rate, 0.0, 0.4) < 1e-4);
        }
        {
            var a = Rig(); a.Triangle(TriTimerFor(110)); var xa = Capture(a, 0.3, 1.0);
            var b = new MdPatchCli.Rig("FIX"); b.Triangle(TriTimerFor(110)); var xb = Capture(b, 0.5, 1.0);
            double d = Db(RmsOf(xa, a.Rate, 0.2, 0.9), RmsOf(xb, b.Rate, 0.2, 0.9));
            Check($"the triangle (a sine at the NES triangle's fixed loudness) is {d:+0.0;-0.0} dB from the NES triangle (limit 2.0)", Math.Abs(d) <= 2.0);
        }
    }

    private static void KeyScale()
    {
        // the key-scale maths in the bank is the chip's: the carrier's total level at volume 0 (the chip's own report) equals the bank's key-scale steps at every block and F-number, for every key-scale field
        int mism = 0, tested = 0;
        for (int kl = 0; kl < 4; kl++)
            foreach (int block in new[] { 0, 2, 4, 5, 6, 7 })
                foreach (int fnum in new[] { 64, 200, 256, 340, 420, 511 })
                {
                    var fm = new Ym2413(APU_SMSFM.ClockHz); fm.Reset(); fm.ResetPatch();
                    fm.WriteRegister(3, (byte)(kl << 6)); fm.WriteRegister(0x30, 0x00);          // user instrument: carrier key-scale field kl; volume 0
                    fm.WriteRegister(0x10, (byte)(fnum & 0xFF)); fm.WriteRegister(0x20, (byte)(0x10 | (block << 1) | (fnum >> 8)));
                    for (int i = 0; i < 64; i++) fm.Calc();
                    int chip = fm.Operator(0, true).Level;
                    tested++;
                    if (chip != OpllPatchBank.KeyScaleSteps(kl, block, fnum)) mism++;
                }
        Check($"the bank's key-scale maths is the chip's: {tested - mism} of {tested} (field, block, F-number) combinations give the same attenuation as the chip reports", mism == 0);
        // the level across the keyboard: sustained ROM instruments at four octaves against the NES formula (every ROM instrument's carrier key-scale field is 0, so this holds without compensation; the compensation is for the designed ones)
        var rows = new List<string>(); var bad = new List<string>(); double worst = 0;
        foreach (int patch in new[] { 0, 3, 7, 8 })
            foreach (double hz in new[] { 110, 220, 440, 880 })
            {
                var a = Rig(patch); a.Pulse(0, TimerFor(hz), 15); double ra = RmsOf(Capture(a, 0.0, 0.6), a.Rate, 0.1, 0.4);
                double d = Db(ra, APU_MD.PulseRms(2, 15)); worst = Math.Max(worst, Math.Abs(d));
                rows.Add($"{P[patch].Name} {hz:F0} Hz {d:+0.0;-0.0}");
                if (Math.Abs(d) > 2.6) bad.Add($"{P[patch].Name} {hz:F0} Hz {d:+0.0;-0.0}");
            }
        Console.WriteLine($"        level at four octaves (volume 15): {string.Join(", ", rows)} dB");
        Check($"the ROM violin, flute, organ and horn (sustained) play the same NES level at 110, 220, 440 and 880 Hz within 2.6 dB (worst {worst:F1} dB): the bridge takes the carrier's key scaling back with the volume register", bad.Count == 0, string.Join(", ", bad));
    }

    private static void CalibrationCheck()
    {
        // The calibration table is a measurement of the FM core; this measures every patch again and fails when the chip no longer plays one at the recorded level (a patch edited without calibrating, or the FM core changed).
        var tbl = OpllPatchBank.CalibrationTable; var bad = new List<string>(); double worst = 0;
        for (int p = 0; p < P.Length; p++)
        {
            if (P[p].Psg) continue;
            double rms = MeasureRaw(P[p]);
            double d = Db(rms, tbl[p]); worst = Math.Max(worst, Math.Abs(d));
            if (Math.Abs(d) > 0.15) bad.Add($"{P[p].Name} measured {rms:F5} vs table {tbl[p]:F5} ({d:+0.00;-0.00} dB)");
        }
        Check($"the calibration table matches the chip: every FM patch measured again (C4, volume 0, 100 to 400 ms) is within 0.15 dB of its recorded level; worst {worst:F2} dB", bad.Count == 0, string.Join("; ", bad));
        // the volume register is 3 dB a step: steps of the chip itself, unrelated to the bridge
        var ls = new List<double>();
        foreach (int v in new[] { 0, 2, 4, 8 }) ls.Add(SegaMeasure.Rms(RawCapture(P[OpllPatchBank.SineIndex], 261.63, 0.5, 0, v), (int)(0.1 * 44100), (int)(0.4 * 44100)));
        Check($"the chip's volume register is 3 dB a step: volumes 2, 4 and 8 are {Db(ls[1], ls[0]):F2}, {Db(ls[2], ls[0]):F2} and {Db(ls[3], ls[0]):F2} dB under volume 0 (expect -6, -12, -24)", Math.Abs(Db(ls[1], ls[0]) + 6) < 0.3 && Math.Abs(Db(ls[2], ls[0]) + 12) < 0.4 && Math.Abs(Db(ls[3], ls[0]) + 24) < 0.5);
    }

    private static void Timbre()
    {
        // what each designed patch is, in harmonics of the fundamental
        double Brightness(int patch)
        {
            double sum = 0;
            for (int n = 2; n <= 6; n++) { double h = Harmonic(patch, n); sum += h * h; }
            return Math.Sqrt(sum);
        }
        double Harmonic(int patch, int n)
        {
            var r = Rig(patch); r.Pulse(0, TimerFor(261.63), 15);
            var x = Capture(r, 0.5, 0.6); double f0 = PulseHz(TimerFor(261.63));
            return SegaMeasure.Component(x, r.Rate, f0 * n, 4000, 4000 + 16384) / SegaMeasure.Component(x, r.Rate, f0, 4000, 4000 + 16384);
        }
        int sine = OpllPatchBank.SineIndex;
        double s2 = Harmonic(sine, 2), s3 = Harmonic(sine, 3), h2 = Harmonic(sine + 1, 2), lead = Brightness(sine + 2), sineBright = Brightness(sine);
        Console.WriteLine($"        harmonics (relative to the fundamental): sine 2nd {s2:F3} 3rd {s3:F3}; rectified 2nd {h2:F3}; fed-back lead 2nd {Harmonic(sine + 2, 2):F3} 3rd {Harmonic(sine + 2, 3):F3}, brightness {lead:F2} (the sine's {sineBright:F3})");
        Check($"the designed sine is a sine (2nd harmonic {Db(Math.Max(s2, 1e-6), 1):F0} dB, 3rd {Db(Math.Max(s3, 1e-6), 1):F0} dB; limit -34)", s2 < 0.02 && s3 < 0.02);
        Check($"the rectified sine has a strong even harmonic (2nd at {h2:F2} of the fundamental; a half-wave rectified sine has 0.42)", h2 > 0.25);
        Check($"the fed-back lead is bright: its partials 2 to 6 add up to {lead:F2} of the fundamental (a sine's {sineBright:F3})", lead > 0.2 && sineBright < 0.02);
        // while a ROM instrument plays the pulses, the triangle is still a clean sine on the user instrument
        {
            var r = Rig(0); r.Pulse(0, TimerFor(261.63), 15); r.Triangle(TriTimerFor(110)); r.Run(0.5, false);
            r.Pulse(0, TimerFor(261.63), 0);          // silence the pulse so the triangle is alone
            var x = Capture(r, 0.3, 0.6); double f0 = TriHz(TriTimerFor(110));
            double a1 = SegaMeasure.Component(x, r.Rate, f0, 4000, 4000 + 16384), a3 = SegaMeasure.Component(x, r.Rate, 3 * f0, 4000, 4000 + 16384);
            Check($"with the ROM violin on the pulses the triangle is still the designed sine (3rd harmonic {Db(Math.Max(a3, 1e-9), a1):F0} dB)", a3 / a1 < 0.02);
        }
        // while a designed patch is chosen the chip's one user instrument is that patch, and the triangle plays it too (documented)
        {
            var r = Rig(sine + 2); r.Triangle(TriTimerFor(110));
            var x = Capture(r, 0.5, 0.6); double f0 = TriHz(TriTimerFor(110));
            double a1 = SegaMeasure.Component(x, r.Rate, f0, 4000, 4000 + 16384), up = 0;
            for (int n = 2; n <= 6; n++) { double h = SegaMeasure.Component(x, r.Rate, n * f0, 4000, 4000 + 16384) / a1; up += h * h; }
            Check($"with the designed fed-back lead chosen the triangle plays it too (partials 2 to 6 add up to {Math.Sqrt(up):F2} of the fundamental): the chip has one user instrument", Math.Sqrt(up) > 0.2);
        }
    }

    private static void Noise()
    {
        {
            var r = Rig(); var p = (APU_SMSFM)r.Apu; r.Noise(15, 9, false);
            var x = Capture(r, 0.3, 0.4);
            var (_, peak) = SegaMeasure.Periodicity(x, 20, 3000);
            Check($"NES long-mode noise at index 9 is white noise at the PSG's fastest fixed rate: noise register ${p.Psg.NoiseControl:X}, audible (rms {SegaMeasure.Rms(x, 0, x.Length):F3}), no periodicity (peak {peak:F2})", p.Psg.NoiseControl == 0x4 && SegaMeasure.Rms(x, 0, x.Length) > 0.02 && peak < 0.4);
        }
        {
            var r = Rig(); var p = (APU_SMSFM)r.Apu; r.Noise(15, 9, true);
            var x = Capture(r, 0.3, 0.4);
            var (_, peak) = SegaMeasure.Periodicity(x, 20, 3000);
            Check($"NES short-mode noise is periodic noise (FB bit clear), strongly periodic (peak {peak:F2}), audible", (p.Psg.NoiseControl & 4) == 0 && peak > 0.7 && SegaMeasure.Rms(x, 0, x.Length) > 0.02);
        }
        {
            var r = Rig(); var p = (APU_SMSFM)r.Apu; r.Noise(15, 7, false); r.Run(0.2, false);
            int expect = (int)Math.Round(APU_SMSFM.ClockHz / (32.0 * (NesHz / 160.0)));
            Check($"noise index 7 (not a fixed PSG rate) uses the fourth rate through tone 3: register ${p.Psg.NoiseControl:X}, tone 3 period {p.Psg.Period(2)} (expect {expect}); the triangle is FM and never takes tone 3 away", p.Psg.NoiseControl == 7 && p.Psg.Period(2) == expect);
        }
        {
            var parts = new List<string>(); double worstSlow = 0;
            foreach (int v in new[] { 15, 10, 6, 3 })
            {
                var a = Rig(); a.Noise(v, 13, false); var xa = Capture(a, 0.3, 0.6);
                var b = new MdPatchCli.Rig("FIX"); b.Noise(v, 13, false); var xb = Capture(b, 0.3, 0.6);
                double d = Db(SegaMeasure.Rms(xa, 0, xa.Length), SegaMeasure.Rms(xb, 0, xb.Length));
                worstSlow = Math.Max(worstSlow, Math.Abs(d)); parts.Add($"v{v} {d:+0.0;-0.0}");
            }
            Check($"slow noise (index 13) is within 2 dB of APU_FIX at volumes 15, 10, 6, 3 (worst {worstSlow:F1})", worstSlow <= 2.0, string.Join(", ", parts));
        }
    }

    private static void Dmc()
    {
        var r = Rig(); var p = (APU_SMSFM)r.Apu;
        r.W(0x4010, 0x0F); r.W(0x4012, 0x00); r.W(0x4013, 0x10); r.W(0x4015, 0x1F);
        r.W(0x4011, 0x40); r.W(0x4011, 0x20); r.W(0x4011, 0x60);
        r.Run(0.05, false);
        Check($"a DMC sample start and three $4011 writes are counted as dropped ({p.DmcStartsDropped} start, {p.DmcDacWritesDropped} DAC writes) and reported in Describe()", p.DmcStartsDropped == 1 && p.DmcDacWritesDropped == 3 && p.Describe().Contains("DMC starts 1") && p.Describe().Contains("$4011 writes 3"));
        Check("the front core still runs the DMC: a $4015 read shows it playing (bit 4)", (r.Apu.ReadAPURegister(0x4015) & 0x10) != 0);
        float[] Render(bool stepFront)
        {
            var rr = Rig(0); ((APU_SMSFM)rr.Apu).StepFront = stepFront;
            rr.Pulse(0, 253, 12); rr.Pulse(1, 190, 9, 0); rr.Triangle(126); rr.Noise(8, 7, false);
            return Capture(rr, 0.3, 0.4);
        }
        var withFront = Render(true); var without = Render(false);
        double maxDiff = 0; for (int i = 0; i < withFront.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(withFront[i] - without[i]));
        Check($"the sound is bit-identical whether the front NES core is stepped or not (largest difference {maxDiff:E1}; a host that never reads the chip back skips the front to save time)", withFront.Length == without.Length && maxDiff == 0);
    }

    private static void Switching()
    {
        // a held note changes patch: pitch and loudness stay, the timbre changes (the sine has no 2nd harmonic, the fed-back lead has)
        int t = TimerFor(261.63); double want = PulseHz(t);
        int sine = OpllPatchBank.SineIndex;
        var rig = Rig(sine); var fmc = (APU_SMSFM)rig.Apu; rig.Pulse(0, t, 15);
        var a = Capture(rig, 0.4, 0.8);
        double a1 = SegaMeasure.Component(a, rig.Rate, want, 2000, 2000 + 16384), a2 = SegaMeasure.Component(a, rig.Rate, 2 * want, 2000, 2000 + 16384);
        fmc.Patch = sine + 2;      // the fed-back lead
        var b = Capture(rig, 0.3, 0.8);
        double b1 = SegaMeasure.Component(b, rig.Rate, want, 2000, 2000 + 16384), b2 = 0;
        for (int n = 2; n <= 6; n++) { double h = SegaMeasure.Component(b, rig.Rate, n * want, 2000, 2000 + 16384) / b1; b2 += h * h; }
        b2 = Math.Sqrt(b2) * b1;
        var (fb, _) = SegaMeasure.Peak(b, rig.Rate, want, 2000, 2000 + 16384, 0.03);
        Check($"switching the patch under a held note changes its timbre (partials above the fundamental: {a2 / a1:F3} of it on the sine, {b2 / b1:F2} on the fed-back lead) and keeps the pitch ({Cents(fb, want):+0.00;-0.00} cents); Patch reads {fmc.Patch}", a2 / a1 < 0.02 && b2 / b1 > 0.2 && Math.Abs(Cents(fb, want)) < 3.6 && fmc.Patch == sine + 2);
        fmc.Patch = OpllPatchBank.PsgIndex; rig.Run(0.3, false);
        Check($"switching to the PSG square moves the pulse to PSG tone 1 (period {fmc.Psg.Period(0)}, expect {t + 1}), leaves FM channel 1 silent, and the triangle stays FM", fmc.Psg.Period(0) == t + 1 && fmc.Psg.Volume(0) < 15 && !fmc.FmKeyed(0));
        fmc.Patch = 0; rig.Run(0.3, false);
        Check("switching back to an FM patch silences the PSG tone and keys the FM channel again, on the violin (instrument 1)", fmc.Psg.Volume(0) == 15 && fmc.FmKeyed(0) && fmc.FmInstrument(0) == 1 && fmc.FmInstrument(2) == 0);
        fmc.Patch = 99;
        Check("an out-of-range patch is the last one (the PSG square), reported as such", fmc.Patch == OpllPatchBank.PsgIndex);
        fmc.Patch = -5;
        Check("a negative patch is the first", fmc.Patch == 0);
        int saved = APU_SMSFM.DefaultPatch;
        try { APU_SMSFM.DefaultPatch = 6; Check("a new core starts on APU_SMSFM.DefaultPatch", ((APU_SMSFM)new MdPatchCli.Rig("SMSFM").Apu).Patch == 6); }
        finally { APU_SMSFM.DefaultPatch = saved; }
        // the registers: instrument and volume are in $30, the pitch in $10 / $20, and the key follows the note
        {
            var r = Rig(1); var f = (APU_SMSFM)r.Apu; r.Pulse(0, t, 15); r.Run(0.1, false);
            byte r30 = f.Fm.Register(0x30), r20 = f.Fm.Register(0x20);
            Check($"the guitar (instrument 2) on FM channel 1: register $30 = {r30:X2} (instrument in the high nibble, volume in the low), key bit {(r20 >> 4) & 1}", (r30 >> 4) == 2 && (r20 & 0x10) != 0);
            r.Pulse(0, t, 0); r.Run(0.1, false);
            Check("the key goes up when the NES channel falls silent", (f.Fm.Register(0x20) & 0x10) == 0);
        }
    }

    private static void Polyphony()
    {
        foreach (int patch in new[] { 0, 7, OpllPatchBank.SineIndex + 3 })
        {
            var r = Rig(patch); var f = (APU_SMSFM)r.Apu;
            int t1 = TimerFor(261.63), t2 = TimerFor(392.0), t3 = TriTimerFor(130.81);
            r.Pulse(0, t1, 15); r.Pulse(1, t2, 12); r.Triangle(t3); r.Noise(6, 9, false);
            var x = Capture(r, 0.6, 1.0);
            double f1 = PulseHz(t1), f2 = PulseHz(t2), f3 = TriHz(t3);
            var found = new List<string>(); bool ok = true;
            foreach (var (name, hz) in new[] { ("pulse 1", f1), ("pulse 2", f2), ("triangle", f3) })
            {
                double at = SegaMeasure.Component(x, r.Rate, hz, 4000, 4000 + 16384), off = SegaMeasure.Component(x, r.Rate, hz * 1.0595, 4000, 4000 + 16384);
                found.Add($"{name} {Db(at, off):F0} dB over a semitone above");
                ok &= at > off * 3 && at > 0.005;
            }
            Check($"pulse 1, pulse 2, the triangle and the noise at once on '{P[patch].Name}': each tone is in the mix ({string.Join(", ", found)}), FM channels 1-3 keyed, the noise on the PSG (volume {f.Psg.Volume(3)})", ok && f.FmKeyed(0) && f.FmKeyed(1) && f.FmKeyed(2) && f.Psg.Volume(3) < 15);
        }
    }

    private static void PsgSquare()
    {
        double worst = 0; var bad = new List<string>();
        foreach (double hz in new[] { 110, 220, 440, 880, 1760, 3500 })
        {
            int t = TimerFor(hz); double want = PulseHz(t);
            var r = Rig(OpllPatchBank.PsgIndex); r.Pulse(0, t, 15);
            var x = Capture(r, 0.3, 1.0);
            var (f0, _) = SegaMeasure.Peak(x, r.Rate, want, 3000, 3000 + 32768, 0.03);
            double err = Cents(f0, want); worst = Math.Max(worst, Math.Abs(err));
            var f = (APU_SMSFM)r.Apu;
            if (Math.Abs(err) > 0.6 || f.Psg.Period(0) != t + 1) bad.Add($"{hz} Hz {err:+0.00;-0.00} period {f.Psg.Period(0)}");
        }
        Check($"the PSG-square patch plays the pulses on PSG tone 1 at period timer + 1, exact (worst {worst:F2} cents over 110 Hz to 3.5 kHz)", bad.Count == 0, string.Join(", ", bad));
        // a note below the PSG's range follows the low-note policy and is counted
        {
            var r = Rig(OpllPatchBank.PsgIndex); var f = (APU_SMSFM)r.Apu; f.LowNotes = LowNotePolicy.Drop; r.Pulse(0, TimerFor(65.4), 15); r.Run(0.2, false);
            Check($"a PSG-square note at 65 Hz (below the PSG's 109 Hz) is silent under the Drop policy and counted once ({f.LowNotesDropped})", f.LowNotesDropped == 1 && f.Psg.Volume(0) == 15);
            var r2 = Rig(OpllPatchBank.PsgIndex); var f2 = (APU_SMSFM)r2.Apu; f2.LowNotes = LowNotePolicy.OctaveUp; r2.Pulse(0, TimerFor(65.4), 15); r2.Run(0.2, false);
            Check($"... and plays the octave above under OctaveUp (counted: {f2.LowNotesTransposed})", f2.LowNotesTransposed == 1 && f2.Psg.Volume(0) < 15 && f2.Psg.Period(0) <= 1023);
        }
    }

    private static void Range()
    {
        {
            var r = Rig(OpllPatchBank.SineIndex); r.Pulse(0, TimerFor(55), 15);
            var x = Capture(r, 0.4, 0.8);
            Check("the NES pulse's lowest notes (55 Hz) sound on the FM chip", RmsOf(x, r.Rate, 0.2, 0.8) > 0.01);
        }
        {
            var r = Rig(OpllPatchBank.SineIndex); var f = (APU_SMSFM)r.Apu; r.Pulse(0, TimerFor(9000), 15); r.Run(0.3, false);
            Check($"a note above the FM chip's top (9 kHz) is silent and counted once ({f.NotesAboveFmRange}); the chip is not keyed", f.NotesAboveFmRange == 1 && !f.FmKeyed(0));
            var r2 = Rig(12); var f2 = (APU_SMSFM)r2.Apu; r2.Pulse(0, TimerFor(4000), 15); r2.Run(0.3, false);
            Check($"the synthesizer bass (carrier at half the base frequency) reaches half as high: 4 kHz is above its top ({f2.NotesAboveFmRange} counted), 2.5 kHz is not", f2.NotesAboveFmRange == 1);
            var r3 = Rig(12); var f3 = (APU_SMSFM)r3.Apu; r3.Pulse(0, TimerFor(2500), 15); r3.Run(0.3, false);
            Check("... and 2.5 kHz plays", f3.NotesAboveFmRange == 0 && f3.FmKeyed(0));
        }
        {
            var r = Rig(OpllPatchBank.SineIndex); r.Pulse(0, 3, 15);          // timer 3: ultrasonic, the NES pulse mutes below period 8
            var x = Capture(r, 0.2, 0.4);
            Check("an ultrasonic pulse (timer 3) is silent and the output stays finite", x.All(float.IsFinite) && SegaMeasure.Rms(x, 0, x.Length) < 1e-3);
        }
    }

    private static void Behaviour()
    {
        // a held note whose NES volume falls (an envelope) follows it, step by step
        {
            var r = Rig(OpllPatchBank.SineIndex); var f = (APU_SMSFM)r.Apu;
            r.Pulse(0, TimerFor(261.63), 15); r.Run(0.1, false); int a15 = f.FmAttenuation(0);
            r.Pulse(0, TimerFor(261.63), 8); r.Run(0.1, false); int a8 = f.FmAttenuation(0);
            r.Pulse(0, TimerFor(261.63), 2); r.Run(0.1, false); int a2 = f.FmAttenuation(0);
            Check($"the volume register follows the NES volume: volume 15 -> {a15}, 8 -> {a8}, 2 -> {a2} (0 is loudest, 3 dB a step)", a15 < a8 && a8 < a2 && a15 >= 0 && a2 <= 15);
        }
        // reset and state: a loaded state resyncs the model; Reset leaves a silent chip
        {
            var r = Rig(0); var f = (APU_SMSFM)r.Apu; r.Pulse(0, TimerFor(261.63), 15); r.Run(0.1, false);
            f.Reset(); r.Run(0.05, false);
            Check("Reset() silences the chip and the PSG", !f.FmKeyed(0) && f.Psg.Volume(0) == 15 && f.Fm.Register(0x20) == 0);
        }
        // the channel mask (the plugin's stems are made this way)
        {
            var r = Rig(OpllPatchBank.SineIndex); var f = (APU_SMSFM)r.Apu; r.Pulse(0, TimerFor(261.63), 15); r.Pulse(1, TimerFor(392), 15);
            f.SetEnabledChannels(0x02); r.Run(0.2, false);
            Check("SetEnabledChannels silences a channel: with only pulse 2 enabled, FM channel 1 is up and FM channel 2 is keyed", !f.FmKeyed(0) && f.FmKeyed(1));
        }
        // zero allocation while playing (the plugin plays this on the audio thread)
        {
            var r = Rig(0); var f = (APU_SMSFM)r.Apu;
            r.Pulse(0, 200, 15); r.Pulse(1, 150, 12); r.Triangle(120); r.Noise(8, 9, false); r.Run(0.5, false);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 40; i++)
            {
                r.Pulse(0, 200 + i * 3, 15 - i % 4); r.Pulse(1, 150 + i, 12); r.Triangle(120 + i);
                f.Patch = i % OpllPatchBank.Count;
                r.Run(0.05, false);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            // Rig.Run itself collects samples only when asked (keep=false); the bridge must allocate nothing
            Check($"no allocation while playing four voices with the notes sliding and the patch changing every 50 ms ({after - before} bytes over 2 s)", after - before == 0);
        }
    }

    private static void Timing()
    {
        // speed: how many seconds of sound one second of CPU makes (the plugin's floor is 5x with four of these running)
        var r = Rig(0); r.Pulse(0, 200, 15); r.Pulse(1, 150, 12); r.Triangle(120); r.Noise(8, 9, false);
        r.Run(0.5, false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        r.Run(10.0, false);
        sw.Stop();
        double x = 10.0 / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"        speed: {x:F1}x real time for one APU_SMSFM with four voices playing (the front NES core is stepped here; the plugin skips it)");
        Check($"one APU_SMSFM runs at {x:F1}x real time (needs 8x or better on its own: the plugin runs four and its floor is 5x)", x >= 8.0);
    }

    private static string FindRom(string given)
    {
        if (given.Length > 0 && File.Exists(given)) return given;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string c = Path.Combine(dir.FullName, "Plugin", "BrokenNes2.Plugin", "Resources", "default.nes");
            if (File.Exists(c)) return c;
        }
        string here = Path.Combine(Directory.GetCurrentDirectory(), "Plugin", "BrokenNes2.Plugin", "Resources", "default.nes");
        return File.Exists(here) ? here : "";
    }

    private static string Fnv(byte[] data) { ulong h = 14695981039346656037UL; foreach (byte b in data) { h ^= b; h *= 1099511628211UL; } return h.ToString("x16"); }

    private static (string Hash, float[] Audio, APU_SMSFM? Fm) RunNes(byte[] rom, string apu, int frames, int patch = 0)
    {
        int saved = APU_SMSFM.DefaultPatch;
        APU_SMSFM.DefaultPatch = patch;
        try
        {
            var nes = new NES { RomName = "game.nes" };
            nes.LoadROM(rom);
            var speed = nes.GetSpeedConfig(); if (speed != null) speed.NtscAccurateFrameRate = true;
            if (!nes.SetApuCore(apu)) throw new InvalidOperationException($"APU core {apu} not applied");
            var audio = new List<float>(); var buf = new float[8192];
            for (int f = 0; f < frames; f++)
            {
                nes.RunFrame();
                int n; while ((n = nes.ActiveApu!.ReadSamples(buf)) > 0) for (int i = 0; i < n; i++) audio.Add(buf[i]);
            }
            return (Fnv(nes.GetFrameBuffer()), audio.ToArray(), nes.ActiveApu as APU_SMSFM);
        }
        finally { APU_SMSFM.DefaultPatch = saved; }
    }

    private static (double Corr, int Windows, int SilentWindows) EnvelopeCorrelation(float[] a, float[] b)
    {
        const int win = 882;
        int nw = Math.Min(a.Length, b.Length) / win;
        var ea = new double[nw]; var eb = new double[nw];
        for (int w = 0; w < nw; w++) { ea[w] = SegaMeasure.Rms(a, w * win, (w + 1) * win); eb[w] = SegaMeasure.Rms(b, w * win, (w + 1) * win); }
        double ma = ea.Average(), mb = eb.Average(), cov = 0, va = 0, vb = 0; int silent = 0;
        for (int w = 0; w < nw; w++) { cov += (ea[w] - ma) * (eb[w] - mb); va += (ea[w] - ma) * (ea[w] - ma); vb += (eb[w] - mb) * (eb[w] - mb); if (eb[w] > 0.01 && ea[w] < 0.002) silent++; }
        return (cov / Math.Sqrt(va * vb), nw, silent);
    }

    private static void NesGame(string? nesRom)
    {
        string path = FindRom(nesRom ?? "");
        if (path.Length == 0) { Check("a NES ROM to run (Plugin/BrokenNes2.Plugin/Resources/default.nes, or --rom)", false, "not found"); return; }
        var rom = File.ReadAllBytes(path);
        const int frames = 900;
        var fix = RunNes(rom, "FIX", frames);
        var fmc = RunNes(rom, "SMSFM", frames);                            // the default patch (the ROM violin)
        var psg = RunNes(rom, "SMSFM", frames, OpllPatchBank.PsgIndex);    // the PSG square
        Check($"the game's picture is bit-identical on APU_SMSFM and APU_FIX after {frames} frames (the sound chip does not change the game's timing): {fmc.Hash}", fix.Hash == fmc.Hash && fix.Hash == psg.Hash, $"FIX {fix.Hash}, SMSFM {fmc.Hash}");
        double rf = SegaMeasure.Rms(fix.Audio, 0, fix.Audio.Length), rm = SegaMeasure.Rms(fmc.Audio, 0, fmc.Audio.Length);
        Check($"the game's music is audible on the Master System FM chip: rms {rm:F4} (APU_FIX {rf:F4}, {Db(rm, rf):+0.0;-0.0} dB), {fmc.Audio.Length} samples (APU_FIX {fix.Audio.Length})", rm > 0.005 && Math.Abs(fmc.Audio.Length - fix.Audio.Length) < 600);
        var env = EnvelopeCorrelation(fmc.Audio, fix.Audio);
        Check($"it is the same music: the loudness envelope correlates {env.Corr:F3} with APU_FIX's over {env.Windows} windows of 20 ms (limit 0.7: the violin's own vibrato and sustain level differ); {env.SilentWindows} windows are silent where FIX sounds", env.Corr > 0.7);
        Console.WriteLine("        violin: " + fmc.Fm!.Describe());
        Check($"the counters say what happened: FM notes were played ({fmc.Fm.FmNotes}); the DMC is reported dropped ({fmc.Fm.DmcStartsDropped} starts)", fmc.Fm.FmNotes > 20);
    }
}
