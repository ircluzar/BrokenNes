// [climb4 x64_emit] Native x86-64 code for compiled CPU blocks (default on on Windows x64; COR64_X64=0 keeps the IL compiler).
//
// Block contract: the same as the IL blocks (CpuRecompiler.cs): running k steps leaves the machine exactly as k Step()
// calls. The native code is a line-for-line port of RecCompile's IL for the ops it supports; a block that holds any other
// op is compiled to IL as before (per-block fallback).
//
// Call: int fn(XCtx* c), called with delegate* unmanaged[SuppressGCTransition]. No GC can run while it runs (the thread
// stays in cooperative mode and the code has no safepoints), so the code may use the address of the pinned interpreter
// object (c->Obj) and fixed native buffers (GPR file, Hi/Lo, RDRAM). The native code never calls managed code: anything
// it cannot finish (interrupt check, a load/store slow path, a handler in a delay slot, div/divu, leaving the block)
// returns an exit code; RunX64 runs the managed piece (the same helper the IL calls) and, where the IL would carry on,
// re-enters the native code at that point (entry table: kind * n + step).
//
// Pinned host registers: rbx = XCtx*, rsi = GPR file (ulong[32]), rdi = RDRAM, r12 = interpreter anchor
// (&m_LastFetchAddress; field offsets measured once), r13d = cad (interrupt-check cadence), r14d = pt (TLB ticks),
// r15d = pcn (Count ticks). Scratch: rax, rcx, rdx, r8. GPRs stay in memory (no caching across ops).
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using cor64.Mips.X64;

namespace cor64.Mips.R4300I
{
    [StructLayout(LayoutKind.Sequential)]
    public struct XCtx
    {
        public long Mode, Basis, LapStart, LastIdx, Cad, Pt, Pcn, Quiet, End, Tk, Tgt, K, Aux, Obj, Gpr, Ram, Sr, Nt, Gen, Steps, Fn, Acc, Vco, Vcc, Clip, Blk, Link, MapKey, CAcc, Fcr, Ep0, Cause, Count, CScale, MiI, MiM;
    }

    public partial class Interpreter
    {
        // [session_leak] COR64_TLB_INLINE=0: TLB-mapped loads/stores in native blocks always take the slow (interpreter) step.
        public static readonly bool s_TlbInline = Environment.GetEnvironmentVariable("COR64_TLB_INLINE") != "0" && cor64.Mips.R4300I.TLB.TLBCache.s_Fc;
        public static readonly bool s_X64 = Environment.GetEnvironmentVariable("COR64_X64") != "0" && ExecMem.Supported; // default on (Windows x64)
        // [fpu_native] measurement: COR64_X64_SLOWOPS=1 counts XSlow exits by op (printed by SessionProbe)
        public static readonly bool s_X64SlowOps = Environment.GetEnvironmentVariable("COR64_X64_SLOWOPS") == "1";
        static readonly bool s_X64SlowPc = Environment.GetEnvironmentVariable("COR64_SPDPOLL2_SLOWPC") == "1"; // [speed_poll2] measurement only: PC in X64SLOW keys
        public static readonly System.Collections.Generic.Dictionary<string, long> X64SlowOpCount = new();
        public static long X64Blocks, X64Rejected, X64Calls, X64Steps, X64Reentries;
        public static readonly System.Collections.Generic.Dictionary<string, long> X64RejectOps = new();
        private static readonly CpuRecFn s_X64Sentinel = (s, i, m, h, st) => 0;

        // XCtx slot offsets
        private const int CMode = 0, CBasis = 8, CLap = 16, CLast = 24, CCad = 32, CPt = 40, CPcn = 48, CQuiet = 56, CEnd = 64,
            CTk = 72, CTgt = 80, CK = 88, CAux = 96, CObj = 104, CGpr = 112, CRam = 120, CSr = 128,
            CBlk = 200, CLink = 208, CMapKey = 216, CAcc = 224, CFcr = 232, CEp0 = 240, CCause = 248, CCount = 256, CCScale = 264, CMiI = 272, CMiM = 280;
        private const int XExit = 0, XDone = 1, XCheck = 2, XSlow = 3, XDSlow = 4, XMulDiv = 5, XStale = 6;
        // [block_link] COR64_X64_NVAL=0: RecEntry compares a native block's words with RDRAM (RecValidate), as before. On:
        // the block's code compares them itself on entry (cmp [ram+phys], imm32 per word, from the same first word
        // RecValidate would use), so the managed side never touches b.Raw; a mismatch returns XStale with nothing run.
        public static readonly bool s_X64NVal = Environment.GetEnvironmentVariable("COR64_X64_NVAL") == "1";
        [NonSerialized] private bool m_RecStale;
        public static readonly bool s_X64Resume = Environment.GetEnvironmentVariable("COR64_X64_RESUME") != "0" && s_X64NVal;
        [NonSerialized] private CpuRecBlock m_ResumeBlk;
        [NonSerialized] private long m_ResumePc, m_ResumeEpoch;
        [NonSerialized] private int m_ResumeOff;
        // after a native run: remember the block it left (when PC is inside it, e.g. a batch-budget exit)
        private unsafe void X64NoteResume(CpuRecBlock b)
        {
            int id = (int)m_XCtx->Blk;
            if (id != b.XId && id >= 0) b = s_XById[id];
            if (!b.NVal || BU_ConditionalPending || BU_UnconditionalPending || BU_DelaySlotNext || BU_ExecuteBranchDelay || BU_NullifyNext) return;
            long off = PC - b.StartPc;
            if (off <= 0 || (off & 3) != 0 || off >= b.Len * 4L) return;
            m_ResumeBlk = b; m_ResumePc = PC; m_ResumeOff = (int)(off >> 2); m_ResumeEpoch = *global::cor64.Mips.R4300I.TLB.MapEpoch.Ptr;
        }

        [NonSerialized] private unsafe XCtx* m_XCtx;

        // [block_link] COR64_X64_LINK=0: every native block returns to RunSteps at its exit, as before. On: an exit with a
        // static target PC (taken j/jal/branch, not-taken fall-through, end of block) jumps straight into the next native
        // block through a link slot {chain entry, map key, target pc}. The slot is filled by RunSteps the first time that
        // exit is followed by RecEntry returning the block at that PC. The chain entry re-checks every word of the target
        // against RDRAM (immediates in the code; a mismatch leaves at step 0 of the target with nothing run), so a stale
        // link costs only a normal exit; a TLB-mapped target also needs the TLB generation + ASID the link was made under.
        // Ticks, the interrupt-check cadence and the quiet window stay in their registers across the jump, exactly as
        // RunX64Core would reload them; nothing managed runs between two linked blocks, so RecEntry's other conditions
        // (no pending branch, exception, interrupt, injected instruction) still hold.
        public static readonly bool s_X64Batch = Environment.GetEnvironmentVariable("COR64_X64_BATCH") != "0"; // [jit_cost1]
        // [jit_cost1] measurement: COR64_X64_EXITS=1 counts native-call returns by exit code (printed by SessionProbe)
        public static readonly bool s_X64Exits = Environment.GetEnvironmentVariable("COR64_X64_EXITS") == "1";
        public static readonly long[] X64ExitCount = new long[8], X64QHist = new long[9], X64QZero = new long[3];
        // [jit_cost1] COR64_X64_TAILBR=0: a block that ends with a branch (delay slot on the next page) stays IL, whose handler
        // path drops the carried quiet window, so every following native call left at its first interrupt check
        public static readonly bool s_X64TailBr = Environment.GetEnvironmentVariable("COR64_X64_TAILBR") != "0";
        public static readonly bool s_X64Fwd = Environment.GetEnvironmentVariable("COR64_X64_FWD") != "0"; // [jit_cost1]
        public static readonly bool s_X64Link = Environment.GetEnvironmentVariable("COR64_X64_LINK") != "0"; // [block_link2] default on
        public static long X64Linked, X64LinkMade;
        private static readonly bool s_X64LinkJr = Environment.GetEnvironmentVariable("COR64_X64_LINK_JR") != "0";
        // [f5_speed3] ways of the jr/jalr link cache per site (1 = the old one-entry cache), newest first
        // [f5_speed3] a link to a target in the source block's own 4 KB virtual page needs no map-epoch check: the source
        // block runs on that page's current translation (COR64_X64_LINK_SAMEPAGE=0: always check mapped targets)
        private static readonly bool s_X64LinkSamePage = Environment.GetEnvironmentVariable("COR64_X64_LINK_SAMEPAGE") != "0";
        // [f5_speed3] links to a target inside a block (a return after a call, a branch into a block): COR64_X64_LINK_MID=0 off
        private static readonly bool s_X64LinkMid = Environment.GetEnvironmentVariable("COR64_X64_LINK_MID") != "0";
        private static readonly int s_X64LinkWays = Math.Clamp(int.TryParse(Environment.GetEnvironmentVariable("COR64_X64_LINK_WAYS"), out int lw) ? lw : 4, 1, 8);
        private static readonly bool s_LinkNoFill = Environment.GetEnvironmentVariable("COR64_X64_LINK_NOFILL") == "1"; // debug
        private static readonly object s_XIdLock = new();
        private static volatile CpuRecBlock[] s_XById = new CpuRecBlock[1024];
        private static int s_XIdNext;
        private static readonly System.Collections.Generic.Stack<int> s_XIdFree = new();
        private static readonly System.Collections.Generic.List<int> s_XIdRetired = new();
        private static unsafe byte* s_SlotArena; private static int s_SlotUsed;
        [NonSerialized] private long m_XPendLink;
        private static int XIdAlloc(CpuRecBlock b)
        {
            lock (s_XIdLock) {
                int id = s_XIdFree.Count > 0 ? s_XIdFree.Pop() : s_XIdNext++;
                var arr = s_XById;
                if (id >= arr.Length) { var na = new CpuRecBlock[arr.Length * 2]; Array.Copy(arr, na, arr.Length); arr = na; }
                arr[id] = b;
                s_XById = arr;
                return id;
            }
        }
        // a retired block's id is reused only after ExecMem.Reclaim (no native code can still run then)
        private static void XIdRetire(CpuRecBlock b) { if (b.XId < 0) return; lock (s_XIdLock) s_XIdRetired.Add(b.XId); }
        private void XIdReclaim()
        {
            m_XPendLink = 0;
            lock (s_XIdLock) { foreach (int id in s_XIdRetired) { s_XById[id] = null; s_XIdFree.Push(id); } s_XIdRetired.Clear(); }
        }
        private static unsafe long* SlotAlloc(int bytes = 32)
        {
            lock (s_XIdLock) {
                if (s_SlotArena == null || s_SlotUsed + bytes > (1 << 20)) { s_SlotArena = (byte*)NativeMemory.AlignedAlloc(1 << 20, 64); NativeMemory.Clear(s_SlotArena, 1 << 20); s_SlotUsed = 0; }
                long* p = (long*)(s_SlotArena + s_SlotUsed); s_SlotUsed += bytes;
                return p;
            }
        }
        // ctx MapKey = address of the epoch mapped links compare with (a never-matching -1 while an NMI-class exception is pending)
        private unsafe long X64MapPtr() => (long)(Cop0.IsNonMaskablePending ? global::cor64.Mips.R4300I.TLB.MapEpoch.Never : global::cor64.Mips.R4300I.TLB.MapEpoch.Ptr);
        // RunSteps: the last native exit missed its link slot and RecEntry now gives block b at PC: fill the slot.
        // Slot layout: header s[-1] = 0 (static target) or ways | (source page << 8) (jr/jalr); then per way
        // [entry, map key, target, start step]. [f5_speed3] start > 0 = a target inside block b (MidEntry, r9 = start).
        private unsafe void X64TryLink(CpuRecBlock b, int start)
        {
            long* s = (long*)m_XPendLink; m_XPendLink = 0;
            if (s_LinkNoFill) return;
            if (b.Retired || b.StartPc + 4L * start != PC) return;
            IntPtr entry = start == 0 ? b.ChainEntry : b.MidEntry;
            if (entry == IntPtr.Zero || (start != 0 && (b.NVal || (b.DelayEntry != null && b.DelayEntry[start])))) return;
            long hdr = s[-1];
            if (hdr != 0) { // jr/jalr site: newest target first (a refill of an existing way moves it to the front)
                int ways = (int)(hdr & 0xFF), w = 0;
                while (w < ways - 1 && s[4 * w] != 0 && s[4 * w + 2] != PC) w++;
                for (; w > 0; w--) { s[4 * w] = s[4 * w - 4]; s[4 * w + 1] = s[4 * w - 3]; s[4 * w + 2] = s[4 * w - 2]; s[4 * w + 3] = s[4 * w - 1]; }
                s[2] = PC;
            }
            else if (s[2] != PC) return;
            s[1] = ((uint)PC >> 30) == 2 ? 0 : hdr != 0 && (hdr >> 8) == (long)((uint)PC >> 12) ? -2 : *global::cor64.Mips.R4300I.TLB.MapEpoch.Ptr;
            s[3] = start;
            s[0] = (long)entry;
            X64LinkMade++;
        }
        private static unsafe uint* s_X64DummySr;

        // ---- field offsets from the anchor (&m_LastFetchAddress) ----
        private static int s_OffEnableCp1, s_OffPc, s_OffTarget, s_OffCond, s_OffUncond, s_OffExecBD;
        private static bool s_OffsDone;
        private static readonly object s_OffLock = new();

