// The AdLib's Yamaha YM3812 (OPL2), ported from ymfm (BSD-3-Clause, Copyright (c) 2021 Aaron Giles; commit 81aec25): ymfm_opl.cpp (opl_registers_base<2>, ym3812) and the shared FM engine
// (ymfm_fm.ipp / ymfm_fm.h: fm_engine_base, fm_channel, fm_operator) and ymfm.h (roundtrip_fp). Provenance: x86/THIRD_PARTY_NOTICES.md section 1.

using System;
using System.Collections.Generic;
using System.IO;

namespace NesEmulator.X86;

/// <summary>
/// The YM3812 at 3.579545 MHz (here exactly three times the PIT clock, the way the machine clock gives it): 9 two-operator channels, rhythm mode, 4 waveforms, AM/PM LFO, timers 1 and 2 with the status
/// register. One chip sample every 72 clocks (24 PIT ticks, ~49716 Hz), resampled to 44.1 kHz with linear interpolation, mono duplicated to stereo. Time is the machine's deterministic clock:
/// <see cref="CatchUp"/> runs the chip up to the present before every port access and at each frame end. The timers and the status always run (AdLib detection works headless); the sound itself
/// is only computed while <see cref="Enabled"/>.
/// </summary>
public sealed partial class Opl2
{
    public const int Rate = 44100;
    public const int PitPerSample = 24;            // 72 chip clocks, the chip at 3 x 1193182 Hz
    private const int ClocksPerSample = 72;
    private const byte StatusTimerA = 0x40, StatusTimerB = 0x20, StatusIrq = 0x80;
    private const int EgAttack = 1, EgDecay = 2, EgSustain = 3, EgRelease = 4, EgDepress = 0;
    private const uint EgQuiet = 0x380;
    private const uint PhaseStepDynamic = 1;
    private const int KeyonNormal = 0, KeyonRhythm = 1, KeyonCsm = 2;

    private sealed class Op
    {
        public int ChOffs, OpOffs;
        public uint Phase;
        public ushort EnvAtt = 0x3ff;
        public int EnvState = EgRelease;
        public byte KeyState, KeyonLive;
        // the cache (ymfm opdata_cache)
        public int Waveform;
        public uint PhaseStep, TotalLevel, BlockFreq, Multiple, EgSustain;
        public readonly byte[] EgRate = new byte[6];
    }

    private sealed class Channel
    {
        public int ChOffs;
        public short Fb0, Fb1, FbIn;
        public Op Op0 = null!, Op1 = null!;
    }

    private readonly Func<long> _pitNow;
    private readonly byte[] _regs = new byte[256];
    private readonly Op[] _ops = new Op[18];
    private readonly Channel[] _ch = new Channel[9];
    private byte _address;
    private uint _envCounter;
    private byte _status, _irqMask = StatusTimerA | StatusTimerB, _irqState;
    private readonly byte[] _timerRunning = new byte[2];
    private readonly long[] _timerEnd = { long.MaxValue, long.MaxValue };   // the chip clock at which each timer expires
    private uint _activeChannels = 0x1ff, _modifiedChannels = 0x1ff, _prepareCount;
    private ushort _lfoAmCounter, _lfoPmCounter;
    private uint _noiseLfsr = 1;
    private byte _lfoAm;
    private long _samples;   // chip samples produced; sample k happens at chip clock 72 k (its low 8 bits are ymfm's m_total_clocks)

    // resampler (chip samples -> 44.1 kHz)
    private int _prev, _cur;                 // chip samples _samples-2 and _samples-1
    private long _outIndex;                  // the next 44.1 kHz sample's index
    private readonly List<short> _out = new();
    public bool Enabled;
    /// <summary>Raw chip samples (before resampling) are also kept here when set (tests: the differential check against ymfm).</summary>
    public List<short>? RawTap;
    /// <summary>The level of the chip's output against the 16-bit mix (1 = ymfm's raw output; one full-volume channel is about +-4000).</summary>
    public int Gain = 1;

    private static readonly ushort[][] Waveforms;
    static Opl2() { Waveforms = BuildWaveforms(); }   // after every field initializer (the tables live in the other half of the class)

    public Opl2(Func<long> pitNow)
    {
        _pitNow = pitNow;
        for (int i = 0; i < 18; i++) _ops[i] = new Op { OpOffs = i + 2 * (i / 6) };
        int[] map = { 0, 3, 1, 4, 2, 5, 6, 9, 7, 10, 8, 11, 12, 15, 13, 16, 14, 17 };
        for (int c = 0; c < 9; c++)
        {
            _ch[c] = new Channel { ChOffs = c, Op0 = _ops[map[c * 2]], Op1 = _ops[map[c * 2 + 1]] };
            _ch[c].Op0.ChOffs = c; _ch[c].Op1.ChOffs = c;
        }
        if (SinTable.Length != 256 || PowerMantissa.Length != 256 || IncrementTable.Length != 64) throw new InvalidOperationException("OPL2 tables");
        Reset();
    }

