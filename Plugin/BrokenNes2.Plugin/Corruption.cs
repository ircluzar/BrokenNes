// Corruption in the plugin: open a stockpile (a file the BrokenNes desktop app wrote), pick a corruption, start it on the loaded game, then play its instruments with Instrument Runaway.
// This file is the plugin's side of the data: what the window shows (CorruptionPanel, CorrRow) and the project section that keeps a started corruption through a saved FL project (CorruptionSection,
// tag "CRP1"). The library is NesEmulator.Corruption (the stockpile file, the memory domains, the anchors); the flow is in Emulator.Corruption.cs and the window in Bn2Editor.cs.
//
// A section is   tag u32 | length i32 | payload   appended after the game-state section, so a build that does not know it skips it. The payload is   CRC-32 u32 (of the JSON)   then UTF-8 JSON in the stockpile file's own format
// (one entry, "project history": its anchor is the replay recipe of the game - the pads it was handed, the corruption writes made to it - when the console cannot keep a state) plus a "project" object:
//   { "v": 1, "stockpilePath": "...", "entryId": "...", "entryName": "...", "entryDescription": "...", "entrySummary": "...", "contentHash": "...", "frozen": false, "startedAtFrame": 123, "note": "..." }
// A console that keeps its state (every console now) needs no history: the game-state section already holds the corrupted machine; this section then only says where it came from. The history is written anyway
// when the project could not hold the machine (the state was too big or refused): the game is then rebuilt by replaying it, as a console without states would be.
using System.Text;
using System.Text.Json;
using NesEmulator.Corruption;

namespace BrokenNes2;

/// <summary>How an entry stands against the game that is loaded.</summary>
internal enum Fit { Fits, OtherGame, OtherConsole, NoGame, CannotHere }

/// <summary>One line of the window's list: an entry, whether it can be started now, why not, and what starting it costs.</summary>
internal sealed class CorrRow
{
    public required StockpileEntry Entry { get; init; }
    public Fit Fit { get; init; }
    /// <summary>Why it cannot be started now (empty: it can).</summary>
    public string Why { get; init; } = "";
    /// <summary>What starting it does, short: "replays 4,210 frames (about 8 s)", "loads its saved machine", "applies to the running game".</summary>
    public string Cost { get; init; } = "";
    public bool CanStart => Why.Length == 0;
}

/// <summary>A stockpile file as the window shows it.</summary>
internal sealed record StockpileView(Stockpile? File, string Path, IReadOnlyList<string> Problems, bool Legacy);

/// <summary>What the window needs between paints: the open stockpile, the selection, the start in progress and the last outcome. Fields that a worker thread sets are volatile.</summary>
internal sealed class CorruptionPanel
{
    private volatile StockpileView? view;
    public StockpileView? View { get => view; set => view = value; }
    public volatile bool Reading;
    public volatile bool Busy;
    public volatile bool CancelRequested;
    /// <summary>Progress of a start in progress ("replaying 2,400 / 4,210 frames"); empty when idle.</summary>
    public volatile string Status = "";
    /// <summary>What the last start (or open) said, for the window and for the note over the picture; <see cref="ResultGood"/> colours it.</summary>
    public volatile string Result = "";
    public volatile bool ResultGood;
    /// <summary>Counts up whenever Result changes (the editor repaints on it).</summary>
    public int ResultStamp;
    public long ResultTick;
    public int Selected = -1;
    public bool Freeze;
    /// <summary>Something the project asked for that could not be done (its corrupted game could not be rebuilt): shown over the picture for as long as this game runs, not for a few seconds.</summary>
    public volatile string? Problem;

    // the corruption the running game was started from (null: none was started on this game)
    public volatile AppliedCorruption? Applied;

    /// <summary>A result that stays on screen (see <see cref="Problem"/>).</summary>
    public void SetProblem(string text) { Problem = text; SetResult(text, false); }

    public void SetResult(string text, bool good)
    {
        Result = text; ResultGood = good; ResultTick = Environment.TickCount64;
        Interlocked.Increment(ref ResultStamp);
    }
}

/// <summary>The corruption a game was started from: what a project keeps to say so.</summary>
internal sealed record AppliedCorruption(string StockpilePath, string EntryId, string EntryName, string Description, string Summary, string ContentHash, long StartedAtFrame);

/// <summary>The corruption section of a project (tag "CRP1").</summary>
internal sealed class CorruptionSection
{
    public const uint Tag = 0x31505243;       // "CRP1"
    public const int MaxStored = 8 << 20;
    public const int MaxRead = 12 << 20;

