using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  PicoDrive 1.85 SN76496 (PSG) engine, ported to C#.
//
//  Source: PicoDrive v1.85 by notaz (tag v1.85, https://github.com/notaz/picodrive/tree/v1.85), files
//  pico/sound/sn76496.c + sn76496.h (MAME's sn76496.c of ~2005, trimmed to one chip), driven the way pico/sound/sound.c
//  does it (PsndRerate: SN76496_init(OSC/15, PsndRate); PsndRender / PsndGetSamplesMS: one whole frame per call).
//  https://raw.githubusercontent.com/notaz/picodrive/v1.85/pico/sound/sn76496.c
//  Copyright: Nicola Salmoria and the MAME team (sn76496.c, "MAME license"), Sebastien Chevalier (2005 write fix);
//  PicoDrive (c) Copyright Dave, 2004; (C) notaz, 2006-2009.
//  License: PicoDrive's COPYING (MAME-style, non-commercial: redistributions may not be sold or used in a commercial
//  product; modified redistributions must include complete source). This is a documented exception to the repository's
//  license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port (quick and dirty first). The bugs and shortcuts of the original are kept on purpose; they are the sound:
//   - white-noise feedback 0x14002 (taps 0, 2, 15 of a 17-bit shifter - not the Sega 16-bit taps 0 and 3), periodic
//     noise a 15-bit rotate (0x08000, "JH 981127"), preset 0x0F35; NoiseFB starts at 0 until $E0 is first written
//   - mono, unipolar (0 .. 0x47FF: a DC offset, no negative half-wave), MAX_OUTPUT lowered from 0x7FFF to 0x47FF
//   - volume table 2 dB steps from MAX_OUTPUT/3 (0x17FF per channel at full volume)
//   - period 0 behaves as 1; tone 3 drives noise mode 3 at 2 x its period
//   - fixed native rate 44,100 Hz, fixed clock OSC/15; no Game Gear stereo (PicoDrive 1.85 has no Game Gear)
// =====================================================================================================================

/// <summary>The PicoDrive 1.85 SN76496: feed it the PSG writes, pull 44.1 kHz mono (unipolar) samples.</summary>
public sealed class PicoSn76496
{
    public const int Rate = 44100;
    const int MAX_OUTPUT = 0x47ff;
    const int STEP = 0x10000;
    const int FB_WNOISE = 0x14002;
    const int FB_PNOISE = 0x08000;
    const uint NG_PRESET = 0x0f35;

    readonly uint UpdateStep;
    readonly int[] VolTable = new int[16];
    readonly int[] Register = new int[8];
    int LastRegister;
    readonly int[] Volume = new int[4];
    uint RNG;
    int NoiseFB;
    readonly int[] Period = new int[4];
    readonly int[] Count = new int[4];
    readonly int[] Output = new int[4];

    /// <summary>SN76496_init(clock, 44100).</summary>
    public PicoSn76496(double clock)
    {
        UpdateStep = (uint)((double)STEP * Rate * 16 / (int)clock);
        LastRegister = 0;
        for (int i = 0; i < 8; i += 2) { Register[i] = 0; Register[i + 1] = 0x0f; }
        for (int i = 0; i < 4; i++) { Output[i] = 0; Period[i] = Count[i] = (int)UpdateStep; }
        RNG = NG_PRESET;
        Output[3] = (int)(RNG & 1);
        // SN76496_set_gain(R, 0)
        double o = MAX_OUTPUT / 3;
        for (int i = 0; i < 15; i++)
        {
            VolTable[i] = o > MAX_OUTPUT / 3 ? MAX_OUTPUT / 3 : (int)o;
            o /= 1.258925412;
        }
        VolTable[15] = 0;
    }

