using System.Text;

namespace BrokenNes2.Instruments;

// =====================================================================================================================================================================
// The instrument model (Round 6, `imodel`): a console-neutral, UI-free description of the instruments a sound-memory snapshot holds.
//
//   Instrument  = typed Fields (id, display name, kind, min, max, current value, one-line explanation) + Blobs (a SNES BRR sample, a Genesis DAC sample, a Game Boy wave RAM, a NES DMC sample).
//   Readers     = SnesInstruments, SegaInstruments, GbInstruments, NesInstruments: snapshot -> Instrument list.
//   Writers     = the same classes: an edited Instrument -> a COPY of the snapshot, which the existing samplers play unchanged (SnesSampler, PsgSampler, OpllSampler, OpnSampler, GbSampler).
//   Corruptor   = InstrumentCorruptor / InstrumentHistory (Corruptor.cs): seeded, deterministic operations with locks, undo, redo, replay.
//   Codec       = InstrumentCodec (InstrumentCodec.cs): compact, versioned, CRC-checked bytes for an instrument and for a history.
//
// Rules this library keeps:
//   * Everything runs on the UI thread (or a worker). Nothing here is called from the audio thread; the samplers only read the finished snapshot.
//   * Everything that decides a corruption is integer arithmetic and the seeded PRNG: the same seed and operation give the same bytes on any machine.
//   * A field says whether the existing sampler PLAYS it (Audible). A field the sampler ignores is shown as such, never silently offered as an edit that does nothing.
// =====================================================================================================================================================================

/// <summary>The console an instrument belongs to; the numbers are the plugin's Console parameter (<c>Cx</c>): 0 NES, 1 Game Boy (and Color), 2 SNES, 3 Master System (and Game Gear), 4 Genesis.</summary>
public enum ConsoleId : byte { Nes = 0, GameBoy = 1, Snes = 2, MasterSystem = 3, Genesis = 4 }

/// <summary>What kind of instrument it is, which fixes its field list (its schema). The numbers are stored in saved histories and presets: append only.</summary>
public enum InstrumentKind : byte
{
    SnesSample = 1,
    OpnPatch = 10, OpnDac = 11,
    PsgTones = 20, PsgNoise = 21,
    OpllUser = 30, OpllRom = 31,
    GbPulse1 = 40, GbPulse2 = 41, GbWave = 42, GbNoise = 43,
    NesPulse1 = 50, NesPulse2 = 51, NesTriangle = 52, NesNoise = 53, NesDmc = 54,
}

/// <summary>How a field is edited: an enum (a short list of named values), an integer range, a bit field (flags: every bit is a toggle) or a signed integer.</summary>
public enum FieldKind : byte { Enum, Range, Bits, Signed }

/// <summary>Whether the sampler that plays the snapshot today uses a field.</summary>
public enum Audible : byte
{
    /// <summary>The sampler reads it: changing it changes the sound (in some cases only under the condition the note names).</summary>
    Yes,
    /// <summary>The sampler ignores it (it plays pitch and level from FL, or forces its own value). Changing it has no audible effect today; the note says why.</summary>
    No,
}

/// <summary>What a blob's bytes are, which decides the granularity the corruptor works at.</summary>
public enum BlobKind : byte
{
    /// <summary>SNES BRR: 9-byte blocks, a header (range 7-4, filter 3-2, loop 1, end 0) and sixteen signed 4-bit samples.</summary>
    Brr,
    /// <summary>Unsigned 8-bit samples, 128 the middle (the Genesis DAC).</summary>
    Pcm8,
    /// <summary>Packed 4-bit samples, the high nibble first (the Game Boy's wave RAM: 16 bytes = 32 samples).</summary>
    Nibbles,
    /// <summary>Plain bytes (the NES DMC's delta-coded sample).</summary>
    Bytes,
}

/// <summary>One editable value of an instrument: the bits of a register (or a few registers) with a name, a range, a current value and a one-line explanation of what it does.</summary>
public sealed class Field
{
    public string Id { get; }
    public string Name { get; }
    /// <summary>A heading the UI can group fields under ("Operator 2", "Envelope").</summary>
    public string Group { get; }
    public FieldKind Kind { get; }
    public int Min { get; }
    public int Max { get; }
    /// <summary>How many bits the field occupies in the register: a bit flip stays inside them.</summary>
    public int Width { get; }
    /// <summary>The value names of an Enum field (index = value - Min), or the names of the bits of a Bits field (index = bit); otherwise null.</summary>
    public string[]? Names { get; }
    public string Explanation { get; }
    public Audible Audible { get; }
    /// <summary>When <see cref="Audible"/> is No: why. When Yes: a condition worth knowing, or null.</summary>
    public string? AudibleNote { get; }
    /// <summary>Show the value in hexadecimal.</summary>
    public bool Hex { get; }
    public int Value { get; set; }

    private Field(string id, string name, string group, FieldKind kind, int min, int max, int width, string[]? names, string explanation, Audible audible, string? audibleNote, bool hex)
    {
        Id = id; Name = name; Group = group; Kind = kind; Min = min; Max = max; Width = width; Names = names; Explanation = explanation; Audible = audible; AudibleNote = audibleNote; Hex = hex;
    }

