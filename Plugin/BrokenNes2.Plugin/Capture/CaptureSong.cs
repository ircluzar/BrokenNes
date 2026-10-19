// Round 9 (capture): the lanes of a take (what FL played on each BrokenNes2 instance) -> a Standard MIDI File with the pitch bends, and the facts that explain it.
//
//   notes      one per note FL started: key = FL's key (InitLevels, cents from MIDI 0: FlPitch) + the instance's Coarse, the velocity FL gave (x 128: FL's 0.5 is MIDI 64), start and end at their real
//              times (the instance's sample clock since FL's Play) converted to ticks (960 per quarter) at the take's tempo (FlClock.Resolve). A note still sounding at Stop ends there (said).
//   bends      what the instance PLAYS is FinalLevels.Pitch + its tuning (slides, per-note pitch, fine pitch, the channel's pitch, Coarse / Fine): the bend at every moment is that pitch minus the key of
//              the note that owns the channel (the newest one sounding: a BrokenNes2 channel is monophonic and plays its newest note), at a pitch-bend range per track (RPN 0) wide enough for every
//              move (the smallest of 2..48 semitones; beyond 48 the value is clamped and counted). A bend is written whenever its 14-bit value changes, before the note-on it belongs to.
//   tracks     SMF type 1: a conductor (tempo, FL's time signature, the facts as text) and one track per instance that got a note, named after its role (Pulse 1, Pulse 2, Triangle, Noise, DMC) or
//              FL's name for its channel, on the MIDI channel VRUN's converter turns into that NES channel: Pulse 1 -> 1, Pulse 2 -> 2, Triangle -> 3, Noise -> 10 (the converter takes channel 10
//              for the noise when it has more notes than channel 4, which the capture never uses; the three densest other channels play the squares and the triangle, the lowest of them on
//              average the triangle: the facts say when that does not give each role its own NES channel). Everything ends at the bar line after the last note: VRUN loops the song there.
using System.Globalization;
using BrokenNes2.Record;
using Vrun.Audio;

namespace BrokenNes2.Capture;

public sealed class CapNote
{
    public int Voice, Key, Velocity;
    public double Start, End;
    public long StartTick, EndTick;
    public bool EndedAtStop, VelocityFromLevel;
    public double InitCents, PlayedAtStart, Tuning;
    /// <summary>The pitch played (cents from C5): the start, then every change FL made while it sounded.</summary>
    public readonly List<(double T, double Cents)> Pitch = new();
    /// <summary>The largest distance between what it played and its key (cents).</summary>
    public double MaxDev;
    public bool Moves => MaxDev > 0.5;
    internal double Current;
}

public sealed class CapTrack
{
    public required LaneIdentity Id;
    /// <summary>MIDI channel 0-15 (FL numbers it 1-16).</summary>
    public int Channel;
    /// <summary>The pitch-bend range written (RPN 0), semitones.</summary>
    public int BendRange = 2;
    public readonly List<CapNote> Notes = new();
    public readonly List<SmfEvent> Events = new();
    public int BendEvents, Clamped, SameKeyCut, Overlapped, EndedAtStop, LevelVelocities;
    /// <summary>The largest pitch-bend distance from the key (cents) and the largest error the 14-bit bend leaves (cents).</summary>
    public double MaxDev, MaxQuantError;
    public string Name => Id.Name;
    public int MovingNotes => Notes.Count(n => n.Moves);
}

