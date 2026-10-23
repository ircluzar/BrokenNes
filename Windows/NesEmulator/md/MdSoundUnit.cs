using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>
/// The Genesis sound subsystem: the Z80 and its 8 KB of RAM, the bank register and the window into 68000 space, the PSG, and the YM2612 (<see cref="Ym2612"/>: six FM channels, the DAC, the LFO, the
/// timers and the busy flag) on the Z80's bus at $4000-$4003 and the 68000's at $A04000. The unit runs by the master clock, mixes FM, DAC and PSG into stereo at the host's rate and answers
/// <see cref="IHubVoiceSource.DescribeVoices"/> with eleven voices for the NES bridges.
/// </summary>
/// <remarks>
/// <para><b>Bus handshake.</b> The 68000 requests the bus ($A11100); the Z80 grants it at the end of its current instruction (the core reports only instruction boundaries, so the grant can be a few
/// T-states later than the chip's end-of-machine-cycle grant), and <see cref="IsBusGranted"/> turns true at that time. While the bus is held the Z80 does not run. RESET ($A11200) holds the Z80 and
/// resets the YM2612 (its registers, not its sample clock); a held reset also lets the 68000 reach the Z80's RAM. The 68000's window works only while it holds the bus or reset is asserted; otherwise it reads $FF.</para>
/// <para><b>The Z80's map.</b> $0000-$3FFF RAM (8 KB, mirrored), $4000-$5FFF YM2612 (four ports repeating), $6000-$60FF the bank register (a write shifts one bit in, LSB first, nine bits),
/// $7F00-$7FFF the VDP window (the PSG at $7F11 and its mirrors), $8000-$FFFF a 32 KB window into 68000 space at <c>bank &lt;&lt; 15</c>. Each bank-window access stalls the Z80 for
/// <see cref="BankWindowZ80Stall"/> T-states and the 68000 for <see cref="BankWindow68kStall"/> clocks.</para>
/// <para><b>Time.</b> Every access carries the master clock it happens at; the YM2612 is run to that clock first (its chip clock is master / 7), so a Z80 driver that writes the DAC at a steady rate
/// plays it at the pitch the real machine would, bank-window stalls included. The Z80 is stepped one T-state per 15 master clocks; the PSG one tick per 16 Z80 clocks.</para>
/// <para><b>Mixing.</b> The FM chip delivers one stereo sample per 1008 master clocks (53,267 Hz NTSC). Each sample is a step of a held level (the chip's output is sample-and-hold), added into a
/// band-limited mixer (<see cref="BandLimitedMixer"/>, the PSG's own resampler) that integrates it at the host rate, so the conversion to 44.1 or 48 kHz has no aliasing. The PSG and the FM are summed
/// with <see cref="FmGain"/> and <see cref="Sn76489.Gain"/>, then clipped to 16 bits. No allocation per sample.</para>
/// </remarks>
public partial class MdSoundUnit : IGenesisSound, IHubVoiceSource, IYm2612Sink, IVoiceStems
{
    /// <summary>Z80 T-states a bank-window access stalls the Z80 for (about 3.3; the sources disagree, the plan lists them).</summary>
    public const double BankWindowZ80Stall = 3.3;
    /// <summary>68000 clocks a Z80 bank-window access stalls the 68000 for (the newer test-ROM measurement says 9.7; others say 11).</summary>
    public const double BankWindow68kStall = 9.7;
    /// <summary>How long the YM2612's busy flag stays up after a data write, in master clocks (192 chip clocks of master / 7).</summary>
    public const long YmBusyMasterClocks = Ym2612.BusyClocks * 7;
    /// <summary>Master clocks per YM2612 clock.</summary>
    public const int MasterPerYmClock = SegaTiming.GenesisYm2612Divider;

    /// <summary>Voices <see cref="DescribeVoices"/> reports: FM 1 to 6, the DAC, PSG tones 1 to 3 and the noise.</summary>
    public const int VoiceCount = Ym2612.VoiceCount + 4;

