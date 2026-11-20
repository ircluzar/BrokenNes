using cor64.IO;
using cor64.Mips;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NLog;
using System.IO;
using cor64.Mips.R4300I;
using cor64.Debugging;
using cor64.RCP;
using cor64.Rdp.LLE;
using cor64.HLE;
using cor64.HLE.OS;
using cor64.PIF;

namespace cor64
{
    public class N64System : cor64.Cores.IN64CpuCore
    {
        private readonly static Logger Log = LogManager.GetCurrentClassLogger();
        private readonly BootManager m_BootManager;
        private Cartridge m_Cartridge;

        private readonly JoyController[] m_JoyControllers = new JoyController[4];

        // [cer64plus] CPU slot contract (main emulation). The PPU is the RDP device, the APU is attached here.
        public string Id => cor64.Cores.N64Cores.CpuCor64;
        public string Description => "VR4300 interpreter + RSP (" + (cor64.Mips.Rsp.RspInterpreter.JitEnabled ? "block cache" : "interpreter") + ")";
        public N64System Machine => this;
        public void RunBatch(int n) => TickBatch(n);
        public long ViCount => DeviceRcp.VideoInterface.ViCount;
        /// <summary>CPU ticks on this machine (Telemetry.Ticks is process-wide, diagnostics only).</summary>
        public long Ticks;
        [NonSerialized] private cor64.Cores.IN64ApuCore m_Apu;
        public cor64.Cores.IN64ApuCore Apu => m_Apu;
        public cor64.Cores.IN64PpuCore Ppu => DeviceRcp.DeviceRdp;
        /// <summary>Choose the APU core (before Boot, or any time: the AI just pushes into the new one).</summary>
        public void AttachApu(cor64.Cores.IN64ApuCore apu) { m_Apu = apu; if (DeviceRcp?.AudioInterface != null) DeviceRcp.AudioInterface.Apu = apu; }

        /// <summary>[cer64plus] Expansion Pak present (8 MB reported to the game). Default on; env COR64_RDRAM_MB=4 turns it off.</summary>
        public bool ExpansionPak { get; set; } = Environment.GetEnvironmentVariable("COR64_RDRAM_MB") != "4";

        public N64System()
        {
            SystemMemory = new N64MemoryController();
            //DeviceMemory.UseSafeAccess();

            /* Boot Management */
            m_BootManager = new BootManager();
            m_BootManager.MMIOWrite += MMIORegWrite;

            Dbg = new EmuDebugger(this);

            m_JoyControllers[0] = new JoyController(0);
        }

        private void MMIORegWrite(BootManager.MMIORegWriteKind kind, uint v)
        {
            switch (kind)
            {
                case BootManager.MMIORegWriteKind.MiVersion: DeviceRcp.RcpInterface.SetVersion(v); break;
                case BootManager.MMIORegWriteKind.SpStatus: DeviceRcp.RspInterface.SetStatus(v); break;
                default: break;
            }
        }

        public N64System CPU(InterpreterBaseR4300I interpreter)
        {
            Log.Info("CPU Engine: {0}", interpreter.Description);

            DeviceCPU = interpreter;

            interpreter.AttachBootManager(m_BootManager);
            return this;
        }

