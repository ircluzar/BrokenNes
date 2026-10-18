// Round 8 (record): recording a SNES game's music and replaying it with no game, through the real plugin DLL (Native AOT). The library checks first (RecordChecks: root, tempo, capture, transcribe,
// smf, section, with their deliberate bugs; the real games are `FruityHost record-test`), then the DLL:
//   flow       the header's Record button (real mouse messages): the window, Record, the game runs, Stop, the transcription, Save MIDI... (the dialog replaced by editor hook 671), the drag handles (the data
//              object made and read back as a drop target reads it: editor hook 672 runs everything but the modal drag loop), a handle clicked without a drag, Close
//   replay     THE PROOF: a recording's MIDI file (read back by VRUN's parser) played the way FL would into one BrokenNes2 instance per track, each on the Presets chip with its track's preset, no game: every pitch
//              value the game held for 4 ms or more (three of FL's 64-sample control steps) is the register the instance's own S-DSP writes (plugin hooks 654/655: the sampler's writes), unless the transcription itself lists it as not exact; volumes
//              within 2; the key-on envelope; nothing allocated while playing. On the synthetic driver's music (a REC1 section adopted: no ROM needed) and on Super Mario World when it is on this machine.
//   capture    the capture in ROM mode while the audio thread pulls the game: 0 bytes allocated on that thread from Record to Stop, no write lost (the game's own log counts the same writes)
//   project    REC1: saved, reopened to the same MIDI bytes; damaged, changed (checksum) and newer sections refused with the reason (a newer one written back as it came); absent: nothing said
//   replace    FL's "import the MIDI file, Replace each MIDI Out channel by BrokenNes2" (2026-10-18): instances made every way FL plausibly makes them (an empty state, a fresh instance's own
//              state, every parameter pushed at its default, made before the tracks were offered) each recognise their track from their notes (key + FL's song position) and play its preset from
//              the first note; playback from the middle, the song 16 bars later, no song position from FL, a channel of other notes never taken; "Play here" by hand; the project; the offer
//              withdrawn; the host log; 0 bytes on the audio thread
//   transport  "start with FL's Play" waits for FL's Play; "stop with FL's Stop" ends it; a recording in which the game keyed nothing says so
//   live       live MIDI out (P2): the messages FL receives (MIDIOut_Delayed, the port) read back against a recording made at the same time: every key-on a note-on on its instrument's channel, pitch
//              and level reproduced, every note ended; FHD_ActivateMIDI; 0 bytes on the audio thread; the synthetic game and Super Mario World (Instrument Runaway ends the notes)
//   refusals   Direct mode, a NES game, Save with nothing recorded: refused with the reason; Save MIDI writes the whole file
// Each scenario with a deliberate bug is run again with the bug switched on in the DLL (plugin hook 657) and must fail.
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using BrokenNes2.Record;
using SynthSnes = BrokenNes.Workshop.Corruption.SynthSnesRom;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkRStart = 640, HkRStop = 641, HkRState = 642, HkRText = 643, HkRWrites = 644, HkRDropped = 645, HkRTracks = 646, HkRTrack = 647, HkRSave = 648, HkRMidi = 649, HkRSection = 650,
        HkROptions = 651, HkRDeal = 652, HkRDealState = 653, HkRProbe = 654, HkRProbeRead = 655, HkRAlloc = 656, HkRMutant = 657, HkRTempo = 658, HkRStall = 659, HkRAgain = 660, HkRTrackMidi = 661,
        HkRPresets = 662, HkRGen = 663, HkRDealt = 664, HkRProbeLost = 665, HkRPreset = 666, HkRAdopt = 667, HkRForget = 668;
    private const int EdROpen = 670, EdRSavePath = 671, EdRDragTest = 672, EdRDrag = 673, EdRDrags = 674, EdRMessage = 675;
    private const int IdRecBtn = 2020, IdRecCloseBtn = 2021, IdRecStartBtn = 2022, IdRecStopBtn = 2023, IdRecSaveBtn = 2024, IdRecDragAllBtn = 2030, IdRecTrackRow = 2040;
    /// <summary>Options (hook 651): stop with FL's Stop only (the default), and with lanes.</summary>
    private const int ROptDefault = 2, ROptLanes = 4;

    private static string RText(PluginInstance p, int field)
    {
        var c = new char[8000];
        fixed (char* cp = c) { long n = p.Test(HkRText, (field << 16) | 8000, (nint)cp); return n < 0 ? "" : new string(c, 0, (int)Math.Min(n, c.Length)); }
    }

    private static string EdText(PluginInstance p, int hook, int field = 0)
    {
        var c = new char[2000];
        fixed (char* cp = c) { long n = p.Test(hook, (field << 16) | 2000, (nint)cp); return n < 0 ? "" : new string(c, 0, (int)Math.Min(n, c.Length)); }
    }

    /// <summary>A byte answer of a hook that copies into a buffer (index = selector | buffer size, value = the buffer; with no buffer it returns the length).</summary>
    private static byte[] RBytes(PluginInstance p, int hook, long selector = 0)
    {
        long n = p.Test(hook, (nint)selector, 0);
        if (n <= 0) return [];
        var b = new byte[n];
        fixed (byte* bp = b) p.Test(hook, (nint)(selector | n), (nint)bp);
        return b;
    }

    /// <summary>Waits until the emulator's recording state is <paramref name="state"/> (0 idle, 1 armed, 2 recording, 3 transcribing, 4 done), rendering a little when asked, or the time passes.</summary>
    private static bool RWait(PluginInstance p, int state, int ms = 60000, bool render = false)
    {
        long until = Environment.TickCount64 + ms;
        var buf = new float[512];
        while (Environment.TickCount64 < until)
        {
            if (p.Test(HkRState) == state) return true;
            if (render) fixed (float* f = buf) { p.NewTick(); p.GenRender(f, 256); }
            Win32Host.Pump(5);
        }
        return p.Test(HkRState) == state;
    }

    /// <summary>The synthetic SNES game (the round-6 vibrato driver: it keys one note at its start, then bends it every 9 ms and fades it) loaded in ROM mode, no frame run yet.</summary>
    private PluginInstance RecordGame(string dir)
    {
        string rom = Path.Combine(dir, "vibrato.sfc");
        if (!File.Exists(rom)) File.WriteAllBytes(rom, SynthSnes.Build(vibrato: true));
        var p = SegaGame(rom, 2, "SNES");
        p.Test(HkROptions, 0, ROptDefault);
        return p;
    }

    /// <summary>The synthetic driver's music (four instruments: vibrato, chords with slides, a panned fading bass, a sample loaded under another's number) as a REC1 section, the way a project brings one.</summary>
    private static byte[] SynthSection(double seconds, out RecSession session, int seed = 7, string name = "Synth take", ulong id = 0xAB)
    {
        var drv = new SynthDriver(132.5, seed);
        var rec = new SpcRecorder(); rec.Arm(); for (int i = 0; i < 40; i++) rec.Feed();
        drv.Play(rec, seconds);
        session = rec.Finish(drv.Aram, drv.Regs, name, "synthetic driver", "", 1_760_000_000, id);
        return RecSection.Build(session);
    }

    /// <summary>A plugin on emulator #6 (Direct mode, no game) holding the recording of <paramref name="section"/> as a project would bring it, transcribed. Null (and a failure) when it did not come.</summary>
    private PluginInstance? Adopted(T t, byte[] section, int options = ROptDefault, int emulator = 6)
    {
        var p = lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        p.SetParam(PEmulator, emulator);
        p.Test(HkRForget);
        p.Test(HkROptions, 0, options);
        fixed (byte* sp = section) p.Test(HkRAdopt, section.Length, (nint)sp);
        if (RWait(p, 4)) return p;
        t.Expect(false, $"the adopted recording was not transcribed (state {p.Test(HkRState)}: {RText(p, 1)} {RText(p, 7)})");
        p.Dispose();
        return null;
    }

    private delegate void RecordScenario(T t, string dir);

    /// <summary>The scenarios are being run again with a deliberate bug on (the real game's take is not run twice).</summary>
    private bool recordMutantPass;

    private (string Name, RecordScenario Body, int Mutant, string Why)[] RecordScenarios() =>
    [
        ("flow", RecordFlow, RecMutants.DragWrongFile, "the song's drag handle drags the first track's file"),
        ("replay", RecordReplay, RecMutants.TuneIgnoredInPreset, "the presets forget their tuning"),
        ("capture", RecordCapture, RecMutants.CaptureAllocates, "the capture allocates at every key-on"),
        ("project", RecordProject, RecMutants.SectionCrcNotChecked, "the project section's checksum is not checked"),
        ("replace", RecordReplaceScenario, RecMutants.DefaultStateCountsAsTouched, "a default state counts as configuring the instance"),
        ("transport", RecordTransportScenario, 0, ""),
        ("live", RecordLiveScenario, RecMutants.LiveOutSilent, "live MIDI out sends nothing"),
        ("refusals", RecordRefusals, RecMutants.SaveWritesHalf, "Save MIDI... writes half the file"),
    ];

    /// <summary>More deliberate bugs, each run once more with the scenario that must catch it (the replace scenario watches several places).</summary>
    private (string Name, RecordScenario Body, int Mutant, string Why)[] RecordExtraMutants() =>
    [
        ("replace", RecordReplaceScenario, RecMutants.DealIgnored, "a recording's tracks are never put on offer"),
        ("replace", RecordReplaceScenario, RecMutants.OfferIgnoresLateInstances, "instances made before the offer are ignored"),
        ("replace", RecordReplaceScenario, RecMutants.BindAfterFirstNote, "the track is taken after the first note was played"),
        ("replace", RecordReplaceScenario, RecMutants.EnginesNotPrebuilt, "the track's engine is built on the audio thread"),
        ("replace", RecordReplaceScenario, RecMutants.TrackSectionDropped, "the project does not keep which track an instance plays"),
        ("replace", RecordReplaceScenario, RecMutants.ManualPickIgnored, "Play here says done and does nothing"),
        ("replace", RecordReplaceScenario, RecMutants.UnrelatedGrabbed, "any key fits a track's note"),
        ("replace", RecordReplaceScenario, RecMutants.ExpressionIgnored, "track channels ignore the recording's bends and volume"),
    ];

    private void RecordTest(T t)
    {
        // the library (no DLL): the same checks as `record-test` without the real games, and their deliberate bugs
        foreach (var o in RecordChecks.RunChecks("root,tempo,capture,transcribe,smf,section,live,match"))
        {
            foreach (var f in o.Report.Failures.Take(3)) t.Expect(false, $"[{o.Id}] {f}");
            foreach (var w in o.Report.Warnings) t.Warn(false, $"[{o.Id}] {w}");
            t.Note($"library {o.Id}: {o.Report.Assertions} assertions passed");
        }
        var (lines, missed) = RecordChecks.RunMutants("root,tempo,capture,transcribe,smf,section,live,match");
        t.Expect(missed == 0, $"{missed} of the library's deliberate bugs were not caught: " + string.Join(" / ", lines.Where(l => l.Contains("MISSED"))));
        t.Note($"library: {lines.Count - missed} of {lines.Count} deliberate bugs caught");

        string dir = Path.Combine(Path.GetTempPath(), "bn2_record_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string presets = Path.Combine(dir, "presets");
        Directory.CreateDirectory(presets);
        string? oldPresets = Environment.GetEnvironmentVariable("BROKENNES_PRESETS_DIR");
        Environment.SetEnvironmentVariable("BROKENNES_PRESETS_DIR", presets);           // (the recording's presets go into a scratch library, never the user's; the DLL reads the variable every time)
        using (var h = lib.Create()) { h.Test(120, 0, 1); h.Test(HkRTempo, 0, 120_000); h.Test(HkRMutant, 0, 0); }
        try
        {
            foreach (var (name, body, _, _) in RecordScenarios())
            {
                var tt = new T();
                RunRecordScenario(name, body, tt, dir);
                t.Failures.AddRange(tt.Failures.Select(f => $"{name}: {f}"));
                t.Warnings.AddRange(tt.Warnings.Select(f => $"{name}: {f}"));
                t.Info.AddRange(tt.Info.Select(f => $"{name}: {f}"));
            }
            int caught = 0, armed = 0;
            recordMutantPass = true;
            foreach (var (name, body, mutant, why) in RecordScenarios().Concat(RecordExtraMutants()))
            {
                if (mutant == 0) continue;
                armed++;
                var tt = new T();
                using (var probe = lib.Create()) probe.Test(HkRMutant, 0, mutant);
                try { RunRecordScenario(name, body, tt, dir); }
                finally { using var probe = lib.Create(); probe.Test(HkRMutant, 0, 0); probe.Test(HkOfferSet, 0, 0); }
                if (tt.Failures.Count > 0) { caught++; t.Note($"deliberate bug '{why}' -> {name} failed: {Short(tt.Failures[0])}"); }
                else t.Expect(false, $"deliberate bug '{why}' was not caught by {name}: that check cannot fail");
            }
            t.Note($"DLL: {caught} of {armed} deliberate bugs caught");
        }
        finally
        {
            recordMutantPass = false;
            using (var h = lib.Create()) { h.Test(120, 0, 0); h.Test(HkOfferSet, 0, 0); h.Test(HkRMutant, 0, 0); h.Test(HkRTempo, 0, 120_000); }     // (no tracks stay on offer for the checks after this one)
            Environment.SetEnvironmentVariable("BROKENNES_PRESETS_DIR", oldPresets);
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        static string Short(string s) => s.Length > 220 ? s[..217] + "..." : s;
    }

    /// <summary>Runs a scenario; the window's ("flow") on a thread of its own in a single-threaded apartment, as FL's user-interface thread is (OLE drag and drop needs one: on this program's
    /// multithreaded main thread the plugin refuses the drag with the reason, which is right, but tests nothing).</summary>
    private static void RunRecordScenario(string name, RecordScenario body, T tt, string dir)
    {
        void Body()
        {
            try { body(tt, dir); }
            catch (Exception e) { tt.Failures.Add($"exception {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
        }
        if (name != "flow") { Body(); return; }
        var th = new Thread(Body) { Name = "record flow (STA, like FL's UI thread)" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join();
    }

    // ------------------------------------------------------------------------------------------------ the window, with real mouse messages

    private void RecordFlow(T t, string dir)
    {
        using var p = RecordGame(dir);
        nint parent = Win32Host.CreateParent();
        try
        {
            p.ShowEditor(parent);
            for (int k = 0; k < 3; k++) Win32Host.Pump(40);
            var hwnd = p.EditorHandle;
            // the header's button: right of Test MIDI export, left of the emulator badge and About, clear of the tab strip
            long btn = p.Test(30, IdRecBtn), mid = p.Test(30, 2000), about = p.Test(30, 40), tab0 = p.Test(30, 1000);
            t.Expect(btn != 0 && mid != 0 && about != 0, $"the header's buttons are not all on screen (Record {btn != 0}, Test MIDI export {mid != 0}, About {about != 0})");
            if (btn == 0) return;
            var (bx, by, bw, bh) = RectOf(btn);
            var (mx, _, mw, _) = RectOf(mid);
            var (ax, _, _, _) = RectOf(about);
            var (tx, ty, _, _) = RectOf(tab0);
            t.Expect(by >= 8 && by + bh <= 44 && bx >= mx + mw && bx + bw <= ax - 230 && (tab0 == 0 || bx + bw <= tx || by + bh <= ty),
                $"the Record button at {bx},{by} {bw}x{bh} overlaps the Test MIDI export button (ends at {mx + mw}), the emulator badge and About (from {ax - 242}) or the tab strip ({tx},{ty})");
            ClickControl(t, p, IdRecBtn, "Record button");
            t.Expect(p.Test(EdROpen) == 1, "the Record window did not open");
            ClickControl(t, p, IdRecStartBtn, "Record");
            t.Expect(p.Test(HkRState) is 1 or 2, $"Record: state {p.Test(HkRState)} ({EdText(p, EdRMessage)})");
            p.Test(HkRun, 0, 360);                                          // 6 s of the game: its note keyed, bent and faded
            long writes = p.Test(HkRWrites);
            t.Expect(p.Test(HkRState) == 2 && writes > 300, $"recording: state {p.Test(HkRState)}, {writes} chip writes after 6 s");
            ClickControl(t, p, IdRecStopBtn, "Stop");
            t.Expect(RWait(p, 4), $"the recording was not transcribed (state {p.Test(HkRState)}: {RText(p, 0)} / {RText(p, 1)})");
            if (p.Test(HkRState) != 4) return;
            int tracks = (int)p.Test(HkRTracks);
            string facts = RText(p, 2), path = RText(p, 3);
            t.Expect(tracks == 1 && facts.Contains("pitch") && File.Exists(path), $"the transcription: {tracks} tracks (the driver plays one instrument), facts '{facts}', file '{path}'");
            var midi = RBytes(p, HkRMidi);
            var m = MidiReplay.Read(midi);
            bool same = File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(midi);
            t.Expect(m.Notes.Count == 1 && m.Problems.Count == 0 && same, $"the MIDI file: {m.Notes.Count} notes (the driver keyed one), problems: {string.Join("; ", m.Problems)}; the file on disk is the recording's: {same}");
            t.Note($"recorded the synthetic game through the window: {writes} chip writes; {facts}");
            // Save MIDI... (the dialog replaced by the test's path)
            string saved = Path.Combine(dir, "saved song.mid");
            fixed (char* c = saved + "\0") p.Test(EdRSavePath, 0, (nint)c);
            ClickControl(t, p, IdRecSaveBtn, "Save MIDI...");
            t.Expect(File.Exists(saved) && File.ReadAllBytes(saved).AsSpan().SequenceEqual(midi), $"Save MIDI... did not write the recording ({EdText(p, EdRMessage)})");
            // the drag handles, dragged with the button down (test mode: everything but the modal drag loop)
            p.Test(EdRDragTest, 0, 1);
            int drags = (int)p.Test(EdRDrags);
            var hc = Centre(p, IdRecDragAllBtn);
            t.Expect(hc != null, "the whole song's drag handle is not on screen");
            if (hc != null) Win32Host.Drag(hwnd, hc.Value.X, hc.Value.Y, hc.Value.X + 40, hc.Value.Y - 30);
            string asked = EdText(p, EdRDrag, 0), carried = EdText(p, EdRDrag, 1), outcome = EdText(p, EdRDrag, 2);
            t.Expect(p.Test(EdRDrags) == drags + 1 && string.Equals(asked, path, StringComparison.OrdinalIgnoreCase) && string.Equals(carried, asked, StringComparison.OrdinalIgnoreCase) && outcome.StartsWith("test:"),
                $"dragging the song: {p.Test(EdRDrags) - drags} drags, asked '{asked}', the data object carried '{carried}', '{outcome}' (the song is '{path}')");
            var row = Centre(p, IdRecTrackRow);
            string trackPath = RText(p, 9).Split('|')[0];
            if (row != null) Win32Host.Drag(hwnd, row.Value.X, row.Value.Y, row.Value.X + 60, row.Value.Y);
            t.Expect(row != null && string.Equals(EdText(p, EdRDrag, 0), trackPath, StringComparison.OrdinalIgnoreCase) && string.Equals(EdText(p, EdRDrag, 1), trackPath, StringComparison.OrdinalIgnoreCase),
                $"dragging track 1: asked '{EdText(p, EdRDrag, 0)}', carried '{EdText(p, EdRDrag, 1)}' (its file is '{trackPath}')");
            t.Expect(File.Exists(trackPath) && MidiReplay.Read(File.ReadAllBytes(trackPath)) is { Format: 0, Notes.Count: 1 }, $"track 1's own file is not a type-0 MIDI file with its note: {trackPath}");
            // a handle clicked without a drag: the window says how
            drags = (int)p.Test(EdRDrags);
            if (hc != null) Win32Host.Click(hwnd, hc.Value.X, hc.Value.Y);
            t.Expect(p.Test(EdRDrags) == drags && EdText(p, EdRMessage).Contains("Hold the button and drag"), $"a click on the handle: {p.Test(EdRDrags) - drags} drags, the window says '{EdText(p, EdRMessage)}'");
            p.Test(EdRDragTest, 0, 0);
            // live MIDI out: the check box and the port, by mouse
            long port0 = p.Test(683, 0, -1);
            ClickControl(t, p, 2033, "port +");
            long port1 = p.Test(683, 0, -1);
            ClickControl(t, p, 2031, "Live MIDI out");
            long liveState = p.Test(681);
            string liveSaid = EdText(p, EdRMessage);
            ClickControl(t, p, 2031, "Live MIDI out (off)");
            t.Expect(port1 == port0 + 1 && liveState is 1 or 3 && liveSaid.Contains($"port {port1}") && p.Test(681) is -1 or 2,
                $"live MIDI out by mouse: port {port0} -> {port1}, on: state {liveState} ('{liveSaid}'), off: state {p.Test(681)}");
            ClickControl(t, p, 2032, "port -");
            ClickControl(t, p, IdRecCloseBtn, "Close");
            t.Expect(p.Test(EdROpen) == 0, "Close did not close the Record window");
            p.HideEditor();
        }
        finally { p.Test(EdRDragTest, 0, 0); Win32Host.DestroyParent(parent); p.Test(HkRForget); }
    }

    // ------------------------------------------------------------------------------------------------ the proof: the MIDI file played into presets, register by register

    private sealed class ReplayPlayer
    {
        public PluginInstance P = null!;
        public List<MidiReplay.Note> Notes = new();
        public long[] BendT = [], CcT = [];
        public int[] BendV = [], CcV = [];
        public int Range = 2;
        /// <summary>The plugin's voice handle (low 16 bits) -> the note's index in the track (key-on order).</summary>
        public Dictionary<int, int> NoteOfHandle = new();
        public List<(long T, int Code)> Probe = new();
    }

    /// <summary>The value in force at <paramref name="tick"/>: the last event at or before it (binary search).</summary>
    private static int ValueAt(long[] ts, int[] vs, long tick, int initial)
    {
        int lo = 0, hi = ts.Length - 1, at = -1;
        while (lo <= hi) { int m = (lo + hi) >> 1; if (ts[m] <= tick) { at = m; lo = m + 1; } else hi = m - 1; }
        return at < 0 ? initial : vs[at];
    }

    /// <summary>Plays a recording's MIDI file the way FL would: one BrokenNes2 instance per track on emulator #7, each on the Presets chip with its track's preset; FL's voices with their pitch (key + the
    /// channel's bend at the channel's RPN range, in cents from C5) and level (velocity / 127 x CC7 / 100), rewritten every 64-sample block at 48 kHz. Returns the players (their probes read), the bytes the
    /// DLL allocated on this (the audio) thread from 0.5 s to the end, and a problem.</summary>
    private (List<ReplayPlayer> Players, long Alloc, string? Problem) PlayTracks(byte[] midi, IReadOnlyList<byte[]> presetFiles)
    {
        var m = MidiReplay.Read(midi);
        const int rate = 48000, block = 64;
        var players = new List<ReplayPlayer>();
        for (int k = 0; k < presetFiles.Count; k++)
        {
            var q = lib.Create();
            var pl = new ReplayPlayer { P = q };
            players.Add(pl);
            q.Dispatcher(Fpd.SetSampleRate, 0, rate);
            q.SetParam(PEmulator, 7);
            var file = presetFiles[k];
            long r;
            fixed (byte* fp = file) r = file.Length == 0 ? 1 : q.Test(340, file.Length, (nint)fp);
            if (r != 0) return (players, 0, $"track {k + 1}'s preset was not loaded: {(file.Length == 0 ? "the track has none" : PText(q))}");
            q.Test(HkRProbe, 0, 2_000_000);
            pl.Notes = m.Notes.Where(n => n.Track == k + 1).ToList();
            if (pl.Notes.Count >= 65536) return (players, 0, $"track {k + 1} has {pl.Notes.Count} notes: more than the replay's handle map holds");
            var chan = pl.Notes.Count > 0 ? m.Of(k + 1, pl.Notes[0].Channel) : new MidiReplay.Chan();
            pl.Range = chan.Range >= 0 ? chan.Range : 2;
            pl.BendT = chan.Bends.Select(b => b.Tick).ToArray(); pl.BendV = chan.Bends.Select(b => b.Bend).ToArray();
            pl.CcT = chan.Cc7.Select(b => b.Tick).ToArray(); pl.CcV = chan.Cc7.Select(b => b.Cc).ToArray();
        }
        double end = m.Notes.Count == 0 ? 1 : m.Seconds(m.Notes.Max(n => n.Off)) + 0.5;
        long endSample = (long)(end * rate);
        var buf = new float[block * 2];
        var active = new List<(ReplayPlayer Pl, MidiReplay.Note Note, SimVoice V)>();
        var next = new int[players.Count];
        long a0 = -1, a1 = -1;
        fixed (float* bp = buf)
        {
            for (long s = 0; s < endSample; s += block)
            {
                long tick = m.TickAt(s / (double)rate);
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    var (pl, n, v) = active[i];
                    if (n.Off <= tick) { pl.P.Host.ReleaseFromMixer(v); active.RemoveAt(i); }
                }
                for (int pi = 0; pi < players.Count; pi++)
                {
                    var pl = players[pi];
                    while (next[pi] < pl.Notes.Count && pl.Notes[next[pi]].On <= tick)
                    {
                        int idx = next[pi]++;
                        var n = pl.Notes[idx];
                        var v = pl.P.Host.NewVoice(0, n.Velocity / 127f);
                        Levels(v, n, pl, tick);
                        v.Handle = pl.P.TriggerVoice(v.Params, v.Tag);
                        pl.NoteOfHandle[(int)(v.Handle & 0xFFFF)] = idx;
                        active.Add((pl, n, v));
                    }
                }
                foreach (var (pl, n, v) in active) Levels(v, n, pl, tick);
                if (a0 < 0 && s >= rate / 2) a0 = players[0].P.Test(HkRAlloc);
                foreach (var pl in players) { pl.P.NewTick(); pl.P.GenRender(bp, block); }
            }
            a1 = players[0].P.Test(HkRAlloc);
        }
        foreach (var pl in players)
        {
            if (pl.P.Test(HkRProbeLost) > 0) return (players, 0, $"the replay probe of an instance overflowed ({pl.P.Test(HkRProbeLost):N0} writes lost)");
            int count = (int)pl.P.Test(HkRProbeRead, 0, 0);
            var raw = new byte[Math.Max(1, count) * 12];
            int got;
            fixed (byte* rp = raw) got = (int)pl.P.Test(HkRProbeRead, count, (nint)rp);
            for (int i = 0; i < got; i++) pl.Probe.Add((BitConverter.ToInt64(raw, i * 12), BitConverter.ToInt32(raw, i * 12 + 8)));
        }
        return (players, a0 < 0 ? 0 : a1 - a0, null);

        static void Levels(SimVoice v, MidiReplay.Note n, ReplayPlayer pl, long tick)
        {
            int bend = ValueAt(pl.BendT, pl.BendV, tick, 0);
            int cc = ValueAt(pl.CcT, pl.CcV, tick, 100);
            ref var f = ref v.Params->FinalLevels;
            f.Pitch = (float)((n.Key - 60) * 100 + bend * pl.Range * 100.0 / 8192.0);
            f.Vol = (float)(n.Velocity / 127.0 * cc / 100.0);
            f.Pan = 0;
            v.Params->InitLevels = f;
        }
    }

    private static string PText(PluginInstance q)
    {
        var c = new char[1000];
        fixed (char* cp = c) { long n = q.Test(341, 1000, (nint)cp); return new string(c, 0, (int)Math.Max(0, Math.Min(n, 1000))); }
    }

    /// <summary>Compares, note by note, the S-DSP registers each instance wrote with the game's: every pitch value the game held for 4 ms or more (three of FL's 64-sample control steps), in the middle of its time (counted from the note's own key-on
    /// on each side), must be the register the instance had then, unless the transcription lists it as not exact; the volumes within 2 (notes keyed with the preset's balance); the envelope keyed is the
    /// preset's, which is the game's for every note the transcription does not list as keyed with another.</summary>
    private static void CompareReplay(T t, string label, Transcription tr, List<ReplayPlayer> players, int[]? trackOf = null)
    {
        int values = 0, exact = 0, reported = 0, unreported = 0, shortValues = 0, vols = 0, volOk = 0, volReported = 0, volOther = 0, envSame = 0, envWrong = 0, envOther = 0, missing = 0, compared = 0;
        double maxErr = 0;
        for (int pi = 0; pi < players.Count; pi++)
        {
            int k = trackOf?[pi] ?? pi;
            if (k >= tr.Tracks.Count) continue;
            var track = tr.Tracks[k];
            var pl = players[pi];
            var probe = pl.Probe;
            // the marks (an FL voice took an S-DSP voice), in time order
            var starts = new List<(int Note, int Voice, long T, int From)>();
            for (int i = 0; i < probe.Count; i++)
            {
                int code = probe[i].Code;
                if (code >> 24 != 1) continue;
                if (pl.NoteOfHandle.TryGetValue(code & 0xFFFF, out int noteIdx)) starts.Add((noteIdx, code >> 16 & 0xFF, probe[i].T, i));
            }
            missing += track.Notes.Count - starts.Select(x => x.Note).Distinct().Count();
            foreach (var (noteIdx, dv, t0, from) in starts)
            {
                if (noteIdx >= track.Notes.Count) continue;
                var gn = tr.Notes[track.Notes[noteIdx]];
                compared++;
                // the voice's registers after this mark, until the voice's next mark
                int to = probe.Count;
                for (int i = from + 1; i < probe.Count; i++) if (probe[i].Code >> 24 == 1 && (probe[i].Code >> 16 & 0xFF) == dv) { to = i; break; }
                int lo = -1, hi = -1, vl = 0, vr = 0, a1 = -1, a2 = -1, g = -1;
                var pitch = new List<(long T, int P)>(); var vol = new List<(long T, int L, int R)>();
                for (int i = from + 1; i < to; i++)
                {
                    int code = probe[i].Code;
                    if (code >> 24 != 0) continue;
                    int reg = code >> 8 & 0x7F, val = code & 0xFF;
                    if (reg >> 4 != dv) continue;
                    switch (reg & 15)
                    {
                        case 0: vl = (sbyte)val; vol.Add((probe[i].T, vl, vr)); break;
                        case 1: vr = (sbyte)val; vol.Add((probe[i].T, vl, vr)); break;
                        case 2: lo = val; if (hi >= 0) pitch.Add((probe[i].T, lo | (hi & 0x3F) << 8)); break;
                        case 3: hi = val; if (lo >= 0) pitch.Add((probe[i].T, lo | (hi & 0x3F) << 8)); break;
                        case 5: if (a1 < 0) a1 = val; break;
                        case 6: if (a2 < 0) a2 = val; break;
                        case 7: if (g < 0) g = val; break;
                    }
                }
                if (pitch.Count == 0) { missing++; continue; }
                // the envelope: the preset's (the instrument's most common), which must be the game's own for a note keyed with it
                bool gameHasPresets = gn.Adsr1 == track.Adsr1 && gn.Adsr2 == track.Adsr2 && gn.Gain == track.Gain;
                bool keyedPresets = a1 == track.Adsr1 && a2 == track.Adsr2 && g == track.Gain;
                if (!keyedPresets) envWrong++;
                else if (gameHasPresets) envSame++;
                else envOther++;
                for (int v = 0; v < gn.Pitches.Count; v++)
                {
                    uint g0 = gn.Pitches[v].T, g1 = v + 1 < gn.Pitches.Count ? gn.Pitches[v + 1].T : gn.End;
                    if (g1 - g0 < 128) { shortValues++; continue; }
                    uint mid = (g0 + g1) / 2;
                    int got = At(pitch, t0 + (mid - gn.Start));
                    values++;
                    if (got == gn.Pitches[v].P) { exact++; continue; }
                    maxErr = Math.Max(maxErr, Math.Abs(1200 * Math.Log2(Math.Max(1, got) / (double)Math.Max(1, gn.Pitches[v].P))));
                    if (tr.Reported(tr.InexactPitch, track.Notes[noteIdx], mid)) { reported++; continue; }
                    unreported++;
                    if (unreported <= 6) t.Expect(false, $"{label}: track {k + 1} note {noteIdx} at {mid / 32000.0:0.000} s: the instance wrote ${got:X4}, the game ${gn.Pitches[v].P:X4}, and the transcription did not say so");
                }
                if (gn.Vols.Count == 0) continue;
                var b0 = gn.Vols[0];
                int bm = Math.Max(Math.Abs(b0.L), Math.Abs(b0.R));
                bool sameBalance = bm == 0 || ((int)Math.Round(127.0 * b0.L / bm), (int)Math.Round(127.0 * b0.R / bm)) == (track.WeightL, track.WeightR);
                for (int v = 0; v < gn.Vols.Count; v++)
                {
                    uint g0 = gn.Vols[v].T, g1 = v + 1 < gn.Vols.Count ? gn.Vols[v + 1].T : gn.End;
                    if (g1 - g0 < 128) continue;
                    uint mid = (g0 + g1) / 2;
                    var (rl, rr) = VolAt(vol, t0 + (mid - gn.Start));
                    if (!sameBalance) { volOther++; continue; }
                    vols++;
                    if (Math.Abs(rl - gn.Vols[v].L) <= 2 && Math.Abs(rr - gn.Vols[v].R) <= 2) volOk++;
                    else if (tr.Reported(tr.InexactVolume, track.Notes[noteIdx], mid)) volReported++;
                    else if (vols - volOk - volReported <= 4) t.Expect(false, $"{label}: track {k + 1} note {noteIdx} at {mid / 32000.0:0.000} s: the instance wrote volumes {rl}/{rr}, the game {gn.Vols[v].L}/{gn.Vols[v].R}");
                }
            }
        }
        int notes = Enumerable.Range(0, players.Count).Select(pi => trackOf?[pi] ?? pi).Where(k => k < tr.Tracks.Count).Sum(k => tr.Tracks[k].Notes.Count);
        t.Expect(unreported == 0 && values > 0, $"{label}: pitch {exact:N0} of {values:N0} values exact, {reported} inexact as the transcription says, {unreported} inexact WITHOUT it saying so");
        t.Expect(missing == 0 && compared == notes, $"{label}: {compared} of {notes} notes keyed by the instances, {missing} never keyed or never given a pitch");
        t.Expect(vols == volOk + volReported, $"{label}: volumes {volOk} of {vols} within 2, {volReported} reported, {vols - volOk - volReported} off WITHOUT a word");
        t.Expect(envWrong == 0, $"{label}: {envWrong} notes keyed with another envelope than their preset's");
        t.Note($"{label}: {notes} notes on {players.Count} instances; pitch {exact:N0} of {values:N0} values held 4 ms or more exact as written by the instances' own S-DSP, {reported} inexact as reported (largest {maxErr:0.##} cents), {shortValues:N0} shorter values not compared; volumes {volOk} of {vols} within 2 ({volReported} reported, {volOther} values of notes with another balance than their preset's); envelope: the game's on {envSame} notes, the preset's (another than the game's, as reported) on {envOther}");

        static int At(List<(long T, int P)> l, long tt) { int v = l[0].P; foreach (var x in l) { if (x.T > tt) break; v = x.P; } return v; }
        static (int, int) VolAt(List<(long T, int L, int R)> l, long tt) { var v = l.Count > 0 ? (l[0].L, l[0].R) : (0, 0); foreach (var x in l) { if (x.T > tt) break; v = (x.L, x.R); } return v; }
    }

    private void RecordReplay(T t, string dir)
    {
        // 1. the synthetic driver's music, as a project would bring it (no ROM): adopted, transcribed by the DLL, replayed through the presets
        byte[] section = SynthSection(20, out var session);
        foreach (bool lanes in new[] { false, true })
        {
            using var p = Adopted(t, section, lanes ? ROptLanes : ROptDefault);
            if (p == null) return;
            var tr = RecTranscribe.Run(session, new RecOptions { Name = session.Name, FlTempo = 120, Lanes = lanes });
            var midi = RBytes(p, HkRMidi);
            t.Expect(midi.AsSpan().SequenceEqual(tr.Midi), $"the DLL's transcription ({midi.Length} bytes of MIDI) differs from the library's ({tr.Midi.Length}) for the same recording");
            var presets = Enumerable.Range(0, tr.Tracks.Count).Select(k => RBytes(p, HkRPreset, (long)k << 24)).ToList();
            var (players, alloc, problem) = PlayTracks(midi, presets);
            try
            {
                t.Expect(problem == null, problem ?? "");
                if (problem == null)
                {
                    CompareReplay(t, lanes ? "synthetic, lanes" : "synthetic", tr, players);
                    t.Expect(alloc == 0, $"{alloc:N0} bytes allocated on the audio thread while {players.Count} preset instances played the recording");
                }
            }
            finally { foreach (var x in players) x.P.Dispose(); p.Test(HkRForget); }
        }
        // 2. Super Mario World's overworld (when the ROM is on this machine): recorded by the DLL from the running game, replayed the same way
        if (recordMutantPass) return;
        if (!File.Exists(ImodelGames.Smw)) { t.Warn(false, "NOT RUN: Super Mario World is not at " + ImodelGames.Smw + ": the replay was checked on the synthetic driver only"); return; }
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
            var s2 = RecSection.Read(RBytes(g, HkRSection), out var why);
            t.Expect(s2 != null, "the DLL's recording section does not read: " + why);
            if (s2 == null) return;
            var tr2 = RecTranscribe.Run(s2, new RecOptions { Name = s2.Name, FlTempo = 120 });
            var midi2 = RBytes(g, HkRMidi);
            t.Expect(midi2.AsSpan().SequenceEqual(tr2.Midi), "the DLL's transcription of Super Mario World differs from the library's");
            var presets2 = Enumerable.Range(0, tr2.Tracks.Count).Select(k => RBytes(g, HkRPreset, (long)k << 24)).ToList();
            var (players2, alloc2, problem2) = PlayTracks(midi2, presets2);
            try
            {
                t.Expect(problem2 == null, problem2 ?? "");
                if (problem2 == null)
                {
                    CompareReplay(t, "Super Mario World", tr2, players2);
                    t.Expect(alloc2 == 0, $"{alloc2:N0} bytes allocated on the audio thread while the Super Mario World tracks played");
                    t.Note($"Super Mario World, recorded by the DLL (20 s of the overworld): {RText(g, 2)}");
                }
            }
            finally { foreach (var x in players2) x.P.Dispose(); }
        }
        finally { g.Test(HkRForget); }
    }

    // ------------------------------------------------------------------------------------------------ the capture on the audio thread

    private void RecordCapture(T t, string dir)
    {
        using var p = RecordGame(dir);
        try
        {
            p.SetParam(PChannel, 5);                                              // (the game's whole mix: the audio thread pulls the game frame by frame, which is where the capture runs)
            var buf = new float[512];
            void Render(int blocks) { fixed (float* f = buf) for (int i = 0; i < blocks; i++) { p.NewTick(); p.GenRender(f, 256); } }
            t.Expect(p.Test(HkRStart) == 0, "Record was refused: " + RText(p, 8));
            long log0 = p.Test(HkSpcLog);                                         // (the game has run no frame yet; Record hooked its log)
            long a0 = p.Test(HkRAlloc);                                           // (after Record: arming makes the capture's memory on the UI thread)
            Render(1800);                                                          // about 9.6 s at 48 kHz: the game's start, its key-on, the driver's bends and fades
            long a1 = p.Test(HkRAlloc);
            bool recording = p.Test(HkRState) == 2;
            p.Test(HkRunaway, 0, 1);                                              // the game frozen: no more writes while the stop is made
            long log1 = p.Test(HkSpcLog);
            p.Test(HkRStop);
            bool done = RWait(p, 4);
            p.Test(HkRunaway, 0, 0);
            t.Expect(recording && done, $"the capture did not run or end (recording {recording}, state {p.Test(HkRState)}: {RText(p, 1)})");
            t.Expect(a1 - a0 == 0, $"{a1 - a0:N0} bytes allocated on the audio thread while the game was recorded (1,800 blocks rendered after Record)");
            var s = RecSection.Read(RBytes(p, HkRSection), out var why);
            t.Expect(s != null, "the recording's section does not read: " + why);
            if (s == null) return;
            int markers = s.RegVals.Count(v => v >> 8 == RecSession.MarkerVersion);
            t.Expect(log0 >= 0 && s.Count - markers == log1 - log0 && s.Dropped == 0, $"the capture holds {s.Count - markers:N0} writes, the game's own log counted {log1 - log0:N0} in the same frames; {s.Dropped} lost");
            t.Expect(p.Test(HkRTracks) == 1, $"{p.Test(HkRTracks)} tracks from the game's one note");
            t.Note($"capture: {s.Seconds:0.0} s, {s.Count - markers:N0} writes (the game's log: {log1 - log0:N0}), {markers} instrument; {a1 - a0} bytes allocated on the audio thread over 1,800 blocks");
        }
        finally { p.Test(HkRunaway, 0, 0); p.Test(HkRForget); }
    }

    // ------------------------------------------------------------------------------------------------ the FL project

    private void RecordProject(T t, string dir)
    {
        byte[] section = SynthSection(8, out var session);
        byte[] state, midi;
        string facts;
        using (var p = Adopted(t, section))
        {
            if (p == null) return;
            midi = RBytes(p, HkRMidi); facts = RText(p, 2);
            state = p.SaveState();
            p.Test(HkRForget);
        }
        int at = FindSection(state, RecSection.Tag);
        t.Expect(at >= 0, "the project has no REC1 section");
        if (at < 0) return;
        int len = BitConverter.ToInt32(state, at + 4);
        var saved = state.AsSpan(at + 8, len).ToArray();
        t.Expect(saved.AsSpan().SequenceEqual(section), $"the project's REC1 ({saved.Length:N0} bytes) is not the recording's section ({section.Length:N0})");
        int gam = FindSection(state, 0x314D4147);                                   // "GAM1": REC1 goes before it (an older DLL skips both by their lengths)
        t.Expect(gam < 0 || gam > at, "REC1 is not before GAM1");

        string Reopen(byte[] project, out long tracks, out string problem, out byte[] again)
        {
            using var q = lib.Create();
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.LoadState(project);
            long until = Environment.TickCount64 + 60000;
            while (q.Test(HkRState) is 3 && Environment.TickCount64 < until) Win32Host.Pump(10);
            tracks = q.Test(HkRTracks); problem = RText(q, 7);
            again = q.SaveState();
            string m = q.Test(HkRState) == 4 ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(RBytes(q, HkRMidi))) + "|" + RText(q, 2) + "|" + RText(q, 6) : "";
            q.Test(HkRForget);
            return m;
        }

        // reopened in a fresh instance (the one that saved is gone): the same MIDI and facts, the presets not added by themselves
        string back = Reopen(state, out long tr0, out _, out _);
        string want = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(midi)) + "|" + facts + "|";
        t.Expect(back.StartsWith(want), $"the reopened project's recording ({tr0} tracks) transcribes to another MIDI file or other facts");
        t.Expect(back.EndsWith("press Add presets"), $"a reopened recording said about its presets: '{back.Split('|').Last()}'");
        // damaged: a byte of the stored data flipped
        var damaged = (byte[])state.Clone();
        damaged[at + 8 + RecSection.HeaderBytes + len / 3] ^= 0x10;
        Reopen(damaged, out long tr1, out string why1, out _);
        t.Expect(tr1 == -1 && why1.Contains("damaged"), $"a damaged REC1: tracks {tr1}, the window says '{why1}'");
        // changed: another recording's bytes (one pitch write moved by one) under the first one's checksum: only the checksum can tell
        int pw = Array.FindIndex(session.RegVals, v => (v >> 8 & 0x8F) == 0x02 && v >> 8 < 0x80);
        session.RegVals[pw] ^= 1;
        var other = RecSection.Build(session);
        BitConverter.GetBytes(BitConverter.ToUInt32(section, section.Length - 4)).CopyTo(other, other.Length - 4);
        var changed = state[..(at + 4)].Concat(BitConverter.GetBytes(other.Length)).Concat(other).Concat(state[(at + 8 + len)..]).ToArray();
        Reopen(changed, out long tr2, out string why2, out _);
        t.Expect(tr2 == -1 && why2.Contains("checksum"), $"a changed REC1 under the old checksum: tracks {tr2}, the window says '{why2}'");
        // newer: version 3 (with a good checksum; version 2 is a NES recording's since record-nes): refused with the reason, and written back as it came
        var newer = (byte[])state.Clone();
        newer[at + 8] = 3;
        uint crc = BrokenNes2.Music.Crc32.Of(newer.AsSpan(at + 8, len - 4));
        BitConverter.TryWriteBytes(newer.AsSpan(at + 8 + len - 4), crc);
        Reopen(newer, out long tr3, out string why3, out byte[] again3);
        int at3 = FindSection(again3, RecSection.Tag);
        t.Expect(tr3 == -1 && why3.Contains("newer"), $"a newer REC1: tracks {tr3}, the window says '{why3}'");
        t.Expect(at3 >= 0 && again3.AsSpan(at3 + 8, BitConverter.ToInt32(again3, at3 + 4)).SequenceEqual(newer.AsSpan(at + 8, len)), "a newer REC1 was not written back as it came");
        // absent: nothing said
        var none = state[..at].Concat(state[(at + 8 + len)..]).ToArray();
        string nothing = Reopen(none, out long tr4, out string why4, out _);
        t.Expect(tr4 == -1 && why4 == "" && nothing == "", $"a project without REC1: tracks {tr4}, problem '{why4}'");
        t.Note($"project: a {section.Length:N0}-byte REC1 reopened to the same {midi.Length:N0}-byte MIDI file; damaged ('{why1}'), changed under the old checksum ('{why2}') and newer ('{why3}') sections refused");
    }

    // ------------------------------------------------------------------------------------------------ the tracks given to new instances

    private const int HkOffer = 700, HkOfferSet = 701, HkTrackOf = 702, HkBindState = 703, HkBindHow = 704, HkConfigured = 705, HkMatchNotes = 706, HkPick = 708, HkTrackStatus = 709, HkLog = 710,
        HkEngineWaits = 712, HkPlaying = 713, HkInstanceNo = 714, HkMatchHow = 715;

    /// <summary>One FL channel in the replace scenario: an instance, the notes its piano roll holds (one track of the imported file, or other notes), where FL says they are.</summary>
    private sealed class ReplacePart
    {
        public required string Name;
        public required PluginInstance P;
        public required List<MidiReplay.Note> Notes;
        public int Track = -1;                  // the track whose notes these are (-1: other notes)
        public int Expect = -1;                 // the track it must end up playing (-1 none)
        public double BeatOffset;               // the song as FL places it, against the file
        public bool NoPosition;                 // FL gives no song position
        public int FlBends;                     // what FL's channel kept of the file's expression: 0 the bends at the file's range and controller 7, 1 nothing, 2 the bends read at 2 semitones and no controller
        public long[] BendT = [], CcT = []; public int[] BendV = [], CcV = []; public int Range = 2;
        public readonly List<nint> Handles = new();
        public long BoundAtFirst = -2;          // the track it plays right after its first note was started (before it was rendered)
        public int Next;
        public MidiReplay? M;                   // the file its notes come from (its own tempo map)
        public long Tick;
    }

    /// <summary>Plays the parts' notes the way FL plays a project: one timeline in 64-sample blocks at 48 kHz, each instance asked for its notes when their time comes, with FL's song position of the block
    /// (plus the part's offset; none when the part has none) and the channel's bend and controller 7 from the file. Returns the bytes the DLL allocated on this (the audio) thread while the notes played.</summary>
    private long PlayReplace(List<ReplacePart> parts, MidiReplay m)
    {
        const int rate = 48000, block = 64;
        foreach (var pt in parts) pt.M ??= m;
        double end = parts.Select(x => x.Notes.Count == 0 ? 0 : x.M!.Seconds(x.Notes.Max(n => n.Off))).DefaultIfEmpty(0).Max() + 0.3;
        var buf = new float[block * 2];
        var active = new List<(ReplacePart Pt, MidiReplay.Note N, SimVoice V)>();
        long a0, a1;
        fixed (float* bp = buf)
        {
            for (int w = 0; w < 40; w++) foreach (var pt in parts) { pt.P.NewTick(); pt.P.GenRender(bp, block); }      // (warm-up: each instance's first blocks)
            a0 = parts[0].P.Test(HkRAlloc);
            for (long s = 0; s < end * rate; s += block)
            {
                foreach (var pt in parts) pt.Tick = pt.M!.TickAt(s / (double)rate);
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    var (pt, n, v) = active[i];
                    if (n.Off <= pt.Tick) { pt.P.Host.ReleaseFromMixer(v); active.RemoveAt(i); }
                }
                foreach (var pt in parts)
                {
                    pt.P.Host.SongBeats = pt.NoPosition ? double.NaN : pt.Tick / (double)pt.M!.Ppq + pt.BeatOffset;
                    while (pt.Next < pt.Notes.Count && pt.Notes[pt.Next].On <= pt.Tick)
                    {
                        var n = pt.Notes[pt.Next++];
                        var v = pt.P.Host.NewVoice(n.Channel, n.Velocity / 127f);
                        ReplaceLevels(v, n, pt, pt.Tick);
                        v.Handle = pt.P.TriggerVoice(v.Params, v.Tag);
                        pt.Handles.Add(v.Handle);
                        if (pt.BoundAtFirst == -2) pt.BoundAtFirst = pt.P.Test(HkTrackOf);
                        active.Add((pt, n, v));
                    }
                }
                foreach (var (pt, n, v) in active) ReplaceLevels(v, n, pt, pt.Tick);
                foreach (var pt in parts) { pt.P.NewTick(); pt.P.GenRender(bp, block); }
            }
            a1 = parts[0].P.Test(HkRAlloc);
            foreach (var (pt, _, v) in active) pt.P.Host.ReleaseFromMixer(v);
            for (int w = 0; w < 8; w++) foreach (var pt in parts) { pt.P.NewTick(); pt.P.GenRender(bp, block); }
        }
        return a1 - a0;

        static void ReplaceLevels(SimVoice v, MidiReplay.Note n, ReplacePart pt, long tick)
        {
            int bend = pt.FlBends == 1 ? 0 : ValueAt(pt.BendT, pt.BendV, tick, 0), cc = pt.FlBends != 0 ? 100 : ValueAt(pt.CcT, pt.CcV, tick, 100);
            int range = pt.FlBends == 2 ? 2 : pt.Range;
            ref var f = ref v.Params->FinalLevels;
            f.Pitch = (float)((n.Key - 60) * 100 + bend * range * 100.0 / 8192.0);
            f.Vol = (float)(n.Velocity / 127.0 * cc / 100.0);
            f.Pan = 0;
            v.Params->InitLevels = f;
        }
    }

    private ReplacePart Part(string name, PluginInstance q, MidiReplay m, int track, int expect, Func<MidiReplay.Note, bool>? which = null, int keyShift = 0)
    {
        var notes = m.Notes.Where(n => n.Track == track + 1 && (which == null || which(n))).Select(n => keyShift == 0 ? n : new MidiReplay.Note { Track = n.Track, Channel = n.Channel, Key = Math.Clamp(n.Key + keyShift, 0, 127), Velocity = n.Velocity, On = n.On, Off = n.Off, Index = n.Index }).ToList();
        var chan = notes.Count > 0 ? m.Of(track + 1, notes[0].Channel) : new MidiReplay.Chan();
        q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        q.Test(HkRProbe, 0, 400_000);
        return new ReplacePart
        {
            Name = name, P = q, Notes = notes, M = m, Track = keyShift == 0 ? track : -1, Expect = expect, Range = chan.Range >= 0 ? chan.Range : 2,
            BendT = chan.Bends.Select(b => b.Tick).ToArray(), BendV = chan.Bends.Select(b => b.Bend).ToArray(), CcT = chan.Cc7.Select(b => b.Tick).ToArray(), CcV = chan.Cc7.Select(b => b.Cc).ToArray(),
        };
    }

    /// <summary>A replace part as the register comparison reads it: its notes in order, the handle of each, everything its sampler wrote.</summary>
    private static ReplayPlayer ToPlayer(ReplacePart pt)
    {
        var pl = new ReplayPlayer { P = pt.P, Notes = pt.Notes };
        for (int i = 0; i < pt.Handles.Count; i++) pl.NoteOfHandle[(int)(pt.Handles[i] & 0xFFFF)] = i;
        int count = (int)pt.P.Test(HkRProbeRead, 0, 0);
        var raw = new byte[Math.Max(1, count) * 12];
        int got;
        fixed (byte* rp = raw) got = (int)pt.P.Test(HkRProbeRead, count, (nint)rp);
        for (int i = 0; i < got; i++) pl.Probe.Add((BitConverter.ToInt64(raw, i * 12), BitConverter.ToInt32(raw, i * 12 + 8)));
        return pl;
    }

    /// <summary>The probe's marks of an instance: the FL voice handles its sampler gave an S-DSP voice to, in order.</summary>
    private static List<int> ProbeMarks(PluginInstance q)
    {
        int count = (int)q.Test(HkRProbeRead, 0, 0);
        var raw = new byte[Math.Max(1, count) * 12];
        int got;
        fixed (byte* rp = raw) got = (int)q.Test(HkRProbeRead, count, (nint)rp);
        var marks = new List<int>();
        for (int i = 0; i < got; i++) { int code = BitConverter.ToInt32(raw, i * 12 + 8); if (code >> 24 == 1) marks.Add(code & 0xFFFF); }
        return marks;
    }

    /// <summary>FL's "import the MIDI file, then Replace each MIDI Out channel by BrokenNes2", every way FL plausibly creates the replacing instance: each untouched instance recognises its track from
    /// its notes and plays that track's preset from its first note. Also: an instance made before the tracks were offered, playback started in the middle, the song placed 16 bars later, no song position
    /// from FL, a channel of other notes (never taken), "Play here" by hand (real mouse), the project, withdrawing the offer, the host log, 0 bytes on the audio thread.</summary>
    private void RecordReplaceScenario(T t, string dir)
    {
        var made = new List<PluginInstance>();
        PluginInstance? p = null, p2 = null;
        string logFile = Path.Combine(dir, "host-log-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        string? oldLog = Environment.GetEnvironmentVariable("BROKENNES_HOSTLOG_FILE");
        Environment.SetEnvironmentVariable("BROKENNES_HOSTLOG_FILE", logFile);
        try
        {
            using (var h = lib.Create()) h.Test(HkLog, 0, 1);
            // D: made BEFORE the tracks are offered (FL may make the instances first)
            var d = lib.Create(); made.Add(d);
            byte[] section = SynthSection(20, out var session);
            p = Adopted(t, section);
            if (p == null) return;
            long until = Environment.TickCount64 + 20000;
            while (p.Test(HkOffer) <= 0 && Environment.TickCount64 < until) Thread.Sleep(10);
            int tracks = (int)p.Test(HkOffer);
            t.Expect(tracks >= 4, $"the recording's tracks are not on offer ({tracks})");
            if (tracks < 4) return;
            var m = MidiReplay.Read(RBytes(p, HkRMidi));
            // a second recording, on another emulator, on offer at the same time
            p2 = Adopted(t, SynthSection(20, out _, seed: 11, name: "Second take", id: 0xCD), emulator: 5);
            if (p2 == null) return;
            until = Environment.TickCount64 + 20000;
            while (p.Test(HkOffer) < tracks + 2 && Environment.TickCount64 < until) Thread.Sleep(10);
            t.Expect(p.Test(HkOffer) > tracks, $"the second recording's tracks are not on offer beside the first's ({p.Test(HkOffer)} tracks)");
            var m2 = MidiReplay.Read(RBytes(p2, HkRMidi));
            // the replacing instances: an empty state, a fresh instance's own state, every parameter pushed at its default
            byte[] fresh;
            using (var f0 = lib.Create()) fresh = f0.SaveState();
            var a = lib.Create(); made.Add(a); a.LoadState([]);
            var b = lib.Create(); made.Add(b); b.LoadState(fresh);
            var c = lib.Create(); made.Add(c);
            var defaults = new[] { 0, 1000, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0 };
            for (int i = 0; i < defaults.Length; i++) c.SetParam(i, defaults[i]);
            t.Expect(new[] { a, b, c, d }.All(q => q.Test(HkConfigured) == 0), $"an instance made by FL counts as configured before any note: empty state {a.Test(HkConfigured)}, its own fresh state {b.Test(HkConfigured)}, default parameters {c.Test(HkConfigured)}, made before the offer {d.Test(HkConfigured)}");
            // more channels: playback from the middle, the song 16 bars later, no song position, other notes at the same times
            var e = lib.Create(); made.Add(e);
            var g = lib.Create(); made.Add(g);
            var hq = lib.Create(); made.Add(hq);
            var fq = lib.Create(); made.Add(fq);
            var kq = lib.Create(); made.Add(kq);
            var k2 = lib.Create(); made.Add(k2);
            var lq = lib.Create(); made.Add(lq);
            long midTick = m.Notes.Where(n => n.Track == 2).Select(n => n.On).OrderBy(x => x).ElementAt(m.Notes.Count(n => n.Track == 2) / 2);
            double fileBpm = 60e6 / m.Tempo[0].Us;
            p.Test(HkRTempo, 0, (nint)Math.Round(fileBpm * 1000));                 // (FL's tempo is the file's: FL took it from the import)
            var parts = new List<ReplacePart>
            {
                Part("A (empty state)", a, m, 2, 2),
                Part("B (its own fresh state)", b, m, 0, 0),
                Part("C (default parameters pushed)", c, m, 3, 3),
                Part("D (made before the offer)", d, m, 1, 1),
                Part("E (playback from the middle)", e, m, 1, 1, n => n.On >= midTick),
                Part("G (the song 16 bars later)", g, m, 0, 0),
                Part("H (no song position)", hq, m, 3, 3),
                Part("F (other notes at the same times)", fq, m, 0, -1, null, 37),
                Part("K (FL lost the bends and the volume)", kq, m, 0, 0),
                Part("K2 (FL read the bends at 2 semitones)", k2, m, 2, 2),
                Part("L (a track of the second recording)", lq, m2, 1, 1),
            };
            parts[5].BeatOffset = 64;
            parts[6].NoPosition = true;
            parts[8].FlBends = 1;
            parts[9].FlBends = 2;
            long alloc = PlayReplace(parts, m);
            until = Environment.TickCount64 + 5000;
            while (parts.Any(x => x.Expect >= 0 && x.P.Test(HkBindState) != 2) && Environment.TickCount64 < until) Thread.Sleep(20);
            foreach (var pt in parts)
            {
                long track = pt.P.Test(HkTrackOf), bstate = pt.P.Test(HkBindState), how = pt.P.Test(HkMatchHow);
                if (pt.Expect < 0)
                {
                    t.Expect(track == -1 && bstate == 0 && pt.P.Test(342) == 0, $"{pt.Name}: taken by track {track + 1} (state {bstate}): an unrelated channel must keep its own sound");
                    continue;
                }
                t.Expect(track == pt.Expect && bstate == 2 && pt.P.Test(HkBindHow) == 1 && pt.P.Test(342) == 1 && pt.P.Test(352) == 1,
                    $"{pt.Name}: plays track {track + 1} (should be {pt.Expect + 1}), state {bstate} (2 = settled on the tracks' emulator), found {(how == 2 ? "by an offset" : "aligned")}, holds a preset {pt.P.Test(342)}, preset mode {pt.P.Test(352)}: {DllText(pt.P, HkTrackStatus)}");
                var marks = ProbeMarks(pt.P);
                bool firstByPreset = marks.Count > 0 && pt.Handles.Count > 0 && marks[0] == (int)(pt.Handles[0] & 0xFFFF);
                bool expectFirst = pt.BeatOffset == 0 && !pt.NoPosition;
                if (expectFirst) t.Expect(firstByPreset && pt.BoundAtFirst == pt.Expect, $"{pt.Name}: its first note was not played by the track's preset (bound right after the note started: {pt.BoundAtFirst + 1}; the preset's engine first played FL voice {(marks.Count > 0 ? marks[0] : -1)}, the first note was {(pt.Handles.Count > 0 ? pt.Handles[0] & 0xFFFF : -1)})");
                int played = pt.Handles.Count(hd => marks.Contains((int)(hd & 0xFFFF)));
                int expectPlayed = expectFirst ? pt.Handles.Count : pt.Handles.Count - TrackMatcher.ShiftedNotes + 1;
                t.Expect(played >= expectPlayed, $"{pt.Name}: {played} of its {pt.Handles.Count} notes played by the track's preset (at least {expectPlayed} expected)");
            }
            t.Expect(alloc == 0, $"{alloc:N0} bytes allocated on the audio thread while {parts.Count} channels played their notes (the instances recognising their tracks and taking their engines included)");
            // the expression: channels whose FL events lost the bends and the volume, or read the bends at 2 semitones, still write the game's own registers (they play the recording's expression)
            var tr = RecTranscribe.Run(session, new RecOptions { Name = session.Name, FlTempo = 120 });
            foreach (int pi in new[] { 8, 9 })
            {
                var pt = parts[pi];
                t.Expect(pt.P.Test(718) == 1, $"{pt.Name}: does not play its track's expression");
                CompareReplay(t, pt.Name, tr, [ToPlayer(pt)], [pt.Expect]);
            }
            int onTrack1 = (int)p.Test(HkPlaying, 1);
            t.Expect(onTrack1 == 2, $"{onTrack1} channels counted on track 2 (D and E play it)");
            t.Note($"replace: {string.Join("; ", parts.Select(x => $"{x.Name} -> {(x.P.Test(HkTrackOf) is var k && k >= 0 ? $"track {k + 1}" : "none")}"))}; 0 bytes on the audio thread: {alloc}");
            t.Expect(DllText(lq, HkTrackStatus).Contains("Second take"), $"L plays a track of another recording than the second: {DllText(lq, HkTrackStatus)}");

            // "Play here" by hand, in the Record window of a fresh instance (real mouse messages)
            var iq = lib.Create(); made.Add(iq);
            nint parent = Win32Host.CreateParent();
            try
            {
                iq.ShowEditor(parent);
                Win32Host.Pump(60);
                ClickControl(t, iq, 2020, "Record button");
                ClickControl(t, iq, 2052, "Play here, row 3");
                t.Expect(iq.Test(HkTrackOf) == 2 && iq.Test(HkBindHow) == 2 && iq.Test(342) == 1 && iq.Test(352) == 1, $"Play here: plays track {iq.Test(HkTrackOf) + 1} (how {iq.Test(HkBindHow)}), holds a preset {iq.Test(342)}, preset mode {iq.Test(352)} ({EdText(iq, EdRMessage)})");
                iq.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }

            // the project: A saved, the instance gone, a fresh one opened from it plays the same track
            var state = a.SaveState();
            int trk = FindSection(state, 0x314B5254);
            t.Expect(trk >= 0, "the project has no TRK1 for A");
            a.Dispose(); made.Remove(a);
            var a2 = lib.Create(); made.Add(a2);
            a2.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            a2.LoadState(state);
            t.Expect(a2.Test(HkTrackOf) == 2 && a2.Test(HkBindHow) == 1 && a2.Test(342) == 1 && a2.Test(352) == 1 && a2.Test(HkConfigured) == 1 && a2.Test(718) == 1,
                $"A reopened: plays track {a2.Test(HkTrackOf) + 1} (how {a2.Test(HkBindHow)}), holds a preset {a2.Test(342)}, preset mode {a2.Test(352)}, configured {a2.Test(HkConfigured)}, the track's expression {a2.Test(718)}");

            // withdrawn: an untouched instance playing a track's notes keeps its own sound
            p.Test(HkRDeal, 0, 0); p2.Test(HkRDeal, 0, 0);
            t.Expect(p.Test(HkOffer) == -1, "the tracks are still on offer after Stop");
            var jq = lib.Create(); made.Add(jq);
            var jpart = Part("J (after the offer was withdrawn)", jq, m, 0, -1);
            PlayReplace([jpart], m);
            t.Expect(jq.Test(HkTrackOf) == -1, $"after the offer was withdrawn an untouched channel took track {jq.Test(HkTrackOf) + 1}");

            // the host log: the calls an instance received, its notes and positions, how it found its track
            Thread.Sleep(600);
            string log = File.Exists(logFile) ? File.ReadAllText(logFile) : "";
            long an = d.Test(HkInstanceNo);
            t.Expect(log.Contains($"#{an} created") && log.Contains("state restored") && log.Contains($"#{an} note #1:") && log.Contains($"#{an} match after note #1") && log.Contains("settled on emulator"),
                $"the host log ({log.Length:N0} chars at {logFile}) lacks a creation, a restored state, D's first note or its match, or a settling");
            t.Note($"host log: {log.Split('\n').Length} lines");
            if (Environment.GetEnvironmentVariable("BROKENNES_KEEP_HOSTLOG") is { Length: > 0 } keep && !recordMutantPass) File.WriteAllText(keep, log);      // (to read what the log looks like)
        }
        finally
        {
            using (var h = lib.Create()) h.Test(HkLog, 0, 0);
            Environment.SetEnvironmentVariable("BROKENNES_HOSTLOG_FILE", oldLog);
            foreach (var q in made) q.Dispose();
            if (p != null) { p.Test(HkRDeal, 0, 0); p.Test(HkRForget); p.Dispose(); }
            if (p2 != null) { p2.Test(HkRDeal, 0, 0); p2.Test(HkRForget); p2.Dispose(); }
        }
    }

    // ------------------------------------------------------------------------------------------------ FL's transport

    private void RecordTransportScenario(T t, string dir)
    {
        using var p = RecordGame(dir);
        try
        {
            p.SetParam(PChannel, 5);
            var buf = new float[512];
            void Render(int blocks) { fixed (float* f = buf) for (int i = 0; i < blocks; i++) { p.NewTick(); p.GenRender(f, 256); } }
            p.Dispatcher(Fpd.SetPlaying, 0, 0);
            p.Test(HkROptions, 0, 3);                                             // start with FL's Play, stop with FL's Stop
            t.Expect(p.Test(HkRStart) == 0 && p.Test(HkRState) == 1, $"Record with 'start with FL's Play': state {p.Test(HkRState)}");
            Render(300);                                                           // FL renders a generator while stopped: the game runs (and keys its one note) but is not recorded
            t.Expect(p.Test(HkRState) == 1 && p.Test(HkRWrites) == 0, $"before FL's Play the recording began: state {p.Test(HkRState)}, {p.Test(HkRWrites)} writes");
            p.Dispatcher(Fpd.SetPlaying, 0, 1);
            Render(600);
            long writes = p.Test(HkRWrites);
            t.Expect(p.Test(HkRState) == 2 && writes > 100, $"after FL's Play: state {p.Test(HkRState)}, {writes} writes");
            p.Dispatcher(Fpd.SetPlaying, 0, 0);
            t.Expect(RWait(p, 4, 60000, render: true), $"FL's Stop did not end the recording (state {p.Test(HkRState)}: {RText(p, 1)})");
            // the note was keyed before FL's Play: the recording holds its bends but no note, and says so
            t.Expect(p.Test(HkRTracks) == 0 && RText(p, 1).Contains("no note") && RText(p, 2).Contains("already sounding"), $"a recording with no key-on: {p.Test(HkRTracks)} tracks, '{RText(p, 1)}'");
            t.Note($"transport: armed until FL's Play, {writes} writes after it, ended by FL's Stop; then: '{RText(p, 1)}'");
        }
        finally { p.Test(HkROptions, 0, ROptDefault); p.Test(HkRForget); }
    }

    // ------------------------------------------------------------------------------------------------ live MIDI out (P2)

    private const int HkLive = 680, HkLiveState = 681, HkLiveCount = 682, HkLivePort = 683, HkLiveRoot = 684, HkLiveChannel = 685, HkLiveText = 687, HkLiveRoots = 688, HkLiveDrain = 689;

    private static bool LiveWait(PluginInstance p, int state, int ms = 20000)
    {
        long until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until) { if (p.Test(HkLiveState) == state) return true; Thread.Sleep(5); }
        return p.Test(HkLiveState) == state;
    }

    private static int[] LiveRoots(PluginInstance p)
    {
        var roots = new int[100_000];
        int n;
        fixed (int* rp = roots) n = (int)p.Test(HkLiveRoots, roots.Length, (nint)rp);
        return roots[..Math.Max(0, n)];
    }

    /// <summary>Live MIDI out through the DLL: switched on (FL asked to enable MIDI out), the game recorded at the same time; the messages FL received (MIDIOut_Delayed, the port in each) read back against the
    /// recording's own writes; switched off: every note ends; nothing allocated on the audio thread. The synthetic game (one note keyed while the root of a sample loaded after the start is not known yet)
    /// and Super Mario World (roots read before it starts; Instrument Runaway ends the notes).</summary>
    private void RecordLiveScenario(T t, string dir)
    {
        using (var p = RecordGame(dir))
        {
            try
            {
                p.SetParam(PChannel, 5);
                var buf = new float[512];
                void Render(int blocks) { fixed (float* f = buf) for (int i = 0; i < blocks; i++) { p.NewTick(); p.GenRender(f, 256); } }
                lock (p.Host.MidiOutLog) p.Host.MidiOutLog.Clear();
                lock (p.Host.DispatcherCalls) p.Host.DispatcherCalls.Clear();
                p.Test(HkLivePort, 0, 3);
                t.Expect(p.Test(HkLive, 0, 1) == 0, "live MIDI out was refused: " + RText(p, 8));
                t.Expect(LiveWait(p, 1), $"live MIDI out did not start (state {p.Test(HkLiveState)})");
                p.Test(HkLiveRoots, 100_000, 0);
                bool activated; lock (p.Host.DispatcherCalls) activated = p.Host.DispatcherCalls.Contains(Fhd.ActivateMidi);
                t.Expect(activated, "FL was not asked to enable its MIDI output (FHD_ActivateMIDI)");
                t.Expect(p.Test(HkRStart) == 0 && RWait(p, 2, 10000), $"the recording beside it did not begin: {RText(p, 8)}");
                long a0 = p.Test(HkRAlloc);
                Render(1100);                                                       // about 5.9 s: the game's upload, its key-on, the driver's bends and fades
                long a1 = p.Test(HkRAlloc);
                p.Test(HkRStop);
                t.Expect(RWait(p, 4), "the recording beside the live out did not end");
                var roots = LiveRoots(p);
                int rootNow = (int)p.Test(HkLiveRoot, 0);
                p.Test(HkLive, 0, 0);
                Render(4);                                                          // (the note-offs go with the next blocks)
                t.Expect(p.Test(HkLiveState) == -1, $"live MIDI out did not let go after it was switched off (state {p.Test(HkLiveState)})");
                List<(uint Msg, bool Delayed)> log; lock (p.Host.MidiOutLog) log = p.Host.MidiOutLog.ToList();
                t.Expect(log.Count > 0 && log.All(x => x.Delayed && x.Msg >> 24 == 3), $"{log.Count} MIDI messages, {log.Count(x => !x.Delayed)} not through MIDIOut_Delayed, {log.Count(x => x.Msg >> 24 != 3)} not on port 3");
                var s = RecSection.Read(RBytes(p, HkRSection), out var why);
                t.Expect(s != null, "the recording does not read: " + why);
                if (s != null)
                {
                    var notes = LiveChecks.Parse(log.Select(x => x.Msg), out int range, out var problems);
                    foreach (var pr in problems.Take(3)) t.Expect(false, "the stream: " + pr);
                    var st = LiveChecks.Compare(LiveChecks.Truth(s), notes, roots, range < 0 ? RecLive.DefaultRange : range);
                    foreach (var pr in st.Problems.Take(4)) t.Expect(false, "synthetic game: " + pr);
                    t.Expect(st.AllGood && st.Keys == 1, "synthetic game: " + st.Line());
                    t.Note("synthetic game: " + st.Line());
                }
                // the sample was loaded after the live out started: its one note was keyed as if its rate were C5 (counted), and the worker found its root after the key-on: the 2 kHz square
                double want = 6900 + 1200 * Math.Log2(2000.0 / 440);
                t.Expect(roots.Length == 1 && roots[0] == 6000 && Math.Abs(rootNow - want) <= 5, $"the root: the note was keyed with {(roots.Length > 0 ? roots[0] / 100.0 : -1):0.00}, the worker then found {rootNow / 100.0:0.00} (the sample is {want / 100:0.00})");
                t.Expect(a1 - a0 == 0, $"{a1 - a0:N0} bytes allocated on the audio thread with the live out and a recording on");
            }
            finally { p.Test(HkLive, 0, 0); p.Test(HkRForget); }
        }
        // Super Mario World: the roots of the samples loaded when it starts; many instruments; Instrument Runaway ends the sounding notes
        if (recordMutantPass) return;
        if (!File.Exists(ImodelGames.Smw)) { t.Warn(false, "NOT RUN: Super Mario World is not at " + ImodelGames.Smw + ": live MIDI out was checked on the synthetic game only"); return; }
        using var g = SegaGame(ImodelGames.Smw, 2, "SNES");
        try
        {
            var pad = InjectRun.Pad(InjectRun.SmwScript);
            DriveFrames(g, pad, 0, 3800);
            lock (g.Host.MidiOutLog) g.Host.MidiOutLog.Clear();
            g.Test(HkLivePort, 0, 2);
            t.Expect(g.Test(HkLive, 0, 1) == 0 && LiveWait(g, 1), "live MIDI out on Super Mario World did not start: " + RText(g, 8));
            g.Test(HkLiveRoots, 100_000, 0);
            g.Test(HkROptions, 0, ROptDefault);
            t.Expect(g.Test(HkRStart) == 0 && RWait(g, 2, 10000), "the recording beside it did not begin: " + RText(g, 8));
            for (int f = 3800; f < 3800 + 1200; f += 10) { DriveFrames(g, pad, f, f + 10); g.Test(HkLiveDrain); }
            g.Test(HkRStop);
            t.Expect(RWait(g, 4, 120000), "the recording beside the live out did not end");
            var roots = LiveRoots(g);
            long unknown = g.Test(HkLiveCount, 2 + 3);
            string describe = EdText(g, HkLiveText);
            // Instrument Runaway freezes the game: its sounding notes end
            g.Test(HkRunaway, 0, 1);
            var buf = new float[512];
            fixed (float* fb = buf) { g.NewTick(); g.GenRender(fb, 256); }
            List<(uint Msg, bool Delayed)> log; lock (g.Host.MidiOutLog) log = g.Host.MidiOutLog.ToList();
            g.Test(HkRunaway, 0, 0);
            var s = RecSection.Read(RBytes(g, HkRSection), out var why);
            t.Expect(s != null, "the Super Mario World recording does not read: " + why);
            if (s == null) return;
            var notes = LiveChecks.Parse(log.Select(x => x.Msg), out int range, out var problems);
            foreach (var pr in problems.Take(3)) t.Expect(false, "Super Mario World stream: " + pr);
            t.Expect(log.All(x => x.Delayed && x.Msg >> 24 == 2), "Super Mario World: messages not through MIDIOut_Delayed on port 2");
            var st = LiveChecks.Compare(LiveChecks.Truth(s), notes, roots, range < 0 ? RecLive.DefaultRange : range);
            foreach (var pr in st.Problems.Take(4)) t.Expect(false, "Super Mario World: " + pr);
            t.Expect(st.AllGood && st.Keys > 100, "Super Mario World: " + st.Line() + " (the hanging ones after Instrument Runaway froze the game)");
            t.Expect(unknown == 0, $"Super Mario World: {unknown} notes keyed before their sample's root was known (the roots are read before the live out starts)");
            t.Note($"Super Mario World (20 s of the overworld, live out on port 2): {st.Line()}; {describe}");
        }
        finally { g.Test(HkLive, 0, 0); g.Test(HkRunaway, 0, 0); g.Test(HkRForget); }
    }

    // ------------------------------------------------------------------------------------------------ what is refused

    private void RecordRefusals(T t, string dir)
    {
        using (var p = lib.Create())
        {
            p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            p.SetParam(PEmulator, 5);
            p.Test(HkRForget);
            t.Expect(p.Test(HkRStart) == 1 && RText(p, 8).Contains("ROM mode"), $"Record in Direct mode: '{RText(p, 8)}'");
            p.SetParam(PConsole, 1);                                         // (a Game Boy: the NES records since record-nes, see selftest record-nes)
            p.SetParam(PMode, 1);
            t.Expect(p.Test(HkRStart) == 1 && RText(p, 8).Contains("SNES and NES games"), $"Record on a Game Boy: '{RText(p, 8)}'");
            p.SetParam(PMode, 0);
            p.SetParam(PConsole, 0);
            string f = Path.Combine(dir, "nothing.mid");
            fixed (char* c = f + "\0") t.Expect(p.Test(HkRSave, 0, (nint)c) == 1 && RText(p, 8).Contains("Nothing to save") && !File.Exists(f), $"Save with nothing recorded: '{RText(p, 8)}'");
            t.Expect(p.Test(HkRDeal, 0, 1) == 1 && RText(p, 8).Contains("Record first"), $"Give the tracks with nothing recorded: '{RText(p, 8)}'");
        }
        // Save MIDI writes the whole file (the deliberate bug writes half of it)
        byte[] section = SynthSection(6, out _);
        using (var p = Adopted(t, section))
        {
            if (p == null) return;
            string f = Path.Combine(dir, "save.mid");
            fixed (char* c = f + "\0") p.Test(HkRSave, 0, (nint)c);
            var want = RBytes(p, HkRMidi);
            t.Expect(File.Exists(f) && File.ReadAllBytes(f).AsSpan().SequenceEqual(want), $"Save MIDI wrote {(File.Exists(f) ? new FileInfo(f).Length : 0)} bytes, the recording's file is {want.Length}");
            p.Test(HkRForget);
        }
    }
}
