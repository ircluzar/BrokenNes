/*
 * NesterApu - the nester family's NES APU (nofrendo nes_apu.c v1.33 + TAKEDA toshiya / YANO takashi changes), two builds:
 *
 *  NesterJ  (PSP, v1.11):  http://rukapsp.genin.jp/PSPSoftware/archive/NesterJ_v1_11src.zip  nes/apu/nes_apu.c + nes_apu.h + nes_apu_wrapper.c,
 *           soundmanager.c, pg.h, sound.h. Built WITHOUT APU_YANO (nes_apu.h: "//#define APU_YANO"), with REALTIME_NOISE and APU_OVERSAMPLE.
 *  NesterDC (Dreamcast, 7.1.0): https://github.com/pqrs-org/NesterDC/archive/refs/tags/v7.1.0.zip  nester/nes/apu/nes_apu.c + nes_apu.h +
 *           nes_apu_wrapper.cpp, dreamcast/dc_sound_mgr.h. Built WITH APU_YANO and REALTIME_NOISE, ideal triangle off (apu_setmode FALSE).
 *
 * Authors: Matthew Conte (Nofrendo, (c) 1998-2000), Darren Ranalli (nester changes), TAKEDA toshiya (ExSound, $4015 sync), YANO takashi
 * (APU_YANO sound code, $4017 bit 7, DC cancel), the NesterJ (PSP) and NesterDC (pqrs.org) porters.
 * License: GNU Library General Public License version 2 (the file header). Its legend: "Any permitted reproduction of these routines, in
 * whole or in part, must bear this legend."  Nofrendo (c) 1998-2000 Matthew Conte (matt@conte.com)
 * An art instrument (APU harvest climb): see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md.
 *
 * Flaws kept on purpose (both builds):
 *  - Timestamp queue: every write is queued with the CPU cycle count and applied when the sample clock (elapsed_cycles) passes it. The
 *    sample clock advances APU_FROM_FIXED(cycle_rate) = 40 whole cycles a sample (the real figure is 40.58), so a chunk covers fewer
 *    cycles than really ran; at the end of each chunk the clock is resynced to the CPU and the writes of the gap all land on the next
 *    chunk's first sample.
 *  - Audio is rendered in chunks when the sound card's playback moves on: NesterJ renders 512-sample PSP banks (PGA_SAMPLES),
 *    NesterDC renders a 2048-sample half of its 0x2000-byte double buffer at once (~46 ms of latency, writes spread over ~2.8 frames).
 *  - No frame sequencer: envelopes, sweeps, length and linear counters run per SAMPLE against tables built from samples-per-frame,
 *    multiplied by 5 (YANO's $4017 bit 7: apu_cnt_rate 5 for 4-step, 4 for 5-step), so 5-step mode simply makes everything 20% slower.
 *  - Sweep: channel 0 ramps up with freq += ~(freq >> shifts) (sweep_complement), channel 1 with freq -= freq >> shifts; the mute test is
 *    the freq_limit table. Noise period table is nofrendo's old one (4..4068), taps 0x40 / 0x02, one global shift register.
 *  - $4015 writes: a disabled channel is cut, but enabling does NOT start a channel; only the next $4003/$4007/$400B/$400F write with the
 *    enable bit set does. DMC "enabled" is set at write time (the "bodge for timestamp queue").
 *  - Triangle write latency hack: 228 / 40 = 5 samples before the linear counter starts.
 *  - DMC delta steps of 2, clamped to 1..0x7D; its IRQ/cycle steal are left to the stock APU here (the game-facing side).
 *  - Expansion sound (VRC6/VRC7/FDS/MMC5/N106/FME7 ExSound) is NOT ported: only the 2A03 channels.
 *  - 44100 Hz (both ports' default/fixed rate).
 * NesterJ (non-YANO) only:
 *  - Levels << 8 averaged per sample (total / num_times, integer division), each channel decaying with x -= x >> 7 every sample;
 *    staircase triangle accumulator (+-2<<8 per step, unbounded) with a 21/16 gain; noise and DMC with 13/16. cycle_rate is the
 *    hard-coded 2659740. The filter block is commented out in the source: no filter at all, plain sum, 16-bit clip.
 * NesterDC (APU_YANO) only:
 *  - Box-filtered (area-weighted, double precision) rectangle, noise and DMC outputs; the triangle is the folded 5-bit staircase
 *    (adder ^ 0x1F on the way down) weighted the same way, *512, 21/16 gain. No decay. Envelope: ++env_vol then wrap (loop) or clamp.
 *  - $4011 sets the DMC level (no delta add); an idle DMC outputs its level directly.
 *  - "Cancel DC offset" (Dec.17.2000): a running max/min tracker whose midpoint is averaged over 1024 samples and subtracted.
 *  - cycle_rate computed as (int)(1789772.5 * 65536 / 44100) = 2659741.
 */
