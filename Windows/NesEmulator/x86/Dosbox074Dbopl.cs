// DOSBox 0.74 "fast" OPL emulator (DBOPL) as an AdLib engine for the x86 APU menu.
//
// Source:   DOSBox 0.74, src/hardware/dbopl.cpp + dbopl.h (driven the way src/hardware/adlib.cpp drives it)
// Version:  DOSBox SVN tags/RELEASE_0_74 (dbopl.cpp r3582; file header "$Id: dbopl.cpp,v 1.10 2009-06-10 19:54:51 harekiet $",
//           adlib.cpp "$Id: adlib.cpp,v 1.42 2009-11-03 20:17:42 qbix79 $")
// URL:      https://sourceforge.net/p/dosbox/code-0/HEAD/tree/tags/RELEASE_0_74/src/hardware/dbopl.cpp
// Authors:  Edward / Harekiet van der Spek (DBOPL), The DOSBox Team (adlib.cpp, Copyright (C) 2002-2010)
// Licence:  GNU GPL version 2 or (at your option) any later version.
//           Documented exception, see THIRD_PARTY_NOTICES.md section 2a.
//
// This is an "art instrument" port: faithful, flaws included. Nothing has been fixed. Build options as shipped:
// DBOPL_WAVE == WAVE_TABLEMUL, WAVE_PRECISION undefined (WAVE_BITS 10), ENV_BITS 9 (ENV_EXTRA 0).
// The OPL3 tables, the 18 channels and the 4-op/stereo synth modes are kept, but only OPL2 behaviour is reachable
// (adlib.cpp in MODE_OPL2 masks register numbers to 0-FFh, so 0x105 can never switch OPL3 on).
//
// Run like DOSBox 0.74 runs an AdLib (oplemu=fast, oplmode=opl2):
//  - oplrate default 44100 (dosbox.cpp "oplrate" default 44100; adlib.cpp clamps to >= 8000), Chip::Setup(44100);
//  - the mixer channel ("FM") is called once per 1 ms mixer tick for ~rate/1000 samples, so register writes take
//    effect only at 1 ms block boundaries: BlockSamples = 44 (44.1 samples per tick, rounded down);
//  - Handler::Generate renders at most 512 samples per call (the mixer loops until it has what it needs);
//  - the mixer channel has SetScale(2.0): each 32-bit sample is doubled, then clipped to 16 bits by the mixer;
//  - timer registers 02h/03h/04h are swallowed by adlib.cpp's Chip::Write and never reach DBOPL;
//  - after 30 s (PIC ticks) without a port write and with no key-on bit set in the B0-B8 register cache, the mixer
//    channel is disabled: the chip stops being clocked (state frozen) and outputs silence until the next write.
//
// Characteristic flaws / quirks kept:
//  - Envelopes are linear-ish approximations of the real logarithmic ones: decay/release use a single per-rate
//    adder (EnvelopeIncreaseTable/8 steps squashed into one), attack rates are "best guess" adders found by a
//    16-pass search against AttackSamplesTable, so attack times only approximate the chip and differ per output rate.
//  - Attack rates 15 (index >= 62) jump to full volume instantly (8 << RATE_SH).
//  - KSR is the reverse of MAME's (comment in source: "pdf manuals says otherwise?"): ksr off still uses keycode>>2.
//  - Volume is a linear sine table (amplitude 4084) times a 384-entry multiply table (WAVE_TABLEMUL), not the
//    real chip's log-sin/exp tables; the exponential wave uses 4085. Envelope resolution is 9 bits, not 9+3.
//  - LFO (tremolo/vibrato) values only change between sample blocks: ForwardLFO splits generation at LFO steps,
//    tremolo is a 52-step triangle and vibrato an 8-step table that runs 4x slower than tremolo.
//  - Operators whose envelope is silent and not changing are skipped for a whole block; skipped channels reset their
//    feedback history (old[0]=old[1]=0) and do not advance their phase, so a silent channel's phase freezes.
//  - Feedback uses an unsigned shift of (old0+old1) so negative sums become huge and are masked by the wave index.
//  - Waveform select bit (reg 01h bit 5) is only applied when E0h changes: toggling reg 01h does not update
//    operators that already have a waveform written, and rewriting E0h with the same value is ignored.
//  - Register writes with an unchanged value are ignored for 20h/40h/80h/C0h/E0h (change detection).
//  - Percussion: hi-hat/snare/cymbal use ForwardWave of ops 2 and 5 directly (MAME-derived phase bits), noise is the
//    MAME 23-bit LFSR at the same precision as the LFO; the rhythm sum is shifted left by 1 (doubled).
//  - Key-on restarts the phase at a per-waveform start offset (WaveStartTable), not at 0.
//  - Note-select (reg 08h bit 6) is only read when a channel's frequency changes, not when 08h is written.
//  - Channel 6-8 synth handler is only restored (ResetC0) when rhythm mode is switched off.
//  - Output is mono, 32-bit accumulated, doubled by the mixer scale and hard-clipped to 16 bits (no soft limiting).
//  - The 30 s idle cut-off freezes the chip mid-release (see above).

using System;

namespace NesEmulator.X86;

/// <summary>DOSBox 0.74's DBOPL ("oplemu=fast"), OPL2 mode, run the way adlib.cpp runs an AdLib.</summary>
public sealed class Dosbox074DboplEngine : IOplEngine
{
    public string Id => "D074F";
    public int Rate => 44100;
    /// <summary>DOSBox 0.74's mixer calls the FM channel once per 1 ms tick: 44.1 samples at 44100 Hz.</summary>
    public int BlockSamples => 44;

    private Dbopl.Chip _chip = null!;
    private readonly int[] _buffer = new int[512 * 2];
    private readonly byte[] _cache = new byte[512];
    private long _samplesSinceWrite;
    private bool _enabled;

