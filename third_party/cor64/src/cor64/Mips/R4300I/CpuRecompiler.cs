// [cer64plus] VR4300 recompiler: hot KSEG0/KSEG1 blocks become one DynamicMethod each.
//
// Contract: running k steps of a block leaves the machine exactly as k calls of Step() would.
//  - Per-step bookkeeping is lazy but exact: the TLB Random and Count ticks pile up in locals and are applied where
//    they can be observed (an interrupt check, a handler call, leaving the block); the interrupt-check cadence lives in
//    a local. Count after n single ticks equals one tick of n (a 32-bit add), Random advances by the same rule.
//  - A block is entered only with no branch pending, no servicing and no NMI pending. Inside it those can only start
//    at an interrupt check or in a handler, and both are followed by a test that hands the step back to the
//    interpreter's own code (ExecuteAfterPrologue / StepTail) and leaves the block.
//  - Non-trapping integer ops (ADDU/ADDIU/SUBU, logic, LUI, 32-bit shifts, SLT*, DADDU/DADDIU) are inline copies of the
//    handlers' arithmetic (CPU_ALWAYS_64); everything else calls its handler directly.
//  - A branch runs the interpreter's tail, then the block continues into the delay slot, whose tail commits the jump.
//  - Blocks take a step budget and can be entered at any instruction (jump table), so TickBatch's batch boundaries and
//    every RSP/VI/AI interleaving are unchanged.
// COR64_CPU_REC=0 turns it off.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace cor64.Mips.R4300I
{
    public delegate int CpuRecFn(Interpreter self, DecodedInstruction[] insts, int max, Action<DecodedInstruction>[] handlers, int start);

    public sealed class CpuRecBlock
    {
        public long StartPc;
        public uint StartPhys;
        public uint[] Raw;
        public DecodedInstruction[] Insts;
        public Action<DecodedInstruction>[] Handlers;
        public int Epoch; // [climb4 x64_emit] RecClear generation the block was discovered in
        public bool Retired; // [climb4 x64_emit] native code handed back to ExecMem
        public IntPtr MidEntry; // [f5_speed3] chained jump into step r9 > 0
        public IntPtr ChainEntry; // [block_link] native entry for a chained jump from another block (validates the words)
        public bool NVal; // [block_link] native code validates its own words at entry (COR64_X64_NVAL)
        public int Len; // Raw.Length, without touching Raw
        public int XId = -1; // [block_link] index in Interpreter.s_XById
        public IntPtr Native; // [climb4 x64_emit] native entry (COR64_X64=1); Fn is then a sentinel
        public volatile CpuRecFn Fn; // published last by the compile worker (fields below are set before it)
        public int Runs;
        public volatile bool Failed;
        public bool Queued;
        public int IlSize;
        [NonSerialized] public bool UsesTlb; // [session_leak] native code probes the TLB cache inline
        public bool[] DelayEntry; // step index is a delay slot that can be entered with the branch already run
        public int MinLoop = int.MaxValue;
        public long TTicks, TSteps, TCalls; // measurement (COR64_CPU_STEP_TIMING=1) // lowest step a taken branch loops back to inside the block (validated on every entry)
    }

    public partial class Interpreter
    {
        // On by default: bit-identical (boot hash, goldens, CPU test ROMs identical with COR64_CPU_REC_AFTER=1) and ~5% faster
        // at steady state. COR64_CPU_REC=0 turns it off.
        public static readonly bool s_CpuRec = Environment.GetEnvironmentVariable("COR64_CPU_REC") != "0";
        private static readonly int s_CpuRecAfter = int.TryParse(Environment.GetEnvironmentVariable("COR64_CPU_REC_AFTER"), out var cra) && cra >= 1 ? cra : 4;
        private const int CpuRecMaxBlock = 32;
        private static readonly bool s_NoVal = Environment.GetEnvironmentVariable("COR64_CPU_REC_NOVAL") == "1"; // measurement only (unsafe)
        private static readonly bool s_RecLoops = Environment.GetEnvironmentVariable("COR64_CPU_REC_LOOP") != "0";
        private static readonly bool s_RecSuperblocks = Environment.GetEnvironmentVariable("COR64_CPU_REC_SB") != "0";
        private static readonly bool s_RecInlineBranch = Environment.GetEnvironmentVariable("COR64_CPU_REC_BR") != "0";
        // measurement aid (COR64_CPU_OPMIX=1): handler calls from compiled blocks, by op name
        // COR64_FPUCVT_INLINE=0: trunc.w / cvt.s / cvt.d / neg / c.cond (ordered, non-signaling-unordered) on the raw FPR path
        private static readonly bool s_FpuCvt = Environment.GetEnvironmentVariable("COR64_FPUCVT_INLINE") != "0";
        private static int CvtKind(in DecodedInstruction d)
        {
            string op = d.Op.Op;
            var f = d.Format;
            bool fl = f == FpuValueType.FSingle || f == FpuValueType.FDouble;
            if (op == "trunc.w.fmt") return fl ? 1 : 0;
            if (op == "cvt.s.fmt") return f == FpuValueType.FDouble || f == FpuValueType.Word ? 2 : 0;
            if (op == "cvt.d.fmt") return f == FpuValueType.FSingle || f == FpuValueType.Word ? 3 : 0;
            if (op == "neg.fmt") return fl ? 4 : 0;
            if (op.StartsWith("c.") && (d.Inst.inst >> 26) == 0x11 && fl
                && (d.Op.Flags & ExecutionFlags.CondUn) == 0) return 5;
            return 0;
        }
        // same as Interpreter.FixedOp(TowardZero) + WriteFixed(is32)
        private static int CvtTruncW(double v)
        {
            double r = Math.Truncate(v);
            return (r >= 2147483648.0 || r < -2147483648.0 || double.IsNaN(r)) ? int.MaxValue : (int)r;
        }
        private static readonly MethodInfo s_CvtTruncW = typeof(Interpreter).GetMethod(nameof(CvtTruncW), BindingFlags.Static | BindingFlags.NonPublic);
        // same as Interpreter.Condition for opcodes without CondUn; false = NaN on an ordered compare (handler raises Invalid)
        private bool CvtCmp(double a, double b, int flags)
        {
            var fl = (ExecutionFlags)flags;
            bool nan = double.IsNaN(a) || double.IsNaN(b);
            if (nan && (fl & ExecutionFlags.CondOrd) != 0) return false;
            bool r = false;
            if ((fl & ExecutionFlags.CondEq) != 0) r |= a == b;
            if ((fl & ExecutionFlags.CondLT) != 0) r |= a < b;
            if ((fl & ExecutionFlags.CondGT) != 0) r |= a > b;
            if ((fl & ExecutionFlags.CondNot) != 0) r = !r;
            State.FCR.Condition = r;
            return true;
        }
        private static readonly MethodInfo s_CvtCmp = typeof(Interpreter).GetMethod(nameof(CvtCmp), BindingFlags.Instance | BindingFlags.NonPublic);

        // measurement only (COR64_FPUCVT_CEIL=1): FPU convert/compare, mflo/mfhi, mult* handlers run twice
        private static readonly bool s_CvtCeil = Environment.GetEnvironmentVariable("COR64_FPUCVT_CEIL") == "1";
        private static bool CvtCeilOp(in DecodedInstruction d)
        {
            string op = d.Op.Op;
            if (op == "mflo" || op == "mfhi" || op == "multu" || op == "mult") return true;
            if ((d.Inst.inst >> 26) != 0x11) return false;
            if (op.StartsWith("c.")) return true;
            bool cv = op.StartsWith("cvt") || op.StartsWith("trunc") || op.StartsWith("round") || op.StartsWith("floor") || op.StartsWith("ceil") || op.StartsWith("neg");
            return cv && d.FloatDest != d.FloatSource;
        }
        public static readonly bool s_OpMix = Environment.GetEnvironmentVariable("COR64_CPU_OPMIX") == "1";
        public static readonly Dictionary<string, long> OpMix = new();
        public static void OpMixCount(string op) { OpMix.TryGetValue(op, out long c); OpMix[op] = c + 1; }
        private static readonly MethodInfo s_OpMixCount = typeof(Interpreter).GetMethod(nameof(OpMixCount));
        // a block entered mid-way must also validate the words its loop-backs return to (COR64_CPU_REC_VAL_LOOPS=0: old behaviour)
        private static readonly bool s_ValLoops = Environment.GetEnvironmentVariable("COR64_CPU_REC_VAL_LOOPS") != "0";
        private static readonly bool s_DelayFast = Environment.GetEnvironmentVariable("COR64_CPUS_DELAYFAST") != "0";
        // [scripts_speed] COR64_SCRIPTS_SPEED_TLBREC=0: TLB-mapped code is interpreted, as before
        private static readonly bool s_TlbRec = Environment.GetEnvironmentVariable("COR64_SCRIPTS_SPEED_TLBREC") != "0";
        private static readonly bool s_RecDelayEntry = Environment.GetEnvironmentVariable("COR64_CPU_REC_DELAY_ENTRY") != "0";
        public static long CpuRecCompiled, CpuRecRejected, CpuRecSteps, CpuRecCalls, CpuRecInterp, CpuRecNoEntry;

        // phys >> 12 -> page of 1024 block slots (RDRAM only)
        private CpuRecBlock[][] m_RecPages = new CpuRecBlock[0x800000 >> 12][];

        [NonSerialized] private int m_RecEpoch;
        private void RecClear()
        {
            // [climb4 x64_emit] COR64_X64_FREE: retire the native code of every dropped block (reclaimed at the next RunSteps)
            if (s_X64 && s_X64Free) {
                foreach (var pg in m_RecPages) if (pg != null) foreach (var blk in pg) if (blk != null && blk.Native != IntPtr.Zero && !blk.Retired) { blk.Retired = true; X64.ExecMem.Retire(blk.Native); XIdRetire(blk); }
            }
            Array.Clear(m_RecPages);
            System.Threading.Interlocked.Increment(ref m_RecEpoch);
        }

        // [mem_fix] COR64_MEM_RELEASE_BLOCKS (default on): a disposed session's native blocks stay in the static
        // s_XById table, which keeps the whole old N64System (about 100-300 MB managed plus its native buffers and code)
        // alive for the rest of the process. On dispose, retire them like RecClear and drop their table slots now; the
        // ids are reused after the next ExecMem.Reclaim, as for any retired block.
        public static readonly bool s_MemReleaseBlocks = Environment.GetEnvironmentVariable("COR64_MEM_RELEASE_BLOCKS") != "0";
        public void ReleaseBlocks()
        {
            if (!s_MemReleaseBlocks) return;
            foreach (var pg in m_RecPages) if (pg != null) foreach (var blk in pg) {
                if (blk == null) continue;
                if (blk.Native != IntPtr.Zero && !blk.Retired) { blk.Retired = true; X64.ExecMem.Retire(blk.Native); XIdRetire(blk); }
                if (blk.XId >= 0) lock (s_XIdLock) { if (s_XById[blk.XId] == blk) s_XById[blk.XId] = null; }
            }
            // blocks dropped from m_RecPages without a retire can still sit in s_XById: find them by their handlers' target
            lock (s_XIdLock) {
                var t = s_XById;
                for (int i = 0; i < t.Length; i++) {
                    var blk = t[i]; if (blk == null) continue;
                    var h = blk.Handlers; bool mine = false;
                    if (h != null) foreach (var d in h) if (d != null && ReferenceEquals(d.Target, this)) { mine = true; break; }
                    if (!mine) continue;
                    if (blk.Native != IntPtr.Zero && !blk.Retired) { blk.Retired = true; X64.ExecMem.Retire(blk.Native); s_XIdRetired.Add(i); }
                    t[i] = null;
                }
            }
            Array.Clear(m_RecPages);
            System.Threading.Interlocked.Increment(ref m_RecEpoch);
        }

        // [dbg_mem] debugger: set the PC between frames (drops any pending branch and carried block state)
        public void DebugSetPC(long pc) { PC = pc; ClearBranchState(); m_QuietValid = false; m_DelayFromRec = false; m_XPendLink = 0; }
        // [dbg_mem] debugger: VA to physical with no side effects (KSEG0/1 direct, else a TLB walk that fills no cache)
        public bool DebugTranslate(long va, out long phys)
        {
            uint v = (uint)va;
            if (v >= 0x80000000u && v < 0xC0000000u) { phys = v & 0x1FFFFFFF; return true; }
            return TLB.TryTranslateDebug(v, out phys);
        }

        public override void InvalidateDecodeCache() { base.InvalidateDecodeCache(); RecClear(); m_QuietValid = false; TLB.InvalidateFastCache(); }

        // Runs n steps, as blocks where possible.
        public void RunSteps(int n)
        {
            if (!s_CpuRec || !s_Guards || PhysicalMemory.Watches != null) { m_QuietValid = false; for (int i = 0; i < n; i++) Step(); return; } // [dbg_mem] watches: interpret (blocks bypass the bus)
            if (s_ClearTest > 0) X64ClearTest();
            if (X64.ExecMem.RetiredCount != 0) { X64.ExecMem.Reclaim(); XIdReclaim(); }
            int done = 0;
            if (s_StepTiming) { RunStepsTimed(n); return; }
            while (done < n) {
                var b = RecEntry(out int start);
                if (b != null) {
                    if (m_XPendLink != 0) X64TryLink(b, start);
                    int k = b.Native != IntPtr.Zero ? RunX64(b, n - done, start) : b.Fn(this, b.Insts, n - done, b.Handlers, start);
                    if (k < 0) { m_RecStale = true; continue; } // [block_link] its words changed: nothing ran, RecEntry drops it
                    if (s_X64Resume && b.Native != IntPtr.Zero) X64NoteResume(b);
                    done += k;
                    CpuRecSteps += k; CpuRecCalls++;
                    m_DelayFromRec = true;
                    if (k > 0) continue;
                }
                bool keepQ = s_QuietKeep && m_QuietValid;
                m_QuietValid = false; m_DelayFromRec = false; m_XPendLink = 0;
                Step(); CpuRecInterp++;
                if (s_X64Exits) { QKeep[keepQ ? 1 : 0]++; if (keepQ && !QuietSafeWord(m_StepWord)) QKeep[2]++; }
                if (keepQ && QuietSafeWord(m_StepWord) && !Cop0.ExceptionPending && !m_ExceptionReturn && !Cop0.ServicingRequred && !Cop0.IsNonMaskablePending) m_QuietValid = true;
                done++;
            }
        }

        // [jit_cost1] COR64_QUIET_KEEP: an interpreter step between blocks keeps the carried quiet window when its op cannot
        // change interrupt state: an integer op (no syscall/break) or a load (Count is re-read at the next entry, MI is
        // compared there, and an exception or pending servicing still drops it). Naboo's page-crossing delay slot (an lbu)
        // made almost every native call start with no window, so its first interrupt check left the native code.
        public static readonly long[] QKeep = new long[3];
        private static readonly bool s_QuietKeep = Environment.GetEnvironmentVariable("COR64_QUIET_KEEP") != "0";
        private static bool QuietSafeWord(uint w)
        {
            uint major = w >> 26;
            if (major == 0) { uint f = w & 0x3F; return f != 0x0C && f != 0x0D && f != 0x0F && f < 0x30; }
            return (major >= 0x08 && major <= 0x0F) || major == 0x18 || major == 0x19 || (major >= 0x20 && major <= 0x27) || major == 0x37;
        }

        // [cer64plus] measurement aid (COR64_CPU_STEP_TIMING=1): wall time of RecEntry, compiled blocks and interpreter steps.
        public static readonly bool s_StepTiming = Environment.GetEnvironmentVariable("COR64_CPU_STEP_TIMING") == "1";
        public static readonly long[] TReason = new long[6], TReasonCalls = new long[6], TReasonSteps = new long[6], TLen = new long[4], TLenCalls = new long[4], TLenSteps = new long[4];
        public static long TInterpMid, TInterpMidRec, TEntry, TBlock, TInterp, TBlockSteps, TFirst, TFirstN;
        public static readonly List<CpuRecBlock> TimedBlocks = new();
        public static readonly long[] TWhy = new long[10];
        // why RecEntry gave no block: 0 mid-branch (branch by interpreter), 1 mid-branch refused, 2 injected/eret/slow fetch,
        // 3 servicing/exception, 4 not KSEG0 RDRAM, 5 no block / words changed, 6 not compiled yet, 7 failed, 8 other
        private int RecWhyNull()
        {
            if (BU_ConditionalPending || BU_UnconditionalPending || BU_DelaySlotNext || BU_ExecuteBranchDelay || BU_NullifyNext)
                return m_DelayFromRec ? 1 : 0;
            if (m_InjectedInst.HasValue || m_ExceptionReturn || !FastFetchMode) return 2;
            if (Cop0.ServicingRequred || Cop0.ExceptionPending) return 3;
            uint va = (uint)PC; if ((va >> 30) != 2 || (va & 0x1FFFFFFF) >= 0x800000) return 4;
            uint phys = va & 0x1FFFFFFF; var page = m_RecPages[phys >> 12]; var b = page?[(int)((phys & 0xFFF) >> 2)];
            if (b == null) return 5;
            if (b.Failed) return 7;
            if (b.Fn == null) return 6;
            return 8;
        }
        public static readonly Dictionary<string, long> DelayLeave = new();
        public static void DelayLeaveCount(string op) { DelayLeave.TryGetValue(op, out long c); DelayLeave[op] = c + 1; }
        public static void ResetTiming()
        {
            TEntry = TBlock = TInterp = TBlockSteps = TInterpMid = TInterpMidRec = 0; CpuRecSteps = CpuRecCalls = CpuRecInterp = 0;
            DelayLeave.Clear(); Array.Clear(TWhy); Array.Clear(TReason); Array.Clear(TReasonCalls); Array.Clear(TReasonSteps); Array.Clear(TLen); Array.Clear(TLenCalls); Array.Clear(TLenSteps);
            foreach (var b in TimedBlocks) { b.TTicks = b.TSteps = 0; }
        }
        public static string TimedReport(int top)
        {
            var l = new List<CpuRecBlock>(TimedBlocks); l.Sort((a, c) => c.TTicks.CompareTo(a.TTicks));
            var sb = new System.Text.StringBuilder(); double f = System.Diagnostics.Stopwatch.Frequency / 1e3;
            // by calls per block: where the time sits (hot blocks vs the long tail)
            long[] lim = { 100, 1000, 10000, 100000, long.MaxValue }; var bt = new long[5]; var bs = new long[5]; var bc = new long[5]; var bn = new int[5];
            foreach (var b in l) { int i = 0; while (b.TCalls > lim[i]) i++; bt[i] += b.TTicks; bs[i] += b.TSteps; bc[i] += b.TCalls; bn[i]++; }
            double ns = 1e9 / System.Diagnostics.Stopwatch.Frequency;
            string[] rn = { "budget", "likelyNT-mid", "likelyNT-end", "blockEnd", "leftBlock", "other" };
            for (int i = 0; i < 6; i++) sb.Append($"REASON {rn[i]} ms={TReason[i] / f:F0} calls={TReasonCalls[i]} steps={TReasonSteps[i]} nsPerCall={TReason[i] * ns / Math.Max(1, TReasonCalls[i]):F0}\n");
            foreach (var kv in DelayLeave) if (kv.Value > 10000) sb.Append($"DELAYLEAVE {kv.Key} {kv.Value}\n");
            sb.Append($"WHYNULL {string.Join(" ", TWhy)}\n");
            sb.Append($"INTERPMID steps={TInterpMid} afterRec={TInterpMidRec}\n");
            string[] ln = { "<=2", "<=4", "<=8", ">8" };
            for (int i = 0; i < 4; i++) sb.Append($"LEN {ln[i]} ms={TLen[i] / f:F0} calls={TLenCalls[i]} steps={TLenSteps[i]}\n");
            for (int i = 0; i < 5; i++) sb.Append($"BUCKET calls<={(lim[i] == long.MaxValue ? "inf" : lim[i].ToString())} blocks={bn[i]} ms={bt[i] / f:F0} steps={bs[i]} calls={bc[i]} nsPerStep={bt[i] * 1e9 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, bs[i]):F0} nsPerCall={bt[i] * 1e9 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, bc[i]):F0}\n");
            for (int i = 0; i < Math.Min(top, l.Count); i++) {
                var b = l[i];
                sb.Append($"BLK {(uint)b.StartPc:X8} ms={b.TTicks / f:F0} steps={b.TSteps} calls={b.TCalls} nsPerStep={b.TTicks * 1e9 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, b.TSteps):F0} :");
                foreach (var d in b.Insts) sb.Append(' ').Append(d.Op.Op);
                sb.Append('\n');
            }
            return sb.ToString();
        }
        private void RunStepsTimed(int n)
        {
            int done = 0;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            while (done < n) {
                var b = RecEntry(out int start);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp(); TEntry += t1 - t0; t0 = t1;
                if (b != null) {
                    int k = b.Fn(this, b.Insts, n - done, b.Handlers, start);
                    t1 = System.Diagnostics.Stopwatch.GetTimestamp(); long dt = t1 - t0; TBlock += t1 - t0; b.TTicks += t1 - t0; b.TSteps += k; b.TCalls++; t0 = t1; TBlockSteps += k;
                    if (b.TCalls == 1) { TimedBlocks.Add(b); TFirst += b.TTicks; TFirstN++; }
                    else {
                        // exit reason: 0 budget, 1 likely not taken (more block follows), 2 likely not taken at block end,
                        // 3 fell off block end, 4 left the block (taken branch/jump), 5 other (handler/interrupt)
                        int r; long off = (PC - b.StartPc) >> 2; int nn = b.Insts.Length;
                        if (k == n - done) r = 0;
                        else if (off >= 2 && off <= nn && PC - b.StartPc == off * 4 && IsCpuBranch(b.Insts[off - 2]) && b.Insts[off - 2].IsLikely() && !BU_ExecuteBranchDelay) r = off < nn ? 1 : 2;
                        else if (off == nn && PC - b.StartPc == off * 4) r = 3;
                        else if (off < 0 || off > nn) r = 4;
                        else r = 5;
                        TReason[r] += dt; TReasonCalls[r]++; TReasonSteps[r] += k;
                        int lb = k <= 2 ? 0 : k <= 4 ? 1 : k <= 8 ? 2 : 3; TLen[lb] += dt; TLenCalls[lb]++; TLenSteps[lb] += k;
                    }
                    done += k; CpuRecSteps += k; CpuRecCalls++; m_DelayFromRec = true;
                    if (k > 0) continue;
                }
                TWhy[RecWhyNull()]++;
                if (BU_ConditionalPending || BU_UnconditionalPending || BU_DelaySlotNext || BU_ExecuteBranchDelay || BU_NullifyNext) { TInterpMid++; if (m_DelayFromRec) TInterpMidRec++; }
                m_QuietValid = false; m_DelayFromRec = false;
                Step(); CpuRecInterp++; done++;
                t1 = System.Diagnostics.Stopwatch.GetTimestamp(); TInterp += t1 - t0; t0 = t1;
            }
        }

        // Every instruction slot of a block points at it, so a batch that ends mid-block resumes inside the same compiled
        // method (a jump table at its start) instead of creating a new block at that PC.
        private unsafe CpuRecBlock RecEntry(out int start)
        {
            start = 0;
            bool bd = false;
            if (BU_ConditionalPending || BU_UnconditionalPending || BU_DelaySlotNext || BU_ExecuteBranchDelay || BU_NullifyNext) {
                // a batch ended between a branch and its delay slot: enter the block at the delay slot
                if (!s_RecDelayEntry || !m_DelayFromRec || !BU_ExecuteBranchDelay || BU_DelaySlotNext || BU_NullifyNext) return null;
                bd = true;
            }
            if (m_InjectedInst.HasValue || m_ExceptionReturn || !FastFetchMode) return null;
            if (Cop0.ServicingRequred || Cop0.ExceptionPending) return null;
            long pc = PC;
            uint va = (uint)pc;
            // [block_link] COR64_X64_RESUME: a batch that ended inside a native block resumes it without the translation and
            // page lookup, while the TLB/ASID epoch is unchanged (the block checks its own words: NVal)
            var rb = m_ResumeBlk;
            if (rb != null) {
                m_ResumeBlk = null;
                if (pc == m_ResumePc && !bd && !m_RecStale && !rb.Retired && m_ResumeEpoch == *global::cor64.Mips.R4300I.TLB.MapEpoch.Ptr
                    && ((va >> 30) == 2 || (s_TlbRec && !Cop0.IsNonMaskablePending))) { start = m_ResumeOff; return rb; }
            }
            uint phys;
            if ((va >> 30) == 2) phys = va & 0x1FFFFFFF;
            else {
                // [scripts_speed] TLB-mapped code (GoldenEye, Perfect Dark, Conker, ...): find its physical block through the
                // TLB, with no side effects. Mapped blocks stay inside one 4 KB page and end at anything that can change the
                // mapping (RecDiscover), so every fetch the block replaces would have translated to the same page.
                if (!s_TlbRec || Cop0.IsNonMaskablePending || !TLB.TryTranslateFetchQuiet(pc, out long mp)) return null;
                phys = (uint)mp;
            }
            if (phys >= 0x800000) return null;
            var page = m_RecPages[phys >> 12];
            if (page == null) m_RecPages[phys >> 12] = page = new CpuRecBlock[1024];
            int slot = (int)((phys & 0xFFF) >> 2);
            var b = page[slot];
            if (b != null) {
                int off = (int)((phys - b.StartPhys) >> 2);
                // [block_link] a native block with NVal checks its words itself when it is entered (unless bd: below)
                if ((uint)off >= (uint)b.Len || b.StartPc + off * 4 != pc) b = null;
                else if (m_RecStale) { m_RecStale = false; b = null; }
                else if (!s_NoVal && !(b.NVal && !bd) && !RecValidate(b, Math.Min(off, b.MinLoop))) b = null;
                else start = off;
            }
            if (bd) {
                // the branch itself must still be the compiled one too (a taken inline branch may loop by its static target)
                if (b == null || b.Fn == null || b.DelayEntry == null || !b.DelayEntry[start] || (!s_NoVal && !RecValidate(b, Math.Min(start - 1, b.MinLoop)))) return null;
                return b;
            }
            if (b == null) {
                b = RecDiscover(pc, phys);
                if (b == null) return null;
                b.Epoch = m_RecEpoch;
                // register the block in its slots that lie in this page
                for (int i = 0; i < b.Raw.Length; i++) {
                    uint p = phys + (uint)(i * 4);
                    if ((p >> 12) != (phys >> 12)) break;
                    page[(int)((p & 0xFFF) >> 2)] = b;
                }
                start = 0;
            }
            if (b.Fn == null) {
                if (b.Failed || ++b.Runs < s_CpuRecAfter) return null;
                if (s_RecBg) {
                    // compiled and JIT-warmed on the worker; the interpreter runs this code until then (same results)
                    if (!b.Queued) { b.Queued = true; RecQueue(b); }
                    return null;
                }
                b.Fn = RecCompile(b);
                if (b.Fn == null) { b.Failed = true; return null; }
            }
            return b;
        }

        // [cer64plus] Background compilation (COR64_CPU_REC_BG=0: compile inline on first use, as before). A DynamicMethod is
        // JIT-compiled on its first call (~3.3 ms per block), so the worker also calls each new block once with start = -1,
        // which returns at once without touching any state. Blocks and the interpreter give identical results, so when a
        // block becomes available changes nothing but host time.
        private static readonly bool s_RecBg = Environment.GetEnvironmentVariable("COR64_CPU_REC_BG") != "0";
        [NonSerialized] private System.Collections.Concurrent.BlockingCollection<CpuRecBlock> m_RecQueue;
        // [cer64plus] COR64_HT_CPUBG_WORKERS: background compiler threads (default 2; 1 = as before). Each block is JIT-
        // compiled by exactly one worker; which one, and when, changes only host time.
        private static readonly int s_RecWorkers = int.TryParse(Environment.GetEnvironmentVariable("COR64_HT_CPUBG_WORKERS"), out var rw) && rw >= 1 ? rw : 2;
        public static long CpuRecBgCompiled, BgEmitTicks, BgJitTicks, BgIlBytes;
        private unsafe void RecQueue(CpuRecBlock b)
        {
            if (m_RecQueue == null) {
                _ = RdramWords; // resolve lazily-initialised pointers on this thread before the worker reads them
                m_RecQueue = new();
                var q = m_RecQueue;
                for (int wi = 0; wi < s_RecWorkers; wi++) {
                var t = new System.Threading.Thread(() => {
                    foreach (var blk in q.GetConsumingEnumerable()) {
                        try {
                            long w0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            var fn = RecCompile(blk);
                            if (fn == null) { blk.Failed = true; continue; }
                            long w1 = System.Diagnostics.Stopwatch.GetTimestamp();
                            fn(this, blk.Insts, 0, blk.Handlers, -1); // JIT now, off the emulation thread
                            System.Threading.Interlocked.Add(ref BgEmitTicks, w1 - w0); System.Threading.Interlocked.Add(ref BgJitTicks, System.Diagnostics.Stopwatch.GetTimestamp() - w1); System.Threading.Interlocked.Add(ref BgIlBytes, blk.IlSize);
                            System.Threading.Interlocked.Increment(ref CpuRecBgCompiled);
                            // a block dropped by RecClear while it was queued is unreachable: hand its code straight back
                            if (blk.Native != IntPtr.Zero && blk.Epoch != System.Threading.Volatile.Read(ref m_RecEpoch) && s_X64Free) { blk.Retired = true; X64.ExecMem.Retire(blk.Native); XIdRetire(blk); }
                            blk.Fn = fn;
                        }
                        catch { blk.Failed = true; }
                    }
                }) { IsBackground = true, Name = "cor64 CPU block compiler", Priority = System.Threading.ThreadPriority.BelowNormal };
                t.Start();
                }
            }
            m_RecQueue.Add(b);
        }

        // The words that will run (from the entry offset on) must still be the compiled ones; RDRAM is read directly.
        private unsafe bool RecValidate(CpuRecBlock b, int from)
        {
            int len = b.Raw.Length - from;
            return new ReadOnlySpan<uint>(RdramWords + (b.StartPhys >> 2) + from, len).SequenceEqual(new ReadOnlySpan<uint>(b.Raw, from, len)); // vectorized
        }

        private static bool IsCpuBranch(in DecodedInstruction d) => d.Op.Family == OperationFamily.Branch;

        private CpuRecBlock RecDiscover(long pc, uint phys)
        {
            var raws = new List<uint>(); var insts = new List<DecodedInstruction>(); var hs = new List<Action<DecodedInstruction>>();
            // Superblocks: a conditional branch's fall-through continues the block (a taken branch leaves it);
            // a jump, or a branch in a delay slot, ends it.
            bool delay = false, endAfterDelay = false;
            bool mapped = ((uint)pc >> 30) != 2; // [scripts_speed] TLB-mapped: stay inside this 4 KB page
            while (raws.Count < CpuRecMaxBlock && phys + raws.Count * 4 < 0x800000 && (!mapped || ((phys + (uint)raws.Count * 4) >> 12) == (phys >> 12))) {
                uint raw = FetchInstruction(phys + raws.Count * 4);
                var d = Disassembler.Decode(raw);
                if (d.IsInvalid) break;
                var h = CallTable[d];
                if (h == null) break;
                raws.Add(raw); insts.Add(d); hs.Add(h);
                string op = d.Op.Op;
                // [scripts_speed] a mapped block ends after anything that can change address translation (EntryHi/ASID, TLB)
                if (mapped && (op == "mtc0" || op == "dmtc0" || op == "tlbwi" || op == "tlbwr" || op == "tlbr")) {
                    if (delay) break;
                    if (!IsCpuBranch(d)) break;
                }
                if (delay) {
                    if (endAfterDelay || IsCpuBranch(d) || !s_RecSuperblocks) break;
                    delay = false;
                    continue;
                }
                if (op == "eret" || op == "syscall" || op == "break") break;
                if (IsCpuBranch(d)) { delay = true; endAfterDelay = op == "j" || op == "jal" || op == "jr" || op == "jalr" || d.Op.XferTarget == RegBoundType.Cp1; }
            }
            if (raws.Count == 0) return null;
            return new CpuRecBlock { StartPc = pc, StartPhys = phys, Len = raws.Count, Raw = raws.ToArray(), Insts = insts.ToArray(), Handlers = hs.ToArray() };
        }

        // [cer64plus] measurement aid: ns per step of RunSteps(16) on a synthetic loop at KSEG0 0x80300000 (31 x op, then
        // j loop + nop); op "addiu" (inline) or "lw" (handler, loads from 0x80300000). Interrupts masked.
        public double MicroRecLoop(int steps, string op)
        {
            uint basePhys = 0x300000;
            uint word = op == "lw" ? 0x8C030000u /* lw r3,0(zero)... */ : 0x24420001u;
            if (op == "lw") { for (int r = 8; r < 16; r++) Writeback64(r, 0xFFFFFFFF80300000UL); word = 0x8D030000u; /* lw r3,0(r8) */ }
            if (op == "sw") { for (int r = 8; r < 16; r++) Writeback64(r, 0xFFFFFFFF80300000UL); word = 0xAD030400u; /* sw r3,0x400(r8) */ }
            if (op == "mul.s") word = 0x46062102u; /* mul.s f4,f4,f6 */
            int body = op == "tight" ? 1 : 31;
            for (int i = 0; i < body; i++) PhysicalMemory.U32(basePhys + i * 4, word);
            PhysicalMemory.U32(basePhys + body * 4, 0x08000000u | ((0x80300000u >> 2) & 0x03FFFFFF));
            PhysicalMemory.U32(basePhys + body * 4 + 4, 0);
            Cop0State.Status.Write((Cop0State.Status.Read() & ~1u) | (op == "mul.s" ? 0x20000000u : 0));
            ClearBranchState();
            PC = unchecked((long)(int)0x80300000);
            int bs = int.TryParse(Environment.GetEnvironmentVariable("COR64_MICRO_BATCH"), out var mb) ? mb : 16;
            for (int i = 0; i < 20000; i++) RunSteps(bs);
            double best = double.MaxValue;
            var sw = new System.Diagnostics.Stopwatch();
            if (Environment.GetEnvironmentVariable("COR64_MICRO_PART") == "entry") {
                long pc0 = PC;
                for (int rep = 0; rep < 5; rep++) {
                    sw.Restart();
                    for (int i = 0; i < steps; i++) { PC = pc0; RecEntry(out _); }
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / steps);
                }
                return best;
            }
            if (Environment.GetEnvironmentVariable("COR64_MICRO_PART") == "exit") {
                for (int rep = 0; rep < 5; rep++) {
                    sw.Restart();
                    for (int i = 0; i < steps; i++) RecExit(PC, 0, 0, 16, 16, 0, 0, -1);
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / steps);
                }
                return best;
            }
            for (int rep = 0; rep < 5; rep++) {
                sw.Restart();
                for (int i = 0; i < steps / bs; i++) RunSteps(bs);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / steps);
            }
            return best;
        }

        // ---- per-step pieces called from the blocks ----

        // Applies piled-up ticks: tlb TLB Random ticks, count Count ticks.
        internal void RecFlush(int tlb, int count)
        {
            if (tlb > 0) TLBTick(tlb);
            if (count > 0) Cop0.MipsTimerTick((ulong)count);
        }

        // Before a handler: apply the piled-up ticks (this step's included), set PC and the fetch address.
        // [cer64plus] Pre + CallHandler + RecAfterHandler as one call (COR64_CPU_REC_SLIM=0: emitted inline as before);
        // the IL then zeroes its tick and quiet locals, as Pre does.
        private static readonly bool s_Slim = Environment.GetEnvironmentVariable("COR64_CPU_REC_SLIM") != "0";
        private static readonly MethodInfo s_RecSlowStep = typeof(Interpreter).GetMethod(nameof(RecSlowStep), BindingFlags.Instance | BindingFlags.NonPublic);
        internal bool RecSlowStep(long pc, int tlb, int count, DecodedInstruction[] insts, Action<DecodedInstruction>[] hs, int i, bool keepQuiet = false)
        {
            RecPre(pc, tlb, count);
            if (!keepQuiet) m_QuietValid = false; // [speed_cat5] ops with no interrupt-state effect keep the quiet window
            var d = insts[i];
            uint major = d.Inst.inst >> 26;
            bool cop1 = major == 0x11 || major == 0x31 || major == 0x35 || major == 0x39 || major == 0x3D;
            if (!cop1 || RecCop1Usable()) hs[i](d);
            return RecAfterHandler();
        }

        internal void RecPre(long pc, int tlb, int count)
        {
            if (tlb > 0) TLBTick(tlb);
            if (count > 0) Cop0.MipsTimerTick((ulong)count);
            PC = pc;
            m_LastFetchAddress = pc;
        }

        // Leaving at step index k (k steps of the block before it ran; PC = the step k address). Returns steps run.
        // basis: k - basis = steps run (it moves when a taken branch loops back inside the block); lapStart/lastIdx: the
        // step index the current lap began at and the step that jumped there (-1 before any loop-back).
        internal int RecExit(long startPc, int k, int basis, int tlb, int count, int cad, int lapStart, int lastIdx)
        {
            if (tlb > 0) TLBTick(tlb);
            if (count > 0) Cop0.MipsTimerTick((ulong)count);
            m_IntCheckCounter = cad;
            PC = startPc + k * 4;
            if (k > lapStart) m_LastFetchAddress = startPc + (k - 1) * 4;
            else if (lastIdx >= 0) m_LastFetchAddress = startPc + lastIdx * 4;
            return k - basis;
        }

        // Leaving after the delay slot committed the branch (PC already final).
        internal void RecDone(long lastPc, int tlb, int count, int cad)
        {
            if (tlb > 0) TLBTick(tlb);
            if (count > 0) Cop0.MipsTimerTick((ulong)count);
            m_IntCheckCounter = cad;
            m_LastFetchAddress = lastPc;
        }

        // Interrupt check in the prologue of step k: applies the piled-up ticks (TLB including this step, Count not), sets
        // PC; true = the step must be finished by the interpreter.
        internal bool RecCheckAt(long pc, int tlb, int count)
        {
            if (tlb > 0) TLBTick(tlb);
            if (count > 0) Cop0.MipsTimerTick((ulong)count);
            PC = pc;
            return RecCheck(pc);
        }

        // [cer64plus] The same check, returning -1 when the step must be finished by the interpreter, else the number of
        // further Count ticks for which a repeat of this check is guaranteed to find nothing new (while no handler runs):
        // MI, SR and Cause only change in handlers inside a block entry, so only the timer can make a later check differ.
        internal int RecCheckQ(long pc, int tlb, int count)
        {
            m_QuietValid = false;
            if (RecCheckAt(pc, tlb, count)) return -1;
            if (!s_RecQuiet) return 0;
            int q = Cop0.QuietTicks();
            // carried across block calls: valid while no handler or interpreter step runs and MI is unchanged
            if (s_RecQuietCarry) {
                m_QuietValid = true;
                m_QuietMi = Cop0.MiPending();
                m_QuietNoTimer = q == int.MaxValue;
                m_QuietCompare = Cop0.TimerCount; m_QuietQ = q; // [audio_fix] Count at the check + window (wrap-safe)
            }
            return q;
        }

        // Quiet window at a block entry (Count is flushed there): what RecCheckQ would return for a repeat of the last check.
        internal int RecQuietAtEntry()
        {
            if (!m_QuietValid || Cop0.MiPending() != m_QuietMi) return 0;
            if (m_QuietNoTimer) return int.MaxValue;
            uint elapsed = Cop0.TimerCount - m_QuietCompare;
            return elapsed >= (uint)m_QuietQ ? 0 : m_QuietQ - (int)elapsed;
        }
        [NonSerialized] private bool m_QuietValid;
        [NonSerialized] private bool m_DelayFromRec; // the last step run was a compiled block's (not the interpreter's)
        [NonSerialized] private bool m_QuietNoTimer;
        [NonSerialized] private uint m_QuietMi, m_QuietCompare;
        [NonSerialized] private int m_QuietQ;
        private static readonly bool s_RecQuietCarry = Environment.GetEnvironmentVariable("COR64_CPU_REC_QUIET_CARRY") != "0";
        private static readonly bool s_RecQuiet = Environment.GetEnvironmentVariable("COR64_CPU_REC_QUIET") != "0";

        // Interrupt check of a step's prologue (PC already set). True = the step must be finished by the interpreter:
        // servicing is pending (taken now or later), or the fetch would fail.
        internal bool RecCheck(long pc)
        {
            Cop0.CheckInterrupts(pc, BU_ExecuteBranchDelay);
            if (!Cop0.ServicingRequred && !Cop0.IsNonMaskablePending) return false;
            if (Cop0.ServicingRequred) CheckIfNeedServicing();
            return true;
        }

        // After a handler outside a delay slot: true = the step was finished generically, leave the block.
        internal bool RecAfterHandler()
        {
            if (m_ExceptionReturn || Cop0.ExceptionPending || Cop0.ServicingRequred || Cop0.IsNonMaskablePending || m_InjectedInst.HasValue
                || BU_ConditionalPending || BU_UnconditionalPending || BU_DelaySlotNext || BU_ExecuteBranchDelay || !FastFetchMode) {
                StepTail(false);
                return true;
            }
            PC += 4;
            return false;
        }

        // After a branch handler: the interpreter's tail; true = leave the block, false = run the delay slot next.
        internal bool RecAfterBranch(long pc)
        {
            StepTail(false);
            return !(BU_ExecuteBranchDelay && PC == pc + 4 && !m_InjectedInst.HasValue && FastFetchMode
                     && !Cop0.ServicingRequred && !Cop0.ExceptionPending);
        }

        // COP1 instructions fault with Coprocessor Unusable when SR.CU1 is clear (same test as StepBody).
        internal bool RecCop1Usable()
        {
            if (Cop0.Cop1Usable) return true;
            Cop0.RaiseCopUnusable(1);
            return false;
        }

        // [cpu_int_handlers] COR64_CPUIH_HILO: mflo/mfhi/mtlo/mthi/mult/multu/div/divu inline (IsOperation64 = true);
        // COR64_CPUIH_CACHE: cache inline (its handler is empty); COR64_CPUIH_BC1: bc1f/bc1t(l) as inline branches.
        private static readonly bool s_IhHiLo = Environment.GetEnvironmentVariable("COR64_CPUIH_HILO") != "0";
        private static readonly bool s_IhCache = Environment.GetEnvironmentVariable("COR64_CPUIH_CACHE") != "0";
        private static readonly bool s_IhBc1 = Environment.GetEnvironmentVariable("COR64_CPUIH_BC1") != "0";
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void RecMulDiv(ulong* g, ulong* hl, int rs, int rt, int kind)
        {
            uint a = (uint)g[rs], c = (uint)g[rt];
            switch (kind) {
                case 0: { ulong r = (ulong)((long)(int)a * (long)(int)c); hl[0] = SE32((uint)(r >> 32)); hl[1] = SE32((uint)r); break; }
                case 1: { ulong r = (ulong)a * (ulong)c; hl[0] = SE32((uint)(r >> 32)); hl[1] = SE32((uint)r); break; }
                case 2:
                    if (c == 0) { hl[0] = SE32(a); hl[1] = (int)a < 0 ? 1UL : ulong.MaxValue; }
                    else if (a == 0x80000000u && c == 0xFFFFFFFFu) { hl[0] = 0; hl[1] = SE32(a); } // the handler's ArithmeticException path
                    else { int q = (int)a / (int)c, r = (int)a % (int)c; hl[0] = SE32((uint)r); hl[1] = SE32((uint)q); }
                    break;
                default:
                    if (c == 0) { hl[0] = SE32(a); hl[1] = ulong.MaxValue; }
                    else { hl[0] = SE32(a % c); hl[1] = SE32(a / c); }
                    break;
            }
        }
        private static readonly MethodInfo s_RecMulDiv = typeof(Interpreter).GetMethod(nameof(RecMulDiv), BindingFlags.Static | BindingFlags.NonPublic);
        internal bool RecCu1() => Cop0.Cop1Usable;
        internal bool RecFpuCond() => State.FCR.Condition;
        private static readonly MethodInfo s_RecCu1 = typeof(Interpreter).GetMethod(nameof(RecCu1), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecFpuCond = typeof(Interpreter).GetMethod(nameof(RecFpuCond), BindingFlags.Instance | BindingFlags.NonPublic);

        // ---- inline integer ops: the handlers' arithmetic with IsOperation64 = true, IsReserved = false ----
        [MethodImpl(MethodImplOptions.AggressiveInlining)] private static ulong SE32(uint v) => (ulong)(long)(int)v;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong RecShift32(ulong value, int sa, bool left, bool unsigned)
        {
            bool isSigned = (value & 0x4000000000000000) == 0x4000000000000000;
            if (left) value <<= sa;
            else {
                if (unsigned) value &= 0xFFFFFFFFU;
                value >>= sa;
                value &= 0xFFFFFFFFU;
                if (!unsigned && isSigned) value |= ~(~0U >> sa);
            }
            return SE32((uint)value);
        }

        // Inline loads: kernel KSEG0/KSEG1 address (fixed mapping, the same as MemoryManager.VirtualToPhysical), aligned,
        // inside RDRAM: read the buffer exactly as the memory controller does (byte at a ^ 3, halfword at a ^ 2, doubleword
        // as two words). False = take the handler (TLB-mapped, unaligned, I/O, out of range).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool RecLoad(byte* ram, ulong* g, int rt, int rs, int imm, int kind)
        {
            uint a = (uint)((long)g[rs] + imm);
            if ((a >> 30) != 2) return false;
            uint p = a & 0x1FFFFFFF;
            int size = kind switch { 0 or 1 => 1, 2 or 3 => 2, 4 or 5 => 4, _ => 8 };
            if (p > 0x800000 - (uint)size || (p & (uint)(size - 1)) != 0) return false;
            if (cor64.Rdp.RdpGuard.Enabled && cor64.Rdp.RdpGuard.TableW[p >> cor64.Rdp.RdpGuard.PageShift] != 0) return false; // [rdp_readback] in-flight RDP output: the handler waits
            ulong v;
            switch (kind) {
                case 0: v = (ulong)(sbyte)ram[p ^ 3]; break;                    // lb
                case 1: v = ram[p ^ 3]; break;                                    // lbu
                case 2: v = (ulong)(short)*(ushort*)(ram + (p ^ 2)); break;       // lh
                case 3: v = *(ushort*)(ram + (p ^ 2)); break;                     // lhu
                case 4: v = (ulong)(int)*(uint*)(ram + p); break;                 // lw
                case 5: v = *(uint*)(ram + p); break;                             // lwu
                default: v = ((ulong)*(uint*)(ram + p) << 32) | *(uint*)(ram + p + 4); break; // ld
            }
            if (rt != 0) g[rt] = v;
            return true;
        }
        // Inline stores: same conditions as RecLoad; the plain-store path of the handler is a direct RDRAM write.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool RecStore(byte* ram, ulong* g, int rt, int rs, int imm, int size)
        {
            uint a = (uint)((long)g[rs] + imm);
            if ((a >> 30) != 2) return false;
            uint p = a & 0x1FFFFFFF;
            if (p > 0x800000 - (uint)size || (p & (uint)(size - 1)) != 0) return false;
            if (cor64.Rdp.RdpGuard.Enabled && cor64.Rdp.RdpGuard.TableA[p >> cor64.Rdp.RdpGuard.PageShift] != 0) return false; // [rdp_readback]
            ulong v = g[rt];
            switch (size) {
                case 1: ram[p ^ 3] = (byte)v; break;
                case 2: *(ushort*)(ram + (p ^ 2)) = (ushort)v; break;
                case 4: *(uint*)(ram + p) = (uint)v; break;
                default: *(uint*)(ram + p) = (uint)(v >> 32); *(uint*)(ram + p + 4) = (uint)v; break;
            }
            return true;
        }
        // Inline FPU loads/stores (lwc1/ldc1/swc1/sdc1): same RDRAM conditions as RecLoad, plus CU1 set (otherwise the
        // handler path raises Coprocessor Unusable). The FPR access is the handler's own (FR-mode aware).
        internal unsafe bool RecFpuMem(int ft, int rs, int imm, int size, bool store)
        {
            if (!Cop0.Cop1Usable) return false;
            ulong* g = (ulong*)State.GetGprRef();
            uint a = (uint)((long)g[rs] + imm);
            if ((a >> 30) != 2) return false;
            uint p = a & 0x1FFFFFFF;
            if (p > 0x800000 - (uint)size || (p & (uint)(size - 1)) != 0) return false;
            if (cor64.Rdp.RdpGuard.Enabled && (store ? cor64.Rdp.RdpGuard.TableA : cor64.Rdp.RdpGuard.TableW)[p >> cor64.Rdp.RdpGuard.PageShift] != 0) return false; // [rdp_readback]
            byte* ram = (byte*)RdramWords;
            var fpr = StateR4000I.FPR;
            if (store) {
                if (size == 4) *(uint*)(ram + p) = fpr.ReadScalarWord(ft);
                else { ulong v = fpr.ReadScalarDword(ft); *(uint*)(ram + p) = (uint)(v >> 32); *(uint*)(ram + p + 4) = (uint)v; }
            }
            else {
                if (size == 4) fpr.WriteScalarWord(ft, *(uint*)(ram + p));
                else fpr.WriteScalarDword(ft, ((ulong)*(uint*)(ram + p) << 32) | *(uint*)(ram + p + 4));
            }
            return true;
        }
        // Inline mtc1/mfc1: TransferReg's 32-bit Cp1 paths.
        internal bool RecXfer1(int gpr, int fs, bool toFpu)
        {
            if (!Cop0.Cop1Usable || !EnableCp1) return false;
            if (toFpu) WriteFPR_W(fs, (uint)ReadGPR64(gpr));
            else { ulong v = (ulong)(long)(int)ReadFPR_W(fs); Writeback64(gpr, IsOperation64 ? v : (uint)v); }
            return true;
        }
        private static readonly MethodInfo s_RecXfer1 = typeof(Interpreter).GetMethod(nameof(RecXfer1), BindingFlags.Instance | BindingFlags.NonPublic);
        // Inline FPU arithmetic (add/sub/mul/div.fmt, S and D): the handlers' own expressions, without the handler-call
        // bookkeeping (tick flush, quiet window reset, after-handler checks). CU1 clear -> the handler path raises.
        internal bool RecFpuArith(int op, bool dbl, int fd, int fs, int ft)
        {
            if (!Cop0.Cop1Usable) return false;
            if (dbl) {
                double a = ReadFPR_D(fs), c = ReadFPR_D(ft);
                WriteFPR_D(fd, op == 0 ? a + c : op == 1 ? a - c : op == 2 ? a * c : a / c);
            }
            else {
                float a = ReadFPR_S(fs), c = ReadFPR_S(ft);
                WriteFPR_S(fd, op == 0 ? a + c : op == 1 ? a - c : op == 2 ? a * c : a / c);
            }
            return true;
        }
        // [cpu_fpu_path] ceiling measurement only (COR64_CPUF_CEIL=1): one extra FPU access chain per inlined FPU op.
        [NonSerialized] private double m_CpufCeilSink;
        internal void RecFpuCeil(int fs, int ft) { if (Cop0.Cop1Usable) m_CpufCeilSink += ReadFPR_D(fs) + ReadFPR_D(ft); }
        private static readonly MethodInfo s_RecFpuCeil = typeof(Interpreter).GetMethod(nameof(RecFpuCeil), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly bool s_CpufCeil = Environment.GetEnvironmentVariable("COR64_CPUF_CEIL") == "1";
        // [cpu_fpu_path] COR64_CPUF_RAW: compiled FPU arithmetic and lwc1/ldc1/swc1/sdc1 address the FPR file directly
        // (fixed native buffer, index resolved at compile time for the FR mode seen then). Each op tests Status
        // (CU1 set, FR equal to the compiled mode) with one load; otherwise it takes the old path.
        [NonSerialized] private StatusRegister m_CpufSr;
        private static readonly bool s_CpufRaw = Environment.GetEnvironmentVariable("COR64_CPUF_RAW") != "0";
        private static readonly FieldInfo s_CpufSrField = typeof(Interpreter).GetField(nameof(m_CpufSr), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_SrValueField = typeof(StatusRegister).GetField("m_Value", BindingFlags.Instance | BindingFlags.NonPublic);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool RecFpuMemRaw(byte* ram, ulong* g, byte* f, int rs, int imm, int size, bool store)
        {
            uint a = (uint)((long)g[rs] + imm);
            if ((a >> 30) != 2) return false;
            uint p = a & 0x1FFFFFFF;
            if (p > 0x800000 - (uint)size || (p & (uint)(size - 1)) != 0) return false;
            if (cor64.Rdp.RdpGuard.Enabled && (store ? cor64.Rdp.RdpGuard.TableA : cor64.Rdp.RdpGuard.TableW)[p >> cor64.Rdp.RdpGuard.PageShift] != 0) return false; // [rdp_readback]
            if (store) {
                if (size == 4) *(uint*)(ram + p) = *(uint*)f;
                else { ulong v = *(ulong*)f; *(uint*)(ram + p) = (uint)(v >> 32); *(uint*)(ram + p + 4) = (uint)v; }
            }
            else {
                if (size == 4) *(uint*)f = *(uint*)(ram + p);
                else *(ulong*)f = ((ulong)*(uint*)(ram + p) << 32) | *(uint*)(ram + p + 4);
            }
            return true;
        }
        private static readonly MethodInfo s_RecFpuMemRaw = typeof(Interpreter).GetMethod(nameof(RecFpuMemRaw), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecFpuArith = typeof(Interpreter).GetMethod(nameof(RecFpuArith), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecFpuMem = typeof(Interpreter).GetMethod(nameof(RecFpuMem), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly bool s_RecInlineFpuArith = Environment.GetEnvironmentVariable("COR64_CPU_REC_FPUARITH") != "0";
        private static readonly bool s_RecInlineFpuMem = Environment.GetEnvironmentVariable("COR64_CPU_REC_FPUMEM") != "0";
        private static readonly MethodInfo s_RecStore = typeof(Interpreter).GetMethod(nameof(RecStore), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecLoad = typeof(Interpreter).GetMethod(nameof(RecLoad), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly bool s_RecInlineLoad = Environment.GetEnvironmentVariable("COR64_CPU_REC_LD") != "0";

        private static readonly MethodInfo s_RecShift32 = typeof(Interpreter).GetMethod(nameof(RecShift32), BindingFlags.Static | BindingFlags.NonPublic);
        private static MethodInfo Setter(string name) => typeof(InterpreterBaseR4300I).GetProperty(name).GetSetMethod();
        private static readonly MethodInfo s_SetCond = Setter(nameof(BU_ConditionalPending));
        private static readonly MethodInfo s_SetUncond = Setter(nameof(BU_UnconditionalPending));
        private static readonly MethodInfo s_SetExecBD = Setter(nameof(BU_ExecuteBranchDelay));
        private static readonly MethodInfo s_SetTarget = Setter(nameof(BU_TargetAddress));
        private static readonly MethodInfo s_GetTarget = typeof(InterpreterBaseR4300I).GetProperty(nameof(BU_TargetAddress)).GetGetMethod();
        private static readonly MethodInfo s_RecDelayTaken = typeof(Interpreter).GetMethod(nameof(RecDelayTaken), BindingFlags.Instance | BindingFlags.NonPublic);
        internal bool RecDelayTaken() => BU_ConditionalPending || BU_UnconditionalPending;
        private static MethodInfo M(string name) => typeof(Interpreter).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecPre = M(nameof(RecPre));
        private static readonly MethodInfo s_RecExit = M(nameof(RecExit));
        private static readonly MethodInfo s_RecDone = M(nameof(RecDone));
        private static readonly MethodInfo s_RecCheckAt = M(nameof(RecCheckAt));
        private static readonly MethodInfo s_RecCheckQ = M(nameof(RecCheckQ));
        private static readonly MethodInfo s_RecQuietAtEntry = M(nameof(RecQuietAtEntry));
        private static readonly FieldInfo s_QuietValidField = typeof(Interpreter).GetField(nameof(m_QuietValid), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecFlush = typeof(Interpreter).GetMethod(nameof(RecFlush), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecCheck = typeof(Interpreter).GetMethod(nameof(RecCheck), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_IntCheckField = typeof(Interpreter).GetField(nameof(m_IntCheckCounter), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_LastFetchField = typeof(Interpreter).GetField(nameof(m_LastFetchAddress), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecAfterHandler = typeof(Interpreter).GetMethod(nameof(RecAfterHandler), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecAfterBranch = typeof(Interpreter).GetMethod(nameof(RecAfterBranch), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo s_RecCop1Usable = typeof(Interpreter).GetMethod(nameof(RecCop1Usable), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo s_ExecAfterPrologue = typeof(Interpreter).GetMethod(nameof(ExecuteAfterPrologue), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo s_StepTail = typeof(Interpreter).GetMethod(nameof(StepTail), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_CpuPc = typeof(BaseInterpreter).GetField("m_Pc", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo s_InvokeH = typeof(Action<DecodedInstruction>).GetMethod("Invoke");

        private unsafe CpuRecFn RecCompile(CpuRecBlock b)
        {
            if (s_X64) {
                var nat = X64Compile(b);
                if (nat != IntPtr.Zero) { b.Native = nat; System.Threading.Interlocked.Increment(ref CpuRecCompiled); return s_X64Sentinel; }
            }
            int n = b.Insts.Length;
            var dm = new DynamicMethod("cpurec_" + ((uint)b.StartPc).ToString("X8"), typeof(int),
                CallStubArg.Params(typeof(Interpreter), typeof(DecodedInstruction[]), typeof(int), typeof(Action<DecodedInstruction>[]), typeof(int)),
                typeof(Interpreter), skipVisibility: true);
            dm.InitLocals = false;
            var il = dm.GetILGenerator();
            long gpr = (long)State.GetGprRef();
            long hiA = (long)State.GetHiRef();
            var fpuSt = StateR4000I.FPR;
            bool rawOn = s_CpufRaw && s_WatchAddr < 0 && ReferenceEquals(fpuSt.StatusReg, Cop0.StatusReg);
            if (rawOn) m_CpufSr = fpuSt.StatusReg;
            long fpr = (long)fpuSt.RawPointer;
            const uint srMask = (uint)(StatusRegister.StatusFlags.UsableCop1 | StatusRegister.StatusFlags.AdditonalFPU);
            bool frc = rawOn && fpuSt.FullMode;
            uint srWant = (uint)StatusRegister.StatusFlags.UsableCop1 | (frc ? (uint)StatusRegister.StatusFlags.AdditonalFPU : 0u);

            void GAddr(int r) { il.Emit(OpCodes.Ldc_I8, gpr + r * 8L); il.Emit(OpCodes.Conv_U); }
            void Ld(int r) { if (r == 0) { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_U8); } else { GAddr(r); il.Emit(OpCodes.Ldind_I8); } }
            void Ld32(int r) { Ld(r); il.Emit(OpCodes.Conv_U4); }
            void Wr(int r, Action v) { if (r == 0) return; GAddr(r); v(); il.Emit(OpCodes.Stind_I8); }
            void SE() { il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Conv_I8); } // (ulong)(long)(int)(uint) of the int32 on the stack
            void SetPc(long pc) { CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I8, pc); il.Emit(OpCodes.Stfld, s_CpuPc); }
            var basis = il.DeclareLocal(typeof(int));   // k - basis = steps run (moves on a loop-back)
            var lapStart = il.DeclareLocal(typeof(int));
            var lastIdx = il.DeclareLocal(typeof(int));
            { var go = il.DefineLabel(); CallStubArg.Ld(il, 4); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Bge, go); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret); il.MarkLabel(go); } // start = -1: JIT warm-up
            CallStubArg.Ld(il, 4); il.Emit(OpCodes.Stloc, basis);
            CallStubArg.Ld(il, 4); il.Emit(OpCodes.Stloc, lapStart);
            il.Emit(OpCodes.Ldc_I4_M1); il.Emit(OpCodes.Stloc, lastIdx);
            void Ret(int k) { il.Emit(OpCodes.Ldc_I4, k); il.Emit(OpCodes.Ldloc, basis); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Ret); }

            bool EmitInline(in DecodedInstruction d)
            {
                int rs = d.Source, rt = d.Target, rd = d.Destination, sa = d.ShiftAmount;
                uint imm = d.Immediate;
                switch (d.Op.Op) {
                    case "addu": Wr(rd, () => { Ld32(rs); Ld32(rt); il.Emit(OpCodes.Add); SE(); }); return true;
                    case "addiu": Wr(rt, () => { Ld32(rs); il.Emit(OpCodes.Ldc_I4, (int)(short)imm); il.Emit(OpCodes.Add); SE(); }); return true;
                    case "subu": Wr(rd, () => { Ld32(rs); Ld32(rt); il.Emit(OpCodes.Sub); SE(); }); return true;
                    case "and": Wr(rd, () => { Ld(rs); Ld(rt); il.Emit(OpCodes.And); }); return true;
                    case "or": Wr(rd, () => { Ld(rs); Ld(rt); il.Emit(OpCodes.Or); }); return true;
                    case "xor": Wr(rd, () => { Ld(rs); Ld(rt); il.Emit(OpCodes.Xor); }); return true;
                    case "nor": Wr(rd, () => { Ld(rs); Ld(rt); il.Emit(OpCodes.Or); il.Emit(OpCodes.Not); }); return true;
                    case "andi": Wr(rt, () => { Ld(rs); il.Emit(OpCodes.Ldc_I8, (long)imm); il.Emit(OpCodes.And); }); return true;
                    case "ori": Wr(rt, () => { Ld(rs); il.Emit(OpCodes.Ldc_I8, (long)imm); il.Emit(OpCodes.Or); }); return true;
                    case "xori": Wr(rt, () => { Ld(rs); il.Emit(OpCodes.Ldc_I8, (long)imm); il.Emit(OpCodes.Xor); }); return true;
                    case "lui": Wr(rt, () => il.Emit(OpCodes.Ldc_I8, (long)(int)(imm << 16))); return true;
                    case "slt": Wr(rd, () => { Ld(rs); Ld(rt); il.Emit(OpCodes.Clt); il.Emit(OpCodes.Conv_U8); }); return true;
                    case "sltu": Wr(rd, () => { Ld(rs); Ld(rt); il.Emit(OpCodes.Clt_Un); il.Emit(OpCodes.Conv_U8); }); return true;
                    case "slti": Wr(rt, () => { Ld(rs); il.Emit(OpCodes.Ldc_I8, (long)(short)imm); il.Emit(OpCodes.Clt); il.Emit(OpCodes.Conv_U8); }); return true;
                    case "sltiu": Wr(rt, () => { Ld(rs); il.Emit(OpCodes.Ldc_I8, (long)(short)imm); il.Emit(OpCodes.Clt_Un); il.Emit(OpCodes.Conv_U8); }); return true;
                    case "daddu": Wr(rd, () => { Ld(rs); Ld(rt); il.Emit(OpCodes.Add); }); return true;
                    case "daddiu": Wr(rt, () => { Ld(rs); il.Emit(OpCodes.Ldc_I8, (long)(short)imm); il.Emit(OpCodes.Add); }); return true;
                    case "sll": case "srl": case "sra": {
                        bool left = d.Op.Op == "sll", uns = d.Op.Op == "srl";
                        Wr(rd, () => { Ld(rt); il.Emit(OpCodes.Ldc_I4, sa); il.Emit(left ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); il.Emit(uns ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, s_RecShift32); });
                        return true;
                    }
                    case "sllv": case "srlv": case "srav": {
                        bool left = d.Op.Op == "sllv", uns = d.Op.Op == "srlv";
                        Wr(rd, () => { Ld(rt); Ld32(rs); il.Emit(OpCodes.Ldc_I4, s_ShvMask); il.Emit(OpCodes.And); il.Emit(left ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); il.Emit(uns ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, s_RecShift32); });
                        return true;
                    }
                    case "cache": return s_IhCache;
                    case "mflo": case "mfhi": case "mtlo": case "mthi": {
                        if (!s_IhHiLo) return false;
                        RegTransferGprHelper(d, out int gs, out int gt);
                        long ha = hiA + (d.Op.Op[2] == 'l' ? 8 : 0);
                        if (d.Op.Op[1] == 'f') Wr(gt, () => { il.Emit(OpCodes.Ldc_I8, ha); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldind_I8); });
                        else { il.Emit(OpCodes.Ldc_I8, ha); il.Emit(OpCodes.Conv_U); Ld(gs); il.Emit(OpCodes.Stind_I8); }
                        return true;
                    }
                    case "mult": case "multu": case "div": case "divu": {
                        if (!s_IhHiLo) return false;
                        int kind = d.Op.Op == "mult" ? 0 : d.Op.Op == "multu" ? 1 : d.Op.Op == "div" ? 2 : 3;
                        il.Emit(OpCodes.Ldc_I8, gpr); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldc_I8, hiA); il.Emit(OpCodes.Conv_U);
                        il.Emit(OpCodes.Ldc_I4, rs); il.Emit(OpCodes.Ldc_I4, rt); il.Emit(OpCodes.Ldc_I4, kind);
                        il.Emit(OpCodes.Call, s_RecMulDiv);
                        return true;
                    }
                }
                return false;
            }

            void CallHandler(int i)
            {
                var d = b.Insts[i];
                var h = b.Handlers[i];
                uint major = d.Inst.inst >> 26;
                bool cop1 = major == 0x11 || major == 0x31 || major == 0x35 || major == 0x39 || major == 0x3D;
                var skip = il.DefineLabel();
                if (s_OpMix) { il.Emit(OpCodes.Ldstr, d.Op.Op + (LoadKind(d) >= 0 || StoreSize(d) > 0 ? "(slow)" : "")); il.Emit(OpCodes.Call, s_OpMixCount); }
                if (cop1) { CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_RecCop1Usable); il.Emit(OpCodes.Brfalse, skip); }
                if (ReferenceEquals(h.Target, this) && !h.Method.IsStatic && h.Method.DeclaringType.IsAssignableFrom(typeof(Interpreter))) {
                    CallStubArg.Ld(il, 0); CallStubArg.Ld(il, 1); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem, typeof(DecodedInstruction));
                    il.Emit(OpCodes.Call, h.Method);
                }
                else {
                    CallStubArg.Ld(il, 3); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem_Ref);
                    CallStubArg.Ld(il, 1); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem, typeof(DecodedInstruction));
                    il.Emit(OpCodes.Callvirt, s_InvokeH);
                }
                if (s_CvtCeil && CvtCeilOp(d) && ReferenceEquals(h.Target, this) && !h.Method.IsStatic) {
                    CallStubArg.Ld(il, 0); CallStubArg.Ld(il, 1); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem, typeof(DecodedInstruction));
                    il.Emit(OpCodes.Call, h.Method);
                }
                il.MarkLabel(skip);
            }

            var cad = il.DeclareLocal(typeof(int));   // interrupt-check cadence (m_IntCheckCounter)
            var pt = il.DeclareLocal(typeof(int));    // TLB ticks not yet applied
            var pcn = il.DeclareLocal(typeof(int));   // Count ticks not yet applied
            var k = il.DeclareLocal(typeof(int));     // step index for the shared stubs
            var end = il.DeclareLocal(typeof(int));   // step index where the budget runs out
            var tk = il.DeclareLocal(typeof(bool));   // inline branch: taken
            var tgt = il.DeclareLocal(typeof(long));  // inline branch: target
            var quiet = il.DeclareLocal(typeof(int)); // Count ticks (pcn) below which a check repeats the last one exactly
            CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldfld, s_IntCheckField); il.Emit(OpCodes.Stloc, cad);
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, pt);
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, pcn);
            if (s_RecQuietCarry) { CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_RecQuietAtEntry); il.Emit(OpCodes.Stloc, quiet); }
            else { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, quiet); }
            // end = start + max (the block's own end needs no check: its last step leaves through the stubs)
            CallStubArg.Ld(il, 4); CallStubArg.Ld(il, 2); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, end);

            var exitStub = il.DefineLabel();      // k -> RecExit
            var budgetStub = il.DefineLabel();    // k = end -> exitStub
            var checkStub = il.DefineLabel();     // k = step whose prologue hit the cadence
            var leaveStub = il.DefineLabel();     // k = steps index after a step finished by the interpreter
            var checkThunks = new List<(Label, int)>();
            var leaveThunks = new List<(Label, int)>();
            var resume = new Label[n];
            for (int i = 0; i < n; i++) resume[i] = il.DefineLabel();

            void Inc(LocalBuilder l) { il.Emit(OpCodes.Ldloc, l); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, l); }
            void Zero(LocalBuilder l) { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, l); }
            void Pre(long pc)
            {
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I8, pc); il.Emit(OpCodes.Ldloc, pt); il.Emit(OpCodes.Ldloc, pcn); il.Emit(OpCodes.Call, s_RecPre);
                Zero(pt); Zero(pcn); Zero(quiet);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stfld, s_QuietValidField);
            }
            void Leave(int kk) { var t = il.DefineLabel(); leaveThunks.Add((t, kk)); il.Emit(OpCodes.Br, t); }
            void LeaveIf(int kk) { var t = il.DefineLabel(); leaveThunks.Add((t, kk)); il.Emit(OpCodes.Brtrue, t); }
            void ExitAt(int kk) { il.Emit(OpCodes.Ldc_I4, kk); il.Emit(OpCodes.Stloc, k); il.Emit(OpCodes.Br, exitStub); }
            void Done(long lastPc)
            {
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I8, lastPc); il.Emit(OpCodes.Ldloc, pt); il.Emit(OpCodes.Ldloc, pcn); il.Emit(OpCodes.Ldloc, cad); il.Emit(OpCodes.Call, s_RecDone);
            }
            void SetBool(MethodInfo setter, bool v) { CallStubArg.Ld(il, 0); il.Emit(v ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, setter); }

            int LoadKind(in DecodedInstruction d)
            {
                switch (d.Op.Op) { case "lb": return 0; case "lbu": return 1; case "lh": return 2; case "lhu": return 3; case "lw": return 4; case "lwu": return 5; case "ld": return 6; default: return -1; }
            }

            int StoreSize(in DecodedInstruction d)
            {
                switch (d.Op.Op) { case "sb": return 1; case "sh": return 2; case "sw": return 4; case "sd": return 8; default: return -1; }
            }

            // Step index a taken branch at index bi lands on, when it can continue inside this block: constant target,
            // block PC in zero-extended form (a taken branch leaves PC = (uint)target), not a delay slot.
            int LoopTarget(int bi)
            {
                if (!s_RecLoops || b.StartPc != (long)(uint)b.StartPc) return -1;
                var bd = b.Insts[bi];
                string op = bd.Op.Op;
                if (op == "jr" || op == "jalr") return -1;
                long pcb = b.StartPc + bi * 4;
                long target = op == "j" || op == "jal" ? ((long)(bd.Inst.target << 2) | (pcb & 0xF0000000)) : pcb + ((long)(short)bd.Immediate << 2) + 4;
                target = (long)(uint)target;
                long off = target - b.StartPc;
                if (off < 0 || (off & 3) != 0 || off / 4 >= n) return -1;
                int t = (int)(off / 4);
                if (t > 0 && IsCpuBranch(b.Insts[t - 1])) return -1;
                return t;
            }

            bool CanInlineBranch(int i)
            {
                if (i + 1 >= n || IsCpuBranch(b.Insts[i + 1])) return false;
                var d = b.Insts[i];
                switch (d.Op.Op) {
                    case "j": case "jal": case "jr": case "jalr": return true;
                    case "beq": case "bne": case "blez": case "bgtz": case "bltz": case "bgez":
                    case "beql": case "bnel": case "blezl": case "bgtzl": case "bltzl": case "bgezl":
                    case "bltzal": case "bgezal": case "bltzall": case "bgezall":
                        return d.Op.XferTarget != RegBoundType.Cp1;
                    case "bc1f": case "bc1t": case "bc1fl": case "bc1tl": return s_IhBc1;
                    default: return false;
                }
            }

            // The branch handler plus the step tail, as stores: the state the delay slot step starts from.
            void EmitBranch(int i, long pc)
            {
                var d = b.Insts[i];
                string op = d.Op.Op;
                bool isJump = op == "j" || op == "jal" || op == "jr" || op == "jalr";
                bool likely = !isJump && d.IsLikely();
                bool link = op == "jal" || op == "jalr" || (!isJump && op.Contains("al"));
                if (isJump) {
                    il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, tk);
                    if (op == "jr" || op == "jalr") { Ld(d.Source); il.Emit(OpCodes.Ldc_I8, 0xFFFFFFFFL); il.Emit(OpCodes.And); }
                    else il.Emit(OpCodes.Ldc_I8, (long)(d.Inst.target << 2) | (pc & 0xF0000000));
                    il.Emit(OpCodes.Stloc, tgt);
                    if (link) {
                        int lr = op == "jalr" && d.Destination != 0 ? d.Destination : 31;
                        Wr(lr, () => il.Emit(OpCodes.Ldc_I8, LinkConst(pc + 8)));
                    }
                    CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldloc, tgt); il.Emit(OpCodes.Call, s_SetTarget);
                    SetBool(s_SetUncond, true);
                }
                else {
                    if (d.Op.XferTarget == RegBoundType.Cp1) {
                        // CU1 clear: the handler path with the handler skipped (RecCop1Usable raises), then leave
                        var usable = il.DefineLabel();
                        CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_RecCu1); il.Emit(OpCodes.Brtrue, usable);
                        Pre(pc);
                        CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_RecCop1Usable); il.Emit(OpCodes.Pop);
                        CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I8, pc); il.Emit(OpCodes.Call, s_RecAfterBranch); il.Emit(OpCodes.Pop);
                        Leave(i + 1);
                        il.MarkLabel(usable);
                    }
                    switch (d.Op.XferTarget == RegBoundType.Cp1 ? (ArithmeticOp)(-1) : d.Op.ArithmeticType) {
                        case (ArithmeticOp)(-1):
                            CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_RecFpuCond);
                            if (d.Op.ArithmeticType == ArithmeticOp.FALSE) { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); }
                            else if (d.Op.ArithmeticType != ArithmeticOp.TRUE) throw new InvalidOperationException("bc1 condition");
                            break;
                        case ArithmeticOp.EQUAL: Ld(d.Source); Ld(d.Target); il.Emit(OpCodes.Ceq); break;
                        case ArithmeticOp.NOT_EQUAL: Ld(d.Source); Ld(d.Target); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); break;
                        case ArithmeticOp.GREATER_THAN: Ld(d.Source); il.Emit(OpCodes.Ldc_I8, 0L); il.Emit(OpCodes.Cgt); break;
                        case ArithmeticOp.LESS_THAN: Ld(d.Source); il.Emit(OpCodes.Ldc_I8, 0L); il.Emit(OpCodes.Clt); break;
                        case ArithmeticOp.GREATER_THAN_OR_EQUAL: Ld(d.Source); il.Emit(OpCodes.Ldc_I8, 0L); il.Emit(OpCodes.Clt); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); break;
                        case ArithmeticOp.LESS_THAN_OR_EQUAL: Ld(d.Source); il.Emit(OpCodes.Ldc_I8, 0L); il.Emit(OpCodes.Cgt); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); break;
                        default: throw new InvalidOperationException("branch condition");
                    }
                    il.Emit(OpCodes.Stloc, tk);
                    long target = pc + ((long)(short)d.Immediate << 2) + 4;
                    il.Emit(OpCodes.Ldc_I8, target); il.Emit(OpCodes.Stloc, tgt);
                    var nt = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, tk); il.Emit(OpCodes.Brfalse, nt);       // BU_TargetAddress only written when taken
                    CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I8, target); il.Emit(OpCodes.Call, s_SetTarget);
                    il.MarkLabel(nt);
                    if (link) Wr(31, () => il.Emit(OpCodes.Ldc_I8, LinkConst(pc + 8)));
                    CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldloc, tk); il.Emit(OpCodes.Call, s_SetCond);
                }
                if (likely) {
                    // likely and not taken: the delay slot is nullified, PC skips it, the block ends
                    var taken = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, tk); il.Emit(OpCodes.Brtrue, taken);
                    SetPc(pc + 8); Done(pc);
                    Ret(i + 1);
                    il.MarkLabel(taken);
                }
                SetBool(s_SetExecBD, true);
                SetPc(pc + 4);
            }

            void EmitCommit(long pcDelay)
            {
                var nt = il.DefineLabel(); var done = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, tk); il.Emit(OpCodes.Brfalse, nt);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldloc, tgt); il.Emit(OpCodes.Conv_U4); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Stfld, s_CpuPc);
                il.Emit(OpCodes.Br, done);
                il.MarkLabel(nt);
                SetPc(pcDelay + 4);
                il.MarkLabel(done);
                SetBool(s_SetExecBD, false);
                SetBool(s_SetCond, false);
                SetBool(s_SetUncond, false);
            }

            // [cer64plus] the inline fast paths of a load/store/FPU step; each branches to loaded on success, falls
            // through (no state touched) when the handler must run. True = something was emitted.
            bool EmitFast(DecodedInstruction d, Label loaded, bool arith)
            {
                int il0 = il.ILOffset;
            int lk = s_RecInlineLoad ? LoadKind(d) : -1;
            int sk = s_RecInlineLoad && s_WatchAddr < 0 ? StoreSize(d) : -1;
            string fop = d.Op.Op;
            int fsz = !s_RecInlineFpuMem || s_WatchAddr >= 0 ? 0 : fop == "lwc1" || fop == "swc1" ? 4 : fop == "ldc1" || fop == "sdc1" ? 8 : 0;
            if (fsz > 0) {
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I4, d.FloatTarget); il.Emit(OpCodes.Ldc_I4, d.Source);
                il.Emit(OpCodes.Ldc_I4, (int)(short)d.Immediate); il.Emit(OpCodes.Ldc_I4, fsz); il.Emit(fop[0] == 's' ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Call, s_RecFpuMem);
                il.Emit(OpCodes.Brtrue, loaded);
            }
            int fa = !s_RecInlineFpuArith || (d.Format != FpuValueType.FSingle && d.Format != FpuValueType.FDouble) ? -1
                : fop == "add.fmt" ? 0 : fop == "sub.fmt" ? 1 : fop == "mul.fmt" ? 2 : fop == "div.fmt" ? 3 : -1;
            if (fa >= 0 && arith) {
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I4, fa); il.Emit(d.Format == FpuValueType.FDouble ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldc_I4, d.FloatDest); il.Emit(OpCodes.Ldc_I4, d.FloatSource); il.Emit(OpCodes.Ldc_I4, d.FloatTarget);
                il.Emit(OpCodes.Call, s_RecFpuArith);
                il.Emit(OpCodes.Brtrue, loaded);
            }
            if (s_RecInlineFpuMem && (fop == "mtc1" || fop == "mfc1")) {
                RegTransferGprHelper(d, out int gs, out int gt);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I4, fop == "mtc1" ? gs : gt); il.Emit(OpCodes.Ldc_I4, d.FloatSource);
                il.Emit(fop == "mtc1" ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Call, s_RecXfer1);
                il.Emit(OpCodes.Brtrue, loaded);
            }
            if (sk > 0) {
                il.Emit(OpCodes.Ldc_I8, (long)RdramWords); il.Emit(OpCodes.Conv_U);
                il.Emit(OpCodes.Ldc_I8, gpr); il.Emit(OpCodes.Conv_U);
                il.Emit(OpCodes.Ldc_I4, d.Target); il.Emit(OpCodes.Ldc_I4, d.Source); il.Emit(OpCodes.Ldc_I4, (int)(short)d.Immediate); il.Emit(OpCodes.Ldc_I4, sk);
                il.Emit(OpCodes.Call, s_RecStore);
                il.Emit(OpCodes.Brtrue, loaded);
            }
            if (lk >= 0) {
                il.Emit(OpCodes.Ldc_I8, (long)RdramWords); il.Emit(OpCodes.Conv_U);
                il.Emit(OpCodes.Ldc_I8, gpr); il.Emit(OpCodes.Conv_U);
                il.Emit(OpCodes.Ldc_I4, d.Target); il.Emit(OpCodes.Ldc_I4, d.Source); il.Emit(OpCodes.Ldc_I4, (int)(short)d.Immediate); il.Emit(OpCodes.Ldc_I4, lk);
                il.Emit(OpCodes.Call, s_RecLoad);
                il.Emit(OpCodes.Brtrue, loaded);
            }
                return il.ILOffset != il0;
            }

            var stepLabels = new Label[n];
            for (int i = 0; i < n; i++) stepLabels[i] = il.DefineLabel();
            // delay slots (the same walk as the emitter below): entered with the branch's state in the BU_ fields
            var delayEntry = new bool[n];
            if (s_RecDelayEntry) {
                for (int i = 0; i + 1 < n; i++) {
                    if (!IsCpuBranch(b.Insts[i])) continue;
                    delayEntry[i + 1] = true;
                    i++;
                }
            }
            var entryLabels = new Label[n];
            for (int i = 0; i < n; i++) entryLabels[i] = delayEntry[i] ? il.DefineLabel() : stepLabels[i];
            CallStubArg.Ld(il, 4);
            il.Emit(OpCodes.Switch, entryLabels);
            Ret(0); // not reached: entries are always inside the block
            for (int i = 0; i < n; i++) {
                if (!delayEntry[i]) continue;
                il.MarkLabel(entryLabels[i]);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_RecDelayTaken); il.Emit(OpCodes.Stloc, tk);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_GetTarget); il.Emit(OpCodes.Stloc, tgt);
                il.Emit(OpCodes.Br, stepLabels[i]);
            }

            bool inDelay = false, inlineBranch = false;
            int minLoop = int.MaxValue;
            for (int i = 0; i < n; i++) {
                var d = b.Insts[i];
                long pc = b.StartPc + i * 4; // same form (sign- or zero-extended) as the interpreter's PC += 4
                il.MarkLabel(stepLabels[i]);
                // budget
                il.Emit(OpCodes.Ldloc, end); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Beq, budgetStub);
                // prologue: TLB tick, cadence (cold thunk to the shared check)
                Inc(pt);
                Inc(cad);
                if (s_Slim) { il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Stloc, k); il.Emit(OpCodes.Ldloc, cad); il.Emit(OpCodes.Ldc_I4, s_IntCheckEvery); il.Emit(OpCodes.Bge, checkStub); }
                else { var t = il.DefineLabel(); checkThunks.Add((t, i)); il.Emit(OpCodes.Ldloc, cad); il.Emit(OpCodes.Ldc_I4, s_IntCheckEvery); il.Emit(OpCodes.Bge, t); }
                il.MarkLabel(resume[i]);
                // body: Count tick
                Inc(pcn);

                if (inDelay) {
                    // [cer64plus] COR64_CPUS_DELAYFAST: a load/store/FPU transfer in the delay slot of an inline branch takes its
                    // fast path and the block goes on (commit, loop-back, superblock) instead of a handler call and a block exit
                    bool fastD = false; var slowD = il.DefineLabel();
                    void DelaySlow()
                    {
                        if (s_StepTiming) { il.Emit(OpCodes.Ldstr, (inlineBranch ? "inl:" : "hnd:") + d.Op.Op); il.Emit(OpCodes.Call, typeof(Interpreter).GetMethod(nameof(DelayLeaveCount))); }
                        if (EmitInline(d)) Pre(pc); else { Pre(pc); CallHandler(i); }
                        CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, s_StepTail); il.Emit(OpCodes.Pop);
                        Leave(i + 1);
                    }
                    bool DelayFast()
                    {
                        var ok = il.DefineLabel();
                        if (!EmitFast(d, ok, false)) return false;
                        il.Emit(OpCodes.Br, slowD);
                        il.MarkLabel(ok);
                        return true;
                    }
                    if (inlineBranch && (EmitInline(d) || (s_DelayFast && (fastD = DelayFast())))) {
                        // taken: commit and leave; not taken: the block goes on (superblock)
                        var nt = il.DefineLabel();
                        il.Emit(OpCodes.Ldloc, tk); il.Emit(OpCodes.Brfalse, nt);
                        CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldloc, tgt); il.Emit(OpCodes.Conv_U4); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Stfld, s_CpuPc);
                        SetBool(s_SetExecBD, false); SetBool(s_SetCond, false); SetBool(s_SetUncond, false);
                        int lt = LoopTarget(i - 1);
                        if (lt >= 0) {
                            if (s_ValLoops) minLoop = Math.Min(minLoop, lt);
                            // taken to a step of this block: continue there (PC is in the block's zero-extended form)
                            int delta = lt - (i + 1);
                            void AddTo(LocalBuilder l) { il.Emit(OpCodes.Ldloc, l); il.Emit(OpCodes.Ldc_I4, delta); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, l); }
                            AddTo(basis); AddTo(end);
                            il.Emit(OpCodes.Ldc_I4, lt); il.Emit(OpCodes.Stloc, lapStart);
                            il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Stloc, lastIdx);
                            il.Emit(OpCodes.Br, stepLabels[lt]);
                        }
                        else {
                            Done(pc);
                            Ret(i + 1);
                        }
                        il.MarkLabel(nt);
                        SetBool(s_SetExecBD, false);
                        if (i + 1 < n) {
                            if (fastD) { il.Emit(OpCodes.Br, stepLabels[i + 1]); il.MarkLabel(slowD); DelaySlow(); }
                            inDelay = false; inlineBranch = false; continue;
                        }
                        SetPc(pc + 4); Done(pc);
                        Ret(i + 1);
                        if (fastD) { il.MarkLabel(slowD); DelaySlow(); }
                        break;
                    }
                    DelaySlow();
                    // later steps stay reachable as entry points
                    inDelay = false; inlineBranch = false;
                    continue;
                }
                if (IsCpuBranch(d) && s_RecInlineBranch && CanInlineBranch(i)) {
                    EmitBranch(i, pc);
                    inlineBranch = true;
                    inDelay = true;
                    continue;
                }
                if (IsCpuBranch(d)) {
                    Pre(pc);
                    CallHandler(i);
                    CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I8, pc); il.Emit(OpCodes.Call, s_RecAfterBranch);
                    LeaveIf(i + 1);
                    if (i + 1 >= n) { Leave(i + 1); break; }
                    inDelay = true;
                    continue;
                }
                if (EmitInline(d)) {
                    if (i == n - 1) ExitAt(n);
                    continue;
                }
                var loaded = il.DefineLabel();
                string fop = d.Op.Op;
                int fsz = !s_RecInlineFpuMem || s_WatchAddr >= 0 ? 0 : fop == "lwc1" || fop == "swc1" ? 4 : fop == "ldc1" || fop == "sdc1" ? 8 : 0;
                if (s_CpufCeil && (fsz > 0 || fop == "add.fmt" || fop == "sub.fmt" || fop == "mul.fmt" || fop == "div.fmt" || fop == "mtc1" || fop == "mfc1")) {
                    CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I4, d.FloatSource & 31); il.Emit(OpCodes.Ldc_I4, d.FloatTarget & 31); il.Emit(OpCodes.Call, s_RecFpuCeil);
                }
                var rawSlow = il.DefineLabel();
                int cvk = rawOn && s_FpuCvt ? CvtKind(d) : 0;
                bool raw = rawOn && (fsz > 0 || cvk != 0 || (s_RecInlineFpuArith && (d.Format == FpuValueType.FSingle || d.Format == FpuValueType.FDouble)
                    && (fop == "add.fmt" || fop == "sub.fmt" || fop == "mul.fmt" || fop == "div.fmt")));
                if (raw) {
                    // Status & (CU1|FR) == CU1|compiled FR, else the old path
                    CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldfld, s_CpufSrField); il.Emit(OpCodes.Ldfld, s_SrValueField);
                    il.Emit(OpCodes.Ldc_I4, (int)srMask); il.Emit(OpCodes.And); il.Emit(OpCodes.Ldc_I4, (int)srWant); il.Emit(OpCodes.Bne_Un, rawSlow);
                    if (fsz > 0) {
                        int ft = d.FloatTarget;
                        long fa8 = fsz == 4 ? fpr + (frc ? ft * 8L : ((ft & ~1) + ft) * 4L) : fpr + (frc ? ft : ft & ~1) * 8L;
                        il.Emit(OpCodes.Ldc_I8, (long)RdramWords); il.Emit(OpCodes.Conv_U);
                        il.Emit(OpCodes.Ldc_I8, gpr); il.Emit(OpCodes.Conv_U);
                        il.Emit(OpCodes.Ldc_I8, fa8); il.Emit(OpCodes.Conv_U);
                        il.Emit(OpCodes.Ldc_I4, d.Source); il.Emit(OpCodes.Ldc_I4, (int)(short)d.Immediate); il.Emit(OpCodes.Ldc_I4, fsz);
                        il.Emit(fop[0] == 's' ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Call, s_RecFpuMemRaw);
                        il.Emit(OpCodes.Brtrue, loaded);
                        il.Emit(OpCodes.Br, rawSlow);
                    }
                    else if (cvk != 0) {
                        long DA(int r) => fpr + (frc ? r : r & ~1) * 8L;
                        long SA(int r) => fpr + (frc ? r * 8L : ((r & ~1) + r) * 4L);
                        bool sd = d.Format == FpuValueType.FDouble;
                        int fd = d.FloatDest, fs = d.FloatSource;
                        void LdSrc() {
                            if (d.Format == FpuValueType.FDouble) { il.Emit(OpCodes.Ldc_I8, DA(fs)); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldind_R8); }
                            else if (d.Format == FpuValueType.FSingle) { il.Emit(OpCodes.Ldc_I8, SA(fs)); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldind_R4); }
                            else { il.Emit(OpCodes.Ldc_I8, SA(fs)); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldind_I4); }
                        }
                        void ZeroHi() { if (frc) { il.Emit(OpCodes.Ldc_I8, SA(fd) + 4); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stind_I4); } }
                        switch (cvk) {
                            case 1: // trunc.w.{s,d}
                                il.Emit(OpCodes.Ldc_I8, SA(fd)); il.Emit(OpCodes.Conv_U);
                                LdSrc(); il.Emit(OpCodes.Conv_R8); il.Emit(OpCodes.Call, s_CvtTruncW); il.Emit(OpCodes.Stind_I4);
                                break;
                            case 2: // cvt.s.{d,w}: (float)double or (float)int
                                il.Emit(OpCodes.Ldc_I8, SA(fd)); il.Emit(OpCodes.Conv_U);
                                LdSrc(); il.Emit(OpCodes.Conv_R4); il.Emit(OpCodes.Stind_R4); ZeroHi();
                                break;
                            case 3: // cvt.d.{s,w}
                                il.Emit(OpCodes.Ldc_I8, DA(fd)); il.Emit(OpCodes.Conv_U);
                                LdSrc(); il.Emit(OpCodes.Conv_R8); il.Emit(OpCodes.Stind_R8);
                                break;
                            case 4: // neg.{s,d}
                                il.Emit(OpCodes.Ldc_I8, sd ? DA(fd) : SA(fd)); il.Emit(OpCodes.Conv_U);
                                LdSrc(); il.Emit(OpCodes.Neg); if (!sd) il.Emit(OpCodes.Conv_R4); il.Emit(sd ? OpCodes.Stind_R8 : OpCodes.Stind_R4);
                                if (!sd) ZeroHi();
                                break;
                            case 5: { // c.cond.{s,d} without the unordered/signaling paths; NaN on an ordered compare -> handler
                                int ftt = d.FloatTarget;
                                CallStubArg.Ld(il, 0);
                                LdSrc(); il.Emit(OpCodes.Conv_R8);
                                if (sd) { il.Emit(OpCodes.Ldc_I8, DA(ftt)); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldind_R8); }
                                else { il.Emit(OpCodes.Ldc_I8, SA(ftt)); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldind_R4); il.Emit(OpCodes.Conv_R8); }
                                il.Emit(OpCodes.Ldc_I4, (int)d.Op.Flags);
                                il.Emit(OpCodes.Call, s_CvtCmp);
                                il.Emit(OpCodes.Brtrue, loaded);
                                il.Emit(OpCodes.Br, rawSlow);
                                break;
                            }
                        }
                        if (cvk != 5) il.Emit(OpCodes.Br, loaded);
                    }
                    else {
                        bool dbl = d.Format == FpuValueType.FDouble;
                        OpCode aop = fop == "add.fmt" ? OpCodes.Add : fop == "sub.fmt" ? OpCodes.Sub : fop == "mul.fmt" ? OpCodes.Mul : OpCodes.Div;
                        long DA(int r) => fpr + (frc ? r : r & ~1) * 8L;
                        long SA(int r) => fpr + (frc ? r * 8L : ((r & ~1) + r) * 4L);
                        int fd = d.FloatDest, fs = d.FloatSource, ftt = d.FloatTarget;
                        il.Emit(OpCodes.Ldc_I8, dbl ? DA(fd) : SA(fd)); il.Emit(OpCodes.Conv_U);
                        il.Emit(OpCodes.Ldc_I8, dbl ? DA(fs) : SA(fs)); il.Emit(OpCodes.Conv_U); il.Emit(dbl ? OpCodes.Ldind_R8 : OpCodes.Ldind_R4);
                        il.Emit(OpCodes.Ldc_I8, dbl ? DA(ftt) : SA(ftt)); il.Emit(OpCodes.Conv_U); il.Emit(dbl ? OpCodes.Ldind_R8 : OpCodes.Ldind_R4);
                        il.Emit(aop);
                        if (!dbl) il.Emit(OpCodes.Conv_R4);
                        il.Emit(dbl ? OpCodes.Stind_R8 : OpCodes.Stind_R4);
                        if (!dbl && frc) { il.Emit(OpCodes.Ldc_I8, SA(fd) + 4); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stind_I4); } // FR=1 single write zeroes the upper word
                        il.Emit(OpCodes.Br, loaded);
                    }
                    il.MarkLabel(rawSlow);
                }
                EmitFast(d, loaded, true);
                if (s_Slim && !s_OpMix) {
                    // one call instead of the inline tick flush + handler + after-checks (much less IL to JIT)
                    CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I8, pc); il.Emit(OpCodes.Ldloc, pt); il.Emit(OpCodes.Ldloc, pcn);
                    CallStubArg.Ld(il, 1); CallStubArg.Ld(il, 3); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldc_I4_0); // [ge_fix1] keepQuiet=false (IL must push the optional arg)
                    il.Emit(OpCodes.Call, s_RecSlowStep);
                    Zero(pt); Zero(pcn); Zero(quiet);
                }
                else {
                    Pre(pc);
                    CallHandler(i);
                    CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_RecAfterHandler);
                }
                LeaveIf(i + 1);
                il.MarkLabel(loaded);
                if (i == n - 1) ExitAt(n);
            }

            // ---- cold stubs ----
            il.MarkLabel(budgetStub);
            il.Emit(OpCodes.Ldloc, end); il.Emit(OpCodes.Stloc, k);
            il.MarkLabel(exitStub);
            CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I8, b.StartPc); il.Emit(OpCodes.Ldloc, k); il.Emit(OpCodes.Ldloc, basis);
            il.Emit(OpCodes.Ldloc, pt); il.Emit(OpCodes.Ldloc, pcn); il.Emit(OpCodes.Ldloc, cad); il.Emit(OpCodes.Ldloc, lapStart); il.Emit(OpCodes.Ldloc, lastIdx); il.Emit(OpCodes.Call, s_RecExit);
            il.Emit(OpCodes.Ret);
            foreach (var (t, kk) in checkThunks) { il.MarkLabel(t); il.Emit(OpCodes.Ldc_I4, kk); il.Emit(OpCodes.Stloc, k); il.Emit(OpCodes.Br, checkStub); }
            foreach (var (t, kk) in leaveThunks) { il.MarkLabel(t); il.Emit(OpCodes.Ldc_I4, kk); il.Emit(OpCodes.Stloc, k); il.Emit(OpCodes.Br, leaveStub); }
            // check: cad = 0, ticks applied, interrupt check at PC = step k; bail -> interpreter finishes step k
            il.MarkLabel(checkStub);
            Zero(cad);
            {
                // quiet: the last check found nothing, no handler ran since, the timer cannot have reached Compare
                var real = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, pcn); il.Emit(OpCodes.Ldloc, quiet); il.Emit(OpCodes.Bge, real);
                il.Emit(OpCodes.Ldloc, k); il.Emit(OpCodes.Switch, resume);
                il.MarkLabel(real);
            }
            CallStubArg.Ld(il, 0);
            il.Emit(OpCodes.Ldc_I8, b.StartPc); il.Emit(OpCodes.Ldloc, k); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Ldc_I8, 4L); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ldloc, pt); il.Emit(OpCodes.Ldloc, pcn); il.Emit(OpCodes.Call, s_RecCheckQ);
            il.Emit(OpCodes.Stloc, quiet);
            Zero(pt); Zero(pcn);
            {
                var ok = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, quiet); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Bge, ok);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldloc, cad); il.Emit(OpCodes.Stfld, s_IntCheckField);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_ExecAfterPrologue); il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldloc, k); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldloc, basis); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Ret);
                il.MarkLabel(ok);
                il.Emit(OpCodes.Ldloc, k); il.Emit(OpCodes.Switch, resume);
                Ret(0); // not reached
            }
            // leave: the step was finished by the interpreter's own code (nothing pending); k steps index after it
            il.MarkLabel(leaveStub);
            CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldloc, cad); il.Emit(OpCodes.Stfld, s_IntCheckField);
            il.Emit(OpCodes.Ldloc, k); il.Emit(OpCodes.Ldloc, basis); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Ret);
            System.Threading.Interlocked.Increment(ref CpuRecCompiled);
            b.IlSize = il.ILOffset;
            b.DelayEntry = s_RecDelayEntry ? delayEntry : null;
            b.MinLoop = minLoop;
            return CallStubArg.Bind<CpuRecFn>(dm);
        }
    }
}
