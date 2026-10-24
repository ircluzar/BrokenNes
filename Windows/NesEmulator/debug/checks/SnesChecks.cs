using System;
using System.Collections.Generic;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Checks;

/// <summary>
/// The SNES rules, also for the WideMess console (docs/projects/brokendev-debugging-plan.md, "D4 settled"). Fed by the D1 hooks: the
/// 65816's watching bus (every access, its idle cycles, writes before and after they land), the board's DMA / HDMA tap and the SPC700's
/// hook (its accesses predicted from each instruction's operands). The picture's own reads are a once-a-frame model at the start of vblank.
/// </summary>
public sealed class SnesChecks : ConsoleChecks
{
    private readonly SnesSession session;
    private readonly BOARD_SFC board;
    private readonly Snes65816 cpu;
    private readonly bool wideMess;
    private readonly WrittenMap wram = new(0x20000), vram = new(0x10000), cgram = new(0x200), oam = new(0x220), aram = new(0x10000);
    private readonly WrittenMap? wramHi;
    private readonly StackCollisions stack = new(0x2000);

    public SnesChecks(Debugger debugger, CheckOptions options) : base(debugger, options)
    {
        var m = (SnesDebugMachine)debugger.Machine;
        session = m.Session; board = m.Board; cpu = (Snes65816)m.Main;
        wideMess = session.Console == ConsoleKind.WideMess;
        if (board.WramHigh is { } hi) wramHi = new WrittenMap(hi.Length);
    }

    public override string Family => "snes";
    public override double NominalFrameCycles => BOARD_SFC.ClocksPerLine * BOARD_SFC.LinesPerFrame;
    public override double VblankCycles => (BOARD_SFC.LinesPerFrame - board.DebugVBlankLine) * (double)BOARD_SFC.ClocksPerLine;
    public override string CycleUnit => "master clocks";
    protected override bool InVblank() => board.DebugInVBlank;
    protected override bool WantsOtherCpu(int cpuIndex) => cpuIndex == 1;
    protected override bool MainWaiting(DebugCpu c, bool executes) => !executes && board.Cpu.Waiting;

    private long lastFrame = -1;
    protected override bool FrameBoundary()
    {
        long f = board.VBlankCount;
        bool b = lastFrame >= 0 && f != lastFrame;
        lastFrame = f;
        return b;
    }

    // ---------------------------------------------------------------- the instruction about to run
    private uint curPc;
    private bool curStackOp;
    private int curStackRel = -1;   // the stack-relative offset of a (d,S) / (d,S),Y instruction
    private ushort curS;
    private long cycles;            // the 65816's bus and idle cycles (the ALU's clock)

    protected override void MainStep(DebugCpu c, long cycle, bool executes, string? interrupt, uint pc)
    {
        var k = board.Cpu;
        curS = k.S; curStackOp = false; curStackRel = -1;
        if (!executes) { if (interrupt != null) curStackOp = true; return; }
        curPc = pc;
        byte op = c.Peek(pc);
        switch (op)
        {
            case 0x48: case 0x8B: case 0x0B: case 0x4B: case 0x08: case 0xDA: case 0x5A: case 0xF4: case 0xD4: case 0x62:   // pushes
            case 0x20: case 0xFC: case 0x22: case 0x00: case 0x02:                                                            // calls
            case 0x68: case 0xAB: case 0x2B: case 0x28: case 0xFA: case 0x7A: case 0x60: case 0x6B: case 0x40:                // pulls, returns
                curStackOp = true; break;
            default:
                if ((op & 0x0F) == 0x03) curStackRel = c.Peek(c.NextAddress(pc, 1));                                           // ORA d,S ... SBC (d,S),Y
                break;
        }
    }

    private bool IsStackAccess(uint wramOffset)
    {
        if (wramOffset >= 0x2000) return false;
        if (curStackOp) { int d = (int)wramOffset - curS; return d >= -4 && d <= 4; }
        if (curStackRel >= 0) { uint a = (ushort)(curS + curStackRel); return wramOffset == a || wramOffset == (ushort)(a + 1); }
        return false;
    }

    // ---------------------------------------------------------------- accesses
    private int pendingVram = -1, pendingCgram = -1, pendingOam = -1;
    private int prefetchWord = -1, cgPtr, oamPtr;
    private long mulAt = -100, divAt = -100; private bool lastWasDiv;
    private bool dmaRunning, dmaFromVblank, dmaHdmaReported; private uint dmaPc;
    private long debugPortWrites;

