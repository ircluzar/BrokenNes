// =====================================================================================================================
// SmsaPsg - a C# port of SMSAdvance's SN76496 / SN76489 sound emulation (GBA, ARM assembly).
//
// Source project : SMSAdvance V2.5 (Sega Master System / Game Gear / SG-1000 emulator for the Game Boy Advance),
//                  based on Loopy's PocketNES - https://www.gamebrew.org/wiki/SMSAdvance_GBA
//                  (archive smsadvanceflu.7z -> SMSAdvance25Src.zip)
// Author         : FluBBa (Fredrik Ahlström, "Fredrik Olsson" in SOURCE.TXT)
// Files ported   : src/sn76496.s (mixer, SN76496_W, SetNoiseF, GG_Stereo_W, frequency_reset, SN76496_reset,
//                  Vbl_SN76496_1/2, SoundVariables), src/sn76496.h; the call sites in cart.s (SOUND_reset: SMS vs
//                  SG-1000 noise taps), io.s (ports $7E/$7F -> SN76496_W, GG port 6 -> GG_Stereo_W), gfx.s
//                  (vblankinterrupt: Vbl_SN76496_1 swaps the DMA double buffer, Vbl_SN76496_2 mixes the next one),
//                  equates.h (PCMWAVSIZE 528).
// License        : SOURCE.TXT: "You are free to do anything you want with it. I only ask that if you do use any of
//                  it in your own project, you give me and Loopy some credit for it." No formal licence is named
//                  (GameBrew lists it as "Mixed"). Credit: FluBBa (Fredrik Ahlström) and Loopy (PocketNES).
// This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md.
//
// Characteristic flaws kept on purpose (they are the sound):
//  - Per-frame rendering: Vbl_SN76496_2 mixes a whole 528-sample block (mixrate 532: 0x1000000/532 = 31,536 Hz) from
//    the registers as they stand at vblank; writes inside a frame are heard from a later block. The block plays
//    through DMA during the NEXT frame (pcmptr0/pcmptr1 double buffer), so there is a further frame of latency.
//  - Tones are a 16-bit phase (chNaddr) advanced by FREQTBL[period] = freqconv / (2 * period + 2) per sample (period
//    + 1, not period); the sample is "on" while the phase's top bit is clear. Periods 0-5 give a step of 0 ("to remove
//    real high tones"): the phase freezes and the channel sits at a constant level (on if it froze in the low half).
//  - Noise: a 16-bit register shifted once per 16-bit phase wrap, seeded 0x8000 on every noise write, feedback
//    0x9000 (white) / 0x8000 (periodic) for SMS/GG, 0x6000 / 0x4000 for the SG-1000; rates freqconv >> 5/6/7 (the
//    asm's "these values sound ok") or tone 2's step. Reset does not clear rng / noisefb (memset covers 44 bytes).
//  - Volume: SMSAdvance's own 16-step table 0x3F,0x32,...,0x02,0x00 (not 2 dB steps), channels summed into an 8-bit
//    sample starting at 0x82 (-126): unipolar, max 0x82 + 4*0x3F = +126. Reset leaves all four volumes at 0 (silence).
//  - Game Gear stereo: GG_Table masks each channel's volume byte per side (on / off, no partial).
//  - The GBA output path: DirectSound A (left) and B (right), 8-bit samples; this port emits the 8-bit sample
//    minus the -126 floor (0..252) scaled by 64, i.e. unipolar like the other ports; the GBA's PWM and AC coupling
//    are not modelled.
// =====================================================================================================================
using System;

namespace NesEmulator.Sega
{
    /// <summary>SMSAdvance 2.5's PSG (see the file header). <see cref="Write"/> / <see cref="WriteStereo"/> port writes; <see cref="Vbl"/>
    /// swaps the double buffer and mixes the next <see cref="MixLength"/> samples.</summary>
    public sealed class SmsaPsg
    {
        public const int MixLength = 528;        // mixlength (528=high, 304=low)
        public const int MixRate = 532;          // mixrate: frequency = 0x1000000/532 Hz
        private const uint NSeed = 0x8000;
        private const uint WFeedSms = 0x9000, PFeedSms = 0x8000, WFeedSn = 0x6000, PFeedSn = 0x4000;
        private const uint FreqConvNtsc = 0x71819, FreqConvPal = 0x70788;
        private static readonly byte[] Attenuation = { 0x3F, 0x32, 0x28, 0x20, 0x19, 0x14, 0x10, 0x0D, 0x0A, 0x08, 0x06, 0x05, 0x04, 0x03, 0x02, 0x00 };
        private static readonly uint[] GgTable =
        {
            0x00000000,0x0000003F,0x00003F00,0x00003F3F,0x003F0000,0x003F003F,0x003F3F00,0x003F3F3F,
            0x3F000000,0x3F00003F,0x3F003F00,0x3F003F3F,0x3F3F0000,0x3F3F003F,0x3F3F3F00,0x3F3F3F3F,
        };

        private readonly bool sn;               // SN76496_reset r0=1 (SG-1000 taps)
        private readonly uint freqconv;
        private readonly ushort[] freqTbl = new ushort[1024];

