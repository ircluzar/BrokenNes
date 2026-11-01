using System;
using System.Collections.Generic;
using NesEmulator.Sega;

namespace NesEmulator.Debugging.Checks;

/// <summary>
/// The Master System / Game Gear rules (BrokenDev Timeflow T0: the first Sega rules, the ones every console's catalogue starts with): work RAM read before it was written,
/// and lag frames, with the profile. Fed by the Z80's hooks of <see cref="SmsDebugMachine"/> (data reads after they happen, writes before they land, ports at $10000 + the port).
/// The machine starts in the state the BIOS leaves (no BIOS runs): the BIOS's own copy of the memory control value at $C000 counts as written.
/// </summary>
public sealed class SmsChecks : ConsoleChecks
{
    private readonly BOARD_SMS board;
    private readonly WrittenMap ram = new(0x2000), ramRead = new(0x2000);
    private ushort curSp;
    private uint curPc;

    public SmsChecks(Debugger debugger, CheckOptions options) : base(debugger, options)
    {
        board = ((SmsDebugMachine)debugger.Machine).Board;
        if (board.Bios == null) ram.Set(0);   // the BIOS left its copy of port $3E there
    }

    public override string Family => "sms";
    private int Lines => board.Vdp.TotalLines;
    public override double NominalFrameCycles => SegaTiming.MasterSystemCpuCyclesPerLine * Lines;
    public override double VblankCycles => SegaTiming.MasterSystemCpuCyclesPerLine * (Lines - board.Vdp.ActiveHeight);
    public override string CycleUnit => "Z80 T-states";
    protected override bool InVblank() => board.Vdp.Line >= board.Vdp.ActiveHeight;
    protected override bool MainWaiting(DebugCpu c, bool executes) => !executes && board.Cpu.BoundaryKind == 3;   // HALT

    private long lastFrame = -1;
    protected override bool FrameBoundary()
    {
        long f = board.Frames;
        bool b = lastFrame >= 0 && f != lastFrame;
        lastFrame = f;
        return b;
    }

    protected override void MainStep(DebugCpu c, long cycle, bool executes, string? interrupt, uint pc)
    {
        curSp = board.Cpu.SP;
        if (executes) curPc = pc;
    }

    // a push, a call or an interrupt writes just below SP: the stack, not the program's data
    private bool IsStack(uint a) { int d = (int)a - curSp; return d >= -2 && d <= 1; }

    public override void OnAccess(DebugCpu c, uint address, byte value, bool write, AccessSource source, int channel)
    {
        if (address >= 0x10000) { if (write) NoteMainWrite(); return; }   // a port: the VDP, the PSG, the pads
        uint a = address & 0xFFFF;
        if (a < 0xC000) { if (write) NoteMainWrite(); return; }
        uint f = (a - 0xC000) & 0x1FFF;
        uint folded = 0xC000 | f;
        if (write)
        {
            ram.Set(f);
            if (!IsStack(folded) && !IsStack(a)) NoteMainWrite();
            return;
        }
        ramRead.Set(f);
        if (!ram[f]) Report("sms.ram-uninit", MemoryPlace(c, folded, $"ram:${f:X4}"), $"ram:${f:X4}", c, curPc, "read before it was ever written");
    }

    public override IEnumerable<(string name, long size, long written)> MemoryUse() { yield return ("ram", ramRead.Size, ramRead.Count()); }
    public override (long deepest, long free) StackUse() => (-1, -1);
}

/// <summary>
/// The Genesis rules (BrokenDev Timeflow T0): the 68000's work RAM read before it was written (by the 68000, by the Z80 through its bank window, or by the VDP's DMA), the Z80's
/// RAM read by the Z80 before anything wrote it, the Z80's space used by the 68000 without the Z80's bus (reads give $FF, writes are lost), and lag frames, with the profile.
/// </summary>
public sealed class MdChecks : ConsoleChecks
{
    private readonly BOARD_MD board;
    private readonly WrittenMap ram = new(0x10000), ramRead = new(0x10000), zram = new(0x2000), zramRead = new(0x2000);
    private uint curPc, curSp;

