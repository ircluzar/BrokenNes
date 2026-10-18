// Round 8 (record): from a recording to FL Studio. One TRACK per instrument (a sample number at one address: RecVersion), each with its own MIDI channel, its notes in musical pitch, the
// driver's pitch moves as pitch bends, its volume as velocity plus controller 7, and a PRESET (round 6 format) that plays the notes back as the game played them:
//
//   pitch    The plugin plays a note of a preset at register round($1000 x 2^((FL cents + tuning) / 1200)) (SnesSampler.PitchForTuned: the tuning added before the one rounding). The
//            tuning is chosen from the sample's root note (RecRoot), so the MIDI keys are the musical notes; every pitch the game wrote is then reached by the key plus a pitch bend,
//            found by searching the 14-bit bend values with THAT function (exact, not estimated). The bend range is written with RPN 0 and is the smallest (at least 2 semitones) that
//            holds every move of the track; up to 8 semitones every register is reachable (a bend step is 0.098 cents, a register step at least 0.106), above it a few top registers may
//            not be (counted).
//   volume   The preset's left / right weights are the instrument's most common balance; velocity is the note's level at the key-on (raised when the note later swells past 127 % of it),
//            controller 7 the level after that relative to the velocity (100 = as keyed). FL is assumed to hand the plugin velocity / 127 x CC7 / 100 as the voice's level: not tested in FL.
//   time     The notes keep the game's times exactly (no quantising): the tempo map (RecTempo) only puts FL's bar lines where the game's beats are. With no tempo found, the file is at FL's
//            own tempo and the notes are at their real times.
//
// What one MIDI channel cannot carry is counted, never hidden: two notes of one instrument sounding at once share the channel's bend and controller 7 (the newest note owns them; an
// older note that needed another value is "disturbed"); a key re-struck on another voice before the first ended; a sample switched, an envelope rewritten or a flag changed under a sounding
// note; pitch modulation (it needs the neighbouring voice); a pitch outside the bend range.
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes2.Record;

public sealed class RecTrack
{
    public int Index, Version, Srcn, Start, Loop, Channel;
    public string Name = "";
    public RootEstimate Root;
    /// <summary>The preset's tuning (cents): register $1000 sounds as MIDI 60 - Tune / 100.</summary>
    public int Tune;
    /// <summary>The driver's own fraction of a semitone (cents) and how much the notes agree on it (1 = all).</summary>
    public double DriverCents, DriverConcentration;
    public int BendRange;
    public int WeightL = 127, WeightR = 127;
    public int Adsr1, Adsr2, Gain;
    public bool Echo, Noise;
    public bool SampleEnded;
    public byte[] Sample = [];
    /// <summary>Indexes into <see cref="Transcription.Notes"/>, in key-on order.</summary>
    public List<int> Notes = new();
    public List<int> Keys = new(), Velocities = new();
    public SmfTrack Smf = new();
    public PresetData? Preset;
    public byte[]? PresetFile;
    public string? PresetProblem;
    // counts
    /// <summary>Pitch values (as FL can play them: see RecTranscribe.Smooth), those replayed exactly, those a note sharing the channel could not have (and volumes the same), values below FL's control rate merged or sampled.</summary>
    public int PitchValues, PitchExact, SharedValues, SharedVolumes, Transients, PitchThinned, PitchOutOfRange, Disturbed, Conflicts, Recut, TooShort, EnvOther, BalanceOther, Swelled, KeyClamped, Rekeyed;
    public double MaxPitchError, MaxVolError, MaxSharedCents;
    public int MaxSharedLevel;
    /// <summary>0 for an instrument's own track; 1, 2, ... for its lanes (with the Lanes option: notes of the instrument that sounded together, each lane its own track and channel, the same preset).</summary>
    public int Lane;
    /// <summary>Which instrument (1 up) the track plays: lanes of one instrument share it.</summary>
    public int Instrument;

    public string RootText => Root.Found ? $"root {RecRoot.NoteName(Root.Midi)} ({Root.How})" : $"no pitch found ({Root.How}): $1000 is C5";

    /// <summary>A lane takes its instrument's analysis (not its counts).</summary>
    public void CopyInstrument(RecTrack f)
    {
        Root = f.Root; Tune = f.Tune; DriverCents = f.DriverCents; DriverConcentration = f.DriverConcentration; BendRange = f.BendRange; WeightL = f.WeightL; WeightR = f.WeightR;
        Adsr1 = f.Adsr1; Adsr2 = f.Adsr2; Gain = f.Gain; Echo = f.Echo; Noise = f.Noise; SampleEnded = f.SampleEnded; Sample = f.Sample;
    }
}

public sealed class RecOptions
{
    /// <summary>FL's tempo, used when no tempo is found in the game (the notes then keep their real times at FL's tempo).</summary>
    public double FlTempo = 120;
    public int MaxNotes = 200_000, MaxPitchEventsPerNote = 4096;
    public string Name = "Recording";
    /// <summary>Notes of one instrument that sound together go to lanes (extra tracks with the same preset): every value exact, more channels in FL. Off: one track per instrument (the user's choice; overlapping notes share a bend).</summary>
    public bool Lanes;
}

/// <summary>A transcription: the tracks, the tempo map, the MIDI file, and what it could not do, in lines for the window.</summary>
public sealed class Transcription
{
    public RecSession Session = null!;
    public List<RecNote> Notes = new();
    public RecNoteStats NoteStats = null!;
    public List<RecTrack> Tracks = new();
    public TempoResult Tempo = new();
    /// <summary>The tempo the ticks are counted at, segment by segment: (seconds from the recording's start, ticks there, ticks per second, microseconds per quarter).</summary>
    public List<(double Seconds, double Tick, double TicksPerSecond, int MicrosPerQuarter)> Map = new();
    public double FlTempoUsed;
    public byte[] Midi = [];
    public List<string> Facts = new();
    public int NotesThinned, PmodNotes, SrcnChangeNotes, EnvChangeNotes, FlagChangeNotes, KeyedWhileOff, ChannelsShared;
    public List<int> SamplesChanged = new();
    /// <summary>The values the file does NOT replay exactly, by (note index, the value's time): a pitch out of the bend range or past the keys, or one the shared channel could not give while another note of the instrument sounded (the checks hold the file to this list).</summary>
    public List<(int Note, uint From, uint To)> InexactPitch = new(), InexactVolume = new();
    /// <summary>Per note (its index in <see cref="Notes"/>): the pitch and volume timelines as FL can play them (RecTranscribe.Smooth), and its MIDI key.</summary>
    public Dictionary<int, List<(uint T, int V)>> PitchT = new();
    public Dictionary<int, List<(uint T, int L, int R)>> VolT = new();
    public Dictionary<int, int> KeyOf = new();
    public bool Lanes;

