using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NesEmulator.Debugging;

/// <summary>What a write hook does to a CPU write it matches (see <see cref="Debugger.AddWriteHook"/>).</summary>
public enum WriteHookAction
{
    /// <summary>Records the write (frame, cycle, PC, old -> new value, the call stack) and lets it land.</summary>
    Log,
    /// <summary>Stops the machine with the write NOT yet applied (it lands when the machine goes on).</summary>
    Break,
    /// <summary>Changes the value: (value AND and | or) XOR xor, or a forced value.</summary>
    Rewrite,
    /// <summary>Drops the write: nothing lands.</summary>
    Block,
    /// <summary>Holds a value like a cheat: put there when the hook is added, and every write lands it instead.</summary>
    Freeze,
}

/// <summary>A write hook: a CPU address range (mirrors folded, like a write watch), an optional condition, and an action.</summary>
public sealed class WriteHook
{
    public int Id { get; internal set; }
    public string CpuId { get; internal set; } = "main";
    public string Spec { get; internal set; } = "";
    public string Resolved { get; internal set; } = "";
    public IReadOnlyList<SpaceRange> Ranges { get; internal set; } = Array.Empty<SpaceRange>();
    public WriteHookAction Action { get; internal set; }
    public string? Condition { get; internal set; }
    internal DebugExpr? Cond;
    /// <summary>Rewrite: the value becomes (value &amp; And | Or) ^ Xor, unless <see cref="Value"/> forces one.</summary>
    public byte And { get; internal set; } = 0xFF;
    public byte Or { get; internal set; }
    public byte Xor { get; internal set; }
    /// <summary>Rewrite (forced) and freeze: the value that lands.</summary>
    public int? Value { get; internal set; }
    /// <summary>Writes it matched (address and condition).</summary>
    public long Hits { get; internal set; }
    /// <summary>Writes it changed or dropped (a rewrite that left the value as it was is not counted).</summary>
    public long Changed { get; internal set; }
    public bool Enabled { get; internal set; } = true;
    /// <summary>Whether the matched writes go to the hook log (default: log and break always; the others too, off with quiet).</summary>
    public bool Logged { get; internal set; } = true;

    internal bool Matches(SpaceKey k)
    {
        foreach (var r in Ranges) if (r.Contains(k)) return true;
        return false;
    }

    public static string ActionText(WriteHookAction a) => a.ToString().ToLowerInvariant();

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("hook #").Append(Id).Append(' ').Append(ActionText(Action)).Append(' ').Append(CpuId == "main" ? "" : CpuId + ":").Append(Spec);
        if (Resolved.Length > 0 && Resolved != Spec) sb.Append(" (").Append(Resolved).Append(')');
        if (Action == WriteHookAction.Rewrite)
        {
            if (Value is int v) sb.Append(" = $").Append(v.ToString("X2"));
            else
            {
                if (And != 0xFF) sb.Append(" and $").Append(And.ToString("X2"));
                if (Or != 0) sb.Append(" or $").Append(Or.ToString("X2"));
                if (Xor != 0) sb.Append(" xor $").Append(Xor.ToString("X2"));
            }
        }
        if (Action == WriteHookAction.Freeze && Value is int f) sb.Append(" = $").Append(f.ToString("X2"));
        if (Condition != null) sb.Append(" if ").Append(Condition);
        sb.Append(" [").Append(Hits).Append(" hit").Append(Hits == 1 ? "" : "s").Append(']');
        if (!Enabled) sb.Append(" (disabled)");
        return sb.ToString();
    }
}