    private readonly SegaMachine machine;
    private readonly Sn76489 psg;
    private readonly Ym2612 ym;
    private readonly BandLimitedMixer fmLeft, fmRight;     // the FM stream, 53 kHz in, the host rate out
    private readonly byte[] ram = new byte[0x2000];
    /// <summary>The Z80's 8 KB of sound RAM (the sound driver's code and data), for tools that read or write the machine's memory (the corruption domains).</summary>
    public byte[] Z80Ram => ram;
    private readonly Z80<ZBus> z80;
    private IGenesisSoundHost? host;

    private bool requested, inReset = true, granted;
    private long grantTime;
    private int bank;                      // the 9-bit bank register
    private long lastMaster;               // everything has been run up to here (the Z80 may be a few T-states ahead of it after a bus grant)
    /// <summary>The master clock the unit has been run up to (a unit that wraps this one and keeps a clock of its own takes it over after a state is loaded).</summary>
    public long Clock => lastMaster;
    private long z80Master;                // the master clock of the Z80's current T-state
    private int stallTicks;                // Z80 T-states still to burn for a bank-window stall
    private bool interrupt;
    private long fmClock;                  // FM samples handed to the mixers so far (monotonic across resets)
    private float lastFmL, lastFmR;
    private int ymAddress0, ymAddress1;    // the address latches, for the write trace
    // the voices on their own (the plugin's stems): the PSG's four from the chip, the FM chip's six and the DAC from one mono band-limited stream each, run beside the mix and read in step with it
    private long stemMask;
    private int fmStemBits;
    private readonly BandLimitedMixer?[] fmStem = new BandLimitedMixer?[7];
    private readonly float[] fmStemLast = new float[7];
    private readonly StemQueue?[] stemQ = new StemQueue?[SegaStems.MdCount];
    public long BusRequests, BankReads, BankWrites, PsgWrites, YmWrites, Z80Ticks;

    /// <summary>The unit's own writes to the YM2612, in order, for tests and tracing: (register, value, 0 or 1 for the part).</summary>
    public Action<int, byte, int>? YmWriteHook;
    /// <summary>Called for every PSG write (a trace, or tests).</summary>
    public Action<byte>? PsgWriteHook;
    /// <summary>Called for every write to one of the YM2612's four ports with the master clock it lands at (a register log, a VGM recording): (master clock, port 0-3, value).</summary>
    public Action<long, int, byte>? YmPortTap;
    /// <summary>Called for every PSG write with the master clock it lands at: (master clock, value).</summary>
    public Action<long, byte>? PsgPortTap;
    /// <summary>Called for every byte the Z80 reads through its bank window (the cartridge address): the sound driver's music, instrument and sample data. The plugin's Instrument Runaway lab uses it to tell which cartridge bytes the sound driver actually reads; null (the default) costs a field check per bank read.</summary>
    public Action<int>? BankReadTap;

    /// <summary>Output gain of the FM chip's mix (before the 16-bit clip); 1 puts a full-scale FM channel at about 17% of full scale, as the chip's own 9-bit DAC range does.</summary>
    public float FmGain = 1.0f;

    public MdSoundUnit(SegaMachine machine)
    {
        this.machine = machine;
        var t = machine.Timeline;
        psg = new Sn76489(t.MasterHz / t.Psg.Divider, machine.SampleRate, PsgVariant.MegaDrive);
        ym = new Ym2612(machine.GenesisModel, t.MasterHz / MasterPerYmClock) { Sink = this };
        fmLeft = new BandLimitedMixer(t.Ym2612SampleRate, machine.SampleRate);
        fmRight = new BandLimitedMixer(t.Ym2612SampleRate, machine.SampleRate);
        z80 = new Z80<ZBus>(new ZBus { U = this });
    }

    public string CoreName => $"MD (Z80, PSG, {(ym.Ladder ? "YM2612" : "YM3438")} FM + DAC)";
    public int SampleRate => machine.SampleRate;
    public Z80<ZBus> Cpu { get { Unpark(); return z80; } }   // a CPU whose polling loop is being fast-forwarded is brought up to date before anything outside looks at it
    public Sn76489 Psg => psg;
    public Ym2612 Fm => ym;
    public int BankRegister => bank;
    public byte[] Ram { get { Unpark(); return ram; } }   // a tool that writes the Z80's RAM must find the CPU up to date

