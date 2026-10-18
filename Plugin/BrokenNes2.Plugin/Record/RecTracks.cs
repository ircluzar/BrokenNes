// Round 8 follow-up (record, 2026-10-18): which track of a recording an FL channel is playing, from the notes it plays.
//
// The user's workflow: drag the recording's MIDI file into FL (one MIDI Out channel per track, the notes in each channel's piano roll), then Replace each channel by BrokenNes2. FL does not
// tell a plugin which channel it replaced or what the notes are; the notes come to the plugin one by one as FL plays them. So an untouched BrokenNes2 recognises its track from the notes it
// is asked to play: the key, and where in the song the note is (FL's mixing time in beats). TrackIndex holds every note of the offered tracks by beat; TrackMatcher (one per instance, its
// arrays made with the instance) weighs each note against it on the audio thread without allocating.
//
//   aligned   the note's key at the note's song position (within Tolerance beats) in a track: the evidence FL's import gives when the song sits where the file put it. A track that has
//             every note so far stays a candidate; the first note can be enough (it is bound before it sounds). Several candidates (tracks playing the same notes): the lowest is played
//             until a note tells them apart (or one of them is on the note's MIDI channel, which FL calls the note's colour). A note no candidate explains starts the count again from it
//             (playback jumped, or a note was edited); a provisional choice that nothing explains any more is let go.
//   shifted   no aligned track (the pattern was moved, or FL gives no song position): offsets from the first note to every note of the same key, kept while later notes fit the same
//             offset of the same track; one track left after ShiftedNotes notes binds. Unrelated notes leave no consistent offset and bind nothing.
//
// The key: FL hands the voice's pitch in cents (from C5) including the channel's pitch at that moment; the MIDI file's bend at a note's start (a slide begins below its key) may or may not
// have reached FL's channel, at its own range or FL's default of 2, so a key matches when the pitch is within 50 cents of the key, of the key with that bend, or with it at range 2.
namespace BrokenNes2.Record;

/// <summary>One track on offer: what the matcher and the window need.</summary>
public sealed class TrackInfo
{
    public int Index;            // the track's index in its recording
    public int Recording;        // which offered recording (0 = the first offered)
    public string Name = "";
    public int Channel;          // its MIDI channel (0..15)
    public int BendRange;
    public int NoteCount;
    public byte[]? PresetFile;
}

/// <summary>Every note of the offered tracks, by song position in beats (quarter notes): built off the audio thread, read-only once made.</summary>
public sealed class TrackIndex
{
    public const double Tolerance = 0.06;          // beats: FL's import rounds to its own PPQ, the mixing time moves in blocks
    public readonly TrackInfo[] Tracks;
    // all notes, by beat
    public readonly double[] Beat;
    public readonly byte[] Key;
    public readonly short[] Track;
    public readonly short[] StartBendCents;         // the track's bend at the note's start, in cents at the track's own range
    public readonly short[] StartBendCents2;        // the same bend at FL's default range of 2 semitones
    // the same notes per track (for the shifted hypotheses): [TrackFirst[t], TrackFirst[t + 1])
    public readonly int[] TrackFirst;
    public readonly double[] TBeat;
    public readonly int[] TNote;                    // index into the arrays above

    public TrackIndex(TrackInfo[] tracks, List<(int Track, double Beat, int Key, int BendCents, int BendCents2)> notes)
    {
        Tracks = tracks;
        notes.Sort((a, b) => a.Beat != b.Beat ? a.Beat.CompareTo(b.Beat) : a.Track != b.Track ? a.Track.CompareTo(b.Track) : a.Key.CompareTo(b.Key));
        int n = notes.Count;
        Beat = new double[n]; Key = new byte[n]; Track = new short[n]; StartBendCents = new short[n]; StartBendCents2 = new short[n];
        for (int i = 0; i < n; i++)
        {
            var x = notes[i];
            Beat[i] = x.Beat; Key[i] = (byte)Math.Clamp(x.Key, 0, 127); Track[i] = (short)x.Track;
            StartBendCents[i] = (short)Math.Clamp(x.BendCents, short.MinValue, short.MaxValue); StartBendCents2[i] = (short)Math.Clamp(x.BendCents2, short.MinValue, short.MaxValue);
        }
        TrackFirst = new int[tracks.Length + 1];
        for (int i = 0; i < n; i++) TrackFirst[Track[i] + 1]++;
        for (int t = 0; t < tracks.Length; t++) TrackFirst[t + 1] += TrackFirst[t];
        TBeat = new double[n]; TNote = new int[n];
        var fill = (int[])TrackFirst.Clone();
        for (int i = 0; i < n; i++) { int at = fill[Track[i]]++; TBeat[at] = Beat[i]; TNote[at] = i; }
    }

    public int Count => Beat.Length;

