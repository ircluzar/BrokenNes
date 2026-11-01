using System;
using System.Collections.Generic;
using NesEmulator.Gb;

namespace NesEmulator.Debugging.Checks;

/// <summary>
/// The Game Boy and Game Boy Color rules (docs/projects/brokendev-debugging-plan.md, "D4 settled"). Fed by the D1 hooks: the SM83's
/// watching bus (reads after they happen, writes before and after they land: the PPU's mode at the access decides VRAM / OAM blocking, as
/// the board's own code does), the OAM DMA and VRAM DMA taps, and the per-instruction hook.
/// </summary>
public sealed class GbChecks : ConsoleChecks
{
    private readonly BOARD_GB board;
    private readonly WrittenMap wram, hram = new(0x7F), oam = new(0xA0), vram, wramRead, hramRead = new(0x7F), vramData;
    private readonly StackCollisions stack = new(0x10000, spOnTop: true);   // the SM83's SP points at the last byte pushed
    private readonly bool cgb;

    public GbChecks(Debugger debugger, CheckOptions options) : base(debugger, options)
    {
        board = ((GbDebugMachine)debugger.Machine).Board;
        wram = new WrittenMap(board.Wram.Length);
        vram = new WrittenMap(board.Ppu.Vram.Length); vramData = new WrittenMap(board.Ppu.Vram.Length); wramRead = new WrittenMap(board.Wram.Length);
        cgb = board.Model == GbModel.Cgb;
    }

    public override string Family => "gb";
    public override double NominalFrameCycles => board.DoubleSpeed ? 35112 : 17556;
    public override double VblankCycles => board.DoubleSpeed ? 2280 : 1140;
    public override string CycleUnit => "M-cycles";
    private PPU_GB Ppu => board.Ppu;
    private CPU_GB Core => (CPU_GB)board.Core;
    protected override bool InVblank() => Ppu.LcdOn && Ppu.Mode == 1;
    // with the LCD off the emulator keeps its frame cadence, but the hardware has no vblank (no mode 1, no interrupt): no lag frame then
    protected override bool VblankCanArrive() => Ppu.LcdOn;
    protected override bool MainWaiting(DebugCpu c, bool executes) => !executes && (Core.Halted || Core.Stopped);

    private long lastFrame = -1;
    protected override bool FrameBoundary()
    {
        long f = Ppu.FrameCount;
        bool b = lastFrame >= 0 && f != lastFrame;
        lastFrame = f;
        return b;
    }

    // ---------------------------------------------------------------- the instruction about to run
    private uint curPc;
    private byte lastOp;
    private bool curStackOp;
    private ushort curSp;
    private bool keyArmed; private long keyArmedFrame; private uint keyArmedPc; private bool keyReported;

    protected override void MainStep(DebugCpu c, long cycle, bool executes, string? interrupt, uint pc)
    {
        curSp = Core.SP; curStackOp = false;
        if (!executes) { if (interrupt != null) curStackOp = true; return; }
        curPc = pc;
        byte op = c.Peek(pc);
        switch (op)
        {
            case 0xC5: case 0xD5: case 0xE5: case 0xF5: case 0xC1: case 0xD1: case 0xE1: case 0xF1:        // PUSH, POP
            case 0xCD: case 0xC4: case 0xCC: case 0xD4: case 0xDC:                                         // CALL
            case 0xC9: case 0xD9: case 0xC0: case 0xC8: case 0xD0: case 0xD8:                              // RET, RETI
            case 0xC7: case 0xCF: case 0xD7: case 0xDF: case 0xE7: case 0xEF: case 0xF7: case 0xFF:        // RST
                curStackOp = true; break;
            case 0x76:   // HALT
                if (!Core.Ime && board.PendingInterrupts != 0 && lastOp != 0xFB)
                    Report("gb.halt-bug", CodePlace(c, pc), "", c, pc, $"HALT with IME clear and interrupts ${board.PendingInterrupts:X2} pending: the next byte is read twice");
                break;
            case 0x10:   // STOP
                if (keyArmed)
                {
                    byte ie = board.DebugIe, joyp = board.DebugJoypSelect;
                    if (ie != 0 || (joyp & 0x30) != 0x30)
                        Report("gb.speed-switch", CodePlace(c, pc), "", c, pc, $"STOP for the speed switch with IE = ${ie:X2} and JOYP = ${joyp | 0xC0:X2} (needs IE = $00 and JOYP = $30)");
                    keyArmed = false;
                }
                break;
        }
        lastOp = op;
    }

    private bool IsStackAccess(uint a)
    {
        if (!curStackOp) return false;
        int d = (int)a - curSp;
        return d >= -3 && d <= 2;
    }

