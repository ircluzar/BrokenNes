using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  MAME 0.36 "YM2413 Translator", ported to C#.
//
//  Source: MAME 0.36 (historic-mame, tag mame036), src/sound/ym2413.c + ym2413.h
//  (https://github.com/mamedev/historic-mame/tree/mame036/src/sound). Written by Paul Leaman; part of MAME
//  (Copyright the MAME team / Nicola Salmoria and the individual authors).
//  License: the MAME license of the time (free, non-commercial use, source must accompany redistribution).
//  This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  Not an OPLL emulator: it turns YM2413 register writes into YM3812 (OPL2) writes for MAME 0.36's fmopl.c
//  (Mame036Opl). The quirks are kept on purpose; they are the sound:
//   - a hand-made instrument table instead of the YM2413's ROM patches ("WS1, WS2 and FB aren't used at present")
//   - no feedback, no connection write: every voice is plain 2-op FM (the OPL's reset default)
//   - the 9-bit OPLL F-number goes into the 10-bit OPL F-number unshifted (everything an octave low)
//   - the channel volume (low nibble of $30-$38) is ignored ("I don't know how to alter the volume"); every voice at full
//   - every $30-$38 write reloads the instrument (KSL/TL bytes included: OPLL reg 3 lands raw on the carrier's KSL/TL)
//   - sustain ($20 bit 5) is dropped; the user instrument's $00-$07 bytes are only applied when a $3x write picks it
//   - rhythm: $0E goes straight to $BD; channels 6-8 get one piano-like "rhythm" patch ("I'm not sure how this works"),
//     rhythm volumes ignored
// =====================================================================================================================

/// <summary>MAME 0.36's ym2413.c: a YM2413 register interface on top of a <see cref="Mame036Opl"/>.</summary>
public sealed class Mame036Ym2413
{
    private const int ym2413_channels = 9;
    private const int ym2413_parameter_count = 12;

    private static readonly int[][] ym2413_instruments =
    {
        /*        AM1  AM2  KS1  KS2  AD1  AD2  SR1  SR2  WS1  WS2   FB */
        new[] { 0x21,0x01,0x72,0x04,0xf1,0x84,0x7e,0x6d,0x00,0x00,0x00,0 },    /* 00 User          */
        new[] { 0x01,0x22,0x23,0x07,0xf0,0xf0,0x07,0x18,0x00,0x00,0x00,0 },    /* 01 Violin        */
        new[] { 0x23,0x01,0x68,0x05,0xf2,0x74,0x6c,0x89,0x00,0x00,0x00,0 },    /* 02 Guitar        */
        new[] { 0x13,0x11,0x25,0x00,0xd2,0xb2,0xf4,0xf4,0x00,0x00,0x00,0 },    /* 03 Piano         */
        new[] { 0x22,0x21,0x1b,0x05,0xc0,0xa1,0x18,0x08,0x00,0x00,0x00,0 },    /* 04 Flute         */
        new[] { 0x22,0x21,0x2c,0x03,0xd2,0xa1,0x18,0x57,0x00,0x00,0x00,0 },    /* 05 Clarinet      */
        new[] { 0x01,0x22,0xba,0x01,0xf1,0xf1,0x1e,0x04,0x00,0x00,0x00,0 },    /* 06 Oboe          */
        new[] { 0x21,0x21,0x28,0x06,0xf1,0xf1,0x6b,0x3e,0x00,0x00,0x00,0 },    /* 07 Trumpet       */
        new[] { 0x27,0x21,0x60,0x00,0xf0,0xf0,0x0d,0x0f,0x00,0x00,0x00,0 },    /* 08 Organ         */
        new[] { 0x20,0x21,0x2b,0x06,0x85,0xf1,0x6d,0x89,0x00,0x00,0x00,0 },    /* 09 Horn          */
        new[] { 0x01,0x21,0xbf,0x02,0x53,0x62,0x5f,0xae,0x01,0x00,0x00,0 },    /* 10 Synthesizer   */
        new[] { 0x23,0x21,0x70,0x07,0xd4,0xa3,0x4e,0x64,0x01,0x00,0x00,0 },    /* 11 Harpsicode    */
        new[] { 0x2b,0x21,0xa4,0x07,0xf6,0x93,0x5c,0x4d,0x00,0x00,0x00,0 },    /* 12 Vibraphone    */
        new[] { 0x21,0x23,0xad,0x07,0x77,0xf1,0x18,0x37,0x00,0x00,0x00,0 },    /* 13 SynthBass     */
        new[] { 0x21,0x21,0x2a,0x03,0xf3,0xe2,0x29,0x46,0x00,0x00,0x00,0 },    /* 14 AcousticBass  */
        new[] { 0x21,0x23,0x37,0x03,0xf3,0xe2,0x29,0x46,0x00,0x00,0x00,0 },    /* 15 ElectricGuitar*/
        new[] { 0x13,0x11,0x25,0x00,0xd7,0xb7,0xf4,0xf4,0x00,0x00,0x00,0 },    /* 16 Rhythm 1: */
        new[] { 0x13,0x11,0x25,0x00,0xd7,0xb7,0xf4,0xf4,0x00,0x00,0x00,0 },    /* 17 Rhythm 2: */
        new[] { 0x13,0x11,0x25,0x00,0xd7,0xb7,0xf4,0xf4,0x00,0x00,0x00,0 },    /* 18 Rhythm 3: */
    };

