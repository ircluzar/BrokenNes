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

    /// <summary>Each note's velocity (1-127; empty: not known). Round 8 record-nes: a NES note whose duty changes while it sounds is on the tracks of each duty, same key, same time; the transcription
    /// gives the copies different velocities, so FL's velocity tells such "unison" tracks apart (<see cref="TrackMatcher"/>).</summary>
    public byte[] Velocity = [];

    /// <summary>The velocity of every note-on of a transcription's tracks, by (track, tick, key).</summary>
    public static Dictionary<(int Track, long Tick, int Key), int> VelocitiesOf(Transcription tr, int firstTrack)
    {
        var d = new Dictionary<(int, long, int), int>();
        for (int k = 0; k < tr.Tracks.Count; k++)
            foreach (var e in tr.Tracks[k].Smf.Events)
                if ((e.Status & 0xF0) == RecMidi.NoteOn && e.Data2 > 0) d[(firstTrack + k, e.Tick, e.Data1)] = e.Data2;
        return d;
    }

    /// <summary>Fills <see cref="Velocity"/> from <see cref="VelocitiesOf"/>.</summary>
    public void SetVelocities(Dictionary<(int Track, long Tick, int Key), int> d)
    {
        var v = new byte[Beat.Length];
        for (int i = 0; i < v.Length; i++) v[i] = (byte)(d.TryGetValue((Track[i], (long)Math.Round(Beat[i] * RecMidi.Ppq), Key[i]), out int x) ? x : 0);
        Velocity = v;
    }

    /// <summary>Whether track <paramref name="t"/> has a note of this pitch AND this velocity (within 1.5 of 127) within <paramref name="tol"/> beats of <paramref name="beat"/>.</summary>
    public bool TrackHasVelocity(int t, double beat, double pitchCents, double velocity, double tol)
    {
        if (Velocity.Length != Beat.Length) return false;
        int lo = TrackFirst[t], hi = TrackFirst[t + 1];
        while (lo < hi) { int m = (lo + hi) >> 1; if (TBeat[m] < beat - tol) lo = m + 1; else hi = m; }
        for (int i = lo; i < TrackFirst[t + 1] && TBeat[i] <= beat + tol; i++) if (KeyMatches(TNote[i], pitchCents) && Math.Abs(Velocity[TNote[i]] - velocity) <= 1.5) return true;
        return false;
    }

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

/// <summary>A track's expression as the recording has it: its notes' keys, its pitch bend (cents) and its controller 7 (percent of the key-on level), by beat. An instance that plays a track can take
/// pitch and volume from here rather than from FL: FL's MIDI import does not bring controllers along with the notes, and (as FL users report) reads every pitch bend at a range of 2 semitones,
/// whatever range the file announces; so after "import, then Replace" the channel's own pitch and volume are not the recording's. Read-only once made; the lookups allocate nothing.</summary>
public sealed class TrackExpression
{
    public const int Version = 1;
    public readonly int Range;
    public readonly int[] NoteTick; public readonly byte[] NoteKey;
    public readonly int[] BendTick; public readonly short[] Bend;            // raw 14-bit values - 8192
    public readonly int[] CcTick; public readonly byte[] Cc;

    public TrackExpression(int range, int[] noteTick, byte[] noteKey, int[] bendTick, short[] bend, int[] ccTick, byte[] cc)
    {
        Range = range; NoteTick = noteTick; NoteKey = noteKey; BendTick = bendTick; Bend = bend; CcTick = ccTick; Cc = cc;
    }

    public static TrackExpression Of(RecTrack t)
    {
        var ev = t.Smf.Events.OrderBy(e => e.Tick).ThenBy(e => e.Order).ToList();
        var nt = new List<int>(); var nk = new List<byte>(); var bt = new List<int>(); var bv = new List<short>(); var ct = new List<int>(); var cv = new List<byte>();
        foreach (var e in ev)
        {
            int kind = e.Status & 0xF0;
            if (kind == RecMidi.NoteOn && e.Data2 > 0) { nt.Add((int)e.Tick); nk.Add(e.Data1); }
            else if (kind == RecMidi.Bend) { bt.Add((int)e.Tick); bv.Add((short)((e.Data1 | e.Data2 << 7) - 8192)); }
            else if (kind == RecMidi.Control && e.Data1 == 7) { ct.Add((int)e.Tick); cv.Add(e.Data2); }
        }
        return new TrackExpression(Math.Max(1, t.BendRange), nt.ToArray(), nk.ToArray(), bt.ToArray(), bv.ToArray(), ct.ToArray(), cv.ToArray());
    }

    private static int Last(int[] ticks, double tick)
    {
        int lo = 0, hi = ticks.Length - 1, at = -1;
        while (lo <= hi) { int m = (lo + hi) >> 1; if (ticks[m] <= tick) { at = m; lo = m + 1; } else hi = m - 1; }
        return at;
    }

