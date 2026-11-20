// [dbg_disasm] Standalone RSP disassembler (scalar unit, COP0 DMA/DP registers, vector unit, LWC2/SWC2).
// Tables follow the RSP documentation; cross-checked against the ares / cen64 RSP decode tables (no code copied).
using System;

namespace cor64.Debug
{
    public static class RspDisassembler
    {
        public static readonly string[] Cop0 = {
            "SP_MEM_ADDR", "SP_DRAM_ADDR", "SP_RD_LEN", "SP_WR_LEN", "SP_STATUS", "SP_DMA_FULL", "SP_DMA_BUSY", "SP_SEMAPHORE",
            "DPC_START", "DPC_END", "DPC_CURRENT", "DPC_STATUS", "DPC_CLOCK", "DPC_BUFBUSY", "DPC_PIPEBUSY", "DPC_TMEM" };
        static readonly string[] s_Vu = new string[64];
        static readonly string[] s_Lv = { "lbv", "lsv", "llv", "ldv", "lqv", "lrv", "lpv", "luv", "lhv", "lfv", "lwv", "ltv" };
        static readonly string[] s_Sv = { "sbv", "ssv", "slv", "sdv", "sqv", "srv", "spv", "suv", "shv", "sfv", "swv", "stv" };
        static readonly int[] s_LvShift = { 0, 1, 2, 3, 4, 4, 3, 3, 4, 4, 4, 4 };

        static RspDisassembler()
        {
            string[] n = {
                "vmulf", "vmulu", "vrndp", "vmulq", "vmudl", "vmudm", "vmudn", "vmudh", "vmacf", "vmacu", "vrndn", "vmacq", "vmadl", "vmadm", "vmadn", "vmadh",
                "vadd", "vsub", null, "vabs", "vaddc", "vsubc", null, null, null, null, null, null, null, "vsar", null, null,
                "vlt", "veq", "vne", "vge", "vcl", "vch", "vcr", "vmrg", "vand", "vnand", "vor", "vnor", "vxor", "vnxor", null, null,
                "vrcp", "vrcpl", "vrcph", "vmov", "vrsq", "vrsql", "vrsqh", "vnop", null, null, null, null, null, null, null, "vnull" };
            Array.Copy(n, s_Vu, 64);
        }

        static string G(int r) => MipsNames.Gpr[r];
        static string V(int r) => "v" + r;

        /// <summary>Element suffix for a VU operand: "", "[0q]", "[1h]", "[3]" ...</summary>
        public static string Element(int e)
        {
            if (e < 2) return "";
            if (e < 4) return $"[{e - 2}q]";
            if (e < 8) return $"[{e - 4}h]";
            return $"[{e - 8}]";
        }

