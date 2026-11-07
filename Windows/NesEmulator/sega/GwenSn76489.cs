using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  Gwenesis (retro-go) PSG: Maxim's SN76489 as cut down by bzhxx "for gwenesis to fit on MCU", ported to C#.
//
//  Source: retro-go, commit a0f857bf5a2f078f9f621355650c65cb96191eef, gwenesis/components/gwenesis/src/sound/
//  gwenesis_sn76489.c + gwenesis_sn76489.h, https://github.com/ducalex/retro-go/tree/a0f857bf5a2f078f9f621355650c65cb96191eef/gwenesis/components/gwenesis/src/sound
//  (upstream gwenesis by bzhxx, https://github.com/bzhxx/gwenesis).
//  Authors: Maxim (2001-2002), Charles MacDonald (SMS Plus changes, 2004), bzhxx (gwenesis simplifications).
//  Licence: AGPL-3.0 (the gwenesis tree's LICENSE). This is a documented exception to the repository's license
//  policy - see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md.
//
//  Characteristic flaws kept on purpose (this is an art instrument; nothing here is "fixed"):
//   - "few simplication for gwenesis to fit on MCU": no mute mask, no stereo, no SR width / feedback config, no
//     BoostNoise switch - the noise is always doubled ("to make some people happy")
//   - the 16-step integer volume table: 3100, -2dB, -4dB, then each triple halved (/2, /4, /8, /16) - a coarse
//     6 dB ladder instead of 2 dB steps, last entry 0
//   - WhiteNoiseFeedback is never initialised (static context, Init does not set it): it stays 0, so white noise
//     feeds back 0 - after a noise-register write the one 0x8000 bit walks out in 16 shifts and the channel goes
//     silent; only periodic noise keeps sounding
//   - PSG_CUTOFF 0x6: tone periods 1-6 are a stuck +1 output (a DC level at the channel's volume)
//   - IntermediatePos is a fixed-point (x65536) long with LONG_MIN (32-bit long on the MCU) as the "unused" mark;
//     the float clock and fraction maths are single precision
//   - INT16 counters (ToneFreqVals, NoiseFreq), INT8 flip-flops, INT16 output accumulation (wraps)
//   - the noise register is reset to 0x8000 on every noise-register write; noise mode 3 copies tone 2's counter
// =====================================================================================================================

/// <summary>Gwenesis's SN76489 (retro-go): feed it PSG bytes, render mono 16-bit samples at any rate.</summary>
public sealed class GwenSn76489
{
    private const int NoiseInitialState = 0x8000, PSG_CUTOFF = 0x6;
    private const long LONG_MIN = int.MinValue;   // 32-bit long (ESP32 / STM32 targets)
    private const int PSG_MAX_VOLUME_MAX = 3100;
    private const int PSG_MAX_VOLUME_2dB = (int)(PSG_MAX_VOLUME_MAX * 0.794328234);
    private const int PSG_MAX_VOLUME_4dB = (int)(PSG_MAX_VOLUME_MAX * 0.630957344);
    private static readonly int[] PSGVolumeValues = {
        PSG_MAX_VOLUME_MAX, PSG_MAX_VOLUME_2dB, PSG_MAX_VOLUME_4dB,
        PSG_MAX_VOLUME_MAX / 2, PSG_MAX_VOLUME_2dB / 2, PSG_MAX_VOLUME_4dB / 2,
        PSG_MAX_VOLUME_MAX / 4, PSG_MAX_VOLUME_2dB / 4, PSG_MAX_VOLUME_4dB / 4,
        PSG_MAX_VOLUME_MAX / 8, PSG_MAX_VOLUME_2dB / 8, PSG_MAX_VOLUME_4dB / 8,
        PSG_MAX_VOLUME_MAX / 16, PSG_MAX_VOLUME_2dB / 16, PSG_MAX_VOLUME_4dB / 16,
        0 };

    private float Clock;
    private readonly float dClock;
    private int NumClocksForSample;
    private readonly int WhiteNoiseFeedback = 0;   // never set by gwenesis_SN76489_Init (kept)
    private readonly ushort[] Registers = new ushort[8];
    private int LatchedRegister;
    private ushort NoiseShiftRegister;
    private short NoiseFreq;
    private readonly short[] ToneFreqVals = new short[4];
    private readonly sbyte[] ToneFreqPos = new sbyte[4];
    private readonly short[] Channels = new short[4];
    private readonly long[] IntermediatePos = new long[4];