        public N64System Boot(Cartridge cartridge)
        {
            /* Attach cartridge to system */
            m_Cartridge = cartridge;
            SystemMemory.MountCartridge(cartridge);
            // [jfg_crash] the CIC-6103 IPL3 jumps to the header entry - 0x100000 and the 6106 to entry - 0x200000
            // (ares/cen64 boot). The osMemSize write at the entry point never fired for those games, leaving the 0 that
            // the LLE IPL3 RDRAM probe stores at 0x318 (Excitebike 64: heap end 0x7FFFFF00 -> TLB fault, logo forever).
            uint entry = cartridge.EntryPoint;
            if (Environment.GetEnvironmentVariable("COR64_CIC_ENTRYFIX") != "0") {
                if (cartridge.IPL?.Cic == LockoutChipType.X103) entry -= 0x100000;
                else if (cartridge.IPL?.Cic == LockoutChipType.X106) entry -= 0x200000;
            }
            DeviceCPU.SetProgramEntryPoint(entry);
            /* [cer64plus] osMemSize: Expansion Pak on unless COR64_RDRAM_MB=4 (or ExpansionPak=false) */
            DeviceCPU.ReportedRdramSize = ExpansionPak ? 0x800000U : 0x400000U;
            DeviceCPU.MemSizeAt3F0 = cartridge.IPL?.Cic == LockoutChipType.X105 && Environment.GetEnvironmentVariable("COR64_CIC6105_MEMSIZE3F0") != "0";

            /* Attach RCP to memory */
            DeviceRcp.AttachToMemory(SystemMemory);
            m_Apu ??= new AiDac();
            // [jfg_crash] DPC_CLOCK. [session_leak] COR64_DPC_CLOCK_LOCAL (default on): this machine's RSP steps, not the
            // process-wide counter (=0: the old static, which leaked from one session into the next).
            if (s_DpcClockLocal && DeviceRcp.DeviceRsp is cor64.Mips.Rsp.RspInterpreter dri)
                DeviceRcp.DisplayProcessorCommandInterface.TickSource = () => Ticks * 2 / 3 + dri.StepsLocal;
            else DeviceRcp.DisplayProcessorCommandInterface.TickSource = () => Ticks * 2 / 3 + cor64.Mips.Rsp.RspInterpreter.RspSteps;
            DeviceRcp.AudioInterface.Apu = m_Apu;

            /* Initlaize core memory */
            SystemMemory.Init();

            /* Cartridge verification */
            // ((MemMappedCart) SystemMemory.Cart).SelfTest(cartridge.RomStream);

            /* Init the RDP */
            if (DeviceRcp.GraphicsHLEDevice == null)
                 DeviceRcp.DeviceRdp.Init();

            /* Init the RSP */
            DeviceRcp.DeviceRsp.Init();

            /* Init the serial controller */
            DeviceRcp.SerialDevice.Init();

            /* Attach memory to CPU */
            DeviceCPU.AttachMemory(SystemMemory);

            /* Attach RCP to CPU */
            DeviceCPU.AttachRcp(DeviceRcp);

            /* Hook up joypads */
            DeviceRcp.SerialDevice.ReadJoycons += UpdateJoyconPif;

            /* Attach DMA engines */
            SystemMemory.PIRegs.AttachDma();
            SystemMemory.SPRegs.AttachDma();
            SystemMemory.SIRegs.AttachDma();

            /* Init the CPU */
            DeviceCPU.Init();

            /* Perform system boot intialization (Motherboard IPl) */
            m_BootManager.BootCartridge(cartridge, true);

            return this;
        }

        public N64System BootForTesting() {
            /* Attach RCP to memory */
            DeviceRcp.AttachToMemory(SystemMemory);

            if (DeviceCPU != null) {
                /* Attach memory to CPU */
                DeviceCPU.AttachMemory(SystemMemory);

                /* Attach RCP to CPU */
                DeviceCPU.AttachRcp(DeviceRcp);
            }
            /* Initialize system memory */
            SystemMemory.Init();

            return this;
        }

        /// <summary>
        /// Step all system events on a single system tick (Single core mode)
        /// </summary>
        private static bool s_Workbench => CoreConfig.Current.WorkbenchMode;

        public void Tick()
        {
            // [cer64plus] the execution counter only matters to the workbench debugger
            if (s_Workbench) Dbg.EnterExecution();
            DeviceCPU.Step();
            Telemetry.Ticks++;
            if (s_Workbench) Dbg.LeaveExecution();

            VI.Tick();
            DeviceRcp.AudioInterface.Tick();
        }

