/*
 * ViceFastSid - VICE's FastSID (MOS6581 SID emulation, fastsid.c) ported to C#.
 * Source project: VICE, the Versatile Commodore Emulator, SVN tags/v3.7, files vice/src/sid/fastsid.c + fastsid.h.
 * URL: https://sourceforge.net/p/vice-emu/code/HEAD/tree/tags/v3.7/vice/src/sid/fastsid.c
 * Authors: Teemu Rantanen, Michael Schwendt, Ettore Perazzoli.
 * Licence: GNU General Public License version 2 or (at your option) any later version (fastsid.c header).
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md
 *
 * Build options reproduced: WAVETABLES defined (as in fastsid.c), vreal_t = float (fixpoint.h without FIXPOINT_ARITHMETIC, the PC
 * build), model 6581 (newsid = 0), clock 1022727 Hz (NTSC C64), sample rate 44100 Hz, factor 1000 (no oversampling).
 * VICE internals stubbed minimally:
 *  - resources_get_int("SidFilters") -> 1 (VICE's default), resources_get_int("SidModel") -> 0 (6581).
 *  - lib_calloc/lib_free/getbuf -> managed arrays (getbuf is only reached when factor != 1000, never here).
 *  - maincpu_clk -> a clock value passed by the caller to Store/Read; sound_sample_position() -> 0.
 *  - sound_t glue / sid_engine_t hooks / snapshot state_read/state_write / dump_state / resid_state_* -> not ported (the NES
 *    translator re-keys a reset engine on state load instead).
 *  - wave6581.h / wave8580.h (the sampled combined-waveform tables waveform30_8580 and waveform50_6581) were NOT part of the harvested
 *    source: wavetable30 and wavetable50 are left zero-filled here. With the 6581 model wavetable60/70 are zero in VICE too.
 *    The NES translator only ever selects pulse ($40), triangle ($10) and noise ($80), so no combined waveform is reached.
 *
 * Characteristic flaws kept on purpose:
 *  - "XXX: check these": adrtable / exptable are guesses; the decay/release "exponential" is 6 linear segments (step >> i).
 *  - "XXX: fix this": DECAY drops straight into SUSTAIN when the counter is already at or below the sustain level, and SUSTAIN
 *    bounces back to DECAY when the level is above - so a sustain level can only be reached from above.
 *  - ADSR counter is a 31-bit value moved by a per-sample step and compared with the +0x80000000 wrap trick; no real SID
 *    rate counter, no ADSR delay bug, the envelope is updated once per OUTPUT sample, not per SID cycle.
 *  - Oscillators advance once per output sample (no band-limiting): high notes alias heavily.
 *  - Noise: the 23-bit shift register is shifted 16 times per accumulator overflow plus (f >> 28) lazily on read (NSHIFT), so the
 *    noise "clock" is 16 shifts per oscillator period instead of one per bit-19 rise; the register is not masked to 23 bits.
 *    Noise combined with any other waveform zeroes the register ("XXX: noise locking correct?") and outputs wavetable00.
 *  - "kludge for ring modulation": wtr[1] = 0x7fff XORs the waveform when the previous voice's accumulator MSB is set; for
 *    triangle+pulse combinations too (cases 3, 5; case 7 only on the 8580).
 *  - Pulse is a pointer into wavetable40 offset by the pulse width (high when the 12-bit phase >= pw), sampled at 12-bit phase
 *    resolution; the test bit points it at the all-high half and zeroes f and fs (oscillator frozen, output constant high).
 *  - Filter (SidFilters on): every voice, filtered or not, goes through ampMod1x8 (8-bit quantisation, x0.7) - unrouted voices
 *    are 8-bit too. Filter types are ad-hoc state-variable approximations with fixed tables tuned at 44100 Hz.
 *  - Output = ((o0 + o1 + o2) >> 20) - 0x600) * volume: a DC offset scaled by the master volume nibble, which is what makes the
 *    $D418 "volume sample" trick audible.
 *  - Gate flips between two samples are only seen as "gateflip": a gate off+on before the next sample restarts ATTACK.
 */
