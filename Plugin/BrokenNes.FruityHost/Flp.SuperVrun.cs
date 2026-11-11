// flp-test checks for the Super VRUN back end (Music/Flp/SuperVrunBackend.cs: SNES, WideMess, Genesis, DOS, Windows 95) and its fence, plus the command the build scripts call:
//   FruityHost flp-backend <song.flp> <engine> <out dir>     engine = supervrun-snes | supervrun-wms | supervrun-md | supervrun-dos | supervrun-95
//     writes <out>/<name>.mid (sf2it.py's MNES layout), <name>.fence.json (brokennes-fence v1) and prints the fence; exit 0 (an OVER line is the composer's, not an error)
//   sv-synthetic   a project written here: programs (duty / triangle / noise), noise keys (period + short mode), velocities (sqrt of the level), pan, bends that give FL's pitch, the per-engine lines
//   sv-fence       fence violations, one per rule: too many voices for each engine, two noises at once, a note out of SNESMOD's range, 16 instances, other consoles / ROM mode / samplers
//   sv-songs       every Reverse Coaster song on each engine: the MIDI parses, its notes = the model's sounding notes, the fence printed
using BrokenNes2.Music.Flp;
using Vrun.Audio;

namespace BrokenNes.FruityHost;

internal static partial class SvChecks
{
    public static int Backend(string[] args)
    {
        if (args.Length < 4 || SuperVrunBackend.Parse(args[2]) is not { } eng) { Console.Error.WriteLine("usage: flp-backend <song.flp> <" + string.Join("|", SuperVrunBackend.Ids) + "> <out dir>"); return 2; }
        string name = Path.GetFileNameWithoutExtension(args[1]);
        if (eng is SvEngine.Dos or SvEngine.Win95 && AdlibBackend.BuildSuperVrun(FlpSong.Read(File.ReadAllBytes(args[1])), name) is { } adl)
        {
            // composed for the AdLib (x86 PC console instances): the song blob replaces the converted module in the DOS / 95 build (oplconv.py reads music/adlib/<song>.opl)
            Directory.CreateDirectory(args[3]);
            File.WriteAllBytes(Path.Combine(args[3], name + ".adlib.opl"), adl.Blob);
            File.WriteAllText(Path.Combine(args[3], name + ".fence.json"), adl.Fence.Json());
            Console.Write(adl.Fence.Text());
            foreach (var v in adl.Voices) Console.WriteLine($"  OPL voice {v.Voice + 1} \"{v.Instrument}\": {v.PatchFrom}, {v.Notes} notes");
            Console.WriteLine($"  {adl.Seconds:0.00} s a loop; wrote {name}.adlib.opl ({adl.Blob.Length} bytes) and {name}.fence.json in {args[3]}");
            return 0;
        }
        var s = SuperVrunBackend.Build(FlpSong.Read(File.ReadAllBytes(args[1])), name, eng);
        Directory.CreateDirectory(args[3]);
        File.WriteAllBytes(Path.Combine(args[3], name + ".mid"), s.Midi);
        File.WriteAllText(Path.Combine(args[3], name + ".fence.json"), s.Fence.Json());
        if (s.Presets.Count > 0)
        {
            // the presets' samples for sf2it.py --presets: {"<MIDI channel 0-15>": {"wav", "name", "c5", "loop_start"}}
            var json = new System.Text.StringBuilder("{");
            foreach (var p in s.Presets)
            {
                string wav = $"{name}.preset{p.MidiChannel}.wav";
                File.WriteAllBytes(Path.Combine(args[3], wav), FlpPresetSound.Wav(p.Sound));
                // the plugin's own middle C held 1.5 s (mono float32, 32 kHz): the reference a game build's recording is compared with
                var preset = FlpSong.Read(File.ReadAllBytes(args[1])).Instruments.First(i => i.Name == p.Instrument).Bn2!.Preset!;
                File.WriteAllBytes(Path.Combine(args[3], $"{name}.preset{p.MidiChannel}.plugin.f32"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(FlpPresetSound.PluginRender(preset, 1.5).AsSpan()).ToArray());
                if (json.Length > 1) json.Append(',');
                json.Append(System.FormattableString.Invariant($"\"{p.MidiChannel - 1}\":{{\"wav\":\"{wav}\",\"name\":\"{p.SampleName}\",\"c5\":{p.Sound.C5:0.###},\"loop_start\":{p.Sound.LoopStart}}}"));
            }
            File.WriteAllText(Path.Combine(args[3], name + ".presets.json"), json.Append('}').ToString());
        }
        Console.Write(s.Fence.Text());
        foreach (var p in s.Parts) Console.WriteLine($"  MIDI ch {p.MidiChannel,2} \"{p.Instrument}\": {p.Sound}, {p.Notes} notes, {p.Slides} slides");
        Console.WriteLine($"  {s.Seconds:0.00} s; wrote {name}.mid and {name}.fence.json in {args[3]}");
        return 0;
    }

    private sealed record N(int Tick, int Ch, int Key, int Vel, int Bend, int Range);

    /// <summary>Note-ons with the program, bend and range in force on their channel (the MIDI read back with VRUN's own reader, apart from the back end).</summary>
    private static (List<N> Notes, int[] Prog, int[] Pan) Read(byte[] midi)
    {
        var m = MidiFile.Parse(midi);
        var all = new List<(int Tick, int Seq, MidiEvent E)>();
        int seq = 0;
        foreach (var t in m.Tracks) { int tick = 0; foreach (var e in t) { tick += e.DeltaTime; all.Add((tick, seq++, e)); } }
        all.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Seq.CompareTo(b.Seq));
        var prog = Enumerable.Repeat(-1, 16).ToArray(); var pan = new int[16]; var bend = new int[16]; var range = Enumerable.Repeat(2, 16).ToArray(); var rpn = new int[16];
        var notes = new List<N>();
        foreach (var (tick, _, e) in all)
        {
            if (e.Status >= 0xF0) continue;
            int c = e.Channel;
            switch (e.Command)
            {
                case 0xC0: prog[c] = e.Data1; break;
                case 0xB0:
                    if (e.Data1 == 10) pan[c] = e.Data2;
                    else if (e.Data1 == 101) rpn[c] = (rpn[c] & 0x7F) | (e.Data2 << 7);
                    else if (e.Data1 == 100) rpn[c] = (rpn[c] & ~0x7F) | e.Data2;
                    else if (e.Data1 == 6 && rpn[c] == 0) range[c] = e.Data2;
                    break;
                case 0xE0: bend[c] = (e.Data1 | (e.Data2 << 7)) - 8192; break;
                case 0x90 when e.Data2 > 0: notes.Add(new N(tick, c, e.Data1, e.Data2, bend[c], range[c])); break;
            }
        }
        return (notes, prog, pan);
    }

