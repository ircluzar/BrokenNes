// Round 8 follow-up (2026-10-18): a log of what FL does to BrokenNes2 instances, for the user to send back. FL could not be run where this plugin is built: when FL does something unexpected
// (the Replace of a channel did not behave as the first design assumed), this log is the evidence instead of a guess.
//
// When it runs (relink, 2026-10-18): AUTOMATICALLY for every instance of the process while a recording's tracks are on offer (TrackOffer.cs), and whenever the user ticked "Log FL's calls" in the
// Record window (process-wide, remembered as Prefs "HostLog": it is on for the instances FL creates after a restart). The user's first FL session with r12 wrote nothing because the box was
// per-choice and off; while tracks are on offer nobody has to remember to tick it now.
//
// What it records, per instance (numbered in creation order), for its first seconds: its creation (with FL's host version), every Dispatcher call FL makes (id, index, value), every parameter FL
// sets (ProcessParam), every saved or restored state (sizes, sections, what that made of the instance), whether it joined the recording, left it or was linked by hand, what FL answered when asked
// for the channel's name (FHD_GetContextInfo), the first notes (pitch, song position as FL gives it, the note's colour and length, velocity), how the instance recognised its track, how far FL moved
// each note's pitch while it sounded (whether FL kept the MIDI file's pitch bends), and the settling. The audio thread writes fixed records into a ring made once (nothing allocated); a writer
// thread formats them into the file. The file is Documents\BrokenNes2 host log.txt, kept under 1 MB: past that it is renamed "BrokenNes2 host log.old.txt" (one older part kept) and a new one begins.
//
// Tests never write the user's file: the test host names a scratch file in BROKENNES_HOSTLOG_FILE and a scratch folder standing for Documents in BROKENNES_HOSTLOG_DEFAULT_DIR, and its `hostlog`
// check fails when a run touched the real file.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using BrokenNes2.Record;

namespace BrokenNes2;

internal static class HostLog
{
    public const int PerInstance = 160;                 // records per instance (its first host calls)
    public const long MaxFileBytes = 1 << 20;
    public const string FileName = "BrokenNes2 host log.txt", OldFileName = "BrokenNes2 host log.old.txt";

    private struct Entry
    {
        public long Seq, Ticks;
        public int Inst, Kind;
        public long A, B, C;
        public double X, Y, Z;
    }

    public const int KindDispatcher = 1, KindParam = 2, KindNote = 3, KindMatch = 4, KindNoteEnd = 5;
    private static readonly Entry[] ring = new Entry[16384];
    private static long next;
    private static readonly ConcurrentQueue<(long Ticks, string Line)> text = new();
    private static volatile bool user = Prefs.GetInt("HostLog", 0) == 1;
    private static volatile bool offerActive;
    private static Thread? writer;
    private static int instances;
    private static long written;

    /// <summary>Whether the log is on (any thread): ticked by the user, tracks on offer, or a capture from FL armed or running (round 9: what FL says about time goes to the log).</summary>
    public static bool On => user || captureActive || (offerActive && !RecMutants.On(RecMutants.LogNotAutomatic));
    private static volatile bool captureActive;

    /// <summary>A capture from FL was armed (true) or ended (false): the log runs meanwhile (round 9, CaptureHub).</summary>
    public static void SetCapture(bool active)
    {
        if (active == captureActive) return;
        if (active) { captureActive = true; Text(0, "capture from FL armed: the log runs until it ends"); }
        else { Text(0, "capture from FL ended" + (user || offerActive ? "" : ": the log stops")); captureActive = false; }
    }
    /// <summary>The user's own setting ("Log FL's calls"; remembered).</summary>
    public static bool UserOn => user;
    /// <summary>On because tracks are on offer.</summary>
    public static bool Automatic => offerActive;

    /// <summary>Where it is written: Documents\BrokenNes2 host log.txt. Tests: BROKENNES_HOSTLOG_FILE names the file, BROKENNES_HOSTLOG_DEFAULT_DIR the folder that stands for Documents.</summary>
    public static string FilePath => !RecMutants.On(RecMutants.LogIgnoresTestPath) && Environment.GetEnvironmentVariable("BROKENNES_HOSTLOG_FILE") is { Length: > 0 } f ? f : Path.Combine(DefaultFolder, FileName);