        // [cer64plus] Batched stepping for the non-workbench loop: n CPU instructions, then n RSP instructions,
        // then video and audio advanced by n ticks. Interrupts and RSP/CPU handshakes are seen at most n-1
        // instructions late, which is well inside what N64 software tolerates.
        private static readonly bool s_DpcClockLocal = Environment.GetEnvironmentVariable("COR64_DPC_CLOCK_LOCAL") != "0";

        public void TickBatch(int n)
        {
            if (cor64.Debug.DebugHooks.Active) { cor64.Debug.DebugHooks.Current?.TickBatch(this, n); return; } // [dbg_exec] debugger: interpreted, exact stops
            var cpu = DeviceCPU;
            bool dbgW = SystemMemory.Watches != null; // [dbg_mem] watches armed: every CPU access runs through the bus
            if (dbgW) { }
            else if (s_FfStats) { if (TickBatchStats(n)) return; }
            else if (s_IdleFF && TryIdleFastForward(n)) return;
            if (!dbgW && s_Poll && s_IdleFF && cpu is cor64.Mips.R4300I.Interpreter pi && pi.PollCandidate && TryPollFastForward(pi, n)) return;
            if (s_SplitTiming) { SplitBusyBatches++; if (DeviceRcp.DeviceRsp.IsHalted) SplitBusyHaltedBatches++; }
            long tIdle0 = s_PhaseTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; bool idleBatch = false;
            long tC0 = tIdle0;
            if (dbgW && cpu is cor64.Mips.R4300I.Interpreter wp) wp.RunSteps(n);
            else if (cpu is cor64.Mips.R4300I.Interpreter ip) {
                if (cor64.Mips.R4300I.Interpreter.ViWaitPc != 0 && ip.TryViWaitBatch(n, DeviceRcp.VideoInterface.Line)) { } // [speed_cat9]
                else if (cor64.Mips.R4300I.Interpreter.s_OneProbe && s_IdleFF && ip.IdleProbeNotLoop) {
                    if (s_PhaseTiming) { s_BusyBatches++; if (DeviceRcp.DeviceRsp.IsHalted) s_BusyHaltedBatches++; }
                    if (s_Poll && ip.PollWantRecord()) ip.PollRecordBatch(n); else ip.RunSteps(n);
                }
                else if (ip.TryIdleSkip(n)) idleBatch = true;
                else {
                    // Entered the idle loop at the delay-slot phase: one real step realigns to the loop start,
                    // skip n-2 (even), one more real step restores the original phase. Same n steps in total.
                    if (ip.TryIdleSkipDelayPhase(n)) { }
                    else if (ip.AtIdleDelaySlot() && n >= 4) {
                        cpu.Step();
                        if (ip.TryIdleSkip(n - 2)) cpu.Step();
                        else if (s_Cat14IdleDs) { if (s_Cat16IdStat) Cat16IdSteps += n - 1; ip.RunSteps(n - 1); } // [climb4 speed_cat14]
                        else for (int i = 1; i < n; i++) cpu.Step();
                    }
                    else { if (s_PhaseTiming) { s_BusyBatches++; if (DeviceRcp.DeviceRsp.IsHalted) s_BusyHaltedBatches++; } if (s_Poll && ip.PollWantRecord()) ip.PollRecordBatch(n); else ip.RunSteps(n); }
                }
            }
            else for (int i = 0; i < n; i++) cpu.Step();
            if (s_PhaseTiming) s_TCpuReal += System.Diagnostics.Stopwatch.GetTimestamp() - tC0;
            Telemetry.Ticks += n;
            Ticks += n;
            long tA = s_PhaseTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; if (s_PhaseTiming) s_TCpu += tA - s_TMark;
            var rsp = DeviceRcp.DeviceRsp;
            if (rsp is cor64.Mips.Rsp.RspInterpreter ri) ri.RunCredit(n);
            else for (int i = 0; i < n; i++) rsp.Step();
            long tB = s_PhaseTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; if (s_PhaseTiming) s_TRsp += tB - tA;
            DeviceRcp.VideoInterface.Tick(n);
            DeviceRcp.AudioInterface.Tick(n);
            if (s_PhaseTiming) { s_TMark = System.Diagnostics.Stopwatch.GetTimestamp(); s_TIo += s_TMark - tB; if (idleBatch) { s_TIdle += s_TMark - tIdle0; s_IdleBatches++; s_TIdleRsp += tB - tA; } }
        }
        // [cer64plus] Idle fast-forward (COR64_IDLE_FF=0 disables). While the CPU sits in its idle loop, its part of a
        // batch changes nothing but counters until the VI or AI raises an interrupt or the timer comes due. So
        // whole batches are run as their RSP credit and VI and AI ticks only, up to and including the first batch that changes the MI
        // interrupt register (or the timer horizon), and the CPU's counters are applied once. Batch boundaries, and so
        // the instruction at which every interrupt is taken, are the same as batch-by-batch stepping.
        // [climb4 speed_cat14] COR64_SPDCAT14_IDLEDS (default on): when the CPU entered a batch at the idle loop's delay slot
        // and the idle skip is refused (an interrupt is due in the batch), the rest of the batch runs through RunSteps
        // (compiled blocks) instead of n-1 interpreter steps. GT 64 interpreted ~44% of its instructions there (the
        // interrupt handler and the code it returns to, ~190 times a frame). COR64_SPDCAT14_IDLEDS=0 = old loop.
        public static readonly bool s_Cat14IdleDs = Environment.GetEnvironmentVariable("COR64_SPDCAT14_IDLEDS") != "0";
        // [climb4 speed_cat16] COR64_SPDCAT16_IDSTAT=1 (measurement only): steps taken by the speed_cat14 IDLEDS fallback, printed at exit
        static readonly bool s_Cat16IdStat = Environment.GetEnvironmentVariable("COR64_SPDCAT16_IDSTAT") == "1";
        static long Cat16IdSteps;
        static readonly bool s_Sm300Halt = Environment.GetEnvironmentVariable("COR64_SM300_HALT") != "0";
        public static readonly bool s_IdleFF = Environment.GetEnvironmentVariable("COR64_IDLE_FF") != "0";
        public static long IdleSpans, IdleSpanBatches;

