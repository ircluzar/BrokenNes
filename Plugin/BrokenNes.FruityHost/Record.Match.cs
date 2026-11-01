// Round 8 follow-up (2026-10-18), rewritten for `reload` (2026-10-18, the user's FL session): the check of TrackMatcher (Record/RecTracks.cs) without the DLL. The notes are fed the way FL 2026
// hands them (Record/FlNotes.cs; the evidence is the host log of the user's session): the pitch in cents above the CHANNEL's base (MIDI note 0 for an imported channel replaced by BrokenNes2, C5
// for one made in FL), the colour = the imported note's MIDI channel - 1, the velocity / 128, NO song position, the time from the instance's own clock at FL's tempo, the transport's start (FL's Play).
// Each note must be bound to the right track AND to the right recorded note (or one that plays exactly the same: same key, velocity, length, bends and controller 7 from its start), because the
// channel plays that note's recorded expression from its own onset.
using BrokenNes2.Record;

namespace BrokenNes.FruityHost;

internal static class MatchChecks
{
    public const int Rate = 48000;

    private static (Transcription Tr, TrackIndex Index, TrackExpression[] Ex) Synthetic()
    {
        var drv = new SynthDriver(132.5);
        var rec = new SpcRecorder(); rec.Arm(); for (int i = 0; i < 40; i++) rec.Feed();
        drv.Play(rec, 20);
        var s = rec.Finish(drv.Aram, drv.Regs, "Match", "synthetic driver", "", 1_760_000_000, 9);
        var tr = RecTranscribe.Run(s, new RecOptions { Name = "Match", FlTempo = 120 });
        return (tr, IndexOf(tr), tr.Tracks.Select(TrackExpression.Of).ToArray());
    }

    /// <summary>The offer's index of a transcription (as TrackOffers builds it: notes, velocities).</summary>
    public static TrackIndex IndexOf(Transcription tr)
    {
        var infos = tr.Tracks.Select((t, k) => new TrackInfo { Index = k, Name = t.Name, Channel = t.Channel, BendRange = t.BendRange, NoteCount = t.Notes.Count }).ToArray();
        var idx = new TrackIndex(infos, TrackIndex.NotesOf(tr, 0));
        idx.SetVelocities(TrackIndex.VelocitiesOf(tr, 0));
        return idx;
    }

    /// <summary>One note as FL plays it.</summary>
    public readonly record struct FlNote(int Note, double Beat, int Key, int Velocity, int Colour);

    /// <summary>A track's notes (TrackIndex order) as FL plays them.</summary>
    public static List<FlNote> NotesOfTrack(TrackIndex idx, int t)
    {
        var l = new List<FlNote>();
        for (int i = idx.TrackFirst[t]; i < idx.TrackFirst[t + 1]; i++) { int n = idx.TNote[i]; l.Add(new FlNote(n, idx.Beat[n], idx.Key[n], idx.Velocity.Length > 0 ? idx.Velocity[n] : 100, idx.Tracks[t].Channel)); }
        return l;
    }

    /// <summary>Whether two notes play the same thing (key, velocity, length, and the track's bends and controller 7 from their starts): identifying one for the other changes nothing heard.</summary>
    public static bool Equivalent(TrackExpression ex, (long On, long Off)[] spans, int a, int b)
    {
        if (a == b) return true;
        if (a < 0 || b < 0 || ex.NoteKey[a] != ex.NoteKey[b] || (ex.NoteVel.Length > 0 && ex.NoteVel[a] != ex.NoteVel[b])) return false;
        long la = spans[a].Off - spans[a].On, lb = spans[b].Off - spans[b].On;
        if (Math.Abs(la - lb) > 1) return false;
        var offs = new SortedSet<long> { 0 };
        foreach (var t in ex.BendTick) { if (t >= spans[a].On && t < spans[a].Off) offs.Add(t - spans[a].On); if (t >= spans[b].On && t < spans[b].Off) offs.Add(t - spans[b].On); }
        foreach (var t in ex.CcTick) { if (t >= spans[a].On && t < spans[a].Off) offs.Add(t - spans[a].On); if (t >= spans[b].On && t < spans[b].Off) offs.Add(t - spans[b].On); }
        foreach (long d in offs)
        {
            double ba = (spans[a].On + d) / 960.0, bb = (spans[b].On + d) / 960.0;
            if (ex.BendRawAt(ba) != ex.BendRawAt(bb) || ex.CcAt(ba) != ex.CcAt(bb)) return false;
        }
        return true;
    }

