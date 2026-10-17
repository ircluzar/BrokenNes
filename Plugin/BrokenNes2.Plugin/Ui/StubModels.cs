// Demonstration models for the tabs: an in-memory instrument model and preset library with the same behaviour the real ones will have in outline
// (typed fields, blobs, a seeded corruptor with history, a searchable library). They exist so the tab shells can be driven and tested before the real models
// land. NOTHING installs them in normal use: UiServices stays empty (the tabs then say "not connected in this build"); only the self-test hook (Fpd.TestBase + 403)
// puts them in. Everything here is deterministic.
namespace BrokenNes2.Ui;

internal sealed class StubInstrumentModel : IInstrumentModelProvider
{
    private sealed class Snap(int[] values, byte[][] blobs) { public readonly int[] Values = values; public readonly byte[][] Blobs = blobs; }

    private sealed class Inst
    {
        public string Name = "", Detail = "";
        public int[] Values = [];
        public bool[] Locked = [];
        public byte[][] Blobs = [];
        public bool[] BlobLocked = [];
        public readonly List<Snap> History = new();
        public int Pos;                      // index in History of the state shown
        public ulong Seed;
    }

    private static readonly (string Id, string Name, FieldKind Kind, int Min, int Max, int Def, string Why, string[]? Names)[] Defs =
    [
        ("volL", "Volume left", FieldKind.Range, 0, 127, 100, "How loud the voice is in the left speaker.", null),
        ("volR", "Volume right", FieldKind.Range, 0, 127, 100, "How loud the voice is in the right speaker.", null),
        ("tune", "Tuning", FieldKind.Signed, -128, 127, 0, "Shifts the pitch of every note by this many steps.", null),
        ("loop", "Loop", FieldKind.Enum, 0, 1, 1, "Whether the sample repeats while the key is held.", ["one-shot", "loops"]),
        ("mode", "Envelope mode", FieldKind.Enum, 0, 1, 0, "ADSR shapes the level by four stages; GAIN sets it directly.", ["ADSR", "GAIN"]),
        ("filter", "Filter", FieldKind.Enum, 0, 3, 1, "Which predictor the sample's blocks use to rebuild the waveform.", ["none", "1 tap", "2 tap", "2 tap+"]),
        ("flags", "Voice flags", FieldKind.BitField, 0, 255, 0x21, "Echo, noise and pitch modulation switches, one bit each.", null),
        ("attack", "Attack", FieldKind.Range, 0, 15, 12, "How fast the level rises after a key press.", null),
        ("decay", "Decay", FieldKind.Range, 0, 7, 5, "How fast the level falls to the sustain level.", null),
        ("sustain", "Sustain level", FieldKind.Range, 0, 7, 6, "The level held while the key stays down.", null),
        ("release", "Release", FieldKind.Range, 0, 31, 20, "How fast the level dies away after the key is released.", null),
        ("pitchBase", "Pitch base", FieldKind.BitField, 0, 0x3FFF, 0x1000, "The sample's own playing rate (14 bits).", null),
    ];

    private readonly Inst[] list;
    private long version = 1;

    // counters the self-test reads (Probe)
    private int nSet, nLock, nByte, nRandom, nNudge, nUndo, nRedo, nTake, nPush, lastStrength, lastNudgeDir, lastNudgeField = -2, lastInst = -1;
    public string LastCall { get; private set; } = "";

    public StubInstrumentModel(int count = 30)
    {
        list = new Inst[count];
        for (int i = 0; i < count; i++)
        {
            var n = new Inst
            {
                Name = $"Sample {i:X2}",
                Detail = $"${0x1A00 + i * 0x90:X4}, {120 + i * 7} ms, {(i % 3 == 0 ? "one-shot" : "loops")}",
                Values = Defs.Select(d => d.Def).ToArray(),
                Locked = new bool[Defs.Length],
                Seed = 0x9E3779B97F4A7C15UL ^ (ulong)i,
            };
            n.Values[0] = 60 + i;                                                // so instruments differ
            n.Blobs = i switch
            {
                1 => [Wave(300, 0), Wave(96, 1)],                                // two blobs
                2 => [[]],                                                       // an empty blob (nothing to draw)
                3 => [],                                                         // no blob at all
                _ => [Wave(i == 0 ? 600 : 144, i)],
            };
            n.BlobLocked = new bool[n.Blobs.Length];
            n.History.Add(Take(n));
            list[i] = n;
        }
    }