    /// <summary>The FM chip's channel mask (bit 0 to 5 FM 1 to 6, bit 6 the DAC): a silenced channel keeps running and only its output is dropped. For stems and for isolating a voice.</summary>
    public int FmMuteMask { get => ym.MuteMask; set => ym.MuteMask = value; }

    public void Attach(IGenesisSoundHost host) => this.host = host;

    public void Reset()
    {
        Array.Clear(ram);
        requested = false; inReset = true; granted = false; grantTime = 0; bank = 0; interrupt = false;
        lastMaster = 0; z80Master = 0; stallTicks = 0; ymAddress0 = ymAddress1 = 0;
        BusRequests = BankReads = BankWrites = PsgWrites = YmWrites = Z80Ticks = 0;
        psg.Reset();
        ym.PowerOn();
        fmLeft.Reset(); fmRight.Reset();
        lastFmL = lastFmR = 0;
        RealignStems();
        z80.Reset(); z80.Irq = false;
        ResetLoops();
    }

    // =================================================================================================================
    //  Stems
    // =================================================================================================================

    public int StemCount => SegaStems.MdCount;
    public string StemName(int stem) => stem < 4 ? SegaStems.PsgNames[stem] : stem == SegaStems.MdDac ? "DAC" : "FM " + (stem - SegaStems.MdFm1 + 1);

    /// <summary>Which stems are made: bits 0 to 3 the PSG's voices, 4 to 9 FM 1 to 6, 10 the DAC (see <see cref="SegaStems"/>).</summary>
    public long StemMask
    {
        get => stemMask;
        set
        {
            value &= (1L << SegaStems.MdCount) - 1;
            psg.VoiceStems = (int)(value & 15);
            int fmBits = (int)(value >> 4) & 0x7F;
            for (int s = 0; s < SegaStems.MdCount; s++)
                if ((value >> s & 1) != 0 && (stemMask >> s & 1) == 0)
                {
                    stemQ[s] ??= new StemQueue();
                    stemQ[s]!.Clear();
                    if (s >= 4)
                    {
                        int c = s - 4;
                        fmStem[c] ??= new BandLimitedMixer(machine.Timeline.Ym2612SampleRate, machine.SampleRate);
                        fmStem[c]!.CopyTimeFrom(fmLeft);
                        fmStemLast[c] = 0;
                    }
                }
            ym.StemLeft = fmBits != 0 ? ym.StemLeft ?? new int[7] : null;
            ym.StemRight = fmBits != 0 ? ym.StemRight ?? new int[7] : null;
            fmStemBits = fmBits;
            stemMask = value;
        }
    }

    public int ReadStem(int stem, Span<float> dest) => (uint)stem < (uint)stemQ.Length && stemQ[stem] != null ? stemQ[stem]!.Pop(dest) : 0;

    /// <summary>The main FM mixers were reset or loaded: the stems start again from their time.</summary>
    private void RealignStems()
    {
        for (int c = 0; c < 7; c++)
            if (fmStem[c] != null) { fmStem[c]!.CopyTimeFrom(fmLeft); fmStemLast[c] = 0; }
        for (int s = 0; s < stemQ.Length; s++) stemQ[s]?.Clear();
    }

    /// <summary>One sample's share of each wanted FM channel into its own stream (the same step of a held level the mix gets).</summary>
    private void StemSample()
    {
        var sl = ym.StemLeft!; var sr = ym.StemRight!;
        float k = (float)(ym.StemScale * FmGain / 32768.0);
        for (int c = 0; c < 7; c++)
        {
            if ((fmStemBits >> c & 1) == 0) continue;
            float m = (sl[c] + sr[c]) * 0.5f * k;
            if (m != fmStemLast[c]) { fmStem[c]!.Step(fmClock, m - fmStemLast[c]); fmStemLast[c] = m; }
        }
    }

    // =================================================================================================================
    //  Running
    // =================================================================================================================

    private bool running;      // the Z80's own bus accesses can reach the VDP, whose events call back into this unit: the inner call only sets lines, it does not run the Z80 again

