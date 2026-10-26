using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BrokenNes.Workshop.Bugs;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Capture;
using NesEmulator.Debugging.Checks;
using NesEmulator.Debugging.Movies;
using NesEmulator.Debugging.Port;
using NesEmulator.Systems;
using static BrokenNes.Workshop.Debug.PortTestCli;

namespace BrokenNes.Workshop.Debug;

/// <summary>
/// The debug port's events in the other tools (D7): the hardware checks (an assert is a <c>port.assert</c> finding no manifest can accept),
/// the profile (the named timers), a movie (markers as <c>m</c> lines, checked on replay), the divergence finder (the last marker before the
/// difference), a bug capture (markers in its movie, every event in bug.json) and the debugger (an assert parks it with the message). Each
/// with a negative control.
/// </summary>
internal static class PortToolsTest
{
    public static void Run(string dir)
    {
        Checks(dir);
        Movies(dir);
        Bugs(dir);
        Debugger(dir);
    }

    private static void Checks(string dir)
    {
        foreach (var console in new[] { "nes", "snes", "gb" })
        {
            string C = console.ToUpperInvariant();
            var (h, _) = Open(dir, console, attach: false);
            using (h)
            {
                var checks = CheckRun.Attach(h.Dbg, new CheckOptions());
                h.RunPlain(20);
                checks.Finish();
                var asserts = checks.Findings.Where(f => f.Rule == AcceptedFinding.PortAssertRule).ToList();
                Check($"{C} checks: the assert is a port.assert finding at its function, once", asserts.Count == 1 && asserts[0].Place == "do_assert" && asserts[0].Count == 1 && asserts[0].Detail.Contains("frame counter < 3"),
                      string.Join(" | ", asserts.Select(f => f.ToString())));
                var acceptAll = new AcceptedFinding { Rule = "*", At = "*", Reason = "everything" };
                var acceptPort = new AcceptedFinding { Rule = "port.*", At = "*", Reason = "asserts" };
                Check($"{C} checks: no accepted entry matches a port.assert (not even '*')", asserts.Count == 1 && !acceptAll.Matches(asserts[0]) && !acceptPort.Matches(asserts[0]), "rule '*' and 'port.*' both refuse it");
                var p = CheckRun.Summarize(checks);
                var work = p.Timers.FirstOrDefault(t => t.Name == PortTestRoms.WorkTimer);
                Check($"{C} profile: the named timers are in the profile", work != null && work.Calls >= 18 && (console != "nes" || work.Worst == PortTestRoms.NesWorkCycles)
                      && CheckRun.ProfileText(p).Any(l => l.Contains("timer " + PortTestRoms.WorkTimer)) && CheckRun.ProfileJson(p)["timers"]!.AsArray().Count == (console == "gb" ? 1 : 2),
                      work == null ? "no timer" : $"{work.Calls} calls, worst {work.Worst} {p.Unit}; {p.PortCounts}");
                Check($"{C} checks: the port is detached when the run finishes", checks.Port is { Attached: false }, "");
            }
            var (hn, _) = Open(dir, console, real: false, attach: false);
            using (hn)
            {
                var checks = CheckRun.Attach(hn.Dbg, new CheckOptions());
                hn.RunPlain(20);
                checks.Finish();
                var p = CheckRun.Summarize(checks);
                Check($"{C} checks: the negative control has no port.assert and no timers", !checks.Findings.Any(f => f.Rule.StartsWith("port.")) && p.Timers.Count == 0, $"{checks.Findings.Count} findings");
            }
            var (ho, _) = Open(dir, console, attach: false);
            using (ho)
            {
                var checks = CheckRun.Attach(ho.Dbg, new CheckOptions { DebugPort = false });
                ho.RunPlain(20);
                checks.Finish();
                Check($"{C} checks: with the port off (CheckOptions.DebugPort false) nothing is heard", !checks.Findings.Any(f => f.Rule.StartsWith("port.")) && checks.Port == null && CheckRun.Summarize(checks).Timers.Count == 0, "");
            }
        }
    }