    public Dosbox074DboplEngine() => Reset();

    public void Reset()
    {
        Dbopl.InitTables();
        _chip = new Dbopl.Chip();
        _chip.Setup((uint)Rate);
        Array.Clear(_cache);
        _samplesSinceWrite = 0;
        _enabled = false;
    }

    public void Write(int reg, byte value)
    {
        _samplesSinceWrite = 0;                 // lastUsed = PIC_Ticks
        _enabled = true;                        // mixerChan->Enable(true)
        reg &= 0xff;                            // MODE_OPL2: reg.normal = WriteAddr(port, val) & 0xff
        if (reg == 0x02 || reg == 0x03 || reg == 0x04) return;   // timers (adlib.cpp Chip::Write)
        _chip.WriteReg((uint)reg, value);
        _cache[reg] = value;
    }

    public void Generate(short[] dest, int count)
    {
        int pos = 0;
        while (pos < count)
        {
            if (!_enabled) { dest[pos++] = 0; continue; }
            int todo = Math.Min(count - pos, 512);   // Handler::Generate clips to 512, the mixer loops for the rest
            _chip.GenerateBlock2((uint)todo, _buffer);
            for (int i = 0; i < todo; i++)
            {
                long s = (long)_buffer[i] * 2;       // mixerChan->SetScale(2.0)
                dest[pos + i] = (short)(s > 32767 ? 32767 : s < -32768 ? -32768 : s);
            }
            pos += todo;
            _samplesSinceWrite += todo;
            // OPL_CallBack: disable after 30 s of silence (30000 PIC ticks of 1 ms)
            if (_samplesSinceWrite * 1000 / Rate > 30000)
            {
                int c;
                for (c = 0xb0; c < 0xb9; c++) if (((_cache[c] | _cache[c + 0x100]) & 0x20) != 0) break;
                if (c == 0xb9) _enabled = false;
                else _samplesSinceWrite = 0;
            }
        }
    }
}

/// <summary>DBOPL itself (namespace DBOPL in dbopl.cpp), WAVE_TABLEMUL build.</summary>
internal static class Dbopl
{
    private const double OPLRATE = 14318180.0 / 288.0;
    private const int TREMOLO_TABLE = 52;
    private const int WAVE_BITS = 10;
    private const int WAVE_SH = 32 - WAVE_BITS;
    private const uint WAVE_MASK = (1u << WAVE_SH) - 1;
    private const int LFO_SH = WAVE_SH - 10;
    private const uint LFO_MAX = 256u << LFO_SH;
    private const int ENV_BITS = 9;
    private const int ENV_MIN = 0;
    private const int ENV_EXTRA = ENV_BITS - 9;
    private const int ENV_MAX = 511 << ENV_EXTRA;
    private const int ENV_LIMIT = (12 * 256) >> (3 - ENV_EXTRA);
    private static bool ENV_SILENT(long x) => x >= ENV_LIMIT;
    private const int RATE_SH = 24;
    private const uint RATE_MASK = (1u << RATE_SH) - 1;
    private const int MUL_SH = 16;
    private const int SHIFT_KSLBASE = 16;
    private const int SHIFT_KEYCODE = 24;

    private static readonly byte[] KslCreateTable = { 64, 32, 24, 19, 16, 12, 11, 10, 8, 6, 5, 4, 3, 2, 1, 0 };
    private static readonly byte[] FreqCreateTable = { 1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30 };
    private static readonly byte[] AttackSamplesTable = { 69, 55, 46, 40, 35, 29, 23, 20, 19, 15, 11, 10, 9 };
    private static readonly byte[] EnvelopeIncreaseTable = { 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32 };

    private static readonly short[] WaveTable = new short[8 * 512];
    private static readonly ushort[] WaveBaseTable = { 0x000, 0x200, 0x200, 0x800, 0xa00, 0xc00, 0x100, 0x400 };
    private static readonly ushort[] WaveMaskTable = { 1023, 1023, 511, 511, 1023, 1023, 512, 1023 };
    private static readonly ushort[] WaveStartTable = { 512, 0, 0, 0, 0, 512, 512, 256 };
    private static readonly ushort[] MulTable = new ushort[384];
    private static readonly byte[] KslTable = new byte[8 * 16];
    private static readonly byte[] TremoloTable = new byte[TREMOLO_TABLE];
    // In C++ these hold byte offsets into the Chip struct (0 = none); here: channel index / operator (chan*2+op), -1 = none.
    private static readonly int[] ChanOffsetTable = new int[32];
    private static readonly int[] OpOffsetTable = new int[64];

    private static readonly sbyte[] VibratoTable = { 1, 0, 1, 30, 1 - 0x80, 0 - 0x80, 1 - 0x80, 30 - 0x80 };
    private static readonly byte[] KslShiftTable = { 31, 1, 2, 0 };

    private static void EnvelopeSelect(byte val, out byte index, out byte shift)
    {
        if (val < 13 * 4) { shift = (byte)(12 - (val >> 2)); index = (byte)(val & 3); }
        else if (val < 15 * 4) { shift = 0; index = (byte)(val - 12 * 4); }
        else { shift = 0; index = 12; }
    }

    internal enum SynthMode { sm2AM, sm2FM, sm3AM, sm3FM, sm4Start, sm3FMFM, sm3AMFM, sm3FMAM, sm3AMAM, sm6Start, sm2Percussion, sm3Percussion }

    // Operator::State
    private const byte OFF = 0, RELEASE = 1, SUSTAIN = 2, DECAY = 3, ATTACK = 4;
    private const byte MASK_KSR = 0x10, MASK_SUSTAIN = 0x20, MASK_VIBRATO = 0x40, MASK_TREMOLO = 0x80;