    private long ChipNow => _pitNow() * 3;

    // ================= tables (ymfm_opl.cpp opl_registers_base constructor, ymfm_fm.ipp) =================

    private static ushort[][] BuildWaveforms()
    {
        var w = new ushort[4][];
        for (int i = 0; i < 4; i++) w[i] = new ushort[1024];
        for (int i = 0; i < 1024; i++) w[0][i] = (ushort)(AbsSinAttenuation((uint)i) | (((i >> 9) & 1) << 15));
        ushort zero = w[0][0];
        for (int i = 0; i < 1024; i++)
        {
            w[1][i] = ((i >> 9) & 1) != 0 ? zero : w[0][i];
            w[2][i] = (ushort)(w[0][i] & 0x7fff);
            w[3][i] = ((i >> 8) & 1) != 0 ? zero : (ushort)(w[0][i] & 0x7fff);
        }
        return w;
    }

    private static uint AbsSinAttenuation(uint input) { if ((input & 0x100) != 0) input = ~input; return SinTable[input & 0xff]; }
    private static uint AttenuationToVolume(uint input) => (uint)(((PowerMantissa[input & 0xff] | 0x400) << 2) >> (int)(input >> 8));
    private static uint AttenuationIncrement(uint rate, uint index) => (IncrementTable[rate] >> (int)(4 * index)) & 0xf;
    private static uint Bits(uint v, int start, int count = 1) => (v >> start) & ((1u << count) - 1);
    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    // ================= registers (opl_registers_base<2>) =================

    private uint Reg(int i, int start, int count) => Bits(_regs[i & 0xff], start, count);
    private bool WaveformEnable => Reg(0x01, 5, 1) != 0;
    private uint TimerAValue => Reg(0x02, 0, 8) * 4;
    private uint TimerBValue => Reg(0x03, 0, 8);
    private byte StatusMask => (byte)(_regs[0x04] & 0x78);
    private bool IrqReset => Reg(0x04, 7, 1) != 0;
    private bool ResetTimerB => (Reg(0x04, 7, 1) | Reg(0x04, 5, 1)) != 0;
    private bool ResetTimerA => (Reg(0x04, 7, 1) | Reg(0x04, 6, 1)) != 0;
    private bool LoadTimerB => Reg(0x04, 1, 1) != 0;
    private bool LoadTimerA => Reg(0x04, 0, 1) != 0;
    private bool Csm => Reg(0x08, 7, 1) != 0;
    private uint NoteSelect => Reg(0x08, 6, 1);
    private uint LfoAmDepth => Reg(0xbd, 7, 1);
    private uint LfoPmDepth => Reg(0xbd, 6, 1);
    private bool RhythmEnable => Reg(0xbd, 5, 1) != 0;
    private uint ChBlockFreq(int c) => (Reg(0xb0 + c, 0, 5) << 8) | Reg(0xa0 + c, 0, 8);
    private uint ChFeedback(int c) => Reg(0xc0 + c, 1, 3);
    private uint ChAlgorithm(int c) => Reg(0xc0 + c, 0, 1);
    private bool OpLfoAm(int o) => Reg(0x20 + o, 7, 1) != 0;
    private bool OpLfoPm(int o) => Reg(0x20 + o, 6, 1) != 0;
    private uint OpEgSustain(int o) => Reg(0x20 + o, 5, 1);
    private uint OpKsr(int o) => Reg(0x20 + o, 4, 1);
    private uint OpMultiple(int o) => Reg(0x20 + o, 0, 4);
    private uint OpKsl(int o) { uint t = Reg(0x40 + o, 6, 2); return Bits(t, 1) | (Bits(t, 0) << 1); }
    private uint OpTotalLevel(int o) => Reg(0x40 + o, 0, 6);
    private uint OpAttackRate(int o) => Reg(0x60 + o, 4, 4);
    private uint OpDecayRate(int o) => Reg(0x60 + o, 0, 4);
    private uint OpSustainLevel(int o) => Reg(0x80 + o, 4, 4);
    private uint OpReleaseRate(int o) => Reg(0x80 + o, 0, 4);
    private int OpWaveform(int o) => WaveformEnable ? (int)Reg(0xe0 + o, 0, 2) : 0;

    private static uint EffectiveRate(uint raw, uint ksr) => raw == 0 ? 0 : Math.Min(raw + ksr, 63u);

    private static uint KeyScaleAtten(uint block, uint fnum4msb)
    {
        ReadOnlySpan<byte> t = new byte[] { 0, 24, 32, 37, 40, 43, 45, 47, 48, 50, 51, 52, 53, 54, 55, 56 };
        int r = t[(int)fnum4msb] - 8 * (int)(block ^ 7);
        return (uint)Math.Max(0, r);
    }