using System;

namespace NesEmulator
{
    public sealed class NesterApu
    {
        public const int SampleRate = 44100;
        private const double BaseFreq = 1789772.5;   // APU_BASEFREQ
        private const int QueueSize = 8192;

        private static readonly byte[] VblLength = { 5, 127, 10, 1, 19, 2, 40, 3, 80, 4, 30, 5, 7, 6, 13, 7, 6, 8, 12, 9, 24, 10, 48, 11, 96, 12, 36, 13, 8, 14, 16, 15 };
        private static readonly int[] FreqLimit = { 0x3FF, 0x555, 0x666, 0x71C, 0x787, 0x7C1, 0x7E0, 0x7F0 };
        private static readonly int[] NoiseFreq = { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };
        private static readonly int[] DmcClocks = { 428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 85, 72, 54 };
        private static readonly int[] DutyLut = { 2, 4, 8, 12 };

        private sealed class Rect
        {
            public byte[] Regs = new byte[4]; public bool Enabled, Holdnote, FixedEnvelope, SweepOn, SweepInc, SweepComplement;
            public int Phaseacc, Freq, OutputVol, Volume, SweepPhase, SweepDelay, SweepShifts, FreqLimit, EnvPhase, EnvDelay, EnvVol, VblLength, Adder, DutyFlip;
        }
        private sealed class Tri
        {
            public byte[] Regs = new byte[3]; public bool Enabled, Holdnote, CounterStarted;
            public int Phaseacc, Freq, OutputVol, Adder, WriteLatency, VblLength, LinearLength;
        }
        private sealed class Noi
        {
            public byte[] Regs = new byte[3]; public bool Enabled, Holdnote, FixedEnvelope;
            public int Phaseacc, Freq, OutputVol, EnvPhase, EnvDelay, EnvVol, VblLength, Volume, XorTap;
        }
        private sealed class Dmc
        {
            public byte[] Regs = new byte[4]; public bool Enabled, Looping, IrqGen, IrqOccurred;
            public int Phaseacc, Freq, OutputVol, Address, CachedAddr, DmaLength, CachedDmaLength, CurByte;
        }

        private readonly Func<ushort, byte> read;
        private readonly bool yano;
        private readonly int chunk;
        private Rect[] rect = { new Rect(), new Rect() };
        private Tri tri = new Tri();
        private Noi noise = new Noi();
        private Dmc dmc = new Dmc();
        private int cycleRate, enableReg, cntRate = 5, sreg = 0x4000, noiseBit;
        private double dcAve, dcMax, dcMin;
        private readonly int[] decayLut = new int[16], vblLut = new int[32], triLengthLut = new int[128];

        // timestamp queue + clocks
        private readonly long[] qTs = new long[QueueSize];
        private readonly ushort[] qAddr = new ushort[QueueSize];
        private readonly byte[] qVal = new byte[QueueSize];
        private int qHead, qTail;
        private long cpuCycles, elapsedCycles;
        private double chunkCycles;
        private int mask = 0x1F;

        private readonly float[] outRing = new float[16384];
        private int outHead, outCount;

        public int ChannelMask { get => mask; set => mask = value & 0x1F; }
        public int Queued => outCount;

