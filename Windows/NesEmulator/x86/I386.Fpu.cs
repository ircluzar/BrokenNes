// Intel 80387 / 486 / Pentium x87 FPU for the CPU models that have one (our own code, not in tiny386).
//
// The eight registers are kept in the real 80-bit format (64-bit significand + sign / exponent), so FLD / FSTP m80, FXCH, FST ST(i),
// FSAVE / FRSTOR and the MMX registers (which alias the significands) are bit-exact. Arithmetic is done in IEEE double (53-bit
// significand) and converted back: precision control and the rounding mode of +, -, *, / are not modelled (round to nearest), but the
// integer stores (FIST / FISTP / FRNDINT / FBSTP) use the rounding mode from the control word, and FILD / FISTP m64 are exact.
// Exceptions: IE (invalid, stack over / underflow), ZE, OE are detected; DE / UE / PE are not. An unmasked exception sets ES / B and
// then raises #MF at the next waiting FPU instruction (CR0.NE set) or calls Ferr (CR0.NE clear: the PC's IRQ 13).

using System;
using System.IO;
using System.Numerics;

namespace NesEmulator.X86;

public sealed partial class I386
{
    private readonly ulong[] _fm = new ulong[8];     // significand of physical register i
    private readonly ushort[] _fe = new ushort[8];   // sign << 15 | biased exponent
    private int _fempty = 0xff;                      // bit i = physical register i is empty (tag 11)
    private int _ftop;
    private int _ftagValid;                          // bit i = tag 00 (valid) whatever the contents say (MMX writes, FRSTOR of a 00 tag)
    private uint _fcw = 0x37f, _fsw;                 // _fsw without TOP (bits 11-13)
    private uint _fip, _fcs, _fdp, _fds, _fop;       // last instruction / operand pointers (FSTENV)

    private const uint FSW_IE = 1, FSW_DE = 2, FSW_ZE = 4, FSW_OE = 8, FSW_UE = 0x10, FSW_PE = 0x20, FSW_SF = 0x40, FSW_ES = 0x80,
        FSW_C0 = 0x100, FSW_C1 = 0x200, FSW_C2 = 0x400, FSW_C3 = 0x4000, FSW_B = 0x8000, FSW_CC = FSW_C0 | FSW_C1 | FSW_C2 | FSW_C3;
    private const ulong IndefM = 0xc000000000000000; private const ushort IndefE = 0xffff;   // the "real indefinite" QNaN

    public uint FpuControl => _fcw;
    public uint FpuStatus => (_fsw & ~0x3800u) | ((uint)_ftop << 11);

    private void FpuInit()
    {
        _fcw = 0x37f; _fsw = 0; _ftop = 0; _fempty = 0xff; _ftagValid = 0;
        _fip = _fcs = _fdp = _fds = _fop = 0;
    }

    // ================= 80-bit <-> double =================

    private static void DoubleToExt(double d, out ulong m, out ushort se)
    {
        ulong bits = (ulong)BitConverter.DoubleToInt64Bits(d);
        ushort sign = (ushort)((bits >> 48) & 0x8000);
        int exp = (int)((bits >> 52) & 0x7ff);
        ulong frac = bits & 0xfffffffffffffUL;
        if (exp == 0x7ff) { se = (ushort)(sign | 0x7fff); m = frac == 0 ? 0x8000000000000000UL : 0xc000000000000000UL | (frac << 11); return; }
        if (exp == 0)
        {
            if (frac == 0) { se = sign; m = 0; return; }
            int lz = BitOperations.LeadingZeroCount(frac << 11);
            m = (frac << 11) << lz;
            se = (ushort)(sign | (1 - 1023 + 16383 - lz));
            return;
        }
        m = 0x8000000000000000UL | (frac << 11);
        se = (ushort)(sign | (exp - 1023 + 16383));
    }

    private static double ExtToDouble(ulong m, ushort se)
    {
        bool neg = (se & 0x8000) != 0;
        int e = se & 0x7fff;
        if (e == 0x7fff)
        {
            if ((m << 1) == 0) return neg ? double.NegativeInfinity : double.PositiveInfinity;
            ulong nan = 0x7ff8000000000000UL | ((m >> 11) & 0xfffffffffffffUL) | (neg ? 0x8000000000000000UL : 0);
            return BitConverter.Int64BitsToDouble((long)nan);
        }
        if (m == 0) return neg ? -0.0 : 0.0;
        if (e == 0) e = 1;   // an 80-bit denormal: same scale as exponent 1
        int lz = BitOperations.LeadingZeroCount(m);
        m <<= lz; e -= lz;
        // round the 64-bit significand to 53 bits, nearest-even
        double v = Math.ScaleB((double)m, e - 16383 - 63);
        return neg ? -v : v;
    }

    // ================= the register stack =================

