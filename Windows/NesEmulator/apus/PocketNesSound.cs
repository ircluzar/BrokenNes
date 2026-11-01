/*
 * PocketNesSound - PocketNES's NES-APU-to-Game-Boy-sound translator, ported to C#.
 * Source project: PocketNES (Game Boy Advance NES emulator) by Dwedit (Dan Weiss), and earlier authors (Loopy, FluBBa).
 * Version: github.com/Dwedit/PocketNES master, commit 9c07f3d1d2a26b957769c5cde942060cc9ea6e64 (2025-07-19).
 * File ported: src/sound.s (ARM assembly, 1,719 lines): register write handlers $4000-$4017, frame_sequencer_handler,
 *   the timer 2/3 "retrigger on the waveform boundary" trick, the DirectSound B DMC mixer (timer1interrupt/pcm_mix),
 *   the frequency multiplier (freq_mult 0xFFED408C, NTSC), noise NR43 table and triangle wave RAM.
 * URL: https://github.com/Dwedit/PocketNES/blob/master/src/sound.s
 * Copyright (C) Dwedit and the PocketNES authors. License: license not stated (PocketNES, Dwedit); documented exception.
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
 *
 * PocketNES synthesises nothing itself: it turns NES register writes into writes to the GBA's Game Boy sound chip.
 * Here that chip is BrokenNes2's own APU_GB (DMG model), clocked at 4.194304 MHz from the NES CPU cycles.
 * Quirks kept:
 *  - Volume/envelope/length/sweep are done in software (on PocketNES's own frame-sequencer timeline) and written as a
 *    fixed NRx2 volume; a GB volume only takes effect on a retrigger, so every volume or period change retriggers the
 *    channel (phase reset) - at the next waveform-period boundary (the GBA timer 2/3 IRQ trick) or immediately when
 *    either the old or new volume is 0.
 *  - NES periods re-quantised to GB 11-bit frequencies (x = (0x80080000 + 0xFFED408C*p) >> 20); period 0 and periods
 *    above ~1748 become x = 0 (the lowest GB note); triangle uses the same formula on the wave channel.
 *  - Triangle is on/off only (NR32 100% or mute), linear counter reload quirk, no volume.
 *  - Noise mapped through a 16-entry NR43 table; any volume change retriggers it (LFSR reset).
 *  - Envelope start: delay loaded and immediately decremented on the same quarter-frame.
 *  - $4017: next frame-sequencer step lands after 1 cycle or a full interval depending on cycle parity.
 *  - DMC: DirectSound B, one 8-bit sample per DMC bit at the DMC rate, +-3 per bit clamped to +-96, start level from
 *    $4011; $4011 alone makes no sound; samples shorter than 50 bytes never play; after a non-looping sample the
 *    output drops to 0. The GBA refill-buffer latency is not modelled (bits play when due).
 */
using System;

namespace NesEmulator
{
    public sealed class PocketNesSound
    {
        public const int SampleRate = 48000; // APU_GB's output rate
        private const long NesClock = 1789773, GbClock = 4194304, GbaClock = 16777216;
        private const int FrameInterval = 7457; // CPU cycles per frame-sequencer step (NTSC)

        private readonly Func<ushort, byte> read;
        private readonly NesEmulator.Gb.APU_GB gb = new NesEmulator.Gb.APU_GB(NesEmulator.Gb.GbModel.Dmg);
        private int mask = 0x1F;
        public int ChannelMask { get => mask; set { mask = value & 0x1F; WriteNr51(); } }

        // NES-side state (sound_state)
        private byte reg4000, reg4001, reg4004, reg4005, reg4008, reg400C, reg400E;
        private bool w4001, w4003, w4005, w4007, w400B, w400F;
        private int sq1Len, sq2Len, triLen, noiseLen, triLinear;
        private int sq1Env, sq1EnvDelay, sq1SweepDelay, sq2Env, sq2EnvDelay, sq2SweepDelay, noiseEnv, noiseEnvDelay;
        private int sq1Period, sq2Period, triPeriod;
        private byte channelEnables, frameMode, frameStep;
        private long frameNext;   // CPU cycle of next frame-sequencer step
        private long cpuTime;