    /// <summary>Whether the transcription says the value of note <paramref name="note"/> in force at sample <paramref name="t"/> is not replayed exactly.</summary>
    public bool Reported(List<(int Note, uint From, uint To)> list, int note, uint t) { foreach (var x in list) if (x.Note == note && t >= x.From && t < x.To) return true; return false; }

    /// <summary>The tick of a time (seconds from the recording's start).</summary>
    public long TickOf(double seconds)
    {
        var seg = Map[0];
        foreach (var m in Map) if (m.Seconds <= seconds) seg = m; else break;
        return (long)Math.Round(seg.Tick + (seconds - seg.Seconds) * seg.TicksPerSecond);
    }

    /// <summary>The time of a tick (the inverse of <see cref="TickOf"/>, for the checks).</summary>
    public double SecondsOf(double tick)
    {
        var seg = Map[0];
        foreach (var m in Map) if (m.Tick <= tick) seg = m; else break;
        return seg.Seconds + (tick - seg.Tick) / seg.TicksPerSecond;
    }

    public int PitchValues => Tracks.Sum(t => t.PitchValues);
    public int PitchExact => Tracks.Sum(t => t.PitchExact);

    /// <summary>One track alone, as a type-0 MIDI file (FL imports it into the channel it is dropped on, without its import window).</summary>
    public byte[] TrackMidi(int track)
    {
        var tempos = Map.Select(m => ((long)Math.Round(m.Tick), m.MicrosPerQuarter)).ToList();
        return RecMidi.Write(tempos, Tracks[track].Name, [], [Tracks[track].Smf], singleTrack: true);
    }
}

/// <summary>Test-only switches: deliberate bugs a check must catch (0 = the real code; only the self-test sets it).</summary>
public static class RecMutants
{
    public static int Active;
    public const int BendOffByOne = 1, TuneIgnoredInPreset = 2, RootOctaveOff = 3, TempoOnePercent = 4, NoBendRange = 5, VelocityIgnoresWeights = 6, CaptureLosesWrites = 7, SectionCrcNotChecked = 8,
        KeyOnVersionLost = 9, NotesQuantised = 10, ConflictsHidden = 11, TrackMidiWrongChannel = 12, DragWrongFile = 13, SaveWritesHalf = 14, CaptureAllocates = 15, DealIgnored = 16, LiveOutSilent = 17,
        LiveRootIgnored = 18, LiveNoteOffLost = 19,
        // the track offer (2026-10-18): which track an FL channel plays
        MatchIgnoresPosition = 20, UnrelatedGrabbed = 21, BindAfterFirstNote = 22, EnginesNotPrebuilt = 23, TrackSectionDropped = 24, DefaultStateCountsAsTouched = 25, ManualPickIgnored = 26, OfferIgnoresLateInstances = 27, ExpressionIgnored = 28,
        // relink (2026-10-18): instances joining the recording, the anchor buttons, the channel's name, the host log
        ParamPushUnjoins = 29, NoAutoJoin = 30, LinkAllIgnored = 31, LinkButtonIgnored = 32, CounterCountsFoundOnly = 33, NameIgnored = 34, LogNotAutomatic = 35, LogIgnoresTestPath = 36, UsersOwnStateJoins = 37, LogNotCapped = 38;
    public static bool On(int m) => Active == m;
}

public static class RecTranscribe
{
    public const int MaxBendRange = 48;

    /// <summary>The 15 MIDI channels a track can have (channel 10, index 9, is left out: General MIDI drums).</summary>
    public static int ChannelOf(int track) { int k = track % 15; return k < 9 ? k : k + 1; }

