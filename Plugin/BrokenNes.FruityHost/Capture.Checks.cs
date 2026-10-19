// Round 9 (capture): the checks of "Capture from FL" that need no plugin DLL (`FruityHost capture-test [--only id,id] [--no-mutants]`, and the first half of `selftest --only capture`), and the
// comparisons the DLL scenarios share (SelfTest.Capture.cs).
//
//   pitch       FL's pitch above the channel's base key in one place (Record/FlNotes.cs, the `reload` agent's): C5 on channels made in FL, MIDI note 0 on imported ones (the real log's 7100 = key 71);
//               the capture reads C5 unless the notes only fit MIDI note 0, and channels that fit both follow the sure ones (said)
//   tempo       the take's tempo from what FL says (FPE_Tempo; samples per tick x PPQ; neither: 120 assumed and said), and the log's samples-per-tick value read as a float (212.30)
//   build       a scripted FL song (FlSong: slides, slide chains, per-note pitch automation, fine pitch, Coarse / Fine on the instances, overlapping notes, a note held over Stop) turned into lane
//               events exactly as the plugin writes them, built, and read back by the INDEPENDENT parser (Capture.SmfDump.py): every note (channel, key, velocity, start and end within 1 tick at
//               960 per quarter), the played pitch at every FL tick within the bend's 14-bit step (R x 100 / 16384 cents), the smallest bend range that holds every move, the channels VRUN's
//               converter expects, the end at the bar line after the last note
//   convert     the captured file through VRUN's converter (the plugin's copy) gives exactly the streams and loop of the ideal file written from the script (same notes, no bends): the converter
//               ignores the bends, and the window says how many
//   lanes       a lane's chunks: 0 bytes allocated per event, a lane whose worker is late counts what it lost (never silently), the cap
//
// Each check is shown able to fail by a deliberate bug (CaptureMutants).
using System.Diagnostics;
using System.Globalization;
using BrokenNes2.Capture;
using BrokenNes2.Music;
using BrokenNes2.Record;
using Vrun.Audio;

namespace BrokenNes.FruityHost;

/// <summary>A MIDI file as the independent parser (Capture.SmfDump.py) read it.</summary>
internal sealed class SmfDump
{
    public int Format, Tracks, Ppq;
    public readonly List<(long Tick, int Us)> Tempos = new();
    public readonly List<(long Tick, int Num, int DenPow)> TimeSigs = new();
    public readonly List<(int Track, string Name, long End)> TrackInfo = new();
    public readonly List<(int Track, int Ch, int Range, long Tick)> Rpns = new();
    public readonly List<(int Track, int Ch, int Key, int Vel, long On, long Off)> Notes = new();
    public readonly List<(int Track, int Ch, long Tick, int Value)> Bends = new();
    public string? Error;

    public static SmfDump Parse(string output)
    {
        var d = new SmfDump();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("ERROR")) { d.Error = line; continue; }
            var f = line.Split(' ');
            int I(int k) => int.Parse(f[k], CultureInfo.InvariantCulture);
            long L(int k) => long.Parse(f[k], CultureInfo.InvariantCulture);
            switch (f[0])
            {
                case "format": d.Format = I(1); d.Tracks = I(3); d.Ppq = I(5); break;
                case "tempo": d.Tempos.Add((L(1), I(2))); break;
                case "timesig": d.TimeSigs.Add((L(1), I(2), I(3))); break;
                case "track": d.TrackInfo.Add((I(1), string.Join(' ', f.Skip(3).TakeWhile(x => x != "end")), L(f.Length - 1))); break;
                case "rpn": d.Rpns.Add((I(1), I(2), I(3), L(4))); break;
                case "note": d.Notes.Add((I(1), I(2), I(3), I(4), L(5), L(6))); break;
                case "bend": d.Bends.Add((I(1), I(2), L(3), I(4))); break;
            }
        }
        return d;
    }
}

internal static class CaptureChecks
{
    public sealed record Check(string Id, string What, Action<ImodelReport> Run);
    public sealed record CheckOutcome(string Id, string What, bool Passed, double Seconds, ImodelReport Report);

    public static readonly Check[] All =
    [
        new("pitch", "FL's pitch above the channel's base key (one shared conversion, Record/FlNotes.cs): C5 on FL's own channels, MIDI note 0 detected on imported ones and said", PitchCheck),
        new("tempo", "the take's tempo from what FL says (FPE_Tempo, samples per tick x PPQ, or 120 said), the log's samples-per-tick bits read as a float", TempoCheck),
        new("build", "a scripted FL song's lane events built and read back by the independent parser: every note within a tick, the played pitch at every FL tick within the bend's step, ranges, channels, the end", BuildCheck),
        new("convert", "the captured file through VRUN's converter = the ideal file written from the script (streams and loop byte for byte); the bends are counted as not played", ConvertCheck),
        new("lanes", "a lane allocates nothing per event, counts what it loses when its worker is late, stops at its cap", LanesCheck),
    ];

    public sealed record Mutant(string Name, int Code, string CatchingCheck);