        // GB-side shadows
        private byte nr12, nr22, nr32, nr42, nr43;
        private int gbSq1X, gbSq2X;
        private long gbTime;
        private long acc;
        private int fsAcc;
        private long sq1TrigTime, sq2TrigTime; private int sq1TimerPeriod = 65536, sq2TimerPeriod = 65536;
        private int sq1TimerNext = 65536, sq2TimerNext = 65536;
        private long sq1Due = -1, sq2Due = -1;

        // DMC (DirectSound B)
        private byte pcmCtrl; private int pcmStart, pcmLength, pcmCount = -1, pcmLevel; private ushort pcmAddr;
        private bool pcmTimerOn; private int pcmBits, pcmByte; private int pcmOut;
        private double pcmAcc; // GBA cycles accumulated toward the next DMC bit

        private readonly float[] outRing = new float[1 << 15];
        private int outHead, outCount;
        private readonly short[] gbBuf = new short[4096];

        private static readonly byte[] LengthTable = {
            0x0A,0xFE,0x14,0x02,0x28,0x04,0x50,0x06,0xA0,0x08,0x3C,0x0A,0x0E,0x0C,0x1A,0x0E,
            0x0C,0x10,0x18,0x12,0x30,0x14,0x60,0x16,0xC0,0x18,0x48,0x1A,0x10,0x1C,0x20,0x1E };
        private static readonly byte[] NoiseFreqs = { 0x00,0x01,0x05,0x11,0x15,0x17,0x25,0x33,0x34,0x35,0x37,0x45,0x47,0x55,0x65,0x75 };
        private static readonly ushort[] PcmFreq = { 0xf054,0xf216,0xf38d,0xf449,0xf588,0xf6b4,0xf7ba,0xf82a,0xf90b,0xfa25,0xfacd,0xfb51,0xfc1f,0xfce4,0xfd5e,0xfe06 };
        private static readonly byte[] TriangleWav = { 0x76,0x54,0x32,0x10,0x01,0x23,0x45,0x67,0x89,0xAB,0xCD,0xEF,0xFE,0xDC,0xBA,0x98 };

        public PocketNesSound(Func<ushort, byte> read) { this.read = read; HardwareReset(); }

        // Sound_hardware_reset
        private void HardwareReset()
        {
            gb.WriteRegister(0x26, 0x80);
            gb.WriteRegister(0x24, 0x77);
            WriteNr51();
            gb.WriteRegister(0x10, 0x08); gb.WriteRegister(0x11, 0); gb.WriteRegister(0x12, 0); nr12 = 0;
            gb.WriteRegister(0x16, 0x08); gb.WriteRegister(0x17, 0); nr22 = 0;
            gb.WriteRegister(0x13, 0); gb.WriteRegister(0x14, 0x80);
            gb.WriteRegister(0x18, 0); gb.WriteRegister(0x19, 0x80);
            gb.WriteRegister(0x1A, 0x40); gb.WriteRegister(0x1B, 0); gb.WriteRegister(0x1C, 0); nr32 = 0;
            for (int i = 0; i < 16; i++) gb.WriteRegister(0x30 + i, TriangleWav[i]);
            gb.WriteRegister(0x1A, 0x80);
            gb.WriteRegister(0x1D, 0); gb.WriteRegister(0x1E, 0x80);
            gb.WriteRegister(0x20, 0); gb.WriteRegister(0x21, 0); nr42 = 0;
            frameNext = FrameInterval;
        }

        private void WriteNr51() { int m = mask & 0x0F; gb.WriteRegister(0x25, (byte)(m | (m << 4))); }

