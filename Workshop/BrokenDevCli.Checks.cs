using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NesEmulator;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Checks;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// BrokenDev <c>verify</c>'s hardware checks, budgets and profile (milestone D4 of docs/projects/brokendev-debugging-plan.md). With a
/// <c>checks</c> section in the manifest, every checked scenario runs a second time with the checks on (the first, unchecked run keeps its
/// hashes and speed): findings are matched against the game's accepted list and anything new FAILS verify; the checked run must end on the
/// same picture as the unchecked one. <c>budgets</c> are measured from those runs and the ROM: over a limit fails, at 90% of it warns.
/// The report shows each budget against the previous build (a history file keyed by the ROM's sha256).
/// <code>
///   "checks":  { "scenarios": ["play"], "frameWait": ["symbol"], "profileFrom": 300,
///                "off":      [ { "rule": "nes.open-bus", "reason": "..." } ],
///                "accepted": [ { "rule": "nes.lag-frame", "at": "load_room*", "reason": "known: ..." } ] }
///   "budgets": { "lagFrames": { "max": 0, "fromFrame": 400, "scenarios": ["play"] }, "busyPercent": { "max": 95, "fromFrame": 400 },
///                "romBankFree": { "min": 2048, "banks": { "31": 0 } }, "memoryFree": { "wram": 1024 }, "stackFree": { "min": 32 } }
/// </code>
/// </summary>
internal static partial class BrokenDevCli
{
    private sealed class CheckedScenario
    {
        public string Id = "";
        public List<Finding> Findings = new();
        public ProfileSummary Profile = new();
        public List<ConsoleChecks.FrameStat> Frames = new();
        public double FrameCycles;
        public Dictionary<string, (long size, long free)> Memory = new();
        public long StackFree = -1, StackDeepest = -1;
        public bool Identical = true; public string IdentityDetail = "";
        public string Result = "ok", Error = "";
        public double Fps;
    }

    private sealed class BudgetResult
    {
        public string Id = "", Kind = "max", Detail = "";
        public double Value, Limit; public string Status = "ok";
        public double? Previous;
        public JsonNode ToJson() => new JsonObject
        {
            ["id"] = Id, ["kind"] = Kind, ["value"] = Math.Round(Value, 2), ["limit"] = Limit, ["status"] = Status,
            ["previous"] = Previous is double p ? Math.Round(p, 2) : null, ["delta"] = Previous is double q ? Math.Round(Value - q, 2) : null, ["detail"] = Detail,
        };
    }