    /// <summary>The folder the log goes to when no file is named: the user's Documents (tests: BROKENNES_HOSTLOG_DEFAULT_DIR).</summary>
    public static string DefaultFolder => Environment.GetEnvironmentVariable("BROKENNES_HOSTLOG_DEFAULT_DIR") is { Length: > 0 } d ? d : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>The user's box (UI thread); remembered for the next time FL starts.</summary>
    public static void Set(bool value)
    {
        if (value) { user = true; Text(0, $"log switched on by the user ({DateTime.Now:yyyy-MM-dd HH:mm:ss})"); }
        else { Text(0, "log switched off by the user" + (offerActive ? " (it stays on while tracks are on offer)" : "")); user = false; }
        Prefs.SetInt("HostLog", value ? 1 : 0);
    }

    /// <summary>Tracks went on offer, or the last ones were withdrawn (TrackOffers.Publish): the log runs for every instance while they are.</summary>
    public static void SetOffer(bool active)
    {
        if (active == offerActive) return;
        if (active) { offerActive = true; Text(0, "tracks are on offer: the log runs for every BrokenNes2 instance until they are withdrawn"); }
        else { Text(0, "no tracks on offer any more" + (user ? "" : ": the log stops (tick \"Log FL's calls\" to keep it on)")); offerActive = false; }
    }

    /// <summary>A new instance's number (1, 2, ...).</summary>
    public static int NextInstance() => Interlocked.Increment(ref instances);
    public static int InstancesMade => Volatile.Read(ref instances);

    /// <summary>A line made off the audio thread (creation, states, settling).</summary>
    public static void Text(int inst, string line)
    {
        if (!On) return;
        text.Enqueue((DateTime.UtcNow.Ticks, $"{DateTime.Now:HH:mm:ss.fff} #{inst} {line}"));
        EnsureWriter();
    }

    /// <summary>A record from any thread (the audio thread included): nothing allocated.</summary>
    public static void Add(int kind, int inst, long a, long b, long c, double x = 0, double y = 0, double z = 0)
    {
        if (!On) return;
        long i = Interlocked.Increment(ref next) - 1;
        ref var e = ref ring[i & (ring.Length - 1)];
        e.Ticks = DateTime.UtcNow.Ticks; e.Inst = inst; e.Kind = kind; e.A = a; e.B = b; e.C = c; e.X = x; e.Y = y; e.Z = z;
        Volatile.Write(ref e.Seq, i + 1);
        if (writer == null) wakeWanted = 1;
    }

    private static int wakeWanted;

    private static void EnsureWriter()
    {
        if (writer != null) return;
        lock (text)
        {
            if (writer != null) return;
            writer = new Thread(Write) { IsBackground = true, Name = "BrokenNes2 host log" };
            writer.Start();
        }
    }

    /// <summary>The audio thread cannot start a thread (it would allocate): the first text line or the UI's next paint does (<see cref="Poll"/>).</summary>
    public static void Poll() { if (On && (writer == null) && Volatile.Read(ref wakeWanted) != 0) EnsureWriter(); }

    /// <summary>Lines written to the file by this process (tests wait for the writer).</summary>
    public static long LinesWritten => Interlocked.Read(ref written);

    /// <summary>Whatever is queued is written now (tests; the writer does it every 200 ms anyway).</summary>
    public static void Flush() { lock (flushGate) WriteOnce(); }

    private static readonly object flushGate = new();
    private static long done;
    private static bool headerWritten;

    private static void Write()
    {
        while (true)
        {
            Thread.Sleep(200);
            lock (flushGate) WriteOnce();
        }
    }

