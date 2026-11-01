using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NesEmulator.Debugging;

[Flags]
public enum AccessKind : byte { None = 0, Exec = 1, Read = 2, Write = 4, ReadWrite = Read | Write }

/// <summary>Who made a memory access: the CPU, or one of the consoles' DMA units.</summary>
public enum AccessSource : byte
{
    Cpu = 0, Dma = 1, Hdma = 2, OamDma = 3, VramDma = 4,
    /// <summary>The machine's other CPU reaching this one's memory: the Genesis's Z80 through its bank window (seen by the 68000's watches), the 68000 through its
    /// window on the Z80's RAM (seen by the Z80's).</summary>
    OtherCpu = 5,
}

/// <summary>A canonical location: every mirror of a byte has the same key (the RAM byte at NES $0010 is the one at $0810; SNES $80:0010 is
/// $7E:0010; Game Boy $E010 is $C010).</summary>
public readonly struct SpaceKey : IEquatable<SpaceKey>
{
    public readonly int Space;
    public readonly uint Offset;
    public SpaceKey(int space, uint offset) { Space = space; Offset = offset; }
    public bool Equals(SpaceKey o) => Space == o.Space && Offset == o.Offset;
    public override bool Equals(object? obj) => obj is SpaceKey k && Equals(k);
    public override int GetHashCode() => Space * 397 ^ (int)Offset;
    public override string ToString() => $"{Space}:{Offset:X}";
}

/// <summary>One canonical address space of a CPU ("cpu" is always id 0: the CPU's own addresses, mirrors folded).</summary>
public sealed class AddressSpace
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public uint Size { get; init; }
    public string Description { get; init; } = "";
    public string[] Aliases { get; init; } = Array.Empty<string>();
    public bool Is(string name) => Name.Equals(name, StringComparison.OrdinalIgnoreCase) || Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A resolved range of a breakpoint, in one space (inclusive).</summary>
public readonly record struct SpaceRange(int Space, uint Start, uint End)
{
    public bool Contains(SpaceKey k) => k.Space == Space && k.Offset >= Start && k.Offset <= End;
}

/// <summary>When a matching hit counts as a break: on the Nth hit and after (AtLeast, the default with N=1), only the Nth (Exactly), every Nth (Every).</summary>
public enum HitMode { AtLeast, Exactly, Every }

/// <summary>A breakpoint, watchpoint or tracepoint.</summary>
public sealed class Breakpoint
{
    public int Id { get; internal set; }
    public string CpuId { get; internal set; } = "main";
    public AccessKind Kind { get; internal set; }
    /// <summary>What was asked for ("player_hp", "$7E0010-$7E001F", "vram:$1000", "prg:$1234").</summary>
    public string Spec { get; internal set; } = "";
    public IReadOnlyList<SpaceRange> Ranges { get; internal set; } = Array.Empty<SpaceRange>();
    /// <summary>A human description of the resolved ranges ("wram:$0010-$001F").</summary>
    public string Resolved { get; internal set; } = "";
    public string? Condition { get; internal set; }
    internal DebugExpr? Cond;
    public HitMode HitMode { get; internal set; } = HitMode.AtLeast;
    public long HitTarget { get; internal set; } = 1;
    /// <summary>Accesses that matched (address, kind and condition).</summary>
    public long Hits { get; internal set; }
    /// <summary>Log and keep running (no break).</summary>
    public bool Tracepoint { get; internal set; }
    /// <summary>A tracepoint's message: text with {expressions} in braces ("hp={[player_hp]} x={X}").</summary>
    public string? LogFormat { get; internal set; }
    internal List<(string text, DebugExpr? expr)>? LogParts;
    public bool Enabled { get; internal set; } = true;
    /// <summary>A run-to target: removed once it breaks.</summary>
    public bool Temporary { get; internal set; }

    public bool Matches(SpaceKey k)
    {
        foreach (var r in Ranges) if (r.Contains(k)) return true;
        return false;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append('#').Append(Id).Append(' ').Append(Tracepoint ? "trace" : "break").Append(' ');
        sb.Append(KindText(Kind)).Append(' ').Append(CpuId == "main" ? "" : CpuId + ":").Append(Spec);
        if (Resolved.Length > 0 && Resolved != Spec) sb.Append(" (").Append(Resolved).Append(')');
        if (Condition != null) sb.Append(" if ").Append(Condition);
        if (!(HitMode == HitMode.AtLeast && HitTarget == 1)) sb.Append(" hits").Append(HitMode switch { HitMode.Exactly => "==", HitMode.Every => "%", _ => ">=" }).Append(HitTarget);
        sb.Append(" [").Append(Hits).Append(" hit").Append(Hits == 1 ? "" : "s").Append(']');
        if (!Enabled) sb.Append(" (disabled)");
        return sb.ToString();
    }

