using System;
using System.Text;
using NesEmulator.Sega;

namespace NesEmulator.Debugging;

// =====================================================================================================================================
//  Z80 (Master System, Game Gear, the Genesis's sound CPU): the whole documented set, the undocumented IXH / IXL / IYH / IYL forms,
//  SLL, IN (C) / OUT (C),0 and the DD CB d op forms (with their register copy). BrokenDev Timeflow T0.
// =====================================================================================================================================

public sealed class DisasmZ80 : IDebugDisassembler
{
    public static readonly DisasmZ80 Instance = new();
    private static readonly string[] R = { "B", "C", "D", "E", "H", "L", "(HL)", "A" };
    private static readonly string[] RP = { "BC", "DE", "HL", "SP" };
    private static readonly string[] RP2 = { "BC", "DE", "HL", "AF" };
    private static readonly string[] CC = { "NZ", "Z", "NC", "C", "PO", "PE", "P", "M" };
    private static readonly string[] Alu = { "ADD A,", "ADC A,", "SUB ", "SBC A,", "AND ", "XOR ", "OR ", "CP " };
    private static readonly string[] Rot = { "RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL" };
    private static readonly string[] Im = { "0", "0", "1", "2", "0", "0", "1", "2" };
    private static readonly string[] Block = { "LDI", "CPI", "INI", "OUTI", "LDD", "CPD", "IND", "OUTD", "LDIR", "CPIR", "INIR", "OTIR", "LDDR", "CPDR", "INDR", "OTDR" };
    private static readonly byte[] BaseLen = BuildBaseLengths();

    private static byte[] BuildBaseLengths()
    {
        var l = new byte[256];
        for (int op = 0; op < 256; op++)
        {
            int x = op >> 6, y = (op >> 3) & 7, z = op & 7, p = y >> 1, q = y & 1;
            int n = 1;
            if (x == 0)
            {
                if (z == 0) n = y >= 2 ? 2 : 1;                    // DJNZ, JR
                else if (z == 1) n = q == 0 ? 3 : 1;               // LD rp,nn
                else if (z == 2) n = p >= 2 ? 3 : 1;               // LD (nn),HL / A
                else if (z == 6) n = 2;                            // LD r,n
            }
            else if (x == 3)
            {
                if (z == 2 || z == 4) n = 3;                       // JP cc / CALL cc
                else if (z == 3) n = y == 0 ? 3 : (y == 2 || y == 3) ? 2 : 1;
                else if (z == 5) n = q == 1 && p == 0 ? 3 : 1;     // CALL nn
                else if (z == 6) n = 2;                            // alu n
            }
            l[op] = (byte)n;
        }
        return l;
    }

    /// <summary>Whether a base opcode uses (HL) as memory (so under DD / FD it takes a displacement byte).</summary>
    private static bool UsesIndirectHl(int op)
    {
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
        if (x == 0) return (z is 4 or 5 or 6) && y == 6;
        if (x == 1) return op != 0x76 && (y == 6 || z == 6);
        if (x == 2) return z == 6;
        return false;
    }

    private static InstrClass FlowOf(int op, int len)
    {
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
        if (x == 3)
        {
            if (op == 0xCD || z == 4) return new InstrClass(len, FlowKind.Call, 2);
            if (z == 7) return new InstrClass(len, FlowKind.Call, 2);                       // RST
            if (op == 0xC9 || z == 0) return new InstrClass(len, FlowKind.Return, 0);       // RET, RET cc
            if (op == 0xC3 || op == 0xE9) return new InstrClass(len, FlowKind.Jump, 0);     // JP nn, JP (HL)
            if (z == 2) return new InstrClass(len, FlowKind.Branch, 0);                     // JP cc
        }
        if (x == 0 && z == 0 && y >= 2) return new InstrClass(len, y == 3 ? FlowKind.Jump : FlowKind.Branch, 0);   // DJNZ, JR, JR cc
        return new InstrClass(len, FlowKind.None, 0);
    }

    public InstrClass Classify(byte op, byte op1, in DecodeContext ctx)
    {
        switch (op)
        {
            case 0xCB: return new InstrClass(2, FlowKind.None, 0);
            case 0xED:
            {
                int x = op1 >> 6, z = op1 & 7;
                if (x == 1 && z == 3) return new InstrClass(4, FlowKind.None, 0);
                if (x == 1 && z == 5) return new InstrClass(2, FlowKind.Return, 0);       // RETN, RETI
                return new InstrClass(2, FlowKind.None, 0);
            }
            case 0xDD:
            case 0xFD:
            {
                if (op1 == 0xCB) return new InstrClass(4, FlowKind.None, 0);
                if (op1 is 0xDD or 0xFD or 0xED) return new InstrClass(1, FlowKind.None, 0);   // a prefix that does nothing
                int len = 1 + BaseLen[op1] + (UsesIndirectHl(op1) ? 1 : 0);
                return FlowOf(op1, len);
            }
        }
        return FlowOf(op, BaseLen[op]);
    }

