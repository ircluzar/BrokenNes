// ============================================================================================================
// NesticleSound - a C# port of NESticle's 1997 prototype NES sound engine.
//
// Source project : NESticle (DOS/Win95 NES emulator), Bloodlust Software - sound code by Icer Addis ("Sardu")
// Version        : the 1997 prototype source tree as reposted in 2019, github.com/athros/NESticle, branch master
//                  (fetched 2026-11-01; the GitHub API was not reachable from this session, so no commit hash)
// Files ported   : Source/NESSOUND.CPP, Source/NESSOUND.H (all of the channel/mixer logic), plus the glue it needs:
//                  Source/SOUND.H (extern SOUNDRATE), Source/MAIN.CPP (int SOUNDRATE=22050), Source/NES.CPP
//                  ($4000-$4013 -> ns->write(a-0x4000), $4015 -> ns->setenable; $4017 never reaches the sound layer)
// URL            : https://github.com/athros/NESticle
// Copyright      : (c) 1997 Bloodlust Software / Icer Addis
// License        : NO LICENSE: leaked 1997 prototype source (Bloodlust Software), included by the project owner's
//                  decision for art; see THIRD_PARTY_NOTICES.md section 2
// This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
// Quirks kept (they ARE the sound):
//  - pitch: freq = 0x20000 / period (real: 1789773/16/(period+1)), so squares play ~2.75 semitones sharp and the triangle
//    (real: /32) about an octave + 2.7 semitones sharp; phase step K = (freq<<16)/SOUNDRATE in 32-bit int (wraps for period<=4).
//  - the noise and DMC channels are the base class with an empty mix: SILENT.
//  - duty table {87.5, 75, 58.5, 25}% of the period (inverted order, and a 58.5% "half" duty).
//  - square volume from a 16-entry table on a 0x7C0 scale; the "envelope" (bit 4 clear) is --vol every 256 samples (a linear
//    fade over ~0x7C0*256 samples, almost flat); sweep is linear: K += deltaK every 256 samples, deltaK=(shift<<16)/SOUNDRATE,
//    negated by the negate bit; the sweep period bits only gate it on/off.
//  - note length: SOUNDRATE*((reg3>>3)+1)/16 samples (the raw 5-bit index, not the length table); "hold note" (bit 5) = 0.5 s.
//    Every write to reg 2 OR reg 3 restarts the note (and the length); the phase is never reset.
//  - triangle: no linear counter, fixed volume 0x600, silent only while $4008 == 0; phase counter not masked (int wrap).
//  - 16-bit signed mix summed in a short (wraps), mono, 22050 Hz; the channels mix only while enabled in $4015.
// ============================================================================================================

namespace NesEmulator
{
    /// <summary>NESticle 1997's nessound, mixing one 16-bit sample at a time at <see cref="SoundRate"/> Hz.</summary>
    public sealed class NesticleSound
    {
        public const int SoundRate = 22050;   // MAIN.CPP: int SOUNDRATE=22050;

        // duty cycles as % of period (0x10000): {87.5, 75.0, 58.5, 25.0}% truncated to int as the C++ initializer does
        private static readonly int[] Duty = { (int)(87.5 * 0x10000 / 100), (int)(75.0 * 0x10000 / 100), (int)(58.5 * 0x10000 / 100), (int)(25.0 * 0x10000 / 100) };

        private static readonly int[] Vols =
        {
            0, 0x200, 0x300, 0x380, 0x400, 0x480, 0x500, 0x580,
            0x600, 0x640, 0x680, 0x6C0, 0x700, 0x740, 0x780, 0x7C0
        };

        private class Channel
        {
            public readonly byte[] r = new byte[4];
            public byte enabled, noteon;
            public int dur, freq, K, deltaK, count;
            public short vol;

            // bitfields (Watcom/MSVC, LSB first)
            protected int PlaybackRate => r[0] & 0x0F;
            protected bool EnvelopeFixed => (r[0] & 0x10) != 0;
            private bool HoldNote => (r[0] & 0x20) != 0;
            protected int DutyCycle => r[0] >> 6;
            private int FreqRange => r[1] & 7;
            private bool FreqSelect => (r[1] & 8) != 0;
            private int FreqChangeSpeed => (r[1] >> 4) & 7;
            private bool FreqVariable => (r[1] & 0x80) != 0;
            private int GetFreq() => (r[2] | (r[3] << 8)) & 0x7FF;
            private int GetTime() => r[3] >> 3;

