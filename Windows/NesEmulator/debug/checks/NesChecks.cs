using System;
using System.Collections.Generic;

namespace NesEmulator.Debugging.Checks;

/// <summary>
/// The NES rules (docs/projects/brokendev-debugging-plan.md, "D4 settled"). Fed by the D1 hooks: the per-instruction hook, the bus's slow
/// path (RAM masked onto it, so every RAM access is seen), OAM DMA and the precise-DMA model's DMC halts. The 6502's dummy reads are told
/// apart from its real reads by decoding each instruction before it runs: only its effective address, its pointer bytes and its stack
/// pulls are real reads.
/// </summary>
public sealed class NesChecks : ConsoleChecks
{
    private readonly NES nes;
    private readonly WrittenMap ram = new(0x800);
    private readonly StackCollisions page1 = new(0x100);

    public NesChecks(Debugger debugger, CheckOptions options) : base(debugger, options)
    {
        nes = ((NesDebugMachine)debugger.Machine).Nes;
    }

    public override string Family => "nes";
    public override double NominalFrameCycles => 29780.5;
    public override double VblankCycles => 20 * 341 / 3.0;
    public override string CycleUnit => "CPU cycles";

    private Bus? Bus => nes.DebugBus;
    private IPpuProbe? Ppu => nes.DebugBus?.ppu as IPpuProbe;
    private int Scanline => Ppu?.ProbeScanline ?? -1;
    private bool Rendering { get { var p = Ppu; if (p == null) return false; int s = p.ProbeScanline; return (p.ProbeMask & 0x18) != 0 && (s < 240 || s == 261); } }
    protected override bool InVblank() { int s = Scanline; return s >= 241 && s <= 260; }

    // ---------------------------------------------------------------- the instruction about to run
    private uint curPc;
    private byte curOp;
    private int curEa = -1;
    private bool curReadsEa;
    private readonly int[] realReads = new int[4];   // RAM-folded or register addresses this instruction really reads (beyond its EA)
    private int realReadCount;
    private bool curStackOp;
    private byte curS;
    private int stackKind;                            // +n pushes n, -n pulls n (for the wrap rule), 0 none
    private int lastStackKind; private byte lastS; private uint lastStackPc; private bool haveLastS;
    private int scanLast = -1;

    // frame-counter IRQ
    private bool irqPending, irqAcked; private uint irqPc; private long irqFrame;
    // DMC
    private readonly Dictionary<string, long> dmcHits = new(StringComparer.Ordinal);

    protected override bool FrameBoundary()
    {
        int s = Scanline;
        bool b = scanLast >= 0 && scanLast <= 240 && s >= 241 && s <= 260;
        scanLast = s;
        return b;
    }

