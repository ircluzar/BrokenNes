// Round 8 (record-nes): from a NES recording to FL Studio. The notes are what the chip SOUNDED (NesNotes over the chip model), the tracks are a channel and its timbre, and each track's preset plays
// its notes back through the plugin's own arithmetic (NesDirect), so the registers the replay writes are the game's:
//
//   tracks   one per (pulse channel, duty) (e.g. "Pulse 1, 25%"), the triangle, one per noise mode (long / short), one per DMC sample. A note whose duty (or noise mode) changes while it sounds is
//            on the track of EVERY duty it uses, struck at the same moment on each, and silent (controller 7 at 0) on a track while another duty sounds: the instances' waves run in step, so the
//            replay changes the duty exactly when the game did, without restarting the wave. A DMC sample cannot be played by any engine yet: its track holds the notes (when each sample
//            started, its rate as the key) and its preset says why it cannot play.
//   pitch    the key is the musical note of the period the note holds longest; every period it has is reached by the key plus a pitch bend found by searching the 14-bit bend values with the
//            plugin's own period function (NesDirect.PulsePeriod / TrianglePeriod / NoiseIndex on FL's float cents): exact, or listed. The hardware sweep is carried this way (its periods as bends).
//   volume   velocity = the note's level at its start (0-15 shown as velocity; raised when a later level needs it), controller 7 the level after that (100 = as struck), each found with the plugin's
//            own level function (NesDirect.Level): exact. The hardware decay envelope is carried this way (its levels as controller 7 at each quarter frame).
//   time     the notes keep the game's times (CPU cycles, exact); the tempo map (RecTempo) only puts FL's bar lines on the game's beats; values held for less than 2 ms (a period written in two
//            halves) are below FL's control rate and merged into the next, counted.
// Nothing that cannot be replayed is silent: it is counted and becomes a line of the facts.
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes2.Record;

public sealed class NesTrack
{
    public int Index, Channel, Duty = -1, Mode = -1, DmcCopy = -1, MidiChannel;
    public string Name = "";
    public int BendRange = 2;
    /// <summary>Indexes into <see cref="NesTranscription.Notes"/>, in start order (a note of several duties is on each of their tracks).</summary>
    public List<int> Notes = new();
    public List<int> Keys = new(), Velocities = new();
    public SmfTrack Smf = new();
    public PresetData? Preset;
    public byte[]? PresetFile;
    public string? PresetProblem;
    /// <summary>The preset loads but cannot play (the DMC): the reason the window and the preset say.</summary>
    public string? CannotPlay;
    public int PitchValues, PitchExact, VolValues, VolExact, Transients, PitchOutOfRange, Rekeyed, KeyClamped, TooShort, Gated;
    public double MaxPitchError;
    public string Kind => Channel switch { 0 => "Pulse 1", 1 => "Pulse 2", 2 => "Triangle", 3 => "Noise", _ => "DMC" };
}

public sealed class NesOptions
{
    public double FlTempo = 120;
    public int MaxNotes = 200_000, MaxEventsPerNote = 4096;
    public string Name = "Recording";
}

public sealed class NesTranscription
{
    public NesSession Session = null!;
    public List<NesNote> Notes = new();
    public NesNoteStats NoteStats = null!;
    public List<NesTrack> Tracks = new();
    public TempoResult Tempo = new();
    public List<(double Seconds, double Tick, double TicksPerSecond, int MicrosPerQuarter)> Map = new();
    public double FlTempoUsed;
    public byte[] Midi = [];
    public List<string> Facts = new();
    public int NotesThinned, DutyChangeNotes, ModeChangeNotes, EnvelopeNotes, SweepNotes, RateChangeNotes, ShortNotes, TiedCopies;
    /// <summary>The velocity of a note's copy on a track when it is on several (see NesTranscribe.DistinctCopies), by (track, note).</summary>
    public Dictionary<(int Track, int Note), int> VelocityOf = new();
    /// <summary>Per (track, note in the track): the timelines the file plays, as FL can play them (below 2 ms merged): the period (pulse / triangle timer, noise index), the level on this track (0 while another
    /// duty sounds), and the MIDI key. Times in CPU cycles from the recording's start.</summary>
    public Dictionary<(int Track, int Note), List<NesVal>> PeriodT = new(), LevelT = new();
    /// <summary>The values the file does NOT replay exactly: (track, note index, from, to) (a pitch out of the bend range).</summary>
    public List<(int Track, int Note, uint From, uint To)> Inexact = new();

    public bool Reported(int track, int note, uint t) { foreach (var x in Inexact) if (x.Track == track && x.Note == note && t >= x.From && t < x.To) return true; return false; }

    public long TickOf(double seconds)
    {
        var seg = Map[0];
        foreach (var m in Map) if (m.Seconds <= seconds) seg = m; else break;
        return (long)Math.Round(seg.Tick + (seconds - seg.Seconds) * seg.TicksPerSecond);
    }

    public double SecondsOf(double tick)
    {
        var seg = Map[0];
        foreach (var m in Map) if (m.Tick <= tick) seg = m; else break;
        return seg.Seconds + (tick - seg.Tick) / seg.TicksPerSecond;
    }

