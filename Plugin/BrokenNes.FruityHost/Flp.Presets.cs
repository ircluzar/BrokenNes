// flp-test checks for the presets in the .flp pipeline (Music/Flp/FlpPresetSound.cs + the Super VRUN back end's preset path):
//   sv-preset-snes     every SNES fixture instrument, clean and corrupted, as a preset in a project written here (the PRS1 section assembled byte by byte, its own CRC): the back end gives the
//                      instance the preset program and a sample; the sample = an independent BRR decode (RecRoot.Decode), the loop at the loop pointer's block, C5 from the tuning; the plugin's own
//                      render of middle C against the sample played at C5 (spectral similarity, printed)
//   sv-preset-engines  a Genesis FM, a Genesis DAC, a Game Boy, a NES and a damaged preset: what each engine's fence says (SNES Ok, MD Note = DAC, DOS / 95 OVER), rendered samples sound and loop,
//                      a damaged / unplayable preset is OVER (never silently dropped), the sound-RAM rule
using BrokenNes2.Instruments;
using BrokenNes2.Music.Flp;
using BrokenNes2.Presets;
using BrokenNes2.Record;

namespace BrokenNes.FruityHost;

internal static partial class SvChecks
{
    /// <summary>A PRS1 section written here byte by byte (u8 version 1, u8 flags 0, u16 0, u32 length, the preset file, u32 CRC-32 zip of everything before), apart from PresetSection.Build.</summary>
    internal static byte[] Prs1(byte[] file, bool damage = false)
    {
        var b = new List<byte> { 1, 0, 0, 0 };
        b.AddRange(BitConverter.GetBytes((uint)file.Length));
        b.AddRange(file);
        uint c = 0xFFFFFFFF;
        foreach (byte x in b) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        c ^= 0xFFFFFFFF;
        if (damage) c ^= 1;
        b.AddRange(BitConverter.GetBytes(c));
        return b.ToArray();
    }

    private static readonly int[] ChipOf = [24, 25, 26, 27, 28];       // PRE-NES, PRE-GB, PRE-SNES, PRE-SMS, PRE-MD (ConsoleId order NES, GB, SNES, SMS, MD)

    private static byte[] PresetProject(params (string Name, PresetData D, bool Damage)[] ps)
    {
        var ch = new List<(string, string, byte[]?)>();
        var ns = new List<FlpChecks.SNote>();
        for (int i = 0; i < ps.Length; i++)
        {
            var file = PresetFile.TryWrite(ps[i].D, out string? why) ?? throw new InvalidOperationException(why);
            int con = (int)ps[i].D.Console;
            ch.Add((ps[i].Name, "BrokenNes2", FlpChecks.Bn2State(core: ChipOf[con], console: con, section: Prs1(file, ps[i].Damage), sectionTag: "PRS1")));
            ns.Add(new(0, 96, 60, i)); ns.Add(new(96, 48, 67, i));
        }
        return FlpChecks.Project(120, ch.ToArray(), [(1, ns.ToArray())], [(0, 1, 192)]);
    }