    protected override void MainStep(DebugCpu cpu, long cycle, bool executes, string? interrupt, uint pc)
    {
        byte s = (byte)cpu.SP;
        // the stack pointer after the previous instruction: did it wrap?
        if (haveLastS && lastStackKind != 0)
        {
            bool wrapped = lastStackKind > 0 ? s > lastS : s < lastS;
            if (wrapped) Report("nes.stack-wrap", CodePlace(cpu, lastStackPc), "", cpu, lastStackPc,
                $"S went from ${lastS:X2} to ${s:X2} on a {(lastStackKind > 0 ? "push" : "pull")}");
        }
        haveLastS = true; lastS = s;
        realReadCount = 0; curEa = -1; curReadsEa = false; curStackOp = false; stackKind = 0; curS = s;

        if (!executes)
        {
            if (interrupt != null)
            {
                curStackOp = true; stackKind = 3; lastStackPc = pc;
                if (interrupt == "IRQ" && nes.GetApuStateProbe() is { } probe && probe.ProbeApuState().FrameIrqFlag && !irqPending)
                { irqPending = true; irqAcked = false; irqPc = pc; irqFrame = M.Frame; }
            }
            lastStackKind = stackKind;
            return;
        }

        curPc = pc;
        byte op = cpu.Peek(pc);
        curOp = op;
        byte lo = cpu.Peek((ushort)(pc + 1)), hi = cpu.Peek((ushort)(pc + 2));
        var g = nes.GetCpuRegs();
        byte x = g.X, y = g.Y;
        var mode = Op6502.Mode(op);

        if (!Op6502.Official(op))
            Report("nes.unofficial-opcode", CodePlace(cpu, pc), "", cpu, pc, $"opcode ${op:X2} ({Op6502.Name(op)})");

        // the instruction's real reads
        Span<byte> b = stackalloc byte[3] { op, lo, hi };
        int ea = Disasm6502.EffectiveAddress(b[..Op6502.Length(op)], x, y, a => cpu.Peek(a));
        curEa = ea; curReadsEa = ea >= 0 && Op6502.ReadsEa(op);
        switch (mode)
        {
            case Op6502.M.Izx: Add((byte)(lo + x)); Add((byte)(lo + x + 1)); break;
            case Op6502.M.Izy: Add(lo); Add((byte)(lo + 1)); break;
            case Op6502.M.Ind: Add(lo | hi << 8); Add((hi << 8) | (byte)(lo + 1)); break;
        }
        switch (op)
        {
            case 0x68: case 0x28: Add(0x100 | (byte)(s + 1)); stackKind = -1; break;                                   // PLA PLP
            case 0x60: Add(0x100 | (byte)(s + 1)); Add(0x100 | (byte)(s + 2)); stackKind = -2; break;                  // RTS
            case 0x40: Add(0x100 | (byte)(s + 1)); Add(0x100 | (byte)(s + 2)); Add(0x100 | (byte)(s + 3)); stackKind = -3;  // RTI
                if (irqPending)
                {
                    if (!irqAcked) Report("nes.frame-irq", CodePlace(cpu, pc), "", cpu, irqPc,
                        $"an IRQ taken at frame {irqFrame} with the frame counter's IRQ raised returned (RTI at ${pc:X4}) without reading $4015");
                    irqPending = false;
                }
                break;
            case 0x48: case 0x08: stackKind = 1; break;   // PHA PHP
            case 0x20: stackKind = 2; break;              // JSR
            case 0x00: stackKind = 3; break;              // BRK
        }
        curStackOp = stackKind != 0;
        if (curStackOp) lastStackPc = pc;
        lastStackKind = stackKind;

        void Add(int a) { if (realReadCount < realReads.Length) realReads[realReadCount++] = a < 0x2000 ? a & 0x7FF : a; }
    }

    private bool IsRealRead(uint address)
    {
        int a = address < 0x2000 ? (int)(address & 0x7FF) : (int)address;
        if (curReadsEa && curEa >= 0 && (curEa < 0x2000 ? curEa & 0x7FF : curEa) == a) return true;
        for (int i = 0; i < realReadCount; i++) if (realReads[i] == a) return true;
        return false;
    }

