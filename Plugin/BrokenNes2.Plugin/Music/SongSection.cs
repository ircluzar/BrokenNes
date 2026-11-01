// Round 7 (midi): the song of "Test MIDI export" in an FL project, the saved-state section `SNG1`. The emulator's song is kept as the MIDI file's own bytes and
// its path, so a reopened project converts the same MIDI with the same converter into the same template and replays the same song (the game-state section
// GAM1 then puts the machine back where it was: the ROM is the same bytes, so its id matches). A project without the section opens as before; an older
// BrokenNes2 skips it by its length; a damaged, cut or newer section is refused with the reason on screen, never half used.
//
//   u16 version (1) | u16 flags (bit 0 the song is the emulator's NES game, bit 1 follow FL's transport, bit 2 stopped) | u16 template format version | u16 path bytes
//   | u32 MIDI bytes | UTF-8 path | the MIDI file | u32 CRC-32 of everything before it
using System.Buffers.Binary;
using System.Text;

namespace BrokenNes2.Music;

public sealed record SongSection(string Path, byte[] Midi, bool Active, bool FollowTransport, bool Stopped)
{
    public const uint Tag = 0x31474E53;                 // "SNG1" (little-endian, like the other sections)
    public const int Version = 1, HeaderBytes = 12, MaxPathBytes = 4096;
    public const int MaxBytes = HeaderBytes + MaxPathBytes + SongConvert.MaxMidiBytes + 4;
    public const int FlagActive = 1, FlagFollow = 2, FlagStopped = 4, KnownFlags = 7;

    public byte[] Build()
    {
        var path = Encoding.UTF8.GetBytes(Path);
        if (path.Length > MaxPathBytes) path = Encoding.UTF8.GetBytes(System.IO.Path.GetFileName(Path));
        var midi = MusicMutate.Has(MusicMutate.SectionDropsMidi) ? [] : Midi;       // (the self-test's deliberate bug)
        var b = new byte[HeaderBytes + path.Length + midi.Length + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(b, Version);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)((Active ? FlagActive : 0) | (FollowTransport ? FlagFollow : 0) | (Stopped ? FlagStopped : 0)));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), MusicTemplate.FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)path.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)midi.Length);
        path.CopyTo(b, HeaderBytes);
        midi.CopyTo(b, HeaderBytes + path.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(b.Length - 4), Crc32.Of(b.AsSpan(0, b.Length - 4)));
        return b;
    }

    /// <summary>The song a section holds, or the reason it cannot be used ("damaged (...)", "cut short", "made by a newer BrokenNes2 ...").</summary>
    public static SongSection? Read(ReadOnlySpan<byte> s, out string? problem)
    {
        problem = null;
        if (s.Length < HeaderBytes + 4) { problem = "cut short"; return null; }
        if (s.Length > MaxBytes) { problem = "too big"; return null; }
        int version = BinaryPrimitives.ReadUInt16LittleEndian(s);
        if (version > Version) { problem = $"made by a newer BrokenNes2 (section version {version})"; return null; }
        if (version != Version) { problem = "of an unknown version"; return null; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(s[^4..]) != Crc32.Of(s[..^4])) { problem = "damaged (the checksum does not match)"; return null; }
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
        if ((flags & ~KnownFlags) != 0) { problem = "it uses a feature this version of BrokenNes2 does not know"; return null; }
        int pathLen = BinaryPrimitives.ReadUInt16LittleEndian(s[6..]);
        uint midiLen = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
        if (pathLen > MaxPathBytes || midiLen > SongConvert.MaxMidiBytes || HeaderBytes + pathLen + (long)midiLen + 4 != s.Length) { problem = "damaged (its lengths)"; return null; }
        if (midiLen == 0) { problem = "damaged (it holds no MIDI file)"; return null; }
        string path;
        try { path = new UTF8Encoding(false, true).GetString(s.Slice(HeaderBytes, pathLen)); }
        catch (DecoderFallbackException) { problem = "damaged (its file name)"; return null; }
        var midi = s.Slice(HeaderBytes + pathLen, (int)midiLen).ToArray();
        return new SongSection(path, midi, (flags & FlagActive) != 0, (flags & FlagFollow) != 0, (flags & FlagStopped) != 0);
    }
}

/// <summary>CRC-32 (IEEE 802.3, the zip one).</summary>
public static class Crc32
{
    private static readonly uint[] Table = Make();

    private static uint[] Make()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Of(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
