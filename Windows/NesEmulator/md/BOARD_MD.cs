using System;
using System.IO;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>The 68000's view of the board: a struct that calls into <see cref="BOARD_MD"/> (see <see cref="IM68kBus"/> for why).</summary>
public struct MdBus : IM68kBus
{
    public BOARD_MD B;
    public ushort Read(uint address, bool upper, bool lower, int cycles) => B.BusRead(address, upper, lower, cycles);
    public void Write(uint address, bool upper, bool lower, ushort data, int cycles) => B.BusWrite(address, upper, lower, data, cycles);
    public void Idle(int cycles) => B.BusIdle(cycles);
    public int InterruptLevel => B.InterruptSample;
    public int Acknowledge(int level, int cycles) => B.BusAcknowledge(level, cycles);
    public void ResetLine(int cycles) => B.BusIdle(cycles);
}

/// <summary>
/// The Genesis / Mega Drive board on one master clock (53.693175 MHz NTSC, 53.203424 MHz PAL): the 68000 at master/7, the Z80 at master/15 (inside the sound unit), the VDP, and the memory map
/// with its arbitration. The 68000 is the clock owner: every bus cycle it makes arrives with the clocks since its last one (<see cref="IM68kBus"/>), the board turns them into master
/// clocks, runs the VDP and the sound unit up to that moment, and only then performs the access, so a register read sees the machine as it is at that instant, a stalled access (a full
/// VDP FIFO, a DMA that holds the bus, a Z80 bank-window access, DRAM refresh) delays the 68000's own clock, and the Z80 and VDP go on while it waits.
/// </summary>
/// <remarks>
/// <para><b>Memory map.</b> $000000-$3FFFFF cartridge (ROM, the SRAM window, the SSF2 banks), $A00000-$A0FFFF the Z80's space through the sound unit (while the 68000 holds the Z80 bus),
/// $A10000-$A1001F the I/O chip (version, two pads, expansion port), $A11100 BUSREQ, $A11200 RESET, $A130F1-$A130FF the mapper, $A14000 TMSS (accepted and ignored: with no boot ROM the VDP is
/// never locked), $C00000-$C0001F the VDP (data, control/status, H/V counter, PSG), $E00000-$FFFFFF the 64 KB of work RAM, mirrored. Everything else is open bus: a read returns the word of the 68000's last bus cycle (the next
    /// instruction word for a read made right after the fetch of it, which is what MacDonald's memory-test ROM shows; the VDP status and the BUSREQ and RESET registers show it in the bits they do not drive).</para>
/// <para><b>Choices recorded here (the plan lists the figures and says how the sources disagree).</b> DRAM refresh: <see cref="RefreshStallCycles"/> clocks lost every <see cref="RefreshPeriodCycles"/>
/// clocks of 68000 time, on every access (the simplest reading of "about 2 of every 128"). Z80 bank-window stalls: the Z80 waits <see cref="MdSoundUnit.BankWindowZ80Stall"/> T-states and the 68000
/// <see cref="MdSoundUnit.BankWindow68kStall"/> clocks per access (the test-ROM measurement; the 11-clock figure of other sources is a one-line change). The 68000 samples the interrupt lines at the
/// start of its last bus cycle of an instruction, which with the VDP's short delay after a register write reproduces the one-instruction latency of the hardware.</para>
/// </remarks>
public sealed class BOARD_MD : IGenesisSoundHost, IMdVdpHost
{
    /// <summary>Master clocks per 68000 clock.</summary>
    public const int ClocksPer68k = SegaTiming.GenesisM68kDivider;
    /// <summary>DRAM refresh holds the 68000 for this many of its clocks ...</summary>
    public const int RefreshStallCycles = 2;
    /// <summary>... every this many clocks of its time.</summary>
    public const int RefreshPeriodCycles = 128;

