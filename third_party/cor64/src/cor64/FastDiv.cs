using System;
using System.Runtime.CompilerServices;

namespace cor64
{
    // [climb4 rsp_speed1] COR64_RSPS1_DIV (default on if it wins): the per-span event horizons (VI field/line, AI sample,
    // CPU timer) divided with 64-bit idiv on every idle-span step (~1.5% of SM64 samples sat right after those idivs).
    // For a non-negative dividend and a positive divisor, a 32-bit unsigned divide or a shift by a power-of-two divisor
    // gives exactly the same quotient as the 64-bit truncating divide; anything else takes the original divide.
    public static class FastDiv
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("COR64_RSPS1_DIV") == "1";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Div(long a, long b)
        {
            if (On && a >= 0 && b > 0) {
                if ((b & (b - 1)) == 0) return a >> System.Numerics.BitOperations.TrailingZeroCount((ulong)b);
                if (((ulong)a >> 32) == 0 && ((ulong)b >> 32) == 0) return (uint)a / (uint)b;
            }
            return a / b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Div(ulong a, ulong b)
        {
            if (On && b != 0) {
                if ((b & (b - 1)) == 0) return a >> System.Numerics.BitOperations.TrailingZeroCount(b);
                if ((a >> 32) == 0 && (b >> 32) == 0) return (uint)a / (uint)b;
            }
            return a / b;
        }
    }
}
