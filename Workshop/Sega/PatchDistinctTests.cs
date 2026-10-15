using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator;
using NesEmulator.Mix;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// The FM patch banks as sounds, by analysis (group <c>patches</c> of <c>--sega-bridge-test</c>): the Genesis bank (<see cref="FmPatchBank"/>, 16 patches through <see cref="APU_MD"/>) and the Master System bank (<see cref="OpllPatchBank"/>, 22 instruments through
/// <see cref="APU_SMSFM"/>). Nobody has listened to either; what can be measured is whether the bank is a bank. For every patch a held C4 is played through the real bridge and reduced to a fingerprint: the first twelve harmonics (dB below the total power) and the loudness
/// envelope over the first second (ten 100 ms slices, dB below the loudest). Then: every patch makes a steady tone at the pitch it was asked for, no two patches are alike (the fingerprints of every pair differ by at least <see cref="MinDistanceDb"/> dB RMS, a pair
/// that includes the PSG square, which is the NES pulse's own sound by design, is exempt and printed), and the loudest and quietest patch are within 3 dB of each other (the calibration the bridges depend on). What this cannot say: which patch sounds good, whether two
/// patches with different fingerprints are easy to tell apart by ear, whether a "Brass" sounds like brass. That needs ears (the report prints the closest pairs so the listening can start there; <c>--md-patches --wav-dir</c> writes a phrase per Genesis patch).
/// </summary>
internal static class PatchDistinctTests
{
    private static int passes, failures;
    private static bool verbose;
    /// <summary>The least RMS difference (dB over 12 harmonics and 10 envelope slices) between two patches that count as different.</summary>
    internal const double MinDistanceDb = 2.0;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }
    private static void Guard(Action group, string name)
    {
        try { group(); }
        catch (Exception e) { Check($"{name} ran to the end", false, e.GetType().Name + ": " + e.Message); }
    }

    public static (int Passes, int Failures) Run(Func<string, bool> want, bool verboseOutput)
    {
        verbose = verboseOutput; passes = failures = 0;
        if (want("patches"))
        {
            Console.WriteLine("\n== the FM patch banks, by analysis (nobody has listened: this says whether the bank is a bank)");
            Guard(() => Bank("Genesis (YM2612)", "MD", FmPatchBank.Count, i => FmPatchBank.Patches[i].Name, i => FmPatchBank.Patches[i].Psg, (a, p) => ((APU_MD)a).Patch = p), "Genesis bank");
            Guard(() => Bank("Master System (YM2413)", "SMSFM", OpllPatchBank.Count, i => OpllPatchBank.Patches[i].Name, i => OpllPatchBank.Patches[i].Psg, (a, p) => ((APU_SMSFM)a).Patch = p), "Master System bank");
        }
        return (passes, failures);
    }

    private sealed record Print(double[] Harmonics, double[] Envelope, double Hz, double Rms);

    private static Print Fingerprint(string chip, int patch, Action<IAPU, int> set)
    {
        const double NesHz = NesVoiceWriter.NesCpuHz;
        double want = 261.63;
        int timer = (int)Math.Round(NesHz / (16.0 * want)) - 1; double nesHz = NesHz / (16.0 * (timer + 1));
        var rig = new MdPatchCli.Rig(chip); set(rig.Apu, patch);
        rig.Pulse(0, timer, 15);
        var slices = new List<double>(); var all = new List<float>();
        for (int k = 0; k < 16; k++) { rig.Run(0.1, true); var part = rig.Take(); all.AddRange(part); slices.Add(SegaMeasure.Rms(part, 0, part.Length)); }
        var x = all.ToArray(); int rate = rig.Rate;
        int from = (int)(rate * 0.9), to = Math.Min(x.Length, from + 16384);
        var (f0, _) = SegaMeasure.Peak(x, rate, nesHz, from, to, 0.04);
        var amps = new double[13]; double total = 0;
        for (int h = 1; h <= 12; h++) { amps[h] = nesHz * h < rate * 0.45 ? SegaMeasure.Component(x, rate, nesHz * h, from, to) : 0; total += amps[h] * amps[h]; }
        var hs = new double[12];
        for (int h = 1; h <= 12; h++) hs[h - 1] = Math.Max(-60, 10 * Math.Log10(Math.Max(1e-12, amps[h] * amps[h] / Math.Max(1e-12, total))));
        double top = slices.Take(10).Max();
        var env = slices.Take(10).Select(s => Math.Max(-40, 20 * Math.Log10(Math.Max(1e-9, s / Math.Max(1e-9, top))))).ToArray();
        return new Print(hs, env, f0, SegaMeasure.Rms(x, from, to));
    }

    private static double Distance(Print a, Print b)
    {
        double s = 0; int n = 0;
        for (int i = 0; i < 12; i++) { double d = a.Harmonics[i] - b.Harmonics[i]; s += d * d; n++; }
        for (int i = 0; i < 10; i++) { double d = a.Envelope[i] - b.Envelope[i]; s += d * d; n++; }
        return Math.Sqrt(s / n);
    }

    private static void Bank(string label, string chip, int count, Func<int, string> name, Func<int, bool> isPsg, Action<IAPU, int> set)
    {
        var prints = new Print[count];
        for (int p = 0; p < count; p++) prints[p] = Fingerprint(chip, p, set);
        double nesHz = NesVoiceWriter.NesCpuHz / (16.0 * ((int)Math.Round(NesVoiceWriter.NesCpuHz / (16.0 * 261.63)) - 1 + 1));
        var badPitch = Enumerable.Range(0, count).Where(p => !(Math.Abs(SegaMeasure.Cents(prints[p].Hz, nesHz)) < 15)).Select(name).ToList();
        Check($"{label}: all {count} patches sound a steady tone at the asked pitch (C4, within 15 cents)", badPitch.Count == 0, badPitch.Count == 0 ? "" : "off: " + string.Join(", ", badPitch));
        var pairs = new List<(double d, int a, int b)>();
        for (int a = 0; a < count; a++) for (int b = a + 1; b < count; b++) pairs.Add((Distance(prints[a], prints[b]), a, b));
        pairs.Sort((x, y) => x.d.CompareTo(y.d));
        var close = pairs.Where(t => t.d < MinDistanceDb && !isPsg(t.a) && !isPsg(t.b)).ToList();
        Check($"{label}: no two patches are alike (every pair differs by at least {MinDistanceDb} dB RMS in harmonics and envelope)", close.Count == 0,
            close.Count == 0 ? $"closest pair {name(pairs[0].a)} / {name(pairs[0].b)} {pairs[0].d:F1} dB" : string.Join("; ", close.Take(5).Select(t => $"{name(t.a)} / {name(t.b)} {t.d:F1} dB")));
        Console.WriteLine($"        the closest pairs to listen to first: {string.Join("; ", pairs.Where(t => !isPsg(t.a) && !isPsg(t.b)).Take(4).Select(t => $"{name(t.a)} / {name(t.b)} {t.d:F1} dB"))}");
        var levels = Enumerable.Range(0, count).Where(p => !isPsg(p)).Select(p => prints[p].Rms).ToList();
        double spread = SegaMeasure.Db(levels.Max(), levels.Min());
        Check($"{label}: the patches are within 3 dB of each other in level (the calibration the bridges rely on)", spread < 3.0, $"loudest {SegaMeasure.Db(levels.Max(), levels.Min()):F2} dB over the quietest");
        if (verbose)
            for (int p = 0; p < count; p++)
                Console.WriteLine($"        [{p,2}] {name(p),-16} {prints[p].Hz:F1} Hz RMS {prints[p].Rms:F4}  harmonics {string.Join(" ", prints[p].Harmonics.Select(v => v.ToString("F0").PadLeft(3)))}  envelope {string.Join(" ", prints[p].Envelope.Select(v => v.ToString("F0").PadLeft(3)))}");
    }
}
