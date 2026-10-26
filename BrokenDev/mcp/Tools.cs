using System.Text.Json.Nodes;

namespace BrokenNes.Mcp;

/// <summary>One MCP tool: its name, what it does (the agent reads this), its input schema and its handler.</summary>
internal sealed record ToolDef(string Name, string Description, JsonObject Schema, Func<Args, ToolOut> Run, bool ReadOnly);

/// <summary>
/// The BrokenNes MCP tools (docs/projects/brokendev-debugging-plan.md section 8): sessions, running, input, memory, the debugger, the
/// picture, sound, states, the checks and the profiler, BrokenDev. Each is a thin call onto <see cref="Session"/>.
/// </summary>
internal static class Tools
{
    private const string SessionDesc = "the session id (from open_rom, open_project or attach_live)";
    private const string CpuDesc = "the CPU: main (default) or spc (the SNES sound CPU, SPC700)";
    private const string SpecDesc = "an address on a CPU: $0010, $8000-$80FF, 7E:0010 (bank:address), wram:$10, vram:$2000, spc:$0200 (cpu spc), a symbol (player_hp: its whole size), symbol+1. A CPU address matches every mirror of what it reaches.";

    private static JsonObject P(string type, string desc, JsonNode? extra = null)
    {
        var o = new JsonObject { ["description"] = desc };
        if (type == "string[]") { o["type"] = "array"; o["items"] = new JsonObject { ["type"] = "string" }; }
        else if (type == "bytes") { o["description"] = desc + " (an array of numbers, or a hex string \"A9 00 8D\")"; }
        else if (type == "number|string") { o["type"] = new JsonArray("integer", "string"); }
        else o["type"] = type;
        if (extra is JsonObject e) foreach (var kv in e) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    private static JsonObject Schema(params (string name, JsonObject prop, bool required)[] ps)
    {
        var props = new JsonObject();
        var req = new JsonArray();
        foreach (var (n, p, r) in ps) { props[n] = p; if (r) req.Add(n); }
        var s = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (req.Count > 0) s["required"] = req;
        return s;
    }

    private static (string, JsonObject, bool) S() => ("session", P("string", SessionDesc), true);
    private static (string, JsonObject, bool) C() => ("cpu", P("string", CpuDesc), false);

    private static T On<T>(Args a, Func<Session, T> f)
    {
        var s = Sessions.Get(a.Req("session"));
        lock (s.Gate) return f(s);
    }

    private static JsonObject Enum(params string[] v) => new() { ["enum"] = J.Strs(v) };

    private static NesEmulator.Debugging.Movies.Movie LoadMovie(string path)
    {
        if (!File.Exists(path)) throw new ToolError($"no movie at '{path}'");
        try { return NesEmulator.Debugging.Movies.Movie.Load(path); }
        catch (FormatException e) { throw new ToolError(e.Message); }
    }

    /// <summary>
    /// The bugs folders to look in (D6): a folder given; a project's (its build record's bugs folder, else its live workspace's, and the default
    /// folder's bundles of that project or its ROM); or, with neither, the default folder. Each with why, and a filter for its bundles.
    /// </summary>
    private static List<(string dir, string why, Func<JsonObject, bool> filter)> BugDirs(string? project, string? folder)
    {
        var l = new List<(string, string, Func<JsonObject, bool>)>();
        string dflt = NesEmulator.Debugging.Capture.BugPlaces.DefaultFolder();
        if (folder is { Length: > 0 }) { l.Add((Path.GetFullPath(folder), "the folder given", _ => true)); return l; }
        if (project is not { Length: > 0 }) { l.Add((dflt, "the default folder", _ => true)); return l; }
        if (!BrokenDevBridge.ProjectIds().Contains(project)) throw new ToolError($"unknown project '{project}' (known: {string.Join(", ", BrokenDevBridge.ProjectIds())})");
        string rec = Path.Combine(BrokenDevBridge.Home, "out", project, "build.json");
        string? sha = null;
        if (File.Exists(rec))
        {
            var b = JsonNode.Parse(File.ReadAllText(rec))!;
            sha = (b["outputs"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(o => (string?)o["id"] == "rom")?["sha256"]?.GetValue<string>();
            string? bugs = b["bugs"]?.GetValue<string>();
            string? ws = b["workspace"]?["kind"]?.GetValue<string>() is "live" or "folder" ? b["workspace"]?["path"]?.GetValue<string>() : null;
            if (bugs is { Length: > 0 }) l.Add((bugs, $"project {project}'s game workspace (its build record)", _ => true));
            else if (ws is { Length: > 0 }) l.Add((Path.Combine(ws, "bugs"), $"project {project}'s live workspace", _ => true));
        }
        l.Add((dflt, $"the default folder: the bundles of project {project} or of its built ROM", x => (string?)x["project"] == project || (sha != null && ((string?)x["romSha256"] ?? "").Equals(sha, StringComparison.OrdinalIgnoreCase))));
        return l;
    }

    /// <summary>A bug by path, or by id in the folders <see cref="BugDirs"/> gives (refused when none or several have it).</summary>
    private static string ResolveBug(string bug, List<(string dir, string why, Func<JsonObject, bool> filter)> dirs)
    {
        if (Directory.Exists(bug) || File.Exists(bug)) return bug;
        var hits = dirs.SelectMany(d => new[] { Path.Combine(d.dir, bug), Path.Combine(d.dir, bug + ".zip") }).Where(p => Directory.Exists(p) || File.Exists(p)).Distinct().ToList();
        if (hits.Count == 1) return hits[0];
        if (hits.Count > 1) throw new ToolError($"bug '{bug}' is in more than one folder: {string.Join("; ", hits)} (give its path)");
        throw new ToolError($"no bug '{bug}' (a bundle folder or zip, or an id in: {string.Join("; ", dirs.Select(d => d.dir))}); list them with action list");
    }

    /// <summary>The Workshop's movie code says no with these; a tool says it as a refusal.</summary>
    private static T Wrap<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or NotSupportedException or FileNotFoundException or InvalidOperationException or FormatException) { throw new ToolError(e.Message); }
    }

    public static List<ToolDef> All()
    {
        var t = new List<ToolDef>();
        void Add(string name, string desc, JsonObject schema, Func<Args, ToolOut> run, bool ro = false) => t.Add(new ToolDef(name, desc, schema, run, ro));

        // ------------------------------------------------------------------------------------------------ sessions
        Add("list_sessions", "The open sessions (headless and live), with what each runs.", Schema(),
            _ => new JsonObject { ["sessions"] = J.Arr(Sessions.All.Select(s => { lock (s.Gate) return (JsonNode)s.Describe(); })) }, ro: true);

        Add("open_rom",
            "Opens a ROM in a new HEADLESS session: BrokenNes's own emulator inside this server, invisible, deterministic, as many at once as you like (no Mesen, no lock). " +
            "Any console: NES (default cores CPU/PPU/APU FIX, as the desktop app), SNES, WideMess (.wms), Game Boy / Color, Master System, Game Gear, Genesis. The debugger covers the NES, SNES (65816 + SPC700), WideMess and Game Boy. " +
            "Symbol files (.mlb NESFab/Mesen, WLA-DX .sym, GBDK .sym/.noi) give names to addresses. checks:true turns the D4 hardware checks and the profiler on from power-on (needed for 'RAM read before written').",
            Schema(("path", P("string", "the ROM file (.nes .sfc .smc .wms .gb .gbc .sms .gg .md .bin, or a .zip)"), true),
                   ("console", P("string", "force a console: nes, snes, wms, gb, gbc, sms, gg, md (default: detected from the ROM)"), false),
                   ("cpu", P("string", "CPU core id (default: the console's default, NES FIX)"), false), ("ppu", P("string", "PPU core id"), false), ("apu", P("string", "APU core id"), false),
                   ("symbols", P("string[]", "symbol files to load"), false),
                   ("checks", P("boolean", "run the hardware checks and the profiler from power-on (default false)"), false),
                   ("checks_off", P("string[]", "rules to switch off (e.g. nes.lag-frame)"), false),
                   ("frame_wait", P("string[]", "symbols or addresses where the game waits for the next frame (the lag-frame rule)"), false),
                   ("call_stack", P("boolean", "track the call stack and the trace ring from the first instruction (default true; false runs faster)"), false)),
            a =>
            {
                var s = HeadlessSession.Open(Sessions.NewId("h"), a.Req("path"), a.Str("console"), a.Str("cpu") ?? "", a.Str("ppu") ?? "", a.Str("apu") ?? "", a.List("symbols"),
                    a.Bool("call_stack", true), a.Bool("checks", false), a.List("checks_off"), a.List("frame_wait"));
                Sessions.Add(s);
                return s.Describe();
            });

        Add("open_project",
            "Opens a BrokenDev project's last build in a new HEADLESS session: its ROM and symbol files from BROKENDEV_HOME\\out\\<project>, the console and the cores its verify uses, " +
            "the frame-wait symbols of its checks section. Projects: brokendev with verb list. (Build it first with the brokendev tool if it has no build.)",
            Schema(("project", P("string", "the project id (vrun-nes, supervrun, smb1-nes, widemess-sample, ...)"), true),
                   ("checks", P("boolean", "the hardware checks and the profiler from power-on (default false)"), false),
                   ("cpu", P("string", "override the CPU core"), false), ("ppu", P("string", "override the PPU core"), false), ("apu", P("string", "override the APU core"), false),
                   ("call_stack", P("boolean", "track the call stack from the first instruction (default true)"), false)),
            a =>
            {
                var p = BrokenDevBridge.Built(a.Req("project"));
                var s = HeadlessSession.Open(Sessions.NewId("h"), p.Rom, p.Console.Length > 0 ? p.Console : null, a.Str("cpu") ?? p.Cpu, a.Str("ppu") ?? p.Ppu, a.Str("apu") ?? p.Apu, p.Symbols,
                    a.Bool("call_stack", true), a.Bool("checks", false), new List<string>(), p.FrameWait);
                Sessions.Add(s);
                var j = s.Describe(); j["project"] = p.Id; j["built"] = p.BuiltAt; j["symbolFiles"] = J.Strs(p.Symbols);
                return j;
            });

        Add("list_instances", "The BrokenNes desktop apps running on this machine (their discovery files), for attach_live. Reading only.", Schema(),
            _ => new JsonObject
            {
                ["instances"] = J.Arr(LiveSession.Instances().Select(i => (JsonNode)new JsonObject { ["pid"] = i.Pid, ["httpPort"] = i.HttpPort, ["alive"] = i.Alive, ["exe"] = i.Exe, ["started"] = i.Started })),
                ["folder"] = LiveSession.InstancesDir,
            }, ro: true);

        Add("attach_live",
            "Attaches a LIVE session to the user's running BrokenNes desktop app by PID (list_instances). It opens READ-ONLY: registers, memory, the call stack, the trace, breakpoints and hits, disassembly, " +
            "expressions and the screenshot can be read at any time. control:true is needed for pausing, stepping, breakpoints, writing memory or registers, pressing buttons and loading the quick state: " +
            "set control:true ONLY when the user asked you in chat to control their game; the app must also have \"enableDebuggerControl\": true in its config (the user's choice; never edit it). " +
            "A live session never saves, never touches progression and never stops the process.",
            Schema(("pid", P("integer", "the desktop app's process id"), true),
                   ("control", P("boolean", "open for control (pause, step, breakpoints, writes, input, state load). Only when the user asked in chat. Default false."), false)),
            a =>
            {
                int pid = (int)(a.Long("pid") ?? throw new ToolError("pid is required"));
                var s = LiveSession.Attach(Sessions.NewId($"live-{pid}-"), pid, a.Bool("control", false));
                Sessions.Add(s);
                return s.Describe();
            });

        Add("close_session", "Closes a session. Headless: the emulator is dropped. Live: the app is left running (a control session lets its game go); the process is never stopped.",
            Schema(S()),
            a =>
            {
                string id = a.Req("session");
                if (!Sessions.Remove(id, out var s) || s == null) throw new ToolError($"no session '{id}'");
                lock (s.Gate) s.Close();
                return new JsonObject { ["closed"] = id, ["kind"] = s.Kind };
            });

        Add("status", "A session's state: console, frame, whether it is stopped at a break (and where), held buttons, watch values, check findings, saved states.", Schema(S()),
            a => On(a, s => s.Status()), ro: true);

        // ------------------------------------------------------------------------------------------------ running
        Add("run", "Runs frames (default 60) or until a breakpoint or watch breaks; the result says which, with the break's place. Optional input: a scripted pad for these frames (\"0:Start,10:,60:Right+A\": frames from now, an empty list releases). Live: control.",
            Schema(S(), ("frames", P("integer", "frames to run (default 60)"), false), ("input", P("string", "a pad script: frame:Button+Button,... (frames from now)"), false)),
            a => On(a, s => s.Run(a.LongOr("frames", 60), a.Str("input") is { Length: > 0 } sc ? HeadlessSession.ParseScript(sc) : null)));

        Add("run_until",
            "Runs until: a frame (frame), a scanline (scanline), an address being executed / read / written (spec + kind, with an optional condition tested at each access), or a condition alone (tested at the end of each frame). " +
            "Gives up after max_frames. Conditions: registers (A, X, PC...), [addr] byte, {addr} word, [symbol], value / address (the access), frame, scanline, C operators. Live: control.",
            Schema(S(), ("spec", P("string", SpecDesc), false), ("kind", P("string", "exec (default), read, write or rw", Enum("exec", "read", "write", "rw")), false),
                   ("condition", P("string", "e.g. A == 3 && [player_hp] < 2"), false), ("frame", P("integer", "run until this frame starts"), false), ("scanline", P("integer", "run until this scanline"), false),
                   ("max_frames", P("integer", "give up after this many frames (default 600)"), false), C()),
            a => On(a, s => s.RunUntil(a.Str("spec"), a.Str("kind") ?? "exec", a.Str("condition"), a.Long("frame"), a.Long("scanline") is long l ? (int)l : null, a.LongOr("max_frames", 600), a.Str("cpu") ?? "main")));

        Add("pause", "Stops at the next instruction, to step from there. (Headless sessions are always still between calls.) Live: control.", Schema(S()), a => On(a, s => s.Pause()));
        Add("continue", "Lets a stopped game go. Live: control (the user's game runs on). Headless: runs 600 frames or until a break.", Schema(S()), a => On(a, s => s.Continue()));
        Add("reset", "Presses the console's reset button (headless only; a live session never resets the user's game). Open the ROM again for a power-on.", Schema(S()), a => On(a, s => s.Reset()));

        // ------------------------------------------------------------------------------------------------ input
        Add("input",
            "The pad. mode press: hold buttons for N frames (default 6) and run them, then release; hold: keep them held until changed; release: let go; script: a pad script (\"0:Start,10:,60:Right+A,120:\", frames from now) run to its end (or run_frames). " +
            "Buttons: A B X Y L R C Z Mode Start Select Up Down Left Right. Live: control (press / hold / release only).",
            Schema(S(), ("mode", P("string", "press, hold, release or script", Enum("press", "hold", "release", "script")), true), ("buttons", P("string[]", "the buttons"), false),
                   ("frames", P("integer", "press: frames to hold (default 6)"), false), ("player", P("integer", "1 (default) or 2"), false),
                   ("script", P("string", "mode script: frame:Button+Button,..."), false), ("run_frames", P("integer", "mode script: frames to run (default: to the last step)"), false)),
            a => On(a, s => s.Input(a.Req("mode").ToLowerInvariant(), a.List("buttons").ToArray(), a.Int("frames", 6), a.Int("player", 1) - 1,
                a.Str("script") is { Length: > 0 } sc ? HeadlessSession.ParseScript(sc) : null, a.Long("run_frames"))));

        // ------------------------------------------------------------------------------------------------ memory
        Add("list_domains", "The machine's memories (domains: RAM, WRAM, VRAM, CGRAM, OAM, ARAM, SRAM, ROM, IO, BUS, ...) with sizes, and each CPU's address spaces for specs.", Schema(S()),
            a => On(a, s => s.Domains()), ro: true);

        Add("read_memory",
            "Reads memory without side effects: by spec on a CPU (" + SpecDesc + ") or by domain + address. Returns hex, bytes and the little-endian value for 8 bytes or fewer. Length defaults to the symbol's size (spec) or 16 (domain); at most 4096.",
            Schema(S(), ("spec", P("string", "address or symbol on a CPU"), false), ("domain", P("string", "a domain id (list_domains)"), false), ("address", P("number|string", "the domain offset ($1F00, 0x1F00, 7936)"), false),
                   ("length", P("integer", "bytes"), false), C()),
            a => On(a, s => s.ReadMemory(a.Str("spec"), a.Str("domain"), a.Long("address"), a.Long("length") is long l ? (int)l : null, a.Str("cpu") ?? "main")), ro: true);

        Add("write_memory", "Writes memory by spec or by domain + address (headless: any session; live: control, by domain + address). Reports the bytes before and after.",
            Schema(S(), ("spec", P("string", "address or symbol on a CPU"), false), ("domain", P("string", "a domain id"), false), ("address", P("number|string", "the domain offset"), false),
                   ("data", P("bytes", "the bytes to write"), true), C()),
            a => On(a, s => s.WriteMemory(a.Str("spec"), a.Str("domain"), a.Long("address"), a.Bytes("data") ?? Array.Empty<byte>(), a.Str("cpu") ?? "main")));

        Add("search_memory",
            "A RAM search over a domain (headless): op eq / ne / gt / lt with a value, or changed / unchanged / increased / decreased against the previous search. new:true starts over from every address. " +
            "Run frames (or press buttons) between searches to narrow the candidates (e.g. find the lives counter: eq 3, lose a life, eq 2).",
            Schema(S(), ("domain", P("string", "a domain id (RAM, WRAM, HRAM, ...)"), true), ("op", P("string", "eq, ne, gt, lt, changed, unchanged, increased, decreased", Enum("eq", "ne", "gt", "lt", "changed", "unchanged", "increased", "decreased")), true),
                   ("value", P("number|string", "the value for eq / ne / gt / lt"), false), ("width", P("integer", "1 (default), 2 or 4 bytes, little-endian"), false),
                   ("new", P("boolean", "start a new search"), false), ("show", P("integer", "candidates to list (default 20)"), false)),
            a => On(a, s => s.SearchMemory(a.Req("domain"), a.Req("op").ToLowerInvariant(), a.Long("value"), a.Int("width", 1), a.Bool("new", false), a.Int("show", 20))), ro: true);

        Add("symbols", "The session's symbols: load a file (live: control), find one by name (its place, size and bytes), the label at an address, or list names containing some text (list: \"*\" for all).",
            Schema(S(), ("load", P("string", "a symbol file to load (.mlb, .sym, .noi)"), false), ("find", P("string", "a symbol name"), false), ("address", P("number|string", "a CPU address: its label"), false),
                   ("list", P("string", "list symbols whose name contains this (\"*\": all)"), false), ("limit", P("integer", "most to list (default 100)"), false), C()),
            a => On(a, s => s.Symbols(a.Str("load"), a.Str("find"), a.Long("address"), a.Str("list"), a.Str("cpu") ?? "main", a.Int("limit", 100))));

        // ------------------------------------------------------------------------------------------------ the debugger
        Add("breakpoint_add",
            "Adds a breakpoint (exec), a watch (read / write / rw: every mirror, DMA and HDMA included) or a tracepoint (tracepoint:true logs and keeps running; log text with {expressions}). " +
            "Optional condition and hit count (\"3\": from the 3rd on, \"==3\": only the 3rd, \"%3\": every 3rd). Live: control.",
            Schema(S(), ("spec", P("string", SpecDesc), true), ("kind", P("string", "exec (default), read, write or rw", Enum("exec", "read", "write", "rw")), false),
                   ("condition", P("string", "e.g. value == 0 && frame > 120"), false), ("hits", P("string", "3, ==3 or %3"), false),
                   ("tracepoint", P("boolean", "log and keep running"), false), ("log", P("string", "a tracepoint's text: \"hp={[player_hp]} x={X}\""), false), C()),
            a => On(a, s => s.AddBreakpoint(a.Req("spec"), a.Str("kind") ?? "exec", a.Str("condition"), a.Str("hits"), a.Bool("tracepoint", false), a.Str("log"), a.Str("cpu") ?? "main")));

        Add("breakpoint_list", "The breakpoints with their hit counts, and the last hits (tracepoint lines too).", Schema(S(), ("hits", P("integer", "hits to list (default 20)"), false)),
            a => On(a, s => s.ListBreakpoints(a.Int("hits", 20))), ro: true);

        Add("breakpoint_remove", "Removes one breakpoint (id) or all. Live: control.", Schema(S(), ("id", P("integer", "the breakpoint id"), false), ("all", P("boolean", "remove every breakpoint"), false)),
            a => On(a, s => s.RemoveBreakpoint(a.Long("id") is long i ? (int)i : null, a.Bool("all", false))));

        Add("watch", "Watch expressions (headless): their values come back with every run, step and status result. action add (expr), remove (index or expr), clear, list.",
            Schema(S(), ("action", P("string", "add, remove, clear or list", Enum("add", "remove", "clear", "list")), true), ("expr", P("string", "[player_hp], {pointer}, A + X, ..."), false), ("index", P("integer", "remove: the watch's index"), false)),
            a => On(a, s => s.Watch(a.Req("action").ToLowerInvariant(), a.Str("expr"), a.Long("index") is long i ? (int)i : null)));

        Add("step",
            "Steps: into (one instruction; count for more), over (a call runs to its return), out (to the return of the current function), scanline, frame; or runs to an address (to), a frame (frame) or a scanline. Live: control.",
            Schema(S(), ("mode", P("string", "into (default), over, out, scanline, frame", Enum("into", "over", "out", "scanline", "frame")), false), ("count", P("integer", "into / over: how many (default 1)"), false),
                   ("to", P("string", "run to this address or symbol"), false), ("frame", P("integer", "run to this frame"), false), ("scanline", P("integer", "run to this scanline"), false), C()),
            a => On(a, s => s.Step((a.Str("mode") ?? "into").ToLowerInvariant(), a.Int("count", 1), a.Str("cpu") ?? "main", a.Str("to"), a.Long("frame"), a.Long("scanline") is long l ? (int)l : null)));

        Add("call_stack", "The call stack (JSR / JSL / CALL / RST and interrupt entries, innermost first), with symbols.", Schema(S(), C()), a => On(a, s => s.CallStack(a.Str("cpu") ?? "main")), ro: true);
        Add("registers", "A CPU's registers, the PC with its symbol and the instruction there, and the frame / scanline / dot / cycle.", Schema(S(), C()), a => On(a, s => s.Registers(a.Str("cpu") ?? "main")), ro: true);
        Add("set_register", "Writes a register (A, X, Y, PC, SP, P, ...). Live: control.", Schema(S(), ("name", P("string", "the register"), true), ("value", P("number|string", "the value"), true), C()),
            a => On(a, s => s.SetRegister(a.Str("cpu") ?? "main", a.Req("name"), a.Long("value") ?? throw new ToolError("value is required"))));
        Add("trace", "The last instructions run (the trace ring: registers, cycle, scanline, symbol; Mesen's trace logger columns), oldest first.", Schema(S(), ("count", P("integer", "lines (default 40)"), false), C()),
            a => On(a, s => s.Trace(a.Int("count", 40), a.Str("cpu") ?? "main")), ro: true);
        Add("trace_frame",
            "Traces every instruction of one coming frame to a file (headless): runs to the start of frame+1 and returns the file, its line count and the first lines. Optional filters: a function (symbol) or address ranges.",
            Schema(S(), ("frame", P("integer", "the frame to trace (now or later)"), true), ("lines", P("integer", "lines to return from the start (default 200)"), false),
                   ("function", P("string", "only inside this function (symbol)"), false), ("ranges", P("string[]", "only these address ranges"), false), C()),
            a => On(a, s => s.TraceFrame(a.Long("frame") ?? throw new ToolError("frame is required"), a.Str("cpu") ?? "main", a.Int("lines", 200), a.Str("function"), a.List("ranges"))));
        Add("disassemble", "Disassembly with symbols, from the PC (default), a CPU address or a code symbol (where it is mapped now).", Schema(S(), ("address", P("string", "a CPU address ($8000), a code symbol, or pc"), false), ("count", P("integer", "instructions (default 16)"), false), C()),
            a => On(a, s => s.Disassemble(a.Str("address"), a.Int("count", 16), a.Str("cpu") ?? "main")), ro: true);
        Add("evaluate", "Evaluates an expression now: registers, [addr], {addr} word, [symbol], frame, scanline, C operators.", Schema(S(), ("expr", P("string", "the expression"), true), C()),
            a => On(a, s => s.Evaluate(a.Req("expr"), a.Str("cpu") ?? "main")), ro: true);

        // ------------------------------------------------------------------------------------------------ the picture
        Add("screenshot", "The picture, returned as an image (PNG), with its size, hash and colour count.", Schema(S(), ("scale", P("integer", "1 (default) to 4"), false)),
            a => On(a, s => s.Screenshot(a.Int("scale", 1))), ro: true);
        Add("layers", "SNES / WideMess (headless): the next frame drawn with only some layers (bg1 bg2 bg3 bg4 obj), as an image; the machine is put back exactly as it was.",
            Schema(S(), ("layers", P("string[]", "bg1, bg2, bg3, bg4, obj"), true), ("scale", P("integer", "1 (default) to 4"), false)),
            a => On(a, s => s.Layers(a.List("layers"), a.Int("scale", 1))), ro: true);
        Add("tiles", "The tile graphics as an image (headless): NES pattern tables (as the PPU sees them, or source chr), Game Boy VRAM tiles, SNES / WideMess VRAM at 2, 4 or 8 bpp; palette: the game's palette by index (SNES CGRAM, Game Boy Color CRAM), default greys.",
            Schema(S(), ("source", P("string", "NES: ppu (default) or chr"), false), ("bpp", P("integer", "SNES: 2, 4 (default) or 8"), false), ("palette", P("integer", "palette index (default -1: greys)"), false),
                   ("start", P("number|string", "first byte (default 0)"), false), ("count", P("integer", "tiles (default 512)"), false), ("scale", P("integer", "1 to 4 (default 2)"), false)),
            a => On(a, s => s.Tiles(a.Str("source"), a.Int("bpp", 4), a.Int("palette", -1), a.LongOr("start", 0), a.Int("count", 0), a.Int("scale", 2))), ro: true);
        Add("sprites", "The sprites in OAM that are on screen (position, tile, palette, flips, priority), and the picture with their boxes drawn (headless).",
            Schema(S(), ("draw", P("boolean", "return the picture with boxes (default true)"), false), ("scale", P("integer", "1 (default) to 4"), false)),
            a => On(a, s => s.Sprites(a.Bool("draw", true), a.Int("scale", 1))), ro: true);

        // ------------------------------------------------------------------------------------------------ sound
        Add("record_sound", "Runs N seconds (headless) and records the sound: peak, RMS, per-second levels, frames with sound, and a .wav file.",
            Schema(S(), ("seconds", P("number", "seconds (default 2, at most 120)"), false), ("wav", P("boolean", "write the .wav (default true)"), false)),
            a => On(a, s => s.RecordSound(a.Double("seconds") ?? 2, a.Bool("wav", true))));

        // ------------------------------------------------------------------------------------------------ states
        Add("save_state", "Saves the machine in a named state in this server's memory (headless; never a file, never the user's saves). A live session never saves.",
            Schema(S(), ("name", P("string", "the state's name"), true)), a => On(a, s => s.SaveState(a.Req("name"))));
        Add("load_state", "Loads a named state (headless), or the app's quick state (live: name \"quick\", control).", Schema(S(), ("name", P("string", "the state's name"), true)),
            a => On(a, s => s.LoadState(a.Req("name"))));
        Add("diff_states", "Compares two states (or a state and \"current\") by symbol: the variables that differ with both values, then the bytes without a symbol, per memory space.",
            Schema(S(), ("a", P("string", "a state name or current"), true), ("b", P("string", "a state name or current (default current)"), false), ("limit", P("integer", "variables to list (default 100)"), false)),
            a => On(a, s => s.DiffStates(a.Req("a"), a.Str("b") ?? "current", a.Int("limit", 100))), ro: true);

        // ------------------------------------------------------------------------------------------------ analysis
        Add("checks",
            "The D4 hardware checks (headless): start (from now; open the ROM with checks:true to have them from power-on), report (the findings so far: rule, place, first frame, PC and symbol, call stack, count), stop (the final report). " +
            "Rules: RAM read before written, picture writes during rendering, OAM DMA timing, the frame IRQ, DMC conflicts, unofficial opcodes, open bus, stack wrap / overflow, lag frames, flash writes; the SNES and Game Boy sets.",
            Schema(S(), ("action", P("string", "start, report or stop", Enum("start", "report", "stop")), true), ("off", P("string[]", "start: rules to switch off"), false),
                   ("frame_wait", P("string[]", "start: frame-wait symbols"), false), ("from", P("integer", "the profile counts frames from this one"), false), ("top", P("integer", "functions in the profile (default 15)"), false)),
            a => On(a, s => s.Checks(a.Req("action").ToLowerInvariant(), a.List("off"), a.List("frame_wait"), a.LongOr("from", 0), a.Int("top", 15))));
        Add("profile", "The profiler (headless, with the checks on): CPU busy per frame (average, worst), lag frames, vblank used for uploads, DMA bytes, the costliest functions (inclusive / exclusive, from the symbols).",
            Schema(S(), ("from", P("integer", "count frames from this one (skip the boot)"), false), ("top", P("integer", "functions (default 15)"), false)),
            a => On(a, s => s.Checks("profile", new(), new(), a.LongOr("from", 0), a.Int("top", 15))), ro: true);

        // ------------------------------------------------------------------------------------------------ BrokenDev
        // ------------------------------------------------------------------------------------------------ movies, step back, divergence, bisect (D5)
        Add("step_back",
            "Steps a HEADLESS session back: by instructions (the main CPU, to the exact instruction: the newest state before it is loaded and the logged input replayed, " +
            "breakpoints and the checks off during the replay; the machine is then stopped there) or by frames (to the start of the n-th frame before now). Running on afterwards replays the same input; " +
            "a new input rewrites the future from there. The history keeps a state every 60 frames, the last 30 (30 s). By instructions needs the call stack tracking (open_rom's call_stack, on by default).",
            Schema(S(), ("instructions", P("integer", "how many instructions back"), false), ("frames", P("integer", "how many frame starts back"), false)),
            a => On(a, s => s.StepBack(a.Long("instructions"), a.Long("frames"))));

        Add("open_movie",
            $"Opens a {NesEmulator.Debugging.Movies.MovieFormat.Name} ({NesEmulator.Debugging.Movies.MovieFormat.Extension}) in a new HEADLESS session on a ROM (the movie's console, cores, power-on and frame timing) and plays it: " +
            "its input drives the session and every keyframe (picture and work-RAM hashes) is checked, so the result says in sync or the first frame it went off course. " +
            "Stop early with frames, then debug from there (breakpoints, step, step_back...); running on plays the rest. The ROM may be another build than the movie's (said in sameRom).",
            Schema(("movie", P("string", "the movie file"), true), ("rom", P("string", "the ROM to play it on"), true), ("symbols", P("string[]", "symbol files"), false),
                   ("frames", P("integer", "play this many frames (default: the whole movie)"), false), ("checks", P("boolean", "the hardware checks from power-on"), false),
                   ("call_stack", P("boolean", "track the call stack (default true; needed for step_back by instructions)"), false)),
            a =>
            {
                var m = LoadMovie(a.Req("movie"));
                var cp = m.Cores.Split('/');
                var s = HeadlessSession.Open(Sessions.NewId("h"), a.Req("rom"), m.Console, cp.Length == 3 ? cp[0] : "", cp.Length == 3 ? cp[1] : "", cp.Length == 3 ? cp[2] : "", a.List("symbols"),
                    a.Bool("call_stack", true), a.Bool("checks", false), new List<string>(), new List<string>());
                if (m.Console == "nes" && (m.NtscTiming || m.PowerOn != "fceux")) { s.Close(); throw new ToolError($"this movie needs {(m.NtscTiming ? "NTSC frame timing" : "")}{(m.NtscTiming && m.PowerOn != "fceux" ? " and " : "")}{(m.PowerOn != "fceux" ? "power-on RAM " + m.PowerOn : "")}, which headless sessions do not set yet: replay it with the movie tool (action play)"); }
                Sessions.Add(s);
                JsonObject j;
                lock (s.Gate) j = s.PlayMovie(m, Path.GetFullPath(a.Req("movie")), a.Long("frames") ?? -1);
                j["session"] = s.Id;
                return j;
            });

        Add("movie",
            $"BrokenNes's own movies ({NesEmulator.Debugging.Movies.MovieFormat.Name}, {NesEmulator.Debugging.Movies.MovieFormat.Extension}): " +
            "save (a headless session's input since it began, with its keyframes, as a movie file), info, play (replays a movie on a ROM, headless, and says in sync or where it went off course), " +
            "export (fm2 for FCEUX, bk2 for BizHawk: cross-checks only), diverge (the divergence finder: the movie on two runs, rom/rom_b or cores/cores_b or ntsc_b, gives the first frame, " +
            "then the first instruction, where they part, and the variables that differ by symbol; or against another emulator's BrokenDev compat run folder), " +
            "test (bisect's oracle on one ROM: good, bad, or can't tell when the replay loses its course).",
            Schema(("action", P("string", "save, info, play, export, diverge or test", Enum("save", "info", "play", "export", "diverge", "test")), true),
                   ("session", P("string", "save: the session"), false), ("path", P("string", "save: the file to write (default: the session's folder)"), false),
                   ("movie", P("string", "the movie file (info, play, export, diverge, test)"), false), ("rom", P("string", "the ROM (play, export, diverge side a, test)"), false),
                   ("symbols", P("string[]", "symbol files (side a)"), false), ("format", P("string", "export: fm2 or bk2", Enum("fm2", "bk2")), false), ("out", P("string", "export: the file to write"), false),
                   ("rom_b", P("string", "diverge: side b's ROM (default: rom)"), false), ("symbols_b", P("string[]", "diverge: side b's symbols"), false),
                   ("cores", P("string", "CPU/PPU/APU for side a (default: the movie's)"), false), ("cores_b", P("string", "diverge: side b's cores"), false),
                   ("ntsc_b", P("boolean", "diverge: side b with NTSC frame timing"), false), ("against", P("string", "diverge: another emulator's compat run folder (spec.txt + result.txt)"), false),
                   ("test", P("string", "test: no-crash | no-lag [from N] | picture at N | <expression> at N"), false), ("frames", P("integer", "play / diverge: stop after this many frames"), false)),
            a =>
            {
                string act = a.Req("action");
                if (act == "save") return On(a, s => s.MovieSave(a.Str("path")));
                var m = LoadMovie(a.Req("movie"));
                switch (act)
                {
                    case "info": return BrokenNes.Workshop.Movies.MovieCli.InfoJson(m);
                    case "play":
                    {
                        using var mm = Wrap(() => BrokenNes.Workshop.Movies.MovieMachine.OpenFor(m, a.Req("rom"), true, a.List("symbols"), a.Str("cores")));
                        var r = BrokenNes.Workshop.Movies.MoviePlayer.Play(m, mm, a.Long("frames") ?? -1);
                        return BrokenNes.Workshop.Movies.MovieCli.ReportJson(m, mm, r);
                    }
                    case "export":
                    {
                        byte[] rom = File.ReadAllBytes(a.Req("rom"));
                        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rom)).ToLowerInvariant();
                        if (!sha.Equals(m.RomSha256, StringComparison.OrdinalIgnoreCase)) throw new ToolError($"the ROM is not the movie's (sha256 {sha[..12]}, the movie's {m.RomSha256[..Math.Min(12, m.RomSha256.Length)]}): an export names the ROM it plays on");
                        string fmt = a.Str("format") ?? "bk2";
                        string outFile = a.Str("out") ?? Path.ChangeExtension(Path.GetFullPath(a.Req("movie")), fmt);
                        var res = Wrap(() => fmt == "fm2" ? BrokenNes.Workshop.Movies.MovieExport.ToFm2(m, rom, outFile) : BrokenNes.Workshop.Movies.MovieExport.ToBk2(m, rom, outFile));
                        return new JsonObject { ["file"] = res.Path, ["frames"] = res.Frames, ["notes"] = J.Strs(res.Notes), ["use"] = "a cross-check on the other emulator (agents debug on BrokenNes)" };
                    }
                    case "diverge":
                    {
                        var sideA = new BrokenNes.Workshop.Movies.DivergeSide(a.Req("rom"), a.List("symbols"), a.Str("cores"), null, "a");
                        if (a.Str("against") is { Length: > 0 } dir) return Wrap(() => BrokenNes.Workshop.Movies.MovieDiverge.Against(m, sideA, dir, 60));
                        var sideB = new BrokenNes.Workshop.Movies.DivergeSide(a.Str("rom_b") ?? a.Req("rom"), a.Has("symbols_b") ? a.List("symbols_b") : a.List("symbols"), a.Str("cores_b") ?? a.Str("cores"), a.Has("ntsc_b") ? a.Bool("ntsc_b", false) : null, "b");
                        return Wrap(() => BrokenNes.Workshop.Movies.MovieDiverge.Run(m, sideA, sideB, a.Long("frames") ?? -1));
                    }
                    case "test":
                    {
                        var (code, j) = Wrap(() => BrokenNes.Workshop.Movies.MovieCli.Test(m, a.Req("rom"), a.List("symbols"), a.Req("test"), new List<string>()));
                        j["exitCode"] = code;
                        return j;
                    }
                    default: throw new ToolError("action is save, info, play, export, diverge or test");
                }
            });

