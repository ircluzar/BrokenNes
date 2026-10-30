using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Timeflow;

/// <summary>A kept profile's description (<c>profile.json</c>).</summary>
public sealed class ProfileMeta
{
    public const string FormatName = "bnprofile";
    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Timeline { get; set; } = "";
    public string TimelineDir { get; set; } = "";
    public string Console { get; set; } = "";
    public string RomName { get; set; } = "";
    public string RomSha256 { get; set; } = "";
    public string Cores { get; set; } = "";
    public long From { get; set; }
    public long To { get; set; }
    public long StackKnownFrom { get; set; }
    public long FramesStored { get; set; }
    public long LastFrameStored { get; set; } = -1;
    /// <summary>writing, complete, truncated (the size limit or the folder's cap stopped the store: <see cref="Reason"/>), off (out of sync), cancelled.</summary>
    public string Status { get; set; } = "writing";
    public string Reason { get; set; } = "";
    public long? OffFrame { get; set; }
    public string OffWhat { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime LastUsedUtc { get; set; }
    /// <summary>When the sweep deletes it: the last use plus its time to live (a kept profile's TTL is the longer keep TTL).</summary>
    public DateTime ExpiresUtc { get; set; }
    public int TtlMinutes { get; set; }
    /// <summary>Survives the end of the session that made it (still deleted when it expires).</summary>
    public bool Keep { get; set; }
    public int OwnerPid { get; set; }
    public long OwnerStartTicks { get; set; }
    public string OwnerName { get; set; } = "";
    public long LimitBytes { get; set; }
    public long Bytes { get; set; }
    public long RawBytes { get; set; }
    public long Instructions { get; set; }
    public long Accesses { get; set; }
    public int Quality { get; set; }
    public List<string> Cpus { get; set; } = new();
    public List<ProfileVarInfo> Vars { get; set; } = new();
    public List<string> Symbols { get; set; } = new();
    public string Note { get; set; } = "";

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Save(string dir)
    {
        string tmp = Path.Combine(dir, "profile.json.tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, Path.Combine(dir, "profile.json"), true);
    }

    public static ProfileMeta Load(string dir)
    {
        var m = JsonSerializer.Deserialize<ProfileMeta>(File.ReadAllText(Path.Combine(dir, "profile.json")), Json) ?? throw new InvalidDataException($"{dir}: an empty profile.json");
        if (m.Format != FormatName) throw new InvalidDataException($"{dir}: not a BrokenNes profile ({m.Format})");
        return m;
    }
}

public sealed record ProfileInfo(string Dir, ProfileMeta Meta, long Bytes);

/// <summary>How a kept profile is stored.</summary>
public sealed class ProfileStoreOptions
{
    /// <summary>This profile's own size limit (512 MB): past it, the frames after are not stored (said in its status), the streaming queries still see them.</summary>
    public long LimitBytes = ProfileStore.DefaultLimitBytes;
    /// <summary>Minutes after its last use that a profile is deleted (60).</summary>
    public int TtlMinutes = ProfileStore.DefaultTtlMinutes;
    public bool Keep;
    /// <summary>Brotli quality of each frame's records.</summary>
    public int Quality = ProfileStore.DefaultQuality;
    public List<string> Symbols = new();
    public string Note = "";
    /// <summary>The profiles folder's cap for this write (else timeflow.json's profilesCapBytes, else 2 GB).</summary>
    public long? CapBytes;
}

/// <summary>
/// The temporary store of kept profiles (T3): <c>&lt;game folder&gt;\profiles\&lt;id&gt;\</c> (beside <c>timelines\</c>, outside the game's cap, with
/// its own limits): <c>profile.json</c>, <c>records.bin</c> (each frame's records, Brotli), <c>frames.idx</c>. A profile is deleted
/// <list type="bullet">
/// <item>when the session that made it ends (<see cref="EndSession"/>: the process exits, the MCP server stops, the desktop app closes) unless it is kept;</item>
/// <item>at the next sweep when the process that made it is gone (a crash or a kill) and it is not kept;</item>
/// <item>at the next sweep when its time to live has passed since its last use (kept or not);</item>
/// <item>oldest first, when the profiles folder is over its cap (2 GB by default) and it is not kept.</item>
/// </list>
/// A sweep runs before every profile is made, when profiles are listed, and when a tool starts (Workshop's profile verbs, the MCP server, the
/// desktop app). Nothing else is written: a span profiled for streaming queries alone writes no file at all.
/// </summary>
public static class ProfileStore
{
    public const long DefaultLimitBytes = 512L * 1024 * 1024;
    public const long DefaultCapBytes = 2L * 1024 * 1024 * 1024;
    public const int DefaultTtlMinutes = 60;
    public const int DefaultKeepTtlMinutes = 7 * 24 * 60;
    public const int DefaultQuality = 4;
    public const string Folder = "profiles";

    private static readonly List<string> mine = new();
    private static readonly object sync = new();
    private static bool hooked;

    public static string ProfilesDir(string gameDir) => Path.Combine(gameDir, Folder);

    /// <summary>"512 MB", "300 KB", "2.0 GB".</summary>
    public static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : $"{b / 1024.0:0} KB";

    public static string NewId() => "p" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();

    private static (int pid, long start, string name) Me()
    {
        using var p = Process.GetCurrentProcess();
        return (p.Id, p.StartTime.ToUniversalTime().Ticks, p.ProcessName);
    }

    /// <summary>True while the process that made a profile still runs (the same PID started at the same time).</summary>
    public static bool OwnerAlive(ProfileMeta m)
    {
        try
        {
            using var p = Process.GetProcessById(m.OwnerPid);
            if (p.HasExited) return false;
            long start;
            try { start = p.StartTime.ToUniversalTime().Ticks; } catch (Exception) { return true; }   // a process we may not inspect: assume it is the owner
            return Math.Abs(start - m.OwnerStartTicks) < TimeSpan.TicksPerSecond * 2;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public static long CapOf(string gameDir)
    {
        string f = Path.Combine(gameDir, "timeflow.json");
        try
        {
            if (File.Exists(f))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("profilesCapBytes", out var c) && c.TryGetInt64(out long v) && v > 0) return v;
            }
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return DefaultCapBytes;
    }

    /// <summary>Starts a kept profile of a timeline (sweeping first): its writer is a sink of the re-run.</summary>
    public static ProfileWriter Create(TimelineMeta tl, string timelineDir, long from, long to, ProfileStoreOptions o)
    {
        string gameDir = TimeflowStore.GameDirOf(timelineDir);
        Sweep(gameDir);
        string root = ProfilesDir(gameDir);
        Directory.CreateDirectory(root);
        string id = NewId();
        string dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        var (pid, start, name) = Me();
        int ttl = o.Keep ? Math.Max(o.TtlMinutes, DefaultKeepTtlMinutes) : Math.Max(1, o.TtlMinutes);
        var m = new ProfileMeta
        {
            Id = id, Timeline = tl.Id, TimelineDir = Path.GetFullPath(timelineDir), Console = tl.Console, RomName = tl.RomName, RomSha256 = tl.RomSha256, Cores = tl.Cores,
            From = from, To = to, CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow, TtlMinutes = ttl, ExpiresUtc = DateTime.UtcNow.AddMinutes(ttl), Keep = o.Keep,
            OwnerPid = pid, OwnerStartTicks = start, OwnerName = name, LimitBytes = o.LimitBytes, Quality = o.Quality, Symbols = o.Symbols.Select(Path.GetFullPath).ToList(), Note = o.Note,
        };
        m.Save(dir);
        lock (sync)
        {
            mine.Add(dir);
            if (!hooked) { hooked = true; AppDomain.CurrentDomain.ProcessExit += (_, _) => EndSession(); }
        }
        long cap = o.CapBytes ?? CapOf(gameDir);
        return new ProfileWriter(dir, m, cap, root);
    }

    /// <summary>Every kept profile (of one game folder, or all).</summary>
    public static List<ProfileInfo> List(string? gameDir = null)
    {
        var l = new List<ProfileInfo>();
        IEnumerable<string> games;
        try { games = gameDir != null ? new[] { gameDir } : TimeflowStore.GameDirs(); }
        catch (InvalidOperationException) { return l; }
        foreach (string g in games)
        {
            string root = ProfilesDir(g);
            if (!Directory.Exists(root)) continue;
            foreach (string d in Directory.GetDirectories(root))
            {
                try { l.Add(new ProfileInfo(d, ProfileMeta.Load(d), TimeflowStore.DirBytes(d))); }
                catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { }
            }
        }
        return l.OrderBy(p => p.Meta.CreatedUtc).ToList();
    }

    /// <summary>A kept profile by id or folder.</summary>
    public static string? Find(string idOrDir)
    {
        if (Directory.Exists(idOrDir) && File.Exists(Path.Combine(idOrDir, "profile.json"))) return Path.GetFullPath(idOrDir);
        try
        {
            foreach (string g in TimeflowStore.GameDirs())
            {
                string d = Path.Combine(ProfilesDir(g), idOrDir);
                if (File.Exists(Path.Combine(d, "profile.json"))) return d;
            }
        }
        catch (InvalidOperationException) { }
        return null;
    }

    /// <summary>What a sweep deleted, and why.</summary>
    public sealed record Swept(string Id, string Why, long Bytes);

    /// <summary>
    /// Deletes what must go: expired profiles, unkept ones whose process is gone (a crash, a kill), broken folders (no description, older than
    /// 10 minutes), and the oldest unkept ones while the folder is over its cap. Never a profile its own live process is still writing, unless it
    /// expired.
    /// </summary>
    public static List<Swept> Sweep(string? gameDir = null, bool all = false)
    {
        var done = new List<Swept>();
        IEnumerable<string> games;
        try { games = gameDir != null ? new[] { gameDir } : TimeflowStore.GameDirs(); }
        catch (InvalidOperationException) { return done; }
        foreach (string g in games.ToList())
        {
            string root = ProfilesDir(g);
            if (!Directory.Exists(root)) continue;
            var live = new List<ProfileInfo>();
            foreach (string d in Directory.GetDirectories(root))
            {
                ProfileMeta? m = null;
                try { m = ProfileMeta.Load(d); }
                catch (Exception e) when (e is IOException or InvalidDataException or JsonException)
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > TimeSpan.FromMinutes(10) || all) { long b = TimeflowStore.DirBytes(d); if (Remove(d)) done.Add(new Swept(Path.GetFileName(d), "no readable profile.json", b)); }
                    continue;
                }
                string? why = all ? "dropped (all)" : DateTime.UtcNow > m.ExpiresUtc ? $"expired (time to live {m.TtlMinutes} min after its last use)" :
                              !m.Keep && !OwnerAlive(m) ? $"its session ended without removing it (process {m.OwnerName} {m.OwnerPid} is gone: a crash or a kill)" : null;
                long bytes = TimeflowStore.DirBytes(d);
                if (why != null) { if (Remove(d)) done.Add(new Swept(m.Id, why, bytes)); continue; }
                live.Add(new ProfileInfo(d, m, bytes));
            }
            long cap = CapOf(g), used = live.Sum(p => p.Bytes);
            foreach (var p in live.Where(p => !p.Meta.Keep && p.Meta.Status != "writing").OrderBy(p => p.Meta.LastUsedUtc))
            {
                if (used <= cap) break;
                if (Remove(p.Dir)) { done.Add(new Swept(p.Meta.Id, $"the profiles folder was over its cap ({ProfileStore.Size(cap)}): the least recently used unkept one goes first", p.Bytes)); used -= p.Bytes; }
            }
            try { if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); } catch (IOException) { }
        }
        return done;
    }