    private void CacheOperator(Op o)
    {
        int oo = o.OpOffs, co = o.ChOffs;
        o.Waveform = OpWaveform(oo);
        uint bf = o.BlockFreq = ChBlockFreq(co);
        uint keycode = Bits(bf, 10, 3) << 1;
        keycode |= Bits(bf, 9 - (int)NoteSelect, 1);
        uint mul = OpMultiple(oo);
        o.Multiple = ((mul & 0xe) | Bits(0xc2aa, (int)mul)) * 2;
        if (o.Multiple == 0) o.Multiple = 1;
        o.PhaseStep = OpLfoPm(oo) ? PhaseStepDynamic : ComputePhaseStep(o, 0);
        o.TotalLevel = OpTotalLevel(oo) << 3;
        uint ksl = OpKsl(oo);
        if (ksl != 0) o.TotalLevel += KeyScaleAtten(Bits(bf, 10, 3), Bits(bf, 6, 4)) << (int)ksl;
        uint sl = OpSustainLevel(oo);
        sl |= (sl + 1) & 0x10;
        o.EgSustain = sl << 5;
        uint ksrval = keycode >> (int)(2 * (OpKsr(oo) ^ 1));
        o.EgRate[EgAttack] = (byte)EffectiveRate(OpAttackRate(oo) * 4, ksrval);
        o.EgRate[EgDecay] = (byte)EffectiveRate(OpDecayRate(oo) * 4, ksrval);
        o.EgRate[EgSustain] = (byte)(OpEgSustain(oo) != 0 ? 0 : EffectiveRate(OpReleaseRate(oo) * 4, ksrval));
        o.EgRate[EgRelease] = (byte)EffectiveRate(OpReleaseRate(oo) * 4, ksrval);
        o.EgRate[EgDepress] = 0x3f;
    }

    private uint ComputePhaseStep(Op o, int lfoRawPm)
    {
        if (!OpLfoPm(o.OpOffs)) lfoRawPm = 0;
        uint bf = o.BlockFreq;
        uint fnum = Bits(bf, 0, 10) << 2;
        fnum += (uint)(lfoRawPm * (int)Bits(bf, 7, 3)) >> 1;   // C++: int32 * uint32 is unsigned, the shift logical
        fnum &= 0xfff;
        uint block = Bits(bf, 10, 3);
        uint step = (fnum << (int)block) >> 2;
        return (step * o.Multiple) >> 1;
    }

    private static readonly sbyte[] PmScale = { 8, 4, 0, -4, -8, -4, 0, 4 };

    private int ClockNoiseAndLfo()
    {
        _noiseLfsr <<= 1;
        _noiseLfsr |= Bits(_noiseLfsr, 23) ^ Bits(_noiseLfsr, 9) ^ Bits(_noiseLfsr, 8) ^ Bits(_noiseLfsr, 1);
        uint am = _lfoAmCounter++;
        if (am >= 210 * 64 - 1) _lfoAmCounter = 0;
        int shift = 9 - 2 * (int)LfoAmDepth;
        _lfoAm = (byte)(((am < 105 * 64) ? am : (210 * 64 + 63 - am)) >> shift);
        uint pm = _lfoPmCounter++;
        return PmScale[Bits(pm, 10, 3)] >> (int)(LfoPmDepth ^ 1);
    }

    private uint NoiseState => _noiseLfsr >> 23;

    // ================= the operator (fm_operator) =================

    private bool Prepare(Op o)
    {
        CacheOperator(o);
        ClockKeystate(o, o.KeyonLive != 0 ? 1u : 0u);
        o.KeyonLive &= unchecked((byte)~(1 << KeyonCsm));
        return o.EnvState != EgRelease || o.EnvAtt < EgQuiet;
    }

    private void ClockOp(Op o, uint envCounter, int lfoRawPm)
    {
        if (Bits(envCounter, 0, 2) == 0) ClockEnvelope(o, envCounter >> 2);
        uint step = o.PhaseStep;
        if (step == PhaseStepDynamic) step = ComputePhaseStep(o, lfoRawPm);
        o.Phase += step;
    }

    private static uint OpPhase(Op o) => o.Phase >> 10;

    private uint EnvelopeAttenuation(Op o, uint amOffset)
    {
        uint r = o.EnvAtt;
        if (OpLfoAm(o.OpOffs)) r += amOffset;
        r += o.TotalLevel;
        return Math.Min(r, 0x3ffu);
    }

    private int ComputeVolume(Op o, uint phase, uint amOffset)
    {
        if (o.EnvAtt > EgQuiet) return 0;
        uint sinAtt = Waveforms[o.Waveform][phase & 0x3ff];
        uint envAtt = EnvelopeAttenuation(o, amOffset) << 2;
        int r = (int)AttenuationToVolume((sinAtt & 0x7fff) + envAtt);
        return Bits(sinAtt, 15) != 0 ? -r : r;
    }

    private static void KeyOnOff(Op o, uint on, int type) => o.KeyonLive = (byte)((o.KeyonLive & ~(1 << type)) | ((int)(on & 1) << type));

