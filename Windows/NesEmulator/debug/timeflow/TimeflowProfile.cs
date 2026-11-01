using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Debugging.Port;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Timeflow;

// =====================================================================================================================================
//  T3: profiled spans (docs/projects/brokendev-timeflow-plan.md, "Level 2: profiled spans" and "T3 as built").
//
//  A span [From, To] of a timeline is re-run from the keyframe at or before From (restored, loaded, proved equal to the record), the
//  recorded pads replayed, every frame's picture and RAM (and the whole state at keyframes) checked against the recording, and from From on
//  the D1 debugger's hooks feed a ProfileRecorder: every instruction of every CPU the debugger covers (PC, the instruction, registers, flags,
//  cycle, scanline, dot), every memory access (address, value, who, the instruction that made it, the canonical keys of the place), the
//  call stack's pushes and pops, the debug port's markers, and the named variables when they change. The records go to sinks: the streaming
//  queries (TimeflowProfileQueries.cs), which keep only their answers, and, when a span must be kept, the store (TimeflowProfileStore.cs).
// =====================================================================================================================================

/// <summary>One instruction (or interrupt entry) of a CPU: the state before it ran.</summary>
public struct ProfInstr
{
    /// <summary>The position in the profile: 0 at the first profiled instruction, counting every CPU's instructions in the order they ran.</summary>
    public long Seq;
    public byte Cpu;
    /// <summary>The timeline frame (the frame whose run made it).</summary>
    public long Frame;
    /// <summary>PC, instruction bytes, registers, flags, cycle (the machine's clock), scanline and dot; Kind 1 NMI, 2 IRQ, 3 another interrupt entry.</summary>
    public TraceRecord R;
    /// <summary>The canonical keys of the PC (the CPU address folded, the ROM / RAM place it runs from): its symbol, whatever bank is mapped later.</summary>
    public byte NKeys;
    public SpaceKey K0, K1, K2, K3;
    public readonly ReadOnlySpan<SpaceKey> Keys(Span<SpaceKey> into) { into[0] = K0; into[1] = K1; into[2] = K2; into[3] = K3; return into[..NKeys]; }
}

/// <summary>One memory access (a CPU's or a DMA unit's).</summary>
public struct ProfAccess
{
    /// <summary>The instruction that made it (its <see cref="ProfInstr.Seq"/>; a DMA byte belongs to the instruction that started the DMA); -1 before the CPU's first.</summary>
    public long Instr;
    public uint Pc;
    public byte Cpu;
    public long Frame;
    /// <summary>The instruction's cycle.</summary>
    public long Cycle;
    public short Scanline, Dot;
    public uint Address;
    public byte Value;
    public bool Write;
    public AccessSource Source;
    public sbyte Channel;
    public byte NKeys;
    public SpaceKey K0, K1, K2, K3;
    public readonly ReadOnlySpan<SpaceKey> Keys(Span<SpaceKey> into) { into[0] = K0; into[1] = K1; into[2] = K2; into[3] = K3; return into[..NKeys]; }
}

/// <summary>A call frame pushed (a call instruction or an interrupt entry, seen at the callee's first step) or popped (its return).</summary>
public struct ProfCall
{
    /// <summary>The next instruction's position (the callee's first, or the one after the return).</summary>
    public long Seq;
    public byte Cpu;
    public long Frame;
    public bool Return;
    public string Kind;
    public uint CallerPc, Target, ReturnPc;
}

/// <summary>A named variable changed: its new and old value (a variable of 1 to 4 bytes as a little-endian number; a larger one byte by byte).</summary>
public struct ProfVar
{
    public int Index;
    /// <summary>-1 a whole variable (1-4 bytes); else the byte of a larger one.</summary>
    public int Byte;
    public long Old, New;
    /// <summary>The instruction whose write changed it (the last write to it before the change was seen).</summary>
    public long Instr;
    public uint Pc;
    public byte Cpu;
    public long Frame;
    public long Cycle;
}

/// <summary>A debug-port marker (D7) of the game.</summary>
public struct ProfMarker
{
    public long Seq;
    public long Frame;
    public string Text;
    public uint Pc;
}

/// <summary>The start of a frame: its pads, and where the profile is.</summary>
public struct ProfFrame
{
    public long Frame;
    /// <summary>The position of the frame's first instruction.</summary>
    public long Seq;
    public PadButtons P1, P2;
}

