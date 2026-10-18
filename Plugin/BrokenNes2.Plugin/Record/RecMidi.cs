// Round 8 (record): writing a Standard MIDI File. Type 1 (a conductor track with the tempo map, then one track per instrument) for the whole recording, type 0 (one track: the tempo and one
// instrument) for a single track, which FL Studio imports into the channel it is dropped on without asking. No running status (every event has its status byte); at one tick the events go
// note-offs first, then controllers, then pitch bends, then note-ons, so a bend meant for a new note is in place before it starts and never reaches the note that ends there.
using System.Text;

namespace BrokenNes2.Record;

/// <summary>One channel event at a tick. <c>Order</c>: 0 note off, 1 controller, 2 pitch bend, 3 note on (events at one tick are written in this order, then in the order they were added).</summary>
public readonly record struct SmfEvent(long Tick, int Order, byte Status, byte Data1, byte Data2);

public sealed class SmfTrack
{
    public string Name = "";
    public string Instrument = "";
    public List<string> Texts = new();
    public List<SmfEvent> Events = new();
}

public static class RecMidi
{
    public const int Ppq = 960;
    public const byte NoteOff = 0x80, NoteOn = 0x90, Control = 0xB0, Bend = 0xE0;

    /// <summary>The tempo map as meta events: (tick, microseconds per quarter).</summary>
    public static byte[] Write(IReadOnlyList<(long Tick, int MicrosPerQuarter)> tempos, string conductorName, IReadOnlyList<string> conductorTexts, IReadOnlyList<SmfTrack> tracks, bool singleTrack = false)
    {
        using var ms = new MemoryStream();
        void U32(uint v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        ms.Write("MThd"u8); U32(6);
        if (singleTrack)
        {
            U16(0); U16(1); U16(Ppq);
            WriteTrack(ms, Merge(tempos, conductorName, conductorTexts, tracks[0]));
        }
        else
        {
            U16(1); U16(1 + tracks.Count); U16(Ppq);
            WriteTrack(ms, Conductor(tempos, conductorName, conductorTexts));
            foreach (var t in tracks) WriteTrack(ms, Channel(t));
        }
        return ms.ToArray();
    }

    private sealed record Ev(long Tick, int Order, int Seq, byte[] Bytes);

    private static List<Ev> Conductor(IReadOnlyList<(long Tick, int MicrosPerQuarter)> tempos, string name, IReadOnlyList<string> texts)
    {
        var e = new List<Ev>();
        int seq = 0;
        e.Add(new Ev(0, -3, seq++, Meta(0x03, Encoding.UTF8.GetBytes(name))));
        foreach (var tx in texts) e.Add(new Ev(0, -3, seq++, Meta(0x01, Encoding.UTF8.GetBytes(tx))));
        e.Add(new Ev(0, -2, seq++, Meta(0x58, [4, 2, 24, 8])));              // 4/4, a quarter is 24 clocks, 8 32nds per quarter
        foreach (var (tick, us) in tempos) e.Add(new Ev(tick, -1, seq++, Meta(0x51, [(byte)(us >> 16), (byte)(us >> 8), (byte)us])));
        return e;
    }

    private static List<Ev> Channel(SmfTrack t)
    {
        var e = new List<Ev>();
        int seq = 0;
        e.Add(new Ev(0, -3, seq++, Meta(0x03, Encoding.UTF8.GetBytes(t.Name))));
        if (t.Instrument.Length > 0) e.Add(new Ev(0, -3, seq++, Meta(0x04, Encoding.UTF8.GetBytes(t.Instrument))));
        foreach (var tx in t.Texts) e.Add(new Ev(0, -3, seq++, Meta(0x01, Encoding.UTF8.GetBytes(tx))));
        foreach (var ev in t.Events)
            e.Add(new Ev(ev.Tick, ev.Order, seq++, (ev.Status & 0xF0) is 0xC0 or 0xD0 ? [ev.Status, ev.Data1] : [ev.Status, ev.Data1, ev.Data2]));
        return e;
    }

    private static List<Ev> Merge(IReadOnlyList<(long Tick, int MicrosPerQuarter)> tempos, string name, IReadOnlyList<string> texts, SmfTrack t)
    {
        var c = Conductor(tempos, name, texts);
        var ch = Channel(t);
        // the single track's own name replaces the conductor's (FL names the channel after it)
        c.RemoveAt(0);
        int seq = 0;
        return ch.Take(1).Concat(c).Concat(ch.Skip(1)).Select(x => x with { Seq = seq++ }).ToList();
    }

    private static byte[] Meta(byte type, byte[] data)
    {
        var len = Vlq(data.Length);
        var b = new byte[2 + len.Length + data.Length];
        b[0] = 0xFF; b[1] = type;
        len.CopyTo(b, 2);
        data.CopyTo(b, 2 + len.Length);
        return b;
    }

    private static void WriteTrack(MemoryStream ms, List<Ev> evs)
    {
        evs.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Seq.CompareTo(b.Seq));
        using var body = new MemoryStream();
        long last = 0;
        foreach (var e in evs)
        {
            long delta = Math.Max(0, e.Tick - last);
            last = Math.Max(last, e.Tick);
            body.Write(Vlq(delta));
            body.Write(e.Bytes);
        }
        body.Write(Vlq(0)); body.Write([0xFF, 0x2F, 0x00]);
        var bytes = body.ToArray();
        ms.Write("MTrk"u8);
        ms.WriteByte((byte)(bytes.Length >> 24)); ms.WriteByte((byte)(bytes.Length >> 16)); ms.WriteByte((byte)(bytes.Length >> 8)); ms.WriteByte((byte)bytes.Length);
        ms.Write(bytes);
    }

    public static byte[] Vlq(long v)
    {
        if (v < 0) v = 0;
        if (v > 0x0FFFFFFF) v = 0x0FFFFFFF;
        Span<byte> tmp = stackalloc byte[5];
        int n = 0;
        tmp[n++] = (byte)(v & 0x7F);
        while ((v >>= 7) > 0) tmp[n++] = (byte)(0x80 | (v & 0x7F));
        var o = new byte[n];
        for (int i = 0; i < n; i++) o[i] = tmp[n - 1 - i];
        return o;
    }

    // ---- channel events ----
    public static SmfEvent On(long tick, int ch, int key, int vel) => new(tick, 3, (byte)(NoteOn | ch), (byte)key, (byte)vel);
    public static SmfEvent Off(long tick, int ch, int key) => new(tick, 0, (byte)(NoteOff | ch), (byte)key, 64);
    public static SmfEvent Cc(long tick, int ch, int cc, int value) => new(tick, 1, (byte)(Control | ch), (byte)cc, (byte)Math.Clamp(value, 0, 127));
    /// <summary>A pitch bend of <paramref name="value"/> (-8192..8191, 0 = centre).</summary>
    public static SmfEvent PitchBend(long tick, int ch, int value) { int v = Math.Clamp(value + 8192, 0, 16383); return new(tick, 2, (byte)(Bend | ch), (byte)(v & 0x7F), (byte)(v >> 7)); }

    /// <summary>The pitch-bend range (RPN 0) in semitones, then the RPN closed (RPN null), as controllers at <paramref name="tick"/>.</summary>
    public static IEnumerable<SmfEvent> BendRange(long tick, int ch, int semitones) =>
    [
        Cc(tick, ch, 101, 0), Cc(tick, ch, 100, 0), Cc(tick, ch, 6, semitones), Cc(tick, ch, 38, 0), Cc(tick, ch, 101, 127), Cc(tick, ch, 100, 127),
    ];
}
