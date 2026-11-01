using System;
using System.Collections.Generic;
using System.IO;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// --sms-render in.vgm out.wav [--unit SMS|SMS-FM|GG|...] [--console sms|gg] [--rate 44100] [--seconds S]: plays a VGM file's SN76489 (and YM2413) writes through a Master System /
/// Game Gear sound unit built by <see cref="SmsSoundFactories"/>, and writes a 16-bit stereo WAV. Added for the foreign APU project's smoke tests (any ISmsSound, the stock ones included).
/// </summary>
internal static class SmsRenderCli
{
    public static int Run(string[] args)
    {
        string? input = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        string? output = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null;
        if (input == null || output == null) { Console.WriteLine("usage: --sms-render in.vgm out.wav [--unit SMS|SMS-FM|GG|<SmsSoundFactories id>] [--console sms|gg] [--rate 44100] [--seconds S]"); return 2; }
        var v = VgmFile.Load(input);
        string unitId = M68kTestCli.Arg(args, "--unit") ?? "SMS-FM";
        var console = (M68kTestCli.Arg(args, "--console") ?? "sms").Equals("gg", StringComparison.OrdinalIgnoreCase) ? ConsoleKind.GameGear : ConsoleKind.MasterSystem;
        int rate = int.TryParse(M68kTestCli.Arg(args, "--rate"), out int r) ? r : 44100;
        double maxSeconds = double.TryParse(M68kTestCli.Arg(args, "--seconds"), out double s) ? s : 60;
        var timeline = SegaTimeline.MasterSystem(SegaRegion.Ntsc, console);
        var machine = new SegaMachine(timeline, GenesisModel.Model1, rate);
        var unit = SmsSoundFactories.Create(unitId, new SmsSoundRequest(machine, console, PsgVariant.Sega));
        unit.Reset();
        if (unit.HasFm) unit.WriteAudioControl(1);   // a VGM has no $F2 write: switch the FM output on (PSG stays on), as a game that found the chip does
        double cpuHz = timeline.MasterHz / timeline.Z80.Divider;
        var pcm = new List<short>();
        var buf = new short[8192];
        long cpuAt = 0;
        void RunTo(long sample)
        {
            long target = (long)(sample * cpuHz / 44100.0);
            while (cpuAt < target) { int step = (int)Math.Min(target - cpuAt, 4096); unit.Run(step); cpuAt += step; int n; while ((n = unit.ReadSamples(buf)) > 0) for (int i = 0; i < n; i++) pcm.Add(buf[i]); }
        }
        long limit = (long)(maxSeconds * 44100);
        int psgW = 0, fmW = 0;
        foreach (var e in v.Events)
        {
            if (e.Sample > limit) break;
            RunTo(e.Sample);
            switch (e.Kind)
            {
                case VgmKind.Psg: unit.WritePsg((byte)e.A); psgW++; break;
                case VgmKind.Opll when unit.HasFm: unit.WriteFmAddress((byte)e.A); unit.WriteFmData((byte)e.B); fmW++; break;
            }
        }
        RunTo(Math.Min(v.EndSample, limit) + 22050);
        WavFile.Write(output, pcm.ToArray(), rate);
        double sum = 0; int peak = 0; foreach (short x in pcm) { sum += (double)x * x; peak = Math.Max(peak, Math.Abs((int)x)); }
        Console.WriteLine($"unit {unitId} ({unit.CoreName}): {psgW} PSG + {fmW} FM writes, {pcm.Count / 2 / (double)rate:F1} s, peak {peak}, rms {(pcm.Count > 0 ? Math.Sqrt(sum / pcm.Count) : 0):F0}; wrote {output}");
        return 0;
    }
}