    private static int WidthFor(int max) { int w = 1; while (max >> w != 0) w++; return w; }

    public static Field Range(string id, string name, string group, int min, int max, string explanation, Audible audible = Audible.Yes, string? note = null, bool hex = false, int? width = null) =>
        new(id, name, group, FieldKind.Range, min, max, width ?? WidthFor(max), null, explanation, audible, note, hex);

    public static Field Signed(string id, string name, string group, int min, int max, int width, string explanation, Audible audible = Audible.Yes, string? note = null) =>
        new(id, name, group, FieldKind.Signed, min, max, width, null, explanation, audible, note, false);

    /// <summary>A flag set; <paramref name="width"/> bits, each a toggle. <paramref name="names"/> (optional) names the bits.</summary>
    public static Field Bits(string id, string name, string group, int width, string explanation, Audible audible = Audible.Yes, string? note = null, string[]? names = null) =>
        new(id, name, group, FieldKind.Bits, 0, (1 << width) - 1, width, names, explanation, audible, note, false);

    public static Field Enum(string id, string name, string group, string[] names, string explanation, Audible audible = Audible.Yes, string? note = null) =>
        new(id, name, group, FieldKind.Enum, 0, names.Length - 1, WidthFor(names.Length - 1), names, explanation, audible, note, false);

    /// <summary>A copy with its own value (the metadata is immutable and shared).</summary>
    public Field Clone() { var f = (Field)MemberwiseClone(); return f; }

    /// <summary>The value as text for a list or log: the name for an Enum, hex when asked, else the number.</summary>
    public string Text => Kind == FieldKind.Enum && Names != null && Value >= Min && Value - Min < Names.Length ? Names[Value - Min] : Hex ? "$" + Value.ToString("X" + Math.Max(2, (Width + 3) / 4)) : Value.ToString();

    public int Clamp(int v) => v < Min ? Min : v > Max ? Max : v;

    /// <summary>The value as unsigned bits of <see cref="Width"/> (a signed value in two's complement): what a bit flip works on.</summary>
    public uint Raw => (uint)Value & (uint)((1UL << Width) - 1);

    /// <summary>Sets the value from raw bits (see <see cref="Raw"/>), sign-extending a Signed field, then clamps to the legal range.</summary>
    public void SetRaw(uint raw)
    {
        raw &= (uint)((1UL << Width) - 1);
        int v = Kind == FieldKind.Signed && (raw >> (Width - 1) & 1) != 0 ? (int)raw - (1 << Width) : (int)raw;
        Value = Clamp(v);
    }
}

/// <summary>A run of bytes that is part of an instrument: a sample or a wave. The kind tells the corruptor how to break it.</summary>
public sealed class Blob
{
    public string Id { get; }
    public string Name { get; }
    public BlobKind Kind { get; }
    public string Explanation { get; }
    public Audible Audible { get; }
    public string? AudibleNote { get; }
    /// <summary>The smallest and largest length the blob may have. Equal means the length is fixed (a sample that lives in sound RAM cannot grow).</summary>
    public int MinLength { get; }
    public int MaxLength { get; }
    public byte[] Data { get; set; }

    public Blob(string id, string name, BlobKind kind, byte[] data, int minLength, int maxLength, string explanation, Audible audible = Audible.Yes, string? audibleNote = null)
    {
        Id = id; Name = name; Kind = kind; Data = data; MinLength = minLength; MaxLength = maxLength; Explanation = explanation; Audible = audible; AudibleNote = audibleNote;
    }

    public Blob Clone() { var b = (Blob)MemberwiseClone(); b.Data = (byte[])Data.Clone(); return b; }

    /// <summary>The number of units the corruptor works on: BRR blocks of 9 bytes, otherwise bytes (a wave RAM counts nibbles, see <see cref="Units"/>).</summary>
    public int Units => Kind switch { BlobKind.Brr => Data.Length / 9, BlobKind.Nibbles => Data.Length * 2, _ => Data.Length };
}

/// <summary>One instrument of a snapshot: its fields and blobs with the current values. A plain mutable object: the corruptor and the UI edit a clone.</summary>
public sealed class Instrument
{
    public ConsoleId Console { get; }
    public InstrumentKind Kind { get; }
    /// <summary>The number the plugin's Instrument picker uses for it in this snapshot (SNES: the sample slot; Sega: the SegaSnapshot picker index; Game Boy: the channel 0-3; NES: the channel 0-4).</summary>
    public int Index { get; }
    public string Name { get; set; }
    public string Description { get; set; }
    public Field[] Fields { get; }
    public Blob[] Blobs { get; }
    /// <summary>Read-only facts for display, not part of the instrument's value ("ARAM $1A40, 37 blocks", "envelope from voice 3").</summary>
    public List<(string Label, string Text)> Facts { get; } = new();
    /// <summary>For an SNES sample: the ARAM address of its first block (the corruptor keeps the loop pointer inside the sample with it). 0 for the others.</summary>
    public int Origin { get; set; }
    /// <summary>False for an instrument that is shown but cannot be written (a ROM instrument of the YM2413).</summary>
    public bool Editable { get; set; } = true;
    public string? ReadOnlyReason { get; set; }
    /// <summary>Whether the game was playing this instrument when it froze (a SNES voice on the sample, a keyed YM2612 or YM2413 channel).</summary>
    public bool Sounding { get; set; }
    /// <summary>For a Genesis DAC loop: the loop's pitch as the snapshot holds it, exactly (the field <c>dac.cycle</c> is that in hundredths of a hertz, rounded). A write keeps the exact value when the field still is its rounding, so editing something and undoing it gives the snapshot's bytes back. NaN: none.</summary>
    public double Exact { get; set; } = double.NaN;