    private static bool Remove(string dir)
    {
        for (int i = 0; i < 3; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); return true; }
            catch (IOException) { System.Threading.Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(50); }
        }
        return !Directory.Exists(dir);
    }

    /// <summary>Deletes one profile now.</summary>
    public static bool Delete(string dir) { lock (sync) mine.Remove(dir); string? root = Path.GetDirectoryName(dir); bool ok = Remove(dir); TidyRoot(root); return ok; }

    private static void TidyRoot(string? root) { try { if (root != null && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); } catch (IOException) { } }

    /// <summary>Keeps a profile past its session (and gives it the keep TTL), or lets it go with the session again.</summary>
    public static ProfileMeta SetKeep(string dir, bool keep, int? ttlMinutes = null)
    {
        var m = ProfileMeta.Load(dir);
        m.Keep = keep;
        m.TtlMinutes = ttlMinutes ?? (keep ? DefaultKeepTtlMinutes : DefaultTtlMinutes);
        if (!keep) { var (pid, start, name) = Me(); m.OwnerPid = pid; m.OwnerStartTicks = start; m.OwnerName = name; lock (sync) if (!mine.Contains(dir)) mine.Add(dir); }
        m.LastUsedUtc = DateTime.UtcNow; m.ExpiresUtc = DateTime.UtcNow.AddMinutes(m.TtlMinutes);
        m.Save(dir);
        return m;
    }

    /// <summary>A use (a query, the desktop's scrubbing): the time to live counts from now.</summary>
    public static void Touch(string dir)
    {
        try { var m = ProfileMeta.Load(dir); m.LastUsedUtc = DateTime.UtcNow; m.ExpiresUtc = DateTime.UtcNow.AddMinutes(m.TtlMinutes); m.Save(dir); }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { }
    }

    /// <summary>The profiles this process made and has not deleted.</summary>
    public static IReadOnlyList<string> Mine { get { lock (sync) return mine.Where(Directory.Exists).ToArray(); } }

    /// <summary>The session ends: every profile this process made and did not keep is deleted. Returns how many.</summary>
    public static int EndSession()
    {
        List<string> list;
        lock (sync) { list = mine.ToList(); mine.Clear(); }
        int n = 0;
        foreach (string d in list)
        {
            try
            {
                if (!Directory.Exists(d)) continue;
                bool keep = false;
                try { keep = ProfileMeta.Load(d).Keep; } catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { }
                if (keep) continue;
                string? root = Path.GetDirectoryName(d);
                if (Remove(d)) n++;
                TidyRoot(root);
            }
            catch (Exception) { }
        }
        return n;
    }
}

