// The game-state section of a project (tag "GAM1"): the whole machine of the game that runs in ROM mode, so that reopening a project in FL
// continues from where the game was (frozen in Instrument Runaway, or running), including every change made to it.
//
// A section is   tag u32 | length i32 | payload   and is appended after the sound sections, so a build that does not know it skips it.
// The payload (little endian):
//     0  u8   version (1)
//     1  u8   flags: 1 frozen (Instrument Runaway was on), 2 has the machine's state, 4 the state is deflated, 8 has the frozen picture, 16 the game could not keep its state
//     2  u8   console (Cx id)
//     3  u8   0
//     4  i64  frames the game had run
//    12  u32  CRC-32 of the state (as the game wrote it)
//    16  i32  length of the state (as the game wrote it)
//    20  i32  length of the state as stored (deflated when flag 4)
//    24  i32  picture width, 28 height, 32 shown width, 36 shown height   (flag 8)
//    40  i32  length of the picture as stored (deflated RGBA, width x height x 4 inflated)
//    44  u32  CRC-32 of the picture's RGBA
//    48  u16  length of the game id, 50 u16 length of the note
//    52  the game id (UTF-8: the ROM's SHA-1), the note (UTF-8: why the project holds no machine), the state, the picture
// A section without the machine (flag 2 clear) says why in its note: the game cannot keep its state yet, or the state was too big for the project.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BrokenNes2;

/// <summary>The frozen picture of a game: RGBA, <c>W</c> x <c>H</c> pixels, shown <c>DispW</c> x <c>DispH</c> in shape.</summary>
internal sealed record PictureCopy(int W, int H, int DispW, int DispH, byte[] Rgba);

internal sealed class GameStateSection
{
    /// <summary>"GAM1".</summary>
    public const uint Tag = 0x314D4147;
    public const int Version = 1;
    public const int HeaderSize = 52;
    /// <summary>The biggest section a save writes (bytes in the project): a state that does not fit is refused with a note, never cut.</summary>
    public const int DefaultMaxStored = 8 << 20;
    /// <summary>The biggest section a load reads, and the biggest state or picture it inflates: a section that claims more is damaged.</summary>
    public const int MaxRead = 32 << 20;
    public const int MaxInflated = 48 << 20;

    /// <summary>The cap a save applies (tests lower it).</summary>
    public static int MaxStored = DefaultMaxStored;

    private const byte FlagFrozen = 1, FlagState = 2, FlagDeflated = 4, FlagPicture = 8, FlagUnsupported = 16;

    public bool Frozen { get; private init; }
    /// <summary>The section holds no machine because the game could not keep its state when it was saved.</summary>
    public bool Unsupported { get; private init; }
    public bool HasState => State != null;
    public int Console { get; private init; }
    public long Frames { get; private init; }
    public string GameId { get; private init; } = "";
    /// <summary>Why this section holds no machine (empty when it holds one).</summary>
    public string Note { get; private init; } = "";
    /// <summary>The machine as the game wrote it (checked against its CRC); null when the section holds none.</summary>
    public byte[]? State { get; private init; }
    public PictureCopy? Picture { get; private init; }
    /// <summary>The payload as it was read, for a project saved again before its game is in place.</summary>
    public byte[] Raw { get; private init; } = [];

    // ------------------------------------------------------------------------------------------------ writing

