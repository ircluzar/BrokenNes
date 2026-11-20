using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace NesEmulator.Systems;

/// <summary>What a save-state file says about itself before anything is loaded from it.</summary>
public sealed record ConsoleStateInfo(ConsoleKind Console, string GameId, string Title, IReadOnlyDictionary<string, string> Units, int PayloadLength);

/// <summary>
/// The one container every console session's save state travels in (<see cref="IConsoleSession.SaveState"/>): a header that names the console, the game (the ROM's SHA-1) and the parts the machine was built from
/// (the sound unit, the board model and region, the layout of the payload), then the session's own bytes, deflated, with a checksum. The header is checked <i>before</i> a byte of the machine is touched, so a state
/// that belongs to another game, another console or another sound unit is refused with a message that says which; it is never loaded into something else, and nothing is swapped behind the user's back.
/// <para>The container is shared and has no knowledge of any console's payload. A session lists the parts that must match (<c>units</c>); the others it writes only for information.</para>
/// </summary>
public static class ConsoleStateFile
{
    private static readonly byte[] Magic = "BNCS"u8.ToArray();
    /// <summary>The layout of this container (not of any console's payload: that is a unit named "format").</summary>
    public const int Version = 1;

    /// <summary>Wraps a session's payload. <paramref name="units"/> are the parts the state depends on, by name (for example <c>apu</c> = <c>SMS-FM</c>, <c>format</c> = <c>sms-1</c>).</summary>
    public static byte[] Wrap(ConsoleKind console, string gameId, string title, IReadOnlyDictionary<string, string> units, byte[] payload)
        => Wrap(console, gameId, title, units, new ArraySegment<byte>(payload));

