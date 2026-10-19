// Round 8 `reload` (2026-10-18): the user's report from FL 2026 (build r13): "after reloading the MIDI and replacing the FLEX/MIDI instruments with BrokenNes, despite relinking themselves, the right
// instruments do not play; in the case of the NES, that really ain't working out". The evidence is the host log of that FL session (Documents\BrokenNes2 host log.txt, copied with the recordings
// it used to %LOCALAPPDATA%\VRUN_Nes_Dev_work\fl-evidence\2026-10-18, or BROKENNES_FLREPLAY_DIR; never in the repository: a game's music), and it shows FL's real conventions (Record/FlNotes.cs).
// Through the real plugin DLL (Native AOT):
//   log-wario   THE LOG REPLAY: the Wario's Woods session as FL ran it. The recording offered from its own MIDI file and presets; the six channels FL made (#10-#15) created with the very Dispatcher
//               calls the log shows, in order (FL's creation sequence, its Play and Stop, focus, IO latency ...); their 32 logged notes each at its logged time with its logged pitch (cents from MIDI 0),
//               colour and velocity; no song position, no channel name (as FL gave none). Every channel must find its track (the one on its colour's MIDI channel) ON ITS FIRST NOTE, read FL's
//               pitch from MIDI 0, settle on PRE-NES holding its track's preset, identify every logged note as the recorded note it is (the MIDI file's note at that time since FL's Play), play
//               it with the recorded key and expression, and its own chip must write the instrument the preset carries (pulse 1 or 2 with the track's duty, the triangle, the noise mode).
//               The user's own clicks in that session (Instrument Runaway, Unlink, Link to recording) are not replayed: the claim is that no click is needed.
//   log-smw     The Super Mario World session of the same log: the seven channels FL made (#2-#8) created with their logged calls; FL played no note of it before the tracks were withdrawn, so
//               their notes are the MIDI file's, played as FL plays them (the log's conventions); each must find its track, settle on PRE-SNES, and its S-DSP must key the sample its preset holds.
//   nes-take    A NES game recorded here (VRUN, built in; Wario's Woods when the roster is there), its channels made by FL's creation sequence (they land on the game's emulator: silent until
//               their first note), played as FL 2026 plays an imported-then-replaced channel (pitch from MIDI 0, colour, no song position, bends at 2 semitones, no controller 7): each finds its
//               track on its first note, and every period, volume and duty the game held for 4 ms or more is in the registers its own chip wrote, from the start, from the middle, in a loop, and
//               on a channel made in FL (pitch from C5)
//   snes-take   Super Mario World recorded here (when its ROM is here): the same, register by register on the S-DSP
//   position    when FL does give a song position, the plugin reads it (FHD_GetMixingTime with offset 0, checked against an offset of one second)
// Each scenario is run again with deliberate bugs in the DLL (plugin hook 657), and must fail: the r13 matcher's reading (pitch from C5) fails the log replay, as it failed in FL.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using BrokenNes2.Record;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkPBase = 790, HkMBase = 791, HkMBaseSure = 792, HkNoteAt = 793, HkJumps = 794, HkUnexplained = 795, HkByColour = 796, HkAligned = 797, HkMSettled = 798, HkMHow = 799,
        HkOfferFiles = 800, HkNoteLog = 801, HkNoteLogRead = 802, HkNoteLogLost = 803, HkFollowing = 804, HkTransportOn = 805, HkSongPos = 806, HkCandidates = 807;

    /// <summary>The evidence of the user's FL session: the host log and the recordings it used, outside the repository.</summary>
    private static string FlEvidenceDir => Environment.GetEnvironmentVariable("BROKENNES_FLREPLAY_DIR") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRUN_Nes_Dev_work", "fl-evidence", "2026-10-18");

    private delegate void ReloadScenario(T t, string dir);
    private bool reloadMutantPass;

    private (string Name, ReloadScenario Body, int Mutant, string Why)[] ReloadScenarios() =>
    [
        ("log-wario", ReloadLogWario, RecMutants.OldPitchBase, "FL's pitch read from C5 (the r13 matcher: what the user's FL session got)"),
        ("log-wario", ReloadLogWario, RecMutants.ExpressionBySongPosition, "the recorded expression by FL's song position (r13: FL gives none, the notes play FL's own pitch and volume)"),
        ("log-smw", ReloadLogSmw, RecMutants.OldPitchBase, "FL's pitch read from C5 (the r13 matcher), on the SNES session"),
        ("nes-take", ReloadNesTake, RecMutants.FlVelocityForExpression, "a recorded note's level from FL's velocity (velocity / 128) instead of its own"),
        ("nes-take", ReloadNesTake, RecMutants.FollowByKeyOnly, "each note taken for the first recorded note of its key (no order, no time between notes)"),
        ("nes-take", ReloadNesTake, RecMutants.WaitingPlaysGame, "a linked channel waiting for its first note plays the game's channel"),
        ("snes-take", ReloadSnesTake, RecMutants.ExpressionBySongPosition, "the recorded expression by FL's song position (r13), on the SNES"),
        ("position", ReloadPosition, RecMutants.MixingTimeNaNOffset, "FHD_GetMixingTime asked with a NaN offset (r13: FL answered NaN)"),
    ];

    private void ReloadTest(T t)
    {
        string dir = Path.Combine(Path.GetTempPath(), "bn2_reload_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string presets = Path.Combine(dir, "presets");
        Directory.CreateDirectory(presets);
        string? oldPresets = Environment.GetEnvironmentVariable("BROKENNES_PRESETS_DIR");
        Environment.SetEnvironmentVariable("BROKENNES_PRESETS_DIR", presets);           // (the recordings' presets go into a scratch library, never the user's)
        using (var h = lib.Create()) { h.Test(120, 0, 1); h.Test(HkRTempo, 0, 120_000); h.Test(HkRMutant, 0, 0); h.Test(HkOfferSet, 0, 0); }
        try
        {
            foreach (var (name, body, _, _) in ReloadScenarios().DistinctBy(x => x.Name))
            {
                if (ScenarioFilter is { } only && !only.Contains(name)) continue;
                var tt = new T();
                try { body(tt, dir); } catch (Exception e) { tt.Failures.Add($"exception {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
                finally { using var h = lib.Create(); h.Test(HkOfferSet, 0, 0); }
                t.Failures.AddRange(tt.Failures.Select(f => $"{name}: {f}"));
                t.Warnings.AddRange(tt.Warnings.Select(f => $"{name}: {f}"));
                t.Info.AddRange(tt.Info.Select(f => $"{name}: {f}"));
            }
            int caught = 0, armed = 0;
            reloadMutantPass = true;
            foreach (var (name, body, mutant, why) in ReloadScenarios())
            {
                if (ScenarioFilter is { } only && (!FilterKeepsMutants || !only.Contains(name))) continue;
                armed++;
                var tt = new T();
                using (var probe = lib.Create()) probe.Test(HkRMutant, 0, mutant);
                try { body(tt, dir); } catch (Exception e) { tt.Failures.Add("exception " + e.Message); }
                finally { using var probe = lib.Create(); probe.Test(HkRMutant, 0, 0); probe.Test(HkOfferSet, 0, 0); }
                bool notRun = tt.Failures.Count == 0 && tt.Warnings.Any(w => w.StartsWith("NOT RUN"));
                if (notRun) { t.Warn(false, $"deliberate bug '{why}': {name} NOT RUN ({tt.Warnings.First(w => w.StartsWith("NOT RUN"))})"); continue; }
                if (tt.Failures.Count > 0) { caught++; t.Note($"deliberate bug '{why}' -> {name} failed: {(tt.Failures[0].Length > 240 ? tt.Failures[0][..237] + "..." : tt.Failures[0])}"); }
                else t.Expect(false, $"deliberate bug '{why}' was not caught by {name}: that check cannot fail");
            }
            t.Note($"DLL: {caught} of {armed} deliberate bugs caught");
        }
        finally
        {
            reloadMutantPass = false;
            using (var h = lib.Create()) { h.Test(120, 0, 0); h.Test(HkRMutant, 0, 0); h.Test(HkOfferSet, 0, 0); h.Test(HkRTempo, 0, 120_000); }
            Environment.SetEnvironmentVariable("BROKENNES_PRESETS_DIR", oldPresets);
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------------------------------------ the host log FL wrote

    private sealed class FlLogInstance
    {
        public int No;
        public long Created = -1;
        public bool Joined;
        public readonly List<(long Ms, int Id, long Index, long Value)> Calls = new();
        public readonly List<(long Ms, float Pitch, int Colour, float Velocity)> Notes = new();
    }

    /// <summary>The host log read back: per instance, its creation, the Dispatcher calls FL made, the notes (time, pitch as FL gave it, colour, velocity), as r13 wrote them.</summary>
    private static Dictionary<int, FlLogInstance> ReadFlLog(string path, out string header)
    {
        var map = new Dictionary<int, FlLogInstance>();
        header = "";
        var head = new Regex(@"^(\d\d):(\d\d):(\d\d)\.(\d\d\d) #(\d+) (.*)$");
        var call = new Regex(@"^Dispatcher id=(\d+) \([^)]*\) index=(-?\d+) value=(-?\d+)");
        // (r13's note line: "note #1: pitch 7100 cents from C5 (key 131), song position none beats (local none), colour 1, length -1 ticks, velocity 0.5"; reload's: "note #1: pitch 7100 (key 71 if from
        // MIDI 0, ...), colour 1 (MIDI channel 2), velocity 0.5 (MIDI 64), length -1 ticks, ...": both are read)
        var note = new Regex(@"^note #\d+: pitch (-?[\d.]+)\b.*?, colour (-?\d+)\b.*?, (?:length -?\d+ ticks, )?velocity ([\d.]+)");
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("====") && header.Length == 0) { header = line; continue; }
            var m = head.Match(line);
            if (!m.Success) continue;
            long ms = ((long.Parse(m.Groups[1].Value) * 60 + long.Parse(m.Groups[2].Value)) * 60 + long.Parse(m.Groups[3].Value)) * 1000 + long.Parse(m.Groups[4].Value);
            int no = int.Parse(m.Groups[5].Value);
            if (no == 0) continue;
            if (!map.TryGetValue(no, out var li)) map[no] = li = new FlLogInstance { No = no };
            string body = m.Groups[6].Value;
            if (body.StartsWith("created ")) li.Created = ms;
            else if (body.StartsWith("joined the recording")) li.Joined = true;
            else if (call.Match(body) is { Success: true } c) li.Calls.Add((ms, int.Parse(c.Groups[1].Value), long.Parse(c.Groups[2].Value), long.Parse(c.Groups[3].Value)));
            else if (note.Match(body) is { Success: true } n) li.Notes.Add((ms, float.Parse(n.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(n.Groups[2].Value), float.Parse(n.Groups[3].Value, CultureInfo.InvariantCulture)));
        }
        return map;
    }

    /// <summary>The Dispatcher ids whose values FL passes as pointers (the log holds addresses of FL's memory): replayed with a pointer to zeroed memory of our own (the plugin does not read them).</summary>
    private static bool PointerCall(int id) => id is 5 or 14 or 31 or 55;

    private static readonly nint junkMemory = (nint)NativeMemory.AllocZeroed(4096);

    /// <summary>An instance created the way FL 2026 created the channels of the user's session: the calls one of them received in its first 200 ms, in order (from the log when it is here, else the
    /// sequence it shows: block size, time signature, samples per tick, sends, playing, process mode, MIDI resolution, enabled, flush, idle time, IO latency, fit time, GUI colour, settings, window
    /// size, debug mode, focus, PluginLoaded, ...).</summary>
    private void FlCreate(PluginInstance q, FlLogInstance? model, int rate)
    {
        q.Dispatcher(Fpd.SetSampleRate, 0, rate);
        if (model != null)
        {
            foreach (var (ms, id, index, value) in model.Calls.Where(c => c.Ms - model.Created <= 200 && c.Id != Fpd.SetPlaying && c.Id != Fpd.SongPosChanged))
                q.Dispatcher(id, (nint)index, PointerCall(id) ? junkMemory : (nint)value);
            return;
        }
        foreach (var (id, value) in new (int, long)[] { (3, 512), (14, -1), (20, 1129579756), (20, 1129579756), (17, 0), (1, 3072), (50, 0), (55, -1), (11, 1), (2, 0), (21, 16), (30, 1422), (31, -1), (19, 0), (33, 0), (29, 0), (5, -1), (29, 0), (47, 0), (22, 1), (19, 0), (40, 0), (20, 1129579756), (47, 0) })
            q.Dispatcher(id, id == 30 ? 1070 : 0, PointerCall(id) ? junkMemory : (nint)value);
    }

    // ------------------------------------------------------------------------------------------------ the log replay

    private sealed class FlReplayed
    {
        public required int No;
        public required PluginInstance P;
        public required HostSim Host;
        public long BoundAtFirst = -2;
        public readonly List<(nint Handle, float Pitch, int Colour, long Ms, long PlayMs)> Notes = new();
        public SimVoice? Held;
        public long HeldUntil;
    }

    private void ReloadLogWario(T t, string dir)
    {
        string ev = FlEvidenceDir, logPath = Path.Combine(ev, "fl-host-log.txt");
        string midi = Path.Combine(ev, "recordings", "Wario's Woods 18.55.44-407ff715", "Wario's Woods 18.55.44.mid"), presets = Path.Combine(ev, "presets", "Wario's Woods 18.55.44");
        if (!File.Exists(logPath) || !File.Exists(midi) || !Directory.Exists(presets)) { t.Warn(false, $"NOT RUN: the evidence of the user's FL session is not at {ev} (BROKENNES_FLREPLAY_DIR)"); return; }
        var log = ReadFlLog(logPath, out string header);
        t.Expect(header.Contains("FL64.exe"), $"the log is not FL's: '{header}'");
        ReplayFlSession(t, log, [10, 11, 12, 13, 14, 15], midi, presets, nes: true, synthesize: false);
    }

    private void ReloadLogSmw(T t, string dir)
    {
        string ev = FlEvidenceDir, logPath = Path.Combine(ev, "fl-host-log.txt");
        string midi = Path.Combine(ev, "recordings", "Super Mario World 18.52.50-f2eaf920", "Super Mario World 18.52.50.mid"), presets = Path.Combine(ev, "presets", "Super Mario World 18.52.50");
        if (!File.Exists(logPath) || !File.Exists(midi) || !Directory.Exists(presets)) { t.Warn(false, $"NOT RUN: the evidence of the user's FL session is not at {ev} (BROKENNES_FLREPLAY_DIR)"); return; }
        var log = ReadFlLog(logPath, out _);
        ReplayFlSession(t, log, [2, 3, 4, 5, 6, 7, 8], midi, presets, nes: false, synthesize: true);
    }

    /// <summary>One session of the log replayed through the DLL (see the file's head). <paramref name="synthesize"/>: FL played no note of it: each instance gets the MIDI file's notes of the track
    /// on its position (the n-th channel FL made, the n-th track), as FL plays an imported channel (Record/FlNotes.cs).</summary>
    private void ReplayFlSession(T t, Dictionary<int, FlLogInstance> log, int[] numbers, string midiPath, string presetDir, bool nes, bool synthesize)
    {
        const int rate = 44100, block = 256;
        var made = new List<PluginInstance>();
        var hosts = new List<HostSim>();
        PluginInstance? src = null;
        try
        {
            var m = MidiReplay.Read(File.ReadAllBytes(midiPath));
            double bpm = 60e6 / m.Tempo[0].Us;
            // the recording, offered from its own files by an instance on emulator 6 (in FL the game ran on emulator #1)
            src = lib.Create(); made.Add(src);
            src.Dispatcher(Fpd.SetSampleRate, 0, rate);
            src.SetParam(PEmulator, 6);
            long offered;
            fixed (char* a = midiPath + "|" + presetDir + "\0") offered = src.Test(HkOfferFiles, 0, (nint)a);
            t.Expect(offered == 0, $"the recording was not offered from its files: {RText(src, 8)}");
            if (offered != 0) return;
            long until = Environment.TickCount64 + 30000;
            while (src.Test(HkOffer) <= 0 && Environment.TickCount64 < until) Thread.Sleep(10);
            int tracks = (int)src.Test(HkOffer);
            var names = RText(src, 5).Split(" | ");
            t.Expect(tracks == m.TrackNames.Count - 1 && names.Length == tracks, $"{tracks} tracks on offer ({string.Join(" / ", names)}), the file has {m.TrackNames.Count - 1}");
            if (tracks <= 0) return;
            src.Test(HkRTempo, 0, (nint)Math.Round(bpm * 1000));                    // (FL's tempo: the recordings were written at it, 130 BPM)
            var chanOfTrack = Enumerable.Range(0, tracks).Select(k => m.Notes.FirstOrDefault(n => n.Track == k + 1)?.Channel ?? -1).ToArray();

            // the events of these instances, in the log's order
            var events = new List<(long Ms, int Seq, int No, int Kind, int Id, long Index, long Value, float Pitch, int Colour, float Vel)>();
            int seq = 0;
            foreach (int no in numbers)
            {
                if (!log.TryGetValue(no, out var li) || li.Created < 0) { t.Expect(false, $"the log has no instance #{no}"); return; }
                events.Add((li.Created, seq++, no, 0, 0, 0, 0, 0, 0, 0));
                foreach (var c in li.Calls) events.Add((c.Ms, seq++, no, 1, c.Id, c.Index, c.Value, 0, 0, 0));
                if (!synthesize) foreach (var n in li.Notes) events.Add((n.Ms, seq++, no, 2, 0, 0, 0, n.Pitch, n.Colour, n.Velocity));
            }
            if (synthesize)
            {
                // FL never played this session's notes: Play 3 s after the last channel was made, each channel the next track's first 40 notes as FL plays an imported channel
                long play = numbers.Max(no => log[no].Created) + 3000;
                for (int i = 0; i < numbers.Length && i < tracks; i++)
                {
                    events.Add((play, seq++, numbers[i], 1, Fpd.SongPosChanged, 0, 0, 0, 0, 0));
                    events.Add((play, seq++, numbers[i], 1, Fpd.SetPlaying, 0, 1, 0, 0, 0));
                    foreach (var n in m.Notes.Where(x => x.Track == i + 1).Take(40))
                        events.Add((play + (long)Math.Round(m.Seconds(n.On) * 1000), seq++, numbers[i], 2, 0, 0, 0, FlNotes.Pitch(n.Key, FlNotes.BaseImported), n.Channel, FlNotes.FlVelocity(n.Velocity)));
                    events.Add((play + 30_000, seq++, numbers[i], 1, Fpd.SetPlaying, 0, 0, 0, 0, 0));
                }
            }
            events.Sort((a, b) => a.Ms != b.Ms ? a.Ms.CompareTo(b.Ms) : a.Seq.CompareTo(b.Seq));

            var inst = new Dictionary<int, FlReplayed>();
            var playStart = new Dictionary<int, long>();
            bool playing = false;
            long lastMs = events[0].Ms, sample = 0, virtualMs = 0;
            var buf = new float[block * 2];
            void Render(long target)
            {
                fixed (float* bp = buf)
                    while (sample < target)
                    {
                        foreach (var r in inst.Values)
                        {
                            if (r.Held != null && sample >= r.HeldUntil) { r.Host.ReleaseFromMixer(r.Held); r.Held = null; }
                            r.P.NewTick(); r.P.GenRender(bp, block);
                        }
                        sample += block;
                    }
            }
            foreach (var e in events)
            {
                long dt = e.Ms - lastMs;
                if (!playing) dt = Math.Min(dt, 250);                                   // (the time between FL's Stop and its next Play is not replayed in full)
                lastMs = e.Ms;
                virtualMs += dt;
                Render(virtualMs * rate / 1000 / block * block);                        // (each event at its own time: the blocks' rounding does not add up)
                switch (e.Kind)
                {
                    case 0:
                    {
                        var h = new HostSim { RootNote = FlNotes.BaseImported, SampleRate = rate, Tempo = bpm }; hosts.Add(h);
                        var q = lib.Create(h); made.Add(q);
                        q.Dispatcher(Fpd.SetSampleRate, 0, rate);
                        q.Test(HkNoteLog, 0, 512);
                        if (nes) q.Test(HkNProbe, 0, 400_000); else q.Test(HkRProbe, 0, 400_000);
                        inst[e.No] = new FlReplayed { No = e.No, P = q, Host = h };
                        break;
                    }
                    case 1:
                    {
                        var r = inst[e.No];
                        if (e.Id == Fpd.SetPlaying) { playing = e.Value != 0; if (playing) playStart[e.No] = e.Ms; }
                        r.P.Dispatcher(e.Id, (nint)e.Index, PointerCall(e.Id) ? junkMemory : (nint)e.Value);
                        break;
                    }
                    default:
                    {
                        var r = inst[e.No];
                        if (r.Held != null) { r.Host.ReleaseFromMixer(r.Held); r.Held = null; }
                        var v = r.Host.NewVoice(e.Colour, e.Vel);
                        ref var f = ref v.Params->FinalLevels;
                        f.Pitch = e.Pitch; f.Vol = e.Vel; f.Pan = 0;
                        v.Params->InitLevels = f;
                        v.Handle = r.P.TriggerVoice(v.Params, v.Tag);
                        if (r.BoundAtFirst == -2) r.BoundAtFirst = r.P.Test(HkTrackOf);
                        r.Notes.Add((v.Handle, e.Pitch, e.Colour, e.Ms, playStart.TryGetValue(e.No, out long ps) ? ps : e.Ms));
                        r.Held = v; r.HeldUntil = sample + rate / 5;
                        break;
                    }
                }
            }
            Render(sample + rate / 2);
            foreach (var r in inst.Values) if (r.Held != null) { r.Host.ReleaseFromMixer(r.Held); r.Held = null; }
            Render(sample + rate / 4);
            until = Environment.TickCount64 + 8000;
            while (inst.Values.Any(r => r.Notes.Count > 0 && r.P.Test(HkBindState) != 2) && Environment.TickCount64 < until) Thread.Sleep(20);

            // what each channel made of it
            int channels = 0, firstOk = 0, settled = 0, notesAll = 0, notesRight = 0, exprAll = 0, exprRight = 0, instrumentOk = 0, loopedAll = 0;
            var lines = new List<string>();
            foreach (var r in inst.Values.OrderBy(x => x.No))
            {
                if (r.Notes.Count == 0) { lines.Add($"#{r.No}: no note"); continue; }
                channels++;
                int colour = r.Notes[0].Colour;
                int want = Array.IndexOf(chanOfTrack, colour);
                long track = r.P.Test(HkTrackOf), state = r.P.Test(HkBindState), pbase = r.P.Test(HkPBase);
                bool presetOk = r.P.Test(342) == 1 && r.P.Test(352) == 1 && (!nes || r.P.Test(HkNTrackPreset) == 1);
                if (r.BoundAtFirst == want && want >= 0) firstOk++;
                bool ok = track == want && state == 2 && pbase == FlNotes.BaseImported && presetOk;
                if (ok) settled++;
                t.Expect(want >= 0 && r.BoundAtFirst == want && ok, $"#{r.No} (colour {colour}): bound on its first note to track {r.BoundAtFirst + 1}, now track {track + 1} (should be {want + 1}: {(want >= 0 ? names[want] : "no track on that channel")}), state {state} (2 = settled; the matcher: settled {r.P.Test(HkMSettled)}, candidates {r.P.Test(HkCandidates)}, how {r.P.Test(HkMHow)}, jumps {r.P.Test(HkJumps)}, unexplained {r.P.Test(HkUnexplained)}), FL's pitch read from {pbase}, its preset {presetOk}: {DllText(r.P, HkTrackStatus)}");
                // every logged note: FL plays a channel's notes in order from the song's start at each Play (the log shows each Play's first note at its start) and, in pattern mode, starts the
                // pattern again at its end (the log shows the tracks' first notes again 11 s after a Play: FL LOOPED the imported pattern, with no word to the plugin), so the k-th note after a
                // Play is the track's (k mod notes)-th note; it must be the one the channel named (its tick), and be played with its recorded key. A logged note whose key is not that note's is
                // not judged (counted).
                var nlog = ReadNoteLog(r.P);
                var trackNotes = m.Notes.Where(n => n.Track == want + 1).OrderBy(n => n.On).ThenBy(n => n.Key).ToList();
                int right = 0, exprOk = 0, asked = 0, unjudged = 0, k = 0, looped = 0;
                long segment = long.MinValue;
                var wrongAt = new List<string>();
                foreach (var (h, pitch, _, ms, play) in r.Notes)
                {
                    if (play != segment) { segment = play; k = 0; }
                    int key = (int)Math.Round(pitch / 100.0);
                    var expected = trackNotes.Count > 0 ? trackNotes[k % trackNotes.Count] : null;
                    if (k >= trackNotes.Count) looped++;
                    k++;
                    if (expected == null || expected.Key != key) { unjudged++; if (wrongAt.Count < 3) wrongAt.Add($"note {k} after the Play at {(ms - play) / 1000.0:0.000} s has key {key}, the track's note {k} {(expected == null ? "none" : $"key {expected.Key} at {m.Seconds(expected.On):0.000} s")}"); continue; }
                    asked++;
                    if (!nlog.TryGetValue((int)(h & 0xFFFF), out var e)) continue;
                    if (e.Tick == (int)expected.On && want >= 0) right++;
                    else if (wrongAt.Count < 3) wrongAt.Add($"note {k} after the Play at {(ms - play) / 1000.0:0.000} s: tick {e.Tick} named, {expected.On} expected");
                    if (e.TrackKey == key) exprOk++;
                }
                notesAll += asked; notesRight += right; exprAll += asked; exprRight += exprOk; loopedAll += looped;
                t.Expect(asked > 0 && right == asked && exprOk == asked && unjudged == 0, $"#{r.No}: {right} of {asked} notes identified as the track's note FL played ({string.Join("; ", wrongAt)}), {exprOk} played with the recorded key and expression, {unjudged} logged notes not the track's next note ({looped} after FL looped the pattern)");
                // the instrument its preset carries, as its own chip plays it
                string inst1 = nes ? NesInstrumentCheck(r.P, names[Math.Max(0, want)], out bool iok) : SnesInstrumentCheck(r.P, names[Math.Max(0, want)], out iok);
                if (iok) instrumentOk++;
                t.Expect(iok, $"#{r.No} ({names[Math.Max(0, want)]}): {inst1}");
                lines.Add($"#{r.No} colour {colour} -> track {track + 1} \"{names[Math.Max(0, want)]}\" on its first note, pitch from MIDI {pbase}, {right}/{asked} notes identified ({looped} after FL's loop), {inst1}");
            }
            t.Expect(channels == numbers.Length, $"{channels} of the {numbers.Length} channels of the session played notes");
            if (!reloadMutantPass) t.Note($"{(nes ? "Wario's Woods" : "Super Mario World")} log replay: {firstOk} of {channels} channels found their track on their FIRST note (by its colour and key), {settled} settled on {(nes ? "PRE-NES" : "PRE-SNES")} with the track's preset; {notesRight} of {notesAll} notes identified as the recorded note FL played ({loopedAll} of them after FL looped the pattern), {exprRight} played with the recorded key and expression; {instrumentOk} of {channels} chips play the preset's instrument | " + string.Join(" | ", lines));
        }
        finally
        {
            foreach (var q in made) q.Dispose();
            foreach (var h in hosts) h.Dispose();
            using var hh = lib.Create(); hh.Test(HkOfferSet, 0, 0);
        }
    }

    /// <summary>An instance's note log (hook 802): by the voice's handle, the recorded note the matcher named, the key played with the recorded expression, the recorded note's tick.</summary>
    private static Dictionary<int, (int RecNote, int InTrack, int TrackKey, int Track, int Base, int Flags, int Tick)> ReadNoteLog(PluginInstance p)
    {
        int count = (int)p.Test(HkNoteLogRead, 0, 0);
        var d = new Dictionary<int, (int, int, int, int, int, int, int)>();
        if (count <= 0) return d;
        var raw = new int[count * 8];
        int got;
        fixed (int* rp = raw) got = (int)p.Test(HkNoteLogRead, count, (nint)rp);
        for (int i = 0; i < got; i++) d[raw[i * 8]] = (raw[i * 8 + 1], raw[i * 8 + 2], raw[i * 8 + 3], raw[i * 8 + 4], raw[i * 8 + 5], raw[i * 8 + 6], raw[i * 8 + 7]);
        return d;
    }

    /// <summary>What a NES channel's own chip wrote (the NES probe) against the instrument its track's preset carries, from the track's name: "Pulse 1, 75%" = the pulse 1 registers with duty 3,
    /// "Triangle" = the triangle's, "Noise" / "Noise (short)" = the noise's with that mode. The words, and whether they agree.</summary>
    private static string NesInstrumentCheck(PluginInstance p, string trackName, out bool ok)
    {
        int count = (int)p.Test(HkNProbeRead, 0, 0);
        var raw = new byte[Math.Max(1, count) * 12];
        int got;
        fixed (byte* rp = raw) got = (int)p.Test(HkNProbeRead, count, (nint)rp);
        var regs = new List<(int Reg, int Val)>();
        for (int i = 0; i < got; i++) { int code = BitConverter.ToInt32(raw, i * 12 + 8); if (code >> 24 == 0) regs.Add((code >> 8 & 0xFF, code & 0xFF)); }
        var dm = Regex.Match(trackName, @"Pulse ([12]), (\d+)%");
        if (dm.Success)
        {
            int pulse = int.Parse(dm.Groups[1].Value) - 1, duty = dm.Groups[2].Value switch { "12" or "12.5" => 0, "25" => 1, "50" => 2, _ => 3 };
            int b = pulse * 4;
            var ctrl = regs.Where(x => x.Reg == b).ToList();
            bool others = regs.Any(x => x.Reg is >= 0 and <= 0x0F && x.Reg >> 2 != pulse);
            ok = ctrl.Count > 0 && ctrl.All(x => x.Val >> 6 == duty) && !others;
            return $"its chip wrote {ctrl.Count} times to ${0x4000 + b:X4}, duties {string.Join(",", ctrl.Select(x => x.Val >> 6).Distinct())} (the preset's: {duty}){(others ? ", and to another channel's registers" : "")}";
        }
        if (trackName.EndsWith("Triangle"))
        {
            ok = regs.Any(x => x.Reg is 0x0A or 0x0B) && regs.All(x => x.Reg is >= 8 and <= 0x0B or > 0x0F);
            return $"its chip wrote the triangle's registers ({regs.Count(x => x.Reg is >= 8 and <= 0x0B)} writes, {regs.Count(x => x.Reg is < 8 or (> 0x0B and <= 0x0F))} elsewhere)";
        }
        if (trackName.Contains("Noise"))
        {
            int mode = trackName.Contains("short") ? 1 : 0;
            var e = regs.Where(x => x.Reg == 0x0E).ToList();
            ok = e.Count > 0 && e.All(x => x.Val >> 7 == mode) && regs.All(x => x.Reg is >= 0x0C and <= 0x0F or > 0x0F);
            return $"its chip wrote the noise's registers, mode {string.Join(",", e.Select(x => x.Val >> 7).Distinct())} (the preset's: {mode})";
        }
        ok = false;
        return "a track name this check does not know: " + trackName;
    }

    /// <summary>What a SNES channel's own S-DSP keyed (the replay probe) against the sample its track's preset carries ("sample N" in the track's name): every voice it keyed took sample N.</summary>
    private static string SnesInstrumentCheck(PluginInstance p, string trackName, out bool ok)
    {
        var sm = Regex.Match(trackName, @"sample (\d+)");
        int want = sm.Success ? int.Parse(sm.Groups[1].Value) : -1;
        int count = (int)p.Test(HkRProbeRead, 0, 0);
        var raw = new byte[Math.Max(1, count) * 12];
        int got;
        fixed (byte* rp = raw) got = (int)p.Test(HkRProbeRead, count, (nint)rp);
        var srcn = new List<int>();
        int keyed = 0;
        var pendingVoice = new HashSet<int>();
        for (int i = 0; i < got; i++)
        {
            int code = BitConverter.ToInt32(raw, i * 12 + 8);
            if (code >> 24 == 1) { keyed++; pendingVoice.Add(code >> 16 & 0xFF); continue; }
            int reg = code >> 8 & 0x7F, val = code & 0xFF;
            if ((reg & 15) == 4 && pendingVoice.Remove(reg >> 4)) srcn.Add(val);
        }
        ok = want >= 0 && keyed > 0 && srcn.Count > 0 && srcn.All(x => x == want);
        return $"its S-DSP keyed {keyed} voices, with samples {string.Join(",", srcn.Distinct())} (the preset's: {want})";
    }

    // ------------------------------------------------------------------------------------------------ a NES take of our own, played as FL plays it

    private void ReloadNesTake(T t, string dir)
    {
        string ev = FlEvidenceDir;
        var log = File.Exists(Path.Combine(ev, "fl-host-log.txt")) ? ReadFlLog(Path.Combine(ev, "fl-host-log.txt"), out _) : null;
        var model = log != null && log.TryGetValue(11, out var li) ? li : null;               // (the calls FL made when it created #11 of the user's session)
        foreach (var (pick, park) in new[] { ("VRUN", 120), ("Wario's Woods", 240) })
        {
            if (reloadMutantPass && pick != "VRUN") continue;
            string? path = pick == "VRUN" ? null : ImodelGames.Find(NesRecGame.Roster, pick, ".nes");
            if (pick != "VRUN" && path == null) { t.Warn(false, $"NOT RUN: {pick} is not in {NesRecGame.Roster}"); continue; }
            var made = new List<PluginInstance>();
            var hostsMade = new List<HostSim>();
            using var g = NesRomGame(path, 3);
            try
            {
                DriveFrames(g, _ => 0, 0, park);
                var s = NesTake(t, g, 900, pick);
                if (s == null) return;
                long until = Environment.TickCount64 + 20000;
                while (g.Test(HkOffer) <= 0 && Environment.TickCount64 < until) Thread.Sleep(10);
                var tr = NesTranscribe.Run(s, new NesOptions { Name = s.Name, FlTempo = 120 });
                var m = MidiReplay.Read(RBytes(g, HkRMidi));
                g.Test(HkRTempo, 0, (nint)Math.Round(60e6 / m.Tempo[0].Us * 1000));
                var usable = Enumerable.Range(0, tr.Tracks.Count).Where(k => tr.Tracks[k].Channel != 4 && tr.Tracks[k].Notes.Count >= 4).ToList();
                if (usable.Count < 2) { t.Expect(false, $"{pick}: only {usable.Count} tracks with notes"); continue; }
                // FL's Replace: one channel per track, created as FL creates them; with Emulator = Auto they land on the game's emulator (#3), where they are silent until their first note
                var parts = new List<ReplacePart>();
                foreach (int k in usable)
                {
                    var hq = new HostSim(); hostsMade.Add(hq);
                    var q = lib.Create(hq); made.Add(q);
                    FlCreate(q, model, 48000);
                    q.Test(HkNProbe, 0, 1_000_000);
                    q.Test(HkNoteLog, 0, 4096);
                    parts.Add(Part($"{tr.Tracks[k].Name}", q, m, k, k));
                }
                // before FL's Play: a channel FL put on the game's emulator (FL's Auto emulator does when the game's emulator has free channels, as in the user's session) plays nothing while it
                // waits for its first note, not the game's channel; the game itself plays on (its own instance)
                var hw = new HostSim(); hostsMade.Add(hw);
                var waiting = lib.Create(hw); made.Add(waiting);
                FlCreate(waiting, model, 48000);
                waiting.SetParam(PEmulator, (int)g.Test(16));
                var buf = new float[512];
                float peak = 0, gamePeak = 0;
                fixed (float* bp = buf)
                    for (int b = 0; b < 120; b++)
                    {
                        g.NewTick(); g.GenRender(bp, 256); for (int i = 0; i < 512; i++) gamePeak = Math.Max(gamePeak, Math.Abs(buf[i]));
                        waiting.NewTick(); waiting.GenRender(bp, 256); for (int i = 0; i < 512; i++) peak = Math.Max(peak, Math.Abs(buf[i]));
                        foreach (var pt in parts) { pt.P.NewTick(); pt.P.GenRender(bp, 256); }
                    }
                int onGame = parts.Count(pt => pt.P.Test(16) == g.Test(16)) + (waiting.Test(16) == g.Test(16) ? 1 : 0);
                t.Expect(waiting.Test(16) == g.Test(16) && waiting.Test(HkLinkState) == 1 && gamePeak > 0.01 && peak == 0, $"{pick}: a channel FL put on the game's emulator (#{waiting.Test(16)}, the game's #{g.Test(16)}, linked {waiting.Test(HkLinkState)}) played peak {peak:0.000} while it waited for its first note (the game's own channel: {gamePeak:0.000}); it must be silent");
                long alloc = PlayReplace(parts, m);
                var traces = new List<string>(allocTrace);
                // pass 2: three more channels on three tracks: FL's Play from the middle, a loop of 8 beats, a channel made in FL (its pitch from C5)
                var extra = new List<ReplacePart>();
                var kinds = new[] { "from the middle", "a loop of 8 beats", "a channel made in FL" };
                for (int i = 0; i < 3 && i < usable.Count; i++)
                {
                    int k = usable[(i + 1) % usable.Count];
                    var hq = new HostSim(); hostsMade.Add(hq);
                    var q = lib.Create(hq); made.Add(q);
                    FlCreate(q, model, 48000);
                    q.Test(HkNProbe, 0, 1_000_000);
                    q.Test(HkNoteLog, 0, 4096);
                    var pt = Part($"{tr.Tracks[k].Name} ({kinds[i]})", q, m, k, k);
                    var on = pt.Notes.Select(n => n.On).ToList();
                    if (i == 0) pt.StartBeat = on[on.Count / 2] / (double)m.Ppq - 0.25;
                    if (i == 1) { pt.StartBeat = on[0] / (double)m.Ppq - 0.25; pt.LoopBeats = 8; }
                    if (i == 2) pt.RootNote = FlNotes.BaseC5;
                    extra.Add(pt);
                }
                alloc += PlayReplace(extra, m);
                traces.AddRange(allocTrace);
                until = Environment.TickCount64 + 8000;
                var all = parts.Concat(extra).ToList();
                while (all.Any(x => x.P.Test(HkBindState) != 2) && Environment.TickCount64 < until) Thread.Sleep(20);
                int firstOk = 0;
                var lines = new List<string>();
                foreach (var pt in all)
                {
                    long track = pt.P.Test(HkTrackOf), state = pt.P.Test(HkBindState), pbase = pt.P.Test(HkPBase);
                    bool first = pt.BoundAtFirst == pt.Expect;
                    if (first) firstOk++;
                    int wantBase = pt.RootNote;
                    t.Expect(track == pt.Expect && state == 2 && pbase == wantBase && pt.P.Test(HkNTrackPreset) == 1 && (first || pt.Notes[0].Channel == 0),
                        $"{pick}, {pt.Name}: track {track + 1} (should be {pt.Expect + 1}; on its first note {pt.BoundAtFirst + 1}), state {state}, FL's pitch read from {pbase} (should be {wantBase}): {DllText(pt.P, HkTrackStatus)}");
                    // the registers: every value the game held for 4 ms or more of the notes this channel played (from the middle and after the loop, the first two notes may be taken for another note
                    // of the same key: not compared)
                    var played = Enumerable.Range(0, tr.Tracks.Count).Select(_ => new NesReplay.Played()).ToList();
                    int count = (int)pt.P.Test(HkNProbeRead, 0, 0);
                    var raw = new byte[Math.Max(1, count) * 12];
                    int got;
                    fixed (byte* rp = raw) got = (int)pt.P.Test(HkNProbeRead, count, (nint)rp);
                    var skip = new HashSet<int>();
                    if (pt.StartBeat > 0 && pt.LoopBeats == 0) foreach (var h in pt.Handles.Take(2)) skip.Add((int)(h & 0xFFFF));
                    if (pt.LoopBeats > 0) { int wrap = Enumerable.Range(1, Math.Max(0, pt.TrackNote.Count - 1)).FirstOrDefault(i => pt.TrackNote[i] < pt.TrackNote[i - 1], -1); if (wrap > 0) foreach (var h in pt.Handles.Skip(wrap)) skip.Add((int)(h & 0xFFFF)); }   // (the second pass of a loop: compared below on its own)
                    var handleIdx = new Dictionary<int, int>();
                    for (int i = 0; i < pt.Handles.Count; i++) if (pt.TrackNote[i] >= 0 && !skip.Contains((int)(pt.Handles[i] & 0xFFFF))) handleIdx[(int)(pt.Handles[i] & 0xFFFF)] = pt.TrackNote[i];
                    for (int i = 0; i < got; i++)
                    {
                        long tt = BitConverter.ToInt64(raw, i * 12);
                        int code = BitConverter.ToInt32(raw, i * 12 + 8);
                        if (code >> 24 == 1) { if (handleIdx.TryGetValue(code & 0xFFFF, out int idx)) played[pt.Expect].Log.Add((tt, 1 << 24 | idx)); }
                        else played[pt.Expect].Log.Add((tt, code));
                    }
                    long loopEnd = pt.LoopBeats > 0 ? (long)Math.Round((pt.StartBeat + pt.LoopBeats) * m.Ppq) : long.MaxValue;
                    var trackOns = m.Notes.Where(n => n.Track == pt.Expect + 1).ToList();
                    var askedSet = handleIdx.Values.Where(q => q < trackOns.Count && trackOns[q].Off <= loopEnd).ToHashSet();          // (FL's loop cuts a note that crosses its end)
                    var only = new NesTranscription { Session = tr.Session, Notes = tr.Notes, Tracks = tr.Tracks.Select((x, i) => i == pt.Expect ? x : new NesTrack { Index = x.Index, Channel = x.Channel, Duty = x.Duty, Mode = x.Mode, Name = x.Name }).ToList(), Inexact = tr.Inexact };
                    var rep = new ImodelReport();
                    NesReplay.Compare(rep, $"{pick}, {pt.Name}", only, played, out string line, pt.StartBeat > 0 || pt.LoopBeats > 0 ? (k, q) => k == pt.Expect && askedSet.Contains(q) : null);
                    foreach (var f in rep.Failures.Take(3)) t.Expect(false, f);
                    lines.Add(line);
                    // the loop's second pass on its own: every note but the first two after the loop
                    if (pt.LoopBeats > 0)
                    {
                        int wrap = Enumerable.Range(1, Math.Max(0, pt.TrackNote.Count - 1)).FirstOrDefault(i => pt.TrackNote[i] < pt.TrackNote[i - 1], -1);
                        t.Expect(wrap > 0, $"{pick}, {pt.Name}: FL's loop never came back");
                        if (wrap <= 0) continue;
                        var second = Enumerable.Range(0, tr.Tracks.Count).Select(_ => new NesReplay.Played()).ToList();
                        var idx2 = new Dictionary<int, int>();
                        for (int i = wrap + 2; i < pt.Handles.Count; i++) if (pt.TrackNote[i] >= 0) idx2[(int)(pt.Handles[i] & 0xFFFF)] = pt.TrackNote[i];
                        for (int i = 0; i < got; i++)
                        {
                            long tt = BitConverter.ToInt64(raw, i * 12);
                            int code = BitConverter.ToInt32(raw, i * 12 + 8);
                            if (code >> 24 == 1) { if (idx2.TryGetValue(code & 0xFFFF, out int idx)) second[pt.Expect].Log.Add((tt, 1 << 24 | idx)); }
                            else second[pt.Expect].Log.Add((tt, code));
                        }
                        var asked2 = idx2.Values.Where(q => q < trackOns.Count && trackOns[q].Off <= loopEnd).ToHashSet();
                        var rep2 = new ImodelReport();
                        NesReplay.Compare(rep2, $"{pick}, {pt.Name}, after the loop", only, second, out string line2, (k, q) => k == pt.Expect && asked2.Contains(q));
                        foreach (var f in rep2.Failures.Take(3)) t.Expect(false, f);
                        lines.Add(line2);
                    }
                }
                t.Expect(alloc == 0, $"{pick}: {alloc:N0} bytes allocated on the audio thread while the channels found their tracks and played{(traces.Count > 0 ? ": " + string.Join(" / ", traces) : "")}");
                if (!reloadMutantPass) t.Note($"{pick} ({usable.Count} tracks, {s.Seconds:0.0} s), played as FL 2026 plays imported channels: {firstOk} of {all.Count} channels found their track on their first note; {onGame} waited on the game's emulator in silence | " + string.Join(" | ", lines));
            }
            finally { foreach (var q in made) q.Dispose(); foreach (var hq in hostsMade) hq.Dispose(); g.Test(HkOfferSet, 0, 0); g.Test(HkRForget); }
        }
    }

    // ------------------------------------------------------------------------------------------------ a SNES take of our own

    private void ReloadSnesTake(T t, string dir)
    {
        if (!File.Exists(ImodelGames.Smw)) { t.Warn(false, "NOT RUN: Super Mario World is not at " + ImodelGames.Smw + " (the synthetic SNES recording is played this way by `record`, scenario replace)"); return; }
        string ev = FlEvidenceDir;
        var log = File.Exists(Path.Combine(ev, "fl-host-log.txt")) ? ReadFlLog(Path.Combine(ev, "fl-host-log.txt"), out _) : null;
        var model = log != null && log.TryGetValue(3, out var li) ? li : null;
        var made = new List<PluginInstance>();
        var hostsMade = new List<HostSim>();
        using var g = SegaGame(ImodelGames.Smw, 2, "SNES");
        try
        {
            var pad = InjectRun.Pad(InjectRun.SmwScript);
            DriveFrames(g, pad, 0, 3800);
            g.Test(HkROptions, 0, ROptDefault);
            t.Expect(g.Test(HkRStart) == 0, "Record on Super Mario World was refused: " + RText(g, 8));
            DriveFrames(g, pad, 3800, 3800 + 1200);
            g.Test(HkRStop);
            if (!RWait(g, 4, 120000)) { t.Expect(false, $"the Super Mario World recording was not transcribed: {RText(g, 0)} {RText(g, 1)}"); return; }
            var s = RecSection.Read(RBytes(g, HkRSection), out var why);
            if (s == null) { t.Expect(false, "the recording's section does not read: " + why); return; }
            var tr = RecTranscribe.Run(s, new RecOptions { Name = s.Name, FlTempo = 120 });
            var m = MidiReplay.Read(RBytes(g, HkRMidi));
            g.Test(HkRTempo, 0, (nint)Math.Round(60e6 / m.Tempo[0].Us * 1000));
            long until = Environment.TickCount64 + 20000;
            while (g.Test(HkOffer) <= 0 && Environment.TickCount64 < until) Thread.Sleep(10);
            var parts = new List<ReplacePart>();
            for (int k = 0; k < tr.Tracks.Count; k++)
            {
                if (tr.Tracks[k].Notes.Count < 2) continue;
                var hq = new HostSim(); hostsMade.Add(hq);
                var q = lib.Create(hq); made.Add(q);
                FlCreate(q, model, 48000);
                q.Test(HkNoteLog, 0, 4096);
                var part = Part(tr.Tracks[k].Name, q, m, k, k);
                if (parts.Count % 2 == 1) { part.FlBends = 1; part.Name += " (FL lost its bends and volume)"; }
                parts.Add(part);
            }
            long alloc = PlayReplace(parts, m);
            // and the busiest track again, from the middle of the song
            int busiest = Enumerable.Range(0, tr.Tracks.Count).OrderByDescending(k => tr.Tracks[k].Notes.Count).First();
            var hm = new HostSim(); hostsMade.Add(hm);
            var qm = lib.Create(hm); made.Add(qm);
            FlCreate(qm, model, 48000);
            var mid = Part(tr.Tracks[busiest].Name + " (from the middle, FL's bends and volume lost)", qm, m, busiest, busiest);
            mid.FlBends = 1;
            mid.StartBeat = mid.Notes[mid.Notes.Count / 2].On / (double)m.Ppq - 0.25;
            alloc += PlayReplace([mid], m);
            until = Environment.TickCount64 + 8000;
            var all = parts.Append(mid).ToList();
            while (all.Any(x => x.P.Test(HkBindState) != 2) && Environment.TickCount64 < until) Thread.Sleep(20);
            int firstOk = 0;
            foreach (var pt in all)
            {
                long track = pt.P.Test(HkTrackOf), state = pt.P.Test(HkBindState);
                if (pt.BoundAtFirst == pt.Expect) firstOk++;
                t.Expect(track == pt.Expect && state == 2 && pt.P.Test(HkPBase) == 0 && (pt.BoundAtFirst == pt.Expect || pt.Notes[0].Channel == 0), $"{pt.Name}: track {track + 1} (should be {pt.Expect + 1}; on its first note {pt.BoundAtFirst + 1}), state {state}, base {pt.P.Test(HkPBase)}: {DllText(pt.P, HkTrackStatus)}");
                var pl = ToPlayer(pt);
                if (pt.StartBeat > 0) foreach (var h in pt.Handles.Take(2)) pl.NoteOfHandle.Remove((int)(h & 0xFFFF));
                CompareReplay(t, "Super Mario World, " + pt.Name, tr, [pl], [pt.Expect]);
            }
            t.Expect(alloc == 0, $"{alloc:N0} bytes allocated on the audio thread while the Super Mario World channels found their tracks and played");
            if (!reloadMutantPass) t.Note($"Super Mario World ({tr.Tracks.Count} tracks, 20 s) played as FL 2026 plays imported channels: {firstOk} of {all.Count} channels found their track on their first note");
        }
        finally { foreach (var q in made) q.Dispose(); foreach (var hq in hostsMade) hq.Dispose(); g.Test(HkOfferSet, 0, 0); g.Test(HkRForget); }
    }

    // ------------------------------------------------------------------------------------------------ FL's song position, when FL gives one

    private void ReloadPosition(T t, string dir)
    {
        var h = new HostSim { SampleRate = 48000, Tempo = 120 };
        var q = lib.Create(h);
        try
        {
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.Test(HkRTempo, 0, 120_000);
            long none = q.Test(HkSongPos);
            h.SongBeats = 12.5;
            long at = q.Test(HkSongPos);
            t.Expect(none == -1 && at == 12500 && h.MixingTimeCalls >= 4, $"FL's song position: none given -> {none} (should be -1), 12.5 beats given -> {at / 1000.0} ({h.MixingTimeCalls} calls of FHD_GetMixingTime)");
            if (!reloadMutantPass) t.Note($"FHD_GetMixingTime with offsets 0 and 1 s: no position when FL gives none, {at / 1000.0} beats when FL gives 12.5");
        }
        finally { q.Dispose(); h.Dispose(); }
    }

    /// <summary>`FruityHost alloc-gc-probe <dll> [blocks] [collections]`: does a collection in the DLL's runtime move the per-thread allocation counter of a thread that allocates nothing? One instance
    /// renders blocks on this thread (a note held, then none) while another thread makes the DLL collect; every block whose count moved is listed with the collections that ran meanwhile. A developer's
    /// aid for the `reload` / `record` allocation checks (their window must not count a collection's bookkeeping as an allocation).</summary>
    public static int AllocGcProbe(string[] args)
    {
        int blocks = args.Length > 2 && int.TryParse(args[2], out int b) ? b : 20000;
        int collections = args.Length > 3 && int.TryParse(args[3], out int c) ? c : 400;
        var lib = new PluginLibrary(args[1]);
        var h = new HostSim();
        var p = lib.Create(h);
        p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        var other = lib.Create(new HostSim());
        var buf = new float[128];
        var deltas = new long[blocks]; var gcs = new int[blocks];
        int moved = 0; long sum = 0;
        fixed (float* bp = buf)
        {
            for (int w = 0; w < 200; w++) { p.NewTick(); p.GenRender(bp, 64); }
            var v = h.FlNoteOn(60, 100, 0, 0, 1);
            v.Handle = p.TriggerVoice(v.Params, v.Tag);
            for (int w = 0; w < 200; w++) { p.NewTick(); p.GenRender(bp, 64); }
            bool stop = false;
            var collector = new Thread(() => { for (int i = 0; i < collections && !Volatile.Read(ref stop); i++) { other.Test(808); Thread.Sleep(2); } }) { IsBackground = true };
            long last = p.Test(HkRAlloc);
            int g0 = (int)p.Test(809);
            collector.Start();
            for (int i = 0; i < blocks; i++)
            {
                if (i == blocks / 2) h.ReleaseFromMixer(v);
                p.NewTick(); p.GenRender(bp, 64);
                long now = p.Test(HkRAlloc);
                int g = (int)p.Test(809);
                deltas[i] = now - last; gcs[i] = g - g0;
                last = now; g0 = g;
            }
            Volatile.Write(ref stop, true);
            collector.Join();
        }
        int collected = 0;
        for (int i = 0; i < blocks; i++) { collected += gcs[i]; if (deltas[i] != 0) { moved++; sum += deltas[i]; } }
        Console.WriteLine($"alloc-gc-probe: {blocks} blocks rendered on this thread, {collected} collections in the DLL's runtime meanwhile (made by another thread); {moved} blocks moved this thread's count, {sum} bytes in all");
        int shown = 0;
        for (int i = 0; i < blocks && shown < 30; i++)
            if (deltas[i] != 0) { shown++; Console.WriteLine($"  block {i}: {deltas[i]:+#;-#;0} bytes, {gcs[i]} collection(s) during it"); }
        p.Dispose(); other.Dispose(); h.Dispose();
        return moved == 0 ? 0 : 1;
    }
}