    public string Format(uint pc, ReadOnlySpan<byte> span, in DecodeContext ctx, Func<uint, string?>? label)
    {
        byte[] b = span.ToArray();
        byte B(int i) => i < b.Length ? b[i] : (byte)0;
        string A16(int a) => label?.Invoke((uint)(a & 0xFFFF)) ?? "$" + (a & 0xFFFF).ToString("X4");
        string N8(int v) => "$" + (v & 0xFF).ToString("X2");
        byte op = B(0);
        if (op == 0xCB) return FormatCb(B(1), null, 0);
        if (op == 0xED) return FormatEd(B(1), B(2) | B(3) << 8, A16);
        if (op is 0xDD or 0xFD)
        {
            string ix = op == 0xDD ? "IX" : "IY";
            byte o = B(1);
            if (o == 0xCB) return FormatCb(B(3), ix, (sbyte)B(2));
            if (o is 0xDD or 0xFD or 0xED) return "NOP*";   // the prefix alone (the next byte starts again)
            bool mem = UsesIndirectHl(o);
            int d = mem ? (sbyte)B(2) : 0;
            int after = mem ? 3 : 2;                      // where the immediate operands start
            string disp = $"({ix}{(d < 0 ? "-" : "+")}${Math.Abs(d):X2})";
            return FormatBase(o, (int)pc + 1, i => B(after + i), ix, disp, mem, A16, N8);
        }
        return FormatBase(op, (int)pc, i => B(1 + i), null, "(HL)", false, A16, N8);
    }

    private static string FormatBase(int op, int pc, Func<int, byte> imm, string? ix, string hlMem, bool mem, Func<int, string> A16, Func<int, string> N8)
    {
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7, p = y >> 1, q = y & 1;
        string HL = ix ?? "HL";
        // the registers as an index prefix changes them: H / L become IXH / IXL unless the instruction also uses (IX+d)
        string Reg(int r) => r == 6 ? hlMem : ix != null && !mem && r == 4 ? ix + "H" : ix != null && !mem && r == 5 ? ix + "L" : R[r];
        string Rp(int i) => i == 2 ? HL : RP[i];
        string Rp2(int i) => i == 2 ? HL : RP2[i];
        int nn = imm(0) | imm(1) << 8;
        int n = imm(0);
        // pc is the address of the opcode byte (after a prefix), so a relative jump lands at pc + 2 + e
        string Rel() { int t = (ushort)(pc + 2 + (sbyte)imm(0)); return A16(t); }
        switch (x)
        {
            case 0:
                switch (z)
                {
                    case 0: return y switch { 0 => "NOP", 1 => "EX AF,AF'", 2 => "DJNZ " + Rel(), 3 => "JR " + Rel(), _ => $"JR {CC[y - 4]}," + Rel() };
                    case 1: return q == 0 ? $"LD {Rp(p)},{A16(nn)}" : $"ADD {HL},{Rp(p)}";
                    case 2:
                        return (q, p) switch
                        {
                            (0, 0) => "LD (BC),A", (0, 1) => "LD (DE),A", (0, 2) => $"LD ({A16(nn)}),{HL}", (0, _) => $"LD ({A16(nn)}),A",
                            (_, 0) => "LD A,(BC)", (_, 1) => "LD A,(DE)", (_, 2) => $"LD {HL},({A16(nn)})", _ => $"LD A,({A16(nn)})",
                        };
                    case 3: return (q == 0 ? "INC " : "DEC ") + Rp(p);
                    case 4: return "INC " + Reg(y);
                    case 5: return "DEC " + Reg(y);
                    case 6: return $"LD {Reg(y)},{N8(n)}";
                    default: return new[] { "RLCA", "RRCA", "RLA", "RRA", "DAA", "CPL", "SCF", "CCF" }[y];
                }
            case 1:
                if (op == 0x76) return "HALT";
                // LD r,(IX+d) / LD (IX+d),r keep H and L as H and L
                return $"LD {(mem && z == 6 ? R[y] : Reg(y))},{(mem && y == 6 ? R[z] : Reg(z))}".Replace("(HL)", hlMem);
            case 2: return Alu[y] + Reg(z);
            default:
                switch (z)
                {
                    case 0: return "RET " + CC[y];
                    case 1:
                        if (q == 0) return "POP " + Rp2(p);
                        return p switch { 0 => "RET", 1 => "EXX", 2 => $"JP ({HL})", _ => $"LD SP,{HL}" };
                    case 2: return $"JP {CC[y]},{A16(nn)}";
                    case 3:
                        return y switch
                        {
                            0 => "JP " + A16(nn), 1 => "CB", 2 => $"OUT ({N8(n)}),A", 3 => $"IN A,({N8(n)})",
                            4 => $"EX (SP),{HL}", 5 => "EX DE,HL", 6 => "DI", _ => "EI",
                        };
                    case 4: return $"CALL {CC[y]},{A16(nn)}";
                    case 5:
                        if (q == 0) return "PUSH " + Rp2(p);
                        return p == 0 ? "CALL " + A16(nn) : "PREFIX";
                    case 6: return Alu[y] + N8(n);
                    default: return "RST " + N8(y * 8);
                }
        }
    }