    public static Transcription Run(RecSession s, RecOptions o)
    {
        var tr = new Transcription { Session = s, Lanes = o.Lanes };
        tr.Notes = RecNotes.Extract(s, out var ns);
        tr.NoteStats = ns;
        if (tr.Notes.Count > o.MaxNotes) { tr.NotesThinned = tr.Notes.Count - o.MaxNotes; tr.Notes.RemoveRange(o.MaxNotes, tr.Notes.Count - o.MaxNotes); }
        tr.SamplesChanged = s.ChangedDuringRecording();

        // ---- tempo and the tick map ----
        tr.Tempo = RecTempo.Estimate(tr.Notes.Select(n => n.StartSeconds), s.Seconds);
        BuildMap(tr, o);

        // ---- tracks: one per instrument, in the order they were first keyed ----
        var byVersion = new SortedDictionary<int, List<int>>();
        for (int i = 0; i < tr.Notes.Count; i++)
        {
            var n = tr.Notes[i];
            if (!byVersion.TryGetValue(n.Version, out var l)) byVersion[n.Version] = l = new List<int>();
            l.Add(i);
        }
        var order = byVersion.Keys.OrderBy(v => s.Versions[v].FirstTime).ThenBy(v => v).ToList();
        var srcnSeen = new Dictionary<int, int>();
        int inst = 0;
        foreach (int ver in order)
        {
            var v = s.Versions[ver];
            int nth = srcnSeen.TryGetValue(v.Srcn, out int c) ? c + 1 : 0;
            srcnSeen[v.Srcn] = nth;
            inst++;
            string baseName = $"{o.Name} - {inst:00} sample {v.Srcn}{(nth > 0 ? ((char)('a' + Math.Min(nth, 25))).ToString() : "")}";
            // lanes (an option): notes of the instrument that sound at the same time go to tracks of their own, so each has its own pitch bend and controller 7
            var lanes = o.Lanes ? Lanes(tr, byVersion[ver]) : [byVersion[ver]];
            for (int ln = 0; ln < lanes.Count; ln++)
            {
                int k = tr.Tracks.Count;
                var t = new RecTrack { Index = k, Version = ver, Srcn = v.Srcn, Start = v.Start, Loop = v.Loop, Channel = ChannelOf(k), Notes = lanes[ln], Lane = ln, Instrument = inst };
                t.Name = ln == 0 ? baseName : $"{baseName} lane {ln + 1}";
                if (t.Name.Length > PresetFile.MaxName) t.Name = t.Name[^PresetFile.MaxName..];
                tr.Tracks.Add(t);
            }
        }
        tr.ChannelsShared = Math.Max(0, tr.Tracks.Count - 15);

        foreach (var g in tr.Tracks.GroupBy(t => t.Version))
        {
            var first = g.First();
            Instrument(tr, first, byVersion[first.Version], o);
            foreach (var t in g.Skip(1)) t.CopyInstrument(first);
        }
        foreach (var t in tr.Tracks) Channel(tr, t, o);
        foreach (var g in tr.Tracks.GroupBy(t => t.Version))
        {
            var first = g.First();
            MakePreset(tr, first);
            foreach (var t in g.Skip(1)) { t.Preset = first.Preset; t.PresetFile = first.PresetFile; t.PresetProblem = first.PresetProblem; }
        }
        foreach (var n in tr.Notes)
        {
            if (n.Pmod) tr.PmodNotes++;
            if (n.SrcnChanges > 0) tr.SrcnChangeNotes++;
            if (n.EnvChanges > 0) tr.EnvChangeNotes++;
            if (n.FlagChanges > 0) tr.FlagChangeNotes++;
            if (n.KeyedWhileOff) tr.KeyedWhileOff++;
        }

        // ---- the file ----
        var tempos = tr.Map.Select(m => ((long)Math.Round(m.Tick), m.MicrosPerQuarter)).ToList();
        var texts = new List<string> { "BrokenNes2 recording of " + (s.Game.Length > 0 ? s.Game : "a SNES game"), "tempo: " + tr.Tempo.Describe() };
        tr.Midi = RecMidi.Write(tempos, o.Name, texts, tr.Tracks.Select(x => x.Smf).ToList());
        Facts(tr);
        return tr;
    }