using System;

namespace NesEmulator
{
    public sealed class ViceFastSid
    {
        public const int ClockNtsc = 1022727;
        public const int SampleRate = 44100;

        private const int ATTACK = 0, DECAY = 1, SUSTAIN = 2, RELEASE = 3, IDLE = 4;
        private const uint NSEED = 0x7ffff8;
        private const int NOISETABLESIZE = 256;

        private static uint NSHIFT(uint v, int n) => unchecked((v << n) | (((v >> (23 - n)) ^ (v >> (18 - n))) & (uint)((1 << n) - 1)));
        private static uint NVALUE(uint v) => (uint)(noiseLSB[v & 0xff] | noiseMID[(v >> 8) & 0xff] | noiseMSB[(v >> 16) & 0xff]);

        // static tables, shared like in fastsid.c
        private static readonly ushort[] wavetable00 = new ushort[2];
        private static readonly ushort[] wavetable10 = new ushort[4096];
        private static readonly ushort[] wavetable20 = new ushort[4096];
        private static readonly ushort[] wavetable30 = new ushort[4096];
        private static readonly ushort[] wavetable40 = new ushort[8192];
        private static readonly ushort[] wavetable50 = new ushort[8192];
        private static readonly ushort[] wavetable60 = new ushort[8192];
        private static readonly ushort[] wavetable70 = new ushort[8192];
        private static readonly byte[] noiseMSB = new byte[NOISETABLESIZE];
        private static readonly byte[] noiseMID = new byte[NOISETABLESIZE];
        private static readonly byte[] noiseLSB = new byte[NOISETABLESIZE];

        /* XXX: check these */
        private static readonly ushort[] adrtable = { 1, 4, 8, 12, 19, 28, 34, 40, 50, 125, 250, 400, 500, 1500, 2500, 4000 };
        /* XXX: check these */
        private static readonly uint[] exptable = { 0x30000000, 0x1c000000, 0x0e000000, 0x08000000, 0x04000000, 0x00000000 };

        private static readonly uint[] sidreadclocks = new uint[9];
        private static readonly float[] lowPassParam = new float[0x800];
        private static readonly float[] bandPassParam = new float[0x800];
        private static readonly float[] filterResTable = new float[16];
        private const float filterRefFreq = 44100.0f;
        private static readonly sbyte[] ampMod1x8 = new sbyte[256];

        private sealed class Voice
        {
            public ViceFastSid s = null!;
            public Voice vprev = null!, vnext = null!;
            public int nr;
            public uint f, fs;
            public byte noise;
            public uint adsr; public int adsrs; public uint adsrz;
            public byte sync, filter, update, gateflip;
            public byte adsrm, attack, decay, sustain, release;
            public int dOff;                       // pointer to registers of this voice (psid->d + i*7)
            public uint rv;
            public ushort[]? wt; public int wtOff; // wavetable pointer = array + offset
            public uint wtpf, wtl;
            public readonly ushort[] wtr = new ushort[2];
            public sbyte filtIO;
            public float filtLow, filtRef;
        }

        private int factor;
        private readonly Voice[] v = { new Voice(), new Voice(), new Voice() };
        private readonly byte[] d = new byte[32];
        private byte has3, vol;
        private readonly int[] adrs = new int[16];
        private readonly uint[] sz = new uint[16];
        private uint speed1;
        private byte update;
        private byte newsid;
        private byte laststore, laststorebit;
        private long laststoreclk;
        private int emulatefilter;
        private float filterDy, filterResDy;
        private byte filterType, filterCurType;
        private ushort filterValue;

        /// <summary>fastsid_open + fastsid_init + fastsid_reset (sidstate all zero).</summary>
        public ViceFastSid(int speed = SampleRate, int cyclesPerSec = ClockNtsc)
        {
            Init(speed, cyclesPerSec, 1000);
            Reset(0);
        }