/// <summary>One write a hook matched.</summary>
public sealed class WriteHookRecord
{
    public long Seq { get; init; }
    public int HookId { get; init; }
    public WriteHookAction Action { get; init; }
    public string CpuId { get; init; } = "main";
    public long Frame { get; init; }
    public int Scanline { get; init; }
    public long Cycle { get; init; }
    /// <summary>The instruction that wrote (its first byte's address).</summary>
    public uint Pc { get; init; }
    public string? PcSymbol { get; init; }
    public uint Address { get; init; }
    /// <summary>The byte there before the write (as the debugger peeks it: a register reads as the open bus).</summary>
    public byte Old { get; init; }
    /// <summary>The value the CPU wrote (after the hooks before this one).</summary>
    public byte Written { get; init; }
    /// <summary>The value that lands after this hook; -1 when the write is dropped.</summary>
    public int Landed { get; init; }
    /// <summary>The call stack, innermost first ("" when the debugger does not track it).</summary>
    public string Stack { get; init; } = "";
    public string Text { get; init; } = "";
}

/// <summary>
/// Write hooks: a generic layer on each CPU's write path that logs, breaks before, rewrites, drops or freezes CPU writes to an address
/// range. It rides on the machines' existing write taps, which are null until something arms them, so with no hook (and no watch) the
/// buses pay exactly what they paid before (one null test on the NES slow path; nothing at all elsewhere). Hooks are session state: they
/// are never saved into a save state, and a machine with none runs bit-identical to one without a debugger. Same inputs and the same
/// hooks give the same results (nothing here depends on the clock or the host).
/// The idea comes from the ML_NesPlayer FCEUX fork (fceu.cpp / x6502.cpp WrRAM), which reacts to writes of $00FC and $0759 mid-frame.
/// </summary>
public sealed partial class Debugger
{
    private readonly List<WriteHook> writeHooks = new();
    private WriteHook[][] hookArmed = Array.Empty<WriteHook[]>();
    private int nextHookId = 1;
    private readonly List<WriteHookRecord> hookLog = new();
    private const int MaxHookLog = 8192;
    private long hookSeq;

    /// <summary>While true a break hook does not stop the machine (it lets the write land): set while a session finishes a frame for a state.</summary>
    public bool HookBreaksOff { get; set; }

    public IReadOnlyList<WriteHook> WriteHooks { get { lock (sync) return writeHooks.ToArray(); } }
    public long WriteHookRecords => hookSeq;

    /// <summary>The hook log after <paramref name="sinceSeq"/> (sequence numbers start at 1), oldest first; at most <see cref="MaxHookLog"/> are kept.</summary>
    public IReadOnlyList<WriteHookRecord> HookLog(long sinceSeq = 0) { lock (hookLog) return hookLog.Where(r => r.Seq > sinceSeq).ToArray(); }
    public void ClearHookLog() { lock (hookLog) hookLog.Clear(); }

    /// <summary>
    /// Adds a write hook on a CPU. <paramref name="spec"/> is a breakpoint spec ($00FC, $0700-$07FF, a symbol, wram:$10). The condition is a
    /// breakpoint condition (value is the byte being written, address the CPU address). Rewrite takes and / or / xor masks or a forced
    /// <paramref name="value"/>; freeze needs <paramref name="value"/> and puts it in place at once.
    /// </summary>
    public WriteHook AddWriteHook(string spec, WriteHookAction action, string? condition = null, byte and = 0xFF, byte or = 0, byte xor = 0, int? value = null, string cpu = "main", bool quiet = false)
    {
        var c = Machine.Cpu(cpu) ?? throw new ArgumentException($"no CPU '{cpu}' on this {Machine.Console} (it has: {string.Join(", ", Machine.Cpus.Select(x => x.Id))})");
        if (!Machine.SupportsWriteHooks(c)) throw new ArgumentException($"write hooks do not reach the {c.Name}'s writes on this {Machine.Console}");
        if (action == WriteHookAction.Freeze && value == null) throw new ArgumentException("a freeze needs the value to hold");
        if (value is int v0 && (v0 < 0 || v0 > 0xFF)) throw new ArgumentException($"value ${v0:X} is not a byte");
        if (action == WriteHookAction.Rewrite && value == null && and == 0xFF && or == 0 && xor == 0) throw new ArgumentException("a rewrite needs and / or / xor masks or a value");
        var ranges = Resolve(c, spec, AccessKind.Write, out string resolved);
        var h = new WriteHook
        {
            CpuId = c.Id, Spec = spec.Trim(), Ranges = ranges, Resolved = resolved, Action = action, And = and, Or = or, Xor = xor, Value = value,
            Logged = !quiet || action is WriteHookAction.Log or WriteHookAction.Break,
        };
        if (!string.IsNullOrWhiteSpace(condition)) { h.Condition = condition.Trim(); h.Cond = DebugExpr.Compile(h.Condition, c); }
        lock (sync) { h.Id = nextHookId++; writeHooks.Add(h); }
        Rearm();
        if (action == WriteHookAction.Freeze) ApplyFreeze(c, h);
        return h;
    }

