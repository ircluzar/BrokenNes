namespace BrokenNes2.Instruments;

/// <summary>
/// Per-sample settings a SNES instrument edit adds to a <see cref="SpcSnapshot"/> (the Runaway working copy), keyed on the sample number (SRCN): an envelope, a tuning, voice volume weights, the echo / noise / pitch-modulation
/// flags, and whether the sample is "pinned" (listed and playable even when its BRR no longer looks well-formed, because the user broke it on purpose).
/// <para><b>Why it exists.</b> <see cref="SnesSampler"/> plays the game's samples at FL's pitch and level, with the envelope of a game voice that used the sample (<see cref="SpcSnapshot.TryEnvelope"/>) or an organ-like default.
/// Without this overlay an edit of the envelope of a sample no voice was playing, of its tuning or of its volumes would have no audible effect. A snapshot without an overlay (a clean game, a project from before this) plays
/// exactly as it always did: every use of it is behind a null test. It is immutable, allocation-free to query, and saved with the project as its own section (<see cref="Tag"/>, <see cref="ToBytes"/>).</para>
/// </summary>
public sealed class SnesOverlay
{
    public const uint Tag = 0x314F5053;          // "SPO1" (little-endian, like "SPC1")
    public const int Version = 1, MaxEntries = 256, EntryBytes = 14;
    /// <summary>The longest a saved overlay can be (the project's section reader refuses anything longer without reading it).</summary>
    public const int MaxBytes = 3 + MaxEntries * EntryBytes + 4;
    public const byte HasEnv = 1, HasTune = 2, HasVol = 4, HasMix = 8, Pinned = 16;
    public const byte MixEcho = 1, MixNoise = 2, MixPmod = 4;
    public const int MaxTuneCents = 4800;

    public struct Entry
    {
        public byte Srcn, Flags, Adsr1, Adsr2, Gain, Mix;
        /// <summary>Cents added to every note of this sample.</summary>
        public short Tune;
        /// <summary>Signed weights of the note's level on the left and right: 127 is the note's full level.</summary>
        public sbyte WeightL, WeightR;
        /// <summary>The sample's length in BRR blocks (pinned samples).</summary>
        public ushort Blocks;
    }

    private readonly Entry[] entries;
    private readonly short[] map = new short[256];
    private readonly double[] pitchFactor;

    public SnesOverlay(IEnumerable<Entry> items)
    {
        var sorted = items.OrderBy(e => e.Srcn).ToList();
        for (int i = sorted.Count - 1; i > 0; i--) if (sorted[i].Srcn == sorted[i - 1].Srcn) sorted.RemoveAt(i - 1);   // the later one of two entries for a sample wins
        entries = sorted.ToArray();
        Array.Fill(map, (short)-1);
        pitchFactor = new double[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            map[entries[i].Srcn] = (short)i;
            pitchFactor[i] = (entries[i].Flags & HasTune) != 0 ? Math.Pow(2.0, entries[i].Tune / 1200.0) : 1.0;
        }
    }

    public int Count => entries.Length;
    public ref readonly Entry At(int i) => ref entries[i];
    public IReadOnlyList<Entry> Entries => entries;

    /// <summary>The entry for a sample number, or -1. Allocation-free.</summary>
    public int IndexOf(int srcn) => map[srcn & 0xFF];

    public bool IsPinned(int srcn) { int i = map[srcn & 0xFF]; return i >= 0 && (entries[i].Flags & Pinned) != 0; }

    /// <summary>The DSP pitch register for a note after this sample's tuning (the register scales exactly with frequency).</summary>
    public int AdjustPitch(int index, int pitch) =>
        (entries[index].Flags & HasTune) == 0 ? pitch : Math.Clamp((int)Math.Round(pitch * pitchFactor[index]), 1, 0x3FFF);

    /// <summary>A voice volume register for a note level (0..127) after this sample's weight (127 is the full level, negative inverts the phase).</summary>
    public int AdjustVolume(int index, int level, bool right)
    {
        if ((entries[index].Flags & HasVol) == 0) return level;
        int w = right ? entries[index].WeightR : entries[index].WeightL;
        return Math.Clamp(level * w / 127, -128, 127);
    }