    public void RunTo(long masterClock)
    {
        if (masterClock <= lastMaster || running) return;
        running = true;
        try { RunInner(masterClock); }
        finally { running = false; }
    }

    private void RunInner(long masterClock)
    {
        long from = lastMaster;
        psg.Run(machine.Timeline.Psg.Due(from, masterClock));
        if (!inReset && !(requested && granted))
        {
            int ticks = machine.Timeline.Z80.Due(from > z80Master ? from : z80Master, masterClock);
            z80Master = Math.Max(z80Master, from);
            if (parked) { ParkedAdvance(ticks); ticks = 0; }       // a polling loop that is being fast-forwarded: the T-states are counted (MdSoundUnitLoops.cs)
            for (int i = 0; i < ticks; i++)
            {
                if (stallTicks > 0) { stallTicks--; z80Master += machine.Timeline.Z80.Divider; continue; }
                z80.Tick();
                Z80Ticks++;
                z80Master += machine.Timeline.Z80.Divider;
                if (requested && z80.AtInstructionBoundary) { granted = true; grantTime = z80Master; break; }
                if (FastLoops && z80.AtInstructionBoundary && LoopStep()) { ParkedAdvance(ticks - i - 1); break; }
            }
        }
        else z80Master = masterClock;
        RunFm(masterClock);
        lastMaster = masterClock;
    }

    /// <summary>Brings the YM2612 to the master clock (its clock is master / 7) and tells the mixers how far the FM stream has got.</summary>
    private void RunFm(long masterClock)
    {
        ym.RunTo(masterClock / MasterPerYmClock);
        fmLeft.Advance(fmClock); fmRight.Advance(fmClock);
        if (fmStemBits != 0) for (int c = 0; c < 7; c++) if ((fmStemBits >> c & 1) != 0) fmStem[c]!.Advance(fmClock);
    }

    /// <summary>One YM2612 sample (the chip calls this as it completes each one): the new level is a step of the held output, added into the band-limited mixers.</summary>
    void IYm2612Sink.Sample(int left, int right)
    {
        float l = left * FmGain * (1f / 32768f), r = right * FmGain * (1f / 32768f);
        fmClock++;
        if (l != lastFmL) { fmLeft.Step(fmClock, l - lastFmL); lastFmL = l; }
        if (r != lastFmR) { fmRight.Step(fmClock, r - lastFmR); lastFmR = r; }
        if (fmStemBits != 0) StemSample();
    }

    // =================================================================================================================
    //  The bus handshake
    // =================================================================================================================

    public void WriteBusRequest(bool request, long masterClock)
    {
        RunTo(masterClock);
        if (request == requested) return;
        LoopBreak();
        requested = request;
        if (request)
        {
            BusRequests++;
            if (inReset) { granted = true; grantTime = masterClock; return; }
            // The Z80 lets go of the bus at the end of the machine cycle it is in (at most about six T-states: BUSREQ is sampled at the end of each cycle; an instruction has up to 23), not at the end of the instruction:
            // a game that starts its sound driver with "request the bus, release RESET, test BUSACK once" (Bugs Bunny in Double Trouble: its loop branches into the middle of the test if the first answer is "not yet")
            // relies on the grant being quick, and an instruction boundary can be more than the 300 master clocks the 68000 takes to get to the test. The core has no machine-cycle marker: it is stopped where it stands after
            // GrantLatencyTicks, which is a place between two micro-steps of the instruction, as the hardware's grant is between two cycles of it.
            granted = false;
            int guard = 0;
            while (!z80.AtInstructionBoundary && guard++ < GrantLatencyTicks)
            {
                if (stallTicks > 0) stallTicks--; else z80.Tick();
                z80Master += machine.Timeline.Z80.Divider;
            }
            granted = true; grantTime = Math.Max(z80Master, masterClock);
            if (z80Master > lastMaster) lastMaster = Math.Max(lastMaster, masterClock);
        }
        else
        {
            granted = false;
            if (z80Master < masterClock) z80Master = masterClock;      // the Z80 starts again now; the time the bus was held is lost to it
        }
    }

