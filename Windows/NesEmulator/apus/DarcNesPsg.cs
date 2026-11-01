// =====================================================================================================================
// DarcNesPsg - a C# port of DarcNES's NES sound emulation ("nes_psg.c").
//
// Source project : DarcNES (multi-system emulator) by Alastair Bridgewater ("nyef"), as carried in iDarcNES
//                  (the iPodLinux port, ProjectZeroSlackr) - https://github.com/ProjectZeroSlackr/iDarcNES
//                  commit 6f8eb3d979ed62ae7db23cb4ce4b08693b0d765b (2015-06-29)
// Files ported   : nes_psg.c (CVS $Id: nes_psg.c,v 1.50 2000/11/18 14:56:57 nyef Exp $), nes_psg.h 1.6;
//                  the call sites in nes.c (the 7457-cycle sync event, the $4000-$400F / $4015 write dispatch) and
//                  snd_unix.c's snd_output_4_waves (the 4-buffer mix, (w1+w2+w3+w4) << 5).
// Copyright      : DarcNES is Copyright 1998, 1999, 2000, 2001 by Alastair Bridgewater.
// License        : DarcNES's own terms (from its "readme"):
//                    "Commercial use prohibited without express written permission from the author. Everyone else
//                     can do whatever they want, provided that I am credited in the documentation (and, if released,
//                     the source code)."
//                  i.e. credit required, NON-COMMERCIAL.
// This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
// Quirks kept (they are the sound):
//  - Rendering happens in blocks: nes_psg_frame runs every 7457 CPU cycles (~240 Hz) and renders 182 samples (44.1 kHz
//    quality) from the registers as they stand then; envelopes and sweeps clock once per block, length counters every
//    4th block (~60 Hz). Mid-block register changes are only heard at the next block.
//  - No DMC at all ($4010-$4013: "FIXME: more sound regs go here").
//  - No noise: nes_psg_wave_4's body is "#if 0" in 1.50, so channel 4's buffer stays zero.
//  - Triangle: "FIXME: Add linear counter" - no linear counter, a 32-step 0x00..0xFF ramp at full volume always; a
//    stopped triangle (or square) freezes its phase and holds a DC level.
//  - 8.24 fixed-point phase accumulators: step = magic / period (nearest-period pitch errors, "some frequency values
//    are not handled well"); duty table order 87.5/75/50/25 (duty 0 is the inverse of 12.5%).
//  - Envelope: decay restarts at 15 on a $4003/$4007 write; length counter loaded from DarcNES's own table; the
//    sweep writes the new period straight into the registers, has no upper-bound mute, pulse 1 subtracts one more.
//  - $4015 only gates the channels (stops the phase); it never clears length counters. $4017 is ignored.
//  - 8-bit unsigned channel buffers, mixed as (sum << 5): unipolar, 16-bit-ish, no filtering.
// =====================================================================================================================
using System;

namespace NesEmulator
{
    /// <summary>DarcNES 1.50's NES sound engine (see the file header). Feed it register writes, call <see cref="Frame"/> every
    /// <see cref="CyclesPerSync"/> CPU cycles; each call renders <see cref="SamplesPerSync"/> samples into <see cref="Mix"/>.</summary>
    public sealed class DarcNesPsg
    {
        public const int CyclesPerSync = 7457;
        public const int SamplesPerSync = 182;   // nes_psg_qual[1]: 44100 Hz
        public const int NominalRate = 44100;

        private const uint PulseMagic = 0x512b3800, TriangleMagic = 0x289d9c00;

        private struct Channel { public byte a, b, c, d; }
        private struct Envelope { public byte decay_counter, rate_counter; }

        private Channel c1, c2, c3, c4;
        private byte control;

        private static readonly byte[] Pulse25 = Make(8), Pulse50 = Make(16), Pulse75 = Make(24), Pulse87 = Make(28);
        private static byte[] Make(int high) { var t = new byte[32]; for (int i = 0; i < high; i++) t[i] = 0x11; return t; }
        private static readonly byte[][] PulseWaves = { Pulse87, Pulse75, Pulse50, Pulse25 };
        private static readonly byte[] Triangle50 =
        {
            0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
            0xff, 0xee, 0xdd, 0xcc, 0xbb, 0xaa, 0x99, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x00,
        };
        private static readonly byte[] Atl =
        {
            5, 127, 10, 1, 20,  2, 40,  3, 80,  4, 30,  5, 7,  6, 13,  7,
            6,   8, 12, 9, 24, 10, 48, 11, 96, 12, 36, 13, 8, 14, 16, 15,
        };

        private readonly byte[][] wave = { new byte[SamplesPerSync], new byte[SamplesPerSync], new byte[SamplesPerSync], new byte[SamplesPerSync] };
        /// <summary>The last block, mixed as snd_unix.c does: (w1 + w2 + w3 + w4) &lt;&lt; 5 (unsigned, 0..32640).</summary>
        public readonly int[] Mix = new int[SamplesPerSync];

        private uint w1Index, w2Index, w3Index;
        private int w1Sweep, w2Sweep;
        private byte w1Len, w2Len, w3Len;
        private Envelope w1Env, w2Env;
        private int lengthCounterClock;

        /// <summary>Channel mute mask for the host (bit 0 pulse 1, bit 1 pulse 2, bit 2 triangle); not in DarcNES.</summary>
        public int ChannelMask = 0x1F;

