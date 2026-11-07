/*
 * NesdsSound - nesDS's ARM7 sound (Mamiya's NSF-player APU, "Sound by Mamiya") ported to C#, with the DS sound channels it plays through.
 * Source project: nesDS (Nintendo DS NES emulator) by loopy, FluBBa and later DS-Homebrew contributors; APU code by Mamiya.
 * Version: github.com/DS-Homebrew/NesDS tag v2.0.1, commit 652c8aa (2025-10-17, the last tag before v2.1 added the APU frame IRQ and DMC fixes).
 * Files ported: arm7/source/s_apu.c (channel renderers, register writes, reset), arm7/source/arm7main.c (mix(): one DS hardware channel per
 *   NES channel, the log volume table "soundconvert", the 2-tap smoothing, dealrawpcm() for $4011 PCM, readAPU() write batching),
 *   arm7/source/audiosys.c (frequency setting).
 * URLs: https://github.com/DS-Homebrew/NesDS/tree/652c8aa/arm7/source
 * License: public domain (nesDS README: "released into the PUBLIC DOMAIN"; arm7/source/license.txt lists Mamiya's player library as "PDS").
 * Sources as fetched (sha256): C:\BrokenNes-tools\apu-harvest\nesds\MANIFEST.sha256.
 *
 * Characteristic flaws kept on purpose (art instrument; do not fix):
 *  - Register writes reach the ARM7 through a FIFO that is drained only after each 128-sample block is mixed: every write lands on a
 *    ~5.3 ms grid (128 samples at ~24 kHz), so fast arpeggios and envelopes smear or vanish.
 *  - Synthesis assumes MIXFREQ 0x5E00 = 24064 Hz but the DS timers run the channels at 33513982/2/0x2B9 ~= 24041.6 Hz: everything is ~0.09% flat.
 *  - Each NES channel has its own frame counter (240/192 Hz) that only advances while that channel is keyed and its length counter is
 *    non-zero; the $4017 mode only picks the frame rate (no 4/5-step sequence, no frame IRQ here).
 *  - Volume: the per-channel log conversion in s_apu.c is commented out (linear 0..15), then arm7main.c's soundconvert() pushes the
 *    linear value through a 1024-entry log-ish curve (logtable[v << shift] << 5): quiet notes are boosted, loud ones compressed.
 *    The DMC path can index logtable[1024] (one past the end) for its lowest level; here that reads as 1023.
 *  - 2-tap smoothing per channel: squares/triangle average with the previous RAW sample, noise/DMC with the previous OUTPUT (an IIR).
 *  - DS channel volumes: squares and triangle 0x3F, noise 0x2F, DMC 0x7F, raw PCM 0x7F (out of 0x7F), all centred.
 *  - Square: silenced for periods <= 4 or >= 0x7FF and by the sweep overflow limit table; the phase resets on $4003/$4007; sweep
 *    negate subtracts one extra on channel 1 only.
 *  - Triangle: the ultrasonic cutoff and the "silent when halted" returns are #if 0'd out, so a stopped triangle keeps outputting
 *    the step it stopped on (a DC hold), and periods <= 4 just freeze the step. Its sign bit (step bit 4) flips every 16 steps, so the
 *    waveform is a 32-step "two-lobed" wave (a 0..7..0 bump, then the same bump negated) instead of the NES's 15..0..15 ramp: right pitch, hollow tone, 3-bit steps.
 *  - Noise: 16-bit LFSR with taps 1 or 6, period table of its own (wavelength_converter_table), clocked at half rate.
 *  - DMC: 6-bit counter stepping +-1 per bit (not +-2), output (dac<<1)+bit0-0x40; a $4015 enable while already enabled does not
 *    restart; the sample address does not wrap back to $8000 (here it wraps to $0000, the C reads past its bank table).
 *  - Raw $4011 PCM goes through a separate 8-bit DS channel fed from a 263-slot per-scanline table that is read on the DS's own
 *    VCOUNT at 100/152 lines per sample; a block with fewer than 10 fresh writes is replaced by silence ("not a playable raw pcm").
 *    The ARM9 side that fills the table is assembly not in this port; here a $4011 write is stored at the NES scanline it happens on.
 *  - The DS mixer: channels summed at their volumes, 10-bit output (the low 6 bits of the 16-bit mix are dropped).
 */
