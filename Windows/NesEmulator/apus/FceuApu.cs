// =====================================================================================================================================
// FceuApu - the sound engine of FCE Ultra 0.96, ported to C# (quick port, quirks kept).
//
// Source project : FCE Ultra (Xodnizel, Ben Parnell et al.)
// Version        : 0.96 (fceu096src.tar.gz, 2003), the last version before 0.98.1 reworked its expansion audio
// Files ported   : sound.c (2A03 APU, the default LOW-quality path: RDoSQLQ / RDoTriangleLQ / RDoNoiseLQ / RDoPCMLQ, FrameSoundStuff,
//                  FlushEmulateSound, SetSoundVariables, PowerSound / ResetSound), sound.h, filter.c (SexyFilter only: the low-quality
//                  path never reaches NeoFilterSound / fcoeffs.h), mbshare/mmc5.c (the MMC5 sound part: Mapper5_SW, Do5SQ, Do5PCM, MMC5Sound).
// URL            : https://sourceforge.net/projects/fceultra/files/Source%20Code/0.96/fceu096src.tar.gz
// Copyright      : (C) 2002 Ben Parnell (sound.c, sound.h, filter.c, mmc5.c), (C) 2002 Xodnizel; FCE Ultra contributors.
// License        : GNU General Public License, version 2 or (at your option) any later version.
// This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
// Defaults kept : FSettings.soundq = 0 (both the Windows and the CLI driver ship with low quality), SndRate 44100 (Windows default),
//                 SoundVolume 100, NTSC. The engine oversamples 16x (OVERSAMPLE) into Wave[] and SexyFilter decimates to 44100 Hz once
//                 per emulated frame, like FlushEmulateSound did once per frame.
// Quirks kept   :
//  - Squares: 16-step duty counter (thresholds 2/4/8/12 of 16), amplitude volume*16*3/5; the sweep's negate subtracts an extra 1 on
//    square 1 only, the "step from 0 to full takes twice as long" looping-envelope hack, frequencies < 8 or > $7FF silent.
//  - Triangle: periods <= 4 output a flat mid level; when halted it holds its last level (static tcout), FCEU's own linear-counter
//    emulation (trimode / tricoop).
//  - Noise: a fixed-point step table (LQNFTable), the LFSR does NOT advance while the noise volume is 0.
//  - DMC: FCEU's rate table, PCMSizeIndex in bytes, the first byte fetched twice in PrepDPCM, the delta counter's "undo on overflow".
//  - The frame sequencer is FCEU's own (fhcnt/fhinc: a step every 7457.5 CPU cycles, 4 steps), not the 2A03's.
//  - SexyFilter: two one-pole stages whose output is INVERTED (acc1 - in + acc2), clipped to 16 bits.
//  - MMC5 (low quality): squares have NO envelope and NO length counter (the raw volume bits), the duty is a fixed-point fraction;
//    the PCM ($5011) is added at value<<2 per oversample (the "too loud MMC5 PCM" that 0.98.1 turned down), and read mode is silent.
//  - $4015 reads do not sync the engine (FCEU called DoPCM there); X6502_DMR's extra cycle is not added to the engine's timestamp.
// =====================================================================================================================================
using System;

namespace NesEmulator
{
    /// <summary>FCE Ultra 0.96's sound engine (low-quality default path) plus its MMC5 sound. Clocked in CPU cycles; produces 44100 Hz.</summary>
    public sealed class FceuApu
    {
        public const int SndRate = 44100;
        private const int OVERSAMPLE = 16;
        private const double NTSC_CPU = 1789772.7272727272727272;
        /// <summary>One NTSC frame of CPU cycles x3 (341*262/3 = 29780.67): FCEU flushed its sound once per emulated frame.</summary>
        private const int FrameCycles3 = 341 * 262;

        private readonly Func<ushort, byte> readMem;
        public FceuApu(Func<ushort, byte> readMem)
        {
            this.readMem = readMem;
            SetSoundVariables();
            PowerSound();
        }

