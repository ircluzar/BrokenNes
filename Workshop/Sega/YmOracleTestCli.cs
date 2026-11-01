using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--ym-oracle-test</c>: the YM2612 (<see cref="Ym2612"/>) against two oracles, both run black-box from the tools folder (outside the repository):
/// <list type="number">
/// <item><b>ymfm itself</b> (<c>ymfm_ref.exe</c>, BSD-3-Clause, the source of the port): random register traffic at random chip clocks, status reads included, run through the original C++ and our C# chip, for the
/// discrete chip and the YM3438 variant. This checks the <i>port</i>: every output sample, every status byte and every read must be equal.</item>
/// <item><b>Nuked-OPN2</b> (<c>opn2_oracle.exe</c>, LGPL-2.1, die-derived, oracle only): scripted cases, same writes at the same chip clocks, compared sample by sample. ymfm is not bit-identical to Nuked by design
/// (it computes a channel's four operators at one instant, the chip pipelines them over 24 slots), so each case states its bound and the numbers it measured.</item>
/// </list>
/// Exit code 0 = all passed, 1 = a mismatch, 3 = an oracle is not built (reported loudly, never as a pass).
/// </summary>
internal static class YmOracleTestCli
{
    private static int passes, failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    public static int Run(string[] args)
    {
        string[] needed = { "ymfm_ref.exe", "opn2_oracle.exe" };
        foreach (var exe in needed)
            if (!File.Exists(YmScript.Harness(exe)))
            {
                Console.WriteLine($"SKIPPED: {exe} is not built ({YmScript.Harness(exe)}). Run Workshop\\Sega\\fetch-tools.ps1 then Workshop\\Sega\\build-oracles.ps1. This is not a pass.");
                return 3;
            }
        bool ymfmOnly = M68kTestCli.Flag(args, "--ymfm-only"), nukedOnly = M68kTestCli.Flag(args, "--nuked-only");
        int seeds = int.TryParse(M68kTestCli.Arg(args, "--seeds"), out int sd) ? sd : 6;
        if (!nukedOnly)
        {
            Console.WriteLine("== the C# port against the original ymfm (random register traffic, every sample, status byte and read must be equal)");
            PortAgainstYmfm(seeds);
        }
        if (!ymfmOnly)
        {
            Console.WriteLine("\n== against Nuked-OPN2 (see the bounds in each line)");
            NukedCases.Run(Check);
        }
        Console.WriteLine($"\n{passes} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------- the port against ymfm

    private static void PortAgainstYmfm(int seeds)
    {
        const int samples = 40_000;
        foreach (bool ladder in new[] { true, false })
        {
            for (int seed = 1; seed <= seeds; seed++)
            {
                var script = FuzzScript(new Random(seed * 7919 + (ladder ? 1 : 2)), samples);
                var mine = script.RunMine(ladder, samples, anyPort: true, setup: c => c.SlotPipeline = false);       // the pure ymfm arithmetic, which is what ymfm_ref runs
                var ref1 = script.RunYmfm(ladder, samples, anyPort: true);
                int badL = 0, badR = 0, badStatus = 0, firstBad = -1;
                long nonZero = 0;
                for (int i = 0; i < samples; i++)
                {
                    if (mine.Left[i] != ref1.Left[i]) { badL++; if (firstBad < 0) firstBad = i; }
                    if (mine.Right[i] != ref1.Right[i]) badR++;
                    if (mine.Status[i] != ref1.Status[i]) badStatus++;
                    if (ref1.Left[i] != 0 || ref1.Right[i] != 0) nonZero++;
                }
                int badReads = 0;
                if (mine.Reads.Count != ref1.Reads.Count) badReads = Math.Max(mine.Reads.Count, ref1.Reads.Count);
                else for (int i = 0; i < mine.Reads.Count; i++) if (mine.Reads[i] != ref1.Reads[i]) badReads++;
                bool ok = badL == 0 && badR == 0 && badStatus == 0 && badReads == 0;
                // the traffic must be non-trivial: an all-silent run proves nothing
                bool busy = nonZero > samples / 10;
                Check($"{(ladder ? "YM2612" : "YM3438")}, seed {seed}: {samples:N0} samples, {script.Events.Count:N0} accesses, {nonZero:N0} non-silent samples", ok && busy,
                    ok ? $"left, right, status and {mine.Reads.Count} reads identical" : $"left {badL}, right {badR}, status {badStatus}, reads {badReads} differ; first at sample {firstBad}");
            }
        }
    }

    /// <summary>Random register traffic: every channel and operator gets plausible patches, then notes, timers, LFO, SSG-EG, channel 3 modes, CSM, the DAC and status reads come and go at random chip clocks.</summary>
    private static YmScript FuzzScript(Random rng, int samples)
    {
        var s = new YmScript();
        long total = YmScript.Clocks(samples);
        void W(long clock, int port, int value) => s.Write(clock, port, value);
        void Reg(long clock, int part, int addr, int value) { W(clock, part * 2, addr); W(clock + rng.Next(0, 3) * 6, part * 2 + 1, value); }

        // an initial patch for every channel, so there is something to hear
        long t = 0;
        for (int part = 0; part < 2; part++)
            for (int c = 0; c < 3; c++)
            {
                for (int slot = 0; slot < 4; slot++)
                {
                    int off = c + 4 * slot;
                    Reg(t += 300, part, 0x30 + off, rng.Next(0, 128) & 0x77);
                    Reg(t += 300, part, 0x40 + off, rng.Next(0, 3) == 0 ? rng.Next(0, 128) : rng.Next(0, 40));
                    Reg(t += 300, part, 0x50 + off, rng.Next(0, 256) & (rng.Next(0, 2) == 0 ? 0xDF : 0xFF));
                    Reg(t += 300, part, 0x60 + off, rng.Next(0, 256) & 0x9F);
                    Reg(t += 300, part, 0x70 + off, rng.Next(0, 32));
                    Reg(t += 300, part, 0x80 + off, rng.Next(0, 256));
                    Reg(t += 300, part, 0x90 + off, rng.Next(0, 4) == 0 ? rng.Next(8, 16) : 0);
                }
                Reg(t += 300, part, 0xB0 + c, rng.Next(0, 64));
                Reg(t += 300, part, 0xB4 + c, rng.Next(0, 4) == 0 ? rng.Next(0, 256) : (0xC0 | rng.Next(0, 64)));
                Reg(t += 300, part, 0xA4 + c, rng.Next(0, 64));
                Reg(t += 300, part, 0xA0 + c, rng.Next(0, 256));
            }
        Reg(t += 300, 0, 0x22, 0x08 | rng.Next(0, 8));

        // then random events until the end
        while (t < total)
        {
            t += 6 * rng.Next(1, 160);
            int kind = rng.Next(0, 100);
            int part = rng.Next(0, 2);
            int c = rng.Next(0, 3);
            if (kind < 22)
            {
                int bank = rng.Next(0, 7);
                int off = c + 4 * rng.Next(0, 4);
                int addr = 0x30 + 0x10 * bank + off;
                int v = bank switch { 0 => rng.Next(0, 128) & 0x77, 1 => rng.Next(0, 3) == 0 ? rng.Next(0, 128) : rng.Next(0, 50), 2 => rng.Next(0, 256), 3 => rng.Next(0, 256), 4 => rng.Next(0, 32), 5 => rng.Next(0, 256), _ => rng.Next(0, 4) == 0 ? rng.Next(8, 16) : rng.Next(0, 16) };
                Reg(t, part, addr, v);
            }
            else if (kind < 38) { Reg(t, part, 0xA4 + c, rng.Next(0, 64)); Reg(t + 600, part, 0xA0 + c, rng.Next(0, 256)); }
            else if (kind < 44) { Reg(t, 0, 0xA8 + rng.Next(0, 3), rng.Next(0, 256)); Reg(t + 600, 0, 0xAC + rng.Next(0, 3), rng.Next(0, 64)); }
            else if (kind < 52) Reg(t, part, 0xB0 + c, rng.Next(0, 64));
            else if (kind < 58) Reg(t, part, 0xB4 + c, rng.Next(0, 256));
            else if (kind < 76) { int ch = rng.Next(0, 6); Reg(t, 0, 0x28, (rng.Next(0, 3) == 0 ? 0xF0 : rng.Next(0, 16) << 4) | (ch < 3 ? ch : ch + 1)); }
            else if (kind < 79) Reg(t, 0, 0x22, rng.Next(0, 16));
            else if (kind < 82) { Reg(t, 0, 0x24, rng.Next(0, 256)); Reg(t + 600, 0, 0x25, rng.Next(0, 4)); }
            else if (kind < 84) Reg(t, 0, 0x26, rng.Next(0, 256));
            else if (kind < 88) Reg(t, 0, 0x27, rng.Next(0, 256));
            else if (kind < 91) Reg(t, 0, rng.Next(0, 3) == 0 ? 0x2B : 0x2C, rng.Next(0, 256));
            else if (kind < 94) { W(t, 0, 0x2A); W(t + 36, 1, rng.Next(0, 256)); }
            else if (kind < 96) { W(t, rng.Next(0, 4), rng.Next(0, 256)); }               // stray port writes: data without address, mismatched pairs
            else s.Read(t, rng.Next(0, 4));
            if (rng.Next(0, 40) == 0) s.Read(t + 12, rng.Next(0, 4));
        }
        return s;
    }
}