    public override void OnIdleCycle(DebugCpu c) { if (c.Index == 0) cycles++; }

    public override void OnAccess(DebugCpu c, uint address, byte value, bool write, AccessSource source, int channel)
    {
        if (c.Index == 1) { SpcAccess(address, value, write); return; }
        if (source == AccessSource.Cpu) cycles++;
        else
        {
            if (write) NoteDma(1);
            if (source == AccessSource.Hdma) HdmaDuringDma(c);
        }
        var key = cpu.Map(address);
        switch (key.Space)
        {
            case Snes65816.SpaceWram:
            case Snes65816.SpaceWramHi:
                Wram(c, address, key, write, source);
                return;
            case Snes65816.SpaceIo:
                Io(c, address, (ushort)key.Offset, value, write, source, channel);
                return;
            case Snes65816.SpaceCpu:
                if (!write && source == AccessSource.Cpu && !(address == 0x4221 || (address & 0xFFFF) == 0x4221))
                    Report("snes.open-bus", MemoryPlace(c, address, $"${address:X6}"), $"${address:X6}", c, curPc, $"read of ${address:X6}, which nothing answers");
                if (write && source == AccessSource.Cpu) NoteMainWrite();
                return;
            default:
                if (write && source == AccessSource.Cpu) NoteMainWrite();
                return;
        }
    }

    private void Wram(DebugCpu c, uint address, SpaceKey key, bool write, AccessSource source)
    {
        bool hi = key.Space == Snes65816.SpaceWramHi;
        var map = hi ? wramHi! : wram;
        string text = hi ? $"wramhi:${key.Offset:X5}" : $"wram:${key.Offset:X5}";
        bool isStack = !hi && source == AccessSource.Cpu && IsStackAccess(key.Offset);
        if (write)
        {
            map.Set(key.Offset);
            if (source != AccessSource.Cpu) return;
            if (isStack) { if (stack.Stack(key.Offset, true)) Report("snes.stack-overflow", MemoryPlace(c, address, text), text, c, curPc, $"the stack pushed onto a byte the game reads as data (S = ${curS:X4})"); }
            else
            {
                NoteMainWrite();
                if (!hi && key.Offset < 0x2000 && stack.Data(key.Offset, true, curS)) Report("snes.stack-overflow", MemoryPlace(c, address, text), text, c, curPc, $"data written into the live stack (S = ${curS:X4})");
            }
            return;
        }
        if (!map[key.Offset])
            Report("snes.wram-uninit", MemoryPlace(c, address, text), text, c, curPc,
                source == AccessSource.Cpu ? "read before it was ever written" : $"read by {source} (channel {cpuChannel(source)}) before it was ever written");
        if (source == AccessSource.Cpu && !hi && key.Offset < 0x2000) { if (isStack) stack.Stack(key.Offset, false); else stack.Data(key.Offset, false, curS); }
        static string cpuChannel(AccessSource s) => s == AccessSource.Hdma ? "HDMA" : "DMA";
    }

