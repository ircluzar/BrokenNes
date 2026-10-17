// Round 6 (presets): the preset file, `*.bn2preset`. A pure library: no UI, no plugin, no clock, no file system. The same data always gives the same bytes, and anything that is not a good preset file is refused with a reason.
//
// A preset is ONE instrument ripped from a game (clean or corrupted), with everything needed to play it again without the game: the instrument as it is now, the clean one, the history of corruptions with the seeds that made them (so the corruption can be replayed),
// the facts a preset engine needs besides the instrument (Context), and a reserved block for the learned note behaviour the Runaway work is deciding. NEVER the ROM: the source game is a name and a SHA-1 for reference only.
//
//   offset  size  meaning
//   0       4     "BN2P"
//   4       2     format version (u16, 1)
//   6       2     flags (u16, 0: any other value is a feature this version does not know, and the file is refused)
//   8       4     header length N (u32)
//   12      N     the header: UTF-8 JSON (name, tags, notes, console, kind, index, source game and SHA-1, created, counts)
//   12+N    2     blob count (u16)
//           each  4-byte tag, u32 length, the bytes:  INS1 the instrument as it is now (InstrumentCodec) | CLN1 the clean instrument | HIS1 the history (InstrumentHistory.Serialize) |
//                 CTX1 the context (PresetContext) | NOTE the note behaviour (u16 version, then data; version 0 = empty, written by this version)
//   end     4     CRC-32 (zip polynomial) of everything before it
//
// Limits (a damaged or hostile file must not make the reader allocate or loop): the file at most 4 MiB, the header 64 KiB, a blob 2 MiB, 16 blobs. Unknown blob tags are kept and written back as they were (a newer minor addition survives a rename).
using System.Text;
using System.Text.Json;
using BrokenNes2.Instruments;

namespace BrokenNes2.Presets;

/// <summary>Everything a preset holds. Plain data: <see cref="PresetFile.Write"/> and <see cref="PresetFile.Read"/> turn it into bytes and back.</summary>
public sealed class PresetData
{
    public string Name = "", Notes = "", SourceGame = "", SourceSha1 = "", InstrumentName = "";
    public string[] Tags = [];
    public ConsoleId Console;
    public InstrumentKind Kind;
    /// <summary>The instrument's number in the snapshot it was ripped from (the SNES sample slot, the Sega picker index, the Game Boy / NES channel).</summary>
    public int Index;
    /// <summary>Seconds since 1970-01-01 UTC the preset was made (given by the caller: the library never reads a clock).</summary>
    public long CreatedUnix;
    /// <summary>How many corruption steps led from the clean instrument to this one (information; the history is the truth).</summary>
    public int Steps;
    /// <summary>The instrument as it is now (<see cref="InstrumentCodec.Pack"/>).</summary>
    public byte[] Instrument = [];
    /// <summary>The instrument as the game had it.</summary>
    public byte[] Clean = [];
    /// <summary>The operations, seeds included, from the clean instrument to this one (<see cref="InstrumentHistory.Serialize"/>).</summary>
    public byte[] History = [];
    /// <summary>What an engine needs besides the instrument: see <see cref="PresetContext"/>.</summary>
    public byte[] Context = [];
    /// <summary>The reserved block for the learned note behaviour. Version 0 = empty (what this version writes). A later version keeps its own bytes here; this one preserves them and does not play them.</summary>
    public int NoteVersion;
    public byte[] NoteBehaviour = [];
    /// <summary>Blobs this version does not know (tag, bytes), written back as they were.</summary>
    public List<(uint Tag, byte[] Data)> Unknown = new();

    // derived by Read / Validate (not stored)
    public int FieldCount, BlobCount;

    public bool Corrupted => Steps > 0;
    public bool HasNoteBehaviour => NoteVersion != 0 || NoteBehaviour.Length > 0;

    public PresetData Copy() => new()
    {
        Name = Name, Notes = Notes, SourceGame = SourceGame, SourceSha1 = SourceSha1, InstrumentName = InstrumentName, Tags = (string[])Tags.Clone(), Console = Console, Kind = Kind, Index = Index, CreatedUnix = CreatedUnix, Steps = Steps,
        Instrument = Instrument, Clean = Clean, History = History, Context = Context, NoteVersion = NoteVersion, NoteBehaviour = NoteBehaviour, Unknown = new(Unknown), FieldCount = FieldCount, BlobCount = BlobCount,
    };
}