using System;
using System.Collections.Generic;

namespace NesEmulator
{
    public sealed class NesdsSound
    {
        public const int SampleRate = 24042;            // 33513982 / 2 / 0x2B9, rounded
        private const double DsRate = 33513982.0 / 2.0 / 0x2B9;
        private const uint NesBaseCycles = 21477270, MixFreq = 0x5E00;
        private const int CpsBits = 16, MixBufSize = 128;

        private readonly Func<ushort, byte> read;
        private int mask = 0x1F;
        public int ChannelMask { get => mask; set => mask = value & 0x1F; }

        // ---- s_apu.c state ----
        private struct Length { public uint Counter; public bool ClockDisable; }
        private struct Linear { public uint Cpf, Fc; public byte Load; public bool Start; public byte Counter; public bool ToCount; public bool Mode; public bool ClockDisable; }
        private struct Env { public bool Disable; public byte Counter, Rate, Timer; public bool Looping; public byte Volume; }
        private struct Sweep { public byte Ch; public bool Active; public byte Rate, Timer; public bool Direction; public byte Shifter; }
        private sealed class Square { public Length Lc; public Env Ed; public Sweep Sw; public uint Cps, Fc, Wl, Pt; public byte St, Fp, Duty; public bool Key; }
        private sealed class Triangle { public Length Lc; public Linear Li; public uint Cps, Fc, Wl, Pt; public byte St, Fp; public bool Key; }
        private sealed class Noise { public Length Lc; public Env Ed; public uint Cps, Fc, Wl, Pt, Rng; public bool RngShort; public byte Fp; public bool Key; }
        private sealed class Dpcm { public uint Cps, Wl, Pt, Length, Adr; public int DacOut, DacOut0; public byte StartLength, StartAdr; public bool Loop, IrqEnable; public byte IrqReport, Input; public bool First; public byte DacBase; public bool Key; }

        private readonly Square[] sq = { new Square(), new Square() };
        private readonly Triangle tri = new Triangle();
        private readonly Noise noi = new Noise();
        private readonly Dpcm dpcm = new Dpcm();
        private readonly uint[] cpf = new uint[3];

        private static readonly byte[] SquareDuty = { 2, 4, 8, 12 };
        private static readonly byte[] VblLength = {
            0x05,0x7F,0x0A,0x01,0x14,0x02,0x28,0x03,0x50,0x04,0x1E,0x05,0x07,0x06,0x0E,0x07,
            0x06,0x08,0x0C,0x09,0x18,0x0A,0x30,0x0B,0x60,0x0C,0x24,0x0D,0x08,0x0E,0x10,0x0F };
        private static readonly uint[] NoiseWl = { 0x002,0x004,0x008,0x010,0x020,0x030,0x040,0x050,0x065,0x07f,0x0be,0x0fe,0x17d,0x1fc,0x3f9,0x7f2 };
        private static readonly uint[] SpdLimit = { 0x3FF,0x555,0x666,0x71C,0x787,0x7C1,0x7E0,0x7F0 };
        private static readonly uint[] DpcmFreq = { 428,380,340,320,286,254,226,214,190,160,142,128,106,85,72,54 };

        // ---- arm7main.c state ----
        private readonly Queue<(ushort a, byte v)> fifo = new();
        private readonly int[] preval = new int[5];
        private readonly byte[] pcmData = new byte[263];
        private byte pcmOut = 0x3F; private int pcmPrevol = 0x3F;
        private readonly short[] chanBuf = new short[5 * MixBufSize];
        private readonly sbyte[] pcmBuf = new sbyte[MixBufSize];
        private double sampleAcc; private int blockPos = MixBufSize;   // samples of the current block already output
        private long cpuTime;
        private const double CpuClock = 1789773.0, DsVcountRate = 263 * 59.8261;

