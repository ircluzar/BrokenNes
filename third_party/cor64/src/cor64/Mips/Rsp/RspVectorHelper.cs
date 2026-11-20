using System.Runtime.Intrinsics;
using System.Runtime.CompilerServices;

namespace cor64.Mips.Rsp
{
    public static class RspVectorHelper {

        // [cer64plus] Element broadcast as one SSSE3 byte shuffle on plain registers (default; COR64_RSP_SIMD=0 off).
        // Raw lane k holds element 7-k (little-endian 16-bit), so destination element i (lane 7-i) takes source element
        // f(i): quarter (e=2,3) (i&~1)|(e&1), half (e=4..7) (i&~3)|(e&3), whole (e=8..15) e&7.
        private static readonly bool s_FastResolve = System.Environment.GetEnvironmentVariable("COR64_RSP_SIMD") != "0"
            && System.Runtime.Intrinsics.X86.Ssse3.IsSupported;
        internal static readonly System.Runtime.Intrinsics.Vector128<byte>[] s_Masks = BuildMasks();

        private static System.Runtime.Intrinsics.Vector128<byte>[] BuildMasks()
        {
            var m = new System.Runtime.Intrinsics.Vector128<byte>[16];
            for (int e = 0; e < 16; e++) {
                var b = new byte[16];
                for (int i = 0; i < 8; i++) {
                    int j = e == 0 || e == 1 ? i : (e & 0xE) == 2 ? (i & ~1) | (e & 1) : (e & 0xC) == 4 ? (i & ~3) | (e & 3) : (e & 7);
                    int dk = 7 - i, sk = 7 - j;
                    b[dk * 2] = (byte)(sk * 2); b[dk * 2 + 1] = (byte)(sk * 2 + 1);
                }
                m[e] = System.Runtime.Intrinsics.Vector128.Create(b);
            }
            return m;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe RspVector Resolve(this RspVector vector, int e, RspVector scratch)
        {
            if (s_FastResolve && (e & 0xE) != 0 && vector.IsPlain) {
                var vec = scratch; // per RSP instance, so sessions on different threads never share it
                var src = System.Runtime.Intrinsics.Vector128.Load((byte*)vector.RawLanes);
                System.Runtime.Intrinsics.X86.Ssse3.Shuffle(src, s_Masks[e]).Store((byte*)vec.RawLanes);
                return vec;
            }
            if (e != 0)
            {
                if ((e & 0xE) == 0x2)
                {
                    return RspVector.ScalarQuarter(vector, e);
                }
                else if ((e & 0xC) == 0x4)
                {
                    return RspVector.ScalarHalf(vector, e);
                }
                else if ((e & 0x8) == 0x8)
                {
                    return RspVector.ScalarWhole(vector, e);
                }
                else
                {
                    return vector;
                }
            }
            else
            {
                return vector;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void StoreProductClampedSignedAM(this RspVector vector, Accumulator accumulator, int index) {
            // Thanks to cxd4

            int acc_h = accumulator.SignedHi(index);
            int acc_m = accumulator.SignedMi(index);

            bool hi = (acc_h >  0) || (acc_h == 0 && (acc_m <  0));
            bool lo = (acc_h < -1) || (acc_h <  0 && (acc_m >= 0));

            int result = acc_m;
            result &= -(lo.ToInt() ^ 1);
            result |= -(hi.ToInt() ^ 0);
            result ^= (hi || lo) ? 0x8000 : 0x0000;

            vector.PackedS16(index, (short)result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void StoreProductClampedSignedAL(this RspVector vector, Accumulator accumulator, int index) {
            // Thanks to cxd4

            StoreProductClampedSignedAM(vector, accumulator, index);

            short result = vector.PackedS16(index);
            short acc_m = accumulator.SignedMi(index);

            if (result != acc_m) {
                result = (short)(result ^ 0x8000);
            }
            else {
                result = accumulator.SignedLo(index);
            }

            vector.PackedS16(index, result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void StoreProductClampedUnsigned(this RspVector vector, Accumulator accumulator, int index) {
            // Thanks to cxd4

            StoreProductClampedSignedAM(vector, accumulator, index);

            int acc_m = accumulator.SignedMi(index);
            int val   = vector.PackedS16(index);
            int result = val & ~(val >> 15);

            if (val > acc_m) {
                result |= 0xFFFF;
            }

            vector.PackedU16(index, (ushort)result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToInt(this bool value) {
            return value ? 1 : 0;
        }
    }
}