    public override void OnAccess(DebugCpu cpu, uint address, byte value, bool write, AccessSource source, int channel)
    {
        ushort a = (ushort)address;
        if (source == AccessSource.OamDma)
        {
            if (!write && a < 0x2000 && !ram[a & 0x7FF])
            {
                string place = MemoryPlace(cpu, a, "");
                if (place.Length == 0) place = $"OAM DMA from page ${a >> 8:X2}";
                Report("nes.ram-uninit", place, $"ram:${a & 0x7FF:X4}", cpu, curPc, $"read by OAM DMA from page ${a >> 8:X2} before it was written");
            }
            return;
        }
        if (a < 0x2000)
        {
            int r = a & 0x7FF;
            bool stack = r >= 0x100 && r < 0x200 && curStackOp && InStackWindow(r);
            if (write)
            {
                ram.Set(r);
                if (stack) { if (page1.Stack((uint)(r & 0xFF), true)) StackCollision(cpu, r, "the stack pushed onto a page-1 byte the game reads as data"); }
                else NoteMainWrite();
            }
            else if (IsRealRead(a))
            {
                if (!ram[r]) Report("nes.ram-uninit", MemoryPlace(cpu, (uint)r, $"ram:${r:X4}"), $"ram:${r:X4}", cpu, curPc, "read before it was ever written");
                if (r >= 0x100 && r < 0x200) { if (stack) page1.Stack((uint)(r & 0xFF), false); else page1.Data((uint)(r & 0xFF), false, curS); }
            }
            return;
        }
        if (write) NoteMainWrite();
        if (a < 0x4000)
        {
            int reg = 0x2000 | (a & 7);
            if (write)
            {
                if ((reg == 0x2007 || reg == 0x2003 || reg == 0x2004) && Rendering)
                    Report("nes.ppu-render-write", CodePlace(cpu, curPc), "", cpu, curPc, $"${reg:X4} written while the PPU renders (line {Scanline})");
                if (reg == 0x2007) NoteUpload(M.Cycle);
            }
            else if (IsRealRead(a))
            {
                if (reg == 0x2007)
                {
                    if (Rendering) Report("nes.ppu-render-write", CodePlace(cpu, curPc), "", cpu, curPc, $"$2007 read while the PPU renders (line {Scanline})");
                    DmcRead(cpu, 0x2007);
                }
                else if (reg is 0x2000 or 0x2001 or 0x2003 or 0x2005 or 0x2006)
                    Report("nes.open-bus", MemoryPlace(cpu, a, $"${reg:X4}"), $"${reg:X4}", cpu, curPc, $"read of the write-only PPU register ${reg:X4} (gives the PPU's open bus)");
            }
            return;
        }
        if (write)
        {
            if (a == 0x4014)
            {
                NoteDma(256);
                NoteUpload(M.Cycle + 513);
                var p = Ppu;
                if (p != null && (p.ProbeMask & 0x18) != 0)
                {
                    int s = p.ProbeScanline, dot = p.ProbeDot;
                    if (s < 240 || s == 261) Report("nes.oam-dma-render", CodePlace(cpu, curPc), "", cpu, curPc, $"OAM DMA started on line {s} while the PPU renders");
                    else if (s >= 241 && s <= 260 && (261 - s) * 341 - dot < 1542)
                        Report("nes.oam-dma-render", CodePlace(cpu, curPc), "", cpu, curPc, $"OAM DMA started on line {s} dot {dot}: its 513 cycles run into the pre-render line");
                }
            }
            else if (a == 0x4017 && (value & 0x40) != 0) irqAcked = true;
            else if (a >= 0x8000 && a < 0xC000 && nes.DebugCartridge is { } cart && cart.hasBattery && cart.mapper is Mapper30)
                Report("nes.flash-write", CodePlace(cpu, curPc), "", cpu, curPc, $"flash command ${value:X2} written to ${a:X4}");
            return;
        }
        if (!IsRealRead(a)) return;
        if (a == 0x4015) { irqAcked = true; return; }
        if (a is 0x4016 or 0x4017) { DmcRead(cpu, a); return; }
        bool open = a < 0x4015 || (a >= 0x4018 && a < 0x5000);
        if (!open && a >= 0x5000 && nes.DebugCartridge is { } c2) open = c2.IsCpuReadOpenBus(a);
        if (open) Report("nes.open-bus", MemoryPlace(cpu, a, $"${a:X4}"), $"${a:X4}", cpu, curPc,
            a < 0x4015 ? $"read of the write-only APU register ${a:X4}" : $"read of ${a:X4}, which nothing answers");
    }

    private bool InStackWindow(int r)
    {
        int lo = 0x100 | (byte)(curS - 4), hi = 0x100 | (byte)(curS + 4);
        return lo <= hi ? r >= lo && r <= hi : r >= lo || r <= hi;
    }

