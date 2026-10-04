using System;
using System.Diagnostics;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-sound-bench [--seconds S] [--runs N] [--limit PERCENT]</c>: what the Genesis sound unit costs, in wall-clock time per second of emulated time, as a share of one core. Four workloads, each timed over
/// S emulated seconds (default 20) and repeated N times (default 3, the best run is the one reported: the shared machine only ever adds time): an idle unit (the chip clocking 53 kHz of silence, the resampler, the PSG), six FM channels of four
/// operators each with the LFO on and a note retriggered every few frames, the same with a DAC stream at 8 kHz and three PSG tones, and that on top of a real Z80 program (the Z80 interpreter counts as sound cost too: it
/// belongs to the unit). The unit is driven directly by its master-clock interface, scanline by scanline, with its output drained every frame the way the host does. Exit code 0 when every workload is under the
/// limit (default 8 percent of a core: the milestone's budget), 1 when one is over, 2 on bad usage.
/// </summary>
internal static class MdSoundBenchCli
{
    private const int Line = SegaTiming.GenesisMasterClocksPerLine;

    private sealed class Workload
    {
        public string Name = "";
        public bool Fm, Dac, Psg, Z80, Lfo = true;
    }

    public static int Run(string[] args)
    {
        double seconds = double.TryParse(M68kTestCli.Arg(args, "--seconds"), out double s) ? s : 20;
        int runs = int.TryParse(M68kTestCli.Arg(args, "--runs"), out int r) ? r : 3;
        double limit = double.TryParse(M68kTestCli.Arg(args, "--limit"), out double l) ? l : 8;
        var loads = new[]
        {
            new Workload { Name = "idle (nothing keyed, Z80 in reset)" },
            new Workload { Name = "6 FM channels x 4 operators, LFO on, notes retriggered", Fm = true },
            new Workload { Name = "... plus a DAC stream (8 kHz) and 3 PSG tones", Fm = true, Dac = true, Psg = true },
            new Workload { Name = "... plus a Z80 program running (DAC loop)", Fm = true, Dac = true, Psg = true, Z80 = true },
        };
        Console.WriteLine($"== Genesis sound unit benchmark: {seconds:F0} emulated seconds per run, best of {runs}, one thread, Release");
        bool ok = true;
        foreach (var w in loads)
        {
            double best = double.MaxValue; long samples = 0;
            for (int i = 0; i < runs + 1; i++)                    // the first pass warms the JIT and is not counted
            {
                double t = RunOne(w, i == 0 ? Math.Min(2, seconds) : seconds, out samples);
                if (i > 0) best = Math.Min(best, t);
            }
            double perSecond = best / seconds;
            double percent = perSecond * 100;
            bool pass = percent < limit;
            ok &= pass;
            Console.WriteLine($"   {(pass ? "PASS" : "FAIL")}  {w.Name,-62} {perSecond * 1000,7:F1} ms per emulated second = {percent,5:F2}% of a core ({1 / Math.Max(1e-9, perSecond),6:F0}x real time)");
        }
        Console.WriteLine(ok ? $"== every workload is under {limit:F0}% of a core" : $"== OVER the {limit:F0}% budget");
        return ok ? 0 : 1;
    }

    private static double RunOne(Workload w, double seconds, out long samples)
    {
        var timeline = SegaTimeline.Genesis(SegaRegion.Ntsc);
        var unit = new MdSoundUnit(new SegaMachine(timeline, GenesisModel.Model1, 44100));
        unit.Reset();
        long t = 0;
        void Ym(int part, int reg, int value) { unit.WriteYm(part * 2, (byte)reg, t); unit.WriteYm(part * 2 + 1, (byte)value, t); }
        if (w.Fm)
        {
            // a bright patch (algorithm 4: two carriers each with a modulator) on all six channels, LFO on, panned both sides
            for (int ch = 0; ch < 6; ch++)
            {
                int part = ch / 3, c = ch % 3;
                for (int n = 0; n < 4; n++)
                {
                    int off = new[] { 0, 8, 4, 12 }[n] + c;
                    Ym(part, 0x30 + off, 0x31 + n);
                    Ym(part, 0x40 + off, n >= 2 ? 0x10 : 0x20);
                    Ym(part, 0x50 + off, 0x1F | (n & 1) << 6);
                    Ym(part, 0x60 + off, 0x80 | 0x0A);
                    Ym(part, 0x70 + off, 0x05);
                    Ym(part, 0x80 + off, 0x4A);
                }
                Ym(part, 0xB0 + c, 0x3C);
                Ym(part, 0xB4 + c, 0xC0 | (w.Lfo ? 0x1B : 0));
                Ym(part, 0xA4 + c, 0x22 + ch);
                Ym(part, 0xA0 + c, 0x80 + ch * 9);
            }
            Ym(0, 0x22, 0x08 | 5);
        }
        if (w.Dac) { Ym(0, 0x2B, 0x80); }
        if (w.Psg)
        {
            foreach (byte b in new byte[] { 0x90, 0x8E, 0x0F, 0xB0, 0xA0, 0x07, 0xD1, 0xC0, 0x03 }) unit.WritePsg(b, t);
        }
        if (w.Z80)
        {
            byte[] prog =
            {
                0x21, 0x00, 0x40, 0x36, 0x2B, 0x23, 0x36, 0x80, 0x2B, 0x36, 0x2A, 0x23,
                0x36, 0xFF, 0x06, 0x14, 0x10, 0xFE, 0x36, 0x00, 0x06, 0x14, 0x10, 0xFE, 0x18, 0xF2,
            };
            Array.Copy(prog, unit.Ram, prog.Length);
            unit.WriteReset(true, t);
            unit.WriteBusRequest(false, t);
        }

        var buf = new short[4096];
        long frame = (long)(timeline.MasterHz / 60.0);
        long end = (long)(seconds * timeline.MasterHz);
        long next = 0; int line = 0;
        long drained = 0;
        var sw = Stopwatch.StartNew();
        while (t < end)
        {
            for (long fEnd = t + frame; t < fEnd; t += Line)
            {
                unit.RunTo(t);
                line++;
                if (w.Fm && line % 8 == 0) { Ym(0, 0x28, 0x00 | (line / 8 % 3)); }
                if (w.Fm && line % 8 == 4) { Ym(0, 0x28, 0xF0 | (line / 8 % 3)); }
                if (w.Dac && line % 56 == 0) { Ym(0, 0x2A, (line * 7) & 0xFF); }
                if (w.Psg && line % 400 == 0) { unit.WritePsg((byte)(0x80 | (line / 400 & 1) << 5 | 0x0E), t); unit.WritePsg((byte)(line / 400 & 0x3F), t); }
            }
            next = t;
            unit.RunTo(next);
            int n;
            while ((n = unit.ReadSamples(buf)) > 0) drained += n;
        }
        sw.Stop();
        samples = drained / 2;
        return sw.Elapsed.TotalSeconds;
    }
}
