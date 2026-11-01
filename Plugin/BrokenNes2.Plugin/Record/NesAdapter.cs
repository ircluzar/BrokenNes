// Round 8 (record-nes): a NES recording's tracks seen through the shape of a SNES transcription, so that everything built for recordings (the Record window, the MIDI files, Save MIDI..., the presets
// in the library, the tracks on offer, their matching by notes, the tracks' expression, TRK1) takes a NES recording as it is, without a second copy of that code. What the shape holds is the
// NES transcription's own: its tracks' names, MIDI channels, bend ranges, events (the same SmfTrack objects), presets and facts; the SNES-only fields (sample, root, envelope) stay empty, and the
// session is a stand-in carrying the recording's name, game, id and length. The NES transcription itself rides beside it (RecordHeld.Nes).
namespace BrokenNes2.Record;

public static class NesAdapter
{
    public static Transcription AsTranscription(NesTranscription n)
    {
        var s = n.Session;
        var stand = new RecSession { Id = s.Id, Name = s.Name, Game = s.Game, Sha1 = s.Sha1, CreatedUnix = s.CreatedUnix, Length = (uint)Math.Min(uint.MaxValue, Math.Round(s.Seconds * 32000)), Dropped = s.Dropped, EndedBy = s.EndedBy };
        var tr = new Transcription
        {
            Session = stand,
            NoteStats = new RecNoteStats(n.NoteStats.Struck, n.Notes.Count, n.NoteStats.StruckSilent, 0, n.NoteStats.BeforeStart, 0),
            Tempo = n.Tempo,
            Map = n.Map,
            FlTempoUsed = n.FlTempoUsed,
            Midi = n.Midi,
            Facts = n.Facts,
        };
        foreach (var t in n.Tracks)
            tr.Tracks.Add(new RecTrack
            {
                Index = t.Index, Version = t.Index, Instrument = t.Index + 1, Lane = 0, Channel = t.MidiChannel, Name = t.Name, BendRange = t.BendRange,
                Notes = new List<int>(t.Notes), Keys = t.Keys, Velocities = t.Velocities, Smf = t.Smf,
                Preset = t.Preset, PresetFile = t.PresetFile, PresetProblem = t.PresetProblem,
                PitchValues = t.PitchValues, PitchExact = t.PitchExact,
            });
        return tr;
    }
}
