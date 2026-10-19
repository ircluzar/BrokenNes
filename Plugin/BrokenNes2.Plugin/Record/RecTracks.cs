// Round 8 follow-up (record, 2026-10-18): which track of a recording an FL channel is playing, and which of its notes, from the notes FL asks it to play.
//
// The user's workflow: drag the recording's MIDI file into FL (one channel per track, the notes in each channel's piano roll), then Replace each channel by BrokenNes2. FL does not tell a
// plugin which channel it replaced; the notes come to the plugin one by one as FL plays them. What FL hands with each note (the user's FL session of 2026-10-18, Record/FlNotes.cs):
//   colour    the imported note's MIDI channel - 1. The recording gives every track a MIDI channel of its own (16 colours: past 15 tracks a channel is shared by two tracks whose notes start
//             apart, RecTranscribe.AssignChannels), so the colour names the track, or one of two.
//   pitch     cents above the CHANNEL's base key: MIDI note 0 on an imported-then-replaced channel, C5 (MIDI 60) on a channel made in FL. The base is not told: it is found here, from the keys.
//   timing    no song position (FHD_GetMixingTime answered nothing as the plugin called it); the instance's own clock (samples it rendered, at FL's tempo) measures the time between notes.
//
// TrackMatcher (one per instance, its arrays made with it: the audio thread allocates nothing) keeps HYPOTHESES (track, base key, which of the track's notes the last note was). The first note
// seeds them with every note of the colour's track(s) whose key the pitch names at either base; each next note keeps a hypothesis when the track has a note of the next key at the time that
// passed since (the interval), and moves it there. One track and one base left: that is the channel's track and base. The note a hypothesis points at is WHICH RECORDED NOTE FL just started:
// the channel plays that note's own bends, volume and velocity from the note's own onset (Bn2Plugin), so nothing needs FL's song position. When FL gives a song position, or the transport started
// from the song's beginning (the time since FL's Play), hypotheses aligned with it are preferred at a seed: playback from the start is decided on its first note.
//   a jump    (playback from the middle, a loop, a note the user moved): no hypothesis explains a note; the hypotheses are seeded again from it; the next notes decide among them.
//   an edit   a note the recording does not have explains nothing: it plays as FL gives it (its key, its velocity, no recorded bend), counted.
//   no colour (a channel the user coloured otherwise, an older file): every track is a candidate and four consistent notes are needed.
// Settled (the track is sure; the instance moves to the tracks' emulator and the matcher keeps following the notes): one track and one base left, and either the colour named it and the first
// note was where the song's start (or FL's position) puts it, or two notes in a row fit (four without the colour).
namespace BrokenNes2.Record;

/// <summary>One track on offer: what the matcher and the window need.</summary>
public sealed class TrackInfo
{
    public int Index;            // the track's index in its recording
    public int Recording;        // which offered recording (0 = the first offered)
    public string Name = "";
    public int Channel;          // its MIDI channel (0..15): FL's note colour
    public int BendRange;
    public int NoteCount;
    public byte[]? PresetFile;
}

/// <summary>Every note of the offered tracks, by song position in beats (quarter notes): built off the audio thread, read-only once made.</summary>
public sealed class TrackIndex
{
    public const double Tolerance = 0.06;          // beats: FL's import rounds to its own PPQ, notes start on block boundaries
    public readonly TrackInfo[] Tracks;
    // all notes, by beat
    public readonly double[] Beat;
    public readonly byte[] Key;
    public readonly short[] Track;
    public readonly short[] StartBendCents;         // the track's bend at the note's start, in cents at the track's own range
    public readonly short[] StartBendCents2;        // the same bend at FL's default range of 2 semitones
    // the same notes per track: [TrackFirst[t], TrackFirst[t + 1]), by beat
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