    // ---------------------------------------------------------------- accesses
    public override void OnAccess(DebugCpu c, uint address, byte value, bool write, AccessSource source, int channel)
    {
        ushort a = (ushort)address;
        if (source != AccessSource.Cpu) { DmaAccess(c, a, value, write, source); return; }
        uint f = a >= 0xE000 && a < 0xFE00 ? (uint)(a - 0x2000) : a;   // echo RAM folded

        if (board.DebugOamDmaActive && f < 0xFF00)
            Report("gb.oam-dma-hram", CodePlace(c, curPc), "", c, curPc, $"{(write ? "a write to" : "a read of")} ${a:X4} during OAM DMA (only HRAM is usable; the CPU sees the DMA's bytes)");

        if (f >= 0xFEA0 && f < 0xFF00) { Report("gb.unusable", "$FEA0-$FEFF", $"${a:X4}", c, curPc, $"{(write ? "write to" : "read of")} ${a:X4} in the unusable area"); if (write) NoteMainWrite(); return; }

        if (f >= 0xA000 && f < 0xC000)
        {
            if (!board.Cart.DebugRamGateOpen || (board.Cart.Ram.Length == 0 && !board.Cart.HasRtc))
                Report("gb.cart-ram-disabled", CodePlace(c, curPc), $"${a:X4}", c, curPc,
                    board.Cart.Ram.Length == 0 && !board.Cart.HasRtc ? $"{(write ? "write to" : "read of")} ${a:X4}: the cartridge has no RAM" : $"{(write ? "write to" : "read of")} ${a:X4} with the cartridge RAM disabled (write $0A to $0000-$1FFF first)");
            if (write) NoteMainWrite();
            return;
        }

        bool wramOrHram = (f >= 0xC000 && f < 0xE000) || (f >= 0xFF80 && f < 0xFFFF);
        if (wramOrHram)
        {
            Span<SpaceKey> keys = stackalloc SpaceKey[4];
            int n = c.AccessKeys(a, write, keys);
            var k = n > 1 ? keys[1] : keys[0];
            bool isHram = f >= 0xFF80;
            var map = isHram ? hram : wram;
            string text = isHram ? $"hram:${f:X4}" : $"wram:${k.Offset:X4}";
            bool isStack = IsStackAccess(f);
            if (write)
            {
                map.Set(k.Offset);
                if (isStack) { if (stack.Stack(f, true)) Report("gb.stack-overflow", MemoryPlace(c, f, text), text, c, curPc, $"the stack pushed onto a byte the game reads as data (SP = ${curSp:X4})"); }
                else NoteMainWrite();
            }
            else
            {
                (isHram ? hramRead : wramRead).Set(k.Offset);
                if (!map[k.Offset]) Report("gb.ram-uninit", MemoryPlace(c, f, text), text, c, curPc, "read before it was ever written");
                if (isStack) stack.Stack(f, false); else stack.Data(f, false, curSp);
            }
            return;
        }

        if (write) NoteMainWrite();
        if (f >= 0x8000 && f < 0xA000)
        {
            if (!write && Ppu.DebugVramReadBlocked) Report("gb.vram-mode3", CodePlace(c, curPc), $"${a:X4}", c, curPc, $"VRAM ${a:X4} read in mode 3 (it gives $FF)");
            return;   // writes are judged once they land (OnWriteDone)
        }
        if (f >= 0xFE00 && f < 0xFEA0)
        {
            if (!write && (Ppu.OamBlocked || board.DebugOamDmaActive)) Report("gb.oam-mode23", CodePlace(c, curPc), $"${a:X4}", c, curPc, $"OAM ${a:X4} read in mode {Ppu.Mode}{(board.DebugOamDmaActive ? " during OAM DMA" : "")} (it gives $FF)");
            return;
        }
        if (f == 0xFF40 && write && Ppu.LcdOn && (value & 0x80) == 0 && Ppu.Mode != 1)
            Report("gb.lcd-off", CodePlace(c, curPc), "", c, curPc, $"LCDC bit 7 cleared on line {Ppu.ReadRegister(0x44)} (mode {Ppu.Mode}): only switch the LCD off in vblank");
        if (cgb && (f == 0xFF69 || f == 0xFF6B) && !write && Ppu.VramBlocked)
            Report("gb.cgb-palette-mode3", CodePlace(c, curPc), $"${a:X4}", c, curPc, $"palette data ${a:X4} read in mode 3 (it gives $FF)");
        if (cgb && f == 0xFF4D && write && (value & 1) != 0 && !keyArmed) { keyArmed = true; keyArmedFrame = M.Frame; keyArmedPc = curPc; keyReported = false; }
        if (f == 0xFF46 && write) { NoteDma(160); NoteUpload(M.Cycle + 160); }
    }

