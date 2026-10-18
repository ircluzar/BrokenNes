// Round 8 (record-nes): `nesrec-test vrun-song`, the cross-check against a driver whose intent is KNOWN: songs converted by VRUN's own converter (the copy the plugin carries, Music/Vrun), written into
// the AudioPlayer template (what Test MIDI export plays), run on the plugin's machine and recorded. VRUN's engine plays four opcode streams on frame counts, and BrokenDev's decoder model (MidiModel,
// round 7) says what every frame's registers must be. The recording's TRANSCRIPTION (its notes: when each channel sounds, its period and volume) is turned back into those registers frame by frame
// and scored against the model: it must agree on every field of every frame once the decode has begun. And the tempo it finds must be the MIDI file's (the converter keeps the first tempo; its
// notes land on whole frames): the error is stated. The songs: BrokenDev's synthetic song (120 BPM) and VRUN's AudioPlayer roster (when the VRUN tree is on this machine).
using BrokenNes2.Music;
using BrokenNes2.Record;
using NesEmulator.Plugin;

namespace BrokenNes.FruityHost;

internal static partial class NesRecChecks
{
    public static void VrunSong(ImodelReport r)
    {
        var bundled = MidiFixtures.Bundled();
        var template = MusicTemplate.Load(bundled.Rom, bundled.Manifest, out var tp);
        if (template == null) { r.Expect(false, "the music test template does not load: " + tp); return; }
        var songs = new List<(string Name, byte[] Midi)> { ("synthetic 120 BPM", MidiFixtures.Synthetic()) };
        var roster = MidiFixtures.RosterSongs();
        if (roster.Count == 0) r.Warn("NOT RUN (partly): the VRUN tree is not on this machine: only the synthetic song is checked");
        songs.AddRange(roster.Take(GameSeconds >= 20 ? 6 : 2).Select(s => (s.Name, s.Midi)));
        double worstTempo = 0;
        int songsDone = 0, tempoExact = 0, tempoGrid = 0;
        foreach (var (name, midi) in songs)
        {
            var build = SongConvert.Convert(midi, out var why);
            if (build == null) { r.Warn($"{name}: the converter refused it ({why})"); continue; }
            var rom = template.Patch(build.Channels, build.Facts.LoopFrames, name);
            var g = new NesRecGame(rom, name);
            int frames = (int)Math.Min(Math.Max(build.Facts.LoopFrames + 60, 600), 25 * NesAudioMachine.FramesPerSecond);
            var s = g.Record(frames, name);
            var tr = NesTranscribe.Run(s, new NesOptions { Name = name });
            // the frame ends (CPU cycles from the start) and the latch trace the game's writes leave at each
            var ends = new List<uint>();
            var trace = new List<byte>();
            var latch = new byte[0x18];
            for (int i = 0; i < s.Count; i++)
            {
                s.Get(i, out uint t, out int reg, out int v);
                if (reg == NesSession.MarkerFrame) { ends.Add(t); trace.AddRange(latch); continue; }
                if (reg <= 0x17) latch[reg] = (byte)v;
            }
            var tra = trace.ToArray();
            var best = MidiModel.Best(build.Channels, tra, ends.Count - 130, 0, 120, skip: 2);
            r.Expect(best.Agree == best.Of, $"{name}: the ROM's own registers agree with VRUN's decoder model on {best.Agree} of {best.Of} fields (decode from frame {best.Start}): the song did not play as converted, nothing to compare with");
            // frame by frame, from the decode's start: what the TRANSCRIPTION says each channel does at the frame's end (sounding or not; its period and volume) against the registers VRUN's engine
            // wrote, which the decoder model predicts (above): a pulse or the noise sounds when its volume is above 0, the triangle when its linear counter is set (VRUN writes $7F or 0)
            var notesOf = Enumerable.Range(0, 4).Select(c => tr.Notes.Where(n => n.Channel == c).OrderBy(n => n.Start).ToList()).ToArray();
            var at = new int[4];
            long fields = 0, agree = 0;
            var diffs = new List<string>();
            for (int f = best.Start + 2; f < ends.Count - 130; f++)
            {
                uint T = ends[f];
                var l = tra.AsSpan(f * 0x18, 0x18);
                for (int c = 0; c < 4; c++)
                {
                    var list = notesOf[c];
                    while (at[c] < list.Count && list[at[c]].End <= T) at[c]++;
                    var n = at[c] < list.Count && list[at[c]].Start <= T ? list[at[c]] : null;
                    bool regSounds = c switch { 0 => (l[0] & 15) > 0, 1 => (l[4] & 15) > 0, 2 => (l[8] & 0x7F) > 0, _ => (l[0xC] & 15) > 0 };
                    int regP = c switch { 0 => l[2] | (l[3] & 7) << 8, 1 => l[6] | (l[7] & 7) << 8, 2 => l[0xA] | (l[0xB] & 7) << 8, _ => l[0xE] & 15 };
                    int regV = c switch { 0 => l[0] & 15, 1 => l[4] & 15, 2 => 15, _ => l[0xC] & 15 };
                    fields++;
                    bool ok = n == null ? !regSounds : regSounds && NesNote.At(n.Periods, T) == regP && NesNote.At(n.Vols, T) == regV;
                    if (ok) agree++;
                    else if (diffs.Count < 6) diffs.Add($"frame {f} channel {c}: the transcription {(n == null ? "silent" : $"period {NesNote.At(n.Periods, T):X3} volume {NesNote.At(n.Vols, T)}")}, the engine {(regSounds ? $"period {regP:X3} volume {regV}" : "silent")}");
                }
            }
            r.Expect(agree == fields, $"{name}: the transcription agrees with what VRUN's engine wrote on {agree} of {fields} frame-channels; first differences: {string.Join("; ", diffs)}");
            var mine = new MidiModel.Score(best.Start, agree, fields, 0, []);
            // the tempo: the MIDI file's first tempo (the converter's own rule), folded into 80-160 BPM
            var mr = MidiReplay.Read(midi);
            double bpm = 60_000_000.0 / mr.Tempo[0].Us;
            while (bpm >= RecTempo.BeatHigh) bpm /= 2;
            while (bpm < RecTempo.BeatLow) bpm *= 2;
            double err = tr.Tempo.Found ? Math.Round((tr.Tempo.Bpm / bpm - 1) * 100, 2) + 0.0 : double.NaN;
            // exact (0.5 %), or the right GRID with another beat: the song's beat a whole number of the tatum found (a ternary feel counted in twos: the bar lines move, every note still sits on the grid)
            double ratio = tr.Tempo.Found ? 60.0 / bpm / tr.Tempo.TatumSeconds : 0;
            bool exactTempo = tr.Tempo.Found && Math.Abs(err) <= 0.5, sameGrid = tr.Tempo.Found && Math.Abs(ratio - Math.Round(ratio)) < 0.01 * ratio && Math.Round(ratio) >= 1;
            if (exactTempo) { worstTempo = Math.Max(worstTempo, Math.Abs(err)); tempoExact++; }
            else if (sameGrid) tempoGrid++;
            r.Expect(!tr.Tempo.Found || exactTempo || sameGrid, $"{name}: tempo {tr.Tempo.Describe()}, the MIDI file's {bpm:0.00} BPM ({err:+0.00;-0.00} %), and its grid ({tr.Tempo.TatumSeconds * 1000:0.0} ms) does not divide the file's beat");
            if (!tr.Tempo.Found) r.Warn($"{name}: no tempo found ({tr.Tempo.Why}); the MIDI file's is {bpm:0.00} BPM");
            if (!exactTempo && sameGrid) r.Warn($"{name}: the beat found ({tr.Tempo.Bpm:0.00} BPM) is not the file's ({bpm:0.00} BPM), but the grid is: the file's beat is {Math.Round(ratio)} of its {tr.Tempo.TatumSeconds * 1000:0.0} ms steps, the estimate counts {tr.Tempo.TatumsPerBeat:0.##} (a ternary feel)");
            songsDone++;
            r.Note($"{name}: {tr.Notes.Count} notes on {tr.Tracks.Count} tracks; the transcription = VRUN's decoder model on {mine.Agree:N0} of {mine.Of:N0} frame fields (the ROM itself: {best.Agree:N0} of {best.Of:N0}, decode from frame {best.Start}); tempo {(tr.Tempo.Found ? $"{tr.Tempo.Bpm:0.00} BPM against the file's {bpm:0.00} ({err:+0.00;-0.00} %)" : "not found")}");
        }
        r.Expect(songsDone > 0, "no song was checked");
        r.Note($"{songsDone} songs; tempo: {tempoExact} the file's within {worstTempo:0.00} %, {tempoGrid} on the file's grid with another beat (ternary feel), {songsDone - tempoExact - tempoGrid} not found");
    }
}
