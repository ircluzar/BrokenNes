using System.Runtime.ConstrainedExecution;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NLog;

namespace cor64.Mips.R4300I
{
    /// <summary>
    /// This class simulates functions of the coprocessor 0 in high level emulation
    /// </summary>
    public class SystemController
    {
        private readonly static Logger Log = LogManager.GetCurrentClassLogger();
        private readonly ExecutionState m_State;
        private bool m_TLBRefillException;
        private bool m_XTLBRefillExcepetion;
        private bool m_CacheErr;
        private bool m_NMIReset;
        private MipsInterface m_Interface;
        private bool m_SoftReset;
        private bool m_Direct;
        private bool m_Syscall = false;
        private bool m_Break = false;
        private bool m_Trap = false;

        const int INTERRUPT_SW0 = 0;  // An interrupt set by user
        const int INTERRUPT_SW1 = 1;  // An interrupt set by user
        const int INTERRUPT_RCP = 2;  // RCP Hardware Events
        const int INTERRUPT_CART = 3; // Cartidge Events
        const int INTERRUPT_RESET = 4; // Reset Button
        const int INTERRUPT_RDB_READ = 5; // Debugger Read
        const int INTERRUPT_RDB_WRITE = 6; // Debugger Write
        const int INTERRUPT_TIMER = 7; // MIPS Timer


        protected CauseRegister CR { get; }
        protected StatusRegister SR { get; }
        public ControlRegisters REGS { get; }

        private bool m_EnableTimer = false;

        public long ExceptionHandlerAddress { get; private set; }

        public SystemController(ControlRegisters cop0State, ExecutionState state)
        {
            m_State = state;
            REGS = cop0State;
            CR = cop0State.Cause;
            SR = cop0State.Status;
        }

        public void AttachInterface(MipsInterface mipsInterface)
        {
            m_Interface = mipsInterface;
        }

        public void SetTLBRefillException(bool isStore)
        {
            CR.SetException(isStore ? ExceptionType.TLBStore : ExceptionType.TLBLoad);
            // TODO: Set CE
            m_TLBRefillException = true;
        }

        public void SetXTLBRefillException(bool isStore)
        {
            CR.SetException(isStore ? ExceptionType.TLBStore : ExceptionType.TLBLoad);
            // TODO: Set CE
            m_TLBRefillException = true;
        }

        // [tlb_exc] A TLB exception raised by a load, store or fetch translation: BadVAddr, Context, XContext and EntryHi
        // are set here; CheckExceptions then enters the handler at the vector chosen here. va is sign-extended.
        //   refill (no matching entry): 0x000 (32-bit kernel addressing) / 0x080 (KX=1); invalid / modified: 0x180.
        //   With EXL already set, every TLB exception goes to 0x180 and EPC and Cause.BD are left unchanged.
        //   With BEV set, the base is 0xBFC00200 instead of 0x80000000.
        [NonSerialized] private bool m_TlbPending;
        [NonSerialized] private bool m_TlbKeepEpc;
        [NonSerialized] private long m_TlbVector;

        public bool TlbExceptionPending => m_TlbPending;

        public void RaiseTlbException(ExceptionType type, bool refill, ulong va)
        {
            var tlb = REGS.TLBRegs;
            tlb.BadVAddr = va;

            // Context: PTEBase (63:23) kept, BadVPN2 (22:4) = va[31:13]
            ulong ctx = REGS.Read(CTS.CP0_REG_CONTEXT);
            REGS.Write(CTS.CP0_REG_CONTEXT, (ctx & ~0x7FFFF0UL) | (((va >> 13) & 0x7FFFF) << 4));

            // XContext: PTEBase (63:33) kept, R (32:31) = va[63:62], BadVPN2 (30:4) = va[39:13]
            ulong xctx = REGS.Read(CTS.CP0_REG_XCONTEXT);
            REGS.Write(CTS.CP0_REG_XCONTEXT, (xctx & 0xFFFFFFFE00000000UL) | ((va >> 62) << 31) | (((va >> 13) & 0x7FFFFFF) << 4));

            // EntryHi: R and VPN2 from the address, ASID kept
            tlb.RegEntryHi.Write((va & 0xC00000FFFFFFE000UL) | tlb.RegEntryHi.ASID);

            RaiseMipsException(type);

            bool exl = SR.ExceptionLevel;
            uint offset = (!exl && refill) ? (SR.TestFlags(StatusRegister.StatusFlags.Kernel64Mode) ? 0x080u : 0x000u) : 0x180u;
            long target;
            if (m_Direct) target = offset;
            else if (SR.TestFlags(StatusRegister.StatusFlags.UseBootstrapVectors)) target = unchecked((int)0xBFC00200) + offset;
            else target = unchecked((int)0x80000000) + offset;
            m_TlbVector = (uint)target;
            m_TlbKeepEpc = exl;
            m_TlbPending = true;
            if (CompatLog.On) {
                if (!s_TlbKeyHandled) CompatLog.Note("cpu:tlb-exception", $"type={type} refill={refill} va={va:X16} exl={exl}");
                else m_TlbWatchDetail = $"type={type} refill={refill} va={va:X16} exl={exl}";
            }
        }

