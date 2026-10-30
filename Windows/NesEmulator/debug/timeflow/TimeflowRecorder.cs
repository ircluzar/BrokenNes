using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NesEmulator.Corruption;
using NesEmulator.Debugging.Checks;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Timeflow;

/// <summary>How a recording is made (the defaults are T1's measured choice: docs/projects/brokendev-timeflow-plan.md, "T1 as built").</summary>
public sealed class TimeflowOptions
{
    /// <summary>T1's choice from the measurements: a keyframe (and a block) every 60 frames, 1024-byte pages, Brotli quality 5.</summary>
    public const int DefaultKeyEvery = 60, DefaultPageSize = 1024, DefaultLevel = 5;
    public const TimelineCompression DefaultCompression = TimelineCompression.Brotli;

    /// <summary>"CPU_FIX/PPU_FIX/APU_FIX" (the NES getters' ids) as the ids the cores are set by ("FIX/FIX/FIX"); other consoles' ids as they are.</summary>
    public static string NormalizeCores(string cores) => string.Join("/", (cores ?? "").Split('/').Select(c =>
    {
        foreach (var pre in new[] { "CPU_", "PPU_", "APU_" }) if (c.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return c[pre.Length..];
        return c;
    }));

    public int KeyEvery = DefaultKeyEvery;
    public int PageSize = DefaultPageSize;
    public TimelineCompression Compression = DefaultCompression;
    public int Level = DefaultLevel;
    public byte[] Rom = Array.Empty<byte>();
    public string RomName = "";
    public string Cores = "";
    public string PowerOn = "fceux";
    public bool Ntsc;
    /// <summary>The game folder (null: <see cref="TimeflowStore.GameDir"/> from the ROM's sha256).</summary>
    public string? GameDir;
    public string Source = "";
    public string Note = "";
    public long StartFrame;
    public bool PowerOnStart;
    public string? ParentId;
    public long? ParentFrame;
    /// <summary>The watched variables (names, the expressions in <see cref="Vars"/>' order) and how to read them after each frame.</summary>
    public List<string> VarNames = new();
    public Func<long[]>? Vars;
    /// <summary>The per-frame coverage (D9's map, by frame), when the debugger is attached and coverage is asked for.</summary>
    public FrameCoverage? Coverage;
    /// <summary>The hardware checks (D4), when they are on: the findings first seen in a frame go into its index row.</summary>
    public ConsoleChecks? Checks;
    /// <summary>The cap in bytes (null: the game folder's own, 5 GB by default).</summary>
    public long? CapBytes;
    /// <summary>Every this many blocks the whole block is checked frame by frame (the others: two frames); 1 checks every frame.</summary>
    public int FullCheckEvery = 8;
    /// <summary>Frames that may wait for the writer before the emulation waits for it (it is never dropped).</summary>
    public int QueueFrames = 180;
}

/// <summary>
/// Records a running game into a timeline (milestone T1): the host calls <see cref="BeforeFrame"/> with the pads the frame is about to get
/// (the state is taken there: between frames) and <see cref="AfterFrame"/> after it ran (the picture, RAM and variables). The emulation thread
/// only takes the state and copies the memory domains; a worker thread finds the changed pages, compresses the blocks, checks restore equals
/// record on samples, keeps the cap and writes the files. A recorder exists only while the recording is on: off, nothing of it runs and
/// nothing is written. A failure (a mismatch, a full disk, a machine that refuses a state, the cap reached by this timeline alone, the cores
/// swapped) stops it with <see cref="StopReason"/>: never silently.
/// </summary>
public sealed class TimeflowRecorder : IDisposable
{
    private sealed class Item
    {
        public TimeflowState State = null!;
        public PadButtons P1, P2;
        public ulong Picture, Ram;
        public byte[][] Domains = Array.Empty<byte[]>();
        public long[] Vars = Array.Empty<long>();
        public List<string> Findings = new();
        public List<(uint, uint)> Coverage = new();
    }

    private readonly TimeflowSource src;
    private readonly TimeflowOptions opt;
    private readonly TimelineWriter writer;
    private readonly BlockingCollection<Item> queue;
    private readonly Thread worker;
    private readonly IMemoryDomain[] domains;
    private readonly ConcurrentBag<byte[]>[] pools;
    private byte[][]? prevDomains;
    private Item? pending;
    private readonly HashSet<string> findingsSeen = new();
    private string coresAtStart;
    private volatile string? stopReason;
    private long framesCaptured;
    private double captureMs, maxCaptureMs;