    /// <summary>Cosine similarity of the magnitude spectra (Hann, 1024-point DFT, bins up to 8 kHz) of two windows.</summary>
    internal static double Similarity(float[] a, float[] b, int at)
    {
        const int n = 1024;
        double[] Mag(float[] x)
        {
            var m = new double[256];
            for (int k = 1; k < 256; k++)
            {
                double re = 0, im = 0;
                for (int i = 0; i < n; i++) { double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n), v = (at + i < x.Length ? x[at + i] : 0) * w; re += v * Math.Cos(2 * Math.PI * k * i / n); im -= v * Math.Sin(2 * Math.PI * k * i / n); }
                m[k] = Math.Sqrt(re * re + im * im);
            }
            return m;
        }
        var ma = Mag(a); var mb = Mag(b);
        double d = 0, na = 0, nb = 0;
        for (int k = 0; k < 256; k++) { d += ma[k] * mb[k]; na += ma[k] * ma[k]; nb += mb[k] * mb[k]; }
        return na == 0 || nb == 0 ? 0 : d / Math.Sqrt(na * nb);
    }

    /// <summary>The exported sample played the way a sampler plays it at middle C: step C5 / 32000, linear, looping at LoopStart.</summary>
    internal static float[] PlayAtC5(FlpPresetPcm s, int count)
    {
        var o = new float[count];
        double pos = 0, step = s.C5 / FlpPresetPcm.Rate;
        for (int i = 0; i < count; i++)
        {
            if (pos >= s.Pcm.Length) { if (!s.Loops) break; pos = s.LoopStart + (pos - s.Pcm.Length); }
            int a = (int)pos; double fr = pos - a; int bI = a + 1 < s.Pcm.Length ? a + 1 : s.Loops ? s.LoopStart : a;
            o[i] = (float)((s.Pcm[a] * (1 - fr) + s.Pcm[bI] * fr) / 32768.0);
            pos += step;
        }
        return o;
    }

    public static void PresetSnes(ImodelReport r)
    {
        var src = PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
        var sims = new List<double>();
        int built = 0;
        foreach (var inst0 in PresetFixtures.Instruments(src))
            foreach (int steps in new[] { 0, 3 })
                foreach (int tune in new[] { 0, 700 })
                {
                    var inst = PresetFixtures.Instruments(src).First(i => i.Index == inst0.Index);
                    if (tune != 0) inst.Field("tune.cents")!.Value = tune;
                    PresetData d;
                    try { d = PresetFixtures.Make(src, inst, steps, 77, $"snes {inst.Index} {steps} {tune}"); } catch { continue; }
                    var song = FlpSong.Read(PresetProject(("Pre", d, false)));
                    var pre = song.Instruments[0].Bn2?.Preset;
                    r.Expect(pre is { Ok: true }, $"slot {inst.Index}: the reader did not carry the PRS1 preset ({pre?.Problem})");
                    if (pre is not { Ok: true }) continue;
                    var played = PresetPlayable.Build(PresetFile.Read(pre.File).Data!, out _);
                    if (played == null || played.CannotPlay != null) { r.Note($"slot {inst.Index} steps {steps}: the plugin itself cannot play it ({played?.CannotPlay}): skipped"); continue; }
                    foreach (var e in new[] { SvEngine.Snes, SvEngine.WideMess })
                    {
                        var s = SuperVrunBackend.Build(song, "p", e);
                        r.Expect(s.Presets.Count == 1 && s.Parts.Count == 1, $"{e} slot {inst.Index}: {s.Presets.Count} preset samples, {s.Parts.Count} parts (1 and 1 expected): {string.Join(" | ", s.Fence.Caveats)}");
                        if (s.Presets.Count != 1) continue;
                        var (_, prog, _) = Read(s.Midi);
                        r.Expect(prog[0] == SuperVrunBackend.PresetProgram, $"{e} slot {inst.Index}: program {prog[0]} (the preset program {SuperVrunBackend.PresetProgram} expected)");
                        r.Expect(s.Fence.Lines.Any(l => l.Rule == "preset" && l.Level == FenceLevel.Ok) && s.Fence.Lines.Any(l => l.Rule == "envelope" && l.Level == FenceLevel.Note), $"{e} slot {inst.Index}: the preset / envelope fence lines");
                    }
                    var snd = SuperVrunBackend.Build(song, "p", SvEngine.Snes).Presets.FirstOrDefault()?.Sound;
                    if (snd == null) continue;
                    built++;
                    // the sample against an independent decode of the preset's own BRR (when it ends and loops inside itself, as a clean sample does)
                    var now = played.Instrument;
                    var brr = now.Blobs[0].Data;
                    int loopOff = now.Get("loop.addr") - now.Origin;
                    bool inside = played.Overrun == 0 && loopOff >= 0 && loopOff < brr.Length && loopOff % 9 == 0;
                    if (inside)
                    {
                        var want = RecRoot.Decode(brr, loopOff, Math.Min(snd.Pcm.Length, 4000), out bool loops);
                        int n = Math.Min(want.Length, snd.Pcm.Length);
                        r.Expect(n > 0 && want.AsSpan(0, n).SequenceEqual(snd.Pcm.AsSpan(0, n)), $"slot {inst.Index} steps {steps}: the sample differs from RecRoot's decode of the preset's BRR");
                        bool lastLoops = brr.Length >= 9 && (brr[^9] & 3) == 3;
                        if (lastLoops && snd.Pcm.Length == brr.Length / 9 * 16)
                            r.Expect(snd.LoopStart == loopOff / 9 * 16, $"slot {inst.Index} steps {steps}: loop at sample {snd.LoopStart}, the loop pointer's block is sample {loopOff / 9 * 16}");
                    }
                    double c5 = Math.Min(32000 * Math.Pow(2, now.Get("tune.cents") / 1200.0), 32000.0 * 0x3FFF / 4096);     // the pitch register tops out at $3FFF
                    r.Expect(Math.Abs(snd.C5 / c5 - 1) < 0.0005, $"slot {inst.Index} tune {now.Get("tune.cents")}: C5 {snd.C5:0.0} Hz, {c5:0.0} expected");
                    // the plugin's own render vs the sample played at C5 (the ADSR scales the level, the spectrum is the sample's)
                    var plug = FlpPresetSound.PluginRender(pre, 1.0);
                    var raw = PlayAtC5(snd, plug.Length);
                    // the S-DSP's Gaussian interpolation at step $1000 (offset 0: weights 370, 1305, 374 of 2048, DSP_SFC.Gauss): what the game's DSP (and the plugin's) does to any sample
                    var mine = new float[raw.Length];
                    for (int i = 1; i + 1 < raw.Length; i++) mine[i] = (370 * raw[i - 1] + 1305 * raw[i] + 374 * raw[i + 1]) / 2048f;
                    double plugRms = Math.Sqrt(plug.Skip(24000).Take(1024).Average(v => (double)v * v));
                    if (snd.Noise || snd.Pmod) r.Note($"slot {inst.Index} steps {steps}: noise / pitch-modulation flag: the plugin plays the noise generator, not compared");
                    else if (plugRms < 1e-3) r.Note($"slot {inst.Index} steps {steps}: the plugin's own render is silent at 0.75 s (its envelope): not compared");
                    else if (snd.Loops || snd.Pcm.Length > 4000)
                    {
                        int at = snd.Loops ? 24000 : Math.Max(0, Math.Min(3000, (int)(snd.Pcm.Length * FlpPresetPcm.Rate / snd.C5) - 1024));      // a sample that ends: a window it fills
                        double sim = Similarity(plug, mine, at);
                        if (sim <= 0.9 && Environment.GetEnvironmentVariable("BN_PS_DUMP") is { Length: > 0 } dump)
                        {
                            File.WriteAllBytes(Path.Combine(dump, $"plug{inst.Index}_{steps}_{tune}.f32"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(plug.AsSpan()).ToArray());
                            File.WriteAllBytes(Path.Combine(dump, $"mine{inst.Index}_{steps}_{tune}.f32"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(mine.AsSpan()).ToArray());
                        }
                        if (sim <= 0.9) r.Note($"slot {inst.Index} steps {steps}: {snd.Pcm.Length} samples, loop {snd.LoopStart}, window {at}, plugin rms {plugRms:0.0000}, flags noise {snd.Noise} pmod {snd.Pmod} echo {snd.Echo}, {snd.Envelope}");
                        sims.Add(sim);
                        r.Expect(sim > (steps == 0 ? 0.9 : 0.7), $"slot {inst.Index} steps {steps} tune {tune}: the plugin's middle C and the sample's are only {sim:0.000} alike");      // (corrupted: a 64-sample screech under a slow attack: the envelope's quantisation at low levels adds high partials the sample alone lacks; measured 0.75)
                    }
                }
        r.Expect(built >= 8, $"only {built} SNES presets became samples");
        if (sims.Count > 0) r.Note($"SNES presets: {built} samples; plugin render vs sample at C5, spectral similarity min {sims.Min():0.0000}, mean {sims.Average():0.0000} over {sims.Count}");
    }

    /// <summary>The other back ends: Vaults GB plays a Game Boy wave preset's wave RAM as its wave table and a pulse preset's duty; Vaults GG and VRUN NES say OVER for presets.</summary>
    public static void PresetOthers(ImodelReport r)
    {
        var gb = PresetFixtures.Synthetic().First(s => s.Name == "gb-dmg");
        var md = PresetFixtures.Synthetic().First(s => s.Name == "opn");
        var waveInst = PresetFixtures.Instruments(gb).FirstOrDefault(i => i.Kind == InstrumentKind.GbWave);
        r.Expect(waveInst != null, "the Game Boy fixture has no wave instrument");
        if (waveInst == null) return;
        var wave = PresetFixtures.Make(gb, waveInst, 2, 5, "gb wave");                  // corrupted: its own wave RAM, not the triangle
        var pulse = PresetFixtures.Make(gb, PresetFixtures.Instruments(gb).First(i => i.Kind == InstrumentKind.GbPulse1), 0, 1, "gb pulse");
        var fm = PresetFixtures.Make(md, PresetFixtures.Instruments(md).First(i => i.Kind == InstrumentKind.OpnPatch), 0, 1, "fm");
        var song = FlpSong.Read(PresetProject(("Wave", wave, false), ("Pulse", pulse, false), ("FM", fm, false)));
        var want = PresetPlayable.Build(wave, out _)!.Instrument.Blobs[0].Data.Take(16).ToArray();
        var v = VocGbBackend.Build(song, "p");
        r.Expect(v.Waves.Any(w => w.AsSpan().SequenceEqual(want)), $"Vaults GB: the wave preset's RAM {Convert.ToHexString(want)} is not a wave table ({string.Join(",", v.Waves.Select(Convert.ToHexString))})");
        r.Expect(!v.Waves.Any(w => w.AsSpan().SequenceEqual(VocGbBackend.TriangleWave)) || want.AsSpan().SequenceEqual(VocGbBackend.TriangleWave), "Vaults GB: the triangle is still in the song (the wave preset was not used)");
        r.Expect(v.Fence.Count(l => l.Rule == "preset" && l.Level == FenceLevel.Ok) == 3, $"Vaults GB: preset lines {string.Join(" | ", v.Fence.Where(l => l.Rule == "preset").Select(l => l.Level + " " + l.Text))}");
        r.Expect(v.Fence.Any(l => l.Rule == "preset" && l.Level == FenceLevel.Note && l.Text.Contains("\"FM\"") && l.Text.Contains("approximated") && l.Text.Contains("lost:")), "Vaults GB: the Genesis preset is not an approximation with its losses");
        var g = VocGbBackend.Build(song, "p", VocTarget.Gg);
        r.Expect(g.Fence.Count(l => l.Rule == "preset" && l.Text.Contains("approximated")) == 3 && !g.Fence.Any(l => l.Rule == "preset" && l.Level == FenceLevel.Over), "Vaults GG: the presets are not engine approximations");
        var n = VrunNesBackend.Build(song, "p");
        r.Expect(n.Caveats.Count(c => c.Contains("approximated")) == 3 && !n.Caveats.Any(c => c.StartsWith("OVER") && c.Contains("preset")) && n.Parts.Count == 2, $"VRUN NES: presets not approximated ({n.Parts.Count} parts: wave on the triangle, pulse + FM on square 1 expected)");
        r.Note($"Vaults GB: wave tables {string.Join(", ", v.Waves.Select(Convert.ToHexString))}");
    }

    public static void PresetEngines(ImodelReport r)
    {
        var md = PresetFixtures.Synthetic().First(s => s.Name == "opn");
        var gb = PresetFixtures.Synthetic().First(s => s.Name == "gb-dmg");
        var nes = PresetFixtures.Synthetic().First(s => s.Name == "nes");
        var snes = PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
        var fm = PresetFixtures.Make(md, PresetFixtures.Instruments(md).First(i => i.Kind == InstrumentKind.OpnPatch), 0, 1, "fm");
        var dac = PresetFixtures.Make(md, PresetFixtures.Instruments(md).First(i => i.Kind == InstrumentKind.OpnDac), 0, 1, "dac");
        var gbw = PresetFixtures.Make(gb, PresetFixtures.Instruments(gb)[0], 0, 1, "gb");
        var np = PresetFixtures.Make(nes, PresetFixtures.Instruments(nes)[0], 0, 1, "nes");
        var sp = PresetFixtures.Make(snes, PresetFixtures.Instruments(snes)[1], 0, 1, "snes");
        var song = FlpSong.Read(PresetProject(("FM", fm, false), ("DAC", dac, false), ("GB", gbw, false), ("NES", np, false), ("SNES", sp, false), ("Bad", sp, true)));
        r.Expect(song.Caveats.Any(c => c.Contains("\"Bad\"") && c.Contains("PRS1")), "the reader says nothing of the damaged PRS1 section");
        foreach (SvEngine e in Enum.GetValues<SvEngine>())
        {
            var s = SuperVrunBackend.Build(song, "e", e);
            string en = SuperVrunBackend.Names[(int)e];
            r.Expect(s.Presets.Count == 4, $"{en}: {s.Presets.Count} preset samples (FM, DAC, Game Boy, SNES = 4 expected)");
            var pl = s.Fence.Lines.Where(l => l.Rule == "preset").ToList();
            r.Expect(pl.Count(l => l.Level == FenceLevel.Over && l.Text.Contains("\"Bad\"")) == 1, $"{en}: the damaged preset is not OVER");
            r.Expect(pl.Count(l => l.Level == FenceLevel.Over && l.Text.Contains("\"NES\"")) == 1, $"{en}: the NES preset (not rendered) is not OVER");
            var good = s.Fence.Lines.Where(l => l.Rule == "preset").Where(l => !l.Text.Contains("\"Bad\"") && !l.Text.Contains("\"NES\"")).ToList();
            var want = e is SvEngine.Snes or SvEngine.WideMess ? FenceLevel.Ok : e == SvEngine.Md ? FenceLevel.Note : FenceLevel.Over;
            r.Expect(good.Count == 4 && good.All(l => l.Level == want), $"{en}: the playable presets' lines are {string.Join(",", good.Select(l => l.Level))} ({want} expected)");
            if (e == SvEngine.Md) r.Expect(good.All(l => l.Text.Contains("DAC")), $"{en}: the Genesis lines do not say DAC");
            foreach (var p in s.Presets)
            {
                double rms = Math.Sqrt(p.Sound.Pcm.Average(v => (double)v * v)) / 32768;
                r.Expect(rms > 0.01, $"{en} {p.Instrument}: the sample is silent (rms {rms:0.0000})");
                r.Expect((p.Sound.Loops || p.Sound.Exact) && p.SampleName == $"pcm{p.MidiChannel}", $"{en} {p.Instrument}: loops {p.Sound.Loops}, name {p.SampleName}");
                if (e == SvEngine.Snes && !p.Sound.Exact)
                {
                    // a rendered loop: 10 periods of middle C at 128 samples each, on BRR block boundaries
                    r.Expect(p.Sound.LoopStart % 16 == 0 && p.Sound.Pcm.Length % 16 == 0, $"{p.Instrument}: loop {p.Sound.LoopStart} / length {p.Sound.Pcm.Length} not on 16-sample BRR blocks");
                    var pcm = p.Sound.Pcm; int a = p.Sound.LoopStart;
                    double jump = Math.Abs(pcm[^1] - (double)pcm[a]) / 32768;
                    var held = PlayAtC5(p.Sound, 96000);
                    double keep = Similarity(held, held, 28000) > 0 ? Similarity(held.Skip(28000).ToArray(), held.Skip(80000).ToArray(), 0) : 0;
                    r.Note($"{p.Instrument}: rendered by the plugin's engine, loop join step {jump:0.000}; a 3 s note: its last second against the render's own end, spectral similarity {keep:0.0000}");
                    double loopRms = Math.Sqrt(pcm.Skip(a).Average(v => (double)v * v)) / 32768;
                    if (loopRms < 1e-3) r.Note($"{p.Instrument}: its envelope has died by the loop (as the plugin's held note): a long note is silent after about 1 s in both");
                    else r.Expect(keep > 0.95, $"{p.Instrument}: a held note drifts from the render once it loops ({keep:0.000})");
                }
            }
        }
        // with --out: the projects the test builds use (a SNES preset and a Genesis FM preset playing a melody, middle C held first)
        if (FlpChecks.OutDir != null)
        {
            Directory.CreateDirectory(FlpChecks.OutDir);
            var loopSnes = PresetFixtures.Make(snes, PresetFixtures.Instruments(snes)[0], 0, 1, "snes loop");
            foreach (var (n, d) in new[] { ("preset snes", loopSnes), ("preset fm", fm) })
            {
                int con = (int)d.Console;
                var st = FlpChecks.Bn2State(core: ChipOf[con], console: con, section: Prs1(PresetFile.TryWrite(d, out _)!), sectionTag: "PRS1");
                FlpChecks.SNote[] mel = [new(0, 192, 60, 0), new(192, 48, 64, 0), new(240, 48, 67, 0), new(288, 96, 72, 0)];
                File.WriteAllBytes(Path.Combine(FlpChecks.OutDir, n + ".flp"), FlpChecks.Project(120, [("Preset", "BrokenNes2", st)], [(1, mel)], [(0, 1, 384)]));
            }
            r.Note($"wrote preset snes.flp and preset fm.flp in {FlpChecks.OutDir}");
        }
        // the sound-RAM rule
        var fr = new FenceReport("t");
        SuperVrunBackend.SoundRamFence(fr, SuperVrunBackend.SongAram + 1);
        r.Expect(fr.AnyOver, "presets bigger than the song's sound RAM are not OVER");
        fr = new FenceReport("t"); SuperVrunBackend.SoundRamFence(fr, 4000);
        r.Expect(!fr.AnyOver && fr.Lines.Single().Level == FenceLevel.Ok, "a small preset is not Ok on sound RAM");
        // a preset instance with a high note: over the S-DSP's pitch ceiling
        var hi = FlpSong.Read(FlpChecks.Project(120, [("Hi", "BrokenNes2", FlpChecks.Bn2State(core: 26, console: 2, section: Prs1(PresetFile.TryWrite(sp, out _)!), sectionTag: "PRS1"))], [(1, [new(0, 24, 100, 0)])], [(0, 1, 96)]));
        var hs = SuperVrunBackend.Build(hi, "h", SvEngine.Snes);
        r.Expect(hs.Fence.Lines.Any(l => l.Rule == "range" && l.Level == FenceLevel.Over), "a preset note above the S-DSP's pitch ceiling is not OVER");
    }

    /// <summary>preset-approx: the engine approximation on the register engines. A NES pulse preset (duty 12.5%, decay envelope period 3, sweep down) on VRUN NES: CC 14 = 0,
    /// CC 7 falling steps inside one note, bends from the sweep, a song VRUN's converter takes; on Vaults GG: falling "vol" steps and "freq" steps. A Game Boy pulse preset
    /// (envelope down from 15, pace 2) as falling "vol" steps on Vaults GB / GG. Deliberate bugs: steps dropped, envelope read as held.</summary>
    public static void PresetApprox(ImodelReport r)
    {
        var nes = PresetFixtures.Synthetic().First(s => s.Name == "nes");
        var gb = PresetFixtures.Synthetic().First(s => s.Name == "gb-dmg");
        var pulse = PresetFixtures.Instruments(nes).First(i => i.Kind == InstrumentKind.NesPulse1);
        void Set(Instrument i, string id, int v) { var f = i.Field(id); r.Expect(f != null, $"no field {id}"); if (f != null) f.Value = v; }
        Set(pulse, "duty", 0); Set(pulse, "const", 0); Set(pulse, "vol", 3); Set(pulse, "halt", 0); Set(pulse, "sweep.on", 1); Set(pulse, "sweep.period", 3); Set(pulse, "sweep.neg", 1); Set(pulse, "sweep.shift", 4);
        var np = PresetFixtures.Make(nes, pulse, 0, 1, "nes decay");
        var gbp = PresetFixtures.Instruments(gb).First(i => i.Kind == InstrumentKind.GbPulse1);
        Set(gbp, "duty", 1); Set(gbp, "env.vol", 15); Set(gbp, "env.dir", 0); Set(gbp, "env.pace", 2);
        var gp = PresetFixtures.Make(gb, gbp, 0, 1, "gb decay");
        var song = FlpSong.Read(PresetProject(("NesP", np, false), ("GbP", gp, false)));
        var songN = FlpSong.Read(PresetProject(("NesP", np, false)));          // alone: on one channel the newest note wins
        var ap = song.Instruments.Where(i => i.Bn2 != null).Select(i => FlpPresetApprox.Of(i.Bn2!, out _)).ToList();
        r.Expect(ap.Count == 2 && ap.All(a => a != null), "the presets cannot be read as approximations");
        if (ap.Count != 2 || ap.Any(a => a == null)) return;
        var a0 = ap[0]!; var a1 = ap[1]!;
        r.Expect(a0.Duty == 0 && a0.Level != null && a0.Pitch != null && a0.LevelAt(0) == 1 && a0.LevelAt(30) == 0, $"NES approximation: duty {a0.Duty}, level {(a0.Level == null ? "held" : string.Join(",", a0.Level.Take(8).Select(x => x.ToString("0.00"))))}, pitch {(a0.Pitch == null ? "none" : "steps")}");
        r.Expect(a1.Duty == 1 && a1.Level != null && a1.LevelAt(0) == 1 && a1.LevelAt(60) < 0.1, "Game Boy approximation: duty 25% and a falling envelope expected");

        var n = VrunNesBackend.Build(songN, "a");
        var mf = global::Vrun.Audio.MidiFile.Parse(n.Midi);
        var evs = mf.Tracks.SelectMany(t => t).ToList();
        var cc7 = evs.Where(e => (e.Status & 0xF0) == 0xB0 && e.Data1 == 7).Select(e => e.Data2).ToList();
        int bendsN = evs.Count(e => (e.Status & 0xF0) == 0xE0);
        r.Expect(evs.Any(e => (e.Status & 0xF0) == 0xB0 && e.Data1 == global::Vrun.Audio.Midi2Nes.DutyCc && e.Data2 == 0), "VRUN NES: the preset's duty 12.5% is not CC 14 = 0");
        r.Expect(cc7.Zip(cc7.Skip(1)).Count(p => p.Second < p.First) >= 6, $"VRUN NES: the decay is not falling CC 7 steps ({string.Join(",", cc7.Take(20))})");
        r.Expect(bendsN >= 4, $"VRUN NES: the sweep made {bendsN} bends");
        r.Expect(n.Caveats.Any(c => c.Contains("\"NesP\"") && c.Contains("approximated on square 1") && c.Contains("decay envelope") && c.Contains("sweep")), "VRUN NES: the fence does not say what the preset keeps");
        var conv = BrokenNes2.Music.SongConvert.Convert(n.Midi, out var why);
        r.Expect(conv != null, $"VRUN's converter refused the approximated song: {why}");
        foreach (var t in new[] { VocTarget.Gg, VocTarget.Gb })
        {
            var v = VocGbBackend.Build(t == VocTarget.Gg ? songN : song, "a", t);
            var vols = v.Ch[0].Concat(v.Ch[1]).Where(e => e.Op == "vol").Select(e => e.Value).ToList();
            r.Expect(vols.Zip(vols.Skip(1)).Count(p => p.Second < p.First) >= 6, $"Vaults {t}: the envelopes are not falling vol steps ({string.Join(",", vols.Take(24))})");
            if (t == VocTarget.Gg) r.Expect(v.Ch[0].Concat(v.Ch[1]).Count(e => e.Op == "freq") >= 4, "Vaults GG: the sweep made no freq steps");
            r.Note($"Vaults {t}: {vols.Count} vol events ({string.Join(",", vols.Take(16))}...); fence {FlpFence.Summary(v.Fence)}");
        }
        r.Note($"VRUN NES: {cc7.Count} CC 7 steps ({string.Join(",", cc7.Take(16))}...), {bendsN} bends");
        foreach (var c in n.Caveats.Where(c => c.Contains("approximated"))) r.Note("    " + c);
    }
}