        /// <param name="yano">false = NesterJ (PSP, plain nofrendo oversampling), true = NesterDC (Dreamcast, APU_YANO).</param>
        public NesterApu(Func<ushort, byte> read, bool yano)
        {
            this.read = read; this.yano = yano;
            chunk = yano ? 2048 : 512;
            cycleRate = yano ? (int)(BaseFreq * 65536.0 / (float)SampleRate) : 2659740;
            int numSamples = SampleRate / 60;
            for (int i = 0; i < 16; i++) decayLut[i] = numSamples * (i + 1) * 5;
            for (int i = 0; i < 32; i++) vblLut[i] = VblLength[i] * numSamples * 5;
            for (int i = 0; i < 128; i++) triLengthLut[i] = numSamples * i * 5;
            Reset();
        }

        public void Reset()
        {
            rect = new[] { new Rect { SweepComplement = true }, new Rect { SweepComplement = false } };
            tri = new Tri(); noise = new Noi(); dmc = new Dmc();
            qHead = qTail = 0; elapsedCycles = cpuCycles = 0; chunkCycles = 0; enableReg = 0;
            for (ushort a = 0x4000; a <= 0x4013; a++) RegWrite(a, 0);
            RegWrite(0x4015, 0);
            cntRate = 5;
        }

        /// <summary>apu_write (+ nester's NES_APU_Write): queued with the CPU timestamp; $4015 sets the DMC's "enabled" at once.</summary>
        public void Write(ushort address, byte value)
        {
            if (address == 0x4015) dmc.Enabled = (value & 0x10) != 0;
            if (address < 0x4000 || address > 0x4017 || address == 0x4014 || address == 0x4016) return;
            if (((qHead + 1) & (QueueSize - 1)) == qTail) return;   // full (the original wraps over itself; dropping is the safe reading)
            qTs[qHead] = cpuCycles; qAddr[qHead] = address; qVal[qHead] = value;
            qHead = (qHead + 1) & (QueueSize - 1);
        }

        public void Clock(int cycles)
        {
            cpuCycles += cycles;
            chunkCycles += cycles;
            double per = chunk * BaseFreq / SampleRate;
            while (chunkCycles >= per) { chunkCycles -= per; Process(chunk); }
        }

        // ---- NesterJ (non-YANO) channels ----
        private int RectJ(Rect c)
        {
            c.OutputVol -= c.OutputVol >> 7;
            if (!c.Enabled || c.VblLength <= 0) return c.OutputVol;
            if (!c.Holdnote) c.VblLength -= cntRate;
            c.EnvPhase -= 4 * cntRate;
            while (c.EnvPhase < 0)
            {
                c.EnvPhase += c.EnvDelay;
                if (c.Holdnote) c.EnvVol = (c.EnvVol + 1) & 0x0F;
                else if (c.EnvVol < 0x0F) c.EnvVol++;
            }
            if (c.Freq < 8 || (!c.SweepInc && c.Freq > c.FreqLimit)) return c.OutputVol;
            Sweep(c);
            c.Phaseacc -= cycleRate;
            if (c.Phaseacc >= 0) return c.OutputVol;
            int output = c.FixedEnvelope ? c.Volume << 8 : (c.EnvVol ^ 0x0F) << 8;
            int numTimes = 0, total = 0;
            while (c.Phaseacc < 0)
            {
                c.Phaseacc += (c.Freq + 1) << 16;
                c.Adder = (c.Adder + 1) & 0x0F;
                if (c.Adder < c.DutyFlip) total += output; else total -= output;
                numTimes++;
            }
            c.OutputVol = total / numTimes;
            return c.OutputVol;
        }

        private void Sweep(Rect c)
        {
            if (!(c.SweepOn && c.SweepShifts != 0)) return;
            c.SweepPhase -= 2 * cntRate;
            while (c.SweepPhase < 0)
            {
                c.SweepPhase += c.SweepDelay;
                if (c.SweepInc) { if (c.SweepComplement) c.Freq += ~(c.Freq >> c.SweepShifts); else c.Freq -= c.Freq >> c.SweepShifts; }
                else c.Freq += c.Freq >> c.SweepShifts;
                if (c.SweepDelay <= 0) break;   // guard: a zero delay would spin forever (decay_lut entries are never 0 here)
            }
        }

