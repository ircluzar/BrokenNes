/*
 * Mame0124Ay8910 - MAME 0.124's AY-3-8910 / YM2149 sound chip (the pre-0.124u4 code, before couriersud's resistor-network rewrite) ported to C#.
 * Source project: MAME, tag mame0124, src/emu/sound/ay8910.c + ay8910.h.
 * URL: https://github.com/mamedev/mame/blob/mame0124/src/emu/sound/ay8910.c
 * Authors (as in its header): "Based on various code snippets by Ville Hallik, Michael Cuddy, Tatsuyuki Satoh, Fabrice Frances, Nicola Salmoria."
 * License: MAME license (non-commercial), see docs/license.txt of mame0124.
 * Sources as fetched (sha256): C:\BrokenNes-tools\apu-harvest\mame0124ay\MANIFEST.sha256.
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md
 *
 * Here the chip is clocked at 1789773 Hz (a NES-CPU-clocked AY: one AY input clock per NES CPU cycle), so the stream runs at clock/8 =
 * 223721.6 Hz as in MAME (stream_create(..., clock/8, ...)); that stream is box-averaged down to 44100 Hz (MAME's default output rate).
 * The resampler is a small stand-in for MAME 0.124's streams.c, not a port of it.
 *
 * Characteristic flaws kept on purpose (art instrument; do not fix):
 *  - Volume table: 32 steps of 1.5 dB from 0x7FFF down (/1.188502227 each, rounded), step 0 forced to 0; the fixed 4-bit amplitudes use
 *    the odd entries (v*2+1), i.e. the YM2149 table used for an AY. No resistor-load / output-mixing model at all (that came in 0.124u4).
 *  - Unipolar output: a channel is 0 or +Vol, so the mix carries a DC offset; the 3 channels are summed into one stream (streams == 1)
 *    and the sum clips at 16 bits (two channels at full amplitude already clip).
 *  - Counters count DOWN from the period in STEP (=2) fixed-point units; a period write adjusts the counter by the difference
 *    (CountX += new - old, clamped to 1), as the source's comment explains. Period 0 acts as 1; envelope period 0 is half of period 1.
 *  - Each output sample is the fraction of its STEP interval the square was high (vola/volb/volc averaging), times the volume, / STEP:
 *    so pitches above the stream rate alias into averaged DC levels instead of silence.
 *  - Disabled tones are forced high (OutputX = 1) and their counters pushed forward by length*STEP at each update - so the result
 *    depends on how often stream_update is called (here: before each changed register write, and once per Clock()).
 *  - Noise: 17-bit Galois LFSR (RNG ^= 0x24000 on bit 0), output toggles when bit0^bit1; clocked on the CountN events inside the
 *    STEP loop. If all three noise enables are off, CountN is pushed forward instead of run.
 *  - Envelope: always the YM2149 32-step behaviour (the AY's 16 steps are not modelled), stepped once per output sample (CountE -= STEP).
 *  - Ports A/B (regs 14/15) and their read/write callbacks are not wired (nothing behind them here); regs 14/15 are just stored.
 */
using System;

namespace NesEmulator
{
    public sealed class Mame0124Ay8910
    {
        public const int Clock = 1789773;     // NES-CPU-clocked AY (the original drivers run 1-2 MHz AYs; this one is fed the NES CPU clock)
        public const int SampleRate = 44100;  // MAME's default output rate
        private const int MAX_OUTPUT = 0x7fff;
        private const int STEP = 2;

        private const int AY_AFINE = 0, AY_ACOARSE = 1, AY_BFINE = 2, AY_BCOARSE = 3, AY_CFINE = 4, AY_CCOARSE = 5, AY_NOISEPER = 6, AY_ENABLE = 7,
            AY_AVOL = 8, AY_BVOL = 9, AY_CVOL = 10, AY_EFINE = 11, AY_ECOARSE = 12, AY_ESHAPE = 13, AY_PORTA = 14, AY_PORTB = 15;

        private int register_latch;
        private readonly byte[] Regs = new byte[16];
        private int lastEnable;
        private int PeriodA, PeriodB, PeriodC, PeriodN, PeriodE;
        private int CountA, CountB, CountC, CountN, CountE;
        private uint VolA, VolB, VolC, VolE;
        private byte EnvelopeA, EnvelopeB, EnvelopeC;
        private byte OutputA, OutputB, OutputC, OutputN;
        private sbyte CountEnv;
        private byte Hold, Alternate, Attack, Holding;
        private int RNG;
        private readonly uint[] VolTable = new uint[32];

