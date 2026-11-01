using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  MAME 0.36 SN76496 PSG, ported to C#.
//
//  Source: MAME 0.36 (1999), src/sound/sn76496.c + sn76496.h, https://github.com/mamedev/historic-mame (tag mame036),
//  raw files at https://raw.githubusercontent.com/mamedev/historic-mame/mame036/src/sound/sn76496.c ; run at MAME's
//  default 22,050 Hz with gain 0 (SN76496interface volume & 0xff00 = 0).
//  Copyright (C) Nicola Salmoria and the MAME development team.
//  License: the MAME license of the time (non-commercial; source must accompany). This is a documented exception to the
//  repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port. The original's shortcuts are kept on purpose, they are the sound:
//   - "noise emulation is not accurate": white noise taps FB_WNOISE 0x12000 (bit0 ^ bit2 into bit 15, a 16-bit LFSR),
//     periodic noise FB_PNOISE 0x08000, the shifter preset to 0x0F35 on every noise-register write
//   - unipolar output (0 .. 0x7FFF, a square from 0 to its level, so a DC offset that moves with the volume), clipped
//     at 0x7FFF, 2 dB volume steps from MAX_OUTPUT/3
//   - period 0 counts as 1; a silent voice's counter is pushed forward instead of run
//   - mono: no Game Gear stereo (the $06 port is ignored)
//   - register changes take effect on the next rendered chunk (MAME's stream granularity; here the host's drain)
// =====================================================================================================================

/// <summary>The MAME 0.36 SN76496: feed it PSG bytes, render 22,050 Hz mono, or fill a host-rate stereo buffer by zero-order hold.</summary>
public sealed class Mame036Sn76496
{
    public const int DefaultRate = 22050;
    private const int MAX_OUTPUT = 0x7fff, STEP = 0x10000;
    private const int FB_WNOISE = 0x12000, FB_PNOISE = 0x08000, NG_PRESET = 0x0f35;

    private readonly int SampleRate;
    private uint UpdateStep;
    private readonly int[] VolTable = new int[16], Register = new int[8], Volume = new int[4], Period = new int[4], Count = new int[4], Output = new int[4];
    private int LastRegister;
    private uint RNG;
    private int NoiseFB;

    // host-rate conversion state
    private double phase;
    private short held;
    private short[] buf = new short[1024];
    private readonly int[] vol = new int[3];

    public Mame036Sn76496(double clock, int sampleRate = DefaultRate, int gain = 0)
    {
        SampleRate = sampleRate;
        UpdateStep = (uint)((double)STEP * SampleRate * 16 / clock);
        for (int i = 0; i < 4; i++) Volume[i] = 0;
        LastRegister = 0;
        for (int i = 0; i < 8; i += 2) { Register[i] = 0; Register[i + 1] = 0x0f; }
        for (int i = 0; i < 4; i++) { Output[i] = 0; Period[i] = Count[i] = (int)UpdateStep; }
        RNG = NG_PRESET;
        Output[3] = (int)(RNG & 1);
        SetGain(gain);
    }

    private void SetGain(int gain)
    {
        gain &= 0xff;
        double o = MAX_OUTPUT / 3;
        while (gain-- > 0) o *= 1.023292992;
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
                case 6:
                {
                    int n = Register[6];
                    NoiseFB = (n & 4) != 0 ? FB_WNOISE : FB_PNOISE;
                    n &= 3;
                    Period[3] = n == 3 ? 2 * Period[2] : (int)(UpdateStep << (5 + n));
                    RNG = NG_PRESET;
                    Output[3] = (int)(RNG & 1);
                    break;
                }
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
            }
        }
    }

    /// <summary>SN76496Update: <paramref name="buffer"/>.Length mono samples at the engine rate.</summary>
    public void Update(Span<short> buffer)
    {
        int length = buffer.Length, pos = 0;
        for (int i = 0; i < 4; i++)
            if (Volume[i] == 0 && Count[i] <= length * STEP) Count[i] += length * STEP;

        while (length > 0)
        {
            int v0 = 0, v1 = 0, v2 = 0, v3 = 0;
            for (int i = 0; i < 3; i++)
            {
                int vv = 0;
                if (Output[i] != 0) vv += Count[i];
                Count[i] -= STEP;
                while (Count[i] <= 0)
                {
                    Count[i] += Period[i];
                    if (Count[i] > 0)
                    {
                        Output[i] ^= 1;
                        if (Output[i] != 0) vv += Period[i];
                        break;
                    }
                    Count[i] += Period[i];
                    vv += Period[i];
                }
                if (Output[i] != 0) vv -= Count[i];
                vol[i] = vv;
            }
            v0 = vol[0]; v1 = vol[1]; v2 = vol[2];

            int left = STEP;
            do
            {
                int nextevent = Count[3] < left ? Count[3] : left;
                if (Output[3] != 0) v3 += Count[3];
                Count[3] -= nextevent;
                if (Count[3] <= 0)
                {
                    if ((RNG & 1) != 0) RNG ^= (uint)NoiseFB;
                    RNG >>= 1;
                    Output[3] = (int)(RNG & 1);
                    Count[3] += Period[3];
                    if (Output[3] != 0) v3 += Period[3];
                }
                if (Output[3] != 0) v3 -= Count[3];
                left -= nextevent;
            } while (left > 0);

            uint o = unchecked((uint)(v0 * Volume[0] + v1 * Volume[1] + v2 * Volume[2] + v3 * Volume[3]));
            if (o > (uint)MAX_OUTPUT * STEP) o = (uint)MAX_OUTPUT * STEP;
            buffer[pos++] = (short)(o / STEP);
            length--;
        }
    }

    /// <summary>Overwrites a host-rate interleaved stereo buffer: renders the engine samples it spans in one chunk, then holds each one (mono on both sides).</summary>
    public void FillHost(Span<short> stereo, int hostRate)
    {
        int frames = stereo.Length / 2;
        if (frames == 0) return;
        // how many engine samples this span crosses
        double end = phase + (double)frames * SampleRate / hostRate;
        int need = (int)end - (int)phase;
        if (need > buf.Length) buf = new short[need * 2];
        if (need > 0) Update(buf.AsSpan(0, need));
        int k = 0; double p = phase, stepE = (double)SampleRate / hostRate;
        int baseI = (int)phase;
        for (int f = 0; f < frames; f++)
        {
            p += stepE;
            int idx = (int)p - baseI - 1;
            if (idx >= 0 && idx < need) held = buf[idx];
            stereo[k++] = held; stereo[k++] = held;
        }
        phase = end - (int)end;
    }

    /// <summary>Re-feeds the stock PSG's registers (after a reset, a state load or a swap): tones, volumes, then the noise register.</summary>
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