    internal sealed class Operator
    {
        public int waveBase;          // index into WaveTable (Bit16s* waveBase)
        public uint waveMask, waveStart;
        public uint waveIndex, waveAdd, waveCurrent;
        public uint chanData, freqMul, vibrato;
        public int sustainLevel, totalLevel;
        public uint currentLevel;
        public int volume;
        public uint attackAdd, decayAdd, releaseAdd, rateIndex;
        public byte rateZero, keyOn;
        public byte reg20, reg40, reg60, reg80, regE0;
        public byte state;            // also selects the volume handler (VolumeHandlerTable[state])
        public byte tremoloMask, vibStrength, ksr;

        public Operator()
        {
            chanData = 0; freqMul = 0; waveIndex = 0; waveAdd = 0; waveCurrent = 0;
            keyOn = 0; ksr = 0; reg20 = 0; reg40 = 0; reg60 = 0; reg80 = 0; regE0 = 0;
            SetState(OFF);
            rateZero = 1 << OFF;
            sustainLevel = ENV_MAX; currentLevel = ENV_MAX; totalLevel = ENV_MAX; volume = ENV_MAX;
            releaseAdd = 0;
        }

        private void UpdateAttack(Chip chip)
        {
            byte rate = (byte)(reg60 >> 4);
            if (rate != 0) { byte val = (byte)((rate << 2) + ksr); attackAdd = chip.attackRates[val]; rateZero &= unchecked((byte)~(1 << ATTACK)); }
            else { attackAdd = 0; rateZero |= 1 << ATTACK; }
        }
        private void UpdateDecay(Chip chip)
        {
            byte rate = (byte)(reg60 & 0xf);
            if (rate != 0) { byte val = (byte)((rate << 2) + ksr); decayAdd = chip.linearRates[val]; rateZero &= unchecked((byte)~(1 << DECAY)); }
            else { decayAdd = 0; rateZero |= 1 << DECAY; }
        }
        private void UpdateRelease(Chip chip)
        {
            byte rate = (byte)(reg80 & 0xf);
            if (rate != 0)
            {
                byte val = (byte)((rate << 2) + ksr);
                releaseAdd = chip.linearRates[val];
                rateZero &= unchecked((byte)~(1 << RELEASE));
                if ((reg20 & MASK_SUSTAIN) == 0) rateZero &= unchecked((byte)~(1 << SUSTAIN));
            }
            else
            {
                rateZero |= 1 << RELEASE;
                releaseAdd = 0;
                if ((reg20 & MASK_SUSTAIN) == 0) rateZero |= 1 << SUSTAIN;
            }
        }

        public void UpdateAttenuation()
        {
            byte kslBase = (byte)((chanData >> SHIFT_KSLBASE) & 0xff);
            uint tl = (uint)(reg40 & 0x3f);
            byte kslShift = KslShiftTable[reg40 >> 6];
            totalLevel = (int)(tl << (ENV_BITS - 7));
            // C++: (Bit8u << 0) promotes to int; >> 31 of a value < 256 is 0
            totalLevel += (kslBase << ENV_EXTRA) >> kslShift;
        }

        public void UpdateFrequency()
        {
            uint freq = chanData & ((1u << 10) - 1);
            int block = (int)((chanData >> 10) & 0xff);
            waveAdd = unchecked((freq << block) * freqMul);
            if ((reg20 & MASK_VIBRATO) != 0)
            {
                vibStrength = (byte)(freq >> 7);
                vibrato = unchecked(((uint)vibStrength << block) * freqMul);
            }
            else { vibStrength = 0; vibrato = 0; }
        }

        public void UpdateRates(Chip chip)
        {
            byte newKsr = (byte)((chanData >> SHIFT_KEYCODE) & 0xff);
            if ((reg20 & MASK_KSR) == 0) newKsr >>= 2;
            if (ksr == newKsr) return;
            ksr = newKsr;
            UpdateAttack(chip); UpdateDecay(chip); UpdateRelease(chip);
        }

        private int RateForward(uint add)
        {
            rateIndex = unchecked(rateIndex + add);
            int ret = (int)(rateIndex >> RATE_SH);
            rateIndex &= RATE_MASK;
            return ret;
        }

        // TemplateVolume<state>, picked through volHandler = VolumeHandlerTable[state]
        private int Volume()
        {
            int vol = volume;
            int change;
            switch (state)
            {
                case OFF:
                    return ENV_MAX;
                case ATTACK:
                    change = RateForward(attackAdd);
                    if (change == 0) return vol;
                    vol += ((~vol) * change) >> 3;
                    if (vol < ENV_MIN) { volume = ENV_MIN; rateIndex = 0; SetState(DECAY); return ENV_MIN; }
                    break;
                case DECAY:
                    vol += RateForward(decayAdd);
                    if (vol >= sustainLevel)
                    {
                        if (vol >= ENV_MAX) { volume = ENV_MAX; SetState(OFF); return ENV_MAX; }
                        rateIndex = 0;
                        SetState(SUSTAIN);
                    }
                    break;
                case SUSTAIN:
                    if ((reg20 & MASK_SUSTAIN) != 0) return vol;
                    goto case RELEASE;   // in sustain phase, but not sustaining, do regular release
                case RELEASE:
                    vol += RateForward(releaseAdd);
                    if (vol >= ENV_MAX) { volume = ENV_MAX; SetState(OFF); return ENV_MAX; }
                    break;
            }
            volume = vol;
            return vol;
        }

        public uint ForwardVolume() => unchecked(currentLevel + (uint)Volume());

        public uint ForwardWave()
        {
            waveIndex = unchecked(waveIndex + waveCurrent);
            return waveIndex >> WAVE_SH;
        }

