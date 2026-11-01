using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Port;

namespace BrokenNes.Workshop;

/// <summary>
/// BrokenDev <c>verify</c> and the debug port (milestone D7 of docs/projects/brokendev-debugging-plan.md): every scenario runs with the port
/// listening (it never changes the game: the checkpoints are the same with it on). The report carries each scenario's prints, markers,
/// timers and errors; <b>a failed assert fails the scenario</b> with its message and place. A scenario's <c>expect.port</c> can ask for more:
/// <code>
///   "port": { "markers": ["started"], "prints": ["boot 1"], "timers": ["draw_hud"], "maxErrors": 0, "asserts": 0 }
/// </code>
/// (<c>markers</c>, <c>prints</c>: each text must appear, a print by its start; <c>timers</c>: each must have run; <c>asserts</c>: how many
/// asserts the scenario expects, for a negative control that plants one).
/// </summary>
internal static partial class BrokenDevCli
{
    /// <summary>The symbol files of the project being verified (set by Verify), so the port names the places of its events.</summary>
    private static List<string> portSymbolFiles = new();

    private sealed partial class ScenarioResult
    {
        public JsonObject? PortJson;
        public List<PortEvent> PortAsserts = new();
        public List<PortEvent> PortPrints = new(), PortMarkers = new();
        public List<PortTimer> PortTimers = new();
        public long PortErrors;
        public string PortNote = "";
    }

    /// <summary>The port on a scenario's machine, or null (said in the report) where the debugger does not reach (the Sega consoles, a
    /// Game Boy session on a foreign CPU).</summary>
    private static DebugPort? ScenarioPort(IRunner runner, ScenarioResult res)
    {
        try
        {
            var dbg = new Debugger(runner.DebugMachine());
            string family = NesEmulator.Debugging.Checks.CheckCatalogue.FamilyOf(dbg.Machine.Console);
            foreach (var s in portSymbolFiles) dbg.Symbols.Load(s, family);
            var port = new DebugPort(dbg.Machine, dbg) { Keep = 2000 };
            port.Attach();
            return port;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            res.PortNote = "no debug port on this machine: " + e.Message;
            return null;
        }
    }

    private static void FinishPort(ScenarioResult res, DebugPort? port)
    {
        if (port == null) { res.PortJson = new JsonObject { ["note"] = res.PortNote }; return; }
        port.Detach();
        res.PortJson = port.Json(200);
        res.PortAsserts = port.Asserts.ToList();
        res.PortMarkers = port.Markers.ToList();
        res.PortPrints = port.Events.Where(e => e.Kind == PortEventKind.Print).ToList();
        res.PortTimers = port.Timers.ToList();
        res.PortErrors = port.Count(PortEventKind.Error);
    }

    private static void EvaluatePort(ScenarioResult r, JsonElement? expect)
    {
        void Add(string id, bool ok, string detail) => r.Checks.Add(new CheckResult { Id = id, Ok = ok, Detail = detail });
        int wantAsserts = 0;
        JsonElement pe = default;
        bool hasExpect = expect is { } e && e.TryGetProperty("port", out pe) && pe.ValueKind == JsonValueKind.Object;
        if (hasExpect && pe.TryGetProperty("asserts", out var wa)) wantAsserts = wa.GetInt32();
        if (r.PortJson == null) return;   // the scenario did not run
        if (r.PortAsserts.Count > 0 || wantAsserts > 0)
            Add("debug-port-asserts", r.PortAsserts.Count == wantAsserts,
                r.PortAsserts.Count == 0 ? $"no assert failed (the scenario expects {wantAsserts})"
                : string.Join("; ", r.PortAsserts.Take(5).Select(a => $"assert failed at {a.Place}, frame {a.Frame}: {a.Text}")) + (r.PortAsserts.Count > 5 ? $" (+{r.PortAsserts.Count - 5} more)" : "")
                  + (wantAsserts > 0 ? $" (the scenario expects {wantAsserts})" : ""));
        if (!hasExpect) return;
        if (r.PortNote.Length > 0) { Add("debug-port", false, r.PortNote); return; }
        if (pe.TryGetProperty("markers", out var mk))
            foreach (var m in mk.EnumerateArray())
            {
                string want = m.GetString() ?? "";
                var hit = r.PortMarkers.FirstOrDefault(x => x.Text == want);
                Add($"debug-port-marker[{want}]", hit != null, hit != null ? $"at frame {hit.Frame} ({hit.Place})" : $"never marked (markers: {string.Join(", ", r.PortMarkers.Select(x => x.Text).Distinct().Take(10))})");
            }
        if (pe.TryGetProperty("prints", out var pr))
            foreach (var p in pr.EnumerateArray())
            {
                string want = p.GetString() ?? "";
                var hit = r.PortPrints.FirstOrDefault(x => x.Text.StartsWith(want, StringComparison.Ordinal));
                Add($"debug-port-print[{want}]", hit != null, hit != null ? $"\"{hit.Text}\" at frame {hit.Frame}" : $"never printed ({r.PortPrints.Count} prints)");
            }
        if (pe.TryGetProperty("timers", out var tm))
            foreach (var t in tm.EnumerateArray())
            {
                string want = t.GetString() ?? "";
                var hit = r.PortTimers.FirstOrDefault(x => x.Name == want);
                Add($"debug-port-timer[{want}]", hit is { Calls: > 0 }, hit is { Calls: > 0 } ? $"{hit.Calls} calls, worst {hit.Worst}" : "never ran");
            }
        if (pe.TryGetProperty("maxErrors", out var me))
            Add("debug-port-errors", r.PortErrors <= me.GetInt64(), $"{r.PortErrors} error event(s), at most {me.GetInt64()}");
    }

    /// <summary>The port's lines in verify's summary (stderr): the asserts, then a few prints and markers.</summary>
    private static void PrintPort(List<ScenarioResult> results)
    {
        foreach (var r in results)
        {
            if (r.PortJson == null || r.PortNote.Length > 0) continue;
            int n = r.PortPrints.Count + r.PortMarkers.Count + r.PortAsserts.Count;
            if (n == 0 && r.PortTimers.Count == 0) continue;
            Console.Error.WriteLine($"  [port] {r.Id}: {r.PortPrints.Count} prints, {r.PortMarkers.Count} markers, {r.PortAsserts.Count} asserts, {r.PortErrors} errors, {r.PortTimers.Count} timers");
            foreach (var a in r.PortAsserts.Take(5)) Console.Error.WriteLine($"  [port] {r.Id} ASSERT frame {a.Frame} at {a.Place}: {a.Text}");
            foreach (var e in r.PortPrints.Concat(r.PortMarkers).OrderBy(e => e.Seq).Take(8))
                Console.Error.WriteLine($"  [port] {r.Id} frame {e.Frame} {e.KindName}: {e.Text}");
            foreach (var t in r.PortTimers.Take(5)) Console.Error.WriteLine($"  [port] {r.Id} timer {t.Name}: {t.Calls} calls, average {t.Average:F0}, worst {t.Worst}");
        }
    }
}
