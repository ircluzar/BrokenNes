using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  Gens 2.10 PSG (SN76489) emulator, ported to C#.
//
//  Source: Gens 2.10 (2002), Gens210Src.zip, file Gens/psg.c (+ psg.h), driven the way Gens drives it (Gens.cpp
//  PSG_Len per scanline, PSG_Special_Update). https://sourceforge.net/projects/gens/files/OldFiles/Gens210Src.zip
//  Copyright (c) 2002 Stephane Dallongeville. The files carry no license line; the SourceForge Gens project states
//  GPL-2.0. This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2
//  and docs/projects/foreign-apu-project.md.
//
//  A quick port. Gens' shipped default PSG_Improv = 0 (PSG_Update, the square renderer; the PSG_Update_SIN
//  "improved" path is not ported). Quirks kept:
//   - unipolar output: a channel adds its volume while its counter's bit 16 is set, nothing otherwise (DC offset)
//   - no band limiting: a 16.16 counter stepped once per output sample, so high tones alias
//   - a period of 0 plays as period 1; a step >= 0x10000 (above half the output rate) is a constant level
//   - MAX_OUTPUT 0x5FFF / 3 per channel, 2 dB steps, volume 15 off
//   - noise: 16-bit shifter reset to 0x4000 on every noise-register write, white feedback mask 0x12000, periodic 0x08000;
//     the "tone 3" rate is half of channel 3's step, taken when the noise register is written; the shifter only
//     advances while its output bit is 1 or at the counter overflow
//   - no Game Gear stereo (Gens has no Game Gear): both sides get the same mono mix
//   - the latch update for a data byte only happens for frequency registers (a data byte to a volume latch is lost)
// =====================================================================================================================

/// <summary>The Gens 2.10 SN76489: <see cref="Write"/> bytes, <see cref="Update"/> adds mono samples into int buffers.</summary>
public sealed class GensPsg
{
    private const int MAX_OUTPUT = 0x5FFF;
    private const uint W_NOISE = 0x12000, P_NOISE = 0x08000, NOISE_DEF = 0x4000;

    private readonly uint[] PSG_Step_Table = new uint[1024];
    private readonly uint[] PSG_Volume_Table = new uint[16];
    private readonly uint[] PSG_Noise_Step_Table = new uint[4];
    private readonly uint[] PSG_Save = new uint[8];

    // struct _psg
    private int Current_Channel, Current_Register;
    private readonly int[] Register = new int[8];
    private readonly uint[] Counter = new uint[4], CntStep = new uint[4];
    private readonly int[] Volume = new int[4];
    private uint Noise_Type, Noise;

    // Gens' globals: the segment the pending samples go to (PSG_Buf / PSG_Len)
    public bool Enable = true;   // PSG_Enable (default 1)
    public int[] BufL = Array.Empty<int>(), BufR = Array.Empty<int>();
    public int BufOff, Len;

    public GensPsg(int clock, int rate)
    {
        int i; double outv;
        for (i = 1; i < 1024; i++)
        {
            outv = (double)clock / (double)(i << 4);
            outv /= rate;
            outv *= 65536.0;
            PSG_Step_Table[i] = (uint)outv;
        }
        PSG_Step_Table[0] = PSG_Step_Table[1];
        for (i = 0; i < 3; i++)
        {
            outv = (double)clock / (double)(1 << (9 + i));
            outv /= rate;
            outv *= 65536.0;
            PSG_Noise_Step_Table[i] = (uint)outv;
        }
        PSG_Noise_Step_Table[3] = 0;

        outv = MAX_OUTPUT / 3.0;
        for (i = 0; i < 15; i++)
        {
            PSG_Volume_Table[i] = (uint)outv;
            outv /= 1.258925412;
        }
        PSG_Volume_Table[15] = 0;
        Reset();
    }