    private static void StartAttack(Op o)
    {
        if (o.EnvState == EgAttack) return;
        o.EnvState = EgAttack;
        o.Phase = 0;
        if (o.EgRate[EgAttack] >= 62) o.EnvAtt = 0;
    }

    private static void StartRelease(Op o)
    {
        if (o.EnvState >= EgRelease) return;
        o.EnvState = EgRelease;
    }

    private static void ClockKeystate(Op o, uint keystate)
    {
        if ((keystate ^ o.KeyState) != 0)
        {
            o.KeyState = (byte)keystate;
            if (keystate != 0) StartAttack(o); else StartRelease(o);
        }
    }

    private static void ClockEnvelope(Op o, uint envCounter)
    {
        if (o.EnvState == EgAttack && o.EnvAtt == 0) o.EnvState = EgDecay;
        if (o.EnvState == EgDecay && o.EnvAtt >= o.EgSustain) o.EnvState = EgSustain;
        uint rate = o.EgRate[o.EnvState];
        int rateShift = (int)(rate >> 2);
        envCounter <<= rateShift;
        if (Bits(envCounter, 0, 11) != 0) return;
        uint relevant = Bits(envCounter, rateShift <= 11 ? 11 : rateShift, 3);
        uint inc = AttenuationIncrement(rate, relevant);
        if (o.EnvState == EgAttack)
        {
            if (rate < 62) o.EnvAtt = (ushort)(o.EnvAtt + ((~o.EnvAtt * (int)inc) >> 4));
        }
        else
        {
            o.EnvAtt = (ushort)(o.EnvAtt + inc);
            if (o.EnvAtt >= 0x400) o.EnvAtt = 0x3ff;
        }
    }

    // ================= the channel (fm_channel) =================

    private void KeyOnOffChannel(Channel c, uint states, int type) { KeyOnOff(c.Op0, Bits(states, 0), type); KeyOnOff(c.Op1, Bits(states, 1), type); }

    private void ClockChannel(Channel c, uint envCounter, int lfoRawPm)
    {
        c.Fb0 = c.Fb1;
        c.Fb1 = c.FbIn;
        ClockOp(c.Op0, envCounter, lfoRawPm);
        ClockOp(c.Op1, envCounter, lfoRawPm);
    }

    private int Op1WithFeedback(Channel c, uint am)
    {
        int opmod = 0;
        uint fb = ChFeedback(c.ChOffs);
        if (fb != 0) opmod = (c.Fb0 + c.Fb1) >> (int)(10 - fb);
        int v = ComputeVolume(c.Op0, (uint)(OpPhase(c.Op0) + opmod), am);
        c.FbIn = (short)v;
        return v;
    }

    private const int RShift = 1, ClipMax = 32767;

    private int Output2Op(Channel c)
    {
        uint am = _lfoAm;
        Op1WithFeedback(c, am);
        int result;
        if (Bits(ChAlgorithm(c.ChOffs), 0) == 0)
        {
            int opmod = c.Fb1 >> 1;   // MODULATOR_DELAY: the modulator's previous output
            result = ComputeVolume(c.Op1, (uint)(OpPhase(c.Op1) + opmod), am) >> RShift;
        }
        else
        {
            result = c.Fb1 >> RShift;
            result += ComputeVolume(c.Op1, OpPhase(c.Op1), am) >> RShift;
            result = Clamp(result, -ClipMax - 1, ClipMax);
        }
        return result;
    }

    private int OutputRhythmCh6(Channel c)
    {
        uint am = _lfoAm;
        int opout1 = Op1WithFeedback(c, am);
        int opmod = Bits(ChAlgorithm(c.ChOffs), 0) != 0 ? 0 : (opout1 >> 1);
        int result = ComputeVolume(c.Op1, (uint)(OpPhase(c.Op1) + opmod), am) >> RShift;
        return result * 2;
    }

    private int OutputRhythmCh7(Channel c, uint phaseSelect)
    {
        uint am = _lfoAm;
        uint noise = Bits(NoiseState, 0);
        uint phase = (phaseSelect << 9) | (0xd0u >> (int)(2 * (noise ^ phaseSelect)));
        int result = ComputeVolume(c.Op0, phase, am) >> RShift;
        uint op13phase = OpPhase(c.Op0);
        phase = (0x100u << (int)Bits(op13phase, 8)) ^ (noise << 8);
        result += ComputeVolume(c.Op1, phase, am) >> RShift;
        result = Clamp(result, -ClipMax - 1, ClipMax);
        return result * 2;
    }

    private int OutputRhythmCh8(Channel c, uint phaseSelect)
    {
        uint am = _lfoAm;
        int result = ComputeVolume(c.Op0, OpPhase(c.Op0), am) >> RShift;
        uint phase = 0x100 | (phaseSelect << 9);
        result += ComputeVolume(c.Op1, phase, am) >> RShift;
        result = Clamp(result, -ClipMax - 1, ClipMax);
        return result * 2;
    }