/// <summary>Writes a profile's records, a frame at a time (a sink of the re-run).</summary>
public sealed class ProfileWriter : ProfileSink, IDisposable
{
    public string Dir { get; }
    public ProfileMeta Meta { get; }
    private readonly long cap;
    private readonly string root;
    private readonly FileStream records, index;
    private readonly MemoryStream buf = new(1 << 20);
    private readonly ProfileCodec.Writer w;
    private long curFrame = -1, frameSeq;
    private int frameInstr;
    private bool stopped, closed;
    private long othersBytes;
    private int framesSinceCapCheck;
    private ProfileContext? ctx;

    internal ProfileWriter(string dir, ProfileMeta meta, long cap, string root)
    {
        Dir = dir; Meta = meta; this.cap = cap; this.root = root;
        records = new FileStream(Path.Combine(dir, "records.bin"), FileMode.Create, FileAccess.Write, FileShare.Read);
        index = new FileStream(Path.Combine(dir, "frames.idx"), FileMode.Create, FileAccess.Write, FileShare.Read);
        w = new ProfileCodec.Writer(buf);
        othersBytes = TimeflowStore.DirBytes(root) - TimeflowStore.DirBytes(dir);
    }

    public bool Stopped => stopped;
    public long BytesWritten => records.Position + index.Position;