/// <summary>
/// What gets a profile's records: a streaming query (it keeps its answer, nothing else) or the store. Records arrive in the order they
/// happened. A stored profile read back feeds the same calls, so a query answers the same over the stream and over a kept profile.
/// </summary>
public abstract class ProfileSink
{
    public virtual void Begin(ProfileContext ctx) { }
    public virtual void OnFrame(in ProfFrame f) { }
    /// <summary>A CPU's whole call stack at a frame's start (outermost first): the frames already entered when it began.</summary>
    public virtual void OnStack(int cpu, IReadOnlyList<ProfCall> frames) { }
    public virtual void OnInstr(in ProfInstr i) { }
    public virtual void OnAccess(in ProfAccess a) { }
    public virtual void OnCall(in ProfCall c) { }
    public virtual void OnVar(in ProfVar v) { }
    public virtual void OnMarker(in ProfMarker m) { }
    /// <summary>The span is over (or it went out of sync, or the size limit stopped the store: <see cref="ProfileContext"/> says).</summary>
    public virtual void End() { }
    /// <summary>True when the sink needs no more records (every sink done: the re-run may stop early).</summary>
    public virtual bool Done => false;
    /// <summary>The frame after which nothing can change the answer (the runner stops there when every sink agrees); long.MaxValue: the span's end.</summary>
    public virtual long LastFrameNeeded => long.MaxValue;
}

/// <summary>What the sinks know about the profile: the machine (to name symbols, disassemble and resolve address specs), the span, the variables.</summary>
public sealed class ProfileContext
{
    public DebugMachine Machine { get; init; } = null!;
    public Debugger Debugger { get; init; } = null!;
    /// <summary>True while the machine runs the span (a streaming query may read it live at an instruction boundary); false over a stored profile.</summary>
    public bool Live { get; init; }
    public string Console { get; init; } = "";
    public string TimelineId { get; init; } = "";
    public long From { get; init; }
    public long To { get; init; }
    /// <summary>The keyframe the re-run began at: call frames entered before it are not known.</summary>
    public long StackKnownFrom { get; init; }
    public IReadOnlyList<ProfileVarInfo> Vars { get; init; } = Array.Empty<ProfileVarInfo>();
    /// <summary>The frame the re-run went out of sync at (its answers past it are not the recorded game's), or null.</summary>
    public long? OffFrame { get; set; }
    public string OffWhat { get; set; } = "";

    private readonly Dictionary<(int, SpaceKey, SpaceKey), string?> symCache = new();