    // ================= the engine (fm_engine_base) =================

    /// <summary>Power-on / reset (ymfm <c>ym3812::reset</c>).</summary>
    public void Reset()
    {
        SetResetStatus(0, 0xff);
        Array.Clear(_regs);
        EngineModeWrite(0);
        foreach (var c in _ch) { c.Fb0 = c.Fb1 = c.FbIn = 0; }
        foreach (var o in _ops) { o.Phase = 0; o.EnvAtt = 0x3ff; o.EnvState = EgRelease; o.KeyState = 0; o.KeyonLive = 0; }
        _address = 0;
        if (_alt != null) { _alt.Reset(); _altQueue.Clear(); _altBufPos = _altBufLen = 0; }
    }

    private void ClockEngine()
    {
        if (_modifiedChannels != 0 || _prepareCount++ >= 4096)
        {
            _activeChannels = 0;
            for (int c = 0; c < 9; c++)
            {
                bool a = Prepare(_ch[c].Op0);
                a |= Prepare(_ch[c].Op1);
                if (a) _activeChannels |= 1u << c;
            }
            _modifiedChannels = _prepareCount = 0;
        }
        _envCounter += 4;
        int pm = ClockNoiseAndLfo();
        for (int c = 0; c < 9; c++) ClockChannel(_ch[c], _envCounter, pm);
    }

    private short OutputEngine()
    {
        int sum = 0;
        uint mask = _activeChannels;
        if (RhythmEnable)
        {
            uint p13 = OpPhase(_ops[13]), p17 = OpPhase(_ops[17]);
            uint sel = (Bits(p13, 2) ^ Bits(p13, 7)) | Bits(p13, 3) | (Bits(p17, 5) ^ Bits(p17, 3));
            for (int c = 0; c < 9; c++)
            {
                if ((mask >> c & 1) == 0) continue;
                sum += c == 6 ? OutputRhythmCh6(_ch[c]) : c == 7 ? OutputRhythmCh7(_ch[c], sel) : c == 8 ? OutputRhythmCh8(_ch[c], sel) : Output2Op(_ch[c]);
            }
        }
        else
            for (int c = 0; c < 9; c++) if ((mask >> c & 1) != 0) sum += Output2Op(_ch[c]);
        return RoundtripFp(sum);
    }

    private static short RoundtripFp(int value)
    {
        if (value < -32768) return -32768;
        if (value > 32767) return 32767;
        int scan = value ^ (value >> 31);
        int exponent = 7 - System.Numerics.BitOperations.LeadingZeroCount((uint)(scan << 17));
        exponent = Math.Max(exponent, 1) - 1;
        int mask = (1 << exponent) - 1;
        return (short)(value & ~mask);
    }

    private void WriteRegister(byte reg, byte data)
    {
        if (_alt != null) { if (_altBlock > 0) _altQueue.Add((reg, data)); else _alt.Write(reg, data); }
        if (reg == 0x04) { EngineModeWrite(data); return; }
        _modifiedChannels = 0x1ff;
        _regs[reg] = data;
        if (reg == 0xbd)
        {
            uint mask = Bits(data, 5) != 0 ? Bits(data, 0, 5) : 0;
            KeyOnOffChannel(_ch[6], Bits(mask, 4) != 0 ? 3u : 0u, KeyonRhythm);
            KeyOnOffChannel(_ch[7], Bits(mask, 0) | (Bits(mask, 3) << 1), KeyonRhythm);
            KeyOnOffChannel(_ch[8], Bits(mask, 2) | (Bits(mask, 1) << 1), KeyonRhythm);
        }
        else if ((reg & 0xf0) == 0xb0 && (reg & 0x0f) < 9)
            KeyOnOffChannel(_ch[reg & 0x0f], Bits(data, 5) != 0 ? 15u : 0u, KeyonNormal);
    }

    private void EngineModeWrite(byte data)
    {
        _modifiedChannels = 0x1ff;
        if ((data & 0x80) != 0) _regs[0x04] |= 0x80; else _regs[0x04] = data;
        if (IrqReset) SetResetStatus(0, 0x78);
        else
        {
            byte reset = 0;
            if (ResetTimerB) reset |= StatusTimerB;
            if (ResetTimerA) reset |= StatusTimerA;
            SetResetStatus(0, reset);
            UpdateTimer(1, LoadTimerB, -(int)(_samples & 15));
            UpdateTimer(0, LoadTimerA, 0);
        }
    }

    private long _now;   // the chip clock of the access being handled (timers start from it)

    private void UpdateTimer(int t, bool enable, int delta)
    {
        if (enable && _timerRunning[t] == 0)
        {
            int period = t == 0 ? (int)(1024 - TimerAValue) : 16 * (int)(256 - TimerBValue);
            period += delta;
            _timerEnd[t] = _now + (long)period * ClocksPerSample;
            _timerRunning[t] = 1;
        }
        else if (!enable)
        {
            _timerEnd[t] = long.MaxValue;
            _timerRunning[t] = 0;
        }
    }