    private static double Cents(N n) => n.Key * 100 + n.Bend / 8192.0 * n.Range * 100;

    // ---- sv-synthetic ----

    /// <summary>pulse 1 at 25% (two notes, the second +50 cents and panned left), pulse 2 at 75%, the triangle with a two-octave slide, short noise (hits at two pitches and two levels).</summary>
    private static byte[] SvProject() => FlpChecks.Project(150,
        [("P1", "BrokenNes2", FlpChecks.Bn2State(duty: 1, channel: 1, pan: -50)), ("P2", "BrokenNes2", FlpChecks.Bn2State(duty: 3, channel: 2)),
         ("Tri", "BrokenNes2", FlpChecks.Bn2State(channel: 3)), ("Nz", "BrokenNes2", FlpChecks.Bn2State(channel: 4, noiseMode: 1))],
        [(1, [new(0, 48, 72, 0), new(96, 48, 60, 0, Fine: 170, Vel: 64), new(0, 96, 67, 1), new(0, 96, 60, 2), new(24, 48, 36, 2, Slide: true), new(0, 12, 60, 3), new(48, 12, 72, 3, Vel: 32)])],
        [(0, 1, 192)]);

    public static void Synthetic(ImodelReport r)
    {
        var song = FlpSong.Read(SvProject());
        foreach (SvEngine e in Enum.GetValues<SvEngine>())
        {
            var s = SuperVrunBackend.Build(song, "sv", e);
            var (notes, prog, pan) = Read(s.Midi);
            string en = SuperVrunBackend.Names[(int)e];
            r.Expect(prog[0] == 1 && prog[1] == 3 && prog[2] == 4 && prog[3] == 5, $"{en}: programs {prog[0]},{prog[1]},{prog[2]},{prog[3]} (25% = 1, 75% = 3, triangle 4, noise 5 expected)");
            r.Expect(pan[0] == 64 - 32 && pan[1] == 64, $"{en}: CC10 {pan[0]} / {pan[1]} (Pan -50 = 32, centre 64)");
            var p1 = notes.Where(n => n.Ch == 0).ToList();
            r.Expect(p1.Count == 2 && Math.Abs(Cents(p1[0]) - 7200) < 1 && Math.Abs(Cents(p1[1]) - 6050) < 1, $"{en}: pulse 1's pitch {string.Join(", ", p1.Select(Cents))} (7200, 6050 expected)");
            // levels: velocity 100 -> level round(15 x 100/128) = 12 -> 127 sqrt(12/15); velocity 64 -> level 8 (7.5 rounded away) -> 127 sqrt(8/15)
            r.Expect(p1.Count == 2 && p1[0].Vel == (int)Math.Round(127 * Math.Sqrt(12 / 15.0)) && p1[1].Vel == (int)Math.Round(127 * Math.Sqrt(8 / 15.0)), $"{en}: velocities {string.Join(", ", p1.Select(n => n.Vel))} (sqrt of the NES level)");
            var nz = notes.Where(n => n.Ch == 3).ToList();
            int i60 = VrunNesBackend.NoiseIndex(0), i72 = VrunNesBackend.NoiseIndex(1200);
            r.Expect(nz.Count == 2 && nz[0].Key == 64 + i60 && nz[1].Key == 64 + i72, $"{en}: noise keys {string.Join(", ", nz.Select(n => n.Key))} ({64 + i60}, {64 + i72} expected: 64 + period index, short mode)");
            r.Expect(notes.Where(n => n.Ch == 2).All(n => n.Vel == 127), $"{en}: the triangle is not at its one level");
            // the slide: the triangle's bend range covers two octaves
            r.Expect(notes.Where(n => n.Ch == 2).All(n => n.Range >= 24), $"{en}: the triangle's bend range does not cover the two-octave slide");
            bool sn = e is SvEngine.Snes or SvEngine.WideMess;
            r.Expect(s.Fence.Lines.Any(l => l.Rule == "duty" && l.Level == FenceLevel.Note) == !sn, $"{en}: the 75% duty line ({(sn ? "none: SNES plays the NES recordings" : "the 25% patch")})");
            r.Expect(s.Fence.Lines.Any(l => l.Rule == "pan" && l.Level == FenceLevel.Note) == !sn, $"{en}: the pan line");
            r.Expect(s.Fence.Lines.Any(l => l.Rule == "noise" && l.Level == FenceLevel.Note) == !sn, $"{en}: the noise line");
            r.Expect(!s.Fence.AnyOver, $"{en}: an OVER line on a song inside every limit: {string.Join(" | ", s.Fence.Caveats)}");
            r.Expect(s.Fence.Json().StartsWith("{\"format\":\"brokennes-fence\",\"version\":1,"), $"{en}: fence JSON header");
        }
    }