    private void StackCollision(DebugCpu cpu, int r, string what) =>
        Report("nes.stack-overflow", MemoryPlace(cpu, (uint)r, $"ram:${r:X4}"), $"ram:${r:X4}", cpu, curPc, $"{what} (S = ${curS:X2})");

    private void DmcRead(DebugCpu cpu, ushort reg)
    {
        if (nes.GetApuStateProbe() is not { } probe || probe.ProbeApuState().DmcBytesRemaining == 0) return;
        var f = Report("nes.dmc-conflict", CodePlace(cpu, curPc), "", cpu, curPc, "");
        if (f != null) f.Detail = $"${reg:X4} read while a DMC sample plays ({(dmcHits.TryGetValue(f.Key, out long h) ? h : 0)} of these reads were hit by a DMC DMA in this run)";
    }

    public override void OnDmcHalt(DebugCpu cpu, uint address)
    {
        if (address is not (0x4016 or 0x4017) && (address < 0x2000 || address >= 0x4000 || (address & 7) != 7)) return;
        string key = "nes.dmc-conflict|" + CodePlace(cpu, curPc);
        dmcHits[key] = (dmcHits.TryGetValue(key, out long h) ? h : 0) + 1;
    }

    public override void Finish()
    {
        base.Finish();
        foreach (var f in Findings)
            if (f.Rule == "nes.dmc-conflict")
            {
                long h = dmcHits.TryGetValue(f.Key, out long x) ? x : 0;
                f.Detail = $"a controller or $2007 read while a DMC sample plays, {f.Count} times ({h} of them hit by a DMC DMA in this run)";
            }
    }

    public override IEnumerable<(string name, long size, long written)> MemoryUse()
    {
        yield return ("ram", 0x800, ram.Count());
    }

    public override (long deepest, long free) StackUse() =>
        page1.Lowest == uint.MaxValue ? (-1, -1) : (0x100 + page1.Lowest, page1.Headroom(0));
}

/// <summary>The 6502's opcode table for the checks: addressing modes, lengths, the official set, which instructions read their effective address.</summary>
internal static class Op6502
{
    public enum M : byte { Imp, Acc, Imm, Zp, Zpx, Zpy, Abs, Abx, Aby, Ind, Izx, Izy, Rel }
    private static readonly M[] Modes = Build();
    private static readonly bool[] official = new bool[256];
    private static readonly string[] Names = (
        "BRK ORA STP SLO NOP ORA ASL SLO PHP ORA ASL ANC NOP ORA ASL SLO BPL ORA STP SLO NOP ORA ASL SLO CLC ORA NOP SLO NOP ORA ASL SLO " +
        "JSR AND STP RLA BIT AND ROL RLA PLP AND ROL ANC BIT AND ROL RLA BMI AND STP RLA NOP AND ROL RLA SEC AND NOP RLA NOP AND ROL RLA " +
        "RTI EOR STP SRE NOP EOR LSR SRE PHA EOR LSR ALR JMP EOR LSR SRE BVC EOR STP SRE NOP EOR LSR SRE CLI EOR NOP SRE NOP EOR LSR SRE " +
        "RTS ADC STP RRA NOP ADC ROR RRA PLA ADC ROR ARR JMP ADC ROR RRA BVS ADC STP RRA NOP ADC ROR RRA SEI ADC NOP RRA NOP ADC ROR RRA " +
        "NOP STA NOP SAX STY STA STX SAX DEY NOP TXA XAA STY STA STX SAX BCC STA STP AHX STY STA STX SAX TYA STA TXS TAS SHY STA SHX AHX " +
        "LDY LDA LDX LAX LDY LDA LDX LAX TAY LDA TAX LAX LDY LDA LDX LAX BCS LDA STP LAX LDY LDA LDX LAX CLV LDA TSX LAS LDY LDA LDX LAX " +
        "CPY CMP NOP DCP CPY CMP DEC DCP INY CMP DEX AXS CPY CMP DEC DCP BNE CMP STP DCP NOP CMP DEC DCP CLD CMP NOP DCP NOP CMP DEC DCP " +
        "CPX SBC NOP ISC CPX SBC INC ISC INX SBC NOP SBC CPX SBC INC ISC BEQ SBC STP ISC NOP SBC INC ISC SED SBC NOP ISC NOP SBC INC ISC").Split(' ');