        // ---- sound.c state ----
        private readonly uint[] Wave = new uint[2048 + 512];
        private readonly int[] WaveFinal = new int[2048 + 512];
        private byte trimode, tricoop;
        private readonly byte[] PSG = new byte[0x18];
        private readonly byte[] decvolume = new byte[3];
        private readonly byte[] realvolume = new byte[3];
        private static readonly int[] tal = { 2, 4, 8, 12 };
        private readonly int[] rdc = new int[2];
        private uint soundtsinc;
        private int lpcmer;
        private readonly int[] sqacc = new int[2];
        private readonly uint[] LQNFTable = new uint[0x10];
        private int PCMaccLQ;
        private byte sqnon;
        private ushort nreg;
        private readonly int[] lengthcount = new int[4];
        private static readonly byte[] Slengthtable =
        {
            0x5,0x7f,0xA,0x1,0x14,0x2,0x28,0x3,0x50,0x4,0x1E,0x5,0x7,0x6,0x0E,0x7,
            0x6,0x08,0xC,0x9,0x18,0xa,0x30,0xb,0x60,0xc,0x24,0xd,0x8,0xe,0x10,0xf
        };
        private readonly uint[] lengthtable = new uint[0x20];
        private static readonly uint[] SNoiseFreqTable = { 2, 4, 8, 0x10, 0x20, 0x30, 0x40, 0x50, 0x65, 0x7f, 0xbe, 0xfe, 0x17d, 0x1fc, 0x3f9, 0x7f2 };
        private int nesincsize;
        private static readonly byte[] NTSCPCMTable = { 0xd6, 0xbe, 0xaa, 0xa0, 0x8f, 0x7f, 0x71, 0x6b, 0x5f, 0x50, 0x47, 0x40, 0x35, 0x2a, 0x24, 0x1b };
        private uint PSG_base;
        private int PCMIRQCount;
        private int PCMfreq;
        private byte PCMBitIndex;
        private uint PCMAddressIndex;
        private int PCMSizeIndex;
        private byte PCMBuffer;
        private readonly byte[] sweepon = new byte[2];
        private readonly int[] curfreq = new int[2];
        private readonly byte[] SweepCount = new byte[2];
        private readonly byte[] DecCountTo1 = new byte[3];
        private byte fcnt;
        private int fhcnt, fhinc;
        private readonly uint[] ChannelBC = new uint[5];
        private uint timestamp;
        // statics inside RDoTriangleLQ / RDoNoiseLQ / SexyFilter
        private uint triTcout; private int triacc; private byte triTc;
        private int noiseCount;
        private long acc1, acc2;

        // ---- mmc5.c sound state ----
        private readonly byte[] MMC5PSG = new byte[0x20];
        private readonly int[] C5BC = new int[3];
        private readonly int[] vcount = new int[2];
        private bool mmc5Fill;   // GameExpSound.Fill = MMC5Sound, set on the first $5000-$5015 write
        private int mmc5inc;

        // ---- host side ----
        private int frameAcc3;
        private const int RingSize = 16384;
        private readonly float[] ring = new float[RingSize];
        private int ringRead, ringWrite, ringCount;

        private static bool CheckFreq(uint cf, byte sr)
        {
            if ((sr & 0x8) == 0)
            {
                uint mod = cf >> (sr & 7);
                if (((mod + cf) & 0x800) != 0) return false;
            }
            return true;
        }

        private void PrepDPCM()
        {
            PCMAddressIndex = 0x4000u + ((uint)PSG[0x12] << 6);
            PCMSizeIndex = (PSG[0x13] << 4) + 1;
            PCMBitIndex = 0;
            PCMBuffer = readMem((ushort)(0x8000 + PCMAddressIndex));
            PCMfreq = NTSCPCMTable[PSG[0x10] & 0xF];
            PCMfreq *= 2;
            PCMaccLQ = PCMfreq << 17;
            PCMIRQCount = ((NTSCPCMTable[PSG[0x10] & 0xF] - ((PSG[0x10] & 0xF) ^ 0xF) - 1) << 4) * ((PSG[0x13] << 4) + 1);
            lpcmer = (int)timestamp;
        }