        // [session_leak] COR64_TLBKEY_HANDLED (default on; =0 logs every TLB exception as before): a TLB exception the
        // game handles (its handler ERETs back to the faulting EPC, e.g. a paging refill) is normal behaviour and is not
        // logged. An exception is recorded as cpu:tlb-exception only when no ERET returns to its EPC within 4096 ERETs
        // (libultra's fault path parks the faulting thread for good), or when more than 32 are pending. Logging only.
        public static readonly bool s_TlbKeyHandled = Environment.GetEnvironmentVariable("COR64_TLBKEY_HANDLED") != "0";
        [NonSerialized] private string m_TlbWatchDetail;

        // pending exceptions: faulting EPC, detail, ERETs seen since. A handler may switch threads first (GoldenEye pages
        // in on another thread) and resume the faulting one later, so the watch lasts TlbWatchErets ERETs.
        [NonSerialized] private System.Collections.Generic.List<(uint epc, string detail, int erets)> m_TlbPending2;
        private const int TlbWatchErets = 4096, TlbWatchMax = 32;

        private void TlbWatchAdd(uint epc, string detail)
        {
            m_TlbPending2 ??= new();
            if (m_TlbPending2.Count >= TlbWatchMax) { CompatLog.Note("cpu:tlb-exception", m_TlbPending2[0].detail + " unhandled: too many pending"); m_TlbPending2.RemoveAt(0); }
            m_TlbPending2.Add((epc, detail, 0));
        }

        /// <summary>[session_leak] Called by ERET when CompatLog is on: resolves pending TLB exception watches.</summary>
        public void NoteEret(uint target)
        {
            var l = m_TlbPending2; if (l == null || l.Count == 0) return;
            for (int i = l.Count - 1; i >= 0; i--) {
                var e = l[i];
                if (e.epc == target) { l.RemoveAt(i); continue; }
                if (++e.erets >= TlbWatchErets) { CompatLog.Note("cpu:tlb-exception", e.detail + $" unhandled: no ERET back to EPC {e.epc:X8} within {TlbWatchErets} ERETs"); l.RemoveAt(i); continue; }
                l[i] = e;
            }
        }

        public void SetInvalidTLBException(bool isStore) {
            throw new NotImplementedException("TODO: Implement TLB invalid");
        }

        // [cpu_diff] AdEL / AdES: BadVAddr = the faulting address, general exception vector (see Interpreter.AddrErr)
        public void RaiseAddressError(bool store, ulong va)
        {
            REGS.TLBRegs.BadVAddr = va;
            RaiseMipsException(store ? ExceptionType.AddressStore : ExceptionType.AddressLoad);
            if (CompatLog.On) CompatLog.Note("cpu:address-error", $"store={store} va={va:X16}");
        }

        public void SetCacheError()
        {
            m_CacheErr = true;
        }

        public void SetSoftReset()
        {
            m_SoftReset = true;
        }

        public void UseDirectAddresses() {
            m_Direct = true;
        }

        // [quirk_fix1] Reset button, first phase: the PIF raises the pre-NMI interrupt (Cause IP4) about 0.5 s before
        // the NMI. libultra posts OS_EVENT_PRENMI and masks IP4 itself; the bit stays set until the reset.
        public void SetPreNmi()
        {
            CR.SetInterruptPending(INTERRUPT_RESET);
        }

        public void SetNMIReset()
        {
            m_NMIReset = true;
        }

        public void MipsTimerTick(ulong count)
        {
            #if HACK_FASTER_TIMER
            count += 100;
            #endif

            if (CountScale != 1) count *= (uint)CountScale;
            REGS.AddCount((uint)count);
        }

