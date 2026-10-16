using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NesEmulator.Corruption;

// The stockpile: a file of corruptions, for every console, that the desktop app farms and the FL plugin plays.
// This file is the model only (no UI, no emulator dependency); StockpileJson reads and writes it. Design: docs/projects/round5-corr-design.md.

/// <summary>One write of a corruption: a byte put into a memory domain of the machine.</summary>
/// <param name="Domain">Domain id (<see cref="IMemoryDomain.Id"/>): ARAM, DSP, WRAM, VRAM, ... or the NES's legacy PRG, PRGRAM, CHR, RAM.</param>
/// <param name="Address">Byte address inside the domain.</param>
/// <param name="Value">The byte written.</param>
/// <param name="Was">The byte that was there when the corruption was made (-1: not recorded).</param>
/// <param name="At">Frames after the anchor at which the write lands (0: at the anchor).</param>
public readonly record struct CorruptionWrite(string Domain, int Address, byte Value, int Was = -1, int At = 0);

/// <summary>What kind of anchor places a corruption in a machine (see <see cref="Anchor"/>).</summary>
public enum AnchorKind { None, State, Boot, Ref }

/// <summary>
/// Where the machine is when a corruption's writes land.
/// <list type="bullet">
/// <item><b>State</b>: a saved machine state (<c>IConsoleSession.SaveState</c>, or the NES's JSON). Start loads it.</item>
/// <item><b>Boot</b>: a recipe from power-on: the ROM, <see cref="Frames"/> frames with the recorded pad (<see cref="Inputs"/>), optionally the save RAM the game started with. Start replays it.</item>
/// <item><b>None</b>: whatever is running now.</item>
/// <item><b>Ref</b>: one of the stockpile's shared <see cref="Stockpile.Bases"/>.</item>
/// </list>
/// An anchor may carry an <see cref="Alt"/> anchor that Start tries (with a message) when this one is refused.
/// </summary>
public sealed class Anchor
{
    public AnchorKind Kind { get; set; } = AnchorKind.None;

    // ---- State ----
    /// <summary>The console key the state belongs to (<c>Consoles.Key</c>: nes snes gb gbc sms gg md).</summary>
    public string Console { get; set; } = "";
    /// <summary>The layout of the state's bytes: "nes-json" (the NES's JSON text as UTF-8), or the session's own (<c>gb-1</c>, ...); informational beyond "nes-json"; the state file itself carries and checks its units.</summary>
    public string StateFormat { get; set; } = "";
    public byte[] StateData { get; set; } = Array.Empty<byte>();
    /// <summary>The game frame the state was taken at (-1: not recorded).</summary>
    public int Frame { get; set; } = -1;

    // ---- Boot ----
    /// <summary>Frames run from power-on before the writes land.</summary>
    public int Frames { get; set; }
    /// <summary>Player 1's pad per frame, <see cref="InputScript"/> text ("60:Start,90:Right+A,120:").</summary>
    public string Inputs { get; set; } = "";
    /// <summary>Player 2's pad per frame (empty: never pressed).</summary>
    public string Inputs2 { get; set; } = "";
    /// <summary>The cartridge save RAM the game started with (null: none, the game started with empty save RAM).</summary>
    public byte[]? Battery { get; set; }

    // ---- Ref ----
    public string BaseId { get; set; } = "";

    /// <summary>The anchor to try when this one is refused (a state a newer core cannot load, say).</summary>
    public Anchor? Alt { get; set; }

    public static Anchor NoneAnchor() => new() { Kind = AnchorKind.None };
}

/// <summary>A shared anchor many entries point to (<see cref="AnchorKind.Ref"/>).</summary>
public sealed class StockpileBase
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public Anchor Anchor { get; set; } = new();
}