        public void Write20(Chip chip, byte val)
        {
            byte change = (byte)(reg20 ^ val);
            if (change == 0) return;
            reg20 = val;
            tremoloMask = (byte)((sbyte)val >> 7);
            tremoloMask &= unchecked((byte)~((1 << ENV_EXTRA) - 1));
            if ((change & MASK_KSR) != 0) UpdateRates(chip);
            if ((reg20 & MASK_SUSTAIN) != 0 || releaseAdd == 0) rateZero |= 1 << SUSTAIN;
            else rateZero &= unchecked((byte)~(1 << SUSTAIN));
            if ((change & (0xf | MASK_VIBRATO)) != 0)
            {
                freqMul = chip.freqMul[val & 0xf];
                UpdateFrequency();
            }
        }

        public void Write40(Chip chip, byte val)
        {
            if ((reg40 ^ val) == 0) return;
            reg40 = val;
            UpdateAttenuation();
        }

        public void Write60(Chip chip, byte val)
        {
            byte change = (byte)(reg60 ^ val);
            reg60 = val;
            if ((change & 0x0f) != 0) UpdateDecay(chip);
            if ((change & 0xf0) != 0) UpdateAttack(chip);
        }

        public void Write80(Chip chip, byte val)
        {
            byte change = (byte)(reg80 ^ val);
            if (change == 0) return;
            reg80 = val;
            byte sustain = (byte)(val >> 4);
            sustain |= (byte)((sustain + 1) & 0x10);   // turn 0xf into 0x1f
            sustainLevel = sustain << (ENV_BITS - 5);
            if ((change & 0x0f) != 0) UpdateRelease(chip);
        }

        public void WriteE0(Chip chip, byte val)
        {
            if ((regE0 ^ val) == 0) return;
            byte waveForm = (byte)(val & ((0x3 & chip.waveFormMask) | (0x7 & (byte)chip.opl3Active)));
            regE0 = val;
            waveBase = WaveBaseTable[waveForm];
            waveStart = (uint)WaveStartTable[waveForm] << WAVE_SH;
            waveMask = WaveMaskTable[waveForm];
        }

        public void SetState(byte s) { state = s; }

        public bool Silent()
        {
            if (!ENV_SILENT(totalLevel + volume)) return false;
            if ((rateZero & (1 << state)) == 0) return false;
            return true;
        }

        public void Prepare(Chip chip)
        {
            currentLevel = unchecked((uint)totalLevel + (uint)(chip.tremoloValue & tremoloMask));
            waveCurrent = waveAdd;
            if ((vibStrength >> chip.vibratoShift) != 0)
            {
                int add = (int)(vibrato >> chip.vibratoShift);
                int neg = chip.vibratoSign;
                add = (add ^ neg) - neg;
                waveCurrent = unchecked(waveCurrent + (uint)add);
            }
        }

        public void KeyOn(byte mask)
        {
            if (keyOn == 0)
            {
                waveIndex = waveStart;
                rateIndex = 0;
                SetState(ATTACK);
            }
            keyOn |= mask;
        }

        public void KeyOff(byte mask)
        {
            keyOn &= (byte)~mask;
            if (keyOn == 0 && state != OFF) SetState(RELEASE);
        }

        public int GetWave(uint index, uint vol)
            => (WaveTable[waveBase + (int)(index & waveMask)] * MulTable[vol >> ENV_EXTRA]) >> MUL_SH;

        public int GetSample(int modulation)
        {
            uint vol = ForwardVolume();
            if (ENV_SILENT(vol))
            {
                waveIndex = unchecked(waveIndex + waveCurrent);
                return 0;
            }
            uint index = ForwardWave();
            index = unchecked(index + (uint)modulation);
            return GetWave(index, vol);
        }
    }

    internal sealed class Channel
    {
        public readonly Operator[] op = { new Operator(), new Operator() };
        public readonly Chip chip;
        public readonly int self;   // this channel's index in chip.chan (C++ pointer arithmetic: this + n)
        public SynthMode synthHandler;
        public uint chanData;
        public int old0, old1;
        public byte feedback, regB0, regC0, fourMask;
        public sbyte maskLeft, maskRight;

        public Channel(Chip chip, int self)
        {
            this.chip = chip; this.self = self;
            old0 = old1 = 0; chanData = 0; regB0 = 0; regC0 = 0;
            maskLeft = -1; maskRight = -1; feedback = 31; fourMask = 0;
            synthHandler = SynthMode.sm2FM;
        }

        private Channel Next(int n) => chip.chan[self + n];
        public Operator Op(int index) => chip.chan[self + (index >> 1)].op[index & 1];

        public void SetChanData(Chip chip, uint data)
        {
            uint change = chanData ^ data;
            chanData = data;
            Op(0).chanData = data;
            Op(1).chanData = data;
            Op(0).UpdateFrequency();
            Op(1).UpdateFrequency();
            if ((change & (0xffu << SHIFT_KSLBASE)) != 0) { Op(0).UpdateAttenuation(); Op(1).UpdateAttenuation(); }
            if ((change & (0xffu << SHIFT_KEYCODE)) != 0) { Op(0).UpdateRates(chip); Op(1).UpdateRates(chip); }
        }

        public void UpdateFrequency(Chip chip, byte fourOp)
        {
            uint data = chanData & 0xffff;
            uint kslBase = KslTable[data >> 6];
            uint keyCode = (data & 0x1c00) >> 9;
            if ((chip.reg08 & 0x40) != 0) keyCode |= (data & 0x100) >> 8;   // notesel == 1
            else keyCode |= (data & 0x200) >> 9;                            // notesel == 0
            data |= (keyCode << SHIFT_KEYCODE) | (kslBase << SHIFT_KSLBASE);
            SetChanData(chip, data);
            if ((fourOp & 0x3f) != 0) Next(1).SetChanData(chip, data);
        }

        public void WriteA0(Chip chip, byte val)
        {
            byte fourOp = (byte)(chip.reg104 & (byte)chip.opl3Active & fourMask);
            if (fourOp > 0x80) return;
            uint change = (chanData ^ val) & 0xff;
            if (change != 0) { chanData ^= change; UpdateFrequency(chip, fourOp); }
        }