    /// <summary>Each note's (on, off) tick in the expression's note order (from the track's SMF events).</summary>
    public static (long On, long Off)[] Spans(RecTrack t, TrackExpression ex)
    {
        var spans = new (long, long)[ex.NoteTick.Length];
        var open = new Dictionary<int, Queue<int>>();
        int k = 0;
        foreach (var e in t.Smf.Events.OrderBy(e => e.Tick).ThenBy(e => e.Order))
        {
            int kind = e.Status & 0xF0;
            if (kind == RecMidi.NoteOn && e.Data2 > 0) { if (!open.TryGetValue(e.Data1, out var q)) open[e.Data1] = q = new(); q.Enqueue(k); spans[k] = (e.Tick, e.Tick); k++; }
            else if ((kind == RecMidi.NoteOff || kind == RecMidi.NoteOn) && open.TryGetValue(e.Data1, out var q2) && q2.Count > 0) { int i = q2.Dequeue(); spans[i] = (spans[i].Item1, e.Tick); }
        }
        return spans;
    }

    /// <summary>The note's number in its track's expression (its NoteTick order) for an index note.</summary>
    public static int ExprNumber(TrackIndex idx, int t, int note) { for (int p = idx.TrackFirst[t]; p < idx.TrackFirst[t + 1]; p++) if (idx.TNote[p] == note) return p - idx.TrackFirst[t]; return -1; }

    /// <summary>Feeds FL notes to a fresh matcher: pitch at <paramref name="baseKey"/>, the time from the beats at <paramref name="bpm"/> (the instance's own clock, 48 kHz, notes on 64-sample blocks),
    /// the transport started at <paramref name="playFrom"/> (beats; FL's Play), no song position. Returns, per note, the recorded note the matcher named.</summary>
    public static int[] Play(TrackMatcher m, TrackIndex idx, IReadOnlyList<FlNote> notes, int baseKey, double bpm, double playFrom, double[]? times = null, Func<FlNote, int>? colour = null)
    {
        double bps = bpm / 60.0 / Rate;
        var got = new int[notes.Count];
        for (int i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            double t = times?[i] ?? n.Beat;
            long sample = (long)Math.Round((t - playFrom) / bps) / 64 * 64;
            m.Feed(idx, FlNotes.Pitch(n.Key, baseKey), colour?.Invoke(n) ?? n.Colour, n.Velocity, sample, bps, 64 * bps, double.NaN, (sample) * bps);
            got[i] = m.NoteAt;
        }
        return got;
    }

