using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using cor64.IO;
using cor64.Debugging;
using System.Diagnostics;
using System.Threading;
using cor64.HLE;
using cor64.HLE.OS;
using cor64.HLE.Debug;

namespace cor64.Mips.R4300I
{
    public partial class Interpreter : InterpreterBaseR4300I
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private bool m_WarnInfiniteJump;
        private DecodedInstruction? m_InjectedInst;
        private bool m_TakenException;
        private long? m_InfiniteJumpAddress;
        private bool m_ExceptionReturn;
        private readonly Dictionary<long, CpuCodeHook> m_Hooks = new();
        private long m_LastFetchAddress;

        public Interpreter(string abi = "o32") : base(new Disassembler(abi))
        {
            #if CPU_PROFILER
            StartedWithProfiler = true;
            #endif
        }

        private bool IsReserved(DecodedInstruction inst)
        {
            #if !CPU_CHECK_RESERVED
            return false;
            #else
            #if !CPU_FORCE_32
            return
                (((inst.Op.Flags & ExecutionFlags.Reserved32) == ExecutionFlags.Reserved32) && !IsOperation64) ||
                (((inst.Op.Flags & ExecutionFlags.Reserved64) == ExecutionFlags.Reserved64) && IsOperation64);
            #else
            return false;
            #endif
            #endif
        }

        protected override void EntryPointHit()
        {
            base.EntryPointHit();

            DebugHooks.AddHooks(this, m_Hooks);

            // OSHooks_1.AddHooks(this, m_Hooks);
            // m_Hooks.Add(0x8018D298, new FatalPrintf(this));
        }

        public override string Description => "MIPS Interpreter";

        public bool CheckIfNeedServicing()
        {
            // Do not do anything if the system control has not signeled interrupts that need servicing
            if (Cop0.ServicingRequred && (Cop0State.Status.ErrorLevel || Cop0State.Status.ExceptionLevel)) {
                Cop0.ServicingRequred = false;

                // Clear the branch state
                ClearBranchState();

                PC = Cop0.ExceptionHandlerAddress;

                if (Cop0State.Status.ErrorLevel)
                {
                    #if DEBUG_INTERRUPTS
                    Log.Debug("Error Handler Taken: " + PC.ToString("X8"));
                    #endif
                }
                else {
                    #if DEBUG_INTERRUPTS
                    Log.Debug("Exception Handler Taken: " + PC.ToString("X8"));
                    #endif
                }

                m_TakenException = true;

                #if DEBUG
                TraceInterrupt();
                #endif

                return true;
            }

            return false;
        }

        public sealed override void ExceptionReturn(DecodedInstruction inst)
        {
            #if DEBUG_INTERRUPTS || DEBUG_ERET

            if (!m_TakenException) {
                Log.Debug("ERET called outside a handler: {0:X8}", PC);
            }
            else {
                Log.Debug("ERET called inside a handler");
            }

            #endif

            State.LLBit = false; // [cer64plus] ERET clears LLBit, so a following SC fails

            if (m_TakenException) {
                if (Cop0State.Status.ErrorLevel)
                {
                    // when servicing an error trap
                    PC = (uint)Cop0.CpuRegisterRead(CTS.CP0_REG_ERROR_EPC);
                    Cop0State.Status.ErrorLevel = false;
                }
                else
                {
                    PC = (uint)Cop0.CpuRegisterRead(CTS.CP0_REG_EPC);

                    #if DEBUG_EPC
                    Log.Debug("EPC -> PC: {0:X8}", PC);
                    #endif

                    Cop0State.Status.ExceptionLevel = false;
                }

                #if DEBUG_INTERRUPTS
                Log.Debug("Interrupt service finished");
                #endif
            }
            else {
                PC = (uint)Cop0.CpuRegisterRead(CTS.CP0_REG_EPC);

                #if DEBUG_EPC
                Log.Debug("EPC -> PC: {0:X8}", PC);
                #endif

                Cop0State.Status.ExceptionLevel = false;
            }

            #if DEBUG_INTERRUPTS || DEBUG_ERET

            if (Cop0State.Status.ErrorLevel && m_TakenException) {
                Log.Debug("Jump to Error EPC: " + PC.ToString("X16"));
            }
            else {
                Log.Debug("Jump to EPC: " + PC.ToString("X16"));
            }

            #endif

            PC &= 0xFFFFFFFF;
            if (CompatLog.On) Cop0.NoteEret((uint)PC); // [session_leak] logging only

            m_TakenException = false;
            State.LLBit = false;
            m_ExceptionReturn = true;
        }

        // [cer64plus] COR64_CPU_OPSTATS=1: per-opcode execution counts, printed at benchmark exit
        public static readonly bool s_OpStats = System.Environment.GetEnvironmentVariable("COR64_CPU_OPSTATS") == "1";
        public static readonly long[] OpCounts = new long[4096];
        public static readonly string[] OpNames = new string[4096];

        private int m_IntCheckCounter;
        private static readonly int s_IntCheckEvery = int.TryParse(System.Environment.GetEnvironmentVariable("COR64_INT_CHECK"), out var ic) && ic >= 1 ? ic : 4;

        // [cer64plus] Exact idle-loop skip (COR64_IDLE_SKIP=0 disables). When the CPU sits at `beq zero,zero,-1; nop` with
        // no branch in flight and nothing can interrupt it during the next n (even) instructions, the n steps are replaced
        // by their only effects: Count += n, n TLB Random ticks, the interrupt-check cadence (with no interrupt taken) and
        // the Cause CE clears. State afterwards is identical to running the n steps.
        public static readonly bool s_IdleSkip = System.Environment.GetEnvironmentVariable("COR64_IDLE_SKIP") != "0";
        public static long IdleSkippedSteps;

        public bool TryIdleSkip(int n) {
            if (!s_IdleSkip || (n & 1) != 0) return false;
            if (BU_ConditionalPending || BU_UnconditionalPending || BU_DelaySlotNext || BU_ExecuteBranchDelay || BU_NullifyNext) return false;
            if (m_InjectedInst.HasValue || m_ExceptionReturn) return false;
            long pc = PC;
            if (!IdlePhys(pc, 4, out long phys)) return false;
            if (!IdleLoopHead(pc, phys)) return false;
            if (!Cop0.IdleSlicePossible((uint)n)) return false;
            int checks = 0;
            for (int i = 0; i < n; i++) {
                TLBTick();
                if (++m_IntCheckCounter >= s_IntCheckEvery) { m_IntCheckCounter = 0; checks++; }
            }
            if (checks > 0) Cop0.IdleSliceApplyCheck(); else Cop0.ClearCauseCe();
            Cop0.MipsTimerTick((ulong)n);
            m_LastFetchAddress = pc + 4;
            IdleSkippedSteps += n;
            return true;
        }

        // [cer64plus] Idle fast-forward (COR64_IDLE_FF=0 disables): how many consecutive batches of n steps TryIdleSkip(n)
        // would skip if nothing outside the CPU changed (0 when it would not skip now). The caller stops earlier as soon
        // as a batch raises an interrupt, then applies the skipped batches with ApplyIdleBatches.
        // m_IdleSpanDelay: the span started at the loop's delay-slot phase (an even batch keeps that phase forever, so
        // without this the CPU would never fast-forward when it entered the loop there).
        private bool m_IdleSpanDelay;
        public static readonly bool s_IdleFFDelay = System.Environment.GetEnvironmentVariable("COR64_IDLE_FF_DELAY") != "0";

        // [climb4 idle_ff] stats only: PC sits in the idle loop (either phase); StatsLastM = last IdleBatchesPossible result
        public static long StatsLastM;
        public bool StatsAtIdleLoop() {
            if (AtIdleDelaySlot()) return true;
            long pc = PC;
            if (!IdlePhys(pc, 4, out long phys)) return false;
            return IdleLoopHead(pc, phys);
        }
        public long IdleBatchesPossible(int n, long cap) { long m = IdleBatchesPossibleCore(n, cap); if (N64System.s_FfStats) StatsLastM = m; return m; }
        // [climb4 idle_ff] COR64_IDLEFF_ONEPROBE (default on): IdleProbeNotLoop is set when IdleBatchesPossible rejected the
        // batch on the same predicates that make TryIdleSkip, TryIdleSkipDelayPhase and AtIdleDelaySlot fail (not at the
        // idle loop in either phase), so TickBatch can go straight to RunSteps. Nothing changes state in between.
        public static readonly bool s_OneProbe = System.Environment.GetEnvironmentVariable("COR64_IDLEFF_ONEPROBE") != "0";
        [NonSerialized] public bool IdleProbeNotLoop;
        long IdleBatchesPossibleCore(int n, long cap) {
            IdleProbeNotLoop = false;
            if (!s_IdleSkip || (n & 1) != 0) return 0;
            m_IdleSpanDelay = false;
            if (s_IdleFFDelay && AtIdleDelaySlot()) {
                if (m_InjectedInst.HasValue || m_ExceptionReturn || (BU_UnconditionalPending && BU_ConditionalPending)) return 0;
                if (!Cop0.IdleSlicePossible((uint)n)) return 0;
                m_IdleSpanDelay = true;
                return Cop0.IdleSliceHorizon((uint)n, cap);
            }
            if (BU_ConditionalPending || BU_UnconditionalPending || BU_DelaySlotNext || BU_ExecuteBranchDelay || BU_NullifyNext) { IdleProbeNotLoop = s_IdleFFDelay; return 0; }
            if (m_InjectedInst.HasValue || m_ExceptionReturn) { IdleProbeNotLoop = s_IdleFFDelay; return 0; }
            long pc = PC;
            if (!IdlePhys(pc, 4, out long phys)) { IdleProbeNotLoop = s_IdleFFDelay; return 0; }
            if (!IdleLoopHead(pc, phys)) { IdleProbeNotLoop = s_IdleFFDelay; return 0; }
            if (!Cop0.IdleSlicePossible((uint)n)) return 0;
            return Cop0.IdleSliceHorizon((uint)n, cap);
        }

        // Called before the span's VI/AI ticks (the CPU part of each batch comes first): with n >= the check interval every
        // skipped batch performs an interrupt check, whose effect (RCP bit refresh, CE clear) is idempotent.
        public bool BeginIdleSpan(int n) {
            if (n < s_IntCheckEvery) return false;
            Cop0.IdleSliceApplyCheck();
            return true;
        }

        // With BeginIdleSpan: same state as `batches` successful TryIdleSkip(n) calls in a row.
        public void ApplyIdleBatches(int n, long batches) {
            long steps = (long)n * batches;
            TLBTick(steps);
            long c = m_IntCheckCounter + steps;
            m_IntCheckCounter = (int)(c % s_IntCheckEvery);
            Cop0.MipsTimerTick((ulong)steps);
            m_LastFetchAddress = m_IdleSpanDelay ? PC : PC + 4;
            IdleSkippedSteps += steps;
        }