        private int TriOut => (tri.OutputVol * 21) >> 4;

        private bool TriCounters()
        {
            var c = tri;
            if (c.CounterStarted)
            {
                if (c.LinearLength > 0) c.LinearLength -= 4 * cntRate;
                if (c.VblLength > 0 && !c.Holdnote) c.VblLength -= cntRate;
            }
            else if (!c.Holdnote && c.WriteLatency != 0)
            {
                if (--c.WriteLatency == 0) c.CounterStarted = true;
            }
            return !(c.LinearLength <= 0 || c.Freq < (4 << 16));
        }

        private int TriJ()
        {
            var c = tri;
            c.OutputVol -= c.OutputVol >> 7;
            if (!c.Enabled || c.VblLength <= 0) return TriOut;
            if (!TriCounters()) return TriOut;
            c.Phaseacc -= cycleRate;
            while (c.Phaseacc < 0)
            {
                c.Phaseacc += c.Freq;
                c.Adder = (c.Adder + 1) & 0x1F;
                if ((c.Adder & 0x10) != 0) c.OutputVol -= 2 << 8; else c.OutputVol += 2 << 8;
            }
            return TriOut;
        }

        private int Shift15(int xorTap)
        {
            int bit0 = sreg & 1, tap = (sreg & xorTap) != 0 ? 1 : 0, bit14 = bit0 ^ tap;
            sreg >>= 1; sreg |= bit14 << 14;
            return bit0 ^ 1;
        }

        private int NoiseOut => (noise.OutputVol * 13) >> 4;

        private void NoiseEnvJ(Noi c)
        {
            if (!c.Holdnote) c.VblLength -= cntRate;
            c.EnvPhase -= 4 * cntRate;
            while (c.EnvPhase < 0)
            {
                c.EnvPhase += c.EnvDelay;
                if (c.Holdnote) c.EnvVol = (c.EnvVol + 1) & 0x0F;
                else if (c.EnvVol < 0x0F) c.EnvVol++;
            }
        }

        private int NoiseJ()
        {
            var c = noise;
            c.OutputVol -= c.OutputVol >> 7;
            if (!c.Enabled || c.VblLength <= 0) return NoiseOut;
            NoiseEnvJ(c);
            c.Phaseacc -= cycleRate;
            if (c.Phaseacc >= 0) return NoiseOut;
            int outvol = c.FixedEnvelope ? c.Volume << 8 : (c.EnvVol ^ 0x0F) << 8;
            int numTimes = 0, total = 0;
            while (c.Phaseacc < 0)
            {
                c.Phaseacc += c.Freq;
                if (Shift15(c.XorTap) != 0) total += outvol; else total -= outvol;
                numTimes++;
            }
            c.OutputVol = total / numTimes;
            return NoiseOut;
        }

        private void DmcReload() { dmc.Address = dmc.CachedAddr; dmc.DmaLength = dmc.CachedDmaLength; dmc.IrqOccurred = false; }

        private int DmcOut => (dmc.OutputVol * 13) >> 4;

        private void DmcFetch(Dmc c)
        {
            c.CurByte = read((ushort)c.Address);   // the cycle steal is APU_FIX's job here
            if (c.Address == 0xFFFF) c.Address = 0x8000; else c.Address++;
        }

        private int DmcJ()
        {
            var c = dmc;
            c.OutputVol -= c.OutputVol >> 7;
            if (c.DmaLength != 0)
            {
                c.Phaseacc -= cycleRate;
                while (c.Phaseacc < 0)
                {
                    c.Phaseacc += c.Freq;
                    if ((c.DmaLength & 7) == 0) DmcFetch(c);
                    if (--c.DmaLength == 0)
                    {
                        if (c.Looping) DmcReload();
                        else { if (c.IrqGen) c.IrqOccurred = true; c.Enabled = false; break; }
                    }
                    int deltaBit = (c.DmaLength & 7) ^ 7;
                    if ((c.CurByte & (1 << deltaBit)) != 0) { if (c.Regs[1] < 0x7D) { c.Regs[1] += 2; c.OutputVol += 2 << 8; } }
                    else if (c.Regs[1] > 1) { c.Regs[1] -= 2; c.OutputVol -= 2 << 8; }
                }
            }
            return DmcOut;
        }

