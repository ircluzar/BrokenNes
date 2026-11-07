// BrokenDev's x86 runner: a DOS game project (kind dos, console x86) built, run and checked on BrokenNes's x86 PC the way the cartridge consoles are.
// The game (.exe / .com) boots on a FreeDOS disk made for it (PcDisks.ForProgram, as the desktop app opens it); a scenario can put files beside it first
// (a test script) and check, after its last frame, the files the game left on the disk (a save, a status file), its port E9h log and what DOS printed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BrokenNes.Workshop;

internal static partial class BrokenDevCli
{
    internal static bool IsX86Key(string console) => console == "x86";

    /// <summary>verify.x86: { "ips": instructions a second } (default the app's, PcSession.DefaultIps). Returns the note the runner's cores line carries.</summary>
    private static string X86Configure(NesEmulator.X86.PcSession pc, JsonElement verify)
    {
        if (verify.ValueKind == JsonValueKind.Object && verify.TryGetProperty("x86", out var x) && x.ValueKind == JsonValueKind.Object && x.TryGetProperty("ips", out var ips))
        {
            long v = ips.GetInt64();
            if (v < 1_000_000 || v > 2_000_000_000) throw new ArgumentException($"verify.x86.ips {v} is outside 1M..2000M");
            pc.Machine.InstructionsPerSecond = v;
        }
        return $" ips={pc.Machine.InstructionsPerSecond / 1_000_000}M" + (pc.Program != null ? " program=" + pc.Program : "");
    }

    /// <summary>Peek on the x86: domain "mem" = physical memory (with paging off, as DOS extenders run, a linear address).</summary>
    private static int PeekX86(NesEmulator.X86.PcSession pc, string domain, int addr) =>
        domain == "mem" ? pc.Machine.Phys8((uint)addr) : throw new ArgumentException($"the x86 PC has no memory domain '{domain}' here (mem)");

    /// <summary>A scenario's "dosFiles": { "TEST.TXT": "line\nline" } put on the disk (root-relative DOS paths, text with CRLF line ends) before the first frame.</summary>
    private static void X86Before(IRunner runner, JsonElement sc)
    {
        if (!sc.TryGetProperty("dosFiles", out var df)) return;
        if (runner is not SnesRunner { Pc: { } pc }) throw new ArgumentException("dosFiles work on an x86 scenario only");
        foreach (var f in df.EnumerateObject())
        {
            string text = (f.Value.GetString() ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            pc.PutFile(f.Name.TrimStart('\\', '/'), Encoding.ASCII.GetBytes(text));
        }
    }

    /// <summary>expect.dos, read after the last frame: files [{ path, exists, contains, notContains }], debugPort and teletype { contains, notContains } (strings or lists).</summary>
    private static void X86End(ScenarioResult r, IRunner runner, JsonElement? expect)
    {
        if (runner is not SnesRunner { Pc: { } pc }) { if (expect is { } e0 && e0.TryGetProperty("dos", out _)) r.Checks.Add(new CheckResult { Id = "dos", Ok = false, Detail = "expect.dos is for x86 projects" }); return; }
        r.X86Port = pc.Machine.DebugPort.ToString();
        if (expect is not { } e || !e.TryGetProperty("dos", out var dos)) return;
        void Add(string id, bool ok, string detail) => r.Checks.Add(new CheckResult { Id = id, Ok = ok, Detail = detail });
        static List<string> Strings(JsonElement o, string key)
        {
            var l = new List<string>();
            if (!o.TryGetProperty(key, out var v)) return l;
            if (v.ValueKind == JsonValueKind.String) l.Add(v.GetString() ?? "");
            else foreach (var s in v.EnumerateArray()) l.Add(s.GetString() ?? "");
            return l;
        }
        void Text(string id, string text, JsonElement spec, string what)
        {
            foreach (var c in Strings(spec, "contains")) Add($"{id}-contains", text.Contains(c, StringComparison.Ordinal), $"{what} {(text.Contains(c, StringComparison.Ordinal) ? "has" : "lacks")} \"{c}\"");
            foreach (var c in Strings(spec, "notContains")) Add($"{id}-not-contains", !text.Contains(c, StringComparison.Ordinal), $"{what} {(text.Contains(c, StringComparison.Ordinal) ? "has" : "lacks")} \"{c}\"");
        }
        if (dos.TryGetProperty("files", out var files))
            foreach (var f in files.EnumerateArray())
            {
                string path = (f.GetProperty("path").GetString() ?? "").TrimStart('\\', '/');
                byte[]? data;
                try { data = pc.ReadFile(path); } catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException) { data = null; }
                string id = "dos-file-" + path.ToLowerInvariant();
                bool want = !f.TryGetProperty("exists", out var ex2) || ex2.ValueKind != JsonValueKind.False;
                Add(id, (data != null) == want, data != null ? $"{path}: {data.Length.ToString(CultureInfo.InvariantCulture)} bytes" : $"{path}: not on the disk");
                if (data != null) Text(id, Encoding.ASCII.GetString(data), f, path);
            }
        if (dos.TryGetProperty("debugPort", out var dp)) Text("dos-debug-port", r.X86Port, dp, "the port E9h log");
        if (dos.TryGetProperty("teletype", out var tt)) Text("dos-teletype", pc.Machine.Teletype.ToString(), tt, "the BIOS teletype output");
    }

    private sealed partial class ScenarioResult
    {
        /// <summary>The x86 program's port E9h log at the last frame (empty elsewhere).</summary>
        public string X86Port = "";
    }
}