        private readonly float[] outRing = new float[1 << 15];
        private int outHead, outCount;

        public NesdsSound(Func<ushort, byte> read) { this.read = read; Reset(); }

        private static uint DivFix(uint p1, uint p2, int fix)
        {
            uint ret = p1 / p2; p1 %= p2;
            while (fix-- > 0) { p1 += p1; ret += ret; if (p1 >= p2) { p1 -= p2; ret++; } }
            return ret;
        }

        public void Reset()
        {
            uint cps = DivFix(NesBaseCycles, 12 * MixFreq, CpsBits);
            foreach (var s in sq) { s.Lc = default; s.Ed = default; s.Sw = default; s.Fc = s.Wl = s.Pt = 0; s.St = s.Fp = s.Duty = 0; s.Key = false; s.Cps = cps; }
            tri.Lc = default; tri.Li = default; tri.Fc = tri.Wl = tri.Pt = 0; tri.St = tri.Fp = 0; tri.Key = false; tri.Cps = cps;
            noi.Lc = default; noi.Ed = default; noi.Fc = noi.Wl = noi.Pt = 0; noi.RngShort = false; noi.Fp = 0; noi.Key = false; noi.Cps = cps; noi.Rng = 0x8000;
            dpcm.Wl = dpcm.Pt = dpcm.Length = dpcm.Adr = 0; dpcm.DacOut = dpcm.DacOut0 = 0; dpcm.StartLength = dpcm.StartAdr = 0; dpcm.Loop = dpcm.IrqEnable = false;
            dpcm.IrqReport = dpcm.Input = 0; dpcm.DacBase = 0; dpcm.Key = false; dpcm.Cps = cps;
            cpf[1] = DivFix(NesBaseCycles, 12 * 240, CpsBits);
            cpf[2] = DivFix(NesBaseCycles, 12 * 240 * 4 / 5, CpsBits);
            cpf[0] = cpf[1];
            sq[1].Sw.Ch = 1;
            tri.Li.Cpf = cpf[1];
            for (int i = 0; i <= 0x17; i++) ApuWrite((ushort)(0x4000 + i), (byte)(i == 0x10 ? 0x10 : 0x00));
            ApuWrite(0x4015, 0x0F);
            dpcm.First = true;
            fifo.Clear(); Array.Clear(preval); Array.Clear(pcmData); pcmOut = 0x3F; pcmPrevol = 0x3F;
        }

        // ======================== s_apu.c ========================
        private static void LengthStep(ref Length lc) { if (lc.Counter != 0 && !lc.ClockDisable) lc.Counter--; }

        private static void LinearStep(ref Linear li, uint cps)
        {
            li.Fc += cps;
            while (li.Fc >= li.Cpf)
            {
                li.Fc -= li.Cpf;
                if (li.ToCount) { li.ToCount = false; li.Mode = true; }
                if (li.Mode && !li.ClockDisable && li.Counter != 0) --li.Counter;
            }
        }

        private static void EnvStep(ref Env ed)
        {
            if (!ed.Disable && ++ed.Timer > ed.Rate)
            {
                ed.Timer = 0;
                if (ed.Counter != 0 || ed.Looping) ed.Counter = (byte)((ed.Counter - 1) & 0xF);
            }
        }

        private static void SweepStep(ref Sweep sw, ref uint wl)
        {
            if (sw.Active && sw.Shifter != 0 && ++sw.Timer > sw.Rate)
            {
                sw.Timer = 0;
                if (sw.Direction) { wl -= wl >> sw.Shifter; if (wl != 0 && sw.Ch == 0) wl--; }
                else wl += wl >> sw.Shifter;
            }
        }