    // ---- sv-fence ----

    private static byte[] Many(int parts, int noises = 0, int key = 60, int noteLen = 96)
    {
        var ch = new List<(string, string, byte[]?)>();
        var ns = new List<FlpChecks.SNote>();
        for (int i = 0; i < parts; i++) { ch.Add(($"P{i}", "BrokenNes2", FlpChecks.Bn2State(channel: 1 + i % 3))); ns.Add(new(0, noteLen, key + i, i)); }
        for (int i = 0; i < noises; i++) { ch.Add(($"N{i}", "BrokenNes2", FlpChecks.Bn2State(channel: 4))); ns.Add(new(0, noteLen, 60, parts + i)); }
        return FlpChecks.Project(120, ch.ToArray(), [(1, ns.ToArray())], [(0, 1, 192)]);
    }

    private static FenceLine? Rule(SuperVrunSong s, string rule) => s.Fence.Lines.FirstOrDefault(l => l.Rule == rule);

    public static void Fence(ImodelReport r)
    {
        foreach (SvEngine e in Enum.GetValues<SvEngine>())
        {
            string en = SuperVrunBackend.Names[(int)e];
            var (voices, sfx, _) = SuperVrunBackend.Budget(e);
            var over = SuperVrunBackend.Build(FlpSong.Read(Many(voices + 1)), "f", e);
            r.Expect(Rule(over, "voices")?.Level == FenceLevel.Over, $"{en}: {voices + 1} parts at once are not over the {voices}-voice limit ({Rule(over, "voices")?.Text})");
            var full = SuperVrunBackend.Build(FlpSong.Read(Many(voices)), "f", e);
            r.Expect(Rule(full, "voices")?.Level == (sfx > 0 ? FenceLevel.Note : FenceLevel.Ok), $"{en}: {voices} parts at once: {Rule(full, "voices")?.Level} ({(sfx > 0 ? "note: the effects' voices" : "ok")} expected)");
            var fits = SuperVrunBackend.Build(FlpSong.Read(Many(voices - sfx)), "f", e);
            r.Expect(Rule(fits, "voices")?.Level == FenceLevel.Ok, $"{en}: {voices - sfx} parts at once are not ok");
            var twoNoise = SuperVrunBackend.Build(FlpSong.Read(Many(1, noises: 2)), "f", e);
            bool sn = e is SvEngine.Snes or SvEngine.WideMess;
            r.Expect(twoNoise.Fence.Lines.Any(l => l.Rule == "noise" && l.Level == FenceLevel.Over) == !sn, $"{en}: two noises at once: {string.Join(" | ", twoNoise.Fence.Lines.Where(l => l.Rule == "noise").Select(l => l.Level))} ({(sn ? "SNES: two channels, no noise line" : "over: one noise voice")})");
            var high = SuperVrunBackend.Build(FlpSong.Read(Many(1, key: 112)), "f", e);
            r.Expect(high.Fence.Lines.Any(l => l.Rule == "range" && l.Level == FenceLevel.Over) == sn, $"{en}: MIDI 112 {(sn ? "is not over SNESMOD's octave 9" : "is over")}");
            var sixteen = SuperVrunBackend.Build(FlpSong.Read(Many(16, noteLen: 1)), "f", e);
            r.Expect(Rule(sixteen, "instruments")?.Level == FenceLevel.Over && SuperVrunBackend.Build(FlpSong.Read(Many(16)), "f", e).Parts.Count == 15, $"{en}: 16 instances are not over the 15 MIDI channels");
            var syn = SuperVrunBackend.Build(FlpSong.Read(FlpChecks.SyntheticProject()), "f", e);
            int instrOver = syn.Fence.Lines.Count(l => l.Rule == "instrument" && l.Level == FenceLevel.Over);
            r.Expect(instrOver == 4, $"{en}: phase 1's synthetic project: {instrOver} instruments over (the sampler, Mix, ROM mode, Game Boy = 4 expected): {string.Join(" | ", syn.Fence.Caveats)}");
            var empty = SuperVrunBackend.Build(FlpSong.Read(FlpChecks.Project(120, [("K", "", null)], [(1, [new(0, 24, 60, 0)])], [(0, 1, 96)])), "f", e);
            r.Expect(Rule(empty, "song")?.Level == FenceLevel.Over, $"{en}: a project with no BrokenNes2 part is not over (NOTHING)");
        }
    }

    // ---- sv-songs ----

    public static void Songs(ImodelReport r)
    {
        var songs = FlpChecks.Songs();
        if (songs.Count == 0) { r.Warn($"NOT RUN: no .flp in {FlpChecks.SongsDir}"); return; }
        foreach (var f in songs)
        {
            var model = FlpSong.Read(File.ReadAllBytes(f));
            string name = Path.GetFileNameWithoutExtension(f);
            foreach (SvEngine e in Enum.GetValues<SvEngine>())
            {
                var s = SuperVrunBackend.Build(model, name, e);
                var (notes, _, _) = Read(s.Midi);
                // independent count: per used instance, the voices that own a stretch of time at a level > 0 (FlpMono is the plugin's rule; the MIDI must hold each such stretch once)
                int want = 0;
                foreach (var ins in model.Instruments.Where(i => i.Bn2 is { Mode: "direct", Console: 0, Channel: >= 0 and <= 3 } && i.Notes.Count > 0).Take(15))
                    want += FlpMono.Resolve([ins]).Segs.Count(sg => sg.V.Level > 0);
                r.Expect(notes.Count == want, $"{name} on {SuperVrunBackend.Names[(int)e]}: {notes.Count} notes in the MIDI, {want} sounding stretches in the model");
                if (e == SvEngine.Snes || e == SvEngine.Md || e == SvEngine.Dos) r.Note($"{name,-12} {s.Fence.Summary}; {notes.Count} notes; {string.Join(" | ", s.Fence.Lines.Where(l => l.Level == FenceLevel.Over).Select(l => l.Text[..Math.Min(70, l.Text.Length)]))}");
                if (FlpChecks.OutDir != null)
                {
                    string d = Path.Combine(FlpChecks.OutDir, SuperVrunBackend.Ids[(int)e]);
                    Directory.CreateDirectory(d);
                    File.WriteAllBytes(Path.Combine(d, name + ".mid"), s.Midi);
                    File.WriteAllText(Path.Combine(d, name + ".fence.json"), s.Fence.Json());
                }
            }
        }
    }
}
