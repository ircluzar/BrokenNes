using cor64.Debugging;
using cor64.HLE;
using cor64.IO;
using cor64.Mips.Analysis;
using cor64.Mips.R4300I;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using static cor64.Cartridge;

// TODO: Disposal pattern for streams

namespace cor64.Mips
{
    public abstract class BaseInterpreter
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private long m_Pc;
        protected IMemoryAccess m_Memory;
        private long m_ProgramStart;
        protected Profiler m_Profiler = new Profiler();

        public event Action<DecodedInstruction> TraceStep;

        protected BaseInterpreter(BaseDisassembler disassembler)
        {
            Disassembler = disassembler;

            TraceLog = new ProgramTrace(disassembler)
            {
                FilterInterruptHandlers = true
            };
        }

        public virtual void SafeSetPC(long address)
        {
            m_Pc = address;
        }

        protected long PC {
            get => m_Pc;

            set {
                m_Pc = value;
            }
        }

        public long ReadPC()
        {
            return m_Pc;
        }

        /// <summary>
        /// Call this to determine when the program's bootloader is finished
        /// </summary>
        /// <param name="address"></param>
        public void SetProgramEntryPoint(long address)
        {
            InBootMode = true;
            m_ProgramStart = address;
        }

        // public abstract void OverrideCoreState(ExecutionState state);

        public void SetDebuggingMode(bool mode)
        {
            DebugMode = mode;
        }

        public void SetTraceMode(ProgramTrace.TraceMode mode)
        {
            TraceMode = mode;

            if (mode != ProgramTrace.TraceMode.None)
            {
                Log.Debug("Execution trace logging has been enabled");
            }
        }

        protected void BeginInstructionProfile(DecodedInstruction inst)
        {
            m_Profiler.StartSession(inst.Op);
        }

        protected void EndInstructionProfile()
        {
            m_Profiler.StopSession();
        }

        public String GetProfiledResults()
        {
            return m_Profiler.GenerateReport();
        }

        // [cer64plus] The current instruction is either a decode-cache slot (the normal case: no per-step copy into a heap
        // field, which costs a GC write barrier per reference in the struct, ~16 ns) or m_CurrentInst (injected/HLE paths).
        protected DecodedInstruction m_CurrentInst;
        protected int m_CurrentSlot = -1;
        public DecodedInstruction CurrentInst {
            get => m_CurrentSlot >= 0 ? m_DecodeEntry[m_CurrentSlot] : m_CurrentInst;
            protected set { m_CurrentInst = value; m_CurrentSlot = -1; }
        }

        public abstract void Step();


        // CLEANUP: Shouldn't be in this base class
        public abstract void AttachBootManager(BootManager bootManager);

        public abstract MipsDebugger Debugger { get; }

        protected virtual void EntryPointHit()
        {
            InBootMode = false;
            Log.Debug("**** Program Entry Point Hit ****");
            TraceLog.MakeEntryNote(PC);
        }

        protected abstract uint FetchInstruction(long address);

        // [cer64plus] Direct-mapped decode cache: index by word address, validated by the raw instruction word,
        // so self-modifying code and freshly DMA'd microcode stay correct without explicit invalidation.
        private const int DecodeCacheBits = 16;
        private readonly uint[] m_DecodeRaw = new uint[1 << DecodeCacheBits];
        private readonly long[] m_DecodeAddr = new long[1 << DecodeCacheBits];
        private readonly DecodedInstruction[] m_DecodeEntry = new DecodedInstruction[1 << DecodeCacheBits];
        private readonly Action<DecodedInstruction>[] m_DecodeHandler = new Action<DecodedInstruction>[1 << DecodeCacheBits]; // [cer64plus] handler cached with the entry

        // [cer64plus] called after a savestate load
        public virtual void InvalidateDecodeCache() { Array.Clear(m_DecodeAddr); }

        /// <summary>[cer64plus] Handler for a freshly decoded instruction (cached with it); null when the core has none.</summary>
        protected virtual Action<DecodedInstruction> LookupHandler(in DecodedInstruction decoded) => null;

