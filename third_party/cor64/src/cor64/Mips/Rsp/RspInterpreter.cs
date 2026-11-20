using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics;
using System.Threading;
using System;
using System.Diagnostics;
using System.IO;
using cor64.IO;
using cor64.RCP;
using NLog;
using cor64.HLE;

/* RSP Notes:
    * VCO = Vector Not Equal and Carry Out
    * VCC = Vector Clip and Compare
    * VCE = Vector Sign Extensions
*/

/* KROM VU Test Roms
    RSPCP2LTV
    RSPCP2LWV
    RSPCP2V056
    RSPCP2V073
    RSPCP2VABS
    RSPCP2VACC
    RSPCP2VADDB
    RSPCP2VADD
    RSPCP2VAND
    RSPCP2VCL
    RSPCP2VCR
    RSPCP2VEQ
    RSPCP2VEXTN
    RSPCP2VEXTQ
    RSPCP2VEXTT
    RSPCP2VLT
    RSPCP2VMACF
    RSPCP2VMADL
    RSPCP2VMADN
    RSPCP2VMUDL
    RSPCP2VMUDN
    RSPCP2VMULF
    RSPCP2VMULQ
    RSPCP2VNOP
    RSPCP2VOR
    RSPCP2VRCPH
    RSPCP2VRCPL
    RSPCP2VRCP
    RSPCP2VRNDP
    RSPCP2VSAC
    RSPCP2VSAR
    RSPCP2VSUBB
    RSPCP2VSUB
    RSPCP2VSUT
    RSPCP2VXOR
*/

// XXX: These only verified by krom tests
/* Vector Unit Testing
    VSUT: Unsupported
    VADDB: Unsupported
    VSUBB: Unsupported
    VEXTT: Unsupported
    VEXTQ: Unsupported
    VEXTN: Unsupported
    V073: Unsupported
    VINST: Unsupported
    VINSQ: Unsupported
    VINSN: Unsupported
    VNULL: Unsupported
    V056: Unsupported
    V057: Unsupported
    VMULQ: Unsupported
    VMACQ: Unsupported
    VRNDP: Unsupported
    VRNDN: Unsupported
    LBV: Verified
    LSV: Verified
    LLV: Verified
    LDV: Verified
    LQV: Verified
    LRV: Verified
    LPV: Verified
    LUV: Verified
    LHV: Verified
    LFV: Unverified
    LWV: Unsupported
    LTV: Verified
    SBV: Verified
    SSV: Verified
    SLV: Verified
    SDV: Verified
    SQV: Verified
    SRV: Verified
    SPV: Verified
    SUV: Verified
    SHV: Verified
    SFV: Unverified
    SWV: Unsupported
    STV: Verified
    VADD: Verified
    VADDC: Verified
    VSUB: Verified
    VSUBC: Verified
    VMULF: Verified
    VMULU: Verified
    VMUDL: Verified
    VMUDM: Verified
    VMUDN: Verified
    VMUDH: Verified
    VMACF: Verified
    VMACU: Verified
    VMADL: Verified
    VMADM: Verified
    VMADN: Verified
    VMADH: Verified
    VSAR: Verified (except VSAW)
    VABS: Verified
    VAND: Verified
    VNAND: Verified
    VOR: Verified
    VNOR: Verified
    VXOR: Verified
    VNXOR: Verified
    VRCP: Verified
    VRCPL: Verified
    VRCPH: Verified
    VRSQ: Verified
    VRSQL: Verified
    VRSQH: Verified
    VEQ: Verified
    VNE: Verified
    VLT: Verified
    VGE: Verified
    VCL: Verified
    VCH: Verified
    VCR: Verified
    VMR: Verified
    VMOV: Verified
*/

namespace cor64.Mips.Rsp
{
    public partial class RspInterpreter : InterpreterBaseRsp
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private DecodedInstruction? m_InjectedInst;
        private DecodedInstruction m_FailedInstruction;
        public override string Description => "Rsp Interpreter";
        public DecodedInstruction FailedInstruction { get => m_FailedInstruction; set => m_FailedInstruction = value; }
        public RspVector[] m_VecRegs;
        private readonly UnmanagedBuffer m_VecPinnedMemory;
        private int m_DivIn = 0;
        private int m_DivOut = 0;
        private bool m_lastDoublePrecision = false;
        private bool m_InterruptOnBroke = false;
        private bool m_SkipForcedWait = false;
        private readonly ExecutionState m_State = new ExecutionState();
        [NonSerialized] private readonly RspVector m_ResolveScratch = new RspVector(); // [cer64plus] element broadcast target
        private long m_LastFetchAddress;

        private TaskType m_SpTaskType;

        private readonly EventWaitHandle m_CpuRspWait = new EventWaitHandle(false, EventResetMode.ManualReset);

        private readonly EventWaitHandle m_HaltWait = new EventWaitHandle(false, EventResetMode.ManualReset);

        public Func<DecodedInstruction, Action<DecodedInstruction>> CallHack { get; set; }

        enum TaskType : int {
            Unknown = 0,
            Graphics,
            Audio,
            Video,
            HVQ = 6,
            HHVQM
        }

        /* RSP Flag Registers */
        private readonly RspCarryFlag m_VCarry = new RspCarryFlag();      // VCO
        private readonly RspCompareFlag m_VccClip = new RspCompareFlag(); // VCC - Clip
        private readonly RspCompareFlag M_VccCompare = new RspCompareFlag(); // VCC - Compare
        private readonly RspCompareFlag m_Vce = new RspCompareFlag();  // VCE


        private const int S16_MIN = -32768;
        private const int S16_MAX = 32767;

        private const uint PC_ALIGN_MASK = 0xFFC;
        private const uint DATA_ALIGN_MASK = 0xFFF;

        public RspInterpreter() : base(new Disassembler("o32"))
        {
            m_VecRegs = new RspVector[32];
            m_VecPinnedMemory = new UnmanagedBuffer(m_VecRegs.Length * 16);

            for (int i = 0; i < m_VecRegs.Length; i++)
            {
                m_VecRegs[i] = new RspVector(m_VecPinnedMemory, i);
            }
        }

        public RspInterpreter(RspVector[] rspVecRegs) : base(new Disassembler("o32")) {
            m_VecRegs = rspVecRegs;
        }

        // [cer64plus] Fast IMEM fetch through a direct pointer. It checks itself against the memory-controller path for the
        // first 200k fetches and permanently falls back to the slow path on any mismatch.
        private IntPtr m_ImemPtr;
        private long m_FastFetchChecks;
        private bool m_FastFetchOff;

        protected override unsafe uint FetchInstruction(long address)
        {
            long a = address & PC_ALIGN_MASK;
            if (m_FastFetchOff) return IMEM.U32(a);
            if (m_ImemPtr == IntPtr.Zero) m_ImemPtr = new SPInterface.MemExports(Interface).IMEMPtr;
            uint fast = N64Endianess.U32(*(uint*)((byte*)m_ImemPtr + (a & 0xFFC)));
            if (m_FastFetchChecks < 200000) {
                m_FastFetchChecks++;
                uint slow = IMEM.U32(a);
                if (slow != fast) {
                    m_FastFetchOff = true;
                    Log.Warn("RSP fast IMEM fetch disagrees at {0:X3}: fast {1:X8} slow {2:X8}; using the slow path", a, fast, slow);
                    return slow;
                }
            }
            return fast;
        }

        public override void AttachMemory(N64MemoryController memory)
        {
            base.AttachMemory(memory);
            Cxd4StaticRsp.SetDMem(DMEM);
        }

        public override void AttachInterface(MipsInterface rcpInterface, SPInterface iface, DPCInterface rdpInterface)
        {
            base.AttachInterface(rcpInterface, iface, rdpInterface);

            iface.PCSet += (pc) => {
                PC = pc & PC_ALIGN_MASK;
                // Log.Debug("RSP PC Set: {0:X8}", PC);
            };

            Status.Change += () =>
            {

                if (Status.IsCmdEmpty)
                    return;

                if (EventLog.Enabled) EventLog.E("SP_STATUS_CMD", Status.Cmd.ToString("X") + " task=" + DMEM.U32(0xFC0) + " pc=" + PC.ToString("X"));

                if (Status.TestCmdFlags(StatusCmdFlags.ClearBroke))
                {
                    Status.StatusFlags &= ~StatusFlags.Broke;
                }

                if (Status.TestCmdFlags(StatusCmdFlags.ClearInterrupt)) {
                    RcpInterface.ClearInterrupt(MipsInterface.INT_SP);
                }
                else if (Status.TestCmdFlags(StatusCmdFlags.SetInterrupt)) {
                    RcpInterface.SetInterrupt(MipsInterface.INT_SP, true);
                }

                if (Status.TestCmdFlags(StatusCmdFlags.ClearInterruptOnBreak)) {
                    m_InterruptOnBroke = false;
                    Status.StatusFlags &= ~StatusFlags.InterruptOnBreak;
                }
                else if (Status.TestCmdFlags(StatusCmdFlags.SetInterruptOnBreak)) {
                    m_InterruptOnBroke = true;
                    Status.StatusFlags |= StatusFlags.InterruptOnBreak;
                }

                ProcessFlag(StatusCmdFlags.ClearSingleStep, StatusCmdFlags.SetSingleStep, StatusFlags.SingleStep);
                if (CompatLog.TraceTasks && Status.TestCmdFlags(StatusCmdFlags.SetSignal0)) System.Console.WriteLine($"SIG0 set steps={RspSteps} pc={PC:X3} halted={IsHalted}");
                ProcessFlag(StatusCmdFlags.ClearSignal0, StatusCmdFlags.SetSignal0, StatusFlags.Signal0Set);
                ProcessFlag(StatusCmdFlags.ClearSignal1, StatusCmdFlags.SetSignal1, StatusFlags.Signal1Set);
                ProcessFlag(StatusCmdFlags.ClearSignal2, StatusCmdFlags.SetSignal2, StatusFlags.Signal2Set);
                ProcessFlag(StatusCmdFlags.ClearSignal3, StatusCmdFlags.SetSignal3, StatusFlags.Signal3Set);
                ProcessFlag(StatusCmdFlags.ClearSignal4, StatusCmdFlags.SetSignal4, StatusFlags.Signal4Set);
                ProcessFlag(StatusCmdFlags.ClearSignal5, StatusCmdFlags.SetSignal5, StatusFlags.Signal5Set);
                ProcessFlag(StatusCmdFlags.ClearSignal6, StatusCmdFlags.SetSignal6, StatusFlags.Signal6Set);
                ProcessFlag(StatusCmdFlags.ClearSignal7, StatusCmdFlags.SetSignal7, StatusFlags.Signal7Set);

                //DebugUltraSignaling();

                if (Status.TestCmdFlags(StatusCmdFlags.ClearHalt))
                {
                    // Allow SP to start execution
                    ReportSpTaskType();

                    // [cer64plus] COR64_TRACE_ALIST=1: log every 50th audio command list (ABI opcode = w0 >> 24)
                    if (s_TraceAlist && IsAudioTask && (s_AlistCount++ % 50) == 0) {
                        uint ptr = DMEM.U32(0xFF0) & 0x7FFFF8, size = DMEM.U32(0xFF4);
                        var sb = new System.Text.StringBuilder();
                        for (uint o = 0; o < Math.Min(size, 0x800u); o += 8)
                            sb.Append(SysMemory.U32(ptr + o).ToString("X8")).Append(':').Append(SysMemory.U32(ptr + o + 4).ToString("X8")).Append(' ');
                        if (EventLog.Enabled) EventLog.E("ALIST", "n=" + s_AlistCount + " ptr=" + ptr.ToString("X") + " size=" + size + " " + sb);
                    }

                    // Start HLE graphics task if using HLE graphics
                    if (HLEGraphicsDevice != null && IsGraphicsTask) {
                        if (cor64.Debug.DebugHooks.Active) cor64.Debug.DebugHooks.RspTaskStart(this, true); // [dbg_exec] HLE gfx task
                        HLEGraphicsDevice.ExecuteGfxTask();
                        SetUltraTaskDone();
                        RspBreak();
                    }
                    #if SKIP_ULTRA_AUDIO
                    else if (IsAudioTask) {
                        // Audio task skip
                        RcpInterface.SetInterrupt(MipsInterface.INT_AI, true);
                        SetUltraTaskDone();
                        RspBreak();
                    }
                    #endif
                    else {
                        Status.StatusFlags &= ~StatusFlags.Broke;  // It seems this is expected to be cleared when unhalted
                        Status.StatusFlags &= ~StatusFlags.Halt;
                    }
                }
                else if (Status.TestCmdFlags(StatusCmdFlags.SetHalt))
                {
                    if (CompatLog.TraceTasks) System.Console.WriteLine($"TASKEND cpu-halt pc={PC:X3} type={DMEM.U32(0xFC0)} status={(uint)Status.StatusFlags:X}");
                    Status.StatusFlags |= StatusFlags.Halt;
                    StopRsp();
                }

                /* Clear out the written command flags */
                Status.ClearCmdFlags();

                /* Check RSP can be unhalted */
                CheckRspCanRun();
            };
        }