public sealed class CaptureResult
{
    public string Name = "";
    public byte[] Midi = [];
    public readonly List<CapTrack> Tracks = new();
    public TempoChoice Tempo = new(120, "", null, true);
    public int MicrosPerQuarter;
    public int BeatsPerBar = 4;
    public long EndTick;
    public int Bars;
    /// <summary>The take's length (the longest lane's clock at Stop), seconds.</summary>
    public double Seconds;
    /// <summary>For the window: the summary first, then what makes the file or VRUN's replay differ from what FL played.</summary>
    public readonly List<string> Facts = new();
    /// <summary>The details (the host log; tests).</summary>
    public readonly List<string> Report = new();
    /// <summary>Why nothing can be handed over (no note), null when the file is made.</summary>
    public string? Problem;
    /// <summary>For each NES channel (square 1, square 2, triangle, noise): the track VRUN's converter gives it (-1 none).</summary>
    public readonly int[] ConverterTrack = [-1, -1, -1, -1];
    public long LostEvents;
    public int Notes => Tracks.Sum(t => t.Notes.Count);
    public int MovingNotes => Tracks.Sum(t => t.MovingNotes);
    public int BendEvents => Tracks.Sum(t => t.BendEvents);
    public double SecondsPerTick => MicrosPerQuarter / 1e6 / CaptureSong.Ppq;
    public long TickOf(double seconds) => CaptureSong.TickOf(seconds, MicrosPerQuarter);
}

public static class CaptureSong
{
    public const int Ppq = RecMidi.Ppq;
    public const int MaxBendRange = 48;

    public static long TickOf(double seconds, int microsPerQuarter) => (long)Math.Round(seconds * 1e6 / microsPerQuarter * Ppq, MidpointRounding.AwayFromZero);

    private static readonly int[] Pool = [4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15, 3];

    /// <summary>The MIDI channel a role asks for (-1: none of its own).</summary>
    public static int ChannelOf(CapRole r) => r switch { CapRole.Pulse1 => 0, CapRole.Pulse2 => 1, CapRole.Triangle => 2, CapRole.Noise => 9, CapRole.Dmc => 4, _ => -1 };

    public static CaptureResult Build(string name, IReadOnlyList<LaneData> lanes, FlTiming start, FlTiming end)
    {
        var r = new CaptureResult { Name = name };
        r.Tempo = FlClock.Resolve(start);
        r.MicrosPerQuarter = Math.Clamp((int)Math.Round(60_000_000.0 / r.Tempo.Bpm), 1, 0xFFFFFF);
        r.BeatsPerBar = start.BeatsPerBar;
        var started = lanes.Where(l => l.Started).ToList();
        r.Seconds = started.Count == 0 ? 0 : started.Max(l => l.StopSeconds);
        r.LostEvents = lanes.Sum(l => l.Lost);

        // ---- the notes of each lane
        foreach (var lane in started.OrderBy(l => (int)l.Identity.Role).ThenBy(l => l.Identity.InstanceNo))
        {
            var t = new CapTrack { Id = lane.Identity };
            ReadNotes(lane, t, r.MicrosPerQuarter);
            if (t.Notes.Count > 0) r.Tracks.Add(t);
        }
        if (r.Tracks.Count == 0)
        {
            r.Problem = lanes.Count == 0 ? "no BrokenNes2 channel was there to capture"
                : started.Count == 0 ? "FL's Play never reached BrokenNes2 (no FPD_SetPlaying): press Play in FL while Capture from FL is armed"
                : $"no BrokenNes2 channel got a note between FL's Play and Stop ({started.Count} channel{(started.Count == 1 ? "" : "s")} listened for {r.Seconds:0.0} s)";
            r.Report.Add("nothing captured: " + r.Problem);
            return r;
        }

        // ---- MIDI channels: the one VRUN's converter turns into the role's NES channel; the others from the pool
        var used = new HashSet<int>();
        int shared = 0;
        foreach (var t in r.Tracks)
        {
            int want = ChannelOf(t.Id.Role);
            if (CaptureMutants.On(CaptureMutants.Pulse2OnChannel1) && t.Id.Role == CapRole.Pulse2) want = 0;     // (the deliberate bug)
            int ch = want >= 0 && (!used.Contains(want) || CaptureMutants.On(CaptureMutants.Pulse2OnChannel1)) ? want : Pool.FirstOrDefault(c => !used.Contains(c), -1);
            if (ch < 0) { ch = Pool[shared % Pool.Length]; shared++; }
            used.Add(ch);
            t.Channel = ch;
        }

        // ---- bends, events
        foreach (var t in r.Tracks) MakeEvents(t, r.MicrosPerQuarter);

        // ---- the end: the bar line after the last note (VRUN loops the song there)
        long lastOff = r.Tracks.SelectMany(t => t.Notes).Max(n => n.EndTick);
        long bar = (long)r.BeatsPerBar * Ppq;
        r.EndTick = CaptureMutants.On(CaptureMutants.EndAtLastNote) ? lastOff : Math.Max(bar, (lastOff + bar - 1) / bar * bar);
        r.Bars = (int)((r.EndTick + bar - 1) / bar);

        Describe(r, lanes, started, start, end);
        r.Midi = Write(r);

        // ---- what VRUN's converter will make of it (its own channel choice, on the very bytes)
        try
        {
            var file = MidiFile.Parse(r.Midi);
            var map = Midi2Nes.SelectChannelMap(file, null, _ => { });
            foreach (var kv in map)
                if (kv.Value is >= 0 and < 4) r.ConverterTrack[kv.Value] = r.Tracks.FindIndex(t => t.Channel == kv.Key && t.Notes.Count > 0);
        }
        catch (Exception e) { r.Report.Add("the converter could not read the capture back: " + e.Message); }
        ConverterFacts(r);
        return r;
    }

