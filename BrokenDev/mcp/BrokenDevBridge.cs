using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace BrokenNes.Mcp;

/// <summary>
/// BrokenDev from the MCP server: a project's built ROM and symbols (to open it headless), and the brokendev.ps1 verbs (build, verify,
/// compat, run, list, status) run as a child process, their output and records returned. The repository is the one the server was
/// installed from (<c>--repo</c>), or found beside the server's own files.
/// </summary>
internal static class BrokenDevBridge
{
    public static string? RepoOverride { get; set; }

    public static string Repo
    {
        get
        {
            if (RepoOverride is { Length: > 0 } r) return r;
            if (Environment.GetEnvironmentVariable("BROKENNES_REPO") is { Length: > 0 } e) return e;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "BrokenDev", "brokendev.ps1"))) return d.FullName;
            throw new ToolError("the BrokenNes repository is not known: the server was started without --repo (brokendev.ps1 mcp install passes it)");
        }
    }

    public static string Script => Path.Combine(Repo, "BrokenDev", "brokendev.ps1");
    public static string Home => Environment.GetEnvironmentVariable("BROKENDEV_HOME") is { Length: > 0 } h ? Path.GetFullPath(h) : @"P:\BrokenDev\env";

    public static List<string> ProjectIds()
    {
        var dir = Path.Combine(Repo, "BrokenDev", "projects");
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "project.json"))).Select(Path.GetFileName).OfType<string>().OrderBy(x => x).ToList() : new();
    }

    public static JsonObject Manifest(string id)
    {
        var p = Path.Combine(Repo, "BrokenDev", "projects", id, "project.json");
        if (!File.Exists(p)) throw new ToolError($"no BrokenDev project '{id}' (known: {string.Join(", ", ProjectIds())})");
        return (JsonObject)JsonNode.Parse(File.ReadAllText(p))!;
    }

    public sealed record BuiltProject(string Id, string Rom, List<string> Symbols, string Console, string Cpu, string Ppu, string Apu, List<string> FrameWait, string BuiltAt);

    /// <summary>The project's last build: its ROM, its symbol files, the console it runs on and the cores its verify uses.</summary>
    public static BuiltProject Built(string id)
    {
        var m = Manifest(id);
        var rec = Path.Combine(Home, "out", id, "build.json");
        if (!File.Exists(rec)) throw new ToolError($"'{id}' has no build record ({rec}); build it first (the brokendev tool, verb build)");
        var b = JsonNode.Parse(File.ReadAllText(rec))!;
        if (b["ok"]?.GetValue<bool>() != true) throw new ToolError($"the last build of '{id}' failed ({rec}); build it again");
        var outs = (b["outputs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        string Published(JsonObject o) => o["published"]?.GetValue<string>() ?? "";
        var rom = outs.FirstOrDefault(o => o["id"]?.GetValue<string>() == "rom") ?? throw new ToolError($"the build record of '{id}' has no 'rom' output");
        var symbolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in (m["build"]?["outputs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            if (o["kind"]?.GetValue<string>() is "symbols" or "mlb" or "sym") symbolIds.Add(o["id"]!.GetValue<string>());
        var syms = outs.Where(o => symbolIds.Contains(o["id"]?.GetValue<string>() ?? "") || (o["id"]?.GetValue<string>() is "mlb" or "sym")).Select(Published).Where(File.Exists).Distinct().ToList();
        string console = (m["run"]?["console"] ?? m["console"])?.GetValue<string>() ?? "";
        if (console is "nested") console = "snes";
        var cores = m["verify"]?["cores"];
        var wait = (m["checks"]?["frameWait"] as JsonArray ?? new JsonArray()).Select(x => x!.GetValue<string>()).ToList();
        return new BuiltProject(id, Published(rom), syms, console, cores?["cpu"]?.GetValue<string>() ?? "", cores?["ppu"]?.GetValue<string>() ?? "", cores?["apu"]?.GetValue<string>() ?? "", wait,
            b["finishedAtUtc"]?.ToString() ?? "");
    }

    /// <summary>brokendev.ps1 bisect (milestone D5): the game's commits, a movie and a test; returns the bisect report (its JSON) and the output.</summary>
    public static JsonObject Bisect(string project, string movie, string test, string good, string bad, int timeoutSec)
    {
        if (!ProjectIds().Contains(project)) throw new ToolError($"unknown project '{project}' (known: {string.Join(", ", ProjectIds())})");
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Script, "bisect", project, "-Movie", movie, "-Test", test, "-Bad", bad, "-Json" };
        if (good.Length > 0) { args.Add("-Good"); args.Add(good); }
        var (code, text, done, secs) = RunScript(args, timeoutSec, killTree: true);
        var j = new JsonObject { ["project"] = project, ["exitCode"] = done ? code : -1, ["seconds"] = secs, ["firstBadFound"] = done && code == 0 };
        if (!done) j["error"] = $"bisect ran longer than {timeoutSec} s and was stopped (only this server's own child process)";
        int s = text.IndexOf('{'), e = text.LastIndexOf('}');
        try { if (s >= 0 && e > s) j["report"] = JsonNode.Parse(text[s..(e + 1)]); } catch { }
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.TrimStart().StartsWith('{') ).ToList();
        j["output"] = J.Strs(lines.Where(l => !l.Contains("step build:")).TakeLast(60));
        return j;
    }

    private static (int code, string text, bool done, double seconds) RunScript(List<string> args, int timeoutSec, bool killTree)
    {
        var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Repo };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new ToolError("powershell.exe did not start");
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        bool done = p.WaitForExit(Math.Clamp(timeoutSec, 10, 7200) * 1000);
        if (!done) { try { p.Kill(entireProcessTree: killTree); } catch { } }
        else p.WaitForExit();
        string text; lock (sb) text = sb.ToString();
        return (done ? p.ExitCode : -1, text, done, Math.Round(sw.Elapsed.TotalSeconds, 1));
    }

    /// <summary>Runs one brokendev.ps1 verb; returns its exit code, its output's last lines and the record it wrote.</summary>
    public static JsonObject RunVerb(string verb, string project, List<string> emu, List<string> scenario, int timeoutSec)
    {
        verb = verb.Trim().ToLowerInvariant();
        if (verb is not ("build" or "verify" or "compat" or "run" or "list" or "status" or "doctor"))
            throw new ToolError("verb is build, verify, compat, run, list, status or doctor (bisect is its own tool; bugs and coverage come with milestones D6 and D9)");
        var ids = ProjectIds();
        if (verb is "build" or "verify" or "compat" or "run" or "status" && !ids.Contains(project) && !(project == "all" && verb is "build" or "verify" or "compat"))
            throw new ToolError($"unknown project '{project}' (known: {string.Join(", ", ids)}{(verb is "run" or "status" ? "" : ", all")})");
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Script, verb };
        if (verb is not ("list" or "doctor")) args.Add(project);
        if (verb is "list" or "doctor") args.Add("-Json");
        if (emu.Count > 0) { args.Add("-Emu"); args.Add(string.Join(",", emu)); }
        if (scenario.Count > 0) { args.Add("-Scenario"); args.Add(string.Join(",", scenario)); }

        var before = verb == "run" ? LiveSession.Instances().Where(i => i.Alive).Select(i => i.Pid).ToHashSet() : new HashSet<int>();
        var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Repo };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new ToolError("powershell.exe did not start");
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        bool done = p.WaitForExit(Math.Clamp(timeoutSec, 10, 3600) * 1000);
        if (!done)
        {
            // our own child only (never anything by name): the verb ran past its time
            // (a 'run' never takes its tree down: the desktop app it started is the user's now)
            try { p.Kill(entireProcessTree: verb != "run"); } catch { }
        }
        else p.WaitForExit();
        string text; lock (sb) text = sb.ToString();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        var j = new JsonObject
        {
            ["verb"] = verb, ["project"] = project, ["exitCode"] = done ? p.ExitCode : -1, ["ok"] = done && p.ExitCode == 0, ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            ["command"] = "powershell " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
        };
        if (!done) j["error"] = $"the verb ran longer than {timeoutSec} s and was stopped (only this server's own child process)";
        if (verb is "list" or "doctor")
        {
            int start = text.IndexOfAny(new[] { '[', '{' });
            try { j["result"] = start >= 0 ? JsonNode.Parse(text[start..]) : null; } catch { j["output"] = J.Strs(lines.TakeLast(80)); }
        }
        else j["output"] = J.Strs(lines.Count > 80 ? lines.Take(10).Append($"... ({lines.Count - 70} lines) ...").Concat(lines.TakeLast(60)) : lines);
        if (verb is "build" or "verify" && project != "all")
        {
            var rec = Path.Combine(Home, "out", project, verb + ".json");
            if (File.Exists(rec)) { j["record"] = rec; try { j["recordOk"] = JsonNode.Parse(File.ReadAllText(rec))?["ok"]?.DeepClone(); } catch { } }
        }
        if (verb == "compat" && project != "all")
        {
            var rec = Path.Combine(Home, "out", project, "compat", "compat.json");
            if (File.Exists(rec)) j["record"] = rec;
        }
        if (verb == "run")
        {
            var started = LiveSession.Instances().Where(i => i.Alive && !before.Contains(i.Pid)).Select(i => i.Pid).ToList();
            j["startedPids"] = new JsonArray(started.Select(x => (JsonNode)x).ToArray());
            j["note"] = started.Count > 0 ? "attach_live with this PID to read it (read-only unless the user asked for control)" : "no new desktop app appeared (see the output)";
        }
        return j;
    }
}
