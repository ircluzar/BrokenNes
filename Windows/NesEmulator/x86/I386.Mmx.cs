// Pentium MMX: the 57 MMX instructions (our own code, not in tiny386). MM0-MM7 are the significands of the x87's physical registers
// 0-7 (not ST(i)): an MMX write sets that register's exponent to all ones, and every MMX instruction but EMMS sets TOP = 0 and tags all
// eight registers valid, so FSAVE / FRSTOR keep MMX state and a task switch with CR0.TS works as for the FPU.

using System;

namespace NesEmulator.X86;

public sealed partial class I386
{
    /// <summary>Is 0F b1 an MMX opcode (60-7F and D0-FF ranges)?</summary>
    private static bool MmxOpcode(uint b1) => b1 switch
    {
        >= 0x60 and <= 0x6b => true,
        0x6e or 0x6f or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x76 or 0x77 or 0x7e or 0x7f => true,
        0xd1 or 0xd2 or 0xd3 or 0xd5 or 0xd8 or 0xd9 or 0xdb or 0xdc or 0xdd or 0xdf => true,
        0xe1 or 0xe2 or 0xe5 or 0xe8 or 0xe9 or 0xeb or 0xec or 0xed or 0xef => true,
        0xf1 or 0xf2 or 0xf3 or 0xf5 or 0xf8 or 0xf9 or 0xfa or 0xfc or 0xfd or 0xfe => true,
        _ => false,
    };

    private ulong Mm(int i) => _fm[i];
    private void SetMm(int i, ulong v) { _fm[i] = v; _fe[i] = 0xffff; }

    private bool MmxRead64(out ulong v)
    {
        if (_mod == 3) { v = Mm(_rm); return true; }
        v = 0;
        if (!FRead(8)) return false;
        v = FGet64(0);
        return true;
    }

    private bool MmxRead32(out ulong v)
    {
        if (_mod == 3) { v = Mm(_rm); return true; }
        v = 0;
        if (!FRead(4)) return false;
        v = FGet32(0);
        return true;
    }