    /// <summary>The tail of PSG_Init: clear the chip, then PSG_Restore_State from an all-silent save.</summary>
    public void Reset()
    {
        Current_Register = 0; Current_Channel = 0; Noise = 0; Noise_Type = 0;
        for (int i = 0; i < 4; i++) { Volume[i] = 0; Counter[i] = 0; CntStep[i] = 0; }
        for (int i = 0; i < 8; i += 2) { PSG_Save[i] = 0; PSG_Save[i + 1] = 0x0F; }
        for (int i = 0; i < 8; i++)
        {
            Write(0x80 | (i << 4) | (int)(PSG_Save[i] & 0xF));
            Write((int)((PSG_Save[i] >> 4) & 0x3F));
        }
    }

    public void Write(int data)
    {
        if ((data & 0x80) != 0)
        {
            Current_Register = (data & 0x70) >> 4;
            Current_Channel = Current_Register >> 1;
            data &= 0x0F;
            Register[Current_Register] = (Register[Current_Register] & 0x3F0) | data;

            if ((Current_Register & 1) != 0)
            {
                SpecialUpdate();
                Volume[Current_Channel] = (int)PSG_Volume_Table[data];
            }
            else
            {
                SpecialUpdate();
                if (Current_Channel != 3)
                {
                    CntStep[Current_Channel] = PSG_Step_Table[Register[Current_Register]];
                    if (Current_Channel == 2 && (Register[6] & 3) == 3) CntStep[3] = CntStep[2] >> 1;
                }
                else
                {
                    Noise = NOISE_DEF;
                    PSG_Noise_Step_Table[3] = CntStep[2] >> 1;
                    CntStep[3] = PSG_Noise_Step_Table[data & 3];
                    Noise_Type = (data & 4) != 0 ? W_NOISE : P_NOISE;
                }
            }
        }
        else
        {
            if ((Current_Register & 1) == 0)
            {
                if (Current_Channel != 3)
                {
                    SpecialUpdate();
                    Register[Current_Register] = (Register[Current_Register] & 0x0F) | ((data & 0x3F) << 4);
                    CntStep[Current_Channel] = PSG_Step_Table[Register[Current_Register]];
                    if (Current_Channel == 2 && (Register[6] & 3) == 3) CntStep[3] = CntStep[2] >> 1;
                }
            }
        }
    }

    /// <summary>PSG_Update: adds <paramref name="length"/> samples (the same on both sides) at <paramref name="off"/>.</summary>
    public void Update(int[] bufL, int[] bufR, int off, int length)
    {
        int i, j;
        uint cur_cnt, cur_step; int cur_vol;

        for (j = 2; j >= 0; j--)
        {
            if ((cur_vol = Volume[j]) != 0)
            {
                if ((cur_step = CntStep[j]) < 0x10000)
                {
                    cur_cnt = Counter[j];
                    for (i = 0; i < length; i++)
                    {
                        if (((cur_cnt += cur_step) & 0x10000) != 0)
                        {
                            bufL[off + i] += cur_vol;
                            bufR[off + i] += cur_vol;
                        }
                    }
                    Counter[j] = cur_cnt;
                }
                else
                {
                    for (i = 0; i < length; i++) { bufL[off + i] += cur_vol; bufR[off + i] += cur_vol; }
                }
            }
            else Counter[j] += CntStep[j] * (uint)length;
        }

        // Channel 3 - Noise
        if ((cur_vol = Volume[3]) != 0)
        {
            cur_cnt = Counter[3];
            cur_step = CntStep[3];
            for (i = 0; i < length; i++)
            {
                cur_cnt += cur_step;
                if ((Noise & 1) != 0)
                {
                    bufL[off + i] += cur_vol;
                    bufR[off + i] += cur_vol;
                    if ((cur_cnt & 0x10000) != 0)
                    {
                        cur_cnt &= 0xFFFF;
                        Noise = (Noise ^ Noise_Type) >> 1;
                    }
                }
                else if ((cur_cnt & 0x10000) != 0)
                {
                    cur_cnt &= 0xFFFF;
                    Noise >>= 1;
                }
            }
            Counter[3] = cur_cnt;
        }
        else Counter[3] += CntStep[3] * (uint)length;
    }

    /// <summary>PSG_Special_Update: renders the pending scanlines and moves the segment pointer past them.</summary>
    public void SpecialUpdate()
    {
        if (Len != 0 && Enable)
        {
            Update(BufL, BufR, BufOff, Len);
            BufOff += Len;
            Len = 0;
        }
    }
}
