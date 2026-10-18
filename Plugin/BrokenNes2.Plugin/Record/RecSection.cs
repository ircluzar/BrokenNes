// Round 8 (record): a recording in an FL project, the saved-state section `REC1`. The section keeps the SESSION (every chip write, the copies of the sound memory), not only its MIDI file: a
// reopened project transcribes it again (the same bytes come out: the transcription is deterministic) and a later BrokenNes2 can transcribe it better. A recording too big for a project
// (over MaxStored once compressed) is written as a marker that says so; a damaged, cut-short or newer section is refused with the reason on screen, never half used; an older BrokenNes2
// skips the tag by its length.
//
//   u16 version (1) | u16 flags (bit 0 the session follows, bit 1 a marker: too big) | u32 payload bytes | u32 stored (deflated) bytes | the stored bytes | u16 note bytes | UTF-8 note
//   | u32 CRC-32 of everything before it
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BrokenNes2.Record;

public static class RecSection
{
    public const uint Tag = 0x31434552;                   // "REC1" (little-endian, like the other sections)
    public const int Version = 1, HeaderBytes = 12;
    public const int FlagSession = 1, FlagTooBig = 2, KnownFlags = 3;
    /// <summary>The biggest compressed recording a project keeps (under the 32 MB an older BrokenNes2 skips by its length).</summary>
    public const int DefaultMaxStored = 24 << 20;
    public static int MaxStored = DefaultMaxStored;
    public const int MaxPayload = 160 << 20;
    public const int MaxBytes = HeaderBytes + (28 << 20) + 2 + 4000 + 4;

    public static byte[] Build(RecSession s)
    {
        var payload = s.ToPayload();
        byte[] stored;
        using (var ms = new MemoryStream())
        {
            using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(payload);
            stored = ms.ToArray();
        }
        if (stored.Length > MaxStored) return Marker($"the recording is {stored.Length / 1048576.0:0.0} MB compressed, more than the {MaxStored / 1048576} MB a project keeps: save its MIDI file and presets to keep it", payload.Length);
        return Assemble(FlagSession, payload.Length, stored, "");
    }

    public static byte[] Marker(string why, int payloadLength = 0) => Assemble(FlagTooBig, payloadLength, [], why);

    private static byte[] Assemble(int flags, int payloadLength, byte[] stored, string note)
    {
        var nb = Encoding.UTF8.GetBytes(note.Length > 1000 ? note[..1000] : note);
        var b = new byte[HeaderBytes + stored.Length + 2 + nb.Length + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(b, Version);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)flags);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)stored.Length);
        stored.CopyTo(b, HeaderBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(HeaderBytes + stored.Length), (ushort)nb.Length);
        nb.CopyTo(b, HeaderBytes + stored.Length + 2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(b.Length - 4), Music.Crc32.Of(b.AsSpan(0, b.Length - 4)));
        return b;
    }

    /// <summary>The recording a section holds; null with the reason when it holds none (a marker: its note), or cannot be used ("damaged (...)", "cut short", "made by a newer BrokenNes2 ...").</summary>
    public static RecSession? Read(ReadOnlySpan<byte> s, out string? problem)
    {
        problem = null;
        if (s.Length < HeaderBytes + 2 + 4) { problem = "cut short"; return null; }
        if (s.Length > MaxBytes) { problem = "too big"; return null; }
        int version = BinaryPrimitives.ReadUInt16LittleEndian(s);
        if (version > Version) { problem = $"made by a newer BrokenNes2 (section version {version})"; return null; }
        if (version != Version) { problem = "of an unknown version"; return null; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(s[^4..]) != Music.Crc32.Of(s[..^4]) && !RecMutants.On(RecMutants.SectionCrcNotChecked)) { problem = "damaged (the checksum does not match)"; return null; }
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
        if ((flags & ~KnownFlags) != 0) { problem = "it uses a feature this version of BrokenNes2 does not know"; return null; }
        uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]), storedLen = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
        if (storedLen > s.Length || payloadLen > MaxPayload || HeaderBytes + (long)storedLen + 2 > s.Length - 4) { problem = "damaged (its lengths)"; return null; }
        int noteLen = BinaryPrimitives.ReadUInt16LittleEndian(s[(HeaderBytes + (int)storedLen)..]);
        if (HeaderBytes + (long)storedLen + 2 + noteLen + 4 != s.Length) { problem = "damaged (its lengths)"; return null; }
        string note;
        try { note = new UTF8Encoding(false, true).GetString(s.Slice(HeaderBytes + (int)storedLen + 2, noteLen)); }
        catch (DecoderFallbackException) { problem = "damaged (its note)"; return null; }
        if ((flags & FlagTooBig) != 0) { problem = note.Length > 0 ? note : "the recording was too big to keep in the project"; return null; }
        if ((flags & FlagSession) == 0 || storedLen == 0) { problem = "damaged (it holds no recording)"; return null; }
        byte[] payload;
        try
        {
            payload = new byte[payloadLen];
            using var z = new DeflateStream(new MemoryStream(s.Slice(HeaderBytes, (int)storedLen).ToArray()), CompressionMode.Decompress);
            int got = 0;
            while (got < payload.Length) { int n = z.Read(payload, got, payload.Length - got); if (n <= 0) break; got += n; }
            if (got != payload.Length || z.ReadByte() >= 0) { problem = "damaged (its compressed data)"; return null; }
        }
        catch (InvalidDataException) { problem = "damaged (its compressed data)"; return null; }
        return RecSession.FromPayload(payload, out problem);
    }
}
