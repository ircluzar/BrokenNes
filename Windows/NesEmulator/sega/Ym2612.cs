using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>Receives each stereo sample (16-bit range, left and right) the chip produces, as it produces it. One call per FM sample, 144 chip clocks apart.</summary>
public interface IYm2612Sink
{
    void Sample(int left, int right);
}

/// <summary>
/// The Yamaha YM2612 (OPN2), the Genesis / Mega Drive's FM chip: six channels of four sine operators in eight algorithms, a phase generator with detune and multiple, an envelope generator (attack,
/// decay, sustain, release, key scaling, SSG-EG), total level, operator-1 feedback, one LFO (AM and PM), channel 3's special mode (a frequency per operator) and CSM, a 9-bit DAC that replaces
/// channel 6, two timers and the busy flag. One sample is 144 clocks of the chip's input clock (master / 7), 53,267 Hz NTSC.
/// </summary>
/// <remarks>
/// <para><b>Source.</b> A C# port of ymfm's OPN2 (<c>ym2612</c> and <c>ym3438</c> in ymfm_opn.cpp, with the shared FM engine of ymfm_fm.ipp and the <c>opna_registers</c> of ymfm_opn.cpp),
/// ymfm commit 81aec25 (2026-07-27), BSD-3-Clause, Copyright (c) 2021 Aaron Giles. The notice and the list of what changed are in <c>sega/THIRD_PARTY_NOTICES.md</c> section 1c. With
/// <see cref="SlotPipeline"/> off the arithmetic is ymfm's line for line (the same tables, shifts, truncations and clamps: <c>--ym-oracle-test</c> checks that against ymfm itself, sample for sample); the class layout is not:
/// the engine, the registers, the operators and the two chip variants are one class here with the operators in an array, the timers keep time in chip clocks instead of calling back into a host, and the output goes
/// to an <see cref="IYm2612Sink"/>.</para>
/// <para><b>The slot pipeline (on by default).</b> ymfm computes a whole sample at once; the chip computes one operator slot per clock in a 24-slot pipeline. Where that shows in the output, this class follows the
/// behaviour of the Nuked-OPN2 oracle (found by comparing scripted runs, none of its code read): the order and the clamp of the channel accumulator, the one-sample delays of the LFO into each slot, the tick phase of the
/// fastest envelope rates, timer A's load phase, the busy flag's latency, the status echo of the discrete chip, the ladder's offsets. The bounds of the match, and what is not exact (note-on and note-off timing between
/// operators, SSG-EG, CSM), are stated by the checks of <c>--ym-oracle-test</c> and in the ledger.</para>
/// <para><b>Time.</b> The chip runs by the clock it is told (<see cref="RunTo"/> and the access methods take the chip clock, master / 7). Sample n completes at clock 144 n. An access lands in the sample
/// whose interval contains it; the timers count from the access that loaded them; the busy flag stays up for <see cref="BusyClocks"/> after a data write.</para>
/// <para><b>Two chips.</b> The discrete YM2612 of the first Mega Drives (<see cref="Ladder"/> true: the DAC's crossover discontinuity, ymfm's <c>ym2612::generate</c>) and the YM3438 / integrated version of
/// the later ones (no ladder, ymfm's <c>ym3438::generate</c>). Output is a signed 16-bit-range stereo pair per sample.</para>
/// </remarks>
public sealed partial class Ym2612 : IHubVoiceSource
{
    /// <summary>Chip input clocks per sample: the divide-by-6 prescaler times the 24 operator slots.</summary>
    public const int ClocksPerSample = 144;
    /// <summary>How long the busy flag stays up after a data write: 32 prescaled clocks = 192 input clocks (about 25 microseconds).</summary>
    public const int BusyClocks = 32 * 6;
    /// <summary>The busy flag rises this many chip clocks after the data write (two prescaled clocks), measured with Nuked-OPN2, and then stays up for <see cref="BusyClocks"/>.</summary>
    public const int BusyLatency = 12;
    /// <summary>The voices <see cref="DescribeVoices"/> reports: FM 1 to 6, then the DAC.</summary>
    public const int VoiceCount = 7;

    private const int Channels = 6, Operators = 24, AllChannels = 0x3F;
    private const int EgAttack = 1, EgDecay = 2, EgSustain = 3, EgRelease = 4;
    private const int EgQuiet = 0x380;
    private const int KeyonNormal = 0, KeyonCsm = 2;
    private const uint PhaseStepDynamic = 1;
    private const int RegMode = 0x27;

    /// <summary>One operator (a "slot"): a sine wave shaped by an envelope. Everything is ymfm's <c>fm_operator</c> plus its cached register data.</summary>
    private sealed class Op
    {
        public int Choffs, Opoffs;             // register offsets of its channel and of itself
        public uint Phase;                     // 10.10 fixed point
        public int EnvAtt = 0x3FF;             // 4.6 attenuation
        public int EnvState = EgRelease;
        public int SsgInverted;                // 0 or 1
        public int KeyState;                   // 0 or 1
        public int KeyonLive;                  // bit 0 direct, bit 2 CSM
        // cached at prepare time (ymfm's opdata_cache), plus the register bits that are read every sample
        public uint PhaseStep, TotalLevel, BlockFreq, Multiple, EgSustainLevel;
        public int Detune;
        public readonly int[] Rate = new int[5];   // by envelope state (index 0 unused)
        public bool SsgEnable, AmEnable;
        public int SsgMode;
        public int PmSens;
    }

    // ---- configuration ----
    /// <summary>True for the discrete YM2612 (Model 1): the DAC's crossover discontinuity (the "ladder effect") is on. False for the YM3438 and the integrated chips.</summary>
    public bool Ladder { get; }
    /// <summary>Whether a status read answers on all four ports (the integrated chips) or only on port 0.</summary>
    public bool StatusOnAnyPort { get; set; }
    /// <summary>Called with every sample; null to drop them (the sample count still advances).</summary>
    public IYm2612Sink? Sink { get; set; }
    /// <summary>Channels to silence: bit 0 to 5 FM 1 to 6, bit 6 the DAC. A silenced channel keeps running, only its output is dropped.</summary>
    public int MuteMask { get; set; }
    /// <summary>The input clock this chip is fed (master / 7), for pitch maths in <see cref="DescribeVoices"/>.</summary>
    public double InputClockHz { get; }

    // ---- registers and engine state ----
    private readonly byte[] regs = new byte[0x200];
    private readonly Op[] ops = new Op[Operators];            // [channel * 4 + n], n = 0..3 is operator 1 to 4 in algorithm order
    private readonly short[] feedback0 = new short[Channels], feedback1 = new short[Channels], feedbackIn = new short[Channels];
    private int address;                                      // 0..0x1FF
    private int dacData;                                      // 9 bits
    private bool dacEnable;
    private uint envCounter;                                  // low 2 bits are a sub-counter
    private byte status;                                      // bit 0 timer A, bit 1 timer B
    private byte totalClocks;                                 // low 8 bits of the samples processed
    private int activeChannels = AllChannels, modifiedChannels = AllChannels, prepareCount;
    private uint lfoCounter;
    private int lfoAm;
    private readonly bool[] timerRunning = new bool[2];
    private readonly long[] timerDeadline = new long[2];
    private long nextEvent = long.MaxValue;
    private long busyStart, busyEnd;                          // the chip clocks the busy flag rises and drops at
    private long sampleCount;                                 // samples completed
    private long clockReached;                                // the chip has been run up to here
    private long now;                                         // the time of the access or timer event being handled

