// flp-test's slide audit (2026-11-11): FL's piano-roll slides through every .flp music engine, measured on the game's own driver where the repository has one.
//   One synthetic project (written byte by byte like the other fixtures): on pulse 1, pulse 2 and the triangle / wave, slides up and down, small (a semitone in 2 ticks) and large
//   (two octaves), short and long (2 to 192 ticks), crossing octaves and the NES period's high byte ($100, $200, $300), a sub-semitone (+50 cent) slide on a detuned note.
//   Per engine the song is built by its back end, the engine's test ROM (the game's real driver) is run headless on BrokenNes, the sounding frequency is read from the chip's
//   registers every frame and compared with FL's pitch over that frame (FlpVocChecks.FlAt: the model alone, FL's measured slide shape). The error of a frame is how far the played
//   pitch is from the range FL's pitch covers during that frame; the check allows the register's own step (half of it, + 2 cents). Frames next to a note's start / end are timing
//   (the engine checks'), the song's start is found by the best alignment (a quarter frame at a time).
//   slides-vrun-nes   VRUN NES: sound-test template (VRUN's bend decoders), APU registers per NMI
//   slides-ctwsms     CTW SMS: the game itself (fab2c's bend decoders + nes_apu.c's PSG mapping), PSG periods
//   slides-vocgb      Vaults GB: the game's driver ("freq" events per frame), Game Boy frequency registers
//   slides-vocgg      Vaults GG: the Game Gear driver on the Game Boy bytes, PSG periods
//   slides-sv         Super VRUN (SNES / WideMess / MD / DOS / 95 share one .it via sf2it.py): the MIDI the back end writes (note + bend x range), NOT the drivers (their builds are
//                     build-supervrun.ps1's: the game's whole toolchain, out of flp-test's reach); see docs/projects/flp-music-pipeline.md section 11 for what was measured there
using BrokenNes2.Music;
using BrokenNes2.Music.Flp;
using Vrun.Audio;

namespace BrokenNes.FruityHost;

internal static class FlpSlideChecks
{
    public static readonly FlpChecks.Check[] All =
    [
        new("slides-vrun-nes", "slides audit, VRUN NES: the sound-test ROM's APU periods per NMI follow FL's slides (up/down, small/large, short/long, crossing octaves and $100/$200/$300)", VrunNes),
        new("slides-ctwsms", "slides audit, CTW SMS: the game's PSG periods follow FL's slides", CtwSms),
        new("slides-vocgb", "slides audit, Vaults GB: the game's Game Boy frequency registers follow FL's slides", VocGb),
        new("slides-vocgg", "slides audit, Vaults GG: the Game Gear driver's PSG periods follow FL's slides", VocGg),
        new("slides-adlib-sv", "slides audit, Super VRUN DOS / 95 AdLib songs (x86 PC instances): dos_snd.c's player (the C# twin of adlib-sv) writes F-numbers that follow FL's slides", AdlibSv),
        new("slides-adlib-vaults", "slides audit, Vaults DOS / 95 AdLib songs: dos_opl.c's player (the C# twin of adlib-vaults) writes F-numbers that follow FL's slides", AdlibVaults),
        new("slides-sv", "slides audit, Super VRUN: the MIDI for sf2it.py (note + bend x range) follows FL's slides on every engine's build", Sv),
    ];

    /// <summary>The slide project for a console (NES instances moved there by the caller): the same three parts on pulse 1, pulse 2, triangle / wave.</summary>
    internal static byte[] Project(int console = 0)
    {
        var notes = new List<FlpChecks.SNote>();
        for (int ch = 0; ch < 3; ch++)
        {
            // the triangle / wave part a fifth below the squares: VRUN's converter gives the triangle to the lowest part (Midi2Nes.SelectChannelMap), so every part stays on its channel
            notes.AddRange(Part(ch, ch == 2 ? -5 : 0));
        }
        return FlpChecks.Project(150,
            [("Sq1", "BrokenNes2", FlpChecks.Bn2State(console: console, place: 0)), ("Sq2", "BrokenNes2", FlpChecks.Bn2State(console: console, place: 1, duty: 2)),
             ("Tri", "BrokenNes2", FlpChecks.Bn2State(console: console, place: 2))],
            [(1, notes.ToArray())], [(0, 1, 1536)]);
    }