        private byte D(Voice pv, int i) => d[pv.dOff + i];

        private void dofilter(Voice pVoice)
        {
            if (pVoice.filter == 0) return;
            var s = pVoice.s;
            unchecked
            {
                if (s.filterType != 0)
                {
                    if (s.filterType == 0x20)
                    {
                        pVoice.filtLow += pVoice.filtRef * s.filterDy;
                        pVoice.filtRef += (pVoice.filtIO - pVoice.filtLow - pVoice.filtRef * s.filterResDy) * s.filterDy;
                        pVoice.filtIO = (sbyte)(int)(pVoice.filtRef - pVoice.filtLow / 4);
                    }
                    else if (s.filterType == 0x40)
                    {
                        float sample;
                        pVoice.filtLow += pVoice.filtRef * s.filterDy * 0.1f;
                        pVoice.filtRef += (pVoice.filtIO - pVoice.filtLow - pVoice.filtRef * s.filterResDy) * s.filterDy;
                        sample = pVoice.filtRef - (float)(pVoice.filtIO / 8);
                        if (sample < -128f) sample = -128f;
                        if (sample > 127f) sample = 127f;
                        pVoice.filtIO = (sbyte)(int)sample;
                    }
                    else
                    {
                        int tmp;
                        float sample, sample2;
                        pVoice.filtLow += pVoice.filtRef * s.filterDy;
                        sample = pVoice.filtIO;
                        sample2 = sample - pVoice.filtLow;
                        tmp = (int)sample2;
                        sample2 -= pVoice.filtRef * s.filterResDy;
                        pVoice.filtRef += sample2 * s.filterDy;
                        pVoice.filtIO = s.filterType == 0x10 ? (sbyte)(int)pVoice.filtLow
                            : s.filterType == 0x30 ? (sbyte)(int)pVoice.filtLow
                            : s.filterType == 0x50 ? (sbyte)((int)sample - (tmp >> 1))
                            : s.filterType == 0x60 ? (sbyte)tmp
                            : s.filterType == 0x70 ? (sbyte)((int)sample - (tmp >> 1)) : (sbyte)0;
                    }
                }
                else pVoice.filtIO = 0;
            }
        }

        /* 15-bit oscillator value */
        private static uint doosc(Voice pv)
        {
            unchecked
            {
                if (pv.noise != 0) return NVALUE(NSHIFT(pv.rv, (int)(pv.f >> 28))) << 7;
                return (uint)(pv.wt![pv.wtOff + (int)((pv.f + pv.wtpf) >> (int)pv.wtl)] ^ pv.wtr[pv.vprev.f >> 31]);
            }
        }

        private void set_adsr(Voice pv, int fm)
        {
            int i;
            switch (fm)
            {
                case ATTACK:
                    pv.adsrs = adrs[pv.attack];
                    pv.adsrz = 0;
                    break;
                case DECAY:
                    /* XXX: fix this */
                    if (pv.adsr <= sz[pv.sustain]) { set_adsr(pv, SUSTAIN); return; }
                    for (i = 0; pv.adsr < exptable[i]; i++) { }
                    pv.adsrs = -adrs[pv.decay] >> i;
                    pv.adsrz = sz[pv.sustain];
                    if (exptable[i] > pv.adsrz) pv.adsrz = exptable[i];
                    break;
                case SUSTAIN:
                    if (pv.adsr > sz[pv.sustain]) { set_adsr(pv, DECAY); return; }
                    pv.adsrs = 0;
                    pv.adsrz = 0;
                    break;
                case RELEASE:
                    if (pv.adsr == 0) { set_adsr(pv, IDLE); return; }
                    for (i = 0; pv.adsr < exptable[i]; i++) { }
                    pv.adsrs = -adrs[pv.release] >> i;
                    pv.adsrz = exptable[i];
                    break;
                case IDLE:
                    pv.adsrs = 0;
                    pv.adsrz = 0;
                    break;
            }
            pv.adsrm = (byte)fm;
        }

