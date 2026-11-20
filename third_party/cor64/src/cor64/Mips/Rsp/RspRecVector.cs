// [cer64plus] RSP recompiler, stage 2: vector ops with their decoded fields as compile-time constants.
//
// The recompiler emits a call to these AggressiveInlining helpers with every operand (registers, element, op flags)
// as an IL constant, so RyuJIT inlines them into the block and folds each op into straight-line SIMD:
// element broadcast as one in-register shuffle, the accumulator as two 256-bit loads/stores.
// The arithmetic is VectorMultiplySimd's, which is bit-identical to the scalar lane loop.
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace cor64.Mips.Rsp
{
    public partial class RspInterpreter
    {
        private static readonly bool s_RecVector = Environment.GetEnvironmentVariable("COR64_RSP_REC_VEC") != "0"
            && Avx2.IsSupported && Ssse3.IsSupported && Environment.GetEnvironmentVariable("COR64_RSP_SIMD") != "0";

        private static readonly MethodInfo s_RecVMul = typeof(RspInterpreter).GetMethod(nameof(RecVMul), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo s_GetAcc = typeof(InterpreterBaseRsp).GetProperty("Acc").GetGetMethod();
        private static readonly MethodInfo s_GetAccRaw = typeof(Accumulator).GetProperty("Raw").GetGetMethod();

        // Vector registers as one contiguous block of 32 x 8 lanes (true for the interpreter's own register file).
        private unsafe short* RecVectorBase()
        {
            if (!s_RecVector) return null;
            short* b = m_VecRegs[0].RawLanes;
            for (int i = 0; i < 32; i++) if (!m_VecRegs[i].IsPlain || m_VecRegs[i].RawLanes != b + i * 8) return null;
            return b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe Vector128<short> RecLoadVt(short* regs, int vt, int e)
        {
            var t = Vector128.Load(regs + vt * 8);
            if ((e & 0xE) != 0) t = Ssse3.Shuffle(t.AsByte(), RspVectorHelper.s_Masks[e]).AsInt16();
            return t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVMul(short* regs, ulong[] accArr, int vd, int vs, int vt, int e,
            bool unsigned, bool acc, bool lp, bool mp, bool np, bool hp)
        {
            var s16 = Vector128.Load(regs + vs * 8);
            var t16 = RecLoadVt(regs, vt, e);
            Vector256<int> sw = (np || lp) ? Avx2.ConvertToVector256Int32(s16.AsUInt16()) : Avx2.ConvertToVector256Int32(s16);
            Vector256<int> tw = (mp || lp) ? Avx2.ConvertToVector256Int32(t16.AsUInt16()) : Avx2.ConvertToVector256Int32(t16);
            var p32 = Avx2.MultiplyLow(sw, tw);
            var pLo = Avx2.ConvertToVector256Int64(p32.GetLower());   // lanes k=0..3 = accumulator 7..4
            var pHi = Avx2.ConvertToVector256Int64(p32.GetUpper());   // lanes k=4..7 = accumulator 3..0
            if (!mp && !np) {
                if (lp) { var m = Vector256.Create(0xFFFFL); pLo = Avx2.ShiftRightLogical(pLo, 16) & m; pHi = Avx2.ShiftRightLogical(pHi, 16) & m; }
                else if (hp) { pLo = Avx2.ShiftLeftLogical(pLo, 16); pHi = Avx2.ShiftLeftLogical(pHi, 16); }
                else {
                    pLo = Avx2.ShiftLeftLogical(pLo, 1); pHi = Avx2.ShiftLeftLogical(pHi, 1);
                    if (!acc) { var r = Vector256.Create(0x8000L); pLo += r; pHi += r; }
                }
            }
            ref ulong a0 = ref MemoryMarshal.GetArrayDataReference(accArr);
            ref var a03 = ref Unsafe.As<ulong, Vector256<long>>(ref a0);
            ref var a47 = ref Unsafe.As<ulong, Vector256<long>>(ref Unsafe.Add(ref a0, 4));
            if (acc) {
                pLo += Avx2.Permute4x64(a47, 0x1B);
                pHi += Avx2.Permute4x64(a03, 0x1B);
            }
            a47 = Avx2.Permute4x64(pLo, 0x1B);
            a03 = Avx2.Permute4x64(pHi, 0x1B);

            var xLo = Low32(Avx2.ShiftRightLogical(pLo, 16)); var xHi = Low32(Avx2.ShiftRightLogical(pHi, 16)); // acc bits 47..16
            var am = Sse2.PackSignedSaturate(xLo, xHi);              // ClampAM
            Vector128<short> res;
            if (lp || (np && acc)) {
                var accm = Vector128.Narrow(xLo, xHi);
                var lo16 = Vector128.Narrow(Low32(pLo), Low32(pHi));
                var same = Vector128.Equals(am, accm);
                res = Vector128.ConditionalSelect(same, lo16, am ^ Vector128.Create(unchecked((short)0x8000)));
            }
            else if (np) res = Vector128.Narrow(Low32(pLo), Low32(pHi));
            else if (unsigned) {
                var accm = Vector128.Narrow(xLo, xHi);
                var pos = am & ~Vector128.ShiftRightArithmetic(am, 15);
                res = pos | Vector128.GreaterThan(am, accm);
            }
            else res = am;
            res.Store(regs + vd * 8);
        }

        // [climb4 sm64_300] COR64_SM300_ACC (see RecEmitVector): accumulator kept in locals across a multiply run.
        internal static readonly bool s_Sm300Acc = Environment.GetEnvironmentVariable("COR64_SM300_ACC") != "0";
        private LocalBuilder m_SmAccLo, m_SmAccHi; private bool m_SmCached, m_SmNextMul;
        private static readonly MethodInfo s_RecVMulC = typeof(RspInterpreter).GetMethod(nameof(RecVMulC), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecAccLoad = typeof(RspInterpreter).GetMethod(nameof(RecAccLoad), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecAccStore = typeof(RspInterpreter).GetMethod(nameof(RecAccStore), BindingFlags.Static | BindingFlags.NonPublic);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RecAccLoad(ulong[] accArr, ref Vector256<long> lo, ref Vector256<long> hi)
        {
            ref ulong a0 = ref MemoryMarshal.GetArrayDataReference(accArr);
            lo = Avx2.Permute4x64(Unsafe.As<ulong, Vector256<long>>(ref Unsafe.Add(ref a0, 4)), 0x1B);
            hi = Avx2.Permute4x64(Unsafe.As<ulong, Vector256<long>>(ref a0), 0x1B);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RecAccStore(ulong[] accArr, Vector256<long> lo, Vector256<long> hi)
        {
            ref ulong a0 = ref MemoryMarshal.GetArrayDataReference(accArr);
            Unsafe.As<ulong, Vector256<long>>(ref Unsafe.Add(ref a0, 4)) = Avx2.Permute4x64(lo, 0x1B);
            Unsafe.As<ulong, Vector256<long>>(ref a0) = Avx2.Permute4x64(hi, 0x1B);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVMulC(short* regs, ref Vector256<long> accLo, ref Vector256<long> accHi, int vd, int vs, int vt, int e,
            bool unsigned, bool acc, bool lp, bool mp, bool np, bool hp)
        {
            var s16 = Vector128.Load(regs + vs * 8);
            var t16 = RecLoadVt(regs, vt, e);
            Vector256<int> sw = (np || lp) ? Avx2.ConvertToVector256Int32(s16.AsUInt16()) : Avx2.ConvertToVector256Int32(s16);
            Vector256<int> tw = (mp || lp) ? Avx2.ConvertToVector256Int32(t16.AsUInt16()) : Avx2.ConvertToVector256Int32(t16);
            var p32 = Avx2.MultiplyLow(sw, tw);
            var pLo = Avx2.ConvertToVector256Int64(p32.GetLower());
            var pHi = Avx2.ConvertToVector256Int64(p32.GetUpper());
            if (!mp && !np) {
                if (lp) { var m = Vector256.Create(0xFFFFL); pLo = Avx2.ShiftRightLogical(pLo, 16) & m; pHi = Avx2.ShiftRightLogical(pHi, 16) & m; }
                else if (hp) { pLo = Avx2.ShiftLeftLogical(pLo, 16); pHi = Avx2.ShiftLeftLogical(pHi, 16); }
                else {
                    pLo = Avx2.ShiftLeftLogical(pLo, 1); pHi = Avx2.ShiftLeftLogical(pHi, 1);
                    if (!acc) { var r = Vector256.Create(0x8000L); pLo += r; pHi += r; }
                }
            }
            if (acc) { pLo += accLo; pHi += accHi; }
            accLo = pLo; accHi = pHi;
            var xLo = Low32(Avx2.ShiftRightLogical(pLo, 16)); var xHi = Low32(Avx2.ShiftRightLogical(pHi, 16));
            var am = Sse2.PackSignedSaturate(xLo, xHi);
            Vector128<short> res;
            if (lp || (np && acc)) {
                var accm = Vector128.Narrow(xLo, xHi);
                var lo16 = Vector128.Narrow(Low32(pLo), Low32(pHi));
                var same = Vector128.Equals(am, accm);
                res = Vector128.ConditionalSelect(same, lo16, am ^ Vector128.Create(unchecked((short)0x8000)));
            }
            else if (np) res = Vector128.Narrow(Low32(pLo), Low32(pHi));
            else if (unsigned) {
                var accm = Vector128.Narrow(xLo, xHi);
                var pos = am & ~Vector128.ShiftRightArithmetic(am, 15);
                res = pos | Vector128.GreaterThan(am, accm);
            }
            else res = am;
            res.Store(regs + vd * 8);
        }

        // [cer64plus] LPV (sh 8) / LUV (sh 7) on a plain register: TryVectorLoadStore's loop on raw pointers.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVPack(short* regs, byte* M, uint rs, int vt, int e, int imm, int sh)
        {
            long address = (long)rs + imm * 8;
            int index = (int)(address & 7) - e;
            address &= ~7L;
            short* V = regs + vt * 8;
            for (int o = 0; o < 8; o++) V[7 - o] = (short)(ushort)(M[((address + ((index + o) & 15)) & 0xFFF) ^ 3] << sh);
        }
        private static readonly MethodInfo s_RecVPack = M(nameof(RecVPack));

        // [cer64plus] VRCP/VRSQ family on plain registers: VectorReciprocal with constant operands.
        private unsafe void RecVRcp(short* regs, ulong[] accArr, int vt, int vd, int e, int de, bool useSqrt, bool high, bool low)
        {
            short* T = regs + vt * 8;
            bool doublePrecision = high || m_lastDoublePrecision;
            int shift = high ? 16 : 0;
            if (low && m_lastDoublePrecision) m_DivIn |= (ushort)T[7 - e] & 0xFFFF;
            else m_DivIn = T[7 - e] << shift;
            if (!high) {
                int _data = m_DivIn, _address, _shift;
                if (!doublePrecision) _data = (_data < 0) ? -_data : _data;
                if (doublePrecision && _data < 0) _data = (_data >= -32768) ? -_data : ~_data;
                _address = _data;
                if (_data == 0) { _shift = !doublePrecision ? 16 : 0; _address <<= _shift; }
                else { _shift = 0; while (_address >= 0) { _address <<= 1; _shift++; } }
                _address = (_address >> 22) & 0x1FF;
                if (useSqrt) { _address &= 0x1FE; _address |= 0x200 | (_shift & 1); }
                _shift ^= 31;
                if (useSqrt) _shift >>= 1;
                m_DivOut = (int)((0x40000000U | ((uint)DivideTable.ReadValue(_address) << 14)) >> _shift);
                if (m_DivIn == 0) m_DivOut = 0x7FFFFFFF;
                else if (m_DivIn == -32768) m_DivOut = -0x00010000;
                else m_DivOut ^= (m_DivIn < 0) ? ~0 : 0;
            }
            RecAccLo(accArr, Vector128.Load(T));
            regs[vd * 8 + 7 - de] = (short)(m_DivOut >> shift);
            m_lastDoublePrecision = high;
        }
        private static readonly MethodInfo s_RecVRcp = typeof(RspInterpreter).GetMethod(nameof(RecVRcp), BindingFlags.Instance | BindingFlags.NonPublic);

        // [climb4 rsp_top_blocks] VRCP family specialized at compile time. The previous VRCP-family op of the block
        // tells the emitter m_lastDoublePrecision (-1 unknown, 0 false, 1 true); reset at every block start.
        [ThreadStatic] internal static int t_RcpDP;
        internal static readonly bool s_RcpSpec = Environment.GetEnvironmentVariable("COR64_RSPTB_RCPSPEC") != "0";

        // VRCPH / VRSQH: DivIn = T[e] << 16; Vd[de] = DivOut >> 16; lastDP = true
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVRcpH(RspInterpreter s, short* regs, ulong[] accArr, int vt, int ti, int di)
        {
            short* T = regs + vt * 8;
            s.m_DivIn = T[ti] << 16;
            RecAccLo(accArr, Vector128.Load(T));
            regs[di] = (short)(s.m_DivOut >> 16);
            s.m_lastDoublePrecision = true;
        }
        private static readonly MethodInfo s_RecVRcpH = M(nameof(RecVRcpH));

        // VRCP / VRCPL / VRSQ / VRSQL with lastDP known (dp); or = merge the low half into DivIn (low && dp)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVRcpL(RspInterpreter s, short* regs, ulong[] accArr, int vt, int ti, int di, bool useSqrt, bool dp, bool or)
        {
            short* T = regs + vt * 8;
            int x = or ? (s.m_DivIn | (ushort)T[ti]) : T[ti];
            s.m_DivIn = x;
            int o;
            if (x == 0) o = 0x7FFFFFFF;
            else if (x == -32768) o = -0x00010000;
            else {
                int data = x;
                if (!dp) data = data < 0 ? -data : data;
                else if (data < 0) data = (data >= -32768) ? -data : ~data;
                int lz = System.Numerics.BitOperations.LeadingZeroCount((uint)data);
                int addr = ((data << lz) >> 22) & 0x1FF;
                int sh = lz ^ 31;
                if (useSqrt) { addr = (addr & 0x1FE) | 0x200 | (lz & 1); sh >>= 1; }
                o = (int)((0x40000000U | ((uint)DivideTable.ReadValue(addr) << 14)) >> sh) ^ (x >> 31);
            }
            s.m_DivOut = o;
            RecAccLo(accArr, Vector128.Load(T));
            regs[di] = (short)o;
            s.m_lastDoublePrecision = false;
        }
        private static readonly MethodInfo s_RecVRcpL = M(nameof(RecVRcpL));
        private static readonly FieldInfo s_LastDpField = typeof(RspInterpreter).GetField(nameof(m_lastDoublePrecision), BindingFlags.Instance | BindingFlags.NonPublic);
        internal static readonly bool s_RecVec3 = Environment.GetEnvironmentVariable("COR64_RSP_REC_VEC3") != "0";

        // ---- stage 3: vector loads/stores (aligned, no element wrap) and VADD/VSUB ----
        // Plain register: big-endian vector byte o is raw byte 15 - o. DMEM holds big-endian words as host words, so an
        // aligned big-endian N-byte value is one host read (halfwords at a ^ 2); stored into the register little-endian at
        // raw offset 16 - e - N it lands exactly where the byte loop puts it.
        private static readonly Vector128<byte> s_QvMask = Vector128.Create((byte)12, 13, 14, 15, 8, 9, 10, 11, 4, 5, 6, 7, 0, 1, 2, 3);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool RecVLoad(short* regs, byte* M, uint rs, int vt, int e, int imm, int n)
        {
            long a = (long)rs + imm * n;
            if ((a & (n - 1)) != 0) return false;
            a &= 0xFFF;
            byte* V = (byte*)(regs + vt * 8);
            switch (n) {
                case 1: V[15 - e] = M[a ^ 3]; break;
                case 2: *(ushort*)(V + 14 - e) = *(ushort*)(M + (a ^ 2)); break;
                case 4: *(uint*)(V + 12 - e) = *(uint*)(M + a); break;
                case 8: *(ulong*)(V + 8 - e) = ((ulong)*(uint*)(M + a) << 32) | *(uint*)(M + a + 4); break;
                default: Ssse3.Shuffle(Vector128.Load(M + a), s_QvMask).Store(V); break;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool RecVStore(short* regs, byte* M, uint rs, int vt, int e, int imm, int n)
        {
            long a = (long)rs + imm * n;
            if ((a & (n - 1)) != 0) return false;
            a &= 0xFFF;
            byte* V = (byte*)(regs + vt * 8);
            switch (n) {
                case 1: M[a ^ 3] = V[15 - e]; break;
                case 2: *(ushort*)(M + (a ^ 2)) = *(ushort*)(V + 14 - e); break;
                case 4: *(uint*)(M + a) = *(uint*)(V + 12 - e); break;
                case 8: { ulong v = *(ulong*)(V + 8 - e); *(uint*)(M + a) = (uint)(v >> 32); *(uint*)(M + a + 4) = (uint)v; break; }
                default: Ssse3.Shuffle(Vector128.Load(V), s_QvMask).Store(M + a); break;
            }
            return true;
        }

        // Non-carry VADD/VSUB: VectorAddSubSimd with constant operands (vd saturated, ACC low = wrapped sum, VCO cleared).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVAddSub(short* regs, ulong[] accArr, RspCarryFlag vco, int vd, int vs, int vt, int e, bool add)
        {
            var sv = Avx2.ConvertToVector256Int32(Vector128.Load(regs + vs * 8));
            var tv = Avx2.ConvertToVector256Int32(RecLoadVt(regs, vt, e));
            var cv = s_CarryLanes[(byte)vco.RegValue];
            var r = add ? sv + tv + cv : sv - tv - cv;
            Sse2.PackSignedSaturate(r.GetLower(), r.GetUpper()).Store(regs + vd * 8);
            var lo = Vector128.Narrow(r.GetLower(), r.GetUpper()).AsUInt16();
            var lo03 = Avx2.ConvertToVector256Int64(lo).AsUInt64();                                         // lanes 0..3 = acc 7..4
            var lo47 = Avx2.ConvertToVector256Int64(Sse2.ShiftRightLogical128BitLane(lo, 8)).AsUInt64();     // lanes 4..7 = acc 3..0
            ref ulong a0 = ref MemoryMarshal.GetArrayDataReference(accArr);
            ref var a03 = ref Unsafe.As<ulong, Vector256<ulong>>(ref a0);
            ref var a47 = ref Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.Add(ref a0, 4));
            var keep = Vector256.Create(0xFFFFFFFF0000UL);
            a47 = (a47 & keep) | Avx2.Permute4x64(lo03, 0x1B);
            a03 = (a03 & keep) | Avx2.Permute4x64(lo47, 0x1B);
            if (add) vco.ClearCarryBits(); else vco.Clear();
        }


        // ---- climb 2: compares, logic, VMRG, VSAR, VMOV (raw lane k holds element 7 - k; flag bit i is element i) ----
        private static readonly Vector128<short>[] s_ElemMask = BuildElemMask();
        private static Vector128<short>[] BuildElemMask()
        {
            var t = new Vector128<short>[256];
            for (int b = 0; b < 256; b++) {
                var l = new short[8];
                for (int k = 0; k < 8; k++) l[k] = (short)-((b >> (7 - k)) & 1);
                t[b] = Vector128.Create(l);
            }
            return t;
        }
        private static readonly byte[] s_Rev8 = BuildRev8();
        private static byte[] BuildRev8() { var r = new byte[256]; for (int b = 0; b < 256; b++) { int v = 0; for (int i = 0; i < 8; i++) if ((b & (1 << i)) != 0) v |= 1 << (7 - i); r[b] = (byte)v; } return r; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ElemBits(Vector128<short> m) => s_Rev8[(byte)Sse2.MoveMask(Sse2.PackSignedSaturate(m, m).AsByte())];

        // ACC low 16 bits of every element = raw lanes of lo (upper 32 bits kept), as RecVAddSub does
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RecAccLo(ulong[] accArr, Vector128<short> lo16)
        {
            var lo = lo16.AsUInt16();
            var lo03 = Avx2.ConvertToVector256Int64(lo).AsUInt64();
            var lo47 = Avx2.ConvertToVector256Int64(Sse2.ShiftRightLogical128BitLane(lo, 8)).AsUInt64();
            ref ulong a0 = ref MemoryMarshal.GetArrayDataReference(accArr);
            ref var a03 = ref Unsafe.As<ulong, Vector256<ulong>>(ref a0);
            ref var a47 = ref Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.Add(ref a0, 4));
            var keep = Vector256.Create(0xFFFFFFFF0000UL);
            a47 = (a47 & keep) | Avx2.Permute4x64(lo03, 0x1B);
            a03 = (a03 & keep) | Avx2.Permute4x64(lo47, 0x1B);
        }

        // VEQ (0), VNE (1), VGE (2), VLT (3): VectorCompare with constant operands
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVCompare(short* regs, ulong[] accArr, RspCarryFlag vco, RspCompareFlag vcc, RspCompareFlag clip,
            int vd, int vs, int vt, int e, int kind)
        {
            var sv = Vector128.Load(regs + vs * 8);
            var tv = RecLoadVt(regs, vt, e);
            ushort co = vco.RegValue;
            var ne = s_ElemMask[co >> 8];
            var c = s_ElemMask[(byte)co];
            var eq = Vector128.Equals(sv, tv);
            Vector128<short> cmp, lo;
            switch (kind) {
                case 0: cmp = eq & ~ne; lo = tv; break;
                case 1: cmp = ~eq | ne; lo = sv; break;
                case 2: cmp = Vector128.GreaterThan(sv, tv) | (eq & ~(ne & c)); lo = Vector128.ConditionalSelect(cmp, sv, tv); break;
                default: cmp = Vector128.LessThan(sv, tv) | (eq & ne & c); lo = Vector128.ConditionalSelect(cmp, sv, tv); break;
            }
            RecAccLo(accArr, lo);
            lo.Store(regs + vd * 8);
            vcc.Value = ElemBits(cmp);
            vco.RegValue = 0;
            clip.Value = 0;
        }

        // VAND/VOR/VXOR (0/1/2, invert = N forms), VABS (3)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVBitwise(short* regs, ulong[] accArr, int vd, int vs, int vt, int e, int kind, bool invert)
        {
            var sv = Vector128.Load(regs + vs * 8);
            var tv = RecLoadVt(regs, vt, e);
            Vector128<short> r = kind switch { 0 => sv & tv, 1 => sv | tv, 2 => sv ^ tv, _ => Ssse3.Sign(tv, sv) };
            if (invert) r = ~r;
            RecAccLo(accArr, r);
            r.Store(regs + vd * 8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVMrg(short* regs, ulong[] accArr, RspCarryFlag vco, RspCompareFlag vcc, int vd, int vs, int vt, int e)
        {
            var sv = Vector128.Load(regs + vs * 8);
            var tv = RecLoadVt(regs, vt, e);
            var r = Vector128.ConditionalSelect(s_ElemMask[vcc.Value], sv, tv);
            RecAccLo(accArr, r);
            r.Store(regs + vd * 8);
            vco.RegValue = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVSar(short* regs, ulong[] accArr, int vd, int e)
        {
            short* d = regs + vd * 8;
            if (e > 2) { Vector128<short>.Zero.Store(d); return; }
            int sh = 32 - 16 * e;
            for (int i = 0; i < 8; i++) d[7 - i] = (short)(accArr[i] >> sh);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVMov(short* regs, ulong[] accArr, int vd, int sel, int vt, int e)
        {
            var tv = RecLoadVt(regs, vt, e);
            RecAccLo(accArr, tv);
            regs[vd * 8 + 7 - sel] = tv.GetElement(7 - sel);
        }

        private static MethodInfo M(string n) => typeof(RspInterpreter).GetMethod(n, BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecVCompare = M(nameof(RecVCompare)), s_RecVBitwise = M(nameof(RecVBitwise)),
            s_RecVMrg = M(nameof(RecVMrg)), s_RecVSar = M(nameof(RecVSar)), s_RecVMov = M(nameof(RecVMov));
        private static readonly FieldInfo s_VccCmpField = typeof(RspInterpreter).GetField("M_VccCompare", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_VccClipField = typeof(RspInterpreter).GetField("m_VccClip", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly bool s_RecVec2 = Environment.GetEnvironmentVariable("COR64_RSP_REC_VEC2") != "0";


        // VADDC (add) / VSUBC: unsigned sum/difference, vd and ACC low = low 16 bits, VCO rebuilt (carry; NE only for VSUBC)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVAddSubC(short* regs, ulong[] accArr, RspCarryFlag vco, int vd, int vs, int vt, int e, bool add)
        {
            var sv = Vector128.Load(regs + vs * 8);
            var tv = RecLoadVt(regs, vt, e);
            var bias = Vector128.Create(unchecked((short)0x8000));
            Vector128<short> r, carry;
            if (add) {
                r = sv + tv;
                carry = Vector128.LessThan(r ^ bias, sv ^ bias);           // unsigned overflow
                vco.RegValue = ElemBits(carry);
            }
            else {
                r = sv - tv;
                carry = Vector128.LessThan(sv ^ bias, tv ^ bias);          // borrow (bit 16 of the 32-bit difference)
                var ne = ~Vector128.Equals(sv, tv);
                vco.RegValue = (ushort)((ElemBits(ne) << 8) | ElemBits(carry));
            }
            RecAccLo(accArr, r);
            r.Store(regs + vd * 8);
        }

        // VCL (0), VCH (1), VCR (2): the interpreter's per-element code with constant operands
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVClip(short* regs, ulong[] accArr, RspCarryFlag vco, RspCompareFlag vccF, RspCompareFlag clipF, RspCompareFlag vceF,
            int vd, int vs, int vt, int e, int mode)
        {
            var tvv = RecLoadVt(regs, vt, e);
            short* tr = stackalloc short[8];
            tvv.Store(tr);
            var svv = Vector128.Load(regs + vs * 8);
            short* sr = stackalloc short[8];
            svv.Store(sr);
            int co = vco.RegValue, vcc = vccF.Value, clip = clipF.Value, vce = vceF.Value;
            int nco = 0, nvcc = 0, nclip = 0, nvce = 0;
            short* res = stackalloc short[8];
            for (int i = 0; i < 8; i++) {
                short s16 = sr[7 - i], t16 = tr[7 - i];
                ushort su = (ushort)s16, tu = (ushort)t16;
                bool carry = ((co >> i) & 1) != 0;
                bool fc, fl, compare; short pass, fail;
                if (mode == 0) {
                    bool ne = ((co >> (8 + i)) & 1) != 0;
                    fc = ((vcc >> i) & 1) != 0;
                    fl = ((clip >> i) & 1) != 0;
                    fail = s16;
                    if (carry) {
                        pass = (short)(-t16);
                        if (!ne) {
                            int full = su + tu;
                            bool sumZero = (full & 0xFFFF) == 0;
                            bool c = full > 0xFFFF;
                            fc = ((vce >> i) & 1) != 0 ? (sumZero || !c) : (sumZero && !c);
                        }
                        compare = fc;
                    }
                    else {
                        pass = t16;
                        if (!ne) fl = su >= tu;
                        compare = fl;
                    }
                }
                else if (mode == 1) {
                    bool isSigned = (short)(su ^ tu) < 0;
                    bool ne;
                    if (isSigned) {
                        short r = (short)(s16 + t16);
                        fc = r <= 0; fl = t16 < 0;
                        nco |= 1 << i;
                        ne = r != 0 && su != (ushort)(tu ^ 0xFFFF);
                        if (r == -1) nvce |= 1 << i;
                        pass = (short)(-t16); fail = s16; compare = fc;
                    }
                    else {
                        short r = (short)(s16 - t16);
                        fc = t16 < 0; fl = r >= 0;
                        ne = r != 0 && su != (ushort)(tu ^ 0xFFFF);
                        pass = t16; fail = s16; compare = fl;
                    }
                    if (ne) nco |= 1 << (8 + i);
                }
                else {
                    bool isSigned = (short)(su ^ tu) < 0;
                    fc = isSigned ? t16 <= ~s16 : t16 <= ~0;
                    fl = isSigned ? ~0 >= t16 : s16 >= t16;
                    ushort slice = isSigned ? (ushort)(tu ^ 0xFFFF) : tu;
                    pass = (short)slice; fail = s16;
                    compare = isSigned ? fc : fl;
                }
                res[7 - i] = compare ? pass : fail;
                if (fc) nvcc |= 1 << i;
                if (fl) nclip |= 1 << i;
            }
            var rv = Vector128.Load(res);
            RecAccLo(accArr, rv);
            rv.Store(regs + vd * 8);
            vco.RegValue = (ushort)nco;
            vceF.Value = (byte)nvce;
            vccF.Value = (byte)nvcc;
            clipF.Value = (byte)nclip;
        }
        private static readonly MethodInfo s_RecVAddSubC = M(nameof(RecVAddSubC)), s_RecVClip = M(nameof(RecVClip));
        // [climb4 rsp_gen3_flags] VCL/VCH/VCR as straight SIMD (lane masks), bit-identical to RecVClip above.
        internal static readonly bool s_RecClipSimd = Environment.GetEnvironmentVariable("COR64_RSPG3F_CLIPSIMD") != "0";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecVClipSimd(short* regs, ulong[] accArr, RspCarryFlag vco, RspCompareFlag vccF, RspCompareFlag clipF, RspCompareFlag vceF,
            int vd, int vs, int vt, int e, int mode)
        {
            var s = Vector128.Load(regs + vs * 8);
            var t = RecLoadVt(regs, vt, e);
            var bias = Vector128.Create(unchecked((short)0x8000));
            var zero = Vector128<short>.Zero;
            var ones = Vector128<short>.AllBitsSet;
            var negT = zero - t;
            Vector128<short> fc, fl, compare, pass;
            if (mode == 0) {
                ushort co = vco.RegValue;
                var carry = s_ElemMask[(byte)co];
                var ne = s_ElemMask[co >> 8];
                var vccIn = s_ElemMask[vccF.Value];
                var clipIn = s_ElemMask[clipF.Value];
                var vceIn = s_ElemMask[vceF.Value];
                var r = s + t;
                var sumZero = Vector128.Equals(r, zero);
                var c = Vector128.LessThan(r ^ bias, s ^ bias);
                var fcNew = Vector128.ConditionalSelect(vceIn, sumZero | ~c, sumZero & ~c);
                var geU = ~Vector128.LessThan(s ^ bias, t ^ bias);
                fc = Vector128.ConditionalSelect(carry & ~ne, fcNew, vccIn);
                fl = Vector128.ConditionalSelect(~carry & ~ne, geU, clipIn);
                compare = Vector128.ConditionalSelect(carry, fc, fl);
                pass = Vector128.ConditionalSelect(carry, negT, t);
                vco.RegValue = 0;
                vceF.Value = 0;
            }
            else if (mode == 1) {
                var sign = Vector128.LessThan(s ^ t, zero);
                var r = Vector128.ConditionalSelect(sign, s + t, s - t);
                var tNeg = Vector128.LessThan(t, zero);
                fc = Vector128.ConditionalSelect(sign, ~Vector128.GreaterThan(r, zero), tNeg);
                fl = Vector128.ConditionalSelect(sign, tNeg, ~Vector128.LessThan(r, zero));
                var ne = ~Vector128.Equals(r, zero) & ~Vector128.Equals(s, ~t);
                var vce = sign & Vector128.Equals(r, ones);
                compare = Vector128.ConditionalSelect(sign, fc, fl);
                pass = Vector128.ConditionalSelect(sign, negT, t);
                vco.RegValue = (ushort)((ElemBits(ne) << 8) | ElemBits(sign));
                vceF.Value = ElemBits(vce);
            }
            else {
                var sign = Vector128.LessThan(s ^ t, zero);
                var tNeg = Vector128.LessThan(t, zero);
                fc = Vector128.ConditionalSelect(sign, ~Vector128.GreaterThan(t, ~s), tNeg);
                fl = Vector128.ConditionalSelect(sign, tNeg, ~Vector128.LessThan(s, t));
                compare = Vector128.ConditionalSelect(sign, fc, fl);
                pass = Vector128.ConditionalSelect(sign, ~t, t);
                vco.RegValue = 0;
                vceF.Value = 0;
            }
            var rv = Vector128.ConditionalSelect(compare, pass, s);
            RecAccLo(accArr, rv);
            rv.Store(regs + vd * 8);
            vccF.Value = ElemBits(fc);
            clipF.Value = ElemBits(fl);
        }
        private static readonly MethodInfo s_RecVClipSimd = M(nameof(RecVClipSimd));

        private static readonly FieldInfo s_VceField = typeof(RspInterpreter).GetField("m_Vce", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo s_RecVLoad = typeof(RspInterpreter).GetMethod(nameof(RecVLoad), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecVStore = typeof(RspInterpreter).GetMethod(nameof(RecVStore), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecVAddSub = typeof(RspInterpreter).GetMethod(nameof(RecVAddSub), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo s_VCarryField = typeof(RspInterpreter).GetField("m_VCarry", BindingFlags.Instance | BindingFlags.NonPublic);

        // Emits a vector op inline; false when this stage has no helper for it (the caller calls the handler).
        // ldGpr pushes a GPR value; callHandler emits the handler call (used as the run-time fallback of loads/stores).
        private unsafe bool RecEmitVector(ILGenerator il, in DecodedInstruction d, Action<DecodedInstruction> h, short* regs,
            Action<int> ldGpr, Action callHandler)
        {
            if (regs == null) return false;
            string name = h.Method.Name;
            if (name == nameof(VectorLoad) || name == nameof(VectorStore)) {
                bool load = name == nameof(VectorLoad);
                int n;
                switch (d.Op.Op) {
                    case "lbv": case "sbv": n = 1; break;
                    case "lsv": case "ssv": n = 2; break;
                    case "llv": case "slv": n = 4; break;
                    case "ldv": case "sdv": n = 8; break;
                    case "lqv": case "sqv": n = 16; break;
                    case "lpv": case "luv":
                        if (!s_RecVec3) return false;
                        il.Emit(OpCodes.Ldc_I8, (long)regs); il.Emit(OpCodes.Conv_U);
                        il.Emit(OpCodes.Ldc_I8, (long)DmemBytes); il.Emit(OpCodes.Conv_U);
                        ldGpr(d.Source);
                        { uint rw = d.Inst.inst; il.Emit(OpCodes.Ldc_I4, d.Target); il.Emit(OpCodes.Ldc_I4, (int)((rw >> 7) & 0xF)); il.Emit(OpCodes.Ldc_I4, ((int)(rw << 25)) >> 25); }
                        il.Emit(OpCodes.Ldc_I4, d.Op.Op == "lpv" ? 8 : 7);
                        il.Emit(OpCodes.Call, s_RecVPack);
                        return true;
                    default: return false;
                }
                uint raw = d.Inst.inst;
                int imm = ((int)(raw << 25)) >> 25;
                int e = (int)((raw >> 7) & 0xF);
                if (n == 16 ? e != 0 : e + n > 16) return false;
                var done = il.DefineLabel();
                il.Emit(OpCodes.Ldc_I8, (long)regs); il.Emit(OpCodes.Conv_U);
                il.Emit(OpCodes.Ldc_I8, (long)DmemBytes); il.Emit(OpCodes.Conv_U);
                ldGpr(d.Source);
                il.Emit(OpCodes.Ldc_I4, d.Target); il.Emit(OpCodes.Ldc_I4, e); il.Emit(OpCodes.Ldc_I4, imm); il.Emit(OpCodes.Ldc_I4, n);
                il.Emit(OpCodes.Call, load ? s_RecVLoad : s_RecVStore);
                il.Emit(OpCodes.Brtrue, done);
                callHandler();
                il.MarkLabel(done);
                return true;
            }
            if ((name == nameof(VectorAdd) || name == nameof(VectorSubtract)) && (d.Op.Flags & ExecutionFlags.Carry) != ExecutionFlags.Carry) {
                il.Emit(OpCodes.Ldc_I8, (long)regs); il.Emit(OpCodes.Conv_U);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_GetAcc); il.Emit(OpCodes.Call, s_GetAccRaw);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldfld, s_VCarryField);
                il.Emit(OpCodes.Ldc_I4, d.VDest); il.Emit(OpCodes.Ldc_I4, d.VSource); il.Emit(OpCodes.Ldc_I4, d.VTarget); il.Emit(OpCodes.Ldc_I4, d.Element);
                il.Emit(name == nameof(VectorAdd) ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Call, s_RecVAddSub);
                return true;
            }
            if (s_RecVec2) {
                void Regs() { il.Emit(OpCodes.Ldc_I8, (long)regs); il.Emit(OpCodes.Conv_U); }
                void AccArr() { CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_GetAcc); il.Emit(OpCodes.Call, s_GetAccRaw); }
                void Fld(FieldInfo f) { CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldfld, f); }
                void I(int v) => il.Emit(OpCodes.Ldc_I4, v);
                string op = d.Op.Op;
                int ck = op switch { "veq" => 0, "vne" => 1, "vge" => 2, "vlt" => 3, _ => -1 };
                if (ck >= 0 && name == nameof(VectorCompare)) {
                    Regs(); AccArr(); Fld(s_VCarryField); Fld(s_VccCmpField); Fld(s_VccClipField);
                    I(d.VDest); I(d.VSource); I(d.VTarget); I(d.Element); I(ck);
                    il.Emit(OpCodes.Call, s_RecVCompare); return true;
                }
                int bk = op switch { "vand" or "vnand" => 0, "vor" or "vnor" => 1, "vxor" or "vnxor" => 2, "vabs" => 3, _ => -1 };
                if (bk >= 0 && name == nameof(VectorBitwise)) {
                    Regs(); AccArr(); I(d.VDest); I(d.VSource); I(d.VTarget); I(d.Element); I(bk);
                    I(op == "vnand" || op == "vnor" || op == "vnxor" ? 1 : 0);
                    il.Emit(OpCodes.Call, s_RecVBitwise); return true;
                }
                if ((op == "vaddc" && name == nameof(VectorAdd)) || (op == "vsubc" && name == nameof(VectorSubtract))) {
                    Regs(); AccArr(); Fld(s_VCarryField); I(d.VDest); I(d.VSource); I(d.VTarget); I(d.Element); I(op == "vaddc" ? 1 : 0);
                    il.Emit(OpCodes.Call, s_RecVAddSubC); return true;
                }
                int clk = op switch { "vcl" => 0, "vch" => 1, "vcr" => 2, _ => -1 };
                if (clk >= 0 && name == nameof(VectorClip)) {
                    Regs(); AccArr(); Fld(s_VCarryField); Fld(s_VccCmpField); Fld(s_VccClipField); Fld(s_VceField);
                    I(d.VDest); I(d.VSource); I(d.VTarget); I(d.Element); I(clk);
                    il.Emit(OpCodes.Call, s_RecClipSimd ? s_RecVClipSimd : s_RecVClip); return true;
                }
                if (op == "vmrg" && name == nameof(VectorClip)) {
                    Regs(); AccArr(); Fld(s_VCarryField); Fld(s_VccCmpField); I(d.VDest); I(d.VSource); I(d.VTarget); I(d.Element);
                    il.Emit(OpCodes.Call, s_RecVMrg); return true;
                }
                if (op == "vsar" && name == nameof(VectorAccumulatorReadWrite)) {
                    Regs(); AccArr(); I(d.VDest); I(d.Element ^ 8);
                    il.Emit(OpCodes.Call, s_RecVSar); return true;
                }
                if (op == "vmov" && name == nameof(VectorMove)) {
                    Regs(); AccArr(); I(d.VDest); I(d.VSource & 7); I(d.VTarget); I(d.Element);
                    il.Emit(OpCodes.Call, s_RecVMov); return true;
                }
            }
            if (!s_RecVec3 && name == nameof(VectorReciprocal)) t_RcpDP = -1;
            if (s_RecVec3 && name == nameof(VectorReciprocal)) {
                var f = d.Op.VectorFlags;
                bool sq = (f & VectorOpFlags.SquareRoot) == VectorOpFlags.SquareRoot;
                bool hi = (f & VectorOpFlags.PartialH) == VectorOpFlags.PartialH;
                bool lo = (f & VectorOpFlags.PartialL) == VectorOpFlags.PartialL;
                int known = t_RcpDP;
                t_RcpDP = hi ? 1 : 0;
                if (s_RcpSpec) {
                    int vt = d.VTarget & 31, ti = 7 - (d.Element & 7), di = d.VDest * 8 + 7 - (d.DestElement & 7);
                    void Args()
                    {
                        CallStubArg.Ld(il, 0);
                        il.Emit(OpCodes.Ldc_I8, (long)regs); il.Emit(OpCodes.Conv_U);
                        CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_GetAcc); il.Emit(OpCodes.Call, s_GetAccRaw);
                        il.Emit(OpCodes.Ldc_I4, vt); il.Emit(OpCodes.Ldc_I4, ti); il.Emit(OpCodes.Ldc_I4, di);
                    }
                    void Low(bool dp)
                    {
                        Args();
                        il.Emit(sq ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                        il.Emit(dp ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                        il.Emit(lo && dp ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Call, s_RecVRcpL);
                    }
                    if (hi) { Args(); il.Emit(OpCodes.Call, s_RecVRcpH); return true; }
                    if (known >= 0) { Low(known == 1); return true; }
                    // unknown at block entry: branch once on the field
                    var isDp = il.DefineLabel(); var end = il.DefineLabel();
                    CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldfld, s_LastDpField); il.Emit(OpCodes.Brtrue, isDp);
                    Low(false); il.Emit(OpCodes.Br, end);
                    il.MarkLabel(isDp); Low(true);
                    il.MarkLabel(end);
                    return true;
                }
                CallStubArg.Ld(il, 0);
                il.Emit(OpCodes.Ldc_I8, (long)regs); il.Emit(OpCodes.Conv_U);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_GetAcc); il.Emit(OpCodes.Call, s_GetAccRaw);
                il.Emit(OpCodes.Ldc_I4, d.VTarget & 31); il.Emit(OpCodes.Ldc_I4, d.VDest); il.Emit(OpCodes.Ldc_I4, d.Element & 7); il.Emit(OpCodes.Ldc_I4, d.DestElement & 7);
                void B3(bool v) => il.Emit(v ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                B3((f & VectorOpFlags.SquareRoot) == VectorOpFlags.SquareRoot);
                B3((f & VectorOpFlags.PartialH) == VectorOpFlags.PartialH);
                B3((f & VectorOpFlags.PartialL) == VectorOpFlags.PartialL);
                il.Emit(OpCodes.Call, s_RecVRcp);
                return true;
            }
            if (h.Method.Name == nameof(VectorMultiply) && s_Sm300Acc && (m_SmCached || m_SmNextMul)) {
                // [climb4 sm64_300] COR64_SM300_ACC: a run of consecutive multiplies keeps the accumulator in two locals
                // (lane order of RecVMul's pLo/pHi), loaded once at the run's start and stored once at its end.
                var f = d.Op.VectorFlags;
                if (!m_SmCached) {
                    m_SmAccLo ??= il.DeclareLocal(typeof(Vector256<long>)); m_SmAccHi ??= il.DeclareLocal(typeof(Vector256<long>));
                    CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_GetAcc); il.Emit(OpCodes.Call, s_GetAccRaw);
                    il.Emit(OpCodes.Ldloca, m_SmAccLo); il.Emit(OpCodes.Ldloca, m_SmAccHi); il.Emit(OpCodes.Call, s_RecAccLoad);
                    m_SmCached = true;
                }
                il.Emit(OpCodes.Ldc_I8, (long)regs); il.Emit(OpCodes.Conv_U);
                il.Emit(OpCodes.Ldloca, m_SmAccLo); il.Emit(OpCodes.Ldloca, m_SmAccHi);
                il.Emit(OpCodes.Ldc_I4, d.VDest); il.Emit(OpCodes.Ldc_I4, d.VSource); il.Emit(OpCodes.Ldc_I4, d.VTarget); il.Emit(OpCodes.Ldc_I4, d.Element);
                void Bc(bool v) => il.Emit(v ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                Bc(d.IsUnsigned());
                Bc((f & VectorOpFlags.Accumulate) == VectorOpFlags.Accumulate);
                Bc((f & VectorOpFlags.PartialL) == VectorOpFlags.PartialL);
                Bc((f & VectorOpFlags.PartialM) == VectorOpFlags.PartialM);
                Bc((f & VectorOpFlags.PartialN) == VectorOpFlags.PartialN);
                Bc((f & VectorOpFlags.PartialH) == VectorOpFlags.PartialH);
                il.Emit(OpCodes.Call, s_RecVMulC);
                if (!m_SmNextMul) {
                    CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_GetAcc); il.Emit(OpCodes.Call, s_GetAccRaw);
                    il.Emit(OpCodes.Ldloc, m_SmAccLo); il.Emit(OpCodes.Ldloc, m_SmAccHi); il.Emit(OpCodes.Call, s_RecAccStore);
                    m_SmCached = false;
                }
                return true;
            }
            if (h.Method.Name == nameof(VectorMultiply)) {
                var f = d.Op.VectorFlags;
                il.Emit(OpCodes.Ldc_I8, (long)regs); il.Emit(OpCodes.Conv_U);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_GetAcc); il.Emit(OpCodes.Call, s_GetAccRaw);
                il.Emit(OpCodes.Ldc_I4, d.VDest); il.Emit(OpCodes.Ldc_I4, d.VSource); il.Emit(OpCodes.Ldc_I4, d.VTarget); il.Emit(OpCodes.Ldc_I4, d.Element);
                void B(bool v) => il.Emit(v ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                B(d.IsUnsigned());
                B((f & VectorOpFlags.Accumulate) == VectorOpFlags.Accumulate);
                B((f & VectorOpFlags.PartialL) == VectorOpFlags.PartialL);
                B((f & VectorOpFlags.PartialM) == VectorOpFlags.PartialM);
                B((f & VectorOpFlags.PartialN) == VectorOpFlags.PartialN);
                B((f & VectorOpFlags.PartialH) == VectorOpFlags.PartialH);
                il.Emit(OpCodes.Call, s_RecVMul);
                return true;
            }
            return false;
        }
    }
}