    /// <summary>Each note's velocity (1-127; empty: not known). A NES note whose duty changes while it sounds is on the tracks of each duty, same key, same time; the transcription gives the copies
    /// different velocities. A channel that plays a recorded note plays this velocity (FL hands velocity / 128, the transcription's levels are velocity / 127: Record/FlNotes.cs).</summary>
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

    /// <summary>Whether FL's pitch (cents from C5, the channel's base applied: FlNotes.ToC5Cents) is note <paramref name="i"/>'s key: within 50 cents of the key, of the key with the bend the file starts
    /// it with, or with that bend at FL's default range.</summary>
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

    /// <summary>The first position of track <paramref name="t"/>'s notes (in <see cref="TBeat"/>) at or after <paramref name="beat"/>.</summary>
    public int TrackLowerBound(int t, double beat)
    {
        int lo = TrackFirst[t], hi = TrackFirst[t + 1];
        while (lo < hi) { int m = (lo + hi) >> 1; if (TBeat[m] < beat) lo = m + 1; else hi = m; }
        return lo;
    }

    /// <summary>Whether track <paramref name="t"/> has a note of this pitch within <paramref name="tol"/> beats of <paramref name="beat"/>.</summary>
    public bool TrackHas(int t, double beat, double pitchCents, double tol)
    {
        for (int i = TrackLowerBound(t, beat - tol); i < TrackFirst[t + 1] && TBeat[i] <= beat + tol; i++) if (KeyMatches(TNote[i], pitchCents)) return true;
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

    /// <summary>A one-track index of a track's expression (a project's TRK1 brings the expression back without the recording: the channel still follows its notes). UI thread.</summary>
    public static TrackIndex FromExpression(TrackExpression ex, int channel)
    {
        var notes = new List<(int, double, int, int, int)>();
        var vel = new Dictionary<(int, long, int), int>();
        for (int i = 0; i < ex.NoteTick.Length; i++)
        {
            double beat = ex.NoteTick[i] / (double)RecMidi.Ppq;
            int b = ex.BendRawAt(beat);
            notes.Add((0, beat, ex.NoteKey[i], (int)Math.Round(b * ex.Range * 100.0 / 8192), (int)Math.Round(b * 200.0 / 8192)));
            if (ex.NoteVel.Length == ex.NoteTick.Length) vel[(0, ex.NoteTick[i], ex.NoteKey[i])] = ex.NoteVel[i];
        }
        var idx = new TrackIndex([new TrackInfo { Index = 0, Channel = channel, BendRange = ex.Range, NoteCount = ex.NoteTick.Length }], notes);
        if (vel.Count > 0) idx.SetVelocities(vel);
        return idx;
    }
}

/// <summary>A track's expression as the recording has it: its notes' keys and velocities, its pitch bend (cents) and its controller 7 (percent of the key-on level), by beat. An instance that plays a
/// track takes pitch and volume from here rather than from FL: FL's MIDI import does not bring controllers along with the notes, and (as FL users report) reads every pitch bend at a range of 2
/// semitones, whatever range the file announces; so after "import, then Replace" the channel's own pitch and volume are not the recording's. Each note is played from its OWN onset (the note FL
/// started, identified by TrackMatcher): no song position is needed. Read-only once made; the lookups allocate nothing.</summary>
public sealed class TrackExpression
{
    public const int Version = 2;
    public readonly int Range;
    public readonly int[] NoteTick; public readonly byte[] NoteKey;
    /// <summary>Each note's velocity (version 2; empty when read from a version 1 expression: FL's velocity is played then).</summary>
    public readonly byte[] NoteVel;
    public readonly int[] BendTick; public readonly short[] Bend;            // raw 14-bit values - 8192
    public readonly int[] CcTick; public readonly byte[] Cc;

    public TrackExpression(int range, int[] noteTick, byte[] noteKey, byte[] noteVel, int[] bendTick, short[] bend, int[] ccTick, byte[] cc)
    {
        Range = range; NoteTick = noteTick; NoteKey = noteKey; NoteVel = noteVel; BendTick = bendTick; Bend = bend; CcTick = ccTick; Cc = cc;
    }