        // [catalog_fix2] Count ticks per CPU step for this game (1 = every certified game). cor64 charges one cycle per
        // instruction and models no cache-miss or RDRAM stalls, so a boot that is memory-bound on hardware finishes in far
        // fewer Count ticks. BattleTanx / BattleTanx GA call osContInit before osCreateViManager; osContInit only arms a
        // timer (needing the timer list that osCreateViManager sets up) when osGetTime() is still under 500 ms, which on
        // hardware it no longer is. cor64 got there at Count ~8.6M (500 ms = 23.4M) and dereferenced the empty timer list.
        // mupen64plus lists these games with CountPerOp=3 (a fact; no code used). COR64_CATFIX2_COUNTX=0 disables the
        // table; COR64_COUNT_SCALE=n forces a factor for any game. Idle skip stays exact: TimerRemaining is in CPU steps.
        public static int CountScale = 1;
        private static readonly bool s_CountX = System.Environment.GetEnvironmentVariable("COR64_CATFIX2_COUNTX") != "0";
        private static readonly int s_CountScaleEnv = int.TryParse(System.Environment.GetEnvironmentVariable("COR64_COUNT_SCALE"), out var cse) && cse >= 1 ? cse : 0;
        public static int CountScaleFor(string gameCode) {
            if (s_CountScaleEnv > 0) return s_CountScaleEnv;
            if (!s_CountX || gameCode == null || gameCode.Length < 3) return 1;
            var id = gameCode.Substring(1, 2);
            // [catalog_fix5] Gauntlet Legends (NGX/NGD) hangs at its Controller Pak notice (800001C8) at 1 tick per step;
            // at 2 it reaches gameplay (mupen64plus lists it with a non-default CountPerOp, a fact only).
            // COR64_CATFIX5_COUNT=0 disables these entries.
            // (rows GX/GD in GameDb.cs)
            // [ingame_fix1] FIFA Soccer 64 (N7I): at 1 tick per step a Count-timer-driven counter (800F158C) becomes non-zero
            // before the main thread runs its one-time init (800CC5FC, called only while 800F158C == 0). The init, which
            // stores 50/60 fps in 800F1634, is then skipped, and after team select `div 0x3C0000 / 0` traps (break 7) forever:
            // an empty stadium. At 2 the init runs first and the match plays. COR64_INGFIX1_FIFACOUNT=0 disables this.
            // (row 7I in GameDb.cs)
            // [ingame_fix3] BattleTanx: Global Assault (NBQ): at 4 ticks per step Start on the title is ignored and the
            // attract loop (3DO -> title -> story) repeats; at 3 (the CountPerOp mupen64plus lists, a fact only) Start opens
            // the Controller Pak screen, Game Setup and the campaign. 2 still hits catalog_fix2's osContInit problem.
            // COR64_INGFIX3_BQCOUNT=0 keeps 4.
            // (row BQ in GameDb.cs)
            if (cor64.Session.GameDb.TryGet(cor64.Session.GameDb.Setting.CountFixed, id, out var kf)) return kf;
            int k1 = cor64.Session.GameDb.TryGet(cor64.Session.GameDb.Setting.CountBase, id, out var k) ? k : 1;
            return VispFix1Count(id, k1);
        }

        // [visp_fix1] COR64_VISPFIX1_COUNT (default on for the listed games; =0 off; =all every game, experiment).
        // FASTER_VI makes one CPU step stand for ViSpeedup 93.75 MHz cycles of video/audio/PI/SI time, but Count still
        // advanced 1 per step. Hardware Count runs at half the CPU clock, so at the default VI_SPEEDUP 11 the guest's
        // osGetTime/OSTimer clock ran 5.5x slower than its VI and audio clocks. Games that wait out a screen or a load on
        // an OSTimer (Hercules: the Titus licence screen never ends at Count scale 1-3, ends at 6) froze, went black or
        // ignored input at VI_SPEEDUP 11 but played at 1. Fix: Count ticks per step = ceil(ViSpeedup / 2), i.e. guest time
        // consistent with the VI. A larger per-game factor (catalog_fix2 table) is kept.
        private static readonly string s_VispFix1Env = System.Environment.GetEnvironmentVariable("COR64_VISPFIX1_COUNT");
        // per-game list: GameDb.Setting.ViCountFix rows (Session/GameDb.cs)
        private static int VispFix1Count(string id, int k)
        {
            if (s_VispFix1Env == "0") return k;
            if (s_VispFix1Env != "all" && !cor64.Session.GameDb.Has(cor64.Session.GameDb.Setting.ViCountFix, id)) return k;
            int v = (cor64.Telemetry.ViSpeedup + 1) / 2;
            return v > k ? v : k;
        }