    private static string FormatCb(byte op, string? ix, int d)
    {
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
        string target = ix == null ? R[z] : $"({ix}{(d < 0 ? "-" : "+")}${Math.Abs(d):X2})";
        string copy = ix != null && z != 6 ? "," + R[z] : "";   // DD CB d op with a register: the result is also copied to it (not for BIT)
        return x switch
        {
            0 => $"{Rot[y]} {target}{copy}",
            1 => $"BIT {y},{target}",
            2 => $"RES {y},{target}{copy}",
            _ => $"SET {y},{target}{copy}",
        };
    }

    private static string FormatEd(byte op, int nn, Func<int, string> A16)
    {
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7, p = y >> 1, q = y & 1;
        if (x == 1)
        {
            switch (z)
            {
                case 0: return y == 6 ? "IN (C)" : $"IN {R[y]},(C)";
                case 1: return y == 6 ? "OUT (C),0" : $"OUT (C),{R[y]}";
                case 2: return (q == 0 ? "SBC HL," : "ADC HL,") + RP[p];
                case 3: return q == 0 ? $"LD ({A16(nn)}),{RP[p]}" : $"LD {RP[p]},({A16(nn)})";
                case 4: return "NEG";
                case 5: return y == 1 ? "RETI" : "RETN";
                case 6: return "IM " + Im[y];
                default: return new[] { "LD I,A", "LD R,A", "LD A,I", "LD A,R", "RRD", "RLD", "NOP*", "NOP*" }[y];
            }
        }
        if (x == 2 && z <= 3 && y >= 4) return Block[(y - 4) * 4 + z];
        return "NOP*";   // an ED opcode with no instruction: two bytes of nothing
    }

    /// <summary>The memory address an instruction reads or writes (the trace's [address] = value column): (HL), (BC), (DE), (nn), (IX+d), (IY+d), (SP) for EX (SP); -1 for none.</summary>
    public static int EffectiveAddress(ReadOnlySpan<byte> b, ushort bc, ushort de, ushort hl, ushort ix, ushort iy, ushort sp)
    {
        if (b.Length == 0) return -1;
        byte op = b[0];
        if (op == 0xCB) return (At(b, 1) & 7) == 6 ? hl : -1;
        if (op == 0xED)
        {
            byte o = At(b, 1);
            if ((o >> 6) == 1 && (o & 7) == 3) return At(b, 2) | At(b, 3) << 8;
            if (o is 0x67 or 0x6F) return hl;                                    // RRD, RLD
            if ((o >> 6) == 2 && (o & 7) <= 1 && ((o >> 3) & 7) >= 4) return hl;  // LDI / CPI and kin read (HL)
            return -1;
        }
        if (op is 0xDD or 0xFD)
        {
            ushort ir = op == 0xDD ? ix : iy;
            byte o = At(b, 1);
            if (o == 0xCB) return (ushort)(ir + (sbyte)At(b, 2));
            if (o == 0xE3) return sp;
            if (UsesIndirectHl(o)) return (ushort)(ir + (sbyte)At(b, 2));
            if (o is 0x22 or 0x2A) return At(b, 2) | At(b, 3) << 8;
            return -1;
        }
        if (UsesIndirectHl(op)) return hl;
        switch (op)
        {
            case 0x02: case 0x0A: return bc;
            case 0x12: case 0x1A: return de;
            case 0x22: case 0x2A: case 0x32: case 0x3A: return At(b, 1) | At(b, 2) << 8;
            case 0xE3: return sp;
        }
        return -1;
    }

