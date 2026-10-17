namespace BrokenNes2.Instruments;

/// <summary>
/// xoshiro256** (Blackman and Vigna, public domain) seeded from one 64-bit number through splitmix64, the seeding they recommend. Integer arithmetic only, so a seed gives the same stream on any machine and any runtime.
/// The corruptor draws every random decision from one of these and stores the seed with the operation: seed + operation reproduce the corruption exactly.
/// </summary>
public struct Prng
{
    private ulong s0, s1, s2, s3;

    public Prng(ulong seed)
    {
        ulong x = seed;
        s0 = SplitMix(ref x); s1 = SplitMix(ref x); s2 = SplitMix(ref x); s3 = SplitMix(ref x);
    }

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextU64()
    {
        ulong result = Rotl(s1 * 5, 7) * 9;
        ulong t = s1 << 17;
        s2 ^= s0; s3 ^= s1; s1 ^= s2; s0 ^= s3;
        s2 ^= t;
        s3 = Rotl(s3, 45);
        return result;
    }

    /// <summary>A number in [0, n), without modulo bias (Lemire's multiply-shift with rejection). n of 0 gives 0.</summary>
    public uint Below(uint n)
    {
        if (n <= 1) return 0;
        ulong x = (uint)(NextU64() >> 32);
        ulong m = x * n;
        uint low = (uint)m;
        if (low < n)
        {
            uint threshold = unchecked(0u - n) % n;
            while (low < threshold)
            {
                x = (uint)(NextU64() >> 32);
                m = x * n;
                low = (uint)m;
            }
        }
        return (uint)(m >> 32);
    }

    /// <summary>A number in [min, max] (inclusive).</summary>
    public int Between(int min, int max) => max <= min ? min : min + (int)Below((uint)(max - min + 1));

    /// <summary>True with probability permille / 1000.</summary>
    public bool Chance(int permille) => permille >= 1000 || (permille > 0 && Below(1000) < (uint)permille);

    /// <summary>Derives a seed for a sub-stream (so a part of an operation does not depend on how many numbers an earlier part drew).</summary>
    public static ulong Mix(ulong seed, ulong salt)
    {
        ulong x = seed ^ (salt * 0x9E3779B97F4A7C15UL);
        return SplitMix(ref x);
    }
}