    public MdCartridge Cart { get; }
    public SegaTimeline Timeline { get; }
    public SegaRegion Region => Timeline.Region;
    public GenesisModel Model { get; }
    public M68k<MdBus> Cpu { get; }
    public MdVdp Vdp { get; }
    public IGenesisSound Sound { get; private set; }
    public MdPads Pads { get; }
    public readonly byte[] Ram = new byte[0x10000];

    private long t;                        // the 68000's clock, in master clocks (the end of its last access)
    private int refreshAccumulator;
    private long cpuStall;                 // master clocks the 68000 owes the Z80 for bank-window accesses made while the sound unit was catching up
    private int iplSample;                 // the interrupt level the 68000 sees (sampled at the start of its last bus cycle)
    private ushort lastOpenBus = 0xFFFF;

    public long MasterClock => t;

    /// <summary>
    /// The interrupt level the 68000 sees. It is sampled where each bus cycle starts (<see cref="BeginAccess"/>), which is right while the processor runs; a processor stopped by STOP makes no bus cycles at all, so
    /// its level is read from the VDP as the clock stands (the VDP has been brought to it by the run loop): without that a stopped game never wakes (Beast Wrestler, Valis, Mr. Nutz, the HardBall games and a dozen more
    /// sit in STOP waiting for the vertical interrupt, which the stale sample hid).
    /// </summary>
    public int InterruptSample
    {
        get
        {
            if (Cpu.Stopped) iplSample = Vdp.IplAt(t);
            return iplSample;
        }
    }
    public long Frames => Vdp.Frames;

    /// <summary>Called with each 68000 instruction's address just before it runs (for traces); null in normal runs.</summary>
    public Action<uint>? InstructionHook;
    /// <summary>Called for each write to the VDP, I/O chip, Z80 area, BUSREQ and RESET, with a text describing it (traces); null in normal runs.</summary>
    public Action<string>? WriteTrace;
    /// <summary>Called for each 68000 read outside the cartridge and the work RAM, with the address, the value returned and what the bus held before it (traces); null in normal runs.</summary>
    public Action<string>? ReadTrace;
    /// <summary>Called for each 68000 access to $200000-$3FFFFF with a text describing it (the I2C EEPROM and mapper regions; traces); null in normal runs.</summary>
    public Action<string>? CartTrace;
    public long BusReads, BusWrites, OpenBusReads;
    /// <summary>Diagnostics for the whole-library sweep: the last address a read found nothing at, writes nobody claimed (and the last one), and the last value written to the TMSS register at $A14000.</summary>
    public uint LastOpenBusAddress, LastUnclaimedWrite;
    public long UnclaimedWrites, TmssWrites;
    public readonly ushort[] TmssWords = new ushort[2];
    private readonly uint[] recentIoAddr = new uint[16];
    private readonly ushort[] recentIoVal = new ushort[16];
    private int recentIoPos;

    /// <summary>The last reads the 68000 made outside the cartridge and the work RAM (VDP, I/O, Z80 area, unmapped), newest last, as "address=value" and merged by address: what a game that is stuck is polling.</summary>
    public string RecentIoReads()
    {
        var seen = new System.Collections.Generic.List<(uint a, ushort v)>();
        for (int k = 0; k < 16 && k < recentIoPos; k++)
        {
            int idx = (recentIoPos - 1 - k) & 15;
            uint a = recentIoAddr[idx];
            if (!seen.Exists(s => s.a == a)) seen.Add((a, recentIoVal[idx]));
        }
        return string.Join(",", seen.ConvertAll(s => $"{s.a:X6}={s.v:X4}"));
    }

