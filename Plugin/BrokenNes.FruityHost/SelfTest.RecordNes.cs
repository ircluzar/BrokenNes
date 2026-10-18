// Round 8 (record-nes): recording a NES game's music and replaying it with no game, through the real plugin DLL (Native AOT). The library checks first (NesRecChecks: model, direct, capture,
// transcribe, smf, section, live, vrun-song, with their deliberate bugs, the real games' takes shortened), then the DLL:
//   flow       the Record window on VRUN (the built-in NES game) with real mouse messages: Record, the game runs, Stop, the transcription, its tracks listed; the DLL's MIDI file = the library's
//              transcription of the DLL's own session (deterministic)
//   capture    the capture while the audio thread pulls the game: 0 bytes allocated on that thread from Record to Stop, every write the bus reported kept (the DLL's own independent count)
//   replay     THE PROOF: the recording's MIDI file played the way FL would into one BrokenNes2 instance per track, each on the Presets chip with its track's preset (a recorded NES track), no game:
//              every value the game's chip held for 4 ms or more (period, volume, duty) is in the registers the instance's own chip was written (plugin hooks 740/741), the duty tracks of a note
//              that changed duty silent in turn, or the transcription says it is not exact; nothing allocated while they play; VRUN and (when the roster is here) Base Wars (duty changes, DMC)
//   project    REC1 version 2: the project's recording transcribed again to the same MIDI bytes; a damaged one refused
//   replace    FL's import, then Replace, with a NES recording: untouched instances (an empty state, a fresh one's state, one made before the offer) recognise their NES tracks from their notes
//              and settle on the tracks' emulator (Presets chip, a NES preset of their track); one whose FL channel lost the bends and the volume still writes the game's registers (the recording's
//              expression); Play here by mouse; TRK1 reopened
//   live       live MIDI out of VRUN through MIDIOut_Delayed beside a recording of the same frames: every transcribed note a note-on at the same key; switched off, every note ends; 0 bytes
//   refusals   Record in Direct mode, a Game Boy game; a DMC track's preset refused with the reason
// Each scenario with a deliberate bug is run again with it switched on in the DLL (plugin hook 657) and must fail.
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using BrokenNes2.Record;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkNProbe = 740, HkNProbeRead = 741, HkNProbeLost = 742, HkNTrackPreset = 743, HkNPayload = 744, HkNCount = 745, HkNDropped = 746, HkNTap = 747;

    /// <summary>An instance on <paramref name="emulator"/> in ROM mode with the NES game at <paramref name="path"/> (null: the built-in VRUN), loaded, no frame run.</summary>
    private PluginInstance NesRomGame(string? path, int emulator = 3)
    {
        PluginInstance p;
        if (path != null) { p = SegaGame(path, 0, "FIX"); return p; }
        p = lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        p.SetParam(PEmulator, emulator);
        p.SetParam(PConsole, 0);
        p.SetParam(PMode, 1);
        WaitForRom(p);
        return p;
    }

    private delegate void NesScenario(T t, string dir);
    private bool nesMutantPass;

    private (string Name, NesScenario Body, int Mutant, string Why)[] NesScenarios() =>
    [
        ("flow", NesFlow, 0, ""),
        ("capture", NesCapture, NesRecMutants.CaptureAllocates, "the capture allocates at every fourth-register write"),
        ("replay", NesReplayScenario, NesTranscribeMutants.TrackBehaviourDropped, "the track presets play as plain NES presets (their own volume, velocity as a gain)"),
        ("replay", NesReplayScenario, NesTranscribeMutants.DutyDropped, "the duty tracks' presets all at 50%"),
        ("project", NesProject, RecMutants.SectionCrcNotChecked, "the project section's checksum is not checked"),
        ("replace", NesReplace, RecMutants.ExpressionIgnored, "track channels ignore the recording's bends and volume"),
        ("live", NesLiveScenario, NesLiveMutants.Silent, "live MIDI out sends nothing"),
        ("refusals", NesRefusals, 0, ""),
    ];

    private void RecordNesTest(T t)
    {
        double keep = NesRecChecks.GameSeconds;
        NesRecChecks.GameSeconds = 10;
        try
        {
            foreach (var o in NesRecChecks.RunChecks("model,direct,capture,transcribe,smf,section,live,vrun-song,match"))
            {
                foreach (var f in o.Report.Failures.Take(3)) t.Expect(false, $"[{o.Id}] {f}");
                foreach (var w in o.Report.Warnings.Where(w => !w.Contains("ternary"))) t.Warn(false, $"[{o.Id}] {w}");
                t.Note($"library {o.Id}: {o.Report.Assertions} assertions passed");
            }
            var (lines, missed) = NesRecChecks.RunMutants(null);
            t.Expect(missed == 0, $"{missed} of the library's deliberate bugs were not caught: " + string.Join(" / ", lines.Where(l => l.Contains("MISSED"))));
            t.Note($"library: {lines.Count - missed} of {lines.Count} deliberate bugs caught");
        }
        finally { NesRecChecks.GameSeconds = keep; }

        string dir = Path.Combine(Path.GetTempPath(), "bn2_recnes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string presets = Path.Combine(dir, "presets");
        Directory.CreateDirectory(presets);
        string? oldPresets = Environment.GetEnvironmentVariable("BROKENNES_PRESETS_DIR");
        Environment.SetEnvironmentVariable("BROKENNES_PRESETS_DIR", presets);           // (the recording's presets go into a scratch library, never the user's)
        using (var h = lib.Create()) { h.Test(120, 0, 1); h.Test(HkRTempo, 0, 120_000); h.Test(HkRMutant, 0, 0); }
        try
        {
            foreach (var (name, body, _, _) in NesScenarios().DistinctBy(x => x.Name))
            {
                var tt = new T();
                RunNesScenario(name, body, tt, dir);
                t.Failures.AddRange(tt.Failures.Select(f => $"{name}: {f}"));
                t.Warnings.AddRange(tt.Warnings.Select(f => $"{name}: {f}"));
                t.Info.AddRange(tt.Info.Select(f => $"{name}: {f}"));
            }
            int caught = 0, armed = 0;
            nesMutantPass = true;
            foreach (var (name, body, mutant, why) in NesScenarios())
            {
                if (mutant == 0) continue;
                armed++;
                var tt = new T();
                using (var probe = lib.Create()) probe.Test(HkRMutant, 0, mutant);
                nesMutantNow = mutant;
                try { RunNesScenario(name, body, tt, dir); }
                finally { nesMutantNow = 0; using var probe = lib.Create(); probe.Test(HkRMutant, 0, 0); probe.Test(HkOfferSet, 0, 0); }
                if (tt.Failures.Count > 0) { caught++; t.Note($"deliberate bug '{why}' -> {name} failed: {(tt.Failures[0].Length > 220 ? tt.Failures[0][..217] + "..." : tt.Failures[0])}"); }
                else t.Expect(false, $"deliberate bug '{why}' was not caught by {name}: that check cannot fail");
            }
            t.Note($"DLL: {caught} of {armed} deliberate bugs caught");
        }
        finally
        {
            nesMutantPass = false;
            using (var h = lib.Create()) { h.Test(120, 0, 0); h.Test(HkRMutant, 0, 0); h.Test(HkOfferSet, 0, 0); }
            Environment.SetEnvironmentVariable("BROKENNES_PRESETS_DIR", oldPresets);
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void RunNesScenario(string name, NesScenario body, T tt, string dir)
    {
        void Body()
        {
            try { body(tt, dir); }
            catch (Exception e) { tt.Failures.Add($"exception {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
        }
        if (name != "flow") { Body(); return; }
        var th = new Thread(Body) { Name = "record-nes flow (STA)" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join();
    }

    /// <summary>Records <paramref name="frames"/> frames of the instance's NES game through the hooks; the session (from the DLL's own payload) or null with a failure.</summary>
    private NesSession? NesTake(T t, PluginInstance p, int frames, string label)
    {
        p.Test(HkROptions, 0, ROptDefault);
        t.Expect(p.Test(HkRStart) == 0, $"{label}: Record was refused: {RText(p, 8)}");
        for (int f = 0; f < frames; f += 30) p.Test(HkRun, 0, Math.Min(30, frames - f));
        p.Test(HkRStop);
        if (!RWait(p, 4, 120000)) { t.Expect(false, $"{label}: the recording was not transcribed (state {p.Test(HkRState)}: {RText(p, 0)} / {RText(p, 1)})"); return null; }
        var s = NesSession.FromPayload(RBytes(p, HkNPayload), out var why);
        t.Expect(s != null, $"{label}: the DLL's NES session does not read: {why}");
        return s;
    }

    // ------------------------------------------------------------------------------------------------ the window, by mouse

    private void NesFlow(T t, string dir)
    {
        using var p = NesRomGame(null, 3);
        nint parent = Win32Host.CreateParent();
        try
        {
            p.Test(HkROptions, 0, ROptDefault);
            p.ShowEditor(parent);
            for (int k = 0; k < 3; k++) Win32Host.Pump(40);
            ClickControl(t, p, IdRecBtn, "Record button");
            t.Expect(p.Test(EdROpen) == 1, "the Record window did not open");
            ClickControl(t, p, IdRecStartBtn, "Record");
            t.Expect(p.Test(HkRState) is 1 or 2, $"Record on VRUN: state {p.Test(HkRState)} ({EdText(p, EdRMessage)}; {RText(p, 8)})");
            p.Test(HkRun, 0, 900);                                          // 15 s of VRUN's title music
            ClickControl(t, p, IdRecStopBtn, "Stop");
            t.Expect(RWait(p, 4), $"the NES recording was not transcribed (state {p.Test(HkRState)}: {RText(p, 0)} / {RText(p, 1)})");
            if (p.Test(HkRState) != 4) return;
            int tracks = (int)p.Test(HkRTracks);
            string names = RText(p, 5), facts = RText(p, 2);
            t.Expect(tracks >= 3 && names.Contains("Pulse 1") && names.Contains("Triangle") && names.Contains("Noise") && File.Exists(RText(p, 3)), $"VRUN's recording: {tracks} tracks ({names}), file '{RText(p, 3)}'");
            var s = NesSession.FromPayload(RBytes(p, HkNPayload), out var why);
            t.Expect(s != null, "the DLL's session does not read: " + why);
            if (s != null)
            {
                var tr = NesTranscribe.Run(s, new NesOptions { Name = s.Name, FlTempo = 120 });
                t.Expect(RBytes(p, HkRMidi).AsSpan().SequenceEqual(tr.Midi), "the DLL's MIDI file differs from the library's transcription of the DLL's own session");
            }
            t.Note($"VRUN through the window: {tracks} tracks ({names}); {facts}");
            ClickControl(t, p, IdRecCloseBtn, "Close");
            p.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); p.Test(HkRForget); }
    }

    // ------------------------------------------------------------------------------------------------ the capture on the audio thread

    private void NesCapture(T t, string dir)
    {
        using var p = NesRomGame(null, 3);
        try
        {
            p.SetParam(PChannel, 5);                                              // (Mix: the audio thread pulls the game frame by frame, which is where the capture runs)
            var buf = new float[512];
            void Render(int blocks) { fixed (float* f = buf) for (int i = 0; i < blocks; i++) { p.NewTick(); p.GenRender(f, 256); } }
            Render(200);                                                           // (the game started)
            p.Test(HkROptions, 0, ROptDefault);
            t.Expect(p.Test(HkRStart) == 0, "Record was refused: " + RText(p, 8));
            long a0 = p.Test(HkRAlloc);
            Render(1800);                                                          // about 9.6 s at 48 kHz
            long a1 = p.Test(HkRAlloc);
            bool recording = p.Test(HkRState) == 2;
            long tap = p.Test(HkNTap);
            p.Test(HkRunaway, 0, 1);
            p.Test(HkRStop);
            bool done = RWait(p, 4);
            p.Test(HkRunaway, 0, 0);
            t.Expect(recording && done, $"the NES capture did not run or end (recording {recording}, state {p.Test(HkRState)}: {RText(p, 1)})");
            t.Expect(a1 - a0 == 0, $"{a1 - a0:N0} bytes allocated on the audio thread while the NES game was recorded (1,800 blocks)");
            var s = NesSession.FromPayload(RBytes(p, HkNPayload), out var why);
            t.Expect(s != null, "the session does not read: " + why);
            if (s == null) return;
            long writes = s.RegVals.Count(v => v >> 8 <= 0x17);
            t.Expect(writes == tap && s.Dropped == 0 && s.BaseExact, $"the capture holds {writes:N0} writes, the DLL's tap counted {tap:N0}; {s.Dropped} lost; starting state the chip's own: {s.BaseExact}");
            t.Note($"capture: {s.Seconds:0.0} s, {writes:N0} writes (the tap's count: {tap:N0}), {a1 - a0} bytes allocated on the audio thread over 1,800 blocks");
        }
        finally { p.Test(HkRunaway, 0, 0); p.Test(HkRForget); }
    }

    // ------------------------------------------------------------------------------------------------ the proof: the MIDI file played into track presets, register by register

    /// <summary>Plays a NES recording's MIDI file the way FL would into one instance per track on emulator 7 (the Presets chip, the track's preset); returns each track's writes and marks (by note index),
    /// the bytes allocated on the audio thread, and a problem.</summary>
    private (List<NesReplay.Played> Played, long Alloc, string? Problem) PlayNesTracks(byte[] midi, NesTranscription tr, IReadOnlyList<byte[]> presetFiles, List<PluginInstance> made)
    {
        var m = MidiReplay.Read(midi);
        const int rate = 48000, block = 64;
        var played = new List<NesReplay.Played>();
        var inst = new List<PluginInstance?>();
        var notes = new List<List<MidiReplay.Note>>();
        var chans = new List<(long[] BT, int[] BV, long[] CT, int[] CV, int Range)>();
        for (int k = 0; k < presetFiles.Count; k++)
        {
            played.Add(new NesReplay.Played());
            var tn = m.Notes.Where(n => n.Track == k + 1).OrderBy(n => n.On).ToList();
            notes.Add(tn);
            var chan = tn.Count > 0 ? m.Of(k + 1, tn[0].Channel) : new MidiReplay.Chan();
            chans.Add((chan.Bends.Select(b => b.Tick).ToArray(), chan.Bends.Select(b => b.Bend).ToArray(), chan.Cc7.Select(c => c.Tick).ToArray(), chan.Cc7.Select(c => c.Cc).ToArray(), chan.Range >= 0 ? chan.Range : 2));
            if (tr.Tracks[k].Channel == 4) { inst.Add(null); continue; }       // (the DMC: refused by its preset, checked in "refusals")
            var q = lib.Create();
            made.Add(q);
            q.Dispatcher(Fpd.SetSampleRate, 0, rate);
            q.SetParam(PEmulator, 7);
            var file = presetFiles[k];
            long r;
            fixed (byte* fp = file) r = file.Length == 0 ? 1 : q.Test(340, file.Length, (nint)fp);
            if (r != 0) return (played, 0, $"track {k + 1}'s preset was not loaded: {(file.Length == 0 ? "none" : PText(q))}");
            if (q.Test(HkNTrackPreset) != 1 && !RecMutantOn(NesTranscribeMutants.TrackBehaviourDropped)) return (played, 0, $"track {k + 1}'s preset does not play as a recorded track ({q.Test(HkNTrackPreset)})");
            q.Test(HkNProbe, 0, 2_000_000);
            inst.Add(q);
        }
        double end = m.Notes.Count == 0 ? 1 : m.Seconds(m.Notes.Max(n => n.Off)) + 0.3;
        var buf = new float[block * 2];
        var active = new List<(int K, int Idx, SimVoice V)>();
        var next = new int[presetFiles.Count];
        var handleOf = new List<Dictionary<int, int>>();
        for (int k = 0; k < presetFiles.Count; k++) handleOf.Add(new());
        long a0 = -1, a1 = -1;
        var live = inst.Where(x => x != null).Cast<PluginInstance>().ToList();
        if (live.Count == 0) return (played, 0, "no track can be played");
        fixed (float* bp = buf)
        {
            for (long s = 0; s < (long)(end * rate); s += block)
            {
                long tick = m.TickAt(s / (double)rate);
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    var (k, idx, v) = active[i];
                    if (notes[k][idx].Off <= tick) { inst[k]!.Host.ReleaseFromMixer(v); active.RemoveAt(i); }
                }
                for (int k = 0; k < presetFiles.Count; k++)
                {
                    if (inst[k] is not { } q) continue;
                    while (next[k] < notes[k].Count && notes[k][next[k]].On <= tick)
                    {
                        int idx = next[k]++;
                        var n = notes[k][idx];
                        var v = q.Host.NewVoice(0, n.Velocity / 127f);
                        Lv(v, n, chans[k], tick);
                        v.Handle = q.TriggerVoice(v.Params, v.Tag);
                        handleOf[k][(int)(v.Handle & 0xFFFF)] = idx;
                        active.Add((k, idx, v));
                    }
                }
                foreach (var (k, idx, v) in active) Lv(v, notes[k][idx], chans[k], tick);
                if (a0 < 0 && s >= rate / 2) a0 = live[0].Test(HkRAlloc);
                foreach (var q in live) { q.NewTick(); q.GenRender(bp, block); }
            }
            a1 = live[0].Test(HkRAlloc);
        }
        for (int k = 0; k < presetFiles.Count; k++)
        {
            if (inst[k] is not { } q) continue;
            if (q.Test(HkNProbeLost) > 0) return (played, 0, $"track {k + 1}'s probe overflowed");
            int count = (int)q.Test(HkNProbeRead, 0, 0);
            var raw = new byte[Math.Max(1, count) * 12];
            int got;
            fixed (byte* rp = raw) got = (int)q.Test(HkNProbeRead, count, (nint)rp);
            for (int i = 0; i < got; i++)
            {
                long tt = BitConverter.ToInt64(raw, i * 12);
                int code = BitConverter.ToInt32(raw, i * 12 + 8);
                if (code >> 24 == 1) { if (handleOf[k].TryGetValue(code & 0xFFFF, out int idx)) played[k].Log.Add((tt, 1 << 24 | idx)); }
                else played[k].Log.Add((tt, code));
            }
            played[k].Notes = notes[k].Count;
        }
        return (played, a0 < 0 ? 0 : a1 - a0, null);

        static void Lv(SimVoice v, MidiReplay.Note n, (long[] BT, int[] BV, long[] CT, int[] CV, int Range) c, long tick)
        {
            int bend = ValueAt(c.BT, c.BV, tick, 0), cc = ValueAt(c.CT, c.CV, tick, 100);
            ref var f = ref v.Params->FinalLevels;
            f.Pitch = (float)((n.Key - 60) * 100 + bend * c.Range * 100.0 / 8192.0);
            f.Vol = (float)(n.Velocity / 127.0 * cc / 100.0);
            f.Pan = 0;
            v.Params->InitLevels = f;
        }
    }

    private int nesMutantNow;
    private bool RecMutantOn(int code) => nesMutantNow == code;

    private void NesReplayScenario(T t, string dir)
    {
        foreach (var (pick, script, park, seconds) in new[] { ("VRUN", "", 120, 15.0), ("Base Wars", "300:start,4", 300, 12.0) })
        {
            string? path = pick == "VRUN" ? null : ImodelGames.Find(NesRecGame.Roster, pick, ".nes");
            if (pick != "VRUN" && path == null) { t.Warn(false, $"NOT RUN: {pick} is not in {NesRecGame.Roster}"); continue; }
            var made = new List<PluginInstance>();
            using var g = NesRomGame(path, 3);
            try
            {
                var pad = script.Length > 0 ? NesRecGame.Script(script) : (_ => 0);
                DriveFrames(g, pad, 0, park);
                var s = NesTake(t, g, (int)(seconds * 60), pick);
                if (s == null) return;
                var tr = NesTranscribe.Run(s, new NesOptions { Name = s.Name, FlTempo = 120 });
                var midi = RBytes(g, HkRMidi);
                t.Expect(midi.AsSpan().SequenceEqual(tr.Midi), $"{pick}: the DLL's MIDI file differs from the library's transcription of its session");
                var presets = Enumerable.Range(0, tr.Tracks.Count).Select(k => RBytes(g, HkRPreset, (long)k << 24)).ToList();
                var (played, alloc, problem) = PlayNesTracks(midi, tr, presets, made);
                t.Expect(problem == null, $"{pick}: {problem}");
                if (problem != null) return;
                var rep = new ImodelReport();
                NesReplay.Compare(rep, pick + " (through the DLL)", tr, played, out string line);
                foreach (var f in rep.Failures.Take(6)) t.Expect(false, f);
                t.Expect(alloc == 0, $"{pick}: {alloc:N0} bytes allocated on the audio thread while {played.Count} track instances played the recording");
                t.Note(line + $"; {alloc} bytes allocated on the audio thread");
            }
            finally { foreach (var q in made) q.Dispose(); g.Test(HkRForget); }
        }
    }

    // ------------------------------------------------------------------------------------------------ the project

    private void NesProject(T t, string dir)
    {
        byte[] section;
        NesSession? s;
        using (var g = NesRomGame(null, 3))
        {
            DriveFrames(g, _ => 0, 0, 60);
            s = NesTake(t, g, 600, "VRUN");
            section = RBytes(g, HkRSection);
            g.Test(HkRForget);
        }
        if (s == null) return;
        t.Expect(section.Length > 16 && BitConverter.ToUInt16(section, 0) == 2, $"the NES recording's REC1 is not version 2 ({(section.Length > 2 ? BitConverter.ToUInt16(section, 0) : -1)})");
        var tr = NesTranscribe.Run(s, new NesOptions { Name = s.Name, FlTempo = 120 });
        using (var p = Adopted(t, section))
        {
            if (p == null) return;
            t.Expect(RBytes(p, HkRMidi).AsSpan().SequenceEqual(tr.Midi), "the project's NES recording transcribes to another MIDI file");
            // the project round trip: saved, reopened in a fresh instance
            var state = p.SaveState();
            p.Test(HkRForget);
            int at = FindSection(state, RecSection.Tag);
            t.Expect(at >= 0, "the project has no REC1");
            using var q = lib.Create();
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            p.Dispose();
            q.LoadState(state);
            long until = Environment.TickCount64 + 60000;
            while (q.Test(HkRState) is 3 or 0 && Environment.TickCount64 < until) Win32Host.Pump(10);
            t.Expect(q.Test(HkRState) == 4 && RBytes(q, HkRMidi).AsSpan().SequenceEqual(tr.Midi), $"the reopened project's NES recording: state {q.Test(HkRState)}, {RText(q, 7)}");
            q.Test(HkRForget);
        }
        // damaged: a byte of the stored data flipped (with the checksum left as it was)
        var bad = (byte[])section.Clone();
        bad[RecSection.HeaderBytes + 20] ^= 0x10;
        using (var p2 = lib.Create())
        {
            p2.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            p2.SetParam(PEmulator, 6);
            p2.Test(HkRForget);
            fixed (byte* sp = bad) p2.Test(HkRAdopt, bad.Length, (nint)sp);
            long until = Environment.TickCount64 + 20000;
            while (p2.Test(HkRState) == 3 && Environment.TickCount64 < until) Thread.Sleep(10);
            t.Expect(p2.Test(HkRTracks) == -1 && RText(p2, 7).Contains("damaged"), $"a damaged NES REC1: tracks {p2.Test(HkRTracks)}, the window says '{RText(p2, 7)}'");
            p2.Test(HkRForget);
        }
        // changed: another recording (one period write moved by one) under the first one's checksum: only the checksum can tell
        int pw = Array.FindIndex(s.RegVals, v => v >> 8 == 0x02);
        if (pw >= 0)
        {
            s.RegVals[pw] ^= 1;
            var other = RecSection.BuildNes(s);
            BitConverter.GetBytes(BitConverter.ToUInt32(section, section.Length - 4)).CopyTo(other, other.Length - 4);
            using var p3 = lib.Create();
            p3.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            p3.SetParam(PEmulator, 6);
            p3.Test(HkRForget);
            fixed (byte* sp = other) p3.Test(HkRAdopt, other.Length, (nint)sp);
            long until = Environment.TickCount64 + 20000;
            while (p3.Test(HkRState) == 3 && Environment.TickCount64 < until) Thread.Sleep(10);
            t.Expect(p3.Test(HkRTracks) == -1 && RText(p3, 7).Contains("checksum"), $"a changed NES REC1 under the old checksum: tracks {p3.Test(HkRTracks)}, the window says '{RText(p3, 7)}'");
            p3.Test(HkRForget);
        }
        else t.Expect(false, "the take has no period write to change");
        t.Note($"project: a {section.Length:N0}-byte REC1 (version 2) reopened to the same {tr.Midi.Length:N0}-byte MIDI file; a damaged one and a changed one under the old checksum refused");
    }

    // ------------------------------------------------------------------------------------------------ FL's import, then Replace

    private void NesReplace(T t, string dir)
    {
        var made = new List<PluginInstance>();
        PluginInstance? p = null;
        try
        {
            var d = lib.Create(); made.Add(d);                                   // made before the tracks are offered
            byte[] section;
            NesSession? s;
            using (var g = NesRomGame(null, 3))
            {
                DriveFrames(g, _ => 0, 0, 60);
                s = NesTake(t, g, 900, "VRUN");
                section = RBytes(g, HkRSection);
                g.Test(HkRForget);
            }
            if (s == null) return;
            p = Adopted(t, section);
            if (p == null) return;
            long until = Environment.TickCount64 + 20000;
            while (p.Test(HkOffer) <= 0 && Environment.TickCount64 < until) Thread.Sleep(10);
            int tracks = (int)p.Test(HkOffer);
            t.Expect(tracks >= 3, $"the NES recording's tracks are not on offer ({tracks})");
            if (tracks < 3) return;
            var m = MidiReplay.Read(RBytes(p, HkRMidi));
            var tr = NesTranscribe.Run(s, new NesOptions { Name = s.Name, FlTempo = 120 });
            byte[] fresh;
            using (var f0 = lib.Create()) fresh = f0.SaveState();
            var a = lib.Create(); made.Add(a); a.LoadState([]);
            var b = lib.Create(); made.Add(b); b.LoadState(fresh);
            var kq = lib.Create(); made.Add(kq);
            double fileBpm = 60e6 / m.Tempo[0].Us;
            p.Test(HkRTempo, 0, (nint)Math.Round(fileBpm * 1000));
            // the tracks with the most notes, so each part's first notes tell its track
            var order = Enumerable.Range(0, tr.Tracks.Count).Where(k => tr.Tracks[k].Channel != 4).OrderByDescending(k => tr.Tracks[k].Notes.Count).ToList();
            // (four channels on four different tracks: a second channel on a track taken a moment before waits for the worker to build that track's next engine, by design, and starts late)
            int kTrack = order.First(k => tr.Tracks[k].Channel < 2);           // (K: a pulse track, whose volume moves)
            var others = order.Where(k => k != kTrack).ToList();
            var parts = new List<ReplacePart>
            {
                Part("A (empty state)", a, m, others[0], others[0]),
                Part("B (its own fresh state)", b, m, others[1], others[1]),
                Part("D (made before the offer)", d, m, others[2], others[2]),
                Part("K (FL lost the bends and the volume)", kq, m, kTrack, kTrack),
            };
            parts[3].FlBends = 1;
            foreach (var pt in parts) pt.P.Test(HkNProbe, 0, 1_000_000);
            long alloc = PlayReplace(parts, m);
            until = Environment.TickCount64 + 5000;
            while (parts.Any(x => x.P.Test(HkBindState) != 2) && Environment.TickCount64 < until) Thread.Sleep(20);
            foreach (var pt in parts)
            {
                long track = pt.P.Test(HkTrackOf), bstate = pt.P.Test(HkBindState);
                t.Expect(track == pt.Expect && bstate == 2 && pt.P.Test(342) == 1 && pt.P.Test(352) == 1 && pt.P.Test(HkNTrackPreset) == 1,
                    $"{pt.Name}: plays track {track + 1} (should be {pt.Expect + 1}: {tr.Tracks[pt.Expect].Name}), state {bstate}, holds a preset {pt.P.Test(342)}, preset mode {pt.P.Test(352)}, a recorded NES track {pt.P.Test(HkNTrackPreset)}: {DllText(pt.P, HkTrackStatus)}");
            }
            t.Expect(alloc == 0, $"{alloc:N0} bytes allocated on the audio thread while the channels recognised their NES tracks and played them");
            // K: FL lost the bends and the volume, the channel plays the recording's expression: the game's registers still
            {
                var k = parts[3];
                t.Expect(k.P.Test(718) == 1, $"{k.Name}: does not play its track's expression");
                var pl = Enumerable.Range(0, tr.Tracks.Count).Select(_ => new NesReplay.Played()).ToList();
                var handle = new Dictionary<int, int>();
                for (int i = 0; i < k.Handles.Count; i++) handle[(int)(k.Handles[i] & 0xFFFF)] = i;
                int count = (int)k.P.Test(HkNProbeRead, 0, 0);
                var raw = new byte[Math.Max(1, count) * 12];
                int got;
                fixed (byte* rp = raw) got = (int)k.P.Test(HkNProbeRead, count, (nint)rp);
                for (int i = 0; i < got; i++)
                {
                    long tt = BitConverter.ToInt64(raw, i * 12);
                    int code = BitConverter.ToInt32(raw, i * 12 + 8);
                    if (code >> 24 == 1) { if (handle.TryGetValue(code & 0xFFFF, out int idx)) pl[k.Expect].Log.Add((tt, 1 << 24 | idx)); }
                    else pl[k.Expect].Log.Add((tt, code));
                }
                // only K's track is compared: the others' notes are left out of the transcription for this comparison
                var only = new NesTranscription { Session = tr.Session, Notes = tr.Notes, Tracks = tr.Tracks.Select((x, i) => i == k.Expect ? x : new NesTrack { Index = x.Index, Channel = x.Channel, Duty = x.Duty, Mode = x.Mode, Name = x.Name }).ToList(), Inexact = tr.Inexact };
                var rep = new ImodelReport();
                NesReplay.Compare(rep, k.Name, only, pl, out string line);
                foreach (var f in rep.Failures.Take(4)) t.Expect(false, f);
                t.Note(line);
            }
            // Play here by mouse, in a fresh instance's Record window
            var iq = lib.Create(); made.Add(iq);
            nint parent = Win32Host.CreateParent();
            try
            {
                iq.ShowEditor(parent);
                Win32Host.Pump(60);
                ClickControl(t, iq, 2020, "Record button");
                ClickControl(t, iq, 2051, "Play here, row 2");
                t.Expect(iq.Test(HkTrackOf) == 1 && iq.Test(HkBindHow) == 2 && iq.Test(342) == 1 && iq.Test(352) == 1 && iq.Test(HkNTrackPreset) == 1, $"Play here: plays track {iq.Test(HkTrackOf) + 1} (how {iq.Test(HkBindHow)}), a NES track preset {iq.Test(HkNTrackPreset)}, preset mode {iq.Test(352)} ({EdText(iq, EdRMessage)})");
                iq.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }
            // TRK1: A's project reopened
            var state = a.SaveState();
            t.Expect(FindSection(state, 0x314B5254) >= 0, "the project has no TRK1 for A");
            a.Dispose(); made.Remove(a);
            var a2 = lib.Create(); made.Add(a2);
            a2.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            a2.LoadState(state);
            t.Expect(a2.Test(HkTrackOf) == others[0] && a2.Test(342) == 1 && a2.Test(HkNTrackPreset) == 1 && a2.Test(718) == 1, $"A reopened: plays track {a2.Test(HkTrackOf) + 1} (should be {others[0] + 1}), a NES track preset {a2.Test(HkNTrackPreset)}, the expression {a2.Test(718)}");
            t.Note($"replace: {string.Join("; ", parts.Select(x => $"{x.Name} -> {tr.Tracks[x.Expect].Name}"))} (each found and settled), A's project reopened plays its track; {alloc} bytes on the audio thread");
        }
        finally { foreach (var q in made) q.Dispose(); p?.Test(HkOfferSet, 0, 0); p?.Test(HkRForget); p?.Dispose(); }
    }

    // ------------------------------------------------------------------------------------------------ live MIDI out

    private void NesLiveScenario(T t, string dir)
    {
        using var p = NesRomGame(null, 3);
        try
        {
            p.SetParam(PChannel, 5);
            var buf = new float[512];
            void Render(int blocks) { fixed (float* f = buf) for (int i = 0; i < blocks; i++) { p.NewTick(); p.GenRender(f, 256); } }
            Render(100);
            lock (p.Host.MidiOutLog) p.Host.MidiOutLog.Clear();
            p.Test(HkLivePort, 0, 4);
            t.Expect(p.Test(HkLive, 0, 1) == 0, "live MIDI out on a NES game was refused: " + RText(p, 8));
            p.Test(HkROptions, 0, ROptDefault);
            t.Expect(p.Test(HkRStart) == 0 && RWait(p, 2, 10000), "the recording beside it did not begin: " + RText(p, 8));
            long a0 = p.Test(HkRAlloc);
            Render(2200);                                                         // about 11.7 s
            long a1 = p.Test(HkRAlloc);
            p.Test(HkRStop);
            t.Expect(RWait(p, 4), "the recording beside the live out did not end");
            p.Test(HkLive, 0, 0);
            Render(4);
            List<(uint Msg, bool Delayed)> log; lock (p.Host.MidiOutLog) log = p.Host.MidiOutLog.ToList();
            t.Expect(log.Count > 0 && log.All(x => x.Delayed && x.Msg >> 24 == 4), $"{log.Count} MIDI messages, {log.Count(x => !x.Delayed)} not through MIDIOut_Delayed, {log.Count(x => x.Msg >> 24 != 4)} not on port 4");
            var s = NesSession.FromPayload(RBytes(p, HkNPayload), out var why);
            t.Expect(s != null, "the recording does not read: " + why);
            if (s == null) return;
            var tr = NesTranscribe.Run(s, new NesOptions { Name = "live" });
            // every transcribed note (not sounding before) a note-on with its key; every note-on ended
            var ons = log.Where(x => (x.Msg & 0xF0) == 0x90 && (x.Msg >> 16 & 0x7F) > 0).Select(x => (int)(x.Msg >> 8 & 0x7F)).ToList();
            int offs = log.Count(x => (x.Msg & 0xF0) == 0x80);
            var want = tr.Notes.Where(n => n.Channel < 4 && !n.BeforeStart).Select(n => n.Channel == 3 ? NesDirect.NoiseKey(n.Periods[0].V) : (int)Math.Round(NesDirect.MidiOf(n.Periods[0].V, n.Channel == 2))).ToList();
            var pool = ons.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            int matched = 0;
            foreach (int k in want) if (pool.TryGetValue(k, out int c) && c > 0) { pool[k] = c - 1; matched++; }
            t.Expect(matched == want.Count && ons.Count == offs && want.Count > 10, $"live: {matched} of {want.Count} transcribed notes had a note-on with their key ({ons.Count} note-ons, {offs} note-offs)");
            t.Expect(a1 - a0 == 0, $"{a1 - a0:N0} bytes allocated on the audio thread with the NES live out and a recording on");
            t.Note($"live: {ons.Count} note-ons, {offs} note-offs on port 4 through MIDIOut_Delayed; {matched} of {want.Count} transcribed notes found; {a1 - a0} bytes on the audio thread");
        }
        finally { p.Test(HkLive, 0, 0); p.Test(HkRForget); }
    }

    // ------------------------------------------------------------------------------------------------ refusals

    private void NesRefusals(T t, string dir)
    {
        using (var p = lib.Create())
        {
            p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            p.SetParam(PEmulator, 5);
            p.Test(HkRForget);
            t.Expect(p.Test(HkRStart) == 1 && RText(p, 8).Contains("ROM mode"), $"Record on a NES emulator in Direct mode: '{RText(p, 8)}'");
        }
        // a DMC track's preset: no engine plays the NES DMC: refused with the reason
        string? path = ImodelGames.Find(NesRecGame.Roster, "Contra (U)", ".nes");
        if (path == null) { t.Warn(false, "NOT RUN: Contra is not in the roster: the DMC track's refusal was not checked"); return; }
        var g = NesRecGame.Find("Contra (U)", "300:start,4")!;
        g.Run(300);
        var tr = NesTranscribe.Run(g.Record(900, "dmc"), new NesOptions { Name = "dmc" });
        var dmc = tr.Tracks.FirstOrDefault(x => x.Channel == 4);
        t.Expect(dmc?.PresetFile != null, "Contra's recording has no DMC track with a preset");
        if (dmc?.PresetFile == null) return;
        using var q = lib.Create();
        q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        q.SetParam(PEmulator, 7);
        long r;
        fixed (byte* fp = dmc.PresetFile) r = q.Test(340, dmc.PresetFile.Length, (nint)fp);
        t.Expect(r != 0 && PText(q).Contains("cannot play on this console yet"), $"a DMC track's preset: loaded {r == 0}, '{PText(q)}'");
        t.Note($"refusals: Direct mode; {dmc.Name}: '{PText(q)}'");
    }
}