        public static string Disassemble(uint w, uint pc, AddressLabeler labeler = null)
        {
            int op = (int)(w >> 26), rs = (int)(w >> 21) & 31, rt = (int)(w >> 16) & 31, rd = (int)(w >> 11) & 31;
            int sa = (int)(w >> 6) & 31, fn = (int)w & 63;
            short simm = (short)w; ushort uimm = (ushort)w;
            string Br() { uint t = (pc + 4 + (uint)(simm << 2)) & 0xFFC; return T(t, labeler); }
            string Mem() => MipsNames.Hex(simm) + "(" + G(rs) + ")";
            string Inv() => "invalid 0x" + w.ToString("X8");
            switch (op)
            {
                case 0:
                    if (w == 0) return "nop";
                    switch (fn)
                    {
                        case 0: return $"sll {G(rd)}, {G(rt)}, {sa}";
                        case 2: return $"srl {G(rd)}, {G(rt)}, {sa}";
                        case 3: return $"sra {G(rd)}, {G(rt)}, {sa}";
                        case 4: return $"sllv {G(rd)}, {G(rt)}, {G(rs)}";
                        case 6: return $"srlv {G(rd)}, {G(rt)}, {G(rs)}";
                        case 7: return $"srav {G(rd)}, {G(rt)}, {G(rs)}";
                        case 8: return $"jr {G(rs)}";
                        case 9: return rd == 31 ? $"jalr {G(rs)}" : $"jalr {G(rd)}, {G(rs)}";
                        case 13: return "break";
                        case 32: case 33: case 34: case 35: case 36: case 37: case 38: case 39: case 42: case 43: {
                            string m = fn switch { 32 => "add", 33 => "addu", 34 => "sub", 35 => "subu", 36 => "and", 37 => "or", 38 => "xor", 39 => "nor", 42 => "slt", _ => "sltu" };
                            return $"{m} {G(rd)}, {G(rs)}, {G(rt)}";
                        }
                    }
                    return Inv();
                case 1: {
                    string m = rt switch { 0 => "bltz", 1 => "bgez", 16 => "bltzal", 17 => "bgezal", _ => null };
                    return m == null ? Inv() : $"{m} {G(rs)}, {Br()}";
                }
                case 2: case 3: return (op == 2 ? "j " : "jal ") + T((w << 2) & 0xFFC, labeler);
                case 4: return rs == 0 && rt == 0 ? "b " + Br() : $"beq {G(rs)}, {G(rt)}, {Br()}";
                case 5: return $"bne {G(rs)}, {G(rt)}, {Br()}";
                case 6: return $"blez {G(rs)}, {Br()}";
                case 7: return $"bgtz {G(rs)}, {Br()}";
                case 8: return $"addi {G(rt)}, {G(rs)}, {MipsNames.Hex(simm)}";
                case 9: return $"addiu {G(rt)}, {G(rs)}, {MipsNames.Hex(simm)}";
                case 10: return $"slti {G(rt)}, {G(rs)}, {MipsNames.Hex(simm)}";
                case 11: return $"sltiu {G(rt)}, {G(rs)}, {MipsNames.Hex(simm)}";
                case 12: return $"andi {G(rt)}, {G(rs)}, {MipsNames.UHex(uimm)}";
                case 13: return $"ori {G(rt)}, {G(rs)}, {MipsNames.UHex(uimm)}";
                case 14: return $"xori {G(rt)}, {G(rs)}, {MipsNames.UHex(uimm)}";
                case 15: return $"lui {G(rt)}, {MipsNames.UHex(uimm)}";
                case 16:
                    if (rs == 0) return $"mfc0 {G(rt)}, {Cop0[rd & 15]}";
                    if (rs == 4) return $"mtc0 {G(rt)}, {Cop0[rd & 15]}";
                    return Inv();
                case 18: return Cop2(w, rs, rt, rd, sa, fn);
                case 32: return $"lb {G(rt)}, {Mem()}";
                case 33: return $"lh {G(rt)}, {Mem()}";
                case 35: return $"lw {G(rt)}, {Mem()}";
                case 36: return $"lbu {G(rt)}, {Mem()}";
                case 37: return $"lhu {G(rt)}, {Mem()}";
                case 39: return $"lwu {G(rt)}, {Mem()}";
                case 40: return $"sb {G(rt)}, {Mem()}";
                case 41: return $"sh {G(rt)}, {Mem()}";
                case 43: return $"sw {G(rt)}, {Mem()}";
                case 50: case 58: {
                    if (rd >= 12) return Inv();
                    int e = (int)(w >> 7) & 15;
                    int off = ((int)(w << 25)) >> 25; // 7-bit signed
                    off <<= s_LvShift[rd];
                    string m = op == 50 ? s_Lv[rd] : s_Sv[rd];
                    return $"{m} {V(rt)}[{e}], {MipsNames.Hex(off)}({G(rs)})";
                }
            }
            return Inv();
        }

        static string T(uint t, AddressLabeler labeler)
        {
            var s = "0x" + t.ToString("X3");
            var l = labeler?.Invoke(t);
            return l == null ? s : s + " <" + l + ">";
        }

        static string Cop2(uint w, int rs, int rt, int rd, int sa, int fn)
        {
            if ((rs & 16) == 0)
            {
                int e = (int)(w >> 7) & 15;
                string cr = rd switch { 0 => "vco", 1 => "vcc", 2 => "vce", _ => "vc" + rd };
                switch (rs)
                {
                    case 0: return $"mfc2 {G(rt)}, {V(rd)}[{e}]";
                    case 2: return $"cfc2 {G(rt)}, {cr}";
                    case 4: return $"mtc2 {G(rt)}, {V(rd)}[{e}]";
                    case 6: return $"ctc2 {G(rt)}, {cr}";
                }
                return "invalid 0x" + w.ToString("X8");
            }
            int el = rs & 15;
            string n = s_Vu[fn];
            if (n == null) return "invalid 0x" + w.ToString("X8");
            int vd = sa, vs = rd, vt = rt;
            if (fn == 29)
            {
                string acc = el switch { 8 => "ACC_H", 9 => "ACC_M", 10 => "ACC_L", _ => "ACC_" + el };
                return $"vsar {V(vd)}, {acc}";
            }
            if (fn >= 48 && fn <= 54) return $"{n} {V(vd)}[{vs & 7}], {V(vt)}{Element(el)}";
            if (fn == 55 || fn == 63) return n;
            return $"{n} {V(vd)}, {V(vs)}, {V(vt)}{Element(el)}";
        }
    }
}