    private void Io(DebugCpu c, uint address, ushort reg, byte value, bool write, AccessSource source, int channel)
    {
        var ppu = board.Ppu;
        bool cpuSrc = source == AccessSource.Cpu;
        if (write)
        {
            if (cpuSrc) NoteMainWrite();
            switch (reg)
            {
                case 0x2118: case 0x2119:
                {
                    int b = ppu.DebugVramWordAddress * 2 + (reg == 0x2119 ? 1 : 0);
                    if (cpuSrc) pendingVram = b; else PictureWrite(c, "VRAM", b, source, channel, landed: ppu.DebugVramWritable);
                    return;
                }
                case 0x2122:
                {
                    int b = ppu.DebugCgramByteAddress;
                    if (cpuSrc) pendingCgram = b; else PictureWrite(c, "CGRAM", b, source, channel, landed: CgramWriteOk(source));
                    cgPtr = (b + 1) & 0x1FF;
                    return;
                }
                case 0x2104:
                {
                    int b = ppu.DebugOamByteAddress;
                    if (cpuSrc) pendingOam = b; else PictureWrite(c, "OAM", b, source, channel, landed: !ppu.DebugInDisplay || ppu.ForcedBlank);
                    return;
                }
                case 0x2121: cgPtr = value * 2; return;
                case 0x2180: WramPort(c, address, value, true, source); return;
            }
            if (!cpuSrc) return;
            switch (reg)
            {
                case 0x4203: mulAt = cycles; lastWasDiv = false; return;
                case 0x4206: divAt = cycles; lastWasDiv = true; return;
                case 0x420B:
                    if (value == 0) return;
                    dmaRunning = true; dmaFromVblank = board.DebugInVBlank; dmaHdmaReported = false; dmaPc = curPc;
                    return;
                case 0x420C:
                {
                    byte now = value, before = board.DebugHdmaEnable;
                    if ((now & ~before) != 0 && board.Scanline < board.DebugVBlankLine)
                        Report("snes.hdma-midframe", CodePlace(c, curPc), "", c, curPc, $"HDMA channels ${(now & ~before):X2} switched on at line {board.Scanline} (HDMAEN ${before:X2} -> ${now:X2})");
                    return;
                }
            }
            if (wideMess && reg >= 0x4230 && reg <= 0x423F) { DebugPortWrite(reg, value); return; }
            return;
        }

        // reads
        if (reg >= 0x2100 && reg < 0x2200)
        {
            byte r = (byte)reg;
            if (r == 0x80) { WramPort(c, address, value, false, source); return; }
            if (r == 0x39 || r == 0x3A)
            {
                // the value read is the prefetch, loaded from the word the address pointed at before its last move
                if (prefetchWord >= 0 && !vram[prefetchWord * 2 + (r == 0x3A ? 1 : 0)])
                    Report("snes.vram-uninit", $"vram:${prefetchWord * 2:X4}", $"vram:${prefetchWord * 2:X4}", c, curPc, $"read through ${reg:X4} by {Who(source, channel)} before it was written");
                bool reloads = ((ppu.DebugVmain & 0x80) != 0) == (r == 0x3A);
                if (reloads) prefetchWord = PreviousVramWord(ppu);
            }
            else if (r == 0x3B)
            {
                if (!cgram[cgPtr]) Report("snes.cgram-uninit", $"cgram:${cgPtr & 0x1FE:X3}", $"cgram:${cgPtr:X3}", c, curPc, $"colour {cgPtr >> 1} read through $213B by {Who(source, channel)} before it was written");
                cgPtr = (cgPtr + 1) & 0x1FF;
            }
            else if (r == 0x38)
            {
                if (!oam[oamPtr]) Report("snes.oam-uninit", $"oam:${oamPtr:X3}", $"oam:${oamPtr:X3}", c, curPc, $"read through $2138 by {Who(source, channel)} before it was written");
                oamPtr = ppu.DebugOamByteAddress;
            }
            else if (r >= 0x40 && r < 0x80) { if (cpuSrc) ApuPortRead(c, r & 3, value); }
            else if (cpuSrc && (r < 0x34 || (r > 0x80 && !(wideMess && board.WideMess is { Unlocked: true } && r >= WmsExtension.FirstBBus))))
                Report("snes.open-bus", MemoryPlace(c, address, $"${reg:X4}"), $"${reg:X4}", c, curPc, r < 0x34 ? $"read of the write-only PPU register ${reg:X4}" : $"read of ${reg:X4}, which nothing answers");
            return;
        }
        if (!cpuSrc) return;
        if (reg >= 0x4214 && reg <= 0x4217)
        {
            long since = cycles - (lastWasDiv ? divAt : mulAt);
            int need = lastWasDiv ? 16 : 8;
            if (since < need && (reg >= 0x4216 || lastWasDiv))
                Report("snes.muldiv-early", CodePlace(c, curPc), "", c, curPc,
                    $"${reg:X4} read {since} CPU cycles after the {(lastWasDiv ? "divide ($4206)" : "multiply ($4203)")} started; the result needs {need}");
            return;
        }
        bool readable = reg is 0x4016 or 0x4017 || (reg >= 0x4210 && reg < 0x4220) || (reg >= 0x4300 && reg < 0x4380)
                        || (reg >= 0x2200 && board.Coprocessor != null && board.Coprocessor.Owns(address >> 16, reg))
                        || (wideMess && reg >= 0x4220 && reg < 0x4240) || reg == 0x4221;
        if (!readable && reg >= 0x6000 && board.DebugCartIndex(address >> 16, reg, out _) >= 0) readable = true;
        if (!readable) Report("snes.open-bus", MemoryPlace(c, address, $"${reg:X4}"), $"${reg:X4}", c, curPc,
            reg >= 0x4200 && reg < 0x4210 ? $"read of the write-only CPU register ${reg:X4}" : $"read of ${reg:X4}, which nothing answers");
    }

