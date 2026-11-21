// TAS workline T1: --tas, the Workshop TAS runner (docs/projects/tas-format.md).
//   --tas run    RUN.tasrun.json | SCRIPT(.tas/.txt/.tlog) [--rom R] [--console c] [--start power-on|state:F] [--out D] [--once] [--bless] [--only ID]
//                [--renderer software|parallel] [--cores C/P/A] [--frames-dir D]    exit 0 = ran, deterministic (twice unless --once) | 1 | 2 usage
//   --tas check  (as run)   exit 0 only when also every chapter with an expect block matches it
//   --tas expand SCRIPT --console c [--fps 60] --out LOG.tlog                    the exact per-frame log
//   --tas import (--bnm M | --fm2 F) --out SCRIPT.tas [--state-out S.state]      a movie as a compact script
//   --tas import-autotas PKGDIR --out DIR                                        an AutoTAS package (seg/*.bnm) as a chaptered run
//   --tas export SCRIPT|LOG --console nes --fm2 OUT --rom R                      NES movie for FCEUX (power-on only)
//   --tas branch RUN --chapter ID --frame F --out DIR [--tail FILE]              state at that frame + a new run continuing from it
//   --tas render RUN|SCRIPT --mp4 OUT.mp4 [--mp4size WxH] [--mp4kbps N] [run options]   lag-free video (Media Foundation H.264/AAC)
// T2 (goals + precision):
//   --tas edit   RUN --chapter ID [--set F:BTNS[:N]]... [--insert F:BTNS:N]... [--delete F:N]... [--trim N | --trim-goal G[+K]] --out DIR
//                applies input edits in order (BTNS: A+B+Right, "-" none, pad 2 after |), plays the chapter with its goals and lag frames,
//                writes DIR/<id>.tlog + DIR/edited.tasrun.json (the run with that chapter replaced; --trim-goal N cuts it at goal N's first frame)
//                --variants FILE: one edit set per line (set ...; insert ...; delete ...), each tried from the base; the best (all goals met,
//                lowest first frame of goal --score N, default the last reach/within goal) is kept, the table goes to DIR/variants.json
//   --tas diff   A B [--rom R] [--romb R] [--ram-every N] [--max F] --out DIR    first diverging frame (picture / RAM), bytes, vars, input before it
//   --tas vars   RUN --chapter ID --frame F                                      the RAM map variables at a frame
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokenNes.Workshop.Tas.Script;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Tas;

