using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--ym-render in.vgm out.wav [--model 1|2|3] [--rate 44100] [--loops N] [--seconds S] [--tail S] [--stems dir] [--solo n] [--mute mask] [--no-psg] [--info]</c>: plays a VGM file's YM2612 and SN76489 commands
/// through the real Genesis sound unit (<see cref="MdSoundUnit"/>: the chip, the band-limited resampler, the PSG) and writes a 16-bit stereo WAV at the host rate, then prints what it measured (length, peak, RMS,
/// clipped samples, writes by kind, notes). <c>--ym-render --compare a.wav b.wav</c> measures how close two renders are; <c>--ym-render --compare-logs a.vgm b.vgm</c> compares the notes and the DAC stream two emulators wrote for the same game (see <see cref="VgmCompare"/>). There is no playback device on the machine this was written on, so nothing here is heard: every claim is a measurement of the samples. <c>--stems dir</c> renders each of the six FM
/// channels, the DAC and the PSG on their own (the unit's mute mask) next to the full mix. <c>--solo n</c> keeps only FM channel n (1 to 6; 7 = the DAC; 8 = the PSG), <c>--mute mask</c> silences the FM channels in the mask
/// (bit 0 to 5 = FM 1 to 6, bit 6 = the DAC), <c>--loops</c> plays the file's loop section that many times in all. Exit code 0 = rendered, 2 = usage or file error.
/// </summary>
internal static class YmRenderCli
{
    public sealed class Stats
    {
        public long Samples, Clipped, YmWrites, KeyOns, DacWrites, PsgWrites;
        public int Peak; public double Rms; public double Seconds;
        public readonly long[] KeyOnsByChannel = new long[6];
        public short[] Pcm16 = Array.Empty<short>();
    }

    /// <summary><c>--ym-render --compare a.wav b.wav [--max-lag N]</c>: how close two renders are: for the best sample lag the signal-to-error ratio of the left channel, and the ratio per second at that lag. Used to check a game's own audio against the replay of its recorded VGM.</summary>
    private static int Compare(string[] args)
    {
        if (args.Length < 4) { Console.WriteLine("usage: --ym-render --compare a.wav b.wav [--max-lag N]"); return 2; }
        var a = WavFile.Read(args[2], out int ra); var b = WavFile.Read(args[3], out int rb);
        int maxLag = int.TryParse(M68kTestCli.Arg(args, "--max-lag"), out int ml) ? ml : 64;
        int n = Math.Min(a.Length, b.Length) / 2;
        Console.WriteLine($"   {Path.GetFileName(args[2])}: {a.Length / 2:N0} samples at {ra} Hz; {Path.GetFileName(args[3])}: {b.Length / 2:N0} samples at {rb} Hz");
        double Err(int lag, int from, int to)
        {
            double e = 0;
            for (int i = Math.Max(from, Math.Max(0, lag)); i < Math.Min(to, n + Math.Min(0, lag)); i++) { double d = a[2 * i] - b[2 * (i - lag)]; e += d * d; }
            return e;
        }
        int bestLag = 0; double best = double.MaxValue;
        for (int lag = -maxLag; lag <= maxLag; lag++) { double e = Err(lag, 0, n); if (e < best) { best = e; bestLag = lag; } }
        double pow = 0; for (int i = 0; i < n; i++) pow += (double)a[2 * i] * a[2 * i];
        Console.WriteLine($"   best lag {bestLag} samples: signal-to-error {10 * Math.Log10(Math.Max(1, pow) / Math.Max(1e-9, best)):F1} dB (lag 0: {10 * Math.Log10(Math.Max(1, pow) / Math.Max(1e-9, Err(0, 0, n))):F1} dB)");
        var secs = new List<string>();
        for (int s = 0; s * ra + ra <= n; s++)
        {
            double p = 0; for (int i = s * ra; i < (s + 1) * ra; i++) p += (double)a[2 * i] * a[2 * i];
            double e = Err(bestLag, s * ra, (s + 1) * ra);
            secs.Add(p < 1e3 ? "-" : $"{10 * Math.Log10(p / Math.Max(1e-9, e)):F0}");
        }
        Console.WriteLine("   per second (dB): " + string.Join(" ", secs));
        return 0;
    }

