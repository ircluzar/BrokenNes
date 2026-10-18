// Round 8 (record-nes): `nesrec-test match`: FL's import-and-Replace with NES tracks (Record/RecTracks.cs, the SNES work's matcher). A NES game's recording seen as FL would offer it (NesAdapter), each
// track's own notes fed to a fresh matcher the way an untouched instance gets them (FL's pitch = key + the start bend at the file's range, the song position in beats, FL's velocity): every track that
// has notes must be the one found, also the duty tracks whose notes are copies of each other (a note that changed duty is on both: the copies' velocities differ, FL's velocity tells them apart).
// Without the velocity the same feeding must leave such tracks undecided or wrong (the evidence that the velocity is what tells them apart).
using BrokenNes2.Record;
using NesEmulator.Plugin;

namespace BrokenNes.FruityHost;

internal static partial class NesRecChecks
{
    public static void Match(ImodelReport r)
    {
        foreach (var (pick, park, script) in new[] { ("Base Wars", 300, "300:start,4"), ("Alien 3", 300, "300:start,4"), ("VRUN", 60, "") })
        {
            var g = pick == "VRUN" ? NesRecGame.Vrun() : NesRecGame.Find(pick, script);
            if (g == null) { r.Warn($"NOT RUN: {pick} is not in {NesRecGame.Roster}"); continue; }
            g.Run(park);
            var ntr = NesTranscribe.Run(g.Record((int)(Math.Min(GameSeconds, 20) * NesAudioMachine.FramesPerSecond), "match"), new NesOptions { Name = "match" });
            var tr = NesAdapter.AsTranscription(ntr);
            var infos = tr.Tracks.Select((t, k) => new TrackInfo { Index = k, Name = t.Name, Channel = t.Channel, BendRange = t.BendRange, NoteCount = t.Notes.Count }).ToArray();
            var idx = new TrackIndex(infos, TrackIndex.NotesOf(tr, 0));
            idx.SetVelocities(TrackIndex.VelocitiesOf(tr, 0));
            int found = 0, tried = 0, foundNoVel = 0, copies = 0;
            string? wrong = null;
            for (int k = 0; k < tr.Tracks.Count; k++)
            {
                if (ntr.Tracks[k].Channel == 4 || tr.Tracks[k].Notes.Count < 3) continue;
                // the track's notes as FL plays them: key, the bend the file starts it with (the track's range), the song position, the velocity
                var ons = new List<(double Beat, int Key, int Vel, double Bend)>();
                int bend = 0;
                foreach (var e in tr.Tracks[k].Smf.Events.OrderBy(e => e.Tick).ThenBy(e => e.Order))
                {
                    int kind = e.Status & 0xF0;
                    if (kind == RecMidi.Bend) bend = (e.Data1 | e.Data2 << 7) - 8192;
                    else if (kind == RecMidi.NoteOn && e.Data2 > 0) ons.Add((e.Tick / (double)RecMidi.Ppq, e.Data1, e.Data2, bend * tr.Tracks[k].BendRange * 100.0 / 8192));
                }
                // does this track share its first notes with another (copies of duty-changing notes)?
                bool shared = ons.Take(8).All(o => Enumerable.Range(0, tr.Tracks.Count).Any(j => j != k && idx.TrackHas(j, o.Beat, (o.Key - 60) * 100 + o.Bend, TrackIndex.Tolerance)));
                if (shared) copies++;
                tried++;
                var m = new TrackMatcher();
                foreach (var o in ons.Take(12)) m.Feed(idx, (o.Key - 60) * 100 + o.Bend, o.Beat, double.NaN, -1, o.Vel);
                if (m.Bound == k) found++;
                else wrong ??= $"track {k + 1} ({tr.Tracks[k].Name}) found as {m.Bound + 1}{(shared ? " (its first notes are copies of another track's)" : "")}";
                var m2 = new TrackMatcher();
                foreach (var o in ons.Take(12)) m2.Feed(idx, (o.Key - 60) * 100 + o.Bend, o.Beat, double.NaN, -1);
                if (m2.Bound == k && !m2.Unsure) foundNoVel++;
            }
            r.Expect(found == tried && tried > 0, $"{g.Name}: {found} of {tried} tracks found from their own first 12 notes; first wrong: {wrong}");
            // every copy of a note on several tracks has a velocity of its own (3 apart), unless the transcription counted it as one it could not separate
            int pairs = 0, same = 0;
            var byNote = new Dictionary<int, List<int>>();
            for (int k = 0; k < ntr.Tracks.Count; k++) for (int q = 0; q < ntr.Tracks[k].Notes.Count; q++) { int ni = ntr.Tracks[k].Notes[q]; if (!byNote.TryGetValue(ni, out var l)) byNote[ni] = l = new(); l.Add(ntr.Tracks[k].Velocities[q]); }
            foreach (var vs in byNote.Values.Where(v => v.Count > 1)) { pairs++; if (vs.Distinct().Count() < vs.Count || vs.Zip(vs.Skip(1)).Any(z => Math.Abs(z.First - z.Second) < 3)) same++; }
            r.Expect(same <= ntr.TiedCopies, $"{g.Name}: {same} of {pairs} notes on several tracks have copies whose velocities are not 3 apart ({ntr.TiedCopies} counted as impossible)");
            r.Note($"{g.Name}: {found} of {tried} tracks found from their notes ({copies} whose first notes are copies of another track's); without FL's velocity {foundNoVel} of {tried} found for sure");
        }
    }
}
