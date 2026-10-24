using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NesEmulator.Debugging.Checks;

/// <summary>
/// The part of the hardware checks every console shares: findings (one per rule and place, with the first frame, PC, symbol, call stack and
/// count), frames and lag frames, the profiler (cycles per frame, per function inclusive and exclusive, vblank time used for uploads, DMA
/// bytes) and the stack-collision bookkeeping. A console's rules (<see cref="NesChecks"/>, <see cref="SnesChecks"/>, <see cref="GbChecks"/>)
/// derive from it. It is a <see cref="DebugObserver"/>: added to a <see cref="Debugger"/>, it arms the hooks; removed, it costs nothing.
/// </summary>
public abstract class ConsoleChecks : DebugObserver
{
    protected ConsoleChecks(Debugger debugger, CheckOptions options)
    {
        Dbg = debugger; M = debugger.Machine; Main = M.Main; Opt = options;
        topLevel = new FunctionStat("(top level)");
        bucket = topLevel;
    }

    public Debugger Dbg { get; }
    public DebugMachine M { get; }
    public DebugCpu Main { get; }
    public CheckOptions Opt { get; }
    public abstract string Family { get; }

    // ======================================================================================== findings

    private readonly Dictionary<string, Finding> findings = new(StringComparer.Ordinal);
    public IReadOnlyCollection<Finding> Findings => findings.Values;

    /// <summary>Records one occurrence of a rule at a place. The first occurrence keeps its frame, PC, symbol and call stack.</summary>
    protected Finding? Report(string rule, string place, string address, DebugCpu cpu, uint pc, string detail, long frame = -1)
    {
        if (!Opt.IsOn(rule)) return null;
        string key = rule + "|" + place;
        if (findings.TryGetValue(key, out var f)) { f.Count++; return f; }
        f = new Finding
        {
            Rule = rule, Place = place, Address = address, FirstFrame = frame >= 0 ? frame : M.Frame, FirstScanline = M.Scanline, Pc = pc,
            PcSymbol = Dbg.SymbolAt(cpu, pc), CallStack = StackText(cpu), Count = 1, Detail = detail,
        };
        findings[key] = f;
        return f;
    }

    /// <summary>A memory rule's place: the variable's symbol (its base name) or the address as the canonical memory names it.</summary>
    protected string MemoryPlace(DebugCpu cpu, uint cpuAddress, string addressText)
    {
        string? s = Symbols ? Dbg.SymbolAt(cpu, cpuAddress) : null;
        return s != null ? AcceptedFinding.BaseName(s) : addressText;
    }

    /// <summary>A code rule's place: the function the PC is in (its symbol, else the function the call stack entered), or the PC.</summary>
    protected string CodePlace(DebugCpu cpu, uint pc)
    {
        if (Symbols)
        {
            string? s = Dbg.SymbolAt(cpu, pc);
            if (s != null) return AcceptedFinding.BaseName(s);
            var st = cpu.State.Stack;
            for (int i = st.Count - 1; i >= 0; i--) { string? t = Dbg.SymbolAt(cpu, st[i].Target); if (t != null) return AcceptedFinding.BaseName(t); }
        }
        return cpu.FormatAddress(pc);
    }

    protected bool Symbols => Dbg.Symbols.All.Count > 0;

    private string StackText(DebugCpu cpu)
    {
        var st = cpu.State.Stack;
        if (st.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = Math.Max(0, st.Count - 8); i < st.Count; i++)
        {
            if (sb.Length > 0) sb.Append(" > ");
            string? t = Symbols ? Dbg.SymbolAt(cpu, st[i].Target) : null;
            sb.Append(st[i].Kind == "JSR" || st[i].Kind == "JSL" || st[i].Kind == "CALL" ? "" : st[i].Kind + " ").Append(t != null ? AcceptedFinding.BaseName(t) : cpu.FormatAddress(st[i].Target));
        }
        return sb.ToString();
    }

    /// <summary>Called once when the run ends (pending states become findings, the last frame is closed).</summary>
    public virtual void Finish()
    {
        if (haveStep) CloseFrame(M.Cycle, partial: true);
    }

    // ======================================================================================== the observer plumbing