        private void DoSQ1() => RDoSQLQ(0);
        private void DoSQ2() => RDoSQLQ(1);
        private void DoTriangle() => RDoTriangleLQ();
        private void DoNoise() => RDoNoiseLQ();
        private void DoPCM() => RDoPCMLQ();

        private void SQReload(int x, byte V)
        {
            if ((PSG[0x15] & (1 << x)) != 0)
            {
                if (x != 0) DoSQ2(); else DoSQ1();
                lengthcount[x] = (int)lengthtable[(V >> 3) & 0x1f];
                sqnon |= (byte)(1 << x);
            }
            sweepon[x] = (byte)(PSG[(x << 2) | 1] & 0x80);
            curfreq[x] = PSG[(x << 2) | 0x2] | ((V & 7) << 8);
            decvolume[x] = 0xF;
            DecCountTo1[x] = (byte)((PSG[(x << 2)] & 0xF) + 1);
            SweepCount[x] = (byte)(((PSG[(x << 2) | 0x1] >> 4) & 7) + 1);
            rdc[x] = 0;
        }

        /// <summary>A CPU write to $4000-$4017 (SetNESSoundMap: $4000-$4013, $4015, $4017; $4011 goes to Write0x11).</summary>
        public void Write(ushort address, byte V)
        {
            if (address < 0x4000 || address > 0x4017 || address == 0x4014 || address == 0x4016) return;
            if (address == 0x4011) { DoPCM(); PSG[0x11] = (byte)(V & 0x7F); return; }
            int A = address & 0x1f;
            switch (A)
            {
                case 0x0: DoSQ1(); if ((V & 0x10) != 0) realvolume[0] = (byte)(V & 0xF); break;
                case 0x1: sweepon[0] = (byte)(V & 0x80); break;
                case 0x2: DoSQ1(); curfreq[0] &= 0xFF00; curfreq[0] |= V; break;
                case 0x3: SQReload(0, V); break;
                case 0x4: DoSQ2(); if ((V & 0x10) != 0) realvolume[1] = (byte)(V & 0xF); break;
                case 0x5: sweepon[1] = (byte)(V & 0x80); break;
                case 0x6: DoSQ2(); curfreq[1] &= 0xFF00; curfreq[1] |= V; break;
                case 0x7: SQReload(1, V); break;
                case 0x8: DoTriangle(); if (trimode != 0) tricoop = (byte)(V & 0x7F); break;
                case 0xa: DoTriangle(); break;
                case 0xb:
                    if ((PSG[0x15] & 0x4) != 0)
                    {
                        DoTriangle();
                        sqnon |= 4;
                        lengthcount[2] = (int)lengthtable[(V >> 3) & 0x1f];
                    }
                    trimode = 1;
                    if ((PSG[0x8] & 0x80) != 0) tricoop = (byte)(PSG[0x8] & 0x7F);
                    break;
                case 0xC: DoNoise(); if ((V & 0x10) != 0) realvolume[2] = (byte)(V & 0xF); break;
                case 0xE: DoNoise(); break;
                case 0xF:
                    if ((PSG[0x15] & 8) != 0)
                    {
                        DoNoise();
                        sqnon |= 8;
                        lengthcount[3] = (int)lengthtable[(V >> 3) & 0x1f];
                    }
                    decvolume[2] = 0xF;
                    DecCountTo1[2] = (byte)((PSG[0xC] & 0xF) + 1);
                    break;
                case 0x10: DoPCM(); break;   // X6502_IRQEnd: the stock APU underneath owns the IRQs
                case 0x15:
                    {
                        int t = V ^ PSG[0x15];
                        if ((t & 1) != 0) DoSQ1();
                        if ((t & 2) != 0) DoSQ2();
                        if ((t & 4) != 0) DoTriangle();
                        if ((t & 8) != 0) DoNoise();
                        if ((t & 0x10) != 0) DoPCM();
                        sqnon &= V;
                        if ((V & 0x10) != 0)
                        {
                            if ((PSG[0x15] & 0x10) == 0) PrepDPCM();
                        }
                        else PCMIRQCount = 0;
                    }
                    break;
                case 0x17:
                    V &= 0xC0;
                    fcnt = 0;
                    if ((V & 0x80) != 0) FrameSoundUpdate();
                    fhcnt = fhinc;
                    break;
            }
            PSG[A] = V;
        }

