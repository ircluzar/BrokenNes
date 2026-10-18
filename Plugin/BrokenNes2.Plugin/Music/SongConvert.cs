// A MIDI file through VRUN's converter (Music/Vrun, copied byte for byte from the VRUN project), and the short facts that explain what the user will hear
// differently from FL Studio: which MIDI channels the four NES channels play, notes that had no channel, notes thinned because the engine reads one opcode
// per channel per frame, the loop, the bytes per channel and truncation, the tempo (only the first counts), pitch bends (not played), very low notes.
//
// Pure code, run on a worker thread (it allocates; it never runs on the audio thread). The facts come from the converter's own result and log lines (the
// same lines VRUN's build_music.py turns into build_report.json) plus a count over the MIDI events for what the converter ignores silently.
using System.Globalization;
using System.Text.RegularExpressions;
using Vrun.Audio;

namespace BrokenNes2.Music;

/// <summary>What converting one song did.</summary>
public sealed class SongFacts
{
    /// <summary>The converter's channel map line ("noise&lt;-ch9(16n), triangle&lt;-ch1(8n), square1&lt;-ch0(16n)"), MIDI channels counted from 0.</summary>
    public string ChannelMap = "";
    /// <summary>For each NES channel (square 1, square 2, triangle, noise): the MIDI channel it plays (1-16, as FL numbers them), 0 = none.</summary>
    public int[] Source = new int[4];
    public int NotesTotal, NotesDropped;
    /// <summary>The MIDI channels (1-16) whose notes no NES channel plays.</summary>
    public List<int> DroppedChannels = [];
    /// <summary>Per NES channel: opcodes the converter thinned because the channel was denser than one change per frame (volume, note-off, note-on).</summary>
    public int[] ThinnedVolume = new int[4], ThinnedOff = new int[4], ThinnedOn = new int[4];
    public int LoopFrames;
    public double LoopSeconds => LoopFrames / Midi2Nes.Fps;
    /// <summary>Bytes per channel before truncation, and whether it was truncated to <see cref="Midi2Nes.MaxChannelBytes"/>.</summary>
    public int[] Bytes = new int[4];
    public bool[] Truncated = new bool[4];
    public int[] FinalBytes = new int[4];
    public double TempoBpm;
    public int TempoChangesIgnored, PitchBends, LowNotes;
    public List<string> Log = [];

    public int Thinned(int ch) => ThinnedVolume[ch] + ThinnedOff[ch] + ThinnedOn[ch];
    public int ThinnedTotal => Thinned(0) + Thinned(1) + Thinned(2) + Thinned(3);
    public bool AnyTruncated => Truncated.Any(t => t);

    private static readonly string[] Names = ["square 1", "square 2", "triangle", "noise"];