            public Channel() { Reset(); }

            public virtual void Mix16(short[] b, int nums) { }
            public virtual void SetVolume() { }

            public void StartNote()
            {
                if (GetFreq() == 0) { noteon = 0; return; }
                freq = 0x20000 / GetFreq();
                K = unchecked(freq << 16) / SoundRate;
                if (!HoldNote) dur = SoundRate * (GetTime() + 1) / 16;
                else dur = SoundRate / 2;
                if (enabled != 0) noteon = 1;
            }

            public void SetDeltaK()
            {
                if (!FreqVariable || FreqRange == 0 || FreqChangeSpeed == 0) { deltaK = 0; return; }
                deltaK = (FreqRange << 16) / SoundRate;
                if (FreqSelect) deltaK = -deltaK;
            }

            public void Write(int a, byte d)
            {
                r[a] = d;
                switch (a)
                {
                    case 0: SetVolume(); break;
                    case 1: SetDeltaK(); break;
                    case 2:
                    case 3: StartNote(); break;
                }
            }

            public void StopNote() => noteon = 0;
            public void Reset() { r[0] = r[1] = r[2] = r[3] = 0; deltaK = 0; StopNote(); enabled = 0; }
            public void SetEnable(bool x) { enabled = (byte)(x ? 1 : 0); if (enabled == 0) StopNote(); }
        }

        private sealed class SquareWave : Channel
        {
            public override void SetVolume() => vol = (short)Vols[PlaybackRate];

            public override void Mix16(short[] b, int nums)
            {
                if (noteon == 0) return;
                if (vol <= 0) { dur -= nums; if (dur < 0) noteon = 0; return; }

                int d = Duty[DutyCycle];
                for (int i = 0; i < nums && dur > 0; i++, dur--)
                {
                    b[i] = unchecked((short)(b[i] + ((count < d) ? vol : -vol)));
                    count += K; count &= 0xFFFF;

                    if ((dur & 255) == 0)
                    {
                        if (!EnvelopeFixed)
                            if (--vol < 0) return;
                        K += deltaK;
                    }
                }
                if (dur < 0) { noteon = 0; return; }
            }
        }

        private sealed class TriangleWave : Channel
        {
            public override void SetVolume() => vol = 0x600;

            public override void Mix16(short[] b, int nums)
            {
                if (noteon == 0) return;
                if (r[0] == 0) { dur -= nums; return; }

                for (int i = 0; i < nums && dur > 0; i++, dur--)
                {
                    count = unchecked(count + K);
                    int x;
                    if ((count & 0x8000) == 0) x = -0x4000 + (count & 0x7FFF);
                    else x = 0x4000 - (count & 0x7FFF);
                    b[i] = unchecked((short)(b[i] + x * vol / 0x4000));
                }
                if (dur < 0) { noteon = 0; return; }
            }
        }

        private readonly Channel[] ch = { new SquareWave(), new SquareWave(), new TriangleWave(), new Channel(), new Channel() };
        private readonly short[] one = new short[1];
        public byte EnableBits;
        /// <summary>BrokenNes channel mute mask (0x01 sq1 .. 0x10 DMC); not part of NESticle.</summary>
        public int ChannelMask = 0x1F;

        public NesticleSound() { Reset(); }

        /// <summary>nessound::write: a = address - $4000 ($00-$13).</summary>
        public void Write(int a, byte d) { if (a >= 0 && a < 0x14) ch[a / 4].Write(a & 3, d); }

        public void SetEnable(byte d)
        {
            EnableBits = d;
            for (int i = 0; i < 5; i++) ch[i].SetEnable((d & (1 << i)) != 0);
        }

        public void Reset() { for (int i = 0; i < 5; i++) ch[i].Reset(); }

        /// <summary>nessound::mix16 for one sample (the per-sample loops behave the same as NESticle's blocks).</summary>
        public short MixOne()
        {
            one[0] = 0;
            for (int i = 0; i < 5; i++)
                if (ch[i].enabled != 0)
                {
                    if ((ChannelMask & (1 << i)) != 0) ch[i].Mix16(one, 1);
                    else { short keep = one[0]; ch[i].Mix16(one, 1); one[0] = keep; }
                }
            return one[0];
        }
    }
}