        // [boot_fix1] COR64_BOOT1_VI (default on; =0 off): hardware VI timing for the first COR64_BOOT1_FIELDS (default 120,
        // 2 s NTSC) VI interrupts after power-on. FASTER_VI gives every VI field ViSpeedup times fewer CPU steps (and, for
        // games outside visp_fix1's list, Count stays 1 tick per step), so boot work and OSTimer waits stretch over ~ViSpeedup
        // times more frames. SM64 idles ~115M Count ticks before its logo: black until frame ~790 at VI_SPEEDUP 11, logo at
        // frame ~85 at VI_SPEEDUP 1 (hardware: 1-2 s). The window runs at speed-up 1 and then the game's own speed-up (and
        // its exact certified timing) returns. Loading a savestate ends the window, so savestate gates are unchanged.
        public static readonly bool Boot1Vi = System.Environment.GetEnvironmentVariable("COR64_BOOT1_VI") != "0";
        public static readonly int Boot1Fields = int.TryParse(System.Environment.GetEnvironmentVariable("COR64_BOOT1_FIELDS"), out var bf) && bf >= 0 ? bf : 120;
        // Count follows: visp_fix1 games get their Count factor for speed-up 1 during the window. Carts whose speed-up a
        // per-cart floor raised above the default (ingame_fix2) keep it.
        private static int s_Boot1Vi, s_Boot1Left, s_Boot1Count;
        private static string s_Boot1Game;
        public static bool Boot1Active { get; private set; }
        public static void Boot1Arm(string gameCode) { s_Boot1Game = gameCode; Boot1Arm(); }
        public static void Boot1Arm()
        {
            Boot1End();
            if (!Boot1Vi || Boot1Fields == 0 || cor64.Telemetry.ViSpeedup <= 1 || cor64.Telemetry.ViSpeedup > cor64.Telemetry.ViSpeedupBase) return;
            s_Boot1Vi = cor64.Telemetry.ViSpeedup; s_Boot1Count = CountScale;
            cor64.Telemetry.ViSpeedup = 1; CountScale = CountScaleFor(s_Boot1Game);
            s_Boot1Left = Boot1Fields; Boot1Active = true;
        }
        public static void Boot1Field() { if (Boot1Active && --s_Boot1Left <= 0) Boot1End(); }
        public static void Boot1End()
        {
            if (!Boot1Active) return;
            cor64.Telemetry.ViSpeedup = s_Boot1Vi; CountScale = s_Boot1Count; Boot1Active = false;
        }

        public uint TimerCount => (uint)REGS.Read(CTS.CP0_REG_COUNT);

        public uint TimerMax => (uint)REGS.Read(CTS.CP0_REG_COMPARE);

        public ExecutionState State => m_State;

        public void CpuRegisterWrite(int i, ulong value) {
            // On timer compare write, clear the timer pending interrupt
            if (i == CTS.CP0_REG_COMPARE) {

                // if (value <= REGS.Read(CTS.CP0_REG_COUNT)) {
                //     Log.Debug("MIPS Why? Compare {0:X8} <= Count {1:X8}", value, REGS.Read(CTS.CP0_REG_COUNT));
                // }

                // [audio_fix] hardware: Count and Compare are 32-bit and the interrupt fires when Count reaches Compare,
                // wrapping. The old test (Compare > Count at the write, then Count >= sign-extended Compare) never
                // fired for a Compare across the Count wrap or with bit 31 set, so libultra OSTimers stayed queued
                // and re-arming one corrupted the timer list (Blast Corps: osStopTimer null store, game + audio freeze).
                if (s_TimerWrap) {
                    m_TimerArm = (uint)REGS.Read(CTS.CP0_REG_COUNT);
                    m_EnableTimer = (uint)value != m_TimerArm;
                }
                else m_EnableTimer = value > REGS.Read(CTS.CP0_REG_COUNT);
                CR.ClearPendingInterrupt(INTERRUPT_TIMER);

#if DEBUG_MIPS_TIMER
                Log.Debug("Mips Timer Compare set to {0}", value);
#endif
            }


#if DEBUG_INTERRUPTS
            if (i == CTS.CP0_REG_EPC) {
                Log.Debug("EPC was modified by CPU");
            }

            if (i == CTS.CP0_REG_ERROR_EPC) {
                Log.Debug("Error EPC was modified by CPU");
            }
#endif

#if DEBUG_EPC
            if (i == CTS.CP0_REG_EPC) {
                Log.Debug("EPC CPU Write: {0:X8}", value);
            }
#endif

            REGS.CpuRegWrite(i, value);
            if (s_TimerWrap && i == CTS.CP0_REG_COUNT) {
                m_TimerArm = (uint)value;
                m_EnableTimer = (uint)REGS.Read(CTS.CP0_REG_COMPARE) != m_TimerArm;
            }
        }