    /// <summary>A repeatable byte pattern (a rough sine with a harmonic) so the waveform and the hex field have something to show.</summary>
    private static byte[] Wave(int length, int variant)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++)
            b[i] = (byte)(128 + 100 * Math.Sin(i * 2 * Math.PI / (24 + variant)) + 20 * Math.Sin(i * 6 * Math.PI / (24 + variant)));
        return b;
    }

    private static Snap Take(Inst n) => new((int[])n.Values.Clone(), n.Blobs.Select(b => (byte[])b.Clone()).ToArray());

    private static void Restore(Inst n, Snap s)
    {
        n.Values = (int[])s.Values.Clone();
        for (int i = 0; i < n.Blobs.Length; i++) n.Blobs[i] = (byte[])s.Blobs[i].Clone();
    }

    private void Commit(Inst n)
    {
        if (n.Pos < n.History.Count - 1) n.History.RemoveRange(n.Pos + 1, n.History.Count - n.Pos - 1);
        n.History.Add(Take(n));
        n.Pos = n.History.Count - 1;
        version++;
    }

    private bool Has(int i) => (uint)i < (uint)list.Length;

    // ---- IInstrumentModelProvider ----
    public string? Unavailable => null;
    public long Version => version;
    public IReadOnlyList<InstrumentSummary> Instruments => list.Select((n, i) => new InstrumentSummary(n.Name, n.Detail, i == 2)).ToArray();

    public IReadOnlyList<InstrumentField> Fields(int instrument)
    {
        if (!Has(instrument)) return [];
        var n = list[instrument];
        return Defs.Select((d, i) => new InstrumentField(d.Id, d.Name, d.Kind, d.Min, d.Max, n.Values[i], d.Why, n.Locked[i], d.Names, i < 7 ? "Voice" : "Envelope")).ToArray();
    }

    public IReadOnlyList<InstrumentBlob> Blobs(int instrument)
    {
        if (!Has(instrument)) return [];
        var n = list[instrument];
        return n.Blobs.Select((b, i) =>
        {
            var samples = new short[b.Length];
            for (int k = 0; k < b.Length; k++) samples[k] = (short)((b[k] - 128) * 256);
            return new InstrumentBlob($"blob{i}", i == 0 ? "Sample" : $"Sample {i + 1}", BlobKind.Pcm8, b, samples, 32000, b.Length >= 100 ? b.Length / 4 : -1, b.Length >= 100 ? b.Length * 7 / 8 : -1, true, n.BlobLocked[i]);
        }).ToArray();
    }

    private int FieldIndex(string id) => Array.FindIndex(Defs, d => d.Id == id);

    public ActionResult SetField(int instrument, string fieldId, int value)
    {
        int f = FieldIndex(fieldId);
        if (!Has(instrument) || f < 0) return ActionResult.Refused("no such field");
        var d = Defs[f];
        var n = list[instrument];
        n.Values[f] = Math.Clamp(value, d.Min, d.Max);
        nSet++; lastInst = instrument; LastCall = $"SetField {instrument} {fieldId} {n.Values[f]}";
        Commit(n);
        return ActionResult.Done($"{d.Name} is now {n.Values[f]}");
    }

    public ActionResult SetFieldLocked(int instrument, string fieldId, bool locked)
    {
        int f = FieldIndex(fieldId);
        if (!Has(instrument) || f < 0) return ActionResult.Refused("no such field");
        list[instrument].Locked[f] = locked;
        nLock++; lastInst = instrument; LastCall = $"SetFieldLocked {instrument} {fieldId} {locked}";
        version++;
        return ActionResult.Done($"{Defs[f].Name} {(locked ? "locked" : "unlocked")}");
    }

    public ActionResult SetBlobByte(int instrument, string blobId, int offset, byte value)
    {
        if (!Has(instrument)) return ActionResult.Refused("no such instrument");
        var n = list[instrument];
        int b = blobId.StartsWith("blob") && int.TryParse(blobId.AsSpan(4), out int k) ? k : -1;
        if (b < 0 || b >= n.Blobs.Length || offset < 0 || offset >= n.Blobs[b].Length) return ActionResult.Refused("no such byte");
        n.Blobs[b][offset] = value;
        nByte++; lastInst = instrument; LastCall = $"SetBlobByte {instrument} {blobId} {offset} {value}";
        Commit(n);
        return ActionResult.Done($"byte {offset:X4} is now {value:X2}");
    }

    public ActionResult SetBlobLocked(int instrument, string blobId, bool locked)
    {
        if (!Has(instrument)) return ActionResult.Refused("no such instrument");
        var n = list[instrument];
        int b = blobId.StartsWith("blob") && int.TryParse(blobId.AsSpan(4), out int k) ? k : -1;
        if (b < 0 || b >= n.Blobs.Length) return ActionResult.Refused("no such blob");
        n.BlobLocked[b] = locked;
        version++;
        LastCall = $"SetBlobLocked {instrument} {blobId} {locked}";
        return ActionResult.Done($"sample data {(locked ? "locked" : "unlocked")}");
    }

    private static ulong Next(ref ulong s) { s ^= s << 13; s ^= s >> 7; s ^= s << 17; return s; }

    public ActionResult Randomise(int instrument, double strength)
    {
        if (!Has(instrument)) return ActionResult.Refused("no such instrument");
        var n = list[instrument];
        ulong s = n.Seed;
        int changed = 0;
        for (int f = 0; f < Defs.Length; f++)
        {
            if (n.Locked[f]) continue;
            if (Next(ref s) % 1000 < (ulong)(strength * 1000))
            {
                var d = Defs[f];
                n.Values[f] = d.Min + (int)(Next(ref s) % (ulong)(d.Max - d.Min + 1));
                changed++;
            }
        }
        for (int b = 0; b < n.Blobs.Length; b++)
            if (!n.BlobLocked[b])
                for (int k = 0; k < n.Blobs[b].Length * strength / 8 && n.Blobs[b].Length > 0; k++) n.Blobs[b][Next(ref s) % (ulong)n.Blobs[b].Length] ^= (byte)(1 << (int)(Next(ref s) % 8));
        n.Seed = s;
        nRandom++; lastInst = instrument; lastStrength = (int)Math.Round(strength * 1000); LastCall = $"Randomise {instrument} {strength:0.00}";
        Commit(n);
        return ActionResult.Done($"randomised {changed} field{(changed == 1 ? "" : "s")} at strength {strength:0%}");
    }

    public ActionResult Nudge(int instrument, string? fieldId, int direction)
    {
        if (!Has(instrument)) return ActionResult.Refused("no such instrument");
        var n = list[instrument];
        ulong s = n.Seed;
        int f = fieldId == null ? (int)(Next(ref s) % (ulong)Defs.Length) : FieldIndex(fieldId);
        if (f < 0) return ActionResult.Refused("no such field");
        n.Seed = s;
        nNudge++; lastInst = instrument; lastNudgeDir = direction; lastNudgeField = fieldId == null ? -1 : f; LastCall = $"Nudge {instrument} {fieldId ?? "(any)"} {direction}";
        if (n.Locked[f]) return ActionResult.Refused($"{Defs[f].Name} is locked");
        var d = Defs[f];
        n.Values[f] = Math.Clamp(n.Values[f] + (d.Kind == FieldKind.BitField ? 1 : Math.Max(1, (d.Max - d.Min) / 32)) * Math.Sign(direction), d.Min, d.Max);
        Commit(n);
        return ActionResult.Done($"nudged {d.Name} to {n.Values[f]}");
    }

    public bool CanUndo(int instrument) => Has(instrument) && list[instrument].Pos > 0;
    public bool CanRedo(int instrument) => Has(instrument) && list[instrument].Pos < list[instrument].History.Count - 1;

    public ActionResult Undo(int instrument)
    {
        if (!CanUndo(instrument)) return ActionResult.Refused("nothing to undo");
        var n = list[instrument];
        Restore(n, n.History[--n.Pos]);
        nUndo++; lastInst = instrument; LastCall = $"Undo {instrument}";
        version++;
        return ActionResult.Done("undone");
    }

    public ActionResult Redo(int instrument)
    {
        if (!CanRedo(instrument)) return ActionResult.Refused("nothing to redo");
        var n = list[instrument];
        Restore(n, n.History[++n.Pos]);
        nRedo++; lastInst = instrument; LastCall = $"Redo {instrument}";
        version++;
        return ActionResult.Done("redone");
    }

    public string HistoryText(int instrument) => Has(instrument) && list[instrument].History.Count > 1 ? $"step {list[instrument].Pos} of {list[instrument].History.Count - 1}" : "";
    public string SeedText(int instrument) => Has(instrument) && list[instrument].History.Count > 1 ? list[instrument].Seed.ToString("X16") : "";

    public (PresetDraft? Draft, string? Refusal) TakeForPreset(int instrument)
    {
        if (!Has(instrument)) return (null, "no such instrument");
        nTake++; lastInst = instrument; LastCall = $"TakeForPreset {instrument}";
        var n = list[instrument];
        return (new PresetDraft(n.Name, "SNES", "Stub game", "taken from the stub model", Take(n)), null);
    }

    public ActionResult PushIntoGame(int instrument)
    {
        if (!Has(instrument)) return ActionResult.Refused("no such instrument");
        nPush++; lastInst = instrument; LastCall = $"Push {instrument}";
        if (instrument % 10 == 9) return ActionResult.Refused("cannot push on this console: the stub says so");
        return ActionResult.Done("pushed: the sample bytes stay until the game reloads them; the envelope is rewritten at every note");
    }

    /// <summary>Numbers the self-test reads (hook 410).</summary>
    public long Probe(int what, int arg)
    {
        var n = list[0];
        switch (what)
        {
            case 0: return version;
            case 1: return nSet;
            case 2: return nLock;
            case 3: return nByte;
            case 4: return nRandom;
            case 5: return nNudge;
            case 6: return nUndo;
            case 7: return nRedo;
            case 8: return nTake;
            case 9: return nPush;
            case 10: return lastStrength;
            case 11: return lastNudgeDir;
            case 12: return (uint)arg < (uint)Defs.Length ? n.Values[arg] : -9999;
            case 13: return n.Blobs.Length > 0 && (uint)arg < (uint)n.Blobs[0].Length ? n.Blobs[0][arg] : -1;
            case 14: return (uint)arg < (uint)Defs.Length ? (n.Locked[arg] ? 1 : 0) : -1;
            case 15: return n.History.Count;
            case 16: return n.Pos;
            case 17: return lastNudgeField;
            case 18: return lastInst;
            case 19: { long h = 17; foreach (var b in n.Blobs) foreach (var x in b) h = h * 31 + x; return h; }
            case 20: return (uint)arg < (uint)Defs.Length ? list[1].Values[arg] : -9999;
            case 21: return list.Length;
            case 22: { int i = arg >> 8, f = arg & 0xFF; return (uint)i < (uint)list.Length && (uint)f < (uint)Defs.Length ? list[i].Values[f] : -9999; }      // value of field f of instrument i (arg = i << 8 | f)
            case 23: { int i = arg >> 8, f = arg & 0xFF; return (uint)i < (uint)list.Length && (uint)f < (uint)Defs.Length ? (list[i].Locked[f] ? 1 : 0) : -1; }
        }
        return 0;
    }
}

