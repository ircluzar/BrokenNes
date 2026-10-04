using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-golden [--update] [--only name] [--list] [--poison] [--games-root dir] [--tests-root dir] [--file path]</c>: the Genesis golden-hash suite. Each entry names a ROM, a region, a pad script and the
/// frames at which the picture's hash is recorded (FNV-1a over width, height and the ARGB pixels); a run reproduces them or reports which frame changed, so a speed change can be shown to leave every
/// picture bit for bit as it was. Entries whose <c>verified</c> field is <c>oracle</c> hold frames that were compared with BlastEm 1.0.0's own screenshots and matched in every pixel (see
/// <c>docs/projects/sega-track-b2a.md</c>); <c>regression</c> entries only pin what this emulator draws. ROMs are not in git: a missing ROM is reported as skipped, never as a pass. <c>--poison</c> flips one
/// bit of every picture before hashing, to show the suite fails when pictures change (every checked frame must then differ). <c>--update</c> re-records the hashes of the entries it ran.
/// ROM names: <c>games:file</c> under the game library (<c>X:\EMULATION\Sega Genesis</c>), <c>tests:file</c> under the Genesis test-ROM folder, <c>tests:archive.zip|inner.bin</c> for a ROM inside a zip.
/// </summary>
internal static class MdGoldenCli
{
    private const string DefaultGames = @"X:\EMULATION\Sega Genesis", DefaultTests = @"C:\BrokenNes-tools\sega\test-sets\md-roms";

    public static ulong PictureHash(uint[] frame, int width, int height, bool poison = false)
    {
        ulong h = 14695981039346656037UL;
        int n = width * height;
        for (int i = 0; i < n; i++) { uint v = frame[i]; if (poison && i == 0) v ^= 1; h ^= v; h *= 1099511628211UL; }
        return h ^ (ulong)width << 40 ^ (ulong)height << 52;
    }

    private static byte[]? LoadRom(string name, string gamesRoot, string testsRoot)
    {
        try
        {
            string root = name.StartsWith("games:") ? gamesRoot : testsRoot;
            string rest = name[(name.IndexOf(':') + 1)..];
            int bar = rest.IndexOf('|');
            if (bar >= 0)
            {
                using var z = ZipFile.OpenRead(Path.Combine(root, rest[..bar]));
                var e = z.Entries.FirstOrDefault(x => x.Name.Equals(rest[(bar + 1)..], StringComparison.OrdinalIgnoreCase));
                if (e == null) return null;
                using var ms = new MemoryStream(); using var s = e.Open(); s.CopyTo(ms); return ms.ToArray();
            }
            string path = Path.Combine(root, rest);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch { return null; }
    }

    private static SortedDictionary<int, PadButtons> ParseInput(string? script)
    {
        var map = new SortedDictionary<int, PadButtons>();
        if (string.IsNullOrWhiteSpace(script)) return map;
        foreach (var part in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            if (colon < 0 || !int.TryParse(part[..colon], out int frame)) continue;
            PadButtons b = PadButtons.None;
            foreach (var name in part[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Enum.TryParse<PadButtons>(name, true, out var one)) b |= one;
            map[frame] = b;
        }
        return map;
    }

    public static int Run(string[] args)
    {
        string file = M68kTestCli.Arg(args, "--file") ?? FindGoldenFile();
        string gamesRoot = M68kTestCli.Arg(args, "--games-root") ?? DefaultGames, testsRoot = M68kTestCli.Arg(args, "--tests-root") ?? DefaultTests;
        bool update = M68kTestCli.Flag(args, "--update"), poison = M68kTestCli.Flag(args, "--poison"), list = M68kTestCli.Flag(args, "--list");
        string? only = M68kTestCli.Arg(args, "--only");
        if (!File.Exists(file)) { Console.WriteLine($"no golden file at {file}"); return 2; }
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var entries = root["entries"]!.AsArray();
        int ok = 0, bad = 0, skipped = 0, frames = 0;
        long totalMs = 0;
        foreach (var node in entries)
        {
            var e = node!.AsObject();
            string name = (string)e["name"]!;
            if (only != null && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            var cps = e["checkpoints"]!.AsObject();
            if (list) { Console.WriteLine($"{name}  [{(string?)e["verified"] ?? "regression"}]  {cps.Count} frames  {(string)e["rom"]!}"); continue; }
            byte[]? rom = LoadRom((string)e["rom"]!, gamesRoot, testsRoot);
            if (rom == null) { skipped++; Console.WriteLine($"  SKIP  {name}: ROM not found ({(string)e["rom"]!})"); continue; }
            RegionChoice? region = ((string?)e["region"])?.ToLowerInvariant() switch { "pal" => RegionChoice.Pal, "ntsc" => RegionChoice.Ntsc, _ => null };
            var session = new MdSession(rom, "M68K", "MD", "MD", region);
            var inputs = ParseInput((string?)e["input"]);
            var wanted = cps.Select(kv => (frame: int.Parse(kv.Key), hash: (string?)kv.Value)).OrderBy(x => x.frame).ToList();
            int last = wanted.Last().frame, next = 0;
            PadButtons pad = PadButtons.None;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = new List<string>();
            bool entryOk = true;
            for (int f = 1; f <= last; f++)
            {
                if (inputs.TryGetValue(f - 1, out var b)) pad = b;
                session.SetPad(0, pad);
                session.RunFrame();
                while (next < wanted.Count && wanted[next].frame == f)
                {
                    ulong h = PictureHash(session.Frame, session.FrameWidth, session.FrameHeight, poison);
                    string hex = h.ToString("X16");
                    if (update) { cps[wanted[next].frame.ToString()] = hex; results.Add($"{f}:updated"); }
                    else if (hex == wanted[next].hash) { results.Add($"{f}:ok"); ok++; }
                    else { results.Add($"{f}:CHANGED {hex}"); bad++; entryOk = false; }
                    next++;
                }
            }
            sw.Stop(); totalMs += sw.ElapsedMilliseconds; frames += last;
            Console.WriteLine($"  {(entryOk ? "PASS" : "FAIL")}  {name} [{(string?)e["verified"] ?? "regression"}]  {string.Join(" ", results)}  ({sw.Elapsed.TotalSeconds:F1} s, {last / Math.Max(0.001, sw.Elapsed.TotalSeconds):F0} frames/s)");
        }
        if (update) { File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine($"== updated {file}"); return 0; }
        if (!list) Console.WriteLine($"== {ok} frames match, {bad} changed, {skipped} entries skipped (ROM missing); {frames:N0} frames run in {totalMs / 1000.0:F1} s ({frames / Math.Max(0.001, totalMs / 1000.0):F0} frames/s overall)");
        return bad == 0 ? 0 : 1;
    }

    private static string FindGoldenFile()
    {
        // from the current directory up (the repository root when run as documented), then from the executable up
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            string? dir = start;
            for (int i = 0; i < 10 && dir != null; i++, dir = Path.GetDirectoryName(dir))
            {
                string p = Path.Combine(dir, "Workshop", "MdGolden", "md_golden.json");
                if (File.Exists(p)) return p;
            }
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "Workshop", "MdGolden", "md_golden.json");
    }
}