    public static TrackExpression Of(RecTrack t)
    {
        var ev = t.Smf.Events.OrderBy(e => e.Tick).ThenBy(e => e.Order).ToList();
        var nt = new List<int>(); var nk = new List<byte>(); var nv = new List<byte>(); var bt = new List<int>(); var bv = new List<short>(); var ct = new List<int>(); var cv = new List<byte>();
        foreach (var e in ev)
        {
            int kind = e.Status & 0xF0;
            if (kind == RecMidi.NoteOn && e.Data2 > 0) { nt.Add((int)e.Tick); nk.Add(e.Data1); nv.Add(e.Data2); }
            else if (kind == RecMidi.Bend) { bt.Add((int)e.Tick); bv.Add((short)((e.Data1 | e.Data2 << 7) - 8192)); }
            else if (kind == RecMidi.Control && e.Data1 == 7) { ct.Add((int)e.Tick); cv.Add(e.Data2); }
        }
        return new TrackExpression(Math.Max(1, t.BendRange), nt.ToArray(), nk.ToArray(), nv.ToArray(), bt.ToArray(), bv.ToArray(), ct.ToArray(), cv.ToArray());
    }

    private static int Last(int[] ticks, double tick)
    {
        int lo = 0, hi = ticks.Length - 1, at = -1;
        while (lo <= hi) { int m = (lo + hi) >> 1; if (ticks[m] <= tick) { at = m; lo = m + 1; } else hi = m - 1; }
        return at;
    }

    /// <summary>The raw bend (14-bit - 8192) in force at <paramref name="beat"/>, 0 before the first.</summary>
    public int BendRawAt(double beat) { int i = Last(BendTick, beat * RecMidi.Ppq + 1e-6); return i < 0 ? 0 : Bend[i]; }

    /// <summary>The bend in force at <paramref name="beat"/> in cents (FL's float, as the transcription computed it), 0 before the first.</summary>
    public float BendCentsAt(double beat)
    {
        int i = Last(BendTick, beat * RecMidi.Ppq + 1e-6);
        return i < 0 ? 0f : (float)(Bend[i] * Range * 100.0 / 8192.0);
    }

    /// <summary>The pitch the transcription made exact for a note of key <paramref name="key"/> at <paramref name="beat"/>: key and bend in cents from C5 (what the engines take).</summary>
    public float PitchCents(int key, double beat)
    {
        int i = Last(BendTick, beat * RecMidi.Ppq + 1e-6);
        return (float)((key - 60) * 100 + (i < 0 ? 0 : Bend[i]) * Range * 100.0 / 8192.0);
    }

    /// <summary>Controller 7 in force at <paramref name="beat"/> (100 before the first).</summary>
    public int CcAt(double beat) { int i = Last(CcTick, beat * RecMidi.Ppq + 1e-6); return i < 0 ? 100 : Cc[i]; }

