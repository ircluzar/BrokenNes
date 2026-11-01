using System;
using System.Text;

namespace NesEmulator.Debugging;

/// <summary>What an instruction does to the flow of control (the call stack and step over / out are built on it).</summary>
public enum FlowKind : byte { None, Call, Return, Jump, Branch, Software }

/// <summary>The processor state an instruction's length depends on (65816 M, X and E).</summary>
public readonly struct DecodeContext
{
    public readonly bool M8, X8, E;
    public DecodeContext(bool m8, bool x8, bool e) { M8 = m8; X8 = x8; E = e; }
    public static readonly DecodeContext Default = new(true, true, true);
}

/// <summary>An instruction's length and its effect on the flow (no strings: called on every instruction while the debugger is armed).</summary>
public readonly struct InstrClass
{
    public readonly int Length;
    public readonly FlowKind Flow;
    /// <summary>Bytes a taken call pushes (the return address, plus the status / bank for BRK, COP, JSL).</summary>
    public readonly int PushBytes;
    public InstrClass(int length, FlowKind flow, int pushBytes) { Length = length; Flow = flow; PushBytes = pushBytes; }
}

/// <summary>A disassembler for one CPU family. <c>bytes</c> are the instruction's bytes from its first (side-effect-free peeks).</summary>
public interface IDebugDisassembler
{
    /// <summary>Length and flow of the instruction starting with <paramref name="op"/> (<paramref name="op1"/> is the next byte, the SM83's CB page).</summary>
    InstrClass Classify(byte op, byte op1, in DecodeContext ctx);
    /// <summary>"LDA #$00", "JSR $8123", ... <paramref name="label"/> names an absolute operand address when a symbol is there (null: none).</summary>
    string Format(uint pc, ReadOnlySpan<byte> bytes, in DecodeContext ctx, Func<uint, string?>? label);
}

// =====================================================================================================================================
//  6502 (NES)
// =====================================================================================================================================

public sealed class Disasm6502 : IDebugDisassembler
{
    public static readonly Disasm6502 Instance = new();
    private enum M : byte { Imp, Acc, Imm, Zp, Zpx, Zpy, Abs, Abx, Aby, Ind, Izx, Izy, Rel }

    private static readonly string[] Names = (
        "BRK ORA STP SLO NOP ORA ASL SLO PHP ORA ASL ANC NOP ORA ASL SLO " +
        "BPL ORA STP SLO NOP ORA ASL SLO CLC ORA NOP SLO NOP ORA ASL SLO " +
        "JSR AND STP RLA BIT AND ROL RLA PLP AND ROL ANC BIT AND ROL RLA " +
        "BMI AND STP RLA NOP AND ROL RLA SEC AND NOP RLA NOP AND ROL RLA " +
        "RTI EOR STP SRE NOP EOR LSR SRE PHA EOR LSR ALR JMP EOR LSR SRE " +
        "BVC EOR STP SRE NOP EOR LSR SRE CLI EOR NOP SRE NOP EOR LSR SRE " +
        "RTS ADC STP RRA NOP ADC ROR RRA PLA ADC ROR ARR JMP ADC ROR RRA " +
        "BVS ADC STP RRA NOP ADC ROR RRA SEI ADC NOP RRA NOP ADC ROR RRA " +
        "NOP STA NOP SAX STY STA STX SAX DEY NOP TXA XAA STY STA STX SAX " +
        "BCC STA STP AHX STY STA STX SAX TYA STA TXS TAS SHY STA SHX AHX " +
        "LDY LDA LDX LAX LDY LDA LDX LAX TAY LDA TAX LAX LDY LDA LDX LAX " +
        "BCS LDA STP LAX LDY LDA LDX LAX CLV LDA TSX LAS LDY LDA LDX LAX " +
        "CPY CMP NOP DCP CPY CMP DEC DCP INY CMP DEX AXS CPY CMP DEC DCP " +
        "BNE CMP STP DCP NOP CMP DEC DCP CLD CMP NOP DCP NOP CMP DEC DCP " +
        "CPX SBC NOP ISC CPX SBC INC ISC INX SBC NOP SBC CPX SBC INC ISC " +
        "BEQ SBC STP ISC NOP SBC INC ISC SED SBC NOP ISC NOP SBC INC ISC").Split(' ');

    // Modes in the 6502's regular pattern: column by column, with the few exceptions patched below.
    private static readonly M[] Modes = BuildModes();