        // [climb4 idle_span] COR64_IDLS_COUNT=1: span batch classes; COR64_IDLS_CEIL=1: bookkeeping run twice (exact)
        static readonly bool s_IdlsCount = Environment.GetEnvironmentVariable("COR64_IDLS_COUNT") == "1";
        static readonly bool s_IdlsMerge = Environment.GetEnvironmentVariable("COR64_IDLS_MERGE") != "0";
        static readonly bool s_IdlsCeil = Environment.GetEnvironmentVariable("COR64_IDLS_CEIL") == "1";
        static long IdlsSpans, IdlsHalted, IdlsEmpty, IdlsRan, s_IdlsSink;
        static N64System() {
            if (s_Cat16IdStat) AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.WriteLine($"IDSTAT steps={Cat16IdSteps}");
            if (s_FfStats) { ResetFfStats(); AppDomain.CurrentDomain.ProcessExit += (_, _) => PrintFfStats(); }
            if (s_IdlsCount) AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.Error.WriteLine(
                $"IDLS spans={IdlsSpans} halted={IdlsHalted} empty={IdlsEmpty} ran={IdlsRan} perSpan={(IdlsHalted+IdlsEmpty+IdlsRan)/(double)Math.Max(1,IdlsSpans):F1}");
        }
        // [climb4 idle_ff] COR64_IDLEFF_STATS=1: call/fail/span counters and per-call cost of TryIdleFastForward (measurement only).
        public static readonly bool s_FfStats = Environment.GetEnvironmentVariable("COR64_IDLEFF_STATS") == "1";
        static long FsCalls, FsOk, FsFailNotLoop, FsFailLoop, FsFailLoopZero, FsTFail, FsTOk, FsTLoopFail, FsBusy, FsBusyIdleSkip,
            FsEndMi, FsEndVi, FsEndCap, FsGapSum, FsGapN, FsGapLoopFail, FsSpanBatches, FsTCal;
        static readonly long[] FsSpanHist = new long[24], FsGapHist = new long[24];
        static long s_FsGap, s_FsGapLoopFail; public static long FsT0;
        public static void ResetFfStats() {
            FsCalls = FsOk = FsFailNotLoop = FsFailLoop = FsFailLoopZero = FsTFail = FsTOk = FsTLoopFail = FsBusy = FsBusyIdleSkip = 0;
            FsEndMi = FsEndVi = FsEndCap = FsGapSum = FsGapN = FsGapLoopFail = FsSpanBatches = 0; s_FsGap = s_FsGapLoopFail = 0;
            Array.Clear(FsSpanHist); Array.Clear(FsGapHist); FsT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long a = System.Diagnostics.Stopwatch.GetTimestamp(); for (int i = 0; i < 1000; i++) System.Diagnostics.Stopwatch.GetTimestamp();
            FsTCal = (System.Diagnostics.Stopwatch.GetTimestamp() - a) / 1000;
        }
        static int Log2(long v) { int b = 0; while (v > 1 && b < 23) { v >>= 1; b++; } return b; }
        static void PrintFfStats() {
            double f = System.Diagnostics.Stopwatch.Frequency / 1e9, wallMs = (System.Diagnostics.Stopwatch.GetTimestamp() - FsT0) / f / 1e6;
            long fail = FsFailNotLoop + FsFailLoop;
            var o = Console.Error;
            o.WriteLine($"IDLEFF wallMs={wallMs:F0} calls={FsCalls} ok={FsOk} failNotLoop={FsFailNotLoop} failAtLoop={FsFailLoop} (m0={FsFailLoopZero}) calTicks={FsTCal}");
            o.WriteLine($"IDLEFF nsPerFail={(FsTFail / f) / Math.Max(1, fail):F1} nsPerLoopFail={(FsTLoopFail / f) / Math.Max(1, FsFailLoop):F1} totFailMs={FsTFail / f / 1e6:F1} nsPerOkSpan={(FsTOk / f) / Math.Max(1, FsOk):F0} totOkMs={FsTOk / f / 1e6:F1}");
            o.WriteLine($"IDLEFF spanBatches={FsSpanBatches} perSpan={FsSpanBatches / (double)Math.Max(1, FsOk):F1} endMi={FsEndMi} endVi={FsEndVi} endCap={FsEndCap}");
            o.WriteLine($"IDLEFF busyBatches={FsBusy} busyIdleSkip={FsBusyIdleSkip} gaps={FsGapN} gapBatchesAvg={FsGapSum / (double)Math.Max(1, FsGapN):F1} gapLoopFails={FsGapLoopFail}");
            o.WriteLine("IDLEFF spanHist(log2) " + string.Join(" ", FsSpanHist));
            o.WriteLine("IDLEFF gapHist(log2) " + string.Join(" ", FsGapHist));
        }
        bool TickBatchStats(int n) {
            if (!s_IdleFF) return false;
            var ip = DeviceCPU as cor64.Mips.R4300I.Interpreter;
            bool atLoop = ip != null && ip.StatsAtIdleLoop();
            var mi = DeviceRcp.RcpInterface; var vi = DeviceRcp.VideoInterface;
            uint int0 = mi.Interrupt; long vc0 = vi.ViCount; long b0 = IdleSpanBatches;
            FsCalls++;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool ok = TryIdleFastForward(n);
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0 - FsTCal;
            if (ok) {
                FsOk++; FsTOk += dt; long d = IdleSpanBatches - b0; FsSpanBatches += d; FsSpanHist[Log2(d)]++;
                if (mi.Interrupt != int0) FsEndMi++; else if (vi.ViCount != vc0) FsEndVi++; else FsEndCap++;
                if (s_FsGap > 0) { FsGapN++; FsGapSum += s_FsGap; FsGapHist[Log2(s_FsGap)]++; FsGapLoopFail += s_FsGapLoopFail; }
                s_FsGap = 0; s_FsGapLoopFail = 0;
                return true;
            }
            FsTFail += dt;
            if (atLoop) { FsFailLoop++; FsTLoopFail += dt; s_FsGapLoopFail++; if (cor64.Mips.R4300I.Interpreter.StatsLastM == 0) FsFailLoopZero++; } else FsFailNotLoop++;
            FsBusy++; s_FsGap++;
            if (atLoop) FsBusyIdleSkip++;
            return false;
        }

