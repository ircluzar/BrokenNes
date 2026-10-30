using System.Text.Json;
using System.Text.Json.Nodes;
using BrokenNes.Workshop.Debug;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BrokenNes.Mcp;

/// <summary>
/// The scripted MCP clients (BrokenDev/tests): each starts this program as an MCP server over stdio (the way Claude Code does), lists its
/// tools and calls them, checking real results with negative controls.
/// <code>
///   --client-test [--keep dir]                       headless sessions on every console, the debugger, picture, sound, states, checks
///   --demo [--keep dir] [--out transcript.json]      the "done when" demo: find two planted bugs in a test ROM with the tools only
///   --live-test --ro-pid N --ctl-pid M --roms dir   live sessions against two desktop apps (control off / on in their scratch configs)
/// </code>
/// Prints a line per check and one JSON line last; exit 0 when every check passed.
/// </summary>
internal static class ClientTest
{
    public enum Suite { Client, Demo, Live }

    private sealed class Res
    {
        public bool IsError; public string Text = ""; public JsonNode? J; public List<byte[]> Images = new();
        public JsonNode? this[string k] => J?[k];
        public override string ToString() => IsError ? "ERROR: " + Text : Text.Length > 300 ? Text[..300] + "..." : Text;
    }

    private static McpClient client = null!;
    private static readonly List<(string name, bool ok, string detail)> results = new();
    private static readonly JsonArray transcript = new();

