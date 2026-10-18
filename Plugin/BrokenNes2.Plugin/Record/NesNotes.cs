// Round 8 (record-nes): the notes of a NES recording, read from what the chip SOUNDS like (NesApuModel), not from what the driver wrote: a NES driver can let the hardware shape its notes (the
// decay envelope, the length counter, the triangle's linear counter, the sweep), so the register writes alone do not say when a note ends or how loud it is. The recording's writes are run through
// the model from the chip's own starting state; after every write and every quarter / half frame each channel's effective state is read:
//
//   a note    is a stretch during which the channel is audible (enabled, its length counter running, not muted by its period or the sweep, a volume above 0; the triangle's linear counter above 0),
//             cut where the driver strikes the channel again (a write to its fourth register: the wave and the envelope restart, as a new FL note restarts them in the replay)
//   its values the period (after the hardware sweep), the volume in force (the constant volume or the envelope's level), the pulse duty, the noise's mode, each from the moment it changed
//
// Silences split notes: the replay's next note restarts the wave there, which is inaudible because nothing sounds before it. Nothing is lost silently: what the transcription cannot carry is counted.
namespace BrokenNes2.Record;

/// <summary>A value from time <c>T</c> (CPU cycles from the recording's start) on.</summary>
public readonly record struct NesVal(uint T, int V);

/// <summary>Why a note ended.</summary>
public enum NesEnd : byte { Running = 0, Silent = 1, Struck = 2, LengthCounter = 3, LinearCounter = 4, Disabled = 5, Muted = 6, RecordingEnd = 7, DmcStopped = 8, DmcDone = 9 }

/// <summary>One note of a NES recording.</summary>
public sealed class NesNote
{
    /// <summary>0 pulse 1, 1 pulse 2, 2 triangle, 3 noise, 4 DMC.</summary>
    public int Channel;
    public uint Start, End;
    /// <summary>Started by a write to its channel's fourth register (else it became audible another way: a volume raised, the channel enabled, or it sounded when the recording began).</summary>
    public bool Struck, BeforeStart;
    public NesEnd EndedBy;
    /// <summary>The period (pulse and triangle: the 11-bit timer; noise: the period index 0-15; DMC: the rate index), the volume in force (0-15; the triangle 15), the pulse duty (0-3) and the noise's short mode (0/1), each a timeline from <see cref="Start"/>.</summary>
    public List<NesVal> Periods = new(), Vols = new(), Duties = new(), Modes = new();
    /// <summary>What moved the values: the hardware envelope (volume changes at a quarter frame), the hardware sweep (period changes at a half frame).</summary>
    public int EnvelopeSteps, SweepSteps;
    /// <summary>DMC: the copy of the sample's bytes (an index into <see cref="NesSession.Dmc"/>, -1 when none was made), whether it loops, and how often its rate changed while it played.</summary>
    public int DmcCopy = -1, DmcRateChanges;
    /// <summary>Made audible a moment (under 2 ms) before the driver struck it: its start is the strike.</summary>
    public int StruckLate;
    public bool DmcLoop;

    public double StartSeconds => Start / NesSession.CpuHz;
    public double Seconds => (End - Start) / NesSession.CpuHz;

    /// <summary>The value of a timeline held for the longest time over the note (ties: the earliest).</summary>
    public int Dominant(List<NesVal> line)
    {
        if (line.Count == 0) return 0;
        Span<long> held = stackalloc long[2048];
        held.Clear();
        int best = line[0].V;
        for (int i = 0; i < line.Count; i++)
        {
            uint to = i + 1 < line.Count ? line[i + 1].T : Math.Max(End, line[i].T);
            int v = line[i].V & 2047;
            held[v] += to - line[i].T;
        }
        long bestHeld = -1;
        foreach (var x in line) { int v = x.V & 2047; if (held[v] > bestHeld) { bestHeld = held[v]; best = x.V; } }
        return best;
    }

    /// <summary>The value of a timeline in force at <paramref name="t"/>.</summary>
    public static int At(List<NesVal> line, uint t)
    {
        int v = line.Count > 0 ? line[0].V : 0;
        foreach (var x in line) { if (x.T > t) break; v = x.V; }
        return v;
    }
}

/// <summary>What the reading found besides the notes.</summary>
public sealed class NesNoteStats
{
    public int Writes, Frames, Struck, StruckSilent, BeforeStart, DmcStarts, DmcNoCopy, Pcm4011, PcmFrames, Writes4017;
    public readonly int[] Notes = new int[5];
    public readonly int[] Ends = new int[10];
}

public static class NesNotes
{
    /// <summary>The longest wait for a quarter frame (CPU cycles: 7457, plus one).</summary>
    public const uint QuarterFrame = 7458;

