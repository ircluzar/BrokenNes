// Round 9 (capture): the lanes of a take (what FL played on each BrokenNes2 instance) -> a Standard MIDI File with the pitch bends, and the facts that explain it.
//
//   base       FL hands a voice's pitch (InitLevels and FinalLevels alike) in cents above the CHANNEL's base key (Record/FlNotes.cs, the `reload` agent's reading of the user's real log): C5 on a
//              channel made in FL (the user's compositions: the default here), MIDI note 0 on a channel FL made by importing a MIDI file. FL tells a plugin no root note, so per channel: the base the
//              instance found from a recording's track when it knows one; else C5, unless the notes cannot be read from C5 (keys above 127: then MIDI note 0, said); a channel whose notes fit both
//              follows the channels that were sure (said). The base used is in the facts and in the file's text.
//   notes      one per note FL started: key = the nearest key of InitLevels.Pitch + the instance's tuning (Coarse x 100 + Fine), the velocity FL gave (x 128: FL's 0.5 is MIDI 64), start and end
//              at their real times (the instance's sample clock since FL's Play) converted to ticks (960 per quarter) at the take's tempo (FlClock.Resolve). A note still sounding at Stop ends there.
//   bends      what the instance PLAYS is FinalLevels.Pitch + its tuning (slides, per-note pitch, fine pitch, the channel's pitch, Coarse / Fine): the bend at every moment is that pitch minus the key of
//              the note that owns the channel (the newest one sounding: a BrokenNes2 channel is monophonic and plays its newest note), at a pitch-bend range per track (RPN 0) wide enough for every
//              move (the smallest of 2..48 semitones; beyond 48 the value is clamped and counted). A bend is written whenever its 14-bit value changes, before the note-on it belongs to. (A bend is
//              the same whatever the base: only the keys depend on it.)
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
    /// <summary>What FL answered when asked its song position at the note-on (FHD_GetMixingTime, offset 0): T and T2.</summary>
    public double FlBeats = double.NaN, FlT2 = double.NaN;
    public bool HasFlTime;
    internal double Current;
}

/// <summary>How a channel's base key was chosen: the instance knew it, the notes only fit MIDI note 0, the notes only fit C5, they fit both (C5, FL's own channels), or like the channels that were sure.</summary>
public enum BaseWhy { Default, Known, OnlyMidi0, OnlyC5, LikeOthers }

public sealed class CapTrack
{
    public required LaneIdentity Id;
    /// <summary>MIDI channel 0-15 (FL numbers it 1-16).</summary>
    public int Channel;
    /// <summary>The pitch-bend range written (RPN 0), semitones.</summary>
    public int BendRange = 2;
    public readonly List<CapNote> Notes = new();
    public readonly List<SmfEvent> Events = new();
    public int BendEvents, Clamped, SameKeyCut, Overlapped, EndedAtStop, LevelVelocities, KeysClamped;
    /// <summary>The largest pitch-bend distance from the key (cents) and the largest error the 14-bit bend leaves (cents).</summary>
    public double MaxDev, MaxQuantError;
    /// <summary>The key FL's pitch was read from on this channel (Record/FlNotes.cs: 60 = C5, 0 = MIDI note 0) and how it was chosen.</summary>
    public int Base = FlNotes.BaseC5;
    public BaseWhy BaseHow;
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