    public override bool WantsSteps(int cpuIndex) => cpuIndex == 0 || WantsOtherCpu(cpuIndex);
    public override bool WantsAccesses(int cpuIndex) => cpuIndex == 0 || WantsOtherCpu(cpuIndex);
    protected virtual bool WantsOtherCpu(int cpuIndex) => false;

    // the main CPU's step state
    private bool haveStep, lastIdle;
    private long lastCycle;
    private uint lastPc;
    private FlowKind lastFlow;
    /// <summary>Interrupt frames on the main CPU's call stack (handlers count neither as waiting nor as the main program's work).</summary>
    protected int InterruptDepth { get; private set; }

    public sealed override void OnStep(DebugCpu cpu, long cycle, bool executes, string? interrupt)
    {
        if (cpu.Index != 0) { OtherCpuStep(cpu, cycle, executes, interrupt); return; }
        if (haveStep)
        {
            long d = cycle - lastCycle;
            if (d < 0 || d > 10_000_000) d = 0;
            frameCycles += d;
            if (lastIdle) frameIdle += d;
            bucket.Exclusive += d;
        }
        else { frameStartCycle = cycle; frameNumber = M.Frame; }
        lastCycle = cycle; haveStep = true;
        if (FrameBoundary()) { CloseFrame(cycle, partial: false); }

        uint pc = cpu.PC;
        MainStep(cpu, cycle, executes, interrupt, pc);

        if (executes)
        {
            // spin detection: a backward branch or jump to the same place twice with no write in between (stack writes aside)
            if (InterruptDepth == 0 && (lastFlow is FlowKind.Branch or FlowKind.Jump) && pc < lastPc && lastPc - pc <= 64)
            {
                spinning = spinHead == pc && writesSinceJump == 0;
                spinHead = pc; writesSinceJump = 0;
            }
            uint bytes = (uint)(cpu.Peek(pc) | cpu.Peek(cpu.NextAddress(pc, 1)) << 8);
            lastFlow = cpu.Classify(bytes).Flow;
            lastPc = pc;
        }
        lastIdle = InterruptDepth == 0 && (spinning || MainWaiting(cpu, executes) || InFrameWait(cpu, pc));
    }

    /// <summary>The main CPU's step for the console's rules (after the frame bookkeeping).</summary>
    protected abstract void MainStep(DebugCpu cpu, long cycle, bool executes, string? interrupt, uint pc);
    protected virtual void OtherCpuStep(DebugCpu cpu, long cycle, bool executes, string? interrupt) { }

    /// <summary>True on the first step after vblank started (the console's frame boundary).</summary>
    protected abstract bool FrameBoundary();

    /// <summary>The main CPU waits in hardware (WAI, HALT) at this step.</summary>
    protected virtual bool MainWaiting(DebugCpu cpu, bool executes) => false;

    public override void OnCall(DebugCpu cpu, CallFrame frame)
    {
        if (cpu.Index != 0) return;
        bool irq = IsInterrupt(frame.Kind);
        if (irq) InterruptDepth++;
        if (!Opt.Profile && !irq) return;
        var fs = FunctionFor(cpu, frame.Target);
        fs.Calls++;
        callStack.Add((fs, M.Cycle, bucket, irq));
        bucket = fs;
    }

    public override void OnReturn(DebugCpu cpu, CallFrame frame)
    {
        if (cpu.Index != 0) return;
        bool irq = IsInterrupt(frame.Kind);
        if (irq && InterruptDepth > 0) InterruptDepth--;
        if (callStack.Count == 0) return;
        var (fs, entry, outer, _) = callStack[^1];
        callStack.RemoveAt(callStack.Count - 1);
        long d = M.Cycle - entry;
        if (d > 0) fs.Inclusive += d;
        bucket = outer;
    }

    protected static bool IsInterrupt(string kind) => kind is not ("JSR" or "JSL" or "CALL" or "RST" or "BRK" or "COP" or "TCALL" or "PCALL");

    // ======================================================================================== waiting and lag frames

    private bool spinning;
    private uint spinHead = uint.MaxValue;
    private int writesSinceJump;

