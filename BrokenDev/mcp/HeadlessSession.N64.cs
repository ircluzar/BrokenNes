// [brokennes_int3] The N64 in BrokenNes's MCP server: a headless session over cor64's N64Session (through N64ConsoleSession),
// N64Debugger (breakpoints, step, run until, call stack, trace) and N64DebugTarget (domains, registers, disassembly, symbols, search).
// Staged in cer64plus (integration/brokennes); it lands at BrokenDev/mcp/HeadlessSession.N64.cs (MERGE_PLAN.md) and needs
// registration.patch (Tools.cs routes open_rom here, Program.cs calls N64Host.Register()).
//
// Why a Session of its own and not a DebugMachine under HeadlessSession: BrokenNes's Debugger drives a per-instruction hook of each
// CPU, with 16-bit trace records, 2-byte Classify and 16-bit address wrap. cor64 has its own debugger, exact for the VR4300 and the RSP,
// that costs nothing while it is off (SM64 stays bit-exact). This adapter maps the MCP tools onto it directly.
// What the N64 cannot do honestly is refused with the reason (layers, tiles, sprites, the D4 checks, step back, movies, Timeflow, ...).

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using NesEmulator.N64;
using NesEmulator.Systems;
using cor64.Debug;

namespace BrokenNes.Mcp;

internal sealed class N64HeadlessSession : Session
{
    public override string Kind => "headless";
    public override string ConsoleKey => "n64";

    // cor64's debug hooks are process-wide (DebugHooks.Current): one N64 session at a time may have its debugger on, and while it does
    // no other N64 session may run (its frames would go through the other session's debugger).
    private static readonly object ownerLock = new();
    private static N64HeadlessSession? debugOwner;

    private readonly N64ConsoleSession cs;
    private readonly N64DebugTarget T;
    private readonly N64Debugger D;
    private readonly string romName, romPath, folder;
    private readonly bool keepCallStack;
    private readonly PadButtons[] held = new PadButtons[4];
    private readonly SortedDictionary<long, List<(int player, PadButtons b)>> schedule = new();
    private long framesStarted;
    private readonly Dictionary<string, (byte[] state, long frames)> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> watches = new();
    private readonly short[] sbuf = new short[65536];
    private List<short>? rec;
    private IReadOnlyList<TraceEntry> lastTrace = Array.Empty<TraceEntry>();
    private (string domain, int width, List<int> cand, byte[] prev)? search;
    private const int TraceRing = 65536;

    private N64HeadlessSession(string id, N64ConsoleSession s, string romName, string romPath, int romSize, bool callStack)
    {
        Id = id; cs = s; this.romName = romName; this.romPath = romPath; keepCallStack = callStack;
        T = N64Host.DebugTarget(s, (ulong)romSize);
        D = new N64Debugger(s.Core, T);
        folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrokenNes", "mcp", "sessions", $"{Environment.ProcessId}-{id}");
    }

    /// <summary>True when open_rom should come here: console n64, an N64 extension, or an N64 header in any byte order.</summary>
    public static bool Claims(string path, string? consoleKey)
    {
        string k = (consoleKey ?? "").Trim().ToLowerInvariant();
        if (k is "n64" or "nintendo64" or "z64") return true;
        if (k.Length > 0) return false;
        if (Path.GetExtension(path).ToLowerInvariant() is ".z64" or ".n64" or ".v64") return true;
        try
        {
            using var f = File.OpenRead(path);
            var h = new byte[4];
            return f.Read(h, 0, 4) == 4 && IsN64Header(h);
        }
        catch (IOException) { return false; }
    }

    private static bool IsN64Header(byte[] r) =>
        (r[0], r[1], r[2], r[3]) is (0x80, 0x37, 0x12, 0x40) or (0x37, 0x80, 0x40, 0x12) or (0x40, 0x12, 0x37, 0x80);