    public override void Begin(ProfileContext c)
    {
        ctx = c;
        Meta.StackKnownFrom = c.StackKnownFrom;
        Meta.Cpus = c.Machine.Cpus.Select(x => x.Id).ToList();
        Meta.Vars = c.Vars.ToList();
        Meta.Save(Dir);
    }

    public override void OnFrame(in ProfFrame f)
    {
        FlushFrame();
        if (stopped) return;
        curFrame = f.Frame; frameSeq = f.Seq; frameInstr = 0;
        w.Reset();
        w.Frame(in f);
    }

    public override void OnStack(int cpu, IReadOnlyList<ProfCall> frames) { if (!stopped && curFrame >= 0) w.Stack(cpu, frames); }
    public override void OnInstr(in ProfInstr i) { if (stopped || curFrame < 0) return; w.Instr(in i); frameInstr++; Meta.Instructions++; }
    public override void OnAccess(in ProfAccess a) { if (stopped || curFrame < 0) return; w.Access(in a); Meta.Accesses++; }
    public override void OnCall(in ProfCall c) { if (!stopped && curFrame >= 0) w.Call(in c); }
    public override void OnVar(in ProfVar v) { if (!stopped && curFrame >= 0) w.Var(in v); }
    public override void OnMarker(in ProfMarker m) { if (!stopped && curFrame >= 0) w.Marker(in m); }

