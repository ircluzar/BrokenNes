using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrokenNes.Mcp;

/// <summary>
/// The user's running BrokenNes desktop app, attached by its PID through its HTTP API (the discovery file
/// <c>%LOCALAPPDATA%\BrokenNes\instances\&lt;pid&gt;.json</c>). <b>Read freely, control when asked</b>
/// (docs/projects/brokendev-debugging-plan.md): a live session opens read-only; pausing, stepping, breakpoints, memory and register
/// writes, input and state loads need a session opened with <c>control: true</c>, which an agent does only when the user asked in chat,
/// AND the app's own <c>enableDebuggerControl</c> setting (the user's config). Saves and progression are never touched, and the process is
/// never stopped: closing the session only lets the game go (detach).
/// </summary>
internal sealed class LiveSession : Session
{
    public override string Kind => "live";
    public override string ConsoleKey => console;
    public int Pid { get; }
    public bool ControlRequested { get; }
    private readonly HttpClient http;
    private readonly string baseUrl;
    private string console = "?";
    private bool attachedByUs;

    private LiveSession(string id, int pid, string baseUrl, bool control)
    {
        Id = id; Pid = pid; this.baseUrl = baseUrl; ControlRequested = control;
        http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(150) };
    }

    // ================================================================================================ instances

    public static string InstancesDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrokenNes", "instances");

    public sealed record Instance(int Pid, int HttpPort, string Exe, string Started, bool Alive);

    public static List<Instance> Instances()
    {
        var list = new List<Instance>();
        if (!Directory.Exists(InstancesDir)) return list;
        foreach (var f in Directory.GetFiles(InstancesDir, "*.json"))
        {
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(f))!;
                int pid = j["pid"]!.GetValue<int>();
                bool alive;
                try { using var p = Process.GetProcessById(pid); alive = !p.HasExited; } catch { alive = false; }
                list.Add(new Instance(pid, j["httpPort"]?.GetValue<int>() ?? 0, j["exePath"]?.GetValue<string>() ?? "", j["startedUtc"]?.ToString() ?? "", alive));
            }
            catch { /* a file being written, or not ours: skipped */ }
        }
        return list.OrderBy(i => i.Pid).ToList();
    }

    public static LiveSession Attach(string id, int pid, bool control)
    {
        var inst = Instances().FirstOrDefault(i => i.Pid == pid) ?? throw new ToolError($"no BrokenNes desktop app with PID {pid} has a discovery file in {InstancesDir} (list_instances shows the running ones)");
        if (!inst.Alive) throw new ToolError($"PID {pid}'s discovery file is stale: that process is not running");
        if (inst.HttpPort <= 0) throw new ToolError($"PID {pid}'s discovery file has no HTTP port");
        var s = new LiveSession(id, pid, $"http://127.0.0.1:{inst.HttpPort}", control);
        var health = s.Get("/api/health", allowFail: true);
        if (health?["success"]?.GetValue<bool>() != true) throw new ToolError($"PID {pid} does not answer on {s.baseUrl}/api/health");
        try { s.console = s.Get("/api/debug/state", allowFail: true)?["console"]?.GetValue<string>() ?? "?"; } catch { }
        return s;
    }

    // ================================================================================================ HTTP

    private JsonNode? Send(HttpMethod m, string path, object? body, bool allowFail)
    {
        using var req = new HttpRequestMessage(m, path);
        if (body != null) req.Content = JsonContent.Create(body);
        HttpResponseMessage resp;
        try { resp = http.Send(req); }
        catch (Exception e) { throw new ToolError($"the desktop app (PID {Pid}) did not answer {m} {path}: {e.Message}"); }
        using (resp)
        {
            string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            JsonNode? j = null;
            try { j = text.Length > 0 ? JsonNode.Parse(text) : null; } catch { }
            if (allowFail) return j;
            bool ok = resp.IsSuccessStatusCode && (j is not JsonObject o || o["success"] is null || o["success"]!.GetValueKind() != JsonValueKind.False);
            if (!ok)
            {
                string err = (j as JsonObject)?["error"]?.ToString() ?? $"HTTP {(int)resp.StatusCode}";
                throw new ToolError($"the desktop app answered {m} {path}: {err}");
            }
            return j;
        }
    }

    private JsonNode? Get(string path, bool allowFail = false) => Send(HttpMethod.Get, path, null, allowFail);
    private JsonNode? Post(string path, object? body = null) => Send(HttpMethod.Post, path, body ?? new { }, false);
    private JsonNode? Delete(string path) => Send(HttpMethod.Delete, path, null, false);
    private static string Q(string s) => Uri.EscapeDataString(s);

    /// <summary>Every control call goes through here: the session must have been opened for control (the user asked), and the app must allow it.</summary>
    private void Control(string what)
    {
        if (!ControlRequested)
            throw new ToolError($"{what} is control, and this live session is read-only. Open a live session with control:true ONLY when the user asked for it in chat (the plan's rule: read freely, control when asked).");
        var st = Get("/api/debug/state", allowFail: true);
        if (st?["controlEnabled"]?.GetValue<bool>() != true)
            throw new ToolError($"{what}: the desktop app refuses control: its config has \"enableDebuggerControl\" off. Only the user turns it on (in the BrokenNes config); an agent never edits it.");
    }

    private void EnsureAttached()
    {
        var st = Get("/api/debug/state", allowFail: true);
        if (st?["attached"]?.GetValue<bool>() == true) return;
        Post("/api/debug/attach");
        attachedByUs = true;
    }

    // ================================================================================================ describe / status

    public override JsonObject Describe()
    {
        var j = new JsonObject
        {
            ["session"] = Id, ["kind"] = Kind, ["pid"] = Pid, ["api"] = baseUrl, ["control"] = ControlRequested, ["console"] = console,
            ["rules"] = "read freely; control only when the user asked in chat (and the app's enableDebuggerControl is on); never saves, never progression, never stops the process",
        };
        var st = Get("/api/debug/state", allowFail: true);
        if (st != null) { j["appAllowsControl"] = st["controlEnabled"]?.DeepClone(); j["attached"] = st["attached"]?.DeepClone(); j["parked"] = st["parked"]?.DeepClone(); j["frame"] = st["frame"]?.DeepClone(); }
        var con = Get("/api/console", allowFail: true);
        if (con?["status"] is JsonNode s) j["game"] = s["description"]?.DeepClone() ?? s.DeepClone();
        var rom = Get("/api/emulator/current-rom", allowFail: true);
        if (rom?["name"] != null) j["rom"] = rom["name"]!.DeepClone();
        return j;
    }

    public override JsonObject Status()
    {
        var j = Describe();
        var st = Get("/api/debug/state", allowFail: true);
        if (st != null) { j["state"] = st.DeepClone(); console = st["console"]?.GetValue<string>() ?? console; }
        return j;
    }

    public override void Close()
    {
        // never stop the process; a control session that attached the debugger lets the game go
        if (ControlRequested && attachedByUs) { try { Post("/api/debug/detach"); } catch { } }
        http.Dispose();
    }

    private static JsonObject Obj(JsonNode? n) => n as JsonObject ?? new JsonObject { ["result"] = n?.DeepClone() };

    // ================================================================================================ reading (always allowed)

    public override JsonObject Registers(string cpu) => Obj(Get($"/api/debug/registers?cpu={Q(cpu)}"));
    public override JsonObject CallStack(string cpu) => Obj(Get($"/api/debug/callstack?cpu={Q(cpu)}"));
    public override JsonObject Trace(int count, string cpu) => Obj(Get($"/api/debug/trace?cpu={Q(cpu)}&count={Math.Clamp(count, 1, 5000)}"));
    public override JsonObject Disassemble(string? address, int count, string cpu) => Obj(Get($"/api/debug/disasm?cpu={Q(cpu)}&count={Math.Clamp(count, 1, 512)}{(string.IsNullOrWhiteSpace(address) || address == "pc" ? "" : "&address=" + Q(address!.TrimStart('$')))}"));
    public override JsonObject Evaluate(string expr, string cpu) => Obj(Get($"/api/debug/eval?cpu={Q(cpu)}&expr={Q(expr)}"));
    public override JsonObject Domains() => Obj(Get("/api/memory/domains"));

    public override JsonObject ListBreakpoints(int hits)
    {
        var j = Obj(Get("/api/debug/breakpoints"));
        var h = Get($"/api/debug/hits?count={Math.Clamp(hits, 0, 2000)}", allowFail: true);
        if (h != null) { j["hits"] = h["hits"]?.DeepClone(); j["totalHits"] = h["total"]?.DeepClone(); }
        return j;
    }

    public override JsonObject ReadMemory(string? spec, string? domain, long? address, int? length, string cpu)
    {
        if (!string.IsNullOrWhiteSpace(spec))
        {
            var j = Obj(Get($"/api/debug/memory?cpu={Q(cpu)}&spec={Q(spec!)}{(length is int l ? "&length=" + Math.Clamp(l, 1, 4096) : "")}"));
            AddHex(j);
            return j;
        }
        if (!string.IsNullOrWhiteSpace(domain))
        {
            var j = Obj(Get($"/api/memory/peek-range?domain={Q(domain!)}&address={address ?? 0}&length={Math.Clamp(length ?? 16, 1, 4096)}"));
            AddHex(j);
            return j;
        }
        throw new ToolError("give a spec (an address or symbol on a CPU) or a domain and an address");
    }

    private static void AddHex(JsonObject j)
    {
        if (j["data"] is not JsonArray a) return;
        var b = a.Select(x => (byte)x!.GetValue<int>()).ToArray();
        j["hex"] = Num.HexBytes(b);
        if (b.Length <= 8) { long v = 0; for (int i = b.Length - 1; i >= 0; i--) v = v << 8 | b[i]; j["value"] = v; }
        if (b.Length > 64) j.Remove("data");
    }

    public override JsonObject Symbols(string? load, string? find, long? address, string? prefix, string cpu, int limit)
    {
        var j = new JsonObject();
        if (!string.IsNullOrWhiteSpace(load)) { Control("loading a symbol file into the app's debugger"); EnsureAttached(); j["load"] = Post("/api/debug/symbols", new { path = load })?.DeepClone(); }
        if (!string.IsNullOrWhiteSpace(find)) j["symbol"] = Get($"/api/debug/symbol?cpu={Q(cpu)}&name={Q(find!)}")?.DeepClone();
        if (address is long a) j["at"] = Get($"/api/debug/symbol?cpu={Q(cpu)}&address={a}")?.DeepClone();
        if (prefix != null) j["note"] = "listing symbols by name is not in the desktop app's API; find one by name or address";
        return j;
    }

    public override ToolOut Screenshot(int scale)
    {
        var fb = Get("/api/console/framebuffer") ?? throw new ToolError("no picture");
        int w = fb["width"]!.GetValue<int>(), h = fb["height"]!.GetValue<int>();
        var rgba = Convert.FromBase64String(fb["data"]!.GetValue<string>());
        var px = new uint[w * h];
        for (int i = 0; i < px.Length && i * 4 + 2 < rgba.Length; i++) px[i] = 0xFF000000u | (uint)rgba[i * 4] << 16 | (uint)rgba[i * 4 + 1] << 8 | rgba[i * 4 + 2];
        var (hash, colours) = Png.Stats(px, px.Length);
        var o = new ToolOut(new JsonObject { ["width"] = w, ["height"] = h, ["hash"] = hash, ["colours"] = colours, ["pid"] = Pid });
        o.Pngs.Add(Png.FromArgb(px, w, h, scale));
        return o;
    }

    // ================================================================================================ control (opened for control AND the app allows it)

    private JsonObject AfterBreak(JsonNode? r)
    {
        var j = Obj(r);
        j["control"] = true;
        return j;
    }

    public override JsonObject AddBreakpoint(string spec, string kind, string? condition, string? hits, bool tracepoint, string? log, string cpu)
    {
        Control("setting a breakpoint");
        EnsureAttached();
        return Obj(Post("/api/debug/breakpoints", new { spec, kind = KindText(kind), cpu, condition, hits, tracepoint, log }));
    }

    private static string KindText(string k) => HeadlessSession.ParseKind(k) switch
    {
        NesEmulator.Debugging.AccessKind.Read => "read", NesEmulator.Debugging.AccessKind.Write => "write", NesEmulator.Debugging.AccessKind.ReadWrite => "rw", _ => "exec",
    };

    public override JsonObject RemoveBreakpoint(int? id, bool all)
    {
        Control("removing breakpoints");
        if (all) return Obj(Delete("/api/debug/breakpoints"));
        if (id is not int i) throw new ToolError("id or all:true is needed");
        return Obj(Delete($"/api/debug/breakpoints/{i}"));
    }

    public override JsonObject Pause() { Control("pausing the game"); return AfterBreak(Post("/api/debug/pause?waitMs=5000")); }

    public override JsonObject Continue() { Control("letting the game go"); return Obj(Post("/api/debug/continue")); }

    public override JsonObject Run(long frames, List<PadStep>? script)
    {
        Control("running frames");
        if (script != null) throw new ToolError("a scripted pad is for headless sessions; in a live session use input (press / hold)");
        var st = Get("/api/debug/state") ?? throw new ToolError("no state");
        long now = st["frame"]?.GetValue<long>() ?? 0;
        frames = Math.Clamp(frames, 1, 36_000);
        int wait = (int)Math.Min(120_000, frames * 1000 / 50 + 5000);
        return AfterBreak(Post("/api/debug/step", new { frame = now + frames, waitMs = wait }));
    }

    public override JsonObject RunUntil(string? spec, string kind, string? condition, long? frame, int? scanline, long maxFrames, string cpu)
    {
        Control("running until a point");
        int wait = (int)Math.Clamp(maxFrames * 1000 / 50 + 5000, 1000, 120_000);
        if (frame is long f) return AfterBreak(Post("/api/debug/step", new { frame = f, waitMs = wait }));
        if (scanline is int l) return AfterBreak(Post("/api/debug/step", new { scanline = l, waitMs = wait }));
        if (string.IsNullOrWhiteSpace(spec)) throw new ToolError("a live session runs until an address (spec), a frame or a scanline");
        if (string.IsNullOrWhiteSpace(condition) && KindText(kind) == "exec") return AfterBreak(Post("/api/debug/step", new { to = spec, cpu, waitMs = wait }));
        EnsureAttached();
        var bp = Post("/api/debug/breakpoints", new { spec, kind = KindText(kind), cpu, condition });
        int id = bp?["breakpoint"]?["id"]?.GetValue<int>() ?? -1;
        Post("/api/debug/continue");
        var w = Get($"/api/debug/wait?timeoutMs={wait}");
        if (id >= 0) { try { Delete($"/api/debug/breakpoints/{id}"); } catch { } }
        return AfterBreak(w);
    }

    public override JsonObject Step(string mode, int count, string cpu, string? to, long? frame, int? scanline)
    {
        Control("stepping");
        if (!string.IsNullOrWhiteSpace(to)) return AfterBreak(Post("/api/debug/step", new { to, cpu }));
        if (frame is long f) return AfterBreak(Post("/api/debug/step", new { frame = f }));
        if (scanline is int l) return AfterBreak(Post("/api/debug/step", new { scanline = l }));
        JsonNode? r = null;
        for (int i = 0; i < Math.Clamp(count, 1, 200); i++) r = Post("/api/debug/step", new { mode = mode is "" or "instruction" ? "into" : mode, cpu });
        return AfterBreak(r);
    }

    public override JsonObject SetRegister(string cpu, string name, long value) { Control("writing a register"); return Obj(Post("/api/debug/registers", new { cpu, name, value })); }

    public override JsonObject WriteMemory(string? spec, string? domain, long? address, byte[] data, string cpu)
    {
        Control("writing memory");
        if (string.IsNullOrWhiteSpace(domain) || address == null) throw new ToolError("in a live session memory is written by domain and address (list_domains); a spec or symbol: read it first to learn its place");
        return Obj(Post("/api/memory/poke-range", new { domain, address = (int)address.Value, data = data.Select(b => (int)b).ToArray() }));
    }

    public override JsonObject Input(string mode, string[] buttons, int frames, int player, List<PadStep>? script, long? runFrames)
    {
        Control("pressing buttons in the user's game");
        switch (mode)
        {
            case "press": return Obj(Post("/api/input/set-buttons", new { player = player + 1, buttons, holdMs = Math.Clamp(frames, 1, 600) * 1000 / 60 }));
            case "hold": return Obj(Post("/api/input/set-buttons", new { player = player + 1, buttons }));
            case "release": return Obj(Post("/api/input/clear"));
            default: throw new ToolError("a live session takes press, hold or release (a scripted pad is for headless sessions)");
        }
    }

    public override JsonObject LoadState(string name)
    {
        Control("loading a state");
        if (!name.Equals("quick", StringComparison.OrdinalIgnoreCase)) throw new ToolError("a live session loads the app's own quick state only (name: \"quick\"); states saved in headless sessions stay there");
        return Obj(Post("/api/emulator/quick-load-state"));
    }

    public override JsonObject SaveState(string name) => throw new ToolError("live sessions never save (the user's states and saves are theirs); save states in a headless session");
    public override JsonObject Reset() => throw new ToolError("live sessions never reset the user's game (that is progression); reset a headless session");
    public override JsonObject TraceFrame(long frame, string cpu, int lines, string? function, List<string> ranges) => throw new ToolError("tracing a whole frame writes a file from the app; do it in a headless session (open the same ROM), or read the trace ring here (trace)");
    public override JsonObject Checks(string action, List<string> off, List<string> wait, long from, int top) => throw new ToolError("the hardware checks run in headless sessions (open the same ROM with checks:true); the desktop app does not run them");
    public override JsonObject RecordSound(double seconds, bool wav) => throw new ToolError("recording sound runs in headless sessions; the desktop app's API has no recorder");
}
