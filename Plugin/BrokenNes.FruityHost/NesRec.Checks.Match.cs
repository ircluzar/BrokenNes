// Round 8 (record-nes), updated for `reload` (2026-10-18): `nesrec-test match`: FL's import-and-Replace with NES tracks (Record/RecTracks.cs). A NES game's recording seen as FL would offer it
// (NesAdapter), each track's own notes fed to a fresh matcher the way FL 2026 hands them to a replaced channel (Record/FlNotes.cs: pitch from MIDI note 0, colour = the MIDI channel - 1, velocity
// / 128, no song position, the instance's own clock): every track that has notes must be the one found, also the duty tracks whose notes are copies of each other (a note that changed duty is on
// both: their colours differ). Without the colour, FL's velocity must still tell the copies apart (the transcription gives them velocities 3 apart); without colour AND velocity such tracks stay
// undecided or wrong (the evidence that those are what tell them apart).
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
            var idx = MatchChecks.IndexOf(tr);
            double bpm = 60e6 / tr.Map[0].MicrosPerQuarter;
            int found = 0, tried = 0, foundNoColour = 0, foundNothing = 0, copies = 0, notesRight = 0, notesAll = 0;
            string? wrong = null;
            for (int k = 0; k < tr.Tracks.Count; k++)
            {
                if (ntr.Tracks[k].Channel == 4 || tr.Tracks[k].Notes.Count < 3) continue;
                var notes = MatchChecks.NotesOfTrack(idx, k).Take(12).ToList();
                // does this track share its first notes with another (copies of duty-changing notes)?
                bool shared = notes.Take(8).All(o => Enumerable.Range(0, tr.Tracks.Count).Any(j => j != k && idx.TrackHas(j, o.Beat, (o.Key - 60) * 100, TrackIndex.Tolerance)));
                if (shared) copies++;
                tried++;
                var m = new TrackMatcher();
                var got = MatchChecks.Play(m, idx, notes, FlNotes.BaseImported, bpm, 0);
                notesAll += notes.Count; notesRight += Enumerable.Range(0, notes.Count).Count(i => got[i] == notes[i].Note);
                if (m.Bound == k && m.Base == FlNotes.BaseImported) found++;
                else wrong ??= $"track {k + 1} ({tr.Tracks[k].Name}) found as {m.Bound + 1} (base {m.Base}){(shared ? " (its first notes are copies of another track's)" : "")}";
                var m2 = new TrackMatcher();
                MatchChecks.Play(m2, idx, notes, FlNotes.BaseImported, bpm, 0, null, _ => 15);              // (a colour no track has: the keys, the time between notes and FL's velocity)
                if (m2.Bound == k && !m2.Unsure) foundNoColour++;
                var m3 = new TrackMatcher();
                MatchChecks.Play(m3, idx, notes.Select(n => n with { Velocity = -1 }).ToList(), FlNotes.BaseImported, bpm, 0, null, _ => 15);
                if (m3.Bound == k && !m3.Unsure) foundNothing++;
            }
            r.Expect(found == tried && tried > 0 && notesRight == notesAll, $"{g.Name}: {found} of {tried} tracks found from their own first 12 notes as FL hands them, {notesRight} of {notesAll} notes identified; first wrong: {wrong}");
            r.Expect(foundNoColour == tried, $"{g.Name}: without the colour {foundNoColour} of {tried} tracks found from their keys, intervals and FL's velocity");
            // every copy of a note on several tracks has a velocity of its own (3 apart), unless the transcription counted it as one it could not separate
            int pairs = 0, same = 0;
            var byNote = new Dictionary<int, List<int>>();
            for (int k = 0; k < ntr.Tracks.Count; k++) for (int q = 0; q < ntr.Tracks[k].Notes.Count; q++) { int ni = ntr.Tracks[k].Notes[q]; if (!byNote.TryGetValue(ni, out var l)) byNote[ni] = l = new(); l.Add(ntr.Tracks[k].Velocities[q]); }
            foreach (var vs in byNote.Values.Where(v => v.Count > 1)) { pairs++; if (vs.Distinct().Count() < vs.Count || vs.Zip(vs.Skip(1)).Any(z => Math.Abs(z.First - z.Second) < 3)) same++; }
            r.Expect(same <= ntr.TiedCopies, $"{g.Name}: {same} of {pairs} notes on several tracks have copies whose velocities are not 3 apart ({ntr.TiedCopies} counted as impossible)");
            r.Note($"{g.Name}: {found} of {tried} tracks found from their colour and notes ({copies} whose first notes are copies of another track's), {notesRight}/{notesAll} notes identified; without the colour {foundNoColour} of {tried} (velocity), without colour and velocity {foundNothing} of {tried} found for sure");
        }
    }
}