        private bool TryIdleFastForward(int n)
        {
            if (DeviceCPU is not cor64.Mips.R4300I.Interpreter ip) return false;
            if (DeviceRcp.DeviceRsp is not cor64.Mips.Rsp.RspInterpreter ri) { ip.IdleProbeNotLoop = false; return false; }
            long m = ip.IdleBatchesPossible(n, 1 << 22);
            if (m < 2 || !ip.BeginIdleSpan(n)) return false;
            var mi = DeviceRcp.RcpInterface;
            var vi = DeviceRcp.VideoInterface; var ai = DeviceRcp.AudioInterface;
            uint int0 = mi.Interrupt;
            long vc0 = vi.ViCount;
            long done = 0;
            // the CPU's idle loop reads nothing the RSP writes; the RSP reaches the CPU only through MI interrupts
            if (s_PhaseTiming) {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                while (done < m) {
                    long ta = System.Diagnostics.Stopwatch.GetTimestamp();
                    ri.RunCredit(n);
                    s_TSpanRsp += System.Diagnostics.Stopwatch.GetTimestamp() - ta;
                    vi.Tick(n); ai.Tick(n);
                    done++;
                    if (mi.Interrupt != int0 || vi.ViCount != vc0) break;
                }
                s_TSpan += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            }
            else if (s_SplitTiming) {
                // coarse: one timestamp pair per span, plus one per RSP running/halted transition
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), tr = 0; bool run = false;
                while (done < m) {
                    bool h = ri.IsHalted;
                    if (h == run) { long t = System.Diagnostics.Stopwatch.GetTimestamp(); if (run) SplitSpanRsp += t - tr; else tr = t; run = !h; }
                    if (h) SplitSpanHaltedBatches++;
                    ri.RunCredit(n);
                    vi.Tick(n); ai.Tick(n);
                    done++;
                    if (mi.Interrupt != int0 || vi.ViCount != vc0) break;
                }
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (run) SplitSpanRsp += t1 - tr;
                SplitSpan += t1 - t0; SplitSpanBatches += done;
            }
            else if (s_IdlsCount) {
                IdlsSpans++;
                while (done < m) {
                    bool h = ri.IsHalted; long st0 = cor64.Mips.Rsp.RspInterpreter.JitBlockRuns + cor64.Mips.Rsp.RspInterpreter.JitFallbackSteps;
                    ri.RunCredit(n);
                    if (h) IdlsHalted++; else if (cor64.Mips.Rsp.RspInterpreter.JitBlockRuns + cor64.Mips.Rsp.RspInterpreter.JitFallbackSteps == st0) IdlsEmpty++; else IdlsRan++;
                    vi.Tick(n); ai.Tick(n);
                    done++;
                    if (mi.Interrupt != int0 || vi.ViCount != vc0) break;
                }
            }
            else if (s_IdlsCeil) {
                // exact "run the bookkeeping twice": zero-credit RSP call and zero ticks are no-ops
                while (done < m) {
                    ri.RunCredit(n);
                    vi.Tick(n); ai.Tick(n);
                    ri.RunCredit(0); vi.Tick(0); ai.Tick(0);
                    if (mi.Interrupt != int0 || vi.ViCount != vc0) s_IdlsSink++;
                    done++;
                    if (mi.Interrupt != int0 || vi.ViCount != vc0) break;
                }
            }
            else if (s_IdlsMerge && cor64.Mips.Rsp.RspInterpreter.SpanRunSupported)
            while (done < m) {
                if (!ri.IsHalted) {
                    // merge batches up to the next VI/AI/timer event; the RSP runs one credit budget and stops at the
                    // first MI change or halt, which is mapped back to the batch it happened in
                    long L = m - done, t = vi.BatchesUntilEvent(n); if (t < L) L = t;
                    t = ai.BatchesUntilSample(n); if (t < L) L = t;
                    if (L >= 2) {
                        long c0 = ri.Credit, nk = (long)n * cor64.Mips.Rsp.RspInterpreter.CreditScale;
                        ri.RunSpan(L * nk, mi, int0, out int lastK);
                        long j = L;
                        if (ri.IsHalted || mi.Interrupt != int0) {
                            long S = c0 + L * nk - ri.Credit;   // steps run; the last block started at S - lastK
                            j = (S - lastK - c0) / nk + 1;      // batch whose credit started it
                            ri.Credit = c0 + j * nk - S;
                            ri.RunCredit(0);                    // rest of batch j (zeroes the credit if halted)
                        }
                        vi.Tick((int)(j * n)); ai.Tick((int)(j * n));
                        done += j;
                        if (mi.Interrupt != int0 || vi.ViCount != vc0) break;
                        continue;
                    }
                }
                else if (s_Sm300Halt && !mi.DmaPending) {
                    // [climb4 sm64_300] COR64_SM300_HALT: halted RSP in an idle span stays halted (only the CPU can start it,
                    // and it is idling). Its batches are VI/AI ticks only, so they merge up to the next VI/AI event.
                    long L = m - done, t = vi.BatchesUntilEvent(n); if (t < L) L = t;
                    t = ai.BatchesUntilSample(n); if (t < L) L = t;
                    if (L >= 2) {
                        ri.RunCredit(n);
                        vi.Tick((int)(L * n)); ai.Tick((int)(L * n));
                        done += L;
                        if (mi.Interrupt != int0 || vi.ViCount != vc0) break;
                        continue;
                    }
                }
                ri.RunCredit(n);
                vi.Tick(n); ai.Tick(n);
                done++;
                if (mi.Interrupt != int0 || vi.ViCount != vc0) break;
            }
            else
            while (done < m) {
                ri.RunCredit(n);
                vi.Tick(n); ai.Tick(n);
                done++;
                if (mi.Interrupt != int0 || vi.ViCount != vc0) break;
            }
            ip.ApplyIdleBatches(n, done);
            Telemetry.Ticks += done * n;
            Ticks += done * n;
            IdleSpans++; IdleSpanBatches += done;
            return true;
        }