    /// <summary><see cref="Wrap(ConsoleKind, string, string, IReadOnlyDictionary{string, string}, byte[])"/> of a part of a buffer (a reused one).</summary>
    public static byte[] Wrap(ConsoleKind console, string gameId, string title, IReadOnlyDictionary<string, string> units, ArraySegment<byte> payload)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic); w.Write((ushort)Version);
            w.Write(Consoles.Key(console)); w.Write(gameId); w.Write(title);
            w.Write((byte)units.Count);
            foreach (var kv in units) { w.Write(kv.Key); w.Write(kv.Value); }
            if (payload.Count >= ParallelMin) w.Write(payload.Count);   // (a big state: its CRC and packed data are added below)
            else
            {
                w.Write(payload.Count); w.Write(Crc32(payload.AsSpan()));
                using var z = new MemoryStream();
                using (var d = new DeflateStream(z, CompressionLevel.Fastest, leaveOpen: true)) d.Write(payload.AsSpan());
                w.Write((int)z.Length); w.Write(z.GetBuffer(), 0, (int)z.Length);
            }
        }
        if (payload.Count < ParallelMin) return ms.ToArray();
        var r = PackBig(ms, payload, null);
        return r.Array!;
    }

    /// <summary><see cref="Wrap(ConsoleKind, string, string, IReadOnlyDictionary{string, string}, ArraySegment{byte})"/> into <paramref name="reuse"/>
    /// when the file fits there (a buffer the caller no longer needs: the bug capture's evicted state), else a new array with 1/8 to spare;
    /// the file is the returned segment (the same bytes as Wrap).</summary>
    public static ArraySegment<byte> WrapInto(ConsoleKind console, string gameId, string title, IReadOnlyDictionary<string, string> units, ArraySegment<byte> payload, byte[]? reuse)
    {
        if (payload.Count < ParallelMin) return new ArraySegment<byte>(Wrap(console, gameId, title, units, payload));
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic); w.Write((ushort)Version);
            w.Write(Consoles.Key(console)); w.Write(gameId); w.Write(title);
            w.Write((byte)units.Count);
            foreach (var kv in units) { w.Write(kv.Key); w.Write(kv.Value); }
            w.Write(payload.Count);
        }
        return PackBig(ms, payload, reuse ?? Array.Empty<byte>());
    }

    /// <summary>The big-state tail: CRC + parallel deflate after the header in <paramref name="ms"/>. <paramref name="reuse"/> null: an exact-size
    /// new array; else that buffer when the file fits, or a new one with room to spare.</summary>
    private static ArraySegment<byte> PackBig(MemoryStream ms, ArraySegment<byte> payload, byte[]? reuse)
    {
        // a big state (the x86 PC: ~110 MB, the bug capture takes one a second): the CRC and the compression in parallel pieces into pooled
        // buffers, then the file in ONE array of its exact size (the same format and payload; no growing copies: they were GBs of garbage)
        var crc = System.Threading.Tasks.Task.Run(() => Crc32(payload.AsSpan()));
        var (parts, lens) = DeflateParallel(payload);
        try
        {
            long packed = 0; foreach (int l in lens) packed += l;
            int head = (int)ms.Length, total = checked(head + 8 + (int)packed);
            var file = reuse == null ? new byte[total] : reuse.Length >= total ? reuse : new byte[total + total / 8];
            ms.GetBuffer().AsSpan(0, head).CopyTo(file);
            BitConverter.TryWriteBytes(file.AsSpan(head, 4), crc.Result);
            BitConverter.TryWriteBytes(file.AsSpan(head + 4, 4), (int)packed);
            int at = head + 8;
            for (int i = 0; i < parts.Length; i++) { parts[i].AsSpan(0, lens[i]).CopyTo(file.AsSpan(at)); at += lens[i]; }
            return new ArraySegment<byte>(file, 0, total);
        }
        finally { foreach (var p in parts) System.Buffers.ArrayPool<byte>.Shared.Return(p); }
    }

    private const int ParallelMin = 16 << 20, ParallelChunk = 1 << 20;

    /// <summary>One raw deflate stream made of independently compressed 1 MB pieces: every piece but the last ends with a sync flush (byte-aligned,
    /// not final), the last one with the final block, so the pieces simply follow each other and any inflater reads them as one stream.
    /// The pieces are pooled buffers (the caller returns them) of their packed lengths.</summary>
    internal static (byte[][] parts, int[] lens) DeflateParallel(ArraySegment<byte> payload)
    {
        int n = (payload.Count + ParallelChunk - 1) / ParallelChunk;
        var parts = new byte[n][]; var lens = new int[n];
        System.Threading.Tasks.Parallel.For(0, n, i =>
        {
            int off = i * ParallelChunk, len = Math.Min(ParallelChunk, payload.Count - off);
            var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(len + len / 8 + 4096);   // (deflate never grows data by more than ~0.1 %)
            var z = new MemoryStream(buf, 0, buf.Length, writable: true, publiclyVisible: true);
            var d = new DeflateStream(z, CompressionLevel.Fastest, leaveOpen: true);
            d.Write(payload.Array!, payload.Offset + off, len);
            if (i < n - 1) { d.Flush(); lens[i] = (int)z.Position; }   // sync flush: everything so far, no final block (what disposing adds after it is ignored)
            d.Dispose();
            if (i == n - 1) lens[i] = (int)z.Position;
            parts[i] = buf;
        });
        return (parts, lens);
    }

    /// <summary>Reads the header only. Throws <see cref="InvalidDataException"/> (with the reason, for the user) when this is not a console state at all.</summary>
    public static ConsoleStateInfo Peek(byte[] file)
    {
        using var r = new BinaryReader(new MemoryStream(file), Encoding.UTF8);
        return ReadHeader(r, out _, out _);
    }

    private static ConsoleStateInfo ReadHeader(BinaryReader r, out uint crc, out int packed)
    {
        crc = 0; packed = 0;
        try
        {
            if (r.BaseStream.Length < 8 || !r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("This file is not a BrokenNes console state.");
            int version = r.ReadUInt16();
            if (version != Version) throw new InvalidDataException($"This state was written in a different state file format (version {version}; this build reads version {Version}).");
            string key = r.ReadString(), id = r.ReadString(), title = r.ReadString();
            var console = Consoles.FromKey(key);
            int n = r.ReadByte();
            var units = new Dictionary<string, string>();
            for (int i = 0; i < n; i++) units[r.ReadString()] = r.ReadString();
            int length = r.ReadInt32(); crc = r.ReadUInt32(); packed = r.ReadInt32();
            return new ConsoleStateInfo(console, id, title, units, length);
        }
        catch (EndOfStreamException) { throw new InvalidDataException("This state file is cut short."); }
    }

    /// <summary>
    /// Checks a state file against the running session and returns its payload. <paramref name="mustMatch"/> are the parts that have to be equal (a missing key in the file counts as different). Every refusal is an
    /// <see cref="InvalidDataException"/> whose message is fit to show the user.
    /// </summary>
    public static byte[] Unwrap(byte[] file, ConsoleKind console, string gameId, string title, IReadOnlyDictionary<string, string> mustMatch)
    {
        using var r = new BinaryReader(new MemoryStream(file), Encoding.UTF8);
        var info = ReadHeader(r, out uint crc, out int packed);
        if (info.Console != console)
            throw new InvalidDataException($"This state is for the {Consoles.DisplayName(info.Console)}, not the {Consoles.DisplayName(console)}.");
        if (!string.Equals(info.GameId, gameId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"This state belongs to \"{info.Title}\", not to \"{title}\" (a state only loads into the game it was saved from).");
        foreach (var kv in mustMatch)
        {
            info.Units.TryGetValue(kv.Key, out string? had);
            if (!string.Equals(had, kv.Value, StringComparison.Ordinal))
                throw new InvalidDataException(Describe(kv.Key, had, kv.Value));
        }
        if (packed < 0 || packed > file.Length - r.BaseStream.Position) throw new InvalidDataException("This state file is cut short.");
        byte[] payload = new byte[info.PayloadLength];
        try
        {
            using var z = new DeflateStream(new MemoryStream(file, (int)r.BaseStream.Position, packed), CompressionMode.Decompress);
            int got = 0;
            while (got < payload.Length) { int k = z.Read(payload, got, payload.Length - got); if (k <= 0) break; got += k; }
            if (got != payload.Length) throw new InvalidDataException("The state's data is shorter than its header says.");
        }
        catch (InvalidDataException) { throw new InvalidDataException("The state file is damaged (its data does not decompress)."); }
        if (Crc32(payload) != crc) throw new InvalidDataException("The state file is damaged (its checksum does not match).");
        return payload;
    }

    /// <summary>The payload of a state file with no check against a running session (its own checksum is checked): what <see cref="IConsoleSession.SaveStatePayload"/> gives by default.</summary>
    public static byte[] PayloadOf(byte[] file)
    {
        var info = Peek(file);
        return Unwrap(file, info.Console, info.GameId, info.Title, new Dictionary<string, string>());
    }

    private static string Describe(string unit, string? saved, string running) => unit switch
    {
        "apu" => $"This state was saved with the {saved ?? "unknown"} sound unit, but the game is running the {running} sound unit. Switch the sound unit to {saved ?? "that one"} in the APU menu and load the state again: a state is never loaded into a different sound unit.",
        "cpu" => $"This state was saved with the {saved ?? "unknown"} CPU core, but the game is running the {running} one. Switch the CPU core to {saved ?? "that one"} and load the state again.",
        "model" => $"This state was saved on a {saved ?? "different"} board model; the game is running as {running}.",
        "region" => $"This state was saved at {saved ?? "another"} timing; the game is running at {running} (the region setting is a part of the machine).",
        "chip" => $"This state was saved with the cartridge chip \"{saved ?? "unknown"}\", but the game is running with \"{running}\" (a state is never loaded into a different chip: the homemade DSP-1 and Nintendo's DSP-1 firmware are different chips, so put the firmware back the way it was, or leave it out, and load the state again).",
        "format" => $"This state was saved by a different version of the core (state layout {saved ?? "unknown"}, this build uses {running}); it cannot be loaded.",
        _ => $"This state was saved with {unit} = {saved ?? "unknown"}; the game is running {unit} = {running}.",
    };

    /// <summary>The per-game file a desktop app keeps a console's quick-save slot in: <c>&lt;root&gt;\States\&lt;console key&gt;\&lt;game id&gt;.&lt;slot&gt;.bnstate</c>.</summary>
    public static string SlotPath(string root, ConsoleKind console, string gameId, string slot = "quick") =>
        Path.Combine(root, "States", Consoles.Key(console), gameId + "." + slot + ".bnstate");

    /// <summary>The key a web app keeps a game's slot under in its key-value store.</summary>
    public static string SlotKey(ConsoleKind console, string gameId, string slot = "quick") => $"state_v1:{Consoles.Key(console)}:{gameId}.{slot}";

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>CRC-32 (the zip / PNG polynomial), a table at a time: the same value as the bit-by-bit definition (the SNES state test checks that), about ten times faster on the half-megabyte a SNES state is.</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {   // slicing-by-8 (the same CRC-32, 8 bytes a step: a 110 MB x86 state took 220 ms byte by byte)
        uint crc = 0xFFFFFFFF;
        var t = Crc8;
        int i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            uint a = crc ^ System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i));
            uint b = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4));
            crc = t[7 * 256 + (a & 0xff)] ^ t[6 * 256 + ((a >> 8) & 0xff)] ^ t[5 * 256 + ((a >> 16) & 0xff)] ^ t[4 * 256 + (a >> 24)]
                ^ t[3 * 256 + (b & 0xff)] ^ t[2 * 256 + ((b >> 8) & 0xff)] ^ t[256 + ((b >> 16) & 0xff)] ^ t[b >> 24];
        }
        var table = CrcTable;
        for (; i < data.Length; i++) crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static readonly uint[] Crc8 = BuildCrc8();
    private static uint[] BuildCrc8()
    {
        var t = new uint[8 * 256];
        var b = BuildCrcTable();
        for (int i = 0; i < 256; i++) t[i] = b[i];
        for (int k = 1; k < 8; k++) for (int i = 0; i < 256; i++) t[k * 256 + i] = (t[(k - 1) * 256 + i] >> 8) ^ t[t[(k - 1) * 256 + i] & 0xff];
        return t;
    }

    /// <summary>The same CRC-32 for other files (BrokenDev Timeflow's blocks).</summary>
    public static uint Crc32Public(ReadOnlySpan<byte> data) => Crc32(data);

    /// <summary>The definition, bit by bit (for tests).</summary>
    internal static uint Crc32Reference(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