    private static M[] BuildModes()
    {
        var m = new M[256];
        for (int op = 0; op < 256; op++)
        {
            int col = op & 0x1F;
            m[op] = col switch
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
                _ => M.Abx,   // 0x1C-0x1F
            };
        }
        m[0x6C] = M.Ind;
        m[0x96] = m[0x97] = m[0xB6] = m[0xB7] = M.Zpy;   // STX/SAX/LDX/LAX zp,Y
        m[0x9E] = m[0x9F] = m[0xBE] = m[0xBF] = M.Aby;   // SHX/AHX/LDX/LAX abs,Y
        return m;
    }

    private static int Len(M m) => m switch { M.Imp or M.Acc => 1, M.Abs or M.Abx or M.Aby or M.Ind => 3, _ => 2 };

    public InstrClass Classify(byte op, byte op1, in DecodeContext ctx) => op switch
    {
        0x20 => new InstrClass(3, FlowKind.Call, 2),
        0x00 => new InstrClass(2, FlowKind.Software, 3),
        0x60 or 0x40 => new InstrClass(1, FlowKind.Return, 0),
        0x4C or 0x6C => new InstrClass(3, FlowKind.Jump, 0),
        _ => new InstrClass(Len(Modes[op]), Modes[op] == M.Rel ? FlowKind.Branch : FlowKind.None, 0),
    };

    public string Format(uint pc, ReadOnlySpan<byte> b, in DecodeContext ctx, Func<uint, string?>? label)
    {
        byte op = b[0];
        string n = Names[op];
        byte lo = b.Length > 1 ? b[1] : (byte)0, hi = b.Length > 2 ? b[2] : (byte)0;
        ushort w = (ushort)(lo | hi << 8);
        string A16(ushort a) => label?.Invoke(a) ?? "$" + a.ToString("X4");
        string A8(byte a) => label?.Invoke(a) ?? "$" + a.ToString("X2");
        return Modes[op] switch
        {
            M.Imp => op == 0x00 ? "BRK" : n,
            M.Acc => n + " A",
            M.Imm => $"{n} #${lo:X2}",
            M.Zp => $"{n} {A8(lo)}",
            M.Zpx => $"{n} {A8(lo)},X",
            M.Zpy => $"{n} {A8(lo)},Y",
            M.Abs => $"{n} {A16(w)}",
            M.Abx => $"{n} {A16(w)},X",
            M.Aby => $"{n} {A16(w)},Y",
            M.Ind => $"{n} ({A16(w)})",
            M.Izx => $"{n} ({A8(lo)},X)",
            M.Izy => $"{n} ({A8(lo)}),Y",
            _ => $"{n} {A16((ushort)(pc + 2 + (sbyte)lo))}",
        };
    }

    /// <summary>The address the instruction reads or writes (before it runs, from its registers), or -1. For the trace's [address] = value column.</summary>
    public static int EffectiveAddress(ReadOnlySpan<byte> b, byte x, byte y, Func<ushort, byte> peek)
    {
        byte op = b[0];
        byte lo = b.Length > 1 ? b[1] : (byte)0, hi = b.Length > 2 ? b[2] : (byte)0;
        ushort w = (ushort)(lo | hi << 8);
        if (op is 0x4C or 0x20) return -1;   // JMP / JSR abs: the operand is the target, not an access
        return Modes[op] switch
        {
            M.Zp => lo,
            M.Zpx => (byte)(lo + x),
            M.Zpy => (byte)(lo + y),
            M.Abs => w,
            M.Abx => (ushort)(w + x),
            M.Aby => (ushort)(w + y),
            M.Izx => peek((byte)(lo + x)) | peek((byte)(lo + x + 1)) << 8,
            M.Izy => (ushort)((peek(lo) | peek((byte)(lo + 1)) << 8) + y),
            _ => -1,
        };
    }
}

// =====================================================================================================================================
//  65816 (SNES)
// =====================================================================================================================================

public sealed class Disasm65816 : IDebugDisassembler
{
    public static readonly Disasm65816 Instance = new();
    public enum M : byte { Imp, Acc, ImmM, ImmX, Imm8, Dp, Dpx, Dpy, Idp, Idx, Idy, Ildp, Ildy, Sr, Isry, Abs, Abx, Aby, Ind, Iax, Ilab, Long, Longx, Rel, Rell, Blk, Pea, Pei }

    private static readonly string[] Names = (
        "BRK ORA COP ORA TSB ORA ASL ORA PHP ORA ASL PHD TSB ORA ASL ORA " +
        "BPL ORA ORA ORA TRB ORA ASL ORA CLC ORA INC TCS TRB ORA ASL ORA " +
        "JSR AND JSL AND BIT AND ROL AND PLP AND ROL PLD BIT AND ROL AND " +
        "BMI AND AND AND BIT AND ROL AND SEC AND DEC TSC BIT AND ROL AND " +
        "RTI EOR WDM EOR MVP EOR LSR EOR PHA EOR LSR PHK JMP EOR LSR EOR " +
        "BVC EOR EOR EOR MVN EOR LSR EOR CLI EOR PHY TCD JML EOR LSR EOR " +
        "RTS ADC PER ADC STZ ADC ROR ADC PLA ADC ROR RTL JMP ADC ROR ADC " +
        "BVS ADC ADC ADC STZ ADC ROR ADC SEI ADC PLY TDC JMP ADC ROR ADC " +
        "BRA STA BRL STA STY STA STX STA DEY BIT TXA PHB STY STA STX STA " +
        "BCC STA STA STA STY STA STX STA TYA STA TXS TXY STZ STA STZ STA " +
        "LDY LDA LDX LDA LDY LDA LDX LDA TAY LDA TAX PLB LDY LDA LDX LDA " +
        "BCS LDA LDA LDA LDY LDA LDX LDA CLV LDA TSX TYX LDY LDA LDX LDA " +
        "CPY CMP REP CMP CPY CMP DEC CMP INY CMP DEX WAI CPY CMP DEC CMP " +
        "BNE CMP CMP CMP PEI CMP DEC CMP CLD CMP PHX STP JML CMP DEC CMP " +
        "CPX SBC SEP SBC CPX SBC INC SBC INX SBC NOP XBA CPX SBC INC SBC " +
        "BEQ SBC SBC SBC PEA SBC INC SBC SED SBC PLX XCE JSR SBC INC SBC").Split(' ');

    public static readonly M[] Modes = BuildModes();