    /// <summary>The key of the track's note that starts within <paramref name="tol"/> beats of <paramref name="beat"/> and fits the pitch (cents from C5), else -1.</summary>
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
        bool v2 = NoteVel.Length == NoteTick.Length && NoteTick.Length > 0;
        w.Write((ushort)(v2 ? 2 : 1)); w.Write((ushort)Range);
        w.Write(NoteTick.Length); for (int i = 0; i < NoteTick.Length; i++) { w.Write(NoteTick[i]); w.Write(NoteKey[i]); if (v2) w.Write(NoteVel[i]); }
        w.Write(BendTick.Length); for (int i = 0; i < BendTick.Length; i++) { w.Write(BendTick[i]); w.Write(Bend[i]); }
        w.Write(CcTick.Length); for (int i = 0; i < CcTick.Length; i++) { w.Write(CcTick[i]); w.Write(Cc[i]); }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Read back (version 1: no velocities; version 2); null when the bytes are not an expression this code knows (every count and range checked).</summary>
    public static TrackExpression? FromBytes(ReadOnlySpan<byte> b)
    {
        try
        {
            var r = new BinaryReader(new MemoryStream(b.ToArray()));
            int version = r.ReadUInt16();
            if (version is < 1 or > Version) return null;
            int range = r.ReadUInt16();
            if (range is < 1 or > 96) return null;
            int n = r.ReadInt32(); if (n is < 0 or > 1_000_000) return null;
            var nt = new int[n]; var nk = new byte[n]; var nv = version >= 2 ? new byte[n] : [];
            for (int i = 0; i < n; i++)
            {
                nt[i] = r.ReadInt32(); nk[i] = r.ReadByte();
                if (version >= 2) { nv[i] = r.ReadByte(); if (nv[i] is 0 or > 127) return null; }
                if (nk[i] > 127 || (i > 0 && nt[i] < nt[i - 1])) return null;
            }
            if (version >= 2 && n == 0) return null;
            int nb = r.ReadInt32(); if (nb is < 0 or > 4_000_000) return null;
            var bt = new int[nb]; var bv = new short[nb];
            for (int i = 0; i < nb; i++) { bt[i] = r.ReadInt32(); bv[i] = r.ReadInt16(); if (bv[i] is < -8192 or > 8191 || (i > 0 && bt[i] < bt[i - 1])) return null; }
            int nc = r.ReadInt32(); if (nc is < 0 or > 4_000_000) return null;
            var ct = new int[nc]; var cv = new byte[nc];
            for (int i = 0; i < nc; i++) { ct[i] = r.ReadInt32(); cv[i] = r.ReadByte(); if (cv[i] > 127 || (i > 0 && ct[i] < ct[i - 1])) return null; }
            if (r.BaseStream.Position != r.BaseStream.Length) return null;
            return new TrackExpression(range, nt, nk, nv, bt, bv, ct, cv);
        }
        catch (EndOfStreamException) { return null; }
    }
}

/// <summary>Which track an instance plays and which recorded note each of its notes is, from the notes FL starts: one per instance, every array made with it (the audio thread allocates nothing).
/// See the file's head for the method.</summary>
public sealed class TrackMatcher
{
    public const int MaxTracks = 256, MaxHyp = 4096, MaxBases = 128;
    /// <summary>Notes in a row that must fit (key and time between them) for a track to be SURE (settled: the instance moves to the tracks' emulator): with a colour that names it (FL's import keeps the
    /// MIDI channel in the colour), and without a colour, or with colour 0 (FL's default colour: a note the user drew is colour 0 too). When the first of them is where the song's start or FL's
    /// position puts it: that one note with a colour (not 0) naming the track, two with colour 0, three without. Before that the track is played PROVISIONALLY from the first note when the colour (not 0) names it, else once
    /// two notes fit or the first is where the song's start puts it; a note that contradicts a provisional track lets it go.</summary>
    public const int SettleByColour = 3, SettleWithoutColour = 4;
    public const int ShiftedNotes = SettleWithoutColour;           // (the older name: notes a channel without colour or position needs)

    // the hypotheses: track, base (index into bases), position in the track's notes (TrackIndex.TBeat / TNote), notes in a row explained
    private short[] hT = new short[MaxHyp], nT = new short[MaxHyp];
    private byte[] hB = new byte[MaxHyp], nB = new byte[MaxHyp];
    private int[] hAt = new int[MaxHyp], nAt = new int[MaxHyp];
    private int[] hN = new int[MaxHyp], nN = new int[MaxHyp];
    private int hCount;
    private readonly int[] bases = new int[MaxBases];
    private int baseCount;
    private bool anyBase;                                           // the bases were taken from the first note's keys (none of FlNotes.Bases fitted)
    private readonly int[] trackStamp = new int[MaxTracks];
    private int stamp;
    private TrackIndex? index;
    private long lastSample;
    private bool haveLast;
    private int missesInRow;
    private bool alignedChain;                                      // the hypotheses were seeded where the song's start (or FL's position) puts the note, and carried on since