    public static N64HeadlessSession Open(string id, string path, string cpu, string ppu, string apu, IEnumerable<string> symbolFiles, bool callStack, bool startChecks)
    {
        if (!File.Exists(path)) throw new ToolError($"no ROM at '{path}'");
        if (startChecks) throw new ToolError("the D4 hardware checks and the profiler do not cover the Nintendo 64 (there are no N64 rules); open it without checks");
        if (cpu.Length > 0 && !cpu.Equals("COR64", StringComparison.OrdinalIgnoreCase)) throw new ToolError($"the Nintendo 64 CPU menu has one core, COR64 (not '{cpu}')");
        byte[] raw = File.ReadAllBytes(path);
        byte[] rom = RomDetect.Unwrap(raw, path, out string inner);
        if (rom.Length < 0x1000 || !IsN64Header(rom)) throw new ToolError($"{Path.GetFileName(path)}: not an N64 cartridge (no N64 header in any byte order)");
        N64ConsoleSession s;
        try { s = new N64ConsoleSession(rom, Path.GetFileName(inner), ppu.Length > 0 ? ppu.ToUpperInvariant() : null, apu.Length > 0 ? apu.ToUpperInvariant() : null); }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException) { throw new ToolError(e.Message); }
        var h = new N64HeadlessSession(id, s, Path.GetFileName(inner), Path.GetFullPath(path), rom.Length, callStack);
        try
        {
            foreach (var f in symbolFiles) h.LoadSymbols(f);
            if (callStack) h.DebugOn();
        }
        catch { h.Close(); throw; }
        return h;
    }

    // ================================================================================================ the debugger on / off

    private bool Owns { get { lock (ownerLock) return debugOwner == this; } }

    /// <summary>Turns cor64's debugger on for this session (exact, interpreted: slower, and not bit-exact with a normal run).</summary>
    private void DebugOn()
    {
        lock (ownerLock)
        {
            if (debugOwner != null && debugOwner != this)
                throw new ToolError($"another N64 session ({Sessions.Mask(debugOwner.Id)}) has the N64 debugger on: cor64's debug hooks are process-wide, so one N64 session at a time can debug (remove its breakpoints, or close it)");
            debugOwner = this;
        }
        if (!D.Enabled) { D.Enabled = true; D.StartTrace("cpu", TraceRing); }
    }

    /// <summary>Back to full speed when nothing needs the debugger (no breakpoint, no call stack asked for, not stopped).</summary>
    private void DebugOffIfIdle()
    {
        if (!Owns || keepCallStack || D.IsPaused || D.ListBreakpoints().Count > 0) return;
        D.Enabled = false;
        lock (ownerLock) if (debugOwner == this) debugOwner = null;
    }

    private void EnsureCanRun()
    {
        lock (ownerLock)
            if (debugOwner != null && debugOwner != this && DebugHooks.Active)
                throw new ToolError($"another N64 session ({Sessions.Mask(debugOwner.Id)}) has the N64 debugger on; cor64's debug hooks are process-wide, so no other N64 session can run frames until it turns it off (its breakpoints removed) or closes");
    }

    // ================================================================================================ describe / status / close

    public override JsonObject Describe() => new()
    {
        ["session"] = Id, ["kind"] = Kind, ["console"] = ConsoleKey, ["consoleName"] = Consoles.DisplayName(ConsoleKind.N64),
        ["rom"] = romName, ["romPath"] = romPath, ["title"] = cs.Title, ["cores"] = $"COR64/{cs.Core.Ppu.Id}/{cs.Core.Apu.Id}", ["description"] = cs.Description,
        ["gameId"] = cs.GameId, ["saveType"] = cs.SaveType,
        ["debugger"] = true, ["debuggerOn"] = D.Enabled, ["frame"] = framesStarted, ["files"] = folder,
        ["cpus"] = J.Arr(new JsonNode[] {
            new JsonObject { ["id"] = "main", ["name"] = "VR4300 (cpu)", ["spaces"] = J.Strs(T.Domains.Where(d => d.CodeCpu == "cpu").Select(d => d.Name)) },
            new JsonObject { ["id"] = "rsp", ["name"] = "RSP", ["spaces"] = J.Strs(new[] { "IMEM", "DMEM" }) } }),
        ["symbols"] = T.Symbols.Count,
        ["domains"] = J.Strs(T.Domains.Select(d => d.Name)),
        ["checks"] = false, ["states"] = cs.SupportsState,
        ["limitations"] = J.Strs(Limitations),
    };

    private static readonly string[] Limitations =
    {
        "the debugger is cor64's: it turns on with a breakpoint, a step, run_until, trace_frame or open_rom call_stack:true, and then runs interpreted (slower, and not bit-exact with a normal run); it turns off again when no breakpoint is left",
        "one N64 session per server can have its debugger on (cor64's hooks are process-wide); while it does, other N64 sessions cannot run",
        "read / write breakpoints need COR64_DBG_WATCH=1 in the server's environment (the bus taps are compiled out of the hot path otherwise)",
        "no layers, tiles or sprites: the RDP draws triangles into a framebuffer, there are no tile layers or OAM",
        "no D4 hardware checks, profiler, debug port, step back, movies, state diffs or Timeflow for the N64",
        "the generic pad: L = Z trigger, Select = L shoulder, X/Y/C/Z = C-Up/C-Left/C-Right/C-Down, directions = the stick at +-80 (the d-pad while Mode is held); input stick:\"x,y\" sets the analog stick",
    };

    public override JsonObject Status()
    {
        var j = Describe();
        j["framesRun"] = framesStarted;
        j["parked"] = D.IsPaused;
        if (D.IsPaused) j["break"] = StopJson(D.LastStop);
        j["held"] = J.Strs(held.Take(2).Select(b => b.ToString()));
        if (watches.Count > 0) j["watches"] = WatchValues();
        j["savedStates"] = J.Strs(states.Keys);
        j["history"] = "no history (step back, movies) on the N64";
        j["rsp"] = RspStatusJson(); // [dbg_rsp] the current task's microcode (read from DMEM / RDRAM on demand)
        return j;
    }

    public override void Close()
    {
        try { if (Owns) D.Enabled = false; } catch { }
        lock (ownerLock) if (debugOwner == this) debugOwner = null;
        try { cs.Dispose(); } catch { }
    }

    // ================================================================================================ running

    private void ApplyInput()
    {
        while (schedule.Count > 0)
        {
            var first = schedule.First();
            if (first.Key > framesStarted) break;
            foreach (var (pl, b) in first.Value) held[pl] = b;
            schedule.Remove(first.Key);
        }
        for (int p = 0; p < 4; p++) cs.SetPad(p, held[p]);
    }

    private void DrainAudio()
    {
        int n;
        while ((n = cs.ReadSamples(sbuf)) > 0) { var r = rec; if (r != null) for (int i = 0; i < n; i++) r.Add(sbuf[i]); }
    }

    /// <summary>Runs up to n frames; stops early at a break. Returns "frames" or "break".</summary>
    private string RunFrames(long n)
    {
        EnsureCanRun();
        if (D.Enabled && D.IsPaused) D.Resume();   // leaves the breakpoint it is stopped at
        n = Math.Clamp(n, 0, 1_000_000);
        var t0 = DateTime.UtcNow;
        for (long f = 0; f < n; f++)
        {
            ApplyInput();
            cs.RunFrame();
            framesStarted++;
            DrainAudio();
            if (D.Enabled && D.IsPaused) return "break";
            if ((DateTime.UtcNow - t0).TotalSeconds > 300) return "timeout";
        }
        return "frames";
    }

    private JsonObject RunResult(string outcome, long before)
    {
        DebugOffIfIdle();   // a run that ended between breaks with nothing armed goes back to full speed
        var j = new JsonObject { ["outcome"] = outcome, ["frame"] = framesStarted, ["framesRun"] = framesStarted - before, ["parked"] = D.Enabled && D.IsPaused };
        if (D.Enabled && D.IsPaused) j["break"] = StopJson(D.LastStop);
        if (outcome == "timeout") j["note"] = "the call's time limit (300 s) passed: the machine stopped where it was (run again to go on)";
        if (watches.Count > 0) j["watches"] = WatchValues();
        return j;
    }

    /// <summary>After a stop driven by N64Debugger itself (step, run until): the frames it ran count too.</summary>
    private JsonObject StopResult(StopInfo st, long before)
    {
        var j = new JsonObject
        {
            ["outcome"] = st.Reason == StopReason.FrameLimit ? "frames" : "break", ["frame"] = framesStarted, ["parked"] = D.IsPaused,
            ["break"] = StopJson(st), ["instructions"] = st.CpuSteps,
        };
        if (watches.Count > 0) j["watches"] = WatchValues();
        DrainAudio();
        return j;
    }

    private JsonObject StopJson(StopInfo st)
    {
        var j = new JsonObject
        {
            ["reason"] = st.Reason.ToString().ToLowerInvariant(), ["cpu"] = st.Cpu == "rsp" ? "rsp" : "main", ["pc"] = Hex32(st.Pc), ["pcSymbol"] = st.Cpu == "rsp" ? null : T.Symbols.Label(st.Pc),
        };
        if (st.BreakpointId > 0) j["breakpoint"] = st.BreakpointId;
        var line = T.Disassemble(st.Cpu == "rsp" ? "rsp" : "cpu", st.Pc, 1);
        if (line.Count == 1) j["instruction"] = line[0].Text;
        return j;
    }

    public override JsonObject Run(long frames, List<PadStep>? script)
    {
        long before = framesStarted;
        if (script != null) Schedule(script, 0);
        return RunResult(RunFrames(frames), before);
    }

    public override JsonObject RunUntil(string? spec, string kind, string? condition, long? frame, int? scanline, long maxFrames, string cpu)
    {
        long before = framesStarted;
        if (scanline != null) throw new ToolError("run_until scanline is not offered on the N64 (cor64 does not stop at a VI line); use frame or an address");
        if (frame is long f)
        {
            if (f < framesStarted) throw new ToolError($"frame {f} has passed (now {framesStarted}); the N64 has no step back");
            return RunResult(RunFrames(f - framesStarted), before);
        }
        if (string.IsNullOrWhiteSpace(spec)) throw new ToolError("run_until needs spec (an address or symbol) or frame");
        string c = CpuId(cpu);
        int mf = (int)Math.Clamp(maxFrames, 1, 100_000);
        // a breakpoint for this run only, so the frames run here (counted, with the scheduled pads); another breakpoint can stop it first
        int id = AddBp(spec!, kind, condition, c);
        try { return RunResult(RunFrames(mf), before); }
        finally { D.RemoveBreakpoint(id); DebugOffIfIdle(); }
    }

    public override JsonObject Pause()
    {
        DebugOn();
        D.Pause();
        return new JsonObject { ["parked"] = true, ["break"] = StopJson(D.LastStop), ["note"] = "headless sessions are always still between calls; the debugger is now on, so step goes from here" };
    }

    public override JsonObject Continue() => Run(600, null);

    public override JsonObject Reset()
    {
        cs.Reset();
        if (D.Enabled) { D.Enabled = false; D.Enabled = true; D.StartTrace("cpu", TraceRing); }   // a fresh call stack and trace
        return Status();
    }

    // ================================================================================================ input

    public JsonObject SetStick(int player, string spec)
    {
        if (player is < 0 or > 3) throw new ToolError("player is 1 to 4");
        spec = spec.Trim();
        if (spec.Equals("off", StringComparison.OrdinalIgnoreCase) || spec.Equals("release", StringComparison.OrdinalIgnoreCase))
        { cs.SetStick(player, null, null); return new JsonObject { ["stick"] = "off", ["note"] = "the directions drive the stick again" }; }
        var p = spec.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length != 2 || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
            throw new ToolError("stick is \"x,y\" (-128..127 each, up and right positive; a real pad reaches about +-80) or \"off\"");
        x = Math.Clamp(x, -128, 127); y = Math.Clamp(y, -128, 127);
        cs.SetStick(player, (sbyte)x, (sbyte)y);
        return new JsonObject { ["stick"] = $"{x},{y}", ["note"] = "held until changed; while it is set the directions drive the d-pad" };
    }

    private PadButtons ParseButtons(IEnumerable<string> names) => Pads.Parse(names, ConsoleKey);

    private void Schedule(List<PadStep> script, int player)
    {
        foreach (var s in script)
        {
            var b = ParseButtons(s.Buttons);
            if (!schedule.TryGetValue(framesStarted + s.Frame, out var l)) schedule[framesStarted + s.Frame] = l = new();
            l.Add((player, b));
        }
    }

    public override JsonObject Input(string mode, string[] buttons, int frames, int player, List<PadStep>? script, long? runFrames)
    {
        if (player is < 0 or > 3) throw new ToolError("player is 1 to 4");
        long before = framesStarted;
        switch (mode)
        {
            case "hold":
            {
                var b = ParseButtons(buttons); held[player] = b;
                var j = Status(); j["held"] = b.ToString(); j["note"] = "held until changed; they reach the game on the next frame run";
                return j;
            }
            case "release": held[player] = PadButtons.None; return Status();
            case "press":
            {
                var b = ParseButtons(buttons);
                frames = Math.Clamp(frames, 1, 600);
                held[player] = b;
                long at = framesStarted + frames;
                if (!schedule.TryGetValue(at, out var l)) schedule[at] = l = new();
                l.Add((player, PadButtons.None));
                var j = RunResult(RunFrames(frames), before);
                j["pressed"] = b.ToString(); j["heldFrames"] = frames;
                return j;
            }
            case "script":
            {
                if (script == null || script.Count == 0) throw new ToolError("script is needed: \"0:Start,10:,60:Right+A,120:\" (frames from now; an empty list releases)");
                Schedule(script, player);
                var j = RunResult(RunFrames(runFrames ?? script.Max(s => s.Frame) + 1), before);
                j["steps"] = script.Count;
                return j;
            }
            default: throw new ToolError("mode is press, hold, release or script");
        }
    }

    public override JsonObject Pointer(int? x, int? y, string action, string device, int frames) =>
        throw new ToolError("the pointer port is WideMess's; the N64 has none (the N64 mouse is not emulated)");

    // ================================================================================================ memory

    public override JsonObject Domains() => new()
    {
        ["console"] = ConsoleKey,
        ["domains"] = J.Arr(T.Domains.Select(d => (JsonNode)new JsonObject
        {
            ["id"] = d.Name, ["label"] = d.Name, ["size"] = (long)d.Size, ["writable"] = d.Writable, ["base"] = Hex32(d.DisplayBase), ["code"] = d.CodeCpu,
        })),
        ["specs"] = "an address is a CPU virtual address ($80001000, KSEG0/KSEG1 or TLB-mapped, through the VIRTUAL domain), domain:offset (rdram:$1000, dmem:0, imem:0, rom:$40), or a symbol (name, name+4)",
    };

    private cor64.Debug.MemoryDomain Dom(string id) =>
        T.Domains.FirstOrDefault(d => d.Name.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? (id.Equals("ram", StringComparison.OrdinalIgnoreCase) || id.Equals("wram", StringComparison.OrdinalIgnoreCase) ? T.Domains.First(d => d.Name == "RDRAM") : null)
        ?? throw new ToolError($"no memory domain '{id}' (the N64 has: {string.Join(", ", T.Domains.Select(d => d.Name))})");

    /// <summary>A spec to (domain, offset, length): "domain:offset", a symbol (+n), or a virtual address.</summary>
    private (cor64.Debug.MemoryDomain d, ulong off, int? len) Spec(string spec)
    {
        spec = spec.Trim();
        int colon = spec.IndexOf(':');
        if (colon > 0)
        {
            var d = Dom(spec[..colon]);
            var (o, l) = Range(spec[(colon + 1)..]);
            return (d, o, l);
        }
        var v = Dom(N64DebugTarget.VirtualDomain);
        var (a, n) = Range(spec);
        return (v, (uint)a, n);
    }

    private (ulong off, int? len) Range(string s)
    {
        int dash = s.IndexOf('-', 1);
        if (dash > 0)
        {
            ulong a = Addr(s[..dash]), b = Addr(s[(dash + 1)..]);
            if (b < a) throw new ToolError($"'{s}': the range ends before it starts");
            return (a, (int)Math.Min(b - a + 1, 1 << 20));
        }
        if (char.IsLetter(s[0]) || s[0] == '_')
        {
            string name = s; long plus = 0; int p = s.IndexOf('+');
            if (p > 0) { name = s[..p]; plus = Num.Parse(s[(p + 1)..], "offset"); }
            if (!T.Symbols.TryGetAddress(name, out ulong a)) throw new ToolError($"no symbol '{name}' ({T.Symbols.Count} loaded; symbols load: a file)");
            var sym = T.Symbols.All.FirstOrDefault(x => x.Name == name);
            return ((uint)a + (ulong)plus, p < 0 && sym.Size > 0 ? (int)sym.Size : null);
        }
        return (Addr(s), null);
    }

    private static ulong Addr(string s) => (ulong)(uint)Num.Parse(s, "address");

    private ulong CodeAddress(string spec, string cpu)
    {
        var (d, off, _) = Spec(spec);
        if (cpu == "rsp") return off & 0xFFC;
        if (d.Name == N64DebugTarget.VirtualDomain) return SymbolTable.Norm(off);
        if (d.Name == "RDRAM") return SymbolTable.Norm(0x80000000UL + off);
        throw new ToolError($"'{spec}': a CPU code address is a virtual address or a symbol (or rdram:offset)");
    }

    public override JsonObject ReadMemory(string? spec, string? domain, long? address, int? length, string cpu)
    {
        cor64.Debug.MemoryDomain d; ulong off; int? l0;
        if (!string.IsNullOrWhiteSpace(spec)) (d, off, l0) = Spec(spec!);
        else if (domain != null && address != null) { d = Dom(domain); off = (ulong)address.Value; l0 = null; }
        else throw new ToolError("read_memory needs spec, or domain + address");
        int len = Math.Clamp(length ?? l0 ?? 16, 1, 65536);
        var buf = new byte[len];
        int n = T.ReadMemory(d.Name, off, buf);
        if (n == 0) throw new ToolError($"{d.Name}:{Hex32(off)} cannot be read (unmapped, past the end, or a TLB miss)");
        return new JsonObject
        {
            ["domain"] = d.Name, ["address"] = Hex32(off), ["length"] = n, ["hex"] = Num.HexBytes(buf.AsSpan(0, n)),
            ["symbol"] = d.Name == N64DebugTarget.VirtualDomain ? T.Symbols.Label(SymbolTable.Norm(off)) : null,
            ["note"] = n < len ? $"{len - n} bytes past what is mapped were not read" : "big-endian, as the N64 stores it",
        };
    }

    public override JsonObject WriteMemory(string? spec, string? domain, long? address, byte[] data, string cpu)
    {
        if (data.Length == 0) throw new ToolError("data is needed");
        cor64.Debug.MemoryDomain d; ulong off;
        if (!string.IsNullOrWhiteSpace(spec)) (d, off, _) = Spec(spec!);
        else if (domain != null && address != null) { d = Dom(domain); off = (ulong)address.Value; }
        else throw new ToolError("write_memory needs spec, or domain + address");
        if (!d.Writable) throw new ToolError($"the {d.Name} domain is read-only here (the cartridge ROM, PIF RAM and the RCP registers are not written by the debugger)");
        var before = new byte[data.Length];
        int nb = T.ReadMemory(d.Name, off, before);
        int n = T.WriteMemory(d.Name, off, data);
        if (n == 0) throw new ToolError($"{d.Name}:{Hex32(off)} cannot be written (unmapped, past the end, or not RAM)");
        var after = new byte[n]; T.ReadMemory(d.Name, off, after);
        return new JsonObject
        {
            ["domain"] = d.Name, ["address"] = Hex32(off), ["written"] = n, ["before"] = Num.HexBytes(before.AsSpan(0, Math.Min(nb, n))), ["after"] = Num.HexBytes(after),
            ["note"] = n < data.Length ? $"stopped after {n} bytes (the rest is not RAM)" : "code written to RDRAM drops the CPU's compiled blocks, so it runs as written",
        };
    }

    public override JsonObject SearchMemory(string domain, string op, long? value, int width, bool fresh, int show)
    {
        if (width is not (1 or 2 or 4)) throw new ToolError("width is 1, 2 or 4 (big-endian)");
        var d = Dom(domain);
        if (d.Name == N64DebugTarget.VirtualDomain) throw new ToolError("search a physical domain (RDRAM, DMEM, IMEM, ROM), not VIRTUAL");
        var mem = new byte[(int)Math.Min(d.Size, 64UL << 20)];
        int n = T.ReadMemory(d.Name, 0, mem);
        long Val(byte[] m, int at) { long v = 0; for (int i = 0; i < width; i++) v = (v << 8) | m[at + i]; return v; }
        bool first = fresh || search == null || search.Value.domain != d.Name || search.Value.width != width;
        IEnumerable<int> from = first ? Enumerable.Range(0, Math.Max(0, (n - width) / width + 1)).Select(i => i * width) : search!.Value.cand;
        byte[]? prev = first ? null : search!.Value.prev;
        Func<int, bool> keep = op switch
        {
            "eq" or "==" => a => Val(mem, a) == (value ?? throw new ToolError("op eq needs value")),
            "ne" or "!=" => a => Val(mem, a) != (value ?? throw new ToolError("op ne needs value")),
            "lt" or "<" => a => Val(mem, a) < (value ?? throw new ToolError("op lt needs value")),
            "gt" or ">" => a => Val(mem, a) > (value ?? throw new ToolError("op gt needs value")),
            "changed" => a => prev == null ? throw new ToolError("changed compares with the last search; search once first") : Val(mem, a) != Val(prev, a),
            "unchanged" or "same" => a => prev == null ? throw new ToolError("unchanged compares with the last search; search once first") : Val(mem, a) == Val(prev, a),
            "increased" or "inc" => a => prev == null ? throw new ToolError("increased compares with the last search; search once first") : Val(mem, a) > Val(prev, a),
            "decreased" or "dec" => a => prev == null ? throw new ToolError("decreased compares with the last search; search once first") : Val(mem, a) < Val(prev, a),
            "all" or "start" => _ => true,
            _ => throw new ToolError("op is eq, ne, lt, gt, changed, unchanged, increased, decreased or all"),
        };
        var cand = from.Where(keep).ToList();
        search = (d.Name, width, cand, mem);
        return new JsonObject
        {
            ["domain"] = d.Name, ["width"] = width, ["candidates"] = cand.Count, ["fresh"] = first,
            ["matches"] = J.Arr(cand.Take(Math.Clamp(show, 0, 500)).Select(a => (JsonNode)new JsonObject { ["offset"] = Hex32((ulong)a), ["value"] = Val(mem, a), ["address"] = Hex32(d.DisplayBase + (ulong)a) })),
        };
    }

    // ================================================================================================ symbols

    private int LoadSymbols(string file)
    {
        if (!File.Exists(file)) throw new ToolError($"no symbol file at '{file}'");
        try { return T.Symbols.LoadFile(file); }
        catch (Exception e) when (e is FormatException or InvalidDataException) { throw new ToolError($"{Path.GetFileName(file)}: {e.Message}"); }
    }

    public override JsonObject Symbols(string? load, string? find, long? address, string? prefix, string cpu, int limit)
    {
        var j = new JsonObject { ["count"] = T.Symbols.Count, ["formats"] = "text (address name [size], nm output, PJ64 / Mupen .sym) or an ELF (.elf) with a symbol table" };
        if (!string.IsNullOrWhiteSpace(load)) { j["loaded"] = LoadSymbols(load!); j["count"] = T.Symbols.Count; }
        if (!string.IsNullOrWhiteSpace(find))
        {
            if (!T.Symbols.TryGetAddress(find!, out ulong a)) throw new ToolError($"no symbol '{find}'");
            var s = T.Symbols.All.First(x => x.Name == find);
            var b = new byte[Math.Clamp((int)(s.Size == 0 ? 4 : s.Size), 1, 256)];
            int n = T.ReadMemory(N64DebugTarget.VirtualDomain, (uint)a, b);
            j["symbol"] = new JsonObject { ["name"] = s.Name, ["address"] = Hex32(a), ["size"] = s.Size, ["bytes"] = n > 0 ? Num.HexBytes(b.AsSpan(0, n)) : null };
        }
        if (address is long ad) j["label"] = T.Symbols.Label(SymbolTable.Norm((ulong)ad));
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            var all = T.Symbols.All.Where(s => prefix == "*" || s.Name.Contains(prefix!, StringComparison.OrdinalIgnoreCase)).ToList();
            j["matching"] = all.Count;
            j["symbols"] = J.Arr(all.Take(Math.Clamp(limit, 1, 5000)).Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["address"] = Hex32(s.Address), ["size"] = s.Size }));
        }
        return j;
    }

    // ================================================================================================ breakpoints, watches

    private static string CpuId(string cpu) => (cpu ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "main" or "cpu" or "vr4300" => "cpu",
        "rsp" => "rsp",
        _ => throw new ToolError($"no CPU '{cpu}' (the N64 has: main (the VR4300), rsp)"),
    };

    private int AddBp(string spec, string kind, string? condition, string cpu)
    {
        var k = kind.Trim().ToLowerInvariant() switch
        {
            "exec" or "x" or "execute" => BreakpointKind.Exec,
            "read" or "r" => BreakpointKind.Read,
            "write" or "w" => BreakpointKind.Write,
            _ => throw new ToolError($"kind '{kind}' is not offered on the N64 (exec, read or write)"),
        };
        if (k != BreakpointKind.Exec && cpu == "rsp") throw new ToolError("read / write breakpoints are the VR4300's; the RSP has exec breakpoints only");
        ulong at; int len = 4;
        if (k == BreakpointKind.Exec) at = CodeAddress(spec, cpu);
        else
        {
            var (d, off, l) = Spec(spec);
            len = Math.Clamp(l ?? 4, 1, 1 << 20);
            at = d.Name == N64DebugTarget.VirtualDomain ? SymbolTable.Norm(off) : d.Name == "RDRAM" ? off : throw new ToolError("a read / write breakpoint is on a virtual address, a symbol or rdram:offset");
        }
        DebugOn();
        int id;
        try { id = D.AddBreakpoint(cpu, k, at, string.IsNullOrWhiteSpace(condition) ? null : condition, len); }
        catch (FormatException e) { DebugOffIfIdle(); throw new ToolError($"condition: {e.Message} (the N64 condition is 'reg op value [&& ...]', e.g. a0 == 0x10, with ==, !=, <, >, <=, >=)"); }
        if (id < 0)
        {
            DebugOffIfIdle();
            throw new ToolError(Environment.GetEnvironmentVariable("COR64_DBG_WATCH") == "1"
                ? $"'{spec}' does not translate to physical memory (a TLB miss right now)"
                : "read / write breakpoints need the bus taps: start the MCP server with COR64_DBG_WATCH=1 (they are compiled out of the hot path otherwise)");
        }
        return id;
    }

    public override JsonObject AddBreakpoint(string spec, string kind, string? condition, string? hits, bool tracepoint, string? log, string cpu)
    {
        if (!string.IsNullOrWhiteSpace(hits)) throw new ToolError("hit-count filters are not offered on the N64 debugger (use a condition)");
        if (tracepoint || !string.IsNullOrWhiteSpace(log)) throw new ToolError("tracepoints (log lines) are not offered on the N64 debugger; use trace / trace_frame");
        int id = AddBp(spec, kind, condition, CpuId(cpu));
        var bp = D.ListBreakpoints().First(b => b.Id == id);
        return new JsonObject { ["id"] = id, ["text"] = bp.ToString(), ["note"] = "the N64 debugger is on now: frames run interpreted (slower) until the last breakpoint is removed" };
    }

    public override JsonObject ListBreakpoints(int hits) => new()
    {
        ["breakpoints"] = J.Arr(D.ListBreakpoints().Select(b => (JsonNode)new JsonObject
        {
            ["id"] = b.Id, ["cpu"] = b.Cpu == "rsp" ? "rsp" : "main", ["kind"] = b.Kind.ToString().ToLowerInvariant(), ["address"] = Hex32(b.Address),
            ["symbol"] = b.Cpu == "rsp" ? null : T.Symbols.Label(b.Address), ["condition"] = b.Condition, ["enabled"] = b.Enabled, ["hits"] = b.HitCount,
        })),
        ["debuggerOn"] = D.Enabled,
        ["lastStop"] = D.IsPaused ? StopJson(D.LastStop) : null,
        ["note"] = "the N64 debugger keeps hit counts, not a hit log",
    };

    public override JsonObject RemoveBreakpoint(int? id, bool all)
    {
        int n = 0;
        if (all) foreach (var b in D.ListBreakpoints()) { D.RemoveBreakpoint(b.Id); n++; }
        else if (id is int i) { if (!D.RemoveBreakpoint(i)) throw new ToolError($"no breakpoint {i}"); n = 1; }
        else throw new ToolError("give id, or all:true");
        DebugOffIfIdle();
        return new JsonObject { ["removed"] = n, ["left"] = D.ListBreakpoints().Count, ["debuggerOn"] = D.Enabled };
    }

    /// <summary>The N64's expressions: a register (cpu or "rsp.x"), [spec] a byte, {spec} a 32-bit big-endian word, h:spec a halfword, or a number.</summary>
    private long Eval(string expr)
    {
        string e = expr.Trim();
        if (e.Length > 2 && ((e[0] == '[' && e[^1] == ']') || (e[0] == '{' && e[^1] == '}')))
            return ReadBe(e[1..^1], e[0] == '[' ? 1 : 4);
        if (e.StartsWith("h:", StringComparison.OrdinalIgnoreCase)) return ReadBe(e[2..], 2);
        if (e.Equals("frame", StringComparison.OrdinalIgnoreCase)) return framesStarted;
        string c = "cpu", r = e;
        if (e.StartsWith("rsp.", StringComparison.OrdinalIgnoreCase)) { c = "rsp"; r = e[4..]; }
        if (T.TryGetRegister(c, r, out ulong v)) return (long)v;
        if (e.Length > 0 && (char.IsDigit(e[0]) || e[0] is '$' or '-' or '%')) return Num.Parse(e);
        throw new ToolError($"'{expr}': the N64 expressions are a register (v0, sp, pc, Status, f0, rsp.v0), [addr] (a byte), h:addr (a halfword), {{addr}} (a word, big-endian), frame or a number; no operators");
    }

    private long ReadBe(string spec, int width)
    {
        var (d, off, _) = Spec(spec);
        var b = new byte[width];
        if (T.ReadMemory(d.Name, off, b) != width) throw new ToolError($"'{spec}' cannot be read now");
        long v = 0; foreach (var x in b) v = (v << 8) | x; return v;
    }

    private JsonArray WatchValues() => J.Arr(watches.Select(w =>
    {
        try { long v = Eval(w); return (JsonNode)new JsonObject { ["expr"] = w, ["value"] = v, ["hex"] = "$" + v.ToString("X") }; }
        catch (ToolError e) { return new JsonObject { ["expr"] = w, ["error"] = e.Message }; }
    }));

    public override JsonObject Watch(string action, string? expr, int? index)
    {
        switch (action)
        {
            case "add":
                if (string.IsNullOrWhiteSpace(expr)) throw new ToolError("expr is needed");
                Eval(expr!);   // refuse a bad one now
                watches.Add(expr!); break;
            case "remove":
                if (index is int i && i >= 0 && i < watches.Count) watches.RemoveAt(i);
                else if (expr != null && watches.Remove(expr)) { }
                else throw new ToolError("remove needs index or the expr");
                break;
            case "clear": watches.Clear(); break;
            case "list": break;
            default: throw new ToolError("action is add, remove, clear or list");
        }
        return new JsonObject { ["watches"] = WatchValues() };
    }

    public override JsonObject Evaluate(string expr, string cpu)
    {
        long v = Eval(expr);
        return new JsonObject { ["expr"] = expr, ["value"] = v, ["hex"] = "$" + v.ToString("X") };
    }

    // ================================================================================================ stepping, call stack, registers

    public override JsonObject Step(string mode, int count, string cpu, string? to, long? frame, int? scanline)
    {
        long before = framesStarted;
        if (scanline != null) throw new ToolError("step to a scanline is not offered on the N64");
        if (frame != null) return RunUntil(null, "exec", null, frame, null, 1_000_000, cpu);
        if (!string.IsNullOrWhiteSpace(to)) return RunUntil(to, "exec", null, null, null, 600, cpu);
        string c = CpuId(cpu);
        count = Math.Clamp(count, 1, 1_000_000);
        DebugOn(); EnsureCanRun(); ApplyInput();
        StopInfo st;
        switch (mode)
        {
            case "into" or "instruction" or "in":
                st = D.Step(c, count); break;
            case "over":
                if (c != "cpu") throw new ToolError("step over is the VR4300's (the RSP has no calls to step over): use into");
                st = D.LastStop;
                for (int i = 0; i < count; i++) { st = D.StepOver(); if (st.Reason is not (StopReason.Step or StopReason.StepOver)) break; }
                break;
            case "out":
            {
                if (c != "cpu") throw new ToolError("step out is the VR4300's (the RSP's call stack is not tracked)");
                var stack = D.CallStack("cpu");
                if (stack.Count == 0) throw new ToolError("step out needs the call stack, which is empty (it is kept while the debugger is on: open_rom call_stack:true, or a breakpoint before the call)");
                int tmp = D.AddBreakpoint("cpu", BreakpointKind.Exec, stack[^1].Return);
                try { RunFrames(600); } finally { D.RemoveBreakpoint(tmp); }
                st = D.LastStop;
                var r = StopResult(st, before);
                if (st.BreakpointId == tmp && r["break"] is JsonObject b) { b["reason"] = "step out"; b.Remove("breakpoint"); }
                return r;
            }
            default: throw new ToolError($"mode '{mode}' is not offered on the N64 (into, over or out)");
        }
        return StopResult(st, before);
    }

    public override JsonObject CallStack(string cpu)
    {
        string c = CpuId(cpu);
        if (c == "rsp") throw new ToolError("the RSP's call stack is not tracked (its microcode has no jal/jr ra discipline cor64 can follow)");
        var st = D.CallStack("cpu");
        return new JsonObject
        {
            ["cpu"] = "main", ["depth"] = st.Count, ["tracked"] = D.Enabled,
            ["frames"] = J.Arr(st.Reverse().Select((f, i) => (JsonNode)new JsonObject
            {
                ["depth"] = i, ["kind"] = "call", ["caller"] = Hex32(f.CallSite), ["callerSymbol"] = T.Symbols.Label(f.CallSite), ["target"] = Hex32(f.Target),
                ["targetSymbol"] = f.Label ?? T.Symbols.Label(f.Target), ["returnsTo"] = Hex32(f.Return), ["sp"] = Hex32(f.Sp), ["text"] = f.ToString(),
            })),
            ["note"] = D.Enabled ? "jal / jalr / bal pushes, jr ra pops (innermost first); interrupts and exceptions are not frames" : "the call stack is kept only while the debugger is on (open_rom call_stack:true, a breakpoint or a step)",
        };
    }

    public override JsonObject Registers(string cpu)
    {
        string c = CpuId(cpu);
        var regs = new JsonObject();
        foreach (var r in T.GetRegisters(c)) regs[r.Name] = r.Bits <= 16 ? "$" + r.Value.ToString(r.Bits <= 8 ? "X2" : "X4") : r.Bits <= 32 ? "$" + ((uint)r.Value).ToString("X8") : r.Bits <= 48 ? "$" + r.Value.ToString("X12") : "$" + r.Value.ToString("X16");
        ulong pc = T.GetPC(c);
        var line = T.Disassemble(c, pc, 1);
        if (c == "rsp") return RspRegistersJson(regs, pc, line);
        return new JsonObject
        {
            ["cpu"] = c == "rsp" ? "rsp" : "main", ["pc"] = Hex32(pc), ["pcSymbol"] = c == "rsp" ? null : T.Symbols.Label(pc),
            ["instruction"] = line.Count == 1 ? line[0].Text : null, ["registers"] = regs, ["frame"] = framesStarted, ["parked"] = D.Enabled && D.IsPaused,
            ["note"] = "no scanline / dot / cycle on the N64 view (the VI line is the VI domain's V_CURRENT)",
        };
    }

    public override JsonObject SetRegister(string cpu, string name, long value)
    {
        string c = CpuId(cpu);
        if (c == "rsp" && N64DebugTarget.TryLane(name.Trim(), out _, out _)) name = name.Trim();
        else if (c == "rsp" && name.Length > 1 && (name[0] is 'v' or 'V') && int.TryParse(name[1..], out int vr) && vr is >= 0 and < 32)
            throw new ToolError($"an RSP vector register is set one 16-bit lane at a time: v{vr}.0 .. v{vr}.7 (lane 0 is the most significant halfword)");
        if (!T.TryGetRegister(c, name, out ulong old)) throw new ToolError($"no register '{name}' on {(c == "rsp" ? "the RSP" : "the VR4300")} (registers lists them)");
        ulong v = (ulong)value;
        if (c == "cpu" && name.Equals("pc", StringComparison.OrdinalIgnoreCase)) v = SymbolTable.Norm(v);
        if (!T.SetRegister(c, name, v)) throw new ToolError($"the register '{name}' is read-only here");
        T.TryGetRegister(c, name, out ulong now);
        return new JsonObject { ["cpu"] = c == "rsp" ? "rsp" : "main", ["name"] = name, ["before"] = "$" + old.ToString("X"), ["after"] = "$" + now.ToString("X") };
    }

    private static JsonObject TraceJson(TraceEntry e, N64DebugTarget t) => new()
    {
        ["pc"] = Hex32(e.Pc), ["word"] = e.Word.ToString("X8"),
        ["text"] = e.Cpu == "rsp" ? RspDisassembler.Disassemble(e.Word, (uint)e.Pc & 0xFFC, null) : R4300Disassembler.Disassemble(e.Word, e.Pc, t.Symbols.Count > 0 ? t.Symbols.Labeler : null),
        ["symbol"] = e.Cpu == "rsp" ? null : t.Symbols.Label(e.Pc),
        ["changed"] = string.Join(" ", e.Deltas.Select(d => $"{MipsNames.Gpr[d.reg]}={d.value:X}")),
    };

    public override JsonObject Trace(int count, string cpu)
    {
        if (CpuId(cpu) == "rsp") throw new ToolError("the trace ring is the VR4300's; trace_frame with cpu rsp traces the RSP for one frame");
        if (!D.Enabled) throw new ToolError("the N64 trace ring records only while the debugger is on (open_rom call_stack:true, a breakpoint or a step); then run and ask again");
        var fresh = D.StopTrace("cpu");
        D.StartTrace("cpu", TraceRing);
        if (fresh.Count > 0) lastTrace = fresh;
        var t = lastTrace.TakeLast(Math.Clamp(count, 1, 5000)).ToList();
        return new JsonObject
        {
            ["cpu"] = "main", ["lines"] = t.Count, ["trace"] = J.Arr(t.Select(e => (JsonNode)TraceJson(e, T))),
            ["note"] = fresh.Count == 0 ? "nothing ran since the last trace; these are the lines it returned then" : "oldest first; registers are the GPRs the instruction changed",
        };
    }

    public override JsonObject TraceFrame(long frame, string cpu, int lines, string? function, List<string> ranges)
    {
        if (!string.IsNullOrWhiteSpace(function) || ranges.Count > 0) throw new ToolError("function / ranges filters are not offered on the N64 trace (it traces the whole frame)");
        string c = CpuId(cpu);
        if (frame < framesStarted) throw new ToolError($"frame {frame} has passed (now {framesStarted}); the N64 has no step back: trace the next frame (frame {framesStarted})");
        if (frame > framesStarted) RunFrames(frame - framesStarted);
        DebugOn(); EnsureCanRun(); ApplyInput();
        bool hadRing = c == "cpu";
        if (c == "rsp") D.StartTaskLog(256); // [dbg_rsp] the frame's RSP tasks, with their microcode
        var all = D.TraceFrame(c, 1 << 21);
        var tasks = c == "rsp" ? D.StopTaskLog() : null;
        framesStarted++;
        DrainAudio();
        if (hadRing) D.StartTrace("cpu", TraceRing);
        DebugOffIfIdle();
        var shown = all.Take(Math.Clamp(lines, 1, 20000)).ToList();
        return new JsonObject
        {
            ["cpu"] = c == "rsp" ? "rsp" : "main", ["frame"] = frame, ["instructions"] = all.Count, ["lines"] = shown.Count,
            ["trace"] = J.Arr(shown.Select(e => (JsonNode)TraceJson(e, T))),
            ["tasks"] = tasks == null ? null : J.Arr(tasks.Select(x => (JsonNode)TaskJson(x))),
            ["note"] = all.Count >= (1 << 21) ? "the ring (2M instructions) filled: the first lines are lost" : all.Count > shown.Count ? $"the first {shown.Count} of {all.Count}" : null,
        };
    }

    public override JsonObject Disassemble(string? address, int count, string cpu)
    {
        string c = CpuId(cpu);
        ulong at = string.IsNullOrWhiteSpace(address) || address!.Trim().Equals("pc", StringComparison.OrdinalIgnoreCase) ? T.GetPC(c) : CodeAddress(address!, c);
        ulong pc = T.GetPC(c);
        var lines = T.Disassemble(c, at, Math.Clamp(count, 1, 2000));
        return new JsonObject
        {
            ["cpu"] = c == "rsp" ? "rsp" : "main", ["from"] = Hex32(at),
            ["lines"] = J.Arr(lines.Select(l => (JsonNode)new JsonObject
            {
                ["address"] = Hex32(l.Address), ["word"] = l.Word.ToString("X8"), ["text"] = l.Text, ["label"] = l.Label,
                ["flow"] = c == "cpu" ? DisasmR4300.Instance.ClassifyWord(l.Word).Flow.ToString().ToLowerInvariant() : null, ["pc"] = (uint)l.Address == (uint)pc ? true : null,
            })),
        };
    }

    // ================================================================================================ picture, sound

    public override ToolOut Screenshot(int scale)
    {
        int w = cs.FrameWidth, h = cs.FrameHeight;
        var (hash, colours) = Png.Stats(cs.Frame, w * h);
        var o = new ToolOut(new JsonObject { ["width"] = w, ["height"] = h, ["displayWidth"] = cs.DisplayWidth, ["hash"] = hash, ["colours"] = colours, ["frame"] = framesStarted, ["ppu"] = cs.Core.Ppu.Id });
        o.Pngs.Add(Png.FromArgb(cs.Frame, w, h, Math.Clamp(scale, 1, 4)));
        return o;
    }

    public override ToolOut Layers(List<string> layers, int scale) =>
        throw new ToolError("the N64 has no picture layers to switch: the RDP draws textured triangles straight into a framebuffer in RDRAM (read it with read_memory, or take a screenshot)");
    public override ToolOut Tiles(string? source, int bpp, int palette, long start, int count, int scale) =>
        throw new ToolError("the N64 has no tile VRAM: textures are loaded into the RDP's TMEM per draw (no fixed tile sheet to show)");
    public override ToolOut Sprites(bool draw, int scale) =>
        throw new ToolError("the N64 has no sprites or OAM: everything on screen is RDP triangles and rectangles");

    public override JsonObject RecordSound(double seconds, bool wav)
    {
        seconds = Math.Clamp(seconds, 0.1, 60);
        long before = framesStarted;
        rec = new List<short>();
        string outcome = RunFrames((long)Math.Ceiling(seconds * cs.FramesPerSecond));
        var s = rec; rec = null;
        int rate = cs.SampleRate;
        double peak = 0, sum = 0;
        foreach (short v in s) { double a = Math.Abs(v / 32768.0); peak = Math.Max(peak, a); sum += a * a; }
        var j = RunResult(outcome, before);
        j["sampleRate"] = rate; j["channels"] = 2; j["samples"] = s.Count / 2;
        j["peak"] = Math.Round(peak, 4); j["rms"] = s.Count > 0 ? Math.Round(Math.Sqrt(sum / s.Count), 4) : 0;
        if (wav && s.Count > 0)
        {
            Directory.CreateDirectory(folder);
            string p = Path.Combine(folder, $"sound-{framesStarted}.wav");
            Wav.Write(p, s.ToArray(), s.Count, rate, 2);
            j["wav"] = p;
        }
        return j;
    }

    // ================================================================================================ states

    public override JsonObject SaveState(string name)
    {
        if (D.Enabled && D.IsPaused) throw new ToolError("the machine is stopped inside a frame (a break or a step): run on to the end of the frame first (run 1), then save");
        states[name] = (cs.SaveState(), framesStarted);
        return new JsonObject { ["saved"] = name, ["frame"] = framesStarted, ["bytes"] = states[name].state.Length, ["states"] = J.Strs(states.Keys) };
    }

    public override JsonObject LoadState(string name)
    {
        if (!states.TryGetValue(name, out var s)) throw new ToolError($"no state '{name}' (saved: {string.Join(", ", states.Keys)})");
        try { cs.LoadState(s.state); }
        catch (InvalidDataException e) { throw new ToolError(e.Message); }
        framesStarted = s.frames;
        schedule.Clear();
        if (D.Enabled) { D.Enabled = false; D.Enabled = true; D.StartTrace("cpu", TraceRing); }   // the call stack starts again from here
        return Status();
    }

    public override JsonObject DiffStates(string a, string b, int limit) =>
        throw new ToolError("state diffs by symbol are not offered on the N64 (its states are cor64 streams); compare RDRAM with read_memory / search_memory changed instead");

    // ================================================================================================ refused, with reasons

    public override JsonObject Checks(string action, List<string> off, List<string> wait, long from, int top) =>
        throw new ToolError("the D4 hardware checks and the profiler have no N64 rules");
    public override JsonObject StepBack(long? instructions, long? frames) =>
        throw new ToolError("the N64 has no step back: its states are about 8 MB each, so there is no history ring (save_state / load_state by name instead)");
    public override JsonObject MovieSave(string? path) =>
        throw new ToolError("movies (.bnm) are not offered on the N64 (they need the history ring)");
    public override JsonObject DebugPort(string action, long since, int limit, List<string> kinds) =>
        throw new ToolError("the debug port is the NES's, SNES / WideMess's and Game Boy's; the N64 has none (ISViewer output is not captured here)");
    public override JsonObject TimeflowRecord(string action, string? note) =>
        throw new ToolError("Timeflow does not cover the N64 (a frame's state is about 8 MB; a delta format is a known gap)");
    public override JsonObject TimeflowJump(string timelineDir, long frame) => throw new ToolError("Timeflow does not cover the N64");
    public override JsonObject TimeflowResume(string mode, long frames, bool record) => throw new ToolError("Timeflow does not cover the N64");

    private static string Hex32(ulong a) => "$" + ((uint)a).ToString("X8");

    // ================================================================================================ [dbg_rsp] RSP view

    private static string H(uint v) => "$" + v.ToString("X8");

    private static JsonObject UcodeJson(UcodeInfo? u) => u == null ? new JsonObject { ["name"] = null } : new JsonObject
    {
        ["name"] = u.Name, ["family"] = u.Family, ["identifiedBy"] = u.Source, ["crc"] = u.Crc.ToString("X8"), ["version"] = u.VersionString,
    };

    private static JsonObject TaskJson(RspTaskRecord x) => new()
    {
        ["index"] = x.Index, ["type"] = OSTaskInfo.TypeName(x.Type), ["hle"] = x.Hle, ["ucode"] = UcodeJson(x.UcodeId),
        ["ucodeAddress"] = H(x.Ucode), ["ucodeSize"] = H(x.UcodeSize), ["dataPtr"] = x.Task == null ? null : H(x.Task.DataPtr), ["dataSize"] = x.Task == null ? null : H(x.Task.DataSize),
    };

    private static JsonObject OSTaskJson(OSTaskInfo t)
    {
        var o = new JsonObject { ["typeName"] = OSTaskInfo.TypeName(t.Type) };
        for (int i = 0; i < OSTaskInfo.FieldNames.Length; i++) o[OSTaskInfo.FieldNames[i]] = H(t[i]);
        return o;
    }

    private JsonObject RspStatusJson()
    {
        var t = T.GetOSTask();
        var st = T.GetRspState();
        return new JsonObject
        {
            ["ucode"] = UcodeJson(T.CurrentUcode()), ["task"] = OSTaskInfo.TypeName(t.Type), ["pc"] = Hex32(st.Pc),
            ["spStatus"] = H(st.SpStatus), ["flags"] = J.Strs(st.StatusFlags()), ["semaphore"] = H(st.Semaphore),
        };
    }

    private JsonObject RspRegistersJson(JsonObject regs, ulong pc, IReadOnlyList<DisasmLine> line)
    {
        var st = T.GetRspState();
        var vec = new JsonObject();
        for (int r = 0; r < 32; r++) vec["v" + r] = st.VectorHex(r);
        var acc = new JsonArray();
        for (int i = 0; i < 8; i++) acc.Add("$" + st.Acc[i].ToString("X12"));
        return new JsonObject
        {
            ["cpu"] = "rsp", ["pc"] = Hex32(pc), ["instruction"] = line.Count == 1 ? line[0].Text : null, ["registers"] = regs,
            ["vectors"] = vec, ["accumulator"] = acc,
            ["flags"] = new JsonObject { ["vco"] = "$" + st.Vco.ToString("X4"), ["vcc"] = "$" + st.Vcc.ToString("X4"), ["vce"] = "$" + st.Vce.ToString("X2") },
            ["div"] = new JsonObject { ["in"] = H((uint)st.DivIn), ["out"] = H((uint)st.DivOut), ["doublePrecision"] = st.DivDoublePrecision },
            ["spStatus"] = H(st.SpStatus), ["spFlags"] = J.Strs(st.StatusFlags()), ["semaphore"] = H(st.Semaphore),
            ["osTask"] = OSTaskJson(st.Task), ["ucode"] = UcodeJson(T.CurrentUcode()),
            ["frame"] = framesStarted, ["parked"] = D.Enabled && D.IsPaused,
            ["note"] = "vectors: lanes 0..7, lane 0 the most significant halfword; set one with set_register name v3.5 (16-bit); the accumulator, flags, DIV and SP registers are read-only here. a graphics task run HLE (trace_frame tasks: hle true) does not execute on the RSP, so the vector state is that of the last task the RSP itself ran",
        };
    }
}