    private int Phys(int i) => (_ftop + i) & 7;
    private bool StEmpty(int i) => ((_fempty >> Phys(i)) & 1) != 0;
    private double St(int i) { int p = Phys(i); return ExtToDouble(_fm[p], _fe[p]); }
    private void SetSt(int i, double v) { int p = Phys(i); DoubleToExt(v, out _fm[p], out _fe[p]); _fempty &= ~(1 << p); _ftagValid &= ~(1 << p); }
    private void SetStRaw(int i, ulong m, ushort se) { int p = Phys(i); _fm[p] = m; _fe[p] = se; _fempty &= ~(1 << p); _ftagValid &= ~(1 << p); }
    private void FPop() { _fempty |= 1 << Phys(0); _ftop = (_ftop + 1) & 7; }

    /// <summary>Raise exception bits; true when every one of them is masked (the masked response may go ahead).</summary>
    private bool FRaise(uint bits)
    {
        _fsw |= bits;
        uint unmasked = bits & ~_fcw & 0x3f;
        if (unmasked == 0) return true;
        if ((_fsw & FSW_ES) == 0)
        {
            _fsw |= FSW_ES | FSW_B;
            if ((Cr0 & 0x20) == 0) Ferr?.Invoke();
        }
        return false;
    }

    private void FPushRaw(ulong m, ushort se)
    {
        int p = (_ftop - 1) & 7;
        if (((_fempty >> p) & 1) == 0)
        {   // stack overflow
            _fsw |= FSW_C1;
            if (!FRaise(FSW_IE | FSW_SF)) return;
            m = IndefM; se = IndefE;
        }
        else _fsw &= ~FSW_C1;
        _ftop = p; _fm[p] = m; _fe[p] = se; _fempty &= ~(1 << p); _ftagValid &= ~(1 << p);
    }
    private void FPush(double v) { DoubleToExt(v, out ulong m, out ushort se); FPushRaw(m, se); }

    /// <summary>Stack underflow check for reading ST(i): true = it is empty (IE + SF raised, C1 cleared).</summary>
    private bool FUnder(int i)
    {
        if (!StEmpty(i)) return false;
        _fsw &= ~FSW_C1;
        FRaise(FSW_IE | FSW_SF);
        return true;
    }
    private bool FUnder2(int i, int j) { bool a = StEmpty(i), b = StEmpty(j); if (!a && !b) return false; _fsw &= ~FSW_C1; FRaise(FSW_IE | FSW_SF); return true; }
    private bool IeMasked => (_fcw & 1) != 0;
    private void SetIndef(int i) { if (IeMasked) SetStRaw(i, IndefM, IndefE); }

    // ================= memory =================

    private readonly OptAddr[] _fpm = new OptAddr[28];
    private readonly byte[] _fbuf = new byte[108];

    private uint FAddr(uint off) => _adsz16 ? (_addr + off) & 0xffff : _addr + off;

    /// <summary>Read n bytes at the operand into _fbuf (every piece translated before any is read).</summary>
    private bool FRead(int n)
    {
        int k = 0;
        for (int o = 0; o < n; o += 4, k++) if (!Translate(ref _fpm[k], 1, _curSeg, FAddr((uint)o), Math.Min(4, n - o))) return false;
        k = 0;
        for (int o = 0; o < n; o += 4, k++)
        {
            if (n - o >= 4) { uint v = Load32(ref _fpm[k]); _fbuf[o] = (byte)v; _fbuf[o + 1] = (byte)(v >> 8); _fbuf[o + 2] = (byte)(v >> 16); _fbuf[o + 3] = (byte)(v >> 24); }
            else { uint v = Load16(ref _fpm[k]); _fbuf[o] = (byte)v; _fbuf[o + 1] = (byte)(v >> 8); }
        }
        return true;
    }

    /// <summary>Write n bytes of _fbuf to the operand (all pieces translated first, so a page fault writes nothing).</summary>
    private bool FWrite(int n)
    {
        int k = 0;
        for (int o = 0; o < n; o += 4, k++) if (!Translate(ref _fpm[k], 2, _curSeg, FAddr((uint)o), Math.Min(4, n - o))) return false;
        k = 0;
        for (int o = 0; o < n; o += 4, k++)
        {
            if (n - o >= 4) Store32(ref _fpm[k], BitConverter.ToUInt32(_fbuf, o));
            else Store16(ref _fpm[k], BitConverter.ToUInt16(_fbuf, o));
        }
        return true;
    }

    private void FPut16(int o, uint v) { _fbuf[o] = (byte)v; _fbuf[o + 1] = (byte)(v >> 8); }
    private void FPut32(int o, uint v) { FPut16(o, v); FPut16(o + 2, v >> 16); }
    private void FPut64(int o, ulong v) { FPut32(o, (uint)v); FPut32(o + 4, (uint)(v >> 32)); }
    private uint FGet16(int o) => BitConverter.ToUInt16(_fbuf, o);
    private uint FGet32(int o) => BitConverter.ToUInt32(_fbuf, o);
    private ulong FGet64(int o) => BitConverter.ToUInt64(_fbuf, o);