    public DebugCpu Cpu(int i) => Machine.Cpus[i];
    public string CpuId(int i) => i < Machine.Cpus.Count ? Machine.Cpus[i].Id : "cpu" + i;
    public int CpuIndex(string id)
    {
        for (int i = 0; i < Machine.Cpus.Count; i++) if (Machine.Cpus[i].Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return i;
        throw new ArgumentException($"no CPU '{id}' on the {Machine.Console} (CPUs: {string.Join(", ", Machine.Cpus.Select(c => c.Id))})");
    }

    /// <summary>The symbol at a place the profile recorded (its keys), cached.</summary>
    public string? SymbolAt(int cpu, ReadOnlySpan<SpaceKey> keys)
    {
        if (Debugger.Symbols.All.Count == 0 || keys.Length == 0) return null;
        var k = (cpu, keys[0], keys[^1]);
        if (symCache.TryGetValue(k, out var s)) return s;
        s = Debugger.SymbolAtKeys(Machine.Cpus[cpu], keys);
        if (symCache.Count > 200_000) symCache.Clear();
        symCache[k] = s;
        return s;
    }

    public string? SymbolOf(in ProfInstr i) { Span<SpaceKey> b = stackalloc SpaceKey[4]; return SymbolAt(i.Cpu, i.Keys(b)); }
    public string? SymbolOfTarget(in ProfAccess a) { Span<SpaceKey> b = stackalloc SpaceKey[4]; return SymbolAt(a.Cpu, a.Keys(b)); }

    /// <summary>The function a PC is in: the code symbol containing it ("name", without "+$N"), else null.</summary>
    public string? FunctionOf(in ProfInstr i)
    {
        string? s = SymbolOf(in i);
        if (s == null) return null;
        int plus = s.IndexOf("+$", StringComparison.Ordinal);
        return plus > 0 ? s[..plus] : s;
    }

    /// <summary>The symbol at a CPU address with the banks the machine has now (an operand label; for a stored profile, the banks at its opening).</summary>
    public string? SymbolAtAddress(int cpu, uint address) => Debugger.Symbols.All.Count == 0 ? null : Debugger.SymbolAt(Machine.Cpus[cpu], address);

    public string Address(int cpu, uint a) => Cpu(cpu).FormatAddress(a);

    /// <summary>The instruction as text ("STA $0050", "-- NMI --").</summary>
    public string Disasm(in ProfInstr i)
    {
        if (i.R.Kind != 0) return i.R.Kind == 1 ? "-- NMI --" : i.R.Kind == 2 ? "-- IRQ --" : "-- interrupt --";
        var c = Cpu(i.Cpu);
        Span<byte> bytes = stackalloc byte[4];
        for (int k = 0; k < 4; k++) bytes[k] = i.R.Byte(k);
        var ctx = c.ContextOf(in i.R);
        int cpu = i.Cpu;
        Func<uint, string?>? label = Debugger.Symbols.All.Count > 0 ? a => { string? s = SymbolAtAddress(cpu, a); return s != null && !s.Contains('+') ? s : null; } : null;
        return c.Disassembler.Format(i.R.Pc, bytes[..Math.Max(1, (int)i.R.Length)], ctx, label);
    }

    /// <summary>A trace line with Mesen's columns (PC, instruction, [address] = value, registers, V, H), then the frame, the cycle, the position and the symbol.</summary>
    public string TraceLine(in ProfInstr i)
    {
        var c = Cpu(i.Cpu);
        var sb = new StringBuilder(140);
        sb.Append(i.R.Pc.ToString(c.AddressDigits == 6 ? "X6" : "X4")).Append("  ");
        int start = sb.Length;
        sb.Append(Disasm(in i));
        if (i.R.Kind == 0 && i.R.Ea >= 0) sb.Append(" [").Append(((uint)i.R.Ea).ToString(c.AddressDigits == 6 && i.R.Ea > 0xFFFF ? "X6" : "X4")).Append("] = $").Append(i.R.EaValue.ToString("X2"));
        sb.Append(' ', Math.Max(1, start + 40 - sb.Length));
        sb.Append(c.FormatRegisters(in i.R));
        sb.Append(" V:").Append(i.R.Scanline.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append(" H:").Append(i.R.Dot.ToString(CultureInfo.InvariantCulture).PadLeft(3));
        sb.Append(" Fr:").Append(i.Frame).Append(" Cyc:").Append(i.R.Cycle).Append(" #").Append(i.Seq);
        if (Machine.Cpus.Count > 1) sb.Append(' ').Append(c.Id);
        string? at = SymbolOf(in i);
        if (at != null) sb.Append("  ; ").Append(at);
        return sb.ToString();
    }

    /// <summary>
    /// A register's value in a trace record, by name, as the CPU's trace columns name it (A, X, Y, S, P, D, DB, B, C, H, L, F, ...; PC from the
    /// record): the record's columns are formatted and read back, so every CPU's packing (the Game Boy's register pairs) is its own adapter's.
    /// Flags (P, PSW, F) read as their bits (a letter per bit from bit 7, upper case set).
    /// </summary>
    public bool TryRegister(in ProfInstr i, string name, out long value)
    {
        value = 0;
        if (name.Equals("PC", StringComparison.OrdinalIgnoreCase)) { value = i.R.Pc; return true; }
        foreach (var (n, v) in Columns(Cpu(i.Cpu).FormatRegisters(in i.R)))
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = v; return true; }
        return false;
    }

    /// <summary>The register names a CPU's trace columns show.</summary>
    public IReadOnlyCollection<string> RegisterNames(int cpu)
    {
        var probe = new TraceRecord();
        return Columns(Cpu(cpu).FormatRegisters(in probe)).Select(c => c.Name).Append("PC").ToArray();
    }

    private static IEnumerable<(string Name, long Value)> Columns(string text)
    {
        foreach (var tok in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = tok.IndexOf(':');
            if (colon <= 0) continue;
            string n = tok[..colon], v = tok[(colon + 1)..];
            if (long.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long x)) { yield return (n, x); continue; }
            long bits = 0;
            for (int k = 0; k < v.Length && k < 8; k++) if (char.IsUpper(v[k])) bits |= 1L << (7 - k);
            yield return (n, bits);
        }
    }
}

/// <summary>A named variable the profile follows: a data symbol of 1 to 4 bytes (a number) or more (byte by byte).</summary>
public sealed record ProfileVarInfo(string Name, int Space, uint Offset, int Size);