        private int SquareRender(Square ch)
        {
            if (!ch.Key || ch.Lc.Counter == 0) return 0;
            ch.Fc += ch.Cps;
            while (ch.Fc >= cpf[0])
            {
                ch.Fc -= cpf[0];
                if ((ch.Fp & 3) == 0) LengthStep(ref ch.Lc);
                if ((ch.Fp & 1) == 0) SweepStep(ref ch.Sw, ref ch.Wl);
                EnvStep(ref ch.Ed);
                ch.Fp++;
            }
            if (!ch.Sw.Direction && ch.Wl > SpdLimit[ch.Sw.Shifter]) return 0;
            if (ch.Wl <= 4 || 0x7FF <= ch.Wl) return 0;
            ch.Pt += ch.Cps;
            while (ch.Pt >= (ch.Wl << CpsBits)) { ch.Pt -= ch.Wl << CpsBits; ch.St = (byte)((ch.St + 1) & 0xF); }
            int output = ch.Ed.Disable ? ch.Ed.Volume : ch.Ed.Counter;
            return ch.St >= ch.Duty ? -output : output;
        }

        private int TriangleRender()
        {
            var ch = tri;
            if (!ch.Key || ch.Lc.Counter == 0 || ch.Li.Counter == 0) { /* #if 0 return 0; */ }
            else
            {
                LinearStep(ref ch.Li, ch.Cps);
                ch.Fc += ch.Cps;
                while (ch.Fc >= cpf[0]) { ch.Fc -= cpf[0]; if ((ch.Fp & 3) == 0) LengthStep(ref ch.Lc); ch.Fp++; }
                if (ch.Wl <= 4) { /* #if 0 return 0; */ }
                else
                {
                    ch.Pt += ch.Cps;
                    while (ch.Pt >= (ch.Wl << CpsBits)) { ch.Pt -= ch.Wl << CpsBits; ch.St++; }
                }
            }
            int output = ch.St & 7;
            if ((ch.St & 8) != 0) output ^= 7;
            return ((ch.St >> 4) & 1) != 0 ? -output : output;
        }

        private int NoiseRender()
        {
            var ch = noi;
            if (!ch.Key || ch.Lc.Counter == 0) return 0;
            ch.Fc += ch.Cps;
            while (ch.Fc >= cpf[0]) { ch.Fc -= cpf[0]; if ((ch.Fp & 3) == 0) LengthStep(ref ch.Lc); EnvStep(ref ch.Ed); ch.Fp++; }
            if (ch.Wl == 0) return 0;
            ch.Pt += ch.Cps;
            while (ch.Pt >= (ch.Wl << (CpsBits + 1)))
            {
                ch.Pt -= ch.Wl << (CpsBits + 1);
                ch.Rng >>= 1;
                ch.Rng |= ((ch.Rng ^ (ch.Rng >> (ch.RngShort ? 6 : 1))) & 1) << 15;
            }
            int output = (ch.Ed.Disable ? ch.Ed.Volume : ch.Ed.Counter) & 0xF;
            return (ch.Rng & 1) != 0 ? -output : output;
        }

        private void DpcmRead() { dpcm.Input = read((ushort)dpcm.Adr); dpcm.Adr++; }

        private void DpcmStart()
        {
            dpcm.Adr = 0xC000u + ((uint)dpcm.StartAdr << 6);
            dpcm.Length = (((uint)dpcm.StartLength << 4) + 1) << 3;
            dpcm.IrqReport = 0;
            DpcmRead();
        }