        private void SetUltraTaskDone() {
            Status.StatusFlags |= StatusFlags.Signal2Set;
        }

        private void DebugUltraSignaling() {
            if (Status.TestCmdFlags(StatusCmdFlags.ClearSignal0)) {
                Log.Debug("RSP SIG: Clear Yield");
            }
            else if (Status.TestCmdFlags(StatusCmdFlags.SetSignal0)) {
                Log.Debug("RSP SIG: Set Yield");
            }

            if (Status.TestStatusFlags(StatusFlags.Signal0Set)) {
                Log.Debug("RSP SIG: STATUS signals Yield");
            }

            if (Status.TestCmdFlags(StatusCmdFlags.ClearSignal1)) {
                Log.Debug("RSP SIG: Clear Yielded");
            }
            else if (Status.TestCmdFlags(StatusCmdFlags.SetSignal1)) {
                Log.Debug("RSP SIG: Set Yielded");
            }

            if (Status.TestStatusFlags(StatusFlags.Signal1Set)) {
                Log.Debug("RSP SIG: STATUS signals Yielded");
            }

            if (Status.TestCmdFlags(StatusCmdFlags.ClearSignal2)) {
                Log.Debug("RSP SIG: Clear TaskDone");
            }
            else if (Status.TestCmdFlags(StatusCmdFlags.SetSignal2)) {
                Log.Debug("RSP SIG: Set TaskDone");
            }

            if (Status.TestStatusFlags(StatusFlags.Signal2Set)) {
                Log.Debug("RSP SIG: STATUS signals TaskDone");
            }

            if (Status.TestCmdFlags(StatusCmdFlags.ClearSignal3)) {
                Log.Debug("RSP SIG: Clear RspSignal");
            }
            else if (Status.TestCmdFlags(StatusCmdFlags.SetSignal3)) {
                Log.Debug("RSP SIG: Set RspSignal");
            }

            if (Status.TestStatusFlags(StatusFlags.Signal3Set)) {
                Log.Debug("RSP SIG: STATUS signals RspSignal");
            }

            if (Status.TestCmdFlags(StatusCmdFlags.ClearSignal4)) {
                Log.Debug("RSP SIG: Clear CpuSignal");
            }
            else if (Status.TestCmdFlags(StatusCmdFlags.SetSignal4)) {
                Log.Debug("RSP SIG: Set CpuSignal");
            }

            if (Status.TestStatusFlags(StatusFlags.Signal4Set)) {
                Log.Debug("RSP SIG: STATUS signals CpuSignal");
            }
        }

        private void ProcessFlag(StatusCmdFlags c, StatusCmdFlags s, StatusFlags f) {
            // Clear
            if (Status.TestCmdFlags(c)) {
                Status.StatusFlags &= ~f;
            }
            // Set
            else if (Status.TestCmdFlags(s)) {
                Status.StatusFlags |= f;
            }
        }

        private void ReportSpTaskType() {
            var type = DMEM.U32(0xFC0);

            // PrintSpTaskType(type);

            m_SpTaskType = TaskType.Unknown;

            switch (type) {
                case 1: m_SpTaskType = TaskType.Graphics; break;
                case 2: m_SpTaskType = TaskType.Audio; break;
                case 3: m_SpTaskType = TaskType.Video; break;
                case 6: m_SpTaskType = TaskType.HVQ; break;
                case 7: m_SpTaskType = TaskType.HHVQM; break;
            }
        }

        private void PrintSpTaskType(uint type) {
            switch (type) {
                case 1: Log.Debug("Executing Graphics SP task"); break;
                case 2: Log.Debug("Executing Audio SP task"); break;
                case 3: Log.Debug("Executing Video SP task"); break;
                case 6: Log.Debug("Executing HVQ SP task"); break;
                case 7: Log.Debug("Executing HVQM SP task"); break;
                default: Log.Debug("Executing unknown SP task: {0}", type); break;
            }
        }

        public bool IsGraphicsTask => m_SpTaskType == TaskType.Graphics;

        public bool IsAudioTask => m_SpTaskType == TaskType.Audio;

        public override void Init()
        {
            // Important we do this
            Status.StatusFlags = StatusFlags.Halt;
        }

        public override void Step()
        {
            if (IsHalted) {
                return;
            }

            #if !OPTIMAL
            if (Debugger.IsBreakActive) {
                return;
            }
            #endif

            //m_HaltWait.WaitOne();

            /* Execute next instruction */
            ExecuteNextInst();
        }

        // TODO: Step with locking and step with polling

        public override void ManualStart(long pc)
        {
            Telemetry.RspStarts++;
            PC = pc & PC_ALIGN_MASK;
            IsHalted = false;
            m_HaltWait.Set();
        }

        private static bool s_dumped; private static bool s_watchOn = false; private static uint s_watchVal; private static int s_watchCount;
        private string FindWord(uint v) { var sb = new System.Text.StringBuilder(); for (uint a = 0; a < 0x1000; a += 4) if (DMEM.U32(a) == v) sb.Append(a.ToString("X") + ","); return sb.ToString(); }
        protected void ExecuteNextInst()
        {
            /* Delay Slot Logic */
            if (WillJump)
            {
                if (BranchDelay)
                {
                    if (!ExecuteInst())
                    {
                        throw new Exception("Failed to execute instruction in delay slot");
                    }

                    if (IsHalted) {
                        Interface.SetPC((uint)PC);
                        return;
                    }

                    BranchDelay = false;
                }

                TakeBranch = false;
                UnconditionalJump = false;

                PC = TargetAddress;
            }
            else
            {
                /* Nornal execution path */
                Telemetry.RspPcCount[(PC >> 2) & 0x3FF]++;
                if (s_TraceAlist && PC == 0x0E4 && IsAudioTask && (s_AlistCount % 50) == 1) AudioStageProbe();
                if (m_RecordResample) s_ResamplePcs.Add(PC);
                if (PC == 0x60 && s_NoCull) { uint kk = ReadGPR(27) & 0xFFF; if ((DMEM.U32(kk) >> 24) == 0xB7) DMEM.U32(kk + 4, DMEM.U32(kk + 4) & ~0x3000u); }
                if (PC == 0x60 && Telemetry.TraceUcode) { uint k1 = ReadGPR(27); if (EventLog.Enabled) EventLog.E("UCODE_FETCH", "k1=" + k1.ToString("X") + " w0=" + DMEM.U32(k1 & 0xFFF).ToString("X8") + " w1=" + DMEM.U32((k1 + 4) & 0xFFF).ToString("X8")); }
                if (PC == 0x3D4 && Telemetry.TraceUcode && !s_dumped && (DMEM.U32((ReadGPR(27) - 4) & 0xFFF) >> 24) == 7) { s_dumped = true; var sb = new System.Text.StringBuilder(); for (uint a = 0; a < 0x400; a += 16) { sb.Append(a.ToString("X3") + ":"); for (uint j = 0; j < 16; j += 4) sb.Append(" " + DMEM.U32(a + j).ToString("X8")); sb.Append('\n'); } System.IO.File.WriteAllText("telemetry/dmem_at_seg7.txt", sb.ToString()); }
                uint wPc = (uint)PC; 
                if (s_ITrace != null && PC == 0x60) { uint kk = ReadGPR(27) & 0xFFF; s_ITrace.WriteLine("CMD " + DMEM.U32(kk).ToString("X8") + " " + DMEM.U32((kk + 4) & 0xFFF).ToString("X8") + " k1=" + ReadGPR(27).ToString("X")); }
                if (ExecuteInst())
                {
                    if (s_ITrace != null) ITraceAfter(wPc, LastReadInst.Inst.inst);
                    if (s_watchOn) { uint nv = DMEM.U32(0x21C); if (nv != s_watchVal) { if (EventLog.Enabled) EventLog.E("SEGWATCH", "pc=" + wPc.ToString("X") + " 21C " + s_watchVal.ToString("X8") + "->" + nv.ToString("X8") + " inst=" + LastReadInst.Inst.inst.ToString("X8") + " " + (s_watchCount++)); s_watchVal = nv; if (s_watchCount > 40) s_watchOn = false; } }
                    PC += 4;
                    PC &= PC_ALIGN_MASK;

                    if (IsHalted) {
                        Interface.SetPC((uint)PC);
                    }
                }
                else
                {
                    throw new Exception(
                        String.Format("Failed to execute instruction (INV:{3}): 0x{0:X8} 0x{1:X8} {2}",
                            PC, 
                            m_FailedInstruction.Inst.inst,
                            DisassembleFailedInst(),
                            m_FailedInstruction.IsInvalid));
                }
            }
        }

        private string DisassembleFailedInst()
        {
            try
            {
                var dis = Disassembler.Disassemble(m_FailedInstruction);

                if (String.IsNullOrEmpty(dis))
                    return "?";

                return dis;
            }
            catch
            {
                return "?";
            }
        }

        public Action<DecodedInstruction> GetOpcodeHandler(DecodedInstruction inst) {
            return CallTable[inst];
        }

        private static readonly bool s_PcRing = System.Environment.GetEnvironmentVariable("COR64_RSP_PCRING") == "1";
        private static readonly uint[] s_Ring = new uint[256]; private static int s_RingI, s_RingDumps;
        private void DumpImemOnce() { var sb = new System.Text.StringBuilder(); for (int a = 0; a < 0x1000; a += 4) sb.Append(a.ToString("X3")).Append(' ').Append(FetchInstruction(a).ToString("X8")).Append((char)10); System.IO.File.WriteAllText("imem_dump_" + s_RingDumps + ".txt", sb.ToString()); var sd = new System.Text.StringBuilder(); for (uint a = 0; a < 0x1000; a += 4) sd.Append(a.ToString("X3")).Append(' ').Append(DMEM.U32(a).ToString("X8")).Append((char)10); System.IO.File.WriteAllText("dmem_dump_" + s_RingDumps + ".txt", sd.ToString()); }

        private bool ExecuteInst()
        {
            DecodedInstruction decoded;

            m_LastFetchAddress = PC;
            if (s_PcRing) { s_Ring[s_RingI++ & 255] = (uint)PC; }

            if (m_InjectedInst.HasValue)
            {
                decoded = m_InjectedInst.Value;
                m_InjectedInst = null;
            }
            else
            {
                decoded = Decode(PC);
            }

            LastReadInst = decoded;

            if (!decoded.IsInvalid)
            {
                var call = CallTable[decoded];

                // XXX: This is used to hijack the opcode handler for debugging reasons
                if (CallHack != null) {
                    call = CallHack(decoded);
                }

                if (call == null)
                {
                    throw new NotSupportedException(String.Format("Opcode {0} not supported", decoded.Op.Op));
                }
                else
                {
                    CurrentInst = decoded;

                    // Break is executed reguardless of being nullified
                    if (!NullifyNext)
                    {
                        #if !OPTIMAL
                        Debugger.CheckInstructionBreakpoints(CurrentInst);

                        TraceInstruction(m_LastFetchAddress, decoded);

                        #endif

                        call(decoded);

                        DebugInstruction(decoded);

                    }
                    else
                    {
                        NullifyNext = false;
                    }

                    return true;
                }
            }
            else
            {
                // [cer64plus] The RSP has no reserved-instruction exception: an invalid encoding executes as a no-op
                // (ares rsp/interpreter.cpp INVALID(), n64-systemtest). Wave Race 64's gfx ucode reaches one.
                m_FailedInstruction = decoded;
                if (s_PcRing && s_RingDumps++ < 3) { var sb = new System.Text.StringBuilder("RSPRING"); for (int i = 0; i < 256; i++) sb.Append(' ').Append(s_Ring[(s_RingI + i) & 255].ToString("X3")); System.Console.WriteLine(sb.ToString()); DumpImemOnce(); }
                if (CompatLog.On) CompatLog.Note("rsp:invalid-op", "pc=" + PC.ToString("X3") + " inst=" + decoded.Inst.inst.ToString("X8"));
                if (NullifyNext) NullifyNext = false;
                return true;
            }
        }

        [Conditional("DEBUG"), Conditional("TESTING")]
        private void DebugInstruction(DecodedInstruction instruction)
        {
            if (core_InstDebugMode != InstructionDebugMode.None) {
                string memNote = null;

                if (core_MemAccessNote != null) {
                    memNote = "     # " + core_MemAccessNote.ReadMeta();
                    core_MemAccessNote = null;
                }

                // if (memNote != null)
                Console.WriteLine("RSP {0:X8} {1} {2}",
                    PC,
                    Disassembler.Disassemble(instruction),
                    memNote ?? ""
                    );
            }
        }