        Add("bisect",
            "Bisects a BrokenDev project's GAME commits (brokendev.ps1 bisect) for the first one where a movie's test turns bad, with today's BrokenNes: a scratch clone of the game's " +
            "repository (never the user's folder, never pushed: the clone has no remote), built halfway back each time (about 6 builds for 60 commits). A commit that does not build is skipped; " +
            "one where the replay loses its course (the movie's course variables differ from the recording before the test frame) is can't tell. Record the movie with course variables (brokendev movie record -Course).",
            Schema(("project", P("string", "the project id"), true), ("movie", P("string", "the movie (a file, or a name in projects\\<id>\\movies or out\\<id>\\movies)"), true),
                   ("test", P("string", "no-crash | no-lag [from N] | picture at N | <expression> at N (for example \"[lives] >= 1 at 600\")"), true),
                   ("good", P("string", "a commit where the test holds (default: the movie's recorded commit)"), false), ("bad", P("string", "a commit where it fails (default HEAD)"), false),
                   ("timeout_sec", P("integer", "give up after this long (default 3600)"), false)),
            a => BrokenDevBridge.Bisect(a.Req("project"), a.Req("movie"), a.Req("test"), a.Str("good") ?? "", a.Str("bad") ?? "HEAD", a.Int("timeout_sec", 3600)));

