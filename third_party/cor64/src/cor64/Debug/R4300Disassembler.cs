// [dbg_disasm] Standalone VR4300 (R4300i) disassembler for the debugger. No emulation state, no hot-path use.
// Opcode tables follow the VR4300 User's Manual (cross-checked against the ares / cen64 decode tables; no code copied).
using System;
using System.Text;

namespace cor64.Debug
{
    public static class MipsNames
    {
        public static readonly string[] Gpr = {
            "zero", "at", "v0", "v1", "a0", "a1", "a2", "a3", "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7",
            "s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7", "t8", "t9", "k0", "k1", "gp", "sp", "fp", "ra" };
        public static readonly string[] Cop0 = {
            "Index", "Random", "EntryLo0", "EntryLo1", "Context", "PageMask", "Wired", "COP0_7",
            "BadVAddr", "Count", "EntryHi", "Compare", "Status", "Cause", "EPC", "PRId",
            "Config", "LLAddr", "WatchLo", "WatchHi", "XContext", "COP0_21", "COP0_22", "COP0_23",
            "COP0_24", "COP0_25", "PErr", "CacheErr", "TagLo", "TagHi", "ErrorEPC", "COP0_31" };
        public static string Fpr(int i) => "f" + i;
        public static string Hex(long v) => v < 0 ? "-0x" + (-v).ToString("X") : "0x" + v.ToString("X");
        public static string UHex(ulong v) => "0x" + v.ToString("X");
    }

    /// <summary>Resolves an address to a label (e.g. "func+0x1C"); null when unknown.</summary>
    public delegate string AddressLabeler(ulong address);

    public static class R4300Disassembler
    {
        static readonly string[] s_Special = new string[64];
        static readonly string[] s_RegImm = new string[32];
        static readonly string[] s_FpuFn = new string[64];
        static readonly string[] s_FpuCond = { "f", "un", "eq", "ueq", "olt", "ult", "ole", "ule", "sf", "ngle", "seq", "ngl", "lt", "nge", "le", "ngt" };

        static R4300Disassembler()
        {
            void S(int i, string n) => s_Special[i] = n;
            S(0, "sll"); S(2, "srl"); S(3, "sra"); S(4, "sllv"); S(6, "srlv"); S(7, "srav"); S(8, "jr"); S(9, "jalr");
            S(12, "syscall"); S(13, "break"); S(15, "sync"); S(16, "mfhi"); S(17, "mthi"); S(18, "mflo"); S(19, "mtlo");
            S(20, "dsllv"); S(22, "dsrlv"); S(23, "dsrav"); S(24, "mult"); S(25, "multu"); S(26, "div"); S(27, "divu");
            S(28, "dmult"); S(29, "dmultu"); S(30, "ddiv"); S(31, "ddivu"); S(32, "add"); S(33, "addu"); S(34, "sub"); S(35, "subu");
            S(36, "and"); S(37, "or"); S(38, "xor"); S(39, "nor"); S(42, "slt"); S(43, "sltu"); S(44, "dadd"); S(45, "daddu");
            S(46, "dsub"); S(47, "dsubu"); S(48, "tge"); S(49, "tgeu"); S(50, "tlt"); S(51, "tltu"); S(52, "teq"); S(54, "tne");
            S(56, "dsll"); S(58, "dsrl"); S(59, "dsra"); S(60, "dsll32"); S(62, "dsrl32"); S(63, "dsra32");
            void R(int i, string n) => s_RegImm[i] = n;
            R(0, "bltz"); R(1, "bgez"); R(2, "bltzl"); R(3, "bgezl"); R(8, "tgei"); R(9, "tgeiu"); R(10, "tlti"); R(11, "tltiu");
            R(12, "teqi"); R(14, "tnei"); R(16, "bltzal"); R(17, "bgezal"); R(18, "bltzall"); R(19, "bgezall");
            void F(int i, string n) => s_FpuFn[i] = n;
            F(0, "add"); F(1, "sub"); F(2, "mul"); F(3, "div"); F(4, "sqrt"); F(5, "abs"); F(6, "mov"); F(7, "neg");
            F(8, "round.l"); F(9, "trunc.l"); F(10, "ceil.l"); F(11, "floor.l"); F(12, "round.w"); F(13, "trunc.w"); F(14, "ceil.w"); F(15, "floor.w");
            F(32, "cvt.s"); F(33, "cvt.d"); F(36, "cvt.w"); F(37, "cvt.l");
            for (int i = 0; i < 16; i++) s_FpuFn[48 + i] = "c." + s_FpuCond[i];
        }

        static string G(int r) => MipsNames.Gpr[r];

        /// <summary>Mnemonic only (first word of the text), for tests and stats.</summary>
        public static string Mnemonic(uint word, ulong pc = 0)
        {
            var t = Disassemble(word, pc);
            int sp = t.IndexOf(' ');
            return sp < 0 ? t : t.Substring(0, sp);
        }

