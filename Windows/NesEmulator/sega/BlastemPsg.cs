using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  BlastEm 0.3.1 SN76489 PSG, ported to C#.
//
//  Source: BlastEm v0.3.1 (Mercurial tag v0.3.1, 2015), psg.c + psg.h (clocking from blastem.c: psg_init(sample_rate,
//  MCLKS_NTSC, MCLKS_PER_PSG = 15 * 16, ...); render_sdl.c mixes it mono into both sides, PSG + YM summed, no filter).
//  URL: https://retrodev.com/repos/blastem/archive/v0.3.1.tar.gz
//  Copyright 2013 Michael Pavone. License: GPL-3.0-or-later. This is a documented exception to the
//  repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port. The shortcuts of the original are kept on purpose; they are the sound:
//   - unipolar output (0 .. 4 x 2340, a DC offset), smspower volume table / 14, mono
//   - point sampling at the host rate (BlastEm's buffer_fraction resampler: no filter, aliasing kept)
//   - a counter of 0 reloads 0 and toggles every tick; noise LFSR 16-bit, tap bit 6 ("white"), reset to 0x8000 on a
//     noise write, starts at 0 (silent noise) until the first one; noise period 3 follows tone 2 only when latched
//   - a data byte (bit 7 clear) only reaches tone channels (a volume latch ignores it)
//   - no Game Gear stereo
// =====================================================================================================================

/// <summary>The BlastEm 0.3.1 PSG: writes in, one mono sample at a time at the host rate.</summary>
public sealed class BlastemPsg
{
    private const ulong BUFFER_INC_RES = 1000000000UL;
    private const int PSG_VOL_DIV = 14;
    private static readonly short[] volume_table = {
        32767/PSG_VOL_DIV, 26028/PSG_VOL_DIV, 20675/PSG_VOL_DIV, 16422/PSG_VOL_DIV, 13045/PSG_VOL_DIV, 10362/PSG_VOL_DIV,
        8231/PSG_VOL_DIV, 6568/PSG_VOL_DIV, 5193/PSG_VOL_DIV, 4125/PSG_VOL_DIV, 3277/PSG_VOL_DIV, 2603/PSG_VOL_DIV,
        2067/PSG_VOL_DIV, 1642/PSG_VOL_DIV, 1304/PSG_VOL_DIV, 0
    };

    private ulong buffer_fraction;
    private readonly ulong buffer_inc;
    private ushort lsfr;
    private readonly ushort[] counter_load = new ushort[4], counters = new ushort[4];
    private readonly byte[] volume = new byte[4], output_state = new byte[4];
    private byte noise_out, noise_use_tone, noise_type, latch;

    /// <param name="sampleRate">Host rate.</param><param name="masterClock">The clock the divider applies to.</param><param name="clockDiv">Clocks per PSG tick (240 of the Genesis master clock, 16 of the SMS Z80 clock).</param>
    public BlastemPsg(int sampleRate, uint masterClock, uint clockDiv)
    {
        buffer_inc = ((BUFFER_INC_RES * (ulong)sampleRate) / masterClock) * clockDiv;   // psg_adjust_master_clock
        Reset();
    }

    public void Reset()
    {
        buffer_fraction = 0; lsfr = 0;
        Array.Clear(counter_load); Array.Clear(counters); Array.Clear(output_state);
        for (int i = 0; i < 4; i++) volume[i] = 0xF;
        noise_out = noise_use_tone = noise_type = latch = 0;
    }

    public void Write(byte value)
    {
        if ((value & 0x80) != 0)
        {
            latch = (byte)(value & 0x70);
            int channel = value >> 5 & 0x3;
            if ((value & 0x10) != 0) volume[channel] = (byte)(value & 0xF);
            else if (channel == 3)
            {
                switch (value & 0x3)
                {
                    case 0: case 1: case 2:
                        counter_load[3] = (ushort)(0x10 << (value & 0x3));
                        noise_use_tone = 0;
                        break;
                    default:
                        counter_load[3] = counter_load[2];
                        noise_use_tone = 1;
                        break;
                }
                noise_type = (byte)(value & 0x4);
                lsfr = 0x8000;
            }
            else
            {
                counter_load[channel] = (ushort)((counter_load[channel] & 0x3F0) | (value & 0xF));
                if (channel == 2 && noise_use_tone != 0) counter_load[3] = counter_load[2];
            }
        }
        else if ((latch & 0x10) == 0)
        {
            int channel = latch >> 5 & 0x3;
            if (channel != 3)
            {
                counter_load[channel] = (ushort)((value << 4 & 0x3F0) | (counter_load[channel] & 0xF));
                if (channel == 2 && noise_use_tone != 0) counter_load[3] = counter_load[2];
            }
        }
    }

    /// <summary>psg_run's loop until it writes the next sample to its buffer; returns that sample.</summary>
    public short NextSample()
    {
        for (;;)
        {
            for (int i = 0; i < 4; i++)
            {
                if (counters[i] != 0) counters[i]--;
                if (counters[i] == 0)
                {
                    counters[i] = counter_load[i];
                    output_state[i] = (byte)(output_state[i] == 0 ? 1 : 0);
                    if (i == 3 && output_state[i] != 0)
                    {
                        noise_out = (byte)(lsfr & 1);
                        lsfr = (ushort)((lsfr >> 1) | (lsfr << 15));
                        if (noise_type != 0 && (lsfr & 0x40) != 0) lsfr ^= 0x8000;
                    }
                }
            }
            buffer_fraction += buffer_inc;
            if (buffer_fraction >= BUFFER_INC_RES)
            {
                buffer_fraction -= BUFFER_INC_RES;
                short acc = 0;
                for (int i = 0; i < 3; i++) if (output_state[i] != 0) acc += volume_table[volume[i]];
                if (noise_out != 0) acc += volume_table[volume[3]];
                return acc;
            }
        }
    }
}