    public override void OnWriteDone(DebugCpu c, uint address, byte value)
    {
        ushort a = (ushort)address;
        if (a >= 0x8000 && a < 0xA000)
        {
            if (Ppu.VramBlocked) Report("gb.vram-mode3", CodePlace(c, curPc), $"${a:X4}", c, curPc, $"VRAM ${a:X4} written in mode 3 (the write was dropped)");
            else { MarkVram(c, a, value); NoteUpload(M.Cycle); }
        }
        else if (a >= 0xFE00 && a < 0xFEA0)
        {
            if (Ppu.DebugOamWriteBlocked || board.DebugOamDmaActive) Report("gb.oam-mode23", CodePlace(c, curPc), $"${a:X4}", c, curPc, $"OAM ${a:X4} written in mode {Ppu.Mode}{(board.DebugOamDmaActive ? " during OAM DMA" : "")} (the write was dropped)");
            else { oam.Set(a - 0xFE00); NoteUpload(M.Cycle); }
        }
        else if (cgb && (a == 0xFF69 || a == 0xFF6B) && Ppu.VramBlocked)
            Report("gb.cgb-palette-mode3", CodePlace(c, curPc), $"${a:X4}", c, curPc, $"palette data ${a:X4} written in mode 3 (the write was dropped)");
    }

    private void MarkVram(DebugCpu c, ushort a, byte value)
    {
        Span<SpaceKey> keys = stackalloc SpaceKey[4];
        int n = c.AccessKeys(a, true, keys);
        if (n > 1 && keys[1].Space == Sm83.SpaceVram) { vram.Set(keys[1].Offset); if (value != 0) vramData.Set(keys[1].Offset); }
    }

    private void DmaAccess(DebugCpu c, ushort a, byte value, bool write, AccessSource source)
    {
        if (write)
        {
            if (source == AccessSource.OamDma) oam.Set(a - 0xFE00);
            else { MarkVram(c, a, value); NoteDma(1); NoteUpload(M.Cycle); }
            return;
        }
        uint f = a >= 0xE000 && a < 0xFE00 ? (uint)(a - 0x2000) : a;
        if ((f >= 0xC000 && f < 0xE000) || (f >= 0xFF80 && f < 0xFFFF))
        {
            Span<SpaceKey> keys = stackalloc SpaceKey[4];
            int n = c.AccessKeys(a, false, keys);
            var k = n > 1 ? keys[1] : keys[0];
            bool isHram = f >= 0xFF80;
            string text = isHram ? $"hram:${f:X4}" : $"wram:${k.Offset:X4}";
            if (!(isHram ? hram : wram)[k.Offset])
            {
                string place = MemoryPlace(c, f, "");
                if (place.Length == 0) place = source == AccessSource.OamDma ? $"OAM DMA from ${f & 0xFF00:X4}" : $"VRAM DMA from ${f & 0xFFF0:X4}";
                Report("gb.ram-uninit", place, text, c, curPc, $"read by {(source == AccessSource.OamDma ? "OAM DMA" : "VRAM DMA")} before it was written");
            }
        }
    }

    protected override void OnFrameEnd(uint mainPc)
    {
        if (Ppu.LcdOn && (Ppu.Lcdc & 0x02) != 0)
        {
            int missing = 0;
            for (int i = 0; i < 0xA0; i++) if (!oam[i]) missing++;
            if (missing > 0) Report("gb.ram-uninit", "OAM (the sprite scan)", "oam", Main, curPc, $"sprites are on and {missing} of the 160 OAM bytes were never written (the PPU scans all 40 sprites)");
        }
        if (keyArmed && !keyReported && M.Frame - keyArmedFrame >= 2)
        {
            keyReported = true;
            Report("gb.speed-switch", CodePlace(Main, keyArmedPc), "", Main, keyArmedPc, $"KEY1 armed for a speed switch at frame {keyArmedFrame} and no STOP followed (the switch only happens on STOP)");
        }
    }

    public override IEnumerable<(string name, long size, long written)> MemoryUse()
    {
        yield return ("wram", wramRead.Size, wramRead.Count());
        yield return ("hram", hramRead.Size, hramRead.Count());
        yield return ("vram", vramData.Size, vramData.Count());
    }

    public override (long deepest, long free) StackUse()
    {
        if (stack.Lowest == uint.MaxValue) return (-1, -1);
        uint start = stack.Lowest >= 0xFF80 ? 0xFF80u : 0xC000u;
        return (stack.Lowest, stack.Headroom(start));
    }
}