    public static void Match(ImodelReport r)
    {
        var (tr, idx, exs) = Synthetic();
        int nt = idx.Tracks.Length;
        double bpm = 60e6 / tr.Map[0].MicrosPerQuarter;
        r.Expect(nt == 4 && idx.Count > 100, $"the synthetic recording: {nt} tracks, {idx.Count} notes");
        var spans = tr.Tracks.Select((t, k) => Spans(t, exs[k])).ToArray();
        bool Right(int t, FlNote n, int got) => got >= 0 && idx.Track[got] == t && Equivalent(exs[t], spans[t], ExprNumber(idx, t, n.Note), ExprNumber(idx, t, got));

        // 1. every track from the song's start, on an imported channel (pitch from MIDI 0) and on one made in FL (from C5): the track and the base on the FIRST note (played from it: its colour, or
        //    for colour 0, FL's default, the song's start), settled by the second, and every note identified as the recorded note it is
        int tracksOk = 0, notesAll = 0, notesRight = 0, cases = 0;
        foreach (int baseKey in FlNotes.Bases)
            for (int t = 0; t < nt; t++)
            {
                cases++;
                var m = new TrackMatcher();
                var notes = NotesOfTrack(idx, t);
                var first = new TrackMatcher();
                Play(first, idx, notes.Take(1).ToList(), baseKey, bpm, 0);
                var second = new TrackMatcher();
                Play(second, idx, notes.Take(2).ToList(), baseKey, bpm, 0);
                bool atFirst = first.Bound == t && first.Base == baseKey && second.Settled;
                var got = Play(m, idx, notes, baseKey, bpm, 0);
                int right = Enumerable.Range(0, notes.Count).Count(i => Right(t, notes[i], got[i]));
                notesAll += notes.Count; notesRight += right;
                if (atFirst && m.Bound == t && m.Base == baseKey && right == notes.Count) tracksOk++;
                else r.Expect(false, $"track {t + 1} (base {baseKey}): on its first note track {first.Bound + 1} base {first.Base} settled {first.Settled}; after {notes.Count} notes track {m.Bound + 1} base {m.Base}; {right} of {notes.Count} notes identified");
            }
        r.Expect(tracksOk == cases, $"from the start: {tracksOk} of {cases} (track, base) found on the first note, {notesRight} of {notesAll} notes identified as their recorded note");

        // 2. playback from the middle (FL's Play with the playhead mid-song: the time since Play is not the song's beat): the track on the first note; the notes identified from the third on
        int midRight = 0, midAll = 0, midTracks = 0;
        for (int t = 0; t < nt; t++)
        {
            var notes = NotesOfTrack(idx, t);
            int from = notes.Count / 2;
            var tail = notes.Skip(from).ToList();
            var m = new TrackMatcher();
            double playhead = tail[0].Beat - 0.37;
            var got = Play(m, idx, tail, 0, bpm, playhead);
            if (m.Bound == t) midTracks++;
            for (int i = 2; i < tail.Count; i++) { midAll++; if (Right(t, tail[i], got[i])) midRight++; }
        }
        r.Expect(midTracks == nt && midRight == midAll, $"from the middle: {midTracks} of {nt} tracks found, {midRight} of {midAll} notes from the third identified");

        // 3. a loop: a track's first 12 notes played twice (FL loops back to the song's start with no word to the plugin): every note identified, but the first two after the loop
        int loopRight = 0, loopAll = 0;
        for (int t = 0; t < nt; t++)
        {
            var notes = NotesOfTrack(idx, t).Take(12).ToList();
            if (notes.Count < 3) continue;
            double span = notes[^1].Beat + 0.5;
            var twice = notes.Concat(notes).ToList();
            var times = notes.Select(n => n.Beat).Concat(notes.Select(n => n.Beat + span)).ToArray();
            var m = new TrackMatcher();
            var got = Play(m, idx, twice, 0, bpm, 0, times);
            for (int i = 0; i < twice.Count; i++) { if (i == notes.Count || i == notes.Count + 1) continue; loopAll++; if (Right(t, twice[i], got[i])) loopRight++; }
        }
        r.Expect(loopRight == loopAll && loopAll > 0, $"a loop of each track's first 12 notes, played twice: {loopRight} of {loopAll} notes identified (all but the first two after the loop)");

        // 4. the user's edits: a note added (a key the track does not have there), a note moved by a third of a beat, a note deleted: the added note is no recorded note; the notes after each edit
        //    (from the second) identified again
        {
            int t = 2;
            var notes = NotesOfTrack(idx, t);
            var edited = notes.Take(24).ToList();
            var times = edited.Select(n => n.Beat).ToList();
            int add = 5, move = 11, del = 17;
            edited.Insert(add, new FlNote(-1, edited[add].Beat - 0.11, edited[add].Key + 13, 90, edited[add].Colour)); times.Insert(add, edited[add].Beat);
            times[move + 1] += 0.33;
            edited.RemoveAt(del + 1); times.RemoveAt(del + 1);
            var m = new TrackMatcher();
            var got = Play(m, idx, edited, 0, bpm, 0, times.ToArray());
            bool addedNone = got[add] == -1;
            int after = 0, afterRight = 0;
            for (int i = 0; i < edited.Count; i++)
            {
                if (i == add || i == move + 1 || i == move + 2 || i == del + 1) continue;      // (the edited notes and the one right after each)
                after++; if (Right(t, edited[i], got[i])) afterRight++;
            }
            r.Expect(addedNone && afterRight == after && m.Bound == t, $"edits: the added note {(addedNone ? "is no recorded note" : $"was taken for recorded note {got[add]}")}; {afterRight} of {after} other notes identified; track {m.Bound + 1}");
        }

        // 5. no colour (a channel coloured otherwise): the track from the keys and the time between notes, sure after 4 notes, never before 3
        int noColour = 0;
        for (int t = 0; t < nt; t++)
        {
            var notes = NotesOfTrack(idx, t).Take(8).ToList();
            var m = new TrackMatcher();
            bool early = false;
            for (int i = 0; i < notes.Count; i++) { Play(m, idx, [notes[i]], 0, bpm, 0, [notes[i].Beat], _ => 12); if (i < 2 && m.Settled) early = true; }
            if (m.Bound == t && m.Settled && !early) noColour++;
            else r.Expect(false, $"track {t + 1} without its colour: track {m.Bound + 1}, settled {m.Settled}, settled before the third note {early}");
        }
        r.Expect(noColour == nt, $"without the colour: {noColour} of {nt} tracks found from their keys and intervals");

        // 6. unrelated channels. (a) The user's own notes in FL's default colour (0, which is also track 1's channel): never settled, never played by a track for two notes running. (b) Random notes
        //    in another track's colour: never settled (a note whose key that track has is played by its instrument provisionally: the colour is the import's). (c) A track's notes moved up 37
        //    semitones: never taken.
        var rng = new Random(5);
        int grabbed = 0, settledAny = 0, provisional = 0, randomNotes = 0;
        for (int trial = 0; trial < 20; trial++)
        {
            bool own = trial < 10;
            var m = new TrackMatcher();
            int run = 0;
            for (int i = 0; i < 16; i++)
            {
                double beat = i * 0.5 + rng.NextDouble() * 0.4;
                Play(m, idx, [new FlNote(-1, beat, rng.Next(30, 100), 90, own ? 0 : 1 + trial % (nt - 1))], 0, bpm, 0);
                randomNotes++;
                run = m.Bound >= 0 ? run + 1 : 0;
                if (m.Bound >= 0) provisional++;
                if (own && run >= 2) grabbed++;
                if (m.Settled) settledAny++;
            }
        }
        for (int t = 0; t < nt; t++)
        {
            var m = new TrackMatcher();
            Play(m, idx, NotesOfTrack(idx, t).Take(10).Select(n => n with { Key = n.Key + 37 }).ToList(), 0, bpm, 0);
            if (m.Bound >= 0 || m.Settled) grabbed++;
        }
        r.Expect(grabbed == 0 && settledAny == 0, $"unrelated channels: {settledAny} settled on a track, {grabbed} held one for two notes running in FL's default colour or took a transposed track ({provisional} of {randomNotes} random notes played by the instrument of the track their colour names, provisionally)");

        // 7. more than 15 tracks: the channels the transcription shares (RecTranscribe.ShareChannels) never put two tracks starting on the same key on one channel when another channel is free of
        //    it, and with 23 tracks every track is found on its first note from its colour and key
        {
            var rr = new Random(23);
            var crafted = new List<(int Track, double Beat, int Key, int B, int B2)>();
            var keysets = new List<(int, HashSet<int>)>();
            for (int t = 0; t < 23; t++)
            {
                int first = 40 + rr.Next(0, 6) * 2;                     // (first keys collide on purpose: six choices for 23 tracks)
                var ks = new HashSet<int>();
                double b = rr.Next(0, 4) * 0.5;
                for (int i = 0; i < 12; i++) { int key = i == 0 ? first : 36 + rr.Next(0, 36); crafted.Add((t, b, key, 0, 0)); ks.Add(key); b += 0.25 + rr.Next(0, 4) * 0.25; }
                keysets.Add((first, ks));
            }
            var chans = RecTranscribe.ShareChannels(keysets);
            int clash = 0, maxPer = 0;
            for (int c = 0; c < 16; c++)
            {
                var on = Enumerable.Range(0, 23).Where(k => chans[k] == c).ToList();
                maxPer = Math.Max(maxPer, on.Count);
                for (int a = 0; a < on.Count; a++) for (int b = a + 1; b < on.Count; b++) if (keysets[on[a]].Item1 == keysets[on[b]].Item1) clash++;
            }
            var infos = Enumerable.Range(0, 23).Select(k => new TrackInfo { Index = k, Channel = chans[k] }).ToArray();
            var big = new TrackIndex(infos, crafted);
            int found = 0;
            for (int t = 0; t < 23; t++)
            {
                var m = new TrackMatcher();
                Play(m, big, NotesOfTrack(big, t).Take(1).ToList(), 0, 120, 0);
                if (m.Bound == t && !m.Unsure) found++;
            }
            r.Expect(clash == 0 && maxPer <= 2 && found == 23 && !chans.Contains(9), $"23 tracks on 15 channels: at most {maxPer} per channel, {clash} channels with two tracks starting on the same key, channel 10 {(chans.Contains(9) ? "used" : "left out")}; {found} of 23 tracks found on their first note");
        }

        // 8. a track's expression (keys, velocities, bends, controller 7 by beat): saved and read back the same (version 2, and version 1 without velocities read too); damage refused
        {
            int same = 0, refused = 0, v1 = 0;
            for (int t = 0; t < nt; t++)
            {
                var ex = exs[t];
                var bytes = ex.ToBytes();
                var back = TrackExpression.FromBytes(bytes);
                if (back != null && back.ToBytes().AsSpan().SequenceEqual(bytes) && back.NoteVel.SequenceEqual(ex.NoteVel) && BitConverter.ToUInt16(bytes, 0) == 2) same++;
                var old = new TrackExpression(ex.Range, ex.NoteTick, ex.NoteKey, [], ex.BendTick, ex.Bend, ex.CcTick, ex.Cc);
                var oldBack = TrackExpression.FromBytes(old.ToBytes());
                if (oldBack != null && BitConverter.ToUInt16(old.ToBytes(), 0) == 1 && oldBack.NoteVel.Length == 0 && oldBack.NoteTick.SequenceEqual(ex.NoteTick)) v1++;
                for (int d = 0; d < 12; d++)
                {
                    var bad = (byte[])bytes.Clone();
                    int at = rng.Next(0, bad.Length);
                    bad[at] ^= (byte)(1 << rng.Next(0, 8));
                    var x = TrackExpression.FromBytes(d % 2 == 0 ? bad : bad.AsSpan(0, bad.Length - 1 - rng.Next(0, 4)));
                    if (x == null || !x.ToBytes().AsSpan().SequenceEqual(bytes)) refused++;
                }
            }
            r.Expect(same == nt && v1 == nt && refused == nt * 12, $"expressions: {same} of {nt} read back the same (version 2, with velocities), {v1} of {nt} version 1 read, {refused} of {nt * 12} damaged copies not taken for the original");
        }

        // 9. nothing allocated per note
        {
            var m = new TrackMatcher();
            var notes = NotesOfTrack(idx, 1);
            Play(m, idx, notes, 0, bpm, 0);
            m.Reset(null);
            double bps = bpm / 60.0 / Rate;
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int pass = 0; pass < 50; pass++) { m.Reset(idx); for (int i = 0; i < notes.Count; i++) m.Feed(idx, FlNotes.Pitch(notes[i].Key, 0), notes[i].Colour, notes[i].Velocity, (long)(notes[i].Beat / bps), bps, 64 * bps, double.NaN, notes[i].Beat); }
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            r.Expect(a1 - a0 == 0, $"{a1 - a0:N0} bytes allocated by {50 * notes.Count:N0} notes fed to the matcher");
        }
        r.Note($"{nt} tracks, {idx.Count} notes, fed as FL 2026 hands them (pitch from the channel's base, colour = MIDI channel - 1, no song position): from the start {tracksOk}/{cases} on the first note and {notesRight}/{notesAll} notes identified; from the middle {midRight}/{midAll}; a loop {loopRight}/{loopAll}; edits; no colour {noColour}/{nt}; unrelated settled {settledAny}, held {grabbed}; 23 tracks on shared channels");
    }
}