    // ------------------------------------------------------------------------------------------------ notes

    private static void ReadNotes(LaneData lane, CapTrack t, int mpq)
    {
        var open = new Dictionary<int, CapNote>();
        double tuning = 0;
        for (int i = 0; i < lane.Count; i++)
        {
            int kind = CapEv.Kind(lane.Code[i]), voice = CapEv.Voice(lane.Code[i]);
            double at = lane.T[i];
            switch (kind)
            {
                case CapEv.Tuning: tuning = lane.A[i]; break;
                case CapEv.On:
                case CapEv.OnLevelVelocity:
                {
                    if (open.Remove(voice, out var was)) { was.End = at; t.Notes.Add(was); }
                    var n = new CapNote
                    {
                        Voice = voice, Start = at, InitCents = lane.A[i], PlayedAtStart = lane.B[i], Tuning = tuning, VelocityFromLevel = kind == CapEv.OnLevelVelocity,
                        Key = FlPitch.KeyOfInit(lane.A[i], tuning),
                    };
                    double vel = lane.C[i] * (CaptureMutants.On(CaptureMutants.VelocityOver127) ? 127.0 : 128.0);
                    n.Velocity = Math.Clamp((int)Math.Round(vel, MidpointRounding.AwayFromZero), 1, 127);
                    n.Pitch.Add((at, lane.B[i]));
                    open[voice] = n;
                    if (n.VelocityFromLevel) t.LevelVelocities++;
                    break;
                }
                case CapEv.Pitch:
                    if (open.TryGetValue(voice, out var p)) p.Pitch.Add((at, lane.A[i]));
                    break;
                case CapEv.Off:
                    if (open.Remove(voice, out var o)) { o.End = at; t.Notes.Add(o); }
                    break;
            }
        }
        foreach (var n in open.Values.OrderBy(n => n.Start))
        {
            if (CaptureMutants.On(CaptureMutants.OpenNotesLost)) break;                       // (the deliberate bug)
            n.End = Math.Max(n.Start, lane.StopSeconds);
            n.EndedAtStop = true;
            t.EndedAtStop++;
            t.Notes.Add(n);
        }
        foreach (var n in t.Notes)
        {
            n.StartTick = TickOf(n.Start, mpq);
            n.EndTick = Math.Max(n.StartTick + 1, TickOf(n.End, mpq));
            double keyCents = FlPitch.FinalOfMidi(n.Key);
            n.MaxDev = n.Pitch.Max(x => Math.Abs(x.Cents - keyCents));
        }
        t.Notes.Sort((a, b) => a.StartTick != b.StartTick ? a.StartTick.CompareTo(b.StartTick) : a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Voice.CompareTo(b.Voice));
    }

    // ------------------------------------------------------------------------------------------------ events: notes, the owner's bend

