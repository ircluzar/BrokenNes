using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Corruption;
using NesEmulator.Debugging.Port;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Timeflow;

/// <summary>
/// The machine a search works with (one per game and cores): it names symbols and resolves address specs for the condition, it is the place the
/// state map is measured on, and the exact phase re-runs candidate frames on it. Opened by the host (Workshop's movie machine, the desktop's
/// <see cref="ProfileMachine"/>); <see cref="Machine"/> and <see cref="Debugger"/> are null on a console the debugger does not cover yet (the
/// Sega consoles until T0): their searches take only the clauses that need no debugger.
/// </summary>
public sealed class SearchMachine : IDisposable
{
    public DebugMachine? Machine { get; init; }
    public Debugger? Debugger { get; init; }
    public TimeflowSource Source { get; init; } = null!;
    public Action<PadButtons, PadButtons> RunFrame { get; init; } = null!;
    public DebugPort? Port { get; init; }
    /// <summary>The game: a <see cref="NES"/> or an <see cref="IConsoleSession"/> (its picture, its sound).</summary>
    public object Game { get; init; } = null!;
    /// <summary>The ROM's sha256 (the machine is refused for a timeline of another ROM).</summary>
    public string RomSha256 { get; init; } = "";
    public Action? OnDispose { get; init; }

    public bool HasDebugger => Machine != null && Debugger != null;

    /// <summary>The machine as a T3 re-run target. Never "powered on": a search jumps (loads states) between its re-runs, so every re-run starts from a loaded state.</summary>
    public ProfileTarget Profile() => new() { Machine = Machine!, Debugger = Debugger!, Source = Source, RunFrame = RunFrame, Port = Port, PoweredOn = false };

    /// <summary>The sound the machine made since the last call, as mono samples from -1 to 1 (the NES's APU_FIX queue or another core's buffer; a console session's stereo).</summary>
    public float[] DrainSound()
    {
        switch (Game)
        {
            case NES n:
                if (n.DebugBus?.ActiveAPU is APU_FIX fix) { int q = fix.GetQueuedSampleCount(); return q > 0 ? fix.GetAudioSamples(q) : Array.Empty<float>(); }
                return n.GetAudioBuffer();
            case IConsoleSession s:
            {
                var all = new List<float>();
                var buf = new short[4096];
                int k;
                while ((k = s.ReadSamples(buf)) > 0) for (int i = 0; i + 1 < k; i += 2) all.Add((buf[i] + buf[i + 1]) / 65536f);
                return all.ToArray();
            }
        }
        return Array.Empty<float>();
    }

    /// <summary>The picture as 0xRRGGBB pixels (the NES's frame buffer, a session's frame).</summary>
    public (uint[] Px, int W, int H) Picture() => DebugPictures.Frame(Game);

    public void Dispose() { try { OnDispose?.Invoke(); } catch (Exception) { } }
}

/// <summary>Opens the machine for a timeline (its own ROM, console and cores), with the debugger when the console has one, and these symbols.</summary>
public delegate SearchMachine SearchMachineOpener(string timelineDir, TimelineMeta meta, IReadOnlyList<string> symbols);

/// <summary>A memory read a frame's recorded state cannot answer (said, never guessed): the clause is then checked by a re-run instead.</summary>
public sealed class SearchUnmappableException : Exception { public SearchUnmappableException(string m) : base(m) { } }

/// <summary>
/// Where a memory byte lives in a timeline's recorded states, measured on the machine: the byte is changed, the machine's state taken, the
/// one byte of the state that changed (with the new value) is its place, and the byte is put back (the state must hash as before). A byte
/// whose place is not exactly one state byte (it lives in the text of the NES's registers, a register with side effects, a port that is not
/// memory, ROM) is unmappable, with the reason. One map per state layout (the sections' names and sizes).
/// </summary>
internal sealed class SearchStateMap
{
    private readonly SearchMachine m;
    private readonly DebugCpu? cpu;
    private readonly IReadOnlyList<IMemoryDomain> domains;
    public readonly string Layout;
    private readonly Dictionary<(int, long), (int Sec, int Off)> places = new();
    private readonly Dictionary<(int, long), string> unmappable = new();
    public int Measured => places.Count;

    public SearchStateMap(SearchMachine m, string layout)
    {
        this.m = m; Layout = layout;
        cpu = m.Machine?.Main;
        domains = m.Source.Domains;
    }