        private int DpcmRender()
        {
            var ch = dpcm;
            if (ch.First) { ch.First = false; ch.DacBase = (byte)ch.DacOut; }
            if (ch.Key && ch.Length != 0)
            {
                ch.Pt += ch.Cps;
                while (ch.Pt >= (ch.Wl << CpsBits))
                {
                    ch.Pt -= ch.Wl << CpsBits;
                    if (ch.Length == 0) continue;
                    if ((ch.Input & 1) != 0) ch.DacOut += ch.DacOut < 0x3F ? 1 : 0;
                    else ch.DacOut -= ch.DacOut > 0 ? 1 : 0;
                    ch.Input >>= 1;
                    if (--ch.Length == 0)
                    {
                        if (ch.Loop) DpcmStart();
                        else { if (ch.IrqEnable) ch.IrqReport = 0x80; ch.Length = 0; }
                    }
                    else if ((ch.Length & 7) == 0) DpcmRead();
                }
                return (ch.DacOut << 1) + ch.DacOut0 - 0x40;
            }
            return 0;
        }

        private void ApuWrite(ushort address, byte value)
        {
            switch (address)
            {
                case 0x4000: case 0x4004:
                {
                    var s = sq[address >= 0x4004 ? 1 : 0];
                    if ((value & 0x10) != 0) s.Ed.Volume = (byte)(value & 0x0F); else s.Ed.Rate = (byte)(value & 0x0F);
                    s.Ed.Disable = (value & 0x10) != 0;
                    s.Lc.ClockDisable = (value & 0x20) != 0;
                    s.Ed.Looping = (value & 0x20) != 0;
                    s.Duty = SquareDuty[value >> 6];
                    break;
                }
                case 0x4001: case 0x4005:
                {
                    var s = sq[address >= 0x4004 ? 1 : 0];
                    s.Sw.Shifter = (byte)(value & 7); s.Sw.Direction = (value & 8) != 0; s.Sw.Rate = (byte)((value >> 4) & 7); s.Sw.Active = (value & 0x80) != 0; s.Sw.Timer = 0;
                    break;
                }
                case 0x4002: case 0x4006:
                {
                    var s = sq[address >= 0x4004 ? 1 : 0];
                    s.Wl &= 0x700; s.Wl += value;
                    break;
                }
                case 0x4003: case 0x4007:
                {
                    var s = sq[address >= 0x4004 ? 1 : 0];
                    s.St = 0;
                    s.Wl &= 0x0FF; s.Wl += (uint)(value & 7) << 8;
                    s.Ed.Counter = 0xF;
                    s.Lc.Counter = VblLength[value >> 3];
                    break;
                }
                case 0x4008:
                    tri.Li.Load = (byte)(value & 0x7F);
                    if (tri.Li.Start)
                    {
                        if ((value & 0x80) == 0) tri.Li.ToCount = true;
                        tri.Li.Mode = false;
                        tri.Li.Counter = tri.Li.Load;
                    }
                    else
                    {
                        if (!tri.Li.Mode) tri.Li.Counter = tri.Li.Load;
                        tri.Li.Mode = true;
                    }
                    tri.Li.ClockDisable = (value & 0x80) != 0;
                    tri.Lc.ClockDisable = (value & 0x80) != 0;
                    tri.Li.Start = (value & 0x80) != 0;
                    break;
                case 0x400A: tri.Wl &= 0x700; tri.Wl += value; break;
                case 0x400B:
                    tri.Wl &= 0x0FF; tri.Wl += (uint)(value & 7) << 8;
                    tri.Lc.Counter = VblLength[value >> 3];
                    tri.Li.Mode = false;
                    tri.Li.Counter = tri.Li.Load;
                    if (!tri.Li.Start) tri.Li.ToCount = true;
                    break;
                case 0x400C:
                    if ((value & 0x10) != 0) noi.Ed.Volume = (byte)(value & 0x0F); else noi.Ed.Rate = (byte)(value & 0x0F);
                    noi.Ed.Disable = (value & 0x10) != 0; noi.Lc.ClockDisable = (value & 0x20) != 0; noi.Ed.Looping = (value & 0x20) != 0;
                    break;
                case 0x400E: noi.Wl = NoiseWl[value & 0x0F]; noi.RngShort = (value & 0x80) != 0; break;
                case 0x400F: noi.Ed.Counter = 0xF; noi.Lc.Counter = VblLength[value >> 3]; break;
                case 0x4010:
                    dpcm.Wl = DpcmFreq[value & 0x0F]; dpcm.Loop = (value & 0x40) != 0; dpcm.IrqEnable = (value & 0x80) != 0;
                    if (!dpcm.IrqEnable) dpcm.IrqReport = 0;
                    break;
                case 0x4011:
                    dpcm.DacOut = (value >> 1) & 0x3F; dpcm.DacBase = (byte)(value & 0x7F); dpcm.DacOut0 = value & 1;
                    break;
                case 0x4012: dpcm.StartAdr = value; break;
                case 0x4013: dpcm.StartLength = value; break;
                case 0x4015:
                    if ((value & 1) != 0) sq[0].Key = true; else { sq[0].Key = false; sq[0].Lc.Counter = 0; }
                    if ((value & 2) != 0) sq[1].Key = true; else { sq[1].Key = false; sq[1].Lc.Counter = 0; }
                    if ((value & 4) != 0) tri.Key = true; else { tri.Key = false; tri.Lc.Counter = 0; tri.Li.Counter = 0; tri.Li.Mode = false; }
                    if ((value & 8) != 0) noi.Key = true; else { noi.Key = false; noi.Lc.Counter = 0; }
                    if ((value & 16) != 0) { if (!dpcm.Key) { dpcm.Key = true; DpcmStart(); } }
                    else dpcm.Key = false;
                    break;
                case 0x4017: cpf[0] = (value & 0x80) != 0 ? cpf[2] : cpf[1]; break;
            }
        }