        // [audio_fix] COR64_AUDIOFIX_TIMERWRAP=0 restores the old comparison. m_TimerArm: Count when the timer was armed;
        // the interrupt is due once (Count - m_TimerArm) reaches (Compare - m_TimerArm), all mod 2^32.
        public static readonly bool s_TimerWrap = System.Environment.GetEnvironmentVariable("COR64_AUDIOFIX_TIMERWRAP") != "0";
        // NonSerialized (state format unchanged): after a load it is 0 until the next Compare/Count write, which gives
        // the old comparison for a Compare below 2^31.
        [NonSerialized] private uint m_TimerArm;
        private uint TimerDistance => (uint)REGS.Read(CTS.CP0_REG_COMPARE) - m_TimerArm;
        private uint TimerElapsed => (uint)REGS.Read(CTS.CP0_REG_COUNT) - m_TimerArm;
        private bool TimerDue => s_TimerWrap ? TimerElapsed >= TimerDistance : REGS.Read(CTS.CP0_REG_COUNT) >= REGS.Read(CTS.CP0_REG_COMPARE);
        // Count ticks until the timer is due (0 = due now); only meaningful while m_EnableTimer.
        private ulong TimerRemaining {
            get {
                ulong r;
                if (s_TimerWrap) { uint d = TimerDistance, e = TimerElapsed; r = e >= d ? 0 : d - e; }
                else { ulong count = REGS.Count, compare = (uint)REGS.Read(CTS.CP0_REG_COMPARE); r = count >= compare ? 0 : compare - count; }
                return CountScale == 1 ? r : (r + (ulong)CountScale - 1) / (ulong)CountScale; // in CPU steps
            }
        }

        public ulong CpuRegisterRead(int i) {
            return REGS.CpuRegRead(i);
        }

        public bool IsNonMaskablePending {
            // TODO: Should compute this at the write of any of these flags 
            get {
                return m_Syscall ||
                m_Break ||
                m_Trap ||
                m_TLBRefillException ||
                m_XTLBRefillExcepetion ||
                m_CacheErr;
            }
        }

        private bool IsServicing => SR.ErrorLevel || SR.ExceptionLevel;

        private bool InterruptsEnabled => SR.InterruptsEnabled && !IsServicing;

        private void CheckTimerInterrupt() {
            /* Timer Interrupt */
            // XXX: Active timer always set the pending bit even if interrupts are disabled
            if (m_EnableTimer) {
                if (TimerDue)
                {
                    CR.SetInterruptPending(INTERRUPT_TIMER);
                    m_EnableTimer = false;

                    #if DEBUG_MIPS_TIMER
                        Log.Debug("Mips timer has hit the target");

                        if (SR.InterruptsEnabled && !SR.ErrorLevel && !SR.ExceptionLevel) {
                            Log.Debug("MIPS Timer has fired but interrupts are disabled");
                        }
                        else {
                            Log.Debug("MIPS Timer will trigger an interrupt");
                        }
                    #endif
                }
            }
        }

        private void CheckRcpInterrupt() {
            /* RCP Hardware Interrupt */
            if (m_Interface != null) {
                CR.ClearPendingInterrupt(INTERRUPT_RCP);

                if ((m_Interface.Interrupt & m_Interface.Mask) != 0)
                {
                    #if DEBUG_INTERRUPTS && DEBUG_MI
                    Log.Debug("RCP INTERRUPT: {0:X8} AND {1:X8}", m_Interface.Interrupt, m_Interface.Mask);
                    #endif

                    #if !FILTER_RCP_INTERRUPTS
                    CR.SetInterruptPending(INTERRUPT_RCP);
                    #endif
                }
            }
        }