    public BOARD_MD(MdCartridge cart, SegaRegion region, GenesisModel model, int sampleRate, SegaBoardParts<MdBus, M68k<MdBus>, MdVdp, IGenesisSound>? parts = null)
    {
        Cart = cart;
        Timeline = SegaTimeline.Genesis(region);
        Model = model;
        parts ??= SegaBoardParts<MdBus, M68k<MdBus>, MdVdp, IGenesisSound>.Stock;

        Vdp = parts.BuildPpu(() => new MdVdp(Timeline));
        Vdp.Attach(this);
        var machine = new SegaMachine(Timeline, model, sampleRate);
        Sound = parts.BuildApu(machine, m => new MdSoundUnit(m));
        Sound.Attach(this);
        byte version = (byte)((cart.Overseas || region == SegaRegion.Pal ? 0x80 : 0) | (region == SegaRegion.Pal ? 0x40 : 0) | 0x20);
        Pads = new MdPads(version);
        Cpu = parts.BuildCpu(new MdBus { B = this }, bus => new M68k<MdBus>(bus));
        // Mega Drive models 1 and 2 never complete a TAS write; the later board does
        Cpu.TasWritesBack = model == GenesisModel.Model3;
        Reset();
    }

    /// <summary>Power-on: the VDP, the sound unit and the pads at their reset state, the 68000 taking its stack pointer and start address from the cartridge's vectors.</summary>
    public void Reset()
    {
        t = 0; refreshAccumulator = 0; cpuStall = 0; iplSample = 0;
        Array.Clear(Ram);
        Vdp.Reset();
        Sound.Reset();
        Cpu.Power();
        Cpu.Reset();
    }

    // =================================================================================================================
    //  Running
    // =================================================================================================================

    /// <summary>Run until the VDP starts the next frame.</summary>
    public void RunFrame()
    {
        Vdp.ClearFrameFlag();
        long guard = t + Timeline.MasterClocksPerFrame * 4;
        while (!Vdp.FrameCompleted && t < guard)
        {
            InstructionHook?.Invoke(Cpu.Pc - 4);
            Cpu.Step();
            Vdp.RunTo(t);
        }
        Sound.RunTo(t);
    }

    /// <summary>
    /// Run until the VDP reaches scanline <paramref name="line"/> (of this frame if it is still ahead, else of the next), the way an emulator that is stepped a frame at a time stops: BlastEm's pause and frame advance
    /// stop at about line 232, after the vertical interrupt has begun (measured with a pad-polling probe ROM: a key pressed while it is paused is first seen by the game at V counter 232). Scripted input meant for
    /// a comparison with it changes the pad here. Returns after the first instruction that starts the line (the 68000's granularity).
    /// </summary>
    public void RunUntilLine(int line)
    {
        int perFrame = Region == SegaRegion.Pal ? 313 : 262;
        long target = Vdp.Frames * perFrame + line;
        if (Vdp.Frames * perFrame + Vdp.Line >= target) target += perFrame;
        long guard = t + Timeline.MasterClocksPerFrame * 4;
        while (Vdp.Frames * perFrame + Vdp.Line < target && t < guard)
        {
            InstructionHook?.Invoke(Cpu.Pc - 4);
            Cpu.Step();
            Vdp.RunTo(t);
        }
        Sound.RunTo(t);
    }

    /// <summary>One 68000 instruction (or interrupt, or stopped slice), then the VDP and the sound unit are brought to the 68000's clock. Returns the 68000 clocks it counted.</summary>
    public int StepInstruction()
    {
        InstructionHook?.Invoke(Cpu.Pc - 4);
        int c = Cpu.Step();
        Vdp.RunTo(t);
        return c;
    }

    // =================================================================================================================
    //  The 68000's bus
    // =================================================================================================================

    private void BeginAccess(int cycles)
    {
        // the interrupt lines are sampled where this bus cycle starts (the last access of an instruction is its prefetch)
        long start = t + (long)(cycles - 4) * ClocksPer68k;
        Vdp.RunTo(start);
        iplSample = Vdp.IplAt(start);
        t += (long)cycles * ClocksPer68k;
        AddRefresh(cycles);
        Vdp.RunTo(t);
        SyncSound();
    }

