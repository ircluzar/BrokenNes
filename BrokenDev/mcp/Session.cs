using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace BrokenNes.Mcp;

/// <summary>A scheduled pad change: from <see cref="Frame"/> on (relative to now when parsed), these buttons are held.</summary>
internal readonly record struct PadStep(long Frame, string[] Buttons);

/// <summary>
/// One emulator the agent drives: a headless one in this process (<see cref="HeadlessSession"/>) or the user's running desktop app
/// (<see cref="LiveSession"/>). Every tool maps onto one of these calls; a session that cannot do one says why.
/// Calls on one session are serialized (<see cref="Gate"/>).
/// </summary>
internal abstract class Session
{
    public string Id { get; init; } = "";
    public abstract string Kind { get; }
    public abstract string ConsoleKey { get; }
    public object Gate { get; } = new();
    public DateTime OpenedUtc { get; } = DateTime.UtcNow;

    public abstract JsonObject Describe();
    public abstract JsonObject Status();
    public abstract void Close();

    protected ToolError No(string what) => new($"{what} is not available on this {Kind} session ({ConsoleKey})");

    // running
    public virtual JsonObject Run(long frames, List<PadStep>? script) => throw No("running frames");
    public virtual JsonObject RunUntil(string? spec, string kind, string? condition, long? frame, int? scanline, long maxFrames, string cpu) => throw No("run until");
    public virtual JsonObject Pause() => throw No("pause");
    public virtual JsonObject Continue() => throw No("continue");
    public virtual JsonObject Reset() => throw No("reset");
    // input
    public virtual JsonObject Input(string mode, string[] buttons, int frames, int player, List<PadStep>? script, long? runFrames) => throw No("input");
    // memory
    public virtual JsonObject Domains() => throw No("memory domains");
    public virtual JsonObject ReadMemory(string? spec, string? domain, long? address, int? length, string cpu) => throw No("reading memory");
    public virtual JsonObject WriteMemory(string? spec, string? domain, long? address, byte[] data, string cpu) => throw No("writing memory");
    public virtual JsonObject SearchMemory(string domain, string op, long? value, int width, bool fresh, int show) => throw No("memory search");
    public virtual JsonObject Symbols(string? load, string? find, long? address, string? prefix, string cpu, int limit) => throw No("symbols");
    // debugger
    public virtual JsonObject AddBreakpoint(string spec, string kind, string? condition, string? hits, bool tracepoint, string? log, string cpu) => throw No("breakpoints");
    public virtual JsonObject ListBreakpoints(int hits) => throw No("breakpoints");
    public virtual JsonObject RemoveBreakpoint(int? id, bool all) => throw No("breakpoints");
    public virtual JsonObject Watch(string action, string? expr, int? index) => throw No("watches");
    public virtual JsonObject Step(string mode, int count, string cpu, string? to, long? frame, int? scanline) => throw No("stepping");
    public virtual JsonObject CallStack(string cpu) => throw No("the call stack");
    public virtual JsonObject Registers(string cpu) => throw No("registers");
    public virtual JsonObject SetRegister(string cpu, string name, long value) => throw No("writing registers");
    public virtual JsonObject Trace(int count, string cpu) => throw No("the trace");
    public virtual JsonObject TraceFrame(long frame, string cpu, int lines, string? function, List<string> ranges) => throw No("tracing a frame");
    public virtual JsonObject Disassemble(string? address, int count, string cpu) => throw No("disassembly");
    public virtual JsonObject Evaluate(string expr, string cpu) => throw No("expressions");
    // picture
    public virtual ToolOut Screenshot(int scale) => throw No("the screenshot");
    public virtual ToolOut Layers(List<string> layers, int scale) => throw No("layers");
    public virtual ToolOut Tiles(string? source, int bpp, int palette, long start, int count, int scale) => throw No("the tile viewer");
    public virtual ToolOut Sprites(bool draw, int scale) => throw No("the sprite list");
    // sound
    public virtual JsonObject RecordSound(double seconds, bool wav) => throw No("recording sound");
    // states
    public virtual JsonObject SaveState(string name) => throw No("save states");
    public virtual JsonObject LoadState(string name) => throw No("loading states");
    public virtual JsonObject DiffStates(string a, string b, int limit) => throw No("state diffs");
    // analysis
    public virtual JsonObject Checks(string action, List<string> off, List<string> wait, long from, int top) => throw No("the hardware checks");
}

/// <summary>Every open session, by id ("h1", "h2", ... headless; "live-<pid>-n" live).</summary>
internal static class Sessions
{
    private static readonly ConcurrentDictionary<string, Session> all = new(StringComparer.OrdinalIgnoreCase);
    private static int next;

    public static string NewId(string prefix) => $"{prefix}{Interlocked.Increment(ref next)}";
    public static void Add(Session s) => all[s.Id] = s;
    public static IEnumerable<Session> All => all.Values.OrderBy(s => s.OpenedUtc);

    public static Session Get(string id) =>
        all.TryGetValue(id, out var s) ? s : throw new ToolError($"no session '{id}' (open ones: {(all.IsEmpty ? "none" : string.Join(", ", all.Keys))}; open one with open_rom, open_project or attach_live)");

    public static bool Remove(string id, out Session? s) => all.TryRemove(id, out s);

    public static void CloseAll()
    {
        foreach (var id in all.Keys.ToList())
            if (all.TryRemove(id, out var s)) { try { s.Close(); } catch { } }
    }
}