    public static readonly Mutant[] Mutants =
    [
        new("every channel read from MIDI note 0 (FL's own channels 60 keys low)", CaptureMutants.BaseFromMidi0, "build"),
        new("C5 always, even where the notes only fit MIDI note 0 (imported channels 60 keys high)", CaptureMutants.BaseDetectionIgnored, "pitch"),
        new("velocity = FL's value x 127", CaptureMutants.VelocityOver127, "build"),
        new("no RPN 0 written (the bends read at 2 semitones)", CaptureMutants.BendRangeUnwritten, "build"),
        new("every bend the other way", CaptureMutants.BendSignFlipped, "build"),
        new("Pulse 2 on MIDI channel 1", CaptureMutants.Pulse2OnChannel1, "build"),
        new("notes still sounding at Stop left out", CaptureMutants.OpenNotesLost, "build"),
        new("the song ends at its last note-off, not at the bar line", CaptureMutants.EndAtLastNote, "build"),
        new("FL's tempo ignored (120 BPM)", CaptureMutants.TempoIgnored, "tempo"),
        new("every 40th pitch event lost without a word", CaptureMutants.EventsLostSilently, "build"),
        new("pitch changes after a note-on not captured", CaptureMutants.SlideIgnored, "build"),
        new("an allocation per captured event", CaptureMutants.CaptureAllocates, "lanes"),
        new("the clock counts at 44,100 Hz whatever the rate", CaptureMutants.ClockFixedRate, "lanes"),
    ];

    // =====================================================================================================================================================================
    // the scripted song

    /// <summary>The song every capture check plays: four BrokenNes2 channels (Pulse 1, Pulse 2 with Coarse +12, Triangle with Fine +7, Noise), 141.3 BPM (a tempo like the user's), PPQ 96, 4/4: plain
    /// notes, fine pitch +-30, a slide up, a slide down an octave, a slide chain, a 26-semitone slide, vibrato by per-note automation, two overlapping notes, a same-key retrigger, a note held over Stop.</summary>
    public static FlSong Song(double bpm = 141.3, int root = 60)
    {
        var s = new FlSong { Name = "capture-song", Bpm = bpm, StopTick = 1600, Root = [root, root, root, root] };
        s.Coarse[1] = 12; s.Fine[2] = 7;
        void N(int inst, int key, int start, int len, int vel = 100, string label = "", Action<FlNote>? more = null)
        {
            var n = new FlNote { Inst = inst, Key = key, Start = start, Length = len, Velocity = vel, Label = label };
            more?.Invoke(n);
            s.Notes.Add(n);
        }
        // Pulse 1: the lead
        N(0, 72, 0, 90, 100, "plain");
        N(0, 74, 96, 90, 110, "fine +30", n => n.Fine = 30);
        N(0, 76, 192, 180, 90, "slide up 3", n => n.Slides = [new FlSlide { StartTick = 48, LengthTicks = 96, ToKey = 79 }]);
        N(0, 79, 384, 186, 120, "vibrato", n => n.Bend = [(0, 0), (24, 40), (48, -40), (72, 40), (96, -40), (120, 0)]);
        N(0, 77, 576, 90, 100, "fine -30", n => n.Fine = -30);
        N(0, 76, 672, 100, 100, "overlapped by the next");
        N(0, 74, 764, 100, 105, "slide down an octave", n => n.Slides = [new FlSlide { StartTick = 20, LengthTicks = 60, ToKey = 62 }]);
        N(0, 72, 960, 40, 100, "same key");
        N(0, 72, 1000, 40, 64, "same key again");
        N(0, 72, 1056, 60, 81, "slide chain", n => n.Slides = [new FlSlide { StartTick = 10, LengthTicks = 20, ToKey = 74 }, new FlSlide { StartTick = 30, LengthTicks = 20, ToKey = 70 }, new FlSlide { StartTick = 50, LengthTicks = 10, ToKey = 72 }]);
        N(0, 60, 1152, 300, 80, "26-semitone slide", n => n.Slides = [new FlSlide { StartTick = 50, LengthTicks = 200, ToKey = 86 }]);
        N(0, 67, 1500, 400, 100, "held over Stop");
        // Pulse 2 (Coarse +12): the harmony
        N(1, 57, 0, 180, 70);
        N(1, 59, 192, 180, 70, "slide down", n => n.Slides = [new FlSlide { StartTick = 100, LengthTicks = 60, ToKey = 55 }]);
        N(1, 60, 384, 360, 75);
        N(1, 62, 768, 360, 75);
        N(1, 64, 1152, 300, 80);
        // Triangle (Fine +7): the bass
        N(2, 48, 0, 190);
        N(2, 43, 192, 190);
        N(2, 45, 384, 190, 100, "slide up 2", n => n.Slides = [new FlSlide { StartTick = 100, LengthTicks = 50, ToKey = 47 }]);
        N(2, 41, 576, 190);
        N(2, 48, 768, 380);
        N(2, 43, 1152, 380);
        // Noise: hits
        for (int i = 0; i < 16; i++) N(3, i % 2 == 0 ? 36 : 42, i * 96, 24, i % 2 == 0 ? 127 : 64);
        return s;
    }

    /// <summary>The MIDI channel (0-15) each rack instance's track should be on: what VRUN's converter turns into its NES channel.</summary>
    public static readonly int[] ExpectedChannel = [0, 1, 2, 9];

    /// <summary>A note's expected end in FL ticks (FL stopped before its end: the Stop).</summary>
    public static int EndTick(FlSong s, FlNote n) => Math.Min(n.End, s.StopTick);

    // =====================================================================================================================================================================
    // the lane events the plugin writes for a song (the "ideal plugin": what Bn2Plugin.Capture.cs does, from the same tick loop as FlRun)