    /// <summary>A few short lines for the window: what makes the replay differ from FL, nothing else.</summary>
    public List<string> Lines()
    {
        var lines = new List<string>();
        var used = new List<string>();
        var silent = new List<string>();
        for (int c = 0; c < 4; c++) if (Source[c] > 0) used.Add($"{Names[c]} <- MIDI {Source[c]}"); else silent.Add(Names[c]);
        lines.Add($"{used.Count} channel{(used.Count == 1 ? "" : "s")}: {string.Join(", ", used)}" + (silent.Count > 0 ? $"; {string.Join(", ", silent)} silent" : ""));
        int dropped = MusicMutate.Has(MusicMutate.FactsHideDropped) ? 0 : NotesDropped;
        lines.Add(dropped > 0
            ? $"{dropped} of {NotesTotal} notes dropped: MIDI channel{(DroppedChannels.Count == 1 ? "" : "s")} {string.Join(", ", DroppedChannels)} {(DroppedChannels.Count == 1 ? "has" : "have")} no NES channel (4 at most)"
            : $"all {NotesTotal} notes have a NES channel");
        if (ThinnedTotal > 0)
        {
            var parts = new List<string>();
            for (int c = 0; c < 4; c++) if (Thinned(c) > 0) parts.Add($"{Names[c]} {Thinned(c)}");
            lines.Add($"{ThinnedTotal} notes or changes thinned (too dense: one change per channel per frame): {string.Join(", ", parts)}");
        }
        lines.Add($"loop {LoopFrames} frames ({LoopSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s): the song starts again at the end of the MIDI");
        if (AnyTruncated)
        {
            var parts = new List<string>();
            for (int c = 0; c < 4; c++) if (Truncated[c]) parts.Add($"{Names[c]} is {Bytes[c]:N0} bytes, more than {Midi2Nes.MaxChannelBytes:N0}");
            lines.Add($"truncated: {string.Join("; ", parts)} (it goes silent before the loop)");
        }
        else lines.Add($"fits: {FinalBytes[0]} + {FinalBytes[1]} + {FinalBytes[2]} + {FinalBytes[3]} bytes (at most {Midi2Nes.MaxChannelBytes:N0} per channel)");
        var other = new List<string> { $"tempo {TempoBpm.ToString("0.#", CultureInfo.InvariantCulture)} BPM" };
        if (TempoChangesIgnored > 0) other.Add($"{TempoChangesIgnored} later tempo change{(TempoChangesIgnored == 1 ? "" : "s")} ignored");
        if (PitchBends > 0) other.Add($"{PitchBends} pitch bend{(PitchBends == 1 ? "" : "s")} not played");
        if (LowNotes > 0) other.Add($"{LowNotes} note{(LowNotes == 1 ? "" : "s")} below A1 at the lowest pitch");
        lines.Add(string.Join("; ", other));
        return lines;
    }
}

/// <summary>A song converted, ready to be put into the template.</summary>
public sealed record SongBuild(string Name, byte[][] Channels, SongFacts Facts);

public static class SongConvert
{
    /// <summary>The largest MIDI file accepted (FL's exports are a few kilobytes; a file this big is not a song).</summary>
    public const int MaxMidiBytes = 4 * 1024 * 1024;
    /// <summary>The track name the converter is given (it appears in its log lines only).</summary>
    public const string TrackName = "song";

    private static readonly Regex Map = new(@"channel map \([^)]*\): (?<map>.*?) \((?<dropped>\d+)/(?<total>\d+) notes", RegexOptions.CultureInvariant);
    private static readonly Regex Overload = new(@"channel overload \((?<ch>square1|square2|triangle|noise)\): dropped (?<v>\d+) volume / (?<o>\d+) note-off / (?<n>\d+) note-on", RegexOptions.CultureInvariant);

    /// <summary>Converts a MIDI file's bytes. Null with the reason (for the user) when it cannot be used: not a MIDI file, no notes, too big.</summary>
    public static SongBuild? Convert(byte[] midi, out string? problem)
    {
        problem = null;
        if (midi.Length > MaxMidiBytes) { problem = $"the file is {midi.Length:N0} bytes: a MIDI song is far smaller (at most {MaxMidiBytes:N0} accepted)"; return null; }
        MidiFile file;
        try { file = MidiFile.Parse(midi); }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or OverflowException)
        { problem = "it is not a MIDI file the converter can read (" + (e is FormatException ? e.Message : "it ends in the middle of an event") + ")"; return null; }

        // what the converter ignores without a word: counted here (it is the same event list it reads)
        var facts = new SongFacts();
        bool firstTempo = true;
        foreach (var track in file.Tracks)
            foreach (var ev in track)
            {
                if (ev.Status == MidiFile.Meta && ev.Data1 == MidiFile.SetTempo && ev.MetaData is { Length: 3 } b)
                {
                    int micros = (b[0] << 16) | (b[1] << 8) | b[2];
                    if (micros <= 0) continue;
                    if (firstTempo) firstTempo = false; else facts.TempoChangesIgnored++;
                }
                else if (ev.Status < 0xF0 && ev.Command == 0xE0) facts.PitchBends++;
            }
        facts.TempoBpm = 60_000_000.0 / Midi2Nes.FindTempo(file);
        if (facts.TempoChangesIgnored > 0)
        {
            // a tempo meta that only repeats the first tempo changes nothing: count only those that differ from it
            int first = Midi2Nes.FindTempo(file);
            int differ = 0; bool skipped = false;
            foreach (var track in file.Tracks)
                foreach (var ev in track)
                    if (ev.Status == MidiFile.Meta && ev.Data1 == MidiFile.SetTempo && ev.MetaData is { Length: 3 } b2)
                    {
                        int micros = (b2[0] << 16) | (b2[1] << 8) | b2[2];
                        if (micros <= 0) continue;
                        if (!skipped) { skipped = true; continue; }
                        if (micros != first) differ++;
                    }
            facts.TempoChangesIgnored = differ;
        }