    /// <summary>Every note of the session, in start order (time, then channel).</summary>
    public static List<NesNote> Extract(NesSession s, out NesNoteStats stats)
    {
        var st = new NesNoteStats();
        var m = NesApuModel.Load(s.BaseState) ?? throw new InvalidDataException("the recording's starting state is damaged");
        var open = new NesNote?[5];
        var done = new List<NesNote>();
        int pendingCopy = -1, frame4011 = 0;
        long triStrike = -1, triZero = -1;          // the triangle's last strike while silent, and its last $4008 write of a reload value 0

        bool Audible(int c, out NesEnd why)
        {
            why = NesEnd.Silent;
            switch (c)
            {
                case 0: case 1:
                {
                    var p = m.PulseOf(c);
                    if (!p.Enabled) { why = NesEnd.Disabled; return false; }
                    if (p.Length == 0) { why = NesEnd.LengthCounter; return false; }
                    if (p.Timer < 8 || p.SweepMutes) { why = NesEnd.Muted; return false; }
                    return p.VolumeInForce > 0;
                }
                case 2:
                    if (!m.T.Enabled) { why = NesEnd.Disabled; return false; }
                    if (m.T.Length == 0) { why = NesEnd.LengthCounter; return false; }
                    if (m.T.Linear == 0) { why = NesEnd.LinearCounter; return false; }
                    if (m.T.Timer < 2) { why = NesEnd.Muted; return false; }
                    return true;
                default:
                    if (!m.N.Enabled) { why = NesEnd.Disabled; return false; }
                    if (m.N.Length == 0) { why = NesEnd.LengthCounter; return false; }
                    return m.N.VolumeInForce > 0;
            }
        }

        int Period(int c) => c switch { 0 => m.P1.Timer, 1 => m.P2.Timer, 2 => m.T.Timer, _ => m.N.Index };
        int Vol(int c) => c switch { 0 => m.P1.VolumeInForce, 1 => m.P2.VolumeInForce, 2 => 15, _ => m.N.VolumeInForce };
        int Duty(int c) => c switch { 0 => m.P1.Duty, 1 => m.P2.Duty, _ => 0 };
        int Mode(int c) => c == 3 && m.N.Short ? 1 : 0;

        void Close(int c, uint at, NesEnd why)
        {
            var n = open[c];
            if (n == null) return;
            n.End = Math.Max(at, n.Start);
            n.EndedBy = why;
            st.Ends[(int)why]++;
            done.Add(n);
            open[c] = null;
        }

        void Fill(NesNote n, int c, uint at)
        {
            n.Periods.Add(new NesVal(at, Period(c)));
            n.Vols.Add(new NesVal(at, Vol(c)));
            if (c < 2) n.Duties.Add(new NesVal(at, Duty(c)));
            if (c == 3) n.Modes.Add(new NesVal(at, Mode(c)));
        }

        void Open(int c, uint at, bool struck, bool before)
        {
            var n = new NesNote { Channel = c, Start = at, Struck = struck, BeforeStart = before };
            Fill(n, c, at);
            open[c] = n;
            if (before) st.BeforeStart++;
            if (struck) st.Struck++;
        }

        // the channels' states after a change at time t (cause: the channel whose fourth register was just written, -1 none; tick: 1 a quarter frame, 2 a half frame, 0 a write)
        void Evaluate(uint t, int struck, int tick)
        {
            for (int c = 0; c < 4; c++)
            {
                bool aud = Audible(c, out var why);
                var n = open[c];
                if (!aud)
                {
                    // the triangle's linear counter runs out at the quarter frame after the write that set it to 0: the note ends at that write (the driver's moment; the replay's chip waits for its own quarter frame the same way)
                    if (n != null) Close(c, c == 2 && why == NesEnd.LinearCounter && triZero >= n.Start && t - triZero <= QuarterFrame ? (uint)triZero : t, why);
                    else if (struck == c) { if (c == 2) triStrike = t; else st.StruckSilent++; }
                    continue;
                }
                // the triangle sounds from the quarter frame after its strike ($400B reloads the linear counter then): the note starts at the strike, the driver's moment, as the replay's own chip delays it alike
                if (n == null) { if (c == 2 && struck != 2 && triStrike >= 0 && t - triStrike <= QuarterFrame) { Open(c, (uint)triStrike, true, false); triStrike = -1; } else Open(c, t, struck == c, false); continue; }
                if (struck == c)
                {
                    // the driver made it audible (its volume, $4015) a moment before it struck it: one note, struck (not a blip of a few cycles and then a note)
                    if (!n.Struck && !n.BeforeStart && t - n.Start < NesTranscribe.MinHold) { n.Struck = true; st.Struck++; n.StruckLate++; n.Periods.Clear(); n.Vols.Clear(); n.Duties.Clear(); n.Modes.Clear(); n.Start = t; Fill(n, c, t); continue; }
                    Close(c, t, NesEnd.Struck); Open(c, t, true, false);
                    continue;
                }
                int p = Period(c), v = Vol(c);
                if (Add(n.Periods, t, p) && tick == 2 && c < 2) n.SweepSteps++;
                if (Add(n.Vols, t, v) && tick >= 1 && c != 2) n.EnvelopeSteps++;
                if (c < 2) Add(n.Duties, t, Duty(c));
                if (c == 3) Add(n.Modes, t, Mode(c));
            }
        }

        // the DMC: a sample starts with a $4015 write when none plays (or loops by itself), stops with a $4015 write without bit 4 or when its bytes run out
        void DmcCheck(uint t)
        {
            var n = open[4];
            if (n == null) return;
            if (!m.DmcEnabled) { Close(4, t, NesEnd.DmcStopped); return; }
            if (m.DmcEnd >= 0 && m.Clock >= m.DmcEnd)
            {
                if ((m.DmcCtrl & 0x40) != 0) { m.LoopDmc(); return; }
                Close(4, (uint)Math.Min(m.DmcEnd, s.Length), NesEnd.DmcDone);
            }
        }

        // what sounds when the recording begins
        for (int c = 0; c < 4; c++) if (Audible(c, out _)) Open(c, 0, false, true);
        if (m.DmcEnd > 0) { var dn = new NesNote { Channel = 4, Start = 0, BeforeStart = true, DmcLoop = (m.DmcCtrl & 0x40) != 0 }; dn.Periods.Add(new NesVal(0, m.DmcCtrl & 15)); dn.Vols.Add(new NesVal(0, 15)); open[4] = dn; st.BeforeStart++; }

        void Advance(uint to)
        {
            while (true)
            {
                // a DMC sample that runs out before the next event
                if (open[4] != null && m.DmcEnd >= 0 && m.DmcEnd <= to && m.DmcEnd >= m.Clock)
                {
                    while (m.StepToNextEvent(m.DmcEnd)) Evaluate((uint)(m.Clock - 1), -1, m.LastTick);
                    DmcCheck((uint)m.Clock);
                    if (open[4] != null && m.DmcEnd <= m.Clock) break;          // (a loop that restarted at once would not move: guard)
                    continue;
                }
                if (!m.StepToNextEvent(to)) break;
                Evaluate((uint)(m.Clock - 1), -1, m.LastTick);
            }
        }

        for (int i = 0; i < s.Count; i++)
        {
            s.Get(i, out uint t, out int reg, out int v);
            if (reg == NesSession.MarkerFrame)
            {
                Advance(t);
                st.Frames++;
                if (frame4011 >= 20) st.PcmFrames++;
                frame4011 = 0;
                continue;
            }
            if (reg == NesSession.MarkerDmc) { pendingCopy = v; continue; }
            Advance(t);
            st.Writes++;
            bool wasPlaying = m.DmcPlaying;
            int oldRate = m.DmcCtrl & 15;
            bool trig = m.Write(reg, v);
            if (reg == 0x08 && (v & 0x7F) == 0) triZero = t;
            if (reg == 0x11) { st.Pcm4011++; frame4011++; }
            if (reg == 0x17) st.Writes4017++;
            if (reg == 0x10 && open[4] != null && (v & 15) != oldRate) open[4]!.DmcRateChanges++;
            if (reg == 0x15)
            {
                if (!m.DmcEnabled) Close(4, t, NesEnd.DmcStopped);
                else if (!wasPlaying && m.DmcPlaying)
                {
                    Close(4, t, NesEnd.Struck);
                    var dn = new NesNote { Channel = 4, Start = t, Struck = true, DmcCopy = pendingCopy, DmcLoop = (m.DmcCtrl & 0x40) != 0 };
                    dn.Periods.Add(new NesVal(t, m.DmcCtrl & 15)); dn.Vols.Add(new NesVal(t, 15));
                    open[4] = dn;
                    st.DmcStarts++;
                    if (pendingCopy < 0) st.DmcNoCopy++;
                }
                pendingCopy = -1;
            }
            int ch = trig ? reg >> 2 : -1;
            Evaluate(t, ch, 0);
        }
        Advance(s.Length);
        for (int c = 0; c < 5; c++) Close(c, s.Length, NesEnd.RecordingEnd);

        done.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Channel.CompareTo(b.Channel));
        foreach (var n in done) st.Notes[n.Channel]++;
        stats = st;
        return done;
    }

    /// <summary>Appends a value when it differs from the last one; a change at the same moment replaces it. True when the timeline changed.</summary>
    private static bool Add(List<NesVal> line, uint t, int v)
    {
        var last = line[^1];
        if (last.V == v) return false;
        if (last.T == t)
        {
            line[^1] = new NesVal(t, v);
            if (line.Count > 1 && line[^2].V == v) line.RemoveAt(line.Count - 1);
            return true;
        }
        line.Add(new NesVal(t, v));
        return true;
    }
}
