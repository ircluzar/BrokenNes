using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace NesEmulator.Debugging;

/// <summary>
/// The BrokenNes debugger engine (milestone D1 of docs/projects/brokendev-debugging-plan.md): breakpoints on execute, read and write
/// (addresses, ranges, symbols, conditions, hit counts, tracepoints), watches that see every mirror and every DMA byte, stepping
/// (instruction, over, out, scanline, frame, run to an address / line / frame), a call stack, a trace ring and filtered trace files, and
/// symbols. One engine for every console; a <see cref="DebugMachine"/> adapts it to the NES, the SNES (65816 + SPC700, also the
/// WideMess) and the Game Boy.
///
/// <para><b>Zero cost when off.</b> The engine arms the machine's hooks (<see cref="DebugMachine.ApplyHooks"/>) only while something
/// needs them: a breakpoint, a watch, a step, a pause, a trace or <see cref="TrackCallStack"/>. With none of these the boards' hook fields are
/// null, and every core runs the code it ran before the debugger existed.</para>
///
/// <para><b>Breaking parks the emulation thread</b> inside the hook, at an instruction boundary (a watch breaks after the instruction that
/// made the access), until <see cref="Continue"/> or a step. The run loops are untouched, so a game runs exactly the same with the
/// debugger attached, breaking and stepping, as without it. Whoever drives the debugger reads the machine while it is parked (or between
/// frames); <see cref="DebugRunner"/> is the headless driver, the desktop app drives it over HTTP.</para>
/// </summary>
public sealed class Debugger
{
    public Debugger(DebugMachine machine)
    {
        Machine = machine;
        machine.Debugger = this;
        for (int i = 0; i < machine.Cpus.Count; i++) machine.Cpus[i].Index = i;
        armed = Armed.Empty(machine.Cpus.Count);
        hooked = new bool[machine.Cpus.Count];
    }

    public DebugMachine Machine { get; }
    public SymbolTable Symbols { get; } = new();
    /// <summary>Tracepoint lines and notices go here (Console.Out in Workshop's runner); null: kept in <see cref="Hits"/> only.</summary>
    public TextWriter? Log { get; set; }
    /// <summary>A lock the emulation thread may hold while it runs frames (the desktop app's emulation lock): released while parked, so the app stays responsive.</summary>
    public object? HostLock { get; set; }
    /// <summary>Instructions the trace ring keeps per CPU.</summary>
    public int TraceRingSize { get; set; } = 16384;

    private readonly object sync = new();
    private readonly List<Breakpoint> bps = new();
    private int nextId = 1;
    private volatile Armed armed;
    private readonly bool[] hooked;
    private bool trackCallStack, traceRing;

    // ---------------------------------------------------------------- the breakpoints

    /// <summary>
    /// Adds a breakpoint. <paramref name="spec"/>: <c>$8000</c>, <c>$8000-$80FF</c>, <c>7E:0010</c> (bank:address), <c>wram:$10-$1F</c>
    /// (a named space), <c>player_hp</c> (a symbol: its whole size for a read / write watch), <c>player_hp+1</c>. A CPU address matches every
    /// mirror of the place it reaches. <paramref name="hits"/>: "3" or "&gt;=3" (from the 3rd hit on), "==3" (only the 3rd), "%3" (every 3rd).
    /// A tracepoint logs (<paramref name="log"/>: text with {expressions}) and keeps running.
    /// </summary>
    public Breakpoint Add(string spec, AccessKind kind, string? condition = null, string? hits = null, bool tracepoint = false, string? log = null, string cpu = "main", bool temporary = false)
    {
        var c = Machine.Cpu(cpu) ?? throw new ArgumentException($"no CPU '{cpu}' on this {Machine.Console} (it has: {string.Join(", ", Machine.Cpus.Select(x => x.Id))})");
        if (kind == AccessKind.None) throw new ArgumentException("a breakpoint needs exec, read and/or write");
        var ranges = Resolve(c, spec, kind, out string resolved);
        var bp = new Breakpoint { CpuId = c.Id, Kind = kind, Spec = spec.Trim(), Ranges = ranges, Resolved = resolved, Tracepoint = tracepoint, Temporary = temporary };
        if (!string.IsNullOrWhiteSpace(condition)) { bp.Condition = condition.Trim(); bp.Cond = DebugExpr.Compile(bp.Condition, c); }
        if (!string.IsNullOrWhiteSpace(hits)) (bp.HitMode, bp.HitTarget) = ParseHits(hits!);
        if (log != null) { bp.LogFormat = log; bp.LogParts = ParseLog(log, c); }
        lock (sync) { bp.Id = nextId++; bps.Add(bp); }
        Rearm();
        return bp;
    }

    public bool Remove(int id)
    {
        bool ok;
        lock (sync) ok = bps.RemoveAll(b => b.Id == id) > 0;
        Rearm();
        return ok;
    }

    public void ClearBreakpoints() { lock (sync) bps.Clear(); Rearm(); }

    public bool SetEnabled(int id, bool on)
    {
        bool ok = false;
        lock (sync) foreach (var b in bps) if (b.Id == id) { b.Enabled = on; ok = true; }
        Rearm();
        return ok;
    }

    public IReadOnlyList<Breakpoint> Breakpoints { get { lock (sync) return bps.ToArray(); } }

    private static (HitMode, long) ParseHits(string h)
    {
        h = h.Trim();
        HitMode m = HitMode.AtLeast;
        if (h.StartsWith("==")) { m = HitMode.Exactly; h = h[2..]; }
        else if (h.StartsWith(">=")) h = h[2..];
        else if (h.StartsWith("%")) { m = HitMode.Every; h = h[1..]; }
        if (!long.TryParse(h.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) || n < 1) throw new FormatException($"hit count '{h}' is not a positive number");
        return (m, n);
    }

    private static List<(string, DebugExpr?)> ParseLog(string log, DebugCpu cpu)
    {
        var parts = new List<(string, DebugExpr?)>();
        int i = 0;
        while (i < log.Length)
        {
            int open = log.IndexOf('{', i);
            if (open < 0) { parts.Add((log[i..], null)); break; }
            if (open > i) parts.Add((log[i..open], null));
            int close = log.IndexOf('}', open);
            if (close < 0) throw new FormatException($"unclosed '{{' in '{log}'");
            string text = log[(open + 1)..close];
            parts.Add((text, DebugExpr.Compile(text, cpu)));
            i = close + 1;
        }
        return parts;
    }

    // ---------------------------------------------------------------- address specs

    /// <summary>Resolves an address spec on a CPU to canonical ranges (see <see cref="Add"/>), with this debugger's symbols.</summary>
    public List<SpaceRange> Resolve(DebugCpu cpu, string spec, AccessKind kind, out string resolved) => ResolveSpec(cpu, Symbols, spec, kind, out resolved);