    public static List<LaneData> IdealLanes(FlSong s, int rate, out FlTruth truth)
    {
        truth = new FlTruth();
        double spt = rate * 60.0 / (s.Bpm * s.Ppq);
        var lanes = Enumerable.Range(0, 4).Select(i => new CaptureLane(new LaneIdentity(i + 1, (CapRole)i, LaneIdentity.RoleName((CapRole)i), "test", 1))).ToArray();
        foreach (var l in lanes) l.Start();
        var active = new List<(FlNote N, int Voice, float Last)>();
        int voice = 1;
        long sample = 0;
        float[] tuningSent = [float.NaN, float.NaN, float.NaN, float.NaN];
        foreach (var l in lanes) l.Feed();
        for (int k = 0; k < s.StopTick; k++)
        {
            for (int i = active.Count - 1; i >= 0; i--)
                if (active[i].N.End <= k) { lanes[active[i].N.Inst].Add(CapEv.Off, active[i].Voice, 0); truth.Samples[active[i].N] = (truth.Samples[active[i].N].Start, sample); active.RemoveAt(i); }
            foreach (var n in s.Notes.Where(n => n.Start == k))
            {
                float tuning = s.Coarse[n.Inst] * 100 + s.Fine[n.Inst];
                var lane = lanes[n.Inst];
                if (tuning != tuningSent[n.Inst]) { lane.Add(CapEv.Tuning, 0, tuning); tuningSent[n.Inst] = tuning; }
                float final = (float)s.FlFinal(n, k);
                lane.Add(CapEv.On, voice, final, final, n.Velocity / 128f);                                       // (InitLevels = FinalLevels at the note-on, both above the channel's root)
                active.Add((n, voice++, final));
                truth.Samples[n] = (sample, -1);
            }
            for (int i = 0; i < active.Count; i++)
            {
                var (n, v, last) = active[i];
                float f = (float)s.FlFinal(n, k);
                if (f != last) { lanes[n.Inst].Add(CapEv.Pitch, v, f, s.Coarse[n.Inst] * 100 + s.Fine[n.Inst]); active[i] = (n, v, f); }
            }
            for (int inst = 0; inst < 4; inst++)
            {
                FlNote? owner = null; float pitch = 0;
                foreach (var (n, _, last) in active) if (n.Inst == inst && (owner == null || n.Start >= owner.Start)) { owner = n; pitch = last; }
                if (owner != null) truth.Owners.Add((k, inst, owner, pitch + (s.Root[inst] - 60) * 100.0 + s.Coarse[inst] * 100 + s.Fine[inst]));
            }
            int len = (int)(Math.Floor((k + 1) * spt) - Math.Floor(k * spt));
            foreach (var l in lanes) l.Advance(len, rate);
            sample += len;
            if (k % 256 == 0) foreach (var l in lanes) l.Feed();
        }
        foreach (var (n, _, _) in active) truth.Samples[n] = (truth.Samples[n].Start, sample);
        foreach (var l in lanes) l.Stop();
        truth.TotalSamples = sample;
        return lanes.Select(l => l.Snapshot()).ToList();
    }

    /// <summary>What FL said about time for a song played at <paramref name="rate"/> (FPE_Tempo when <paramref name="withTempo"/>).</summary>
    public static FlTiming Timing(FlSong s, int rate, bool withTempo, int processMode = 3072) =>
        new((float)(rate * 60.0 / (s.Bpm * s.Ppq)), 0, s.StepsPerBar, s.StepsPerBeat, s.Ppq, true, withTempo ? (float)s.Bpm : double.NaN, 0, 0, processMode, rate);

    // =====================================================================================================================================================================
    // the independent parser and the comparisons (also used by the DLL scenarios)

