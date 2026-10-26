using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NesEmulator;
using NesEmulator.Corruption;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Checks;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;

namespace BrokenNes.Mcp;

/// <summary>
/// A game in this process on the Workshop engine: the NES (the FIX cores by default, as the desktop app) or a console session (SNES,
/// WideMess, Game Boy / Color, Master System, Game Gear, Genesis), with the D1 debugger (NES, SNES with its SPC700, WideMess, Game Boy) and
/// its <see cref="DebugRunner"/>, which runs frames on its own thread so a breakpoint can park it mid-frame. Every call is synchronous, so
/// a session is deterministic: the same calls give the same machine. Nothing here touches a file of the user's (no saves, no config).
/// </summary>
internal sealed class HeadlessSession : Session
{
    public override string Kind => "headless";
    public override string ConsoleKey => Consoles.Key(console);

    private readonly ConsoleKind console;
    private readonly string romName, romPath;
    private NES? nes;
    private APU_FIX? fix;
    private IConsoleSession? cs;
    private DebugMachine? M;
    private Debugger? D;
    private DebugRunner? R;
    private string cores = "";
    private readonly string folder;

    // input: what each pad holds, and changes scheduled by frame (the frame index counts frames started in this session)
    private readonly object padLock = new();
    private readonly PadButtons[] held = new PadButtons[2];
    private readonly SortedDictionary<long, List<(int player, PadButtons b)>> schedule = new();
    private long framesStarted;

    // sound
    private readonly short[] sbuf = new short[65536];
    private List<short>? rec;
    private int recChannels = 1;

    private readonly Dictionary<string, Snapshot> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> watches = new();
    private ConsoleChecks? checks;
    private long checksFrom;
    // the debug port (D7): listening from power-on; an assert or a break command parks the machine with the message
    private NesEmulator.Debugging.Port.DebugPort? port;
    private long portReported;
    private SearchState? search;
    // the history (D5): every frame's input since the session began (its movie), a state every 60 frames (step back), the keyframes
    private DebugHistory? history;
    private string historyNote = "";
    private Movie? playingMovie;
    private string playingFile = "";

    private sealed class Snapshot
    {
        public byte[] State = Array.Empty<byte>();
        public Dictionary<int, byte[]> Spaces = new();
        public long Frame, FramesStarted;
        public DateTime TakenUtc = DateTime.UtcNow;
    }

    private sealed class SearchState
    {
        public string Domain = ""; public int Width; public List<int> Candidates = new(); public byte[] Previous = Array.Empty<byte>();
    }

    private HeadlessSession(string id, ConsoleKind console, string romName, string romPath)
    {
        Id = id; this.console = console; this.romName = romName; this.romPath = romPath;
        folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrokenNes", "mcp", "sessions", $"{Environment.ProcessId}-{id}");
    }

    public string Folder { get { Directory.CreateDirectory(folder); return folder; } }
    public bool HasDebugger => D != null;

    // ================================================================================================ opening

    public static HeadlessSession Open(string id, string path, string? consoleKey, string cpu, string ppu, string apu, IEnumerable<string> symbolFiles, bool callStack, bool startChecks, List<string> checksOff, List<string> frameWait)
    {
        if (!File.Exists(path)) throw new ToolError($"no ROM at '{path}'");
        byte[] raw = File.ReadAllBytes(path);
        byte[] rom = RomDetect.Unwrap(raw, path, out string inner);
        ConsoleKind? want = string.IsNullOrWhiteSpace(consoleKey) ? null : ParseConsole(consoleKey!);
        var c = want ?? RomDetect.Detect(rom, inner) ?? throw new ToolError($"{Path.GetFileName(path)}: not a ROM BrokenNes recognises (give console: nes, snes, wms, gb, gbc, sms, gg, md)");
        var h = new HeadlessSession(id, c, Path.GetFileName(inner), Path.GetFullPath(path));
        try
        {
            if (c == ConsoleKind.Nes)
            {
                var n = new NES { RomName = Path.GetFileName(inner), RomPath = Path.GetFullPath(path) };
                n.LoadROM(rom);
                string ci = cpu.Length > 0 ? cpu : "FIX", pi = ppu.Length > 0 ? ppu : "FIX", ai = apu.Length > 0 ? apu : "FIX";
                // a refused core is an error, never a quiet fallback
                if (!n.SetCpuCore(ci)) throw new ToolError($"the NES CPU core '{ci}' did not apply");
                if (!n.SetPpuCore(pi)) throw new ToolError($"the NES PPU core '{pi}' did not apply");
                if (!n.SetApuCore(ai)) throw new ToolError($"the NES APU core '{ai}' did not apply");
                h.nes = n;
                h.fix = n.GetApuStateProbe() as APU_FIX;
                h.cores = $"{n.GetCpuCoreId()}/{n.GetPpuCoreId()}/{n.GetApuCoreId()}";
            }
            else
            {
                string Pick(CoreSlot slot, string idWanted)
                {
                    if (idWanted.Length == 0) return CoreCatalog.Default(c, slot);
                    var opt = CoreCatalog.Options(c, slot).FirstOrDefault(o => o.Id.Equals(idWanted, StringComparison.OrdinalIgnoreCase));
                    return opt?.Id ?? throw new ToolError($"the {Consoles.DisplayName(c)} {slot} menu has no core '{idWanted}' (it has: {string.Join(", ", CoreCatalog.Options(c, slot).Select(o => o.Id).Take(30))})");
                }
                string ci = Pick(CoreSlot.Cpu, cpu), pi = Pick(CoreSlot.Ppu, ppu), ai = Pick(CoreSlot.Apu, apu);
                h.cs = ConsoleSessions.Create(c, rom, ci, pi, ai, null, Path.GetFileName(inner));
                h.cores = $"{ci}/{pi}/{ai}";
            }
            try { h.M = h.nes != null ? DebugMachines.For(h.nes) : DebugMachines.For(h.cs!); }
            catch (NotSupportedException) { h.M = null; }   // a Sega console: frames, input, memory, picture and sound, no debugger yet
            if (h.M != null)
            {
                h.D = new Debugger(h.M) { TraceRingSize = 65536 };
                if (callStack) h.D.TrackCallStack = true;   // an interactive session: the call stack (and the trace ring) from the first instruction
                h.R = new DebugRunner(h.D, h.Frame);
                foreach (var f in symbolFiles) h.D.Symbols.Load(f, h.M.Console);
                h.port = new NesEmulator.Debugging.Port.DebugPort(h.M, h.D) { BreakOnAssert = true, BreakOnBreak = true, Keep = 4096 };
                h.port.Attach();
                if (startChecks) h.Checks("start", checksOff, frameWait, 0, 15);
            }
            else if (symbolFiles.Any() || startChecks)
                throw new ToolError($"the debugger and the checks do not cover the {Consoles.DisplayName(c)} yet (D1 and D4: NES, SNES with its SPC700, WideMess, Game Boy)");
            h.StartHistory(null);
        }
        catch { h.Close(); throw; }
        return h;
    }

    public static ConsoleKind ParseConsole(string key)
    {
        string k = key.Trim().ToLowerInvariant();
        if (k is not ("nes" or "famicom" or "snes" or "sfc" or "gb" or "gameboy" or "gbc" or "gameboycolor" or "sms" or "mastersystem" or "gg" or "gamegear" or "md" or "genesis" or "megadrive" or "wms" or "widemess"))
            throw new ToolError($"unknown console '{key}' (nes, snes, wms, gb, gbc, sms, gg, md)");
        return k is "nes" or "famicom" ? ConsoleKind.Nes : Consoles.FromKey(k);
    }

    // ================================================================================================ the frame (on the runner's thread)

    private void Frame()
    {
        ApplyInput();
        if (nes != null) nes.RunFrame(); else cs!.RunFrame();
        Interlocked.Increment(ref framesStarted);
        DrainAudio();
        history?.AfterFrame();
    }

    private byte[] SaveRaw() => nes != null ? Encoding.UTF8.GetBytes(nes.SaveState()) : cs!.SaveState();
    private void LoadRaw(byte[] s) { if (nes != null) nes.LoadState(Encoding.UTF8.GetString(s)); else cs!.LoadState(s); }
    private ulong MoviePicture() => nes != null ? MovieHash.Picture(nes.GetFrameBuffer()) : MovieHash.Picture(cs!.Frame, cs.FrameWidth * cs.FrameHeight);
    private ulong MovieRam() => MovieHash.Ram(nes != null ? MachineDomains.For(nes) : MachineDomains.For(cs!));

    /// <summary>The history from now (null: power-on; else the state it starts from). A console without save states has none, and says so.</summary>
    private void StartHistory(byte[]? startState)
    {
        if (cs != null && !cs.SupportsState) { history = null; historyNote = $"no history (step back, movies): {cs.StateUnsupportedReason}"; return; }
        history = new DebugHistory(SaveRaw, D, 60, 30, 60, () => (MoviePicture(), MovieRam()), startState);
        historyNote = startState == null ? "the history starts at power-on" : "the history (and the session's movie) starts from the state loaded";
        playingMovie = null;
    }

    private void ApplyInput()
    {
        PadButtons p0, p1;
        lock (padLock)
        {
            long now = Interlocked.Read(ref framesStarted);
            while (schedule.Count > 0)
            {
                var first = schedule.First();
                if (first.Key > now) break;
                foreach (var (pl, b) in first.Value) held[pl] = b;
                schedule.Remove(first.Key);
            }
            p0 = held[0]; p1 = held[1];
        }
        if (history != null) (p0, p1) = history.BeforeFrame(p0, p1);   // the log's input while replaying (after a step back, or a movie)
        if (nes != null) nes.SetInputs(NesPad(p0), NesPad(p1));
        else { cs!.SetPad(0, p0); cs.SetPad(1, p1); }
    }

