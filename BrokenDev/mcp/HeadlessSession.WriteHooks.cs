using System.Text.Json.Nodes;
using NesEmulator.Debugging;

namespace BrokenNes.Mcp;

// Write hooks (BrokenDev debugging, 2026-11-27): log, break before, rewrite, drop or freeze CPU writes to an address range, on the NES,
// SNES / WideMess (65816), Game Boy, Master System / Game Gear and Genesis (68000 and Z80). The engine is Debugger.WriteHooks.cs.
internal sealed partial class HeadlessSession
{
    private long hooksReported;

    private static JsonObject HookJson(WriteHook h) => new()
    {
        ["id"] = h.Id, ["cpu"] = h.CpuId, ["action"] = WriteHook.ActionText(h.Action), ["spec"] = h.Spec, ["resolved"] = h.Resolved, ["condition"] = h.Condition,
        ["and"] = h.Action == WriteHookAction.Rewrite && h.Value == null ? Num.Hex(h.And) : null,
        ["or"] = h.Action == WriteHookAction.Rewrite && h.Value == null ? Num.Hex(h.Or) : null,
        ["xor"] = h.Action == WriteHookAction.Rewrite && h.Value == null ? Num.Hex(h.Xor) : null,
        ["value"] = h.Value is int v ? Num.Hex((byte)v) : null,
        ["hits"] = h.Hits, ["changed"] = h.Changed, ["logged"] = h.Logged, ["enabled"] = h.Enabled, ["text"] = h.ToString(),
    };

    private static JsonObject HookRecordJson(DebugMachine m, WriteHookRecord r)
    {
        var c = m.Cpu(r.CpuId) ?? m.Main;
        return new JsonObject
        {
            ["seq"] = r.Seq, ["hook"] = r.HookId, ["action"] = WriteHook.ActionText(r.Action), ["cpu"] = r.CpuId, ["frame"] = r.Frame, ["scanline"] = r.Scanline, ["cycle"] = r.Cycle,
            ["pc"] = c.FormatAddress(r.Pc), ["pcSymbol"] = r.PcSymbol, ["address"] = c.FormatAddress(r.Address), ["old"] = Num.Hex(r.Old), ["written"] = Num.Hex(r.Written),
            ["landed"] = r.Landed < 0 ? null : Num.Hex((byte)r.Landed), ["dropped"] = r.Landed < 0, ["stack"] = r.Stack.Length > 0 ? r.Stack : null, ["text"] = r.Text,
        };
    }

    public static WriteHookAction ParseHookAction(string a) => a.Trim().ToLowerInvariant() switch
    {
        "log" => WriteHookAction.Log, "break" => WriteHookAction.Break, "rewrite" or "mask" => WriteHookAction.Rewrite, "block" or "drop" => WriteHookAction.Block,
        "freeze" or "hold" => WriteHookAction.Freeze,
        _ => throw new ToolError($"action '{a}' is not log, break, rewrite, block or freeze"),
    };

    private static byte? HookByte(long? v, string what) => v switch
    {
        null => null,
        >= 0 and <= 0xFF => (byte)v.Value,
        _ => throw new ToolError($"{what} ${v:X} is not a byte"),
    };

    public override JsonObject WriteHookAdd(string spec, string action, string? condition, long? and, long? or, long? xor, long? value, bool quiet, string cpu)
    {
        var d = Dbg; CpuOf(cpu);
        var act = ParseHookAction(action);
        try
        {
            var h = d.AddWriteHook(spec, act, condition, HookByte(and, "and") ?? 0xFF, HookByte(or, "or") ?? 0, HookByte(xor, "xor") ?? 0,
                HookByte(value, "value") is byte b ? b : null, string.IsNullOrWhiteSpace(cpu) ? "main" : cpu, quiet);
            var j = new JsonObject { ["hook"] = HookJson(h) };
            j["note"] = act switch
            {
                WriteHookAction.Break => "the machine stops at a matching write with the write NOT yet applied (registers and memory as before it); run / continue / step lets it land",
                WriteHookAction.Freeze => "the value was put in place now; every write there lands it instead (a state load or reset puts it back too)",
                WriteHookAction.Log => "matching writes go to the hook log (write_hook_list); run results count the new ones",
                _ => "hooks are session state: never saved in a save state, and a movie saved from this session does not carry them",
            };
            return j;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException) { throw new ToolError(e.Message); }
    }

    public override JsonObject WriteHookList(long since, int limit)
    {
        var d = Dbg;
        var log = d.HookLog(since);
        int n = Math.Clamp(limit, 0, 4000);
        return new JsonObject
        {
            ["hooks"] = J.Arr(d.WriteHooks.Select(h => (JsonNode)HookJson(h))),
            ["records"] = d.WriteHookRecords,
            ["log"] = J.Arr(log.Skip(Math.Max(0, log.Count - n)).Select(r => (JsonNode)HookRecordJson(M!, r))),
            ["note"] = log.Count > n ? $"the last {n} of {log.Count} records after #{since} (the log keeps the newest 8192)" : null,
        };
    }

    public override JsonObject WriteHookRemove(int? id, bool all, bool clearLog)
    {
        var d = Dbg;
        var j = new JsonObject();
        if (all) j["removed"] = d.ClearWriteHooks();
        else if (id is int i) { if (!d.RemoveWriteHook(i)) throw new ToolError($"no write hook #{i}"); j["removed"] = 1; j["id"] = i; }
        else if (!clearLog) throw new ToolError("id, all:true or clear_log:true is needed");
        if (clearLog) { d.ClearHookLog(); j["logCleared"] = true; }
        j["hooks"] = d.WriteHooks.Count;
        return j;
    }

    /// <summary>The hook log's news since the last result (counts, and the last few lines).</summary>
    private void HookNews(JsonObject j)
    {
        var d = D;
        if (d == null || d.WriteHookRecords == hooksReported) return;
        var news = d.HookLog(hooksReported);
        long total = d.WriteHookRecords - hooksReported;
        hooksReported = d.WriteHookRecords;
        j["writeHooks"] = new JsonObject
        {
            ["new"] = total,
            ["last"] = J.Strs(news.TakeLast(8).Select(r => r.Text)),
            ["note"] = "write_hook_list has the records",
        };
    }

    /// <summary>Whether any hook changes what the machine does (a movie made from this session would not replay the same without it).</summary>
    private bool HooksAlter => D?.WriteHooks.Any(h => h.Enabled && h.Action is WriteHookAction.Rewrite or WriteHookAction.Block or WriteHookAction.Freeze) == true;
}