        // private void SetHaltMode(bool halted)
        // {
        //     // XXX: The RSP starts executiong if unhalted, unbroken

        //     if (halted)
        //     {
                
        //         //m_HaltWait.Reset();
        //         // m_CpuRspWait.Set();
        //     }
        //     else
        //     {
        //         Status.StatusFlags &= ~StatusFlags.Halt;
        //         //m_HaltWait.Set();
        //         // CpuWaitForFinish();
        //     }
        // }

        private void CheckRspCanRun() {
            if (IsHalted) {
                if ((Status.StatusFlags & StatusFlags.Halt) == StatusFlags.Halt) {
                    return;
                }

                if ((Status.StatusFlags & StatusFlags.Broke) == StatusFlags.Broke) {
                    return;
                }

                Telemetry.RspStarts++;
                if (cor64.Debug.DebugHooks.Active) cor64.Debug.DebugHooks.RspTaskStart(this, false); // [dbg_exec] RSP task log
                if (CompatLog.TraceTasks) System.Console.WriteLine($"TASKSTART steps={RspSteps} pc={PC:X3} flags={DMEM.U32(0xFC4):X} status={(uint)Status.StatusFlags:X}");
                if (CompatLog.On) CompatLog.RspTask(DMEM.U32(0xFC0), DMEM.U32(0xFD0), DMEM.U32(0xFD4), DMEM.U32(0xFD8), DMEM.U32(0xFDC));
                if (Telemetry.DumpGfxTask > 0) DumpGfxTask();
                if (s_ITraceTask > 0 && DMEM.U32(0xFC0) == 1) { s_ITraceGfx++; if (s_ITraceGfx >= s_ITraceTask && s_ITraceGfx < s_ITraceTask + s_ITraceN) { if (s_ITrace != null) { s_ITrace.WriteLine("TASKSTART " + s_ITraceGfx); goto trDone; } System.IO.Directory.CreateDirectory("telemetry"); s_ITrace = new System.IO.StreamWriter("telemetry/rsp_itrace.txt"); var d = new System.Text.StringBuilder(); for (long a = 0; a < 0x1000; a += 4) d.Append(DMEM.U32(a).ToString("X8")).Append((char)10); System.IO.File.WriteAllText("telemetry/itrace_dmem0.txt", d.ToString()); var im = new System.Text.StringBuilder(); for (int a = 0; a < 0x1000; a += 4) { uint w = FetchInstruction(a); string dd; try { dd = Disassembler.Disassemble(Disassembler.Decode(w)); } catch { dd = "?"; } im.Append(a.ToString("X3")).Append(' ').Append(w.ToString("X8")).Append(' ').Append(dd).Append((char)10); } System.IO.File.WriteAllText("telemetry/itrace_imem0.txt", im.ToString()); trDone:; } else if (s_ITrace != null) { s_ITrace.Close(); s_ITrace = null; s_ITraceTask = 0; } }
                IsHalted = false;
                m_HaltWait.Set();
            }
        }

        // [cer64plus] Debug aid (COR64_DUMP_GFX=<n>): at the start of the n-th next graphics task, write RDRAM and DMEM to telemetry/.
        private unsafe void DumpGfxTask() {
            byte* rp = Telemetry.RdramPtr != null ? Telemetry.RdramPtr : CompatLog.RdramPtr; // [ingame_fix3] debug aid: Telemetry.RdramPtr is never set
            if (DMEM.U32(0xFC0) != 1 || rp == null) return;
            if (--Telemetry.DumpGfxTask != 0) return;
            var buf = new byte[0x800000];
            System.Runtime.InteropServices.Marshal.Copy((System.IntPtr)rp, buf, 0, buf.Length);
            System.IO.Directory.CreateDirectory("telemetry");
            System.IO.File.WriteAllBytes("telemetry/gfx_rdram.bin", buf);
            var d = new System.Text.StringBuilder();
            for (long a = 0; a < 0x1000; a += 4) d.Append(DMEM.U32(a).ToString("X8")).Append('\n');
            System.IO.File.WriteAllText("telemetry/gfx_dmem.txt", d.ToString());
        }

        // [cer64plus] Debug aid (COR64_RSP_ITRACE=<n>): interpreter trace of the n-th graphics task (needs RSP_REC=0 RSP_JIT=0).
        private static int s_ITraceTask = int.TryParse(System.Environment.GetEnvironmentVariable("COR64_RSP_ITRACE"), out var it) ? it : 0;
        private static int s_ITraceGfx; private static int s_ITraceN = int.TryParse(System.Environment.GetEnvironmentVariable("COR64_RSP_ITRACE_N"), out var itn) ? itn : 1; private static System.IO.StreamWriter s_ITrace;
        private readonly uint[] m_TrG = new uint[32]; private readonly short[] m_TrV = new short[256]; private ushort m_TrFl; private byte m_TrVce;
        private unsafe void ITraceAfter(uint pc, uint inst) {
            var sb = new System.Text.StringBuilder(); sb.Append(pc.ToString("X3")).Append(' ').Append(inst.ToString("X8"));
            for (int r = 1; r < 32; r++) { uint v = ReadGPR(r); if (v != m_TrG[r]) { m_TrG[r] = v; sb.Append(" r").Append(r).Append('=').Append(v.ToString("X")); } }
            for (int r = 0; r < 32; r++) { short* l = m_VecRegs[r].RawLanes; bool ch = false; for (int e = 0; e < 8; e++) if (l[e] != m_TrV[r * 8 + e]) ch = true; if (ch) { sb.Append(" v").Append(r).Append('='); for (int e = 0; e < 8; e++) { short x = m_VecRegs[r].PackedS16(e); for (int k = 0; k < 8; k++) m_TrV[r * 8 + k] = l[k]; sb.Append(((ushort)x).ToString("X4")).Append(e < 7 ? "," : ""); } } }
            uint vco = ReadVCO(), vcc = ReadVCC(); byte vce = ReadVCE(); uint key = vco | (vcc << 16); if (key != m_TrKey || vce != m_TrVce) { m_TrKey = key; m_TrVce = vce; sb.Append(" vco=").Append(vco.ToString("X4")).Append(" vcc=").Append(vcc.ToString("X4")).Append(" vce=").Append(vce.ToString("X2")); }
            s_ITrace.WriteLine(sb.ToString());
        }
        private uint m_TrKey;

        private static readonly bool s_NoCull = System.Environment.GetEnvironmentVariable("COR64_EXP_NOCULL") == "1";

        private void StopRsp() {
            IsHalted = true;
            m_HaltWait.Reset();
        }

        public void CpuWaitForFinish() {
            if (!IsHalted) {
                m_CpuRspWait.Reset();
                m_CpuRspWait.WaitOne();
            }
        }

        public override ExecutionState State => m_State;

        public override void AttachBootManager(BootManager bootManager)
        {
            throw new NotSupportedException();
        }

        public override ushort ReadVCC() {
            return (ushort)((m_VccClip.Value << 8) | M_VccCompare.Value);
        }

        public override ushort ReadVCO() {
            return m_VCarry.RegValue;
        }

        public override void WriteVC0(ushort value) {
            m_VCarry.RegValue = value;
        }

        public override void WriteVCC(ushort vcc) {
            M_VccCompare.Value = (byte)vcc;
            m_VccClip.Value = (byte)(vcc >> 8);
        }

        public override byte ReadVCE() {
            return m_Vce.Value;
        }

        public void WriteVCE(byte value) {
            m_Vce.Value = value;
        }

        private static ushort ClampS16(int value) {
            return (ushort)Math.Clamp(value, S16_MIN, S16_MAX);
        }

        private static ushort Merge(bool compare, short pass, short fail) {
            if (compare) {
                return (ushort)pass;
            }
            else {
                return (ushort)fail;
            }
        }

        private static bool Merge(bool compare, bool pass, bool fail) {
            if (compare) {
                return pass;
            }
            else {
                return fail;
            }
        }

        /* ----------------------------------------------------------------
         * Mips Instruction Core
         * ----------------------------------------------------------------
         */

        public override void Add(DecodedInstruction inst)
        {
            bool isImmediate = inst.IsImmediate();
            uint operandA = ReadGPR(inst.Source);
            uint operandB = isImmediate ? (uint)(short)inst.Immediate : ReadGPR(inst.Target);
            uint result;
            int dest = isImmediate ? inst.Target : inst.Destination;

            // RSP does not trap any exceptions
            // checking for overflow is useless

            unchecked
            {
                result = operandA + operandB;
            }

            Writeback(dest, result);
        }

        public override void BitwiseLogic(DecodedInstruction inst)
        {
            bool isImmediate = inst.IsImmediate();
            uint a = ReadGPR(inst.Source);
            uint b = isImmediate ? inst.Immediate : ReadGPR(inst.Target);

            uint result = inst.Op.ArithmeticType switch
            {
                ArithmeticOp.AND => a & b,
                ArithmeticOp.OR => a | b,
                ArithmeticOp.XOR => a ^ b,
                ArithmeticOp.NOR => ~(a | b),
                _ => throw new InvalidOperationException("Bitwise logic"),
            };

            Writeback(isImmediate ? inst.Target : inst.Destination, result);
        }

        public override void Branch(DecodedInstruction inst)
        {
            bool isLink = inst.IsLink();
            uint source = ReadGPR(inst.Source);
            uint target = ReadGPR(inst.Target);

            TargetAddress = CoreUtils.ComputeBranchPC(false, PC, CoreUtils.ComputeBranchTargetOffset(inst.Immediate));
            TargetAddress &= PC_ALIGN_MASK;
            TakeBranch = ComputeBranchCondition(false, source, target, inst.Op.XferTarget, inst.Op.ArithmeticType);

            if (isLink)
            {
                Writeback(31, (uint)(PC + 8) & PC_ALIGN_MASK);
            }

            // Always true for RSP
            BranchDelay = true;

            // if (!TakeBranch) {
            //     TargetAddress = 0;
            // }
        }

        public override void Jump(DecodedInstruction inst)
        {
            bool isLink = inst.IsLink();
            bool isRegister = inst.IsRegister();
            BranchDelay = true;
            UnconditionalJump = true;

            TargetAddress = CoreUtils.ComputeTargetPC(isRegister, PC, ReadGPR(inst.Source), inst.Inst.target);
            TargetAddress &= PC_ALIGN_MASK;

            if (isLink) {
                if (isRegister && inst.Destination != 0) {
                    Writeback(inst.Destination, (uint)(PC + 8) & PC_ALIGN_MASK);
                }
                else {
                    Writeback(31, (uint)(PC + 8) & PC_ALIGN_MASK);
                }
            }
        }

        // RSP scalar loads/stores may be unaligned and wrap within the 4K bank: go bytewise when unaligned.
        // [cer64plus] Direct DMEM access (host words hold the big-endian words: byte a is at a^3, halfword at a^2); unaligned
        // accesses go bytewise and wrap within the 4K bank. Was a full memory-controller decode per access.
        private static readonly bool s_FastDmem = Environment.GetEnvironmentVariable("COR64_RSP_FAST_DMEM") != "0";
        private unsafe byte DmU8(long a) => s_FastDmem ? DmemBytes[(a & 0xFFF) ^ 3] : DMEM.U8(a & 0xFFF);
        private unsafe void DmU8(long a, byte v) { if (s_FastDmem) DmemBytes[(a & 0xFFF) ^ 3] = v; else DMEM.U8(a & 0xFFF, v); }
        private unsafe ushort RdU16(long a) { if (!s_FastDmem && (a & 1) == 0) return DMEM.U16(a); if ((a & 1) == 0) return *(ushort*)(DmemBytes + ((a & 0xFFF) ^ 2)); return (ushort)((DmU8(a) << 8) | DmU8(a + 1)); }
        private unsafe uint RdU32(long a) { if (!s_FastDmem && (a & 3) == 0) return DMEM.U32(a); if ((a & 3) == 0) return *(uint*)(DmemBytes + (a & 0xFFF)); uint v = 0; for (int i = 0; i < 4; i++) v = (v << 8) | DmU8(a + i); return v; }
        private unsafe void WrU16(long a, ushort v) { if (!s_FastDmem && (a & 1) == 0) { DMEM.U16(a, v); return; } if ((a & 1) == 0) { *(ushort*)(DmemBytes + ((a & 0xFFF) ^ 2)) = v; return; } DmU8(a, (byte)(v >> 8)); DmU8(a + 1, (byte)v); }
        private unsafe void WrU32(long a, uint v) { if (!s_FastDmem && (a & 3) == 0) { DMEM.U32(a, v); return; } if ((a & 3) == 0) { *(uint*)(DmemBytes + (a & 0xFFF)) = v; return; } for (int i = 0; i < 4; i++) DmU8(a + i, (byte)(v >> (24 - 8 * i))); }

