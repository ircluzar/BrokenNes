using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>One scripted access to a YM2612: a write or a status read at a chip clock (the chip's input clock, master / 7; 144 per FM sample).</summary>
internal readonly record struct YmEvent(long Clock, bool IsRead, int Port, int Value);

/// <summary>
/// A register-write script for the YM2612 verifiers: a time cursor, a list of accesses, and the three ways to run it: on our chip (<see cref="Ym2612"/>), on the original ymfm (<c>ymfm_ref.exe</c>)
/// and on Nuked-OPN2 (<c>opn2_oracle.exe</c>), with the same accesses at the same chip clocks. Times are chip input clocks; Nuked takes one clock call per 6 of them, so every access sits on a multiple of 6.
/// </summary>
internal sealed class YmScript
{
    public const int Sample = Ym2612.ClocksPerSample;
    /// <summary>The data write follows its address write by this many chip clocks (Nuked needs a few clock calls to take an address up).</summary>
    public const int AddressToData = 24;
    /// <summary>Spacing of consecutive register writes: two samples, longer than the busy flag.</summary>
    public const int Gap = 2 * Sample;

    public readonly List<YmEvent> Events = new();
    /// <summary>The cursor: where the next <see cref="Reg"/> goes.</summary>
    public long T;

    public static string ToolsRoot => Environment.GetEnvironmentVariable("BROKENNES_SEGA_TOOLS") ?? @"C:\BrokenNes-tools\sega";
    public static string Harness(string exe) => Path.Combine(ToolsRoot, @"oracles\harness", exe);

    public YmScript Write(long clock, int port, int value) { Events.Add(new YmEvent(clock, false, port, value)); return this; }
    public YmScript Read(long clock, int port) { Events.Add(new YmEvent(clock, true, port, 0)); return this; }

    /// <summary>Address then data for a register of part 0 or 1, at the cursor; the cursor moves on by <see cref="Gap"/>.</summary>
    public YmScript Reg(int part, int addr, int value)
    {
        Write(T, part * 2, addr);
        Write(T + AddressToData, part * 2 + 1, value);
        T += Gap;
        return this;
    }

    /// <summary>Register 0x000-0x1FF in one number (part = bit 8).</summary>
    public YmScript Reg(int reg, int value) => Reg(reg >> 8 & 1, reg & 0xFF, value);

    /// <summary>The cursor rounded up to the start of a sample, plus <paramref name="samples"/> more.</summary>
    public YmScript Align(int samples = 0)
    {
        T = (T + Sample - 1) / Sample * Sample + (long)samples * Sample;
        return this;
    }

    /// <summary>Clocks of the whole run for <paramref name="samples"/> samples.</summary>
    public static long Clocks(long samples) => samples * Sample;

    // ---------------------------------------------------------------------------------------------- our chip

    public sealed class Result
    {
        public int[] Left = Array.Empty<int>(), Right = Array.Empty<int>();
        public byte[] Status = Array.Empty<byte>();            // the register at the end of each sample
        public List<(long Clock, int Port, int Value)> Reads = new();
    }

    private sealed class Collector : IYm2612Sink
    {
        public readonly List<int> L = new(), R = new();
        public void Sample(int left, int right) { L.Add(left); R.Add(right); }
    }

    /// <summary>Runs the script on our chip for <paramref name="samples"/> samples. <paramref name="setup"/> can change the chip before the first access (mute mask, status decode).</summary>
    public Result RunMine(bool ladder, int samples, bool anyPort = false, Action<Ym2612>? setup = null, Action<Ym2612, long>? perSample = null)
    {
        var chip = new Ym2612(ladder) { StatusOnAnyPort = anyPort };
        setup?.Invoke(chip);
        var col = new Collector();
        chip.Sink = col;
        var res = new Result();
        var status = new byte[samples];
        var ordered = StableByClock(Events);              // ties keep script order (an address write and its data write can share a clock)
        int next = 0;
        for (int s = 0; s < samples; s++)
        {
            long end = (long)(s + 1) * Sample;
            // accesses inside this sample's interval, then the sample (an access at exactly the end clock is applied after the sample ending there)
            while (next < ordered.Count && ordered[next].Clock < end)
            {
                var e = ordered[next++];
                if (e.IsRead) res.Reads.Add((e.Clock, e.Port, chip.Read(e.Port, e.Clock)));
                else chip.Write(e.Port, (byte)e.Value, e.Clock);
            }
            chip.RunTo(end);
            status[s] = chip.ReadStatus();
            perSample?.Invoke(chip, s);
        }
        res.Left = col.L.ToArray(); res.Right = col.R.ToArray(); res.Status = status;
        return res;
    }

    private static List<YmEvent> StableByClock(List<YmEvent> events)
    {
        var idx = new int[events.Count];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        Array.Sort(idx, (a, b) => events[a].Clock != events[b].Clock ? events[a].Clock.CompareTo(events[b].Clock) : a.CompareTo(b));
        var list = new List<YmEvent>(events.Count);
        foreach (int i in idx) list.Add(events[i]);
        return list;
    }

    // ---------------------------------------------------------------------------------------------- the original ymfm

    private static string TempDir
    {
        get
        {
            string d = Path.Combine(Path.GetTempPath(), "bn_ym_oracle_" + Environment.ProcessId);
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static int RunProcess(string exe, string arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { p.Kill(); throw new TimeoutException(Path.GetFileName(exe) + " did not finish"); }
        if (p.ExitCode != 0) throw new InvalidOperationException(Path.GetFileName(exe) + " failed: " + errTask.Result);
        return p.ExitCode;
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Runs the script on ymfm itself. variant 0 = ym2612 (ladder), 1 = ym3438.</summary>
    public Result RunYmfm(bool ladder, int samples, bool anyPort = false)
    {
        string dir = TempDir, tag = Tag();
        string script = Path.Combine(dir, tag + ".txt"), output = Path.Combine(dir, tag + ".bin");
        var sb = new StringBuilder();
        sb.Append(ladder ? 0 : 1).Append(' ').Append((long)samples * Sample).Append(' ').Append(anyPort ? 1 : 0).Append('\n');
        foreach (var e in StableByClock(Events)) sb.Append(e.IsRead ? $"R {e.Clock} {e.Port}\n" : $"W {e.Clock} {e.Port} {e.Value}\n");
        File.WriteAllText(script, sb.ToString());
        RunProcess(Harness("ymfm_ref.exe"), $"\"{script}\" \"{output}\"", 300_000);
        var bytes = File.ReadAllBytes(output);
        var res = new Result { Left = new int[samples], Right = new int[samples], Status = new byte[samples] };
        for (int s = 0; s < samples; s++)
        {
            res.Left[s] = BitConverter.ToInt32(bytes, s * 9);
            res.Right[s] = BitConverter.ToInt32(bytes, s * 9 + 4);
            res.Status[s] = bytes[s * 9 + 8];
        }
        res.Reads = ParseReads(output + ".reads");
        TryDelete(script, output, output + ".reads");
        return res;
    }

    private static List<(long, int, int)> ParseReads(string path)
    {
        var list = new List<(long, int, int)>();
        foreach (var line in File.ReadAllLines(path))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 3) list.Add((long.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1]), int.Parse(p[2])));
        }
        return list;
    }

    private static void TryDelete(params string[] paths) { foreach (var p in paths) { try { File.Delete(p); } catch { } } }

    // ---------------------------------------------------------------------------------------------- Nuked-OPN2

    /// <summary>What one Nuked run produced, per FM sample (24 clock calls).</summary>
    public sealed class NukedResult
    {
        /// <summary>The sum of the library's left output over the sample's 24 clock calls, divided by 3: the six channels' values in the 9-bit domain, which is what ymfm's mixer sums too.</summary>
        public int[] AccLeft = Array.Empty<int>(), AccRight = Array.Empty<int>();
        public byte[] Busy = Array.Empty<byte>(), TimerA = Array.Empty<byte>(), TimerB = Array.Empty<byte>();
        /// <summary>Per sample: the 24 slots' envelope level, phase counter and envelope output at the sample's end (only with <c>state</c>).</summary>
        public ushort[][] EgLevel = Array.Empty<ushort[]>();
        public uint[][] Phase = Array.Empty<uint[]>();
        public short[][] ChOut = Array.Empty<short[]>();
        public short[][] FmOut = Array.Empty<short[]>();
        public ushort[][] FmMod = Array.Empty<ushort[]>();
        public uint[][] PgInc = Array.Empty<uint[]>();
        /// <summary>Per sample: lfo_am, lfo_pm, lfo_cnt, lfo_en.</summary>
        public byte[][] Lfo = Array.Empty<byte[]>();
        public List<(long Clock, int Port, int Value)> Reads = new();
        /// <summary>The raw per-clock left output, when requested.</summary>
        public short[] Raw = Array.Empty<short>();
    }

    /// <summary>Where the 24-clock window that makes up one oracle sample starts, in clock calls from the multiple of 24 (the library's output is time-multiplexed: each channel's value shows up for a few
    /// of the 24 clocks, so a sample is the sum over a window, and the window's phase decides which of the six channels' values count as the same sample). Chosen so all six channels line up with ours.</summary>
    public static int NukedWindow = 0;

    /// <summary>Runs the script on Nuked-OPN2. <paramref name="chipType"/> bit 0 = YM2612 mode, bit 1 = status on any port. Every access clock must be a multiple of 6.</summary>
    public NukedResult RunNuked(int chipType, int samples, bool state = false, bool raw = false, int? window = null)
    {
        int win = window ?? NukedWindow;
        string dir = TempDir, tag = Tag();
        string script = Path.Combine(dir, tag + ".txt"), output = Path.Combine(dir, tag + ".bin");
        var sb = new StringBuilder();
        // per-clock records are needed to sum the multiplexed output, so flag bit 0 is always set; one window more than asked for, so a shifted window still has its last clocks
        sb.Append(chipType).Append(' ').Append((long)(samples + 1) * 24).Append(' ').Append(1 | (state ? 2 : 0)).Append('\n');
        foreach (var e in StableByClock(Events))
        {
            if (e.Clock % 6 != 0) throw new InvalidOperationException($"access at chip clock {e.Clock} is not on a Nuked clock boundary");
            sb.Append(e.IsRead ? $"R {e.Clock / 6} {e.Port}\n" : $"W {e.Clock / 6} {e.Port} {e.Value}\n");
        }
        File.WriteAllText(script, sb.ToString());
        RunProcess(Harness("opn2_oracle.exe"), $"\"{script}\" \"{output}\"", 600_000);
        var bytes = File.ReadAllBytes(output);
        var res = new NukedResult
        {
            AccLeft = new int[samples], AccRight = new int[samples], Busy = new byte[samples], TimerA = new byte[samples], TimerB = new byte[samples],
        };
        if (state) { res.EgLevel = new ushort[samples][]; res.Phase = new uint[samples][]; res.ChOut = new short[samples][]; res.FmOut = new short[samples][]; res.FmMod = new ushort[samples][]; res.PgInc = new uint[samples][]; res.Lfo = new byte[samples][]; }
        if (raw) res.Raw = new short[samples * 24];
        var rawL = new short[(samples + 1) * 24]; var rawR = new short[(samples + 1) * 24];
        int pos = 0;
        for (int s = 0; s <= samples; s++)
        {
            for (int k = 0; k < 24; k++)
            {
                rawL[s * 24 + k] = BitConverter.ToInt16(bytes, pos); rawR[s * 24 + k] = BitConverter.ToInt16(bytes, pos + 2);
                if (k == 23 && s < samples)
                {
                    res.Busy[s] = bytes[pos + 4]; res.TimerA[s] = bytes[pos + 5]; res.TimerB[s] = bytes[pos + 6];
                }
                pos += 8;
                if (k == 23 && state)
                {
                    if (s < samples)
                    {
                        var lv = new ushort[24]; var ph = new uint[24]; var co = new short[6];
                        for (int i = 0; i < 24; i++) lv[i] = BitConverter.ToUInt16(bytes, pos + i * 2);
                        for (int i = 0; i < 24; i++) ph[i] = BitConverter.ToUInt32(bytes, pos + 48 + i * 4);
                        for (int i = 0; i < 6; i++) co[i] = BitConverter.ToInt16(bytes, pos + 144 + i * 2);
                        var fo = new short[24]; var fmd = new ushort[24];
                        for (int i = 0; i < 24; i++) { fo[i] = BitConverter.ToInt16(bytes, pos + 156 + 48 + i * 2); fmd[i] = BitConverter.ToUInt16(bytes, pos + 156 + 96 + i * 2); }
                        var inc = new uint[24]; var lf = new byte[4];
                        for (int i = 0; i < 24; i++) inc[i] = BitConverter.ToUInt32(bytes, pos + 156 + 144 + i * 4);
                        for (int i = 0; i < 4; i++) lf[i] = bytes[pos + 156 + 144 + 96 + i];
                        res.EgLevel[s] = lv; res.Phase[s] = ph; res.ChOut[s] = co; res.FmOut[s] = fo; res.FmMod[s] = fmd; res.PgInc[s] = inc; res.Lfo[s] = lf;
                    }
                    pos += 48 + 96 + 12 + 48 + 48 + 48 + 96 + 4;
                }
            }
        }
        for (int s = 0; s < samples; s++)
        {
            int sl = 0, sr = 0;
            for (int k = 0; k < 24; k++) { sl += rawL[s * 24 + win + k]; sr += rawR[s * 24 + win + k]; if (raw) res.Raw[s * 24 + k] = rawL[s * 24 + win + k]; }
            res.AccLeft[s] = sl / 3; res.AccRight[s] = sr / 3;
        }
        res.Reads = ParseReads(output + ".reads");
        TryDelete(script, output, output + ".reads");
        return res;
    }

    /// <summary>ymfm's final scaling applied to a mixed accumulator, so Nuked's sums can be compared with our output as integers.</summary>
    public static int Scale(bool ladder, int acc) => ladder ? acc * 128 * 64 / (6 * 65) : acc * 128 / 6;
}