    /// <summary>The chip clock the chip has been run to.</summary>
    public long Clock => clockReached;
    /// <summary>Samples produced since power-on.</summary>
    public long Samples => sampleCount;
    public int LastLeft { get; private set; }
    public int LastRight { get; private set; }
    /// <summary>Data writes counted (busy-setting writes).</summary>
    public long DataWrites { get; private set; }

    /// <param name="ladder">True for the discrete YM2612 (Model 1), false for the YM3438 / integrated chip (Models 2 and 3).</param>
    /// <param name="inputClockHz">The chip's input clock; only the voice pitch maths uses it.</param>
    public Ym2612(bool ladder, double inputClockHz = 7_670_453.0)
    {
        Ladder = ladder;
        StatusOnAnyPort = !ladder;
        InputClockHz = inputClockHz;
        for (int ch = 0; ch < Channels; ch++)
        {
            int choffs = ch % 3 + 0x100 * (ch / 3);
            for (int n = 0; n < 4; n++)
            {
                // ymfm's operator map: algorithm order 1, 2, 3, 4 sits at register offsets 0, 8, 4, 12 within the channel's block (the chip's own register order is 1, 3, 2, 4)
                int opoffs = choffs + (n == 0 ? 0 : n == 1 ? 8 : n == 2 ? 4 : 12);
                ops[ch * 4 + n] = new Op { Choffs = choffs, Opoffs = opoffs };
            }
        }
        PowerOn();
    }

    public Ym2612(GenesisModel model, double inputClockHz = 7_670_453.0) : this(model == GenesisModel.Model1, inputClockHz) { }

    // =================================================================================================================
    //  Reset
    // =================================================================================================================

    public int DebugLfoAm => lfoAm;
    public int DebugLfoPm;
    public uint DebugLfoCounter => lfoCounter;
    public uint DebugPhaseStep(int channel, int op) => ops[channel * 4 + op].PhaseStep == PhaseStepDynamic ? ComputePhaseStep(ops[channel * 4 + op], DebugLfoPm) : ops[channel * 4 + op].PhaseStep;
    /// <summary>The LFO runs in real time while an operator's own state (phase, envelope) reaches its output a few samples after the access that set it up (the pipeline's latency, 4 samples of output delay here). To the output of a note the LFO is therefore that many samples further on than the sample being computed; the counter starts that far ahead.</summary>
    private const uint LfoLead = 4;
    private readonly int[] lfoHistAm = new int[4], lfoHistPm = new int[4];
    private static readonly int[] SlotOfOp = { 0, 2, 1, 3 };
    /// <summary>Per operator slot (6 x the operator's place in the chip's order 1, 3, 2, 4, plus the channel), how many samples before the freshest LFO value an operator's amplitude modulation is taken; vibrato is one more. Measured against Nuked-OPN2 (every slot exact over 6000 samples at LFO rate 6): the slots that follow the LFO's update point in the 24-slot cycle see an older value.</summary>
    private static readonly int[] LfoAmDelay = { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 2 };
    private int lfoAmUsed;
    /// <summary>Fast rates (48 and up, the rate field's top two bits all set... see <see cref="ClockEnvelope"/>) step the increment pattern from a different phase than ymfm's table: by rate modulo 4.</summary>
    private static readonly uint[] FastRatePhase = { 0, 3, 1, 1 };

    /// <summary>Everything back to the state of a chip that has just been powered: registers, operators, counters, the timer and sample clocks at zero.</summary>
    public void PowerOn()
    {
        envCounter = 0; totalClocks = 0; prepareCount = 0; lfoCounter = SlotPipeline ? LfoLead : 0; lfoAm = 0;
        sampleCount = 0; clockReached = 0; now = 0; busyStart = busyEnd = 0; nextEvent = long.MaxValue; lastStatusRead = 0;
        LastLeft = LastRight = 0; DataWrites = 0;
        address = 0; dacData = 0; dacEnable = false;
        status = 0;
        Array.Clear(feedback0); Array.Clear(feedback1); Array.Clear(feedbackIn);
        timerRunning[0] = timerRunning[1] = false;
        ResetRegisters();
    }

    /// <summary>The reset pin: registers cleared (both pans on), the timers stopped, the operators in release and silent, the DAC off. The envelope and LFO counters, the sample clock and the
    /// pending output keep going (ymfm's <c>ym2612::reset</c>; it also leaves the DAC enable and data alone, which a real reset does not, so they are cleared here).</summary>
    public void ResetRegisters()
    {
        status = 0;
        Array.Clear(regs);
        regs[0xB4] = regs[0xB5] = regs[0xB6] = 0xC0;
        regs[0x1B4] = regs[0x1B5] = regs[0x1B6] = 0xC0;
        address = 0; dacData = 0; dacEnable = false;
        ModeWrite(0);
        for (int ch = 0; ch < Channels; ch++) feedback0[ch] = feedback1[ch] = feedbackIn[ch] = 0;
        Array.Clear(hist1); Array.Clear(hist2);
        foreach (var o in ops)
        {
            o.Phase = 0; o.EnvAtt = 0x3FF; o.EnvState = EgRelease; o.SsgInverted = 0; o.KeyState = 0; o.KeyonLive = 0;
        }
        modifiedChannels = AllChannels;
    }

    // =================================================================================================================
    //  Running
    // =================================================================================================================

    /// <summary>Runs the chip to the given chip clock: completes every sample due and fires every timer that has expired, in time order.</summary>
    public void RunTo(long chipClock)
    {
        if (chipClock < clockReached) chipClock = clockReached;
        while (true)
        {
            long end = (sampleCount + 1) * ClocksPerSample;
            if (end > chipClock) break;
            if (nextEvent <= end) ProcessEvents(end);
            GenerateSample();
            sampleCount++;
        }
        if (nextEvent <= chipClock) ProcessEvents(chipClock);
        clockReached = chipClock;
    }

    private void ProcessEvents(long limit)
    {
        while (nextEvent <= limit)
        {
            int t = timerRunning[0] && (!timerRunning[1] || timerDeadline[0] <= timerDeadline[1]) ? 0 : 1;
            if (!timerRunning[t] || timerDeadline[t] > limit) break;
            long d = timerDeadline[t];
            long saved = now;
            now = d;
            TimerExpired(t);
            now = saved;
        }
        RecomputeNextEvent();
    }

    private void RecomputeNextEvent()
    {
        long e = long.MaxValue;
        if (timerRunning[0]) e = timerDeadline[0];
        if (timerRunning[1] && timerDeadline[1] < e) e = timerDeadline[1];
        nextEvent = e;
    }

