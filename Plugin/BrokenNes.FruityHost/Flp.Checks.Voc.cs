// flp-test's checks of the back ends for the Vaults GB / Vaults GG / CTW SMS VRUN games (Music/Flp/VocGbBackend.cs, VocGgBackend.cs, CtwSmsBackend.cs):
//   vocgb-synthetic   a Game Boy project written here: events, streams, tables and the fence (every rule a part of it breaks: a NES instance, the Mix channel, a sampler, a note under
//                     the pulse's range, partial pan, overlapping notes), and a project with nothing for the Game Boy (the fence says NOTHING, never an empty pass)
//   vocgb-fl          per frame, the events play FL's pitch and level (worked out from the song model alone, apart from the back end): synthetic + the user's songs as Game Boy songs
//   vocgb-python      the game's own converter (VRUN_VOC_dev tools/music/vocmusic.py, the events front end) makes the same streams and tables from the events file (NOT RUN without it)
//   vocgb-engine      the test ROM (the game's own driver, Resources/flpvocgb.gbc) in BrokenNes's Game Boy: frame by frame its four channels play the events (period, level, duty, on/off),
//                     and a model transposed by a semitone does not (the negative control); every song, synthetic + the user's songs as Game Boy songs
//   vocgg-*           the same for Vaults GG (the Game Boy bytes, the Game Gear's PSG driver), CtwSms-* for CTW SMS
// The user's songs are NES projects: they are read as if every BrokenNes2 instance sat on the target console (Retarget), which is how the user would compose them for it.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrokenNes2.Music.Flp;
using NesEmulator;
using NesEmulator.Gb;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static class FlpVocChecks
{
    public static readonly FlpChecks.Check[] All =
    [
        new("vocgb-synthetic", "Vaults GB: a Game Boy project written here: events, streams, tables and every fence rule it breaks", GbSynthetic),
        new("vocgb-fl", "Vaults GB: per frame the events play FL's pitch and level (synthetic + the user's songs as Game Boy songs)", GbFl),
        new("vocgb-python", "Vaults GB: the game's own converter makes the same bytes from the events file", GbPython),
        new("vocgb-engine", "Vaults GB: the test ROM (the game's own driver) plays the events frame by frame in BrokenNes's Game Boy", GbEngine),
        new("vocgg-synthetic", "Vaults GG: the Game Gear's fence on the same project (duty, wave as a square, low notes, noise rates, levels, frame rate)", GgSynthetic),
        new("vocgg-engine", "Vaults GG: the test ROM (the game's own PSG sound unit) plays the Game Boy bytes frame by frame in BrokenNes's Game Gear", GgEngine),
        .. FlpSmsChecks.All,
    ];

    internal static string RepoFile(string rel) => Path.Combine(MidiFixtures.RepoRoot(), rel);
    internal static string VocGame = Environment.GetEnvironmentVariable("BROKENNES_VOC") ?? @"C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_VOC_dev";
    internal static string? OutDir => FlpChecks.OutDir;

    /// <summary>The model with every BrokenNes2 instance moved to a console (how the user would compose the same song for it).</summary>
    internal static FlpSongModel Retarget(FlpSongModel s, int console)
    {
        foreach (var i in s.Instruments) if (i.Bn2 is { } b) { b.Console = console; b.ConsoleName = FlpSong.Consoles[console]; b.Core = 0; if (b.Params.Length > 10) b.Params[10] = console; }
        return s;
    }

    internal static List<(string Name, FlpSongModel Song)> UserSongs(int console)
        => FlpChecks.Songs().Select(f => (Path.GetFileNameWithoutExtension(f), Retarget(FlpSong.Read(File.ReadAllBytes(f)), console))).ToList();

    // ---- the synthetic Game Boy project ----

    internal static byte[] GbProject() => FlpChecks.Project(150,
        [("Kick", "", null),
         ("Lead", "BrokenNes2", FlpChecks.Bn2State(duty: 0, console: 1, place: 0, pan: -100)),
         ("Deep", "BrokenNes2", FlpChecks.Bn2State(duty: 3, console: 1, place: 1, pan: 30)),
         ("Bass", "BrokenNes2", FlpChecks.Bn2State(console: 1, place: 2)),
         ("Hat", "BrokenNes2", FlpChecks.Bn2State(console: 1, place: 3, noiseMode: 1, volume: 500)),
         ("Mix", "BrokenNes2", FlpChecks.Bn2State(console: 1, channel: 5)),
         ("NesLead", "BrokenNes2", FlpChecks.Bn2State(place: 0))],
        [(1, [new(0, 48, 72, 1), new(24, 48, 76, 1), new(96, 48, 60, 1, Fine: 170), new(0, 40, 30, 2), new(48, 40, 62, 2, Vel: 64),
              new(0, 80, 48, 3), new(0, 40, 36, 3, Slide: true), new(0, 12, 60, 4), new(48, 12, 66, 4, Vel: 128),
              new(0, 24, 60, 0), new(0, 24, 60, 5), new(0, 24, 60, 6)])],
        [(0, 1, 192), (192, 1, 72)]);

    public static void GbSynthetic(ImodelReport r)
    {
        var s = FlpSong.Read(GbProject());
        var v = VocGbBackend.Build(s, "synthetic");
        string fence = string.Join(" | ", v.Fence);
        bool Has(Fit f, string rule, string text) => v.Fence.Any(l => l.Fit == f && l.Rule == rule && l.Text.Contains(text));
        r.Expect(Has(Fit.Out, "instruments", "is a NES instrument"), "no OUT line for the NES instance");
        r.Expect(Has(Fit.Out, "instruments", "is not BrokenNes2"), "no OUT line for the sampler");
        r.Expect(Has(Fit.Out, "channels", "Mix"), "no OUT line for the Mix-channel instance");
        r.Expect(Has(Fit.Out, "note range", "under the lowest"), "no OUT line for pulse 2's note 30 (under C2)");
        r.Expect(Has(Fit.Changed, "pan", "partly panned"), "no CHANGED line for pulse 2's Pan 30");
        r.Expect(Has(Fit.Changed, "polyphony", "cut short"), "no CHANGED line for the lead's overlapping notes");
        r.Expect(Has(Fit.Ok, "duty", "12.5%") && Has(Fit.Ok, "duty", "75%"), "the duty line does not list 12.5% and 75%");
        r.Expect(Has(Fit.Ok, "noise", "7-bit"), "the noise line does not say Short = 7-bit");
        int F(int tick) => (int)Math.Round(tick * 60.0 / 150 / 96 * VocGbBackend.Fps, MidpointRounding.AwayFromZero);
        var p1 = v.Ch[0];
        r.Expect(p1.Any(e => e.Frame == 0 && e.Op == "on" && e.Value == 72) && p1.Any(e => e.Frame == F(24) && e.Op == "on" && e.Value == 76), $"pulse 1: 72 at 0, 76 at frame {F(24)} (newest wins): {string.Join(" ", p1.Take(8))}");
        r.Expect(p1.Any(e => e.Frame == F(96) && e.Op == "pitch" && e.Value == VocGbBackend.PulseX(VocGbBackend.MidiHz(60.5))), "pulse 1: the +50-cent note is not an exact-register note ($D1)");
        r.Expect(p1.Any(e => e.Op == "pan" && e.Value == 2), "pulse 1: Pan -100 is not hard left ($D5 2)");
        r.Expect(v.Instruments.Any(x => x[0] == 0x00 && x[6] == 0) && v.Instruments.Any(x => x[0] == 0xC0), $"instrument rows for duty 12.5% and 75%: {string.Join(" ", v.Instruments.Select(Convert.ToHexString))}");
        r.Expect(v.Instruments.Any(x => x[6] == 8 && x[0] == 0), "no 7-bit noise instrument row");
        var p2 = v.Ch[1];
        r.Expect(p2.Any(e => e.Op == "on" && e.Value == 42), $"pulse 2: note 30 raised an octave to 42: {string.Join(" ", p2.Where(e => e.Op is "on" or "pitch"))}");
        r.Expect(p2.Any(e => e.Op == "vol" && e.Value == 8), $"pulse 2: velocity 64 = level round(15 x 64/128) = 8 (7.5 rounds away): {string.Join(" ", p2.Where(e => e.Op == "vol"))}");
        int freqs = v.Ch[2].Count(e => e.Op == "freq");
        r.Expect(freqs > 5 && v.Ch[2].Where(e => e.Op == "freq").Last().Value < VocGbBackend.FreqTab[60], $"wave: the slide down an octave: {freqs} frequency changes");
        r.Expect(v.Ch[3].Count(e => e.Op == "on") == 4 && v.Ch[3].Where(e => e.Op == "vol").Select(e => e.Value).Distinct().Order().SequenceEqual([6, 8]), $"noise: 4 hits at levels 6 (velocity 100 x Volume 0.5) and 8 (128 x 0.5): {string.Join(" ", v.Ch[3])}");
        r.Expect(v.Streams.All(x => x.Length > 0 && x[^1] == 0xFF) && v.Streams[0].Contains((byte)0xD4), "streams: each ends with the loop $FF, the loop point $D4 is there");
        r.Note($"synthetic: streams {string.Join("+", v.Streams.Select(x => x.Length))} bytes; fence: {FlpFence.Summary(v.Fence)}");
        foreach (var l in FlpFence.Lines(v.Fence)) r.Note("    " + l);

        // a project with nothing for the Game Boy: the fence says NOTHING (never an empty pass)
        var nes = VocGbBackend.Build(FlpSong.Read(FlpChecks.Project(150, [("NesLead", "BrokenNes2", FlpChecks.Bn2State(place: 0))], [(1, [new(0, 48, 72, 0)])], [(0, 1, 96)])), "nes-only");
        r.Expect(nes.Fence.Any(l => l.Fit == Fit.Out && l.Text.StartsWith("NOTHING")), "a NES-only project: no NOTHING line in the Game Boy fence");
        // a stream over the bank: OUT with the number of bytes over
        var big = Retarget(FlpSong.Read(FlpChecks.Project(150, [("Fast", "BrokenNes2", FlpChecks.Bn2State(console: 1, place: 0))],
            [(1, Enumerable.Range(0, 9000).Select(i => new FlpChecks.SNote(i * 8, 4, 50 + i % 30, 0, Vel: 40 + i % 80)).ToArray())], [(0, 1, 72000)])), 1);
        var bv = VocGbBackend.Build(big, "big");
        r.Expect(bv.Fence.Any(l => l.Fit == Fit.Out && l.Rule == "bytes" && l.Text.Contains("bytes over")), $"a 9000-note stream ({bv.Streams[0].Length} bytes) has no OUT bytes line");
        r.Expect(VocGbBackend.Patch(Template().Rom, Template().Manifest, bv, out var why) == null && why != null, "the test ROM accepted a stream bigger than its room");
    }

    // ---- FL's pitch per frame ----

    /// <summary>What FL plays on a channel of a console at an FL tick, from the model alone: the newest held note, its slides, its level.</summary>
    internal static (double Cents, int Level, FlpInstrument I)? FlAt(FlpSongModel s, int console, int channel, double tick)
    {
        (FlpNote N, double Base, double Volume, int Order, FlpInstrument I)? own = null;
        int order = 0;
        foreach (var ins in s.Instruments)
        {
            if (ins.Bn2 is not { Mode: "direct" } b || b.Console != console || b.Channel != channel) continue;
            double tune = b.Param(3) * 100 + b.Param(4), vol = Math.Clamp(b.Param(1, 1000) / 1000.0, 0, 1);
            foreach (var n in ins.Notes)
            {
                int o = order++;
                if (n.Slide || n.End <= n.Start || !(n.Start <= tick && tick < n.End)) continue;
                if (own == null || n.Start > own.Value.N.Start || n.Start == own.Value.N.Start && o > own.Value.Order) own = (n, n.Key * 100 + n.Fine - 120 + tune, vol, o, ins);
            }
        }
        if (own is not { } w) return null;
        double cur = w.Base, tune2 = w.I.Bn2!.Param(3) * 100 + w.I.Bn2.Param(4);
        foreach (var g in w.I.Notes.Where(g => g.Slide && w.N.Start <= g.Start && g.Start < w.N.End).OrderBy(g => g.Start))
        {
            double gs = g.Start - VrunNesBackend.SlideEarly, gl = g.End - g.Start + VrunNesBackend.SlideExtra, to = g.Key * 100 + g.Fine - 120 + tune2;
            if (tick <= gs) break;
            if (tick >= gs + gl) cur = to; else { cur += (to - cur) * (tick - gs) / gl; break; }
        }
        return (cur, Math.Clamp((int)Math.Round(15 * (w.N.Velocity / 128.0) * w.Volume, MidpointRounding.AwayFromZero), 0, 15), w.I);
    }

    /// <summary>The state the events give each frame (after that frame's events): sounding, the frequency register, the level, the duty; noise: the NR43 value.</summary>
    internal sealed record FrameState(bool On, int X, int Vol, int Duty, int Nr43, int Note = -1);
    internal static FrameState[] Model(VocSong v, int c, int transpose = 0)
    {
        var st = new FrameState[v.Length];
        var late = c == 1 && v.Target == VocTarget.Gb ? VocGbBackend.QuadFrames(v) : new SortedSet<int>();
        int Tr(int x) => transpose == 0 ? x : Math.Clamp(2048 - (int)Math.Round((2048 - x) / Math.Pow(2, transpose / 12.0)), 0, 2047);
        bool on = false; int x = 0, vol = 15, duty = 2, nr = 0, note = -1;
        var ev = v.Ch[c].Select((e, i) => (e, i)).OrderBy(q => q.e.Frame).ThenBy(q => q.i).Select(q => late.Contains(q.e.Frame) ? q.e with { Frame = q.e.Frame + 1 } : q.e).ToList();
        int k = 0;
        for (int f = 0; f < v.Length; f++)
        {
            for (; k < ev.Count && ev[k].Frame <= f; k++)
            {
                var e = ev[k];
                switch (e.Op)
                {
                    case "inst": duty = e.Inst!.Duty; break;
                    case "vol": vol = e.Value; break;
                    case "on": on = true; if (c == 3) nr = e.Value & 0xF7; else { x = Tr(VocGbBackend.FreqTab[Math.Clamp(e.Value + (c == 2 ? 12 : 0), 0, 127)]); note = e.Value + transpose; } break;
                    case "pitch": on = true; x = Tr(e.Value); note = -1; break;
                    case "freq": x = Tr(e.Value); note = -1; break;
                    case "off": on = false; break;
                }
            }
            st[f] = new FrameState(on && vol > 0, x, vol, duty, nr, note);
        }
        return st;
    }

    public static void GbFl(ImodelReport r)
    {
        var all = new List<(string, FlpSongModel)> { ("synthetic", FlpSong.Read(GbProject())) };
        all.AddRange(UserSongs(1));
        if (FlpChecks.Songs().Count == 0) r.Warn($"NOT RUN: the user's songs (no .flp in {FlpChecks.SongsDir}); the synthetic one was checked");
        foreach (var (name, s) in all)
        {
            var v = VocGbBackend.Build(s, name);
            double ticksPerFrame = s.Ppq * s.Tempo / 60.0 / VocGbBackend.Fps;
            var parts = new List<string>();
            for (int c = 0; c < 3; c++)
            {
                if (v.Notes[c] == 0) continue;
                var m = Model(v, c);
                var bounds = new SortedSet<double>(s.Instruments.Where(i => i.Bn2 is { } b && b.Console == 1 && b.Channel == c).SelectMany(i => i.Notes.Where(n => !n.Slide).SelectMany(n => new double[] { n.Start, n.End })));
                int judged = 0, off = 0; string first = "";
                for (int f = 1; f + 1 < v.Length; f++)
                {
                    double t0 = f * ticksPerFrame;
                    // frames next to a note start / stop are timing (half a frame either way): judged where FL holds its state across the whole frame
                    if (bounds.GetViewBetween(t0 - ticksPerFrame * 0.5, t0 + ticksPerFrame).Count > 0) continue;
                    var a = FlAt(s, 1, c, t0 - ticksPerFrame * 0.5); var b = FlAt(s, 1, c, t0); var z = FlAt(s, 1, c, t0 + ticksPerFrame * 0.99);
                    judged++;
                    var st = m[f];
                    bool bad;
                    if (b == null || (c < 2 && b.Value.Level == 0)) bad = st.On;
                    else if (!st.On) bad = true;
                    else
                    {
                        double want = b.Value.Cents;
                        int lo = c < 2 ? VocGbBackend.PulseMin : VocGbBackend.WaveMin;
                        while (want / 100 < lo - 0.5) want += 1200;
                        double hz = VocGbBackend.XHz(st.X, c == 2), err = Math.Abs(1200 * Math.Log2(hz / VocGbBackend.MidiHz(want / 100)));
                        double slope = Math.Abs((z?.Cents ?? want) - (a?.Cents ?? want));
                        int stepHalf = (int)Math.Ceiling(1200 * Math.Log2(VocGbBackend.XHz(Math.Min(2047, st.X + 1), c == 2) / hz) / 2 + 1);
                        bad = err > stepHalf + slope || (c < 2 && st.Vol != b.Value.Level) || (c < 2 && st.Duty != (b.Value.I.Bn2!.Param(5, 1) & 3));
                    }
                    if (bad) { off++; if (first.Length == 0) first = $"frame {f}: FL {(b == null ? "silent" : $"{b.Value.Cents:0.0} cents level {b.Value.Level}")}, events {st}"; }
                }
                r.Expect(off == 0, $"{name} {VocGbBackend.Names[c]}: {off} of {judged} frames the events do not play FL's pitch / level / duty; first {first}");
                parts.Add($"{VocGbBackend.Names[c]} {judged - off}/{judged}");
            }
            r.Note($"{name,-14} {v.Length} frames: {string.Join(", ", parts)} frames on FL's pitch, level and duty");
        }
    }

    // ---- Vaults GG ----

    public static void GgSynthetic(ImodelReport r)
    {
        var v = VocGgBackend.Build(FlpSong.Read(GbProject()), "synthetic");
        bool Has(Fit f, string rule, string text) => v.Fence.Any(l => l.Fit == f && l.Rule == rule && l.Text.Contains(text));
        var gb = VocGbBackend.Build(FlpSong.Read(GbProject()), "synthetic");
        r.Expect(Enumerable.Range(0, 4).All(c => v.Streams[c].AsSpan().SequenceEqual(gb.Streams[c])), "the Game Gear song is not the Game Boy song's bytes");
        r.Expect(Has(Fit.Changed, "duty", "50%"), "no CHANGED duty line (12.5% / 75% on the PSG)");
        r.Expect(Has(Fit.Changed, "wave", "square"), "no CHANGED wave line (the triangle plays as a square)");
        r.Expect(Has(Fit.Out, "note range", "PSG's lowest"), "no OUT line for the notes under A2 (pulse 2's 42, the wave's 48 slid down)");
        r.Expect(Has(Fit.Changed, "noise", "white noise only"), "no CHANGED line for Short noise");
        r.Expect(Has(Fit.Changed, "pan", "mono"), "no CHANGED pan line");
        r.Expect(Has(Fit.Changed, "timing", "fast"), "no CHANGED frame-rate line");
        r.Expect(Has(Fit.Out, "instruments", "is a NES instrument"), "no OUT line for the NES instance");
        r.Note($"synthetic (Game Gear): fence {FlpFence.Summary(v.Fence)}");
        foreach (var l in FlpFence.Lines(v.Fence)) r.Note("    " + l);
        // a Master System console project is a Game Gear song too (the PSG chip the Game Gear has)
        var sms = VocGgBackend.Build(Retarget(FlpSong.Read(GbProject()), 3), "sms");
        r.Expect(sms.Notes.Take(3).Sum() > 0, "a project on the Master System console gives no Game Gear notes");
    }

    internal static (int[] Period, int[] Att, int Noise)[] RunGg(byte[] rom, int frames)
    {
        var kind = ConsoleKind.GameGear;
        var s = ConsoleSessions.Create(kind, rom, CoreCatalog.Default(kind, CoreSlot.Cpu), CoreCatalog.Default(kind, CoreSlot.Ppu), CoreCatalog.Default(kind, CoreSlot.Apu), null, "flp.gg");
        var inner = s is NesEmulator.Mix.SegaPictureSession w ? w.Inner : s;
        var psg = ((NesEmulator.Sega.SmsSound)((NesEmulator.Sega.SmsSession)inner).Board.Sound).Psg;
        var buf = new short[1 << 16];
        var o = new (int[], int[], int)[frames];
        for (int i = 0; i < frames; i++)
        {
            s.RunFrame();
            while (s.ReadSamples(buf) > 0) { }
            o[i] = ([psg.Period(0), psg.Period(1), psg.Period(2)], [psg.Volume(0), psg.Volume(1), psg.Volume(2), psg.Volume(3)], psg.NoiseControl);
        }
        return o;
    }

    public static void GgEngine(ImodelReport r)
    {
        string rp = RepoFile(@"Plugin\BrokenNes2.Plugin\Resources\flpvocgg.gg");
        var tmpl = File.ReadAllBytes(rp); var man = File.ReadAllText(Path.ChangeExtension(rp, ".txt"));
        var all = new List<(string, FlpSongModel)> { ("synthetic", FlpSong.Read(GbProject())) };
        all.AddRange(UserSongs(3));
        foreach (var (name, s) in all)
        {
            var v = VocGgBackend.Build(s, name);
            var rom = VocGgBackend.Patch(tmpl, man, v, out var why);
            r.Expect(rom != null, $"{name}: the test ROM refused the song: {why}");
            if (rom == null) continue;
            int n = Math.Min(v.Length, 3600);
            var run = RunGg(rom, n + 200);
            int lead = Enumerable.Range(0, 3).FirstOrDefault(c => v.Notes[c] > 0, -1);
            if (lead < 0) { r.Warn($"{name}: no tonal channel to line up"); continue; }
            (int P, int A) Want(FrameState st, int c, int tr = 0) => !st.On ? (-1, 15) : ((st.Note >= 0 ? VocGgBackend.PeriodOfNote(st.Note) : VocGgBackend.Period(c, st.X)).N, c == 2 ? 0 : VocGgBackend.AttOf[st.Vol]);
            bool Ok((int P, int A) w, (int[] Period, int[] Att, int Noise) g, int c) => w.A == 15 ? g.Att[c] == 15 : g.Att[c] == w.A && (c == 3 || g.Period[c] == w.P);
            var lm = Model(v, lead);
            int start = 0, best = -1;
            for (int s0 = 0; s0 < 180; s0++)
            {
                int a = 0;
                for (int f = 0; f < Math.Min(n, 600); f++) a += Ok(Want(lm[f], lead), run[s0 + f], lead) ? 1 : 0;
                if (a > best) { best = a; start = s0; }
            }
            var parts = new List<string>();
            for (int c = 0; c < 3; c++)
            {
                if (v.Notes[c] == 0) continue;
                var m = Model(v, c); var neg = Model(v, c, transpose: 1);
                int agree = 0, negAgree = 0, sounding = 0; string first = "";
                for (int f = 0; f < n; f++)
                {
                    var g = run[start + f];
                    bool a = Ok(Want(m[f], c), g, c);
                    if (a) agree++;
                    if (m[f].On) { sounding++; if (Ok(Want(neg[f], c), g, c)) negAgree++; }
                    if (!a && first.Length == 0) first = $"frame {f}: events {m[f]} -> period {Want(m[f], c).P} att {Want(m[f], c).A}; PSG period {g.Period[c]} att {g.Att[c]}";
                }
                double pct = agree / (double)n, negPct = sounding == 0 ? 0 : negAgree / (double)sounding;
                r.Expect(pct >= 0.98, $"{name} {VocGbBackend.Names[c]}: the Game Gear driver plays the events on {pct:P1} of {n} frames (98% wanted); first {first}");
                r.Expect(sounding < 20 || negPct < 0.5, $"{name} {VocGbBackend.Names[c]}: the negative control (a semitone up) agrees on {negPct:P0} of the sounding frames");
                parts.Add($"{VocGbBackend.Names[c]} {pct:P1} (control {negPct:P0})");
            }
            r.Note($"{name,-14} ROM sha256 {Convert.ToHexString(SHA256.HashData(rom))[..16].ToLowerInvariant()}, song from frame {start}: {string.Join(", ", parts)}; fence {FlpFence.Summary(v.Fence)}");
            if (OutDir != null)
            {
                Directory.CreateDirectory(OutDir);
                File.WriteAllBytes(Path.Combine(OutDir, name + ".vaults.gg"), rom);
                File.WriteAllLines(Path.Combine(OutDir, name + ".vaults-gg.fence.txt"), FlpFence.Lines(v.Fence));
            }
        }
    }

    // ---- the game's own converter ----

    internal static string? Python(string script, string[] args, out string? problem)
    {
        problem = null;
        var psi = new ProcessStartInfo("python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
        psi.ArgumentList.Add("-I"); psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            if (p.ExitCode != 0) { problem = $"python exit {p.ExitCode}: {e.Result.Trim()}"; return null; }
            return o.Result;
        }
        catch (Exception x) { problem = "python could not be run: " + x.Message; return null; }
    }

    public static void GbPython(ImodelReport r)
    {
        string tools = Path.Combine(VocGame, @"tools\music");
        if (!File.Exists(Path.Combine(tools, "vocmusic.py")) || !File.ReadAllText(Path.Combine(tools, "vocmusic.py")).Contains("def events_song")) { r.Warn($"NOT RUN: the game's converter with the events front end is not at {tools}"); return; }
        string tmp = Path.Combine(Path.GetTempPath(), "bn2-flp-vocgb-" + Environment.ProcessId);
        Directory.CreateDirectory(tmp);
        var all = new List<(string, FlpSongModel)> { ("synthetic", FlpSong.Read(GbProject())) };
        all.AddRange(UserSongs(1));
        foreach (var (name, s) in all)
        {
            var v = VocGbBackend.Build(s, name);
            string f = Path.Combine(tmp, name + ".vocsong.json");
            File.WriteAllText(f, VocGbBackend.EventsJson(v));
            var outp = Python(RepoFile(@"BrokenDev\tools\music\vocgb\events_dump.py"), [tools, f], out var why);
            if (outp == null) { r.Warn($"NOT RUN: {name}: {why}"); continue; }
            using var j = JsonDocument.Parse(outp);
            var py = j.RootElement.GetProperty("streams").EnumerateArray().Select(x => x.GetString()!).ToArray();
            for (int c = 0; c < 4; c++) r.Expect(py[c] == Convert.ToHexString(v.Streams[c]).ToLowerInvariant(), $"{name} {VocGbBackend.Names[c]}: the game's converter writes another stream ({py[c].Length / 2} bytes vs {v.Streams[c].Length})");
            var pyi = j.RootElement.GetProperty("instruments").EnumerateArray().Select(x => x.GetString()!).ToArray();
            r.Expect(pyi.SequenceEqual(v.Instruments.Select(x => Convert.ToHexString(x).ToLowerInvariant())), $"{name}: instruments differ: {string.Join(",", pyi)} vs {string.Join(",", v.Instruments.Select(Convert.ToHexString))}");
            var pyn = j.RootElement.GetProperty("noise").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            r.Expect(pyn.SequenceEqual(v.Noise), $"{name}: the noise table differs");
            r.Note($"{name,-14} streams {string.Join("+", v.Streams.Select(x => x.Length))} bytes, {v.Instruments.Count} instruments, {v.Noise.Count} noise settings: {(r.Failures.Count == 0 ? "= the game's converter" : "DIFFERS")}");
        }
    }

    // ---- the test ROM in BrokenNes's Game Boy ----

    internal static (byte[] Rom, string Manifest) Template() =>
        (File.ReadAllBytes(RepoFile(@"Plugin\BrokenNes2.Plugin\Resources\flpvocgb.gbc")), File.ReadAllText(RepoFile(@"Plugin\BrokenNes2.Plugin\Resources\flpvocgb.txt")));

    /// <summary>The ROM run for some frames; per frame, after it, each channel's view.</summary>
    internal static GbApuChannelView[][] RunGb(byte[] rom, int frames)
    {
        var s = ConsoleSessions.Create(ConsoleKind.GameBoy, rom, CoreCatalog.Default(ConsoleKind.GameBoy, CoreSlot.Cpu), CoreCatalog.Default(ConsoleKind.GameBoy, CoreSlot.Ppu), "GB", null, "flp.gb");
        var apu = (APU_GB)((GbSession)s).Apu;
        var buf = new short[1 << 16];
        var o = new GbApuChannelView[frames][];
        for (int i = 0; i < frames; i++)
        {
            s.RunFrame();
            while (s.ReadSamples(buf) > 0) { }
            o[i] = [apu.Channel(0), apu.Channel(1), apu.Channel(2), apu.Channel(3)];
        }
        return o;
    }

    internal static int Agree(FrameState m, GbApuChannelView g, int c)
    {
        bool on = g.Enabled && g.DacOn && g.Volume > 0;
        if (on != m.On) return 0;
        if (!on) return 1;
        if (c < 3 && g.Period != m.X) return 0;
        if (c < 2 && (g.Volume != m.Vol || g.Duty != m.Duty)) return 0;
        return 1;
    }

    public static void GbEngine(ImodelReport r)
    {
        var (tmpl, man) = Template();
        var all = new List<(string, FlpSongModel)> { ("synthetic", FlpSong.Read(GbProject())) };
        all.AddRange(UserSongs(1));
        foreach (var (name, s) in all)
        {
            var v = VocGbBackend.Build(s, name);
            var rom = VocGbBackend.Patch(tmpl, man, v, out var why);
            r.Expect(rom != null, $"{name}: the test ROM refused the song: {why}");
            if (rom == null) continue;
            int n = Math.Min(v.Length, 3600);
            var run = RunGb(rom, n + 200);
            var parts = new List<string>();
            int start = -1;
            // the song's first frame in the run: the start that agrees best on the first channel with notes (the boot takes some frames)
            int lead = Enumerable.Range(0, 3).FirstOrDefault(c => v.Notes[c] > 0, -1);
            if (lead < 0) { r.Warn($"{name}: no tonal channel to line up"); continue; }
            var lm = Model(v, lead);
            int best = -1;
            for (int s0 = 0; s0 < 180; s0++)
            {
                int a = 0;
                for (int f = 0; f < Math.Min(n, 600); f++) a += Agree(lm[f], run[s0 + f][lead], lead);
                if (a > best) { best = a; start = s0; }
            }
            for (int c = 0; c < 3; c++)
            {
                if (v.Notes[c] == 0) continue;
                var m = Model(v, c); var neg = Model(v, c, transpose: 1);
                int agree = 0, negAgree = 0, sounding = 0;
                string first = "";
                for (int f = 0; f < n; f++)
                {
                    var g = run[start + f][c];
                    int a = Agree(m[f], g, c);
                    agree += a;
                    if (m[f].On) { sounding++; negAgree += Agree(neg[f], g, c); }
                    if (a == 0 && first.Length == 0) first = $"frame {f}: events {m[f]}, Game Boy on {g.Enabled && g.DacOn && g.Volume > 0} period {g.Period} vol {g.Volume} duty {g.Duty}";
                }
                double pct = agree / (double)n, negPct = sounding == 0 ? 0 : negAgree / (double)sounding;
                r.Expect(pct >= 0.98, $"{name} {VocGbBackend.Names[c]}: the game's driver plays the events on {pct:P1} of {n} frames (98% wanted); first {first}");
                r.Expect(sounding < 20 || negPct < 0.5, $"{name} {VocGbBackend.Names[c]}: the negative control (a semitone up) agrees on {negPct:P0} of the sounding frames");
                parts.Add($"{VocGbBackend.Names[c]} {pct:P1} (control {negPct:P0})");
            }
            if (v.Notes[3] > 0)
            {
                var heard = new HashSet<int>(); var want = v.Ch[3].Where(e => e.Op == "on").Select(e => e.Value & 0xF7).ToHashSet();
                for (int f = 0; f < n; f++) { var g = run[start + f][3]; if (g.Enabled && g.Volume > 0) heard.Add(g.NoiseShift << 4 | g.NoiseDivisorCode); }
                r.Expect(want.SetEquals(heard), $"{name} noise: the rates heard ({string.Join(",", heard.Select(x => x.ToString("X2")))}) are not the events' ({string.Join(",", want.Select(x => x.ToString("X2")))})");
                parts.Add($"noise {heard.Count} rates");
            }
            r.Note($"{name,-14} ROM sha256 {Convert.ToHexString(SHA256.HashData(rom))[..16].ToLowerInvariant()}, song from frame {start}: {string.Join(", ", parts)}");
            if (OutDir != null)
            {
                Directory.CreateDirectory(OutDir);
                File.WriteAllBytes(Path.Combine(OutDir, name + ".vaults.gb"), rom);
                File.WriteAllText(Path.Combine(OutDir, name + ".vocsong.json"), VocGbBackend.EventsJson(v));
                File.WriteAllLines(Path.Combine(OutDir, name + ".vaults-gb.fence.txt"), FlpFence.Lines(v.Fence));
            }
        }
    }
}