    /// <summary>The track being played (-1 none), how sure (Settled: the track is decided; the notes are still followed), tracks still fitting (Candidates), how it was found (1 the colour and the
    /// keys, 2 the keys and intervals alone, 3 by hand or by name: locked).</summary>
    public int Bound = -1, Candidates, How, Notes;
    public bool Settled, Unsure;
    /// <summary>The channel's base key (FlNotes: 0 for an imported channel, 60 for one made in FL); -1 not known yet. BaseSure: every hypothesis left agrees on it.</summary>
    public int Base = -1;
    public bool BaseSure;
    /// <summary>The recorded note FL just started (an index into the TrackIndex's notes), -1 when the note is not in the recording; whether that is sure (one hypothesis), whether it was aligned with
    /// FL's song position or the transport's start.</summary>
    public int NoteAt = -1;
    public bool NoteSure, Aligned;
    /// <summary>Whether the colour named the candidates (a track of that MIDI channel exists), and whether that colour is FL's default one (0: a note the user drew has it too, so it is weak evidence).</summary>
    public bool ByColour, ColourWeak;
    /// <summary>Counters for the window and the log: notes no recorded note explains, choices let go, switches between candidates, seeds after the first (jumps, loops, edits).</summary>
    public int Unexplained, LetGo, Switches, Jumps;
    /// <summary>A track (and base, -1: to be found) given by hand or by the channel's name, or settled: only its notes are followed.</summary>
    public int LockedTrack = -1, LockedBase = -1;

    // (the older interface's fields: the window and the tests read them)
    public double Offset;
    public bool OwnClock;

    public void Reset(TrackIndex? idx)
    {
        index = idx;
        hCount = 0; Notes = 0; haveLast = false; missesInRow = 0;
        Bound = -1; Candidates = 0; How = 0; Settled = false; Unsure = false; Unexplained = 0; LetGo = 0; Switches = 0; Jumps = 0;
        Base = -1; BaseSure = false; NoteAt = -1; NoteSure = false; Aligned = false; ByColour = false; ColourWeak = false; alignedChain = false;
        LockedTrack = -1; LockedBase = -1; anyBase = false; baseCount = 0; velRun = 0;
    }

    /// <summary>Only track <paramref name="track"/> is followed from now (settled, picked by hand, named, restored); <paramref name="baseKey"/> -1 while the base is to be found.</summary>
    public void Lock(TrackIndex idx, int track, int baseKey)
    {
        if (!ReferenceEquals(idx, index)) { Reset(idx); }
        LockedTrack = track; LockedBase = baseKey;
        Bound = track; Settled = true; Unsure = false; Candidates = 1;
        if (baseKey >= 0) { Base = baseKey; BaseSure = true; }
        if (How == 0) How = 3;
        // the hypotheses of other tracks or bases go
        int k = 0;
        for (int h = 0; h < hCount; h++)
            if (hT[h] == track && (baseKey < 0 || bases[hB[h]] == baseKey)) { hT[k] = hT[h]; hB[k] = hB[h]; hAt[k] = hAt[h]; hN[k] = hN[h]; k++; }
        hCount = k;
    }

    /// <summary>The transport stopped or jumped: the next note seeds the hypotheses again (the track and base found are kept).</summary>
    public void Restart() { hCount = 0; haveLast = false; alignedChain = false; }