        private void FrameSoundStuff(int V)
        {
            int P;
            DoSQ1(); DoSQ2(); DoNoise();
            if ((V & 1) == 1)
            {
                if ((PSG[0x15] & 4) != 0 && (sqnon & 4) != 0)
                    if ((PSG[8] & 0x80) == 0)
                    {
                        if (lengthcount[2] > 0)
                        {
                            lengthcount[2]--;
                            if (lengthcount[2] <= 0) { DoTriangle(); sqnon &= unchecked((byte)~4); }
                        }
                    }

                for (P = 0; P < 2; P++)
                {
                    if ((PSG[0x15] & (P + 1)) != 0 && (sqnon & (P + 1)) != 0)
                    {
                        if ((PSG[P << 2] & 0x20) == 0)
                        {
                            if (lengthcount[P] > 0)
                            {
                                lengthcount[P]--;
                                if (lengthcount[P] <= 0) sqnon &= (byte)~(P + 1);
                            }
                        }
                    }
                    if (sweepon[P] != 0)
                    {
                        int mod = 0;
                        if (SweepCount[P] > 0) SweepCount[P]--;
                        if (SweepCount[P] <= 0)
                        {
                            SweepCount[P] = (byte)(((PSG[(P << 2) + 0x1] >> 4) & 7) + 1);
                            if ((PSG[(P << 2) + 0x1] & 0x8) != 0)
                            {
                                mod -= (P ^ 1) + ((curfreq[P]) >> (PSG[(P << 2) + 0x1] & 7));
                                if (curfreq[P] != 0 && (PSG[(P << 2) + 0x1] & 7) != 0) curfreq[P] += mod;
                            }
                            else
                            {
                                mod = curfreq[P] >> (PSG[(P << 2) + 0x1] & 7);
                                if (((mod + curfreq[P]) & 0x800) != 0) { sweepon[P] = 0; curfreq[P] = 0; }
                                else if (curfreq[P] != 0 && (PSG[(P << 2) + 0x1] & 7) != 0) curfreq[P] += mod;
                            }
                        }
                    }
                }

                if ((PSG[0x15] & 0x8) != 0 && (sqnon & 8) != 0)
                {
                    if ((PSG[0xC] & 0x20) == 0)
                    {
                        if (lengthcount[3] > 0)
                        {
                            lengthcount[3]--;
                            if (lengthcount[3] <= 0) sqnon &= unchecked((byte)~8);
                        }
                    }
                }
            }
            // case 0 (case 1 falls through): envelope decay + linear counter
            if ((PSG[0x8] & 0x80) == 0 && trimode != 0)
            {
                trimode = 0;
                tricoop = (byte)(PSG[0x8] & 0x7F);
            }
            if (trimode == 0)
            {
                if (tricoop != 0)
                {
                    if (tricoop == 1) DoTriangle();
                    tricoop--;
                }
            }
            for (P = 0; P < 2; P++)
            {
                if (DecCountTo1[P] > 0) DecCountTo1[P]--;
                if (DecCountTo1[P] <= 0)
                {
                    DecCountTo1[P] = (byte)((PSG[P << 2] & 0xF) + 1);
                    if (decvolume[P] != 0 || (PSG[P << 2] & 0x20) != 0)
                    {
                        decvolume[P]--;
                        if ((PSG[P << 2] & 0x20) != 0 && decvolume[P] == 0) DecCountTo1[P] <<= 1;
                        decvolume[P] &= 15;
                    }
                }
                if ((PSG[P << 2] & 0x10) == 0) realvolume[P] = decvolume[P];
            }
            if (DecCountTo1[2] > 0) DecCountTo1[2]--;
            if (DecCountTo1[2] <= 0)
            {
                DecCountTo1[2] = (byte)((PSG[0xC] & 0xF) + 1);
                if (decvolume[2] != 0 || (PSG[0xC] & 0x20) != 0)
                {
                    decvolume[2]--;
                    if ((PSG[0xC] & 0x20) != 0 && decvolume[2] == 0) DecCountTo1[2] <<= 1;
                    decvolume[2] &= 15;
                }
            }
            if ((PSG[0xC] & 0x10) == 0) realvolume[2] = decvolume[2];
        }

