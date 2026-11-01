using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--sega-oracle-test</c>: the SN76489 against an oracle, sample for sample. The oracle is Nuked-PSG (a die-derived emulation of the Genesis VDP's PSG, GPL), which stays in the
/// tools folder outside the repository and is driven black-box by <c>psg_oracle.exe</c> (built by <c>build-oracles.ps1</c>). Both chips get the same register writes on the same PSG sample
/// boundaries and are compared on the raw level of every channel, one PSG sample (16 input clocks) at a time, so what is checked is timing and sequence, not a listening test:
/// tone periods across the whole 10-bit range, and the noise register in every mode (white and periodic, the three fixed rates, and the rate taken from tone 3), over 120,000 samples each,
/// where one wrong tap or one off-by-one in a rate shows as a mismatch.
/// Exit code 0 = every check passed, 1 = a mismatch, 3 = the oracle is not built (reported loudly, never as a pass).
/// </summary>
internal static class SegaOracleTestCli
{
    private const double Clock = 3_579_545.0;
    private static int failures, passes;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    private static string ToolsRoot => Environment.GetEnvironmentVariable("BROKENNES_SEGA_TOOLS") ?? @"C:\BrokenNes-tools\sega";

    public static int Run(string[] args)
    {
        string exe = Path.Combine(ToolsRoot, @"oracles\harness\psg_oracle.exe");
        if (!File.Exists(exe))
        {
            Console.WriteLine($"SKIPPED: the PSG oracle is not built ({exe}). Run Workshop\\Sega\\fetch-tools.ps1 then Workshop\\Sega\\build-oracles.ps1. This is not a pass.");
            return 3;
        }
        Console.WriteLine("== tone periods against Nuked-PSG");
        Tones(exe);
        Console.WriteLine("\n== noise register against Nuked-PSG");
        Noise(exe);
        Console.WriteLine("\n== where the Master System and Game Gear part differs, by design");
        SmsPart();
        Console.WriteLine("\n== attenuation (information, not a pass or fail)");
        Attenuation(exe);
        Console.WriteLine($"\n{passes} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------- drivers

    private static byte[] Oracle(string exe, int samples, IReadOnlyList<(int Sample, byte Value)> writes)
    {
        string dir = Path.Combine(Path.GetTempPath(), "bn_psg_oracle_" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, "script.txt"), output = Path.Combine(dir, "out.bin");
        var sb = new StringBuilder().AppendLine(samples.ToString());
        foreach (var (s, v) in writes) sb.Append(s).Append(' ').Append(v).AppendLine();
        File.WriteAllText(script, sb.ToString());
        var psi = new ProcessStartInfo(exe, $"\"{script}\" \"{output}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        if (!p.WaitForExit(120_000)) { p.Kill(); throw new TimeoutException("psg_oracle did not finish"); }
        if (p.ExitCode != 0) throw new InvalidOperationException("psg_oracle failed: " + p.StandardError.ReadToEnd());
        return File.ReadAllBytes(output);
    }

    private static byte[] Mine(PsgVariant variant, int samples, IReadOnlyList<(int Sample, byte Value)> writes)
    {
        var psg = new Sn76489(Clock, 44100, variant);
        var outp = new byte[samples];
        var scratch = new short[4096];
        int next = 0;
        for (int i = 0; i < samples; i++)
        {
            while (next < writes.Count && writes[next].Sample <= i) psg.Write(writes[next++].Value);
            psg.Run(16);
            outp[i] = (byte)psg.RawState;
            if ((i & 255) == 255) psg.ReadSamples(scratch);   // keep the audio buffer drained; only the raw levels are compared
        }
        return outp;
    }

    private static (int Mismatches, int Shift) BestAlignment(byte[] oracle, byte[] mine, int bit, int from, int to, int maxShift)
    {
        int best = int.MaxValue, bestShift = 0;
        for (int shift = -maxShift; shift <= maxShift; shift++)
        {
            int bad = 0;
            for (int i = from; i < to && bad < best; i++)
            {
                int j = i + shift;
                if (j < 0 || j >= mine.Length) { bad++; continue; }
                if (((oracle[i] >> bit) & 1) != ((mine[j] >> bit) & 1)) bad++;
            }
            if (bad < best) { best = bad; bestShift = shift; }
        }
        return (best, bestShift);
    }

    /// <summary>The lengths of the runs of tone 1's level after the settling time, without the first and last (cut by the window).</summary>
    private static List<int> Runs(byte[] samples)
    {
        var runs = new List<int>(); int length = 0; int last = (samples[1000] >> 3) & 1;
        for (int i = 1000; i < samples.Length; i++)
        {
            int level = (samples[i] >> 3) & 1;
            if (level == last) { length++; continue; }
            runs.Add(length); length = 1; last = level;
        }
        if (runs.Count > 2) { runs.RemoveAt(0); }
        return runs;
    }

    private static byte Latch(int channel, bool volume, int low4) => (byte)(0x80 | channel << 5 | (volume ? 0x10 : 0) | low4 & 0xF);

    private static List<(int, byte)> ToneWrites(int channel, int period) => new()
    {
        (40, Latch(channel, false, period & 0xF)),
        (41, (byte)(period >> 4 & 0x3F)),
    };

    // ---------------------------------------------------------------------------------------------- tones

    private static void Tones(string exe)
    {
        int[] periods = { 0, 1, 2, 3, 4, 5, 7, 16, 100, 254, 255, 256, 257, 511, 1023 };
        const int samples = 24_000;
        foreach (int channel in new[] { 0, 1, 2 })
        {
            int bit = 3 - channel;
            var bad = new List<string>();
                        foreach (int period in periods)
            {
                var writes = ToneWrites(channel, period);
                var o = Oracle(exe, samples, writes);
                var m = Mine(PsgVariant.MegaDrive, samples, writes);
                // the oracle's counters were already running when the period was written, so the two start a half period apart by an arbitrary phase: search a full period either way
                var (mismatches, shift) = BestAlignment(o, m, bit, 4400, samples - 2200, Math.Max(4, Math.Min(2 * period + 8, 2100)));
                if (mismatches != 0) bad.Add($"N={period}: {mismatches} mismatches (best shift {shift})");
                }
            Check($"tone {channel + 1}: periods 0 to 1023 ({periods.Length} of them) match the oracle sample for sample", bad.Count == 0,
                bad.Count == 0 ? "each aligned to the oracle's counter phase, then identical for the whole run" : string.Join("; ", bad));
        }
    }

    // ---------------------------------------------------------------------------------------------- noise

    private static void Noise(string exe)
    {
        const int samples = 120_000;
        foreach (bool white in new[] { true, false })
        {
            foreach (int rate in new[] { 0, 1, 2 })
            {
                int control = (white ? 4 : 0) | rate;
                var writes = new List<(int, byte)> { (40, (byte)(0xE0 | control)) };
                var o = Oracle(exe, samples, writes);
                var m = Mine(PsgVariant.MegaDrive, samples, writes);
                var (bad, shift) = BestAlignment(o, m, 0, 4000, samples - 400, 400);
                Check($"noise {(white ? "white" : "periodic")}, fixed rate {rate} (clock/{512 << rate}): {samples - 4400:N0} samples identical", bad == 0, bad == 0 ? $"aligned at {shift:+0;-0;0} samples" : $"{bad} mismatches at best alignment {shift}");
            }
            foreach (int p3 in new[] { 3, 17, 100 })
            {
                int control = (white ? 4 : 0) | 3;
                var writes = new List<(int, byte)> { (40, Latch(2, false, p3 & 0xF)), (41, (byte)(p3 >> 4 & 0x3F)), (42, (byte)(0xE0 | control)) };
                var o = Oracle(exe, samples, writes);
                var m = Mine(PsgVariant.MegaDrive, samples, writes);
                var (bad, shift) = BestAlignment(o, m, 0, 4000, samples - 400, 400);
                Check($"noise {(white ? "white" : "periodic")}, rate from tone 3 (period {p3}): {samples - 4400:N0} samples identical", bad == 0, bad == 0 ? $"aligned at {shift:+0;-0;0} samples" : $"{bad} mismatches at best alignment {shift}");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------- attenuation

    /// <summary>Our 2 dB steps (the documented attenuation of the chip) against the levels the oracle uses. They agree near full volume and part at low volumes, where the oracle's are measured
    /// from a real Genesis output stage. This is a mixing difference, not a timing one, so it is reported rather than failed; the Genesis track decides whether the MegaDrive variant should follow it.</summary>
    private static void Attenuation(string exe)
    {
        var psi = new ProcessStartInfo(exe, "--levels") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        p.WaitForExit();
        var sb = new StringBuilder("  volume  ours   oracle  difference\n");
        double worst = 0; int worstAt = 0;
        for (int v = 0; v < 15 && v < lines.Length; v++)
        {
            double ours = Math.Pow(10, -2.0 * v / 20), theirs = double.Parse(lines[v], System.Globalization.CultureInfo.InvariantCulture);
            double db = 20 * Math.Log10(theirs / ours);
            if (Math.Abs(db) > Math.Abs(worst)) { worst = db; worstAt = v; }
            sb.AppendLine($"  {v,6}  {ours,5:F3}  {theirs,6:F3}  {db,+6:F1} dB");
        }
        Console.Write(sb);
        Console.WriteLine($"  largest difference {worst:+0.0;-0.0} dB at volume {worstAt}; volume 15 is silent in both");
    }

    // ---------------------------------------------------------------------------------------------- the Master System part

    private static void SmsPart()
    {
        // The Genesis part (the oracle) flips the output every PSG clock at a period of 0 or 1. The Master System and Game Gear part holds it high there (documented behaviour that
        // sample-playback tricks rely on). There is no die-derived oracle for the SMS part in the tools folder, so this is asserted from the documentation, and the divergence is
        // recorded here so it is not mistaken for an error.
        var writes = ToneWrites(0, 1);
        var sms = Mine(PsgVariant.Sega, 4000, writes);
        var md = Mine(PsgVariant.MegaDrive, 4000, writes);
        bool smsHigh = sms.Skip(100).All(b => (b & 8) != 0);
        bool mdFlips = md.Skip(100).Zip(md.Skip(101), (a, b) => ((a ^ b) & 8) != 0).All(x => x);
        Check("period 1: the Master System / Game Gear part holds the output high, the Genesis part flips it every PSG clock", smsHigh && mdFlips);
        // The two parts start a different half period apart (one held high before the write, the other already flipping), so compare the shape: the lengths of the runs.
        var sms2 = Runs(Mine(PsgVariant.Sega, 24_000, ToneWrites(0, 254)));
        var md2 = Runs(Mine(PsgVariant.MegaDrive, 24_000, ToneWrites(0, 254)));
        Check("period 254: the two parts play the same square wave (identical run lengths)", sms2.Count > 40 && sms2.SequenceEqual(md2), $"{sms2.Count} runs, every one {sms2.Distinct().Count() switch { 1 => "the same length", _ => "of mixed length" }}");
    }
}