        // ---- each channel's base key, then the notes of each lane
        var bases = ResolveBases(started);
        foreach (var lane in started.OrderBy(l => (int)l.Identity.Role).ThenBy(l => l.Identity.InstanceNo))
        {
            var (b, why) = bases[lane];
            var t = new CapTrack { Id = lane.Identity, Base = b, BaseHow = why };
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
        // the window shows the first few: the summary, then what changes the replay most (lost events, guesses, the bends VRUN drops, the converter's own choice, the base), then the rest
        static int Rank(string f) => f.Contains("LOST") ? 0 : f.Contains("ASSUMED") || f.Contains("ASSUMING") ? 1 : f.Contains("no pitch bend") ? 2 : f.Contains("chooses by itself") ? 3 : f.Contains("MIDI note 0") && !f.StartsWith("keys read from C5") ? 4 : f.Contains("not played") ? 5 : 9;
        var rest = r.Facts.Skip(1).Select((f, i) => (f, i)).OrderBy(x => Rank(x.f)).ThenBy(x => x.i).Select(x => x.f).ToList();
        r.Facts.RemoveRange(1, r.Facts.Count - 1);
        r.Facts.AddRange(rest);
        return r;
    }

    // ------------------------------------------------------------------------------------------------ notes

    /// <summary>The key above the base a note names: the nearest whole key of FL's pitch + the instance's tuning (cents).</summary>
    private static int KeyAboveBase(double flPitch, double tuning) => (int)Math.Round((flPitch + tuning) / 100.0, MidpointRounding.AwayFromZero);

    /// <summary>The MIDI key's pitch in cents from C5 (what the engines and the bends are measured in).</summary>
    public static double C5CentsOfKey(int key) => (key - FlNotes.BaseC5) * 100.0;