        // [idle_speed] COR64_IDLE_POLL (default on): exact poll-loop fast-forward (see Mips/R4300I/PollLoop.cs). Batches
        // run one at a time (RSP credit, VI, AI) so the polled RDRAM words can be re-checked after each one.
        static readonly bool s_Poll = cor64.Mips.R4300I.Interpreter.s_PollSkip;
        private bool TryPollFastForward(cor64.Mips.R4300I.Interpreter ip, int n)
        {
            if (DeviceRcp.DeviceRsp is not cor64.Mips.Rsp.RspInterpreter ri) return false;
            int q = ip.PollMatch(n, 1 << 22, out long m);
            if (q < 0 || !ip.BeginIdleSpan(n)) return false;
            var mi = DeviceRcp.RcpInterface;
            var vi = DeviceRcp.VideoInterface; var ai = DeviceRcp.AudioInterface;
            uint int0 = mi.Interrupt;
            long vc0 = vi.ViCount;
            long done = 0;
            while (done < m) {
                ri.RunCredit(n);
                vi.Tick(n); ai.Tick(n);
                done++;
                if (mi.Interrupt != int0 || vi.ViCount != vc0 || !ip.PollWordsSame()) break;
            }
            ip.PollApply(n, done, q);
            Telemetry.Ticks += done * n;
            Ticks += done * n;
            return true;
        }