    public Instrument(ConsoleId console, InstrumentKind kind, int index, string name, string description, Field[] fields, Blob[] blobs)
    {
        Console = console; Kind = kind; Index = index; Name = name; Description = description; Fields = fields; Blobs = blobs;
    }

    public Field? Field(string id) { foreach (var f in Fields) if (f.Id == id) return f; return null; }
    public Blob? Blob(string id) { foreach (var b in Blobs) if (b.Id == id) return b; return null; }

    /// <summary>The value of a field; throws for an id the instrument does not have (a mistyped id is a bug, not a default).</summary>
    public int Get(string id) => (Field(id) ?? throw new KeyNotFoundException($"{Kind} has no field '{id}'")).Value;

    public Instrument Clone()
    {
        var c = new Instrument(Console, Kind, Index, Name, Description, Array.ConvertAll(Fields, f => f.Clone()), Array.ConvertAll(Blobs, b => b.Clone()))
        { Origin = Origin, Editable = Editable, ReadOnlyReason = ReadOnlyReason, Sounding = Sounding, Exact = Exact };
        c.Facts.AddRange(Facts);
        return c;
    }

    /// <summary>Whether every field value and blob byte is the same (names and facts are not compared).</summary>
    public bool SameValues(Instrument o)
    {
        if (Kind != o.Kind || Fields.Length != o.Fields.Length || Blobs.Length != o.Blobs.Length) return false;
        for (int i = 0; i < Fields.Length; i++) if (Fields[i].Id != o.Fields[i].Id || Fields[i].Value != o.Fields[i].Value) return false;
        for (int i = 0; i < Blobs.Length; i++) if (Blobs[i].Id != o.Blobs[i].Id || !Blobs[i].Data.AsSpan().SequenceEqual(o.Blobs[i].Data)) return false;
        return true;
    }

    public override string ToString()
    {
        var sb = new StringBuilder($"{Name} [{Kind} #{Index}]");
        foreach (var f in Fields) sb.Append($" {f.Id}={f.Value}");
        foreach (var b in Blobs) sb.Append($" {b.Id}[{b.Data.Length}]");
        return sb.ToString();
    }
}

/// <summary>The outcome of writing an edited instrument into a snapshot copy: the new snapshot, or the reason it could not be done (never a silent partial write).</summary>
public sealed class WriteResult
{
    public bool Ok { get; }
    public string? Reason { get; }
    /// <summary>The snapshot copy with the edit in it (SpcSnapshot, GbSnapshot, SegaSnapshot or NesSnapshot); null when not <see cref="Ok"/>.</summary>
    public object? Snapshot { get; }
    /// <summary>What the write changed, for display ("loop pointer, 12 sample bytes, envelope"): empty when the instrument was unchanged.</summary>
    public IReadOnlyList<string> Changed { get; }

    private WriteResult(bool ok, string? reason, object? snapshot, IReadOnlyList<string> changed) { Ok = ok; Reason = reason; Snapshot = snapshot; Changed = changed; }
    public static WriteResult Success(object snapshot, IReadOnlyList<string> changed) => new(true, null, snapshot, changed);
    public static WriteResult Fail(string reason) => new(false, reason, null, Array.Empty<string>());
}

/// <summary>The snapshot of any console behind one interface: list its instruments, read one, write an edit into a copy.</summary>
public interface IInstrumentSource
{
    ConsoleId Console { get; }
    /// <summary>The snapshot object (SpcSnapshot, GbSnapshot, SegaSnapshot or NesSnapshot).</summary>
    object Snapshot { get; }
    IReadOnlyList<Instrument> List();
    Instrument? Read(int index);
    /// <summary>Writes <paramref name="edited"/> into a COPY of the snapshot; the snapshot itself is never changed. A write of an instrument that equals the snapshot's own gives a copy with identical bytes.</summary>
    WriteResult Write(Instrument edited);
}

/// <summary>Finds the reader for a snapshot object.</summary>
public static class InstrumentSources
{
    public static IInstrumentSource? For(object? snapshot) => snapshot switch
    {
        SpcSnapshot s => new SnesInstruments(s),
        SegaSnapshot s => new SegaInstruments(s),
        GbSnapshot s => new GbInstruments(s),
        NesSnapshot s => new NesInstruments(s),
        _ => null,
    };
}

// InstrumentMutants (the test-only bug switches) lives in InstrumentMutants.cs.