        public override void Load(DecodedInstruction inst)
        {
            long address;
            int size;

            bool upperImm = inst.IsImmediate();
            bool unsigned = inst.IsUnsigned();

            if (upperImm)
            {
                uint imm = inst.Immediate;
                imm <<= 16;

                Writeback(inst.Target, imm);
            }
            else
            {
                size = inst.DataSize();

                long baseAddress = (long)ReadGPR(inst.Source);
                long offset = (short)inst.Immediate;
                address = baseAddress + offset;
                address &= DATA_ALIGN_MASK;

                if (unsigned)
                {
                    switch (size)
                    {
                        default: throw new InvalidOperationException("How did this happen (unsigned)?");
                        case 1: Writeback(inst.Target, DmU8(address)); break;
                        case 2: Writeback(inst.Target, RdU16(address)); break;
                        case 4: Writeback(inst.Target, RdU32(address)); break;
                    }
                }
                else
                {
                    switch (size)
                    {
                        default: throw new InvalidOperationException("How did this happen?");
                        case 1: Writeback(inst.Target, (uint)(sbyte)DmU8(address)); break;
                        case 2: Writeback(inst.Target, (uint)(short)RdU16(address)); break;
                        case 4: Writeback(inst.Target, (uint)(int)RdU32(address)); break;
                    }
                }
            }
        }

        public override void SetOnLessThan(DecodedInstruction inst)
        {
            bool isImmediate = inst.IsImmediate();
            int dest = isImmediate ? inst.Target : inst.Destination;
            byte result = 0;

            uint operandA = ReadGPR(inst.Source);
            uint operandB = isImmediate ? (uint)(short)inst.Immediate : ReadGPR(inst.Target);

            if (inst.IsUnsigned()) {
                if (operandA < operandB)
                {
                    result = 1;
                }
            }
            else {
                if ((int)operandA < (int)operandB) {
                    result = 1;
                }
            }



            Writeback(dest, result);
        }

        public override void Shift(DecodedInstruction inst)
        {
            int shiftAmount;

            if (inst.IsVariableShift())
            {
                shiftAmount = (int)(ReadGPR(inst.Source) & 0x1F);
            }
            else
            {
                shiftAmount = inst.ShiftAmount;
            }

            uint value = ReadGPR(inst.Target);

            if (inst.Op.ArithmeticType == ArithmeticOp.LSHIFT)
            {
                value <<= shiftAmount;
            }
            else
            {
                if (inst.IsUnsigned()) {
                    value >>= shiftAmount;
                }
                else {
                    unchecked {
                        value = (uint)((int)value >> shiftAmount);
                    }
                }
            }

            Writeback(inst.Destination, value);
        }

        public override void Store(DecodedInstruction inst)
        {
            long address;
            int size = inst.DataSize();
            int baseAddress = (int)ReadGPR(inst.Source);
            int offset = (short)inst.Immediate;
            address = baseAddress + offset;
            address &= DATA_ALIGN_MASK;

            switch (size)
            {
                default: throw new InvalidOperationException("How did this happen?");
                case 1: DmU8(address, (byte)ReadGPR(inst.Target)); break;
                case 2: WrU16(address, (ushort)ReadGPR(inst.Target)); break;
                case 4: WrU32(address, (uint)ReadGPR(inst.Target)); break;
            }
        }

        public override void Subtract(DecodedInstruction inst)
        {
            uint operandA = ReadGPR(inst.Source);
            uint operandB = ReadGPR(inst.Target);
            uint result;

            // RSP does not trap on any exceptions
            // no need to check for underflows

            unchecked
            {
                result = operandA - operandB;
            }

            Writeback(inst.Destination, result);
        }

        public override void TransferReg(DecodedInstruction inst)
        {
            uint value = 0;

            RegTransferGprHelper(inst, out int regSource, out int regDest);

            /* Source value to copy */
            switch (inst.Op.XferSource)
            {
                case RegBoundType.Gpr:
                    {
                        value = ReadGPR(regSource); break;
                    }

                case RegBoundType.Cp0:
                    {
                        if (regSource >= 0 && regSource < 8)
                    	{
                            /* Read from SP Interface Regs */

                            // Log.Debug("SP Interface -> RSP: {0} {1:X8}", regSource, value);
                            value = Interface.ReadRegForRsp(regSource);
                    	}
                    	else if (regSource >= 8 && regSource < 16)
                    	{
                            var select = regSource - 8;

                            //Log.Debug("RDP -> RSP Command: {0} {1:X8}", select, value);
                            value = RdpInterface.ReadRegForRsp(select);
                    	}
                    	else
                    	{
                    		Log.Error("Unknown RSP COP0 Read: " + regSource.ToString()); if (CompatLog.On) CompatLog.Note("rsp:cop0-read-unknown", "reg=" + regSource);
                    	}

                        break;
                    }

                case RegBoundType.Cp2:
                    {
                        int e = inst.VDest >> 1; // [cer64plus] element is bits 7..10 (sa >> 1), as in ares MTC2/MFC2

                        byte b1 = m_VecRegs[inst.Destination].PackedU8((e + 1) & 0xF);
                        byte b2 = m_VecRegs[inst.Destination].PackedU8((e + 0) & 0xF);

                        ushort v = (ushort)( ((ushort)b2 << 8) | b1 );

                        value = (uint)(short)v;

                        break;
                    }

                case RegBoundType.Cp2Ctl:
                    {
                        switch (inst.Destination & 3) {
                            case 0: {
                                value = ReadVCO();
                                break;
                            }

                            case 1: {
                                value = ReadVCC();
                                break;
                            }

                            case 2:
                            case 3: {
                                value = ReadVCE();
                                break;
                            }

                            default: Console.WriteLine("FIXME: Read a vector control reg into GPR: " + inst.VSource.ToString()); break;
                        }

                        break;
                    }

                default: throw new NotSupportedException();
            }

            /* Target */
            switch (inst.Op.XferTarget)
            {
                case RegBoundType.Gpr:
                    {
                        Writeback(regDest, value);
                        break;
                    }

                case RegBoundType.Cp0:
                    {
                        m_SkipForcedWait = true;
                        if (s_C0Hist) { C0Hist[regDest & 15]++; C0PcHist[((int)PC & 0xFFF) >> 2]++; }

                        if (regDest >= 0 && regDest < 8)
                    	{
                    		/* Write to SP Interface Regs */

                            // Log.Debug("RSP -> SP Interface: {0} {1:X8}", regDest, value);
                            long tq = cor64.N64System.s_PhaseTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                            Interface.RegWriteFromRsp(regDest, value);
                            if (cor64.N64System.s_PhaseTiming) cor64.N64System.s_TRspSp += System.Diagnostics.Stopwatch.GetTimestamp() - tq;
                    	}
                    	else if (regDest >= 8 && regDest < 16)
                    	{
                            /* Write to RDP Command Regs */

                            var select = regDest - 8;

                            // Log.Debug("RSP -> RDP Command: {0} {1:X8}", ABI.GetLabel("o32", ABI.RegType.SpCop0, regDest), value);
                            long tq = cor64.N64System.s_PhaseTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                            RdpInterface.RegWriteFromRsp(select, value);
                            if (cor64.N64System.s_PhaseTiming) cor64.N64System.s_TRspDp += System.Diagnostics.Stopwatch.GetTimestamp() - tq;
                    	}
                    	else
                    	{
                    		Log.Error("Unknown RSP COP0 Write: " + regDest.ToString()); if (CompatLog.On) CompatLog.Note("rsp:cop0-write-unknown", "reg=" + regDest);
                    	}

                        break;
                    }

                case RegBoundType.Cp2:
                    {
                        ushort write = (ushort)value;
                        int e = inst.VDest >> 1; // [cer64plus] element is bits 7..10 (sa >> 1), as in ares MTC2/MFC2

                        byte b1 = (byte)write;
                        byte b2 = (byte)(write >> 8);

                        m_VecRegs[inst.Destination].PackedU8((e + 1) & 0xF, b1);
                        m_VecRegs[inst.Destination].PackedU8((e + 0) & 0xF, b2);

                        break;
                    }

                case RegBoundType.Cp2Ctl:
                    {
                        switch (inst.Destination & 3) {
                            case 0: {
                                m_VCarry.RegValue = (ushort)value;
                                break;
                            }

                            case 1: {
                                WriteVCC((ushort)value);
                                break;
                            }

                            case 2:
                            case 3: {
                                m_Vce.Value = (byte)value;
                                break;
                            }

                            default: Console.WriteLine("FIXME: Write GPR into vector ctrl: " + inst.VSource.ToString()); break;
                        }

                        break;
                    }

                default: throw new NotSupportedException();
            }
        }

        public override void Break(DecodedInstruction inst)
        {
            if (BranchDelay && TakeBranch) {
                PC = BranchTarget;
                PC &= PC_ALIGN_MASK;
            }

            RspBreak();
        }

        // [cer64plus] Audio stage probe (COR64_TRACE_ALIST=1): at each ABI1 command fetch (IMEM 0x0E4), log the peak sample the
        // previous command left in its output buffer. Buffer bookkeeping follows the ABI1 command layout (DMEM base 0x5C0).
        private int m_AIn, m_AOut, m_ACount, m_ADryR, m_AWetL, m_AWetR;
        private uint m_APrevW0, m_APrevW1;
        private static readonly string[] s_AbiNames = { "SPNOOP","ADPCM","CLEARBUFF","ENVMIXER","LOADBUFF","RESAMPLE","SAVEBUFF","SEGMENT","SETBUFF","SETVOL","DMEMMOVE","LOADADPCM","MIXER","INTERLEAVE","POLEF","SETLOOP" };

        private int DmemPeak(int start, int count) {
            int peak = 0;
            for (int o = 0; o + 1 < count && o < 0x1000; o += 2) peak = Math.Max(peak, Math.Abs((int)(short)RdU16((start + o) & 0xFFF)));
            return peak;
        }

        private void AudioStageProbe() {
            uint sp = ReadGPR(29);
            uint w0 = DMEM.U32(sp & 0xFFF), w1 = DMEM.U32((sp + 4) & 0xFFF);
            // report on the previous command
            int op = (int)(m_APrevW0 >> 24) & 0xF;
            string detail = null;
            switch (op) {
                case 1: case 5: detail = "out=" + m_AOut.ToString("X") + " peak=" + DmemPeak(m_AOut, m_ACount) + " in_peak=" + DmemPeak(m_AIn, m_ACount); break;
                case 3: detail = "dryL_peak=" + DmemPeak(m_AOut, m_ACount) + " dryR_peak=" + DmemPeak(m_ADryR, m_ACount) + " in_peak=" + DmemPeak(m_AIn, m_ACount); break;
                case 4: detail = "in=" + m_AIn.ToString("X") + " peak=" + DmemPeak(m_AIn, m_ACount); break;
                case 12: { int o = (int)(m_APrevW1 & 0xFFFF) + 0x5C0, i = (int)(m_APrevW1 >> 16) + 0x5C0; detail = "out_peak=" + DmemPeak(o, m_ACount) + " in_peak=" + DmemPeak(i, m_ACount); break; }
                case 13: detail = "out_peak=" + DmemPeak(m_AOut, m_ACount * 2); break;
            }
            if (detail != null) if (EventLog.Enabled) EventLog.E("ACMD", s_AbiNames[op] + " " + m_APrevW0.ToString("X8") + ":" + m_APrevW1.ToString("X8") + " count=" + m_ACount + " " + detail);
            if (s_ResamplePcs != null && op == 5 && !s_ResampleDumped && s_ResamplePcs.Count > 0) {
                s_ResampleDumped = true;
                var sb = new System.Text.StringBuilder();
                foreach (var pc in s_ResamplePcs) { uint w = IMEM.U32(pc); string d; try { d = Disassembler.Disassemble(Disassembler.Decode(w)); } catch { d = "?"; } sb.Append(pc.ToString("X3")).Append(": ").Append(w.ToString("X8")).Append("  ").Append(d).Append('\n'); }
                System.IO.File.WriteAllText("telemetry/resample_pcs.txt", sb.ToString());
            }
            m_RecordResample = ((w0 >> 24) & 0xF) == 5 && !s_ResampleDumped;
            if (m_RecordResample) s_ResamplePcs.Clear();
            // track SETBUFF for the command about to run
            if ((w0 >> 24) == 8) {
                if (((w0 >> 16) & 8) != 0) { m_ADryR = (int)(w0 & 0xFFFF) + 0x5C0; m_AWetL = (int)(w1 >> 16) + 0x5C0; m_AWetR = (int)(w1 & 0xFFFF) + 0x5C0; }
                else { m_AIn = (int)(w0 & 0xFFFF) + 0x5C0; m_AOut = (int)(w1 >> 16) + 0x5C0; m_ACount = (int)(w1 & 0xFFFF); }
            }
            m_APrevW0 = w0; m_APrevW1 = w1;
        }

        private static readonly System.Collections.Generic.SortedSet<long> s_ResamplePcs = new();
        private static bool s_ResampleDumped;
        private bool m_RecordResample;

