using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Timeflow;

/// <summary>
/// A timeline's description (<c>timeline.json</c> in its folder): what was recorded (the console, the ROM by sha256, the cores), how (the
/// keyframe interval, the page size, the compression), where it comes from (a parent timeline and frame when it is a branch), and how it
/// ended. The format is milestone T1's (docs/projects/brokendev-timeflow-plan.md, "T1 as built").
/// </summary>
public sealed class TimelineMeta
{
    public const string FormatName = "bntimeline";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = CurrentVersion;
    public string Id { get; set; } = "";
    public string Console { get; set; } = "";
    public string RomName { get; set; } = "";
    public string RomSha256 { get; set; } = "";
    public long RomSize { get; set; }
    /// <summary>The ROM file's extension in the store (<c>roms\&lt;sha256&gt;&lt;ext&gt;</c>).</summary>
    public string RomExt { get; set; } = "";
    public string Cores { get; set; } = "";
    /// <summary>The NES's power-on RAM fill and frame timing (D5's words), for a replay that starts at power-on.</summary>
    public string PowerOn { get; set; } = "fceux";
    public bool Ntsc { get; set; }
    public string Project { get; set; } = "";
    /// <summary>Who recorded it: "desktop" (the user's play), "workshop", "mcp" (an agent's headless session), "test".</summary>
    public string Source { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime LastUsedUtc { get; set; }
    /// <summary>The frame number of the first frame (0 for a recording, the parent's frame for a branch).</summary>
    public long StartFrame { get; set; }
    /// <summary>True when the first frame is the game's power-on (nothing ran before it).</summary>
    public bool PowerOnStart { get; set; }
    public string? ParentId { get; set; }
    public long? ParentFrame { get; set; }
    public int KeyEvery { get; set; }
    public int PageSize { get; set; }
    public TimelineCompression Compression { get; set; }
    public int Level { get; set; }
    /// <summary>The memory domains the per-frame index counts changed bytes of (id and size, in order), at <see cref="IndexPage"/>-byte pages.</summary>
    public List<TimelineDomain> Domains { get; set; } = new();
    public int IndexPage { get; set; } = 256;
    /// <summary>The watched variables (name = debugger expression), in the order the index stores their values.</summary>
    public List<string> Vars { get; set; } = new();
    public bool Coverage { get; set; }
    public bool Checks { get; set; }
    /// <summary>T4: the index counts each frame's reads and writes per 256-byte page of every place the main CPU (and its DMA) touched (<see cref="TimelineExtras.Accesses"/>).</summary>
    public bool Accesses { get; set; }
    /// <summary>A console session's state header (the parts the state depends on): the restore wraps a payload with it.</summary>
    public Dictionary<string, string>? Units { get; set; }
    public string GameId { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>recording, closed (ended by the host), stopped (ended by a failure: <see cref="StopReason"/>).</summary>
    public string Status { get; set; } = "recording";
    public string StopReason { get; set; } = "";
    public long Frames { get; set; }
    public long StoredBytes { get; set; }
    public long RawBytes { get; set; }
    /// <summary>The restore-equals-record samples checked while recording, and how many failed (any failure stops the recording).</summary>
    public long VerifiedFrames { get; set; }
    public long VerifyFailures { get; set; }
    /// <summary>Marked keep by the user: never retired by the cap.</summary>
    public bool Keep { get; set; }

    [JsonIgnore] public long EndFrame => StartFrame + Frames;

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new FixedWidthUtc() } };