internal static class TasCli
{
    public static int Run(string[] args)
    {
        string Opt(string n, string d = "") { int i = Array.FindIndex(args, 2, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        bool Has(string n) => args.Skip(2).Any(a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase));
        if (args.Length < 3) { Console.Error.WriteLine("usage: --tas run|check|expand|import|import-autotas|export|branch|render|edit|diff|vars ... (Workshop/Tas/TasCli.cs)"); return 2; }
        RomTestCli.EnsureConsole();
        string verb = args[1].ToLowerInvariant(), target = args[2];
        try
        {
            switch (verb)
            {
                case "run": case "check": case "render":
                {
                    var run = LoadRun(target, Opt("console"), Opt("rom"), Opt("start", "power-on"));
                    var o = Options(run, Opt, Has);
                    if (Has("only")) o.Only = Opt("only");
                    if (verb == "render")
                    {
                        if (Opt("mp4").Length == 0) throw new ArgumentException("--tas render needs --mp4 OUT.mp4");
                        var sz = Opt("mp4size", "0x0").Split('x');
                        o.Twice = false; o.Sink = new Mp4Sink(Path.GetFullPath(Opt("mp4")), int.Parse(sz[0]), int.Parse(sz[1]), int.Parse(Opt("mp4kbps", "6000")));
                    }
                    else if (Opt("frames-dir") is { Length: > 0 } fd) o.Sink = new FrameDirSink(fd);
                    var rep = TasRunner.Run(run, o);
                    if (Has("bless")) { if (run.Raw.Count == 0) throw new ArgumentException("--bless needs a run file"); TasRunner.Bless(run, rep); }
                    Summary(rep, o.OutDir);
                    if (o.Sink is Mp4Sink ms) Console.WriteLine($"TAS render: {ms.Frames} frames, {ms.Seconds:F1} s -> {Opt("mp4")}");
                    bool ok = rep["error"] == null && ((bool?)rep["determinism"]!["identical"] ?? true);
                    if (verb == "check") ok &= (bool)rep["expect"]!["ok"]! && (int)rep["expect"]!["checked"]! > 0;
                    return ok ? 0 : 1;
                }
                case "expand":
                {
                    string console = Opt("console").Length > 0 ? Opt("console") : throw new ArgumentException("--console");
                    var p = TasScript.ExpandFile(target, TasButtonMap.For(console), double.Parse(Opt("fps", "60"), System.Globalization.CultureInfo.InvariantCulture), TasTargets.ConsoleOps(console));
                    using (var w = new StreamWriter(Opt("out"))) TasScript.WriteLog(p, w);
                    Console.WriteLine($"TAS expand: {p.Frames} frames, {p.Steps.Count - p.Frames} ops -> {Opt("out")}");
                    return 0;
                }
                case "import":
                {
                    List<TasPad[]> frames = new(); var head = new List<string>(); int pads = 1; var resets = new HashSet<int>();
                    if (Has("bnm"))
                    {
                        var mv = Movie.Load(Opt("bnm"));
                        if (mv.Console != "nes") throw new NotSupportedException("bnm import: NES movies (other consoles: T7)");
                        pads = mv.UsesPad2 ? 2 : 1;
                        var c = new BrokenNes.Workshop.Movies.MoviePlayer.Cursor(mv);
                        for (long f = 0; f < mv.Frames; f++) { c.At(f); frames.Add(new[] { new TasPad(NesBits(c.P1), 0, 0), new TasPad(NesBits(c.P2), 0, 0) }); }
                        head.Add($"# imported from {Path.GetFileName(Opt("bnm"))}: {mv.RomName}, {mv.Frames} frames, start {mv.Start}");
                        if (mv.Start == MovieStart.State && Opt("state-out") is { Length: > 0 } so) { File.WriteAllBytes(so, mv.StartState!); head.Add($"# start state: {Path.GetFileName(so)}"); }
                    }
                    else
                    {
                        var (h, fr) = Fm2Movie.Load(Opt("fm2"));
                        pads = fr.Any(x => x.P2 != null && x.P2.Any(b => b)) ? 2 : 1;
                        for (int i = 0; i < fr.Count; i++) { frames.Add(new[] { new TasPad(Bits(fr[i].P1), 0, 0), new TasPad(fr[i].P2 != null ? Bits(fr[i].P2!) : 0, 0, 0) }); if (fr[i].Reset) resets.Add(i); }
                        head.Add($"# imported from {Path.GetFileName(Opt("fm2"))}: {h.RomFilename}, {fr.Count} frames");
                        if (resets.Count > 0) throw new NotSupportedException($"fm2 import: soft resets at frames {string.Join(",", resets.Take(5))} are not supported yet");
                    }
                    File.WriteAllLines(Opt("out"), head.Concat(TasScript.Compact(TasButtonMap.Nes, frames, pads)));
                    Console.WriteLine($"TAS import: {frames.Count} frames -> {Opt("out")}");
                    return 0;
                }
                case "import-autotas": return ImportAutoTas(target, Opt("out"));
                case "edit":
                {
                    var run = LoadRun(target, Opt("console"), Opt("rom"), Opt("start", "power-on"));
                    string outDir = Path.GetFullPath(Opt("out", "tas_edit"));
                    var opt = Options(run, Opt, Has);
                    using var s = new TasSession(run, Opt("chapter", run.Chapters[0].Id), Opt("rom") is { Length: > 0 } r ? r : null, opt.Open, Path.Combine(outDir, "scratch"));
                    var applied = new List<string>();
                    for (int i = 2; i + 1 < args.Length; i++)
                    {
                        var a = args[i].ToLowerInvariant(); var v = args[i + 1].Split(':');
                        if (a == "--set") { s.Set(int.Parse(v[0]), v.Length > 2 ? int.Parse(v[2]) : 1, s.Parse(v[1]), append: true); applied.Add($"set {args[i + 1]}"); }
                        else if (a == "--insert") { s.Insert(int.Parse(v[0]), v.Length > 2 ? int.Parse(v[2]) : 1, s.Parse(v[1])); applied.Add($"insert {args[i + 1]}"); }
                        else if (a == "--delete") { s.Delete(int.Parse(v[0]), v.Length > 1 ? int.Parse(v[1]) : 1); applied.Add($"delete {args[i + 1]}"); }
                    }
                    if (Opt("variants") is { Length: > 0 } vf)
                    {
                        // one edit set per line ("set F:BTNS:N; insert F:BTNS:N; delete F:N"), each from the edited base; best = all goals met, lowest
                        // first frame of goal --score (default: the last reach/within goal)
                        var baseSteps = s.Snapshot(); var table = new JsonArray(); JsonObject? best = null; string? bestLine = null; int bestScore = int.MaxValue;
                        var lines = File.ReadAllLines(vf).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).Prepend("").ToList();
                        foreach (var line in lines)
                        {
                            s.Restore(baseSteps);
                            foreach (var e in line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Apply(s, e);
                            var rr = s.Evaluate();
                            var gs = rr["goals"]!.AsArray();
                            int gi = Opt("score") is { Length: > 0 } sc ? int.Parse(sc) : Enumerable.Range(0, gs.Count).LastOrDefault(i => (string?)gs[i]!["kind"] is "reach" or "within");
                            bool ok = (bool?)rr["goal"] ?? true; int score = ok && gs.Count > 0 ? (int?)gs[gi]!["firstFrame"] ?? int.MaxValue : int.MaxValue;
                            table.Add(new JsonObject { ["edits"] = line.Length == 0 ? "(base)" : line, ["ok"] = ok, ["score"] = score == int.MaxValue ? null : score, ["firsts"] = new JsonArray(gs.Select(g => (JsonNode?)g!["firstFrame"]?.DeepClone()).ToArray()) });
                            Console.WriteLine($"  {(ok ? "ok " : "BAD")} score {(score == int.MaxValue ? "-" : score.ToString())}  {(line.Length == 0 ? "(base)" : line)}");
                            if (score < bestScore) { bestScore = score; best = rr; bestLine = line; }
                        }
                        s.Restore(baseSteps);
                        if (bestLine != null) foreach (var e in bestLine.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { Apply(s, e); applied.Add(e); }
                        File.WriteAllText(Path.Combine(outDir, "variants.json"), table.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        Console.WriteLine($"TAS edit: {lines.Count} variants, best score {bestScore} ({(string.IsNullOrEmpty(bestLine) ? "(base)" : bestLine)}) -> {Path.Combine(outDir, "variants.json")}");
                    }
                    var ev = s.Evaluate();
                    int trim = -1;
                    if (Opt("trim") is { Length: > 0 } tn) trim = int.Parse(tn);
                    if (Opt("trim-goal") is { Length: > 0 } tg) { var gp = tg.Split('+'); var g = ev["goals"]!.AsArray()[int.Parse(gp[0])]!; trim = (int?)g["firstFrame"] ?? -1; if (trim < 0) throw new ArgumentException($"--trim-goal {tg}: goal not reached"); if (gp.Length > 1) trim += int.Parse(gp[1]); }
                    ev["edits"] = new JsonArray(applied.Select(x => (JsonNode)x!).ToArray());
                    ev["run"] = s.Save(outDir, trim); ev["trimmedTo"] = trim >= 0 ? trim : null;
                    File.WriteAllText(Path.Combine(outDir, "edit.json"), ev.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine(ev.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
                case "diff":
                {
                    if (args.Length < 4) throw new ArgumentException("--tas diff A B");
                    var a = LoadRun(target, Opt("console"), Opt("rom"), Opt("start", "power-on"));
                    var b = LoadRun(args[3], Opt("console"), Opt("romb") is { Length: > 0 } rb ? rb : Opt("rom"), Opt("start", "power-on"));
                    var res = TasDiff.Run(a, b, Opt("rom") is { Length: > 0 } ra ? ra : null, Opt("romb") is { Length: > 0 } rb2 ? rb2 : Opt("rom") is { Length: > 0 } ra2 ? ra2 : null,
                        Opt("out", Path.Combine(Path.GetTempPath(), "brokennes-tas", "diff_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"))), int.Parse(Opt("ram-every", "1")), int.Parse(Opt("max", int.MaxValue.ToString())));
                    Console.WriteLine(res.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    return (bool)res["identical"]! ? 0 : 1;
                }
                case "vars":
                {
                    var run = LoadRun(target, Opt("console"), Opt("rom"), Opt("start", "power-on"));
                    using var s = new TasSession(run, Opt("chapter", run.Chapters[0].Id), Opt("rom") is { Length: > 0 } r ? r : null, Options(run, Opt, Has).Open, Path.Combine(Path.GetTempPath(), "brokennes-tas", "vars"));
                    s.Seek(int.Parse(Opt("frame", "0")));
                    Console.WriteLine(s.Status().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
                case "export":
                {
                    string console = Opt("console", "nes");
                    var prog = target.EndsWith(".tlog") ? TasScript.ReadLog(target, TasTargets.ConsoleOps(console)) : TasScript.ExpandFile(target, TasButtonMap.For(console), 60, TasTargets.ConsoleOps(console));
                    if (Opt("tlog") is { Length: > 0 } tl) { using var w = new StreamWriter(tl); TasScript.WriteLog(prog, w); }
                    if (Opt("fm2") is { Length: > 0 } fm)
                    {
                        if (prog.Console != "nes") throw new NotSupportedException(".fm2 is FCEUX's NES format");
                        var bad = prog.Steps.FirstOrDefault(s => !s.IsFrame && s.Op is "loadstate" or "poke" or "reset");
                        if (bad != null) throw new NotSupportedException($".fm2 export: line {bad.Line} '{bad.Op}' has no FCEUX movie equivalent");
                        var nes = new NesEmulator.NES(); nes.LoadROM(NesEmulator.Systems.RomDetect.Unwrap(File.ReadAllBytes(Opt("rom")), Opt("rom"), out _));
                        var w = new Fm2Writer { EmuVersion = 22020, RomFilename = Path.GetFileNameWithoutExtension(Opt("rom")), RomChecksumMd5 = nes.ComputeRomMd5(), ChecksumBase64 = true, Port1 = (int)Fm2InputDevice.Gamepad };
                        w.Comments.Add("author BrokenNes TAS script export");
                        foreach (var s in prog.Steps.Where(s => s.IsFrame)) w.RecordFrame(false, NesBools(s.Pads[0].Buttons), NesBools(s.Pads.Length > 1 ? s.Pads[1].Buttons : 0));
                        w.Save(fm);
                    }
                    Console.WriteLine($"TAS export: {prog.Frames} frames");
                    return 0;
                }
                case "branch":
                {
                    var run = LoadRun(target, Opt("console"), Opt("rom"), Opt("start", "power-on"));
                    var o = Options(run, Opt, Has); o.Twice = false;
                    var res = Branch(run, Opt("chapter"), int.Parse(Opt("frame")), Opt("out"), Opt("tail") is { Length: > 0 } t ? File.ReadAllLines(t) : null, o);
                    Console.WriteLine(res.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    return res["error"] == null ? 0 : 1;
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or FileNotFoundException or NotSupportedException or InvalidDataException) { Console.Error.WriteLine("TAS: " + e.Message); return 2; }
        Console.Error.WriteLine($"--tas {verb}: unknown verb"); return 2;
    }

    /// <summary>One edit: "set F:BTNS[:N]", "insert F:BTNS[:N]", "delete F[:N]".</summary>
    internal static void Apply(TasSession s, string e)
    {
        var w = e.Split(' ', 2, StringSplitOptions.TrimEntries); var v = w[1].Split(':');
        switch (w[0])
        {
            case "set": s.Set(int.Parse(v[0]), v.Length > 2 ? int.Parse(v[2]) : 1, s.Parse(v[1]), append: true); break;
            case "insert": s.Insert(int.Parse(v[0]), v.Length > 2 ? int.Parse(v[2]) : 1, s.Parse(v[1])); break;
            case "delete": s.Delete(int.Parse(v[0]), v.Length > 1 ? int.Parse(v[1]) : 1); break;
            default: throw new FormatException($"edit '{e}': set F:BTNS[:N] | insert F:BTNS[:N] | delete F[:N]");
        }
    }

    internal static TasRun LoadRun(string target, string console, string rom, string start)
    {
        if (target.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { var r = TasRun.Load(target); if (rom.Length > 0) r.Rom = Path.GetFullPath(rom); return r; }
        if (console.Length == 0) console = rom.Length > 0 ? TasTargets.DetectConsole(rom) : throw new ArgumentException("a bare script needs --console or --rom");
        return TasRun.ForScript(target, console, rom.Length > 0 ? Path.GetFullPath(rom) : "", start);
    }

    private static TasRunOptions Options(TasRun run, Func<string, string, string> Opt, Func<string, bool> Has) => new()
    {
        OutDir = Path.GetFullPath(Opt("out", Path.Combine(Environment.GetEnvironmentVariable("BROKENNES_TAS_OUT") ?? Path.Combine(Path.GetTempPath(), "brokennes-tas"), run.Chapters[0].Id + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")))),
        Twice = !Has("once"),
        Open = new TasOpenOptions { Cores = Opt("cores", ""), N64Renderer = Opt("renderer", "") is { Length: > 0 } r ? r : null },
    };

    private static void Summary(JsonObject rep, string outDir)
    {
        foreach (var c in rep["chapters"]!.AsArray())
        {
            var end = rep["checkpoints"]!.AsArray().LastOrDefault(x => (string?)x!["chapter"] == (string?)c!["id"]);
            Console.WriteLine($"  chapter {c!["id"]}: {c["frames"]} frames, end picture {end?["picture"]} ram {end?["ram"]}{(c["goal"] is JsonNode g ? $", goal {((bool)g ? "met" : "NOT met")}" : "")}");
        }
        Console.WriteLine($"TAS {rep["console"]} frames={rep["frames"]} checkpoints={rep["checkpoints"]!.AsArray().Count} error={(rep["error"] == null ? "none" : (string?)rep["error"])} " +
                          $"deterministic={rep["determinism"]!["identical"]?.ToString() ?? "unchecked"} expect={rep["expect"]!["ok"]}({rep["expect"]!["checked"]}) -> {Path.Combine(outDir, "report.json")}");
    }

    private static ulong NesBits(PadButtons b) { ulong r = 0; var a = Debug.DebugHost.NesPad(b); for (int i = 0; i < 8; i++) if (a[i]) r |= 1UL << i; return r; }
    private static ulong Bits(bool[] a) { ulong r = 0; for (int i = 0; i < a.Length && i < 8; i++) if (a[i]) r |= 1UL << i; return r; }
    private static bool[] NesBools(ulong b) { var r = new bool[8]; for (int i = 0; i < 8; i++) r[i] = (b & (1UL << i)) != 0; return r; }

    /// <summary>Runs a run up to frame F of a chapter, saves the state there, and writes DIR/branch.tasrun.json: one chapter from that state with the
    /// tail script (given) or the rest of the original chapter as an exact log.</summary>
    internal static JsonObject Branch(TasRun run, string chapter, int frame, string outDir, string[]? tail, TasRunOptions o)
    {
        var ch = run.Chapters.FirstOrDefault(c => c.Id == chapter) ?? throw new ArgumentException($"no chapter '{chapter}' (have {string.Join(", ", run.Chapters.Select(c => c.Id))})");
        outDir = Path.GetFullPath(outDir); Directory.CreateDirectory(outDir);
        o.OutDir = Path.Combine(outDir, "to_branch"); o.Only = chapter; o.StopAtFrame = frame; o.Screenshots = false; o.Twice = false;
        var rep = TasRunner.Run(run, o);
        if (rep["error"] != null) return rep;
        File.Copy(Path.Combine(o.OutDir, chapter, "end.state"), Path.Combine(outDir, "branch.state"), true);
        string script;
        if (tail != null) { script = "tail.tas"; File.WriteAllLines(Path.Combine(outDir, script), tail); }
        else
        {
            var prog = TasRunner.Expand(run, ch, 60);
            var rest = new TasProgram { Console = prog.Console, Map = prog.Map, Fps = prog.Fps, PadsUsed = prog.PadsUsed };
            int f = 0; foreach (var s in prog.Steps) { if (f >= frame) rest.Steps.Add(s); if (s.IsFrame) f++; }
            script = "rest.tlog"; using var w = new StreamWriter(Path.Combine(outDir, script)); TasScript.WriteLog(rest, w);
        }
        var j = new JsonObject
        {
            ["schema"] = 1, ["console"] = run.Console, ["game"] = run.Game, ["rom_sha1"] = (string?)rep["romSha1"], ["rom"] = (string?)rep["rom"], ["cores"] = run.Cores, ["power_on"] = run.PowerOn, ["ntsc"] = run.Ntsc,
            ["branched_from"] = new JsonObject { ["run"] = run.Path, ["chapter"] = chapter, ["frame"] = frame },
            ["chapters"] = new JsonArray(new JsonObject { ["id"] = chapter + "_b" + frame, ["start"] = "state:branch.state", ["script"] = script, ["goal"] = new JsonArray(ch.Goal.Select(g => (JsonNode)g!).ToArray()) }),
        };
        string path = Path.Combine(outDir, "branch.tasrun.json");
        File.WriteAllText(path, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new JsonObject { ["branch"] = path, ["state"] = Path.Combine(outDir, "branch.state"), ["frame"] = frame, ["script"] = Path.Combine(outDir, script), ["picture"] = rep["checkpoints"]!.AsArray().Last()!["picture"]?.DeepClone() };
    }

    /// <summary>An AutoTAS package (autotas.json + seg/*.bnm) as a TAS run: one script per chapter, chained (boot from power-on, then previous),
    /// each chapter's expect = the package's key_picture / key_ram at its end.</summary>
    private static int ImportAutoTas(string pkg, string outDir)
    {
        var a = JsonNode.Parse(File.ReadAllText(Path.Combine(pkg, "autotas.json")))!.AsObject();
        Directory.CreateDirectory(outDir);
        var chs = new JsonArray(); bool first = true;
        foreach (var c in a["chapters"]!.AsArray())
        {
            string id = (string)c!["id"]!;
            var mv = Movie.Load(Path.Combine(pkg, (string)c["segment"]!));
            var frames = new List<TasPad[]>(); var cur = new BrokenNes.Workshop.Movies.MoviePlayer.Cursor(mv);
            for (long f = 0; f < mv.Frames; f++) { cur.At(f); frames.Add(new[] { new TasPad(NesBits(cur.P1), 0, 0), new TasPad(NesBits(cur.P2), 0, 0) }); }
            File.WriteAllLines(Path.Combine(outDir, id + ".tas"), new[] { $"# {c["title"]}: AutoTAS chapter {id} (from {c["segment"]}, {mv.Frames} frames)" }.Concat(TasScript.Compact(TasButtonMap.Nes, frames, mv.UsesPad2 ? 2 : 1)));
            if (mv.Start == MovieStart.State) File.WriteAllBytes(Path.Combine(outDir, id + ".start.state"), mv.StartState!);
            chs.Add(new JsonObject
            {
                ["id"] = id, ["title"] = (string?)c["title"], ["start"] = first ? "power-on" : "previous", ["script"] = id + ".tas",
                ["expect"] = new JsonObject { ["frames"] = mv.Frames, ["checkpoints"] = new JsonObject { ["end"] = new JsonObject { ["picture"] = (string?)c["key_picture"], ["ram"] = (string?)c["key_ram"] } } },
            });
            first = false;
        }
        var core = (string?)a["core"]?["cores"] ?? "";
        var run = new JsonObject
        {
            ["schema"] = 1, ["console"] = (string?)a["console"], ["game"] = (string?)a["game_id"], ["title"] = (string?)a["title"], ["rom_sha1"] = (string?)a["rom_sha1"],
            ["cores"] = string.Join("/", core.Split('/').Select(x => x.Contains('_') ? x[(x.IndexOf('_') + 1)..] : x)), ["power_on"] = "fceux", ["ntsc"] = false,
            ["source"] = Path.GetFullPath(pkg), ["chapters"] = chs,
        };
        File.WriteAllText(Path.Combine(outDir, "run.tasrun.json"), run.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"TAS import-autotas: {chs.Count} chapters -> {Path.Combine(outDir, "run.tasrun.json")}");
        return 0;
    }
}

/// <summary>--frames-dir: every frame as a PNG (and the sound as a WAV) — the plain offline dump.</summary>
internal sealed class FrameDirSink : ITasFrameSink
{
    private readonly string dir; private int n; private BinaryWriter? wav; private long bytes; private int rate;
    public FrameDirSink(string d) { dir = d; Directory.CreateDirectory(d); }
    public void Begin(int w, int h, double fps, int sampleRate) { if (wav != null) return; rate = sampleRate; wav = new BinaryWriter(File.Create(Path.Combine(dir, "audio.wav"))); wav.Write(new byte[44]); }
    public void Frame(uint[] argb, int w, int h) => TasPng.Write(Path.Combine(dir, $"f{n++:D6}.png"), argb, w, h);
    public void Audio(ReadOnlySpan<short> s) { foreach (var v in s) wav!.Write(v); bytes += s.Length * 2; }
    public void End()
    {
        if (wav == null) return;
        wav.Seek(0, SeekOrigin.Begin);
        wav.Write("RIFF"u8); wav.Write((int)(36 + bytes)); wav.Write("WAVEfmt "u8); wav.Write(16); wav.Write((short)1); wav.Write((short)2); wav.Write(rate); wav.Write(rate * 4); wav.Write((short)4); wav.Write((short)16);
        wav.Write("data"u8); wav.Write((int)bytes); wav.Dispose(); wav = null;
    }
}