    private static IEnumerable<FlpChecks.SNote> Part(int ch, int t)
    {
        var notes = new List<FlpChecks.SNote>();
        {
            notes.Add(new(0, 384, 60, ch));                       // C4 held
            notes.Add(new(48, 48, 62, ch, Slide: true));          // up 2 semitones, 48 ticks
            notes.Add(new(144, 192, 48, ch, Slide: true));        // down an octave and more, long (NES pulse: $1AB -> $357, crosses $200 and $300)
            notes.Add(new(480, 384, 72, ch));                     // C5
            notes.Add(new(500, 2, 71, ch, Slide: true));          // down a semitone in 2 ticks (shorter than a frame)
            notes.Add(new(600, 96, 84, ch, Slide: true));         // up an octave (NES pulse: $0D5 -> $06A)
            notes.Add(new(720, 96, 64, ch, Slide: true));         // down 20 semitones (crosses $100 upward in period)
            notes.Add(new(960, 192, 55, ch, Fine: 120));          // G3
            notes.Add(new(1000, 120, 55, ch, Slide: true, Fine: 170));   // +50 cents, slow (sub-unit period steps)
            notes.Add(new(1200, 240, 67, ch));                    // G4
            notes.Add(new(1210, 200, 43, ch, Slide: true));       // two octaves down, long
        }
        return notes.Select(n => n with { Key = n.Key + t });
    }

    private static double Dump = -1;
    internal sealed record Result(int Judged, int Off, double MaxErr, double MaxBeyondStep, string First, int Below = 0, int Folded = 0);

    /// <summary>Compare a played track (Hz per frame, NaN = silent; the register's step in cents per frame) with FL on a channel; the song starts at the best-aligned frame.</summary>
    internal static Result Measure(FlpSongModel s, int console, int channel, double[] hz, double[] step, double fps, double minCents = 0, double lag = 0, double tol = 2.0, bool movingOnly = false, bool fold = false, int raise = 0)
    {
        double tpf = s.Ppq * s.Tempo / 60.0 / fps;
        var bounds = new SortedSet<double>(s.Instruments.Where(i => i.Bn2 is { } b && b.Console == console && b.Channel == channel).SelectMany(i => i.Notes.Where(n => !n.Slide).SelectMany(n => new double[] { n.Start, n.End })));
        double end = bounds.Count > 0 ? bounds.Max : 0;
        Result Score(double start)
        {
            int judged = 0, off = 0, below = 0, folded = 0; double max = 0, beyond = 0; string first = "";
            for (int f = 0; f < hz.Length; f++)
            {
                double t0 = (f - start) * tpf;
                if (t0 < 0 || t0 + tpf > end) continue;
                if (bounds.GetViewBetween(t0 - tpf, t0 + 2 * tpf).Count > 0) continue;
                double lo = double.MaxValue, hi = double.MinValue; bool silent = false;
                for (int q = 0; q <= 4 + (int)(4 * lag); q++) { var a = FlpVocChecks.FlAt(s, console, channel, t0 - lag * tpf + tpf * q / 4.0); if (a == null) { silent = true; break; } lo = Math.Min(lo, a.Value.Cents); hi = Math.Max(hi, a.Value.Cents); }
                if (silent) continue;
                if (lo < minCents) { below++; continue; }
                if (movingOnly && hi - lo < 0.5) continue;
                judged++;
                if (double.IsNaN(hz[f])) { off++; max = double.PositiveInfinity; if (first.Length == 0) first = $"frame {f}: silent, FL {lo:0}..{hi:0} cents"; continue; }
                double c = 6900 + 1200 * Math.Log2(hz[f] / 440.0);
                double err = c < lo ? lo - c : c > hi ? c - hi : 0;
                if (fold) { double c2 = c + 1200, e2 = c2 < lo ? lo - c2 : c2 > hi ? c2 - hi : 0; if (e2 < err) { err = e2; folded++; } }
                for (int k = 1; k <= raise && err > 0; k++) { double c2 = c - 1200 * k, e2 = c2 < lo ? lo - c2 : c2 > hi ? c2 - hi : 0; if (e2 < err) { err = e2; folded++; } }
                max = Math.Max(max, err);
                double b = Math.Max(0, err - step[f] / 2);
                beyond = Math.Max(beyond, b);
                if (Environment.GetEnvironmentVariable("BN_SLIDE_DUMP") is { } dump && dump == $"{console}/{channel}" && start == Dump) Console.WriteLine($"  f{f} t{t0:0.0} played {c:0.0} FL {lo:0.0}..{hi:0.0} step {step[f]:0.0}");
                if (b > tol) { off++; if (first.Length == 0) first = $"frame {f} (tick {t0:0.0}): played {c:0.0} cents, FL {lo:0.0}..{hi:0.0}, register step {step[f]:0.0}"; }
            }
            return new Result(judged, off, max, beyond, first, below, folded);
        }
        // the song's start: among the starts that judge (nearly) the whole song, the one with the fewest frames off, then the smallest error
        var all = Enumerable.Range(0, 4 * 400).Select(k => Score(k / 4.0)).ToList();
        int most = all.Max(x => x.Judged);
        Result? best = null;
        double bestStart = 0;
        for (int k = 0; k < all.Count; k++)
        {
            var r = all[k];
            if (r.Judged < most * 0.95) continue;
            if (best == null || r.Off < best.Off || r.Off == best.Off && r.MaxBeyondStep < best.MaxBeyondStep) { best = r; bestStart = k / 4.0; }
        }
        if (Environment.GetEnvironmentVariable("BN_SLIDE_DUMP") == $"{console}/{channel}") { Dump = bestStart; Score(bestStart); Dump = -1; }
        return best ?? new Result(0, 0, 0, 0, "nothing judged");
    }