    private static M[] BuildModes()
    {
        var m = new M[256];
        for (int op = 0; op < 256; op++)
        {
            int col = op & 0x1F;
            m[op] = col switch
            {
                0x01 => M.Idx, 0x03 => M.Sr, 0x05 => M.Dp, 0x07 => M.Ildp, 0x09 => M.ImmM, 0x0D => M.Abs, 0x0F => M.Long,
                0x11 => M.Idy, 0x12 => M.Idp, 0x13 => M.Isry, 0x15 => M.Dpx, 0x17 => M.Ildy, 0x19 => M.Aby, 0x1D => M.Abx, 0x1F => M.Longx,
                0x10 => M.Rel,
                0x04 or 0x06 => M.Dp, 0x14 or 0x16 => M.Dpx,
                0x0C or 0x0E => M.Abs, 0x1C or 0x1E => M.Abx,
                0x0A => M.Acc, 0x1A => M.Acc,
                _ => M.Imp,
            };
        }
        // the irregular ones
        m[0x00] = m[0x02] = m[0x42] = m[0xC2] = m[0xE2] = M.Imm8;   // BRK, COP, WDM signatures; REP, SEP
        m[0x20] = M.Abs; m[0x22] = M.Long; m[0x44] = m[0x54] = M.Blk; m[0x4C] = M.Abs; m[0x5C] = M.Long;
        m[0x62] = M.Rell; m[0x82] = M.Rell; m[0x6C] = M.Ind; m[0x7C] = M.Iax; m[0xDC] = M.Ilab; m[0xFC] = M.Iax;
        m[0x80] = M.Rel; m[0x89] = M.ImmM;
        m[0xA0] = m[0xA2] = m[0xC0] = m[0xE0] = M.ImmX;
        m[0x96] = m[0xB6] = M.Dpy; m[0xBE] = M.Aby; m[0x9C] = M.Abs; m[0x9E] = M.Abx; m[0x64] = M.Dp; m[0x74] = M.Dpx;
        m[0x3A] = m[0x1A] = M.Acc;   // DEC A / INC A
        m[0x4A] = m[0x6A] = m[0x0A] = m[0x2A] = M.Acc;
        m[0xD4] = M.Pei; m[0xF4] = M.Pea;
        m[0x8A] = m[0x9A] = m[0xAA] = m[0xBA] = m[0xCA] = m[0xEA] = m[0x9B] = m[0xBB] = m[0xFA] = m[0xDA] = m[0x5A] = m[0x7A] = M.Imp;
        m[0x5B] = m[0x7B] = m[0x1B] = m[0x3B] = M.Imp;
        m[0xCB] = m[0xDB] = m[0xEB] = m[0xFB] = m[0x0B] = m[0x2B] = m[0x4B] = m[0x6B] = m[0x8B] = m[0xAB] = M.Imp;
        m[0xC8] = m[0xE8] = m[0x88] = M.Imp;
        return m;
    }

    public static int Len(M m, in DecodeContext c) => m switch
    {
        M.Imp or M.Acc => 1,
        M.ImmM => c.M8 ? 2 : 3,
        M.ImmX => c.X8 ? 2 : 3,
        M.Abs or M.Abx or M.Aby or M.Ind or M.Iax or M.Ilab or M.Rell or M.Blk or M.Pea => 3,
        M.Long or M.Longx => 4,
        _ => 2,
    };

    public InstrClass Classify(byte op, byte op1, in DecodeContext ctx)
    {
        int len = Len(Modes[op], ctx);
        return op switch
        {
            0x20 or 0xFC => new InstrClass(3, FlowKind.Call, 2),
            0x22 => new InstrClass(4, FlowKind.Call, 3),
            0x00 or 0x02 => new InstrClass(2, FlowKind.Software, ctx.E ? 3 : 4),
            0x60 or 0x6B or 0x40 => new InstrClass(1, FlowKind.Return, 0),
            0x4C or 0x5C or 0x6C or 0x7C or 0xDC => new InstrClass(len, FlowKind.Jump, 0),
            0x80 or 0x82 => new InstrClass(len, FlowKind.Jump, 0),
            _ => new InstrClass(len, Modes[op] == M.Rel ? FlowKind.Branch : FlowKind.None, 0),
        };
    }

    public string Format(uint pc, ReadOnlySpan<byte> b, in DecodeContext ctx, Func<uint, string?>? label)
    {
        byte op = b[0];
        string n = Names[op];
        uint b1 = b.Length > 1 ? b[1] : 0u, b2 = b.Length > 2 ? b[2] : 0u, b3 = b.Length > 3 ? b[3] : 0u;
        uint B(int i) => i == 1 ? b1 : i == 2 ? b2 : i == 3 ? b3 : 0u;
        uint w = B(1) | B(2) << 8, l = w | B(3) << 16;
        uint bank = pc & 0xFF0000;
        string A16(uint a) => label?.Invoke(a) ?? "$" + (a & 0xFFFF).ToString("X4");
        string A24(uint a) => label?.Invoke(a) ?? "$" + a.ToString("X6");
        string A8(uint a) => "$" + a.ToString("X2");
        var m = Modes[op];
        return m switch
        {
            M.Imp => n,
            M.Acc => n + " A",
            M.Imm8 => $"{n} #${B(1):X2}",
            M.ImmM => ctx.M8 ? $"{n} #${B(1):X2}" : $"{n} #${w:X4}",
            M.ImmX => ctx.X8 ? $"{n} #${B(1):X2}" : $"{n} #${w:X4}",
            M.Dp => $"{n} {A8(B(1))}",
            M.Dpx => $"{n} {A8(B(1))},X",
            M.Dpy => $"{n} {A8(B(1))},Y",
            M.Idp => $"{n} ({A8(B(1))})",
            M.Idx => $"{n} ({A8(B(1))},X)",
            M.Idy => $"{n} ({A8(B(1))}),Y",
            M.Ildp => $"{n} [{A8(B(1))}]",
            M.Ildy => $"{n} [{A8(B(1))}],Y",
            M.Sr => $"{n} {A8(B(1))},S",
            M.Isry => $"{n} ({A8(B(1))},S),Y",
            M.Abs => op is 0x20 or 0x4C ? $"{n} {A16(bank | w)}" : $"{n} {A16(w)}",
            M.Abx => $"{n} {A16(w)},X",
            M.Aby => $"{n} {A16(w)},Y",
            M.Ind => $"{n} ({A16(w)})",
            M.Iax => $"{n} ({A16(bank | w)},X)",
            M.Ilab => $"{n} [{A16(w)}]",
            M.Long => $"{n} {A24(l)}",
            M.Longx => $"{n} {A24(l)},X",
            M.Rel => $"{n} {A16(bank | (ushort)(pc + 2 + (sbyte)B(1)))}",
            M.Rell => $"{n} {A16(bank | (ushort)(pc + 3 + (short)w))}",
            M.Blk => $"{n} ${B(2):X2},${B(1):X2}",
            M.Pea => $"{n} ${w:X4}",
            M.Pei => $"{n} ({A8(B(1))})",
            _ => n,
        };
    }

