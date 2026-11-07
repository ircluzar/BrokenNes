/*
 * NofrendoApu - Nofrendo's NES APU (nes/apu.c) as carried by retro-go, ported to C#.
 * Source: retro-go commit 05e97e8 (2022-11-18), retro-core/components/nofrendo/nes/apu.c + apu.h
 *         https://github.com/ducalex/retro-go/tree/05e97e8/retro-core/components/nofrendo/nes
 * Authors: Matthew Conte (Nofrendo, (c) 1998-2000), Alex Duchesne (retro-go changes).
 * License: GNU Library General Public License version 2 (the file header); retro-go's COPYING is GPLv2.
 * The original legend: "Any permitted reproduction of these routines, in whole or in part, must bear this legend."
 *   Nofrendo (c) 1998-2000 Matthew Conte (matt@conte.com)
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and
 * docs/projects/apu-harvest-catalog.md
 *
 * Characteristic flaws kept on purpose:
 *  - No cycle stepping: register writes take effect at once and the whole frame (sample_rate / 60 samples) is rendered
 *    at the frame's end (nofrendo calls apu_emulate() once per frame after the CPU ran), so every write of a frame lands
 *    on its first sample; mid-frame timing is lost.
 *  - The frame counter advance in apu_process is commented out: no 240 Hz sequencer at all. Envelope, length, sweep and
 *    triangle linear counters run per SAMPLE against tables precomputed from samples-per-frame (decay_lut, vbl_lut,
 *    trilength_lut), so their rates approximate 240/120/60 Hz via "phase -= 4 / -= 2" tricks.
 *  - Channel outputs are levels << 8 averaged over the sub-sample periods (total / num_times, integer division), each
 *    decaying with x -= x >> 7 every sample; the triangle is a running +-(2<<8) accumulator (unbounded staircase with a
 *    5/4 gain), noise and DMC have a 3/4 gain.
 *  - Rectangle sweep: channel 0 ramps up with "freq += ~(freq >> shifts)", channel 1 with "freq -= freq >> shifts";
 *    "ramp down" adds. Mute test is the freq_limit table, not the real sweep target.
 *  - Triangle "write latency" hack: (int)(228 / cycle_rate) samples before the linear counter starts.
 *  - Noise period table is the old nofrendo one (4..4068, not the real 2034/4068 tail), xor tap 0x40 / 0x02.
 *  - DMC: delta steps of 2 clamped to 1..0x7D, IRQ/enable "bodge for timestamp queue", $4011 adds the delta to the
 *    decaying output.
 *  - Default filter APU_FILTER_WEIGHTED: (3*cur + prev) / 4, then a 16-bit clip.
 *  - The per-channel enable options in apu_process are commented out in the original; BrokenNes's channel mask is
 *    applied here at the mix only (the channels still run).
 *  - $4009 / $400D ignored; $4017 only resets the (dead) frame counter state.
 * Sample rate: no retro-go default for nofrendo is visible in these files (apu_init takes it as a parameter), so 32000 Hz
 * is used. NES_REFRESH_RATE = 60 and NES_CPU_CLOCK = 1789772 (NTSC) are assumed from nes.h (not in the harvested files).
 */
using System;

namespace NesEmulator
{
    public sealed class NofrendoApu
    {
        public const int SampleRate = 32000;
        private const int RefreshRate = 60;
        private const double CpuClock = 1789772.0;
        private const int CyclesPerFrame = 29830; // NES_CPU_CLOCK / 60 rounded; frame boundary for rendering

        private static readonly byte[] VblLength = {
            5, 127, 10, 1, 19, 2, 40, 3, 80, 4, 30, 5, 7, 6, 13, 7, 6, 8, 12, 9, 24, 10, 48, 11, 96, 12, 36, 13, 8, 14, 16, 15 };
        private static readonly int[] FreqLimit = { 0x3FF, 0x555, 0x666, 0x71C, 0x787, 0x7C1, 0x7E0, 0x7F0 };
        private static readonly int[] NoiseFreq = { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };
        private static readonly int[] DmcClocks = { 428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 85, 72, 54 };
        private static readonly int[] DutyFlip = { 2, 4, 8, 12 };

