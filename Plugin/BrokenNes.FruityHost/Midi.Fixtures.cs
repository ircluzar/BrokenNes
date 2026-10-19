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

    /// <summary>The user's VRUN tree, or null (then every check that needs it is reported NOT RUN, never passed): BROKENNES_VRUN, else VRUN_ROOT (set by the VRUN
    /// workspace's nesdev-env.sh: the live repo, as a Git Bash path or a Windows one), else the live workspace beside this repository (PROJECTS\VRUN_Nes_Dev, the layout
    /// WORKSPACE.md describes), else BrokenDev's restored copy. Since round 9 the tree must have VRUN's pitch-bend engine (merged into the live VRUN 2026-10-18); an older
    /// copy (BrokenDev's restore of the 2026-10-12 archive predates it) fails the converter and template checks, by design.</summary>
    public static string? Vrun()
    {
        static string? Win(string? p) => string.IsNullOrEmpty(p) ? null : p.Length > 3 && p[0] == '/' && char.IsLetter(p[1]) && p[2] == '/' ? $"{char.ToUpperInvariant(p[1])}:{p[2..].Replace('/', '\\')}" : p;
        static string? Live()
        {
            try
            {
                string s = Path.Combine(Path.GetDirectoryName(RepoRoot())!, @"VRUN_Nes_Dev\sys0\simulations\VRUN");
                return File.Exists(Path.Combine(s, "build.sh")) ? s : null;
            }
            catch (InvalidOperationException) { return null; }
        }
        string dir = Win(Environment.GetEnvironmentVariable("BROKENNES_VRUN")) ?? Win(Environment.GetEnvironmentVariable("VRUN_ROOT")) ?? Live()
            ?? Path.Combine(Environment.GetEnvironmentVariable("BROKENDEV_HOME") is { Length: > 0 } h ? h : @"P:\BrokenDev\env", @"vrun_nes_dev\sys0\simulations\VRUN");
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

    // ---- pitch bends (round 9) ----

    private static (int, byte[]) Bend(int tick, int ch, int value)
    {
        int v = Math.Clamp(value + 8192, 0, 16383);
        return (tick, [(byte)(0xE0 | ch), (byte)(v & 0x7F), (byte)(v >> 7 & 0x7F)]);
    }

    /// <summary>RPN 0 (the bend range) set to <paramref name="semitones"/>, then the RPN deselected, at <paramref name="tick"/>.</summary>
    private static IEnumerable<(int, byte[])> BendRange(int tick, int ch, int semitones) =>
    [
        (tick, [(byte)(0xB0 | ch), 101, 0]), (tick, [(byte)(0xB0 | ch), 100, 0]), (tick, [(byte)(0xB0 | ch), 6, (byte)semitones]),
        (tick, [(byte)(0xB0 | ch), 101, 127]), (tick, [(byte)(0xB0 | ch), 100, 127]),
    ];

    /// <summary>A linear bend from <paramref name="a"/> to <paramref name="b"/> between two ticks, one event every <paramref name="step"/> ticks.</summary>
    private static IEnumerable<(int, byte[])> Ramp(int ch, int from, int to, int a, int b, int step = 4)
    {
        for (int t = from; t <= to; t += step) yield return Bend(t, ch, a + (int)Math.Round((double)(b - a) * (t - from) / Math.Max(1, to - from)));
        yield return Bend(to, ch, b);
    }

    /// <summary>Songs with pitch bends, as FL exports them (bend events a few ticks apart, RPN 0 ranges): each has a lead on MIDI channel 1 (square 1), a bass on 2 (the triangle:
    /// the lowest), a second voice on 3 (square 2) and drums on 10 (the noise), 96 ticks a beat at 120 BPM, and bends where its name says. The VRUN converter's own checks
    /// (engine/audio/converter/tests) hold the same shapes against a model of the decoders; here they go through the plugin's whole path into the ROM.</summary>
    public static List<MidiSong> BendSongs()
    {
        const int tpq = 96;
        var list = new List<MidiSong>();
        byte[] Song(int beats, IEnumerable<(int, byte[])> lead, IEnumerable<(int, byte[])> bass, IEnumerable<(int, byte[])> second, IEnumerable<(int, byte[])>? drumBends = null)
        {
            int end = beats * tpq;
            var drums = Enumerable.Range(0, beats * 2).SelectMany(i => Notes(9, [(i % 4 == 0 ? 36 : i % 2 == 0 ? 38 : 42, 8)], 110, i * tpq / 2)).Concat(drumBends ?? []);
            return File_(1, tpq, Track([Tempo(0, 500000)], end), Track(lead, end), Track(bass, end), Track(second, end), Track(drums, end));
        }
        IEnumerable<(int, byte[])> Plain(int ch, int beats, int note) => Notes(ch, Enumerable.Range(0, beats).Select(i => (note + i % 3 * 2, tpq)), 90);

        // FL slide notes as exported: each lead note glides into the next over a quarter beat (RPN 0 = 12), the next note starts where the glide ends with the bend reset
        {
            var lead = new List<(int, byte[])>(BendRange(0, 0, 12));
            int[] seq = [72, 79, 74, 69, 76, 72];
            int t0 = 0;
            for (int i = 0; i < seq.Length - 1; i++)
            {
                int len = tpq + tpq / 4;
                lead.Add((t0, [0x90, (byte)seq[i], 100]));
                lead.AddRange(Ramp(0, t0 + tpq * 3 / 4, t0 + len - 1, 0, Math.Clamp((int)Math.Round((seq[i + 1] - seq[i]) / 12.0 * 8192), -8192, 8191), 3));
                lead.Add((t0 + len, [0x80, (byte)seq[i], 0]));
                lead.Add(Bend(t0 + len, 0, 0));
                t0 += len;
            }
            lead.AddRange(Notes(0, [(seq[^1], tpq)], 100, t0));
            list.Add(new("bendglides", Song(8, lead, Plain(1, 8, 36), Plain(2, 8, 60)), "written here: FL glides into the next note (RPN 12), square 1"));
        }
        // octave slides on the second voice (RPN 0 = 12): up an octave over two beats, down two octaves in one, back in one; then a note that starts bent
        {
            var second = new List<(int, byte[])>(BendRange(0, 2, 12));
            second.AddRange(Notes(2, [(67, 5 * tpq)], 100));
            second.AddRange(Ramp(2, tpq / 2, 2 * tpq, 0, 8191));
            second.AddRange(Ramp(2, 2 * tpq, 3 * tpq, 8191, -8192));
            second.AddRange(Ramp(2, 3 * tpq, 4 * tpq, -8192, 0));
            second.Add(Bend(5 * tpq + tpq / 2, 2, 2048));
            second.AddRange(Notes(2, [(64, 2 * tpq)], 100, 5 * tpq + tpq / 2));
            second.Add(Bend(8 * tpq - 2, 2, 0));
            list.Add(new("bendoctaves", Song(8, Plain(0, 8, 76), Plain(1, 8, 36), second), "written here: octave slides up and down and a bent note (RPN 12), square 2"));
        }
        // a vibrato on the bass (the triangle: 5.5 Hz, +-0.5 semitone at the default range of 2), then a held bend
        {
            var bass = new List<(int, byte[])>(Notes(1, [(45, 4 * tpq - 8)], 100));
            for (int t = 0; t < 4 * tpq - 8; t += 2) bass.Add(Bend(t, 1, (int)Math.Round(2048 * Math.Sin(2 * Math.PI * 5.5 * t / (double)tpq * 0.5))));
            bass.Add(Bend(4 * tpq - 8, 1, -4096));
            bass.AddRange(Notes(1, [(40, 4 * tpq - 8)], 100, 4 * tpq));
            bass.Add(Bend(6 * tpq, 1, 0));
            list.Add(new("bendvibrato", Song(8, Plain(0, 8, 76), bass, Plain(2, 8, 60)), "written here: vibrato and a held bend on the triangle (default range)"));
        }
        // notes that start bent, and one bend held across several notes (MIDI keeps it), default range
        {
            var lead = new List<(int, byte[])>();
            int[] bends = [-8192, 4096, 0, 8191, -4096, 2048];
            int[] notes = [72, 74, 76, 77, 79, 77];
            for (int i = 0; i < 6; i++) { lead.Add(Bend(i * tpq, 0, bends[i])); lead.AddRange(Notes(0, [(notes[i], tpq - 8)], 100, i * tpq)); }
            var second = new List<(int, byte[])> { Bend(tpq, 2, 4096) };
            for (int i = 0; i < 5; i++) second.AddRange(Notes(2, [(62 + i, tpq - 12)], 90, tpq + i * tpq));
            second.Add(Bend(7 * tpq, 2, 0));
            list.Add(new("bendprebent", Song(8, lead, Plain(1, 8, 36), second), "written here: notes that start bent (square 1), a bend held across notes (square 2)"));
        }
        // all at once, and the drums bend too (the noise has no pitch: counted, not played)
        {
            var lead = new List<(int, byte[])>(BendRange(0, 0, 12));
            lead.AddRange(Notes(0, [(72, 2 * tpq), (76, 2 * tpq), (79, 4 * tpq)], 100));
            lead.AddRange(Ramp(0, tpq, 2 * tpq - 1, 0, 2731, 3));
            lead.Add(Bend(2 * tpq, 0, 0));
            lead.AddRange(Ramp(0, 5 * tpq, 7 * tpq, 0, -4096, 4));
            var bass = new List<(int, byte[])>(Notes(1, [(40, 8 * tpq - 8)], 100));
            for (int t = 0; t < 8 * tpq - 8; t += 3) bass.Add(Bend(t, 1, (int)Math.Round(1500 * Math.Sin(2 * Math.PI * 4.0 * t / (double)tpq * 0.5))));
            var second = new List<(int, byte[])>(BendRange(0, 2, 2));
            second.AddRange(Notes(2, [(64, 4 * tpq), (67, 4 * tpq)], 90));
            second.AddRange(Ramp(2, 2 * tpq, 3 * tpq, 0, 8191));
            second.AddRange(Ramp(2, 3 * tpq, 4 * tpq, 8191, 0));
            list.Add(new("bendall", Song(8, lead, bass, second, Ramp(9, 0, 4 * tpq, 0, 8191, 8)), "written here: bends on square 1, square 2, the triangle and the drums"));
        }
        // sixteenth notes under a fast vibrato: the channel's one opcode per frame is mostly taken by the notes, so bend frames are thinned (counted), never the notes
        {
            var lead = new List<(int, byte[])>();
            for (int i = 0; i < 32; i++) lead.AddRange(Notes(0, [(72 + i % 4 * 3, tpq / 4 - 6)], 100, i * tpq / 4));
            for (int t = 0; t < 8 * tpq; t += 2) lead.Add(Bend(t, 0, (int)Math.Round(4096 * Math.Sin(2 * Math.PI * 6.0 * t / (double)tpq * 0.5))));
            list.Add(new("benddense", Song(8, lead, Plain(1, 8, 36), Plain(2, 8, 60)), "written here: a vibrato under sixteenth notes: bend frames thinned"));
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