        // ---- frequency translation (update_sqN_period_r2 / update_tri_period) ----
        private static int GbFreq(int p)
        {
            uint r = unchecked((uint)(0xFFED408Cu * (uint)p + 0x80080000u));
            int x = (int)r < 0 ? 0 : (int)(r >> 20);
            if (x == 0x800) x = 0x7FF;
            return x;
        }

        // ---- square 1 ----
        private void WriteSquare1()
        {
            sq1Due = -1;
            sq1TrigTime = gbTime; sq1TimerPeriod = sq1TimerNext;
            gb.WriteRegister(0x13, (byte)gbSq1X); gb.WriteRegister(0x14, (byte)(0x80 | (gbSq1X >> 8)));
        }
        private void ArmSquare1()
        {
            long el = gbTime - sq1TrigTime; long k = el / sq1TimerPeriod + 1;
            sq1Due = sq1TrigTime + k * sq1TimerPeriod;
        }
        private void UpdateSq1Period()
        {
            gbSq1X = GbFreq(sq1Period);
            sq1TimerNext = 32 * (2048 - gbSq1X);
            ArmSquare1();
            UpdateSq1Volume();
        }
        private void UpdateSq1Volume()
        {
            int v = ((reg4000 & 0x10) != 0 ? reg4000 : sq1Env) << 4 & 0xF0;
            UpdateSq1VolumeEnv(v);
        }
        private void UpdateSq1VolumeEnv(int v) { if (sq1Len == 0) v = 0; UpdateSq1VolumeR0(v); }
        private void UpdateSq1VolumeR0(int v)
        {
            int p = sq1Period, t = (reg4001 & 8) == 0 ? p + (p >> (reg4001 & 7)) : p;
            if (t >= 2048) v = 0;
            if (p <= 7) v = 0;
            int old = nr12; nr12 = (byte)v; gb.WriteRegister(0x12, (byte)v);
            if (v == old) return;
            if (v == 0 || old == 0) { WriteSquare1(); return; }
            ArmSquare1();
        }

        // ---- square 2 ----
        private void WriteSquare2()
        {
            sq2Due = -1;
            sq2TrigTime = gbTime; sq2TimerPeriod = sq2TimerNext;
            gb.WriteRegister(0x18, (byte)gbSq2X); gb.WriteRegister(0x19, (byte)(0x80 | (gbSq2X >> 8)));
        }
        private void ArmSquare2()
        {
            long el = gbTime - sq2TrigTime; long k = el / sq2TimerPeriod + 1;
            sq2Due = sq2TrigTime + k * sq2TimerPeriod;
        }
        private void UpdateSq2Period()
        {
            gbSq2X = GbFreq(sq2Period);
            sq2TimerNext = 32 * (2048 - gbSq2X);
            ArmSquare2();
            UpdateSq2Volume();
        }
        private void UpdateSq2Volume()
        {
            int v = ((reg4004 & 0x10) != 0 ? reg4004 : sq2Env) << 4;
            UpdateSq2VolumeEnv(v);
        }
        private void UpdateSq2VolumeEnv(int v) { if (sq2Len == 0) v = 0; UpdateSq2VolumeR0(v); }
        private void UpdateSq2VolumeR0(int v)
        {
            int p = sq2Period, t = (reg4005 & 8) == 0 ? p + (p >> (reg4005 & 7)) : p;
            if (t >= 2048) v = 0;
            if (p <= 7) v = 0;
            int old = nr22; nr22 = (byte)v; gb.WriteRegister(0x17, (byte)v);
            v &= 0xF0;
            if (v == old) return;
            if (v == 0 || old == 0) { WriteSquare2(); return; }
            ArmSquare2();
        }

        // ---- triangle ----
        private void UpdateTriVolume()
        {
            nr32 = (byte)(triLen != 0 && triLinear != 0 ? nr32 | 0x20 : nr32 & ~0x20);
            gb.WriteRegister(0x1C, nr32);
        }
        private void UpdateTriPeriod()
        {
            int x = GbFreq(triPeriod);
            gb.WriteRegister(0x1D, (byte)x); gb.WriteRegister(0x1E, (byte)(x >> 8));
        }