    private void AddRefresh(int cycles)
    {
        refreshAccumulator += cycles;
        if (refreshAccumulator >= RefreshPeriodCycles)
        {
            refreshAccumulator -= RefreshPeriodCycles;
            t += RefreshStallCycles * ClocksPer68k;
        }
    }

    /// <summary>Bring the sound unit to the 68000's clock; whatever its Z80 cost the 68000 on the way (bank-window accesses) is paid, and the unit and VDP run on through the stall.</summary>
    private void SyncSound()
    {
        Sound.RunTo(t);
        while (cpuStall > 0)
        {
            t += cpuStall; cpuStall = 0;
            Vdp.RunTo(t);
            Sound.RunTo(t);
        }
    }

    /// <summary>A VDP port access can leave the VDP busy (a full FIFO, a DMA); the 68000 resumes when it is not.</summary>
    private void AfterVdpAccess()
    {
        if (Vdp.Time > t) t = Vdp.Time;
        while (Vdp.DmaHaltsCpu)
        {
            Vdp.AdvanceWork();
            if (Vdp.Time > t) t = Vdp.Time;
        }
        SyncSound();
    }

    public void BusIdle(int cycles)
    {
        t += (long)cycles * ClocksPer68k;
        AddRefresh(cycles);
    }

    public int BusAcknowledge(int level, int cycles)
    {
        t += (long)cycles * ClocksPer68k;
        Vdp.RunTo(t);
        Vdp.AcknowledgeInterrupt(level);
        iplSample = 0;
        return 24 + level;
    }

    public ushort BusRead(uint address, bool upper, bool lower, int cycles)
    {
        BeginAccess(cycles);
        BusReads++;
        address &= 0xFFFFFE;
        ushort v = ReadWord(address, upper, lower, t);
        if (address >= 0x400000 && address < 0xE00000)
        {
            recentIoAddr[recentIoPos & 15] = address; recentIoVal[recentIoPos & 15] = v; recentIoPos++;
            if (ReadTrace != null) ReadTrace($"read ${address:X6}{(upper ? "u" : "-")}{(lower ? "l" : "-")} -> {v:X4} (bus {lastOpenBus:X4})");
        }
        lastOpenBus = v;
        return v;
    }

    public void BusWrite(uint address, bool upper, bool lower, ushort data, int cycles)
    {
        BeginAccess(cycles);
        BusWrites++;
        address &= 0xFFFFFE;
        WriteWord(address, upper, lower, data, t);
        lastOpenBus = data;
    }

    private ushort ReadWord(uint address, bool upper, bool lower, long now)
    {
        if (address < 0x400000)
        {
            ushort cv = Cart.ReadWord(address);
            if (CartTrace != null && address >= 0x200000) CartTrace($"cart read ${address:X6}{(upper ? "u" : "-")}{(lower ? "l" : "-")} -> {cv:X4}");
            return cv;
        }
        if (address >= 0xE00000) { int a = (int)(address & 0xFFFE); return (ushort)(Ram[a] << 8 | Ram[a + 1]); }
        if (address >= 0xC00000 && address < 0xE00000) return ReadVdp(address, now);
        if (address >= 0xA00000 && address < 0xA10000)
        {
            byte b = Sound.Read((int)(address & 0xFFFF) | (!upper && lower ? 1 : 0), now);
            return (ushort)(b << 8 | b);
        }
        if (address >= 0xA10000 && address < 0xA10020)
        {
            byte b = Pads.Read((int)(address & 0x1F) | (!upper && lower ? 1 : 0), now);
            return (ushort)(b << 8 | b);
        }
        if (address == 0xA11100) return (ushort)(lastOpenBus & 0xFEFF | (Sound.IsBusGranted(now) ? 0x0000 : 0x0100));     // only bit 8 is the register's; the rest of the word is the bus's
        // the RESET register reads back whether the line is released (bit 8; oracle-derived: BlastEm shows it, hardware not verified), the rest of the word is the bus's
        if (address == 0xA11200) return (ushort)(lastOpenBus & 0xFEFF | (Sound.IsInReset ? 0x0000 : 0x0100));
        if (address >= 0xA130F0 && address < 0xA13100) return 0xFFFF;
        OpenBusReads++; LastOpenBusAddress = address;
        return lastOpenBus;
    }