    /// <summary>Whether FL's pitch (cents from C5) is note <paramref name="i"/>'s key: within 50 cents of the key, of the key with the bend the file starts it with, or with that bend at FL's default range.</summary>
    public bool KeyMatches(int i, double pitchCents)
    {
        if (RecMutants.On(RecMutants.UnrelatedGrabbed)) return true;           // (the self-test's deliberate bug: any key fits, so a channel playing other notes at the same times is taken)
        double d = pitchCents - (Key[i] - 60) * 100.0;
        return Math.Abs(d) <= 50 || Math.Abs(d - StartBendCents[i]) <= 50 || Math.Abs(d - StartBendCents2[i]) <= 50;
    }

    /// <summary>The first note at or after <paramref name="beat"/> (binary search).</summary>
    public int LowerBound(double beat)
    {
        int lo = 0, hi = Beat.Length;
        while (lo < hi) { int m = (lo + hi) >> 1; if (Beat[m] < beat) lo = m + 1; else hi = m; }
        return lo;
    }

    /// <summary>Whether track <paramref name="t"/> has a note of this pitch within <paramref name="tol"/> beats of <paramref name="beat"/>.</summary>
    public bool TrackHas(int t, double beat, double pitchCents, double tol)
    {
        int lo = TrackFirst[t], hi = TrackFirst[t + 1];
        while (lo < hi) { int m = (lo + hi) >> 1; if (TBeat[m] < beat - tol) lo = m + 1; else hi = m; }
        for (int i = lo; i < TrackFirst[t + 1] && TBeat[i] <= beat + tol; i++) if (KeyMatches(TNote[i], pitchCents)) return true;
        return false;
    }

    /// <summary>The notes of a transcription's tracks (note-ons of each track's events, in beats; the bend each starts with).</summary>
    public static List<(int Track, double Beat, int Key, int BendCents, int BendCents2)> NotesOf(Transcription tr, int firstTrack)
    {
        var list = new List<(int, double, int, int, int)>();
        for (int k = 0; k < tr.Tracks.Count; k++)
        {
            var t = tr.Tracks[k];
            var ev = t.Smf.Events.OrderBy(e => e.Tick).ThenBy(e => e.Order).ToList();
            int bend = 0, range = Math.Max(1, t.BendRange);
            foreach (var e in ev)
            {
                int kind = e.Status & 0xF0;
                if (kind == RecMidi.Bend) bend = (e.Data1 | e.Data2 << 7) - 8192;
                else if (kind == RecMidi.NoteOn && e.Data2 > 0)
                    list.Add((firstTrack + k, e.Tick / (double)RecMidi.Ppq, e.Data1, (int)Math.Round(bend * range * 100.0 / 8192), (int)Math.Round(bend * 200.0 / 8192)));
            }
        }
        return list;
    }
}

/// <summary>Which track an instance plays, from the notes it has played: one per instance, everything made with it (the audio thread allocates nothing).</summary>
public sealed class TrackMatcher
{
    public const int MaxTracks = 256, MaxHyp = 2048, ShiftedNotes = 4, SettleNotes = 3, UnisonSettleNotes = 8;
    public const double ShiftTolerance = 0.06;

    private readonly int[] hits = new int[MaxTracks], misses = new int[MaxTracks], stamp = new int[MaxTracks];
    private readonly short[] hypTrack = new short[MaxHyp];
    private readonly double[] hypOff = new double[MaxHyp];
    private readonly int[] hypHits = new int[MaxHyp];
    private int hypCount;
    private bool hypOwnClock;
    private TrackIndex? index;
    private int note;

    /// <summary>The track being played (-1 none), whether it is sure (Settled: matching stops), how many tracks still fit (Candidates), how it was found (1 aligned, 2 shifted).</summary>
    public int Bound = -1, Candidates, How, Notes;
    public bool Settled, Unsure;
    /// <summary>Counters for the window and the log: notes that matched nothing, choices let go, switches between unison candidates.</summary>
    public int Unexplained, LetGo, Switches;

    public void Reset(TrackIndex? idx)
    {
        index = idx;
        Array.Clear(hits); Array.Clear(misses); Array.Clear(stamp);
        hypCount = 0; note = 0; Notes = 0;
        Bound = -1; Candidates = 0; How = 0; Settled = false; Unsure = false; Unexplained = 0; LetGo = 0; Switches = 0;
    }