        /// <summary>True when the word decodes to a defined VR4300 instruction.</summary>
        public static bool IsValid(uint word) => !Disassemble(word, 0).StartsWith("invalid", StringComparison.Ordinal);

        /// <summary>Disassemble one word at virtual address <paramref name="pc"/> (low 32 bits used for targets).</summary>
        public static string Disassemble(uint w, ulong pc, AddressLabeler labeler = null)
        {
            int op = (int)(w >> 26), rs = (int)(w >> 21) & 31, rt = (int)(w >> 16) & 31, rd = (int)(w >> 11) & 31;
            int sa = (int)(w >> 6) & 31, fn = (int)w & 63;
            short simm = (short)w; ushort uimm = (ushort)w;
            uint pc32 = (uint)pc;
            string Br() { uint t = pc32 + 4 + (uint)(simm << 2); return Target(t, labeler); }
            string Mem() => MipsNames.Hex(simm) + "(" + G(rs) + ")";
            string Inv() => "invalid 0x" + w.ToString("X8");

            switch (op)
            {
                case 0: return Special(w, rs, rt, rd, sa, fn, labeler);
                case 1: {
                    var n = s_RegImm[rt]; if (n == null) return Inv();
                    if (n[0] == 't') return $"{n} {G(rs)}, {MipsNames.Hex(simm)}";
                    return $"{n} {G(rs)}, {Br()}";
                }
                case 2: case 3: {
                    uint t = ((pc32 + 4) & 0xF0000000u) | ((w & 0x03FFFFFFu) << 2);
                    return (op == 2 ? "j " : "jal ") + Target(t, labeler);
                }
                case 4: case 20:
                    if (op == 4 && rs == 0 && rt == 0) return "b " + Br();
                    return $"{(op == 4 ? "beq" : "beql")} {G(rs)}, {G(rt)}, {Br()}";
                case 5: case 21: return $"{(op == 5 ? "bne" : "bnel")} {G(rs)}, {G(rt)}, {Br()}";
                case 6: case 22: return rt != 0 ? Inv() : $"{(op == 6 ? "blez" : "blezl")} {G(rs)}, {Br()}";
                case 7: case 23: return rt != 0 ? Inv() : $"{(op == 7 ? "bgtz" : "bgtzl")} {G(rs)}, {Br()}";
                case 8: return $"addi {G(rt)}, {G(rs)}, {MipsNames.Hex(simm)}";
                case 9: return $"addiu {G(rt)}, {G(rs)}, {MipsNames.Hex(simm)}";
                case 10: return $"slti {G(rt)}, {G(rs)}, {MipsNames.Hex(simm)}";
                case 11: return $"sltiu {G(rt)}, {G(rs)}, {MipsNames.Hex(simm)}";
                case 12: return $"andi {G(rt)}, {G(rs)}, {MipsNames.UHex(uimm)}";
                case 13: return $"ori {G(rt)}, {G(rs)}, {MipsNames.UHex(uimm)}";
                case 14: return $"xori {G(rt)}, {G(rs)}, {MipsNames.UHex(uimm)}";
                case 15: return rs != 0 ? Inv() : $"lui {G(rt)}, {MipsNames.UHex(uimm)}";
                case 16: return Cop0(w, rs, rt, rd, fn);
                case 17: return Cop1(w, pc32, rs, rt, rd, sa, fn, labeler);
                case 18: return Cop2(w, rs, rt, rd);
                case 24: return $"daddi {G(rt)}, {G(rs)}, {MipsNames.Hex(simm)}";
                case 25: return $"daddiu {G(rt)}, {G(rs)}, {MipsNames.Hex(simm)}";
                case 47: return $"cache 0x{rt:X}, {Mem()}";
                case 49: return $"lwc1 {MipsNames.Fpr(rt)}, {Mem()}";
                case 53: return $"ldc1 {MipsNames.Fpr(rt)}, {Mem()}";
                case 57: return $"swc1 {MipsNames.Fpr(rt)}, {Mem()}";
                case 61: return $"sdc1 {MipsNames.Fpr(rt)}, {Mem()}";
            }
            string ls = op switch {
                26 => "ldl", 27 => "ldr", 32 => "lb", 33 => "lh", 34 => "lwl", 35 => "lw", 36 => "lbu", 37 => "lhu",
                38 => "lwr", 39 => "lwu", 40 => "sb", 41 => "sh", 42 => "swl", 43 => "sw", 44 => "sdl", 45 => "sdr",
                46 => "swr", 48 => "ll", 52 => "lld", 55 => "ld", 56 => "sc", 60 => "scd", 63 => "sd", _ => null };
            // 50 lwc2 / 54 ldc2 / 58 swc2 / 62 sdc2 and 51/59 (COP3) are not usable on the VR4300.
            return ls == null ? Inv() : $"{ls} {G(rt)}, {Mem()}";
        }