    public MdChecks(Debugger debugger, CheckOptions options) : base(debugger, options)
    {
        board = ((MdDebugMachine)debugger.Machine).Board;
    }

    public override string Family => "md";
    public override double NominalFrameCycles => board.Timeline.MasterClocksPerFrame;
    public override double VblankCycles => board.Timeline.MasterClocksPerFrame * (1.0 - board.Vdp.ActiveLines / (board.Region == SegaRegion.Pal ? 313.0 : 262.0));
    public override string CycleUnit => "master clocks";
    protected override bool InVblank() => board.Vdp.Line >= board.Vdp.ActiveLines;
    protected override bool MainWaiting(DebugCpu c, bool executes) => !executes && board.Cpu.Stopped;
    protected override bool WantsOtherCpu(int cpuIndex) => cpuIndex == 1;

    private long lastFrame = -1;
    protected override bool FrameBoundary()
    {
        long f = board.Vdp.Frames;
        bool b = lastFrame >= 0 && f != lastFrame;
        lastFrame = f;
        return b;
    }

    protected override void MainStep(DebugCpu c, long cycle, bool executes, string? interrupt, uint pc)
    {
        curSp = board.Cpu.A[7] & 0xFFFFFF;
        if (executes) curPc = pc;
    }

    private bool IsStack(uint a) { long d = (long)(a & 0xFFFFFF) - curSp; return d >= -6 && d <= 5; }

    public override void OnAccess(DebugCpu c, uint address, byte value, bool write, AccessSource source, int channel)
    {
        if (c.Index == 1) { Z80Access(c, address, value, write, source); return; }
        uint a = address & 0xFFFFFF;
        if (a >= 0xE00000)
        {
            uint f = a & 0xFFFF;
            if (write) { ram.Set(f); if (source == AccessSource.Cpu && !IsStack(a) && !IsStack(0xFF0000 | f)) NoteMainWrite(); return; }
            ramRead.Set(f);
            if (!ram[f])
            {
                string who = source switch { AccessSource.Dma => " by the VDP's DMA", AccessSource.OtherCpu => " by the Z80 through its bank window", _ => "" };
                Report("md.ram-uninit", MemoryPlace(c, 0xFF0000 | f, $"ram:${f:X4}"), $"ram:${f:X4}", c, curPc, $"read{who} before it was ever written");
            }
            return;
        }
        if (source != AccessSource.Cpu) return;
        if (write) NoteMainWrite();
        if (a >= 0xA00000 && a < 0xA10000)
        {
            var unit = board.Sound as MdSoundUnit;
            if (unit != null && !unit.DebugWindowOpen(board.MasterClock))
                Report("md.z80-bus", CodePlace(c, curPc), $"${a:X6}", c, curPc, $"{(write ? "a write to" : "a read of")} ${a:X6} in the Z80's space without its bus (BUSREQ not granted, RESET released): {(write ? "the write is lost" : "it reads $FF")}");
            else if (a < 0xA04000)
            {
                uint z = a & 0x1FFF;
                if (write) zram.Set(z); else zramRead.Set(z);
            }
        }
    }

    private void Z80Access(DebugCpu c, uint address, byte value, bool write, AccessSource source)
    {
        uint a = address & 0xFFFF;
        if (address >= 0x10000 || a >= 0x4000) return;   // ports, the YM2612, the bank register, the windows
        uint z = a & 0x1FFF;
        if (write) { zram.Set(z); return; }
        zramRead.Set(z);
        if (!zram[z] && source == AccessSource.Cpu)
            Report("md.zram-uninit", $"zram:${z:X4}", $"zram:${z:X4}", c, c.PC, "the Z80 read its RAM before the Z80 or the 68000 wrote it");
    }

    public override IEnumerable<(string name, long size, long written)> MemoryUse()
    {
        yield return ("ram", ramRead.Size, ramRead.Count());
        yield return ("zram", zramRead.Size, zramRead.Count());
    }

    public override (long deepest, long free) StackUse() => (-1, -1);
}