        private byte lastreg;
        private readonly uint[] chreg = new uint[4];
        private uint ggL = 0x3F3F3F3F, ggR = 0x3F3F3F3F;
        private readonly uint[] ch = new uint[4];   // freq (low half) | addr (high half)
        private uint rng = NSeed, noisefb = WFeedSms;
        private uint volumes;                       // ch0volume..ch3volume bytes

        private sbyte[] play = new sbyte[MixLength * 2], next = new sbyte[MixLength * 2];   // L then R

        /// <summary>The block playing now (DMA source), interleaved L,R as 8-bit DirectSound samples.</summary>
        public ReadOnlySpan<sbyte> Playing => play;

        public SmsaPsg(bool pal, bool sg1000)
        {
            sn = sg1000;
            freqconv = pal ? FreqConvPal : FreqConvNtsc;
            FrequencyReset();
            Reset();
            Array.Fill(play, unchecked((sbyte)0x82)); Array.Fill(next, unchecked((sbyte)0x82));
        }

        private void FrequencyReset()
        {
            for (int r4 = 2048; r4 > 0; r4 -= 2)
            {
                uint v = freqconv / (uint)r4;
                if (r4 < 7 * 2) v = 0;   // to remove real high tones
                freqTbl[(r4 - 2) >> 1] = (ushort)v;
            }
        }

        public void Reset()
        {
            // memset 44 bytes: lastreg, chNreg, GG_Volume_L/R, chNfreq/addr (rng and noisefb survive)
            lastreg = 0; Array.Clear(chreg); Array.Clear(ch);
            volumes = 0;   // str r1,ch0volume: silence
            WriteStereo(0xFF);
        }

        public void WriteStereo(byte v)
        {
            ggL = GgTable[(v & 0xF0) >> 4];
            ggR = GgTable[v & 0x0F];
        }

        public void Write(byte r0)
        {
            uint r1;
            if ((r0 & 0x80) != 0) { r1 = (uint)(r0 & 0x70); lastreg = (byte)r1; }
            else r1 = lastreg;
            bool vol = (r1 & 0x10) != 0;
            r1 >>= 5;
            if (vol)
            {
                int sh = (int)r1 * 8;
                volumes = (volumes & ~(0xFFu << sh)) | (uint)Attenuation[r0 & 0x0F] << sh;
                return;
            }
            if (r1 == 3) { SetNoiseF(r0); return; }
            uint w = chreg[r1];
            if ((r0 & 0x80) == 0) w = (w & 0xFFFF00FF) | (uint)(r0 & 0x3F) << 8;
            else w = (w & 0xFFFFFF00) | (uint)(byte)(r0 << 4);
            chreg[r1] = w;
            ushort f = freqTbl[(w >> 3) >> 1];
            ch[r1] = (ch[r1] & 0xFFFF0000) | f;
            if (r1 == 2 && (chreg[3] & 3) == 3) ch[3] = (ch[3] & 0xFFFF0000) | f;
        }

        private void SetNoiseF(byte r0)
        {
            chreg[3] = r0;
            bool white = (r0 & 4) != 0;
            noisefb = sn ? (white ? WFeedSn : PFeedSn) : (white ? WFeedSms : PFeedSms);
            rng = NSeed;
            uint r2 = freqconv, f;
            int m = r0 & 3;
            f = m == 0 ? r2 >> 5 : r2 >> 6;
            if (m == 2) f = r2 >> 7;
            else if (m == 3) f = ch[2] & 0xFFFF;
            ch[3] = (ch[3] & 0xFFFF0000) | (f & 0xFFFF);
        }

        /// <summary>vblankinterrupt: Vbl_SN76496_1 (the mixed block starts playing) then Vbl_SN76496_2 (mix the next one).</summary>
        public void Vbl()
        {
            (play, next) = (next, play);
            uint r0 = ggL & volumes, r1 = ggR & volumes;
            uint r2 = ch[0], r3 = ch[1], r4 = ch[2], r5 = ch[3], r6 = rng, r7 = noisefb;
            for (int i = 0; i < MixLength; i++)
            {
                uint l = 0x82, r = 0x82;
                r2 += r2 << 16; if ((int)r2 >= 0) { l += r0; r += r1; }
                r3 += r3 << 16; if ((int)r3 >= 0) { l += r0 >> 8; r += r1 >> 8; }
                r4 += r4 << 16; if ((int)r4 >= 0) { l += r0 >> 16; r += r1 >> 16; }
                ulong s = (ulong)r5 + ((ulong)r5 << 16 & 0xFFFFFFFF);
                r5 = (uint)s;
                if ((s >> 32) != 0)
                {
                    bool c = (r6 & 1) != 0; r6 >>= 1;
                    if (c) r6 ^= r7;
                }
                if ((r6 & 1) != 0) { l += r0 >> 24; r += r1 >> 24; }
                next[i * 2] = unchecked((sbyte)(byte)l);
                next[i * 2 + 1] = unchecked((sbyte)(byte)r);
            }
            ch[0] = r2; ch[1] = r3; ch[2] = r4; ch[3] = r5; rng = r6;
        }
    }
}
