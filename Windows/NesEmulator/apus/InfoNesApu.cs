/*
 * InfoNesApu - a quick port of InfoNES's pAPU (sound) engine.
 *
 * Source project : InfoNES (Jay's Factory), "2000/05/29 InfoNES Project ( based on DarcNES and NesterJ )"
 * Version        : github master branch as fetched on 2026-11-01 (no tagged release)
 * Files ported   : src/InfoNES_pAPU.cpp, src/InfoNES_pAPU.h (whole engine), plus the call sites that shape it:
 *                  src/InfoNES.cpp (InfoNES_pAPUVsync once per frame at the V-blank scanline), src/K6502_rw.h ($4000-$4013 and
 *                  $4015 writes go to the event queue), src/K6502.cpp (g_wPassedClocks), src/win32/InfoNES_System_Win.cpp
 *                  (InfoNES_SoundOutput: the 5 channel buffers averaged, (w1+w2+w3+w4+w5)/5, into unsigned 8-bit PCM)
 * URL            : https://github.com/jay-kumogata/InfoNES
 * Copyright      : Copyright (C) 1999-2004 Jay's Factory <jays_factory@excite.co.jp> (as in src/win32/InfoNES_System_Win.cpp;
 *                  the repository has no NOTICE file)
 * License        : Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0). This file is a modified
 *                  (translated to C#) version of the files above.
 *
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
 *
 * Quirks kept (they ARE the sound):
 *  - Default quality preset (pAPU_QUALITY 3): 44100 Hz, 735 samples rendered per V-sync, all 5 channels in one go.
 *  - Register writes go to an event queue stamped "entertime - g_wPassedClocks"; g_wPassedClocks is only the cycle count
 *    inside the current scanline, so every stamp is below the first sample's 41 cycles: ALL of a frame's writes land on
 *    sample 0 of the next render (per-frame register granularity, last write wins). Stamped 0 here, same result.
 *  - The envelope and sweep phase counters are DWORD (unsigned): "Phase -= 4; while (Phase < 0)" never runs. So envelopes
 *    never move (volume = the low 4 bits of $4000, flat, in both envelope modes; noise in envelope mode is a flat 15) and
 *    sweeps never run - square 2's "Freq -= ~(Freq >> n)" (the wrong-way negative sweep) is ported but, as in the
 *    original, unreachable.
 *  - Square / triangle with period < 8 (or a square above ApuFreqLimit with negate off, sweep enabled or not) "break"s out
 *    of the render loop: the rest of that channel's buffer keeps the PREVIOUS frame's samples (a repeating stale frame).
 *  - Odd length table ApuAtl (halved NES table with 127 at index 1), counted once per frame; noise gets it doubled.
 *  - Triangle: linear counter = ($4008 & 0x7F) << 6 SAMPLES, length counter counted per SAMPLE once "started", a magic
 *    write latency of 3; with the hold bit set it never starts and sustains until the next $4008/$4015 write.
 *  - Noise: 9-bit / 5-bit shift register reseeded on $400E, at most one shift per output sample, output 0..15 only.
 *  - DMC: delta counter 0..63 (output 0..127), the cycle-rate constant is ~10x too small (plays ~10x slow), fetches from
 *    $C000 + A*64 through the CPU bus, and its buffer is NOT cleared when $4015 bit 4 is off (stale frame repeats).
 *  - Mixing: each channel is an unsigned byte (squares 0x11 * volume, triangle 0..255, noise 0..15, DMC 0..127),
 *    averaged /5 into an unsigned byte. Rendered at V-sync only (one frame of latency).
 * Deviations (to fit the host, not to improve the sound):
 *  - V-sync is taken every 29781 CPU cycles (one NTSC frame) instead of at the PPU's scanline 241.
 *  - The unsigned 8-bit stream is centred by a one-pole DC blocker, the way a sound card's AC coupling would.
 *  - The original hangs if the DMC is enabled before $4010 is ever written (ApuC5Freq = 0); here that DMC step is skipped.
 *  - The event queue (15000 entries) drops writes past its end instead of overrunning memory.
 */
