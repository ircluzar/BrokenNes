// [dbg_exec] Execution side of the debugger (BrokenNes pause/continue/step/run_until, breakpoint_add/remove/list,
// call_stack, trace, trace_frame). Implemented by N64Debugger over an N64Session. While the debugger is enabled the
// system runs N64System.DebugTickBatch (interpreter, one instruction at a time, exact stops); while it is disabled
// the only cost is one static flag test per batch (DebugHooks.Active) and one per RSP task start.
using System;
using System.Collections.Generic;

namespace cor64.Debug
{
    public enum BreakpointKind { Exec, Read, Write }

    public sealed class Breakpoint
    {
        public int Id { get; internal set; }
        /// <summary>"cpu" or "rsp" (Read/Write: cpu only).</summary>
        public string Cpu { get; internal set; }
        public BreakpointKind Kind { get; internal set; }
        /// <summary>Exec: CPU VA (sign-extended 32-bit compare) or RSP IMEM offset. Read/Write: as for AddWatch.</summary>
        public ulong Address { get; internal set; }
        public int Length { get; internal set; } = 4;
        /// <summary>Optional register condition, e.g. "a0 == 0x10 &amp;&amp; sp != 0" (ops == != &lt; &gt; &lt;= &gt;=,
        /// operands are register names or numbers). Null: always.</summary>
        public string Condition { get; internal set; }
        /// <summary>Read/Write: value test on the accessed value (see WatchCondition).</summary>
        public WatchCondition ValueCondition { get; internal set; }
        public ulong Value { get; internal set; }
        public ulong Mask { get; internal set; } = ulong.MaxValue;
        public bool Enabled { get; set; } = true;
        public long HitCount { get; internal set; }
        internal int WatchId = -1;
        internal Func<bool> CompiledCondition;
        public override string ToString() => $"#{Id} {Cpu} {Kind} {(uint)Address:X8}{(Condition != null ? " if " + Condition : "")} hits={HitCount}";
    }

    public enum StopReason { None, Paused, Step, Breakpoint, RunUntil, StepOver, Watch, FrameLimit }

    public readonly struct StopInfo
    {
        public readonly StopReason Reason; public readonly string Cpu; public readonly ulong Pc; public readonly int BreakpointId;
        public readonly long CpuSteps;
        public StopInfo(StopReason r, string cpu, ulong pc, int bp, long steps) { Reason = r; Cpu = cpu; Pc = pc; BreakpointId = bp; CpuSteps = steps; }
        public override string ToString() => $"{Reason} {Cpu} pc={(uint)Pc:X8}{(BreakpointId > 0 ? " bp#" + BreakpointId : "")}";
    }

    public readonly struct CallFrame
    {
        /// <summary>Address of the jal/jalr; Target = callee entry; Return = call site + 8; Sp at the call.</summary>
        public readonly ulong CallSite, Target, Return, Sp; public readonly string Label;
        public CallFrame(ulong site, ulong target, ulong ret, ulong sp, string label) { CallSite = site; Target = target; Return = ret; Sp = sp; Label = label; }
        public override string ToString() => $"{(uint)Target:X8} {Label} (from {(uint)CallSite:X8})";
    }

    public interface IDebugExecution
    {
        /// <summary>Debug mode on/off. On: interpreted, exact stops; off: zero cost (DebugHooks.Active false).</summary>
        bool Enabled { get; set; }
        bool IsPaused { get; }
        StopInfo LastStop { get; }

        /// <summary>Stops at the next instruction boundary (RunFrame returns early while paused).</summary>
        void Pause();
        /// <summary>Unpauses; execution continues on the next RunFrame. The breakpoint at the current PC is stepped over.</summary>
        void Resume();
        /// <summary>Runs exactly n instructions of cpu ("cpu" or "rsp"), then pauses. Synchronous (drives RunFrame).</summary>
        StopInfo Step(string cpu, int n = 1, int maxFrames = 600);
        /// <summary>CPU: a jal/jalr/bal (with its delay slot) runs to the return address; anything else is Step 1.</summary>
        StopInfo StepOver(int maxFrames = 600);
        /// <summary>Runs until cpu reaches pc (or another breakpoint stops it first).</summary>
        StopInfo RunUntil(string cpu, ulong pc, int maxFrames = 600);
        /// <summary>Runs frames until a stop (breakpoint, watch) or maxFrames.</summary>
        StopInfo Continue(int maxFrames = 600);

