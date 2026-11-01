using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Capture;
using NesEmulator.Debugging.Checks;
using NesEmulator.Debugging.Movies;
using NesEmulator.Debugging.Port;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Bugs;

/// <summary>
/// The chaos bots' pad (milestone D9, "D9 settled": chaos bots): a seeded generator of segments: random presses, held directions, jump
/// spam, menu mashing, picked by weight. Its own SplitMix64, so a seed gives the same pads on every machine and every .NET.
/// </summary>
internal sealed class ChaosPad
{
    public sealed class Weights
    {
        public double Press = 3, Hold = 3, Jump = 3, Mash = 1;
        public static Weights Parse(string text)
        {
            var w = new Weights();
            if (string.IsNullOrWhiteSpace(text)) return w;
            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length != 2 || !double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < 0) throw new ArgumentException($"chaos weights: '{part}' is not name=number (press, hold, jump, mash)");
                switch (kv[0].ToLowerInvariant())
                {
                    case "press": w.Press = v; break;
                    case "hold": w.Hold = v; break;
                    case "jump": w.Jump = v; break;
                    case "mash": w.Mash = v; break;
                    default: throw new ArgumentException($"chaos weights: unknown '{kv[0]}' (press, hold, jump, mash)");
                }
            }
            if (w.Press + w.Hold + w.Jump + w.Mash <= 0) throw new ArgumentException("chaos weights: all zero");
            return w;
        }
        public override string ToString() => FormattableString.Invariant($"press={Press},hold={Hold},jump={Jump},mash={Mash}");
    }

    private ulong s;
    private readonly Weights w;
    private readonly PadButtons[] buttons;
    private static readonly PadButtons[] Dirs = { PadButtons.Up, PadButtons.Down, PadButtons.Left, PadButtons.Right };
    private int left, mode, toggle, period; private PadButtons held;

    public ChaosPad(ulong seed, Weights weights, string console)
    {
        s = seed; w = weights;
        var b = new List<PadButtons> { PadButtons.A, PadButtons.B, PadButtons.Select, PadButtons.Start };
        if (console is "snes" or "wms") b.AddRange(new[] { PadButtons.X, PadButtons.Y, PadButtons.L, PadButtons.R });
        buttons = b.ToArray();
    }

    /// <summary>A bot's seed from the run's seed and its number (bot 0 has the run's seed itself).</summary>
    public static ulong BotSeed(ulong seed, int bot) => bot == 0 ? seed : Mix(seed + 0x9E3779B97F4A7C15UL * (ulong)bot);

    private static ulong Mix(ulong z) { z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; return z ^ (z >> 31); }
    private ulong Next() { s += 0x9E3779B97F4A7C15UL; return Mix(s); }
    private int Range(int lo, int hi) => lo + (int)(Next() % (ulong)(hi - lo + 1));
    private PadButtons Dir() => Dirs[Range(0, 3)];
    private PadButtons Btn() => buttons[Range(0, buttons.Length - 1)];

    /// <summary>The pad for the next frame.</summary>
    public PadButtons Frame()
    {
        if (left <= 0) Begin();
        left--;
        switch (mode)
        {
            case 2:   // jump spam: the held direction, A on and off
                if (--toggle <= 0) { toggle = period; held ^= PadButtons.A; }
                return held;
            case 3:   // menu mashing: each frame a fresh mix of the face buttons
                PadButtons m = PadButtons.None;
                foreach (var b in buttons) if ((Next() & 3) == 0) m |= b;
                if ((Next() & 7) == 0) m |= Dir();
                return m;
            default: return held;
        }
    }

    private void Begin()
    {
        double total = w.Press + w.Hold + w.Jump + w.Mash;
        double r = (Next() >> 11) * (1.0 / (1UL << 53)) * total;
        mode = r < w.Press ? 0 : r < w.Press + w.Hold ? 1 : r < w.Press + w.Hold + w.Jump ? 2 : 3;
        switch (mode)
        {
            case 0:   // a random press (a button or two, maybe a direction), then a short release
                if ((Next() & 1) == 0 && held != PadButtons.None) { held = PadButtons.None; left = Range(1, 8); break; }
                held = Btn(); if ((Next() & 1) == 0) held |= Btn(); if ((Next() & 1) == 0) held |= Dir();
                left = Range(1, 10); break;
            case 1:   // a held direction (sometimes running: B)
                held = Dir(); if ((Next() & 3) == 0) held |= PadButtons.B;
                left = Range(30, 120); break;
            case 2:
                held = (Next() & 3) == 0 ? PadButtons.None : Dir();
                period = Range(2, 6); toggle = period; left = Range(30, 90); break;
            default: left = Range(20, 60); break;
        }
    }
}