    private int[]? lengths;
    /// <summary>The state has this map's layout (the sections' sizes; the names are fixed by the console).</summary>
    public bool Fits(TimeflowState st)
    {
        lengths ??= Layout.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x[(x.LastIndexOf(':') + 1)..])).ToArray();
        if (st.Sections.Length != lengths.Length) return false;
        for (int i = 0; i < lengths.Length; i++) if (st.Sections[i].Length != lengths[i]) return false;
        return true;
    }

    public static string LayoutOf(TimeflowState st) => string.Join(",", st.Names.Zip(st.Sections, (n, s) => $"{n}:{s.Length}"));

    /// <summary>The space id a name means: the main CPU's spaces (the debugger's), then the memory domains (from 1000).</summary>
    public static bool TrySpace(DebugCpu? cpu, IReadOnlyList<IMemoryDomain> domains, string name, out int space)
    {
        var sp = cpu?.FindSpace(name);
        if (sp != null) { space = sp.Id; return true; }
        for (int i = 0; i < domains.Count; i++)
            if (domains[i].Id.Equals(name, StringComparison.OrdinalIgnoreCase) && domains[i].Unavailable == null) { space = 1000 + i; return true; }
        space = -1;
        return false;
    }

    public static byte Peek(DebugCpu? cpu, IReadOnlyList<IMemoryDomain> domains, int space, long a)
    {
        if (space >= 1000) { var d = domains[space - 1000]; return a >= 0 && a < d.Size ? d.Peek((int)a) : (byte)0; }
        if (cpu == null) throw new SearchUnmappableException(NoCpu);
        return space < 0 ? cpu.Peek((uint)a) : cpu.PeekSpace(space, (uint)a);
    }

    public const string NoCpu = "a CPU address needs the debugger on this console's CPU (the Sega consoles: milestone T0): name a memory domain instead, e.g. [RAM:$10]";

    private bool Poke(int space, long a, byte v)
    {
        if (space >= 1000) { var d = domains[space - 1000]; if (!d.Writable || a < 0 || a >= d.Size) return false; d.Poke((int)a, v); return true; }
        if (cpu == null) throw new SearchUnmappableException(NoCpu);
        return space < 0 ? cpu.Poke((uint)a, v) : cpu.PokeSpace(space, (uint)a, v);
    }

    private bool IsSafeToPoke(int space, long a, out string why)
    {
        why = "";
        if (space >= 1000)
        {
            var d = domains[space - 1000];
            if (d.Kind == DomainKind.Rom) { why = $"{d.Id} is ROM: it is not part of a recorded state"; return false; }
            return true;
        }
        if (cpu == null) { why = NoCpu; return false; }
        Span<SpaceKey> keys = stackalloc SpaceKey[4];
        int n = space < 0 ? cpu.AccessKeys((uint)a, false, keys) : 1;
        if (space >= 0) keys[0] = new SpaceKey(space, (uint)a);
        bool memory = false;
        for (int i = 0; i < n; i++)
        {
            int ks = keys[i].Space;
            string nm = cpu.Spaces.FirstOrDefault(s => s.Id == ks)?.Name ?? "";
            if (nm is "prg" or "rom" or "io" or "regs") { why = $"{cpu.FormatAddress((uint)a)} is {(nm is "io" or "regs" ? "a register" : "ROM")}: not memory a recorded state holds as a byte"; return false; }
            if (nm != "cpu") memory = true;
        }
        if (memory) return true;
        // a CPU address only the CPU space knows: memory the console's map calls RAM (the NES's $0000-$1FFF, the Game Boy's work / high RAM)
        string con = m.Machine?.Console ?? "";
        uint x = (uint)a;
        bool ram = con switch
        {
            "nes" => x < 0x2000,
            "gb" or "gbc" => x is >= 0xC000 and < 0xFE00 or >= 0xFF80 and < 0xFFFF,
            _ => false,
        };
        if (!ram) why = $"{cpu.FormatAddress(x)} is not RAM on the {con} (a port, open bus or a coprocessor): not measured from a recorded state";
        return ram;
    }

    /// <summary>The state byte that holds (space, address); measured once (the machine must hold a state of this map's layout).</summary>
    public (int Sec, int Off) Locate(int space, long a)
    {
        var key = (space, a);
        if (places.TryGetValue(key, out var p)) return p;
        if (unmappable.TryGetValue(key, out var why)) throw new SearchUnmappableException(why);
        try { p = Measure(space, a); places[key] = p; return p; }
        catch (SearchUnmappableException e) { unmappable[key] = e.Message; throw; }
    }

    private (int, int) Measure(int space, long a)
    {
        string where = space < 0 ? (cpu?.FormatAddress((uint)a) ?? $"${a:X}") : space >= 1000 ? $"{domains[space - 1000].Id}:${a:X}" : $"{cpu!.Spaces.First(s => s.Id == space).Name}:${a:X}";
        if (!IsSafeToPoke(space, a, out string why)) throw new SearchUnmappableException(why);
        var s0 = m.Source.Capture();
        if (LayoutOf(s0) != Layout) throw new SearchUnmappableException($"the machine does not hold a state of the timeline's layout (internal: {LayoutOf(s0)} vs {Layout})");
        byte v0 = Peek(cpu, domains, space, a), v1 = (byte)(v0 ^ 0x5A);
        if (!Poke(space, a, v1)) throw new SearchUnmappableException($"{where} cannot be written on this machine (so its place in the state cannot be measured)");
        TimeflowState s1;
        try { s1 = m.Source.Capture(); }
        finally { Poke(space, a, v0); }
        var s2 = m.Source.Capture();
        if (s2.Hash() != s0.Hash()) throw new InvalidDataException($"measuring where {where} is in the state left the machine changed (its state hashes {s2.Hash():x16}, not {s0.Hash():x16}): the search stops rather than read a wrong place");
        if (!s0.Skeleton.AsSpan().SequenceEqual(s1.Skeleton)) throw new SearchUnmappableException($"{where} is kept in the state's text (registers / timing), not as a byte of a memory");
        int found = 0, sec = -1, off = -1;
        for (int i = 0; i < s0.Sections.Length; i++)
        {
            var x = s0.Sections[i]; var y = s1.Sections[i];
            int n = Math.Min(x.Length, y.Length);
            for (int k = 0; k < n; k++)
                if (x[k] != y[k]) { found++; if (x[k] == v0 && y[k] == v1) { sec = i; off = k; } }
        }
        if (found != 1 || sec < 0) throw new SearchUnmappableException($"{where} is not one byte of the recorded state ({found} state bytes changed with it)");
        return (sec, off);
    }
}