    private static void Check(string name, bool ok, string detail)
    {
        results.Add((name, ok, detail));
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name} - {detail}");
    }

    private static async Task<Res> Call(string tool, object? args = null, bool log = true)
    {
        var el = JsonSerializer.SerializeToElement(args ?? new { });
        var dict = new Dictionary<string, object?>();
        foreach (var p in el.EnumerateObject()) dict[p.Name] = p.Value.Clone();
        var r = await client.CallToolAsync(tool, dict);
        var res = new Res { IsError = r.IsError == true };
        foreach (var c in r.Content)
        {
            if (c is TextContentBlock t) res.Text += t.Text;
            else if (c is ImageContentBlock im) res.Images.Add(im.DecodedData.ToArray());
        }
        if (!res.IsError) { try { res.J = JsonNode.Parse(res.Text); } catch { } }
        if (log) transcript.Add(new JsonObject { ["tool"] = tool, ["args"] = JsonNode.Parse(el.GetRawText()), ["error"] = res.IsError, ["result"] = res.J?.DeepClone() ?? res.Text, ["images"] = res.Images.Count });
        return res;
    }

    private static string S(JsonNode? n) => n?.ToString() ?? "";
    private static long L(JsonNode? n) => n == null ? long.MinValue : n.GetValueKind() == JsonValueKind.Number ? n.GetValue<long>() : long.TryParse(n.ToString(), out long v) ? v : long.MinValue;
    private static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G';

    public static int Run(string[] args, Suite suite) => RunAsync(args, suite).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args, Suite suite)
    {
        string Opt(string n, string d) { int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string dir = Opt("keep", Path.Combine(Path.GetTempPath(), $"brokennes-mcp-test-{Environment.ProcessId}"));
        Directory.CreateDirectory(dir);
        var serverArgs = new List<string>();
        if (BrokenDevBridge.RepoOverride is { } repo) { serverArgs.Add("--repo"); serverArgs.Add(repo); }
        // Timeflow's recordings go to a scratch folder (the server inherits it), never the real BROKENDEV_HOME\timeflow
        Environment.SetEnvironmentVariable("BROKENDEV_TIMEFLOW", Path.Combine(dir, "timeflow"));
        var transport = new StdioClientTransport(new StdioClientTransportOptions { Name = "brokennes", Command = Environment.ProcessPath!, Arguments = serverArgs });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            client = await McpClient.CreateAsync(transport);
            var tools = await client.ListToolsAsync();
            var names = tools.Select(t => t.Name).ToHashSet();
            string[] want = { "open_rom", "open_project", "attach_live", "list_instances", "list_sessions", "close_session", "status", "run", "run_until", "pause", "reset", "input",
                "read_memory", "write_memory", "search_memory", "list_domains", "symbols", "breakpoint_add", "breakpoint_list", "breakpoint_remove", "watch", "step", "call_stack",
                "registers", "trace", "trace_frame", "disassemble", "evaluate", "screenshot", "layers", "tiles", "sprites", "record_sound", "save_state", "load_state", "diff_states",
                "checks", "profile", "brokendev", "step_back", "open_movie", "movie", "bisect", "bugs", "debug_port",
                "timeflow_record", "timeflow_list", "timeflow_jump", "timeflow_resume" };
            var missing = want.Where(w => !names.Contains(w)).ToList();
            Check("tools_listed", missing.Count == 0 && tools.All(t => t.JsonSchema.ValueKind == JsonValueKind.Object), $"{tools.Count} tools over stdio; missing: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}");
            switch (suite)
            {
                case Suite.Client: await ClientSuite(dir); break;
                case Suite.Demo: await DemoSuite(dir, Opt("out", "")); break;
                case Suite.Live: await LiveSuite(int.Parse(Opt("ro-pid", "0")), int.Parse(Opt("ctl-pid", "0")), Opt("roms", dir)); break;
            }
        }
        catch (Exception e) { Check("run", false, e.GetType().Name + ": " + e.Message); }
        finally { if (client != null) await client.DisposeAsync(); }
        int pass = results.Count(r => r.ok), fail = results.Count - pass;
        Console.WriteLine(J.Text(new JsonObject
        {
            ["suite"] = suite.ToString().ToLowerInvariant(), ["pass"] = fail == 0, ["checks"] = results.Count, ["passed"] = pass, ["failed"] = fail, ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            ["failures"] = J.Strs(results.Where(r => !r.ok).Select(r => r.name)),
        }));
        return fail == 0 ? 0 : 1;
    }

    // ==================================================================================================================== the client test

    /// <summary>The debug port's test ROMs (Workshop's PortTestRoms) per console: the ROM, its symbols, and the negative control's ROM.</summary>
    private static IEnumerable<(string key, string rom, string sym, string neg)> PortRoms(string dir)
    {
        var (nr, _, nm) = PortTestRoms.Nes(); var (nn, _, _) = PortTestRoms.Nes(0x4018);
        yield return ("nes", DebugTestRoms.Write(dir, "porttest.nes", nr), DebugTestRoms.Write(dir, "porttest.mlb", nm), DebugTestRoms.Write(dir, "porttest_neg.nes", nn));
        var (sr, _, ss) = PortTestRoms.Snes(); var (sn, _, _) = PortTestRoms.Snes(0x4240);
        yield return ("snes", DebugTestRoms.Write(dir, "porttest.sfc", sr), DebugTestRoms.Write(dir, "porttest.sym", ss), DebugTestRoms.Write(dir, "porttest_neg.sfc", sn));
        var (gr, _, gs) = PortTestRoms.GameBoy(); var (gn, _, _) = PortTestRoms.GameBoy(false);
        yield return ("gb", DebugTestRoms.Write(dir, "porttest.gb", gr), DebugTestRoms.Write(dir, "porttest_gb.sym", gs), DebugTestRoms.Write(dir, "porttest_neg.gb", gn));
    }

    private static async Task ClientSuite(string dir)
    {
        var (nr, na, nm) = DebugTestRoms.Nes();
        var (sr, sa, ss, sy) = DebugTestRoms.Snes();
        var (gr, ga, gs, gn) = DebugTestRoms.GameBoy();
        var (pr, pa, pm) = PlantedBug.Build(fixedBuild: false);
        string nes = DebugTestRoms.Write(dir, "debugtest.nes", nr), nesSym = DebugTestRoms.Write(dir, "debugtest.mlb", nm);
        string snes = DebugTestRoms.Write(dir, "debugtest.sfc", sr), snesSym = DebugTestRoms.Write(dir, "debugtest.sym", sy);
        string gb = DebugTestRoms.Write(dir, "debugtest.gb", gr), gbNoi = DebugTestRoms.Write(dir, "debugtest_gb.noi", gn);
        string game = DebugTestRoms.Write(dir, "planted.nes", pr), gameSym = DebugTestRoms.Write(dir, "planted.mlb", pm);
        string sms = DebugTestRoms.Write(dir, "idle.sms", PlantedBug.SmsIdle());
        var (mdr, mdm, mdz, mdn) = SegaDebugTestRoms.Genesis();
        string md = DebugTestRoms.Write(dir, "debugtest.md", mdr), mdSym = DebugTestRoms.Write(dir, "debugtest_md_symbols.txt", mdn);
        string H(Asm a, string l) => "$" + a[l].ToString("X4");

        // ---------------------------------------------------------------- NES: a session, a breakpoint on a symbol, the stack, the trace, stepping
        var o = await Call("open_rom", new { path = nes, symbols = new[] { nesSym } });
        string h1 = S(o["session"]);
        Check("nes_open", !o.IsError && S(o["console"]) == "nes" && S(o["cores"]) == "CPU_FIX/PPU_FIX/APU_FIX" && L(o["symbols"]) > 0, $"{h1}: {S(o["description"])}, {L(o["symbols"])} symbols");
        var bad = await Call("open_rom", new { path = Path.Combine(dir, "nothing.nes") });
        Check("open_missing_rom_refused", bad.IsError && bad.Text.Contains("no ROM"), bad.Text);
        var bp = await Call("breakpoint_add", new { session = h1, spec = "sub2" });
        var r = await Call("run", new { session = h1, frames = 60 });
        Check("nes_break_on_symbol", S(r["outcome"]) == "break" && S(r["break"]?["pc"]) == H(na, "sub2") && S(r["break"]?["pcSymbol"]) == "sub2", $"{S(bp["breakpoint"]?["text"])}; {S(r["break"]?["text"])}");
        var st = await Call("call_stack", new { session = h1 });
        var frames = st["frames"] as JsonArray;
        Check("nes_call_stack", frames?.Count == 2 && S(frames[0]?["targetSymbol"]) == "sub2" && S(frames[1]?["targetSymbol"]) == "sub1", string.Join(" | ", frames?.Select(f => S(f?["text"])) ?? Array.Empty<string>()));
        var tr = await Call("trace", new { session = h1, count = 3 });
        var lines = (tr["lines"] as JsonArray)?.Select(S).ToList() ?? new();
        Check("nes_trace", lines.Count == 3 && lines.Any(l => l.Contains("JSR sub2")), string.Join(" || ", lines));
        await Call("breakpoint_remove", new { session = h1, all = true });
        await Call("step", new { session = h1, mode = "out" });
        var so = await Call("step", new { session = h1, mode = "out" });
        Check("nes_step_out", S(so["break"]?["pc"]) == H(na, "inc10"), S(so["break"]?["text"]));
        var si = await Call("step", new { session = h1, mode = "into", count = 3 });
        Check("nes_step_into_count", S(si["outcome"]) == "break" && (si["passed"] as JsonArray)?.Count == 2, $"after 3 steps: {S(si["break"]?["text"])}");
        var dis = await Call("disassemble", new { session = h1, address = H(na, "loop"), count = 2 });
        Check("nes_disassemble", S(dis["lines"]?[0]?["text"]) == "JSR sub1" && S(dis["lines"]?[0]?["label"]) == "loop", $"{S(dis["lines"]?[0]?["bytes"])} {S(dis["lines"]?[0]?["text"])} ; {S(dis["lines"]?[0]?["label"])}");
        var rg = await Call("registers", new { session = h1 });
        Check("nes_registers", rg["registers"]?["A"] != null && S(rg["cpu"]) == "main" && S(rg["pc"]).StartsWith("$C"), $"PC {S(rg["pc"])} {S(rg["instruction"])} A={S(rg["registers"]?["A"])}");

        // memory by symbol, through a mirror, writes, expressions, a mirror watch
        var m1 = await Call("read_memory", new { session = h1, spec = "counter" });
        await Call("run", new { session = h1, frames = 30 });
        var m2 = await Call("read_memory", new { session = h1, spec = "counter" });
        Check("nes_memory_by_symbol", L(m2["value"]) > L(m1["value"]) && S(m1["resolved"]).Contains("$0010", StringComparison.OrdinalIgnoreCase) || L(m2["value"]) > L(m1["value"]), $"counter {L(m1["value"])} -> {L(m2["value"])} after 30 frames ({S(m1["resolved"])})");
        var nosym = await Call("read_memory", new { session = h1, spec = "no_such_symbol" });
        Check("nes_unknown_symbol_refused", nosym.IsError, nosym.Text);
        var w = await Call("write_memory", new { session = h1, spec = "$0812", data = new[] { 0x5A } });
        var m3 = await Call("read_memory", new { session = h1, domain = "RAM", address = 0x12, length = 1 });
        Check("nes_write_through_mirror", w["verified"]?.GetValue<bool>() == true && L(m3["value"]) == 0x5A, $"wrote $0812 = 5A; RAM $0012 reads {S(m3["hex"])}");
        var ev = await Call("evaluate", new { session = h1, expr = "[counter] + 1" });
        var m4 = await Call("read_memory", new { session = h1, spec = "counter" });
        Check("nes_evaluate", L(ev["value"]) == L(m4["value"]) + 1, $"[counter] + 1 = {L(ev["value"])} (counter {L(m4["value"])})");
        var wb = await Call("breakpoint_add", new { session = h1, spec = "mirror_target", kind = "write" });
        var wr = await Call("run", new { session = h1, frames = 5 });
        Check("nes_mirror_watch", S(wr["break"]?["address"]) == "$0812" && S(wr["break"]?["accessPc"]) == H(na, "sta_mirror"), $"{S(wb["breakpoint"]?["resolved"])}: {S(wr["break"]?["text"])}");
        await Call("breakpoint_remove", new { session = h1, all = true });
        var cond = await Call("breakpoint_add", new { session = h1, spec = "counter", kind = "write", condition = "value == 0" });
        var cr = await Call("run", new { session = h1, frames = 600 });
        Check("nes_conditional_watch", S(cr["outcome"]) == "break" && S(cr["break"]?["value"]) == "$00", $"{S(cond["breakpoint"]?["text"])}: {S(cr["break"]?["text"])}");
        await Call("breakpoint_remove", new { session = h1, all = true });
        var bl = await Call("breakpoint_list", new { session = h1 });
        Check("nes_breakpoints_cleared", (bl["breakpoints"] as JsonArray)?.Count == 0 && L(bl["totalHits"]) > 0, $"{(bl["breakpoints"] as JsonArray)?.Count} breakpoints, {L(bl["totalHits"])} hits so far");
        var ru = await Call("run_until", new { session = h1, condition = "[counter] >= 200", max_frames = 2000 });
        var m5 = await Call("read_memory", new { session = h1, spec = "counter" });
        Check("nes_run_until_condition", ru["reached"]?.GetValue<bool>() == true && L(m5["value"]) >= 200, $"reached at frame {L(ru["frame"])}, counter {L(m5["value"])}");
        var neverHit = await Call("run_until", new { session = h1, spec = "never", max_frames = 30 });
        Check("nes_run_until_never_reached", neverHit["reached"]?.GetValue<bool>() == false && S(neverHit["outcome"]) == "frames", $"'never' (nothing jumps there): reached {S(neverHit["reached"])}, {S(neverHit["note"])}");
        long fNow = L((await Call("status", new { session = h1 }))["frame"]);
        var rf = await Call("run_until", new { session = h1, frame = fNow + 10 });
        Check("nes_run_until_frame", L(rf["frame"]) == fNow + 10, $"frame {fNow} -> {L(rf["frame"])}");
        var past = await Call("run_until", new { session = h1, frame = 1 });
        Check("nes_run_until_past_frame_refused", past.IsError, past.Text);
        var wa = await Call("watch", new { session = h1, action = "add", expr = "[counter]" });
        var wv = await Call("run", new { session = h1, frames = 3 });
        Check("nes_watch_values", (wv["watches"] as JsonArray)?.Count == 1 && wv["watches"]?[0]?["value"] != null, $"watch [counter] = {S(wv["watches"]?[0]?["value"])}");
        var tf = await Call("trace_frame", new { session = h1, frame = L(wv["frame"]) + 1, lines = 5 });
        Check("nes_trace_frame", L(tf["linesWritten"]) > 100 && File.Exists(S(tf["traceFile"])) && (tf["head"] as JsonArray)?.Count == 5, $"{L(tf["linesWritten"])} lines in {Path.GetFileName(S(tf["traceFile"]))}; first: {S(tf["head"]?[0])}");
        var sy1 = await Call("symbols", new { session = h1, find = "counter" });
        Check("nes_symbols_find", S(sy1["symbol"]?["address"]) == "$0010" && S(sy1["symbol"]?["space"]) == "cpu", S(sy1["symbol"]?["text"]));

        // states: save, diff by symbol, load
        var sv = await Call("save_state", new { session = h1, name = "a" });
        var before = await Call("read_memory", new { session = h1, spec = "counter" });
        await Call("run", new { session = h1, frames = 7 });
        var df = await Call("diff_states", new { session = h1, a = "a", b = "current" });
        var vars = (df["variables"] as JsonArray)?.Select(v => S(v?["variable"])).ToList() ?? new();
        Check("nes_diff_by_symbol", vars.Contains("counter") && vars.Contains("frames") && !vars.Contains("xval"), $"{L(df["variablesDiffering"])} variables differ: {string.Join(", ", vars)} (xval, always 3, does not)");
        var ld = await Call("load_state", new { session = h1, name = "a" });
        var after = await Call("read_memory", new { session = h1, spec = "counter" });
        Check("nes_state_round_trip", !sv.IsError && !ld.IsError && L(after["value"]) == L(before["value"]), $"counter {L(before["value"])} saved, 7 frames later loaded back: {L(after["value"])}");
        var nost = await Call("load_state", new { session = h1, name = "nope" });
        Check("nes_unknown_state_refused", nost.IsError, nost.Text);

        // the picture
        var shot = await Call("screenshot", new { session = h1, scale = 2 });
        Check("nes_screenshot_image", shot.Images.Count == 1 && IsPng(shot.Images[0]) && L(shot["width"]) == 256 && L(shot["height"]) == 240, $"{shot.Images.FirstOrDefault()?.Length} PNG bytes, {L(shot["width"])}x{L(shot["height"])} x2, hash {S(shot["hash"])}");
        var tl = await Call("tiles", new { session = h1 });
        Check("nes_tiles_image", tl.Images.Count == 1 && IsPng(tl.Images[0]) && L(tl["tiles"]) == 512, $"{L(tl["tiles"])} tiles, {S(tl["what"])}");
        var sp = await Call("sprites", new { session = h1 });
        Check("nes_sprites", !sp.IsError && sp["sprites"] is JsonArray && sp.Images.Count == 1, $"{L(sp["count"])} sprites on screen");

        // two sessions at once are deterministic (and a different run is not: the negative control)
        var x1 = await Call("open_rom", new { path = nes, call_stack = false });
        var x2 = await Call("open_rom", new { path = nes, call_stack = false });
        var x3 = await Call("open_rom", new { path = nes, call_stack = false });
        string s1 = S(x1["session"]), s2 = S(x2["session"]), s3 = S(x3["session"]);
        await Call("run", new { session = s1, frames = 120 }); await Call("run", new { session = s2, frames = 120 }); await Call("run", new { session = s3, frames = 121 });
        string Ram(Res q) => S(q["hex"]);
        var r1 = Ram(await Call("read_memory", new { session = s1, domain = "RAM", address = 0, length = 2048 }, log: false));
        var r2 = Ram(await Call("read_memory", new { session = s2, domain = "RAM", address = 0, length = 2048 }, log: false));
        var r3 = Ram(await Call("read_memory", new { session = s3, domain = "RAM", address = 0, length = 2048 }, log: false));
        Check("deterministic_sessions", r1 == r2 && r1.Length > 0, $"{s1} and {s2}: 120 frames each, the same 2 KB of RAM");
        Check("deterministic_negative_control", r1 != r3, $"{s3} ran 121 frames: its RAM differs");
        var ls = await Call("list_sessions");
        Check("list_sessions", (ls["sessions"] as JsonArray)?.Count >= 4, $"{(ls["sessions"] as JsonArray)?.Count} open");
        foreach (var x in new[] { s1, s2, s3 }) await Call("close_session", new { session = x });
        var gone = await Call("status", new { session = s1 });
        Check("closed_session_refused", gone.IsError && gone.Text.Contains("no session"), gone.Text);

        // ---------------------------------------------------------------- the planted game: input, sound, a RAM search, the checks
        var pg = await Call("open_rom", new { path = game, symbols = new[] { gameSym }, checks = true });
        string hp = S(pg["session"]);
        await Call("run", new { session = hp, frames = 10 });
        var noPress = await Call("read_memory", new { session = hp, spec = "pad_a" });
        var pressed = await Call("input", new { session = hp, mode = "press", buttons = new[] { "A" }, frames = 4 });
        var duringPress = await Call("read_memory", new { session = hp, spec = "pad_a" });
        await Call("run", new { session = hp, frames = 2 });
        var released = await Call("read_memory", new { session = hp, spec = "pad_a" });
        Check("input_press", L(noPress["value"]) == 0 && L(duringPress["value"]) == 1 && L(released["value"]) == 0, $"pad_a: {L(noPress["value"])} before, {L(duringPress["value"])} while A was pressed, {L(released["value"])} after ({S(pressed["pressed"])})");
        var sc = await Call("input", new { session = hp, mode = "script", script = "0:A,3:,6:A,8:" , run_frames = 7 });
        var scv = await Call("read_memory", new { session = hp, spec = "pad_a" });
        Check("input_script", !sc.IsError && L(sc["framesRun"]) == 7 && L(scv["value"]) == 1, $"script ran {L(sc["framesRun"])} frames; pad_a {L(scv["value"])} at frame 7 (A again from 6)");
        var badBtn = await Call("input", new { session = hp, mode = "press", buttons = new[] { "Q" } });
        Check("input_unknown_button_refused", badBtn.IsError, badBtn.Text);
        var snd = await Call("record_sound", new { session = hp, seconds = 1 });
        Check("sound_tone", snd["silent"]?.GetValue<bool>() == false && L(snd["nonSilentFrames"]) > 50 && File.Exists(S(snd["wav"])), $"peak {S(snd["peakDb"])} dB, rms {S(snd["rmsDb"])} dB, {L(snd["nonSilentFrames"])} frames with sound, {Path.GetFileName(S(snd["wav"]))}");
        var quiet = await Call("record_sound", new { session = h1, seconds = 1, wav = false });
        Check("sound_negative_control", quiet["silent"]?.GetValue<bool>() == true, $"the debugger test ROM plays nothing: peak {S(quiet["peakDb"])} dB");
        await Call("search_memory", new { session = hp, domain = "RAM", op = "unchanged", @new = true });
        await Call("run", new { session = hp, frames = 1 });
        var srch = await Call("search_memory", new { session = hp, domain = "RAM", op = "increased" });
        var cands = (srch["matches"] as JsonArray)?.Select(x => S(x?["offset"])).ToList() ?? new();
        Check("search_memory", cands.Contains("$0041") && L(srch["candidates"]) < 50, $"{L(srch["candidates"])} bytes increased in one frame, among them $0041 (score): {string.Join(" ", cands.Take(8))}");
        var rep = await Call("checks", new { session = hp, action = "report" });
        Check("checks_report", L(rep["findingCount"]) >= 1 && L(rep["profile"]?["frames"]) > 0, $"{L(rep["findingCount"])} findings, {L(rep["profile"]?["frames"])} frames profiled, busy {S(rep["profile"]?["busyPercentAverage"])}%");
        var noChecks = await Call("checks", new { session = h1, action = "report" });
        Check("checks_off_refused", noChecks.IsError, noChecks.Text);
        var prof = await Call("profile", new { session = hp, top = 5 });
        Check("profile", (prof["profile"]?["functions"] as JsonArray)?.Count > 0, string.Join(" | ", (prof["profileText"] as JsonArray)?.Take(2).Select(S) ?? Array.Empty<string>()));

        // ---------------------------------------------------------------- SNES (with the SPC700), WideMess, the Game Boy, a Sega console
        var so1 = await Call("open_rom", new { path = snes, symbols = new[] { snesSym } });
        string hs = S(so1["session"]);
        Check("snes_open", S(so1["console"]) == "snes" && (so1["cpus"] as JsonArray)?.Count == 2, $"{hs}: {S(so1["description"])}");
        await Call("run", new { session = hs, frames = 30 });
        var vr = await Call("read_memory", new { session = hs, spec = "vram:$2000", length = 4 });
        Check("snes_vram_by_spec", S(vr["hex"]) == "11 22 33 44", $"vram:$2000 = {S(vr["hex"])} (the test ROM's DMA)");
        var spcR = await Call("registers", new { session = hs, cpu = "spc" });
        Check("snes_spc700_registers", S(spcR["name"]) == "SPC700" && spcR["registers"]?["PC"] != null, $"SPC700 PC {S(spcR["pc"])} {S(spcR["instruction"])}");
        await Call("breakpoint_add", new { session = hs, spec = "mirror_target", kind = "write" });
        var smw = await Call("run", new { session = hs, frames = 10 });
        Check("snes_mirror_watch", S(smw["break"]?["address"]) == "$800013", S(smw["break"]?["text"]));
        await Call("breakpoint_remove", new { session = hs, all = true });
        var lay = await Call("layers", new { session = hs, layers = new[] { "bg1" } });
        var afterLay = await Call("status", new { session = hs });
        Check("snes_layers", lay.Images.Count == 1 && IsPng(lay.Images[0]) && L(afterLay["frame"]) == L(lay["frame"]), $"bg1 alone: {L(lay["width"])}x{L(lay["height"])}, the machine back in frame {L(afterLay["frame"])}");
        var stl = await Call("tiles", new { session = hs, bpp = 4, count = 64 });
        Check("snes_tiles", stl.Images.Count == 1 && L(stl["tiles"]) == 64, S(stl["what"]));
        var wm = await Call("open_rom", new { path = snes, console = "wms" });
        string hw = S(wm["session"]);
        var wmr = await Call("run", new { session = hw, frames = 20 });
        Check("widemess_session", S(wm["console"]) == "wms" && S(wmr["outcome"]) == "frames" && (wm["cpus"] as JsonArray)?.Count >= 1, $"{hw}: {S(wm["description"])}");
        var go = await Call("open_rom", new { path = gb, symbols = new[] { gbNoi } });
        string hg = S(go["session"]);
        await Call("breakpoint_add", new { session = hg, spec = "_bank2_func" });
        var gbr = await Call("run", new { session = hg, frames = 30 });
        Check("gb_banked_symbol", S(go["console"]) is "gb" or "gbc" && S(gbr["break"]?["pc"]) == "$4000", $"{S(go["description"])}: {S(gbr["break"]?["text"])}");
        var gsp = await Call("sprites", new { session = hg, draw = false });
        Check("gb_sprites", !gsp.IsError && gsp["sprites"] is JsonArray, $"{L(gsp["count"])} sprites");
        var mo = await Call("open_rom", new { path = sms, console = "sms" });
        string hm = S(mo["session"]);
        var mr = await Call("run", new { session = hm, frames = 30 });
        var ms = await Call("screenshot", new { session = hm });
        Check("sega_session", S(mo["console"]) == "sms" && S(mr["outcome"]) == "frames" && ms.Images.Count == 1, $"{hm}: {S(mo["description"])}; {L(mr["framesRun"])} frames, picture {L(ms["width"])}x{L(ms["height"])}");
        // the Sega debugger (Timeflow T0): the Z80 of the Master System, the 68000 and the sound Z80 of the Genesis
        await Call("breakpoint_add", new { session = hm, spec = "$0010" });
        var smb = await Call("run", new { session = hm, frames = 30 });
        var smd = await Call("disassemble", new { session = hm, address = "$7FE0", count = 1 });
        var smr = await Call("registers", new { session = hm });
        Check("sega_breakpoint", S(smb["outcome"]) == "break" && S(smb["break"]?["pc"]) == "$0010" && S(smd["lines"]?[0]?["text"]) == "JP $0003" && smr.Text.Contains("\"IX\""),
              $"{S(smb["break"]?["text"])}; {S(smd["lines"]?[0]?["text"])}");
        await Call("breakpoint_remove", new { session = hm, all = true });
        var mdo = await Call("open_rom", new { path = md, symbols = new[] { mdSym } });
        string hmd = S(mdo["session"]);
        await Call("breakpoint_add", new { session = hmd, spec = "sub2" });
        var mdb = await Call("run", new { session = hmd, frames = 30 });
        var mds = await Call("call_stack", new { session = hmd });
        var mdf = mds["frames"] as JsonArray;
        Check("genesis_68000_break_and_stack", S(mdb["outcome"]) == "break" && S(mdb["break"]?["pcSymbol"]) == "sub2" && mdf?.Count == 2 && S(mdf[0]?["kind"]) == "BSR" && S(mdf[1]?["kind"]) == "JSR" && S(mdf[1]?["targetSymbol"]) == "sub1",
              $"{S(mdb["break"]?["text"])}; {string.Join(" | ", mdf?.Select(f => S(f?["text"])) ?? Array.Empty<string>())}");
        await Call("breakpoint_remove", new { session = hmd, all = true });
        await Call("breakpoint_add", new { session = hmd, spec = "$" + mdz["zsub"].ToString("X4"), cpu = "z80" });
        var zb = await Call("run", new { session = hmd, frames = 30 });
        var zt = await Call("trace", new { session = hmd, count = 2, cpu = "z80" });
        Check("genesis_z80_break_and_trace", S(zb["outcome"]) == "break" && S(zb["break"]?["cpu"]) == "z80" && (zt["lines"] as JsonArray)?.Select(S).Any(l => l.Contains("CALL $" + mdz["zsub"].ToString("X4"))) == true,
              $"{S(zb["break"]?["text"])}; {string.Join(" || ", (zt["lines"] as JsonArray)?.Select(S) ?? Array.Empty<string>())}");
        var noCpu = await Call("breakpoint_add", new { session = hmd, spec = "$0000", cpu = "spc" });
        Check("genesis_unknown_cpu_refused", noCpu.IsError && noCpu.Text.Contains("z80"), noCpu.Text);
        await Call("close_session", new { session = hmd }, log: false);
        var dom = await Call("list_domains", new { session = hm });
        Check("sega_domains", (dom["domains"] as JsonArray)?.Count > 0, string.Join(", ", (dom["domains"] as JsonArray)?.Select(d => S(d?["id"])) ?? Array.Empty<string>()));

        // ---------------------------------------------------------------- live sessions refuse without a desktop app; BrokenDev; a real project if built
        var nolive = await Call("attach_live", new { pid = 1 });
        Check("live_unknown_pid_refused", nolive.IsError, nolive.Text);
        var inst = await Call("list_instances");
        Check("list_instances", inst["instances"] is JsonArray, $"{(inst["instances"] as JsonArray)?.Count} discovery files in {S(inst["folder"])}");
        var bd = await Call("brokendev", new { verb = "list" });
        var ids = (bd["result"] as JsonArray)?.Select(x => S(x?["Id"])).ToList() ?? new();
        Check("brokendev_list", bd["ok"]?.GetValue<bool>() == true && ids.Contains("vrun-nes"), $"{ids.Count} projects: {string.Join(", ", ids)}");
        var bdBad = await Call("brokendev", new { verb = "build", project = "no-such-project" });
        Check("brokendev_unknown_project_refused", bdBad.IsError, bdBad.Text);
        var proj = await Call("open_project", new { project = "vrun-nes", call_stack = false });
        if (proj.IsError) Check("open_project_vrun_nes", proj.Text.Contains("build"), "not built on this machine: " + proj.Text);
        else
        {
            string hv = S(proj["session"]);
            var vrr = await Call("run", new { session = hv, frames = 120 });
            var vs = await Call("screenshot", new { session = hv });
            Check("open_project_vrun_nes", S(proj["console"]) == "nes" && L(proj["symbols"]) > 100 && L(vs["colours"]) > 1 && L(vrr["framesRun"]) == 120, $"{S(proj["rom"])}: {L(proj["symbols"])} symbols, 120 frames, {L(vs["colours"])} colours");
            await Call("close_session", new { session = hv });
        }
        foreach (var x in new[] { h1, hp, hs, hw, hg, hm }) await Call("close_session", new { session = x }, log: false);
        await MovieSuite(dir, sms);
    }

    // ==================================================================================================================== movies, step back, divergence (D5)

    private static async Task MovieSuite(string dir, string sms)
    {
        string Planted(string name, string settings)
        {
            var (rom, mlb) = BrokenNes.Workshop.Movies.MovieTestRoms.PlantedGame(BrokenNes.Workshop.Movies.MovieTestRoms.ParseSettings(settings));
            File.WriteAllText(Path.Combine(dir, name + ".mlb"), mlb);
            return DebugTestRoms.Write(dir, name + ".nes", rom);
        }
        string good = Planted("mgood", "version 1\nstart Start\nfloor 1\nbonus 1\n"), bad = Planted("mbad", "version 1\nstart Start\nfloor 0\nbonus 1\n"), menu = Planted("mmenu", "version 1\nstart Select\nfloor 1\nbonus 1\n");
        string Sym(string rom) => Path.ChangeExtension(rom, ".mlb");

        // a session's input since power-on, saved as a movie, replayed in sync; on the regression it goes off course
        var o = await Call("open_rom", new { path = good, symbols = new[] { Sym(good) } });
        string h = S(o["session"]);
        await Call("input", new { session = h, mode = "script", script = "60:Start,66:,200:A,260:", run_frames = 600 });
        var sv = await Call("movie", new { action = "save", session = h, path = Path.Combine(dir, "session") });
        string mv = S(sv["file"]);
        Check("movie_save", !sv.IsError && File.Exists(mv) && L(sv["frames"]) == 600 && L(sv["keyframes"]) == 10 && S(sv["start"]) == "power-on", $"{mv}: {L(sv["frames"])} frames, {L(sv["inputChanges"])} input changes, {L(sv["keyframes"])} keyframes");
        var info = await Call("movie", new { action = "info", movie = mv });
        Check("movie_info", S(info["script1"]) == "60:Start,66:,200:A,260:" && S(info["format"]).Length > 0, $"{S(info["format"])} {S(info["extension"])}: {S(info["script1"])}");
        var om = await Call("open_movie", new { movie = mv, rom = good, symbols = new[] { Sym(good) } });
        Check("open_movie_in_sync", om["inSync"]?.GetValue<bool>() == true && L(om["keyframesMatched"]) == 10 && om["sameRom"]?.GetValue<bool>() == true, $"{L(om["keyframesMatched"])} of {L(om["keyframesChecked"])} keyframes, frame {L(om["frame"])}");
        await Call("close_session", new { session = S(om["session"]) }, log: false);
        var ob = await Call("open_movie", new { movie = mv, rom = bad, symbols = new[] { Sym(bad) } });
        Check("open_movie_off_course_on_the_regression", ob["inSync"]?.GetValue<bool>() == false && S(ob["offCourse"]).Contains("300") && ob["sameRom"]?.GetValue<bool>() == false, S(ob["offCourse"]));
        await Call("close_session", new { session = S(ob["session"]) }, log: false);

        // step back: to the exact instruction, and by frames
        await Call("close_session", new { session = h }, log: false);
        o = await Call("open_rom", new { path = good, symbols = new[] { Sym(good) } });
        h = S(o["session"]);
        await Call("input", new { session = h, mode = "script", script = "60:Start,66:", run_frames = 70 });
        var r = await Call("run_until", new { session = h, spec = "lose_life" });
        var at = await Call("registers", new { session = h });
        await Call("step", new { session = h, count = 4 });
        var moved = await Call("registers", new { session = h });
        var back = await Call("step_back", new { session = h, instructions = 4 });
        var again = await Call("registers", new { session = h });
        string Regs(Res x) => $"{S(x["pc"])} {x["registers"]?.ToJsonString()} {S(x["cycle"])}";
        Check("step_back_instructions", !back.IsError && r["reached"]?.GetValue<bool>() == true && Regs(again) == Regs(at) && Regs(moved) != Regs(at), $"{Regs(again)} (the breakpoint's {Regs(at)}); {back["steppedBack"]?.ToJsonString()}");
        await Call("run", new { session = h, frames = 5 });
        var f0 = await Call("status", new { session = h });
        var m0 = await Call("read_memory", new { session = h, spec = "frames" });
        await Call("run", new { session = h, frames = 3 });
        var bf = await Call("step_back", new { session = h, frames = 3 });
        var m1 = await Call("read_memory", new { session = h, spec = "frames" });
        Check("step_back_frames", !bf.IsError && L(bf["steppedBack"]?["toFrame"]) == L(f0["framesRun"]) && L(m1["value"]) == L(m0["value"]), $"to frame {L(bf["steppedBack"]?["toFrame"])} (was {L(f0["framesRun"])}); frames {L(m1["value"])} (then {L(m0["value"])})");
        var far = await Call("step_back", new { session = h, frames = 100000 });
        Check("step_back_too_far_refused", far.IsError, far.Text);
        var both = await Call("step_back", new { session = h });
        Check("step_back_needs_a_count", both.IsError, both.Text);
        await Call("close_session", new { session = h }, log: false);
        var sm = await Call("open_rom", new { path = sms });
        string hs = S(sm["session"]);
        await Call("run", new { session = hs, frames = 130 });
        var smi = await Call("step_back", new { session = hs, instructions = 1 });
        var smf = await Call("step_back", new { session = hs, frames = 60 });
        Check("step_back_sega", !smi.IsError && !smf.IsError && L(smf["steppedBack"]?["toFrame"]) == 70, $"instructions: {(smi.IsError ? smi.Text : "stepped back 1")}; frames: to {L(smf["steppedBack"]?["toFrame"])}");
        await Call("close_session", new { session = hs }, log: false);

        // the divergence finder, the state diff by symbol, bisect's test, the exports
        var dv = await Call("movie", new { action = "diverge", movie = mv, rom = good, symbols = new[] { Sym(good) }, rom_b = bad, symbols_b = new[] { Sym(bad) } });
        var v0 = dv["stateAfter"]?["variables"]?[0];
        Check("movie_diverge", dv["diverged"]?.GetValue<bool>() == true && L(dv["frame"]) == 252 && S(dv["firstInstruction"]?["pcSymbolA"]) == "lose_life+$2" && S(v0?["variable"]) == "lives", S(dv["summary"]));
        var same = await Call("movie", new { action = "diverge", movie = mv, rom = good, symbols = new[] { Sym(good) } });
        Check("movie_diverge_same_build_does_not", same["diverged"]?.GetValue<bool>() == false, S(same["summary"]));
        var tg = await Call("movie", new { action = "test", movie = mv, rom = good, symbols = new[] { Sym(good) }, test = "[lives] >= 1 at 600" });
        var tb = await Call("movie", new { action = "test", movie = mv, rom = bad, symbols = new[] { Sym(bad) }, test = "[lives] >= 1 at 600" });
        var tm = await Call("movie", new { action = "test", movie = mv, rom = menu, symbols = new[] { Sym(menu) }, test = "[app_state] == 1 at 600" });
        Check("movie_test_verdicts", L(tg["exitCode"]) == 0 && L(tb["exitCode"]) == 1 && S(tm["verdict"]) is "bad" or "cant-tell", $"good {S(tg["verdict"])}, regression {S(tb["verdict"])}, menu {S(tm["verdict"])} ({S(tm["why"])})");
        var ex = await Call("movie", new { action = "export", movie = mv, rom = good, format = "bk2", @out = Path.Combine(dir, "session.bk2") });
        Check("movie_export_bk2", !ex.IsError && File.Exists(Path.Combine(dir, "session.bk2")), $"{S(ex["file"])} {ex["notes"]?.ToJsonString()}");
        var exBad = await Call("movie", new { action = "export", movie = mv, rom = bad, format = "fm2" });
        Check("movie_export_refuses_another_rom", exBad.IsError && exBad.Text.Contains("not the movie's"), exBad.Text);
        var nomovie = await Call("movie", new { action = "info", movie = Path.Combine(dir, "nothing.bnm") });
        Check("movie_missing_refused", nomovie.IsError && nomovie.Text.Contains("no movie"), nomovie.Text);

        // the debug port (D7): the port test ROMs' messages, an assert that parks the session, the timers; the negative control writes beside the port
        foreach (var (key, file, symf, neg) in PortRoms(dir))
        {
            var po = await Call("open_rom", new { path = file, symbols = new[] { symf } });
            string ph = S(po["session"]);
            var pr1 = await Call("run", new { session = ph, frames = 10 });
            long newEvents = L(pr1["debugPort"]?["new"]);
            if (key == "gb")
            {
                // the Game Boy ROM's bare ld b,b comes first: the game's own break; the next run reaches the assert
                Check("port_gb_break_parks", S(pr1["outcome"]) == "break" && S(pr1["break"]?["reason"]) == "port-break" && S(pr1["break"]?["pcSymbol"]) == "bare_break", $"{S(pr1["outcome"])}: {S(pr1["break"]?["text"])}");
                pr1 = await Call("run", new { session = ph, frames = 10 });
                newEvents += L(pr1["debugPort"]?["new"]);
            }
            Check($"port_{key}_assert_parks", S(pr1["outcome"]) == "break" && S(pr1["break"]?["reason"]) == "assert" && S(pr1["break"]?["text"]).Contains("frame counter < 3"), $"{S(pr1["outcome"])}: {S(pr1["break"]?["text"])}");
            Check($"port_{key}_run_result_has_new_events", newEvents >= 3 && L(pr1["debugPort"]?["asserts"]) == 1, $"{newEvents} new events in the run results");
            await Call("run", new { session = ph, frames = 6 }, log: false);
            var pe = await Call("debug_port", new { session = ph, action = "events", kinds = new[] { "print", "marker" } });
            var texts = (pe["events"] as JsonArray)?.Select(e => S(e?["text"])).ToList() ?? new();
            Check($"port_{key}_events", texts.Contains("hello from the port") && texts.Contains("start") && texts.Contains("five frames"), string.Join(" | ", texts));
            var pt = await Call("debug_port", new { session = ph, action = "timers" });
            var work = (pt["timers"] as JsonArray)?.FirstOrDefault(t => S(t?["name"]) == "work");
            Check($"port_{key}_timers", work != null && L(work["calls"]) >= 4 && L(work["best"]) == L(work["worst"]) && (key != "nes" || L(work["worst"]) == 57), $"work: {work?.ToJsonString()}");
            await Call("close_session", new { session = ph }, log: false);
            var no = await Call("open_rom", new { path = neg });
            string nh = S(no["session"]);
            var nr1 = await Call("run", new { session = nh, frames = 10 });
            var ne = await Call("debug_port", new { session = nh, action = "events" });
            Check($"port_{key}_negative_control", S(nr1["outcome"]) == "frames" && nr1["debugPort"] == null && L(ne["total"]) == 0, $"{S(nr1["outcome"])}, {L(ne["total"])} events");
            await Call("close_session", new { session = nh }, log: false);
        }
        var smsPort = await Call("open_rom", new { path = sms });
        var smsNo = await Call("debug_port", new { session = S(smsPort["session"]), action = "events" });
        Check("port_sega_refused", smsNo.IsError && smsNo.Text.Contains("no debug port"), smsNo.Text);
        await Call("close_session", new { session = S(smsPort["session"]) }, log: false);

        // the bug captures (D6): a capture made the way the apps make one (the last 30 s of 40 s of play), listed, replayed, opened in a session
        string bugs = Path.Combine(dir, "bugs");
        var cap = BrokenNes.Workshop.Bugs.BugCli.Make(good, null, "", 2400, "60:Start,66:,200:A,260:,900:Start,906:,1500:A,1506:", "", out var meta);
        meta.Note = "client test";
        string bundle = NesEmulator.Debugging.Capture.BugBundle.WriteFolder(bugs, NesEmulator.Debugging.Capture.BugBundle.Files(cap, meta), DateTime.Now);
        string bugId = Path.GetFileName(bundle);
        var bl = await Call("bugs", new { action = "list", folder = bugs });
        Check("bugs_list", !bl.IsError && L(bl["count"]) == 1 && S(bl["bugs"]?[0]?["id"]) == bugId && S(bl["bugs"]?[0]?["note"]) == "client test", $"{L(bl["count"])} bundle(s): {S(bl["bugs"]?[0]?["id"])} '{S(bl["bugs"]?[0]?["note"])}'");
        var bp = await Call("bugs", new { action = "play", bug = bugId, folder = bugs });
        Check("bugs_play_in_sync", !bp.IsError && bp["inSync"]?.GetValue<bool>() == true && L(bp["exitCode"]) == 0, S(bp["summary"]));
        var bo = await Call("bugs", new { action = "open", bug = bundle, symbols = new[] { Sym(good) } });
        string bh = S(bo["session"]);
        Check("bugs_open_session_in_sync", !bo.IsError && bo["inSync"]?.GetValue<bool>() == true && L(bo["keyframesMatched"]) >= 30, $"session {bh}: {L(bo["keyframesMatched"])} of {L(bo["keyframesChecked"])} keyframes, frame {L(bo["frame"])}");
        var lives = await Call("read_memory", new { session = bh, spec = "lives" });
        var bback = await Call("step_back", new { session = bh, frames = 60 });
        Check("bugs_open_debuggable", !lives.IsError && !bback.IsError, $"lives {lives["values"]?.ToJsonString() ?? lives.Text}; step_back {(bback.IsError ? bback.Text : "ok")}");
        await Call("close_session", new { session = bh }, log: false);
        // the negative controls: the same capture without its A press; an id no folder has; a project that does not exist
        string late = Path.Combine(dir, "late-bug");
        foreach (var f in Directory.GetFiles(bundle, "*", SearchOption.AllDirectories)) { string t = Path.Combine(late, Path.GetRelativePath(bundle, f)); Directory.CreateDirectory(Path.GetDirectoryName(t)!); File.Copy(f, t, true); }
        string mvf = Path.Combine(late, NesEmulator.Debugging.Capture.BugBundle.MovieFile), text = File.ReadAllText(mvf);
        var lines = text.Split('\n').ToList();
        int aAt = lines.FindIndex(l => l.StartsWith("i ") && l.Split(' ').Length > 2 && l.Split(' ')[2].Split('+').Contains("A"));
        var aParts = lines[aAt].Split(' '); aParts[2] = "-"; lines[aAt] = string.Join(' ', aParts);   // the A press (a life lost) taken out
        File.WriteAllText(mvf, string.Join('\n', lines));
        var bpl = await Call("bugs", new { action = "play", bug = late });
        Check("bugs_play_without_the_a_press_off_course", !bpl.IsError && bpl["inSync"]?.GetValue<bool>() == false && L(bpl["exitCode"]) == 1, S(bpl["summary"]));
        var bu = await Call("bugs", new { action = "info", bug = "no-such-bug", folder = bugs });
        Check("bugs_unknown_refused", bu.IsError && bu.Text.Contains("no bug"), bu.Text);
        var bpr = await Call("bugs", new { action = "list", project = "no-such-project" });
        Check("bugs_unknown_project_refused", bpr.IsError && bpr.Text.Contains("unknown project"), bpr.Text);

        // ---------------------------------------------------------------- Timeflow (T1, T2): a headless session's own recording, the list, jump, resume, a branch
        await TimeflowSuite(dir, sms);
    }

    // ==================================================================================================================== the demo (done when)

    /// <summary>Timeflow on headless sessions (milestones T1, T2): record, list, jump into a new session, replay in sync, a branch; each with a negative control.</summary>
    private static async Task TimeflowSuite(string dir, string sms)
    {
        var (pr, _, pm) = PlantedBug.Build(fixedBuild: false);
        string game = DebugTestRoms.Write(dir, "timeflow.nes", pr), gameSym = DebugTestRoms.Write(dir, "timeflow.mlb", pm);
        var o = await Call("open_rom", new { path = game, symbols = new[] { gameSym } });
        string h = S(o["session"]);
        var st0 = await Call("timeflow_record", new { session = h, action = "status" });
        Check("timeflow_off_by_default", !st0.IsError && st0["recording"]?.GetValue<bool>() == false && st0["timeline"] == null, st0.Text);
        var on = await Call("timeflow_record", new { session = h, action = "start" });
        await Call("run", new { session = h, frames = 300, input = "0:Start,8:,60:Right,120:Right+A,130:Right,200:Left,260:" });
        var off = await Call("timeflow_record", new { session = h, action = "stop" });
        string tid = S(off["closed"]?["id"]);
        Check("timeflow_record_headless", !on.IsError && !off.IsError && L(off["closed"]?["frames"]) == 300 && off["recording"]?.GetValue<bool>() == false, $"{tid}: {L(off["closed"]?["frames"])} frames, {L(off["closed"]?["storedBytes"])} bytes");
        var ls = await Call("timeflow_list", new { timeline = tid });
        Check("timeflow_list", !ls.IsError && L(ls["frames"]) == 300 && S(ls["status"]) == "closed" && S(ls["source"]) == "mcp" && L(ls["verifyFailures"]) == 0 && L(ls["verifiedFrames"]) > 0, $"{S(ls["status"])}, {L(ls["frames"])} frames, restore = record checked on {L(ls["verifiedFrames"])}, {S(ls["dir"])}");
        var all = await Call("timeflow_list");
        Check("timeflow_list_all", !all.IsError && all.Text.Contains(tid) && S(all["root"]).Contains("timeflow"), S(all["root"]));
        var j = await Call("timeflow_jump", new { timeline = tid, frame = 150, symbols = new[] { gameSym } });
        string jh = S(j["session"]);
        Check("timeflow_jump_new_session", !j.IsError && L(j["timelineFrame"]) == 150 && S(j["state"]).Length == 16 && jh.Length > 0, $"{jh} at frame {L(j["timelineFrame"])}, state {S(j["state"])}");
        var rp = await Call("timeflow_resume", new { session = jh, mode = "replay" });
        Check("timeflow_replay_in_sync", !rp.IsError && rp["inSync"]?.GetValue<bool>() == true && L(rp["framesRun"]) == 150, $"{L(rp["framesRun"])} frames, {L(rp["checks"])} checks, in sync {S(rp["inSync"])} {S(rp["offFrame"])} {S(rp["offWhat"])}");
        var jx = await Call("timeflow_jump", new { timeline = tid, frame = 150, call_stack = false });
        var rpx = await Call("timeflow_resume", new { session = S(jx["session"]), mode = "replay" });
        Check("timeflow_replay_in_sync_no_call_stack", !rpx.IsError && rpx["inSync"]?.GetValue<bool>() == true, $"{L(rpx["framesRun"])} frames, in sync {S(rpx["inSync"])} {S(rpx["offFrame"])} {S(rpx["offWhat"])}");
        await Call("close_session", new { session = S(jx["session"]) }, log: false);
        // a branch: the jumped session records, plays new input: a timeline whose parent is tid at 150
        var j2 = await Call("timeflow_jump", new { timeline = tid, frame = 150, session = jh });
        await Call("timeflow_record", new { session = jh, action = "start" });
        await Call("input", new { session = jh, mode = "hold", buttons = new[] { "Left", "B" } });
        var lv = await Call("timeflow_resume", new { session = jh, mode = "live", frames = 90 });
        var boff = await Call("timeflow_record", new { session = jh, action = "stop" });
        string bid = S(boff["closed"]?["id"]);
        var bl = await Call("timeflow_list", new { timeline = bid });
        Check("timeflow_branch", !j2.IsError && !lv.IsError && S(bl["parent"]) == tid && L(bl["parentFrame"]) == 150 && L(bl["startFrame"]) == 150 && L(bl["frames"]) == 90, $"{bid}: parent {S(bl["parent"])} at {S(bl["parentFrame"])}, frames {L(bl["startFrame"])}+{L(bl["frames"])}");
        // the negative controls
        var far = await Call("timeflow_jump", new { timeline = tid, frame = 5000 });
        Check("timeflow_jump_outside_refused", far.IsError && far.Text.Contains("not in timeline"), far.Text);
        var none = await Call("timeflow_jump", new { timeline = "no-such-timeline", frame = 1 });
        Check("timeflow_unknown_timeline_refused", none.IsError && none.Text.Contains("no timeline"), none.Text);
        var so = await Call("open_rom", new { path = sms });
        string sh = S(so["session"]);
        var other = await Call("timeflow_jump", new { timeline = tid, frame = 10, session = sh });
        Check("timeflow_jump_other_machine_refused", other.IsError && (other.Text.Contains("recorded on the nes") || other.Text.Contains("another ROM")), other.Text);
        var nojump = await Call("timeflow_resume", new { session = sh, mode = "replay" });
        Check("timeflow_resume_needs_a_jump", nojump.IsError && nojump.Text.Contains("follows a timeflow_jump"), nojump.Text);
        var badReplay = await Call("timeflow_jump", new { timeline = tid, frame = 100, session = jh });
        await Call("write_memory", new { session = jh, domain = "RAM", address = 0x40, data = new[] { 0x77 } });
        var off2 = await Call("timeflow_resume", new { session = jh, mode = "replay", frames = 60 });
        Check("timeflow_replay_off_when_changed", !badReplay.IsError && off2["inSync"]?.GetValue<bool>() == false, $"a RAM byte written after the jump: in sync {S(off2["inSync"])}, off at {S(off2["offFrame"])} ({S(off2["offWhat"])})");
        foreach (var id in new[] { h, jh, sh }) await Call("close_session", new { session = id }, log: false);
    }

    private static async Task DemoSuite(string dir, string outPath)
    {
        // The game and its symbol file, as a game repo ships them. The "agent" below knows only that the game ends too early and asks the tools.
        var (pr, pa, pm) = PlantedBug.Build(fixedBuild: false);
        var (fr, fa, fm) = PlantedBug.Build(fixedBuild: true);
        string game = DebugTestRoms.Write(dir, "planted.nes", pr), sym = DebugTestRoms.Write(dir, "planted.mlb", pm);
        string fixedGame = DebugTestRoms.Write(dir, "planted_fixed.nes", fr), fixedSym = DebugTestRoms.Write(dir, "planted_fixed.mlb", fm);

        // 1. run the game headless with the hardware checks from power-on
        var o = await Call("open_rom", new { path = game, symbols = new[] { sym }, checks = true });
        string h = S(o["session"]);
        await Call("run", new { session = h, frames = 300 });
        var rep = await Call("checks", new { session = h, action = "report" });
        var uninit = (rep["findings"] as JsonArray)?.FirstOrDefault(f => S(f?["rule"]) == "nes.ram-uninit");
        string var1 = S(uninit?["place"]);
        Check("demo_bug1_checks_find_it", uninit != null && var1 == "bonus" && S(uninit["pcSymbol"]).StartsWith("add_bonus"),
            $"{S(uninit?["rule"])} at {var1} ({S(uninit?["address"])}): first frame {L(uninit?["firstFrame"])}, pc {S(uninit?["pcSymbol"])}, stack {S(uninit?["callStack"])}");

        // 2. look at the read with the debugger, in a fresh session (deterministic: the same run)
        var o2 = await Call("open_rom", new { path = game, symbols = new[] { sym } });
        string h2 = S(o2["session"]);
        await Call("breakpoint_add", new { session = h2, spec = var1, kind = "read", hits = "==1" });
        var hit = await Call("run", new { session = h2, frames = 300 });
        var stack = await Call("call_stack", new { session = h2 });
        var val = await Call("read_memory", new { session = h2, spec = var1 });
        var writes = await Call("breakpoint_list", new { session = h2 });
        Check("demo_bug1_debugger_confirms", S(hit["break"]?["access"]) == "read" && S(hit["break"]?["accessPcSymbol"]) == "add_bonus" && (stack["frames"] as JsonArray)?.Any(f => S(f?["targetSymbol"]) == "update") == true,
            $"the first read of {var1}: {S(hit["break"]?["text"])}; value then {S(val["valueHex"])} (power-on RAM); stack {string.Join(" <- ", (stack["frames"] as JsonArray)?.Select(f => S(f?["targetSymbol"])) ?? Array.Empty<string>())}");
        // and no write came before it: a write watch from power-on in a third session breaks only after the read
        var o3 = await Call("open_rom", new { path = game, symbols = new[] { sym }, call_stack = false });
        string h3 = S(o3["session"]);
        await Call("breakpoint_add", new { session = h3, spec = var1, kind = "write", hits = "==1" });
        var firstWrite = await Call("run", new { session = h3, frames = 300 });
        Check("demo_bug1_no_write_before_read", S(firstWrite["break"]?["accessPcSymbol"]).StartsWith("add_bonus") && L(firstWrite["break"]?["cycle"]) > L(hit["break"]?["cycle"]) && L(firstWrite["break"]?["frame"]) >= L(hit["break"]?["frame"]),
            $"the first write of {var1} is {S(firstWrite["break"]?["accessPcSymbol"])} (frame {L(firstWrite["break"]?["frame"])}, cycle {L(firstWrite["break"]?["cycle"])}), after the read (cycle {L(hit["break"]?["cycle"])}): nothing initialises it");

        // 3. the second report: the game ends too early. Watch game_over being set and look around.
        var o4 = await Call("open_rom", new { path = game, symbols = new[] { sym } });
        string h4 = S(o4["session"]);
        await Call("breakpoint_add", new { session = h4, spec = "game_over", kind = "write", condition = "value != 0" });
        var go = await Call("run", new { session = h4, frames = 600 });
        var lives = await Call("read_memory", new { session = h4, spec = "lives" });
        var tr = await Call("trace", new { session = h4, count = 6 });
        var lines = (tr["lines"] as JsonArray)?.Select(S).ToList() ?? new();
        // the function the call stack names (the innermost frame's target), disassembled by its symbol
        var gstack = await Call("call_stack", new { session = h4 });
        string fn = S(gstack["frames"]?[0]?["targetSymbol"]);
        var disasm = await Call("disassemble", new { session = h4, address = fn, count = 3 });
        string branch = S(disasm["lines"]?[1]?["text"]);
        bool sawBranch = lines.Any(l => l.Contains("BEQ")) && lines.Any(l => l.Contains("DEC"));
        Check("demo_bug2_found", fn == "lose_life" && S(go["outcome"]) == "break" && L(lives["value"]) == 2 && branch.StartsWith("BEQ") && sawBranch,
            $"game_over set at frame {L(go["break"]?["frame"])} by {S(go["break"]?["accessPcSymbol"])} with lives = {L(lives["value"])}, inside {fn} (the call stack); {fn}: {string.Join("; ", (disasm["lines"] as JsonArray)?.Select(l => S(l?["text"])) ?? Array.Empty<string>())} (BEQ where BNE was meant); trace: {string.Join(" || ", lines.TakeLast(4))}");

        // 4. the negative control: the fixed build has neither (checks clean, the game goes on past the first hit)
        var o5 = await Call("open_rom", new { path = fixedGame, symbols = new[] { fixedSym }, checks = true });
        string h5 = S(o5["session"]);
        await Call("breakpoint_add", new { session = h5, spec = "game_over", kind = "write", condition = "value != 0" });
        var fx = await Call("run", new { session = h5, frames = 150 });
        var fl = await Call("read_memory", new { session = h5, spec = "lives" });
        var frep = await Call("checks", new { session = h5, action = "stop" });
        var fixedUninit = (frep["findings"] as JsonArray)?.Count(f => S(f?["rule"]) == "nes.ram-uninit") ?? -1;
        Check("demo_fixed_build_clean", S(fx["outcome"]) == "frames" && L(fl["value"]) == 1 && fixedUninit == 0,
            $"the fixed build: no game over after two hits (lives {L(fl["value"])}), {fixedUninit} RAM-read-before-written findings ({L(frep["findingCount"])} findings in all: {string.Join(", ", (frep["findings"] as JsonArray)?.Select(f => S(f?["rule"])) ?? Array.Empty<string>())})");
        foreach (var x in new[] { h, h2, h3, h4, h5 }) await Call("close_session", new { session = x }, log: false);
        if (outPath.Length > 0) File.WriteAllText(outPath, transcript.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ==================================================================================================================== live sessions

    private static async Task LiveSuite(int roPid, int ctlPid, string roms)
    {
        if (roPid <= 0 || ctlPid <= 0) { Check("live_args", false, "--ro-pid and --ctl-pid are needed (BrokenDev/tests/mcp-live.ps1 starts the two apps)"); return; }
        var labels = JsonNode.Parse(File.ReadAllText(Path.Combine(roms, "labels.json")))!;
        string sub2 = "$" + labels["nes"]!["sub2"]!.GetValue<uint>().ToString("X4");
        var inst = await Call("list_instances");
        var pids = (inst["instances"] as JsonArray)?.Where(i => i?["alive"]?.GetValue<bool>() == true).Select(i => L(i?["pid"])).ToList() ?? new();
        Check("live_instances_listed", pids.Contains(roPid) && pids.Contains(ctlPid), $"running: {string.Join(", ", pids)}");

        // the app WITHOUT enableDebuggerControl: reading works; control is refused by the session (read-only) and by the app (control:true)
        var ro = await Call("attach_live", new { pid = roPid });
        string a = S(ro["session"]);
        Check("live_readonly_open", !ro.IsError && ro["control"]?.GetValue<bool>() == false && S(ro["console"]) == "nes", $"{a}: {S(ro["game"])}; app allows control: {S(ro["appAllowsControl"])}");
        var reg = await Call("registers", new { session = a });
        var mem = await Call("read_memory", new { session = a, spec = "$0010", length = 2 });
        var shot = await Call("screenshot", new { session = a });
        Check("live_readonly_reads", reg["cpu"] != null && !mem.IsError && mem["hex"] != null && shot.Images.Count == 1 && IsPng(shot.Images[0]), $"registers PC {S(reg["cpu"]?["pcHex"])}; $0010 = {S(mem["hex"])}; screenshot {L(shot["width"])}x{L(shot["height"])}");
        var refused = await Call("pause", new { session = a });
        Check("live_readonly_refuses_pause", refused.IsError && refused.Text.Contains("read-only"), refused.Text);
        var refusedW = await Call("write_memory", new { session = a, domain = "System RAM", address = 0x30, data = new[] { 1 } });
        Check("live_readonly_refuses_write", refusedW.IsError && refusedW.Text.Contains("read-only"), refusedW.Text);
        var refusedS = await Call("save_state", new { session = a, name = "x" });
        Check("live_never_saves", refusedS.IsError && refusedS.Text.Contains("never save"), refusedS.Text);
        var ctlOnRo = await Call("attach_live", new { pid = roPid, control = true });
        string a2 = S(ctlOnRo["session"]);
        var appRefuses = await Call("breakpoint_add", new { session = a2, spec = sub2 });
        Check("live_app_without_control_refuses", appRefuses.IsError && appRefuses.Text.Contains("enableDebuggerControl"), appRefuses.Text);
        await Call("close_session", new { session = a }); await Call("close_session", new { session = a2 });

        // the app WITH enableDebuggerControl (its scratch config): still read-only unless the session is opened for control
        var ro2 = await Call("attach_live", new { pid = ctlPid });
        string b0 = S(ro2["session"]);
        var stillRefused = await Call("breakpoint_add", new { session = b0, spec = sub2 });
        Check("live_readonly_even_when_app_allows", ro2["appAllowsControl"]?.GetValue<bool>() == true && stillRefused.IsError && stillRefused.Text.Contains("read-only"), stillRefused.Text);
        await Call("close_session", new { session = b0 });
        var ctl = await Call("attach_live", new { pid = ctlPid, control = true });
        string b = S(ctl["session"]);
        var sy = await Call("symbols", new { session = b, load = Path.Combine(roms, "debugtest.mlb") });
        var until = await Call("run_until", new { session = b, spec = "sub2" });
        Check("live_control_break", !sy.IsError && until["parked"]?.GetValue<bool>() == true && S(until["lastBreak"]?["pcSymbol"]) == "sub2", $"symbols {S(sy["load"]?["loaded"])}; {S(until["lastBreak"]?["text"])}");
        var cs = await Call("call_stack", new { session = b });
        var frames = cs["frames"] as JsonArray;
        Check("live_call_stack", frames?.Count == 2 && S(frames[0]?["targetSymbol"]) == "sub2" && S(frames[1]?["targetSymbol"]) == "sub1", string.Join(" | ", frames?.Select(f => S(f?["text"])) ?? Array.Empty<string>()));
        var step = await Call("step", new { session = b, mode = "over" });
        Check("live_step", step["parked"]?.GetValue<bool>() == true, S(step["lastBreak"]?["text"]));
        var wr = await Call("write_memory", new { session = b, domain = "System RAM", address = 0x30, data = new[] { 0x5A } });
        var rd = await Call("read_memory", new { session = b, domain = "System RAM", address = 0x30, length = 1 });
        Check("live_control_write", !wr.IsError && L(rd["value"]) == 0x5A, $"System RAM $0030 = {S(rd["hex"])}");
        var inp = await Call("input", new { session = b, mode = "press", buttons = new[] { "A" }, frames = 3 });
        Check("live_control_input", !inp.IsError, inp.ToString());
        var cl = await Call("close_session", new { session = b });
        await Task.Delay(800);
        var after = await Call("attach_live", new { pid = ctlPid });
        string c = S(after["session"]);
        var s1 = await Call("status", new { session = c }); await Task.Delay(600); var s2 = await Call("status", new { session = c });
        Check("live_close_lets_the_game_go", !cl.IsError && s2["state"]?["attached"]?.GetValue<bool>() == false && L(s2["state"]?["frame"]) > L(s1["state"]?["frame"]),
            $"after close: attached {S(s2["state"]?["attached"])}, frames {L(s1["state"]?["frame"])} -> {L(s2["state"]?["frame"])}; the process is still running: {System.Diagnostics.Process.GetProcessById(ctlPid) is { HasExited: false }}");
        // Timeflow: an agent can only ASK the user's app to record; nothing records until the user allows it (no one answers here)
        var tr = await Call("timeflow_record", new { session = c, action = "request", reason = "the MCP live test asks (nobody answers)" });
        await Task.Delay(1500);
        var ts = await Call("timeflow_record", new { session = c, action = "status" });
        Check("live_timeflow_request_records_nothing", !tr.IsError && S(tr["request"]) == "pending" && ts["recording"]?.GetValue<bool>() == false && S(ts["request"]?["state"]) == "pending" && ts["timeline"] == null,
              $"request {S(tr["request"])}; 1.5 s later: recording {S(ts["recording"])}, request {S(ts["request"]?["state"])}, timeline {S(ts["timeline"])}");
        var tstart = await Call("timeflow_record", new { session = c, action = "start" });
        Check("live_timeflow_start_refused", tstart.IsError && tstart.Text.Contains("only the user"), tstart.Text);
        var tc = await Call("timeflow_record", new { session = c, action = "cancel" });
        var ts2 = await Call("timeflow_record", new { session = c, action = "status" });
        Check("live_timeflow_cancel", !tc.IsError && S(ts2["request"]?["state"]) == "cancelled" && ts2["recording"]?.GetValue<bool>() == false, $"request {S(ts2["request"]?["state"])}, recording {S(ts2["recording"])}");
        await Call("close_session", new { session = c });
    }
}
