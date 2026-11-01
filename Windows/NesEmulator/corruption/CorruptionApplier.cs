using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Systems;

namespace NesEmulator.Corruption;

/// <summary>What applying a list of writes did, and why anything was left out (the caller shows <see cref="Problems"/>).</summary>
public sealed class ApplyReport
{
    public int Requested, Applied, Changed, UnknownDomain, Unavailable, OutOfRange, NotWritable;
    public List<string> Problems { get; } = new();
    public bool Complete => Applied == Requested;

    public string Describe() =>
        $"{Applied} of {Requested} writes applied, {Changed} changed a byte" +
        (Complete ? "" : $" ({UnknownDomain} to a memory this machine does not have, {Unavailable} to one that is not there now, {OutOfRange} past the end of their memory, {NotWritable} to a memory that cannot be written)");
}

/// <summary>The bytes a set of writes replaced, in order, so that they can be put back.</summary>
public sealed class WriteJournal
{
    internal readonly List<(IMemoryDomain Domain, int Address, byte Before)> Items = new();
    public int Count => Items.Count;
}

/// <summary>Applies corruption writes to a machine's domains, and removes them again where that is possible.</summary>
public static class CorruptionApplier
{
    /// <summary>
    /// Writes <paramref name="writes"/> in order (only those whose <see cref="CorruptionWrite.At"/> is <paramref name="at"/>: the caller calls again at the later frames). A write to a domain the machine lacks, to one that is
    /// not there now, past a domain's end or to one that cannot be written is skipped and counted with a reason; nothing is redirected to another memory. Must be called between frames.
    /// </summary>
    public static ApplyReport Apply(IReadOnlyList<IMemoryDomain> domains, IEnumerable<CorruptionWrite> writes, WriteJournal? journal = null, int at = 0)
    {
        var report = new ApplyReport();
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in writes)
        {
            if (w.At != at) continue;
            report.Requested++;
            var d = MachineDomains.Find(domains, w.Domain);
            if (d == null) { report.UnknownDomain++; if (missing.Add(w.Domain)) report.Problems.Add($"this machine has no memory called '{w.Domain}' (it has: {string.Join(", ", domains.Select(x => x.Id))})"); continue; }
            if (d.Unavailable != null) { report.Unavailable++; if (missing.Add(w.Domain)) report.Problems.Add($"'{d.Id}' is not available: {d.Unavailable}"); continue; }
            if (!d.Writable) { report.NotWritable++; if (missing.Add(w.Domain)) report.Problems.Add($"'{d.Id}' cannot be written"); continue; }
            if ((uint)w.Address >= (uint)d.Size) { report.OutOfRange++; if (missing.Add(w.Domain + "!")) report.Problems.Add($"'{d.Id}' is {d.Size:N0} bytes long: address {w.Address:N0} is past its end"); continue; }
            byte before = d.Peek(w.Address);
            journal?.Items.Add((d, w.Address, before));
            d.Poke(w.Address, w.Value);
            report.Applied++;
            if (d.Peek(w.Address) != before) report.Changed++;
        }
        return report;
    }

    /// <summary>
    /// Puts the journalled bytes back, last write first. Exact for a ROM (nothing else changes it). For memories the running game also writes, this restores the byte, not the machine: whatever the game
    /// did with the changed byte in the meantime stays done, and removing a corruption from a running game means starting again from its anchor.
    /// </summary>
    public static void Revert(WriteJournal journal)
    {
        for (int i = journal.Items.Count - 1; i >= 0; i--)
        {
            var (d, a, before) = journal.Items[i];
            d.Poke(a, before);
        }
        journal.Items.Clear();
    }
}

public enum BlastType { Random, Tilt, Nop, BitFlip, RandomTilt }

/// <summary>A small deterministic generator (SplitMix64): the same seed gives the same numbers on every .NET version, which <see cref="Random"/> does not promise.</summary>
public sealed class SeededRandom
{
    private ulong state;
    public SeededRandom(long seed) { state = unchecked((ulong)seed); }
    public ulong Next64()
    {
        unchecked
        {
            ulong z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
    /// <summary>0 to <paramref name="n"/> - 1.</summary>
    public int Next(int n) => n <= 1 ? 0 : (int)(Next64() % (ulong)n);
}

/// <summary>Makes corruption writes (farming): a seeded random choice of addresses in the chosen domains, with the final value and the byte that was there recorded.</summary>
public static class BlastGenerator
{
    public static BlastType Parse(string? name) => (name ?? "").Trim().ToUpperInvariant() switch
    {
        "TILT" => BlastType.Tilt, "NOP" => BlastType.Nop, "BITFLIP" => BlastType.BitFlip, "RANDOMTILT" => BlastType.RandomTilt, _ => BlastType.Random,
    };

    /// <summary>The byte a "NOP" blast writes on a console: the CPU's no-operation opcode (65816 and 6502 $EA; SM83 and Z80 $00; the 68000's $4E71 as $4E on even and $71 on odd addresses).</summary>
    public static byte NopByte(ConsoleKind console, int address) => console switch
    {
        ConsoleKind.Nes or ConsoleKind.Snes => 0xEA,
        ConsoleKind.Genesis => (address & 1) == 0 ? (byte)0x4E : (byte)0x71,
        _ => 0x00,
    };

    /// <summary>
    /// <paramref name="intensity"/> writes (1 to 65535), each to a domain picked uniformly from <paramref name="selected"/> (or, with <paramref name="weightBySize"/>, by size) at a uniformly random address of it. TILT adds or subtracts one
    /// from the byte that is there, BITFLIP flips one bit of it, NOP writes the console's no-op, RANDOM a random byte; RANDOMTILT is RANDOM or TILT by a coin. Unavailable and empty domains are never chosen. The same seed on the same
    /// machine gives the same writes; the values are final (computed from the live byte), so replaying them needs no randomness.
    /// </summary>
    public static List<CorruptionWrite> Generate(IReadOnlyList<IMemoryDomain> domains, IEnumerable<string> selected, int intensity, BlastType type, long seed, ConsoleKind console, bool weightBySize = false)
    {
        var chosen = selected.Select(id => MachineDomains.Find(domains, id)).Where(d => d is { Unavailable: null, Writable: true, Size: > 0 }).Select(d => d!).ToList();
        var result = new List<CorruptionWrite>();
        if (chosen.Count == 0) return result;
        intensity = Math.Clamp(intensity, 1, 65535);
        var rnd = new SeededRandom(seed);
        long total = chosen.Sum(d => (long)d.Size);
        for (int i = 0; i < intensity; i++)
        {
            IMemoryDomain d;
            if (weightBySize)
            {
                long pick = (long)(rnd.Next64() % (ulong)total);
                int k = 0;
                while (k < chosen.Count - 1 && pick >= chosen[k].Size) { pick -= chosen[k].Size; k++; }
                d = chosen[k];
            }
            else d = chosen[rnd.Next(chosen.Count)];
            int addr = rnd.Next(d.Size);
            byte was = d.Peek(addr);
            var mode = type == BlastType.RandomTilt ? (rnd.Next(2) == 0 ? BlastType.Random : BlastType.Tilt) : type;
            byte value = mode switch
            {
                BlastType.Tilt => (byte)(was + (rnd.Next(2) == 0 ? 1 : 255)),
                BlastType.Nop => NopByte(console, addr),
                BlastType.BitFlip => (byte)(was ^ (1 << rnd.Next(8))),
                _ => (byte)rnd.Next(256),
            };
            result.Add(new CorruptionWrite(d.Id, addr, value, was));
        }
        return result;
    }
}