        // ======================== arm7main.c ========================
        private static readonly short[] LogTable = BuildLogTable();

        private static short SoundConvert(int output, int sft)
        {
            int idx = (output >= 0 ? output : -output) << sft;
            if (idx > 1023) idx = 1023;   // logtable[1024] (DMC's lowest level) is past the end in the C
            int v = LogTable[idx];
            return (short)((output >= 0 ? v : -v) << 5);
        }

        /// <summary>NES register write (from the CPU side). It reaches the ARM7 through the FIFO: applied after the block being mixed.</summary>
        public void Write(ushort addr, byte value)
        {
            if (addr < 0x4000 || addr > 0x4017) return;
            if (addr == 0x4011)
            {
                // ARM9 side: the raw PCM table, one slot per NES scanline (reconstruction, see the header)
                int line = (int)(cpuTime % 29781 * 262 / 29781);
                pcmData[line] = (byte)(0x80 | (value & 0x7F));
            }
            fifo.Enqueue((addr, value));
        }

        private void MixBlock()
        {
            int o = 0;
            for (int i = 0; i < MixBufSize; i++) { int v = SoundConvert(SquareRender(sq[0]), 6); chanBuf[o++] = (short)((preval[0] + v) / 2); preval[0] = v; }
            for (int i = 0; i < MixBufSize; i++) { int v = SoundConvert(SquareRender(sq[1]), 6); chanBuf[o++] = (short)((preval[1] + v) / 2); preval[1] = v; }
            for (int i = 0; i < MixBufSize; i++) { int v = SoundConvert(TriangleRender(), 7); chanBuf[o++] = (short)((preval[2] + v) / 2); preval[2] = v; }
            for (int i = 0; i < MixBufSize; i++) { int v = SoundConvert(NoiseRender(), 6); v = (preval[3] + v) / 2; chanBuf[o++] = (short)v; preval[3] = v; }
            for (int i = 0; i < MixBufSize; i++) { int v = SoundConvert(DpcmRender(), 4); v = (preval[4] + v) / 2; chanBuf[o++] = (short)v; preval[4] = v; }
            DealRawPcm();
            while (fifo.Count > 0) { var (a, v) = fifo.Dequeue(); ApuWrite(a, v); }   // readAPU()
        }

