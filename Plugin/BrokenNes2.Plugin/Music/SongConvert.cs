// A MIDI file through VRUN's converter (Music/Vrun, copied byte for byte from the VRUN project), and the short facts that explain what the user will hear
// differently from FL Studio: which MIDI channels the four NES channels play, notes that had no channel, notes thinned because the engine reads one opcode
// per channel per frame, the loop, the bytes per channel and truncation, the tempo (only the first counts), pitch bends (round 9: played, with what was
// thinned or not played and why), very low notes.
//
// Pitch bends: every song is converted with the converter's bends ON. A song without bends converts exactly as with them off (the converter's own
// guarantee, held by midi-test), so "on whenever the file has bends" needs no switch: a file with bends plays them, a file without plays as it always did.
// VRUN's game build plays a song's bends only when its roster entry says "bends": true (engine/audio/README.md, "Pitch bends"); the facts say so.
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
    /// <summary>What the converter did with each NES channel whose MIDI channel bends (Bends.cs: played, thinned, not played and why); empty when no mapped channel bends.</summary>
    public IReadOnlyList<BendReport> Bends = [];
    public List<string> Log = [];

    public int Thinned(int ch) => ThinnedVolume[ch] + ThinnedOff[ch] + ThinnedOn[ch];
    public int ThinnedTotal => Thinned(0) + Thinned(1) + Thinned(2) + Thinned(3);
    public bool AnyTruncated => Truncated.Any(t => t);

    /// <summary>The bend reports of the channels that play their bends.</summary>
    public IEnumerable<BendReport> BendsPlayed => Bends.Where(b => !b.Ignored && !b.Dropped);
    /// <summary>NES channels that play pitch bends (bit 0 square 1 .. bit 2 triangle).</summary>
    public int BendMask => BendsPlayed.Aggregate(0, (m, b) => m | 1 << b.NesChannel);
    /// <summary>Bend opcodes written (slides, pitch steps, bent note-ons), the bytes they added, and the frames off target (a bend opcode was needed while a note held the
    /// channel's one opcode per frame: the bends the engine thinned).</summary>
    public int BendOpcodes => BendsPlayed.Sum(b => b.SlideOps + b.PitchOps + b.NoteOnsAsPitch);
    public int BendBytes => BendsPlayed.Sum(b => b.BytesAdded);
    public int BendFramesOff => MusicMutate.Has(MusicMutate.FactsHideBendLoss) ? 0 : BendsPlayed.Sum(b => b.FramesOverTolerance);

    private static readonly string[] Names = ["square 1", "square 2", "triangle", "noise"];

    /// <summary>The one line about pitch bends (null when the file has none): where they play and what that cost, what was thinned, where and why they do not play.</summary>
    public string? BendLine()
    {
        if (PitchBends == 0) return null;
        var ci = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        var played = BendsPlayed.OrderBy(b => b.NesChannel).ToList();
        if (played.Count > 0)
        {
            int slides = played.Sum(b => b.SlideOps), steps = played.Sum(b => b.PitchOps + b.NoteOnsAsPitch);
            string where = string.Join(", ", played.Select(b => Names[b.NesChannel] + (b.SlidesOff ? " (held only: no room for slides)" : b.ToleranceCents > b.RequestedToleranceCents ? $" (at {b.ToleranceCents.ToString("0.#", ci)} cents: room)" : "")));
            var quality = new List<string>();
            if (played.Any(b => b.HeldBentFrames > 0)) quality.Add("held exact");
            if (played.Any(b => b.BentFrames > b.HeldBentFrames)) quality.Add($"moving within {played.Max(b => b.MaxMovingErrorCents).ToString("0", ci)} cents");
            parts.Add($"bends played on {where}: {slides} slide{(slides == 1 ? "" : "s")} + {steps} pitch step{(steps == 1 ? "" : "s")} (+{BendBytes} bytes)" + (quality.Count > 0 ? ", " + string.Join(", ", quality) : ""));
            int off = BendFramesOff;
            if (off > 0) parts.Add($"{off} bend frame{(off == 1 ? "" : "s")} thinned (no free frame)");
        }
        foreach (var b in Bends.Where(b => b.Ignored || b.Dropped).OrderBy(b => b.NesChannel))
            parts.Add($"not played on {Names[b.NesChannel]}" + (b.Ignored ? " (no pitch)" : b.DropReason is { } why && why.Contains("truncated") ? " (truncated)" : " (no room)"));
        if (parts.Count == 0) parts.Add($"{PitchBends} pitch bend{(PitchBends == 1 ? "" : "s")}, none on a NES channel's MIDI channel (or all centred)");
        return string.Join("; ", parts);
    }

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
        string loop = $"loop {LoopFrames} frames ({LoopSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s)";
        int loopAt = lines.Count;
        lines.Add(loop + ": the song starts again at the end of the MIDI");
        if (AnyTruncated)
        {
            var parts = new List<string>();
            for (int c = 0; c < 4; c++) if (Truncated[c]) parts.Add($"{Names[c]} {Bytes[c]:N0}");
            lines.Add($"truncated (over {Midi2Nes.MaxChannelBytes:N0} bytes): {string.Join(", ", parts)}; {(parts.Count == 1 ? "it goes" : "they go")} silent before the loop");
        }
        else lines.Add($"fits: {FinalBytes[0]} + {FinalBytes[1]} + {FinalBytes[2]} + {FinalBytes[3]} bytes (at most {Midi2Nes.MaxChannelBytes:N0} per channel)");
        var other = new List<string> { $"tempo {TempoBpm.ToString("0.#", CultureInfo.InvariantCulture)} BPM" };
        if (TempoChangesIgnored > 0) other.Add($"{TempoChangesIgnored} later tempo change{(TempoChangesIgnored == 1 ? "" : "s")} ignored");
        if (LowNotes > 0) other.Add($"{LowNotes} note{(LowNotes == 1 ? "" : "s")} below A1 at the lowest pitch");
        // (the window has room for six lines: with the bends line and a thinning line, the tempo goes on the loop's line)
        if (BendLine() is { } bends && lines.Count + 2 > 6) lines[loopAt] = loop + "; " + string.Join("; ", other);
        else lines.Add(string.Join("; ", other));
        if (BendLine() is { } b) lines.Add(b);
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
        // bends on (a no-op for a song without bends); the deliberate bug BendsIgnored converts as round 7 did
        var options = new ConvertOptions { Bends = !MusicMutate.Has(MusicMutate.BendsIgnored) };
        try { result = Midi2Nes.ConvertSong(file, TrackName, facts.Log.Add, options); }
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
        facts.Bends = result.Bends;
        for (int c = 0; c < 4; c++) { facts.Bytes[c] = result.UntruncatedLengths[c]; facts.Truncated[c] = result.Truncated[c]; facts.FinalBytes[c] = result.Channels[c].Length; }
        var channels = result.Channels;
        if (MusicMutate.Has(MusicMutate.ConverterOffByOne))
            channels = channels.Select(s => MapOpcodes(s, (op, i, c) => op < 0x7F ? (byte)(op + 1) : op)).ToArray();
        if (MusicMutate.Has(MusicMutate.SlideSignFlipped))
            channels = channels.Select(FlipSlides).ToArray();
        return new SongBuild(TrackName, channels, facts);
    }

    /// <summary>A copy of a stream with <paramref name="f"/> applied to each opcode byte (never to an operand: $D0 has one, the bend opcodes $D1/$D2 two).</summary>
    private static byte[] MapOpcodes(byte[] s, Func<byte, int, byte[], byte> f)
    {
        var o = (byte[])s.Clone();
        for (int i = 0; i < o.Length; i++)
        {
            byte op = s[i];
            o[i] = f(op, i, s);
            if (op == 0xD0) i += 1;
            else if (op is Vrun.Audio.Bends.OpPitch or Vrun.Audio.Bends.OpSlide) i += 2;
            else if (op == 0xFF) break;
        }
        return o;
    }

    /// <summary>The deliberate bug SlideSignFlipped: every slide's slope negated (the ROM then bends the wrong way: the check against the MIDI's pitch must see it).</summary>
    private static byte[] FlipSlides(byte[] s)
    {
        var o = (byte[])s.Clone();
        for (int i = 0; i < o.Length; i++)
        {
            byte op = s[i];
            if (op == Vrun.Audio.Bends.OpSlide && i + 2 < o.Length)
            {
                int v = -(short)(s[i + 1] | s[i + 2] << 8);
                o[i + 1] = (byte)v; o[i + 2] = (byte)(v >> 8);
            }
            if (op == 0xD0) i += 1;
            else if (op is Vrun.Audio.Bends.OpPitch or Vrun.Audio.Bends.OpSlide) i += 2;
            else if (op == 0xFF) break;
        }
        return o;
    }

    /// <summary>A name for the song from its file name (the AudioPlayer's title rules apply when it is written into the ROM).</summary>
    public static string NameOf(string path) => Path.GetFileNameWithoutExtension(path);
}