/// <summary>What a failed read says: the reason, in words fit for the user.</summary>
public readonly record struct PresetRead(PresetData? Data, string? Problem)
{
    public bool Ok => Data != null;
}

public static class PresetFile
{
    public const string Extension = ".bn2preset";
    public const int FormatVersion = 1;
    public const int MaxFileBytes = 4 << 20, MaxHeaderBytes = 64 << 10, MaxBlobBytes = 2 << 20, MaxBlobs = 16;
    public const int MaxName = 80, MaxNotes = 4000, MaxTags = 24, MaxTagLength = 32, MaxGame = 260, MaxSha1 = 40;

    private const uint Magic = 0x50324E42;                       // "BN2P"
    public const uint TagInstrument = 0x31534E49, TagClean = 0x314E4C43, TagHistory = 0x31534948, TagContext = 0x31585443, TagNote = 0x45544F4E;   // INS1, CLN1, HIS1, CTX1, NOTE

    public static readonly string[] ConsoleNames = ["NES", "GB", "SNES", "SMS", "MD"];      // the short names the Presets tab filters by (ConsoleId order)

    public static string ConsoleName(ConsoleId c) => (int)c < ConsoleNames.Length ? ConsoleNames[(int)c] : "?";

    public static bool TryConsole(string name, out ConsoleId console)
    {
        for (int i = 0; i < ConsoleNames.Length; i++) if (ConsoleNames[i] == name) { console = (ConsoleId)i; return true; }
        console = default;
        return false;
    }

    // ---- writing ----

    /// <summary>The bytes of a preset. Fails (with the reason) when a part is too big for the format or the instrument parts are not what the header says; never produces a file this code would refuse to read.</summary>
    public static byte[]? TryWrite(PresetData d, out string? problem)
    {
        problem = Check(d) ?? Validate(d);                           // (Validate fills the counts the header carries)
        if (problem != null) return null;
        var header = HeaderJson(d);
        if (header.Length > MaxHeaderBytes) { problem = "the header is too long"; return null; }
        var blobs = new List<(uint Tag, byte[] Data)>
        {
            (TagInstrument, d.Instrument), (TagClean, d.Clean), (TagHistory, d.History), (TagContext, d.Context),
            (TagNote, NoteBlob(d)),
        };
        foreach (var u in d.Unknown.OrderBy(x => x.Tag)) blobs.Add(u);
        foreach (var b in blobs) if (b.Data.Length > MaxBlobBytes) { problem = $"a part of the preset is {b.Data.Length:N0} bytes, over the limit of {MaxBlobBytes:N0}"; return null; }
        if (blobs.Count > MaxBlobs) { problem = "too many parts"; return null; }
        long total = 12 + header.Length + 2 + 4 + blobs.Sum(b => 8L + b.Data.Length);
        if (total > MaxFileBytes) { problem = $"the preset would be {total:N0} bytes, over the limit of {MaxFileBytes:N0}"; return null; }

        var o = new byte[total];
        int p = 0;
        void U16(int v) { o[p++] = (byte)v; o[p++] = (byte)(v >> 8); }
        void U32(uint v) { o[p++] = (byte)v; o[p++] = (byte)(v >> 8); o[p++] = (byte)(v >> 16); o[p++] = (byte)(v >> 24); }
        U32(Magic); U16(FormatVersion); U16(0); U32((uint)header.Length);
        header.CopyTo(o, p); p += header.Length;
        U16(blobs.Count);
        foreach (var b in blobs) { U32(b.Tag); U32((uint)b.Data.Length); b.Data.CopyTo(o, p); p += b.Data.Length; }
        U32(InstrumentCrc.Of(o.AsSpan(0, p)));
        return o;
    }

    private static byte[] NoteBlob(PresetData d)
    {
        var b = new byte[2 + d.NoteBehaviour.Length];
        b[0] = (byte)d.NoteVersion; b[1] = (byte)(d.NoteVersion >> 8);
        d.NoteBehaviour.CopyTo(b, 2);
        return b;
    }

