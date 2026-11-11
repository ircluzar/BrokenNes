// `FruityHost opl-fit-test [--no-mutants]`: the OPL2 patch fitter (Pc/OplFit.cs), no DLL.
//   self      a patch that is NOT in the bank (seeded random fields), rendered on the AdLib: the fit must reach >= 85 % and beat every bank patch as it is by 5 points (measured 88.3 %: a local minimum, the exact patch is not found)
//   square    a band-limited 50 % square at 440 Hz (odd harmonics): the fit must beat the bank's sine ("SV triangle") by 10 points and put the 3rd harmonic within 6 dB of -9.5 dB
//   pluck     a decaying sawtooth (a plucked string): the fit's envelope must decay (last frame below half the first)
//   mutant    OplFit.Mutant 1 (the candidates measured an octave up) must lose >= 5 points on `self`
using BrokenNes2.Pc;

namespace BrokenNes.FruityHost;

public static class OplFitChecks
{
    public static int Main(string[] args)
    {
        bool mutants = !args.Contains("--no-mutants");
        int fails = 0;
        void Expect(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) fails++; }
        const double f0 = 440;
        int n = 44100 / 2;

        // self
        var rng = new Random(7);
        var secret = (byte[])OplBank.Patches[OplBank.GmFirst + 40].Regs.Clone();   // violin, then moved off the bank
        OplBank.SetField(secret, 4, 3); OplBank.SetField(secret, 6, 20); OplBank.SetField(secret, 24, 5); OplBank.SetField(secret, 13, 6);
        var target = OplFit.Render(secret, f0, n);
        var tf = OplFit.Measure(target, 44100, f0);
        double bankBest = OplBank.Patches.Where(p => !p.Snare).Max(p => 100 * (1 - OplFit.Compare(tf, OplFit.Measure(OplFit.Render(p.Regs, f0, n), 44100, f0)).Distance));
        var r = OplFit.Fit(target, 44100, f0);
        Console.WriteLine($"      self: {r.Similarity}% (bank best as is {bankBest:0.0}%), from {r.StartedFrom}, {r.Renders} renders, patch {OplFit.Hex(r.Patch)} (secret {OplFit.Hex(secret)})");
        Expect(r.Similarity >= 85 && r.Similarity >= bankBest + 5, "self: an off-bank AdLib patch is approached (>= 85 %, 5 points over the bank's best as is)");

        // square
        var sq = new float[n];
        for (int i = 0; i < n; i++) { double s = 0; for (int k = 1; k < 40; k += 2) s += Math.Sin(2 * Math.PI * f0 * k * i / 44100) / k; sq[i] = (float)(0.3 * s); }
        var sf = OplFit.Measure(sq, 44100, f0);
        double sine = 100 * (1 - OplFit.Compare(sf, OplFit.Measure(OplFit.Render(OplBank.Patches[3].Regs, f0, n), 44100, f0)).Distance);
        var rs = OplFit.Fit(sq, 44100, f0);
        var rf = OplFit.Measure(OplFit.Render(rs.Patch, f0, n), 44100, f0);
        Console.WriteLine($"      square: {rs.Similarity}% (sine {sine:0.0}%), harmonics dB {string.Join(" ", rf.Spectrum.Take(6).Select(x => x.ToString("0")))}, from {rs.StartedFrom}");
        Expect(rs.Similarity >= sine + 10 && Math.Abs(rf.Spectrum[2] - -9.5) <= 6, "square: better than the sine by 10 points, 3rd harmonic near -9.5 dB");

        // pluck
        var pl = new float[n];
        for (int i = 0; i < n; i++) { double ph = (f0 * i / 44100) % 1; pl[i] = (float)((2 * ph - 1) * 0.4 * Math.Exp(-i / (44100 * 0.12))); }
        var rp = OplFit.Fit(pl, 44100, f0);
        var pe = OplFit.Measure(OplFit.Render(rp.Patch, f0, n), 44100, f0).Envelope;
        Console.WriteLine($"      pluck: {rp.Similarity}%, envelope first {pe[1]:0.00} last {pe[^1]:0.00}, from {rp.StartedFrom}");
        Expect(pe[^1] < pe[1] * 0.5, "pluck: the fitted patch decays");

        if (mutants)
        {
            OplFit.Mutant = 1;
            var m = OplFit.Fit(target, 44100, f0);
            OplFit.Mutant = 0;
            Console.WriteLine($"      mutant 1 (octave-up measure): self {m.Similarity}% vs {r.Similarity}%");
            Expect(m.Similarity <= r.Similarity - 5, "deliberate bug 1 is caught (the fit gets worse)");
        }
        Console.WriteLine($"{fails} failed");
        return fails == 0 ? 0 : 1;
    }
}