        private void FrameSoundUpdate()
        {
            if (fcnt == 3)
            {
                if ((PSG[0x17] & 0x80) != 0) fhcnt += fhinc;
                // frame IRQ: owned by the stock APU underneath
            }
            FrameSoundStuff(fcnt);
            fcnt = (byte)((fcnt + 1) & 3);
        }

        private bool PCMFreak()
        {
            int t;
            byte bah = PSG[0x11];
            if (PCMBitIndex == 0)
            {
                if (PCMSizeIndex == 0)
                {
                    if ((PSG[0x10] & 0x40) != 0) PrepDPCM();
                    else { PSG[0x15] &= unchecked((byte)~0x10); return false; }
                }
                else
                {
                    // (soundq == 0: no PCMacc-=1)
                    PCMBuffer = readMem((ushort)(0x8000 + PCMAddressIndex));
                    PCMAddressIndex = (PCMAddressIndex + 1) & 0x7fff;
                }
                PCMSizeIndex--;
            }
            t = (((PCMBuffer >> PCMBitIndex) & 1) << 2) - 2;
            PSG[0x11] = (byte)(PSG[0x11] + t);
            if ((PSG[0x11] & 0x80) != 0) PSG[0x11] = bah;
            PCMBitIndex = (byte)((PCMBitIndex + 1) & 7);
            return true;
        }

        private int EndPos() => (int)((timestamp << 16) / soundtsinc);

        private void RDoPCMLQ()
        {
            int V, start, end, freq;
            uint outv = (uint)((PSG[0x11] << 3) - (PSG[0x11] << 1));
            start = (int)ChannelBC[4];
            end = EndPos();
            if (end <= start) return;
            ChannelBC[4] = (uint)end;

            if (PCMIRQCount > 0)
            {
                int cycs = (int)timestamp - lpcmer;
                PCMIRQCount -= cycs;
                if (PCMIRQCount <= 0)
                {
                    if ((PSG[0x10] & 0x80) != 0 && (PSG[0x10] & 0x40) == 0) PSG[0x15] &= unchecked((byte)~0x10);
                }
            }
            lpcmer = (int)timestamp;

            if ((PSG[0x15] & 0x10) != 0)
            {
                freq = PCMfreq;
                freq <<= 17;
                for (V = start; V < end; V++)
                {
                    PCMaccLQ -= nesincsize;
                    if (PCMaccLQ <= 0)
                    {
                        if (!PCMFreak())
                        {
                            for (; V < end; V++) Wave[V >> 4] += (uint)((PSG[0x11] << 3) - (PSG[0x11] << 1));
                            return;
                        }
                        PCMaccLQ += freq;
                    }
                    Wave[V >> 4] += outv;
                }
            }
            else
            {
                if ((end - start) > 64)
                {
                    for (V = start; V <= (start | 15); V++) Wave[V >> 4] += outv;
                    outv <<= 4;
                    for (V = (start >> 4) + 1; V < (end >> 4); V++) Wave[V] += outv;
                    outv >>= 4;
                    for (V = end & ~15; V < end; V++) Wave[V >> 4] += outv;
                }
                else
                    for (V = start; V < end; V++) Wave[V >> 4] += outv;
            }
        }

