using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes.Workshop.Debug;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace BrokenNes.Workshop.Movies;

/// <summary>
/// <c>--determinism-test [--consoles nes,snes,...] [--frames N] [--roms FILE] [--only replay,twin,state,inplace,idem] [--keep DIR]</c>:
/// replay determinism on every console's accuracy core (the default cores), compat catalog P0 item 2 (docs/projects/compat-catalog-plan.md).
/// Per ROM, a seeded "real play" input (directions held, A/B mashed, Start now and then) over N frames, and:
/// <list type="bullet">
/// <item>replay: record a .bnm movie, write and parse it, replay it on a freshly powered-on machine; every keyframe's picture and RAM must match;</item>
/// <item>twin: two fresh machines in lock step, picture and RAM compared EVERY frame, and the final save-state bytes (hidden state: power-on reproducibility);</item>
/// <item>state: a save state at N/3 and 2N/3 loaded into a fresh machine, continued to the end, every frame compared to the straight run, and the final state bytes;</item>
/// <item>inplace: save + load in place every 250 frames on one machine, every frame compared to the straight run;</item>
/// <item>idem: save, load, save gives the same bytes.</item>
/// </list>
/// ROMs: the built-in test ROMs always run; the user's games and commercial ROMs come from <c>UAT/determinism-roms.txt</c> (a missing file is NOT RUN, never a pass).
/// Exit 0 when every check passes.
/// </summary>
internal static class DeterminismTestCli
{
    private static int pass, fail, notRun;
    private static readonly List<string> failures = new();
    private static readonly Dictionary<string, (int pass, int fail)> perConsole = new();