    /// <summary>The $2180 port: the work RAM byte it reaches (D1's mirror folding knows the port's address).</summary>
    private void WramPort(DebugCpu c, uint address, byte value, bool write, AccessSource source)
    {
        Span<SpaceKey> keys = stackalloc SpaceKey[4];
        int n = cpu.AccessKeys(address, write, keys);
        if (n < 2) return;
        var k = keys[1];
        uint cpuAddr = k.Space == Snes65816.SpaceWram ? 0x7E0000u + k.Offset : 0x700000u + k.Offset;
        if (write) { (k.Space == Snes65816.SpaceWram ? wram : wramHi)?.Set(k.Offset); return; }
        var map = k.Space == Snes65816.SpaceWram ? wram : wramHi;
        if (map != null && !map[k.Offset])
        {
            string text = k.Space == Snes65816.SpaceWram ? $"wram:${k.Offset:X5}" : $"wramhi:${k.Offset:X5}";
            Report("snes.wram-uninit", MemoryPlace(c, cpuAddr, text), text, c, curPc, $"read through $2180 by {Who(source, -1)} before it was ever written");
        }
    }

    private static string Who(AccessSource s, int channel) => s == AccessSource.Cpu ? "the CPU" : $"{s} channel {channel}";

    private int PreviousVramWord(PPU_SFC ppu)
    {
        int inc = (ppu.DebugVmain & 3) switch { 0 => 1, 1 => 32, _ => 128 };
        return (ppu.DebugVramReadWordAddress - inc) & 0x7FFF;
    }

    private bool CgramWriteOk(AccessSource source)
    {
        var ppu = board.Ppu;
        if (!ppu.DebugInDisplay || ppu.ForcedBlank || source == AccessSource.Hdma) return true;
        int dot = board.LineClock >> 2;
        return dot < 22 || dot >= 274;   // h-blank
    }

    public override void OnWriteDone(DebugCpu c, uint address, byte value)
    {
        if (c.Index != 0) return;
        var ppu = board.Ppu;
        if (pendingVram >= 0) { PictureWrite(c, "VRAM", pendingVram, AccessSource.Cpu, -1, landed: ppu.DebugVramWritable); pendingVram = -1; }
        if (pendingCgram >= 0) { PictureWrite(c, "CGRAM", pendingCgram, AccessSource.Cpu, -1, landed: CgramWriteOk(AccessSource.Cpu)); pendingCgram = -1; }
        if (pendingOam >= 0) { PictureWrite(c, "OAM", pendingOam, AccessSource.Cpu, -1, landed: !ppu.DebugInDisplay || ppu.ForcedBlank); pendingOam = -1; }
        var key = cpu.Map(address);
        if (key.Space != Snes65816.SpaceIo) return;
        switch (key.Offset)
        {
            case 0x2116: case 0x2117: prefetchWord = ppu.DebugVramReadWordAddress; break;
            case 0x2102: case 0x2103: case 0x2104: oamPtr = ppu.DebugOamByteAddress; break;
            case 0x420B: dmaRunning = false; break;
        }
    }