    private static byte At(ReadOnlySpan<byte> b, int i) => i < b.Length ? b[i] : (byte)0;
}

// =====================================================================================================================================
//  Motorola 68000 (Genesis): every opcode the core executes (its own decode table says which are legal), with the extension words.
//  BrokenDev Timeflow T0.
// =====================================================================================================================================

/// <summary>
/// The 68000 disassembler. An instruction is one to five 16-bit words, big-endian; its length follows from the first word alone (<see cref="Classify"/>). The debugger's
/// tools pass the instruction's bytes; where they pass fewer (a trace record keeps four), an instance made with a memory reader (<see cref="WithMemory"/>) reads the
/// rest from memory.
/// </summary>
public sealed class DisasmM68k : IDebugDisassembler
{
    public static readonly DisasmM68k Instance = new(null);
    private readonly Func<uint, byte>? memory;
    private DisasmM68k(Func<uint, byte>? memory) { this.memory = memory; }
    /// <summary>A disassembler that reads the extension words it is not given from <paramref name="memory"/> (a side-effect-free peek).</summary>
    public static DisasmM68k WithMemory(Func<uint, byte> memory) => new(memory);

    private static readonly string[] Cc = { "T", "F", "HI", "LS", "CC", "CS", "NE", "EQ", "VC", "VS", "PL", "MI", "GE", "LT", "GT", "LE" };
    private static readonly string[] Sz = { ".B", ".W", ".L", "" };

    private static byte Group(int op) => M68k<MdBus>.DecodeGroup((ushort)op);

    /// <summary>Extension words of an effective address (mode, register) for an operand of <paramref name="size"/> (0 byte, 1 word, 2 long).</summary>
    private static int EaWords(int mode, int reg, int size) => mode switch
    {
        5 or 6 => 1,
        7 => reg switch { 0 => 1, 1 => 2, 2 => 1, 3 => 1, 4 => size == 2 ? 2 : 1, _ => 0 },
        _ => 0,
    };

    private static int EaWords(int ea, int size) => EaWords((ea >> 3) & 7, ea & 7, size);

    /// <summary>Words in the instruction starting with <paramref name="op"/>.</summary>
    public static int Words(int op)
    {
        int ea = op & 0x3F, size = (op >> 6) & 3;
        switch (Group(op))
        {
            case M68kH.AddEaDn: case M68kH.SubEaDn: case M68kH.Cmp: case M68kH.AndEaDn: case M68kH.OrEaDn:
            case M68kH.AddDnEa: case M68kH.SubDnEa: case M68kH.AndDnEa: case M68kH.OrDnEa: case M68kH.Eor:
            case M68kH.Clr: case M68kH.Neg: case M68kH.Negx: case M68kH.Not: case M68kH.Tst:
            case M68kH.AddqEa: case M68kH.AddqAn: case M68kH.SubqEa: case M68kH.SubqAn:
                return 1 + EaWords(ea, size);
            case M68kH.Adda: case M68kH.Suba: case M68kH.Cmpa:
                return 1 + EaWords(ea, (op & 0x100) != 0 ? 2 : 1);
            case M68kH.Addi: case M68kH.Subi: case M68kH.Andi: case M68kH.Ori: case M68kH.Eori: case M68kH.Cmpi:
                return 1 + (size == 2 ? 2 : 1) + EaWords(ea, size);
            case M68kH.AndiCcr: case M68kH.AndiSr: case M68kH.OriCcr: case M68kH.OriSr: case M68kH.EoriCcr: case M68kH.EoriSr:
            case M68kH.Dbcc: case M68kH.Link: case M68kH.Stop: case M68kH.MovepToMem: case M68kH.MovepToReg:
                return 2;
            case M68kH.Bcc: case M68kH.Bra: case M68kH.Bsr:
                return (op & 0xFF) == 0 ? 2 : 1;
            case M68kH.BitReg: return 1 + EaWords(ea, 0);
            case M68kH.BitImm: return 2 + EaWords(ea, 0);
            case M68kH.Chk: case M68kH.Divs: case M68kH.Divu: case M68kH.Muls: case M68kH.Mulu:
            case M68kH.MoveFromSr: case M68kH.MoveToCcr: case M68kH.MoveToSr: case M68kH.ShiftEa:
                return 1 + EaWords(ea, 1);
            case M68kH.Nbcd: case M68kH.Tas: case M68kH.Scc: return 1 + EaWords(ea, 0);
            case M68kH.Jmp: case M68kH.Jsr: case M68kH.Lea: case M68kH.Pea: return 1 + EaWords(ea, 2);
            case M68kH.MovemToMem: case M68kH.MovemToReg: return 2 + EaWords(ea, (op & 0x40) != 0 ? 2 : 1);
            case M68kH.Move: case M68kH.Movea:
            {
                int ms = (op >> 12) & 3, s = ms == 1 ? 0 : ms == 3 ? 1 : 2;
                return 1 + EaWords(ea, s) + EaWords((op >> 6) & 7, (op >> 9) & 7, s);
            }
            default: return 1;
        }
    }

