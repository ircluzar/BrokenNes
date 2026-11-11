// The .flp music pipeline, phase 1 (docs/projects/flp-music-pipeline.md): `FruityHost flp-test [--songs DIR] [--out DIR] [--only id,id] [--no-mutants]`. No plugin DLL involved.
//   identity    the C# reader (Music/Flp/FlpSong.cs) writes the same canonical JSON bytes as the JS one (BrokenDev/tools/music/flp/flpsong.mjs, run with node) on every .flp of the songs folder
//   synthetic   a project written here (the FLhd/FLdt events): what the reader reads (tempo, playlist placement, notes, slide flag, BrokenNes2 parameters, Auto channel, sections) and what the
//               VRUN NES back end makes of it (channels, keys, levels, bends, the caveats for every part the game cannot play)
//   fl-pitch    per frame, the MIDI file the back end wrote plays the pitch FL plays (worked out here from the song model alone, apart from the back end's code): note, key, bend
//   engine      round 7's engine check on each song: the ROM the plugin builds plays the converter's streams frame for frame (three negative controls), and its bends follow the MIDI
//   duty        each square's pulse duty: the back end writes the instances' Duty as CC 14, the converter starts the square's stream with the duty opcode 0xD3 when it is
//               not 50% (and only then), and the sound-test ROM's $4000/$4004 writes carry that duty in bits 6-7 on every sounding write (read from the machine's APU write tap)
//   rom         each song as a sound-test ROM (with --out: written there, with its facts), sha256 printed
// The songs folder defaults to the user's Reverse Coaster folder (read only), else the repository's copy; NOT RUN when neither has a .flp. Deliberate bugs in the back end (FlpMutants) must each be caught.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BrokenNes2.Music;
using BrokenNes2.Music.Flp;
using Vrun.Audio;

namespace BrokenNes.FruityHost;

internal static class FlpChecks
{
    public sealed record Check(string Id, string What, Action<ImodelReport> Run);

    /// <summary>The user's Reverse Coaster folder when it is there (read only), else the copy in the repository (BrokenDev/tools/music/flp/songs/ReverseCoaster, SHA256SUMS beside it).</summary>
    internal static string SongsDir = Directory.Exists(@"C:\Users\philt\Desktop\Reverse Coaster") ? @"C:\Users\philt\Desktop\Reverse Coaster"
        : Path.Combine(MidiFixtures.RepoRoot(), @"BrokenDev\tools\music\flp\songs\ReverseCoaster");
    internal static string? OutDir;

    public static readonly Check[] All =
    [
        new("identity", "the C# reader writes the JS reader's canonical JSON byte for byte on every song", Identity),
        new("synthetic", "a project written here: the reader's model and the VRUN NES back end's file and caveats", Synthetic),
        new("fl-pitch", "the MIDI the back end writes plays FL's pitch (from the model alone), frame by frame, on every NES channel", FlPitch),
        new("engine", "the sound-test ROM of each song plays the converter's streams frame for frame and its bends follow the MIDI", Engine),
        new("bend-signs", "hand-written streams: positive / negative, sub-unit / whole slides crossing the period's high byte, on square 1, square 2 and the triangle, play exactly in the template", BendSigns),
        new("duty", "each square's Duty reaches the MIDI (CC 14), the streams (0xD3 when not 50%) and the ROM's $4000/$4004 writes", Duty),
        new("rom", "each song becomes a sound-test ROM (round 7's template), facts and caveats said", Rom),
        .. FlpVocChecks.All,     // Vaults GB / Vaults GG / CTW SMS (Flp.Checks.Voc.cs)
        new("sv-synthetic", "Super VRUN back end (SNES, WideMess, MD, DOS, 95): programs, noise keys, levels, pan, pitch, per-engine fence lines", SvChecks.Synthetic),
        new("sv-fence", "Super VRUN fence: each limit broken on purpose is reported OVER (voices, noise, range, instances, instruments, empty song)", SvChecks.Fence),
        new("sv-preset-snes", "presets: every SNES fixture instrument (clean, corrupted, tuned) from a PRS1 section written byte by byte becomes the song's own sample: BRR decode, loop, C5, the plugin's render alike", SvChecks.PresetSnes),
        new("sv-preset-engines", "presets per engine: Genesis FM / DAC, Game Boy, SNES rendered or exact; SNES Ok, MD DAC Note, DOS / 95 OVER; damaged and NES presets OVER; sound RAM; pitch ceiling", SvChecks.PresetEngines),
        new("preset-others", "presets on Vaults GB (a wave preset's RAM is the wave table, a pulse preset's duty), Vaults GG and VRUN NES (OVER, never silent)", SvChecks.PresetOthers),
        new("fl-volume", "an FL channel volume knob off its default (78%) gives the note line on every back end (VRUN NES, CTW SMS, Vaults GB / GG, Super VRUN), the default gives none; overworld's real knobs", FlVolume),
        new("sv-songs", "every song on every Super VRUN engine: MIDI notes = the model's sounding notes, fence printed (with --out: written)", SvChecks.Songs),
    ];

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    internal static List<string> Songs() => Directory.Exists(SongsDir) ? Directory.GetFiles(SongsDir, "*.flp").OrderBy(f => f, StringComparer.Ordinal).ToList() : [];

    private static bool NoSongs(ImodelReport r)
    {
        if (Songs().Count > 0) return false;
        r.Warn($"NOT RUN: no .flp in {SongsDir}");
        return true;
    }

    // ---- fl-volume ----