        private static readonly bool s_DbgExc = System.Environment.GetEnvironmentVariable("COR64_DBG_EXC") == "1"; // debug aid
        private static int s_DbgExcN; private static readonly int s_DbgExcMax = int.TryParse(System.Environment.GetEnvironmentVariable("COR64_DBG_EXC_MAX"), out var dem) ? dem : 2000, s_DbgExcSkip = int.TryParse(System.Environment.GetEnvironmentVariable("COR64_DBG_EXC_SKIP"), out var des) ? des : 0; private static readonly bool s_DbgExcNoInt = System.Environment.GetEnvironmentVariable("COR64_DBG_EXC_NOINT") == "1"; // debug aid
        private static readonly bool s_DbgGpr = System.Environment.GetEnvironmentVariable("COR64_DBG_GPR") == "1"; // debug aid
        private static string DbgRegs() { var c = InterpreterBaseR4300I.DbgInst; if (c == null) return ""; var sb = new System.Text.StringBuilder(" gpr"); for (int i = 1; i < 32; i++) sb.Append(' ').Append(i).Append('=').Append(((uint)c.ReadGPR64(i)).ToString("X8")); return sb.ToString(); }
        private void SignalServiceNeeded(long pc, long target, bool isErrorException, bool isDelaySlot) {
                #if DEBUG_INTERRUPTS
                Log.Debug("Execute Interrupt Handler");
                #endif

                SR.ExceptionLevel = !isErrorException;
                SR.ErrorLevel = isErrorException;

                var selectedEPC = isErrorException ? CTS.CP0_REG_ERROR_EPC : CTS.CP0_REG_EPC;

                // Sign extension for 64-bit mode
                if (SR.IsOperation64) {
                    pc = (int)pc;
                }

                if (isDelaySlot)
                {
                    REGS.Write(selectedEPC, (ulong)(pc - 4));
                    CR.SetBranchDelayBit();
                }
                else
                {
                    REGS.Write(selectedEPC, (ulong)pc);
                    CR.ClearBranchDelayBit();
                }

                #if DEBUG_INTERRUPTS
                Log.Debug("PC to return {0:X8} after servicing interrupt/exception", REGS.CpuRegRead(isErrorException ? CTS.CP0_REG_ERROR_EPC : CTS.CP0_REG_EPC));
                #endif

                ExceptionHandlerAddress = target;
                if (s_DbgExc && (!s_DbgExcNoInt || (REGS.Read(CTS.CP0_REG_CAUSE) & 0x7C) != 0) && s_DbgExcN++ < s_DbgExcMax && s_DbgExcN > s_DbgExcSkip) System.Console.WriteLine($"EXC pc={(uint)pc:X8} tgt={(uint)target:X8} cause={(uint)REGS.Read(CTS.CP0_REG_CAUSE):X8} sr={(uint)REGS.Read(CTS.CP0_REG_SR):X8} bad={(uint)REGS.TLBRegs.BadVAddr:X8} ds={isDelaySlot} count={(uint)REGS.Read(CTS.CP0_REG_COUNT):X8} ticks={Telemetry.Ticks} mi={cor64.Mips.MipsInterface.DbgInst?.DbgIntr:X2}/{cor64.Mips.MipsInterface.DbgInst?.DbgMask:X2}" + (s_DbgGpr ? DbgRegs() : ""));

                // Ready for servicing
                ServicingRequred = true;
        }

        private uint _VecAddress(uint offset) {
            if (m_Direct) {
                return offset;
            }
            else {
                return 0x80000000 + offset;
                // TODO: Should respect the bootstrap vectors flag
            }
        }

        public void ClearCauseCe() => CR.ClearCe();

        // [cer64plus] Idle skip support. True only if, over the next n instructions, no interrupt can be taken and no timer
        // state can change: no reset pending, the timer cannot reach Compare, and no enabled interrupt is pending once the
        // RCP bit is refreshed from the current MI state (MI cannot change during a CPU slice).
        // [scripts_speed] COR64_SCRIPTS_SPEED_TIMERHI (default on): CheckTimerInterrupt compares the zero-extended 32-bit
        // Count with the Compare register as stored, i.e. 64-bit and sign-extended by mtc0. A Compare with bit 31 set is
        // stored as FFFFFFFF_xxxxxxxx, which Count can never reach, so with this emulator's timer that interrupt cannot
        // fire, and the idle skip need not stop for it (Perfect Dark idles with Compare 9FD93E24 and Count past it).
        // Exact for the current timer semantics; if the timer comparison is changed (hardware fires on Count == Compare,
        // 32-bit), this predicate must change with it. See lanes/scripts_speed/findings/FINDINGS.md.
        private static readonly bool s_TimerHi = System.Environment.GetEnvironmentVariable("COR64_SCRIPTS_SPEED_TIMERHI") != "0";
        private bool TimerUnreachable() => !s_TimerWrap && s_TimerHi && REGS.Read(CTS.CP0_REG_COMPARE) > 0xFFFFFFFFUL;