    public int PitchValues => Tracks.Sum(t => t.PitchValues);
    public int PitchExact => Tracks.Sum(t => t.PitchExact);

    public byte[] TrackMidi(int track)
    {
        var tempos = Map.Select(m => ((long)Math.Round(m.Tick), m.MicrosPerQuarter)).ToList();
        return RecMidi.Write(tempos, Tracks[track].Name, [], [Tracks[track].Smf], singleTrack: true);
    }
}

public static class NesTranscribe
{
    public const int MaxBendRange = 48;
    /// <summary>How long a value must be held to be one for FL: 2 ms in CPU cycles (FL's plugin control step is 64 host samples, 1.3 to 1.5 ms).</summary>
    public const uint MinHold = 3580;
    public static readonly string[] DutyNames = ["12.5%", "25%", "50%", "75%"];

    /// <summary>FL's cents for a key and a 14-bit bend at a bend range, as the float FL hands over (the SNES transcription's RecTranscribe.FlCents).</summary>
    public static double FlCents(int key, int bend, int range) => (float)((key - 60) * 100 + bend * range * 100.0 / 8192.0);

    /// <summary>The value the plugin writes for that pitch on channel <paramref name="channel"/> (0/1 pulse: the timer, below 8 shown as 8 and muted; 2 triangle: the timer; 3 noise: the index).</summary>
    public static int Register(int channel, int key, int bend, int range)
    {
        double c = FlCents(key, bend, range);
        return channel switch { 2 => NesDirect.TrianglePeriod(c), 3 => NesDirect.NoiseIndex(c), _ => Math.Max(8, NesDirect.PulsePeriod(c)) };
    }

    /// <summary>The level the plugin plays for a velocity and a controller 7 value (FL assumed to hand it velocity / 127 x CC7 / 100 as the voice's float level, as for the SNES; the Volume parameter at 100 %).</summary>
    public static int PlayedLevel(int velocity, int cc) => NesDirect.Level((float)(velocity / 127.0 * cc / 100.0), 1.0);