    public bool RemoveWriteHook(int id)
    {
        bool ok;
        lock (sync) ok = writeHooks.RemoveAll(h => h.Id == id) > 0;
        Rearm();
        return ok;
    }

    public int ClearWriteHooks()
    {
        int n;
        lock (sync) { n = writeHooks.Count; writeHooks.Clear(); }
        Rearm();
        return n;
    }

    public bool SetWriteHookEnabled(int id, bool on)
    {
        bool ok = false;
        lock (sync) foreach (var h in writeHooks) if (h.Id == id) { h.Enabled = on; ok = true; }
        Rearm();
        return ok;
    }

    /// <summary>Puts every freeze's value back in place (after a state load or a reset, which brought other bytes in).</summary>
    public void ApplyFreezes()
    {
        foreach (var h in WriteHooks)
            if (h.Enabled && h.Action == WriteHookAction.Freeze && Machine.Cpu(h.CpuId) is { } c) ApplyFreeze(c, h);
    }

    private static void ApplyFreeze(DebugCpu c, WriteHook h)
    {
        byte v = (byte)h.Value!.Value;
        int left = 0x10000;   // a freeze over a huge range only holds what is written (the first 64 KB are put in place)
        foreach (var r in h.Ranges)
            for (uint a = r.Start; a <= r.End && left > 0; a++, left--) c.PokeSpace(r.Space, a, v);
    }

    private void RebuildWriteHooks(IReadOnlyList<DebugCpu> cpus)
    {
        var na = new WriteHook[cpus.Count][];
        for (int i = 0; i < cpus.Count; i++)
        {
            string id = cpus[i].Id;
            na[i] = writeHooks.Where(h => h.Enabled && h.CpuId == id).ToArray();
        }
        hookArmed = na;
    }