    /// <summary>The bend in force at <paramref name="beat"/> in cents (FL's float, as the transcription computed it), 0 before the first.</summary>
    public float BendCentsAt(double beat)
    {
        int i = Last(BendTick, beat * RecMidi.Ppq + 1e-6);
        return i < 0 ? 0f : (float)(Bend[i] * Range * 100.0 / 8192.0);
    }

    /// <summary>FL's pitch for a note of key <paramref name="key"/> at <paramref name="beat"/>: key and bend in cents from C5, as the float FL hands a plugin (the transcription made it exact for this).</summary>
    public float PitchCents(int key, double beat)
    {
        int i = Last(BendTick, beat * RecMidi.Ppq + 1e-6);
        return (float)((key - 60) * 100 + (i < 0 ? 0 : Bend[i]) * Range * 100.0 / 8192.0);
    }

    /// <summary>Controller 7 in force at <paramref name="beat"/> (100 before the first).</summary>
    public int CcAt(double beat) { int i = Last(CcTick, beat * RecMidi.Ppq + 1e-6); return i < 0 ? 100 : Cc[i]; }

    /// <summary>The key of the track's note that starts within <paramref name="tol"/> beats of <paramref name="beat"/> and fits FL's pitch, else -1.</summary>
    public int KeyAt(double beat, double pitchCents, double tol)
    {
        double t0 = (beat - tol) * RecMidi.Ppq, t1 = (beat + tol) * RecMidi.Ppq;
        int lo = 0, hi = NoteTick.Length;
        while (lo < hi) { int m = (lo + hi) >> 1; if (NoteTick[m] < t0) lo = m + 1; else hi = m; }
        int best = -1; double bestD = double.MaxValue;
        for (int i = lo; i < NoteTick.Length && NoteTick[i] <= t1; i++)
        {
            double d = Math.Abs(pitchCents - (NoteKey[i] - 60) * 100.0 - BendCentsAt(NoteTick[i] / (double)RecMidi.Ppq));
            double d0 = Math.Abs(pitchCents - (NoteKey[i] - 60) * 100.0);
            double dd = Math.Min(d, d0);
            if (dd < bestD) { bestD = dd; best = NoteKey[i]; }
        }
        return best;
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)Version); w.Write((ushort)Range);
        w.Write(NoteTick.Length); for (int i = 0; i < NoteTick.Length; i++) { w.Write(NoteTick[i]); w.Write(NoteKey[i]); }
        w.Write(BendTick.Length); for (int i = 0; i < BendTick.Length; i++) { w.Write(BendTick[i]); w.Write(Bend[i]); }
        w.Write(CcTick.Length); for (int i = 0; i < CcTick.Length; i++) { w.Write(CcTick[i]); w.Write(Cc[i]); }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Read back; null when the bytes are not an expression of this version (every count and range checked).</summary>
    public static TrackExpression? FromBytes(ReadOnlySpan<byte> b)
    {
        try
        {
            var r = new BinaryReader(new MemoryStream(b.ToArray()));
            if (r.ReadUInt16() != Version) return null;
            int range = r.ReadUInt16();
            if (range is < 1 or > 96) return null;
            int n = r.ReadInt32(); if (n is < 0 or > 1_000_000) return null;
            var nt = new int[n]; var nk = new byte[n];
            for (int i = 0; i < n; i++) { nt[i] = r.ReadInt32(); nk[i] = r.ReadByte(); if (nk[i] > 127 || (i > 0 && nt[i] < nt[i - 1])) return null; }
            int nb = r.ReadInt32(); if (nb is < 0 or > 4_000_000) return null;
            var bt = new int[nb]; var bv = new short[nb];
            for (int i = 0; i < nb; i++) { bt[i] = r.ReadInt32(); bv[i] = r.ReadInt16(); if (bv[i] is < -8192 or > 8191 || (i > 0 && bt[i] < bt[i - 1])) return null; }
            int nc = r.ReadInt32(); if (nc is < 0 or > 4_000_000) return null;
            var ct = new int[nc]; var cv = new byte[nc];
            for (int i = 0; i < nc; i++) { ct[i] = r.ReadInt32(); cv[i] = r.ReadByte(); if (cv[i] > 127 || (i > 0 && ct[i] < ct[i - 1])) return null; }
            if (r.BaseStream.Position != r.BaseStream.Length) return null;
            return new TrackExpression(range, nt, nk, bt, bv, ct, cv);
        }
        catch (EndOfStreamException) { return null; }
    }
}

/// <summary>Which track an instance plays, from the notes it has played: one per instance, everything made with it (the audio thread allocates nothing).</summary>
public sealed class TrackMatcher
{
    public const int MaxTracks = 256, MaxHyp = 2048, ShiftedNotes = 4, SettleNotes = 3, UnisonSettleNotes = 8;
    public const double ShiftTolerance = 0.06;

    private readonly int[] hits = new int[MaxTracks], misses = new int[MaxTracks], stamp = new int[MaxTracks], velHits = new int[MaxTracks];
    private readonly short[] hypTrack = new short[MaxHyp];
    private readonly double[] hypOff = new double[MaxHyp];
    private readonly int[] hypHits = new int[MaxHyp];
    private int hypCount;
    private bool hypOwnClock;
    private TrackIndex? index;
    private int note;