    public static int Run(string[] args)
    {
        if (args.Length > 1 && args[1] == "--compare") return Compare(args);
        if (args.Length > 1 && args[1] == "--compare-logs") return VgmCompare.Run(args);
        string? input = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        string? output = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null;
        if (input == null) { Console.WriteLine("usage: --ym-render in.vgm [out.wav] [--model 1|2|3] [--rate 44100] [--loops N] [--seconds S] [--tail S] [--stems dir] [--solo n] [--mute mask] [--no-psg] [--info]"); return 2; }
        VgmFile v;
        try { v = VgmFile.Load(input); }
        catch (Exception e) { Console.WriteLine($"cannot read {input}: {e.Message}"); return 2; }
        int model = int.TryParse(M68kTestCli.Arg(args, "--model"), out int m) ? m : 1;
        int rate = int.TryParse(M68kTestCli.Arg(args, "--rate"), out int r) ? r : 44100;
        int loops = int.TryParse(M68kTestCli.Arg(args, "--loops"), out int l) ? l : 1;
        double maxSeconds = double.TryParse(M68kTestCli.Arg(args, "--seconds"), out double sec) ? sec : double.PositiveInfinity;
        double tail = double.TryParse(M68kTestCli.Arg(args, "--tail"), out double tl) ? tl : 0.5;
        int mute = int.TryParse(M68kTestCli.Arg(args, "--mute"), out int mk) ? mk : 0;
        bool psg = !M68kTestCli.Flag(args, "--no-psg");
        int solo = int.TryParse(M68kTestCli.Arg(args, "--solo"), out int sl) ? sl : 0;
        string? stems = M68kTestCli.Arg(args, "--stems");
        var gm = model switch { 2 => GenesisModel.Model2, 3 => GenesisModel.Model3, _ => GenesisModel.Model1 };

        Console.WriteLine($"== {Path.GetFileName(input)}: VGM version {v.Version >> 8}.{v.Version & 0xFF:X2}, YM2612 clock {v.Ym2612Clock:N0}, SN76489 clock {v.Sn76489Clock:N0}, {v.TotalSamples / 44100.0:F1} s, {v.Events.Count:N0} writes{(v.LoopEvent >= 0 ? $", loops from {v.LoopStartSample / 44100.0:F1} s" : "")}{(v.Gd3Title.Length > 0 ? $", \"{v.Gd3Title}\"" : "")}");
        if (v.Ym2612Clock == 0) Console.WriteLine("   WARNING: the file has no YM2612 clock (it is not a Genesis recording); its YM2612 writes, if any, are played anyway");
        if (v.Ym2413Writes > 0 || v.UnsupportedCommands > 0) Console.WriteLine($"   not played: {v.Ym2413Writes:N0} YM2413 writes, {v.UnsupportedCommands:N0} commands for other chips");
        if (M68kTestCli.Flag(args, "--info")) return 0;
        if (output == null && stems == null) { Console.WriteLine("no output file given (use out.wav or --stems dir)"); return 2; }

        int fmMute = mute;
        bool usePsg = psg;
        if (solo >= 1 && solo <= 6) { fmMute = 0x7F & ~(1 << (solo - 1)); usePsg = false; }
        else if (solo == 7) { fmMute = 0x3F; usePsg = false; }
        else if (solo == 8) { fmMute = 0x7F; }