    public static string? Python()
    {
        foreach (var cand in new[] { "python", "py" })
        {
            try { var t = MidiFixtures.Run(cand, ["-I", "-c", "import sys; print(sys.version_info[0])"], timeoutSec: 60); if (t.Exit == 0 && t.Output.Trim().StartsWith('3')) return cand; }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }

    /// <summary>The file read by Capture.SmfDump.py (null with a warning when there is no Python: NOT RUN, never passed).</summary>
    public static SmfDump? Dump(ImodelReport r, byte[] midi, string name)
    {
        string? py = Python();
        if (py == null) { r.Warn("NOT RUN: no Python 3 on this machine: the file was not read by the independent parser"); return null; }
        string dir = Path.Combine(Path.GetTempPath(), $"bn2_capture_{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, name + ".mid");
        File.WriteAllBytes(file, midi);
        string script = Path.Combine(MidiFixtures.RepoRoot(), @"Plugin\BrokenNes.FruityHost\Capture.SmfDump.py");
        var p = MidiFixtures.Run(py, ["-I", script, file], timeoutSec: 60);
        var d = SmfDump.Parse(p.Output);
        r.Expect(p.Exit == 0 && d.Error == null, $"the independent parser refused the file (exit {p.Exit}): {d.Error ?? p.Output.Trim()}");
        return p.Exit == 0 ? d : null;
    }

    public sealed class Compared
    {
        public int Notes, NotesFound, MaxTickError, PitchPoints, PitchOk;
        public double MaxPitchError, AllowedPitchError;
        public int[] Range = new int[4];
    }

    /// <summary>The notes and the played pitch of a song against a capture's file as the independent parser read it. <paramref name="truth"/> is what FL delivered (owners per tick).</summary>
    public static Compared Compare(ImodelReport r, SmfDump d, FlSong s, FlTruth truth, string label)
    {
        var c = new Compared();
        int ratio = d.Ppq / s.Ppq;
        r.Expect(d.Format == 1 && d.Ppq == 960 && d.Tracks == 5, $"{label}: format {d.Format}, PPQ {d.Ppq}, {d.Tracks} tracks (1, 960 and a conductor + 4 expected)");
        r.Expect(d.Tempos.Count == 1 && d.Tempos[0].Tick == 0, $"{label}: {d.Tempos.Count} tempo events (one, at tick 0, expected)");
        if (d.Tempos.Count > 0)
        {
            double bpm = 60e6 / d.Tempos[0].Us;
            r.Expect(Math.Abs(bpm / s.Bpm - 1) < 2e-5, $"{label}: tempo {bpm:0.####} BPM written, FL's is {s.Bpm} BPM");
        }
        r.Expect(d.TimeSigs.Count == 1 && d.TimeSigs[0].Num == s.StepsPerBar / s.StepsPerBeat && d.TimeSigs[0].DenPow == 2, $"{label}: time signature {string.Join(",", d.TimeSigs.Select(x => $"{x.Num}/{1 << x.DenPow}"))}, FL's is {s.StepsPerBar / s.StepsPerBeat}/4");
        // the tracks: names and channels
        string[] names = ["Pulse 1", "Pulse 2", "Triangle", "Noise"];
        var trackOf = new int[4];
        for (int inst = 0; inst < 4; inst++)
        {
            int ti = d.TrackInfo.FindIndex(t => t.Name == names[inst]);
            trackOf[inst] = ti < 0 ? -1 : d.TrackInfo[ti].Track;
            r.Expect(ti >= 0, $"{label}: no track named \"{names[inst]}\" (tracks: {string.Join(", ", d.TrackInfo.Select(t => t.Name))})");
            if (ti < 0) continue;
            var chans = d.Notes.Where(n => n.Track == trackOf[inst]).Select(n => n.Ch).Distinct().ToList();
            r.Expect(chans.Count == 1 && chans[0] == ExpectedChannel[inst] + 1, $"{label}: {names[inst]} is on MIDI channel {string.Join(",", chans)}, VRUN's converter expects {ExpectedChannel[inst] + 1}");
        }
        // every track ends at the bar line after the last note
        long lastOff = (long)s.Notes.Max(n => EndTick(s, n)) * ratio, bar = (long)(s.StepsPerBar / s.StepsPerBeat) * d.Ppq, end = (lastOff + bar - 1) / bar * bar;
        r.Expect(d.TrackInfo.All(t => t.End == end), $"{label}: the tracks end at {string.Join(",", d.TrackInfo.Select(t => t.End).Distinct())}, the bar line after the last note is {end} (VRUN loops there)");
        // the notes
        var unused = d.Notes.ToList();
        foreach (var n in s.Notes)
        {
            c.Notes++;
            int ch = ExpectedChannel[n.Inst] + 1, key = n.Key + s.Coarse[n.Inst] + (int)Math.Round(s.Fine[n.Inst] / 100.0);
            long on = (long)n.Start * ratio, off = (long)EndTick(s, n) * ratio;
            int k = unused.FindIndex(x => x.Track == trackOf[n.Inst] && x.Ch == ch && x.Key == key && Math.Abs(x.On - on) <= 1);
            if (k < 0) { r.Expect(false, $"{label}: {names[n.Inst]} note key {key} at tick {on} ({n.Label}) is not in the file"); continue; }
            var f = unused[k];
            unused.RemoveAt(k);
            c.NotesFound++;
            c.MaxTickError = (int)Math.Max(c.MaxTickError, Math.Max(Math.Abs(f.On - on), Math.Abs(f.Off - off)));
            r.Expect(Math.Abs(f.Off - off) <= 1, $"{label}: {names[n.Inst]} note key {key} at {on} ({n.Label}) ends at tick {f.Off}, FL ended it at {off}");
            r.Expect(f.Vel == n.Velocity, $"{label}: {names[n.Inst]} note key {key} at {on} has velocity {f.Vel}, FL's is {n.Velocity} ({n.Velocity}/128)");
        }
        r.Expect(unused.Count == 0, $"{label}: {unused.Count} notes in the file that FL did not play: {string.Join("; ", unused.Take(4).Select(x => $"track {x.Track} key {x.Key} at {x.On}"))}");
        // the pitch at every FL tick: the newest sounding note's key + the bend at that tick
        for (int inst = 0; inst < 4; inst++)
        {
            int ti = trackOf[inst];
            if (ti < 0) continue;
            var rp = d.Rpns.Where(x => x.Track == ti).ToList();
            int range = rp.Count > 0 ? rp[^1].Range : 2;
            c.Range[inst] = range;
            r.Expect(rp.Count == 1 && rp[0].Tick == 0, $"{label}: {names[inst]} has {rp.Count} pitch-bend ranges (RPN 0) (one at tick 0 expected)");
            var devs = truth.Owners.Where(o => o.Inst == inst).Select(o => o.Played - KeyCents(o.Note, s)).DefaultIfEmpty(0).ToList();
            double up = Math.Max(0, devs.Max()), down = Math.Max(0, -devs.Min());
            int need = 2;
            while (need < 48 && (up > need * 100.0 * 8191 / 8192 || down > need * 100.0)) need++;
            r.Expect(range == need, $"{label}: {names[inst]}'s bend range is {range} semitones; its moves (+{up:0.#} / -{down:0.#} cents) need {need}, the smallest whose 14 bits reach them");
            var notes = d.Notes.Where(x => x.Track == ti).ToList();
            var bends = d.Bends.Where(x => x.Track == ti).OrderBy(x => x.Tick).ToList();
            double allowed = range * 100.0 / 16384 + 1e-3;
            c.AllowedPitchError = Math.Max(c.AllowedPitchError, allowed);
            int bad = 0;
            foreach (var o in truth.Owners.Where(o => o.Inst == inst))
            {
                long t = (long)o.Tick * ratio;
                var sounding = notes.Where(x => x.On <= t && x.Off > t).OrderBy(x => x.On).LastOrDefault();
                c.PitchPoints++;
                if (sounding.Key == 0 && sounding.Off == 0) { if (bad++ < 3) r.Expect(false, $"{label}: {names[inst]} at FL tick {o.Tick}: no note sounds in the file, FL played {o.Note.Label}"); continue; }
                int bend = 0;
                foreach (var b in bends) { if (b.Tick > t) break; bend = b.Value; }
                double filePitch = (sounding.Key - 60) * 100.0 + bend * range * 100.0 / 8192;
                double err = Math.Abs(filePitch - o.Played);
                c.MaxPitchError = Math.Max(c.MaxPitchError, err);
                if (err <= allowed) c.PitchOk++;
                else if (bad++ < 3) r.Expect(false, $"{label}: {names[inst]} at FL tick {o.Tick} ({o.Note.Label}): the file plays {filePitch:0.###} cents from C5, FL played {o.Played:0.###} (error {err:0.###}, allowed {allowed:0.###})");
            }
            r.Expect(bad == 0, $"{label}: {names[inst]}: {bad} FL ticks where the file's pitch is not what FL played");
        }
        return c;
    }

    /// <summary>The key a note is written at (its piano-roll key + the instance's whole semitones), in cents from C5.</summary>
    private static double KeyCents(FlNote n, FlSong s) => (n.Key + s.Coarse[n.Inst] + Math.Round(s.Fine[n.Inst] / 100.0) - 60) * 100.0;

    /// <summary>The ideal file: the script's notes at their FL ticks x 10 on the expected channels, no bends, the take's tempo (microseconds per quarter as the capture wrote it), the same end.</summary>
    public static byte[] IdealMidi(FlSong s, int microsPerQuarter)
    {
        int ratio = BrokenNes2.Record.RecMidi.Ppq / s.Ppq;
        var tracks = new List<BrokenNes2.Record.SmfTrack>();
        string[] names = ["Pulse 1", "Pulse 2", "Triangle", "Noise"];
        for (int inst = 0; inst < 4; inst++)
        {
            var t = new BrokenNes2.Record.SmfTrack { Name = names[inst] };
            foreach (var n in s.Notes.Where(n => n.Inst == inst))
            {
                int key = n.Key + s.Coarse[inst] + (int)Math.Round(s.Fine[inst] / 100.0);
                t.Events.Add(BrokenNes2.Record.RecMidi.On((long)n.Start * ratio, ExpectedChannel[inst], key, n.Velocity));
                t.Events.Add(BrokenNes2.Record.RecMidi.Off((long)EndTick(s, n) * ratio, ExpectedChannel[inst], key));
            }
            tracks.Add(t);
        }
        long lastOff = (long)s.Notes.Max(n => EndTick(s, n)) * ratio, bar = (long)(s.StepsPerBar / s.StepsPerBeat) * BrokenNes2.Record.RecMidi.Ppq, end = (lastOff + bar - 1) / bar * bar;
        var bytes = BrokenNes2.Record.RecMidi.Write([(0, microsPerQuarter)], "ideal", [], tracks);
        // RecMidi ends each track at its last event: put a marker at the song's end in the conductor so the converter's loop is the same length (its span is the longest track's)
        return WithEnd(bytes, end);
    }

    /// <summary>Appends a marker meta event at <paramref name="end"/> to the first track (the conductor), so the file's span reaches it.</summary>
    private static byte[] WithEnd(byte[] smf, long end)
    {
        var file = MidiFile.Parse(smf);
        long last = file.Tracks[0].Sum(e => (long)e.DeltaTime);
        int p = 14;
        int len = (smf[p + 4] << 24) | (smf[p + 5] << 16) | (smf[p + 6] << 8) | smf[p + 7];
        int bodyStart = p + 8, bodyEnd = bodyStart + len;
        // the body ends in 00 FF 2F 00 (end of track at delta 0): insert "delta FF 06 00" (an empty marker) before it
        var delta = BrokenNes2.Record.RecMidi.Vlq(end - last);
        var insert = delta.Concat(new byte[] { 0xFF, 0x06, 0x00 }).ToArray();
        int at = bodyEnd - 4;
        var o = new List<byte>(smf.Take(at)); o.AddRange(insert); o.AddRange(smf.Skip(at));
        int newLen = len + insert.Length;
        o[p + 4] = (byte)(newLen >> 24); o[p + 5] = (byte)(newLen >> 16); o[p + 6] = (byte)(newLen >> 8); o[p + 7] = (byte)newLen;
        return o.ToArray();
    }

    // =====================================================================================================================================================================
    // the checks

    public static void PitchCheck(ImodelReport r)
    {
        // the shared conversion (Record/FlNotes.cs): the real log's 7100 on an imported channel and 1100 on a channel made in FL are both key 71, 1100 cents from C5
        r.Expect(FlNotes.Key(7100, FlNotes.BaseImported) == 71 && FlNotes.Key(1100, FlNotes.BaseC5) == 71 && FlNotes.ToC5Cents(7100, FlNotes.BaseImported) == 1100 && FlNotes.ToC5Cents(1100, FlNotes.BaseC5) == 1100,
            "FlNotes: 7100 from MIDI note 0 and 1100 from C5 are key 71, 1100 cents from C5");
        // which base the capture reads each channel from: FL's own channels (C5), imported ones (MIDI note 0), a mix
        foreach (var (label, roots, expect) in new (string, int[], (int Base, BaseWhy How)[])[]
        {
            ("channels made in FL", [60, 60, 60, 60], [(60, BaseWhy.Default), (60, BaseWhy.Default), (60, BaseWhy.OnlyC5), (60, BaseWhy.OnlyC5)]),    // (the bass and the drums sit below C5: from MIDI note 0 they would be below key 0)
            ("imported channels", [0, 0, 0, 0], [(0, BaseWhy.OnlyMidi0), (0, BaseWhy.OnlyMidi0), (0, BaseWhy.LikeOthers), (0, BaseWhy.LikeOthers)]),
            ("imported pulses, a triangle made in FL", [0, 0, 60, 0], [(0, BaseWhy.OnlyMidi0), (0, BaseWhy.OnlyMidi0), (60, BaseWhy.OnlyC5), (60, BaseWhy.Default)]),
        })
        {
            var s = Song(root: 60);
            s.Root = roots;
            var res = CaptureSong.Build("bases", IdealLanes(s, 48000, out _), Timing(s, 48000, true), Timing(s, 48000, true));
            if (res.Problem != null) { r.Expect(false, $"{label}: {res.Problem}"); continue; }
            for (int i = 0; i < 4; i++)
            {
                var t = res.Tracks.First(x => x.Id.Role == (CapRole)i);
                r.Expect(t.Base == expect[i].Base && t.BaseHow == expect[i].How, $"{label}: {t.Name} read from {t.Base} ({t.BaseHow}), expected {expect[i].Base} ({expect[i].How})");
            }
            string facts = string.Join(" | ", res.Facts);
            if (roots.All(x => x == 60)) r.Expect(facts.Contains("keys read from C5") && !facts.Contains("only fit"), $"{label}: the base is not said as C5: {facts}");
            else r.Expect(facts.Contains("read from MIDI note 0: their notes only fit there") && res.Facts.IndexOf(res.Facts.First(f => f.Contains("only fit"))) <= 3, $"{label}: the detected base is not said among the first facts: {facts}");
            if (label.StartsWith("imported pulses")) r.Expect(facts.Contains("Noise read from C5") && facts.Contains("60 too high"), $"{label}: the noise channel that fits both is not said: {facts}");
            if (roots.All(x => x == 0)) r.Expect(facts.Contains("Triangle, Noise too, like the other channels"), $"{label}: the channels read like the others are not said: {facts}");
            r.Note($"{label}: bases {string.Join("/", res.Tracks.Select(t => t.Base))}");
        }
    }

    public static void TempoCheck(ImodelReport r)
    {
        // the log's value: 1129579756 = 0x435404EC is a float: 212.0192 samples per tick = 44,100 x 60 / (130 BPM x PPQ 96), FL's defaults at the user's 44.1 kHz
        float logSpt = BitConverter.Int32BitsToSingle(1129579756);
        r.Expect(1129579756 == 0x435404EC && Math.Abs(logSpt - 44100 * 60.0 / (130 * 96)) < 1e-4, $"the log's FPD_SetSamplesPerTick value 1129579756 read as a float is {logSpt}, expected {44100 * 60.0 / (130 * 96):0.####} (44.1 kHz, 130 BPM, PPQ 96)");
        r.Note($"the real log's samples per tick: 1129579756 = 0x{1129579756:X8} = {logSpt:0.####} as a float = {44100 * 60.0 / (logSpt * 96):0.###} BPM at 44.1 kHz with PPQ 96 ({48000 * 60.0 / (logSpt * 96):0.##} at 48 kHz)");
        var s = Song();
        var t = FlClock.Resolve(Timing(s, 48000, withTempo: true));
        r.Expect(Math.Abs(t.Bpm - s.Bpm) < 1e-4 && !t.Guessed && t.Check != null && t.Check.Contains("agree"), $"FPE_Tempo: {t.Bpm} ({t.Source}; {t.Check})");
        var u = FlClock.Resolve(Timing(s, 48000, withTempo: false));
        r.Expect(Math.Abs(u.Bpm / s.Bpm - 1) < 2e-7 && !u.Guessed, $"samples per tick x PPQ: {u.Bpm} for {s.Bpm} ({u.Source})");
        var v = FlClock.Resolve(new FlTiming(0, 0, 0, 0, 0, false, double.NaN, 0, 0, -1, 48000));
        r.Expect(v.Bpm == 120 && v.Guessed && v.Source.Contains("ASSUMED"), $"nothing from FL: {v.Bpm} ({v.Source})");
        var w = FlClock.Resolve(new FlTiming(logSpt, 0, 0, 0, 0, false, double.NaN, 0, 0, -1, 48000));
        r.Expect(w.Guessed && w.Source.Contains("ASSUMING PPQ 96"), $"samples per tick without a readable time signature: said as a guess ({w.Source})");
        // the SDK's TTimeSigInfo read: sane values only
        FlClock.Reset();
        FlClock.TimeSig(16, 4, 96);
        var now = FlClock.Now(48000);
        r.Expect(now.TimeSigKnown && now.Ppq == 96 && now.BeatsPerBar == 4, "FL's time signature 16 steps / 4 per beat / PPQ 96 read");
        FlClock.Reset();
        FlClock.TimeSig(0x7FFF_FFFF, -3, 0);
        r.Expect(!FlClock.Now(48000).TimeSigKnown, "an absurd time signature is not taken");
        FlClock.Reset();
        // a take's tempo: the file at FL's tempo (the tempo mutant writes 120)
        var lanes = IdealLanes(s, 48000, out _);
        var res = CaptureSong.Build("tempo", lanes, Timing(s, 48000, true), Timing(s, 48000, true));
        r.Expect(Math.Abs(60e6 / res.MicrosPerQuarter / s.Bpm - 1) < 2e-5, $"the take is written at {60e6 / res.MicrosPerQuarter:0.###} BPM, FL's is {s.Bpm}");
    }

    public static void BuildCheck(ImodelReport r)
    {
        // 141.3 BPM at 48 kHz (a tempo whose samples per tick are far from whole), the user's own FL: 130 BPM at 44.1 kHz (the real log's samples per tick), on channels made in FL (root C5) and on
        // channels FL made by importing a MIDI file (root MIDI note 0: the capture must find it)
        foreach (var (rate, bpm, root) in new[] { (48000, 141.3, 60), (44100, 130.0, 60), (44100, 130.0, 0) })
            foreach (bool tempo in new[] { true, false })
            {
                var s = Song(bpm, root);
                string label = $"{rate} Hz {bpm} BPM, {(tempo ? "FPE_Tempo" : "no FPE_Tempo")}, {(root == 60 ? "channels made in FL" : "imported channels")}";
                var lanes = IdealLanes(s, rate, out var truth);
                var res = CaptureSong.Build("build", lanes, Timing(s, rate, tempo), Timing(s, rate, tempo));
                r.Expect(res.Problem == null, $"{label}: {res.Problem}");
                if (res.Problem != null) continue;
                r.Expect(res.LostEvents == 0, $"{label}: {res.LostEvents} events lost");
                var d = Dump(r, res.Midi, $"build_{rate}_{tempo}");
                if (d == null) continue;
                var c = Compare(r, d, s, truth, label);
                r.Expect(res.Facts.Any(f => f.Contains("still sounding when FL stopped")), $"{label}: the note held over Stop is not said");
                r.Expect(res.Facts.Any(f => f.Contains("no pitch bend") && f.Contains("their keys")), $"{label}: the facts do not say VRUN's engine drops the bends: {string.Join(" | ", res.Facts)}");
                if (tempo)
                    r.Note($"{label}: {c.NotesFound}/{c.Notes} notes, start and end within {c.MaxTickError} tick(s) at 960 per quarter; pitch at {c.PitchOk}/{c.PitchPoints} FL ticks within {c.AllowedPitchError:0.###} cents (worst {c.MaxPitchError:0.####}); ranges {string.Join("/", c.Range)} semitones; {res.BendEvents} bends");
            }
    }

    public static void ConvertCheck(ImodelReport r)
    {
        var s = Song();
        var lanes = IdealLanes(s, 48000, out _);
        var res = CaptureSong.Build("convert", lanes, Timing(s, 48000, true), Timing(s, 48000, true));
        if (res.Problem != null) { r.Expect(false, res.Problem); return; }
        var cap = SongConvert.Convert(res.Midi, out var why1);
        var ideal = SongConvert.Convert(IdealMidi(s, res.MicrosPerQuarter), out var why2);
        r.Expect(cap != null && ideal != null, $"the converter refused: {why1} / {why2}");
        if (cap == null || ideal == null) return;
        for (int ch = 0; ch < 4; ch++)
            r.Expect(cap.Channels[ch].AsSpan().SequenceEqual(ideal.Channels[ch]), $"{Midi2Nes.ChannelNames[ch]}: the captured file's stream ({cap.Channels[ch].Length} bytes) differs from the ideal file's ({ideal.Channels[ch].Length})");
        r.Expect(cap.Facts.LoopFrames == ideal.Facts.LoopFrames, $"loop {cap.Facts.LoopFrames} frames, the ideal file's {ideal.Facts.LoopFrames}");
        r.Expect(cap.Facts.Source.SequenceEqual(new[] { 1, 2, 3, 10 }), $"the converter's channels: square 1 <- {cap.Facts.Source[0]}, square 2 <- {cap.Facts.Source[1]}, triangle <- {cap.Facts.Source[2]}, noise <- {cap.Facts.Source[3]} (1, 2, 3, 10 expected)");
        r.Expect(cap.Facts.PitchBends == res.BendEvents && res.BendEvents > 0, $"the converter's facts count {cap.Facts.PitchBends} pitch bends, the capture wrote {res.BendEvents}");
        r.Expect(res.ConverterTrack.SequenceEqual(new[] { 0, 1, 2, 3 }), $"the capture's view of the converter's choice: {string.Join(",", res.ConverterTrack)}");
        r.Note($"streams {string.Join(" + ", cap.Channels.Select(c => c.Length))} bytes, loop {cap.Facts.LoopFrames} frames, {cap.Facts.PitchBends} bends the engine does not play: identical to the ideal file's streams");
        // the converter gives the triangle to the lowest channel: a song whose Pulse 2 plays lower than its triangle is said
        var low = Song();
        low.Coarse[1] = -36;
        var lr = CaptureSong.Build("low", IdealLanes(low, 48000, out _), Timing(low, 48000, true), Timing(low, 48000, true));
        r.Expect(lr.Facts.Any(f => f.Contains("chooses by itself") && f.Contains("triangle plays Pulse 2")), $"a Pulse 2 lower than the triangle: the converter's choice is not said: {string.Join(" | ", lr.Facts)}");
    }

    public static void LanesCheck(ImodelReport r)
    {
        var lane = new CaptureLane(new LaneIdentity(1, CapRole.Pulse1, "Pulse 1", "test", 1));
        lane.Start();
        lane.Feed();
        for (int i = 0; i < 1000; i++) { lane.Add(CapEv.Pitch, 1, i); lane.Advance(64, 48000); }    // (the first calls: JIT)
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20000; i++) { lane.Add(CapEv.Pitch, 1, i); lane.Advance(64, 48000); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        r.Expect(bytes < 4096, $"{bytes} bytes allocated for 20,000 events (0 expected; a real allocation per event would be 20,000 x 24 bytes or more)");
        r.Note($"{bytes} bytes allocated on the writing thread for 20,000 events");
        // a worker that never comes: the lane fills its first chunks, then counts every event it cannot keep
        var starved = new CaptureLane(new LaneIdentity(2, CapRole.Pulse2, "Pulse 2", "test", 1));
        starved.Start();
        int sent = CaptureLane.FirstChunks * CaptureLane.ChunkEvents + 5000;
        for (int i = 0; i < sent; i++) starved.Add(CapEv.Pitch, 1, i);
        r.Expect(starved.Count == CaptureLane.FirstChunks * CaptureLane.ChunkEvents && starved.Lost == 5000, $"a starved lane kept {starved.Count} and counted {starved.Lost} lost of {sent} (expected {CaptureLane.FirstChunks * CaptureLane.ChunkEvents} and 5,000)");
        var d = starved.Snapshot();
        r.Expect(d.Lost == 5000 && d.Count == starved.Count, "the snapshot carries the loss");
        // the clock: seconds = samples / rate at each block's rate (an export at another rate)
        var clock = new CaptureLane(new LaneIdentity(3, CapRole.Noise, "Noise", "test", 1));
        clock.Start();
        clock.Advance(48000, 48000); clock.Advance(44100, 44100); clock.Advance(96000, 96000);
        r.Expect(Math.Abs(clock.Seconds - 3) < 1e-12, $"three blocks of one second at 48, 44.1 and 96 kHz: the clock says {clock.Seconds:0.######} s");
        // events before FL's Play are not kept; after Stop neither
        var gate = new CaptureLane(new LaneIdentity(4, CapRole.Other, "x", "test", 1));
        gate.Add(CapEv.On, 1, 6000, 0, 0.5f);
        gate.Start(); gate.Add(CapEv.On, 2, 6000, 0, 0.5f); gate.Stop(); gate.Add(CapEv.On, 3, 6000, 0, 0.5f);
        r.Expect(gate.Count == 1, $"{gate.Count} events kept around Play and Stop (1 expected)");
    }

    // =====================================================================================================================================================================
    // runner

    public static List<CheckOutcome> RunChecks(string? only, Action<string>? log = null)
    {
        var list = new List<CheckOutcome>();
        foreach (var c in All)
        {
            if (only != null && !only.Split(',').Contains(c.Id)) continue;
            var r = new ImodelReport();
            var sw = Stopwatch.StartNew();
            try { c.Run(r); }
            catch (Exception e) { r.Failures.Add($"exception {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            var o = new CheckOutcome(c.Id, c.What, r.Failures.Count == 0, sw.Elapsed.TotalSeconds, r);
            list.Add(o);
            string verdict = !o.Passed ? "FAIL" : r.Warnings.Any(w => w.StartsWith("NOT RUN")) ? "NOT RUN" : r.Warnings.Count > 0 ? "WARN" : "PASS";
            log?.Invoke($"  {verdict,-7} {c.Id,-8} {r.Assertions,7} assertions  [{o.Seconds:0.0}s]" + (r.Notes.Count > 0 ? "\n        " + string.Join("\n        ", r.Notes) : "") + (r.Warnings.Count > 0 ? "\n        warning: " + string.Join("\n        warning: ", r.Warnings) : "") + (r.Failures.Count > 0 ? "\n        FAILED: " + string.Join("\n        FAILED: ", r.Failures.Take(8)) + (r.Failures.Count > 8 ? $"\n        ... and {r.Failures.Count - 8} more" : "") : ""));
        }
        return list;
    }

    public static (List<string> Lines, int Missed) RunMutants(string? only)
    {
        var lines = new List<string>();
        int missed = 0;
        foreach (var m in Mutants)
        {
            if (only != null && !only.Split(',').Contains(m.CatchingCheck)) continue;
            var check = All.First(c => c.Id == m.CatchingCheck);
            var r = new ImodelReport();
            CaptureMutants.Active = m.Code;
            try { check.Run(r); }
            catch (Exception e) { r.Failures.Add($"exception {e.GetType().Name}: {e.Message}"); }
            finally { CaptureMutants.Active = 0; }
            bool caught = r.Failures.Count > 0;
            if (!caught) missed++;
            lines.Add($"  {(caught ? "caught" : "MISSED")}  [{m.CatchingCheck}] {m.Name}: {r.Failures.Count} failing assertions" + (caught ? $", e.g. {(r.Failures[0].Length > 170 ? r.Failures[0][..167] + "..." : r.Failures[0])}" : ""));
        }
        return (lines, missed);
    }

    /// <summary>`FruityHost capture-test [--only id,id] [--no-mutants]`</summary>
    public static int Main(string[] args)
    {
        string? only = null;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--only") only = args[i + 1];
        bool mutants = !args.Contains("--no-mutants");
        Console.WriteLine("BrokenNes.FruityHost capture-test (Capture from FL without the plugin DLL: FL's pitch conventions, the tempo, the MIDI file read back independently, VRUN's converter, the lanes)");
        var results = RunChecks(only, Console.WriteLine);
        int failed = results.Count(o => !o.Passed), missed = 0;
        if (mutants)
        {
            Console.WriteLine("deliberate bugs (each must be caught by the check named):");
            var (lines, m) = RunMutants(only);
            foreach (var l in lines) Console.WriteLine(l);
            missed = m;
        }
        int notRun = results.Count(o => o.Passed && o.Report.Warnings.Any(w => w.StartsWith("NOT RUN")));
        Console.WriteLine($"{results.Count - failed - notRun} checks passed, {notRun} NOT RUN, {failed} failed, {results.Sum(o => o.Report.Assertions)} assertions; {missed} deliberate bugs missed");
        return failed == 0 && missed == 0 ? 0 : 1;
    }
}