    /// <summary>The 24-bit address the instruction's operand reaches (before it runs), or -1.</summary>
    public static long EffectiveAddress(ReadOnlySpan<byte> b, ushort x, ushort y, ushort d, ushort s, byte dbr, bool e, Func<uint, byte> peek)
    {
        byte op = b[0];
        uint b1 = b.Length > 1 ? b[1] : 0u, b2 = b.Length > 2 ? b[2] : 0u, b3 = b.Length > 3 ? b[3] : 0u;
        uint B(int i) => i == 1 ? b1 : i == 2 ? b2 : i == 3 ? b3 : 0u;
        uint w = B(1) | B(2) << 8, l = w | B(3) << 16;
        uint db = (uint)dbr << 16;
        uint Dp(uint o) => (d + o) & 0xFFFF;
        uint Ptr16(uint at) => peek(at) | (uint)peek((at + 1) & 0xFFFF) << 8;
        uint Ptr24(uint at) => Ptr16(at) | (uint)peek((at + 2) & 0xFFFF) << 16;
        if (op is 0x20 or 0x4C or 0x22 or 0x5C) return -1;
        return Modes[op] switch
        {
            M.Dp => Dp(B(1)),
            M.Dpx => Dp(B(1) + x),
            M.Dpy => Dp(B(1) + y),
            M.Idp => db | Ptr16(Dp(B(1))),
            M.Idx => db | Ptr16(Dp(B(1) + x)),
            M.Idy => (db | Ptr16(Dp(B(1)))) + y & 0xFFFFFF,
            M.Ildp => Ptr24(Dp(B(1))),
            M.Ildy => Ptr24(Dp(B(1))) + y & 0xFFFFFF,
            M.Sr => (s + B(1)) & 0xFFFF,
            M.Isry => (db | Ptr16((s + B(1)) & 0xFFFF)) + y & 0xFFFFFF,
            M.Abs => db | w,
            M.Abx => (db | w) + x & 0xFFFFFF,
            M.Aby => (db | w) + y & 0xFFFFFF,
            M.Long => l,
            M.Longx => l + x & 0xFFFFFF,
            _ => -1,
        };
    }
}

// =====================================================================================================================================
//  SPC700 (the SNES sound CPU)
// =====================================================================================================================================

public sealed class DisasmSpc700 : IDebugDisassembler
{
    public static readonly DisasmSpc700 Instance = new();

