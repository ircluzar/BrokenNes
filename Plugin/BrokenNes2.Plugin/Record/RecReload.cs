// Round 8 `reload` (2026-10-18): a recording read back from the files it left (its MIDI file and its tracks' presets), so its tracks can be offered without the session that made them: a recording
// made before FL was restarted, or one the test bench replays from the user's own FL session (the log replay check). The transcription's tracks are rebuilt from the file's events exactly as the
// recording wrote them (names, MIDI channels, the RPN 0 bend range, every note, bend and controller with the writer's order at one tick), so TrackOffers, the matcher and the expression read the
// same thing they read from a fresh recording. Pure code, no UI.
namespace BrokenNes2.Record;

public static class RecReload
{
    /// <summary>A Standard MIDI File as the recording wrote it (type 1: a conductor named after the recording, one track per instrument), with each track's preset found by its name
    /// (<paramref name="presetOf"/> gets the track's name and returns the preset file's bytes, or null). Null with the reason when the file is not one this can read.</summary>
    public static (Transcription? Tr, string? Problem) FromMidi(byte[] midi, Func<string, byte[]?> presetOf)
    {
        List<(string Name, List<(long Tick, byte St, byte D1, byte D2)> Ev, List<(long Tick, int Us)> Tempo)> tracks;
        int ppq;
        try { (tracks, ppq) = Read(midi); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException) { return (null, "the MIDI file is damaged: " + e.Message); }
        if (tracks.Count < 2) return (null, "the MIDI file has no instrument track (a recording's file has a conductor and one track per instrument)");
        if (ppq != RecMidi.Ppq) return (null, $"the MIDI file's resolution is {ppq} ticks per beat, a recording's is {RecMidi.Ppq}: it was not written by BrokenNes2's Record (or FL saved it again)");
        string name = tracks[0].Name.Length > 0 ? tracks[0].Name : "recording";
        var tr = new Transcription { Session = new RecSession { Name = name, Id = IdOf(midi) } };
        foreach (var (t, us) in tracks[0].Tempo.Take(1)) tr.Map.Add((0, 0, RecMidi.Ppq * 1e6 / us, us));
        if (tr.Map.Count == 0) tr.Map.Add((0, 0, RecMidi.Ppq * 2.0, 500_000));
        for (int k = 1; k < tracks.Count; k++)
        {
            var (tn, ev, _) = tracks[k];
            var t = new RecTrack { Index = k - 1, Version = k - 1, Instrument = k, Name = tn };
            int ch = ev.Count > 0 ? ev[0].St & 15 : RecTranscribe.ChannelOf(k - 1);
            t.Channel = ch;
            int rpnMsb = 127, rpnLsb = 127, range = 2, notes = 0;
            foreach (var (tick, st, d1, d2) in ev)
            {
                int kind = st & 0xF0;
                int order = kind switch { RecMidi.NoteOff => 0, RecMidi.NoteOn => d2 == 0 ? 0 : 3, RecMidi.Control => 1, RecMidi.Bend => 2, _ => 1 };
                if (kind == RecMidi.Control)
                {
                    if (d1 == 101) rpnMsb = d2; else if (d1 == 100) rpnLsb = d2; else if (d1 == 6 && rpnMsb == 0 && rpnLsb == 0) range = Math.Max(1, (int)d2);
                }
                if (kind == RecMidi.NoteOn && d2 > 0) notes++;
                t.Smf.Events.Add(new SmfEvent(tick, order, st, d1, d2));
            }
            t.BendRange = range;
            t.Smf.Name = tn;
            for (int i = 0; i < notes; i++) t.Notes.Add(i);                     // (the count is what the offer shows; the notes themselves are in the events)
            t.PresetFile = presetOf(tn);
            if (t.PresetFile == null) t.PresetProblem = $"no preset named \"{tn}\" was found beside the recording";
            tr.Tracks.Add(t);
        }
        tr.Midi = midi;
        return (tr, null);
    }

    /// <summary>A stable id for a recording read from its file (the file's bytes decide it: the same file is the same recording).</summary>
    public static ulong IdOf(byte[] midi)
    {
        ulong h = 1469598103934665603UL;
        foreach (byte b in midi) { h ^= b; h *= 1099511628211UL; }
        return h | 1UL << 63;
    }

    private static (List<(string, List<(long, byte, byte, byte)>, List<(long, int)>)> Tracks, int Ppq) Read(byte[] b)
    {
        if (b.Length < 14 || b[0] != 'M' || b[1] != 'T' || b[2] != 'h' || b[3] != 'd') throw new InvalidDataException("no MThd header");
        int ntrk = b[10] << 8 | b[11], div = b[12] << 8 | b[13];
        if ((div & 0x8000) != 0) throw new InvalidDataException("SMPTE time is not a recording's");
        int p = 14;
        var list = new List<(string, List<(long, byte, byte, byte)>, List<(long, int)>)>();
        for (int ti = 0; ti < ntrk; ti++)
        {
            if (p + 8 > b.Length || b[p] != 'M' || b[p + 1] != 'T' || b[p + 2] != 'r' || b[p + 3] != 'k') throw new InvalidDataException($"track {ti}: no MTrk chunk");
            int len = b[p + 4] << 24 | b[p + 5] << 16 | b[p + 6] << 8 | b[p + 7];
            p += 8;
            int end = p + len;
            if (len < 0 || end > b.Length) throw new InvalidDataException($"track {ti}: its length runs past the end of the file");
            long t = 0;
            byte st = 0;
            string name = "";
            var ev = new List<(long, byte, byte, byte)>();
            var tempo = new List<(long, int)>();
            while (p < end)
            {
                int d = 0;
                for (int i = 0; i < 4; i++) { byte c = b[p++]; d = d << 7 | c & 0x7F; if ((c & 0x80) == 0) break; }
                t += d;
                if ((b[p] & 0x80) != 0) st = b[p++];
                if (st == 0xFF)
                {
                    byte type = b[p++];
                    int ln = 0;
                    for (int i = 0; i < 4; i++) { byte c = b[p++]; ln = ln << 7 | c & 0x7F; if ((c & 0x80) == 0) break; }
                    if (type == 3) name = System.Text.Encoding.UTF8.GetString(b, p, ln);
                    if (type == 0x51 && ln == 3) tempo.Add((t, b[p] << 16 | b[p + 1] << 8 | b[p + 2]));
                    p += ln;
                    st = 0;
                }
                else if (st is 0xF0 or 0xF7)
                {
                    int ln = 0;
                    for (int i = 0; i < 4; i++) { byte c = b[p++]; ln = ln << 7 | c & 0x7F; if ((c & 0x80) == 0) break; }
                    p += ln;
                    st = 0;
                }
                else if (st >= 0x80)
                {
                    int kind = st & 0xF0;
                    byte d1 = b[p++], d2 = kind is 0xC0 or 0xD0 ? (byte)0 : b[p++];
                    ev.Add((t, st, d1, d2));
                }
                else throw new InvalidDataException($"track {ti}: data byte with no status");
            }
            p = end;
            list.Add((name, ev, tempo));
        }
        return (list, div);
    }
}