    /// <summary>The most Z80 T-states the unit lets pass between a BUSREQ and the grant when the Z80 is in the middle of an instruction (the end of the machine cycle it is in).</summary>
    public static int GrantLatencyTicks = 6;
    public bool IsBusGranted(long masterClock)
    {
        RunTo(masterClock);
        return requested && granted && masterClock >= grantTime;
    }

    public void WriteReset(bool released, long masterClock)
    {
        RunTo(masterClock);
        LoopBreak();
        bool wasInReset = inReset;
        inReset = !released;
        if (inReset)
        {
            // RESET is the YM2612's reset pin as well: its registers clear (its clocks keep running); the PSG, on the Z80 side of the bus, goes silent too
            z80.ResetPin();
            ym.ResetRegisters();
            ymAddress0 = ymAddress1 = 0;
            psg.Reset();
            stallTicks = 0;
        }
        else if (wasInReset)
        {
            z80.ResetPin();
            if (z80Master < masterClock) z80Master = masterClock;
        }
    }

    public bool IsInReset => inReset;

    // =================================================================================================================
    //  The 68000's window
    // =================================================================================================================

    private bool Reachable(long masterClock) => (requested && granted && masterClock >= grantTime) || inReset;

    public byte Read(int offset, long masterClock)
    {
        RunTo(masterClock);
        offset &= 0xFFFF;
        if (!Reachable(masterClock)) return 0xFF;
        return Z80Read(offset, masterClock, fromZ80: false);
    }

    public void Write(int offset, byte value, long masterClock)
    {
        RunTo(masterClock);
        offset &= 0xFFFF;
        if (!Reachable(masterClock)) return;
        Z80Write(offset, value, masterClock, fromZ80: false);
    }

    public void SetInterrupt(bool asserted, long masterClock)
    {
        RunTo(masterClock);
        LoopBreak();
        interrupt = asserted;
        z80.Irq = asserted;
    }

    public void WritePsg(byte value, long masterClock)
    {
        RunTo(masterClock);
        psg.Write(value);
        PsgWrites++;
        PsgWriteHook?.Invoke(value);
        PsgPortTap?.Invoke(masterClock, value);
    }

    /// <summary>A write straight to one of the YM2612's four ports (0 address, 1 data, 2 address of part 2, 3 data of part 2) at a master clock, as a VGM player or a tool does it: no Z80, no bus handshake.</summary>
    public void WriteYm(int port, byte value, long masterClock)
    {
        RunTo(masterClock);
        YmWrite(port, value, masterClock);
    }

    /// <summary>A read of a YM2612 port straight from the chip (the status register), at a master clock.</summary>
    public byte ReadYm(int port, long masterClock)
    {
        RunTo(masterClock);
        return ym.Read(port, masterClock / MasterPerYmClock);
    }

    // =================================================================================================================
    //  The Z80's map (used by the Z80 itself and by the 68000 through its window)
    // =================================================================================================================

    private byte Z80Read(int address, long master, bool fromZ80)
    {
        if (address < 0x4000) return ram[address & 0x1FFF];
        if (fromZ80) loopImpure = true;           // a read outside the Z80's own RAM (the YM2612's status, the bank window, the VDP) is not a read of something that only the 68000 can change
        if (address < 0x6000) return ym.Read(address & 3, master / MasterPerYmClock);
        if (address < 0x7F00) return 0xFF;
        if (address < 0x8000)
        {
            if (fromZ80 && host != null) { byte v = host.ReadBus(0xC00000 + (address & 0xFF), master, out _); return v; }
            return 0xFF;
        }
        if (!fromZ80 || host == null) return 0xFF;     // the 68000 cannot see the bank window from its own side of the bus
        BankReads++;
        uint a = (uint)(bank << 15 | address & 0x7FFF);
        BankReadTap?.Invoke((int)a);
        byte data = host.ReadBus((int)a, master, out int wait);
        stallTicks += (int)Math.Ceiling((wait > 0 ? wait : (long)(BankWindowZ80Stall * machine.Timeline.Z80.Divider)) / (double)machine.Timeline.Z80.Divider);
        return data;
    }