        // ------------------------------------------------------------------------------------------------ bug captures (D6)
        Add("bugs",
            "The bug captures (BrokenDev D6: the desktop app's F9 key and Lite's Bug button save the last 30 s: a movie from a save state that replays exactly, the ROM, the sound, a picture a second, " +
            "the player's note). list: a project's (its game workspace's bugs folder, and the default folder's that belong to it), a folder's, or the default folder's (%LOCALAPPDATA%\\BrokenNes\\bugs); " +
            "info: one bundle; play: replays one headless (every keyframe and the last picture checked, the hardware checks on) and says in sync or where it went off course; " +
            "open: opens one in a new HEADLESS session and plays it to its end (or to frames), so the bug can be debugged there: read memory, breakpoints, step_back through the last 30 s. " +
            "A bug is a bundle folder, its bug.json, Lite's .zip, or a bundle id from list (with project or folder).",
            Schema(("action", P("string", "list, info, play or open", Enum("list", "info", "play", "open")), true),
                   ("project", P("string", "list / id lookup: a BrokenDev project id"), false), ("folder", P("string", "list / id lookup: a bugs folder"), false),
                   ("bug", P("string", "info, play, open: the bundle (path or id)"), false), ("rom", P("string", "play / open: another ROM than the bundle's own copy"), false),
                   ("symbols", P("string[]", "symbol files (default: the bundle's)"), false), ("frames", P("integer", "open: play this many frames (default: the whole capture)"), false),
                   ("checks", P("boolean", "open: the hardware checks during the replay (play always runs them)"), false)),
            a =>
            {
                string act = a.Req("action");
                var dirs = BugDirs(a.Str("project"), a.Str("folder"));
                if (act == "list")
                {
                    var arr = new JsonArray();
                    foreach (var (dir, why, filter) in dirs)
                        foreach (var x in BrokenNes.Workshop.Bugs.BugCli.List(dir).OfType<JsonObject>())
                            if (filter(x)) { x["folder"] = dir; x["folderWhy"] = why; arr.Add(x.DeepClone()); }
                    return new JsonObject { ["bugs"] = arr, ["folders"] = J.Strs(dirs.Select(d => $"{d.dir} ({d.why})")), ["count"] = arr.Count };
                }
                var b = Wrap(() => BrokenNes.Workshop.Bugs.BugBundleReader.Open(ResolveBug(a.Req("bug"), dirs)));
                switch (act)
                {
                    case "info": return BrokenNes.Workshop.Bugs.BugCli.Summary(b);
                    case "play":
                    {
                        string outDir = Path.Combine(Path.GetTempPath(), "brokennes-mcp-bugs");
                        var (code, j) = Wrap(() => BrokenNes.Workshop.Bugs.BugCli.Play(b, a.Str("rom"), a.List("symbols"), true, outDir));
                        j["exitCode"] = code;
                        return j;
                    }
                    case "open":
                    {
                        if (!b.HasMovie) throw new ToolError($"this capture has no movie to replay: {b.S("noMovie")}");
                        var m = Wrap(() => b.LoadMovie());
                        string rom = Wrap(() => b.Rom(a.Str("rom"), out _));
                        var syms = a.List("symbols").Count > 0 ? a.List("symbols") : b.Symbols();
                        var cp = m.Cores.Split('/');
                        var s = HeadlessSession.Open(Sessions.NewId("h"), rom, m.Console, cp.Length == 3 ? cp[0] : "", cp.Length == 3 ? cp[1] : "", cp.Length == 3 ? cp[2] : "", syms,
                            true, a.Bool("checks", false), new List<string>(), new List<string>());
                        if (m.Console == "nes" && m.NtscTiming) { s.Close(); throw new ToolError("this capture was made with NTSC frame timing, which headless sessions do not set yet: use action play"); }
                        Sessions.Add(s);
                        JsonObject j;
                        lock (s.Gate) j = s.PlayMovie(m, b.File_(b.S("movie")), a.Long("frames") ?? -1);
                        j["session"] = s.Id;
                        j["bug"] = BrokenNes.Workshop.Bugs.BugCli.Summary(b);
                        return j;
                    }
                    default: throw new ToolError("action is list, info, play or open");
                }
            });

        Add("brokendev",
            "Runs a BrokenDev verb on a project (BrokenDev/brokendev.ps1): build, verify (the checks, budgets and accepted findings), compat (the other emulators), run (opens it in the desktop app: its PID comes back for attach_live), " +
            "list (the projects), status, doctor. Returns the exit code, the output and the record file.",
            Schema(("verb", P("string", "build, verify, compat, run, list, status or doctor", Enum("build", "verify", "compat", "run", "list", "status", "doctor")), true),
                   ("project", P("string", "the project id (or all for build / verify / compat)"), false), ("emu", P("string[]", "compat: emulator ids"), false), ("scenario", P("string[]", "compat: scenario ids"), false),
                   ("timeout_sec", P("integer", "give up after this long (default 1800)"), false)),
            a => BrokenDevBridge.RunVerb(a.Req("verb"), a.Str("project") ?? "", a.List("emu"), a.List("scenario"), a.Int("timeout_sec", 1800)));

        return t;
    }
}