    private static void FlVolume(ImodelReport r)
    {
        const string Words = "FL channel volume isn't carried; use the plugin's Volume";
        var model = FlpSong.Read(SyntheticProject());
        var lead = model.Instruments.First(i => i.Name == "Lead");
        foreach (var i in model.Instruments) i.Levels = [6400, FlpFence.FlDefaultVolume, 0];
        lead.Levels = [6400, 7120, 0];
        IEnumerable<(string Engine, IEnumerable<string> Lines)> All(FlpSongModel m) =>
        [
            ("VRUN NES", VrunNesBackend.Build(m, "v").Caveats),
            ("CTW SMS", CtwSmsBackend.Build(m, "v").Fence.Select(l => l.Text)),
            ("Vaults GB", VocGbBackend.Build(m, "v").Fence.Select(l => l.Text)),
            ("Vaults GG", VocGgBackend.Build(m, "v").Fence.Select(l => l.Text)),
            .. Enum.GetValues<SvEngine>().Select(e => ("Super VRUN " + e, SuperVrunBackend.Build(m, "v", e).Fence.Lines.Select(l => l.Text))),
        ];
        foreach (var (engine, lines) in All(model))
        {
            var hit = lines.Where(l => l.Contains(Words)).ToList();
            r.Expect(hit.Count == 1 && hit[0].Contains("\"Lead\"") && hit[0].Contains("56%"), $"{engine}: channel Lead's FL volume 56% gives {hit.Count} note lines: {string.Join(" | ", hit)}");
        }
        lead.Levels = [6400, FlpFence.FlDefaultVolume, 0];
        foreach (var (engine, lines) in All(model))
            r.Expect(!lines.Any(l => l.Contains(Words)), $"{engine}: every FL volume at its default, yet a volume note line");
        // the user's overworld: two knobs moved (channels 0 and 3, measured from the file's event 219)
        var ow = Songs().FirstOrDefault(f => Path.GetFileName(f) == "overworld.flp");
        if (ow == null) { r.Warn("overworld.flp not found: the real-song half is not run"); return; }
        var real = FlpSong.Read(File.ReadAllBytes(ow));
        var want = real.Instruments.Where(i => i.Bn2 != null && i.Notes.Count > 0 && i.Levels is { Length: >= 2 } l && l[1] != FlpFence.FlDefaultVolume).Select(i => i.Rack).ToList();
        var got = FlpFence.ChannelVolume(real).ToList();
        var moved = real.Instruments.Where(i => i.Levels is { Length: >= 2 } l && l[1] != FlpFence.FlDefaultVolume).Select(i => $"{i.Rack} \"{i.Name}\" {(i.Bn2 == null ? i.Plugin : "BrokenNes2")} {i.Notes.Count} notes").ToList();
        r.Expect(moved.Count == 2 && got.Count == want.Count, $"overworld: {moved.Count} moved knobs (2 in the file), {want.Count} BrokenNes2 channels with a moved FL volume, {got.Count} note lines");
        r.Note($"overworld: knobs moved on {string.Join(", ", moved)}; note lines: {(got.Count == 0 ? "none (no BrokenNes2 channel with notes among them)" : string.Join(" | ", got.Select(l => l.Text)))}");
    }

    // ---- identity ----