    public void Write(int data)
    {
        if ((data & 0x80) != 0)
        {
            int r = (data & 0x70) >> 4, c = r / 2;
            LastRegister = r;
            Register[r] = (Register[r] & 0x3f0) | (data & 0x0f);
            switch (r)
            {
                case 0: case 2: case 4:
                    Period[c] = (int)(UpdateStep * (uint)Register[r]);
                    if (Period[c] == 0) Period[c] = (int)UpdateStep;
                    if (r == 4 && (Register[6] & 0x03) == 0x03) Period[3] = 2 * Period[2];
                    break;
                case 1: case 3: case 5: case 7:
                    Volume[c] = VolTable[data & 0x0f];
                    break;
                case 6: NoiseWrite(); break;
            }
        }
        else
        {
            int r = LastRegister, c = r / 2;
            switch (r)
            {
                case 0: case 2: case 4:
                    Register[r] = (Register[r] & 0x0f) | ((data & 0x3f) << 4);
                    Period[c] = (int)(UpdateStep * (uint)Register[r]);
                    if (Period[c] == 0) Period[c] = (int)UpdateStep;
                    if (r == 4 && (Register[6] & 0x03) == 0x03) Period[3] = 2 * Period[2];
                    break;
                case 1: case 3: case 5: case 7:
                    Volume[c] = VolTable[data & 0x0f];
                    Register[r] = (Register[r] & 0x3f0) | (data & 0x0f);
                    break;
                case 6:
                    Register[r] = (Register[r] & 0x3f0) | (data & 0x0f);
                    NoiseWrite();
                    break;
            }
        }
    }

    void NoiseWrite()
    {
        int n = Register[6];
        NoiseFB = (n & 4) != 0 ? FB_WNOISE : FB_PNOISE;
        n &= 3;
        Period[3] = n == 3 ? 2 * Period[2] : (int)(UpdateStep << (5 + n));
        RNG = NG_PRESET;
        Output[3] = (int)(RNG & 1);
    }

    /// <summary>SN76496Update (mono): adds <paramref name="length"/> samples into <paramref name="buffer"/> from <paramref name="offset"/>.</summary>
    public void Update(int[] buffer, int offset, int length)
    {
        for (int i = 0; i < 4; i++)
            if (Volume[i] == 0 && Count[i] <= length * STEP) Count[i] += length * STEP;

        Span<int> vol = stackalloc int[4];
        while (length > 0)
        {
            int v0, v1, v2, v3;
            vol.Clear();
            for (int i = 0; i < 3; i++)
            {
                if (Output[i] != 0) vol[i] += Count[i];
                Count[i] -= STEP;
                while (Count[i] <= 0)
                {
                    Count[i] += Period[i];
                    if (Count[i] > 0)
                    {
                        Output[i] ^= 1;
                        if (Output[i] != 0) vol[i] += Period[i];
                        break;
                    }
                    Count[i] += Period[i];
                    vol[i] += Period[i];
                }
                if (Output[i] != 0) vol[i] -= Count[i];
            }

            int left = STEP;
            do
            {
                int nextevent = Count[3] < left ? Count[3] : left;
                if (Output[3] != 0) vol[3] += Count[3];
                Count[3] -= nextevent;
                if (Count[3] <= 0)
                {
                    if ((RNG & 1) != 0) RNG ^= (uint)NoiseFB;
                    RNG >>= 1;
                    Output[3] = (int)(RNG & 1);
                    Count[3] += Period[3];
                    if (Output[3] != 0) vol[3] += Period[3];
                }
                if (Output[3] != 0) vol[3] -= Count[3];
                left -= nextevent;
            } while (left > 0);

            v0 = vol[0]; v1 = vol[1]; v2 = vol[2]; v3 = vol[3];
            uint o = (uint)(v0 * Volume[0] + v1 * Volume[1] + v2 * Volume[2] + v3 * Volume[3]);
            if (o > (uint)MAX_OUTPUT * STEP) o = (uint)MAX_OUTPUT * STEP;
            o /= STEP;
            buffer[offset++] += (int)o;
            length--;
        }
    }
}