    private static void Movies(string dir)
    {
        foreach (var console in new[] { "nes", "snes", "gb" })
        {
            string C = console.ToUpperInvariant();
            var (rom, name, _, sym, _) = Rom(dir, console);
            string romPath = DebugTestRoms.Write(dir, "mv_" + name, rom);
            Movie mv;
            using (var m = MovieMachine.Open(romPath, null, "", "fceux", false, true, new[] { sym }))
                mv = MoviePlayer.Record(m, "", "", 12, 4, Array.Empty<(string, string)>(), "test");
            string text = mv.ToText();
            var back = Movie.Parse(text);
            Check($"{C} movie: the port's markers are recorded as 'm' lines at their frames", back.Marks.Count == 2 && back.Marks[0].Text == PortTestRoms.StartMark && back.Marks[1].Text == PortTestRoms.FiveMark && text.Contains("\nm " + back.Marks[1].Frame + " " + PortTestRoms.FiveMark),
                  string.Join(" | ", back.Marks.Select(k => $"{k.Frame} {k.Text}")));
            using (var m = MovieMachine.OpenFor(back, romPath, true, new[] { sym }))
            {
                var r = MoviePlayer.Play(back, m);
                Check($"{C} movie: a replay gives the same markers at the same frames", r.InSync && r.MarksNote != null && r.MarksNote.Contains("same frames") && r.Marks.SequenceEqual(back.Marks), r.MarksNote ?? "no note");
            }
            // negative control: a recording whose second marker is a frame late is reported
            var moved = Movie.Parse(text);
            var k1 = moved.Marks[1]; moved.Marks[1] = new MovieMark(k1.Frame + 1, k1.Text);
            using (var m = MovieMachine.OpenFor(moved, romPath, true, new[] { sym }))
            {
                var r = MoviePlayer.Play(moved, m);
                Check($"{C} movie: the negative control (a marker a frame late in the file) is reported", r.MarksNote != null && r.MarksNote.StartsWith("marker 2 differs"), r.MarksNote ?? "no note");
            }
        }
        // the divergence finder names the last marker before the difference: the NES test ROM against a build whose fifth-frame marker is gone
        var (ra, _, _, syma, _) = Rom(dir, "nes");
        var (rb, _, _) = PortTestRoms.Nes(0x401C, noFiveMark: true);
        string pa = DebugTestRoms.Write(dir, "div_a.nes", ra), pb = DebugTestRoms.Write(dir, "div_b.nes", rb);
        Movie dm;
        using (var m = MovieMachine.Open(pa, null, "", "fceux", false, true)) dm = MoviePlayer.Record(m, "", "", 12, 4, Array.Empty<(string, string)>(), "a");
        var j = MovieDiverge.Run(dm, new DivergeSide(pa, new() { syma }, null, null, "a"), new DivergeSide(pb, new() { syma }, null, null, "b"), 12);
        Check("NES diverge: the report names the last marker each build gave, and the first marker they disagree on", (bool?)j["diverged"] == true && (long?)j["frame"] == 4
              && ((string?)j["lastMarker"]?["a"])?.Contains("'five frames' at frame 4") == true && ((string?)j["lastMarker"]?["b"])?.Contains("'start' at frame 0") == true
              && ((string?)j["markersDiffer"])?.StartsWith("marker 2") == true,
              $"frame {j["frame"]}; last marker a {j["lastMarker"]?["a"]}, b {j["lastMarker"]?["b"]}; {j["markersDiffer"]}");
    }

    private static void Bugs(string dir)
    {
        foreach (var console in new[] { "nes", "gb" })
        {
            string C = console.ToUpperInvariant();
            var (rom, name, _, _, _) = Rom(dir, console);
            string romPath = DebugTestRoms.Write(dir, "bug_" + name, rom);
            var d = BugCli.Make(romPath, null, "", 90, "", "", out var meta);
            var files = BugBundle.Files(d, meta);
            using var info = JsonDocument.Parse(files[BugBundle.InfoFile]);
            var port = info.RootElement.GetProperty("debugPort").EnumerateArray().ToList();
            string movie = Encoding.UTF8.GetString(files[BugBundle.MovieFile]);
            var mv = Movie.Parse(movie);
            Check($"{C} bug capture: bug.json has the port's events, the movie its markers", port.Any(e => e.GetProperty("kind").GetString() == "assert") && port.Count(e => e.GetProperty("kind").GetString() == "marker") == 2
                  && mv.Marks.Count == 2 && mv.Marks[1].Text == PortTestRoms.FiveMark,
                  $"{port.Count} events ({string.Join(", ", port.GroupBy(e => e.GetProperty("kind").GetString()).Select(g => $"{g.Count()} {g.Key}"))}); movie markers {string.Join(" | ", mv.Marks.Select(k => $"{k.Frame} {k.Text}"))}");
            var (rn, nn, _, _, _) = Rom(dir, console, real: false);
            string negPath = DebugTestRoms.Write(dir, "bugneg_" + nn, rn);
            var dn = BugCli.Make(negPath, null, "", 90, "", "", out var metaN);
            Check($"{C} bug capture: the negative control has no port events", dn.Notes.Count == 0, $"{dn.Notes.Count} notes");
        }
    }