    /// <summary>One note: FL's pitch (cents from C5), its song position in beats (NaN: FL gave none), the instance's own clock in beats, and the note's colour (MIDI channel; -1 unknown).
    /// Updates <see cref="Bound"/> / <see cref="Settled"/>. Allocation-free.</summary>
    public void Feed(TrackIndex idx, double pitchCents, double pos, double ownBeats, int color)
    {
        if (!ReferenceEquals(idx, index)) Reset(idx);
        if (Settled) return;
        note++; Notes++;
        int nt = Math.Min(idx.Tracks.Length, MaxTracks);
        bool havePos = !double.IsNaN(pos);
        bool ignorePos = RecMutants.On(RecMutants.MatchIgnoresPosition);
        int matched = 0;
        if (havePos)
        {
            int a = ignorePos ? 0 : idx.LowerBound(pos - TrackIndex.Tolerance);
            for (int i = a; i < idx.Count && (ignorePos || idx.Beat[i] <= pos + TrackIndex.Tolerance); i++)
            {
                int t = idx.Track[i];
                if (t >= nt || stamp[t] == note || !idx.KeyMatches(i, pitchCents)) continue;
                stamp[t] = note; hits[t]++; matched++;
            }
            for (int t = 0; t < nt; t++) if (stamp[t] != note) misses[t]++;
        }
        int cand = havePos ? Count(nt) : 0;
        if (havePos && cand == 0 && matched > 0)
        {
            // the notes so far fit no track together (playback jumped, a note was edited): start again from this note
            for (int t = 0; t < nt; t++) { bool hit = stamp[t] == note; hits[t] = hit ? 1 : 0; misses[t] = hit ? 0 : 1; }
            cand = Count(nt);
        }
        // the aligned choice: the track kept from before, else the one on the note's MIDI channel, else the lowest
        int choiceA = -1, strengthA = 0;
        if (cand > 0)
        {
            if (Bound >= 0 && Bound < nt && hits[Bound] > 0 && misses[Bound] == 0) choiceA = Bound;
            if (choiceA < 0 && color >= 0)
            {
                int byColor = -1, n = 0;
                for (int t = 0; t < nt; t++) if (hits[t] > 0 && misses[t] == 0 && idx.Tracks[t].Channel == color) { byColor = t; n++; }
                if (n == 1) choiceA = byColor;
            }
            if (choiceA < 0) for (int t = 0; t < nt; t++) if (hits[t] > 0 && misses[t] == 0) { choiceA = t; break; }
            strengthA = hits[choiceA];
        }
        // the shifted hypotheses, kept alongside (song positions offset by a constant, or the instance's own clock when FL gives none)
        int trackS = -1, strengthS = 0;
        double t0 = havePos ? pos : ownBeats;
        if (!double.IsNaN(t0))
        {
            bool own = !havePos;
            if (hypCount > 0 && own == hypOwnClock)
            {
                int kept = 0;
                for (int h = 0; h < hypCount; h++)
                {
                    if (!idx.TrackHas(hypTrack[h], t0 - hypOff[h], pitchCents, ShiftTolerance)) continue;
                    hypTrack[kept] = hypTrack[h]; hypOff[kept] = hypOff[h]; hypHits[kept] = hypHits[h] + 1; kept++;
                }
                hypCount = kept;
            }
            else hypCount = 0;
            if (hypCount == 0) Seed(idx, nt, t0, pitchCents, own);
            else
            {
                int track = hypTrack[0], best = 0;
                bool one = true;
                for (int h = 0; h < hypCount; h++) { if (hypTrack[h] != track) one = false; best = Math.Max(best, hypHits[h]); }
                if (one && best >= ShiftedNotes) { trackS = track; strengthS = best; }
            }
        }
        if (cand == 0) Unexplained++;
        if (strengthA > 0 && strengthA >= strengthS)
        {
            if (Bound >= 0 && choiceA != Bound) Switches++;
            Bound = choiceA; How = 1; Candidates = cand; Unsure = cand > 1;
            Settled = cand == 1 ? strengthA >= SettleNotes : strengthA >= UnisonSettleNotes;
        }
        else if (trackS >= 0)
        {
            if (Bound >= 0 && Bound != trackS) Switches++;
            Bound = trackS; How = 2; Candidates = 1; Unsure = false;
            Settled = strengthS >= ShiftedNotes + 1;
        }
        else LetGoIfProvisional();                    // nothing supports a choice any more (a note the tracks do not have): a provisional one is let go
    }

    private void Seed(TrackIndex idx, int nt, double t0, double pitchCents, bool own)
    {
        hypCount = 0; hypOwnClock = own;
        for (int i = 0; i < idx.Count && hypCount < MaxHyp; i++)
        {
            if (idx.Track[i] >= nt || !idx.KeyMatches(i, pitchCents)) continue;
            hypTrack[hypCount] = idx.Track[i]; hypOff[hypCount] = t0 - idx.Beat[i]; hypHits[hypCount] = 1; hypCount++;
        }
    }

    private void LetGoIfProvisional()
    {
        if (Bound >= 0 && !Settled) { Bound = -1; Candidates = 0; Unsure = false; How = 0; LetGo++; }
    }

    private int Count(int nt)
    {
        int c = 0;
        for (int t = 0; t < nt; t++) if (hits[t] > 0 && misses[t] == 0) c++;
        return c;
    }
}