    // ================= integers =================

    private static void LongToExt(long v, out ulong m, out ushort se)
    {
        if (v == 0) { m = 0; se = 0; return; }
        ushort sign = (ushort)(v < 0 ? 0x8000 : 0);
        ulong a = v < 0 ? (ulong)(-(v + 1)) + 1 : (ulong)v;
        int lz = BitOperations.LeadingZeroCount(a);
        m = a << lz;
        se = (ushort)(sign | (16383 + 63 - lz));
    }

    /// <summary>Round an 80-bit value to an integer with rounding mode rc (0 nearest, 1 down, 2 up, 3 chop). False = NaN / out of range.</summary>
    private static bool ExtToLong(ulong m, ushort se, int rc, int bits, out long result)
    {
        result = 0;
        bool neg = (se & 0x8000) != 0;
        int e = se & 0x7fff;
        if (e == 0x7fff) return false;
        if (m == 0) return true;
        int shift = 16383 + 63 - e;          // integer part = m >> shift
        ulong ip; bool fracNz, gtHalf, eqHalf;
        if (shift <= 0)
        {
            if (shift < 0) return false;     // >= 2^64
            ip = m; fracNz = gtHalf = eqHalf = false;
        }
        else if (shift < 64)
        {
            ip = m >> shift;
            ulong fr = m << (64 - shift);
            fracNz = fr != 0; gtHalf = fr > 0x8000000000000000UL; eqHalf = fr == 0x8000000000000000UL;
        }
        else if (shift == 64) { ip = 0; fracNz = true; gtHalf = m > 0x8000000000000000UL; eqHalf = m == 0x8000000000000000UL; }
        else { ip = 0; fracNz = true; gtHalf = false; eqHalf = false; }
        bool up = rc switch
        {
            0 => gtHalf || (eqHalf && (ip & 1) != 0),
            1 => neg && fracNz,
            2 => !neg && fracNz,
            _ => false,
        };
        if (up) { ip++; if (ip == 0) return false; }
        ulong lim = 1UL << (bits - 1);
        if (neg) { if (ip > lim) return false; result = ip == 0 ? 0 : -(long)(ip - 1) - 1; }
        else { if (ip >= lim) return false; result = (long)ip; }
        return true;
    }

    private int Rc => (int)((_fcw >> 10) & 3);

    private static double RoundRc(double v, int rc) => rc switch
    {
        0 => Math.Round(v, MidpointRounding.ToEven), 1 => Math.Floor(v), 2 => Math.Ceiling(v), _ => Math.Truncate(v),
    };

    // ================= arithmetic =================