        private sealed class Rect
        {
            public byte[] Regs = new byte[4]; public bool Enabled; public float Accum; public int Freq, OutputVol, FixedEnvelope, Holdnote, Volume;
            public int SweepPhase, SweepDelay, SweepOn, SweepShifts, SweepInc, FreqLimit, EnvPhase, EnvDelay, EnvVol, VblLength, Adder, DutyFlip;
        }
        private sealed class Tri
        {
            public byte[] Regs = new byte[3]; public bool Enabled; public float Accum; public int Freq, OutputVol, Holdnote, CounterStarted, WriteLatency, VblLength, Adder, LinearLength;
        }
        private sealed class Noi
        {
            public byte[] Regs = new byte[3]; public bool Enabled; public float Accum; public int Freq, OutputVol, EnvPhase, EnvDelay, EnvVol, FixedEnvelope, Holdnote;
            public byte Volume, XorTap; public ushort ShiftReg; public int VblLength;
        }
        private sealed class Dmc
        {
            public byte[] Regs = new byte[4]; public bool Enabled, Looping, IrqGen, IrqOccurred; public float Accum; public int Freq, OutputVol;
            public int Address, CachedAddr, DmaLength, CachedDmaLength, CurByte;
        }

        private readonly Func<ushort, byte> read;
        private Rect[] rect = { new Rect(), new Rect() };
        private Tri tri = new Tri();
        private Noi noise = new Noi();
        private Dmc dmc = new Dmc();
        private int prevSample, samplesPerFrame;
        private float cycleRate;
        private readonly int[] decayLut = new int[16], vblLut = new int[32], triLengthLut = new int[128];
        private int frameCycles, mask = 0x1F;

        private readonly float[] outRing = new float[16384];
        private int outHead, outCount;

        public int ChannelMask { get => mask; set => mask = value & 0x1F; }
        public int Queued => outCount;

        public NofrendoApu(Func<ushort, byte> read) { this.read = read; Reset(); }

        public void Reset()
        {
            rect = new[] { new Rect(), new Rect() }; tri = new Tri(); noise = new Noi(); dmc = new Dmc();
            prevSample = 0; frameCycles = 0;
            samplesPerFrame = SampleRate / RefreshRate;
            cycleRate = (float)CpuClock / SampleRate;
            noise.ShiftReg = 0x4000;
            for (int i = 0; i < 16; i++) decayLut[i] = samplesPerFrame * (i + 1);
            for (int i = 0; i < 32; i++) vblLut[i] = VblLength[i] * samplesPerFrame;
            for (int i = 0; i < 128; i++) triLengthLut[i] = (int)(0.25 * i * samplesPerFrame);
            for (ushort a = 0x4000; a <= 0x4013; a++) Write(a, 0);
            Write(0x4015, 0x00);
            Write(0x4017, 0x80);
        }

        public void Clock(int cpuCycles)
        {
            frameCycles += cpuCycles;
            while (frameCycles >= CyclesPerFrame) { frameCycles -= CyclesPerFrame; Process(samplesPerFrame); }
        }

        private int Rectangle(int ch)
        {
            var r = rect[ch];
            r.OutputVol -= r.OutputVol >> 7;
            if (!r.Enabled || r.VblLength == 0) return r.OutputVol;
            if (r.Holdnote == 0) r.VblLength--;
            r.EnvPhase -= 4;
            while (r.EnvPhase < 0)
            {
                r.EnvPhase += r.EnvDelay;
                if (r.Holdnote != 0) r.EnvVol = (r.EnvVol + 1) & 0x0F;
                else if (r.EnvVol < 0x0F) r.EnvVol++;
            }
            if (r.Freq < 8 || (r.SweepInc == 0 && r.Freq > r.FreqLimit)) return r.OutputVol;
            if (r.SweepOn != 0 && r.SweepShifts != 0)
            {
                r.SweepPhase -= 2;
                while (r.SweepPhase < 0)
                {
                    r.SweepPhase += r.SweepDelay;
                    if (r.SweepInc != 0)
                    {
                        if (ch == 0) r.Freq += ~(r.Freq >> r.SweepShifts);
                        else r.Freq -= r.Freq >> r.SweepShifts;
                    }
                    else r.Freq += r.Freq >> r.SweepShifts;
                }
            }
            r.Accum -= cycleRate;
            if (r.Accum >= 0) return r.OutputVol;
            int output = r.FixedEnvelope != 0 ? r.Volume << 8 : (r.EnvVol ^ 0x0F) << 8;
            int numTimes = 0, total = 0;
            while (r.Accum < 0)
            {
                r.Accum += r.Freq + 1;
                r.Adder = (r.Adder + 1) & 0x0F;
                if (r.Adder < r.DutyFlip) total += output; else total -= output;
                numTimes++;
            }
            r.OutputVol = total / numTimes;
            return r.OutputVol;
        }