        private static bool s_AudioImemDumped;
        private static readonly bool s_OldVls = System.Environment.GetEnvironmentVariable("COR64_OLD_VLS") == "1";
        private static readonly bool s_TraceAlist = System.Environment.GetEnvironmentVariable("COR64_TRACE_ALIST") == "1";
        private static long s_AlistCount;

        private void RspBreak() {
            /* Break is used to halt the RSP processor */
            if (EventLog.Enabled) EventLog.E("RSP_BREAK", "pc=" + PC.ToString("X") + " intOnBreak=" + m_InterruptOnBroke);
            // [cer64plus] one-time disassembly of the audio microcode (task type 2) for reference comparison
            if (!s_AudioImemDumped && DMEM.U32(0xFC0) == 2 && System.IO.Directory.Exists("telemetry")) {
                s_AudioImemDumped = true;
                var sb = new System.Text.StringBuilder();
                for (long a = 0; a < 0x1000; a += 4) {
                    uint w = IMEM.U32(a);
                    string d; try { d = Disassembler.Disassemble(Disassembler.Decode(w)); } catch { d = "?"; }
                    sb.Append(a.ToString("X3")).Append(": ").Append(w.ToString("X8")).Append("  ").Append(d).Append('\n');
                }
                System.IO.File.WriteAllText("telemetry/audio_imem.txt", sb.ToString());
            }
            if (CompatLog.TraceTasks) System.Console.WriteLine($"TASKEND break steps={RspSteps} pc={PC:X3} type={DMEM.U32(0xFC0)} flags={DMEM.U32(0xFC4):X} status={(uint)Status.StatusFlags:X}");
            Status.StatusFlags |= StatusFlags.Broke;
            Status.StatusFlags |= StatusFlags.Halt;

            StopRsp();

            if (m_InterruptOnBroke) {
                RcpInterface.SetInterrupt(MipsInterface.INT_SP, true);
                // Log.Debug("RSP Interrupt has been set on break");
            }
        }

       /* ----------------------------------------------------------------
        * Mips Vector Unit Instruction Core
        * ----------------------------------------------------------------
        */

        public override void VectorUnitReserved(DecodedInstruction inst)
        {
            throw new MipsException(ExceptionType.Reserved);
        }


        // [cer64plus] Byte-exact vector loads/stores ported from ares (n64/rsp/interpreter-vpu.cpp).
        // DMEM byte access through a direct pointer: DMEM is host little-endian 32-bit words, so big-endian byte a is host byte a ^ 3.
        private IntPtr m_DmemPtr;
        private unsafe byte* DmemBytes { get { if (m_DmemPtr == IntPtr.Zero) m_DmemPtr = new SPInterface.MemExports(Interface).DMEMPtr; return (byte*)m_DmemPtr; } }
        private unsafe byte DB(long a) => DmemBytes[(a & 0xFFF) ^ 3];
        private unsafe void DB(long a, byte v) => DmemBytes[(a & 0xFFF) ^ 3] = v;
        private static byte VB(RspVector v, int i) { ushort e = v.PackedU16((i >> 1) & 7); return (i & 1) == 0 ? (byte)(e >> 8) : (byte)e; }
        private static void VB(RspVector v, int i, byte b) { int n = (i >> 1) & 7; ushort e = v.PackedU16(n); e = (i & 1) == 0 ? (ushort)((e & 0x00FF) | (b << 8)) : (ushort)((e & 0xFF00) | b); v.PackedU16(n, e); }

        // [cer64plus] Fast vector loads/stores (default; COR64_RSP_SIMD=0 off): same loops as TryVectorLoadStore, opcode looked up by ID (no string
        // switch), bytes through pointers. Plain register: big-endian vector byte i is raw lane byte 15 - i; DMEM byte a
        // is DmemBytes[(a & 0xFFF) ^ 3].
        private static readonly bool s_VlsFast = Environment.GetEnvironmentVariable("COR64_RSP_SIMD") != "0";
        private static readonly byte[] s_VlsKind = BuildVlsKinds();
        private static byte[] BuildVlsKinds()
        {
            var k = new byte[1024];
            Opcode[] ops = { RspOpcodes.LBV, RspOpcodes.LSV, RspOpcodes.LLV, RspOpcodes.LDV, RspOpcodes.LQV, RspOpcodes.LRV, RspOpcodes.LPV, RspOpcodes.LUV, RspOpcodes.LHV,
                             RspOpcodes.SBV, RspOpcodes.SSV, RspOpcodes.SLV, RspOpcodes.SDV, RspOpcodes.SQV, RspOpcodes.SRV, RspOpcodes.SPV, RspOpcodes.SUV, RspOpcodes.SHV };
            for (int i = 0; i < ops.Length; i++) k[ops[i].ID] = (byte)(i + 1);
            return k;
        }

        private unsafe bool TryVectorLoadStoreFast(DecodedInstruction inst, bool load)
        {
            int kind = inst.Op.ID < s_VlsKind.Length ? s_VlsKind[inst.Op.ID] : 0;
            if (kind == 0) return s_LfvSfv && TryVectorLoadStore(inst, load); // [emu_fix5] lfv/sfv
            var vt = m_VecRegs[inst.Target];
            if (!vt.IsPlain) return TryVectorLoadStore(inst, load);
            uint raw = inst.Inst.inst;
            int imm = ((int)(raw << 25)) >> 25;
            int e = (int)((raw >> 7) & 0xF);
            long rs = ReadGPR(inst.Source);
            byte* V = (byte*)vt.RawLanes;
            byte* M = DmemBytes;
            long address;
            switch (kind) {
                case 1: V[15 - (e & 15)] = M[((rs + imm) & 0xFFF) ^ 3]; return true;
                case 2: address = rs + imm * 2; for (int o = e; o < Math.Min(e + 2, 16); o++) V[15 - (o & 15)] = M[(address++ & 0xFFF) ^ 3]; return true;
                case 3: address = rs + imm * 4; for (int o = e; o < Math.Min(e + 4, 16); o++) V[15 - (o & 15)] = M[(address++ & 0xFFF) ^ 3]; return true;
                case 4: address = rs + imm * 8; for (int o = e; o < Math.Min(e + 8, 16); o++) V[15 - (o & 15)] = M[(address++ & 0xFFF) ^ 3]; return true;
                case 5: { address = rs + imm * 16; int end = Math.Min(16 + e - (int)(address & 15), 16); for (int o = e; o < end; o++) V[15 - (o & 15)] = M[(address++ & 0xFFF) ^ 3]; return true; }
                case 6: { address = rs + imm * 16; int start = 16 - ((int)(address & 15) - e); address &= ~15L; for (int o = start; o < 16; o++) V[15 - (o & 15)] = M[(address++ & 0xFFF) ^ 3]; return true; }
                case 10: M[((rs + imm) & 0xFFF) ^ 3] = V[15 - (e & 15)]; return true;
                case 11: address = rs + imm * 2; for (int o = e; o < e + 2; o++) M[(address++ & 0xFFF) ^ 3] = V[15 - (o & 15)]; return true;
                case 12: address = rs + imm * 4; for (int o = e; o < e + 4; o++) M[(address++ & 0xFFF) ^ 3] = V[15 - (o & 15)]; return true;
                case 13: address = rs + imm * 8; for (int o = e; o < e + 8; o++) M[(address++ & 0xFFF) ^ 3] = V[15 - (o & 15)]; return true;
                case 14: { address = rs + imm * 16; int end = e + (16 - (int)(address & 15)); for (int o = e; o < end; o++) M[(address++ & 0xFFF) ^ 3] = V[15 - (o & 15)]; return true; }
            }
            return TryVectorLoadStore(inst, load);
        }

        internal static readonly bool s_LfvSfv = Environment.GetEnvironmentVariable("COR64_EMUFIX5_LFVSFV") != "0";
        private bool TryVectorLoadStore(DecodedInstruction inst, bool load)
        {
            uint raw = inst.Inst.inst;
            int imm = ((int)(raw << 25)) >> 25;          // s7 offset
            int e = (int)((raw >> 7) & 0xF);
            var vt = m_VecRegs[inst.Target];
            long rs = ReadGPR(inst.Source);
            string op = inst.Op.Op;
            long address;
            switch (op) {
                case "lbv": VB(vt, e, DB(rs + imm)); return true;
                case "lsv": address = rs + imm * 2; for (int o = e; o < Math.Min(e + 2, 16); o++) VB(vt, o & 15, DB(address++)); return true;
                case "llv": address = rs + imm * 4; for (int o = e; o < Math.Min(e + 4, 16); o++) VB(vt, o & 15, DB(address++)); return true;
                case "ldv": address = rs + imm * 8; for (int o = e; o < Math.Min(e + 8, 16); o++) VB(vt, o & 15, DB(address++)); return true;
                case "lqv": { address = rs + imm * 16; int end = Math.Min(16 + e - (int)(address & 15), 16); for (int o = e; o < end; o++) VB(vt, o & 15, DB(address++)); return true; }
                case "lrv": { address = rs + imm * 16; int start = 16 - ((int)(address & 15) - e); address &= ~15L; for (int o = start; o < 16; o++) VB(vt, o & 15, DB(address++)); return true; }
                case "lpv": { address = rs + imm * 8; int index = (int)(address & 7) - e; address &= ~7L; for (int o = 0; o < 8; o++) vt.PackedU16(o, (ushort)(DB(address + ((index + o) & 15)) << 8)); return true; }
                case "luv": { address = rs + imm * 8; int index = (int)(address & 7) - e; address &= ~7L; for (int o = 0; o < 8; o++) vt.PackedU16(o, (ushort)(DB(address + ((index + o) & 15)) << 7)); return true; }
                case "lhv": { address = rs + imm * 16; int index = (int)(address & 7) - e; address &= ~7L; for (int o = 0; o < 8; o++) vt.PackedU16(o, (ushort)(DB(address + ((index + o * 2) & 15)) << 7)); return true; }
                case "sbv": DB(rs + imm, VB(vt, e)); return true;
                case "ssv": address = rs + imm * 2; for (int o = e; o < e + 2; o++) DB(address++, VB(vt, o & 15)); return true;
                case "slv": address = rs + imm * 4; for (int o = e; o < e + 4; o++) DB(address++, VB(vt, o & 15)); return true;
                case "sdv": address = rs + imm * 8; for (int o = e; o < e + 8; o++) DB(address++, VB(vt, o & 15)); return true;
                case "sqv": { address = rs + imm * 16; int end = e + (16 - (int)(address & 15)); for (int o = e; o < end; o++) DB(address++, VB(vt, o & 15)); return true; }
                case "srv": { address = rs + imm * 16; int end = e + (int)(address & 15); int b = 16 - (int)(address & 15); address &= ~15L; for (int o = e; o < end; o++) DB(address++, VB(vt, (o + b) & 15)); return true; }
                case "spv": address = rs + imm * 8; for (int o = e; o < e + 8; o++) { if ((o & 15) < 8) DB(address++, VB(vt, (o & 7) << 1)); else DB(address++, (byte)(vt.PackedU16(o & 7) >> 7)); } return true;
                case "suv": address = rs + imm * 8; for (int o = e; o < e + 8; o++) { if ((o & 15) < 8) DB(address++, (byte)(vt.PackedU16(o & 7) >> 7)); else DB(address++, VB(vt, (o & 7) << 1)); } return true;
                case "shv": { address = rs + imm * 16; int index = (int)(address & 7); address &= ~7L; for (int o = 0; o < 8; o++) { int bb = e + o * 2; byte val = (byte)((VB(vt, bb & 15) << 1) | (VB(vt, (bb + 1) & 15) >> 7)); DB(address + ((index + o * 2) & 15), val); } return true; }
                // [emu_fix5] LFV/SFV (packed fourth) as on hardware (behaviour from ares rsp/interpreter-vpu.cpp, ISC). The old
                // VectorLoadStoreHelper Forth path used e as a lane index, stored the low byte instead of value >> 7 and
                // ignored the 8-byte alignment/wrap: RE2's 32bpp FMV microcode is mostly SFV. COR64_EMUFIX5_LFVSFV=0 = old.
                case "lfv" when s_LfvSfv: {
                    address = rs + imm * 16; int index = (int)(address & 7) - e; address &= ~7L;
                    Span<byte> tmp = stackalloc byte[16];
                    for (int o = 0; o < 4; o++) {
                        ushort a0 = (ushort)(DB(address + ((index + o * 4) & 15)) << 7), a1 = (ushort)(DB(address + ((index + o * 4 + 8) & 15)) << 7);
                        tmp[o * 2] = (byte)(a0 >> 8); tmp[o * 2 + 1] = (byte)a0; tmp[(o + 4) * 2] = (byte)(a1 >> 8); tmp[(o + 4) * 2 + 1] = (byte)a1;
                    }
                    int end = Math.Min(e + 8, 16); for (int o = e; o < end; o++) VB(vt, o, tmp[o]);
                    return true;
                }
                case "sfv" when s_LfvSfv: {
                    address = rs + imm * 16; int b = (int)(address & 7); address &= ~7L;
                    int s0 = e switch { 0 or 15 => 0, 1 => 6, 4 => 1, 5 => 7, 8 => 4, 11 => 3, 12 => 5, _ => -1 };
                    for (int k = 0; k < 4; k++) {
                        // element order: 0-3 rotate inside the low or high half
                        byte val = 0;
                        if (s0 >= 0) { int half = s0 & 4; int el = half | ((s0 + k) & 3); val = (byte)(vt.PackedU16(el) >> 7); }
                        DB(address + ((b + k * 4) & 15), val);
                    }
                    return true;
                }
            }
            return false;
        }