        private void RDoSQLQ(int x)
        {
            int start, end, freq, V, amp, rthresh;
            start = (int)ChannelBC[x];
            end = EndPos();
            if (end <= start) return;
            ChannelBC[x] = (uint)end;

            if (curfreq[x] < 8 || curfreq[x] > 0x7ff) return;
            if (!CheckFreq((uint)curfreq[x], PSG[(x << 2) | 0x1])) return;
            if ((PSG[0x15] & sqnon & (1 << x)) == 0) return;

            amp = realvolume[x] * 16 * 3 / 5;
            rthresh = tal[(PSG[(x << 2)] & 0xC0) >> 6];
            freq = curfreq[x] + 1;
            freq <<= 17;
            for (V = start; V < end; V++)
            {
                if (rdc[x] < rthresh) Wave[V >> 4] += (uint)amp;
                sqacc[x] -= nesincsize;
                if (sqacc[x] <= 0)
                {
                    do
                    {
                        sqacc[x] += freq;
                        rdc[x] = (rdc[x] + 1) & 15;
                    } while (sqacc[x] <= 0);
                }
            }
        }

        private void RDoTriangleLQ()
        {
            int V, start, end;
            int freq = ((PSG[0xa] | ((PSG[0xb] & 7) << 8)) + 1);
            start = (int)ChannelBC[2];
            end = EndPos();
            if (end <= start) return;
            ChannelBC[2] = (uint)end;

            if (!((PSG[0x15] & 0x4) != 0 && (sqnon & 4) != 0 && tricoop != 0))
            {
                for (V = start; V < end; V++) Wave[V >> 4] += triTcout;
            }
            else if (freq <= 4)
            {
                for (V = start; V < end; V++) Wave[V >> 4] += (0xF << 4) >> 1;
            }
            else
            {
                freq <<= 17;
                for (V = start; V < end; V++)
                {
                    triacc -= nesincsize;
                    if (triacc <= 0)
                    {
                        do
                        {
                            triacc += freq;
                            triTc = (byte)((triTc + 1) & 0x1F);
                        } while (triacc <= 0);
                        triTcout = (uint)(triTc & 0xF);
                        if ((triTc & 0x10) != 0) triTcout ^= 0xF;
                        triTcout <<= 4;
                    }
                    Wave[V >> 4] += triTcout;
                }
            }
        }

        private void RDoNoiseLQ()
        {
            int inc, V, start, end;
            start = (int)ChannelBC[3];
            end = EndPos();
            if (end <= start) return;
            ChannelBC[3] = (uint)end;
            if ((PSG[0x15] & 0x8) != 0 && (sqnon & 8) != 0)
            {
                uint outo;
                uint a0 = (uint)(realvolume[2] << 3);
                inc = (int)LQNFTable[PSG[0xE] & 0xF];
                outo = ((nreg >> 0xe) & 1) == 0 ? a0 : 0;
                if (realvolume[2] != 0)
                {
                    int tap = (PSG[0xE] & 0x80) != 0 ? 8 : 13;   // "short" noise
                    for (V = start; V < end; V++)
                    {
                        Wave[V >> 4] += outo;
                        if (noiseCount >= inc)
                        {
                            int feedback = ((nreg >> tap) & 1) ^ ((nreg >> 14) & 1);
                            nreg = (ushort)(((nreg << 1) + feedback) & 0x7fff);
                            outo = ((nreg >> 0xe) & 1) == 0 ? a0 : 0;
                            noiseCount -= inc;
                        }
                        noiseCount += 0x4000;
                    }
                }
            }
        }

        // ---- MMC5 (mbshare/mmc5.c, low-quality sfun) ----