    private void WriteWord(uint address, bool upper, bool lower, ushort data, long now)
    {
        if (address < 0x400000)
        {
            if (CartTrace != null && address >= 0x200000) CartTrace($"cart write ${address:X6}{(upper ? "u" : "-")}{(lower ? "l" : "-")} = {data:X4}");
            if (upper) Cart.WriteByte(address, (byte)(data >> 8));
            if (lower) Cart.WriteByte(address | 1, (byte)data);
            return;
        }
        if (address >= 0xE00000)
        {
            int a = (int)(address & 0xFFFE);
            if (upper) Ram[a] = (byte)(data >> 8);
            if (lower) Ram[a + 1] = (byte)data;
            return;
        }
        if (address >= 0xC00000 && address < 0xE00000) { WriteVdp(address, upper, lower, data, now); return; }
        if (address >= 0xA00000 && address < 0xA10000)
        {
            byte v = upper ? (byte)(data >> 8) : (byte)data;
            WriteTrace?.Invoke($"z80 area ${address | (!upper && lower ? 1u : 0u):X6} = {v:X2}");
            Sound.Write((int)(address & 0xFFFF) | (!upper && lower ? 1 : 0), v, now);
            return;
        }
        if (address >= 0xA10000 && address < 0xA10020)
        {
            if (lower)
            {
                WriteTrace?.Invoke($"io ${address | 1:X6} = {(byte)data:X2}");
                Pads.Write((int)(address & 0x1F) | 1, (byte)data, now);
            }
            return;
        }
        if (address == 0xA11100)
        {
            if (upper) { bool req = (data >> 8 & 1) != 0; WriteTrace?.Invoke($"busreq {(req ? "request" : "release")}"); Sound.WriteBusRequest(req, now); }
            return;
        }
        if (address == 0xA11200)
        {
            if (upper) { bool rel = (data >> 8 & 1) != 0; WriteTrace?.Invoke($"z80 reset {(rel ? "released" : "asserted")}"); Sound.WriteReset(rel, now); }
            return;
        }
        if (address >= 0xA130F0 && address < 0xA13100)
        {
            if (lower) Cart.WriteMapper(address | 1, (byte)data);
            return;
        }
        if (address >= 0xA14000 && address < 0xA14004) { TmssWrites++; TmssWords[(address - 0xA14000) >> 1] = data; return; }
        // everything else: ignored (counted for the sweep)
        UnclaimedWrites++; LastUnclaimedWrite = address;
    }

    // ---- the VDP's addresses

    private ushort ReadVdp(uint address, long now)
    {
        if ((address & 0xE700E0) != 0xC00000) { OpenBusReads++; return lastOpenBus; }
        ushort v;
        switch ((int)(address & 0x1F))
        {
            case >= 0x00 and <= 0x03: v = Vdp.ReadData(now); break;
            case >= 0x04 and <= 0x07: v = (ushort)(Vdp.ReadStatus(now) & MdVdp.StatusMask | lastOpenBus & ~MdVdp.StatusMask); break;
            case >= 0x08 and <= 0x0F: v = Vdp.ReadHvCounter(now); break;
            default: OpenBusReads++; return lastOpenBus;
        }
        AfterVdpAccess();
        return v;
    }