    /// <summary>One note FL started. <paramref name="flPitch"/> as FL hands it (InitLevels.Pitch: cents above the channel's base), <paramref name="colour"/> FPV_GetColor (-1 unknown),
    /// <paramref name="velocity"/> the MIDI velocity (FlNotes.MidiVelocity; -1 unknown), <paramref name="sample"/> the instance's own clock at the note (samples), <paramref name="beatsPerSample"/>
    /// FL's tempo, <paramref name="blockBeats"/> the length of FL's last block in beats (a note can start anywhere in it), <paramref name="songPos"/> FL's song position in beats (NaN: none),
    /// <paramref name="transportBeat"/> the beats since FL's Play (NaN: not playing). Allocation-free.</summary>
    public void Feed(TrackIndex idx, double flPitch, int colour, int velocity, long sample, double beatsPerSample, double blockBeats, double songPos, double transportBeat)
    {
        if (!ReferenceEquals(idx, index)) { int lt = LockedTrack, lb = LockedBase; Reset(idx); if (lt >= 0 && lt < idx.Tracks.Length) Lock(idx, lt, lb); }
        Notes++;
        int nt = Math.Min(idx.Tracks.Length, MaxTracks);
        double delta = haveLast ? (sample - lastSample) * beatsPerSample : double.NaN;
        double tol = Math.Max(TrackIndex.Tolerance, 1.2 * blockBeats);
        bool keyOnly = RecMutants.On(RecMutants.FollowByKeyOnly);
        // 1. the hypotheses that the time since the last note and this key carry on
        int kept = 0;
        if (hCount > 0 && haveLast && !keyOnly)
        {
            double tolD = tol + 0.01 * Math.Abs(delta);
            for (int h = 0; h < hCount; h++)
            {
                int t = hT[h];
                if (LockedTrack >= 0 && t != LockedTrack) continue;
                double c5 = FlNotes.ToC5Cents(flPitch, bases[hB[h]]);
                double expected = idx.TBeat[hAt[h]] + delta;
                int best = -1; double bestD = double.MaxValue;
                for (int p = idx.TrackLowerBound(t, expected - tolD); p < idx.TrackFirst[t + 1] && idx.TBeat[p] <= expected + tolD; p++)
                {
                    if (p == hAt[h] || !idx.KeyMatches(idx.TNote[p], c5)) continue;
                    double d = Math.Abs(idx.TBeat[p] - expected);
                    if (d < bestD) { bestD = d; best = p; }
                }
                if (best < 0) continue;
                bool dup = false;
                for (int q = Math.Max(0, kept - 4); q < kept; q++) if (nAt[q] == best && nT[q] == t && nB[q] == hB[h]) { dup = true; break; }
                if (dup) continue;
                nT[kept] = (short)t; nB[kept] = hB[h]; nAt[kept] = best; nN[kept] = hN[h] + 1; kept++;
            }
        }
        Swap(kept);
        // 2. none: seed them from this note (the first note, a jump, a loop, an edit)
        bool seeded = false;
        Aligned = false;
        if (hCount == 0)
        {
            seeded = true;
            if (Notes > 1) Jumps++;
            Seed(idx, nt, flPitch, colour);
            // prefer the hypotheses where FL's song position, or the song's start (the time since FL's Play), puts the note
            double pos = !double.IsNaN(songPos) ? songPos : transportBeat;
            if (!double.IsNaN(pos) && hCount > 1 && !keyOnly)
            {
                int k = 0;
                for (int h = 0; h < hCount; h++)
                    if (Math.Abs(idx.TBeat[hAt[h]] - pos) <= tol) { hT[k] = hT[h]; hB[k] = hB[h]; hAt[k] = hAt[h]; hN[k] = hN[h]; k++; }
                if (k > 0) { hCount = k; Aligned = true; }
            }
            else if (!double.IsNaN(pos) && hCount == 1 && Math.Abs(idx.TBeat[hAt[0]] - pos) <= tol) Aligned = true;
            alignedChain = Aligned;
        }
        lastSample = sample; haveLast = true;
        if (hCount == 0)
        {
            // nothing in the recording is this note (an added or transposed note, another channel's notes): it plays as FL gives it
            NoteAt = -1; NoteSure = false;
            Unexplained++;
            if (!Settled && Bound >= 0 && ++missesInRow >= 1) { Bound = -1; Candidates = 0; Unsure = false; How = 0; LetGo++; Base = -1; BaseSure = false; }      // (a provisional track this note contradicts is let go)
            haveLast = false;                                       // (the next note seeds again: no interval from a note that is not in the recording)
            return;
        }
        missesInRow = 0;
        // 3. FL's velocity: FL's import keeps each note's velocity, and the recording gives notes that sound different different velocities (the key-on level; a NES note on two duty tracks:
        //    copies 3 apart). The hypotheses whose note has FL's velocity are kept when there are some (when the user changed a velocity, none has it: all stay). This tells apart notes of the
        //    same key at the same intervals with another volume (an echo; playback from the middle), and tracks that play the same notes.
        bool byVelocity = false;
        if (velocity > 0 && idx.Velocity.Length == idx.Count && hCount > 1 && !RecMutants.On(RecMutants.FollowByKeyOnly))
        {
            int k = 0;
            for (int h = 0; h < hCount; h++) if (Math.Abs(idx.Velocity[idx.TNote[hAt[h]]] - velocity) <= 1) k++;
            if (k > 0 && k < hCount)
            {
                bool manyTracks = !SameTrackAll();
                for (int h = 0, w = 0; h < hCount; h++)
                {
                    if (Math.Abs(idx.Velocity[idx.TNote[hAt[h]]] - velocity) > 1) continue;
                    hT[w] = hT[h]; hB[w] = hB[h]; hAt[w] = hAt[h]; hN[w] = hN[h]; w++;
                }
                hCount = k;
                byVelocity = manyTracks && SameTrackAll();           // (the velocity named the track among tracks that play the same notes)
            }
        }
        velRun = byVelocity && (velRun == 0 || hT[0] == Bound) ? velRun + 1 : 0;
        // which hypothesis this note is: the track and base played so far, else the first
        int pick = -1;
        if (pick < 0 && Bound >= 0) for (int h = 0; h < hCount; h++) if (hT[h] == Bound && (Base < 0 || bases[hB[h]] == Base)) { pick = h; break; }
        if (pick < 0) pick = 0;
        int track = hT[pick];
        NoteAt = idx.TNote[hAt[pick]];
        NoteSure = hCount == 1;
        // 4. the track, the base, how sure
        stamp++;
        int cand = 0;
        bool baseAgrees = true;
        for (int h = 0; h < hCount; h++)
        {
            if (trackStamp[hT[h]] != stamp) { trackStamp[hT[h]] = stamp; cand++; }
            if (hT[h] == track && hB[h] != hB[pick]) baseAgrees = false;
        }
        int inRow = hN[pick];
        bool strong = ByColour && !ColourWeak && !anyBase;
        // played from this note: a track the colour names, or one whose notes fit twice or start where the song's start puts them (else this note keeps the channel's own sound)
        if (LockedTrack >= 0 || strong || alignedChain || inRow >= 2 || byVelocity)
        {
            if (Bound >= 0 && Bound != track) Switches++;
            Bound = track; Candidates = cand; Unsure = cand > 1 && !byVelocity;
            Base = bases[hB[pick]]; BaseSure = baseAgrees;
            if (LockedTrack < 0) How = ByColour && !ColourWeak ? 1 : 2;
        }
        else { Candidates = cand; Unsure = cand > 1; }
        if (!Settled && Bound == track)
        {
            int need = alignedChain ? (strong ? 1 : ByColour && !anyBase ? 2 : SettleWithoutColour - 1) : strong ? SettleByColour : SettleWithoutColour;
            bool sure = baseAgrees && (cand == 1 && inRow >= need || velRun >= SettleWithoutColour);
            if (sure) Settled = true;
        }
        _ = seeded;
    }