        public bool IdleSlicePossible(uint n) {
            if (m_NMIReset || m_SoftReset || ServicingRequred || IsNonMaskablePending || CR.ExceptionThrown) return false;
            if (m_EnableTimer && !TimerUnreachable() && TimerRemaining <= n) return false;
            uint pending = CR.InterruptPending & ~(1u << INTERRUPT_RCP);
            if (m_Interface != null && (m_Interface.Interrupt & m_Interface.Mask) != 0) pending |= 1u << INTERRUPT_RCP;
            if (InterruptsEnabled && (pending & SR.InterruptMask) != 0) return false;
            return true;
        }

        // [cer64plus] Largest m such that IdleSlicePossible(m * n) holds, given that IdleSlicePossible(n) already does.
        public long IdleSliceHorizon(uint n, long cap) {
            if (!m_EnableTimer || TimerUnreachable()) return cap;
            ulong rem = TimerRemaining;
            if (rem == 0) return 0;
            return Math.Min(cap, (long)FastDiv.Div(rem - 1, (ulong)n));
        }

        // [cer64plus] Count ticks after which CheckTimerInterrupt could first fire (Count >= Compare): 0 if it could now.
        // [speed_poll1] an enabled interrupt line is pending, whatever IE says
        public bool PendingMasked() {
            uint pending = CR.InterruptPending & ~(1u << INTERRUPT_RCP);
            if (m_Interface != null && (m_Interface.Interrupt & m_Interface.Mask) != 0) pending |= 1u << INTERRUPT_RCP;
            return (pending & SR.InterruptMask) != 0;
        }
        public uint MiMask => m_Interface != null ? m_Interface.Mask : 0; // [speed_poll1]
        public uint MiPending() => m_Interface != null ? m_Interface.Interrupt & m_Interface.Mask : 0;

        public int QuietTicks() {
            if (!m_EnableTimer) return int.MaxValue;
            return (int)Math.Min(TimerRemaining, int.MaxValue);
        }

        // Applies the effect of the interrupt checks inside a skipped idle slice (RCP pending bit refreshed, Cause CE cleared).
        public void IdleSliceApplyCheck() { CR.ClearCe(); CheckRcpInterrupt(); }

        public void CheckInterrupts(long pc, bool isDelaySlot) {
            // Reset some cause bits
            CR.ClearCe();

            // TODO: Should precompute based during the write to this flag
            //       Use vars for interrupt_cacheTarget, interrupt_target
            // if (SR.TestFlags(StatusRegister.StatusFlags.UseBootstrapVectors))
            // {
            //     cacheTarget = 0xBFC00200;
            //     target = 0xBFC00200;
            // }
            // else
            // {
            //     cacheTarget = 0xA0000180;
            //     target = 0x80000180;
            // }

            CheckTimerInterrupt();
            CheckRcpInterrupt();

            /* Hard Reset */
            if (m_NMIReset) {
                m_NMIReset = false;
                // TODO Respect the m_Direct flag for testing
                SignalServiceNeeded(pc, 0xBFC00000, true, isDelaySlot);
            }

            /* Soft Reset (via the reset button) */
            else if (m_SoftReset)
            {
                m_SoftReset = false;
                CR.SetInterruptPending(INTERRUPT_RESET);
                // TODO Respect the m_Direct flag for testing
                SignalServiceNeeded(pc, 0xBFC00000, false, isDelaySlot);
            }

            /* Normal interrupts */
            else if (InterruptsEnabled && (CR.InterruptPending & SR.InterruptMask) != 0) {
                    CR.SetException(ExceptionType.Interrupt, false);
                    SignalServiceNeeded(pc, _VecAddress(0x180), false, isDelaySlot);

                    // #if DEBUG_INTERRUPTS
                    // Log.Debug("IP: {0:X8} SR: {1:X8} = {2}", CR.InterruptPending, SR.InterruptMask, executeHandler);
                    // #endif
            }

            CR.ClearThrownException();
        }