        public void WriteB0(Chip chip, byte val)
        {
            byte fourOp = (byte)(chip.reg104 & (byte)chip.opl3Active & fourMask);
            if (fourOp > 0x80) return;
            uint change = (chanData ^ ((uint)val << 8)) & 0x1f00;
            if (change != 0) { chanData ^= change; UpdateFrequency(chip, fourOp); }
            if (((val ^ regB0) & 0x20) == 0) return;
            regB0 = val;
            if ((val & 0x20) != 0)
            {
                Op(0).KeyOn(0x1); Op(1).KeyOn(0x1);
                if ((fourOp & 0x3f) != 0) { Next(1).Op(0).KeyOn(1); Next(1).Op(1).KeyOn(1); }
            }
            else
            {
                Op(0).KeyOff(0x1); Op(1).KeyOff(0x1);
                if ((fourOp & 0x3f) != 0) { Next(1).Op(0).KeyOff(1); Next(1).Op(1).KeyOff(1); }
            }
        }

        public void WriteC0(Chip chip, byte val)
        {
            byte change = (byte)(val ^ regC0);
            if (change == 0) return;
            regC0 = val;
            feedback = (byte)((val >> 1) & 7);
            if (feedback != 0) feedback = (byte)(9 - feedback);
            else feedback = 31;
            if (chip.opl3Active != 0)
            {
                if (((chip.reg104 & fourMask) & 0x3f) != 0)
                {
                    Channel chan0, chan1;
                    if ((fourMask & 0x80) == 0) { chan0 = this; chan1 = Next(1); }
                    else { chan0 = Next(-1); chan1 = this; }
                    int synth = ((chan0.regC0 & 1) << 0) | ((chan1.regC0 & 1) << 1);
                    switch (synth)
                    {
                        case 0: chan0.synthHandler = SynthMode.sm3FMFM; break;
                        case 1: chan0.synthHandler = SynthMode.sm3AMFM; break;
                        case 2: chan0.synthHandler = SynthMode.sm3FMAM; break;
                        case 3: chan0.synthHandler = SynthMode.sm3AMAM; break;
                    }
                }
                else if ((fourMask & 0x40) != 0 && (chip.regBD & 0x20) != 0) { }
                else if ((val & 1) != 0) synthHandler = SynthMode.sm3AM;
                else synthHandler = SynthMode.sm3FM;
                maskLeft = (sbyte)((val & 0x10) != 0 ? -1 : 0);
                maskRight = (sbyte)((val & 0x20) != 0 ? -1 : 0);
            }
            else
            {
                if ((fourMask & 0x40) != 0 && (chip.regBD & 0x20) != 0) { }
                else if ((val & 1) != 0) synthHandler = SynthMode.sm2AM;
                else synthHandler = SynthMode.sm2FM;
            }
        }

        public void ResetC0(Chip chip)
        {
            byte val = regC0;
            regC0 ^= 0xff;
            WriteC0(chip, val);
        }

        private void GeneratePercussion(bool opl3Mode, Chip chip, int[] output, int o)
        {
            // BassDrum
            int mod = (int)((uint)(old0 + old1) >> feedback);
            old0 = old1;
            old1 = Op(0).GetSample(mod);
            if ((regC0 & 1) != 0) mod = 0;
            else mod = old0;
            int sample = Op(1).GetSample(mod);

            uint noiseBit = chip.ForwardNoise() & 0x1;
            uint c2 = Op(2).ForwardWave();
            uint c5 = Op(5).ForwardWave();
            uint phaseBit = (((c2 & 0x88) ^ ((c2 << 5) & 0x80)) | ((c5 ^ (c5 << 2)) & 0x20)) != 0 ? 0x02u : 0x00u;

            // Hi-Hat
            uint hhVol = Op(2).ForwardVolume();
            if (!ENV_SILENT(hhVol))
            {
                uint hhIndex = (phaseBit << 8) | (0x34u << (int)(phaseBit ^ (noiseBit << 1)));
                sample += Op(2).GetWave(hhIndex, hhVol);
            }
            // Snare Drum
            uint sdVol = Op(3).ForwardVolume();
            if (!ENV_SILENT(sdVol))
            {
                uint sdIndex = (0x100 + (c2 & 0x100)) ^ (noiseBit << 8);
                sample += Op(3).GetWave(sdIndex, sdVol);
            }
            // Tom-tom
            sample += Op(4).GetSample(0);
            // Top-Cymbal
            uint tcVol = Op(5).ForwardVolume();
            if (!ENV_SILENT(tcVol))
            {
                uint tcIndex = (1 + phaseBit) << 8;
                sample += Op(5).GetWave(tcIndex, tcVol);
            }
            sample <<= 1;
            if (opl3Mode) { output[o] += sample; output[o + 1] += sample; }
            else output[o] += sample;
        }