    // =================================================================================================================
    //  The ports
    // =================================================================================================================

    /// <summary>A write to one of the four ports: 0 address (part 1), 1 data (part 1), 2 address (part 2), 3 data (part 2).</summary>
    public void Write(int port, byte value, long chipClock)
    {
        RunTo(chipClock);
        now = clockReached;
        switch (port & 3)
        {
            case 0: address = value; break;
            case 2: address = 0x100 | value; break;
            case 1:
                if ((address & 0x100) != 0) return;                       // data for a part-2 address is ignored on this port
                if (address == 0x2A) { dacData = (dacData & ~0x1FE) | ((value ^ 0x80) << 1); NoteDac(); }
                else if (address == 0x2B) dacEnable = (value & 0x80) != 0;
                else if (address == 0x2C) dacData = (dacData & ~1) | ((value >> 3) & 1);
                else WriteRegister(address, value);
                DataWrites++; busyStart = now + (SlotPipeline ? BusyLatency : 0); busyEnd = busyStart + BusyClocks;
                break;
            case 3:
                if ((address & 0x100) == 0) return;
                WriteRegister(address, value);
                DataWrites++; busyStart = now + (SlotPipeline ? BusyLatency : 0); busyEnd = busyStart + BusyClocks;
                break;
        }
    }

    private byte lastStatusRead;

    /// <summary>The 68000 or Z80 reads a port. Port 0 is the status register. The integrated chips (<see cref="StatusOnAnyPort"/>) answer every port with it; the discrete chip answers ports 1 to 3 with
    /// whatever its status register last read (nothing before the first read: 0), which is what Nuked-OPN2 does in its YM2612 mode.</summary>
    public byte Read(int port, long chipClock)
    {
        RunTo(chipClock);
        if ((port & 3) != 0 && !StatusOnAnyPort) return lastStatusRead;
        return lastStatusRead = ReadStatus();
    }

    /// <summary>The status register at the clock the chip has been run to: bit 7 busy, bit 1 timer B overflow, bit 0 timer A overflow.</summary>
    public byte ReadStatus() => (byte)((clockReached >= busyStart && clockReached < busyEnd ? 0x80 : 0) | (status & 3));

    /// <summary>A register as last written (the frequency-high registers read as latched).</summary>
    public byte Register(int index) => regs[index & 0x1FF];

    public bool DacEnabled => dacEnable;
    public int DacValue => (short)(dacData << 7) >> 7;

    // ---- test hooks (the oracle comparisons read the engine's internals) ----
    /// <summary>When set (24 entries), every sample stores each operator's 14-bit output there, index <c>channel * 4 + n</c> with n = 0..3 operator 1 to 4; a panned-off channel stores operator 1 only.</summary>
    public int[]? DebugOpOutputs { get; set; }
    /// <summary>When set (6 entries), every sample stores each channel's 9-bit output before panning (0 for a channel that is silent or runs the DAC); the slot-pipeline path only.</summary>
    public int[]? DebugChannelOutputs { get; set; }
    /// <summary>An operator's envelope attenuation (0 loudest, 0x3FF silent) and its 20-bit phase accumulator.</summary>
    public int DebugEnvelope(int channel, int op) => ops[channel * 4 + op].EnvAtt;
    public uint DebugPhase(int channel, int op) => ops[channel * 4 + op].Phase & 0xFFFFF;
    public int DebugEnvelopeState(int channel, int op) => ops[channel * 4 + op].EnvState;

    // =================================================================================================================
    //  Register file (ymfm: opn_registers_base::write and fm_engine_base::write)
    // =================================================================================================================

    private void WriteRegister(int index, byte data)
    {
        if (index == RegMode) { ModeWrite(data); return; }
        modifiedChannels = AllChannels;

        if ((index & 0xF0) == 0xA0)
        {
            // frequency registers are latched pairs: the high byte (A4-A7, AC-AF) only latches, the low byte (A0-A3, A8-AB) applies the latch; B8/B9 hold the latches
            if ((index & 3) == 3) return;
            int latch = 0xB8 | ((index >> 3) & 1);
            if ((index & 4) != 0) regs[latch] = (byte)(data & 0x3F);
            else { regs[index] = data; regs[index | 4] = regs[latch]; }
            return;
        }
        if ((index & 0xF8) == 0xB8) return;                           // the internal latch registers

        regs[index] = data;
        if (index == 0x28)
        {
            int channel = data & 3;
            if (channel == 3) return;
            channel += ((data >> 2) & 1) * 3;
            int mask = (data >> 4) & 0xF;
            for (int n = 0; n < 4; n++)
            {
                var o = ops[channel * 4 + n];
                o.KeyonLive = (o.KeyonLive & ~(1 << KeyonNormal)) | (((mask >> n) & 1) << KeyonNormal);
            }
        }
    }

    private void ModeWrite(byte data)
    {
        modifiedChannels = AllChannels;
        regs[RegMode] = data;
        // reset timer flags, then (re)load the timers; timer B's first period is a little short because the x16 prescaler is free-running
        if ((data & 0x20) != 0) status &= unchecked((byte)~2);
        if ((data & 0x10) != 0) status &= unchecked((byte)~1);
        UpdateTimer(1, (data & 2) != 0, -(totalClocks & 15));
        UpdateTimer(0, (data & 1) != 0, 0, fromWrite: true);
    }

    private int TimerAValue => regs[0x24] << 2 | regs[0x25] & 3;

    /// <summary>Timer A counts from a fixed point of the chip's sample cycle (18 chip clocks into it, measured with Nuked-OPN2): a load written at any moment starts at the next such point, so its flag
    /// rises a whole number of samples later at that same phase. Timer B is not quantised that way (its period runs from the write itself, less the free-running prescaler's phase).</summary>
    private const int TimerALoadPhase = 18;

    private void UpdateTimer(int t, bool enable, int deltaPeriod, bool fromWrite = false)
    {
        if (enable && !timerRunning[t])
        {
            int period = t == 0 ? 1024 - TimerAValue : 16 * (256 - regs[0x26]);
            period += deltaPeriod;
            long start = now;
            if (t == 0 && fromWrite && SlotPipeline) start = now < TimerALoadPhase ? TimerALoadPhase : (now - TimerALoadPhase) / ClocksPerSample * ClocksPerSample + ClocksPerSample + TimerALoadPhase;
            timerDeadline[t] = start + (long)period * ClocksPerSample;
            timerRunning[t] = true;
        }
        else if (!enable) timerRunning[t] = false;
        RecomputeNextEvent();
    }

    private void TimerExpired(int t)
    {
        if (t == 0 && (regs[RegMode] & 4) != 0) status |= 1;
        else if (t == 1 && (regs[RegMode] & 8) != 0) status |= 2;

        // timer A in CSM mode keys on every operator of channel 3
        if (t == 0 && ((regs[RegMode] >> 6) & 3) == 2)
        {
            for (int n = 0; n < 4; n++) ops[2 * 4 + n].KeyonLive |= 1 << KeyonCsm;
            modifiedChannels |= 1 << 2;
        }
        timerRunning[t] = false;
        UpdateTimer(t, true, 0);
    }

