using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>
/// The Genesis sound subsystem with a real Z80 in it: the Z80 and its 8 KB of RAM, the bank register and the window into 68000 space, the PSG, and the YM2612's register interface
/// (address and data ports, the status register's busy flag, timers A and B, the DAC registers) without its sound: <b>the FM chip makes no audio yet</b>, so a game's FM music is silent
/// while its Z80 driver, the PSG and the timers run for real. The Genesis track replaces this class's YM2612 part with the real chip; the contract (<see cref="IGenesisSound"/>) does not change.
/// </summary>
/// <remarks>
/// <para><b>Bus handshake.</b> The 68000 requests the bus ($A11100); the Z80 grants it at the end of its current instruction (the core reports only instruction boundaries, so the grant can be a few
/// T-states later than the chip's end-of-machine-cycle grant), and <see cref="IsBusGranted"/> turns true at that time. While the bus is held the Z80 does not run. RESET ($A11200) holds the Z80 and
/// the YM2612 reset; a held reset also lets the 68000 reach the Z80's RAM. The 68000's window works only while it holds the bus or reset is asserted; otherwise it reads $FF.</para>
/// <para><b>The Z80's map.</b> $0000-$3FFF RAM (8 KB, mirrored), $4000-$5FFF YM2612 (four ports repeating), $6000-$60FF the bank register (a write shifts one bit in, LSB first, nine bits),
/// $7F00-$7FFF the VDP window (the PSG at $7F11 and its mirrors), $8000-$FFFF a 32 KB window into 68000 space at <c>bank &lt;&lt; 15</c>. Each bank-window access stalls the Z80 for
/// <see cref="BankWindowZ80Stall"/> T-states and the 68000 for <see cref="BankWindow68kStall"/> clocks.</para>
/// </remarks>
public sealed class MdSoundUnit : IGenesisSound
{
    /// <summary>Z80 T-states a bank-window access stalls the Z80 for (about 3.3; the sources disagree, the plan lists them).</summary>
    public const double BankWindowZ80Stall = 3.3;
    /// <summary>68000 clocks a Z80 bank-window access stalls the 68000 for (the newer test-ROM measurement says 9.7; others say 11).</summary>
    public const double BankWindow68kStall = 9.7;
    /// <summary>How long the YM2612's busy flag stays up after a data write: 192 clocks of its 7.67 MHz input clock.</summary>
    public const long YmBusyMasterClocks = 192 * 7;

    private readonly SegaMachine machine;
    private readonly Sn76489 psg;
    private readonly byte[] ram = new byte[0x2000];
    private readonly Z80<ZBus> z80;
    private IGenesisSoundHost? host;

    private bool requested, inReset = true, granted;
    private long grantTime;
    private int bank;                      // the 9-bit bank register
    private long lastMaster;               // everything has been run up to here (the Z80 may be a few T-states ahead of it after a bus grant)
    private long z80Master;                // the master clock of the Z80's current T-state
    private int stallTicks;                // Z80 T-states still to burn for a bank-window stall
    private bool interrupt;
    public long BusRequests, BankReads, BankWrites, PsgWrites, YmWrites, Z80Ticks;

    // ---- YM2612 interface (no synthesis)
    private readonly byte[] ym = new byte[0x200];      // registers of part 1 ($000-$0FF) and part 2 ($100-$1FF)
    private int ymAddress0, ymAddress1;
    private long ymBusyUntil;
    private bool timerALoaded, timerBLoaded, timerAEnable, timerBEnable, timerAFlag, timerBFlag;
    private int timerACount, timerBCount;
    private long timerSample;              // YM samples (1008 master clocks each) run so far

    /// <summary>The unit's own writes to the YM2612, in order, for tests and tracing: (register, value, 0 or 1 for the part).</summary>
    public Action<int, byte, int>? YmWriteHook;
    /// <summary>Called for every PSG write (a trace, or tests).</summary>
    public Action<byte>? PsgWriteHook;

    public MdSoundUnit(SegaMachine machine)
    {
        this.machine = machine;
        psg = new Sn76489(machine.Timeline.MasterHz / machine.Timeline.Psg.Divider, machine.SampleRate, PsgVariant.MegaDrive);
        z80 = new Z80<ZBus>(new ZBus { U = this });
    }

    public string CoreName => "MD (Z80, PSG, YM2612 registers: no FM audio)";
    public int SampleRate => machine.SampleRate;
    public Z80<ZBus> Cpu => z80;
    public Sn76489 Psg => psg;
    public int BankRegister => bank;
    public byte[] Ram => ram;

    public void Attach(IGenesisSoundHost host) => this.host = host;