    /// <summary>A VRAM / CGRAM / OAM byte written by the CPU or a DMA: marked written if it landed, a finding if the picture was being drawn.</summary>
    private void PictureWrite(DebugCpu c, string what, int b, AccessSource source, int channel, bool landed)
    {
        if (landed)
        {
            switch (what)
            {
                case "VRAM": vram.Set(b & 0xFFFF); break;
                case "CGRAM": if ((b & 1) == 1) { cgram.Set(b & 0x1FF); cgram.Set((b - 1) & 0x1FF); } break;   // the colour lands on its high byte
                default: if (b < 0x200) { if ((b & 1) == 1) { oam.Set(b); oam.Set(b - 1); } } else oam.Set(b % 0x220); break;
            }
            NoteUpload(board.MasterClock);
            return;
        }
        string rule = source == AccessSource.Dma && dmaFromVblank ? "snes.dma-overrun" : "snes.ppu-write-active";
        string by = source == AccessSource.Cpu ? "the CPU" : $"{source} channel {channel}";
        uint pc = source == AccessSource.Cpu ? curPc : dmaPc;
        Report(rule, CodePlace(c, pc), "", c, pc,
            rule == "snes.dma-overrun" ? $"a DMA started in vblank was still writing {what} on line {board.Scanline} (the write was dropped)"
                                       : $"{what} written by {by} on line {board.Scanline} dot {board.LineClock >> 2} without forced blank");
        // a CGRAM write during the picture still lands somewhere (the address the PPU reads), an OAM one at the PPU's address: the
        // game's own byte is not what it meant, so it stays unwritten
    }

    private void HdmaDuringDma(DebugCpu c)
    {
        if (!dmaRunning || dmaHdmaReported) return;
        dmaHdmaReported = true;
        Report("snes.dma-hdma", CodePlace(c, dmaPc), "", c, dmaPc, $"an HDMA transfer ran on line {board.Scanline} during the general DMA started here (the 5A22 v1 freezes)");
    }

    // ---------------------------------------------------------------- the SPC700 handshake
    private uint pollPc = uint.MaxValue; private int pollPort = -1; private byte pollValue; private long pollFrames, pollLastCycle; private bool pollReported;

    private void ApuPortRead(DebugCpu c, int port, byte value)
    {
        if (pollPc == curPc && pollPort == port && pollValue == value) { pollLastCycle = board.MasterClock; return; }
        pollPc = curPc; pollPort = port; pollValue = value; pollFrames = 0; pollReported = false; pollLastCycle = board.MasterClock;
    }

    protected override void OnFrameEnd(uint mainPc)
    {
        // a frame spent inside the poll loop (the main program's last instruction near the poll, the port read in the last frame)
        if (pollPc != uint.MaxValue && board.MasterClock - pollLastCycle < (long)NominalFrameCycles && ((long)mainPc - pollPc) is >= -32 and <= 32)
        {
            pollFrames++;
            if (pollFrames >= Opt.HandshakeFrames && !pollReported)
            {
                pollReported = true;
                Report("snes.spc-handshake", CodePlace(Main, pollPc), "", Main, pollPc,
                    $"the 65816 polled ${0x2140 + pollPort:X4} for {pollFrames} frames waiting for the SPC700 (the port stayed ${pollValue:X2})");
            }
        }
        else pollFrames = 0;
        CheckVoices();
        CheckPicture();
    }

    // ---------------------------------------------------------------- the echo buffer
    private APU_SFC? Apu => session.Spc;

    private bool EchoRegion(out int start, out int length)
    {
        start = 0; length = 0;
        var apu = Apu;
        if (apu == null) return false;
        var r = apu.Dsp.Regs;
        if ((r[0x6C] & 0x20) != 0) return false;   // echo writes off
        start = r[0x6D] << 8;
        length = Math.Max(4, apu.Dsp.DebugEchoLength);
        return true;
    }

    private static bool InRegion(int a, int start, int length) => ((a - start) & 0xFFFF) < length;

    protected override void OtherCpuStep(DebugCpu c, long cycle, bool executes, string? interrupt)
    {
        if (!executes || !EchoRegion(out int s, out int n)) return;
        int pc = (int)c.PC;
        if (InRegion(pc, s, n))
            Report("snes.echo-overrun", $"SPC700 code in the echo buffer ${s:X4}-${(s + n - 1) & 0xFFFF:X4}", $"spc:${pc:X4}", c, (uint)pc,
                $"the SPC700 runs code at ${pc:X4}, inside the echo buffer the DSP keeps writing (ESA ${s >> 8:X2}, {n} bytes)");
    }

    private void SpcAccess(uint address, byte value, bool write)
    {
        int a = (int)(address & 0xFFFF);
        if (write) { aram.Set(a); return; }
        if (a >= 0xF0 && a <= 0xFF) return;
        if (EchoRegion(out int s, out int n) && InRegion(a, s, n))
            Report("snes.echo-overrun", $"SPC700 data in the echo buffer ${s:X4}-${(s + n - 1) & 0xFFFF:X4}", $"spc:${a:X4}", Dbg.Machine.Cpus[1], Dbg.Machine.Cpus[1].PC,
                $"the SPC700 reads ${a:X4}, inside the echo buffer the DSP keeps writing (ESA ${s >> 8:X2}, {n} bytes)");
    }