        private void DealRawPcm()
        {
            int pcmLine = (int)((long)(cpuTime / CpuClock * DsVcountRate) % 263);
            int count = 0, line = 0;
            for (int i = 0; i < MixBufSize; i++)
            {
                if ((pcmData[pcmLine] & 0x80) != 0) { pcmOut = (byte)(pcmData[pcmLine] & 0x7F); pcmData[pcmLine] = 0; count++; }
                pcmBuf[i] = (sbyte)(pcmOut + pcmPrevol - 0x80);
                pcmPrevol = pcmOut;
                line += 100;
                if (line >= 152) { line -= 152; pcmLine++; if (pcmLine > 262) pcmLine = 0; }
            }
            if (count < 10) { Array.Clear(pcmBuf); pcmPrevol = 0x3F; pcmOut = 0x3F; }
        }

        private static readonly int[] DsVol = { 0x3F, 0x3F, 0x3F, 0x2F, 0x7F };

        private void EmitSample(int i)
        {
            int mix = 0;
            for (int c = 0; c < 5; c++) if ((mask & (1 << c)) != 0) mix += chanBuf[c * MixBufSize + i] * DsVol[c] / 128;
            if ((mask & 0x10) != 0) mix += (pcmBuf[i] << 8) * 0x7F / 128;
            mix /= 2;                                        // centred pan: half to each speaker
            if (mix > 32767) mix = 32767; else if (mix < -32768) mix = -32768;
            mix &= ~0x3F;                                    // 10-bit DS output
            float s = mix / 32768f;
            if (outCount == outRing.Length) { outHead = (outHead + 1) & (outRing.Length - 1); outCount--; }
            outRing[(outHead + outCount) & (outRing.Length - 1)] = s;
            outCount++;
        }