    public AppliedCorruption Applied { get; private init; } = new("", "", "", "", "", "", 0);
    /// <summary>Why the section could not be used ("damaged", "cut short"); null when it is whole. A project is never silently loaded without the corruption it says it has: the editor shows this.</summary>
    public string? Problem { get; private init; }
    /// <summary>A section that could not be read: it has no content, only the reason.</summary>
    public static CorruptionSection Damaged(string problem) => new() { Problem = problem };
    public bool Frozen { get; private init; }
    /// <summary>The place of the game as a replay recipe (anchor of kind boot); null when the section holds none.</summary>
    public StockpileEntry? History { get; private init; }
    public string Note { get; private init; } = "";
    /// <summary>The payload as it was read, for a project saved again before its game is in place.</summary>
    public byte[] Raw { get; private init; } = [];

    /// <summary>The payload. <paramref name="history"/> is the game's place as a replay recipe, or null (with <paramref name="note"/> saying why none is kept, when it should have been). Null when it does not fit.</summary>
    public static byte[]? Build(AppliedCorruption applied, bool frozen, StockpileEntry? history, string note)
    {
        var file = new Stockpile { Name = "project", Writer = "BrokenNes2 plugin" };
        if (history != null) file.Entries.Add(history);
        string json = StockpileJson.Write(file, indented: false);
        // add the "project" object by writing the file again around the stockpile's own text: the stockpile object ends with '}'
        var sb = new StringBuilder(json.Length + 512);
        sb.Append(json, 0, json.Length - 1);
        sb.Append(",\"project\":{");
        sb.Append("\"v\":1,");
        AppendString(sb, "stockpilePath", applied.StockpilePath); sb.Append(',');
        AppendString(sb, "entryId", applied.EntryId); sb.Append(',');
        AppendString(sb, "entryName", applied.EntryName); sb.Append(',');
        AppendString(sb, "entryDescription", applied.Description); sb.Append(',');
        AppendString(sb, "entrySummary", applied.Summary); sb.Append(',');
        AppendString(sb, "contentHash", applied.ContentHash); sb.Append(',');
        sb.Append("\"frozen\":").Append(frozen ? "true" : "false").Append(',');
        sb.Append("\"startedAtFrame\":").Append(applied.StartedAtFrame).Append(',');
        AppendString(sb, "note", note);
        sb.Append("}}");
        var json8 = Encoding.UTF8.GetBytes(sb.ToString());
        if (json8.Length + 4 > MaxStored) return null;
        var bytes = new byte[4 + json8.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, GameStateSection.Crc32(json8));
        json8.CopyTo(bytes, 4);
        return bytes;
    }

    private static void AppendString(StringBuilder sb, string name, string value)
    {
        sb.Append('"').Append(name).Append("\":\"").Append(JsonEncodedText.Encode(value).ToString()).Append('"');
    }

    /// <summary>Reads and checks a payload. Nothing in it is trusted: a section that is not what it says comes back as <see cref="Damaged"/> (and the reason in <paramref name="problem"/>), never as a quiet nothing.</summary>
    public static CorruptionSection? Read(byte[] p, out string? problem)
    {
        problem = null;
        if (p.Length < 20 || p.Length > MaxRead) { problem = "damaged"; return Damaged(problem); }
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p) != GameStateSection.Crc32(p.AsSpan(4))) { problem = "damaged"; return Damaged(problem); }
        var json = p[4..];
        try
        {
            var read = StockpileJson.Read(json);
            if (read.Stockpile == null) { problem = read.Refusals.Count > 0 ? read.Refusals[0] : "damaged"; return Damaged(problem); }
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (!doc.RootElement.TryGetProperty("project", out var pr) || pr.ValueKind != JsonValueKind.Object) { problem = "damaged"; return Damaged(problem); }
            string S(string n) => pr.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            long At() => pr.TryGetProperty("startedAtFrame", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) && l >= 0 ? l : 0;
            bool frozen = pr.TryGetProperty("frozen", out var f) && f.ValueKind == JsonValueKind.True;
            var history = read.Stockpile.Entries.FirstOrDefault(e => e.Anchor.Kind == AnchorKind.Boot);
            return new CorruptionSection
            {
                Applied = new AppliedCorruption(S("stockpilePath"), S("entryId"), S("entryName"), S("entryDescription"), S("entrySummary"), S("contentHash"), At()),
                Frozen = frozen, History = history, Note = S("note"), Raw = p,
            };
        }
        catch (JsonException) { problem = "damaged"; return Damaged(problem); }
    }
}