    public InstrClass Classify(byte op, byte op1, in DecodeContext ctx)
    {
        int w = op << 8 | op1;
        int len = Words(w) * 2;
        switch (Group(w))
        {
            case M68kH.Jsr: case M68kH.Bsr: return new InstrClass(len, FlowKind.Call, 4);
            case M68kH.Trap: return new InstrClass(len, FlowKind.Software, 6);
            case M68kH.Rts: case M68kH.Rte: case M68kH.Rtr: return new InstrClass(len, FlowKind.Return, 0);
            case M68kH.Jmp: case M68kH.Bra: return new InstrClass(len, FlowKind.Jump, 0);
            case M68kH.Bcc: case M68kH.Dbcc: return new InstrClass(len, FlowKind.Branch, 0);
        }
        return new InstrClass(len, FlowKind.None, 0);
    }

    /// <summary>The call-stack name of a call instruction (JSR, BSR, TRAP).</summary>
    public static string CallName(int op) => Group(op) switch { M68kH.Bsr => "BSR", M68kH.Trap => "TRAP", _ => "JSR" };

    private ref struct Reader
    {
        public ReadOnlySpan<byte> B; public uint Pc; public int Pos; public Func<uint, byte>? Mem;
        public int Byte(int i) => i < B.Length ? B[i] : Mem != null ? Mem((Pc + (uint)i) & 0xFFFFFF) : 0;
        public int Word() { int v = Byte(Pos) << 8 | Byte(Pos + 1); Pos += 2; return v; }
        public uint Long() { uint hi = (uint)Word(); return hi << 16 | (uint)Word(); }
    }

    public string Format(uint pc, ReadOnlySpan<byte> bytes, in DecodeContext ctx, Func<uint, string?>? label)
    {
        var r = new Reader { B = bytes, Pc = pc, Pos = 0, Mem = memory };
        int op = r.Word();
        try { return Decode(op, pc, ref r, label); }
        catch (Exception) { return $"DC.W ${op:X4}"; }
    }

    private static string Hex(uint v, int digits) => "$" + v.ToString("X" + digits);
    private static string Addr(uint a, Func<uint, string?>? label) => label?.Invoke(a & 0xFFFFFF) ?? Hex(a & 0xFFFFFF, 6);
    private static string Signed(int v) => v < 0 ? "-$" + (-v).ToString("X") : "$" + v.ToString("X");

    /// <summary>An effective address as text, reading its extension words.</summary>
    private static string Ea(int mode, int reg, int size, uint pc, ref Reader r, Func<uint, string?>? label)
    {
        switch (mode)
        {
            case 0: return "D" + reg;
            case 1: return "A" + reg;
            case 2: return $"(A{reg})";
            case 3: return $"(A{reg})+";
            case 4: return $"-(A{reg})";
            case 5: { int d = (short)r.Word(); return $"{Signed(d)}(A{reg})"; }
            case 6: return Brief($"A{reg}", r.Word());
            default:
                switch (reg)
                {
                    case 0: { int a = (short)r.Word(); return Addr((uint)a, label) + ".W"; }
                    case 1: return Addr(r.Long(), label);
                    case 2: { uint at = pc + (uint)r.Pos; int d = (short)r.Word(); return Addr((uint)(at + d), label) + "(PC)"; }
                    case 3: { uint at = pc + (uint)r.Pos; int ext = r.Word(); uint t = (uint)(at + (sbyte)ext); return Brief(Addr(t, label) + "-PC", ext, pcRelative: true, target: Addr(t, label)); }
                    case 4: return "#" + (size == 2 ? Hex(r.Long(), 8) : size == 1 ? Hex((uint)r.Word(), 4) : Hex((uint)(r.Word() & 0xFF), 2));
                    default: return "?";
                }
        }
    }