        // stream glue (not from ay8910.c): CPU cycles -> clock/8 stream samples -> box-averaged 44100 Hz output ring
        private int cycleAcc;            // CPU cycles not yet turned into stream samples (8 per sample)
        private int pendingSamples;      // stream samples owed since the last stream_update
        private readonly int[] tmp = new int[4096];
        private double boxSum, boxPos;   // accumulated (sample * weight) and weight of the current output sample
        private const double SrcPerOut = Clock / 8.0 / SampleRate;  // ~5.073 stream samples per output sample
        private readonly float[] ring = new float[16384];
        private int head, count;

        public Mame0124Ay8910() { build_mixer_table(); Reset(); }

        private void build_mixer_table()
        {
            double o = MAX_OUTPUT;
            for (int i = 31; i > 0; i--)
            {
                VolTable[i] = (uint)(o + 0.5);   /* round to nearest */
                o /= 1.188502227;                /* = 10 ^ (1.5/20) = 1.5dB */
            }
            VolTable[0] = 0;
        }

        /// <summary>ay8910_reset_ym.</summary>
        public void Reset()
        {
            register_latch = 0;
            RNG = 1;
            OutputA = 0; OutputB = 0; OutputC = 0;
            OutputN = 0xff;
            lastEnable = -1;
            for (int i = 0; i < AY_PORTA; i++) _AYWriteReg(i, 0);
            cycleAcc = 0; pendingSamples = 0; boxSum = 0; boxPos = 0;
        }

        /// <summary>Register-select then data write (ay8910_write_ym addr 0, then addr 1).</summary>
        public void WriteReg(int r, int data)
        {
            WritePort(0, r);
            WritePort(1, data);
        }

        public void WritePort(int addr, int data)
        {
            if ((addr & 1) != 0)
            {
                int r = register_latch;
                if (r > 15) return;
                if (r < 14)
                {
                    if (r == AY_ESHAPE || Regs[r] != (byte)data)
                        StreamUpdate();   /* update the output buffer before changing the register */
                }
                _AYWriteReg(r, data);
            }
            else register_latch = data & 0x0f;
        }

        private void _AYWriteReg(int r, int v)
        {
            int old;
            Regs[r] = (byte)v;
            switch (r)
            {
                case AY_AFINE:
                case AY_ACOARSE:
                    Regs[AY_ACOARSE] &= 0x0f;
                    old = PeriodA;
                    PeriodA = (Regs[AY_AFINE] + 256 * Regs[AY_ACOARSE]) * STEP;
                    if (PeriodA == 0) PeriodA = STEP;
                    CountA += PeriodA - old;
                    if (CountA <= 0) CountA = 1;
                    break;
                case AY_BFINE:
                case AY_BCOARSE:
                    Regs[AY_BCOARSE] &= 0x0f;
                    old = PeriodB;
                    PeriodB = (Regs[AY_BFINE] + 256 * Regs[AY_BCOARSE]) * STEP;
                    if (PeriodB == 0) PeriodB = STEP;
                    CountB += PeriodB - old;
                    if (CountB <= 0) CountB = 1;
                    break;
                case AY_CFINE:
                case AY_CCOARSE:
                    Regs[AY_CCOARSE] &= 0x0f;
                    old = PeriodC;
                    PeriodC = (Regs[AY_CFINE] + 256 * Regs[AY_CCOARSE]) * STEP;
                    if (PeriodC == 0) PeriodC = STEP;
                    CountC += PeriodC - old;
                    if (CountC <= 0) CountC = 1;
                    break;
                case AY_NOISEPER:
                    Regs[AY_NOISEPER] &= 0x1f;
                    old = PeriodN;
                    PeriodN = Regs[AY_NOISEPER] * STEP;
                    if (PeriodN == 0) PeriodN = STEP;
                    CountN += PeriodN - old;
                    if (CountN <= 0) CountN = 1;
                    break;
                case AY_ENABLE:
                    // port A/B direction callbacks: nothing wired here
                    lastEnable = Regs[AY_ENABLE];
                    break;
                case AY_AVOL:
                    Regs[AY_AVOL] &= 0x1f;
                    EnvelopeA = (byte)(Regs[AY_AVOL] & 0x10);
                    VolA = EnvelopeA != 0 ? VolE : VolTable[Regs[AY_AVOL] != 0 ? Regs[AY_AVOL] * 2 + 1 : 0];
                    break;
                case AY_BVOL:
                    Regs[AY_BVOL] &= 0x1f;
                    EnvelopeB = (byte)(Regs[AY_BVOL] & 0x10);
                    VolB = EnvelopeB != 0 ? VolE : VolTable[Regs[AY_BVOL] != 0 ? Regs[AY_BVOL] * 2 + 1 : 0];
                    break;
                case AY_CVOL:
                    Regs[AY_CVOL] &= 0x1f;
                    EnvelopeC = (byte)(Regs[AY_CVOL] & 0x10);
                    VolC = EnvelopeC != 0 ? VolE : VolTable[Regs[AY_CVOL] != 0 ? Regs[AY_CVOL] * 2 + 1 : 0];
                    break;
                case AY_EFINE:
                case AY_ECOARSE:
                    old = PeriodE;
                    PeriodE = (Regs[AY_EFINE] + 256 * Regs[AY_ECOARSE]) * STEP;
                    if (PeriodE == 0) PeriodE = STEP / 2;
                    CountE += PeriodE - old;
                    if (CountE <= 0) CountE = 1;
                    break;
                case AY_ESHAPE:
                    Regs[AY_ESHAPE] &= 0x0f;
                    Attack = (byte)((Regs[AY_ESHAPE] & 0x04) != 0 ? 0x1f : 0x00);
                    if ((Regs[AY_ESHAPE] & 0x08) == 0)
                    {
                        /* if Continue = 0, map the shape to the equivalent one which has Continue = 1 */
                        Hold = 1;
                        Alternate = Attack;
                    }
                    else
                    {
                        Hold = (byte)(Regs[AY_ESHAPE] & 0x01);
                        Alternate = (byte)(Regs[AY_ESHAPE] & 0x02);
                    }
                    CountE = PeriodE;
                    CountEnv = 0x1f;
                    Holding = 0;
                    VolE = VolTable[CountEnv ^ Attack];
                    if (EnvelopeA != 0) VolA = VolE;
                    if (EnvelopeB != 0) VolB = VolE;
                    if (EnvelopeC != 0) VolC = VolE;
                    break;
                case AY_PORTA:
                case AY_PORTB:
                    break; // no port devices
            }
        }