    private static readonly int[] order1 = { 0x00, 0x01, 0x02, 0x08, 0x09, 0x0a, 0x10, 0x11, 0x12 };
    private static readonly int[] order2 = { 0x03, 0x04, 0x05, 0x0b, 0x0c, 0x0d, 0x13, 0x14, 0x15 };
    private static readonly int[] instReg = { 0x20, 0x20, 0x40, 0x40, 0x60, 0x60, 0x80, 0x80, 0xe0, 0xe0 };

    private readonly Mame036Opl opl;
    private int rhythm_mode;
    private int pending_register;
    private readonly int[] user_instrument = new int[ym2413_parameter_count];

    public Mame036Ym2413(Mame036Opl opl) { this.opl = opl; Start(); }

    public Mame036Opl Opl => opl;

    /// <summary>YM2413_sh_start: chip state to 0, the user instrument from table entry 0.</summary>
    public void Start()
    {
        rhythm_mode = 0; pending_register = 0;
        for (int j = 0; j < ym2413_parameter_count; j++) user_instrument[j] = ym2413_instruments[0][j];
    }

    private void OPL_WRITE(int reg, int data) { opl.Write(0, reg); opl.Write(1, data); }
    private void OPL_WRITE_DATA1(int offset, int channel, int data) => OPL_WRITE(offset + order1[channel], data);
    private void OPL_WRITE_DATA2(int offset, int channel, int data) => OPL_WRITE(offset + order2[channel], data);

    private void SetInstrument(int channel, int inst)
    {
        int[] pn = inst == 0 ? user_instrument : ym2413_instruments[inst];
        for (int i = 0; i < 10; i++)
        {
            if ((i & 1) != 0) OPL_WRITE_DATA2(instReg[i], channel, pn[i]);
            else OPL_WRITE_DATA1(instReg[i], channel, pn[i]);
        }
        /* I dont know wich connection type the YM2413 has - feedback left at the default */
    }

    public void WriteRegister(int data) => pending_register = data & 0xff;

    public void WriteData(int data)
    {
        data &= 0xff;
        int pending = pending_register;
        switch (pending)
        {
            case >= 0x00 and <= 0x07:
                user_instrument[pending] = data;
                break;
            case 0x0e:
                rhythm_mode = data & 0x20;
                if ((data & 0x20) != 0)
                {
                    for (int i = 6; i < 9; i++) OPL_WRITE(0xb0 + i, 0);
                    SetInstrument(6, 16);
                    SetInstrument(7, 17);
                    SetInstrument(8, 18);
                }
                OPL_WRITE(0xbd, data & 0x3f);
                break;
            case >= 0x10 and <= 0x18:
                OPL_WRITE(0xa0 + (pending - 0x10), data);
                break;
            case >= 0x20 and <= 0x28:
            {
                int channel = pending - 0x20;
                int block = (data >> 1) & 0x07;
                int value = (data & 0x10) << 1;   /* key on/off */
                value |= data & 0x01;               /* freq MSB */
                value |= block << 2;                /* octave */
                OPL_WRITE(0xb0 + channel, value);
                break;
            }
            case >= 0x30 and <= 0x38:
            {
                int channel = pending - 0x30;
                if (rhythm_mode != 0 && channel >= 6)
                {
                    /* rhythm volumes: not handled */
                }
                else
                {
                    SetInstrument(channel, (data >> 4) & 0x0f);
                    /* volume = data & 0x0f: not handled */
                }
                break;
            }
            default:
                break;   /* "YM2413: Write to register %02x" to the error log */
        }
    }
}