    private static void WriteOnce()
    {
        var batch = new List<(long Ticks, string Line)>();
        while (text.TryDequeue(out var line)) batch.Add(line);
        long upTo = Volatile.Read(ref next);
        if (upTo - done > ring.Length) { batch.Add((0, $"({upTo - done - ring.Length} records lost: the ring is {ring.Length})")); done = upTo - ring.Length; }
        for (; done < upTo; done++)
        {
            ref var e = ref ring[done & (ring.Length - 1)];
            if (Volatile.Read(ref e.Seq) != done + 1) break;          // (still being written)
            batch.Add((e.Ticks, Format(e)));
        }
        if (batch.Count == 0) return;
        var sb = new StringBuilder();
        if (!headerWritten)
        {
            headerWritten = true;
            string exe;
            try { exe = Path.GetFileName(Environment.ProcessPath ?? "?"); } catch (Exception) { exe = "?"; }
            sb.AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} BrokenNes2 {typeof(HostLog).Assembly.GetName().Version} in {exe} (process {Environment.ProcessId}) ====");
        }
        foreach (var (_, line) in batch.OrderBy(x => x.Ticks)) sb.AppendLine(line);
        try
        {
            var path = FilePath;
            if (File.Exists(path) && new FileInfo(path).Length + sb.Length > MaxFileBytes && !RecMutants.On(RecMutants.LogNotCapped))
            {
                // over 1 MB: the file becomes the "old" part (one older part is kept), a new one begins
                string old = Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.GetFileNameWithoutExtension(path) + ".old" + Path.GetExtension(path));
                File.Move(path, old, overwrite: true);
                sb.Insert(0, $"(the log went over {MaxFileBytes / 1024} KB: the part before this line is in {Path.GetFileName(old)})" + Environment.NewLine);
            }
            File.AppendAllText(path, sb.ToString());
            Interlocked.Add(ref written, batch.Count);
        }
        catch (Exception) { /* a log must never take FL down */ }
    }

    private static string Format(in Entry e)
    {
        var t = new DateTime(e.Ticks, DateTimeKind.Utc).ToLocalTime();
        var c = CultureInfo.InvariantCulture;
        string body = e.Kind switch
        {
            KindDispatcher => $"Dispatcher id={e.A} ({DispatcherName(e.A)}) index={e.B} value={e.C}",
            KindParam => $"ProcessParam index={e.A} ({(e.A is >= 0 and < P.Count ? ParamNames[e.A] : "?")}) value={e.B} flags=0x{e.C:X}",
            KindNote => string.Create(c, $"note #{e.A}: pitch {e.X:0.##} cents from C5 (key {60 + Math.Round(e.X / 100)}), song position {Pos(e.Y)} beats (local {Pos(e.Z)}), colour {e.B >> 32}, length {(int)e.B} ticks, velocity {e.C / 1000.0:0.###}"),
            KindMatch => (e.B & 0xFFFF) == 0 ? $"match after note #{e.A}: no track yet ({e.C} aligned candidates)"
                : $"match after note #{e.A}: track {e.B & 0xFFFF} ({((e.B >> 16 & 1) != 0 ? "settled" : "provisional")}, {((e.B >> 17 & 1) != 0 ? $"unsure: {e.C} tracks have these notes" : "the only candidate")}, by {(e.B >> 18) switch { 1 => "the same key at the same song position", 2 => "a constant offset", _ => "?" }})",
            KindNoteEnd => string.Create(c, $"note #{e.A} ended: FL moved its pitch between {e.X:0.##} and {e.Y:0.##} cents while it sounded (a bend FL applies shows as a range)"),
            _ => $"kind {e.Kind} {e.A} {e.B} {e.C}",
        };
        return $"{t:HH:mm:ss.fff} #{e.Inst} {body}";

        static string Pos(double v) => double.IsNaN(v) ? "none" : v.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static readonly string[] ParamNames = ["Sound chip", "Volume", "Pan", "Coarse", "Fine", "Pulse duty", "Channel", "Noise mode", "Mode", "Emulator", "Console", "Instrument", "Voice", "Recipe"];

    /// <summary>The FPD_ name of a dispatcher id FL sends (fp_plugclass.h), for the log.</summary>
    private static string DispatcherName(long id) => id switch
    {
        0 => "ShowEditor", 1 => "ProcessMode", 2 => "Flush", 3 => "SetBlockSize", 4 => "SetSampleRate", 5 => "WindowMinMax", 6 => "KillAVoice", 7 => "UseVoiceLevels", 9 => "SetPreset",
        10 => "ChanSampleChanged", 11 => "SetEnabled", 12 => "SetPlaying", 13 => "SongPosChanged", 14 => "SetTimeSig", 15 => "CollectFile", 16 => "SetInternalParam", 17 => "SetNumSends",
        18 => "LoadFile", 19 => "SetFitTime", 20 => "SetSamplesPerTick", 21 => "SetIdleTime", 22 => "SetFocus", 23 => "Transport", 24 => "MIDIIn", 25 => "RoutingChanged", 26 => "GetParamInfo",
        27 => "ProjLoaded", 28 => "WrapperLoadState", 29 => "ShowSettings", 30 => "SetIOLatency", 32 => "PreferredNumIO", 33 => "GetGUIColor", 34 => "CloseAllWindows", 35 => "RenderWindowBitmap",
        36 => "StealKBFocus", 37 => "GetHelpContext", 38 => "RegChanged", 39 => "ArrangeWindows", 40 => "PluginLoaded", 41 => "ContextInfoChanged", 42 => "ProjectInfoChanged",
        43 => "GetDemoPlugins", 44 => "UnLockDemoPlugins", 46 => "ColorWasPicked", 47 => "IsInDebugMode", 48 => "ColorsHaveChanged", 49 => "GetStateSizeEstimate", 50 => "UseIncreasedMIDIResolution",
        51 => "ConvertStringToValue", 52 => "GetParamType", _ => "?",
    };
}
