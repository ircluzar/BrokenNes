// tri-octave-sms [song.flp ...]: where CTW SMS's tone 3 (the triangle) sounds against FL's key (the plugin plays it at the key), before and after 2026-11-13's TriangleAtKey.
//   Each song is read as a Master System project (FlpVocChecks.Retarget, as flp-test does), built by CtwSmsBackend twice (triangleAtKey false = the old octave, true = now),
//   patched into the game (Resources/flpctwsms.sms) and run on BrokenNes's Master System; per triangle note (not a slide, at least 2 frames) the PSG period held most in the
//   note's frames gives 3579545 / (32 N), compared with FL's key (+ Coarse / Fine). Notes the game raised whole octaves (under the PSG's range) are counted apart.
//   No song given: every .flp of FlpChecks.SongsDir.
using System.Globalization;
using BrokenNes2.Music.Flp;

namespace BrokenNes.FruityHost;

internal static class FlpTriOctaveSms
{
    public static int Main(string[] args)
    {
        if (args.Length > 1 && args[1] == "--trace-slides")
        {
            foreach (bool k in new[] { false, true })
            {
                var sv = CtwSmsBackend.Build(FlpSong.Read(FlpSlideChecks.Project(3)), "slides", k);
                var t = MidiChecks.BundledTemplate(); var (_, _, mlb) = MidiFixtures.Bundled();
                var run = MidiRun.Of(t.Patch(sv.Build!.Channels, sv.Build.Facts.LoopFrames, "s"), sv.Build.Facts.LoopFrames + 60, mlb);
                int st = Array.IndexOf(sv.Build.Facts.Source, 3);
                Console.WriteLine($"atKey {k}: tri stream {st}: {sv.Build.Facts.BendLine()}");
                Console.WriteLine("  bytes " + string.Join(" ", sv.Build.Channels[st].Select(x => x.ToString("X2"))));
                for (int fr = 0; fr < run.TriKey.Length; fr++) if (run.TriKey[fr]) { var l = run.Latch.AsSpan(fr * MidiModel.LatchBytes, MidiModel.LatchBytes); Console.WriteLine($"  keyon f{fr} period ${(l[st * 4 + 3] & 7) << 8 | l[st * 4 + 2]:X3}"); }
            }
            return 0;
        }
        var files = args.Length > 1 ? args.Skip(1).ToList() : FlpChecks.Songs();
        if (files.Count == 0) { Console.Error.WriteLine("no .flp songs"); return 1; }
        var (tmpl, man) = FlpSmsChecks.Template();
        foreach (var f in files)
        {
            string name = Path.GetFileNameWithoutExtension(f);
            var s = FlpVocChecks.Retarget(FlpSong.Read(File.ReadAllBytes(f)), 3);
            if (!s.Instruments.Any(i => i.Bn2 is { Console: 3, Channel: 2, Mode: "direct" } && i.Notes.Count > 0)) { Console.WriteLine($"{name}: no triangle"); continue; }
            var line = new List<string>();
            foreach (bool atKey in new[] { false, true })
                line.Add((atKey ? "after " : "before ") + Measure(s, name, atKey, tmpl, man));
            Console.WriteLine($"{name}: {string.Join(" | ", line)}");
        }
        return 0;
    }

    private static string Measure(FlpSongModel s, string name, bool atKey, byte[] tmpl, string man)
    {
        var v = CtwSmsBackend.Build(s, name, atKey);
        var rom = CtwSmsBackend.Patch(tmpl, man, v, out var why);
        if (rom == null || v.Build == null) return "refused: " + why;
        int psg = Array.IndexOf(v.Build.Facts.Source, 3);   // MIDI channel 3 = the triangle's part
        if (psg is < 0 or > 2) return $"triangle part on channel {psg}";
        int frames = Math.Min(v.Build.Facts.LoopFrames, 1800) + 400;
        var run = FlpSmsChecks.RunSms(rom, frames);
        bool On(int fr) => fr >= 0 && fr < frames && run[fr].Att[psg] != 15 && run[fr].Period[psg] > 0;
        int first = Enumerable.Range(31, frames - 31).FirstOrDefault(On, -1);
        if (first < 0) return "silent";
        var tri = s.Instruments.Where(i => i.Bn2 is { Console: 3, Channel: 2, Mode: "direct" }).ToList();
        var notes = tri.SelectMany(i => i.Notes.Where(n => !n.Slide).Select(n => (n, b: i.Bn2!))).OrderBy(x => x.n.Start).ToList();
        double tpf = s.Ppq * s.Tempo / 60.0 / CtwSmsBackend.NesFps;   // VRUN's engine counts its frames on the Master System's frame
        double t0 = notes.Min(x => x.n.Start);
        var cents = new List<double>(); int raised = 0; var oct = new SortedDictionary<int, int>();
        foreach (var (n, b) in notes)
        {
            if (n.End - n.Start < 2 * tpf) continue;
            int f0 = first + (int)Math.Round((n.Start - t0) / tpf) + 1, f1 = first + (int)Math.Round((n.End - t0) / tpf) - 1;
            if (f1 >= frames - 400 || f1 <= f0) break;
            var ps = Enumerable.Range(f0, f1 - f0).Where(On).Select(fr => run[fr].Period[psg]).ToList();
            if (ps.Count == 0) continue;
            int N = ps.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
            double key = n.Key + (n.Fine - 120) / 100.0 + b.Param(3) + b.Param(4) / 100.0;
            double fl = 440 * Math.Pow(2, (key - 69) / 12), hz = 3579545.0 / (32.0 * N);
            double c = 1200 * Math.Log2(hz / fl);
            if (Environment.GetEnvironmentVariable("BN_TRI_DUMP") == "1" && atKey) Console.WriteLine(FormattableString.Invariant($"    t {(n.Start - t0) / (double)s.Ppq:0.00} beats key {key:0.#} f{f0}-{f1} N {N} ({string.Join(",", ps.Distinct())}) {c:0}"));
            int o = (int)Math.Round(c / 1200); oct[o] = oct.GetValueOrDefault(o) + 1;
            double expect = atKey ? 0 : -1200;
            if (Math.Abs(c - expect) > 600) { raised++; continue; }   // a whole-octave raise (under the PSG's range) or a line-up miss: counted apart
            cents.Add(c);
        }
        if (cents.Count == 0) return $"no notes at the expected octave ({raised} apart); octaves {string.Join(" ", oct.Select(kv => $"{kv.Key * 1200:+0;-0;0}:{kv.Value}"))}";
        cents.Sort();
        return FormattableString.Invariant($"{cents.Count} notes median {cents[cents.Count / 2]:0.0} cents (min {cents[0]:0.0}, max {cents[^1]:0.0}){(raised > 0 ? $", {raised} apart (octave raises)" : "")}; octaves {string.Join(" ", oct.Select(kv => $"{kv.Key * 1200:+0;-0;0}:{kv.Value}"))}");
    }
}