    private void Z80Write(int address, byte value, long master, bool fromZ80)
    {
        if (fromZ80) loopImpure = true;           // a write by the Z80 itself is a side effect: not a loop that can be skipped
        else if (address < 0x4000) { loopImpure = true; if (parked) Unpark(); }   // the 68000 is about to change the RAM a loop is reading: a turn being recorded is spoilt, a parked CPU is brought up to date first
        if (address < 0x4000) { ram[address & 0x1FFF] = value; return; }
        if (address < 0x6000) { YmWrite(address & 3, value, master); return; }
        if (address < 0x6100) { bank = bank >> 1 | (value & 1) << 8; return; }
        if (address < 0x7F00) return;
        if (address < 0x8000)
        {
            int reg = address & 0xFF;
            if (reg >= 0x11 && reg <= 0x17 && (reg & 1) != 0) { psg.Write(value); PsgWrites++; PsgWriteHook?.Invoke(value); PsgPortTap?.Invoke(master, value); return; }
            if (fromZ80 && host != null) host.WriteBus(0xC00000 + reg, value, master, out _);
            return;
        }
        if (!fromZ80 || host == null) return;
        BankWrites++;
        uint a = (uint)(bank << 15 | address & 0x7FFF);
        host.WriteBus((int)a, value, master, out int wait);
        stallTicks += (int)Math.Ceiling((wait > 0 ? wait : (long)(BankWindowZ80Stall * machine.Timeline.Z80.Divider)) / (double)machine.Timeline.Z80.Divider);
    }

    // =================================================================================================================
    //  The YM2612's ports
    // =================================================================================================================

    private void YmWrite(int port, byte value, long master)
    {
        ym.Write(port, value, master / MasterPerYmClock);
        YmPortTap?.Invoke(master, port & 3, value);
        switch (port)
        {
            case 0: ymAddress0 = value; break;
            case 2: ymAddress1 = value; break;
            case 1: YmWrites++; YmWriteHook?.Invoke(ymAddress0, value, 0); break;
            case 3: YmWrites++; YmWriteHook?.Invoke(ymAddress1, value, 1); break;
        }
    }

    // =================================================================================================================
    //  Z80 bus
    // =================================================================================================================

    public struct ZBus : IZ80Bus
    {
        public MdSoundUnit U;
        public byte FetchOpcode(ushort address) => U.Z80Read(address, U.z80Master, true);
        public byte Read(ushort address) => U.Z80Read(address, U.z80Master, true);
        public void Write(ushort address, byte value) => U.Z80Write(address, value, U.z80Master, true);
        public byte In(ushort port) { U.loopImpure = true; return 0xFF; }
        public void Out(ushort port, byte value) { U.loopImpure = true; }
        public byte InterruptVector() => 0xFF;
    }

    // =================================================================================================================
    //  Sound out, voices, state
    // =================================================================================================================

    /// <summary>Stereo 16-bit samples ready at the host rate (interleaved L, R): the FM and the PSG summed and clipped. Returns the number of shorts written (always even).</summary>
    public int ReadSamples(Span<short> dest)
    {
        const int Chunk = 256;
        int total = 0;
        Span<short> psgBuf = stackalloc short[Chunk * 2];
        Span<float> fl = stackalloc float[Chunk], fr = stackalloc float[Chunk];
        while (dest.Length - total >= 2)
        {
            int frames = Math.Min(Math.Min(Math.Min(fmLeft.Available, fmRight.Available), Chunk), (dest.Length - total) / 2);
            if (frames <= 0) break;
            // the PSG delivers however many frames it has finished, up to what we ask for; both streams are at the same host-rate sample, so ask for exactly `frames`
            int got = psg.ReadSamples(psgBuf[..(frames * 2)]) / 2;
            if (got < frames) frames = got;
            if (frames <= 0) break;
            fmLeft.Read(fl[..frames]); fmRight.Read(fr[..frames]);
            if (stemMask != 0)
            {
                // every wanted stem gets the same frames the mix does (the PSG's from its own queue, the FM chip's from their streams)
                Span<float> st = stackalloc float[Chunk];
                for (int s = 0; s < SegaStems.MdCount; s++)
                {
                    if ((stemMask >> s & 1) == 0) continue;
                    int k = s < 4 ? psg.ReadVoiceStem(s, st[..frames]) : fmStem[s - 4]!.Read(st[..frames]);
                    stemQ[s]!.Push(st[..k]);
                }
            }
            for (int i = 0; i < frames; i++)
            {
                int l = psgBuf[2 * i] + (int)(fl[i] * 32768f), r = psgBuf[2 * i + 1] + (int)(fr[i] * 32768f);
                dest[total + 2 * i] = (short)(l < -32768 ? -32768 : l > 32767 ? 32767 : l);
                dest[total + 2 * i + 1] = (short)(r < -32768 ? -32768 : r > 32767 ? 32767 : r);
            }
            total += frames * 2;
        }
        return total;
    }