/// <summary>
/// The names a state clause's expression reads: memory (by CPU address, the debugger's spaces, the memory domains), the timeline's watched
/// variables (by name: their recorded values), <c>frame</c>, and the symbols. Registers and the scanline / dot / cycle are refused: a state
/// clause is about memory at the end of a frame (an instruction's registers are a breakpoint's <c>if</c>). Reads come from the recorded state
/// (the coarse phase: <see cref="State"/> through the measured map) or from the running machine (the exact phase).
/// </summary>
internal sealed class SearchStateEnv : IExprEnvironment
{
    private readonly DebugCpu? cpu;
    private readonly IReadOnlyList<IMemoryDomain> domains;
    private readonly List<string> varNames;
    public long Frame;
    public long[] Vars = Array.Empty<long>();
    /// <summary>Coarse: the recorded state at the frame's end (fetched when a memory byte is first read) and the map; exact: null (reads the machine).</summary>
    public Func<TimeflowState>? State;
    public SearchStateMap? Map;
    /// <summary>The map for a state of another layout (measured when first needed).</summary>
    public Func<TimeflowState, string, SearchStateMap>? MapFor;
    public bool ReadMemory;
    private TimeflowState? cur;

    public SearchStateEnv(DebugCpu? cpu, IReadOnlyList<IMemoryDomain> domains, IEnumerable<string> watched)
    {
        this.cpu = cpu; this.domains = domains;
        varNames = watched.Select(v => { int eq = v.IndexOf('='); return (eq > 0 ? v[..eq] : v).Trim().ToLowerInvariant(); }).ToList();
    }

    public void NextFrame(long frame, long[] vars) { Frame = frame; Vars = vars; cur = null; ReadMemory = false; }

    bool IExprEnvironment.TryRegister(string name, out Func<long> read)
    {
        if (cpu != null && cpu.RegisterNames.Any(r => r.Equals(name, StringComparison.OrdinalIgnoreCase)) && !varNames.Contains(name.ToLowerInvariant()))
            throw new FormatException($"'{name}' is a register: a state clause is about memory at the end of a frame (a register at an instruction is a breakpoint's condition: exec <where> if {name} == 3)");
        read = null!;
        return false;
    }

    bool IExprEnvironment.TryMachineValue(string name, out Func<long> read)
    {
        int vi = varNames.IndexOf(name);
        if (vi >= 0) { read = () => vi < Vars.Length ? Vars[vi] : throw new SearchUnmappableException($"the watched variable '{name}' has no value in this frame's index"); return true; }
        if (name == "frame") { read = () => Frame; return true; }
        if (name is "scanline" or "dot" or "cycle") throw new FormatException($"'{name}' has no meaning at the end of a frame (a state clause): use it in a breakpoint's condition (exec <where> if {name} > 200)");
        read = null!;
        return false;
    }

    bool IExprEnvironment.TrySymbol(string name, out long address, out int space, out string? error)
    {
        address = 0; space = -1; error = null;
        if (cpu == null) { error = $"'{name}': symbols need the debugger on this console (T0 for the Sega consoles): name a memory domain, [RAM:$10]"; return false; }
        return ((IExprEnvironment)cpu).TrySymbol(name, out address, out space, out error);
    }

    bool IExprEnvironment.TrySpace(string name, out int space) => SearchStateMap.TrySpace(cpu, domains, name, out space);

    byte IExprEnvironment.ReadCpu(long address) => Read(-1, address & (cpu?.AddressDigits == 6 ? 0xFFFFFF : 0xFFFF));
    byte IExprEnvironment.ReadSpace(int space, long offset) => Read(space, offset);

    private byte Read(int space, long a)
    {
        ReadMemory = true;
        if (State == null) return SearchStateMap.Peek(cpu, domains, space, a);
        if (cur == null)
        {
            cur = State();
            if (Map == null || !Map.Fits(cur))
            {
                Map = MapFor?.Invoke(cur, SearchStateMap.LayoutOf(cur));
                if (Map == null || !Map.Fits(cur)) throw new SearchUnmappableException("this frame's state has a layout no map was measured for");
            }
        }
        var (sec, off) = Map.Locate(space, a);
        return cur.Sections[sec][off];
    }
}