        // [cer64plus] Skip n (even) steps from the idle loop's delay-slot phase (part of COR64_IDLE_SKIP). The loop is a 2-step cycle,
        // so after an even number of steps PC and the branch state are exactly as before; only Count, TLB Random, the
        // interrupt-check cadence and the Cause CE clears advance (no interrupt can land: IdleSlicePossible).
        public bool TryIdleSkipDelayPhase(int n) {
            if ((n & 1) != 0 || !AtIdleDelaySlot()) return false; // AtIdleDelaySlot is false when the idle skip is off
            if (m_InjectedInst.HasValue || m_ExceptionReturn || (BU_UnconditionalPending && BU_ConditionalPending)) return false;
            if (!Cop0.IdleSlicePossible((uint)n)) return false;
            int checks = 0;
            for (int i = 0; i < n; i++) {
                TLBTick();
                if (++m_IntCheckCounter >= s_IntCheckEvery) { m_IntCheckCounter = 0; checks++; }
            }
            if (checks > 0) Cop0.IdleSliceApplyCheck(); else Cop0.ClearCauseCe();
            Cop0.MipsTimerTick((ulong)n);
            m_LastFetchAddress = PC;
            IdleSkippedSteps += n;
            return true;
        }

        // True when the CPU is about to execute the delay slot of the idle loop `beq zero,zero,-1; nop`.
        public bool AtIdleDelaySlot() {
            if (AtJIdleDelaySlot()) return true;
            if (!s_IdleSkip || !BU_ExecuteBranchDelay || !BU_ConditionalPending || BU_NullifyNext || BU_DelaySlotNext) return false;
            long pc = PC;
            if (!IdlePhys(pc, -4, out long phys)) return false;
            if (BU_TargetAddress != pc - 4 || FetchInstruction(phys) != 0x00000000) return false;
            uint head = FetchInstruction(phys - 4);
            if (head == 0x1000FFFF) return true; // beq zero,zero,-1 (BU_ConditionalPending tested above)
            return false;
        }

        // [scripts_speed] COR64_SCRIPTS_SPEED_MIDLE (default on): the idle loop may also sit in TLB-mapped code (GoldenEye's
        // is at 7000071C). The words are found through the TLB with no side effects; the loop and its other word (`other`
        // bytes away) must lie in the same 4 KB page and the fetch must not fault, so each skipped step's fetch would have
        // translated to the same place, like a KSEG0 fetch. KSEG0/KSEG1 is unchanged.
        public static readonly bool s_MIdle = System.Environment.GetEnvironmentVariable("COR64_SCRIPTS_SPEED_MIDLE") != "0";
        private bool IdlePhys(long pc, int other, out long phys) {
            if (((ulong)pc & 0xC0000000UL) == 0x80000000UL || ((ulong)pc & 0xFFFFFFFFC0000000UL) == 0xFFFFFFFF80000000UL) { phys = pc & 0x1FFFFFFF; return true; }
            phys = 0;
            if (!s_MIdle || !FastFetchMode || Cop0.IsNonMaskablePending) return false;
            if ((((uint)pc + (uint)other) >> 12) != ((uint)pc >> 12)) return false;
            if (!TLB.TryTranslateFetchQuiet(pc, out phys)) return false;
            return phys + 8 <= 0x800000 && phys >= 4;
        }

        // [scripts_speed] COR64_SCRIPTS_SPEED_JIDLE (default on): the idle skip also accepts `j self; nop` (libultra's idle
        // thread `for (;;);` as built by many games, e.g. Mario Party at 80000514). It is the same 2-step cycle as
        // `beq zero,zero,-1; nop`: after an even number of steps PC and the branch unit are as before, and the jump has no
        // other effect. In its delay-slot phase the branch unit holds BU_UnconditionalPending instead of BU_ConditionalPending.
        public static readonly bool s_JIdle = System.Environment.GetEnvironmentVariable("COR64_SCRIPTS_SPEED_JIDLE") != "0";
        private static uint JSelf(long pc) => 0x08000000u | (((uint)pc & 0x0FFFFFFCu) >> 2);
        private bool IdleLoopHead(long pc, long phys) {
            uint w = FetchInstruction(phys);
            if (w != 0x1000FFFF && !(s_JIdle && w == JSelf(pc) && (((uint)pc + 4) & 0xF0000000u) == ((uint)pc & 0xF0000000u))) return false;
            return FetchInstruction(phys + 4) == 0x00000000;
        }
        // delay-slot phase of `j self; nop` (pc = the nop)
        private bool AtJIdleDelaySlot() {
            if (!s_IdleSkip || !s_JIdle || !BU_ExecuteBranchDelay || !BU_UnconditionalPending || BU_ConditionalPending || BU_NullifyNext || BU_DelaySlotNext) return false;
            long pc = PC;
            if (!IdlePhys(pc, -4, out long phys)) return false;
            return BU_TargetAddress == pc - 4 && FetchInstruction(phys) == 0x00000000 && FetchInstruction(phys - 4) == JSelf(pc - 4);
        }