        /// <summary>A CPU write to $5000-$5015 (Mapper5_SW).</summary>
        public void WriteMmc5(ushort address, byte V)
        {
            if (address < 0x5000 || address > 0x5015) return;
            int A = address & 0x1F;
            mmc5Fill = true;
            switch (A)
            {
                case 0x10:
                case 0x11: Do5PCM(); break;
                case 0x0: case 0x2: case 0x3: Do5SQ(0); break;
                case 0x4: case 0x6: case 0x7: Do5SQ(1); break;
                case 0x15:
                    {
                        int t = V ^ MMC5PSG[0x15];
                        if ((t & 1) != 0) Do5SQ(0);
                        if ((t & 2) != 0) Do5SQ(1);
                    }
                    break;
            }
            MMC5PSG[A] = V;
        }

        private void Do5PCM()
        {
            int V, start, end;
            start = C5BC[2];
            end = EndPos();
            if (end <= start) return;
            C5BC[2] = end;
            if ((MMC5PSG[0x10] & 0x40) == 0 && MMC5PSG[0x11] != 0)
                for (V = start; V < end; V++) Wave[V >> 4] += (uint)(MMC5PSG[0x11] << 2);
        }

        private void Do5SQ(int P)
        {
            int start, end, V, freq;
            start = C5BC[P];
            end = EndPos();
            if (end <= start) return;
            C5BC[P] = end;
            if ((MMC5PSG[0x15] & (P + 1)) != 0)
            {
                uint dcycs;
                byte amplitude;
                long vcoo;
                freq = MMC5PSG[(P << 2) + 0x2] | ((MMC5PSG[(P << 2) + 0x3] & 7) << 8);
                if (freq < 8) return;
                freq += 1;
                mmc5inc = (int)((double)(SndRate * OVERSAMPLE) * 16384 / ((double)PSG_base / freq));
                switch (MMC5PSG[P << 2] & 0xC0)
                {
                    default:
                    case 0x00: dcycs = (uint)mmc5inc >> 3; break;
                    case 0x40: dcycs = (uint)mmc5inc >> 2; break;
                    case 0x80: dcycs = (uint)mmc5inc >> 1; break;
                    case 0xC0: dcycs = (uint)(mmc5inc + mmc5inc + mmc5inc) >> 2; break;
                }
                amplitude = (byte)((MMC5PSG[P << 2] & 15) << 4);
                vcoo = vcount[P];
                for (V = start; V < end; V++)
                {
                    if (vcoo < dcycs) Wave[V >> 4] += amplitude;
                    vcoo += 0x4000;
                    if (vcoo >= mmc5inc) vcoo -= mmc5inc;
                }
                vcount[P] = (int)vcoo;
            }
        }

        private void MMC5Sound(int Count)
        {
            Do5SQ(0); Do5SQ(1); Do5PCM();
            for (int x = 0; x < 3; x++) C5BC[x] = Count;
        }

        // ---- filter.c ----
        private void SexyFilter(int count)
        {
            int mul1 = (94 << 16) / SndRate;
            int mul2 = (24 << 16) / SndRate;
            int vmul = (100 << 16) / 100;
            for (int i = 0; i < count; i++)
            {
                long ino = (long)(int)Wave[i] * vmul;
                acc1 += ((ino - acc1) * mul1) >> 16;
                acc2 += ((ino - acc1 - acc2) * mul2) >> 16;
                Wave[i] = 0;
                int t = (int)((acc1 - ino + acc2) >> 16);
                if (t > 32767) t = 32767;
                if (t < -32768) t = -32768;
                WaveFinal[i] = t;
            }
        }