        // ---- noise ----
        private void UpdateNoiseVolume()
        {
            int v = ((reg400C & 0x10) != 0 ? reg400C : noiseEnv) << 4 & 0xF0;
            UpdateNoiseVolumeEnv(v);
        }
        private void UpdateNoiseVolumeEnv(int v) { if (noiseLen == 0) v = 0; UpdateNoiseVolumeR0(v); }
        private void UpdateNoiseVolumeR0(int v)
        {
            int old = nr42; nr42 = (byte)v; gb.WriteRegister(0x21, (byte)v);
            if (v == old) return;
            gb.WriteRegister(0x22, nr43); gb.WriteRegister(0x23, 0x80);
        }

        // ---- register writes ----
        /// <param name="dmcActive">NES-side DMC bytes remaining was non-zero before this write (from the stock APU underneath).</param>
        public void Write(ushort addr, byte v, bool dmcActive = false)
        {
            switch (addr)
            {
                case 0x4000: reg4000 = v; gb.WriteRegister(0x11, (byte)(v & 0xC0)); UpdateSq1Volume(); break;
                case 0x4001: reg4001 = v; w4001 = true; break;
                case 0x4002: sq1Period = (sq1Period & 0x700) | v; UpdateSq1Period(); break;
                case 0x4003:
                    w4003 = true;
                    sq1Len = (channelEnables & 1) != 0 ? LengthTable[v >> 3] : 0;
                    sq1Period = (sq1Period & 0xFF) | ((v & 7) << 8);
                    UpdateSq1Period();
                    WriteSquare1();
                    break;
                case 0x4004: reg4004 = v; gb.WriteRegister(0x16, (byte)(v & 0xC0)); UpdateSq2Volume(); break;
                case 0x4005: reg4005 = v; w4005 = true; break;
                case 0x4006: sq2Period = (sq2Period & 0x700) | v; UpdateSq2Period(); break;
                case 0x4007:
                    w4007 = true;
                    sq2Len = (channelEnables & 2) != 0 ? LengthTable[v >> 3] : 0;
                    sq2Period = (sq2Period & 0xFF) | ((v & 7) << 8);
                    UpdateSq2Period();
                    WriteSquare2();
                    break;
                case 0x4008: reg4008 = v; break;
                case 0x400A: triPeriod = (triPeriod & 0x700) | v; UpdateTriPeriod(); break;
                case 0x400B:
                    w400B = true;
                    triLen = (channelEnables & 4) != 0 ? LengthTable[v >> 3] : 0;
                    triPeriod = (triPeriod & 0xFF) | ((v & 7) << 8);
                    UpdateTriVolume(); UpdateTriPeriod();
                    break;
                case 0x400C: reg400C = v; UpdateNoiseVolume(); break;
                case 0x400E:
                    reg400E = v;
                    nr43 = (byte)(NoiseFreqs[v & 15] | ((v & 0x80) != 0 ? 8 : 0));
                    gb.WriteRegister(0x22, nr43); gb.WriteRegister(0x23, 0);
                    break;
                case 0x400F:
                    w400F = true;
                    noiseLen = (channelEnables & 8) != 0 ? LengthTable[v >> 3] : 0;
                    UpdateNoiseVolume();
                    break;
                case 0x4010: pcmCtrl = v; break;   // timer 0 rate follows pcmCtrl & 15 directly
                case 0x4011: pcmLevel = ((v + v) & 0xFE) - 0x80; break;
                case 0x4012: pcmStart = 0xC000 | (v << 6); break;
                case 0x4013: pcmLength = (v << 4) + 1; break;
                case 0x4015: Write4015(v, dmcActive); break;
                case 0x4017:
                {
                    frameMode = (byte)(v & 0xC0);
                    frameStep = (byte)((frameMode & 0x80) != 0 ? 0 : 1);
                    long until = frameNext - cpuTime; if (until < 0) until = 0;
                    frameNext = cpuTime + ((until & 1) != 0 ? 1 : FrameInterval);
                    break;
                }
            }
        }