        // [cer64plus] measurement aid: ns per handler call for the raw instruction word (min of 5 x iters).
        // Registers 8..15 are pointed at a scratch RDRAM area first so loads/stores hit memory, like game code.
        public (double handlerNs, string name) MicroBench(uint raw, int iters)
        {
            var d = Disassembler.Decode(raw); var h = CallTable[d];
            if (h == null || d.IsInvalid) return (0, "?");
            for (int r = 8; r < 16; r++) Writeback64(r, 0xFFFFFFFF80300000UL);
            double best = double.MaxValue;
            var sw = new System.Diagnostics.Stopwatch();
            for (int rep = 0; rep < 5; rep++) {
                sw.Restart();
                for (int i = 0; i < iters; i++) { h(d); BU_ConditionalPending = false; BU_UnconditionalPending = false; BU_DelaySlotNext = false; BU_NullifyNext = false; }
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / iters);
            }
            return (best, d.Op.Op);
        }

        // [cer64plus] measurement aid: ns per full Step() on a synthetic loop at physical 0x300000 (KSEG0 0x80300000):
        // 63 x `addiu r2,r2,1` then `j loop` + `nop`. Interrupts are masked (SR.IE cleared) so nothing else runs.
        public double MicroStepLoop(int steps)
        {
            uint basePhys = 0x300000;
            for (int i = 0; i < 63; i++) PhysicalMemory.U32(basePhys + i * 4, 0x24420001);
            PhysicalMemory.U32(basePhys + 63 * 4, 0x08000000u | ((0x80300000u >> 2) & 0x03FFFFFF));
            PhysicalMemory.U32(basePhys + 64 * 4, 0);
            Cop0State.Status.Write(Cop0State.Status.Read() & ~1u);
            ClearBranchState();
            PC = unchecked((long)(int)0x80300000);
            for (int i = 0; i < 10000; i++) Step();
            double best = double.MaxValue;
            var sw = new System.Diagnostics.Stopwatch();
            for (int rep = 0; rep < 5; rep++) {
                sw.Restart();
                for (int i = 0; i < steps; i++) Step();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / steps);
            }
            return best;
        }

        protected override Action<DecodedInstruction> LookupHandler(in DecodedInstruction decoded) => CallTable[decoded];

        // [cer64plus] measurement aid: ns per call of each piece of a step, on the live interpreter (min of 5 x n calls).
        public System.Collections.Generic.List<(string, double)> MicroParts(int n)
        {
            var res = new System.Collections.Generic.List<(string, double)>();
            long phys = 0x300000; long pc = unchecked((long)(int)0x80300000);
            PhysicalMemory.U32(phys, 0x24420001);
            void T(string name, Action body) {
                double best = double.MaxValue; var sw = new System.Diagnostics.Stopwatch();
                for (int rep = 0; rep < 5; rep++) { sw.Restart(); for (int i = 0; i < n; i++) body(); best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / n); }
                res.Add((name, best));
            }
            uint sink = 0; long ls = 0;
            T("empty lambda (harness cost)", () => { });
            T("FetchInstruction (memory controller)", () => { sink += FetchInstruction(phys); });
            T("DecodeCached (fetch + cache check)", () => { ref readonly var d = ref DecodeCached(phys, out var h); sink += d.Inst.inst; });
            T("copy into a heap field (old per-step cost)", () => { m_CurrentInst = DecodeCached(phys, out var h); });
            T("TryVirtToPhys (KSEG0)", () => { TryVirtToPhys(false, pc, out ls); });
            T("TLBTick", () => TLBTick());
            T("Cop0.CheckInterrupts", () => Cop0.CheckInterrupts(pc, false));
            T("Cop0.ClearCauseCe", () => Cop0.ClearCauseCe());
            T("CheckIfNeedServicing", () => CheckIfNeedServicing());
            T("Cop0.MipsTimerTick(1)", () => Cop0.MipsTimerTick(1));
            T("Cop0.Cop1Usable", () => { if (Cop0.Cop1Usable) sink++; });
            T("Cop0.CheckExceptions", () => Cop0.CheckExceptions(pc, false));
            var h0 = CallTable[m_CurrentInst];
            T("handler delegate call (addiu)", () => h0(m_CurrentInst));
            Console.Error.WriteLine(sink + ls);
            return res;
        }

        [NonSerialized] internal uint m_StepWord = 0xFFFFFFFF; // [jit_cost1] the word the last Step() decoded (all ones: none)
        public override void Step()
        {
            m_QuietValid = false; m_DelayFromRec = false; // a generic step may run any handler
            m_StepWord = 0xFFFFFFFF;
            if (s_Cat14Stat) Cat14Count((uint)PC);
            ExecuteNext();
        }
        // [climb4 speed_cat14] COR64_SPDCAT14_STAT=1 (measurement only): interpreter Step() calls by PC, printed at exit
        public static readonly bool s_Cat14Stat = Environment.GetEnvironmentVariable("COR64_SPDCAT14_STAT") == "1";
        static readonly System.Collections.Generic.Dictionary<uint, long> s_Cat14Pc = new(); static long s_Cat14N;
        static void Cat14Count(uint pc) {
            if (s_Cat14N++ == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) => { var l = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<uint, long>>(s_Cat14Pc); l.Sort((a, b) => b.Value.CompareTo(a.Value)); Console.WriteLine($"CAT14STEP n={s_Cat14N} " + string.Join(" ", l.GetRange(0, Math.Min(25, l.Count)).ConvertAll(kv => kv.Key.ToString("X8") + "=" + kv.Value))); };
            s_Cat14Pc.TryGetValue(pc, out long c); s_Cat14Pc[pc] = c + 1;
        }

        // [cer64plus] First part of a step (shared by ExecuteNext and the block runner): TLB Random tick, interrupt check
        // cadence, and entry into a pending exception handler (which changes PC).
        public static readonly System.Collections.Generic.Dictionary<uint, uint> PcInst = new(); // [timing] debug aid with PcHist
        public static readonly bool s_PcHist = Environment.GetEnvironmentVariable("COR64_CPU_PCHIST") == "1"; // measurement only
        public static readonly System.Collections.Generic.Dictionary<uint, long> PcHist = new();
        static uint s_PrevPc; static int s_LowEntries; // debug aid (COR64_CPU_PCHIST)
        public static long s_PcHistN; private static readonly long s_PcHistSkip = long.TryParse(Environment.GetEnvironmentVariable("COR64_CPU_PCHIST_SKIP"), out long sk) ? sk : 0;
        private void StepPrologue()
        {
            TLBTick();

            // Check if the system controller detects pending events
            // [cer64plus] Full interrupt check every 4th instruction (COR64_INT_CHECK=1 restores every instruction).
            // The timer test is Count >= Compare, so nothing is missed; interrupts are taken at most 3 instructions later.
            // (Cause.CE is no longer cleared between interrupt checks: CE only changes when an exception is raised.)
            if (++m_IntCheckCounter >= s_IntCheckEvery) { m_IntCheckCounter = 0; Cop0.CheckInterrupts(PC, BU_ExecuteBranchDelay); }

            // Check if the CPU should start service handling
            if (!s_Guards || Cop0.ServicingRequred) CheckIfNeedServicing();
        }

        // [cer64plus] Inline guards: the per-step servicing and exception checks are only called when they can act
        // (same results; COR64_STEP_GUARDS=0 calls them every step as before).
        private static readonly bool s_Guards = Environment.GetEnvironmentVariable("COR64_STEP_GUARDS") != "0";

        private bool ExecuteNext() {
            // TODO: still handle the result

            #if !OPTIMAL
            if (Debugger.IsBreakActive) {
                return true;
            }
            #endif

            StepPrologue();
            return ExecuteAfterPrologue();
        }

        // [cer64plus] Rest of a step after its prologue (also used by the recompiler when a step must finish generically).
        internal bool ExecuteAfterPrologue() {
            /* Virtual To Physical */
            if (!TryFetchToPhys(PC, out long physicalAddress))
            {
                // Exception raised during fetch stage
                // [tlb_exc] a TLB miss on the fetch: enter the handler now (EPC = this PC, or the branch for a delay slot)
                if (Cop0.TlbExceptionPending) {
                    Cop0.CheckExceptions(PC, BU_ExecuteBranchDelay);
                    CheckIfNeedServicing();
                    BU_ExecuteBranchDelay = false;
                }
                // Return right away, let the exception handler work on the next step
                return true;
            }

            bool hleJump = false;

            // CLEANUP: Move this into a conditional method?
            #if ENABLE_CPU_HOOKS
            if (m_Hooks.TryGetValue(PC, out CpuCodeHook hook))
            {
                var result = hook.Execute();

                if (result == CpuCodeHook.ReturnControl.HLECall) {
                    // Execute a 'jr ra' branch
                    CurrentInst = Disassembler.Decode(0x03E00008);
                    hleJump = true;
                }
                else {
                    CurrentInst = DecodeNext(physicalAddress);
                }
            }
            else {
                CurrentInst = DecodeNext(physicalAddress);
            }

            #else

            // [cer64plus] decode-cache entry by reference with its cached handler (was: two 48-byte copies plus a CallTable lookup)
            Action<DecodedInstruction> instHandler;
            if (m_InjectedInst.HasValue) {
                CurrentInst = m_InjectedInst.Value;
                m_InjectedInst = null;
                instHandler = m_CurrentInst.IsInvalid ? null : CallTable[m_CurrentInst];
                return StepBody(in m_CurrentInst, instHandler, hleJump);
            }
            ref readonly DecodedInstruction cur = ref DecodeCached(physicalAddress, out instHandler, out m_CurrentSlot);
            m_StepWord = cur.Inst.inst; // [jit_cost1]

            #endif

            #if ENABLE_CPU_HOOKS
            var instHandler = CurrentInst.IsInvalid ? null : CallTable[CurrentInst];
            #endif
            if (s_PcHist && ++s_PcHistN > s_PcHistSkip) { uint key = (uint)PC; PcHist.TryGetValue(key, out long c); PcHist[key] = c + 1; PcInst[key] = cur.Inst.inst;
                if (key - 0x80000000u < 0x200u && s_PrevPc - 0x80000000u >= 0x200u && s_LowEntries++ < 40) Console.WriteLine($"LOWENTRY {s_PrevPc:X8} -> {key:X8} ra={(uint)ReadGPR64(31):X8} sp={(uint)ReadGPR64(29):X8}");
                s_PrevPc = key; }
            return StepBody(in cur, instHandler, hleJump);
        }

        // [cer64plus] Second part of a step (shared with the block runner): Count tick, the handler, exceptions, branches.
        // CurrentInst holds the instruction; instHandler its CallTable entry.
        private bool StepBody(in DecodedInstruction cur, Action<DecodedInstruction> instHandler, bool hleJump)
        {
            m_LastFetchAddress = PC;

            Cop0.MipsTimerTick(1);

            if (cur.IsInvalid) {
                if (CompatLog.On) CompatLog.Note("cpu:invalid-op", $"pc={PC:X8}");
                return false;
            }

            if (s_OpStats) { int oid = cur.Op.ID; if (oid < OpCounts.Length) { OpCounts[oid]++; OpNames[oid] ??= cur.Op.Op; } }

            if (instHandler == null)
                throw new NotSupportedException(String.Format("Opcode {0} not supported", CurrentInst.Op.Op));

            #if !OPTIMAL
            Debugger.CheckInstructionBreakpoints(CurrentInst);
            #endif

            #if DEBUG
            TraceInstruction(PC, CurrentInst);
            #endif

            /* --- EXECUTE INSTRUCTION HANDLER */

            #if CPU_PROFILER
            BeginInstructionProfile(CurrentInst);
            #endif

            // EXECUTE HANDLER
            // [cer64plus] COP1 (0x11), LWC1, LDC1, SWC1, SDC1 fault with Coprocessor Unusable (CE=1) when SR.CU1 is clear.
            if (s_Ring) RingRecord(cur.Inst.inst);
            uint majorOp = cur.Inst.inst >> 26;
            if ((majorOp == 0x11 || majorOp == 0x31 || majorOp == 0x35 || majorOp == 0x39 || majorOp == 0x3D) && !Cop0.Cop1Usable)
                Cop0.RaiseCopUnusable(1);
            else if (s_Ref) { RefBefore(); instHandler(cur); RefAfter(cur.Inst.inst); }
            else
                instHandler(cur);

            #if CPU_PROFILER
            EndInstructionProfile();
            #endif

            #if DEBUG
            DebugInstruction(CurrentInst, PC, m_TakenException);
            #endif

            DebugFpu(CurrentInst);

            /* --------------------------------- */

            return StepTail(hleJump);
        }

        // [cer64plus] End of a step after the handler ran: ERET return, exceptions, servicing, PC advance, branch commit.
        internal bool StepTail(bool hleJump)
        {

            // Returned from vector routine via ERET
            // We don't do anything else, because we have to
            // still execute the interrupted instruction
            if (m_ExceptionReturn) {
                m_ExceptionReturn = false;
                return true;
            }

            // Checked for rasied exceptions
            if (!s_Guards || Cop0.ExceptionPending) Cop0.CheckExceptions(PC, BU_ExecuteBranchDelay);

            // Check if the CPU should start service handling
            if (!hleJump) {
                if (!(s_Guards ? Cop0.ServicingRequred && CheckIfNeedServicing() : CheckIfNeedServicing())) {
                    // Shift PC to next instruction
                    PC += 4;
                }
            }

            BU_ExecuteBranchDelay = false;

            // Peform the branch or jump if conditions are met
            if (hleJump || ((BU_ConditionalPending || BU_UnconditionalPending) && !BU_DelaySlotNext)) {
                /* Should always be a word-aligned relative PC jump */
                /* Always force 32-bit addresses */
                PC = (uint)BU_TargetAddress;
                BU_ConditionalPending = false;
                BU_UnconditionalPending = false;
                return true;
            }

            // The delay slot is next
            if (BU_DelaySlotNext) {

                if (BU_NullifyNext) {
                    BU_ExecuteBranchDelay = false;
                    BU_DelaySlotNext = false;
                    BU_NullifyNext = false;
                    PC += 4; // Move PC to inst after delay slot
                }
                else {
                    BU_DelaySlotNext = false;
                    BU_ExecuteBranchDelay = true;
                }
            }

            return true;
        }

        private DecodedInstruction DecodeNext(long physicalAddress) {
            if (m_InjectedInst.HasValue)
            {
                var decoded = m_InjectedInst.Value;
                m_InjectedInst = null;
                return decoded;
            }
            else
            {
                return Decode(physicalAddress);
            }
        }

        private void DebugInstruction(DecodedInstruction instruction, long pc, bool inInt)
        {
            if (core_InstDebugMode != InstructionDebugMode.None)
            {
                if (core_InstDebugMode == InstructionDebugMode.Full || (core_InstDebugMode == InstructionDebugMode.ProgramOnly && !InBootMode)) {
                    if (m_InfiniteJumpAddress != null) {
                        if (PC == m_InfiniteJumpAddress || PC == (m_InfiniteJumpAddress + 4)) {
                            return;
                        }
                    }
                    else {
                        if (m_WarnInfiniteJump) {
                            m_InfiniteJumpAddress = PC;
                        }
                    }

                    string memNote = null;

                    if (core_MemAccessNote != null) {
                        memNote = "     # " + core_MemAccessNote.ReadMeta();
                        core_MemAccessNote = null;
                    }

                    String type = inInt ? "INTR" : (InBootMode ? "BOOT" : "PROG");

                    #if TESTING
                    type = "TEST";
                    #endif

                    // if (memNote != null)
                    Console.WriteLine("CPU {0:X8} {4:X8} |{1}| {2} {3}",
                        pc,
                        type,
                        Disassembler.Disassemble(pc, instruction),
                        memNote ?? "",
                        instruction.Inst.inst
                        );
                }
            }
        }

        [Conditional("FPU_DEBUG_INST")]
        private void DebugFpu(DecodedInstruction inst) {
            if (inst.Op.Family == OperationFamily.Fpu || inst.Op.Family == OperationFamily.LoadFpu || inst.Op.Family == OperationFamily.StoreFpu) {
                Console.WriteLine("FPU {0:X8} {1}",
                    PC,
                    Disassembler.Disassemble(inst)
                    );
            }
        }

        // [cpu_diff] Debug aid (COR64_CPUDIFF_RING=1, interpreter steps only: use with COR64_CPU_REC=0): keeps the last
        // 512 steps (PC, instruction, all GPRs before it) and writes them to telemetry/ring.txt at the first TLB exception.
        internal static readonly bool s_Ring = Environment.GetEnvironmentVariable("COR64_CPUDIFF_RING") == "1";
        private static readonly int RingN = int.TryParse(Environment.GetEnvironmentVariable("COR64_CPUDIFF_RING_N"), out var rn) ? rn : 512;
        private static readonly ulong[] s_RingRegs = new ulong[RingN * 32];
        private static readonly uint[] s_RingPc = new uint[RingN], s_RingInst = new uint[RingN];
        private static long s_RingPos; private static bool s_RingDumped; private static Interpreter s_RingOwner;
        internal static void RingDumpStatic(string why) => s_RingOwner?.RingDump(why);
        private void RingRecord(uint inst) {
            s_RingOwner = this; int i = (int)(s_RingPos++ % RingN);
            s_RingPc[i] = (uint)PC; s_RingInst[i] = inst;
            for (int r = 0; r < 32; r++) s_RingRegs[i * 32 + r] = ReadGPR64(r);
        }
        private static readonly int s_RingAt = int.TryParse(Environment.GetEnvironmentVariable("COR64_CPUDIFF_RING_AT"), out var ra) ? ra : 1;
        private static int s_RingHits;
        internal void RingDump(string why) {
            if (!s_Ring || s_RingDumped || ++s_RingHits < s_RingAt) return; s_RingDumped = true;
            var sb = new StringBuilder(); sb.AppendLine("why " + why + " steps " + s_RingPos);
            for (long k = Math.Max(0, s_RingPos - RingN); k < s_RingPos; k++) {
                int i = (int)(k % RingN);
                sb.Append(string.Format("{0:X8} {1:X8} {2,-28}", s_RingPc[i], s_RingInst[i], Disassembler.Disassemble(s_RingPc[i], Disassembler.Decode(s_RingInst[i]))));
                for (int r = 1; r < 32; r++) sb.Append(' ').Append(r).Append('=').Append(s_RingRegs[i * 32 + r].ToString("X"));
                sb.AppendLine();
            }
            System.IO.Directory.CreateDirectory("telemetry");
            System.IO.File.WriteAllText("telemetry/ring.txt", sb.ToString());
            System.IO.File.WriteAllText("telemetry/tlb.txt", TLB.DumpEntries() + string.Format("EntryHi={0:X16} PageMask={1:X8}\n", Cop0.REGS.TLBRegs.EntryHi, Cop0.REGS.TLBRegs.PageMask));
            unsafe { System.IO.File.WriteAllBytes("telemetry/ram.bin", new ReadOnlySpan<byte>((byte*)RdramWords, 8 << 20).ToArray()); }
        }

        public void InjectInst(DecodedInstruction inst)
        {
            m_InjectedInst = inst;
        }

        private void ConvertToSingle(FpuValueType format, int source, int dest)
        {
            switch (format) {
                case FpuValueType.FSingle: throw new InvalidOperationException("float32 to float32");
                case FpuValueType.FDouble: WriteFPR_S(dest, (float)ReadFPR_D(source)); break;
                case FpuValueType.Word: WriteFPR_S(dest, (int)ReadFPR_W(source)); break;
                case FpuValueType.Doubleword:  WriteFPR_S(dest, (long)ReadFPR_DW(source)); break;
                default: throw new InvalidOperationException("cannot convert to reserved format");
            }
        }

        private void ConvertToDouble(FpuValueType format, int source, int dest)
        {
            switch (format) {
                case FpuValueType.FSingle: WriteFPR_D(dest, ReadFPR_S(source)); break;
                case FpuValueType.FDouble: throw new InvalidOperationException("float64 to float64");
                case FpuValueType.Word: WriteFPR_D(dest, (int)ReadFPR_W(source)); break;
                case FpuValueType.Doubleword: WriteFPR_D(dest, (long)ReadFPR_DW(source)); break;
                default: throw new InvalidOperationException("cannot convert to reserved format");
            }
        }

        private void ConvertToFixed32(FpuValueType format, int source, int dest)
        {
            switch (format) {
                case FpuValueType.FSingle: { double r = RoundBy(ReadFPR_S(source), State.FCR.RoundMode); WriteFPR_W(dest, unchecked((uint)(int)r)); break; }
                case FpuValueType.FDouble: { double r = RoundBy(ReadFPR_D(source), State.FCR.RoundMode); WriteFPR_W(dest, unchecked((uint)(int)r)); break; }
                case FpuValueType.Word: throw new InvalidOperationException("u32 to u32");
                case FpuValueType.Doubleword: throw new InvalidOperationException("u32 to u64");
                default: throw new InvalidOperationException("cannot convert to reserved format");
            }
        }

        private void ConvertToFixed64(FpuValueType format, int source, int dest)
        {
            switch (format) {
                case FpuValueType.FSingle: { double r = RoundBy(ReadFPR_S(source), State.FCR.RoundMode); WriteFPR_DW(dest, unchecked((ulong)(long)r)); break; }
                case FpuValueType.FDouble: { double r = RoundBy(ReadFPR_D(source), State.FCR.RoundMode); WriteFPR_DW(dest, unchecked((ulong)(long)r)); break; }
                case FpuValueType.Word: throw new InvalidOperationException("u32 to u64");
                case FpuValueType.Doubleword: throw new InvalidOperationException("u64 to u64");
                default: throw new InvalidOperationException("cannot convert to reserved format");
            }
        }


        /* ------------------------------------------------------------------
         * Implemented Opcodes
         * ------------------------------------------------------------------
         */

        public override void Add32(DecodedInstruction inst)
        {
            if (IsReserved(inst))
            {
                SetExceptionState(ExceptionType.Reserved);
                return;
            }

            /* Operands must be valid 32-bit sign extended in 64-bit mode */
            /* Immediate is always signed-extended */
            /* Unsigned operations don't cause integer overflow exceptions */

            /* We cheat by doing 32-bit sign-extending for immedate in 64-bit mode,
             * the processor will use 64-bit values to add, and check overflow
             * then store the result as a signed 32-bit value, we force everything to
             * use 32-bits so the CLR runtime can throw exceptions for us */

            bool isUnsigned = inst.IsUnsigned();
            bool isImmediate = inst.IsImmediate();
            uint operandA = ReadGPR32(inst.Source);
            uint operandB = isImmediate ? (uint)(short)inst.Immediate : ReadGPR32(inst.Target);
            uint result = 0;
            int dest = isImmediate ? inst.Target : inst.Destination;

            if (isUnsigned)
            {
                unchecked
                {
                    result = operandA + operandB;
                }
            }
            else
            {
                int r = 0;
                int _a = (int)operandA;
                int _b = (int)operandB;

                try
                {
                    checked
                    {
                        r = _a + _b;
                    }
                }
                catch (OverflowException)
                {
                    SetExceptionState(ExceptionType.Overflow);
                    return;
                }

                result = (uint)r;
            }

            Writeback32(dest, result);
        }

        public override void BitwiseLogic(DecodedInstruction inst)
        {
            if (IsReserved(inst))
            {
                SetExceptionState(ExceptionType.Reserved);
                return;
            }

            bool isImmediate = inst.IsImmediate();
            int dest = isImmediate ? inst.Target : inst.Destination;
            ulong source = ReadGPR64(inst.Source);
            ulong target = isImmediate ? inst.Immediate : ReadGPR64(inst.Target);

            void Bitwise32(ArithmeticOp compareOp, uint a, uint b)
            {
                uint result = 0;

                switch (compareOp)
                {
                    case ArithmeticOp.AND: result = a & b; break;
                    case ArithmeticOp.OR:  result = a | b; break;
                    case ArithmeticOp.XOR: result = a ^ b; break;
                    case ArithmeticOp.NOR: result = ~(a | b); break;
                    default: throw new InvalidOperationException("Bitwise logic 32");
                }

                Writeback64((isImmediate ? inst.Target : inst.Destination), result);
            }

            void Bitwise64(ArithmeticOp compareOp, ulong a, ulong b)
            {
                ulong result = 0;

                switch (compareOp)
                {
                    case ArithmeticOp.AND: result = a & b; break;
                    case ArithmeticOp.OR: result = a | b; break;
                    case ArithmeticOp.XOR: result = a ^ b; break;
                    case ArithmeticOp.NOR: result = ~(a | b); break;
                    default: throw new InvalidOperationException("Bitwise logic 64");
                }

                Writeback64((isImmediate ? inst.Target : inst.Destination), result);
            }


            if (IsOperation64)
            {
                Bitwise64(inst.Op.ArithmeticType, source, target);
            }
            else
            {
                Bitwise32(inst.Op.ArithmeticType, (uint)source, (uint)target);
            }
        }

        public override void Add64(DecodedInstruction inst)
        {
            if (IsReserved(inst))
            {
                SetExceptionState(ExceptionType.Reserved);
                return;
            }

            bool isUnsigned = inst.IsUnsigned();
            bool isImmediate = inst.IsImmediate();
            ulong operandA = ReadGPR64(inst.Source);
            ulong operandB = isImmediate ? (ulong)(short)inst.Immediate : ReadGPR64(inst.Target);
            ulong result = 0;
            int dest = isImmediate ? inst.Target : inst.Destination;

            if (isUnsigned)
            {
                unchecked
                {
                    result = operandA + operandB;
                }
            }
            else
            {
                long r = 0;
                long _a = (long)operandA;
                long _b = (long)operandB;

                try
                {
                    checked
                    {
                        r = _a + _b;
                    }
                }
                catch (OverflowException)
                {
                    SetExceptionState(ExceptionType.Overflow);
                    return;
                }

                result = (ulong)r;
            }

            Writeback64(dest, result);
        }

        public override void Divide32(DecodedInstruction inst)
        {
            if (IsReserved(inst))
            {
                SetExceptionState(ExceptionType.Reserved);
                return;
            }

            bool isUnsigned = inst.IsUnsigned();
            uint operandA = ReadGPR32(inst.Source);
            uint operandB = ReadGPR32(inst.Target);
            uint result = 0;
            uint remainder = 0;

            try
            {
                if (isUnsigned)
                {
                    /* Divide by zero */
                    if (operandB == 0)
                    {
                        WritebackHiLo32(operandA, 0xFFFFFFFFU);
                        return;
                    }

                    unchecked
                    {
                        result = operandA / operandB;
                        remainder = operandA % operandB;
                    }
                }
                else
                {
                    /* Divide by zero */
                    if (operandB == 0)
                    {
                        WritebackHiLo32(operandA, (int)operandA < 0 ? 1U : 0xFFFFFFFFU);
                        return;
                    }

                    int q = 0;
                    int r = 0;
                    int _a = (int)operandA;
                    int _b = (int)operandB;

                    checked
                    {
                        q = _a / _b;
                        r = _a % _b;
                    }

                    result = (uint)q;
                    remainder = (uint)r;
                }

                WritebackHiLo32(remainder, result);
            }
            catch (ArithmeticException)
            {
                WritebackHiLo32(0, operandA);
                return;
            }
        }

        public override void Divide64(DecodedInstruction inst)
        {
            if (IsReserved(inst))
            {
                SetExceptionState(ExceptionType.Reserved);
                return;
            }

            bool isUnsigned = inst.IsUnsigned();
            ulong operandA = ReadGPR64(inst.Source);
            ulong operandB = ReadGPR64(inst.Target);
            ulong result = 0;
            ulong remainder = 0;

            try
            {
                if (isUnsigned)
                {
                    /* Divide by zero */
                    if (operandB == 0)
                    {
                        WritebackHiLo64(operandA, 0xFFFFFFFFFFFFFFFFUL);
                        return;
                    }

                    unchecked
                    {
                        result = operandA / operandB;
                        remainder = operandA % operandB;
                    }
                }
                else
                {
                    /* Divide by zero */
                    if (operandB == 0)
                    {
                        WritebackHiLo64(operandA, (long)operandA < 0 ? 1UL : 0xFFFFFFFFFFFFFFFFUL);
                        return;
                    }

                    long q = 0;
                    long r = 0;
                    long _a = (long)operandA;
                    long _b = (long)operandB;

                    checked
                    {
                        q = _a / _b;
                        r = _a % _b;
                    }

                    result = (ulong)q;
                    remainder = (ulong)r;
                }

                WritebackHiLo64(remainder, result);
            }
            catch (ArithmeticException)
            {
                WritebackHiLo64(0, operandA);
                return;
            }
        }

        public override void Multiply32(DecodedInstruction inst)
        {
            if (IsReserved(inst))
            {
                SetExceptionState(ExceptionType.Reserved);
                return;
            }

            bool isUnsigned = inst.IsUnsigned();
            uint operandA = ReadGPR32(inst.Source);
            uint operandB = ReadGPR32(inst.Target);
            ulong result = 0;
            uint resultLo = 0;
            uint resultHi = 0;

            /* MIPS document says no overflow exceptions, ever... */
            unchecked
            {
                if (isUnsigned)
                    result = (ulong)operandA * (ulong)operandB;
                else
                    result = (ulong)((long)(int)operandA * (long)(int)operandB);

            }

            resultLo = (uint)(result);
            resultHi = (uint)(result >> 32);

            WritebackHiLo32(resultHi, resultLo);
        }

        public override void Multiply64(DecodedInstruction inst)
        {
            if (IsReserved(inst))
            {
                SetExceptionState(ExceptionType.Reserved);
                return;
            }

            bool isUnsigned = inst.IsUnsigned();
            ulong operandA = ReadGPR64(inst.Source);
            ulong operandB = ReadGPR64(inst.Target);

            /* MIPS document says no overflow exceptions, ever... */
            unchecked
            {
                UInt128 value;

                if (isUnsigned)
                {
                    value = CoreUtils.Multiply64_Unsigned(operandA, operandB);
                }
                else
                {
                    value = CoreUtils.Multiply64_Signed(operandA, operandB);
                }

                WritebackHiLo64(value.hi, value.lo);
            }
        }

        // [tlb_exc] SLLV/SRLV/SRAV shift by rs[4:0] (VR4300 manual; ares SLLV/SRLV/SRAV); the old 0x3F mask turned a
        // shift by 32..63 into 0. GoldenEye's inflate (huft_build) shifts by 33 at boot. COR64_TLBEXC_SHV=0 = old mask.
        internal static readonly int s_ShvMask = Environment.GetEnvironmentVariable("COR64_TLBEXC_SHV") == "0" ? 0x3F : 0x1F;

        public override void Shift32(DecodedInstruction inst)
        {
            if (IsReserved(inst))
            {
                SetExceptionState(ExceptionType.Reserved);
                return;
            }

            int shiftAmount = 0;

            if (inst.IsVariableShift())
            {
                shiftAmount = (int)(ReadGPR32(inst.Source) & s_ShvMask);
            }
            else
            {
                shiftAmount = inst.ShiftAmount;
            }

            ulong value;

            bool isSigned;

            if (IsOperation64) {
                value = ReadGPR64(inst.Target);
                isSigned = (value & 0x4000000000000000) == 0x4000000000000000;
            }
            else {
                value = ReadGPR32(inst.Target);
                isSigned = (value & 0x80000000) == 0x80000000;
            }

            if (inst.Op.ArithmeticType == ArithmeticOp.LSHIFT)
            {
                value <<= shiftAmount;
            }
            else
            {
                // Unsigned always inserts 0s
                if (inst.IsUnsigned())
                    value &= 0xFFFFFFFFU;

                // Do a 64-bit shift
                value >>= shiftAmount;

                // Force the value to be 32-bit
                value &= 0xFFFFFFFFU;

                // Sign extend
                if (!inst.IsUnsigned() && isSigned)
                {
                    /* Sign extend */
                    value |= ~(~0U >> shiftAmount);
                }
            }

            Writeback32(inst.Destination, value);
        }

        // [cpu_diff] DSRA/DSRAV/DSRA32 fill with bit 63 (ares: rt.s64 >> sa). The old code tested bit 62, so a value with
        // bit 62 set and bit 63 clear came out negative (Yoshi's Story libultra 64-bit math at 80082DAC). Only the
        // interpreter implements 64-bit shifts (the IL and x64 blocks call this handler). COR64_CPUDIFF_DSRA=0 = old.
        internal static readonly bool s_DsraFix = Environment.GetEnvironmentVariable("COR64_CPUDIFF_DSRA") != "0";

        public override void Shift64(DecodedInstruction inst)
        {
            if (IsReserved(inst))
            {
                SetExceptionState(ExceptionType.Reserved);
                return;
            }

            int shiftAmount = 0;

            if (inst.IsVariableShift())
            {
                shiftAmount = (int)(ReadGPR32(inst.Source) & 0x3F);
            }
            else
            {
                shiftAmount = inst.ShiftAmount;

                if (inst.IsShift32())
                {
                    shiftAmount += 32;
                }
            }

            ulong value = ReadGPR64(inst.Target);

            if (inst.Op.ArithmeticType == ArithmeticOp.LSHIFT)
            {
                value <<= shiftAmount;
            }
            else
            {
                if (s_DsraFix && !inst.IsUnsigned()) {
                    value = (ulong)((long)value >> shiftAmount);
                }
                else {
                bool isSigned = (value & 0x4000000000000000) == 0x4000000000000000;

                value >>= shiftAmount;

                if (!inst.IsUnsigned() && isSigned)
                {
                    /* Sign extend */
                    value |= ~(~0UL >> shiftAmount);
                }
                }
            }

            Writeback64(inst.Destination, value);
        }

        public override void Subtract32(DecodedInstruction inst)
        {
            if (IsReserved(inst))
            {
                SetExceptionState(ExceptionType.Reserved);
                return;
            }

            bool isUnsigned = inst.IsUnsigned();
            uint operandA = ReadGPR32(inst.Source);
            uint operandB = ReadGPR32(inst.Target);
            uint result = 0;

            if (isUnsigned)
            {
                unchecked
                {
                    result = operandA - operandB;
                }
            }
            else
            {
                int r = 0;
                int _a = (int)operandA;
                int _b = (int)operandB;

                try
                {
                    checked
                    {
                        r = _a - _b;
                    }
                }
                catch (OverflowException)
                {
                    SetExceptionState(ExceptionType.Overflow);
                    return;
                }

                result = (uint)r;
            }

            Writeback32(inst.Destination, result);
        }

        public override void Subtract64(DecodedInstruction inst)
        {
            if (IsReserved(inst))
            {
                SetExceptionState(ExceptionType.Reserved);
                return;
            }

            bool isUnsigned = inst.IsUnsigned();
            ulong operandA = ReadGPR64(inst.Source);
            ulong operandB = ReadGPR64(inst.Target);
            ulong result = 0;

            if (isUnsigned)
            {
                unchecked
                {
                    result = operandA - operandB;
                }
            }
            else
            {
                long r = 0;
                long _a = (long)operandA;
                long _b = (long)operandB;

                try
                {
                    checked
                    {
                        r = _a - _b;
                    }
                }
                catch (OverflowException)
                {
                    SetExceptionState(ExceptionType.Overflow);
                    return;
                }

                result = (ulong)r;
            }

            Writeback64(inst.Destination, result);
        }

        public override void SetOnLessThan(DecodedInstruction inst)
        {
            if (IsReserved(inst))
            {
                SetExceptionState(ExceptionType.Reserved);
                return;
            }

            bool unsigned = inst.IsUnsigned();
            bool immediate = inst.IsImmediate();
            ulong mask = IsOperation64 ? 0xFFFFFFFFFFFFFFFF : 0x00000000FFFFFFFF;
            int dest = immediate ? inst.Target : inst.Destination;
            byte result = 0;

            ulong operandA = ReadGPR64(inst.Source) & mask;
            ulong operandB = 0;

            if (immediate)
            {
                operandB = (ulong)(long)(short)inst.Immediate;
            }
            else
            {
                operandB = ReadGPR64(inst.Target);
            }

            operandB &= mask;

            if (unsigned)
            {
                if (operandA < operandB)
                {
                    result = 1;
                }
            }
            else
            {
                if (!IsOperation64)
                {
                    operandA = (ulong)(int)operandA;
                    operandB = (ulong)(int)operandB;
                }

                if ((long)operandA < (long)operandB)
                {
                    result = 1;
                }
            }

            Writeback64(dest, result);
        }

        public override void TransferReg(DecodedInstruction inst)
        {
            ulong value = 0;
            int gpr_Source = inst.Target;
            int gpr_Target = inst.Destination;

            RegTransferGprHelper(inst, out gpr_Source, out gpr_Target);

            /* Source value to copy */
            switch (inst.Op.XferSource)
            {
                case RegBoundType.Hi: value = ReadHi(); break;
                case RegBoundType.Lo: value = ReadLo(); break;
                case RegBoundType.Gpr:
                    {
                        value = ReadGPR64(gpr_Source); break;
                    }
                case RegBoundType.Cp0:
                    {
                        if (!EnableCp0)
                        {
                            SetExceptionState(ExceptionType.Unusable);
                            return;
                        }

                        value = Cop0.CpuRegisterRead(inst.Destination);

                        /* If running 64-bit mode, with the 32-bit version, then sign extend */
                        if (IsOperation64 && inst.IsData32())
                        {
                            value = (ulong)(int)(uint)value;
                        }

                        break;
                    }
                case RegBoundType.Cp1:
                    {
                        if (!EnableCp1)
                        {
                            SetExceptionState(ExceptionType.Unusable);
                            return;
                        }

                        if (inst.IsData64()) {
                            value = ReadFPR_DW(inst.FloatSource);
                        }
                        else {
                            // 32-bit value must be sign extended
                            value = (ulong)(long)(int)ReadFPR_W(inst.FloatSource);
                        }

                        break;
                    }

                case RegBoundType.Cp1Ctl:
                    {
                        if (!EnableCp1)
                        {
                            SetExceptionState(ExceptionType.Unusable);
                            return;
                        }

                        // FPU co-processor revision info
                        if (inst.Destination == 0) {
                            value = 0xA00U;
                        }

                        if (inst.Destination == 31) {

                            value = State.FCR.Value;

                            /* If running 64-bit mode, then sign extend */
                            if (IsOperation64)
                            {
                                value = (ulong)(int)(uint)value;
                            }
                        }


                        break;
                    }
            }

            /* Target */
            switch (inst.Op.XferTarget)
            {
                case RegBoundType.Gpr:
                    {
                        Writeback64(gpr_Target, IsOperation64 ? value : (uint)value);
                        break;
                    }

                case RegBoundType.Hi:
                    {
                        WriteHi(IsOperation64 ? value : (uint)value);
                        break;
                    }

                case RegBoundType.Lo:
                    {
                        WriteLo(IsOperation64 ? value : (uint)value);
                        break;
                    }
                case RegBoundType.Cp0:
                    {
                        int index = inst.Destination;

                        if (index == CTS.CP0_REG_CONTEXT) {
                            value &= 0xFFFFFFFFFF800000UL;
                        }

                        if (index == CTS.CP0_REG_XCONTEXT) {
                            value &= 0xFFFFFFFE00000000UL;
                        }

                        if (index == CTS.CP0_REG_BADVADDR) {
                            break;
                        }

                        if (index == CTS.CP0_REG_LLADDR) {
                            value &= 0xFFFFFFFFU;
                        }

                        Cop0.CpuRegisterWrite(index, value);
                        break;
                    }
                case RegBoundType.Cp1:
                    {
                        if (!EnableCp1)
                        {
                            SetExceptionState(ExceptionType.Unusable);
                            return;
                        }

                        if (inst.IsData64()) {
                            WriteFPR_DW(inst.FloatSource, value);
                        }
                        else {
                            WriteFPR_W(inst.FloatSource, (uint)value);
                        }

                        break;
                    }
                case RegBoundType.Cp1Ctl:
                    {
                        if (!EnableCp1)
                        {
                            SetExceptionState(ExceptionType.Unusable);
                            return;
                        }

                        if (inst.Destination == 31)
                            State.FCR.Value = (uint)value;

                        break;
                    }
            }
        }

        // [cpu_diff] JAL/JALR/BxxAL write the return address sign-extended from 32 bits, as the 64-bit PC is (ares:
        // rd.u64 = PC + 4 on the sign-extended PC). The old code stored it zero-extended (0x00000000800xxxxx), which breaks
        // 64-bit compares and arithmetic on ra. Same in all three CPU paths. COR64_CPUDIFF_LINK=0 = old.
        internal static readonly bool s_LinkFix = Environment.GetEnvironmentVariable("COR64_CPUDIFF_LINK") != "0";
        internal static ulong LinkVal(long v) => s_LinkFix ? (ulong)(long)(int)v : (ulong)v;
        internal static long LinkConst(long v) => s_LinkFix ? (long)(int)v : v;

        public override void Branch(DecodedInstruction inst)
        {
            bool isLikely = inst.IsLikely();
            bool isLink = inst.IsLink();

            // The delay slot RA side effect
            if (BU_ExecuteBranchDelay ) {
                if (isLink) {
                    Writeback64(31, LinkVal(BU_TargetAddress + 4));
                }

                return;
            }

            ulong source = ReadGPR64(inst.Source);
            ulong target = ReadGPR64(inst.Target);

            BU_ConditionalPending = ComputeBranchCondition(IsOperation64, source, target, inst.Op.XferTarget, inst.Op.ArithmeticType);

            if (BU_ConditionalPending) {
                BU_TargetAddress = CoreUtils.ComputeBranchPC(IsOperation64, PC, CoreUtils.ComputeBranchTargetOffset(inst.Immediate));
            }

            if (isLink)
            {
                Writeback64(31, LinkVal(PC + 8));
            }

            #if !OPTIMAL
            Debugger.CheckBranchBreakpoints((uint)BU_TargetAddress, BU_ConditionalPending);
            #endif

            // Nullify the delay slot if likely branch not taken
            BU_NullifyNext = isLikely && !BU_ConditionalPending;

            // Always indicate the delay slot if next
            BU_DelaySlotNext = true;
        }

        public override void Jump(DecodedInstruction inst)
        {
            bool isLink = inst.IsLink();
            bool isRegister = inst.IsRegister();

            if (BU_ExecuteBranchDelay) {
                // Jump doesn't execute but there is a side-effect for the RA

                if (isLink) {
                    if (isRegister && inst.Destination != 0) {
                        Writeback64(inst.Destination, LinkVal(BU_TargetAddress + 4));
                    }
                    else {
                        Writeback64(31,  LinkVal(BU_TargetAddress + 4));
                    }
                }

                return;
            }


            BU_TargetAddress = CoreUtils.ComputeTargetPC(isRegister, PC, (long)ReadGPR64(inst.Source), inst.Inst.target);
            BU_UnconditionalPending = true;
            BU_DelaySlotNext = true;

            if (isLink)
            {
                // TODO: The recompiler will need this fix
                if (isRegister && inst.Destination != 0) {
                    Writeback64(inst.Destination,  LinkVal(PC + 8));
                }
                else {
                    Writeback64(31,  LinkVal(PC + 8));
                }
            }

            #if !OPTIMAL
            Debugger.CheckBranchBreakpoints((uint)BU_TargetAddress, true);

            if (!isRegister && (uint)BU_TargetAddress == (uint)PC && !m_WarnInfiniteJump)
            {
                Log.Warn("An unconditional infinite jump was hit: " + m_LastFetchAddress.ToString("X8"));
                m_WarnInfiniteJump = true;
            }

            #endif
        }

        // [cer64plus] Debug aid: COR64_WATCH=<hex physical addr> logs every CPU store to that 8-byte block (PC, size, value).
        private static readonly long s_WatchAddr = long.TryParse(Environment.GetEnvironmentVariable("COR64_WATCH"), System.Globalization.NumberStyles.HexNumber, null, out var wa) ? (wa & ~7L) : -1;
        private static int s_WatchCount;
        private static readonly int s_WatchMax = int.TryParse(Environment.GetEnvironmentVariable("COR64_WATCH_MAX"), out var wx) ? wx : 400;
        private static readonly int s_WatchMin = int.TryParse(Environment.GetEnvironmentVariable("COR64_WATCH_MIN"), out var wm) ? wm : 0; // only log |s16 value| >= this
        private void WatchHit(long address, int size, DecodedInstruction inst) {
            if (s_WatchMin > 0 && Math.Abs((int)(short)ReadGPR64(inst.Target)) < s_WatchMin) return;
            if (++s_WatchCount > s_WatchMax) return; System.IO.Directory.CreateDirectory("telemetry");
            System.IO.File.AppendAllText("telemetry/watch.txt", string.Format("pc={0:X8} addr={1:X6} size={2} value={3:X} ra={4:X8} t2={5:X8}\n",
                (uint)PC, address, size, ReadGPR64(inst.Target), (uint)ReadGPR64(31), (uint)ReadGPR64(10)));
        }

        // [cpu_diff] Address error exceptions (AdEL/AdES): a halfword, word or doubleword access (not LWL/LWR/SWL/SWR/
        // LDL/LDR/SDL/SDR) to an address that is not a multiple of its size raises ExcCode 4 (load) or 5 (store) with
        // BadVAddr = the address (VR4300 manual 6.4.x; ares CPU::Exception::addressLoad/addressStore). The inline RDRAM paths
        // of the IL and x64 blocks already send unaligned accesses to these handlers, so all three paths agree.
        // Instruction-fetch address errors are not modelled. COR64_CPUDIFF_ADE=0 = old (access done unaligned).
        internal static readonly bool s_Ade = Environment.GetEnvironmentVariable("COR64_CPUDIFF_ADE") != "0";
        private bool AddrErr(long address, int size, bool store)
        {
            if (!s_Ade || size <= 1 || (address & (size - 1)) == 0) return false;
            Cop0.RaiseAddressError(store, (ulong)(long)(int)address);
            return true;
        }

        public override void Store(DecodedInstruction inst)
        {
            long address = 0;
            int size = inst.DataSize();
            ExecutionFlags flags = inst.Op.Flags;
            bool LLMode = (flags & ExecutionFlags.Link) == ExecutionFlags.Link;

            if (!IsOperation64 && size == 8)
            {
                SetExceptionState(ExceptionType.Reserved);
                return;
            }

            /* Conditional store notes:
             * The LLBit must be set a Load linked operation before this type of operation
             * TODO: When ERET instruction is supported, it causes any conditional store to fail afterwards
             */

            // [cer64plus] SC/SCD store only while LLBit is set; on failure nothing is stored and rt = 0. (Was inverted.)
            if (LLMode && !State.LLBit) { Writeback64(inst.Target, 0); return; }

            if (!IsOperation64)
            {
                int baseAddress = (int)ReadGPR32(inst.Source);
                int offset = (short)inst.Immediate;
                address = baseAddress + offset;
            }
            else
            {
                long baseAddress = (long)ReadGPR64(inst.Source);
                long offset = (short)inst.Immediate;
                address = baseAddress + offset;
            }

            /* Always 32-bit address */
            address &= 0xFFFFFFFFU;

            if (!inst.IsLeft() && !inst.IsRight() && AddrErr(address, size, true)) return;
            /* Virtual To Physical */
            if (!TryVirtToPhys(true, address, out address))
            {
                // Exception raised during fetch stage
                // Return right away, let the exception handler work on the next step
                return;
            }

            try
            {
                if (s_WatchAddr >= 0 && (inst.IsLeft() || inst.IsRight()) && (address & ~7L) == s_WatchAddr) WatchHit(address, -size, inst);
                if (inst.IsLeft())
                {
                    switch (size)
                    {
                        default: break;

                        case 4:
                            {
                                int index = (int)((uint)address & 3);
                                uint val = PhysicalMemory.U32(address & ~3);
                                val &= CTS.SWL_MASK[index];
                                val |= ReadGPR32(inst.Target) >> CTS.SWL_SHIFT[index];
                                PhysicalMemory.U32(address & ~3, val);
                                break;
                            }

                        case 8:
                            {
                                int index = (int)((uint)address & 7);
                                ulong val = PhysicalMemory.U64(address & ~7);
                                val &= CTS.SDL_MASK[index];
                                val |= ReadGPR64(inst.Target) >> CTS.SDL_SHIFT[index];
                                PhysicalMemory.U64(address & ~7, val);
                                break;
                            }
                    }
                }
                else if (inst.IsRight())
                {
                    switch (size)
                    {
                        default: break;

                        case 4:
                            {
                                int index = (int)((uint)address & 3);
                                uint val = PhysicalMemory.U32(address & ~3);
                                val &= CTS.SWR_MASK[index];
                                val |= ReadGPR32(inst.Target) << CTS.SWR_SHIFT[index];
                                PhysicalMemory.U32(address & ~3, val);
                                break;
                            }

                        case 8:
                            {
                                int index = (int)((uint)address & 7);
                                ulong val = PhysicalMemory.U64(address & ~7);
                                val &= CTS.SDR_MASK[index];
                                val |= ReadGPR64(inst.Target) << CTS.SDR_SHIFT[index];
                                PhysicalMemory.U64(address & ~7, val);
                                break;
                            }
                    }
                }
                else
                {
                    switch (size)
                    {
                        default: throw new InvalidOperationException("How did this happen?");
                        case 1: PhysicalMemory.U8(address, (byte)ReadGPR64(inst.Target)); break;
                        case 2: PhysicalMemory.U16(address, (ushort)ReadGPR64(inst.Target)); break;
                        case 4: PhysicalMemory.U32(address, (uint)ReadGPR32(inst.Target)); break;
                        case 8: PhysicalMemory.U64(address, ReadGPR64(inst.Target)); break;
                    }

                    if (s_WatchAddr >= 0 && (address & ~7L) == s_WatchAddr) WatchHit(address, size, inst);

                    /* Store conditional */
                    if (LLMode)
                    {
                        Writeback64(inst.Target, State.LLBit ? 1U : 0U);
                    }

                    // n64_systemtest isviewer
                    #if ENABLE_ISVIEWER
                    if (size == 4 && address == MemHelper.VirtualToPhysical(0xB3FF0014)) {
                        int len = PhysicalMemory.S32(address);

                        if (len > 0) {
                            long addr = MemHelper.VirtualToPhysical(0xB3FF0020);

                            for (int i = 0; i < len; i++) {
                                char readChar = (char)PhysicalMemory.U8(addr + i);
                                if (readChar == '\0') break;
                                Console.Write(readChar);
                            }
                        }
                    }
                    #endif
                }
            }
            catch (MipsException e)
            {
                SetExceptionState(e.Exception);
            }
        }

        public override void Load(DecodedInstruction inst)
        {
            /* Modes: Upper Immediate, Unsigned / Signed (8, 16, 32, 64), Left / Right (32, 64), Load Linked */
            long address = 0;
            int size = 0;

            bool upperImm = inst.IsImmediate();
            bool loadLinked = inst.IsLink();
            bool left = inst.IsLeft();
            bool right = inst.IsRight();
            bool unsigned = inst.IsUnsigned();

            uint read32 = 0;
            ulong read64 = 0;

            try
            {
                if (upperImm)
                {
                    uint imm = inst.Immediate;
                    imm <<= 16;

                    Writeback32(inst.Target, imm);

                    return;
                }
                else
                {
                    size = inst.DataSize();

                    if (!IsOperation64 && size == 8)
                    {
                        SetExceptionState(ExceptionType.Reserved);
                        return;
                    }

                    if (!IsOperation64)
                    {
                        int baseAddress = (int)ReadGPR32(inst.Source);
                        int offset = (short)inst.Immediate;
                        address = baseAddress + offset;
                    }
                    else
                    {
                        long baseAddress = (long)ReadGPR64(inst.Source);
                        long offset = (short)inst.Immediate;
                        address = baseAddress + offset;
                    }

                    /* Always 32-bit address */
                    address &= 0xFFFFFFFFU;

                    if (!left && !right && AddrErr(address, size, false)) return;
                    /* Virtual To Physical */
                    if (!TryVirtToPhys(false, address, out address))
                    {
                        // Exception raised during fetch stage
                        // Return right away, let the exception handler work on the next step
                        return;
                    }

                    // REF: https://www2.cs.duke.edu/courses/cps104/fall02/homework/lwswlr.html
                    // REF: Project64 source

                    if (left)
                    {
                        switch (size)
                        {
                            default: break;

                            case 4:
                                {
                                    int index = (int)((uint)address & 3);
                                    read32 = PhysicalMemory.U32(address & ~3);
                                    uint val = ReadGPR32(inst.Target);
                                    val &= CTS.LWL_MASK[index];
                                    val |= read32 << CTS.LWL_SHIFT[index];
                                    read32 = val;
                                    break;
                                }

                            case 8:
                                {
                                    int index = (int)((uint)address & 7);
                                    read64 = PhysicalMemory.U64(address & ~7);
                                    ulong val = ReadGPR64(inst.Target);
                                    val &= CTS.LDL_MASK[index];
                                    val |= read64 << CTS.LDL_SHIFT[index];
                                    read64 = val;
                                    break;
                                }
                        }
                    }
                    else if (right)
                    {
                        switch (size)
                        {
                            default: break;

                            case 4:
                                {
                                    int index = (int)((uint)address & 3);
                                    read32 = PhysicalMemory.U32(address & ~3);
                                    uint val = ReadGPR32(inst.Target);
                                    val &= CTS.LWR_MASK[index];
                                    val |= read32 >> CTS.LWR_SHIFT[index];
                                    read32 = val;
                                    break;
                                }

                            case 8:
                                {
                                    int index = (int)((uint)address & 7);
                                    read64 = PhysicalMemory.U64(address & ~7);
                                    ulong val = ReadGPR64(inst.Target);
                                    val &= CTS.LDR_MASK[index];
                                    val |= read64 >> CTS.LDR_SHIFT[index];
                                    read64 = val;
                                    break;
                                }
                        }
                    }
                    else
                    {
                        if (loadLinked)
                        {
                            State.LLBit = true;
                            Cop0.CpuRegisterWrite(17, (ulong)((address & 0xFFFFFFFF) >> 4)); // [cer64plus] LLAddr = physical address >> 4
                        }

                        if (size == 4) {
                            read32 = PhysicalMemory.U32(address);
                        }
                        else if (size == 8) {
                            read64 = PhysicalMemory.U64(address);
                        }
                    }

                    #if TESTING
                    StringBuilder sb = new StringBuilder();
                    sb.Append("LOAD ...");
                    for (int i = 0; i < 8; i++) {
                        sb.Append(PhysicalMemory.DirectReadByte(address + i).ToString("X2"));
                    }
                    sb.Append("...");
                    Console.WriteLine(sb.ToString());
                    #endif

                    if (unsigned)
                    {
                        switch (size)
                        {
                            default: throw new InvalidOperationException("How did this happen (unsigned)?");
                            case 1: Writeback64(inst.Target, PhysicalMemory.U8(address)); break;
                            case 2: Writeback64(inst.Target, PhysicalMemory.U16(address)); break;
                            case 4: Writeback64(inst.Target, read32); break;
                            case 8: Writeback64(inst.Target, read64); break;
                        }
                    }
                    else
                    {
                        switch (size)
                        {
                            default: throw new InvalidOperationException("How did this happen?");
                            case 1: Writeback64(inst.Target, (ulong)(sbyte)PhysicalMemory.U8(address)); break;
                            case 2: Writeback64(inst.Target, (ulong)(short)PhysicalMemory.U16(address)); break;
                            case 4: Writeback64(inst.Target, (ulong)(int)read32); break;
                            case 8: Writeback64(inst.Target, (ulong)(long)read64); break;
                        }
                    }
                }
            }
            catch (MipsException e)
            {
                SetExceptionState(e.Exception);
            }
        }

        public static readonly bool s_DbgCache = Environment.GetEnvironmentVariable("COR64_DBG_CACHEOPS") == "1"; // debug aid
        static int s_DbgCacheN;
        public override void Cache(DecodedInstruction inst)
        {
            if (s_DbgCache && s_DbgCacheN++ < 20000) Console.WriteLine($"CACHEOP pc={(uint)PC:X8} op={(inst.Inst.inst >> 16) & 31:X2} va={(uint)((long)ReadGPR64(inst.Source) + (short)inst.Immediate):X8}");
        }

        public override void Sync(DecodedInstruction inst)
        {
        }

        public override void FloatLoad(DecodedInstruction inst)
        {
            try
            {
                var size = inst.DataSize();

                long baseAddress = (long)ReadGPR64(inst.Source);
                long offset = (short)inst.Immediate;
                var address = baseAddress + offset;

                /* Always 32-bit address */
                address &= 0xFFFFFFFFU;

                if (AddrErr(address, size, false)) return;
                /* Virtual To Physical */
                if (!TryVirtToPhys(false, address, out address))
                {
                    // Exception raised during fetch stage
                    // Return right away, let the exception handler work on the next step
                    return;
                }

                switch (size)
                {
                    default: throw new InvalidOperationException("Unsupported FPU Load Size: " + size.ToString());
                    case 4: WriteFPR_W(inst.FloatTarget, PhysicalMemory.U32(address)); break;
                    case 8: WriteFPR_DW(inst.FloatTarget, PhysicalMemory.U64(address)); break;
                }

                /* If loading a doubleword and FR = 0, we don't care, we bypass 32-bit stuff */
            }
            catch (MipsException e)
            {
                SetExceptionState(e.Exception);
            }

            /* TODO: Simulate odd result registers for 64-bit reads ? */
        }

        public override void FloatStore(DecodedInstruction inst)
        {
            var size = inst.DataSize();

            long baseAddress = (long)ReadGPR64(inst.Source);
            long offset = (short)inst.Immediate;
            long address = baseAddress + offset;

            /* Always 32-bit address */
            address &= 0xFFFFFFFFU;

            if (AddrErr(address, size, true)) return;
            /* Virtual To Physical */
            if (!TryVirtToPhys(true, address, out address))
            {
                // Exception raised during fetch stage
                // Return right away, let the exception handler work on the next step
                return;
            }

            try
            {
                switch (size)
                {
                    default: throw new InvalidOperationException("Unsupported FPU Store Size: " + size.ToString());
                    case 4: PhysicalMemory.U32(address, ReadFPR_W(inst.FloatTarget)); break;
                    case 8: PhysicalMemory.U64(address, ReadFPR_DW(inst.FloatTarget)); break;
                }
                if (s_WatchAddr >= 0 && (address & ~7L) == s_WatchAddr && ++s_WatchCount <= 400)
                    System.IO.File.AppendAllText("telemetry/watch.txt", string.Format("FPU pc={0:X8} addr={1:X6} size={2} bits={3:X8} f={4} ra={5:X8} f4={6} f6={7} f8={8} f20={9} sp={10:X8}\n",
                        (uint)PC, address, size, ReadFPR_W(inst.FloatTarget), BitConverter.Int32BitsToSingle((int)ReadFPR_W(inst.FloatTarget)), (uint)ReadGPR64(31),
                        ReadFPR_S(4), ReadFPR_S(6), ReadFPR_S(8), ReadFPR_S(20), (uint)ReadGPR64(29)));
                if (s_WatchAddr >= 0 && (address & ~7L) == s_WatchAddr && Math.Abs(ReadFPR_S(6)) > 1000) {
                    long sp = (long)ReadGPR64(29) & 0x1FFFFFFF;
                    long obj = PhysicalMemory.U32(sp + 0x58) & 0x1FFFFFFF;
                    var sb = new System.Text.StringBuilder("  obj=" + obj.ToString("X6") + " mtx(+E8):");
                    for (int k = 0; k < 16; k++) sb.Append(' ').Append(BitConverter.Int32BitsToSingle((int)PhysicalMemory.U32(obj + 0xE8 + 4 * k)));
                    System.IO.File.AppendAllText("telemetry/watch.txt", sb + "\n");
                }
            }
            catch (MipsException e)
            {
                SetExceptionState(e.Exception);
            }
        }

        public override void Add(DecodedInstruction inst)
        {
            if (inst.Format == FpuValueType.FSingle)
            {
                WriteFPR_S(inst.FloatDest, ReadFPR_S(inst.FloatSource) + ReadFPR_S(inst.FloatTarget));
            }
            else if (inst.Format == FpuValueType.FDouble)
            {
                WriteFPR_D(inst.FloatDest, ReadFPR_D(inst.FloatSource) + ReadFPR_D(inst.FloatTarget));
            }
            else
            {
                SetExceptionState(FpuExceptionFlags.Unimplemented);
            }
        }

        public override void Subtract(DecodedInstruction inst)
        {
            if (inst.Format == FpuValueType.FSingle)
            {
                WriteFPR_S(inst.FloatDest, ReadFPR_S(inst.FloatSource) - ReadFPR_S(inst.FloatTarget));
            }
            else if (inst.Format == FpuValueType.FDouble)
            {
                WriteFPR_D(inst.FloatDest, ReadFPR_D(inst.FloatSource) - ReadFPR_D(inst.FloatTarget));
            }
            else
            {
                SetExceptionState(FpuExceptionFlags.Unimplemented);
            }
        }

        public override void Multiply(DecodedInstruction inst)
        {
            if (inst.Format == FpuValueType.FSingle)
            {
                WriteFPR_S(inst.FloatDest, ReadFPR_S(inst.FloatSource) * ReadFPR_S(inst.FloatTarget));
            }
            else if (inst.Format == FpuValueType.FDouble)
            {
                WriteFPR_D(inst.FloatDest, ReadFPR_D(inst.FloatSource) * ReadFPR_D(inst.FloatTarget));
            }
            else
            {
                SetExceptionState(FpuExceptionFlags.Unimplemented);
            }
        }

        public override void Divide(DecodedInstruction inst)
        {
            if (inst.Format == FpuValueType.FSingle)
            {
                WriteFPR_S(inst.FloatDest, ReadFPR_S(inst.FloatSource) / ReadFPR_S(inst.FloatTarget));
            }
            else if (inst.Format == FpuValueType.FDouble)
            {
                WriteFPR_D(inst.FloatDest, ReadFPR_D(inst.FloatSource) / ReadFPR_D(inst.FloatTarget));
            }
            else
            {
                SetExceptionState(FpuExceptionFlags.Unimplemented);
            }
        }

        public override void SqrRoot(DecodedInstruction inst)
        {
            if (inst.Format == FpuValueType.FSingle)
            {
                WriteFPR_S(inst.FloatDest, (float)Math.Sqrt(ReadFPR_S(inst.FloatSource)));
            }
            else if (inst.Format == FpuValueType.FDouble)
            {
                WriteFPR_D(inst.FloatDest, Math.Sqrt(ReadFPR_D(inst.FloatSource)));
            }
            else
            {
                SetExceptionState(FpuExceptionFlags.Unimplemented);
            }
        }

        public override void Abs(DecodedInstruction inst)
        {
            if (inst.Format == FpuValueType.FSingle)
            {
                WriteFPR_S(inst.FloatDest, Math.Abs(ReadFPR_S(inst.FloatSource)));
            }
            else if (inst.Format == FpuValueType.FDouble)
            {
                WriteFPR_D(inst.FloatDest, Math.Abs(ReadFPR_D(inst.FloatSource)));
            }
            else
            {
                SetExceptionState(FpuExceptionFlags.Unimplemented);
            }
        }

        internal static readonly bool s_MovSFix = Environment.GetEnvironmentVariable("COR64_CPUDIFF_MOVS") != "0";

        public override void Mov(DecodedInstruction inst)
        {
            // [cpu_diff] MOV.S with FR=0 copies only the 32-bit register: the old double copy also overwrote the other
            // half of fd's even/odd pair and read the wrong half for an odd fs. COR64_CPUDIFF_MOVS=0 = old.
            if (s_MovSFix && inst.Format == FpuValueType.FSingle && !StateR4000I.FPR.FullMode)
            {
                WriteFPR_W(inst.FloatDest, ReadFPR_W(inst.FloatSource));
            }
            else if (inst.Format == FpuValueType.FDouble || inst.Format == FpuValueType.FSingle)
            {
                WriteFPR_D(inst.FloatDest, ReadFPR_D(inst.FloatSource));
            }
            else
            {
                SetExceptionState(FpuExceptionFlags.Unimplemented);
            }
        }

        public override void Neg(DecodedInstruction inst)
        {
            if (inst.Format == FpuValueType.FSingle)
            {
                WriteFPR_S(inst.FloatDest, -ReadFPR_S(inst.FloatSource));
            }
            else if (inst.Format == FpuValueType.FDouble)
            {
                WriteFPR_D(inst.FloatDest, -ReadFPR_D(inst.FloatSource));
            }
            else
            {
                SetExceptionState(FpuExceptionFlags.Unimplemented);
            }
        }

        // [cer64plus] FPU float->int conversions. .NET 9+ saturates (uint)negativeDouble to 0, so always go through signed casts.
        private static double RoundBy(double v, RoundingMode m) => m switch {
            RoundingMode.TowardZero => Math.Truncate(v),
            RoundingMode.TowardPostive => Math.Ceiling(v),
            RoundingMode.TowardNegative => Math.Floor(v),
            _ => Math.Round(v, MidpointRounding.ToEven)
        };
        private double ReadFloatSrc(DecodedInstruction inst, out bool ok) {
            ok = true;
            if (inst.Format == FpuValueType.FSingle) return ReadFPR_S(inst.FloatSource);
            if (inst.Format == FpuValueType.FDouble) return ReadFPR_D(inst.FloatSource);
            ok = false; return 0;
        }
        private void WriteFixed(DecodedInstruction inst, double rounded, bool is32) {
            if (is32) {
                int w = (rounded >= 2147483648.0 || rounded < -2147483648.0 || double.IsNaN(rounded)) ? int.MaxValue : (int)rounded;
                WriteFPR_W(inst.FloatDest, unchecked((uint)w));
            }
            else {
                long l = (rounded >= 9223372036854775808.0 || rounded < -9223372036854775808.0 || double.IsNaN(rounded)) ? long.MaxValue : (long)rounded;
                WriteFPR_DW(inst.FloatDest, unchecked((ulong)l));
            }
        }
        private void FixedOp(DecodedInstruction inst, RoundingMode mode) {
            double v = ReadFloatSrc(inst, out bool ok);
            if (!ok) { SetExceptionState(FpuExceptionFlags.Unimplemented); return; }
            WriteFixed(inst, RoundBy(v, mode), inst.Op.Flags == ExecutionFlags.Data32);
        }

        public override void Round(DecodedInstruction inst) => FixedOp(inst, RoundingMode.Nearest);

        public override void Truncate(DecodedInstruction inst) => FixedOp(inst, RoundingMode.TowardZero);

        public override void Ceil(DecodedInstruction inst) => FixedOp(inst, RoundingMode.TowardPostive);

        public override void Floor(DecodedInstruction inst) => FixedOp(inst, RoundingMode.TowardNegative);

        public override void Convert(DecodedInstruction inst)
        {
            // Console.WriteLine("Convert To {6}: S:{0} T:{1} D:{2} FS:{3} FT:{4} FD:{5}", inst.Source, inst.Target, inst.Destination, inst.FloatSource, inst.FloatTarget, inst.FloatDest, inst.Format.ToString());

            if (inst.Op.Flags == ExecutionFlags.DataS) {
                ConvertToSingle(inst.Format, inst.FloatSource, inst.FloatDest);
            }
            else if (inst.Op.Flags == ExecutionFlags.DataD) {
                ConvertToDouble(inst.Format, inst.FloatSource, inst.FloatDest);
            }
            else if (inst.Op.Flags == ExecutionFlags.Data32) {
                ConvertToFixed32(inst.Format, inst.FloatSource, inst.FloatDest);
            }
            else if (inst.Op.Flags == ExecutionFlags.Data64) {
                ConvertToFixed64(inst.Format, inst.FloatSource, inst.FloatDest);
            }
            else {
                SetExceptionState(FpuExceptionFlags.Unimplemented);
            }
        }

        /* Exceptions: Invalid and Unimplemented */
        public override void Condition(DecodedInstruction inst)
        {
            bool flag_signaling = inst.Op.ArithmeticType == ArithmeticOp.SIGNALING;
            bool flag_equal = inst.Op.Flags.TestFlag(ExecutionFlags.CondEq);
            bool flag_less_than = inst.Op.Flags.TestFlag(ExecutionFlags.CondLT);
            bool flag_greater_than = inst.Op.Flags.TestFlag(ExecutionFlags.CondGT);
            bool flag_not = inst.Op.Flags.TestFlag(ExecutionFlags.CondNot);
            bool flag_unordered = inst.Op.Flags.TestFlag(ExecutionFlags.CondUn);
            bool flag_forced_ordered = inst.Op.Flags.TestFlag(ExecutionFlags.CondOrd);

            bool result = false;

            double a = 0.0d;
            double b = 0.0d;
            bool hasNaN = false;

            if (inst.Format == FpuValueType.FSingle || inst.Format == FpuValueType.FDouble)
            {
                if (inst.Format == FpuValueType.FSingle)
                {
                    a = ReadFPR_S(inst.FloatSource);
                    b = ReadFPR_S(inst.FloatTarget);
                }
                else
                {
                    a = ReadFPR_D(inst.FloatSource);
                    b = ReadFPR_D(inst.FloatTarget);
                }

                hasNaN = double.IsNaN(a) || double.IsNaN(b);

                if (flag_forced_ordered && hasNaN)
                {
                    SetExceptionState(FpuExceptionFlags.Invalid);
                }
                else
                {
                    if (flag_unordered)
                    {
                        result |= hasNaN;

                        if (flag_signaling)
                        {
                            /* Compare that both are signaling NaNs */
                            // TODO: This needs real testing, and on some website I found that this always results false.
                            throw new Exception();
                            //result |= (State.FPR.SignalNaNTable[inst.FloatSource] == State.FPR.SignalNaNTable[inst.FloatTarget]);
                        }
                    }

                    if (flag_equal)
                    {
                        result |= (a == b);
                    }

                    if (flag_less_than)
                    {
                        result |= (a < b);
                    }

                    if (flag_greater_than)
                    {
                        result |= (a > b);
                    }
                }

                if (flag_not)
                {
                    result = !result;
                }
             
                State.FCR.Condition = result;
            }
            else
            {
                SetExceptionState(FpuExceptionFlags.Unimplemented);
            }
        }

        public override void Trap(DecodedInstruction inst) {
            bool isImmediate = inst.IsImmediate();
            bool isUnsigned = inst.IsUnsigned();
            var result = false;

            void Compare64() {
                if (isUnsigned) {
                    var source = ReadGPR64(inst.Source);
                    var target = isImmediate ? (ulong)(short)inst.Immediate : ReadGPR64(inst.Target);

                    result = inst.Op.ArithmeticType switch
                    {
                        ArithmeticOp.EQUAL => source == target,
                        ArithmeticOp.LESS_THAN => source < target,
                        ArithmeticOp.GREATER_THAN => source > target,
                        ArithmeticOp.NOT_EQUAL => source != target,
                        _ => throw new EmuException("Invalid trap condition type"),
                    };
                }
                else {
                    var source = (long)ReadGPR64(inst.Source);
                    var target = isImmediate ? (long)(short)inst.Immediate : (long)ReadGPR64(inst.Target);

                    result = inst.Op.ArithmeticType switch
                    {
                        ArithmeticOp.EQUAL => source == target,
                        ArithmeticOp.LESS_THAN => source < target,
                        ArithmeticOp.GREATER_THAN => source > target,
                        ArithmeticOp.NOT_EQUAL => source != target,
                        _ => throw new EmuException("Invalid trap condition type"),
                    };
                }
            }

            void Compare32() {
                if (isUnsigned) {
                    var source = ReadGPR32(inst.Source);
                    var target = isImmediate ? (uint)(short)inst.Immediate : ReadGPR32(inst.Target);

                    result = inst.Op.ArithmeticType switch
                    {
                        ArithmeticOp.EQUAL => source == target,
                        ArithmeticOp.LESS_THAN => source < target,
                        ArithmeticOp.GREATER_THAN => source > target,
                        ArithmeticOp.NOT_EQUAL => source != target,
                        _ => throw new EmuException("Invalid trap condition type"),
                    };
                }
                else {
                    var source = (int)ReadGPR32(inst.Source);
                    var target = isImmediate ? (int)(short)inst.Immediate : (int)ReadGPR32(inst.Target);

                    result = inst.Op.ArithmeticType switch
                    {
                        ArithmeticOp.EQUAL => source == target,
                        ArithmeticOp.LESS_THAN => source < target,
                        ArithmeticOp.GREATER_THAN => source > target,
                        ArithmeticOp.NOT_EQUAL => source != target,
                        _ => throw new EmuException("Invalid trap condition type"),
                    };
                }
            }

            if (IsOperation64) {
                Compare64();
            }
            else {
                Compare32();
            }

            if (result) {
                Cop0.TrapException();
            }
        }

        public override void Break(DecodedInstruction inst) {
            SetBreakException();
        }

        public override void TLBProbe(DecodedInstruction inst) {
            TLB.Probe();
        }

        public override void TLBWrite(DecodedInstruction inst) {
            TLB.Write(inst.Op.Flags.TestFlag(ExecutionFlags.Random));
        }

        public override void TLBRead(DecodedInstruction inst) {
            TLB.Read();
        }

        public override void Syscall(DecodedInstruction inst)
        {
            SetSyscallException();
        }

        public bool InfiniteLoopWarn => m_WarnInfiniteJump;
    }
}
