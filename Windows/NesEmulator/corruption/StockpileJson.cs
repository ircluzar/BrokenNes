using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace NesEmulator.Corruption;

/// <summary>What reading a stockpile file produced: the stockpile (null when the file as a whole was refused), what was dropped and why, and what was merely worth saying.</summary>
public sealed class StockpileReadResult
{
    public Stockpile? Stockpile { get; set; }
    /// <summary>Entries or the whole file refused, in words fit for the user (never silent: the caller shows them).</summary>
    public List<string> Refusals { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>The file was an older one (a top-level array, from the Glitch Harvester's export).</summary>
    public bool Legacy { get; set; }
    public bool Ok => Stockpile != null;
}

/// <summary>
/// Reads and writes stockpile files (JSON, UTF-8, version 2). Written with <see cref="Utf8JsonWriter"/> and read with <see cref="JsonDocument"/>: no reflection, so the Native AOT plugin, the browser build and the desktop
/// share one implementation. A reader ignores properties it does not know (the format grows by adding), refuses a newer <c>version</c> with a message, treats the file as hostile (size limits, bounded decompression) and
/// reads the older files (a top-level array: the Glitch Harvester's own export and Lite's, which spelled the machine state differently).
/// </summary>
public static class StockpileJson
{
    public const long MaxFileBytes = 64L * 1024 * 1024;
    public const int MaxEntries = 4096;
    public const int MaxWritesPerEntry = 1_000_000;
    public const int MaxStateBytes = 64 * 1024 * 1024;
    public const int MaxBatteryBytes = 1024 * 1024;
    public const int MaxInputChars = 1_000_000;

    // ===================================================================================================== writing

    public static string Write(Stockpile s, bool indented = true) => Encoding.UTF8.GetString(WriteBytes(s, indented));

    public static byte[] WriteBytes(Stockpile s, bool indented = true)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = indented }))
        {
            w.WriteStartObject();
            w.WriteString("format", Stockpile.FormatId);
            w.WriteNumber("version", Stockpile.CurrentVersion);
            w.WriteString("created", s.Created.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            if (s.Writer.Length > 0) w.WriteString("writer", s.Writer);
            if (s.Name.Length > 0) w.WriteString("name", s.Name);
            w.WriteStartArray("entries");
            foreach (var e in s.Entries) WriteEntry(w, e, indented);
            w.WriteEndArray();
            // only the shared bases something points to
            var used = new HashSet<string>(s.Entries.SelectMany(AnchorRefs), StringComparer.OrdinalIgnoreCase);
            var bases = s.Bases.Where(b => used.Contains(b.Id)).ToList();
            if (bases.Count > 0)
            {
                w.WriteStartArray("bases");
                foreach (var b in bases)
                {
                    w.WriteStartObject();
                    w.WriteString("id", b.Id);
                    w.WriteString("name", b.Name);
                    w.WritePropertyName("anchor"); WriteAnchor(w, b.Anchor);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static IEnumerable<string> AnchorRefs(StockpileEntry e)
    {
        for (var a = e.Anchor; a != null; a = a.Alt) if (a.Kind == AnchorKind.Ref) yield return a.BaseId;
    }

    private static void WriteEntry(Utf8JsonWriter w, StockpileEntry e, bool indented)
    {
        w.WriteStartObject();
        w.WriteString("id", e.Id);
        w.WriteString("name", e.Name);
        w.WriteString("description", e.Description);
        w.WriteString("created", e.Created.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        if (e.Tags.Count > 0) { w.WriteStartArray("tags"); foreach (var t in e.Tags) w.WriteStringValue(t); w.WriteEndArray(); }
        w.WriteStartObject("game");
        w.WriteString("console", e.Game.Console);
        w.WriteString("sha1", e.Game.Sha1);
        if (e.Game.Sha256.Length > 0) w.WriteString("sha256", e.Game.Sha256);
        w.WriteString("title", e.Game.Title);
        w.WriteString("file", e.Game.File);
        w.WriteEndObject();
        w.WriteStartObject("machine");
        w.WriteString("cpu", e.Machine.Cpu); w.WriteString("ppu", e.Machine.Ppu); w.WriteString("apu", e.Machine.Apu);
        if (e.Machine.Region.Length > 0) w.WriteString("region", e.Machine.Region);
        if (e.Machine.Model.Length > 0) w.WriteString("model", e.Machine.Model);
        if (e.Machine.Note.Length > 0) w.WriteString("note", e.Machine.Note);
        w.WriteEndObject();
        w.WritePropertyName("anchor"); WriteAnchor(w, e.Anchor);
        w.WriteStartArray("writes");
        foreach (var x in e.Writes) w.WriteRawValue((indented ? "\n        " : "") + RawWrite(x), skipInputValidation: true);   // one line per write
        w.WriteEndArray();
        w.WriteString("summary", e.Summary());
        if (e.Source != null)
        {
            w.WriteStartObject("source");
            w.WriteString("blast", e.Source.Blast);
            w.WriteNumber("intensity", e.Source.Intensity);
            w.WriteNumber("seed", e.Source.Seed);
            w.WriteStartArray("domains"); foreach (var d in e.Source.Domains) w.WriteStringValue(d); w.WriteEndArray();
            if (e.Source.SizeModel.Length > 0) w.WriteString("sizeModel", e.Source.SizeModel);
            w.WriteEndObject();
        }
        if (e.Legacy.Length > 0) w.WriteString("legacy", e.Legacy);
        w.WriteEndObject();
    }

    private static string RawWrite(CorruptionWrite x)
    {
        var sb = new StringBuilder(64);
        sb.Append("{\"domain\":\"").Append(JsonEncodedText.Encode(x.Domain).ToString()).Append('"')
          .Append(",\"address\":").Append(x.Address.ToString(CultureInfo.InvariantCulture))
          .Append(",\"value\":").Append(x.Value.ToString(CultureInfo.InvariantCulture));
        if (x.Was >= 0) sb.Append(",\"was\":").Append(x.Was.ToString(CultureInfo.InvariantCulture));
        if (x.At != 0) sb.Append(",\"at\":").Append(x.At.ToString(CultureInfo.InvariantCulture));
        return sb.Append('}').ToString();
    }

    private static void WriteAnchor(Utf8JsonWriter w, Anchor a)
    {
        w.WriteStartObject();
        w.WriteString("kind", a.Kind switch { AnchorKind.State => "state", AnchorKind.Boot => "boot", AnchorKind.Ref => "ref", _ => "none" });
        switch (a.Kind)
        {
            case AnchorKind.State:
                w.WriteString("console", a.Console);
                w.WriteString("stateFormat", a.StateFormat);
                if (a.Frame >= 0) w.WriteNumber("frame", a.Frame);
                w.WriteString("encoding", "deflate-base64");
                w.WriteString("data", DeflateBase64(a.StateData));
                break;
            case AnchorKind.Boot:
                w.WriteNumber("frames", a.Frames);
                w.WriteString("inputs", a.Inputs);
                if (a.Inputs2.Length > 0) w.WriteString("inputs2", a.Inputs2);
                if (a.Battery is { Length: > 0 }) { w.WriteString("batteryEncoding", "deflate-base64"); w.WriteString("battery", DeflateBase64(a.Battery)); }
                if (a.Events.Count > 0)
                {
                    w.WriteStartArray("events");
                    foreach (var ev in a.Events)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("frame", ev.Frame);
                        w.WriteStartArray("writes");
                        foreach (var x in ev.Writes) w.WriteRawValue(RawWrite(x), skipInputValidation: true);
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                break;
            case AnchorKind.Ref:
                w.WriteString("base", a.BaseId);
                break;
        }
        if (a.Alt != null) { w.WritePropertyName("alt"); WriteAnchor(w, a.Alt); }
        w.WriteEndObject();
    }

    private static string DeflateBase64(byte[] data)
    {
        using var z = new MemoryStream();
        using (var d = new DeflateStream(z, CompressionLevel.Optimal, leaveOpen: true)) d.Write(data, 0, data.Length);
        return Convert.ToBase64String(z.GetBuffer(), 0, (int)z.Length);
    }

    /// <summary>Writes the file in one piece: to a temporary beside it, then over the target, so an interrupted write never leaves half a stockpile.</summary>
    public static void SaveFile(string path, Stockpile s)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, WriteBytes(s));
        File.Move(tmp, path, overwrite: true);
    }

    // ===================================================================================================== reading

    public static StockpileReadResult LoadFile(string path)
    {
        var r = new StockpileReadResult();
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { r.Refusals.Add($"the file '{Path.GetFileName(path)}' does not exist"); return r; }
            if (fi.Length > MaxFileBytes) { r.Refusals.Add($"'{fi.Name}' is {fi.Length / 1048576} MB, more than the {MaxFileBytes / 1048576} MB a stockpile may be"); return r; }
            return Read(File.ReadAllBytes(path));
        }
        catch (Exception e)
        {
            r.Refusals.Add($"could not read '{Path.GetFileName(path)}': {e.Message}");
            return r;
        }
    }

    public static StockpileReadResult Read(string json) => Read(Encoding.UTF8.GetBytes(json));

    public static StockpileReadResult Read(byte[] bytes)
    {
        var r = new StockpileReadResult();
        if (bytes.Length > MaxFileBytes) { r.Refusals.Add($"the stockpile is {bytes.Length / 1048576} MB, more than the {MaxFileBytes / 1048576} MB a stockpile may be"); return r; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException e) { r.Refusals.Add("this is not a stockpile: the file is not valid JSON (" + e.Message + ")"); return r; }
        using (doc)
        {
            var root = doc.RootElement;
            try
            {
                if (root.ValueKind == JsonValueKind.Array) return ReadLegacy(root, r);
                if (root.ValueKind != JsonValueKind.Object) { r.Refusals.Add("this is not a stockpile: the JSON is neither a stockpile object nor a list of corruptions"); return r; }
                if (!TryProp(root, "format", out var fmt) || fmt.ValueKind != JsonValueKind.String || fmt.GetString() != Stockpile.FormatId)
                { r.Refusals.Add("this is not a BrokenNes stockpile (its \"format\" is missing or not \"" + Stockpile.FormatId + "\")"); return r; }
                int version = TryProp(root, "version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int vi) ? vi : 0;
                if (version > Stockpile.CurrentVersion)
                { r.Refusals.Add($"this stockpile was written by a newer BrokenNes (file version {version}; this build reads up to version {Stockpile.CurrentVersion}). Update the plugin or the app to open it"); return r; }
                if (version < 1) { r.Refusals.Add("this stockpile has no readable version number"); return r; }

                var s = new Stockpile { Version = version, Name = Str(root, "name"), Writer = Str(root, "writer"), Created = Date(root, "created") };
                if (TryProp(root, "bases", out var bs) && bs.ValueKind == JsonValueKind.Array)
                    foreach (var b in bs.EnumerateArray())
                    {
                        if (b.ValueKind != JsonValueKind.Object || !TryProp(b, "anchor", out var ba)) { r.Warnings.Add("a shared base without an anchor was skipped"); continue; }
                        var anchor = ReadAnchor(ba, r, "base '" + Str(b, "name") + "'", 0);
                        if (anchor == null) continue;
                        s.Bases.Add(new StockpileBase { Id = Str(b, "id"), Name = Str(b, "name"), Anchor = anchor });
                    }
                if (!TryProp(root, "entries", out var es) || es.ValueKind != JsonValueKind.Array) { r.Refusals.Add("this stockpile has no list of entries"); return r; }
                int index = 0;
                foreach (var e in es.EnumerateArray())
                {
                    index++;
                    if (s.Entries.Count >= MaxEntries) { r.Refusals.Add($"entries past the {MaxEntries}th were not read"); break; }
                    var entry = ReadEntry(e, r, index, legacy: false);
                    if (entry == null) continue;
                    if (entry.Anchor.Kind == AnchorKind.Ref && s.Resolve(entry.Anchor) == null)
                    { r.Refusals.Add($"entry {index} ('{entry.Name}') refused: it points to a shared base ('{entry.Anchor.BaseId}') the file does not contain"); continue; }
                    s.Entries.Add(entry);
                }
                r.Stockpile = s;
                return r;
            }
            catch (Exception e)
            {
                r.Stockpile = null;
                r.Refusals.Add("this stockpile could not be read: " + e.Message);
                return r;
            }
        }
    }

    // ---- the older files: a top-level array of { Id, Name, BaseStateId, Created, Writes, State | BaseState } ----

    private static StockpileReadResult ReadLegacy(JsonElement root, StockpileReadResult r)
    {
        r.Legacy = true;
        var s = new Stockpile { Version = 1, Writer = "Glitch Harvester export (older file)" };
        int index = 0;
        foreach (var e in root.EnumerateArray())
        {
            index++;
            if (s.Entries.Count >= MaxEntries) { r.Refusals.Add($"entries past the {MaxEntries}th were not read"); break; }
            var entry = ReadEntry(e, r, index, legacy: true);
            if (entry != null) s.Entries.Add(entry);
        }
        r.Stockpile = s;
        r.Warnings.Add("this is an older stockpile file (a bare list of NES corruptions); it will be saved in the current format");
        return r;
    }

    private static bool TryReadWrite(JsonElement w, out CorruptionWrite result, out string why)
    {
        result = default; why = "";
        if (w.ValueKind != JsonValueKind.Object) { why = "is not an object"; return false; }
        string domain = Str(w, "domain");
        if (!IsValidDomainId(domain)) { why = $"names a domain ('{Clip(domain, 24)}') that is not a domain id"; return false; }
        if (!TryProp(w, "address", out var a) || a.ValueKind != JsonValueKind.Number || !a.TryGetInt32(out int addr) || addr < 0) { why = "has no valid address"; return false; }
        if (!TryProp(w, "value", out var vv) || vv.ValueKind != JsonValueKind.Number || !vv.TryGetInt32(out int val) || val is < 0 or > 255) { why = "has a value that is not a byte"; return false; }
        int was = TryProp(w, "was", out var wv) && wv.ValueKind == JsonValueKind.Number && wv.TryGetInt32(out int wasv) && wasv is >= 0 and <= 255 ? wasv : -1;
        int at = TryProp(w, "at", out var av) && av.ValueKind == JsonValueKind.Number && av.TryGetInt32(out int atv) && atv >= 0 ? atv : 0;
        result = new CorruptionWrite(domain, addr, (byte)val, was, at);
        return true;
    }

    private static StockpileEntry? ReadEntry(JsonElement e, StockpileReadResult r, int index, bool legacy)
    {
        if (e.ValueKind != JsonValueKind.Object) { r.Refusals.Add($"entry {index} refused: it is not an object"); return null; }
        var entry = new StockpileEntry
        {
            Id = Str(e, "id") is { Length: > 0 } id ? id : Guid.NewGuid().ToString(),
            Name = Clip(Str(e, "name"), 256),
            Description = Clip(Str(e, "description"), 65536),
            Created = Date(e, "created"),
        };
        string label = $"entry {index} ('{entry.Name}')";
        if (TryProp(e, "tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            foreach (var t in tags.EnumerateArray().Take(64)) if (t.ValueKind == JsonValueKind.String) entry.Tags.Add(Clip(t.GetString() ?? "", 64));

        // writes
        if (TryProp(e, "writes", out var ws) && ws.ValueKind == JsonValueKind.Array)
        {
            int n = ws.GetArrayLength();
            if (n > MaxWritesPerEntry) { r.Refusals.Add($"{label} refused: {n:N0} writes, more than the {MaxWritesPerEntry:N0} an entry may hold"); return null; }
            int wi = 0;
            entry.Writes.Capacity = n;
            foreach (var w in ws.EnumerateArray())
            {
                wi++;
                if (!TryReadWrite(w, out var cw, out string werr)) { r.Refusals.Add($"{label} refused: write {wi} {werr}"); return null; }
                entry.Writes.Add(cw);
            }
        }

        if (TryProp(e, "game", out var g) && g.ValueKind == JsonValueKind.Object)
            entry.Game = new GameRef { Console = Str(g, "console"), Sha1 = Str(g, "sha1").ToLowerInvariant(), Sha256 = Str(g, "sha256").ToLowerInvariant(), Title = Clip(Str(g, "title"), 128), File = Clip(Path.GetFileName(Str(g, "file")), 256) };
        if (TryProp(e, "machine", out var m) && m.ValueKind == JsonValueKind.Object)
            entry.Machine = new MachineSpec { Cpu = Str(m, "cpu"), Ppu = Str(m, "ppu"), Apu = Str(m, "apu"), Region = Clip(Str(m, "region"), 32), Model = Clip(Str(m, "model"), 32), Note = Clip(Str(m, "note"), 256) };
        if (TryProp(e, "source", out var src) && src.ValueKind == JsonValueKind.Object)
        {
            var bs = new BlastSource { Blast = Str(src, "blast"), SizeModel = Str(src, "sizeModel") };
            if (TryProp(src, "intensity", out var ii) && ii.ValueKind == JsonValueKind.Number && ii.TryGetInt32(out int inten)) bs.Intensity = inten;
            if (TryProp(src, "seed", out var sd) && sd.ValueKind == JsonValueKind.Number && sd.TryGetInt64(out long seed)) bs.Seed = seed;
            if (TryProp(src, "domains", out var dd) && dd.ValueKind == JsonValueKind.Array) foreach (var d in dd.EnumerateArray().Take(32)) if (d.ValueKind == JsonValueKind.String) bs.Domains.Add(d.GetString() ?? "");
            entry.Source = bs;
        }
        entry.Legacy = Str(e, "legacy");

        if (legacy)
        {
            // Id Name BaseStateId Created Writes State | BaseState. A savestate of the NES (JSON text); Lite spelled it BaseState and the engine State.
            string state = Str(e, "state");
            if (state.Length == 0) state = Str(e, "basestate");
            entry.Game.Console = "nes";
            if (state.Length > 0)
            {
                if (state.Length > MaxStateBytes) { r.Refusals.Add($"{label} refused: its saved machine is larger than {MaxStateBytes / 1048576} MB"); return null; }
                entry.Anchor = new Anchor { Kind = AnchorKind.State, Console = "nes", StateFormat = "nes-json", StateData = Encoding.UTF8.GetBytes(state) };
                NesStateIdentity.Fill(entry.Game, state);
            }
            else
            {
                entry.Anchor = new Anchor { Kind = AnchorKind.None };
                entry.Legacy = "no-state";
                r.Warnings.Add($"{label} carries no saved machine (an export from the browser build that spelled it differently, or one made without a base): it can only be applied to whatever game is running, not replayed to the place it was made");
            }
        }
        else
        {
            if (TryProp(e, "anchor", out var an) && an.ValueKind == JsonValueKind.Object)
            {
                var anchor = ReadAnchor(an, r, label, 0);
                if (anchor == null) return null;
                entry.Anchor = anchor;
            }
            else entry.Anchor = new Anchor { Kind = AnchorKind.None };
        }
        return entry;
    }

    private static Anchor? ReadAnchor(JsonElement a, StockpileReadResult r, string label, int depth)
    {
        string kind = Str(a, "kind").ToLowerInvariant();
        var anchor = new Anchor();
        switch (kind)
        {
            case "none": anchor.Kind = AnchorKind.None; break;
            case "ref": anchor.Kind = AnchorKind.Ref; anchor.BaseId = Str(a, "base"); break;
            case "state":
            {
                anchor.Kind = AnchorKind.State;
                anchor.Console = Str(a, "console");
                anchor.StateFormat = Str(a, "stateFormat");
                if (TryProp(a, "frame", out var fr) && fr.ValueKind == JsonValueKind.Number && fr.TryGetInt32(out int frame)) anchor.Frame = frame;
                string enc = Str(a, "encoding").ToLowerInvariant(), data = Str(a, "data");
                if (!DecodeBytes(data, enc, MaxStateBytes, out var bytes, out string why)) { r.Refusals.Add($"{label} refused: its saved machine is damaged ({why})"); return null; }
                anchor.StateData = bytes;
                break;
            }
            case "boot":
            {
                anchor.Kind = AnchorKind.Boot;
                if (!TryProp(a, "frames", out var fr) || fr.ValueKind != JsonValueKind.Number || !fr.TryGetInt32(out int frames) || frames < 0) { r.Refusals.Add($"{label} refused: its boot anchor has no frame count"); return null; }
                anchor.Frames = frames;
                anchor.Inputs = Str(a, "inputs"); anchor.Inputs2 = Str(a, "inputs2");
                if (anchor.Inputs.Length > MaxInputChars || anchor.Inputs2.Length > MaxInputChars) { r.Refusals.Add($"{label} refused: its recorded input is too long"); return null; }
                if (TryProp(a, "events", out var evs) && evs.ValueKind == JsonValueKind.Array)
                {
                    int total = 0;
                    foreach (var ev in evs.EnumerateArray())
                    {
                        if (ev.ValueKind != JsonValueKind.Object || !TryProp(ev, "frame", out var evf) || !evf.TryGetInt32(out int evFrame) || evFrame < 0 || !TryProp(ev, "writes", out var evw) || evw.ValueKind != JsonValueKind.Array)
                        { r.Refusals.Add($"{label} refused: one of its recorded corruption events is damaged"); return null; }
                        var ae = new AnchorEvent { Frame = evFrame };
                        foreach (var w in evw.EnumerateArray())
                        {
                            if (++total > MaxWritesPerEntry) { r.Refusals.Add($"{label} refused: its recorded events hold more than {MaxWritesPerEntry:N0} writes"); return null; }
                            if (!TryReadWrite(w, out var cw, out string werr)) { r.Refusals.Add($"{label} refused: a recorded event's write {werr}"); return null; }
                            ae.Writes.Add(cw);
                        }
                        anchor.Events.Add(ae);
                    }
                }
                string bdata = Str(a, "battery");
                if (bdata.Length > 0)
                {
                    if (!DecodeBytes(bdata, Str(a, "batteryEncoding").ToLowerInvariant(), MaxBatteryBytes, out var bat, out string bwhy)) { r.Refusals.Add($"{label} refused: its save RAM is damaged ({bwhy})"); return null; }
                    anchor.Battery = bat;
                }
                break;
            }
            default:
                r.Refusals.Add($"{label} refused: it has an anchor of a kind this build does not know ('{Clip(kind, 24)}')");
                return null;
        }
        if (TryProp(a, "alt", out var alt) && alt.ValueKind == JsonValueKind.Object && depth < 2)
            anchor.Alt = ReadAnchor(alt, r, label + " (alternative)", depth + 1);   // a damaged alternative is dropped with its message; the main anchor stands
        return anchor;
    }

    /// <summary>Decodes "deflate-base64" (the writer's), "base64" or "json" (the text itself as UTF-8) into at most <paramref name="max"/> bytes.</summary>
    private static bool DecodeBytes(string data, string encoding, int max, out byte[] bytes, out string why)
    {
        bytes = Array.Empty<byte>(); why = "";
        try
        {
            switch (encoding)
            {
                case "json":
                    if (data.Length > max) { why = "too large"; return false; }
                    bytes = Encoding.UTF8.GetBytes(data); return true;
                case "base64":
                {
                    if (data.Length / 4L * 3 > max) { why = "too large"; return false; }
                    bytes = Convert.FromBase64String(data); return true;
                }
                case "deflate-base64":
                {
                    if (data.Length / 4L * 3 > max) { why = "too large"; return false; }   // the compressed size is bounded too; the inflated size is checked below
                    var packed = Convert.FromBase64String(data);
                    using var z = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    var buf = new byte[65536]; long total = 0; int k;
                    while ((k = z.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += k;
                        if (total > max) { why = "it inflates past the limit"; return false; }
                        outMs.Write(buf, 0, k);
                    }
                    bytes = outMs.ToArray(); return true;
                }
                default:
                    why = $"unknown encoding '{Clip(encoding, 24)}'"; return false;
            }
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException) { why = e.Message; return false; }
    }

    // ---- helpers ----

    /// <summary>A domain id: 1 to 16 letters, digits or underscores.</summary>
    public static bool IsValidDomainId(string s)
    {
        if (s.Length is 0 or > 16) return false;
        foreach (char c in s) if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) return false;
        return true;
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n];

    private static bool TryProp(JsonElement o, string name, out JsonElement value)
    {
        if (o.ValueKind == JsonValueKind.Object)
            foreach (var p in o.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private static string Str(JsonElement o, string name) => TryProp(o, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTime Date(JsonElement o, string name) =>
        TryProp(o, name, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.UtcNow;
}
