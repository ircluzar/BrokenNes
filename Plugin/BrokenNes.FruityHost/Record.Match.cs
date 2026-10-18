// Round 8 follow-up (2026-10-18): the check of TrackMatcher (Record/RecTracks.cs) without the DLL: which track a channel plays, from its notes. The tracks are the synthetic driver's
// transcription (4 instruments) and crafted ones for the cases a real song may hold (two tracks playing the same notes, then parting).
using BrokenNes2.Record;

namespace BrokenNes.FruityHost;

internal static class MatchChecks
{
    private static (Transcription Tr, TrackIndex Index) Synthetic()
    {
        var drv = new SynthDriver(132.5);
        var rec = new SpcRecorder(); rec.Arm(); for (int i = 0; i < 40; i++) rec.Feed();
        drv.Play(rec, 20);
        var s = rec.Finish(drv.Aram, drv.Regs, "Match", "synthetic driver", "", 1_760_000_000, 9);
        var tr = RecTranscribe.Run(s, new RecOptions { Name = "Match", FlTempo = 120 });
        var infos = tr.Tracks.Select((t, k) => new TrackInfo { Index = k, Name = t.Name, Channel = t.Channel, BendRange = t.BendRange, NoteCount = t.Notes.Count }).ToArray();
        return (tr, new TrackIndex(infos, TrackIndex.NotesOf(tr, 0)));
    }

    /// <summary>A track's notes as the matcher's index holds them: (beat, key, the bend it starts with in cents at the track's range).</summary>
    private static List<(double Beat, int Key, int Bend)> NotesOfTrack(TrackIndex idx, int t)
    {
        var l = new List<(double, int, int)>();
        for (int i = idx.TrackFirst[t]; i < idx.TrackFirst[t + 1]; i++) { int n = idx.TNote[i]; l.Add((idx.Beat[n], idx.Key[n], idx.StartBendCents[n])); }
        return l;
    }