        // [climb4 rsp_speed1] COR64_RSPSPD1_TV: LTV/STV on raw lane bytes and the DMEM pointer. Same addresses, element order
        // and register indexing as VectorLoadStoreHelper.LoadTranposed/StoreTranposed; cases where those run past DMEM's end
        // (into IMEM), touch a non-plain register or index past $v31 keep the old path.
        internal static readonly bool s_Spd1Tv = Environment.GetEnvironmentVariable("COR64_RSPSPD1_TV") != "0";
        private unsafe bool TryTransposeFast(DecodedInstruction inst, bool load)
        {
            if (inst.Op.VectorFlags != VectorOpFlags.Transpose || inst.Op.Flags != ExecutionFlags.None) return false;
            int target = inst.VTarget, element = inst.Inst.lsde;
            int length = 8; if (length > 32 - target) length = 32 - target;
            uint raw = inst.Inst.inst;
            byte* M = DmemBytes;
            if (load) {
                short offset = Cxd4StaticRsp.ComputeOffset(raw); offset <<= 4;
                long address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(ReadGPR(inst.Source), offset);
                address = ((address + 8) & 0xFF0) + (element & 1);
                if (address + 2 * length > 0x1000) return false;
                for (int i = 0; i < length; i++) if (!m_VecRegs[target + i].IsPlain) return false;
                for (int i = 0; i < length; i++) {
                    int de = ((8 - (element >> 1) + i) << 1) & 0xF;
                    byte* V = (byte*)m_VecRegs[target + i].RawLanes;
                    V[15 - de] = M[address++ ^ 3];
                    V[15 - (de + 1)] = M[address++ ^ 3];
                }
                return true;
            }
            else {
                if (target + 7 > 31) return false;
                short offset = (short)((raw & 64) > 0 ? -(short)((~raw % 64) + 1) : (short)(raw % 64)); offset <<= 4;
                long address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(ReadGPR(inst.Source), offset);
                if (address + 2 * length > 0x1000) return false;
                for (int i = 0; i < 8; i++) if (!m_VecRegs[target + i].IsPlain) return false;
                int de = element >> 1;
                for (int i = 0; i < 2 * length; i += 2) {
                    byte* V = (byte*)m_VecRegs[target + de].RawLanes;
                    M[address++ ^ 3] = V[15 - i];
                    M[address++ ^ 3] = V[15 - (i + 1)];
                    de++; de &= 7;
                }
                return true;
            }
        }

        public override void VectorLoad(DecodedInstruction inst)
        {
            if (s_Spd1Tv && TryTransposeFast(inst, true)) return;
            if (!s_OldVls && (s_VlsFast ? TryVectorLoadStoreFast(inst, true) : TryVectorLoadStore(inst, true))) return;
            var executeFlag = inst.Op.Flags;
            var vectorFlag  = inst.Op.VectorFlags;
            var target =  m_VecRegs[inst.Target];
            var element = inst.Inst.lsde;
            var baseAddress = ReadGPR(inst.Source);
            // var offset = inst.Offset;

            long address;

            // From cxd4 RSP, this fixes a triangle issue
            short offset = Cxd4StaticRsp.ComputeOffset(inst.Inst.inst);

            if (executeFlag != ExecutionFlags.None) {
                switch (executeFlag) {
                    case ExecutionFlags.Data8: {
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.LoadByteIntoVector(DMEM, address, target, element);
                        break;
                    }

                    case ExecutionFlags.Data16: {
                        offset <<= 1;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.LoadU16IntoVector(DMEM, address, target, element);
                        break;
                    }

                    case ExecutionFlags.Data32: {
                        offset <<= 2;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.LoadU32IntoVector(DMEM, address, target, element);
                        break;
                    }

                    case ExecutionFlags.Data64: {
                        offset <<= 3;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.LoadU64IntoVector(DMEM, address, target, element);
                        break;
                    }

                    case ExecutionFlags.Data128: {
                        offset <<= 4;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.LoadU128IntoVector(DMEM, address, target, element);
                        break;
                    }

                    case ExecutionFlags.Unsigned: {
                        // offset <<= 3;
                        // address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        // VectorLoadStoreHelper.LoadPacked_Unsigned(DMEM, address, target, element);
                        Cxd4StaticRsp.LUV(target, (uint)element, offset, baseAddress);
                        break;
                    }

                    default: throw new EmuException("Invalid RSP VectorLoad Execution Flag");
                }
            }

            if (vectorFlag != VectorOpFlags.None) {
                switch (vectorFlag) {
                    case VectorOpFlags.Forth: {
                        offset <<= 4;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.LoadPacked_Forth(DMEM, address, target, element);
                        break;
                    }

                    case VectorOpFlags.Half: {
                        offset <<= 4;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.LoadPacked_Half(DMEM, address, target);
                        break;
                    }

                    case VectorOpFlags.Upper: {
                        offset <<= 3;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.LoadPacked_Upper(DMEM, address, target);
                        break;
                    }
                    case VectorOpFlags.Wrap: {
                        throw new NotSupportedException("LWV is an undocumented opcode");
                    }

                    case VectorOpFlags.Transpose: {
                        offset <<= 4;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.LoadTranposed(DMEM, address, m_VecRegs, inst.VTarget, element);
                        break;
                    }

                    case VectorOpFlags.Rest: {
                        Log.Debug("TODO: LRV");
                        break;
                    }

                    default: throw new EmuException("Invalid RSP VectorLoad Vector Flag");
                }
            }
        }

        public override void VectorStore(DecodedInstruction inst)
        {
            if (s_Spd1Tv && TryTransposeFast(inst, false)) return;
            if (!s_OldVls && (s_VlsFast ? TryVectorLoadStoreFast(inst, false) : TryVectorLoadStore(inst, false))) return;
            var executeFlag = inst.Op.Flags;
            var vectorFlag  = inst.Op.VectorFlags;
            var target =  m_VecRegs[inst.Target];
            var element = inst.Inst.lsde;
            var baseAddress = ReadGPR(inst.Source);
            var raw = inst.Inst.inst;
            // var offset = inst.Offset;

            // From cxd4 RSP, this fixes a triangle issue
            short offset = (short)((raw & 64) > 0 ? -(short)((~raw % 64) + 1) : (short)(raw % 64));

            long address;

            if (executeFlag != ExecutionFlags.None) {
                switch (executeFlag) {
                    case ExecutionFlags.Data8: {
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.StoreByteFromVector(DMEM, address, target, element);
                        break;
                    }

                    case ExecutionFlags.Data16: {
                        offset <<= 1;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.StoreU16FromVector(DMEM, address, target, element);
                        break;
                    }

                    case ExecutionFlags.Data32: {
                        offset <<= 2;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.StoreU32FromVector(DMEM, address, target, element);
                        break;
                    }

                    case ExecutionFlags.Data64: {
                        offset <<= 3;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.StoreU64FromVector(DMEM, address, target, element);
                        break;
                    }

                    case ExecutionFlags.Data128: {
                        offset <<= 4;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.StoreU128FromVector(DMEM, address, target, element);
                        break;
                    }

                    case ExecutionFlags.Unsigned: {
                        offset <<= 3;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.StorePacked_Unsigned(DMEM, address, target);
                        break;
                    }

                    default: throw new EmuException("Invalid RSP VectorStore Execution Flag");
                }
            }

            if (vectorFlag != VectorOpFlags.None) {
                switch (vectorFlag) {
                    case VectorOpFlags.Forth: {
                        offset <<= 4;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.StorePacked_Forth(DMEM, address, target, element);
                        break;
                    }

                    case VectorOpFlags.Half: {
                        offset <<= 4;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.StorePacked_Half(DMEM, address, target); 
                        break;
                    }

                    case VectorOpFlags.Upper: {
                        offset <<= 3;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.StorePacked_Upper(DMEM, address, target);
                        break;
                    }
                    case VectorOpFlags.Wrap: {
                        offset <<= 4;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.StoreWrapped(DMEM, address, m_VecRegs, inst.VTarget, element);
                        break;
                    }

                    case VectorOpFlags.Transpose: {
                        offset <<= 4;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.StoreTranposed(DMEM, address, m_VecRegs, inst.VTarget, element);
                        break;
                    }

                    case VectorOpFlags.Rest: {
                        offset <<= 4;
                        address = VectorLoadStoreHelper.ComputeAlignedVectorAddress(baseAddress, offset);
                        VectorLoadStoreHelper.StoreRest(DMEM, address, m_VecRegs, inst.VTarget, element);
                        break;
                    }

                    default: throw new EmuException("Invalid RSP VectorStore Vector Flag");
                }
            }
        }

        // [cer64plus] Non-carry VADD/VSUB (default; COR64_RSP_SIMD=0 off) on plain registers with AVX2 (32-bit lanes, carry from VCO,
        // signed saturating pack for vd, ACC low written with Acc.Lo's exact masking). Same results as the lane loop.
        private static readonly bool s_VaddSimd = Environment.GetEnvironmentVariable("COR64_RSP_SIMD") != "0" && Avx2.IsSupported;
        private static readonly Vector256<int>[] s_CarryLanes = BuildCarryLanes();
        private static Vector256<int>[] BuildCarryLanes()
        {
            var t = new Vector256<int>[256];
            for (int b = 0; b < 256; b++) {
                var l = new int[8];
                for (int k = 0; k < 8; k++) l[k] = (b >> (7 - k)) & 1;
                t[b] = Vector256.Create(l);
            }
            return t;
        }

        private unsafe bool VectorAddSubSimd(RspVector vs, RspVector vt, RspVector vd, bool add)
        {
            if (!vs.IsPlain || !vt.IsPlain || !vd.IsPlain) return false;
            var sv = Avx2.ConvertToVector256Int32(Vector128.Load(vs.RawLanes));
            var tv = Avx2.ConvertToVector256Int32(Vector128.Load(vt.RawLanes));
            var cv = s_CarryLanes[(byte)m_VCarry.RegValue];
            var r = add ? sv + tv + cv : sv - tv - cv;
            Sse2.PackSignedSaturate(r.GetLower(), r.GetUpper()).Store(vd.RawLanes);
            var lo = Vector128.Narrow(r.GetLower(), r.GetUpper()).AsUInt16();
            ulong[] A = Acc.Raw;
            for (int k = 0; k < 8; k++) A[7 - k] = lo.GetElement(k) | (A[7 - k] & 0xFFFFFFFF0000UL);
            return true;
        }

        public override void VectorAdd(DecodedInstruction inst) {
            var carry = (inst.Op.Flags & ExecutionFlags.Carry) == ExecutionFlags.Carry;
            var vt = m_VecRegs[inst.VTarget].Resolve(inst.Element, m_ResolveScratch);
            if (s_VaddSimd && !carry && VectorAddSubSimd(m_VecRegs[inst.VSource], vt, m_VecRegs[inst.VDest], true)) { m_VCarry.ClearCarryBits(); return; }

            for (int i = 0; i < 8; i++) {
                if (!carry) {
                    int result = (short)m_VecRegs[inst.VSource].PackedU16(i) + (short)vt.PackedU16(i) + m_VCarry.GetCarryBit(i);
                    m_VecRegs[inst.VDest].PackedU16(i, ClampS16(result));
                    Acc.Lo(i, (ushort)result); // [cer64plus] only ACC low is written (ares)
                }
                else {
                    uint result = (uint)m_VecRegs[inst.VSource].PackedU16(i) + (uint)vt.PackedU16(i);
                    m_VecRegs[inst.VDest].PackedU16(i, (ushort)result);
                    m_VCarry.SetNotEqualBit(i, 0);
                    m_VCarry.SetCarryBit(i, (byte)(result >> 16));
                    Acc.Lo(i, (ushort)result); // [cer64plus] only ACC low is written (ares)
                }
            }

            if (!carry) {
                m_VCarry.ClearCarryBits();
            }
        }