        // ---- NesterDC (APU_YANO) channels ----
        private void EnvYano(ref int envPhase, int envDelay, ref int envVol, bool holdnote, ref int vblLength)
        {
            envPhase -= 4 * cntRate;
            while (envPhase < 0) { envPhase += envDelay; ++envVol; }
            if (holdnote) envVol &= 0xF;
            else { vblLength -= cntRate; if (envVol > 0xF) envVol = 0xF; }
        }

        private int RectY(Rect c)
        {
            if (!c.Enabled || c.VblLength <= 0) return c.OutputVol;
            EnvYano(ref c.EnvPhase, c.EnvDelay, ref c.EnvVol, c.Holdnote, ref c.VblLength);
            if (c.Freq < 8 || (!c.SweepInc && c.Freq > c.FreqLimit)) return c.OutputVol;
            Sweep(c);
            int output = c.FixedEnvelope ? c.Volume << 8 : (c.EnvVol ^ 0x0F) << 8;
            double sw = c.Phaseacc; if (sw > cycleRate) sw = cycleRate;
            double total = c.Adder < c.DutyFlip ? sw : -sw;
            c.Phaseacc -= cycleRate;
            while (c.Phaseacc < 0)
            {
                c.Phaseacc += (c.Freq + 1) << 16;
                c.Adder = (c.Adder + 1) & 0x0F;
                sw = (c.Freq + 1) << 16;
                if (c.Phaseacc > 0) sw -= c.Phaseacc;
                total += c.Adder < c.DutyFlip ? sw : -sw;
            }
            c.OutputVol = (int)Math.Floor(output * total / cycleRate + 0.5);
            return c.OutputVol;
        }

        private int TriY()
        {
            var c = tri;
            if (!c.Enabled || c.VblLength <= 0) return TriOut;
            if (!TriCounters()) return TriOut;
            // ideal_triangle is FALSE in NesterDC (apu_setmode(APUMODE_IDEAL_TRIANGLE, FALSE)): the folded staircase
            double sw = c.Phaseacc; if (sw > cycleRate) sw = cycleRate;
            double total = ((((c.Adder & 0x10) != 0) ? 0x1F : 0) ^ c.Adder) * sw;
            c.Phaseacc -= cycleRate;
            while (c.Phaseacc < 0)
            {
                c.Phaseacc += c.Freq;
                c.Adder = (c.Adder + 1) & 0x1F;
                sw = c.Freq;
                if (c.Phaseacc > 0) sw -= c.Phaseacc;
                total += ((((c.Adder & 0x10) != 0) ? 0x1F : 0) ^ c.Adder) * sw;
            }
            c.OutputVol = (int)Math.Floor(total * 512 / cycleRate + 0.5);
            return TriOut;
        }

        private int NoiseY()
        {
            var c = noise;
            if (!c.Enabled || c.VblLength <= 0) return NoiseOut;
            EnvYano(ref c.EnvPhase, c.EnvDelay, ref c.EnvVol, c.Holdnote, ref c.VblLength);
            int outvol = c.FixedEnvelope ? c.Volume << 8 : (c.EnvVol ^ 0x0F) << 8;
            double sw = c.Phaseacc; if (sw > cycleRate) sw = cycleRate;
            double total = noiseBit != 0 ? sw : -sw;
            c.Phaseacc -= cycleRate;
            while (c.Phaseacc < 0)
            {
                c.Phaseacc += c.Freq;
                noiseBit = Shift15(c.XorTap);
                sw = c.Freq;
                if (c.Phaseacc > 0) sw -= c.Phaseacc;
                total += noiseBit != 0 ? sw : -sw;
            }
            c.OutputVol = (int)Math.Floor(outvol * total / cycleRate + 0.5);
            return NoiseOut;
        }