    private static string Brief(string baseReg, int ext, bool pcRelative = false, string? target = null)
    {
        string xn = ((ext & 0x8000) != 0 ? "A" : "D") + ((ext >> 12) & 7) + ((ext & 0x800) != 0 ? ".L" : ".W");
        if (pcRelative) return $"{target}(PC,{xn})";
        return $"{Signed((sbyte)ext)}({baseReg},{xn})";
    }

    private static string Ea(int ea, int size, uint pc, ref Reader r, Func<uint, string?>? label) => Ea((ea >> 3) & 7, ea & 7, size, pc, ref r, label);

    private static string RegList(int mask, bool reversed)
    {
        if (reversed) { int m = 0; for (int i = 0; i < 16; i++) if ((mask >> i & 1) != 0) m |= 1 << (15 - i); mask = m; }
        var sb = new StringBuilder();
        for (int bank = 0; bank < 2; bank++)
        {
            char c = bank == 0 ? 'D' : 'A';
            int i = 0;
            while (i < 8)
            {
                if ((mask >> (bank * 8 + i) & 1) == 0) { i++; continue; }
                int j = i;
                while (j + 1 < 8 && (mask >> (bank * 8 + j + 1) & 1) != 0) j++;
                if (sb.Length > 0) sb.Append('/');
                sb.Append(c).Append(i);
                if (j > i) sb.Append('-').Append(c).Append(j);
                i = j + 1;
            }
        }
        return sb.Length == 0 ? "0" : sb.ToString();
    }