    private static void BuildMap(Transcription tr, RecOptions o)
    {
        const int ppq = RecMidi.Ppq;
        var tempo = tr.Tempo;
        if (!tempo.Found)
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
            double bpm = seg.Bpm * (RecMutants.On(RecMutants.TempoOnePercent) ? 1.01 : 1);
            int us = (int)Math.Round(60_000_000 / bpm);
            double rate = ppq * 1e6 / us;
            if (i == 0)
            {
                // the first downbeat on a bar line: the file starts a little before the recording (less than a bar), so that FL's bars fall where the game's are
                double atDown = seg.DownbeatSeconds * rate;
                double lead = Math.Ceiling(atDown / bar - 1e-9) * bar - atDown;
                tr.Map.Add((0, lead, rate, us));
            }
            else
            {
                // a tempo change: a bridge from the last bar line of the stretch before to this stretch's first downbeat, at the tempo that makes it a whole number of bars, so FL's bars stay on the game's
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

    /// <summary>What the plugin's FL voice level is for a MIDI key and a 14-bit bend at a bend range: (key - 60) x 100 + bend x range x 100 / 8192 cents, as a float, as FL hands it over (TLevelParams.Pitch).</summary>
    public static double FlCents(int key, int bend, int range) => (float)((key - 60) * 100 + bend * range * 100.0 / 8192.0);

    /// <summary>The register the plugin writes for that pitch on a preset tuned by <paramref name="tune"/> (SnesSampler.PitchForTuned: the very function the plugin calls).</summary>
    public static int Register(int key, int bend, int range, int tune) => SnesSampler.PitchForTuned(FlCents(key, bend, range), tune);

    /// <summary>How long a value must be held to be one for FL: 64 S-DSP samples, 2 ms (FL's plugin control step is 64 host samples, 1.3 to 1.5 ms; a MIDI tick at 960 PPQ is 0.3 to 0.8 ms).</summary>
    public const uint MinHold = 64;

    /// <summary>
    /// A timeline (values held from their time until the next one's, the last until <paramref name="end"/>) as FL can play it: a run of values each held for less than <see cref="MinHold"/> that lasts
    /// less than MinHold in all is a transient (a pitch whose low byte was written a sample before its high byte, a value rewritten at once): the value after it starts where it started; a longer
    /// run of short values is a fast sweep, sampled every MinHold. <paramref name="transient"/> counts the values that are not kept as themselves. Equal neighbours are merged.
    /// </summary>
    public static List<(uint T, int V)> Smooth(IReadOnlyList<(uint T, int V)> values, uint end, out int transient)
    {
        transient = 0;
        var kept = new List<(uint T, int V)>();
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
            if (kept.Count > 0 && kept[^1].T == at) { kept[^1] = (at, v); return; }
            kept.Add((at, v));
        }
    }

    /// <summary>Every 14-bit bend that makes the plugin write register <paramref name="p"/> for <paramref name="key"/> (a contiguous range: the register is monotonic in the bend), the one nearest the ideal, and whether any does
    /// (when none does: the nearest bend alone, and its error in cents).</summary>
    public static (int Lo, int Hi, int Ideal, bool Exact, double ErrCents) BendInterval(int p, int key, int range, int tune)
    {
        int b = BendFor(p, key, range, tune, out bool exact, out double err);
        if (!exact) return (b, b, b, false, err);
        int lo = b, hi = b;
        while (lo > -8192 && Register(key, lo - 1, range, tune) == p) lo--;
        while (hi < 8191 && Register(key, hi + 1, range, tune) == p) hi++;
        if (RecMutants.On(RecMutants.BendOffByOne)) { int w = hi - lo + 1; return (Math.Min(8191, lo + w), Math.Min(8191, hi + w), Math.Min(8191, b + w), true, 0); }     // (the deliberate bug: the next register's bends)
        return (lo, hi, b, true, 0);
    }

    /// <summary>The voice level the plugin plays for a velocity and a controller 7 value (FL assumed to hand it velocity / 127 x CC7 / 100 as TLevelParams.Vol; the plugin's round(127 x Vol)).</summary>
    public static int PlayedLevel(int velocity, int cc) => Math.Clamp((int)Math.Round(127 * (float)(velocity / 127.0 * cc / 100.0)), 0, 127);

    /// <summary>Every controller 7 value that plays <paramref name="level"/> at <paramref name="velocity"/> (contiguous: the level is monotonic in the value), the one nearest 100 x level / velocity, and whether any does.</summary>
    public static (int Lo, int Hi, int Ideal, bool Exact) CcInterval(int level, int velocity)
    {
        int lo = -1, hi = -1;
        for (int cc = 0; cc <= 127; cc++) if (PlayedLevel(velocity, cc) == level) { if (lo < 0) lo = cc; hi = cc; }
        int ideal = Math.Clamp((int)Math.Round(100.0 * level / velocity), 0, 127);
        if (lo < 0)
        {
            int best = ideal, err = int.MaxValue;
            for (int cc = 0; cc <= 127; cc++) { int e = Math.Abs(PlayedLevel(velocity, cc) - level); if (e < err) { err = e; best = cc; } }
            return (best, best, best, false);
        }
        return (lo, hi, Math.Clamp(ideal, lo, hi), true);
    }

    /// <summary>The bend (-8192..8191) that makes the plugin write register <paramref name="p"/> for <paramref name="key"/>, or the nearest one when no bend does (exact = false).</summary>
    public static int BendFor(int p, int key, int range, int tune, out bool exact, out double errCents)
    {
        double want = 1200 * Math.Log2(Math.Max(1, p) / 4096.0) - tune - (key - 60) * 100.0;
        int b0 = (int)Math.Round(want * 8192.0 / (range * 100.0));
        int best = Math.Clamp(b0, -8192, 8191); int bestErr = int.MaxValue;
        for (int d = 0; d <= 16; d++)
        {
            foreach (int b in d == 0 ? new[] { b0 } : new[] { b0 - d, b0 + d })
            {
                if (b < -8192 || b > 8191) continue;
                int err = Math.Abs(Register(key, b, range, tune) - p);
                if (err < bestErr) { bestErr = err; best = b; }
                if (err == 0) break;
            }
            if (bestErr == 0) break;
        }
        exact = bestErr == 0;
        int got = Register(key, best, range, tune);
        errCents = exact ? 0 : Math.Abs(1200 * Math.Log2(Math.Max(1, got) / (double)Math.Max(1, p)));
        return best;
    }

    /// <summary>The velocity (1..127) whose replay (level x weight / 127 on each side, the plugin's integer arithmetic) is nearest the volumes <paramref name="l"/>, <paramref name="r"/>; <paramref name="err"/> is the larger side's error.</summary>
    public static int LevelFor(int l, int r, int wl, int wr, out int err)
    {
        if (RecMutants.On(RecMutants.VelocityIgnoresWeights)) { wl = 127; wr = 127; }
        int best = 1; err = int.MaxValue;
        for (int v = 0; v <= 127; v++)
        {
            int e = Math.Max(Math.Abs(v * wl / 127 - l), Math.Abs(v * wr / 127 - r));
            if (e < err) { err = e; best = v; }
        }
        return best;
    }

    /// <summary>The lanes of an instrument's notes: each note in the first lane free at its start (a lane is free once its last note has ended), so no two notes of a lane sound together.</summary>
    private static List<List<int>> Lanes(Transcription tr, List<int> notes)
    {
        var lanes = new List<List<int>>();
        var busyUntil = new List<uint>();
        foreach (int i in notes)
        {
            var n = tr.Notes[i];
            int l = busyUntil.FindIndex(e => e <= n.Start);
            if (l < 0) { lanes.Add(new List<int>()); busyUntil.Add(0); l = lanes.Count - 1; }
            lanes[l].Add(i);
            busyUntil[l] = Math.Max(n.End, n.Start + 1);
        }
        return lanes;
    }

    /// <summary>The instrument of a track (and of its lanes): its bytes, its root and the preset's tuning, the most common envelope, balance and flags (from ALL its notes); each note's timelines as FL can
    /// play them (<see cref="Smooth"/>), its key, and the bend range that holds every move.</summary>
    private static void Instrument(Transcription tr, RecTrack t, List<int> all, RecOptions o)
    {
        var s = tr.Session;
        var notes = all.Select(i => tr.Notes[i]).ToList();
        t.Sample = s.SampleBytes(t.Version, out bool ended);
        t.SampleEnded = ended;
        int loopOff = t.Loop - t.Start;
        if (loopOff < 0 || loopOff % 9 != 0 || loopOff >= t.Sample.Length) loopOff = -1;
        t.Noise = notes.Count(n => n.Noise) * 2 > notes.Count;
        t.Echo = notes.Count(n => n.Echo) * 2 > notes.Count;
        t.Root = t.Noise ? new RootEstimate(0, 0, "a noise instrument") : RecRoot.Estimate(t.Sample, loopOff);
        if (RecMutants.On(RecMutants.RootOctaveOff) && t.Root.Found) t.Root = t.Root with { Hz = t.Root.Hz / 2 };
        int tune = t.Root.Found ? (int)Math.Round(100 * (60 - t.Root.Midi)) : 0;
        var (dc, conc) = RecRoot.DriverOffset(notes.Select(n => n.Pitches[0].P), tune);
        t.DriverCents = dc; t.DriverConcentration = conc;
        if (conc >= 0.8 && Math.Abs(dc) >= 1) tune += (int)Math.Round(dc);
        t.Tune = Math.Clamp(tune, -SnesOverlay.MaxTuneCents, SnesOverlay.MaxTuneCents);

        var env = notes.GroupBy(n => (n.Adsr1, n.Adsr2, n.Gain)).OrderByDescending(g => g.Count()).First().Key;
        (t.Adsr1, t.Adsr2, t.Gain) = env;
        t.EnvOther = notes.Count(n => (n.Adsr1, n.Adsr2, n.Gain) != env);
        var bal = notes.Select(n => Balance(n.Vols[0].L, n.Vols[0].R)).Where(b => b != (0, 0)).GroupBy(b => b).OrderByDescending(g => g.Count()).Select(g => g.Key).DefaultIfEmpty((127, 127)).First();
        (t.WeightL, t.WeightR) = bal;
        t.BalanceOther = notes.Count(n => Balance(n.Vols[0].L, n.Vols[0].R) is var b && b != (0, 0) && b != bal);

        // each note's timelines as FL can play them: values held for less than MinHold are below its control rate (a pitch written in two halves, a fast sweep)
        double maxDev = 0;
        foreach (int i in all)
        {
            var n = tr.Notes[i];
            var pl = Smooth(n.Pitches.Select(p => (p.T, p.P)).ToList(), n.End, out int tp);
            if (pl.Count > o.MaxPitchEventsPerNote) { t.PitchThinned += pl.Count - o.MaxPitchEventsPerNote; pl.RemoveRange(o.MaxPitchEventsPerNote, pl.Count - o.MaxPitchEventsPerNote); }
            var vl = Smooth(n.Vols.Select(v => (v.T, (v.L + 128) << 8 | (v.R + 128))).ToList(), n.End, out int tv);
            t.Transients += tp + tv;
            tr.PitchT[i] = pl;
            tr.VolT[i] = vl.Select(v => (v.T, (v.V >> 8) - 128, (v.V & 0xFF) - 128)).ToList();
            // the key: the key-on's musical note; a note whose pitch then wanders further than the largest bend range (a corrupted driver) is keyed at the middle of its excursion
            double Midi(int p) => 60 + (1200 * Math.Log2(Math.Max(1, p) / 4096.0) - t.Tune) / 100.0;
            int key = (int)Math.Round(Midi(pl[0].V));
            double lo = pl.Min(p => Midi(p.V)), hi = pl.Max(p => Midi(p.V));
            if (Math.Max(hi - key, key - lo) > MaxBendRange - 0.5) { key = (int)Math.Round((lo + hi) / 2); t.Rekeyed++; }
            if (key is < 0 or > 127) { t.KeyClamped++; key = Math.Clamp(key, 0, 127); }
            tr.KeyOf[i] = key;
            maxDev = Math.Max(maxDev, Math.Max(hi - key, key - lo) * 100);
        }
        t.BendRange = Math.Clamp((int)Math.Ceiling((maxDev + 1) / 100.0), 2, MaxBendRange);
    }

    /// <summary>A track's channel: velocities, the bend and controller-7 intervals each value needs, and the events that give every sounding note its values where they can.</summary>
    private static void Channel(Transcription tr, RecTrack t, RecOptions o)
    {
        var notes = t.Notes.Select(i => tr.Notes[i]).ToList();
        var pitchT = t.Notes.Select(i => tr.PitchT[i]).ToList();
        var volT = t.Notes.Select(i => tr.VolT[i]).ToList();
        foreach (int i in t.Notes) t.Keys.Add(tr.KeyOf[i]);

        // ---- what each value needs from the channel: the interval of bends (and of CC7 values) that give it EXACTLY ----
        var bendNeed = new List<(int Lo, int Hi, int Ideal, bool Exact)[]>();
        var ccNeed = new List<(int Lo, int Hi, int Ideal, bool Exact)[]>();
        for (int i = 0; i < notes.Count; i++)
        {
            int key = t.Keys[i];
            var bn = new (int, int, int, bool)[pitchT[i].Count];
            for (int k = 0; k < bn.Length; k++)
            {
                int p = pitchT[i][k].V;
                var (lo, hi, ideal, exact, err) = BendInterval(p, key, t.BendRange, t.Tune);
                bn[k] = (lo, hi, ideal, exact);
                if (!exact)
                {
                    t.MaxPitchError = Math.Max(t.MaxPitchError, err);
                    if (Math.Abs(1200 * Math.Log2(Math.Max(1, p) / 4096.0) - t.Tune - (key - 60) * 100.0) > t.BendRange * 100) t.PitchOutOfRange++;
                }
            }
            bendNeed.Add(bn);
            // the levels: velocity is the key-on level unless the note later swells past 127 % of it; controller 7 carries the rest (100 = as keyed)
            var lv = volT[i].Select(v => LevelFor(v.L, v.R, t.WeightL, t.WeightR, out _)).ToList();
            int peak = lv.Max();
            int vel = Math.Min(127, Math.Max(1, Math.Max(lv[0], (int)Math.Ceiling(peak * 100 / 127.0))));
            if (vel > lv[0]) t.Swelled++;
            t.Velocities.Add(vel);
            var cn = new (int, int, int, bool)[lv.Count];
            for (int k = 0; k < cn.Length; k++)
            {
                cn[k] = CcInterval(lv[k], vel);
                int lev = PlayedLevel(vel, cn[k].Item3);
                int e = Math.Max(Math.Abs(lev * t.WeightL / 127 - volT[i][k].L), Math.Abs(lev * t.WeightR / 127 - volT[i][k].R));
                t.MaxVolError = Math.Max(t.MaxVolError, e);
            }
            ccNeed.Add(cn);
        }

        // ---- the channel's events. At every tick where something changes, the channel's bend is chosen inside the intervals of ALL the notes sounding then (two notes of a chord usually
        // have intervals that overlap: both stay exact); only when they do not overlap does the newest note win, and every value that the channel then cannot give is marked inexact. ----
        var ev = t.Smf.Events;
        int ch = RecMutants.On(RecMutants.TrackMidiWrongChannel) && t.Index == 1 ? ChannelOf(0) : t.Channel;
        if (!RecMutants.On(RecMutants.NoBendRange)) ev.AddRange(RecMidi.BendRange(0, ch, t.BendRange));
        ev.Add(RecMidi.Cc(0, ch, 7, 100));
        ev.Add(RecMidi.PitchBend(0, ch, 0));
        int curBend = 0, curCc = 100;
        var starts = new long[notes.Count]; var ends = new long[notes.Count];
        for (int i = 0; i < notes.Count; i++)
        {
            starts[i] = tr.TickOf(notes[i].StartSeconds);
            ends[i] = tr.TickOf(notes[i].End / 32000.0);
            if (RecMutants.On(RecMutants.NotesQuantised)) { long g = RecMidi.Ppq / 4; starts[i] = (starts[i] + g / 2) / g * g; }
            if (ends[i] <= starts[i]) { ends[i] = starts[i] + 1; t.TooShort++; }
        }
        // (tick, kind 0 end / 1 start / 2 next pitch value / 3 next volume value, note, value index)
        var all = new List<(long Tick, int Kind, int Note, int K)>();
        for (int i = 0; i < notes.Count; i++)
        {
            all.Add((starts[i], 1, i, 0));
            all.Add((ends[i], 0, i, 0));
            for (int k = 1; k < pitchT[i].Count; k++) all.Add((Math.Clamp(tr.TickOf(pitchT[i][k].T / 32000.0), starts[i], ends[i]), 2, i, k));
            for (int k = 1; k < volT[i].Count; k++) all.Add((Math.Clamp(tr.TickOf(volT[i][k].T / 32000.0), starts[i], ends[i]), 3, i, k));
        }
        all.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : a.Note != b.Note ? a.Note.CompareTo(b.Note) : a.K.CompareTo(b.K));
        var sounding = new List<int>();                      // in start order: the last is the newest
        var curP = new int[notes.Count]; var curV = new int[notes.Count];
        var badP = new List<bool[]>(); var badV = new List<bool[]>();
        for (int i = 0; i < notes.Count; i++) { badP.Add(new bool[pitchT[i].Count]); badV.Add(new bool[volT[i].Count]); }
        var keyOwner = new Dictionary<int, int>();          // key -> the note sounding it on this channel
        var cut = new bool[notes.Count];
        var startsNow = new List<int>();
        for (int e = 0; e < all.Count; )
        {
            long tick = all[e].Tick;
            startsNow.Clear();
            for (; e < all.Count && all[e].Tick == tick; e++)
            {
                var (_, kind, i, k) = all[e];
                switch (kind)
                {
                    case 0:
                        if (!sounding.Remove(i)) break;          // (already cut by a re-struck key)
                        if (keyOwner.TryGetValue(t.Keys[i], out int kowner) && kowner == i) { ev.Add(RecMidi.Off(tick, ch, t.Keys[i])); keyOwner.Remove(t.Keys[i]); }
                        break;
                    case 1:
                    {
                        int key = t.Keys[i];
                        if (keyOwner.TryGetValue(key, out int other) && sounding.Contains(other))
                        {
                            // the same key struck again on another voice: one MIDI channel cannot hold two notes of one key, the earlier one ends here (what it had left is lost)
                            ev.Add(RecMidi.Off(tick, ch, key));
                            sounding.Remove(other);
                            t.Recut++;
                            cut[other] = true;
                            for (int q = curP[other]; q < badP[other].Length; q++) badP[other][q] = true;
                            for (int q = curV[other]; q < badV[other].Length; q++) badV[other][q] = true;
                        }
                        sounding.Add(i);
                        keyOwner[key] = i;
                        startsNow.Add(i);
                        break;
                    }
                    case 2: if (sounding.Contains(i)) curP[i] = k; break;
                    case 3: if (sounding.Contains(i)) curV[i] = k; break;
                }
            }
            if (sounding.Count == 0) continue;
            int newest = sounding[^1];
            curBend = Resolve(sounding, newest, curBend, j => bendNeed[j][curP[j]], (j, v) =>
            {
                badP[j][curP[j]] = true;
                int p = pitchT[j][curP[j]].V;
                t.MaxSharedCents = Math.Max(t.MaxSharedCents, Math.Abs(1200 * Math.Log2(Math.Max(1, Register(t.Keys[j], v, t.BendRange, t.Tune)) / (double)Math.Max(1, p))));
            }, v => ev.Add(RecMidi.PitchBend(tick, ch, v)));
            curCc = Resolve(sounding, newest, curCc, j => ccNeed[j][curV[j]], (j, v) =>
            {
                badV[j][curV[j]] = true;
                t.MaxSharedLevel = Math.Max(t.MaxSharedLevel, Math.Abs(PlayedLevel(t.Velocities[j], v) - PlayedLevel(t.Velocities[j], ccNeed[j][curV[j]].Ideal)));
            }, v => ev.Add(RecMidi.Cc(tick, ch, 7, v)));
            foreach (int i in startsNow) ev.Add(RecMidi.On(tick, ch, t.Keys[i], t.Velocities[i]));
        }

        // ---- the accounting, value by value ----
        for (int i = 0; i < notes.Count; i++)
        {
            bool shared = false;
            uint end = notes[i].End;
            for (int k = 0; k < bendNeed[i].Length; k++)
            {
                t.PitchValues++;
                uint to = k + 1 < pitchT[i].Count ? pitchT[i][k + 1].T : Math.Max(end, pitchT[i][k].T + 1);
                if (badP[i][k]) { t.SharedValues++; shared = true; tr.InexactPitch.Add((t.Notes[i], pitchT[i][k].T, to)); }
                else if (bendNeed[i][k].Exact) t.PitchExact++;
                else tr.InexactPitch.Add((t.Notes[i], pitchT[i][k].T, to));
            }
            for (int k = 0; k < ccNeed[i].Length; k++)
            {
                uint to = k + 1 < volT[i].Count ? volT[i][k + 1].T : Math.Max(end, volT[i][k].T + 1);
                if (badV[i][k]) { t.SharedVolumes++; shared = true; tr.InexactVolume.Add((t.Notes[i], volT[i][k].T, to)); }
                else if (!ccNeed[i][k].Exact) tr.InexactVolume.Add((t.Notes[i], volT[i][k].T, to));
            }
            if (shared) t.Disturbed++;
        }

        // the channel's value for this tick: inside every sounding note's interval when they overlap (the current one kept if it is inside, else the newest note's ideal moved into the overlap);
        // else the newest note's, and every note it does not suit gets its current value marked
        int Resolve(List<int> sounding, int newest, int cur, Func<int, (int Lo, int Hi, int Ideal, bool Exact)> need, Action<int, int> mark, Action<int> emit)
        {
            int lo = int.MinValue, hi = int.MaxValue;
            foreach (int j in sounding) { var n = need(j); lo = Math.Max(lo, n.Lo); hi = Math.Min(hi, n.Hi); }
            var mine = need(newest);
            int target;
            if (lo <= hi) target = cur >= lo && cur <= hi ? cur : Math.Clamp(mine.Ideal, lo, hi);
            else target = cur >= mine.Lo && cur <= mine.Hi ? cur : mine.Ideal;
            if (target != cur) emit(target);
            foreach (int j in sounding)
            {
                var n = need(j);
                if ((target < n.Lo || target > n.Hi) && !(RecMutants.On(RecMutants.ConflictsHidden) && j != newest)) { mark(j, target); t.Conflicts++; }
            }
            return target;
        }

        t.Smf.Name = t.Name;
        t.Smf.Instrument = t.Lane == 0 ? t.Name : t.Name[..t.Name.LastIndexOf(" lane ", StringComparison.Ordinal)];      // (a lane plays its instrument's preset)
        t.Smf.Texts.Add($"BrokenNes2: sample {t.Srcn} at ${t.Start:X4}, {t.RootText}, preset tuning {t.Tune:+0;-0;0} cents, pitch-bend range {t.BendRange} semitones (RPN 0)");
    }

