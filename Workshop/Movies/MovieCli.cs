using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Checks;
using NesEmulator.Debugging.Movies;

namespace BrokenNes.Workshop.Movies;

/// <summary>
/// <c>--movie</c>: BrokenNes's own movies (milestone D5 of docs/projects/brokendev-debugging-plan.md; the format: <see cref="Movie"/>, its
/// working name and extension: <see cref="MovieFormat"/>).
/// <code>
///   --movie record  --rom R --frames N [--input "120:Start,132:"] [--input2 S] [--console c] [--cores C/P/A] [--power-on fceux|zeros|ones] [--ntsc]
///                   [--key-every 60] [--sym F]... [--var name=expr]... [--build TEXT] [--start-state FILE] --out M.bnm
///   --movie import  --fm2 F --rom R [--key-every 60] [--build TEXT] --out M.bnm          (an FCEUX movie, replayed here to fill the keyframes)
///   --movie play    --movie M [--rom R] [--cores C/P/A] [--sym F]... [--frames N] [--json]   0 every keyframe matched | 1 off course or crashed
///   --movie info    --movie M
///   --movie export  --movie M --rom R (--fm2 OUT | --bk2 OUT)
///   --movie diverge --movie M --rom R [--sym F]... [--rom-b R2] [--sym-b F]... [--cores C/P/A] [--cores-b C/P/A] [--ntsc-b on|off] [--frames N] [--json]
///   --movie diverge --movie M --rom R --against COMPATDIR [--max-shift 60]    (another emulator's BrokenDev compat run: spec.txt + result.txt)
///   --movie test    --movie M --rom R [--sym F]... --test "TEST" [--frame-wait sym]... [--json]
///                   TEST: no-crash | no-lag [from N] | picture at N (the recording's keyframe) | EXPR at N (a debugger expression, true = good)
///                   exit 0 good | 1 bad | 4 can't tell (the replay lost its course before the test frame) | 3 cannot run here | 2 usage
/// </code>
/// </summary>
internal static class MovieCli
{
    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        // options start after "--movie <verb>" (so "--movie M" is the movie file, not the command)
        string Opt(string n, string d = "") { int i = Array.FindIndex(args, 2, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        List<string> All(string n) { var l = new List<string>(); for (int i = 2; i < args.Length - 1; i++) if (args[i].Equals("--" + n, StringComparison.OrdinalIgnoreCase)) l.Add(args[i + 1]); return l; }
        bool Has(string n) => args.Skip(2).Any(a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase));
        if (args.Length < 2) { Console.Error.WriteLine("usage: --movie record|import|play|info|export|diverge|test ... (see Workshop/Movies/MovieCli.cs)"); return 2; }
        string verb = args[1].ToLowerInvariant();
        try
        {
            switch (verb)
            {
                case "record":
                {
                    string rom = Opt("rom"), outFile = Opt("out");
                    if (rom.Length == 0 || outFile.Length == 0 || !long.TryParse(Opt("frames"), out long frames) || frames <= 0) { Console.Error.WriteLine("record needs --rom, --frames N and --out"); return 2; }
                    var vars = All("var").Select(v => { int e = v.IndexOf('='); return e > 0 ? (v[..e].Trim(), v[(e + 1)..].Trim()) : throw new ArgumentException($"--var '{v}': name=expression"); }).ToList();
                    using var m = MovieMachine.Open(rom, Opt("console") is { Length: > 0 } c ? c : null, Opt("cores"), Opt("power-on", "fceux"), Has("ntsc"), true, All("sym"));
                    byte[]? st = Opt("start-state") is { Length: > 0 } sp ? File.ReadAllBytes(sp) : null;
                    var mv = MoviePlayer.Record(m, Opt("input"), Opt("input2"), frames, int.Parse(Opt("key-every", "60"), CultureInfo.InvariantCulture), vars, Opt("build"), st);
                    mv.Save(outFile);
                    Console.WriteLine($"recorded {outFile}: {mv.Frames} frames, {mv.Inputs.Count} input changes, {mv.Keys.Count} keyframes, {m.ConsoleKey} {m.Cores}{(mv.Vars.Count > 0 ? ", course: " + string.Join(", ", mv.Vars.Select(v => v.Name)) : "")}");
                    return 0;
                }
                case "import":
                {
                    string fm2 = Opt("fm2"), rom = Opt("rom"), outFile = Opt("out");
                    if (fm2.Length == 0 || rom.Length == 0 || outFile.Length == 0) { Console.Error.WriteLine("import needs --fm2, --rom and --out"); return 2; }
                    using var m = MovieMachine.Open(rom, "nes", Opt("cores"), Opt("power-on", "fceux"), Has("ntsc"), false);
                    var mv = MovieExport.FromFm2(fm2, m, int.Parse(Opt("key-every", "60"), CultureInfo.InvariantCulture), Opt("build"));
                    mv.Save(outFile);
                    Console.WriteLine($"imported {fm2} -> {outFile}: {mv.Frames} frames, {mv.Inputs.Count} input changes");
                    return 0;
                }
                case "info":
                {
                    var mv = Movie.Load(Need(Opt("movie"), "--movie"));
                    Console.WriteLine(InfoJson(mv).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
                case "play":
                {
                    var mv = Movie.Load(Need(Opt("movie"), "--movie"));
                    string rom = Opt("rom", "");
                    if (rom.Length == 0) { Console.Error.WriteLine("play needs --rom (the movie names its ROM by sha256, not by place)"); return 2; }
                    using var m = MovieMachine.OpenFor(mv, rom, true, All("sym"), Opt("cores") is { Length: > 0 } co ? co : null);
                    long frames = long.TryParse(Opt("frames"), out long fr) ? fr : -1;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = MoviePlayer.Play(mv, m, frames);
                    var j = ReportJson(mv, m, r); j["fps"] = Math.Round(r.FramesRun / Math.Max(1e-9, sw.Elapsed.TotalSeconds));
                    if (Has("json")) Console.WriteLine(j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    else Console.WriteLine(j["summary"]!.GetValue<string>());
                    return r.InSync ? 0 : 1;
                }
                case "export":
                {
                    var mv = Movie.Load(Need(Opt("movie"), "--movie"));
                    byte[] rom = File.ReadAllBytes(Need(Opt("rom"), "--rom"));
                    string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rom)).ToLowerInvariant();
                    if (!sha.Equals(mv.RomSha256, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine($"the ROM is not the movie's (sha256 {sha}, the movie's {mv.RomSha256}): an export names the ROM it plays on"); return 2; }
                    MovieExport.Result res;
                    if (Opt("fm2") is { Length: > 0 } f) res = MovieExport.ToFm2(mv, rom, f);
                    else if (Opt("bk2") is { Length: > 0 } b) res = MovieExport.ToBk2(mv, rom, b);
                    else { Console.Error.WriteLine("export needs --fm2 OUT or --bk2 OUT"); return 2; }
                    Console.WriteLine($"wrote {res.Path}: {res.Frames} frames");
                    foreach (var n in res.Notes) Console.WriteLine("note: " + n);
                    return 0;
                }
                case "diverge":
                {
                    var mv = Movie.Load(Need(Opt("movie"), "--movie"));
                    string rom = Need(Opt("rom"), "--rom");
                    var a = new DivergeSide(rom, All("sym"), Opt("cores") is { Length: > 0 } ca ? ca : null, null, "a");
                    JsonObject j;
                    if (Opt("against") is { Length: > 0 } dir) j = MovieDiverge.Against(mv, a, dir, int.Parse(Opt("max-shift", "60"), CultureInfo.InvariantCulture));
                    else
                    {
                        bool? ntscB = Opt("ntsc-b") switch { "on" => true, "off" => false, "" => null, var x => throw new ArgumentException($"--ntsc-b {x}: on or off") };
                        var b = new DivergeSide(Opt("rom-b", rom), Has("sym-b") ? All("sym-b") : All("sym"), Opt("cores-b") is { Length: > 0 } cb ? cb : a.Cores, ntscB, "b");
                        j = MovieDiverge.Run(mv, a, b, long.TryParse(Opt("frames"), out long fr) ? fr : -1);
                    }
                    if (Has("json")) Console.WriteLine(j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    else PrintDiverge(j);
                    return j["diverged"]!.GetValue<bool>() ? 1 : 0;
                }
                case "test":
                {
                    var mv = Movie.Load(Need(Opt("movie"), "--movie"));
                    string rom = Need(Opt("rom"), "--rom"), test = Need(Opt("test"), "--test");
                    JsonObject j; int code;
                    try { (code, j) = Test(mv, rom, All("sym"), test, All("frame-wait")); }
                    catch (Exception e) when (e is InvalidDataException or NotSupportedException or FileNotFoundException or ArgumentException && e.Message.Contains("did not apply"))
                    {
                        code = 3; j = new JsonObject { ["verdict"] = "cannot-run", ["why"] = e.Message };
                    }
                    if (Has("json")) Console.WriteLine(j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    else Console.WriteLine($"{j["verdict"]}: {j["why"]}");
                    return code;
                }
                case "build-planted":
                    // the movie tests' planted game, built from its settings file (the bisect proof's throwaway repository builds with this)
                    return MovieTestRoms.Build(Need(Opt("settings"), "--settings"), Need(Opt("out"), "--out"));
                default:
                    Console.Error.WriteLine($"unknown --movie verb '{verb}' (record, import, play, info, export, diverge, test, build-planted)");
                    return 2;
            }
        }
        catch (Exception e) when (e is FormatException or ArgumentException or FileNotFoundException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            Console.Error.WriteLine($"--movie {verb}: {e.Message}");
            return 2;
        }
    }

    private static string Need(string v, string what) => v.Length > 0 ? v : throw new ArgumentException($"{what} is needed");

    public static JsonObject InfoJson(Movie mv) => new()
    {
        ["format"] = MovieFormat.Name, ["extension"] = MovieFormat.Extension, ["console"] = mv.Console, ["rom"] = mv.RomName, ["romSha256"] = mv.RomSha256, ["romBytes"] = mv.RomBytes,
        ["build"] = mv.Build, ["emulator"] = mv.Emulator, ["cores"] = mv.Cores, ["timing"] = mv.NtscTiming ? "ntsc" : "60", ["powerOn"] = mv.PowerOn,
        ["start"] = mv.Start == MovieStart.PowerOn ? "power-on" : "state", ["frames"] = mv.Frames, ["keyEvery"] = mv.KeyEvery, ["keyframes"] = mv.Keys.Count,
        ["inputChanges"] = mv.Inputs.Count, ["pad2"] = mv.UsesPad2, ["course"] = new JsonArray(mv.Vars.Select(v => (JsonNode)$"{v.Name} = {v.Expr}").ToArray()),
        ["script1"] = mv.ToScript(1), ["script2"] = mv.UsesPad2 ? mv.ToScript(2) : null,
    };

    public static JsonObject ReportJson(Movie mv, MovieMachine m, PlayReport r)
    {
        var j = new JsonObject
        {
            ["rom"] = m.RomPath, ["sameRom"] = r.SameRom, ["cores"] = m.Cores, ["framesRun"] = r.FramesRun, ["crashed"] = r.Crashed, ["crashInfo"] = r.Crashed ? r.CrashInfo : null,
            ["keyframesChecked"] = r.KeysChecked, ["keyframesMatched"] = r.KeysMatched, ["inSync"] = r.InSync,
            ["offCourseAfter"] = r.OffCourseFrame, ["offCourse"] = r.OffCourseFrame != null ? r.OffCourseWhat : null,
            ["courseLostAfter"] = r.CourseLostFrame, ["courseLost"] = r.CourseLostFrame != null ? r.CourseLostWhat : null, ["courseNote"] = r.CourseNote,
        };
        string rom = r.SameRom ? "the recorded ROM" : $"ANOTHER ROM than the recording's ({m.RomSha256[..12]}, recorded {mv.RomSha256[..Math.Min(12, mv.RomSha256.Length)]})";
        j["summary"] = r.Crashed ? $"crashed after {r.FramesRun} frames on {rom}: {r.CrashInfo}"
            : r.OffCourseFrame != null ? $"OFF COURSE after {r.OffCourseFrame} frames on {rom}: {r.OffCourseWhat} ({r.KeysMatched} of {r.KeysChecked} keyframes matched)"
            : $"in sync on {rom}: {r.KeysMatched} of {r.KeysChecked} keyframes matched over {r.FramesRun} frames";
        return j;
    }

    private static void PrintDiverge(JsonObject j)
    {
        Console.WriteLine(j["summary"]!.GetValue<string>());
        if (j["firstInstruction"] is JsonObject fi)
        {
            Console.WriteLine($"  first differing instruction of the frame: #{fi["index"]}{(fi["why"] is { } w ? " (" + w + ")" : "")}");
            Console.WriteLine("  a:"); foreach (var l in fi["a"]!.AsArray()) Console.WriteLine("    " + l);
            Console.WriteLine("  b:"); foreach (var l in fi["b"]!.AsArray()) Console.WriteLine("    " + l);
        }
        if (j["firstTiming"] is JsonObject ft) Console.WriteLine($"  {ft["why"]} (instruction #{ft["index"]})");
        if (j["cpuSame"] is { } cs) Console.WriteLine("  " + cs);
        if (j["stateAfter"] is JsonObject sa)
        {
            Console.WriteLine($"  after the frame, by symbol: {sa["variablesDiffering"]} variable(s) differ");
            foreach (var v in sa["variables"]!.AsArray().Take(20)) Console.WriteLine($"    {v!["variable"]} ({v["at"]}): a {v["valueA"]?.ToString() ?? v["a"]!.ToString()}  b {v["valueB"]?.ToString() ?? v["b"]!.ToString()}");
            foreach (var s in sa["spaces"]!.AsArray()) Console.WriteLine($"    space {s!["space"]}: {s["bytesDiffering"]} byte(s), {s["withoutSymbol"]} without a symbol");
        }
        if (j["differing"] is JsonArray d) foreach (var v in d) Console.WriteLine($"    {v!["variable"]}: BrokenNes {v["brokennes"]}, other {v["other"]}");
        if (j["note"] is { } n) Console.WriteLine("  note: " + n);
    }

    // ------------------------------------------------------------------------------------------------ test (bisect's oracle)

    /// <summary>
    /// Replays a movie on a ROM (any build) and decides a test: 0 good, 1 bad, 4 can't tell (the movie's course variables stopped matching the
    /// recording before the test's frame: the replay went somewhere else, a different menu for example, so the test says nothing).
    /// </summary>
    public static (int code, JsonObject report) Test(Movie mv, string rom, List<string> symbols, string test, List<string> frameWait)
    {
        test = test.Trim();
        string kind; long at = mv.Frames, from = 0; string expr = "";
        var mAt = System.Text.RegularExpressions.Regex.Match(test, @"^(.*?)\s+at\s+(\d+)$");
        if (test == "no-crash") kind = "no-crash";
        else if (test.StartsWith("no-lag")) { kind = "no-lag"; var mf = System.Text.RegularExpressions.Regex.Match(test, @"^no-lag(\s+from\s+(\d+))?$"); if (!mf.Success) throw new ArgumentException($"test '{test}': no-lag [from N]"); if (mf.Groups[2].Success) from = long.Parse(mf.Groups[2].Value, CultureInfo.InvariantCulture); }
        else if (mAt.Success && mAt.Groups[1].Value == "picture") { kind = "picture"; at = long.Parse(mAt.Groups[2].Value, CultureInfo.InvariantCulture); }
        else if (mAt.Success) { kind = "expr"; expr = mAt.Groups[1].Value; at = long.Parse(mAt.Groups[2].Value, CultureInfo.InvariantCulture); }
        else throw new ArgumentException($"test '{test}': no-crash | no-lag [from N] | picture at N | <expression> at N");
        if (at < 1 || at > mv.Frames) throw new ArgumentException($"test frame {at} is outside the movie (1 to {mv.Frames})");
        if (kind == "picture" && mv.KeyAt(at) == null) throw new ArgumentException($"the movie has no keyframe after {at} frames to compare the picture with (keyframes every {mv.KeyEvery})");

        using var m = MovieMachine.OpenFor(mv, rom, true, symbols);
        var j = new JsonObject { ["test"] = test, ["rom"] = m.RomPath, ["romSha256"] = m.RomSha256, ["sameRom"] = m.RomSha256 == mv.RomSha256, ["course"] = mv.Vars.Count > 0 ? string.Join(", ", mv.Vars.Select(v => v.Name)) : "none recorded: the course is not checked (record the movie with --var for bisect's can't-tell)" };
        ConsoleChecks? checks = null;
        if (kind == "no-lag")
        {
            if (m.Host.Dbg == null) throw new NotSupportedException("the lag-frame test needs the hardware checks, which do not cover this console");
            var o = new CheckOptions(); foreach (var w in frameWait) o.FrameWait.Add(w);
            checks = CheckRun.Attach(m.Host.Dbg, o);
        }
        long? exprValue = null; bool? pictureSame = null;
        long stopAt = kind is "expr" or "picture" ? at : mv.Frames;
        var r = MoviePlayer.Play(mv, m, stopAt, done =>
        {
            if (done != at) return;
            if (kind == "expr") exprValue = m.Host.Dbg!.Evaluate(expr);
            if (kind == "picture") pictureSame = m.Picture() == mv.KeyAt(at)!.Picture;
        });
        if (checks != null) checks.Finish();
        j["framesRun"] = r.FramesRun;
        j["courseLostAfter"] = r.CourseLostFrame;
        if (r.CourseNote != null) j["courseNote"] = r.CourseNote;
        if (r.CourseLostFrame != null && r.CourseLostFrame < at && !(r.Crashed && kind == "no-crash"))
        {
            j["verdict"] = "cant-tell"; j["why"] = $"the replay went off course after {r.CourseLostFrame} frames ({r.CourseLostWhat}), before the test at {at}: this build plays the movie somewhere else";
            return (4, j);
        }
        if (r.Crashed) { j["verdict"] = "bad"; j["why"] = $"the game crashed after {r.FramesRun} frames: {r.CrashInfo}"; return (1, j); }
        switch (kind)
        {
            case "no-crash": j["verdict"] = "good"; j["why"] = $"ran all {r.FramesRun} frames"; return (0, j);
            case "no-lag":
            {
                var p = CheckRun.Summarize(checks!, from);
                j["lagFrames"] = p.LagFrames;
                bool good = p.LagFrames == 0;
                j["verdict"] = good ? "good" : "bad";
                j["why"] = good ? $"no lag frame from frame {from}" : $"{p.LagFrames} lag frame(s) from frame {from}: {string.Join(", ", p.LagFrameList.Take(10))}";
                return (good ? 0 : 1, j);
            }
            case "picture":
                j["verdict"] = pictureSame == true ? "good" : "bad";
                j["why"] = pictureSame == true ? $"the picture after {at} frames is the recording's" : $"the picture after {at} frames differs from the recording's";
                return (pictureSame == true ? 0 : 1, j);
            default:
                j["value"] = exprValue;
                j["verdict"] = exprValue != 0 ? "good" : "bad";
                j["why"] = $"{expr} = {exprValue} after {at} frames";
                return (exprValue != 0 ? 0 : 1, j);
        }
    }
}