    /// <summary>Operand forms: d = direct page byte, a = absolute word, i = immediate, r = relative, s/t = second direct byte, m = mem.bit word, u = PCALL page byte.</summary>
    private static readonly string[] Table =
    {
        // 0x00
        "NOP", "TCALL 0", "SET1 d.0", "BBS d.0,r", "OR A,d", "OR A,!a", "OR A,(X)", "OR A,[d+X]", "OR A,#i", "OR t,s", "OR1 C,m", "ASL d", "ASL !a", "PUSH PSW", "TSET1 !a", "BRK",
        "BPL r", "TCALL 1", "CLR1 d.0", "BBC d.0,r", "OR A,d+X", "OR A,!a+X", "OR A,!a+Y", "OR A,[d]+Y", "OR t,#i", "OR (X),(Y)", "DECW d", "ASL d+X", "ASL A", "DEC X", "CMP X,!a", "JMP [!a+X]",
        "CLRP", "TCALL 2", "SET1 d.1", "BBS d.1,r", "AND A,d", "AND A,!a", "AND A,(X)", "AND A,[d+X]", "AND A,#i", "AND t,s", "OR1 C,/m", "ROL d", "ROL !a", "PUSH A", "CBNE d,r", "BRA r",
        "BMI r", "TCALL 3", "CLR1 d.1", "BBC d.1,r", "AND A,d+X", "AND A,!a+X", "AND A,!a+Y", "AND A,[d]+Y", "AND t,#i", "AND (X),(Y)", "INCW d", "ROL d+X", "ROL A", "INC X", "CMP X,d", "CALL !a",
        "SETP", "TCALL 4", "SET1 d.2", "BBS d.2,r", "EOR A,d", "EOR A,!a", "EOR A,(X)", "EOR A,[d+X]", "EOR A,#i", "EOR t,s", "AND1 C,m", "LSR d", "LSR !a", "PUSH X", "TCLR1 !a", "PCALL u",
        "BVC r", "TCALL 5", "CLR1 d.2", "BBC d.2,r", "EOR A,d+X", "EOR A,!a+X", "EOR A,!a+Y", "EOR A,[d]+Y", "EOR t,#i", "EOR (X),(Y)", "CMPW YA,d", "LSR d+X", "LSR A", "MOV X,A", "CMP Y,!a", "JMP !a",
        "CLRC", "TCALL 6", "SET1 d.3", "BBS d.3,r", "CMP A,d", "CMP A,!a", "CMP A,(X)", "CMP A,[d+X]", "CMP A,#i", "CMP t,s", "AND1 C,/m", "ROR d", "ROR !a", "PUSH Y", "DBNZ d,r", "RET",
        "BVS r", "TCALL 7", "CLR1 d.3", "BBC d.3,r", "CMP A,d+X", "CMP A,!a+X", "CMP A,!a+Y", "CMP A,[d]+Y", "CMP t,#i", "CMP (X),(Y)", "ADDW YA,d", "ROR d+X", "ROR A", "MOV A,X", "CMP Y,d", "RETI",
        "SETC", "TCALL 8", "SET1 d.4", "BBS d.4,r", "ADC A,d", "ADC A,!a", "ADC A,(X)", "ADC A,[d+X]", "ADC A,#i", "ADC t,s", "EOR1 C,m", "DEC d", "DEC !a", "MOV Y,#i", "POP PSW", "MOV t,#i",
        "BCC r", "TCALL 9", "CLR1 d.4", "BBC d.4,r", "ADC A,d+X", "ADC A,!a+X", "ADC A,!a+Y", "ADC A,[d]+Y", "ADC t,#i", "ADC (X),(Y)", "SUBW YA,d", "DEC d+X", "DEC A", "MOV X,SP", "DIV YA,X", "XCN A",
        "EI", "TCALL 10", "SET1 d.5", "BBS d.5,r", "SBC A,d", "SBC A,!a", "SBC A,(X)", "SBC A,[d+X]", "SBC A,#i", "SBC t,s", "MOV1 C,m", "INC d", "INC !a", "CMP Y,#i", "POP A", "MOV (X)+,A",
        "BCS r", "TCALL 11", "CLR1 d.5", "BBC d.5,r", "SBC A,d+X", "SBC A,!a+X", "SBC A,!a+Y", "SBC A,[d]+Y", "SBC t,#i", "SBC (X),(Y)", "MOVW YA,d", "INC d+X", "INC A", "MOV SP,X", "DAS A", "MOV A,(X)+",
        "DI", "TCALL 12", "SET1 d.6", "BBS d.6,r", "MOV d,A", "MOV !a,A", "MOV (X),A", "MOV [d+X],A", "CMP X,#i", "MOV !a,X", "MOV1 m,C", "MOV d,Y", "MOV !a,Y", "MOV X,#i", "POP X", "MUL YA",
        "BNE r", "TCALL 13", "CLR1 d.6", "BBC d.6,r", "MOV d+X,A", "MOV !a+X,A", "MOV !a+Y,A", "MOV [d]+Y,A", "MOV d,X", "MOV d+Y,X", "MOVW d,YA", "MOV d+X,Y", "DEC Y", "MOV A,Y", "CBNE d+X,r", "DAA A",
        "CLRV", "TCALL 14", "SET1 d.7", "BBS d.7,r", "MOV A,d", "MOV A,!a", "MOV A,(X)", "MOV A,[d+X]", "MOV A,#i", "MOV X,!a", "NOT1 m", "MOV Y,d", "MOV Y,!a", "NOTC", "POP Y", "SLEEP",
        "BEQ r", "TCALL 15", "CLR1 d.7", "BBC d.7,r", "MOV A,d+X", "MOV A,!a+X", "MOV A,!a+Y", "MOV A,[d]+Y", "MOV X,d", "MOV X,d+Y", "MOV t,s", "MOV Y,d+X", "INC Y", "MOV Y,A", "DBNZ Y,r", "STOP",
    };

    // Instruction lengths, row = high nibble (from the operand forms in Table).
    private static readonly byte[] Lengths =
    {
        1,1,2,3, 2,3,1,2, 2,3,3,2, 3,1,3,1,
        2,1,2,3, 2,3,3,2, 3,1,2,2, 1,1,3,3,
        1,1,2,3, 2,3,1,2, 2,3,3,2, 3,1,3,2,
        2,1,2,3, 2,3,3,2, 3,1,2,2, 1,1,2,3,
        1,1,2,3, 2,3,1,2, 2,3,3,2, 3,1,3,2,
        2,1,2,3, 2,3,3,2, 3,1,2,2, 1,1,3,3,
        1,1,2,3, 2,3,1,2, 2,3,3,2, 3,1,3,1,
        2,1,2,3, 2,3,3,2, 3,1,2,2, 1,1,2,1,
        1,1,2,3, 2,3,1,2, 2,3,3,2, 3,2,1,3,
        2,1,2,3, 2,3,3,2, 3,1,2,2, 1,1,1,1,
        1,1,2,3, 2,3,1,2, 2,3,3,2, 3,2,1,1,
        2,1,2,3, 2,3,3,2, 3,1,2,2, 1,1,1,1,
        1,1,2,3, 2,3,1,2, 2,3,3,2, 3,2,1,1,
        2,1,2,3, 2,3,3,2, 2,2,2,2, 1,1,3,1,
        1,1,2,3, 2,3,1,2, 2,3,3,2, 3,1,1,1,
        2,1,2,3, 2,3,3,2, 2,2,3,2, 1,1,2,1,
    };

    public static int Length(byte op) => Lengths[op];

    public InstrClass Classify(byte op, byte op1, in DecodeContext ctx) => op switch
    {
        0x3F => new InstrClass(3, FlowKind.Call, 2),
        0x4F => new InstrClass(2, FlowKind.Call, 2),
        0x0F => new InstrClass(1, FlowKind.Software, 3),
        0x6F or 0x7F => new InstrClass(1, FlowKind.Return, 0),
        0x5F or 0x1F or 0x2F => new InstrClass(Lengths[op], FlowKind.Jump, 0),
        _ when (op & 0x0F) == 0x01 => new InstrClass(1, FlowKind.Call, 2),   // TCALL n
        _ => new InstrClass(Lengths[op], (op & 0x1F) == 0x10 || (op & 0x0F) == 0x03 || op is 0x2E or 0x6E or 0xDE or 0xFE ? FlowKind.Branch : FlowKind.None, 0),
    };