    internal static string? JsJson(string flp, out string? problem)
    {
        problem = null;
        string cli = Path.Combine(MidiFixtures.RepoRoot(), @"BrokenDev\tools\music\flp\flpsong-cli.mjs");
        var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
        psi.ArgumentList.Add(cli); psi.ArgumentList.Add(flp);
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            if (p.ExitCode != 0) { problem = $"node exit {p.ExitCode}: {e.Result.Trim()}"; return null; }
            return o.Result;
        }
        catch (Exception x) { problem = "node could not be run: " + x.Message; return null; }
    }

    public static void Identity(ImodelReport r)
    {
        if (NoSongs(r)) return;
        foreach (var f in Songs())
        {
            var cs = FlpSong.Json(FlpSong.Read(File.ReadAllBytes(f)));
            var js = JsJson(f, out var why);
            if (js == null) { r.Warn($"NOT RUN: {Path.GetFileName(f)}: {why}"); continue; }
            bool same = js == cs;
            int at = 0; while (at < Math.Min(js.Length, cs.Length) && js[at] == cs[at]) at++;
            r.Expect(same, $"{Path.GetFileName(f)}: the C# model differs from the JS one at character {at}: JS ...{Mid(js, at)}... C# ...{Mid(cs, at)}...");
            r.Note($"{Path.GetFileName(f),-18} model {Encoding.UTF8.GetByteCount(cs),7} bytes sha256 {Sha(Encoding.UTF8.GetBytes(cs))[..16]} {(same ? "= JS" : "DIFFERS")}");
        }
    }
    private static string Mid(string s, int at) => s.Substring(Math.Max(0, at - 30), Math.Min(60, s.Length - Math.Max(0, at - 30)));

    // ---- synthetic ----

    private static byte[] Ev(int id, int v) => id < 64 ? [(byte)id, (byte)v] : id < 128 ? [(byte)id, (byte)v, (byte)(v >> 8)] : [(byte)id, (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
    private static byte[] Text(int id, byte[] d)
    {
        var o = new List<byte> { (byte)id };
        int n = d.Length;
        do { o.Add((byte)((n & 127) | (n > 127 ? 128 : 0))); n >>= 7; } while (n > 0);
        o.AddRange(d);
        return o.ToArray();
    }
    private static byte[] U16(string s) => Encoding.Unicode.GetBytes(s + "\0");
    internal static byte[] Bn2State(int core = 0, int volume = 1000, int pan = 0, int coarse = 0, int fine = 0, int duty = 2, int channel = 0, int noiseMode = 0, int mode = 0, int console = 0, int place = 0, byte[]? section = null, string sectionTag = "XYZ1")
    {
        int[] p = [core, volume, pan, coarse, fine, duty, channel, noiseMode, mode, 0, console, 0, 0, 0];
        var b = new List<byte>();
        b.AddRange("BFNP"u8.ToArray()); b.AddRange(BitConverter.GetBytes(2)); b.AddRange(BitConverter.GetBytes(p.Length));
        foreach (int v in p) b.AddRange(BitConverter.GetBytes(v));
        b.AddRange(BitConverter.GetBytes(0)); b.AddRange("EMU2"u8.ToArray()); b.AddRange(BitConverter.GetBytes(1)); b.AddRange(BitConverter.GetBytes(place));
        if (section != null) { b.AddRange(System.Text.Encoding.Latin1.GetBytes(sectionTag)); b.AddRange(BitConverter.GetBytes(section.Length)); b.AddRange(section); }
        return b.ToArray();
    }
    internal sealed record SNote(int Pos, int Len, int Key, int Ch, bool Slide = false, int Fine = 120, int Vel = 100);
    private static byte[] Notes(IEnumerable<SNote> list)
    {
        var o = new List<byte>();
        foreach (var n in list)
        {
            var b = new byte[24];
            BitConverter.GetBytes((uint)n.Pos).CopyTo(b, 0); BitConverter.GetBytes((ushort)(0x4000 | (n.Slide ? 8 : 0))).CopyTo(b, 4); BitConverter.GetBytes((ushort)n.Ch).CopyTo(b, 6);
            BitConverter.GetBytes((uint)n.Len).CopyTo(b, 8); BitConverter.GetBytes((ushort)n.Key).CopyTo(b, 12); b[16] = (byte)n.Fine; b[18] = 64; b[20] = 64; b[21] = (byte)n.Vel; b[22] = 128; b[23] = 128;
            o.AddRange(b);
        }
        return o.ToArray();
    }
    internal static byte[] Project(int tempo, (string Name, string Plugin, byte[]? State)[] channels, (int Pattern, SNote[] Notes)[] patterns, (int Pos, int Pattern, int Len)[] items)
    {
        var e = new List<byte>();
        e.AddRange(Text(199, Encoding.Latin1.GetBytes("26.1.0.5530\0"))); e.AddRange(Ev(28, 1)); e.AddRange(new byte[] { 0xAC, 1, 1, 0 }); e.AddRange(Text(192, U16("FL Studio"))); e.AddRange(Ev(156, tempo * 1000));
        foreach (var (n, list) in patterns) { e.AddRange(Ev(65, n)); e.AddRange(Text(224, Notes(list))); }
        for (int i = 0; i < channels.Length; i++)
        {
            var c = channels[i];
            e.AddRange(Ev(64, i)); e.AddRange(Ev(21, c.State != null ? 2 : 0)); e.AddRange(Text(201, U16(c.Plugin))); e.AddRange(Text(203, U16(c.Name)));
            if (c.State != null) e.AddRange(Text(213, c.State));
        }
        var pl = new byte[88 * items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            int o = 88 * i;
            BitConverter.GetBytes((uint)items[i].Pos).CopyTo(pl, o); BitConverter.GetBytes((ushort)20480).CopyTo(pl, o + 4); BitConverter.GetBytes((ushort)(20480 + items[i].Pattern)).CopyTo(pl, o + 6);
            BitConverter.GetBytes((uint)items[i].Len).CopyTo(pl, o + 8); BitConverter.GetBytes((ushort)499).CopyTo(pl, o + 12); BitConverter.GetBytes((ushort)64).CopyTo(pl, o + 18);
            BitConverter.GetBytes(-1).CopyTo(pl, o + 24); BitConverter.GetBytes(-1).CopyTo(pl, o + 28);
        }
        e.AddRange(Ev(99, 0)); e.AddRange(Text(241, U16("Arrangement"))); e.AddRange(Text(233, pl));
        var head = new List<byte>(); head.AddRange("FLhd"u8.ToArray()); head.AddRange(BitConverter.GetBytes(6)); head.AddRange(new byte[] { 0, 0, (byte)channels.Length, 0, 96, 0 });
        head.AddRange("FLdt"u8.ToArray()); head.AddRange(BitConverter.GetBytes(e.Count)); head.AddRange(e);
        return head.ToArray();
    }

    /// <summary>The synthetic song: a lead on pulse 1 (Auto, 25% duty, two overlapping notes, +50 cents), a bass on the triangle with a two-octave slide, a noise hit pair, a ROM-mode instance, a
    /// Game Boy instance and a sampler; one pattern placed twice on the playlist (the second clip cut short).</summary>
    internal static byte[] SyntheticProject() => Project(150,
        [("Kick", "", null), ("Lead", "BrokenNes2", Bn2State(duty: 1, place: 0, section: [1, 2, 3])), ("Bass", "BrokenNes2", Bn2State(place: 2, coarse: -12)),
         ("Hat", "BrokenNes2", Bn2State(channel: 4, volume: 500)), ("Mix", "BrokenNes2", Bn2State(channel: 5)), ("ROM", "BrokenNes2", Bn2State(mode: 1, place: 1)), ("GB", "BrokenNes2", Bn2State(core: 30, console: 1))],
        [(1, [new(0, 48, 72, 1), new(24, 48, 76, 1), new(96, 48, 60, 1, Fine: 170), new(0, 80, 60, 2), new(0, 40, 36, 2, Slide: true), new(0, 12, 60, 3), new(48, 12, 66, 3, Vel: 128),
              new(0, 24, 60, 0), new(0, 24, 60, 4), new(0, 24, 60, 5), new(0, 24, 60, 6)])],
        [(0, 1, 192), (192, 1, 72)]);

    public static void Synthetic(ImodelReport r)
    {
        var bytes = SyntheticProject();
        var s = FlpSong.Read(bytes);
        r.Expect(s.Ppq == 96 && s.TempoMilli == 150000 && s.FlVersion == "26.1.0.5530" && s.TimelineMode == "playlist" && s.End == 264, $"project facts: ppq {s.Ppq}, tempo {s.TempoMilli}, FL {s.FlVersion}, {s.TimelineMode}, end {s.End}");
        r.Expect(s.Instruments.Count == 7, $"{s.Instruments.Count} instruments, 7 expected");
        var lead = s.Instruments[1];
        r.Expect(lead.Bn2 is { Channel: 0, Mode: "direct", Console: 0 } && lead.Bn2.Param(5) == 1 && lead.Bn2.Sections.Count == 1 && lead.Bn2.Sections[0].Tag == "XYZ1", "the lead's state: Auto channel = where it sat (pulse 1), Direct, duty 25%, one section XYZ1");
        // the playlist: the pattern twice; the second clip is 72 ticks: notes starting at 0 and 24 are in it, the one at 96 is not; notes are cut at the clip's end
        r.Expect(lead.Notes.Count == 5, $"the lead has {lead.Notes.Count} notes on the timeline, 5 expected (3 + 2 in the cut clip)");
        r.Expect(lead.Notes.Any(n => n.Start == 216 && n.End == 264 && n.Key == 76), "the second clip's note at 24 is not cut at the clip's end (192 + 72 = 264)");
        r.Expect(s.Instruments[2].Notes.Count(n => n.Slide) == 2, "the bass's slide notes are not flagged");
        r.Expect(s.Instruments[5].Bn2!.Mode == "rom" && s.Instruments[6].Bn2!.ConsoleName == "GB", "ROM mode / Game Boy console not read");
        r.Expect(FlpSong.Json(s) == FlpSong.Json(FlpSong.Read(bytes)), "the model's text is not stable");

        var v = VrunNesBackend.Build(s, "synthetic");
        var midi = MidiFile.Parse(v.Midi);
        var on = new List<(int Tick, int Ch, int Key, int Vel)>();
        foreach (var t in midi.Tracks) { int tick = 0; foreach (var e in t) { tick += e.DeltaTime; if (e.Status < 0xF0 && e.Command == MidiFile.NoteOn && e.Data2 > 0) on.Add((tick, e.Channel, e.Data1, e.Data2)); } }
        r.Expect(midi.TicksPerQuarter == 960, $"PPQ {midi.TicksPerQuarter}");
        var sq = on.Where(x => x.Ch == 0).OrderBy(x => x.Tick).ToList();
        // pulse 1, first clip: 72 at 0, 76 at 24 (newest wins), 60(+50 cents) at 96 -> key 61 at -50 or 60 at +50 (rounded half away: 60.5 -> 61)
        r.Expect(sq.Count >= 3 && sq[0] == (0, 0, 72, 96) && sq[1] == (240, 0, 76, 96) && sq[2].Tick == 960 && sq[2].Key == 61, $"pulse 1 notes: {string.Join(" ", sq.Take(4))}");
        // level: velocity 100 x Volume 1 -> round(15 x 100/128) = 12 -> velocity 96?   15*100/128 = 11.72 -> 12 -> 96
        r.Expect(sq.All(x => x.Vel == 96), $"pulse 1 velocities {string.Join(",", sq.Select(x => x.Vel).Distinct())}, 96 expected (level 12 x 8)");
        var tri = on.Where(x => x.Ch == 2).ToList();
        r.Expect(tri.Count == 2 && tri[0].Key == 48, $"triangle: {string.Join(" ", tri)} (key 60 with Coarse -12 = 48)");
        r.Expect(on.Count(x => x.Ch == 9) == 4 && on.Where(x => x.Ch == 9).All(x => x.Key >> 4 == 3), $"noise hits: {string.Join(" ", on.Where(x => x.Ch == 9))} (4 expected, keys $30-$3F = the period index)");
        r.Expect(v.Caveats.Any(c => c.Contains("softer than full level")), "no caveat for the soft noise (Volume 0.5)");
        r.Expect(!v.Caveats.Any(c => c.Contains("Duty")), "a caveat for the lead's 25% duty (VRUN plays it now)");
        r.Expect(v.Parts.FirstOrDefault(p => p.NesChannel == 0)?.Duty == 1, "the lead's part does not carry its 25% duty");
        r.Expect(v.Caveats.Any(c => c.Contains("ROM mode")), "no caveat for the ROM-mode instance");
        r.Expect(v.Caveats.Any(c => c.Contains("GB instrument")), "no caveat for the Game Boy instance");
        r.Expect(v.Caveats.Any(c => c.Contains("is not BrokenNes2")), "no caveat for the sampler");
        r.Expect(v.Caveats.Any(c => c.Contains("Mix/DMC")), "no caveat for the Mix-channel instance");
        // the bass's slide: bends on channel 3 going down
        int bends = 0, minBend = 0;
        foreach (var t in midi.Tracks) foreach (var e in t) if (e.Status < 0xF0 && e.Command == MidiFile.PitchBend && e.Channel == 2) { bends++; minBend = Math.Min(minBend, (e.Data2 << 7 | e.Data1) - 8192); }
        r.Expect(bends > 10 && minBend < -8000, $"the triangle's two-octave slide: {bends} bends, lowest {minBend}");
        r.Note($"synthetic: {on.Count} notes, {bends} triangle bends, caveats: {string.Join(" | ", v.Caveats)}");
    }

    // ---- FL's pitch, frame by frame ----

    /// <summary>What FL plays on a NES channel at an FL tick, from the model alone: the newest held note of the instances on that channel (slides glide it as FL does), its level; null = silence.</summary>
    internal static (double Cents, int Level)? FlAt(FlpSongModel s, int nes, double tick)
    {
        (FlpNote N, double Base, double Volume, int Order, FlpInstrument I)? own = null;
        int order = 0;
        foreach (var ins in s.Instruments)
        {
            if (ins.Bn2 is not { Mode: "direct", Console: 0 } b || b.Channel != nes) continue;
            double tune = b.Param(3) * 100 + b.Param(4), vol = Math.Clamp(b.Param(1, 1000) / 1000.0, 0, 1);
            foreach (var n in ins.Notes)
            {
                int o = order++;
                if (n.Slide || n.End <= n.Start || !(n.Start <= tick && tick < n.End)) continue;
                if (own == null || n.Start > own.Value.N.Start || n.Start == own.Value.N.Start && o > own.Value.Order) own = (n, n.Key * 100 + n.Fine - 120 + tune, vol, o, ins);
            }
        }
        if (own is not { } w) return null;
        double cur = w.Base;
        var b2 = w.I.Bn2!;
        double tune2 = b2.Param(3) * 100 + b2.Param(4);
        foreach (var g in w.I.Notes.Where(g => g.Slide && w.N.Start <= g.Start && g.Start < w.N.End).OrderBy(g => g.Start))
        {
            double gs = g.Start - VrunNesBackend.SlideEarly, gl = g.End - g.Start + VrunNesBackend.SlideExtra, to = g.Key * 100 + g.Fine - 120 + tune2;
            if (tick <= gs) break;
            if (tick >= gs + gl) cur = to; else { cur += (to - cur) * (tick - gs) / gl; break; }
        }
        int level = Math.Clamp((int)Math.Round(15 * (w.N.Velocity / 128.0) * w.Volume, MidpointRounding.AwayFromZero), 0, 15);
        return level == 0 ? null : (cur, level);
    }

    public static void FlPitch(ImodelReport r)
    {
        var all = new List<(string Name, byte[] Flp)> { ("synthetic", SyntheticProject()) };
        foreach (var f in Songs()) all.Add((Path.GetFileNameWithoutExtension(f), File.ReadAllBytes(f)));
        if (Songs().Count == 0) r.Warn($"NOT RUN: the user's songs (no .flp in {SongsDir}); the synthetic one was checked");
        foreach (var (name, flp) in all)
        {
            var s = FlpSong.Read(flp);
            var v = VrunNesBackend.Build(s, name);
            int frames = (int)Math.Ceiling(v.Seconds * Midi2Nes.Fps);
            double ticksPerFrame = s.Ppq * s.Tempo / 60.0 / Midi2Nes.Fps;
            var parts = new List<string>();
            foreach (var p in v.Parts)
            {
                int mc = p.NesChannel == 3 ? 9 : p.NesChannel;
                var ideal = MidiChecks.IdealPitch(v.Midi, mc, frames);
                int judged = 0, off = 0, edge = 0; double worst = 0; string first = "";
                for (int k = 0; k < frames; k++)
                {
                    // frame k holds every event with floor(tick x frames-per-tick) <= k (IdealPitch), i.e. up to the frame's END: FL's state just before the next frame
                    double tick = (k + 1) * ticksPerFrame - 1e-6;
                    var fl = FlAt(s, p.NesChannel, tick);
                    var near = ideal.NoteFrames.Contains(k) || ideal.NoteFrames.Contains(k + 1) || ideal.NoteFrames.Contains(k - 1);
                    if (near) { edge++; continue; }
                    judged++;
                    int mn = ideal.Note[k];
                    bool bad;
                    double err = 0;
                    if (fl == null) bad = mn >= 0;
                    else if (mn < 0) bad = true;
                    else if (p.NesChannel == 3) bad = (mn & 15) != VrunNesBackend.NoiseIndex(fl.Value.Cents - 6000);
                    else { err = Math.Abs(mn * 100 + ideal.Semis[k] * 100 - fl.Value.Cents); bad = err > 2.0 + SlopeAt(s, p.NesChannel, tick, ticksPerFrame); }
                    worst = Math.Max(worst, bad ? err : 0);
                    if (bad) { off++; if (first.Length == 0) first = $"frame {k} (tick {tick:0.0}): FL {(fl == null ? "silent" : $"{fl.Value.Cents:0.0} cents")}, MIDI note {mn} bend {ideal.Semis[k]:0.00}"; }
                }
                r.Expect(off == 0, $"{name} {VrunNesBackend.NesNames[p.NesChannel]}: {off} of {judged} frames the MIDI does not play FL's pitch; first {first}");
                parts.Add($"{VrunNesBackend.NesNames[p.NesChannel]} {judged - off}/{judged}");
            }
            r.Note($"{name,-14} {frames} frames: " + string.Join(", ", parts) + " frames on FL's pitch (frames next to a note start/stop are timing, the engine check's)");
        }
    }

    /// <summary>How far FL's pitch moves in one FL tick around there (cents): a moving bend may be that far off.</summary>
    private static double SlopeAt(FlpSongModel s, int nes, double tick, double tpf)
    {
        // the file holds a bend per FL tick: between two of them FL moves at most one tick's worth
        var a = FlAt(s, nes, tick - 1); var b = FlAt(s, nes, tick); var c = FlAt(s, nes, tick + 1);
        double m = 0;
        if (a != null && b != null) m = Math.Max(m, Math.Abs(b.Value.Cents - a.Value.Cents));
        if (c != null && b != null) m = Math.Max(m, Math.Abs(c.Value.Cents - b.Value.Cents));
        return m;
    }

    // ---- the ROM ----

    internal static List<(string Name, VrunNesSong Song, SongBuild Build, byte[] Rom)> Roms(ImodelReport r)
    {
        var t = MidiChecks.BundledTemplate();
        var list = new List<(string, VrunNesSong, SongBuild, byte[])>();
        foreach (var f in Songs())
        {
            string name = Path.GetFileNameWithoutExtension(f);
            var v = VrunNesBackend.Build(FlpSong.Read(File.ReadAllBytes(f)), name);
            var b = SongConvert.Convert(v.Midi, out var why);
            r.Expect(b != null, $"{name}: the converter refused the file: {why}");
            if (b == null) continue;
            list.Add((name, v, b, t.Patch(b.Channels, b.Facts.LoopFrames, name)));
        }
        return list;
    }

    public static void Engine(ImodelReport r)
    {
        if (NoSongs(r)) return;
        var t = MidiChecks.BundledTemplate();
        var (_, _, mlb) = MidiFixtures.Bundled();
        var songs = Songs().Select(f => { string n = Path.GetFileNameWithoutExtension(f); return new MidiSong(n, VrunNesBackend.Build(FlpSong.Read(File.ReadAllBytes(f)), n).Midi, f); }).ToList();
        for (int i = 0; i < songs.Count; i++)
        {
            var s = songs[i];
            var res = MidiChecks.EngineOne(r, s, t, mlb, songs[(i + 1) % songs.Count]);
            r.Expect(res.Score.Worst >= 1.0, $"{s.Name}: the ROM does not play the converter's streams: {res.Score.Agree}/{res.Score.Of}; {string.Join("; ", res.Score.Mismatches.Take(3))}");
            r.Expect(res.ControlsFailed, $"{s.Name}: a negative control did not fail");
            var b = SongConvert.Convert(s.Midi, out _)!;
            var bendNotes = new List<string>();
            int loop = b.Facts.LoopFrames, n = Math.Min(2 * loop, 2400);
            var run = MidiRun.Of(t.Patch(b.Channels, loop, s.Name), n + MusicTemplate.Preroll + 4, mlb);
            int start = MidiModel.Best(b.Channels, run.Latch, n, skip: MidiChecks.Skip).Start;
            foreach (var rep in b.Facts.Bends.Where(x => !x.Ignored && !x.Dropped))
            {
                var j = MidiChecks.JudgeBends(run.Latch, start, rep.NesChannel, MidiChecks.IdealPitch(s.Midi, rep.MidiChannel, loop), loop, rep.ToleranceCents, rep.SlidesOff);
                r.Expect(j.Off <= 2 * rep.FramesOverTolerance, $"{s.Name} {MusicTemplate.ChannelNames[rep.NesChannel]}: {j.Off} of {j.Checked} frames off the MIDI's pitch in the ROM (converter says {rep.FramesOverTolerance})");
                bendNotes.Add($"{MusicTemplate.ChannelNames[rep.NesChannel]} bends {j.Bent} frames, {j.Off} off");
            }
            r.Note($"{s.Name,-14} {res.Score.Agree}/{res.Score.Of} field-frames exact, loop {res.LoopFrames}" + (bendNotes.Count > 0 ? "; " + string.Join("; ", bendNotes) : ""));
        }
    }

    // ---- slide signs (2026-11-10, the FL song "achievements") ----

    /// <summary>Hand-written streams (no song file needed): on square 1, square 2 and the triangle, a pitch then slides of every sign and size the converter writes
    /// (positive below one period unit, the case that broke: NESFab miscompiled square 2's 16.8 + 8.8 add and added the period's low byte into the high one;
    /// positive whole, negative below one unit, negative whole), each crossing the period's high byte. The template must play them exactly as the decoder model says,
    /// and the model with the slides negated must not (the streams really slide).</summary>
    public static void BendSigns(ImodelReport r)
    {
        static byte[] Tonal() =>
        [
            0xD1, 0xF0, 0x00, 0xD2, 0x10, 0x00, 0xD0, 0xF0,   // period $0F0, +1/16 a frame for 240 frames: up through $100
            0xD1, 0x08, 0x02, 0xD2, 0x00, 0x01, 0xD0, 0x20,   // $208, +1 a frame: past $220
            0xD1, 0x04, 0x03, 0xD2, 0xF0, 0xFF, 0xD0, 0x60,   // $304, -1/16 a frame: down through $300
            0xD1, 0x10, 0x01, 0xD2, 0x00, 0xFE, 0xD0, 0x20,   // $110, -2 a frame: down through $100
            0xD1, 0xFC, 0x01, 0xD2, 0x80, 0x00, 0xD0, 0x40,   // $1FC, +1/2 a frame
            0x80, 0xFF,
        ];
        var streams = new[] { Tonal(), Tonal(), Tonal(), new byte[] { 0x80, 0xD0, 0xFF, 0xD0, 0xFF, 0xFF } };
        int loop = 0xF0 + 0x20 + 0x60 + 0x20 + 0x40 + 1, n = loop - 4;
        var t = MidiChecks.BundledTemplate();
        var (_, _, mlb) = MidiFixtures.Bundled();
        var rom = t.Patch(streams, loop, "bend signs");
        if (FlpMutants.On(FlpMutants.SlideSignLost))
        {
            // the deliberate bug: square 2's slide never gets its sign byte $FF (LDA/LDX/LDY #$FF; STA/STX/STY mus_sq2_vel@2 -> #$00)
            var m = System.Text.RegularExpressions.Regex.Match(mlb, @"R:([0-9A-Fa-f]{4}):mus_sq2_vel@2:");
            int planted = 0;
            if (m.Success)
            {
                int zp = Convert.ToInt32(m.Groups[1].Value, 16);
                for (int i = MusicTemplate.HeaderBytes; i + 3 < rom.Length; i++)
                    if (rom[i + 1] == 0xFF && rom[i + 3] == zp && (rom[i], rom[i + 2]) is (0xA9, 0x85) or (0xA2, 0x86) or (0xA0, 0x84)) { rom[i + 1] = 0; planted++; }
            }
            r.Expect(planted > 0, "bend signs: the deliberate bug found no sign-byte store to plant itself in");
        }
        var run = MidiRun.Of(rom, n + MusicTemplate.Preroll + 4, mlb);
        var sc = MidiModel.Best(streams, run.Latch, n, skip: MidiChecks.Skip);
        r.Expect(sc.Worst >= 1.0, $"bend signs: the template does not play the slides as the model does: {sc.Agree}/{sc.Of}; {string.Join("; ", sc.Mismatches.Take(3))}");
        var flipped = MidiModel.ScoreAt(streams, run.Latch, sc.Start, n, MidiChecks.Skip, flipSlides: true);
        r.Expect(flipped.Worst < 1.0, "bend signs: the model with the slides negated agrees too (the streams do not slide)");
        r.Note($"bend signs: {sc.Agree}/{sc.Of} field-frames exact (start {sc.Start}); slides negated: {flipped.Agree}/{flipped.Of}");
    }

    // ---- pulse duty ----

    public static void Duty(ImodelReport r)
    {
        if (NoSongs(r)) return;
        var t = MidiChecks.BundledTemplate();
        int nonHalf = 0;
        foreach (var (name, v, b, rom) in Roms(r))
        {
            var model = FlpSong.Read(File.ReadAllBytes(Path.Combine(SongsDir, name + ".flp")));
            var notes = new List<string>();
            foreach (var p in v.Parts.Where(p => p.NesChannel <= 1))
            {
                // what FL plays: the Duty of the first instance on that square (the back end's rule)
                int fl = model.Instruments.Where(i => i.Bn2 is { Mode: "direct", Console: 0 } x && x.Channel == p.NesChannel && i.Notes.Count > 0).Select(i => i.Bn2!.Param(5) & 3).FirstOrDefault(2);
                r.Expect(p.Duty == fl, $"{name} {VrunNesBackend.NesNames[p.NesChannel]}: the part says duty {p.Duty}, FL's instance has {fl}");
                // the converter's choice of NES channel: the stream that plays this part
                int nes = Array.IndexOf(b.Facts.Source, p.NesChannel + 1);
                if (nes is < 0 or > 1) { r.Warn($"{name}: {VrunNesBackend.NesNames[p.NesChannel]} is not played on a square (converter's choice)"); continue; }
                var st = b.Channels[nes];
                bool op = st.Length >= 2 && st[0] == Midi2Nes.OpDuty;
                r.Expect(op == (fl != 2), $"{name} {MusicTemplate.ChannelNames[nes]}: stream starts {(op ? $"with $D3 ${st[1]:X2}" : "without $D3")}, duty {VrunNesBackend.DutyNames[fl]}%");
                if (op) r.Expect(st[1] == (0x30 | fl << 6), $"{name} {MusicTemplate.ChannelNames[nes]}: $D3 operand ${st[1]:X2}, ${0x30 | fl << 6:X2} expected");
                if (fl != 2) nonHalf++;
                // the ROM: every write to the square's volume register that sounds (volume bits non-zero) has the duty in bits 6-7
                var m = new NesEmulator.Plugin.NesAudioMachine(rom, null, "FIX", "FIX", "FIX") { HostSampleRate = 44100 };
                int reg = nes * 4, sounding = 0, wrong = 0; byte firstWrong = 0;
                m.RecordTap = (a, val, _) => { if (a == 0x4000 + reg && (val & 0x0F) != 0) { sounding++; if (val >> 6 != fl) { if (wrong++ == 0) firstWrong = val; } } };
                var buf = new float[4096];
                int frames = Math.Min(b.Facts.LoopFrames + 120, 3000);
                for (int f = 0; f < frames; f++) { m.RunFrame(); while (m.ReadAvailable(buf) > 0) { } if (m.Crashed) { r.Failures.Add($"{name}: the ROM crashed: {m.CrashInfo}"); break; } }
                r.Expect(sounding > 0 && wrong == 0, $"{name} ${0x4000 + reg:X4}: {wrong} of {sounding} sounding writes not at duty {VrunNesBackend.DutyNames[fl]}% (first ${firstWrong:X2})");
                notes.Add($"{MusicTemplate.ChannelNames[nes]} {VrunNesBackend.DutyNames[fl]}%: {(op ? $"$D3 ${st[1]:X2}" : "no opcode")}, ${0x4000 + reg:X4} {sounding - wrong}/{sounding} sounding writes at that duty");
            }
            r.Note($"{name,-14} " + string.Join("; ", notes));
        }
        r.Expect(nonHalf > 0, "no square of the songs plays a duty other than 50%: the check proves nothing about the opcode");
    }

    public static void Rom(ImodelReport r)
    {
        if (NoSongs(r)) return;
        foreach (var (name, v, b, rom) in Roms(r))
        {
            var facts = b.Facts.Lines().ToList();
            var choice = VrunNesBackend.ChannelChoice(v, b.Facts).ToList();
            r.Note($"{name}: ROM sha256 {Sha(rom)}, MIDI sha256 {Sha(v.Midi)[..16]}, {v.Seconds:0.00} s, parts: {string.Join("; ", v.Parts.Select(p => $"{VrunNesBackend.NesNames[p.NesChannel]} <- {p.Instruments} {p.Notes} notes, {p.Slides} slides"))}");
            foreach (var l in facts) r.Note("    fact: " + l);
            foreach (var l in choice.Concat(v.Caveats)) r.Note("    caveat: " + l);
            if (OutDir != null)
            {
                Directory.CreateDirectory(OutDir);
                File.WriteAllBytes(Path.Combine(OutDir, name + ".nes"), rom);
                File.WriteAllBytes(Path.Combine(OutDir, name + ".vrun.mid"), v.Midi);
                File.WriteAllText(Path.Combine(OutDir, name + ".txt"), string.Join("\n", facts.Select(x => "fact: " + x).Concat(choice.Concat(v.Caveats).Select(x => "caveat: " + x))) + "\n");
                File.WriteAllText(Path.Combine(OutDir, name + ".song.json"), FlpSong.Json(FlpSong.Read(File.ReadAllBytes(Path.Combine(SongsDir, name + ".flp")))));
            }
        }
    }

    // ---- deliberate bugs ----

    public sealed record Mutant(int Bit, string What, string CaughtBy);
    public static readonly Mutant[] Mutants =
    [
        new(FlpMutants.SlideSignFlipped, "slides glide the wrong way", "fl-pitch"),
        new(FlpMutants.NoteFineIgnored, "the notes' fine pitch is ignored", "fl-pitch"),
        new(FlpMutants.OldestNoteWins, "the oldest held note keeps the channel", "fl-pitch"),
        new(FlpMutants.TuningIgnored, "the instance's Coarse/Fine are ignored", "synthetic"),
        new(FlpMutants.ReaderDropsLastNote, "the C# reader drops each pattern's last note", "identity"),
        new(FlpMutants.DutyIgnored, "the squares' Duty is not written (50% in the game)", "duty"),
        new(FlpMutants.SlideSignLost, "square 2's slide loses its sign extension in the ROM (the 2026-11-10 NESFab miscompile's class)", "bend-signs"),
        new(FlpMutants.ChannelVolumeSilent, "an FL channel volume off its default is not reported", "fl-volume"),
        new(SvMutants.VoiceFenceBlind, "the Super VRUN fence does not see too many voices", "sv-fence"),
        new(SvMutants.DutyAs50, "the Super VRUN back end plays every pulse at 50%", "sv-synthetic"),
        new(SvMutants.ShortNoiseAsLong, "short noise written as long noise", "sv-synthetic"),
        new(SvMutants.VelocityLinear, "velocity linear in the level (FluidSynth squares it)", "sv-synthetic"),
        new(PsMutants.LoopIgnored, "a preset's sample loses its loop", "sv-preset-snes"),
        new(PsMutants.TuningIgnored, "a preset's tuning is not in its C5", "sv-preset-snes"),
        new(PsMutants.PresetFenceBlind, "the fence does not see presets over the song's sound RAM", "sv-preset-engines"),
        new(PsMutants.MdPresetAsPulse, "the Genesis fence says a preset is an FM patch", "sv-preset-engines"),
        new(PsMutants.GbPresetIgnored, "Vaults GB ignores a Game Boy preset", "preset-others"),
        new(PsMutants.NesPresetIgnored, "VRUN NES plays a preset instance as a plain NES channel", "preset-others"),
        new(CtwSmsMutants.SlidesDropped, "the CTW SMS back end drops the slide notes (the old no-bends game)", "ctwsms-synthetic"),
        new(CtwSmsMutants.PresetIgnored, "CTW SMS reads a preset instance as a plain channel", "ctwsms-preset"),
        new(CtwSmsMutants.PresetFenceSilent, "CTW SMS drops a preset it cannot play without a fence line", "ctwsms-preset"),
        new(CtwSmsMutants.PeriodicAsWhiteSilent, "CTW SMS plays a periodic-noise preset as white noise without saying so", "ctwsms-preset"),
    ];

    public static int Main(string[] args)
    {
        string? only = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--only") only = args[i + 1];
            else if (args[i] == "--songs") SongsDir = args[i + 1];
            else if (args[i] == "--out") OutDir = args[i + 1];
        }
        bool mutants = !args.Contains("--no-mutants");
        Console.WriteLine($"BrokenNes.FruityHost flp-test (the .flp music pipeline, phase 1: shared reader + VRUN NES back end). Songs: {SongsDir} ({Songs().Count} .flp)");
        var ids = only?.Split(',').ToHashSet();
        int failed = 0, notRun = 0;
        foreach (var c in All.Where(c => ids == null || ids.Contains(c.Id)))
        {
            var rep = new ImodelReport();
            var sw = Stopwatch.StartNew();
            try { c.Run(rep); } catch (Exception e) { rep.Failures.Add("threw: " + e); }
            bool nr = rep.Assertions == 0 && rep.Warnings.Any(w => w.StartsWith("NOT RUN"));
            if (nr) notRun++; else if (rep.Failures.Count > 0) failed++;
            Console.WriteLine($"{(nr ? "NOT RUN" : rep.Failures.Count == 0 ? "PASS" : "FAIL")} {c.Id} ({rep.Assertions} assertions, {sw.Elapsed.TotalSeconds:0.0} s): {c.What}");
            foreach (var n in rep.Notes) Console.WriteLine("    " + n);
            foreach (var w in rep.Warnings) Console.WriteLine("    warning: " + w);
            foreach (var f in rep.Failures.Take(12)) Console.WriteLine("    FAIL: " + f);
        }
        int missed = 0;
        if (mutants)
        {
            Console.WriteLine("deliberate bugs (each must be caught by the check named):");
            foreach (var m in Mutants.Where(m => ids == null || ids.Contains(m.CaughtBy)))
            {
                FlpMutants.Mask = m.Bit;
                var rep = new ImodelReport();
                try { All.First(c => c.Id == m.CaughtBy).Run(rep); } catch (Exception e) { rep.Failures.Add("threw: " + e.Message); }
                FlpMutants.Mask = 0;
                bool caught = rep.Failures.Count > 0;
                bool skipped = rep.Assertions == 0;
                if (!caught && !skipped) missed++;
                Console.WriteLine($"  {(caught ? "caught" : skipped ? "NOT RUN" : "MISSED")}: {m.What} ({m.CaughtBy}{(caught ? ": " + rep.Failures[0][..Math.Min(110, rep.Failures[0].Length)] : "")})");
            }
        }
        Console.WriteLine($"{All.Length - failed - notRun} passed, {notRun} NOT RUN, {failed} failed; {missed} deliberate bugs missed");
        return failed == 0 && missed == 0 ? 0 : 1;
    }
}