using System;

namespace NesEmulator
{
    /// <summary>The InfoNES pAPU engine (see the file header). Fed register writes, clocked in CPU cycles; renders 735 samples per frame.</summary>
    public sealed class InfoNesApu
    {
        public const int SampleRate = 44100;
        private const int CyclesPerFrame = 29781;

        // ---- pAPU_QUALITY 3 (InfoNES_pAPU.h default) ----
        private const uint ApuPulseMagic = 0x289d9c00, ApuTriangleMagic = 0x289d9c00, ApuNoiseMagic = 0x289d9c00;
        private const int ApuSamplesPerSync = 735, ApuCyclesPerSample = 41;
        private const uint ApuCycleRate = 265664;

        private readonly Func<ushort, byte> read;
        public InfoNesApu(Func<ushort, byte> cpuRead) { read = cpuRead; Init(); }

        // ---- event queue ----
        private const int APU_EVENT_MAX = 15000;
        private const byte APUET_MASK = 0xfc, APUET_C1 = 0x00, APUET_C2 = 0x04, APUET_C3 = 0x08, APUET_C4 = 0x0c, APUET_C5 = 0x10, APUET_W_CTRL = 0x20;
        private readonly long[] evTime = new long[APU_EVENT_MAX];
        private readonly byte[] evType = new byte[APU_EVENT_MAX], evData = new byte[APU_EVENT_MAX];
        private int cur_event;

        private readonly byte[][] wave_buffers = { new byte[735], new byte[735], new byte[735], new byte[735], new byte[735] };
        private byte ApuCtrl, ApuCtrlNew;

        // Rectangle #1
        private byte ApuC1a, ApuC1b, ApuC1c, ApuC1d; private byte[] ApuC1Wave = pulse_50;
        private uint ApuC1Skip, ApuC1Index, ApuC1EnvPhase, ApuC1SweepPhase, ApuC1Freq; private byte ApuC1EnvVol, ApuC1Atl;
        // Rectangle #2
        private byte ApuC2a, ApuC2b, ApuC2c, ApuC2d; private byte[] ApuC2Wave = pulse_50;
        private uint ApuC2Skip, ApuC2Index, ApuC2EnvPhase, ApuC2SweepPhase, ApuC2Freq; private byte ApuC2EnvVol, ApuC2Atl;
        // Triangle
        private byte ApuC3a, ApuC3b, ApuC3c, ApuC3d; private uint ApuC3Skip, ApuC3Index, ApuC3Llc; private byte ApuC3Atl, ApuC3WriteLatency, ApuC3CounterStarted;
        // Noise
        private byte ApuC4a, ApuC4b, ApuC4c, ApuC4d; private uint ApuC4Sr, ApuC4Skip, ApuC4Index, ApuC4EnvPhase; private byte ApuC4Atl, ApuC4EnvVol;
        // DPCM
        private readonly byte[] ApuC5Reg = new byte[4];
        private byte ApuC5Enable, ApuC5Looping, ApuC5CurByte, ApuC5DpcmValue;
        private int ApuC5Freq, ApuC5Phaseacc;
        private ushort ApuC5Address, ApuC5CacheAddr;
        private int ApuC5DmaLength, ApuC5CacheDmaLength;