        private void Write4015(byte v, bool dmcActive)
        {
            int old = (channelEnables & 0x0F) | (dmcActive ? 0x10 : 0);
            channelEnables = v;
            int changed = old ^ v;
            if (changed == 0) return;
            int off = changed & old;
            if ((off & 1) != 0) { sq1Len = 0; UpdateSq1VolumeR0(0); }
            if ((off & 2) != 0) { sq2Len = 0; UpdateSq2VolumeR0(0); }
            if ((off & 4) != 0) { triLen = 0; nr32 &= unchecked((byte)~0x20); gb.WriteRegister(0x1C, nr32); }
            if ((off & 8) != 0) { noiseLen = 0; UpdateNoiseVolumeR0(0); }
            if ((changed & 0x10) == 0) return;
            if ((v & 0x10) == 0) { pcmCount = -1; pcmTimerOn = false; pcmOut = 0; return; }
            pcmAddr = (ushort)pcmStart; pcmCount = pcmLength;
            if (pcmCount >= 50) { pcmTimerOn = true; pcmBits = 0; }
        }

        // ---- frame sequencer (frame_sequencer_handler) ----
        private void Envelope(byte reg, ref bool written, ref int env, ref int delay, Action<int> setVol)
        {
            int r2 = reg & 15;
            if (written) { written = false; delay = r2; env = 15; }
            delay--;
            if (delay >= 0) return;
            delay = r2;
            if (((reg & 0x20) | env) == 0) return;
            env = (env - 1) & 15;
            if ((reg & 0x10) == 0) setVol(env << 4);
        }

        private void Sweep(byte reg, ref bool written, ref int delay, ref int period, bool sq1)
        {
            int r0 = delay;
            if (((reg & 0x87) | r0) == 0) return;
            delay = (r0 == 0 || written) ? (reg >> 4) & 7 : r0 - 1;
            written = false;
            if (r0 != 0) return;
            int shift = reg & 7;
            if (shift == 0 || (reg & 0x80) == 0) return;
            int p = period; if (p < 8) return;
            int off = p >> shift;
            if ((reg & 8) != 0) off = sq1 ? ~off : -off;
            p += off;
            if (p >= 0x800) return;
            period = p;
            if (sq1) UpdateSq1Period(); else UpdateSq2Period();
        }

        private void FrameSequencer()
        {
            Envelope(reg4000, ref w4003, ref sq1Env, ref sq1EnvDelay, UpdateSq1VolumeEnv);
            Envelope(reg4004, ref w4007, ref sq2Env, ref sq2EnvDelay, UpdateSq2VolumeEnv);
            Envelope(reg400C, ref w400F, ref noiseEnv, ref noiseEnvDelay, UpdateNoiseVolumeEnv);
            bool wasW = w400B;
            if ((reg4008 & 0x80) == 0) w400B = false;
            if (wasW) triLinear = (reg4008 & 0x7F) + 1;
            if (triLinear != 0) triLinear--;
            UpdateTriVolume();

            int step = frameStep & 3; frameStep++;
            int interval = FrameInterval;
            if (step == 1) interval -= 2;
            else if (step == 3) { if ((frameMode & 0x80) != 0) interval = interval * 2 - 6; }
            else
            {
                if ((reg4000 & 0x20) == 0 && sq1Len > 0) { sq1Len--; UpdateSq1Volume(); }
                if ((reg4004 & 0x20) == 0 && sq2Len > 0) { sq2Len--; UpdateSq2Volume(); }
                if ((reg4008 & 0x80) == 0 && triLen > 0) { triLen--; UpdateTriVolume(); }
                if ((reg400C & 0x20) == 0 && noiseLen > 0) { noiseLen--; UpdateNoiseVolume(); }
                Sweep(reg4001, ref w4001, ref sq1SweepDelay, ref sq1Period, true);
                Sweep(reg4005, ref w4005, ref sq2SweepDelay, ref sq2Period, false);
            }
            frameNext += interval;
        }