    static Op6502()
    {
        foreach (var hx in ("00 01 05 06 08 09 0A 0D 0E 10 11 15 16 18 19 1D 1E 20 21 24 25 26 28 29 2A 2C 2D 2E 30 31 35 36 38 39 3D 3E " +
                            "40 41 45 46 48 49 4A 4C 4D 4E 50 51 55 56 58 59 5D 5E 60 61 65 66 68 69 6A 6C 6D 6E 70 71 75 76 78 79 7D 7E " +
                            "81 84 85 86 88 8A 8C 8D 8E 90 91 94 95 96 98 99 9A 9D A0 A1 A2 A4 A5 A6 A8 A9 AA AC AD AE B0 B1 B4 B5 B6 B8 B9 BA BC BD BE " +
                            "C0 C1 C4 C5 C6 C8 C9 CA CC CD CE D0 D1 D5 D6 D8 D9 DD DE E0 E1 E4 E5 E6 E8 E9 EA EC ED EE F0 F1 F5 F6 F8 F9 FD FE").Split(' '))
            official[Convert.ToInt32(hx, 16)] = true;
    }

    private static M[] Build()
    {
        var m = new M[256];
        for (int op = 0; op < 256; op++)
        {
            m[op] = (op & 0x1F) switch
            {
                0x00 => op == 0x20 ? M.Abs : op >= 0x80 ? M.Imm : M.Imp,
                0x01 or 0x03 => M.Izx,
                0x02 => op >= 0x80 ? M.Imm : M.Imp,
                0x04 or 0x05 or 0x06 or 0x07 => M.Zp,
                0x08 => M.Imp,
                0x09 or 0x0B => M.Imm,
                0x0A => op < 0x80 ? M.Acc : M.Imp,
                0x0C or 0x0D or 0x0E or 0x0F => M.Abs,
                0x10 => M.Rel,
                0x11 or 0x13 => M.Izy,
                0x12 => M.Imp,
                0x14 or 0x15 or 0x16 or 0x17 => M.Zpx,
                0x18 or 0x1A => M.Imp,
                0x19 or 0x1B => M.Aby,
                _ => M.Abx,
            };
        }
        m[0x6C] = M.Ind;
        m[0x96] = m[0x97] = m[0xB6] = m[0xB7] = M.Zpy;
        m[0x9E] = m[0x9F] = m[0xBE] = m[0xBF] = M.Aby;
        return m;
    }

    public static M Mode(byte op) => Modes[op];
    public static bool Official(byte op) => official[op];
    public static string Name(byte op) => Names[op];
    public static int Length(byte op) => Modes[op] switch { M.Imp or M.Acc => 1, M.Abs or M.Abx or M.Aby or M.Ind => 3, _ => 2 };

    /// <summary>The instruction reads memory at its effective address (loads, compares, read-modify-writes; not stores, jumps or the unofficial NOPs).</summary>
    public static bool ReadsEa(byte op)
    {
        var m = Modes[op];
        if (m is M.Imp or M.Acc or M.Imm or M.Rel or M.Ind || op is 0x20 or 0x4C) return false;
        int cc = op & 3, aaa = op >> 5;
        return cc switch
        {
            1 => aaa != 4,                                         // all but STA
            2 => aaa != 4,                                         // ASL ROL LSR ROR LDX DEC INC (not STX / SHX)
            0 => op is 0x24 or 0x2C || aaa is 5 or 6 or 7,        // BIT, LDY, CPY, CPX (the unofficial NOPs read but use nothing)
            _ => aaa != 4,                                         // the unofficial RMW combos and LAX / LAS (not SAX / SHA / TAS)
        };
    }
}