    /// <summary>The payload of a section with the machine. Returns null when it does not fit under <see cref="MaxStored"/> (the caller then writes <see cref="BuildWithout"/> and says so).</summary>
    public static byte[]? Build(bool frozen, int console, long frames, string gameId, byte[] state, PictureCopy? picture)
    {
        byte flags = (byte)(FlagState | (frozen ? FlagFrozen : 0));
        // a state that is already deflated (the console sessions') gains nothing from it: the smaller of the two is kept
        var packed = Deflate(state);
        bool deflated = packed.Length < state.Length;
        var stored = deflated ? packed : state;
        if (deflated) flags |= FlagDeflated;
        byte[] pic = [];
        uint picCrc = 0;
        if (picture != null)
        {
            flags |= FlagPicture;
            pic = Deflate(picture.Rgba);
            picCrc = Crc32(picture.Rgba);
        }
        var id = Encoding.UTF8.GetBytes(gameId);
        long total = HeaderSize + id.Length + stored.Length + pic.Length;
        if (total > MaxStored) return null;
        var b = new byte[total];
        b[0] = Version; b[1] = flags; b[2] = (byte)console;
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(4), frames);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), Crc32(state));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), state.Length);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(20), stored.Length);
        if (picture != null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), picture.W);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), picture.H);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(32), picture.DispW);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(36), picture.DispH);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40), pic.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(44), picCrc);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(48), (ushort)id.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(50), 0);
        int at = HeaderSize;
        id.CopyTo(b, at); at += id.Length;
        stored.CopyTo(b, at); at += stored.Length;
        pic.CopyTo(b, at);
        return b;
    }

    /// <summary>The payload of a section that holds no machine, with the reason (shown to the user when the project is opened).</summary>
    public static byte[] BuildWithout(bool frozen, int console, long frames, string gameId, string note, bool unsupported)
    {
        var id = Encoding.UTF8.GetBytes(gameId);
        var text = Encoding.UTF8.GetBytes(note.Length > 600 ? note[..600] : note);
        var b = new byte[HeaderSize + id.Length + text.Length];
        b[0] = Version; b[1] = (byte)((frozen ? FlagFrozen : 0) | (unsupported ? FlagUnsupported : 0)); b[2] = (byte)console;
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(4), frames);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(48), (ushort)id.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(50), (ushort)text.Length);
        id.CopyTo(b, HeaderSize);
        text.CopyTo(b, HeaderSize + id.Length);
        return b;
    }

    // ------------------------------------------------------------------------------------------------ reading

    /// <summary>Reads and checks a payload. Nothing in it is trusted: lengths, picture size, inflated sizes and checksums are all checked, and a section that fails any is returned as null with the reason in <paramref name="problem"/>
    /// ("damaged", "cut short" or "from a newer version"), never partly.</summary>
    public static GameStateSection? Read(byte[] p, out string? problem)
    {
        problem = null;
        if (p.Length < HeaderSize) { problem = "cut short"; return null; }
        if (p[0] != Version) { problem = p[0] > Version ? "from a newer version of the plugin" : "damaged"; return null; }
        byte flags = p[1];
        int console = p[2];
        long frames = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(4));
        uint stateCrc = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(12));
        int rawLen = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(16)), storedLen = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(20));
        int picW = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(24)), picH = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(28));
        int dispW = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(32)), dispH = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(36));
        int picStored = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(40));
        uint picCrc = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(44));
        int idLen = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(48)), noteLen = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(50));
        bool hasState = (flags & FlagState) != 0, hasPicture = (flags & FlagPicture) != 0;
        if (frames < 0 || console >= Cx.Count) { problem = "damaged"; return null; }
        long need = HeaderSize + (long)idLen + noteLen + (hasState ? storedLen : 0) + (hasPicture ? picStored : 0);
        if (idLen > 200 || noteLen > 1000 || (hasState && (rawLen < 0 || storedLen < 0 || rawLen > MaxInflated || storedLen > MaxRead)) || (hasPicture && (picStored < 0 || picW is < 1 or > 512 || picH is < 1 or > 480 || dispW is < 1 or > 512 || dispH is < 1 or > 480)) || need != p.Length)
        {
            problem = "damaged";
            return null;
        }
        int at = HeaderSize;
        string id = Encoding.UTF8.GetString(p, at, idLen); at += idLen;
        string note = Encoding.UTF8.GetString(p, at, noteLen); at += noteLen;
        byte[]? state = null;
        if (hasState)
        {
            state = (flags & FlagDeflated) != 0 ? Inflate(p, at, storedLen, rawLen) : storedLen == rawLen ? p.AsSpan(at, storedLen).ToArray() : null;
            at += storedLen;
            if (state == null || Crc32(state) != stateCrc) { problem = "damaged"; return null; }
        }
        PictureCopy? picture = null;
        if (hasPicture)
        {
            var rgba = Inflate(p, at, picStored, picW * picH * 4);
            if (rgba == null || Crc32(rgba) != picCrc) { problem = "damaged"; return null; }
            picture = new PictureCopy(picW, picH, dispW, dispH, rgba);
        }
        return new GameStateSection { Frozen = (flags & FlagFrozen) != 0, Unsupported = (flags & FlagUnsupported) != 0, Console = console, Frames = frames, GameId = id, Note = note, State = state, Picture = picture, Raw = p };
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream(data.Length / 2 + 64);
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    /// <summary>Inflates exactly <paramref name="expected"/> bytes; null when the stream is damaged, shorter or longer.</summary>
    private static byte[]? Inflate(byte[] src, int offset, int length, int expected)
    {
        try
        {
            var result = new byte[expected];
            using var z = new DeflateStream(new MemoryStream(src, offset, length), CompressionMode.Decompress);
            int got = 0;
            while (got < expected) { int k = z.Read(result, got, expected - got); if (k <= 0) return null; got += k; }
            return z.ReadByte() < 0 ? result : null;       // data after the announced length is as damaged as data missing
        }
        catch (InvalidDataException) { return null; }
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
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

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