        private int Triangle()
        {
            var t = tri;
            t.OutputVol -= t.OutputVol >> 7;
            if (!t.Enabled || t.VblLength == 0) goto output;
            if (t.CounterStarted != 0)
            {
                if (t.LinearLength > 0) t.LinearLength--;
                if (t.VblLength != 0 && t.Holdnote == 0) t.VblLength--;
            }
            else if (t.Holdnote == 0 && t.WriteLatency != 0)
            {
                if (--t.WriteLatency == 0) t.CounterStarted = 1;
            }
            if (t.LinearLength == 0 || t.Freq < 4) goto output;
            t.Accum -= cycleRate;
            while (t.Accum < 0)
            {
                t.Accum += t.Freq;
                t.Adder = (t.Adder + 1) & 0x1F;
                if ((t.Adder & 0x10) != 0) t.OutputVol -= 2 << 8; else t.OutputVol += 2 << 8;
            }
        output:
            return t.OutputVol + (t.OutputVol >> 2);
        }

        private int NoiseCh()
        {
            var n = noise;
            int numTimes = 0, total = 0, outvol;
            n.OutputVol -= n.OutputVol >> 7;
            if (!n.Enabled || n.VblLength == 0) goto output;
            if (n.Holdnote == 0) n.VblLength--;
            n.EnvPhase -= 4;
            while (n.EnvPhase < 0)
            {
                n.EnvPhase += n.EnvDelay;
                if (n.Holdnote != 0) n.EnvVol = (n.EnvVol + 1) & 0x0F;
                else if (n.EnvVol < 0x0F) n.EnvVol++;
            }
            n.Accum -= cycleRate;
            if (n.Accum >= 0) goto output;
            outvol = n.FixedEnvelope != 0 ? n.Volume << 8 : (n.EnvVol ^ 0x0F) << 8;
            while (n.Accum < 0)
            {
                int sreg = n.ShiftReg;
                int tap = (sreg & n.XorTap) != 0 ? 1 : 0;
                int bit0 = sreg & 1;
                int bit14 = bit0 ^ tap;
                total += (bit0 ^ 1) != 0 ? outvol : -outvol;
                numTimes++;
                n.ShiftReg = (ushort)((bit14 << 14) | (sreg >> 1));
                n.Accum += n.Freq;
            }
            n.OutputVol = total / numTimes;
        output:
            return (n.OutputVol + n.OutputVol + n.OutputVol) >> 2;
        }

        private void DmcReload() { dmc.Address = dmc.CachedAddr; dmc.DmaLength = dmc.CachedDmaLength; dmc.IrqOccurred = false; }

        private int DmcCh()
        {
            var d = dmc;
            d.OutputVol -= d.OutputVol >> 7;
            if (d.DmaLength != 0)
            {
                d.Accum -= cycleRate;
                while (d.Accum < 0)
                {
                    d.Accum += d.Freq;
                    int deltaBit = (d.DmaLength & 7) ^ 7;
                    if (deltaBit == 7)
                    {
                        d.CurByte = read((ushort)d.Address);   // the cycle steal / IRQ are APU_FIX's job here
                        if (d.Address == 0xFFFF) d.Address = 0x8000; else d.Address++;
                    }
                    if (--d.DmaLength == 0)
                    {
                        if (d.Looping) DmcReload();
                        else
                        {
                            if (d.IrqGen) d.IrqOccurred = true;
                            d.Enabled = false;
                            break;
                        }
                    }
                    if ((d.CurByte & (1 << deltaBit)) != 0)
                    {
                        if (d.Regs[1] < 0x7D) { d.Regs[1] += 2; d.OutputVol += 2 << 8; }
                    }
                    else if (d.Regs[1] > 1) { d.Regs[1] -= 2; d.OutputVol -= 2 << 8; }
                }
            }
            return (d.OutputVol + d.OutputVol + d.OutputVol) >> 2;
        }