    /// <summary>Whether the data can be written: limits on the texts, a console and kind that agree, a context that fits the console. (Deeper checks are <see cref="Validate"/>'s.)</summary>
    private static string? Check(PresetData d)
    {
        if (d.Name.Length == 0) return "the preset has no name";
        if (d.Name.Length > MaxName) return $"the name is longer than {MaxName} characters";
        if (d.Notes.Length > MaxNotes) return $"the notes are longer than {MaxNotes} characters";
        if (d.SourceGame.Length > MaxGame) return "the source game's name is too long";
        if (d.SourceSha1.Length > MaxSha1 || d.SourceSha1.Any(ch => !Uri.IsHexDigit(ch))) return "the source SHA-1 is not hexadecimal";
        if (d.Tags.Length > MaxTags || d.Tags.Any(t => t.Length == 0 || t.Length > MaxTagLength)) return "too many tags, or a tag that is empty or too long";
        if (HasControl(d.Name) || HasControl(d.SourceGame) || HasControl(d.InstrumentName) || d.Tags.Any(HasControl)) return "a text holds a control character";
        if ((int)d.Console >= ConsoleNames.Length) return "unknown console";
        if (d.NoteVersion is < 0 or > 0xFFFF) return "bad note-behaviour version";
        return null;
    }

    private static bool HasControl(string s) { foreach (char c in s) if (c < 32 || c == 127) return true; return false; }

    private static byte[] HeaderJson(PresetData d)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("format", FormatVersion);
            w.WriteString("app", "BrokenNes2");
            w.WriteString("name", d.Name);
            w.WriteStartArray("tags");
            foreach (var t in d.Tags) w.WriteStringValue(t);
            w.WriteEndArray();
            w.WriteString("notes", d.Notes);
            w.WriteString("console", ConsoleName(d.Console));
            w.WriteString("kind", d.Kind.ToString());
            w.WriteNumber("kindId", (int)d.Kind);
            w.WriteNumber("index", d.Index);
            w.WriteString("instrument", d.InstrumentName);
            w.WriteString("game", d.SourceGame);
            w.WriteString("sha1", d.SourceSha1.ToLowerInvariant());
            w.WriteNumber("created", d.CreatedUnix);
            w.WriteNumber("fields", d.FieldCount);
            w.WriteNumber("blobs", d.BlobCount);
            w.WriteNumber("steps", d.Steps);
            w.WriteNumber("noteBehaviour", d.NoteVersion);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    // ---- reading ----

    /// <summary>Reads the container and the header and checks the checksum, without unpacking the instruments (what a library scan needs). Use <see cref="Read"/> to get a preset that is safe to play.</summary>
    public static PresetRead ReadShallow(byte[] b)
    {
        if (b.Length > MaxFileBytes) return Fail($"too big ({b.Length:N0} bytes; a preset is at most {MaxFileBytes:N0})");
        if (b.Length < 4 || U32(b, 0) != Magic) return Fail(b.Length == 0 ? "empty" : "not a BrokenNes2 preset file");
        if (b.Length < 12 + 2 + 4) return Fail("cut short");
        int version = b[4] | b[5] << 8;
        if (version > FormatVersion) return Fail($"made by a newer BrokenNes2 (preset format {version}, this one reads up to {FormatVersion})");
        if (version < FormatVersion) return Fail($"an unknown preset format ({version})");
        if ((b[6] | b[7] << 8) != 0) return Fail("it uses a feature this version of BrokenNes2 does not know");
        uint crc = U32(b, b.Length - 4);
        if (crc != InstrumentCrc.Of(b.AsSpan(0, b.Length - 4)) && !PresetMutants.On(PresetMutants.CrcNotChecked)) return Fail("damaged (the checksum does not match)");
        long hl = U32(b, 8);
        if (hl > MaxHeaderBytes || 12 + hl + 2 + 4 > b.Length) return Fail("damaged (the header's length)");
        var d = new PresetData();
        string? hp = ParseHeader(b.AsSpan(12, (int)hl), d);
        if (hp != null) return Fail("damaged (" + hp + ")");
        int p = 12 + (int)hl, end = b.Length - 4;
        int count = b[p] | b[p + 1] << 8; p += 2;
        if (count > MaxBlobs) return Fail("damaged (too many parts)");
        var seen = new HashSet<uint>();
        for (int i = 0; i < count; i++)
        {
            if (p + 8 > end) return Fail("cut short");
            uint tag = U32(b, p); long len = U32(b, p + 4); p += 8;
            if (len > MaxBlobBytes || p + len > end) return Fail("damaged (a part's length)");
            if (!seen.Add(tag)) return Fail("damaged (a part appears twice)");
            var data = b.AsSpan(p, (int)len).ToArray();
            p += (int)len;
            switch (tag)
            {
                case TagInstrument: d.Instrument = data; break;
                case TagClean: d.Clean = data; break;
                case TagHistory: d.History = data; break;
                case TagContext: d.Context = data; break;
                case TagNote:
                    if (data.Length < 2) return Fail("damaged (the note-behaviour part)");
                    d.NoteVersion = data[0] | data[1] << 8;
                    d.NoteBehaviour = data.AsSpan(2).ToArray();
                    if (d.NoteVersion == 0 && d.NoteBehaviour.Length != 0) return Fail("damaged (an empty note-behaviour part holds data)");
                    break;
                default: d.Unknown.Add((tag, data)); break;
            }
        }
        if (p != end) return Fail("damaged (bytes after the last part)");
        if (d.Instrument.Length == 0 || d.Clean.Length == 0 || d.History.Length == 0 || d.Context.Length == 0 && d.Console != ConsoleId.Nes) return Fail("damaged (a part is missing)");
        return new PresetRead(d, null);
    }

