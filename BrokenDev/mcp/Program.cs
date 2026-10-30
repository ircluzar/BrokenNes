using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NesEmulator.Systems;

namespace BrokenNes.Mcp;

/// <summary>
/// <c>BrokenNes.Mcp.exe [--repo &lt;BrokenNes checkout&gt;]</c>: the BrokenNes MCP server on stdio (what Claude Code starts).
/// <c>--client-test</c>, <c>--demo</c> and <c>--live-test --app &lt;BrokenNes.Windows.exe&gt;</c>: the scripted MCP clients that test it
/// (each starts this program again as a server over stdio and talks MCP to it). <c>--list-tools</c> prints the tools.
/// </summary>
internal static class Program
{
    public static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0";

    public static int Main(string[] args)
    {
        string Opt(string n) { int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        bool Flag(string n) => args.Any(a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase));
        if (Opt("repo") is { Length: > 0 } repo) BrokenDevBridge.RepoOverride = Path.GetFullPath(repo);
        // the hosts that run WideMess turn it on at start-up (the desktop app and Workshop do the same)
        Consoles.WideMessEnabled = true;

        if (Flag("client-test")) return ClientTest.Run(args, ClientTest.Suite.Client);
        if (Flag("demo")) return ClientTest.Run(args, ClientTest.Suite.Demo);
        if (Flag("live-test")) return ClientTest.Run(args, ClientTest.Suite.Live);
        if (Opt("write-test-roms") is { Length: > 0 } romDir)
        {
            // the live test's ROM (the debugger tests' NES ROM, its labels) for the desktop apps it starts
            Directory.CreateDirectory(romDir);
            var (nr, na, nm) = BrokenNes.Workshop.Debug.DebugTestRoms.Nes();
            BrokenNes.Workshop.Debug.DebugTestRoms.Write(romDir, "debugtest.nes", nr);
            BrokenNes.Workshop.Debug.DebugTestRoms.Write(romDir, "debugtest.mlb", nm);
            File.WriteAllText(Path.Combine(romDir, "labels.json"), JsonSerializer.Serialize(new { nes = na.Labels.ToDictionary(k => k.Key, k => k.Value) }));
            Console.WriteLine($"wrote the live test's ROM to {romDir}");
            return 0;
        }
        if (Flag("list-tools")) { foreach (var t in Tools.All()) Console.WriteLine($"{t.Name,-18} {t.Description}"); return 0; }
        if (Flag("version")) { Console.WriteLine($"BrokenNes MCP server {Version}"); return 0; }
        return Serve().GetAwaiter().GetResult();
    }

    private const string Instructions =
        "BrokenNes, driven directly. HEADLESS sessions (open_rom, open_project) are BrokenNes's own emulator inside this server: invisible, deterministic, many at once, every console " +
        "(NES, SNES, WideMess, Game Boy / Color, Master System, Game Gear, Genesis); the debugger (breakpoints, mirror-aware watches, stepping, call stack, trace, symbols) covers NES, SNES with its SPC700, WideMess and Game Boy; " +
        "the D4 hardware checks and the profiler run with checks:true. Debug games here, not on Mesen (Mesen is for cross-checks). " +
        "LIVE sessions (attach_live) read the user's running desktop app: read freely; open with control:true only when the user asked in chat to control their game (and the app must have enableDebuggerControl on); " +
        "a live session never saves, never touches progression and never stops the process. Results are JSON (pictures as images). Typical: open_rom with checks:true, run 600, checks report, then breakpoint_add on what a finding names, run, call_stack, trace. " +
        "Timeflow: a recorded timeline (timeflow_record on your headless session, timeflow_list) can be profiled over any span at full depth with timeflow_profile and streaming queries ('writes hp', 'last-write hp before 900', 'became hp == 0', 'stack N', 'histogram'): the write that corrupted a variable, with its instruction and call stack, without playing again.";

    private static async Task<int> Serve()
    {
        // stdout belongs to the protocol: anything the emulator prints goes to stderr instead
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);

        var tools = Tools.All().ToDictionary(t => t.Name, StringComparer.Ordinal);
        var list = tools.Values.Select(t => new Tool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = JsonSerializer.SerializeToElement(t.Schema),
            Annotations = new ToolAnnotations { ReadOnlyHint = t.ReadOnly, DestructiveHint = false, OpenWorldHint = false },
        }).ToList();

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "brokennes", Version = Version, Title = "BrokenNes" },
            ServerInstructions = Instructions,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (ctx, ct) => ValueTask.FromResult(new ListToolsResult { Tools = list }),
                CallToolHandler = async (ctx, ct) =>
                {
                    var p = ctx.Params!;
                    if (!tools.TryGetValue(p.Name, out var tool)) return Error($"unknown tool '{p.Name}'");
                    var a = new Args(p.Arguments);
                    try { return Result(await Task.Run(() => tool.Run(a), ct)); }
                    catch (ToolError e) { return Error(e.Message); }
                    catch (Exception e) { return Error($"{tool.Name} failed: {e.GetType().Name}: {e.Message}"); }
                },
            },
        };

        // Timeflow T3: profiles an earlier server left behind (its process crashed or was killed) or that expired go now
        _ = Task.Run(() => { try { NesEmulator.Debugging.Timeflow.ProfileStore.Sweep(); } catch (Exception e) { Console.Error.WriteLine("[timeflow] profile sweep: " + e.Message); } });
        var transport = new StreamServerTransport(stdin, stdout, "brokennes");
        await using var server = McpServer.Create(transport, options);
        try { await server.RunAsync(); }
        finally
        {
            // the session's end: this server's profiles go unless kept (first: a client that closes may not wait for the sessions to close)
            try { int n = NesEmulator.Debugging.Timeflow.ProfileStore.EndSession(); if (n > 0) Console.Error.WriteLine($"[timeflow] {n} unkept profile(s) deleted at the session's end"); } catch (Exception) { }
            Sessions.CloseAll();
        }
        return 0;
    }

    public static CallToolResult Result(ToolOut o)
    {
        var content = new List<ContentBlock> { new TextContentBlock { Text = J.Text(o.Json) } };
        foreach (var png in o.Pngs) content.Add(ImageContentBlock.FromBytes(png, "image/png"));
        return new CallToolResult { Content = content, StructuredContent = JsonSerializer.SerializeToElement(o.Json), IsError = false };
    }

    public static CallToolResult Error(string message) => new()
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
        IsError = true,
    };
}
