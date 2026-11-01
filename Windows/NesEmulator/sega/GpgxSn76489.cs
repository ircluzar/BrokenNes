using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  Genesis Plus GX 1.3.0 PSG (Maxim's SN76489, as modified for SMS Plus and Genesis Plus), ported to C#.
//
//  Source: Genesis Plus GX 1.3.0 (Eke-Eke, 2008-12-14), tree at commit 6f65ac3, source/sound/sn76489.c + sn76489.h,
//  https://raw.githubusercontent.com/ekeeke/Genesis-Plus-GX/6f65ac3/source/sound/sn76489.c . GX 1.3.0 has no
//  sn76496.c: its PSG is this file. Configured as sound.c's sound_init() did: MUTE_ALLON, VOL_FULL, FB_SEGAVDP (0x0009),
//  SRW_SEGAVDP (16 bits), BoostNoise off, rendered directly at the output rate (system.c then applied psg_preamp 150%).
//  Copyright (C) 2001-2002 Maxim; 2004 Charles MacDonald; 2007-2008 Eke-Eke. License: the SN76489 core's terms within
//  Genesis Plus / SMS Plus (Charles MacDonald, non-commercial), the same code that shipped alongside MAME-licensed fm.c.
//  This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and
//  docs/projects/foreign-apu-project.md.
//
//  Quirks kept:
//   - float clock and "super-high quality" fractional edges (IntermediatePos, with FLT_MIN as the "unused" marker)
//   - tone periods 0 and 1 are a stuck +1 output (PSG_CUTOFF 0x1): a DC level at the channel's volume
//   - the VGM plugin's volume table (VOL_FULL: 1516 ... 60, 0), bipolar squares, noise unipolar (0/level)
//   - the noise register is reset to 0x8000 on every noise-register write; noise mode 3 copies tone 2's counter
//   - mono: the Game Gear stereo byte is stored but never used by Update
// =====================================================================================================================

/// <summary>Genesis Plus GX 1.3.0's SN76489 (Maxim's core): feed it PSG bytes, render mono 16-bit samples at any rate.</summary>
public sealed class GpgxSn76489
{
    private const int NoiseInitialState = 0x8000, PSG_CUTOFF = 0x1;
    private const float FLT_MIN = 1.17549435e-38f;
    private const int FB_SEGAVDP = 0x0009, SRW_SEGAVDP = 16;
    private static readonly int[] PSGVolumeValues = { 1516, 1205, 957, 760, 603, 479, 381, 303, 240, 191, 152, 120, 96, 76, 60, 0 };   // VOL_FULL

    private readonly int Mute = 15;
    private readonly int WhiteNoiseFeedback = FB_SEGAVDP, SRWidth = SRW_SEGAVDP;
    private readonly bool BoostNoise = false;
    private float Clock;
    private readonly float dClock;
    private int PSGStereo, NumClocksForSample;
    private readonly int[] Registers = new int[8];
    private int LatchedRegister, NoiseShiftRegister, NoiseFreq;
    private readonly int[] ToneFreqVals = new int[4], ToneFreqPos = new int[4], Channels = new int[4];
    private readonly float[] IntermediatePos = new float[4];

    public GpgxSn76489(double psgClockHz, int samplingRate)
    {
        dClock = (float)psgClockHz / 16.0f / samplingRate;
        Reset();
    }

    public void Reset()
    {
        PSGStereo = 0xFF;
        for (int i = 0; i <= 3; i++)
        {
            Registers[2 * i] = 1;
            Registers[2 * i + 1] = 0xf;
            NoiseFreq = 0x10;
            ToneFreqVals[i] = 0;
            ToneFreqPos[i] = 1;
            IntermediatePos[i] = FLT_MIN;
        }
        LatchedRegister = 0;
        NoiseShiftRegister = NoiseInitialState;
        Clock = 0;
    }

    public void Write(int data)
    {
        if ((data & 0x80) != 0)
        {
            LatchedRegister = (data >> 4) & 0x07;
            Registers[LatchedRegister] = (Registers[LatchedRegister] & 0x3f0) | (data & 0xf);
        }
        else
        {
            if ((LatchedRegister % 2) == 0 && LatchedRegister < 5)
                Registers[LatchedRegister] = (Registers[LatchedRegister] & 0x00f) | ((data & 0x3f) << 4);
            else
                Registers[LatchedRegister] = data & 0x0f;
        }
        switch (LatchedRegister)
        {
            case 0: case 2: case 4:
                if (Registers[LatchedRegister] == 0) Registers[LatchedRegister] = 1;
                break;
            case 6:
                NoiseShiftRegister = NoiseInitialState;
                NoiseFreq = 0x10 << (Registers[6] & 0x3);
                break;
        }
    }

    public void GGStereoWrite(int data) => PSGStereo = data;   // stored, never used (as in GX 1.3.0)

    /// <summary>SN76489_Update for one sample.</summary>
    public short Next()
    {
        int i;
        for (i = 0; i <= 2; ++i)
            if (IntermediatePos[i] != FLT_MIN)
                Channels[i] = (short)((Mute >> i & 0x1) * PSGVolumeValues[Registers[2 * i + 1]] * IntermediatePos[i]);
            else
                Channels[i] = (Mute >> i & 0x1) * PSGVolumeValues[Registers[2 * i + 1]] * ToneFreqPos[i];
        Channels[3] = (short)((Mute >> 3 & 0x1) * PSGVolumeValues[Registers[7]] * (NoiseShiftRegister & 0x1));
        if (BoostNoise) Channels[3] <<= 1;
        short outp = 0;
        for (i = 0; i <= 3; ++i) outp += (short)Channels[i];

        Clock += dClock;
        NumClocksForSample = (int)Clock;
        Clock -= NumClocksForSample;

        for (i = 0; i <= 2; ++i) ToneFreqVals[i] -= NumClocksForSample;
        if (NoiseFreq == 0x80) ToneFreqVals[3] = ToneFreqVals[2];
        else ToneFreqVals[3] -= NumClocksForSample;

        for (i = 0; i <= 2; ++i)
        {
            if (ToneFreqVals[i] <= 0)
            {
                if (Registers[i * 2] > PSG_CUTOFF)
                {
                    IntermediatePos[i] = (NumClocksForSample - Clock + 2 * ToneFreqVals[i]) * ToneFreqPos[i] / (NumClocksForSample + Clock);
                    ToneFreqPos[i] = -ToneFreqPos[i];
                }
                else
                {
                    ToneFreqPos[i] = 1;
                    IntermediatePos[i] = FLT_MIN;
                }
                ToneFreqVals[i] += Registers[i * 2] * (NumClocksForSample / Registers[i * 2] + 1);
            }
            else IntermediatePos[i] = FLT_MIN;
        }

        if (ToneFreqVals[3] <= 0)
        {
            ToneFreqPos[3] = -ToneFreqPos[3];
            if (NoiseFreq != 0x80) ToneFreqVals[3] += NoiseFreq * (NumClocksForSample / NoiseFreq + 1);
            if (ToneFreqPos[3] == 1)
            {
                int Feedback;
                if ((Registers[6] & 0x4) != 0)
                {
                    int t = NoiseShiftRegister & WhiteNoiseFeedback;
                    Feedback = (t != 0 && (t ^ WhiteNoiseFeedback) != 0) ? 1 : 0;   // case 0x0009
                }
                else Feedback = NoiseShiftRegister & 1;
                NoiseShiftRegister = (NoiseShiftRegister >> 1) | (Feedback << (SRWidth - 1));
            }
        }
        return outp;
    }

    /// <summary>Re-feeds the stock PSG's registers (after a reset, a state load or a swap).</summary>
    public void ReplayFrom(Sn76489 psg)
    {
        for (int c = 0; c < 3; c++)
        {
            int p = psg.Period(c);
            Write(0x80 | c << 5 | (p & 0x0f));
            Write((p >> 4) & 0x3f);
            Write(0x90 | c << 5 | (psg.Volume(c) & 0x0f));
        }
        Write(0xe0 | (psg.NoiseControl & 0x07));
        Write(0xf0 | (psg.Volume(3) & 0x0f));
    }
}