    /// <summary>Runs the checks and budgets of a manifest; null when it has neither (verify as before).</summary>
    private static JsonObject? RunChecksAndBudgets(JsonElement root, JsonElement verify, string console, string romPath, byte[] romBytes, List<ScenarioResult> results, string only, string historyPath, out bool ok)
    {
        ok = true;
        bool hasChecks = root.TryGetProperty("checks", out var chk) && chk.ValueKind == JsonValueKind.Object;
        bool hasBudgets = root.TryGetProperty("budgets", out var bud) && bud.ValueKind == JsonValueKind.Object;
        if (!hasChecks && !hasBudgets) return null;

        // ---- options
        var opt = new CheckOptions();
        var accepted = new List<AcceptedFinding>();
        var problems = new List<string>();
        var scenarioIds = new List<string>();
        long profileFrom = 0;
        if (hasChecks)
        {
            if (chk.TryGetProperty("frameWait", out var fw)) foreach (var w in fw.EnumerateArray()) opt.FrameWait.Add(w.GetString() ?? "");
            if (chk.TryGetProperty("profileFrom", out var pf)) profileFrom = pf.GetInt64();
            if (chk.TryGetProperty("off", out var off))
                foreach (var o in off.EnumerateArray())
                {
                    string rule = o.GetProperty("rule").GetString() ?? "";
                    if (CheckCatalogue.Find(rule) == null) problems.Add($"checks.off: unknown rule '{rule}'");
                    if (!o.TryGetProperty("reason", out var r) || string.IsNullOrWhiteSpace(r.GetString())) problems.Add($"checks.off: '{rule}' has no reason");
                    opt.Off.Add(rule);
                }
            if (chk.TryGetProperty("accepted", out var acc))
                foreach (var a in acc.EnumerateArray())
                {
                    var e = new AcceptedFinding { Rule = a.GetProperty("rule").GetString() ?? "", At = a.TryGetProperty("at", out var at) ? at.GetString() ?? "*" : "*", Reason = a.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "" };
                    if (!CheckCatalogue.All.Any(r => AcceptedFinding.Glob(e.Rule).IsMatch(r.Id))) problems.Add($"checks.accepted: rule '{e.Rule}' matches no rule of the catalogue");
                    if (string.IsNullOrWhiteSpace(e.Reason)) problems.Add($"checks.accepted: '{e.Rule}' at '{e.At}' has no reason");
                    accepted.Add(e);
                }
            if (chk.TryGetProperty("scenarios", out var ss)) foreach (var s in ss.EnumerateArray()) scenarioIds.Add(s.GetString() ?? "");
        }
        var allScenarios = verify.GetProperty("scenarios").EnumerateArray().Concat(promotedScenarios).ToList();
        if (scenarioIds.Count == 0)
            scenarioIds = allScenarios.Where(s => !(s.TryGetProperty("info", out var inf) && inf.ValueKind == JsonValueKind.True)).Select(s => s.GetProperty("id").GetString() ?? "").ToList();
        if (only.Length > 0) scenarioIds = scenarioIds.Where(s => s.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var s in scenarioIds) if (!allScenarios.Any(x => x.GetProperty("id").GetString() == s)) problems.Add($"checks.scenarios: no verify scenario '{s}'");

        // ---- the symbols the build published next to the ROM
        var symbolFiles = new List<string>();
        foreach (var o in root.GetProperty("build").GetProperty("outputs").EnumerateArray())
            if (o.TryGetProperty("kind", out var k) && k.GetString() == "symbols")
            {
                string p = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(romPath))!, o.GetProperty("publishAs").GetString() ?? "");
                if (File.Exists(p)) symbolFiles.Add(p);
            }

        // ---- the checked runs
        var runs = new List<CheckedScenario>();
        if (hasChecks || hasBudgets)
            foreach (var sid in scenarioIds)
            {
                var sc = allScenarios.FirstOrDefault(x => x.GetProperty("id").GetString() == sid);
                if (sc.ValueKind != JsonValueKind.Object) continue;
                var plain = results.FirstOrDefault(r => r.Id == sid);
                runs.Add(RunChecked(console, romPath, romBytes, verify, sc, opt, symbolFiles, profileFrom, plain));
            }

        // ---- findings: merged over the scenarios, matched against the accepted list
        var merged = new Dictionary<string, Finding>(StringComparer.Ordinal);
        foreach (var r in runs)
            foreach (var f in r.Findings)
            {
                if (merged.TryGetValue(f.Key, out var m)) { m.Count += f.Count; continue; }
                f.Scenario = r.Id;
                merged[f.Key] = f;
            }
        foreach (var f in merged.Values)
            foreach (var a in accepted)
                if (a.Matches(f)) { f.AcceptedReason = a.Reason; a.Matched++; break; }
        var findings = merged.Values.OrderBy(f => f.AcceptedReason != null).ThenBy(f => f.Rule).ThenBy(f => f.FirstFrame).ToList();
        int fresh = findings.Count(f => f.AcceptedReason == null);

        // ---- budgets
        var budgets = new List<BudgetResult>();
        if (hasBudgets) budgets = EvaluateBudgets(bud, runs, RomFor(romBytes, romPath), console, problems);
        var history = ReadHistory(historyPath, out var previous, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(romBytes)).ToLowerInvariant());
        foreach (var b in budgets) if (previous != null && previous.TryGetPropertyValue(b.Id, out var pv) && pv != null) b.Previous = pv.GetValue<double>();

        bool runsOk = runs.All(r => r.Result == "ok" && r.Identical);
        ok = fresh == 0 && runsOk && problems.Count == 0 && budgets.All(b => b.Status != "fail");

        var report = new JsonObject
        {
            ["ok"] = ok,
            ["newFindings"] = fresh, ["acceptedFindings"] = findings.Count - fresh,
            ["problems"] = new JsonArray(problems.Select(p => (JsonNode)p).ToArray()),
            ["scenarios"] = new JsonArray(runs.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id, ["result"] = r.Result, ["error"] = r.Error, ["fpsChecked"] = Math.Round(r.Fps, 1),
                ["frameIdentical"] = r.Identical, ["identity"] = r.IdentityDetail, ["findings"] = r.Findings.Count,
                ["profile"] = CheckRun.ProfileJson(r.Profile),
                ["memory"] = new JsonArray(r.Memory.Select(m => (JsonNode)new JsonObject { ["name"] = m.Key, ["size"] = m.Value.size, ["free"] = m.Value.free }).ToArray()),
                ["stack"] = new JsonObject { ["deepest"] = r.StackDeepest, ["free"] = r.StackFree },
            }).ToArray()),
            ["findings"] = new JsonArray(findings.Select(CheckRun.FindingJson).ToArray()),
            ["staleAccepted"] = new JsonArray(accepted.Where(a => a.Matched == 0).Select(a => (JsonNode)new JsonObject { ["rule"] = a.Rule, ["at"] = a.At, ["reason"] = a.Reason }).ToArray()),
            ["budgets"] = new JsonArray(budgets.Select(b => b.ToJson()).ToArray()),
            ["romBanks"] = new JsonArray(RomBanks.Free(RomFor(romBytes, romPath), Family(console)).Select(b => (JsonNode)new JsonObject { ["bank"] = b.Index, ["size"] = b.Size, ["free"] = b.Free }).ToArray()),
            ["previousBuild"] = previous?.DeepClone(),
        };
        if (historyPath.Length > 0 && only.Length == 0) WriteHistory(historyPath, history, romBytes, budgets);
        return report;
    }

    /// <summary>The checks' part of verify's summary (stderr, as the rest of it).</summary>
    private static void PrintChecks(JsonObject c)
    {
        var findings = c["findings"]!.AsArray();
        Console.Error.WriteLine($"  [checks] {(bool)c["ok"]! switch { true => "ok", false => "FAIL" }}: {(int)c["newFindings"]!} new finding(s), {(int)c["acceptedFindings"]!} accepted");
        foreach (var p in c["problems"]!.AsArray()) Console.Error.WriteLine($"  [checks] FAIL manifest: {(string?)p}");
        foreach (var s in c["scenarios"]!.AsArray())
        {
            var pr = s!["profile"]!;
            Console.Error.WriteLine($"  [checks] {(string?)s["id"]}: {(string?)s["result"]}, {((bool)s["frameIdentical"]! ? "frame-identical" : "NOT frame-identical: " + (string?)s["identity"])}, {(double)s["fpsChecked"]!:F0} fps checked; " +
                $"busy {(double)pr["busyPercentAverage"]!:F1}% (worst {(double)pr["busyPercentWorst"]!:F1}% at frame {(long)pr["busyWorstFrame"]!}), {(int)pr["lagFrames"]!} lag frames, vblank uploads worst {(double)pr["vblankUploadPercentWorst"]!:F1}%");
        }
        foreach (var f in findings.Where(f => !(bool)f!["accepted"]!))
            Console.Error.WriteLine($"  [checks] NEW {(string?)f!["rule"]} at {(string?)f["place"]} ({(string?)f["scenario"]} frame {(long)f["firstFrame"]!}, pc {(string?)f["pc"]} {(string?)f["pcSymbol"]}, {(long)f["count"]!}x): {(string?)f["detail"]}");
        foreach (var b in c["budgets"]!.AsArray())
        {
            string trend = b!["previous"] is JsonNode prev ? $" (previous build {(double)prev:0.##}, {((double)b["delta"]! >= 0 ? "+" : "")}{(double)b["delta"]!:0.##})" : "";
            Console.Error.WriteLine($"  [budget] {((string?)b["status"])!.ToUpperInvariant(),-4} {(string?)b["id"]}: {(double)b["value"]!:0.##} ({((string?)b["kind"] == "max" ? "at most" : "at least")} {(double)b["limit"]!:0.##}){trend} - {(string?)b["detail"]}");
        }
        foreach (var a in c["staleAccepted"]!.AsArray())
            Console.Error.WriteLine($"  [checks] note: the accepted '{(string?)a!["rule"]}' at '{(string?)a["at"]}' matched nothing in this run ({(string?)a["reason"]})");
    }

    private static string Family(string console) => console switch { "nes" => "nes", "gb" or "gbc" => "gb", _ => "snes" };

    private static byte[] RomFor(byte[] rom, string path) => RomDetect.Unwrap(rom, Path.GetFileName(path), out _);

    /// <summary>One scenario again, with the checks on: the same cores, power-on RAM, frame timing, pokes and pad script.</summary>
    private static CheckedScenario RunChecked(string console, string romPath, byte[] rom, JsonElement verify, JsonElement sc, CheckOptions opt, List<string> symbolFiles, long profileFrom, ScenarioResult? plain)
    {
        var res = new CheckedScenario { Id = sc.GetProperty("id").GetString() ?? "?" };
        int frames = sc.GetProperty("frames").GetInt32();
        bool ntsc = sc.TryGetProperty("ntscFrameTiming", out var nt) && nt.ValueKind == JsonValueKind.True;
        string powerOnRam = sc.TryGetProperty("powerOnRam", out var por) ? por.GetString() ?? "fceux" : "fceux";
        var steps = ParseInput(sc.TryGetProperty("input", out var ip) ? ip.GetString() ?? "" : "");
        var steps2 = ParseInput(sc.TryGetProperty("input2", out var ip2) ? ip2.GetString() ?? "" : "");
        var pokes = new List<(int Frame, int Addr, byte Value)>();
        if (sc.TryGetProperty("pokes", out var pk))
            foreach (var p in pk.EnumerateArray())
            {
                string a = p.GetProperty("addr").ValueKind == JsonValueKind.String ? p.GetProperty("addr").GetString() ?? "0" : p.GetProperty("addr").GetInt32().ToString(CultureInfo.InvariantCulture);
                pokes.Add((p.GetProperty("frame").GetInt32(), a.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(a[2..], 16) : int.Parse(a, CultureInfo.InvariantCulture), (byte)p.GetProperty("value").GetInt32()));
            }
        try
        {
            // the scenario's own console (a gb scenario of a gbc project) and the battery save it powered on with, as the unchecked run
            using IRunner runner = MakeRunner(ScenarioConsole(console, sc), romPath, rom, verify, ntsc, powerOnRam);
            if (plain?.BatteryIn is { } save) runner.ImportBattery(save);
            var dbg = new Debugger(runner.DebugMachine());
            foreach (var s in symbolFiles) dbg.Symbols.Load(s, CheckCatalogue.FamilyOf(dbg.Machine.Console));
            var checks = CheckRun.Attach(dbg, opt);
            var held = new List<string>(); var held2 = new List<string>(); int next = 0, next2 = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
            {
                foreach (var p in pokes) if (p.Frame == f && runner is NesRunner nr) nr.Poke(p.Addr, p.Value);
                while (next < steps.Count && steps[next].Frame <= f) { held = steps[next].Names; next++; }
                while (next2 < steps2.Count && steps2[next2].Frame <= f) { held2 = steps2[next2].Names; next2++; }
                runner.Step(held, held2);
                if (runner.Crashed) { res.Result = "crashed"; res.Error = runner.CrashInfo; break; }
                runner.DrainAudio();
            }
            sw.Stop();
            res.Fps = frames / Math.Max(1e-9, sw.Elapsed.TotalSeconds);
            checks.Finish();
            dbg.Detach();
            res.Findings = checks.Findings.ToList();
            res.Profile = CheckRun.Summarize(checks, profileFrom);
            res.Frames = checks.Frames.ToList();
            res.FrameCycles = checks.NominalFrameCycles;
            foreach (var (name, size, written) in checks.MemoryUse()) res.Memory[name] = (size, size - written);
            (res.StackDeepest, res.StackFree) = checks.StackUse();
            var (hash, _) = runner.Picture();
            var last = plain?.Checkpoints.FirstOrDefault(c => c.Frame == frames);
            if (last != null)
            {
                res.Identical = last.Hash == hash.ToString("X16");
                res.IdentityDetail = res.Identical ? $"the checked run ends on the unchecked run's picture ({last.Hash})" : $"the checked run ended on {hash:X16}, the unchecked run on {last.Hash}: the checks disturbed the game";
            }
            else res.IdentityDetail = "no unchecked run to compare with";
        }
        catch (Exception ex) { res.Result = "crashed"; res.Error = ex.GetType().Name + ": " + ex.Message; }
        return res;
    }

    // ---------------------------------------------------------------------------------------------------- budgets

    private static List<BudgetResult> EvaluateBudgets(JsonElement bud, List<CheckedScenario> runs, byte[] romBytes, string console, List<string> problems)
    {
        var list = new List<BudgetResult>();
        IEnumerable<CheckedScenario> Pick(JsonElement b)
        {
            if (!b.TryGetProperty("scenarios", out var s)) return runs;
            var ids = s.EnumerateArray().Select(x => x.GetString()).ToHashSet();
            return runs.Where(r => ids.Contains(r.Id));
        }
        long From(JsonElement b) => b.TryGetProperty("fromFrame", out var f) ? f.GetInt64() : 0;
        foreach (var p in bud.EnumerateObject())
        {
            var b = p.Value;
            switch (p.Name)
            {
                case "lagFrames":
                {
                    long from = From(b);
                    var picked = Pick(b).ToList();
                    int lag = picked.Sum(r => r.Frames.Count(f => f.Frame >= from && f.Lag));
                    var where = picked.SelectMany(r => r.Frames.Where(f => f.Frame >= from && f.Lag).Take(8).Select(f => $"{r.Id}@{f.Frame}")).Take(12);
                    list.Add(Max("lagFrames", lag, b.GetProperty("max").GetDouble(), $"lag frames from frame {from} in {string.Join(", ", picked.Select(r => r.Id))}{(lag > 0 ? ": " + string.Join(" ", where) : "")}"));
                    break;
                }
                case "busyPercent":
                {
                    long from = From(b);
                    double worst = 0; string at = "";
                    foreach (var r in Pick(b))
                        foreach (var f in r.Frames.Where(f => f.Frame >= from && !f.Lag && f.Cycles > r.FrameCycles * 0.5))
                        {
                            double pc = 100.0 * f.Busy / r.FrameCycles;
                            if (pc > worst) { worst = pc; at = $"{r.Id}@{f.Frame}"; }
                        }
                    list.Add(Max("busyPercent", worst, b.GetProperty("max").GetDouble(), $"the busiest frame that made it from frame {from} ({at}) keeps the CPU busy {worst:F1}% of the frame (lag frames are the lagFrames budget's)"));
                    break;
                }
                case "romBankFree":
                {
                    bool all = b.TryGetProperty("min", out var m);
                    double min = all ? m.GetDouble() : 0;
                    var over = new Dictionary<int, double>();
                    if (b.TryGetProperty("banks", out var bk)) foreach (var x in bk.EnumerateObject()) over[int.Parse(x.Name, CultureInfo.InvariantCulture)] = x.Value.GetDouble();
                    var banks = RomBanks.Free(romBytes, Family(console));
                    BudgetResult? worst = null;
                    foreach (var bank in banks)
                    {
                        if (!all && !over.ContainsKey(bank.Index)) continue;   // without a "min", only the banks listed are budgeted
                        double limit = over.TryGetValue(bank.Index, out var v) ? v : min;
                        var r = Floor($"romBankFree.{bank.Index}", bank.Free, limit, bank.Size, $"bank {bank.Index}: {bank.Free} of {bank.Size} bytes free (padding runs of 64 or more $00 / $FF bytes)");
                        if (worst == null || Rank(r) > Rank(worst) || (Rank(r) == Rank(worst) && r.Value - r.Limit < worst.Value - worst.Limit)) worst = r;
                        if (r.Status != "ok") list.Add(r);
                    }
                    if (worst != null && worst.Status == "ok") list.Add(new BudgetResult { Id = "romBankFree", Kind = "min", Value = worst.Value, Limit = worst.Limit, Status = "ok", Detail = $"every bank within its limit; the tightest: {worst.Detail}" });
                    break;
                }
                case "memoryFree":
                    foreach (var mem in b.EnumerateObject().Where(x => x.Name != "what"))
                    {
                        var have = runs.Where(r => r.Memory.ContainsKey(mem.Name)).ToList();
                        if (have.Count == 0) { problems.Add($"budgets.memoryFree: this console has no memory '{mem.Name}' (it has: {string.Join(", ", runs.SelectMany(r => r.Memory.Keys).Distinct())})"); continue; }
                        long size = have[0].Memory[mem.Name].size, free = have.Min(r => r.Memory[mem.Name].free);
                        list.Add(Floor($"memoryFree.{mem.Name}", free, mem.Value.GetDouble(), size, $"{mem.Name}: {free} of {size} bytes unused in the checked scenarios ({(mem.Name is "vram" or "aram" ? "never written a non-zero value" : "never read")})"));
                    }
                    break;
                case "stackFree":
                {
                    var have = runs.Where(r => r.StackFree >= 0).ToList();
                    long free = have.Count == 0 ? -1 : have.Min(r => r.StackFree);
                    double min = b.GetProperty("min").GetDouble();
                    var r = new BudgetResult { Id = "stackFree", Kind = "min", Value = free, Limit = min, Detail = $"{free} bytes between the deepest stack and the data below it" };
                    r.Status = free < 0 ? "ok" : free < min ? "fail" : free < min / 0.9 ? "warn" : "ok";
                    list.Add(r);
                    break;
                }
                case "what": break;
                default: problems.Add($"budgets: unknown budget '{p.Name}' (lagFrames, busyPercent, romBankFree, memoryFree, stackFree)"); break;
            }
        }
        return list;

        static int Rank(BudgetResult r) => r.Status == "fail" ? 2 : r.Status == "warn" ? 1 : 0;
    }

    /// <summary>A cap: over it fails, at 90% of it warns.</summary>
    private static BudgetResult Max(string id, double value, double max, string detail) => new()
    {
        Id = id, Kind = "max", Value = value, Limit = max, Detail = detail,
        Status = value > max ? "fail" : max > 0 && value >= 0.9 * max ? "warn" : "ok",
    };

    /// <summary>A floor of free bytes: under it fails; with the floor at 90% or more of what is free, it warns (within about 11% of the floor).</summary>
    private static BudgetResult Floor(string id, double free, double min, double size, string detail) => new()
    {
        Id = id, Kind = "min", Value = free, Limit = min, Detail = detail,
        Status = free < min ? "fail" : min > 0 && min >= 0.9 * free ? "warn" : "ok",
    };

    // ---------------------------------------------------------------------------------------------------- the history (the trend since the previous build)

    private static JsonArray ReadHistory(string path, out JsonObject? previous, string sha)
    {
        previous = null;
        var arr = new JsonArray();
        if (path.Length == 0 || !File.Exists(path)) return arr;
        try { arr = JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new JsonArray(); } catch { arr = new JsonArray(); }
        for (int i = arr.Count - 1; i >= 0; i--)
            if (arr[i] is JsonObject o && (string?)o["romSha256"] != sha) { previous = o["budgets"] as JsonObject; if (previous != null) { previous = (JsonObject)previous.DeepClone(); previous["romSha256"] = (string?)o["romSha256"]; previous["verifiedAtUtc"] = (string?)o["verifiedAtUtc"]; } break; }
        return arr;
    }

    private static void WriteHistory(string path, JsonArray history, byte[] rom, List<BudgetResult> budgets)
    {
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rom)).ToLowerInvariant();
        var values = new JsonObject();
        foreach (var b in budgets) values[b.Id] = Math.Round(b.Value, 2);
        // one entry per build: a re-verify of the same ROM replaces its entry
        for (int i = history.Count - 1; i >= 0; i--) if (history[i] is JsonObject o && (string?)o["romSha256"] == sha) history.RemoveAt(i);
        history.Add(new JsonObject { ["romSha256"] = sha, ["verifiedAtUtc"] = DateTime.UtcNow.ToString("o"), ["budgets"] = values });
        while (history.Count > 50) history.RemoveAt(0);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, history.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
