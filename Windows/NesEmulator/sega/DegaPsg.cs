// Dega 1.16 - "Dave's PSG Sound emulator" with its Enhanced PSG mode (sampled instruments + echo), quick port.
// Source project: Dega 1.16 (Sega Master System / Game Gear emulator), https://github.com/Plombo/dega (master, fetched 2026-11-01)
// Files ported: mast/dpsg.cpp (whole), mast/samp.cpp (whole), mast/samples.h (as data: DegaSamples.cs), the bits of mast/snd.cpp
//   that drive it (MsndInit: SampInit then DpsgInit at the frontend's rate; MsndRefresh's register re-feed; MsndTo's clip to 16 bits),
//   struct Dpsg / struct Sample from mast/mastint.h.
// Copyright: Dave (the FinalBurn author); later maintainers of the Dega tree.
// License (Dega README, verbatim): "You can use this software freely as long as you don't use it commercially."
//   and "I do not accept responsibility for any effects, adverse or otherwise, that this code may have on you, your computer,
//   your sanity, your dog, and anything else that you can think of. Use it at your own risk."  (non-commercial terms)
// This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
// Quirks kept (all of them are the sound):
//  - Enhanced mode: tone channel 0 plays DspSample, channel 1 MoogSample (8-bit sampled instruments, looped), channel 2 a +-0x14 square;
//    periods >= 0x9f0 use the plain square (After Burner speech); periods <= 4 output DC at the channel volume (speech / Phantasy Star).
//  - A volume rise of more than 2 steps restarts the sample from 0 ("Louder: it's likely this is a new note").
//  - Echo: channel 0 gets (ch0+ch1)/2 from 4095 samples ago added back (feedback), then ch0 and ch1 are scaled by 3/4.
//  - The two-byte tone write is detected with the LAST byte written: a tone latch followed by any other latch byte ORs that byte << 4
//    into the first channel's period (no bit-7 check).
//  - Volume table "based on Maxim's real SMS experiments" (RealVol << 4); the instrument samples are hot (up to ~0x7f00 per channel) and
//    the sum is hard-clipped to 16 bits.
//  - Noise: 16-bit shifter, white taps 0x12000-ish (bit0 into bit16, bit3 into bit16), seed 1 reloaded on a noise write; the noise steps
//    by DpsgInc/Period like the tones (Dega's own rate, not the chip's).
//  - Game Gear stereo: a channel switched off on one side is NOT muted, it plays at 1/4 there.
//  - The PSG clock is Dega's fixed 3,579,540 Hz (also on PAL / Game Gear); the rate is the frontend's (44,100 Hz assumed).
using System;

namespace NesEmulator.Sega;

internal sealed class DegaPsg
{
    private sealed class Sample
    {
        public byte[] Data = Array.Empty<byte>();
        public int Len, PeriodCount, LoopDist;
    }

    private sealed class Chan
    {
        public Sample? Sam; // the sample we are playing for this channel
        public int Pos, Add; // position in the sample and add value (10-bit fraction)
        public short Vol; // real volume
    }

    // struct Dpsg (mastint.h)
    private readonly byte[] volume = new byte[4];   // 0-15 (15 = loudest)
    private readonly ushort[] period = new ushort[4];
    private byte stereo; // inverted GG stereo byte
    private byte last;   // last byte sent

    private readonly Chan[] chan = { new(), new(), new(), new() };
    private uint noise;
    private const int DpsgClock = 3579540;
    private readonly int dpsgInc;
    public readonly bool Enhance;

    private static readonly short[] RealVol =
    {
        0x000, 0x011, 0x017, 0x01d, 0x028, 0x031, 0x03a, 0x04b,
        0x060, 0x074, 0x094, 0x0bd, 0x0e0, 0x0fc, 0x0ff, 0x100,
    };

    private readonly int[] echo = new int[0x1000];
    private const int EchoMask = 0x0fff;
    private int echoPos;

    // samp.cpp
    private static readonly byte[] Square = MakeSquare();
    private readonly Sample[] samples = new Sample[5];

    private static byte[] MakeSquare()
    {
        var s = new byte[0x80];
        for (int i = 0; i < s.Length; i++) { int p = 0x80; if (i < s.Length >> 1) p -= 0x14; else p += 0x14; s[i] = (byte)p; }
        return s;
    }