    private static void Debugger(string dir)
    {
        foreach (var console in new[] { "nes", "snes", "gb" })
        {
            string C = console.ToUpperInvariant();
            var (h, port) = Open(dir, console);
            using (h)
            {
                port.BreakOnAssert = true;
                var o = h.Runner.Run(10);
                var b = h.Dbg.LastBreak;
                Check($"{C} debugger: an assert parks the debugger with the message and the place", o == RunOutcome.Break && b?.Reason == "assert" && b.Text.Contains("frame counter < 3") && b.Text.Contains("do_assert"), $"{o}: {b?.Text}");
                var o2 = h.Runner.Run(5);
                Check($"{C} debugger: it runs on after the assert", o2 == RunOutcome.Frames, o2.ToString());
                port.Detach();
            }
            var (hn, pn) = Open(dir, console);
            using (hn)
            {
                var o = hn.Runner.Run(10);
                Check($"{C} debugger: with BreakOnAssert off the assert is only recorded (the negative control)", o == RunOutcome.Frames && pn.Asserts.Count == 1, $"{o}, {pn.Asserts.Count} assert(s)");
                pn.Detach();
            }
        }
    }

    /// <summary>The real games: the Game Boy hello world's dev build speaks (BrokenDev's build of vrun-voc); games that do not use the port
    /// (VRUN, Super VRUN, the WideMess sample's release build) say nothing: no false events. A game not on this machine is NOT RUN.</summary>
    public static void Games(string dir)
    {
        void Run(string name, string path, string? sym, ConsoleKind? kind, int frames, string input, Action<DebugPort> check)
        {
            if (!File.Exists(path)) { NotRun(name, $"{path} is not on this machine"); return; }
            if (kind == ConsoleKind.WideMess) NesEmulator.Systems.Consoles.WideMessEnabled = true;
            byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(path), Path.GetFileName(path), out string inner);
            using var g = CheckedGame.Open(rom, inner, kind);
            if (sym != null && File.Exists(sym)) g.Dbg.Symbols.Load(sym, g.Family);
            using var port = new DebugPort(g.Host.Machine, g.Dbg);
            port.Attach();
            g.Run(frames, CheckedGame.ParseInput(input));
            check(port);
        }
        Run("the Game Boy hello world's dev build", @"P:\BrokenDev\env\out\vrun-voc\vrun_voc.gbc", @"P:\BrokenDev\env\out\vrun-voc\vrun_voc.noi", null, 400, "120:Right,180:,200:A,206:", p =>
        {
            var prints = p.Events.Where(e => e.Kind == PortEventKind.Print).Select(e => e.Text).ToList();
            var t = p.Timer("move_player");
            Check("VOC dev build: its prints, markers and the move_player timer, no assert, no error", prints.Contains("boot 1") && p.Markers.Select(m => m.Text).SequenceEqual(new[] { "started", "beep" })
                  && t is { Calls: > 300 } && p.Asserts.Count == 0 && p.Count(PortEventKind.Error) == 0 && p.Events.First(e => e.Text == "boot 1").PcSymbol?.StartsWith("_count_boot") == true,
                  $"{string.Join(" | ", p.Events.Select(e => e.Text))}; move_player {t?.Calls} calls, worst {t?.Worst}");
        });
        Run("VRUN (does not use the port)", @"C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_Nes_Dev\builds\2026-10-23_poweron\vrun_game.nes", null, null, 900, "120:Start,132:,400:Right", p =>
            Check("VRUN: a game that does not use the port hears nothing (no false events)", p.EventCount == 0 && p.Writes == 0, $"{p.EventCount} events, {p.Writes} writes"));
        Run("Super VRUN (does not use the port)", @"P:\BrokenDev\env\out\supervrun\supervrun.sfc", null, null, 900, "120:Start,132:", p =>
            Check("Super VRUN: a game that does not use the port hears nothing (no false events)", p.EventCount == 0 && p.Writes == 0, $"{p.EventCount} events, {p.Writes} writes"));
        string sample = FindUp("WideMess\\testroms\\bin\\devkit_sample.wms");
        Run("the WideMess sample's release build", sample, null, ConsoleKind.WideMess, 600, "30:Right+B", p =>
            Check("WideMess sample, release build (the committed devkit_sample.wms): its compiled-away calls send nothing", p.EventCount == 0 && p.Writes == 0, $"{p.EventCount} events, {p.Writes} writes"));
    }

    private static string FindUp(string rel)
    {
        for (var d = new DirectoryInfo(Environment.CurrentDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, rel);
            if (File.Exists(p)) return p;
        }
        return rel;
    }
}