        // ---- DirectSound B DMC (timer1interrupt / pcm_mix) ----
        private void PcmBit()
        {
            if (!pcmTimerOn) return;
            if (pcmBits == 0)
            {
                if (pcmCount < 0) { pcmOut = 0; return; }
                pcmCount--;
                if (pcmCount < 0)
                {
                    if ((pcmCtrl & 0x40) != 0 && pcmLength > 0) { pcmCount = pcmLength - 1; pcmAddr = (ushort)pcmStart; }
                    else { pcmOut = 0; pcmTimerOn = false; return; }   // buffer cleared, timer 0 stopped
                }
                pcmByte = read(pcmAddr); pcmAddr++;
                pcmBits = 8;
            }
            pcmLevel += (pcmByte & 1) != 0 ? 3 : -3;
            if (pcmLevel > 96) pcmLevel = 96;
            if (pcmLevel < -96) pcmLevel = -96;
            pcmByte >>= 1; pcmBits--;
            pcmOut = pcmLevel;
        }

        // ---- clocking ----
        public void Clock(int cpuCycles)
        {
            for (int c = 0; c < cpuCycles; c++)
            {
                cpuTime++;
                if (cpuTime >= frameNext) FrameSequencer();
            }
            if (pcmTimerOn)
            {
                pcmAcc += cpuCycles * (double)GbaClock / NesClock;
                double per = 0x10000 - PcmFreq[pcmCtrl & 15];
                while (pcmAcc >= per && pcmTimerOn) { pcmAcc -= per; PcmBit(); }
                if (!pcmTimerOn) pcmAcc = 0;
            }
            acc += cpuCycles * GbClock;
            int t = (int)(acc / NesClock); acc -= t * NesClock;
            while (t > 0)
            {
                int chunk = t;
                if (sq1Due >= 0 && sq1Due - gbTime < chunk) chunk = (int)Math.Max(0, sq1Due - gbTime);
                if (sq2Due >= 0 && sq2Due - gbTime < chunk) chunk = (int)Math.Max(0, sq2Due - gbTime);
                if (chunk > 0)
                {
                    int fsLeft = 8192 - fsAcc; if (chunk > fsLeft) chunk = fsLeft;
                    gb.Tick(chunk); gbTime += chunk; t -= chunk; fsAcc += chunk;
                    if (fsAcc >= 8192) { fsAcc -= 8192; gb.FrameSequencerStep(); }
                }
                if (sq1Due >= 0 && gbTime >= sq1Due) WriteSquare1();
                if (sq2Due >= 0 && gbTime >= sq2Due) WriteSquare2();
            }
            int n;
            float dmc = (mask & 0x10) != 0 ? pcmOut * (7000f / 128f / 32768f) : 0f;
            while ((n = gb.ReadSamples(gbBuf)) > 0)
                for (int i = 0; i < n; i += 2)
                    Push((gbBuf[i] + gbBuf[i + 1]) * (0.5f / 32768f) + dmc);
        }

        private void Push(float s)
        {
            if (outCount == outRing.Length) { outHead = (outHead + 1) & (outRing.Length - 1); outCount--; }
            outRing[(outHead + outCount) & (outRing.Length - 1)] = s;
            outCount++;
        }

        public int Queued => outCount;

        public int Read(Span<float> dest)
        {
            int n = Math.Min(outCount, dest.Length);
            for (int i = 0; i < n; i++) dest[i] = outRing[(outHead + i) & (outRing.Length - 1)];
            outHead = (outHead + n) & (outRing.Length - 1);
            outCount -= n;
            return n;
        }

        public void ClearOutput() { outHead = outCount = 0; }
    }
}