    private void CheckVoices()
    {
        var apu = Apu;
        if (apu == null || !EchoRegion(out int s, out int n)) return;
        for (int i = 0; i < n; i += 64) aram.Set((s + i) & 0xFFFF);   // the echo buffer is in use (for the headroom)
        var r = apu.Dsp.Regs;
        var spc = Dbg.Machine.Cpus[1];
        for (int v = 0; v < 8; v++)
        {
            if (r[v * 0x10 + 8] == 0) continue;   // ENVX 0: silent
            int brr = apu.Dsp.DebugVoiceBrrAddress(v);
            if (InRegion(brr, s, n))
                Report("snes.echo-overrun", $"samples in the echo buffer ${s:X4}-${(s + n - 1) & 0xFFFF:X4}", $"spc:${brr:X4}", spc, spc.PC, $"voice {v} plays samples at ${brr:X4}, inside the echo buffer");
            int dir = (r[0x5D] << 8) + r[v * 0x10 + 4] * 4;
            if (InRegion(dir & 0xFFFF, s, n))
                Report("snes.echo-overrun", $"the sample directory in the echo buffer ${s:X4}-${(s + n - 1) & 0xFFFF:X4}", $"spc:${dir & 0xFFFF:X4}", spc, spc.PC, $"voice {v}'s directory entry at ${dir & 0xFFFF:X4} is inside the echo buffer");
        }
    }

    // ---------------------------------------------------------------- the WideMess debug port (reserved for D7)
    private void DebugPortWrite(ushort reg, byte value) { debugPortWrites++; }
    public long DebugPortWrites => debugPortWrites;

    // ---------------------------------------------------------------- the picture's own reads
    private readonly HashSet<long> checkedTiles = new();