        /// <summary>AY8910Update (streams == 1: the three channels summed into one buffer).</summary>
        private void AY8910Update(int[] buf1, int length)
        {
            int outn;
            int o = 0;

            if ((Regs[AY_ENABLE] & 0x01) != 0)
            {
                if (CountA <= length * STEP) CountA += length * STEP;
                OutputA = 1;
            }
            else if (Regs[AY_AVOL] == 0)
            {
                if (CountA <= length * STEP) CountA += length * STEP;
            }
            if ((Regs[AY_ENABLE] & 0x02) != 0)
            {
                if (CountB <= length * STEP) CountB += length * STEP;
                OutputB = 1;
            }
            else if (Regs[AY_BVOL] == 0)
            {
                if (CountB <= length * STEP) CountB += length * STEP;
            }
            if ((Regs[AY_ENABLE] & 0x04) != 0)
            {
                if (CountC <= length * STEP) CountC += length * STEP;
                OutputC = 1;
            }
            else if (Regs[AY_CVOL] == 0)
            {
                if (CountC <= length * STEP) CountC += length * STEP;
            }

            if ((Regs[AY_ENABLE] & 0x38) == 0x38) /* all off */
                if (CountN <= length * STEP) CountN += length * STEP;

            outn = (OutputN | Regs[AY_ENABLE]);

            while (length != 0)
            {
                int vola, volb, volc;
                int left;
                vola = volb = volc = 0;

                left = STEP;
                do
                {
                    int nextevent;
                    if (CountN < left) nextevent = CountN;
                    else nextevent = left;

                    if ((outn & 0x08) != 0)
                    {
                        if (OutputA != 0) vola += CountA;
                        CountA -= nextevent;
                        while (CountA <= 0)
                        {
                            CountA += PeriodA;
                            if (CountA > 0)
                            {
                                OutputA ^= 1;
                                if (OutputA != 0) vola += PeriodA;
                                break;
                            }
                            CountA += PeriodA;
                            vola += PeriodA;
                        }
                        if (OutputA != 0) vola -= CountA;
                    }
                    else
                    {
                        CountA -= nextevent;
                        while (CountA <= 0)
                        {
                            CountA += PeriodA;
                            if (CountA > 0) { OutputA ^= 1; break; }
                            CountA += PeriodA;
                        }
                    }

                    if ((outn & 0x10) != 0)
                    {
                        if (OutputB != 0) volb += CountB;
                        CountB -= nextevent;
                        while (CountB <= 0)
                        {
                            CountB += PeriodB;
                            if (CountB > 0)
                            {
                                OutputB ^= 1;
                                if (OutputB != 0) volb += PeriodB;
                                break;
                            }
                            CountB += PeriodB;
                            volb += PeriodB;
                        }
                        if (OutputB != 0) volb -= CountB;
                    }
                    else
                    {
                        CountB -= nextevent;
                        while (CountB <= 0)
                        {
                            CountB += PeriodB;
                            if (CountB > 0) { OutputB ^= 1; break; }
                            CountB += PeriodB;
                        }
                    }

                    if ((outn & 0x20) != 0)
                    {
                        if (OutputC != 0) volc += CountC;
                        CountC -= nextevent;
                        while (CountC <= 0)
                        {
                            CountC += PeriodC;
                            if (CountC > 0)
                            {
                                OutputC ^= 1;
                                if (OutputC != 0) volc += PeriodC;
                                break;
                            }
                            CountC += PeriodC;
                            volc += PeriodC;
                        }
                        if (OutputC != 0) volc -= CountC;
                    }
                    else
                    {
                        CountC -= nextevent;
                        while (CountC <= 0)
                        {
                            CountC += PeriodC;
                            if (CountC > 0) { OutputC ^= 1; break; }
                            CountC += PeriodC;
                        }
                    }

                    CountN -= nextevent;
                    if (CountN <= 0)
                    {
                        /* Is noise output going to change? */
                        if (((RNG + 1) & 2) != 0) /* (bit0^bit1)? */
                        {
                            OutputN = (byte)~OutputN;
                            outn = (OutputN | Regs[AY_ENABLE]);
                        }
                        if ((RNG & 1) != 0) RNG ^= 0x24000; /* Galois configuration */
                        RNG >>= 1;
                        CountN += PeriodN;
                    }

                    left -= nextevent;
                } while (left > 0);

                /* update envelope */
                if (Holding == 0)
                {
                    CountE -= STEP;
                    if (CountE <= 0)
                    {
                        do
                        {
                            CountEnv--;
                            CountE += PeriodE;
                        } while (CountE <= 0);

                        if (CountEnv < 0)
                        {
                            if (Hold != 0)
                            {
                                if (Alternate != 0) Attack ^= 0x1f;
                                Holding = 1;
                                CountEnv = 0;
                            }
                            else
                            {
                                if (Alternate != 0 && (CountEnv & 0x20) != 0) Attack ^= 0x1f;
                                CountEnv &= 0x1f;
                            }
                        }

                        VolE = VolTable[CountEnv ^ Attack];
                        if (EnvelopeA != 0) VolA = VolE;
                        if (EnvelopeB != 0) VolB = VolE;
                        if (EnvelopeC != 0) VolC = VolE;
                    }
                }

                buf1[o++] = (int)((vola * VolA + volb * VolB + volc * VolC) / STEP);
                length--;
            }
        }