        /// <summary>[cer64plus] Like Decode, but returns the cache entry by reference plus its handler: no struct copies, no table lookup.</summary>
        protected ref readonly DecodedInstruction DecodeCached(long physicalAddress, out Action<DecodedInstruction> handler)
        {
            return ref DecodeCached(physicalAddress, out handler, out _);
        }

        protected ref readonly DecodedInstruction DecodeCached(long physicalAddress, out Action<DecodedInstruction> handler, out int cacheSlot)
        {
            uint raw = FetchInstruction(physicalAddress);
            int slot = (int)((physicalAddress >> 2) & ((1 << DecodeCacheBits) - 1));
            if (m_DecodeRaw[slot] != raw || m_DecodeAddr[slot] != physicalAddress + 1) {
                var decode = Disassembler.Decode(raw);
                m_DecodeRaw[slot] = raw;
                m_DecodeAddr[slot] = physicalAddress + 1;
                m_DecodeEntry[slot] = decode;
                m_DecodeHandler[slot] = decode.IsInvalid ? null : LookupHandler(decode);
            }

            if (InBootMode)
            {
                if (m_ProgramStart != 0 && m_Pc == m_ProgramStart)
                {
                    EntryPointHit();
                }
            }

            handler = m_DecodeHandler[slot];
            cacheSlot = slot;
            return ref m_DecodeEntry[slot];
        }

        protected DecodedInstruction Decode(long physicalAddress)
        {
            uint raw = FetchInstruction(physicalAddress);
            int slot = (int)((physicalAddress >> 2) & ((1 << DecodeCacheBits) - 1));
            DecodedInstruction decode;
            if (m_DecodeRaw[slot] == raw && m_DecodeAddr[slot] == physicalAddress + 1) {
                decode = m_DecodeEntry[slot];
            }
            else {
                decode = Disassembler.Decode(raw);
                m_DecodeRaw[slot] = raw;
                m_DecodeAddr[slot] = physicalAddress + 1; // +1 so the zeroed array never matches address 0
                m_DecodeEntry[slot] = decode;
                m_DecodeHandler[slot] = decode.IsInvalid ? null : LookupHandler(decode);
            }

            if (InBootMode)
            {
                if (m_ProgramStart != 0 && m_Pc == m_ProgramStart)
                {
                    EntryPointHit();
                }
            }

            return decode;
        }

        protected void TraceInstruction(long address, DecodedInstruction decode)
        {
            if (TraceMode != ProgramTrace.TraceMode.None && decode.Op.Family != OperationFamily.Invalid && IsTraceActive)
            {
                TraceLog.AppendInstruction(address, decode);
                TraceStep?.Invoke(decode);
            }
        }

        protected void TraceInterrupt() {
            if (TraceMode != ProgramTrace.TraceMode.None && IsTraceActive)
            {
                TraceLog.Interrupt();
            }
        }

        protected void TraceMemoryHit(ulong address, bool isWrite, String val)
        {
            // TODO: Trace append mode
            if (IsMemTraceActive)
                TraceLog.TraceMemoryAccess(address, isWrite, val);

            // if (core_InstDebugMode != InstructionDebugMode.None) {
            //     if (m_MemTraceFirstOne || !m_MemTraceAppendMode || core_MemAccessNote == null)
            //         core_MemAccessNote = new MemoryAccessMeta(address, isWrite, val);
            //     else
            //         core_MemAccessNote.AppendValue(val);
            // }

            // m_MemTraceFirstOne = false;
        }

        public virtual String GetGPRName(int i) {
            return i.ToString();
        }