        private void trigger_adsr(Voice pv)
        {
            switch (pv.adsrm)
            {
                case ATTACK:
                    pv.adsr = 0x7fffffff;
                    set_adsr(pv, DECAY);
                    break;
                case DECAY:
                case RELEASE:
                    if (pv.adsr >= 0x80000000) pv.adsr = 0;
                    set_adsr(pv, pv.adsrm);
                    break;
            }
        }

        private void setup_sid()
        {
            if (update == 0) return;
            vol = (byte)(d[0x18] & 0x0f);
            has3 = (byte)(((d[0x18] & 0x80) != 0 && (d[0x17] & 0x04) == 0) ? 0 : 1);
            if (emulatefilter != 0)
            {
                v[0].filter = (byte)((d[0x17] & 0x01) != 0 ? 1 : 0);
                v[1].filter = (byte)((d[0x17] & 0x02) != 0 ? 1 : 0);
                v[2].filter = (byte)((d[0x17] & 0x04) != 0 ? 1 : 0);
                filterType = (byte)(d[0x18] & 0x70);
                if (filterType != filterCurType)
                {
                    filterCurType = filterType;
                    for (int i = 0; i < 3; i++) { v[i].filtLow = 0; v[i].filtRef = 0; }
                }
                filterValue = (ushort)(0x7ff & ((d[0x15] & 7) | (d[0x16] << 3)));
                filterDy = filterType == 0x20 ? bandPassParam[filterValue] : lowPassParam[filterValue];
                filterResDy = filterResTable[d[0x17] >> 4] - filterDy;
                if (filterResDy < 1.0f) filterResDy = 1.0f;
            }
            else { v[0].filter = 0; v[1].filter = 0; v[2].filter = 0; }
            update = 0;
        }

        private void setup_voice(Voice pv)
        {
            if (pv.update == 0) return;
            pv.attack = (byte)(D(pv, 5) / 0x10);
            pv.decay = (byte)(D(pv, 5) & 0x0f);
            pv.sustain = (byte)(D(pv, 6) / 0x10);
            pv.release = (byte)(D(pv, 6) & 0x0f);
            pv.sync = (byte)((D(pv, 4) & 0x02) != 0 ? 1 : 0);
            pv.fs = unchecked(speed1 * (uint)(D(pv, 0) + D(pv, 1) * 0x100));
            if ((D(pv, 4) & 0x08) != 0) { pv.f = pv.fs = 0; pv.rv = NSEED; }
            pv.noise = 0;
            pv.wtl = 20;
            pv.wtpf = 0;
            pv.wtr[1] = 0;
            int pw = D(pv, 2) + (D(pv, 3) & 0x0f) * 0x100;
            switch ((D(pv, 4) & 0xf0) >> 4)
            {
                case 0: pv.wt = wavetable00; pv.wtOff = 0; pv.wtl = 31; break;
                case 1: pv.wt = wavetable10; pv.wtOff = 0; if ((D(pv, 4) & 0x04) != 0) pv.wtr[1] = 0x7fff; break;
                case 2: pv.wt = wavetable20; pv.wtOff = 0; break;
                case 3: pv.wt = wavetable30; pv.wtOff = 0; if ((D(pv, 4) & 0x04) != 0) pv.wtr[1] = 0x7fff; break;
                case 4:
                    pv.wt = wavetable40;
                    pv.wtOff = (D(pv, 4) & 0x08) != 0 ? 4096 : 4096 - pw;
                    break;
                case 5:
                    pv.wt = wavetable50; pv.wtOff = (int)(pv.wtpf = (uint)(4096 - pw));
                    pv.wtpf <<= 20;
                    if ((D(pv, 4) & 0x04) != 0) pv.wtr[1] = 0x7fff;
                    break;
                case 6:
                    pv.wt = wavetable60; pv.wtOff = (int)(pv.wtpf = (uint)(4096 - pw));
                    pv.wtpf <<= 20;
                    break;
                case 7:
                    pv.wt = wavetable70; pv.wtOff = (int)(pv.wtpf = (uint)(4096 - pw));
                    pv.wtpf <<= 20;
                    if ((D(pv, 4) & 0x04) != 0 && newsid != 0) pv.wtr[1] = 0x7fff;
                    break;
                case 8: pv.noise = 1; pv.wt = null; pv.wtOff = 0; pv.wtl = 0; break;
                default:
                    /* XXX: noise locking correct? */
                    pv.rv = 0; pv.wt = wavetable00; pv.wtOff = 0; pv.wtl = 31;
                    break;
            }
            switch (pv.adsrm)
            {
                case ATTACK:
                case DECAY:
                case SUSTAIN:
                    if ((D(pv, 4) & 0x01) != 0) set_adsr(pv, pv.gateflip != 0 ? ATTACK : pv.adsrm);
                    else set_adsr(pv, RELEASE);
                    break;
                case RELEASE:
                case IDLE:
                    if ((D(pv, 4) & 0x01) != 0) set_adsr(pv, ATTACK);
                    else set_adsr(pv, pv.adsrm);
                    break;
            }
            pv.update = 0;
            pv.gateflip = 0;
        }

