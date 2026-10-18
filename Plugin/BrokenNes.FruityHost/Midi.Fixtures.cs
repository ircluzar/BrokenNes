// Round 7 (midi): what the Test MIDI export checks are fed. The user's VRUN tree (read only; found under BROKENDEV_HOME like BrokenDev finds it, or BROKENNES_VRUN), its two rosters and their
// MIDI files, BrokenDev's synthetic FL-style song (make_test_mid.py, ported here and compared with the Python original when Python is on the machine), stress songs written here
// (dense, long, many channels, tempo changes, bends, drums only, format 0, running status, no notes, not a MIDI), the template script (BrokenDev/tools/music/build-musictest.ps1)
// and VRUN's own converter CLI, built by that script in its scratch copy. Nothing here writes into the VRUN tree; scratch output goes under %LOCALAPPDATA%\VRUN_Nes_Dev_work\midi-test.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace BrokenNes.FruityHost;

internal sealed record MidiSong(string Name, byte[] Midi, string Source, string MidiPathAsWritten = "");

internal static class MidiFixtures
{
    public static string RepoRoot([CallerFilePath] string here = "")
    {
        var env = Environment.GetEnvironmentVariable("BROKENNES_REPO");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        var d = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "BrokenNes.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("the repository was not found (set BROKENNES_REPO)");
    }

    /// <summary>The user's restored VRUN tree, or null (then every check that needs it is reported NOT RUN, never passed).</summary>
    public static string? Vrun()
    {
        var env = Environment.GetEnvironmentVariable("BROKENNES_VRUN");
        string dir = !string.IsNullOrEmpty(env) ? env : Path.Combine(Environment.GetEnvironmentVariable("BROKENDEV_HOME") is { Length: > 0 } h ? h : @"P:\BrokenDev\env", @"vrun_nes_dev\sys0\simulations\VRUN");
        return File.Exists(Path.Combine(dir, "build.sh")) && Directory.Exists(Path.Combine(dir, @"engine\audio\converter")) ? dir : null;
    }