        static string Target(uint t, AddressLabeler labeler)
        {
            var s = "0x" + t.ToString("X8");
            var l = labeler?.Invoke(0xFFFFFFFF00000000UL * (t >> 31) | t);
            return l == null ? s : s + " <" + l + ">";
        }

        static string Special(uint w, int rs, int rt, int rd, int sa, int fn, AddressLabeler labeler)
        {
            if (w == 0) return "nop";
            var n = s_Special[fn];
            if (n == null) return "invalid 0x" + w.ToString("X8");
            switch (fn)
            {
                case 0: case 2: case 3: case 56: case 58: case 59: case 60: case 62: case 63:
                    return $"{n} {G(rd)}, {G(rt)}, {sa}";
                case 4: case 6: case 7: case 20: case 22: case 23:
                    return $"{n} {G(rd)}, {G(rt)}, {G(rs)}";
                case 8: return $"jr {G(rs)}";
                case 9: return rd == 31 ? $"jalr {G(rs)}" : $"jalr {G(rd)}, {G(rs)}";
                case 12: case 13: { uint code = (w >> 6) & 0xFFFFF; return code == 0 ? n : $"{n} 0x{code:X}"; }
                case 15: return "sync";
                case 16: case 18: return $"{n} {G(rd)}";
                case 17: case 19: return $"{n} {G(rs)}";
                case 24: case 25: case 26: case 27: case 28: case 29: case 30: case 31:
                case 48: case 49: case 50: case 51: case 52: case 54:
                    return $"{n} {G(rs)}, {G(rt)}";
                default: return $"{n} {G(rd)}, {G(rs)}, {G(rt)}";
            }
        }

        static string Cop0(uint w, int rs, int rt, int rd, int fn)
        {
            switch (rs)
            {
                case 0: return $"mfc0 {G(rt)}, {MipsNames.Cop0[rd]}";
                case 1: return $"dmfc0 {G(rt)}, {MipsNames.Cop0[rd]}";
                case 4: return $"mtc0 {G(rt)}, {MipsNames.Cop0[rd]}";
                case 5: return $"dmtc0 {G(rt)}, {MipsNames.Cop0[rd]}";
            }
            if (rs >= 16)
            {
                switch (fn) { case 1: return "tlbr"; case 2: return "tlbwi"; case 6: return "tlbwr"; case 8: return "tlbp"; case 24: return "eret"; }
            }
            return "invalid 0x" + w.ToString("X8");
        }

        static string Cop1(uint w, uint pc32, int rs, int rt, int rd, int sa, int fn, AddressLabeler labeler)
        {
            string fs = MipsNames.Fpr(rd);
            switch (rs)
            {
                case 0: return $"mfc1 {G(rt)}, {fs}";
                case 1: return $"dmfc1 {G(rt)}, {fs}";
                case 2: return $"cfc1 {G(rt)}, fcr{rd}";
                case 4: return $"mtc1 {G(rt)}, {fs}";
                case 5: return $"dmtc1 {G(rt)}, {fs}";
                case 6: return $"ctc1 {G(rt)}, fcr{rd}";
                case 8: {
                    string n = (rt & 3) switch { 0 => "bc1f", 1 => "bc1t", 2 => "bc1fl", _ => "bc1tl" };
                    uint t = pc32 + 4 + (uint)((short)w << 2);
                    return n + " " + Target(t, labeler);
                }
                case 16: case 17: case 20: case 21: {
                    char f = rs switch { 16 => 's', 17 => 'd', 20 => 'w', _ => 'l' };
                    var n = s_FpuFn[fn];
                    if (n == null) break;
                    if (rs >= 20 && fn != 32 && fn != 33) break; // W/L only convert
                    if ((fn == 32 && rs == 16) || (fn == 33 && rs == 17)) break; // cvt.s.s / cvt.d.d
                    string fd = MipsNames.Fpr(sa), ft = MipsNames.Fpr(rt);
                    if (fn >= 48) return $"{n}.{f} {fs}, {ft}";
                    if (fn <= 3) return $"{n}.{f} {fd}, {fs}, {ft}";
                    return $"{n}.{f} {fd}, {fs}";
                }
            }
            return "invalid 0x" + w.ToString("X8");
        }

        static string Cop2(uint w, int rs, int rt, int rd)
        {
            // The VR4300 has no COP2: these raise Coprocessor Unusable / Reserved Instruction. Shown for completeness.
            switch (rs)
            {
                case 0: return $"mfc2 {G(rt)}, {rd} ; cop2-invalid";
                case 1: return $"dmfc2 {G(rt)}, {rd} ; cop2-invalid";
                case 2: return $"cfc2 {G(rt)}, {rd} ; cop2-invalid";
                case 4: return $"mtc2 {G(rt)}, {rd} ; cop2-invalid";
                case 5: return $"dmtc2 {G(rt)}, {rd} ; cop2-invalid";
                case 6: return $"ctc2 {G(rt)}, {rd} ; cop2-invalid";
            }
            return "invalid 0x" + w.ToString("X8") + " ; cop2";
        }
    }
}