    /// <summary>Every bend that makes the plugin write <paramref name="p"/> for <paramref name="key"/> (contiguous: the register is monotonic in the bend), the one nearest the middle, and whether any does.</summary>
    public static (int Lo, int Hi, int Ideal, bool Exact, double ErrCents) BendInterval(int channel, int p, int key, int range)
    {
        // the cents the value needs, from the key (pulse / triangle: the period's pitch; noise: the middle of its index's three semitones)
        double want = channel == 3 ? (NesDirect.NoiseKey(p) + 0.5 - key) * 100 : (NesDirect.MidiOf(p, channel == 2) - key) * 100;
        int b0 = Math.Clamp((int)Math.Round(want * 8192.0 / (range * 100.0)), -8192, 8191);
        int best = b0, bestErr = int.MaxValue;
        for (int d = 0; d <= 64 && bestErr != 0; d++)
        {
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                int b = b0 + sgn * d;
                if (b < -8192 || b > 8191) continue;
                int err = Math.Abs(Register(channel, key, b, range) - p);
                if (err < bestErr) { bestErr = err; best = b; }
                if (err == 0 || d == 0) break;
            }
        }
        if (bestErr != 0)
        {
            int got = Register(channel, key, best, range);
            double e = channel == 3 ? Math.Abs(got - p) * 300.0 : Math.Abs(NesDirect.MidiOf(got, channel == 2) - NesDirect.MidiOf(p, channel == 2)) * 100;
            return (best, best, best, false, e);
        }
        int lo = best, hi = best;
        while (lo > -8192 && Register(channel, key, lo - 1, range) == p) lo--;
        while (hi < 8191 && Register(channel, key, hi + 1, range) == p) hi++;
        if (RecMutants.On(NesTranscribeMutants.BendOffByOne)) { int w = hi - lo + 1; int s = channel == 3 || channel == 2 ? w : -w; return (Math.Clamp(lo + s, -8192, 8191), Math.Clamp(hi + s, -8192, 8191), Math.Clamp((lo + hi) / 2 + s, -8192, 8191), true, 0); }
        return (lo, hi, (lo + hi) / 2, true, 0);
    }

    /// <summary>Every controller 7 value that plays <paramref name="level"/> at <paramref name="velocity"/>, the one nearest 100 x level / (the velocity's level), and whether any does.</summary>
    public static (int Lo, int Hi, int Ideal, bool Exact) CcInterval(int level, int velocity)
    {
        int lo = -1, hi = -1;
        for (int cc = 0; cc <= 127; cc++) if (PlayedLevel(velocity, cc) == level) { if (lo < 0) lo = cc; hi = cc; }
        if (lo < 0) return (0, 0, 0, false);
        double full = 15.0 * velocity / 127.0;
        int ideal = Math.Clamp((int)Math.Round(100.0 * level / Math.Max(1e-9, full)), lo, hi);
        if (RecMutants.On(NesTranscribeMutants.VolumeOffByOne) && level > 0) ideal = Math.Min(127, hi + 1);
        return (lo, hi, ideal, true);
    }

    /// <summary>The smallest velocity whose level is <paramref name="level"/> at controller 7 = 100 (the piano roll then shows the struck level); 1 for level 0.</summary>
    public static int VelocityFor(int level)
    {
        for (int v = 1; v <= 127; v++) if (PlayedLevel(v, 100) >= level) return v;
        return 127;
    }

    /// <summary>A timeline as FL can play it: a run of values each held for less than <see cref="MinHold"/> that lasts less than MinHold in all is a transient (the value after it starts where it started);
    /// a longer run of short values is sampled every MinHold. <paramref name="transient"/> counts the values not kept as themselves.</summary>
    public static List<NesVal> Smooth(IReadOnlyList<NesVal> values, uint end, out int transient)
    {
        transient = 0;
        var kept = new List<NesVal>();
        uint Hold(int k) => (k + 1 < values.Count ? values[k + 1].T : Math.Max(end, values[k].T)) - values[k].T;
        int i = 0;
        while (i < values.Count)
        {
            if (Hold(i) >= MinHold || i == values.Count - 1 && kept.Count == 0) { Add(values[i].T, values[i].V); i++; continue; }
            int j = i;
            while (j < values.Count && Hold(j) < MinHold) j++;
            uint runStart = values[i].T, runEnd = j < values.Count ? values[j].T : Math.Max(end, values[j - 1].T);
            transient += j - i;
            if (runEnd - runStart < MinHold)
            {
                if (j < values.Count) { Add(runStart, values[j].V); i = j + 1; }
                else { Add(runStart, values[j - 1].V); i = j; }
            }
            else
            {
                int k = i;
                for (uint at = runStart; at < runEnd; at += MinHold)
                {
                    while (k + 1 < j && values[k + 1].T <= at) k++;
                    Add(at, values[k].V);
                }
                i = j;
            }
        }
        return kept;

        void Add(uint at, int v)
        {
            if (kept.Count > 0 && kept[^1].V == v) return;
            if (kept.Count > 0 && kept[^1].T == at) { kept[^1] = new NesVal(at, v); return; }
            kept.Add(new NesVal(at, v));
        }
    }

    public static int ChannelOf(int track) => RecTranscribe.ChannelOf(track);

    public static NesTranscription Run(NesSession s, NesOptions o)
    {
        var tr = new NesTranscription { Session = s };
        tr.Notes = NesNotes.Extract(s, out var ns);
        tr.NoteStats = ns;
        // notes too short for FL's control rate are not notes (a channel audible for a few cycles between two writes): left out, counted
        tr.ShortNotes = tr.Notes.RemoveAll(n => n.End - n.Start < MinHold && n.Channel != 4);
        if (tr.Notes.Count > o.MaxNotes) { tr.NotesThinned = tr.Notes.Count - o.MaxNotes; tr.Notes.RemoveRange(o.MaxNotes, tr.Notes.Count - o.MaxNotes); }

        // ---- tempo and the tick map ----
        tr.Tempo = RecTempo.Estimate(tr.Notes.Select(n => n.StartSeconds), s.Seconds);
        BuildMap(tr, o);

        // ---- tracks: (channel, duty) for the pulses, the triangle, (noise, mode), (DMC, sample), in the order they first sound ----
        var keyOf = new Dictionary<(int Ch, int Sub), NesTrack>();
        var firstOf = new Dictionary<NesTrack, uint>();
        var dmcSame = DmcContents(s);
        NesTrack TrackFor(int ch, int sub, uint first)
        {
            if (keyOf.TryGetValue((ch, sub), out var t)) return t;
            t = new NesTrack { Channel = ch };
            if (ch < 2) t.Duty = sub; else if (ch == 3) t.Mode = sub; else if (ch == 4) t.DmcCopy = sub;
            keyOf[(ch, sub)] = t;
            firstOf[t] = first;
            return t;
        }
        var noteTracks = new List<(int Note, NesTrack Track)>();
        for (int i = 0; i < tr.Notes.Count; i++)
        {
            var n = tr.Notes[i];
            if (n.Channel < 2)
            {
                if (n.Duties.Select(d => d.V).Distinct().Count() > 1) tr.DutyChangeNotes++;
                foreach (int d in n.Duties.Select(x => x.V).Distinct().OrderBy(x => x)) noteTracks.Add((i, TrackFor(n.Channel, d, n.Start)));
            }
            else if (n.Channel == 3)
            {
                if (n.Modes.Select(d => d.V).Distinct().Count() > 1) tr.ModeChangeNotes++;
                foreach (int md in n.Modes.Select(x => x.V).Distinct().OrderBy(x => x)) noteTracks.Add((i, TrackFor(3, md, n.Start)));
            }
            else if (n.Channel == 2) noteTracks.Add((i, TrackFor(2, 0, n.Start)));
            else noteTracks.Add((i, TrackFor(4, n.DmcCopy >= 0 ? dmcSame[n.DmcCopy] : -1, n.Start)));
            if (n.EnvelopeSteps > 0) tr.EnvelopeNotes++;
            if (n.SweepSteps > 0) tr.SweepNotes++;
            if (n.DmcRateChanges > 0) tr.RateChangeNotes++;
        }
        foreach (var (t, k) in firstOf.OrderBy(x => x.Value).ThenBy(x => x.Key.Channel).ThenBy(x => x.Key.Duty + x.Key.Mode + x.Key.DmcCopy).Select((x, k) => (x.Key, k)))
        {
            t.Index = k;
            t.MidiChannel = ChannelOf(k);
            t.Name = $"{o.Name} - {k + 1:00} {TrackLabel(t, s)}";
            if (t.Name.Length > PresetFile.MaxName) t.Name = t.Name[^PresetFile.MaxName..];
            tr.Tracks.Add(t);
        }
        tr.Tracks.Sort((a, b) => a.Index.CompareTo(b.Index));
        foreach (var (note, t) in noteTracks) t.Notes.Add(note);

        tr.TiedCopies = DistinctCopies(tr);
        foreach (var t in tr.Tracks) Channel(tr, t, o);
        // past 15 tracks, channels are shared by tracks whose first notes differ (FL's note colour is the channel: reload, RecTranscribe.ShareChannels)
        if (tr.Tracks.Count > 15)
        {
            var chans = RecTranscribe.ShareChannels(tr.Tracks.Select(t => RecTranscribe.KeysOf(t.Smf)).ToList());
            for (int k = 0; k < tr.Tracks.Count; k++) if (tr.Tracks[k].MidiChannel != chans[k]) { tr.Tracks[k].MidiChannel = chans[k]; RecTranscribe.SetChannel(tr.Tracks[k].Smf, chans[k]); }
        }
        foreach (var t in tr.Tracks) MakePreset(tr, t);

        var tempos = tr.Map.Select(m => ((long)Math.Round(m.Tick), m.MicrosPerQuarter)).ToList();
        var texts = new List<string> { "BrokenNes2 recording of " + (s.Game.Length > 0 ? s.Game : "a NES game"), "tempo: " + tr.Tempo.Describe() };
        tr.Midi = RecMidi.Write(tempos, o.Name, texts, tr.Tracks.Select(x => x.Smf).ToList());
        Facts(tr);
        return tr;
    }

    private static string TrackLabel(NesTrack t, NesSession s) => t.Channel switch
    {
        0 or 1 => $"{t.Kind}, {DutyNames[t.Duty & 3]}",
        2 => "Triangle",
        3 => t.Mode == 1 ? "Noise (short)" : "Noise",
        _ => t.DmcCopy >= 0 && t.DmcCopy < s.Dmc.Length ? $"DMC sample ${s.Dmc[t.DmcCopy].Address:X4} ({s.Dmc[t.DmcCopy].Length} bytes)" : "DMC (no copy of its sample)",
    };

    /// <summary>For each DMC copy, the first copy with the same bytes (one instrument however many addresses it was started from).</summary>
    private static int[] DmcContents(NesSession s)
    {
        var same = new int[s.Dmc.Length];
        for (int i = 0; i < s.Dmc.Length; i++)
        {
            same[i] = i;
            for (int j = 0; j < i; j++) if (s.Dmc[j].Data.SequenceEqual(s.Dmc[i].Data)) { same[i] = same[j]; break; }
        }
        return same;
    }

    private static void BuildMap(NesTranscription tr, NesOptions o)
    {
        const int ppq = RecMidi.Ppq;
        var tempo = tr.Tempo;
        if (!tempo.Found || RecMutants.On(NesTranscribeMutants.TempoIgnored))
        {
            double bpm = Math.Clamp(o.FlTempo, 10, 999);
            int us = (int)Math.Round(60_000_000 / bpm);
            tr.FlTempoUsed = bpm;
            tr.Map.Add((0, 0, ppq * 1e6 / us, us));
            return;
        }
        double bar = 4.0 * ppq;
        double TickAt(double sec)
        {
            var seg = tr.Map[0];
            foreach (var m in tr.Map) if (m.Seconds <= sec) seg = m; else break;
            return seg.Tick + (sec - seg.Seconds) * seg.TicksPerSecond;
        }
        double prevDown = 0, prevBar = 0;
        for (int i = 0; i < tempo.Segments.Count; i++)
        {
            var seg = tempo.Segments[i];
            double bpm = seg.Bpm;
            int us = (int)Math.Round(60_000_000 / bpm);
            double rate = ppq * 1e6 / us;
            if (i == 0)
            {
                double atDown = seg.DownbeatSeconds * rate;
                double lead = Math.Ceiling(atDown / bar - 1e-9) * bar - atDown;
                tr.Map.Add((0, lead, rate, us));
            }
            else
            {
                double barLine = prevDown + Math.Floor((seg.Seconds - prevDown) / prevBar) * prevBar;
                double dt = seg.DownbeatSeconds - barLine;
                int nBars = Math.Max(1, (int)Math.Round(dt / prevBar));
                int usB = dt > 0 ? (int)Math.Round(dt * 1e6 / (nBars * 4)) : 0;
                if (dt > 0.05 && usB is >= 100_000 and <= 4_000_000 && barLine >= tr.Map[^1].Seconds)
                {
                    tr.Map.Add((barLine, TickAt(barLine), ppq * 1e6 / usB, usB));
                    tr.Map.Add((seg.DownbeatSeconds, TickAt(seg.DownbeatSeconds), rate, us));
                }
                else tr.Map.Add((seg.Seconds, TickAt(seg.Seconds), rate, us));
            }
            prevDown = seg.DownbeatSeconds;
            prevBar = 4 * 60.0 / bpm;
        }
    }

    /// <summary>A track's events: per note its key, velocity, bend range, the bends and controller 7 values that give each value exactly; a note of several duties is silent here while another duty sounds.</summary>
    private static void Channel(NesTranscription tr, NesTrack t, NesOptions o)
    {
        int ch = t.Channel;
        var ev = t.Smf.Events;
        int mc = t.MidiChannel;
        // the timelines of each note on this track, as FL can play them
        var periods = new List<List<NesVal>>(); var levels = new List<List<NesVal>>();
        double maxDev = 0;
        foreach (int ni in t.Notes)
        {
            var n = tr.Notes[ni];
            var pl = Smooth(n.Periods, n.End, out int tp);
            var vl = LevelLine(n, t, out int tv, out bool gated);
            if (gated) t.Gated++;
            t.Transients += tp + tv;
            if (pl.Count > o.MaxEventsPerNote) pl.RemoveRange(o.MaxEventsPerNote, pl.Count - o.MaxEventsPerNote);
            if (vl.Count > o.MaxEventsPerNote) vl.RemoveRange(o.MaxEventsPerNote, vl.Count - o.MaxEventsPerNote);
            periods.Add(pl); levels.Add(vl);
            tr.PeriodT[(t.Index, ni)] = pl; tr.LevelT[(t.Index, ni)] = vl;
            // the key: the note of the period held longest
            int dom = n.Dominant(n.Periods);
            double Midi(int p) => ch switch { 3 => NesDirect.NoiseKey(p) + 0.5, 4 => 48 + p, _ => NesDirect.MidiOf(p, ch == 2) };
            int key = ch == 3 ? NesDirect.NoiseKey(dom) : (int)Math.Round(Midi(dom));
            double lo = pl.Min(p => Midi(p.V)), hi = pl.Max(p => Midi(p.V));
            if (Math.Max(hi - key, key - lo) > MaxBendRange - 0.5) { key = (int)Math.Round((lo + hi) / 2); t.Rekeyed++; }
            if (key is < 0 or > 127) { t.KeyClamped++; key = Math.Clamp(key, 0, 127); }
            t.Keys.Add(key);
            if (ch != 4) maxDev = Math.Max(maxDev, Math.Max(hi - key, key - lo) * 100);
        }
        t.BendRange = Math.Clamp((int)Math.Ceiling((maxDev + 1) / 100.0), 2, MaxBendRange);
        if (RecMutants.On(NesTranscribeMutants.NoBendRange) && t.Index == 0) { }
        else ev.AddRange(RecMidi.BendRange(0, mc, t.BendRange));
        ev.Add(RecMidi.Cc(0, mc, 7, 100));
        ev.Add(RecMidi.PitchBend(0, mc, 0));
        int curBend = 0, curCc = 100;
        long lastEnd = -1;
        for (int k = 0; k < t.Notes.Count; k++)
        {
            int ni = t.Notes[k];
            var n = tr.Notes[ni];
            int key = t.Keys[k];
            var pl = periods[k]; var vl = levels[k];
            long start = tr.TickOf(n.StartSeconds), end = tr.TickOf(n.End / NesSession.CpuHz);
            if (RecMutants.On(NesTranscribeMutants.NotesQuantised)) { long g = RecMidi.Ppq / 4; start = (start + g / 2) / g * g; }
            if (start < lastEnd) start = lastEnd;                // (a channel's notes never overlap; the tick map's rounding must not make two touch out of order)
            if (end <= start) { end = start + 1; t.TooShort++; }
            lastEnd = end;
            // velocity: the struck level, raised when a later level needs more than controller 7 can give (a note on several duty tracks: each copy its own, see DistinctCopies)
            int vel = tr.VelocityOf.TryGetValue((t.Index, ni), out int forced) ? forced : DefaultVelocity(ch, vl);
            t.Velocities.Add(vel);
            // the values: (tick, kind 0 bend / 1 cc, value index)
            var bends = new (int Lo, int Hi, int Ideal, bool Exact)[pl.Count];
            for (int q = 0; q < pl.Count; q++)
            {
                if (ch == 4) { bends[q] = (0, 0, 0, true); continue; }
                var (lo2, hi2, ideal, exact, err) = BendInterval(ch, pl[q].V, key, t.BendRange);
                bends[q] = (lo2, hi2, ideal, exact);
                t.PitchValues++;
                if (exact) t.PitchExact++;
                else
                {
                    t.MaxPitchError = Math.Max(t.MaxPitchError, err);
                    t.PitchOutOfRange++;
                    uint to = q + 1 < pl.Count ? pl[q + 1].T : Math.Max(n.End, pl[q].T + 1);
                    tr.Inexact.Add((t.Index, ni, pl[q].T, to));
                }
            }
            var ccs = new (int Lo, int Hi, int Ideal, bool Exact)[vl.Count];
            for (int q = 0; q < vl.Count; q++)
            {
                ccs[q] = ch is 2 or 4 ? (100, 100, 100, true) : CcInterval(vl[q].V, vel);
                t.VolValues++;
                if (ccs[q].Exact) t.VolExact++;
            }
            // at the start: the first bend and controller 7, then the note
            int b0 = curBend >= bends[0].Lo && curBend <= bends[0].Hi ? curBend : bends[0].Ideal;
            if (b0 != curBend) { ev.Add(RecMidi.PitchBend(start, mc, b0)); curBend = b0; }
            int c0 = curCc >= ccs[0].Lo && curCc <= ccs[0].Hi ? curCc : ccs[0].Ideal;
            if (c0 != curCc) { ev.Add(RecMidi.Cc(start, mc, 7, c0)); curCc = c0; }
            ev.Add(RecMidi.On(start, mc, key, vel));
            var later = new List<(long Tick, int Kind, int Q)>();
            for (int q = 1; q < pl.Count; q++) later.Add((Math.Clamp(tr.TickOf(pl[q].T / NesSession.CpuHz), start, end), 0, q));
            for (int q = 1; q < vl.Count; q++) later.Add((Math.Clamp(tr.TickOf(vl[q].T / NesSession.CpuHz), start, end), 1, q));
            later.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : a.Q.CompareTo(b.Q));
            foreach (var (tick, kind, q) in later)
            {
                if (kind == 0)
                {
                    int b = curBend >= bends[q].Lo && curBend <= bends[q].Hi ? curBend : bends[q].Ideal;
                    if (b != curBend) { ev.Add(RecMidi.PitchBend(tick, mc, b)); curBend = b; }
                }
                else
                {
                    int c = curCc >= ccs[q].Lo && curCc <= ccs[q].Hi ? curCc : ccs[q].Ideal;
                    if (c != curCc) { ev.Add(RecMidi.Cc(tick, mc, 7, c)); curCc = c; }
                }
            }
            ev.Add(RecMidi.Off(end, mc, key));
        }
        t.Smf.Name = t.Name;
        t.Smf.Instrument = t.Name;
        t.Smf.Texts.Add($"BrokenNes2: {TrackLabel(t, tr.Session)}, pitch-bend range {t.BendRange} semitones (RPN 0); controller 7 is the volume (100 = as struck)");
    }

    /// <summary>A note's level on a track, as FL can play it: the volume while the track's duty (noise mode) is the one sounding, else 0; the triangle and the DMC at 15.</summary>
    public static List<NesVal> LevelLine(NesNote n, NesTrack t, out int transients, out bool gated)
    {
        int ch = t.Channel;
        List<NesVal> raw;
        gated = false;
        if (ch < 2 || ch == 3)
        {
            var sel = ch < 2 ? n.Duties : n.Modes;
            int mine = ch < 2 ? t.Duty : t.Mode;
            var times = n.Vols.Select(v => v.T).Concat(sel.Select(d => d.T)).Distinct().OrderBy(x => x).ToList();
            raw = times.Select(x => new NesVal(x, NesNote.At(sel, x) == mine ? NesNote.At(n.Vols, x) : 0)).ToList();
            gated = raw.Any(v => v.V == 0) && sel.Count > 1;
        }
        else raw = [new NesVal(n.Start, 15)];
        return Smooth(Compact(raw), n.End, out transients);
    }

    /// <summary>The velocity of a note on a track: its level when struck, raised until controller 7 (at most 127) reaches its loudest level; the triangle and the DMC 127.</summary>
    public static int DefaultVelocity(int ch, List<NesVal> vl)
    {
        if (ch is 2 or 4) return 127;
        int peak = vl.Max(v => v.V), first = vl[0].V;
        int vel = Math.Max(VelocityFor(first), VelocityFor((int)Math.Ceiling(peak * 100 / 127.0)));
        while (vel < 127 && PlayedLevel(vel, 127) < peak) vel++;
        return vel;
    }

    /// <summary>A note whose duty (noise mode) changes while it sounds is on the track of each duty, struck at the same moment with the same key: for FL's import-and-Replace (each channel recognises
    /// its track from its notes, Record/RecTracks.cs) the copies get velocities at least 3 apart, so FL's velocity tells the tracks apart. Exactness is kept: controller 7 is solved for each copy's own
    /// velocity (any velocity that reaches the copy's loudest level can play every level exactly). Returns the copies that could not be made distinct.</summary>
    private static int DistinctCopies(NesTranscription tr)
    {
        int tied = 0;
        if (RecMutants.On(NesTranscribeMutants.CopiesNotDistinct)) return 0;          // (the deliberate bug: every copy keeps its own default velocity)
        var byNote = new Dictionary<int, List<NesTrack>>();
        foreach (var t in tr.Tracks) foreach (int ni in t.Notes) { if (!byNote.TryGetValue(ni, out var l)) byNote[ni] = l = new(); l.Add(t); }
        foreach (var (ni, ts) in byNote)
        {
            if (ts.Count < 2) continue;
            var used = new List<int>();
            foreach (var t in ts.OrderBy(x => x.Index))
            {
                var vl = LevelLine(tr.Notes[ni], t, out _, out _);
                int floor = DefaultVelocity(t.Channel, vl), vel = floor;
                while (used.Any(u => Math.Abs(u - vel) < 3) && vel < 127) vel++;
                if (used.Any(u => Math.Abs(u - vel) < 3)) { tied++; vel = floor; }
                used.Add(vel);
                tr.VelocityOf[(t.Index, ni)] = vel;
            }
        }
        return tied;
    }

    private static List<NesVal> Compact(List<NesVal> raw)
    {
        var o = new List<NesVal>();
        foreach (var v in raw)
        {
            if (o.Count > 0 && o[^1].V == v.V) continue;
            if (o.Count > 0 && o[^1].T == v.T) { o[^1] = v; continue; }
            o.Add(v);
        }
        return o;
    }

    /// <summary>The track's preset: a NES instrument of its channel with its duty (or noise mode), constant volume, length halted, no sweep (the registers Direct mode writes), and the note behaviour
    /// of a recorded track (PresetNote: version 3): FL's velocity and controller 7 are the chip's volume register and the pitch is NesDirect's, so the notes replay as the game played them.</summary>
    private static void MakePreset(NesTranscription tr, NesTrack t)
    {
        var s = tr.Session;
        var kind = t.Channel switch { 0 => InstrumentKind.NesPulse1, 1 => InstrumentKind.NesPulse2, 2 => InstrumentKind.NesTriangle, 3 => InstrumentKind.NesNoise, _ => InstrumentKind.NesDmc };
        int blob = t.Channel == 4 && t.DmcCopy >= 0 && t.DmcCopy < s.Dmc.Length ? s.Dmc[t.DmcCopy].Length : 0;
        var inst = NesInstruments.Template(kind, blob);
        inst.Name = t.Name.Length > PresetFile.MaxName ? t.Name[..PresetFile.MaxName] : t.Name;
        void Set(string id, int value) { var f = inst.Field(id); if (f != null) f.Value = f.Clamp(value); }
        switch (t.Channel)
        {
            case 0: case 1:
                Set("duty", RecMutants.On(NesTranscribeMutants.DutyDropped) ? 2 : t.Duty); Set("halt", 1); Set("const", 1); Set("vol", 15);
                Set("sweep.on", 0); Set("sweep.period", 0); Set("sweep.neg", 1); Set("sweep.shift", 0); Set("length", 31);
                break;
            case 2: Set("control", 1); Set("linear", 127); Set("length", 31); break;
            case 3: Set("halt", 1); Set("const", 1); Set("vol", 15); Set("mode", t.Mode); Set("period", 8); Set("length", 31); break;
            default:
                if (t.DmcCopy >= 0 && t.DmcCopy < s.Dmc.Length)
                {
                    var d = s.Dmc[t.DmcCopy];
                    inst.Blobs[0].Data = d.Data.ToArray();
                    Set("addr", (d.Address - 0xC000) >> 6); Set("len", (d.Length - 1) >> 4);
                }
                t.CannotPlay = "cannot play on this console yet: the NES DMC reads its sample from the game's memory and Direct mode has no game to read it from";
                break;
        }
        var packed = InstrumentCodec.Pack(inst);
        var history = new InstrumentHistory(inst).Serialize();
        string notes = $"Recorded from {(s.Game.Length > 0 ? s.Game : "a NES game")} by BrokenNes2: {TrackLabel(t, s)}. The MIDI track of the same name plays it as the game did: pitch-bend range {t.BendRange} semitones (set FL's channel pitch range to {t.BendRange} if its import did not), controller 7 = the volume.";
        var tags = new List<string> { "nes", "recording" };
        string rec = PresetStore.SafeFileName(s.Name);
        if (rec.Length is > 0 and <= PresetFile.MaxTagLength) tags.Add(rec);
        var d2 = PresetMaker.Make(inst.Name, inst.Name, s.Game.Length > PresetFile.MaxGame ? s.Game[..PresetFile.MaxGame] : s.Game, s.Sha1.All(Uri.IsHexDigit) && s.Sha1.Length <= 40 ? s.Sha1 : "", notes.Length > PresetFile.MaxNotes ? notes[..PresetFile.MaxNotes] : notes,
            s.CreatedUnix, packed, packed, history, [], out string? problem, tags.ToArray());
        if (d2 == null) { t.PresetProblem = problem; return; }
        if (t.Channel != 4 && !RecMutants.On(NesTranscribeMutants.TrackBehaviourDropped)) PresetNote.AttachNesTrack(d2);
        var bytes = PresetFile.TryWrite(d2, out problem);
        if (bytes == null) { t.PresetProblem = problem; return; }
        t.Preset = d2;
        t.PresetFile = bytes;
    }

    private static void Facts(NesTranscription tr)
    {
        var f = tr.Facts;
        var s = tr.Session;
        var st = tr.NoteStats;
        f.Add($"{tr.Notes.Count:N0} notes on {tr.Tracks.Count} track{(tr.Tracks.Count == 1 ? "" : "s")} in {s.Seconds:0.0} s ({st.Writes:N0} chip writes, {st.Frames:N0} frames)");
        if (tr.Notes.Count == 0) f.Add(st.Struck == 0 && st.StruckSilent == 0 ? "no note: the game sounded no channel while it was recorded" : $"no note: the game struck {st.StruckSilent} notes, but none of them made a sound");
        f.Add(tr.Tempo.Found ? $"tempo {tr.Tempo.Describe()}: set FL's tempo to {60_000_000.0 / tr.Map[0].MicrosPerQuarter:0.###} BPM" : $"{tr.Tempo.Describe()}: the notes are at their real times at FL's tempo ({tr.FlTempoUsed:0.##} BPM); keep FL at that tempo");
        int pv = tr.PitchValues, pe = tr.PitchExact, vv = tr.Tracks.Sum(t => t.VolValues), ve = tr.Tracks.Sum(t => t.VolExact), trans = tr.Tracks.Sum(t => t.Transients);
        double worst = tr.Tracks.Count == 0 ? 0 : tr.Tracks.Max(t => t.MaxPitchError);
        f.Add((pe == pv ? $"pitch: all {pv:N0} period values are replayed exactly (key + bend)" : $"pitch: {pe:N0} of {pv:N0} period values exact; {pv - pe:N0} out of the bend range, off by up to {worst:0.##} cents")
            + (ve == vv ? $"; volume: all {vv:N0} levels exact (velocity + controller 7)" : $"; volume: {ve:N0} of {vv:N0} levels exact")
            + (trans > 0 ? $"; {trans:N0} values held under 2 ms (below FL's control rate) merged into the next" : ""));
        var hw = new List<string>();
        if (tr.EnvelopeNotes > 0) hw.Add($"{tr.EnvelopeNotes} notes shaped by the hardware envelope (its levels are controller 7, step by step)");
        if (tr.SweepNotes > 0) hw.Add($"{tr.SweepNotes} notes moved by the hardware sweep (its periods are pitch bends)");
        int ends = st.Ends[(int)NesEnd.LengthCounter] + st.Ends[(int)NesEnd.LinearCounter];
        if (ends > 0) hw.Add($"{ends} notes ended by the length or linear counter (where the chip ended them)");
        if (hw.Count > 0) f.Add("the chip's own automation, replayed as values: " + string.Join("; ", hw));
        if (tr.DutyChangeNotes > 0 || tr.ModeChangeNotes > 0)
            f.Add($"{tr.DutyChangeNotes + tr.ModeChangeNotes} notes changed duty (or noise mode) while sounding: they are on the track of each duty they use, struck together, silent (controller 7 at 0) while the other sounds, so the duty changes when the game changed it and the wave does not restart; each copy has a velocity of its own, so a replaced FL channel tells its track from the other"
                + (tr.TiedCopies > 0 ? $" ({tr.TiedCopies} copies could not be given one: those channels may need Play here)" : ""));
        var lost = new List<string>();
        if (s.Dropped > 0) lost.Add($"{s.Dropped:N0} chip writes lost (the capture ran out of memory blocks)");
        if (s.EndedBy == RecEnd.MemoryCap) lost.Add(s.TimeCap ? "the recording stopped at its longest (39 minutes)" : "the recording stopped at the memory cap");
        if (tr.NotesThinned > 0) lost.Add($"{tr.NotesThinned:N0} notes past the limit of the file left out");
        if (tr.ShortNotes > 0) lost.Add($"{tr.ShortNotes:N0} notes shorter than 2 ms left out (below FL's control rate)");
        if (st.BeforeStart > 0) lost.Add(st.BeforeStart == 1 ? "1 note was already sounding when it started (it starts at its first moment)" : $"{st.BeforeStart} notes were already sounding when it started (they start at its first moment)");
        if (!s.BaseExact) lost.Add($"the game ran on the {s.Chip.Replace("APU_", "")} chip, which cannot report its counters: the recording's first notes start as if just struck, and the transcription follows the FIX chip's rules");
        else if (s.Chip.Length > 0 && s.Chip != "APU_FIX") lost.Add($"the game ran on the {s.Chip.Replace("APU_", "")} chip; the presets play on the FIX chip");
        if (lost.Count > 0) f.Add(string.Join("; ", lost));
        int dmcTracks = tr.Tracks.Count(t => t.Channel == 4);
        if (dmcTracks > 0 || st.PcmFrames > 0)
            f.Add((dmcTracks > 0 ? $"{st.DmcStarts} DMC samples played ({dmcTracks} track{(dmcTracks == 1 ? "" : "s")}): no engine can play the NES DMC yet, their presets say so" + (tr.RateChangeNotes > 0 ? $"; {tr.RateChangeNotes} changed rate while playing" : "") + (st.DmcNoCopy > 0 ? $"; {st.DmcNoCopy} with no copy of their bytes" : "") : "")
                + (st.PcmFrames > 0 ? $"{(dmcTracks > 0 ? "; " : "")}raw sound written straight to the DMC's level ($4011, {st.Pcm4011:N0} writes in {st.PcmFrames} frames): not transcribed" : ""));
        int presets = tr.Tracks.Count(t => t.PresetFile != null);
        f.Add(presets == tr.Tracks.Count ? $"{presets} presets, one per track, named like their tracks" : $"{presets} presets; {tr.Tracks.Count - presets} tracks have none: " + string.Join("; ", tr.Tracks.Where(t => t.PresetProblem != null).Select(t => $"{t.Index + 1:00}: {t.PresetProblem}").Take(3)));
    }
}

/// <summary>The NES transcription's deliberate bugs (codes in <see cref="RecMutants"/>' space).</summary>
public static class NesTranscribeMutants
{
    public const int BendOffByOne = 111, VolumeOffByOne = 112, TempoIgnored = 113, NoBendRange = 114, NotesQuantised = 115, DutyDropped = 116, TrackBehaviourDropped = 117, CopiesNotDistinct = 118;
}
