using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BrokenNes.Workshop.Debug;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace BrokenNes.Workshop.Movies;

/// <summary>
/// <c>--movie-test [--only format,play,export,diverge,test,back,games] [--keep DIR]</c>: milestone D5's suite (docs/projects/brokendev-debugging-plan.md):
/// the movie format, record and replay on the NES, SNES and Game Boy, the .fm2 / .bk2 exports, the divergence finder and the state diff by
/// symbol, the bisect test verdicts and step back. Every check has a negative control (the same code shown to say no). The user's games run
/// when they are on this machine (a missing one is NOT RUN, never a pass). Exit 0 when every check passes.
/// </summary>
internal static class MovieTestCli
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

    private const string Script = "60:Start,66:,200:A,260:";

    public static int Run(string[] args)
    {
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        var only = Opt("only", "format,play,export,diverge,test,back,games").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string dir = Opt("keep", Path.Combine(Path.GetTempPath(), "brokennes-movie-test-" + Environment.ProcessId));
        Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"{MovieFormat.Name} ({MovieFormat.Extension}) tests in {dir}");

        // the planted game: the good build, the regression (lives reach 0), another menu (Select starts), and a broken build
        string Planted(string name, string settings)
        {
            File.WriteAllText(Path.Combine(dir, name + ".txt"), settings);
            var (rom, mlb) = MovieTestRoms.PlantedGame(MovieTestRoms.ParseSettings(settings));
            File.WriteAllText(Path.Combine(dir, name + ".mlb"), mlb);
            return DebugTestRoms.Write(dir, name + ".nes", rom);
        }
        string good = Planted("good", "version 1\nstart Start\nfloor 1\nbonus 1\n");
        string bad = Planted("bad", "version 1\nstart Start\nfloor 0\nbonus 1\n");
        string menu = Planted("menu", "version 1\nstart Select\nfloor 1\nbonus 1\n");
        string harmless = Planted("harmless", "version 2\nstart Start\nfloor 1\nbonus 1\n");
        var goodSym = new List<string> { Path.ChangeExtension(good, ".mlb") };
        Movie Rec(string rom, string script, long frames, List<string>? sym = null, string cores = "", bool ntsc = false)
        {
            using var m = MovieMachine.Open(rom, null, cores, "fceux", ntsc, true, sym ?? goodSym);
            return MoviePlayer.Record(m, script, "", frames, 60, new[] { ("app_state", "[app_state]") }, "test");
        }
        PlayReport Play(Movie mv, string rom, List<string>? sym = null, string? cores = null)
        {
            using var m = MovieMachine.OpenFor(mv, rom, true, sym ?? goodSym, cores);
            return MoviePlayer.Play(mv, m);
        }
        var movie = Rec(good, Script, 600);
        string moviePath = Path.Combine(dir, "play" + MovieFormat.Extension);
        movie.Save(moviePath);

        void Group(string key, string title, Action body)
        {
            if (!only.Contains(key)) return;
            Console.WriteLine($"== {title}");
            try { body(); }
            catch (Exception e) { Check($"{title}: ran to the end", false, e.GetType().Name + ": " + e.Message + " " + e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()); }
        }

        Group("format", "the movie format", () => Format(movie, moviePath));
        Group("play", "record and replay", () => PlayTests(movie, good, bad, harmless, goodSym, Play, dir));
        Group("export", "the .fm2 and .bk2 exports", () => Exports(movie, good, dir));
        Group("diverge", "the divergence finder and the state diff by symbol", () => Diverge(movie, good, bad, goodSym, dir));
        Group("test", "bisect's test verdicts", () => Verdicts(movie, good, bad, menu, harmless, goodSym, dir));
        Group("back", "step back", () => Back(good, goodSym, dir));
        Group("games", "the user's games (when present)", () => Games(dir));

        Console.WriteLine();
        Console.WriteLine($"movie tests: {pass} passed, {fail} failed, {notRun} not run ({sw.Elapsed.TotalSeconds:F1} s)");
        foreach (var f in failures) Console.WriteLine("  FAILED " + f);
        return fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------------------ format

    private static void Format(Movie movie, string path)
    {
        string text = File.ReadAllText(path);
        var back = Movie.Parse(text);
        Check("the text round-trips (parse then write gives the same file)", back.ToText() == text, $"{text.Length} characters, {back.Inputs.Count} input changes, {back.Keys.Count} keyframes");
        Check("the first line is the format's magic and version (one constant to rename it)", text.StartsWith(MovieFormat.Magic + " " + MovieFormat.Version + "\n"), text.Split('\n')[0]);
        Check("the input reads back as verify's script", back.ToScript(1) == "0:,60:Start,66:,200:A,260:" || back.ToScript(1) == "60:Start,66:,200:A,260:", back.ToScript(1));
        Check("keyframes every 60 frames and at the end, with the course variable", back.Keys.Count == 10 && back.Keys[^1].Frame == 600 && back.Keys.All(k => k.Vars.Length == 1), $"{back.Keys.Count} keyframes, last after {back.Keys[^1].Frame}");
        var bads = new (string name, string text)[]
        {
            ("another file's first line", text.Replace(MovieFormat.Magic + " 1", "version 3")),
            ("a newer version", text.Replace(MovieFormat.Magic + " 1", MovieFormat.Magic + " 99")),
            ("no end line (cut short)", text.Replace("end\n", "")),
            ("an input past the length", text.Replace("frames 600", "frames 100")),
            ("input frames out of order", text.Replace("i 200 A -", "i 30 A -")),
            ("an unknown button", text.Replace("i 200 A -", "i 200 Turbo -")),
            ("a keyframe without its course value", text.Replace("k 60 ", "k 60 0 ").Replace(" 0\nk 120", "\nk 120").Replace("k 60 0 ", "k 60 ")),
            ("an unknown line", text.Replace("end\n", "zz 1\nend\n")),
        };
        foreach (var (name, t) in bads)
        {
            bool refused = Throws(() => Movie.Parse(t), out string msg) && t != text;
            Check($"NEGATIVE: {name} is refused", refused, msg);
        }
        // a state start keeps its state
        var st = new Movie { Console = "nes", RomName = "x.nes", RomSha256 = "00", Frames = 10, Start = MovieStart.State, StartState = Encoding.UTF8.GetBytes("{\"state\":1}") };
        st.SetPads(0, PadButtons.None, PadButtons.None);
        var st2 = Movie.Parse(st.ToText());
        Check("a movie that starts from a state carries the state (gzip + base64)", st2.Start == MovieStart.State && st2.StartState!.SequenceEqual(st.StartState!), $"{st2.StartState!.Length} bytes");
    }

    // ------------------------------------------------------------------------------------------------ record and replay

    private static void PlayTests(Movie movie, string good, string bad, string harmless, List<string> sym, Func<Movie, string, List<string>?, string?, PlayReport> play, string dir)
    {
        var r = play(movie, good, sym, null);
        Check("NES: a recorded movie replays in sync (every keyframe's picture, RAM and course)", r.InSync && r.KeysMatched == 10 && r.CourseLostFrame == null && r.SameRom, $"{r.KeysMatched} of {r.KeysChecked}, same ROM {r.SameRom}");
        var tampered = Movie.Parse(File.ReadAllText(Path.Combine(dir, "play" + MovieFormat.Extension)).Replace("i 60 Start -", "i 61 Start -"));
        var t = play(tampered, good, sym, null);
        Check("NEGATIVE: Start one frame later is off course (at the first keyframe after it)", !t.InSync && t.OffCourseFrame == 120, $"off course after {t.OffCourseFrame}: {t.OffCourseWhat}");
        var other = play(movie, bad, new List<string> { Path.ChangeExtension(bad, ".mlb") }, null);
        Check("NEGATIVE: another build is named as another ROM, and goes off course where it behaves differently", !other.SameRom && other.OffCourseFrame == 300 && other.KeysMatched == 4, $"same ROM {other.SameRom}, off course after {other.OffCourseFrame} ({other.KeysMatched} matched)");
        PlayReport ntsc;
        using (var m = MovieMachine.OpenFor(movie, good, true, sym, null, ntscOverride: true)) ntsc = MoviePlayer.Play(movie, m);
        Check("NEGATIVE: NTSC frames instead of the recorded 60 fps ones go off course (the game sees its input at other moments)", !ntsc.InSync, $"off course after {ntsc.OffCourseFrame}: {ntsc.OffCourseWhat}");
        var hl = play(movie, harmless, new List<string> { Path.ChangeExtension(harmless, ".mlb") }, null);
        Check("a harmless rebuild (another version byte) keeps its course though its RAM differs", hl.CourseLostFrame == null && !hl.SameRom && hl.OffCourseFrame == 60, $"course lost {hl.CourseLostFrame?.ToString() ?? "never"}, off course after {hl.OffCourseFrame} (the version byte)");

        // the debugger armed (every instruction hooked, the trace ring on) is frame-identical
        using (var m = MovieMachine.OpenFor(movie, good, true, sym))
        {
            m.Host.Dbg.TrackCallStack = true; m.Host.Dbg.TraceRing = true;
            var a = MoviePlayer.Play(movie, m);
            Check("the debugger armed during the replay changes nothing (every keyframe still matches)", a.InSync && a.KeysMatched == 10, $"{a.KeysMatched} matched, armed {m.Host.Dbg.IsArmed}");
        }
        using (var m = MovieMachine.OpenFor(movie, good, false))
        {
            var a = MoviePlayer.Play(movie, m);
            Check("no debugger at all: the same keyframes (the course is not checked, and says why)", a.InSync && a.CourseNote != null, a.CourseNote ?? "(no note)");
        }

        // the SNES and the Game Boy (the debugger test ROMs): a movie with input, replayed; a tampered keyframe is caught
        foreach (var (name, rom, file, console) in new[] { ("SNES", DebugTestRoms.Snes().rom, "movie.sfc", "snes"), ("Game Boy", DebugTestRoms.GameBoy().rom, "movie.gb", "gb") })
        {
            string p = DebugTestRoms.Write(dir, file, rom);
            Movie mv;
            using (var m = MovieMachine.Open(p, console, "", "fceux", false, true)) mv = MoviePlayer.Record(m, "10:Right+A,40:,50:Start,52:", "5:B,9:", 180, 30, Array.Empty<(string, string)>(), "");
            mv = Movie.Parse(mv.ToText());
            PlayReport rr;
            using (var m = MovieMachine.OpenFor(mv, p, true)) rr = MoviePlayer.Play(mv, m);
            Check($"{name}: a movie (both pads) replays in sync", rr.InSync && rr.KeysMatched == 6 && mv.UsesPad2, $"{rr.KeysMatched} of {rr.KeysChecked} keyframes, {mv.Console} {mv.Cores}");
            var k = mv.Keys[3]; k.Ram ^= 1;
            using (var m = MovieMachine.OpenFor(mv, p, true)) rr = MoviePlayer.Play(mv, m);
            Check($"NEGATIVE: {name}: a keyframe that does not match is caught", !rr.InSync && rr.OffCourseFrame == k.Frame, $"off course after {rr.OffCourseFrame}");
        }
        Check("NEGATIVE: a core the console's menu does not offer is refused (never a quiet substitute)",
              Throws(() => MovieMachine.Open(DebugTestRoms.Write(dir, "movie2.gb", DebugTestRoms.GameBoy().rom), "gb", "NOPE/NOPE/NOPE", "fceux", false, false).Dispose(), out string why) && why.Contains("does not offer"), why);
    }

    // ------------------------------------------------------------------------------------------------ exports

    private static void Exports(Movie movie, string good, string dir)
    {
        byte[] rom = File.ReadAllBytes(good);
        string fm2 = Path.Combine(dir, "play.fm2");
        var res = MovieExport.ToFm2(movie, rom, fm2);
        var lines = File.ReadAllLines(fm2);
        var input = lines.Where(l => l.StartsWith('|')).ToList();
        Check(".fm2: one input line per frame in FCEUX's layout (|cmd|pad 1|pad 2|port 2|)", input.Count == 600 && input[0] == "|0|........|........||" && input[60] == "|0|....T...|........||" && input[200] == "|0|.......A|........||",
              $"{input.Count} lines; frame 60 {input[60]}, frame 200 {input[200]}");
        var nes = new NesEmulator.NES(); nes.LoadROM(rom);
        Check(".fm2: the ROM's checksum as FCEUX writes it (base64 of the MD5 of PRG + CHR)", lines.Contains("romChecksum base64:" + Convert.ToBase64String(nes.ComputeRomMd5())), lines.First(l => l.StartsWith("romChecksum")));
        Check(".fm2: the export says what timing it cannot carry", res.Notes.Any(n => n.Contains("60.0988")), string.Join(" | ", res.Notes));
        Movie back;
        using (var m = MovieMachine.Open(good, "nes", "", "fceux", false, false)) back = MovieExport.FromFm2(fm2, m, 60, "");
        Check(".fm2 round trip: imported back, the same input and the same keyframe hashes", back.ToScript(1) == movie.ToScript(1) && back.Keys.Select(k => (k.Frame, k.Picture, k.Ram)).SequenceEqual(movie.Keys.Select(k => (k.Frame, k.Picture, k.Ram))),
              $"{back.ToScript(1)}; {back.Keys.Count} keyframes");
        var sneaky = File.ReadAllText(fm2).Replace("|0|....T...|........||\n|0|....T...|", "|0|........|........||\n|0|....T...|");
        File.WriteAllText(Path.Combine(dir, "late.fm2"), sneaky);
        Movie late;
        using (var m = MovieMachine.Open(good, "nes", "", "fceux", false, false)) late = MovieExport.FromFm2(Path.Combine(dir, "late.fm2"), m, 60, "");
        Check("NEGATIVE: an .fm2 with Start one frame later imports to other keyframes", late.ToScript(1) != movie.ToScript(1) && late.Keys[1].Ram != movie.Keys[1].Ram, late.ToScript(1));

        string bk2 = Path.Combine(dir, "play.bk2");
        MovieExport.ToBk2(movie, rom, bk2);
        using (var z = ZipFile.OpenRead(bk2))
        {
            string Read(string n) { using var s = new StreamReader(z.GetEntry(n)!.Open()); return s.ReadToEnd(); }
            var header = Read("Header.txt"); var log = Read("Input Log.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string sha = MovieExport.BizHawkSha1("nes", rom);
            Check(".bk2: BizHawk's header (platform, core, the SHA-1 of PRG + CHR)", header.Contains("Platform NES\n") && header.Contains("Core NesHawk\n") && header.Contains("SHA1 " + sha + "\n") && sha == Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(rom[16..])), $"SHA1 {sha}");
            var frames = log.Where(l => l.StartsWith('|')).ToList();
            Check(".bk2: BizHawk's NES log key and one line per frame", log[0] == "[Input]" && log[1] == "LogKey:#Reset|Power|#P1 Up|P1 Down|P1 Left|P1 Right|P1 Start|P1 Select|P1 B|P1 A|#P2 Up|P2 Down|P2 Left|P2 Right|P2 Start|P2 Select|P2 B|P2 A|"
                  && frames.Count == 600 && frames[60] == "|..|....S...|........|" && frames[200] == "|..|.......A|........|" && log[^1] == "[/Input]", $"{frames.Count} frames; frame 60 {frames[60]}");
        }
        var snesMovie = new Movie { Console = "snes", RomName = "x.sfc", RomSha256 = "00", Frames = 2 }; snesMovie.SetPads(0, PadButtons.Y | PadButtons.R, PadButtons.None);
        string sbk2 = Path.Combine(dir, "snes.bk2");
        MovieExport.ToBk2(snesMovie, new byte[] { 1, 2, 3 }, sbk2);
        using (var z = ZipFile.OpenRead(sbk2)) { using var s = new StreamReader(z.GetEntry("Input Log.txt")!.Open()); var l = s.ReadToEnd().Split('\n'); Check(".bk2: the SNES pad (Y and R in BizHawk's order)", l[2] == "|..|......Y....r|............|", l[2]); }
        Check("NEGATIVE: an .fm2 of a SNES movie is refused", Throws(() => MovieExport.ToFm2(snesMovie, new byte[16], Path.Combine(dir, "x.fm2")), out string w1) && w1.Contains("NES"), w1);
        var stm = Movie.Parse(movie.ToText()); stm.Start = MovieStart.State; stm.StartState = new byte[] { 1 };
        Check("NEGATIVE: a movie that starts from a BrokenNes state is refused (the other emulator cannot load it)", Throws(() => MovieExport.ToBk2(stm, rom, Path.Combine(dir, "y.bk2")), out string w2) && w2.Contains("save state"), w2);
        var gbm = new Movie { Console = "gb", RomName = "x.gb", RomSha256 = "00", Frames = 2 }; gbm.SetPads(0, PadButtons.None, PadButtons.A);
        Check("NEGATIVE: a Game Boy movie that presses pad 2 is refused", Throws(() => MovieExport.ToBk2(gbm, new byte[4], Path.Combine(dir, "z.bk2")), out string w3) && w3.Contains("pad 2"), w3);
    }

    // ------------------------------------------------------------------------------------------------ diverge

    private static void Diverge(Movie movie, string good, string bad, List<string> sym, string dir)
    {
        var badSym = new List<string> { Path.ChangeExtension(bad, ".mlb") };
        var same = MovieDiverge.Run(movie, new DivergeSide(good, sym, null, null, "a"), new DivergeSide(good, sym, null, null, "b"), -1);
        Check("NEGATIVE: the same build on both sides does not diverge", same["diverged"]!.GetValue<bool>() == false, same["summary"]!.ToString());
        var d = MovieDiverge.Run(movie, new DivergeSide(good, sym, null, null, "good"), new DivergeSide(bad, badSym, null, null, "regression"), -1);
        var fi = d["firstInstruction"] as System.Text.Json.Nodes.JsonObject;
        string firstA = fi?["a"]?.AsArray().Last()?.ToString() ?? "";
        Check("two builds: the first differing frame is the one where the third life would be lost", d["diverged"]!.GetValue<bool>() && d["frame"]!.GetValue<long>() == 252, d["summary"]!.ToString());
        Check("two builds: the first differing instruction is the planted CMP #floor in lose_life", fi != null && fi["pcSymbolA"]?.ToString() == "lose_life+$2" && firstA.Contains("CMP #$01") && (fi["b"]?.AsArray().Last()?.ToString() ?? "").Contains("CMP #$00"),
              firstA + " | " + fi?["b"]?.AsArray().Last());
        var after = d["stateAfter"]!.AsObject();
        var vars = after["variables"]!.AsArray();
        Check("state diff by symbol: after the frame exactly one variable differs, lives, 1 against 0", after["variablesDiffering"]!.GetValue<int>() == 1 && vars[0]!["variable"]!.ToString() == "lives" && vars[0]!["valueA"]!.GetValue<long>() == 1 && vars[0]!["valueB"]!.GetValue<long>() == 0,
              string.Join(", ", vars.Select(v => $"{v!["variable"]} {v["valueA"]}/{v["valueB"]}")));
        Check("state diff by symbol: before the frame nothing differs", d["stateBefore"]!["variablesDiffering"]!.GetValue<int>() == 0, $"{d["stateBefore"]!["variablesDiffering"]}");
        var c = MovieDiverge.Run(movie, new DivergeSide(good, sym, null, null, "60"), new DivergeSide(good, sym, null, true, "ntsc"), -1);
        Check("two frame timings: the divergence is found in the first frame (one side runs more instructions)", c["diverged"]!.GetValue<bool>() && c["frame"]!.GetValue<long>() == 0 && (c["firstInstruction"]?["why"]?.ToString() ?? "").Contains("more instructions"), c["summary"]!.ToString());

        // against another emulator's trace: compat runs written here from a build that boots 2 frames later (shifted) and from the regression
        string Compat(string name, string rom, int delay)
        {
            string cdir = Path.Combine(dir, "compat-" + name); Directory.CreateDirectory(cdir);
            File.WriteAllText(Path.Combine(cdir, "spec.txt"), "# bdcompat-spec 1\nconsole nes\nread app_state ram 11\nread lives ram 12\nread score ram 13\n");
            var sb = new StringBuilder("# bdcompat-result 1\nemu test emulator " + name + "\nreads app_state lives score\n");
            using var m = MovieMachine.OpenFor(movie, rom, false);
            var cur = new MoviePlayer.Cursor(movie);
            for (int f = 0; f < 600 + delay; f++)
            {
                cur.At(Math.Max(0, f - delay)); if (f < delay) { cur.P1 = PadButtons.None; }
                m.Frame(f < delay ? PadButtons.None : cur.P1, PadButtons.None);
                var ram = m.Host.Domains().First(x => x.Id == "RAM");
                sb.Append($"t {f + 1} {ram.Peek(0x11)} {ram.Peek(0x12)} {ram.Peek(0x13)}\n");
            }
            sb.Append($"end done {600 + delay}\n");
            File.WriteAllText(Path.Combine(cdir, "result.txt"), sb.ToString());
            return cdir;
        }
        var a1 = MovieDiverge.Against(movie, new DivergeSide(good, sym, null, null, "a"), Compat("same", good, 0), 30);
        Check("NEGATIVE: against a trace of the same game and input, no divergence", a1["diverged"]!.GetValue<bool>() == false && a1["shift"]!.GetValue<int>() == 0, a1["summary"]!.ToString());
        var a2 = MovieDiverge.Against(movie, new DivergeSide(good, sym, null, null, "a"), Compat("shifted", good, 2), 30);
        Check("against a trace whose input lands 2 frames later: the shift is found and nothing else differs", a2["diverged"]!.GetValue<bool>() == false && a2["shift"]!.GetValue<int>() == 2, a2["summary"]!.ToString());
        var a3 = MovieDiverge.Against(movie, new DivergeSide(good, sym, null, null, "a"), Compat("regression", bad, 0), 30);
        Check("against a trace that differs: the first frame and the variable (lives 1 against 0)", a3["diverged"]!.GetValue<bool>() && a3["framesRunWhenFound"]!.GetValue<long>() == 253 && a3["differing"]!.AsArray().Any(v => v!["variable"]!.ToString() == "lives"), a3["summary"]!.ToString());
    }

    // ------------------------------------------------------------------------------------------------ test verdicts

    private static void Verdicts(Movie movie, string good, string bad, string menu, string harmless, List<string> sym, string dir)
    {
        (int, string) T(string rom, string test, string? s = null)
        {
            var (code, j) = MovieCli.Test(movie, rom, new List<string> { s ?? Path.ChangeExtension(rom, ".mlb") }, test, new List<string>());
            return (code, $"{j["verdict"]}: {j["why"]}");
        }
        var g = T(good, "[lives] >= 1 at 600"); Check("good: a test that holds is good (exit 0)", g.Item1 == 0, g.Item2);
        var b = T(bad, "[lives] >= 1 at 600"); Check("NEGATIVE: the regression is bad (exit 1)", b.Item1 == 1, b.Item2);
        var m = T(menu, "[lives] >= 1 at 600"); Check("another menu: the replay loses its course before the test, so it can't tell (exit 4), never a guess", m.Item1 == 4, m.Item2);
        var h = T(harmless, "[lives] >= 1 at 600"); Check("a harmless rebuild is good (its RAM differs, its course does not)", h.Item1 == 0, h.Item2);
        var p = T(good, "picture at 600"); Check("picture at a keyframe: the recording's picture is good", p.Item1 == 0, p.Item2);
        var nc = T(bad, "no-crash"); Check("no-crash: the regression does not crash", nc.Item1 == 0, nc.Item2);
        var nl = T(good, "no-lag from 70"); Check("no-lag: the planted game never lags (it waits for its NMI every frame)", nl.Item1 == 0, nl.Item2);
        Check("NEGATIVE: a test frame outside the movie is refused", Throws(() => T(good, "[lives] at 9999"), out string w) && w.Contains("outside"), w);
        Check("NEGATIVE: a picture test where the movie has no keyframe is refused", Throws(() => T(good, "picture at 599"), out string w2) && w2.Contains("keyframe"), w2);
    }

    // ------------------------------------------------------------------------------------------------ step back

    private static void Back(string good, List<string> sym, string dir)
    {
        string Regs(DebugHost h) { var c = h.Machine.Main; return string.Join(" ", c.RegisterNames.Distinct().Select(r => c.TryGetRegister(r, out long v) ? $"{r}={v:X}" : "")) + $" cyc={h.Machine.Cycle} f={h.Machine.Frame}"; }
        byte[] rom = File.ReadAllBytes(good);
        using (var h = DebugHost.Open(rom, "good.nes"))
        {
            foreach (var s in sym) h.Dbg.Symbols.Load(s, h.Machine.Console);
            h.Dbg.TrackCallStack = true;
            var hist = h.EnableHistory(60, 30);
            h.SetPad(new[] { "Start" }); h.Runner.Run(70); h.SetPad(Array.Empty<string>());
            var bp = h.Dbg.Add("lose_life", AccessKind.Exec);
            var o = h.Runner.Run(600);
            string atBreak = Regs(h); ulong ramAtBreak = h.MovieRam();
            for (int i = 0; i < 5; i++) h.Runner.Step(() => h.Dbg.StepInto());
            string after5 = Regs(h);
            var r = hist.BackInstructions(h.Runner, 5, h.LoadState, h.EnsureBetweenFrames);
            Check("NES: back 5 instructions after 5 steps: the registers, cycle, frame and RAM of the breakpoint again", o == RunOutcome.Break && Regs(h) == atBreak && h.MovieRam() == ramAtBreak && atBreak != after5 && r.Note.Length == 0,
                  $"{Regs(h)} | at the break {atBreak}");
            for (int i = 0; i < 5; i++) h.Runner.Step(() => h.Dbg.StepInto());
            Check("NES: and forward again: the same 5 instructions land in the same place", Regs(h) == after5, Regs(h));
            h.Dbg.Remove(bp.Id);
            h.Runner.Run(1);   // the rest of the frame
            long f0 = hist.FrameIndex; ulong ram0 = h.MovieRam(), pic0 = h.MoviePicture();
            h.Runner.Run(3);
            ulong ram3 = h.MovieRam();
            var rb = hist.BackFrames(h.Runner, 3, false, h.LoadState, h.EnsureBetweenFrames);
            Check("NES: back 3 frames: the RAM and the picture of 3 frames ago", hist.FrameIndex == f0 && h.MovieRam() == ram0 && h.MoviePicture() == pic0 && ram3 != ram0, $"frame {hist.FrameIndex} (was {f0}), state of frame {rb.SnapshotFrame}");
            h.Runner.Run(3);
            Check("NES: and 3 frames on: the same future (the logged input replays)", h.MovieRam() == ram3, $"{h.MovieRam():X16} vs {ram3:X16}");
            hist.BackFrames(h.Runner, 3, false, h.LoadState, h.EnsureBetweenFrames);
            h.SetPad(new[] { "A" }); h.Runner.Run(3); h.SetPad(Array.Empty<string>());
            Check("NEGATIVE: a new input after stepping back rewrites the future (holding A scores)", h.MovieRam() != ram3, $"{h.MovieRam():X16} vs {ram3:X16}");
            Check("NEGATIVE: stepping back past the history's oldest state is refused", Throws(() => hist.BackFrames(h.Runner, 100000, false, h.LoadState, h.EnsureBetweenFrames), out string w) && w.Contains("only"), w);
        }
        using (var h = DebugHost.Open(rom, "good.nes"))
        {
            var hist = h.EnableHistory(10, 2);
            h.Runner.Run(100);
            Check("NEGATIVE: by instructions without the instruction count (no call stack tracking) is refused", Throws(() => hist.BackInstructions(h.Runner, 1, h.LoadState, h.EnsureBetweenFrames), out string w1) && w1.Contains("instruction count"), w1);
            Check("NEGATIVE: further back than the states kept (2 every 10 frames) is refused", Throws(() => hist.BackFrames(h.Runner, 50, false, h.LoadState, h.EnsureBetweenFrames), out string w2) && w2.Contains("does not reach back"), w2);
        }
        // the SNES (65816) and the Game Boy (SM83)
        foreach (var (name, rom2, file, sub, labels) in new[] { ("SNES", DebugTestRoms.Snes().rom, "back.sfc", "ssub", DebugTestRoms.Snes().asm), ("Game Boy", DebugTestRoms.GameBoy().rom, "back.gb", "sub2", DebugTestRoms.GameBoy().asm) })
        {
            using var h = DebugHost.Open(rom2, file);
            h.Dbg.TrackCallStack = true;
            var hist = h.EnableHistory(20, 10);
            h.Runner.Run(45);
            var bp = h.Dbg.Add("$" + labels[sub].ToString("X"), AccessKind.Exec);
            var o = h.Runner.Run(30);
            string atBreak = Regs(h);
            h.Dbg.Remove(bp.Id);
            for (int i = 0; i < 4; i++) h.Runner.Step(() => h.Dbg.StepInto());
            string after = Regs(h);
            var r = hist.BackInstructions(h.Runner, 4, h.LoadState, h.EnsureBetweenFrames);
            Check($"{name}: back 4 instructions: the registers, clock and frame of the breakpoint again", o == RunOutcome.Break && Regs(h) == atBreak && after != atBreak && r.Note.Length == 0, $"{Regs(h)} | at the break {atBreak}");
        }
    }

    // ------------------------------------------------------------------------------------------------ the user's games

    private static void Games(string dir)
    {
        string home = Environment.GetEnvironmentVariable("BROKENDEV_HOME") is { Length: > 0 } e ? e : @"P:\BrokenDev\env";
        foreach (var (name, rom, sym, script, frames) in new[]
        {
            ("VRUN", Path.Combine(home, "out", "vrun-nes", "vrun_game.nes"), Path.Combine(home, "out", "vrun-nes", "vrun_game.mlb"), "120:Start,132:,400:Right,600:Right+B,700:Right", 900),
            ("Super VRUN", Path.Combine(home, "out", "supervrun", "supervrun.sfc"), Path.Combine(home, "out", "supervrun", "supervrun.sym"), "200:Start,206:,400:Start,406:,600:Right,700:", 900),
        })
        {
            if (!File.Exists(rom)) { NotRun($"{name}: record and replay", $"{rom} is not built here"); continue; }
            var syms = File.Exists(sym) ? new List<string> { sym } : new List<string>();
            Movie mv;
            var sw = Stopwatch.StartNew();
            using (var m = MovieMachine.Open(rom, null, "", "fceux", false, true, syms)) mv = MoviePlayer.Record(m, script, "", frames, 60, Array.Empty<(string, string)>(), "");
            double rec = sw.Elapsed.TotalSeconds;
            mv = Movie.Parse(mv.ToText());
            PlayReport r;
            using (var m = MovieMachine.OpenFor(mv, rom, true, syms)) r = MoviePlayer.Play(mv, m);
            Check($"{name}: {frames} frames of scripted play recorded and replayed in sync", r.InSync && r.KeysMatched == mv.Keys.Count, $"{r.KeysMatched} of {r.KeysChecked} keyframes, recorded at {frames / rec:F0} fps");
            var shifted = Movie.Parse(mv.ToText().Replace(" Start -\n", " - -\n"));
            using (var m = MovieMachine.OpenFor(shifted, rom, true, syms)) r = MoviePlayer.Play(shifted, m);
            Check($"NEGATIVE: {name}: the same movie without its Start presses goes off course", !r.InSync, $"off course after {r.OffCourseFrame}");
            if (name == "VRUN")
            {
                using (var m = MovieMachine.OpenFor(mv, rom, true, syms, "FIX/FMC/FIX")) r = MoviePlayer.Play(mv, m);
                Check("NEGATIVE: VRUN: the movie on another picture chip (PPU FMC) goes off course", !r.InSync, $"off course after {r.OffCourseFrame}: {r.OffCourseWhat}");
            }
        }
    }
}
