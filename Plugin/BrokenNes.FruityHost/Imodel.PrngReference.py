"""Independent reference for BrokenNes2.Instruments.Prng: xoshiro256** seeded by splitmix64, and Lemire's bounded draw on the top 32 bits.
Written from the published algorithms (Blackman & Vigna), not from the C# code, so a match proves the C# implements the standard generators."""
M = (1 << 64) - 1

def splitmix(x):
    x = (x + 0x9E3779B97F4A7C15) & M
    z = x
    z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & M
    z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & M
    return x, z ^ (z >> 31)

def rotl(x, k):
    return ((x << k) | (x >> (64 - k))) & M

class X:
    def __init__(self, seed):
        x = seed & M
        s = []
        for _ in range(4):
            x, z = splitmix(x)
            s.append(z)
        self.s = s
    def next(self):
        s = self.s
        result = (rotl((s[1] * 5) & M, 7) * 9) & M
        t = (s[1] << 17) & M
        s[2] ^= s[0]; s[3] ^= s[1]; s[1] ^= s[2]; s[0] ^= s[3]
        s[2] ^= t
        s[3] = rotl(s[3], 45)
        return result
    def below(self, n):
        if n <= 1:
            return 0
        x = self.next() >> 32
        m = x * n
        low = m & 0xFFFFFFFF
        if low < n:
            threshold = ((1 << 32) - n) % n
            while low < threshold:
                x = self.next() >> 32
                m = x * n
                low = m & 0xFFFFFFFF
        return m >> 32

for seed in (0, 1, 12345, 0xDEADBEEFCAFEBABE):
    g = X(seed)
    print(f"seed {seed:#x}: " + ", ".join(f"0x{g.next():016X}UL" for _ in range(4)))
for seed, n in ((7, 13), (99, 1000), (2024, 3), (5, 1 << 31)):
    g = X(seed)
    print(f"seed {seed} below {n}: " + ", ".join(str(g.below(n)) for _ in range(8)))
