using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NesEmulator.Debugging;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// BrokenDev's <c>verify</c> and <c>run</c> for the Game Boy (consoles <c>gb</c> and <c>gbc</c>; milestone D2 of
/// docs/projects/brokendev-debugging-plan.md, the hello world of VRUN: Vaults of Obsolete Code), and three scenario keys any console may use:
/// <list type="bullet">
///   <item><c>"console": "gb"</c> on a scenario of a gbc project (or the reverse): the same cartridge on the other model, so one dual-mode ROM is
///   proven in colour on a Game Boy Color and in four shades on an original.</item>
///   <item><c>"batteryFrom": "&lt;scenario id&gt;"</c>: the scenario powers on with the battery save the named (earlier) scenario left, as a second
///   power-on of the same cartridge (a boot counter, a save game).</item>
///   <item><c>expect.memory</c>: <c>[{ "symbol": "_boot_count", "size": 2, "is": 2, "what": "..." }]</c> (or <c>"addr": "0xC0B5"</c>; <c>is</c>,
///   <c>not</c>, <c>min</c>, <c>max</c>; little-endian when <c>size</c> is 2): the game's memory at the last frame, read by the build's symbols.
///   NES: system RAM; Game Boy: the CPU's view without side effects. <c>expect.maxDistinctColors</c>: at most N colours at the last frame.</item>
/// </list>
/// Compat (<c>--brokendev run</c>) reads the Game Boy's <c>wram</c> (32 KB on a Color, 8 KB on an original: offset 0 = $C000, bank n at n x $1000),
/// <c>hram</c> (offset 0 = $FF80) and <c>cartram</c> (the cartridge's RAM, bank n at n x $2000), the domains the other emulators' adapters read too.
/// </summary>
internal static partial class BrokenDevCli
{
    private sealed partial class ScenarioResult
    {
        /// <summary>The battery save this scenario powered on with (batteryFrom), and the one it left (when another scenario asks for it).</summary>
        public byte[]? BatteryIn, BatteryOut;
        public string BatteryNote = "";
        public string Console = "";
        public List<(JsonElement Spec, long Value, string Where)> Memory = new();
    }

    internal static bool IsGameBoyKey(string console) => console is "gb" or "gbc";

    /// <summary>The console a scenario runs on: the project's, or its own "console" when it names the other model of the same family.</summary>
    private static string ScenarioConsole(string projectConsole, JsonElement sc)
    {
        if (!sc.TryGetProperty("console", out var c) || c.GetString() is not { Length: > 0 } own || own == projectConsole) return projectConsole;
        if (IsGameBoyKey(own) && IsGameBoyKey(projectConsole)) return own;
        throw new ArgumentException($"scenario console '{own}' is not the other model of the project's console '{projectConsole}' (only gb and gbc swap)");
    }

    private static IRunner MakeRunner(string console, string romPath, byte[] rom, JsonElement verify, bool ntsc, string powerOnRam) =>
        console == "nes" ? new NesRunner(romPath, rom, verify, ntsc, powerOnRam) : new SnesRunner(rom, Path.GetFileName(romPath), verify, console);