        // [cer64plus] measurement aid (COR64_PHASE_TIMING=1): wall time spent in the CPU, RSP and VI/AI parts of TickBatch.
        // [cer64plus] measurement aid (COR64_SPLIT_TIMING=1): coarse split with few timestamps (per idle span and per RSP
        // start/stop inside spans); the busy path is the rest of the frame. ResetSplit() after warm-up.
        public static readonly bool s_SplitTiming = Environment.GetEnvironmentVariable("COR64_SPLIT_TIMING") == "1";
        public static long SplitSpan, SplitSpanRsp, SplitSpanBatches, SplitSpanHaltedBatches, SplitBusyBatches, SplitBusyHaltedBatches, SplitT0;
        public static void ResetSplit() { SplitSpan = SplitSpanRsp = SplitSpanBatches = SplitSpanHaltedBatches = SplitBusyBatches = SplitBusyHaltedBatches = 0; SplitT0 = System.Diagnostics.Stopwatch.GetTimestamp(); if (s_FfStats) ResetFfStats(); }
        public static readonly bool s_PhaseTiming = Environment.GetEnvironmentVariable("COR64_PHASE_TIMING") == "1";
        public static long s_BusyBatches, s_BusyHaltedBatches, s_TCpuReal, s_TSpan, s_TSpanRsp, s_TRspSp, s_TRspDp, s_TIdle, s_IdleBatches, s_TIdleRsp, s_TCpu, s_TRsp, s_TIo, s_TMark = System.Diagnostics.Stopwatch.GetTimestamp();