        // ---- wave data ----
        private static readonly byte[] pulse_25 = Pulse(8), pulse_50 = Pulse(16), pulse_75 = Pulse(24), pulse_87 = Pulse(28);
        private static byte[] Pulse(int high) { var b = new byte[0x20]; for (int i = 0; i < high; i++) b[i] = 0x11; return b; }
        private static readonly byte[] triangle_50 = {
            0x00, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xa0, 0xb0, 0xc0, 0xd0, 0xe0, 0xf0,
            0xff, 0xef, 0xdf, 0xcf, 0xbf, 0xaf, 0x9f, 0x8f, 0x7f, 0x6f, 0x5f, 0x4f, 0x3f, 0x2f, 0x1f, 0x0f };
        private static readonly byte[][] pulse_waves = { pulse_87, pulse_75, pulse_50, pulse_25 };
        private static readonly byte[] ApuAtl = {
            5, 127, 10, 1, 19,  2, 40,  3, 80,  4, 30,  5, 7,  6, 13,  7,
            6,   8, 12, 9, 24, 10, 48, 11, 96, 12, 36, 13, 8, 14, 16, 15 };
        private static readonly ushort[] ApuFreqLimit = { 0x3FF, 0x555, 0x666, 0x71C, 0x787, 0x7C1, 0x7E0, 0x7F0 };
        private static readonly uint[] ApuNoiseFreq = { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };
        private static readonly uint[] ApuDpcmCycles = { 428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 85, 72, 54 };

        // ---- InfoNES_pAPU.h macros ----
        private int C1Vol => ApuC1a & 0x0f; private int C1Env => ApuC1a & 0x10; private int C1Hold => ApuC1a & 0x20; private int C1DutyCycle => ApuC1a & 0xc0;
        private uint C1EnvDelay => (uint)((ApuC1a & 0x0f) + 1); private int C1SweepOn => ApuC1b & 0x80; private int C1SweepIncDec => ApuC1b & 0x08;
        private int C1SweepShifts => ApuC1b & 0x07; private uint C1SweepDelay => (uint)(((ApuC1b & 0x70) >> 4) + 1); private uint C1FreqLimit => ApuFreqLimit[ApuC1b & 0x07];
        private int C2Vol => ApuC2a & 0x0f; private int C2Env => ApuC2a & 0x10; private int C2Hold => ApuC2a & 0x20; private int C2DutyCycle => ApuC2a & 0xc0;
        private uint C2EnvDelay => (uint)((ApuC2a & 0x0f) + 1); private int C2SweepOn => ApuC2b & 0x80; private int C2SweepIncDec => ApuC2b & 0x08;
        private int C2SweepShifts => ApuC2b & 0x07; private uint C2SweepDelay => (uint)(((ApuC2b & 0x70) >> 4) + 1); private uint C2FreqLimit => ApuFreqLimit[ApuC2b & 0x07];
        private int C3Holdnote => ApuC3a & 0x80; private uint C3LinearLength => (uint)((ApuC3a & 0x7f) << 6);
        private byte C3LengthCounter => ApuAtl[(ApuC3d & 0xf8) >> 3]; private uint C3Freq => (uint)(((ApuC3d & 0x07) << 8) + ApuC3c);
        private int C4Vol => ApuC4a & 0x0f; private uint C4EnvDelay => (uint)((ApuC4a & 0x0f) + 1); private int C4Env => ApuC4a & 0x10; private int C4Hold => ApuC4a & 0x20;
        private uint C4Freq => ApuNoiseFreq[ApuC4c & 0x0f]; private int C4Small => ApuC4c & 0x80; private byte C4LengthCounter => (byte)(ApuAtl[ApuC4d >> 3] << 1);

        /// <summary>The DWORD "Phase &lt; 0" test of the original: an unsigned value is never below zero, so the loop it guards never runs.</summary>
        private static bool BelowZero(uint v) => false;

        // ---- host side ----
        private int cycleAcc;
        private readonly float[] outRing = new float[16384]; private int outW, outR, outN;
        private float dcIn, dcOut;
        public int ChannelMask = 0x1F;

        /// <summary>$4000-$4013 and $4015 writes (K6502_rw.h): queued for the next V-sync render.</summary>
        public void Write(ushort addr, byte value)
        {
            byte type;
            if (addr >= 0x4000 && addr <= 0x4013) type = (byte)(addr & 0x1f);
            else if (addr == 0x4015) type = APUET_W_CTRL;
            else return;
            if (cur_event >= APU_EVENT_MAX) return;
            evTime[cur_event] = 0; evType[cur_event] = type; evData[cur_event] = value; cur_event++;
        }