        /// <summary>BlockTemplate&lt;mode&gt;: returns the index of the next channel to run.</summary>
        public int Block(Chip chip, uint samples, int[] output, int outOff)
        {
            SynthMode mode = synthHandler;
            switch (mode)
            {
                case SynthMode.sm2AM:
                case SynthMode.sm3AM:
                    if (Op(0).Silent() && Op(1).Silent()) { old0 = old1 = 0; return self + 1; }
                    break;
                case SynthMode.sm2FM:
                case SynthMode.sm3FM:
                    if (Op(1).Silent()) { old0 = old1 = 0; return self + 1; }
                    break;
                case SynthMode.sm3FMFM:
                    if (Op(3).Silent()) { old0 = old1 = 0; return self + 2; }
                    break;
                case SynthMode.sm3AMFM:
                    if (Op(0).Silent() && Op(3).Silent()) { old0 = old1 = 0; return self + 2; }
                    break;
                case SynthMode.sm3FMAM:
                    if (Op(1).Silent() && Op(3).Silent()) { old0 = old1 = 0; return self + 2; }
                    break;
                case SynthMode.sm3AMAM:
                    if (Op(0).Silent() && Op(2).Silent() && Op(3).Silent()) { old0 = old1 = 0; return self + 2; }
                    break;
            }
            Op(0).Prepare(chip);
            Op(1).Prepare(chip);
            if (mode > SynthMode.sm4Start) { Op(2).Prepare(chip); Op(3).Prepare(chip); }
            if (mode > SynthMode.sm6Start) { Op(4).Prepare(chip); Op(5).Prepare(chip); }
            for (uint i = 0; i < samples; i++)
            {
                if (mode == SynthMode.sm2Percussion) { GeneratePercussion(false, chip, output, outOff + (int)i); continue; }
                else if (mode == SynthMode.sm3Percussion) { GeneratePercussion(true, chip, output, outOff + (int)i * 2); continue; }

                int mod = (int)((uint)(old0 + old1) >> feedback);
                old0 = old1;
                old1 = Op(0).GetSample(mod);
                int sample = 0;
                int out0 = old0;
                if (mode == SynthMode.sm2AM || mode == SynthMode.sm3AM) sample = out0 + Op(1).GetSample(0);
                else if (mode == SynthMode.sm2FM || mode == SynthMode.sm3FM) sample = Op(1).GetSample(out0);
                else if (mode == SynthMode.sm3FMFM)
                {
                    int next = Op(1).GetSample(out0);
                    next = Op(2).GetSample(next);
                    sample = Op(3).GetSample(next);
                }
                else if (mode == SynthMode.sm3AMFM)
                {
                    sample = out0;
                    int next = Op(1).GetSample(0);
                    next = Op(2).GetSample(next);
                    sample += Op(3).GetSample(next);
                }
                else if (mode == SynthMode.sm3FMAM)
                {
                    sample = Op(1).GetSample(out0);
                    int next = Op(2).GetSample(0);
                    sample += Op(3).GetSample(next);
                }
                else if (mode == SynthMode.sm3AMAM)
                {
                    sample = out0;
                    int next = Op(1).GetSample(0);
                    sample += Op(2).GetSample(next);
                    sample += Op(3).GetSample(0);
                }
                switch (mode)
                {
                    case SynthMode.sm2AM:
                    case SynthMode.sm2FM:
                        output[outOff + (int)i] += sample;
                        break;
                    case SynthMode.sm3AM:
                    case SynthMode.sm3FM:
                    case SynthMode.sm3FMFM:
                    case SynthMode.sm3AMFM:
                    case SynthMode.sm3FMAM:
                    case SynthMode.sm3AMAM:
                        output[outOff + (int)i * 2 + 0] += sample & maskLeft;
                        output[outOff + (int)i * 2 + 1] += sample & maskRight;
                        break;
                }
            }
            switch (mode)
            {
                case SynthMode.sm2AM: case SynthMode.sm2FM: case SynthMode.sm3AM: case SynthMode.sm3FM:
                    return self + 1;
                case SynthMode.sm3FMFM: case SynthMode.sm3AMFM: case SynthMode.sm3FMAM: case SynthMode.sm3AMAM:
                    return self + 2;
                case SynthMode.sm2Percussion: case SynthMode.sm3Percussion:
                    return self + 3;
            }
            return 18;   // C++ returns 0 (a null channel) here; unreachable for the modes above
        }
    }

    internal sealed class Chip
    {
        public uint lfoCounter, lfoAdd;
        public uint noiseCounter, noiseAdd, noiseValue;
        public readonly uint[] freqMul = new uint[16];
        public readonly uint[] linearRates = new uint[76];
        public readonly uint[] attackRates = new uint[76];
        public readonly Channel[] chan = new Channel[18];
        public byte reg104, reg08, reg04, regBD;
        public byte vibratoIndex, tremoloIndex;
        public sbyte vibratoSign;
        public byte vibratoShift, tremoloValue, vibratoStrength, tremoloStrength;
        public byte waveFormMask;
        public sbyte opl3Active;

        public Chip()
        {
            for (int i = 0; i < 18; i++) chan[i] = new Channel(this, i);
            reg08 = 0; reg04 = 0; regBD = 0; reg104 = 0; opl3Active = 0;
        }

        public uint ForwardNoise()
        {
            noiseCounter = unchecked(noiseCounter + noiseAdd);
            uint count = noiseCounter >> LFO_SH;
            noiseCounter &= WAVE_MASK;
            for (; count > 0; --count)
            {
                noiseValue ^= 0x800302u & (0u - (noiseValue & 1));
                noiseValue >>= 1;
            }
            return noiseValue;
        }

        public uint ForwardLFO(uint samples)
        {
            vibratoSign = (sbyte)(VibratoTable[vibratoIndex >> 2] >> 7);
            vibratoShift = (byte)((VibratoTable[vibratoIndex >> 2] & 7) + vibratoStrength);
            tremoloValue = (byte)(TremoloTable[tremoloIndex] >> tremoloStrength);
            uint todo = LFO_MAX - lfoCounter;
            uint count = unchecked((todo + lfoAdd - 1) / lfoAdd);
            if (count > samples)
            {
                count = samples;
                lfoCounter = unchecked(lfoCounter + count * lfoAdd);
            }
            else
            {
                lfoCounter = unchecked(lfoCounter + count * lfoAdd);
                lfoCounter &= LFO_MAX - 1;
                vibratoIndex = (byte)((vibratoIndex + 1) & 31);
                if (tremoloIndex + 1 < TREMOLO_TABLE) ++tremoloIndex;
                else tremoloIndex = 0;
            }
            return count;
        }