    private void FlushFrame()
    {
        if (curFrame < 0 || stopped) return;
        var raw = buf.GetBuffer().AsSpan(0, (int)buf.Length);
        var packed = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        if (!BrotliEncoder.TryCompress(raw, packed, out int n, Meta.Quality, 22)) throw new IOException("Brotli could not compress a profile frame");
        if (BytesWritten + n + 64 > Meta.LimitBytes)
        {
            Stop($"the profile reached its size limit ({ProfileStore.Size(Meta.LimitBytes)}) at frame {curFrame}: frames {curFrame}-{Meta.To} are not stored (the streaming queries of the re-run still saw them)");
            return;
        }
        if (++framesSinceCapCheck >= 16) { framesSinceCapCheck = 0; othersBytes = TimeflowStore.DirBytes(root) - TimeflowStore.DirBytes(Dir); }
        if (othersBytes + BytesWritten + n > cap)
        {
            ProfileStore.Sweep(Path.GetDirectoryName(root)!);
            othersBytes = TimeflowStore.DirBytes(root) - TimeflowStore.DirBytes(Dir);
            if (othersBytes + BytesWritten + n > cap)
            {
                Stop($"the profiles folder reached its cap ({ProfileStore.Size(cap)}, the other profiles in it kept) at frame {curFrame}: frames {curFrame}-{Meta.To} are not stored");
                return;
            }
        }
        long off = records.Position;
        records.Write(packed, 0, n);
        Span<byte> row = stackalloc byte[ProfileCodec.IndexRow];
        BitConverter.TryWriteBytes(row[0..8], curFrame);
        BitConverter.TryWriteBytes(row[8..16], off);
        BitConverter.TryWriteBytes(row[16..20], n);
        BitConverter.TryWriteBytes(row[20..24], raw.Length);
        BitConverter.TryWriteBytes(row[24..28], ProfileCodec.Crc32(packed.AsSpan(0, n)));
        BitConverter.TryWriteBytes(row[28..36], frameSeq);
        BitConverter.TryWriteBytes(row[36..40], frameInstr);
        index.Write(row);
        Meta.FramesStored++; Meta.LastFrameStored = curFrame; Meta.RawBytes += raw.Length; Meta.Bytes = BytesWritten;
        curFrame = -1;
    }

    private void Stop(string why) { stopped = true; Meta.Status = "truncated"; Meta.Reason = why; curFrame = -1; }

    public override void End() => Close(null);

    /// <summary>Finishes the files (the last frame, the description).</summary>
    public void Close(ProfileReport? r)
    {
        if (closed) { if (r != null) Finish(r); return; }
        FlushFrame();
        closed = true;
        records.Flush(); index.Flush();
        records.Dispose(); index.Dispose();
        Meta.Bytes = TimeflowStore.DirBytes(Dir);
        if (Meta.Status == "writing") Meta.Status = "complete";
        if (ctx?.OffFrame is long off) { Meta.OffFrame = off; Meta.OffWhat = ctx.OffWhat; if (Meta.Status == "complete") Meta.Status = "off"; }
        if (r != null) Finish(r);
        Meta.Save(Dir);
    }

    private void Finish(ProfileReport r)
    {
        if (r.Cancelled && Meta.Status == "complete") { Meta.Status = "cancelled"; Meta.Reason = $"cancelled after frame {Meta.LastFrameStored}"; }
        if (r.OffFrame is long off) { Meta.OffFrame = off; Meta.OffWhat = r.OffWhat; if (Meta.Status == "complete") Meta.Status = "off"; }
        if (r.StoppedEarly && Meta.Status == "complete") Meta.Reason = $"the queries needed frames {Meta.From}-{Meta.LastFrameStored} only";
        Meta.Save(Dir);
    }

    public void Dispose() { if (!closed) Close(null); }
}