    // =================================================================================================================
    //  Cached register data (ymfm: opn_registers_base::cache_operator_data and compute_phase_step)
    // =================================================================================================================

    private uint ChBlockFreq(int choffs) => (uint)((regs[0xA4 + choffs] & 0x3F) << 8 | regs[0xA0 + choffs]);

    private uint MultiBlockFreq(int num) => (uint)((regs[0xAC + num] & 0x3F) << 8 | regs[0xA8 + num]);

    private void CacheOperator(Op o)
    {
        int choffs = o.Choffs, opoffs = o.Opoffs;
        uint blockFreq = o.BlockFreq = ChBlockFreq(choffs);

        // channel 3's special mode gives three of its four operators a frequency of their own
        if ((regs[RegMode] >> 6 & 3) != 0 && choffs == 2)
        {
            if (opoffs == 2) blockFreq = o.BlockFreq = MultiBlockFreq(1);
            else if (opoffs == 10) blockFreq = o.BlockFreq = MultiBlockFreq(2);
            else if (opoffs == 6) blockFreq = o.BlockFreq = MultiBlockFreq(0);
        }

        // the 5-bit key code: the top four bits of block and fnum, and a magic bit from the next lower fnum bits
        uint keycode = ((blockFreq >> 10) & 0xF) << 1;
        keycode |= (0xFE80u >> (int)((blockFreq >> 7) & 0xF)) & 1;

        int detuneReg = regs[0x30 + opoffs] >> 4 & 7;
        int adjust = DetuneTable[keycode * 4 + (uint)(detuneReg & 3)];
        o.Detune = (detuneReg & 4) != 0 ? -adjust : adjust;

        int multi = regs[0x30 + opoffs] & 0xF;
        o.Multiple = multi == 0 ? 1u : (uint)multi * 2;               // an x.1 value: 0 means 0.5

        bool lfoOn = (regs[0x22] & 8) != 0;
        o.PmSens = regs[0xB4 + choffs] & 7;
        o.PhaseStep = !lfoOn || o.PmSens == 0 ? ComputePhaseStep(o, 0) : PhaseStepDynamic;

        o.TotalLevel = (uint)(regs[0x40 + opoffs] & 0x7F) << 3;

        // the 4-bit sustain level, but 15 means 31
        uint sl = (uint)(regs[0x80 + opoffs] >> 4 & 0xF);
        sl |= (sl + 1) & 0x10;
        o.EgSustainLevel = sl << 5;

        int ksr = regs[0x50 + opoffs] >> 6 & 3;
        uint ksrval = keycode >> (ksr ^ 3);
        o.Rate[EgAttack] = EffectiveRate((regs[0x50 + opoffs] & 0x1F) * 2, ksrval);
        o.Rate[EgDecay] = EffectiveRate((regs[0x60 + opoffs] & 0x1F) * 2, ksrval);
        o.Rate[EgSustain] = EffectiveRate((regs[0x70 + opoffs] & 0x1F) * 2, ksrval);
        o.Rate[EgRelease] = EffectiveRate((regs[0x80 + opoffs] & 0xF) * 4 + 2, ksrval);

        o.SsgEnable = (regs[0x90 + opoffs] & 8) != 0;
        o.SsgMode = regs[0x90 + opoffs] & 7;
        o.AmEnable = (regs[0x60 + opoffs] & 0x80) != 0;
    }

    private static int EffectiveRate(int raw, uint ksr) => raw == 0 ? 0 : (int)Math.Min((uint)raw + ksr, 63u);

    private uint ComputePhaseStep(Op o, int lfoRawPm)
    {
        uint fnum = (o.BlockFreq & 0x7FF) << 1;
        int pmSens = regs[0xB4 + o.Choffs] & 7;
        if (pmSens != 0)
        {
            fnum += (uint)LfoPmPhaseAdjustment((o.BlockFreq >> 4) & 0x7F, pmSens, lfoRawPm);
            fnum &= 0xFFF;
        }
        int block = (int)((o.BlockFreq >> 11) & 7);
        uint phaseStep = (fnum << block) >> 2;
        phaseStep += (uint)o.Detune;
        phaseStep &= 0x1FFFF;
        return (phaseStep * o.Multiple) >> 1;
    }

    private static int LfoPmPhaseAdjustment(uint fnumBits, int pmSensitivity, int lfoRawPm)
    {
        int absPm = lfoRawPm < 0 ? -lfoRawPm : lfoRawPm;
        uint shifts = LfoPmShifts[pmSensitivity * 8 + (absPm & 7)];
        int adjust = (int)((fnumBits >> (int)(shifts & 0xF)) + (fnumBits >> (int)((shifts >> 4) & 0xF)));
        if (pmSensitivity > 5) adjust <<= pmSensitivity - 5;
        adjust >>= 2;
        return lfoRawPm < 0 ? -adjust : adjust;
    }

    // =================================================================================================================
    //  The engine (ymfm: fm_engine_base::clock, fm_channel::clock, fm_operator)
    // =================================================================================================================

    private void ClockEngine()
    {
        totalClocks++;

        // anything written (or a key edge) is picked up here; also every 4096 samples to catch ending notes
        if (modifiedChannels != 0 || prepareCount++ >= 4096)
        {
            activeChannels = 0;
            for (int ch = 0; ch < Channels; ch++)
            {
                int active = 0;
                for (int n = 0; n < 4; n++) if (PrepareOperator(ops[ch * 4 + n])) active = 1;
                activeChannels |= active << ch;
            }
            modifiedChannels = prepareCount = 0;
            // ymfm leaves a CSM note on until something else makes it prepare again; the chip keys it on for one sample only
            modifiedChannels |= csmPulse; csmPulse = 0;
        }

        // the envelope counter advances by one a sample, skipping a value so its low two bits count 0, 1, 2 (a divide by three)
        uint envPrevious = envCounter;
        if (((++envCounter) & 3) == 3) envCounter += 1;

        int lfoRawPm = ClockLfo();
        DebugLfoPm = lfoRawPm;
        for (int k = lfoHistAm.Length - 1; k > 0; k--) { lfoHistAm[k] = lfoHistAm[k - 1]; lfoHistPm[k] = lfoHistPm[k - 1]; }
        lfoHistAm[0] = lfoAm; lfoHistPm[0] = lfoRawPm;
        lfoAmUsed = lfoAm;

        for (int ch = 0; ch < Channels; ch++)
        {
            feedback0[ch] = feedback1[ch];
            feedback1[ch] = feedbackIn[ch];
            for (int n = 0; n < 4; n++)
            {
                // the chip's envelope counter ticks between operator slot 21 and 22 of its 24-slot cycle: operator 4 of channels 5 and 6 see a tick one sample after the others
                uint env = SlotPipeline && n == 3 && ch >= 4 ? envPrevious : envCounter;
                int pm = SlotPipeline ? lfoHistPm[LfoAmDelay[ch + 6 * SlotOfOp[n]] + 1] : lfoRawPm;
                ClockOperator(ops[ch * 4 + n], env, pm);
            }
        }
    }