    private void TimerExpired(int t)
    {
        SetResetStatus(t == 0 ? StatusTimerA : StatusTimerB, 0);
        if (t == 0 && Csm)
            for (int c = 0; c < 9; c++) { KeyOnOffChannel(_ch[c], 0xf, KeyonCsm); _modifiedChannels |= 1u << c; }
        _timerRunning[t] = 0;
        UpdateTimer(t, true, 0);
    }

    private void SetResetStatus(byte set, byte reset)
    {
        _status = (byte)((_status | set) & ~reset);
        _irqState = (byte)((_status & _irqMask & ~StatusMask) != 0 ? 1 : 0);
        if (_irqState != 0) _status |= StatusIrq; else _status = (byte)(_status & ~StatusIrq);
    }

    // ================= time =================

    /// <summary>Runs the chip (timers, and the sound while <see cref="Enabled"/>) up to the machine's present time.</summary>
    public void CatchUp() => RunTo(ChipNow);

    /// <summary>Runs the chip up to chip clock <paramref name="clock"/>: every sample at or before it (sample k at clock 72 k) and every timer that expires at or before it, in time order (a timer
    /// first when both fall on the same clock).</summary>
    public void RunTo(long clock)
    {
        while (true)
        {
            long tTimer = Math.Min(_timerEnd[0], _timerEnd[1]);
            long tSample = _samples * ClocksPerSample;
            if (tTimer <= clock && tTimer <= tSample)
            {
                _now = tTimer;
                TimerExpired(_timerEnd[0] == tTimer ? 0 : 1);
                continue;
            }
            if (_alt != null)
            {
                // an alternative engine draws the sound on its own timeline; ymfm's sample clock only paces the timers here
                if (tSample > clock) { if (Enabled) RunAlt(clock); break; }
                long until = Math.Min(clock, tTimer - 1);
                _samples = until / ClocksPerSample + 1;
                continue;
            }
            if (tSample > clock) break;
            if (!Enabled)
            {
                // nothing to compute: jump to the next timer (or to the present)
                long until = Math.Min(clock, tTimer - 1);
                _samples = until / ClocksPerSample + 1;
                continue;
            }
            ClockEngine();
            short v = OutputEngine();
            _samples++;
            RawTap?.Add(v);
            Resample(v);
        }
        _now = clock;
    }

    // ================= an alternative engine (OplEngines.cs) =================

    private IOplEngine? _alt;
    private int _altBlock;
    private readonly List<(byte reg, byte v)> _altQueue = new();
    private long _altSamples;          // engine samples drawn; sample k sits at chip clock k * 3 * PIT / rate
    private short[] _altBuf = new short[1];
    private int _altBufPos, _altBufLen;
    private int _aPrev, _aCur;
    private long _aOutIndex;

    /// <summary>The engine heard (null = ymfm, the card's own). The registers as they are now are replayed into a new engine (notes keyed on stay on).</summary>
    public IOplEngine? Engine
    {
        get => _alt;
        set
        {
            CatchUp();
            _alt = value; _altQueue.Clear(); _altBufPos = _altBufLen = 0;
            if (_alt == null) { _modifiedChannels = 0x1ff; return; }
            _altBlock = _alt.BlockSamples;
            if (_altBuf.Length < Math.Max(1, _altBlock)) _altBuf = new short[Math.Max(1, _altBlock)];
            _alt.Reset();
            ReplayIntoAlt();
            AltRestart();
        }
    }

    private void ReplayIntoAlt()
    {
        if (_alt == null) return;
        _alt.Write(0x01, _regs[0x01]); _alt.Write(0x08, _regs[0x08]);
        for (int r = 0x20; r < 0x100; r++) if (r != 0xbd && (r & 0xf0) != 0xb0) _alt.Write(r, _regs[r]);
        for (int r = 0xb0; r < 0xb9; r++) _alt.Write(r, _regs[r]);
        _alt.Write(0xbd, _regs[0xbd]);
    }

    private void AltRestart()
    {
        if (_alt == null) return;
        long chip = ChipNow;
        _altSamples = (long)((Int128)chip * _alt.Rate / (3L * Pit8254.Freq)) + 1;
        _aOutIndex = (long)((Int128)_pitNow() * Rate / Pit8254.Freq);
        _aPrev = _aCur = 0; _altBufPos = _altBufLen = 0;
    }

    private short NextAlt()
    {
        if (_altBufPos >= _altBufLen)
        {
            if (_altBlock > 0)
            {
                _alt!.Generate(_altBuf, _altBlock);   // the block is drawn with the registers as they were before it; its writes land after it
                foreach (var (r, v) in _altQueue) _alt.Write(r, v);
                _altQueue.Clear();
                _altBufLen = _altBlock;
            }
            else { _alt!.Generate(_altBuf, 1); _altBufLen = 1; }
            _altBufPos = 0;
        }
        return _altBuf[_altBufPos++];
    }

