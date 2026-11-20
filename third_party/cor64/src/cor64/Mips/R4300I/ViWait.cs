using System;

namespace cor64.Mips.R4300I
{
    // [climb4 speed_cat9] COR64_SPDCAT9_VIWAIT (default on; =0 off): VI line wait skip. Roadsters Trophy spins in
    //   head: jal F; nop; ... F: lui rT,hi; jr ra; lw rV,lo(rT)  (VI_CURRENT, A4400010); sltiu rA,rV,K; bne rA,zero,head; nop
    // for almost every CPU cycle (82M block exits per 300 frames, no RSP work). VI_CURRENT only changes in Video.Tick, which
    // runs after the CPU part of a batch (the register read has no side effect), so inside one batch every pass reads the same
    // line. When that line is below K the loop cannot leave during the batch: the CPU part is a few real steps up to the
    // loop head, a whole number of 8-step passes replaced by their only effects (Count, TLB Random, the interrupt-check
    // cadence with no interrupt taken, Cause CE, and the registers one pass writes: ra, rT, rV, rA), then the real rest.
    // Same state as running the steps. Enabled per game by a GameDb ViLineWait row (value = loop head PC).
    public partial class Interpreter
    {
        public static uint ViWaitPc; // loop head (0 = off); set when the cart is attached
        public static long ViWaitSkipped, ViWaitBatches;
        static readonly bool s_ViWaitOn = Environment.GetEnvironmentVariable("COR64_SPDCAT9_VIWAIT") != "0";
        public static void ViWaitFor(string gameCode) {
            ViWaitPc = 0;
            if (!s_ViWaitOn || !cor64.Session.GameDb.TryGet(cor64.Session.GameDb.Setting.ViLineWait, cor64.Session.GameDb.Id(gameCode), out int v)) return;
            ViWaitPc = (uint)v;
        }

        // decoded loop (valid while the words are unchanged)
        int m_VwT, m_VwV, m_VwA; uint m_VwF; ulong m_VwK, m_VwTVal;

        bool ViWaitShape() {
            uint h = ViWaitPc;
            if ((h & 0xC0000000u) != 0x80000000u) return false;
            long p = h & 0x1FFFFFFF;
            if (p + 32 > 0x800000) return false;
            uint w0 = FetchInstruction(p), w1 = FetchInstruction(p + 4), w5 = FetchInstruction(p + 8), w6 = FetchInstruction(p + 12), w7 = FetchInstruction(p + 16);
            if ((w0 >> 26) != 3 || w1 != 0 || w7 != 0) return false;
            uint f = ((h + 4) & 0xF0000000u) | ((w0 & 0x03FFFFFF) << 2);
            if ((f & 0xC0000000u) != 0x80000000u) return false;
            long fp = f & 0x1FFFFFFF;
            if (fp + 12 > 0x800000) return false;
            uint w2 = FetchInstruction(fp), w3 = FetchInstruction(fp + 4), w4 = FetchInstruction(fp + 8);
            if ((w2 & 0xFFE00000u) != 0x3C000000u || w3 != 0x03E00008u || (w4 >> 26) != 0x23) return false;
            int t = (int)((w2 >> 16) & 31);
            if (t == 0 || t == 31 || (int)((w4 >> 21) & 31) != t) return false;
            int v = (int)((w4 >> 16) & 31);
            ulong tval = (ulong)(long)(int)(w2 << 16);
            uint addr = (uint)((long)tval + (short)(w4 & 0xFFFF));
            if (addr != 0xA4400010u || v == 0 || v == t || v == 31) return false;
            if ((w5 >> 26) != 0x0B || (int)((w5 >> 21) & 31) != v) return false;
            int a = (int)((w5 >> 16) & 31);
            if (a == 0 || a == v || a == t || a == 31) return false;
            if (w6 != (0x14000000u | ((uint)a << 21) | 0xFFFCu)) return false; // bne a,zero,head
            m_VwT = t; m_VwV = v; m_VwA = a; m_VwF = f; m_VwTVal = tval; m_VwK = (ulong)(long)(short)(w5 & 0xFFFF);
            return true;
        }

        bool ViWaitInLoop() {
            uint pc = (uint)PC, h = ViWaitPc;
            return (pc >= h && pc <= h + 16) || (pc >= m_VwF && pc <= m_VwF + 8);
        }

        bool ViWaitAtHead() => (uint)PC == ViWaitPc && (PC >> 32) == 0 && !BU_ConditionalPending && !BU_UnconditionalPending && !BU_DelaySlotNext
            && !BU_ExecuteBranchDelay && !BU_NullifyNext && !m_InjectedInst.HasValue && !m_ExceptionReturn;

        /// <summary>CPU part of a batch inside the VI line wait. False (nothing run) when it does not apply.</summary>
        public unsafe bool TryViWaitBatch(int n, uint line) {
            if (ViWaitPc == 0 || n < 24) return false;
            if (m_VwF == 0 || !ViWaitInLoop()) { if (!ViWaitShape() || !ViWaitInLoop()) return false; }
            else if (!ViWaitShape()) return false;
            if ((ulong)(long)(int)line >= m_VwK) return false; // the loop leaves during this batch
            int done = 0;
            while (done < 8 && !ViWaitAtHead()) { Step(); done++; }
            int m = ((n - done) / 8) * 8;
            if (!ViWaitAtHead() || m == 0 || !Cop0.IdleSlicePossible((uint)m)) { if (n > done) RunSteps(n - done); return true; }
            int checks = 0;
            for (int i = 0; i < m; i++) {
                TLBTick();
                if (++m_IntCheckCounter >= s_IntCheckEvery) { m_IntCheckCounter = 0; checks++; }
            }
            if (checks > 0) Cop0.IdleSliceApplyCheck(); else Cop0.ClearCauseCe();
            Cop0.MipsTimerTick((ulong)m);
            ulong* g = (ulong*)State.GetGprRef();
            g[31] = (ulong)(long)(int)(ViWaitPc + 8);
            g[m_VwT] = m_VwTVal;
            g[m_VwV] = (ulong)(long)(int)line;
            g[m_VwA] = 1;
            m_LastFetchAddress = PC + 16;
            IdleSkippedSteps += m; ViWaitSkipped += m; ViWaitBatches++;
            if (n - done - m > 0) RunSteps(n - done - m);
            return true;
        }
    }
}