/// <summary>The machine a span re-runs on: its debugger (with the game's symbols), its Timeflow source, and how to run one frame with both pads.</summary>
public sealed class ProfileTarget
{
    public DebugMachine Machine { get; init; } = null!;
    public Debugger Debugger { get; init; } = null!;
    public TimeflowSource Source { get; init; } = null!;
    public Action<PadButtons, PadButtons> RunFrame { get; init; } = null!;
    /// <summary>The machine's debug port (D7), when it has one: its markers go into the profile.</summary>
    public DebugPort? Port { get; init; }
    /// <summary>
    /// The machine was just powered on with the timeline's ROM, cores and power-on RAM, and has run nothing. A span in the first keyframe block of a
    /// timeline that starts at power-on then re-runs from this power-on itself (its state checked equal to the recorded frame-0 state) rather than
    /// from the loaded frame-0 state. T3 added this because a NES loaded with its power-on state ran its first frame a cycle off (its pending reset
    /// sequence was not in the state: fixed in T4, the state now says it, and a replay from a loaded frame 0 stays in sync, --timeflow-test
    /// group poweron). It is kept because it still guards something: a loaded NES shows the picture from before the load for one frame, so only a
    /// re-run from the power-on itself compares frame 0's picture too (and timelines recorded before T4, whose frame-0 state lacks the field,
    /// fall back to the loaded state, said in the notes).
    /// </summary>
    public bool PoweredOn { get; init; }
}

/// <summary>What to profile.</summary>
public sealed class ProfileSpan
{
    /// <summary>The first and last frame profiled (both included).</summary>
    public long From, To;
    /// <summary>The variables to follow (symbol names); null: every data symbol (RAM / cartridge RAM) of the symbols loaded.</summary>
    public List<string>? Vars;
    /// <summary>At most this many variables are followed (the first, by address); more are said in the report.</summary>
    public int MaxVars = 4096;
    /// <summary>Stop at the first frame out of sync (the default) or note it and go on.</summary>
    public bool StopWhenOff = true;
    /// <summary>A test's hook: called on the emulation thread before each frame of the re-run runs (the forced-desync control pokes RAM here).</summary>
    public Action<long>? BeforeFrame;
    /// <summary>Asked between frames: true stops the re-run (the desktop's Cancel).</summary>
    public Func<bool>? Cancel;
    /// <summary>Told after each profiled frame (frames done, of how many).</summary>
    public Action<long, long>? Progress;
}

/// <summary>What a profiled re-run did: in sync or not, what it saw, what it cost.</summary>
public sealed class ProfileReport
{
    public string TimelineId = "", Console = "";
    public long From, To, KeyFrame;
    public long FramesProfiled, PreRollFrames;
    public long? OffFrame;
    public string OffWhat = "";
    public bool InSync => OffFrame == null;
    public bool PictureHeld;
    public bool Cancelled;
    public bool StoppedEarly;
    public long Instructions, Interrupts, Accesses, Calls, VarChanges, Markers;
    public Dictionary<string, long> InstructionsByCpu = new();
    public List<string> Cpus = new();
    public List<string> NotProfiled = new();
    public int VarsFollowed;
    public int VarsSkipped;
    public double JumpMs, PreRollMs, ProfileMs;
    public double Fps;
    /// <summary>The console's real frame rate (NES / SNES NTSC 60.0988, Game Boy 59.7275).</summary>
    public double RealFps;
    /// <summary>How many times slower than real time the profiled frames ran (1 = real time).</summary>
    public double TimesSlower => Fps > 0 ? RealFps / Fps : 0;
    public List<string> Notes = new();
}

/// <summary>The observer that turns the debugger's hooks into profile records.</summary>
internal sealed class ProfileRecorder : DebugObserver
{
    private readonly DebugMachine m;
    private readonly ProfileSink[] sinks;
    private readonly int ncpu;
    private readonly long[] cpuSeq;
    private readonly uint[] cpuPc;
    private readonly long[] cpuCycle;
    private long seq;
    public long Frame;
    public long Instructions, Interrupts, Accesses, Calls, VarChanges, Markers;
    public readonly long[] ByCpu;
    // the variables
    private readonly ProfileVarInfo[] vars;
    private readonly byte[][] last;
    private readonly Dictionary<SpaceKey, int> varAt = new();
    private readonly bool[] dirty;
    private readonly List<int> dirtyList = new();
    private readonly long[] wInstr; private readonly uint[] wPc; private readonly byte[] wCpu; private readonly long[] wCycle;
    private readonly DebugCpu varCpu;