    private static readonly double Indef = BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000000000UL));

    /// <summary>D8-style op (0 add, 1 mul, 4 sub, 5 subr, 6 div, 7 divr) of a and b; false = an unmasked exception (do not store).</summary>
    private bool FArith(int op, double a, double b, out double r)
    {
        r = op switch { 0 => a + b, 1 => a * b, 4 => a - b, 5 => b - a, 6 => a / b, _ => b / a };
        bool nanIn = double.IsNaN(a) || double.IsNaN(b);
        if (double.IsNaN(r) && !nanIn) { r = Indef; return FRaise(FSW_IE); }
        if (op >= 6)
        {
            double num = op == 6 ? a : b, den = op == 6 ? b : a;
            if (den == 0 && !nanIn && num != 0 && !double.IsInfinity(num)) return FRaise(FSW_ZE);
        }
        if (double.IsInfinity(r) && !double.IsInfinity(a) && !double.IsInfinity(b) && !nanIn) return FRaise(FSW_OE | FSW_PE);
        return true;
    }

    private void FCompare(double a, double b, bool unordered)
    {
        _fsw &= ~(FSW_C0 | FSW_C2 | FSW_C3 | FSW_C1);
        if (double.IsNaN(a) || double.IsNaN(b)) { _fsw |= FSW_C0 | FSW_C2 | FSW_C3; if (!unordered) FRaise(FSW_IE); }
        else if (a < b) _fsw |= FSW_C0;
        else if (a == b) _fsw |= FSW_C3;
    }

    private void FCompareEmpty() { _fsw |= FSW_C0 | FSW_C2 | FSW_C3; }

    private int FWaitCheck()
    {
        if ((_fsw & FSW_ES) != 0 && (Cr0 & 0x20) != 0) return K(Throw0(EX_MF));
        return OK;
    }

    /// <summary>WAIT / FWAIT on a chip with an FPU.</summary>
    private int FpuWait() => FWaitCheck();

    // ================= the ESC opcodes =================

    private int FpuExec(uint b1)
    {
        uint ip0 = Ip;
        if (!DecodeModrm()) return FAIL;
        int reg = _reg;
        int esc = (int)(b1 & 7);
        bool nowait = (esc == 1 && _mod != 3 && (reg == 6 || reg == 7)) || (esc == 5 && _mod != 3 && (reg == 6 || reg == 7))
            || (esc == 3 && _mod == 3 && reg == 4) || (esc == 7 && _mod == 3 && reg == 4);
        if (!nowait && (_fsw & ES_MASK) != 0 && (Cr0 & 0x20) != 0) return K(Throw0(EX_MF));
        bool control = nowait || (esc == 1 && _mod != 3 && (reg == 4 || reg == 5)) || (esc == 5 && _mod != 3 && reg == 4);
        if (!control)
        {
            _fip = ip0; _fcs = SegSel[SEG_CS]; _fop = ((b1 & 7) << 8) | (uint)((_mod << 6) | (reg << 3) | _rm);
            if (_mod != 3) { _fdp = _addr; _fds = SegSel[_curSeg < 0 ? SEG_DS : _curSeg]; }
        }
        return _mod != 3 ? FpuMem(esc, reg) : FpuReg(esc, reg, _rm);
    }
    private const uint ES_MASK = FSW_ES;

    private int FpuMem(int esc, int reg)
    {
        switch (esc)
        {
            case 0: case 2: case 4: case 6:
            {   // arithmetic with a memory operand: D8 m32real, DA m32int, DC m64real, DE m16int
                int n = esc == 4 ? 8 : esc == 6 ? 2 : 4;
                if (!FRead(n)) return FAIL;
                double b = esc switch
                {
                    0 => BitConverter.Int32BitsToSingle((int)FGet32(0)),
                    2 => (int)FGet32(0),
                    4 => BitConverter.Int64BitsToDouble((long)FGet64(0)),
                    _ => (short)FGet16(0),
                };
                if (reg == 2 || reg == 3)
                {
                    if (FUnder(0)) FCompareEmpty(); else FCompare(St(0), b, false);
                    if (reg == 3) FPop();
                    return OK;
                }
                if (FUnder(0)) { SetIndef(0); return OK; }
                if (FArith(reg, St(0), b, out double r)) SetSt(0, r);
                return OK;
            }
            case 1:
                switch (reg)
                {
                    case 0: if (!FRead(4)) return FAIL; FPush(BitConverter.Int32BitsToSingle((int)FGet32(0))); return OK;
                    case 2: case 3:
                    {
                        uint bits;
                        if (FUnder(0)) { if (!IeMasked) return OK; bits = 0xffc00000u; }
                        else bits = (uint)BitConverter.SingleToInt32Bits((float)St(0));
                        FPut32(0, bits);
                        if (!FWrite(4)) return FAIL;
                        if (reg == 3) FPop();
                        return OK;
                    }
                    case 4: return FLoadEnv(false);
                    case 5: if (!FRead(2)) return FAIL; _fcw = FGet16(0) | 0x40; FRecheck(); return OK;
                    case 6: if (!FStoreEnv(0)) return FAIL; _fcw |= 0x3f; return OK;
                    case 7: FPut16(0, _fcw); return K(FWrite(2));
                }
                break;
            case 3:
                switch (reg)
                {
                    case 0: { if (!FRead(4)) return FAIL; LongToExt((int)FGet32(0), out ulong m, out ushort se); FPushRaw(m, se); return OK; }
                    case 2: case 3: return FIntStore(32, reg == 3);
                    case 5: { if (!FRead(10)) return FAIL; FPushRaw(FGet64(0), (ushort)FGet16(8)); return OK; }
                    case 7:
                    {
                        ulong m; ushort se;
                        if (FUnder(0)) { if (!IeMasked) return OK; m = IndefM; se = IndefE; }
                        else { int p = Phys(0); m = _fm[p]; se = _fe[p]; }
                        FPut64(0, m); FPut16(8, se);
                        if (!FWrite(10)) return FAIL;
                        FPop();
                        return OK;
                    }
                }
                break;
            case 5:
                switch (reg)
                {
                    case 0: { if (!FRead(8)) return FAIL; FPush(BitConverter.Int64BitsToDouble((long)FGet64(0))); return OK; }
                    case 2: case 3:
                    {
                        if (FUnder(0)) { if (!IeMasked) return OK; FPut64(0, 0xfff8000000000000UL); }
                        else FPut64(0, (ulong)BitConverter.DoubleToInt64Bits(St(0)));
                        if (!FWrite(8)) return FAIL;
                        if (reg == 3) FPop();
                        return OK;
                    }
                    case 4: return FLoadEnv(true);
                    case 6:
                    {
                        int envn = FEnvSize;
                        FStoreEnvBuf();
                        for (int i = 0; i < 8; i++) { int p = Phys(i); FPut64(envn + i * 10, _fm[p]); FPut16(envn + i * 10 + 8, _fe[p]); }
                        if (!FWrite(envn + 80)) return FAIL;
                        FpuInit();
                        return OK;
                    }
                    case 7: FPut16(0, FpuStatus); return K(FWrite(2));
                }
                break;
            case 7:
                switch (reg)
                {
                    case 0: { if (!FRead(2)) return FAIL; LongToExt((short)FGet16(0), out ulong m, out ushort se); FPushRaw(m, se); return OK; }
                    case 2: case 3: return FIntStore(16, reg == 3);
                    case 4: return FBld();
                    case 5: { if (!FRead(8)) return FAIL; LongToExt((long)FGet64(0), out ulong m, out ushort se); FPushRaw(m, se); return OK; }
                    case 6: return FBstp();
                    case 7: return FIntStore(64, true);
                }
                break;
        }
        return K(Throw0(EX_UD));
    }

    private int FIntStore(int bits, bool pop)
    {
        long v;
        bool ok;
        if (FUnder(0)) ok = false;
        else { int p = Phys(0); ok = ExtToLong(_fm[p], _fe[p], Rc, bits, out v); if (ok) goto store; }
        if (!FRaise(FSW_IE)) return OK;
        v = bits == 16 ? unchecked((short)0x8000) : bits == 32 ? int.MinValue : long.MinValue;
    store:
        if (bits == 16) FPut16(0, (uint)v); else if (bits == 32) FPut32(0, (uint)v); else FPut64(0, (ulong)v);
        if (!FWrite(bits / 8)) return FAIL;
        if (pop) FPop();
        return OK;
    }

    private int FBld()
    {
        if (!FRead(10)) return FAIL;
        long v = 0;
        for (int i = 8; i >= 0; i--) { int b = _fbuf[i]; v = v * 100 + (b >> 4) * 10 + (b & 15); }
        if ((_fbuf[9] & 0x80) != 0) v = -v;
        LongToExt(v, out ulong m, out ushort se);
        if ((_fbuf[9] & 0x80) != 0 && v == 0) se = 0x8000;
        FPushRaw(m, se);
        return OK;
    }

    private int FBstp()
    {
        long v = 0;
        bool ok = !FUnder(0);
        if (ok) { int p = Phys(0); ok = ExtToLong(_fm[p], _fe[p], Rc, 64, out v) && v > -1_000_000_000_000_000_000L && v < 1_000_000_000_000_000_000L; }
        if (!ok)
        {
            if (!FRaise(FSW_IE)) return OK;
            for (int i = 0; i < 7; i++) _fbuf[i] = 0;
            _fbuf[7] = 0xc0; _fbuf[8] = 0xff; _fbuf[9] = 0xff;   // packed BCD indefinite
        }
        else
        {
            bool neg = v < 0 || (v == 0 && (_fe[Phys(0)] & 0x8000) != 0);
            ulong a = (ulong)Math.Abs(v);
            for (int i = 0; i < 9; i++) { int lo = (int)(a % 10); a /= 10; int hi = (int)(a % 10); a /= 10; _fbuf[i] = (byte)(hi << 4 | lo); }
            _fbuf[9] = (byte)(neg ? 0x80 : 0);
        }
        if (!FWrite(10)) return FAIL;
        FPop();
        return OK;
    }

    // ---- FSTENV / FLDENV / FSAVE / FRSTOR ----

    private bool PmNotV86 => (Cr0 & 1) != 0 && (Flags & VM) == 0;
    private int FEnvSize => _opsz16 ? 14 : 28;

    private uint FTagWord()
    {
        uint tw = 0;
        for (int p = 0; p < 8; p++)
        {
            uint t;
            if (((_fempty >> p) & 1) != 0) t = 3;
            else if (((_ftagValid >> p) & 1) != 0) t = 0;
            else
            {
                int e = _fe[p] & 0x7fff;
                if (e == 0x7fff || (e == 0 && _fm[p] != 0) || (e != 0 && (_fm[p] >> 63) == 0)) t = 2;
                else if (e == 0 && _fm[p] == 0) t = 1;
                else t = 0;
            }
            tw |= t << (p * 2);
        }
        return tw;
    }

    private void FStoreEnvBuf()
    {
        uint tw = FTagWord();
        if (_opsz16)
        {
            FPut16(0, _fcw); FPut16(2, FpuStatus); FPut16(4, tw);
            if (PmNotV86) { FPut16(6, _fip); FPut16(8, _fcs); FPut16(10, _fdp); FPut16(12, _fds); }
            else
            {
                uint lip = (_fcs << 4) + _fip, ldp = (_fds << 4) + _fdp;
                FPut16(6, lip); FPut16(8, ((lip >> 4) & 0xf000) | (_fop & 0x7ff)); FPut16(10, ldp); FPut16(12, (ldp >> 4) & 0xf000);
            }
        }
        else
        {
            FPut32(0, 0xffff0000 | _fcw); FPut32(4, 0xffff0000 | FpuStatus); FPut32(8, 0xffff0000 | tw);
            if (PmNotV86) { FPut32(12, _fip); FPut32(16, (_fop << 16) | (_fcs & 0xffff)); FPut32(20, _fdp); FPut32(24, 0xffff0000 | (_fds & 0xffff)); }
            else
            {
                uint lip = (_fcs << 4) + _fip, ldp = (_fds << 4) + _fdp;
                FPut32(12, 0xffff0000 | (lip & 0xffff)); FPut32(16, ((lip >> 4) & 0x0ffff000) | (_fop & 0x7ff));
                FPut32(20, 0xffff0000 | (ldp & 0xffff)); FPut32(24, (ldp >> 4) & 0x0ffff000);
            }
        }
    }

    private bool FStoreEnv(int _)
    {
        FStoreEnvBuf();
        return FWrite(FEnvSize);
    }

    private int FLoadEnv(bool withRegs)
    {
        int envn = FEnvSize;
        if (!FRead(withRegs ? envn + 80 : envn)) return FAIL;
        uint cw, sw, tw;
        if (_opsz16) { cw = FGet16(0); sw = FGet16(2); tw = FGet16(4); }
        else { cw = FGet16(0); sw = FGet16(4); tw = FGet16(8); }
        _fcw = cw | 0x40;
        _ftop = (int)((sw >> 11) & 7);
        _fsw = sw & ~0x3800u;
        _fempty = 0; _ftagValid = 0;
        for (int p = 0; p < 8; p++) { uint t = (tw >> (p * 2)) & 3; if (t == 3) _fempty |= 1 << p; else if (t == 0) _ftagValid |= 1 << p; }
        if (withRegs)
            for (int i = 0; i < 8; i++) { int p = Phys(i); _fm[p] = FGet64(envn + i * 10); _fe[p] = (ushort)FGet16(envn + i * 10 + 8); }
        FRecheck();
        return OK;
    }

    /// <summary>After FLDCW / FLDENV / FRSTOR: ES / B follow the unmasked pending exceptions.</summary>
    private void FRecheck()
    {
        bool pend = (_fsw & ~_fcw & 0x3f) != 0;
        if (pend) { if ((_fsw & FSW_ES) == 0) { _fsw |= FSW_ES | FSW_B; if ((Cr0 & 0x20) == 0) Ferr?.Invoke(); } }
        else _fsw &= ~(FSW_ES | FSW_B);
    }

    // ---- register forms ----

    private static readonly (ulong m, ushort e)[] FConst =
    {
        (0x8000000000000000UL, 0x3fff),   // 1
        (0xd49a784bcd1b8afeUL, 0x4000),   // log2(10)
        (0xb8aa3b295c17f0bcUL, 0x3fff),   // log2(e)
        (0xc90fdaa22168c235UL, 0x4000),   // pi
        (0x9a209a84fbcff799UL, 0x3ffd),   // log10(2)
        (0xb17217f7d1cf79acUL, 0x3ffe),   // ln(2)
        (0, 0),                           // +0
    };

    private int FpuReg(int esc, int reg, int i)
    {
        switch (esc)
        {
            case 0: case 4: case 6:
            {
                if (reg == 2 || reg == 3)
                {   // FCOM / FCOMP ST(i) (DC / DE aliases too); DE D9 = FCOMPP
                    if (esc == 6 && reg == 3 && i != 1) return K(Throw0(EX_UD));
                    if (FUnder2(0, i)) FCompareEmpty(); else FCompare(St(0), St(i), false);
                    if (reg == 3 || esc == 6) FPop();
                    if (esc == 6 && reg == 3) FPop();
                    return OK;
                }
                int dst = esc == 0 ? 0 : i;
                int op = esc == 0 ? reg : reg >= 4 ? reg ^ 1 : reg;
                if (FUnder2(0, i)) SetIndef(dst);
                else if (FArith(op, St(dst), St(dst == 0 ? i : 0), out double r)) SetSt(dst, r);
                if (esc == 6) FPop();
                return OK;
            }
            case 1:
                switch (reg)
                {
                    case 0:
                    {
                        if (FUnder(i)) { FPushRaw(IndefM, IndefE); return OK; }
                        int p = Phys(i);
                        FPushRaw(_fm[p], _fe[p]);
                        return OK;
                    }
                    case 1:
                    {
                        if (FUnder2(0, i)) { if (!IeMasked) return OK; if (StEmpty(0)) SetIndef(0); if (StEmpty(i)) SetIndef(i); }
                        int a = Phys(0), b = Phys(i);
                        (_fm[a], _fm[b]) = (_fm[b], _fm[a]); (_fe[a], _fe[b]) = (_fe[b], _fe[a]);
                        _fsw &= ~FSW_C1;
                        return OK;
                    }
                    case 2: if (i == 0) return OK; break;   // FNOP
                    case 3: return FStoreSt(i, true);      // FSTP1 alias
                    case 4:
                    {
                        if (i == 5) { FXam(); return OK; }
                        if (FUnder(0)) { SetIndef(0); return OK; }
                        int p = Phys(0);
                        switch (i)
                        {
                            case 0: _fe[p] ^= 0x8000; _fsw &= ~FSW_C1; return OK;
                            case 1: _fe[p] &= 0x7fff; _fsw &= ~FSW_C1; return OK;
                            case 4: FCompare(St(0), 0.0, false); return OK;
                        }
                        break;
                    }
                    case 5:
                        if (i == 7) break;
                        FPushRaw(FConst[i].m, FConst[i].e);
                        return OK;
                    case 6: case 7: return FTranscend(reg * 8 + i);
                }
                break;
            case 2:
                if (reg == 5 && i == 1)
                {   // FUCOMPP
                    if (FUnder2(0, 1)) FCompareEmpty(); else FCompare(St(0), St(1), true);
                    FPop(); FPop();
                    return OK;
                }
                break;
            case 3:
                if (reg == 4)
                {
                    switch (i)
                    {
                        case 0: case 1: case 4: return OK;                              // FNENI / FNDISI / FSETPM
                        case 2: _fsw &= ~(0x3fu | FSW_SF | FSW_ES | FSW_B); return OK;  // FNCLEX
                        case 3: FpuInit(); return OK;                                   // FNINIT
                    }
                }
                break;
            case 5:
                switch (reg)
                {
                    case 0: _fempty |= 1 << Phys(i); return OK;   // FFREE
                    case 1: return FpuReg(1, 1, i);              // FXCH4 alias
                    case 2: return FStoreSt(i, false);
                    case 3: return FStoreSt(i, true);
                    case 4: case 5:
                        if (FUnder2(0, i)) FCompareEmpty(); else FCompare(St(0), St(i), true);
                        if (reg == 5) FPop();
                        return OK;
                }
                break;
            case 7:
                switch (reg)
                {
                    case 0: _fempty |= 1 << Phys(i); FPop(); return OK;   // FFREEP
                    case 1: return FpuReg(1, 1, i);
                    case 2: case 3: return FStoreSt(i, true);
                    case 4: if (i == 0) { S16(0, FpuStatus); return OK; } break;   // FNSTSW AX
                }
                break;
        }
        return K(Throw0(EX_UD));
    }

    private int FStoreSt(int i, bool pop)
    {
        if (FUnder(0)) { if (!IeMasked) return OK; SetStRaw(i, IndefM, IndefE); }
        else { int p = Phys(0); SetStRaw(i, _fm[p], _fe[p]); }
        if (pop) FPop();
        return OK;
    }

    private void FXam()
    {
        _fsw &= ~FSW_CC;
        int p = Phys(0);
        if ((_fe[p] & 0x8000) != 0) _fsw |= FSW_C1;
        if (StEmpty(0)) { _fsw |= FSW_C3 | FSW_C0; return; }
        int e = _fe[p] & 0x7fff; ulong m = _fm[p];
        if (e == 0x7fff) _fsw |= (m << 1) == 0 ? FSW_C2 | FSW_C0 : FSW_C0;
        else if (e == 0) _fsw |= m == 0 ? FSW_C3 : FSW_C3 | FSW_C2;
        else if ((m >> 63) == 0) { }   // unnormal: unsupported (000)
        else _fsw |= FSW_C2;
    }

    private int FTranscend(int op)
    {
        // D9 F0-FF: op = reg * 8 + rm (48 = F0 ... 63 = FF)
        switch (op)
        {
            case 54: _ftop = (_ftop - 1) & 7; _fsw &= ~FSW_C1; return OK;   // FDECSTP
            case 55: _ftop = (_ftop + 1) & 7; _fsw &= ~FSW_C1; return OK;   // FINCSTP
        }
        bool two = op is 49 or 51 or 53 or 56 or 57 or 61;
        if (two ? FUnder2(0, 1) : FUnder(0))
        {
            SetIndef(0);
            if (op is 49 or 51 or 57) FPop();
            return OK;
        }
        double x = St(0);
        _fsw &= ~FSW_C2;
        switch (op)
        {
            case 48: SetSt(0, Math.Pow(2, x) - 1); return OK;                                 // F2XM1
            case 49: { double r = St(1) * Math.Log2(x); if (x < 0 || double.IsNaN(r) && !double.IsNaN(x)) { FRaise(FSW_IE); r = Indef; } else if (x == 0 && St(1) != 0) FRaise(FSW_ZE); SetSt(1, r); FPop(); return OK; } // FYL2X
            case 50:                                                                          // FPTAN
                if (Math.Abs(x) >= 9.223372036854775808e18) { _fsw |= FSW_C2; return OK; }
                SetSt(0, Math.Tan(x)); FPush(1.0); return OK;
            case 51: SetSt(1, Math.Atan2(St(1), x)); FPop(); return OK;                       // FPATAN
            case 52:                                                                          // FXTRACT
            {
                if (x == 0) { FRaise(FSW_ZE); SetSt(0, double.NegativeInfinity); FPush(x); return OK; }
                if (double.IsInfinity(x) || double.IsNaN(x)) { SetSt(0, double.IsNaN(x) ? x : double.PositiveInfinity); FPush(x); return OK; }
                int p = Phys(0);
                ulong m = _fm[p]; int e = _fe[p] & 0x7fff; ushort sign = (ushort)(_fe[p] & 0x8000);
                if (e == 0) e = 1;
                int lz = BitOperations.LeadingZeroCount(m); m <<= lz; e -= lz;
                SetSt(0, e - 16383);
                FPushRaw(m, (ushort)(sign | 16383));
                return OK;
            }
            case 53: case 56:                                                                 // FPREM1 / FPREM
            {
                double y = St(1);
                if (y == 0 || double.IsInfinity(x) || double.IsNaN(x) || double.IsNaN(y)) { FRaise(FSW_IE); SetIndef(0); return OK; }
                if (double.IsInfinity(y)) { _fsw &= ~(FSW_C0 | FSW_C1 | FSW_C3); return OK; }
                // the exact remainder in one go (C2 = 0: complete); the quotient bits come from (x - r) / y
                double r = op == 56 ? x % y : Math.IEEERemainder(x, y);
                double qi = Math.Round((x - r) / y);
                ulong qb = double.IsFinite(qi) && Math.Abs(qi) < 9e18 ? (ulong)Math.Abs(qi) : 0;
                _fsw &= ~(FSW_C0 | FSW_C1 | FSW_C3);
                if ((qb & 1) != 0) _fsw |= FSW_C1;
                if ((qb & 2) != 0) _fsw |= FSW_C3;
                if ((qb & 4) != 0) _fsw |= FSW_C0;
                SetSt(0, r);
                return OK;
            }
            case 57: { double r = St(1) * Math.Log2(x + 1); SetSt(1, r); FPop(); return OK; } // FYL2XP1
            case 58: if (x < 0) { FRaise(FSW_IE); SetIndef(0); return OK; } SetSt(0, Math.Sqrt(x)); return OK; // FSQRT
            case 59:                                                                          // FSINCOS
                if (Math.Abs(x) >= 9.223372036854775808e18) { _fsw |= FSW_C2; return OK; }
                SetSt(0, Math.Sin(x)); FPush(Math.Cos(x)); return OK;
            case 60:                                                                          // FRNDINT
            {
                int p = Phys(0);
                if (ExtToLong(_fm[p], _fe[p], Rc, 64, out long l)) { ushort sign = (ushort)(_fe[p] & 0x8000); LongToExt(l, out ulong m, out ushort se); if (l == 0) se = sign; SetStRaw(0, m, se); }
                else SetSt(0, RoundRc(x, Rc));   // huge values are integers already
                return OK;
            }
            case 61:                                                                          // FSCALE
            {
                double s = Math.Truncate(St(1));
                int n = s > 100000 ? 100000 : s < -100000 ? -100000 : (int)s;
                SetSt(0, Math.ScaleB(x, n));
                return OK;
            }
            case 62: case 63:                                                                 // FSIN / FCOS
                if (Math.Abs(x) >= 9.223372036854775808e18) { _fsw |= FSW_C2; return OK; }
                SetSt(0, op == 62 ? Math.Sin(x) : Math.Cos(x));
                return OK;
        }
        return K(Throw0(EX_UD));
    }

    // ================= save state =================

    private void SaveFpu(BinaryWriter w)
    {
        for (int i = 0; i < 8; i++) { w.Write(_fm[i]); w.Write(_fe[i]); }
        w.Write(_fempty); w.Write(_ftagValid); w.Write(_ftop); w.Write(_fcw); w.Write(_fsw);
        w.Write(_fip); w.Write(_fcs); w.Write(_fdp); w.Write(_fds); w.Write(_fop);
    }

    private void LoadFpu(BinaryReader r)
    {
        for (int i = 0; i < 8; i++) { _fm[i] = r.ReadUInt64(); _fe[i] = r.ReadUInt16(); }
        _fempty = r.ReadInt32(); _ftagValid = r.ReadInt32(); _ftop = r.ReadInt32(); _fcw = r.ReadUInt32(); _fsw = r.ReadUInt32();
        _fip = r.ReadUInt32(); _fcs = r.ReadUInt32(); _fdp = r.ReadUInt32(); _fds = r.ReadUInt32(); _fop = r.ReadUInt32();
    }
}