    /// <summary>Each started lane's base key (see the file's header): known by the instance; else C5 unless its notes only fit MIDI note 0 (a key above 127 from C5); a lane whose notes fit both
    /// follows the lanes that were sure when they all said MIDI note 0, else C5.</summary>
    private static Dictionary<LaneData, (int Base, BaseWhy How)> ResolveBases(List<LaneData> lanes)
    {
        var kind = new Dictionary<LaneData, BaseWhy>();
        foreach (var lane in lanes)
        {
            double tuning = 0;
            int hi = int.MinValue, lo = int.MaxValue;
            for (int i = 0; i < lane.Count; i++)
            {
                int k = CapEv.Kind(lane.Code[i]);
                if (k == CapEv.Tuning) tuning = lane.A[i];
                else if (k is CapEv.On or CapEv.OnLevelVelocity) { int rel = KeyAboveBase(lane.A[i], tuning); hi = Math.Max(hi, rel); lo = Math.Min(lo, rel); }
            }
            kind[lane] = lane.Identity.BaseHint >= 0 ? BaseWhy.Known
                : hi == int.MinValue ? BaseWhy.Default
                : hi + FlNotes.BaseC5 > 127 && lo >= 0 ? BaseWhy.OnlyMidi0                      // from C5 a key above 127; from MIDI note 0 every key fits
                : lo < 0 ? BaseWhy.OnlyC5                                                       // from MIDI note 0 a key below 0
                : BaseWhy.Default;
        }
        bool sureMidi0 = kind.Values.Any(k => k == BaseWhy.OnlyMidi0), sureC5 = kind.Values.Any(k => k == BaseWhy.OnlyC5);
        var o = new Dictionary<LaneData, (int, BaseWhy)>();
        foreach (var (lane, k) in kind)
        {
            (int, BaseWhy) b = k switch
            {
                BaseWhy.Known => (lane.Identity.BaseHint, k),
                BaseWhy.OnlyMidi0 => (FlNotes.BaseImported, k),
                BaseWhy.OnlyC5 => (FlNotes.BaseC5, k),
                _ => sureMidi0 && !sureC5 ? (FlNotes.BaseImported, BaseWhy.LikeOthers) : (FlNotes.BaseC5, BaseWhy.Default),
            };
            if (CaptureMutants.On(CaptureMutants.BaseFromMidi0)) b = (FlNotes.BaseImported, BaseWhy.Default);                         // (the deliberate bugs)
            else if (CaptureMutants.On(CaptureMutants.BaseDetectionIgnored) && k != BaseWhy.Known) b = (FlNotes.BaseC5, BaseWhy.Default);
            o[lane] = b;
        }
        return o;
    }

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
                    int key = t.Base + KeyAboveBase(lane.A[i], tuning);
                    if (key is < 0 or > 127) { t.KeysClamped++; key = Math.Clamp(key, 0, 127); }
                    double played = FlNotes.ToC5Cents(lane.B[i], t.Base) + tuning;
                    var n = new CapNote { Voice = voice, Start = at, InitCents = lane.A[i], PlayedAtStart = played, Tuning = tuning, VelocityFromLevel = kind == CapEv.OnLevelVelocity, Key = key };
                    double vel = lane.C[i] * (CaptureMutants.On(CaptureMutants.VelocityOver127) ? 127.0 : 128.0);
                    n.Velocity = Math.Clamp((int)Math.Round(vel, MidpointRounding.AwayFromZero), 1, 127);
                    n.Pitch.Add((at, played));
                    open[voice] = n;
                    if (n.VelocityFromLevel) t.LevelVelocities++;
                    break;
                }
                case CapEv.Pitch:
                    if (open.TryGetValue(voice, out var p)) p.Pitch.Add((at, FlNotes.ToC5Cents(lane.A[i], t.Base) + lane.B[i]));
                    break;
                case CapEv.MixTime:
                    if (open.TryGetValue(voice, out var m)) { m.FlBeats = lane.A[i]; m.FlT2 = lane.B[i]; m.HasFlTime = true; }
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
            double keyCents = C5CentsOfKey(n.Key);
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
            bends.Add((at, owner.Current - C5CentsOfKey(owner.Key), owner));
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
        // the base key FL's pitch was read from (FL tells a plugin no root note: Record/FlNotes.cs): said first when a channel is not read from C5, else last
        var from0 = r.Tracks.Where(t => t.BaseHow == BaseWhy.OnlyMidi0).Select(t => t.Name).ToList();
        var like = r.Tracks.Where(t => t.BaseHow == BaseWhy.LikeOthers).Select(t => t.Name).ToList();
        var known = r.Tracks.Where(t => t.BaseHow == BaseWhy.Known).Select(t => $"{t.Name} from {(t.Base == FlNotes.BaseC5 ? "C5" : $"MIDI note {t.Base}")}").ToList();
        string? baseFact = null;
        if (from0.Count > 0)
            baseFact = $"{string.Join(", ", from0)}: {(from0.Count == 1 ? "its notes only fit" : "their notes only fit")} if FL counts the pitch from MIDI note 0 (a channel FL made by importing a MIDI file), so {(from0.Count == 1 ? "it is" : "they are")} read that way"
                + (like.Count > 0 ? $"; {string.Join(", ", like)} too, like the other channels (their notes fit both)" : "");
        if (known.Count > 0) baseFact = (baseFact == null ? "" : baseFact + "; ") + "pitch read as found from the recording's track: " + string.Join(", ", known);
        var fitBoth = r.Tracks.Where(t => t.BaseHow == BaseWhy.Default && t.Base == FlNotes.BaseC5).Select(t => t.Name).ToList();
        if (baseFact != null && fitBoth.Count > 0) baseFact += $"; {string.Join(", ", fitBoth)} read from C5 ({(fitBoth.Count == 1 ? "its" : "their")} notes fit both readings: if {(fitBoth.Count == 1 ? "it was" : "they were")} imported too, {(fitBoth.Count == 1 ? "its keys are" : "their keys are")} 60 too high)";
        if (baseFact != null) r.Facts.Add(baseFact);
        int clampedKeys = r.Tracks.Sum(t => t.KeysClamped);
        if (clampedKeys > 0) r.Facts.Add($"{clampedKeys} note{(clampedKeys == 1 ? "" : "s")} outside MIDI's keys 0-127 at the base used: clamped (the channel's root note may not be C5 or MIDI note 0)");
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
        // FL's own song position at the notes (asked with offset 0, as the SDK says), against the capture's clock: evidence for whether FL gives one
        var asked = r.Tracks.SelectMany(t => t.Notes).Where(n => n.HasFlTime).OrderBy(n => n.Start).ToList();
        if (asked.Count > 0)
        {
            if (asked.All(n => n.FlBeats == 0 && n.FlT2 == CapEv.MixSentinel))
                r.Report.Add($"FL's song position (FHD_GetMixingTime, GT_Beats, offset 0) at {asked.Count} notes: FL left the time as it was (no song position)");
            else
            {
                var diffs = asked.Where(n => double.IsFinite(n.FlBeats)).Select(n => n.FlBeats - n.Start * r.Tempo.Bpm / 60.0).OrderBy(x => x).ToList();
                string first = string.Join(", ", asked.Take(3).Select(n => $"T {n.FlBeats.ToString("0.####", ci)} T2 {n.FlT2.ToString("0.####", ci)} at {n.Start:0.####} s"));
                if (diffs.Count == 0) r.Report.Add($"FL's song position at {asked.Count} notes: not a number ({first})");
                else
                {
                    double med = diffs[diffs.Count / 2], spread = diffs[^1] - diffs[0];
                    r.Report.Add($"FL's song position at {asked.Count} notes ({first}, ...): FL's beats minus the capture's clock: median {med.ToString("0.####", ci)}, spread {spread.ToString("0.####", ci)} beats");
                    r.Facts.Add(spread < 0.02
                        ? $"FL's own song position agrees with the capture's clock at {diffs.Count} notes (within {spread.ToString("0.###", ci)} beats{(Math.Abs(med) > 0.01 ? $"; FL's Play was at beat {med.ToString("0.##", ci)}" : "")})"
                        : $"FL's own song position and the capture's clock differ by up to {spread.ToString("0.###", ci)} beats over {diffs.Count} notes (the host log has the numbers)");
                }
            }
        }
        if (r.Tracks.All(t => t.Base == FlNotes.BaseC5 && t.BaseHow is BaseWhy.Default or BaseWhy.OnlyC5))
            r.Facts.Add("keys read from C5, as FL counts the pitch on channels made in FL (FL tells a plugin no root note; a channel FL made by importing a MIDI file counts from MIDI note 0, which these notes do not show)");
        int silent = lanes.Count(l => l.Started) - r.Tracks.Count;
        if (silent > 0) r.Report.Add($"{silent} channel{(silent == 1 ? "" : "s")} got no note: no track");

        r.Report.Add($"tempo: {r.Tempo.Source}{(r.Tempo.Check != null ? "; " + r.Tempo.Check : "")}; {r.MicrosPerQuarter} us per quarter, {Ppq} ticks per quarter");
        r.Report.Add($"FL's timing at Play: samples per tick {start.SamplesPerTick:0.####}, time signature {(start.TimeSigKnown ? $"{start.StepsPerBar}/{start.StepsPerBeat} steps, PPQ {start.Ppq}" : "unknown")}, FPE_Tempo {(start.HasTempo ? start.Tempo.ToString("0.###", ci) : "none")}, process mode {start.ProcessMode}, {start.Rate:N0} Hz");
        foreach (var t in r.Tracks)
            r.Report.Add($"track \"{t.Name}\" (#{t.Id.InstanceNo}, {t.Id.Detail}): pitch read from {(t.Base == FlNotes.BaseC5 ? "C5" : $"MIDI note {t.Base}")} ({t.BaseHow}), MIDI {t.Channel + 1}, {t.Notes.Count} notes ({t.MovingNotes} move), {t.BendEvents} bends at range {t.BendRange} (largest {t.MaxDev:0.##} cents from the key, 14-bit error at most {t.MaxQuantError:0.###} cents){(t.Clamped > 0 ? $", {t.Clamped} clamped" : "")}{(t.SameKeyCut > 0 ? $", {t.SameKeyCut} same-key cuts" : "")}{(t.EndedAtStop > 0 ? $", {t.EndedAtStop} ended at Stop" : "")}");
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