    /// <summary>
    /// A CPU write about to land (the machine's write tap). Runs the hooks that match, in the order they were added: each sees the value
    /// the ones before it left. False: the write is dropped. A break hook parks the machine here, before the write lands.
    /// </summary>
    internal bool FilterWrite(DebugCpu cpu, uint address, ref byte value)
    {
        var all = hookArmed;
        if (cpu.Index >= all.Length) return true;
        var hooks = all[cpu.Index];
        if (hooks.Length == 0) return true;
        Span<SpaceKey> keys = stackalloc SpaceKey[4];
        int n = cpu.AccessKeys(address, true, keys);
        var st = cpu.State;
        bool land = true;
        WriteHook? breaker = null;
        byte firstWritten = value;
        foreach (var h in hooks)
        {
            bool m = false;
            for (int i = 0; i < n && !m; i++) m = h.Matches(keys[i]);
            if (!m) continue;
            if (h.Cond != null)
            {
                st.Ctx.Value = value; st.Ctx.Address = address; st.Ctx.Source = (long)AccessSource.Cpu;
                if (!h.Cond.IsTrue(st.Ctx)) continue;
            }
            h.Hits++;
            byte before = value;
            switch (h.Action)
            {
                case WriteHookAction.Rewrite: value = h.Value is int fv ? (byte)fv : (byte)((value & h.And | h.Or) ^ h.Xor); break;
                case WriteHookAction.Freeze: value = (byte)h.Value!.Value; break;
                case WriteHookAction.Block: land = false; break;
                case WriteHookAction.Break: breaker ??= h; break;
            }
            if (!land || value != before) h.Changed++;
            if (h.Logged) Note(cpu, st, h, address, before, land ? value : -1);
            if (!land) break;   // a dropped write: the hooks after it have nothing to see
        }
        if (breaker != null && land && !HookBreaksOff)
        {
            var b = new BreakInfo
            {
                Reason = "write-hook", CpuId = cpu.Id, BreakpointId = breaker.Id, Kind = AccessKind.Write, Address = address, Value = value, Source = AccessSource.Cpu,
                AccessPc = st.HaveLast ? st.LastPc : cpu.PC,
            };
            string? sym = SymbolAt(cpu, address);
            b.Text = $"write-hook #{breaker.Id} write {cpu.FormatAddress(address)}{(sym != null ? $" ({sym})" : "")} = ${value:X2} (was ${cpu.Peek(address):X2}{(firstWritten != value ? $", the CPU wrote ${firstWritten:X2}" : "")}), NOT yet applied: " +
                     $"it lands when the machine goes on | from the instruction at {cpu.FormatAddress(b.AccessPc)} | frame {Machine.Frame} line {Machine.Scanline} dot {Machine.Dot}";
            CancelSteps(cpu);
            Park(cpu, b);
        }
        return land;
    }

    private void Note(DebugCpu cpu, CpuDebugState st, WriteHook h, uint address, byte written, int landed)
    {
        uint pc = st.HaveLast ? st.LastPc : cpu.PC;
        byte old = cpu.Peek(address);
        string stack = "";
        if (st.Stack.Count > 0)
        {
            var sb = new StringBuilder();
            for (int i = st.Stack.Count - 1, k = 0; i >= 0 && k < 8; i--, k++)
            {
                var f = st.Stack[i];
                if (k > 0) sb.Append(" <- ");
                sb.Append(f.Kind).Append(' ').Append(SymbolAt(cpu, f.Target) ?? cpu.FormatAddress(f.Target));
            }
            if (st.Stack.Count > 8) sb.Append(" <- ...");
            stack = sb.ToString();
        }
        string? sym = SymbolAt(cpu, address), pcs = SymbolAt(cpu, pc);
        string what = landed < 0 ? "dropped" : landed != written ? $"-> ${landed:X2}" : "";
        string text = string.Create(CultureInfo.InvariantCulture,
            $"#{h.Id} {WriteHook.ActionText(h.Action)} {cpu.FormatAddress(address)}{(sym != null ? $" ({sym})" : "")} ${old:X2} <- ${written:X2}{(what.Length > 0 ? " " + what : "")} pc {cpu.FormatAddress(pc)}{(pcs != null ? $" ({pcs})" : "")} frame {Machine.Frame} line {Machine.Scanline} cycle {Machine.Cycle}{(stack.Length > 0 ? " | " + stack : "")}");
        lock (hookLog)
        {
            hookLog.Add(new WriteHookRecord
            {
                Seq = ++hookSeq, HookId = h.Id, Action = h.Action, CpuId = cpu.Id, Frame = Machine.Frame, Scanline = Machine.Scanline, Cycle = Machine.Cycle, Pc = pc, PcSymbol = pcs,
                Address = address, Old = old, Written = written, Landed = landed, Stack = stack, Text = text,
            });
            if (hookLog.Count > MaxHookLog) hookLog.RemoveRange(0, hookLog.Count - MaxHookLog);
        }
        if (h.Action == WriteHookAction.Log) Log?.WriteLine("[hook] " + text);
    }
}