    public static string Work(string name)
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "VRUN_Nes_Dev_work", "midi-test", name);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>The tracks of a roster: (name, midi path as written, resolved path).</summary>
    public static List<(string Name, string Midi, string Path)> Roster(string rosterFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(rosterFile));
        var list = new List<(string, string, string)>();
        foreach (var t in doc.RootElement.GetProperty("tracks").EnumerateArray())
        {
            string midi = t.GetProperty("midi").GetString()!;
            list.Add((t.GetProperty("name").GetString()!, midi, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(rosterFile)!, midi))));
        }
        return list;
    }

    /// <summary>The eleven songs of VRUN's AudioPlayer roster (empty without the VRUN tree).</summary>
    public static List<MidiSong> RosterSongs()
    {
        var v = Vrun();
        if (v == null) return [];
        return Roster(Path.Combine(v, @"src\audioplayer\roster.json")).Select(t => new MidiSong(t.Name, File.ReadAllBytes(t.Path), "VRUN roster", t.Midi)).ToList();
    }

    // ---- MIDI writing ----

    private static byte[] Vlq(int n)
    {
        var o = new List<byte> { (byte)(n & 0x7F) };
        n >>= 7;
        while (n != 0) { o.Add((byte)(n & 0x7F | 0x80)); n >>= 7; }
        o.Reverse();
        return o.ToArray();
    }

    /// <summary>A track chunk: events (tick, bytes), note-offs before note-ons at the same tick (as make_test_mid.py sorts them), then end of track at <paramref name="end"/>.</summary>
    public static byte[] Track(IEnumerable<(int Tick, byte[] Msg)> events, int end, bool runningStatus = false)
    {
        var sorted = events.Select((e, i) => (e.Tick, e.Msg, i)).OrderBy(e => e.Tick).ThenBy(e => (e.Msg[0] & 0xF0) == 0x80 ? 0 : 1).ThenBy(e => e.i).ToList();
        var d = new List<byte>();
        int last = 0, status = -1;
        foreach (var (tick, msg, _) in sorted)
        {
            d.AddRange(Vlq(tick - last));
            if (runningStatus && msg[0] < 0xF0 && msg[0] == status) d.AddRange(msg.Skip(1)); else d.AddRange(msg);
            status = msg[0] < 0xF0 ? msg[0] : -1;
            last = tick;
        }
        d.AddRange(Vlq(end - last));
        d.AddRange([0xFF, 0x2F, 0x00]);
        var chunk = new List<byte>(Encoding.ASCII.GetBytes("MTrk"));
        chunk.AddRange([(byte)(d.Count >> 24), (byte)(d.Count >> 16), (byte)(d.Count >> 8), (byte)d.Count]);
        chunk.AddRange(d);
        return chunk.ToArray();
    }

    public static byte[] File_(int format, int tpq, params byte[][] tracks)
    {
        var h = new List<byte>(Encoding.ASCII.GetBytes("MThd")) { 0, 0, 0, 6, 0, (byte)format, (byte)(tracks.Length >> 8), (byte)tracks.Length, (byte)(tpq >> 8), (byte)tpq };
        foreach (var t in tracks) h.AddRange(t);
        return h.ToArray();
    }

    public static IEnumerable<(int, byte[])> Notes(int channel, IEnumerable<(int Note, int Len)> seq, int vel = 100, int at = 0)
    {
        int t = at;
        foreach (var (note, len) in seq)
        {
            yield return (t, [(byte)(0x90 | channel), (byte)note, (byte)vel]);
            yield return (t + len, [(byte)(0x80 | channel), (byte)note, 0]);
            t += len;
        }
    }

    private static (int, byte[]) Tempo(int tick, int micros) => (tick, [0xFF, 0x51, 0x03, (byte)(micros >> 16), (byte)(micros >> 8), (byte)micros]);

    /// <summary>BrokenDev's synthetic FL-style song (make_test_mid.py): a lead on channel 1, a bass on 2, drums on 10, 16 beats at 120 BPM, format 1, 480 ticks a beat.</summary>
    public static byte[] Synthetic()
    {
        const int tpq = 480, end = tpq * 16;
        var tempo = Track([Tempo(0, 500000)], end);
        var lead = Track(Notes(0, Enumerable.Range(0, 4).SelectMany(_ => new[] { (60, tpq), (64, tpq), (67, tpq), (72, tpq) })), end);
        var bass = Track(Notes(1, Enumerable.Range(0, 4).SelectMany(_ => new[] { (48, tpq * 2), (43, tpq * 2) })), end);
        var drums = Track(Enumerable.Range(0, 16).SelectMany(beat => Notes(9, [(beat % 2 == 0 ? 36 : 38, 120)], 110, beat * tpq)), end);
        return File_(1, tpq, tempo, lead, bass, drums);
    }

    /// <summary>Songs that make the converter do everything it can: thin, truncate, drop channels, ignore tempo changes and bends, and the shapes of file it must read
    /// (format 0, running status, note-on with velocity 0 as note-off, CC 7). Deterministic.</summary>
    public static List<MidiSong> Stress()
    {
        const int tpq = 480;
        var list = new List<MidiSong> { new("synthetic", Synthetic(), "BrokenDev make_test_mid.py") };
        // dense: a run of notes shorter than a frame (20 ticks at 180 BPM: 0.83 frame), each with its own velocity (a note and a volume change each), plus a bass: thinning
        {
            int end = tpq * 8;
            var ev = new List<(int, byte[])>();
            for (int i = 0; i < 180; i++) ev.AddRange(Notes(0, [(60 + i * 7 % 19, 20)], 40 + i * 13 % 87, i * 20));
            list.Add(new("dense", File_(1, tpq, Track([Tempo(0, 333333)], end), Track(ev, end), Track(Notes(1, Enumerable.Repeat((40, tpq), 8)), end)), "written here: thinning"));
        }
        // long: four minutes of sixteenth notes on two channels: more than 1280 bytes, truncated
        {
            int beats = 480, end = tpq * beats;
            var a = Notes(0, Enumerable.Range(0, beats * 4).Select(i => (60 + i * 7 % 24, tpq / 4)), 90);
            var b = Notes(2, Enumerable.Range(0, beats * 2).Select(i => (72 + i * 5 % 12, tpq / 2)), 60);
            list.Add(new("long", File_(1, tpq, Track([Tempo(0, 500000)], end), Track(a, end), Track(b, end)), "written here: truncation"));
        }
        // many channels: eight melodic channels and drums: notes on the channels the NES has no room for are dropped; tempo changes and pitch bends that are ignored; CC 7 volume
        {
            int end = tpq * 16;
            var tracks = new List<byte[]> { Track([Tempo(0, 500000), Tempo(tpq * 4, 400000), Tempo(tpq * 8, 600000), Tempo(tpq * 12, 500000)], end) };
            for (int ch = 0; ch < 8; ch++)
            {
                var ev = Notes(ch, Enumerable.Range(0, 16 - ch).Select(i => (48 + ch * 5 + i % 7, tpq)), 70 + ch * 5).ToList();
                if (ch == 1) for (int i = 0; i < 16; i++) ev.Add((i * tpq + tpq / 2, [0xE1, 0x00, (byte)(0x40 + i)]));
                if (ch == 0) for (int i = 0; i < 8; i++) ev.Add((i * tpq * 2 + 10, [0xB0, 7, (byte)(127 - i * 12)]));
                tracks.Add(Track(ev, end));
            }
            tracks.Add(Track(Enumerable.Range(0, 32).SelectMany(i => Notes(9, [(i % 3 == 0 ? 36 : 42, 60)], 100, i * tpq / 2)), end));
            list.Add(new("manychannels", File_(1, tpq, tracks.ToArray()), "written here: dropped channels, tempo changes, bends, CC 7"));
        }
        // drums only, format 0 with running status and note-on velocity 0 as note-off, all in one track
        {
            int end = tpq * 8;
            var ev = new List<(int, byte[])> { Tempo(0, 450000) };
            for (int i = 0; i < 32; i++) { ev.Add((i * tpq / 4, [0x99, (byte)(i % 2 == 0 ? 36 : 38), 120])); ev.Add((i * tpq / 4 + 40, [0x99, (byte)(i % 2 == 0 ? 36 : 38), 0])); }
            list.Add(new("drumsonly", File_(0, tpq, Track(ev, end, runningStatus: true)), "written here: format 0, running status, velocity 0, drums only"));
        }
        // very low notes (below A1: the lowest pitch) on a square and the triangle, and a long rest at the start
        {
            int end = tpq * 12;
            list.Add(new("lownotes", File_(1, 96, Track([Tempo(0, 500000)], 96 * 12), Track(Notes(0, [(20, 96), (28, 96), (40, 96), (24, 96)], 100, 96 * 4), 96 * 12), Track(Notes(1, [(12, 192), (30, 192)], 100, 96 * 2), 96 * 12)), "written here: notes below A1, a rest at the start"));
        }
        return list;
    }

    /// <summary>Files the converter cannot use: no notes, not a MIDI file, cut short.</summary>
    public static List<MidiSong> Refused()
    {
        var syn = Synthetic();
        return
        [
            new("nonotes", File_(1, 480, Track([Tempo(0, 500000)], 480 * 4), Track([(0, [0xB0, 7, 100])], 480 * 4)), "written here: no notes"),
            new("notmidi", Encoding.UTF8.GetBytes("This is not a MIDI file, it is a text file somebody named .mid"), "written here: text"),
            new("cutshort", syn[..(syn.Length * 2 / 3)], "written here: the synthetic song cut at two thirds"),
            new("empty", [], "written here: zero bytes"),
        ];
    }

    // ---- processes ----

    public sealed record Proc(int Exit, string Output, double Seconds);

    public static Proc Run(string exe, IEnumerable<string> args, string? cwd = null, int timeoutSec = 900)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = cwd ?? Environment.CurrentDirectory };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Windows PowerShell 5.1 started from a process that inherited PowerShell 7's PSModulePath loads 7's modules and loses Get-FileHash: give it its own
        if (exe.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)) psi.Environment.Remove("PSModulePath");
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return new Proc(-1, "timed out", sw.Elapsed.TotalSeconds); }
        p.WaitForExit();
        return new Proc(p.ExitCode, o.Result + (e.Result.Length > 0 ? "\n--- stderr ---\n" + e.Result : ""), sw.Elapsed.TotalSeconds);
    }

    /// <summary>Runs BrokenDev/tools/music/build-musictest.ps1 (Windows PowerShell, like BrokenDev's own scripts), building in <paramref name="work"/>.</summary>
    public static Proc BuildScript(string mode, string outDir, string work, string? mid = null, string? name = null)
    {
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(RepoRoot(), @"BrokenDev\tools\music\build-musictest.ps1"), "-Mode", mode, "-Out", outDir, "-Work", work };
        if (Vrun() is { } v) { args.Add("-Vrun"); args.Add(v); }
        if (mid != null) { args.Add("-Mid"); args.Add(mid); }
        if (name != null) { args.Add("-Name"); args.Add(name); }
        return Run("powershell.exe", args);
    }

    /// <summary>VRUN's own converter CLI as build_music.py built it in the scratch copy of the template build (null: not built yet).</summary>
    public static string? VrunCli(string work)
    {
        var dll = Path.Combine(work, @"VRUN\engine\audio\converter\cli\bin\Release\net10.0\midi2nes.dll");
        return File.Exists(dll) ? dll : null;
    }

    /// <summary>The bundled template, from the repository's files (the same bytes the plugin embeds).</summary>
    public static (byte[] Rom, string Manifest, string Mlb) Bundled()
    {
        string res = Path.Combine(RepoRoot(), @"Plugin\BrokenNes2.Plugin\Resources");
        return (File.ReadAllBytes(Path.Combine(res, "musictest.nes")), File.ReadAllText(Path.Combine(res, "musictest.txt")), File.ReadAllText(Path.Combine(res, "musictest.mlb")));
    }
}