        private void X64Offsets()
        {
            lock (s_OffLock) {
                if (s_OffsDone) return;
                var anchor = typeof(Interpreter).GetField(nameof(m_LastFetchAddress), BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo Bk(string p) => typeof(InterpreterBaseR4300I).GetField("<" + p + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("backing field " + p);
                int Off(FieldInfo f)
                {
                    var dm = new DynamicMethod("x64off", typeof(long), new[] { typeof(Interpreter) }, typeof(Interpreter), true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldflda, f); il.Emit(OpCodes.Conv_U);
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldflda, anchor); il.Emit(OpCodes.Conv_U);
                    il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Ret);
                    var fn = (Func<Interpreter, long>)dm.CreateDelegate(typeof(Func<Interpreter, long>));
                    long a = fn(this), c;
                    while ((c = fn(this)) != a) a = c; // a GC between the two ldflda would skew one reading
                    return checked((int)a);
                }
                s_OffPc = Off(s_CpuPc);
                s_OffEnableCp1 = Off(typeof(BaseInterpreter).GetField("<EnableCp1>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("EnableCp1"));
                s_OffTarget = Off(Bk(nameof(BU_TargetAddress)));
                s_OffCond = Off(Bk(nameof(BU_ConditionalPending)));
                s_OffUncond = Off(Bk(nameof(BU_UnconditionalPending)));
                s_OffExecBD = Off(Bk(nameof(BU_ExecuteBranchDelay)));
                s_OffsDone = true;
            }
        }

        // ---- which blocks go native ----
        private static bool X64Inline(in DecodedInstruction d)
        {
            switch (d.Op.Op) {
                case "addu": case "addiu": case "subu": case "and": case "or": case "xor": case "nor": case "andi": case "ori":
                case "xori": case "lui": case "slt": case "sltu": case "slti": case "sltiu": case "daddu": case "daddiu":
                case "sll": case "srl": case "sra": case "sllv": case "srlv": case "srav":
                    return true;
                case "cache": return s_IhCache;
                case "mflo": case "mfhi": case "mtlo": case "mthi": case "mult": case "multu": case "div": case "divu": return s_IhHiLo;
                case "dsll": case "dsrl": case "dsra": case "dsll32": case "dsrl32": case "dsra32": case "dsllv": case "dsrlv": case "dsrav":
                case "dsubu":
                    return s_Cat4D64 && s_DsraFix && s_Cat4D64Env != "mul";
                case "dmult": case "dmultu": return s_Cat4D64 && s_Cat4D64Env != "shift";
                case "ddivu": return s_Cat10DDivU; // [speed_cat10]
            }
            return false;
        }
        // [speed_cat4] COR64_SPDCAT4_D64 (default on; =0 off): dsll/dsrl/dsra(32)/dsllv/dsrlv/dsrav/dsubu inline and
        // dmult/dmultu through the XMulDiv exit, so blocks with them go native instead of IL (Shift64 / handler arithmetic;
        // CPU_ALWAYS_64, no reserved check). Duke Nukem 64's hot loops at 800703F0 / 80063FB0 were rejected for these.
        // [climb4 speed_cat10] COR64_SPDCAT10_DDIVU (default on; =0 off): ddivu through the XMulDiv exit (Divide64's unsigned
        // path: b = 0 -> hi = a, lo = ~0) instead of the XSlow handler step. Hybrid Heaven's 64-bit math helper.
        // [climb4 speed_cat11] COR64_SPDCAT11_IE (default on; =0 off): mtc0 Status that newly sets IE / IM bits stays inline when
        // no enabled interrupt can be due: the carried quiet window is still open (PCN < Quiet: the timer is not due within the
        // unapplied ticks and MI is unchanged since the last real check, which refreshed Cause IP2 / IP7) and
        // (Cause.IP & new IM) == 0. The slow path took no interrupt either (the handler only writes Status; the next real
        // check does), so every check the window skips would have found nothing. Otherwise the existing XSlow exit.
        internal static readonly bool s_Cat11Ie = Environment.GetEnvironmentVariable("COR64_SPDCAT11_IE") != "0";
        // COR64_SPDCAT11_COUNT (default on; =0 off): mfc0 Count in kernel mode reads (uint)(Count + PCN * CountScale) sign-extended
        // (what RecPre's flush + the handler return); ticks stay unapplied (a pure op: the quiet window is unchanged as before).
        // COR64_SPDCAT11_EXL (default on; =0 off): mtc0 Status that only sets EXL (with the IE / IM changes above) inline: it
        // disables interrupts (the interrupt-exit path before eret; 1080). Clearing EXL stays slow.
        // COR64_SPDCAT11_EPC (default on; =0 off): mtc0 EPC in kernel mode stores the 64-bit GPR (CpuRegWrite's plain register).
        internal static readonly bool s_Cat11Exl = Environment.GetEnvironmentVariable("COR64_SPDCAT11_EXL") != "0";
        internal static readonly bool s_Cat11Epc = Environment.GetEnvironmentVariable("COR64_SPDCAT11_EPC") != "0";
        internal static readonly bool s_Cat11Count = Environment.GetEnvironmentVariable("COR64_SPDCAT11_COUNT") != "0";
        // COR64_SPDCAT11_CVTL (default on; =0 off): cvt.l.d native: roundsd by FCSR.RM (RoundBy), cvttsd2si; the 2^63 indefinite
        // result (NaN / out of range / -2^63) takes the XSlow exit (the handler's saturating .NET conversion).
        internal static readonly bool s_Cat11CvtL = Environment.GetEnvironmentVariable("COR64_SPDCAT11_CVTL") != "0" && System.Runtime.Intrinsics.X86.Sse41.IsSupported;
        // [climb4 speed_cat12] COR64_SPDCAT12_MI (default on; =0 off): lw / lwu of MI_INTR (A4300008) and MI_INTR_MASK (A430000C)
        // and sw of MI_INTR_MASK inline (TGR2 / 1080 osSetIntMask). The store applies ProcessMaskClearSet's pair encoding (pair i:
        // bit 0 set -> clear mask bit i, else 2 -> set it) when MI_INTR & mask is unchanged, so the quiet interrupt-check window
        // stays exact (MiPending unchanged: no check can find anything new); a write that changes MiPending takes the XSlow exit.
        // Off with COR64_EVENTLOG / COR64_DBG_WATCH (the bus logs those accesses).
        internal static readonly bool s_Cat12Mi = Environment.GetEnvironmentVariable("COR64_SPDCAT12_MI") != "0" && !global::cor64.EventLog.Enabled && !global::cor64.N64MemoryController.s_DbgWatch;
        // COR64_SPDCAT12_DIV (default on; =0 off): div / divu, ddivu (RecDDivU) and dmultu (mul r64 = Multiply64_Unsigned) inline (dmult stays on
        // the exit: Multiply64_Signed is not the true 128-bit product); div / divu (RecMulDiv's results, as 64-bit idiv / div so 0x80000000 / -1
        // cannot fault; divide by zero: hi = rs, lo = rs < 0 ? 1 : -1 (div) / -1 (divu)) instead of the XMulDiv exit. Hybrid Heaven.
        internal static readonly bool s_Cat12Div = Environment.GetEnvironmentVariable("COR64_SPDCAT12_DIV") != "0";
        internal static readonly bool s_Cat10DDivU = Environment.GetEnvironmentVariable("COR64_SPDCAT10_DDIVU") != "0";
        private static unsafe void RecDDivU(ulong* g, ulong* hl, int rs, int rt)
        {
            ulong a = g[rs], b = g[rt];
            if (b == 0) { hl[0] = a; hl[1] = ulong.MaxValue; return; }
            hl[0] = a % b; hl[1] = a / b;
        }
        private static unsafe void RecDMul(ulong* g, ulong* hl, int rs, int rt, bool unsig)
        {
            var v = unsig ? global::cor64.Mips.CoreUtils.Multiply64_Unsigned(g[rs], g[rt]) : global::cor64.Mips.CoreUtils.Multiply64_Signed(g[rs], g[rt]);
            hl[0] = v.hi; hl[1] = v.lo;
        }
        // [speed_cat5] COR64_SPDCAT5_SLOW (default on; =0 off): ops with no native form (mfc0, ddiv(u), break, lwl/lwr/swl/swr,
        // ldl/ldr/sdl/sdr) no longer reject the whole block to IL: outside delay slots they run through an unconditional XSlow
        // exit (RecSlowStep = the IL's own Pre + handler + after). They are kept out of batched segments (an exit inside a
        // segment would not undo the segment's tick pre-add). Duke Nukem 64's remaining rejects.
        internal static readonly bool s_Cat5Slow = Environment.GetEnvironmentVariable("COR64_SPDCAT5_SLOW") != "0";
        private static bool X64Generic(in DecodedInstruction d) => s_Cat5Slow && (d.Op.Op is "mfc0" or "ddiv" or "ddivu" or "break"
            or "lwl" or "lwr" or "swl" or "swr" or "ldl" or "ldr" or "sdl" or "sdr" || (s_Cat5Cop && d.Op.Op is "mtc0" or "cfc1" or "ctc1"
            or "dmtc1" or "dmfc1" or "cvt.l.fmt") || (s_Cat8Gen && d.Op.Op is "cvt.w.fmt" or "tlbp " or "tlbwi ")
            || (s_Cat13CvtW && d.Op.Op == "cvt.w.fmt" && (d.Format == FpuValueType.FSingle || d.Format == FpuValueType.FDouble))
            || (s_Cat13Eret && d.Op.Op == "eret "));
        // [climb4 speed_cat13] COR64_SPDCAT13_ERET (default on; =0 off): eret (always a block's last step) through an XSlow exit
        // that leaves the block (RecSlowStep = the IL's Pre + handler + RecAfterHandler), so exception handlers run native.
        // 1080 Snowboarding's TLB handler at 80111000 (5 ldc1 ... eret) ran as an IL block 5.3M times per 600 frames; its
        // slow-step Pre is what dropped the carried quiet window before ~5.3M of the 5.4M no-window native entries.
        // [climb4 speed_cat13] COR64_SPDCAT13_FIX (default on; =0 off): round.w / floor.w / ceil.w (S, D) and cvt.d.l native
        // (roundsd 0 / 1 / 2, cvttsd2si r64, written when it fits int32, else the slow exit = the handler's int.MaxValue;
        // cvt.d.l = cvtsi2sd r64, as (double)(long)). South Park Rally's rejects.
        internal static readonly bool s_Cat13Fix = Environment.GetEnvironmentVariable("COR64_SPDCAT13_FIX") != "0" && System.Runtime.Intrinsics.X86.Sse41.IsSupported;
        private static int X64Cat13Fix(in DecodedInstruction d)
        {
            if (!s_Cat13Fix) return 0;
            bool fl = d.Format == FpuValueType.FSingle || d.Format == FpuValueType.FDouble;
            return d.Op.Op switch { "round.w.fmt" when fl => 11, "floor.w.fmt" when fl => 12, "ceil.w.fmt" when fl => 13, "cvt.d.fmt" when d.Format == FpuValueType.Doubleword => 14, _ => 0 };
        }
        // [climb4 speed_cat13] COR64_SPDCAT13_BRK (default on; =0 off): break in a delay slot (the compiler's divide-by-zero
        // guard, beql ... break) no longer rejects the block: it always takes the XDSlow exit (handler = the exception).
        // [climb4 speed_cat17] COR64_SPDCAT17_SEG (default on; =0 off): inline mfc0 / mtc0 Status (EmitC0Status) may sit inside a
        // batched segment. Its slow path undoes the ticks of the steps after it (cad modulo the check cadence, as the per-step
        // copy would hold it) and takes the op's XSlow exit; the resume runs the segment's per-step copy. __osDisableInt /
        // __osRestoreInt (Racer: ~50k calls a frame each) now pay one tick update instead of one per step.
        // [speed_cat17] measurement only: COR64_SPDCAT17_CNT=1 counts chained block entries and the words they validate
        public static readonly bool s_Cat17Cnt = Environment.GetEnvironmentVariable("COR64_SPDCAT17_CNT") == "1";
        public static readonly unsafe long* s_Cat17CntBuf = (long*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(64);
        // COR64_SPDCAT17_BRMEM (default on; =0 off): also a load / store delay slot (its slow path stores the branch state
        // and leaves through the delay-slot exit, as the per-step path would)
        internal static readonly bool s_Cat17BrMem = Environment.GetEnvironmentVariable("COR64_SPDCAT17_BRMEM") != "0";
        // COR64_SPDCAT17_LIKELY (default on; =0 off): also the likely forms (not taken: the delay slot's tick is taken back and the
        // block is left at pc + 8, as EmitBranch's not-taken path)
        internal static readonly bool s_Cat17Likely = Environment.GetEnvironmentVariable("COR64_SPDCAT17_LIKELY") != "0";
        // [climb4 speed_cat17] COR64_SPDCAT17_CADFIX (default on; =0 old behaviour): a load / store slow path inside a batched
        // segment took the later steps' ticks back with cad - r, but when the segment had wrapped cad (every check inside
        // the run skipped) that left cad negative, so the next interrupt check came up to s_IntCheckEvery steps late versus
        // the per-step code. Now cad is taken modulo the cadence (exactly what the per-step copy holds). Changes the hashes
        // of games that hit it (South Park Rally); the per-step path (all segments forced cold) gives the fixed result.
        internal static readonly bool s_Cat17CadFix = Environment.GetEnvironmentVariable("COR64_SPDCAT17_CADFIX") != "0";
        internal static readonly bool s_Cat17Br = Environment.GetEnvironmentVariable("COR64_SPDCAT17_BR") != "0";
        // [n64_speed] lane levers (each exact: same steps, same interrupt-check points, same machine state):
        // COR64_SPD_BRFPU=1 (lever 1): a simple branch whose delay slot is a native FPU op (lwc1/swc1/ldc1/sdc1, mtc1/mfc1,
        // add/sub/mul/div, cvt/trunc/neg/c.cond...) also closes a batched segment (FastBranch's load/store delay path: the
        // delay slot's slow path stores the branch state, then the XDSlow exit), instead of the per-step tick bookkeeping.
        internal static readonly bool s_SpdBrFpu = Environment.GetEnvironmentVariable("COR64_SPD_BRFPU") == "1";
        // COR64_SPD_LAZYBR=1 (lever 2; jumps, likely branches, bc1f/bc1t; see EmitBranch for the other modes): a per-step branch keeps its taken flag / target in ctx only; the interpreter's branch
        // fields (target, cond / uncond, ExecuteBranchDelay, PC = delay slot) are written only on the delay slot's exit paths
        // (budget end, interrupt check that leaves, delay-slot slow exit), which are the only places the managed side reads them.
        internal static readonly int s_SpdLazyBrM = int.TryParse(Environment.GetEnvironmentVariable("COR64_SPD_LAZYBR"), out int lzb) ? lzb : 0;
        internal static readonly bool s_SpdLazyBr = s_SpdLazyBrM > 0;
        // COR64_SPD_SRHOIST=1 (lever 3): inside a batched segment the Status CU1/FR test is done by the first FPU op only
        // (nothing in a segment's hot path can change Status CU1/FR, and it has no entry point after its head); that first
        // test's other-FR-mode case takes the op's slow exit (the per-step copy, with the dual-FR code, runs the rest).
        internal static readonly bool s_SpdSrHoist = Environment.GetEnvironmentVariable("COR64_SPD_SRHOIST") == "1";
        internal static readonly bool s_Cat17Seg = Environment.GetEnvironmentVariable("COR64_SPDCAT17_SEG") != "0";
        internal static readonly bool s_Cat16Chk = Environment.GetEnvironmentVariable("COR64_SPDCAT16_CHK") != "0";
        internal static readonly bool s_Cat13Brk = Environment.GetEnvironmentVariable("COR64_SPDCAT13_BRK") != "0";
        private static bool X64Br(in DecodedInstruction d) => IsCpuBranch(d) && !(s_Cat13Eret && d.Op.Op == "eret ");
        internal static readonly bool s_Cat13Eret = Environment.GetEnvironmentVariable("COR64_SPDCAT13_ERET") != "0";
        // [climb4 speed_cat13] COR64_SPDCAT13_CVTW (default on; =0 off): cvt.w.s / cvt.w.d no longer reject their block to IL.
        // Native: RoundBy(FCSR.RM) as cvt.l.d (roundsd), cvttsd2si r64, written when the result fits int32; NaN / out of range
        // take the XSlow exit (the handler), which keeps the quiet window (no interrupt-state effect). 1080's 29 cvt.w rejects.
        internal static readonly bool s_Cat13CvtW = Environment.GetEnvironmentVariable("COR64_SPDCAT13_CVTW") != "0" && System.Runtime.Intrinsics.X86.Sse41.IsSupported;
        // [speed_cat8] COR64_SPDCAT8_GEN (opt-in: =1; no measured gain on 1080): cvt.w.fmt (FCSR rounding), tlbp, tlbwi through XSlow (the handler),
        // so their blocks go native; tlbp/tlbwi leave the block after the step (the translation may change). 1080 Snowboarding rejects.
        // [climb4 speed_cat9] COR64_SPDCAT9_C0 (default on; =0 off): mfc0/mtc0 of Status (cp0 12) inline (EmitC0Status), with
        // the XSlow exit kept as the fallback. TGR2 / 1080 / Hybrid Heaven / Lamborghini spend their frame in
        // __osDisableInt / __osRestoreInt slow steps.
        internal static readonly bool s_Cat9C0 = Environment.GetEnvironmentVariable("COR64_SPDCAT9_C0") != "0";
        private static bool X64C0Status(in DecodedInstruction d) => (d.Op.Op == "mfc0" || d.Op.Op == "mtc0") && d.Destination == 12 && X64Generic(d);
        // [climb4 speed_cat10] COR64_SPDCAT10_C1 (default on; =0 off): cfc1 (FCSR / revision), dmfc1, and ctc1 FCSR writes that keep
        // the rounding mode and FS bits, inline (EmitC1Ctl) in kernel mode with CU1 set; anything else takes the XSlow exit.
        internal static readonly bool s_Cat10C1 = Environment.GetEnvironmentVariable("COR64_SPDCAT10_C1") != "0";
        // COR64_SPDCAT10_C1RM (default on; =0 off): ctc1 that changes the rounding mode / FS also inline (only the cvt.w / cvt.l
        // handlers read the rounding mode, at run time; native code never does), with no block exit.
        internal static readonly bool s_Cat10C1Rm = Environment.GetEnvironmentVariable("COR64_SPDCAT10_C1RM") != "0";
        private static bool X64C1Ctl(in DecodedInstruction d) => s_Cat10C1 && (d.Op.Op == "cfc1" || d.Op.Op == "dmfc1" || (d.Op.Op == "ctc1" && d.Destination == 31)) && X64Generic(d);
        internal static readonly bool s_Cat8Gen = Environment.GetEnvironmentVariable("COR64_SPDCAT8_GEN") == "1";
        // [speed_cat5] COR64_SPDCAT5_COP (default on): also mtc0 (Status etc.), cfc1/ctc1, dmtc1/dmfc1, cvt.l through XSlow (speed_cat6:
        // Hybrid Heaven / Shadows / Aidyn interrupt helpers and the 64-bit math helper). mtc0 and ctc1 always leave the block after
        // the step (interrupt enable, FR, rounding mode change what the native code assumes).
        // COR64_SPDCAT5_QUIET (default on): ops with no interrupt-state effect (mfc0, cfc1, dmfc1, dmtc1, cvt.l, ddiv(u), lwl/lwr/
        // ldl/ldr) keep the quiet interrupt-check window (recomputed from the timer, as at block entry) instead of dropping it.
        internal static readonly bool s_Cat5Cop = Environment.GetEnvironmentVariable("COR64_SPDCAT5_COP") != "0";
        internal static readonly bool s_Cat5Quiet = Environment.GetEnvironmentVariable("COR64_SPDCAT5_QUIET") != "0";
        private static bool X64Cat5Leave(string o) => o is "mtc0" or "ctc1" or "tlbp " or "tlbwi " or "eret ";
        private static bool X64Cat5Pure(string o) => s_Cat5Quiet && o is "mfc0" or "cfc1" or "dmfc1" or "dmtc1" or "cvt.l.fmt" or "ddiv" or "ddivu"
            or "lwl" or "lwr" or "ldl" or "ldr" || (s_Cat5Quiet && s_Cat13CvtW && o == "cvt.w.fmt");
        static readonly string s_Cat4D64Env = Environment.GetEnvironmentVariable("COR64_SPDCAT4_D64"); // measurement: =shift / =mul only
        internal static readonly bool s_Cat4D64 = Environment.GetEnvironmentVariable("COR64_SPDCAT4_D64") != "0";
        private static int X64LoadKind(in DecodedInstruction d) => !s_RecInlineLoad ? -1 : d.Op.Op switch { "lb" => 0, "lbu" => 1, "lh" => 2, "lhu" => 3, "lw" => 4, "lwu" => 5, "ld" => 6, _ => -1 };
        private static int X64StoreSize(in DecodedInstruction d) => !s_RecInlineLoad || s_WatchAddr >= 0 ? -1 : d.Op.Op switch { "sb" => 1, "sh" => 2, "sw" => 4, "sd" => 8, _ => -1 };
        private static bool X64Mem(in DecodedInstruction d) => X64LoadKind(d) >= 0 || X64StoreSize(d) > 0;
        private static bool X64Branch(in DecodedInstruction d)
        {
            if (d.Op.XferTarget == RegBoundType.Cp1) return false;
            switch (d.Op.Op) {
                case "j": case "jal": case "jr": case "jalr":
                case "beq": case "bne": case "blez": case "bgtz": case "bltz": case "bgez":
                case "beql": case "bnel": case "blezl": case "bgtzl": case "bltzl": case "bgezl":
                case "bltzal": case "bgezal": case "bltzall": case "bgezall":
                    return true;
            }
            return false;
        }

        // [climb4 x64_emit] COR64_X64_FPU=0: blocks with FPU ops stay IL. On: lwc1/ldc1/swc1/sdc1, mtc1/mfc1 and
        // add/sub/mul/div.{s,d} on the raw FPR path (one Status test: CU1 set and FR equal to the compiled mode, else the
        // handler path), as the IL's COR64_CPUF_RAW code.
        public static readonly bool s_X64Free = Environment.GetEnvironmentVariable("COR64_X64_FREE") != "0";
        // measurement / test only: COR64_X64_CLEARTEST=N drops every compiled CPU block every N RunSteps calls (exercises FREE)
        private static readonly int s_ClearTest = int.TryParse(Environment.GetEnvironmentVariable("COR64_X64_CLEARTEST"), out var ct) ? ct : 0;
        [NonSerialized] private int m_ClearTestCnt;
        private void X64ClearTest() { if (++m_ClearTestCnt >= s_ClearTest) { m_ClearTestCnt = 0; RecClear(); } }
        private static readonly bool s_DslowCu1 = Environment.GetEnvironmentVariable("COR64_X64_DSLOW_CU1") != "0";
        public static readonly bool s_X64Fpu = Environment.GetEnvironmentVariable("COR64_X64_FPU") != "0";
        private static int X64FpuMemSize(in DecodedInstruction d) => !s_RecInlineFpuMem || s_WatchAddr >= 0 ? 0 : d.Op.Op switch { "lwc1" or "swc1" => 4, "ldc1" or "sdc1" => 8, _ => 0 };
        private static bool X64FpuXfer(in DecodedInstruction d) => s_RecInlineFpuMem && (d.Op.Op == "mtc1" || d.Op.Op == "mfc1");
        private static int X64FpuArith(in DecodedInstruction d) => !s_RecInlineFpuArith || (d.Format != FpuValueType.FSingle && d.Format != FpuValueType.FDouble) ? -1
            : d.Op.Op switch { "add.fmt" => 0, "sub.fmt" => 1, "mul.fmt" => 2, "div.fmt" => 3, _ => -1 };
        // [fpu_native] COR64_FPU_NATIVE_CVT=0: blocks with trunc.w / cvt.s / cvt.d / neg / c.cond (the IL's COR64_FPUCVT_INLINE
        // subset) stay IL. COR64_FPU_NATIVE_BC1=0: blocks with bc1f/bc1t(l) stay IL. Both mirror the IL's raw-path code.
        public static readonly bool s_X64Cvt = Environment.GetEnvironmentVariable("COR64_FPU_NATIVE_CVT") != "0";
        public static readonly bool s_X64DualFr = Environment.GetEnvironmentVariable("COR64_FPU_NATIVE_DUALFR") != "0";
        public static readonly bool s_X64Bc1 = Environment.GetEnvironmentVariable("COR64_FPU_NATIVE_BC1") != "0";
        // [fpu_native] COR64_FPU_NATIVE_MISC=0: mov.fmt / abs.fmt / sqrt.fmt (S, D) stay IL (their handlers' expressions).
        // COR64_FPU_NATIVE_DELAY=0: FPU arithmetic/convert ops in a delay slot keep the block IL.
        public static readonly bool s_X64Misc = Environment.GetEnvironmentVariable("COR64_FPU_NATIVE_MISC") != "0";
        public static readonly bool s_X64FDelay = Environment.GetEnvironmentVariable("COR64_FPU_NATIVE_DELAY") != "0";
        private static int X64Misc(in DecodedInstruction d) => !s_X64Misc || (d.Inst.inst >> 26) != 0x11 || (d.Format != FpuValueType.FSingle && d.Format != FpuValueType.FDouble) ? 0
            : d.Op.Op switch { "mov.fmt" => 6, "abs.fmt" => 7, "sqrt.fmt" => 8, _ => 0 };
        private static int X64Cvt(in DecodedInstruction d) => s_X64Cvt && s_FpuCvt ? CvtKind(d) : 0;
        private static bool IsBc1(in DecodedInstruction d) => d.Op.XferTarget == RegBoundType.Cp1 && d.Op.Op is "bc1f" or "bc1t" or "bc1fl" or "bc1tl";
        // [speed_cat8] COR64_SPDCAT8_OVF (default on; =0 off): add/addi/sub (trapping 32-bit) go native: the 32-bit op sets OF,
        // an overflow leaves through the op's slow exit (XSlow/XDSlow: the handler raises the Overflow exception) before
        // anything is written; else the sign-extended result is stored, as Add32/Sub32. Iggy, Top Gear Rally 2, Re-Volt rejects.
        internal static readonly bool s_Cat8Ovf = Environment.GetEnvironmentVariable("COR64_SPDCAT8_OVF") != "0";
        private static bool X64Ovf(in DecodedInstruction d) => s_Cat8Ovf && d.Op.Op is "add" or "addi" or "sub";
        private bool X64RawOn => s_X64Fpu && s_CpufRaw && s_WatchAddr < 0 && ReferenceEquals(StateR4000I.FPR.StatusReg, Cop0.StatusReg);

        private static bool X64Global => s_X64 && s_Slim && !s_OpMix && !s_CpufCeil && !s_CvtCeil && s_RecInlineBranch;

        private string X64Supported(CpuRecBlock b)
        {
            int n = b.Insts.Length;
            bool raw = X64RawOn;
            bool inDelay = false;
            for (int i = 0; i < n; i++) {
                var d = b.Insts[i];
                if (inDelay) {
                    if (!(X64Inline(d) || (s_Cat13Brk && d.Op.Op == "break") || (raw && X64Cat13Fix(d) != 0) || (s_DelayFast && (X64Mem(d) || X64Ovf(d) || (raw && (X64FpuMemSize(d) > 0 || X64FpuXfer(d) || (s_X64FDelay && (X64FpuArith(d) >= 0 || X64Cvt(d) != 0 || X64Misc(d) != 0)))))))) return d.Op.Op;
                    inDelay = false;
                    continue;
                }
                if (X64Br(d)) {
                    if (!(X64Branch(d) || (raw && s_X64Bc1 && s_IhBc1 && IsBc1(d))) || (i + 1 >= n && !s_X64TailBr) || (i + 1 < n && IsCpuBranch(b.Insts[i + 1]))) return d.Op.Op;
                    inDelay = true;
                    continue;
                }
                if (!X64Inline(d) && !X64Mem(d) && !X64Ovf(d) && !X64Generic(d) && !(raw && (X64FpuMemSize(d) > 0 || X64FpuXfer(d) || X64FpuArith(d) >= 0 || X64Cvt(d) != 0 || X64Misc(d) != 0 || X64Cat13Fix(d) != 0))) return d.Op.Op;
            }
            return null;
        }

        // Returns the native entry point, or IntPtr.Zero to compile the block to IL. Sets b.DelayEntry/b.MinLoop as RecCompile does.
        private unsafe IntPtr X64Compile(CpuRecBlock b)
        {
            if (!X64Global) return IntPtr.Zero;
            string why = X64Supported(b);
            if (why != null) {
                lock (X64RejectOps) { X64RejectOps.TryGetValue(why, out long c); X64RejectOps[why] = c + 1; }
                System.Threading.Interlocked.Increment(ref X64Rejected);
                return IntPtr.Zero;
            }
            X64Offsets();
            if (X64RawOn) m_CpufSr = StateR4000I.FPR.StatusReg;
            bool c0Native = s_Cat9C0 && m_CpufSr != null && ReferenceEquals(m_CpufSr, Cop0.StatusReg); // [speed_cat9]
            bool miOn = s_Cat12Mi && PhysicalMemory?.MIRegs != null; // [speed_cat12]
            bool rawOn11 = X64RawOn && c0Native; // [speed_cat11] cvt.l.d needs the raw FPR path
            int n = b.Insts.Length;
            long hiA = (long)State.GetHiRef();
            var a = new X64Asm();
            const R CTX = R.Rbx, G = R.Rsi, RAM = R.Rdi, OBJ = R.R12, CAD = R.R13, PT = R.R14, PCN = R.R15;
            M C(int off) => new M(CTX, off);
            M Gm(int r) => new M(G, r * 8);
            M O(int off) => new M(OBJ, off);
            // [jit_cost1] COR64_X64_FWD: inside a batched run, the guest register the previous op stored is still in a host
            // register; a load of it right after copies the host register instead of re-reading the GPR file.
            bool fwdOn = false; int fwdReg = -1; R fwdHost = R.Rax;
            bool Fwd(R d, int r, bool w)
            {
                if (fwdOn && fwdReg > 0 && r == fwdReg) { if (w) { if (d != fwdHost) a.MovRR(d, fwdHost); } else a.MovRR(d, fwdHost, false); return true; }
                if (d == fwdHost) fwdReg = -1;
                return false;
            }
            void LdG(R d, int r) { if (Fwd(d, r, true)) return; if (r == 0) a.MovRI(d, 0); else a.MovRM(d, Gm(r)); }
            void LdG32(R d, int r) { if (Fwd(d, r, false)) return; if (r == 0) a.MovRI(d, 0); else a.MovRM(d, Gm(r), false); }
            void StG(int r, R s) { if (r != 0) { a.MovMR(Gm(r), s); if (fwdOn) { fwdReg = r; fwdHost = s; } } }
            void SetPc(long pc) { a.MovRI(R.Rax, pc); a.MovMR(O(s_OffPc), R.Rax); }

            var exitCommon = a.NewLabel(); var budgetStub = a.NewLabel(); var checkStub = a.NewLabel(); var trap = a.NewLabel();
            var entryTab = a.NewLabel(); var resumeTab = a.NewLabel();
            var step = new XLabel[n]; var resume = new XLabel[n]; var after = new XLabel[n];
            for (int i = 0; i < n; i++) { step[i] = a.NewLabel(); resume[i] = a.NewLabel(); }
            var cold = new System.Collections.Generic.List<Action>();
            void ExitCode(int code, int k) { a.MovMI32(C(CK), k, false); a.MovRI(R.Rax, code); a.Jmp(exitCommon); }
            XLabel ColdExit(int code, int k) { var l = a.NewLabel(); cold.Add(() => { a.Bind(l); ExitCode(code, k); }); return l; }
            void Done(long lastPc, int k) { a.MovRI(R.Rax, lastPc); a.MovMR(C(CAux), R.Rax); ExitCode(XDone, k); }
            bool link = s_X64Link;
            int xid = link ? XIdAlloc(b) : -1;
            // leave after step k-1 (last instruction at lastPc) for the static target pc (PC already set): through the
            // link slot when it holds a block, else as Done (xexit: as the fall-off XExit at k, which also sets PC)
            // dyn: jr/jalr (target in CTgt, compared with the slot's last target)
            void LinkOrDone(long lastPc, int k, long target, bool xexit, bool dyn = false)
            {
                if (!link || (dyn && !s_X64LinkJr)) { if (xexit) ExitCode(XExit, k); else Done(lastPc, k); return; }
                int ways = dyn ? s_X64LinkWays : 1;
                long* slot = SlotAlloc(32 + 32 * ways) + 4; slot[2] = target;
                // one page: dyn sites keep the source page in s[3] bits 8+ (-1 = no same-page skip)
                bool onePage = s_X64LinkSamePage && (((uint)b.StartPc ^ (uint)(b.StartPc + 4 * b.Insts.Length - 1)) >> 12) == 0;
                long srcPage = onePage ? (long)((uint)b.StartPc >> 12) : 0xFFFFFFFFFL;
                slot[-1] = dyn ? (ways | (srcPage << 8)) : 0;
                var miss = a.NewLabel(); var plain = a.NewLabel();
                a.AluMI(X64Asm.Alu8.Cmp, C(CEnd), k, false); a.Jcc(Cc.LE, plain); // batch budget used up: no next block to run
                // same-page links (slot key -2 at jr/jalr sites) skip the epoch compare while the epoch still equals the one
                // this native call started with (ctx Ep0: the first block was looked up on that translation); NMI pending
                // (MapKey -> -1) or a TLB/ASID change inside the call falls back to the slot's own epoch
                bool samePageStatic = onePage && !dyn && ((uint)target >> 30) != 2 && (((uint)target ^ (uint)b.StartPc) >> 12) == 0;
                a.MovRI(R.Rcx, (long)slot);
                if (dyn) {
                    // ways are filled front to back: an empty way ends the search
                    var go = a.NewLabel();
                    a.MovRM(R.R9, C(CTgt)); a.MovRM(R.R8, C(CMapKey)); a.MovRR(R.R10, R.Rcx);
                    for (int w = 0; w < ways; w++) {
                        var next = w + 1 < ways ? a.NewLabel() : miss; int o = 32 * w;
                        if (w > 0) a.AluRI(X64Asm.Alu8.Add, R.R10, 32);
                        a.MovRM(R.Rax, new M(R.Rcx, o)); a.Alu(X64Asm.Alu8.Or, R.Rax, R.Rax); a.Jcc(Cc.E, miss);
                        a.AluRM(X64Asm.Alu8.Cmp, R.R9, new M(R.Rcx, o + 16)); a.Jcc(Cc.NE, next);
                        a.MovRM(R.Rdx, new M(R.Rcx, o + 8)); a.Alu(X64Asm.Alu8.Or, R.Rdx, R.Rdx); a.Jcc(Cc.E, go);
                        a.AluRM(X64Asm.Alu8.Cmp, R.Rdx, new M(R.R8, 0)); a.Jcc(Cc.E, go);
                        if (onePage) {
                            a.AluRI(X64Asm.Alu8.Cmp, R.Rdx, -2); a.Jcc(Cc.NE, next);
                            a.MovRM(R.Rdx, new M(R.R8, 0)); a.AluRM(X64Asm.Alu8.Cmp, R.Rdx, C(CEp0)); a.Jcc(Cc.E, go);
                        }
                        if (w + 1 < ways) { a.Jmp(next); a.Bind(next); } else a.Jmp(miss);
                    }
                    a.Bind(go);
                    a.MovRM(R.R9, new M(R.R10, 24));
                }
                else {
                    a.MovRM(R.Rax, new M(R.Rcx, 0)); a.Alu(X64Asm.Alu8.Or, R.Rax, R.Rax); a.Jcc(Cc.E, miss);
                    if (((uint)target >> 30) != 2) {
                        var ok = a.NewLabel();
                        a.MovRM(R.R8, C(CMapKey));
                        if (samePageStatic) { a.MovRM(R.Rdx, new M(R.R8, 0)); a.AluRM(X64Asm.Alu8.Cmp, R.Rdx, C(CEp0)); a.Jcc(Cc.E, ok); }
                        a.MovRM(R.Rdx, new M(R.Rcx, 8)); a.AluRM(X64Asm.Alu8.Cmp, R.Rdx, new M(R.R8, 0)); a.Jcc(Cc.NE, miss);
                        a.Bind(ok);
                    }
                    a.MovRM(R.R9, new M(R.Rcx, 24));
                }
                if (xexit) { a.MovRI(R.Rdx, target); a.MovMR(O(s_OffPc), R.Rdx); }
                a.MovRI(R.Rdx, lastPc); a.MovMR(O(0), R.Rdx);
                a.MovRI(R.Rdx, k); a.AluRM(X64Asm.Alu8.Sub, R.Rdx, C(CBasis), false); a.AluMR(X64Asm.Alu8.Add, C(CAcc), R.Rdx, false);
                a.AluMI(X64Asm.Alu8.Sub, C(CEnd), k, false);
                a.MovMI32(C(CBasis), 0, false); a.MovMI32(C(CLap), 0, false); a.MovMI32(C(CLast), -1, false);
                a.JmpR(R.Rax);
                a.Bind(miss);
                a.MovMR(C(CLink), R.Rcx);
                a.Bind(plain);
                if (xexit) ExitCode(XExit, k); else Done(lastPc, k);
            }

            // prologue
            foreach (var r in new[] { R.Rbx, R.Rbp, R.Rsi, R.Rdi, R.R12, R.R13, R.R14, R.R15 }) a.Push(r);
            a.MovRR(CTX, R.Rcx);
            a.MovRM(G, C(CGpr)); a.MovRM(RAM, C(CRam)); a.MovRM(OBJ, C(CObj)); a.MovRM(R.Rbp, C(CSr));
            a.MovRM(CAD, C(CCad), false); a.MovRM(PT, C(CPt), false); a.MovRM(PCN, C(CPcn), false);
            a.MovRM(R.Rax, C(CMode), false);
            a.LeaRip(R.Rcx, entryTab);
            a.JmpM(new M(R.Rcx, R.Rax, 8, 0));

            // [rdp_readback] rax = phys: a page the in-flight RDP work may write (load) or touch (store) takes the slow path,
            // whose memory-controller access waits for that work. Clobbers rcx, rdx.
            void GuardPage(bool load, XLabel slow)
            {
                if (!cor64.Rdp.RdpGuard.Enabled) return;
                a.MovRR(R.Rcx, R.Rax, false); a.ShiftI(X64Asm.Sh.Shr, R.Rcx, cor64.Rdp.RdpGuard.PageShift, false);
                a.MovRI(R.Rdx, (long)(load ? cor64.Rdp.RdpGuard.TableW : cor64.Rdp.RdpGuard.TableA));
                a.CmpMI8(new M(R.Rdx, R.Rcx, 1, 0), 0); a.Jcc(Cc.NE, slow);
            }

            // [session_leak] COR64_TLB_INLINE: eax = a mapped virtual address (not KSEG0/1). Probes the TLB translation
            // cache (TLBCache fast cache: the same hit TryTranslate would take) and on a hit continues at `phys` with
            // eax = the physical address; a miss, or a store to a page not cached dirty, takes `slow` as before.
            // Clobbers rcx, rdx, r10, r11.
            XLabel MappedCold(XLabel slow, bool store, XLabel phys)
            {
                if (!s_TlbInline) return slow;
                b.UsesTlb = true;
                var l = a.NewLabel();
                ulong* tag = TLB.FcTagPtr; uint* val = TLB.FcValPtr; ulong* khi = TLB.FcKeyHiPtr;
                cold.Add(() => {
                    a.Bind(l);
                    a.MovRR(R.Rcx, R.Rax, false); a.ShiftI(X64Asm.Sh.Shr, R.Rcx, 12, false);
                    a.MovRR(R.Rdx, R.Rcx, false); a.AluRI(X64Asm.Alu8.And, R.Rdx, cor64.Mips.R4300I.TLB.TLBCache.FcMask, false);
                    a.MovRI(R.R10, (long)khi); a.AluRM(X64Asm.Alu8.Or, R.Rcx, new M(R.R10, 0));
                    a.MovRI(R.R10, (long)tag); a.AluRM(X64Asm.Alu8.Cmp, R.Rcx, new M(R.R10, R.Rdx, 8, 0)); a.Jcc(Cc.NE, slow);
                    a.MovRI(R.R10, (long)val); a.MovRM(R.R11, new M(R.R10, R.Rdx, 4, 0), false);
                    if (store) { a.TestRI(R.R11, 1); a.Jcc(Cc.E, slow); }
                    a.AluRI(X64Asm.Alu8.And, R.R11, unchecked((int)0xFFFFF000u), false);
                    a.AluRI(X64Asm.Alu8.And, R.Rax, 0xFFF, false);
                    a.Alu(X64Asm.Alu8.Or, R.Rax, R.R11, false);
                    a.Jmp(phys);
                });
                return l;
            }

            // one step's fast path for a load/store: falls through on success, jumps to slow otherwise
            void EmitMem(in DecodedInstruction d, XLabel slow)
            {
                int lk = X64LoadKind(d), sk = X64StoreSize(d);
                int size = lk >= 0 ? (lk switch { 0 or 1 => 1, 2 or 3 => 2, 4 or 5 => 4, _ => 8 }) : sk;
                LdG32(R.Rax, d.Source); fwdReg = -1;
                a.AluRI(X64Asm.Alu8.Add, R.Rax, (int)(short)d.Immediate, false);
                a.MovRR(R.Rcx, R.Rax, false); a.ShiftI(X64Asm.Sh.Shr, R.Rcx, 30, false);
                var physL = a.NewLabel();
                a.AluRI(X64Asm.Alu8.Cmp, R.Rcx, 2, false); a.Jcc(Cc.NE, MappedCold(slow, lk < 0, physL));
                a.AluRI(X64Asm.Alu8.And, R.Rax, 0x1FFFFFFF, false);
                a.Bind(physL);
                bool mi = miOn && (lk == 4 || lk == 5 || sk == 4); // [speed_cat12]
                var miJoin = mi ? a.NewLabel() : null;
                if (mi) {
                    var miL = a.NewLabel(); var sl0 = slow; int rt = d.Target; bool sext = lk == 4, st = sk == 4;
                    a.AluRI(X64Asm.Alu8.Cmp, R.Rax, 0x800000 - size, false); a.Jcc(Cc.A, miL);
                    cold.Add(() => {
                        a.Bind(miL);
                        if (st) {
                            a.AluRI(X64Asm.Alu8.Cmp, R.Rax, 0x0430000C, false); a.Jcc(Cc.NE, sl0);
                            if (rt == 0) a.MovRI(R.Rcx, 0); else a.MovRM(R.Rcx, Gm(rt), false);
                            a.MovRR(R.Rdx, R.Rcx, false); a.AluRI(X64Asm.Alu8.And, R.Rdx, 0x555, false);           // clear pairs (bit 0)
                            a.ShiftI(X64Asm.Sh.Shr, R.Rcx, 1, false); a.AluRI(X64Asm.Alu8.And, R.Rcx, 0x555, false);
                            a.MovRR(R.R8, R.Rdx, false); a.Not(R.R8, false); a.Alu(X64Asm.Alu8.And, R.Rcx, R.R8, false); // set pairs (== 2)
                            foreach (var rr in new[] { R.Rdx, R.Rcx }) { // even bits -> bits 0..5
                                a.MovRR(R.R8, rr, false); a.ShiftI(X64Asm.Sh.Shr, R.R8, 1, false); a.Alu(X64Asm.Alu8.Or, rr, R.R8, false); a.AluRI(X64Asm.Alu8.And, rr, 0x333, false);
                                a.MovRR(R.R8, rr, false); a.ShiftI(X64Asm.Sh.Shr, R.R8, 2, false); a.Alu(X64Asm.Alu8.Or, rr, R.R8, false); a.AluRI(X64Asm.Alu8.And, rr, 0xF0F, false);
                                a.MovRR(R.R8, rr, false); a.ShiftI(X64Asm.Sh.Shr, R.R8, 4, false); a.Alu(X64Asm.Alu8.Or, rr, R.R8, false); a.AluRI(X64Asm.Alu8.And, rr, 0xFF, false);
                            }
                            a.MovRM(R.R11, C(CMiM)); a.MovRM(R.Rax, new M(R.R11, 0), false);                   // old mask
                            a.Not(R.Rdx, false); a.Alu(X64Asm.Alu8.And, R.Rdx, R.Rax, false); a.Alu(X64Asm.Alu8.Or, R.Rdx, R.Rcx, false); // new mask
                            a.MovRM(R.R8, C(CMiI)); a.MovRM(R.R8, new M(R.R8, 0), false);
                            a.MovRR(R.Rcx, R.Rdx, false); a.Alu(X64Asm.Alu8.Xor, R.Rcx, R.Rax, false); a.Alu(X64Asm.Alu8.And, R.Rcx, R.R8, false);
                            a.Jcc(Cc.NE, sl0);                                                                  // MiPending would change
                            a.MovMR(new M(R.R11, 0), R.Rdx, false);
                        }
                        else {
                            var isM = a.NewLabel(); var rd = a.NewLabel();
                            a.AluRI(X64Asm.Alu8.Cmp, R.Rax, 0x0430000C, false); a.Jcc(Cc.E, isM);
                            a.AluRI(X64Asm.Alu8.Cmp, R.Rax, 0x04300008, false); a.Jcc(Cc.NE, sl0);
                            a.MovRM(R.Rcx, C(CMiI)); a.Jmp(rd);
                            a.Bind(isM); a.MovRM(R.Rcx, C(CMiM));
                            a.Bind(rd);
                            if (sext) a.MovsxdRM(R.Rcx, new M(R.Rcx, 0)); else a.MovRM(R.Rcx, new M(R.Rcx, 0), false);
                        }
                        a.Jmp(miJoin);
                    });
                }
                else { a.AluRI(X64Asm.Alu8.Cmp, R.Rax, 0x800000 - size, false); a.Jcc(Cc.A, slow); }
                if (size > 1) { a.TestRI(R.Rax, size - 1); a.Jcc(Cc.NE, slow); }
                GuardPage(lk >= 0, slow);
                var m = new M(RAM, R.Rax, 1, 0);
                if (lk >= 0) {
                    switch (lk) {
                        case 0: a.AluRI(X64Asm.Alu8.Xor, R.Rax, 3, false); a.Movsx8RM(R.Rcx, m); break;
                        case 1: a.AluRI(X64Asm.Alu8.Xor, R.Rax, 3, false); a.Movzx8RM(R.Rcx, m); break;
                        case 2: a.AluRI(X64Asm.Alu8.Xor, R.Rax, 2, false); a.Movsx16RM(R.Rcx, m); break;
                        case 3: a.AluRI(X64Asm.Alu8.Xor, R.Rax, 2, false); a.Movzx16RM(R.Rcx, m); break;
                        case 4: a.MovsxdRM(R.Rcx, m); break;
                        case 5: a.MovRM(R.Rcx, m, false); break;
                        default:
                            a.MovRM(R.Rcx, m, false); a.ShiftI(X64Asm.Sh.Shl, R.Rcx, 32);
                            a.MovRM(R.Rdx, new M(RAM, R.Rax, 1, 4), false); a.Alu(X64Asm.Alu8.Or, R.Rcx, R.Rdx);
                            break;
                    }
                    if (mi) a.Bind(miJoin);
                    StG(d.Target, R.Rcx);
                }
                else {
                    LdG(R.Rcx, d.Target);
                    switch (sk) {
                        case 1: a.AluRI(X64Asm.Alu8.Xor, R.Rax, 3, false); a.MovMR8(m, R.Rcx); break;
                        case 2: a.AluRI(X64Asm.Alu8.Xor, R.Rax, 2, false); a.MovMR16(m, R.Rcx); break;
                        case 4: a.MovMR(m, R.Rcx, false); break;
                        default:
                            a.MovRR(R.Rdx, R.Rcx); a.ShiftI(X64Asm.Sh.Shr, R.Rdx, 32);
                            a.MovMR(m, R.Rdx, false); a.MovMR(new M(RAM, R.Rax, 1, 4), R.Rcx, false);
                            break;
                    }
                    if (mi) a.Bind(miJoin);
                }
            }

            // FPU ops on the raw FPR path (rbp = &Status): falls through on success, jumps to slow otherwise
            int lazyBr = -1; // [n64_speed] lever 2: index of the branch whose fields are not written yet (its delay slot is being emitted)
            bool srOn = false, srValid = false; // [n64_speed] lever 3 state (only inside a segment's hot path)
            var fpuSt = StateR4000I.FPR;
            bool frc = fpuSt.FullMode;
            long fpr = (long)fpuSt.RawPointer;
            const uint srMask = (uint)(StatusRegister.StatusFlags.UsableCop1 | StatusRegister.StatusFlags.AdditonalFPU);
            uint srWant = (uint)StatusRegister.StatusFlags.UsableCop1 | (frc ? (uint)StatusRegister.StatusFlags.AdditonalFPU : 0u);
            int DA(int r) => (frc ? r : r & ~1) * 8;
            int SA(int r) => frc ? r * 8 : ((r & ~1) + r) * 4;
            // [fpu_native] COR64_FPU_NATIVE_DUALFR: the other FR mode gets its own copy of the op (cold), so a game that runs
            // threads in both FR modes (indy, naboo) stays native. The FPR layout depends only on Status.FR (FpuState).
            void EmitFpu(in DecodedInstruction d, XLabel slow)
            {
                if (X64Ovf(d)) { // [speed_cat8]
                    int dst = d.Op.Op == "addi" ? d.Target : d.Destination;
                    LdG32(R.Rax, d.Source);
                    if (d.Op.Op == "addi") a.AluRI(X64Asm.Alu8.Add, R.Rax, (int)(short)d.Immediate, false);
                    else { LdG32(R.Rcx, d.Target); a.Alu(d.Op.Op == "add" ? X64Asm.Alu8.Add : X64Asm.Alu8.Sub, R.Rax, R.Rcx, false); }
                    a.Jcc(Cc.O, slow);
                    if (dst != 0) { a.MovsxdRR(R.Rax, R.Rax); StG(dst, R.Rax); }
                    return;
                }
                a.MovRI(R.R8, fpr);
                if (srOn && srValid) { EmitFpuBody(d, slow); return; } // [n64_speed] lever 3: tested by an earlier op of this segment
                a.MovRM(R.Rax, new M(R.Rbp, 0), false);
                a.AluRI(X64Asm.Alu8.And, R.Rax, (int)srMask, false); a.AluRI(X64Asm.Alu8.Cmp, R.Rax, (int)srWant, false);
                if (srOn) { a.Jcc(Cc.NE, slow); EmitFpuBody(d, slow); srValid = true; return; }
                if (!s_X64DualFr) { a.Jcc(Cc.NE, slow); EmitFpuBody(d, slow); return; }
                var alt = a.NewLabel(); var done = a.NewLabel();
                a.Jcc(Cc.NE, alt);
                EmitFpuBody(d, slow);
                a.Bind(done);
                var dc = d;
                cold.Add(() => {
                    a.Bind(alt);
                    uint other = (uint)StatusRegister.StatusFlags.UsableCop1 | (frc ? 0u : (uint)StatusRegister.StatusFlags.AdditonalFPU);
                    a.AluRI(X64Asm.Alu8.Cmp, R.Rax, (int)other, false); a.Jcc(Cc.NE, slow);
                    frc = !frc;
                    EmitFpuBody(dc, slow);
                    frc = !frc;
                    a.Jmp(done);
                });
            }
            void EmitFpuBody(in DecodedInstruction d, XLabel slow)
            {
                int fsz = X64FpuMemSize(d);
                if (fsz > 0) {
                    int ft = d.FloatTarget;
                    int fo = fsz == 4 ? SA(ft) : DA(ft);
                    LdG32(R.Rax, d.Source);
                    a.AluRI(X64Asm.Alu8.Add, R.Rax, (int)(short)d.Immediate, false);
                    a.MovRR(R.Rcx, R.Rax, false); a.ShiftI(X64Asm.Sh.Shr, R.Rcx, 30, false);
                    var physL = a.NewLabel();
                    a.AluRI(X64Asm.Alu8.Cmp, R.Rcx, 2, false); a.Jcc(Cc.NE, MappedCold(slow, d.Op.Op[0] == 's', physL));
                    a.AluRI(X64Asm.Alu8.And, R.Rax, 0x1FFFFFFF, false);
                    a.Bind(physL);
                    a.AluRI(X64Asm.Alu8.Cmp, R.Rax, 0x800000 - fsz, false); a.Jcc(Cc.A, slow);
                    a.TestRI(R.Rax, fsz - 1); a.Jcc(Cc.NE, slow);
                    GuardPage(d.Op.Op[0] != 's', slow);
                    var m = new M(RAM, R.Rax, 1, 0); var m4 = new M(RAM, R.Rax, 1, 4); var f = new M(R.R8, fo);
                    bool store = d.Op.Op[0] == 's';
                    if (store) {
                        if (fsz == 4) { a.MovRM(R.Rcx, f, false); a.MovMR(m, R.Rcx, false); }
                        else { a.MovRM(R.Rcx, f); a.MovRR(R.Rdx, R.Rcx); a.ShiftI(X64Asm.Sh.Shr, R.Rdx, 32); a.MovMR(m, R.Rdx, false); a.MovMR(m4, R.Rcx, false); }
                    }
                    else {
                        if (fsz == 4) { a.MovRM(R.Rcx, m, false); a.MovMR(f, R.Rcx, false); }
                        else { a.MovRM(R.Rcx, m, false); a.ShiftI(X64Asm.Sh.Shl, R.Rcx, 32); a.MovRM(R.Rdx, m4, false); a.Alu(X64Asm.Alu8.Or, R.Rcx, R.Rdx); a.MovMR(f, R.Rcx); }
                    }
                    return;
                }
                if (d.Op.Op == "dmfc1") { // [speed_cat10] dmfc1: ReadScalarDword (the FR layout), 64-bit GPR write
                    RegTransferGprHelper(d, out int gs1, out int gt1);
                    if (gt1 != 0) { a.MovRM(R.Rcx, new M(R.R8, DA(d.FloatSource))); StG(gt1, R.Rcx); }
                    return;
                }
                if (X64FpuXfer(d)) {
                    RegTransferGprHelper(d, out int gs, out int gt);
                    a.CmpMI8(O(s_OffEnableCp1), 0); a.Jcc(Cc.E, slow); // RecXfer1: !EnableCp1 -> handler path
                    var f = new M(R.R8, SA(d.FloatSource));
                    if (d.Op.Op == "mtc1") { LdG32(R.Rcx, gs); a.MovMR(f, R.Rcx, false); }
                    else if (gt != 0) { a.MovsxdRM(R.Rcx, f); StG(gt, R.Rcx); }
                    return;
                }
                int cvk = X64Cvt(d);
                if (cvk == 0) cvk = X64Misc(d);
                if (cvk == 0 && s_Cat11CvtL && d.Op.Op == "cvt.l.fmt" && d.Format == FpuValueType.FDouble) cvk = 9; // [speed_cat11]
                if (cvk == 0) cvk = X64Cat13Fix(d); // [speed_cat13]
                if (cvk == 0 && s_Cat13CvtW && d.Op.Op == "cvt.w.fmt" && (d.Format == FpuValueType.FSingle || d.Format == FpuValueType.FDouble)) cvk = 10; // [speed_cat13]
                if (cvk != 0) {
                    // [fpu_native] the IL's cvk cases (CpuRecompiler raw path), same results bit for bit
                    bool sd = d.Format == FpuValueType.FDouble;
                    int fd = d.FloatDest, fs = d.FloatSource;
                    var src = new M(R.R8, sd ? DA(fs) : SA(fs));
                    void ZeroHi() { if (frc) a.MovMI32(new M(R.R8, SA(fd) + 4), 0, false); }
                    switch (cvk) {
                        case 1: { // trunc.w: cvtts?2si (64-bit); a value outside int32 (or NaN) -> int.MaxValue, as CvtTruncW
                            a.Sse2RM(sd ? 0xF2 : 0xF3, 0x2C, (int)R.Rax, src, true);
                            a.MovsxdRR(R.Rcx, R.Rax);
                            var ok = a.NewLabel();
                            a.Alu(X64Asm.Alu8.Cmp, R.Rcx, R.Rax); a.Jcc(Cc.E, ok);
                            a.MovRI(R.Rax, int.MaxValue);
                            a.Bind(ok);
                            a.MovMR(new M(R.R8, SA(fd)), R.Rax, false);
                            break;
                        }
                        case 2: // cvt.s from D (cvtsd2ss) or W (cvtsi2ss)
                            if (sd) a.Sse2RM(0xF2, 0x5A, 0, src); else a.Sse2RM(0xF3, 0x2A, 0, src);
                            a.SseRM(false, X64Asm.Sse.MovSt, 0, new M(R.R8, SA(fd)));
                            ZeroHi();
                            break;
                        case 3: // cvt.d from S (cvtss2sd) or W (cvtsi2sd)
                            if (d.Format == FpuValueType.FSingle) a.Sse2RM(0xF3, 0x5A, 0, src); else a.Sse2RM(0xF2, 0x2A, 0, src);
                            a.SseRM(true, X64Asm.Sse.MovSt, 0, new M(R.R8, DA(fd)));
                            break;
                        case 14: // [speed_cat13] cvt.d.l: cvtsi2sd r64 (as (double)(long))
                            a.Sse2RM(0xF2, 0x2A, 0, new M(R.R8, DA(fs)), true);
                            a.SseRM(true, X64Asm.Sse.MovSt, 0, new M(R.R8, DA(fd)));
                            break;
                        case 11: case 12: case 13: { // [speed_cat13] round.w / floor.w / ceil.w: roundsd 0 / 1 / 2, cvttsd2si r64, int32 range or slow
                            if (sd) a.SseRM(true, X64Asm.Sse.Mov, 0, src); else a.Sse2RM(0xF3, 0x5A, 0, src);
                            a.RoundsdRR(0, 0, (byte)(cvk - 11));
                            a.Cvttsd2siRR64((int)R.Rax, 0);
                            a.MovsxdRR(R.Rcx, R.Rax); a.Alu(X64Asm.Alu8.Cmp, R.Rcx, R.Rax); a.Jcc(Cc.NE, slow);
                            a.MovMR(new M(R.R8, SA(fd)), R.Rax, false);
                            break;
                        }
                        case 10: { // [speed_cat13] cvt.w.s / cvt.w.d: (double)src, RoundBy(FCSR.RM), cvttsd2si r64, int32 range or slow
                            if (sd) a.SseRM(true, X64Asm.Sse.Mov, 0, src); else a.Sse2RM(0xF3, 0x5A, 0, src);
                            a.MovRM(R.Rcx, C(CFcr)); a.MovRM(R.Rcx, new M(R.Rcx, 0), false); a.AluRI(X64Asm.Alu8.And, R.Rcx, 3, false);
                            var w1 = a.NewLabel(); var w2 = a.NewLabel(); var w3 = a.NewLabel(); var wd = a.NewLabel();
                            a.AluRI(X64Asm.Alu8.Cmp, R.Rcx, 1, false); a.Jcc(Cc.E, w1);
                            a.AluRI(X64Asm.Alu8.Cmp, R.Rcx, 2, false); a.Jcc(Cc.E, w2);
                            a.AluRI(X64Asm.Alu8.Cmp, R.Rcx, 3, false); a.Jcc(Cc.E, w3);
                            a.RoundsdRR(0, 0, 0); a.Jmp(wd);
                            a.Bind(w1); a.RoundsdRR(0, 0, 3); a.Jmp(wd);
                            a.Bind(w2); a.RoundsdRR(0, 0, 2); a.Jmp(wd);
                            a.Bind(w3); a.RoundsdRR(0, 0, 1);
                            a.Bind(wd);
                            a.Cvttsd2siRR64((int)R.Rax, 0);
                            a.MovsxdRR(R.Rcx, R.Rax); a.Alu(X64Asm.Alu8.Cmp, R.Rcx, R.Rax); a.Jcc(Cc.NE, slow);
                            a.MovMR(new M(R.R8, SA(fd)), R.Rax, false);
                            break;
                        }
                        case 9: { // [speed_cat11] cvt.l.d: RoundBy(FCSR.RM) = roundsd 0 / 3 / 2 / 1, then cvttsd2si r64
                            a.SseRM(true, X64Asm.Sse.Mov, 0, src);
                            a.MovRM(R.Rcx, C(CFcr)); a.MovRM(R.Rcx, new M(R.Rcx, 0), false); a.AluRI(X64Asm.Alu8.And, R.Rcx, 3, false);
                            var r1 = a.NewLabel(); var r2 = a.NewLabel(); var r3 = a.NewLabel(); var rd = a.NewLabel();
                            a.AluRI(X64Asm.Alu8.Cmp, R.Rcx, 1, false); a.Jcc(Cc.E, r1);
                            a.AluRI(X64Asm.Alu8.Cmp, R.Rcx, 2, false); a.Jcc(Cc.E, r2);
                            a.AluRI(X64Asm.Alu8.Cmp, R.Rcx, 3, false); a.Jcc(Cc.E, r3);
                            a.RoundsdRR(0, 0, 0); a.Jmp(rd);
                            a.Bind(r1); a.RoundsdRR(0, 0, 3); a.Jmp(rd);
                            a.Bind(r2); a.RoundsdRR(0, 0, 2); a.Jmp(rd);
                            a.Bind(r3); a.RoundsdRR(0, 0, 1);
                            a.Bind(rd);
                            a.Cvttsd2siRR64((int)R.Rax, 0);
                            a.MovRI(R.Rcx, long.MinValue); a.Alu(X64Asm.Alu8.Cmp, R.Rax, R.Rcx); a.Jcc(Cc.E, slow);
                            a.MovMR(new M(R.R8, DA(fd)), R.Rax);
                            break;
                        }
                        case 4: // neg: sign bit flip (NaN too, as the JIT's xorps)
                            if (sd) { a.MovRM(R.Rax, src); a.MovRI(R.Rcx, long.MinValue); a.Alu(X64Asm.Alu8.Xor, R.Rax, R.Rcx); a.MovMR(new M(R.R8, DA(fd)), R.Rax); }
                            else { a.MovRM(R.Rax, src, false); a.AluRI(X64Asm.Alu8.Xor, R.Rax, int.MinValue, false); a.MovMR(new M(R.R8, SA(fd)), R.Rax, false); ZeroHi(); }
                            break;
                        case 6: // mov.fmt: FR=0 mov.s copies the 32-bit register (COR64_CPUDIFF_MOVS), else the 64-bit pair/register
                            if (!sd && !frc && s_MovSFix) { a.MovRM(R.Rax, new M(R.R8, SA(fs)), false); a.MovMR(new M(R.R8, SA(fd)), R.Rax, false); }
                            else { a.MovRM(R.Rax, new M(R.R8, DA(fs))); a.MovMR(new M(R.R8, DA(fd)), R.Rax); }
                            break;
                        case 7: // abs: sign bit cleared (Math.Abs: andps/andpd)
                            if (sd) { a.MovRM(R.Rax, src); a.MovRI(R.Rcx, long.MaxValue); a.Alu(X64Asm.Alu8.And, R.Rax, R.Rcx); a.MovMR(new M(R.R8, DA(fd)), R.Rax); }
                            else { a.MovRM(R.Rax, src, false); a.AluRI(X64Asm.Alu8.And, R.Rax, int.MaxValue, false); a.MovMR(new M(R.R8, SA(fd)), R.Rax, false); ZeroHi(); }
                            break;
                        case 8: // sqrt: D sqrtsd; S (float)Math.Sqrt((double)f) = cvtss2sd, sqrtsd, cvtsd2ss
                            if (sd) a.Sse2RM(0xF2, 0x51, 0, src);
                            else { a.Sse2RM(0xF3, 0x5A, 0, src); a.Sse2RR(0xF2, 0x51, 0, 0); a.Sse2RR(0xF2, 0x5A, 0, 0); }
                            if (sd) a.SseRM(true, X64Asm.Sse.MovSt, 0, new M(R.R8, DA(fd)));
                            else { a.SseRM(false, X64Asm.Sse.MovSt, 0, new M(R.R8, SA(fd))); ZeroHi(); }
                            break;
                        default: { // c.cond (no CondUn): unordered + CondOrd -> slow (the handler raises Invalid)
                            var fl = d.Op.Flags;
                            int ftt = d.FloatTarget;
                            a.MovRI(R.Rdx, 0); // before ucomis: MovRI 0 may be an xor (flags)
                            a.SseRM(sd, X64Asm.Sse.Mov, 0, src);
                            a.Sse2RM(sd ? 0x66 : -1, 0x2E, 0, new M(R.R8, sd ? DA(ftt) : SA(ftt)));
                            bool eq = (fl & ExecutionFlags.CondEq) != 0, lt = (fl & ExecutionFlags.CondLT) != 0, gt = (fl & ExecutionFlags.CondGT) != 0;
                            bool not = (fl & ExecutionFlags.CondNot) != 0;
                            var unord = a.NewLabel(); var set = a.NewLabel();
                            a.Jcc(Cc.P, unord);
                            int m3 = (eq ? 1 : 0) | (lt ? 2 : 0) | (gt ? 4 : 0);
                            if (m3 == 0 || m3 == 7) a.MovRI(R.Rdx, (m3 == 7) != not ? 1 : 0);
                            else {
                                Cc cc = m3 switch { 1 => Cc.E, 2 => Cc.B, 4 => Cc.A, 3 => Cc.BE, 5 => Cc.AE, _ => Cc.NE };
                                if (not) cc = (Cc)((int)cc ^ 1);
                                a.SetCc(cc, R.Rdx);
                            }
                            a.Jmp(set);
                            a.Bind(unord);
                            if ((fl & ExecutionFlags.CondOrd) != 0) a.Jmp(slow);
                            else a.MovRI(R.Rdx, not ? 1 : 0);
                            a.Bind(set);
                            a.MovRM(R.Rcx, C(CFcr));
                            a.MovRM(R.Rax, new M(R.Rcx, 0), false);
                            a.AluRI(X64Asm.Alu8.And, R.Rax, ~(1 << 23), false);
                            a.ShiftI(X64Asm.Sh.Shl, R.Rdx, 23, false);
                            a.Alu(X64Asm.Alu8.Or, R.Rax, R.Rdx, false);
                            a.MovMR(new M(R.Rcx, 0), R.Rax, false);
                            break;
                        }
                    }
                    return;
                }
                {
                    int op = X64FpuArith(d);
                    bool dbl = d.Format == FpuValueType.FDouble;
                    int fd = d.FloatDest, fs = d.FloatSource, ftt = d.FloatTarget;
                    var aop = op == 0 ? X64Asm.Sse.Add : op == 1 ? X64Asm.Sse.Sub : op == 2 ? X64Asm.Sse.Mul : X64Asm.Sse.Div;
                    a.SseRM(dbl, X64Asm.Sse.Mov, 0, new M(R.R8, dbl ? DA(fs) : SA(fs)));
                    a.SseRM(dbl, aop, 0, new M(R.R8, dbl ? DA(ftt) : SA(ftt)));
                    a.SseRM(dbl, X64Asm.Sse.MovSt, 0, new M(R.R8, dbl ? DA(fd) : SA(fd)));
                    if (!dbl && frc) a.MovMI32(new M(R.R8, SA(fd) + 4), 0, false);
                }
            }

            // inline integer op (the IL's EmitInline); div/divu leave through XMulDiv and resume at after[i]
            void EmitInline(in DecodedInstruction d, int i)
            {
                int rs = d.Source, rt = d.Target, rd = d.Destination, sa = d.ShiftAmount;
                uint imm = d.Immediate;
                string op = d.Op.Op;
                void Bin32(X64Asm.Alu8 o) { if (rd == 0) return; LdG32(R.Rax, rs); LdG32(R.Rcx, rt); a.Alu(o, R.Rax, R.Rcx, false); a.MovsxdRR(R.Rax, R.Rax); StG(rd, R.Rax); }
                void Bin64(X64Asm.Alu8 o, bool not = false) { if (rd == 0) return; LdG(R.Rax, rs); LdG(R.Rcx, rt); a.Alu(o, R.Rax, R.Rcx); if (not) a.Not(R.Rax); StG(rd, R.Rax); }
                void Imm64(X64Asm.Alu8 o, int v) { if (rt == 0) return; LdG(R.Rax, rs); a.AluRI(o, R.Rax, v); StG(rt, R.Rax); }
                void Set(int dst, Cc cc, bool immForm)
                {
                    if (dst == 0) return;
                    LdG(R.Rax, rs);
                    if (!immForm) LdG(R.Rcx, rt);
                    a.MovRI(R.Rdx, 0);
                    if (immForm) a.AluRI(X64Asm.Alu8.Cmp, R.Rax, (int)(short)imm); else a.Alu(X64Asm.Alu8.Cmp, R.Rax, R.Rcx);
                    a.SetCc(cc, R.Rdx);
                    StG(dst, R.Rdx);
                }
                switch (op) {
                    case "addu": Bin32(X64Asm.Alu8.Add); return;
                    case "subu": Bin32(X64Asm.Alu8.Sub); return;
                    case "addiu":
                        if (rt == 0) return;
                        LdG32(R.Rax, rs); a.AluRI(X64Asm.Alu8.Add, R.Rax, (int)(short)imm, false); a.MovsxdRR(R.Rax, R.Rax); StG(rt, R.Rax); return;
                    case "and": Bin64(X64Asm.Alu8.And); return;
                    case "or": Bin64(X64Asm.Alu8.Or); return;
                    case "xor": Bin64(X64Asm.Alu8.Xor); return;
                    case "nor": Bin64(X64Asm.Alu8.Or, true); return;
                    case "daddu": Bin64(X64Asm.Alu8.Add); return;
                    case "andi": Imm64(X64Asm.Alu8.And, (int)(imm & 0xFFFF)); return;
                    case "ori": Imm64(X64Asm.Alu8.Or, (int)(imm & 0xFFFF)); return;
                    case "xori": Imm64(X64Asm.Alu8.Xor, (int)(imm & 0xFFFF)); return;
                    case "daddiu": Imm64(X64Asm.Alu8.Add, (int)(short)imm); return;
                    case "lui": if (rt == 0) return; a.MovRI(R.Rax, (long)(int)(imm << 16)); StG(rt, R.Rax); return;
                    case "slt": Set(rd, Cc.L, false); return;
                    case "sltu": Set(rd, Cc.B, false); return;
                    case "slti": Set(rt, Cc.L, true); return;
                    case "sltiu": Set(rt, Cc.B, true); return;
                    case "sll": if (rd == 0) return; LdG32(R.Rax, rt); a.ShiftI(X64Asm.Sh.Shl, R.Rax, sa & 63, true); a.MovsxdRR(R.Rax, R.Rax); StG(rd, R.Rax); return;
                    case "srl": if (rd == 0) return; LdG32(R.Rax, rt); a.ShiftI(X64Asm.Sh.Shr, R.Rax, sa & 63, true); a.MovsxdRR(R.Rax, R.Rax); StG(rd, R.Rax); return;
                    case "sra": {
                        if (rd == 0) return;
                        LdG(R.Rax, rt); a.MovRR(R.Rdx, R.Rax);
                        a.ShiftI(X64Asm.Sh.Shr, R.Rax, sa & 63, true);
                        a.ShiftI(X64Asm.Sh.Shl, R.Rdx, 1); a.ShiftI(X64Asm.Sh.Sar, R.Rdx, 63);           // all ones if bit 62 set
                        a.AluRI(X64Asm.Alu8.And, R.Rdx, (int)~(~0U >> (sa & 31)), false);
                        a.Alu(X64Asm.Alu8.Or, R.Rax, R.Rdx, false); a.MovsxdRR(R.Rax, R.Rax); StG(rd, R.Rax); return;
                    }
                    case "sllv": case "srlv": case "srav": {
                        if (rd == 0) return;
                        LdG32(R.Rcx, rs); a.AluRI(X64Asm.Alu8.And, R.Rcx, s_ShvMask, false);
                        if (op == "sllv") { LdG(R.Rax, rt); a.ShiftCl(X64Asm.Sh.Shl, R.Rax); }
                        else if (op == "srlv") { LdG32(R.Rax, rt); a.ShiftCl(X64Asm.Sh.Shr, R.Rax); }
                        else {
                            LdG(R.Rax, rt); a.MovRR(R.Rdx, R.Rax); a.ShiftCl(X64Asm.Sh.Shr, R.Rax);
                            a.MovRI(R.R8, 0xFFFFFFFFL); a.ShiftCl(X64Asm.Sh.Shr, R.R8, false); a.Not(R.R8, false);
                            a.ShiftI(X64Asm.Sh.Shl, R.Rdx, 1); a.ShiftI(X64Asm.Sh.Sar, R.Rdx, 63);
                            a.Alu(X64Asm.Alu8.And, R.Rdx, R.R8, false); a.Alu(X64Asm.Alu8.Or, R.Rax, R.Rdx, false);
                        }
                        a.MovsxdRR(R.Rax, R.Rax); StG(rd, R.Rax); return;
                    }
                    case "cache": return;
                    case "mflo": case "mfhi": case "mtlo": case "mthi": {
                        RegTransferGprHelper(d, out int gs, out int gt);
                        int off = op[2] == 'l' ? 8 : 0;
                        a.MovRI(R.Rcx, hiA);
                        if (op[1] == 'f') { if (gt == 0) return; a.MovRM(R.Rax, new M(R.Rcx, off)); StG(gt, R.Rax); }
                        else { LdG(R.Rax, gs); a.MovMR(new M(R.Rcx, off), R.Rax); }
                        return;
                    }
                    case "mult": case "multu": {
                        LdG32(R.Rax, rs); LdG32(R.Rcx, rt);
                        if (op == "mult") { a.MovsxdRR(R.Rax, R.Rax); a.MovsxdRR(R.Rcx, R.Rcx); }
                        a.ImulRR(R.Rax, R.Rcx);
                        a.MovRR(R.Rdx, R.Rax); a.ShiftI(X64Asm.Sh.Shr, R.Rdx, 32); a.MovsxdRR(R.Rdx, R.Rdx); a.MovsxdRR(R.Rax, R.Rax);
                        a.MovRI(R.Rcx, hiA); a.MovMR(new M(R.Rcx, 0), R.Rdx); a.MovMR(new M(R.Rcx, 8), R.Rax);
                        return;
                    }
                    case "dsll": case "dsrl": case "dsra": case "dsll32": case "dsrl32": case "dsra32": { // [speed_cat4]
                        if (rd == 0) return;
                        int n6 = (sa & 31) + (op.EndsWith("32") ? 32 : 0);
                        LdG(R.Rax, rt);
                        a.ShiftI(op[2] == 'l' && op[3] == 'l' ? X64Asm.Sh.Shl : op[3] == 'a' ? X64Asm.Sh.Sar : X64Asm.Sh.Shr, R.Rax, n6);
                        StG(rd, R.Rax); return;
                    }
                    case "dsllv": case "dsrlv": case "dsrav": { // [speed_cat4] x64 masks CL to 6 bits, as Shift64's & 0x3F
                        if (rd == 0) return;
                        LdG32(R.Rcx, rs); LdG(R.Rax, rt);
                        a.ShiftCl(op == "dsllv" ? X64Asm.Sh.Shl : op == "dsrav" ? X64Asm.Sh.Sar : X64Asm.Sh.Shr, R.Rax);
                        StG(rd, R.Rax); return;
                    }
                    case "dsubu": Bin64(X64Asm.Alu8.Sub); return; // [speed_cat4]
                    case "div" when s_Cat12Div: case "divu" when s_Cat12Div: { // [speed_cat12] RecMulDiv kinds 2 / 3, as 64-bit divides (no #DE)
                        bool sg = op == "div";
                        var zero = a.NewLabel(); var put = a.NewLabel();
                        LdG32(R.Rcx, rt); LdG32(R.Rax, rs);
                        a.TestRI(R.Rcx, -1); a.Jcc(Cc.E, zero);
                        if (sg) { a.MovsxdRR(R.Rax, R.Rax); a.MovsxdRR(R.Rcx, R.Rcx); a.Cqo(); }
                        else a.Alu(X64Asm.Alu8.Xor, R.Rdx, R.Rdx, false);
                        a.DivR(R.Rcx, sg);
                        a.MovsxdRR(R.Rax, R.Rax); a.MovsxdRR(R.Rdx, R.Rdx);
                        a.Bind(put);
                        a.MovRI(R.Rcx, hiA); a.MovMR(new M(R.Rcx, 0), R.Rdx); a.MovMR(new M(R.Rcx, 8), R.Rax);
                        cold.Add(() => {
                            a.Bind(zero);
                            a.MovsxdRR(R.Rdx, R.Rax);
                            if (sg) { a.MovRR(R.Rax, R.Rdx); a.ShiftI(X64Asm.Sh.Sar, R.Rax, 63); a.Alu(X64Asm.Alu8.Add, R.Rax, R.Rax); a.Not(R.Rax); }
                            else a.MovRI(R.Rax, -1);
                            a.Jmp(put);
                        });
                        fwdReg = -1; return;
                    }
                    case "dmultu" when s_Cat12Div && s_Cat4D64Env != "shift": { // [speed_cat12] Multiply64_Unsigned = mul r64
                        LdG(R.Rax, rs); LdG(R.Rcx, rt); a.MulR(R.Rcx);
                        a.MovRI(R.Rcx, hiA); a.MovMR(new M(R.Rcx, 0), R.Rdx); a.MovMR(new M(R.Rcx, 8), R.Rax);
                        fwdReg = -1; return;
                    }
                    case "ddivu" when s_Cat12Div: { // [speed_cat12] RecDDivU: b = 0 -> hi = a, lo = ~0; else div r64
                        var zero = a.NewLabel(); var put = a.NewLabel();
                        LdG(R.Rax, rs); LdG(R.Rcx, rt);
                        a.AluRI(X64Asm.Alu8.Cmp, R.Rcx, 0); a.Jcc(Cc.E, zero);
                        a.Alu(X64Asm.Alu8.Xor, R.Rdx, R.Rdx, false); a.DivR(R.Rcx, false);
                        a.Bind(put);
                        a.MovRI(R.Rcx, hiA); a.MovMR(new M(R.Rcx, 0), R.Rdx); a.MovMR(new M(R.Rcx, 8), R.Rax);
                        cold.Add(() => { a.Bind(zero); a.MovRR(R.Rdx, R.Rax); a.MovRI(R.Rax, -1); a.Jmp(put); });
                        fwdReg = -1; return;
                    }
                    case "dmult": case "dmultu": case "ddivu":
                    case "div": case "divu":
                        after[i] = a.NewLabel();
                        ExitCode(XMulDiv, i);
                        a.Bind(after[i]);
                        if (s_Cat4D64) fwdReg = -1; // [speed_cat4] the exit/resume clobbers the forwarded host register
                        return;
                }
                throw new InvalidOperationException("x64 inline " + op);
            }

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
                if (t > 0 && X64Br(b.Insts[t - 1])) return -1;
                return t;
            }

            // [n64_speed] lever 2: the interpreter branch fields of the lazy branch at i (from ctx Tk / Tgt), on an exit path only
            void MatBranch(int i)
            {
                var d = b.Insts[i]; string op = d.Op.Op; long pc = b.StartPc + i * 4L;
                if (op == "j" || op == "jal" || op == "jr" || op == "jalr") { a.MovRM(R.Rax, C(CTgt)); a.MovMR(O(s_OffTarget), R.Rax); a.MovMI8(O(s_OffUncond), 1); }
                else {
                    var nt0 = a.NewLabel();
                    a.MovRM(R.Rdx, C(CTk), false); a.TestRI(R.Rdx, 0xFF); a.Jcc(Cc.E, nt0);
                    a.MovRM(R.Rcx, C(CTgt)); a.MovMR(O(s_OffTarget), R.Rcx);
                    a.Bind(nt0);
                    a.MovMR8(O(s_OffCond), R.Rdx);
                }
                a.MovMI8(O(s_OffExecBD), 1);
                SetPc(pc + 4);
            }
            XLabel MatExit(int code, int k) { if (lazyBr < 0) return ColdExit(code, k); var l = a.NewLabel(); int bi = lazyBr; cold.Add(() => { a.Bind(l); MatBranch(bi); ExitCode(code, k); }); return l; }
            void EmitBranch(int i, long pc)
            {
                var d = b.Insts[i];
                string op = d.Op.Op;
                bool lazy = s_SpdLazyBr && i + 1 < n; lazyBr = lazy ? i : -1;
{ bool jj0 = op == "j" || op == "jal" || op == "jr" || op == "jalr"; bool c1 = d.Op.XferTarget == RegBoundType.Cp1;
                  // =1: jumps, likely branches and bc1f/bc1t (exact on every game measured); =9 also plain beq/bne/blez/bgtz/bltz/bgez,
                  // which changes Racer's hashes after ~1500 frames (cause not found: kept off); =2 jumps, =4 bc1, =6 likely only
                  bool on = s_SpdLazyBrM switch { 1 => jj0 || c1 || d.IsLikely(), 2 => jj0, 3 => !jj0 && !c1, 4 => c1, 5 => !jj0 && !c1 && !d.IsLikely(), 6 => !jj0 && !c1 && d.IsLikely(), 9 => true, _ => false };
                  if (!on) { lazy = false; lazyBr = -1; } }
                bool isJump = op == "j" || op == "jal" || op == "jr" || op == "jalr";
                bool likely = !isJump && d.IsLikely();
                bool link = op == "jal" || op == "jalr" || (!isJump && op.Contains("al"));
                if (isJump) {
                    a.MovMI32(C(CTk), 1, false);
                    if (op == "jr" || op == "jalr") LdG32(R.Rax, d.Source);
                    else a.MovRI(R.Rax, (long)(d.Inst.target << 2) | (pc & 0xF0000000));
                    a.MovMR(C(CTgt), R.Rax);
                    if (link) {
                        int lr = op == "jalr" && d.Destination != 0 ? d.Destination : 31;
                        a.MovRI(R.Rcx, LinkConst(pc + 8)); StG(lr, R.Rcx);
                    }
                    if (!lazy) {
                    a.MovMR(O(s_OffTarget), R.Rax);
                    a.MovMI8(O(s_OffUncond), 1);
                    }
                }
                else if (d.Op.XferTarget == RegBoundType.Cp1) {
                    // [fpu_native] bc1f/bc1t(l): CU1 clear -> the interpreter step (raises Coprocessor Unusable and leaves)
                    a.MovRM(R.Rax, new M(R.Rbp, 0), false);
                    a.TestRI(R.Rax, (int)StatusRegister.StatusFlags.UsableCop1); a.Jcc(Cc.E, ColdExit(XSlow, i));
                    a.MovRM(R.Rcx, C(CFcr)); a.MovRM(R.Rdx, new M(R.Rcx, 0), false);
                    a.ShiftI(X64Asm.Sh.Shr, R.Rdx, 23, false); a.AluRI(X64Asm.Alu8.And, R.Rdx, 1, false);
                    if (d.Op.ArithmeticType == ArithmeticOp.FALSE) a.AluRI(X64Asm.Alu8.Xor, R.Rdx, 1, false);
                    else if (d.Op.ArithmeticType != ArithmeticOp.TRUE) throw new InvalidOperationException("bc1 condition");
                    a.MovMR(C(CTk), R.Rdx, false);
                    long target = pc + ((long)(short)d.Immediate << 2) + 4;
                    a.MovRI(R.Rcx, target); a.MovMR(C(CTgt), R.Rcx);
                    if (!lazy) {
                    var nt = a.NewLabel();
                    a.TestRI(R.Rdx, 0xFF); a.Jcc(Cc.E, nt);
                    a.MovMR(O(s_OffTarget), R.Rcx);
                    a.Bind(nt);
                    a.MovMR8(O(s_OffCond), R.Rdx);
                    }
                }
                else {
                    LdG(R.Rax, d.Source);
                    Cc cc;
                    switch (d.Op.ArithmeticType) {
                        case ArithmeticOp.EQUAL: LdG(R.Rcx, d.Target); cc = Cc.E; break;
                        case ArithmeticOp.NOT_EQUAL: LdG(R.Rcx, d.Target); cc = Cc.NE; break;
                        case ArithmeticOp.GREATER_THAN: a.MovRI(R.Rcx, 0); cc = Cc.G; break;
                        case ArithmeticOp.LESS_THAN: a.MovRI(R.Rcx, 0); cc = Cc.L; break;
                        case ArithmeticOp.GREATER_THAN_OR_EQUAL: a.MovRI(R.Rcx, 0); cc = Cc.GE; break;
                        case ArithmeticOp.LESS_THAN_OR_EQUAL: a.MovRI(R.Rcx, 0); cc = Cc.LE; break;
                        default: throw new InvalidOperationException("branch condition");
                    }
                    a.MovRI(R.Rdx, 0);
                    a.Alu(X64Asm.Alu8.Cmp, R.Rax, R.Rcx);
                    a.SetCc(cc, R.Rdx);
                    a.MovMR(C(CTk), R.Rdx, false);
                    long target = pc + ((long)(short)d.Immediate << 2) + 4;
                    a.MovRI(R.Rcx, target); a.MovMR(C(CTgt), R.Rcx);
                    var nt = a.NewLabel();
                    a.TestRI(R.Rdx, 0xFF); a.Jcc(Cc.E, nt);
                    if (!lazy) a.MovMR(O(s_OffTarget), R.Rcx);
                    a.Bind(nt);
                    if (link) { a.MovRI(R.Rax, LinkConst(pc + 8)); StG(31, R.Rax); }
                    if (!lazy) a.MovMR8(O(s_OffCond), R.Rdx);
                }
                if (likely) {
                    var taken = a.NewLabel();
                    a.AluMI(X64Asm.Alu8.Cmp, C(CTk), 0, false); a.Jcc(Cc.NE, taken);
                    SetPc(pc + 8); LinkOrDone(pc, i + 1, pc + 8, false);
                    a.Bind(taken);
                }
                if (!lazy) {
                a.MovMI8(O(s_OffExecBD), 1);
                SetPc(pc + 4);
                }
            }

            // delay slots, as RecCompile computes them
            var delayEntry = new bool[n];
            if (s_RecDelayEntry) for (int i = 0; i + 1 < n; i++) { if (!X64Br(b.Insts[i])) continue; delayEntry[i + 1] = true; i++; }

            // [jit_cost1] COR64_X64_BATCH: a run of plain steps (no branch, delay slot, div or block end; no loop target
            // inside) pays its tick bookkeeping once at its head when no budget end or unskipped interrupt check falls
            // inside it; otherwise (and for entries inside the run) it runs the per-step copy (cold).
            var segLen = new int[n];
            int minLoop = int.MaxValue;
            bool curSegBr = false;
            var segEndBr = new bool[n]; var segHasBr = new bool[n]; // [speed_cat17]
            // [climb4 speed_cat17] COR64_SPDCAT17_BR (default on; =0 off): beq / bne / blez / bgtz / bltz / bgez, j / jal / jr whose
            // delay slot is a plain ALU op end a batched segment instead of running per step. Nothing between the branch and
            // its resolution can leave the block (no tick site, no slow path), so the branch state (Tk / Tgt, BU_* flags,
            // ExecuteBranchDelay, PC + 4) is never observed and is not stored; taken: PC = target, then the loop jump or the
            // link exactly as the per-step path. The segment's per-step copy (budget end, check due) keeps the old code.
            bool SegBrOk(int i)
            {
                if (!s_Cat17Br || i + 1 >= n) return false;
                var bd = b.Insts[i]; var dd = b.Insts[i + 1];
                if (bd.Op.XferTarget == RegBoundType.Cp1) return false;
                bool okb = bd.Op.Op switch { "j" or "jal" or "jr" => true, "beq" or "bne" or "blez" or "bgtz" or "bltz" or "bgez" => !bd.IsLikely(),
                    "beql" or "bnel" or "blezl" or "bgtzl" or "bltzl" or "bgezl" => s_Cat17Likely && bd.IsLikely(), _ => false };
                bool okd = dd.Op.Op is "addu" or "subu" or "addiu" or "and" or "or" or "xor" or "nor" or "daddu" or "andi" or "ori" or "xori" or "daddiu"
                    or "lui" or "slt" or "sltu" or "slti" or "sltiu" or "sll" or "srl" or "sra" or "sllv" or "srlv" or "srav";
                return okb && ((okd && X64Inline(dd)) || (s_Cat17BrMem && X64Mem(dd)) || (s_Cat17BrMem && SegFpuD(dd)));
            }
            bool SegFpuD(in DecodedInstruction dd) => s_SpdBrFpu && X64RawOn && !X64Inline(dd) && !X64Mem(dd) && !X64Generic(dd) && !X64Ovf(dd)
                && (X64FpuMemSize(dd) > 0 || X64FpuXfer(dd) || X64FpuArith(dd) >= 0 || X64Cvt(dd) != 0 || X64Misc(dd) != 0);
            if (s_X64Batch) {
                var isHead = new bool[n]; var bad = new bool[n];
                var segBr = new bool[n];
                for (int i = 0; i < n; i++) {
                    if (X64Br(b.Insts[i])) { bad[i] = true; if (i + 1 < n) bad[i + 1] = true; int lt = LoopTarget(i); if (lt >= 0) isHead[lt] = true; if (SegBrOk(i)) segBr[i] = true; i++; }
                }
                for (int i = 0; i < n; i++) { string o = b.Insts[i].Op.Op; if (o == "div" || o == "divu" || o == "dmult" || o == "dmultu" || (X64Generic(b.Insts[i]) && !(s_Cat17Seg && c0Native && X64C0Status(b.Insts[i]))) || i == n - 1) bad[i] = true; }
                // [speed_cat17] a simple branch + ALU delay slot (no loop target on the delay slot) closes the segment before it
                for (int i = 0; i < n; i++) if (segBr[i] && !isHead[i + 1]) { bad[i] = false; bad[i + 1] = false; segEndBr[i + 1] = true; }
                for (int i = 0; i < n; ) {
                    if (bad[i]) { i++; continue; }
                    int j = i; while (j < n && !bad[j] && (j == i || !isHead[j])) { j++; if (segEndBr[j - 1]) break; }
                    if (segEndBr[j - 1] && j - i >= 2 && !segEndBr[i]) segHasBr[i] = true;
                    segLen[i] = j - i; i = j;
                }
            }
            void StepTicks(int i)
            {
                int lb = lazyBr >= 0 && lazyBr == i - 1 ? lazyBr : -1; // [n64_speed] lever 2: the delay slot of a lazy branch
                if (lb < 0) { a.AluMI(X64Asm.Alu8.Cmp, C(CEnd), i, false); a.Jcc(Cc.E, budgetStub); }
                else { var bl = a.NewLabel(); cold.Add(() => { a.Bind(bl); MatBranch(lb); a.Jmp(budgetStub); }); a.AluMI(X64Asm.Alu8.Cmp, C(CEnd), i, false); a.Jcc(Cc.E, bl); }
                a.Inc(PT); a.Inc(CAD);
                a.AluRI(X64Asm.Alu8.Cmp, CAD, s_IntCheckEvery, false);
                { var t = a.NewLabel(); int kk = i; cold.Add(() => { a.Bind(t);
                    // [climb4 speed_cat16] COR64_SPDCAT16_CHK (default on; =0 off): the quiet-window skip is tested at this site and
                    // resumes with a direct jump, instead of the shared checkStub's indirect jump through resumeTab (one
                    // indirect branch with every step as a target, taken every s_IntCheckEvery steps of a loop)
                    if (s_Cat16Chk) { a.MovRI(CAD, 0); a.AluRM(X64Asm.Alu8.Cmp, PCN, C(CQuiet), false); a.Jcc(Cc.L, resume[kk]); }
                    if (lb >= 0) MatBranch(lb);
                    a.MovRI(R.Rax, kk); a.Jmp(checkStub); }); a.Jcc(Cc.GE, t); }
                a.Bind(resume[i]);
                a.Inc(PCN);
            }
            void PlainStep(int j, int segEnd, bool fast)
            {
                var d = b.Insts[j];
                if (fast && s_Cat17Seg && c0Native && X64C0Status(d)) { // [climb4 speed_cat17]
                    var l = a.NewLabel(); int r = segEnd - j, jj = j, every = s_IntCheckEvery;
                    cold.Add(() => {
                        a.Bind(l);
                        if (r > 0) {
                            a.AluRI(X64Asm.Alu8.Sub, PT, r, false); a.AluRI(X64Asm.Alu8.Sub, PCN, r, false); a.AluRI(X64Asm.Alu8.Sub, CAD, r, false);
                            if ((every & (every - 1)) == 0) a.AluRI(X64Asm.Alu8.And, CAD, every - 1, false); // the segment's cad may have wrapped
                        }
                        ExitCode(XSlow, jj);
                    });
                    fwdOn = false; fwdReg = -1; EmitC0Status(d, l); fwdReg = -1; srValid = false; return;
                }
                fwdOn = fast && s_X64Fwd;
                if (fwdOn) {
                    // ops whose register loads come first and that end with their one StG (else the host copy is lost)
                    bool clean = d.Op.Op switch {
                        "addu" or "subu" or "addiu" or "and" or "or" or "xor" or "nor" or "daddu" or "andi" or "ori" or "xori" or "daddiu"
                            or "lui" or "slt" or "sltu" or "slti" or "sltiu" or "sll" or "srl" or "sra" => true,
                        _ => X64LoadKind(d) >= 0 || X64StoreSize(d) > 0 };
                    if (!clean) fwdReg = -1;
                    if (X64Inline(d)) EmitInline(d, j); else PlainMemFast(d, j, segEnd);
                    // a clean op either emitted nothing (rd = 0: the copy still holds) or ended with its StG; a store
                    // clobbered the scratch registers after its address load
                    if (!clean || X64StoreSize(d) > 0) fwdReg = -1;
                    fwdOn = false;
                    return;
                }
                if (X64Inline(d)) { EmitInline(d, j); return; }
                if (!fast && c0Native && s_Cat11Epc && d.Op.Op == "mtc0" && d.Destination == 14 && X64Generic(d)) { // [speed_cat11]
                    after[j] = a.NewLabel(); var sl = ColdExit(XSlow, j);
                    RegTransferGprHelper(d, out int gs, out int _);
                    a.MovRM(R.Rcx, new M(R.Rbp, 0), false); a.AluRI(X64Asm.Alu8.And, R.Rcx, 0x18, false); a.Jcc(Cc.NE, sl);
                    if (gs == 0) a.MovRI(R.Rax, 0); else a.MovRM(R.Rax, Gm(gs));
                    a.MovRM(R.Rcx, C(CCount)); a.MovMR(new M(R.Rcx, 14 * 8), R.Rax);
                    a.Bind(after[j]); fwdReg = -1; return;
                }
                if (!fast && c0Native && s_Cat11Count && d.Op.Op == "mfc0" && d.Destination == 9 && X64Generic(d)) { after[j] = a.NewLabel(); var sl = ColdExit(XSlow, j); EmitC0Count(d, sl); a.Bind(after[j]); fwdReg = -1; return; } // [speed_cat11]
                if (!fast && rawOn11 && s_Cat11CvtL && d.Op.Op == "cvt.l.fmt" && d.Format == FpuValueType.FDouble && X64Generic(d)) { after[j] = a.NewLabel(); var sl = ColdExit(XSlow, j); a.CmpMI8(O(s_OffEnableCp1), 0); a.Jcc(Cc.E, sl); EmitFpu(d, sl); a.Bind(after[j]); fwdReg = -1; return; } // [speed_cat11]
                if (!fast && rawOn11 && s_Cat13CvtW && d.Op.Op == "cvt.w.fmt" && X64Generic(d)) { after[j] = a.NewLabel(); var sl = ColdExit(XSlow, j); a.CmpMI8(O(s_OffEnableCp1), 0); a.Jcc(Cc.E, sl); EmitFpu(d, sl); a.Bind(after[j]); fwdReg = -1; return; } // [speed_cat13]
                if (!fast && c0Native && X64C0Status(d)) { after[j] = a.NewLabel(); var sl = ColdExit(XSlow, j); EmitC0Status(d, sl); a.Bind(after[j]); fwdReg = -1; return; } // [speed_cat9]
                if (!fast && c0Native && X64C1Ctl(d)) { after[j] = a.NewLabel(); var sl = ColdExit(XSlow, j); EmitC1Ctl(d, sl); a.Bind(after[j]); fwdReg = -1; return; } // [speed_cat10]
                if (!fast && X64Generic(d)) { after[j] = a.NewLabel(); ExitCode(XSlow, j); a.Bind(after[j]); fwdReg = -1; return; } // [speed_cat5]
                if (fast) { PlainMemFast(d, j, segEnd); return; }
                after[j] = a.NewLabel();
                var slow = ColdExit(XSlow, j);
                if (X64Mem(d)) EmitMem(d, slow); else EmitFpu(d, slow);
                a.Bind(after[j]);
            }
            // [speed_cat9] mfc0 / mtc0 Status natively ([rbp] = Status, pinned per call): mfc0 in kernel mode (KSU = 0, so
            // 64-bit operation: sign-extended read); mtc0 when the masked write changes nothing but IE and leaves IE clear
            // or only clears IE / IM bits (or leaves them, or EXL/ERL is set): no interrupt can become due (__osDisableInt, osSetIntMask
            // narrowing the mask, restores of a disabled state). Anything else takes the op's XSlow exit.
            void EmitC0Status(in DecodedInstruction d, XLabel slow)
            {
                RegTransferGprHelper(d, out int gs, out int gt);
                a.MovRM(R.Rcx, new M(R.Rbp, 0), false);
                a.MovRR(R.Rdx, R.Rcx, false); a.AluRI(X64Asm.Alu8.And, R.Rdx, 0x18, false); a.Jcc(Cc.NE, slow);
                if (d.Op.Op == "mfc0") { if (gt != 0) { a.MovsxdRR(R.Rax, R.Rcx); StG(gt, R.Rax); } return; }
                const uint SM = 0xFE20FFFF;
                LdG32(R.Rax, gs); a.AluRI(X64Asm.Alu8.And, R.Rax, unchecked((int)SM), false);
                a.MovRR(R.Rdx, R.Rcx, false); a.AluRI(X64Asm.Alu8.And, R.Rdx, unchecked((int)~SM), false); a.Alu(X64Asm.Alu8.Or, R.Rax, R.Rdx, false);
                a.MovRR(R.Rdx, R.Rax, false); a.Alu(X64Asm.Alu8.Xor, R.Rdx, R.Rcx, false); a.AluRI(X64Asm.Alu8.And, R.Rdx, s_Cat11Exl ? ~0xFF03 : ~0xFF01, false); a.Jcc(Cc.NE, slow);
                if (s_Cat11Exl) { a.MovRR(R.Rdx, R.Rax, false); a.Not(R.Rdx, false); a.Alu(X64Asm.Alu8.And, R.Rdx, R.Rcx, false); a.TestRI(R.Rdx, 2); a.Jcc(Cc.NE, slow); } // [speed_cat11] EXL cleared: slow
                var ok = a.NewLabel();
                a.TestRI(R.Rax, 1); a.Jcc(Cc.E, ok);            // IE clear after the write
                a.TestRI(R.Rax, 6); a.Jcc(Cc.NE, ok);           // EXL / ERL set
                a.MovRR(R.Rdx, R.Rcx, false); a.Not(R.Rdx, false); a.Alu(X64Asm.Alu8.And, R.Rdx, R.Rax, false);
                a.AluRI(X64Asm.Alu8.And, R.Rdx, 0xFF01, false);
                if (!s_Cat11Ie) a.Jcc(Cc.NE, slow); // an IE / IM bit newly set: slow (interrupt may be due)
                else { // [speed_cat11] inline unless an enabled interrupt may be due
                    a.Jcc(Cc.E, ok);
                    a.AluRM(X64Asm.Alu8.Cmp, PCN, C(CQuiet), false); a.Jcc(Cc.GE, slow);
                    a.MovRM(R.Rdx, C(CCause)); a.MovRM(R.Rdx, new M(R.Rdx, 0), false);
                    a.Alu(X64Asm.Alu8.And, R.Rdx, R.Rax, false); a.TestRI(R.Rdx, 0xFF00); a.Jcc(Cc.NE, slow);
                }
                a.Bind(ok);
                a.MovMR(new M(R.Rbp, 0), R.Rax, false);
            }
            // [speed_cat11] mfc0 Count, KSU = 0 (64-bit operation): (uint)(Count + PCN * CountScale), sign-extended
            void EmitC0Count(in DecodedInstruction d, XLabel slow)
            {
                RegTransferGprHelper(d, out int gs, out int gt);
                a.MovRM(R.Rcx, new M(R.Rbp, 0), false);
                a.AluRI(X64Asm.Alu8.And, R.Rcx, 0x18, false); a.Jcc(Cc.NE, slow);
                if (gt == 0) return;
                a.MovRM(R.Rcx, C(CCount)); a.MovRM(R.Rax, new M(R.Rcx, 9 * 8), false);
                a.MovRM(R.Rdx, C(CCScale), false); a.ImulRR(R.Rdx, PCN);
                a.Alu(X64Asm.Alu8.Add, R.Rax, R.Rdx, false);
                a.MovsxdRR(R.Rax, R.Rax); StG(gt, R.Rax);
            }
            // [speed_cat10] cfc1 / dmfc1 / ctc1 FCSR (TransferReg's Cp1 / Cp1Ctl paths): KSU = 0 (64-bit operation), CU1 and EnableCp1 set
            void EmitC1Ctl(in DecodedInstruction d, XLabel slow)
            {
                RegTransferGprHelper(d, out int gs, out int gt);
                a.MovRM(R.Rcx, new M(R.Rbp, 0), false);
                a.MovRR(R.Rdx, R.Rcx, false); a.AluRI(X64Asm.Alu8.And, R.Rdx, 0x18, false); a.Jcc(Cc.NE, slow);
                a.TestRI(R.Rcx, unchecked((int)(uint)StatusRegister.StatusFlags.UsableCop1)); a.Jcc(Cc.E, slow);
                a.CmpMI8(O(s_OffEnableCp1), 0); a.Jcc(Cc.E, slow);
                if (d.Op.Op == "dmfc1") { EmitFpu(d, slow); return; }
                if (d.Op.Op == "cfc1") {
                    if (gt == 0) return;
                    int fs = d.Destination;
                    if (fs == 31) { a.MovRM(R.Rcx, C(CFcr)); a.MovsxdRM(R.Rax, new M(R.Rcx, 0)); }
                    else a.MovRI(R.Rax, fs == 0 ? 0xA00 : 0);
                    StG(gt, R.Rax);
                    return;
                }
                LdG32(R.Rax, gs); a.AluRI(X64Asm.Alu8.And, R.Rax, 0x183FFFF, false);
                a.MovRM(R.Rcx, C(CFcr)); a.MovRM(R.Rdx, new M(R.Rcx, 0), false);
                if (!s_Cat10C1Rm) { a.Alu(X64Asm.Alu8.Xor, R.Rdx, R.Rax, false); a.AluRI(X64Asm.Alu8.And, R.Rdx, 0x1000003, false); a.Jcc(Cc.NE, slow); }
                a.MovMR(new M(R.Rcx, 0), R.Rax, false);
            }
            void PlainMemFast(in DecodedInstruction d, int j, int segEnd)
            {
                {
                    var l = a.NewLabel(); int r = segEnd - j, jj = j; bool mask = curSegBr || s_Cat17CadFix;
                    cold.Add(() => {
                        a.Bind(l);
                        if (r > 0) { a.AluRI(X64Asm.Alu8.Sub, PT, r, false); a.AluRI(X64Asm.Alu8.Sub, CAD, r, false); a.AluRI(X64Asm.Alu8.Sub, PCN, r, false);
                            // [speed_cat17] a segment closed by a branch has more steps after a load / store: its cad may have wrapped
                            if (mask && (s_IntCheckEvery & (s_IntCheckEvery - 1)) == 0) a.AluRI(X64Asm.Alu8.And, CAD, s_IntCheckEvery - 1, false); }
                        ExitCode(XSlow, jj);
                    });
                    if (X64Mem(d)) EmitMem(d, l); else EmitFpu(d, l);
                }
            }
            // [speed_cat17] branch i + its ALU delay slot i + 1 at the end of a batched segment (ticks already added)
            void FastBranch(int i)
            {
                var d = b.Insts[i]; string op = d.Op.Op; long pc = b.StartPc + i * 4L, dpc = pc + 4;
                bool cond = !(op == "j" || op == "jal" || op == "jr");
                bool fpuD = SegFpuD(b.Insts[i + 1]); bool mem = X64Mem(b.Insts[i + 1]) || fpuD;
                bool likely = cond && d.IsLikely(); var ntL = likely ? a.NewLabel() : null;
                long target = op == "jr" ? -1 : (long)(uint)(op == "j" || op == "jal" ? ((long)(d.Inst.target << 2) | (pc & 0xF0000000)) : pc + ((long)(short)d.Immediate << 2) + 4);
                if (cond) {
                    LdG(R.Rax, d.Source);
                    Cc cc;
                    switch (d.Op.ArithmeticType) {
                        case ArithmeticOp.EQUAL: LdG(R.Rcx, d.Target); cc = Cc.E; break;
                        case ArithmeticOp.NOT_EQUAL: LdG(R.Rcx, d.Target); cc = Cc.NE; break;
                        case ArithmeticOp.GREATER_THAN: a.MovRI(R.Rcx, 0); cc = Cc.G; break;
                        case ArithmeticOp.LESS_THAN: a.MovRI(R.Rcx, 0); cc = Cc.L; break;
                        case ArithmeticOp.GREATER_THAN_OR_EQUAL: a.MovRI(R.Rcx, 0); cc = Cc.GE; break;
                        case ArithmeticOp.LESS_THAN_OR_EQUAL: a.MovRI(R.Rcx, 0); cc = Cc.LE; break;
                        default: throw new InvalidOperationException("branch condition");
                    }
                    a.MovRI(R.Rdx, 0); a.Alu(X64Asm.Alu8.Cmp, R.Rax, R.Rcx); a.SetCc(cc, R.Rdx);
                    if (mem) a.MovMR(C(CTk), R.Rdx, false); else a.MovRR(R.R11, R.Rdx);
                }
                else if (op == "jr") { if (mem) { LdG32(R.Rax, d.Source); a.MovMR(C(CTgt), R.Rax); } else LdG32(R.R10, d.Source); }
                if (op == "jal") { a.MovRI(R.Rcx, LinkConst(pc + 8)); StG(31, R.Rcx); }
                if (likely) { a.TestRI(R.Rdx, 0xFF); a.Jcc(Cc.E, ntL); } // not taken: the delay slot is skipped
                if (mem) {
                    // [speed_cat17] BRMEM: the delay slot's slow path first stores the branch state EmitBranch would have
                    // (the taken flag is in ctx Tk, a jr target in ctx Tgt), then takes the delay-slot exit (XDSlow) at i + 1
                    var sl = a.NewLabel(); var dd = b.Insts[i + 1];
                    cold.Add(() => {
                        a.Bind(sl);
                        if (!cond) {
                            a.MovMI32(C(CTk), 1, false);
                            if (op == "jr") a.MovRM(R.Rcx, C(CTgt)); else { a.MovRI(R.Rcx, target); a.MovMR(C(CTgt), R.Rcx); }
                            a.MovMR(O(s_OffTarget), R.Rcx); a.MovMI8(O(s_OffUncond), 1);
                        }
                        else {
                            var nt0 = a.NewLabel();
                            a.MovRI(R.Rcx, target); a.MovMR(C(CTgt), R.Rcx);
                            a.MovRM(R.Rdx, C(CTk), false); a.TestRI(R.Rdx, 0xFF); a.Jcc(Cc.E, nt0);
                            a.MovMR(O(s_OffTarget), R.Rcx);
                            a.Bind(nt0);
                            a.MovMR8(O(s_OffCond), R.Rdx);
                        }
                        a.MovMI8(O(s_OffExecBD), 1);
                        SetPc(pc + 4);
                        ExitCode(XDSlow, i + 1);
                    });
                    if (fpuD) EmitFpu(dd, sl); else EmitMem(dd, sl);
                }
                else EmitInline(b.Insts[i + 1], i + 1);
                if (mem && op == "jr") a.MovRM(R.R10, C(CTgt));
                var nt = a.NewLabel();
                if (cond && !likely) { if (mem) a.AluMI(X64Asm.Alu8.Cmp, C(CTk), 0, false); else a.TestRI(R.R11, 0xFF); a.Jcc(Cc.E, nt); }
                int lt = LoopTarget(i);
                if (lt >= 0) {
                    SetPc(target);
                    if (s_ValLoops) minLoop = Math.Min(minLoop, lt);
                    int delta = lt - (i + 2);
                    a.AluMI(X64Asm.Alu8.Add, C(CBasis), delta, false); a.AluMI(X64Asm.Alu8.Add, C(CEnd), delta, false);
                    a.MovMI32(C(CLap), lt, false); a.MovMI32(C(CLast), i + 1, false);
                    a.Jmp(step[lt]);
                }
                else if (op == "jr") { a.MovMR(O(s_OffPc), R.R10); a.MovMR(C(CTgt), R.R10); LinkOrDone(dpc, i + 2, -1, false, true); }
                else { SetPc(target); LinkOrDone(dpc, i + 2, target, false); }
                a.Bind(nt);
                if (likely) {
                    // the segment added the delay slot's tick: take it back (cad modulo the cadence, as the per-step path holds it),
                    // then leave as EmitBranch's not-taken likely path
                    a.Bind(ntL);
                    a.AluRI(X64Asm.Alu8.Sub, PT, 1, false); a.AluRI(X64Asm.Alu8.Sub, PCN, 1, false); a.AluRI(X64Asm.Alu8.Sub, CAD, 1, false);
                    if ((s_IntCheckEvery & (s_IntCheckEvery - 1)) == 0) a.AluRI(X64Asm.Alu8.And, CAD, s_IntCheckEvery - 1, false);
                    SetPc(pc + 8); LinkOrDone(pc, i + 1, pc + 8, false);
                }
                else if (i + 2 >= n) { SetPc(dpc + 4); LinkOrDone(dpc, i + 2, dpc + 4, false); }
                else if (cond) SetPc(dpc); // not taken: PC as EmitBranch left it (the delay slot's pc)
            }
            void EmitSegment(int i0, int m)
            {
                int every = s_IntCheckEvery; bool pow2 = (every & (every - 1)) == 0;
                var slowL = a.NewLabel(); var ok = a.NewLabel();
                a.Bind(step[i0]);
                a.MovRM(R.Rax, C(CEnd), false); a.AluRI(X64Asm.Alu8.Sub, R.Rax, i0, false); a.AluRI(X64Asm.Alu8.Cmp, R.Rax, m, false); a.Jcc(Cc.B, slowL);
                a.MovRR(R.Rax, CAD, false); a.AluRI(X64Asm.Alu8.Add, R.Rax, m, false); a.AluRI(X64Asm.Alu8.Cmp, R.Rax, every, false);
                if (pow2) {
                    a.Jcc(Cc.L, ok);
                    // every check inside the run would be skipped (pcn < quiet at each): cad wraps, as checkStub resets it
                    a.AluRI(X64Asm.Alu8.Cmp, CAD, every, false); a.Jcc(Cc.GE, slowL);
                    a.MovRR(R.Rdx, PCN, false); a.AluRI(X64Asm.Alu8.Add, R.Rdx, m - 1, false); a.AluRM(X64Asm.Alu8.Cmp, R.Rdx, C(CQuiet), false); a.Jcc(Cc.GE, slowL);
                    a.Bind(ok);
                    a.AluRI(X64Asm.Alu8.And, R.Rax, every - 1, false);
                }
                else { a.Jcc(Cc.GE, slowL); a.Bind(ok); }
                a.MovRR(CAD, R.Rax, false); a.AluRI(X64Asm.Alu8.Add, PT, m, false); a.AluRI(X64Asm.Alu8.Add, PCN, m, false);
                int segEnd = i0 + m - 1;
                fwdReg = -1;
                bool br = segHasBr[i0]; curSegBr = br;
                if (s_Cat17Seg && c0Native) for (int j = i0; j < i0 + m; j++) if (X64C0Status(b.Insts[j])) curSegBr = true; // a longer segment than before
                srOn = s_SpdSrHoist; srValid = false;
                for (int j = i0; j <= (br ? segEnd - 2 : segEnd); j++) PlainStep(j, segEnd, true);
                fwdReg = -1; fwdOn = false;
                if (br) FastBranch(segEnd - 1);
                srOn = false; srValid = false;
                cold.Add(() => {
                    a.Bind(slowL);
                    if (!br) { for (int j = i0; j <= segEnd; j++) { if (j > i0) a.Bind(step[j]); StepTicks(j); PlainStep(j, segEnd, false); } a.Jmp(step[segEnd + 1]); return; }
                    bool inD = false, ended = false;
                    for (int j = i0; j <= segEnd; j++) { if (j > i0) a.Bind(step[j]); StepTicks(j); ended = StepBody(j, ref inD); }
                    if (!ended) a.Jmp(step[segEnd + 1]);
                });
            }

            bool inDelay = false;

            // one step (after its StepTicks) on the per-step path; true = the block ends here
            bool StepBody(int i, ref bool inDelay)
            {
                var d = b.Insts[i];
                long pc = b.StartPc + i * 4;
                if (inDelay) {
                    if (s_Cat13Brk && d.Op.Op == "break") a.Jmp(MatExit(XDSlow, i)); // [speed_cat13]
                    else if (X64Inline(d)) EmitInline(d, i);
                    else if (X64Mem(d)) EmitMem(d, MatExit(XDSlow, i));
                    else EmitFpu(d, MatExit(XDSlow, i));
                    lazyBr = -1;
                    var nt = a.NewLabel();
                    a.AluMI(X64Asm.Alu8.Cmp, C(CTk), 0, false); a.Jcc(Cc.E, nt);
                    a.MovRM(R.Rax, C(CTgt), false); a.MovMR(O(s_OffPc), R.Rax);
                    a.MovMI8(O(s_OffExecBD), 0); a.MovMI8(O(s_OffCond), 0); a.MovMI8(O(s_OffUncond), 0);
                    int lt = LoopTarget(i - 1);
                    if (lt >= 0) {
                        if (s_ValLoops) minLoop = Math.Min(minLoop, lt);
                        int delta = lt - (i + 1);
                        a.AluMI(X64Asm.Alu8.Add, C(CBasis), delta, false); a.AluMI(X64Asm.Alu8.Add, C(CEnd), delta, false);
                        a.MovMI32(C(CLap), lt, false); a.MovMI32(C(CLast), i, false);
                        a.Jmp(step[lt]);
                    }
                    else {
                        var bd = b.Insts[i - 1]; string bop = bd.Op.Op; long bpc = pc - 4;
                        if (bop == "jr" || bop == "jalr") LinkOrDone(pc, i + 1, -1, false, true);
                        else LinkOrDone(pc, i + 1, (long)(uint)(bop == "j" || bop == "jal" ? ((long)(bd.Inst.target << 2) | (bpc & 0xF0000000)) : bpc + ((long)(short)bd.Immediate << 2) + 4), false); // PC = low 32 bits of CTgt
                    }
                    a.Bind(nt);
                    a.MovMI8(O(s_OffExecBD), 0);
                    inDelay = false;
                    if (i + 1 < n) return false;
                    SetPc(pc + 4); LinkOrDone(pc, i + 1, pc + 4, false);
                    return true;
                }
                if (X64Br(d)) {
                    EmitBranch(i, pc);
                    // [jit_cost1] tail branch: its delay slot is outside the block; leave as a batch ending at the delay slot
                    if (i == n - 1) { ExitCode(XExit, n); return true; }
                    inDelay = true; return false;
                }
                PlainStep(i, i, false);
                if (i == n - 1) LinkOrDone(pc, n, b.StartPc + n * 4L, true);
                return false;
            }
            for (int i = 0; i < n; i++) {
                if (!inDelay && segLen[i] >= 2) { int m = segLen[i]; EmitSegment(i, m); i += m - 1; continue; }
                a.Bind(step[i]);
                StepTicks(i);
                if (StepBody(i, ref inDelay)) break;
            }

            // ---- shared stubs ----
            a.Bind(budgetStub);
            a.MovRM(R.Rax, C(CEnd), false); a.MovMR(C(CK), R.Rax, false); a.MovRI(R.Rax, XExit); a.Jmp(exitCommon);
            // eax = k: cad = 0; a repeat of the last check inside the quiet window is skipped (resume at k), else leave
            a.Bind(checkStub);
            a.MovRI(CAD, 0);
            { var real = a.NewLabel();
              a.AluRM(X64Asm.Alu8.Cmp, PCN, C(CQuiet), false); a.Jcc(Cc.GE, real);
              a.LeaRip(R.Rcx, resumeTab); a.JmpM(new M(R.Rcx, R.Rax, 8, 0));
              a.Bind(real);
              a.MovMR(C(CK), R.Rax, false); a.MovRI(R.Rax, XCheck); a.Jmp(exitCommon); }
            var chainEntry = a.NewLabel(); var midEntry = a.NewLabel(); var midTab = a.NewLabel(); XLabel[] midVal = null;
            if (link) {
                // [block_link] entered by a jump from another block (ticks, cad, quiet in registers; basis 0, end = budget left)
                var bad = a.NewLabel();
                a.Bind(chainEntry);
                if (s_Cat17Cnt) { a.MovRI(R.Rax, (long)s_Cat17CntBuf); a.IncM(new M(R.Rax, 0), true); a.AluMI(X64Asm.Alu8.Add, new M(R.Rax, 8), n, true); }
                a.MovMI32(C(CBlk), xid, true);
                a.AluMI(X64Asm.Alu8.Cmp, C(CEnd), 0, false); a.Jcc(Cc.LE, budgetStub);
                for (int j = 0; j < n; j++) { a.AluMI(X64Asm.Alu8.Cmp, new M(RAM, (int)(b.StartPhys + 4 * j)), (int)b.Raw[j], false); a.Jcc(Cc.NE, bad); }
                a.Jmp(step[0]);
                a.Bind(bad);
                ExitCode(XExit, 0);
                if (s_X64LinkMid && n > 1) {
                    // [f5_speed3] entered at step r9 (> 0) of this block: basis/lap = r9, end += r9 (end was the budget left)
                    var badM = a.NewLabel();
                    a.Bind(midEntry);
                    a.MovMI32(C(CBlk), xid, true);
                    a.MovMR(C(CBasis), R.R9, false); a.MovMR(C(CLap), R.R9, false); a.AluMR(X64Asm.Alu8.Add, C(CEnd), R.R9, false);
                    a.MovRM(R.Rax, C(CEnd), false); a.Alu(X64Asm.Alu8.Cmp, R.Rax, R.R9, false); a.Jcc(Cc.LE, budgetStub);
                    // words from min(start, first loop target) on: words before that never run in this entry
                    a.LeaRip(R.Rcx, midTab); a.JmpM(new M(R.Rcx, R.R9, 8, 0));
                    midVal = new XLabel[n];
                    for (int j = 0; j < n; j++) { midVal[j] = a.NewLabel(); a.Bind(midVal[j]); a.AluMI(X64Asm.Alu8.Cmp, new M(RAM, (int)(b.StartPhys + 4 * j)), (int)b.Raw[j], false); a.Jcc(Cc.NE, badM); }
                    a.LeaRip(R.Rcx, entryTab); a.JmpM(new M(R.Rcx, R.R9, 8, 0));
                    a.Bind(badM);
                    a.MovMR(C(CK), R.R9, false); a.MovRI(R.Rax, XExit); a.Jmp(exitCommon);
                }
            }
            // [block_link] validated entry (Mode = 3n + s): words from min(s, minLoop) on, then step s
            bool nval = s_X64NVal;
            var vl = new XLabel[n];
            if (nval) {
                var stale = a.NewLabel();
                for (int j = 0; j < n; j++) { vl[j] = a.NewLabel(); a.Bind(vl[j]); a.AluMI(X64Asm.Alu8.Cmp, new M(RAM, (int)(b.StartPhys + 4 * j)), (int)b.Raw[j], false); a.Jcc(Cc.NE, stale); }
                a.MovRM(R.Rax, C(CMode), false); a.AluRI(X64Asm.Alu8.Sub, R.Rax, 3 * n, false);
                a.LeaRip(R.Rcx, entryTab); a.JmpM(new M(R.Rcx, R.Rax, 8, 0));
                a.Bind(stale);
                a.MovRI(R.Rax, XStale); a.Jmp(exitCommon);
            }
            for (int ci = 0; ci < cold.Count; ci++) cold[ci](); // cold pieces may add more (MappedCold in a dual-FR copy)
            a.Bind(exitCommon);
            a.MovMR(C(CCad), CAD, false); a.MovMR(C(CPt), PT, false); a.MovMR(C(CPcn), PCN, false);
            foreach (var r in new[] { R.R15, R.R14, R.R13, R.R12, R.Rdi, R.Rsi, R.Rbp, R.Rbx }) a.Pop(r);
            a.Ret();
            a.Bind(trap); a.Int3();
            a.Align(8);
            var ent = new XLabel[(nval ? 4 : 3) * n];
            for (int i = 0; i < n; i++) { ent[i] = step[i]; ent[n + i] = resume[i]; ent[2 * n + i] = after[i] ?? trap; if (nval) ent[3 * n + i] = vl[Math.Min(i, minLoop)]; }
            a.Bind(entryTab); a.AbsTable(ent);
            a.Bind(resumeTab); a.AbsTable(resume);
            if (midVal != null) { var mt = new XLabel[n]; for (int i = 0; i < n; i++) mt[i] = midVal[Math.Min(i, minLoop)]; a.Bind(midTab); a.AbsTable(mt); }

            byte* code = ExecMem.Publish(a);
            if (code == null) return IntPtr.Zero;
            if (X64Prof.On) X64Prof.Add((long)code, a.Length, b); // [speed_cat17] measurement
            if (link) { b.XId = xid; b.ChainEntry = (IntPtr)(code + chainEntry.Pos); b.MidEntry = s_X64LinkMid && n > 1 ? (IntPtr)(code + midEntry.Pos) : IntPtr.Zero; }
            b.NVal = nval;
            b.DelayEntry = s_RecDelayEntry ? delayEntry : null;
            b.MinLoop = minLoop;
            b.IlSize = a.Length;
            System.Threading.Interlocked.Increment(ref X64Blocks);
            return (IntPtr)code;
        }

        // Runs a native block: the managed side of every exit code is the IL's own code for that point.
        private unsafe int RunX64(CpuRecBlock b, int max, int start) { int k = RunX64Core(b, max, start); if (k > 0) X64Steps += k; m_XPendLink = m_XCtx->Link; return k; }
        private unsafe int RunX64Core(CpuRecBlock b, int max, int start)
        {
            XCtx* c = m_XCtx;
            if (c == null) {
                if (X64Prof.On) X64Prof.Start(); // [speed_cat17] measurement
                c = m_XCtx = (XCtx*)NativeMemory.AllocZeroed((nuint)sizeof(XCtx));
                c->Gpr = (long)State.GetGprRef(); c->Ram = (long)RdramWords;
            }
            int n = b.Insts.Length;
            c->Cad = m_IntCheckCounter; c->Pt = 0; c->Pcn = 0;
            c->Quiet = s_RecQuietCarry ? RecQuietAtEntry() : 0;
            if (s_X64Exits && c->Quiet == 0) X64QZero[!m_QuietValid ? 0 : Cop0.MiPending() != m_QuietMi ? 1 : 2]++;
            c->Basis = start; c->LapStart = start; c->LastIdx = -1; c->End = start + max;
            if (b.DelayEntry != null && b.DelayEntry[start]) { c->Tk = RecDelayTaken() ? 1 : 0; c->Tgt = BU_TargetAddress; }
            c->Mode = b.NVal && !(b.DelayEntry != null && b.DelayEntry[start] && RecDelayTaken()) ? 3 * n + start : start;
            c->Blk = b.XId; c->Link = 0; c->CAcc = 0;
            if (s_X64Link) { c->MapKey = X64MapPtr(); c->Ep0 = *global::cor64.Mips.R4300I.TLB.MapEpoch.Ptr; }
            int cur = b.XId;
            var fn = (delegate* unmanaged[SuppressGCTransition]<XCtx*, int>)b.Native;
            var tlbc = b.UsesTlb || s_X64Link ? TLB : null; // only blocks with an inline TLB probe ([block_link2] linked blocks may need it)
            X64Calls++;
            if (s_X64DummySr == null) s_X64DummySr = (uint*)NativeMemory.AllocZeroed(4);
            ref uint srRef = ref (m_CpufSr != null ? ref m_CpufSr.RawValueRef : ref *s_X64DummySr);
            fixed (long* anchor = &m_LastFetchAddress)
            fixed (uint* srp = &srRef)
            fixed (uint* fcrp = &State.FCR.RawValueRef)
            fixed (uint* causep = &Cop0.CauseRawRef)
            fixed (ulong* countp = &Cop0.RegsRawRef) {
                c->Obj = (long)anchor; c->Sr = (long)srp; c->Fcr = (long)fcrp;
                c->Cause = (long)causep; c->Count = (long)countp; c->CScale = SystemController.CountScale; // [climb4 speed_cat11]
                var mir = PhysicalMemory?.MIRegs; if (mir != null) { c->MiI = (long)mir.ExportInterruptPtr(); c->MiM = (long)mir.ExportMaskPtr(); } // [climb4 speed_cat12]
                while (true) {
                    tlbc?.FcUpdateKeyHi(); // [session_leak] ASID/generation for the inline TLB probe (changes only outside native code)
                    int code = fn(c);
                    if (s_X64Exits) { X64ExitCount[code & 7]++; if (code == XCheck && c->Quiet == 0) X64ExitCount[7]++; }
                    if ((int)c->Blk != cur) { cur = (int)c->Blk; b = s_XById[cur]; n = b.Insts.Length; fn = (delegate* unmanaged[SuppressGCTransition]<XCtx*, int>)b.Native; X64Linked++; }
                    int k = (int)c->K, basis = (int)c->Basis - (int)c->CAcc;
                    switch (code) {
                        case XExit:
                            return RecExit(b.StartPc, k, basis, (int)c->Pt, (int)c->Pcn, (int)c->Cad, (int)c->LapStart, (int)c->LastIdx);
                        case XDone:
                            RecDone(c->Aux, (int)c->Pt, (int)c->Pcn, (int)c->Cad);
                            return k - basis;
                        case XCheck: {
                            int q = RecCheckQ(b.StartPc + k * 4L, (int)c->Pt, (int)c->Pcn);
                            c->Pt = 0; c->Pcn = 0; c->Quiet = q;
                            if (s_X64Exits) { if (q < 0) X64QHist[0]++; else X64QHist[Math.Min(8, 1 + System.Numerics.BitOperations.Log2((uint)q + 1) / 4)]++; }
                            if (q < 0) { m_IntCheckCounter = (int)c->Cad; ExecuteAfterPrologue(); return k + 1 - basis; }
                            c->Mode = n + k;
                            break;
                        }
                        case XSlow: {
                            if (s_X64SlowOps) lock (X64SlowOpCount) { string key = b.Insts[k].Op.Op + (k == 0 || !IsCpuBranch(b.Insts[k - 1]) ? "" : "/ds") + "/sr" + ((Cop0.StatusReg.RawValueRef >> 26) & 0x9).ToString("x") + (m_CpufSr == null ? "N" : ReferenceEquals(m_CpufSr, Cop0.StatusReg) ? "" : "S") + (StateR4000I.FPR.FullMode ? "F" : "h") + (s_X64SlowPc ? "@" + ((uint)(b.StartPc + k * 4L)).ToString("X8") + "|" + DbgPollState + (PollCandidate ? "c" : "n") : ""); X64SlowOpCount.TryGetValue(key, out long sc); X64SlowOpCount[key] = sc + 1; }
                            string sop = s_Cat5Slow ? b.Insts[k].Op.Op : null; bool pure = sop != null && X64Generic(b.Insts[k]) && X64Cat5Pure(sop);
                            bool left = RecSlowStep(b.StartPc + k * 4L, (int)c->Pt, (int)c->Pcn, b.Insts, b.Handlers, k, pure);
                            c->Pt = 0; c->Pcn = 0; c->Quiet = pure && !left ? RecQuietAtEntry() : 0;
                            if (sop != null && X64Cat5Leave(sop) && X64Generic(b.Insts[k])) left = true; // [speed_cat5]
                            if (left) { m_IntCheckCounter = (int)c->Cad; return k + 1 - basis; }
                            c->Mode = 2 * n + k;
                            if (s_X64Link) c->MapKey = X64MapPtr();
                            break;
                        }
                        case XDSlow: {
                            RecPre(b.StartPc + k * 4L, (int)c->Pt, (int)c->Pcn);
                            c->Pt = 0; c->Pcn = 0; c->Quiet = 0;
                            m_QuietValid = false;
                            // [emu_fix2] a COP1 op in a delay slot with CU1 clear raises Coprocessor Unusable (as the IL's
                            // CallHandler and RecSlowStep do); running the handler wrote the FPR and lost the exception.
                            // Kirby 64 lost an mtc1 to its lazy FPU-context switch (A/B ignored in Pop Star 1-1).
                            // COR64_X64_DSLOW_CU1=0 restores the old (wrong) path.
                            uint dmaj = b.Insts[k].Inst.inst >> 26;
                            bool dcop1 = s_DslowCu1 && (dmaj == 0x11 || dmaj == 0x31 || dmaj == 0x35 || dmaj == 0x39 || dmaj == 0x3D);
                            if (!dcop1 || RecCop1Usable()) b.Handlers[k](b.Insts[k]);
                            StepTail(false);
                            m_IntCheckCounter = (int)c->Cad;
                            return k + 1 - basis;
                        }
                        case XMulDiv: {
                            var d = b.Insts[k];
                            if (s_X64SlowOps) lock (X64SlowOpCount) { string key = "muldiv:" + d.Op.Op; X64SlowOpCount.TryGetValue(key, out long sc); X64SlowOpCount[key] = sc + 1; } // [speed_cat12] measurement
                            if (d.Op.Op == "ddivu") RecDDivU((ulong*)c->Gpr, (ulong*)State.GetHiRef(), d.Source, d.Target); else // [speed_cat10]
                            if (d.Op.Op[0] == 'd' && d.Op.Op[1] == 'm') RecDMul((ulong*)c->Gpr, (ulong*)State.GetHiRef(), d.Source, d.Target, d.Op.Op == "dmultu"); else // [speed_cat4]
                            RecMulDiv((ulong*)c->Gpr, (ulong*)State.GetHiRef(), d.Source, d.Target, d.Op.Op == "div" ? 2 : 3);
                            c->Mode = 2 * n + k;
                            break;
                        }
                        case XStale:
                            if (c->CAcc == 0) return -1; // nothing ran
                            return RecExit(b.StartPc, 0, basis, (int)c->Pt, (int)c->Pcn, (int)c->Cad, 0, -1);
                        default: throw new InvalidOperationException("x64 exit " + code);
                    }
                    X64Reentries++;
                }
            }
        }
    }
}