    /// <summary>A write by the main program that is not to its stack: whatever loop it is in, it is working, not waiting.</summary>
    protected void NoteMainWrite()
    {
        if (InterruptDepth != 0) return;
        writesSinceJump++;
        spinning = false;
    }

    /// <summary>In one of the manifest's frame-wait functions (their symbols' extents, by canonical address: a banked function only in its bank).</summary>
    private bool InFrameWait(DebugCpu cpu, uint pc)
    {
        if (Opt.FrameWait.Count == 0 || !Symbols) return false;
        if (waitRanges == null)
        {
            waitRanges = new List<SpaceRange>();
            var globs = Opt.FrameWait.Select(AcceptedFinding.Glob).ToList();
            foreach (var sym in Dbg.Symbols.All)
                if (globs.Any(g => g.IsMatch(AcceptedFinding.BaseName(sym.Name)) || g.IsMatch(sym.Name)) && cpu.BindSymbol(sym, out var k))
                    waitRanges.Add(new SpaceRange(k.Space, k.Offset, k.Offset + (uint)Math.Max(1, sym.Size) - 1));
        }
        if (waitRanges.Count == 0) return false;
        Span<SpaceKey> keys = stackalloc SpaceKey[4];
        int n = cpu.AccessKeys(pc, false, keys);
        for (int i = 0; i < n; i++) foreach (var r in waitRanges) if (r.Contains(keys[i])) return true;
        return false;
    }
    private List<SpaceRange>? waitRanges;

    // ======================================================================================== frames and the profile

    public sealed class FrameStat
    {
        public long Frame; public long Cycles; public long Busy; public bool Lag; public long UploadCycles = -1; public long DmaBytes;
    }

    private readonly List<FrameStat> frames = new();
    public IReadOnlyList<FrameStat> Frames => frames;
    private long frameStartCycle, frameCycles, frameIdle, frameDma, vblankStartCycle = -1, lastUpload = -1, frameNumber;

    /// <summary>The cycles of one whole frame on this console (in <see cref="DebugMachine.Cycle"/> units), for the percentages.</summary>
    public abstract double NominalFrameCycles { get; }
    /// <summary>The cycles vblank lasts (the time uploads have).</summary>
    public abstract double VblankCycles { get; }
    public abstract string CycleUnit { get; }

    private void CloseFrame(long cycle, bool partial)
    {
        if (frameCycles > 0)
        {
            bool lag = !partial && !lastIdle && frames.Count > 0;
            if (lag) LagFrame(frameNumber);
            if (!partial) OnFrameEnd(lastPc);
            frames.Add(new FrameStat
            {
                Frame = frameNumber, Cycles = frameCycles, Busy = frameCycles - frameIdle, Lag = lag,
                UploadCycles = vblankStartCycle >= 0 && lastUpload >= vblankStartCycle ? lastUpload - vblankStartCycle : -1, DmaBytes = frameDma,
            });
        }
        frameCycles = 0; frameIdle = 0; frameDma = 0; lastUpload = -1;
        vblankStartCycle = cycle; frameStartCycle = cycle; frameNumber = M.Frame;
    }

    /// <summary>At each frame boundary (vblank started), with the main program's last PC: the console's once-a-frame checks.</summary>
    protected virtual void OnFrameEnd(uint mainPc) { }

    /// <summary>Vblank arrived while the main program was working (not in a spin, WAI, HALT or a declared wait).</summary>
    private void LagFrame(long frame)
    {
        // the place: the function the main program was in when the frame ran out
        Report(Family + ".lag-frame", CodePlace(Main, lastPc), "", Main, lastPc, "the main program had not reached its frame wait when vblank arrived", frame);
    }

    /// <summary>A write to the picture's memory (or a DMA to it) at <paramref name="cycle"/>: the vblank upload time ends there.</summary>
    protected void NoteUpload(long cycle) { if (vblankStartCycle >= 0 && InVblank()) lastUpload = cycle; }
    protected abstract bool InVblank();
    protected void NoteDma(long bytes) => frameDma += bytes;

    // per-function cycles
    public sealed class FunctionStat
    {
        public FunctionStat(string name) { Name = name; }
        public string Name { get; }
        public long Exclusive, Inclusive, Calls;
    }