    public GwenSn76489(double psgClockHz, int samplingRate)
    {
        dClock = (float)(int)psgClockHz / 16 / samplingRate;
        Reset();
    }

    public void Reset()
    {
        for (int i = 0; i <= 3; i++)
        {
            Registers[2 * i] = 1;
            Registers[2 * i + 1] = 0xf;
            NoiseFreq = 0x10;
            ToneFreqVals[i] = 0;
            ToneFreqPos[i] = 1;
            IntermediatePos[i] = LONG_MIN;
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
            Registers[LatchedRegister] = (ushort)((Registers[LatchedRegister] & 0x3f0) | (data & 0xf));
        }
        else
        {
            if ((LatchedRegister % 2) == 0 && LatchedRegister < 5)
                Registers[LatchedRegister] = (ushort)((Registers[LatchedRegister] & 0x00f) | ((data & 0x3f) << 4));
            else
                Registers[LatchedRegister] = (ushort)(data & 0x0f);
        }
        switch (LatchedRegister)
        {
            case 0: case 2: case 4:
                if (Registers[LatchedRegister] == 0) Registers[LatchedRegister] = 1;
                break;
            case 6:
                NoiseShiftRegister = NoiseInitialState;
                NoiseFreq = (short)(0x10 << (Registers[6] & 0x3));
                break;
        }
    }

    /// <summary>gwenesis_SN76489_Update for one sample.</summary>
    public short Next()
    {
        int i;
        for (i = 0; i <= 2; ++i)
            if (IntermediatePos[i] != LONG_MIN)
                Channels[i] = (short)(PSGVolumeValues[Registers[2 * i + 1]] * IntermediatePos[i] / 65536);
            else
                Channels[i] = (short)(PSGVolumeValues[Registers[2 * i + 1]] * ToneFreqPos[i]);

        Channels[3] = (short)(PSGVolumeValues[Registers[7]] * (NoiseShiftRegister & 0x1));
        Channels[3] <<= 1;   // "Double noise volume to make some people happy"

        short outp = Channels[0];
        outp += Channels[1];
        outp += Channels[2];
        outp += Channels[3];

        Clock += dClock;
        NumClocksForSample = (int)Clock;
        Clock -= NumClocksForSample;

        for (i = 0; i <= 2; ++i) ToneFreqVals[i] = (short)(ToneFreqVals[i] - NumClocksForSample);
        if (NoiseFreq == 0x80) ToneFreqVals[3] = ToneFreqVals[2];
        else ToneFreqVals[3] = (short)(ToneFreqVals[3] - NumClocksForSample);

        for (i = 0; i <= 2; ++i)
        {
            if (ToneFreqVals[i] <= 0)
            {
                if (Registers[i * 2] > PSG_CUTOFF)
                {
                    IntermediatePos[i] = (int)((NumClocksForSample - Clock + 2 * ToneFreqVals[i]) * ToneFreqPos[i] / (NumClocksForSample + Clock) * 65536);
                    ToneFreqPos[i] = (sbyte)-ToneFreqPos[i];
                }
                else
                {
                    ToneFreqPos[i] = 1;
                    IntermediatePos[i] = LONG_MIN;
                }
                ToneFreqVals[i] = (short)(ToneFreqVals[i] + Registers[i * 2] * (NumClocksForSample / Registers[i * 2] + 1));
            }
            else IntermediatePos[i] = LONG_MIN;
        }

        if (ToneFreqVals[3] <= 0)
        {
            ToneFreqPos[3] = (sbyte)-ToneFreqPos[3];
            if (NoiseFreq != 0x80) ToneFreqVals[3] = (short)(ToneFreqVals[3] + NoiseFreq * (NumClocksForSample / NoiseFreq + 1));
            if (ToneFreqPos[3] == 1)
            {
                int Feedback;
                if ((Registers[6] & 0x4) != 0)
                {
                    int t = NoiseShiftRegister & WhiteNoiseFeedback;
                    Feedback = (t != 0 && (t ^ WhiteNoiseFeedback) != 0) ? 1 : 0;
                }
                else Feedback = NoiseShiftRegister & 1;
                NoiseShiftRegister = (ushort)((NoiseShiftRegister >> 1) | (Feedback << 15));
            }
        }
        return outp;
    }

    /// <summary>Re-feeds the stock PSG's registers (after a reset, a state load or a swap).</summary>
    public void ReplayFrom(Sn76489 psg)
    {
        foreach (var b in NesEmulator.Sega.MdSoundUnit.PsgReplayBytes(psg)) Write(b);
    }
}