    private int velRun;

    private bool SameTrackAll() { for (int h = 1; h < hCount; h++) if (hT[h] != hT[0]) return false; return true; }

    private void Swap(int kept)
    {
        (hT, nT) = (nT, hT); (hB, nB) = (nB, hB); (hAt, nAt) = (nAt, hAt); (hN, nN) = (nN, hN);
        hCount = kept;
    }

    /// <summary>The first note's hypotheses: every note of the candidate tracks (the colour's, else all) whose key the pitch names at a base (FlNotes.Bases, or the one found or locked). When the
    /// colour names a track and no usual base fits, every base under which the pitch is one of the track's keys (the base comes from the notes; more notes must agree before it is sure).</summary>
    private void Seed(TrackIndex idx, int nt, double flPitch, int colour)
    {
        hCount = 0;
        // the candidate tracks
        ByColour = false;
        int ch = RecMutants.On(RecMutants.ColourIgnored) ? -1 : FlNotes.ChannelOfColour(colour);
        if (LockedTrack < 0 && ch >= 0) for (int t = 0; t < nt; t++) if (idx.Tracks[t].Channel == ch) { ByColour = true; break; }
        ColourWeak = ByColour && ch == 0;
        // the bases
        baseCount = 0; anyBase = false;
        if (LockedBase >= 0) bases[baseCount++] = LockedBase;
        else if (BaseSure && Base >= 0 && Settled) bases[baseCount++] = Base;
        else if (RecMutants.On(RecMutants.OldPitchBase)) bases[baseCount++] = FlNotes.BaseC5;           // (the deliberate bug: FL's pitch read from C5 only, the r13 matcher)
        else foreach (int b in FlNotes.Bases) bases[baseCount++] = b;
        AddSeeds(idx, nt, flPitch, ch);
        if (hCount == 0 && ByColour)
        {
            // the colour's tracks have no such key (a file made with other channels, notes the user recoloured): every track is a candidate, as without a colour
            ByColour = false; ColourWeak = false;
            AddSeeds(idx, nt, flPitch, ch);
        }
        if (hCount == 0 && LockedBase < 0 && !(BaseSure && Settled) && !RecMutants.On(RecMutants.OldPitchBase) && LockedTrack >= 0)
        {
            // a track given by hand (Play here) or by the channel's name, and no usual base fits: a base under which the pitch is one of the track's keys; the next notes must agree (a channel FL
            // set another root note on). Not when the track is still to be found: a transposed or unrelated channel would then fit some base of some track.
            baseCount = 0; anyBase = true;
            for (int t = 0; t < nt; t++)
            {
                if (t != LockedTrack) continue;
                for (int p = idx.TrackFirst[t]; p < idx.TrackFirst[t + 1]; p++)
                {
                    int b = (int)Math.Round(idx.Key[idx.TNote[p]] - flPitch / 100.0);
                    if (b < 0 || b > 127) continue;                     // (a root note is a key of FL's keyboard)
                    bool have = false;
                    for (int q = 0; q < baseCount; q++) if (bases[q] == b) { have = true; break; }
                    if (!have && baseCount < MaxBases) bases[baseCount++] = b;
                }
            }
            AddSeeds(idx, nt, flPitch, ch);
        }
    }

    private void AddSeeds(TrackIndex idx, int nt, double flPitch, int ch)
    {
        for (int bi = 0; bi < baseCount; bi++)
        {
            double c5 = FlNotes.ToC5Cents(flPitch, bases[bi]);
            for (int t = 0; t < nt; t++)
            {
                if (LockedTrack >= 0 ? t != LockedTrack : ByColour && idx.Tracks[t].Channel != ch) continue;
                for (int p = idx.TrackFirst[t]; p < idx.TrackFirst[t + 1] && hCount < MaxHyp; p++)
                {
                    if (!idx.KeyMatches(idx.TNote[p], c5)) continue;
                    hT[hCount] = (short)t; hB[hCount] = (byte)bi; hAt[hCount] = p; hN[hCount] = 1; hCount++;
                }
            }
        }
    }
}