        protected bool ComputeBranchCondition(bool isDword, ulong source, ulong target, RegBoundType copSelect,  ArithmeticOp compareOp)
        {
            bool EQ_ZERO()
            {
                return source == 0;
            }

            bool GT_ZERO()
            {
                if (isDword)
                {
                    ulong v = source;
                    return (v >> 63) == 0 && v > 0;
                }
                else
                {
                    uint v = (uint)source;
                    return (v >> 31) == 0 && v > 0;
                }
            }

            bool LT_ZERO()
            {
                if (isDword)
                {
                    ulong v = source;
                    return (v >> 63) == 1 && v > 0;
                }
                else
                {
                    uint v = (uint)source;
                    return (v >> 31) == 1 && v > 0;
                }
            }

            bool COP_FLAG(bool compare)
            {
                switch (copSelect)
                {
                    case RegBoundType.Cp1:
                        {
                            var cond = State.FCR.Condition;

                            if (!compare)
                            {
                                /* Expecting FALSE condition */
                                return !cond && true;
                            }
                            else
                            {
                                /* Expecting TRUE condition */
                                return cond && true;
                            }
                        }

                    default: throw new NotSupportedException("MIPS does not support this kind of unit: " + copSelect.ToString());
                }
            }

            bool condition = compareOp switch
            {
                ArithmeticOp.EQUAL => source == target,
                ArithmeticOp.NOT_EQUAL => source != target,
                ArithmeticOp.GREATER_THAN => GT_ZERO(),
                ArithmeticOp.LESS_THAN => LT_ZERO(),
                ArithmeticOp.GREATER_THAN_OR_EQUAL => EQ_ZERO() || GT_ZERO(),
                ArithmeticOp.LESS_THAN_OR_EQUAL => EQ_ZERO() || LT_ZERO(),
                ArithmeticOp.FALSE => COP_FLAG(false),
                ArithmeticOp.TRUE => COP_FLAG(true),
                _ => throw new NotSupportedException("MIPS does not support this branch operation"),
            };
            return condition;
        }

        public static void RegTransferGprHelper(DecodedInstruction inst, out int gprSource, out int gprTarget)
        {
            gprSource = inst.Target;
            gprTarget = inst.Destination;

            if (inst.Op.XferSource == RegBoundType.Gpr)
            {
                switch (inst.Op.OperandFmt)
                {
                    default: break;
                    case OperandType.R_S: gprSource = inst.Source; break;
                }
            }

            if (inst.Op.XferTarget == RegBoundType.Gpr)
            {
                // RSP: CP0 -> GPR
                if (inst.Op.OperandFmt == OperandType.RspCp0_CT) {
                    gprSource = inst.Destination;
                    gprTarget = inst.Target;
                    return;
                }

                // RSP: GPR -> CP0
                if (inst.Op.OperandFmt == OperandType.RspCp0_TC) {
                    gprSource = inst.Target;
                    gprTarget = inst.Destination;
                    return;
                }

                switch (inst.Op.OperandFmt)
                {
                    default: break;
                    case OperandType.R_D: gprTarget = inst.Destination; break;
                    case OperandType.Cop0_CT:
                    case OperandType.Cop0_TC:
                    case OperandType.Cop1_CT:
                    case OperandType.Cop1_TC:
                    case OperandType.VU_CT: // RSP V[e] -> GPR
                    case OperandType.VU_TC: // GPR -> RSP V[e]
                    case OperandType.VU_FromCtrl:
                    case OperandType.VU_ToCtrl:
                    case OperandType.Cop1_FromCtrl:
                    case OperandType.Cop1_ToCtrl:
                        {
                            gprSource = inst.Target;
                            gprTarget = inst.Target;
                            break;
                        }
                }
            }
        }

        public virtual String Description
        {
            get {
                return this.ToString();
            }
        }

        public BaseDisassembler Disassembler { get; }

        public ProgramTrace TraceLog { get; }

        public abstract ExecutionState State { get; }


        public bool DebugMode { get; private set; }

        protected InstructionDebugMode core_InstDebugMode;

        protected MemoryAccessMeta core_MemAccessNote;

        public void SetInstructionDebugMode(InstructionDebugMode mode)
        {
            core_InstDebugMode = mode;
        }

        public bool InBootMode { get; protected set; } = true;

        public ProgramTrace.TraceMode TraceMode { get; private set; }

        public InstructionDebugMode InstDebugMode => core_InstDebugMode;

        public bool IsTraceActive => TraceMode == ProgramTrace.TraceMode.Full || (TraceMode == ProgramTrace.TraceMode.ProgramOnly && !InBootMode);

        public bool IsMemTraceActive => IsTraceActive && ((TraceLog.Details & ProgramTrace.TraceDetails.MemoryAccess) == ProgramTrace.TraceDetails.MemoryAccess);

        protected bool EnableCp0 { get; set; } = true;

        protected bool EnableCp1 { get; set; } = true;

        protected bool EnableCp2 { get; set; } = true;
    }
}