        public override void VectorSubtract(DecodedInstruction inst) {
            var carry = (inst.Op.Flags & ExecutionFlags.Carry) == ExecutionFlags.Carry;
            var vs = m_VecRegs[inst.VSource];
            var vt = m_VecRegs[inst.VTarget].Resolve(inst.Element, m_ResolveScratch);
            if (s_VaddSimd && !carry && VectorAddSubSimd(vs, vt, m_VecRegs[inst.VDest], false)) { m_VCarry.Clear(); return; }

            if (carry) {
                m_VCarry.Clear();
            }

            for (int i = 0; i < 8; i++) {
                if (!carry) {
                    int result = vs.PackedS16(i) - vt.PackedS16(i) - m_VCarry.GetCarryBit(i);
                    Acc.Lo(i, (ushort)result);

                    short lo = (short) ((result + 0x8000) >> 31);
                    short hi = (short) ((0x7FFF - result) >> 31);
                    short d = (short)(ushort)result;

                    d &= (short)~lo;
                    d |= hi;
                    d ^= (short)(0x8000 & (short)(hi | lo));

                    m_VecRegs[inst.VDest].PackedS16(i, d);
                }
                else {
                    uint result = (uint)vs.PackedU16(i) - vt.PackedU16(i);
                    m_VecRegs[inst.VDest].PackedU16(i, (ushort)result);
                    Acc.Lo(i, (ushort)result); // [cer64plus] only ACC low is written (ares)

                    var signBit = (1 & (result >> 16)) != 0;

                    if (result == 0) {
                        m_VCarry.SetCarryBit(i, 0);
                        m_VCarry.SetNotEqualBit(i, 0);
                    }
                    else {
                        if (signBit) {
                            /* result < 0 */
                            m_VCarry.SetCarryBit(i, 1);
                            m_VCarry.SetNotEqualBit(i, 1);
                        }
                        else {
                            /* result > 0 */
                            m_VCarry.SetCarryBit(i, 0);
                            m_VCarry.SetNotEqualBit(i, 1);
                        }
                    }
                }
            }

            if (!carry) {
                m_VCarry.Clear();
            }
        }

        public override void VectorMultiply(DecodedInstruction inst) {
            var unsigned = inst.IsUnsigned();
            var acc = (inst.Op.VectorFlags & VectorOpFlags.Accumulate) == VectorOpFlags.Accumulate;
            var lp = (inst.Op.VectorFlags & VectorOpFlags.PartialL) == VectorOpFlags.PartialL;
            var mp = (inst.Op.VectorFlags & VectorOpFlags.PartialM) == VectorOpFlags.PartialM;
            var np = (inst.Op.VectorFlags & VectorOpFlags.PartialN) == VectorOpFlags.PartialN;
            var hp = (inst.Op.VectorFlags & VectorOpFlags.PartialH) == VectorOpFlags.PartialH;
            var vt = m_VecRegs[inst.VTarget].Resolve(inst.Element, m_ResolveScratch);
            var vs = m_VecRegs[inst.VSource];
            var vd = m_VecRegs[inst.VDest];

            if (vs.IsPlain && vt.IsPlain && vd.IsPlain) { VectorMultiplyFast(vs, vt, vd, unsigned, acc, lp, mp, np, hp); return; }

            for (int i = 0; i < 8; i++)
            {
                ulong product;

                // Multiply fractions (with/without scalars)
                if (mp) product      = (ulong)(vs.PackedS16(i) * vt.PackedU16(i));
                else if (np) product = (ulong)(vs.PackedU16(i) * vt.PackedS16(i));
                else if (lp) product = (ulong)(vs.PackedU16(i) * vt.PackedU16(i));
                else product         = (ulong)(vs.PackedS16(i) * vt.PackedS16(i));

                // Product shifting and rounding
                if (!mp && !np)
                {
                    if (lp)
                    {
                        product >>= 16;
                        product &= 0xFFFF;
                    }
                    else if (hp)
                    {
                        product <<= 16;
                    }
                    else
                    {
                        product <<= 1;
                        if (!acc) product += 0x8000; // Product Rounding
                    }
                }

                // Product accumulation
                if (acc)
                    product += Acc[i];

                Acc[i] = product;

                // Write accumulator into writeback vector register with clamping
                if (lp)
                {
                    vd.StoreProductClampedSignedAL(Acc, i);
                }
                else if (np && acc) {
                    vd.StoreProductClampedSignedAL(Acc, i);
                }
                else if (np && !acc)
                {
                    vd.PackedU16(i, Acc.Lo(i));
                }
                else
                {
                    if (unsigned)
                    {
                        vd.StoreProductClampedUnsigned(Acc, i);
                    }
                    else
                    {
                        vd.StoreProductClampedSignedAM(Acc, i);
                    }
                }
            }
        }

        // [cer64plus] Same arithmetic as the generic VectorMultiply loop (bit for bit, including its int-overflow
        // behaviour for u16*u16), on raw lanes and the raw accumulator array instead of virtual accessors.
        private static int ClampAM(ulong a) {
            int acc_h = (short)(ushort)(a >> 32);
            int acc_m = (short)(ushort)(a >> 16);
            bool hi = (acc_h > 0) || (acc_h == 0 && (acc_m < 0));
            bool lo = (acc_h < -1) || (acc_h < 0 && (acc_m >= 0));
            int result = acc_m;
            result &= -((lo ? 1 : 0) ^ 1);
            result |= -((hi ? 1 : 0) ^ 0);
            result ^= (hi || lo) ? 0x8000 : 0x0000;
            return (short)result;
        }

        // [cer64plus] AVX2 version of VectorMultiplyFast (default when AVX2 exists; COR64_RSP_SIMD=0 disables), bit-identical by construction:
        //  - every product fits in 32 bits (s16*s16, s16*u16, u16*s16; u16*u16 only matters masked to 16 bits);
        //  - the int product is sign-extended to 64 bits exactly as the scalar `(ulong)(int)` cast does;
        //  - ClampAM is a signed saturation of accumulator bits 47..16 to 16 bits (PackSignedSaturate).
        // Lane k of the vector registers is element 7-k, i.e. accumulator index i = 7-k.
        private static readonly bool s_VmulSimd = Environment.GetEnvironmentVariable("COR64_RSP_SIMD") != "0"
            && System.Runtime.Intrinsics.X86.Avx2.IsSupported;

        private static unsafe void VectorMultiplySimd(short* S, short* T, short* D, ulong[] A, bool unsigned, bool acc, bool lp, bool mp, bool np, bool hp)
        {
            var s16 = Vector128.Load(S); var t16 = Vector128.Load(T);
            // operand signedness: mp = s*u, np = u*s, lp = u*u, otherwise s*s
            Vector256<int> sw = (np || lp) ? Avx2.ConvertToVector256Int32(s16.AsUInt16()) : Avx2.ConvertToVector256Int32(s16);
            Vector256<int> tw = (mp || lp) ? Avx2.ConvertToVector256Int32(t16.AsUInt16()) : Avx2.ConvertToVector256Int32(t16);
            var p32 = Avx2.MultiplyLow(sw, tw);                       // the scalar int product (wraps the same way)
            var pLo = Avx2.ConvertToVector256Int64(p32.GetLower());   // lanes k=0..3, sign-extended like (ulong)(int)
            var pHi = Avx2.ConvertToVector256Int64(p32.GetUpper());   // lanes k=4..7
            if (!mp && !np) {
                if (lp) { var m = Vector256.Create(0xFFFFL); pLo = Avx2.ShiftRightLogical(pLo, 16) & m; pHi = Avx2.ShiftRightLogical(pHi, 16) & m; }
                else if (hp) { pLo = Avx2.ShiftLeftLogical(pLo, 16); pHi = Avx2.ShiftLeftLogical(pHi, 16); }
                else {
                    pLo = Avx2.ShiftLeftLogical(pLo, 1); pHi = Avx2.ShiftLeftLogical(pHi, 1);
                    if (!acc) { var r = Vector256.Create(0x8000L); pLo += r; pHi += r; }
                }
            }
            if (acc) {
                pLo += Vector256.Create((long)A[7], (long)A[6], (long)A[5], (long)A[4]);
                pHi += Vector256.Create((long)A[3], (long)A[2], (long)A[1], (long)A[0]);
            }
            A[7] = (ulong)pLo.GetElement(0); A[6] = (ulong)pLo.GetElement(1); A[5] = (ulong)pLo.GetElement(2); A[4] = (ulong)pLo.GetElement(3);
            A[3] = (ulong)pHi.GetElement(0); A[2] = (ulong)pHi.GetElement(1); A[1] = (ulong)pHi.GetElement(2); A[0] = (ulong)pHi.GetElement(3);

            var xLo = Low32(Avx2.ShiftRightLogical(pLo, 16)); var xHi = Low32(Avx2.ShiftRightLogical(pHi, 16)); // acc bits 47..16
            var am = Sse2.PackSignedSaturate(xLo, xHi);              // ClampAM
            var accm = Vector128.Narrow(xLo, xHi);                    // (short)(acc >> 16)
            Vector128<short> res;
            if (lp || (np && acc)) {
                var lo16 = Vector128.Narrow(Low32(pLo), Low32(pHi));
                var same = Vector128.Equals(am, accm);
                res = Vector128.ConditionalSelect(same, lo16, am ^ Vector128.Create(unchecked((short)0x8000)));
            }
            else if (np) res = Vector128.Narrow(Low32(pLo), Low32(pHi));
            else if (unsigned) {
                var pos = am & ~Vector128.ShiftRightArithmetic(am, 15);
                res = pos | Vector128.GreaterThan(am, accm);
            }
            else res = am;
            res.Store(D);
        }

        // [climb4 rsp_speed1] COR64_RSPS1_INL: Low32 was a real call (0.44% of SM64 samples) inside big compiled RSP
        // blocks once the JIT's inline budget ran out; force it inline. Same instructions either way.
        private static readonly bool s_Inl = Environment.GetEnvironmentVariable("COR64_RSPS1_INL") == "1";
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static Vector128<int> Low32(Vector256<long> v) => s_Inl ? Low32I(v) : Low32N(v);
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static Vector128<int> Low32I(Vector256<long> v) =>
            Avx2.PermuteVar8x32(v.AsInt32(), Vector256.Create(0, 2, 4, 6, 0, 2, 4, 6)).GetLower();
        private static Vector128<int> Low32N(Vector256<long> v) =>
            Avx2.PermuteVar8x32(v.AsInt32(), Vector256.Create(0, 2, 4, 6, 0, 2, 4, 6)).GetLower();

        private unsafe void VectorMultiplyFast(RspVector vs, RspVector vt, RspVector vd, bool unsigned, bool acc, bool lp, bool mp, bool np, bool hp) {
            short* S = vs.RawLanes; short* T = vt.RawLanes; short* D = vd.RawLanes;
            if (s_VmulSimd) { VectorMultiplySimd(S, T, D, Acc.Raw, unsigned, acc, lp, mp, np, hp); return; }
            ulong[] A = Acc.Raw;
            for (int i = 0; i < 8; i++) {
                int k = 7 - i;
                short s16 = S[k]; short t16 = T[k];
                ushort su = (ushort)s16, tu = (ushort)t16;
                ulong product;
                if (mp) product = (ulong)(s16 * tu);
                else if (np) product = (ulong)(su * t16);
                else if (lp) product = (ulong)(su * tu);
                else product = (ulong)(s16 * t16);

                if (!mp && !np) {
                    if (lp) { product >>= 16; product &= 0xFFFF; }
                    else if (hp) { product <<= 16; }
                    else { product <<= 1; if (!acc) product += 0x8000; }
                }
                if (acc) product += A[i];
                A[i] = product;

                short am = (short)ClampAM(product);
                if (lp || (np && acc)) {
                    short accm = (short)(ushort)(product >> 16);
                    D[k] = am != accm ? (short)(am ^ 0x8000) : (short)(ushort)product;
                }
                else if (np) {
                    D[k] = (short)(ushort)product;
                }
                else if (unsigned) {
                    int accm = (short)(ushort)(product >> 16);
                    int val = am;
                    int result = val & ~(val >> 15);
                    if (val > accm) result |= 0xFFFF;
                    D[k] = (short)(ushort)result;
                }
                else {
                    D[k] = am;
                }
            }
        }