    /// <summary>
    /// What the unit is playing as hub voices, in a stable order: FM 1 to 6 (pulses), the DAC (noise), PSG tones 1 and 2 (pulses), tone 3 (a triangle) and the PSG noise: 11 voices.
    /// See <see cref="Ym2612.DescribeVoices"/> and <see cref="Sn76489.DescribeVoices"/> for how each is derived.
    /// </summary>
    public int DescribeVoices(Span<HubVoice> voices)
    {
        int n = ym.DescribeVoices(voices);
        n += psg.DescribeVoices(voices[n..]);
        return n;
    }

    public void SaveState(BinaryWriter w)
    {
        Unpark();
        w.Write(ram); w.Write(requested); w.Write(inReset); w.Write(granted); w.Write(grantTime); w.Write(bank); w.Write(lastMaster); w.Write(z80Master); w.Write(stallTicks); w.Write(interrupt);
        w.Write(ymAddress0); w.Write(ymAddress1);
        ym.SaveState(w);
        psg.SaveState(w);
        z80.SaveState(w);
        // the audio path: the FM chip's output as the band-limited mixers hold it (a restored unit's next samples are those of the one that saved, bit for bit), the last levels, the counters
        fmLeft.SaveState(w); fmRight.SaveState(w);
        w.Write(fmClock); w.Write(lastFmL); w.Write(lastFmR);
        w.Write(BusRequests); w.Write(BankReads); w.Write(BankWrites); w.Write(PsgWrites); w.Write(YmWrites); w.Write(Z80Ticks);
    }

    public void LoadState(BinaryReader r)
    {
        r.Read(ram, 0, ram.Length);
        requested = r.ReadBoolean(); inReset = r.ReadBoolean(); granted = r.ReadBoolean(); grantTime = r.ReadInt64(); bank = r.ReadInt32(); lastMaster = r.ReadInt64(); z80Master = r.ReadInt64(); stallTicks = r.ReadInt32(); interrupt = r.ReadBoolean();
        ymAddress0 = r.ReadInt32(); ymAddress1 = r.ReadInt32();
        ym.LoadState(r);
        psg.LoadState(r);
        z80.LoadState(r);
        ResetLoops();       // (C4a) the loop detector starts again from the loaded Z80 state; SaveState settled any parked loop first
        fmLeft.LoadState(r); fmRight.LoadState(r);
        fmClock = r.ReadInt64(); lastFmL = r.ReadSingle(); lastFmR = r.ReadSingle();
        BusRequests = r.ReadInt64(); BankReads = r.ReadInt64(); BankWrites = r.ReadInt64(); PsgWrites = r.ReadInt64(); YmWrites = r.ReadInt64(); Z80Ticks = r.ReadInt64();
        // the plugin's stems (one stream per voice, beside the mix) start again from the mix's time
        RealignStems();
    }

    public string Describe() { Unpark(); return DescribeCore(); }

    private string DescribeCore() =>
        $"MD sound unit (Z80 + PSG + {ym.Describe()}): z80 pc={z80.PC:X4} {(inReset ? "RESET" : requested && granted ? "bus held" : requested ? "bus requested" : "running")} bank=${bank:X3} z80-int={interrupt} " +
        $"psg-writes={PsgWrites} ym-writes={YmWrites} bank-reads={BankReads} z80-ticks={Z80Ticks} (loops parked {LoopParks}x, {LoopParkedTicks} ticks counted)";
}