    private static bool[] NesPad(PadButtons b) => new[]
    {
        b.HasFlag(PadButtons.A), b.HasFlag(PadButtons.B), b.HasFlag(PadButtons.Select), b.HasFlag(PadButtons.Start),
        b.HasFlag(PadButtons.Up), b.HasFlag(PadButtons.Down), b.HasFlag(PadButtons.Left), b.HasFlag(PadButtons.Right),
    };

    private void DrainAudio()
    {
        if (nes != null)
        {
            float[] chunk = fix != null ? (fix.GetQueuedSampleCount() is int q && q > 0 ? fix.GetAudioSamples(q) : Array.Empty<float>()) : nes.GetAudioBuffer();
            var r = rec;
            if (r != null) foreach (float f in chunk) r.Add((short)Math.Clamp(f * 32767f, -32768f, 32767f));
            return;
        }
        int n;
        while ((n = cs!.ReadSamples(sbuf)) > 0)
        {
            var r = rec;
            if (r != null) for (int i = 0; i < n; i++) r.Add(sbuf[i]);
        }
    }

    private int SampleRate => nes != null ? (fix != null ? fix.GetSampleRate() : nes.GetAudioSampleRate()) : cs!.SampleRate;
    private double Fps => nes != null ? 60.0988 : cs!.FramesPerSecond;

    // ================================================================================================ helpers

    private Debugger Dbg => D ?? throw new ToolError($"the debugger does not cover the {Consoles.DisplayName(console)} yet (D1: NES, SNES with its SPC700, WideMess, Game Boy)");

    private DebugCpu CpuOf(string id)
    {
        var m = M ?? throw new ToolError($"the debugger does not cover the {Consoles.DisplayName(console)} yet");
        return m.Cpu(string.IsNullOrWhiteSpace(id) ? "main" : id) ?? throw new ToolError($"no CPU '{id}' (this {m.Console} has: {string.Join(", ", m.Cpus.Select(c => c.Id))})");
    }

    private IReadOnlyList<IMemoryDomain> AllDomains() => M != null ? M.Domains() : nes != null ? MachineDomains.For(nes) : MachineDomains.For(cs!);

    private IMemoryDomain Domain(string id)
    {
        var all = AllDomains();
        return all.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => d.Label.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? (id.Equals("System RAM", StringComparison.OrdinalIgnoreCase) || id.Equals("wram", StringComparison.OrdinalIgnoreCase) && nes != null ? all.FirstOrDefault(d => d.Id == "RAM") : null)
            ?? throw new ToolError($"no memory domain '{id}' (this {ConsoleKey} has: {string.Join(", ", all.Select(d => d.Id))})");
    }

    private long Frames => M?.Frame ?? Interlocked.Read(ref framesStarted);

    /// <summary>A parked machine is stopped inside a frame; states, resets and the layer view need it between frames. Runs the rest of the frame with the breakpoints off.</summary>
    private string? EnsureBetweenFrames()
    {
        if (D == null || !D.IsParked) return null;
        long f0 = M!.Frame;
        for (int tries = 0; tries < 4 && D.IsParked; tries++)
        {
            var on = D.Breakpoints.Where(b => b.Enabled).Select(b => b.Id).ToList();
            foreach (var id in on) D.SetEnabled(id, false);
            bool pa = port?.BreakOnAssert ?? false, pb = port?.BreakOnBreak ?? false;
            if (port != null) { port.BreakOnAssert = false; port.BreakOnBreak = false; }   // the debug port's asserts too
            try { R!.Run(1); }
            finally { foreach (var id in on) D.SetEnabled(id, true); if (port != null) { port.BreakOnAssert = pa; port.BreakOnBreak = pb; } }
        }
        if (D.IsParked) throw new ToolError("the machine is parked and a step keeps it there; run some frames first");
        return $"the machine was stopped inside frame {f0}; that frame was finished first (breakpoints off for it)";
    }

    /// <summary>After the machine jumped (a state load, a reset): the call stack and the hooks start again from here.</summary>
    private void FreshDebugState()
    {
        if (D == null) return;
        var on = D.Breakpoints.Where(b => b.Enabled).Select(b => b.Id).ToList();
        var obs = D.Observers.ToArray();
        bool track = D.TrackCallStack;
        foreach (var id in on) D.SetEnabled(id, false);
        foreach (var o in obs) D.RemoveObserver(o);
        D.TrackCallStack = false;
        D.TrackCallStack = track;
        foreach (var o in obs) D.AddObserver(o);
        foreach (var id in on) D.SetEnabled(id, true);
    }

    private JsonObject RunResult(RunOutcome o, long framesBefore)
    {
        var j = new JsonObject
        {
            ["outcome"] = o.ToString().ToLowerInvariant(),
            ["frame"] = Frames,
            ["framesRun"] = Interlocked.Read(ref framesStarted) - framesBefore,
            ["parked"] = D?.IsParked ?? false,
        };
        if (o == RunOutcome.Break && D?.LastBreak is { } b) j["break"] = BreakJson(b);
        if (o == RunOutcome.Error) j["error"] = R?.Error?.Message;
        if (o == RunOutcome.Timeout) j["note"] = "the call's time limit (300 s) passed before the run ended: the machine was paused where it was (run again to go on)";
        if (watches.Count > 0) j["watches"] = WatchValues();
        if (checks != null) j["checkFindings"] = checks.Findings.Count;
        PortNews(j);
        return j;
    }

    /// <summary>The debug port's events since the last result (at most 40 here; the debug_port tool has them all).</summary>
    private void PortNews(JsonObject j)
    {
        var p = port;
        if (p == null) return;
        var news = p.Since(portReported);
        if (news.Count == 0) return;
        portReported = news[^1].Seq;
        j["debugPort"] = new JsonObject
        {
            ["new"] = news.Count,
            ["events"] = J.Arr(news.TakeLast(40).Select(e => (JsonNode)PortEventJson(e))),
            ["asserts"] = news.Count(e => e.Kind == NesEmulator.Debugging.Port.PortEventKind.Assert),
            ["note"] = news.Count > 40 ? $"the last 40 of {news.Count} new events (debug_port action events has them all)" : null,
        };
    }

    private static JsonObject PortEventJson(NesEmulator.Debugging.Port.PortEvent e) => new()
    {
        ["seq"] = e.Seq, ["kind"] = e.KindName, ["text"] = e.Text, ["frame"] = e.Frame, ["pc"] = e.PcText, ["pcSymbol"] = e.PcSymbol,
    };

    public override JsonObject DebugPort(string action, long since, int limit, List<string> kinds)
    {
        var p = port ?? throw new ToolError($"no debug port on this session: the debugger does not cover the {Consoles.DisplayName(console)}");
        limit = Math.Clamp(limit, 1, 4096);
        switch (action)
        {
            case "events":
            {
                var ev = p.Since(since).Where(e => kinds.Count == 0 || kinds.Contains(e.KindName, StringComparer.OrdinalIgnoreCase)).ToList();
                if (ev.Count > 0) portReported = Math.Max(portReported, ev[^1].Seq);
                return new JsonObject
                {
                    ["where"] = p.Where, ["total"] = p.EventCount, ["matching"] = ev.Count, ["events"] = J.Arr(ev.TakeLast(limit).Select(e => (JsonNode)PortEventJson(e))),
                    ["note"] = ev.Count > limit ? $"the last {limit} of {ev.Count}" : (p.EventCount > p.Keep ? $"the port keeps the last {p.Keep} events; asserts and markers are all kept (action status)" : null),
                };
            }
            case "timers":
                return new JsonObject { ["cycleUnit"] = p.CycleUnit, ["timers"] = J.Arr(p.Timers.Select(t => (JsonNode)t.Json())), ["text"] = J.Strs(p.TimerText()) };
            case "status":
                return p.Json(Math.Min(limit, 50));
            case "clear":
                p.Clear(); portReported = 0;
                return new JsonObject { ["cleared"] = true };
            default: throw new ToolError("action is events, timers, status or clear");
        }
    }

    private JsonObject BreakJson(BreakInfo b)
    {
        var c = M!.Cpu(b.CpuId) ?? M.Main;
        var j = new JsonObject
        {
            ["reason"] = b.Reason, ["cpu"] = b.CpuId, ["pc"] = c.FormatAddress(b.Pc), ["pcSymbol"] = b.PcSymbol,
            ["frame"] = b.Frame, ["scanline"] = b.Scanline, ["dot"] = b.Dot, ["cycle"] = b.Cycle, ["text"] = b.Text,
        };
        if (b.BreakpointId is int id) j["breakpoint"] = id;
        if (b.Reason is "watch" or "breakpoint" && b.Kind != AccessKind.Exec && b.Kind != AccessKind.None)
        {
            j["access"] = Breakpoint.KindText(b.Kind); j["address"] = c.FormatAddress(b.Address); j["value"] = Num.Hex(b.Value);
            j["source"] = b.Source.ToString(); j["accessPc"] = c.FormatAddress(b.AccessPc); j["accessPcSymbol"] = D!.SymbolAt(c, b.AccessPc);
        }
        j["instruction"] = DisasmLine(c, c.PC)["text"]?.GetValue<string>();
        var t = D!.TraceTail(1, b.CpuId);
        if (t.Length > 0) j["lastExecuted"] = D.FormatTrace(t[0], b.CpuId);
        return j;
    }