        public void Clock(int cpuCycles)
        {
            cycleAcc += cpuCycles;
            while (cycleAcc >= CyclesPerFrame) { cycleAcc -= CyclesPerFrame; Vsync(); }
        }

        public int Queued => outN;
        public int Read(Span<float> dest)
        {
            int n = Math.Min(dest.Length, outN);
            for (int i = 0; i < n; i++) { dest[i] = outRing[outR]; outR = (outR + 1) & (outRing.Length - 1); }
            outN -= n; return n;
        }
        public void ClearOutput() { outW = outR = outN = 0; dcIn = dcOut = 0; }

        // ===== Rectangle #1 =====
        private int WriteWave1(int cycles, int ev)
        {
            while (ev < cur_event && evTime[ev] < cycles)
            {
                if ((evType[ev] & APUET_MASK) == APUET_C1)
                {
                    switch (evType[ev] & 0x03)
                    {
                        case 0: ApuC1a = evData[ev]; ApuC1Wave = pulse_waves[C1DutyCycle >> 6]; break;
                        case 1: ApuC1b = evData[ev]; break;
                        case 2:
                            ApuC1c = evData[ev];
                            ApuC1Freq = (uint)(((ApuC1d & 0x07) << 8) + ApuC1c); ApuC1Atl = ApuAtl[(ApuC1d & 0xf8) >> 3];
                            ApuC1Skip = ApuC1Freq != 0 ? (ApuPulseMagic << 1) / ApuC1Freq : 0; break;
                        case 3:
                            ApuC1d = evData[ev];
                            ApuC1Freq = (uint)(((ApuC1d & 0x07) << 8) + ApuC1c); ApuC1Atl = ApuAtl[(ApuC1d & 0xf8) >> 3];
                            ApuC1Skip = ApuC1Freq != 0 ? (ApuPulseMagic << 1) / ApuC1Freq : 0; break;
                    }
                }
                else if (evType[ev] == APUET_W_CTRL)
                {
                    ApuCtrlNew = evData[ev];
                    if ((evData[ev] & 1) == 0) ApuC1Atl = 0;
                }
                ev++;
            }
            return ev;
        }

        private void RenderWave1()
        {
            int cycles = 0, ev = 0; var buf = wave_buffers[0];
            ApuCtrlNew = ApuCtrl;
            for (int i = 0; i < ApuSamplesPerSync; i++)
            {
                cycles += ApuCyclesPerSample;
                ev = WriteWave1(cycles, ev);

                ApuC1EnvPhase -= 4;
                while (BelowZero(ApuC1EnvPhase))
                {
                    ApuC1EnvPhase += C1EnvDelay;
                    if (C1Hold != 0) ApuC1EnvVol = (byte)((ApuC1EnvVol + 1) & 0x0f);
                    else if (ApuC1EnvVol < 0x0f) ApuC1EnvVol++;
                }

                if (ApuC1Freq < 8 || (C1SweepIncDec == 0 && ApuC1Freq > C1FreqLimit)) { buf[i] = 0; break; }

                if (C1SweepOn != 0 && C1SweepShifts != 0)
                {
                    ApuC1SweepPhase -= 2;
                    while (BelowZero(ApuC1SweepPhase))
                    {
                        ApuC1SweepPhase += C1SweepDelay;
                        if (C1SweepIncDec != 0) ApuC1Freq += ~(ApuC1Freq >> C1SweepShifts);
                        else ApuC1Freq += (ApuC1Freq >> C1SweepShifts);
                    }
                    if (ApuC1Freq != 0) ApuC1Skip = (ApuPulseMagic << 1) / ApuC1Freq;
                }

                if ((ApuCtrlNew & 0x01) != 0 && (ApuC1Atl != 0 || C1Hold != 0))
                {
                    ApuC1Index += ApuC1Skip; ApuC1Index &= 0x1fffffff;
                    buf[i] = C1Env != 0 ? (byte)(ApuC1Wave[ApuC1Index >> 24] * (C1Vol + ApuC1EnvVol)) : (byte)(ApuC1Wave[ApuC1Index >> 24] * C1Vol);
                }
                else buf[i] = 0;
            }
            if (ApuC1Atl != 0) ApuC1Atl--;
        }