/// <summary>Reads a kept profile back into sinks (the same calls the re-run made).</summary>
public sealed class ProfileReader
{
    public string Dir { get; }
    public ProfileMeta Meta { get; }
    private readonly (long Frame, long Offset, int Packed, int Raw, uint Crc, long Seq, int Instr)[] rows;

    public ProfileReader(string dir)
    {
        Dir = dir;
        Meta = ProfileMeta.Load(dir);
        var idx = File.ReadAllBytes(Path.Combine(dir, "frames.idx"));
        rows = new (long, long, int, int, uint, long, int)[idx.Length / ProfileCodec.IndexRow];
        for (int i = 0; i < rows.Length; i++)
        {
            var b = idx.AsSpan(i * ProfileCodec.IndexRow, ProfileCodec.IndexRow);
            rows[i] = (BitConverter.ToInt64(b[0..8]), BitConverter.ToInt64(b[8..16]), BitConverter.ToInt32(b[16..20]), BitConverter.ToInt32(b[20..24]), BitConverter.ToUInt32(b[24..28]), BitConverter.ToInt64(b[28..36]), BitConverter.ToInt32(b[36..40]));
        }
    }

    public IReadOnlyList<long> Frames => rows.Select(r => r.Frame).ToArray();
    public long FirstFrame => rows.Length > 0 ? rows[0].Frame : -1;
    public long LastFrame => rows.Length > 0 ? rows[^1].Frame : -1;
    public long FirstSeq(long frame) { foreach (var r in rows) if (r.Frame == frame) return r.Seq; return -1; }
    public int InstructionsOf(long frame) { foreach (var r in rows) if (r.Frame == frame) return r.Instr; return 0; }
    /// <summary>The frame holding instruction #seq, or -1.</summary>
    public long FrameOfSeq(long seq)
    {
        long f = -1;
        foreach (var r in rows) { if (r.Seq > seq) break; f = r.Frame; }
        return f;
    }

