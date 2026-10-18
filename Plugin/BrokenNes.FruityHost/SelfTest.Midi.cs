// Round 7 (midi): "Test MIDI export" through the real plugin DLL (Native AOT). The library checks first (MidiChecks: the quick ones; the full converter identity, template equality
// and full-rebuild reference are `FruityHost midi-test`), then the DLL: the button and its window with real mouse messages (the file dialog replaced by editor hook 621, as the
// Corruption window's is), the ROM the plugin builds playing the converter's streams frame by frame from the plugin's own machine (hook 611), the stems, Play / Stop / Restart,
// FL's transport, Reload after the file changed, the FL project (save, reopen, damaged, newer, absent), the refusals, 0 bytes allocated on the audio thread; then each scenario again
// with a deliberate bug switched on in the DLL (hook 616), which the scenario must catch. Plugin hooks 600-619 (Bn2Plugin.Song.cs), editor hooks 620-622 (Bn2Editor.Song.cs).
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using BrokenNes2.Music;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkSOpen = 600, HkSBusy = 601, HkSRunning = 602, HkSText = 603, HkSPlay = 604, HkSRestart = 605, HkSStop = 606, HkSReload = 607, HkSFollow = 608, HkSStopped = 609,
        HkSRom = 610, HkSLatch = 611, HkSTemplateOk = 612, HkSTemplate = 613, HkSStamp = 614, HkSNum = 615, HkSMutate = 616, HkSFrames = 617, HkSGen = 618, HkSSection = 619;
    private const int EdSOpen = 620, EdSPick = 621, EdSNote = 622;
    private const int IdMidBtn = 2000, IdMidCloseBtn = 2001, IdMidOpenBtn = 2002, IdMidPlayBtn = 2003, IdMidRestartBtn = 2004, IdMidStopBtn = 2005, IdMidReloadBtn = 2006, IdMidFollowBtn = 2007;

    private static string SText(PluginInstance p, int field)
    {
        var c = new char[2000];
        fixed (char* cp = c) { long n = p.Test(HkSText, (field << 16) | c.Length, (nint)cp); return n < 0 ? "" : new string(c, 0, (int)Math.Min(n, c.Length)); }
    }

    private static long SOpen(PluginInstance p, string path) { fixed (char* c = path + "\0") return p.Test(HkSOpen, 0, (nint)c); }

    /// <summary>Waits until no conversion runs and the game is the song (or <paramref name="ms"/> passes), rendering a little so the audio side takes the new game.</summary>
    private static bool SWait(PluginInstance p, Func<bool> done, int ms = 30000)
    {
        long until = Environment.TickCount64 + ms;
        var buf = new float[512];
        while (Environment.TickCount64 < until)
        {
            if (p.Test(HkSBusy) == 0 && done()) return true;
            fixed (float* f = buf) { p.NewTick(); p.GenRender(f, 256); }
            Win32Host.Pump(5);
        }
        return p.Test(HkSBusy) == 0 && done();
    }

    /// <summary>Waits, without running the game, until the song's game is built (taken or waiting to be taken): for the checks that count frames from power-on.</summary>
    private static bool SReady(PluginInstance p, int ms = 30000)
    {
        long until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until) { if (p.Test(HkSBusy) == 0 && p.Test(HkSRunning) is 1 or 2) return true; Thread.Sleep(5); }
        return false;
    }

    private static byte[] SLatch(PluginInstance p, int frames)
    {
        var buf = new byte[frames * MidiModel.LatchBytes];
        long n;
        fixed (byte* b = buf) n = p.Test(HkSLatch, (nint)b, frames);
        return n == frames ? buf : buf[..(int)(Math.Max(0, n) * MidiModel.LatchBytes)];
    }

    private static byte[]? SRom(PluginInstance p, string dir)
    {
        string f = Path.Combine(dir, $"rom_{Environment.TickCount64}.nes");
        fixed (char* c = f + "\0") if (p.Test(HkSRom, 0, (nint)c) != 1) return null;
        return File.ReadAllBytes(f);
    }

    private static string MidiDir()
    {
        string d = Path.Combine(Path.GetTempPath(), $"bn2_midi_{Environment.ProcessId}");
        Directory.CreateDirectory(d);
        return d;
    }

    private static string WriteMid(string name, byte[] bytes) { string f = Path.Combine(MidiDir(), name + ".mid"); File.WriteAllBytes(f, bytes); return f; }

    private static MidiSong Stress(string name) => MidiFixtures.Stress().First(s => s.Name == name);

    /// <summary>What the library's own path builds for a song (convert, patch the bundled template): the ROM the plugin must build.</summary>
    private static byte[] LibraryRom(byte[] midi, string name)
    {
        var (rom, manifest, _) = MidiFixtures.Bundled();
        var t = MusicTemplate.Load(rom, manifest, out _)!;
        var b = SongConvert.Convert(midi, out _)!;
        return t.Patch(b.Channels, b.Facts.LoopFrames, name);
    }

    /// <summary>A fresh instance on its own emulator #8 (so nothing of another scenario is there), 48 kHz, NES, ROM mode with the built-in game.</summary>
    private PluginInstance SongPlugin(bool rom = true)
    {
        var p = lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        p.SetParam(PEmulator, 8);
        if (rom) { p.SetParam(PMode, 1); WaitForRom(p); }
        return p;
    }

    private delegate void SongScenario(T t);

    private (string Name, SongScenario Body, int Mutant, string Why)[] SongScenarios() =>
    [
        ("flow", SongFlow, MusicMutate.PlayCallsStop, "the Play button stops"),
        ("engine", SongEngineDll, MusicMutate.PatchSkew, "square 1 patched one byte late"),
        ("stems", SongStems, 0, ""),
        ("transport", SongTransport, MusicMutate.TransportIgnored, "FL's transport start does not restart the song"),
        ("reload", SongReload, MusicMutate.ReloadCached, "Reload converts the bytes it had instead of reading the file"),
        ("project", SongProject, MusicMutate.SectionDropsMidi, "the project section is written without the MIDI file"),
        ("refusals", SongRefusals, MusicMutate.RefusalSilent, "a refusal says nothing"),
        ("alloc", SongAlloc, MusicMutate.RestartAllocates, "a restart drops the prepared stems (the audio thread makes new ones)"),
    ];

    private void MidiTest(T t)
    {
        // the pure checks first (the quick ones; converter identity, template equality and the full-rebuild reference are FruityHost midi-test)
        foreach (var o in MidiChecks.RunChecks("synthetic-port,title,template-refusals,facts,section"))
        {
            foreach (var f in o.Report.Failures.Take(3)) t.Expect(false, $"[{o.Id}] {f}");
            foreach (var w in o.Report.Warnings) t.Warn(false, $"[{o.Id}] {w}");
        }
        foreach (var (name, body, _, _) in SongScenarios())
        {
            var tt = new T();
            try { body(tt); }
            catch (Exception e) { tt.Failures.Add($"exception {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            t.Failures.AddRange(tt.Failures.Select(f => $"{name}: {f}"));
            t.Warnings.AddRange(tt.Warnings.Select(f => $"{name}: {f}"));
            t.Info.AddRange(tt.Info.Select(f => $"{name}: {f}"));
        }
        // each scenario with its deliberate bug: it must fail
        int caught = 0, armed = 0;
        foreach (var (name, body, mutant, why) in SongScenarios())
        {
            if (mutant == 0) continue;
            armed++;
            var tt = new T();
            using (var probe = lib.Create()) probe.Test(HkSMutate, 0, mutant);
            try { body(tt); }
            catch (Exception e) { tt.Failures.Add($"exception {e.GetType().Name}: {e.Message}"); }
            finally { using var probe = lib.Create(); probe.Test(HkSMutate, 0, 0); }
            if (tt.Failures.Count > 0) { caught++; t.Note($"deliberate bug '{why}' -> {name} failed: {tt.Failures[0]}"); }
            else t.Expect(false, $"deliberate bug '{why}' was not caught by {name}: that check cannot fail");
        }
        t.Note($"{caught} of {armed} deliberate bugs caught");
    }

    // ------------------------------------------------------------------------------------------------ the button and the window, with real mouse messages

    private void SongFlow(T t)
    {
        string mid = WriteMid("flow song", MidiFixtures.Synthetic());
        using var p = SongPlugin();
        nint parent = Win32Host.CreateParent();
        try
        {
            p.ShowEditor(parent);
            for (int k = 0; k < 3; k++) Win32Host.Pump(40);
            // the button sits in the header, clear of the About button and of the tab strip
            var btn = p.Test(30, IdMidBtn);
            t.Expect(btn != 0, "there is no Test MIDI export button");
            if (btn == 0) return;
            var r = RectOf(btn);
            foreach (int other in new[] { 40, 1000, 1001, 1002, 1, 80, 500 })
            {
                long o = p.Test(30, other);
                if (o != 0) { var q = RectOf(o); t.Expect(r.X + r.W <= q.X || q.X + q.W <= r.X || r.Y + r.H <= q.Y || q.Y + q.H <= r.Y, $"the button overlaps control {other}"); }
            }
            t.Expect(r.Y + r.H <= 52, $"the button is not in the header ({r})");
            ClickControl(t, p, IdMidBtn, "Test MIDI export button");
            t.Expect(p.Test(EdSOpen) == 1, "the window did not open");
            t.Expect(p.Test(30, 80) == 0 && p.Test(30, IdMidBtn) == 0, "controls behind the window are still reachable");
            t.Expect(p.Test(30, IdMidPlayBtn) != 0 && p.Test(30, IdMidReloadBtn) != 0 && p.Test(30, IdMidFollowBtn) != 0, "the window's Play / Reload / follow controls are not drawn");
            fixed (char* c = mid + "\0") p.Test(EdSPick, 0, (nint)c);
            ClickControl(t, p, IdMidOpenBtn, "Open MIDI...");
            t.Expect(SWait(p, () => p.Test(HkSRunning) == 1), $"the song did not become the game: '{SText(p, 0)}'");
            t.Expect(SText(p, 0).StartsWith("Loaded flow song.mid"), $"the window said '{SText(p, 0)}'");
            t.Expect(SText(p, 2).Contains("replaced VRUN (built-in)"), $"the note about the game it replaced says '{SText(p, 2)}'");
            t.Expect(SText(p, 3) == "flow song", $"the song's name is '{SText(p, 3)}'");
            string facts = SText(p, 1);
            t.Expect(facts.Contains("square 1 <- MIDI 1") && facts.Contains("square 2 silent") && facts.Contains("loop 480 frames"), "the facts are not the synthetic song's: " + facts);
            t.Expect(p.Test(21) == 1 && p.Test(14) == 0, "the emulator is not in NES ROM mode");
            var audio = RenderBlocks(p, 1.5);
            t.Expect(Audio.Rms(audio, 0, audio.Length) > 0.003, $"the song is silent (rms {Audio.Rms(audio, 0, audio.Length):0.0000})");
            // Stop, Play, Restart through the buttons
            Win32Host.Pump(40);
            ClickControl(t, p, IdMidStopBtn, "Stop");
            t.Expect(p.Test(HkSStopped) == 1, "Stop did not stop the song");
            var quiet = RenderBlocks(p, 0.5);
            t.Expect(Audio.Rms(quiet, 4800, quiet.Length) < 1e-6, $"a stopped song still sounds (rms {Audio.Rms(quiet, 4800, quiet.Length):0.000000})");
            Win32Host.Pump(40);
            ClickControl(t, p, IdMidPlayBtn, "Play");
            SWait(p, () => p.Test(HkSStopped) == 0, 5000);
            t.Expect(p.Test(HkSStopped) == 0, "Play did not play the song again");
            var again = RenderBlocks(p, 1.0);
            t.Expect(Audio.Rms(again, 0, again.Length) > 0.003, "after Play the song is silent");
            long restarts = p.Test(HkSNum, 9);
            Win32Host.Pump(40);
            ClickControl(t, p, IdMidRestartBtn, "Restart");
            SWait(p, () => p.Test(HkSNum, 9) > restarts, 5000);
            t.Expect(p.Test(HkSNum, 9) > restarts, "Restart did not restart the song");
            // the follow tick
            bool follow = p.Test(HkSFollow) == 1;
            ClickControl(t, p, IdMidFollowBtn, "follow FL's transport");
            t.Expect(p.Test(HkSFollow) == (follow ? 0 : 1), "the follow tick did not change");
            t.Expect(follow, "following FL's transport is not on by default");
            ClickControl(t, p, IdMidFollowBtn, "follow FL's transport");
            ClickControl(t, p, IdMidCloseBtn, "Close");
            t.Expect(p.Test(EdSOpen) == 0, "the window did not close");
            Win32Host.Pump(60);
            t.Expect(p.Test(EdSNote) == 1, "the note about the replaced game is not over the picture after the window closed");
            p.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); }
    }

    // ------------------------------------------------------------------------------------------------ the engine, from the plugin's own machine

    private void SongEngineDll(T t)
    {
        var songs = new List<MidiSong> { Stress("synthetic"), Stress("dense") };
        songs.AddRange(MidiFixtures.RosterSongs().Where(s => s.Name is "think" or "level2"));
        if (MidiFixtures.Vrun() == null) t.Warn(false, "NOT RUN on VRUN's roster songs (the VRUN tree is not on this machine): the songs written here were checked");
        string dir = MidiDir();
        foreach (var s in songs)
        {
            using var p = SongPlugin();
            p.Test(120, 0, 1);                                                    // the editor does not run the game behind the test's back
            try
            {
                t.Expect(SOpen(p, WriteMid(s.Name, s.Midi)) == 0, $"{s.Name}: the open was refused");
                t.Expect(SReady(p), $"{s.Name}: the song's game was not built: '{SText(p, 0)}'");
                var b = SongConvert.Convert(s.Midi, out _)!;
                int n = Math.Clamp(b.Facts.LoopFrames + 120, 560, 2400);
                // from power-on (the hook takes the built game first, as the audio thread would): the decode begins after the preroll
                var all = SLatch(p, n + 120);
                t.Expect(p.Test(HkSRunning) == 1 && p.Test(HkSFrames) == n + 120, $"{s.Name}: the song game ran {p.Test(HkSFrames)} frames, expected {n + 120} from power-on");
                var rom = SRom(p, dir);
                t.Expect(rom != null && rom.AsSpan().SequenceEqual(LibraryRom(s.Midi, s.Name)), $"{s.Name}: the ROM the plugin built is not the one the library builds (the one midi-test verifies)");
                var sc = MidiModel.Best(b.Channels, all, n, skip: MidiChecks.Skip);
                t.Expect(sc.Worst >= 1.0, $"{s.Name}: the plugin's machine does not play the converter's streams: {sc.Agree}/{sc.Of}; {string.Join("; ", sc.Mismatches)}");
                t.Expect(sc.Start == MusicTemplate.Preroll, $"{s.Name}: the decode began in frame {sc.Start}, expected {MusicTemplate.Preroll}");
                // after a restart (the copy run up to the decode): the decode is in the first frame
                SWait(p, () => p.Test(HkSNum, 11) == 1, 10000);
                p.Test(HkSRestart);
                var after = SLatch(p, n + 20);
                var sr = MidiModel.Best(b.Channels, after, n, 0, 8, MidiChecks.Skip);
                t.Expect(sr.Worst >= 1.0 && sr.Start == 0, $"{s.Name}: after Restart the decode began in frame {sr.Start} ({sr.Agree}/{sr.Of}); a restart should start the song in the first frame");
                t.Note($"{s.Name}: {sc.Agree}/{sc.Of} field-frames from power-on (decode in frame {sc.Start}), {sr.Agree}/{sr.Of} after a restart (decode in frame {sr.Start})");
            }
            finally { p.Test(120, 0, 0); }
        }
    }

    // ------------------------------------------------------------------------------------------------ the stems: each NES channel on its own instance

    private void SongStems(T t)
    {
        using var p = SongPlugin();
        t.Expect(SOpen(p, WriteMid("stems", MidiFixtures.Synthetic())) == 0, "the open was refused");
        t.Expect(SWait(p, () => p.Test(HkSRunning) == 1), "the song did not become the game");
        var tones = new List<PluginInstance>();
        try
        {
            for (int c = 0; c < 4; c++)
            {
                var q = lib.Create();
                q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                q.SetParam(PEmulator, 8);
                q.SetParam(PChannel, c + 1);
                tones.Add(q);
            }
            t.Expect(p.Test(17) == 4, $"the first instance is on channel {p.Test(17)}, not Mix");
            SWait(p, () => p.Test(HkSNum, 11) == 1, 10000);
            p.Test(HkSRestart);
            var mix = new float[48000 * 2]; var stems = new float[4][];
            for (int c = 0; c < 4; c++) stems[c] = new float[mix.Length];
            var buf = new float[1024];
            for (int blk = 0; blk < mix.Length / 512; blk++)
            {
                fixed (float* f = buf) { p.NewTick(); p.GenRender(f, 512); }
                for (int i = 0; i < 512; i++) mix[blk * 512 + i] = (buf[2 * i] + buf[2 * i + 1]) / 2;
                for (int c = 0; c < 4; c++)
                {
                    fixed (float* f = buf) { tones[c].NewTick(); tones[c].GenRender(f, 512); }
                    for (int i = 0; i < 512; i++) stems[c][blk * 512 + i] = (buf[2 * i] + buf[2 * i + 1]) / 2;
                }
            }
            int from = 9600;
            double[] rms = stems.Select(s => Audio.Rms(s, from, s.Length)).ToArray();
            t.Expect(rms[0] > 0.002 && rms[2] > 0.002 && rms[3] > 0.0005, $"a channel the song uses is silent: square 1 {rms[0]:0.0000}, triangle {rms[2]:0.0000}, noise {rms[3]:0.0000}");
            t.Expect(rms[1] < 1e-5, $"square 2 (the song has nothing on it) sounds: {rms[1]:0.000000}");
            // the stems add up to the mix (as rom-stems measures it: correlation around the means)
            var sum = new float[mix.Length];
            for (int i = 0; i < mix.Length; i++) sum[i] = stems[0][i] + stems[1][i] + stems[2][i] + stems[3][i];
            double corr = Correlation(mix, sum, from);
            t.Expect(corr > 0.9, $"the four stems do not add up to the mix (correlation {corr:0.000})");
            t.Note($"stems rms: square 1 {rms[0]:0.0000}, square 2 {rms[1]:0.000000}, triangle {rms[2]:0.0000}, noise {rms[3]:0.0000}; sum vs mix correlation {corr:0.0000}");
        }
        finally { foreach (var q in tones) q.Dispose(); }
    }

    // ------------------------------------------------------------------------------------------------ FL's transport

    private void SongTransport(T t)
    {
        using var p = SongPlugin();
        p.Test(120, 0, 1);
        try
        {
            var b = SongConvert.Convert(MidiFixtures.Synthetic(), out _)!;
            t.Expect(SOpen(p, WriteMid("transport", MidiFixtures.Synthetic())) == 0, "the open was refused");
            t.Expect(SWait(p, () => p.Test(HkSRunning) == 1), "the song did not become the game");
            SLatch(p, 100);
            SWait(p, () => p.Test(HkSNum, 11) == 1, 10000);
            long r0 = p.Test(HkSNum, 9);
            p.Dispatcher(Fpd.SetPlaying, 0, 1);                                 // FL's Play
            var after = SLatch(p, 600);
            var sc = MidiModel.Best(b.Channels, after, 480, 0, 8, MidiChecks.Skip);
            t.Expect(p.Test(HkSNum, 9) == r0 + 1, "FL's transport start did not restart the song");
            t.Expect(sc.Start == 0 && sc.Worst >= 1.0, $"after FL's Play the decode began in frame {sc.Start} ({sc.Agree}/{sc.Of}), expected the first frame");
            // the first sound after FL's Play: within one frame (rendering, as FL does)
            p.Dispatcher(Fpd.SetPlaying, 0, 0);
            t.Expect(p.Test(HkSStopped) == 1, "FL's transport stop did not stop the song");
            var quiet = RenderBlocks(p, 0.4);
            t.Expect(Audio.Rms(quiet, 4800, quiet.Length) < 1e-6, "the song sounds after FL's Stop");
            SWait(p, () => p.Test(HkSNum, 11) == 1, 10000);
            p.Dispatcher(Fpd.SetPlaying, 0, 1);
            var played = RenderBlocks(p, 0.3);
            int first = Array.FindIndex(played, v => Math.Abs(v) > 1e-4);
            double ms = first * 1000.0 / 48000;
            t.Expect(first >= 0 && ms < 33.4, $"the song's first sound came {ms:0.0} ms after FL's Play (expected within two NES frames, 33.3 ms)");
            t.Note($"FL's Play -> first sound after {ms:0.0} ms; FL's Stop -> silent; the decode after a transport start begins in frame {sc.Start}");
            // a repeated "playing" (the other instances of the project say it too) does not restart again; follow off: nothing happens
            long r1 = p.Test(HkSNum, 9);
            p.Dispatcher(Fpd.SetPlaying, 0, 1);
            SLatch(p, 4);
            t.Expect(p.Test(HkSNum, 9) == r1, "a repeated transport start restarted the song again");
            p.Test(HkSFollow, 1, 0);
            p.Dispatcher(Fpd.SetPlaying, 0, 0);
            p.Dispatcher(Fpd.SetPlaying, 0, 1);
            SLatch(p, 4);
            t.Expect(p.Test(HkSStopped) == 0 && p.Test(HkSNum, 9) == r1, "with follow off, FL's transport still stops or restarts the song");
            p.Test(HkSFollow, 1, 1);
        }
        finally { p.Test(120, 0, 0); }
    }

    // ------------------------------------------------------------------------------------------------ Reload after another export

    private void SongReload(T t)
    {
        string dir = MidiDir();
        string mid = Path.Combine(dir, "reload.mid");
        File.WriteAllBytes(mid, MidiFixtures.Synthetic());
        using var p = SongPlugin();
        t.Expect(SOpen(p, mid) == 0 && SWait(p, () => p.Test(HkSRunning) == 1), "the first export did not load");
        long gen = p.Test(HkSGen);
        // unchanged file: it says so
        t.Expect(p.Test(HkSReload) == 0, "Reload was refused");
        SWait(p, () => p.Test(HkSGen) > gen);
        t.Expect(SText(p, 0).Contains("has not changed"), $"Reload of an unchanged file said '{SText(p, 0)}'");
        // the user exports again over the same file
        var dense = Stress("dense").Midi;
        File.WriteAllBytes(mid, dense);
        gen = p.Test(HkSGen);
        t.Expect(p.Test(HkSReload) == 0, "Reload was refused");
        t.Expect(SWait(p, () => p.Test(HkSGen) > gen && p.Test(HkSRunning) == 1), "Reload did not load the new export");
        t.Expect(p.Test(HkSNum, 3) == SongConvert.Convert(dense, out _)!.Facts.LoopFrames, $"after Reload the loop is {p.Test(HkSNum, 3)} frames, not the new export's");
        var rom = SRom(p, dir);
        t.Expect(rom != null && rom.AsSpan().SequenceEqual(LibraryRom(dense, "reload")), "after Reload the plugin plays a ROM other than the new export's");
        t.Expect(SText(p, 0).Contains("the new export plays"), $"Reload said '{SText(p, 0)}'");
        // the file is gone: refused, the song plays on
        File.Delete(mid);
        long stamp = p.Test(HkSStamp);
        p.Test(HkSReload);
        SWait(p, () => p.Test(HkSStamp) > stamp, 10000);
        t.Expect(SText(p, 0).Contains("could not be read") && p.Test(HkSRunning) == 1, $"Reload of a deleted file said '{SText(p, 0)}' (song running: {p.Test(HkSRunning)})");
    }

    // ------------------------------------------------------------------------------------------------ the FL project

    private static int FindSection(byte[] state, uint tag)
    {
        for (int i = 0; i + 8 <= state.Length; i++) if (BitConverter.ToUInt32(state, i) == tag) return i;
        return -1;
    }

    private void SongProject(T t)
    {
        var song = Stress("manychannels");
        byte[] state; byte[] rom; long frames; string facts;
        string dir = MidiDir();
        using (var p = SongPlugin())
        {
            p.Test(120, 0, 1);
            t.Expect(SOpen(p, WriteMid("project song", song.Midi)) == 0 && SReady(p), "the song did not load");
            SLatch(p, 150);
            rom = SRom(p, dir) ?? [];
            frames = p.Test(HkSFrames);
            facts = SText(p, 1);
            state = p.SaveState();
            t.Expect(FindSection(state, SongSection.Tag) >= 0, "the saved project has no SNG1 section");
            t.Expect(p.Test(HkSSection) > song.Midi.Length, $"the section ({p.Test(HkSSection)} bytes) cannot hold the {song.Midi.Length}-byte MIDI file");
            p.Test(120, 0, 0);
        }
        // reopened (a fresh instance: the one that saved is gone, as when FL opens the project)
        using (var q = lib.Create())
        {
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.Test(120, 0, 1);
            q.LoadState(state);
            t.Expect(SReady(q), $"the reopened project does not build the song: '{SText(q, 0)}'");
            SLatch(q, 0);                                                         // (the built game is taken, no frame is run)
            t.Expect(q.Test(HkSRunning) == 1, "the reopened project's game is not the song");
            var back = SRom(q, dir);
            t.Expect(back != null && back.AsSpan().SequenceEqual(rom), "the reopened project's ROM is not the saved one (different song or template)");
            t.Expect(SText(q, 1) == facts, "the reopened project's facts differ");
            t.Expect(q.Test(86) == 1 && q.Test(HkSFrames) == frames, $"the game's state was not put back (restore {q.Test(86)}, frame {q.Test(HkSFrames)} vs {frames})");
            t.Expect(SText(q, 0).Contains("converted again"), $"the window says '{SText(q, 0)}' after the reopen");
            q.Test(120, 0, 0);
        }
        // damaged: one byte of the MIDI flipped (the checksum catches it)
        int at = FindSection(state, SongSection.Tag);
        var damaged = (byte[])state.Clone(); damaged[at + 8 + 40] ^= 0x10;
        using (var q = lib.Create())
        {
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.LoadState(damaged);
            SWait(q, () => q.Test(9) == 1, 15000);
            t.Expect(q.Test(HkSRunning) == -1 && SText(q, 0).Contains("damaged"), $"a damaged section: song {q.Test(HkSRunning)}, window '{SText(q, 0)}'");
        }
        // newer: version 2 with a valid checksum
        var newer = (byte[])state.Clone();
        int len = BitConverter.ToInt32(newer, at + 4);
        newer[at + 8] = 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(newer.AsSpan(at + 8 + len - 4), Crc32.Of(newer.AsSpan(at + 8, len - 4)));
        using (var q = lib.Create())
        {
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.LoadState(newer);
            SWait(q, () => q.Test(9) == 1, 15000);
            t.Expect(q.Test(HkSRunning) == -1 && SText(q, 0).Contains("newer"), $"a newer section: song {q.Test(HkSRunning)}, window '{SText(q, 0)}'");
        }
        // a project without the section (an older one): the built-in game, no song, nothing said
        using (var old = SongPlugin())
        {
            var plain = old.SaveState();
            using var q = lib.Create();
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.LoadState(plain);
            WaitForRom(q);
            t.Expect(q.Test(HkSRunning) == -1 && SText(q, 0) == "" && q.Test(9) == 1, $"a project without the section: song {q.Test(HkSRunning)}, window '{SText(q, 0)}'");
        }
    }

    // ------------------------------------------------------------------------------------------------ what cannot be used

    private void SongRefusals(T t)
    {
        using var p = SongPlugin();
        p.Test(HkSReload);
        t.Expect(SText(p, 0).Contains("Nothing to reload"), $"Reload with no song said '{SText(p, 0)}'");
        t.Expect(SOpen(p, WriteMid("good", MidiFixtures.Synthetic())) == 0 && SWait(p, () => p.Test(HkSRunning) == 1), "the good song did not load");
        long gen = p.Test(HkSGen);
        foreach (var (name, bytes, expect) in new (string, byte[], string)[]
                 {
                     ("not a midi", System.Text.Encoding.UTF8.GetBytes("plain text, not a MIDI file"), "not a MIDI file"),
                     ("no notes", MidiFixtures.Refused().First(s => s.Name == "nonotes").Midi, "no notes"),
                     ("drums only", Stress("drumsonly").Midi, "only drums"),
                     ("cut short", MidiFixtures.Refused().First(s => s.Name == "cutshort").Midi, "not a MIDI file"),
                 })
        {
            long stamp = p.Test(HkSStamp);
            SOpen(p, WriteMid(name, bytes));
            SWait(p, () => p.Test(HkSStamp) > stamp, 10000);
            string said = SText(p, 0);
            t.Expect(said.Contains("was not loaded") && said.Contains(expect) && said.Contains("Nothing changed"), $"{name}: the window said '{said}'");
            t.Expect(p.Test(HkSGen) == gen && p.Test(HkSRunning) == 1, $"{name}: the song that was playing was replaced or stopped");
        }
        // a template the code does not know: Test MIDI export refuses, with the reason
        string tdir = MidiDir();
        var (rom, manifest, _) = MidiFixtures.Bundled();
        File.WriteAllBytes(Path.Combine(tdir, "v2.nes"), rom);
        File.WriteAllText(Path.Combine(tdir, "v2.txt"), manifest.Replace("version = 1", "version = 2"));
        fixed (char* c = Path.Combine(tdir, "v2.txt") + "\0") t.Expect(p.Test(HkSTemplate, 0, (nint)c) == 0, "the test template could not be set");
        try
        {
            t.Expect(p.Test(HkSTemplateOk) == 0, "a version-2 template is accepted");
            long stamp = p.Test(HkSStamp);
            SOpen(p, WriteMid("good2", MidiFixtures.Synthetic()));
            SWait(p, () => p.Test(HkSStamp) > stamp, 10000);
            t.Expect(SText(p, 0).Contains("cannot run in this build") && SText(p, 0).Contains("version 2"), $"a version-2 template: the window said '{SText(p, 0)}'");
            t.Expect(p.Test(HkSGen) == gen, "a song was loaded with a template the code does not know");
        }
        finally { p.Test(HkSTemplate, 0, 0); }
        t.Expect(p.Test(HkSTemplateOk) == 1, "the built-in template is not usable");
    }

    // ------------------------------------------------------------------------------------------------ nothing allocated on the audio thread

    private void SongAlloc(T t)
    {
        using var p = SongPlugin();
        var tones = new List<PluginInstance>();
        try
        {
            t.Expect(SOpen(p, WriteMid("alloc", Stress("dense").Midi)) == 0 && SWait(p, () => p.Test(HkSRunning) == 1), "the song did not load");
            for (int c = 0; c < 4; c++) { var q = lib.Create(); q.Dispatcher(Fpd.SetSampleRate, 0, 48000); q.SetParam(PEmulator, 8); q.SetParam(PChannel, c + 1); tones.Add(q); }
            var all = new List<PluginInstance> { p }; all.AddRange(tones);
            var buf = new float[1024];
            long Render(int blocks)
            {
                long sum = 0;
                for (int b = 0; b < blocks; b++)
                    foreach (var i in all)
                    {
                        long a0 = i.Test(4);
                        fixed (float* f = buf) { i.NewTick(); i.GenRender(f, 512); }
                        sum += i.Test(4) - a0;
                    }
                return sum;
            }
            Render(60);                                                            // stems made, rings warm
            SWait(p, () => p.Test(HkSNum, 11) == 1, 10000);
            GC.Collect();
            long steady = Render(150);
            long a1 = p.Test(4); p.Dispatcher(Fpd.SetPlaying, 0, 1); long dispatch = p.Test(4) - a1;
            long restart = Render(60);
            long r0 = p.Test(HkSNum, 9);
            t.Expect(r0 >= 1, "the transport start did not restart the song");
            p.Dispatcher(Fpd.SetPlaying, 0, 0);
            long stopped = Render(30);
            SWait(p, () => p.Test(HkSNum, 11) == 1, 10000);
            p.Dispatcher(Fpd.SetPlaying, 0, 1);
            long again = Render(60);
            t.Expect(p.Test(HkSNum, 9) > r0, "the second transport start did not restart the song");
            long total = steady + dispatch + restart + stopped + again;
            t.Expect(total == 0, $"{total:N0} bytes allocated on the audio thread (steady {steady}, FPD_SetPlaying {dispatch}, around a restart {restart}, stopped {stopped}, a second restart {again}); must be 0");
            t.Note($"allocation on the audio thread with Mix and the four stems claimed: {total} B over {(150 + 60 + 30 + 60) * 5} render calls, two transport restarts and a stop");
        }
        finally { foreach (var q in tones) q.Dispose(); }
    }
}