        private void FlushEmulateSound()
        {
            int x, end;
            if (timestamp == 0) return;
            DoSQ1(); DoSQ2(); DoTriangle(); DoNoise(); DoPCM();

            end = EndPos();
            if (mmc5Fill) MMC5Sound(end & 0xF);
            SexyFilter(end >> 4);
            if ((end & 0xF) != 0) Wave[0] = Wave[end >> 4];
            Wave[end >> 4] = 0;

            for (x = 0; x < 5; x++) ChannelBC[x] = (uint)(end & 0xF);
            timestamp = (uint)((soundtsinc * (uint)(end & 0xF)) >> 16);
            end >>= 4;
            lpcmer = (int)timestamp;

            for (int i = 0; i < end; i++)
            {
                ring[ringWrite] = WaveFinal[i] / 32768f;
                ringWrite = (ringWrite + 1) & (RingSize - 1);
                if (ringCount < RingSize) ringCount++; else ringRead = (ringRead + 1) & (RingSize - 1);
            }
        }

        // ---- the CPU loop (x6502.c) ----

        /// <summary>Advances the engine by <paramref name="cycles"/> CPU cycles, the way X6502_Run did after each instruction.</summary>
        public void Clock(int cycles)
        {
            if (cycles <= 0) return;
            timestamp += (uint)cycles;
            fhcnt -= cycles * 48;
            if (fhcnt <= 0)
            {
                FrameSoundUpdate();
                fhcnt += fhinc;
            }
            if ((PSG[0x15] & 0x10) != 0) DoPCM();
            frameAcc3 += cycles * 3;
            if (frameAcc3 >= FrameCycles3)
            {
                frameAcc3 -= FrameCycles3;
                FlushEmulateSound();
            }
        }

        private void SetSoundVariables()
        {
            int x;
            fhinc = 14915;
            fhinc *= 24;
            for (x = 0; x < 0x20; x++) lengthtable[x] = (uint)(Slengthtable[x] << 1);
            nesincsize = (int)(((long)1 << 17) * NTSC_CPU / (SndRate * OVERSAMPLE));
            Array.Clear(sqacc);
            Array.Clear(ChannelBC);
            for (x = 0; x < 0x10; x++)
            {
                double z = SNoiseFreqTable[x] << 1;
                z = NTSC_CPU / z;
                z = (double)(SndRate * OVERSAMPLE) * 16384 / z;
                LQNFTable[x] = (uint)z;
            }
            PSG_base = (uint)(NTSC_CPU / 16);
            soundtsinc = (uint)((ulong)(NTSC_CPU * 65536) / (ulong)(SndRate * OVERSAMPLE));
            // Mapper5_ESI
            Array.Clear(C5BC); Array.Clear(vcount);
        }

        private void PowerSound()
        {
            Array.Clear(PSG);
            curfreq[0] = curfreq[1] = 0;
            sqnon = 0;
            PSG[0x17] = 0;
            fhcnt = fhinc;
            fcnt = 0;
            nreg = 1;
            Array.Clear(Wave);
            Array.Clear(ChannelBC);
        }

        private void ResetSound()
        {
            for (int x = 0; x < 0x16; x++)
                if (x != 1 && x != 5 && x != 0x14) Write((ushort)(0x4000 + x), 0);
            PSG[0x17] = 0;
            fhcnt = fhinc;
            fcnt = 0;
            nreg = 1;
            for (int x = 0; x < 2; x++) sqacc[x] = (int)(((uint)2048 << 17) / (uint)nesincsize);
        }

        /// <summary>Power-on then reset (PowerSound + ResetSound), and an empty output queue.</summary>
        public void Reset()
        {
            PowerSound();
            ResetSound();
            Array.Clear(MMC5PSG); Array.Clear(C5BC); Array.Clear(vcount); mmc5Fill = false;
            ClearOutput();
        }

        // ---- output queue ----
        public int Queued => ringCount;
        public void ClearOutput() { ringRead = ringWrite = ringCount = 0; }
        public int Read(Span<float> dest)
        {
            int n = Math.Min(dest.Length, ringCount);
            for (int i = 0; i < n; i++) { dest[i] = ring[ringRead]; ringRead = (ringRead + 1) & (RingSize - 1); }
            ringCount -= n;
            return n;
        }
    }
}
