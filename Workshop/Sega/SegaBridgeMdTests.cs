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
/// The Genesis half of <c>--sega-bridge-test</c> (milestone C2): <c>mdcatalog</c> (the menus), <c>mdapu</c> (<see cref="APU_MD"/>: NES registers to the YM2612 and the PSG, with the FM patch bank), <c>mdunit</c>
/// (<see cref="MdSoundOnNes"/>: a Genesis game's sound on NES chips, the real unit inside), <c>mdgame</c> (a NES game on APU_MD, and a real Genesis game's Z80 sound driver through the unit when a ROM is given). Every figure is
/// measured in captured audio: pitch in cents against the NES formula or against the stock unit, level in decibels against APU_FIX or the stock unit, and each limit says why it is the limit.
/// </summary>
internal static class SegaBridgeMdTests
{
    private static int passes, failures;
    private static bool verbose;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }
    /// <summary>Runs one group of checks; an exception in it (a measurement of silence, an index out of range) is a failed check with the message, not a crashed run that hides every later check.</summary>
    private static void Guard(Action group, string? name = null)
    {
        try { group(); }
        catch (Exception e) { Check($"{name ?? group.Method.Name} ran to the end", false, e.GetType().Name + ": " + e.Message); }
    }
    private static void Info(string text) { if (verbose) Console.WriteLine("        " + text); }

    private const double NesHz = NesVoiceWriter.NesCpuHz;
    /// <summary>What the Genesis sound on a NES chip is documented to be under the stock unit (the NES pulse's loudest step against a full-scale FM carrier): written here so the checks do not take it from the code they check.</summary>
    private const double ScaleDb = -4.0;
    private static readonly double Scale = Math.Pow(10, ScaleDb / 20);
    private static double Cents(double hz, double want) => SegaMeasure.Cents(hz, want);
    private static double Db(double a, double b)
    {
        if (!(a > 0 && b > 0) || !double.IsFinite(a) || !double.IsFinite(b)) throw new InvalidOperationException($"a level ratio of {a} to {b} (one side is silent or not a number)");
        return SegaMeasure.Db(a, b);
    }

    /// <summary>How far, in decibels, the nearest of the NES's 15 volume steps (a 50% pulse, or the noise) is from a wanted loudness, plus 0.8 dB for the measurement and APU_FIX's own 0.4 dB: the most a bridge that picks the nearest step can be off.</summary>
    private static double StepBound(double rms, bool noise = false)
    {
        double best = double.MaxValue;
        for (int v = 1; v <= 15; v++) best = Math.Min(best, Math.Abs(Db(noise ? 0.5 * APU_SMS.NoiseOut(v) : APU_MD.PulseRms(2, v), rms)));
        return best + 0.8;
    }
    private static int TimerFor(double hz) => (int)Math.Round(NesHz / (16.0 * hz)) - 1;
    private static int TriTimerFor(double hz) => (int)Math.Round(NesHz / (32.0 * hz)) - 1;
    private static double PulseHz(int t) => NesHz / (16.0 * (t + 1));
    private static double TriHz(int t) => NesHz / (32.0 * (t + 1));
    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public static (int Passes, int Failures) Run(string[] args, Func<string, bool> want, string? nesRom, string? mdRom, bool verboseOutput)
    {
        verbose = verboseOutput; passes = failures = 0;
        if (want("mdcatalog")) { Console.WriteLine("\n== Genesis menus and factory"); Catalog(); }
        if (want("mdapu")) { Console.WriteLine("\n== APU_MD: NES registers to the YM2612 and the PSG"); Guard(Bank); Guard(Pitch); Guard(Level); Guard(CalibrationCheck); Guard(Noise); Guard(Dmc); Guard(Switching); Guard(Polyphony); Guard(PsgSquare); Guard(Range); Guard(Behaviour); Guard(Timing); }
        if (want("mdunit")) { Console.WriteLine("\n== MdSoundOnNes: a Genesis game's sound on NES chips"); Guard(UnitFactory); Guard(UnitEveryChip); Guard(UnitPitch); Guard(UnitLevel); Guard(UnitPsg); Guard(UnitDac); Guard(UnitPolyphony); Guard(UnitLowNotes); Guard(UnitCounters); Guard(UnitZ80); Guard(UnitState); Guard(UnitNoAllocation); }
        if (want("mdgame")) { Console.WriteLine("\n== a NES game on APU_MD, and a Genesis game through MdSoundOnNes"); Guard(() => NesGame(nesRom), "NesGame"); Guard(() => GenesisGame(mdRom), "GenesisGame"); }
        return (passes, failures);
    }

    // ============================================================================================ menus

    private static void Catalog()
    {
        Check("CoreRegistry discovers the NES-facing core as APU id MD", CoreRegistry.ApuIds.Contains("MD", StringComparer.OrdinalIgnoreCase) && CoreRegistry.ApuTypes["MD"] == typeof(APU_MD));
        var nes = CoreCatalog.Options(ConsoleKind.Nes, CoreSlot.Apu).FirstOrDefault(o => o.Id == "MD");
        Check("the NES APU menu lists MD under the Genesis family ('From the Genesis'), with an honest label", nes is { Family: "Genesis" } && nes.Label.Contains("DMC is dropped") && nes.Label.Contains("YM2612"), nes?.Label ?? "(missing)");
        Check("... and the NES-family sound chip list (used by the other consoles' menus) does not include it", CoreCatalog.NesFamilyOf(CoreSlot.Apu, "MD") == "Genesis" && CoreCatalog.NesFamilyOf(CoreSlot.Apu, "FIX") == "NES");
        var apu = CoreCatalog.Options(ConsoleKind.Genesis, CoreSlot.Apu);
        Check($"the Genesis APU menu: the stock unit MD first, then the sound on NES chips ({apu.Skip(1).Count(o => o.Family == "NES")} of them), then on other consoles' chips through the NES registers", apu[0].Id == "MD" && apu.Count > 5 && apu.Skip(1).All(o => o.Id.StartsWith("NES:")) && apu.Skip(1).TakeWhile(o => o.Family == "NES").Count() > 15);
        Check("... NES:FIX and NES:FIXS are offered; the NES entries' labels say mono, no FM timbre, and that the DAC becomes noise", apu.Any(o => o.Id == "NES:FIX") && apu.Any(o => o.Id == "NES:FIXS") && apu.Skip(1).Where(o => o.Family == "NES").All(o => o.Label.Contains("mono") && o.Label.Contains("no FM timbre") && o.Label.Contains("DAC")));
        Check("... the Game Boy, SNES and Master System chips are offered through the NES (C4b), the Genesis's own is not (a Genesis game on the Genesis chip is the console)", new[] { "NES:DMG", "NES:DMGS", "NES:SNES", "NES:SMS", "NES:SMSFM" }.All(id => apu.Any(o => o.Id == id && o.Family != "NES")) && apu.All(o => o.Id != "NES:MD"));
        Check("... a stored NES:FIX is kept, a stored id the menu lacks falls back to the stock unit", CoreCatalog.Resolve(ConsoleKind.Genesis, CoreSlot.Apu, "NES:FIX") == "NES:FIX" && CoreCatalog.Resolve(ConsoleKind.Genesis, CoreSlot.Apu, "NES:NOPE") == "MD");
        var mdPpu = CoreCatalog.Options(ConsoleKind.Genesis, CoreSlot.Ppu);   // the picture slot gained the NES picture chips in C3a (verified by --picture-bridge-test)
        Check("the Genesis CPU slot is unchanged (own core only) and its picture slot is its own chip first, then NES chips", CoreCatalog.Options(ConsoleKind.Genesis, CoreSlot.Cpu).Count == 1 && mdPpu[0].Id == "MD" && mdPpu.Skip(1).All(o => o.Id.StartsWith("NES:")) && mdPpu.All(o => o.Id != "NES:MD"));
        Check("the Master System and Game Gear menus still list the PSG on NES chips and no Genesis unit of their own (the Genesis chip is a hub entry NES:MD)", CoreCatalog.Options(ConsoleKind.MasterSystem, CoreSlot.Apu).All(o => o.Id != "MD") && CoreCatalog.Options(ConsoleKind.MasterSystem, CoreSlot.Apu).Any(o => o.Id == "NES:MD") && CoreCatalog.Options(ConsoleKind.GameGear, CoreSlot.Apu).Count > 5);
    }

    // ============================================================================================ APU_MD

    private static MdPatchCli.Rig Rig(int patch = 0)
    {
        var r = new MdPatchCli.Rig("MD");
        ((APU_MD)r.Apu).Patch = patch;
        return r;
    }

    private static float[] Capture(MdPatchCli.Rig r, double settle, double seconds) { r.Run(settle, false); r.Run(seconds, true); return r.Take(); }

    private static void Bank()
    {
        var P = FmPatchBank.Patches;
        Check($"the bank has {P.Length} patches: {P.Count(p => !p.Psg)} FM and the PSG square last; the sine (the triangle's voice) is in it", P.Length == 16 && P[^1].Psg && P.Count(p => p.Psg) == 1 && FmPatchBank.SineIndex >= 0 && !P[FmPatchBank.SineIndex].Psg && FmPatchBank.PsgIndex == 15);
        Check("names are unique and every patch says what it is", P.Select(p => p.Name).Distinct().Count() == P.Length && P.All(p => p.Name.Length > 0 && p.Description.Length > 20) && FmPatchBank.Names.SequenceEqual(P.Select(p => p.Name)));
        bool ranges = P.All(p => p.Algorithm is >= 0 and <= 7 && p.Feedback is >= 0 and <= 7 && p.Operators.Length == 4 && p.Operators.All(o => o.Mul is >= 0 and <= 15 && o.Tl is >= 0 and <= 127 && o.Dt is >= 0 and <= 7 && o.Ar is >= 0 and <= 31 && o.D1r is >= 0 and <= 31 && o.Sl is >= 0 and <= 15 && o.D2r is >= 0 and <= 31 && o.Rr is >= 0 and <= 15 && o.Rs is >= 0 and <= 3));
        Check("every register field of every patch is inside its register's range", ranges);
        bool integerCarriers = P.Where(p => !p.Psg).All(p => Enumerable.Range(0, 4).Where(n => (p.CarrierMask >> n & 1) != 0 && p.Operators[n].Tl < FmPatch.OffTl).All(n => p.Operators[n].Mul >= 1));
        Check("every sounding carrier is at an integer multiple (MUL 1 or more) of the note: the pitch is the NES note's, whatever the timbre", integerCarriers);
        bool holds = P.Where(p => !p.Psg).All(p => Enumerable.Range(0, 4).Where(n => (p.CarrierMask >> n & 1) != 0 && p.Operators[n].Tl < FmPatch.OffTl).All(n => p.Operators[n].D1r == 0 && p.Operators[n].Sl == 0 && p.Operators[n].D2r == 0));
        Check("every sounding carrier holds its level (no decay): the NES's own volume writes and envelope shape the note", holds);
        double nesMax = APU_MD.PulseRms(2, 15);
        var low = P.Where(p => !p.Psg && p.Rms() < nesMax * 0.94).Select(p => $"{p.Name} {Db(p.Rms(), nesMax):F1} dB").ToList();
        Check($"every FM patch can reach the loudest NES pulse step (calibrated RMS at least within 0.5 dB of {nesMax:F4})", low.Count == 0, string.Join(", ", low));
        Check("the calibration table has an entry for every FM patch and none for the PSG square", FmPatchBank.CalibrationTable.Length == P.Length && Enumerable.Range(0, P.Length).All(i => P[i].Psg ? FmPatchBank.CalibrationTable[i] == 0 : FmPatchBank.CalibrationTable[i] > 0.02));
        Check("clamping: an index out of range is the last patch (the PSG square), never an exception", FmPatchBank.Clamp(99) == 15 && FmPatchBank.Clamp(-3) == 0);

        // the pitch solver: every NES timer value, both channels
        double worst = 0, worstLow = 0; int unreachable = 0, solved = 0; double worstHz = 0;
        for (int t = 8; t <= 2047; t++)
        {
            double hz = PulseHz(t);
            if (!FmPatchBank.TryPitch(hz, APU_MD.YmClockHz, out int b, out int f, out double c)) { unreachable++; continue; }
            solved++;
            double actual = FmPatchBank.Hz(b, f, APU_MD.YmClockHz);
            if (Math.Abs(Cents(actual, hz) - c) > 1e-6) { Check("TryPitch's cents agree with the chip's own frequency formula", false, $"timer {t}"); break; }
            if (Math.Abs(c) > worst) { worst = Math.Abs(c); worstHz = hz; }
            if (hz < 110) worstLow = Math.Max(worstLow, Math.Abs(c));
        }
        double worstTri = 0; int triUnreachable = 0;
        for (int t = 2; t <= 2047; t++)
        {
            if (!FmPatchBank.TryPitch(TriHz(t), APU_MD.YmClockHz, out _, out _, out double c)) { triUnreachable++; continue; }
            worstTri = Math.Max(worstTri, Math.Abs(c));
        }
        Console.WriteLine($"        frequency registers: worst error {worst:F2} cents over {solved} pulse timers (at {worstHz:F1} Hz), {worstLow:F2} below 110 Hz, {worstTri:F2} over the triangle's whole range; {unreachable} pulse and {triUnreachable} triangle timers are above the FM chip's top (about 6.6 kHz)");
        Check("the nearest block/fnum is within 1.7 cents for every NES pulse timer the FM chip can reach (the phase counter's step: 1.65 cents at 27 Hz, under 0.9 from 54 Hz up)", solved > 1900 && worst < 1.7);
        Check("... and for every triangle timer (27 Hz up)", worstTri < 1.7);
        Check("... a pitch above the chip's top is refused (not squeezed to a wrong note): timers 8 to 30 (above 3.7 kHz are fine, above 6.7 kHz are not)", !FmPatchBank.TryPitch(8000, APU_MD.YmClockHz, out _, out _, out _) && FmPatchBank.TryPitch(6500, APU_MD.YmClockHz, out _, out _, out _) && !FmPatchBank.TryPitch(0, APU_MD.YmClockHz, out _, out _, out _));
    }

    private static void Pitch()
    {
        // ---- pulses: every patch, three notes. The fundamental is measured from the audio (a windowed transform refined to a fraction of a cent) ----
        var rows = new List<string>(); double worstAll = 0; int subOk = 0, fmCount = 0;
        foreach (int note in new[] { 45, 60, 81 })
        {
            double hzWant = 440.0 * Math.Pow(2, (note - 69) / 12.0); int t = TimerFor(hzWant); double want = PulseHz(t);
            double worst = 0; var bad = new List<string>();
            for (int p = 0; p < FmPatchBank.Count; p++)
            {
                var rig = Rig(p); rig.Pulse(0, t, 15);
                var x = Capture(rig, 0.8, 1.0);
                int from = 4000, to = from + 32768;
                var (f0, a1) = SegaMeasure.Peak(x, rig.Rate, want, from, to, 0.03);
                double err = Cents(f0, want);
                var md = (APU_MD)rig.Apu;
                double limit = FmPatchBank.Patches[p].Psg ? 0.5 : 1.2;
                if (Math.Abs(err) > limit) bad.Add($"{FmPatchBank.Patches[p].Name} {err:+0.00;-0.00}");
                worst = Math.Max(worst, Math.Abs(err));
                if (!FmPatchBank.Patches[p].Psg)
                {
                    fmCount++;
                    // the fundamental is the lowest strong partial: nothing at half the frequency (a wrong MUL or an octave error would put energy there)
                    double sub = SegaMeasure.Component(x, rig.Rate, f0 / 2, from, to), top = Enumerable.Range(1, 6).Max(h => SegaMeasure.Component(x, rig.Rate, f0 * h, from, to));
                    if (Db(Math.Max(sub, 1e-9), top) < -40) subOk++;
                }
            }
            worstAll = Math.Max(worstAll, worst);
            rows.Add($"{Names[((note % 12) + 12) % 12]}{note / 12 - 1} {want:F1} Hz worst {worst:F2} c");
            Check($"all 16 patches play {Names[note % 12]}{note / 12 - 1} ({want:F2} Hz, NES timer {t}) at the NES pitch: worst {worst:F2} cents (limit 1.2 for FM: the chip's phase-counter step; 0.5 for the PSG square)", bad.Count == 0, string.Join(", ", bad));
        }
        Check($"... and for every FM patch the fundamental is the lowest strong partial (nothing within 40 dB at half its frequency): {subOk} of {fmCount} note-patch pairs", subOk == fmCount);
        Console.WriteLine($"        pulse pitch through the whole path: worst {worstAll:F2} cents over 48 note-patch pairs ({string.Join("; ", rows)})");

        // ---- pulse 2 and the triangle, across their ranges (the sine patch carries the triangle) ----
        double worstTri = 0, worstP2 = 0; var tbad = new List<string>();
        foreach (double hz in new[] { 27.5, 41.2, 55, 110, 220, 440, 880, 1760, 3520 })
        {
            int t = TriTimerFor(hz); double want = TriHz(t);
            if (t > 2047) continue;
            var rig = Rig(); rig.Triangle(t);
            var x = Capture(rig, 0.8, 1.6);
            var (f0, _) = SegaMeasure.Peak(x, rig.Rate, want, 4000, 4000 + 65536, 0.05);
            double err = Cents(f0, want); worstTri = Math.Max(worstTri, Math.Abs(err));
            if (Math.Abs(err) > 1.8) tbad.Add($"{hz} Hz {err:+0.00;-0.00}");
        }
        foreach (double hz in new[] { 55, 110, 440, 1760 })
        {
            int t = TimerFor(hz); double want = PulseHz(t);
            var rig = Rig(0); rig.Pulse(1, t, 15);
            var x = Capture(rig, 0.8, hz < 100 ? 1.6 : 1.0);
            var (f0, _) = SegaMeasure.Peak(x, rig.Rate, want, 4000, 4000 + (hz < 100 ? 65536 : 32768), 0.05);
            worstP2 = Math.Max(worstP2, Math.Abs(Cents(f0, want)));
            var md = (APU_MD)rig.Apu;
            if (!md.FmKeyed(1) || md.FmKeyed(0)) tbad.Add("pulse 2 is not on FM voice 2");
        }
        Check($"the triangle (FM voice 3, a sine) plays 27 Hz to 3.5 kHz at the NES pitch: worst {worstTri:F2} cents (limit 1.8: the phase-counter step at 27 Hz is 1.65 cents); pulse 2 on FM voice 2: worst {worstP2:F2} cents", tbad.Count == 0 && worstP2 < 1.2, string.Join(", ", tbad));

        // ---- the chip is told the pitch the NES asked for: the registers say so too (cents between the NES note and the frequency the FM chip is set to) ----
        {
            var rig = Rig(0); var md = (APU_MD)rig.Apu; rig.Pulse(0, TimerFor(440), 15); rig.Run(0.1, false);
            Check($"the chip's set frequency is reported ({md.FmHz(0):F3} Hz for the NES note {PulseHz(TimerFor(440)):F3} Hz = {md.FmCents(0):+0.000;-0.000} cents)", md.FmKeyed(0) && Math.Abs(md.FmCents(0)) < 0.9 && Math.Abs(Cents(md.FmHz(0), PulseHz(TimerFor(440)))) < 0.9);
        }
    }

    /// <summary>The AC RMS of a captured note after it settles: seconds <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static double RmsOf(float[] x, int rate, double from, double to)
    {
        if ((int)(to * rate) > x.Length + rate / 50 || (int)(from * rate) >= x.Length) throw new InvalidOperationException($"the window {from}-{to} s lies outside the {x.Length / (double)rate:F2} s that was captured");
        double r = SegaMeasure.Rms(x, (int)(from * rate), Math.Min(x.Length, (int)(to * rate)));
        if (!double.IsFinite(r)) throw new InvalidOperationException("non-finite RMS");
        return r;
    }

    private static void Level()
    {
        // ---- every patch at C4, volume 15, against APU_FIX at the same registers (what a NES chip would play) ----
        var fix = new MdPatchCli.Rig("FIX"); fix.Pulse(0, TimerFor(261.63), 15);
        var fx = Capture(fix, 0.5, 3.5); double fixRms = RmsOf(fx, fix.Rate, 1.5, 3.5);
        double worst = 0; var rows = new List<string>(); var bad = new List<string>();
        for (int p = 0; p < FmPatchBank.Count; p++)
        {
            var rig = Rig(p); rig.Pulse(0, TimerFor(261.63), 15);
            var x = Capture(rig, 0.5, 3.5); double rms = RmsOf(x, rig.Rate, 1.5, 3.5);
            double d = Db(rms, fixRms); worst = Math.Max(worst, Math.Abs(d));
            rows.Add($"{FmPatchBank.Patches[p].Name} {d:+0.0;-0.0}");
            if (Math.Abs(d) > 2.5) bad.Add($"{FmPatchBank.Patches[p].Name} {d:+0.0;-0.0} dB");
        }
        Console.WriteLine($"        level against APU_FIX (C4, volume 15, 2 s after the attack): {string.Join(", ", rows)} dB; worst {worst:F1} dB");
        Check($"all 16 patches play the loudest NES pulse step within 2.5 dB of APU_FIX (worst {worst:F1} dB; the limit is the patch's own slow beating and APU_FIX's own filter, which is 0.3 dB under the NES formula)", bad.Count == 0, string.Join(", ", bad));

        // ---- every FM patch at four NES volumes (110 Hz, so FIX's own low-pass does not touch it): the carriers' total level must scale the sound linearly, patch by patch ----
        {
            var fixAt = new Dictionary<int, double>();
            foreach (int v in new[] { 15, 8, 4, 2 }) { var b = new MdPatchCli.Rig("FIX"); b.Pulse(0, TimerFor(110), v); fixAt[v] = RmsOf(Capture(b, 1.5, 1.2), b.Rate, 0.1, 1.2); }
            var vbad = new List<string>(); double worstAll = 0;
            for (int p = 0; p < FmPatchBank.Count; p++)
            {
                if (FmPatchBank.Patches[p].Psg) continue;
                foreach (int v in new[] { 15, 8, 4, 2 })
                {
                    var a = Rig(p); a.Pulse(0, TimerFor(110), v); var xa = Capture(a, 1.5, 1.2);
                    double d = Db(RmsOf(xa, a.Rate, 0.1, 1.2), fixAt[v]); worstAll = Math.Max(worstAll, Math.Abs(d));
                    if (Math.Abs(d) > 2.5) vbad.Add($"{FmPatchBank.Patches[p].Name} v{v} {d:+0.0;-0.0}");
                }
            }
            Check($"every FM patch at NES volumes 15, 8, 4 and 2 (110 Hz) is within 2.5 dB of APU_FIX: the level is linear in the carriers' total level (worst {worstAll:F1} dB over {15 * 4} patch-volume pairs)", vbad.Count == 0, string.Join(", ", vbad));
        }

        // ---- the whole range of NES volumes and duty cycles on one patch each of three kinds (110 Hz, so FIX's own low-pass does not touch it). Two references: the NES mixer's own formula (what the bridge aims at) and APU_FIX's output ----
        {
            double worstFix = 0, worstFormula = 0; var fbad = new List<string>(); var fixRows = new List<string>(); double worstFixVsFormula = 0;
            foreach (var (duty, dutyName) in new[] { (0, "12.5%"), (1, "25%"), (2, "50%"), (3, "75%") })
                foreach (int v in new[] { 15, 12, 9, 6, 4, 3, 2, 1 })
                {
                    var b = new MdPatchCli.Rig("FIX"); b.Pulse(0, TimerFor(110), v, duty); double rf = RmsOf(Capture(b, 1.5, 1.2), b.Rate, 0.1, 1.2);
                    double formula = APU_MD.PulseRms(duty, v), dFixFormula = Db(rf, formula);
                    if (v >= 4) worstFixVsFormula = Math.Max(worstFixVsFormula, Math.Abs(dFixFormula));
                    if (duty == 2 && v <= 3) fixRows.Add($"v{v} {dFixFormula:+0.0;-0.0}");
                    foreach (int patch in new[] { FmPatchBank.SineIndex, 0, 2 })
                    {
                        var a2 = Rig(patch); a2.Pulse(0, TimerFor(110), v, duty); double ra = RmsOf(Capture(a2, 1.5, 1.2), a2.Rate, 0.1, 1.2);
                        double dFormula = Db(ra, formula), dFix = Db(ra, rf);
                        worstFormula = Math.Max(worstFormula, Math.Abs(dFormula));
                        double limit = v >= 3 ? 2.5 : 4.5;       // APU_FIX's two quietest steps (and the 12.5% pulse's) are below the NES mixer formula: see the line above
                        if (v >= 3) worstFix = Math.Max(worstFix, Math.Abs(dFix));
                        if (Math.Abs(dFormula) > 1.5 || Math.Abs(dFix) > limit) fbad.Add($"'{FmPatchBank.Patches[patch].Name}' {dutyName} v{v}: {dFormula:+0.0;-0.0} dB from the formula, {dFix:+0.0;-0.0} from FIX");
                    }
                }
            Console.WriteLine($"        APU_FIX against the NES mixer formula (50% duty, 110 Hz): worst {worstFixVsFormula:F1} dB at volumes 4 to 15; at the quietest steps {string.Join(", ", fixRows)} dB (FIX is quieter than the formula there: APU_MD aims at the formula)");
            Check($"the sine, the brass and the additive square at all 15 NES volumes and four duty cycles: within 1.5 dB of the NES mixer formula (worst {worstFormula:F1} dB) and within 2.5 dB of APU_FIX from volume 3 up (worst {worstFix:F1}; 4.5 dB at volumes 1-2, where APU_FIX itself is under the formula)", fbad.Count == 0 && worstFixVsFormula < 1.5, string.Join("; ", fbad));
        }
        {
            var seq = new List<double>();
            foreach (int v in new[] { 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 }) { var a2 = Rig(0); a2.Pulse(0, TimerFor(110), v); seq.Add(RmsOf(Capture(a2, 1.5, 0.8), a2.Rate, 0.1, 0.8)); }
            bool mono = true; for (int i = 1; i < seq.Count; i++) mono &= seq[i] <= seq[i - 1] * 1.001;
            Check($"the level never rises as the NES volume falls from 15 to 1 and covers {Db(seq[0], seq[^1]):F1} dB (the NES's own range is 22 dB)", mono && Db(seq[0], seq[^1]) > 20);
        }
        {
            var z = Rig(0); z.Pulse(0, TimerFor(261.63), 0);
            Check("volume 0 is silent", RmsOf(Capture(z, 0.6, 0.4), z.Rate, 0.0, 0.4) < 1e-4);
        }

        // ---- the triangle against the NES triangle (a fixed level) ----
        {
            var a = Rig(); a.Triangle(TriTimerFor(110)); var xa = Capture(a, 0.8, 1.5);
            var b = new MdPatchCli.Rig("FIX"); b.Triangle(TriTimerFor(110)); var xb = Capture(b, 0.8, 1.5);
            double d = Db(RmsOf(xa, a.Rate, 0.2, 1.4), RmsOf(xb, b.Rate, 0.2, 1.4));
            Check($"the triangle (a sine at the NES triangle's fixed loudness) is {d:+0.0;-0.0} dB from the NES triangle (limit 2.0)", Math.Abs(d) <= 2.0);
        }
    }

    private static void CalibrationCheck()
    {
        // The calibration table is a measurement of the FM core; this measures every patch again and fails when the chip no longer plays one at the recorded level (a patch edited without calibrating, or the FM core changed).
        var tbl = FmPatchBank.CalibrationTable; var bad = new List<string>(); double worst = 0;
        for (int p = 0; p < FmPatchBank.Count; p++)
        {
            var patch = FmPatchBank.Patches[p];
            if (patch.Psg) continue;
            var (rms, lo, hi) = MdPatchCli.SteadyRms(patch);
            double d = Db(rms, tbl[p]); worst = Math.Max(worst, Math.Abs(d));
            double allowed = Db(hi, lo) > 3 ? 1.0 : 0.3;       // a patch with two carriers a few cents apart beats slowly: a few seconds of it average to within a dB
            if (Math.Abs(d) > allowed) bad.Add($"{patch.Name} measured {rms:F5} vs table {tbl[p]:F5} ({d:+0.00;-0.00} dB)");
        }
        Check($"the calibration table matches the chip: every FM patch measured again (6 s of C4) is within 0.3 dB of its recorded level (1 dB for the two chorus patches, which beat); worst {worst:F2} dB", bad.Count == 0, string.Join("; ", bad));
        // the level scale really is linear in the carriers' total level: 16 steps of 0.75 dB = 12 dB
        {
            var savedModel = APU_MD.DefaultModel; double r0, r16, r40;
            try { APU_MD.DefaultModel = GenesisModel.Model2; r0 = FullRmsAt(0); r16 = FullRmsAt(16); r40 = FullRmsAt(40); }      // the integrated chip has no ladder: the steps are the pure 0.75 dB
            finally { APU_MD.DefaultModel = savedModel; }
            Check($"carrier total level is 0.75 dB a step: 16 steps lower is {Db(r16, r0):F2} dB (expect -12.00), 40 steps lower {Db(r40, r0):F2} dB (expect -30.00)", Math.Abs(Db(r16, r0) + 12.0) < 0.2 && Math.Abs(Db(r40, r0) + 30.0) < 0.3);
        }
    }

    /// <summary>The RMS of a sine carrier whose total level is <paramref name="steps"/>, with the bridge's own attenuation divided back out (a custom patch, played through the whole APU_MD).</summary>
    private static double FullRmsAt(int steps)
    {
        var p = new FmPatch("probe", "", 7, 0, new[] { new FmOperator(1, 127), new FmOperator(1, 127), new FmOperator(1, 127), new FmOperator(1, steps) });
        var rig = new MdPatchCli.Rig("MD"); var md = (APU_MD)rig.Apu; md.UseCustomPatch(p);
        rig.Pulse(0, TimerFor(261.63), 15); rig.Run(0.5, false); rig.Run(1.0, true);
        var x = rig.Take();
        return SegaMeasure.Rms(x, 0, x.Length) / FmPatchBank.Amplitude(md.FmAttenuation(0));
    }

    private static void Noise()
    {
        // the PSG noise, as APU_SMS does it, but tone 3 is always free (the triangle is FM), so any NES rate is exact
        {
            var r = Rig(); var p = (APU_MD)r.Apu; r.Noise(15, 9, false);
            var x = Capture(r, 0.3, 0.4);
            var (_, peak) = SegaMeasure.Periodicity(x, 20, 3000);
            Check($"NES long-mode noise at index 9 is white noise at the PSG's fastest fixed rate: noise register ${p.Psg.NoiseControl:X}, audible (rms {SegaMeasure.Rms(x, 0, x.Length):F3}), no periodicity (peak {peak:F2})", p.Psg.NoiseControl == 0x4 && SegaMeasure.Rms(x, 0, x.Length) > 0.02 && peak < 0.4);
        }
        {
            var r = Rig(); var p = (APU_MD)r.Apu; r.Noise(15, 9, true);
            var x = Capture(r, 0.3, 0.4);
            var (_, peak) = SegaMeasure.Periodicity(x, 20, 3000);
            Check($"NES short-mode noise is periodic noise (FB bit clear), strongly periodic (peak {peak:F2}), audible", (p.Psg.NoiseControl & 4) == 0 && peak > 0.7 && SegaMeasure.Rms(x, 0, x.Length) > 0.02);
        }
        {
            // buzz pitch of the short mode against APU_FIX over indices 5 to 11
            double worst = 0; var parts = new List<string>();
            foreach (int idx in new[] { 5, 6, 7, 8, 9, 10, 11 })
            {
                var a = Rig(); a.Noise(15, idx, true); var xa = Capture(a, 0.3, 0.5);
                var b = new MdPatchCli.Rig("FIX"); b.Noise(15, idx, true); var xb = Capture(b, 0.3, 0.5);
                var pa = SegaMeasure.Periodicity(xa, 30, 3000); var pb = SegaMeasure.Periodicity(xb, 30, 3000);
                double err = pa.Lag > 0 && pb.Lag > 0 ? Cents(44100.0 / pa.Lag, 44100.0 / pb.Lag) : double.NaN;
                if (!double.IsNaN(err)) worst = Math.Max(worst, Math.Abs(err));
                parts.Add($"idx {idx} {err:+0;-0} c");
            }
            Check($"short-mode buzz keeps its pitch within {worst:F0} cents of APU_FIX over noise indices 5-11 (limit 60: whole autocorrelation lags and the PSG's integer tone-3 periods)", worst < 60, string.Join("; ", parts));
        }
        {
            var r = Rig(); var p = (APU_MD)r.Apu; r.Noise(15, 7, false); r.Run(0.2, false);
            int expect = (int)Math.Round(APU_MD.PsgClockHz / (32.0 * (NesHz / 160.0)));
            Check($"noise index 7 (11.2 kHz, not a fixed PSG rate) uses the fourth rate through tone 3: register ${p.Psg.NoiseControl:X}, tone 3 period {p.Psg.Period(2)} (expect {expect}); the triangle never takes tone 3 away here", p.Psg.NoiseControl == 7 && p.Psg.Period(2) == expect);
            r.Triangle(253); r.Run(0.2, false);
            Check("... even while the triangle sounds (it is on the FM chip): the noise rate stays exact", p.Psg.NoiseControl == 7 && p.Psg.Period(2) == expect && ((APU_MD)r.Apu).FmKeyed(2));
        }
        {
            // The noise is replayed at the NES noise's own AC RMS. APU_FIX low-passes its output (the NES's analog stage), which takes about 3 dB out of noise whose shift rate is 7 kHz and nothing out of slow noise:
            // so the level is compared on slow noise (index 13, 1.76 kHz shift rate) within 2 dB, and the fast noise (index 9) is held to 4.5 dB and its excess is shown to be the filter's.
            var parts = new List<string>(); double worstSlow = 0, worstFast = 0;
            foreach (int v in new[] { 15, 10, 6, 3 })
                foreach (var (idx, fast) in new[] { (13, false), (9, true) })
                {
                    var a = Rig(); a.Noise(v, idx, false); var xa = Capture(a, 0.3, 0.6);
                    var b = new MdPatchCli.Rig("FIX"); b.Noise(v, idx, false); var xb = Capture(b, 0.3, 0.6);
                    double d = Db(SegaMeasure.Rms(xa, 0, xa.Length), SegaMeasure.Rms(xb, 0, xb.Length));
                    if (fast) worstFast = Math.Max(worstFast, Math.Abs(d)); else worstSlow = Math.Max(worstSlow, Math.Abs(d));
                    parts.Add($"{(fast ? "fast" : "slow")} v{v} {d:+0.0;-0.0}");
                }
            Check($"noise level against APU_FIX: slow noise (index 13) within 2 dB at volumes 15, 10, 6, 3 (worst {worstSlow:F1}), fast noise (index 9) within 4.5 dB (worst {worstFast:F1}: APU_FIX's own output low-pass takes about 3 dB out of 7 kHz noise)", worstSlow <= 2.0 && worstFast <= 4.5, string.Join(", ", parts));
        }
        {
            var r = Rig(); var p = (APU_MD)r.Apu; r.Noise(15, 9, false); r.Run(0.1, false);
            long before = p.Syncs; int control = p.Psg.NoiseControl;
            r.Run(0.2, false);
            Check("a held noise does not rewrite the noise register (that would reload the shift register every sync)", p.Psg.NoiseControl == control && p.Syncs > before + 100);
        }
    }

    private static void Dmc()
    {
        var r = Rig(); var p = (APU_MD)r.Apu;
        r.W(0x4010, 0x0F); r.W(0x4012, 0x00); r.W(0x4013, 0x10); r.W(0x4015, 0x1F);
        r.W(0x4011, 0x40); r.W(0x4011, 0x20); r.W(0x4011, 0x60);
        r.Run(0.05, false);
        Check($"a DMC sample start and three $4011 writes are counted as dropped ({p.DmcStartsDropped} start, {p.DmcDacWritesDropped} DAC writes) and reported in Describe()", p.DmcStartsDropped == 1 && p.DmcDacWritesDropped == 3 && p.Describe().Contains("DMC starts 1") && p.Describe().Contains("$4011 writes 3"));
        Check("the front core still runs the DMC: a $4015 read shows it playing (bit 4)", (r.Apu.ReadAPURegister(0x4015) & 0x10) != 0);
        var a = Rig(); a.W(0x4017, 0x00); a.Run(0.02, false);
        bool irq = (a.Apu.ReadAPURegister(0x4015) & 0x40) != 0;
        var q = Rig(); q.W(0x4010, 0x0F); q.W(0x4012, 0x00); q.W(0x4013, 0x01); q.W(0x4015, 0x1F);
        bool on = (q.Apu.ReadAPURegister(0x4015) & 0x10) != 0; q.Run(0.03, false); bool off = (q.Apu.ReadAPURegister(0x4015) & 0x10) == 0;
        Check("the front core keeps what a game can read real: the frame IRQ flag rises ($4015 bit 6), a short DMC sample plays out (bit 4 set, then clear)", irq && on && off);
        // the plugin's switch: with the front off the sound is bit-identical (nothing reads the chip back)
        float[] Render(bool stepFront)
        {
            var rr = Rig(0); ((APU_MD)rr.Apu).StepFront = stepFront;
            rr.Pulse(0, 253, 12); rr.Pulse(1, 190, 9, 0); rr.Triangle(126); rr.Noise(8, 7, false);
            return Capture(rr, 0.3, 0.4);
        }
        var withFront = Render(true); var without = Render(false);
        double maxDiff = 0; for (int i = 0; i < withFront.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(withFront[i] - without[i]));
        Check($"the sound is bit-identical whether the front NES core is stepped or not (largest difference {maxDiff:E1}; a host that never reads the chip back skips the front to save time)", withFront.Length == without.Length && maxDiff == 0);
    }

    private static void Switching()
    {
        // a held note changes patch: the pitch and the loudness stay, the timbre changes (the third harmonic of a sine is nothing, of the additive square it is 1/3)
        int t = TimerFor(261.63); double want = PulseHz(t);
        var rig = Rig(FmPatchBank.SineIndex); var md = (APU_MD)rig.Apu; rig.Pulse(0, t, 15);
        var a = Capture(rig, 0.6, 0.8);
        double a1 = SegaMeasure.Component(a, rig.Rate, want, 2000, 2000 + 32768), a3 = SegaMeasure.Component(a, rig.Rate, 3 * want, 2000, 2000 + 32768);
        double rmsA = RmsOf(a, rig.Rate, 0.2, 0.8);
        md.Patch = 2;      // Square (additive)
        var b = Capture(rig, 0.3, 0.8);
        double b1 = SegaMeasure.Component(b, rig.Rate, want, 2000, 2000 + 32768), b3 = SegaMeasure.Component(b, rig.Rate, 3 * want, 2000, 2000 + 32768);
        var (fb, _) = SegaMeasure.Peak(b, rig.Rate, want, 2000, 2000 + 32768, 0.03);
        double rmsB = RmsOf(b, rig.Rate, 0.2, 0.8);
        Check($"switching the patch under a held note changes its timbre (3rd harmonic {Db(a3, a1):F0} dB on the sine, {Db(b3, b1):F1} dB on the square: expect -9.5) and keeps the pitch ({Cents(fb, want):+0.00;-0.00} cents) and the loudness ({Db(rmsB, rmsA):+0.0;-0.0} dB)", Db(a3, a1) < -40 && Math.Abs(Db(b3, b1) + 9.5) < 1.5 && Math.Abs(Cents(fb, want)) < 1.2 && Math.Abs(Db(rmsB, rmsA)) < 1.5 && md.Patch == 2);
        md.Patch = 15; rig.Run(0.3, false);
        Check($"switching to the PSG square moves the pulse to PSG tone 1 (period {md.Psg.Period(0)}, expect {t + 1}), leaves FM voice 1 silent, and the triangle stays FM", md.Psg.Period(0) == t + 1 && md.Psg.Volume(0) < 15 && !md.FmKeyed(0));
        md.Patch = 3; rig.Run(0.3, false);
        Check("switching back to an FM patch silences the PSG tone and keys the FM voice again", md.Psg.Volume(0) == 15 && md.FmKeyed(0));
        md.Patch = 99;
        Check("an out-of-range patch is the last one (the PSG square), reported as such", md.Patch == 15);
        md.Patch = -5;
        Check("a negative patch is the first", md.Patch == 0);
        {
            // the app's setting reaches a core that is already running, and every new one (the desktop's panel and Lite's picker call this)
            int savedDefault = APU_MD.DefaultPatch;
            try
            {
                var live = Rig(0); var lm = (APU_MD)live.Apu; live.Pulse(0, t, 15); live.Run(0.1, false);
                APU_MD.SetPatchEverywhere(7); live.Run(0.1, false);
                bool liveOk = lm.Patch == 7 && lm.Describe().Contains($"'{FmPatchBank.Patches[7].Name}'");
                APU_MD.SetPatchEverywhere(99);
                int fresh = ((APU_MD)new MdPatchCli.Rig("MD").Apu).Patch;
                Check($"APU_MD.SetPatchEverywhere (the desktop's panel, Lite's picker) switches a running core at its next sync (now '{FmPatchBank.Patches[7].Name}': {liveOk}) and sets the start patch for new ones (out of range is the last: default {APU_MD.DefaultPatch}, a new core {fresh}, expect 15)", liveOk && APU_MD.DefaultPatch == 15 && fresh == 15);
            }
            finally { APU_MD.SetPatchEverywhere(savedDefault); }
        }
        // the default for a new core is the static setting
        int saved = APU_MD.DefaultPatch;
        try { APU_MD.DefaultPatch = 6; Check("a new core starts on APU_MD.DefaultPatch (the desktop's setting)", ((APU_MD)new MdPatchCli.Rig("MD").Apu).Patch == 6); }
        finally { APU_MD.DefaultPatch = saved; }
    }

    private static void Polyphony()
    {
        foreach (int patch in new[] { 0, 2, 13 })
        {
            var r = Rig(patch); var md = (APU_MD)r.Apu;
            int t1 = TimerFor(261.63), t2 = TimerFor(392.0), t3 = TriTimerFor(130.81);
            r.Pulse(0, t1, 15); r.Pulse(1, t2, 12); r.Triangle(t3); r.Noise(6, 9, false);
            var x = Capture(r, 0.8, 1.0);
            double f1 = PulseHz(t1), f2 = PulseHz(t2), f3 = TriHz(t3);
            var found = new List<string>(); bool ok = true;
            foreach (var (name, hz) in new[] { ("pulse 1", f1), ("pulse 2", f2), ("triangle", f3) })
            {
                double at = SegaMeasure.Component(x, r.Rate, hz, 4000, 4000 + 32768), off = SegaMeasure.Component(x, r.Rate, hz * 1.0595, 4000, 4000 + 32768);
                found.Add($"{name} {Db(at, off):F0} dB over a semitone above");
                ok &= at > off * 4 && at > 0.01;
            }
            Check($"pulse 1, pulse 2, the triangle and the noise at once on '{FmPatchBank.Patches[patch].Name}': each tone is in the mix ({string.Join(", ", found)}), FM voices 1-3 keyed, the noise on the PSG (volume {md.Psg.Volume(3)})", ok && md.FmKeyed(2) && (patch == 15 || md.FmKeyed(0) && md.FmKeyed(1)) && md.Psg.Volume(3) < 15);
        }
        {
            double single = RmsOf(Capture(Rig(0).Also(r => r.Pulse(0, TimerFor(261.63), 15)), 0.8, 1.2), 44100, 0.2, 1.2);
            var r = Rig(0); r.Pulse(0, TimerFor(261.63), 15); r.Pulse(1, TimerFor(392.0), 15); r.Triangle(TriTimerFor(130.81)); r.Noise(8, 9, false);
            double all = RmsOf(Capture(r, 0.8, 1.2), 44100, 0.2, 1.2);
            Check($"four voices ({all:F3}) are louder than one ({single:F3})", all > single * 1.5);
        }
    }

    private static T Also<T>(this T value, Action<T> action) { action(value); return value; }

    private static void PsgSquare()
    {
        var saved = APU_MD.DefaultLowNotes;
        try
        {
            APU_MD.DefaultLowNotes = LowNotePolicy.Drop;
            var r = Rig(15); var p = (APU_MD)r.Apu; r.Pulse(0, 1500, 15); r.W(0x4001, 0x08);    // 74 Hz: below the PSG's lowest tone (109 Hz)
            var x = Capture(r, 0.3, 0.3);
            Check("PSG square, policy Drop: a pulse below the PSG's range (74 Hz) is silent and counted once; FM is not asked to play it (that is the point of this patch)", SegaMeasure.Rms(x, 0, x.Length) < 1e-4 && p.LowNotesDropped == 1 && p.LowNotesTransposed == 0 && !p.FmKeyed(0), $"rms {SegaMeasure.Rms(x, 0, x.Length):E1}, dropped {p.LowNotesDropped}");
            r.Run(0.1, false);
            Check("... held longer it is still one note", p.LowNotesDropped == 1);
            APU_MD.DefaultLowNotes = LowNotePolicy.OctaveUp;
            var q = Rig(15); var pq = (APU_MD)q.Apu; q.Pulse(0, 1500, 15); q.W(0x4001, 0x08);
            var y = Capture(q, 0.5, 1.0);
            double ideal = PulseHz(1500), got = SegaMeasure.Peak(y, q.Rate, ideal * 2, 4000, 4000 + 32768, 0.03).Hz;
            Check($"PSG square, policy OctaveUp: the same note plays an octave up ({got:F2} Hz, {Cents(got, ideal * 2):+0.00;-0.00} cents from exactly 2x; an odd period is up to 1.2 cents off), counted as transposed", Math.Abs(Cents(got, ideal * 2)) < 1.5 && pq.LowNotesTransposed == 1 && pq.LowNotesDropped == 0);
        }
        finally { APU_MD.DefaultLowNotes = saved; }
        {
            // the pulse's own pitch and its duty-dependent loudness through the PSG tone: as APU_SMS
            var r = Rig(15); var p = (APU_MD)r.Apu; double worst = 0;
            foreach (int t in new[] { 1022, 508, 253, 127, 63, 31 })
            {
                var rig = Rig(15); var pp = (APU_MD)rig.Apu; rig.Pulse(0, t, 15);
                var x = Capture(rig, 0.4, 1.0);
                var (f0, _) = SegaMeasure.Peak(x, rig.Rate, PulseHz(t), 4000, 4000 + 32768, 0.03);
                worst = Math.Max(worst, Math.Abs(Cents(f0, PulseHz(t))));
                if (pp.Psg.Period(0) != t + 1) { Check($"PSG square: timer {t} is PSG period {t + 1}", false, $"period {pp.Psg.Period(0)}"); break; }
            }
            Check($"PSG square: the PSG's tone period is the NES timer + 1 exactly and the pitch error is {worst:F3} cents over 109 Hz to 3.5 kHz", worst < 0.5);
        }
    }

    private static void Range()
    {
        // an FM note above the chip's top (about 6.6 kHz) is silenced and counted once, never played at a wrong pitch
        var r = Rig(0); var p = (APU_MD)r.Apu; r.Pulse(0, 8, 15);    // timer 8: 12.4 kHz
        var x = Capture(r, 0.2, 0.3);
        Check($"a pulse at 12.4 kHz (above the FM chip's top) is silent and counted once ({p.NotesAboveFmRange})", SegaMeasure.Rms(x, 0, x.Length) < 1e-4 && p.NotesAboveFmRange == 1 && !p.FmKeyed(0));
        r.Run(0.1, false);
        Check("... held longer it is still one note", p.NotesAboveFmRange == 1);
        r.Pulse(0, TimerFor(440), 15); r.Run(0.2, false); r.Pulse(0, 8, 15); r.Run(0.1, false);
        Check("a second one is a second count", p.NotesAboveFmRange == 2, $"{p.NotesAboveFmRange}");
        // the whole NES pitch range plays: 54 Hz pulse bottom and 27 Hz triangle bottom, no note is lost to the FM chip's range
        var lo = Rig(0); var plo = (APU_MD)lo.Apu; lo.Pulse(0, 2047, 15); lo.Triangle(2047); lo.Run(0.3, false);
        Check($"the NES's lowest notes (pulse 54.6 Hz, triangle 27.3 Hz) play on FM: {plo.FmHz(0):F2} Hz and {plo.FmHz(2):F2} Hz, nothing dropped", plo.FmKeyed(0) && plo.FmKeyed(2) && Math.Abs(Cents(plo.FmHz(0), PulseHz(2047))) < 1.8 && Math.Abs(Cents(plo.FmHz(2), TriHz(2047))) < 1.8 && plo.NotesAboveFmRange == 0 && plo.LowNotesDropped == 0);
        // the pitch guard still applies (an opt-in ear guard shared by every bridge)
        bool was = PitchGuard.Enabled; float ceil = PitchGuard.CeilingHz;
        try
        {
            PitchGuard.CeilingHz = 1000; PitchGuard.Enabled = true;
            var g = Rig(0); var pg = (APU_MD)g.Apu; g.Pulse(0, TimerFor(2000), 15);
            var y = Capture(g, 0.2, 0.3);
            Check("the shared high-pitch guard mutes a note above its ceiling before it is mixed", SegaMeasure.Rms(y, 0, y.Length) < 1e-4 && !pg.FmKeyed(0));
        }
        finally { PitchGuard.Enabled = was; PitchGuard.CeilingHz = ceil; }
    }

    private static void Behaviour()
    {
        // channel masks, reset, polyphony, state
        {
            var r = Rig(0);
            r.Pulse(0, TimerFor(261.63), 15); r.Pulse(1, TimerFor(392.0), 15); r.Triangle(TriTimerFor(130.81)); r.Noise(8, 9, false);
            var all = SegaMeasure.Rms(Capture(r, 0.8, 0.8), 0, 35280); var maskTests = new List<string>(); bool ok = true;
            foreach (var (mask, name) in new[] { (0x1E, "pulse 1"), (0x1D, "pulse 2"), (0x1B, "triangle"), (0x17, "noise") })
            {
                r.Apu.SetEnabledChannels(mask); var x = Capture(r, 0.3, 0.4); double rr = SegaMeasure.Rms(x, 0, x.Length);
                maskTests.Add($"{name} off {rr / all:P0}"); ok &= rr < all * 0.97 && rr > 0;
            }
            r.Apu.SetEnabledChannels(0x00); var none = Capture(r, 0.3, 0.3);
            Check($"SetEnabledChannels removes each voice from the mix ({string.Join(", ", maskTests)}) and 0 silences it ({SegaMeasure.Rms(none, 0, none.Length):E1})", ok && SegaMeasure.Rms(none, 0, none.Length) < 1e-4);
        }
        {
            var r = Rig(0); var p = (APU_MD)r.Apu;
            r.Pulse(0, 253, 15); r.Triangle(126); r.Noise(8, 9, false); r.Run(0.1, false);
            r.Apu.Reset();
            r.Run(0.05, false);
            Check("Reset puts both chips back to silence (no FM voice keyed, PSG volumes off)", !p.FmKeyed(0) && !p.FmKeyed(1) && !p.FmKeyed(2) && Enumerable.Range(0, 4).All(c => p.Psg.Volume(c) == 15));
        }
        {
            // a loaded state: the model is rebuilt from the front core, so the chips play what the state was playing
            var a = Rig(2); var pa = (APU_MD)a.Apu;
            a.Pulse(0, 253, 12); a.Pulse(1, 190, 7, 1); a.Triangle(126); a.Noise(9, 9, false); a.Run(0.2, false);
            object state = a.Apu.GetState();
            var b = Rig(2); var pb = (APU_MD)b.Apu; b.Apu.SetState(state); b.Run(0.05, false);
            Check($"a state loaded into a fresh APU_MD plays what the state was playing: FM {pb.FmHz(0):F1}/{pb.FmHz(1):F1}/{pb.FmHz(2):F1} Hz (the original {pa.FmHz(0):F1}/{pa.FmHz(1):F1}/{pa.FmHz(2):F1}), noise ${pb.Psg.NoiseControl:X}", pb.FmKeyed(0) && pb.FmKeyed(1) && pb.FmKeyed(2) && Math.Abs(pb.FmHz(0) - pa.FmHz(0)) < 0.01 && Math.Abs(pb.FmHz(1) - pa.FmHz(1)) < 0.01 && Math.Abs(pb.FmHz(2) - pa.FmHz(2)) < 0.01 && pb.Psg.NoiseControl == pa.Psg.NoiseControl);
        }
        {
            // the application path of the desktop menu: swap the sound chip of a running NES while a note is held
            var nes = new NES { RomName = "idle.nes" }; nes.LoadROM(SnesToNes.IdleRom());
            bool a = nes.SetApuCore("FIX");
            nes.ActiveApu!.WriteAPURegister(0x4017, 0x40); nes.ActiveApu.WriteAPURegister(0x4015, 0x0F); nes.ActiveApu.WriteAPURegister(0x4001, 0x08);
            nes.ActiveApu.WriteAPURegister(0x4000, 0xBF); nes.ActiveApu.WriteAPURegister(0x4002, 253); nes.ActiveApu.WriteAPURegister(0x4003, 0xF8);
            bool b = nes.SetApuCore("MD");
            Check("NES.SetApuCore swaps APU_FIX for APU_MD while a note is held (the application path of the desktop menu)", a && b && nes.GetApuCoreId() == "APU_MD", nes.GetApuCoreId());
        }
        {
            var p = (APU_MD)Rig().Apu;
            Check("it says what it is: a Genesis core with the patch named in Describe(), and a Description that says what is dropped", p.CoreName.StartsWith("MD:YM2612+SN76489+") && p.Description.Contains("Genesis") && p.Description.Contains("DMC is dropped") && p.Describe().Contains("'Brass'") && p.GetSampleRate() == 44100);
        }
    }

    private static void Timing()
    {
        // the chips run on the NES clock in whole-number ratios: the YM2612 at 30/7 and the PSG at 2 per NES CPU cycle
        {
            var r = Rig(0); var p = (APU_MD)r.Apu;
            long cycles = 1_789_773, done = 0;
            while (done < cycles) { int c = (int)Math.Min(32, cycles - done); p.Step(c); done += c; }
            long wantSamples = cycles * 30 / 7 / 144;
            Check($"one second of NES time (1,789,773 cycles) gives the FM chip exactly {wantSamples:N0} samples (30/7 chip clocks per NES cycle, 144 per sample): got {p.Fm.Samples:N0}", p.Fm.Samples == wantSamples);
            Check($"the FM chip's clock is the NES clock x 30 / 7 = {APU_MD.YmClockHz:N1} Hz, the Genesis master clock / 7 (53.693175 MHz / 7 = {53_693_175.0 / 7:N1} Hz): {Math.Abs(APU_MD.YmClockHz / (53_693_175.0 / 7) - 1) * 1e6:F2} ppm apart (the NES clock constant is 1,789,773 Hz, not 1,789,772.7)", Math.Abs(APU_MD.YmClockHz / (53_693_175.0 / 7) - 1) < 3e-6);
        }
        {
            // the audio rate is the host's 44.1 kHz and keeps the NES clock: 2 s of NES time gives 88,200 samples
            var r = Rig(0); r.Pulse(0, 253, 15);
            r.Run(2.0, true); var x = r.Take();
            Check($"2.0 s of NES time gives {x.Length} samples (expect 88200 +-1%)", Math.Abs(x.Length - 88200) < 882);
        }
        {
            // the two board models: the discrete chip (Model 1, the default) and the integrated one play the same pitch and nearly the same level
            var saved = APU_MD.DefaultModel;
            try
            {
                var res = new List<(double Hz, double Rms)>();
                foreach (var model in new GenesisModel[] { GenesisModel.Model1, GenesisModel.Model2 })
                {
                    APU_MD.DefaultModel = model;
                    var r = Rig(2); var p = (APU_MD)r.Apu; r.Pulse(0, TimerFor(261.63), 15);
                    var x = Capture(r, 0.6, 0.8);
                    res.Add((SegaMeasure.Peak(x, r.Rate, PulseHz(TimerFor(261.63)), 2000, 2000 + 32768, 0.03).Hz, RmsOf(x, r.Rate, 0.1, 0.8)));
                    if (p.Fm.Ladder != (model == GenesisModel.Model1)) { Check("the chip model follows APU_MD.DefaultModel", false); return; }
                }
                Check($"Model 1 (the ladder effect on, the default) and Model 2 play the same pitch ({Cents(res[0].Hz, res[1].Hz):+0.000;-0.000} cents apart) and nearly the same level ({Db(res[0].Rms, res[1].Rms):+0.00;-0.00} dB apart on the additive square; limit 1.0: the ladder compensation assumes sine-like waveforms)", Math.Abs(Cents(res[0].Hz, res[1].Hz)) < 0.2 && Math.Abs(Db(res[0].Rms, res[1].Rms)) < 1.0);
            }
            finally { APU_MD.DefaultModel = saved; }
        }
        {
            // no allocation while playing: notes, volumes and patches change under the running chips and the audio is drained
            var r = Rig(0);
            r.Pulse(0, 253, 15); r.Pulse(1, 190, 12); r.Triangle(126); r.Noise(10, 7, false);
            r.Run(0.3, false);
            var md = (APU_MD)r.Apu; var buf = new float[512];
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++)
            {
                r.Apu.Step(32); r.Apu.ReadSamples(buf);
                if (i % 500 == 0) { r.Pulse(0, 200 + i % 100, 1 + i % 15); r.Noise(1 + i % 15, i % 16, i % 2 == 0); md.Patch = (i / 500) % 16; }
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check($"stepping 640,000 NES cycles with notes, volumes and patches changing and the audio drained allocates {bytes} bytes (limit 0)", bytes == 0);
        }
    }

    // ============================================================================================ a NES game on APU_MD

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

    private static (string Hash, float[] Audio, APU_MD? Md) RunNes(byte[] rom, string apu, int frames, int patch = 0)
    {
        int saved = APU_MD.DefaultPatch;
        APU_MD.DefaultPatch = patch;
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
            return (Fnv(nes.GetFrameBuffer()), audio.ToArray(), nes.ActiveApu as APU_MD);
        }
        finally { APU_MD.DefaultPatch = saved; }
    }

    /// <summary>Pitch-class energy of a stretch: the Hann-windowed magnitude at every semitone from A1 (55 Hz) to A7 summed into the 12 classes. Timbre moves energy between harmonics but a note keeps its class.</summary>
    private static double[] Chroma(float[] x, int from, int length)
    {
        var c = new double[12];
        for (int n = 0; n < 72; n++)
        {
            double hz = 55.0 * Math.Pow(2, n / 12.0);
            c[(n + 9) % 12] += SegaMeasure.Component(x, 44100, hz, from, from + length);     // A is class 9 of C-based classes
        }
        return c;
    }

    /// <summary>Mean Pearson correlation of the 12-class chroma of 100 ms windows of two renditions, over the windows where both sound (and how many that is). Matching notes score high whatever the timbre; different notes score near zero.</summary>
    private static (double Mean, int Windows) ChromaSimilarity(float[] a, float[] b, int shiftB = 0)
    {
        const int win = 4410;
        double sum = 0; int n = 0;
        for (int s0 = 0; s0 + win + shiftB <= Math.Min(a.Length, b.Length); s0 += win)
        {
            if (SegaMeasure.Rms(a, s0, s0 + win) < 0.002 || SegaMeasure.Rms(b, s0 + shiftB, s0 + shiftB + win) < 0.002) continue;
            var ca = Chroma(a, s0, win); var cb = Chroma(b, s0 + shiftB, win);
            double ma = ca.Average(), mb = cb.Average(), cov = 0, va = 0, vb = 0;
            for (int i = 0; i < 12; i++) { cov += (ca[i] - ma) * (cb[i] - mb); va += (ca[i] - ma) * (ca[i] - ma); vb += (cb[i] - mb) * (cb[i] - mb); }
            if (va <= 0 || vb <= 0) continue;
            sum += cov / Math.Sqrt(va * vb); n++;
        }
        return (n == 0 ? 0 : sum / n, n);
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

    private static void NesGame(string? romArg)
    {
        string path = FindRom(romArg ?? "");
        if (path.Length == 0) { Check("a NES ROM to run (Plugin/BrokenNes2.Plugin/Resources/default.nes, or --rom)", false, "not found"); return; }
        var rom = File.ReadAllBytes(path);
        const int frames = 900;
        var fix = RunNes(rom, "FIX", frames);
        var md = RunNes(rom, "MD", frames);                         // the default patch (brass)
        var psg = RunNes(rom, "MD", frames, FmPatchBank.PsgIndex);  // the PSG square: the policy for low notes applies (default OctaveUp)
        Check($"the game's picture is bit-identical on APU_MD and APU_FIX after {frames} frames (the sound chips do not change the game's timing): {md.Hash}", fix.Hash == md.Hash && fix.Hash == psg.Hash, $"FIX {fix.Hash}, MD {md.Hash}");
        double rf = SegaMeasure.Rms(fix.Audio, 0, fix.Audio.Length), rm = SegaMeasure.Rms(md.Audio, 0, md.Audio.Length);
        Check($"the game's music is audible on the Genesis chips: rms {rm:F4} (APU_FIX {rf:F4}, {Db(rm, rf):+0.0;-0.0} dB), {md.Audio.Length} samples (APU_FIX {fix.Audio.Length})", rm > 0.005 && Math.Abs(md.Audio.Length - fix.Audio.Length) < 600);
        var env = EnvelopeCorrelation(md.Audio, fix.Audio); var envPsg = EnvelopeCorrelation(psg.Audio, fix.Audio);
        Check($"it is the same music: the loudness envelope of the FM version correlates {env.Corr:F3} with APU_FIX's over {env.Windows} windows of 20 ms (limit 0.8); {env.SilentWindows} windows are silent where FIX sounds", env.Corr > 0.8);
        Check($"... the PSG-square version too ({envPsg.Corr:F3}; {envPsg.SilentWindows} silent windows: the low notes it cannot play, moved an octave up under the default policy)", envPsg.Corr > 0.8);
        Console.WriteLine("        brass: " + md.Md!.Describe());
        Console.WriteLine("        PSG square: " + psg.Md!.Describe());
        Check($"the counters say what happened: FM notes were played ({md.Md.FmNotes}), nothing above the FM range, nothing dropped; the PSG square keeps the bass on the FM triangle, so no low note had to move ({psg.Md.LowNotesTransposed} transposed, {psg.Md.LowNotesDropped} dropped)", md.Md.FmNotes > 20 && md.Md.NotesAboveFmRange == 0 && psg.Md.LowNotesDropped == 0);
        // the notes survive: the 12-class chroma of 100 ms windows (timbre moves energy between harmonics, a note keeps its pitch class) against the NES chip's own, and against the same game 2.5 s later as the "different notes" control
        var same = ChromaSimilarity(md.Audio, fix.Audio); var other = ChromaSimilarity(md.Audio, fix.Audio, 110250); var samePsg = ChromaSimilarity(psg.Audio, fix.Audio);
        // The ceiling of this measure is the exact-pitch reference: the PSG square (period = NES timer + 1, 0.00 cents) differs from APU_FIX's pulse in duty and filter too, so it does not reach 1. The FM version must be as near the NES chip as that reference (90%), and far from the control.
        Check($"the same notes: the pitch-class chroma of the FM version correlates {same.Mean:F2} with APU_FIX's over {same.Windows} windows (the exact-pitch PSG square, the measure's ceiling here: {samePsg.Mean:F2}), against {other.Mean:F2} for the same game 2.5 s apart (different notes)", same.Windows > 100 && same.Mean > 0.4 && same.Mean >= 0.9 * samePsg.Mean && same.Mean > other.Mean + 0.3 && samePsg.Mean > other.Mean + 0.3);
    }

    // ============================================================================================ MdSoundOnNes

    private static SegaMachine Machine(SegaRegion region = SegaRegion.Ntsc, GenesisModel model = GenesisModel.Model1) => new(SegaTimeline.Genesis(region), model, 44100);

    /// <summary>A sound unit driven the way a driver would: YM2612 and PSG writes at master-clock times, run a scanline at a time, the audio drained into a list (left channel).</summary>
    private sealed class UnitRig
    {
        public readonly IGenesisSound Unit;
        public readonly MdSoundUnit Real;            // the unit itself, or the one inside the bridge
        public long Master;
        private readonly List<float> samples = new();
        private readonly short[] buf = new short[16384];
        public double MasterHz;
        public UnitRig(string id, SegaRegion region = SegaRegion.Ntsc, GenesisModel model = GenesisModel.Model1)
        {
            var m = Machine(region, model);
            MasterHz = m.Timeline.MasterHz;
            Unit = GenesisSoundFactories.Create(id, m);
            Unit.Reset();
            Real = Unit as MdSoundUnit ?? ((MdSoundOnNes)Unit).Inner;
        }
        public int Rate => Unit.SampleRate;
        public void Ym(int reg, int value)
        {
            int part = reg >> 8 & 1;
            Real.WriteYm(part * 2, (byte)reg, Master); Master += 400;
            Real.WriteYm(part * 2 + 1, (byte)value, Master); Master += 1400;     // longer than the busy flag (192 chip clocks = 1344 master clocks)
        }
        public void Psg(int value) { Unit.WritePsg((byte)value, Master); Master += 64; }
        public void Run(double seconds, bool keep = true)
        {
            long end = Master + (long)(MasterHz * seconds);
            int lines = 0;
            while (Master < end)
            {
                Master = Math.Min(end, Master + 3420);
                Unit.RunTo(Master);
                if (++lines % 8 == 0) Drain(keep);
            }
            Drain(keep);
        }
        public float[] Take() { var a = samples.ToArray(); samples.Clear(); return a; }
        public void Pump() => Drain(true);
        private void Drain(bool keep)
        {
            int n;
            while ((n = Unit.ReadSamples(buf)) > 0) if (keep) for (int i = 0; i < n; i += 2) samples.Add(buf[i] / 32768f);
        }
        public float[] Capture(double settle, double seconds) { Run(settle, false); Run(seconds, true); return Take(); }

        // ---- FM helpers: a channel with the operators of a patch (or just a sine), a pitch by block/fnum, key on/off ----
        public void LoadPatch(int ch, FmPatch p, int extraTl = 0)
        {
            int c = ch % 3 + 0x100 * (ch / 3);
            for (int n = 0; n < 4; n++)
            {
                var o = p.Operators[n]; int off = c + FmPatchBank.SlotOffset(n);
                bool carrier = (p.CarrierMask >> n & 1) != 0;
                Ym(0x30 + off, o.Dt << 4 | o.Mul); Ym(0x40 + off, Math.Min(127, o.Tl + (carrier && o.Tl < FmPatch.OffTl ? extraTl : 0))); Ym(0x50 + off, o.Rs << 6 | o.Ar);
                Ym(0x60 + off, o.D1r); Ym(0x70 + off, o.D2r); Ym(0x80 + off, o.Sl << 4 | o.Rr); Ym(0x90 + off, 0);
            }
            Ym(0xB0 + c, p.Feedback << 3 | p.Algorithm);
            Ym(0xB4 + c, 0xC0);
        }
        public double FmPitch(int ch, double hz)
        {
            if (!FmPatchBank.TryPitch(hz, Real.Fm.InputClockHz, out int block, out int fnum, out _)) throw new InvalidOperationException($"{hz} Hz is out of the FM chip's range");
            int c = ch % 3 + 0x100 * (ch / 3);
            Ym(0xA4 + c, block << 3 | fnum >> 8); Ym(0xA0 + c, fnum & 0xFF);
            return FmPatchBank.Hz(block, fnum, Real.Fm.InputClockHz);
        }
        public void Key(int ch, bool on) => Ym(0x28, (on ? 0xF0 : 0x00) | (ch < 3 ? ch : ch + 1));
        public void Pan(int ch, int bits) => Ym(0xB4 + ch % 3 + 0x100 * (ch / 3), bits << 6);
        public void PsgTone(int ch, int period, int volume) { Psg(0x80 | ch << 5 | (period & 15)); Psg(period >> 4 & 0x3F); Psg(0x90 | ch << 5 | volume); }
    }

    private static readonly FmPatch SinePatch = FmPatchBank.Patches[FmPatchBank.SineIndex];

    private static void UnitFactory()
    {
        var m = Machine();
        var plain = GenesisSoundFactories.Create("NES", m);
        Check("the factory builds the unit under id NES (FIX behind it) and NES:FIXS names the chip", plain is MdSoundOnNes { BackApuId: "FIX" } && GenesisSoundFactories.Create("NES:FIXS", m) is MdSoundOnNes { BackApuId: "FIXS" } && GenesisSoundFactories.Create("nes:qn", m) is MdSoundOnNes { BackApuId: "qn" });
        bool threw1 = false, threw2 = false;
        try { GenesisSoundFactories.Create("NES:NOPE", m); } catch (ArgumentException) { threw1 = true; }
        try { GenesisSoundFactories.Create("NOPE:FIX", m); } catch (ArgumentException) { threw2 = true; }
        Check("an unknown NES chip, or an unknown unit with a colon, is an error (never a quiet substitute)", threw1 && threw2);
        Check("the stock unit is still what MD builds, and the NES ids are listed", GenesisSoundFactories.Create("MD", m) is MdSoundUnit && GenesisSoundFactories.Ids.Contains("NES") && GenesisSoundFactories.Create("MD-STUB", m) is GenesisSoundStub);
        var u = (MdSoundOnNes)plain;
        Check($"it says what it is ({u.CoreName}), reports the NES chip's rate ({u.SampleRate} Hz), and the real unit is inside (Z80, YM2612, PSG)", u.CoreName.StartsWith("NES:") && u.CoreName.Contains("MD (Z80, PSG") && u.SampleRate == 44100 && u.Inner.Cpu != null);
        Check("the writer has one bank on NES:FIX (3 tonal slots, 1 noise) and two on NES:FIXS (6 tonal, 2 noise)", u.Writer.Banks == 1 && ((MdSoundOnNes)GenesisSoundFactories.Create("NES:FIXS", m)).Writer.Banks == 2);
    }

    private static void UnitEveryChip()
    {
        // every NES chip the Genesis APU menu offers builds a unit that plays an FM note and a PSG tone (a chip that cannot would be a menu entry that lies)
        var ids = CoreCatalog.Options(ConsoleKind.Genesis, CoreSlot.Apu).Skip(1).Select(o => o.Id).ToList();
        var silent = new List<string>(); var failed = new List<string>(); int ok = 0;
        foreach (string id in ids)
        {
            try
            {
                var r = new UnitRig(id);
                r.LoadPatch(0, SinePatch, 6); r.FmPitch(0, 261.63); r.Key(0, true); r.PsgTone(0, 508, 1);
                var x = r.Capture(0.3, 0.5);
                if (SegaMeasure.Rms(x, 0, x.Length) > 0.003) ok++; else silent.Add(id);
            }
            catch (Exception e) { failed.Add($"{id}: {e.GetType().Name} {e.Message}"); }
        }
        Check($"every NES chip in the Genesis APU menu ({ids.Count}) builds a unit that sounds: {ok} do; silent: [{string.Join(", ", silent)}]; failed: [{string.Join("; ", failed)}]", ids.Count >= 15 && silent.Count == 0 && failed.Count == 0);
    }

    private static void UnitPitch()
    {
        // an FM sine on each of the six channels at A4, and then six more notes on one channel; the NES unit's pitch against the stock unit's, both measured the same way
        double worstDiff = 0; var bad = new List<string>(); var rows = new List<string>();
        foreach (var (ch, hz) in new[] { (0, 440.0), (1, 440.0), (2, 440.0), (3, 440.0), (4, 440.0), (5, 440.0), (0, 110.0), (0, 220.0), (0, 880.0), (0, 1760.0), (0, 3520.0), (1, 61.74), (2, 82.4) })
        {
            if (hz < 54.6) continue;
            double chipHz = 0;
            float[] Play(string id)
            {
                var r = new UnitRig(id); r.LoadPatch(ch, SinePatch, 6); chipHz = r.FmPitch(ch, hz); r.Key(ch, true);
                return r.Capture(0.6, hz < 100 ? 1.6 : 1.0);
            }
            var stock = Play("MD"); var nes = Play("NES");
            int len = hz < 100 ? 65536 : 32768;
            double hs = SegaMeasure.Peak(stock, 44100, chipHz, 4000, 4000 + len, 0.03).Hz, hn = SegaMeasure.Peak(nes, 44100, chipHz, 4000, 4000 + len, 0.03).Hz;
            int t = TimerFor(chipHz); double analytic = Cents(PulseHz(t), chipHz), diff = Cents(hn, hs);
            double bound = 1731.0 * 0.5 / (t + 1) + 0.3;      // half a NES timer step, in cents, and the measurement's own noise
            worstDiff = Math.Max(worstDiff, Math.Abs(diff));
            rows.Add($"FM{ch + 1} {chipHz:F1} Hz {diff:+0.0;-0.0} c");
            if (Math.Abs(diff) > bound || Math.Abs(diff - analytic) > 0.5) bad.Add($"FM{ch + 1} {chipHz:F1} Hz: {diff:+0.00;-0.00} cents (NES timer {t} rounds by {analytic:+0.00;-0.00}, bound {bound:F2})");
        }
        Console.WriteLine($"        FM pitch through the NES chip against the stock unit: {string.Join("; ", rows)}");
        Check($"FM notes on all six channels, 110 Hz to 3.5 kHz, come out at the stock unit's pitch to within the NES timer's own step (worst {worstDiff:F1} cents; each note's error is the rounding of its NES timer value, which the check recomputes)", bad.Count == 0, string.Join("; ", bad));
    }

    private static void UnitLevel()
    {
        // level against the stock unit: the NES chip plays every voice 4.0 dB quieter (the NES pulse's loudest step against a full-scale FM carrier), so the figure checked is the deviation from that.
        // The -4.0 is written here, not read from MdSoundOnNes.LevelScale: a test that took its expectation from the code would pass whatever the code said (the check below ties the two).
        double scaleDb = ScaleDb;
        Check($"the bridge's LevelScale is the documented -4.0 dB (the NES pulse's loudest step is 4 dB under a full-scale FM carrier): {Db(MdSoundOnNes.LevelScale, 1):F2} dB", Math.Abs(Db(MdSoundOnNes.LevelScale, 1) - ScaleDb) < 0.1);
        Console.WriteLine($"        every voice is replayed {scaleDb:F2} dB quieter than the stock unit (the NES pulse's loudest step against a full-scale FM carrier)");
        double worst = 0; var rows = new List<string>(); var bad = new List<string>();
        foreach (int tl in new[] { 4, 8, 12, 16, 20, 24, 28 })
        {
            double rs, rn;
            { var r = new UnitRig("MD"); r.LoadPatch(0, SinePatch, tl); r.FmPitch(0, 261.63); r.Key(0, true); rs = RmsOf(r.Capture(0.5, 0.9), 44100, 0.3, 0.9); }
            { var r = new UnitRig("NES"); r.LoadPatch(0, SinePatch, tl); r.FmPitch(0, 261.63); r.Key(0, true); rn = RmsOf(r.Capture(0.5, 0.9), 44100, 0.3, 0.9); }
            double d = Db(rn, rs) - scaleDb, bound = StepBound(rs * Scale); worst = Math.Max(worst, Math.Abs(d) - bound + 0.8);
            rows.Add($"TL {tl}: {Db(rn, rs):+0.0;-0.0}");
            if (Math.Abs(d) > bound) bad.Add($"TL {tl} {d:+0.0;-0.0} (bound {bound:F1})");
        }
        Console.WriteLine($"        FM sine level against the stock unit (dB, expected {scaleDb:F1} up to the NES's own step): {string.Join(", ", rows)}");
        Check($"an FM voice's loudness follows its total level: over total levels 4 to 28 the NES pulse is as near {scaleDb:F1} dB under the stock unit as its 15 volume steps allow (the check allows the distance to the nearest step plus 0.8 dB; steps are 1.3 to 6 dB apart)", bad.Count == 0, string.Join(", ", bad));
        // patches with modulators and several carriers: the level estimate treats carriers as independent sines, a modulator at a ratio of 1 or 2 moves the real figure: report it, hold it to 3.5 dB
        var rows2 = new List<string>(); double worst2 = 0; var bad2 = new List<string>();
        foreach (int p in new[] { 2, 3, 4, 6, 9, 10, 12 })
        {
            double rs, rn;
            { var r = new UnitRig("MD"); r.LoadPatch(0, FmPatchBank.Patches[p], 14); r.FmPitch(0, 261.63); r.Key(0, true); rs = RmsOf(r.Capture(0.5, 3.0), 44100, 1.0, 3.0); }
            { var r = new UnitRig("NES"); r.LoadPatch(0, FmPatchBank.Patches[p], 14); r.FmPitch(0, 261.63); r.Key(0, true); rn = RmsOf(r.Capture(0.5, 3.0), 44100, 1.0, 3.0); }
            double d = Db(rn, rs) - scaleDb; worst2 = Math.Max(worst2, Math.Abs(d));
            rows2.Add($"{FmPatchBank.Patches[p].Name} {d:+0.0;-0.0}");
            if (Math.Abs(d) > 3.5) bad2.Add($"{FmPatchBank.Patches[p].Name} {d:+0.0;-0.0}");
        }
        Console.WriteLine($"        deviation from {scaleDb:F1} dB for real patches: {string.Join(", ", rows2)}");
        Check($"patches with several carriers or a modulator are within 3.5 dB of the same offset (worst {worst2:F1} dB: the bridge reads the carriers' levels, not the waveform)", bad2.Count == 0, string.Join(", ", bad2));
    }

    private static void UnitPsg()
    {
        // PSG tones 1 to 3 against the stock unit: pitch (to the NES timer's step) and level
        var bad = new List<string>(); var rows = new List<string>(); double worstP = 0;
        foreach (var (ch, period) in new[] { (0, 1023), (0, 508), (0, 254), (0, 127), (0, 64), (1, 254), (1, 100), (2, 508), (2, 200) })
        {
            float[] Play(string id) { var r = new UnitRig(id); r.PsgTone(ch, period, 2); return r.Capture(0.4, 1.0); }
            var stock = Play("MD"); var nes = Play("NES");
            double hz = 53_693_175.0 / 15 / (32.0 * period);
            double hs = SegaMeasure.Peak(stock, 44100, hz, 4000, 4000 + 32768, 0.03).Hz, hn = SegaMeasure.Peak(nes, 44100, hz, 4000, 4000 + 32768, 0.03).Hz;
            int t = TimerFor(hz); double analytic = Cents(PulseHz(t), hz), diff = Cents(hn, hs);
            double bound = 1731.0 * 0.5 / (t + 1) + 0.3;
            worstP = Math.Max(worstP, Math.Abs(diff));
            double lvl = Db(RmsOf(nes, 44100, 0.2, 1.0), RmsOf(stock, 44100, 0.2, 1.0));
            rows.Add($"tone {ch + 1} N={period} {diff:+0.0;-0.0} c {lvl:+0.0;-0.0} dB");
            if (Math.Abs(diff - analytic) > 0.6 || Math.Abs(diff) > bound) bad.Add($"tone {ch + 1} N={period}: {diff:+0.00;-0.00} cents (timer rounding {analytic:+0.00;-0.00})");
            // the NES chip's own output filter (APU_FIX low-passes near 1 kHz) takes 1 to 6 dB off a square wave above about 500 Hz, on any NES note: the level is held where that filter does not reach, and shown above it
            double stockRms = RmsOf(stock, 44100, 0.2, 1.0);
            if (hz < 300 && Math.Abs(lvl - ScaleDb) > StepBound(stockRms * Scale)) bad.Add($"tone {ch + 1} N={period}: level {lvl:+0.0;-0.0} dB");
        }
        Console.WriteLine($"        PSG tones through the NES chip: {string.Join("; ", rows)}");
        Check($"PSG tones 1 to 3 (tone 3 on a pulse: it is a square wave) play at the stock unit's pitch to the NES timer's step (worst {worstP:F1} cents) and, below 300 Hz, at the stock level less 4 dB to the nearest NES step (above that APU_FIX's own output low-pass takes 1 to 6 dB off any NES note: shown in the line above)", bad.Count == 0, string.Join("; ", bad));
        // PSG volume steps
        {
            var seq = new List<double>(); var vbad = new List<string>();
            foreach (int k in new[] { 0, 2, 4, 6, 8, 10, 12 })
            {
                double rs, rn;
                { var r = new UnitRig("MD"); r.PsgTone(0, 508, k); rs = RmsOf(r.Capture(0.4, 0.8), 44100, 0.2, 0.8); }
                { var r = new UnitRig("NES"); r.PsgTone(0, 508, k); rn = RmsOf(r.Capture(0.4, 0.8), 44100, 0.2, 0.8); }
                double d = Db(rn, rs) - ScaleDb, bound = StepBound(rs * Scale); seq.Add(rn);
                if (Math.Abs(d) > bound) vbad.Add($"volume {k}: {d:+0.0;-0.0} dB (bound {bound:F1})");
            }
            bool mono = true; for (int i = 1; i < seq.Count; i++) mono &= seq[i] <= seq[i - 1] * 1.001;
            Check("PSG volume 0 to 12 (0 to -24 dB): the NES level is as near the stock unit's less 4 dB as the NES's 15 volume steps allow (nearest step plus 0.8 dB), and falls with it", vbad.Count == 0 && mono, string.Join("; ", vbad));
        }
        // noise: white and periodic, level
        foreach (var (reg, name, periodic) in new[] { (0xE4, "white", false), (0xE0, "periodic", true) })
        {
            var r = new UnitRig("NES"); r.Psg(reg); r.Psg(0xF0); r.Psg(0xF0 & 0xFF); r.Psg(0x90 | 3 << 5 | 2);
            var x = r.Capture(0.4, 0.6);
            var (lag, peak) = SegaMeasure.Periodicity(x, 20, 3000);
            Check($"PSG {name} noise (register ${reg:X2}) is {(periodic ? "periodic" : "white")} on the NES noise channel: rms {SegaMeasure.Rms(x, 0, x.Length):F3}, periodicity {peak:F2} at lag {lag}", SegaMeasure.Rms(x, 0, x.Length) > 0.02 && (periodic ? peak > 0.7 : peak < 0.4));
        }
        {
            var r = new UnitRig("NES"); r.Psg(0xE4); r.Psg(0x90 | 3 << 5 | 15);
            Check("noise at volume 15 (off) is silent", SegaMeasure.Rms(r.Capture(0.4, 0.4), 0, 17640) < 1e-4);
        }
        {
            // the Genesis PSG's DC trick (a period of 0 or 1) is not a note: silent, as the hub says
            var r = new UnitRig("NES"); r.PsgTone(0, 1, 0);
            Check("tone period 1 (the DC trick behind PCM playback through the PSG) is not a note: silent on the NES chip", SegaMeasure.Rms(r.Capture(0.4, 0.4), 0, 17640) < 1e-4);
        }
    }

    private static void UnitDac()
    {
        // the DAC played as a noise hit: a burst of noise samples at 8 kHz at several amplitudes; the stock unit's level against the NES chip's
        double rate = 8000; var rows = new List<string>(); var bad = new List<string>();
        foreach (int amp in new[] { 120, 60, 30, 15 })
        {
            double rs, rn; MdSoundOnNes? unit = null; float[] nesAudio;
            double RunDac(string id, out float[] audio)
            {
                var r = new UnitRig(id); r.Ym(0x2B, 0x80);
                var rng = new Random(2026);
                long end = r.Master + (long)(r.MasterHz * 0.8); long step = (long)(r.MasterHz / rate); long next = r.Master;
                while (r.Master < end)
                {
                    int v = 128 + rng.Next(-amp, amp + 1);
                    r.Real.WriteYm(0, 0x2A, r.Master); r.Real.WriteYm(1, (byte)v, r.Master + 20);
                    next += step;
                    while (r.Master < next) { r.Master = Math.Min(next, r.Master + 3420); r.Unit.RunTo(r.Master); }
                    r.Pump();
                    if (id == "NES") unit = (MdSoundOnNes)r.Unit;
                }
                r.Ym(0x2B, 0x00);
                audio = r.Take();
                return SegaMeasure.Rms(audio, 4410, Math.Min(audio.Length, 4410 + 25000));
            }
            rs = RunDac("MD", out _); rn = RunDac("NES", out nesAudio);
            double d = Db(rn, rs) - ScaleDb, bound = StepBound(rs * Scale, noise: true) + 1.5;
            rows.Add($"swing {amp * 2 / 256.0:F2}: {Db(rn, rs):+0.0;-0.0} dB ({d:+0.0;-0.0} off the offset, bound {bound:F1})");
            if (d > bound || d < -(bound + 3.5)) bad.Add($"amplitude {amp}: {d:+0.0;-0.0} dB off (bound +{bound:F1} / -{bound + 3.5:F1})");
            var (_, peak) = SegaMeasure.Periodicity(nesAudio.Skip(4410).Take(12000).ToArray(), 20, 2000);
            if (amp == 120) Check($"a DAC stream of noise samples is a noise hit on the NES chip (periodicity {peak:F2}), counted ({unit!.DacStreams} stream, {unit.DacSyncs} syncs as noise) and reported in Describe()", peak < 0.5 && unit.DacStreams >= 1 && unit.DacSyncs > 100 && unit.Describe().Contains("DAC samples"));
        }
        Console.WriteLine($"        DAC noise bursts, NES against stock: {string.Join("; ", rows)}");
        Check("the hit's loudness follows the DAC's swing: at four amplitudes within the nearest NES noise step plus 2.3 dB of the stock unit's level less 4 dB, and up to 3.5 dB lower still (APU_FIX's output low-pass takes about 3 dB out of noise at 3.5 kHz: see the noise levels of APU_MD); the DAC's samples themselves are not carried, its swing is, modelled as noise at 0.6 of its peak", bad.Count == 0, string.Join("; ", bad));
        {
            // DAC off: silence again
            var r = new UnitRig("NES"); r.Ym(0x2B, 0x80); r.Ym(0x2A, 0xFF); r.Ym(0x2B, 0x00); r.Ym(0x2A, 0x00);
            r.Run(0.1, false);
            var x = r.Capture(0.2, 0.3);
            Check("with the DAC switched off the noise hit ends", SegaMeasure.Rms(x, 0, x.Length) < 1e-4);
        }
    }

    private static void UnitPolyphony()
    {
        // six FM voices at six pitches, on NES:FIXS (six tonal slots): every voice is in the mix; on NES:FIX (three): the three loudest, the rest counted
        double[] hz = { 150, 211, 297, 389, 467, 563 };          // no pitch is an odd multiple of another within 4 Hz (a pulse has odd harmonics)
        var tls = new[] { 4, 5, 6, 7, 8, 9 };
        foreach (string id in new[] { "NES:FIXS", "NES:FIX" })
        {
            var r = new UnitRig(id); var u = (MdSoundOnNes)r.Unit;
            for (int c = 0; c < 6; c++) { r.LoadPatch(c, SinePatch, tls[c]); r.FmPitch(c, hz[c]); }
            for (int c = 0; c < 6; c++) r.Key(c, true);
            var x = r.Capture(0.8, 1.0);
            int slots = u.Writer.PulseSlots + u.Writer.TriangleSlots;
            var present = new List<string>(); int heard = 0;
            for (int c = 0; c < 6; c++)
            {
                double at = SegaMeasure.Component(x, 44100, hz[c], 4000, 4000 + 32768), off = SegaMeasure.Component(x, 44100, hz[c] * 1.0595, 4000, 4000 + 32768);
                bool yes = at > off * 3 && at > 0.004; if (yes) heard++;
                present.Add($"FM{c + 1}{(yes ? "+" : "-")}");
            }
            // the loudest voices keep a channel (FM1 is the loudest: TL 4)
            bool loudest = true; for (int c = 0; c < Math.Min(slots, 6); c++) { double at = SegaMeasure.Component(x, 44100, hz[c], 4000, 4000 + 32768), off = SegaMeasure.Component(x, 44100, hz[c] * 1.0595, 4000, 4000 + 32768); loudest &= at > off * 3; }
            Check($"{id}: six FM voices at six pitches, {slots} NES tonal channels: {heard} are heard ({string.Join(" ", present)}), the loudest ones first; voices without a channel counted ({u.VoicesWithoutChannel} voice-syncs)", heard == slots && loudest && (slots >= 6 ? u.VoicesWithoutChannel == 0 : u.VoicesWithoutChannel > 100));
        }
        {
            // NES:FIX has two pulses and a triangle. Two quiet voices sit on the pulses and a loud low note (below the pulse's range) on the triangle; a loud newcomer finds no free channel and takes one of the quiet voices' pulse
            var r = new UnitRig("NES:FIX"); var u = (MdSoundOnNes)r.Unit;
            r.LoadPatch(0, SinePatch, 30); r.FmPitch(0, 220); r.LoadPatch(1, SinePatch, 30); r.FmPitch(1, 277.18);
            r.LoadPatch(2, SinePatch, 2); r.FmPitch(2, 41.2);
            r.LoadPatch(3, SinePatch, 4); r.FmPitch(3, 349.23);
            r.Key(0, true); r.Key(1, true); r.Key(2, true);
            r.Run(0.5, false);
            long stolen0 = u.VoicesStolen;
            r.Key(3, true); r.Run(0.3, false);
            var x = r.Capture(0.2, 1.0);
            double f4 = 349.23, at = SegaMeasure.Component(x, 44100, f4, 4000, 4000 + 32768), off = SegaMeasure.Component(x, 44100, f4 * 1.0595, 4000, 4000 + 32768);
            Check($"a loud FM voice (TL 4) takes a NES channel from a quiet one (TL 30) when all three are in use: stolen {u.VoicesStolen - stolen0}, its pitch is in the mix ({Db(at, off):F0} dB over a semitone above)", u.VoicesStolen - stolen0 >= 1 && at > off * 4);
        }
        {
            // a voice that stops frees its channel for the next
            var r = new UnitRig("NES:FIX"); var u = (MdSoundOnNes)r.Unit;
            for (int c = 0; c < 4; c++) { r.LoadPatch(c, SinePatch, 8); r.FmPitch(c, 200 * Math.Pow(1.3, c)); }
            for (int c = 0; c < 3; c++) r.Key(c, true);
            r.Run(0.4, false);
            r.Key(0, false); r.Run(0.3, false); r.Key(3, true);
            var x = r.Capture(0.3, 1.0);
            double f4 = 200 * Math.Pow(1.3, 3), at = SegaMeasure.Component(x, 44100, f4, 4000, 4000 + 32768), off = SegaMeasure.Component(x, 44100, f4 * 1.0595, 4000, 4000 + 32768);
            Check($"when a voice stops its NES channel goes to the next voice that starts ({Db(at, off):F0} dB over a semitone above, no steal needed: {u.VoicesStolen} stolen)", at > off * 4 && u.VoicesStolen == 0);
        }
    }

    private static void UnitLowNotes()
    {
        // an FM note at 41.2 Hz (E1, below the NES pulse's 54.6 Hz): loud, so the triangle (exact down to 27 Hz) takes it; quiet, so a pulse plays it an octave up (policy) or not at all
        {
            var r = new UnitRig("NES:FIX"); var u = (MdSoundOnNes)r.Unit;
            r.LoadPatch(0, SinePatch, 2); double chip = r.FmPitch(0, 41.2); r.Key(0, true);
            var x = r.Capture(0.8, 1.6);
            var (f, _) = SegaMeasure.Peak(x, 44100, chip, 4000, 4000 + 65536, 0.05);
            Check($"a loud FM note at 41.2 Hz (below the NES pulse) plays on the NES triangle at its own pitch: {f:F2} Hz ({Cents(f, chip):+0.0;-0.0} cents from the chip's {chip:F2} Hz; the NES timer steps are {1731.0 / (TriTimerFor(chip) + 1):F1} cents), nothing transposed or dropped", Math.Abs(Cents(f, chip)) < 2.5 && u.LowNotesTransposed == 0 && u.LowNotesDropped == 0);
        }
        foreach (var policy in new[] { LowNotePolicy.OctaveUp, LowNotePolicy.Drop })
        {
            var r = new UnitRig("NES:FIX"); var u = (MdSoundOnNes)r.Unit; u.LowNotes = policy;
            r.LoadPatch(0, SinePatch, 40); double chip = r.FmPitch(0, 41.2); r.Key(0, true);
            var x = r.Capture(0.8, 1.6);
            if (policy == LowNotePolicy.OctaveUp)
            {
                var (f, a) = SegaMeasure.Peak(x, 44100, chip * 2, 4000, 4000 + 65536, 0.05);
                Check($"a quiet FM note at 41.2 Hz (the triangle is too loud for it) plays on a pulse an octave up: {f:F2} Hz ({Cents(f, chip * 2):+0.0;-0.0} cents from 2x), counted as transposed once ({u.LowNotesTransposed}), none dropped", Math.Abs(Cents(f, chip * 2)) < 4 && a > 0.001 && u.LowNotesTransposed == 1 && u.LowNotesDropped == 0);
            }
            else Check($"policy Drop: the same quiet note is silent ({SegaMeasure.Rms(x, 0, x.Length):E1}) and counted once ({u.LowNotesDropped}), not moved", SegaMeasure.Rms(x, 0, x.Length) < 2e-4 && u.LowNotesDropped == 1 && u.LowNotesTransposed == 0);
        }
        {
            var saved = APU_MD.DefaultLowNotes;
            Check("the policy of a new unit is APU_MD.DefaultLowNotes (a game keeps its bass line: OctaveUp)", saved == LowNotePolicy.OctaveUp && ((MdSoundOnNes)GenesisSoundFactories.Create("NES", Machine())).LowNotes == LowNotePolicy.OctaveUp);
        }
    }

    private static void UnitCounters()
    {
        // FM timbre, panning and the LFO are counted, never silent
        var r = new UnitRig("NES:FIXS"); var u = (MdSoundOnNes)r.Unit;
        r.LoadPatch(0, FmPatchBank.Patches[0], 6); r.FmPitch(0, 261.63);
        r.LoadPatch(1, SinePatch, 8); r.FmPitch(1, 330); r.Pan(1, 2);             // left only
        r.LoadPatch(2, SinePatch, 8); r.FmPitch(2, 392); r.Pan(2, 0);             // neither speaker: silent
        r.Key(0, true); r.Key(1, true); r.Key(2, true);
        r.Run(0.5, false);
        Check($"FM notes are counted as plain pulses ({u.FmNotes}: the one panned to no speaker is silent and not a note), one-sided pan is counted ({u.OneSidedNotes})", u.FmNotes == 2 && u.OneSidedNotes == 1, u.Describe());
        Check("... and Describe() says so", u.Describe().Contains("FM timbre") && u.Describe().Contains("one-sided pan (1 notes)"));
        long before = u.LfoSyncs;
        r.Ym(0x22, 0x0C); r.Ym(0xB4, 0xC0 | 0x04);       // LFO on, channel 1 with vibrato sensitivity
        r.Run(0.3, false);
        Check($"the LFO's vibrato is not carried and says so: {u.LfoSyncs - before} voice-syncs counted with the LFO on and a sensitive voice playing", u.LfoSyncs - before > 100);
        long keys = u.FmNotes; r.Key(0, false); r.Run(0.2, false); r.Key(0, true); r.Run(0.2, false);
        Check("each key-on is a note: striking the same channel again counts again", u.FmNotes == keys + 1);
    }

    /// <summary>A Z80 program (raw bytes) that makes a YM2612 / PSG write list with a delay after each write: LD A,n / LD (nn),A / LD B,n / DJNZ, then a spin.</summary>
    private static byte[] Z80Driver(IEnumerable<(int Address, int Value)> writes)
    {
        var code = new List<byte>();
        foreach (var (address, value) in writes)
        {
            code.AddRange(new byte[] { 0x3E, (byte)value, 0x32, (byte)address, (byte)(address >> 8), 0x06, 0x60, 0x10, 0xFE });
        }
        code.AddRange(new byte[] { 0x18, 0xFE });
        return code.ToArray();
    }

    private static void UnitZ80()
    {
        // the real sound unit inside the bridge runs a Z80 driver: the 68000 takes the bus, loads the driver into Z80 RAM, releases reset and the bus; the driver programs the YM2612 and the PSG
        var writes = new List<(int, int)>();
        void Ym(int reg, int val) { writes.Add((0x4000, reg)); writes.Add((0x4001, val)); }
        for (int n = 0; n < 4; n++) { int off = new[] { 0, 8, 4, 12 }[n]; Ym(0x30 + off, 0x01); Ym(0x40 + off, n == 3 ? 10 : 127); Ym(0x50 + off, 0x1F); Ym(0x60 + off, 0); Ym(0x70 + off, 0); Ym(0x80 + off, 0x0F); }
        Ym(0xB0, 0x07); Ym(0xB4, 0xC0);
        var tp = FmPatchBank.TryPitch(329.63, 53_693_175.0 / 7, out int block, out int fnum, out _);
        Ym(0xA4, block << 3 | fnum >> 8); Ym(0xA0, fnum & 0xFF); Ym(0x28, 0xF0);
        writes.Add((0x7F11, 0x80 | 0 << 5 | (254 & 15))); writes.Add((0x7F11, 254 >> 4)); writes.Add((0x7F11, 0x90 | 4));
        var driver = Z80Driver(writes);

        double[] Measure(string id, out string desc, out long ymWrites, out long bank)
        {
            var m = Machine(); var unit = GenesisSoundFactories.Create(id, m); unit.Reset();
            long t = 0;
            unit.WriteBusRequest(true, t); bool granted = unit.IsBusGranted(t += 100);
            for (int i = 0; i < driver.Length; i++) unit.Write(i, driver[i], t);
            unit.WriteReset(true, t += 100); unit.WriteBusRequest(false, t += 100);
            var rig = new List<float>(); var buf = new short[16384];
            long end = t + (long)(m.Timeline.MasterHz * 1.6);
            while (t < end) { t += 3420; unit.RunTo(t); int n; while ((n = unit.ReadSamples(buf)) > 0) for (int i = 0; i < n; i += 2) rig.Add(buf[i] / 32768f); }
            var x = rig.ToArray();
            var real = unit as MdSoundUnit ?? ((MdSoundOnNes)unit).Inner;
            desc = $"{(granted ? "bus granted" : "no grant")}, z80 pc {real.Cpu.PC:X4}, ym writes {real.YmWrites}, psg writes {real.PsgWrites}";
            ymWrites = real.YmWrites; bank = real.PsgWrites;
            double e4 = FmPatchBank.Hz(block, fnum, 53_693_175.0 / 7);
            var (fFm, aFm) = SegaMeasure.Peak(x, unit.SampleRate, e4, 20000, 20000 + 32768, 0.03);
            double psgHz = 53_693_175.0 / 15 / (32.0 * 254);
            var (fPsg, aPsg) = SegaMeasure.Peak(x, unit.SampleRate, psgHz, 20000, 20000 + 32768, 0.03);
            return new[] { fFm, aFm, fPsg, aPsg, e4, psgHz };
        }
        var stock = Measure("MD", out string d1, out long y1, out long p1); var nes = Measure("NES:FIX", out string d2, out long y2, out long p2);
        Check($"a real Z80 driver in Z80 RAM runs inside the bridge: the same writes reach the YM2612 and the PSG as on the stock unit ({y2} YM data writes, {p2} PSG writes; stock {y1} and {p1}); {d2}", y1 == y2 && p1 == p2 && y2 >= 29 && p2 == 3);
        double dFm = Cents(nes[0], stock[0]), dPsg = Cents(nes[2], stock[2]);
        Check($"the driver's FM note (E4, {nes[4]:F2} Hz) and PSG tone ({nes[5]:F2} Hz) both come out of the NES chip at the stock unit's pitch: FM {dFm:+0.0;-0.0} cents, PSG {dPsg:+0.0;-0.0} cents (the NES timer's step at these pitches is {1731.0 / (TimerFor(nes[4]) + 1):F1} and {1731.0 / (TimerFor(nes[5]) + 1):F1} cents)", Math.Abs(dFm) < 1731.0 / (TimerFor(nes[4]) + 1) / 2 + 0.5 && Math.Abs(dPsg) < 1731.0 / (TimerFor(nes[5]) + 1) / 2 + 0.5 && nes[1] > 0.003 && nes[3] > 0.003);
        // the handshake the 68000 sees is the real unit's
        {
            var a = GenesisSoundFactories.Create("MD", Machine()); var b = GenesisSoundFactories.Create("NES", Machine());
            a.Reset(); b.Reset();
            bool same = a.IsInReset == b.IsInReset;
            var sa = new List<bool>(); var sb = new List<bool>();      // what the 68000 would read from the bus-request port, at the moments it reads it
            sa.Add(a.IsBusGranted(0)); sb.Add(b.IsBusGranted(0));       // before any request
            a.WriteBusRequest(true, 0); b.WriteBusRequest(true, 0);
            sa.Add(a.IsBusGranted(100)); sb.Add(b.IsBusGranted(100));   // requested
            a.WriteReset(true, 100); b.WriteReset(true, 100); a.WriteBusRequest(false, 200); b.WriteBusRequest(false, 200);
            sa.Add(a.IsBusGranted(300)); sb.Add(b.IsBusGranted(300));   // released again: the Z80 has the bus back, a grant that says otherwise would let the 68000 write Z80 RAM under a running driver
            a.RunTo(2000); b.RunTo(2000);
            same &= a.IsInReset == b.IsInReset && a.Read(0x4000, 2000) == b.Read(0x4000, 2000) && sa.SequenceEqual(sb);
            Check($"the BUSREQ / RESET handshake and the Z80 RAM window answer exactly as the stock unit's (grant before a request / while requested / after release: stock {string.Join("/", sa)}, bridge {string.Join("/", sb)}; the stock answers both ways)", same && b.IsInReset == false && sa.Contains(true) && sa.Contains(false));
        }
    }

    private static void UnitState()
    {
        var r = new UnitRig("NES:FIX"); var u = (MdSoundOnNes)r.Unit;
        r.LoadPatch(0, SinePatch, 10); r.FmPitch(0, 261.63); r.Key(0, true); r.PsgTone(0, 300, 1); r.Run(0.3, false);
        var ms = new MemoryStream(); u.SaveState(new BinaryWriter(ms));
        string before = u.Inner.Describe();
        u.Reset();
        bool quiet = u.Inner.Psg.Volume(0) == 15;
        ms.Position = 0; u.LoadState(new BinaryReader(ms));
        r.Run(0.1, false);
        Check("state round-trips (the inner unit's registers come back), Reset silences the unit, and the NES chip plays what the loaded state was playing", quiet && u.Inner.Psg.Period(0) == 300 && u.Inner.Psg.Volume(0) == 1 && u.Writer.Writes > 0, u.Describe());
        // PAL: the master clock is slower, so every note is lower; the NES chip is told the real pitch
        {
            var pal = new UnitRig("NES", SegaRegion.Pal); pal.PsgTone(0, 254, 2);
            var x = pal.Capture(0.4, 1.0);
            double want = 53_203_424.0 / 15 / (32.0 * 254), got = SegaMeasure.Peak(x, 44100, want, 4000, 4000 + 32768, 0.03).Hz;
            int t = TimerFor(want);
            Check($"PAL: PSG tone period 254 plays {got:F2} Hz (expect {want:F2}, {Cents(got, want):+0.00;-0.00} cents; NTSC would be {53_693_175.0 / 15 / (32.0 * 254):F2})", Math.Abs(Cents(got, want)) < 1731.0 / (t + 1) / 2 + 0.5);
        }
        {
            var r2 = new UnitRig("NES"); var x = r2.Capture(0, 2.0);
            Check($"2.0 s of master clock gives {x.Length} samples (expect 88200 +-1%)", Math.Abs(x.Length - 88200) < 882);
        }
    }

    private static void UnitNoAllocation()
    {
        var r = new UnitRig("NES:FIXS");
        for (int c = 0; c < 4; c++) { r.LoadPatch(c, SinePatch, 10 + c); r.FmPitch(c, 200 + 60 * c); r.Key(c, true); }
        r.PsgTone(0, 254, 2); r.Psg(0xE5); r.Psg(0xF0 | 3);
        r.Run(0.4, false);
        var buf = new short[4096]; var u = r.Unit;
        long master = r.Master;
        // warm-up: the JIT's tiering allocates a little the first thousands of times a method runs (the plugin is Native AOT and has no such thing); steady state is what is measured
        for (int i = 0; i < 12000; i++) { master += 3420; u.RunTo(master); if (i % 100 == 0) { r.Real.WriteYm(0, 0xA0, master); r.Real.WriteYm(1, (byte)(i % 256), master + 10); u.ReadSamples(buf); } }
        var slices = new long[6];
        long before = GC.GetAllocatedBytesForCurrentThread(), last = before;
        for (int i = 0; i < 6000; i++)
        {
            master += 3420; u.RunTo(master);
            if (i % 100 == 0) { r.Real.WriteYm(0, 0xA0, master); r.Real.WriteYm(1, (byte)(i % 256), master + 10); u.ReadSamples(buf); }
            if (i % 1000 == 999) { long now = GC.GetAllocatedBytesForCurrentThread(); slices[i / 1000] = now - last; last = now; }
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Info("allocation per 1000 scanlines: " + string.Join(", ", slices));
        Check($"6000 scanlines with notes changing, voices allocated and the audio drained allocate {bytes} bytes (limit 0)", bytes == 0);
    }

    // ============================================================================================ a real Genesis game

    private static void GenesisGame(string? romPath)
    {
        if (romPath == null || !File.Exists(romPath)) { Console.WriteLine("  (not run: no Genesis ROM given; pass --md-rom <file.bin>)"); return; }
        var rom = File.ReadAllBytes(romPath);
        const int frames = 1560;
        string name = Path.GetFileNameWithoutExtension(romPath);
        (MdSession S, float[] Audio, long Ms) Run(string apu)
        {
            var s = new MdSession(rom, "M68K", "MD", apu);
            var audio = new List<float>(); var buf = new short[16384];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
            {
                s.SetPad(0, f is >= 480 and < 486 or >= 840 and < 846 or >= 1200 and < 1206 ? PadButtons.Start : PadButtons.None);
                s.RunFrame();
                int n; while ((n = s.ReadSamples(buf)) > 0) for (int i = 0; i < n; i += 2) audio.Add(buf[i] / 32768f);
            }
            return (s, audio.ToArray(), sw.ElapsedMilliseconds);
        }
        var stock = Run("MD"); var fixr = Run("NES:FIX"); var fixs = Run("NES:FIXS");
        ulong H(MdSession s) { ulong h = 14695981039346656037UL; foreach (byte b in s.Board.Ram) { h ^= b; h *= 1099511628211UL; } foreach (ushort v in s.Board.Vdp.Vram) { h ^= (byte)v; h *= 1099511628211UL; } return h; }
        Check($"{name}: the game runs identically on the stock unit and on the NES chips ({frames} frames, the 68000 sees the same sound unit: work RAM and VRAM hash equal; {stock.Ms} ms stock, {fixr.Ms} ms NES:FIX, {fixs.Ms} ms NES:FIXS)", H(stock.S) == H(fixr.S) && H(stock.S) == H(fixs.S), $"{H(stock.S):X16} {H(fixr.S):X16} {H(fixs.S):X16}");
        var real = (MdSoundUnit)stock.S.Board.Sound; var br = (MdSoundOnNes)fixs.S.Board.Sound;
        Check($"the game's Z80 driver ran: {real.Z80Ticks:N0} Z80 T-states, {real.YmWrites:N0} YM2612 writes, {real.PsgWrites:N0} PSG writes (the same {br.Inner.YmWrites:N0} / {br.Inner.PsgWrites:N0} inside the bridge)", real.YmWrites > 100 && br.Inner.YmWrites == real.YmWrites && br.Inner.PsgWrites == real.PsgWrites);
        double rs = SegaMeasure.Rms(stock.Audio, 0, stock.Audio.Length), rf = SegaMeasure.Rms(fixs.Audio, 0, fixs.Audio.Length);
        Console.WriteLine($"        {fixs.S.Board.Sound.Describe()}");
        Check($"the game's sound is audible on the NES chip: rms {rf:F4} against the stock unit's {rs:F4} ({Db(rf, rs):+0.0;-0.0} dB; the NES chip is {ScaleDb:F1} dB under by design)", rf > 0.001 && rs > 0.001);
        var env = EnvelopeCorrelation(fixs.Audio, stock.Audio);
        Check($"it is the same music: the loudness envelope on NES:FIXS correlates {env.Corr:F3} with the stock unit's over {env.Windows} windows of 20 ms (limit 0.6: the stock mix has voices the NES chip cannot carry)", env.Corr > 0.6);
        var simFixs = ChromaSimilarity(fixs.Audio, stock.Audio); var simFix = ChromaSimilarity(fixr.Audio, stock.Audio); var simOther = ChromaSimilarity(fixs.Audio, stock.Audio, 110250);
        Check($"the same notes: the pitch-class chroma of 100 ms windows on NES:FIXS correlates {simFixs.Mean:F2} with the stock unit's over {simFixs.Windows} windows (NES:FIX with three tonal channels {simFix.Mean:F2}), against {simOther.Mean:F2} for the same game 2.5 s apart (different notes)", simFixs.Windows > 100 && simFixs.Mean > 0.5 && simFixs.Mean > simOther.Mean + 0.25);
        Check($"nothing was lost silently: the counters say {br.FmNotes} FM notes as plain pulses, {br.DacSyncs} DAC syncs as noise, {br.VoicesWithoutChannel} voice-syncs without a channel, {br.VoicesStolen} stolen, {br.OneSidedNotes} one-sided, {br.LowNotesTransposed} transposed", br.FmNotes > 0 || br.Inner.YmWrites < 100);
    }
}