internal sealed class StubPresetLibrary : IPresetLibrary
{
    private sealed class Item { public string Id = "", Name = "", Console = "", Category = "", Source = "", Notes = ""; public string[] Tags = []; public int Fields, Blobs; public bool Behaviour; }

    private readonly List<Item> items = new();
    private long version = 1;
    private int nLoad, nRename, nDelete, nExport, nImport, nAudition, nAdd, nextId = 100;
    public string LastCall { get; private set; } = "";

    public StubPresetLibrary()
    {
        string[] consoles = ["NES", "GB", "SNES", "SMS", "MD", "SNES"];
        string[] names = ["Glass Bell", "Fuzz Bass", "Choir Pad", "Snare Crack", "Wobble Lead", "Sub Kick", "Pluck Harp", "Noise Burst", "Dream Strings", "Metal Hit", "Slap Bass", "Breath Flute", "Square Lead", "Rim Click"];
        for (int i = 0; i < names.Length; i++)
            items.Add(new Item
            {
                Id = $"p{i:D3}", Name = names[i], Console = consoles[i % consoles.Length], Category = i % 2 == 0 ? "Leads" : "Drums",
                Source = $"Stub game {i % 4}", Notes = $"A demonstration preset. Number {i}. It holds no sound: it is made up by the test.",
                Tags = i % 3 == 0 ? ["corrupted", "bright"] : ["clean"], Fields = 8 + i % 5, Blobs = i % 3, Behaviour = i % 4 == 0,
            });
    }