        public override void VectorAccumulatorReadWrite(DecodedInstruction inst) {
            // This instruction acts as 2 opcodes known as VSAR and VSAW
            // VSAR - Read the accumulator into dest vector
            // VSAW - Write source vector unto the accumulator

            // XXX: VT is unused in this instruction

            // XXX: VSAW is not clearly explained.
            //      The official doc says VSAW writes
            //      vector source into the accumulator
            //      howerver this breaks tests
            //      VSAW only works in the case
            //      that vector source is preset by the acc 

            // Convert the range from 8:F to 0:7
            int e = inst.Element ^ 8;

            // VDest is cleared on invalid element values
            // Do nothing else
            if (e > 2) {
                m_VecRegs[inst.VDest].Clear();
                return;
            }

            for (int i = 0; i < 8; i++) {
                if (e == 0) {
                    // m_VecRegs[inst.VSource].PackedU16(i, Acc.Hi(i));
                    m_VecRegs[inst.VDest].PackedU16(i, Acc.Hi(i));
                    //Acc.Hi(i, m_VecRegs[inst.VSource].PackedU16(i));
                }
                else if (e == 1) {
                    // m_VecRegs[inst.VSource].PackedU16(i, Acc.Mi(i));
                    m_VecRegs[inst.VDest].PackedU16(i, Acc.Mi(i));
                    //Acc.Mi(i, m_VecRegs[inst.VSource].PackedU16(i));
                }
                else if (e == 2) {
                    // m_VecRegs[inst.VSource].PackedU16(i, Acc.Lo(i));
                    m_VecRegs[inst.VDest].PackedU16(i, Acc.Lo(i));
                    //Acc.Lo(i, m_VecRegs[inst.VSource].PackedU16(i));
                }
                else {
                    Console.WriteLine("VSAR element = " + inst.Element.ToString() + " ?");
                }
            }
        }

        public override void VectorBitwise(DecodedInstruction inst) {
            // [cer64plus] resolve the element selector once, before any lane of vd is written
            var vtResolved = m_VecRegs[inst.VTarget].Resolve(inst.Element, m_ResolveScratch);
            Span<ushort> tv = stackalloc ushort[8];
            for (int k = 0; k < 8; k++) tv[k] = vtResolved.PackedU16(k);
            for (int i = 0; i < 8; i++) {
                var source = (int) m_VecRegs[inst.VSource].PackedU16(i);
                var target = (int) tv[i];
                int result;

                switch (inst.Op.ArithmeticType) {
                    default: throw new EmuException("vector bitwise op not supported: " + inst.Op.ArithmeticType.ToString());
                    case ArithmeticOp.AND:
                    case ArithmeticOp.NAND: {
                        result = source & target;
                        break;
                    }

                    case ArithmeticOp.OR:
                    case ArithmeticOp.NOR: {
                        result = source | target;
                        break;
                    }

                    case ArithmeticOp.XOR:
                    case ArithmeticOp.NXOR: {
                        result = source ^ target;
                        break;
                    }

                    case ArithmeticOp.ABSOLUTE: {
                        var s = (short)source;
                        var t = (short)target;
                        int r = 0;

                        if (s < 0) {
                            r = -t;
                        }
                        else if (s == 0) {
                            r = 0;
                        }
                        else if (s > 0) {
                            r = t;
                        }

                        result = (short)r;

                        break;
                    }
                }

                if (inst.Op.ArithmeticType == ArithmeticOp.NAND || inst.Op.ArithmeticType == ArithmeticOp.NOR || inst.Op.ArithmeticType == ArithmeticOp.NXOR) {
                    result = ~result;
                }

                ushort value = (ushort)result;
                Acc.Lo(i, value); // [cer64plus] only ACC low is written (ares)

                m_VecRegs[inst.VDest].PackedU16(i, value);
            }
        }

        public override void VectorReciprocal(DecodedInstruction inst) {
            var target = m_VecRegs[inst.VTarget & 31];
            var dest = m_VecRegs[inst.VDest];
            var useSqrt = (inst.Op.VectorFlags & VectorOpFlags.SquareRoot) == VectorOpFlags.SquareRoot;
            var high = (inst.Op.VectorFlags & VectorOpFlags.PartialH) == VectorOpFlags.PartialH;
            var low = (inst.Op.VectorFlags & VectorOpFlags.PartialL) == VectorOpFlags.PartialL;
            var doublePrecision = high || m_lastDoublePrecision;

            int shift = high ? 16 : 0;

            if (low && m_lastDoublePrecision) {
                m_DivIn |= target.PackedU16(inst.Element & 7) & 0xFFFF;
            }
            else {
                m_DivIn = target.PackedS16(inst.Element & 7) << shift;
            }


            if (!high) {
                int _data = m_DivIn;
                int _address;
                int _shift;

                if (!doublePrecision)
                {
                    _data = (_data < 0) ? -_data : _data;
                }

                if (doublePrecision && _data < 0)
                {
                    _data = (_data >= -32768) ? -_data : ~_data;
                }

                /*
                 * Note, from the code just above, that data cannot be negative.
                 * (data >= 0) is unconditionally forced by the above algorithm.
                 */

                _address = _data;

                if (_data == 0)
                {
                    _shift = !doublePrecision ? 16 : 0;
                    _address <<= _shift;
                }
                else
                {
                    _shift = 0;

                    while (_address >= 0)
                    {
                        _address <<= 1;
                        _shift++;
                    }
                }

                _address = (_address >> 22) & 0x1FF;

                if (useSqrt)
                {
                    _address &= 0x1FE;
                    _address |= 0x200 | (_shift & 1);
                }

                /* flipping shift direction from left- to right- */
                _shift ^= 31;

                if (useSqrt)
                    _shift >>= 1;

                m_DivOut = (int) ((0x40000000U | ((uint)DivideTable.ReadValue(_address) << 14)) >> _shift);

                if (m_DivIn == 0) {
                    /* corner case:  overflow via division by zero */
                    m_DivOut = 0x7FFFFFFF;
                }
                else if (m_DivIn == -32768) {
                    /* corner case:  signed underflow barrier */
                    m_DivOut = -0x00010000;
                }
                else {
                    m_DivOut ^= (m_DivIn < 0) ? ~0 : 0;
                }
            }

            Acc.SetLoFromVector(target);

            dest.PackedS16(inst.DestElement & 7, (short)(m_DivOut >> shift));

            m_lastDoublePrecision = high;
        }

        public override void VectorCompare(DecodedInstruction inst) {
            var source = m_VecRegs[inst.VSource];
            var target = m_VecRegs[inst.VTarget].Resolve(inst.Element, m_ResolveScratch);
            var dest   = m_VecRegs[inst.VDest];

            /* Discard the previous data */
            M_VccCompare.Clear();

            for (int i = 0; i < 8; i++) {
                var source_value = source.PackedS16(i);
                var target_value = target.PackedS16(i);
                var equal = source_value == target_value;

                switch (inst.Op.ArithmeticType) {
                    default: throw new EmuException("Invalid arithmetic flag for vector compare");

                    case ArithmeticOp.EQUAL: {
                        M_VccCompare.SetBool(i, equal);
                        M_VccCompare[i] &= (byte)(m_VCarry.GetNotEqualBit(i) ^ 1);
                        Acc.Lo(i, target.PackedU16(i));
                        break;
                    }

                    case ArithmeticOp.NOT_EQUAL: {
                        M_VccCompare.SetBool(i, !equal);
                        M_VccCompare[i] |= m_VCarry.GetNotEqualBit(i);
                        Acc.Lo(i, source.PackedU16(i));
                        break;
                    }

                    case ArithmeticOp.GREATER_THAN_OR_EQUAL: {
                        equal &= !(m_VCarry.GetNotEqualBool(i) && m_VCarry.GetCarryBitBool(i));
                        M_VccCompare.SetBool(i, (source_value > target_value) || equal);
                        Acc.Lo(i, Merge(M_VccCompare.GetBool(i), source.PackedS16(i), target.PackedS16(i)));
                        break;
                    }

                    case ArithmeticOp.LESS_THAN_OR_EQUAL: {
                        equal &= m_VCarry.GetNotEqualBool(i) && m_VCarry.GetCarryBitBool(i);
                        M_VccCompare.SetBool(i, (source_value < target_value) || equal);
                        Acc.Lo(i, Merge(M_VccCompare.GetBool(i), source.PackedS16(i), target.PackedS16(i)));
                        break;
                    }
                }

                /* Copy ACC_L into DST */
                dest.PackedU16(i, Acc.Lo(i));
            }

            /* Zero out flags */
            m_VCarry.Clear();
            m_VccClip.Clear();
        }

        public override void VectorClip(DecodedInstruction inst) {
            var source = m_VecRegs[inst.VSource];
            var target = m_VecRegs[inst.VTarget].Resolve(inst.Element, m_ResolveScratch);
            var dest   = m_VecRegs[inst.VDest];

            bool isMerge = (inst.Op.VectorFlags & VectorOpFlags.Merge) == VectorOpFlags.Merge;

            bool flag_compare = false;
            bool flag_clip = false;
            bool compare;
            short pass;
            short fail;

            for (int i = 0; i < 8; i++) {
                var target_slice = target.PackedU16(i);

                var source_u16 = source.PackedU16(i);
                var source_s16 = source.PackedS16(i);
                var target_u16 = target.PackedU16(i);
                var target_s16 = target.PackedS16(i);

                var carry = m_VCarry.GetCarryBitBool(i);

                switch (inst.Op.VectorFlags) {
                    case VectorOpFlags.PartialL: {
                        // [cer64plus] VCL ported from ares (interpreter-vpu.cpp, SISD path)
                        bool ne = m_VCarry.GetNotEqualBool(i);   // VCOH
                        flag_compare = M_VccCompare.GetBool(i);  // VCCL
                        flag_clip = m_VccClip.GetBool(i);        // VCCH
                        fail = source_s16;
                        if (carry) {
                            pass = (short)(-target_s16);
                            if (!ne) {
                                int full = source_u16 + target_u16;
                                bool sumZero = (full & 0xFFFF) == 0;
                                bool c = full > 0xFFFF;
                                flag_compare = m_Vce.GetBool(i) ? (sumZero || !c) : (sumZero && !c);
                            }
                            compare = flag_compare;
                        }
                        else {
                            pass = target_s16;
                            if (!ne) flag_clip = source_u16 >= target_u16;
                            compare = flag_clip;
                        }

                        m_VCarry.SetCarryBit(i, 0);
                        m_VCarry.SetNotEqualBit(i, false);
                        m_Vce.SetBool(i, false);

                        break;
                    }

                    case VectorOpFlags.PartialH: {
                        // [cer64plus] VCH rewritten to match ares (interpreter-vpu.cpp, SISD path)
                        bool is_signed = (short)(source_u16 ^ target_u16) < 0;
                        bool ne = false;
                        if (is_signed) {
                            short res = (short)(source_s16 + target_s16);
                            flag_compare = res <= 0;          // VCCL
                            flag_clip = target_s16 < 0;       // VCCH
                            m_VCarry.SetCarryBit(i, true);    // VCOL
                            ne = res != 0 && source_u16 != (ushort)(target_u16 ^ 0xFFFF);
                            m_Vce.SetBool(i, res == -1);
                            pass = (short)(-target_s16);
                            fail = source_s16;
                            compare = flag_compare;
                        }
                        else {
                            short res = (short)(source_s16 - target_s16);
                            flag_compare = target_s16 < 0;    // VCCL
                            flag_clip = res >= 0;             // VCCH
                            m_VCarry.SetCarryBit(i, false);
                            ne = res != 0 && source_u16 != (ushort)(target_u16 ^ 0xFFFF);
                            m_Vce.SetBool(i, false);
                            pass = target_s16;
                            fail = source_s16;
                            compare = flag_clip;
                        }
                        m_VCarry.SetNotEqualBit(i, ne);       // VCOH
                        break;
                    }

                    case VectorOpFlags.Crimp: {
                        var is_signed = (short)(source_u16 ^ target_u16) < 0;

                        flag_compare = is_signed ? target_s16 <= ~source_s16 : target_s16 <= ~0;
                        flag_clip = is_signed ? ~0 >= target_s16 : source_s16 >= target_s16;

                        if (is_signed) {
                            target_slice ^= 0xFFFF;
                        }

                        pass = (short) target_slice;
                        fail = source_s16;
                        compare = Merge(is_signed, flag_compare, flag_clip);

                        m_VCarry.SetCarryBit(i, 0);
                        m_VCarry.SetNotEqualBit(i, false); // [cer64plus] ares: VCR clears VCOH
                        m_Vce.SetBool(i, false);

                        break;
                    }

                    case VectorOpFlags.Merge: {
                        pass = source_s16;
                        fail = target_s16;
                        compare = M_VccCompare.GetBool(i);
                        break;
                    }

                    default: throw new EmuException("Invalid RSP clipping mode");
                }

                Acc.Lo(i, Merge(compare, pass, fail));
                dest.PackedU16(i, Acc.Lo(i));

                if (!isMerge) {
                    m_VccClip.SetBool(i, flag_clip);
                    M_VccCompare.SetBool(i, flag_compare);
                }
            }

            if (isMerge) m_VCarry.Clear(); // [cer64plus] ares: VMRG clears VCOL/VCOH
        }
        public override void VectorMove(DecodedInstruction inst)
        {
            var target = m_VecRegs[inst.VTarget].Resolve(inst.Element, m_ResolveScratch);
            var dest = m_VecRegs[inst.VDest];

            int elementSelect = inst.VSource & 7;

            var element = target.PackedU16(elementSelect);
            Acc.SetLoFromVector(target);
            dest.PackedU16(elementSelect, element);
        }
    }
}