        // ===== Rectangle #2 =====
        private int WriteWave2(int cycles, int ev)
        {
            while (ev < cur_event && evTime[ev] < cycles)
            {
                if ((evType[ev] & APUET_MASK) == APUET_C2)
                {
                    switch (evType[ev] & 0x03)
                    {
                        case 0: ApuC2a = evData[ev]; ApuC2Wave = pulse_waves[C2DutyCycle >> 6]; break;
                        case 1: ApuC2b = evData[ev]; break;
                        case 2:
                            ApuC2c = evData[ev];
                            ApuC2Freq = (uint)(((ApuC2d & 0x07) << 8) + ApuC2c); ApuC2Atl = ApuAtl[(ApuC2d & 0xf8) >> 3];
                            ApuC2Skip = ApuC2Freq != 0 ? (ApuPulseMagic << 1) / ApuC2Freq : 0; break;
                        case 3:
                            ApuC2d = evData[ev];
                            ApuC2Freq = (uint)(((ApuC2d & 0x07) << 8) + ApuC2c); ApuC2Atl = ApuAtl[(ApuC2d & 0xf8) >> 3];
                            ApuC2Skip = ApuC2Freq != 0 ? (ApuPulseMagic << 1) / ApuC2Freq : 0; break;
                    }
                }
                else if (evType[ev] == APUET_W_CTRL)
                {
                    ApuCtrlNew = evData[ev];
                    if ((evData[ev] & 2) == 0) ApuC2Atl = 0;
                }
                ev++;
            }
            return ev;
        }

        private void RenderWave2()
        {
            int cycles = 0, ev = 0; var buf = wave_buffers[1];
            ApuCtrlNew = ApuCtrl;
            for (int i = 0; i < ApuSamplesPerSync; i++)
            {
                cycles += ApuCyclesPerSample;
                ev = WriteWave2(cycles, ev);

                ApuC2EnvPhase -= 4;
                while (BelowZero(ApuC2EnvPhase))
                {
                    ApuC2EnvPhase += C2EnvDelay;
                    if (C2Hold != 0) ApuC2EnvVol = (byte)((ApuC2EnvVol + 1) & 0x0f);
                    else if (ApuC2EnvVol < 0x0f) ApuC2EnvVol++;
                }

                if (ApuC2Freq < 8 || (C2SweepIncDec == 0 && ApuC2Freq > C2FreqLimit)) { buf[i] = 0; break; }

                if (C2SweepOn != 0 && C2SweepShifts != 0)
                {
                    ApuC2SweepPhase -= 2;
                    while (BelowZero(ApuC2SweepPhase))
                    {
                        ApuC2SweepPhase += C2SweepDelay;
                        if (C2SweepIncDec != 0) ApuC2Freq -= ~(ApuC2Freq >> C2SweepShifts);   // the wrong-way sweep of square 2 (unreachable, see header)
                        else ApuC2Freq += (ApuC2Freq >> C2SweepShifts);
                    }
                    if (ApuC2Freq != 0) ApuC2Skip = (ApuPulseMagic << 1) / ApuC2Freq;
                }

                if ((ApuCtrlNew & 0x02) != 0 && (ApuC2Atl != 0 || C2Hold != 0))
                {
                    ApuC2Index += ApuC2Skip; ApuC2Index &= 0x1fffffff;
                    buf[i] = C2Env != 0 ? (byte)(ApuC2Wave[ApuC2Index >> 24] * (C2Vol + ApuC2EnvVol)) : (byte)(ApuC2Wave[ApuC2Index >> 24] * C2Vol);
                }
                else buf[i] = 0;
            }
            if (ApuC2Atl != 0) ApuC2Atl--;
        }

