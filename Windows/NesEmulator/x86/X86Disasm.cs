// A small x86 disassembler for the debugger (BrokenDev MCP): 16- and 32-bit code, the integer instruction set of the 386 to the Pentium II
// (one-byte opcodes, the common 0F opcodes, ModRM / SIB, prefixes), Intel syntax. x87 / MMX opcodes are named by group only ("fpu d9 /5").
// Our own code (not from a library): enough to read code at a breakpoint, not an assembler-grade decoder.

using System.Text;

namespace NesEmulator.X86;

public static class X86Disasm
{
    private static readonly string[] R32 = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };
    private static readonly string[] R16 = { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" };
    private static readonly string[] R8 = { "al", "cl", "dl", "bl", "ah", "ch", "dh", "bh" };
    private static readonly string[] Sreg = { "es", "cs", "ss", "ds", "fs", "gs", "?6", "?7" };
    private static readonly string[] Alu = { "add", "or", "adc", "sbb", "and", "sub", "xor", "cmp" };
    private static readonly string[] Shf = { "rol", "ror", "rcl", "rcr", "shl", "shr", "sal", "sar" };
    private static readonly string[] Cc = { "o", "no", "b", "ae", "e", "ne", "be", "a", "s", "ns", "p", "np", "l", "ge", "le", "g" };
    private static readonly string[] M16 = { "bx+si", "bx+di", "bp+si", "bp+di", "si", "di", "bp", "bx" };

    /// <summary>One instruction at linear address <paramref name="lin"/> (offset <paramref name="eip"/> in its code segment).
    /// <paramref name="read"/> gives a byte at a linear address, or -1 when it is not mapped. Returns the length (0: unreadable) and the text.</summary>
    public static (int len, string text) Decode(Func<uint, int> read, uint lin, uint eip, bool code32)
    {
        var d = new Dec(read, lin, eip, code32);
        try { string t = d.Run(); return (d.Pos, t); }
        catch (Unmapped) { return (d.Pos, d.Pos == 0 ? "(not mapped)" : "(runs into unmapped memory)"); }
    }

    private sealed class Unmapped : Exception { }

    private sealed class Dec
    {
        private readonly Func<uint, int> read; private readonly uint lin, eip; private readonly bool c32;
        public int Pos;
        private bool o16, a16; private string? seg; private string rep = "", lck = "";
        private int modrm = -1;
        public Dec(Func<uint, int> r, uint l, uint e, bool c) { read = r; lin = l; eip = e; c32 = c; }

        private byte B() { int v = read(lin + (uint)Pos); if (v < 0) throw new Unmapped(); Pos++; if (Pos > 15) throw new Unmapped(); return (byte)v; }
        private ushort W() => (ushort)(B() | (B() << 8));
        private uint D() => (uint)(W() | (W() << 16));
        private bool Op32 => c32 != o16;
        private bool Ad32 => c32 != a16;
        private string Rv(int r) => Op32 ? R32[r] : R16[r];
        private uint Iv() => Op32 ? D() : W();
        private static string H(uint v) => v < 10 ? v.ToString() : "0x" + v.ToString("x");
        private static string Sx(int v) => v < 0 ? "-" + H((uint)-v) : "+" + H((uint)v);
        private uint Next => eip + (uint)Pos;
        private string Rel8() { sbyte r = (sbyte)B(); return H(Mask(Next + (uint)r)); }
        private string RelV() { uint r = Op32 ? D() : (uint)(short)W(); return H(Mask(Next + r)); }
        private uint Mask(uint v) => Op32 ? v : v & 0xffff;

        private int Mod => modrm >> 6;
        private int Reg => (modrm >> 3) & 7;
        private int Rm => modrm & 7;
        private void M() { if (modrm < 0) modrm = B(); }

        /// <summary>The r/m operand; size: 1 byte, 2 word, 4 dword, 0 none (lea / far pointers), 6 fword, 8 qword.</summary>
        private string E(int size)
        {
            M();
            if (Mod == 3) return size == 1 ? R8[Rm] : size == 2 ? R16[Rm] : size == 4 ? R32[Rm] : Rv(Rm);
            string a;
            if (!Ad32)
            {
                if (Mod == 0 && Rm == 6) a = H(W());
                else { a = M16[Rm]; if (Mod == 1) a += Sx((sbyte)B()); else if (Mod == 2) a += Sx((short)W()); }
            }
            else
            {
                string b;
                if (Rm == 4)
                {
                    byte sib = B(); int sc = sib >> 6, ix = (sib >> 3) & 7, bs = sib & 7;
                    b = bs == 5 && Mod == 0 ? H(D()) : R32[bs];
                    if (ix != 4) b += "+" + R32[ix] + (sc > 0 ? "*" + (1 << sc) : "");
                }
                else if (Mod == 0 && Rm == 5) b = H(D());
                else b = R32[Rm];
                if (Mod == 1) b += Sx((sbyte)B()); else if (Mod == 2) b += Sx((int)D());
                a = b;
            }
            string sz = size switch { 1 => "byte ", 2 => "word ", 4 => "dword ", 6 => "fword ", 8 => "qword ", -1 => Op32 ? "dword " : "word ", _ => "" };
            return sz + (seg != null ? seg + ":" : "") + "[" + a + "]";
        }
        private string Ev() => E(-1);
        private string Gv() { M(); return Rv(Reg); }

        public string Run()
        {
            byte b;
            while (true)
            {
                b = B();
                switch (b)
                {
                    case 0x26: seg = "es"; continue; case 0x2e: seg = "cs"; continue; case 0x36: seg = "ss"; continue; case 0x3e: seg = "ds"; continue;
                    case 0x64: seg = "fs"; continue; case 0x65: seg = "gs"; continue; case 0x66: o16 = true; continue; case 0x67: a16 = true; continue;
                    case 0xf0: lck = "lock "; continue; case 0xf2: rep = "repne "; continue; case 0xf3: rep = "rep "; continue;
                }
                break;
            }
            return lck + One(b);
        }

        private string One(byte b)
        {
            if (b < 0x40 && (b & 7) < 6)
            {
                string n = Alu[b >> 3];
                switch (b & 7)
                {
                    case 0: { string e = E(1); return $"{n} {e}, {R8[Reg]}"; }
                    case 1: { string e = Ev(); return $"{n} {e}, {Rv(Reg)}"; }
                    case 2: { string e = E(1); return $"{n} {R8[Reg]}, {e}"; }
                    case 3: { string e = Ev(); return $"{n} {Rv(Reg)}, {e}"; }
                    case 4: return $"{n} al, {H(B())}";
                    default: return $"{n} {Rv(0)}, {H(Iv())}";
                }
            }
            switch (b)
            {
                case 0x06 or 0x0e or 0x16 or 0x1e: return "push " + Sreg[b >> 3];
                case 0x07 or 0x17 or 0x1f: return "pop " + Sreg[b >> 3];
                case 0x27: return "daa"; case 0x2f: return "das"; case 0x37: return "aaa"; case 0x3f: return "aas";
                case 0x0f: return Two(B());
            }
            if (b is >= 0x40 and <= 0x47) return "inc " + Rv(b & 7);
            if (b is >= 0x48 and <= 0x4f) return "dec " + Rv(b & 7);
            if (b is >= 0x50 and <= 0x57) return "push " + Rv(b & 7);
            if (b is >= 0x58 and <= 0x5f) return "pop " + Rv(b & 7);
            if (b is >= 0x70 and <= 0x7f) return "j" + Cc[b & 15] + " " + Rel8();
            if (b is >= 0x91 and <= 0x97) return $"xchg {Rv(0)}, {Rv(b & 7)}";
            if (b is >= 0xb0 and <= 0xb7) return $"mov {R8[b & 7]}, {H(B())}";
            if (b is >= 0xb8 and <= 0xbf) return $"mov {Rv(b & 7)}, {H(Iv())}";
            if (b is >= 0xd8 and <= 0xdf) { M(); string e = E(0); return $"fpu {b:x2} /{Reg} {e}"; }
            switch (b)
            {
                case 0x60: return Op32 ? "pushad" : "pusha";
                case 0x61: return Op32 ? "popad" : "popa";
                case 0x62: { M(); string e = E(0); return $"bound {Rv(Reg)}, {e}"; }
                case 0x63: { string e = E(2); return $"arpl {e}, {R16[Reg]}"; }
                case 0x68: return "push " + H(Iv());
                case 0x69: { string e = Ev(); return $"imul {Rv(Reg)}, {e}, {H(Iv())}"; }
                case 0x6a: return "push " + H((uint)(sbyte)B());
                case 0x6b: { string e = Ev(); return $"imul {Rv(Reg)}, {e}, {(sbyte)B()}"; }
                case 0x6c: return rep + "insb"; case 0x6d: return rep + (Op32 ? "insd" : "insw");
                case 0x6e: return rep + "outsb"; case 0x6f: return rep + (Op32 ? "outsd" : "outsw");
                case 0x80 or 0x82: { string e = E(1); return $"{Alu[Reg]} {e}, {H(B())}"; }
                case 0x81: { string e = Ev(); return $"{Alu[Reg]} {e}, {H(Iv())}"; }
                case 0x83: { string e = Ev(); int i = (sbyte)B(); return $"{Alu[Reg]} {e}, {(i < 0 ? "-" + H((uint)-i) : H((uint)i))}"; }
                case 0x84: { string e = E(1); return $"test {e}, {R8[Reg]}"; }
                case 0x85: { string e = Ev(); return $"test {e}, {Rv(Reg)}"; }
                case 0x86: { string e = E(1); return $"xchg {e}, {R8[Reg]}"; }
                case 0x87: { string e = Ev(); return $"xchg {e}, {Rv(Reg)}"; }
                case 0x88: { string e = E(1); return $"mov {e}, {R8[Reg]}"; }
                case 0x89: { string e = Ev(); return $"mov {e}, {Rv(Reg)}"; }
                case 0x8a: { string e = E(1); return $"mov {R8[Reg]}, {e}"; }
                case 0x8b: { string e = Ev(); return $"mov {Rv(Reg)}, {e}"; }
                case 0x8c: { string e = E(2); return $"mov {e}, {Sreg[Reg]}"; }
                case 0x8d: { M(); string e = E(0); return $"lea {Rv(Reg)}, {e}"; }
                case 0x8e: { string e = E(2); return $"mov {Sreg[Reg]}, {e}"; }
                case 0x8f: return "pop " + Ev();
                case 0x90: return rep == "rep " ? (rep = "") + "pause" : "nop";
                case 0x98: return Op32 ? "cwde" : "cbw";
                case 0x99: return Op32 ? "cdq" : "cwd";
                case 0x9a: { uint o = Iv(); ushort s = W(); return $"call far {H(s)}:{H(o)}"; }
                case 0x9b: return "wait";
                case 0x9c: return Op32 ? "pushfd" : "pushf";
                case 0x9d: return Op32 ? "popfd" : "popf";
                case 0x9e: return "sahf"; case 0x9f: return "lahf";
                case 0xa0: return $"mov al, {Moffs("byte")}";
                case 0xa1: return $"mov {Rv(0)}, {Moffs(Op32 ? "dword" : "word")}";
                case 0xa2: return $"mov {Moffs("byte")}, al";
                case 0xa3: return $"mov {Moffs(Op32 ? "dword" : "word")}, {Rv(0)}";
                case 0xa4: return rep + "movsb"; case 0xa5: return rep + (Op32 ? "movsd" : "movsw");
                case 0xa6: return rep + "cmpsb"; case 0xa7: return rep + (Op32 ? "cmpsd" : "cmpsw");
                case 0xa8: return $"test al, {H(B())}";
                case 0xa9: return $"test {Rv(0)}, {H(Iv())}";
                case 0xaa: return rep + "stosb"; case 0xab: return rep + (Op32 ? "stosd" : "stosw");
                case 0xac: return rep + "lodsb"; case 0xad: return rep + (Op32 ? "lodsd" : "lodsw");
                case 0xae: return rep + "scasb"; case 0xaf: return rep + (Op32 ? "scasd" : "scasw");
                case 0xc0: { string e = E(1); return $"{Shf[Reg]} {e}, {B()}"; }
                case 0xc1: { string e = Ev(); return $"{Shf[Reg]} {e}, {B()}"; }
                case 0xc2: return "ret " + H(W());
                case 0xc3: return "ret";
                case 0xc4: { M(); string e = E(0); return $"les {Rv(Reg)}, {e}"; }
                case 0xc5: { M(); string e = E(0); return $"lds {Rv(Reg)}, {e}"; }
                case 0xc6: { string e = E(1); return $"mov {e}, {H(B())}"; }
                case 0xc7: { string e = Ev(); return $"mov {e}, {H(Iv())}"; }
                case 0xc8: { ushort s = W(); return $"enter {H(s)}, {B()}"; }
                case 0xc9: return "leave";
                case 0xca: return "retf " + H(W());
                case 0xcb: return "retf";
                case 0xcc: return "int3";
                case 0xcd: return "int " + H(B());
                case 0xce: return "into";
                case 0xcf: return Op32 ? "iretd" : "iret";
                case 0xd0: { string e = E(1); return $"{Shf[Reg]} {e}, 1"; }
                case 0xd1: { string e = Ev(); return $"{Shf[Reg]} {e}, 1"; }
                case 0xd2: { string e = E(1); return $"{Shf[Reg]} {e}, cl"; }
                case 0xd3: { string e = Ev(); return $"{Shf[Reg]} {e}, cl"; }
                case 0xd4: { byte i = B(); return i == 10 ? "aam" : "aam " + H(i); }
                case 0xd5: { byte i = B(); return i == 10 ? "aad" : "aad " + H(i); }
                case 0xd6: return "salc";
                case 0xd7: return "xlat";
                case 0xe0: return "loopne " + Rel8(); case 0xe1: return "loope " + Rel8(); case 0xe2: return "loop " + Rel8();
                case 0xe3: return (Ad32 ? "jecxz " : "jcxz ") + Rel8();
                case 0xe4: return $"in al, {H(B())}"; case 0xe5: return $"in {Rv(0)}, {H(B())}";
                case 0xe6: return $"out {H(B())}, al"; case 0xe7: return $"out {H(B())}, {Rv(0)}";
                case 0xe8: return "call " + RelV();
                case 0xe9: return "jmp " + RelV();
                case 0xea: { uint o = Iv(); ushort s = W(); return $"jmp far {H(s)}:{H(o)}"; }
                case 0xeb: return "jmp short " + Rel8();
                case 0xec: return "in al, dx"; case 0xed: return $"in {Rv(0)}, dx";
                case 0xee: return "out dx, al"; case 0xef: return $"out dx, {Rv(0)}";
                case 0xf1: return "icebp";
                case 0xf4: return "hlt"; case 0xf5: return "cmc";
                case 0xf6: { string e = E(1); string[] n = { "test", "test", "not", "neg", "mul", "imul", "div", "idiv" }; return Reg < 2 ? $"test {e}, {H(B())}" : $"{n[Reg]} {e}"; }
                case 0xf7: { string e = Ev(); string[] n = { "test", "test", "not", "neg", "mul", "imul", "div", "idiv" }; return Reg < 2 ? $"test {e}, {H(Iv())}" : $"{n[Reg]} {e}"; }
                case 0xf8: return "clc"; case 0xf9: return "stc"; case 0xfa: return "cli"; case 0xfb: return "sti"; case 0xfc: return "cld"; case 0xfd: return "std";
                case 0xfe: { string e = E(1); return Reg switch { 0 => "inc " + e, 1 => "dec " + e, _ => $"(bad fe /{Reg})" }; }
                case 0xff:
                {
                    M();
                    return Reg switch
                    {
                        0 => "inc " + Ev(), 1 => "dec " + Ev(), 2 => "call " + Ev(), 3 => "call far " + E(0), 4 => "jmp " + Ev(), 5 => "jmp far " + E(0), 6 => "push " + Ev(),
                        _ => "(bad ff /7)",
                    };
                }
            }
            return $"db {H(b)}";
        }

        private string Moffs(string size) => $"{size} {seg ?? "ds"}:[{H(Ad32 ? D() : W())}]";

        private string Two(byte b)
        {
            if (b is >= 0x80 and <= 0x8f) return "j" + Cc[b & 15] + " " + RelV();
            if (b is >= 0x90 and <= 0x9f) return "set" + Cc[b & 15] + " " + E(1);
            if (b is >= 0x40 and <= 0x4f) { string e = Ev(); return $"cmov{Cc[b & 15]} {Rv(Reg)}, {e}"; }
            if (b is >= 0xc8 and <= 0xcf) return "bswap " + R32[b & 7];
            switch (b)
            {
                case 0x00: { string e = E(2); string[] n = { "sldt", "str", "lldt", "ltr", "verr", "verw", "(bad)", "(bad)" }; return $"{n[Reg]} {e}"; }
                case 0x01:
                {
                    M();
                    string[] n = { "sgdt", "sidt", "lgdt", "lidt", "smsw", "(bad)", "lmsw", "invlpg" };
                    return $"{n[Reg]} {(Reg is 4 or 6 ? E(2) : E(0))}";
                }
                case 0x02: { string e = E(2); return $"lar {Rv(Reg)}, {e}"; }
                case 0x03: { string e = E(2); return $"lsl {Rv(Reg)}, {e}"; }
                case 0x06: return "clts"; case 0x08: return "invd"; case 0x09: return "wbinvd"; case 0x0b: return "ud2";
                case 0x20: M(); return $"mov {R32[Rm]}, cr{Reg}";
                case 0x21: M(); return $"mov {R32[Rm]}, dr{Reg}";
                case 0x22: M(); return $"mov cr{Reg}, {R32[Rm]}";
                case 0x23: M(); return $"mov dr{Reg}, {R32[Rm]}";
                case 0x30: return "wrmsr"; case 0x31: return "rdtsc"; case 0x32: return "rdmsr"; case 0x33: return "rdpmc";
                case 0x34: return "sysenter"; case 0x35: return "sysexit";
                case 0xa0: return "push fs"; case 0xa1: return "pop fs"; case 0xa2: return "cpuid";
                case 0xa8: return "push gs"; case 0xa9: return "pop gs"; case 0xaa: return "rsm";
                case 0xa3: { string e = Ev(); return $"bt {e}, {Rv(Reg)}"; }
                case 0xab: { string e = Ev(); return $"bts {e}, {Rv(Reg)}"; }
                case 0xb3: { string e = Ev(); return $"btr {e}, {Rv(Reg)}"; }
                case 0xbb: { string e = Ev(); return $"btc {e}, {Rv(Reg)}"; }
                case 0xa4: { string e = Ev(); return $"shld {e}, {Rv(Reg)}, {B()}"; }
                case 0xa5: { string e = Ev(); return $"shld {e}, {Rv(Reg)}, cl"; }
                case 0xac: { string e = Ev(); return $"shrd {e}, {Rv(Reg)}, {B()}"; }
                case 0xad: { string e = Ev(); return $"shrd {e}, {Rv(Reg)}, cl"; }
                case 0xaf: { string e = Ev(); return $"imul {Rv(Reg)}, {e}"; }
                case 0xb0: { string e = E(1); return $"cmpxchg {e}, {R8[Reg]}"; }
                case 0xb1: { string e = Ev(); return $"cmpxchg {e}, {Rv(Reg)}"; }
                case 0xb2: { M(); string e = E(0); return $"lss {Rv(Reg)}, {e}"; }
                case 0xb4: { M(); string e = E(0); return $"lfs {Rv(Reg)}, {e}"; }
                case 0xb5: { M(); string e = E(0); return $"lgs {Rv(Reg)}, {e}"; }
                case 0xb6: { string e = E(1); return $"movzx {Rv(Reg)}, {e}"; }
                case 0xb7: { string e = E(2); return $"movzx {Rv(Reg)}, {e}"; }
                case 0xbe: { string e = E(1); return $"movsx {Rv(Reg)}, {e}"; }
                case 0xbf: { string e = E(2); return $"movsx {Rv(Reg)}, {e}"; }
                case 0xba: { string e = Ev(); string[] n = { "?", "?", "?", "?", "bt", "bts", "btr", "btc" }; return $"{n[Reg]} {e}, {B()}"; }
                case 0xbc: { string e = Ev(); return $"bsf {Rv(Reg)}, {e}"; }
                case 0xbd: { string e = Ev(); return $"bsr {Rv(Reg)}, {e}"; }
                case 0xc0: { string e = E(1); return $"xadd {e}, {R8[Reg]}"; }
                case 0xc1: { string e = Ev(); return $"xadd {e}, {Rv(Reg)}"; }
                case 0xc7: { M(); return Reg == 1 ? "cmpxchg8b " + E(8) : $"(bad 0f c7 /{Reg})"; }
                case 0x77: return "emms";
            }
            if (b is (>= 0x60 and <= 0x7f) or (>= 0xd0 and <= 0xff)) { M(); string e = E(0); return $"mmx 0f {b:x2} {e}"; }
            return $"db 0x0f, {H(b)}";
        }
    }
}