    public string Format(uint pc, ReadOnlySpan<byte> b, in DecodeContext ctx, Func<uint, string?>? label)
    {
        byte op = b[0];
        string t = Table[op];
        int len = Lengths[op];
        byte b1 = b.Length > 1 ? b[1] : (byte)0, b2 = b.Length > 2 ? b[2] : (byte)0, b3 = b.Length > 3 ? b[3] : (byte)0;
        byte B(int i) => i == 1 ? b1 : i == 2 ? b2 : i == 3 ? b3 : (byte)0;
        var sb = new StringBuilder(24);
        int next = 1;   // next operand byte
        // operand bytes come in order of appearance, except the two-operand forms (t,s and t,#i) whose source byte comes first
        bool srcFirst = t.Contains("t,s") || t.Contains("t,#i");
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            bool word = i + 1 < t.Length;
            if (c == '!' && i + 1 < t.Length && t[i + 1] == 'a')
            {
                ushort a = (ushort)(B(next) | B(next + 1) << 8); next += 2; i++;
                sb.Append('!').Append(label?.Invoke(a) ?? "$" + a.ToString("X4"));
            }
            else if (c == 'd' && IsOperandChar(t, i))
            { sb.Append('$').Append(B(next).ToString("X2")); next++; }
            else if (c == 't' && IsOperandChar(t, i))
            { sb.Append('$').Append(B(srcFirst ? 2 : next).ToString("X2")); if (!srcFirst) next++; }
            else if (c == 's' && IsOperandChar(t, i))
            { sb.Append('$').Append(B(1).ToString("X2")); }
            else if (c == 'i' && i > 0 && t[i - 1] == '#')
            { sb.Append('$').Append(B(srcFirst ? 1 : next).ToString("X2")); if (!srcFirst) next++; }
            else if (c == 'r' && IsOperandChar(t, i))
            {
                ushort target = (ushort)(pc + len + (sbyte)B(len - 1));
                sb.Append(label?.Invoke(target) ?? "$" + target.ToString("X4"));
            }
            else if (c == 'm' && IsOperandChar(t, i))
            {
                ushort mw = (ushort)(B(1) | B(2) << 8);
                sb.Append('$').Append((mw & 0x1FFF).ToString("X4")).Append('.').Append(mw >> 13);
            }
            else if (c == 'u' && IsOperandChar(t, i))
            { sb.Append("$FF").Append(B(1).ToString("X2")); }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // an operand letter, not part of a mnemonic or register name
    private static bool IsOperandChar(string t, int i)
    {
        int space = t.IndexOf(' ');
        if (space < 0 || i <= space) return false;
        char prev = t[i - 1], nextc = i + 1 < t.Length ? t[i + 1] : ' ';
        return !char.IsLetter(prev) && !char.IsLetter(nextc);
    }

    /// <summary>The memory the instruction will touch (before it runs): up to 4 (address, kind) pairs, kind 2 = read, 4 = write, 6 = both. Stores' dummy reads are not listed.</summary>
    public static int Accesses(ReadOnlySpan<byte> b, byte a, byte x, byte y, byte sp, bool p, Func<ushort, byte> peek, (ushort addr, byte kind)[] o)
    {
        byte op = b[0];
        byte b1 = b.Length > 1 ? b[1] : (byte)0, b2 = b.Length > 2 ? b[2] : (byte)0, b3 = b.Length > 3 ? b[3] : (byte)0;
        byte B(int i) => i == 1 ? b1 : i == 2 ? b2 : i == 3 ? b3 : (byte)0;
        ushort Dp(int off) => (ushort)((p ? 0x100 : 0) | (off & 0xFF));
        ushort Abs = (ushort)(B(1) | B(2) << 8);
        ushort Ptr(ushort at) => (ushort)(peek(at) | peek(Dp(at + 1)) << 8);
        int n = 0;
        void Add(int addr, int kind) { if (n < o.Length) o[n++] = ((ushort)addr, (byte)kind); }
        string t = Table[op];
        int sp1 = 0x100 | (byte)(sp + 1), sp0 = 0x100 | sp;
        bool store = t.StartsWith("MOV ") && (t.Length > 4 && t[4] != 'A' && t[4] != 'X' && t[4] != 'Y' && !t.StartsWith("MOV SP") && !t.StartsWith("MOV1 C"));
        bool cmp = t.StartsWith("CMP") || t.StartsWith("CMPW") || t.StartsWith("BBS") || t.StartsWith("BBC") || t.StartsWith("CBNE");
        bool rmwTarget = !store && !cmp && (t.StartsWith("OR t") || t.StartsWith("AND t") || t.StartsWith("EOR t") || t.StartsWith("ADC t") || t.StartsWith("SBC t")
                         || t.StartsWith("OR (X),(Y)") || t.StartsWith("AND (X),(Y)") || t.StartsWith("EOR (X),(Y)") || t.StartsWith("ADC (X),(Y)") || t.StartsWith("SBC (X),(Y)"));
        int memKind = store ? 4 : 2;
        switch (op)
        {
            // stack
            case 0x0D: case 0x2D: case 0x4D: case 0x6D: Add(sp0, 4); return n;
            case 0x8E: case 0xAE: case 0xCE: case 0xEE: Add(sp1, 2); return n;
            case 0x3F: case 0x4F: Add(sp0, 4); Add(0x100 | (byte)(sp - 1), 4); return n;
            case 0x0F: Add(sp0, 4); Add(0x100 | (byte)(sp - 1), 4); Add(0x100 | (byte)(sp - 2), 4); Add(0xFFDE, 2); return n;
            case 0x6F: Add(sp1, 2); Add(0x100 | (byte)(sp + 2), 2); return n;
            case 0x7F: Add(sp1, 2); Add(0x100 | (byte)(sp + 2), 2); Add(0x100 | (byte)(sp + 3), 2); return n;
            case 0x1F: { ushort at = (ushort)(Abs + x); Add(at, 2); Add((ushort)(at + 1), 2); return n; }   // JMP [!a+X]: the pointer
            // 16-bit
            case 0x1A: case 0x3A: Add(Dp(B(1)), 6); Add(Dp(B(1) + 1), 6); return n;          // DECW / INCW
            case 0x5A: case 0x7A: case 0x9A: case 0xBA: Add(Dp(B(1)), 2); Add(Dp(B(1) + 1), 2); return n;   // CMPW / ADDW / SUBW / MOVW YA,d
            case 0xDA: Add(Dp(B(1)), 4); Add(Dp(B(1) + 1), 4); return n;                        // MOVW d,YA
            // bit ops on absolute bits
            case 0x0A: case 0x2A: case 0x4A: case 0x6A: case 0x8A: case 0xAA: Add((B(1) | B(2) << 8) & 0x1FFF, 2); return n;
            case 0xCA: case 0xEA: Add((B(1) | B(2) << 8) & 0x1FFF, 6); return n;
            case 0x0E: case 0x4E: Add(Abs, 6); return n;   // TSET1 / TCLR1
        }
        if ((op & 0x0F) == 0x01) { Add(sp0, 4); Add(0x100 | (byte)(sp - 1), 4); Add(0xFFDE - 2 * (op >> 4), 2); return n; }   // TCALL
        if ((op & 0x0F) == 0x02) { Add(Dp(B(1)), 6); return n; }   // SET1 / CLR1
        if ((op & 0x0F) == 0x03) { Add(Dp(B(1)), 2); return n; }   // BBS / BBC
        if (op is 0x2E or 0x6E) { Add(Dp(B(1)), op == 0x6E ? 6 : 2); return n; }   // CBNE d / DBNZ d
        if (op == 0xDE) { Add(Dp(B(1) + x), 2); return n; }
        if (t.Contains("t,s")) { Add(Dp(B(1)), 2); Add(Dp(B(2)), op == 0xFA ? 4 : cmp ? 2 : 6); return n; }
        if (t.Contains("t,#i")) { Add(Dp(B(2)), op == 0x8F ? 4 : cmp ? 2 : 6); return n; }
        if (t.Contains("(X),(Y)")) { Add(Dp(y), 2); Add(Dp(x), cmp ? 2 : 6); return n; }
        bool rmw = t.StartsWith("ASL") || t.StartsWith("ROL") || t.StartsWith("LSR") || t.StartsWith("ROR") || t.StartsWith("INC") || t.StartsWith("DEC");
        if (rmw && (t.EndsWith(" A") || t.EndsWith(" X") || t.EndsWith(" Y"))) return n;
        int kind = rmw ? 6 : rmwTarget ? 6 : memKind;
        if (t.Contains("[d+X]")) { ushort at = Dp(B(1) + x); Add(at, 2); Add(Dp(at + 1), 2); Add(Ptr(at), kind); return n; }
        if (t.Contains("[d]+Y")) { ushort at = Dp(B(1)); Add(at, 2); Add(Dp(at + 1), 2); Add((ushort)(Ptr(at) + y), kind); return n; }
        if (t.Contains("!a+X")) { Add((ushort)(Abs + x), kind); return n; }
        if (t.Contains("!a+Y")) { Add((ushort)(Abs + y), kind); return n; }
        if (t.Contains("!a")) { Add(Abs, kind); return n; }
        if (t.Contains("(X)+")) { Add(Dp(x), kind); return n; }
        if (t.Contains("(X)")) { Add(Dp(x), kind); return n; }
        if (t.Contains("d+X")) { Add(Dp(B(1) + x), kind); return n; }
        if (t.Contains("d+Y")) { Add(Dp(B(1) + y), kind); return n; }
        if (t.Contains(",d") || t.Contains(" d,") || t.EndsWith(" d")) { Add(Dp(B(1)), kind); return n; }
        return n;
    }
}

// =====================================================================================================================================
//  SM83 (Game Boy)
// =====================================================================================================================================

public sealed class DisasmSm83 : IDebugDisassembler
{
    public static readonly DisasmSm83 Instance = new();
    private static readonly string[] R = { "B", "C", "D", "E", "H", "L", "(HL)", "A" };
    private static readonly string[] RP = { "BC", "DE", "HL", "SP" };
    private static readonly string[] RP2 = { "BC", "DE", "HL", "AF" };
    private static readonly string[] CC = { "NZ", "Z", "NC", "C" };
    private static readonly string[] Alu = { "ADD A,", "ADC A,", "SUB ", "SBC A,", "AND ", "XOR ", "OR ", "CP " };
    private static readonly string[] Rot = { "RLC", "RRC", "RL", "RR", "SLA", "SRA", "SWAP", "SRL" };
    private static readonly byte[] Lengths = BuildLengths();