        // ===== Triangle =====
        private int WriteWave3(int cycles, int ev)
        {
            while (ev < cur_event && evTime[ev] < cycles)
            {
                if ((evType[ev] & APUET_MASK) == APUET_C3)
                {
                    switch (evType[ev] & 3)
                    {
                        case 0: ApuC3a = evData[ev]; ApuC3Llc = C3LinearLength; break;
                        case 1: ApuC3b = evData[ev]; break;
                        case 2: ApuC3c = evData[ev]; ApuC3Skip = C3Freq != 0 ? ApuTriangleMagic / C3Freq : 0; break;
                        case 3: ApuC3d = evData[ev]; ApuC3Atl = C3LengthCounter; ApuC3Skip = C3Freq != 0 ? ApuTriangleMagic / C3Freq : 0; break;
                    }
                }
                else if (evType[ev] == APUET_W_CTRL)
                {
                    ApuCtrlNew = evData[ev];
                    if ((evData[ev] & 4) == 0) { ApuC3Atl = 0; ApuC3Llc = 0; }
                }
                ev++;
            }
            return ev;
        }

        private void RenderWave3()
        {
            int cycles = 0, ev = 0; var buf = wave_buffers[2];
            ApuCtrlNew = ApuCtrl;
            for (int i = 0; i < ApuSamplesPerSync; i++)
            {
                cycles += ApuCyclesPerSample;
                ev = WriteWave3(cycles, ev);

                if (C3Freq < 8) { buf[i] = 0; break; }

                if (ApuC3CounterStarted != 0)
                {
                    if (ApuC3Atl > 0 && C3Holdnote == 0) ApuC3Atl--;
                    if (ApuC3Llc > 0) ApuC3Llc--;
                }
                else if (C3Holdnote == 0 && ApuC3WriteLatency > 0)
                {
                    if (--ApuC3WriteLatency == 0) ApuC3CounterStarted = 0x01;
                }

                if ((ApuCtrlNew & 0x04) != 0 && ((ApuC3Atl > 0 || C3Holdnote != 0) && ApuC3Llc > 0))
                {
                    ApuC3Index += ApuC3Skip; ApuC3Index &= 0x1fffffff;
                    buf[i] = triangle_50[ApuC3Index >> 24];
                }
                else buf[i] = 0;
            }
        }

        // ===== Noise =====
        private int WriteWave4(int cycles, int ev)
        {
            while (ev < cur_event && evTime[ev] < cycles)
            {
                if ((evType[ev] & APUET_MASK) == APUET_C4)
                {
                    switch (evType[ev] & 3)
                    {
                        case 0: ApuC4a = evData[ev]; break;
                        case 1: ApuC4b = evData[ev]; break;
                        case 2:
                            ApuC4c = evData[ev];
                            ApuC4Sr = C4Small != 0 ? 0x001fu : 0x01ffu;
                            ApuC4Skip = C4Freq != 0 ? ApuNoiseMagic / C4Freq : 0;
                            ApuC4Atl = C4LengthCounter; break;
                        case 3:
                            ApuC4d = evData[ev];
                            ApuC4Skip = C4Freq != 0 ? ApuNoiseMagic / C4Freq : 0;
                            ApuC4Atl = C4LengthCounter; break;
                    }
                }
                else if (evType[ev] == APUET_W_CTRL)
                {
                    ApuCtrlNew = evData[ev];
                    if ((evData[ev] & 8) == 0) ApuC4Atl = 0;
                }
                ev++;
            }
            return ev;
        }

