using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Corruption;

namespace NesEmulator.Debugging;

/// <summary>
/// What the sound monitor shows (BrokenDev D8): one line per channel of the running console's sound chip, read without side effects.
/// NES: APU_FIX's phase probe (duty, period, frequency, this cycle's output of the pulses, triangle, noise and DMC; another APU core says
/// it has no probe). SNES / WideMess: the S-DSP's eight voices (volume, pitch, sample, envelope, output) and KON / ENDX. Game Boy: NR52's
/// channel switches and each channel's readable registers, the wave RAM.
/// </summary>
public static class DebugSound
{
    public sealed class Channel
    {
        public string Name = "";
        public string Text = "";
        /// <summary>0..1, this moment's level (an envelope or output), or -1 when the chip does not show one.</summary>
        public double Level = -1;
        public bool On;
        public override string ToString() => $"{Name}: {(On ? "on " : "off")} {Text}";
    }

    /// <summary>The sound monitor's mute (one bit a channel, in <see cref="Channels"/>' order) on the SNES / WideMess S-DSP or the Game Boy's
    /// APU_GB: only the mix changes, never the config; 0 puts it back exactly. False (with the reason) where the chip has no such switch.</summary>
    public static bool SetMute(object? game, int mask, out string why)
    {
        switch (game)
        {
            case NesEmulator.Systems.SnesSession s when s.Spc is { } spc:
                spc.Dsp.DebugMute = (byte)mask; why = ""; return true;
            case NesEmulator.Systems.GbSession g when g.Board.Apu is NesEmulator.Gb.APU_GB apu:
                apu.DebugMute = mask & 15; why = ""; return true;
            case NES:
                why = "the NES's channels are muted in Sound > Sound Channels"; return false;
            default:
                why = "this sound chip has no mute the monitor can use"; return false;
        }
    }

    /// <summary>The mute now in force (0: none).</summary>
    public static int Mute(object? game) => game switch
    {
        NesEmulator.Systems.SnesSession s when s.Spc is { } spc => spc.Dsp.DebugMute,
        NesEmulator.Systems.GbSession g when g.Board.Apu is NesEmulator.Gb.APU_GB apu => apu.DebugMute,
        _ => 0,
    };

    public static List<Channel> Channels(DebugMachine m, object? game, out string what)
    {
        var list = new List<Channel>();
        var doms = m.Domains();
        IMemoryDomain? Find(string id) => doms.FirstOrDefault(x => x.Id == id && x.Unavailable == null && x.Size > 0);
        switch (DebugPictures.Family(m.Console))
        {
            case "nes":
            {
                if ((game as NES)?.DebugBus?.ActiveAPU is not APU_FIX fix) { what = "this APU core has no probe (APU_FIX, the default, has)"; return list; }
                var p = fix.ProbeApuPhase();
                const double cpu = 1789773.0;
                static string Hz(double f) => f >= 1 ? $"{f,8:F1} Hz" : "       - Hz";
                list.Add(new Channel { Name = "Pulse 1", On = p.Pulse1Output > 0, Level = p.Pulse1Output / 15.0, Text = $"duty {p.Pulse1Duty} period ${p.Pulse1Period:X3} {Hz(p.Pulse1Period >= 8 ? cpu / (16.0 * (p.Pulse1Period + 1)) : 0)} out {p.Pulse1Output,2}" });
                list.Add(new Channel { Name = "Pulse 2", On = p.Pulse2Output > 0, Level = p.Pulse2Output / 15.0, Text = $"duty {p.Pulse2Duty} period ${p.Pulse2Period:X3} {Hz(p.Pulse2Period >= 8 ? cpu / (16.0 * (p.Pulse2Period + 1)) : 0)} out {p.Pulse2Output,2}" });
                list.Add(new Channel { Name = "Triangle", On = p.TriangleOutput > 0, Level = p.TriangleOutput / 15.0, Text = $"step {p.TriangleSeqIndex,2} period ${p.TrianglePeriod:X3} {Hz(p.TrianglePeriod >= 2 ? cpu / (32.0 * (p.TrianglePeriod + 1)) : 0)} out {p.TriangleOutput,2}" });
                list.Add(new Channel { Name = "Noise", On = p.NoiseOutput > 0, Level = p.NoiseOutput / 15.0, Text = $"{(p.NoiseMode ? "short" : "long ")} period {p.NoisePeriod,4} lfsr ${p.NoiseShift:X4} out {p.NoiseOutput,2}" });
                list.Add(new Channel { Name = "DMC", On = !p.DmcSilence, Level = p.DmcOutput / 127.0, Text = $"level {p.DmcOutput,3} bits left {p.DmcBitsRemaining}" });
                what = "the APU (APU_FIX's phase probe; outputs are this cycle's)";
                return list;
            }
            case "snes":
            {
                var dsp = Find("DSP");
                if (dsp == null) { what = "no S-DSP here (the sound unit is not the SNES's own)"; return list; }
                int kon = dsp.Peek(0x4C), endx = dsp.Peek(0x7C);
                for (int v = 0; v < 8; v++)
                {
                    int b = v << 4;
                    int vl = (sbyte)dsp.Peek(b), vr = (sbyte)dsp.Peek(b + 1), pitch = dsp.Peek(b + 2) | (dsp.Peek(b + 3) & 0x3F) << 8;
                    int srcn = dsp.Peek(b + 4), envx = dsp.Peek(b + 8), outx = (sbyte)dsp.Peek(b + 9);
                    list.Add(new Channel
                    {
                        Name = $"Voice {v}", On = envx > 0, Level = envx / 127.0,
                        Text = $"vol {vl,4} {vr,4} pitch ${pitch:X4} (x{pitch / 4096.0,5:F2} the sample's rate) srcn ${srcn:X2} envx {envx,3} outx {outx,4}{((endx >> v & 1) != 0 ? " end" : "")}",
                    });
                }
                what = $"the S-DSP's 8 voices (KON ${kon:X2}, ENDX ${endx:X2}, main volume {(sbyte)dsp.Peek(0x0C)} {(sbyte)dsp.Peek(0x1C)})";
                return list;
            }
            case "gb":
            {
                var io = Find("IO");
                if (io == null) { what = "no I/O registers"; return list; }
                int nr52 = io.Peek(0x26);
                string R(int from, int n) => string.Join(" ", Enumerable.Range(from, n).Select(i => io.Peek(i).ToString("X2")));
                list.Add(new Channel { Name = "Pulse 1", On = (nr52 & 1) != 0, Text = $"NR10-14 {R(0x10, 5)}" });
                list.Add(new Channel { Name = "Pulse 2", On = (nr52 & 2) != 0, Text = $"NR21-24 {R(0x16, 4)}" });
                list.Add(new Channel { Name = "Wave", On = (nr52 & 4) != 0, Text = $"NR30-34 {R(0x1A, 5)} wave {R(0x30, 16).Replace(" ", "")}" });
                list.Add(new Channel { Name = "Noise", On = (nr52 & 8) != 0, Text = $"NR41-44 {R(0x20, 4)}" });
                what = $"NR52 ${nr52:X2} ({((nr52 & 0x80) != 0 ? "sound on" : "sound off")}), NR50 ${io.Peek(0x24):X2}, NR51 ${io.Peek(0x25):X2} (registers as the CPU reads them: write-only bits read 1)";
                return list;
            }
            default:
                what = $"no sound monitor for {m.Console} yet";
                return list;
        }
    }
}