    private static byte[] BuildLengths()
    {
        var l = new byte[256];
        for (int op = 0; op < 256; op++)
        {
            int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
            int n = 1;
            if (x == 0)
            {
                if (z == 0) n = y == 0 ? 1 : y == 1 ? 3 : y == 2 ? 2 : 2;   // NOP, LD (nn),SP, STOP, JR...
                else if (z == 1) n = (y & 1) == 0 ? 3 : 1;
                else if (z == 6) n = 2;
            }
            else if (x == 3)
            {
                if (z == 0) n = y >= 4 && (y == 4 || y == 6 || y == 5 || y == 7) ? 2 : 1;   // LDH (n),A / ADD SP,e / LDH A,(n) / LD HL,SP+e
                else if (z == 2) n = y < 4 ? 3 : (y == 5 || y == 7) ? 3 : 1;
                else if (z == 3) n = y == 0 ? 3 : y == 1 ? 2 : 1;
                else if (z == 4) n = y < 4 ? 3 : 1;
                else if (z == 5) n = y == 1 ? 3 : 1;
                else if (z == 6) n = 2;
            }
            l[op] = (byte)n;
        }
        return l;
    }

    public InstrClass Classify(byte op, byte op1, in DecodeContext ctx)
    {
        int len = Lengths[op];
        switch (op)
        {
            case 0xCD: case 0xC4: case 0xCC: case 0xD4: case 0xDC: return new InstrClass(3, FlowKind.Call, 2);
            case 0xC9: case 0xD9: case 0xC0: case 0xC8: case 0xD0: case 0xD8: return new InstrClass(1, FlowKind.Return, 0);
            case 0xC3: case 0xE9: case 0xC2: case 0xCA: case 0xD2: case 0xDA: case 0x18: return new InstrClass(len, FlowKind.Jump, 0);
            case 0x20: case 0x28: case 0x30: case 0x38: return new InstrClass(2, FlowKind.Branch, 0);
        }
        if ((op & 0xC7) == 0xC7) return new InstrClass(1, FlowKind.Call, 2);   // RST
        return new InstrClass(len, FlowKind.None, 0);
    }