        private void RenderWave4()
        {
            int cycles = 0, ev = 0; var buf = wave_buffers[3];
            ApuCtrlNew = ApuCtrl;
            for (int i = 0; i < ApuSamplesPerSync; i++)
            {
                cycles += ApuCyclesPerSample;
                ev = WriteWave4(cycles, ev);

                ApuC4EnvPhase -= 4;
                while (BelowZero(ApuC4EnvPhase))
                {
                    ApuC4EnvPhase += C4EnvDelay;
                    if (C4Hold != 0) ApuC4EnvVol = (byte)((ApuC4EnvVol + 1) & 0x0f);
                    else if (ApuC4EnvVol < 0x0f) ApuC4EnvVol++;
                }

                if ((ApuCtrlNew & 0x08) != 0)
                {
                    ApuC4Index += ApuC4Skip;
                    if (ApuC4Index > 0x1fffffff)
                    {
                        if (C4Small != 0) ApuC4Sr |= (uint)((((ApuC4Sr & 1) == 0 ? 1 : 0) ^ ((ApuC4Sr & 4) == 0 ? 1 : 0)) << 5);
                        else ApuC4Sr |= (uint)((((ApuC4Sr & 1) == 0 ? 1 : 0) ^ ((ApuC4Sr & 16) == 0 ? 1 : 0)) << 9);
                        ApuC4Sr >>= 1;
                    }
                    ApuC4Index &= 0x1fffffff;

                    if (ApuC4Atl != 0 && (ApuC4Sr & 1) != 0) buf[i] = C4Env == 0 ? (byte)C4Vol : (byte)(ApuC4EnvVol ^ 0x0f);
                    else buf[i] = 0;
                }
                else buf[i] = 0;
            }
            if (ApuC4Atl != 0 && C4Hold == 0) ApuC4Atl--;
        }

        // ===== DPCM =====
        private int WriteWave5(int cycles, int ev)
        {
            while (ev < cur_event && evTime[ev] < cycles)
            {
                if ((evType[ev] & APUET_MASK) == APUET_C5)
                {
                    byte d = evData[ev];
                    ApuC5Reg[evType[ev] & 3] = d;
                    switch (evType[ev] & 3)
                    {
                        case 0: ApuC5Freq = (int)(ApuDpcmCycles[d & 0x0F] << 16); ApuC5Looping = (byte)(d & 0x40); break;
                        case 1: ApuC5DpcmValue = (byte)((d & 0x7F) >> 1); break;
                        case 2: ApuC5CacheAddr = (ushort)(0xC000 + (ushort)(d << 6)); break;
                        case 3: ApuC5CacheDmaLength = ((d << 4) + 1) << 3; break;
                    }
                }
                else if (evType[ev] == APUET_W_CTRL)
                {
                    ApuCtrlNew = evData[ev];
                    if ((evData[ev] & 0x10) == 0) { ApuC5Enable = 0; ApuC5DmaLength = 0; }
                    else
                    {
                        ApuC5Enable = 0xFF;
                        if (ApuC5DmaLength == 0) { ApuC5Address = ApuC5CacheAddr; ApuC5DmaLength = ApuC5CacheDmaLength; }
                    }
                }
                ev++;
            }
            return ev;
        }

        private void RenderWave5()
        {
            int cycles = 0, ev = 0; var buf = wave_buffers[4];
            ApuCtrlNew = ApuCtrl;
            for (int i = 0; i < ApuSamplesPerSync; i++)
            {
                cycles += ApuCyclesPerSample;
                ev = WriteWave5(cycles, ev);

                if (ApuC5DmaLength != 0 && ApuC5Freq > 0)   // "> 0": the original hangs here when $4010 was never written
                {
                    ApuC5Phaseacc = unchecked(ApuC5Phaseacc - (int)ApuCycleRate);
                    while (ApuC5Phaseacc < 0)
                    {
                        ApuC5Phaseacc += ApuC5Freq;
                        if ((ApuC5DmaLength & 7) == 0)
                        {
                            ApuC5CurByte = read(ApuC5Address);
                            if (ApuC5Address == 0xFFFF) ApuC5Address = 0x8000; else ApuC5Address++;
                        }
                        if (--ApuC5DmaLength == 0)
                        {
                            if (ApuC5Looping != 0) { ApuC5Address = ApuC5CacheAddr; ApuC5DmaLength = ApuC5CacheDmaLength; }
                            else { ApuC5Enable = 0; break; }
                        }
                        if ((ApuC5CurByte & (1 << ((ApuC5DmaLength & 7) ^ 7))) != 0) { if (ApuC5DpcmValue < 0x3F) ApuC5DpcmValue += 1; }
                        else { if (ApuC5DpcmValue > 1) ApuC5DpcmValue -= 1; }
                    }
                }

                if ((ApuCtrlNew & 0x10) != 0) buf[i] = (byte)((ApuC5Reg[1] & 0x01) + (ApuC5DpcmValue << 1));
            }
        }