        /// <summary>Exec: cpu "cpu"/"rsp". Read/Write: cpu only, built on the dbg_mem watches (needs COR64_DBG_WATCH=1;
        /// returns -1 without it or if the VA does not translate).</summary>
        int AddBreakpoint(string cpu, BreakpointKind kind, ulong address, string condition = null, int length = 4,
            WatchCondition valueCondition = WatchCondition.Any, ulong value = 0, ulong mask = ulong.MaxValue);
        bool RemoveBreakpoint(int id);
        IReadOnlyList<Breakpoint> ListBreakpoints();

        /// <summary>jal/jalr -> jr ra shadow stack (kept only while Enabled), innermost last, labelled from Symbols.</summary>
        IReadOnlyList<CallFrame> CallStack(string cpu = "cpu");
    }

    public readonly struct TraceEntry
    {
        public readonly string Cpu; public readonly ulong Pc; public readonly uint Word;
        /// <summary>GPRs changed by the instruction: (index, new value).</summary>
        public readonly (int reg, ulong value)[] Deltas;
        public TraceEntry(string cpu, ulong pc, uint word, (int, ulong)[] d) { Cpu = cpu; Pc = pc; Word = word; Deltas = d; }
        public override string ToString()
        {
            var s = $"{(uint)Pc:X8}: {Word:X8}";
            foreach (var (r, v) in Deltas) s += $" {MipsNames.Gpr[r]}={v:X}";
            return s;
        }
    }

    public sealed class RspTaskRecord
    {
        public long Index; public long CpuTicks;
        /// <summary>True for a graphics task run by the HLE graphics device (the RSP itself does not run it).</summary>
        public bool Hle;
        /// <summary>OSTask fields from DMEM 0xFC0 (type, flags, ucode, ucode size, ucode data, data size).</summary>
        public uint Type, Flags, Ucode, UcodeSize, UcodeData, UcodeDataSize;
        /// <summary>CRC-32 of the ucode text in RDRAM (Ucode, min(UcodeSize, 0x800) bytes: UcodeTable.TextCrc).</summary>
        public uint UcodeCrc;
        public byte[] Dmem;
        /// <summary>[dbg_rsp] The microcode identified (UcodeTable.Identify) and the full OSTask header.</summary>
        public UcodeInfo UcodeId;
        public OSTaskInfo Task;
        public string UcodeName => UcodeId?.Name;
        public override string ToString() => $"task {Index}{(Hle ? " hle" : "")} type={Type} ucode={Ucode:X8}+{UcodeSize:X} crc={UcodeCrc:X8} id={UcodeName}";
    }

    public interface IDebugTrace
    {
        /// <summary>Starts recording each executed instruction of cpu into a ring of ringSize entries (turns Enabled on).</summary>
        void StartTrace(string cpu, int ringSize = 4096);
        /// <summary>Stops the trace of cpu and returns its entries, oldest first.</summary>
        IReadOnlyList<TraceEntry> StopTrace(string cpu);
        /// <summary>Traces one whole frame (StartTrace, RunFrame, StopTrace).</summary>
        IReadOnlyList<TraceEntry> TraceFrame(string cpu, int ringSize = 1 << 20);
        /// <summary>Records each RSP task start (ucode CRC, DMEM snapshot), keeping the last max tasks.</summary>
        void StartTaskLog(int max = 64);
        IReadOnlyList<RspTaskRecord> StopTaskLog();
        IReadOnlyList<RspTaskRecord> TaskLog { get; }
    }
}