    public void Reset()
    {
        Array.Clear(ram); Array.Clear(ym);
        requested = false; inReset = true; granted = false; grantTime = 0; bank = 0; interrupt = false;
        lastMaster = 0; z80Master = 0; stallTicks = 0; ymAddress0 = ymAddress1 = 0; ymBusyUntil = 0;
        timerALoaded = timerBLoaded = timerAEnable = timerBEnable = timerAFlag = timerBFlag = false; timerACount = timerBCount = 0; timerSample = 0;
        BusRequests = BankReads = BankWrites = PsgWrites = YmWrites = Z80Ticks = 0;
        psg.Reset();
        z80.Reset(); z80.Irq = false;
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
        RunTimers(masterClock);
        if (!inReset && !(requested && granted))
        {
            int ticks = machine.Timeline.Z80.Due(from > z80Master ? from : z80Master, masterClock);
            z80Master = Math.Max(z80Master, from);
            for (int i = 0; i < ticks; i++)
            {
                if (stallTicks > 0) { stallTicks--; z80Master += machine.Timeline.Z80.Divider; continue; }
                z80.Tick();
                Z80Ticks++;
                z80Master += machine.Timeline.Z80.Divider;
                if (requested && z80.AtInstructionBoundary) { granted = true; grantTime = z80Master; break; }
            }
        }
        else z80Master = masterClock;
        lastMaster = masterClock;
    }

    private void RunTimers(long masterClock)
    {
        long target = masterClock / SegaTiming.GenesisYm2612MasterClocksPerSample;
        while (timerSample < target)
        {
            timerSample++;
            if (timerALoaded && ++timerACount >= 1024 - TimerAValue)
            {
                timerACount = 0;
                if (timerAEnable) timerAFlag = true;
            }
            if (timerBLoaded && ++timerBCount >= (256 - ym[0x26]) * 16)
            {
                timerBCount = 0;
                if (timerBEnable) timerBFlag = true;
            }
        }
    }

    private int TimerAValue => ym[0x24] << 2 | ym[0x25] & 3;

    // =================================================================================================================
    //  The bus handshake
    // =================================================================================================================

