using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>
/// The cross-console comparison (Sega VRUN ports, milestone S0 of docs/projects/sega-vrun-ports-plan.md; it replaces Timeflow T6). A session is
/// recorded on the ORIGINAL (a Timeflow timeline: Game Boy, NES, SNES, any console), its pads are replayed on the PORT (another ROM, another
/// console), both games' variables are read by name every frame (each side's own symbols, through the debugger's expressions), and the two runs
/// are compared per game-logic EVENT (a variable of kind "event" changing: a room, an item, a death, a clear) with each side's tick rate mapped
/// to a common time, and key POSITIONS (kind "position") compared at every event and every N ticks, within the per-game tolerances of the
/// mapping (a project manifest's "comparison" section, or a JSON file of the same shape). The FIRST drift is reported with both sides' state,
/// and both sides are opened at that point with T3's profiler (a span before the drift, the drifting variable's writes). The port's run is
/// recorded as a timeline; <c>recheck</c> replays a port's recorded sessions on a rebuild and reports the first frame the change altered.
/// <code>
///   --timeflow compare  (--original TIMELINE | --original-rom R --input S|auto[:seed] [--input2 S] --frames N [--original-console c] [--original-cores C/P/A] [--original-project P])
///                       [--original-sym F]... --port ROM [--port-console c] [--port-cores C/P/A] [--port-sym F]... [--port-project P]
///                       (--map FILE.json | --manifest project.json) [--frames N] [--window 30] [--no-open] [--json]
///   --timeflow recheck  (--timeline ID | --project P | --game-dir DIR) --rom NEW [--sym-new F]... [--sym-old F]... [--map FILE | --manifest M] [--json]
/// </code>
/// compare: exit 0 no drift, 1 a drift (reported), 2 usage, 3 cannot run. recheck: exit 0 no recorded session changed, 1 a change (its first frame).
/// </summary>
internal static class TimeflowComparing
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Cli(string verb, Func<string, string, string> Opt, Func<string, List<string>> All, Func<string, bool> Has, bool json)
    {
        if (verb == "compare")
        {
            var a = new CompareArgs
            {
                Original = Opt("original", ""), OriginalRom = Opt("original-rom", ""), Input = Opt("input", ""), Input2 = Opt("input2", ""),
                Frames = long.TryParse(Opt("frames", ""), out long fr) ? fr : 0, OriginalConsole = Opt("original-console", ""), OriginalCores = Opt("original-cores", ""),
                OriginalProject = Opt("original-project", ""), OriginalSymbols = All("original-sym"), Port = Opt("port", ""), PortConsole = Opt("port-console", ""),
                PortCores = Opt("port-cores", ""), PortSymbols = All("port-sym"), PortProject = Opt("port-project", ""), Map = Opt("map", ""), Manifest = Opt("manifest", ""),
                Window = int.TryParse(Opt("window", ""), out int w) ? w : 30, Open = !Has("no-open"),
            };
            var r = Compare(a);
            Console.WriteLine(json ? r.ToJsonString(Indented) : CompareText(r));
            return r["drift"] is null ? 0 : 1;
        }
        if (verb == "recheck")
        {
            var r = Recheck(Opt("timeline", ""), Opt("project", ""), Opt("game-dir", ""), Opt("rom", ""), All("sym-new"), All("sym-old"), Opt("map", ""), Opt("manifest", ""));
            Console.WriteLine(json ? r.ToJsonString(Indented) : RecheckText(r));
            return r["changed"]?.GetValue<bool>() == true ? 1 : 0;
        }
        throw new ArgumentException($"unknown compare verb '{verb}'");
    }

    // ================================================================================================ the mapping

    public sealed class Var
    {
        public string Name = "", Kind = "event", Original = "", Port = "", Watch = "";
        public double Tolerance, ScaleO = 1, ScaleP = 1, OffsetO, OffsetP;
        public double Mapped(long v, bool port) => port ? v * ScaleP + OffsetP : v * ScaleO + OffsetO;
    }

    public sealed class Rate
    {
        public double Fps;          // frames a second (0: the console's own, from the machine)
        public double TickHz;       // game ticks a second (0: one tick a frame)
        public string Tick = "";    // an expression that reads the game's own tick counter (wins over TickHz)
        public string Origin = "";  // "var=value": time 0 is the first frame that variable has that value (the two games' clocks start at different moments)
    }

    public sealed class Spec
    {
        public List<Var> Vars = new();
        public Rate O = new(), P = new();
        public double TickTolerance, PositionTolerance;
        public string Input = "auto";                     // time | frame | auto (time when the frame rates differ)
        public Dictionary<string, string> Buttons = new(StringComparer.OrdinalIgnoreCase);
        public double SampleEveryTicks;
        public string OriginalProject = "", Note = "";
        public long SettleFrames;
        public string TickLeadWhen = "";   // "var=value": tickLead applies only while the port's variable has that value (S3: in gameplay, not the menus)
        public long TickLead;   // input tick: frames added to the re-anchored mapping (comparison.tickLead)

        public static Spec Load(string map, string manifest)
        {
            if (map.Length > 0) return Parse(JsonDocument.Parse(File.ReadAllText(map)).RootElement, map);
            if (manifest.Length > 0)
            {
                var root = JsonDocument.Parse(File.ReadAllText(manifest)).RootElement;
                if (!root.TryGetProperty("comparison", out var c)) throw new ArgumentException($"{manifest} has no 'comparison' section");
                return Parse(c, manifest);
            }
            throw new ArgumentException("the mapping is needed: --map FILE.json or --manifest project.json (its 'comparison' section)");
        }

        public static Spec Parse(JsonElement e, string where)
        {
            var s = new Spec();
            string Str(JsonElement x, string n, string d = "") => x.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? d : d;
            double Num(JsonElement x, string n, double d = 0) => x.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
            s.OriginalProject = Str(e, "original"); s.Note = Str(e, "what");
            if (e.TryGetProperty("rate", out var rate))
            {
                foreach (var (side, r) in new[] { ("original", s.O), ("port", s.P) })
                    if (rate.TryGetProperty(side, out var rs)) { r.Fps = Num(rs, "fps"); r.TickHz = Num(rs, "tickHz"); r.Tick = Str(rs, "tick"); r.Origin = Str(rs, "origin"); }
            }
            if (e.TryGetProperty("tolerance", out var tol)) { s.TickTolerance = Num(tol, "ticks"); s.PositionTolerance = Num(tol, "positions"); }
            s.Input = Str(e, "input", "auto").ToLowerInvariant();
            if (s.Input is not ("auto" or "time" or "frame" or "tick" or "update")) throw new ArgumentException($"{where}: comparison.input must be time, frame, tick, update or auto");
            s.SampleEveryTicks = Num(e, "sampleEveryTicks");
            s.SettleFrames = (long)Num(e, "settleFrames");
            s.TickLead = (long)Num(e, "tickLead", 0);
            s.TickLeadWhen = Str(e, "tickLeadWhen");
            if (e.TryGetProperty("buttons", out var b) && b.ValueKind == JsonValueKind.Object)
                foreach (var p in b.EnumerateObject()) s.Buttons[p.Name] = p.Value.GetString() ?? "";
            if (!e.TryGetProperty("variables", out var vars) || vars.ValueKind != JsonValueKind.Array || vars.GetArrayLength() == 0)
                throw new ArgumentException($"{where}: comparison.variables is empty (nothing to compare)");
            foreach (var v in vars.EnumerateArray())
            {
                var x = new Var { Name = Str(v, "name"), Kind = Str(v, "kind", "event").ToLowerInvariant(), Original = Str(v, "original"), Watch = Str(v, "watch") };
                x.Port = Str(v, "port", x.Original);
                if (x.Name.Length == 0 || x.Original.Length == 0) throw new ArgumentException($"{where}: a comparison variable needs a name and an 'original' expression");
                if (x.Kind is not ("event" or "position")) throw new ArgumentException($"{where}: variable {x.Name}: kind must be event or position");
                x.Tolerance = v.TryGetProperty("tolerance", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : x.Kind == "position" ? s.PositionTolerance : 0;
                if (v.TryGetProperty("scale", out var sc)) { x.ScaleO = Num(sc, "original", 1); x.ScaleP = Num(sc, "port", 1); }
                if (v.TryGetProperty("offset", out var of)) { x.OffsetO = Num(of, "original"); x.OffsetP = Num(of, "port"); }
                if (s.Vars.Any(o => o.Name == x.Name)) throw new ArgumentException($"{where}: variable {x.Name} twice");
                s.Vars.Add(x);
            }
            return s;
        }
    }

    /// <summary>A mapping expression: a bare symbol reads its byte ("room" -> "[room]"); "be16 sym" a big-endian word (the 68000); else the debugger's expression.</summary>
    public static string Expr(string e)
    {
        e = e.Trim();
        if (Regex.IsMatch(e, @"^[A-Za-z_.][A-Za-z0-9_.@]*$")) return $"[{e}]";
        var m = Regex.Match(e, @"^be16\s+(.+)$");
        if (m.Success) return $"(([{m.Groups[1].Value}]) << 8 | [{m.Groups[1].Value} + 1])";
        m = Regex.Match(e, @"^le16\s+(.+)$");
        if (m.Success) return $"{{{m.Groups[1].Value}}}";
        return e;
    }

    /// <summary>The symbol a profile watches for a variable: its own 'watch', else the first name in its expression.</summary>
    private static string WatchOf(Var v, bool port)
    {
        if (v.Watch.Length > 0) return v.Watch;
        var m = Regex.Match(port ? v.Port : v.Original, @"[A-Za-z_][A-Za-z0-9_]*");
        while (m.Success && m.Value is "be16" or "le16") m = m.NextMatch();
        return m.Success ? m.Value : "";
    }

    // ================================================================================================ the runs

    public sealed class CompareArgs
    {
        public string Original = "", OriginalRom = "", Input = "", Input2 = "", OriginalConsole = "", OriginalCores = "", OriginalProject = "";
        public List<string> OriginalSymbols = new(), PortSymbols = new();
        public string Port = "", PortConsole = "", PortCores = "", PortProject = "", Map = "", Manifest = "";
        public long Frames;
        public int Window = 30;
        public bool Open = true;
    }

    /// <summary>One side's run: per frame (after the frame) the variables' values and the time in ticks.</summary>
    private sealed class Side
    {
        public string Label = "", Console = "", Rom = "", RomSha = "", Timeline = "", TimelineDir = "";
        public List<string> Symbols = new();
        public string[] Watches = Array.Empty<string>();   // per variable: the CPU address range its expression reads ("$C1F9-$C1FA"), for the profile
        public double Fps;
        public readonly List<long[]> Values = new();
        public readonly List<double> Ticks = new();
        public long? OffFrame; public string OffWhat = "";
        public string Crash = "";
        public long Frames => Values.Count;
    }

    private sealed record Ev(int Var, long From, long To, long Frame, double Tick);

    private static double TickOf(Rate r, double fps, long frameDone, long tickValue, ref long tickBase, ref long tickPrev)
    {
        if (r.Tick.Length > 0)
        {
            // a 16-bit counter that wraps keeps counting up
            if (tickValue < tickPrev && tickPrev - tickValue > 0x8000) tickBase += 0x10000;
            tickPrev = tickValue;
            return tickBase + tickValue;
        }
        double hz = r.TickHz > 0 ? r.TickHz : fps;
        return frameDone * hz / fps;
    }

    private static double FpsOf(MovieMachine m)
    {
        var s = m.Host.Session;
        if (s != null && s.FramesPerSecond > 0) return s.FramesPerSecond;
        return m.ConsoleKey == "nes" ? 60.0988 : 60;
    }

    /// <summary>The CPU addresses an expression reads (its first [..] or {..}: one byte or a word), as a profile's 'writes' range; '' when it reads none.</summary>
    private static string WatchRange(NesEmulator.Debugging.Debugger d, string expr)
    {
        var m = Regex.Match(expr, @"([\[{])([^\[\]{}]+)[\]}]");
        if (!m.Success || m.Groups[2].Value.Contains(':')) return "";
        try
        {
            long a = d.Evaluate(m.Groups[2].Value);
            int width = m.Groups[1].Value == "{" || expr.StartsWith("((", StringComparison.Ordinal) ? 2 : 1;
            return width == 1 ? $"${a:X4}" : $"${a:X4}-${a + width - 1:X4}";
        }
        catch (Exception) { return ""; }
    }

    private static Func<long>[] Evaluators(MovieMachine m, Spec spec, bool port, out Func<long>? tick) => Evaluators(m, spec, port, out tick, out _);

    private static Func<long>[] Evaluators(MovieMachine m, Spec spec, bool port, out Func<long>? tick, out string[] watches)
    {
        var d = m.Host.Dbg ?? throw new NotSupportedException($"the {m.ConsoleKey} has no debugger here: its variables cannot be read by name");
        var list = new List<Func<long>>();
        foreach (var v in spec.Vars)
        {
            string e = Expr(port ? v.Port : v.Original);
            try { d.Evaluate(e); } catch (Exception x) { throw new ArgumentException($"variable {v.Name} on the {(port ? "port" : "original")} ({e}): {x.Message}"); }
            list.Add(() => d.Evaluate(e));
        }
        watches = spec.Vars.Select(v => v.Watch.Length > 0 ? v.Watch : WatchRange(d, Expr(port ? v.Port : v.Original))).ToArray();
        var r = port ? spec.P : spec.O;
        tick = null;
        if (r.Tick.Length > 0)
        {
            string te = Expr(r.Tick);
            try { d.Evaluate(te); } catch (Exception x) { throw new ArgumentException($"the {(port ? "port" : "original")}'s tick ({te}): {x.Message}"); }
            tick = () => d.Evaluate(te);
        }
        return list.ToArray();
    }

    private static PadButtons MapPad(PadButtons p, Spec spec)
    {
        if (spec.Buttons.Count == 0 || p == PadButtons.None) return p;
        PadButtons o = PadButtons.None;
        foreach (PadButtons b in Enum.GetValues<PadButtons>())
        {
            if (b == PadButtons.None || (p & b) == 0) continue;
            if (spec.Buttons.TryGetValue(b.ToString(), out string? to))
            {
                if (to.Length == 0 || to == "-") continue;   // a button the port has no use for
                o |= Enum.Parse<PadButtons>(to, true);
            }
            else o |= b;
        }
        return o;
    }

    private static TimeflowOptions OptionsFor(MovieMachine m, string source, string note, string project)
    {
        var o = new TimeflowOptions { Rom = m.RomFile, RomName = m.RomName, Cores = m.Cores, PowerOn = m.PowerOn, Ntsc = m.PowerOnNtsc, Source = source, Note = note, PowerOnStart = true };
        if (project.Length > 0) o.GameDir = TimeflowStore.ProjectDir(project);
        return o;
    }

    /// <summary>Records the original on the fly (an --original-rom run): a power-on timeline like 'timeflow record', its symbols kept.</summary>
    private static string RecordOriginal(CompareArgs a)
    {
        var input = TimeflowInput.Parse(a.Input, a.Input2, null);
        if (a.Frames <= 0) throw new ArgumentException("--frames N is needed to record the original");
        using var m = MovieMachine.Open(a.OriginalRom, a.OriginalConsole.Length > 0 ? a.OriginalConsole : null, a.OriginalCores, "fceux", false, true, a.OriginalSymbols);
        var rec = TimeflowRecorder.Start(TimeflowMachines.SourceFor(m), OptionsFor(m, "compare-original", "recorded by timeflow compare", a.OriginalProject));
        for (long f = 0; f < a.Frames && rec.Running; f++)
        {
            var (p1, p2) = input.At(f);
            rec.BeforeFrame(p1, p2);
            m.Frame(p1, p2);
            rec.AfterFrame();
            if (m.Crashed) break;
        }
        string dir = rec.Stop();
        if (a.OriginalSymbols.Count > 0) TimeflowHotpatching.KeepSymbols(TimeflowStore.GameDirOf(dir), m.RomSha256, a.OriginalSymbols);
        if (rec.StopReason != null) throw new InvalidDataException($"recording the original stopped: {rec.StopReason}");
        return dir;
    }

    /// <summary>The original: its timeline replayed from power-on on a new machine, checked frame by frame against the recording, the variables read.</summary>
    private static Side RunOriginal(string dir, Spec spec, List<string> symbols, long maxFrames)
    {
        using var rd = new TimelineReader(dir);
        var meta = rd.Meta;
        if (!meta.PowerOnStart || meta.StartFrame != 0) throw new ArgumentException($"timeline {meta.Id} does not start at power-on (frame {meta.StartFrame}): the port starts at power-on, so the original must too");
        if (symbols.Count == 0) symbols = TimeflowHotpatching.KeptSymbols(dir, meta.RomSha256);
        using var m = TimeflowMachines.OpenFor(dir, meta, true, symbols);
        var src = TimeflowMachines.SourceFor(m);
        var side = new Side { Label = "original", Console = meta.Console, Rom = meta.RomName, RomSha = meta.RomSha256, Timeline = meta.Id, TimelineDir = dir, Symbols = symbols, Fps = spec.O.Fps > 0 ? spec.O.Fps : FpsOf(m) };
        var ev = Evaluators(m, spec, false, out var tick, out side.Watches);
        long tb = 0, tp = 0;
        long end = maxFrames > 0 ? Math.Min(rd.EndFrame, maxFrames) : rd.EndFrame;
        for (long f = 0; f < end; f++)
        {
            var (p1, p2) = rd.Pads(f);
            m.Frame(p1, p2);
            var row = rd.Row(f);
            if (side.OffFrame == null && (src.PictureHash() != row.Picture || src.RamHash() != row.Ram))
            { side.OffFrame = f; side.OffWhat = src.RamHash() != row.Ram ? "the work RAM after the frame" : "the picture after the frame"; }
            side.Values.Add(ev.Select(e => e()).ToArray());
            side.Ticks.Add(TickOf(spec.O, side.Fps, f + 1, tick?.Invoke() ?? 0, ref tb, ref tp));
            if (m.Crashed) { side.Crash = m.CrashInfo; break; }
        }
        TimeflowStore.Touch(dir);
        return side;
    }

    /// <summary>The port: the original's pads (mapped by time and by button) on the port's ROM from power-on, recorded as a timeline, the variables read.</summary>
    private static Side RunPort(CompareArgs a, Spec spec, string origDir, Side orig)
    {
        using var rd = new TimelineReader(origDir);
        using var m = MovieMachine.Open(a.Port, a.PortConsole.Length > 0 ? a.PortConsole : null, a.PortCores, "fceux", false, true, a.PortSymbols);
        double fpsP = spec.P.Fps > 0 ? spec.P.Fps : FpsOf(m);
        bool byTime = spec.Input == "time" || spec.Input == "auto" && Math.Abs(fpsP - orig.Fps) > 0.01;
        long frames = byTime ? (long)Math.Floor(orig.Frames * fpsP / orig.Fps) : orig.Frames;
        if (spec.Input is "tick" or "update") frames += 600;   // room to catch up: the port may reach the original's last tick later
        var side = new Side { Label = "port", Console = m.ConsoleKey, Rom = m.RomName, RomSha = m.RomSha256, Symbols = a.PortSymbols, Fps = fpsP };
        var ev = Evaluators(m, spec, true, out var tick, out side.Watches);
        var rec = TimeflowRecorder.Start(TimeflowMachines.SourceFor(m), OptionsFor(m, "compare-port", $"timeflow compare: the pads of timeline {orig.Timeline} ({orig.Console} {orig.Rom})", a.PortProject));
        long tb = 0, tp = 0;
        // input "tick" (S3: the same game code on two CPUs that spend different frames loading): the pads go by frame, re-anchored at
        // every change of the game's tick (rate.tick): the port's frame g, s frames after its tick became T, plays the original's frame
        // s (+ tickLead) after the original's tick became T. Menus and stories (the tick still) keep their frame timing; a load that
        // takes the two machines different frames only shifts what follows by the difference.
        // input "update": every frame of the port plays the pad the original's game used at the same tick: the pad of the frame before the
        // original's frame that moved its tick past T (a pad is read in vblank and used by the next update; lag frames re-read it), shifted
        // by tickLead (the port's own latency). The tick must count every update that reads the pads (menus too).
        bool byUpdate = spec.Input == "update";
        var padFor = new Dictionary<long, long>();
        if (byUpdate)
            for (int k = 1; k < orig.Ticks.Count; k++)
                for (long t = (long)orig.Ticks[k - 1]; t < (long)orig.Ticks[k] && t - (long)orig.Ticks[k - 1] < 64; t++) padFor.TryAdd(t, k - 1);   // a catch-up frame runs several updates on one pad
        bool byTick = spec.Input == "tick" || byUpdate;
        if (byTick && (spec.O.Tick.Length == 0 || spec.P.Tick.Length == 0)) throw new ArgumentException("comparison.input tick needs rate.original.tick and rate.port.tick (the game's tick counters)");
        var firstAt = new Dictionary<long, long>();
        for (int k = 0; k < orig.Ticks.Count; k++) firstAt.TryAdd(k == 0 ? 0 : (long)orig.Ticks[k - 1], k);
        long curT = -1, anchor = 0;
        int leadVar = -1; long leadVal = 0;
        if (spec.TickLeadWhen.Length > 0)
        {
            var lm = Regex.Match(spec.TickLeadWhen, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(-?\d+)\s*$");
            if (!lm.Success || (leadVar = spec.Vars.FindIndex(v => v.Name == lm.Groups[1].Value)) < 0) throw new ArgumentException($"tickLeadWhen '{spec.TickLeadWhen}': var=value (a comparison variable)");
            leadVal = long.Parse(lm.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        for (long g = 0; g < frames && rec.Running; g++)
        {
            long f = byTime ? Math.Min(orig.Frames - 1, (long)Math.Floor(g * orig.Fps / fpsP)) : g;
            if (byTick)
            {
                long tNow = g == 0 ? 0 : (long)side.Ticks[(int)g - 1];
                if (tNow != curT) { curT = tNow; anchor = g; }
                long lead = leadVar < 0 || (g > 0 && side.Values[(int)g - 1][leadVar] == leadVal) ? spec.TickLead : 0;
                if (byUpdate) f = padFor.TryGetValue(tNow, out long ku) ? ku + lead : -1;
                else f = firstAt.TryGetValue(tNow, out long k0) ? k0 + (g - anchor) + lead : -1;
                if (f >= orig.Frames) f = -1;
            }
            var (p1, p2) = f >= 0 ? rd.Pads(f) : (PadButtons.None, PadButtons.None);
            p1 = MapPad(p1, spec); p2 = MapPad(p2, spec);
            rec.BeforeFrame(p1, p2);
            m.Frame(p1, p2);
            rec.AfterFrame();
            side.Values.Add(ev.Select(e => e()).ToArray());
            side.Ticks.Add(TickOf(spec.P, fpsP, g + 1, tick?.Invoke() ?? 0, ref tb, ref tp));
            if (m.Crashed) { side.Crash = m.CrashInfo; break; }
        }
        string dir = rec.Stop();
        side.TimelineDir = dir; side.Timeline = rec.Id;
        if (a.PortSymbols.Count > 0) TimeflowHotpatching.KeepSymbols(TimeflowStore.GameDirOf(dir), m.RomSha256, a.PortSymbols);
        if (rec.StopReason != null) side.Crash = (side.Crash.Length > 0 ? side.Crash + "; " : "") + "the port's recording stopped: " + rec.StopReason;
        return side;
    }

    /// <summary>A side's clock moved so that time 0 is its origin (rate.origin "var=value"); the origin frame, or null when there is none.</summary>
    private static long? ApplyOrigin(Side s, Spec spec, Rate r)
    {
        if (r.Origin.Length == 0) return null;
        var m = Regex.Match(r.Origin, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(-?\d+)\s*$");
        if (!m.Success) throw new ArgumentException($"rate origin '{r.Origin}': var=value (a comparison variable's name)");
        int vi = spec.Vars.FindIndex(v => v.Name == m.Groups[1].Value);
        if (vi < 0) throw new ArgumentException($"rate origin '{r.Origin}': no comparison variable {m.Groups[1].Value}");
        long want = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int f0 = s.Values.FindIndex(v => v[vi] == want);
        if (f0 < 0) return null;
        double t0 = s.Ticks[f0];
        for (int i = 0; i < s.Ticks.Count; i++) s.Ticks[i] -= t0;
        return f0;
    }

    private static List<Ev> Events(Side s, Spec spec)
    {
        var list = new List<Ev>();
        for (long f = Math.Max(1, spec.SettleFrames); f < s.Frames; f++)
            for (int i = 0; i < spec.Vars.Count; i++)
            {
                if (spec.Vars[i].Kind != "event") continue;
                long a = s.Values[(int)f - 1][i], b = s.Values[(int)f][i];
                if (a != b) list.Add(new Ev(i, a, b, f, s.Ticks[(int)f]));
            }
        return list;
    }

    private static JsonObject State(Side s, Spec spec, long frame)
    {
        var o = new JsonObject();
        if (frame < 0 || frame >= s.Frames) return o;
        for (int i = 0; i < spec.Vars.Count; i++) o[spec.Vars[i].Name] = s.Values[(int)frame][i];
        return o;
    }

    private static JsonObject At(Side s, Spec spec, long frame) => new()
    {
        ["frame"] = frame, ["tick"] = frame >= 0 && frame < s.Frames ? Math.Round(s.Ticks[(int)frame], 3) : null, ["timeline"] = s.Timeline, ["state"] = State(s, spec, frame),
    };

    /// <summary>The frame of a side whose time first reaches <paramref name="tick"/> (the last frame when it never does).</summary>
    private static long FrameAtTick(Side s, double tick)
    {
        int lo = 0, hi = s.Ticks.Count - 1;
        if (hi < 0) return -1;
        if (s.Ticks[hi] < tick) return hi;
        while (lo < hi) { int mid = (lo + hi) / 2; if (s.Ticks[mid] >= tick) hi = mid; else lo = mid + 1; }
        return lo;
    }

    private sealed class Drift { public string Kind = "", Var = "", Detail = ""; public int? Event; public long FrameO, FrameP; public double TickO; }

    private static Drift? Positions(Side o, Side p, Spec spec, long fo, long fp)
    {
        for (int i = 0; i < spec.Vars.Count; i++)
        {
            var v = spec.Vars[i];
            if (v.Kind != "position") continue;
            double a = v.Mapped(o.Values[(int)fo][i], false), b = v.Mapped(p.Values[(int)fp][i], true);
            if (Math.Abs(a - b) > v.Tolerance + 1e-9)
                return new Drift { Kind = "position", Var = v.Name, FrameO = fo, FrameP = fp, TickO = o.Ticks[(int)fo], Detail = $"{v.Name} is {a.ToString(CultureInfo.InvariantCulture)} on the original and {b.ToString(CultureInfo.InvariantCulture)} on the port: {Math.Abs(a - b).ToString(CultureInfo.InvariantCulture)} apart, the tolerance is {v.Tolerance.ToString(CultureInfo.InvariantCulture)}" };
        }
        return null;
    }

    private static Drift? FirstDrift(Side o, Side p, Spec spec, List<Ev> eo, List<Ev> ep, out int matched)
    {
        Drift? ev = null;
        matched = 0;
        int n = Math.Max(eo.Count, ep.Count);
        for (int k = 0; k < n && ev == null; k++)
        {
            if (k >= ep.Count)
            {
                var x = eo[k];
                // the port ran out of frames before the original's event: only a drift when the port's run reached that time
                if (p.Ticks.Count == 0 || p.Ticks[^1] + spec.TickTolerance < x.Tick) break;
                ev = new Drift { Kind = "missing", Event = k, Var = spec.Vars[x.Var].Name, FrameO = x.Frame, FrameP = FrameAtTick(p, x.Tick), TickO = x.Tick, Detail = $"the original's event #{k} ({spec.Vars[x.Var].Name} {x.From} -> {x.To} at tick {x.Tick:0.##}) never happens on the port" };
                break;
            }
            if (k >= eo.Count)
            {
                var y = ep[k];
                if (o.Ticks.Count == 0 || o.Ticks[^1] + spec.TickTolerance < y.Tick) break;
                ev = new Drift { Kind = "extra", Event = k, Var = spec.Vars[y.Var].Name, FrameO = FrameAtTick(o, y.Tick), FrameP = y.Frame, TickO = y.Tick, Detail = $"the port's event #{k} ({spec.Vars[y.Var].Name} {y.From} -> {y.To} at tick {y.Tick:0.##}) never happens on the original" };
                break;
            }
            var a = eo[k]; var b = ep[k];
            var va = spec.Vars[a.Var]; var vb = spec.Vars[b.Var];
            if (a.Var != b.Var || Math.Abs(va.Mapped(a.To, false) - vb.Mapped(b.To, true)) > va.Tolerance + 1e-9)
            {
                ev = new Drift { Kind = "different-event", Event = k, Var = va.Name, FrameO = a.Frame, FrameP = b.Frame, TickO = a.Tick, Detail = $"event #{k}: the original's {va.Name} {a.From} -> {a.To} (tick {a.Tick:0.##}), the port's {vb.Name} {b.From} -> {b.To} (tick {b.Tick:0.##})" };
                break;
            }
            if (Math.Abs(a.Tick - b.Tick) > spec.TickTolerance + 1e-9)
            {
                ev = new Drift { Kind = "time", Event = k, Var = va.Name, FrameO = a.Frame, FrameP = b.Frame, TickO = a.Tick, Detail = $"event #{k} ({va.Name} -> {a.To}) at tick {a.Tick:0.##} on the original and {b.Tick:0.##} on the port: {Math.Abs(a.Tick - b.Tick):0.##} ticks apart, the tolerance is {spec.TickTolerance}" };
                break;
            }
            var pd = Positions(o, p, spec, a.Frame, b.Frame);
            if (pd != null) { pd.Event = k; pd.Detail = $"at event #{k} ({va.Name} -> {a.To}): " + pd.Detail; ev = pd; break; }
            matched++;
        }
        Drift? sample = null;
        if (spec.SampleEveryTicks > 0 && o.Ticks.Count > 0 && p.Ticks.Count > 0)
        {
            double last = Math.Min(o.Ticks[^1], p.Ticks[^1]);
            for (double t = spec.SampleEveryTicks; t <= last && sample == null; t += spec.SampleEveryTicks)
            {
                long fo = FrameAtTick(o, t), fp = FrameAtTick(p, t);
                if (fo < spec.SettleFrames) continue;
                sample = Positions(o, p, spec, fo, fp);
                if (sample != null) { sample.Kind = "position"; sample.Detail = $"at tick {t:0.##} (every {spec.SampleEveryTicks} ticks): " + sample.Detail; }
            }
        }
        if (ev == null) return sample;
        if (sample == null) return ev;
        return sample.TickO < ev.TickO ? sample : ev;
    }

    public static JsonObject Compare(CompareArgs a)
    {
        var spec = Spec.Load(a.Map, a.Manifest);
        if (a.Port.Length == 0) throw new ArgumentException("--port ROM is needed");
        string origDir;
        if (a.Original.Length > 0) origDir = TimeflowStore.Find(a.Original) ?? throw new FileNotFoundException($"no timeline '{a.Original}' under {TimeflowStore.Root()}");
        else if (a.OriginalRom.Length > 0) origDir = RecordOriginal(a);
        else throw new ArgumentException("the original is needed: --original TIMELINE (a power-on recording) or --original-rom R with --input and --frames (recorded now)");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var o = RunOriginal(origDir, spec, a.OriginalSymbols, a.Original.Length > 0 ? a.Frames : 0);
        if (a.PortSymbols.Count == 0) throw new ArgumentException("--port-sym F is needed (the port's variables are read by name)");
        var p = RunPort(a, spec, origDir, o);
        long? originO = ApplyOrigin(o, spec, spec.O), originP = ApplyOrigin(p, spec, spec.P);
        var eo = Events(o, spec); var ep = Events(p, spec);
        var d = FirstDrift(o, p, spec, eo, ep, out int matched);
        var r = new JsonObject
        {
            ["original"] = new JsonObject { ["timeline"] = o.Timeline, ["console"] = o.Console, ["rom"] = o.Rom, ["romSha256"] = o.RomSha, ["frames"] = o.Frames, ["fps"] = Math.Round(o.Fps, 4),
                                            ["ticks"] = o.Ticks.Count > 0 ? Math.Round(o.Ticks[^1], 2) : 0, ["events"] = eo.Count, ["replayInSync"] = o.OffFrame == null,
                                            ["replayOff"] = o.OffFrame != null ? $"frame {o.OffFrame}: {o.OffWhat}" : null, ["crash"] = o.Crash.Length > 0 ? o.Crash : null },
            ["port"] = new JsonObject { ["timeline"] = p.Timeline, ["console"] = p.Console, ["rom"] = p.Rom, ["romSha256"] = p.RomSha, ["frames"] = p.Frames, ["fps"] = Math.Round(p.Fps, 4),
                                        ["ticks"] = p.Ticks.Count > 0 ? Math.Round(p.Ticks[^1], 2) : 0, ["events"] = ep.Count, ["crash"] = p.Crash.Length > 0 ? p.Crash : null },
            ["mapping"] = new JsonObject
            {
                ["variables"] = new JsonArray(spec.Vars.Select(v => (JsonNode)$"{v.Name} ({v.Kind}{(v.Tolerance > 0 ? $", tolerance {v.Tolerance.ToString(CultureInfo.InvariantCulture)}" : "")}): {Expr(v.Original)} / {Expr(v.Port)}").ToArray()),
                ["tickTolerance"] = spec.TickTolerance, ["input"] = spec.Input,
                ["originOriginal"] = spec.O.Origin.Length > 0 ? $"{spec.O.Origin}: frame {originO?.ToString(CultureInfo.InvariantCulture) ?? "never"}" : null,
                ["originPort"] = spec.P.Origin.Length > 0 ? $"{spec.P.Origin}: frame {originP?.ToString(CultureInfo.InvariantCulture) ?? "never"}" : null, ["sampleEveryTicks"] = spec.SampleEveryTicks,
                ["timeOriginal"] = spec.O.Tick.Length > 0 ? $"the game's tick {Expr(spec.O.Tick)}" : $"{(spec.O.TickHz > 0 ? spec.O.TickHz : o.Fps):0.####} ticks/s at {o.Fps:0.####} fps",
                ["timePort"] = spec.P.Tick.Length > 0 ? $"the game's tick {Expr(spec.P.Tick)}" : $"{(spec.P.TickHz > 0 ? spec.P.TickHz : p.Fps):0.####} ticks/s at {p.Fps:0.####} fps",
            },
            ["eventsMatched"] = matched,
            ["eventsOriginal"] = new JsonArray(eo.Take(40).Select(e => (JsonNode)$"#{eo.IndexOf(e)} f{e.Frame} t{e.Tick:0.##} {spec.Vars[e.Var].Name} {e.From}->{e.To}").ToArray()),
            ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2),
        };
        if (o.OffFrame != null) r["warning"] = $"the original's replay left its recording at frame {o.OffFrame} ({o.OffWhat}): its values after that are not the recorded session's";
        if (d == null) { r["drift"] = null; r["verdict"] = $"no drift: {matched} event(s) in the same order within the tolerances" + (spec.SampleEveryTicks > 0 ? $", positions within them every {spec.SampleEveryTicks} ticks" : ""); return r; }
        var dj = new JsonObject
        {
            ["kind"] = d.Kind, ["variable"] = d.Var, ["event"] = d.Event, ["detail"] = d.Detail,
            ["original"] = At(o, spec, d.FrameO), ["port"] = At(p, spec, d.FrameP),
        };
        r["drift"] = dj;
        r["verdict"] = $"DRIFT ({d.Kind}) after {matched} matching event(s): {d.Detail}";
        if (a.Open) dj["opened"] = OpenBoth(o, p, spec, d, a.Window);
        return r;
    }

    /// <summary>Both sides opened at the drift: T3 profiles of the span before it on each side's timeline, watching the drifting variable.</summary>
    private static JsonObject OpenBoth(Side o, Side p, Spec spec, Drift d, int window)
    {
        var v = spec.Vars.FirstOrDefault(x => x.Name == d.Var);
        var j = new JsonObject();
        foreach (var (s, f, port) in new[] { (o, d.FrameO, false), (p, d.FrameP, true) })
        {
            long from = Math.Max(0, f - window), to = Math.Max(0, Math.Min(f, s.Frames - 1));
            int vi = v != null ? spec.Vars.IndexOf(v) : -1;
            string watch = vi >= 0 && vi < s.Watches.Length && s.Watches[vi].Length > 0 ? s.Watches[vi] : v != null ? WatchOf(v, port) : "";
            var queries = new List<string>();
            if (watch.Length > 0) queries.Add($"writes {watch} limit 12");
            queries.Add("summary");
            var side = new JsonObject
            {
                ["timeline"] = s.Timeline, ["from"] = from, ["to"] = to, ["watch"] = watch.Length > 0 ? watch : null,
                ["resume"] = $"brokendev.ps1 timeflow resume {s.Timeline} -Frame {from}   (or MCP timeflow_jump / timeflow_profile)",
            };
            try
            {
                var pr = TimeflowProfiling.Run(new ProfileRequest { Timeline = s.TimelineDir, From = from, To = to, Queries = queries, Symbols = s.Symbols, StopWhenOff = false });
                side["profile"] = new JsonObject { ["inSync"] = pr["inSync"]?.DeepClone(), ["framesProfiled"] = pr["framesProfiled"]?.DeepClone(), ["queries"] = pr["queries"]?.DeepClone() };
            }
            catch (Exception e) when (e is not OutOfMemoryException) { side["profileError"] = e.Message; }
            j[s.Label] = side;
        }
        return j;
    }

    private static string CompareText(JsonObject r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"original {r["original"]!["console"]} {r["original"]!["rom"]} (timeline {r["original"]!["timeline"]}, {r["original"]!["frames"]} frames, {r["original"]!["events"]} events)");
        sb.AppendLine($"port     {r["port"]!["console"]} {r["port"]!["rom"]} (timeline {r["port"]!["timeline"]}, {r["port"]!["frames"]} frames, {r["port"]!["events"]} events)");
        if (r["warning"] is { } w) sb.AppendLine("WARNING  " + w);
        sb.AppendLine(r["verdict"]!.ToString());
        if (r["drift"] is JsonObject d)
        {
            sb.AppendLine($"  original at frame {d["original"]!["frame"]} (tick {d["original"]!["tick"]}): {d["original"]!["state"]!.ToJsonString()}");
            sb.AppendLine($"  port     at frame {d["port"]!["frame"]} (tick {d["port"]!["tick"]}): {d["port"]!["state"]!.ToJsonString()}");
            if (d["opened"] is JsonObject op)
                foreach (var kv in op)
                    sb.AppendLine($"  opened {kv.Key}: timeline {kv.Value!["timeline"]} frames {kv.Value["from"]}-{kv.Value["to"]}" + (kv.Value["profileError"] is { } pe ? $" (profile failed: {pe})" : $", profiled ({kv.Value["profile"]?["framesProfiled"]} frames), watching {kv.Value["watch"]}"));
        }
        return sb.ToString().TrimEnd();
    }

    // ================================================================================================ the re-check after a rebuild

    public static JsonObject Recheck(string timeline, string project, string gameDir, string newRom, List<string> newSyms, List<string> oldSyms, string map, string manifest)
    {
        if (newRom.Length == 0 || !File.Exists(newRom)) throw new ArgumentException("--rom NEW (the rebuilt ROM) is needed");
        Spec? spec = map.Length > 0 || manifest.Length > 0 ? TryLoad(map, manifest) : null;
        var dirs = new List<string>();
        if (timeline.Length > 0) dirs.Add(TimeflowStore.Find(timeline) ?? throw new FileNotFoundException($"no timeline '{timeline}' under {TimeflowStore.Root()}"));
        else
        {
            string g = gameDir.Length > 0 ? Path.GetFullPath(gameDir) : project.Length > 0 ? TimeflowStore.ProjectDir(project) : throw new ArgumentException("--timeline, --project or --game-dir is needed");
            // the recorded sessions: power-on timelines that are not branches (a hotpatch branch is a what-if, not a session)
            dirs.AddRange(TimeflowStore.List(g).Where(t => t.Meta.PowerOnStart && t.Meta.StartFrame == 0 && t.Meta.ParentId == null && t.Meta.Frames > 0).Select(t => t.Dir));
        }
        if (dirs.Count == 0) throw new FileNotFoundException("no recorded session to re-check (record one with timeflow record or timeflow compare)");
        byte[] nb = File.ReadAllBytes(newRom);
        var arr = new JsonArray();
        bool any = false;
        foreach (string dir in dirs)
        {
            var one = RecheckOne(dir, newRom, newSyms, oldSyms, spec);
            if (one["changed"]?.GetValue<bool>() == true) any = true;
            arr.Add(one);
        }
        return new JsonObject
        {
            ["rom"] = newRom, ["romSha256"] = TimeflowStore.Sha256(nb), ["sessions"] = arr, ["changed"] = any,
            ["verdict"] = any ? $"the rebuild changed {arr.Count(x => x!["changed"]!.GetValue<bool>())} of {arr.Count} recorded session(s): see each one's first altered frame"
                              : $"the rebuild changed none of the {arr.Count} recorded session(s): every frame's picture and work RAM as recorded",
        };
    }

    private static Spec? TryLoad(string map, string manifest)
    {
        try { return Spec.Load(map, manifest); } catch (ArgumentException) when (manifest.Length > 0 && map.Length == 0) { return null; }
    }

    private static JsonObject RecheckOne(string dir, string newRom, List<string> newSyms, List<string> oldSyms, Spec? spec)
    {
        using var rd = new TimelineReader(dir);
        var meta = rd.Meta;
        if (oldSyms.Count == 0) oldSyms = TimeflowHotpatching.KeptSymbols(dir, meta.RomSha256);
        using var nm = MovieMachine.Open(newRom, meta.Console, meta.Cores, meta.Console == "nes" ? meta.PowerOn : "fceux", meta.Ntsc, true, newSyms);
        var src = TimeflowMachines.SourceFor(nm);
        var o = new JsonObject { ["timeline"] = meta.Id, ["rom"] = meta.RomName, ["recordedRomSha256"] = meta.RomSha256, ["newRomSha256"] = nm.RomSha256, ["sameRom"] = nm.RomSha256 == meta.RomSha256, ["frames"] = rd.Frames };
        if (!meta.PowerOnStart || meta.StartFrame != 0) { o["changed"] = false; o["skipped"] = "not a power-on recording"; return o; }
        long off = -1; string what = "";
        for (long f = 0; f < rd.EndFrame; f++)
        {
            var (p1, p2) = rd.Pads(f);
            nm.Frame(p1, p2);
            var row = rd.Row(f);
            ulong pic = src.PictureHash(), ram = src.RamHash();
            if (pic != row.Picture || ram != row.Ram)
            {
                off = f;
                what = (pic != row.Picture ? "the picture" : "") + (pic != row.Picture && ram != row.Ram ? " and " : "") + (ram != row.Ram ? "the work RAM" : "");
                break;
            }
            if (nm.Crashed) { off = f; what = "the new build crashed: " + nm.CrashInfo; break; }
        }
        o["changed"] = off >= 0;
        if (off < 0) { o["verdict"] = $"unchanged: all {rd.Frames} frames as recorded"; return o; }
        o["firstAlteredFrame"] = off;
        o["what"] = what;
        o["verdict"] = $"the first frame the change altered is {off}: {what} after the frame differs from the recording";
        if (spec != null)
        {
            // the mapped variables at that frame on the recorded build (its machine replayed to the frame) and on the new one, by each build's symbols
            var vars = new JsonObject();
            try
            {
                var en = Evaluators(nm, spec, true, out _);
                using var om = TimeflowMachines.OpenFor(dir, meta, true, oldSyms);
                for (long f = 0; f <= off; f++) { var (p1, p2) = rd.Pads(f); om.Frame(p1, p2); }
                var eo = Evaluators(om, spec, true, out _);
                for (int i = 0; i < spec.Vars.Count; i++)
                {
                    long a = eo[i](), b = en[i]();
                    vars[spec.Vars[i].Name] = new JsonObject { ["recorded"] = a, ["new"] = b, ["differs"] = a != b };
                }
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException) { vars["error"] = e.Message; }
            o["variables"] = vars;
        }
        return o;
    }

    private static string RecheckText(JsonObject r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(r["verdict"]!.ToString());
        foreach (var s in r["sessions"]!.AsArray())
        {
            sb.Append($"  {s!["timeline"]} ({s["frames"]} frames{(s["sameRom"]!.GetValue<bool>() ? ", the same ROM" : "")}): {s["verdict"] ?? s["skipped"]}");
            if (s["variables"] is JsonObject v) sb.Append("  " + string.Join(", ", v.Where(kv => kv.Value is JsonObject jo && jo["differs"]!.GetValue<bool>()).Select(kv => $"{kv.Key} {kv.Value!["recorded"]} -> {kv.Value["new"]}")));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