    /// <summary>The left / right balance of a key-on, as weights of 127 for the louder side (signs kept: a negative volume flips the phase).</summary>
    private static (int L, int R) Balance(int l, int r)
    {
        int m = Math.Max(Math.Abs(l), Math.Abs(r));
        if (m == 0) return (0, 0);
        return ((int)Math.Round(127.0 * l / m), (int)Math.Round(127.0 * r / m));
    }

    /// <summary>The track's preset: the sample (as it was at its first key-on), the most common envelope, the tuning, the balance, echo and noise; the context the engine needs (the S-DSP registers then, and the
    /// audio RAM a sample with no end block plays on into). Deterministic: the same recording gives the same bytes.</summary>
    private static void MakePreset(Transcription tr, RecTrack t)
    {
        var s = tr.Session;
        var v = s.Versions[t.Version];
        var shot = v.Shot >= 0 && v.Shot < s.Shots.Length ? s.Shots[v.Shot] : s.End;
        if (shot == null) { t.PresetProblem = "no copy of the sound memory holds this sample"; return; }
        if (t.Sample.Length == 0 || t.Start + t.Sample.Length > SpcSnapshot.AramSize) { t.PresetProblem = "the sample does not fit in audio RAM"; return; }
        var inst = SnesInstruments.Template(t.Srcn, t.Sample.Length);
        inst.Name = t.Name.Length > PresetFile.MaxName ? t.Name[..PresetFile.MaxName] : t.Name;
        inst.Origin = t.Start;
        inst.Blobs[0].Data = (byte[])t.Sample.Clone();
        void Set(string id, int value) { var f = inst.Field(id)!; f.Value = f.Clamp(value); }
        Set("env.adsr", t.Adsr1 >> 7); Set("env.ar", t.Adsr1 & 15); Set("env.dr", t.Adsr1 >> 4 & 7);
        Set("env.sl", t.Adsr2 >> 5); Set("env.sr", t.Adsr2 & 31);
        Set("gain.mode", t.Gain >> 7); Set("gain.curve", t.Gain >> 5 & 3); Set("gain.rate", t.Gain & 31);
        Set("vol.l", t.WeightL); Set("vol.r", t.WeightR);
        Set("tune.cents", RecMutants.On(RecMutants.TuneIgnoredInPreset) ? 0 : t.Tune);
        Set("mix.echo", t.Echo ? 1 : 0); Set("mix.noise", t.Noise ? 1 : 0); Set("mix.pmod", 0);
        Set("loop.addr", t.Loop);
        var snap = new SpcSnapshot(shot.Aram, shot.Dsp);
        var ctx = PresetContext.Of(snap, inst);
        var packed = InstrumentCodec.Pack(inst);
        var history = new InstrumentHistory(inst).Serialize();
        string notes = $"Recorded from {(s.Game.Length > 0 ? s.Game : "a SNES game")} by BrokenNes2: sample {t.Srcn} at ${t.Start:X4}, {t.RootText}. The MIDI track of the same name plays it as the game did: tuning {t.Tune:+0;-0;0} cents, "
            + $"pitch-bend range {t.BendRange} semitones (set FL's channel pitch range to {t.BendRange} if its import did not).";
        string game = PresetStore.GameTitle(s.Game);
        var tags = new List<string> { "snes", "sample", "recording" };
        string rec = PresetStore.SafeFileName(s.Name);
        if (rec.Length is > 0 and <= PresetFile.MaxTagLength) tags.Add(rec);
        var d = PresetMaker.Make(inst.Name, inst.Name, s.Game.Length > PresetFile.MaxGame ? s.Game[..PresetFile.MaxGame] : s.Game, s.Sha1.All(Uri.IsHexDigit) && s.Sha1.Length <= 40 ? s.Sha1 : "", notes.Length > PresetFile.MaxNotes ? notes[..PresetFile.MaxNotes] : notes,
            s.CreatedUnix, packed, packed, history, ctx, out string? problem, tags.ToArray());
        if (d == null) { t.PresetProblem = problem; return; }
        var bytes = PresetFile.TryWrite(d, out problem);
        if (bytes == null) { t.PresetProblem = problem; return; }
        t.Preset = d;
        t.PresetFile = bytes;
        _ = game;
    }