/// <summary>One problem a chaos bot found.</summary>
internal sealed class ChaosProblem
{
    public int Bot; public ulong Seed; public string Kind = ""; public long Frame; public string Detail = "";
    public string Movie = "", Bug = "";
    public JsonObject Json() => new()
    {
        ["bot"] = Bot, ["seed"] = Seed.ToString(CultureInfo.InvariantCulture), ["kind"] = Kind, ["frame"] = Frame, ["detail"] = Detail,
        ["movie"] = Movie, ["bug"] = Bug,
        ["replay"] = $"--chaos --rom <rom> --seed {Seed} --bots 1 --frames {Frame + 1}  (or: --movie play --movie {Movie})",
    };
}

/// <summary>
/// <c>--chaos</c>: the chaos bots (milestone D9 of docs/projects/brokendev-debugging-plan.md, "D9 settled"). They run ONLY when asked
/// (brokendev.ps1 chaos): never from checkup, never scheduled.
/// <code>
///   --chaos --rom R [--console c] [--cores C/P/A] [--sym F]... [--seed S] [--bots K] (--frames N | --minutes M)
///           [--state F]... [--weights press=3,hold=3,jump=3,mash=1] [--softlock SECONDS] [--off rule]... [--accept rule@place]...
///           [--no-checks] [--project ID] --out DIR [--json]
/// </code>
/// K headless sessions (the D4 checks and the D7 debug port on), bot 0 from power-on and the others in turn from each --state, each fed by
/// its seeded pad. A bot stops at its first problem: a crash (the core crashed or jammed, a BRK / RST $38 loop), a softlock (the input
/// changed but the picture and RAM did not for --softlock seconds), a new check finding (one not --accept'ed: a lag spike is the checks'
/// lag-frame rule), a debug-port assert. Each problem keeps a movie (.bnm, from the bot's start: movie play / bisect) and a D6 bug capture
/// (the last seconds, bug play), and its seed, which replays it exactly. Report: DIR\chaos.json. Exit 0 nothing found, 1 problems, 2 usage.
/// </summary>
internal static class ChaosCli
{
    public sealed class Options
    {
        public string Rom = "", Console = "", Cores = "", Project = "", Out = "";
        public List<string> Symbols = new(), States = new();
        public ulong Seed = 1; public int Bots = 1; public long Frames; public double Minutes;
        public ChaosPad.Weights Weights = new();
        public double SoftlockSeconds = 10;
        public HashSet<string> Off = new(StringComparer.Ordinal);
        public List<AcceptedFinding> Accepted = new();
        public bool Checks = true;
    }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d = "") { int i = Array.FindIndex(args, 1, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        List<string> All(string n) { var l = new List<string>(); for (int i = 1; i < args.Length - 1; i++) if (args[i].Equals("--" + n, StringComparison.OrdinalIgnoreCase)) l.Add(args[i + 1]); return l; }
        bool Has(string n) => args.Skip(1).Any(a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase));
        try
        {
            var o = new Options
            {
                Rom = Opt("rom"), Console = Opt("console"), Cores = Opt("cores"), Project = Opt("project"), Out = Opt("out"),
                Symbols = All("sym"), States = All("state"), Weights = ChaosPad.Weights.Parse(Opt("weights")), Checks = !Has("no-checks"),
            };
            if (o.Rom.Length == 0 || o.Out.Length == 0) throw new ArgumentException("--chaos needs --rom and --out");
            if (Opt("seed") is { Length: > 0 } sd && !ulong.TryParse(sd, NumberStyles.Integer, CultureInfo.InvariantCulture, out o.Seed)) throw new ArgumentException($"--seed {sd}: not a whole number");
            if (Opt("seed").Length == 0) o.Seed = (ulong)DateTime.UtcNow.Ticks & 0xFFFFFFFFUL;   // printed and recorded: every run can be repeated
            if (Opt("bots") is { Length: > 0 } b && (!int.TryParse(b, out o.Bots) || o.Bots < 1 || o.Bots > 64)) throw new ArgumentException("--bots: 1 to 64");
            if (Opt("frames") is { Length: > 0 } f && (!long.TryParse(f, out o.Frames) || o.Frames < 1)) throw new ArgumentException("--frames: a positive number");
            if (Opt("minutes") is { Length: > 0 } mi && (!double.TryParse(mi, NumberStyles.Float, CultureInfo.InvariantCulture, out o.Minutes) || o.Minutes <= 0)) throw new ArgumentException("--minutes: a positive number");
            if (o.Frames == 0 && o.Minutes == 0) o.Minutes = 1;
            if (Opt("softlock") is { Length: > 0 } sl && (!double.TryParse(sl, NumberStyles.Float, CultureInfo.InvariantCulture, out o.SoftlockSeconds) || o.SoftlockSeconds <= 0)) throw new ArgumentException("--softlock: seconds, positive");
            foreach (var r in All("off")) { if (CheckCatalogue.Find(r) == null) throw new ArgumentException($"--off: unknown rule '{r}'"); o.Off.Add(r); }
            foreach (var a in All("accept"))
            {
                int at = a.IndexOf('@');
                o.Accepted.Add(new AcceptedFinding { Rule = at < 0 ? a : a[..at], At = at < 0 ? "*" : a[(at + 1)..], Reason = "accepted by the project (chaos --accept)" });
            }
            var report = RunAll(o, line => System.Console.WriteLine(line));
            File.WriteAllText(Path.Combine(o.Out, "chaos.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (Has("json")) System.Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (int)report["problemCount"]! == 0 ? 0 : 1;
        }
        catch (Exception e) when (e is ArgumentException or FileNotFoundException or InvalidDataException or NotSupportedException or IOException)
        {
            System.Console.Error.WriteLine($"--chaos: {e.Message}");
            return 2;
        }
    }

    /// <summary>Runs the bots (in parallel), writes their movies and captures under <see cref="Options.Out"/>, returns the report.</summary>
    public static JsonObject RunAll(Options o, Action<string> log)
    {
        Directory.CreateDirectory(o.Out);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deadline = o.Minutes > 0 ? DateTime.UtcNow.AddMinutes(o.Minutes) : DateTime.MaxValue;
        log($"chaos: {o.Bots} bot(s), seed {o.Seed}, {(o.Frames > 0 ? o.Frames + " frames each" : o.Minutes.ToString(CultureInfo.InvariantCulture) + " minute(s)")}, weights {o.Weights}, softlock {o.SoftlockSeconds} s, checks {(o.Checks ? "on" : "off")}");
        var results = new BotResult[o.Bots];
        Parallel.For(0, o.Bots, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(o.Bots, Environment.ProcessorCount / 2)) },
            k => results[k] = RunBot(o, k, deadline));
        var problems = results.Where(r => r.Problem != null).Select(r => r.Problem!).ToList();
        foreach (var r in results)
            log(r.Problem != null ? $"  bot {r.Bot} (seed {r.Seed}, from {r.Start}): {r.Problem.Kind.ToUpperInvariant()} at frame {r.Problem.Frame}: {r.Problem.Detail}\n      movie {r.Problem.Movie}\n      bug   {r.Problem.Bug}"
                                  : r.Error != null ? $"  bot {r.Bot} (seed {r.Seed}): could not run: {r.Error}"
                                  : $"  bot {r.Bot} (seed {r.Seed}, from {r.Start}): nothing found in {r.Frames} frames");
        log($"chaos: {problems.Count} problem(s) in {results.Sum(r => r.Frames)} frames, {sw.Elapsed.TotalSeconds:F1} s");
        return new JsonObject
        {
            ["format"] = "brokendev-chaos", ["version"] = 1, ["project"] = o.Project.Length > 0 ? o.Project : null, ["rom"] = Path.GetFullPath(o.Rom),
            ["romSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(o.Rom))).ToLowerInvariant(),
            ["seed"] = o.Seed.ToString(CultureInfo.InvariantCulture), ["bots"] = o.Bots, ["frames"] = o.Frames, ["minutes"] = o.Minutes, ["weights"] = o.Weights.ToString(),
            ["softlockSeconds"] = o.SoftlockSeconds, ["checks"] = o.Checks, ["emulator"] = MovieMachine.EmulatorBuild(), ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            ["problemCount"] = problems.Count,
            ["problems"] = new JsonArray(problems.Select(p => (JsonNode)p.Json()).ToArray()),
            ["runs"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject
            {
                ["bot"] = r.Bot, ["seed"] = r.Seed.ToString(CultureInfo.InvariantCulture), ["start"] = r.Start, ["frames"] = r.Frames, ["fps"] = Math.Round(r.Fps),
                ["result"] = r.Problem?.Kind ?? (r.Error != null ? "error" : "clean"), ["error"] = r.Error, ["ranBytes"] = r.RanBytes,
                ["acceptedFindings"] = r.Accepted,
            }).ToArray()),
        };
    }

    public sealed class BotResult
    {
        public int Bot; public ulong Seed; public string Start = "power-on"; public long Frames; public double Fps; public long RanBytes = -1;
        public int Accepted; public ChaosProblem? Problem; public string? Error;
    }

    /// <summary>The BRK-loop watch: the main CPU's break opcode (6502 / 65816 BRK, the Game Boy's RST $38) counted per frame.</summary>
    private sealed class BrkWatch : DebugObserver
    {
        private readonly byte op; public long Count;
        public BrkWatch(byte opcode) { op = opcode; }
        public override bool WantsSteps(int cpuIndex) => cpuIndex == 0;
        public override void OnStep(DebugCpu c, long cycle, bool executes, string? interrupt) { if (executes && interrupt == null && c.Peek(c.PC) == op) Count++; }
    }

    private const int BrkLoopPerFrame = 64;
    private const int KeyEvery = 60;

    public static BotResult RunBot(Options o, int bot, DateTime deadline)
    {
        var res = new BotResult { Bot = bot, Seed = ChaosPad.BotSeed(o.Seed, bot) };
        byte[]? state = null;
        if (bot > 0 && o.States.Count > 0) { string sp = o.States[(bot - 1) % o.States.Count]; state = File.ReadAllBytes(sp); res.Start = Path.GetFileName(sp); }
        MovieMachine m;
        try { m = MovieMachine.Open(o.Rom, o.Console.Length > 0 ? o.Console : null, o.Cores, "fceux", false, true, o.Symbols.Count > 0 ? o.Symbols : null); }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or FileNotFoundException) { res.Error = e.Message; return res; }
        using (m)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var mv = MoviePlayer.NewFor(m, 0, KeyEvery, $"chaos seed {o.Seed} bot {bot}");
            if (state != null) { m.Host.LoadState(state); mv.Start = MovieStart.State; mv.StartState = state; }
            var ring = new BugCaptureRing();
            var src = m.Host.Nes != null ? BugCaptureSource.For(m.Host.Nes) : BugCaptureSource.For(m.Host.Session!);
            ring.Attach(src, 44100, m.Host.Session?.FramesPerSecond ?? 60, powerOn: state == null);
            string? assert = null;
            if (m.Port != null) m.Port.Event += e => { ring.Note(e.KindName, e.Text); if (e.Kind == PortEventKind.Assert && assert == null) assert = $"debug-port assert '{e.Text}' at {e.Place}"; };
            ConsoleChecks? checks = null; BrkWatch? brk = null; CodeCoverage? cov = null;
            if (m.Host.Dbg is { } dbg)
            {
                if (o.Checks)
                {
                    var co = new CheckOptions { DebugPort = false };   // the movie machine's own port hears the asserts
                    foreach (var r in o.Off) co.Off.Add(r);
                    if (state != null) foreach (var r in new[] { "nes.ram-uninit", "snes.wram-uninit", "snes.vram-uninit", "snes.cgram-uninit", "snes.oam-uninit", "gb.ram-uninit" }) co.Off.Add(r);
                    checks = CheckRun.Attach(dbg, co);
                }
                brk = new BrkWatch(m.ConsoleKey is "gb" or "gbc" ? (byte)0xFF : (byte)0x00);
                dbg.AddObserver(brk);
                cov = CodeCoverage.Attach(dbg);
            }
            var pad = new ChaosPad(res.Seed, o.Weights, m.ConsoleKey);
            double fps = m.Host.Session?.FramesPerSecond ?? 60.0988;
            long softlockFrames = (long)Math.Ceiling(o.SoftlockSeconds * fps);
            ulong lastPic = 0, lastRam = 0; long sameSince = 0; bool inputChanged = false; PadButtons prev = PadButtons.None;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pcm = new short[8192]; var mono = new float[4096];
            string? kind = null, detail = null;
            long f = 0;
            for (; o.Frames <= 0 || f < o.Frames; f++)
            {
                if ((f & 63) == 0 && DateTime.UtcNow >= deadline) break;
                var p1 = pad.Frame();
                mv.SetPads(f, p1, PadButtons.None);
                ring.Frame(p1, PadButtons.None);
                long brkBefore = brk?.Count ?? 0;
                m.Frame(p1, PadButtons.None);
                if (m.Host.Nes is { } n) { var au = n.GetAudioBuffer(); ring.Audio(au, au.Length); }
                else { int k; while ((k = m.Host.Session!.ReadSamples(pcm)) > 0) { int fr = Math.Min(k / 2, mono.Length); for (int i = 0; i < fr; i++) mono[i] = (pcm[i * 2] + pcm[i * 2 + 1]) / 65536f; ring.Audio(mono, fr); } }
                if (Movie.IsKeyFrame(f + 1, long.MaxValue, KeyEvery)) mv.Keys.Add(new MovieKey { Frame = f + 1, Picture = m.Picture(), Ram = m.Ram(), Vars = Array.Empty<long>() });
                // ---- the problems, in the order a person would name them
                if (m.Crashed) { kind = "crash"; detail = $"the core crashed: {m.CrashInfo.Split('\n')[0]}"; break; }
                if (brk != null && brk.Count - brkBefore >= BrkLoopPerFrame) { kind = "crash"; detail = $"a BRK loop: {brk.Count - brkBefore} break instructions in one frame (PC ${m.Host.Dbg!.Machine.Main.PC:X4})"; break; }
                if (assert != null) { kind = "assert"; detail = assert; break; }
                if (checks != null)
                {
                    Finding? fresh = null;
                    foreach (var fi in checks.Findings)
                    {
                        if (seen.Contains(fi.Key)) continue;
                        seen.Add(fi.Key);
                        if (o.Accepted.Any(a => a.Matches(fi))) { res.Accepted++; continue; }
                        fresh ??= fi;
                    }
                    if (fresh != null) { kind = "finding"; detail = $"NEW {fresh.Rule} at {fresh.Place} (first frame {fresh.FirstFrame}, PC {fresh.PcSymbol ?? "$" + fresh.Pc.ToString("X4")}): {fresh.Detail}"; break; }
                }
                ulong pic = m.Picture(), ram = m.Ram();
                if (f == 0 || pic != lastPic || ram != lastRam) { lastPic = pic; lastRam = ram; sameSince = f; inputChanged = false; }
                else
                {
                    if (p1 != prev) inputChanged = true;
                    if (inputChanged && f - sameSince >= softlockFrames) { kind = "softlock"; detail = $"the input changed but the picture and RAM have not since frame {sameSince + 1} ({o.SoftlockSeconds} s)"; break; }
                }
                prev = p1;
            }
            res.Frames = Math.Min(f + 1, o.Frames > 0 ? o.Frames : f + 1);
            if (kind == null) res.Frames = f;
            res.Fps = res.Frames / Math.Max(1e-9, sw.Elapsed.TotalSeconds);
            if (cov != null) res.RanBytes = cov.Ran.LongCount(x => x);
            checks?.Finish();
            if (kind == null) return res;

            // ---- keep it: the movie from the bot's start, and the D6 capture of the last seconds
            long frames = f + 1;
            mv.Frames = frames;
            if (mv.Keys.Count == 0 || mv.Keys[^1].Frame != frames) mv.Keys.Add(new MovieKey { Frame = frames, Picture = m.Picture(), Ram = m.Ram(), Vars = Array.Empty<long>() });
            mv.Marks.AddRange(m.MarksSeen.Where(k => k.Frame < frames));
            string dir = Path.Combine(o.Out, $"bot{bot}-seed{res.Seed}");
            Directory.CreateDirectory(dir);
            string moviePath = Path.Combine(dir, $"chaos-{kind}.bnm");
            mv.Save(moviePath);
            string bugPath = "";
            try
            {
                var d = ring.Capture();
                var meta = new BugMeta
                {
                    App = MovieMachine.EmulatorBuild() + " (--chaos)", RomName = Path.GetFileName(o.Rom), Rom = m.RomFile, RomPath = m.RomPath, Cores = m.Cores,
                    Note = $"chaos {kind} at frame {frames}: {detail} (seed {o.Seed}, bot {bot}, bot seed {res.Seed}; replay: --chaos --seed {o.Seed} --bots {bot + 1} or the movie {Path.GetFileName(moviePath)})",
                    Build = $"chaos seed {o.Seed}",
                };
                meta.Settings.Add(("chaos.seed", o.Seed.ToString(CultureInfo.InvariantCulture)));
                meta.Settings.Add(("chaos.bot", bot.ToString(CultureInfo.InvariantCulture)));
                meta.Settings.Add(("chaos.weights", o.Weights.ToString()));
                bugPath = BugBundle.WriteFolder(Path.Combine(dir, "bugs"), BugBundle.Files(d, meta), meta.CapturedLocal);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException) { bugPath = "(no capture: " + e.Message + ")"; }
            res.Problem = new ChaosProblem { Bot = bot, Seed = res.Seed, Kind = kind, Frame = frames, Detail = detail!, Movie = moviePath, Bug = bugPath };
            return res;
        }
    }
}
