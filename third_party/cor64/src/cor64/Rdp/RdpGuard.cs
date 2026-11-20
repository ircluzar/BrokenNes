using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace cor64.Rdp
{
    // [rdp_readback] RDRAM pages the RDP's in-flight GPU work may still write (W) or read (R). parallel-rdp runs a submitted
    // batch on the GPU while emulation goes on; any emulated access (CPU, including the recompilers' inline RDRAM paths,
    // RSP/PI/SI DMA, AI) that touches such a page first waits for that work. Reads conflict with W pages; writes with
    // W or R pages (a texture the batch has yet to load, a color buffer it will blend into). The outcome then no longer
    // depends on host timing, whatever the game does, so no per-game sync tables are needed.
    // The memory controller's per-access check reads the tables inline (one or two bytes) and calls out only on a hit.
    // Two native byte tables indexed by phys >> PageShift (8 MB of RDRAM): TableW (nonzero = W) for loads and TableA
    // (nonzero = W or R) for stores, so the inline fast paths test one byte. All zero when nothing is in flight.
    // COR64_RDP_READBACK_GUARD=0 turns it off (then the old per-game tables apply).
    public static unsafe class RdpGuard
    {
        public const int PageShift = 12;
        public const int Pages = 0x800000 >> PageShift;
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("COR64_RDP_READBACK_GUARD") != "0";
        public static readonly byte* TableW = (byte*)NativeMemory.AllocZeroed((nuint)Pages + 64);
        public static readonly byte* TableA = (byte*)NativeMemory.AllocZeroed((nuint)Pages + 64);
        public static readonly byte* TableR = (byte*)NativeMemory.AllocZeroed((nuint)Pages + 64); // R pages (slow path only)
        // Exact byte ranges behind TableW (kept by ParallelRdp; WAll = unknown). A page hit that misses them all is a
        // neighbour sharing a 4 KB page with an in-flight buffer and does not wait. COR64_RDP_READBACK_EXACT=0: pages only.
        public const int MaxRanges = 8;
        public static readonly uint[] WLo = new uint[MaxRanges], WHi = new uint[MaxRanges];
        public static int WN;
        public static bool WAll;
        private static readonly bool s_Exact = Environment.GetEnvironmentVariable("COR64_RDP_READBACK_EXACT") != "0";
        // [rdp_sync1] exact byte ranges behind TableR (see ParallelRdp.AddRRange). COR64_RDPSYNC1_REXACT=0: R pages only.
        public const int MaxRRanges = 64;
        public static readonly bool RExact = Environment.GetEnvironmentVariable("COR64_RDPSYNC1_REXACT") != "0";
        public static readonly uint[] RLo = new uint[MaxRRanges], RHi = new uint[MaxRRanges];
        public static int RN;
        public static bool Armed;            // any table entry set
        public static Action Waiter;          // finishes all in-flight RDP work and clears the tables
        public static long Hits, DmaHits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(uint phys, uint len) { if (Enabled && phys < 0x800000 && (TableW[phys >> PageShift] | TableW[(phys + len - 1) >> PageShift]) != 0) Check(phys, len, false); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(uint phys, uint len) { if (Enabled && phys < 0x800000 && (TableA[phys >> PageShift] | TableA[(phys + len - 1) >> PageShift]) != 0) Check(phys, len, true); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadDma(uint phys, uint len) { if (Enabled && Armed) CheckDma(phys, len, false); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDma(uint phys, uint len) { if (Enabled && Armed) CheckDma(phys, len, true); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CheckDma(uint phys, uint len, bool write) { if (Overlaps(phys, len, write)) { DmaHits++; if (s_Log) Log(phys, len, write, true); HitAt(phys, len, write); } }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Check(uint phys, uint len, bool write) { if (Overlaps(phys, len, write)) { if (s_Log) Log(phys, len, write, false); HitAt(phys, len, write); } }
        // [rdp_sync1] diagnostics only: COR64_RDPSYNC1_LOG=1 prints each guard hit (address, kind, the in-flight ranges).
        private static readonly bool s_Log = Environment.GetEnvironmentVariable("COR64_RDPSYNC1_LOG") == "1";
        private static void Log(uint phys, uint len, bool write, bool dma)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < WN; i++) sb.Append($" {WLo[i]:X6}-{WHi[i]:X6}");
            sb.Append($" RN={RN}");
            for (int i = 0; i < RN; i++) if (phys < RHi[i] && RLo[i] < phys + len) sb.Append($" r{RLo[i]:X6}-{RHi[i]:X6}");
            Console.Error.WriteLine($"GHIT {(dma ? "dma" : "cpu")} {(write ? "W" : "R")} {phys:X6} len={len} R={TableR[phys >> PageShift]} W={TableW[phys >> PageShift]} all={WAll}{sb}");
        }

        private static bool Overlaps(uint phys, uint len, bool write)
        {
            if (len == 0 || phys >= 0x800000) return false;
            ulong end = Math.Min((ulong)phys + len, 0x800000UL);
            byte* t = write ? TableA : TableW;
            bool wHit = false, rHit = false;
            for (uint p = phys >> PageShift, last = (uint)((end - 1) >> PageShift); p <= last; p++) {
                if (t[p] == 0) continue;
                if (!s_Exact) return true;
                if (write && TableR[p] != 0) { if (!RExact) return true; rHit = true; } // read pages: ranges only with RExact
                if (TableW[p] != 0) wHit = true;
                else if (!(write && TableR[p] != 0)) return true; // neither bit (cannot happen): be safe
            }
            if (rHit) for (int i = 0; i < RN; i++) if (phys < RHi[i] && RLo[i] < end) return true;
            if (!wHit) return false;
            if (WAll) return true;
            for (int i = 0; i < WN; i++) if (phys < WHi[i] && WLo[i] < end) return true;
            return false;
        }

        public static void ClearAll() { NativeMemory.Clear(TableW, Pages); NativeMemory.Clear(TableA, Pages); NativeMemory.Clear(TableR, Pages); Armed = false; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Hit() { HitLen = 0; Hits++; Waiter?.Invoke(); }
        // [climb4 rdp_handoff1] the access behind the current hit, for COR64_RDPHO1_PWAIT (HitLen 0 = unknown: wait for everything)
        public static uint HitPhys, HitLen;
        public static bool HitWrite;
        private static void HitAt(uint phys, uint len, bool write) { HitPhys = phys; HitLen = len; HitWrite = write; Hits++; Waiter?.Invoke(); HitLen = 0; }
    }
}