    /// <summary>The symbol files the build published beside the ROM (build.outputs of kind symbols).</summary>
    private static List<string> PublishedSymbolFiles(JsonElement root, string romPath)
    {
        var list = new List<string>();
        foreach (var o in root.GetProperty("build").GetProperty("outputs").EnumerateArray())
            if (o.TryGetProperty("kind", out var k) && k.GetString() == "symbols")
            {
                string p = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(romPath))!, o.GetProperty("publishAs").GetString() ?? "");
                if (File.Exists(p)) list.Add(p);
            }
        return list;
    }

    private static SymbolTable? LoadSymbols(List<string> files, string console)
    {
        if (files.Count == 0) return null;
        var t = new SymbolTable();
        string family = console == "nes" ? "nes" : IsGameBoyKey(console) ? "gb" : "snes";
        foreach (var f in files) t.Load(f, family);
        return t;
    }

    /// <summary>expect.memory, read at the last frame (the runner is still alive).</summary>
    private static void ReadMemoryExpectations(ScenarioResult res, IRunner runner, JsonElement? expect, SymbolTable? symbols)
    {
        if (expect is not { } e || !e.TryGetProperty("memory", out var mem)) return;
        foreach (var m in mem.EnumerateArray())
        {
            int size = m.TryGetProperty("size", out var sz) ? sz.GetInt32() : 1;
            if (size is < 1 or > 4) throw new ArgumentException("expect.memory: size must be 1 to 4");
            uint addr; string where;
            if (m.TryGetProperty("symbol", out var s))
            {
                string name = s.GetString() ?? "";
                if (symbols == null) throw new ArgumentException($"expect.memory: '{name}' needs the build's symbols (an output of kind symbols)");
                if (!symbols.TryFind(name, out var sym, out string? err)) throw new ArgumentException($"expect.memory: {err ?? "no symbol " + name}");
                addr = sym.Address; where = $"{name} (${addr:X4})";
            }
            else
            {
                string a = m.GetProperty("addr").GetString() ?? "0";
                addr = a.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt32(a[2..], 16) : a.StartsWith('$') ? Convert.ToUInt32(a[1..], 16) : uint.Parse(a, CultureInfo.InvariantCulture);
                where = $"${addr:X4}";
            }
            long v = 0;
            for (int i = 0; i < size; i++) v |= (long)runner.PeekCpu(addr + (uint)i) << (8 * i);
            res.Memory.Add((m.Clone(), v, where));
        }
    }

    private static void EvaluateGb(ScenarioResult r, JsonElement? expect)
    {
        void Add(string id, bool ok, string detail) => r.Checks.Add(new CheckResult { Id = id, Ok = ok, Detail = detail });
        if (expect is { } e && e.TryGetProperty("maxDistinctColors", out var mx))
            Add("max-distinct-colors", r.Colors <= mx.GetInt32(), $"{r.Colors} colours at the last frame, need <= {mx.GetInt32()}");
        int n = 0;
        foreach (var (spec, v, where) in r.Memory)
        {
            n++;
            string id = "memory-" + (spec.TryGetProperty("symbol", out var s) ? (s.GetString() ?? "").TrimStart('_') : n.ToString(CultureInfo.InvariantCulture));
            string what = spec.TryGetProperty("what", out var w) ? " - " + w.GetString() : "";
            bool ok = true; var want = new List<string>();
            if (spec.TryGetProperty("is", out var isv)) { ok &= v == isv.GetInt64(); want.Add($"= {isv.GetInt64()}"); }
            if (spec.TryGetProperty("not", out var nv)) { ok &= v != nv.GetInt64(); want.Add($"!= {nv.GetInt64()}"); }
            if (spec.TryGetProperty("min", out var mn)) { ok &= v >= mn.GetInt64(); want.Add($">= {mn.GetInt64()}"); }
            if (spec.TryGetProperty("max", out var ma)) { ok &= v <= ma.GetInt64(); want.Add($"<= {ma.GetInt64()}"); }
            if (want.Count == 0) { ok = false; want.Add("(no is / not / min / max given)"); }
            Add(id, ok, $"{where} = {v} at the last frame, need {string.Join(" and ", want)}{what}");
        }
        if (r.BatteryNote.Length > 0) Add("battery-in", r.BatteryIn != null && r.BatteryNote.StartsWith("Applied", StringComparison.Ordinal), r.BatteryNote);
    }

    private static JsonNode? GbJson(ScenarioResult r) => new JsonObject
    {
        ["console"] = r.Console,
        ["battery"] = r.BatteryNote.Length > 0 || r.BatteryOut != null ? new JsonObject { ["in"] = r.BatteryNote, ["outBytes"] = r.BatteryOut?.Length ?? 0 } : null,
        ["memory"] = r.Memory.Count == 0 ? null : new JsonArray(r.Memory.Select(m => (JsonNode)new JsonObject { ["where"] = m.Where, ["value"] = m.Value }).ToArray()),
    };

    /// <summary>The scenario ids some other scenario powers on from (their battery is kept at the end).</summary>
    private static HashSet<string> BatterySources(JsonElement verify) =>
        verify.GetProperty("scenarios").EnumerateArray()
            .Select(s => s.TryGetProperty("batteryFrom", out var b) ? b.GetString() ?? "" : "")
            .Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>A Game Boy memory domain for compat (the domains every emulator's adapter reads): wram, hram, cartram.</summary>
    private static int PeekGb(GbSession s, string domain, int addr)
    {
        var b = s.Board;
        return domain switch
        {
            "wram" => (uint)addr < (uint)b.Wram.Length ? b.Wram[addr] : throw new ArgumentException($"wram offset {addr:X} is outside {b.Wram.Length} bytes"),
            "hram" => (uint)addr < (uint)b.Hram.Length ? b.Hram[addr] : throw new ArgumentException($"hram offset {addr:X} is outside {b.Hram.Length} bytes"),
            "cartram" => (uint)addr < (uint)b.Cart.Ram.Length ? b.Cart.Ram[addr] : throw new ArgumentException($"cartram offset {addr:X} is outside the cartridge's {b.Cart.Ram.Length} bytes"),
            _ => throw new ArgumentException($"the Game Boy has no memory domain '{domain}' here (wram, hram, cartram)"),
        };
    }
}