    public void WriteBusRequest(bool request, long masterClock)
    {
        RunTo(masterClock);
        if (request == requested) return;
        requested = request;
        if (request)
        {
            BusRequests++;
            if (inReset) { granted = true; grantTime = masterClock; return; }
            // the Z80 finishes the instruction it is in, then lets go of the bus
            granted = false;
            int guard = 0;
            while (!z80.AtInstructionBoundary && guard++ < 64)
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

    public bool IsBusGranted(long masterClock)
    {
        RunTo(masterClock);
        return requested && granted && masterClock >= grantTime;
    }

    public void WriteReset(bool released, long masterClock)
    {
        RunTo(masterClock);
        bool wasInReset = inReset;
        inReset = !released;
        if (inReset)
        {
            // RESET also resets the YM2612 and (through the Z80's own reset) the Z80 side of the PSG's input state
            z80.ResetPin();
            Array.Clear(ym); ymAddress0 = ymAddress1 = 0; timerALoaded = timerBLoaded = timerAEnable = timerBEnable = timerAFlag = timerBFlag = false;
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
        interrupt = asserted;
        z80.Irq = asserted;
    }

    public void WritePsg(byte value, long masterClock)
    {
        RunTo(masterClock);
        psg.Write(value);
        PsgWrites++;
        PsgWriteHook?.Invoke(value);
    }

    // =================================================================================================================
    //  The Z80's map (used by the Z80 itself and by the 68000 through its window)
    // =================================================================================================================

    private byte Z80Read(int address, long master, bool fromZ80)
    {
        if (address < 0x4000) return ram[address & 0x1FFF];
        if (address < 0x6000) return YmRead(address & 3, master);
        if (address < 0x7F00) return 0xFF;
        if (address < 0x8000)
        {
            if (fromZ80 && host != null) { byte v = host.ReadBus(0xC00000 + (address & 0xFF), master, out _); return v; }
            return 0xFF;
        }
        if (!fromZ80 || host == null) return 0xFF;     // the 68000 cannot see the bank window from its own side of the bus
        BankReads++;
        uint a = (uint)(bank << 15 | address & 0x7FFF);
        byte data = host.ReadBus((int)a, master, out int wait);
        stallTicks += (int)Math.Ceiling((wait > 0 ? wait : (long)(BankWindowZ80Stall * machine.Timeline.Z80.Divider)) / (double)machine.Timeline.Z80.Divider);
        return data;
    }

    private void Z80Write(int address, byte value, long master, bool fromZ80)
    {
        if (address < 0x4000) { ram[address & 0x1FFF] = value; return; }
        if (address < 0x6000) { YmWrite(address & 3, value, master); return; }
        if (address < 0x6100) { bank = bank >> 1 | (value & 1) << 8; return; }
        if (address < 0x7F00) return;
        if (address < 0x8000)
        {
            int reg = address & 0xFF;
            if (reg >= 0x11 && reg <= 0x17 && (reg & 1) != 0) { psg.Write(value); PsgWrites++; PsgWriteHook?.Invoke(value); return; }
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
    //  The YM2612's registers
    // =================================================================================================================

    private byte YmRead(int port, long master)
    {
        // every port reads the status register: busy (bit 7), timer B (bit 1), timer A (bit 0)
        int s = (master < ymBusyUntil ? 0x80 : 0) | (timerBFlag ? 2 : 0) | (timerAFlag ? 1 : 0);
        return (byte)s;
    }

    private void YmWrite(int port, byte value, long master)
    {
        switch (port)
        {
            case 0: ymAddress0 = value; break;
            case 2: ymAddress1 = value; break;
            case 1: YmData(0, ymAddress0, value, master); break;
            case 3: YmData(1, ymAddress1, value, master); break;
        }
    }

    private void YmData(int part, int reg, byte value, long master)
    {
        YmWrites++;
        ymBusyUntil = master + YmBusyMasterClocks;
        ym[part << 8 | reg & 0xFF] = value;
        YmWriteHook?.Invoke(reg, value, part);
        if (part != 0) return;
        switch (reg)
        {
            case 0x27:
                if ((value & 0x01) != 0 && !timerALoaded) timerACount = 0;
                if ((value & 0x02) != 0 && !timerBLoaded) timerBCount = 0;
                timerALoaded = (value & 0x01) != 0; timerBLoaded = (value & 0x02) != 0;
                timerAEnable = (value & 0x04) != 0; timerBEnable = (value & 0x08) != 0;
                if ((value & 0x10) != 0) timerAFlag = false;
                if ((value & 0x20) != 0) timerBFlag = false;
                break;
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
        public byte In(ushort port) => 0xFF;
        public void Out(ushort port, byte value) { }
        public byte InterruptVector() => 0xFF;
    }

    // =================================================================================================================
    //  Sound out, state
    // =================================================================================================================

    public int ReadSamples(Span<short> dest) => psg.ReadSamples(dest);

    public void SaveState(BinaryWriter w)
    {
        w.Write(ram); w.Write(ym); w.Write(requested); w.Write(inReset); w.Write(granted); w.Write(grantTime); w.Write(bank); w.Write(lastMaster); w.Write(z80Master); w.Write(stallTicks); w.Write(interrupt);
        w.Write(ymAddress0); w.Write(ymAddress1); w.Write(ymBusyUntil);
        w.Write(timerALoaded); w.Write(timerBLoaded); w.Write(timerAEnable); w.Write(timerBEnable); w.Write(timerAFlag); w.Write(timerBFlag); w.Write(timerACount); w.Write(timerBCount); w.Write(timerSample);
        psg.SaveState(w);
        z80.SaveState(w);
    }

    public void LoadState(BinaryReader r)
    {
        r.Read(ram, 0, ram.Length); r.Read(ym, 0, ym.Length);
        requested = r.ReadBoolean(); inReset = r.ReadBoolean(); granted = r.ReadBoolean(); grantTime = r.ReadInt64(); bank = r.ReadInt32(); lastMaster = r.ReadInt64(); z80Master = r.ReadInt64(); stallTicks = r.ReadInt32(); interrupt = r.ReadBoolean();
        ymAddress0 = r.ReadInt32(); ymAddress1 = r.ReadInt32(); ymBusyUntil = r.ReadInt64();
        timerALoaded = r.ReadBoolean(); timerBLoaded = r.ReadBoolean(); timerAEnable = r.ReadBoolean(); timerBEnable = r.ReadBoolean(); timerAFlag = r.ReadBoolean(); timerBFlag = r.ReadBoolean();
        timerACount = r.ReadInt32(); timerBCount = r.ReadInt32(); timerSample = r.ReadInt64();
        psg.LoadState(r);
        z80.LoadState(r);
    }

    public string Describe() =>
        $"MD sound unit (Z80 + PSG + YM2612 registers, FM silent): z80 pc={z80.PC:X4} {(inReset ? "RESET" : requested && granted ? "bus held" : requested ? "bus requested" : "running")} bank=${bank:X3} z80-int={interrupt} " +
        $"psg-writes={PsgWrites} ym-writes={YmWrites} bank-reads={BankReads} z80-ticks={Z80Ticks}";
}