        // [cer64plus] rebuild caches after a savestate load
        public void AfterStateLoad()
        {
            DeviceCPU.InvalidateDecodeCache();
            DeviceRcp.DeviceRsp.InvalidateDecodeCache();
            SystemMemory.SPRegs.ImemGeneration++; // IMEM bytes were replaced wholesale
        }

        public Video VI => DeviceRcp.VideoInterface;

        public PIFController PIF => SystemMemory.PIF;

        /// <summary>
        /// If an exception is thrown during a tick, call this to cleanup anything aftwards
        /// </summary>
        public void TickFinally()
        {
            Dbg.LeaveExecution();
        }

        // For supporting multiple threads, we need an event system to schedule ticks in parallel

        public void DumpExecutionLog(Stream stream)
        {
            StreamWriter writer = new StreamWriter(stream);

            if (DeviceCPU != null)
            {
                if (DeviceCPU.TraceLog.HasContent)
                {
                    writer.WriteLine("Trace Log Dump:");

                    var tracelog = DeviceCPU.TraceLog.GenerateTraceLog();

                    for (int i = 0; i < tracelog.Count; i++)
                        writer.WriteLine(tracelog[i]);

                    writer.WriteLine("\n\n");
                    writer.Flush();
                }
            }
            else
            {
                writer.WriteLine("No CPU was attached!");
            }
        }

        private void UpdateJoyconPif() {
            for (int i = 0; i < m_JoyControllers.Length; i++) {
                if (m_JoyControllers[i] == null) continue;
                m_JoyControllers[i].Read(out var b, out var x, out var y);
                PIF.JoyWriteState(i, b, x, y);
            }
        }

        public EmuDebugger Dbg { get; }

        public InterpreterBaseR4300I DeviceCPU { get; private set; }

        public N64MemoryController SystemMemory { get; }

        public RcpCore DeviceRcp { get; } = new RcpCore();

        public Cartridge AttachedCartridge => m_Cartridge;

        public JoyController[] Joys => m_JoyControllers;
    }
}