        /// <summary>fastsid_calculate_single_sample.</summary>
        public short CalculateSingleSample()
        {
            unchecked
            {
                uint o0, o1, o2;
                bool dosync1, dosync2;
                setup_sid();
                Voice v0 = v[0]; setup_voice(v0);
                Voice v1 = v[1]; setup_voice(v1);
                Voice v2 = v[2]; setup_voice(v2);

                dosync1 = false;
                if ((v0.f += v0.fs) < v0.fs) { v0.rv = NSHIFT(v0.rv, 16); if (v1.sync != 0) dosync1 = true; }
                dosync2 = false;
                if ((v1.f += v1.fs) < v1.fs) { v1.rv = NSHIFT(v1.rv, 16); if (v2.sync != 0) dosync2 = true; }
                if ((v2.f += v2.fs) < v2.fs)
                {
                    v2.rv = NSHIFT(v2.rv, 16);
                    if (v0.sync != 0) { v0.rv = NSHIFT(v0.rv, (int)(v0.f >> 28)); v0.f = 0; }
                }
                if (dosync2) { v2.rv = NSHIFT(v2.rv, (int)(v2.f >> 28)); v2.f = 0; }
                if (dosync1) { v1.rv = NSHIFT(v1.rv, (int)(v1.f >> 28)); v1.f = 0; }

                if ((v0.adsr += (uint)v0.adsrs) + 0x80000000 < v0.adsrz + 0x80000000) trigger_adsr(v0);
                if ((v1.adsr += (uint)v1.adsrs) + 0x80000000 < v1.adsrz + 0x80000000) trigger_adsr(v1);
                if ((v2.adsr += (uint)v2.adsrs) + 0x80000000 < v2.adsrz + 0x80000000) trigger_adsr(v2);

                o0 = v0.adsr >> 16;
                o1 = v1.adsr >> 16;
                o2 = v2.adsr >> 16;
                if (o0 != 0) o0 *= doosc(v0);
                if (o1 != 0) o1 *= doosc(v1);
                if (has3 != 0 && o2 != 0) o2 *= doosc(v2); else o2 = 0;
                if (emulatefilter != 0)
                {
                    v0.filtIO = ampMod1x8[o0 >> 22]; dofilter(v0); o0 = (uint)(v0.filtIO + 0x80) << (7 + 15);
                    v1.filtIO = ampMod1x8[o1 >> 22]; dofilter(v1); o1 = (uint)(v1.filtIO + 0x80) << (7 + 15);
                    v2.filtIO = ampMod1x8[o2 >> 22]; dofilter(v2); o2 = (uint)(v2.filtIO + 0x80) << (7 + 15);
                }
                return (short)(((int)((o0 + o1 + o2) >> 20) - 0x600) * vol);
            }
        }