    private int csmPulse;           // channels whose CSM key-on was just consumed: they are prepared again next sample, which keys them off

    private bool PrepareOperator(Op o)
    {
        CacheOperator(o);
        ClockKeystate(o, o.KeyonLive != 0 ? 1 : 0);
        if (SlotPipeline && (o.KeyonLive & (1 << KeyonCsm)) != 0) csmPulse |= 1 << (o.Choffs % 3 + 3 * (o.Choffs >> 8));
        o.KeyonLive &= ~(1 << KeyonCsm);
        return o.EnvState != EgRelease || o.EnvAtt < EgQuiet;
    }

    private static readonly byte[] LfoMaxCount = { 109, 78, 72, 68, 63, 45, 9, 6 };

    private int ClockLfo()
    {
        if ((regs[0x22] & 8) == 0)
        {
            // LFO off: the counter sits at zero, where the AM value is 0x3F (some games depend on that attenuation)
            lfoCounter = SlotPipeline ? LfoLead : 0;
            lfoAm = 0x3F;
            return 0;
        }
        uint subcount = (byte)lfoCounter++;
        if (subcount >= LfoMaxCount[regs[0x22] & 7])
            lfoCounter += 0x101 - subcount;       // the off-by-one of the real chip: the fastest rate is faster than the manual says

        lfoAm = (int)((lfoCounter >> 8) & 0x3F);
        if (((lfoCounter >> 14) & 1) == 0) lfoAm ^= 0x3F;

        int pm = (int)((lfoCounter >> 10) & 7);
        if (((lfoCounter >> 13) & 1) != 0) pm ^= 7;
        return ((lfoCounter >> 14) & 1) != 0 ? -pm : pm;
    }

    private void ClockOperator(Op o, uint env, int lfoRawPm)
    {
        if (o.SsgEnable) ClockSsgEgState(o); else o.SsgInverted = 0;
        if ((env & 3) == 0) ClockEnvelope(o, env >> 2);
        uint step = o.PhaseStep;
        if (step == PhaseStepDynamic) step = ComputePhaseStep(o, lfoRawPm);
        o.Phase += step;
    }

    private void StartAttack(Op o, bool isRestart)
    {
        if (o.EnvState == EgAttack) return;
        o.EnvState = EgAttack;
        // not inverted at the start unless SSG-EG is on in one of the inverted modes; a restart leaves it to the SSG-EG code
        if (!isRestart) o.SsgInverted = (o.SsgEnable ? 1 : 0) & (o.SsgMode >> 2 & 1);
        if (!isRestart) o.Phase = 0;
        if (o.Rate[EgAttack] >= 62) o.EnvAtt = 0;
    }

    private static void StartRelease(Op o)
    {
        if (o.EnvState >= EgRelease) return;
        o.EnvState = EgRelease;
        if (o.SsgInverted != 0)
        {
            o.EnvAtt = (0x200 - o.EnvAtt) & 0x3FF;
            o.SsgInverted = 0;
        }
    }

    private void ClockKeystate(Op o, int keystate)
    {
        if ((keystate ^ o.KeyState) == 0) return;
        o.KeyState = keystate;
        if (keystate != 0) StartAttack(o, false); else StartRelease(o);
    }

    private void ClockSsgEgState(Op o)
    {
        // work happens only once the attenuation crosses 0x200
        if ((o.EnvAtt & 0x200) == 0) return;
        int mode = o.SsgMode;
        if ((mode & 1) != 0)
        {
            // hold modes (1, 3, 5, 7): the inverted flag takes the end state (0 for modes 1 and 7, 1 for 3 and 5)
            o.SsgInverted = (mode >> 2 ^ mode >> 1) & 1;
            if (o.EnvState != EgAttack) o.EnvAtt = o.SsgInverted != 0 ? 0x200 : 0x3FF;
        }
        else
        {
            // continuous modes (0, 2, 4, 6): the alternating ones toggle the inversion (even in attack), decay and sustain start the attack again, modes 0 and 4 reset the phase
            o.SsgInverted ^= mode >> 1 & 1;
            if (o.EnvState == EgDecay || o.EnvState == EgSustain) StartAttack(o, true);
            if ((mode >> 1 & 1) == 0) o.Phase = 0;
        }
        if (o.EnvState == EgRelease) o.EnvAtt = 0x3FF;
    }

    private void ClockEnvelope(Op o, uint env)
    {
        int state = o.EnvState, nextState = state;
        if (SlotPipeline)
        {
            // the end of the attack is noticed but acted on one envelope tick later (the tick that sees the level at 0 still steps with the attack rate, which does nothing); decay into sustain is immediate
            if (state == EgAttack && o.EnvAtt == 0) { if (o.SsgEnable) state = nextState = EgDecay; else nextState = EgDecay; }
            else if (state == EgDecay && (uint)o.EnvAtt >= o.EgSustainLevel) state = nextState = EgSustain;
        }
        else
        {
            if (o.EnvState == EgAttack && o.EnvAtt == 0) o.EnvState = EgDecay;
            // decay to sustain right after, in case the sustain level is 0 (the cymbals of Shinobi's sound test depend on it, says ymfm)
            if (o.EnvState == EgDecay && (uint)o.EnvAtt >= o.EgSustainLevel) o.EnvState = EgSustain;
            state = nextState = o.EnvState;
        }

        int rate = o.Rate[state];
        int rateShift = rate >> 2;
        uint orig = env;
        env <<= rateShift;
        if ((env & 0x7FF) != 0) { o.EnvState = nextState; return; }   // the fractional part must be zero for a tick

        uint relevant = (env >> (rateShift <= 11 ? 11 : rateShift)) & 7;
        if (SlotPipeline && rateShift >= 12) relevant = (orig + FastRatePhase[rate & 3]) & 7;
        int increment = (int)((IncrementTable[rate] >> (int)(4 * relevant)) & 0xF);

        if (state == EgAttack)
        {
            // an attack rate of 62 or 63 does not increment once past the key-on (handled in StartAttack); at level 0 the attack is over (and the pipeline never steps below it)
            if (rate < 62 && (o.EnvAtt != 0 || !SlotPipeline)) o.EnvAtt = (o.EnvAtt + ((~o.EnvAtt * increment) >> 4)) & 0xFFFF;
        }
        else
        {
            if (!o.SsgEnable) o.EnvAtt += increment;
            else if (o.EnvAtt < 0x200) o.EnvAtt += 4 * increment;     // SSG-EG counts at four times the rate, below the mid-point only
            if (o.EnvAtt >= 0x400) o.EnvAtt = 0x3FF;
        }
        o.EnvState = nextState;
    }