    /// <summary>Reads a preset and checks everything: the container, the header, the instrument (and the clean one) against its codec and schema, the history against the instrument, the context against the console. A preset that comes out of here is safe to play.</summary>
    public static PresetRead Read(byte[] b)
    {
        var r = ReadShallow(b);
        if (r.Data == null) return r;
        string? problem = Validate(r.Data);
        return problem == null ? r : Fail(problem);
    }

    /// <summary>The deep checks, run on the data of a shallow read (or on data built in memory). Fills <see cref="PresetData.FieldCount"/> and <see cref="PresetData.BlobCount"/>. Null when good, else the reason.</summary>
    public static string? Validate(PresetData d)
    {
        var inst = InstrumentCodec.Unpack(d.Instrument, out string? p1);
        if (inst == null) return "its instrument is " + p1;
        var clean = InstrumentCodec.Unpack(d.Clean, out string? p2);
        if (clean == null) return "its clean instrument is " + p2;
        if (inst.Console != d.Console || inst.Kind != d.Kind || inst.Index != d.Index) return "damaged (the header does not describe the instrument)";
        if (clean.Console != inst.Console || clean.Kind != inst.Kind || clean.Index != inst.Index) return "damaged (the clean instrument is another instrument)";
        var h = InstrumentHistory.Deserialize(d.History, out string? p3);
        if (h == null) return "its history is " + p3;
        if (!h.Clean.SameValues(clean)) return "damaged (the history starts from another instrument than the clean one)";
        if (!h.Current.SameValues(inst) && !PresetMutants.On(PresetMutants.HistoryNotChecked)) return "damaged (the history does not lead to the instrument)";
        string? c = PresetContext.Check(d.Console, d.Context);
        if (c != null) return "its context is " + c;
        string? nb = PresetNote.Check(d);
        if (nb != null) return nb;
        d.FieldCount = inst.Fields.Length;
        d.BlobCount = inst.Blobs.Length;
        d.Steps = h.Operations.Take(h.Cursor).Count(o => o.Kind is not (OpKind.Lock or OpKind.Unlock));
        return null;
    }

    private static PresetRead Fail(string why) => new(null, why);

    private static uint U32(byte[] b, int p) => (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24);

    private static string? ParseHeader(ReadOnlySpan<byte> json, PresetData d)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 4, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "the header is not an object";
            if (!Int(root, "format", out int format) || format != FormatVersion) return "the header's format";
            if (!Str(root, "name", MaxName, out d.Name) || d.Name.Length == 0) return "the name";
            if (!Str(root, "notes", MaxNotes, out d.Notes)) return "the notes";
            if (!Str(root, "game", MaxGame, out d.SourceGame)) return "the source game";
            if (!Str(root, "sha1", MaxSha1, out d.SourceSha1) || d.SourceSha1.Any(ch => !Uri.IsHexDigit(ch))) return "the source SHA-1";
            if (!Str(root, "instrument", MaxName, out d.InstrumentName)) return "the instrument's name";
            if (!Str(root, "console", 8, out string console) || !TryConsole(console, out d.Console)) return "the console";
            if (!Int(root, "kindId", out int kind) || kind is < 0 or > 255) return "the instrument kind";
            d.Kind = (InstrumentKind)kind;
            if (!Int(root, "index", out d.Index)) return "the instrument number";
            if (!root.TryGetProperty("created", out var cr) || cr.ValueKind != JsonValueKind.Number || !cr.TryGetInt64(out d.CreatedUnix) || d.CreatedUnix < 0) return "the creation time";
            if (!Int(root, "fields", out d.FieldCount) || !Int(root, "blobs", out d.BlobCount) || !Int(root, "steps", out d.Steps)) return "the counts";
            if (!Int(root, "noteBehaviour", out d.NoteVersion) || d.NoteVersion is < 0 or > 0xFFFF) return "the note-behaviour version";
            if (!root.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() > MaxTags) return "the tags";
            var list = new List<string>();
            foreach (var t in tags.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.String) return "a tag";
                string s = t.GetString() ?? "";
                if (s.Length == 0 || s.Length > MaxTagLength) return "a tag";
                list.Add(s);
            }
            d.Tags = list.ToArray();
            return null;
        }
        catch (JsonException e) { return "the header is not JSON: " + e.Message; }
    }

    private static bool Int(JsonElement o, string name, out int v)
    {
        v = 0;
        return o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v);
    }

    private static bool Str(JsonElement o, string name, int max, out string v)
    {
        v = "";
        if (!o.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String) return false;
        string s = e.GetString() ?? "";
        if (s.Length > max) return false;
        v = s;
        return true;
    }

    /// <summary>The checksum a file ends with (also the cheapest "is this the same preset" test).</summary>
    public static uint Checksum(byte[] file) => file.Length >= 4 ? U32(file, file.Length - 4) : 0;
}

