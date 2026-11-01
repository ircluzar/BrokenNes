// =====================================================================================================================
// DarcSmsPsg - a C# port of DarcNES's Master System sound emulation ("sms_psg.c").
//
// Source project : DarcNES (multi-system emulator) by Alastair Bridgewater ("nyef"), as carried in iDarcNES
//                  (the iPodLinux port, ProjectZeroSlackr) - https://github.com/ProjectZeroSlackr/iDarcNES
//                  commit 6f8eb3d979ed62ae7db23cb4ce4b08693b0d765b (2015-06-29)
// Files ported   : sms_psg.c (CVS $Id: sms_psg.c,v 1.14 2000/10/05 07:45:39 nyef Exp $), sms_psg.h; the call sites in
//                  sms.c (port $7E/$7F writes, sms_psg_vsync every 228 * 262 Z80 cycles) and snd_unix.c's
//                  snd_output_4_waves (the 4-buffer mix, (w1+w2+w3+w4) << 5).
// Copyright      : DarcNES is Copyright 1998, 1999, 2000, 2001 by Alastair Bridgewater.
// License        : DarcNES's own terms (from its "readme"):
//                    "Commercial use prohibited without express written permission from the author. Everyone else
//                     can do whatever they want, provided that I am credited in the documentation (and, if released,
//                     the source code)."
//                  i.e. credit required, NON-COMMERCIAL.
// This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
// Quirks kept (they are the sound):
//  - Per-frame rendering: sms_psg_vsync renders a whole 735-sample block (44.1 kHz quality) once per video frame from
//    the registers as they stand; writes inside a frame are heard from the next block.
//  - The noise shift register is 5 bits (seeded 0x1f, feedback into bit 5): the real chip's is 15-16 bits, so the
//    "white" noise repeats every 31 steps (metallic, tonal) and the periodic noise is a 5-step pattern; the
//    periodic feedback is the INVERTED low bit. The register is reseeded on every noise-register write.
//  - Noise steps once per full 8.24 phase wrap (not per half period); rates 0x10 << n or tone 2's period.
//  - LINEAR volume: (15 - v) * 0x11 (no 2 dB steps).
//  - 8.24 fixed-point phase with magic 0x512b3400 (derived from 3.579545 MHz; the comment about the older 125000
//    figure, "probably wrong", is the reason this one exists); period 0 is silence.
//  - Unipolar 8-bit channel buffers, mixed as (sum << 5); mono (no Game Gear stereo in DarcNES).
// =====================================================================================================================
using System;

namespace NesEmulator.Sega
{
    /// <summary>DarcNES 1.14's SN76489 (see the file header). <see cref="Write"/> port writes; <see cref="Vsync"/> renders
    /// <see cref="SamplesPerSync"/> samples into <see cref="Mix"/>.</summary>
    public sealed class DarcSmsPsg
    {
        public const int SamplesPerSync = 735;   /* 44100 / 60 = 735 samples per sync */
        public const int CyclesPerSync = 228 * 262;
        private const uint Magic = 0x512b3400;   // sms_psg_qual[1]

        private int lastchan;
        private readonly byte[] vols = new byte[4], freqls = new byte[4], freqhs = new byte[4];
        private readonly uint[] index = new uint[4], skip = new uint[4];
        private uint shiftreg;

        private readonly byte[][] wave = { new byte[SamplesPerSync], new byte[SamplesPerSync], new byte[SamplesPerSync], new byte[SamplesPerSync] };
        /// <summary>The last block, (w1 + w2 + w3 + w4) &lt;&lt; 5: unsigned 0..32640.</summary>
        public readonly int[] Mix = new int[SamplesPerSync];

        private void CalcFreqs(int channel)
        {
            uint freq;
            if (channel != 3) freq = (uint)((freqhs[channel] << 4) + freqls[channel]);
            else
            {
                if ((freqls[3] & 3) != 3) freq = (uint)(0x10 << (freqls[3] & 3));
                else freq = (uint)((freqhs[2] << 4) + freqls[2]);
                shiftreg = 0x1f;
            }
            skip[channel] = freq != 0 ? Magic / freq : 0;

            if (channel == 2 && (freqls[3] & 3) == 3)
            {
                freq = (uint)((freqhs[2] << 4) + freqls[2]);
                skip[3] = freq != 0 ? Magic / freq : 0;
            }
        }

        public void Write(byte data)
        {
            if ((data & 0x80) != 0)
            {
                if ((data & 0x10) != 0) vols[(data >> 5) & 3] = (byte)((15 - (data & 15)) * 0x11);   /* volume */
                else
                {   /* freq low */
                    lastchan = (data >> 5) & 3;
                    freqls[lastchan] = (byte)(data & 15);
                    CalcFreqs(lastchan);
                }
            }
            else
            {   /* freq high */
                freqhs[lastchan] = (byte)(data & 0x3f);
                CalcFreqs(lastchan);
            }
        }

        private void Pulse(int ch)
        {
            var buf = wave[ch];
            for (int i = 0; i < SamplesPerSync; i++)
            {
                index[ch] += skip[ch];
                index[ch] &= 0x1fffffff;
                buf[i] = (index[ch] & 0x10000000) != 0 ? vols[ch] : (byte)0;
            }
        }

        private void Wave4()
        {
            if (shiftreg == 0) shiftreg = 0x1f;
            var buf = wave[3];
            for (int i = 0; i < SamplesPerSync; i++)
            {
                index[3] += skip[3];
                if (index[3] > 0x1fffffff)
                {
                    uint b0 = (shiftreg & 1) == 0 ? 1u : 0u;
                    if ((freqls[3] & 0x04) != 0)
                    {
                        uint b2 = (shiftreg & 4) == 0 ? 1u : 0u;
                        shiftreg |= (b0 ^ b2) << 5;
                    }
                    else shiftreg |= b0 << 5;
                    shiftreg >>= 1;
                }
                index[3] &= 0x1fffffff;
                buf[i] = (shiftreg & 1) != 0 ? vols[3] : (byte)0;
            }
        }

        public void Vsync()
        {
            Pulse(0); Pulse(1); Pulse(2); Wave4();
            for (int i = 0; i < SamplesPerSync; i++)
                Mix[i] = (wave[0][i] + wave[1][i] + wave[2][i] + wave[3][i]) << 5;
        }
    }
}