    public static void Match(ImodelReport r)
    {
        var (tr, idx) = Synthetic();
        int nt = idx.Tracks.Length;
        r.Expect(nt == 4 && idx.Count > 100, $"the synthetic recording: {nt} tracks, {idx.Count} notes");
        var rng = new Random(5);
        // 1. every track, from its first note, in both ways FL may hand the pitch (with the file's bend at the note's start, or without it): bound before the first note sounds, settled in 3
        int boundFirst = 0, uniqueFirst = 0, settled = 0, right = 0, cases = 0;
        foreach (bool withBend in new[] { false, true })
            for (int t = 0; t < nt; t++)
            {
                var m = new TrackMatcher();
                var notes = NotesOfTrack(idx, t);
                cases++;
                for (int i = 0; i < notes.Count && i < 12; i++)
                {
                    var n = notes[i];
                    double pitch = (n.Key - 60) * 100 + (withBend ? n.Bend : 0) + rng.Next(-20, 21), beat = n.Beat + (rng.NextDouble() - 0.5) * 0.02;
                    m.Feed(idx, pitch, beat, double.NaN, -1);
                    if (i > 0) continue;
                    // the first note: tracks that have it (the same key at that time) besides this one make it ambiguous, the rest must be bound at once
                    var have = new HashSet<int>();
                    for (int j = 0; j < idx.Count; j++) if (Math.Abs(idx.Beat[j] - beat) <= TrackIndex.Tolerance && idx.KeyMatches(j, pitch)) have.Add(idx.Track[j]);
                    if (have.Count == 1) { uniqueFirst++; if (m.Bound == t) boundFirst++; }
                }
                if (m.Bound == t) right++;
                if (m.Settled) settled++;
                if (m.Bound != t) r.Expect(false, $"track {t + 1} ({(withBend ? "with" : "without")} the start bend): the matcher says {m.Bound + 1} after {m.Notes} notes ({m.Candidates} candidates)");
            }
        r.Expect(right == cases && settled == cases && boundFirst == uniqueFirst && uniqueFirst > 0, $"aligned: {right} of {cases} tracks found and {settled} settled; {boundFirst} of the {uniqueFirst} whose first note no other track has were bound on it");
        // 2. playback started in the middle (any note of a track, not only its first)
        int mid = 0;
        for (int t = 0; t < nt; t++)
        {
            var m = new TrackMatcher();
            var notes = NotesOfTrack(idx, t);
            int from = notes.Count / 2;
            m.Feed(idx, (notes[from].Key - 60) * 100, notes[from].Beat, double.NaN, -1);
            if (m.Bound == t) mid++;
        }
        r.Expect(mid == nt, $"from the middle of the song: {mid} of {nt} tracks found on their first note");
        // 3. an unrelated channel: random notes, and other keys at the very times a track plays: never taken, never settled
        int grabbed = 0, brief = 0;
        for (int trial = 0; trial < 20; trial++)
        {
            var m = new TrackMatcher();
            bool everSettled = false, held = false;
            int run = 0;
            for (int i = 0; i < 16; i++)
            {
                m.Feed(idx, rng.Next(-30, 30) * 100, rng.NextDouble() * 40, rng.NextDouble() * 40, -1);
                everSettled |= m.Settled;
                run = m.Bound >= 0 ? run + 1 : 0;
                if (run == 1) brief++;
                held |= run >= 2;                            // (a random note can fall on a track's note: taken for that note, let go at the next)
            }
            if (everSettled || held) grabbed++;
        }
        for (int t = 0; t < nt; t++)
        {
            var m = new TrackMatcher();
            foreach (var n in NotesOfTrack(idx, t).Take(10)) m.Feed(idx, (n.Key - 60 + 37) * 100, n.Beat, double.NaN, -1);      // (keys no track plays, at the track's own times)
            if (m.Bound >= 0 || m.Settled) grabbed++;
        }
        r.Expect(grabbed == 0, $"unrelated channels: {grabbed} of {20 + nt} taken by a track for more than one note or settled ({brief} single random notes fell on a track's note and were let go at the next)");
        // 4. the song moved by 16 bars (a pattern placed later than the file put it): found after 3 notes by a constant offset
        int shifted = 0;
        for (int t = 0; t < nt; t++)
        {
            var m = new TrackMatcher();
            foreach (var n in NotesOfTrack(idx, t).Skip(3).Take(6)) m.Feed(idx, (n.Key - 60) * 100, n.Beat + 64, double.NaN, -1);
            if (m.Bound == t && m.How == 2 && m.Settled) shifted++;
            else r.Expect(false, $"track {t + 1} 64 beats later: the matcher says {m.Bound + 1} (how {m.How}, settled {m.Settled})");
        }
        r.Expect(shifted == nt, $"a song 64 beats later: {shifted} of {nt} tracks found by their offset and settled within 6 notes");
        // 5. no song position from FL: the instance's own clock (any start), found the same way
        int own = 0;
        for (int t = 0; t < nt; t++)
        {
            var m = new TrackMatcher();
            double start = 7.3;
            foreach (var n in NotesOfTrack(idx, t).Take(6)) m.Feed(idx, (n.Key - 60) * 100, double.NaN, n.Beat + start + (rng.NextDouble() - 0.5) * 0.03, -1);
            if (m.Bound == t && m.Settled) own++;
        }
        r.Expect(own == nt, $"no song position: {own} of {nt} tracks found from the instance's own clock");
        // 6. unison: two crafted tracks play the same 4 notes, then part; and the note's colour (its MIDI channel) settling it at once
        var a = new List<(int, double, int, int, int)>();
        for (int i = 0; i < 8; i++) { a.Add((0, i, 60 + (i % 3), 0, 0)); a.Add((1, i, 60 + (i % 3) + (i >= 4 ? 5 : 0), 0, 0)); }
        var uni = new TrackIndex([new TrackInfo { Index = 0, Channel = 0 }, new TrackInfo { Index = 1, Channel = 1 }], a);
        var mu = new TrackMatcher();
        bool unsureAtFirst = false;
        for (int i = 0; i < 8; i++)
        {
            mu.Feed(uni, (60 + (i % 3) + (i >= 4 ? 5 : 0) - 60) * 100, i, double.NaN, -1);
            if (i == 0) unsureAtFirst = mu.Unsure && mu.Bound == 0 && !mu.Settled;
        }
        r.Expect(unsureAtFirst && mu.Bound == 1 && mu.Switches == 1 && mu.Settled, $"unison: unsure on the first note (plays track 1 meanwhile: {unsureAtFirst}), then track {mu.Bound + 1} after they parted ({mu.Switches} switch, settled {mu.Settled})");
        var mc = new TrackMatcher();
        mc.Feed(uni, 0, 0, double.NaN, 1);
        r.Expect(mc.Bound == 1, $"unison with the note's colour = track 2's MIDI channel: track {mc.Bound + 1} at once");
        // 7. a note added by the user before the choice is sure lets the choice go; the next notes of the track bring it back
        {
            var m = new TrackMatcher();
            var notes = NotesOfTrack(idx, 2);
            m.Feed(idx, (notes[0].Key - 60) * 100, notes[0].Beat, double.NaN, -1);
            bool first = m.Bound == 2;
            m.Feed(idx, (notes[0].Key - 60 + 41) * 100, notes[0].Beat + 0.37, double.NaN, -1);
            bool letGo = m.Bound == -1;
            for (int i = 1; i < 5; i++) m.Feed(idx, (notes[i].Key - 60) * 100, notes[i].Beat, double.NaN, -1);
            r.Expect(first && letGo && m.Bound == 2 && m.Settled, $"an added note: bound {first}, let go {letGo}, back on track {m.Bound + 1} (settled {m.Settled})");
        }
        // 8. nothing allocated per note
        {
            var m = new TrackMatcher();
            var notes = NotesOfTrack(idx, 1);
            m.Feed(idx, 0, 0, 0, -1);
            m.Reset(null);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int pass = 0; pass < 50; pass++) { m.Reset(idx); foreach (var n in notes) m.Feed(idx, (n.Key - 60) * 100, double.NaN, n.Beat + 3, -1); foreach (var n in notes) m.Feed(idx, (n.Key - 60) * 100, n.Beat, double.NaN, -1); }
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            r.Expect(a1 - a0 == 0, $"{a1 - a0:N0} bytes allocated by {50 * notes.Count * 2:N0} notes fed to the matcher");
        }
        r.Note($"{nt} tracks, {idx.Count} notes: aligned {right}/{cases} (on the first note {boundFirst} of the {uniqueFirst} unambiguous ones), from the middle {mid}/{nt}, shifted by 16 bars {shifted}/{nt}, own clock {own}/{nt}, unrelated taken {grabbed}/{20 + nt}, unison resolved, colour, an added note");
    }
}
