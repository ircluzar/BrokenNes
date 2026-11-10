// flp-test's checks of the CTW SMS back end (Music/Flp/CtwSmsBackend.cs): see Flp.Checks.Voc.cs for the family.
//   ctwsms-synthetic  a Master System project written here: the fence (slides not played, a note under A2, a detuned note, other consoles, the frame rate), no opcode the game's
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
        new("ctwsms-engine", "CTW SMS: the test ROM (the game itself, its title song replaced) plays what VRUN NES plays, through nes_apu.c's mapping, frame by frame", SmsEngine),
    ];

    internal static byte[] SmsProject() => FlpChecks.Project(150,
        [("Kick", "", null),
         ("Lead", "BrokenNes2", FlpChecks.Bn2State(console: 3, place: 0)),
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

    private static bool PlainOnly(byte[] st)
    {
        for (int i = 0; i < st.Length; i++) { if (st[i] == 0xD0) { i++; continue; } if (st[i] > 0xD0 && st[i] != 0xFF) return false; }
        return true;
    }

    public static void SmsSynthetic(ImodelReport r)
    {
        var v = CtwSmsBackend.Build(FlpSong.Read(SmsProject()), "synthetic");
        bool Has(FenceLevel f, string rule, string text) => v.Fence.Any(l => l.Level == f && l.Rule == rule && l.Text.Contains(text));
        r.Expect(v.Build != null, "VRUN's converter refused the synthetic song");
        r.Expect(Has(FenceLevel.Over, "slides", "not played"), "no OUT line for the triangle's slide");
        r.Expect(Has(FenceLevel.Over, "note range", "under the PSG"), "no OUT line for tone 2's note 40 (under A2)");
        r.Expect(Has(FenceLevel.Note, "tuning", "nearest semitone"), "no CHANGED line for the +50-cent note");
        r.Expect(Has(FenceLevel.Over, "instruments", "is a GB instrument"), "no OUT line for the Game Boy instance");
        r.Expect(Has(FenceLevel.Over, "instruments", "is not BrokenNes2"), "no OUT line for the sampler");
        r.Expect(Has(FenceLevel.Note, "timing", "slower"), "no CHANGED frame-rate line");
        r.Expect(!v.Fence.Any(l => l.Text.Contains("Duty 25%")), "VRUN NES's duty caveat leaked into the Master System fence (the PSG has no duty)");
        r.Expect(v.Build != null && v.Build.Channels.All(PlainOnly), "a stream has an opcode the game's plain decoders cannot play");
        var (tmpl, _) = Template();
        var copy = (byte[])tmpl.Clone(); CtwSmsBackend.SegaChecksum(copy);
        r.Expect(copy.AsSpan().SequenceEqual(tmpl), "the Sega header checksum recomputed on the template differs from makesms's");
        r.Note($"synthetic (Master System): fence {FlpFence.Summary(v.Fence)}");
        foreach (var l in FlpFence.Lines(v.Fence)) r.Note("    " + l);
        var gbOnly = CtwSmsBackend.Build(FlpSong.Read(FlpVocChecks.GbProject()), "gb-only");
        r.Expect(gbOnly.Fence.Any(l => l.Level == FenceLevel.Over && l.Text.StartsWith("NOTHING")), "a Game Boy project: no NOTHING line in the Master System fence");
    }

    /// <summary>What nes_apu.c makes of a NES latch (the registers as an NMI left them): per tone the PSG period and attenuation, the noise's attenuation and rate.</summary>
    internal static (int[] N, int[] Att, int Rate) NesToPsg(ReadOnlySpan<byte> l)
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
        n[2] = CtwSmsBackend.PsgPeriod(pt, true).N;
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
        var all = new List<(string, FlpSongModel)> { ("synthetic", FlpSong.Read(SmsProject())) };
        all.AddRange(FlpVocChecks.UserSongs(3));
        foreach (var (name, s) in all)
        {
            var v = CtwSmsBackend.Build(s, name);
            var rom = CtwSmsBackend.Patch(tmpl, man, v, out var why);
            r.Expect(rom != null, $"{name}: the test ROM refused the song: {why}");
            if (rom == null || v.Build == null) continue;
            int n = Math.Min(v.Build.Facts.LoopFrames, 1500);
            var nes = MidiRun.Of(nesT.Patch(v.Build.Channels, v.Build.Facts.LoopFrames, name), n + 60, mlb);
            var want = Enumerable.Range(0, n + 60).Select(f => NesToPsg(nes.Latch.AsSpan(f * MidiModel.LatchBytes, MidiModel.LatchBytes))).ToArray();
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