    private static void Facts(Transcription tr)
    {
        var f = tr.Facts;
        var s = tr.Session;
        f.Add($"{tr.Notes.Count:N0} notes on {tr.Tracks.Count} instrument{(tr.Tracks.Count == 1 ? "" : "s")} in {s.Seconds:0.0} s ({s.Count:N0} chip writes)");
        if (tr.Notes.Count == 0)
            f.Add((tr.NoteStats.KeyOns == 0 ? "no key-on: the game started no note while it was recorded" : $"no note to transcribe: the game's {tr.NoteStats.KeyOns} key-ons asked for no volume")
                + (s.HeldAtStart > 0 ? $" ({s.HeldAtStart} voices were already sounding when Record began: press Record before the music starts, or record until its next notes)" : ""));
        f.Add(tr.Tempo.Found ? $"tempo {tr.Tempo.Describe()}: set FL's tempo to {60_000_000.0 / tr.Map[0].MicrosPerQuarter:0.###} BPM" : $"{tr.Tempo.Describe()}: the notes are at their real times at FL's tempo ({tr.FlTempoUsed:0.##} BPM); keep FL at that tempo");
        int pv = tr.PitchValues, pe = tr.PitchExact, shared = tr.Tracks.Sum(t => t.SharedValues), sharedV = tr.Tracks.Sum(t => t.SharedVolumes), trans = tr.Tracks.Sum(t => t.Transients);
        double worst = tr.Tracks.Count == 0 ? 0 : tr.Tracks.Max(t => t.MaxPitchError);
        f.Add((pe == pv ? $"pitch: all {pv:N0} pitch values are replayed exactly (key + bend)"
            : $"pitch: {pe:N0} of {pv:N0} values exact" + (shared > 0 ? $"; {shared:N0} could not have their bend while another note of the same instrument sounded (one channel each)" : "") + (pv - pe - shared > 0 ? $"; {pv - pe - shared:N0} off by up to {worst:0.##} cents (bend range or key limits)" : ""))
            + (trans > 0 ? $"; {trans:N0} pitch and volume values held under {MinHold / 32.0:0} ms (below FL's control rate) merged into the next" : ""));
        int dist = tr.Tracks.Sum(t => t.Disturbed), recut = tr.Tracks.Sum(t => t.Recut);
        double sc = tr.Tracks.Count == 0 ? 0 : tr.Tracks.Max(t => t.MaxSharedCents);
        int sl = tr.Tracks.Count == 0 ? 0 : tr.Tracks.Max(t => t.MaxSharedLevel);
        if (dist > 0 || recut > 0) f.Add($"one channel per instrument: {dist} notes sounded with another note of the same instrument that needed another bend or volume (pitch off by at most {sc:0.##} cents, {sharedV:N0} volume values off by at most {sl}){(recut > 0 ? $"; {recut} cut short by the same key on another voice" : "")}: the Lanes option gives such notes tracks of their own");
        int lanes = tr.Tracks.Count(t => t.Lane > 0);
        if (tr.Lanes) f.Add(lanes > 0 ? $"lanes: {lanes} extra tracks for notes of one instrument that sounded together (each plays its instrument's preset)" : "lanes: no instrument played two notes at once, no extra track");
        int rekeyed = tr.Tracks.Sum(t => t.Rekeyed);
        if (rekeyed > 0) f.Add($"{rekeyed} notes wandered further than a {MaxBendRange}-semitone bend from their key-on: keyed at the middle of their range instead");
        var not = new List<string>();
        if (tr.PmodNotes > 0) not.Add($"{tr.PmodNotes} with pitch modulation");
        if (tr.SrcnChangeNotes > 0) not.Add($"{tr.SrcnChangeNotes} whose sample changed while sounding");
        if (tr.EnvChangeNotes > 0) not.Add($"{tr.EnvChangeNotes} with an envelope rewritten while sounding");
        int envOther = tr.Tracks.Sum(t => t.EnvOther);
        if (envOther > 0) not.Add($"{envOther} keyed with another envelope than their preset's");
        if (tr.KeyedWhileOff > 0) not.Add($"{tr.KeyedWhileOff} keyed while released");
        if (not.Count > 0) f.Add("not replayed: notes " + string.Join(", ", not));
        var lost = new List<string>();
        if (s.Dropped > 0) lost.Add($"{s.Dropped:N0} chip writes lost (the capture ran out of memory blocks)");
        if (s.EndedBy == RecEnd.MemoryCap) lost.Add("the recording stopped at the memory cap");
        if (tr.NotesThinned > 0) lost.Add($"{tr.NotesThinned:N0} notes past the limit of the file left out");
        if (s.ShotsMissing > 0) lost.Add($"{s.ShotsMissing} instruments took their sample from the memory at Stop (no copy was ready)");
        if (s.VersionsOver > 0) lost.Add($"{s.VersionsOver} key-ons of instruments past the limit of {SpcRecorder.MaxVersions}");
        if (tr.SamplesChanged.Count > 0) lost.Add($"{tr.SamplesChanged.Count} samples were rewritten during the recording (their first version is used)");
        if (s.HeldAtStart > 0) lost.Add($"{s.HeldAtStart} voices were already sounding when it started (not transcribed)");
        if (tr.ChannelsShared > 0) lost.Add($"{tr.ChannelsShared} tracks share a MIDI channel number with an earlier track (16 channels)");
        if (lost.Count > 0) f.Add(string.Join("; ", lost));
        var owners = tr.Tracks.Where(t => t.Lane == 0).ToList();
        int presets = owners.Count(t => t.PresetFile != null);
        f.Add(presets == owners.Count ? $"{presets} presets, one per instrument, named like their tracks" : $"{presets} presets; {owners.Count - presets} instruments have none: " + string.Join("; ", owners.Where(t => t.PresetProblem != null).Select(t => $"{t.Index + 1:00}: {t.PresetProblem}").Take(3)));
    }
}