    /// <summary>Measured strictly (FL's pitch during the frame itself), then judged with the engine's allowance: <paramref name="lag"/> frames late, <paramref name="tol"/> cents past the half step.</summary>
    private static void Report(ImodelReport r, string engine, string part, FlpSongModel s, int console, int channel, double[] hz, double[] step, double fps, double minCents = 0, double lag = 0, double tol = 2.0, string? why = null, bool movingOnly = false, bool fold = false, int raise = 0)
    {
        var strict = Measure(s, console, channel, hz, step, fps, minCents, fold: fold, raise: raise);
        var m = lag == 0 && tol == 2.0 && !movingOnly ? strict : Measure(s, console, channel, hz, step, fps, minCents, lag, tol, movingOnly, fold, raise);
        if (raise > 0) r.Note($"    {engine} {part}: {strict.Folded} of {strict.Judged} frames are in notes the game raised whole octaves (under the PSG's range where the NES plays them: fenced)");
        if (fold) r.Warn($"{engine} {part}: {strict.Folded} of {strict.Judged} frames play an octave BELOW FL (nes_apu.c doubles the NES triangle's period like the NES does, and halves it only past the PSG's 10 bits: the octave flips inside a slide at period $3FF); judged modulo that octave");
        r.Expect(m.Judged >= (movingOnly ? 60 : 100), $"{engine} {part}: only {m.Judged} frames judged");
        r.Expect(m.Off == 0, $"{engine} {part}: {m.Off} of {m.Judged} frames off FL's slide by more than the register's half step + {tol} cents{(lag > 0 ? $" (allowing {lag} frame late)" : "")}; first {m.First}");
        Rows.Add((engine, part, strict.MaxErr, strict.MaxBeyondStep, m.MaxBeyondStep, lag, tol));
        r.Note($"{engine,-16} {part,-18} {strict.Judged} frames: max error {strict.MaxErr:0.0} cents, beyond the register's half step {strict.MaxBeyondStep:0.0}" + (lag > 0 ? $" ({m.MaxBeyondStep:0.0} allowing {lag} frame late)" : "") + (strict.Below > 0 ? $"; {strict.Below} frames under the chip's lowest note (fenced, not judged)" : "") + (why != null ? "; " + why : ""));
    }
    internal static readonly List<(string Engine, string Part, double Max, double Beyond, double Allowed, double Lag, double Tol)> Rows = new();