        /// <summary>fastsid_calculate_samples with interleave 1 (factor 1000 path).</summary>
        public int CalculateSamples(Span<short> pbuf)
        {
            if (factor == 1000)
            {
                for (int i = 0; i < pbuf.Length; i++) pbuf[i] = CalculateSingleSample();
                return pbuf.Length;
            }
            int n = pbuf.Length * factor / 1000;
            var tmp = new short[Math.Max(n, pbuf.Length)];  // getbuf
            for (int i = 0; i < n; i++) tmp[i] = CalculateSingleSample();
            tmp.AsSpan(0, pbuf.Length).CopyTo(pbuf);
            return pbuf.Length;
        }

        private void init_filter(int freq)
        {
            float yMax = 1.0f, yMin = 0.01f, resDyMax = 1.0f, resDyMin = 2.0f, resDy = resDyMin;
            float yAdd, yTmp;
            float filterFs = 400.0f, filterFm = 60.0f, filterFt = 0.05f;
            float filterAmpl;
            filterValue = 0; filterType = 0; filterCurType = 0; filterDy = 0; filterResDy = 0;

            float rk; int uk;
            for (uk = 0, rk = 0; rk < 0x800; rk++, uk++)
            {
                float h = (float)((((Math.Exp(rk / 2048 * Math.Log(filterFs)) / filterFm) + filterFt) * filterRefFreq) / freq);
                if (h < yMin) h = yMin;
                if (h > yMax) h = yMax;
                lowPassParam[uk] = h;
            }
            yMax = 0.22f; yMin = 0.002f;
            yAdd = (float)((yMax - yMin) / 2048.0);
            yTmp = yMin;
            for (uk = 0, rk = 0; rk < 0x800; rk++, uk++)
            {
                bandPassParam[uk] = (yTmp * filterRefFreq) / freq;
                yTmp += yAdd;
            }
            for (uk = 0; uk < 16; uk++)
            {
                filterResTable[uk] = resDy;
                resDy -= ((resDyMin - resDyMax) / 15);
            }
            filterResTable[0] = resDyMin;
            filterResTable[15] = resDyMax;
            /* XXX: if psid->emulatefilter = 0, ampMod1x8 is never referenced */
            filterAmpl = emulatefilter != 0 ? 0.7f : 1.0f;
            for (int si = 0; si < 256; si++) ampMod1x8[si] = unchecked((sbyte)(int)((si - 0x80) * filterAmpl));
        }