        /// <summary>Advance by NES CPU cycles (= AY input clocks); renders the owed stream samples (a stream_update at the end of each call).</summary>
        public void Run(int cpuCycles)
        {
            cycleAcc += cpuCycles;
            pendingSamples += cycleAcc >> 3;
            cycleAcc &= 7;
            StreamUpdate();
        }

        private void StreamUpdate()
        {
            while (pendingSamples > 0)
            {
                int n = Math.Min(pendingSamples, tmp.Length);
                AY8910Update(tmp, n);
                pendingSamples -= n;
                for (int i = 0; i < n; i++) Resample(tmp[i]);
            }
        }

        private void Resample(int s)
        {
            // MAME mixer: 16-bit clip of the summed stream
            if (s > 32767) s = 32767; else if (s < -32768) s = -32768;
            double w = 1.0;
            while (w > 0)
            {
                double take = Math.Min(w, SrcPerOut - boxPos);
                boxSum += s * take; boxPos += take; w -= take;
                if (boxPos >= SrcPerOut - 1e-9)
                {
                    Push((float)(boxSum / SrcPerOut / 32768.0));
                    boxSum = 0; boxPos = 0;
                }
            }
        }

        private void Push(float v)
        {
            if (count == ring.Length) { head = (head + 1) % ring.Length; count--; }
            ring[(head + count) % ring.Length] = v; count++;
        }

        public int Queued => count;
        public int Read(Span<float> dest)
        {
            int n = Math.Min(dest.Length, count);
            for (int i = 0; i < n; i++) { dest[i] = ring[head]; head = (head + 1) % ring.Length; }
            count -= n;
            return n;
        }
        public void ClearOutput() { head = count = 0; }
    }
}