    /// <summary>Feeds the stored frames from..to (both included) to the sinks; a damaged frame is refused with where it is.</summary>
    public void Feed(IReadOnlyList<ProfileSink> sinks, ProfileContext ctx, long from = long.MinValue, long to = long.MaxValue)
    {
        foreach (var s in sinks) s.Begin(ctx);
        var cpus = ctx.Machine.Cpus.Count;
        using var fs = new FileStream(Path.Combine(Dir, "records.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long last = sinks.Count == 0 ? long.MaxValue : sinks.Max(s => s.LastFrameNeeded);
        foreach (var r in rows)
        {
            if (r.Frame < from) continue;
            if (r.Frame > to || r.Frame > last) break;
            var packed = new byte[r.Packed];
            fs.Seek(r.Offset, SeekOrigin.Begin);
            fs.ReadExactly(packed);
            if (ProfileCodec.Crc32(packed) != r.Crc) throw new InvalidDataException($"profile {Meta.Id}: frame {r.Frame}'s records are damaged (checksum)");
            var raw = new byte[r.Raw];
            if (!BrotliDecoder.TryDecompress(packed, raw, out int got) || got != r.Raw) throw new InvalidDataException($"profile {Meta.Id}: frame {r.Frame}'s records do not decompress");
            ProfileCodec.Read(raw, sinks, cpus);
            if (sinks.Count > 0 && sinks.All(s => s.Done)) break;
        }
        foreach (var s in sinks) s.End();
        ProfileStore.Touch(Dir);
    }
}

/// <summary>The record encoding of a stored profile frame: a tag byte, then varints (zigzag where signed).</summary>
internal static class ProfileCodec
{
    public const int IndexRow = 40;
    private const byte TFrame = 1, TInstr = 2, TAccess = 3, TCall = 4, TReturn = 5, TVar = 6, TMarker = 7, TStack = 9;

    internal sealed class Writer
    {
        private readonly MemoryStream s;
        private readonly long[] lastCycle = new long[8];
        public Writer(MemoryStream s) { this.s = s; }
        public void Reset() { s.SetLength(0); Array.Clear(lastCycle); }

        private void B(byte b) => s.WriteByte(b);
        private void U(ulong v) { while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; } s.WriteByte((byte)v); }
        private void Z(long v) => U((ulong)((v << 1) ^ (v >> 63)));
        private void Str(string t) { var b = Encoding.UTF8.GetBytes(t ?? ""); U((ulong)b.Length); s.Write(b); }
        private void Key(SpaceKey k) { B((byte)k.Space); U(k.Offset); }

        public void Frame(in ProfFrame f) { B(TFrame); Z(f.Frame); Z(f.Seq); U((ushort)f.P1); U((ushort)f.P2); }

        public void Stack(int cpu, IReadOnlyList<ProfCall> frames)
        {
            B(TStack); B((byte)cpu); U((ulong)frames.Count);
            foreach (var c in frames) { Str(c.Kind); U(c.CallerPc); U(c.Target); U(c.ReturnPc); Z(c.Frame); }
        }

        public void Instr(in ProfInstr i)
        {
            B(TInstr); B(i.Cpu); B(i.R.Kind);
            int c = i.Cpu & 7;
            Z(i.R.Cycle - lastCycle[c]); lastCycle[c] = i.R.Cycle;
            Z(i.R.Scanline); Z(i.R.Dot); U(i.R.Pc);
            B(i.R.Length);
            int len = Math.Min(4, Math.Max(1, (int)i.R.Length));
            for (int k = 0; k < len; k++) B(i.R.Byte(k));
            U(i.R.R0); U(i.R.R1); U(i.R.R2); U(i.R.R3); U(i.R.R4); B(i.R.P); B(i.R.R5); B(i.R.R6);
            Z(i.R.Ea); U(i.R.EaValue);
            B(i.NKeys);
            if (i.NKeys > 0) Key(i.K0); if (i.NKeys > 1) Key(i.K1); if (i.NKeys > 2) Key(i.K2); if (i.NKeys > 3) Key(i.K3);
        }

        public void Access(in ProfAccess a)
        {
            B(TAccess); B(a.Cpu); B((byte)((a.Write ? 1 : 0) | (int)a.Source << 1)); B((byte)(a.Channel + 1));
            U(a.Address); B(a.Value); Z(a.Scanline); Z(a.Dot);
            B(a.NKeys);
            if (a.NKeys > 0) Key(a.K0); if (a.NKeys > 1) Key(a.K1); if (a.NKeys > 2) Key(a.K2); if (a.NKeys > 3) Key(a.K3);
        }

        public void Call(in ProfCall c) { B(c.Return ? TReturn : TCall); B(c.Cpu); Str(c.Kind); U(c.CallerPc); U(c.Target); U(c.ReturnPc); }
        public void Var(in ProfVar v) { B(TVar); U((ulong)v.Index); Z(v.Byte); Z(v.Old); Z(v.New); Z(v.Instr); U(v.Pc); B(v.Cpu); Z(v.Cycle); }
        public void Marker(in ProfMarker m) { B(TMarker); Str(m.Text); U(m.Pc); }
    }

    /// <summary>Decodes a frame's records and calls the sinks in order.</summary>
    public static void Read(byte[] raw, IReadOnlyList<ProfileSink> sinks, int ncpu)
    {
        int p = 0;
        byte B() => raw[p++];
        ulong U() { ulong v = 0; int sh = 0; while (true) { byte b = raw[p++]; v |= (ulong)(b & 0x7F) << sh; if (b < 0x80) return v; sh += 7; } }
        long Z() { ulong u = U(); return (long)(u >> 1) ^ -(long)(u & 1); }
        string Str() { int n = (int)U(); string t = Encoding.UTF8.GetString(raw, p, n); p += n; return t; }
        SpaceKey Key() { int sp = B(); return new SpaceKey(sp, (uint)U()); }
        long frame = 0, seq = 0;
        var lastCycle = new long[8];
        var cpuSeq = Enumerable.Repeat(-1L, Math.Max(ncpu, 8)).ToArray();
        var cpuPc = new uint[Math.Max(ncpu, 8)];
        var cpuCycle = new long[Math.Max(ncpu, 8)];
        int sc = sinks.Count;
        while (p < raw.Length)
        {
            byte tag = B();
            switch (tag)
            {
                case TFrame:
                {
                    var f = new ProfFrame { Frame = Z(), Seq = Z(), P1 = (PadButtons)U(), P2 = (PadButtons)U() };
                    frame = f.Frame; seq = f.Seq;
                    for (int s = 0; s < sc; s++) sinks[s].OnFrame(in f);
                    break;
                }
                case TStack:
                {
                    int cpu = B(); int n = (int)U();
                    var l = new List<ProfCall>(n);
                    for (int k = 0; k < n; k++) l.Add(new ProfCall { Seq = seq, Cpu = (byte)cpu, Kind = Str(), CallerPc = (uint)U(), Target = (uint)U(), ReturnPc = (uint)U(), Frame = Z() });
                    for (int s = 0; s < sc; s++) sinks[s].OnStack(cpu, l);
                    break;
                }
                case TInstr:
                {
                    var i = new ProfInstr { Seq = seq++, Cpu = B(), Frame = frame };
                    i.R.Kind = B();
                    int c = i.Cpu & 7;
                    i.R.Cycle = lastCycle[c] + Z(); lastCycle[c] = i.R.Cycle;
                    i.R.Frame = frame;
                    i.R.Scanline = (short)Z(); i.R.Dot = (short)Z(); i.R.Pc = (uint)U();
                    i.R.Length = B();
                    int len = Math.Min(4, Math.Max(1, (int)i.R.Length));
                    uint bytes = 0; for (int k = 0; k < len; k++) bytes |= (uint)B() << (8 * k);
                    i.R.Bytes = bytes;
                    i.R.R0 = (ushort)U(); i.R.R1 = (ushort)U(); i.R.R2 = (ushort)U(); i.R.R3 = (ushort)U(); i.R.R4 = (ushort)U(); i.R.P = B(); i.R.R5 = B(); i.R.R6 = B();
                    i.R.Ea = (int)Z(); i.R.EaValue = (ushort)U();
                    i.NKeys = B();
                    if (i.NKeys > 0) i.K0 = Key(); if (i.NKeys > 1) i.K1 = Key(); if (i.NKeys > 2) i.K2 = Key(); if (i.NKeys > 3) i.K3 = Key();
                    cpuSeq[i.Cpu] = i.Seq; cpuPc[i.Cpu] = i.R.Pc; cpuCycle[i.Cpu] = i.R.Cycle;
                    for (int s = 0; s < sc; s++) sinks[s].OnInstr(in i);
                    break;
                }
                case TAccess:
                {
                    var a = new ProfAccess { Cpu = B(), Frame = frame };
                    byte fl = B(); a.Write = (fl & 1) != 0; a.Source = (AccessSource)(fl >> 1); a.Channel = (sbyte)(B() - 1);
                    a.Address = (uint)U(); a.Value = B(); a.Scanline = (short)Z(); a.Dot = (short)Z();
                    a.NKeys = B();
                    if (a.NKeys > 0) a.K0 = Key(); if (a.NKeys > 1) a.K1 = Key(); if (a.NKeys > 2) a.K2 = Key(); if (a.NKeys > 3) a.K3 = Key();
                    a.Instr = cpuSeq[a.Cpu]; a.Pc = cpuPc[a.Cpu]; a.Cycle = cpuCycle[a.Cpu];
                    for (int s = 0; s < sc; s++) sinks[s].OnAccess(in a);
                    break;
                }
                case TCall or TReturn:
                {
                    var c = new ProfCall { Seq = seq, Cpu = B(), Frame = frame, Return = tag == TReturn, Kind = Str(), CallerPc = (uint)U(), Target = (uint)U(), ReturnPc = (uint)U() };
                    for (int s = 0; s < sc; s++) sinks[s].OnCall(in c);
                    break;
                }
                case TVar:
                {
                    var v = new ProfVar { Index = (int)U(), Byte = (int)Z(), Old = Z(), New = Z(), Instr = Z(), Pc = (uint)U(), Cpu = B(), Cycle = Z(), Frame = frame };
                    for (int s = 0; s < sc; s++) sinks[s].OnVar(in v);
                    break;
                }
                case TMarker:
                {
                    var m = new ProfMarker { Seq = seq, Frame = frame, Text = Str(), Pc = (uint)U() };
                    for (int s = 0; s < sc; s++) sinks[s].OnMarker(in m);
                    break;
                }
                default: throw new InvalidDataException($"a profile record with tag {tag} at byte {p - 1}");
            }
        }
    }

    private static readonly uint[] crcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
