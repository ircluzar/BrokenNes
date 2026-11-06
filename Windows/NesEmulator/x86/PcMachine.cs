// The x86 console's board: a 486-class PC/AT (CPU, 8259 PICs, 8254 PIT, 8042 keyboard controller, CMOS clock, VGA, the BIOS in C#,
// disks through INT 13h). Written for BrokenNes; the chips are ported from tiny386 where their files say so. The x86 console is
// ISOLATED (docs/projects/next-pool-plans.md, P3): none of these parts is a slot of another console, nor the other way round.
//
// Time is deterministic: one clock counted in CPU instructions (plus the instructions a halted CPU sleeps through), turned into PIT
// ticks at InstructionsPerSecond. The same disk image and the same input give the same run.

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed partial class PcMachine : IX86Bus
{
    public const int DefaultMemory = 16 * 1024 * 1024;
    public readonly I386 Cpu;
    public readonly Pic8259 Pic = new();
    public readonly Pit8254 Pit;
    public readonly Kbc8042 Kbc;
    public readonly Cmos Cmos;
    public readonly Vga Vga;
    public readonly DiskImage?[] Floppy = new DiskImage?[2];
    public readonly DiskImage?[] Hdd = new DiskImage?[2];
    public readonly int MemKb;

    /// <summary>How fast the machine runs (instructions per emulated second). About a 486DX-33 for typical code.</summary>
    public long InstructionsPerSecond = 20_000_000;
    private long _idle;                       // instructions a halted CPU slept through
    private byte _port61;
    private bool _port61Refresh;

    /// <summary>Set when the machine stopped (a CPU abort / triple fault, or a BIOS boot failure): the reason.</summary>
    public string? Stopped;
    /// <summary>Text the BIOS wrote with INT 10h teletype (handy for tests: what DOS printed).</summary>
    public readonly System.Text.StringBuilder Teletype = new();
    /// <summary>Port E9h output (the Bochs debug port): a test program's log.</summary>
    public readonly System.Text.StringBuilder DebugPort = new();
    /// <summary>Called for a port the board does not answer (for devices added later: sound cards).</summary>
    public Func<int, int, int>? ExtraIn;     // (port, size) -> value or -1
    public Func<int, int, int, bool>? ExtraOut; // (port, size, value) -> handled

    public long Clock => Cpu.Cycle + _idle;
    public long PitNow => (long)((Int128)Clock * Pit8254.Freq / InstructionsPerSecond);

    public PcMachine(int memBytes = DefaultMemory)
    {
        MemKb = memBytes / 1024;
        Cpu = new I386(4, memBytes, this);
        Pit = new Pit8254(() => PitNow, () => Pic.Pulse(0));
        Kbc = new Kbc8042(Pic);
        Cmos = new Cmos(() => PitNow, MemKb);
        Vga = new Vga(() => PitNow);
        Pic.Raise = () => Cpu.RaiseIrq();
        RegisterTraps();
    }

    // ================= IX86Bus =================

    public bool MapsIoMem => true;

    public byte MemRead8(uint a) => a < 0xc0000 ? Vga.MemRead(a) : (byte)0xff;
    public void MemWrite8(uint a, byte v) { if (a < 0xc0000) Vga.MemWrite(a, v); }
    public ushort MemRead16(uint a) => (ushort)(MemRead8(a) | (MemRead8(a + 1) << 8));
    public void MemWrite16(uint a, ushort v) { MemWrite8(a, (byte)v); MemWrite8(a + 1, (byte)(v >> 8)); }
    public uint MemRead32(uint a) => (uint)(MemRead16(a) | (MemRead16(a + 2) << 16));
    public void MemWrite32(uint a, uint v) { MemWrite16(a, (ushort)v); MemWrite16(a + 2, (ushort)(v >> 16)); }

    public int AckIrq() => Pic.ReadIrq();

    public byte In8(int port)
    {
        switch (port)
        {
            case 0x20: case 0x21: case 0xa0: case 0xa1: return Pic.In(port);
            case 0x40: case 0x41: case 0x42: case 0x43: return Pit.In(port);
            case 0x60: { byte v = Kbc.In(0x60); return v; }
            case 0x64: return Kbc.In(0x64);
            case 0x61:
                // bit 4: the refresh toggle (flips every 15 us), bit 5: PIT channel 2's output
                _port61Refresh = ((PitNow / 18) & 1) != 0;
                return (byte)((_port61 & 0x0f) | (_port61Refresh ? 0x10 : 0) | (Pit.GetOut(2) != 0 ? 0x20 : 0));
            case 0x70: case 0x71: return Cmos.In(port);
            case 0x92: return (byte)(Kbc.A20 ? 2 : 0);
            case 0x201: return 0xff;                          // no joystick
            case >= 0x3b0 and <= 0x3df: return Vga.In(port);
        }
        if (ExtraIn != null) { int v = ExtraIn(port, 8); if (v >= 0) return (byte)v; }
        return 0xff;
    }

    public void Out8(int port, byte v)
    {
        switch (port)
        {
            case 0x20: case 0x21: case 0xa0: case 0xa1: Pic.Out(port, v); return;
            case 0x40: case 0x41: case 0x42: case 0x43: Pit.Out(port, v); return;
            case 0x60: case 0x64: Kbc.Out(port, v); if (Kbc.ResetRequested) CpuResetRequest(); return;
            case 0x61: _port61 = v; Pit.SetGate(2, v & 1); Speaker?.Invoke(); return;
            case 0x70: case 0x71: Cmos.Out(port, v); return;
            case 0x92: Kbc.A20 = (v & 2) != 0; if ((v & 1) != 0) CpuResetRequest(); return;
            case 0x80: return;                                // POST code
            case 0xe9: DebugPort.Append((char)v); return;
            case BiosTrapPort: BiosTrap(); return;
            case >= 0x3b0 and <= 0x3df: Vga.Out(port, v); return;
        }
        ExtraOut?.Invoke(port, 8, v);
    }

    public ushort In16(int port)
    {
        if (ExtraIn != null) { int v = ExtraIn(port, 16); if (v >= 0) return (ushort)v; }
        return (ushort)(In8(port) | (In8(port + 1) << 8));
    }
    public void Out16(int port, ushort v)
    {
        if (ExtraOut != null && ExtraOut(port, 16, v)) return;
        Out8(port, (byte)v); Out8(port + 1, (byte)(v >> 8));
    }
    public uint In32(int port) => (uint)(In16(port) | (In16(port + 2) << 16));
    public void Out32(int port, uint v) { Out16(port, (ushort)v); Out16(port + 2, (ushort)(v >> 16)); }

    /// <summary>Port 61h bit 1 (speaker data) and bit 0 (PIT channel 2 gate).</summary>
    public int Port61 => _port61;
    public Action? Speaker;

    private void CpuResetRequest()
    {
        Kbc.ResetRequested = false;
        // a warm reset: the BIOS checks CMOS 0Fh (shutdown code) / 40:67 like an AT BIOS; DOS extenders leaving to real mode use it.
        byte code = Cmos.Ram[0x0f];
        if (code is 0x05 or 0x0a)
        {
            Cmos.Ram[0x0f] = 0;
            Cpu.Reset();
            uint vec = Phys16(0x467) | ((uint)Phys16(0x469) << 4);
            Cpu.SetCsIp(Phys16(0x469), Phys16(0x467));
            if (code == 0x05) Pic.Out(0x20, 0x20);
            return;
        }
        PowerOn(keepDisks: true);
    }

    // ================= physical memory helpers (for the BIOS and the host) =================

    public byte Phys8(uint a) => a is >= 0xa0000 and < 0xc0000 ? Vga.MemRead(a) : a < Cpu.MemSize ? Cpu.Mem[a] : (byte)0xff;
    public ushort Phys16(uint a) => (ushort)(Phys8(a) | (Phys8(a + 1) << 8));
    public uint Phys32(uint a) => (uint)(Phys16(a) | (Phys16(a + 2) << 16));
    public void PhysW8(uint a, byte v) { if (a is >= 0xa0000 and < 0xc0000) Vga.MemWrite(a, v); else if (a < Cpu.MemSize) Cpu.Mem[a] = v; }
    public void PhysW16(uint a, ushort v) { PhysW8(a, (byte)v); PhysW8(a + 1, (byte)(v >> 8)); }
    public void PhysW32(uint a, uint v) { PhysW16(a, (ushort)v); PhysW16(a + 2, (ushort)(v >> 16)); }

    // ================= running =================

    private long _lastFrame = -1;
    /// <summary>Instructions per batch between device updates (an interrupt waits at most this long).</summary>
    public int Batch = 256;

    /// <summary>Runs until the VGA starts its next frame (vertical retrace), renders the picture. False when the machine has stopped.</summary>
    public bool RunFrame()
    {
        if (Stopped != null) return false;
        long frame = Vga.FrameIndex(PitNow);
        if (_lastFrame < 0) _lastFrame = frame;
        long target = _lastFrame + 1;
        // the clock value where that frame begins
        long guard = 0;
        while (Vga.FrameIndex(PitNow) < target)
        {
            if (!RunBatch()) return false;
            if (++guard > 100_000_000) { Stopped = "frame never ended"; return false; }
        }
        _lastFrame = target;
        Vga.Render();
        return Stopped == null;
    }

    /// <summary>Runs for <paramref name="instr"/> machine-clock instructions (headless tests).</summary>
    public bool RunClock(long instr)
    {
        long end = Clock + instr;
        while (Clock < end && Stopped == null) if (!RunBatch()) return false;
        return Stopped == null;
    }

    private bool RunBatch()
    {
        Pit.UpdateIrq();
        Kbc.Pump();
        try
        {
            if (Cpu.Halt)
            {
                if (Cpu.Intr && (Cpu.Flags & I386.IF) != 0) { Cpu.Step(1); return true; }
                // sleep until the next timer interrupt, but no further than the end of this frame's batch
                long nextPit = Pit.NextIrq0();
                long nowPit = PitNow;
                long sleepPit = nextPit == long.MaxValue ? 1000 : Math.Max(1, nextPit - nowPit);
                long sleepInstr = Math.Max(1, (long)((Int128)sleepPit * InstructionsPerSecond / Pit8254.Freq));
                _idle += Math.Min(sleepInstr, Batch * 16);
                if ((Cpu.Flags & I386.IF) == 0 && nextPit == long.MaxValue && !Kbc.HasPendingKeys) { }
                return true;
            }
            Cpu.Step(Batch);
        }
        catch (I386AbortException e) { Stopped = "CPU stopped: " + e.Message; return false; }
        return true;
    }

    // ================= power =================

    /// <summary>Cold boot: clears memory, resets every chip, runs the BIOS's POST and boots (floppy A: first, then the hard disk).</summary>
    public void PowerOn(bool keepDisks = true)
    {
        Stopped = null;
        Array.Clear(Cpu.Mem);
        Cpu.Reset();
        Pic.Reset();
        Pit.Reset();
        _lastFrame = -1;
        Post();
    }

    // ================= save state =================

    public byte[] SaveState()
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(0x33384350); // "PC83"
            w.Write(1);
            Cpu.SaveState(w);
            w.Write(_idle); w.Write(_port61); w.Write(_lastFrame); w.Write(InstructionsPerSecond);
            Pic.Save(w); Pit.Save(w); Kbc.Save(w); Cmos.Save(w); Vga.Save(w);
            foreach (var d in new[] { Floppy[0], Floppy[1], Hdd[0], Hdd[1] })
            {
                w.Write(d != null);
                if (d != null) { w.Write(d.Data.Length); w.Write(d.Data); }
            }
        }
        return ms.ToArray();
    }

    public void LoadState(byte[] s)
    {
        using var r = new BinaryReader(new MemoryStream(s));
        if (r.ReadInt32() != 0x33384350 || r.ReadInt32() != 1) throw new InvalidDataException("not an x86 console state");
        Cpu.LoadState(r);
        _idle = r.ReadInt64(); _port61 = r.ReadByte(); _lastFrame = r.ReadInt64(); InstructionsPerSecond = r.ReadInt64();
        Pic.Load(r); Pit.Load(r); Kbc.Load(r); Cmos.Load(r); Vga.Load(r);
        var disks = new DiskImage?[4];
        var old = new[] { Floppy[0], Floppy[1], Hdd[0], Hdd[1] };
        for (int i = 0; i < 4; i++)
        {
            if (!r.ReadBoolean()) continue;
            var data = r.ReadBytes(r.ReadInt32());
            disks[i] = new DiskImage(data, i < 2);
        }
        Floppy[0] = disks[0]; Floppy[1] = disks[1]; Hdd[0] = disks[2]; Hdd[1] = disks[3];
        Stopped = null;
    }
}