    private void RunAlt(long clock)
    {
        long rate = _alt!.Rate, den = 3L * Pit8254.Freq;
        while ((Int128)_altSamples * den <= (Int128)clock * rate)
        {
            _aPrev = _aCur; _aCur = NextAlt();
            long k = _altSamples++;
            // 44.1 kHz sample m sits at engine-sample position m * rate / 44100
            while (true)
            {
                long num = _aOutIndex * rate;
                long idx = num / Rate;
                if (idx >= k) break;
                if (idx < k - 1) { _aOutIndex++; continue; }
                long frac = num % Rate;
                int s = (int)(_aPrev + (_aCur - _aPrev) * frac / Rate) * Gain;
                short o = (short)Clamp(s, -32768, 32767);
                _out.Add(o); _out.Add(o);
                _aOutIndex++;
            }
        }
        if (_out.Count > Rate * 4) _out.RemoveRange(0, _out.Count - Rate * 2);
    }

    private void Resample(int v)
    {
        _prev = _cur; _cur = v;
        long k = _samples - 1;   // the chip sample just made; the samples between k-1 and k are interpolated
        const long Den = (long)Rate * PitPerSample;   // 44.1 kHz sample m sits at chip-sample position m * 1193182 / Den
        while (true)
        {
            long num = _outIndex * Pit8254.Freq;
            long idx = num / Den;
            if (idx >= k) break;
            if (idx < k - 1) { _outIndex++; continue; }   // (only right after a restart)
            long frac = num % Den;
            int s = (int)(_prev + (_cur - _prev) * frac / Den) * Gain;
            short o = (short)Clamp(s, -32768, 32767);
            _out.Add(o); _out.Add(o);
            _outIndex++;
        }
        if (_out.Count > Rate * 4) _out.RemoveRange(0, _out.Count - Rate * 2);
    }

    /// <summary>Adds up to <paramref name="count"/> shorts (stereo interleaved) of the sound drawn so far onto <paramref name="buffer"/> (saturating); returns how many were added.</summary>
    public int MixInto(short[] buffer, int count)
    {
        int n = Math.Min(_out.Count, count) & ~1;
        for (int i = 0; i < n; i++) buffer[i] = (short)Clamp(buffer[i] + _out[i], -32768, 32767);
        _out.RemoveRange(0, n);
        return n;
    }

    public int Pending => _out.Count;

    /// <summary>Puts the chip's sample clock at the machine's present time (after loading a state that had no chip in it).</summary>
    public void Resync() { _samples = ChipNow / ClocksPerSample + 1; _timerEnd[0] = _timerEnd[1] = long.MaxValue; _timerRunning[0] = _timerRunning[1] = 0; }

    /// <summary>FA-ST-3: the drawn samples and both resamplers' positions and history (ymfm's and the alternative engine's, with the block in progress), so a state load continues sample for sample.</summary>
    public void SaveOutput(BinaryWriter w)
    {
        PcOut.Write(w, _out); w.Write(_outIndex); w.Write(_prev); w.Write(_cur); w.Write(_samples);
        w.Write(_alt != null);
        if (_alt == null) return;
        w.Write(_altSamples); w.Write(_altBuf.Length); foreach (var v in _altBuf) w.Write(v); w.Write(_altBufPos); w.Write(_altBufLen); w.Write(_aPrev); w.Write(_aCur); w.Write(_aOutIndex);
    }

    public void LoadOutput(BinaryReader r)
    {
        PcOut.Read(r, _out); _outIndex = r.ReadInt64(); _prev = r.ReadInt32(); _cur = r.ReadInt32(); _samples = r.ReadInt64();
        bool alt = r.ReadBoolean();
        if (!alt) { if (_alt != null) AltRestart(); return; }
        long altSamples = r.ReadInt64(); var buf = new short[r.ReadInt32()]; for (int i = 0; i < buf.Length; i++) buf[i] = r.ReadInt16();
        int pos = r.ReadInt32(), len = r.ReadInt32(), ap = r.ReadInt32(), ac = r.ReadInt32(); long aoi = r.ReadInt64();
        if (_alt == null) return;   // the engine changed since (LoadEngine reported it): nothing of the old engine's output applies
        _altSamples = altSamples; if (_altBuf.Length < buf.Length) _altBuf = new short[buf.Length]; Array.Copy(buf, _altBuf, buf.Length);
        _altBufPos = pos; _altBufLen = len; _aPrev = ap; _aCur = ac; _aOutIndex = aoi;
    }

    /// <summary>After a state load without the output section (an older state) or a cold boot: forget the drawn samples, carry on from the present.</summary>
    public void Restart()
    {
        _out.Clear();
        long chip = ChipNow;
        if (_samples * ClocksPerSample <= chip) _samples = chip / ClocksPerSample + 1;
        _outIndex = (long)((Int128)_pitNow() * Rate / Pit8254.Freq);
        _prev = _cur = 0;
        AltRestart();
    }