    public DegaPsg(int rate, bool enhance = true)
    {
        Enhance = enhance;
        // SampInit
        for (int i = 0; i < samples.Length; i++) samples[i] = new Sample { Data = Square, Len = Square.Length, PeriodCount = 1, LoopDist = Square.Length };
        if (Enhance)
        {
            var d = DegaSamples.DspSample;
            samples[1] = new Sample { Data = d, Len = d.Length, PeriodCount = 292, LoopDist = d.Length >> 1 };
            var m = DegaSamples.MoogSample;
            samples[2] = new Sample { Data = m, Len = m.Length, PeriodCount = 33 * 2, LoopDist = 672 };
        }
        // DpsgInit
        noise = 1;
        dpsgInc = rate > 0 ? (DpsgClock << 5) / rate : 0;
        Recalc();
    }

    private void RecalcVol(int c)
    {
        var pc = chan[c];
        pc.Vol = (short)(RealVol[volume[c] & 15] << 4);
    }

    private void RecalcAdd(int c)
    {
        var pc = chan[c];
        if (period[c] <= 0) return;
        if (pc.Sam == null)
        {
            // start a new sample
            pc.Sam = period[c] >= 0x9f0 ? samples[0] : samples[1 + c];
            pc.Pos = 0;
        }
        if (pc.Sam.PeriodCount > 0)
            pc.Add = pc.Sam.Len * dpsgInc / period[c] / pc.Sam.PeriodCount;
    }

    public void Recalc() { for (int c = 0; c < 4; c++) { RecalcAdd(c); RecalcVol(c); } }

    public void Write(byte d)
    {
        if ((last & 0x90) == 0x80)
        {
            int c = last >> 5 & 3;
            period[c] &= 0x00f; period[c] |= (ushort)(d << 4);
            RecalcAdd(c);
        }
        if ((d & 0x90) == 0x80)
        {
            int c = d >> 5 & 3;
            period[c] &= 0xff0; period[c] |= (ushort)(d & 15);
            RecalcAdd(c);
            if (c == 3) noise = 1;
        }
        if ((d & 0x90) == 0x90)
        {
            int c = d >> 5 & 3; byte v = (byte)(~d & 15);
            if (v > volume[c] + 2) { chan[c].Sam = null; RecalcAdd(c); } // louder: it's likely this is a new note
            volume[c] = v;
            RecalcVol(c);
        }
        last = d;
    }

    public void WriteStereo(byte d) => stereo = (byte)~d;

    private void UpdateNoise()
    {
        int add, type = period[3];
        if ((type & 3) == 3) add = period[2] > 0 ? dpsgInc / period[2] : dpsgInc << 3;
        else add = dpsgInc << (3 - (type & 3));
        chan[3].Pos += add;
        while (chan[3].Pos >= 0x400)
        {
            noise ^= (noise & 1) << 16;
            if ((type & 4) != 0) noise ^= (noise & 8) << 13;
            noise >>= 1;
            chan[3].Pos -= 0x400;
        }
    }

    private readonly int[] output = new int[4];

    /// <summary>DpsgCalc + MsndTo's clip: one stereo sample.</summary>
    public void Calc(out short left, out short right)
    {
        var o = output; o[0] = o[1] = o[2] = o[3] = 0;
        for (int c = 0; c < 3; c++)
        {
            var ch = chan[c];
            if (ch.Vol <= 0) continue;
            if (period[c] <= 4) { o[c] = ch.Vol; continue; } // speech/pstar
            var ps = ch.Sam; if (ps == null) continue;
            int p;
            while (true)
            {
                p = ch.Pos >> 10;
                if (p < ps.Len) break;
                ch.Pos -= ps.LoopDist << 10;
                ch.Pos &= 0x7ffffc00; // stop aliasing on square wave
            }
            if (p >= 0) { int v = ps.Data[p] - 0x80; o[c] = ch.Vol * v >> 4; }
            ch.Pos += ch.Add;
        }
        o[3] = (noise & 1) != 0 ? chan[3].Vol : -chan[3].Vol;
        UpdateNoise();

        if (Enhance)
        {
            o[0] += echo[(echoPos + 1) & EchoMask];
            o[0] *= 3; o[0] >>= 2;
            o[1] *= 3; o[1] >>= 2;
            echo[echoPos] = (o[0] + o[1]) >> 1;
            echoPos = (echoPos + 1) & EchoMask;
        }

        int l = 0, r = 0, st = stereo;
        for (int c = 0; c < 4; c++, st >>= 1)
        {
            int p = o[c];
            switch (st & 0x11)
            {
                default: l += p; r += p; break;
                case 0x01: l += p; r += p >> 2; break;
                case 0x10: l += p >> 2; r += p; break;
                case 0x11: l += p >> 2; r += p >> 2; break;
            }
        }
        left = (short)Math.Clamp(l, -0x8000, 0x7fff);
        right = (short)Math.Clamp(r, -0x8000, 0x7fff);
    }
}