    private void CheckPicture()
    {
        if (wideMess) return;   // the WideMess's 512 KB VRAM and 8-layer mode are not modelled
        var ppu = board.Ppu;
        var r = ppu.GetRegisterSnapshot();
        if ((r.Inidisp & 0x80) != 0) return;   // forced blank: nothing is drawn
        if (!cgram[0] || !cgram[1]) Report("snes.cgram-uninit", "the backdrop colour (cgram:$000)", "cgram:$000", Main, curPc, "the picture is on and colour 0 (the backdrop) was never written");
        int screens = r.Tm | r.Ts;
        int height = ppu.VisibleHeight;
        if ((screens & 0x10) != 0) CheckSprites(ppu, r, height);
        int mode = r.Bgmode & 7;
        if (mode > 4) return;   // modes 5-7 are not modelled
        int[] bpp = mode switch { 0 => new[] { 2, 2, 2, 2 }, 1 => new[] { 4, 4, 2, 0 }, 2 => new[] { 4, 4, 0, 0 }, 3 => new[] { 8, 4, 0, 0 }, _ => new[] { 8, 2, 0, 0 } };
        checkedTiles.Clear();
        for (int bg = 0; bg < 4; bg++)
        {
            if (bpp[bg] == 0 || (screens & (1 << bg)) == 0) continue;
            byte sc = bg switch { 0 => r.Bg1sc, 1 => r.Bg2sc, 2 => r.Bg3sc, _ => r.Bg4sc };
            int nba = bg switch { 0 => r.Bg12nba & 0xF, 1 => r.Bg12nba >> 4, 2 => r.Bg34nba & 0xF, _ => r.Bg34nba >> 4 };
            int hofs = bg switch { 0 => r.Bg1hofs, 1 => r.Bg2hofs, 2 => r.Bg3hofs, _ => r.Bg4hofs };
            int vofs = bg switch { 0 => r.Bg1vofs, 1 => r.Bg2vofs, 2 => r.Bg3vofs, _ => r.Bg4vofs };
            bool big = (r.Bgmode & (0x10 << bg)) != 0;
            int ts = big ? 16 : 8;
            int mapBase = (sc & 0xFC) << 8, w = (sc & 1) != 0 ? 64 : 32, h = (sc & 2) != 0 ? 64 : 32, charBase = nba << 12;
            int palBase = mode == 0 ? bg * 32 : 0;
            int wordsPerTile = bpp[bg] * 4;
            string layer = $"BG{bg + 1}";
            int mapMissing = 0, charMissing = 0, palMissing = 0, firstMap = -1, firstChar = -1, firstPal = -1;
            for (int ty = vofs / ts; ty <= (vofs + height - 1) / ts; ty++)
                for (int tx = hofs / ts; tx <= (hofs + 255) / ts; tx++)
                {
                    int mx = tx & (w - 1), my = ty & (h - 1);
                    int word = mapBase + (my & 31) * 32 + (mx & 31) + ((mx & 32) != 0 ? 0x400 : 0) + ((my & 32) != 0 ? (w == 64 ? 0x800 : 0x400) : 0);
                    word &= 0x7FFF;
                    if (!vram[word * 2] || !vram[word * 2 + 1]) { mapMissing++; if (firstMap < 0) firstMap = word * 2; continue; }
                    ushort e = ppu.Vram[word];
                    int tile = e & 0x3FF, pal = (e >> 10) & 7;
                    for (int sub = 0; sub < (big ? 4 : 1); sub++)
                    {
                        int t = (tile + (sub & 1) + ((sub & 2) != 0 ? 16 : 0)) & 0x3FF;
                        int charWord = (charBase + t * wordsPerTile) & 0x7FFF;
                        long tkey = (long)bg << 40 | (long)charWord << 8 | (long)pal;
                        if (!checkedTiles.Add(tkey)) continue;
                        if (!WordsWritten(charWord, wordsPerTile)) { charMissing++; if (firstChar < 0) firstChar = charWord * 2; continue; }
                        if (bpp[bg] == 8 && (r.Cgwsel & 1) != 0) continue;   // direct colour: no palette
                        int colourBase = bpp[bg] == 8 ? 0 : palBase + pal * (1 << bpp[bg]);
                        int missing = ColoursMissing(ppu, charWord, bpp[bg], colourBase);
                        if (missing >= 0) { palMissing++; if (firstPal < 0) firstPal = missing; }
                    }
                }
            if (mapMissing > 0) Report("snes.vram-uninit", $"{layer} tile map", $"vram:${firstMap:X4}", Main, curPc, $"{layer} shows {mapMissing} tile-map entries never written (first vram:${firstMap:X4}; map at word ${mapBase:X4})");
            if (charMissing > 0) Report("snes.vram-uninit", $"{layer} tiles", $"vram:${firstChar:X4}", Main, curPc, $"{layer} draws {charMissing} tiles never written (first vram:${firstChar:X4}; tiles at word ${charBase:X4})");
            if (palMissing > 0) Report("snes.cgram-uninit", $"{layer} colours", $"cgram:${firstPal * 2:X3}", Main, curPc, $"{layer} draws with {palMissing} palettes holding colours never written (first colour {firstPal})");
        }
    }