    public string Format(uint pc, ReadOnlySpan<byte> b, in DecodeContext ctx, Func<uint, string?>? label)
    {
        byte op = b[0];
        byte n = b.Length > 1 ? b[1] : (byte)0;
        ushort nn = (ushort)(n | (b.Length > 2 ? b[2] : 0) << 8);
        string A16(ushort a) => label?.Invoke(a) ?? "$" + a.ToString("X4");
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7, p = y >> 1, q = y & 1;
        if (op == 0xCB)
        {
            int cx = n >> 6, cy = (n >> 3) & 7, cz = n & 7;
            return cx switch { 0 => $"{Rot[cy]} {R[cz]}", 1 => $"BIT {cy},{R[cz]}", 2 => $"RES {cy},{R[cz]}", _ => $"SET {cy},{R[cz]}" };
        }
        switch (x)
        {
            case 0:
                switch (z)
                {
                    case 0:
                        return y switch
                        {
                            0 => "NOP", 1 => $"LD ({A16(nn)}),SP", 2 => "STOP", 3 => $"JR {A16((ushort)(pc + 2 + (sbyte)n))}",
                            _ => $"JR {CC[y - 4]},{A16((ushort)(pc + 2 + (sbyte)n))}",
                        };
                    case 1: return q == 0 ? $"LD {RP[p]},${nn:X4}" : $"ADD HL,{RP[p]}";
                    case 2:
                        string mem = p switch { 0 => "(BC)", 1 => "(DE)", 2 => "(HL+)", _ => "(HL-)" };
                        return q == 0 ? $"LD {mem},A" : $"LD A,{mem}";
                    case 3: return q == 0 ? $"INC {RP[p]}" : $"DEC {RP[p]}";
                    case 4: return $"INC {R[y]}";
                    case 5: return $"DEC {R[y]}";
                    case 6: return $"LD {R[y]},${n:X2}";
                    default: return new[] { "RLCA", "RRCA", "RLA", "RRA", "DAA", "CPL", "SCF", "CCF" }[y];
                }
            case 1: return op == 0x76 ? "HALT" : $"LD {R[y]},{R[z]}";
            case 2: return Alu[y] + R[z];
            default:
                switch (z)
                {
                    case 0:
                        return y switch
                        {
                            < 4 => $"RET {CC[y]}", 4 => $"LDH (${n:X2}),A", 5 => $"ADD SP,{(sbyte)n}", 6 => $"LDH A,(${n:X2})", _ => $"LD HL,SP{((sbyte)n >= 0 ? "+" : "")}{(sbyte)n}",
                        };
                    case 1:
                        if (q == 0) return $"POP {RP2[p]}";
                        return p switch { 0 => "RET", 1 => "RETI", 2 => "JP HL", _ => "LD SP,HL" };
                    case 2:
                        return y switch { < 4 => $"JP {CC[y]},{A16(nn)}", 4 => "LD (C),A", 5 => $"LD ({A16(nn)}),A", 6 => "LD A,(C)", _ => $"LD A,({A16(nn)})" };
                    case 3:
                        return y switch { 0 => $"JP {A16(nn)}", 6 => "DI", 7 => "EI", _ => $"DB ${op:X2}" };
                    case 4: return y < 4 ? $"CALL {CC[y]},{A16(nn)}" : $"DB ${op:X2}";
                    case 5:
                        if (q == 0) return $"PUSH {RP2[p]}";
                        return p == 0 ? $"CALL {A16(nn)}" : $"DB ${op:X2}";
                    case 6: return Alu[y] + $"${n:X2}";
                    default: return $"RST ${y * 8:X2}";
                }
        }
    }

    /// <summary>The address the instruction reads or writes (before it runs), or -1.</summary>
    public static int EffectiveAddress(ReadOnlySpan<byte> b, byte bReg, byte c, byte d, byte e, byte h, byte l, ushort sp)
    {
        byte op = b[0];
        byte n = b.Length > 1 ? b[1] : (byte)0;
        ushort nn = (ushort)(n | (b.Length > 2 ? b[2] : 0) << 8);
        ushort hl = (ushort)(h << 8 | l);
        if (op == 0xCB) return (n & 7) == 6 ? hl : -1;
        switch (op)
        {
            case 0x02: case 0x0A: return bReg << 8 | c;
            case 0x12: case 0x1A: return d << 8 | e;
            case 0x22: case 0x2A: case 0x32: case 0x3A: case 0x34: case 0x35: case 0x36: return hl;
            case 0x08: case 0xEA: case 0xFA: return nn;
            case 0xE0: case 0xF0: return 0xFF00 | n;
            case 0xE2: case 0xF2: return 0xFF00 | c;
        }
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
        if (x == 1 && op != 0x76 && (y == 6 || z == 6)) return hl;
        if (x == 2 && z == 6) return hl;
        if (x == 0 && (z == 4 || z == 5) && y == 6) return hl;
        return -1;
    }

    public static int Length(byte op) => Lengths[op];
}
