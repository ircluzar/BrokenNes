using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NesEmulator.Debugging.Checks;

namespace BrokenNes.Workshop;

/// <summary>
/// BrokenDev <c>verify</c>'s coverage map (milestone D9 of docs/projects/brokendev-debugging-plan.md, "D9 settled"): with <c>--coverage</c>
/// or a manifest <c>coverage</c> section, every checked scenario (the promoted movies included) runs with <see cref="CodeCoverage"/> on the
/// checks' debugger; the ROM bytes run are merged over the scenarios and reported per bank, with the ROM symbols never run. The check:
/// <code>
///   "coverage": { "mustRun": ["update_player", "rom:$1234"], "minPercent": 40 }
/// </code>
/// a <c>mustRun</c> entry that never ran, an unknown one, or a share under <c>minPercent</c> fails verify, naming it.
/// </summary>
internal static partial class BrokenDevCli
{
    /// <summary>--coverage on the verify command line.</summary>
    private static bool coverageWanted;

    private static bool WantsCoverage(JsonElement root) => coverageWanted || (root.TryGetProperty("coverage", out var c) && c.ValueKind == JsonValueKind.Object);

    /// <summary>The checked runs for coverage alone (a manifest with no checks or budgets): every non-information scenario, findings ignored.</summary>
    private static List<CheckedScenario> RunForCoverage(JsonElement root, JsonElement verify, string console, string romPath, byte[] romBytes, List<ScenarioResult> results, string only)
    {
        var runs = new List<CheckedScenario>();
        var symbolFiles = PublishedSymbolFiles(root, romPath);
        foreach (var sc in verify.GetProperty("scenarios").EnumerateArray().Concat(promotedScenarios))
        {
            if (sc.TryGetProperty("info", out var inf) && inf.ValueKind == JsonValueKind.True) continue;
            string sid = sc.GetProperty("id").GetString() ?? "";
            if (only.Length > 0 && !sid.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
            runs.Add(RunChecked(console, romPath, romBytes, verify, sc, new CheckOptions(), symbolFiles, 0, results.FirstOrDefault(r => r.Id == sid), coverage: true));
        }
        return runs;
    }

    private static JsonObject BuildCoverage(JsonElement root, string console, string romPath, byte[] romBytes, List<CheckedScenario> runs, out bool ok)
    {
        var problems = new List<string>();
        var withMap = runs.Where(r => r.Ran != null).ToList();
        int size = withMap.Count > 0 ? withMap.Max(r => r.Ran!.Length) : 0;
        var ran = new bool[size];
        foreach (var r in withMap) for (int i = 0; i < r.Ran!.Length; i++) ran[i] |= r.Ran[i];
        var syms = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var r in withMap) foreach (var kv in r.RomSymbols) syms.TryAdd(kv.Key, kv.Value);
        foreach (var r in runs.Where(r => r.Result != "ok")) problems.Add($"the checked run of '{r.Id}' {r.Result}: {r.Error} (its coverage is partial)");

        // per bank, with the padding the budgets count as free
        var banks = RomBanks.Free(RomFor(romBytes, romPath), Family(console));
        int bankSize = banks.Count > 0 ? banks[0].Size : Math.Max(1, size);
        var bankJson = new JsonArray();
        long allRan = 0, allCode = 0;
        int nb = banks.Count > 0 ? banks.Count : (size + bankSize - 1) / bankSize;
        for (int b = 0; b < nb; b++)
        {
            int start = b * bankSize, len = banks.Count > b ? banks[b].Size : Math.Min(bankSize, size - start), free = banks.Count > b ? banks[b].Free : 0;
            int n = 0;
            for (int i = start; i < start + len && i < size; i++) if (ran[i]) n++;
            int code = Math.Max(0, len - free);
            allRan += n; allCode += code;
            bankJson.Add(new JsonObject { ["bank"] = b, ["size"] = len, ["ran"] = n, ["free"] = free, ["percent"] = Pct(n, code) });
        }
        double percent = Pct(allRan, allCode);

        // the ROM symbols never run (code labels, and data labels kept in ROM: a data label is never "run")
        var never = syms.Where(kv => !ran[kv.Value]).OrderBy(kv => kv.Value).ToList();

        // the check
        var must = new JsonArray();
        double? minPercent = null;
        if (root.TryGetProperty("coverage", out var cs) && cs.ValueKind == JsonValueKind.Object)
        {
            if (cs.TryGetProperty("mustRun", out var mr))
                foreach (var e in mr.EnumerateArray())
                {
                    string spec = e.GetString() ?? "";
                    uint off; string where;
                    var m = System.Text.RegularExpressions.Regex.Match(spec, @"^(?:rom|prg):\$?([0-9A-Fa-f]+)$");
                    if (m.Success) { off = uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture); where = spec; }
                    else if (syms.TryGetValue(spec, out off) || syms.TryGetValue("_" + spec, out off)) where = $"rom:${off:X}";
                    else { problems.Add($"coverage.mustRun: '{spec}' is no ROM symbol of this build (nor rom:$offset)"); must.Add(new JsonObject { ["spec"] = spec, ["ran"] = false, ["detail"] = "unknown" }); continue; }
                    bool did = off < (uint)size && ran[off];
                    if (!did) problems.Add($"coverage.mustRun: '{spec}' ({where}) never ran in the checked scenarios");
                    must.Add(new JsonObject { ["spec"] = spec, ["at"] = where, ["ran"] = did });
                }
            if (cs.TryGetProperty("minPercent", out var mp) && mp.ValueKind == JsonValueKind.Number)
            {
                minPercent = mp.GetDouble();
                if (percent < minPercent) problems.Add($"coverage.minPercent: {percent:0.##}% of the code bytes ran, under the floor of {minPercent:0.##}%");
            }
        }
        if (size == 0) problems.Add("no ROM space on the main CPU: nothing to map");
        ok = problems.Count == 0;
        return new JsonObject
        {
            ["ok"] = ok, ["percent"] = percent, ["ranBytes"] = allRan, ["codeBytes"] = allCode, ["romBytes"] = size, ["bankSize"] = bankSize,
            ["scenarios"] = new JsonArray(runs.Select(r => (JsonNode)r.Id).ToArray()),
            ["banks"] = bankJson,
            ["symbols"] = new JsonObject
            {
                ["inRom"] = syms.Count, ["neverRun"] = never.Count,
                ["neverRunList"] = new JsonArray(never.Take(300).Select(kv => (JsonNode)new JsonObject { ["name"] = kv.Key, ["at"] = $"rom:${kv.Value:X}" }).ToArray()),
            },
            ["mustRun"] = must, ["minPercent"] = minPercent,
            ["problems"] = new JsonArray(problems.Select(p => (JsonNode)p).ToArray()),
        };
        static double Pct(long n, long d) => d <= 0 ? 0 : Math.Round(Math.Min(100.0, 100.0 * n / d), 1);
    }

    private static void PrintCoverage(JsonObject c)
    {
        Console.Error.WriteLine($"  [coverage] {((bool)c["ok"]! ? "ok" : "FAIL")}: {(double)c["percent"]!:0.#}% of the code bytes ran ({(long)c["ranBytes"]!} of {(long)c["codeBytes"]!}), {(int)c["symbols"]!["neverRun"]!} of {(int)c["symbols"]!["inRom"]!} ROM symbols never ran");
        int empty = 0;
        foreach (var b in c["banks"]!.AsArray())
        {
            if ((int)b!["ran"]! == 0 && (int)b["free"]! == (int)b["size"]!) { empty++; continue; }   // an all-padding bank: counted, not listed
            Console.Error.WriteLine($"  [coverage]   bank {(int)b["bank"]!,3}: {(double)b["percent"]!,5:0.#}% ({(int)b["ran"]!} bytes ran, {(int)b["free"]!} free of {(int)b["size"]!})");
        }
        if (empty > 0) Console.Error.WriteLine($"  [coverage]   {empty} bank(s) all padding (nothing to run)");
        foreach (var p in c["problems"]!.AsArray()) Console.Error.WriteLine($"  [coverage] FAIL {(string?)p}");
    }
}