    private void CheckSprites(PPU_SFC ppu, PPU_SFC.RegisterSnapshot r, int height)
    {
        int missing = 0;
        for (int i = 0; i < 0x220; i++) if (!oam[i]) missing++;
        if (missing > 0) Report("snes.oam-uninit", "OAM (the sprite scan)", "oam", Main, curPc, $"sprites are on and {missing} of the 544 OAM bytes were never written (the PPU scans all 128 sprites)");
        var (small, large) = (r.Obsel >> 5) switch { 0 => (8, 16), 1 => (8, 32), 2 => (8, 64), 3 => (16, 32), 4 => (16, 64), 5 => (32, 64), 6 => (16, 32), _ => (16, 32) };
        int nameBase = (r.Obsel & 7) << 13, gap = ((r.Obsel >> 3) & 3) + 1 << 12;
        int charMissing = 0, palMissing = 0, firstChar = -1, firstPal = -1;
        checkedTiles.Clear();
        for (int s = 0; s < 128; s++)
        {
            if (!oam[s * 4] || !oam[s * 4 + 1] || !oam[s * 4 + 2] || !oam[s * 4 + 3]) continue;
            byte hiBits = (byte)(ppu.Oam[0x200 + (s >> 2)] >> ((s & 3) * 2));
            int x = ppu.Oam[s * 4] | (hiBits & 1) << 8, y = ppu.Oam[s * 4 + 1];
            int size = (hiBits & 2) != 0 ? large : small;
            if (x >= 256 && x + size <= 512) continue;          // off the left or right edge
            if (y >= height && y + size <= 256) continue;       // below the picture (and not wrapping to its top)
            int tile = ppu.Oam[s * 4 + 2] | (ppu.Oam[s * 4 + 3] & 1) << 8, pal = (ppu.Oam[s * 4 + 3] >> 1) & 7;
            for (int row = 0; row < size / 8; row++)
                for (int col = 0; col < size / 8; col++)
                {
                    int t = (tile & 0x100) | (((tile & 0xF0) + row * 16) & 0xF0) | ((tile + col) & 0x0F);
                    int word = (nameBase + (t & 0xFF) * 16 + ((t & 0x100) != 0 ? gap : 0)) & 0x7FFF;
                    if (!checkedTiles.Add((long)word << 8 | (long)pal)) continue;
                    if (!WordsWritten(word, 16)) { charMissing++; if (firstChar < 0) firstChar = word * 2; continue; }
                    int m = ColoursMissing(ppu, word, 4, 128 + pal * 16);
                    if (m >= 0) { palMissing++; if (firstPal < 0) firstPal = m; }
                }
        }
        if (charMissing > 0) Report("snes.vram-uninit", "sprite tiles", $"vram:${firstChar:X4}", Main, curPc, $"sprites on screen draw {charMissing} tiles never written (first vram:${firstChar:X4})");
        if (palMissing > 0) Report("snes.cgram-uninit", "sprite colours", $"cgram:${firstPal * 2:X3}", Main, curPc, $"sprites on screen draw with {palMissing} palettes holding colours never written (first colour {firstPal})");
    }

    private bool WordsWritten(int word, int count)
    {
        for (int i = 0; i < count; i++) { int b = ((word + i) & 0x7FFF) * 2; if (!vram[b] || !vram[b + 1]) return false; }
        return true;
    }

    /// <summary>The first colour (index into CGRAM) the tile draws with that was never written, or -1.</summary>
    private int ColoursMissing(PPU_SFC ppu, int word, int bpp, int colourBase)
    {
        Span<bool> used = stackalloc bool[256];
        for (int row = 0; row < 8; row++)
        {
            int p0 = ppu.Vram[(word + row) & 0x7FFF], p1 = bpp >= 4 ? ppu.Vram[(word + 8 + row) & 0x7FFF] : 0;
            int p2 = bpp == 8 ? ppu.Vram[(word + 16 + row) & 0x7FFF] : 0, p3 = bpp == 8 ? ppu.Vram[(word + 24 + row) & 0x7FFF] : 0;
            for (int px = 0; px < 8; px++)
            {
                int bit = 7 - px;
                int v = (p0 >> bit & 1) | (p0 >> (bit + 8) & 1) << 1;
                if (bpp >= 4) v |= (p1 >> bit & 1) << 2 | (p1 >> (bit + 8) & 1) << 3;
                if (bpp == 8) v |= (p2 >> bit & 1) << 4 | (p2 >> (bit + 8) & 1) << 5 | (p3 >> bit & 1) << 6 | (p3 >> (bit + 8) & 1) << 7;
                if (v != 0) used[v] = true;
            }
        }
        for (int v = 1; v < (1 << bpp); v++)
        {
            if (!used[v]) continue;
            int ci = (colourBase + v) & 0xFF;
            if (!cgram[ci * 2] || !cgram[ci * 2 + 1]) return ci;
        }
        return -1;
    }

    // ---------------------------------------------------------------- the report
    public override IEnumerable<(string name, long size, long written)> MemoryUse()
    {
        yield return ("wram", 0x20000, wram.Count());
        if (wramHi != null) yield return ("wramhi", wramHi.Size, wramHi.Count());
        yield return ("vram", 0x10000, vram.Count());
        yield return ("cgram", 0x200, cgram.Count());
        yield return ("oam", 0x220, oam.Count());
        yield return ("aram", 0x10000, aram.Count());
    }

    public override (long deepest, long free) StackUse() => stack.Lowest == uint.MaxValue ? (-1, -1) : (stack.Lowest, stack.Headroom(0));
}