    private JsonArray WatchValues()
    {
        var a = new JsonArray();
        foreach (var w in watches)
        {
            try { long v = D != null ? D.Evaluate(w) : 0; a.Add(new JsonObject { ["expr"] = w, ["value"] = v, ["hex"] = "$" + v.ToString("X") }); }
            catch (Exception e) { a.Add(new JsonObject { ["expr"] = w, ["error"] = e.Message }); }
        }
        return a;
    }

    private RunOutcome RunFrames(long frames)
    {
        frames = Math.Clamp(frames, 0, 1_000_000);
        if (frames == 0) return RunOutcome.Frames;
        if (R != null)
        {
            var o = R.Run(frames, 300_000);
            // a run past the call's time limit is stopped where it is (parked at an instruction), so the session is still between calls
            if (o == RunOutcome.Timeout) { D!.Pause(); D.WaitForPark(10_000); }
            return o;
        }
        for (long i = 0; i < frames; i++) Frame();
        return RunOutcome.Frames;
    }

    private RunOutcome StepCmd(Action cmd, long guard) => R!.Step(cmd, Math.Max(1, guard), 300_000);

    // ================================================================================================ describe / status

    public override JsonObject Describe()
    {
        var j = new JsonObject
        {
            ["session"] = Id, ["kind"] = Kind, ["console"] = ConsoleKey, ["consoleName"] = Consoles.DisplayName(console),
            ["rom"] = romName, ["romPath"] = romPath, ["cores"] = cores, ["description"] = nes != null ? $"NES {cores}" : cs!.Description,
            ["debugger"] = D != null, ["frame"] = Frames, ["files"] = folder,
        };
        if (M != null)
        {
            j["cpus"] = J.Arr(M.Cpus.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["spaces"] = J.Strs(c.Spaces.Select(s => s.Name)) }));
            j["limitations"] = J.Strs(M.Limitations);
            j["symbols"] = D!.Symbols.All.Count;
            j["symbolFiles"] = J.Strs(D.Symbols.Files);
        }
        else j["limitations"] = J.Strs(new[] { $"no debugger for the {Consoles.DisplayName(console)} yet: frames, input, memory domains, the picture, sound and states work" });
        j["domains"] = J.Strs(AllDomains().Select(d => d.Id));
        j["checks"] = checks != null;
        if (cs != null) j["states"] = cs.SupportsState; else j["states"] = true;
        return j;
    }

    public override JsonObject Status()
    {
        var j = Describe();
        j["framesRun"] = Interlocked.Read(ref framesStarted);
        j["parked"] = D?.IsParked ?? false;
        if (D?.IsParked == true && D.LastBreak is { } b) j["break"] = BreakJson(b);
        lock (padLock) j["held"] = J.Strs(new[] { held[0].ToString(), held[1].ToString() });
        if (watches.Count > 0) j["watches"] = WatchValues();
        if (checks != null) j["checkFindings"] = checks.Findings.Count;
        if (port != null) j["debugPort"] = new JsonObject { ["events"] = port.EventCount, ["asserts"] = port.Count(NesEmulator.Debugging.Port.PortEventKind.Assert), ["markers"] = port.Count(NesEmulator.Debugging.Port.PortEventKind.Marker), ["timers"] = port.Timers.Count };
        j["savedStates"] = J.Strs(states.Keys);
        j["history"] = history == null ? historyNote : $"{historyNote}; {history.FrameIndex} frames, step back to frame {history.OldestFrame}{(history.Replaying ? $"; replaying the log ({history.Logged - history.FrameIndex} frames ahead)" : "")}{(history.SaveError != null ? "; " + history.SaveError : "")}";
        if (playingMovie != null && history != null)
            j["movie"] = new JsonObject { ["file"] = playingFile, ["frames"] = playingMovie.Frames, ["keyframesChecked"] = history.KeysChecked, ["keyframesMatched"] = history.KeysMatched, ["offCourseAfter"] = history.OffCourseFrame, ["offCourse"] = history.OffCourseFrame != null ? history.OffCourseWhat : null };
        return j;
    }

    public override void Close()
    {
        try { port?.Detach(); } catch { }
        port = null;
        try { R?.Dispose(); } catch { }
        try { cs?.Dispose(); } catch { }
        R = null; D = null;
    }

    // ================================================================================================ running

    public override JsonObject Run(long frames, List<PadStep>? script)
    {
        long before = Interlocked.Read(ref framesStarted);
        if (script != null) { BranchHistory(); Schedule(script, 0); }
        var o = RunFrames(frames);
        return RunResult(o, before);
    }

    public override JsonObject RunUntil(string? spec, string kind, string? condition, long? frame, int? scanline, long maxFrames, string cpu)
    {
        var d = Dbg;
        long before = Interlocked.Read(ref framesStarted);
        maxFrames = Math.Clamp(maxFrames, 1, 1_000_000);
        if (frame is long f)
        {
            if (f <= M!.Frame) throw new ToolError($"frame {f} has passed (the machine is in frame {M.Frame}); load a state saved earlier, or open the ROM again");
            var o = StepCmd(() => d.RunToFrame(f), f - M.Frame + 2);
            var j = RunResult(o, before); j["reached"] = o == RunOutcome.Break && d.LastBreak?.Reason == "run-to" || M.Frame >= f; return j;
        }
        if (scanline is int line)
        {
            var o = StepCmd(() => d.RunToScanline(line), maxFrames);
            return RunResult(o, before);
        }
        if (!string.IsNullOrWhiteSpace(spec))
        {
            var bp = d.Add(spec!, ParseKind(kind), condition, null, false, null, cpu, temporary: true);
            var o = StepCmd(d.Continue, maxFrames);
            bool reached = o == RunOutcome.Break && d.LastBreak?.BreakpointId == bp.Id;
            if (d.Breakpoints.Any(x => x.Id == bp.Id)) d.Remove(bp.Id);
            var j = RunResult(o, before); j["reached"] = reached; j["target"] = bp.ToString();
            if (!reached && o == RunOutcome.Frames) j["note"] = $"not reached in {maxFrames} frames";
            return j;
        }
        if (!string.IsNullOrWhiteSpace(condition))
        {
            // a condition alone is tested at the end of every frame (an address with it tests it at every access instead)
            d.Evaluate(condition!, cpu);   // a bad expression is an error now, not after the run
            for (long i = 0; i < maxFrames; i++)
            {
                var o = RunFrames(1);
                if (o != RunOutcome.Frames) { var jb = RunResult(o, before); jb["reached"] = false; return jb; }
                if (d.Evaluate(condition!, cpu) != 0) { var j = RunResult(o, before); j["reached"] = true; j["condition"] = condition; return j; }
            }
            var jn = RunResult(RunOutcome.Frames, before); jn["reached"] = false; jn["note"] = $"'{condition}' stayed false for {maxFrames} frames (tested at each frame's end)";
            return jn;
        }
        throw new ToolError("run_until needs one of: frame, scanline, address (spec, with kind and an optional condition), or condition");
    }

    public override JsonObject Pause()
    {
        var d = Dbg;
        if (d.IsParked) { var j0 = RunResult(RunOutcome.Break, Interlocked.Read(ref framesStarted)); j0["note"] = "already stopped"; return j0; }
        long before = Interlocked.Read(ref framesStarted);
        var o = StepCmd(d.Pause, 2);
        var j = RunResult(o, before);
        j["note"] = "headless sessions are always still between calls; pause stops at the next instruction, so you can step from there";
        return j;
    }

    public override JsonObject Continue() => Run(600, null);

    public override JsonObject Reset()
    {
        string? note = EnsureBetweenFrames();
        if (nes != null) nes.Reset(); else cs!.Reset();
        FreshDebugState();
        try { StartHistory(SaveRaw()); } catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { history = null; historyNote = "no history after the reset: " + e.Message; }
        var j = Status(); j["reset"] = "the console's reset button (memory kept, the CPU restarted at its reset vector on the next frame); open the ROM again for a power-on";
        if (note != null) j["note"] = note;
        return j;
    }

    // ================================================================================================ input

    public static PadButtons ParseButtons(IEnumerable<string> names)
    {
        PadButtons p = PadButtons.None;
        foreach (var raw in names)
        {
            string n = raw.Trim();
            if (n.Length == 0) continue;
            string k = n.ToLowerInvariant() switch { "sel" => "Select", "u" => "Up", "d" => "Down", "l" => "Left", "r" => "Right", "pause" => "Select", "reset" => "Mode", _ => n };
            if (k is "l" or "r") k = k.ToUpperInvariant();
            if (!Enum.TryParse<PadButtons>(k, true, out var b) || b == PadButtons.None) throw new ToolError($"unknown button '{raw}' (A B X Y L R C Z Mode Start Select Up Down Left Right)");
            p |= b;
        }
        return p;
    }

    /// <summary>"f:Btn+Btn,f:,..." (frames relative to now).</summary>
    public static List<PadStep> ParseScript(string spec)
    {
        var list = new List<PadStep>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int c = part.IndexOf(':');
            if (c < 0 || !long.TryParse(part[..c], NumberStyles.Integer, CultureInfo.InvariantCulture, out long f) || f < 0) throw new ToolError($"bad pad step '{part}' (frame:Button+Button, e.g. 0:Start,10:,60:Right+A)");
            list.Add(new PadStep(f, part[(c + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }
        return list.OrderBy(s => s.Frame).ToList();
    }

    private void Schedule(List<PadStep> script, int player)
    {
        lock (padLock)
        {
            long now = Interlocked.Read(ref framesStarted);
            foreach (var s in script)
            {
                var b = ParseButtons(s.Buttons);
                if (!schedule.TryGetValue(now + s.Frame, out var l)) schedule[now + s.Frame] = l = new();
                l.Add((player, b));
            }
        }
    }

    public override JsonObject Input(string mode, string[] buttons, int frames, int player, List<PadStep>? script, long? runFrames)
    {
        if (player is < 0 or > 1) throw new ToolError("player is 1 or 2");
        long before = Interlocked.Read(ref framesStarted);
        BranchHistory();
        switch (mode)
        {
            case "hold":
            {
                var b = ParseButtons(buttons);
                lock (padLock) held[player] = b;
                var j = Status(); j["held"] = b.ToString(); j["note"] = "held until changed; they reach the game on the next frame run";
                return j;
            }
            case "release":
                lock (padLock) held[player] = PadButtons.None;
                return Status();
            case "press":
            {
                var b = ParseButtons(buttons);
                frames = Math.Clamp(frames, 1, 600);
                lock (padLock)
                {
                    held[player] = b;
                    long at = Interlocked.Read(ref framesStarted) + frames;
                    if (!schedule.TryGetValue(at, out var l)) schedule[at] = l = new();
                    l.Add((player, PadButtons.None));
                }
                var j = RunResult(RunFrames(frames), before);
                j["pressed"] = b.ToString(); j["heldFrames"] = frames;
                return j;
            }
            case "script":
            {
                if (script == null || script.Count == 0) throw new ToolError("script is needed: \"0:Start,10:,60:Right+A,120:\" (frames from now; an empty list releases)");
                Schedule(script, player);
                long n = runFrames ?? script.Max(s => s.Frame) + 1;
                var j = RunResult(RunFrames(n), before);
                j["steps"] = script.Count;
                return j;
            }
            default: throw new ToolError("mode is press, hold, release or script");
        }
    }

    // ================================================================================================ memory

    public override JsonObject Domains()
    {
        var j = new JsonObject
        {
            ["console"] = ConsoleKey,
            ["domains"] = J.Arr(AllDomains().Select(d => (JsonNode)new JsonObject
            {
                ["id"] = d.Id, ["label"] = d.Label, ["size"] = d.Size, ["kind"] = d.Kind.ToString(), ["writable"] = d.Writable, ["unavailable"] = d.Unavailable,
            })),
        };
        if (M != null)
            j["cpuSpaces"] = J.Arr(M.Cpus.Select(c => (JsonNode)new JsonObject
            {
                ["cpu"] = c.Id,
                ["spaces"] = J.Arr(c.Spaces.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["size"] = s.Size, ["aliases"] = J.Strs(s.Aliases), ["description"] = s.Description })),
            }));
        j["specs"] = "read_memory takes a domain + address, or a spec on a CPU: $0010, $8000-$80FF, 7E:0010 (bank:address), wram:$10, vram:$2000, spc:$0200 (cpu spc), a symbol, symbol+1";
        return j;
    }

    private (DebugCpu c, SpaceRange r, string resolved, int len) ResolveRead(string spec, string cpu, int? length)
    {
        var c = CpuOf(cpu);
        List<SpaceRange> ranges;
        string resolved;
        try { ranges = Debugger.ResolveSpec(c, D!.Symbols, spec, AccessKind.Read, out resolved); }
        catch (Exception e) when (e is FormatException or ArgumentException) { throw new ToolError(e.Message); }
        var r = ranges[0];
        int len = Math.Clamp(length ?? (int)Math.Min(256, r.End - r.Start + 1), 1, 65536);
        return (c, r, resolved, len);
    }

    public override JsonObject ReadMemory(string? spec, string? domain, long? address, int? length, string cpu)
    {
        byte[] data; var j = new JsonObject();
        if (!string.IsNullOrWhiteSpace(spec))
        {
            var (c, r, resolved, len) = ResolveRead(spec!, cpu, length);
            len = Math.Min(len, 4096);
            data = new byte[len];
            for (int i = 0; i < len; i++) data[i] = c.PeekSpace(r.Space, r.Start + (uint)i);
            j["spec"] = spec; j["resolved"] = resolved; j["cpu"] = c.Id; j["space"] = c.Spaces.First(s => s.Id == r.Space).Name; j["offset"] = Num.Hex(r.Start, 4);
            if (D!.Symbols.TryFind(spec!.Trim(), out var sym, out _)) j["symbol"] = sym.ToString();
        }
        else if (!string.IsNullOrWhiteSpace(domain))
        {
            var d = Domain(domain!);
            if (d.Unavailable != null) throw new ToolError($"{d.Id}: {d.Unavailable}");
            long a = address ?? 0;
            if (a < 0 || a >= d.Size) throw new ToolError($"{d.Id} is {d.Size} bytes; address {Num.Hex(a, 4)} is outside it");
            int len = (int)Math.Min(Math.Clamp(length ?? 16, 1, 4096), d.Size - a);
            data = new byte[len];
            for (int i = 0; i < len; i++) data[i] = d.Peek((int)a + i);
            j["domain"] = d.Id; j["offset"] = Num.Hex(a, 4);
        }
        else throw new ToolError("give a spec (an address, bank:address, space:address or symbol, on a CPU) or a domain and an address");
        j["length"] = data.Length;
        j["hex"] = Num.HexBytes(data);
        if (data.Length <= 8) { long v = 0; for (int i = data.Length - 1; i >= 0; i--) v = v << 8 | data[i]; j["value"] = v; j["valueHex"] = "$" + v.ToString("X" + data.Length * 2); }
        if (data.Length <= 64) j["bytes"] = new JsonArray(data.Select(b => (JsonNode)(int)b).ToArray());
        j["frame"] = Frames;
        return j;
    }

    public override JsonObject WriteMemory(string? spec, string? domain, long? address, byte[] data, string cpu)
    {
        if (data.Length == 0) throw new ToolError("data is needed (bytes: [1,2,3] or \"01 02 03\")");
        var j = new JsonObject();
        var before = new byte[data.Length]; var after = new byte[data.Length];
        if (!string.IsNullOrWhiteSpace(spec))
        {
            var (c, r, resolved, _) = ResolveRead(spec!, cpu, data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                uint o = r.Start + (uint)i;
                before[i] = c.PeekSpace(r.Space, o);
                if (!c.PokeSpace(r.Space, o, data[i])) throw new ToolError($"{resolved}+{i}: the debugger cannot write there (ROM or a register)");
                after[i] = c.PeekSpace(r.Space, o);
            }
            j["spec"] = spec; j["resolved"] = resolved;
        }
        else if (!string.IsNullOrWhiteSpace(domain))
        {
            var d = Domain(domain!);
            if (!d.Writable || d.Unavailable != null) throw new ToolError($"{d.Id} cannot be written here{(d.Unavailable != null ? ": " + d.Unavailable : "")}");
            long a = address ?? throw new ToolError("address is needed with a domain");
            if (a < 0 || a + data.Length > d.Size) throw new ToolError($"{d.Id} is {d.Size} bytes; {data.Length} bytes at {Num.Hex(a, 4)} do not fit");
            for (int i = 0; i < data.Length; i++) { before[i] = d.Peek((int)a + i); d.Poke((int)a + i, data[i]); after[i] = d.Peek((int)a + i); }
            j["domain"] = d.Id; j["offset"] = Num.Hex(a, 4);
        }
        else throw new ToolError("give a spec or a domain and an address");
        j["before"] = Num.HexBytes(before); j["after"] = Num.HexBytes(after); j["verified"] = after.SequenceEqual(data);
        return j;
    }

    public override JsonObject SearchMemory(string domain, string op, long? value, int width, bool fresh, int show)
    {
        var d = Domain(domain);
        if (d.Unavailable != null) throw new ToolError($"{d.Id}: {d.Unavailable}");
        if (d.Size > 4 * 1024 * 1024) throw new ToolError($"{d.Id} is {d.Size} bytes; search a smaller domain (work RAM, VRAM, ...)");
        if (width is not (1 or 2 or 4)) throw new ToolError("width is 1, 2 or 4 bytes");
        var now = new byte[d.Size];
        for (int i = 0; i < now.Length; i++) now[i] = d.Peek(i);
        long Val(byte[] m, int at) { long v = 0; for (int k = width - 1; k >= 0; k--) v = v << 8 | m[at + k]; return v; }
        bool start = fresh || search == null || search.Domain != d.Id || search.Width != width;
        if (start) search = new SearchState { Domain = d.Id, Width = width, Candidates = Enumerable.Range(0, Math.Max(0, d.Size - width + 1)).ToList(), Previous = now };
        var s = search!;
        bool needsValue = op is "eq" or "ne" or "gt" or "lt";
        if (needsValue && value == null) throw new ToolError($"op '{op}' needs a value");
        if (start && !needsValue && op != "all") { s.Previous = now; return new JsonObject { ["domain"] = d.Id, ["candidates"] = s.Candidates.Count, ["note"] = $"a new search starts from every address; run frames, then search again with op '{op}' to compare with now" }; }
        long target = value ?? 0;
        s.Candidates = s.Candidates.Where(a =>
        {
            long v = Val(now, a), p = Val(s.Previous, a);
            return op switch
            {
                "eq" => v == target, "ne" => v != target, "gt" => v > target, "lt" => v < target,
                "changed" => v != p, "unchanged" => v == p, "increased" => v > p, "decreased" => v < p,
                "all" => true,
                _ => throw new ToolError("op is eq, ne, gt, lt (with value), changed, unchanged, increased, decreased"),
            };
        }).ToList();
        s.Previous = now;
        var c = M?.Main;
        var spaceName = c?.FindSpace(d.Id)?.Name ?? (nes != null && d.Id == "RAM" ? "cpu" : null);
        var j = new JsonObject { ["domain"] = d.Id, ["op"] = op, ["value"] = value, ["width"] = width, ["candidates"] = s.Candidates.Count, ["frame"] = Frames };
        j["matches"] = J.Arr(s.Candidates.Take(Math.Clamp(show, 0, 500)).Select(a =>
        {
            var o = new JsonObject { ["offset"] = Num.Hex(a, 4), ["value"] = Val(now, a) };
            if (spaceName != null)
            {
                string spec = spaceName == "cpu" ? Num.Hex(a, 4) : $"{spaceName}:{Num.Hex(a, 4)}";
                o["spec"] = spec;
                if (spaceName == "cpu" && D != null && D.SymbolAt(c!, (uint)a) is { } sym) o["symbol"] = sym;
            }
            return (JsonNode)o;
        }));
        return j;
    }

    public override JsonObject Symbols(string? load, string? find, long? address, string? prefix, string cpu, int limit)
    {
        var d = Dbg; var c = CpuOf(cpu);
        var j = new JsonObject();
        if (!string.IsNullOrWhiteSpace(load))
        {
            if (!File.Exists(load)) throw new ToolError($"no symbol file at '{load}'");
            j["loaded"] = d.Symbols.Load(load!, M!.Console); j["file"] = load;
        }
        if (!string.IsNullOrWhiteSpace(find))
        {
            if (!d.Symbols.TryFind(find!, out var s, out string? err)) throw new ToolError(err ?? $"no symbol '{find}'");
            var o = new JsonObject { ["name"] = s.Name, ["domain"] = s.Domain, ["address"] = Num.Hex(s.Address, 4), ["bank"] = s.Bank, ["size"] = s.Size, ["sizeKnown"] = s.SizeKnown, ["file"] = Path.GetFileName(s.File), ["text"] = s.ToString() };
            if (c.BindSymbol(s, out var key))
            {
                o["space"] = c.Spaces.First(x => x.Id == key.Space).Name; o["offset"] = Num.Hex(key.Offset, 4);
                var b = new byte[Math.Min(s.Size, 64)];
                for (int i = 0; i < b.Length; i++) b[i] = c.PeekSpace(key.Space, key.Offset + (uint)i);
                o["hex"] = Num.HexBytes(b);
            }
            j["symbol"] = o;
        }
        if (address is long a) j["at"] = new JsonObject { ["address"] = c.FormatAddress((uint)a), ["label"] = d.SymbolAt(c, (uint)a) };
        if (prefix != null)
        {
            var hits = d.Symbols.All.Where(s => prefix == "*" || s.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            j["matches"] = hits.Count;
            j["symbols"] = J.Strs(hits.Take(Math.Clamp(limit, 1, 2000)).Select(s => s.ToString()));
        }
        j["total"] = d.Symbols.All.Count; j["files"] = J.Strs(d.Symbols.Files);
        return j;
    }

    // ================================================================================================ the debugger

    public static AccessKind ParseKind(string? k) => (k ?? "exec").Trim().ToLowerInvariant() switch
    {
        "exec" or "x" or "execute" or "" => AccessKind.Exec,
        "read" or "r" => AccessKind.Read,
        "write" or "w" => AccessKind.Write,
        "rw" or "access" or "readwrite" => AccessKind.ReadWrite,
        _ => throw new ToolError($"kind '{k}' is exec, read, write or rw"),
    };

    private static JsonObject BpJson(Breakpoint b) => new()
    {
        ["id"] = b.Id, ["cpu"] = b.CpuId, ["kind"] = Breakpoint.KindText(b.Kind), ["spec"] = b.Spec, ["resolved"] = b.Resolved, ["condition"] = b.Condition,
        ["hitMode"] = b.HitMode.ToString(), ["hitTarget"] = b.HitTarget, ["hits"] = b.Hits, ["tracepoint"] = b.Tracepoint, ["log"] = b.LogFormat, ["enabled"] = b.Enabled, ["text"] = b.ToString(),
    };

    public override JsonObject AddBreakpoint(string spec, string kind, string? condition, string? hits, bool tracepoint, string? log, string cpu)
    {
        var d = Dbg; CpuOf(cpu);
        try { var b = d.Add(spec, ParseKind(kind), condition, hits, tracepoint, log, string.IsNullOrWhiteSpace(cpu) ? "main" : cpu); return new JsonObject { ["breakpoint"] = BpJson(b) }; }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException) { throw new ToolError(e.Message); }
    }

    public override JsonObject ListBreakpoints(int hits)
    {
        var d = Dbg;
        var all = d.Hits;
        return new JsonObject
        {
            ["breakpoints"] = J.Arr(d.Breakpoints.Select(b => (JsonNode)BpJson(b))),
            ["totalHits"] = d.TotalHits,
            ["hits"] = J.Arr(all.Skip(Math.Max(0, all.Count - Math.Clamp(hits, 0, 2000))).Select(h => (JsonNode)new JsonObject
            {
                ["breakpoint"] = h.BreakpointId, ["cpu"] = h.CpuId, ["kind"] = Breakpoint.KindText(h.Kind), ["address"] = Num.Hex(h.Address, 4), ["value"] = Num.Hex(h.Value),
                ["source"] = h.Source.ToString(), ["pc"] = Num.Hex(h.Pc, 4), ["frame"] = h.Frame, ["scanline"] = h.Scanline, ["hit"] = h.HitNumber, ["logged"] = h.Logged, ["text"] = h.Text,
            })),
        };
    }

    public override JsonObject RemoveBreakpoint(int? id, bool all)
    {
        var d = Dbg;
        if (all) { int n = d.Breakpoints.Count; d.ClearBreakpoints(); return new JsonObject { ["removed"] = n }; }
        if (id is not int i) throw new ToolError("id or all:true is needed");
        if (!d.Remove(i)) throw new ToolError($"no breakpoint #{i}");
        return new JsonObject { ["removed"] = 1, ["id"] = i };
    }

    public override JsonObject Watch(string action, string? expr, int? index)
    {
        var d = Dbg;
        switch (action)
        {
            case "add":
                if (string.IsNullOrWhiteSpace(expr)) throw new ToolError("expr is needed ([player_hp], {ptr}, A + X, ...)");
                try { d.Evaluate(expr!); } catch (Exception e) when (e is FormatException or ArgumentException) { throw new ToolError(e.Message); }
                watches.Add(expr!.Trim()); break;
            case "remove":
                if (index is int i && i >= 0 && i < watches.Count) watches.RemoveAt(i);
                else if (expr != null && watches.Remove(expr.Trim())) { }
                else throw new ToolError("give the watch's index or its expression");
                break;
            case "clear": watches.Clear(); break;
            case "list": break;
            default: throw new ToolError("action is add, remove, clear or list");
        }
        return new JsonObject { ["watches"] = WatchValues(), ["note"] = "watch values come back with every run, step and status result" };
    }

    public override JsonObject Step(string mode, int count, string cpu, string? to, long? frame, int? scanline)
    {
        var d = Dbg; string c = string.IsNullOrWhiteSpace(cpu) ? "main" : cpu; CpuOf(c);
        long before = Interlocked.Read(ref framesStarted);
        RunOutcome o;
        if (!string.IsNullOrWhiteSpace(to)) o = StepCmd(() => { try { d.RunTo(to!, c); } catch (Exception e) when (e is FormatException or ArgumentException) { throw new ToolError(e.Message); } }, 600);
        else if (frame is long f) return RunUntil(null, "exec", null, f, null, 600, c);
        else if (scanline is int l) o = StepCmd(() => d.RunToScanline(l), 600);
        else
        {
            count = Math.Clamp(count, 1, 10_000);
            var passed = new JsonArray();
            o = RunOutcome.Break;
            for (int i = 0; i < count; i++)
            {
                o = mode switch
                {
                    "into" or "instruction" or "" => StepCmd(() => d.StepInto(c), 600),
                    "over" => StepCmd(() => d.StepOver(c), 600),
                    "out" => StepCmd(() => d.StepOut(c), 600),
                    "scanline" or "line" => StepCmd(d.StepScanline, 600),
                    "frame" => StepCmd(d.StepFrame, 600),
                    _ => throw new ToolError("mode is into, over, out, scanline or frame (or give to, frame or scanline)"),
                };
                if (o != RunOutcome.Break) break;
                if (count > 1 && i < count - 1 && passed.Count < 64 && d.LastBreak is { } lb) passed.Add(lb.Text);
            }
            var j = RunResult(o, before);
            if (passed.Count > 0) j["passed"] = passed;
            return j;
        }
        return RunResult(o, before);
    }

    public override JsonObject CallStack(string cpu)
    {
        var d = Dbg; var c = CpuOf(cpu);
        var st = d.CallStack(c.Id);
        var frames = new JsonArray();
        for (int i = st.Count - 1; i >= 0; i--)
        {
            var f = st[i];
            frames.Add(new JsonObject
            {
                ["depth"] = st.Count - 1 - i, ["kind"] = f.Kind, ["caller"] = c.FormatAddress(f.CallerPc), ["callerSymbol"] = f.CallerSymbol, ["target"] = c.FormatAddress(f.Target),
                ["targetSymbol"] = f.TargetSymbol, ["returnsTo"] = c.FormatAddress(f.ReturnPc), ["frame"] = f.Frame, ["text"] = f.ToString(),
            });
        }
        return new JsonObject
        {
            ["cpu"] = c.Id, ["pc"] = c.FormatAddress(c.PC), ["pcSymbol"] = d.SymbolAt(c, c.PC), ["parked"] = d.IsParked, ["tracked"] = d.TrackCallStack || d.IsArmed,
            ["frames"] = frames, ["order"] = "innermost first (depth 0 is the function the CPU is in)",
        };
    }

    public override JsonObject Registers(string cpu)
    {
        var c = CpuOf(cpu);
        var regs = new JsonObject();
        foreach (var r in c.RegisterNames.Distinct()) if (c.TryGetRegister(r, out long v)) regs[r] = "$" + v.ToString("X");
        return new JsonObject
        {
            ["cpu"] = c.Id, ["name"] = c.Name, ["pc"] = c.FormatAddress(c.PC), ["pcSymbol"] = D?.SymbolAt(c, c.PC), ["sp"] = "$" + c.SP.ToString("X"),
            ["registers"] = regs, ["instruction"] = DisasmLine(c, c.PC)["text"]?.GetValue<string>(), ["parked"] = D?.IsParked ?? false,
            ["frame"] = M!.Frame, ["scanline"] = M.Scanline, ["dot"] = M.Dot, ["cycle"] = M.Cycle,
        };
    }

    public override JsonObject SetRegister(string cpu, string name, long value)
    {
        var c = CpuOf(cpu);
        if (!c.TrySetRegister(name, value)) throw new ToolError($"the {c.Name} has no writable register '{name}' (it has: {string.Join(", ", c.RegisterNames.Distinct())})");
        return Registers(cpu);
    }

    public override JsonObject Trace(int count, string cpu)
    {
        var d = Dbg; var c = CpuOf(cpu);
        var t = d.TraceTail(Math.Clamp(count, 1, 5000), c.Id);
        return new JsonObject
        {
            ["cpu"] = c.Id, ["lines"] = J.Strs(t.Select(r => d.FormatTrace(r, c.Id))), ["count"] = t.Length,
            ["note"] = t.Length == 0 ? "the trace ring is empty: it records while the debugger's hooks are armed (the session's call stack tracking, a breakpoint or a step)" : "oldest first; the last line is the most recent instruction (Mesen's trace logger columns)",
        };
    }

    public override JsonObject TraceFrame(long frame, string cpu, int lines, string? function, List<string> ranges)
    {
        var d = Dbg; var c = CpuOf(cpu);
        if (frame < M!.Frame || frame == M.Frame && d.IsParked) throw new ToolError($"frame {frame} has started or passed (the machine is in frame {M.Frame}); trace a later frame, or load a state saved before it");
        string path = Path.Combine(Folder, $"trace_{c.Id}_frame{frame}.txt");
        var opt = new Debugger.TraceFileOptions { Cpu = c.Id, FromFrame = frame, ToFrame = frame, Function = function };
        opt.Ranges.AddRange(ranges);
        try { d.StartTraceFile(path, opt); }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException) { throw new ToolError(e.Message); }
        long before = Interlocked.Read(ref framesStarted);
        RunOutcome o;
        try { o = StepCmd(() => d.RunToFrame(frame + 1), frame + 1 - M.Frame + 2); }
        finally { }
        long n = d.StopTraceFile();
        var all = File.Exists(path) ? File.ReadLines(path).Take(200_000).ToList() : new List<string>();
        int head = Math.Clamp(lines, 0, 2000);
        var j = RunResult(o, before);
        j["traceFile"] = path; j["linesWritten"] = n;
        j["head"] = J.Strs(all.Take(head));
        if (all.Count > head) j["tail"] = J.Strs(all.Skip(Math.Max(head, all.Count - 20)));
        if (!(o == RunOutcome.Break && d.LastBreak?.Reason == "run-to")) j["note"] = "the run stopped before the frame ended (a breakpoint?): the trace is the part that ran";
        return j;
    }

    private JsonObject DisasmLine(DebugCpu c, uint pc)
    {
        uint bytes = c.FetchBytes(pc);
        var ic = c.Classify(bytes);
        var b = new byte[Math.Max(1, ic.Length)];
        for (int k = 0; k < b.Length; k++) b[k] = (byte)(bytes >> (8 * k));
        var d = D;
        string text = c.Disassembler.Format(pc, b, c.Context, d == null ? null : x => { var s = d.SymbolAt(c, x); return s != null && !s.Contains('+') ? s : null; });
        return new JsonObject { ["address"] = c.FormatAddress(pc), ["bytes"] = Num.HexBytes(b), ["text"] = text, ["label"] = d?.SymbolAt(c, pc), ["length"] = b.Length };
    }

    public override JsonObject Disassemble(string? address, int count, string cpu)
    {
        var c = CpuOf(cpu);
        uint pc = c.PC;
        if (!string.IsNullOrWhiteSpace(address) && !address!.Equals("pc", StringComparison.OrdinalIgnoreCase))
        {
            string a = address.Trim();
            if (Regex.IsMatch(a, "^(\\$|0x|0X)?[0-9A-Fa-f]+$")) pc = (uint)Num.Parse(a.StartsWith('$') || a.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(a, "^[0-9]+$") ? a : "$" + a, "address");
            else
            {
                // a code symbol: the CPU address where it is mapped now (the PC's bank, then bank 0 / $80 on the SNES)
                var d = Dbg;
                uint? found = null;
                foreach (uint bank in new[] { c.PC & 0xFF0000, 0u, 0x800000u }.Distinct())
                {
                    for (uint x = 0; x <= 0xFFFF && found == null; x++) if (d.SymbolAt(c, bank | x) == a) found = bank | x;
                    if (found != null || c.AddressDigits < 6) break;
                }
                pc = found ?? throw new ToolError($"'{a}' is not a number, nor a code symbol mapped into the CPU's address space now");
            }
        }
        var lines = new JsonArray();
        for (int i = 0; i < Math.Clamp(count, 1, 512); i++)
        {
            var l = DisasmLine(c, pc);
            lines.Add(l);
            pc = c.NextAddress(pc, l["length"]!.GetValue<int>());
        }
        return new JsonObject { ["cpu"] = c.Id, ["pc"] = c.FormatAddress(c.PC), ["lines"] = lines };
    }

    public override JsonObject Evaluate(string expr, string cpu)
    {
        var d = Dbg; var c = CpuOf(cpu);
        try { long v = d.Evaluate(expr, c.Id); return new JsonObject { ["expr"] = expr, ["value"] = v, ["hex"] = "$" + v.ToString("X") }; }
        catch (Exception e) when (e is FormatException or ArgumentException) { throw new ToolError(e.Message); }
    }

    // ================================================================================================ the picture

    private (uint[] px, int w, int h) Picture()
    {
        if (nes != null)
        {
            var fb = nes.GetFrameBuffer();
            var a = new uint[256 * 240];
            for (int i = 0; i < a.Length; i++) a[i] = 0xFF000000u | (uint)fb[i * 4] << 16 | (uint)fb[i * 4 + 1] << 8 | fb[i * 4 + 2];
            return (a, 256, 240);
        }
        int w = cs!.FrameWidth, h = cs.FrameHeight;
        var px = new uint[w * h];
        Array.Copy(cs.Frame, px, px.Length);
        return (px, w, h);
    }

    public override ToolOut Screenshot(int scale)
    {
        var (px, w, h) = Picture();
        var (hash, colours) = Png.Stats(px, px.Length);
        var o = new ToolOut(new JsonObject { ["width"] = w, ["height"] = h, ["scale"] = scale, ["frame"] = Frames, ["hash"] = hash, ["colours"] = colours, ["parked"] = D?.IsParked ?? false });
        if (D?.IsParked == true) o.Json["note"] = "stopped inside a frame: this is the last finished picture (part of it may already be redrawn)";
        o.Pngs.Add(Png.FromArgb(px, w, h, scale));
        return o;
    }

    public override ToolOut Layers(List<string> layers, int scale)
    {
        if (cs is not SnesSession sn) throw new ToolError("layers are the SNES and WideMess picture's backgrounds and sprites; this console's picture has no layer switch in the engine yet");
        if (!((IConsoleSession)sn).SupportsState) throw new ToolError("the layer view draws one frame and puts the machine back with a state, and this session has no states: " + ((IConsoleSession)sn).StateUnsupportedReason);
        int mask = 0;
        foreach (var l in layers.Select(x => x.Trim().ToLowerInvariant()))
            mask |= l switch { "bg1" => 1, "bg2" => 2, "bg3" => 4, "bg4" => 8, "obj" or "sprites" or "oam" => 16, "all" => 0x1F, _ => throw new ToolError($"layer '{l}' is bg1, bg2, bg3, bg4 or obj") };
        if (mask == 0) throw new ToolError("give the layers to show: bg1, bg2, bg3, bg4, obj");
        string? note = EnsureBetweenFrames();
        var saved = sn.SaveState();
        int old = sn.Ppu.DebugLayerMask;
        var on = D?.Breakpoints.Where(b => b.Enabled).Select(b => b.Id).ToList() ?? new();
        var obs = D?.Observers.ToArray() ?? Array.Empty<DebugObserver>();
        (uint[] px, int w, int h) pic;
        try
        {
            if (D != null) { foreach (var id in on) D.SetEnabled(id, false); foreach (var o in obs) D.RemoveObserver(o); }
            sn.Ppu.DebugLayerMask = mask;
            ApplyInput();
            sn.RunFrame();
            pic = Picture();
            while (sn.ReadSamples(sbuf) > 0) { }
        }
        finally
        {
            sn.Ppu.DebugLayerMask = old;
            sn.LoadState(saved);
            if (D != null) { foreach (var o in obs) D.AddObserver(o); foreach (var id in on) D.SetEnabled(id, true); }
            FreshDebugState();
        }
        var (hash, colours) = Png.Stats(pic.px, pic.px.Length);
        var res = new ToolOut(new JsonObject
        {
            ["layers"] = J.Strs(layers), ["mask"] = Num.Hex(mask), ["width"] = pic.w, ["height"] = pic.h, ["hash"] = hash, ["colours"] = colours, ["frame"] = Frames,
            ["how"] = "the next frame drawn with only these layers on the main and sub screens, then the machine put back exactly as it was (a state)",
        });
        if (note != null) res.Json["note"] = note;
        res.Pngs.Add(Png.FromArgb(pic.px, pic.w, pic.h, scale));
        return res;
    }


    public override ToolOut Tiles(string? source, int bpp, int palette, long start, int count, int scale)
    {
        // one decoder with the desktop's Tiles window (BrokenDev D8): NesEmulator.Debugging.DebugPictures
        string family = nes != null ? "nes" : cs is GbSession ? "gb" : cs is SnesSession ? "snes" : "";
        if (family == "") throw new ToolError($"the tile viewer covers the NES, SNES, WideMess and Game Boy; not the {Consoles.DisplayName(console)} yet");
        DebugPictures.TileSheet t;
        try { t = DebugPictures.Tiles(AllDomains(), family, source, bpp, palette, start, count); }
        catch (ArgumentException e) { throw new ToolError(e.Message); }
        var o = new ToolOut(new JsonObject
        {
            ["what"] = t.What, ["start"] = Num.Hex(t.Start, 4), ["tiles"] = t.Tiles, ["tileBytes"] = t.TileBytes, ["columns"] = t.Columns, ["width"] = t.Width, ["height"] = t.Height,
            ["palette"] = t.Palette,
            ["colours"] = t.Colours,
        });
        o.Pngs.Add(Png.FromArgb(t.Pixels, t.Width, t.Height, Math.Clamp(scale, 1, 4)));
        return o;
    }

    public override ToolOut Sprites(bool draw, int scale)
    {
        // one decoder with the desktop's Sprites window (BrokenDev D8): NesEmulator.Debugging.DebugPictures
        if (M == null || !(nes != null || cs is GbSession || cs is SnesSession))
            throw new ToolError($"the sprite list covers the NES, SNES, WideMess and Game Boy; not the {Consoles.DisplayName(console)} yet");
        List<DebugPictures.SpriteInfo> sprites;
        string note;
        try { sprites = DebugPictures.Sprites(M, out note); }
        catch (ArgumentException e) { throw new ToolError(e.Message); }
        var list = new JsonArray();
        foreach (var p in sprites)
        {
            if (nes != null)
                list.Add(new JsonObject { ["index"] = p.Index, ["x"] = p.X, ["y"] = p.Y, ["tile"] = Num.Hex(p.Tile), ["palette"] = p.Palette, ["behind"] = p.Behind, ["flipX"] = p.FlipX, ["flipY"] = p.FlipY });
            else if (cs is GbSession)
                list.Add(new JsonObject { ["index"] = p.Index, ["x"] = p.X, ["y"] = p.Y, ["tile"] = Num.Hex(p.Tile), ["flags"] = Num.Hex(p.Flags), ["behind"] = p.Behind, ["flipY"] = p.FlipY, ["flipX"] = p.FlipX, ["dmgPalette"] = p.DmgPalette, ["cgbPalette"] = p.CgbPalette, ["vramBank"] = p.VramBank });
            else
                list.Add(new JsonObject { ["index"] = p.Index, ["x"] = p.X, ["y"] = p.Y, ["tile"] = Num.Hex(p.Tile, 3), ["palette"] = p.Palette, ["priority"] = p.Priority, ["flipX"] = p.FlipX, ["flipY"] = p.FlipY, ["large"] = p.Large });
        }
        var o = new ToolOut(new JsonObject
        {
            ["sprites"] = list, ["count"] = list.Count, ["frame"] = Frames,
            ["note"] = note,
        });
        if (draw)
        {
            var (px, w, h) = Picture();
            DebugPictures.DrawBoxes(px, w, h, sprites);
            o.Pngs.Add(Png.FromArgb(px, w, h, scale));
        }
        return o;
    }

    // ================================================================================================ sound

    public override JsonObject RecordSound(double seconds, bool wav)
    {
        seconds = Math.Clamp(seconds, 0.1, 120);
        long frames = (long)Math.Round(seconds * Fps);
        recChannels = nes != null ? 1 : 2;
        var r = new List<short>((int)(seconds * SampleRate * recChannels) + 4096);
        long before = Interlocked.Read(ref framesStarted);
        rec = r;
        RunOutcome o;
        try { o = RunFrames(frames); }
        finally { rec = null; }
        var j = RunResult(o, before);
        int rate = SampleRate, ch = recChannels;
        var s = r.ToArray();
        int perFrame = Math.Max(1, (int)(rate / Fps) * ch);
        int nonSilent = 0; double sum = 0; int peak = 0;
        var perSecond = new JsonArray();
        double secSum = 0; int secN = 0;
        for (int i = 0; i < s.Length; i += perFrame)
        {
            int p = 0;
            for (int k = i; k < Math.Min(s.Length, i + perFrame); k++) { int a = Math.Abs((int)s[k]); if (a > p) p = a; sum += (double)s[k] * s[k]; secSum += (double)s[k] * s[k]; secN++; }
            if (p > 327) nonSilent++;
            if (p > peak) peak = p;
            if (secN >= rate * ch) { perSecond.Add(Math.Round(Db(Math.Sqrt(secSum / secN)), 1)); secSum = 0; secN = 0; }
        }
        if (secN > rate * ch / 4) perSecond.Add(Math.Round(Db(Math.Sqrt(secSum / secN)), 1));
        j["sampleRate"] = rate; j["channels"] = ch; j["samples"] = s.Length / ch; j["seconds"] = Math.Round(s.Length / (double)ch / rate, 2);
        j["peak"] = Math.Round(peak / 32768.0, 4); j["peakDb"] = Math.Round(Db(peak), 1); j["rmsDb"] = Math.Round(Db(Math.Sqrt(sum / Math.Max(1, s.Length))), 1);
        j["nonSilentFrames"] = nonSilent; j["rmsDbPerSecond"] = perSecond;
        j["silent"] = peak <= 327;
        if (wav)
        {
            string path = Path.Combine(Folder, $"sound_frame{before}_{frames}f.wav");
            Wav.Write(path, s, s.Length, rate, ch);
            j["wav"] = path;
        }
        return j;
    }

    private static double Db(double amplitude) => amplitude <= 0 ? -120 : 20 * Math.Log10(amplitude / 32768.0);

    // ================================================================================================ states

    private Snapshot Take()
    {
        var snap = new Snapshot { Frame = Frames, FramesStarted = Interlocked.Read(ref framesStarted) };
        if (nes != null) snap.State = Encoding.UTF8.GetBytes(nes.SaveState());
        else
        {
            if (!cs!.SupportsState) throw new ToolError(cs.StateUnsupportedReason);
            snap.State = cs.SaveState();
        }
        if (M != null)
            foreach (var sp in M.Main.Spaces)
            {
                if (sp.Size > 2 * 1024 * 1024 || sp.Name is "rom" or "prg") continue;
                var b = new byte[sp.Size];
                for (uint i = 0; i < sp.Size; i++) b[i] = M.Main.PeekSpace(sp.Id, i);
                snap.Spaces[sp.Id] = b;
            }
        return snap;
    }

    public override JsonObject SaveState(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Equals("current", StringComparison.OrdinalIgnoreCase)) throw new ToolError("a state needs a name (not 'current')");
        string? note = EnsureBetweenFrames();
        var s = Take();
        states[name] = s;
        var j = new JsonObject { ["state"] = name, ["frame"] = s.Frame, ["bytes"] = s.State.Length, ["states"] = J.Strs(states.Keys), ["where"] = "in this server's memory only (never a file, never the user's saves)" };
        if (note != null) j["note"] = note;
        return j;
    }

    public override JsonObject LoadState(string name)
    {
        if (!states.TryGetValue(name, out var s)) throw new ToolError($"no state '{name}' (saved: {(states.Count == 0 ? "none" : string.Join(", ", states.Keys))})");
        string? note = EnsureBetweenFrames();
        try
        {
            if (nes != null) nes.LoadState(Encoding.UTF8.GetString(s.State));
            else cs!.LoadState(s.State);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException) { throw new ToolError(e.Message); }
        FreshDebugState();
        StartHistory(s.State);
        var j = Status(); j["loaded"] = name; j["stateFrame"] = s.Frame;
        j["callStack"] = "restarted from here (the debugger's call stack and hooks start again after a jump)";
        if (checks != null) j["checksNote"] = "the hardware checks keep what they learned before the load (for example which RAM was written); stop and start them again for a clean run from this state";
        if (note != null) j["note"] = note;
        return j;
    }

    public override JsonObject DiffStates(string a, string b, int limit)
    {
        if (M == null) throw new ToolError("a diff by symbol needs the debugger's view of the memory (NES, SNES, WideMess, Game Boy)");
        Snapshot Get(string n) => n.Equals("current", StringComparison.OrdinalIgnoreCase) ? Take() : states.TryGetValue(n, out var s) ? s : throw new ToolError($"no state '{n}' (saved: {string.Join(", ", states.Keys)}; or 'current')");
        if (a.Equals("current", StringComparison.OrdinalIgnoreCase) || b.Equals("current", StringComparison.OrdinalIgnoreCase)) EnsureBetweenFrames();
        var A = Get(a); var B = Get(b);
        // the state diff by symbol is the engine's (SymbolDiff, D5): the divergence finder and bisect use the same one
        var r = SymbolDiff.Compare(M.Main, D!.Symbols, A.Spaces, B.Spaces, limit);
        var diffs = new JsonArray();
        foreach (var v in r.Variables)
        {
            var o = new JsonObject { ["variable"] = v.Name, ["at"] = v.Place, ["size"] = v.A.Length, ["a"] = Num.HexBytes(v.A), ["b"] = Num.HexBytes(v.B) };
            if (v.ValueA is long va && v.ValueB is long vb) { o["valueA"] = va; o["valueB"] = vb; }
            diffs.Add(o);
        }
        var spaces = new JsonArray();
        foreach (var sd in r.Spaces) spaces.Add(new JsonObject { ["space"] = sd.Space, ["bytesDiffering"] = sd.Differing, ["withoutSymbol"] = sd.WithoutSymbol, ["firstWithoutSymbol"] = J.Strs(sd.FirstWithoutSymbol) });
        return new JsonObject
        {
            ["a"] = a, ["b"] = b, ["frameA"] = A.Frame, ["frameB"] = B.Frame, ["symbols"] = D.Symbols.All.Count, ["variablesDiffering"] = r.VariablesDiffering, ["variables"] = diffs, ["spaces"] = spaces,
            ["note"] = D.Symbols.All.Count == 0 ? "no symbols loaded: only the raw bytes per space are compared (load the game's .mlb / .sym for names)" : null,
        };
    }

    // ================================================================================================ movies and step back (D5)

    /// <summary>A new input while the session is behind the end of its log (after a step back, or inside a movie): the future is rewritten from here.</summary>
    private void BranchHistory()
    {
        if (history == null || !history.Replaying) return;
        history.Branch();
        if (playingMovie != null) { playingMovie = null; historyNote += "; a new input stopped the movie: the session plays live from here"; }
    }

    /// <summary>Plays a movie from power-on in this new session: its input is the log, its keyframes are checked as the session runs.</summary>
    public JsonObject PlayMovie(Movie m, string file, long frames)
    {
        if (history == null) throw new ToolError(historyNote);
        if (m.Start == MovieStart.State)
        {
            try { LoadRaw(m.StartState!); } catch (Exception e) { throw new ToolError($"the movie's start state does not load here: {e.Message}"); }
            StartHistory(m.StartState);
        }
        history.Preload(m);
        playingMovie = m; playingFile = file;
        long before = Interlocked.Read(ref framesStarted);
        var o = RunFrames(Math.Clamp(frames < 0 ? m.Frames : frames, 0, m.Frames));
        var j = RunResult(o, before);
        j["movie"] = file; j["movieFrames"] = m.Frames;
        j["keyframesChecked"] = history.KeysChecked; j["keyframesMatched"] = history.KeysMatched;
        j["inSync"] = history.OffCourseFrame == null;
        if (history.OffCourseFrame != null) j["offCourse"] = $"off course after {history.OffCourseFrame} frames ({history.OffCourseWhat})";
        j["sameRom"] = m.RomSha256.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(romPath))), StringComparison.OrdinalIgnoreCase);
        j["note"] = "run on to play the rest of the movie (its keyframes keep being checked); after its last frame the session plays live input; a new input before its end stops the movie there";
        return j;
    }

    public override JsonObject StepBack(long? instructions, long? frames)
    {
        var h = history ?? throw new ToolError(historyNote);
        if ((instructions == null) == (frames == null)) throw new ToolError("step back by instructions or by frames (one of them)");
        DebugHistory.BackResult r;
        bool portOn = port?.Attached == true;
        port?.Detach();   // the replay's frames were heard already: the debug port listens again from where the replay ends
        try
        {
            if (instructions is long n)
            {
                if (D == null) throw new ToolError($"step back by instructions needs the debugger, which does not cover the {Consoles.DisplayName(console)} yet (by frames works)");
                r = h.BackInstructions(R!, n, LoadRaw, () => EnsureBetweenFrames());
            }
            else r = h.BackFrames(R, frames!.Value, D?.IsParked ?? false, LoadRaw, () => EnsureBetweenFrames(), k => { for (long i = 0; i < k; i++) Frame(); });
        }
        catch (InvalidOperationException e) { throw new ToolError(e.Message); }
        catch (ArgumentException e) { throw new ToolError(e.Message); }
        finally { if (portOn) port!.Attach(); }
        Interlocked.Exchange(ref framesStarted, h.FrameIndex);
        var j = Status();
        j["steppedBack"] = new JsonObject
        {
            ["fromFrame"] = r.FromFrame, ["toFrame"] = r.ToFrame, ["fromInstruction"] = r.FromInstruction >= 0 ? r.FromInstruction : null, ["toInstruction"] = r.ToInstruction >= 0 ? r.ToInstruction : null,
            ["stateOfFrame"] = r.SnapshotFrame, ["framesReplayed"] = r.FramesReplayed, ["note"] = r.Note.Length > 0 ? r.Note : null,
        };
        j["callStack"] = "restarted at the state the replay began from (the frames before it are not on it)";
        if (checks != null) j["checksNote"] = "the hardware checks did not see the replay (they keep what they learned before the step back)";
        j["future"] = "run or step to replay the same input forward; a new input from here rewrites the future";
        return j;
    }

    public override JsonObject MovieSave(string? path)
    {
        var h = history ?? throw new ToolError(historyNote);
        string? note = EnsureBetweenFrames();
        Movie m;
        try { m = h.ToMovie(h.FrameIndex); }
        catch (InvalidOperationException e) { throw new ToolError(e.Message); }
        byte[] file = File.ReadAllBytes(romPath);
        static string Strip(string id, string prefix) => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? id[prefix.Length..] : id;
        var cp = cores.Split('/');
        m.Console = nes != null ? "nes" : ConsoleKey; m.RomName = romName; m.RomSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)).ToLowerInvariant();
        m.RomBytes = file.Length; m.Emulator = "BrokenNes MCP server " + Program.Version;
        m.Cores = nes != null && cp.Length == 3 ? $"{Strip(cp[0], "CPU_")}/{Strip(cp[1], "PPU_")}/{Strip(cp[2], "APU_")}" : cores;
        m.Build = $"{romName} as opened in an MCP session at {DateTime.Now:s}";
        if (path == null || path.Trim().Length == 0) path = Path.Combine(Folder, $"session-{Id}-{DateTime.Now:yyyyMMdd-HHmmss}{MovieFormat.Extension}");
        else if (!path.EndsWith(MovieFormat.Extension, StringComparison.OrdinalIgnoreCase)) path += MovieFormat.Extension;
        m.Save(path);
        var j = new JsonObject
        {
            ["file"] = Path.GetFullPath(path), ["format"] = MovieFormat.Name, ["frames"] = m.Frames, ["inputChanges"] = m.Inputs.Count, ["keyframes"] = m.Keys.Count,
            ["start"] = m.Start == MovieStart.PowerOn ? "power-on" : "a save state (the one this session loaded or reset from)", ["cores"] = m.Cores,
            ["next"] = "replay it with open_movie, export it (movie export: fm2 / bk2), or promote it into a project's verify tests (brokendev.ps1 movie promote, only when the user wants it)",
        };
        if (note != null) j["note"] = note;
        return j;
    }

    // ================================================================================================ the checks and the profiler

    public override JsonObject Checks(string action, List<string> off, List<string> wait, long from, int top)
    {
        var d = Dbg;
        switch (action)
        {
            case "start":
            {
                if (checks != null) throw new ToolError("the checks are already on in this session (stop them first)");
                var opt = new CheckOptions();
                foreach (var r in off) opt.Off.Add(r);
                foreach (var w in wait) opt.FrameWait.Add(w);
                try { checks = CheckRun.Attach(d, opt); }
                catch (NotSupportedException e) { throw new ToolError(e.Message); }
                checksFrom = from;
                return new JsonObject
                {
                    ["checks"] = "on", ["family"] = checks.Family, ["fromFrame"] = Frames,
                    ["rules"] = J.Strs(CheckCatalogue.For(checks.Family).Select(r => r.Id)),
                    ["note"] = Frames > 0 ? "started after power-on: rules about memory read before it was written only know the writes from now on (open the ROM with checks:true for a clean run)" : "on from power-on",
                };
            }
            case "report": case "profile":
            {
                if (checks == null) throw new ToolError("the checks are off: start them (checks action start), or open the ROM with checks:true");
                return Report(checks, action == "profile", from, top, final: false);
            }
            case "stop":
            {
                if (checks == null) throw new ToolError("the checks are off");
                var c = checks;
                c.Finish();
                var j = Report(c, false, from, top, final: true);
                d.RemoveObserver(c);
                checks = null;
                return j;
            }
            default: throw new ToolError("action is start, report, profile or stop");
        }
    }

    private JsonObject Report(ConsoleChecks c, bool profileOnly, long from, int top, bool final)
    {
        var p = CheckRun.Summarize(c, from > 0 ? from : checksFrom);
        var json = CheckRun.Json(c, p);
        var prof = (JsonObject)json["profile"]!;
        if (prof["functions"] is JsonArray fa) while (fa.Count > Math.Clamp(top, 0, 30)) fa.RemoveAt(fa.Count - 1);
        var j = new JsonObject { ["frame"] = Frames, ["final"] = final };
        if (!profileOnly)
        {
            j["findingCount"] = c.Findings.Count;
            j["findings"] = json["findings"]!.DeepClone();
            j["memory"] = json["memory"]!.DeepClone();
            j["stack"] = json["stack"]!.DeepClone();
        }
        j["profile"] = prof.DeepClone();
        j["profileText"] = J.Strs(CheckRun.ProfileText(p, top));
        if (!final) j["note"] = "a report while running: findings still pending at the end of a run (a handler that never returns, say) appear when the checks are stopped";
        return j;
    }
}