    private readonly FunctionStat topLevel;
    private FunctionStat bucket;
    private readonly Dictionary<ulong, FunctionStat> functions = new();
    private readonly Dictionary<string, FunctionStat> functionsByName = new(StringComparer.Ordinal);
    private readonly List<(FunctionStat fs, long entry, FunctionStat outer, bool irq)> callStack = new();

    public IEnumerable<FunctionStat> Functions => functionsByName.Values.Append(topLevel);

    private FunctionStat FunctionFor(DebugCpu cpu, uint target)
    {
        Span<SpaceKey> keys = stackalloc SpaceKey[4];
        int n = cpu.AccessKeys(target, false, keys);
        var k = keys[n - 1];
        ulong key = (ulong)(uint)k.Space << 32 | k.Offset;
        if (functions.TryGetValue(key, out var fs)) return fs;
        string? s = Symbols ? Dbg.SymbolAt(cpu, target) : null;
        string name = s != null ? AcceptedFinding.BaseName(s) : cpu.FormatAddress(target);
        if (!functionsByName.TryGetValue(name, out fs)) functionsByName[name] = fs = new FunctionStat(name);
        functions[key] = fs;
        return fs;
    }

    // ======================================================================================== stack collisions

    /// <summary>
    /// Stack-overflow bookkeeping over one RAM region (by canonical offset): a byte read as data while it lay below the stack pointer, later
    /// pushed onto (the stack grew into the data), or a byte written as data while it lay inside the live stack (the data overwrote the stack).
    /// </summary>
    protected sealed class StackCollisions
    {
        private readonly byte[] state;   // bit 0: read as data below S; bit 1: used by the stack; bit 2: reported
        public uint Top;                  // the highest stack byte seen (the live stack is S+1..Top)
        public uint Lowest = uint.MaxValue;
        public StackCollisions(int size) { state = new byte[size]; }

        /// <summary>Returns true when this access is a collision (reported once per byte).</summary>
        public bool Data(uint offset, bool write, uint sp)
        {
            if (offset >= state.Length) return false;
            if (!write) { if (offset <= sp) state[offset] |= 1; return false; }
            if (offset > sp && offset <= Top && (state[offset] & 2) != 0 && (state[offset] & 4) == 0) { state[offset] |= 4; return true; }
            return false;
        }

        public bool Stack(uint offset, bool write)
        {
            if (offset >= state.Length) return false;
            if (offset > Top || Top == 0) Top = Math.Max(Top, offset);
            if (offset < Lowest) Lowest = offset;
            state[offset] |= 2;
            if (write && (state[offset] & 1) != 0 && (state[offset] & 4) == 0) { state[offset] |= 4; return true; }
            return false;
        }

        /// <summary>The gap between the deepest stack byte and the nearest byte read as data below it (or the region's start).</summary>
        public long Headroom(uint regionStart)
        {
            if (Lowest == uint.MaxValue) return -1;
            for (long a = (long)Lowest - 1; a >= regionStart; a--) if ((state[a] & 1) != 0) return Lowest - 1 - a;
            return Lowest - regionStart;
        }
    }

    // ======================================================================================== the report

    /// <summary>Memory the game wrote (for the budgets' headroom): name, size, bytes written at least once.</summary>
    public abstract IEnumerable<(string name, long size, long written)> MemoryUse();

    /// <summary>The stack: the deepest it went and the bytes free below it (-1: unknown).</summary>
    public abstract (long deepest, long free) StackUse();
}

/// <summary>A bitmap of bytes written (for the read-before-written rules and the headroom budgets).</summary>
public sealed class WrittenMap
{
    private readonly ulong[] bits;
    public int Size { get; }
    public WrittenMap(int size) { Size = size; bits = new ulong[(size + 63) / 64]; }
    public bool this[long i] => i >= 0 && i < Size && (bits[i >> 6] & 1UL << (int)(i & 63)) != 0;
    public void Set(long i) { if (i >= 0 && i < Size) bits[i >> 6] |= 1UL << (int)(i & 63); }
    public void SetRange(long from, long count) { for (long i = from; i < from + count; i++) Set(i); }
    public long Count() { long n = 0; foreach (var b in bits) n += System.Numerics.BitOperations.PopCount(b); return n; }
}