    /// <summary>VRUN NES (and CTW SMS, its translation) slide in period units per frame (linear in the period), FL in cents: between the two curves up to ~7 cents beyond the register's step (measured).</summary>
    private const double PeriodSlideTol = 10.0;

    /// <summary>The Game Boy register's step (cents) at a frequency.</summary>
    private static double GbStep(double hz, bool wave) { double x = 2048 - (wave ? 65536.0 : 131072.0) / hz; return Cents(VocGbBackend.XHz((int)Math.Min(2047, Math.Round(x) + 1), wave), VocGbBackend.XHz((int)Math.Round(x), wave)); }


    private static double Cents(double a, double b) => Math.Abs(1200 * Math.Log2(a / b));
    private const double NesCpu = 1789773.0, PsgClock = 3579545.0;
    /// <summary>The PSG's lowest tone (period 1023: 109.3 Hz, just under A2) in cents: lower pitches are the engines' fenced "note range" (raised or dropped), not slides.</summary>
    private static readonly double PsgLowest = 6900 + 1200 * Math.Log2(PsgClock / (32.0 * 1023) / 440.0);

    // ---- VRUN NES ----

    internal static (double[] Hz, double[] Step) NesTrack(byte[] latch, int stream, int frames)
    {
        var hz = new double[frames]; var st = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            var l = latch.AsSpan(f * MidiModel.LatchBytes, MidiModel.LatchBytes);
            int p = (l[stream * 4 + 3] & 7) << 8 | l[stream * 4 + 2];
            bool tri = stream == 2;
            bool on = (l[0x15] & (1 << stream)) != 0 && (tri ? (l[8] & 0x7F) != 0 && p >= 2 : (l[stream * 4] & 15) != 0 && p >= 8);
            double div = tri ? 32 : 16;
            hz[f] = on ? NesCpu / (div * (p + 1)) : double.NaN;
            st[f] = Cents(NesCpu / (div * p), NesCpu / (div * (p + 1)));
        }
        return (hz, st);
    }

    public static void VrunNes(ImodelReport r)
    {
        var s = FlpSong.Read(Project());
        var v = VrunNesBackend.Build(s, "slides");
        var t = MidiChecks.BundledTemplate();
        var (_, _, mlb) = MidiFixtures.Bundled();
        var b = SongConvert.Convert(v.Midi, out var why, t.Region, triangleAtKey: true);
        r.Expect(b != null, $"the converter refused the slide song: {why}");
        if (b == null) return;
        int n = b.Facts.LoopFrames + 60;
        var run = MidiRun.Of(t.Patch(b.Channels, b.Facts.LoopFrames, "slides"), n, mlb);
        foreach (var p in v.Parts.Where(p => p.NesChannel < 3))
        {
            int nes = Array.IndexOf(b.Facts.Source, p.NesChannel + 1);
            if (nes is < 0 or > 2) { r.Expect(false, $"VRUN NES {VrunNesBackend.NesNames[p.NesChannel]}: the converter put it on stream {nes}"); continue; }
            var (hz, st) = NesTrack(run.Latch, nes, n);
            Report(r, "VRUN NES", VrunNesBackend.NesNames[p.NesChannel], s, 0, p.NesChannel, hz, st, Midi2Nes.Fps, tol: PeriodSlideTol);
        }
    }

    // ---- CTW SMS ----

    public static void CtwSms(ImodelReport r)
    {
        var s = FlpSong.Read(Project(3));
        var v = CtwSmsBackend.Build(s, "slides");
        var (tmpl, man) = FlpSmsChecks.Template();
        var rom = CtwSmsBackend.Patch(tmpl, man, v, out var why);
        r.Expect(rom != null && v.Build != null, $"the CTW SMS test ROM refused the slide song: {why}");
        if (rom == null || v.Build == null) return;
        foreach (var l in v.Fence.Where(l => l.Rule == "slides")) r.Note("    fence: " + l.Text);
        int n = v.Build.Facts.LoopFrames + 400;
        var run = FlpSmsChecks.RunSms(rom, n);
        for (int c = 0; c < 3; c++)
        {
            // the converter's channel choice: the PSG tone channel that plays this part
            int psg = Array.IndexOf(v.Build.Facts.Source, c + 1);
            if (psg is < 0 or > 2) { r.Expect(false, $"CTW SMS {CtwSmsBackend.Names[c]}: on channel {psg}"); continue; }
            var hz = new double[n]; var st = new double[n];
            for (int f = 0; f < n; f++)
            {
                int N = run[f].Period[psg];
                hz[f] = run[f].Att[psg] == 15 || N < 1 ? double.NaN : PsgClock / (32.0 * N);
                st[f] = N > 1 ? Cents(PsgClock / (32.0 * (N - 1)), PsgClock / (32.0 * N)) : 0;
            }
            if (psg != 2) { Report(r, "CTW SMS", CtwSmsBackend.Names[c], s, 3, c, hz, st, CtwSmsBackend.NesFps, PsgLowest, tol: PeriodSlideTol); continue; }
            // the triangle (2026-11-12): where VRUN NES plays it, an octave below FL (judged an octave up, as VRUN NES's); a note under the PSG's range is raised whole octaves
            // for the whole note (judged modulo those raises); frames under the range an octave below FL are the fenced note range. And no octave jump inside a note:
            // two sounding frames in a row an octave apart (+-60 cents; the slide song has no legato octave leaps on the triangle)
            Report(r, "CTW SMS", CtwSmsBackend.Names[c] + " (+1 oct)", s, 3, c, hz.Select(x => x * 2).ToArray(), st, CtwSmsBackend.NesFps, PsgLowest + 1200, tol: 6.0, raise: 2);   // (measured 5.2 beyond the half step, 2026-11-12: tighter than the squares' PeriodSlideTol)
            int jumps = 0; string firstJump = "";
            for (int f = 1; f < n; f++)
            {
                if (double.IsNaN(hz[f]) || double.IsNaN(hz[f - 1])) continue;
                double d = Math.Abs(1200 * Math.Log2(hz[f] / hz[f - 1]));
                if (Math.Abs(d - 1200) < 60 || Math.Abs(d - 2400) < 60) { jumps++; if (firstJump.Length == 0) firstJump = $"frame {f}: {hz[f - 1]:0.0} -> {hz[f]:0.0} Hz"; }
            }
            r.Expect(jumps == 0, $"CTW SMS tone 3 (triangle): {jumps} octave jumps between two sounding frames (a note's octave must hold through its slides); first {firstJump}");
            r.Note($"CTW SMS tone 3: {jumps} octave jumps between sounding frames");
        }
    }

    // ---- Vaults GB / GG ----

    public static void VocGb(ImodelReport r)
    {
        var s = FlpSong.Read(Project(1));
        var v = VocGbBackend.Build(s, "slides");
        var (tmpl, man) = FlpVocChecks.Template();
        var rom = VocGbBackend.Patch(tmpl, man, v, out var why);
        r.Expect(rom != null, $"the Vaults GB test ROM refused the slide song: {why}");
        if (rom == null) return;
        foreach (var l in v.Fence.Where(l => l.Rule == "slides")) r.Note("    fence: " + l.Text);
        int n = v.Length + 400;
        var run = FlpVocChecks.RunGb(rom, n);
        for (int c = 0; c < 3; c++)
        {
            var hz = new double[n]; var st = new double[n];
            for (int f = 0; f < n; f++)
            {
                var g = run[f][c];
                hz[f] = g.Enabled && g.DacOn && g.Volume > 0 ? VocGbBackend.XHz(g.Period, c == 2) : double.NaN;
                st[f] = Cents(VocGbBackend.XHz(Math.Min(2047, g.Period + 1), c == 2), VocGbBackend.XHz(g.Period, c == 2));
            }
            Report(r, "Vaults GB", VocGbBackend.Names[c], s, 1, c, hz, st, VocGbBackend.Fps);
        }
    }

    public static void VocGg(ImodelReport r)
    {
        var s = FlpSong.Read(Project(1));
        var v = VocGgBackend.Build(s, "slides");
        string rp = FlpVocChecks.RepoFile(@"Plugin\BrokenNes2.Plugin\Resources\flpvocgg.gg");
        var rom = VocGgBackend.Patch(File.ReadAllBytes(rp), File.ReadAllText(Path.ChangeExtension(rp, ".txt")), v, out var why);
        r.Expect(rom != null, $"the Vaults GG test ROM refused the slide song: {why}");
        if (rom == null) return;
        int n = v.Length + 400;
        var run = FlpVocChecks.RunGg(rom, n);
        for (int c = 0; c < 3; c++)
        {
            var hz = new double[n]; var st = new double[n];
            for (int f = 0; f < n; f++)
            {
                int N = run[f].Period[c];
                hz[f] = run[f].Att[c] == 15 || N < 1 ? double.NaN : PsgClock / (32.0 * N);
                st[f] = (N > 1 ? Cents(PsgClock / (32.0 * (N - 1)), PsgClock / (32.0 * N)) : 0) + (double.IsNaN(hz[f]) ? 0 : GbStep(hz[f], c == 2));
            }
            // the Game Gear plays the Game Boy's frames at its own 59.92 Hz: the song's time per driver frame is the Game Boy's
            Report(r, "Vaults GG", VocGbBackend.Names[c], s, 1, c, hz, st, VocGbBackend.Fps, PsgLowest, lag: 1, movingOnly: true,
                why: "judged while FL slides: the GG driver turns the Game Boy register into a PSG period (two roundings: the step allowed is both) and runs a frame behind the Game Boy driver. Held notes started by a note byte play the note table (the nearest PSG periods since 2026-11-12: vocgg-engine checks them); a note HELD AFTER a slide keeps the slide's Game Boy register, which the driver's x 0.8516 (not 0.8534) and the register's step leave up to 34 cents off at C7 (measured 2026-11-12: the driver's math, not the table)");
        }
    }

    // ---- AdLib (x86 PC instances: Super VRUN DOS / 95 and Vaults DOS / 95 song blobs) ----

    private static FlpSongModel PcSong() => FlpSong.Read(FlpChecks.Project(150, [("Lead", "BrokenNes2", AdlibChecks.PcState(AdlibBackend.FirstPcChip, 2))], [(1, Part(0, 0).ToArray())], [(0, 1, 1536)]));

    /// <summary>The OPL2 voice's frequency per tick from the player's register writes (A0+v / B0+v: F-number, block, key-on), NaN while keyed off.</summary>
    private static (double[] Hz, double[] Step) OplTrack(int ticks, int a0, Action<Action<int, int, int>> play)
    {
        var lo = new int[ticks]; var hi = new int[ticks];
        int l = 0, h = 0, last = -1;
        play((t, reg, v) =>
        {
            for (int k = last + 1; k < t && k < ticks; k++) { lo[k] = l; hi[k] = h; }
            last = Math.Max(last, t - 1);
            if (reg == a0) l = v; else if (reg == a0 + 0x10) h = v;
            if (t < ticks) { lo[t] = l; hi[t] = h; }
        });
        for (int k = last + 1; k < ticks; k++) { lo[k] = l; hi[k] = h; }
        var hz = new double[ticks]; var st = new double[ticks];
        for (int t = 0; t < ticks; t++)
        {
            int fnum = (hi[t] & 3) << 8 | lo[t], block = hi[t] >> 2 & 7;
            double f = fnum * 49716.0 / (1 << (20 - block));
            hz[t] = (hi[t] & 0x20) != 0 && fnum > 0 ? f : double.NaN;
            st[t] = fnum > 1 ? Cents(fnum, fnum - 1) : 0;
        }
        return (hz, st);
    }

    public static void AdlibSv(ImodelReport r)
    {
        var s = PcSong();
        var a = AdlibBackend.BuildSuperVrun(s, "slides");
        r.Expect(a != null, "the PC slide song is not an AdLib song");
        if (a == null) return;
        int rate = a.Blob[0] | a.Blob[1] << 8, n = a.Ticks + 30;
        var (hz, st) = OplTrack(n, 0xA0, tap => AdlibChecks.Play(a.Blob, n, tap: tap));
        Report(r, "Super VRUN DOS/95", "AdLib voice 1", s, 5, 0, hz, st, rate / 256.0);
    }

    public static void AdlibVaults(ImodelReport r)
    {
        var s = PcSong();
        var a = AdlibBackend.BuildVaults(s, "slides");
        r.Expect(a != null, "the PC slide song is not a Vaults AdLib song");
        if (a == null) return;
        int n = (int)(VocGbBackend.Fps * 7);
        var (hz, st) = OplTrack(n, 0xA3, tap => AdlibChecks.PlayVaults(a.Blob, n, tap));
        Report(r, "Vaults DOS/95", "AdLib voice 1", s, 5, 0, hz, st, VocGbBackend.Fps);
    }

    // ---- Super VRUN (the MIDI for sf2it.py) ----

    public static void Sv(ImodelReport r)
    {
        var s = FlpSong.Read(Project());
        foreach (var e in new[] { SvEngine.Snes, SvEngine.Md, SvEngine.Dos })
        {
            var v = SuperVrunBackend.Build(s, "slides", e);
            var m = MidiFile.Parse(v.Midi);
            var all = new List<(int Tick, int Seq, MidiEvent E)>();
            int seq = 0;
            foreach (var t in m.Tracks) { int tick = 0; foreach (var x in t) { tick += x.DeltaTime; all.Add((tick, seq++, x)); } }
            all.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Seq.CompareTo(b.Seq));
            double fps = 60.0, midiPerFl = m.TicksPerQuarter / (double)s.Ppq, tpfMidi = s.Ppq * s.Tempo / 60.0 / fps * midiPerFl;
            foreach (var part in v.Parts)
            {
                int mc = part.MidiChannel - 1;
                var ins = s.Instruments.First(i => i.Name == part.Instrument);
                int nes = ins.Bn2!.Channel;
                int frames = (int)Math.Ceiling(v.Seconds * fps) + 2;
                var hz = new double[frames]; var st = new double[frames];
                int key = -1, bend = 0, range = 2, rpn = 0x3FFF, k = 0;
                for (int f = 0; f < frames; f++)
                {
                    double upto = (f + 1) * tpfMidi;
                    for (; k < all.Count && all[k].Tick < upto; k++)
                    {
                        var x = all[k].E;
                        if (x.Status >= 0xF0 || x.Channel != mc) continue;
                        switch (x.Command)
                        {
                            case 0xB0:
                                if (x.Data1 == 101) rpn = (rpn & 0x7F) | (x.Data2 << 7); else if (x.Data1 == 100) rpn = (rpn & ~0x7F) | x.Data2; else if (x.Data1 == 6 && rpn == 0) range = x.Data2;
                                break;
                            case 0xE0: bend = (x.Data1 | (x.Data2 << 7)) - 8192; break;
                            case 0x90 when x.Data2 > 0: key = x.Data1; break;
                            case 0x80: case 0x90: if (x.Data1 == key) key = -1; break;
                        }
                    }
                    hz[f] = key < 0 ? double.NaN : VocGbBackend.MidiHz(key + bend / 8192.0 * range);
                    st[f] = range * 100.0 / 8192;
                }
                // FL's time at the frame's END (the MIDI's state after every event up to it): the start offset absorbs it
                Report(r, SuperVrunBackend.Names[(int)e] + " MIDI", VrunNesBackend.NesNames[nes], s, 0, nes, hz, st, fps);
            }
        }
    }
}