    private static void Check(string console, string name, bool ok, string detail)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
        var (p, f) = perConsole.TryGetValue(console, out var v) ? v : (0, 0);
        if (ok) { pass++; perConsole[console] = (p + 1, f); }
        else { fail++; failures.Add($"[{console}] {name}: {detail}"); perConsole[console] = (p, f + 1); }
    }

    public static int Run(string[] args)
    {
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        var consoles = Opt("consoles", string.Join(",", MovieMachine.ConsoleKeys)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => c.ToLowerInvariant() == "wm" ? "wms" : c.ToLowerInvariant()).ToHashSet();
        long frames = long.Parse(Opt("frames", "3000"));
        var only = Opt("only", "replay,twin,state,inplace,idem").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string dir = Opt("keep", Path.Combine(Path.GetTempPath(), "brokennes-determinism-" + Environment.ProcessId));
        string list = Opt("roms", Path.Combine(AppContext.BaseDirectory, "determinism-roms.txt"));
        if (!File.Exists(list)) list = FindRepoFile("UAT/determinism-roms.txt") ?? list;
        Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();

        var roms = new List<(string console, string path, string label)>
        {
            ("nes", DebugTestRoms.Write(dir, "planted.nes", MovieTestRoms.PlantedGame(MovieTestRoms.ParseSettings("version 1\nstart Start\nfloor 1\nbonus 1\n")).rom), "test ROM: planted game"),
            ("snes", DebugTestRoms.Write(dir, "debug.sfc", DebugTestRoms.Snes().rom), "test ROM: debugger SNES"),
            ("gb", DebugTestRoms.Write(dir, "debug.gb", DebugTestRoms.GameBoy().rom), "test ROM: debugger Game Boy"),
        };
        if (File.Exists(list))
            foreach (var raw in File.ReadAllLines(list))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.StartsWith('#')) continue;
                var p = l.Split('|', 3, StringSplitOptions.TrimEntries);
                if (p.Length < 2) continue;
                roms.Add((p[0].ToLowerInvariant(), Environment.ExpandEnvironmentVariables(p[1]), p.Length > 2 ? p[2] : Path.GetFileName(p[1])));
            }
        else Console.WriteLine($"NOT RUN  the ROM list {list} is missing: only the built-in test ROMs run");

        Console.WriteLine($"determinism tests: {frames} frames per ROM, consoles {string.Join(" ", consoles)}, checks {string.Join(" ", only)}");
        foreach (var (console, path, label) in roms)
        {
            if (!consoles.Contains(console)) continue;
            Console.WriteLine($"== {console}: {label}");
            if (!File.Exists(path)) { Console.WriteLine($"  NOT RUN  {path} is not on this machine"); notRun++; continue; }
            try { OneRom(console, path, label, frames, only, dir); }
            catch (Exception e) { Check(console, $"{label}: ran to the end", false, e.GetType().Name + ": " + e.Message + " " + e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()); }
        }

        Console.WriteLine();
        foreach (var kv in perConsole.OrderBy(k => Array.IndexOf(MovieMachine.ConsoleKeys, k.Key)))
            Console.WriteLine($"  {kv.Key,-5} {(kv.Value.fail == 0 ? "PASS" : "FAIL")}  {kv.Value.pass} passed, {kv.Value.fail} failed");
        Console.WriteLine($"determinism tests: {pass} passed, {fail} failed, {notRun} not run ({sw.Elapsed.TotalSeconds:F1} s)");
        foreach (var f in failures) Console.WriteLine("  FAILED " + f);
        return fail == 0 ? 0 : 1;
    }

    private static string? FindRepoFile(string rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>The seeded play: a held direction (mostly Right), A / B mashed, Start for a few frames every ~7 s (gets past titles and menus).</summary>
    public static Movie PlayInput(Movie mv, long frames, int seed)
    {
        var rng = new Random(seed);
        PadButtons[] dirs = { PadButtons.Right, PadButtons.Right, PadButtons.Right, PadButtons.Left, PadButtons.Up, PadButtons.Down, PadButtons.None, PadButtons.Right | PadButtons.Up };
        long f = 0;
        while (f < frames)
        {
            if (f % 420 < 40 && f % 420 >= 30) { mv.SetPads(f, PadButtons.Start, PadButtons.None); f += 4; if (f < frames) mv.SetPads(f, PadButtons.None, PadButtons.None); f += 6; continue; }
            var d = dirs[rng.Next(dirs.Length)];
            int len = rng.Next(6, 50);
            for (int i = 0; i < len && f < frames; i++, f++)
            {
                var b = d;
                if ((i / 3 + rng.Next(2)) % 2 == 0) b |= PadButtons.A;
                if (rng.Next(5) == 0) b |= PadButtons.B;
                mv.SetPads(f, b, PadButtons.None);
            }
        }
        return mv;
    }

    private readonly record struct Hashes(ulong Pic, ulong Ram);

    private static List<Hashes> Straight(Movie mv, MovieMachine m, long from, long to, Action<long>? at = null)
    {
        var h = new List<Hashes>();
        for (long f = from; f < to; f++)
        {
            var (p1, p2) = mv.PadsAt(f);
            m.Frame(p1, p2);
            h.Add(new Hashes(m.Picture(), m.Ram()));
            at?.Invoke(f + 1);
        }
        return h;
    }

    private static string FirstDiff(List<Hashes> a, List<Hashes> b, long offset)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            if (a[i] != b[i]) return $"first differs after frame {offset + i + 1} ({(a[i].Pic != b[i].Pic ? "picture" : "")}{(a[i].Pic != b[i].Pic && a[i].Ram != b[i].Ram ? " + " : "")}{(a[i].Ram != b[i].Ram ? "RAM" : "")})";
        return a.Count == b.Count ? "" : $"lengths {a.Count} vs {b.Count}";
    }

    private static string FirstByte(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return $"first differing byte {i} of {a.Length}/{b.Length}: ...{Ctx(a, i)} vs ...{Ctx(b, i)}";
        return a.Length == b.Length ? "" : $"lengths {a.Length} vs {b.Length}";
    }

    private static string Ctx(byte[] s, int i)
    {
        int st = Math.Max(0, i - 40), len = Math.Min(s.Length - st, 80);
        var t = new string(s.Skip(st).Take(len).Select(c => c >= 32 && c < 127 ? (char)c : '.').ToArray());
        return t;
    }

    private static void OneRom(string console, string path, string label, long frames, HashSet<string> only, string dir)
    {
        int seed = (int)(MovieHashSeed(path) & 0x7fffffff);
        MovieMachine Fresh() => MovieMachine.Open(path, console, "", "fceux", false, false);
        Movie mv;
        using (var m = Fresh())
        {
            mv = PlayInput(MoviePlayer.NewFor(m, frames, 60, "determinism"), frames, seed);
            Console.WriteLine($"  cores {m.Cores}, {mv.Inputs.Count} input changes, seed {seed}");
        }

        // the straight run: every frame's hashes
        List<Hashes> straight; byte[]? endState = null; byte[]? s1 = null, s2 = null;
        long n1 = frames / 3, n2 = 2 * frames / 3;
        bool states = true; string noState = "";
        using (var m = Fresh())
        {
            straight = Straight(mv, m, 0, frames, f =>
            {
                if (!states) return;
                try { if (f == n1) s1 = m.Host.SaveState(); if (f == n2) s2 = m.Host.SaveState(); }
                catch (NotSupportedException e) { states = false; noState = e.Message; }
            });
            if (states) endState = m.Host.SaveState();
        }
        string lp = Path.GetFileNameWithoutExtension(path);

        if (only.Contains("replay"))
        {
            Movie rec;
            var script = string.Join(",", mv.Inputs.Select(i => $"{i.Frame}:{(i.P1 == PadButtons.None ? "" : Movie.ButtonsText(i.P1))}"));
            using (var m = Fresh()) rec = MoviePlayer.Record(m, script, "", frames, 60, Array.Empty<(string, string)>(), "determinism");
            string file = Path.Combine(dir, console + "-" + seed + MovieFormat.Extension);
            rec.Save(file);
            var back = Movie.Load(file);
            PlayReport r;
            using (var m = MovieMachine.OpenFor(back, path, false)) r = MoviePlayer.Play(back, m);
            bool recMatchesStraight = rec.Keys.All(k => straight[(int)k.Frame - 1] == new Hashes(k.Picture, k.Ram));
            Check(console, $"{label}: a recorded .bnm replays in sync on a fresh power-on", r.InSync && r.KeysMatched == r.KeysChecked && r.KeysChecked == rec.Keys.Count && recMatchesStraight,
                  $"{r.KeysMatched} of {r.KeysChecked} keyframes{(r.OffCourseFrame != null ? $", off course after {r.OffCourseFrame}: {r.OffCourseWhat}" : "")}{(recMatchesStraight ? "" : ", the recording's keyframes differ from the straight run")}{(r.Crashed ? ", crashed " + r.CrashInfo : "")}");
        }

        if (only.Contains("twin"))
        {
            List<Hashes> twin; byte[]? twinEnd = null;
            using (var m = Fresh()) { twin = Straight(mv, m, 0, frames); if (states) twinEnd = m.Host.SaveState(); }
            string d = FirstDiff(straight, twin, 0);
            Check(console, $"{label}: two power-ons in lock step match every frame", d.Length == 0, d.Length == 0 ? $"{frames} frames" : d);
            if (states)
            {
                string b = FirstByte(StatePayload(endState!), StatePayload(twinEnd!));
                Check(console, $"{label}: two power-ons end in the same save state (hidden state)", b.Length == 0, b.Length == 0 ? $"{endState!.Length} bytes" : b);
            }
        }

        if (!states) { Console.WriteLine($"  NOT RUN  save-state checks: {noState}"); notRun++; return; }

        if (only.Contains("state"))
            foreach (var (n, s) in new[] { (n1, s1!), (n2, s2!) })
            {
                List<Hashes> cont; byte[] contEnd;
                using (var m = Fresh()) { Straight(mv, m, 0, 7); m.Host.LoadState(s); cont = Straight(mv, m, n, frames); contEnd = m.Host.SaveState(); }
                string d = FirstDiff(straight.Skip((int)n).ToList(), cont, n);
                Check(console, $"{label}: a state saved after frame {n}, loaded on a fresh machine, continues like the straight run", d.Length == 0, d.Length == 0 ? $"{frames - n} frames" : d);
                string b = FirstByte(StatePayload(endState!), StatePayload(contEnd));
                Check(console, $"{label}: ... and ends in the same save state", b.Length == 0, b.Length == 0 ? $"{contEnd.Length} bytes" : b);
            }

        if (only.Contains("inplace"))
        {
            List<Hashes> ip;
            using (var m = Fresh()) ip = Straight(mv, m, 0, frames, f => { if (f % 250 == 0) m.Host.LoadState(m.Host.SaveState()); });
            string d = FirstDiff(straight, ip, 0);
            Check(console, $"{label}: save + load in place every 250 frames changes nothing", d.Length == 0, d.Length == 0 ? $"{frames} frames" : d);
        }

        if (only.Contains("idem"))
        {
            using var m = Fresh();
            m.Host.LoadState(s1!);
            var again = m.Host.SaveState();
            string b = FirstByte(StatePayload(s1!), StatePayload(again));
            Check(console, $"{label}: save, load, save gives the same state", b.Length == 0, b.Length == 0 ? $"{again.Length} bytes" : b);
        }
    }

    private static byte[] StatePayload(byte[] s)
    {
        // a session state is the BNCS container (header, then the deflated payload): compare the payload, so a difference names a real offset
        if (s.Length < 8 || s[0] != 'B' || s[1] != 'N' || s[2] != 'C' || s[3] != 'S') return MaskHostQueue(s);
        using var r = new BinaryReader(new MemoryStream(s), System.Text.Encoding.UTF8);
        r.ReadBytes(4); r.ReadUInt16(); r.ReadString(); r.ReadString(); r.ReadString();
        int n = r.ReadByte();
        for (int i = 0; i < n; i++) { r.ReadString(); r.ReadString(); }
        int length = r.ReadInt32(); r.ReadUInt32(); int packed = r.ReadInt32();
        using var z = new System.IO.Compression.DeflateStream(new MemoryStream(r.ReadBytes(packed)), System.IO.Compression.CompressionMode.Decompress);
        var o = new MemoryStream(); z.CopyTo(o);
        return o.ToArray();
    }

    /// <summary>
    /// The NES APUs save their host audio queue's positions (ringWrite / ringRead / ringCount: how many output samples wait for the sound
    /// card, not emulated state; the samples themselves are not saved and a load empties the queue on purpose, as battery.ps1's
    /// load-save-not-idempotent filter says). They depend on how often the host drained the queue, so they are masked to 0 here.
    /// </summary>
    private static byte[] MaskHostQueue(byte[] s)
    {
        var t = System.Text.Encoding.UTF8.GetString(s);
        var m = System.Text.RegularExpressions.Regex.Replace(t, @"(\\*""ring(?:Write|Read|Count)\\*""):\d+", "$1:0");
        return System.Text.Encoding.UTF8.GetBytes(m);
    }

    private static ulong MovieHashSeed(string path)
    {
        ulong h = 14695981039346656037;
        foreach (char c in Path.GetFileName(path)) { h ^= c; h *= 1099511628211; }
        return h;
    }
}