    /// <summary>Resolves an address spec without a debugger (a tool that only reads memory): <paramref name="symbols"/> may be empty.</summary>
    public static List<SpaceRange> ResolveSpec(DebugCpu cpu, SymbolTable symbols, string spec, AccessKind kind, out string resolved)
    {
        spec = spec.Trim();
        if (spec.Length == 0) throw new FormatException("empty address");
        int explicitSpace = -1;
        string body = spec;
        int colon = spec.IndexOf(':');
        if (colon > 0)
        {
            string head = spec[..colon];
            var sp = cpu.FindSpace(head);
            if (sp != null) { explicitSpace = sp.Id; body = spec[(colon + 1)..]; }
            else if (IsHex(head) && head.Length <= 2)
            {
                // bank:address[-address]
                uint bank = uint.Parse(head, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                string rest = spec[(colon + 1)..];
                SplitRange(rest, out string a, out string? b);
                if (!TryNumber(a, out long lo) || (b != null && !TryNumber(b, out _))) throw new FormatException($"'{spec}': expected bank:address");
                long hi = lo;
                if (b != null) TryNumber(b, out hi);
                var list = new List<SpaceRange>();
                for (long x = lo; x <= hi; x++)
                {
                    if (!cpu.TryBankedSpec(bank, (uint)x, out var key)) throw new FormatException($"'{spec}': this CPU has no banked address {head}:{x:X4}");
                    AddMerged(list, key);
                }
                resolved = Describe(cpu, list);
                return list;
            }
            else throw new FormatException($"'{head}' is not a memory space of the {cpu.Name} (it has: {string.Join(", ", cpu.Spaces.Select(s => s.Name))})");
        }
        SplitRange(body, out string first, out string? second);
        var ka = Endpoint(cpu, symbols, first, explicitSpace, out int sizeA, out bool symA);
        if (second == null)
        {
            var list = new List<SpaceRange>();
            if (ka.space >= 0)
            {
                uint len = (uint)Math.Max(1, (kind & AccessKind.ReadWrite) != 0 && symA ? sizeA : 1);
                list.Add(new SpaceRange(ka.space, ka.offset, ka.offset + len - 1));
            }
            else AddMerged(list, cpu.CpuSpecKey((uint)ka.offset));
            resolved = Describe(cpu, list);
            return list;
        }
        var kb = Endpoint(cpu, symbols, second, explicitSpace >= 0 ? explicitSpace : ka.space, out _, out _);
        if (ka.space < 0 && kb.space < 0)
        {
            // a CPU range: every address through the mirror folding, merged
            uint lo = ka.offset, hi = kb.offset;
            if (hi < lo) throw new FormatException($"'{spec}': the range ends before it starts");
            if (hi - lo > 0xFFFFFF) throw new FormatException($"'{spec}': range too large");
            var list = new List<SpaceRange>();
            for (ulong a = lo; a <= hi; a++) AddMerged(list, cpu.CpuSpecKey((uint)a));
            resolved = Describe(cpu, list);
            return list;
        }
        int space = ka.space >= 0 ? ka.space : kb.space;
        if (ka.space >= 0 && kb.space >= 0 && ka.space != kb.space) throw new FormatException($"'{spec}': the two ends are in different memories");
        if (kb.offset < ka.offset) throw new FormatException($"'{spec}': the range ends before it starts");
        var one = new List<SpaceRange> { new(space, ka.offset, kb.offset) };
        resolved = Describe(cpu, one);
        return one;
    }

    private static void AddMerged(List<SpaceRange> list, SpaceKey k)
    {
        if (list.Count > 0)
        {
            var last = list[^1];
            if (last.Space == k.Space && k.Offset == last.End + 1) { list[^1] = last with { End = k.Offset }; return; }
            if (last.Space == k.Space && k.Offset >= last.Start && k.Offset <= last.End) return;
        }
        foreach (var r in list) if (r.Contains(k)) return;
        list.Add(new SpaceRange(k.Space, k.Offset, k.Offset));
    }

    private static string Describe(DebugCpu cpu, List<SpaceRange> list) =>
        string.Join(", ", list.Take(4).Select(r =>
        {
            string name = cpu.Spaces.FirstOrDefault(s => s.Id == r.Space)?.Name ?? r.Space.ToString();
            return r.Start == r.End ? $"{name}:${r.Start:X}" : $"{name}:${r.Start:X}-${r.End:X}";
        })) + (list.Count > 4 ? $" (+{list.Count - 4} more)" : "");

    private static void SplitRange(string s, out string a, out string? b)
    {
        int dash = s.IndexOf('-', 1);
        if (dash < 0) { a = s.Trim(); b = null; return; }
        a = s[..dash].Trim(); b = s[(dash + 1)..].Trim();
    }

    /// <summary>One end of a spec: (space, offset) for a named space or a symbol; space -1 for a CPU address.</summary>
    private static (int space, uint offset) Endpoint(DebugCpu cpu, SymbolTable symbols, string text, int space, out int size, out bool isSymbol)
    {
        size = 1; isSymbol = false;
        long plus = 0;
        int p = text.LastIndexOf('+');
        if (p > 0 && TryNumber(text[(p + 1)..], out long off)) { plus = off; text = text[..p].Trim(); }
        if (!text.StartsWith("$") && !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && symbols.TryFind(text, out var sym, out string? err))
        {
            if (!cpu.BindSymbol(sym, out var key)) throw new FormatException($"symbol '{text}' ({sym}) is not on the {cpu.Name}");
            isSymbol = true; size = plus == 0 ? sym.Size : 1;
            return (key.Space == 0 && space < 0 ? -1 : key.Space, (uint)(key.Offset + plus));
        }
        if (TryNumber(text, out long v)) return (space, (uint)(v + plus));
        throw new FormatException($"'{text}' is neither an address nor a loaded symbol{(symbols.All.Count == 0 ? " (no symbols are loaded)" : "")}");
    }

    /// <summary>A number in a spec: $hex, 0xhex, or bare hex (addresses are written in hex).</summary>
    private static bool TryNumber(string t, out long v)
    {
        t = t.Trim();
        if (t.StartsWith("$") || t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || t.StartsWith("%")) return DebugExpr.TryParseNumber(t, out v);
        if (t.StartsWith("#")) return long.TryParse(t[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out v);   // #decimal
        return long.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    private static bool IsHex(string s) => s.Length > 0 && s.All(Uri.IsHexDigit);

    // ---------------------------------------------------------------- arming

    internal sealed class CpuArmed
    {
        public Breakpoint[] Exec = Array.Empty<Breakpoint>(), Read = Array.Empty<Breakpoint>(), Write = Array.Empty<Breakpoint>();
        public SpaceFlags?[] Flags = Array.Empty<SpaceFlags?>();
    }

    internal sealed class Armed
    {
        public CpuArmed[] Cpus = Array.Empty<CpuArmed>();
        public static Armed Empty(int n) => new() { Cpus = Enumerable.Range(0, n).Select(_ => new CpuArmed()).ToArray() };
    }

    /// <summary>Keeps the call stack tracked (and so the hooks armed) with nothing else set: an interactive session wants a call stack the
    /// moment it pauses. Off by default: without breakpoints the debugger then costs nothing.</summary>
    public bool TrackCallStack { get => trackCallStack; set { trackCallStack = value; Rearm(); } }

    /// <summary>Records every instruction into the per-CPU ring (always on while anything else keeps the hooks armed).</summary>
    public bool TraceRing { get => traceRing; set { traceRing = value; Rearm(); } }

    // ---------------------------------------------------------------- observers (the checks and the profiler, D4)

    private DebugObserver[] observers = Array.Empty<DebugObserver>();

    /// <summary>Adds a tool that sees every step and access the hooks see (<see cref="DebugObserver"/>); the hooks it wants are armed.</summary>
    public void AddObserver(DebugObserver o)
    {
        lock (sync) { var a = observers; Array.Resize(ref a, a.Length + 1); a[^1] = o; observers = a; }
        Rearm();
    }

    public void RemoveObserver(DebugObserver o)
    {
        lock (sync) observers = observers.Where(x => !ReferenceEquals(x, o)).ToArray();
        Rearm();
    }

    public IReadOnlyList<DebugObserver> Observers => observers;

    /// <summary>A CPU write after it landed (from the SNES / Game Boy watching buses).</summary>
    internal void OnWriteDone(DebugCpu cpu, uint address, byte value)
    {
        var obs = observers;
        for (int i = 0; i < obs.Length; i++) obs[i].OnWriteDone(cpu, address, value);
    }

    /// <summary>An internal cycle of the 65816 (from the SNES watching bus).</summary>
    internal void OnIdleCycle(DebugCpu cpu)
    {
        var obs = observers;
        for (int i = 0; i < obs.Length; i++) obs[i].OnIdleCycle(cpu);
    }

    /// <summary>A DMC fetch halted the NES CPU on a read (from the NES bus's precise-DMA model).</summary>
    internal void OnDmcHalt(DebugCpu cpu, uint address)
    {
        var obs = observers;
        for (int i = 0; i < obs.Length; i++) obs[i].OnDmcHalt(cpu, address);
    }

    /// <summary>Rebuilds the matching tables and arms exactly the hooks something needs. While parked a hook that is no longer needed stays
    /// armed until the machine resumes (<paramref name="resuming"/>): the CPU has not moved, so its call stack is still right for a step.</summary>
    public void Rearm() => Rearm(resuming: false);

    private void Rearm(bool resuming)
    {
        bool keepParked = !resuming && IsParked;
        var cpus = Machine.Cpus;
        var na = Armed.Empty(cpus.Count);
        lock (sync)
        {
            for (int i = 0; i < cpus.Count; i++)
            {
                var c = cpus[i];
                var mine = bps.Where(b => b.Enabled && b.CpuId == c.Id).ToList();
                var ca = na.Cpus[i];
                ca.Exec = mine.Where(b => (b.Kind & AccessKind.Exec) != 0).ToArray();
                ca.Read = mine.Where(b => (b.Kind & AccessKind.Read) != 0).ToArray();
                ca.Write = mine.Where(b => (b.Kind & AccessKind.Write) != 0).ToArray();
                ca.Flags = new SpaceFlags?[c.Spaces.Count == 0 ? 1 : c.Spaces.Max(s => s.Id) + 1];
                foreach (var b in mine)
                    foreach (var r in b.Ranges)
                    {
                        if (r.Space < 0 || r.Space >= ca.Flags.Length) continue;
                        var sp = c.Spaces.First(s => s.Id == r.Space);
                        (ca.Flags[r.Space] ??= new SpaceFlags(Math.Max(sp.Size, r.End + 1))).Set(r.Start, r.End, (byte)b.Kind);
                    }
            }
            armed = na;
        }
        var exec = new bool[cpus.Count];
        var mem = new bool[cpus.Count];
        bool global = pauseRequested || gstep != GlobalStep.None || runToFrame >= 0 || runToScanline >= 0 || runToCount >= 0;
        var obs = observers;
        for (int i = 0; i < cpus.Count; i++)
        {
            var ca = na.Cpus[i];
            bool obsSteps = false, obsMem = false;
            foreach (var o in obs) { obsSteps |= o.WantsSteps(i); obsMem |= o.WantsAccesses(i); }
            mem[i] = ca.Read.Length + ca.Write.Length > 0 || obsMem;
            exec[i] = mem[i] || obsSteps || ca.Exec.Length > 0 || cpus[i].State.Step != StepMode.None || trackCallStack || traceRing
                      || (traceFile != null && traceFile.CpuIndex == i) || (i == 0 && global) || (keepParked && hooked[i]);
            if (exec[i] && !hooked[i]) ResetState(cpus[i]);   // the call stack is tracked from the moment the hook is armed
            hooked[i] = exec[i];
            if (exec[i] && cpus[i].State.Ring == null && TraceRingSize > 0) cpus[i].State.Ring = new TraceRecord[TraceRingSize];
        }
        Machine.ApplyHooks(exec, mem);
    }

    private static void ResetState(DebugCpu c)
    {
        var st = c.State;
        st.Stack.Clear(); st.CallPending = false; st.IrqPending = null; st.HaveLast = false; st.LastWasReturn = false; st.TracedFunctionFrames = 0; st.SpcPendingWrites = 0;
    }

    /// <summary>True when any hook is armed (the machine is paying for the debugger).</summary>
    public bool IsArmed => hooked.Any(h => h);

    /// <summary>Whether the per-instruction hook of a CPU (by index) is wanted (a machine that shares one hook between CPUs asks).</summary>
    internal bool WantsCpu(int index) => hooked[index];

    /// <summary>Removes every breakpoint, step, trace and hook, and lets a parked emulation go: the machine runs as if no debugger existed.</summary>
    public void Detach()
    {
        lock (sync) { bps.Clear(); observers = Array.Empty<DebugObserver>(); }
        pauseRequested = false; gstep = GlobalStep.None; runToFrame = -1; runToScanline = -1; runToCount = -1;
        trackCallStack = false; traceRing = false;
        StopTraceFile();
        foreach (var c in Machine.Cpus) { c.State.Step = StepMode.None; c.State.PendingBreak = null; }
        Rearm(resuming: true);
        lock (gate) { parked = false; Monitor.PulseAll(gate); }
    }

    // ---------------------------------------------------------------- running and stepping (any thread)

    private volatile bool pauseRequested;
    private enum GlobalStep { None, Scanline, Frame }
    private GlobalStep gstep;
    private long gStartFrame; private int gStartScanline;
    private long runToFrame = -1;
    private long runToCount = -1;
    private int runToScanline = -1; private bool runToScanlineLeft;

    /// <summary>Break at the next instruction of the main CPU.</summary>
    public void Pause() { pauseRequested = true; Rearm(); }

    /// <summary>Break at the next instruction of the main CPU with a reason of its own ("assert", "port-break": the debug port of D7) and
    /// the text the break shows.</summary>
    public void Pause(string reason, string text) { pauseReason = reason; pauseText = text; pauseRequested = true; Rearm(); }
    private volatile string? pauseReason, pauseText;
    private BreakInfo PauseBreak(DebugCpu cpu)
    {
        var b = new BreakInfo { Reason = pauseReason ?? "pause", CpuId = cpu.Id };
        if (pauseText != null) b.Text = pauseText;
        pauseReason = null; pauseText = null;
        return b;
    }

    /// <summary>Lets a parked emulation run on.</summary>
    public void Continue()
    {
        Rearm(resuming: true);
        lock (gate) { parked = false; Monitor.PulseAll(gate); }
    }

    public void StepInto(string cpu = "main") { var c = CpuOf(cpu); c.State.Step = StepMode.Into; Continue(); }

    /// <summary>Runs the instruction at PC; a call (JSR, JSL, CALL, RST, an interrupt taken meanwhile) runs to its return.</summary>
    public void StepOver(string cpu = "main") { var c = CpuOf(cpu); c.State.StepDepth = c.State.Stack.Count; c.State.Step = StepMode.Over; Continue(); }

    /// <summary>Runs until the current function returns (with no frame known: until the next return instruction).</summary>
    public void StepOut(string cpu = "main")
    {
        var c = CpuOf(cpu);
        c.State.StepDepth = c.State.Stack.Count;
        c.State.Step = c.State.Stack.Count > 0 ? StepMode.Out : StepMode.OutAny;
        Continue();
    }

    public void StepScanline() { gStartFrame = Machine.Frame; gStartScanline = Machine.Scanline; gstep = GlobalStep.Scanline; Continue(); }
    public void StepFrame() { gStartFrame = Machine.Frame; gstep = GlobalStep.Frame; Continue(); }
    public void RunToFrame(long frame) { runToFrame = frame; Continue(); }

    /// <summary>Runs until the main CPU has run <paramref name="count"/> instructions in all (<see cref="InstructionCount"/>), then breaks before
    /// the next one ("step-back": the replay of D5's step back lands here).</summary>
    public void RunToInstructionCount(long count) { runToCount = count; Continue(); }

    /// <summary>The instructions a CPU has run while the debugger's hook was armed (with <see cref="TrackCallStack"/> on: since it was turned on).
    /// Step back (D5) counts positions with it.</summary>
    public long InstructionCount(string cpu = "main") => CpuOf(cpu).State.Executed;

    /// <summary>Sets <see cref="InstructionCount"/> (after a state load puts the machine back where the count was taken).</summary>
    public void SetInstructionCount(long value, string cpu = "main") => CpuOf(cpu).State.Executed = value;
    public void RunToScanline(int line) { runToScanline = line; runToScanlineLeft = Machine.Scanline != line; Continue(); }

    /// <summary>Runs to an address (a temporary execute breakpoint, removed when it breaks).</summary>
    public Breakpoint RunTo(string spec, string cpu = "main")
    {
        var bp = Add(spec, AccessKind.Exec, cpu: cpu, temporary: true);
        Continue();
        return bp;
    }

    private DebugCpu CpuOf(string id) => Machine.Cpu(id) ?? throw new ArgumentException($"no CPU '{id}' on this {Machine.Console}");

    // ---------------------------------------------------------------- parking

    private readonly object gate = new();
    private bool parked;
    private BreakInfo? lastBreak;
    public long ParkCount { get; private set; }
    /// <summary>Raised on the emulation thread just before it parks.</summary>
    public event Action<BreakInfo>? Parked;

    public bool IsParked { get { lock (gate) return parked; } }
    public BreakInfo? LastBreak { get { lock (gate) return lastBreak; } }

    /// <summary>Waits until the emulation parks (true) or the timeout passes.</summary>
    public bool WaitForPark(int timeoutMs)
    {
        lock (gate)
        {
            var end = Environment.TickCount64 + timeoutMs;
            while (!parked)
            {
                long left = end - Environment.TickCount64;
                if (left <= 0 || !Monitor.Wait(gate, (int)left)) return parked;
            }
            return true;
        }
    }

    private void Park(DebugCpu cpu, BreakInfo info)
    {
        info.Pc = cpu.PC;
        info.PcSymbol = SymbolAt(cpu, cpu.PC);
        info.Frame = Machine.Frame; info.Scanline = Machine.Scanline; info.Dot = Machine.Dot; info.Cycle = Machine.Cycle;
        if (info.Text.Length == 0) info.Text = DescribeBreak(cpu, info);
        lock (gate) { lastBreak = info; parked = true; ParkCount++; Monitor.PulseAll(gate); }
        try { Parked?.Invoke(info); } catch (Exception e) { Log?.WriteLine("[debug] Parked handler failed: " + e.Message); }
        int released = 0;
        var hl = HostLock;
        if (hl != null) while (Monitor.IsEntered(hl)) { Monitor.Exit(hl); released++; }
        try { lock (gate) { while (parked) Monitor.Wait(gate); } }
        finally { for (; released > 0; released--) Monitor.Enter(hl!); }
    }

    private string DescribeBreak(DebugCpu cpu, BreakInfo b)
    {
        var sb = new StringBuilder();
        sb.Append(b.Reason);
        if (b.BreakpointId is int id) sb.Append(" #").Append(id);
        if (b.Kind is AccessKind.Read or AccessKind.Write)
        {
            sb.Append(' ').Append(b.Kind == AccessKind.Read ? "read" : "write").Append(' ').Append(cpu.FormatAddress(b.Address));
            string? s = SymbolAt(cpu, b.Address);
            if (s != null) sb.Append(" (").Append(s).Append(')');
            sb.Append(" = $").Append(b.Value.ToString("X2"));
            if (b.Source != AccessSource.Cpu) sb.Append(" by ").Append(b.Source).Append(b.Channel >= 0 ? " ch" + b.Channel : "");
            sb.Append(" from the instruction at ").Append(cpu.FormatAddress(b.AccessPc));
        }
        sb.Append(" | ").Append(cpu.Id).Append(" next ").Append(cpu.FormatAddress(b.Pc));
        if (b.PcSymbol != null) sb.Append(" (").Append(b.PcSymbol).Append(')');
        sb.Append(" | frame ").Append(b.Frame).Append(" line ").Append(b.Scanline).Append(" dot ").Append(b.Dot);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- the hooks (emulation thread)

    /// <summary>Before each step of a CPU (an instruction, an interrupt entry or a halted cycle).</summary>
    internal void OnInstruction(DebugCpu cpu, long cycle)
    {
        var st = cpu.State;
        uint pc = cpu.PC, sp = cpu.SP;
        bool exec = cpu.WillExecute(out string? irq);

        // the call stack: what the previous step did
        bool pushed = false;
        if (st.CallPending)
        {
            st.CallPending = false;
            if (sp == st.CallTargetSpExpected) { PushFrame(cpu, st.CallKind, st.CallPc, pc, st.CallReturnPc, sp); pushed = true; }
        }
        if (st.IrqPending != null)
        {
            string kind = st.IrqPending; st.IrqPending = null;
            if (sp != st.IrqSp) PushFrame(cpu, kind, st.IrqPc, pc, st.IrqPc, sp);
        }
        else if (!pushed && st.HaveLast && cpu.DetectInterruptEntry(st.LastPc, st.LastSp, pc, sp, out string guessed))
            PushFrame(cpu, guessed, st.LastPc, pc, st.LastPc, sp);
        var stack = st.Stack;
        while (stack.Count > 0 && cpu.StackAbove(sp, stack[^1].SpAfter)) PopFrame(cpu);

        var obs = observers;
        for (int i = 0; i < obs.Length; i++) obs[i].OnStep(cpu, cycle, exec, irq);

        if (!exec)
        {
            if (irq != null)
            {
                st.IrqPending = irq; st.IrqPc = pc; st.IrqSp = sp;
                if (st.Ring != null) RecordInterrupt(cpu, st, pc, irq, cycle);
            }
            // a watch hit or a pause is still honoured at an interrupt entry or on a halted CPU (an instruction boundary)
            var hb = st.PendingBreak; st.PendingBreak = null;
            if (hb == null && cpu.Index == 0 && pauseRequested) { pauseRequested = false; hb = PauseBreak(cpu); }
            if (hb != null) Park(cpu, hb);
            return;
        }

        if (st.Ring != null || traceFile != null) Record(cpu, st, pc, cycle);

        var br = st.PendingBreak; st.PendingBreak = null;
        var ca = armed.Cpus[cpu.Index];
        if (ca.Exec.Length > 0) br = MatchExec(cpu, st, ca, pc, br);
        br ??= CheckStep(cpu, st);
        if (br != null)
        {
            CancelSteps(cpu);   // any break ends a step or run-to in progress (as in Mesen)
            Park(cpu, br);
            pc = cpu.PC; sp = cpu.SP;   // the user may have changed them while parked
        }

        // classify the instruction about to run, for the call stack and step out
        uint bytes = (uint)(cpu.Peek(pc) | cpu.Peek(cpu.NextAddress(pc, 1)) << 8);
        var ic = cpu.Classify(bytes);
        if (ic.Flow is FlowKind.Call or FlowKind.Software)
        {
            st.CallPending = true;
            st.CallKind = cpu.Disassembler is DisasmM68k ? DisasmM68k.CallName((int)((bytes & 0xFF) << 8 | (bytes >> 8))) : CallName(cpu, (byte)bytes);
            st.CallPc = pc;
            st.CallReturnPc = cpu.NextAddress(pc, ic.Length);
            st.CallTargetSpExpected = cpu.StackAfterPush(sp, ic.PushBytes);
        }
        st.LastWasReturn = ic.Flow == FlowKind.Return;
        st.HaveLast = true; st.LastPc = pc; st.LastSp = sp;
        st.Executed++;
    }

    private static string CallName(DebugCpu cpu, byte op) => cpu.Disassembler switch
    {
        Disasm6502 => op == 0x00 ? "BRK" : "JSR",
        Disasm65816 => op switch { 0x22 => "JSL", 0x00 => "BRK", 0x02 => "COP", _ => "JSR" },
        DisasmSpc700 => op switch { 0x3F => "CALL", 0x4F => "PCALL", 0x0F => "BRK", _ => "TCALL" },
        _ => (op & 0xC7) == 0xC7 ? "RST" : "CALL",
    };

    private void PushFrame(DebugCpu cpu, string kind, uint callerPc, uint target, uint returnPc, uint spAfter)
    {
        var st = cpu.State;
        var f = new CallFrame { Kind = kind, CallerPc = callerPc, Target = target, ReturnPc = returnPc, SpAfter = spAfter, Frame = Machine.Frame };
        var tf = traceFile;
        if (tf != null && tf.CpuIndex == cpu.Index && tf.Function is SpaceKey fk)
        {
            Span<SpaceKey> keys = stackalloc SpaceKey[4];
            int n = cpu.AccessKeys(target, false, keys);
            for (int i = 0; i < n; i++) if (keys[i].Equals(fk)) { f.TracedFunction = true; st.TracedFunctionFrames++; break; }
        }
        if (st.Stack.Count > 512) { var old = st.Stack[0]; st.Stack.RemoveAt(0); NotifyReturn(cpu, old); }   // a runaway (a stack reset without returns): keep the newest
        st.Stack.Add(f);
        var obs = observers;
        for (int i = 0; i < obs.Length; i++) obs[i].OnCall(cpu, f);
    }

    private void PopFrame(DebugCpu cpu)
    {
        var st = cpu.State;
        var f = st.Stack[^1];
        st.Stack.RemoveAt(st.Stack.Count - 1);
        if (f.TracedFunction && st.TracedFunctionFrames > 0) st.TracedFunctionFrames--;
        NotifyReturn(cpu, f);
    }

    private void NotifyReturn(DebugCpu cpu, CallFrame f)
    {
        var obs = observers;
        for (int i = 0; i < obs.Length; i++) obs[i].OnReturn(cpu, f);
    }

    private BreakInfo? MatchExec(DebugCpu cpu, CpuDebugState st, CpuArmed ca, uint pc, BreakInfo? br)
    {
        Span<SpaceKey> keys = stackalloc SpaceKey[4];
        int n = cpu.AccessKeys(pc, false, keys);
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            var f = keys[i].Space < ca.Flags.Length ? ca.Flags[keys[i].Space] : null;
            if (f != null && (f.Get(keys[i].Offset) & (byte)AccessKind.Exec) != 0) { any = true; break; }
        }
        if (!any) return br;
        List<Breakpoint>? done = null;
        foreach (var bp in ca.Exec)
        {
            bool m = false;
            for (int i = 0; i < n && !m; i++) m = bp.Matches(keys[i]);
            if (!m) continue;
            if (!Fire(cpu, st, bp, pc, 0, AccessKind.Exec, AccessSource.Cpu, -1, pc)) continue;
            if (bp.Tracepoint) continue;
            br ??= new BreakInfo { Reason = bp.Temporary ? "run-to" : "breakpoint", CpuId = cpu.Id, BreakpointId = bp.Id, Kind = AccessKind.Exec, Address = pc, AccessPc = pc };
            if (bp.Temporary) (done ??= new List<Breakpoint>()).Add(bp);
        }
        if (done != null) { lock (sync) foreach (var b in done) bps.Remove(b); Rearm(); }
        return br;
    }

    /// <summary>A memory access by a CPU or a DMA (the machine's tap).</summary>
    internal void OnAccess(DebugCpu cpu, uint address, byte value, bool write, AccessSource source, int channel)
    {
        var obs = observers;
        for (int i = 0; i < obs.Length; i++) obs[i].OnAccess(cpu, address, value, write, source, channel);
        var ca = armed.Cpus[cpu.Index];
        if ((write ? ca.Write : ca.Read).Length == 0) return;
        Span<SpaceKey> keys = stackalloc SpaceKey[4];
        int n = cpu.AccessKeys(address, write, keys);
        OnAccessKeys(cpu, ca, keys[..n], address, value, write, source, channel);
    }

    /// <summary>An access to a place outside the CPU's address space (the NES OAM through DMA): one key, shown at <paramref name="address"/>.</summary>
    internal void OnAccessKey(DebugCpu cpu, SpaceKey key, uint address, byte value, bool write, AccessSource source, int channel)
    {
        var ca = armed.Cpus[cpu.Index];
        if ((write ? ca.Write : ca.Read).Length == 0) return;
        Span<SpaceKey> keys = stackalloc SpaceKey[1];
        keys[0] = key;
        OnAccessKeys(cpu, ca, keys, address, value, write, source, channel);
    }

    /// <summary>A Z80 I/O port access (the Sega consoles): the observers see it at <paramref name="observerAddress"/> ($10000 + the port, outside the memory's
    /// addresses), the watches match <paramref name="key"/> (the port the console decodes) and show <paramref name="port"/>.</summary>
    internal void OnPortAccess(DebugCpu cpu, SpaceKey key, uint observerAddress, uint port, byte value, bool write)
    {
        var obs = observers;
        for (int i = 0; i < obs.Length; i++) obs[i].OnAccess(cpu, observerAddress, value, write, AccessSource.Cpu, -1);
        OnAccessKey(cpu, key, port, value, write, AccessSource.Cpu, -1);
    }

    private void OnAccessKeys(DebugCpu cpu, CpuArmed ca, Span<SpaceKey> keys, uint address, byte value, bool write, AccessSource source, int channel)
    {
        var list = write ? ca.Write : ca.Read;
        int n = keys.Length;
        byte bit = write ? (byte)AccessKind.Write : (byte)AccessKind.Read;
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            var f = keys[i].Space < ca.Flags.Length ? ca.Flags[keys[i].Space] : null;
            if (f != null && (f.Get(keys[i].Offset) & bit) != 0) { any = true; break; }
        }
        if (!any) return;
        var st = cpu.State;
        var kind = write ? AccessKind.Write : AccessKind.Read;
        foreach (var bp in list)
        {
            bool m = false;
            for (int i = 0; i < n && !m; i++) m = bp.Matches(keys[i]);
            if (!m) continue;
            if (!Fire(cpu, st, bp, address, value, kind, source, channel, st.LastPc)) continue;
            if (bp.Tracepoint) continue;
            st.PendingBreak ??= new BreakInfo
            {
                Reason = "watch", CpuId = cpu.Id, BreakpointId = bp.Id, Kind = kind, Address = address, Value = value,
                Source = source, Channel = channel, AccessPc = st.LastPc,
            };
        }
    }

    /// <summary>Condition, hit count, hit log, tracepoint line. True when the breakpoint fires (breaks, or logs for a tracepoint).</summary>
    private bool Fire(DebugCpu cpu, CpuDebugState st, Breakpoint bp, uint address, byte value, AccessKind kind, AccessSource source, int channel, uint accessPc)
    {
        st.Ctx.Source = (long)source;
        if (bp.Cond != null)
        {
            st.Ctx.Value = value; st.Ctx.Address = address;
            if (!bp.Cond.IsTrue(st.Ctx)) return false;
        }
        long h = ++bp.Hits;
        bool fire = bp.HitMode switch
        {
            HitMode.Exactly => h == bp.HitTarget,
            HitMode.Every => h % bp.HitTarget == 0,
            _ => h >= bp.HitTarget,
        };
        if (!fire) return false;
        string text;
        if (bp.LogParts != null)
        {
            st.Ctx.Value = value; st.Ctx.Address = address;
            var sb = new StringBuilder();
            foreach (var (t, e) in bp.LogParts)
            {
                if (e == null) sb.Append(t);
                else { long v = e.Evaluate(st.Ctx); sb.Append(v is >= 0 and <= 0xFF ? "$" + v.ToString("X2") : v is >= 0 and <= 0xFFFF ? "$" + v.ToString("X4") : v.ToString(CultureInfo.InvariantCulture)); }
            }
            text = $"#{bp.Id} {sb}";
        }
        else
        {
            string where = kind == AccessKind.Exec ? $"exec {cpu.FormatAddress(address)}" : $"{(kind == AccessKind.Read ? "read" : "write")} {cpu.FormatAddress(address)} = ${value:X2}";
            string? sym = SymbolAt(cpu, address);
            text = $"#{bp.Id} {where}{(sym != null ? $" ({sym})" : "")}{(source != AccessSource.Cpu ? $" by {source}{(channel >= 0 ? " ch" + channel : "")}" : "")} pc {cpu.FormatAddress(accessPc)} frame {Machine.Frame} line {Machine.Scanline}";
        }
        var rec = new HitRecord
        {
            BreakpointId = bp.Id, CpuId = cpu.Id, Kind = kind, Address = address, Value = value, Source = source, Channel = channel, Pc = accessPc,
            Frame = Machine.Frame, Scanline = Machine.Scanline, Cycle = Machine.Cycle, HitNumber = h, Logged = bp.Tracepoint, Text = text,
        };
        lock (hits)
        {
            hits.Add(rec);
            if (hits.Count > MaxHits) hits.RemoveRange(0, hits.Count - MaxHits);
            TotalHits++;
        }
        if (bp.Tracepoint) Log?.WriteLine(text);
        return true;
    }

    private void CancelSteps(DebugCpu cpu)
    {
        cpu.State.Step = StepMode.None;
        if (cpu.Index != 0) return;
        pauseRequested = false; gstep = GlobalStep.None; runToFrame = -1; runToScanline = -1; runToCount = -1;
    }

    private BreakInfo? CheckStep(DebugCpu cpu, CpuDebugState st)
    {
        switch (st.Step)
        {
            case StepMode.Into:
                st.Step = StepMode.None; return new BreakInfo { Reason = "step", CpuId = cpu.Id };
            case StepMode.Over:
                if (st.Stack.Count <= st.StepDepth) { st.Step = StepMode.None; return new BreakInfo { Reason = "step-over", CpuId = cpu.Id }; }
                break;
            case StepMode.Out:
                if (st.Stack.Count < st.StepDepth) { st.Step = StepMode.None; return new BreakInfo { Reason = "step-out", CpuId = cpu.Id }; }
                break;
            case StepMode.OutAny:
                if (st.LastWasReturn) { st.Step = StepMode.None; return new BreakInfo { Reason = "step-out", CpuId = cpu.Id }; }
                break;
        }
        if (cpu.Index != 0) return null;
        if (pauseRequested) { pauseRequested = false; return PauseBreak(cpu); }
        if (gstep == GlobalStep.Scanline && (Machine.Scanline != gStartScanline || Machine.Frame != gStartFrame)) { gstep = GlobalStep.None; return new BreakInfo { Reason = "scanline", CpuId = cpu.Id }; }
        if (gstep == GlobalStep.Frame && Machine.Frame > gStartFrame) { gstep = GlobalStep.None; return new BreakInfo { Reason = "frame", CpuId = cpu.Id }; }
        if (runToFrame >= 0 && Machine.Frame >= runToFrame) { runToFrame = -1; return new BreakInfo { Reason = "frame", CpuId = cpu.Id }; }
        if (runToCount >= 0 && st.Executed >= runToCount) { runToCount = -1; return new BreakInfo { Reason = "step-back", CpuId = cpu.Id }; }
        if (runToScanline >= 0)
        {
            int s = Machine.Scanline;
            if (s != runToScanline) runToScanlineLeft = true;
            else if (runToScanlineLeft) { runToScanline = -1; return new BreakInfo { Reason = "scanline", CpuId = cpu.Id }; }
        }
        return null;
    }

    // ---------------------------------------------------------------- the hit log

    private const int MaxHits = 4096;
    private readonly List<HitRecord> hits = new();
    public long TotalHits { get; private set; }
    public IReadOnlyList<HitRecord> Hits { get { lock (hits) return hits.ToArray(); } }
    public void ClearHits() { lock (hits) { hits.Clear(); TotalHits = 0; } }

    // ---------------------------------------------------------------- the call stack

    /// <summary>The call stack of a CPU, outermost first, with symbols.</summary>
    public IReadOnlyList<CallFrame> CallStack(string cpu = "main")
    {
        var c = CpuOf(cpu);
        var list = c.State.Stack.ToArray();
        foreach (var f in list) { f.TargetSymbol = SymbolAt(c, f.Target); f.CallerSymbol = SymbolAt(c, f.CallerPc); }
        return list;
    }

    // ---------------------------------------------------------------- symbols

    private readonly Dictionary<int, SymbolIndex> symbolIndex = new();
    private sealed class SymbolIndex { public int Version; public Dictionary<int, (uint[] at, DebugSymbol[] sym)> BySpace = new(); }

    /// <summary>The symbol at a CPU address ("name", or "name+$N" inside one), or null.</summary>
    public string? SymbolAt(DebugCpu cpu, uint address)
    {
        if (Symbols.All.Count == 0) return null;
        Span<SpaceKey> keys = stackalloc SpaceKey[4];
        int n = cpu.AccessKeys(address, false, keys);
        return SymbolAtKeys(cpu, keys[..n]);
    }

    /// <summary>The symbol at canonical keys taken earlier (T3's profiles keep an instruction's keys, so a later lookup names the bank it ran in):
    /// the memory it maps to first, the CPU address last, as <see cref="SymbolAt"/>.</summary>
    public string? SymbolAtKeys(DebugCpu cpu, ReadOnlySpan<SpaceKey> keys)
    {
        if (Symbols.All.Count == 0) return null;
        var idx = IndexFor(cpu);
        int n = keys.Length;
        string? best = null;
        for (int i = n - 1; i >= 0; i--)   // the memory it maps to first (a ROM offset names one bank), the CPU address last
        {
            if (!idx.BySpace.TryGetValue(keys[i].Space, out var t)) continue;
            int j = Array.BinarySearch(t.at, keys[i].Offset);
            if (j >= 0)
            {
                while (j > 0 && t.at[j - 1] == keys[i].Offset) j--;
                return t.sym[j].Name;
            }
            j = ~j - 1;
            if (j >= 0)
            {
                var s = t.sym[j];
                uint delta = keys[i].Offset - t.at[j];
                if (delta < (uint)Math.Max(1, s.Size)) best ??= $"{s.Name}+${delta:X}";
            }
        }
        return best;
    }

    private SymbolIndex IndexFor(DebugCpu cpu)
    {
        lock (symbolIndex)
        {
            if (symbolIndex.TryGetValue(cpu.Index, out var idx) && idx.Version == Symbols.Version) return idx;
            idx = new SymbolIndex { Version = Symbols.Version };
            var lists = new Dictionary<int, List<(uint, DebugSymbol)>>();
            foreach (var s in Symbols.All)
            {
                if (s.Name.StartsWith("SECTIONSTART_") || s.Name.StartsWith("SECTIONEND_")) continue;   // WLA section markers name no place of their own
                if (!cpu.BindSymbol(s, out var k)) continue;
                if (!lists.TryGetValue(k.Space, out var l)) lists[k.Space] = l = new List<(uint, DebugSymbol)>();
                l.Add((k.Offset, s));
            }
            foreach (var (space, l) in lists)
            {
                var sorted = l.OrderBy(x => x.Item1).ToArray();
                idx.BySpace[space] = (sorted.Select(x => x.Item1).ToArray(), sorted.Select(x => x.Item2).ToArray());
            }
            symbolIndex[cpu.Index] = idx;
            return idx;
        }
    }

    /// <summary>Reads a variable by name, all its bytes (little-endian when used as a number).</summary>
    public byte[] ReadVariable(string name, string cpu = "main")
    {
        var c = CpuOf(cpu);
        if (!Symbols.TryFind(name, out var sym, out string? err)) throw new ArgumentException(err);
        if (!c.BindSymbol(sym, out var k)) throw new ArgumentException($"symbol '{name}' is not on the {c.Name}");
        var b = new byte[Math.Max(1, sym.Size)];
        for (int i = 0; i < b.Length; i++) b[i] = c.PeekSpace(k.Space, (uint)(k.Offset + i));
        return b;
    }

    /// <summary>Writes a variable by name: <paramref name="value"/> little-endian over its size.</summary>
    public void WriteVariable(string name, long value, string cpu = "main")
    {
        var c = CpuOf(cpu);
        if (!Symbols.TryFind(name, out var sym, out string? err)) throw new ArgumentException(err);
        if (!c.BindSymbol(sym, out var k)) throw new ArgumentException($"symbol '{name}' is not on the {c.Name}");
        for (int i = 0; i < Math.Max(1, sym.Size); i++)
            if (!c.PokeSpace(k.Space, (uint)(k.Offset + i), (byte)(value >> (8 * i)))) throw new InvalidOperationException($"'{name}' is in memory the debugger cannot write");
    }

    /// <summary>Evaluates an expression now ("[player_hp] + 1", "A", "{$7E0010}").</summary>
    public long Evaluate(string expression, string cpu = "main")
    {
        var c = CpuOf(cpu);
        var e = DebugExpr.Compile(expression, c);
        var ctx = new ExprContext { Address = c.PC };
        return e.Evaluate(ctx);
    }

    // ---------------------------------------------------------------- the trace

    private void Record(DebugCpu cpu, CpuDebugState st, uint pc, long cycle)
    {
        var r = new TraceRecord { Cycle = cycle, Frame = Machine.Frame, Scanline = (short)Machine.Scanline, Dot = (short)Machine.Dot, Pc = pc, Ea = -1 };
        cpu.FillTrace(ref r);
        if (st.Ring != null)
        {
            st.Ring[st.RingPos] = r;
            st.RingPos = (st.RingPos + 1) % st.Ring.Length;
            st.RingCount++;
        }
        var tf = traceFile;
        if (tf != null && tf.CpuIndex == cpu.Index) tf.Consider(this, cpu, st, in r);
    }

    private void RecordInterrupt(DebugCpu cpu, CpuDebugState st, uint pc, string kind, long cycle)
    {
        var r = new TraceRecord { Cycle = cycle, Frame = Machine.Frame, Scanline = (short)Machine.Scanline, Dot = (short)Machine.Dot, Pc = pc, Ea = -1, Kind = kind == "NMI" ? (byte)1 : kind == "IRQ" ? (byte)2 : (byte)3 };
        cpu.FillTrace(ref r);
        st.Ring![st.RingPos] = r;
        st.RingPos = (st.RingPos + 1) % st.Ring.Length;
        st.RingCount++;
        var tf = traceFile;
        if (tf != null && tf.CpuIndex == cpu.Index) tf.Consider(this, cpu, st, in r);
    }

    /// <summary>The last <paramref name="count"/> traced instructions of a CPU, oldest first.</summary>
    public TraceRecord[] TraceTail(int count, string cpu = "main")
    {
        var st = CpuOf(cpu).State;
        if (st.Ring == null || st.RingCount == 0) return Array.Empty<TraceRecord>();
        int n = (int)Math.Min(Math.Min(count, st.RingCount), st.Ring.Length);
        var o = new TraceRecord[n];
        for (int i = 0; i < n; i++) o[i] = st.Ring[(st.RingPos - n + i + st.Ring.Length * 2) % st.Ring.Length];
        return o;
    }

    /// <summary>A trace line with Mesen's trace logger columns: PC, the instruction (with symbols), [address] = value, the registers, V (scanline), H (dot), then Fr / Cyc.</summary>
    public string FormatTrace(in TraceRecord r, string cpu = "main", bool labels = true, bool frameAndCycle = true)
    {
        var c = CpuOf(cpu);
        return FormatTrace(c, in r, labels, frameAndCycle);
    }

    internal string FormatTrace(DebugCpu c, in TraceRecord r, bool labels, bool frameAndCycle)
    {
        var sb = new StringBuilder(128);
        sb.Append(r.Pc.ToString(c.AddressDigits == 6 ? "X6" : "X4")).Append("  ");
        int start = sb.Length;
        if (r.Kind != 0) sb.Append("-- ").Append(r.Kind == 1 ? "NMI" : r.Kind == 2 ? "IRQ" : "interrupt").Append(" --");
        else
        {
            Span<byte> bytes = stackalloc byte[4];
            for (int i = 0; i < 4; i++) bytes[i] = r.Byte(i);
            var ctx = c.ContextOf(in r);
            Func<uint, string?>? label = labels && Symbols.All.Count > 0 ? a => ExactSymbol(c, a) : null;
            sb.Append(c.Disassembler.Format(r.Pc, bytes[..Math.Clamp((int)r.Length, 1, 4)], ctx, label));   // a record keeps four bytes (the 68000's disassembler reads the rest)
            if (r.Ea >= 0) sb.Append(" [").Append(((uint)r.Ea).ToString(c.AddressDigits == 6 && r.Ea > 0xFFFF ? "X6" : "X4")).Append("] = $").Append(r.EaValue.ToString("X2"));
        }
        int pad = start + 40 - sb.Length;
        sb.Append(' ', Math.Max(1, pad));
        sb.Append(c.FormatRegisters(in r));
        sb.Append(" V:").Append(r.Scanline.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append(" H:").Append(r.Dot.ToString(CultureInfo.InvariantCulture).PadLeft(3));
        if (frameAndCycle) sb.Append(" Fr:").Append(r.Frame).Append(" Cyc:").Append(r.Cycle);
        if (labels && r.Kind == 0)
        {
            string? at = SymbolAt(c, r.Pc);
            if (at != null) sb.Append("  ; ").Append(at);
        }
        return sb.ToString();
    }

    private string? ExactSymbol(DebugCpu c, uint a)
    {
        string? s = SymbolAt(c, a);
        return s != null && !s.Contains('+') ? s : null;
    }

    private TraceFile? traceFile;

    /// <summary>A trace to a file of the instructions that pass a filter (PC ranges, a bank, inside a function, a frame window).</summary>
    public sealed class TraceFileOptions
    {
        public string Cpu { get; set; } = "main";
        /// <summary>Address specs (as for breakpoints) the PC must be in; empty: anywhere.</summary>
        public List<string> Ranges { get; set; } = new();
        /// <summary>Only this bank (65816 PBR, or a NES / Game Boy ROM bank by its CPU address's bank: -1 any).</summary>
        public int Bank { get; set; } = -1;
        /// <summary>Only while this function runs (it and everything it calls), by symbol or address.</summary>
        public string? Function { get; set; }
        public long FromFrame { get; set; } = 0;
        public long ToFrame { get; set; } = long.MaxValue;
        public bool Labels { get; set; } = true;
        public long MaxLines { get; set; } = 5_000_000;
    }

    private sealed class TraceFile
    {
        public int CpuIndex;
        public StreamWriter Writer = null!;
        public List<SpaceRange> Ranges = new();
        public int Bank = -1;
        public SpaceKey? Function;
        public uint FunctionSize;
        public long From, To, Max, Lines;
        public bool Labels;
        public string Path = "";

        public void Consider(Debugger d, DebugCpu c, CpuDebugState st, in TraceRecord r)
        {
            if (r.Frame < From || r.Frame > To || Lines >= Max) return;
            if (Bank >= 0 && (r.Pc >> 16) != (uint)Bank) return;
            if (Ranges.Count > 0 || Function != null)
            {
                Span<SpaceKey> keys = stackalloc SpaceKey[4];
                int n = c.AccessKeys(r.Pc, false, keys);
                bool inRange = Ranges.Count == 0;
                for (int i = 0; i < n && !inRange; i++) foreach (var rg in Ranges) if (rg.Contains(keys[i])) { inRange = true; break; }
                if (!inRange) return;
                if (Function is SpaceKey fk && st.TracedFunctionFrames == 0)
                {
                    bool inside = false;
                    for (int i = 0; i < n && !inside; i++) inside = keys[i].Space == fk.Space && keys[i].Offset >= fk.Offset && keys[i].Offset < fk.Offset + FunctionSize;
                    if (!inside) return;
                }
            }
            Writer.WriteLine(d.FormatTrace(c, in r, Labels, true));
            Lines++;
        }
    }

    public void StartTraceFile(string path, TraceFileOptions o)
    {
        StopTraceFile();
        var c = CpuOf(o.Cpu);
        var tf = new TraceFile
        {
            CpuIndex = c.Index, Bank = o.Bank, From = o.FromFrame, To = o.ToFrame, Max = o.MaxLines, Labels = o.Labels, Path = path,
            Writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16) { NewLine = "\n" },
        };
        foreach (var spec in o.Ranges) tf.Ranges.AddRange(Resolve(c, spec, AccessKind.Exec, out _));
        if (o.Function != null)
        {
            var r = Resolve(c, o.Function, AccessKind.Exec, out _);
            tf.Function = new SpaceKey(r[0].Space, r[0].Start);
            tf.FunctionSize = Symbols.TryFind(o.Function, out var sym, out _) ? (uint)Math.Max(1, sym.Size) : 1;
        }
        tf.Writer.WriteLine($"# BrokenNes trace: {Machine.Console} {c.Name}; columns as Mesen's trace logger (PC, instruction, [address] = value, registers, V scanline, H dot), then Fr frame and Cyc cycle");
        traceFile = tf;
        Rearm();
    }

    /// <summary>Closes the trace file; returns how many lines it holds.</summary>
    public long StopTraceFile()
    {
        var tf = traceFile;
        if (tf == null) return 0;
        traceFile = null;
        tf.Writer.Flush();
        tf.Writer.Dispose();
        Rearm();
        return tf.Lines;
    }
}