        private int DmcY()
        {
            var c = dmc;
            if (c.DmaLength != 0)
            {
                double sw = c.Phaseacc; if (sw > cycleRate) sw = cycleRate;
                double total = (c.Regs[1] << 8) * sw;
                c.Phaseacc -= cycleRate;
                while (c.Phaseacc < 0)
                {
                    c.Phaseacc += c.Freq;
                    if ((c.DmaLength & 7) == 0) DmcFetch(c);
                    if (--c.DmaLength == 0)
                    {
                        if (c.Looping) DmcReload();
                        else
                        {
                            if (c.IrqGen) c.IrqOccurred = true;
                            sw = c.Freq - c.Phaseacc;
                            total += (c.Regs[1] << 8) * sw;
                            while (c.Phaseacc < 0) c.Phaseacc += c.Freq;
                            c.Enabled = false;
                            break;
                        }
                    }
                    int deltaBit = (c.DmaLength & 7) ^ 7;
                    if ((c.CurByte & (1 << deltaBit)) != 0) { if (c.Regs[1] < 0x7D) c.Regs[1] += 2; }
                    else if (c.Regs[1] > 1) c.Regs[1] -= 2;
                    sw = c.Freq;
                    if (c.Phaseacc > 0) sw -= c.Phaseacc;
                    total += (c.Regs[1] << 8) * sw;
                }
                c.OutputVol = (int)Math.Floor(total / cycleRate + 0.5);
            }
            else c.OutputVol = c.Regs[1] << 8;
            return DmcOut;
        }

        // apu_regwrite
        private void RegWrite(ushort address, byte value)
        {
            int ch;
            switch (address)
            {
                case 0x4000: case 0x4004:
                    { var r = rect[(address & 4) != 0 ? 1 : 0]; r.Regs[0] = value; r.Volume = value & 0x0F; r.EnvDelay = decayLut[value & 0x0F];
                      r.Holdnote = (value & 0x20) != 0; r.FixedEnvelope = (value & 0x10) != 0; r.DutyFlip = DutyLut[value >> 6]; }
                    break;
                case 0x4001: case 0x4005:
                    { var r = rect[(address & 4) != 0 ? 1 : 0]; r.Regs[1] = value; r.SweepOn = (value & 0x80) != 0; r.SweepShifts = value & 7;
                      r.SweepDelay = decayLut[(value >> 4) & 7]; r.SweepInc = (value & 0x08) != 0; r.FreqLimit = FreqLimit[value & 7]; }
                    break;
                case 0x4002: case 0x4006:
                    { var r = rect[(address & 4) != 0 ? 1 : 0]; r.Regs[2] = value; r.Freq = (r.Freq & ~0xFF) | value; }
                    break;
                case 0x4003: case 0x4007:
                    { ch = (address & 4) != 0 ? 1 : 0; var r = rect[ch]; r.Regs[3] = value; r.VblLength = vblLut[value >> 3]; r.EnvVol = 0;
                      r.Freq = ((value & 7) << 8) | (r.Freq & 0xFF); r.Adder = 0; if ((enableReg & (1 << ch)) != 0) r.Enabled = true; }
                    break;
                case 0x4008:
                    tri.Regs[0] = value; tri.Holdnote = (value & 0x80) != 0;
                    if (!tri.CounterStarted && tri.VblLength > 0) tri.LinearLength = triLengthLut[value & 0x7F];
                    break;
                case 0x400A:
                    tri.Regs[1] = value; tri.Freq = ((((tri.Regs[2] & 7) << 8) + value) + 1) << 16;
                    break;
                case 0x400B:
                    tri.Regs[2] = value;
                    tri.WriteLatency = 228 / (cycleRate >> 16);
                    tri.Freq = ((((value & 7) << 8) + tri.Regs[1]) + 1) << 16;
                    tri.VblLength = vblLut[value >> 3];
                    tri.CounterStarted = false;
                    tri.LinearLength = triLengthLut[tri.Regs[0] & 0x7F];
                    if ((enableReg & 0x04) != 0) tri.Enabled = true;
                    break;
                case 0x400C:
                    noise.Regs[0] = value; noise.EnvDelay = decayLut[value & 0x0F]; noise.Holdnote = (value & 0x20) != 0;
                    noise.FixedEnvelope = (value & 0x10) != 0; noise.Volume = value & 0x0F;
                    break;
                case 0x400E:
                    noise.Regs[1] = value; noise.Freq = NoiseFreq[value & 0x0F] << 16; noise.XorTap = (value & 0x80) != 0 ? 0x40 : 0x02;
                    break;
                case 0x400F:
                    noise.Regs[2] = value; noise.VblLength = vblLut[value >> 3]; noise.EnvVol = 0;
                    if ((enableReg & 0x08) != 0) noise.Enabled = true;
                    break;
                case 0x4010:
                    dmc.Regs[0] = value; dmc.Freq = DmcClocks[value & 0x0F] << 16; dmc.Looping = (value & 0x40) != 0;
                    if ((value & 0x80) != 0) dmc.IrqGen = true; else { dmc.IrqGen = false; dmc.IrqOccurred = false; }
                    break;
                case 0x4011:
                    value &= 0x7F;
                    if (!yano) dmc.OutputVol += (value - dmc.Regs[1]) << 8;
                    dmc.Regs[1] = value;
                    break;
                case 0x4012:
                    dmc.Regs[2] = value; dmc.CachedAddr = 0xC000 + (ushort)(value << 6);
                    break;
                case 0x4013:
                    dmc.Regs[3] = value; dmc.CachedDmaLength = ((value << 4) + 1) << 3;
                    break;
                case 0x4015:
                    dmc.Enabled = (value & 0x10) != 0;
                    enableReg = value;
                    for (ch = 0; ch < 2; ch++) if ((value & (1 << ch)) == 0) { rect[ch].Enabled = false; rect[ch].VblLength = 0; }
                    if ((value & 0x04) == 0) { tri.Enabled = false; tri.VblLength = 0; tri.LinearLength = 0; tri.CounterStarted = false; tri.WriteLatency = 0; }
                    if ((value & 0x08) == 0) { noise.Enabled = false; noise.VblLength = 0; }
                    if ((value & 0x10) != 0) { if (dmc.DmaLength == 0) DmcReload(); }
                    else { dmc.DmaLength = 0; dmc.IrqOccurred = false; }
                    break;
                case 0x4017:
                    cntRate = (value & 0x80) != 0 ? 4 : 5;
                    break;
            }
        }