    /// <summary>The track being played (-1 none), whether it is sure (Settled: matching stops), how many tracks still fit (Candidates), how it was found (1 aligned, 2 shifted).</summary>
    public int Bound = -1, Candidates, How, Notes;
    /// <summary>Where the track is against the time the notes came with: track beat = that time - Offset (0 when aligned); OwnClock: the time was the instance's own clock (FL gave no song position).</summary>
    public double Offset;
    public bool OwnClock;
    public bool Settled, Unsure;
    /// <summary>Counters for the window and the log: notes that matched nothing, choices let go, switches between unison candidates.</summary>
    public int Unexplained, LetGo, Switches;

    public void Reset(TrackIndex? idx)
    {
        index = idx;
        Array.Clear(hits); Array.Clear(misses); Array.Clear(stamp); Array.Clear(velHits);
        hypCount = 0; note = 0; Notes = 0;
        Bound = -1; Candidates = 0; How = 0; Settled = false; Unsure = false; Unexplained = 0; LetGo = 0; Switches = 0;
    }

    /// <summary>One note: FL's pitch (cents from C5), its song position in beats (NaN: FL gave none), the instance's own clock in beats, and the note's colour (MIDI channel; -1 unknown).
    /// Updates <see cref="Bound"/> / <see cref="Settled"/>. Allocation-free.</summary>
    /// <param name="velocity">FL's velocity of the note on the 1-127 scale (FPV_GetVolume x 127; negative: unknown): among tracks that play the same notes it names the one whose note has that velocity
    /// (a NES note on two duty tracks: the transcription gives the copies different velocities).</param>
    public void Feed(TrackIndex idx, double pitchCents, double pos, double ownBeats, int color, double velocity = -1)
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
            for (int t = 0; t < nt; t++) { bool hit = stamp[t] == note; hits[t] = hit ? 1 : 0; misses[t] = hit ? 0 : 1; velHits[t] = 0; }
            cand = Count(nt);
        }
        // the aligned choice: among tracks that play the same notes (unison), the one whose notes have FL's velocity; else the track kept from before, else the one on the note's MIDI channel, else the lowest
        int choiceA = -1, strengthA = 0;
        bool byVelocity = false;
        if (cand > 1 && velocity >= 0 && havePos && idx.Velocity.Length == idx.Count)
        {
            for (int t = 0; t < nt; t++) if (hits[t] > 0 && misses[t] == 0 && idx.TrackHasVelocity(t, pos, pitchCents, velocity, TrackIndex.Tolerance)) velHits[t]++;
            int best = -1, bestN = 0, second = 0;
            for (int t = 0; t < nt; t++)
            {
                if (hits[t] == 0 || misses[t] != 0) continue;
                if (velHits[t] > bestN) { second = bestN; bestN = velHits[t]; best = t; }
                else if (velHits[t] > second) second = velHits[t];
            }
            if (best >= 0 && bestN > second && bestN == hits[best]) { choiceA = best; byVelocity = true; }     // (every note so far had that velocity on that track, and no other track's did as well)
        }
        if (cand > 0 && choiceA < 0)
        {
            if (Bound >= 0 && Bound < nt && hits[Bound] > 0 && misses[Bound] == 0) choiceA = Bound;
            if (choiceA < 0 && color >= 0)
            {
                int byColor = -1, n = 0;
                for (int t = 0; t < nt; t++) if (hits[t] > 0 && misses[t] == 0 && idx.Tracks[t].Channel == color) { byColor = t; n++; }
                if (n == 1) choiceA = byColor;
            }
            if (choiceA < 0) for (int t = 0; t < nt; t++) if (hits[t] > 0 && misses[t] == 0) { choiceA = t; break; }
        }
        if (choiceA >= 0) strengthA = hits[choiceA];
        // the shifted hypotheses, kept alongside (song positions offset by a constant, or the instance's own clock when FL gives none)
        int trackS = -1, strengthS = 0;
        double offS = 0;
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
                for (int h = 0; h < hypCount; h++) { if (hypTrack[h] != track) one = false; if (hypHits[h] > best) { best = hypHits[h]; offS = hypOff[h]; } }
                if (one && best >= ShiftedNotes) { trackS = track; strengthS = best; }
            }
        }
        if (cand == 0) Unexplained++;
        if (strengthA > 0 && strengthA >= strengthS)
        {
            if (Bound >= 0 && choiceA != Bound) Switches++;
            Bound = choiceA; How = 1; Candidates = cand; Unsure = cand > 1 && !byVelocity; Offset = 0; OwnClock = false;
            Settled = cand == 1 || byVelocity ? strengthA >= SettleNotes : strengthA >= UnisonSettleNotes;
        }
        else if (trackS >= 0)
        {
            if (Bound >= 0 && Bound != trackS) Switches++;
            Bound = trackS; How = 2; Candidates = 1; Unsure = false; Offset = offS; OwnClock = hypOwnClock;
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