    /// <summary>A copy with an entry added or replaced; an entry with no flags left is removed. Null when nothing is left.</summary>
    public static SnesOverlay? With(SnesOverlay? current, Entry e)
    {
        var list = current != null ? new List<Entry>(current.entries) : new List<Entry>();
        list.RemoveAll(x => x.Srcn == e.Srcn);
        if ((e.Flags & (HasEnv | HasTune | HasVol | HasMix | Pinned)) != 0) list.Add(e);
        return list.Count == 0 ? null : new SnesOverlay(list);
    }

    // ---- saved with the project: u8 version, u16 count, entries (14 bytes), u32 CRC-32 of everything before it ----

    public byte[] ToBytes()
    {
        var b = new byte[3 + entries.Length * EntryBytes + 4];
        b[0] = Version; b[1] = (byte)entries.Length; b[2] = (byte)(entries.Length >> 8);
        int o = 3;
        foreach (var e in entries)
        {
            b[o++] = e.Srcn; b[o++] = e.Flags; b[o++] = e.Adsr1; b[o++] = e.Adsr2; b[o++] = e.Gain; b[o++] = e.Mix;
            b[o++] = (byte)e.Tune; b[o++] = (byte)(e.Tune >> 8); b[o++] = (byte)e.WeightL; b[o++] = (byte)e.WeightR; b[o++] = (byte)e.Blocks; b[o++] = (byte)(e.Blocks >> 8);
            b[o++] = 0; b[o++] = 0;                                  // reserved
        }
        uint crc = InstrumentCrc.Of(b.AsSpan(0, o));
        b[o++] = (byte)crc; b[o++] = (byte)(crc >> 8); b[o++] = (byte)(crc >> 16); b[o++] = (byte)(crc >> 24);
        return b;
    }

    /// <summary>Reads a saved overlay; null (with the reason) for anything that is not one: a wrong version, a length that does not fit, a checksum that does not match, a value out of range. Never trusted.</summary>
    public static SnesOverlay? FromBytes(byte[] b, out string? problem)
    {
        problem = null;
        if (b.Length < 7) { problem = "cut short"; return null; }
        if (b[0] != Version) { problem = b[0] > Version ? "from a newer version" : "unknown version"; return null; }
        int count = b[1] | b[2] << 8;
        if (count > MaxEntries || b.Length != 3 + count * EntryBytes + 4) { problem = "wrong length"; return null; }
        int end = 3 + count * EntryBytes;
        uint crc = (uint)(b[end] | b[end + 1] << 8 | b[end + 2] << 16 | b[end + 3] << 24);
        if (crc != InstrumentCrc.Of(b.AsSpan(0, end)) && !InstrumentMutants.On(InstrumentMutants.CrcNotChecked)) { problem = "damaged (checksum)"; return null; }
        var list = new List<Entry>();
        int o = 3;
        for (int i = 0; i < count; i++, o += EntryBytes)
        {
            var e = new Entry
            {
                Srcn = b[o], Flags = b[o + 1], Adsr1 = b[o + 2], Adsr2 = b[o + 3], Gain = b[o + 4], Mix = b[o + 5],
                Tune = (short)(b[o + 6] | b[o + 7] << 8), WeightL = (sbyte)b[o + 8], WeightR = (sbyte)b[o + 9], Blocks = (ushort)(b[o + 10] | b[o + 11] << 8),
            };
            if ((e.Flags & ~(HasEnv | HasTune | HasVol | HasMix | Pinned)) != 0 || (e.Mix & ~7) != 0 || Math.Abs((int)e.Tune) > MaxTuneCents || e.Blocks > 4096 || ((e.Flags & Pinned) != 0 && e.Blocks == 0))
            { problem = "damaged (a value out of range)"; return null; }
            list.Add(e);
        }
        for (int i = 1; i < list.Count; i++) if (list[i].Srcn <= list[i - 1].Srcn) { problem = "damaged (entries out of order)"; return null; }
        return new SnesOverlay(list);
    }
}

/// <summary>CRC-32 (the zip polynomial) for the instrument formats.</summary>
internal static class InstrumentCrc
{
    private static readonly uint[] Table = Build();
    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Of(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