    // ================= ports 388h / 389h =================

    /// <summary>A read of port 388h: the status (ymfm: status | 06h). 389h reads FFh.</summary>
    public long StatusReads, DataWrites;   // diagnostics (not saved)

    public byte ReadStatus() { StatusReads++; CatchUp(); return (byte)((_status & ~StatusMask) | 0x06); }

    public void WriteAddress(byte v) { CatchUp(); _address = v; }

    public void WriteData(byte v) { DataWrites++; CatchUp(); WriteRegister(_address, v); }

    /// <summary>Direct register write at chip clock <paramref name="clock"/> (tests: scripted sequences).</summary>
    public void WriteAt(long clock, byte reg, byte v) { RunTo(clock); WriteRegister(reg, v); }

    public byte Register(int i) => _regs[i & 0xff];

    // ================= save state =================

    public void Save(BinaryWriter w)
    {
        w.Write(_regs); w.Write(_address); w.Write(_envCounter); w.Write(_status); w.Write(_irqMask); w.Write(_irqState);
        w.Write(_timerRunning); w.Write(_timerEnd[0]); w.Write(_timerEnd[1]);
        w.Write(_lfoAmCounter); w.Write(_lfoPmCounter); w.Write(_noiseLfsr); w.Write(_lfoAm); w.Write(_samples); w.Write(_prepareCount);
        foreach (var c in _ch) { w.Write(c.Fb0); w.Write(c.Fb1); w.Write(c.FbIn); }
        foreach (var o in _ops) { w.Write(o.Phase); w.Write(o.EnvAtt); w.Write((byte)o.EnvState); w.Write(o.KeyState); w.Write(o.KeyonLive); }
    }

    public void Load(BinaryReader r)
    {
        r.Read(_regs, 0, 256); _address = r.ReadByte(); _envCounter = r.ReadUInt32(); _status = r.ReadByte(); _irqMask = r.ReadByte(); _irqState = r.ReadByte();
        r.Read(_timerRunning, 0, 2); _timerEnd[0] = r.ReadInt64(); _timerEnd[1] = r.ReadInt64();
        _lfoAmCounter = r.ReadUInt16(); _lfoPmCounter = r.ReadUInt16(); _noiseLfsr = r.ReadUInt32(); _lfoAm = r.ReadByte(); _samples = r.ReadInt64(); _prepareCount = r.ReadUInt32();
        foreach (var c in _ch) { c.Fb0 = r.ReadInt16(); c.Fb1 = r.ReadInt16(); c.FbIn = r.ReadInt16(); }
        foreach (var o in _ops) { o.Phase = r.ReadUInt32(); o.EnvAtt = r.ReadUInt16(); o.EnvState = r.ReadByte(); o.KeyState = r.ReadByte(); o.KeyonLive = r.ReadByte(); }
        _modifiedChannels = 0x1ff;   // ymfm invalidates the caches after a restore
        _out.Clear();
        if (_alt != null) { _alt.Reset(); _altQueue.Clear(); ReplayIntoAlt(); }   // re-keyed from the registers; LoadEngine then puts the engine's own saved state back
    }

    /// <summary>FA-ST-1: the alternative engine's own state (its id, then every field of the engine object, EngineState) and the writes queued for its next block.</summary>
    public void SaveEngine(BinaryWriter w)
    {
        w.Write(_alt?.Id ?? "");
        if (_alt == null) return;
        EngineState.Save(_alt, typeof(object), w, units: true);
        w.Write(_altQueue.Count); foreach (var (r, v) in _altQueue) { w.Write(r); w.Write(v); }
    }

    public void LoadEngine(BinaryReader r)
    {
        string id = r.ReadString();
        if (id.Length == 0) { if (_alt != null) Console.Error.WriteLine($"[EngineState] AdLib {_alt.Id}: the state was saved on ymfm; engine re-keyed from the registers (not sample-exact)"); return; }
        if (_alt != null && _alt.Id == id)
        {
            if (!EngineState.TryLoad(_alt, typeof(object), r, units: true)) EngineState.ReportRekey(_alt);
            int n = r.ReadInt32(); _altQueue.Clear(); for (int i = 0; i < n; i++) _altQueue.Add((r.ReadByte(), r.ReadByte()));
        }
        else
        {
            // skip another engine's section
            r.ReadUInt64(); r.ReadUInt64(); r.ReadBytes(r.ReadInt32());
            int n = r.ReadInt32(); r.ReadBytes(n * 2);
            if (_alt != null) Console.Error.WriteLine($"[EngineState] AdLib {_alt.Id}: the state was saved on {id}; engine re-keyed from the registers (not sample-exact)");
        }
    }

    /// <summary>A state from before FA-ST-1: the engine keeps its re-key, reported.</summary>
    public void NoEngineState() { if (_alt != null) Console.Error.WriteLine($"[EngineState] AdLib {_alt.Id}: state from before FA-ST-1; engine re-keyed from the registers (not sample-exact)"); }
}
