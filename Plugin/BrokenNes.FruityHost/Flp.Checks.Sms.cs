// flp-test's checks of the CTW SMS back end (Music/Flp/CtwSmsBackend.cs): see Flp.Checks.Voc.cs for the family.
//   ctwsms-synthetic  a Master System project written here: the fence (slides and detune played bent, duty not on the PSG, a note under A2, other consoles, the frame rate), no opcode the template's
//                     plain decoders cannot play, the Sega header checksum the patch recomputes = makesms's
//   ctwsms-engine     the test ROM (THE GAME, its title song replaced) in BrokenNes's Master System, frame by frame against VRUN NES's own sound-test ROM with the same streams, its
//                     registers per NMI mapped as nes_apu.c maps them (period, attenuation, noise rate); a semitone-up / next-rate control must not agree
using System.Security.Cryptography;
using BrokenNes2.Music.Flp;
using NesEmulator;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static class FlpSmsChecks
{
    public static readonly FlpChecks.Check[] All =
    [
        new("ctwsms-synthetic", "CTW SMS: a Master System project written here: the converter's streams and every fence rule it breaks (slides, low notes, detune, other consoles)", SmsSynthetic),
        new("ctwsms-preset", "CTW SMS presets: Master System PSG tones / white / periodic noise presets (PRS1 written byte by byte) play as their PSG voice; FM-unit and Game Boy presets as engine approximations (losses listed), damaged and empty presets OVER", SmsPreset),
        new("ctwsms-engine","CTW SMS: the test ROM (the game itself, its title song replaced) plays what VRUN NES plays, through nes_apu.c's mapping, frame by frame", SmsEngine),
    ];

    internal static byte[] SmsProject() => FlpChecks.Project(150,
        [("Kick", "", null),
         ("Lead", "BrokenNes2", FlpChecks.Bn2State(console: 3, place: 0, duty: 1)),
         ("Deep", "BrokenNes2", FlpChecks.Bn2State(console: 3, place: 1)),
         ("Bass", "BrokenNes2", FlpChecks.Bn2State(console: 3, place: 2)),
         ("Hat", "BrokenNes2", FlpChecks.Bn2State(console: 3, place: 3)),
         ("GbLead", "BrokenNes2", FlpChecks.Bn2State(console: 1, place: 0))],
        [(1, [new(0, 48, 72, 1), new(24, 48, 76, 1), new(96, 48, 60, 1, Fine: 170), new(0, 40, 40, 2), new(48, 40, 62, 2, Vel: 64),
              new(0, 80, 60, 3), new(0, 40, 48, 3, Slide: true), new(0, 12, 60, 4), new(48, 12, 66, 4), new(0, 24, 60, 0), new(0, 24, 60, 5)])],
        [(0, 1, 192), (192, 1, 72)]);

    internal static (byte[] Rom, string Manifest) Template()
    {
        string rp = FlpVocChecks.RepoFile(@"Plugin\BrokenNes2.Plugin\Resources\flpctwsms.sms");
        return (File.ReadAllBytes(rp), File.ReadAllText(Path.ChangeExtension(rp, ".txt")));
    }

    private static bool HasBend(byte[] st)
    {
        for (int i = 0; i < st.Length; i++) { if (st[i] == 0xD0 || st[i] == 0xD3) { i++; continue; } if (st[i] is 0xD1 or 0xD2) return true; if (st[i] == 0xFF) return false; }
        return false;
    }

    public static void SmsSynthetic(ImodelReport r)
    {
        var v = CtwSmsBackend.Build(FlpSong.Read(SmsProject()), "synthetic");
        bool Has(FenceLevel f, string rule, string text) => v.Fence.Any(l => l.Level == f && l.Rule == rule && l.Text.Contains(text));
        r.Expect(v.Build != null, "VRUN's converter refused the synthetic song");
        r.Expect(Has(FenceLevel.Ok, "slides", "as VRUN NES plays them"), "no line saying the triangle's slide plays");
        int bassAt = v.Build == null ? -1 : Array.IndexOf(v.Build.Facts.Source, 3);   // "Bass" (the triangle's instance, MIDI channel 3): the converter may move it
        r.Expect(bassAt is >= 0 and < 3 && HasBend(v.Build!.Channels[bassAt]), $"the triangle's slide is not in its stream (NES channel {bassAt}: no $D1/$D2 bend opcode): the slide was dropped");
        r.Expect(v.Build != null && v.Build.Channels[0].Length > 1 && v.Build.Channels[0][0] == 0xD3, "tone 1's Duty 25% did not reach the stream as VRUN's $D3 duty opcode");
        r.Expect(Has(FenceLevel.Note, "duty", "no duty"), "no CHANGED line for tone 1's Duty 25% (the PSG has no duty)");
        r.Expect(Has(FenceLevel.Over, "note range", "under the PSG"), "no OUT line for tone 2's note 40 (under A2)");
        r.Expect(Has(FenceLevel.Ok, "tuning", "played bent"), "no line for the +50-cent note played bent");
        r.Expect(Has(FenceLevel.Over, "instruments", "is a GB instrument"), "no OUT line for the Game Boy instance");
        r.Expect(Has(FenceLevel.Over, "instruments", "is not BrokenNes2"), "no OUT line for the sampler");
        r.Expect(Has(FenceLevel.Note, "timing", "slower"), "no CHANGED frame-rate line");
        r.Expect(!v.Fence.Any(l => l.Text.Contains("Duty 25%")), "VRUN NES's duty caveat leaked into the Master System fence (the PSG has no duty)");
        r.Expect(v.Build != null && Enumerable.Range(0, 4).All(c => CtwSmsBackend.BadOpcode(v.Build.Channels[c], c) == null), "a stream has an opcode the template's decoders cannot play");
        // the refusal still works: an opcode the decoders do not know, a bend on the noise, a duty that is not a square's first opcode
        r.Expect(CtwSmsBackend.BadOpcode([0x40, 0xD4, 0xFF], 0) != null && CtwSmsBackend.BadOpcode([0xD1, 0, 1, 0xFF], 3) != null && CtwSmsBackend.BadOpcode([0x40, 0xD3, 0x70, 0xFF], 0) != null, "the opcode fence lets through bytes the decoders would misread");
        var (tmpl, _) = Template();
        var copy = (byte[])tmpl.Clone(); CtwSmsBackend.SegaChecksum(copy);
        r.Expect(copy.AsSpan().SequenceEqual(tmpl), "the Sega header checksum recomputed on the template differs from makesms's");
        r.Note($"synthetic (Master System): fence {FlpFence.Summary(v.Fence)}");
        foreach (var l in FlpFence.Lines(v.Fence)) r.Note("    " + l);
        // a Game Boy project (with a NES instance): no Master System instance, so its NES instance plays (NES is the inter-console standard, 2026-11-12); without NES instances: NOTHING
        var gbSong = FlpSong.Read(FlpVocChecks.GbProject());
        var gbOnly = CtwSmsBackend.Build(gbSong, "gb-project");
        r.Expect(gbOnly.Fence.Any(l => l.Rule == "console" && l.Level == FenceLevel.Note) && gbOnly.Nes.Parts.Count > 0, $"a Game Boy project with a NES instance: CTW SMS does not play the NES instance ({FlpFence.Summary(gbOnly.Fence)})");
        gbSong.Instruments.RemoveAll(i => i.Bn2 is { Console: 0 });
        var gbNoNes = CtwSmsBackend.Build(gbSong, "gb-only");
        r.Expect(gbNoNes.Fence.Any(l => l.Level == FenceLevel.Over && l.Text.StartsWith("NOTHING")), "a Game Boy project without NES instances: no NOTHING line in the Master System fence");
    }

    public static void SmsPreset(ImodelReport r)
    {
        var psg = PresetFixtures.Synthetic().First(s => s.Name == "psg");
        var opll = PresetFixtures.Synthetic().First(s => s.Name == "opll");
        var gb = PresetFixtures.Synthetic().First(s => s.Name == "gb-dmg");
        var pl = PresetFixtures.Instruments(psg);
        var tones = PresetFixtures.Make(psg, pl.First(i => i.Kind == BrokenNes2.Instruments.InstrumentKind.PsgTones), 0, 1, "tones");
        var noises = pl.Where(i => i.Kind == BrokenNes2.Instruments.InstrumentKind.PsgNoise).ToList();
        r.Expect(noises.Count == 2, $"the PSG fixture has {noises.Count} noise instruments (white + periodic expected)");
        if (noises.Count < 2) return;
        var white = PresetFixtures.Make(psg, noises[0], 0, 1, "white");
        var periodic = PresetFixtures.Make(psg, noises[1], 0, 1, "periodic");
        var fm = PresetFixtures.Make(opll, PresetFixtures.Instruments(opll).First(i => i.Kind is BrokenNes2.Instruments.InstrumentKind.OpllUser or BrokenNes2.Instruments.InstrumentKind.OpllRom), 0, 1, "fm");
        var gbp = PresetFixtures.Make(gb, PresetFixtures.Instruments(gb)[0], 0, 1, "gb");
        byte[] S(BrokenNes2.Presets.PresetData d, bool damage = false) => SvChecks.Prs1(BrokenNes2.Presets.PresetFile.TryWrite(d, out _)!, damage);
        var song = FlpSong.Read(FlpChecks.Project(150,
            [("Tones", "BrokenNes2", FlpChecks.Bn2State(core: 27, console: 3, place: 1, duty: 0, section: S(tones), sectionTag: "PRS1")),
             ("White", "BrokenNes2", FlpChecks.Bn2State(core: 27, console: 3, place: 3, section: S(white), sectionTag: "PRS1")),
             ("Periodic", "BrokenNes2", FlpChecks.Bn2State(core: 27, console: 3, place: 3, section: S(periodic), sectionTag: "PRS1")),
             ("FM", "BrokenNes2", FlpChecks.Bn2State(core: 27, console: 3, place: 0, section: S(fm), sectionTag: "PRS1")),
             ("GB", "BrokenNes2", FlpChecks.Bn2State(core: 25, console: 1, place: 0, section: S(gbp), sectionTag: "PRS1")),
             ("Bad", "BrokenNes2", FlpChecks.Bn2State(core: 27, console: 3, place: 0, section: S(tones, true), sectionTag: "PRS1")),
             ("Empty", "BrokenNes2", FlpChecks.Bn2State(core: 27, console: 3, place: 0))],
            [(1, [new(0, 96, 72, 0), new(48, 96, 76, 0), new(0, 24, 60, 1), new(96, 24, 60, 2), new(0, 48, 60, 3), new(0, 48, 60, 4), new(0, 48, 60, 5), new(0, 48, 60, 6)])],
            [(0, 1, 192)]));
        var v = CtwSmsBackend.Build(song, "presets");
        var P = v.Fence.Where(l => l.Rule == "preset").ToList();
        bool Has(FenceLevel lv, string who, string text) => P.Any(l => l.Level == lv && l.Text.Contains($"\"{who}\"") && l.Text.Contains(text));
        r.Expect(Has(FenceLevel.Ok, "Tones", "PSG tones preset") && Has(FenceLevel.Ok, "Tones", "tone 2"), "the tones preset is not Ok on tone 2 (its instance's channel)");
        r.Expect(Has(FenceLevel.Note, "Tones", "one at a time"), "the tones preset's overlapping notes are not said (the plugin plays 3 at once)");
        r.Expect(Has(FenceLevel.Ok, "White", "noise preset") && !Has(FenceLevel.Note, "White", "PERIODIC"), "the white-noise preset is not Ok on the noise");
        r.Expect(Has(FenceLevel.Note, "Periodic", "PERIODIC"), "the periodic-noise preset is not a Note (the driver writes white noise)");
        r.Expect(Has(FenceLevel.Note, "FM", "FM-unit") && Has(FenceLevel.Note, "FM", "approximated") && Has(FenceLevel.Note, "FM", "lost:"), "the FM-unit preset is not an approximation with its losses");
        r.Expect(Has(FenceLevel.Note, "GB", "GB preset") && Has(FenceLevel.Note, "GB", "approximated on tone 1"), "the Game Boy preset is not an approximation on tone 1");
        r.Expect(Has(FenceLevel.Over, "Bad", "damaged"), "the damaged preset is not OVER");
        r.Expect(Has(FenceLevel.Over, "Empty", "no PRS1"), "the Presets chip without a preset is not OVER");
        var parts = v.Nes.Parts.Select(p => p.Instruments).ToList();
        r.Expect(v.Nes.Parts.Count == 3 && parts.Any(x => x.Contains("Tones")) && parts.Any(x => x.Contains("White") && x.Contains("Periodic")) && parts.Any(x => x.Contains("FM") && x.Contains("GB")), $"the game's parts are {string.Join(" | ", v.Nes.Parts.Select(p => $"{p.NesChannel}:{p.Instruments}"))} (Tones on a tone, White + Periodic on the noise, FM + GB approximated on tone 1 expected)");
        r.Expect(v.Nes.Parts.FirstOrDefault(p => p.Instruments.Contains("Tones"))?.NesChannel == 1, "the tones preset is not on its instance's tone (tone 2)");
        r.Expect(!v.Fence.Any(l => l.Rule == "duty" && l.Level == FenceLevel.Note), "a PSG preset's instance Duty (12.5%) leaked into the duty line: a preset has no duty");
        r.Expect(v.Build != null && Enumerable.Range(0, 4).All(c => CtwSmsBackend.BadOpcode(v.Build.Channels[c], c) == null), "a preset song's stream has an opcode the game's decoders cannot play");
        r.Note($"presets (Master System): fence {FlpFence.Summary(v.Fence)}");
        foreach (var l in FlpFence.Lines(P)) r.Note("    " + l);
    }

    /// <summary>What nes_apu.c makes of a NES latch (the registers as an NMI left them): per tone the PSG period and attenuation, the noise's attenuation and rate.</summary>
    /// <summary>The whole run through nes_apu.c (the triangle's octave is kept per note: the key-ons are the run's).</summary>
    internal static (int[] N, int[] Att, int Rate)[] NesToPsg(MidiRun run, int frames)
    {
        int shift = 0;
        var o = new (int[] N, int[] Att, int Rate)[frames];
        for (int f = 0; f < frames; f++) o[f] = NesToPsg(run.Latch.AsSpan(f * MidiModel.LatchBytes, MidiModel.LatchBytes), f < run.TriKey.Length && run.TriKey[f], ref shift);
        return o;
    }

    internal static (int[] N, int[] Att, int Rate) NesToPsg(ReadOnlySpan<byte> l, bool triKey, ref int triShift)
    {
        var n = new int[3]; var att = new int[4];
        int chn = l[0x15];
        for (int c = 0; c < 2; c++)
        {
            int p = (l[c * 4 + 3] & 7) << 8 | l[c * 4 + 2];
            att[c] = (chn & (1 << c)) == 0 || p < 8 ? 15 : CtwSmsBackend.Vol2Att[l[c * 4] & 15];
            n[c] = CtwSmsBackend.PsgPeriod(p, false).N;
        }
        int pt = (l[0xB] & 7) << 8 | l[0xA];
        att[2] = (chn & 4) == 0 || (l[8] & 0x7F) == 0 || pt < 2 ? 15 : 3;
        n[2] = CtwSmsBackend.TrianglePsg(pt, triKey, ref triShift);
        att[3] = (chn & 8) == 0 ? 15 : CtwSmsBackend.Vol2Att[l[0xC] & 15];
        return (n, att, CtwSmsBackend.NoiseRate(l[0xE] & 15));
    }

    internal static (int[] Period, int[] Att, int Noise)[] RunSms(byte[] rom, int frames)
    {
        var kind = ConsoleKind.MasterSystem;
        var s = ConsoleSessions.Create(kind, rom, CoreCatalog.Default(kind, CoreSlot.Cpu), CoreCatalog.Default(kind, CoreSlot.Ppu), CoreCatalog.Default(kind, CoreSlot.Apu), null, "flp.sms");
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

    private static int Agree((int[] N, int[] Att, int Rate) w, (int[] Period, int[] Att, int Noise) g, int c)
        => w.Att[c] == 15 ? (g.Att[c] == 15 ? 1 : 0) : g.Att[c] == w.Att[c] && (c == 3 ? (g.Noise & 3) == w.Rate : g.Period[c] == w.N[c]) ? 1 : 0;

    public static void SmsEngine(ImodelReport r)
    {
        var (tmpl, man) = Template();
        var nesT = MidiChecks.BundledTemplate();
        var (_, _, mlb) = MidiFixtures.Bundled();
        var all = new List<(string, FlpSongModel)> { ("synthetic", FlpSong.Read(SmsProject())), ("slides", FlpSong.Read(FlpSlideChecks.Project(3))) };   // slides: the triangle's octave through slides crossing N = 1023
        all.AddRange(FlpVocChecks.UserSongs(3));
        foreach (var (name, s) in all)
        {
            var v = CtwSmsBackend.Build(s, name);
            var rom = CtwSmsBackend.Patch(tmpl, man, v, out var why);
            r.Expect(rom != null, $"{name}: the test ROM refused the song: {why}");
            if (rom == null || v.Build == null) continue;
            // the reference is VRUN NES's template: a stream past its room (CTW SMS has more) cannot be played there
            if (v.Build.Channels.Any(x => x.Length > nesT.Region)) { r.Note($"{name}: not compared: a stream ({string.Join(" + ", v.Build.Channels.Select(x => x.Length))}) is past VRUN NES's room of {nesT.Region}"); continue; }
            int n = Math.Min(v.Build.Facts.LoopFrames, 1500);
            var nes = MidiRun.Of(nesT.Patch(v.Build.Channels, v.Build.Facts.LoopFrames, name), n + 60, mlb);
            var want = NesToPsg(nes, n + 60);
            var run = RunSms(rom, n + 1500);
            int nf = Enumerable.Range(0, want.Length).FirstOrDefault(f => want[f].Att.Any(a => a != 15), -1);
            int sf = Enumerable.Range(31, run.Length - 31).FirstOrDefault(f => run[f].Att.Any(a => a != 15), -1);
            if (nf < 0 || sf < 0) { r.Expect(false, $"{name}: no sound to line up (NES first sound {nf}, Master System {sf})"); continue; }
            int best = -1, bs = 0, bn = 0;
            for (int dn = -6; dn <= 6; dn++)
                for (int ds = -6; ds <= 6; ds++)
                {
                    int a0 = nf + dn, b0 = sf + ds, a = 0;
                    if (a0 < 0 || b0 < 0) continue;
                    for (int f = 0; f < 300 && a0 + f < want.Length && b0 + f < run.Length; f++) for (int c = 0; c < 4; c++) a += Agree(want[a0 + f], run[b0 + f], c);
                    if (a > best) { best = a; bs = b0; bn = a0; }
                }
            int len = Math.Min(n, Math.Min(want.Length - bn, run.Length - bs));
            var parts = new List<string>();
            for (int c = 0; c < 4; c++)
            {
                int agree = 0, sounding = 0, negAgree = 0; string first = "";
                for (int f = 0; f < len; f++)
                {
                    var w = want[bn + f]; var g = run[bs + f];
                    int a = Agree(w, g, c);
                    agree += a;
                    if (w.Att[c] != 15)
                    {
                        sounding++;
                        var nn = (int[])w.N.Clone(); int rate = w.Rate;
                        if (c < 3) nn[c] = Math.Max(1, (int)Math.Round(w.N[c] / Math.Pow(2, 1 / 12.0))); else rate = (w.Rate + 1) % 3;
                        negAgree += Agree((nn, w.Att, rate), g, c);
                    }
                    if (a == 0 && first.Length == 0) first = $"frame {f}: NES -> period {(c < 3 ? w.N[c] : -1)} att {w.Att[c]} rate {w.Rate}; PSG period {(c < 3 ? g.Period[c] : -1)} att {g.Att[c]} noise {g.Noise}";
                }
                if (sounding == 0) continue;
                double pct = agree / (double)Math.Max(1, len), negPct = negAgree / (double)sounding;
                r.Expect(pct >= 0.98, $"{name} {CtwSmsBackend.Names[c]}: the game plays VRUN NES's registers (mapped) on {pct:P1} of {len} frames (98% wanted); first {first}");
                r.Expect(sounding < 20 || negPct < 0.5, $"{name} {CtwSmsBackend.Names[c]}: the negative control agrees on {negPct:P0} of the sounding frames");
                parts.Add($"{CtwSmsBackend.Names[c]} {pct:P1} (control {negPct:P0})");
            }
            r.Note($"{name,-14} ROM sha256 {Convert.ToHexString(SHA256.HashData(rom))[..16].ToLowerInvariant()}, song from Master System frame {bs} (NES {bn}), {len} frames: {string.Join(", ", parts)}; fence {FlpFence.Summary(v.Fence)}");
            if (FlpChecks.OutDir != null)
            {
                Directory.CreateDirectory(FlpChecks.OutDir);
                File.WriteAllBytes(Path.Combine(FlpChecks.OutDir, name + ".ctw.sms"), rom);
                File.WriteAllLines(Path.Combine(FlpChecks.OutDir, name + ".ctw-sms.fence.txt"), FlpFence.Lines(v.Fence));
            }
        }
    }
}