        public void CheckExceptions(long pc, bool isDelaySlot) {
            // Reset some cause bits
            CR.ClearCe();

            // if (SR.TestFlags(StatusRegister.StatusFlags.UseBootstrapVectors))
            // {
            //     cacheTarget = 0xBFC00200;
            //     target = 0xBFC00200;
            // }
            // else
            // {
            //     cacheTarget = 0xA0000000;
            //     target = 0x80000000;
            // }

            if (IsNonMaskablePending) {
                // TODO: make non maskable exceptions just enumerable since only one can be happen at a time

                /* 32-bit TLB Exception */
                if (m_TLBRefillException)
                {
                    m_TLBRefillException = false;
                    #if DEBUG_TLB
                    Log.Debug("32-bit TLB exeception thrown");
                    #endif
                    SignalServiceNeeded(pc, _VecAddress(0x80), false, isDelaySlot);
                }

                /* 64-bit TLB Exception */
                else if (m_XTLBRefillExcepetion)
                {
                    #if DEBUG_TLB
                    Log.Debug("64-bit TLB exeception thrown");
                    #endif
                    m_XTLBRefillExcepetion = false;
                    SignalServiceNeeded(pc,  _VecAddress(0x80), false, isDelaySlot);
                }

                /* CPU Cache Error */
                else if (m_CacheErr)
                {
                    m_CacheErr = false;
                    SignalServiceNeeded(pc, _VecAddress(0x100), true, isDelaySlot);
                }

                /* SYSCALL: Non maskable exception */
                else if (m_Syscall) {
                    m_Syscall = false;
                    SignalServiceNeeded(pc, _VecAddress(0x180), false, isDelaySlot);
                }

                /* BREAK: Non maskable exception */
                else if (m_Break) {
                    m_Break = false;
                    SignalServiceNeeded(pc, _VecAddress(0x180), false, isDelaySlot);
                }

                /* TRAP: Non maskable exception */
                else if (m_Trap) {
                    m_Trap = false;
                    SignalServiceNeeded(pc, _VecAddress(0x180), false, isDelaySlot);
                }
            }

            /* MIPS Maskable Exception */
            else if (CR.ExceptionThrown)
            {
                if (m_TlbPending) {
                    m_TlbPending = false;
                    if (CompatLog.On && !s_TlbKeyHandled) CompatLog.Note("cpu:tlb-exception-pc", $"pc={(uint)pc:X8} bd={isDelaySlot}"); // [cpu_diff] logging only
                    if (Interpreter.s_Ring) Interpreter.RingDumpStatic($"tlb pc={(uint)pc:X8}");
                    if (m_TlbKeepEpc) {
                        // EXL was set: EPC and BD unchanged, general vector
                        ExceptionHandlerAddress = m_TlbVector;
                        ServicingRequred = true;
                    }
                    else SignalServiceNeeded(pc, m_TlbVector, false, isDelaySlot);
                    if (CompatLog.On && s_TlbKeyHandled && m_TlbWatchDetail != null) {
                        TlbWatchAdd((uint)REGS.Read(CTS.CP0_REG_EPC), m_TlbWatchDetail + $" pc={(uint)pc:X8} bd={isDelaySlot}"); m_TlbWatchDetail = null;
                    }
                    CR.ClearThrownException();
                    return;
                }

                // Log.Debug("MIPS threw an exception: {0}", CR.Exception);
                SignalServiceNeeded(pc, _VecAddress(0x180), false, isDelaySlot);
            }

            CR.ClearThrownException();
        }

        public int ModeBits => (int)SR.ModeBits;

        /// <summary>[cer64plus] CheckExceptions does something only when this is true (otherwise it clears flags that are already clear).</summary>
        public bool ExceptionPending => IsNonMaskablePending || CR.ExceptionThrown;

        public bool IsKernelMode => SR.ModeBits == 0;

        public bool IsSupervisorMode => SR.ModeBits == 1;

        public bool IsUserMode => SR.ModeBits == 2;

        public bool IsOperation64 => SR.IsOperation64;

        public void RaiseMipsException(ExceptionType type) {
            State.LLBit = false; // All exceptions cause atomic operations to fail
            CR.SetException(type);
        }

        // [cer64plus] SR.CU1 gates every COP1 instruction. libultra relies on the resulting fault to mark a thread
        // as an FPU user (thread->fp); without it, FPU registers are not saved across thread switches.
        public StatusRegister StatusReg => SR; // [cpu_fpu_path]
        internal ref uint CauseRawRef => ref CR.RawValueRef; // [climb4 speed_cat11]
        internal ref ulong RegsRawRef => ref REGS.RegsRef; // [climb4 speed_cat11]
        public bool Cop1Usable => SR.TestFlags(StatusRegister.StatusFlags.UsableCop1);

        public void RaiseCopUnusable(uint unit) {
            RaiseMipsException(ExceptionType.Unusable);
            CR.SetCe(unit);
        }

        public void SyscallException() {
            CR.SetException(ExceptionType.Syscall);
            m_Syscall = true;
        }

        public void BreakException() {
            CR.SetException(ExceptionType.Breakpoint);
            m_Break = true;
        }

        public void TrapException() {
            CR.SetException(ExceptionType.Trap);
            m_Trap = true;
        }

        /// <summary>
        /// This should be only set when the system controller detects interrupts waiting to be serviced
        /// </summary>
        public bool ServicingRequred;
    }
}