    public static string KindText(AccessKind k) => k switch
    {
        AccessKind.Exec => "exec", AccessKind.Read => "read", AccessKind.Write => "write", AccessKind.ReadWrite => "access",
        _ => k.ToString().ToLowerInvariant(),
    };
}

/// <summary>Why the emulation stopped, and where.</summary>
public sealed class BreakInfo
{
    /// <summary>breakpoint, watch, step, step-over, step-out, scanline, frame, run-to, pause.</summary>
    public string Reason { get; init; } = "";
    public string CpuId { get; init; } = "main";
    /// <summary>The address of the next instruction (the one the CPU is about to run).</summary>
    public uint Pc { get; internal set; }
    public string? PcSymbol { get; internal set; }
    public int? BreakpointId { get; init; }
    public AccessKind Kind { get; init; }
    /// <summary>For a watch: the CPU address accessed, the value, who accessed it, and the instruction that did (its PC).</summary>
    public uint Address { get; init; }
    public byte Value { get; init; }
    public AccessSource Source { get; init; }
    public int Channel { get; init; } = -1;
    public uint AccessPc { get; init; }
    public long Frame { get; internal set; }
    public int Scanline { get; internal set; }
    public int Dot { get; internal set; }
    public long Cycle { get; internal set; }
    public string Text { get; internal set; } = "";
    public override string ToString() => Text;
}

/// <summary>One entry of a call stack: a call instruction, or an interrupt.</summary>
public sealed class CallFrame
{
    /// <summary>JSR, JSL, BRK, COP, CALL, RST, TCALL, PCALL, NMI, IRQ, INT40 (a Game Boy interrupt by vector), ...</summary>
    public string Kind { get; init; } = "";
    public uint CallerPc { get; init; }
    public uint Target { get; internal set; }
    public uint ReturnPc { get; init; }
    /// <summary>The stack pointer right after the call pushed its return address: the frame has returned once the stack rises above it.</summary>
    public uint SpAfter { get; internal set; }
    public long Frame { get; init; }
    internal bool TracedFunction;
    public string? TargetSymbol { get; internal set; }
    public string? CallerSymbol { get; internal set; }
    public override string ToString() => $"{Kind} {CallerSymbol ?? "$" + CallerPc.ToString("X")} -> {TargetSymbol ?? "$" + Target.ToString("X")} (returns to ${ReturnPc:X})";
}

/// <summary>A breakpoint match that broke or was logged.</summary>
public sealed class HitRecord
{
    public int BreakpointId { get; init; }
    public string CpuId { get; init; } = "main";
    public AccessKind Kind { get; init; }
    public uint Address { get; init; }
    public byte Value { get; init; }
    public AccessSource Source { get; init; }
    public int Channel { get; init; } = -1;
    public uint Pc { get; init; }
    public long Frame { get; init; }
    public int Scanline { get; init; }
    public long Cycle { get; init; }
    public long HitNumber { get; init; }
    public bool Logged { get; init; }
    public string Text { get; init; } = "";
    public override string ToString() => Text;
}

/// <summary>Sparse per-byte flags over a space (4 KB pages allocated where a breakpoint is).</summary>
internal sealed class SpaceFlags
{
    private readonly byte[]?[] pages;
    public SpaceFlags(uint size) { pages = new byte[]?[Math.Max(1, (int)((size + 0xFFFu) >> 12))]; }
    public byte Get(uint offset)
    {
        uint p = offset >> 12;
        if (p >= (uint)pages.Length) return 0;
        var page = pages[p];
        return page == null ? (byte)0 : page[offset & 0xFFF];
    }
    public void Set(uint start, uint end, byte flags)
    {
        for (ulong a = start; a <= end; a++)
        {
            uint p = (uint)(a >> 12);
            if (p >= (uint)pages.Length) break;
            var page = pages[p] ??= new byte[0x1000];
            page[a & 0xFFF] |= flags;
        }
    }
}
