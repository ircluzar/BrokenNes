using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using BrokenNes.Workshop.Debug;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging.Capture;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace BrokenNes.Workshop.Bugs;

/// <summary>
/// <c>--bug-test [--only media,ring,bundle,places,games,speed] [--keep DIR]</c>: milestone D6's suite (docs/projects/brokendev-debugging-plan.md):
/// the bug capture's rolling buffer and bundles. The capture leaves the game frame-identical (armed against off, every frame's picture and
/// RAM) on the NES, SNES, WideMess and Game Boy; it allocates nothing between its once-a-second states; a capture replays exactly (every
/// keyframe and the last picture) from its bundle folder and from Lite's zip; a break (a state load) starts the history again and says
/// why; the places a bundle goes. Every check has a negative control. The user's games run when they are on this machine (a missing one
/// is NOT RUN, never a pass). Exit 0 when every check passes.
/// </summary>
internal static class BugTestCli
{
    private static int pass, fail, notRun;
    private static readonly List<string> failures = new();

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
        if (ok) pass++; else { fail++; failures.Add(name + ": " + detail); }
    }

    private static void NotRun(string name, string why) { Console.WriteLine($"  NOT RUN  {name}: {why}"); notRun++; }

    private static bool Throws(Action a, out string message)
    {
        try { a(); message = "no error"; return false; }
        catch (Exception e) { message = e.Message; return true; }
    }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        var only = Opt("only", "media,ring,bundle,places,games,speed").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string dir = Opt("keep", Path.Combine(Path.GetTempPath(), "brokennes-bug-test-" + Environment.ProcessId));
        Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"bug capture tests in {dir}");

        var (planted, _) = MovieTestRoms.PlantedGame(MovieTestRoms.ParseSettings("version 1\nstart Start\nfloor 1\nbonus 1\n"));
        string nes = DebugTestRoms.Write(dir, "planted.nes", planted);
        string snes = DebugTestRoms.Write(dir, "capture.sfc", DebugTestRoms.Snes().rom);
        string gb = DebugTestRoms.Write(dir, "capture.gb", DebugTestRoms.GameBoy().rom);

        void Group(string key, string title, Action body)
        {
            if (!only.Contains(key)) return;
            Console.WriteLine($"== {title}");
            try { body(); }
            catch (Exception e) { Check($"{title}: ran to the end", false, e.GetType().Name + ": " + e.Message + " " + e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()); }
        }

        Group("media", "the picture and sound files", Media);
        Group("ring", "the rolling buffer leaves the game unchanged", () => Ring(nes, snes, gb));
        Group("bundle", "captures replay exactly", () => Bundles(nes, snes, gb, dir));
        Group("places", "where a bundle goes", () => Places(dir));
        Group("games", "the user's games (when present)", () => Games(dir));
        Group("speed", "what the capture costs", () => Speed(nes, dir));

        Console.WriteLine();
        Console.WriteLine($"bug tests: {pass} passed, {fail} failed, {notRun} not run ({sw.Elapsed.TotalSeconds:F1} s)");
        foreach (var f in failures) Console.WriteLine("  FAILED " + f);
        return fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------------------ a machine driven like the apps drive it

    /// <summary>A game run frame by frame as the desktop app runs it: the pads, the frame, the sound drained into the ring (when armed).</summary>
    private sealed class Rig : IDisposable
    {
        public readonly MovieMachine M;
        public readonly BugCaptureRing? Ring;
        private readonly short[] pcm = new short[8192];
        private readonly float[] mono = new float[4096];
        public long Frames;
        public Rig(string rom, string? console, bool armed, Func<BugCaptureSource, BugCaptureSource>? wrap = null)
        {
            M = MovieMachine.Open(rom, console, "", "fceux", false, false);
            if (!armed) return;
            Ring = new BugCaptureRing();
            var src = M.Host.Nes != null ? BugCaptureSource.For(M.Host.Nes) : BugCaptureSource.For(M.Host.Session!);
            if (wrap != null) src = wrap(src);
            Ring.Attach(src, 44100, M.Host.Session?.FramesPerSecond ?? 60, powerOn: true);
        }
        public void Frame(PadButtons p1, PadButtons p2 = PadButtons.None)
        {
            Ring?.Frame(p1, p2);
            M.Frame(p1, p2);
            Frames++;
            Drain();
        }
        private void Drain()
        {
            if (M.Host.Nes is { } n)
            {
                var a = n.GetAudioBuffer();
                if (a.Length > 0) Ring?.Audio(a, a.Length);
                return;
            }
            var s = M.Host.Session!;
            int k;
            while ((k = s.ReadSamples(pcm)) > 0)
            {
                int frames = Math.Min(k / 2, mono.Length);
                for (int i = 0; i < frames; i++) mono[i] = (pcm[i * 2] + pcm[i * 2 + 1]) / 65536f;
                Ring?.Audio(mono, frames);
            }
        }
        public void Dispose() => M.Dispose();
    }

    /// <summary>The pads of a scripted session at a frame: presses spread over the run so the game sees input all the time.</summary>
    private static (PadButtons, PadButtons) Pads(long f, string console)
    {
        PadButtons p1 = PadButtons.None, p2 = PadButtons.None;
        if (f % 240 is >= 60 and < 66) p1 |= PadButtons.Start;
        if (f % 97 < 20) p1 |= PadButtons.Right;
        if (f % 151 < 9) p1 |= PadButtons.A;
        if (console != "gb" && f % 131 < 7) p2 |= PadButtons.B;
        return (p1, p2);
    }

    // ------------------------------------------------------------------------------------------------ media

    private static void Media()
    {
        var px = new uint[7 * 5];
        for (int i = 0; i < px.Length; i++) px[i] = 0xFF000000u | (uint)(i * 0x050307);
        byte[] png = BugMedia.Png(px, 7, 5);
        var (back, w, h) = BugMedia.ReadPng(png);
        Check("a PNG reads back pixel for pixel", w == 7 && h == 5 && back.SequenceEqual(px), $"{png.Length} bytes, {w} x {h}");
        var bad = (byte[])png.Clone(); bad[40] ^= 0x55;
        Check("NEGATIVE: a damaged PNG is refused (its chunk CRC)", Throws(() => BugMedia.ReadPng(bad), out string why), why);
        var wav = BugMedia.Wav(new[] { 0f, 0.5f, -1f, 2f }, 32000);
        Check("a WAV: RIFF, mono 16-bit at the rate, the samples clipped", Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && BitConverter.ToInt32(wav, 24) == 32000 && BitConverter.ToInt16(wav, 22) == 1
              && BitConverter.ToInt16(wav, 44 + 2) == 16384 && BitConverter.ToInt16(wav, 44 + 4) == -32767 && BitConverter.ToInt16(wav, 44 + 6) == 32767 && wav.Length == 44 + 8, $"{wav.Length} bytes");
    }

    // ------------------------------------------------------------------------------------------------ ring: frame-identical, no allocation

    private static void Ring(string nes, string snes, string gb)
    {
        foreach (var (name, rom, console, frames) in new[] { ("NES", nes, "nes", 900L), ("SNES", snes, "snes", 400L), ("Game Boy", gb, "gb", 600L) })
        {
            using var off = new Rig(rom, console, false);
            using var on = new Rig(rom, console, true);
            long firstDiff = -1;
            for (long f = 0; f < frames; f++)
            {
                var (a, b) = Pads(f, console);
                off.Frame(a, b); on.Frame(a, b);
                if (firstDiff < 0 && (off.M.Picture() != on.M.Picture() || off.M.Ram() != on.M.Ram())) firstDiff = f;
            }
            Check($"{name}: armed, every frame's picture and work RAM are the ones with the capture off", firstDiff < 0, firstDiff < 0 ? $"{frames} frames identical, {on.Ring!.HeldSeconds:F1} s held" : $"first difference after frame {firstDiff}");
        }
        // the negative control: a capture source whose state-taking writes the game's RAM IS seen by the same comparison
        {
            using var off = new Rig(nes, "nes", false);
            Rig? onRef = null;
            using var on = onRef = new Rig(nes, "nes", true, s => new BugCaptureSource(s.ConsoleKey, () => { onRef!.M.Host.Nes!.PokeSystemRam(0x10, 0x5A); return s.SaveState!(); }, "", s.PictureHash, s.RamHash, s.CopyPicture));
            long firstDiff = -1;
            for (long f = 0; f < 300 && firstDiff < 0; f++) { var (a, b) = Pads(f, "nes"); off.Frame(a, b); on.Frame(a, b); if (off.M.Ram() != on.M.Ram()) firstDiff = f; }
            Check("NEGATIVE: a capture that writes the game's RAM when it takes a state is caught by the same comparison", firstDiff >= 0, $"first difference after frame {firstDiff}");
        }
        // no allocation between the states (the plan: a preallocated ring buffer, nothing per frame)
        {
            using var r = new Rig(nes, "nes", true);
            for (long f = 0; f < 200; f++) { var (a, b) = Pads(f, "nes"); r.Frame(a, b); }
            var ring = r.Ring!;
            var samples = new float[735];
            long worst = 0, total = 0; int measured = 0;
            for (long f = 200; f < 800; f++)
            {
                bool stateFrame = (ring.FrameCount - 0) % ring.Every == 0;
                var (a, b) = Pads(f, "nes");
                long before = GC.GetAllocatedBytesForCurrentThread();
                ring.Frame(a, b);
                ring.Audio(samples, samples.Length);
                long used = GC.GetAllocatedBytesForCurrentThread() - before;
                r.M.Frame(a, b); r.M.Host.Nes!.GetAudioBuffer();
                if (stateFrame) continue;
                worst = Math.Max(worst, used); total += used; measured++;
            }
            Check("the ring's per-frame work (pads, sound) allocates nothing between its once-a-second states", worst == 0, $"{measured} frames measured, {total} bytes allocated (worst {worst})");
            ring.Break("the allocation test");   // the next frame takes a state
            long b3 = GC.GetAllocatedBytesForCurrentThread();
            ring.Frame(PadButtons.None, PadButtons.None);
            long stateAlloc = GC.GetAllocatedBytesForCurrentThread() - b3;
            Check("NEGATIVE: the measurement sees the allocation of a frame that takes a state", stateAlloc > 0, $"{stateAlloc} bytes (the machine's state, {ring.LastStateBytes} bytes, {ring.LastStateMs:F2} ms)");
        }
    }

    // ------------------------------------------------------------------------------------------------ bundles

    private static BugMeta Meta(string rom, string cores, string note = "test") => new()
    {
        Note = note, App = "BrokenNes Workshop --bug-test", RomName = Path.GetFileName(rom), Rom = File.ReadAllBytes(rom), RomPath = rom, Cores = cores,
    };

    private static (int code, JsonObject j) PlayDir(string bundle, string? rom = null) => BugCli.Play(BugBundleReader.Open(bundle), rom, new List<string>(), false, null);

    private static void Bundles(string nes, string snes, string gb, string dir)
    {
        string bugs = Path.Combine(dir, "bugs");
        foreach (var (name, rom, console, frames) in new[] { ("NES", nes, "nes", 2500L), ("SNES", snes, "snes", 2200L), ("Game Boy", gb, "gb", 2300L) })
        {
            using var r = new Rig(rom, console, true);
            for (long f = 0; f < frames; f++) { var (a, b) = Pads(f, console); r.Frame(a, b); }
            var d = r.Ring!.Capture();
            var files = BugBundle.Files(d, Meta(rom, r.M.Cores));
            string b1 = BugBundle.WriteFolder(bugs, files, DateTime.Now);
            var mv = Movie.Load(Path.Combine(b1, BugBundle.MovieFile));
            bool span = d.Frames >= 30 * 60 && d.Frames <= 31 * 60 + 1 && mv.Start == MovieStart.State && mv.Frames == d.Frames;
            Check($"{name}: a capture after {frames} frames holds the last 30 s from a state", span, $"{d.Frames} frames ({d.Seconds:F1} s), start {mv.Start}, {mv.Keys.Count} keyframes, {d.Pictures.Count} pictures, {d.Audio.Length} samples");
            var (code, j) = PlayDir(b1);
            Check($"{name}: the bundle replays exactly (every keyframe and the last picture)", code == 0, (string)j["summary"]!);
            bool media = File.Exists(Path.Combine(b1, "audio.wav")) && File.Exists(Path.Combine(b1, "last.png")) && File.Exists(Path.Combine(b1, "strip.png")) && Directory.GetFiles(Path.Combine(b1, "pictures")).Length >= 30
                         && File.Exists(Path.Combine(b1, "rom", Path.GetFileName(rom)));
            Check($"{name}: the bundle has its sound, a picture a second, the strip, the last picture and its ROM", media, string.Join(", ", Directory.GetFiles(b1, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(b1, p)).Take(8)) + " ...");
            if (name == "NES")
            {
                // the negative controls: one input moved (a capture of the first 20 s, whose movie holds the title's Start press), the start state of another moment
                using (var early = new Rig(rom, console, true))
                {
                    for (long f = 0; f < 1200; f++) { var (a, b) = Pads(f, console); early.Frame(a, b); }
                    string be = BugBundle.WriteFolder(bugs, BugBundle.Files(early.Ring!.Capture(), Meta(rom, early.M.Cores, "first 20 s")), DateTime.Now);
                    var (ce, je) = PlayDir(be);
                    Check("a capture of the first 20 s (from the state before the first frame) replays exactly", ce == 0, (string)je["summary"]!);
                    var lines = File.ReadAllText(Path.Combine(be, BugBundle.MovieFile)).Split('\n').ToList();
                    long FrameOf(string l) => long.Parse(l.Split(' ')[1]);
                    int at = Enumerable.Range(0, lines.Count - 1).First(i => lines[i].StartsWith("i ") && lines[i].Contains("Start") && lines[i + 1].StartsWith("i ") && FrameOf(lines[i + 1]) > FrameOf(lines[i]) + 1);
                    var parts = lines[at].Split(' '); parts[1] = (long.Parse(parts[1]) + 1).ToString(); lines[at] = string.Join(' ', parts);
                    string b2 = CopyBundle(be, dir, "late-start");
                    File.WriteAllText(Path.Combine(b2, BugBundle.MovieFile), string.Join('\n', lines));
                    var (c2, j2) = PlayDir(b2);
                    Check("NEGATIVE: the same capture with the title's Start press a frame late is off course", c2 == 1, (string)j2["summary"]!);
                }
                string b3 = CopyBundle(b1, dir, "wrong-state");
                var wrong = Movie.Load(Path.Combine(b1, BugBundle.MovieFile));
                wrong.StartState = Encoding.UTF8.GetBytes(r.M.Host.Nes!.SaveState());
                wrong.Save(Path.Combine(b3, BugBundle.MovieFile));
                var (c3, j3) = PlayDir(b3);
                Check("NEGATIVE: the same input from the state of another moment is off course", c3 == 1, (string)j3["summary"]!);
                // Lite's download: the same files as one zip
                string zip = Path.Combine(dir, BugBundle.ZipName(Path.GetFileName(rom), DateTime.Now));
                File.WriteAllBytes(zip, BugBundle.Zip(files));
                var (cz, jz) = PlayDir(zip);
                Check("the same capture as Lite's zip replays exactly", cz == 0, (string)jz["summary"]!);
                var info = BugBundleReader.Open(zip);
                Check("the zip's bug.json names the ROM, the cores, the history and the note", info.S("rom", "sha256").Length == 64 && info.S("cores") == r.M.Cores && info.S("history", "start") == "state" && info.S("note") == "test", $"{info.S("rom", "name")} {info.S("cores")} {info.S("history", "why")}");
                var listed = BugCli.List(bugs);
                Check("the bugs folder lists its bundles", listed.Count >= 1 && listed.All(x => x!["error"] == null), $"{listed.Count} bundle(s)");
            }
        }

        // a break: a state load (here: the game reset) starts the history again, and the bundle says why
        {
            using var r = new Rig(nes, "nes", true);
            for (long f = 0; f < 1200; f++) { var (a, b) = Pads(f, "nes"); r.Frame(a, b); }
            var st = r.M.Host.SaveState();
            for (long f = 1200; f < 1500; f++) { var (a, b) = Pads(f, "nes"); r.Frame(a, b); }
            r.M.Host.LoadState(st);
            r.Ring!.Break("a state was loaded");
            for (long f = 1500; f < 2100; f++) { var (a, b) = Pads(f, "nes"); r.Frame(a, b); }
            var d = r.Ring.Capture();
            string b1 = BugBundle.WriteFolder(bugs, BugBundle.Files(d, Meta(nes, r.M.Cores, "after a load")), DateTime.Now);
            var info = BugBundleReader.Open(b1);
            Check("a break starts the history again: the capture holds only the 10 s since the load, and says why", d.Frames == 600 && info.S("history", "why").Contains("a state was loaded"), info.S("history", "why"));
            var (code, j) = PlayDir(b1);
            Check("the capture after a break replays exactly", code == 0, (string)j["summary"]!);
            // the negative control: the same load WITHOUT telling the ring: the movie runs across the load and the replay sees it
            using var r2 = new Rig(nes, "nes", true);
            for (long f = 0; f < 1200; f++) { var (a, b) = Pads(f, "nes"); r2.Frame(a, b); }
            var st2 = r2.M.Host.SaveState();
            for (long f = 1200; f < 1500; f++) { var (a, b) = Pads(f, "nes"); r2.Frame(a, b); }
            r2.M.Host.LoadState(st2);
            for (long f = 1500; f < 2100; f++) { var (a, b) = Pads(f, "nes"); r2.Frame(a, b); }
            var d2 = r2.Ring!.Capture();
            string b2 = BugBundle.WriteFolder(bugs, BugBundle.Files(d2, Meta(nes, r2.M.Cores, "load not told")), DateTime.Now);
            var (c2, j2) = PlayDir(b2);
            Check("NEGATIVE: a state load the ring is not told of makes a capture that is off course (and the replay says so)", c2 == 1, (string)j2["summary"]!);
        }

        // a core swapped while the game runs: the ring sees the machine change at its next second and starts again there (the negative
        // control needs a game the swap visibly changes: VRUN, in the games group)
        Swap(nes, "the planted game", bugs, negative: false);

        // a console without save states: the movie starts at the power-on while the history reaches it; after that, no movie (said)
        {
            using var r = new Rig(nes, "nes", true, s => new BugCaptureSource(s.ConsoleKey, null, "This console has no save states yet.", s.PictureHash, s.RamHash, s.CopyPicture));
            for (long f = 0; f < 900; f++) { var (a, b) = Pads(f, "nes"); r.Frame(a, b); }
            var d = r.Ring!.Capture();
            string b1 = BugBundle.WriteFolder(bugs, BugBundle.Files(d, Meta(nes, r.M.Cores, "no states")), DateTime.Now);
            var (code, j) = PlayDir(b1);
            Check("no save states, 15 s after the power-on: the movie starts at the power-on and replays exactly", d.Start == MovieStart.PowerOn && d.Frames == 900 && code == 0, (string)j["summary"]!);
            for (long f = 900; f < 2400; f++) { var (a, b) = Pads(f, "nes"); r.Frame(a, b); }
            var d2 = r.Ring.Capture();
            string b2 = BugBundle.WriteFolder(bugs, BugBundle.Files(d2, Meta(nes, r.M.Cores, "no states, later")), DateTime.Now);
            var (c2, j2) = PlayDir(b2);
            Check("NEGATIVE: no save states, 40 s after the power-on: no movie, the bundle says why, the replay refuses", d2.NoMovieWhy != null && c2 == 3 && Directory.GetFiles(Path.Combine(b2, "pictures")).Length >= 30, d2.NoMovieWhy ?? "(a movie)");
        }
        // nothing to capture
        {
            using var r = new Rig(nes, "nes", true);
            Check("NEGATIVE: a capture before the first frame is refused, with the reason", Throws(() => r.Ring!.Capture(), out string why) && why.Contains("no frame"), why);
        }
        Check("NEGATIVE: a folder that is not a bundle is refused", Throws(() => BugBundleReader.Open(dir), out string w2) && w2.Contains("bug.json"), w2);
    }

    /// <summary>The PPU swapped to FMC after 25 s: watched, the history starts again and replays; not watched (the negative), the capture runs across the swap and is off course.</summary>
    private static void Swap(string rom, string name, string bugs, bool negative)
    {
        BugCaptureData Run(bool watched)
        {
            using var r = new Rig(rom, "nes", true, watched ? null : s => new BugCaptureSource(s.ConsoleKey, s.SaveState, "", s.PictureHash, s.RamHash, s.CopyPicture, null));
            for (long f = 0; f < 1500; f++) { var (a, b) = Pads(f, "nes"); r.Frame(a, b); }
            r.M.Host.Nes!.SetPpuCore("FMC");
            for (long f = 1500; f < 2400; f++) { var (a, b) = Pads(f, "nes"); r.Frame(a, b); }
            return r.Ring!.Capture();
        }
        var d = Run(true);
        string b1 = BugBundle.WriteFolder(bugs, BugBundle.Files(d, Meta(rom, "FIX/FMC/FIX", "core swap")), DateTime.Now);
        var info = BugBundleReader.Open(b1);
        var (code, j) = PlayDir(b1);
        Check($"{name}: a core swapped while the game runs starts the history again (said), and the capture replays exactly on the new cores",
              code == 0 && d.Frames <= 900 && info.S("history", "why").Contains("machine changed"), $"{info.S("history", "why")}; {(string)j["summary"]!}");
        if (!negative) return;
        var d2 = Run(false);
        string b2 = BugBundle.WriteFolder(bugs, BugBundle.Files(d2, Meta(rom, "FIX/FMC/FIX", "core swap, not watched")), DateTime.Now);
        var (c2, j2) = PlayDir(b2);
        Check($"NEGATIVE: {name}: the same swap with the machine not watched gives a capture across it, which the replay finds off course", c2 == 1 && d2.Frames >= 1800, (string)j2["summary"]!);
    }

    private static string CopyBundle(string from, string dir, string name)
    {
        string to = Path.Combine(dir, "variants", name);
        if (Directory.Exists(to)) Directory.Delete(to, true);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string t = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t);
        }
        return to;
    }

    // ------------------------------------------------------------------------------------------------ places

    private static void Places(string dir)
    {
        string home = Path.Combine(dir, "bdhome"), ws = Path.Combine(dir, "MyGame_dev"), deflt = Path.Combine(dir, "default-bugs");
        Directory.CreateDirectory(Path.Combine(home, "out", "mygame")); Directory.CreateDirectory(Path.Combine(home, "out", "frozen")); Directory.CreateDirectory(Path.Combine(home, "out", "failed"));
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, "game.mlb"), "P:0010:main\n");
        string J(string s) => s.Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(home, "out", "mygame", "build.json"), $"{{\"project\":\"mygame\",\"title\":\"My game\",\"ok\":true,\"startedAtUtc\":\"2026-10-25T00:00:00Z\",\"workspace\":{{\"kind\":\"live\",\"path\":\"{J(ws)}\"}},\"outputs\":[{{\"id\":\"rom\",\"sha256\":\"aaaa\"}},{{\"id\":\"mlb\",\"published\":\"{J(Path.Combine(ws, "game.mlb"))}\"}}]}}");
        File.WriteAllText(Path.Combine(home, "out", "frozen", "build.json"), "{\"project\":\"frozen\",\"ok\":true,\"workspace\":{\"kind\":\"frozen\",\"path\":\"\"},\"outputs\":[{\"id\":\"rom\",\"sha256\":\"bbbb\"}]}");
        File.WriteAllText(Path.Combine(home, "out", "failed", "build.json"), "{\"project\":\"failed\",\"ok\":false,\"workspace\":{\"kind\":\"live\",\"path\":\"C:\\\\nowhere\"},\"outputs\":[{\"id\":\"rom\",\"sha256\":\"cccc\"}]}");
        string? oldHome = Environment.GetEnvironmentVariable("BROKENDEV_HOME"), oldBugs = Environment.GetEnvironmentVariable("BROKENNES_BUGS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("BROKENDEV_HOME", home);
            Environment.SetEnvironmentVariable("BROKENNES_BUGS_DIR", deflt);
            var syms = new List<(string, byte[])>();
            string p1 = BugPlaces.For("AAAA", out var pr1, out string w1, syms);
            Check("a project's ROM goes to its game workspace's bugs folder, with the symbols of its build record", p1 == Path.Combine(ws, "bugs") && pr1?.Id == "mygame" && syms.Count == 1, $"{p1}; {w1}; {syms.Count} symbol file(s)");
            string p2 = BugPlaces.For("bbbb", out var pr2, out string w2);
            Check("a project whose record names no workspace (a frozen build) goes to the default folder, and says so", p2 == deflt && pr2?.Id == "frozen" && w2.Contains("names no game workspace"), w2);
            string p3 = BugPlaces.For("cccc", out var pr3, out string w3);
            Check("NEGATIVE: a failed build's record is no project's ROM", p3 == deflt && pr3 == null, w3);
            string p4 = BugPlaces.For("dddd", out var pr4, out string w4);
            Check("NEGATIVE: any other ROM goes to the default folder", p4 == deflt && pr4 == null && w4.Contains("no BrokenDev project"), w4);
            Environment.SetEnvironmentVariable("BROKENNES_BUGS_DIR", null);
            string d = BugPlaces.DefaultFolder(), appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Check("the default folder is %LOCALAPPDATA%\\BrokenNes\\bugs, never the app's config or saves (%APPDATA%\\BrokenNes)", d.EndsWith(@"BrokenNes\bugs", StringComparison.OrdinalIgnoreCase) && !d.StartsWith(appData, StringComparison.OrdinalIgnoreCase), d);
            string twice = BugBundle.WriteFolder(Path.Combine(dir, "same-second"), new SortedDictionary<string, byte[]> { ["bug.json"] = new byte[] { 0x7B, 0x7D } }, new DateTime(2026, 1, 2, 3, 4, 5));
            string again = BugBundle.WriteFolder(Path.Combine(dir, "same-second"), new SortedDictionary<string, byte[]> { ["bug.json"] = new byte[] { 0x7B, 0x7D } }, new DateTime(2026, 1, 2, 3, 4, 5));
            Check("two captures in the same second get two folders (never one written over)", Path.GetFileName(twice) == "2026-01-02_030405" && Path.GetFileName(again) == "2026-01-02_030405-2", $"{Path.GetFileName(twice)}, {Path.GetFileName(again)}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BROKENDEV_HOME", oldHome);
            Environment.SetEnvironmentVariable("BROKENNES_BUGS_DIR", oldBugs);
        }
    }

    // ------------------------------------------------------------------------------------------------ the user's games

    private static void Games(string dir)
    {
        string home = Environment.GetEnvironmentVariable("BROKENDEV_HOME") is { Length: > 0 } e ? e : @"P:\BrokenDev\env";
        foreach (var (name, rom, console, frames) in new[]
        {
            ("VRUN", Path.Combine(home, "out", "vrun-nes", "vrun_game.nes"), "nes", 2400L),
            ("Super VRUN", Path.Combine(home, "out", "supervrun", "supervrun.sfc"), "snes", 2400L),
            ("VRUN: Vaults of Obsolete Code", Path.Combine(home, "out", "vrun-voc", "vrun_voc.gbc"), "gbc", 2400L),
        })
        {
            if (!File.Exists(rom)) { NotRun($"{name}: a capture replays exactly", $"{rom} is not built here"); continue; }
            using var r = new Rig(rom, console, true);
            for (long f = 0; f < frames; f++) { var (a, b) = Pads(f, console); r.Frame(a, b); }
            var d = r.Ring!.Capture();
            string b1 = BugBundle.WriteFolder(Path.Combine(dir, "games"), BugBundle.Files(d, Meta(rom, r.M.Cores, name)), DateTime.Now);
            var (code, j) = PlayDir(b1);
            Check($"{name}: a capture after {frames / 60} s of scripted play replays exactly", code == 0 && d.Frames >= 1800, $"{(string)j["summary"]!}; state {r.Ring.LastStateBytes / 1024} KB, {r.Ring.MaxStateMs:F1} ms at worst");
            if (name == "VRUN") Swap(rom, name, Path.Combine(dir, "games"), negative: true);
        }
    }

    // ------------------------------------------------------------------------------------------------ speed

    private static void Speed(string nes, string dir)
    {
        string home = Environment.GetEnvironmentVariable("BROKENDEV_HOME") is { Length: > 0 } e ? e : @"P:\BrokenDev\env";
        var roms = new List<(string, string, string)> { ("the planted NES game", nes, "nes") };
        string vrun = Path.Combine(home, "out", "vrun-nes", "vrun_game.nes"), sv = Path.Combine(home, "out", "supervrun", "supervrun.sfc");
        if (File.Exists(vrun)) roms.Add(("VRUN", vrun, "nes")); else NotRun("VRUN: speed", "not built here");
        if (File.Exists(sv)) roms.Add(("Super VRUN", sv, "snes")); else NotRun("Super VRUN: speed", "not built here");
        foreach (var (name, rom, console) in roms)
        {
            const int frames = 1200;
            double Fps(bool armed)
            {
                using var r = new Rig(rom, console, armed);
                for (int f = 0; f < 120; f++) { var (a, b) = Pads(f, console); r.Frame(a, b); }
                var sw = Stopwatch.StartNew();
                for (int f = 120; f < 120 + frames; f++) { var (a, b) = Pads(f, console); r.Frame(a, b); }
                return frames / sw.Elapsed.TotalSeconds;
            }
            // interleaved A/B (the CPU is shared with other agents): the best of three each
            double offBest = 0, onBest = 0;
            for (int i = 0; i < 3; i++) { offBest = Math.Max(offBest, Fps(false)); onBest = Math.Max(onBest, Fps(true)); }
            double cost = 100.0 * (offBest - onBest) / offBest;
            Check($"{name}: speed with the capture armed is within 5% of off (best of three, interleaved)", cost < 5, $"off {offBest:F0} fps, armed {onBest:F0} fps ({cost:+0.0;-0.0}%)");
        }
    }
}