        public void Write(ushort address, byte value)
        {
            int chan;
            switch (address)
            {
                case 0x4000: case 0x4004:
                    chan = (address & 4) >> 2; var r0 = rect[chan];
                    r0.Regs[0] = value; r0.Volume = value & 0x0F; r0.EnvDelay = decayLut[value & 0x0F];
                    r0.Holdnote = (value >> 5) & 1; r0.FixedEnvelope = (value >> 4) & 1; r0.DutyFlip = DutyFlip[value >> 6];
                    break;
                case 0x4001: case 0x4005:
                    chan = (address & 4) >> 2; var r1 = rect[chan];
                    r1.Regs[1] = value; r1.SweepOn = (value >> 7) & 1; r1.SweepShifts = value & 7;
                    r1.SweepDelay = decayLut[(value >> 4) & 7]; r1.SweepInc = (value >> 3) & 1; r1.FreqLimit = FreqLimit[value & 7];
                    break;
                case 0x4002: case 0x4006:
                    chan = (address & 4) >> 2; var r2 = rect[chan];
                    r2.Regs[2] = value; r2.Freq = (r2.Freq & ~0xFF) | value;
                    break;
                case 0x4003: case 0x4007:
                    chan = (address & 4) >> 2; var r3 = rect[chan];
                    r3.Regs[3] = value; r3.VblLength = vblLut[value >> 3]; r3.EnvVol = 0;
                    r3.Freq = ((value & 7) << 8) | (r3.Freq & 0xFF); r3.Adder = 0;
                    break;
                case 0x4008:
                    tri.Regs[0] = value; tri.Holdnote = (value >> 7) & 1;
                    if (tri.CounterStarted == 0 && tri.VblLength != 0) tri.LinearLength = triLengthLut[value & 0x7F];
                    break;
                case 0x400A:
                    tri.Regs[1] = value; tri.Freq = (((tri.Regs[2] & 7) << 8) + value) + 1;
                    break;
                case 0x400B:
                    tri.Regs[2] = value;
                    tri.WriteLatency = (int)(228 / cycleRate);
                    tri.Freq = (((value & 7) << 8) + tri.Regs[1]) + 1;
                    tri.VblLength = vblLut[value >> 3];
                    tri.CounterStarted = 0;
                    tri.LinearLength = triLengthLut[tri.Regs[0] & 0x7F];
                    break;
                case 0x400C:
                    noise.Regs[0] = value; noise.EnvDelay = decayLut[value & 0x0F]; noise.Holdnote = (value >> 5) & 1;
                    noise.FixedEnvelope = (value >> 4) & 1; noise.Volume = (byte)(value & 0x0F);
                    break;
                case 0x400E:
                    noise.Regs[1] = value; noise.Freq = NoiseFreq[value & 0x0F]; noise.XorTap = (byte)((value & 0x80) != 0 ? 0x40 : 0x02);
                    break;
                case 0x400F:
                    noise.Regs[2] = value; noise.VblLength = vblLut[value >> 3]; noise.EnvVol = 0;
                    break;
                case 0x4010:
                    dmc.Regs[0] = value; dmc.Freq = DmcClocks[value & 0x0F]; dmc.Looping = ((value >> 6) & 1) != 0;
                    if ((value & 0x80) != 0) dmc.IrqGen = true; else { dmc.IrqGen = false; dmc.IrqOccurred = false; }
                    break;
                case 0x4011:
                    value &= 0x7F;
                    dmc.OutputVol += (value - dmc.Regs[1]) << 8;
                    dmc.Regs[1] = value;
                    break;
                case 0x4012:
                    dmc.Regs[2] = value; dmc.CachedAddr = 0xC000 + (ushort)(value << 6);
                    break;
                case 0x4013:
                    dmc.Regs[3] = value; dmc.CachedDmaLength = ((value << 4) + 1) << 3;
                    break;
                case 0x4015:
                    dmc.Enabled = ((value >> 4) & 1) != 0;
                    for (chan = 0; chan < 2; chan++)
                    {
                        if ((value & (1 << chan)) != 0) rect[chan].Enabled = true;
                        else { rect[chan].Enabled = false; rect[chan].VblLength = 0; }
                    }
                    if ((value & 0x04) != 0) tri.Enabled = true;
                    else { tri.Enabled = false; tri.VblLength = 0; tri.LinearLength = 0; tri.CounterStarted = 0; tri.WriteLatency = 0; }
                    if ((value & 0x08) != 0) noise.Enabled = true;
                    else { noise.Enabled = false; noise.VblLength = 0; }
                    if ((value & 0x10) != 0) { if (dmc.DmaLength == 0) DmcReload(); }
                    else dmc.DmaLength = 0;
                    dmc.IrqOccurred = false;
                    break;
                default: break; // $4017: frame counter is dead (advance commented out); $4009/$400D unused
            }
        }

        private void Process(int numSamples)
        {
            int prev = prevSample;
            while (numSamples-- > 0)
            {
                int p1 = Rectangle(0), p2 = Rectangle(1), t = Triangle(), n = NoiseCh(), d = DmcCh();
                int accum = 0;
                if ((mask & 1) != 0) accum += p1;
                if ((mask & 2) != 0) accum += p2;
                if ((mask & 4) != 0) accum += t;
                if ((mask & 8) != 0) accum += n;
                if ((mask & 16) != 0) accum += d;
                accum = (accum + accum + accum + prev) >> 2;   // APU_FILTER_WEIGHTED (default)
                prev = accum;
                if (accum > 0x7FFF) accum = 0x7FFF; else if (accum < -0x8000) accum = -0x8000;
                if (outCount < outRing.Length)
                {
                    outRing[(outHead + outCount) & (outRing.Length - 1)] = accum / 32768f;
                    outCount++;
                }
            }
            prevSample = prev;
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