    private static uint AttenuationToVolume(uint input) => (uint)PowerTable[input & 0xFF] >> (int)(input >> 8);

    private int ComputeVolume(Op o, uint phase, uint amOffset)
    {
        if (o.EnvAtt > EgQuiet) return 0;
        uint sinAttenuation = Waveform[phase & 0x3FF];

        uint result = (uint)o.EnvAtt;
        if (o.SsgInverted != 0) result = (0x200 - result) & 0x3FF;
        if (o.AmEnable) result += amOffset;
        result += o.TotalLevel;
        if (result > 0x3FF) result = 0x3FF;

        int volume = (int)AttenuationToVolume((sinAttenuation & 0x7FFF) + (result << 2));
        return (sinAttenuation & 0x8000) != 0 ? -volume : volume;
    }

    private uint LfoAmOffset(int choffs, int am = -1)
    {
        // AM sensitivity shifts: 0 -> 7, 1 -> 3, 2 -> 1, 3 -> 0 (silent, 1.4, 5.9 and 11.8 dB on paper)
        int amShift = (1 << ((regs[0xB4 + choffs] >> 4 & 3) ^ 3)) - 1;
        return (uint)(((am < 0 ? lfoAmUsed : am) << 1) >> amShift);
    }

    // The eight algorithms as one word each (ymfm's s_algorithm_ops): bit 0 operator 2's input, bits 1-3 operator 3's, bits 4-6 operator 4's, bits 7-9 which of operators 1-3 join the output sum.
    private static int Algorithm(int op2in, int op3in, int op4in, int op1out, int op2out, int op3out) => op2in | op3in << 1 | op4in << 4 | op1out << 7 | op2out << 8 | op3out << 9;
    private static readonly int[] AlgorithmOps =
    {
        Algorithm(1, 2, 3, 0, 0, 0),    // 0: O1 -> O2 -> O3 -> O4 -> out
        Algorithm(0, 5, 3, 0, 0, 0),    // 1: (O1 + O2) -> O3 -> O4 -> out
        Algorithm(0, 2, 6, 0, 0, 0),    // 2: (O1 + (O2 -> O3)) -> O4 -> out
        Algorithm(1, 0, 7, 0, 0, 0),    // 3: ((O1 -> O2) + O3) -> O4 -> out
        Algorithm(1, 0, 3, 0, 1, 0),    // 4: ((O1 -> O2) + (O3 -> O4)) -> out
        Algorithm(1, 1, 1, 0, 1, 1),    // 5: (O1 -> O2, O3, O4) -> out
        Algorithm(1, 0, 0, 0, 1, 1),    // 6: ((O1 -> O2) + O3 + O4) -> out
        Algorithm(0, 0, 0, 1, 1, 1),    // 7: (O1 + O2 + O3 + O4) -> out
    };

    private readonly int[] opout = new int[8];

    // ---- the slot pipeline: the delays between operators that the chip has and ymfm does not ----
    // The chip computes its 24 operators one per clock in the order operator 1, 3, 2, 4 of channels 1 to 6, so an operator that feeds another computed earlier in the cycle reaches it a sample
    // late. Measured against Nuked-OPN2 (the modulation inputs and channel sums match it exactly for every algorithm): operator 1's output is one sample late everywhere and has a second
    // delay register, operator 2 has one delay register, and the carriers are added in the order 1, 3, 2, 4 with a saturating add to 9 bits after each. ymfm computes the four operators of a
    // channel at one instant instead (and adds in the order 4, 1, 2, 3 with a clip of 257), so with this off the chip is exactly ymfm; with it on it is exactly Nuked in steady state.
    private readonly int[] hist1 = new int[Channels * 3];      // operator 1's output one, two and three samples ago, per channel
    private readonly int[] hist2 = new int[Channels];          // operator 2's output one sample ago

    /// <summary>True (the default): model the slot pipeline's delays (see the source). False: ymfm's arithmetic exactly, which the port is verified against.</summary>
    public bool SlotPipeline { get; set; } = true;

    private void SilenceChannel(int ch)
    {
        hist1[ch * 3 + 2] = hist1[ch * 3 + 1]; hist1[ch * 3 + 1] = hist1[ch * 3]; hist1[ch * 3] = 0;
        hist2[ch] = 0;
    }

    /// <summary>One channel's 9-bit output value in the slot-pipeline model (before panning).</summary>
    private int ChannelValuePipelined(int ch)
    {
        var o1 = ops[ch * 4]; var o2 = ops[ch * 4 + 1]; var o3 = ops[ch * 4 + 2]; var o4 = ops[ch * 4 + 3];
        int choffs = o1.Choffs;
        uint am1 = LfoAmOffset(choffs, lfoHistAm[LfoAmDelay[ch]]), am3 = LfoAmOffset(choffs, lfoHistAm[LfoAmDelay[ch + 6]]);
        uint am2 = LfoAmOffset(choffs, lfoHistAm[LfoAmDelay[ch + 12]]), am4 = LfoAmOffset(choffs, lfoHistAm[LfoAmDelay[ch + 18]]);
        int h = ch * 3;
        int a1 = hist1[h], a2 = hist1[h + 1], b1 = hist2[ch];

        int fb = regs[0xB0 + choffs] >> 3 & 7;
        int mod1 = fb != 0 ? (a1 + a2) >> (10 - fb) : 0;           // the feedback is operator 1's last two outputs, as in ymfm
        int v1 = (short)ComputeVolume(o1, (o1.Phase >> 10) + (uint)mod1, am1);

        // what feeds operators 2 and 3 (operator 4 is below: it also takes operator 3 of the same sample); a1, a2 are operator 1 one and two samples ago, b1 operator 2 one sample ago
        int alg = regs[0xB0 + choffs] & 7;
        int m2, m3, m4;
        switch (alg)
        {
            case 0: m2 = a1; m3 = b1; m4 = 0; break;
            case 1: m2 = 0; m3 = a2 + b1; m4 = 0; break;
            case 2: m2 = 0; m3 = b1; m4 = a1; break;
            case 3: m2 = a1; m3 = 0; m4 = b1; break;
            case 4: m2 = a1; m3 = 0; m4 = 0; break;
            case 5: m2 = a1; m3 = a2; m4 = a1; break;
            case 6: m2 = a1; m3 = 0; m4 = 0; break;
            default: m2 = 0; m3 = 0; m4 = 0; break;
        }
        int v2 = (short)ComputeVolume(o2, (o2.Phase >> 10) + (uint)(m2 >> 1), am2);
        int v3 = (short)ComputeVolume(o3, (o3.Phase >> 10) + (uint)(m3 >> 1), am3);
        // operator 4 takes operator 3 of the same sample in algorithms 0 to 4 (plus operator 1 or 2 of the sample before in 2 and 3); in 5 only operator 1; in 6 and 7 nothing
        int m4sum = alg <= 4 ? m4 + v3 : m4;
        int v4 = ComputeVolume(o4, (o4.Phase >> 10) + (uint)(m4sum >> 1), am4);
        var trace = DebugOpOutputs;
        if (trace != null) { trace[ch * 4] = v1; trace[ch * 4 + 1] = v2; trace[ch * 4 + 2] = v3; trace[ch * 4 + 3] = v4; }

        int acc = 0;
        if (alg == 7) acc = Clamp(acc + (a1 >> 5), -256, 255);
        if (alg >= 5) acc = Clamp(acc + (v3 >> 5), -256, 255);
        if (alg >= 4) acc = Clamp(acc + (v2 >> 5), -256, 255);
        acc = Clamp(acc + (v4 >> 5), -256, 255);

        hist1[h + 2] = hist1[h + 1]; hist1[h + 1] = a1; hist1[h] = v1;
        hist2[ch] = v2;
        if (DebugChannelOutputs != null) DebugChannelOutputs[ch] = acc;
        return acc;
    }