        public void Clock(int cpuCycles)
        {
            cpuTime += cpuCycles;
            sampleAcc += cpuCycles * (DsRate / CpuClock);
            while (sampleAcc >= 1.0)
            {
                sampleAcc -= 1.0;
                if (blockPos >= MixBufSize) { MixBlock(); blockPos = 0; }
                EmitSample(blockPos++);
            }
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

        private static short[] BuildLogTable() => new short[] {
0,10,17,23,28,33,38,42,47,51,54,58,62,66,69,73,76,79,
82,86,89,92,95,98,101,104,107,110,113,115,118,121,124,126,
129,132,134,137,139,142,145,147,150,152,155,157,159,162,164,167,
169,171,174,176,178,181,183,185,188,190,192,194,197,199,201,203,
205,208,210,212,214,216,218,220,222,225,227,229,231,233,235,237,
239,241,243,245,247,249,251,253,255,257,259,261,263,265,267,269,
271,272,274,276,278,280,282,284,286,288,289,291,293,295,297,299,
301,302,304,306,308,310,311,313,315,317,319,320,322,324,326,327,
329,331,333,334,336,338,340,341,343,345,346,348,350,352,353,355,
357,358,360,362,363,365,367,368,370,372,373,375,377,378,380,381,
383,385,386,388,390,391,393,394,396,398,399,401,402,404,406,407,
409,410,412,413,415,416,418,420,421,423,424,426,427,429,430,432,
433,435,436,438,439,441,442,444,445,447,448,450,451,453,454,456,
457,459,460,462,463,465,466,468,469,470,472,473,475,476,478,479,
480,482,483,485,486,488,489,490,492,493,495,496,497,499,500,502,
503,504,506,507,509,510,511,513,514,515,517,518,520,521,522,524,
525,526,528,529,530,532,533,534,536,537,538,540,541,542,544,545,
546,548,549,550,551,553,554,555,557,558,559,561,562,563,564,566,
567,568,570,571,572,573,575,576,577,578,580,581,582,583,585,586,
587,588,590,591,592,593,595,596,597,598,600,601,602,603,604,606,
607,608,609,610,612,613,614,615,616,618,619,620,621,622,624,625,
626,627,628,629,631,632,633,634,635,636,638,639,640,641,642,643,
645,646,647,648,649,650,651,653,654,655,656,657,658,659,660,662,
663,664,665,666,667,668,669,670,672,673,674,675,676,677,678,679,
680,681,682,684,685,686,687,688,689,690,691,692,693,694,695,696,
697,698,700,701,702,703,704,705,706,707,708,709,710,711,712,713,
714,715,716,717,718,719,720,721,722,723,724,725,726,727,728,729,
730,731,732,733,734,735,736,737,738,739,740,741,742,743,744,745,
746,747,748,749,750,751,752,753,754,755,756,757,758,759,760,760,
761,762,763,764,765,766,767,768,769,770,771,772,773,774,774,775,
776,777,778,779,780,781,782,783,784,784,785,786,787,788,789,790,
791,792,792,793,794,795,796,797,798,799,799,800,801,802,803,804,
805,805,806,807,808,809,810,811,811,812,813,814,815,816,816,817,
818,819,820,821,821,822,823,824,825,826,826,827,828,829,830,830,
831,832,833,834,834,835,836,837,838,838,839,840,841,842,842,843,
844,845,845,846,847,848,849,849,850,851,852,852,853,854,855,855,
856,857,858,858,859,860,861,861,862,863,864,864,865,866,866,867,
868,869,869,870,871,872,872,873,874,874,875,876,876,877,878,879,
879,880,881,881,882,883,883,884,885,885,886,887,888,888,889,890,
890,891,892,892,893,894,894,895,895,896,897,897,898,899,899,900,
901,901,902,903,903,904,905,905,906,906,907,908,908,909,910,910,
911,911,912,913,913,914,914,915,916,916,917,917,918,919,919,920,
920,921,922,922,923,923,924,924,925,926,926,927,927,928,928,929,
930,930,931,931,932,932,933,933,934,935,935,936,936,937,937,938,
938,939,939,940,940,941,942,942,943,943,944,944,945,945,946,946,
947,947,948,948,949,949,950,950,951,951,952,952,953,953,954,954,
955,955,956,956,957,957,957,958,958,959,959,960,960,961,961,962,
962,963,963,963,964,964,965,965,966,966,967,967,967,968,968,969,
969,970,970,970,971,971,972,972,973,973,973,974,974,975,975,975,
976,976,977,977,977,978,978,979,979,979,980,980,980,981,981,982,
982,982,983,983,983,984,984,985,985,985,986,986,986,987,987,987,
988,988,988,989,989,989,990,990,990,991,991,991,992,992,992,993,
993,993,994,994,994,995,995,995,996,996,996,996,997,997,997,998,
998,998,999,999,999,999,1000,1000,1000,1001,1001,1001,1001,1002,1002,1002,
1002,1003,1003,1003,1004,1004,1004,1004,1005,1005,1005,1005,1006,1006,1006,1006,
1007,1007,1007,1007,1007,1008,1008,1008,1008,1009,1009,1009,1009,1009,1010,1010,
1010,1010,1011,1011,1011,1011,1011,1012,1012,1012,1012,1012,1013,1013,1013,1013,
1013,1013,1014,1014,1014,1014,1014,1015,1015,1015,1015,1015,1015,1016,1016,1016,
1016,1016,1016,1017,1017,1017,1017,1017,1017,1017,1018,1018,1018,1018,1018,1018,
1018,1019,1019,1019,1019,1019,1019,1019,1019,1019,1020,1020,1020,1020,1020,1020,
1020,1020,1020,1021,1021,1021,1021,1021,1021,1021,1021,1021,1021,1021,1022,1022,
1022,1022,1022,1022,1022,1022,1022,1022,1022,1022,1022,1022,1023,1023,1023,1023,
1023,1023,1023,1023,1023,1023,1023,1023,1023,1023,1023,1023,1023,1023,1023,1023,
1023,1023,1023,1023,1023,1023,1023,1023,1023,1023,1023,1023,1023,1023 };
    }
}