    public ProfileRecorder(DebugMachine m, ProfileSink[] sinks, ProfileVarInfo[] vars)
    {
        this.m = m; this.sinks = sinks; this.vars = vars;
        ncpu = m.Cpus.Count;
        cpuSeq = Enumerable.Repeat(-1L, ncpu).ToArray();
        cpuPc = new uint[ncpu]; cpuCycle = new long[ncpu]; ByCpu = new long[ncpu];
        varCpu = m.Main;
        last = new byte[vars.Length][];
        dirty = new bool[vars.Length];
        wInstr = new long[vars.Length]; wPc = new uint[vars.Length]; wCpu = new byte[vars.Length]; wCycle = new long[vars.Length];
        for (int v = 0; v < vars.Length; v++)
        {
            var x = vars[v];
            last[v] = new byte[x.Size];
            for (int b = 0; b < x.Size; b++)
            {
                last[v][b] = varCpu.PeekSpace(x.Space, (uint)(x.Offset + b));
                varAt.TryAdd(new SpaceKey(x.Space, (uint)(x.Offset + b)), v);
            }
        }
    }

    public long Seq => seq;

    public override bool WantsSteps(int cpuIndex) => true;
    public override bool WantsAccesses(int cpuIndex) => true;

    public override void OnStep(DebugCpu cpu, long cycle, bool executes, string? interrupt)
    {
        if (!executes && interrupt == null) return;   // a halted cycle
        if (dirtyList.Count > 0) FlushVars();
        int ci = cpu.Index;
        var p = new ProfInstr { Seq = seq++, Cpu = (byte)ci, Frame = Frame };
        p.R = new TraceRecord { Cycle = cycle, Frame = Frame, Scanline = (short)m.Scanline, Dot = (short)m.Dot, Pc = cpu.PC, Ea = -1, Kind = interrupt == null ? (byte)0 : interrupt == "NMI" ? (byte)1 : interrupt == "IRQ" ? (byte)2 : (byte)3 };
        cpu.FillTrace(ref p.R);
        Span<SpaceKey> keys = stackalloc SpaceKey[4];
        int n = cpu.AccessKeys(p.R.Pc, false, keys);
        p.NKeys = (byte)n;
        if (n > 0) p.K0 = keys[0]; if (n > 1) p.K1 = keys[1]; if (n > 2) p.K2 = keys[2]; if (n > 3) p.K3 = keys[3];
        cpuSeq[ci] = p.Seq; cpuPc[ci] = p.R.Pc; cpuCycle[ci] = cycle;
        if (interrupt != null) Interrupts++; else { Instructions++; ByCpu[ci]++; }
        for (int s = 0; s < sinks.Length; s++) sinks[s].OnInstr(in p);
    }

    public override void OnAccess(DebugCpu cpu, uint address, byte value, bool write, AccessSource source, int channel)
    {
        int ci = cpu.Index;
        var a = new ProfAccess
        {
            Instr = cpuSeq[ci], Pc = cpuPc[ci], Cpu = (byte)ci, Frame = Frame, Cycle = cpuCycle[ci], Scanline = (short)m.Scanline, Dot = (short)m.Dot,
            Address = address, Value = value, Write = write, Source = source, Channel = (sbyte)channel,
        };
        Span<SpaceKey> keys = stackalloc SpaceKey[4];
        int n = cpu.AccessKeys(address, write, keys);
        a.NKeys = (byte)n;
        if (n > 0) a.K0 = keys[0]; if (n > 1) a.K1 = keys[1]; if (n > 2) a.K2 = keys[2]; if (n > 3) a.K3 = keys[3];
        Accesses++;
        if (write && ci == 0 && varAt.Count > 0)
            for (int k = 0; k < n; k++)
                if (varAt.TryGetValue(keys[k], out int v))
                {
                    if (!dirty[v]) { dirty[v] = true; dirtyList.Add(v); }
                    wInstr[v] = a.Instr; wPc[v] = a.Pc; wCpu[v] = (byte)ci; wCycle[v] = a.Cycle;
                    break;
                }
        for (int s = 0; s < sinks.Length; s++) sinks[s].OnAccess(in a);
    }

    public override void OnCall(DebugCpu cpu, CallFrame frame) => Call(cpu, frame, false);
    public override void OnReturn(DebugCpu cpu, CallFrame frame) => Call(cpu, frame, true);

    private void Call(DebugCpu cpu, CallFrame f, bool ret)
    {
        Calls++;
        var c = new ProfCall { Seq = seq, Cpu = (byte)cpu.Index, Frame = Frame, Return = ret, Kind = f.Kind, CallerPc = f.CallerPc, Target = f.Target, ReturnPc = f.ReturnPc };
        for (int s = 0; s < sinks.Length; s++) sinks[s].OnCall(in c);
    }