        // ===== InfoNES_pAPUVsync + InfoNES_SoundOutput (win32) =====
        private void Vsync()
        {
            RenderWave1(); RenderWave2(); RenderWave3(); RenderWave4(); RenderWave5();
            ApuCtrl = ApuCtrlNew;

            var w1 = wave_buffers[0]; var w2 = wave_buffers[1]; var w3 = wave_buffers[2]; var w4 = wave_buffers[3]; var w5 = wave_buffers[4];
            int m = ChannelMask;
            for (int i = 0; i < ApuSamplesPerSync; i++)
            {
                int sum = ((m & 1) != 0 ? w1[i] : 0) + ((m & 2) != 0 ? w2[i] : 0) + ((m & 4) != 0 ? w3[i] : 0) + ((m & 8) != 0 ? w4[i] : 0) + ((m & 16) != 0 ? w5[i] : 0);
                byte wave = (byte)(sum / 5);   // unsigned 8-bit PCM
                float x = wave / 128f;
                float y = x - dcIn + 0.995f * dcOut; dcIn = x; dcOut = y;   // AC coupling (see header)
                if (outN == outRing.Length) { outR = (outR + 1) & (outRing.Length - 1); outN--; }
                outRing[outW] = y; outW = (outW + 1) & (outRing.Length - 1); outN++;
            }
            cur_event = 0;
        }

        /// <summary>InfoNES_pAPUInit (quality 3).</summary>
        public void Init()
        {
            ApuCtrl = ApuCtrlNew = 0;
            ApuC1Wave = pulse_50; ApuC2Wave = pulse_50;
            ApuC1a = ApuC1b = ApuC1c = ApuC1d = 0; ApuC2a = ApuC2b = ApuC2c = ApuC2d = 0; ApuC4a = ApuC4b = ApuC4c = ApuC4d = 0;
            ApuC1Skip = ApuC2Skip = ApuC4Skip = 0; ApuC1Index = ApuC2Index = ApuC4Index = 0;
            ApuC1EnvPhase = ApuC2EnvPhase = ApuC4EnvPhase = 0; ApuC1EnvVol = ApuC2EnvVol = ApuC4EnvVol = 0;
            ApuC1Atl = ApuC2Atl = ApuC4Atl = 0; ApuC1SweepPhase = ApuC2SweepPhase = 0; ApuC1Freq = ApuC2Freq = 0; ApuC4Sr = 0;
            ApuC3a = ApuC3b = ApuC3c = ApuC3d = 0; ApuC3Atl = 0; ApuC3Llc = 0; ApuC3WriteLatency = 3; ApuC3CounterStarted = 0;
            ApuC3Skip = ApuC3Index = 0;
            Array.Clear(ApuC5Reg); ApuC5Enable = ApuC5Looping = ApuC5CurByte = ApuC5DpcmValue = 0;
            ApuC5Freq = ApuC5Phaseacc;   // sic: the original copies the (zero) phase accumulator
            ApuC5Address = ApuC5CacheAddr = 0; ApuC5DmaLength = ApuC5CacheDmaLength = 0;
            foreach (var b in wave_buffers) Array.Clear(b);
            cur_event = 0; cycleAcc = 0;
            ClearOutput();
        }
    }
}
