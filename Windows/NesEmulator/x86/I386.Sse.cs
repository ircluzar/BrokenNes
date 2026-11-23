// Pentium III: SSE (our own code, not in tiny386). Eight 128-bit XMM registers + MXCSR, the packed / scalar single-precision instructions,
// the MMX extensions (PSHUFW, PINSRW, PEXTRW, PMOVMSKB, PMIN / PMAX, PAVG, PMULHUW, PSADBW, MOVNTQ, MASKMOVQ), PREFETCH (the P6 hint
// NOPs), SFENCE, LDMXCSR / STMXCSR, FXSAVE / FXRSTOR, and #XM (vector 19; #UD when CR4.OSXMMEXCPT is clear).
//
// Arithmetic is IEEE single precision with every MXCSR rounding mode: the operation is done in double (exact for MUL and the int <-> float
// conversions; for ADD / SUB / DIV / SQRT the exact error's sign comes from TwoSum / an FMA residual), then rounded once to float with the
// mode, so results and IE / DE / ZE / OE / UE / PE are bit-exact (tininess after rounding, FTZ). RCPPS / RSQRTPS are 12-bit approximations
// (the P3's own table is not reproduced: same error bound, not the same bits). DAZ does not exist on the P3 (MXCSR_MASK FFBF).
// Interpreter only: the JIT runs every SSE instruction out of line (they are not in its decoder, so DK_SLOW).

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed partial class I386
{
    private readonly uint[] _xmm = new uint[32];      // XMM i lane j = _xmm[i * 4 + j]
    private uint _mxcsr = 0x1f80;
    private uint _mxf;                                // exception flags raised by the instruction running
    private readonly uint[] _xs = new uint[4], _xr = new uint[4];
    private const uint CR4_OSFXSR = 0x200, CR4_OSXMMEXCPT = 0x400, MxcsrMask = 0xffbf;
    public const int EX_XM = 19;
    private const uint MX_IE = 1, MX_DE = 2, MX_ZE = 4, MX_OE = 8, MX_UE = 0x10, MX_PE = 0x20, MX_FTZ = 0x8000, QNanDefault = 0xffc00000;

    public uint Mxcsr => _mxcsr;
    public uint Xmm(int reg, int lane) => _xmm[reg * 4 + lane];

    private void SseReset() { Array.Clear(_xmm); _mxcsr = 0x1f80; }

    /// <summary>The 0F opcodes the P3 adds that use the XMM registers.</summary>
    private static bool SseXmmOp(uint b) => b is (>= 0x10 and <= 0x17) or (>= 0x28 and <= 0x2f) or (>= 0x50 and <= 0x59) or (>= 0x5c and <= 0x5f) or 0xc2 or 0xc6;
    /// <summary>The MMX extensions (MMX registers, MMX rules).</summary>
    private static bool SseMmxOp(uint b) => b is 0x70 or 0xc4 or 0xc5 or 0xd7 or 0xda or 0xde or 0xe0 or 0xe3 or 0xe4 or 0xe7 or 0xea or 0xee or 0xf6 or 0xf7;
    private static bool SseScalarForm(uint b) => b is 0x10 or 0x11 or 0x2a or 0x2c or 0x2d or 0x51 or 0x52 or 0x53 or 0x58 or 0x59 or 0x5c or 0x5d or 0x5e or 0x5f or 0xc2;

    private bool P66 => _opsz16 != _c16;

    private int SseExec(uint b1)
    {
        if (b1 == 0xae) return SseGrp15();
        if (SseMmxOp(b1))
        {
            if (P66 || _rep != 0) return K(Throw0(EX_UD));
            if ((Cr0 & 4) != 0) return K(Throw0(EX_UD));
            if ((Cr0 & 8) != 0) return K(Throw0(EX_NM));
            if ((_fsw & FSW_ES) != 0 && (Cr0 & 0x20) != 0) return K(Throw0(EX_MF));
            if (!DecodeModrm()) return FAIL;
            return MmxExtBody(b1);
        }
        bool ss = _rep == 1;
        if (P66 || _rep == 2 || (ss && !SseScalarForm(b1))) return K(Throw0(EX_UD));
        if ((Cr0 & 4) != 0 || (Cr4 & CR4_OSFXSR) == 0) return K(Throw0(EX_UD));
        if ((Cr0 & 8) != 0) return K(Throw0(EX_NM));
        // the MMX-register conversions also check a pending x87 exception (CVTPS2PI / CVTTPS2PI; CVTPI2PS from an MMX register only)
        if (!ss && b1 is 0x2c or 0x2d && (_fsw & FSW_ES) != 0 && (Cr0 & 0x20) != 0) return K(Throw0(EX_MF));
        if (!DecodeModrm()) return FAIL;
        if (!ss && b1 == 0x2a && _mod == 3 && (_fsw & FSW_ES) != 0 && (Cr0 & 0x20) != 0) return K(Throw0(EX_MF));
        _mxf = 0;
        return SseBody(b1, ss);
    }

    // ---------------- memory ----------------

    private bool Aligned16() => ((SegBase[_curSeg] + _addr) & 15) == 0;

    /// <summary>The source operand into _xs: the XMM register rm, or n bytes of memory (the other lanes 0). Packed m128 forms need 16-byte alignment.</summary>
    private bool XSrc(int n, bool align)
    {
        if (_mod == 3) { int s = _rm * 4; _xs[0] = _xmm[s]; _xs[1] = _xmm[s + 1]; _xs[2] = _xmm[s + 2]; _xs[3] = _xmm[s + 3]; return true; }
        if (align && !Aligned16()) return Throw(EX_GP, 0);
        if (n == 4) { if (!FLd32(out uint v)) return false; _xs[0] = v; _xs[1] = _xs[2] = _xs[3] = 0; return true; }
        if (!FRead(n)) return false;
        for (int i = 0; i < 4; i++) _xs[i] = i * 4 < n ? FGet32(i * 4) : 0;
        return true;
    }

    /// <summary>n bytes of XMM lanes from _xmm[from] to the memory operand.</summary>
    private bool XStore(int n, bool align, int from)
    {
        if (align && !Aligned16()) return Throw(EX_GP, 0);
        if (n == 4) return FSt32(_xmm[from]);
        for (int i = 0; i < n / 4; i++) FPut32(i * 4, _xmm[from + i]);
        return FWrite(n);
    }

    /// <summary>The instruction's exceptions: all masked = flags set, true; an unmasked one = flags set, #XM (or #UD without OSXMMEXCPT), the
    /// destination untouched. An unmasked pre-computation exception (IE DE ZE) keeps the post-computation flags (OE UE PE) unreported.</summary>
    private bool SseCommit()
    {
        uint f = _mxf; _mxf = 0;
        uint unm = f & ~(_mxcsr >> 7) & 0x3f;
        if (unm != 0)
        {
            if ((unm & 7) != 0) f &= 7;
            _mxcsr |= f;
            return Throw0((Cr4 & CR4_OSXMMEXCPT) != 0 ? EX_XM : EX_UD);
        }
        _mxcsr |= f;
        return true;
    }

    // ---------------- float helpers ----------------

    private static bool IsNanF(uint x) => (x & 0x7fffffff) > 0x7f800000;
    private static bool IsSnanF(uint x) => IsNanF(x) && (x & 0x400000) == 0;
    private static bool IsDenF(uint x) => (x & 0x7f800000) == 0 && (x & 0x7fffff) != 0;
    private static float F(uint x) => BitConverter.UInt32BitsToSingle(x);
    private static uint B(float f) => BitConverter.SingleToUInt32Bits(f);
    private int MxRc => (int)(_mxcsr >> 13) & 3;

    /// <summary>Rounds r (a double whose exact value is r + something of sign s) to float with the rounding mode rc.</summary>
    private static float RndF(double r, int s, int rc)
    {
        float f = (float)r;   // nearest, ties to even (r itself may already be rounded: resolved below)
        double fd = f;
        int dir = fd != r ? (r > fd ? 1 : -1) : s;   // the exact value's side of f
        if (dir == 0) return f;
        if (rc == 0)
        {
            if (fd == r) return f;   // r is a float: the exact value is within half a double ulp of it, nearer than any midpoint
            // r is exactly halfway between f and its neighbour: the exact value's side decides
            float g = r > fd ? MathF.BitIncrement(f) : MathF.BitDecrement(f);
            if (!float.IsInfinity(g) && ((double)f + g) * 0.5 == r && s != 0) return (s > 0) == (g > f) ? g : f;
            return f;
        }
        if (dir > 0 && (rc == 2 || (rc == 3 && f < 0))) return MathF.BitIncrement(f);
        if (dir < 0 && (rc == 1 || (rc == 3 && f > 0))) return MathF.BitDecrement(f);
        return f;
    }

    /// <summary>The rounded float of a finite r (exact = r + sign s): PE / OE / UE and FTZ; masked overflow gives inf or the largest float
    /// by the rounding mode.</summary>
    private uint RoundResult(double r, int s)
    {
        int rc = MxRc;
        if (double.IsInfinity(r)) return B((float)r);
        float f = RndF(r, s, rc);
        double a = Math.Abs(r);
        if (a > float.MaxValue)
        {   // overflow: rounded with an unbounded exponent (r / 2 rounds in range, exactly scaled)
            float h = RndF(r * 0.5, s, rc);
            if (Math.Abs(h) >= 1.7014118346046923e38f) _mxf |= MX_OE | MX_PE;
            else if ((double)f != r || s != 0) _mxf |= MX_PE;
            return B(f);
        }
        bool inexact = (double)f != r || s != 0;
        if (a != 0 && a < 1.1754943508222875e-38)
        {   // tininess after rounding: rounded with an unbounded exponent (scaled up 2^64, exact)
            float h = RndF(r * 18446744073709551616.0, s, rc);
            bool tiny = Math.Abs(h) < 1.1754943508222875e-38 * 18446744073709551616.0;
            if (tiny)
            {
                bool um = (_mxcsr & (MX_UE << 7)) != 0;
                if (!um) { _mxf |= MX_UE | (inexact ? MX_PE : 0); return B(f); }
                if ((_mxcsr & MX_FTZ) != 0) { _mxf |= MX_UE | MX_PE; return r < 0 ? 0x80000000u : 0; }
                if (inexact) _mxf |= MX_UE | MX_PE;
                return B(f);
            }
        }
        if (inexact) _mxf |= MX_PE;
        return B(f);
    }

    /// <summary>a op b: 0 ADD, 1 SUB, 2 MUL, 3 DIV, 4 SQRT (of b; a unused).</summary>
    private uint FArith(int op, uint a, uint b)
    {
        bool an = op != 4 && IsNanF(a), bn = IsNanF(b);
        if (an || bn)
        {
            if ((an && IsSnanF(a)) || (bn && IsSnanF(b))) _mxf |= MX_IE;
            return (an ? a : b) | 0x400000;   // the first operand's NaN wins, quieted
        }
        if ((op != 4 && IsDenF(a)) || IsDenF(b)) _mxf |= MX_DE;
        double da = F(a), db = F(b), r;
        int s = 0;
        switch (op)
        {
            case 0: case 1:
            {
                if (op == 1) db = -db;
                r = da + db;
                if (double.IsNaN(r)) { _mxf |= MX_IE; return QNanDefault; }
                if (double.IsFinite(r))
                {
                    double bb = r - da, err = (da - (r - bb)) + (db - bb);
                    s = Math.Sign(err);
                    if (r == 0 && s == 0 && MxRc == 1 && !(da == 0 && db == 0 && double.IsNegative(da) == double.IsNegative(db))) r = -0.0;
                }
                break;
            }
            case 2:
                r = da * db;   // exact (24 x 24 bits)
                if (double.IsNaN(r)) { _mxf |= MX_IE; return QNanDefault; }
                break;
            case 3:
                if (db == 0)
                {
                    if (da == 0) { _mxf |= MX_IE; return QNanDefault; }
                    if (double.IsFinite(da)) _mxf |= MX_ZE;
                    return ((a ^ b) & 0x80000000) | 0x7f800000;
                }
                r = da / db;
                if (double.IsNaN(r)) { _mxf |= MX_IE; return QNanDefault; }
                if (double.IsFinite(r) && double.IsFinite(da) && double.IsFinite(db)) s = Math.Sign(Math.FusedMultiplyAdd(-r, db, da)) * Math.Sign(db);
                break;
            default:
                if (db < 0) { _mxf |= MX_IE; return QNanDefault; }
                r = Math.Sqrt(db);
                if (double.IsFinite(r)) s = Math.Sign(Math.FusedMultiplyAdd(-r, r, db));
                break;
        }
        return RoundResult(r, s);
    }

    private uint FMinMax(bool max, uint a, uint b)
    {
        if (IsNanF(a) || IsNanF(b)) { _mxf |= MX_IE; return b; }   // any NaN: the source operand, as it is
        if (IsDenF(a) || IsDenF(b)) _mxf |= MX_DE;
        float x = F(a), y = F(b);
        return max ? (x > y ? a : b) : (x < y ? a : b);
    }

    private uint FCmp(int pred, uint a, uint b)
    {
        bool nan = IsNanF(a) || IsNanF(b);
        if (nan) { if (pred is 1 or 2 or 5 or 6 || IsSnanF(a) || IsSnanF(b)) _mxf |= MX_IE; }
        else if (IsDenF(a) || IsDenF(b)) _mxf |= MX_DE;
        float x = F(a), y = F(b);
        bool r = pred switch
        {
            0 => !nan && x == y, 1 => !nan && x < y, 2 => !nan && x <= y, 3 => nan,
            4 => nan || x != y, 5 => nan || !(x < y), 6 => nan || !(x <= y), _ => !nan,
        };
        return r ? 0xffffffffu : 0;
    }

    /// <summary>RCPPS / RSQRTPS: 12 significant bits (the P3's bound: relative error under 1.5 x 2^-12); no exceptions; tiny inputs are zero.</summary>
    private static uint FApprox(bool rsqrt, uint x)
    {
        if (IsNanF(x)) return x | 0x400000;
        uint sign = x & 0x80000000;
        if ((x & 0x7f800000) == 0) return sign | 0x7f800000;                  // +-0 and denormals: +-inf
        if (rsqrt && sign != 0) return QNanDefault;
        if ((x & 0x7fffffff) == 0x7f800000) return sign;                     // +-inf: +-0
        double v = F(x), r = rsqrt ? 1.0 / Math.Sqrt(v) : 1.0 / v;
        uint rb = B((float)r);
        rb = (rb + 0x400) & 0xfffff800;                                       // round to 12 significant bits
        if ((rb & 0x7f800000) == 0) return sign;                              // tiny results flush to zero
        return rb;
    }

    private uint CvtF2I(uint a, bool trunc)
    {
        if (IsNanF(a)) { _mxf |= MX_IE; return 0x80000000; }
        double v = F(a);
        double r = trunc ? Math.Truncate(v) : MxRc switch { 0 => Math.Round(v, MidpointRounding.ToEven), 1 => Math.Floor(v), 2 => Math.Ceiling(v), _ => Math.Truncate(v) };
        if (r < -2147483648.0 || r > 2147483647.0) { _mxf |= MX_IE; return 0x80000000; }
        if (r != v) _mxf |= MX_PE;
        return (uint)(int)r;
    }

    private uint CvtI2F(uint i) => RoundResult((int)i, 0);

    // ---------------- the XMM instructions ----------------

    private int SseBody(uint b1, bool ss)
    {
        int d = _reg * 4, rs = _rm * 4;
        switch (b1)
        {
            case 0x10:   // MOVUPS xmm, xmm/m128 | MOVSS xmm, xmm/m32
                if (ss && _mod == 3) { _xmm[d] = _xmm[rs]; return OK; }
                if (!XSrc(ss ? 4 : 16, false)) return FAIL;
                Array.Copy(_xs, 0, _xmm, d, 4);
                return OK;
            case 0x11:   // MOVUPS xmm/m128, xmm | MOVSS xmm/m32, xmm
                if (_mod == 3) { if (ss) _xmm[rs] = _xmm[d]; else Array.Copy(_xmm, d, _xmm, rs, 4); return OK; }
                return K(XStore(ss ? 4 : 16, false, d));
            case 0x12:   // MOVLPS xmm, m64 | MOVHLPS xmm, xmm
                if (_mod == 3) { _xmm[d] = _xmm[rs + 2]; _xmm[d + 1] = _xmm[rs + 3]; return OK; }
                if (!XSrc(8, false)) return FAIL;
                _xmm[d] = _xs[0]; _xmm[d + 1] = _xs[1];
                return OK;
            case 0x13:   // MOVLPS m64, xmm
                if (_mod == 3) return K(Throw0(EX_UD));
                return K(XStore(8, false, d));
            case 0x14: case 0x15:   // UNPCKLPS / UNPCKHPS
            {
                if (!XSrc(16, true)) return FAIL;
                int h = b1 == 0x15 ? 2 : 0;
                uint a0 = _xmm[d + h], a1 = _xmm[d + h + 1];
                _xmm[d] = a0; _xmm[d + 1] = _xs[h]; _xmm[d + 2] = a1; _xmm[d + 3] = _xs[h + 1];
                return OK;
            }
            case 0x16:   // MOVHPS xmm, m64 | MOVLHPS xmm, xmm
                if (_mod == 3) { uint l0 = _xmm[rs], l1 = _xmm[rs + 1]; _xmm[d + 2] = l0; _xmm[d + 3] = l1; return OK; }
                if (!XSrc(8, false)) return FAIL;
                _xmm[d + 2] = _xs[0]; _xmm[d + 3] = _xs[1];
                return OK;
            case 0x17:   // MOVHPS m64, xmm
                if (_mod == 3) return K(Throw0(EX_UD));
                return K(XStore(8, false, d + 2));
            case 0x28:   // MOVAPS xmm, xmm/m128
                if (!XSrc(16, true)) return FAIL;
                Array.Copy(_xs, 0, _xmm, d, 4);
                return OK;
            case 0x29:   // MOVAPS xmm/m128, xmm
                if (_mod == 3) { Array.Copy(_xmm, d, _xmm, rs, 4); return OK; }
                return K(XStore(16, true, d));
            case 0x2b:   // MOVNTPS m128, xmm
                if (_mod == 3) return K(Throw0(EX_UD));
                return K(XStore(16, true, d));
            case 0x2a:
                if (ss)
                {   // CVTSI2SS xmm, r/m32
                    uint v;
                    if (_mod == 3) v = R[_rm]; else if (!FLd32(out v)) return FAIL;
                    uint r = CvtI2F(v);
                    if (!SseCommit()) return FAIL;
                    _xmm[d] = r;
                    return OK;
                }
                else
                {   // CVTPI2PS xmm, mm/m64
                    ulong m;
                    if (_mod == 3) { m = Mm(_rm); MmxTouch(); } else if (!FLd64(out m)) return FAIL;
                    uint r0 = CvtI2F((uint)m), r1 = CvtI2F((uint)(m >> 32));
                    if (!SseCommit()) return FAIL;
                    _xmm[d] = r0; _xmm[d + 1] = r1;
                    return OK;
                }
            case 0x2c: case 0x2d:
            {
                bool trunc = b1 == 0x2c;
                if (ss)
                {   // CVT(T)SS2SI r32, xmm/m32
                    if (!XSrc(4, false)) return FAIL;
                    uint r = CvtF2I(_xs[0], trunc);
                    if (!SseCommit()) return FAIL;
                    R[_reg] = r;
                    return OK;
                }
                // CVT(T)PS2PI mm, xmm/m64
                if (!XSrc(8, false)) return FAIL;
                uint q0 = CvtF2I(_xs[0], trunc), q1 = CvtF2I(_xs[1], trunc);
                if (!SseCommit()) return FAIL;
                MmxTouch(); SetMm(_reg, q0 | (ulong)q1 << 32);
                return OK;
            }
            case 0x2e: case 0x2f:   // UCOMISS / COMISS xmm, xmm/m32
            {
                if (!XSrc(4, false)) return FAIL;
                uint a = _xmm[d], b = _xs[0];
                uint f;
                if (IsNanF(a) || IsNanF(b)) { if (b1 == 0x2f || IsSnanF(a) || IsSnanF(b)) _mxf |= MX_IE; f = ZF | PF | CF; }
                else
                {
                    if (IsDenF(a) || IsDenF(b)) _mxf |= MX_DE;
                    float x = F(a), y = F(b);
                    f = x < y ? CF : x == y ? ZF : 0;
                }
                if (!SseCommit()) return FAIL;
                Flags = (Flags & ~(CF | PF | AF | ZF | SF | OF)) | f;
                _ccMask = 0;
                return OK;
            }
            case 0x50:   // MOVMSKPS r32, xmm
                if (_mod != 3) return K(Throw0(EX_UD));
                R[_reg] = (_xmm[rs] >> 31) | ((_xmm[rs + 1] >> 31) << 1) | ((_xmm[rs + 2] >> 31) << 2) | ((_xmm[rs + 3] >> 31) << 3);
                return OK;
            case 0x54: case 0x55: case 0x56: case 0x57:   // ANDPS / ANDNPS / ORPS / XORPS
                if (!XSrc(16, true)) return FAIL;
                for (int i = 0; i < 4; i++)
                {
                    uint a = _xmm[d + i], b = _xs[i];
                    _xmm[d + i] = b1 switch { 0x54 => a & b, 0x55 => ~a & b, 0x56 => a | b, _ => a ^ b };
                }
                return OK;
            case 0xc6:   // SHUFPS xmm, xmm/m128, imm8
            {
                if (!Fetch8(out uint imm)) return FAIL;
                if (!XSrc(16, true)) return FAIL;
                uint r0 = _xmm[d + (int)(imm & 3)], r1 = _xmm[d + (int)((imm >> 2) & 3)], r2 = _xs[(imm >> 4) & 3], r3 = _xs[(imm >> 6) & 3];
                _xmm[d] = r0; _xmm[d + 1] = r1; _xmm[d + 2] = r2; _xmm[d + 3] = r3;
                return OK;
            }
        }
        // the lane-wise arithmetic: compute all lanes into _xr, then commit (an unmasked exception leaves the destination alone)
        uint pred = 0;
        if (b1 == 0xc2) { if (!Fetch8(out pred)) return FAIL; pred &= 7; }
        if (!XSrc(ss ? 4 : 16, !ss)) return FAIL;
        int n = ss ? 1 : 4;
        for (int i = 0; i < n; i++)
        {
            uint a = _xmm[d + i], b = _xs[i];
            _xr[i] = b1 switch
            {
                0x51 => FArith(4, 0, b),
                0x52 => FApprox(true, b),
                0x53 => FApprox(false, b),
                0x58 => FArith(0, a, b),
                0x59 => FArith(2, a, b),
                0x5c => FArith(1, a, b),
                0x5d => FMinMax(false, a, b),
                0x5e => FArith(3, a, b),
                0x5f => FMinMax(true, a, b),
                _ => FCmp((int)pred, a, b),   // C2: CMPPS / CMPSS
            };
        }
        if (!SseCommit()) return FAIL;
        Array.Copy(_xr, 0, _xmm, d, n);
        return OK;
    }

    // ---------------- 0F AE: FXSAVE FXRSTOR LDMXCSR STMXCSR SFENCE ----------------

    private readonly OptAddr[] _fxm = new OptAddr[72];
    private readonly byte[] _fxbuf = new byte[288];
    private const int FxLen = 288;   // header + x87 + XMM; the reserved tail (288-511) is never written

    private int SseGrp15()
    {
        if (P66 || _rep != 0) return K(Throw0(EX_UD));
        if (!DecodeModrm()) return FAIL;
        if (_mod == 3) return _reg == 7 ? OK : K(Throw0(EX_UD));   // SFENCE (LFENCE / MFENCE are SSE2)
        switch (_reg)
        {
            case 0: case 1:   // FXSAVE / FXRSTOR m512
                if ((Cr0 & 0xc) != 0) return K(Throw0(EX_NM));
                if (!Aligned16()) return K(Throw(EX_GP, 0));
                return _reg == 0 ? FxSave() : FxRstor();
            case 2: case 3:   // LDMXCSR / STMXCSR m32
            {
                if ((Cr0 & 4) != 0 || (Cr4 & CR4_OSFXSR) == 0) return K(Throw0(EX_UD));
                if ((Cr0 & 8) != 0) return K(Throw0(EX_NM));
                if (_reg == 3) return K(FSt32(_mxcsr));
                if (!FLd32(out uint v)) return FAIL;
                if ((v & ~MxcsrMask) != 0) return K(Throw(EX_GP, 0));
                _mxcsr = v;
                return OK;
            }
        }
        return K(Throw0(EX_UD));
    }

    private bool FxTranslate(int rwm)
    {
        for (int o = 0, k = 0; o < FxLen; o += 4, k++) if (!Translate(ref _fxm[k], rwm, _curSeg, FAddr((uint)o), 4)) return false;
        return true;
    }

    private void FxPut16(int o, uint v) { _fxbuf[o] = (byte)v; _fxbuf[o + 1] = (byte)(v >> 8); }
    private void FxPut32(int o, uint v) { FxPut16(o, v); FxPut16(o + 2, v >> 16); }

    private int FxSave()
    {
        if (!FxTranslate(2)) return FAIL;
        Array.Clear(_fxbuf);
        FxPut16(0, _fcw); FxPut16(2, FpuStatus);
        _fxbuf[4] = (byte)(~_fempty & 0xff);                // abridged tag: 1 = not empty (physical registers)
        FxPut16(6, _fop & 0x7ff);
        FxPut32(8, _fip); FxPut16(12, _fcs); FxPut32(16, _fdp); FxPut16(20, _fds);
        FxPut32(24, _mxcsr); FxPut32(28, 0);               // MXCSR_MASK 0 on the P3 (= the default FFBF)
        for (int i = 0; i < 8; i++)
        {
            int p = Phys(i), o = 32 + i * 16;
            FxPut32(o, (uint)_fm[p]); FxPut32(o + 4, (uint)(_fm[p] >> 32)); FxPut16(o + 8, _fe[p]);
        }
        for (int i = 0; i < 32; i++) FxPut32(160 + i * 4, _xmm[i]);
        for (int o = 0, k = 0; o < FxLen; o += 4, k++) Store32(ref _fxm[k], BitConverter.ToUInt32(_fxbuf, o));
        return OK;
    }

    private int FxRstor()
    {
        if (!FxTranslate(1)) return FAIL;
        for (int o = 0, k = 0; o < FxLen; o += 4, k++) BitConverter.TryWriteBytes(_fxbuf.AsSpan(o), Load32(ref _fxm[k]));
        uint mx = BitConverter.ToUInt32(_fxbuf, 24);
        if ((mx & ~MxcsrMask) != 0) return K(Throw(EX_GP, 0));
        uint sw = BitConverter.ToUInt16(_fxbuf, 2);
        _fcw = BitConverter.ToUInt16(_fxbuf, 0) | 0x40u;
        _ftop = (int)((sw >> 11) & 7); _fsw = sw & ~0x3800u;
        _fempty = ~_fxbuf[4] & 0xff; _ftagValid = 0;           // the full tags follow the contents again
        _fop = BitConverter.ToUInt16(_fxbuf, 6) & 0x7ffu;
        _fip = BitConverter.ToUInt32(_fxbuf, 8); _fcs = BitConverter.ToUInt16(_fxbuf, 12);
        _fdp = BitConverter.ToUInt32(_fxbuf, 16); _fds = BitConverter.ToUInt16(_fxbuf, 20);
        for (int i = 0; i < 8; i++)
        {
            int p = Phys(i), o = 32 + i * 16;
            _fm[p] = BitConverter.ToUInt64(_fxbuf, o); _fe[p] = BitConverter.ToUInt16(_fxbuf, o + 8);
        }
        for (int i = 0; i < 32; i++) _xmm[i] = BitConverter.ToUInt32(_fxbuf, 160 + i * 4);
        _mxcsr = mx;
        FRecheck();
        return OK;
    }

    // ---------------- the MMX extensions ----------------

    private int MmxExtBody(uint b1)
    {
        int d = _reg;
        switch (b1)
        {
            case 0x70:   // PSHUFW mm, mm/m64, imm8
            {
                if (!Fetch8(out uint imm)) return FAIL;
                if (!MmxRead64(out ulong s)) return FAIL;
                ulong r = 0;
                for (int i = 0; i < 4; i++) r |= ((s >> (int)(((imm >> (2 * i)) & 3) * 16)) & 0xffff) << (16 * i);
                MmxTouch(); SetMm(d, r);
                return OK;
            }
            case 0xc4:   // PINSRW mm, r32/m16, imm8
            {
                if (!Fetch8(out uint imm)) return FAIL;
                uint v;
                if (_mod == 3) v = R[_rm];
                else { if (!Translate(ref _meml, 1, _curSeg, _addr, 2)) return FAIL; v = Load16(ref _meml); }
                int sh = (int)(imm & 3) * 16;
                MmxTouch(); SetMm(d, (Mm(d) & ~(0xffffUL << sh)) | ((ulong)(v & 0xffff) << sh));
                return OK;
            }
            case 0xc5:   // PEXTRW r32, mm, imm8
            {
                if (_mod != 3) return K(Throw0(EX_UD));
                if (!Fetch8(out uint imm)) return FAIL;
                MmxTouch();
                R[_reg] = (uint)(Mm(_rm) >> (int)((imm & 3) * 16)) & 0xffff;
                return OK;
            }
            case 0xd7:   // PMOVMSKB r32, mm
            {
                if (_mod != 3) return K(Throw0(EX_UD));
                ulong s = Mm(_rm); uint m = 0;
                for (int i = 0; i < 8; i++) m |= (uint)((s >> (8 * i + 7)) & 1) << i;
                MmxTouch(); R[_reg] = m;
                return OK;
            }
            case 0xe7:   // MOVNTQ m64, mm
                if (_mod == 3) return K(Throw0(EX_UD));
                if (!FSt64(Mm(d))) return FAIL;
                MmxTouch();
                return OK;
            case 0xf7:   // MASKMOVQ mm1, mm2: the bytes of mm1 whose mm2 byte has bit 7 set, to DS:[(E)DI]
            {
                if (_mod != 3) return K(Throw0(EX_UD));
                int seg = _curSeg < 0 ? SEG_DS : _curSeg;
                uint di = _adsz16 ? R[7] & 0xffff : R[7];
                ulong v = Mm(d), m = Mm(_rm);
                for (int i = 0; i < 8; i++)
                    if (((m >> (8 * i + 7)) & 1) != 0 && !Translate(ref _fpm[i], 2, seg, _adsz16 ? (di + (uint)i) & 0xffff : di + (uint)i, 1)) return FAIL;
                for (int i = 0; i < 8; i++) if (((m >> (8 * i + 7)) & 1) != 0) Store8(ref _fpm[i], (uint)(v >> (8 * i)) & 0xff);
                MmxTouch();
                return OK;
            }
        }
        if (!MmxRead64(out ulong b)) return FAIL;
        ulong a = Mm(d), res = 0;
        switch (b1)
        {
            case 0xda: case 0xde: case 0xe0:   // PMINUB / PMAXUB / PAVGB
                for (int i = 0; i < 64; i += 8)
                {
                    uint x = (uint)(a >> i) & 0xff, y = (uint)(b >> i) & 0xff;
                    uint z = b1 == 0xda ? Math.Min(x, y) : b1 == 0xde ? Math.Max(x, y) : (x + y + 1) >> 1;
                    res |= (ulong)z << i;
                }
                break;
            case 0xea: case 0xee:   // PMINSW / PMAXSW
                for (int i = 0; i < 64; i += 16)
                {
                    short x = (short)(a >> i), y = (short)(b >> i);
                    res |= (ulong)(ushort)(b1 == 0xea ? Math.Min(x, y) : Math.Max(x, y)) << i;
                }
                break;
            case 0xe3: case 0xe4:   // PAVGW / PMULHUW
                for (int i = 0; i < 64; i += 16)
                {
                    uint x = (uint)(a >> i) & 0xffff, y = (uint)(b >> i) & 0xffff;
                    res |= (ulong)((b1 == 0xe3 ? (x + y + 1) >> 1 : (x * y) >> 16) & 0xffff) << i;
                }
                break;
            default:   // F6: PSADBW
            {
                uint sum = 0;
                for (int i = 0; i < 64; i += 8) sum += (uint)Math.Abs((int)((a >> i) & 0xff) - (int)((b >> i) & 0xff));
                res = sum;
                break;
            }
        }
        MmxTouch(); SetMm(d, res);
        return OK;
    }

    // ---------------- save state ----------------

    private void SaveSse(BinaryWriter w)
    {
        w.Write(0x31455353u);   // "SSE1"
        foreach (uint x in _xmm) w.Write(x);
        w.Write(_mxcsr);
    }

    private void LoadSse(BinaryReader r)
    {
        if (r.ReadUInt32() != 0x31455353u) throw new InvalidDataException("the state has no SSE block (not a Pentium III state)");
        for (int i = 0; i < 32; i++) _xmm[i] = r.ReadUInt32();
        _mxcsr = r.ReadUInt32();
    }
}