/// <summary>Turns what the Instruments tab hands over (the packed instrument, the clean one, the history, the context) into a preset.</summary>
public static class PresetMaker
{
    /// <summary>The words an instrument kind is tagged with.</summary>
    public static string KindTag(InstrumentKind k) => k switch
    {
        InstrumentKind.SnesSample => "sample", InstrumentKind.OpnPatch => "fm patch", InstrumentKind.OpnDac => "dac", InstrumentKind.PsgTones => "psg tones", InstrumentKind.PsgNoise => "psg noise",
        InstrumentKind.OpllUser => "fm user", InstrumentKind.OpllRom => "fm rom", InstrumentKind.GbPulse1 or InstrumentKind.GbPulse2 or InstrumentKind.NesPulse1 or InstrumentKind.NesPulse2 => "pulse",
        InstrumentKind.GbWave => "wave", InstrumentKind.GbNoise or InstrumentKind.NesNoise => "noise", InstrumentKind.NesTriangle => "triangle", InstrumentKind.NesDmc => "dmc", _ => "instrument",
    };

    /// <summary>Null with the reason when the parts are not a good instrument; otherwise a preset (not yet written: <see cref="PresetFile.TryWrite"/> checks the rest). Tags default to the console, the kind and clean or corrupted.</summary>
    public static PresetData? Make(string name, string instrumentName, string sourceGame, string sourceSha1, string notes, long createdUnix, byte[] packedNow, byte[] packedClean, byte[] history, byte[] context, out string? problem, string[]? tags = null)
    {
        var inst = InstrumentCodec.Unpack(packedNow, out string? p1);
        if (inst == null) { problem = "the instrument is " + p1; return null; }
        var h = InstrumentHistory.Deserialize(history, out string? p2);
        if (h == null) { problem = "the history is " + p2; return null; }
        int steps = h.Operations.Take(h.Cursor).Count(o => o.Kind is not (OpKind.Lock or OpKind.Unlock));
        var d = new PresetData
        {
            Name = name.Trim(), InstrumentName = instrumentName, SourceGame = sourceGame, SourceSha1 = sourceSha1.ToLowerInvariant(), Notes = notes, CreatedUnix = createdUnix,
            Console = inst.Console, Kind = inst.Kind, Index = inst.Index, Steps = steps, Instrument = packedNow, Clean = packedClean, History = history, Context = context,
            Tags = tags ?? [PresetFile.ConsoleName(inst.Console).ToLowerInvariant(), KindTag(inst.Kind), steps > 0 ? "corrupted" : "clean"],
        };
        problem = null;
        return d;
    }
}

/// <summary>Test-only switches that inject a deliberate bug into the preset library, so a verifier can be shown able to fail. Zero (the default) is the real code; nothing sets it outside the self-test.</summary>
internal static class PresetMutants
{
    public static int Active;
    public const int CrcNotChecked = 1, HistoryNotChecked = 2, LibraryOverwrites = 3, SectionCrcNotChecked = 4, LoadsWrongBytes = 5, ImportSkipsValidation = 6, SnesEnvelopeDropped = 7, SnesTuningDropped = 8, GenesisPatchIndexOff = 9,
        GbDutyDropped = 10, ProjectKeepsNothing = 11, ExportWritesWrong = 12, SearchIgnoresTags = 13, RenameKeepsOldName = 14, DeleteKeepsFile = 15,
        NoteNotChecked = 16, NoteRecipesDropped = 17, VoiceIgnored = 18;
    public static bool On(int m) => Active == m;
}