    private static void MakeEvents(CapTrack t, int mpq)
    {
        // same key struck again before its release on this channel: the earlier note ends where the new one starts (a MIDI channel holds one note per key)
        var lastOfKey = new Dictionary<int, CapNote>();
        var keep = new List<CapNote>();
        foreach (var n in t.Notes)
        {
            if (lastOfKey.TryGetValue(n.Key, out var prev) && prev.EndTick > n.StartTick)
            {
                t.SameKeyCut++;
                if (prev.StartTick >= n.StartTick) keep.Remove(prev);
                else prev.EndTick = n.StartTick;
            }
            lastOfKey[n.Key] = n;
            keep.Add(n);
        }
        // (notes still sounding when a later one starts: the channel plays the newest, the bend follows it)
        for (int i = 0; i + 1 < t.Notes.Count; i++)
            if (t.Notes[i + 1].Start < t.Notes[i].End) t.Overlapped++;

        // the owner's distance from its key at every change: pass 1 finds the range, pass 2 writes
        var pts = new List<(double T, int Order, int Seq, CapNote N, double Cents)>();
        int seq = 0;
        foreach (var n in t.Notes)
        {
            pts.Add((n.Start, 1, seq++, n, n.PlayedAtStart));
            for (int k = 1; k < n.Pitch.Count; k++) pts.Add((n.Pitch[k].T, 2, seq++, n, n.Pitch[k].Cents));
            pts.Add((n.End, 0, seq++, n, 0));
        }
        pts.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Seq.CompareTo(b.Seq));
        var active = new List<CapNote>();
        var bends = new List<(double T, double Dev, CapNote Owner)>();
        for (int i = 0; i < pts.Count; i++)
        {
            var (at, order, _, n, cents) = pts[i];
            if (order == 0) active.Remove(n);
            else if (order == 1) { n.Current = cents; active.Add(n); }
            else n.Current = cents;
            if (i + 1 < pts.Count && pts[i + 1].T == at) continue;                 // a moment's changes all applied first
            var owner = active.Count > 0 ? active[^1] : null;
            if (owner == null) continue;
            bends.Add((at, owner.Current - FlPitch.FinalOfMidi(owner.Key), owner));
        }
        // the smallest range whose 14 bits reach every move: up to +range x 8191/8192, down to -range
        double up = bends.Count == 0 ? 0 : Math.Max(0, bends.Max(b => b.Dev)), down = bends.Count == 0 ? 0 : Math.Max(0, -bends.Min(b => b.Dev));
        t.MaxDev = Math.Max(up, down);
        int range = 2;
        while (range < MaxBendRange && (up > range * 100.0 * 8191 / 8192 || down > range * 100.0)) range++;
        t.BendRange = range;