    private int MmxExec(uint b1)
    {
        if (!MmxOpcode(b1)) return K(Throw0(EX_UD));
        if ((Cr0 & 4) != 0) return K(Throw0(EX_UD));             // CR0.EM
        if ((Cr0 & 8) != 0) return K(Throw0(EX_NM));             // CR0.TS
        if ((_fsw & FSW_ES) != 0 && (Cr0 & 0x20) != 0) return K(Throw0(EX_MF));
        if (b1 == 0x77) { _fempty = 0xff; _ftop = 0; return OK; }   // EMMS (TOP is left alone on real chips: it is 0 after any MMX op anyway)
        if (!DecodeModrm()) return FAIL;
        int d = _reg;
        ulong a, b, r;
        switch (b1)
        {
            case 0x6e: // MOVD mm, r/m32
            {
                uint v;
                if (_mod == 3) v = R[_rm];
                else { if (!Translate(ref _meml, 1, _curSeg, _addr, 4)) return FAIL; v = Load32(ref _meml); }
                MmxTouch(); SetMm(d, v);
                return OK;
            }
            case 0x7e: // MOVD r/m32, mm
            {
                uint v = (uint)Mm(d);
                if (_mod == 3) R[_rm] = v;
                else { if (!Translate(ref _meml, 2, _curSeg, _addr, 4)) return FAIL; Store32(ref _meml, v); }
                MmxTouch();
                return OK;
            }
            case 0x6f: if (!MmxRead64(out b)) return FAIL; MmxTouch(); SetMm(d, b); return OK;   // MOVQ mm, mm/m64
            case 0x7f: // MOVQ mm/m64, mm
                if (_mod == 3) { MmxTouch(); SetMm(_rm, Mm(d)); return OK; }
                FPut64(0, Mm(d));
                if (!FWrite(8)) return FAIL;
                MmxTouch();
                return OK;
            case 0x71: case 0x72: case 0x73: // shift by an immediate
            {
                if (!Fetch8(out uint imm)) return FAIL;
                if (_mod != 3) return K(Throw0(EX_UD));
                int w = b1 == 0x71 ? 16 : b1 == 0x72 ? 32 : 64;
                int kind = _reg;   // 2 SRL, 4 SRA, 6 SLL
                if (kind != 2 && kind != 6 && !(kind == 4 && w != 64)) return K(Throw0(EX_UD));
                MmxTouch();
                SetMm(_rm, MmxShift(Mm(_rm), w, kind, imm));
                return OK;
            }
        }
        bool low32 = b1 is >= 0x60 and <= 0x62;
        if (low32 ? !MmxRead32(out b) : !MmxRead64(out b)) return FAIL;
        MmxTouch();
        a = Mm(d);
        switch (b1)
        {
            case 0x60: r = Interleave(a, b, 8, false); break;
            case 0x61: r = Interleave(a, b, 16, false); break;
            case 0x62: r = Interleave(a, b, 32, false); break;
            case 0x68: r = Interleave(a, b, 8, true); break;
            case 0x69: r = Interleave(a, b, 16, true); break;
            case 0x6a: r = Interleave(a, b, 32, true); break;
            case 0x63: r = Pack(a, b, 16, true); break;    // PACKSSWB
            case 0x67: r = Pack(a, b, 16, false); break;   // PACKUSWB
            case 0x6b: r = Pack(a, b, 32, true); break;    // PACKSSDW
            case 0x64: r = Lanes(a, b, 8, 20); break;  case 0x65: r = Lanes(a, b, 16, 20); break; case 0x66: r = Lanes(a, b, 32, 20); break; // PCMPGT
            case 0x74: r = Lanes(a, b, 8, 21); break;  case 0x75: r = Lanes(a, b, 16, 21); break; case 0x76: r = Lanes(a, b, 32, 21); break; // PCMPEQ
            case 0xd1: r = MmxShift(a, 16, 2, b); break; case 0xd2: r = MmxShift(a, 32, 2, b); break; case 0xd3: r = MmxShift(a, 64, 2, b); break;
            case 0xe1: r = MmxShift(a, 16, 4, b); break; case 0xe2: r = MmxShift(a, 32, 4, b); break;
            case 0xf1: r = MmxShift(a, 16, 6, b); break; case 0xf2: r = MmxShift(a, 32, 6, b); break; case 0xf3: r = MmxShift(a, 64, 6, b); break;
            case 0xd5: r = Lanes(a, b, 16, 30); break;   // PMULLW
            case 0xe5: r = Lanes(a, b, 16, 31); break;   // PMULHW
            case 0xf5: r = Pmaddwd(a, b); break;
            case 0xdb: r = a & b; break;
            case 0xdf: r = ~a & b; break;
            case 0xeb: r = a | b; break;
            case 0xef: r = a ^ b; break;
            case 0xfc: r = Lanes(a, b, 8, 0); break;  case 0xfd: r = Lanes(a, b, 16, 0); break; case 0xfe: r = Lanes(a, b, 32, 0); break;   // PADD
            case 0xf8: r = Lanes(a, b, 8, 1); break;  case 0xf9: r = Lanes(a, b, 16, 1); break; case 0xfa: r = Lanes(a, b, 32, 1); break;   // PSUB
            case 0xec: r = Lanes(a, b, 8, 2); break;  case 0xed: r = Lanes(a, b, 16, 2); break;   // PADDS
            case 0xe8: r = Lanes(a, b, 8, 3); break;  case 0xe9: r = Lanes(a, b, 16, 3); break;   // PSUBS
            case 0xdc: r = Lanes(a, b, 8, 4); break;  case 0xdd: r = Lanes(a, b, 16, 4); break;   // PADDUS
            case 0xd8: r = Lanes(a, b, 8, 5); break;  case 0xd9: r = Lanes(a, b, 16, 5); break;   // PSUBUS
            default: return K(Throw0(EX_UD));
        }
        SetMm(d, r);
        return OK;
    }

    /// <summary>Every MMX instruction but EMMS: TOP = 0, all registers valid.</summary>
    private void MmxTouch() { _ftop = 0; _fempty = 0; _ftagValid = 0xff; }