        public void WriteBD(byte val)
        {
            byte change = (byte)(regBD ^ val);
            if (change == 0) return;
            regBD = val;
            vibratoStrength = (byte)((val & 0x40) != 0 ? 0x00 : 0x01);
            tremoloStrength = (byte)((val & 0x80) != 0 ? 0x00 : 0x02);
            if ((val & 0x20) != 0)
            {
                if ((change & 0x20) != 0)
                    chan[6].synthHandler = opl3Active != 0 ? SynthMode.sm3Percussion : SynthMode.sm2Percussion;
                if ((val & 0x10) != 0) { chan[6].op[0].KeyOn(0x2); chan[6].op[1].KeyOn(0x2); }
                else { chan[6].op[0].KeyOff(0x2); chan[6].op[1].KeyOff(0x2); }
                if ((val & 0x1) != 0) chan[7].op[0].KeyOn(0x2); else chan[7].op[0].KeyOff(0x2);
                if ((val & 0x8) != 0) chan[7].op[1].KeyOn(0x2); else chan[7].op[1].KeyOff(0x2);
                if ((val & 0x4) != 0) chan[8].op[0].KeyOn(0x2); else chan[8].op[0].KeyOff(0x2);
                if ((val & 0x2) != 0) chan[8].op[1].KeyOn(0x2); else chan[8].op[1].KeyOff(0x2);
            }
            else if ((change & 0x20) != 0)
            {
                chan[6].ResetC0(this);
                chan[6].op[0].KeyOff(0x2); chan[6].op[1].KeyOff(0x2);
                chan[7].op[0].KeyOff(0x2); chan[7].op[1].KeyOff(0x2);
                chan[8].op[0].KeyOff(0x2); chan[8].op[1].KeyOff(0x2);
            }
        }

        private Operator? RegOp(uint reg)
        {
            int index = (int)(((reg >> 3) & 0x20) | (reg & 0x1f));
            int o = OpOffsetTable[index];
            return o < 0 ? null : chan[o >> 1].op[o & 1];
        }
        private Channel? RegChan(uint reg)
        {
            int index = (int)(((reg >> 4) & 0x10) | (reg & 0xf));
            int c = ChanOffsetTable[index];
            return c < 0 ? null : chan[c];
        }

        public void WriteReg(uint reg, byte val)
        {
            switch ((reg & 0xf0) >> 4)
            {
                case 0x00:
                    if (reg == 0x01) waveFormMask = (byte)((val & 0x20) != 0 ? 0x7 : 0x0);
                    else if (reg == 0x104)
                    {
                        if (((reg104 ^ val) & 0x3f) == 0) return;
                        reg104 = (byte)(0x80 | (val & 0x3f));
                    }
                    else if (reg == 0x105)
                    {
                        if (((opl3Active ^ val) & 1) == 0) return;
                        opl3Active = (sbyte)((val & 1) != 0 ? -1 : 0);
                        for (int i = 0; i < 18; i++) chan[i].ResetC0(this);
                    }
                    else if (reg == 0x08) reg08 = val;
                    break;   // falls through to case 0x10 in C++, which breaks
                case 0x01:
                    break;
                case 0x02: case 0x03: RegOp(reg)?.Write20(this, val); break;
                case 0x04: case 0x05: RegOp(reg)?.Write40(this, val); break;
                case 0x06: case 0x07: RegOp(reg)?.Write60(this, val); break;
                case 0x08: case 0x09: RegOp(reg)?.Write80(this, val); break;
                case 0x0a: RegChan(reg)?.WriteA0(this, val); break;
                case 0x0b:
                    if (reg == 0xbd) WriteBD(val);
                    else RegChan(reg)?.WriteB0(this, val);
                    break;
                case 0x0c: RegChan(reg)?.WriteC0(this, val); break;   // falls through to 0xd0 (break) in C++
                case 0x0d: break;
                case 0x0e: case 0x0f: RegOp(reg)?.WriteE0(this, val); break;
            }
        }

        public void GenerateBlock2(uint total, int[] output)
        {
            int off = 0;
            while (total > 0)
            {
                uint samples = ForwardLFO(total);
                Array.Clear(output, off, (int)samples);
                for (int ch = 0; ch < 9;) ch = chan[ch].Block(this, samples, output, off);
                total -= samples;
                off += (int)samples;
            }
        }

        public void GenerateBlock3(uint total, int[] output)
        {
            int off = 0;
            while (total > 0)
            {
                uint samples = ForwardLFO(total);
                Array.Clear(output, off, (int)samples * 2);
                for (int ch = 0; ch < 18;) ch = chan[ch].Block(this, samples, output, off);
                total -= samples;
                off += (int)samples * 2;
            }
        }

