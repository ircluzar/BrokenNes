using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--sega-chips-test</c>: verifiers for the shared Sega chips, by measurement. The SN76489 is checked here (protocol, pitch, attenuation, the period 0/1 DC behaviour,
/// noise register periods computed independently, Game Gear stereo, state, band-limiting). The Z80 has its own verifier against instruction-level test vectors.
/// Exit code 0 = every check passed.
/// </summary>
internal static class SegaChipsTestCli
{
    private const double Clock = 3_579_545.0;
    private const int Rate = 44100;
    private static int failures, passes;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    public static int Run(string[] args)
    {
        Console.WriteLine("== SN76489");
        Psg();
        Console.WriteLine($"\n{passes} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------- helpers

    private static void Write(Sn76489 p, params byte[] bytes) { foreach (var b in bytes) p.Write(b); }
    private static byte[] Tone(int channel, int period) => new byte[] { (byte)(0x80 | channel << 5 | (period & 0xF)), (byte)(period >> 4 & 0x3F) };
    private static byte Vol(int channel, int volume) => (byte)(0x90 | channel << 5 | volume & 0xF);

    /// <summary>Runs the chip for <paramref name="seconds"/> and returns what it played (left and right, as floats in -1..1).</summary>
    private static (float[] L, float[] R) Capture(Sn76489 p, double seconds)
    {
        var l = new List<float>(); var r = new List<float>(); var buf = new short[2048];
        long total = (long)(seconds * Clock);
        for (long done = 0; done < total; done += 512)
        {
            p.Run(512);
            int n = p.ReadSamples(buf);
            for (int i = 0; i < n; i += 2) { l.Add(buf[i] / 32768f); r.Add(buf[i + 1] / 32768f); }
        }
        return (l.ToArray(), r.ToArray());
    }

    private static double Rms(float[] x, int from) { double s = 0; int n = 0; for (int i = from; i < x.Length; i++) { s += x[i] * x[i]; n++; } return n == 0 ? 0 : Math.Sqrt(s / n); }

    /// <summary>Rising zero crossings per second over the part after <paramref name="skipSeconds"/>.</summary>
    private static double Frequency(float[] x, double skipSeconds)
    {
        int start = (int)(skipSeconds * Rate), crossings = 0;
        for (int i = start + 1; i < x.Length; i++) if (x[i - 1] < 0 && x[i] >= 0) crossings++;
        return crossings / ((x.Length - start) / (double)Rate);
    }

    /// <summary>Magnitude of one frequency in a signal (Goertzel).</summary>
    private static double Goertzel(float[] x, int from, int to, double hz)
    {
        double w = 2 * Math.PI * hz / Rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (int i = from; i < to; i++) { double s0 = x[i] + c * s1 - s2; s2 = s1; s1 = s0; }
        return Math.Sqrt(s1 * s1 + s2 * s2 - c * s1 * s2) / (to - from) * 2;
    }

    private static double Hz(int period) => Clock / (32.0 * period);

    // ---------------------------------------------------------------------------------------------- the checks

    private static void Psg()
    {
        // the write protocol
        var p = new Sn76489(Clock, Rate);
        Write(p, 0x8E, 0x0F);
        Check("latch + data write sets a 10-bit tone period (0x8E, 0x0F -> 254)", p.Period(0) == 254, $"{p.Period(0)}");
        Write(p, 0xA1, 0x20, 0xC5, 0x3F);
        Check("tone 2 and tone 3 take their own registers", p.Period(1) == (0x20 << 4 | 1) && p.Period(2) == (0x3F << 4 | 5), $"{p.Period(1)} {p.Period(2)}");
        Write(p, 0x93);
        Check("a volume latch sets the volume", p.Volume(0) == 3);
        Write(p, 0x05);
        Check("a data byte after a volume latch replaces the volume (the latch is not cleared)", p.Volume(0) == 5);
        Write(p, 0xE5);
        Check("a noise latch sets the noise control", p.NoiseControl == 5);
        Write(p, 0x06);
        Check("a data byte after a noise latch replaces the noise control", p.NoiseControl == 6);
        Write(p, 0xD2);
        Check("channel 3's volume is its own (and tone 1's is untouched)", p.Volume(2) == 2 && p.Volume(0) == 5, $"{p.Volume(2)} {p.Volume(0)}");
        var fresh = new Sn76489(Clock, Rate);
        Check("power-on: every volume is 15 (silent), periods 0", Enumerable.Range(0, 4).All(c => fresh.Volume(c) == 15) && Enumerable.Range(0, 3).All(c => fresh.Period(c) == 0));

        // pitch: the output flips every N PSG clocks, so the frequency is clock / (32 N)
        foreach (int period in new[] { 1000, 254, 100, 20 })
        {
            var q = new Sn76489(Clock, Rate);
            Write(q, Tone(0, period)); Write(q, Vol(0, 0));
            var (l, _) = Capture(q, 1.6);
            double hz = Frequency(l, 0.4), expect = Hz(period);
            Check($"tone period {period}: {expect:0.0} Hz", Math.Abs(hz - expect) / expect < 0.006, $"measured {hz:0.0}");
        }

        // attenuation: 2 dB per step, volume 15 is silence
        double RmsAt(int volume)
        {
            var q = new Sn76489(Clock, Rate);
            Write(q, Tone(0, 254)); Write(q, Vol(0, volume));
            var (l, _) = Capture(q, 0.8);
            return Rms(l, (int)(0.3 * Rate));
        }
        double r0 = RmsAt(0), r4 = RmsAt(4), r8 = RmsAt(8), r15 = RmsAt(15);
        Check("volume 4 is 13045/32767 of volume 0", Math.Abs(r4 / r0 - 13045.0 / 32767) < 0.02, $"{r4 / r0:0.000} vs {13045.0 / 32767:0.000}");
        Check("volume 8 is 5193/32767 of volume 0", Math.Abs(r8 / r0 - 5193.0 / 32767) < 0.01, $"{r8 / r0:0.000} vs {5193.0 / 32767:0.000}");
        Check("volume 15 is silent", r15 < 1e-4, $"{r15:0.00000}");

        // tone period 0 and 1 hold the output high on the Sega part (no oscillation), and the TI part treats 0 as 1024
        foreach (int period in new[] { 0, 1 })
        {
            var q = new Sn76489(Clock, Rate);
            Write(q, Tone(0, period)); Write(q, Vol(0, 0));
            var (l, _) = Capture(q, 1.0);
            Check($"Sega: period {period} holds a constant level (no tone)", Rms(l, (int)(0.5 * Rate)) < 0.002, $"rms {Rms(l, (int)(0.5 * Rate)):0.00000}");
        }
        var ti = new Sn76489(Clock, Rate, PsgVariant.Ti);
        Write(ti, Tone(0, 0)); Write(ti, Vol(0, 0));
        var (tl, _) = Capture(ti, 1.6);
        Check("TI: period 0 acts as 1024 (109 Hz)", Math.Abs(Frequency(tl, 0.4) - Hz(1024)) / Hz(1024) < 0.02, $"{Frequency(tl, 0.4):0.0} Hz");

        // sample playback through the volume register while the tone is held (the classic trick): the output follows the volume writes
        {
            var q = new Sn76489(Clock, Rate);
            Write(q, Tone(0, 0));
            var l = new List<float>(); var buf = new short[2048];
            int clocksPerSample = (int)(Clock / 8000);
            for (int s = 0; s < 8000 / 2; s++)
            {
                Write(q, Vol(0, (s & 1) == 0 ? 0 : 15));   // an 4 kHz square wave made of volume writes at 8 kHz
                q.Run(clocksPerSample);
                int n = q.ReadSamples(buf);
                for (int i = 0; i < n; i += 2) l.Add(buf[i] / 32768f);
            }
            var arr = l.ToArray();
            Check("volume writes alone make sound (PCM playback through a held tone)", Rms(arr, Rate / 4) > 0.01, $"rms {Rms(arr, Rate / 4):0.0000}");
        }

        // the noise register: periods computed independently of the chip (57,337 white and 16 periodic for the Sega part; 32,767 and 15 for the TI's)
        int PeriodOf(PsgVariant v, int noiseControl, int maxShifts)
        {
            var q = new Sn76489(Clock, Rate, v);
            Write(q, (byte)(0xE0 | noiseControl & 7)); Write(q, Vol(3, 0));
            int init = q.NoiseRegisterValue, seen = 0, last = init;
            // rate 0: the register moves once per 512 CPU clocks; sample after each
            for (int i = 0; i < maxShifts + 4; i++)
            {
                q.Run(512);
                int now = q.NoiseRegisterValue;
                if (now != last) { seen++; last = now; if (now == init) return seen; }
            }
            return -1;
        }
        Check("Sega white noise (16-bit, taps 0 and 3) repeats after 57,337 shifts", PeriodOf(PsgVariant.Sega, 4, 60000) == 57337, $"{PeriodOf(PsgVariant.Sega, 4, 60000)}");
        Check("Sega periodic noise repeats after 16 shifts", PeriodOf(PsgVariant.Sega, 0, 100) == 16, $"{PeriodOf(PsgVariant.Sega, 0, 100)}");
        Check("TI white noise (15-bit, taps 0 and 1) repeats after 32,767 shifts", PeriodOf(PsgVariant.Ti, 4, 40000) == 32767, $"{PeriodOf(PsgVariant.Ti, 4, 40000)}");
        Check("TI periodic noise repeats after 15 shifts", PeriodOf(PsgVariant.Ti, 0, 100) == 15, $"{PeriodOf(PsgVariant.Ti, 0, 100)}");
        var nz = new Sn76489(Clock, Rate);
        Write(nz, 0xE4, Vol(3, 0)); nz.Run(512 * 40);
        int before = nz.NoiseRegisterValue; Write(nz, 0xE4);
        Check("any write to the noise register reloads the shift register ($8000)", before != 0x8000 && nz.NoiseRegisterValue == 0x8000, $"{before:X4} -> {nz.NoiseRegisterValue:X4}");

        // the noise shift rates: clock/512, /1024, /2048 and the rate of tone channel 3
        foreach (var (control, divisor) in new[] { (0, 512), (1, 1024), (2, 2048) })
        {
            var q = new Sn76489(Clock, Rate);
            Write(q, (byte)(0xE0 | control), Vol(3, 0));
            int shifts = 0, last = q.NoiseRegisterValue;
            for (int i = 0; i < 60 * divisor / 4; i++) { q.Run(4); int v = q.NoiseRegisterValue; if (v != last) { shifts++; last = v; } }
            double expect = 60.0;
            Check($"noise rate {control}: one shift every {divisor} CPU clocks", Math.Abs(shifts - expect) <= 2, $"{shifts} shifts in 60 periods");
        }
        {
            var q = new Sn76489(Clock, Rate);
            Write(q, Tone(2, 40)); Write(q, 0xE3, Vol(3, 0));
            int shifts = 0, last = q.NoiseRegisterValue;
            for (int i = 0; i < 60 * 40 * 32 / 4; i++) { q.Run(4); int v = q.NoiseRegisterValue; if (v != last) { shifts++; last = v; } }
            Check("noise rate 3 follows tone channel 3 (period 40: one shift every 2 x 40 PSG clocks)", Math.Abs(shifts - 60) <= 3, $"{shifts} shifts expected about 60");
        }

        // Game Gear stereo (port $06): left and right enables per channel
        {
            var q = new Sn76489(Clock, Rate);
            Write(q, Tone(0, 254)); Write(q, Vol(0, 0));
            q.WriteStereo(0x10);   // tone 1 on the left only
            var (l, r) = Capture(q, 0.8);
            Check("stereo $10: tone 1 on the left, nothing on the right", Rms(l, (int)(0.3 * Rate)) > 0.05 && Rms(r, (int)(0.3 * Rate)) < 0.002, $"L {Rms(l, (int)(0.3 * Rate)):0.000} R {Rms(r, (int)(0.3 * Rate)):0.000}");
            q.WriteStereo(0x01);
            (l, r) = Capture(q, 0.8);
            Check("stereo $01: tone 1 on the right, nothing on the left", Rms(r, (int)(0.3 * Rate)) > 0.05 && Rms(l, (int)(0.3 * Rate)) < 0.002);
            var mono = new Sn76489(Clock, Rate);
            Write(mono, Tone(0, 254)); Write(mono, Vol(0, 0));
            var (ml, mr) = Capture(mono, 0.5);
            Check("power-on stereo ($FF): both sides identical", mono.StereoMask == 0xFF && ml.SequenceEqual(mr));
        }

        // state: registers and counters survive a save and load
        {
            var a = new Sn76489(Clock, Rate);
            Write(a, Tone(0, 200)); Write(a, Tone(1, 333)); Write(a, 0xE5, Vol(0, 2), Vol(1, 4), Vol(3, 6)); a.Run(123_457);
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) a.SaveState(w);
            ms.Position = 0;
            var b = new Sn76489(Clock, Rate);
            using (var r = new BinaryReader(ms)) b.LoadState(r);
            bool same = true;
            for (int i = 0; i < 2000; i++)
            {
                a.Run(97); b.Run(97);
                same &= Enumerable.Range(0, 4).All(c => a.ChannelLevel(c) == b.ChannelLevel(c)) && a.NoiseRegisterValue == b.NoiseRegisterValue;
            }
            Check("a restored chip runs on exactly like the original (levels and noise register match for 2,000 steps)", same);
        }

        // band-limiting: a 10.2 kHz square wave (period 11) aliases its 3rd harmonic (30.5 kHz) down to 13.6 kHz when sampled naively; the band-limited steps must keep that low
        {
            var q = new Sn76489(Clock, Rate);
            Write(q, Tone(0, 11)); Write(q, Vol(0, 0));
            var (l, _) = Capture(q, 1.0);
            int from = (int)(0.3 * Rate), to = (int)(0.9 * Rate);
            double f0 = Hz(11), fund = Goertzel(l, from, to, f0), alias = Goertzel(l, from, to, Rate - 3 * f0);
            Check("aliasing is suppressed (alias of the 3rd harmonic is under 12% of the fundamental; a naive sampler gives about 33%)", fund > 0.05 && alias / fund < 0.12, $"fundamental {fund:0.0000} at {f0:0} Hz, alias {alias:0.0000} at {Rate - 3 * f0:0} Hz, ratio {alias / fund:0.000}");
        }

        // output rate: one NTSC frame (59,736 CPU clocks) makes about 735 samples
        {
            var q = new Sn76489(Clock, Rate);
            var buf = new short[4096]; int total = 0;
            for (int f = 0; f < 600; f++) { q.Run(59_736); total += q.ReadSamples(buf) / 2; }
            double expect = 600 * 59_736 / Clock * Rate;
            Check("about 735 samples per frame at 44.1 kHz", Math.Abs(total - expect) <= 20, $"{total} frames of audio in 600 frames, expected {expect:0}");
        }
    }
}