    private static string Decode(int op, uint pc, ref Reader r, Func<uint, string?>? label)
    {
        int ea = op & 0x3F, size = (op >> 6) & 3, dn = (op >> 9) & 7, low = op & 7;
        string S = size < 3 ? Sz[size] : "";
        switch (Group(op))
        {
            case M68kH.AddEaDn: return $"ADD{S} {Ea(ea, size, pc, ref r, label)},D{dn}";
            case M68kH.SubEaDn: return $"SUB{S} {Ea(ea, size, pc, ref r, label)},D{dn}";
            case M68kH.Cmp: return $"CMP{S} {Ea(ea, size, pc, ref r, label)},D{dn}";
            case M68kH.AndEaDn: return $"AND{S} {Ea(ea, size, pc, ref r, label)},D{dn}";
            case M68kH.OrEaDn: return $"OR{S} {Ea(ea, size, pc, ref r, label)},D{dn}";
            case M68kH.AddDnEa: return $"ADD{S} D{dn},{Ea(ea, size, pc, ref r, label)}";
            case M68kH.SubDnEa: return $"SUB{S} D{dn},{Ea(ea, size, pc, ref r, label)}";
            case M68kH.AndDnEa: return $"AND{S} D{dn},{Ea(ea, size, pc, ref r, label)}";
            case M68kH.OrDnEa: return $"OR{S} D{dn},{Ea(ea, size, pc, ref r, label)}";
            case M68kH.Eor: return $"EOR{S} D{dn},{Ea(ea, size, pc, ref r, label)}";
            case M68kH.Adda: case M68kH.Suba: case M68kH.Cmpa:
            {
                string n = Group(op) == M68kH.Adda ? "ADDA" : Group(op) == M68kH.Suba ? "SUBA" : "CMPA";
                int s = (op & 0x100) != 0 ? 2 : 1;
                return $"{n}{Sz[s]} {Ea(ea, s, pc, ref r, label)},A{dn}";
            }
            case M68kH.Addi: case M68kH.Subi: case M68kH.Andi: case M68kH.Ori: case M68kH.Eori: case M68kH.Cmpi:
            {
                string n = Group(op) switch { M68kH.Addi => "ADDI", M68kH.Subi => "SUBI", M68kH.Andi => "ANDI", M68kH.Ori => "ORI", M68kH.Eori => "EORI", _ => "CMPI" };
                string imm = "#" + (size == 2 ? Hex(r.Long(), 8) : size == 1 ? Hex((uint)r.Word(), 4) : Hex((uint)(r.Word() & 0xFF), 2));
                return $"{n}{S} {imm},{Ea(ea, size, pc, ref r, label)}";
            }
            case M68kH.AndiCcr: return $"ANDI #{Hex((uint)(r.Word() & 0xFF), 2)},CCR";
            case M68kH.OriCcr: return $"ORI #{Hex((uint)(r.Word() & 0xFF), 2)},CCR";
            case M68kH.EoriCcr: return $"EORI #{Hex((uint)(r.Word() & 0xFF), 2)},CCR";
            case M68kH.AndiSr: return $"ANDI #{Hex((uint)r.Word(), 4)},SR";
            case M68kH.OriSr: return $"ORI #{Hex((uint)r.Word(), 4)},SR";
            case M68kH.EoriSr: return $"EORI #{Hex((uint)r.Word(), 4)},SR";
            case M68kH.AddqEa: case M68kH.AddqAn: return $"ADDQ{S} #{(dn == 0 ? 8 : dn)},{Ea(ea, size, pc, ref r, label)}";
            case M68kH.SubqEa: case M68kH.SubqAn: return $"SUBQ{S} #{(dn == 0 ? 8 : dn)},{Ea(ea, size, pc, ref r, label)}";
            case M68kH.Addx: return (op & 8) != 0 ? $"ADDX{S} -(A{low}),-(A{dn})" : $"ADDX{S} D{low},D{dn}";
            case M68kH.Subx: return (op & 8) != 0 ? $"SUBX{S} -(A{low}),-(A{dn})" : $"SUBX{S} D{low},D{dn}";
            case M68kH.Abcd: return (op & 8) != 0 ? $"ABCD -(A{low}),-(A{dn})" : $"ABCD D{low},D{dn}";
            case M68kH.Sbcd: return (op & 8) != 0 ? $"SBCD -(A{low}),-(A{dn})" : $"SBCD D{low},D{dn}";
            case M68kH.Cmpm: return $"CMPM{S} (A{low})+,(A{dn})+";
            case M68kH.ShiftReg:
            {
                string n = ((op >> 3) & 3) switch { 0 => "AS", 1 => "LS", 2 => "ROX", _ => "RO" } + ((op & 0x100) != 0 ? "L" : "R");
                string count = (op & 0x20) != 0 ? $"D{dn}" : $"#{(dn == 0 ? 8 : dn)}";
                return $"{n}{S} {count},D{low}";
            }
            case M68kH.ShiftEa:
            {
                string n = ((op >> 9) & 3) switch { 0 => "AS", 1 => "LS", 2 => "ROX", _ => "RO" } + ((op & 0x100) != 0 ? "L" : "R");
                return $"{n}.W {Ea(ea, 1, pc, ref r, label)}";
            }
            case M68kH.Bcc: case M68kH.Bra: case M68kH.Bsr:
            {
                int cc = (op >> 8) & 15;
                string n = cc == 0 ? "BRA" : cc == 1 ? "BSR" : "B" + Cc[cc];
                int d8 = (sbyte)op;
                if (d8 == 0) { int d = (short)r.Word(); return $"{n}.W {Addr((uint)(pc + 2 + d), label)}"; }
                return $"{n}.S {Addr((uint)(pc + 2 + d8), label)}";
            }
            case M68kH.Dbcc:
            {
                int cc = (op >> 8) & 15;
                int d = (short)r.Word();
                return $"{(cc == 1 ? "DBRA" : "DB" + Cc[cc])} D{low},{Addr((uint)(pc + 2 + d), label)}";
            }
            case M68kH.Scc: return $"S{Cc[(op >> 8) & 15]} {Ea(ea, 0, pc, ref r, label)}";
            case M68kH.BitReg:
            {
                string n = size switch { 0 => "BTST", 1 => "BCHG", 2 => "BCLR", _ => "BSET" };
                return $"{n} D{dn},{Ea(ea, 0, pc, ref r, label)}";
            }
            case M68kH.BitImm:
            {
                string n = size switch { 0 => "BTST", 1 => "BCHG", 2 => "BCLR", _ => "BSET" };
                int bit = r.Word() & 0xFF;
                return $"{n} #{bit},{Ea(ea, 0, pc, ref r, label)}";
            }
            case M68kH.Chk: return $"CHK.W {Ea(ea, 1, pc, ref r, label)},D{dn}";
            case M68kH.Divs: return $"DIVS.W {Ea(ea, 1, pc, ref r, label)},D{dn}";
            case M68kH.Divu: return $"DIVU.W {Ea(ea, 1, pc, ref r, label)},D{dn}";
            case M68kH.Muls: return $"MULS.W {Ea(ea, 1, pc, ref r, label)},D{dn}";
            case M68kH.Mulu: return $"MULU.W {Ea(ea, 1, pc, ref r, label)},D{dn}";
            case M68kH.Clr: return $"CLR{S} {Ea(ea, size, pc, ref r, label)}";
            case M68kH.Neg: return $"NEG{S} {Ea(ea, size, pc, ref r, label)}";
            case M68kH.Negx: return $"NEGX{S} {Ea(ea, size, pc, ref r, label)}";
            case M68kH.Not: return $"NOT{S} {Ea(ea, size, pc, ref r, label)}";
            case M68kH.Tst: return $"TST{S} {Ea(ea, size, pc, ref r, label)}";
            case M68kH.ExgDd: return $"EXG D{dn},D{low}";
            case M68kH.ExgAa: return $"EXG A{dn},A{low}";
            case M68kH.ExgDa: return $"EXG D{dn},A{low}";
            case M68kH.Ext: return $"EXT{((op & 0x40) != 0 ? ".L" : ".W")} D{low}";
            case M68kH.Jmp: return $"JMP {Ea(ea, 2, pc, ref r, label)}";
            case M68kH.Jsr: return $"JSR {Ea(ea, 2, pc, ref r, label)}";
            case M68kH.Lea: return $"LEA {Ea(ea, 2, pc, ref r, label)},A{dn}";
            case M68kH.Pea: return $"PEA {Ea(ea, 2, pc, ref r, label)}";
            case M68kH.Link: return $"LINK A{low},#{Signed((short)r.Word())}";
            case M68kH.Unlk: return $"UNLK A{low}";
            case M68kH.Move: case M68kH.Movea:
            {
                int ms = (op >> 12) & 3, s = ms == 1 ? 0 : ms == 3 ? 1 : 2;
                string src = Ea(ea, s, pc, ref r, label);
                int dm = (op >> 6) & 7;
                if (dm == 1) return $"MOVEA{Sz[s]} {src},A{dn}";
                string dst = Ea(dm, dn, s, pc, ref r, label);
                return $"MOVE{Sz[s]} {src},{dst}";
            }
            case M68kH.MovemToMem:
            {
                int mask = r.Word();
                int mode = (ea >> 3) & 7;
                return $"MOVEM{((op & 0x40) != 0 ? ".L" : ".W")} {RegList(mask, mode == 4)},{Ea(ea, 2, pc, ref r, label)}";
            }
            case M68kH.MovemToReg:
            {
                int mask = r.Word();
                return $"MOVEM{((op & 0x40) != 0 ? ".L" : ".W")} {Ea(ea, 2, pc, ref r, label)},{RegList(mask, false)}";
            }
            case M68kH.MovepToMem: return $"MOVEP{((op & 0x40) != 0 ? ".L" : ".W")} D{dn},{Signed((short)r.Word())}(A{low})";
            case M68kH.MovepToReg: return $"MOVEP{((op & 0x40) != 0 ? ".L" : ".W")} {Signed((short)r.Word())}(A{low}),D{dn}";
            case M68kH.Moveq: return $"MOVEQ #{Signed((sbyte)op)},D{dn}";
            case M68kH.MoveFromSr: return $"MOVE SR,{Ea(ea, 1, pc, ref r, label)}";
            case M68kH.MoveToCcr: return $"MOVE {Ea(ea, 1, pc, ref r, label)},CCR";
            case M68kH.MoveToSr: return $"MOVE {Ea(ea, 1, pc, ref r, label)},SR";
            case M68kH.MoveFromUsp: return $"MOVE USP,A{low}";
            case M68kH.MoveToUsp: return $"MOVE A{low},USP";
            case M68kH.Nbcd: return $"NBCD {Ea(ea, 0, pc, ref r, label)}";
            case M68kH.Tas: return $"TAS {Ea(ea, 0, pc, ref r, label)}";
            case M68kH.Nop: return "NOP";
            case M68kH.Reset: return "RESET";
            case M68kH.Rte: return "RTE";
            case M68kH.Rts: return "RTS";
            case M68kH.Rtr: return "RTR";
            case M68kH.Trapv: return "TRAPV";
            case M68kH.Stop: return $"STOP #{Hex((uint)r.Word(), 4)}";
            case M68kH.Swap: return $"SWAP D{low}";
            case M68kH.Trap: return $"TRAP #{op & 15}";
            default:
                if (op == 0x4AFC) return "ILLEGAL";
                if ((op & 0xF000) == 0xA000) return $"LINEA ${op:X4}";
                if ((op & 0xF000) == 0xF000) return $"LINEF ${op:X4}";
                return $"DC.W ${op:X4}";
        }
    }
}