        // the events
        if (!CaptureMutants.On(CaptureMutants.BendRangeUnwritten)) t.Events.AddRange(RecMidi.BendRange(0, t.Channel, range));
        foreach (var n in keep)
        {
            t.Events.Add(RecMidi.On(n.StartTick, t.Channel, n.Key, n.Velocity));
            t.Events.Add(RecMidi.Off(n.EndTick, t.Channel, n.Key));
        }
        if (keep.Count != t.Notes.Count) { t.Notes.Clear(); t.Notes.AddRange(keep); }
        int last = 0;
        double unit = range * 100.0 / 8192;                                         // cents per bend step
        foreach (var (at, dev, _) in bends)
        {
            double d = CaptureMutants.On(CaptureMutants.BendSignFlipped) ? -dev : dev;
            int v = (int)Math.Round(d / unit, MidpointRounding.AwayFromZero);
            if (v < -8192 || v > 8191) { t.Clamped++; v = Math.Clamp(v, -8192, 8191); }
            else t.MaxQuantError = Math.Max(t.MaxQuantError, Math.Abs(v * unit - d));
            if (v == last) continue;
            t.Events.Add(RecMidi.PitchBend(TickOf(at, mpq), t.Channel, v));
            t.BendEvents++;
            last = v;
        }
    }

    // ------------------------------------------------------------------------------------------------ facts

    private static string Dur(double s) => $"{(int)(s / 60)}:{s % 60:00.0}";

    private static void Describe(CaptureResult r, IReadOnlyList<LaneData> lanes, List<LaneData> started, FlTiming start, FlTiming end)
    {
        var ci = CultureInfo.InvariantCulture;
        string chans = string.Join(", ", r.Tracks.Select(t => $"{t.Name} -> MIDI {t.Channel + 1}"));
        r.Facts.Add($"Captured {r.Notes} note{(r.Notes == 1 ? "" : "s")} from {r.Tracks.Count} channel{(r.Tracks.Count == 1 ? "" : "s")} ({chans}) in {Dur(r.Seconds)}: {r.Bars} bar{(r.Bars == 1 ? "" : "s")} of {r.BeatsPerBar}/4 at {r.Tempo.Bpm.ToString("0.##", ci)} BPM");
        int moving = r.MovingNotes;
        if (moving > 0)
            r.Facts.Add($"{moving} note{(moving == 1 ? " slides or bends" : "s slide or bend")} ({r.BendEvents} pitch-bend events, range {string.Join("/", r.Tracks.Where(t => t.BendEvents > 0).Select(t => t.BendRange).Distinct())} semitones): VRUN's engine has no pitch bend and plays {(moving == 1 ? "it" : "them")} at {(moving == 1 ? "its key" : "their keys")}; the MIDI file keeps the bends");
        else r.Facts.Add("no slide or pitch bend in the take");
        if (r.Tempo.Guessed) r.Facts.Add("tempo " + r.Tempo.Source);
        int atStop = r.Tracks.Sum(t => t.EndedAtStop);
        if (atStop > 0) r.Facts.Add($"{atStop} note{(atStop == 1 ? " was" : "s were")} still sounding when FL stopped: {(atStop == 1 ? "it ends" : "they end")} there");
        int clamped = r.Tracks.Sum(t => t.Clamped);
        if (clamped > 0) r.Facts.Add($"{clamped} pitch value{(clamped == 1 ? "" : "s")} more than {MaxBendRange} semitones from the key: clamped to the bend range's end");
        if (r.LostEvents > 0) r.Facts.Add($"{r.LostEvents:N0} events were LOST (the capture's memory ran out or its worker was late): the file misses them");
        int cut = r.Tracks.Sum(t => t.SameKeyCut);
        if (cut > 0) r.Facts.Add($"{cut} note{(cut == 1 ? "" : "s")} struck again on the same key before {(cut == 1 ? "its" : "their")} release: the earlier one ends there (one note per key on a MIDI channel)");
        int over = r.Tracks.Sum(t => t.Overlapped);
        if (over > 0) r.Facts.Add($"{over} note{(over == 1 ? "" : "s")} overlapped the next one on {(over == 1 ? "its" : "their")} channel: the channel plays the newest (as BrokenNes2 does) and the bend follows it");
        int levelVel = r.Tracks.Sum(t => t.LevelVelocities);
        if (levelVel > 0) r.Facts.Add($"FL gave no velocity for {levelVel} note{(levelVel == 1 ? "" : "s")} (FPV_GetVelocity): their velocity is the voice's level");
        var late = started.Where(l => l.JoinedLate).ToList();
        if (late.Count > 0) r.Facts.Add($"{string.Join(", ", late.Select(l => l.Identity.Name))} got no Play of {(late.Count == 1 ? "its" : "their")} own from FL: {(late.Count == 1 ? "it joined" : "they joined")} the take at the others' clock");
        if (started.Count > 1)
        {
            double max = started.Max(l => l.StopSeconds), min = started.Min(l => l.StopSeconds);
            var behind = started.Where(l => max - l.StopSeconds > 0.05).ToList();
            if (behind.Count > 0) r.Facts.Add($"{string.Join(", ", behind.Select(l => $"{l.Identity.Name} ({max - l.StopSeconds:0.00} s)"))}: FL rendered {(behind.Count == 1 ? "this channel" : "these channels")} less than the others (FL's Smart disable?): {(behind.Count == 1 ? "its" : "their")} notes may be early by up to that");
            r.Report.Add($"lane clocks at Stop: {min:0.000000} .. {max:0.000000} s");
        }
        var jumps = started.Count == 0 ? new List<double>() : started.Select(l => Enumerable.Range(0, l.Count).Where(i => CapEv.Kind(l.Code[i]) == CapEv.SongPos && l.T[i] > 0.05).Select(i => l.T[i]).ToList()).OrderByDescending(x => x.Count).First();
        if (jumps.Count > 0) r.Facts.Add($"FL said its song position changed during the take (a jump, or FL's loop) at {string.Join(", ", jumps.Take(4).Select(Dur))}: the file runs on through it (stop FL before it loops)");
        if (end.TempoChanges > start.TempoChanges || end.SptChanges > start.SptChanges) r.Facts.Add($"FL's tempo changed during the take: the file keeps the first ({r.Tempo.Bpm.ToString("0.##", ci)} BPM) with every note at its real time, which is what VRUN plays (it honours the first tempo only)");
        if (started.Any(l => l.RateChanges > 0)) r.Facts.Add("FL changed its sample rate during the take: the clock follows it");
        if (((start.ProcessMode | end.ProcessMode) & 16) != 0 && start.ProcessMode >= 0) r.Facts.Add("captured while FL rendered (Export): the clock is the samples FL rendered, not the wall clock");
        int silent = lanes.Count(l => l.Started) - r.Tracks.Count;
        if (silent > 0) r.Report.Add($"{silent} channel{(silent == 1 ? "" : "s")} got no note: no track");

        r.Report.Add($"tempo: {r.Tempo.Source}{(r.Tempo.Check != null ? "; " + r.Tempo.Check : "")}; {r.MicrosPerQuarter} us per quarter, {Ppq} ticks per quarter");
        r.Report.Add($"FL's timing at Play: samples per tick {start.SamplesPerTick:0.####}, time signature {(start.TimeSigKnown ? $"{start.StepsPerBar}/{start.StepsPerBeat} steps, PPQ {start.Ppq}" : "unknown")}, FPE_Tempo {(start.HasTempo ? start.Tempo.ToString("0.###", ci) : "none")}, process mode {start.ProcessMode}, {start.Rate:N0} Hz");
        foreach (var t in r.Tracks)
            r.Report.Add($"track \"{t.Name}\" (#{t.Id.InstanceNo}, {t.Id.Detail}): MIDI {t.Channel + 1}, {t.Notes.Count} notes ({t.MovingNotes} move), {t.BendEvents} bends at range {t.BendRange} (largest {t.MaxDev:0.##} cents from the key, 14-bit error at most {t.MaxQuantError:0.###} cents){(t.Clamped > 0 ? $", {t.Clamped} clamped" : "")}{(t.SameKeyCut > 0 ? $", {t.SameKeyCut} same-key cuts" : "")}{(t.EndedAtStop > 0 ? $", {t.EndedAtStop} ended at Stop" : "")}");
        r.Report.Add($"end: tick {r.EndTick} ({r.Bars} bars of {r.BeatsPerBar}), the last note ends at tick {r.Tracks.SelectMany(t => t.Notes).Max(n => n.EndTick)}");
    }

    /// <summary>What VRUN's converter does with the roles (its own rule on the file's note counts and pitches): said when a role does not get its own NES channel.</summary>
    private static void ConverterFacts(CaptureResult r)
    {
        string[] nes = ["square 1", "square 2", "triangle", "noise"];
        CapRole[] wanted = [CapRole.Pulse1, CapRole.Pulse2, CapRole.Triangle, CapRole.Noise];
        var moved = new List<string>();
        for (int c = 0; c < 4; c++)
        {
            int k = r.ConverterTrack[c];
            if (k < 0) continue;
            var t = r.Tracks[k];
            if (t.Id.Role != wanted[c]) moved.Add($"{nes[c]} plays {t.Name}");
        }
        var unplayed = r.Tracks.Where((t, i) => Array.IndexOf(r.ConverterTrack, i) < 0).Select(t => t.Name).ToList();
        if (moved.Count > 0)
            r.Facts.Add($"VRUN's converter chooses by itself: {string.Join(", ", moved)} (it gives the noise MIDI channel 10, the triangle the lowest of the three busiest other channels on average, the squares the other two)");
        if (unplayed.Count > 0)
            r.Facts.Add($"VRUN's engine has four channels: {string.Join(", ", unplayed)} {(unplayed.Count == 1 ? "is" : "are")} not played (in the MIDI file all the same)");
        r.Report.Add("converter: " + string.Join(", ", Enumerable.Range(0, 4).Select(c => $"{nes[c]} <- {(r.ConverterTrack[c] >= 0 ? r.Tracks[r.ConverterTrack[c]].Name : "nothing")}")));
    }

    // ------------------------------------------------------------------------------------------------ the file

    private sealed record Ev(long Tick, int Order, int Seq, byte[] Bytes);

    private static byte[] Write(CaptureResult r)
    {
        using var ms = new MemoryStream();
        void U32(uint v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        ms.Write("MThd"u8); U32(6); U16(1); U16(1 + r.Tracks.Count); U16(Ppq);
        int seq = 0;
        var cond = new List<Ev>
        {
            new(0, -3, seq++, Meta(0x03, r.Name)),
            new(0, -3, seq++, Meta(0x01, "Captured from FL Studio by BrokenNes2 (Test MIDI export > Capture from FL)")),
            new(0, -3, seq++, Meta(0x01, "tempo: " + r.Tempo.Source)),
        };
        foreach (var f in r.Facts) cond.Add(new Ev(0, -3, seq++, Meta(0x01, f)));
        cond.Add(new Ev(0, -2, seq++, MetaBytes(0x58, [(byte)r.BeatsPerBar, 2, 24, 8])));
        cond.Add(new Ev(0, -1, seq++, MetaBytes(0x51, [(byte)(r.MicrosPerQuarter >> 16), (byte)(r.MicrosPerQuarter >> 8), (byte)r.MicrosPerQuarter])));
        WriteTrack(ms, cond, r.EndTick);
        foreach (var t in r.Tracks)
        {
            var e = new List<Ev> { new(0, -3, seq++, Meta(0x03, t.Name)), new(0, -3, seq++, Meta(0x04, "BrokenNes2 " + LaneIdentity.RoleName(t.Id.Role))) };
            foreach (var ev in t.Events) e.Add(new Ev(ev.Tick, ev.Order, seq++, [ev.Status, ev.Data1, ev.Data2]));
            WriteTrack(ms, e, r.EndTick);
        }
        return ms.ToArray();
    }

    private static byte[] Meta(byte type, string text) => MetaBytes(type, System.Text.Encoding.UTF8.GetBytes(text));

    private static byte[] MetaBytes(byte type, byte[] data)
    {
        var len = RecMidi.Vlq(data.Length);
        var b = new byte[2 + len.Length + data.Length];
        b[0] = 0xFF; b[1] = type;
        len.CopyTo(b, 2);
        data.CopyTo(b, 2 + len.Length);
        return b;
    }

    private static void WriteTrack(MemoryStream ms, List<Ev> evs, long endTick)
    {
        evs.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Seq.CompareTo(b.Seq));
        using var body = new MemoryStream();
        long last = 0;
        foreach (var e in evs)
        {
            body.Write(RecMidi.Vlq(Math.Max(0, e.Tick - last)));
            last = Math.Max(last, e.Tick);
            body.Write(e.Bytes);
        }
        body.Write(RecMidi.Vlq(Math.Max(0, endTick - last))); body.Write([0xFF, 0x2F, 0x00]);       // every track ends at the song's end: VRUN's converter loops there
        var bytes = body.ToArray();
        ms.Write("MTrk"u8);
        ms.WriteByte((byte)(bytes.Length >> 24)); ms.WriteByte((byte)(bytes.Length >> 16)); ms.WriteByte((byte)(bytes.Length >> 8)); ms.WriteByte((byte)bytes.Length);
        ms.Write(bytes);
    }
}