    private static ulong MmxShift(ulong v, int w, int kind, ulong count)
    {
        if (w == 64)
        {
            if (count > 63) return 0;
            return kind == 2 ? v >> (int)count : v << (int)count;
        }
        ulong r = 0;
        ulong mask = w == 16 ? 0xffffUL : 0xffffffffUL;
        for (int s = 0; s < 64; s += w)
        {
            ulong x = (v >> s) & mask, y;
            if (kind == 4)
            {
                long sx = w == 16 ? (short)x : (int)x;
                y = (ulong)(sx >> (int)Math.Min(count, (ulong)(w - 1))) & mask;
            }
            else if (count >= (ulong)w) y = 0;
            else y = (kind == 2 ? x >> (int)count : x << (int)count) & mask;
            r |= y << s;
        }
        return r;
    }

    private static ulong Interleave(ulong a, ulong b, int w, bool high)
    {
        ulong mask = w == 32 ? 0xffffffffUL : (1UL << w) - 1;
        int n = 32 / w, start = high ? 32 : 0;
        ulong r = 0;
        for (int i = 0; i < n; i++)
        {
            ulong x = (a >> (start + i * w)) & mask, y = (b >> (start + i * w)) & mask;
            r |= x << (2 * i * w);
            r |= y << ((2 * i + 1) * w);
        }
        return r;
    }

    private static ulong Pack(ulong a, ulong b, int w, bool signed)
    {
        // w = source lane width; result lanes are w / 2
        int o = w / 2;
        ulong r = 0;
        int n = 64 / w;
        for (int half = 0; half < 2; half++)
        {
            ulong src = half == 0 ? a : b;
            for (int i = 0; i < n; i++)
            {
                long x = w == 16 ? (short)(src >> (i * 16)) : (int)(src >> (i * 32));
                long lo, hi;
                if (signed) { lo = -(1L << (o - 1)); hi = (1L << (o - 1)) - 1; } else { lo = 0; hi = (1L << o) - 1; }
                long y = Math.Clamp(x, lo, hi);
                r |= ((ulong)y & ((1UL << o) - 1)) << ((half * n + i) * o);
            }
        }
        return r;
    }

    private static ulong Pmaddwd(ulong a, ulong b)
    {
        ulong r = 0;
        for (int i = 0; i < 2; i++)
        {
            int s = i * 32;
            int p = (short)(a >> s) * (short)(b >> s) + (short)(a >> (s + 16)) * (short)(b >> (s + 16));
            r |= (ulong)(uint)p << s;
        }
        return r;
    }

    /// <summary>Lane-wise op: 0 add, 1 sub, 2 add signed-saturate, 3 sub signed-saturate, 4 add unsigned-saturate, 5 sub unsigned-saturate,
    /// 20 compare greater (signed), 21 compare equal, 30 multiply low, 31 multiply high (signed).</summary>
    private static ulong Lanes(ulong a, ulong b, int w, int op)
    {
        ulong mask = w == 32 ? 0xffffffffUL : (1UL << w) - 1;
        long smin = -(1L << (w - 1)), smax = (1L << (w - 1)) - 1, umax = (long)mask;
        ulong r = 0;
        for (int s = 0; s < 64; s += w)
        {
            ulong x = (a >> s) & mask, y = (b >> s) & mask;
            long sx = (long)(x << (64 - w)) >> (64 - w), sy = (long)(y << (64 - w)) >> (64 - w);
            long z = op switch
            {
                0 => (long)(x + y),
                1 => (long)(x - y),
                2 => Math.Clamp(sx + sy, smin, smax),
                3 => Math.Clamp(sx - sy, smin, smax),
                4 => Math.Min((long)(x + y), umax),
                5 => Math.Max((long)x - (long)y, 0),
                20 => sx > sy ? -1 : 0,
                21 => x == y ? -1 : 0,
                30 => sx * sy,
                _ => (sx * sy) >> 16,
            };
            r |= ((ulong)z & mask) << s;
        }
        return r;
    }
}
