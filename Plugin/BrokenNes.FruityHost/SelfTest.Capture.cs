// Round 9 (capture): "Capture from FL" through the real plugin DLL (Native AOT), played by the FL-faithful host (Capture.FlHost.cs: the real log's Dispatcher sequence, notes in FL ticks, InitLevels
// from MIDI note 0, FinalLevels from C5, slides inside their parent note, per-note pitch automation, no song position). The library checks first (CaptureChecks), then the DLL:
//
//   flow     the window with real mouse messages: Capture from FL arms, FL plays the song on four BrokenNes2 channels (Direct mode), FL's Stop builds the file, Test MIDI export plays it at once in
//            the same emulator, the facts say what VRUN's engine drops, Save MIDI... writes the file, the MIDI-out box
//   file     the captured file read by the independent parser: every note FL played (key, velocity, start and end within 1 tick at 960 per quarter, the channel VRUN's converter expects), the
//            pitch FL played at every tick within the bend's 14-bit step, the smallest bend range, the end at the bar line; at FL's own 44.1 kHz / 130 BPM without FPE_Tempo too; and the pitch
//            registers the instance's own chip wrote are those the file's pitch gives through Direct mode's arithmetic (or within a rounding edge)
//   engine   the song Test MIDI export built from the take: the ROM is the library's for those bytes, the plugin's machine plays the converter's streams frame by frame, the streams and loop are
//            those of the ideal file written from the script (the converter drops the bends: counted)
//   render   FL's Export (PM_IsRendering, another sample rate than the live one): the same file
//   alloc    0 bytes allocated on the audio thread while four channels are captured (live MIDI out on too)
//   live     "Send my notes and pitch bends as MIDI out": every note FL played and the played pitch at every tick, through MIDIOut_Delayed on port 1, channels 1/2/3/10
//   edge     nothing played, cancel, a channel FL never started (joins late, said), a note held over Stop (said), arming again puts the emulator back in Direct mode
//
// Each scenario is run again with a deliberate bug switched on in the DLL (plugin hook 799) and must fail. Plugin hooks 790-809 (Bn2Plugin.Capture.cs), editor hooks 810-819 (Bn2Editor.Capture.cs).
using BrokenNes.Fruity;
using BrokenNes2.Capture;
using BrokenNes2.Music;
using BrokenNes2.Record;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkCArm = 790, HkCCancel = 791, HkCState = 792, HkCText = 793, HkCMidi = 794, HkCSave = 795, HkCNum = 796, HkCLive = 798, HkCMutant = 799, HkCReset = 800, HkCClock = 801;
    private const int EdCText = 810, EdCSavePath = 811, EdCShown = 812, EdCFacts = 813;
    private const int IdCapArmBtn = 2120, IdCapSaveBtn = 2121, IdCapLiveBtn = 2122;
    private const int CapEmu = 7;

    private static string CText(PluginInstance p, int field)
    {
        var c = new char[8000];
        fixed (char* cp = c) { long n = p.Test(HkCText, (field << 16) | 0xFFFF & c.Length, (nint)cp); return n < 0 ? "" : new string(c, 0, (int)Math.Min(n, c.Length)); }
    }

    private static string CEdText(PluginInstance p)
    {
        var c = new char[8000];
        fixed (char* cp = c) { long n = p.Test(EdCText, c.Length, (nint)cp); return n < 0 ? "" : new string(c, 0, (int)Math.Min(n, c.Length)); }
    }

    private static byte[] CMidi(PluginInstance p)
    {
        long n = p.Test(HkCMidi, 0, 0);
        if (n <= 0) return [];
        var b = new byte[n];
        fixed (byte* bp = b) p.Test(HkCMidi, (nint)n, (nint)bp);
        return b;
    }

    /// <summary>A fresh FL process as far as the capture knows: no take, no timing heard (a throwaway instance asks; it is gone before the rack is made).</summary>
    private void CapFresh()
    {
        using var probe = lib.Create();
        probe.Test(HkCReset);
    }

    /// <summary>Four BrokenNes2 channels made the way FL makes them (the log's sequence), on emulator #<see cref="CapEmu"/>, Direct mode: Pulse 1, Pulse 2, Triangle, Noise.</summary>
    private List<PluginInstance> CapRack(FlRun run, FlSong s, int count = 4)
    {
        var rack = new List<PluginInstance>();
        for (int c = 0; c < count; c++)
        {
            var p = lib.Create();
            run.Create(p, s);
            p.SetParam(PEmulator, CapEmu);
            p.SetParam(PChannel, (c & 3) + 1);
            rack.Add(p);
        }
        return rack;
    }

    /// <summary>Waits (without rendering) for the take to be built by the capture's worker.</summary>
    private static bool CapDone(PluginInstance p, int ms = 20000)
    {
        long until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until) { if (p.Test(HkCState) == CaptureSession.Done) return true; Thread.Sleep(5); }
        return false;
    }

    /// <summary>Arms a take from <paramref name="rack"/>[0], lets FL play the song, waits for the file. Null when something failed (said).</summary>
    private byte[]? CapTake(T t, List<PluginInstance> rack, FlRun run, FlSong s)
    {
        t.Expect(rack[0].Test(HkCArm) == 0, $"Capture from FL was refused: {CText(rack[0], 5)}");
        t.Expect(rack[0].Test(HkCState) == CaptureSession.Armed, $"the take is not armed (state {rack[0].Test(HkCState)})");
        run.Play(rack, s);
        if (!CapDone(rack[0])) { t.Expect(false, $"the take was not built after FL's Stop (state {rack[0].Test(HkCState)}: {CText(rack[0], 0)})"); return null; }
        var why = CText(rack[0], 4);
        t.Expect(why == "", "nothing was captured: " + why);
        var midi = CMidi(rack[0]);
        t.Expect(midi.Length > 0, "the take has no MIDI file");
        return midi.Length > 0 ? midi : null;
    }

    private static void Dispose(List<PluginInstance> rack) { foreach (var p in rack) p.Dispose(); }

    /// <summary>The library's report into this scenario's.</summary>
    private static void Absorb(T t, ImodelReport r, string label)
    {
        foreach (var f in r.Failures.Take(6)) t.Expect(false, $"{label}: {f}");
        if (r.Failures.Count > 6) t.Expect(false, $"{label}: ... and {r.Failures.Count - 6} more");
        foreach (var w in r.Warnings) t.Warn(false, $"{label}: {w}");
        foreach (var n in r.Notes) t.Note($"{label}: {n}");
    }

    private delegate void CapScenario(T t);

    private (string Name, CapScenario Body, int[] Mutants)[] CaptureScenarios() =>
    [
        ("flow", CapFlow, [CaptureMutants.NotFed]),
        ("file", CapFile, [CaptureMutants.InitFromC5, CaptureMutants.VelocityOver127, CaptureMutants.TempoIgnored, CaptureMutants.BendSignFlipped, CaptureMutants.SlideIgnored, CaptureMutants.BendRangeUnwritten, CaptureMutants.EventsLostSilently]),
        ("engine", CapEngine, [CaptureMutants.Pulse2OnChannel1, CaptureMutants.EndAtLastNote]),
        ("render", CapRender, [CaptureMutants.ClockFixedRate]),
        ("alloc", CapAlloc, [CaptureMutants.CaptureAllocates]),
        ("live", CapLive, [CaptureMutants.LiveNoBends]),
        ("edge", CapEdge, [CaptureMutants.OpenNotesLost]),
    ];

    private void CaptureTest(T t)
    {
        foreach (var o in CaptureChecks.RunChecks(null))
        {
            foreach (var f in o.Report.Failures.Take(3)) t.Expect(false, $"[{o.Id}] {f}");
            foreach (var w in o.Report.Warnings) t.Warn(false, $"[{o.Id}] {w}");
        }
        var (lines, missed) = CaptureChecks.RunMutants(null);
        t.Expect(missed == 0, $"{missed} library deliberate bugs were not caught: " + string.Join("; ", lines.Where(l => l.Contains("MISSED"))));
        t.Note($"library: {CaptureChecks.Mutants.Length - missed} of {CaptureChecks.Mutants.Length} deliberate bugs caught");
        foreach (var (name, body, _) in CaptureScenarios())
        {
            var tt = new T();
            try { body(tt); }
            catch (Exception e) { tt.Failures.Add($"exception {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            t.Failures.AddRange(tt.Failures.Select(f => $"{name}: {f}"));
            t.Warnings.AddRange(tt.Warnings.Select(f => $"{name}: {f}"));
            t.Info.AddRange(tt.Info.Select(f => $"{name}: {f}"));
        }
        int caught = 0, armed = 0;
        foreach (var (name, body, mutants) in CaptureScenarios())
            foreach (int m in mutants)
            {
                armed++;
                var tt = new T();
                using (var probe = lib.Create()) probe.Test(HkCMutant, 0, m);
                try { body(tt); }
                catch (Exception e) { tt.Failures.Add($"exception {e.GetType().Name}: {e.Message}"); }
                finally { using var probe = lib.Create(); probe.Test(HkCMutant, 0, 0); }
                if (tt.Failures.Count > 0) { caught++; t.Note($"deliberate bug {m} -> {name} failed: {tt.Failures[0]}"); }
                else t.Expect(false, $"deliberate bug {m} was not caught by {name}: that check cannot fail");
            }
        t.Note($"DLL: {caught} of {armed} deliberate bugs caught");
    }

    // ------------------------------------------------------------------------------------------------ the window, with real mouse messages

    private void CapFlow(T t)
    {
        CapFresh();
        var s = CaptureChecks.Song();
        using var run = new FlRun { Rate = 48000 };
        var rack = CapRack(run, s);
        nint parent = Win32Host.CreateParent();
        try
        {
            var p = rack[0];
            p.ShowEditor(parent);
            for (int k = 0; k < 3; k++) Win32Host.Pump(40);
            ClickControl(t, p, IdMidBtn, "Test MIDI export button");
            Win32Host.Pump(40);
            t.Expect(p.Test(EdSOpen) == 1 && p.Test(EdCShown) == 1, "the Test MIDI export window with the capture strip is not open");
            // the row: Open MIDI..., Capture from FL, Save MIDI..., all inside the window, none over another
            var ids = new[] { IdMidOpenBtn, IdCapArmBtn, IdCapSaveBtn, IdCapLiveBtn, IdMidPlayBtn, IdMidRestartBtn, IdMidStopBtn, IdMidReloadBtn, IdMidFollowBtn, IdMidCloseBtn };
            var rects = ids.Select(i => (Id: i, R: p.Test(30, i))).ToList();
            foreach (var (id, r) in rects) t.Expect(r != 0, $"control {id} is not drawn");
            for (int a = 0; a < rects.Count; a++)
                for (int b = a + 1; b < rects.Count; b++)
                {
                    if (rects[a].R == 0 || rects[b].R == 0) continue;
                    var (ax, ay, aw, ah) = RectOf(rects[a].R); var (bx, by, bw, bh) = RectOf(rects[b].R);
                    t.Expect(ax + aw <= bx || bx + bw <= ax || ay + ah <= by || by + bh <= ay, $"controls {rects[a].Id} and {rects[b].Id} overlap");
                }
            var (cx, cy, cw, ch) = RectOf(p.Test(30, IdCapArmBtn));
            var (ox, oy, ow, oh) = RectOf(p.Test(30, IdMidOpenBtn));
            t.Expect(cy == oy && cx > ox, "Capture from FL is not on Open MIDI...'s row, to its right");
            t.Expect(CEdText(p).Contains("Capture from FL: press it"), $"the strip before a take says '{CEdText(p)}'");
            // arm by mouse
            ClickControl(t, p, IdCapArmBtn, "Capture from FL");
            Win32Host.Pump(40);
            t.Expect(p.Test(HkCState) == CaptureSession.Armed, $"the click did not arm a take (state {p.Test(HkCState)})");
            t.Expect(CEdText(p).Contains("ARMED") && CEdText(p).Contains("4 BrokenNes2 channels listen"), $"armed, the strip says '{CEdText(p)}'");
            // FL plays the song from its start and stops
            run.Play(rack, s);
            t.Expect(CapDone(p), $"the take was not built (state {p.Test(HkCState)}: {CText(p, 0)})");
            t.Expect(SWait(p, () => p.Test(HkSRunning) == 1, 30000), $"the captured song did not become the game: '{SText(p, 0)}' / '{CText(p, 5)}'");
            Win32Host.Pump(60);
            string strip = CEdText(p);
            t.Expect(strip.Contains("Captured FL capture") && strip.Contains("39 notes on 4 channels"), $"after the take the strip says '{strip}'");
            t.Expect(strip.Contains("no pitch bend") && p.Test(EdCFacts) >= 1, $"the strip does not show what VRUN's engine drops ({p.Test(EdCFacts)} facts drawn): '{strip}'");
            t.Expect(SText(p, 0).StartsWith("Loaded FL capture"), $"Test MIDI export said '{SText(p, 0)}'");
            t.Expect(SText(p, 3).StartsWith("FL capture"), $"the song's name is '{SText(p, 3)}'");
            string facts = SText(p, 1);
            t.Expect(facts.Contains("square 1 <- MIDI 1") && facts.Contains("square 2 <- MIDI 2") && facts.Contains("triangle <- MIDI 3") && facts.Contains("noise <- MIDI 10"), $"the converter's facts: {facts}");
            t.Expect(facts.Contains("pitch bends not played"), $"the converter's facts do not count the bends: {facts}");
            t.Expect(p.Test(21) == 1 && p.Test(14) == 0, "the emulator is not in NES ROM mode with the song");
            var audio = RenderBlocks(p, 1.0);
            t.Expect(Audio.Rms(audio, 0, audio.Length) > 0.003, $"the captured song is silent in VRUN's engine (rms {Audio.Rms(audio, 0, audio.Length):0.0000})");
            // Save MIDI... (the dialog replaced by editor hook 811)
            string saved = Path.Combine(MidiDir(), $"saved_{Environment.TickCount64}.mid");
            fixed (char* c = saved + "\0") p.Test(EdCSavePath, 0, (nint)c);
            ClickControl(t, p, IdCapSaveBtn, "Save MIDI...");
            Win32Host.Pump(40);
            t.Expect(File.Exists(saved) && File.ReadAllBytes(saved).AsSpan().SequenceEqual(CMidi(p)), $"Save MIDI... did not write the take's file: {CText(p, 5)}");
            // the MIDI-out box, both ways
            bool live = p.Test(HkCLive, 0, -1) == 1;
            ClickControl(t, p, IdCapLiveBtn, "send my notes as MIDI out");
            t.Expect(p.Test(HkCLive, 0, -1) == (live ? 0 : 1), "the MIDI-out box did not change");
            ClickControl(t, p, IdCapLiveBtn, "send my notes as MIDI out");
            t.Expect(p.Test(HkCLive, 0, -1) == (live ? 1 : 0), "the MIDI-out box did not change back");
            t.Expect(!live, "the MIDI-out box is on by default");
            ClickControl(t, p, IdMidCloseBtn, "Close");
            t.Expect(p.Test(EdSOpen) == 0, "the window did not close");
            p.HideEditor();
            t.Note($"window: armed by mouse, 39 notes captured from FL on 4 channels, played at once by Test MIDI export; '{strip.Split('\n')[0]}'");
        }
        finally { Win32Host.DestroyParent(parent); Dispose(rack); }
    }

    // ------------------------------------------------------------------------------------------------ the file, read back independently

    private void CapFile(T t)
    {
        foreach (var (rate, bpm, tempo) in new[] { (48000, 141.3, true), (44100, 130.0, false) })
        {
            string label = $"{rate} Hz {bpm} BPM, {(tempo ? "FPE_Tempo" : "no FPE_Tempo (FL's samples per tick x PPQ)")}";
            CapFresh();
            var s = CaptureChecks.Song(bpm);
            using var run = new FlRun { Rate = rate, SendTempo = tempo };
            var rack = CapRack(run, s);
            try
            {
                // the period registers the channels' own chips held after each tick (Direct mode's arithmetic on what FL sent)
                var periods = new List<(int Tick, int P1, int Tri)>();
                run.AfterTick = k => { if (k < s.StopTick) periods.Add((k, (int)rack[0].Test(1, 0), (int)rack[0].Test(1, 2))); };
                var midi = CapTake(t, rack, run, s);
                if (midi == null) continue;
                var r = new ImodelReport();
                var d = CaptureChecks.Dump(r, midi, "file_" + rate);
                if (d != null)
                {
                    var c = CaptureChecks.Compare(r, d, s, run.Truth, label);
                    r.Note($"{c.NotesFound}/{c.Notes} notes, start and end within {c.MaxTickError} tick(s) at 960 per quarter; the pitch FL played at {c.PitchOk}/{c.PitchPoints} ticks within {c.AllowedPitchError:0.###} cents (worst {c.MaxPitchError:0.####}); bend ranges {string.Join("/", c.Range)}");
                    // the chip's period registers against the file's pitch through Direct mode's arithmetic (NesDirect, the code that wrote them)
                    var (same, edge, wrong) = PeriodsAgainstFile(d, s, run.Truth, periods, c.Range);
                    r.Expect(wrong.Count == 0, $"{label}: {wrong.Count} ticks where the file's pitch gives another period register than the chip held: {string.Join("; ", wrong.Take(3))}");
                    r.Note($"period registers: {same} of {same + edge + wrong.Count} ticks the very register the chip held (pulse 1 and triangle), {edge} at a rounding edge (within the bend's step), {wrong.Count} other");
                }
                string facts = CText(rack[0], 1);
                r.Expect(facts.Contains("still sounding when FL stopped") && facts.Contains("no pitch bend"), $"{label}: the take's facts: {facts}");
                if (!tempo) r.Expect(CText(rack[0], 2).Contains("from FL's") && CText(rack[0], 2).Contains("PPQ 96"), $"{label}: the tempo's source is not FL's samples per tick x PPQ: {CText(rack[0], 2).Split('\n').FirstOrDefault(l => l.StartsWith("tempo"))}");
                Absorb(t, r, label);
            }
            finally { Dispose(rack); }
        }
    }

    /// <summary>For pulse 1 and the triangle at every FL tick: the period Direct mode's arithmetic gives for the file's pitch (key + bend) against the register the instance's chip held. A difference is a
    /// rounding edge when the pitch FL played is within the bend's step of a period boundary (the file cannot be closer than its 14 bits).</summary>
    private static (int Same, int Edge, List<string> Wrong) PeriodsAgainstFile(SmfDump d, FlSong s, FlTruth truth, List<(int Tick, int P1, int Tri)> periods, int[] range)
    {
        int same = 0, edge = 0;
        var wrong = new List<string>();
        int ratio = d.Ppq / s.Ppq;
        foreach (var (inst, triangle) in new[] { (0, false), (2, true) })
        {
            int ti = d.TrackInfo.FindIndex(x => x.Name == (inst == 0 ? "Pulse 1" : "Triangle"));
            if (ti < 0) continue;
            int track = d.TrackInfo[ti].Track;
            var notes = d.Notes.Where(x => x.Track == track).ToList();
            var bends = d.Bends.Where(x => x.Track == track).OrderBy(x => x.Tick).ToList();
            foreach (var o in truth.Owners.Where(o => o.Inst == inst))
            {
                var reg = periods.FirstOrDefault(x => x.Tick == o.Tick);
                int held = inst == 0 ? reg.P1 : reg.Tri;
                long tk = (long)o.Tick * ratio;
                var sounding = notes.Where(x => x.On <= tk && x.Off > tk).OrderBy(x => x.On).LastOrDefault();
                int bend = 0;
                foreach (var b in bends) { if (b.Tick > tk) break; bend = b.Value; }
                double filePitch = (sounding.Key - 60) * 100.0 + bend * range[inst] * 100.0 / 8192;
                int fromFile = triangle ? NesDirect.TrianglePeriod(filePitch) : Math.Max(8, NesDirect.PulsePeriod(filePitch));
                if (fromFile == held) { same++; continue; }
                double step = range[inst] * 100.0 / 8192;
                int lo = triangle ? NesDirect.TrianglePeriod(o.Played - step) : Math.Max(8, NesDirect.PulsePeriod(o.Played - step));
                int hi = triangle ? NesDirect.TrianglePeriod(o.Played + step) : Math.Max(8, NesDirect.PulsePeriod(o.Played + step));
                if ((fromFile == lo || fromFile == hi) && (held == lo || held == hi)) edge++;
                else wrong.Add($"{(triangle ? "triangle" : "pulse 1")} tick {o.Tick}: the chip held ${held:X3}, the file gives ${fromFile:X3} (FL played {o.Played:0.###} cents)");
            }
        }
        return (same, edge, wrong);
    }

    // ------------------------------------------------------------------------------------------------ the captured song in VRUN's engine

    private void CapEngine(T t)
    {
        CapFresh();
        var s = CaptureChecks.Song();
        using var run = new FlRun { Rate = 48000 };
        var rack = CapRack(run, s);
        try
        {
            var midi = CapTake(t, rack, run, s);
            if (midi == null) return;
            var p = rack[0];
            p.Test(120, 0, 1);                                                    // (no editor runs the game behind the test's back)
            t.Expect(SReady(p), $"the captured song's game was not built: '{SText(p, 0)}' / '{CText(p, 5)}'");
            var b = SongConvert.Convert(midi, out var why);
            t.Expect(b != null, "the converter refused the captured file: " + why);
            if (b == null) return;
            int n = Math.Clamp(b.Facts.LoopFrames + 120, 560, 2400);
            var all = SLatch(p, n + 120);
            var rom = SRom(p, MidiDir());
            t.Expect(rom != null && rom.AsSpan().SequenceEqual(LibraryRom(midi, SText(p, 3))), "the ROM the plugin built from the take is not the library's for the same bytes");
            var sc = MidiModel.Best(b.Channels, all, n, skip: MidiChecks.Skip);
            t.Expect(sc.Worst >= 1.0 && sc.Start == MusicTemplate.Preroll, $"the plugin's machine does not play the converter's streams of the take: {sc.Agree}/{sc.Of} from frame {sc.Start}; {string.Join("; ", sc.Mismatches)}");
            // the streams and loop are the ideal file's (the script's notes, no bends) at the take's tempo
            int mpq = (int)p.Test(HkCNum, 9);
            var ideal = SongConvert.Convert(CaptureChecks.IdealMidi(s, mpq), out _)!;
            for (int c = 0; c < 4; c++)
                t.Expect(b.Channels[c].AsSpan().SequenceEqual(ideal.Channels[c]), $"{Vrun.Audio.Midi2Nes.ChannelNames[c]}: the take's stream ({b.Channels[c].Length} bytes) is not the ideal file's ({ideal.Channels[c].Length})");
            long bar = 4L * 960, lastOff = (long)s.Notes.Max(x => CaptureChecks.EndTick(s, x)) * 10, end = (lastOff + bar - 1) / bar * bar;
            int expectFrames = Math.Max((int)(end * ((double)mpq / 960 * Vrun.Audio.Midi2Nes.Fps) / 1_000_000.0), 2);
            t.Expect(b.Facts.LoopFrames == expectFrames && ideal.Facts.LoopFrames == expectFrames, $"the loop is {b.Facts.LoopFrames} frames (ideal {ideal.Facts.LoopFrames}); the bar line after the last note ({end / bar} bars) is {expectFrames}");
            t.Expect(b.Facts.PitchBends == p.Test(HkCNum, 2) && b.Facts.PitchBends > 0, $"the converter counts {b.Facts.PitchBends} pitch bends not played, the take wrote {p.Test(HkCNum, 2)}");
            t.Note($"the take in VRUN's engine: {sc.Agree}/{sc.Of} field-frames of the converter's streams from frame {sc.Start}; streams {string.Join(" + ", b.Channels.Select(x => x.Length))} bytes = the ideal file's; loop {b.Facts.LoopFrames} frames ({end / bar} bars); {b.Facts.PitchBends} bends not played");
        }
        finally { rack[0].Test(120, 0, 0); Dispose(rack); }
    }

    // ------------------------------------------------------------------------------------------------ FL's Export: rendered at another rate, faster than real time

    private void CapRender(T t)
    {
        CapFresh();
        var s = CaptureChecks.Song(130);
        using var run = new FlRun { Rate = 44100, SendTempo = false };
        var rack = CapRack(run, s);
        try
        {
            // FL renders the song (File > Export > WAV): another rate, PM_IsRendering
            run.Rate = 48000; run.Rendering = true;
            foreach (var p in rack) { p.Dispatcher(Fpd.SetSampleRate, 0, 48000); p.Dispatcher(1, 0, 3072 | 16); }
            var midi = CapTake(t, rack, run, s);
            if (midi == null) return;
            var r = new ImodelReport();
            var d = CaptureChecks.Dump(r, midi, "render");
            if (d != null)
            {
                var c = CaptureChecks.Compare(r, d, s, run.Truth, "export at 48 kHz");
                r.Note($"export: {c.NotesFound}/{c.Notes} notes within {c.MaxTickError} tick(s), the pitch at {c.PitchOk}/{c.PitchPoints} ticks within {c.AllowedPitchError:0.###} cents");
            }
            r.Expect(CText(rack[0], 1).Contains("while FL rendered"), "the facts do not say the take was rendered: " + CText(rack[0], 1));
            Absorb(t, r, "render");
        }
        finally { Dispose(rack); }
    }

    // ------------------------------------------------------------------------------------------------ nothing allocated on the audio thread

    private void CapAlloc(T t)
    {
        CapFresh();
        var s = CaptureChecks.Song();
        using var run = new FlRun { Rate = 48000 };
        var rack = CapRack(run, s);
        try
        {
            rack[0].Test(HkCLive, 0, 1);                                          // the MIDI out of the notes too
            CapTake(t, rack, run, s);                                             // a first take: everything the first time
            t.Expect(SWait(rack[0], () => rack[0].Test(HkSRunning) == 1, 30000), "the first take's song did not become the game");
            t.Expect(rack[0].Test(HkCArm) == 0, "the second take was refused");     // (the emulator goes back to Direct mode here, on this thread: its chips are made now)
            GC.Collect();
            long[] before = rack.Select(p => p.Test(4)).ToArray();
            run.Play(rack, s);
            long total = 0;
            for (int i = 0; i < rack.Count; i++) total += rack[i].Test(4) - before[i];
            t.Expect(CapDone(rack[0]), "the second take was not built");
            long notes = rack.Sum(p => p.Test(HkCNum, 7));
            t.Expect(notes == s.Notes.Count, $"the instances captured {notes} notes in the measured take, FL played {s.Notes.Count}");
            t.Expect(total == 0, $"{total:N0} bytes allocated on the audio thread while four channels were captured (FL's Play and Stop, {s.Notes.Count} notes, slides, MIDI out on); must be 0");
            t.Note($"{total} bytes allocated on the audio thread over a take of {s.StopTick + run.AfterStopTicks} FL ticks on 4 channels ({notes} notes, MIDI out on)");
        }
        finally { rack[0].Test(HkCLive, 0, 0); Dispose(rack); }
    }

    // ------------------------------------------------------------------------------------------------ the notes as MIDI out

    private void CapLive(T t)
    {
        CapFresh();
        var s = CaptureChecks.Song();
        using var run = new FlRun { Rate = 48000 };
        var rack = CapRack(run, s);
        try
        {
            rack[0].Test(HkCLive, 0, 1);
            foreach (var p in rack) lock (p.Host.MidiOutLog) p.Host.MidiOutLog.Clear();
            // where each instance's MIDI out was after each tick (the state at a tick = the messages up to it)
            var marks = new List<int[]>();
            run.AfterTick = k => marks.Add(rack.Select(p => { lock (p.Host.MidiOutLog) return p.Host.MidiOutLog.Count; }).ToArray());
            run.Play(rack, s);                                                    // no take armed: the MIDI out works alone
            int[] channels = CaptureChecks.ExpectedChannel;
            int notesOk = 0, pitchOk = 0, pitchN = 0, bad = 0;
            double worst = 0;
            for (int inst = 0; inst < 4; inst++)
            {
                List<(uint Msg, bool Delayed)> log;
                lock (rack[inst].Host.MidiOutLog) log = rack[inst].Host.MidiOutLog.ToList();
                t.Expect(log.Count > 0 && log.All(m => m.Delayed && (m.Msg >> 24) == Bn2Live.Port && (m.Msg & 0x0F) == channels[inst]), $"instance {inst}: {log.Count} messages, not all MIDIOut_Delayed on port {Bn2Live.Port} channel {channels[inst] + 1}");
                int firstOn = log.FindIndex(m => (m.Msg & 0xF0) == 0x90);
                var cc = log.Take(Math.Max(0, firstOn)).Where(m => (m.Msg & 0xF0) == 0xB0).Select(m => ((int)((m.Msg >> 8) & 0x7F), (int)((m.Msg >> 16) & 0x7F))).ToList();
                int i101 = cc.IndexOf((101, 0)), i100 = cc.IndexOf((100, 0)), i6 = cc.IndexOf((6, Bn2Live.Range));
                t.Expect(firstOn > 0 && i101 >= 0 && i100 > i101 && i6 > i100, $"instance {inst}: the bend range (RPN 0 = {Bn2Live.Range}) is not sent before the first note");
                // the note-ons in order against the script
                var ons = log.Where(m => (m.Msg & 0xF0) == 0x90 && ((m.Msg >> 16) & 0x7F) > 0).Select(m => (Key: (int)((m.Msg >> 8) & 0x7F), Vel: (int)((m.Msg >> 16) & 0x7F))).ToList();
                var want = s.Notes.Where(n => n.Inst == inst).OrderBy(n => n.Start).Select(n => (Key: n.Key + s.Coarse[inst] + (int)Math.Round(s.Fine[inst] / 100.0), Vel: n.Velocity)).ToList();
                t.Expect(ons.SequenceEqual(want), $"instance {inst}: note-ons {string.Join(",", ons.Take(6))}..., FL played {string.Join(",", want.Take(6))}...");
                if (ons.SequenceEqual(want)) notesOk += ons.Count;
                int offs = log.Count(m => (m.Msg & 0xF0) == 0x80);
                t.Expect(offs == want.Count, $"instance {inst}: {offs} note-offs for {want.Count} notes");
                // the pitch at every tick: the newest sounding note + the last bend
                foreach (var o in run.Truth.Owners.Where(o => o.Inst == inst))
                {
                    int upTo = marks[o.Tick][inst];
                    var sounding = new List<int>();
                    int bend = 0;
                    for (int i = 0; i < upTo; i++)
                    {
                        uint m = log[i].Msg; int st = (int)(m & 0xF0), d1 = (int)((m >> 8) & 0x7F), d2 = (int)((m >> 16) & 0x7F);
                        if (st == 0x90 && d2 > 0) sounding.Add(d1);
                        else if (st == 0x80 || (st == 0x90 && d2 == 0)) sounding.Remove(d1);
                        else if (st == 0xE0) bend = (d1 | d2 << 7) - 8192;
                    }
                    pitchN++;
                    if (sounding.Count == 0) { if (bad++ < 3) t.Expect(false, $"instance {inst} tick {o.Tick}: no note sounds in the MIDI out, FL played {o.Note.Label}"); continue; }
                    double p = (sounding[^1] - 60) * 100.0 + bend * Bn2Live.Range * 100.0 / 8192;
                    double err = Math.Abs(p - o.Played);
                    worst = Math.Max(worst, err);
                    if (err <= Bn2Live.Range * 100.0 / 16384 + 1e-3) pitchOk++;
                    else if (bad++ < 3) t.Expect(false, $"instance {inst} tick {o.Tick} ({o.Note.Label}): the MIDI out plays {p:0.###} cents, FL played {o.Played:0.###}");
                }
            }
            t.Expect(bad == 0, $"{bad} ticks where the MIDI out's pitch is not what FL played");
            t.Note($"MIDI out: {notesOk} note-ons as FL played them, the pitch at {pitchOk}/{pitchN} ticks within {Bn2Live.Range * 100.0 / 16384:0.###} cents (worst {worst:0.####}), MIDIOut_Delayed, port {Bn2Live.Port}");
            // off: nothing more goes out (after the all-notes-off)
            rack[0].Test(HkCLive, 0, 0);
            foreach (var p in rack) lock (p.Host.MidiOutLog) p.Host.MidiOutLog.Clear();
            run.AfterTick = null;
            run.Play(rack, s);
            int after = rack.Sum(p => { lock (p.Host.MidiOutLog) return p.Host.MidiOutLog.Count(m => (m.Msg & 0xF0) != 0xB0); });
            t.Expect(after == 0, $"{after} note messages went out with the box unticked");
        }
        finally { rack[0].Test(HkCLive, 0, 0); Dispose(rack); }
    }

    /// <summary>The live MIDI out's fixed settings (Bn2Plugin.CaptureLive.cs).</summary>
    private static class Bn2Live { public const int Port = 1, Range = 48; }

    // ------------------------------------------------------------------------------------------------ the edges

    private void CapEdge(T t)
    {
        CapFresh();
        var s = CaptureChecks.Song();
        using var run = new FlRun { Rate = 48000 };
        var rack = CapRack(run, s);
        try
        {
            var p = rack[0];
            // nothing played: said, nothing handed over
            var empty = new FlSong { Name = "empty", Bpm = s.Bpm, StopTick = 200 };
            t.Expect(p.Test(HkCArm) == 0, "the empty take was refused");
            run.Play(rack, empty);
            t.Expect(CapDone(p), "the empty take was not finished");
            t.Expect(CText(p, 4).Contains("no BrokenNes2 channel got a note") && CText(p, 0).StartsWith("Nothing captured"), $"an empty take says '{CText(p, 0)}' ({CText(p, 4)})");
            t.Expect(p.Test(HkSRunning) == -1, "an empty take loaded a song");
            // cancel: nothing kept, FL's Play records nothing
            t.Expect(p.Test(HkCArm) == 0, "arming again was refused");
            p.Test(HkCCancel);
            t.Expect(p.Test(HkCState) == CaptureSession.Cancelled, $"cancel left the take in state {p.Test(HkCState)}");
            run.Play(rack, empty);
            t.Expect(p.Test(HkCNum, 6) == -1 && p.Test(HkCState) == CaptureSession.Cancelled, "a cancelled take still listens");
            // a channel FL never starts (no FPD_SetPlaying of its own): it joins the take at the others' clock, said; the note held over Stop is said
            run.NoPlay = [3];
            var midi = CapTake(t, rack, run, s);
            run.NoPlay = [];
            string facts = CText(p, 1);
            t.Expect(facts.Contains("Noise got no Play of its own") && facts.Contains("joined the take"), $"the late channel is not said: {facts}");
            t.Expect(facts.Contains("1 note was still sounding when FL stopped"), $"the note held over Stop is not said: {facts}");
            if (midi != null)
            {
                var r = new ImodelReport();
                var d = CaptureChecks.Dump(r, midi, "edge");
                if (d != null)
                {
                    var noise = d.TrackInfo.FirstOrDefault(x => x.Name == "Noise");
                    int late = d.Notes.Count(x => x.Track == noise.Track && x.Ch == 10);
                    r.Expect(late == 16, $"the late channel's notes: {late} of 16 in the file");
                    long first = d.Notes.Where(x => x.Track == noise.Track).Select(x => x.On).DefaultIfEmpty(-1).Min();
                    r.Expect(first == 0, $"the late channel's first note is at tick {first} (it struck with the others at the start: 0)");
                    var held = s.Notes.First(x => x.Label == "held over Stop");
                    r.Expect(d.Notes.Any(x => x.Ch == 1 && x.Key == held.Key && x.On == held.Start * 10 && x.Off == s.StopTick * 10), "the note held over Stop is not in the file ending at the Stop");
                }
                Absorb(t, r, "edge");
            }
            // the song is the game now (ROM mode): arming again puts the emulator back in Direct mode, so FL's notes are heard while capturing
            t.Expect(SWait(p, () => p.Test(HkSRunning) == 1, 30000), "the take's song did not become the game");
            t.Expect(p.Test(21) == 1, "the emulator is not in ROM mode with the song");
            t.Expect(p.Test(HkCArm) == 0, "arming over the song was refused");
            t.Expect(p.Test(21) == 0 && CText(p, 0).Contains("back in Direct mode"), $"arming over the take's song: mode {p.Test(21)}, the window says '{CText(p, 0)}'");
            p.Test(HkCCancel);
        }
        finally { Dispose(rack); }
    }
}