    public void Marker(PortEvent e)
    {
        if (e.Kind != PortEventKind.Marker) return;
        Markers++;
        var mk = new ProfMarker { Seq = seq, Frame = Frame, Text = e.Text, Pc = e.Pc };
        for (int s = 0; s < sinks.Length; s++) sinks[s].OnMarker(in mk);
    }

    /// <summary>The variables written since the last instruction: read back, a record for each that changed.</summary>
    public void FlushVars()
    {
        foreach (int v in dirtyList)
        {
            dirty[v] = false;
            var x = vars[v];
            var old = last[v];
            if (x.Size <= 4)
            {
                long o = 0, nw = 0; bool changed = false;
                for (int b = 0; b < x.Size; b++)
                {
                    byte now = varCpu.PeekSpace(x.Space, (uint)(x.Offset + b));
                    if (now != old[b]) changed = true;
                    o |= (long)old[b] << (8 * b); nw |= (long)now << (8 * b);
                    old[b] = now;
                }
                if (changed) Emit(v, -1, o, nw);
            }
            else
                for (int b = 0; b < x.Size; b++)
                {
                    byte now = varCpu.PeekSpace(x.Space, (uint)(x.Offset + b));
                    if (now == old[b]) continue;
                    Emit(v, b, old[b], now);
                    old[b] = now;
                }
        }
        dirtyList.Clear();
    }

    private void Emit(int v, int b, long o, long nw)
    {
        VarChanges++;
        var r = new ProfVar { Index = v, Byte = b, Old = o, New = nw, Instr = wInstr[v], Pc = wPc[v], Cpu = wCpu[v], Frame = Frame, Cycle = wCycle[v] };
        for (int s = 0; s < sinks.Length; s++) sinks[s].OnVar(in r);
    }

    /// <summary>A frame begins: its record, then each CPU's call stack as the debugger has it (the frames already entered).</summary>
    public void BeginFrame(long frame, PadButtons p1, PadButtons p2, Debugger d)
    {
        if (dirtyList.Count > 0) FlushVars();
        Frame = frame;
        var f = new ProfFrame { Frame = frame, Seq = seq, P1 = p1, P2 = p2 };
        for (int s = 0; s < sinks.Length; s++) sinks[s].OnFrame(in f);
        for (int ci = 0; ci < ncpu; ci++)
        {
            var st = d.CallStack(m.Cpus[ci].Id);
            var list = st.Select(c => new ProfCall { Seq = seq, Cpu = (byte)ci, Frame = frame, Kind = c.Kind, CallerPc = c.CallerPc, Target = c.Target, ReturnPc = c.ReturnPc }).ToList();
            for (int s = 0; s < sinks.Length; s++) sinks[s].OnStack(ci, list);
        }
    }

    public void EndFrame() { if (dirtyList.Count > 0) FlushVars(); }
}

/// <summary>
/// Re-runs a span of a timeline at full depth (<see cref="Run"/>). The re-run starts at the keyframe at or before <see cref="ProfileSpan.From"/>
/// (its state restored, loaded and proved equal to the recorded one), plays the recorded pads with the call stack tracked up to From, then
/// profiles From..To. Every frame's picture and RAM, and the whole state at every keyframe, are checked against the recording: a re-run that
/// goes off is reported (the frame and what differed), and its answers past that frame are not the recorded game's.
/// </summary>
public static class TimeflowProfiler
{
    /// <summary>The consoles' real frame rates (for "how many times slower than real time").</summary>
    public static double RealFps(string console) => console switch
    {
        "gb" or "gbc" => 59.7275,
        "sms" or "gg" or "md" => 59.9227,
        _ => 60.0988,
    };