        private void Init(int speed, int cyclesPerSec, int factorIn)
        {
            factor = factorIn;
            speed1 = (uint)((cyclesPerSec << 8) / speed);
            for (int i = 0; i < 16; i++)
            {
                adrs[i] = (int)(500 * 8 * speed1 / adrtable[i]);
                sz[i] = unchecked(0x8888888u * (uint)i);
            }
            update = 1;
            emulatefilter = 1;   // stub: resources_get_int("SidFilters") -> VICE default 1
            init_filter(speed);
            setup_sid();
            for (int i = 0; i < 3; i++)
            {
                v[i].vprev = v[(i + 2) % 3];
                v[i].vnext = v[(i + 1) % 3];
                v[i].nr = i;
                v[i].dOff = i * 7;
                v[i].s = this;
                v[i].rv = NSEED;
                v[i].filtLow = 0; v[i].filtRef = 0; v[i].filtIO = 0;
                v[i].update = 1;
                setup_voice(v[i]);
            }
            int sid_model = 0;   // stub: resources_get_int("SidModel") -> 0 (6581)
            newsid = (byte)(sid_model == 1 || sid_model == 2 ? 1 : 0);
            for (int i = 0; i < 4096; i++)
            {
                wavetable10[i] = (ushort)(i < 2048 ? i << 4 : 0xffff - (i << 4));
                wavetable20[i] = (ushort)(i << 3);
                wavetable30[i] = 0;          // waveform30_8580[i] << 7: table not in the harvested source (see header)
                wavetable40[i + 4096] = 0x7fff;
                wavetable50[i + 4096] = 0;   // waveform50_6581[i >> 3] << 7: table not in the harvested source
                wavetable60[i + 4096] = 0;
                wavetable70[i + 4096] = 0;
            }
            for (int i = 0; i < NOISETABLESIZE; i++)
            {
                noiseLSB[i] = (byte)(((i >> (7 - 2)) & 0x04) | ((i >> (4 - 1)) & 0x02) | ((i >> (2 - 0)) & 0x01));
                noiseMID[i] = (byte)(((i >> (13 - 8 - 4)) & 0x10) | ((i << (3 - (11 - 8))) & 0x08));
                noiseMSB[i] = (byte)(((i << (7 - (22 - 16))) & 0x80) | ((i << (6 - (20 - 16))) & 0x40) | ((i << (5 - (16 - 16))) & 0x20));
            }
            for (int i = 0; i < 9; i++) sidreadclocks[i] = 13;
        }

        /// <summary>fastsid_read; maincpu_clk is passed in, sound_sample_position() stubbed to 0.</summary>
        public byte Read(int addr, long maincpuClk)
        {
            byte ret;
            switch (addr)
            {
                case 0x19: ret = 0xff; break;
                case 0x1a: ret = 0xff; break;
                case 0x1b:
                    {
                        ushort ffix = 0;   // (uint16_t)(sound_sample_position() * fs), stubbed
                        uint rvstore = v[2].rv;
                        unchecked
                        {
                            if (v[2].noise != 0 && v[2].f + ffix < v[2].f) v[2].rv = NSHIFT(v[2].rv, 16);
                            v[2].f += ffix;
                            ret = (byte)(doosc(v[2]) >> 7);
                            v[2].f -= ffix;
                        }
                        v[2].rv = rvstore;
                        break;
                    }
                case 0x1c: ret = (byte)(v[2].adsr >> 23); break;
                default:
                    long tmp;
                    while ((tmp = laststorebit) != 0 && (tmp = laststoreclk + sidreadclocks[tmp]) < maincpuClk)
                    {
                        laststoreclk = tmp;
                        laststore &= unchecked((byte)(0xfeff >> laststorebit--));
                    }
                    ret = laststore;
                    break;
            }
            return ret;
        }

        /// <summary>fastsid_store.</summary>
        public void Store(int addr, byte b, long maincpuClk = 0)
        {
            switch (addr)
            {
                case 4: if (((d[addr] ^ b) & 1) != 0) v[0].gateflip = 1; v[0].update = 1; break;
                case 0: case 1: case 2: case 3: case 5: case 6: v[0].update = 1; break;
                case 11: if (((d[addr] ^ b) & 1) != 0) v[1].gateflip = 1; v[1].update = 1; break;
                case 7: case 8: case 9: case 10: case 12: case 13: v[1].update = 1; break;
                case 18: if (((d[addr] ^ b) & 1) != 0) v[2].gateflip = 1; v[2].update = 1; break;
                case 14: case 15: case 16: case 17: case 19: case 20: v[2].update = 1; break;
                default: update = 1; break;
            }
            d[addr] = b;
            laststore = b;
            laststorebit = 8;
            laststoreclk = maincpuClk;
        }

        /// <summary>fastsid_reset.</summary>
        public void Reset(long cpuClk)
        {
            for (int addr = 0; addr < 32; addr++) Store(addr, 0);
            laststoreclk = cpuClk;
        }
    }
}