        public void Setup(uint rate)
        {
            double original = OPLRATE;
            double scale = original / rate;
            noiseAdd = (uint)(0.5 + scale * (1 << LFO_SH));
            noiseCounter = 0;
            noiseValue = 1;
            lfoAdd = (uint)(0.5 + scale * (1 << LFO_SH));
            lfoCounter = 0;
            vibratoIndex = 0;
            tremoloIndex = 0;

            uint freqScale = (uint)(0.5 + scale * (1 << (WAVE_SH - 1 - 10)));
            for (int i = 0; i < 16; i++) freqMul[i] = freqScale * FreqCreateTable[i];

            for (byte i = 0; i < 76; i++)
            {
                EnvelopeSelect(i, out byte index, out byte shift);
                linearRates[i] = (uint)(scale * (EnvelopeIncreaseTable[index] << (RATE_SH + ENV_EXTRA - shift - 3)));
            }
            for (byte i = 0; i < 62; i++)
            {
                EnvelopeSelect(i, out byte index, out byte shift);
                int orig = (int)(uint)((AttackSamplesTable[index] << shift) / scale);
                int guessAdd = (int)(uint)(scale * (EnvelopeIncreaseTable[index] << (RATE_SH - shift - 3)));
                int bestAdd = guessAdd;
                uint bestDiff = 1u << 30;
                for (uint passes = 0; passes < 16; passes++)
                {
                    int volume = ENV_MAX;
                    int samples = 0;
                    uint count = 0;
                    while (volume > 0 && samples < orig * 2)
                    {
                        count = unchecked(count + (uint)guessAdd);
                        int change = (int)(count >> RATE_SH);
                        count &= RATE_MASK;
                        if (change != 0) volume += (~volume * change) >> 3;
                        samples++;
                    }
                    int diff = orig - samples;
                    uint lDiff = (uint)Math.Abs(diff);
                    if (lDiff < bestDiff)
                    {
                        bestDiff = lDiff;
                        bestAdd = guessAdd;
                        if (bestDiff == 0) break;
                    }
                    if (diff < 0)
                    {
                        int mul = ((orig - diff) << 12) / orig;
                        guessAdd = unchecked((guessAdd * mul) >> 12);
                        guessAdd++;
                    }
                    else if (diff > 0)
                    {
                        int mul = ((orig - diff) << 12) / orig;
                        guessAdd = unchecked((guessAdd * mul) >> 12);
                        guessAdd--;
                    }
                }
                attackRates[i] = (uint)bestAdd;
            }
            for (byte i = 62; i < 76; i++) attackRates[i] = 8u << RATE_SH;

            chan[0].fourMask = 0x00 | (1 << 0);
            chan[1].fourMask = 0x80 | (1 << 0);
            chan[2].fourMask = 0x00 | (1 << 1);
            chan[3].fourMask = 0x80 | (1 << 1);
            chan[4].fourMask = 0x00 | (1 << 2);
            chan[5].fourMask = 0x80 | (1 << 2);
            chan[9].fourMask = 0x00 | (1 << 3);
            chan[10].fourMask = 0x80 | (1 << 3);
            chan[11].fourMask = 0x00 | (1 << 4);
            chan[12].fourMask = 0x80 | (1 << 4);
            chan[13].fourMask = 0x00 | (1 << 5);
            chan[14].fourMask = 0x80 | (1 << 5);
            chan[6].fourMask = 0x40;
            chan[7].fourMask = 0x40;
            chan[8].fourMask = 0x40;

            // Clear everything in opl3 mode
            WriteReg(0x105, 0x1);
            for (uint i = 0; i < 512; i++)
            {
                if (i == 0x105) continue;
                WriteReg(i, 0xff);
                WriteReg(i, 0x0);
            }
            WriteReg(0x105, 0x0);
            // Clear everything in opl2 mode
            for (uint i = 0; i < 255; i++)
            {
                WriteReg(i, 0xff);
                WriteReg(i, 0x0);
            }
        }
    }

    private static bool doneTables;
    private static readonly object tablesLock = new();

    public static void InitTables()
    {
        lock (tablesLock)
        {
            if (doneTables) return;
            for (int i = 0; i < 384; i++)
            {
                int s = i * 8;
                double val = 0.5 + Math.Pow(2.0, -1.0 + (255 - s) * (1.0 / 256)) * (1 << MUL_SH);
                MulTable[i] = (ushort)val;
            }
            for (int i = 0; i < 512; i++)
            {
                WaveTable[0x0200 + i] = (short)(Math.Sin((i + 0.5) * (Math.PI / 512.0)) * 4084);
                WaveTable[0x0000 + i] = (short)-WaveTable[0x200 + i];
            }
            for (int i = 0; i < 256; i++)
            {
                WaveTable[0x700 + i] = (short)(0.5 + Math.Pow(2.0, -1.0 + (255 - i * 8) * (1.0 / 256)) * 4085);
                WaveTable[0x6ff - i] = (short)-WaveTable[0x700 + i];
            }
            for (int i = 0; i < 256; i++)
            {
                WaveTable[0x400 + i] = WaveTable[0];
                WaveTable[0x500 + i] = WaveTable[0];
                WaveTable[0x900 + i] = WaveTable[0];
                WaveTable[0xc00 + i] = WaveTable[0];
                WaveTable[0xd00 + i] = WaveTable[0];
                WaveTable[0x800 + i] = WaveTable[0x200 + i];
                WaveTable[0xa00 + i] = WaveTable[0x200 + i * 2];
                WaveTable[0xb00 + i] = WaveTable[0x000 + i * 2];
                WaveTable[0xe00 + i] = WaveTable[0x200 + i * 2];
                WaveTable[0xf00 + i] = WaveTable[0x200 + i * 2];
            }
            for (int oct = 0; oct < 8; oct++)
            {
                int bas = oct * 8;
                for (int i = 0; i < 16; i++)
                {
                    int val = bas - KslCreateTable[i];
                    if (val < 0) val = 0;
                    KslTable[oct * 16 + i] = (byte)(val * 4);
                }
            }
            for (int i = 0; i < TREMOLO_TABLE / 2; i++)
            {
                byte val = (byte)(i << ENV_EXTRA);
                TremoloTable[i] = val;
                TremoloTable[TREMOLO_TABLE - 1 - i] = val;
            }
            for (int i = 0; i < 32; i++)
            {
                int index = i & 0xf;
                if (index >= 9) { ChanOffsetTable[i] = -1; continue; }
                if (index < 6) index = (index % 3) * 2 + (index / 3);
                if (i >= 16) index += 9;
                ChanOffsetTable[i] = index;
            }
            for (int i = 0; i < 64; i++)
            {
                if (i % 8 >= 6 || ((i / 8) % 4 == 3)) { OpOffsetTable[i] = -1; continue; }
                int chNum = (i / 8) * 3 + (i % 8) % 3;
                if (chNum >= 12) chNum += 16 - 12;
                int opNum = (i % 8) / 3;
                OpOffsetTable[i] = ChanOffsetTable[chNum] * 2 + opNum;
            }
            doneTables = true;
        }
    }
}