    /// <summary>
    /// The variables a profile follows: the asked names, or every data symbol (bound on the main CPU outside ROM: work RAM, cartridge RAM,
    /// high RAM; on the NES its internal RAM) up to <paramref name="max"/>.
    /// </summary>
    public static List<ProfileVarInfo> ResolveVars(DebugMachine m, Debugger d, List<string>? names, int max, out int skipped)
    {
        skipped = 0;
        var cpu = m.Main;
        var list = new List<ProfileVarInfo>();
        if (names != null)
        {
            foreach (var n in names)
            {
                if (!d.Symbols.TryFind(n, out var sym, out string? err)) throw new ArgumentException(err ?? $"no symbol '{n}'");
                if (!cpu.BindSymbol(sym, out var k)) throw new ArgumentException($"symbol '{n}' is not on the {cpu.Name}");
                list.Add(new ProfileVarInfo(sym.Name, k.Space, k.Offset, Math.Clamp(sym.Size, 1, 4096)));
            }
            return list;
        }
        var seen = new HashSet<(int, uint)>();
        foreach (var sym in d.Symbols.All.OrderBy(s => s.Address))
        {
            if (sym.Name.StartsWith("SECTIONSTART_") || sym.Name.StartsWith("SECTIONEND_")) continue;
            if (!cpu.BindSymbol(sym, out var k)) continue;
            if (!IsData(m, cpu, k, sym)) continue;
            if (!seen.Add((k.Space, k.Offset))) continue;
            if (list.Count >= max) { skipped++; continue; }
            list.Add(new ProfileVarInfo(sym.Name, k.Space, k.Offset, Math.Clamp(sym.Size, 1, 256)));
        }
        return list;
    }

    private static bool IsData(DebugMachine m, DebugCpu cpu, SpaceKey k, DebugSymbol sym)
    {
        string space = cpu.Spaces.FirstOrDefault(s => s.Id == k.Space)?.Name ?? "";
        if (space is "wram" or "sram" or "hram" or "wramhi") return true;
        if (space != "cpu") return false;
        return m.Console switch
        {
            "nes" => k.Offset < 0x0800,
            "gb" or "gbc" => k.Offset is >= 0xC000 and < 0xE000 or >= 0xFF80 and < 0xFFFF,
            _ => false,
        };
    }