    public string? Unavailable => null;
    public long Version => version;

    public IReadOnlyList<PresetSummary> Search(string text, string? console)
    {
        string q = (text ?? "").Trim();
        return items
            .Where(p => (console == null || p.Console == console)
                        && (q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Source.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PresetSummary(p.Id, p.Name, p.Console, p.Category, $"{p.Source}, {p.Fields} fields"))
            .ToArray();
    }

    public PresetDetails? Details(string id)
    {
        var p = items.Find(x => x.Id == id);
        return p == null ? null : new PresetDetails(p.Id, p.Name, p.Console, p.Category, p.Source, "0123456789ABCDEF0123456789ABCDEF01234567", p.Tags, p.Notes, p.Fields, p.Blobs, p.Behaviour, p.Id.StartsWith("p0") ? "library" : "this project");
    }

    public ActionResult Add(PresetDraft draft)
    {
        nAdd++; LastCall = $"Add {draft.Name}";
        items.Add(new Item { Id = $"p{nextId++}", Name = draft.Name, Console = draft.Console, Category = "Ripped", Source = draft.SourceGame, Notes = draft.Notes, Fields = 12, Blobs = 1 });
        version++;
        return ActionResult.Done($"\"{draft.Name}\" is in the library");
    }

    public ActionResult Load(string id) { nLoad++; LastCall = $"Load {id}"; return items.Exists(p => p.Id == id) ? ActionResult.Done("loaded " + id) : ActionResult.Refused("no such preset"); }

    public ActionResult Rename(string id, string newName)
    {
        nRename++; LastCall = $"Rename {id} {newName}";
        var p = items.Find(x => x.Id == id);
        if (p == null) return ActionResult.Refused("no such preset");
        if (string.IsNullOrWhiteSpace(newName)) return ActionResult.Refused("a preset needs a name");
        p.Name = newName.Trim(); version++;
        return ActionResult.Done($"renamed to \"{p.Name}\"");
    }

    public ActionResult Delete(string id)
    {
        nDelete++; LastCall = $"Delete {id}";
        if (items.RemoveAll(p => p.Id == id) == 0) return ActionResult.Refused("no such preset");
        version++;
        return ActionResult.Done("deleted");
    }

    public ActionResult Export(string id, string path) { nExport++; LastCall = $"Export {id} {path}"; return items.Exists(p => p.Id == id) ? ActionResult.Done("exported to " + path) : ActionResult.Refused("no such preset"); }
    public ActionResult Import(string path) { nImport++; LastCall = $"Import {path}"; return ActionResult.Done("imported " + path); }
    public ActionResult Audition(string id) { nAudition++; LastCall = $"Audition {id}"; return items.Exists(p => p.Id == id) ? ActionResult.Done("playing " + id) : ActionResult.Refused("no such preset"); }

    /// <summary>Numbers the self-test reads (hook 411).</summary>
    public long Probe(int what)
    {
        switch (what)
        {
            case 0: return version;
            case 1: return items.Count;
            case 2: return nLoad;
            case 3: return nRename;
            case 4: return nDelete;
            case 5: return nExport;
            case 6: return nImport;
            case 7: return nAudition;
            case 8: return nAdd;
        }
        return 0;
    }
}