    /// <summary>One channel's output (ymfm's <c>output_4op</c> with a right shift of 5 and a clip of 256): the sum goes to the left and right accumulators the channel's pan bits select.</summary>
    private void ChannelOutput(int ch, ref int left, ref int right)
    {
        var o1 = ops[ch * 4]; var o2 = ops[ch * 4 + 1]; var o3 = ops[ch * 4 + 2]; var o4 = ops[ch * 4 + 3];
        int choffs = o1.Choffs;
        uint am = LfoAmOffset(choffs);

        // operator 1 has optional self-feedback
        int opmod = 0;
        int fb = regs[0xB0 + choffs] >> 3 & 7;
        if (fb != 0) opmod = (feedback0[ch] + feedback1[ch]) >> (10 - fb);
        int op1value = feedbackIn[ch] = (short)ComputeVolume(o1, (o1.Phase >> 10) + (uint)opmod, am);

        int pan = regs[0xB4 + choffs] >> 6 & 3;
        var trace = DebugOpOutputs;
        if (trace != null) trace[ch * 4] = op1value;
        if (pan == 0) return;

        int algorithmOps = AlgorithmOps[regs[0xB0 + choffs] & 7];
        var op = opout;
        op[0] = 0;
        op[1] = (short)op1value;

        opmod = op[algorithmOps & 1] >> 1;
        op[2] = (short)ComputeVolume(o2, (o2.Phase >> 10) + (uint)opmod, am);
        op[5] = (short)(op[1] + op[2]);

        opmod = op[algorithmOps >> 1 & 7] >> 1;
        op[3] = (short)ComputeVolume(o3, (o3.Phase >> 10) + (uint)opmod, am);
        op[6] = (short)(op[1] + op[3]);
        op[7] = (short)(op[2] + op[3]);

        opmod = op[algorithmOps >> 4 & 7] >> 1;
        int result = ComputeVolume(o4, (o4.Phase >> 10) + (uint)opmod, am);
        if (trace != null) { trace[ch * 4 + 1] = op[2]; trace[ch * 4 + 2] = op[3]; trace[ch * 4 + 3] = result; }
        result >>= 5;

        if ((algorithmOps & 0x080) != 0) result = Clamp(result + (op[1] >> 5), -257, 256);
        if ((algorithmOps & 0x100) != 0) result = Clamp(result + (op[2] >> 5), -257, 256);
        if ((algorithmOps & 0x200) != 0) result = Clamp(result + (op[3] >> 5), -257, 256);

        if ((pan & 2) != 0) left += result;
        if ((pan & 1) != 0) right += result;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    // =================================================================================================================
    //  One sample (ymfm: ym2612::generate and ym3438::generate)
    // =================================================================================================================

    private void GenerateSample()
    {
        ClockEngine();

        int accL = 0, accR = 0;
        int lastFm = dacEnable ? 5 : 6;
        for (int ch = 0; ch < lastFm; ch++)
        {
            int tl = 0, tr = 0;
            if (DebugChannelOutputs != null) DebugChannelOutputs[ch] = 0;
            if (SlotPipeline)
            {
                int v = 0;
                if ((activeChannels >> ch & 1) != 0) v = ChannelValuePipelined(ch); else SilenceChannel(ch);
                if ((MuteMask >> ch & 1) != 0) v = 0;                           // a muted channel keeps running; only its output is dropped
                int cpan = regs[0xB4 + ops[ch * 4].Choffs] >> 6 & 3;
                tl = (cpan & 2) != 0 ? v : 0; tr = (cpan & 1) != 0 ? v : 0;
                if (Ladder)
                {
                    // the DAC's discontinuity follows the sign of the channel's value, whichever sides it is panned to (measured with Nuked-OPN2): a side the channel plays on sits at +4 or -3 around it,
                    // a side it is panned off from at +4 or -4 (the gated value keeps its sign bit)
                    accL += (cpan & 2) != 0 ? v + (v < 0 ? -3 : 4) : (v < 0 ? -4 : 4);
                    accR += (cpan & 1) != 0 ? v + (v < 0 ? -3 : 4) : (v < 0 ? -4 : 4);
                }
                else { accL += tl; accR += tr; }
                continue;
            }
            if ((activeChannels >> ch & 1) != 0) ChannelOutput(ch, ref tl, ref tr);
            if ((MuteMask >> ch & 1) != 0) { tl = 0; tr = 0; }                // a muted channel keeps running; only its output is dropped
            if (Ladder)
            {
                // the discrete chip's DAC is not linear around zero: positive values sit 4 high, negative ones 3 low (applied to every channel, silent ones included)
                accL += tl < 0 ? tl - 3 : tl + 4;
                accR += tr < 0 ? tr - 3 : tr + 4;
            }
            else { accL += tl; accR += tr; }
        }

        if (dacEnable)
        {
            int dac = (short)(dacData << 7) >> 7;                     // the 9-bit signed DAC value
            if ((MuteMask >> 6 & 1) != 0) dac = 0;
            int pan = regs[0x1B6] >> 6 & 3;                           // channel 6's pan bits steer the DAC
            if (Ladder)
            {
                int dv = dac < 0 ? dac - 3 : dac + 4;
                int off = SlotPipeline ? (dac < 0 ? -4 : 4) : 4;                    // pipeline model: the sign of the DAC value decides the offset on a side that is panned off too
                accL += (pan & 2) != 0 ? dv : off;
                accR += (pan & 1) != 0 ? dv : off;
            }
            else
            {
                if ((pan & 2) != 0) accL += dac;
                if ((pan & 1) != 0) accR += dac;
            }
        }

        // the real output is multiplexed, not mixed; ymfm averages the six channels. The discrete chip also scales by 64/65 for the discontinuity added above
        int left, right;
        if (Ladder) { left = accL * 128 * 64 / (6 * 65); right = accR * 128 * 64 / (6 * 65); }
        else { left = accL * 128 / 6; right = accR * 128 / 6; }
        LastLeft = left; LastRight = right;
        Sink?.Sample(left, right);
    }

    // =================================================================================================================
    //  What the chip is playing, for the NES hub and the plugin
    // =================================================================================================================

    /// <summary>
    /// Writes the chip's voices into <paramref name="voices"/> and returns how many (<see cref="VoiceCount"/> = 7): FM channel 1 to 6, then the DAC. Each FM voice is a pulse (50% duty) whose pitch is the
    /// loudest carrier operator's frequency (block, fnum, multiple and detune, no vibrato) and whose level is the carriers' current loudness (total level plus the envelope's attenuation, 0.09375 dB a
    /// step, summed over the carriers and capped at 1); a channel that is keyed off, panned to neither side, or on the DAC is silent. The DAC is a noise voice whose level follows the recent DAC swing
    /// (the largest distance from centre since the last call, as a fraction of full scale) and whose rate is a fixed 4 kHz: a sampled drum becomes a noise hit. FM timbre is not in the description
    /// (the plan accepts that); <see cref="Register"/> gives a bridge the algorithm, feedback and operator registers if it wants a patch.
    /// </summary>
    public int DescribeVoices(Span<HubVoice> voices)
    {
        double sampleRate = InputClockHz / ClocksPerSample;
        for (int ch = 0; ch < Channels; ch++)
        {
            var ph = ops[ch * 4];
            int choffs = ph.Choffs;
            int alg = regs[0xB0 + choffs] & 7;
            int pan = regs[0xB4 + choffs] >> 6 & 3;
            int carriers = alg switch { 7 => 0xF, 5 or 6 => 0xE, 4 => 0xA, _ => 0x8 };     // operator bits that reach the output
            double level = 0, bestAmp = 0, hz = 0;
            bool keyed = false;
            for (int n = 0; n < 4; n++)
            {
                var o = ops[ch * 4 + n];
                if (o.KeyState != 0) keyed = true;
                if ((carriers >> n & 1) == 0 || o.KeyState == 0) continue;
                // the cache is current: every register write marks the channels for the next prepare; descriptions between a write and its prepare see the previous patch, a sample late at most
                int att = Math.Min(o.EnvAtt + (int)o.TotalLevel, 0x3FF);
                double amp = Math.Pow(10.0, -att * 0.09375 / 20.0);
                level += amp;
                if (amp > bestAmp)
                {
                    bestAmp = amp;
                    uint step = o.PhaseStep == PhaseStepDynamic ? ComputePhaseStep(o, 0) : o.PhaseStep;
                    hz = step * sampleRate / 1048576.0;
                }
            }
            bool silent = !keyed || pan == 0 || (ch == 5 && dacEnable) || (MuteMask >> ch & 1) != 0;
            voices[ch] = new HubVoice(HubVoiceKind.Pulse, silent ? 0 : hz, silent ? 0f : (float)Math.Min(1.0, level), Duty: 2);
        }
        float dac = dacEnable && (MuteMask >> 6 & 1) == 0 ? dacSwing : 0f;
        dacSwing = 0f;
        voices[Channels] = new HubVoice(HubVoiceKind.Noise, 4000.0, dac, ShortNoise: false);
        return VoiceCount;
    }

    private float dacSwing;

    /// <summary>Tracks the DAC's distance from centre between voice descriptions (called on every DAC data write); kept out of the sample loop.</summary>
    private void NoteDac()
    {
        float swing = Math.Abs((short)(dacData << 7) >> 7) / 256f;
        if (swing > dacSwing) dacSwing = swing;
    }

    // =================================================================================================================
    //  State
    // =================================================================================================================

    public void SaveState(BinaryWriter w)
    {
        w.Write(regs);
        foreach (var o in ops)
        {
            w.Write(o.Phase); w.Write(o.EnvAtt); w.Write(o.EnvState); w.Write(o.SsgInverted); w.Write(o.KeyState); w.Write(o.KeyonLive);
        }
        for (int i = 0; i < Channels; i++) { w.Write(feedback0[i]); w.Write(feedback1[i]); w.Write(feedbackIn[i]); }
        foreach (int v in hist1) w.Write(v);
        foreach (int v in hist2) w.Write(v);
        w.Write(address); w.Write(dacData); w.Write(dacEnable); w.Write(envCounter); w.Write(status); w.Write(totalClocks);
        w.Write(activeChannels); w.Write(prepareCount); w.Write(lfoCounter); w.Write(lfoAm);
        for (int t = 0; t < 2; t++) { w.Write(timerRunning[t]); w.Write(timerDeadline[t]); }
        w.Write(busyStart); w.Write(busyEnd); w.Write(lastStatusRead); w.Write(sampleCount); w.Write(clockReached); w.Write(now);
        w.Write(LastLeft); w.Write(LastRight); w.Write(MuteMask);
    }

    public void LoadState(BinaryReader r)
    {
        r.Read(regs, 0, regs.Length);
        foreach (var o in ops)
        {
            o.Phase = r.ReadUInt32(); o.EnvAtt = r.ReadInt32(); o.EnvState = r.ReadInt32(); o.SsgInverted = r.ReadInt32(); o.KeyState = r.ReadInt32(); o.KeyonLive = r.ReadInt32();
        }
        for (int i = 0; i < Channels; i++) { feedback0[i] = r.ReadInt16(); feedback1[i] = r.ReadInt16(); feedbackIn[i] = r.ReadInt16(); }
        for (int i = 0; i < hist1.Length; i++) hist1[i] = r.ReadInt32();
        for (int i = 0; i < hist2.Length; i++) hist2[i] = r.ReadInt32();
        address = r.ReadInt32(); dacData = r.ReadInt32(); dacEnable = r.ReadBoolean(); envCounter = r.ReadUInt32(); status = r.ReadByte(); totalClocks = r.ReadByte();
        activeChannels = r.ReadInt32(); prepareCount = r.ReadInt32(); lfoCounter = r.ReadUInt32(); lfoAm = r.ReadInt32();
        for (int t = 0; t < 2; t++) { timerRunning[t] = r.ReadBoolean(); timerDeadline[t] = r.ReadInt64(); }
        busyStart = r.ReadInt64(); busyEnd = r.ReadInt64(); lastStatusRead = r.ReadByte(); sampleCount = r.ReadInt64(); clockReached = r.ReadInt64(); now = r.ReadInt64();
        LastLeft = r.ReadInt32(); LastRight = r.ReadInt32(); MuteMask = r.ReadInt32();
        RecomputeNextEvent();
        modifiedChannels = AllChannels;       // the caches are rebuilt on the next sample (ymfm's invalidate_caches)
    }

    public string Describe()
    {
        int keyed = 0;
        for (int ch = 0; ch < Channels; ch++) for (int n = 0; n < 4; n++) if (ops[ch * 4 + n].KeyState != 0) { keyed |= 1 << ch; break; }
        return $"{(Ladder ? "YM2612" : "YM3438")} samples={sampleCount} keyed=0b{Convert.ToString(keyed, 2).PadLeft(6, '0')} dac={(dacEnable ? "on" : "off")} status=${ReadStatus():X2} timers={(timerRunning[0] ? "A" : "-")}{(timerRunning[1] ? "B" : "-")}";
    }
}