        /// <summary>A write to $4000-$4017 (nes.c's nes_write_bank_4 dispatch).</summary>
        public void Write(ushort addr, byte v)
        {
            if (addr < 0x4010)
            {
                switch (addr & 15)
                {
                    case 0: c1.a = v; break; case 1: c1.b = v; break; case 2: c1.c = v; break;
                    case 3: c1.d = v; w1Env.decay_counter = 0x0f; w1Len = Atl[v >> 3]; break;
                    case 4: c2.a = v; break; case 5: c2.b = v; break; case 6: c2.c = v; break;
                    case 7: c2.d = v; w2Env.decay_counter = 0x0f; w2Len = Atl[v >> 3]; break;
                    case 8: c3.a = v; break; case 9: c3.b = v; break; case 10: c3.c = v; break;
                    case 11: c3.d = v; w3Len = Atl[v >> 3]; break;
                    case 12: c4.a = v; break; case 13: c4.b = v; break; case 14: c4.c = v; break; case 15: c4.d = v; break;
                }
            }
            else if (addr == 0x4015) control = v;
            // $4010-$4013: "FIXME: more sound regs go here"; $4017: "FIXME: IRQ control?"
        }

        private static byte EnvelopeGetVolume(ref Channel ch, ref Envelope e) => (ch.a & 0x10) != 0 ? (byte)(ch.a & 0x0f) : e.decay_counter;

        private static void EnvelopeRunCounter(ref Channel ch, ref Envelope e)
        {
            if (e.rate_counter-- != 0) return;
            e.rate_counter = (byte)(ch.a & 0x0f);
            if (e.decay_counter-- != 0) return;
            e.decay_counter = (byte)((ch.a & 0x20) != 0 ? 0x0f : 0);
        }

        private static void RunSweepUnit(ref Channel ch, ref int sweepClock, ushort freq, bool isC1)
        {
            if ((ch.b & 0x80) == 0) return;
            if (sweepClock-- != 0) return;
            sweepClock = (ch.b >> 3) & 0x0e;
            int shift = ch.b & 7;
            if (shift == 0) return;
            if ((ch.b & 0x08) != 0)
            {
                freq -= (ushort)(freq >> shift);
                if (isC1) freq--;
            }
            else freq += (ushort)(freq >> shift);
            ch.c = (byte)freq;
            ch.d = (byte)((ch.d & ~7) | ((freq >> 8) & 7));
            /* FIXME: sweep unit upper bound shutdown? */
        }

        private void Pulse(ref Channel ch, ref Envelope env, ref uint index, ref int sweep, byte len, byte enableBit, byte[] buf, bool isC1)
        {
            byte volume = EnvelopeGetVolume(ref ch, ref env);
            ushort freq = (ushort)(((ch.d & 0x07) << 8) + ch.c);
            uint step = freq >= 8 ? PulseMagic / freq : 0;   /* sweep unit lower bound shutdown */
            if (len == 0) step = 0;
            if ((control & enableBit) == 0) step = 0;        /* channel output enable */
            var sample = PulseWaves[ch.a >> 6];
            for (int i = 0; i < SamplesPerSync; i++)
            {
                index += step;
                index &= 0x1fffffff;
                buf[i] = (byte)(sample[index >> 24] * volume);
            }
            EnvelopeRunCounter(ref ch, ref env);
            RunSweepUnit(ref ch, ref sweep, freq, isC1);
        }

        private void Triangle()
        {
            /* FIXME: Add linear counter */
            ushort freq = (ushort)(((c3.d & 0x07) << 8) + c3.c);
            uint step = freq != 0 ? TriangleMagic / freq : 0;
            if (w3Len == 0) step = 0;
            if ((control & 0x04) == 0) step = 0;
            var buf = wave[2];
            for (int i = 0; i < SamplesPerSync; i++)
            {
                w3Index += step;
                w3Index &= 0x1fffffff;
                buf[i] = Triangle50[w3Index >> 24];
            }
        }

        /// <summary>nes_psg_frame: render one 182-sample block into <see cref="Mix"/>.</summary>
        public void Frame()
        {
            Pulse(ref c1, ref w1Env, ref w1Index, ref w1Sweep, w1Len, 0x01, wave[0], true);
            Pulse(ref c2, ref w2Env, ref w2Index, ref w2Sweep, w2Len, 0x02, wave[1], false);
            Triangle();
            // nes_psg_wave_4: "#if 0" - the noise never renders; wave[3] stays zero.

            if (lengthCounterClock-- == 0)
            {
                lengthCounterClock = 3; /* FIXME: This may want to be 3 */
                if ((c1.a & 0x20) == 0 && w1Len != 0) w1Len--;
                if ((c2.a & 0x20) == 0 && w2Len != 0) w2Len--;
                if ((c3.a & 0x80) == 0 && w3Len != 0) w3Len--;
            }

            int m = ChannelMask;
            for (int i = 0; i < SamplesPerSync; i++)
            {
                int s = ((m & 1) != 0 ? wave[0][i] : 0) + ((m & 2) != 0 ? wave[1][i] : 0) + ((m & 4) != 0 ? wave[2][i] : 0) + wave[3][i];
                Mix[i] = s << 5;
            }
        }
    }
}