        double skew = double.TryParse(M68kTestCli.Arg(args, "--skew-us"), out double sk) ? sk : 0;       // shifts every write by a fixed time: for measuring how much of a difference between two renders is only timing
        var main = Render(v, gm, rate, loops, maxSeconds, tail, fmMute, usePsg, skew);
        Describe("all voices", main);
        if (output != null) { WavFile.Write(output, main.Pcm16, rate); Console.WriteLine($"   wrote {output}"); }
        if (stems != null)
        {
            Directory.CreateDirectory(stems);
            string baseName = Path.GetFileNameWithoutExtension(input);
            for (int voice = 1; voice <= 8; voice++)
            {
                int vm = voice <= 6 ? 0x7F & ~(1 << (voice - 1)) : voice == 7 ? 0x3F : 0x7F;
                var st = Render(v, gm, rate, loops, maxSeconds, tail, vm, voice == 8);
                string name = voice <= 6 ? $"fm{voice}" : voice == 7 ? "dac" : "psg";
                string path = Path.Combine(stems, $"{baseName}_{name}.wav");
                WavFile.Write(path, st.Pcm16, rate);
                Describe(name, st);
            }
            Console.WriteLine($"   stems in {stems}");
        }
        return 0;
    }

    private static void Describe(string what, Stats s) =>
        Console.WriteLine($"   {what,-10}: {s.Seconds:F1} s, peak {s.Peak} ({20 * Math.Log10(Math.Max(1, s.Peak) / 32768.0):F1} dBFS), RMS {s.Rms:F0} ({20 * Math.Log10(Math.Max(1e-9, s.Rms) / 32768.0):F1} dBFS), {s.Clipped:N0} clipped samples; writes: YM {s.YmWrites:N0} (key-ons {s.KeyOns:N0}: {string.Join(" ", s.KeyOnsByChannel)}, DAC {s.DacWrites:N0}), PSG {s.PsgWrites:N0}");

    /// <summary>Plays the file through a fresh unit and returns the audio and what was written.</summary>
    internal static Stats Render(VgmFile v, GenesisModel model, int rate, int loops, double maxSeconds, double tail, int fmMute, bool psg, double skewMicroseconds = 0)
    {
        double ymClock = v.Ym2612Clock > 0 ? v.Ym2612Clock : 7_670_453;
        var region = Math.Abs(ymClock - 7_600_489) < Math.Abs(ymClock - 7_670_453) ? SegaRegion.Pal : SegaRegion.Ntsc;
        var timeline = SegaTimeline.Genesis(region);
        var unit = new MdSoundUnit(new SegaMachine(timeline, model, rate)) { FmMuteMask = fmMute };
        unit.Reset();
        var st = new Stats();
        var pcm = new List<short>();
        var buf = new short[8192];
        void Drain() { int n; while ((n = unit.ReadSamples(buf)) > 0) for (int i = 0; i < n; i++) pcm.Add(buf[i]); }
        long Master(long sample) => (long)(sample * timeline.MasterHz / 44100.0 + skewMicroseconds * 1e-6 * timeline.MasterHz);

        long passLength = v.EndSample;
        long limit = double.IsInfinity(maxSeconds) ? long.MaxValue : (long)(maxSeconds * 44100);
        long offset = 0;
        long lastWrittenAt = 0;
        for (int pass = 0; pass < Math.Max(1, loops); pass++)
        {
            int first = pass == 0 || v.LoopEvent < 0 ? 0 : v.LoopEvent;
            long baseSample = pass == 0 || v.LoopEvent < 0 ? 0 : v.LoopStartSample;
            if (pass > 0 && v.LoopEvent < 0) break;                    // nothing to loop
            for (int i = first; i < v.Events.Count; i++)
            {
                var e = v.Events[i];
                long sample = offset + e.Sample - baseSample;
                if (sample > limit) { passLength = limit; break; }
                long master = Master(sample);
                lastWrittenAt = Math.Max(lastWrittenAt, master);
                switch (e.Kind)
                {
                    case VgmKind.Ym0: case VgmKind.Ym1:
                    {
                        int part = e.Part();
                        unit.WriteYm(part * 2, (byte)e.A, master);
                        unit.WriteYm(part * 2 + 1, (byte)e.B, master);
                        st.YmWrites++;
                        if (part == 0 && e.A == 0x28 && (e.B & 0xF0) != 0) { int ch = (e.B & 3) + ((e.B & 4) != 0 ? 3 : 0); if ((e.B & 3) != 3) { st.KeyOns++; st.KeyOnsByChannel[ch]++; } }
                        if (part == 0 && e.A == 0x2A) st.DacWrites++;
                        break;
                    }
                    case VgmKind.Psg:
                        if (psg) { unit.WritePsg((byte)e.A, master); st.PsgWrites++; }
                        break;
                }
                if ((i & 63) == 0) Drain();
            }
            offset += (pass == 0 ? v.EndSample : v.EndSample - v.LoopStartSample);
            if (offset > limit) break;
        }
        long endSample = Math.Min(offset, limit);
        long endMaster = Master(endSample) + (long)(tail * timeline.MasterHz);
        unit.RunTo(Math.Max(endMaster, lastWrittenAt));
        Drain();
        st.Pcm16 = pcm.ToArray();
        st.Samples = st.Pcm16.Length / 2;
        double sum = 0;
        foreach (short s in st.Pcm16) { int a = Math.Abs((int)s); if (a > st.Peak) st.Peak = a; if (a >= 32767) st.Clipped++; sum += (double)s * s; }
        st.Rms = st.Pcm16.Length == 0 ? 0 : Math.Sqrt(sum / st.Pcm16.Length);
        st.Seconds = st.Samples / (double)rate;
        return st;
    }
}