    public string Id => writer.Meta.Id;
    public string Dir => writer.Dir;
    public string GameDir { get; }
    public TimelineMeta Meta => writer.Meta;
    public string ConsoleKey => src.ConsoleKey;
    /// <summary>Recording (false once stopped or failed).</summary>
    public bool Running => stopReason == null && !closed;
    /// <summary>Why the recording stopped by itself, or null.</summary>
    public string? StopReason => stopReason;
    public long FramesCaptured => Interlocked.Read(ref framesCaptured);
    public long FramesWritten => writer.Meta.Frames;
    public long StoredBytes => writer.Meta.StoredBytes;
    public long RawBytes => writer.Meta.RawBytes;
    /// <summary>The game folder's size (timelines + ROMs), updated after each block.</summary>
    public long GameBytes { get; private set; }
    public long CapBytes { get; }
    public List<string> Retired { get; } = new();
    public double CaptureMsAverage => framesCaptured > 0 ? captureMs / framesCaptured : 0;
    public double CaptureMsMax => maxCaptureMs;
    public double CompressMs => writer.CompressMs;
    public double VerifyMs => writer.VerifyMs;
    /// <summary>Called once (on the worker or the emulation thread) when the recording stops by itself.</summary>
    public event Action<string>? Stopped;

    private TimeflowRecorder(TimeflowSource src, TimeflowOptions opt, TimelineWriter writer, string gameDir)
    {
        this.src = src; this.opt = opt; this.writer = writer; GameDir = gameDir;
        domains = src.Domains.Where(d => d.Unavailable == null && d.Kind != DomainKind.Rom && d.Size > 0).ToArray();
        pools = domains.Select(_ => new ConcurrentBag<byte[]>()).ToArray();
        queue = new BlockingCollection<Item>(Math.Max(4, opt.QueueFrames));
        coresAtStart = SafeCores();
        CapBytes = opt.CapBytes ?? TimeflowStore.CapOf(gameDir);
        GameBytes = TimeflowStore.GameBytes(gameDir);
        worker = new Thread(Work) { IsBackground = true, Name = "Timeflow writer", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    private string SafeCores() { try { return src.Cores(); } catch (Exception e) { return "?" + e.Message; } }

    /// <summary>
    /// Starts a recording: the ROM is kept once in the game's folder, a timeline folder made, the worker started. Throws (with the reason) when
    /// the machine has no states, or when the game's folder is already over its cap with nothing left to retire.
    /// </summary>
    public static TimeflowRecorder Start(TimeflowSource src, TimeflowOptions opt)
    {
        if (src.NoStateWhy.Length > 0) throw new NotSupportedException($"Timeflow cannot record this machine: {src.NoStateWhy}");
        if (opt.Rom.Length == 0) throw new ArgumentException("Timeflow needs the ROM the game was started from");
        string sha = TimeflowStore.Sha256(opt.Rom);
        string project = "";
        string gameDir = opt.GameDir ?? TimeflowStore.GameDir(sha, opt.RomName, out project);
        var retired = TimeflowStore.Enforce(gameDir, null, out long size, opt.CapBytes);
        long cap = opt.CapBytes ?? TimeflowStore.CapOf(gameDir);
        if (size >= cap) throw new IOException($"the game's Timeflow folder {gameDir} is at its cap ({Gb(size)} of {Gb(cap)}) and every timeline in it is marked keep: nothing can be recorded until one is unmarked or the cap is raised");
        string romPath = TimeflowStore.StoreRom(gameDir, opt.Rom, opt.RomName);
        // the first state must be taken now, so a machine that refuses one is said before a folder is made
        var first = src.Capture();
        string id = TimeflowStore.NewId();
        var meta = new TimelineMeta
        {
            Id = id, Console = src.ConsoleKey, RomName = opt.RomName, RomSha256 = sha, RomSize = opt.Rom.Length, RomExt = Path.GetExtension(romPath),
            Cores = TimeflowOptions.NormalizeCores(opt.Cores), PowerOn = opt.PowerOn, Ntsc = opt.Ntsc, Project = project, Source = opt.Source, Note = opt.Note,
            CreatedUtc = DateTime.UtcNow, LastUsedUtc = DateTime.UtcNow, StartFrame = opt.StartFrame, PowerOnStart = opt.PowerOnStart,
            ParentId = opt.ParentId, ParentFrame = opt.ParentFrame, KeyEvery = Math.Max(1, opt.KeyEvery), PageSize = Math.Max(16, opt.PageSize),
            Compression = opt.Compression, Level = opt.Level, Vars = opt.VarNames.ToList(), Coverage = opt.Coverage != null, Checks = opt.Checks != null,
        };
        if (src.Header is { } h) { meta.Units = new Dictionary<string, string>(h.Units); meta.GameId = h.GameId; meta.Title = h.Title; }
        foreach (var d in src.Domains.Where(d => d.Unavailable == null && d.Kind != DomainKind.Rom && d.Size > 0)) meta.Domains.Add(new TimelineDomain { Id = d.Id, Size = d.Size });
        var w = new TimelineWriter(Path.Combine(TimeflowStore.TimelinesDir(gameDir), id), meta) { FullCheckEvery = opt.FullCheckEvery };
        var r = new TimeflowRecorder(src, opt, w, gameDir);
        r.Retired.AddRange(retired);
        r.firstState = first;
        return r;
    }

    private TimeflowState? firstState;

    private static string Gb(long b) => $"{b / (1024.0 * 1024 * 1024):0.00} GB";

    /// <summary>Before a frame (emulation thread, between frames): takes the state and notes the pads the frame gets.</summary>
    public void BeforeFrame(PadButtons p1, PadButtons p2)
    {
        if (!Running) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeflowState st;
        try
        {
            if (firstState != null) { st = firstState; firstState = null; }
            else
            {
                if (framesCaptured > 0 && framesCaptured % Math.Max(1, opt.KeyEvery) == 0)
                {
                    // at each keyframe: the machine still runs the cores it started with (a swap means the frames before cannot replay into the ones after)
                    string c = SafeCores();
                    if (c != coresAtStart) { Fail($"the machine changed ({coresAtStart} to {c}): a timeline holds one machine; switch recording off and on to start a new one"); return; }
                }
                st = src.Capture();
            }
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or InvalidDataException)
        {
            Fail($"the machine refused a state at frame {opt.StartFrame + framesCaptured}: {e.Message}");
            return;
        }
        pending = new Item { State = st, P1 = p1, P2 = p2 };
        Ms(t0);
    }

    /// <summary>After the frame ran (emulation thread): the picture and RAM hashes, the domains' bytes, the variables, the frame's coverage and findings.</summary>
    public void AfterFrame()
    {
        var it = pending;
        pending = null;
        if (it == null || !Running) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        it.Picture = src.PictureHash();
        it.Ram = src.RamHash();
        it.Domains = new byte[domains.Length][];
        for (int i = 0; i < domains.Length; i++)
        {
            var d = domains[i];
            if (!pools[i].TryTake(out var buf) || buf.Length != d.Size) buf = new byte[d.Size];
            Copy(d, buf);
            it.Domains[i] = buf;
        }
        if (opt.Vars != null) { try { it.Vars = opt.Vars(); } catch (Exception e) { Fail($"a watched variable could not be read: {e.Message}"); return; } }
        if (opt.Coverage != null) { opt.Coverage.TakeFrame(it.Coverage); }
        if (opt.Checks != null)
            foreach (var f in opt.Checks.Findings)
                if (findingsSeen.Add(f.Key)) it.Findings.Add($"{f.Rule} at {f.Place}");
        Ms(t0, count: true);
        // never dropped: when the writer is behind, the emulation waits (the cost shows in the frame rate, and is measured)
        while (Running)
        {
            try { if (queue.TryAdd(it, 50)) return; }
            catch (InvalidOperationException) { return; }
        }
    }

    private void Ms(long t0, bool count = false)
    {
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (count) { Interlocked.Increment(ref framesCaptured); lastMs += ms; captureMs += lastMs; if (lastMs > maxCaptureMs) maxCaptureMs = lastMs; lastMs = 0; }
        else lastMs = ms;
    }
    private double lastMs;

    private static void Copy(IMemoryDomain d, byte[] into)
    {
        switch (d)
        {
            case ArrayDomain a: a.CopyTo(into); return;
            case WordDomain w: w.CopyTo(into); return;
            default: for (int i = 0; i < into.Length; i++) into[i] = d.Peek(i); return;
        }
    }

    private void Fail(string reason)
    {
        if (Interlocked.CompareExchange(ref stopReason, reason, null) != null) return;
        try { queue.CompleteAdding(); } catch (ObjectDisposedException) { }
        try { Stopped?.Invoke(reason); } catch { }
    }

    private void Work()
    {
        try
        {
            foreach (var it in queue.GetConsumingEnumerable())
            {
                if (stopReason != null) break;
                var row = new TimelineRow { P1 = (ushort)it.P1, P2 = (ushort)it.P2, Picture = it.Picture, Ram = it.Ram };
                var ex = new TimelineExtras { Vars = it.Vars, Findings = it.Findings, Coverage = it.Coverage };
                int ip = writer.Meta.IndexPage;
                for (int i = 0; i < it.Domains.Length; i++)
                {
                    var cur = it.Domains[i];
                    var old = prevDomains?[i];
                    if (old == null || old.Length != cur.Length) { ex.Domains.Add((0, Array.Empty<int>())); continue; }
                    int changed = 0; var pages = new List<int>();
                    for (int p = 0, o = 0; o < cur.Length; p++, o += ip)
                    {
                        int len = Math.Min(ip, cur.Length - o);
                        var a = cur.AsSpan(o, len); var b = old.AsSpan(o, len);
                        if (a.SequenceEqual(b)) continue;
                        pages.Add(p);
                        for (int k = 0; k < len; k++) if (a[k] != b[k]) changed++;
                    }
                    ex.Domains.Add((changed, pages.ToArray()));
                }
                if (prevDomains != null) for (int i = 0; i < prevDomains.Length; i++) if (prevDomains[i] != null) pools[i].Add(prevDomains[i]);
                prevDomains = it.Domains;
                long blocksBefore = writer.BlocksWritten;
                writer.Append(it.State, it.State.Hash(), row, ex);
                if (writer.BlocksWritten != blocksBefore) AfterBlock();
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            Fail(e is IOException ? $"the timeline could not be written: {e.Message}" : e.Message);
        }
        catch (Exception e)
        {
            Fail($"the timeline writer failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private void AfterBlock()
    {
        var retired = TimeflowStore.Enforce(GameDir, Id, out long size, opt.CapBytes, Dir, writer.BytesWritten);
        Retired.AddRange(retired);
        GameBytes = size;
        if (size > CapBytes) Fail($"the game's Timeflow folder reached its cap ({Gb(size)} of {Gb(CapBytes)}) with nothing older to retire: the recording stops here (frames {writer.Meta.StartFrame}-{writer.Meta.EndFrame - 1} are kept)");
    }

    private bool closed;

    /// <summary>Ends the recording: every frame given is written (the worker drains), the timeline closed. Returns the timeline's folder.</summary>
    public string Stop()
    {
        if (closed) return Dir;
        closed = true;
        pending = null;
        try { queue.CompleteAdding(); } catch (ObjectDisposedException) { }
        worker.Join();
        try
        {
            if (stopReason != null) writer.Close("stopped", stopReason);
            else writer.Close("closed");
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            stopReason ??= e.Message;
            try { writer.Meta.Status = "stopped"; writer.Meta.StopReason = stopReason; writer.Meta.Save(Dir); } catch (IOException) { }
        }
        GameBytes = TimeflowStore.GameBytes(GameDir);
        return Dir;
    }

    public void Dispose() { Stop(); queue.Dispose(); }
}

/// <summary>
/// The code the main CPU ran in each frame (the D9 coverage map, by frame, for the timeline's index): an observer on the D1 debugger that marks
/// each instruction's ROM offset, and hands the frame's ranges over at its end. Only while a recording asks for it (it arms the main CPU's
/// step hook, which costs speed).
/// </summary>
public sealed class FrameCoverage : DebugObserver
{
    private readonly int romSpace = -1;
    private readonly SpaceKey[] keys = new SpaceKey[8];
    private readonly bool[] ran;
    private readonly List<uint> touched = new();
    private readonly Debugger debugger;

    public FrameCoverage(Debugger debugger)
    {
        this.debugger = debugger;
        var cpu = debugger.Machine.Cpus[0];
        var rom = cpu.Spaces.FirstOrDefault(s => s.Is("rom"));
        if (rom != null) romSpace = rom.Id;
        ran = new bool[rom?.Size ?? 0];
    }

    public static FrameCoverage Attach(Debugger d) { var c = new FrameCoverage(d); d.AddObserver(c); return c; }
    public void Detach() => debugger.RemoveObserver(this);

    public override bool WantsSteps(int cpuIndex) => cpuIndex == 0 && romSpace >= 0;

    public override void OnStep(DebugCpu c, long cycle, bool executes, string? interrupt)
    {
        if (!executes || c.Index != 0) return;
        uint pc = c.PC;
        int n = c.AccessKeys(pc, false, keys);
        for (int i = 0; i < n; i++)
        {
            if (keys[i].Space != romSpace) continue;
            uint off = keys[i].Offset;
            if (off >= (uint)ran.Length) return;
            uint bytes = (uint)(c.Peek(pc) | c.Peek(c.NextAddress(pc, 1)) << 8);
            int len = Math.Max(1, c.Classify(bytes).Length);
            for (int k = 0; k < len && off + k < (uint)ran.Length; k++)
                if (!ran[off + k]) { ran[off + k] = true; touched.Add(off + (uint)k); }
            return;
        }
    }

    /// <summary>The frame's ROM bytes run, as sorted (start, length) ranges; the marks are cleared for the next frame.</summary>
    public void TakeFrame(List<(uint Start, uint Length)> into)
    {
        touched.Sort();
        int i = 0;
        while (i < touched.Count)
        {
            uint s = touched[i], e = s;
            while (i + 1 < touched.Count && touched[i + 1] == e + 1) { i++; e++; }
            into.Add((s, e - s + 1));
            i++;
        }
        foreach (uint o in touched) ran[o] = false;
        touched.Clear();
    }
}
