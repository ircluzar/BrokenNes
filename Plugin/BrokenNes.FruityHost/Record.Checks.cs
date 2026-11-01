// Round 8 (record): the checks of the recording session and its transcription that need no plugin DLL: `FruityHost record-test [--only id,id] [--no-mutants] [--seconds n]`, and the first half of
// `selftest --only record`.
//
//   root        root notes of samples of KNOWN pitch (BRR encoded here: sine, saw, square, a weak fundamental, fractional periods, a one-shot) within 3 cents; noise has none
//   tempo       synthetic onsets at known tempos (the driver's 2 ms or 4.5 ms tick as jitter, 16ths, 8ths, triplets) within 0.1 %; a tempo change found; random onsets give no tempo
//   capture     the capture keeps every write of a 60 s synthetic take (its count equals the writes made), versions and copies; a starved capture COUNTS what it lost; nothing allocated per write
//   transcribe  the synthetic driver's music: the notes read back as the driver played them; roots, keys, tempo, bend ranges; replayed from the MIDI bytes alone (VRUN's parser) through the
//               plugin's own pitch function and the presets' own tuning: every pitch value of a note no other note of its instrument overlaps is the game's register EXACTLY; volumes within 2;
//               times within 0.6 ms; the overlaps the transcription reports are the ones the replay finds
//   smf         the file read back by an independent parser (Python, written apart from the writer) and by VRUN's: structure, tracks, channels, notes, RPN ranges, tempo
//   section     REC1 round trip; every damage refused (cut, flipped bits, newer, unknown flag, lying lengths); the marker of a recording too big for a project
//   games       Super Mario World, Donkey Kong Country and a heavily corrupted Super Mario World (from X:\EMULATION\...: NOT RUN when the ROMs are not there): captured, transcribed, replayed
//               from the MIDI file: pitch exact, volumes, times; tempo against the driver's own tick; root notes against an FFT of the sample played by the preset's engine
using System.Diagnostics;
using System.Text;
using BrokenNes2;
using BrokenNes2.Instruments;
using BrokenNes2.Presets;
using BrokenNes2.Record;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static class RecordChecks
{
    public sealed record Check(string Id, string What, Action<ImodelReport> Run);
    public sealed record CheckOutcome(string Id, string What, bool Passed, double Seconds, ImodelReport Report);
    public sealed record Mutant(string Name, string CatchingCheck, int Code);

    public static double GameSeconds = 40;

    public static readonly Check[] All =
    [
        new("root", "the root note of samples of known pitch, within 3 cents; no pitch in noise", Root),
        new("tempo", "the tempo of synthetic onsets within 0.1 %, a tempo change found, no tempo in random onsets", Tempo),
        new("capture", "every write of a long take kept and counted; a starved capture counts its losses; versions, markers and copies; nothing allocated per write", Capture),
        new("transcribe", "the synthetic driver's music: notes, roots, keys, tempo; the MIDI file replayed through the plugin's pitch function gives the game's pitch register exactly", Transcribe),
        new("smf", "the MIDI file read back by an independent Python parser and by VRUN's", Smf),
        new("section", "the project section REC1 round-trips and refuses damage", Section),
        new("live", "live MIDI out of the synthetic driver: every key-on a note-on on its instrument's channel, key-on pitch and every held move and level reproduced, the roots, nothing allocated per write", LiveChecks.Live),
        new("match", "which track and which recorded note a channel plays, fed as FL 2026 hands notes (pitch from the channel's base, colour = MIDI channel, no song position): from the start on the first note, from the middle, a loop, edits, no colour, unrelated notes never taken, 23 tracks on shared channels; nothing allocated", MatchChecks.Match),
        new("games", "SMW, DKC and a corrupted SMW captured, transcribed and replayed: pitch exact, volumes, times, tempo against the driver's tick, roots against an FFT", Games),
    ];

    public static readonly Mutant[] Mutants =
    [
        new("pitch bends one register step off", "transcribe", RecMutants.BendOffByOne),
        new("the preset forgets its tuning", "transcribe", RecMutants.TuneIgnoredInPreset),
        new("the root note an octave off", "transcribe", RecMutants.RootOctaveOff),
        new("the tempo 1 % off", "transcribe", RecMutants.TempoOnePercent),
        new("no pitch-bend range written (RPN 0)", "transcribe", RecMutants.NoBendRange),
        new("velocity ignores the instrument's balance", "transcribe", RecMutants.VelocityIgnoresWeights),
        new("note starts quantised to the 16th grid", "transcribe", RecMutants.NotesQuantised),
        new("overlapping notes not counted", "transcribe", RecMutants.ConflictsHidden),
        new("track 2 written on track 1's channel", "smf", RecMutants.TrackMidiWrongChannel),
        new("every 1,000th write lost without a word", "capture", RecMutants.CaptureLosesWrites),
        new("a new instrument's marker lost", "capture", RecMutants.KeyOnVersionLost),
        new("the section's checksum not checked", "section", RecMutants.SectionCrcNotChecked),
        new("live MIDI out sends nothing", "live", RecMutants.LiveOutSilent),
        new("live notes keyed without their sample's root", "live", RecMutants.LiveRootIgnored),
        new("live note-offs lost", "live", RecMutants.LiveNoteOffLost),
        new("any key fits a track's note (an unrelated channel is taken)", "match", RecMutants.UnrelatedGrabbed),
        new("FL's pitch read from C5 only (the r13 matcher: an imported channel's pitch is from MIDI note 0)", "match", RecMutants.OldPitchBase),
        new("the note's colour ignored (the MIDI channel FL keeps in it)", "match", RecMutants.ColourIgnored),
        new("each note taken for the first recorded note of its key (no order, no time between notes)", "match", RecMutants.FollowByKeyOnly),
        new("past 15 tracks the MIDI channels repeat in order (two tracks starting on one key share a colour)", "match", RecMutants.SharedChannelsInOrder),
    ];

    // =====================================================================================================================================================================

    public static void Root(ImodelReport r)
    {
        var cases = new (double Period, int Loop, int Shape, string What)[]
        {
            (16, 16, 0, "2000 Hz sine"), (20, 80, 1, "1600 Hz saw"), (25, 400, 2, "1280 Hz square"), (32, 64, 0, "1000 Hz sine"), (37.5, 600, 1, "853.3 Hz saw (period 37.5)"),
            (48, 48, 3, "666.7 Hz weak fundamental"), (64, 64, 1, "500 Hz saw"), (100, 400, 0, "320 Hz sine"), (128, 128, 2, "250 Hz square"), (150, 1200, 3, "213.3 Hz weak fundamental"),
            (250, 2000, 1, "128 Hz saw"), (400, 400, 0, "80 Hz sine"), (533.333, 1600, 1, "60 Hz saw"),
        };
        double worst = 0;
        foreach (var (period, loop, shape, what) in cases)
        {
            var brr = RecBrr.Encode(RecBrr.Wave(period, loop, shape), true);
            var est = RecRoot.Estimate(brr, 0);
            double want = 32000 / period;
            double err = est.Found ? 1200 * Math.Log2(est.Hz / want) : double.NaN;
            r.Expect(est.Found && Math.Abs(err) <= 3, $"{what}: estimated {(est.Found ? $"{est.Hz:0.00} Hz ({err:+0.0;-0.0} cents)" : "no pitch: " + est.How)}");
            if (est.Found) worst = Math.Max(worst, Math.Abs(err));
        }
        // a one-shot: a 500 Hz sine that dies away over 4096 samples
        var shot = new short[4096];
        for (int i = 0; i < shot.Length; i++) shot[i] = (short)(12000 * Math.Exp(-i / 1500.0) * Math.Sin(2 * Math.PI * i / 64));
        var e1 = RecRoot.Estimate(RecBrr.Encode(shot, false), -1);
        r.Expect(e1.Found && Math.Abs(1200 * Math.Log2(e1.Hz / 500)) <= 3, $"a one-shot 500 Hz sine: {(e1.Found ? $"{e1.Hz:0.00} Hz" : e1.How)}");
        // noise: no pitch (or a very unclear one)
        var rnd = new Random(5);
        var noise = new short[4096];
        for (int i = 0; i < noise.Length; i++) noise[i] = (short)rnd.Next(-12000, 12000);
        var e2 = RecRoot.Estimate(RecBrr.Encode(noise, true), 0);
        r.Expect(!e2.Found, $"white noise has no pitch, but one was found: {e2.Hz:0.0} Hz ({e2.How})");
        // the decoder against the S-DSP itself: the sample played at $1000 by the real DSP (DSP_SFC) through the sampler gives the same samples
        var aram = new byte[0x10000]; var dsp = new byte[128];
        var brr2 = RecBrr.Encode(RecBrr.Wave(64, 64, 1), true);
        brr2.CopyTo(aram, 0x1000);
        aram[0x200] = 0x00; aram[0x201] = 0x10; aram[0x202] = 0x00; aram[0x203] = 0x10;
        dsp[0x5D] = 2; dsp[0x0C] = dsp[0x1C] = 0x7F; dsp[0x6C] = 0x20;
        var mine = RecRoot.Decode(brr2, 0, 400, out _);
        var real = new NesEmulator.Snes.DSP_SFC(aram);
        for (int i = 0; i < 128; i++) real.Write(i, dsp[i]);
        real.Write(0x6C, 0x20);
        real.Write(0x00, 0x7F); real.Write(0x01, 0x7F); real.Write(0x02, 0x00); real.Write(0x03, 0x10); real.Write(0x04, 0); real.Write(0x05, 0x8F); real.Write(0x06, 0xE0); real.Write(0x4C, 1);
        int outx = 0; var outs = new List<int>();
        for (int i = 0; i < 600; i++) { real.RunSample(); outs.Add((sbyte)real.Read(0x09)); outx++; }
        // OUTX is the interpolated, enveloped sample's top byte: compare the period, not the samples (the envelope and the Gaussian filter shape them)
        var zc = Enumerable.Range(201, 398).Count(i => outs[i - 1] < 0 && outs[i] >= 0);
        r.Expect(mine.Length == 400 && zc is >= 5 and <= 7, $"the decoder and the real S-DSP: {mine.Length} samples decoded, the DSP's own output crosses zero {zc} times in 398 samples (a 500 Hz tone: 6)");
        r.Note($"{cases.Length} looped samples of known pitch within {worst:0.00} cents; a one-shot {e1.Hz:0.00} Hz; noise: {e2.How}");
    }

    public static void Tempo(ImodelReport r)
    {
        var rnd = new Random(11);
        // music on a grid: a beat layer (two voices on every beat, the bass and a chord) and a melody on random tatums; quantised to the driver's tick
        List<double> Song(double bpm, int perBeat, double tick, double from, double to, double downbeat)
        {
            double beat = 60 / bpm, tatum = beat / perBeat;
            var s = new List<double>();
            for (double b = downbeat; b < to; b += beat) if (b >= from) { double q = Math.Round(b / tick) * tick; s.Add(q); s.Add(q + 0.0005); }
            for (double t = downbeat + tatum * rnd.Next(0, perBeat); t < to; t += tatum * rnd.Next(1, 4)) if (t >= from) s.Add(Math.Round(t / tick) * tick + 0.001);
            return s;
        }
        // the rule: the beat is the tatum x 2^n (or 3 x 2^n for a ternary grid) in 80-160 BPM; a song notated outside that range comes out folded into it
        static double Fold(double bpm) { while (bpm >= RecTempo.BeatHigh) bpm /= 2; while (bpm < RecTempo.BeatLow) bpm *= 2; return bpm; }
        foreach (var (bpm, tick, perBeat, label) in new[] { (72.0, 0.0045, 4, "72 BPM, 16ths, 4.5 ms tick"), (97.3, 0.002, 4, "97.3 BPM, 16ths, 2 ms tick"), (120.0, 0.0045, 2, "120 BPM, 8ths"), (150.0, 0.002, 4, "150 BPM, 16ths"), (177.0, 0.0045, 3, "177 BPM, triplets"), (133.33, 0.0045, 4, "133.33 BPM, 16ths") })
        {
            var starts = Song(bpm, perBeat, tick, 0.3, 60, 0.3);
            var res = RecTempo.Estimate(starts, 60);
            double want = Fold(bpm);
            double err = res.Found ? (res.Bpm / want - 1) * 100 : double.NaN;
            r.Expect(res.Found && Math.Abs(err) <= 0.1, $"{label}: {res.Describe()} ({err:+0.000;-0.000} % from {want:0.##} BPM)");
            r.Expect(!res.Found || res.Segments.Count == 1, $"{label}: {res.Segments.Count - 1} tempo changes found in a steady tempo");
            // the downbeat: on the beat layer's first beat, or a whole number of beats from it
            double beats = (res.FirstDownbeat - 0.3) / res.BeatSeconds;
            r.Expect(!res.Found || Math.Abs(beats - Math.Round(beats)) < 0.05, $"{label}: the first downbeat at {res.FirstDownbeat:0.000} s is {beats:0.00} beats from the song's first beat");
        }
        // a tempo change: 30 s at 120 then 30 s at 140
        {
            var starts = Song(120, 4, 0.002, 0.3, 30, 0.3).Concat(Song(140, 4, 0.002, 30.05, 60, 30.05)).ToList();
            var res = RecTempo.Estimate(starts, 60);
            var segs = res.Segments;
            bool ok = res.Found && segs.Count == 2 && Math.Abs(segs[0].Bpm / 120 - 1) < 0.002 && Math.Abs(segs[1].Bpm / 140 - 1) < 0.002 && Math.Abs(segs[1].Seconds - 30) < 12.5;
            r.Expect(ok, $"120 then 140 BPM: {res.Describe()}; segments {string.Join(", ", segs.Select(s => $"{s.Bpm:0.00} BPM from {s.Seconds:0.0} s"))}");
        }
        // random onsets: no tempo
        for (int seed = 1; seed <= 3; seed++)
        {
            var rr = new Random(seed);
            var starts = new List<double>();
            for (double t = 0.1; t < 60; t += -Math.Log(1 - rr.NextDouble()) * 0.15) starts.Add(t);
            var res = RecTempo.Estimate(starts, 60);
            r.Expect(!res.Found, $"random onsets (seed {seed}) gave a tempo: {res.Describe()}");
            r.Note($"random onsets (seed {seed}): {res.Describe()}");
        }
    }

    public static void Capture(ImodelReport r)
    {
        // a 60 s take of the synthetic driver: every write it made is in the session, in order
        var drv = new SynthDriver(132.5);
        var rec = new SpcRecorder();
        rec.Arm();
        var feeder = new Thread(() => { while (!rec.Halted) { rec.Feed(); Thread.Sleep(5); } }) { IsBackground = true };
        feeder.Start();
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        drv.Play(rec, 60);
        long alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        feeder.Join();
        var s = rec.Finish(drv.Aram, drv.Regs, "capture", "synthetic", "", 0, 1);
        int markers = s.RegVals.Count(v => v >> 8 == RecSession.MarkerVersion);
        r.Expect(s.Count - markers == drv.Probe && s.Dropped == 0, $"60 s synthetic take: the session holds {s.Count - markers:N0} writes (+{markers} instrument markers), the driver made {drv.Probe:N0}; {s.Dropped} counted as lost");
        r.Expect(s.Versions.Length == 4, $"4 instruments expected (3 samples, and a 4th under the 2nd one's number from 12 s), {s.Versions.Length} found: {string.Join(", ", s.Versions.Select(v => $"#{v.Srcn}@${v.Start:X4}"))}");
        r.Expect(markers == s.Versions.Length, $"one marker per instrument, at its first key-on: {markers} markers for {s.Versions.Length} instruments");
        // the copies hold each instrument's bytes as they were at its first key-on
        foreach (var (v, i) in s.Versions.Select((v, i) => (v, i)))
        {
            var want = SynthDriver.Instruments.First(x => x.Addr == v.Start);
            var bytes = s.SampleBytes(i, out bool ended);
            var brr = RecBrr.Encode(RecBrr.Wave(want.Period, want.Loop, want.Shape), true);
            r.Expect(ended && bytes.AsSpan().SequenceEqual(brr), $"instrument {i} (#{v.Srcn} at ${v.Start:X4}): the copy does not hold its sample ({bytes.Length} bytes, ended {ended})");
        }
        // the per-write cost: the capture must allocate nothing (the driver's own planning is done before the take: this measures the playing of the plan, which is OnWrite)
        r.Note($"60 s take: {s.Count:N0} entries in {Math.Ceiling(s.Count / (double)SpcRecorder.ChunkWrites)} chunks, {s.Shots.Length} sound-memory copies; the driver + capture thread allocated {alloc:N0} bytes in all");
        {
            var rec2 = new SpcRecorder(); rec2.Arm();
            var aram = new byte[0x10000]; var regs = new byte[128]; regs[0x5D] = 2;
            rec2.Begin(aram, regs, 0);
            for (int i = 0; i < 1000; i++) rec2.OnWrite(0x10, (byte)i, i);       // warm
            GC.Collect();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 300_000; i++) { regs[0x14] = (byte)(i & 7); rec2.OnWrite((byte)(i % 0x70 == 0x4C ? 0x10 : i % 0x70), (byte)i, 1000 + i); if (i % 997 == 0) { regs[0x4C] = 1; rec2.OnWrite(0x4C, 1, 1000 + i); } }
            long a1 = GC.GetAllocatedBytesForCurrentThread() - a0;
            r.Expect(a1 == 0, $"300,000 writes and 300 key-ons into the capture allocated {a1:N0} bytes on the writing thread (the chunks are made beforehand by Arm and Feed, on another thread in the plugin)");
        }
        // a starved capture (the feeder stalled): it runs out of chunks and COUNTS every write it could not keep
        {
            var rec3 = new SpcRecorder();
            rec3.Arm();
            rec3.StallFeeder(true);
            var aram = new byte[0x10000]; var regs = new byte[128];
            rec3.Begin(aram, regs, 0);
            long made = 0;
            for (long i = 0; i < 600_000; i++) { rec3.OnWrite(0x12, (byte)i, i * 4); rec3.Feed(); made++; }
            rec3.Halt(RecEnd.Stop);
            var s3 = rec3.Finish(aram, regs, "starved", "synthetic", "", 0, 2);
            r.Expect(s3.Dropped > 0 && s3.Count + s3.Dropped == made, $"600,000 writes with the feeder stalled after the first {SpcRecorder.FirstChunks} chunks: kept {s3.Count:N0} + counted lost {s3.Dropped:N0} = {s3.Count + s3.Dropped:N0} of {made:N0}");
            var tr3 = RecTranscribe.Run(s3, new RecOptions { Name = "starved" });
            r.Expect(tr3.Facts.Any(f => f.Contains("lost")), "the transcription of a starved take does not say writes were lost: " + string.Join(" | ", tr3.Facts));
        }
        // the memory cap: a take that fills every chunk stops there and says so
        {
            var rec4 = new SpcRecorder(); rec4.Arm();
            var aram = new byte[0x10000]; var regs = new byte[128];
            rec4.Begin(aram, regs, 0);
            long i = 0;
            while (!rec4.Halted && i < (long)SpcRecorder.MaxChunks * SpcRecorder.ChunkWrites + 10) { rec4.OnWrite(0x12, (byte)i, i); if (i % 30_000 == 0) rec4.Feed(); i++; }
            r.Expect(rec4.Halted && rec4.EndedBy == RecEnd.MemoryCap && rec4.Count == (long)SpcRecorder.MaxChunks * SpcRecorder.ChunkWrites, $"the cap: halted {rec4.Halted}, ended by {rec4.EndedBy}, {rec4.Count:N0} writes kept of a cap of {(long)SpcRecorder.MaxChunks * SpcRecorder.ChunkWrites:N0}, {rec4.MemoryBytes / 1048576.0:0.0} MB");
        }
    }

    // ---- the replay of a MIDI file through the plugin's pitch function, against the game's registers ----

    public sealed class ReplayStats
    {
        public int Values, Exact, Reported, Unreported, Transient, Notes, Matched, VolValues, VolWithin, VolReported, VolOtherBalance, VolTransient, TimesOk;
        public double MaxTimeErrMs, MaxVolErr, MaxPitchErrCents;
        public List<string> Problems = new();
        public string Line() => $"{Notes} notes; pitch: {Exact:N0} of {Values:N0} values held 2 ms or more exact, {Reported} inexact as the transcription says, {Unreported} inexact WITHOUT it saying so ({Transient:N0} values held under 2 ms not compared); volume within 2: {VolWithin:N0} of {VolValues:N0} ({VolReported} reported, {VolOtherBalance:N0} values of notes with another balance than their preset's, max error {MaxVolErr}); times: {TimesOk} of {Matched} within 0.6 ms (max {MaxTimeErrMs:0.000} ms)";
    }

    /// <summary>Replays a transcription's MIDI file (read by VRUN's parser) note by note through the plugin's pitch function with each track's PRESET (its tuning and weights read back from the preset file), and compares with
    /// the game's own registers: <paramref name="truth"/> gives, per track in key-on order, each note (its index in the transcription, start, end) with its pitch and volume timelines (seconds from the recording's start).
    /// Every value the game held for at least 2 ms is evaluated in the middle of its time: it must be exact, unless the transcription itself lists it as inexact (and then it is counted as such).</summary>
    public static ReplayStats Replay(Transcription tr, Func<int, List<(int Note, double Start, double End, List<(double T, int P)> Pitch, List<(double T, int L, int R)> Vol)>> truth)
    {
        var st = new ReplayStats();
        var m = MidiReplay.Read(tr.Midi);
        st.Problems.AddRange(m.Problems);
        double offset = double.NaN, hold = RecTranscribe.MinHold / 32000.0;
        for (int k = 0; k < tr.Tracks.Count; k++)
        {
            var track = tr.Tracks[k];
            var pd = PresetFile.Read(track.PresetFile ?? []);
            if (pd.Data == null) { st.Problems.Add($"track {k + 1}: no preset ({track.PresetProblem ?? pd.Problem})"); continue; }
            var inst = InstrumentCodec.Unpack(pd.Data.Instrument, out _)!;
            int tune = inst.Get("tune.cents"), wl = inst.Get("vol.l"), wr = inst.Get("vol.r");
            var notes = m.Notes.Where(n => n.Track == k + 1).ToList();
            var game = truth(k);
            if (notes.Count != game.Count) st.Problems.Add($"track {k + 1}: the file has {notes.Count} notes, the game {game.Count}");
            if (notes.Count == 0) continue;
            int ch = notes[0].Channel;
            var chan = m.Of(k + 1, ch);
            if (!chan.RpnSeen) st.Problems.Add($"track {k + 1} (channel {ch + 1}): no pitch-bend range (RPN 0) in the file");
            int range = chan.Range >= 0 ? chan.Range : 2;
            var bends = chan.Bends.Select(b => (b.Tick, b.Bend)).ToList();
            var ccs = chan.Cc7.Select(b => (b.Tick, b.Cc)).ToList();
            for (int i = 0; i < Math.Min(notes.Count, game.Count); i++)
            {
                var n = notes[i]; var g = game[i];
                st.Notes++;
                // time: the note's start in the file against the game's (one offset for the whole file: the lead-in before the first downbeat)
                double fileStart = m.Seconds(n.On);
                if (double.IsNaN(offset)) offset = fileStart - g.Start;
                double terr = Math.Abs(fileStart - offset - g.Start) * 1000;
                st.Matched++;
                st.MaxTimeErrMs = Math.Max(st.MaxTimeErrMs, terr);
                if (terr <= 0.6) st.TimesOk++;
                // pitch: each value the game held for 2 ms or more, in the middle of its time, from the file's key, the channel's bend then and the preset's tuning
                for (int v = 0; v < g.Pitch.Count; v++)
                {
                    var (t0, p) = g.Pitch[v];
                    double t1 = v + 1 < g.Pitch.Count ? g.Pitch[v + 1].T : g.End;
                    if (t1 - t0 < hold) { st.Transient++; continue; }
                    double t = (t0 + t1) / 2;
                    long tick = Math.Max(n.On, m.TickAt(t + offset));
                    if (tick >= n.Off) tick = n.Off - 1;
                    int bend = MidiReplay.At(bends, tick, 0);
                    double cents = (float)((n.Key - 60) * 100 + bend * range * 100.0 / 8192.0);
                    int reg = SnesSampler.PitchForTuned(cents, tune);
                    st.Values++;
                    if (reg == p) { st.Exact++; continue; }
                    st.MaxPitchErrCents = Math.Max(st.MaxPitchErrCents, Math.Abs(1200 * Math.Log2(Math.Max(1, reg) / (double)Math.Max(1, p))));
                    if (tr.Reported(tr.InexactPitch, g.Note, (uint)Math.Round(t * 32000))) { st.Reported++; continue; }
                    st.Unreported++;
                    if (st.Problems.Count < 12) st.Problems.Add($"track {k + 1} note {i} at {t:0.000} s: the replay plays ${reg:X4}, the game wrote ${p:X4} (key {n.Key}, bend {bend}, range {range}, tuning {tune}), and the transcription did not say so");
                }
                // volume: velocity x CC7 as the voice level, the preset's weights
                var bal = g.Vol[0];
                int bm = Math.Max(Math.Abs(bal.L), Math.Abs(bal.R));
                bool sameBalance = bm == 0 || ((int)Math.Round(127.0 * bal.L / bm), (int)Math.Round(127.0 * bal.R / bm)) == (wl, wr);
                for (int v = 0; v < g.Vol.Count; v++)
                {
                    var (t0, l, rr) = g.Vol[v];
                    double t1 = v + 1 < g.Vol.Count ? g.Vol[v + 1].T : g.End;
                    if (t1 - t0 < hold) { st.VolTransient++; continue; }
                    double t = (t0 + t1) / 2;
                    long tick = Math.Max(n.On, m.TickAt(t + offset));
                    if (tick >= n.Off) tick = n.Off - 1;
                    int cc = MidiReplay.At(ccs, tick, 100);
                    float vol = (float)(n.Velocity / 127.0 * cc / 100.0);
                    int level = Math.Clamp((int)Math.Round(127 * vol), 0, 127);
                    int e = Math.Max(Math.Abs(level * wl / 127 - l), Math.Abs(level * wr / 127 - rr));
                    if (!sameBalance) { st.VolOtherBalance++; continue; }
                    st.VolValues++;
                    if (e <= 2) st.VolWithin++;
                    else if (tr.Reported(tr.InexactVolume, g.Note, (uint)Math.Round(t * 32000))) st.VolReported++;
                    else st.MaxVolErr = Math.Max(st.MaxVolErr, e);
                }
            }
        }
        return st;
    }

    /// <summary>The truth per track from a transcription's own notes (for real games: the extraction itself is checked against the synthetic driver).</summary>
    public static Func<int, List<(int, double, double, List<(double, int)>, List<(double, int, int)>)>> TruthOf(Transcription tr) => k =>
        tr.Tracks[k].Notes.Select(i => (i, tr.Notes[i])).Select(x => (x.i, x.Item2.StartSeconds, x.Item2.End / 32000.0, x.Item2.Pitches.Select(p => (p.T / 32000.0, p.P)).ToList(), x.Item2.Vols.Select(v => (v.T / 32000.0, v.L, v.R)).ToList())).ToList();

    public static void Transcribe(ImodelReport r)
    {
        const double bpm = 132.5;
        var drv = new SynthDriver(bpm);
        var rec = new SpcRecorder();
        rec.Arm();
        for (int i = 0; i < 40; i++) rec.Feed();
        drv.Play(rec, 30);
        var s = rec.Finish(drv.Aram, drv.Regs, "Synth", "synthetic driver", "", 1_760_000_000, 3);
        // 1. the notes read back are the driver's
        var notes = RecNotes.Extract(s, out var ns);
        int same = 0;
        for (int i = 0; i < Math.Min(notes.Count, drv.Notes.Count); i++)
        {
            var a = notes[i]; var b = drv.Notes[i];
            bool eq = a.Voice == b.Voice && a.Srcn == b.Srcn && a.Start == b.Start && a.End == b.End && a.Pitches.Select(p => (p.T, p.P)).SequenceEqual(b.Pitches.Select(p => (p.T, p.P))) && a.Vols.Select(v => (v.T, v.L, v.R)).SequenceEqual(b.Vols.Select(v => (v.T, v.L, v.R)));
            if (eq) same++;
            else if (r.Failures.Count < 4) r.Expect(false, $"note {i}: read back as voice {a.Voice} #{a.Srcn} {a.Start}-{a.End} with {a.Pitches.Count} pitch and {a.Vols.Count} volume values; the driver played voice {b.Voice} #{b.Srcn} {b.Start}-{b.End} with {b.Pitches.Count} and {b.Vols.Count}");
        }
        r.Expect(notes.Count == drv.Notes.Count && same == notes.Count, $"{same} of {drv.Notes.Count} notes read back exactly as the driver played them ({notes.Count} read)");
        // 2. the transcription
        var tr = RecTranscribe.Run(s, new RecOptions { Name = "Synth" });
        r.Expect(tr.Tracks.Count == 4, $"4 tracks expected, {tr.Tracks.Count}: {string.Join(", ", tr.Tracks.Select(t => t.Name))}");
        foreach (var t in tr.Tracks)
        {
            int ins = Array.FindIndex(SynthDriver.Instruments, x => x.Addr == t.Start);
            double f0 = SynthDriver.F0(ins);
            r.Expect(t.Root.Found && Math.Abs(1200 * Math.Log2(t.Root.Hz / f0)) <= 5, $"{t.Name}: root {t.Root.Hz:0.00} Hz, the sample is {f0:0.00} Hz");
            // the keys are the driver's own notes (its fine tuning is the preset's)
            var keys = t.Notes.Select(i => drv.Notes[i].Key).ToList();
            int keyOk = t.Keys.Zip(keys).Count(p => p.First == p.Second);
            r.Expect(keyOk == keys.Count, $"{t.Name}: {keyOk} of {keys.Count} MIDI keys are the notes the driver played (first wrong: {t.Keys.Zip(keys).FirstOrDefault(p => p.First != p.Second)})");
            r.Expect(t.BendRange >= 2 && t.BendRange <= 8, $"{t.Name}: bend range {t.BendRange} semitones");
            r.Note($"{t.Name}: {t.Notes.Count} notes, {t.RootText}, tuning {t.Tune} cents, driver offset {t.DriverCents:+0.0;-0.0} cents ({t.DriverConcentration:0.00}), bend range {t.BendRange}, weights {t.WeightL}/{t.WeightR}");
        }
        r.Expect(tr.Tempo.Found && Math.Abs(tr.Tempo.Bpm / bpm - 1) < 0.001, $"tempo: {tr.Tempo.Describe()}, the driver played {bpm} BPM");
        var m = MidiReplay.Read(tr.Midi);
        double fileBpm = 60e6 / m.Tempo[0].Us;
        r.Expect(Math.Abs(fileBpm / bpm - 1) < 0.001, $"the file's tempo is {fileBpm:0.000} BPM, the driver's {bpm}");
        // the downbeat: the file's bar lines fall on the driver's bars (its first note at 0.05 s + two 16ths, bars from 0.05 s)
        // 3. the replay from the file
        var st = Replay(tr, TruthOf(tr));
        foreach (var p in st.Problems.Take(6)) r.Expect(false, p);
        r.Expect(st.Unreported == 0, $"pitch: {st.Exact:N0} of {st.Values:N0} values exact, {st.Reported} inexact as reported, {st.Unreported} inexact and NOT reported");
        r.Expect(st.Exact >= 0.9 * st.Values, $"pitch: only {st.Exact:N0} of {st.Values:N0} values exact");
        r.Expect(st.VolWithin + st.VolReported == st.VolValues, $"volume: {st.VolWithin} of {st.VolValues} values within 2, {st.VolReported} reported (max unreported error {st.MaxVolErr})");
        r.Expect(st.TimesOk == st.Matched, $"times: {st.TimesOk} of {st.Matched} note starts within 0.6 ms (max {st.MaxTimeErrMs:0.000} ms)");
        // the transcription's own count of exact values agrees with the replay's (the replay compares at one moment per value; the transcription marks a value if it was wrong at any moment)
        r.Expect(tr.PitchExact <= st.Exact + st.Transient, $"the transcription counts {tr.PitchExact:N0} exact values, the replay finds {st.Exact:N0}");
        // presets: one per track, readable, playable, the tuning and the envelope of the track
        foreach (var t in tr.Tracks)
        {
            var rd = PresetFile.Read(t.PresetFile ?? []);
            r.Expect(rd.Ok, $"{t.Name}: the preset does not read back: {rd.Problem ?? t.PresetProblem}");
            if (!rd.Ok) continue;
            var play = PresetPlayable.Build(rd.Data!, out var why);
            r.Expect(play?.Snes != null, $"{t.Name}: the preset does not build: {why}");
            r.Expect(rd.Data!.Name == t.Name, $"the preset is named \"{rd.Data.Name}\", its track \"{t.Name}\"");
        }
        r.Note(st.Line());
        r.Note($"{tr.Notes.Count} notes, {tr.Tracks.Count} tracks, {tr.Midi.Length:N0} bytes of MIDI; tempo {tr.Tempo.Describe()}; facts: " + string.Join(" | ", tr.Facts));
        // 4. a recording in which the driver keyed nothing (a note that began before Record bends on): no track, a file FL can still read, and the window says why
        var rq = new SpcRecorder();
        rq.Arm();
        var aram = new byte[0x10000]; var regs = new byte[128];
        regs[0x4C] = 1; regs[0x03] = 0x10; regs[0x08] = 0x40;                  // (voice 0 sounding: its envelope is up)
        rq.Begin(aram, regs, 0);
        for (int i = 0; i < 400; i++) rq.OnWrite(0x03, (byte)(0x10 + (i & 1)), i * 288L);
        rq.Clock(400 * 288);
        rq.Halt(RecEnd.Stop);
        var sq = rq.Finish(aram, regs, "Quiet", "synthetic driver", "", 1_760_000_000, 4);
        var tq = RecTranscribe.Run(sq, new RecOptions { Name = "Quiet", FlTempo = 120 });
        var mq = MidiReplay.Read(tq.Midi);
        r.Expect(tq.Tracks.Count == 0 && tq.Notes.Count == 0 && mq.Problems.Count == 0 && mq.Notes.Count == 0, $"a recording with no key-on: {tq.Tracks.Count} tracks, {tq.Notes.Count} notes, MIDI problems: {string.Join("; ", mq.Problems)}");
        r.Expect(tq.Facts.Count > 0 && tq.Facts[0].StartsWith("0 notes") && tq.Facts.Any(f => f.Contains("no key-on")), "a recording with no key-on does not say so: " + string.Join(" | ", tq.Facts));
    }

    public static void Smf(ImodelReport r)
    {
        var drv = new SynthDriver(132.5, 3);
        var rec = new SpcRecorder(); rec.Arm(); for (int i = 0; i < 20; i++) rec.Feed();
        drv.Play(rec, 20);
        var tr = RecTranscribe.Run(rec.Finish(drv.Aram, drv.Regs, "Smf", "synthetic", "", 1_760_000_000, 4), new RecOptions { Name = "Smf" });
        var m = MidiReplay.Read(tr.Midi);
        r.Expect(m.Format == 1 && m.TrackNames.Count == tr.Tracks.Count + 1, $"VRUN's parser: format {m.Format}, {m.TrackNames.Count} tracks (a conductor and {tr.Tracks.Count})");
        foreach (var p in m.Problems.Take(4)) r.Expect(false, "VRUN's parser: " + p);
        var chans = new HashSet<int>();
        for (int k = 0; k < tr.Tracks.Count; k++)
        {
            var c = m.TrackChannels[k + 1];
            r.Expect(c.Count == 1, $"track {k + 1} uses {c.Count} MIDI channels ({string.Join(",", c)})");
            r.Expect(c.Count == 0 || chans.Add(c.First()), $"track {k + 1}'s channel {c.FirstOrDefault() + 1} is also another track's");
            r.Expect(m.TrackNames[k + 1] == tr.Tracks[k].Name, $"track {k + 1} is named \"{m.TrackNames[k + 1]}\", expected \"{tr.Tracks[k].Name}\"");
        }
        r.Expect(!m.TrackChannels[0].Any(), "the conductor track carries channel events");
        // single-track files: type 0, one channel
        for (int k = 0; k < tr.Tracks.Count; k++)
        {
            var one = MidiReplay.Read(tr.TrackMidi(k));
            r.Expect(one.Format == 0 && one.TrackNames.Count == 1 && one.TrackChannels[0].Count == 1 && one.Notes.Count == tr.Tracks[k].Notes.Count && one.Tempo.Count == tr.Map.Count, $"track {k + 1} alone: format {one.Format}, {one.TrackNames.Count} tracks, {one.Notes.Count} notes of {tr.Tracks[k].Notes.Count}, {one.Tempo.Count} tempo events");
        }
        // the independent parser (Python, written apart from the writer: Record.SmfCheck.py)
        string? py = Python();
        if (py == null) { r.Warn("NOT RUN: no Python 3 on this machine: the file is not read by the independent parser"); return; }
        string dir = Path.Combine(Path.GetTempPath(), $"bn2_record_{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "smf.mid");
        File.WriteAllBytes(file, tr.Midi);
        string script = Path.Combine(MidiFixtures.RepoRoot(), @"Plugin\BrokenNes.FruityHost\Record.SmfCheck.py");
        var p2 = MidiFixtures.Run(py, ["-I", script, file], timeoutSec: 60);
        r.Expect(p2.Exit == 0, $"Record.SmfCheck.py refused the file (exit {p2.Exit}): {p2.Output.Trim()}");
        if (p2.Exit != 0) return;
        // its summary: one line per track "track i name=... channels=... notes=n bends=b range=R"
        var lines = p2.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string head = lines.FirstOrDefault(l => l.StartsWith("format")) ?? "";
        r.Expect(head == $"format 1 tracks {tr.Tracks.Count + 1} ppq {RecMidi.Ppq}", $"the Python parser's header: \"{head}\"");
        for (int k = 0; k < tr.Tracks.Count; k++)
        {
            var t = tr.Tracks[k];
            string want = $"track {k + 1} channels {t.Channel + 1} notes {t.Notes.Count} range {t.BendRange}";
            r.Expect(lines.Any(l => l.StartsWith(want)), $"the Python parser has no line \"{want}...\"; it said: {lines.FirstOrDefault(l => l.StartsWith($"track {k + 1} ")) ?? "nothing"}");
        }
        string tempoLine = lines.FirstOrDefault(l => l.StartsWith("tempo")) ?? "";
        r.Expect(tempoLine.StartsWith($"tempo {tr.Map.Count} first {tr.Map[0].MicrosPerQuarter}"), $"the Python parser's tempo: \"{tempoLine}\"");
        r.Note("Python: " + string.Join(" / ", lines.Take(3)));
        // a damaged file is refused by it (the parser can fail)
        var bad = (byte[])tr.Midi.Clone();
        bad[18] ^= 0x40;                                      // the first track's length
        File.WriteAllBytes(file, bad);
        var p3 = MidiFixtures.Run(py, ["-I", script, file], timeoutSec: 60);
        r.Expect(p3.Exit != 0, "the Python parser accepted a file whose first track lies about its length");
    }

    private static string? Python()
    {
        foreach (var cand in new[] { "python", "py" })
        {
            try { var t = MidiFixtures.Run(cand, ["-I", "-c", "import sys; print(sys.version_info[0])"], timeoutSec: 60); if (t.Exit == 0 && t.Output.Trim().StartsWith('3')) return cand; }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }

    public static void Section(ImodelReport r)
    {
        var drv = new SynthDriver(120, 5);
        var rec = new SpcRecorder(); rec.Arm(); for (int i = 0; i < 20; i++) rec.Feed();
        drv.Play(rec, 8);
        var s = rec.Finish(drv.Aram, drv.Regs, "Section test", "synthetic", "0123456789abcdef0123456789abcdef01234567", 1_760_000_123, 77);
        var b = RecSection.Build(s);
        var back = RecSection.Read(b, out var why);
        r.Expect(back != null, "the section does not read back: " + why);
        if (back == null) return;
        r.Expect(back.ToPayload().AsSpan().SequenceEqual(s.ToPayload()), "the session read back differs from the one saved");
        var tr1 = RecTranscribe.Run(s, new RecOptions { Name = "S" }); var tr2 = RecTranscribe.Run(back, new RecOptions { Name = "S" });
        r.Expect(tr1.Midi.AsSpan().SequenceEqual(tr2.Midi) && tr1.Tracks.Zip(tr2.Tracks).All(p => (p.First.PresetFile ?? []).AsSpan().SequenceEqual(p.Second.PresetFile ?? [])), "the reopened recording transcribes to other bytes (MIDI or presets)");
        r.Expect(RecSection.Build(back).AsSpan().SequenceEqual(b), "saving the read-back session again gives other bytes");
        // damage: cut at many lengths, a flipped bit in many places
        int refused = 0, tried = 0;
        var rnd = new Random(3);
        for (int cut = 0; cut < b.Length; cut += Math.Max(1, b.Length / 400)) { tried++; if (RecSection.Read(b.AsSpan(0, cut), out _) == null) refused++; }
        for (int k = 0; k < 600; k++)
        {
            var d = (byte[])b.Clone();
            int at = k < 40 ? k : rnd.Next(d.Length);
            d[at] ^= (byte)(1 << rnd.Next(8));
            tried++;
            if (RecSection.Read(d, out _) == null) refused++;
        }
        r.Expect(refused == tried, $"{refused} of {tried} damaged sections refused");
        // newer, unknown flag, lying length: refused with a valid checksum
        byte[] Resign(byte[] x) { var y = (byte[])x.Clone(); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(y.AsSpan(y.Length - 4), BrokenNes2.Music.Crc32.Of(y.AsSpan(0, y.Length - 4))); return y; }
        var newer = (byte[])b.Clone(); newer[0] = 2;
        RecSection.Read(Resign(newer), out var wNewer);
        var flag = (byte[])b.Clone(); flag[2] |= 0x80;
        RecSection.Read(Resign(flag), out var wFlag);
        var lie = (byte[])b.Clone(); lie[9] ^= 0x10;                       // the stored length 4 KB off
        RecSection.Read(Resign(lie), out var wLie);
        var lie2 = (byte[])b.Clone(); lie2[4] ^= 1;                        // the payload length one off
        RecSection.Read(Resign(lie2), out var wLie2);
        r.Expect(wNewer?.Contains("newer") == true && wFlag?.Contains("does not know") == true && wLie?.Contains("damaged") == true && wLie2?.Contains("damaged") == true,
            $"refusals: newer \"{wNewer}\", flag \"{wFlag}\", stored length \"{wLie}\", payload length \"{wLie2}\"");
        // a recording too big for a project: a marker that says so
        int old = RecSection.MaxStored;
        RecSection.MaxStored = 100;
        try
        {
            var mk = RecSection.Build(s);
            r.Expect(RecSection.Read(mk, out var wm) == null && wm?.Contains("MB a project keeps") == true, $"a recording over the cap: \"{wm}\"");
        }
        finally { RecSection.MaxStored = old; }
        r.Note($"a {s.Seconds:0.0} s recording: {b.Length:N0} bytes in the project ({s.ToPayload().Length:N0} before compression); {refused} of {tried} damaged sections refused");
    }

    // =====================================================================================================================================================================

    public static void Games(ImodelReport r)
    {
        const string Lib = @"X:\EMULATION\Super Nintendo\Super Nintendo (U)";
        int frames = (int)(GameSeconds * 60);
        var games = new List<(string Label, Func<RecGame?> Make, Action<RecGame>? Corrupt)>
        {
            ("Super Mario World", () => RecGame.Make(ImodelGames.Smw, 3800, InjectRun.SmwScript), null),
            ("Donkey Kong Country", () => ImodelGames.Find(Lib, "Donkey Kong Country (U) (V1.2)", ".smc") is { } p ? RecGame.Make(p, 1500, null) : null, null),
            ("Super Mario World, corrupted", () => RecGame.Make(ImodelGames.Smw, 3800, InjectRun.SmwScript), g => CorruptAlive(g, r)),
            ("Super Mario World (intro scenes), corrupted", () => RecGame.Make(ImodelGames.Smw, 2200, InjectRun.SmwScript), g => CorruptAlive(g, r)),
        };
        foreach (var (label, make, corrupt) in games)
        {
            var sw = Stopwatch.StartNew();
            using var g = make();
            if (g == null) { r.Warn($"NOT RUN: {label}: the ROM is not on this machine"); continue; }
            corrupt?.Invoke(g);
            var s = g.Record(frames, label.Replace(",", ""));
            int markers = s.RegVals.Count(v => v >> 8 == RecSession.MarkerVersion);
            r.Expect(s.Count - markers == g.Probe && s.Dropped == 0, $"{label}: the session holds {s.Count - markers:N0} writes, the S-DSP's own probe counted {g.Probe:N0}; {s.Dropped} lost");
            var tr = RecTranscribe.Run(s, new RecOptions { Name = label.Length > 20 ? label[..20] : label });
            r.Expect(tr.Notes.Count > 20, $"{label}: only {tr.Notes.Count} notes in {GameSeconds} s");
            var st = Replay(tr, TruthOf(tr));
            foreach (var p in st.Problems.Take(4)) r.Expect(false, $"{label}: {p}");
            r.Expect(st.Unreported == 0, $"{label}: pitch {st.Exact:N0} of {st.Values:N0} exact, {st.Reported} inexact as reported, {st.Unreported} inexact and NOT reported");
            r.Expect(st.VolWithin + st.VolReported == st.VolValues, $"{label}: volume {st.VolWithin:N0} of {st.VolValues:N0} values within 2, {st.VolReported} reported (max unreported {st.MaxVolErr})");
            r.Expect(st.TimesOk == st.Matched, $"{label}: times {st.TimesOk} of {st.Matched} within 0.6 ms (max {st.MaxTimeErrMs:0.000})");
            // tempo against the driver's own clock: the S-DSP write bursts come at the driver's sequencer ticks; for Nintendo's driver (SMW) the tick is also computed from the SPC700's timer 0 and the
            // driver's tempo byte in audio RAM (a tick each time the byte, added every timer period to an 8-bit accumulator, carries: period x 256 / tempo)
            double tick = DriverTick(s);
            string tempoLine = tr.Tempo.Found ? $"{tr.Tempo.Describe()}; tatum {tr.Tempo.TatumSeconds * 1000:0.000} ms = {tr.Tempo.TatumSeconds / tick:0.000} driver ticks of {tick * 1000:0.000} ms" : tr.Tempo.Describe() + $" (driver tick {tick * 1000:0.000} ms)";
            if (corrupt == null) r.Expect(tr.Tempo.Found, $"{label}: no tempo found: {tempoLine}");
            if (corrupt == null && tr.Tempo.Found)
            {
                double ticks = tr.Tempo.TatumSeconds / tick;
                if (label.StartsWith("Super Mario")) r.Expect(Math.Abs(ticks - Math.Round(ticks)) < 0.002 * ticks, $"{label}: the tatum is not a whole number of the driver's ticks: {tempoLine}");
                if (label.StartsWith("Super Mario") && NintendoTick(g, tick) is var (period, tempoByte, at, seqTick) && seqTick > 0)
                {
                    double beatTicks = tr.Tempo.BeatSeconds / seqTick;
                    tempoLine += $"; timer 0 period {period * 1000:0.000} ms, tempo byte {tempoByte} at ${at:X4}: sequencer tick {seqTick * 1000:0.0000} ms, the beat is {beatTicks:0.0000} of them";
                    r.Expect(Math.Abs(beatTicks - Math.Round(beatTicks)) < 0.001 * beatTicks, $"{label}: the beat is not a whole number of the driver's sequencer ticks: {tempoLine}");
                }
            }
            // root notes: the estimate against an FFT of the sample played by the preset's engine at $1000: the strongest peak should be a harmonic of the fundamental found (k x f0)
            var roots = new List<string>();
            int consistent = 0, compared = 0;
            foreach (var t in tr.Tracks.Where(t => t.Lane == 0 && t.Root.Found && t.PresetFile != null))
            {
                double fft = PresetFft(t);
                if (double.IsNaN(fft)) continue;
                compared++;
                double ratio = fft / t.Root.Hz;
                int k = (int)Math.Round(ratio);
                bool ok = k >= 1 && Math.Abs(ratio - k) < 0.03 * k;
                if (ok) consistent++;
                roots.Add($"#{t.Srcn} {t.Root.Hz:0.0} Hz: peak {fft:0.0} Hz = {ratio:0.000} x{(ok ? $" (harmonic {k})" : " (NOT a harmonic)")}");
            }
            if (compared > 0) roots.Insert(0, $"{consistent} of {compared} strongest FFT peaks are harmonics of the fundamental found");
            // lanes: the same recording with every instrument's overlapping notes on tracks of their own: nothing is shared, so every value within the bend range is exact
            var trL = RecTranscribe.Run(s, new RecOptions { Name = "lanes", Lanes = true });
            var stL = Replay(trL, TruthOf(trL));
            foreach (var p in stL.Problems.Take(3)) r.Expect(false, $"{label} (lanes): {p}");
            r.Expect(stL.Unreported == 0 && trL.Tracks.Sum(t => t.SharedValues + t.SharedVolumes) == 0, $"{label} (lanes): {stL.Unreported} unreported inexact values, {trL.Tracks.Sum(t => t.SharedValues)} pitch and {trL.Tracks.Sum(t => t.SharedVolumes)} volume values still shared");
            r.Note($"{label} with lanes: {trL.Tracks.Count} tracks for {trL.Tracks.Count(t => t.Lane == 0)} instruments; {stL.Line()}");
            r.Note($"{label}: {s.Seconds:0.0} s, {s.Count:N0} writes, {tr.Notes.Count} notes, {tr.Tracks.Count} tracks; {st.Line()}");
            r.Note($"{label}: tempo {tempoLine}");
            r.Note($"{label}: facts: {string.Join(" | ", tr.Facts)}");
            if (roots.Count > 0) r.Note($"{label}: roots: {string.Join("; ", roots)}");
            r.Note($"{label}: bend ranges {string.Join(",", tr.Tracks.Select(t => t.BendRange))}; disturbed {tr.Tracks.Sum(t => t.Disturbed)}, recut {tr.Tracks.Sum(t => t.Recut)}; ran in {sw.Elapsed.TotalSeconds:0} s");
            LastGames[label] = tr;
        }
    }

    public static readonly Dictionary<string, Transcription> LastGames = new();

    /// <summary>A heavily corrupted state made with the shared corruption library (300 random bytes of audio RAM, the driver's code excluded at $0400-$0FFF... any byte): the first seed whose driver still keys notes and whose music differs from the clean game's.</summary>
    private static void CorruptAlive(RecGame g, ImodelReport r)
    {
        var state = g.S.SaveState();
        for (long seed = 1; seed <= 24; seed++)
        {
            g.S.LoadState(state);
            var domains = NesEmulator.Corruption.MachineDomains.For(g.S);
            var writes = NesEmulator.Corruption.BlastGenerator.Generate(domains, ["ARAM"], 300, NesEmulator.Corruption.BlastType.Random, seed, ConsoleKind.Snes);
            NesEmulator.Corruption.CorruptionApplier.Apply(domains, writes);
            // alive? key-ons in the next 5 s
            long keyOns = 0;
            g.Spc.DspWriteTap = (reg, v, _) => { if (reg == 0x4C) keyOns += System.Numerics.BitOperations.PopCount(v); };
            var buf = new short[1 << 16];
            for (int f = 0; f < 300; f++) { g.S.SetPad(0, (PadButtons)g.Pad(g.Frame + f)); g.S.RunFrame(); while (g.S.ReadSamples(buf) > 0) { } }
            g.Spc.DspWriteTap = null;
            if (keyOns >= 60)
            {
                g.Frame += 300;
                r.Note($"corrupted state: seed {seed}, {writes.Count} random writes into audio RAM (the shared corruption library), the driver keyed {keyOns} voices in the next 5 s");
                return;
            }
        }
        g.S.LoadState(state);
        r.Warn("no corruption of 24 seeds kept the driver keying notes: the corrupted game is recorded clean");
    }

    /// <summary>Nintendo's sound driver (N-SPC, Super Mario World): its sequencer ticks each time a tempo byte, added to an 8-bit accumulator every period of the SPC700's timer 0, carries. The timer's period is
    /// read from the APU (its target register x 125 us); the tempo byte is the byte of audio RAM page 0 whose value predicts the measured burst period best. Null when no byte fits within 0.3 %.</summary>
    private static (double Period, int TempoByte, int At, double SeqTick)? NintendoTick(RecGame g, double burst)
    {
        var f = typeof(NesEmulator.Snes.APU_SFC).GetField("timerTarget", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (f?.GetValue(g.Spc) is not byte[] targets || targets[0] == 0) return null;
        double period = targets[0] * 0.000125;
        int want = (int)Math.Round(period * 256 / burst);
        if (want is < 1 or > 255) return null;
        double seq = period * 256 / want;
        if (Math.Abs(seq / burst - 1) > 0.003) return null;
        for (int a = 0; a < 0x100; a++) if (g.Spc.Aram[a] == want) return (period, want, a, seq);
        return null;
    }

    /// <summary>The driver's tick: the S-DSP writes come in bursts at its timer; the period that aligns the burst starts best (2 to 20 ms).</summary>
    public static double DriverTick(RecSession s)
    {
        var bursts = new List<double>();
        uint last = uint.MaxValue;
        for (int i = 0; i < s.Count; i++) { if (s.RegVals[i] >> 8 == RecSession.MarkerVersion) continue; uint t = s.Times[i]; if (last == uint.MaxValue || t - last > 16) bursts.Add(t / 32000.0); last = t; }
        var w = bursts.Select(_ => 1.0).ToArray();
        return RecTempo.BestPeriod(bursts.ToArray(), w, 0.005, 0.04, out _);       // (above the 2 ms of Nintendo's timer itself: the sequencer's tick)
    }

    /// <summary>The strongest spectral peak of the track's preset played at $1000 (no tuning, a long note) by the preset engine: the sampler the plugin uses.</summary>
    private static double PresetFft(RecTrack t)
    {
        var rd = PresetFile.Read(t.PresetFile!);
        if (rd.Data == null) return double.NaN;
        var play = PresetPlayable.Build(rd.Data, out _);
        if (play?.Snes == null) return double.NaN;
        var sampler = new SnesSampler(play.Snes, 32000, 16384);
        sampler.NoteOnCents(0, t.Srcn, -t.Tune, 100, 100);       // (FL's cents minus the tuning: register $1000, the sample's own rate)
        var buf = new float[2 * 16384];
        sampler.Render(buf, 16384);
        var mono = new float[16384];
        for (int i = 0; i < mono.Length; i++) mono[i] = buf[2 * i] + buf[2 * i + 1];
        return InjectSpectrum.PeakHz(mono, 4096, 8192, 32000, 30, 8000, 1e-6);
    }

    // =====================================================================================================================================================================

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
            bool notRun = r.Assertions == 0 && r.Warnings.Any(w => w.StartsWith("NOT RUN"));
            string verdict = !o.Passed ? "FAIL" : notRun ? "NOT RUN" : r.Warnings.Count > 0 ? "WARN" : "PASS";
            log?.Invoke($"  {verdict,-7} {c.Id,-11} {r.Assertions,7} assertions  [{o.Seconds:0.0}s]" + (r.Notes.Count > 0 ? "\n        " + string.Join("\n        ", r.Notes) : "") + (r.Warnings.Count > 0 ? "\n        warning: " + string.Join("\n        warning: ", r.Warnings) : "") + (r.Failures.Count > 0 ? "\n        FAILED: " + string.Join("\n        FAILED: ", r.Failures.Take(10)) + (r.Failures.Count > 10 ? $"\n        ... and {r.Failures.Count - 10} more" : "") : ""));
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
            RecMutants.Active = m.Code;
            try { check.Run(r); }
            catch (Exception e) { r.Failures.Add($"exception {e.GetType().Name}: {e.Message}"); }
            finally { RecMutants.Active = 0; }
            bool caught = r.Failures.Count > 0;
            if (!caught) missed++;
            lines.Add($"  {(caught ? "caught" : "MISSED")}  [{m.CatchingCheck}] {m.Name}: {r.Failures.Count} failing assertions" + (caught ? $", e.g. {(r.Failures[0].Length > 200 ? r.Failures[0][..197] + "..." : r.Failures[0])}" : ""));
        }
        return (lines, missed);
    }

    /// <summary>`FruityHost record-dump smw|dkc|synth [seconds] [track]`: a recording's tempo map, its tracks, and one track's notes with their pitch values and the channel's events (a development aid).</summary>
    public static int Dump(string[] args)
    {
        string which = args.Length > 1 ? args[1] : "synth";
        double secs = args.Length > 2 ? double.Parse(args[2]) : 20;
        int track = args.Length > 3 ? int.Parse(args[3]) : 0;
        int park = args.Length > 4 ? int.Parse(args[4]) : which == "dkc" ? 1500 : 2200;
        RecSession s;
        if (which == "synth") { var d = new SynthDriver(132.5); var rec = new SpcRecorder(); rec.Arm(); for (int i = 0; i < 40; i++) rec.Feed(); d.Play(rec, secs); s = rec.Finish(d.Aram, d.Regs, "Synth", "synthetic", "", 0, 1); }
        else
        {
            using var g = which == "dkc" ? RecGame.Make(ImodelGames.Find(@"X:\EMULATION\Super Nintendo\Super Nintendo (U)", "Donkey Kong Country (U) (V1.2)", ".smc")!, park, null) : RecGame.Make(ImodelGames.Smw, park, InjectRun.SmwScript);
            if (g == null) { Console.WriteLine("no ROM"); return 2; }
            s = g.Record((int)(secs * 60), which);
        }
        var tr = RecTranscribe.Run(s, new RecOptions { Name = which });
        Console.WriteLine($"{s.Seconds:0.0} s, {s.Count} writes, {tr.Notes.Count} notes; tempo {tr.Tempo.Describe()}; driver tick {DriverTick(s) * 1000:0.000} ms");
        {
            // per window of 6 s: the onsets, the best grid and its share, by voice
            var (ot, ow) = RecTempo.Onsets(tr.Notes.Select(n => n.StartSeconds));
            for (double ws = 0; ws < s.Seconds; ws += 6)
            {
                int a = Array.FindIndex(ot, x => x >= ws), b = Array.FindIndex(ot, x => x >= ws + 6);
                if (a < 0) break; if (b < 0) b = ot.Length;
                var voices = tr.Notes.Where(n => n.StartSeconds >= ws && n.StartSeconds < ws + 6).GroupBy(n => n.Voice).OrderBy(g => g.Key).Select(g => $"v{g.Key}:{g.Count()}");
                if (b - a < 6) { Console.WriteLine($"  window {ws,4:0}-{ws + 6,4:0} s: {b - a} onsets ({string.Join(" ", voices)})"); continue; }
                double tat = RecTempo.BestPeriod(ot, ow, 0.06, 1.0, out double al, a, b);
                RecTempo.Alignment(ot, ow, tat, out double ph, a, b);
                Console.WriteLine($"  window {ws,4:0}-{ws + 6,4:0} s: {b - a} onsets, tatum {tat * 1000:0.00} ms ({tat / DriverTick(s):0.00} driver ticks), align {al:0.00}, on grid {RecTempo.OnGrid(ot, ow, tat, ph, a, b) * 100:0}%, beat {60 / RecTempo.Beat(ot, ow, tat, a, b):0.0} BPM ({string.Join(" ", voices)})");
                if (args.Contains("--phases"))
                {
                    // where the onsets fall in the tatum, in 8 ms bins, by voice
                    foreach (var vg in tr.Notes.Where(n => n.StartSeconds >= ws && n.StartSeconds < ws + 6).GroupBy(n => n.Voice).OrderBy(g => g.Key))
                        Console.WriteLine($"      v{vg.Key}: " + string.Join(" ", vg.Select(n => { double x = (n.StartSeconds - ph) / tat; return $"{(x - Math.Floor(x)) * tat * 1000:0}"; })));
                }
            }
            if (args.Contains("--aram")) { var e = s.End!.Aram; Console.WriteLine("  ARAM $00-$FF at the end: " + Convert.ToHexString(e.AsSpan(0, 256))); }
        }
        foreach (var m in tr.Map) Console.WriteLine($"  map: from {m.Seconds:0.0000} s tick {m.Tick:0.00} rate {m.TicksPerSecond:0.000}/s ({m.MicrosPerQuarter} us)");
        foreach (var seg in tr.Tempo.Segments) Console.WriteLine($"  segment: from {seg.Seconds:0.000} s {seg.Bpm:0.000} BPM downbeat {seg.DownbeatSeconds:0.000} tatum {seg.TatumSeconds * 1000:0.000} ms");
        foreach (var t in tr.Tracks) Console.WriteLine($"  track {t.Index + 1}: {t.Name}, {t.Notes.Count} notes, ch {t.Channel + 1}, range {t.BendRange}, tune {t.Tune}, {t.RootText}, exact {t.PitchExact}/{t.PitchValues}, shared {t.SharedValues}, disturbed {t.Disturbed}");
        foreach (var f in tr.Facts) Console.WriteLine("  fact: " + f);
        if (track < tr.Tracks.Count)
        {
            var t = tr.Tracks[track];
            foreach (var (ni, k) in t.Notes.Select((ni, k) => (ni, k)).Take(12))
            {
                var n = tr.Notes[ni];
                Console.WriteLine($"  note {k}: voice {n.Voice} {n.StartSeconds:0.0000}-{n.End / 32000.0:0.0000} s ticks {tr.TickOf(n.StartSeconds)}-{tr.TickOf(n.End / 32000.0)} key {t.Keys[k]} vel {t.Velocities[k]}: pitches " + string.Join(" ", n.Pitches.Take(10).Select(p => $"{p.T / 32000.0:0.0000}:${p.P:X4}")));
            }
            foreach (var e in t.Smf.Events.Where(e => (e.Status & 0xF0) is 0xE0 or 0x90 or 0x80).Take(40)) Console.WriteLine($"    ev tick {e.Tick} {(e.Status & 0xF0):X2} {e.Data1} {e.Data2} {((e.Status & 0xF0) == 0xE0 ? $"bend {(e.Data1 | e.Data2 << 7) - 8192}" : "")}");
        }
        return 0;
    }

    /// <summary>`FruityHost record-test [--only id,id] [--no-mutants] [--seconds n]`</summary>
    public static int Cli(string[] args)
    {
        string? only = null;
        for (int i = 0; i < args.Length - 1; i++) { if (args[i] == "--only") only = args[i + 1]; if (args[i] == "--seconds") GameSeconds = double.Parse(args[i + 1]); }
        bool mutants = !args.Contains("--no-mutants");
        Console.WriteLine("BrokenNes.FruityHost record-test (round 8: the recording session, the transcription, the MIDI file, the project section; no plugin DLL involved)");
        var results = RunChecks(only, Console.WriteLine);
        int failed = results.Count(o => !o.Passed), missed = 0;
        if (mutants)
        {
            Console.WriteLine("deliberate bugs (each must be caught by the check named):");
            var (lines, m) = RunMutants(only == null ? "root,tempo,capture,transcribe,smf,section,live,match" : only);
            foreach (var l in lines) Console.WriteLine(l);
            missed = m;
        }
        int notRun = results.Count(o => o.Report.Assertions == 0 && o.Report.Warnings.Any(w => w.StartsWith("NOT RUN")));
        Console.WriteLine($"{results.Count - failed - notRun} checks passed, {notRun} NOT RUN, {failed} failed, {results.Sum(o => o.Report.Assertions)} assertions; {missed} deliberate bugs missed");
        return failed == 0 && missed == 0 ? 0 : 1;
    }
}