        SongResult result;
        try { result = Midi2Nes.ConvertSong(file, TrackName, facts.Log.Add); }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException)
        { problem = $"the converter could not convert it ({e.Message.TrimEnd('.')})"; return null; }

        foreach (var line in facts.Log)
        {
            var m = Map.Match(line);
            if (m.Success)
            {
                facts.ChannelMap = m.Groups["map"].Value;
                facts.NotesDropped = int.Parse(m.Groups["dropped"].Value, CultureInfo.InvariantCulture);
                facts.NotesTotal = int.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture);
            }
            var o = Overload.Match(line);
            if (o.Success)
            {
                int c = Array.IndexOf(Midi2Nes.ChannelNames, o.Groups["ch"].Value);
                facts.ThinnedVolume[c] = int.Parse(o.Groups["v"].Value, CultureInfo.InvariantCulture);
                facts.ThinnedOff[c] = int.Parse(o.Groups["o"].Value, CultureInfo.InvariantCulture);
                facts.ThinnedOn[c] = int.Parse(o.Groups["n"].Value, CultureInfo.InvariantCulture);
            }
        }
        // note counts per MIDI channel (the map line holds them only for the mapped ones): the dropped channels, and notes too low to play
        var counts = new int[16];
        foreach (var track in file.Tracks)
            foreach (var ev in track)
                if (ev.Command == MidiFile.NoteOn && ev.Data2 > 0 && ev.Status < 0xF0) counts[ev.Channel]++;
        facts.NotesTotal = counts.Sum();
        if (facts.NotesTotal == 0) { problem = "it has no notes: nothing would play on the NES channels"; return null; }
        var mapped = new HashSet<int>();
        foreach (var kv in result.ChannelMap) { mapped.Add(kv.Key); if (kv.Value is >= 0 and < 4 && counts[kv.Key] > 0) facts.Source[kv.Value] = kv.Key + 1; }
        facts.NotesDropped = 0;
        for (int ch = 0; ch < 16; ch++) if (counts[ch] > 0 && !mapped.Contains(ch)) { facts.DroppedChannels.Add(ch + 1); facts.NotesDropped += counts[ch]; }
        if (facts.Source.All(s => s == 0))
        {
            // the converter's own rule: with no channel besides the drums, it falls back to its old fixed map (MIDI channels 1-4) and the drums are left out
            problem = counts[9] > 0 && counts.Count(c => c > 0) == 1
                ? "it has only drums (MIDI channel 10): with no other channel the converter keeps its old fixed map (MIDI channels 1-4) and leaves the drums out, so nothing would play. Add a melodic channel"
                : "none of its notes are on a channel the converter gives the NES: nothing would play";
            return null;
        }
        foreach (var track in file.Tracks)
            foreach (var ev in track)
                if (ev.Command == MidiFile.NoteOn && ev.Data2 > 0 && ev.Status < 0xF0 && ev.Data1 < 33 && mapped.Contains(ev.Channel)
                    && result.ChannelMap.Any(kv => kv.Key == ev.Channel && kv.Value != 3)) facts.LowNotes++;     // (the noise channel has no pitch: not counted)

        facts.LoopFrames = result.LoopFrames;
        for (int c = 0; c < 4; c++) { facts.Bytes[c] = result.UntruncatedLengths[c]; facts.Truncated[c] = result.Truncated[c]; facts.FinalBytes[c] = result.Channels[c].Length; }
        var channels = result.Channels;
        if (MusicMutate.Has(MusicMutate.ConverterOffByOne))
            channels = channels.Select(s => s.Select((b, i) => b < 0x7F && (i == 0 || s[i - 1] != 0xD0) ? (byte)(b + 1) : b).ToArray()).ToArray();
        return new SongBuild(TrackName, channels, facts);
    }

    /// <summary>A name for the song from its file name (the AudioPlayer's title rules apply when it is written into the ROM).</summary>
    public static string NameOf(string path) => Path.GetFileNameWithoutExtension(path);
}