    private void WriteVdp(uint address, bool upper, bool lower, ushort data, long now)
    {
        if ((address & 0xE700E0) != 0xC00000) return;
        int reg = (int)(address & 0x1F);
        if (reg <= 0x03)
        {
            WriteTrace?.Invoke($"vdp data {data:X4}");
            Vdp.WriteData(data, now);
            AfterVdpAccess();
        }
        else if (reg <= 0x07)
        {
            WriteTrace?.Invoke($"vdp ctrl {data:X4}");
            Vdp.WriteControl(data, now);
            AfterVdpAccess();
        }
        else if (reg >= 0x10 && reg <= 0x17)
        {
            if (lower) { WriteTrace?.Invoke($"psg {(byte)data:X2}"); Sound.WritePsg((byte)data, now); }
        }
    }

    // =================================================================================================================
    //  What the Z80 and the VDP's DMA see of the 68000's space
    // =================================================================================================================

    public byte ReadBus(int address, long masterClock, out int waitMasterClocks)
    {
        // the bank-window access stalls the Z80 and, a little later than it, the 68000
        cpuStall += (long)(MdSoundUnit.BankWindow68kStall * ClocksPer68k);
        waitMasterClocks = (int)(MdSoundUnit.BankWindowZ80Stall * SegaTiming.GenesisZ80Divider);
        uint a = (uint)address & 0xFFFFFF;
        long now = Math.Max(masterClock, Vdp.Time);
        ushort w;
        if (a >= 0xC00000 && a < 0xE00000)
        {
            // the Z80 can reach the VDP's ports; the VDP is brought to the later of the two clocks
            w = ReadVdp(a & ~1u, now);
        }
        else w = ReadWord(a & ~1u, true, true, now);
        return (byte)((a & 1) != 0 ? w : w >> 8);
    }

    public void WriteBus(int address, byte value, long masterClock, out int waitMasterClocks)
    {
        cpuStall += (long)(MdSoundUnit.BankWindow68kStall * ClocksPer68k);
        waitMasterClocks = (int)(MdSoundUnit.BankWindowZ80Stall * SegaTiming.GenesisZ80Divider);
        uint a = (uint)address & 0xFFFFFF;
        long now = Math.Max(masterClock, Vdp.Time);
        bool odd = (a & 1) != 0;
        WriteWord(a & ~1u, !odd, odd, (ushort)(value << 8 | value), now);
    }

    public ushort ReadBusWord(uint address)
    {
        uint a = address & 0xFFFFFE;
        if (a < 0x400000) return Cart.ReadWord(a);
        if (a >= 0xE00000) { int i = (int)(a & 0xFFFE); return (ushort)(Ram[i] << 8 | Ram[i + 1]); }
        return 0xFFFF;      // a DMA source in I/O or the Z80's space reads open bus
    }

    public void SetZ80Interrupt(bool asserted, long masterClock) => Sound.SetInterrupt(asserted, masterClock);

    // =================================================================================================================
    //  State and status
    // =================================================================================================================

    public void SetPad(int player, PadButtons buttons) => Pads.SetButtons(player, buttons);

    public string Describe() =>
        $"BOARD_MD {Region} {Model} t={t} frames={Vdp.Frames} | 68000 pc={Cpu.Pc - 4:X6} sr={Cpu.ReadSr():X4} instr={Cpu.Instructions} addr-errors={Cpu.AddressErrorCount}{(Cpu.Halted ? " HALTED" : "")} | {Vdp.Describe()} | {Sound.Describe()} | {Pads.Describe()}";

    public void SaveState(BinaryWriter w)
    {
        w.Write(t); w.Write(refreshAccumulator); w.Write(cpuStall); w.Write(iplSample); w.Write(Ram);
        Vdp.SaveState(w); Sound.SaveState(w); Cart.SaveState(w);
    }

    public void LoadState(BinaryReader r)
    {
        t = r.ReadInt64(); refreshAccumulator = r.ReadInt32(); cpuStall = r.ReadInt64(); iplSample = r.ReadInt32(); r.Read(Ram, 0, Ram.Length);
        Vdp.LoadState(r); Sound.LoadState(r); Cart.LoadState(r);
    }
}