        // apu_process
        private void Process(int numSamples)
        {
            long elapsed = elapsedCycles;
            int step = cycleRate >> 16;
            while (numSamples-- > 0)
            {
                while (qHead != qTail && qTs[qTail] <= elapsed)
                {
                    RegWrite(qAddr[qTail], qVal[qTail]);
                    qTail = (qTail + 1) & (QueueSize - 1);
                }
                elapsed += step;
                int p1, p2, t, n, d;
                if (yano) { p1 = RectY(rect[0]); p2 = RectY(rect[1]); t = TriY(); n = NoiseY(); d = DmcY(); }
                else { p1 = RectJ(rect[0]); p2 = RectJ(rect[1]); t = TriJ(); n = NoiseJ(); d = DmcJ(); }
                int accum = 0;
                if ((mask & 1) != 0) accum += p1;
                if ((mask & 2) != 0) accum += p2;
                if ((mask & 4) != 0) accum += t;
                if ((mask & 8) != 0) accum += n;
                if ((mask & 16) != 0) accum += d;
                if (yano)
                {
                    // Cancel DC offset Dec.17.2000
                    double delta = (dcMax - dcMin) / 32768.0;
                    dcMax -= delta; dcMin += delta;
                    if (accum > dcMax) dcMax = accum;
                    if (accum < dcMin) dcMin = accum;
                    dcAve -= dcAve / 1024.0;
                    dcAve += (dcMax + dcMin) / 2048.0;
                    accum -= (int)dcAve;
                }
                if (accum > 0x7FFF) accum = 0x7FFF; else if (accum < -0x8000) accum = -0x8000;
                if (outCount < outRing.Length)
                {
                    outRing[(outHead + outCount) & (outRing.Length - 1)] = accum / 32768f;
                    outCount++;
                }
            }
            elapsedCycles = cpuCycles;   // resync cycle counter
        }

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
