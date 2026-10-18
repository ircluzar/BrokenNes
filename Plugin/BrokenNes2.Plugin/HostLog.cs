// Round 8 follow-up (2026-10-18): a log of what FL does to BrokenNes2 instances, switched on from the Record window, for the user to send back. FL could not be run where this plugin is built:
// when FL does something unexpected (the Replace of a channel did not behave as the first design assumed), this log is the evidence instead of a guess.
//
// What it records, per instance (numbered in creation order), for its first seconds: its creation, every Dispatcher call FL makes (id, index, value), every parameter FL sets (ProcessParam),
// every saved or restored state (sizes, whether it held anything but defaults, what that made of the instance), the first notes (pitch, song position as FL gives it in three ways, the note's
// colour and length, velocity), how the instance recognised its track, how far FL moved each note's pitch while it sounded (whether FL kept the MIDI file's pitch bends), and the settling.
// The audio thread writes fixed records into a ring made once (nothing allocated); a writer thread formats them into the file. The setting is remembered (Prefs "HostLog"), so it is on for the
// instances FL creates after a restart.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace BrokenNes2;

internal static class HostLog
{
    public const int PerInstance = 160;                 // records per instance (its first host calls)
    public const long MaxFileBytes = 4 << 20;

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
    private static volatile bool on = Prefs.GetInt("HostLog", 0) == 1;
    private static Thread? writer;
    private static int instances;

    /// <summary>Whether the log is on (any thread).</summary>
    public static bool On => on;

    /// <summary>Where it is written: Documents\BrokenNes2 host log.txt (tests: BROKENNES_HOSTLOG_FILE).</summary>
    public static string FilePath => Environment.GetEnvironmentVariable("BROKENNES_HOSTLOG_FILE") is { Length: > 0 } f ? f
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BrokenNes2 host log.txt");

    /// <summary>The log on or off (UI thread); remembered for the next time FL starts.</summary>
    public static void Set(bool value)
    {
        on = value;
        Prefs.SetInt("HostLog", value ? 1 : 0);
        if (value) Text(0, $"log switched on ({DateTime.Now:yyyy-MM-dd HH:mm:ss}); BrokenNes2 build {typeof(HostLog).Assembly.GetName().Version}");
    }

    /// <summary>A new instance's number (1, 2, ...).</summary>
    public static int NextInstance() => Interlocked.Increment(ref instances);
    public static int InstancesMade => Volatile.Read(ref instances);

    /// <summary>A line made off the audio thread (creation, states, settling).</summary>
    public static void Text(int inst, string line)
    {
        if (!on) return;
        text.Enqueue((DateTime.UtcNow.Ticks, $"{DateTime.Now:HH:mm:ss.fff} #{inst} {line}"));
        EnsureWriter();
    }

    /// <summary>A record from any thread (the audio thread included): nothing allocated.</summary>
    public static void Add(int kind, int inst, long a, long b, long c, double x = 0, double y = 0, double z = 0)
    {
        if (!on) return;
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
    public static void Poll() { if (on && (writer == null) && Volatile.Read(ref wakeWanted) != 0) EnsureWriter(); }

    private static void Write()
    {
        long done = 0;
        var sb = new StringBuilder();
        var batch = new List<(long Ticks, string Line)>();
        while (true)
        {
            Thread.Sleep(200);
            sb.Clear(); batch.Clear();
            while (text.TryDequeue(out var line)) batch.Add(line);
            long upTo = Volatile.Read(ref next);
            if (upTo - done > ring.Length) { batch.Add((0, $"({upTo - done - ring.Length} records lost: the ring is {ring.Length})")); done = upTo - ring.Length; }
            for (; done < upTo; done++)
            {
                ref var e = ref ring[done & (ring.Length - 1)];
                if (Volatile.Read(ref e.Seq) != done + 1) break;          // (still being written)
                batch.Add((e.Ticks, Format(e)));
            }
            if (batch.Count == 0) continue;
            foreach (var (_, line) in batch.OrderBy(x => x.Ticks)) sb.AppendLine(line);
            try
            {
                var path = FilePath;
                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes) continue;
                File.AppendAllText(path, sb.ToString());
            }
            catch (Exception) { /* a log must never take FL down */ }
        }
    }

    private static string Format(in Entry e)
    {
        var t = new DateTime(e.Ticks, DateTimeKind.Utc).ToLocalTime();
        var c = CultureInfo.InvariantCulture;
        string body = e.Kind switch
        {
            KindDispatcher => $"Dispatcher id={e.A} index={e.B} value={e.C}",
            KindParam => $"ProcessParam index={e.A} value={e.B} flags=0x{e.C:X}",
            KindNote => string.Create(c, $"note #{e.A}: pitch {e.X:0.##} cents from C5 (key {60 + Math.Round(e.X / 100)}), song position {Pos(e.Y)} beats (local {Pos(e.Z)}), colour {e.B >> 32}, length {(int)e.B} ticks, velocity {e.C / 1000.0:0.###}"),
            KindMatch => (e.B & 0xFFFF) == 0 ? $"match after note #{e.A}: no track yet ({e.C} aligned candidates)"
                : $"match after note #{e.A}: track {e.B & 0xFFFF} ({((e.B >> 16 & 1) != 0 ? "settled" : "provisional")}, {((e.B >> 17 & 1) != 0 ? $"unsure: {e.C} tracks have these notes" : "the only candidate")}, by {(e.B >> 18) switch { 1 => "the same key at the same song position", 2 => "a constant offset", _ => "?" }})",
            KindNoteEnd => string.Create(c, $"note #{e.A} ended: FL moved its pitch between {e.X:0.##} and {e.Y:0.##} cents while it sounded (a bend FL applies shows as a range)"),
            _ => $"kind {e.Kind} {e.A} {e.B} {e.C}",
        };
        return $"{t:HH:mm:ss.fff} #{e.Inst} {body}";

        static string Pos(double v) => double.IsNaN(v) ? "none" : v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