    /// <summary>Re-runs [span.From, span.To] of the timeline on <paramref name="t"/>, feeding every record to <paramref name="sinks"/>.</summary>
    public static ProfileReport Run(TimeflowPlayback pb, ProfileTarget t, ProfileSpan span, IReadOnlyList<ProfileSink> sinks, Action<ProfileContext>? onContext = null)
    {
        var meta = pb.Meta;
        var rd = pb.Reader;
        if (t.Machine == null || t.Debugger == null)
            throw new NotSupportedException($"profiling needs the debugger on the console's CPUs, which does not cover the {meta.Console}: no profile is made");
        if (span.From > span.To) throw new ArgumentException($"the span {span.From}-{span.To} ends before it starts");
        if (!rd.Has(span.From) || !rd.Has(span.To))
            throw new ArgumentOutOfRangeException(nameof(span), $"frames {span.From}-{span.To} are not all in timeline {meta.Id} (it holds frames {rd.StartFrame}-{rd.EndFrame - 1})");
        long key = span.From;
        while (key > rd.StartFrame && !rd.Row(key).IsKey) key--;
        var r = new ProfileReport { TimelineId = meta.Id, Console = meta.Console, From = span.From, To = span.To, KeyFrame = key, RealFps = RealFps(meta.Console) };
        r.Cpus.AddRange(t.Machine.Cpus.Select(c => $"{c.Id} ({c.Name})"));
        r.NotProfiled.AddRange(t.Machine.Limitations);
        var d = t.Debugger;
        var swAll = Stopwatch.StartNew();
        bool fromPowerOn = false;
        if (t.PoweredOn && key == rd.StartFrame && meta.PowerOnStart)
        {
            // the timeline began at power-on and so does this machine: start from the power-on itself when its state is the recorded one
            t.Source.ForgetHeader();
            fromPowerOn = t.Source.Capture().Hash() == rd.Row(key).State;
            r.Notes.Add(fromPowerOn ? $"re-run from the power-on itself (the machine's state at power-on equals the recorded frame {key}), not from a loaded state"
                                    : $"the machine's power-on state is not the recorded frame {key}'s: re-run from the loaded state");
        }
        if (!fromPowerOn) pb.Jump(t.Source, key);
        r.JumpMs = swAll.Elapsed.TotalMilliseconds;

        var vars = ResolveVars(t.Machine, d, span.Vars, span.MaxVars, out int skipped);
        r.VarsSkipped = skipped;
        ProfileRecorder? rec = null;
        bool trackBefore = d.TrackCallStack;
        Action<PortEvent>? onPort = null;
        var ctx = new ProfileContext
        {
            Machine = t.Machine, Debugger = d, Live = true, Console = meta.Console, TimelineId = meta.Id, From = span.From, To = span.To, StackKnownFrom = key, Vars = vars,
        };
        long lastNeeded = long.MaxValue;
        var sw = Stopwatch.StartNew();
        double preMs = 0;
        try
        {
            d.TrackCallStack = true;   // the call stack is known from the keyframe on
            for (long f = key; f <= span.To; f++)
            {
                if (span.Cancel?.Invoke() == true) { r.Cancelled = true; break; }
                var row = rd.Row(f);
                if (f > key && row.IsKey)
                {
                    var now = t.Source.Capture();
                    if (now.Hash() != row.State)
                    {
                        string why = "";
                        try { why = TimeflowPlayback.Explain(rd.State(f), now); } catch (InvalidDataException) { }
                        Off(r, ctx, f, "the whole state at a keyframe" + why + " (the re-run is not the recorded game from here)");
                        if (span.StopWhenOff) break;
                    }
                }
                if (f == span.From)
                {
                    preMs = sw.Elapsed.TotalMilliseconds;
                    r.PreRollFrames = f - key;
                    rec = new ProfileRecorder(t.Machine, sinks.ToArray(), vars.ToArray());
                    r.VarsFollowed = vars.Count;
                    onContext?.Invoke(ctx);
                    foreach (var s in sinks) s.Begin(ctx);
                    lastNeeded = sinks.Count == 0 ? span.To : sinks.Max(s => s.LastFrameNeeded);
                    if (t.Port != null) { var rr = rec; onPort = e => rr.Marker(e); t.Port.Event += onPort; }
                    d.AddObserver(rec);
                    sw.Restart();
                }
                var (p1, p2) = ((PadButtons)row.P1, (PadButtons)row.P2);
                rec?.BeginFrame(f, p1, p2, d);
                span.BeforeFrame?.Invoke(f);
                t.RunFrame(p1, p2);
                rec?.EndFrame();
                ulong pic = t.Source.PictureHash(), ram = t.Source.RamHash();
                bool held = f == key && meta.Console == "nes" && pic != row.Picture && ram == row.Ram;
                if (held) r.PictureHeld = true;
                else if (pic != row.Picture || ram != row.Ram)
                {
                    Off(r, ctx, f, (pic != row.Picture ? "the picture" : "") + (pic != row.Picture && ram != row.Ram ? " and " : "") + (ram != row.Ram ? "the work RAM" : "") + " after the frame");
                    if (span.StopWhenOff) { if (rec != null) r.FramesProfiled++; break; }
                }
                if (rec != null)
                {
                    r.FramesProfiled++;
                    span.Progress?.Invoke(r.FramesProfiled, span.To - span.From + 1);
                    if (f >= lastNeeded || (sinks.Count > 0 && sinks.All(s => s.Done))) { r.StoppedEarly = f < span.To; break; }
                }
            }
        }
        finally
        {
            if (rec != null) d.RemoveObserver(rec);
            if (onPort != null && t.Port != null) t.Port.Event -= onPort;
            d.TrackCallStack = trackBefore;
        }
        r.ProfileMs = rec != null ? sw.Elapsed.TotalMilliseconds : 0;
        r.PreRollMs = rec != null ? preMs : sw.Elapsed.TotalMilliseconds;
        if (rec != null)
        {
            foreach (var s in sinks) s.End();
            r.Instructions = rec.Instructions; r.Interrupts = rec.Interrupts; r.Accesses = rec.Accesses; r.Calls = rec.Calls; r.VarChanges = rec.VarChanges; r.Markers = rec.Markers;
            for (int i = 0; i < t.Machine.Cpus.Count; i++) r.InstructionsByCpu[t.Machine.Cpus[i].Id] = rec.ByCpu[i];
        }
        r.Fps = r.ProfileMs > 0 ? r.FramesProfiled / (r.ProfileMs / 1000.0) : 0;
        if (key < span.From) r.Notes.Add($"re-run from keyframe {key} ({span.From - key} frame(s) before the span, call stack tracked, not profiled)");
        if (r.PictureHeld) r.Notes.Add("the NES's first picture after a load is the one before it (a NES state holds no picture): that frame's picture was not compared, its RAM was");
        if (skipped > 0) r.Notes.Add($"{skipped} more data symbols were not followed as variables (the first {span.MaxVars} by address were)");
        TimeflowStore.Touch(pb.Dir);
        return r;
    }

    private static void Off(ProfileReport r, ProfileContext ctx, long f, string what)
    {
        if (r.OffFrame != null) return;
        r.OffFrame = f; r.OffWhat = what;
        ctx.OffFrame = f; ctx.OffWhat = what;
    }
}