/// <summary>The game a corruption was made on. Identity is the SHA-1 of the ROM file's bytes as the console session sees them; a path is never stored.</summary>
public sealed class GameRef
{
    /// <summary><c>Consoles.Key</c>: nes snes gb gbc sms gg md.</summary>
    public string Console { get; set; } = "";
    public string Sha1 { get; set; } = "";
    /// <summary>SHA-256 as the NES's savestates record it (<c>romHash</c>); only legacy NES entries carry it when no SHA-1 is known.</summary>
    public string Sha256 { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>The file name only.</summary>
    public string File { get; set; } = "";

    public bool HasIdentity => Sha1.Length > 0 || Sha256.Length > 0;
}

/// <summary>The cores and parts the corruption was made with. A replay compares them with the machine it builds and says what differs.</summary>
public sealed class MachineSpec
{
    public string Cpu { get; set; } = "";
    public string Ppu { get; set; } = "";
    public string Apu { get; set; } = "";
    /// <summary>Anything else that is part of the machine: the coprocessor and its firmware, say.</summary>
    public string Note { get; set; } = "";
}

/// <summary>How a corruption was farmed (informational: the writes are what is replayed).</summary>
public sealed class BlastSource
{
    public string Blast { get; set; } = "";
    public int Intensity { get; set; }
    public long Seed { get; set; }
    public List<string> Domains { get; set; } = new();
    /// <summary>"true" (each domain's real size) or "legacy" (the NES farm's probed sizes): the model the writes' addresses were drawn from.</summary>
    public string SizeModel { get; set; } = "";
}

/// <summary>One corruption: a name and description for the user, the game it fits, where its machine is, and the writes.</summary>
public sealed class StockpileEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public List<string> Tags { get; set; } = new();
    public GameRef Game { get; set; } = new();
    public MachineSpec Machine { get; set; } = new();
    public Anchor Anchor { get; set; } = new();
    public List<CorruptionWrite> Writes { get; set; } = new();
    public BlastSource? Source { get; set; }
    /// <summary>Set by the reader when the entry came from an older file in a form that lost something ("no-state": a Lite export that carried no machine state).</summary>
    public string Legacy { get; set; } = "";

    /// <summary>A one-line description of the writes: "16 writes: ARAM x12, DSP x4".</summary>
    public string Summary()
    {
        if (Writes.Count == 0) return "no writes";
        var groups = Writes.GroupBy(w => w.Domain).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(6)
            .Select(g => $"{g.Key} x{g.Count()}");
        return $"{Writes.Count} write{(Writes.Count == 1 ? "" : "s")}: " + string.Join(", ", groups);
    }

    /// <summary>A short hex hash of what makes this corruption this corruption (game, anchor and writes; not the name or description). A project that refers to an entry keeps it to notice a changed stockpile.</summary>
    public string ContentHash()
    {
        var sb = new StringBuilder();
        sb.Append(Game.Console).Append('|').Append(Game.Sha1).Append('|').Append(Game.Sha256).Append('|');
        AppendAnchor(sb, Anchor);
        foreach (var w in Writes) sb.Append(w.Domain).Append(':').Append(w.Address).Append('=').Append(w.Value).Append('@').Append(w.At).Append(';');
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant()[..16];
    }

    private static void AppendAnchor(StringBuilder sb, Anchor a)
    {
        sb.Append((int)a.Kind).Append('/').Append(a.Frames).Append('/').Append(a.Inputs).Append('/').Append(a.Inputs2).Append('/').Append(a.BaseId).Append('/').Append(a.StateFormat).Append('/');
        if (a.StateData.Length > 0) sb.Append(Convert.ToHexString(SHA1.HashData(a.StateData)));
        if (a.Battery is { Length: > 0 }) sb.Append('/').Append(Convert.ToHexString(SHA1.HashData(a.Battery)));
        sb.Append('|');
        if (a.Alt != null) { sb.Append("alt:"); AppendAnchor(sb, a.Alt); }
    }
}

/// <summary>A stockpile file in memory.</summary>
public sealed class Stockpile
{
    public const string FormatId = "brokennes-stockpile";
    /// <summary>The newest layout this build reads and writes. Bumped only for a change an older reader would misread; added properties do not bump it.</summary>
    public const int CurrentVersion = 2;
    public const string FileSuffix = ".stockpile.json";

    public string Name { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    /// <summary>Who wrote it (informational).</summary>
    public string Writer { get; set; } = "";
    public int Version { get; set; } = CurrentVersion;
    public List<StockpileEntry> Entries { get; } = new();
    public List<StockpileBase> Bases { get; } = new();

    /// <summary>The anchor an entry really starts from: its own, or the shared base it points to. Null when the base is missing.</summary>
    public Anchor? Resolve(Anchor anchor)
    {
        if (anchor.Kind != AnchorKind.Ref) return anchor;
        var b = Bases.FirstOrDefault(x => string.Equals(x.Id, anchor.BaseId, StringComparison.OrdinalIgnoreCase));
        return b?.Anchor;
    }

    public StockpileEntry? Find(string idOrName) =>
        Entries.FirstOrDefault(e => string.Equals(e.Id, idOrName, StringComparison.OrdinalIgnoreCase))
        ?? Entries.FirstOrDefault(e => string.Equals(e.Name, idOrName, StringComparison.OrdinalIgnoreCase));
}