    /// <summary>
    /// Times written at a fixed width (7 fraction digits, always): System.Text.Json's own form drops trailing zeros, so every rewrite of
    /// <c>lastUsedUtc</c> (each jump, replay or profile touches it) moved the description's size by a byte or two, and the game folder's
    /// measured size with it (the pstore check's 138285 vs 138286). Reads any ISO form (older descriptions).
    /// </summary>
    private sealed class FixedWidthUtc : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
            DateTime.Parse(r.GetString() ?? "", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        public override void Write(Utf8JsonWriter w, DateTime v, JsonSerializerOptions o) =>
            w.WriteStringValue((v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Save(string dir)
    {
        string tmp = Path.Combine(dir, "timeline.json.tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, Path.Combine(dir, "timeline.json"), true);
    }

    public static TimelineMeta Load(string dir)
    {
        string f = Path.Combine(dir, "timeline.json");
        if (!File.Exists(f)) throw new FileNotFoundException($"no timeline in {dir} (timeline.json is missing)");
        var m = JsonSerializer.Deserialize<TimelineMeta>(File.ReadAllText(f), Json) ?? throw new InvalidDataException($"{f} is empty");
        if (m.Format != FormatName) throw new InvalidDataException($"{f} is not a BrokenNes timeline (format '{m.Format}')");
        if (m.Version > CurrentVersion) throw new InvalidDataException($"{f} was written by a newer timeline format (version {m.Version}; this build reads {CurrentVersion})");
        return m;
    }
}

public sealed class TimelineDomain { public string Id { get; set; } = ""; public int Size { get; set; } }

/// <summary>One frame of the per-frame index (<c>index.tfx</c>, fixed rows, uncompressed for fast scans). The state is the one at the start of
/// the frame; the input is what the frame was given; the picture and RAM hashes (D5's) are after it ran.</summary>
public struct TimelineRow
{
    public const int Size = 48;
    public ushort P1, P2;
    public uint Flags;
    public ulong Picture, Ram, State;
    public long ExtraOffset;
    public int ExtraLength;
    /// <summary>The frame's record before compression (bytes): what the frame cost.</summary>
    public int RecordBytes;
    public const uint FlagKey = 1;
    public bool IsKey => (Flags & FlagKey) != 0;

    public void Write(Span<byte> b)
    {
        BitConverter.TryWriteBytes(b[0..], P1); BitConverter.TryWriteBytes(b[2..], P2); BitConverter.TryWriteBytes(b[4..], Flags);
        BitConverter.TryWriteBytes(b[8..], Picture); BitConverter.TryWriteBytes(b[16..], Ram); BitConverter.TryWriteBytes(b[24..], State);
        BitConverter.TryWriteBytes(b[32..], ExtraOffset); BitConverter.TryWriteBytes(b[40..], ExtraLength); BitConverter.TryWriteBytes(b[44..], RecordBytes);
    }

    public static TimelineRow Read(ReadOnlySpan<byte> b) => new()
    {
        P1 = BitConverter.ToUInt16(b[0..]), P2 = BitConverter.ToUInt16(b[2..]), Flags = BitConverter.ToUInt32(b[4..]),
        Picture = BitConverter.ToUInt64(b[8..]), Ram = BitConverter.ToUInt64(b[16..]), State = BitConverter.ToUInt64(b[24..]),
        ExtraOffset = BitConverter.ToInt64(b[32..]), ExtraLength = BitConverter.ToInt32(b[40..]), RecordBytes = BitConverter.ToInt32(b[44..]),
    };
}

/// <summary>The variable part of a frame's index (<c>extra.tfx</c>): what changed in each domain, the watched variables, the hardware-check
/// findings of the frame, the code the main CPU ran in it (the D9 coverage of the frame, as ranges of ROM offsets).</summary>
public sealed class TimelineExtras
{
    /// <summary>Per domain (the meta's order): the bytes that differ from the frame before, and the <see cref="TimelineMeta.IndexPage"/>-byte pages they are in.</summary>
    public List<(int ChangedBytes, int[] Pages)> Domains = new();
    public long[] Vars = Array.Empty<long>();
    /// <summary>The findings first seen in this frame ("rule at where").</summary>
    public List<string> Findings = new();
    /// <summary>ROM offsets the main CPU ran this frame, as (start, length) ranges; empty when coverage is off.</summary>
    public List<(uint Start, uint Length)> Coverage = new();
    /// <summary>
    /// T4: the frame's memory accesses by the main CPU and its DMA, counted per 256-byte page of each canonical place they touched (the
    /// debugger's spaces: a NES write to $0850 counts in the CPU space's page 0 and nowhere else; one to $6010 in the CPU space and the PRG
    /// RAM's), sorted by space then page. Null when the timeline was recorded without them (<see cref="TimelineMeta.Accesses"/>).
    /// </summary>
    public List<AccessPage>? Accesses;

    /// <summary>One page's accesses in a frame.</summary>
    public readonly record struct AccessPage(int Space, uint Page, int Reads, int Writes);

    private const int TagAccesses = 1;

    public byte[] Encode()
    {
        using var ms = new MemoryStream(64);
        TimelineCodec.WriteVar(ms, (ulong)Domains.Count);
        foreach (var (changed, pages) in Domains)
        {
            TimelineCodec.WriteVar(ms, (ulong)changed);
            TimelineCodec.WriteVar(ms, (ulong)pages.Length);
            int last = 0; foreach (int p in pages) { TimelineCodec.WriteVar(ms, (ulong)(p - last)); last = p; }
        }
        TimelineCodec.WriteVar(ms, (ulong)Vars.Length);
        foreach (long v in Vars) TimelineCodec.WriteZig(ms, v);
        TimelineCodec.WriteVar(ms, (ulong)Findings.Count);
        foreach (var f in Findings) TimelineCodec.WriteString(ms, f);
        if (Coverage.Count == 0) TimelineCodec.WriteVar(ms, 0);
        else
        {
            // sparse and compressed: the ranges as deltas, deflated (a frame runs a few thousand instructions in a few dozen runs)
            using var raw = new MemoryStream();
            uint last = 0;
            TimelineCodec.WriteVar(raw, (ulong)Coverage.Count);
            foreach (var (s, l) in Coverage) { TimelineCodec.WriteVar(raw, s - last); TimelineCodec.WriteVar(raw, l); last = s + l; }
            using var z = new MemoryStream();
            using (var d = new DeflateStream(z, CompressionLevel.Fastest, true)) d.Write(raw.GetBuffer(), 0, (int)raw.Length);
            TimelineCodec.WriteVar(ms, (ulong)z.Length);
            ms.Write(z.GetBuffer(), 0, (int)z.Length);
        }
        // T4's tagged trailer (a T1-T3 record ends before it, and a reader of those stops there): the access counts
        if (Accesses != null)
        {
            TimelineCodec.WriteVar(ms, TagAccesses);
            TimelineCodec.WriteVar(ms, (ulong)Accesses.Count);
            int ls = 0; uint lp = 0;
            foreach (var a in Accesses)
            {
                if (a.Space != ls) lp = 0;
                TimelineCodec.WriteVar(ms, (ulong)(a.Space - ls)); TimelineCodec.WriteVar(ms, a.Page - lp);
                TimelineCodec.WriteVar(ms, (ulong)a.Reads); TimelineCodec.WriteVar(ms, (ulong)a.Writes);
                ls = a.Space; lp = a.Page;
            }
        }
        return ms.ToArray();
    }

    public static TimelineExtras Decode(ReadOnlySpan<byte> b)
    {
        var e = new TimelineExtras();
        int at = 0;
        int nd = (int)TimelineCodec.ReadVar(b, ref at);
        for (int i = 0; i < nd; i++)
        {
            int changed = (int)TimelineCodec.ReadVar(b, ref at), np = (int)TimelineCodec.ReadVar(b, ref at);
            var pages = new int[np]; int last = 0;
            for (int k = 0; k < np; k++) { last += (int)TimelineCodec.ReadVar(b, ref at); pages[k] = last; }
            e.Domains.Add((changed, pages));
        }
        int nv = (int)TimelineCodec.ReadVar(b, ref at);
        e.Vars = new long[nv];
        for (int i = 0; i < nv; i++) e.Vars[i] = TimelineCodec.ReadZig(b, ref at);
        int nf = (int)TimelineCodec.ReadVar(b, ref at);
        for (int i = 0; i < nf; i++) e.Findings.Add(TimelineCodec.ReadString(b, ref at));
        int zl = (int)TimelineCodec.ReadVar(b, ref at);
        if (zl > 0)
        {
            if (at + zl > b.Length) throw new InvalidDataException("a frame's index record is cut short");
            using var z = new DeflateStream(new MemoryStream(b.Slice(at, zl).ToArray()), CompressionMode.Decompress);
            using var raw = new MemoryStream(); z.CopyTo(raw);
            var r = raw.ToArray().AsSpan(); int ra = 0;
            int n = (int)TimelineCodec.ReadVar(r, ref ra); uint last = 0;
            for (int i = 0; i < n; i++) { uint s = last + (uint)TimelineCodec.ReadVar(r, ref ra); uint l = (uint)TimelineCodec.ReadVar(r, ref ra); e.Coverage.Add((s, l)); last = s + l; }
            at += zl;
        }
        while (at < b.Length)
        {
            int tag = (int)TimelineCodec.ReadVar(b, ref at);
            if (tag != TagAccesses) throw new InvalidDataException($"a frame's index record has an unknown part {tag} (written by a newer build)");
            int n = (int)TimelineCodec.ReadVar(b, ref at);
            var list = new List<AccessPage>(n);
            int ls = 0; uint lp = 0;
            for (int i = 0; i < n; i++)
            {
                int ds = (int)TimelineCodec.ReadVar(b, ref at);
                if (ds != 0) lp = 0;
                int sp = ls + ds; uint pg = lp + (uint)TimelineCodec.ReadVar(b, ref at);
                int rd = (int)TimelineCodec.ReadVar(b, ref at), wr = (int)TimelineCodec.ReadVar(b, ref at);
                list.Add(new AccessPage(sp, pg, rd, wr));
                ls = sp; lp = pg;
            }
            e.Accesses = list;
        }
        return e;
    }
}

/// <summary>One row of <c>blocks.tfi</c>: where a block is in <c>frames.tfb</c> and what it holds.</summary>
public struct TimelineBlock
{
    public const int Size = 40;
    public long Offset;
    public int Packed, Raw;
    public uint Crc;
    public long FirstFrame;
    public int Count;
    public ulong RawHash;

    public void Write(Span<byte> b)
    {
        BitConverter.TryWriteBytes(b[0..], Offset); BitConverter.TryWriteBytes(b[8..], Packed); BitConverter.TryWriteBytes(b[12..], Raw);
        BitConverter.TryWriteBytes(b[16..], Crc); BitConverter.TryWriteBytes(b[20..], Count); BitConverter.TryWriteBytes(b[24..], FirstFrame); BitConverter.TryWriteBytes(b[32..], RawHash);
    }

    public static TimelineBlock Read(ReadOnlySpan<byte> b) => new()
    {
        Offset = BitConverter.ToInt64(b[0..]), Packed = BitConverter.ToInt32(b[8..]), Raw = BitConverter.ToInt32(b[12..]),
        Crc = BitConverter.ToUInt32(b[16..]), Count = BitConverter.ToInt32(b[20..]), FirstFrame = BitConverter.ToInt64(b[24..]), RawHash = BitConverter.ToUInt64(b[32..]),
    };
}

/// <summary>
/// Writes a timeline (single-threaded: the recorder's worker calls it). Frames are appended one by one; every <see cref="TimelineMeta.KeyEvery"/>
/// frames the block is compressed and written with its index rows, so the files on disk always hold whole blocks only (a crash loses at most
/// the block being filled). After each block a sample of its frames is decoded back from the compressed bytes and its hash compared with the
/// recorded state's (restore equals record): a mismatch throws, and the recorder stops with the reason.
/// </summary>
public sealed class TimelineWriter : IDisposable
{
    public string Dir { get; }
    public TimelineMeta Meta { get; }
    private readonly FileStream frames, blocks, index, extra;
    private readonly MemoryStream block = new(1 << 20);
    private readonly List<TimelineRow> rows = new();
    private readonly List<byte[]> extras = new();
    private TimeflowState? prev;
    private readonly byte[] scratch;
    private long extraAt;
    private readonly Random sampler = new(12345);
    /// <summary>Every this many blocks, every frame of the block is checked (the others: the last frame and one at random).</summary>
    public int FullCheckEvery { get; set; } = 8;
    public long BlocksWritten { get; private set; }
    /// <summary>The tests' fault: changes a block's compressed bytes before the restore-equals-record check (given the raw block), as a damaged encoder would.</summary>
    internal static Func<byte[], byte[], TimelineMeta, byte[]>? TamperForTest = null;
    public double CompressMs { get; private set; }
    public double VerifyMs { get; private set; }

    public TimelineWriter(string dir, TimelineMeta meta)
    {
        Dir = dir; Meta = meta;
        Directory.CreateDirectory(dir);
        if (File.Exists(Path.Combine(dir, "frames.tfb"))) throw new IOException($"{dir} already holds a timeline");
        frames = new FileStream(Path.Combine(dir, "frames.tfb"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        blocks = new FileStream(Path.Combine(dir, "blocks.tfi"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        index = new FileStream(Path.Combine(dir, "index.tfx"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        extra = new FileStream(Path.Combine(dir, "extra.tfx"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        scratch = new byte[Math.Max(16, meta.PageSize)];
        meta.Status = "recording";
        meta.Save(dir);
    }

    /// <summary>The bytes this writer has written (its four files and the description), what the cap counts while the files are open.</summary>
    public long BytesWritten => frames.Position + blocks.Position + index.Position + extra.Position + 2048;

    /// <summary>Frames appended (written or waiting in the block being filled).</summary>
    public long Appended => Meta.Frames + rows.Count;

    /// <summary>Appends a frame: its state (at the frame's start), the state's hash, its index row and extras.</summary>
    public void Append(TimeflowState st, ulong stateHash, TimelineRow row, TimelineExtras ex)
    {
        long before = block.Length;
        bool key = rows.Count == 0 || prev == null || !st.SameLayout(prev);
        if (key) TimelineCodec.WriteKey(block, st);
        else TimelineCodec.WriteDelta(block, st, prev!, Meta.PageSize, scratch);
        row.State = stateHash;
        row.Flags = key ? row.Flags | TimelineRow.FlagKey : row.Flags & ~TimelineRow.FlagKey;
        row.RecordBytes = (int)(block.Length - before);
        rows.Add(row);
        extras.Add(ex.Encode());
        prev = st;
        if (rows.Count >= Meta.KeyEvery) Flush();
    }

    /// <summary>Writes the block being filled (a partial block at the end).</summary>
    public void Flush()
    {
        if (rows.Count == 0) return;
        var raw = block.GetBuffer().AsSpan(0, (int)block.Length);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        byte[] packed = TimelineCodec.Compress(raw, Meta.Compression, Meta.Level);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        CompressMs += (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (TamperForTest != null) packed = TamperForTest(packed, raw.ToArray(), Meta);
        // restore equals record: decode the block back from the compressed bytes and compare the states' hashes (a sample of the frames)
        Verify(packed, raw.Length);
        VerifyMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var b = new TimelineBlock { Offset = frames.Position, Packed = packed.Length, Raw = raw.Length, Crc = TimelineCodec.Crc32(packed), FirstFrame = Meta.StartFrame + Meta.Frames, Count = rows.Count, RawHash = TimeflowHash.Of(raw) };
        frames.Write(packed);
        Span<byte> br = stackalloc byte[TimelineBlock.Size]; b.Write(br); blocks.Write(br);
        Span<byte> rr = stackalloc byte[TimelineRow.Size];
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            r.ExtraOffset = extraAt; r.ExtraLength = extras[i].Length;
            extra.Write(extras[i]); extraAt += extras[i].Length;
            r.Write(rr); index.Write(rr);
        }
        frames.Flush(); blocks.Flush(); index.Flush(); extra.Flush();
        Meta.Frames += rows.Count;
        Meta.StoredBytes += packed.Length;
        Meta.RawBytes += raw.Length;
        rows.Clear(); extras.Clear(); block.SetLength(0);
        BlocksWritten++;
        Meta.Save(Dir);
    }

    private void Verify(byte[] packed, int rawLength)
    {
        bool all = FullCheckEvery > 0 && BlocksWritten % FullCheckEvery == 0;
        int pick = sampler.Next(rows.Count);
        long first = Meta.StartFrame + Meta.Frames;
        byte[] raw;
        try { raw = TimelineCodec.Decompress(packed, rawLength, Meta.Compression); }
        catch (InvalidDataException e) { Meta.VerifyFailures++; throw new InvalidDataException($"restore does not equal record in the block of frames {first}-{first + rows.Count - 1}: {e.Message}"); }
        var st = new TimeflowState();
        int at = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            try { TimelineCodec.Read(raw, ref at, st, Meta.PageSize); }
            catch (InvalidDataException e) { Meta.VerifyFailures++; throw new InvalidDataException($"restore does not equal record at frame {first + i}: the block does not decode ({e.Message})"); }
            if (!all && i != pick && i != rows.Count - 1) continue;
            Meta.VerifiedFrames++;
            ulong h = st.Hash();
            if (h != rows[i].State)
            {
                Meta.VerifyFailures++;
                throw new InvalidDataException($"restore does not equal record at frame {Meta.StartFrame + Meta.Frames + i}: the state decoded from the block hashes {h:x16}, the recorded state {rows[i].State:x16}");
            }
        }
        if (at != raw.Length) throw new InvalidDataException($"a block decodes {at} of its {raw.Length} bytes");
    }

    /// <summary>Ends the timeline: the last block is written and the description says how it ended.</summary>
    public void Close(string status, string reason = "")
    {
        try { if (status != "stopped") Flush(); }
        finally
        {
            Meta.Status = status; Meta.StopReason = reason;
            Meta.Save(Dir);
            Dispose();
        }
    }

    private bool disposed;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        frames.Dispose(); blocks.Dispose(); index.Dispose(); extra.Dispose();
    }
}

/// <summary>
/// Reads a timeline: its description, rows, extras, and the state of any frame (the block decoded from its keyframe, the deltas applied up to
/// the frame, the result's hash compared with the recorded one: a damaged block or delta is refused with the reason, never returned).
/// </summary>
public sealed class TimelineReader : IDisposable
{
    public string Dir { get; }
    public TimelineMeta Meta { get; }
    private readonly List<TimelineBlock> blocks = new();
    private readonly byte[] indexBytes;
    private readonly FileStream frames, extra;
    private int cachedBlock = -1;
    private byte[]? cachedRaw;
    // the decoder's place in the cached block: the frame it holds now and the byte after its record
    private int cachedAt; private long cachedFrame = -1; private TimeflowState? cachedState;

    public TimelineReader(string dir)
    {
        Dir = dir;
        Meta = TimelineMeta.Load(dir);
        byte[] bt = ReadShared(Path.Combine(dir, "blocks.tfi"));
        for (int i = 0; i + TimelineBlock.Size <= bt.Length; i += TimelineBlock.Size) blocks.Add(TimelineBlock.Read(bt.AsSpan(i)));
        indexBytes = ReadShared(Path.Combine(dir, "index.tfx"));
        frames = new FileStream(Path.Combine(dir, "frames.tfb"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        extra = new FileStream(Path.Combine(dir, "extra.tfx"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // the frames that are whole on disk: those of the written blocks (a timeline being recorded grows; this reader sees it as it was)
        Frames = Math.Min(blocks.Sum(b => (long)b.Count), indexBytes.Length / TimelineRow.Size);
    }

    private static byte[] ReadShared(string f)
    {
        using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var b = new byte[s.Length]; s.ReadExactly(b); return b;
    }

    public long Frames { get; }
    public long StartFrame => Meta.StartFrame;
    public long EndFrame => Meta.StartFrame + Frames;
    public IReadOnlyList<TimelineBlock> Blocks => blocks;

    public bool Has(long frame) => frame >= StartFrame && frame < EndFrame;

    private long Local(long frame)
    {
        if (!Has(frame)) throw new ArgumentOutOfRangeException(nameof(frame), $"frame {frame} is not in timeline {Meta.Id} (frames {StartFrame} to {EndFrame - 1})");
        return frame - StartFrame;
    }

    public TimelineRow Row(long frame) => TimelineRow.Read(indexBytes.AsSpan((int)(Local(frame) * TimelineRow.Size), TimelineRow.Size));

    public TimelineExtras Extras(long frame)
    {
        var r = Row(frame);
        var b = new byte[r.ExtraLength];
        lock (extra) { extra.Position = r.ExtraOffset; extra.ReadExactly(b); }
        return TimelineExtras.Decode(b);
    }

    public (PadButtons P1, PadButtons P2) Pads(long frame) { var r = Row(frame); return ((PadButtons)r.P1, (PadButtons)r.P2); }

    private byte[]? extraAll;

    /// <summary>A frame's extras from the whole extras file read once (a scan over every frame: one read, not one seek a frame).</summary>
    public TimelineExtras ExtrasScan(long frame)
    {
        var r = Row(frame);
        if (extraAll == null || r.ExtraOffset + r.ExtraLength > extraAll.Length)
            lock (extra) { extraAll = new byte[extra.Length]; extra.Position = 0; extra.ReadExactly(extraAll); }
        if (r.ExtraOffset + r.ExtraLength > extraAll.Length) throw new InvalidDataException($"timeline {Meta.Id}: frame {frame}'s index record is past the end of extra.tfx");
        return TimelineExtras.Decode(extraAll.AsSpan((int)r.ExtraOffset, r.ExtraLength));
    }

    /// <summary>
    /// The state at the start of a frame, like <see cref="State"/> (its hash checked against the recorded one) but without a copy: the returned
    /// object is the reader's own and changes at the next call (a scan reads it and moves on).
    /// </summary>
    public TimeflowState StateView(long frame)
    {
        State(frame, copy: false, out var st);
        return st;
    }

    /// <summary>The block that holds a frame (the blocks are in order; all but the last hold <see cref="TimelineMeta.KeyEvery"/> frames).</summary>
    private int BlockOf(long local)
    {
        long f = StartFrame + local;
        int lo = 0, hi = blocks.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (blocks[mid].FirstFrame <= f) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// The state at the start of <paramref name="frame"/>: the nearest keyframe before it (its block's first frame) and the deltas up to it.
    /// The result's hash is the recorded one, or this throws <see cref="InvalidDataException"/> saying where the timeline is damaged.
    /// </summary>
    public TimeflowState State(long frame) { State(frame, copy: true, out var st); return st; }

    private void State(long frame, bool copy, out TimeflowState result)
    {
        long local = Local(frame);
        int bi = BlockOf(local);
        var b = blocks[bi];
        if (cachedBlock != bi || cachedRaw == null)
        {
            var packed = new byte[b.Packed];
            lock (frames) { frames.Position = b.Offset; frames.ReadExactly(packed); }
            uint crc = TimelineCodec.Crc32(packed);
            if (crc != b.Crc) throw new InvalidDataException($"timeline {Meta.Id}: the block of frames {b.FirstFrame}-{b.FirstFrame + b.Count - 1} is damaged (its checksum is {crc:x8}, recorded {b.Crc:x8})");
            var raw = TimelineCodec.Decompress(packed, b.Raw, Meta.Compression);
            if (TimeflowHash.Of(raw) != b.RawHash) throw new InvalidDataException($"timeline {Meta.Id}: the block of frames {b.FirstFrame}-{b.FirstFrame + b.Count - 1} decodes to other bytes than were recorded");
            cachedBlock = bi; cachedRaw = raw; cachedFrame = -1; cachedState = null; cachedAt = 0;
        }
        if (cachedState == null || cachedFrame > frame) { cachedState = new TimeflowState(); cachedAt = 0; cachedFrame = b.FirstFrame - 1; }
        while (cachedFrame < frame)
        {
            TimelineCodec.Read(cachedRaw, ref cachedAt, cachedState, Meta.PageSize);
            cachedFrame++;
        }
        var row = Row(frame);
        ulong h = cachedState.Hash();
        if (h != row.State)
        {
            var bad = cachedFrame; cachedState = null;
            throw new InvalidDataException($"timeline {Meta.Id}: frame {bad} restored from its keyframe (frame {b.FirstFrame}) and deltas hashes {h:x16}, but the recorded state hashes {row.State:x16}: a delta is damaged");
        }
        result = copy ? cachedState.Clone() : cachedState;
    }

    public void Dispose() { frames.Dispose(); extra.Dispose(); }
}
