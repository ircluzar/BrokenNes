// tri-octave <song.flp> <out dir> [seconds]: where VRUN NES's triangle sounds against the plugin's (2026-11-12, the user's item 1: check, change nothing).
//   The song's NES triangle instance alone: (a) a scenario of its notes with the instance's own parameters (out/<song>.tri.scenario.json, for `render <dll>`: the plugin's
//   NES chip in Direct mode), (b) VRUN's sound-test ROM of the same part (out/<song>.tri.nes, VRUN's converter + the bundled musictest template), run here too: the
//   triangle's $400A/$400B period every NMI and its frequency CPU / (32 (period + 1)). The expected key per note is printed so the render's pitch can be compared.
using System.Globalization;
using BrokenNes2.Music;
using BrokenNes2.Music.Flp;
using Vrun.Audio;

namespace BrokenNes.FruityHost;

internal static class FlpTriOctave
{
    public static int Main(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: tri-octave <song.flp> <out dir> [seconds]"); return 2; }
        double seconds = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 30;
        var s = FlpSong.Read(File.ReadAllBytes(args[1]));
        string name = Path.GetFileNameWithoutExtension(args[1]);
        var tri = s.Instruments.Where(i => i.Bn2 is { Console: 0, Channel: 2, Mode: "direct" } && i.Notes.Count > 0).ToList();
        if (tri.Count == 0) { Console.Error.WriteLine("no NES triangle instance with notes"); return 1; }
        var ins = tri[0];
        s.Instruments = [ins];
        Directory.CreateDirectory(args[2]);

        // (a) the plugin: the instance's parameters, its notes (FL key = MIDI key, C5 = 60; Coarse / Fine in the parameters)
        var sc = new Scenario { Name = name + " triangle", Tempo = s.Tempo };
        for (int i = 0; i < ins.Bn2!.Params.Length; i++) sc.Params[i] = ins.Bn2.Params[i];
        foreach (var n in ins.Notes.Where(n => !n.Slide && n.End > n.Start && n.Start < seconds * s.Tempo / 60 * s.Ppq).OrderBy(n => n.Start))
            sc.Notes.Add(new SimNote { StartBeat = n.Start / (double)s.Ppq, LengthBeats = (n.End - n.Start) / (double)s.Ppq, Key = n.Key + (n.Fine - 120) / 100.0, Channel = 2, Velocity = n.Velocity / 128.0 });
        sc.LengthBeats = sc.Notes.Max(n => n.StartBeat + n.LengthBeats);
        sc.Save(Path.Combine(args[2], name + ".tri.scenario.json"));

        // (b) VRUN: the converter + the sound-test template, run on BrokenNes (FIX cores)
        var v = VrunNesBackend.Build(s, name);
        var t = MidiChecks.BundledTemplate();
        var (_, _, mlb) = MidiFixtures.Bundled();
        var b = SongConvert.Convert(v.Midi, out var why, t.Region, triangleAtKey: true);
        if (b == null) { Console.Error.WriteLine("converter refused: " + why); return 1; }
        var rom = t.Patch(b.Channels, b.Facts.LoopFrames, name);
        File.WriteAllBytes(Path.Combine(args[2], name + ".tri.nes"), rom);
        int frames = (int)(seconds * Midi2Nes.Fps) + 120;
        var run = MidiRun.Of(rom, frames, mlb);
        int stream = Array.IndexOf(b.Facts.Source, 3);
        Console.WriteLine($"{name}: triangle instance \"{ins.Name}\" ({ins.Notes.Count} notes; coarse {ins.Bn2.Param(3)}, fine {ins.Bn2.Param(4)}); VRUN stream {stream}; {sc.Notes.Count} notes in {seconds} s");
        // per note: FL's key, its frequency at the key, VRUN's sounding frequency (the period held most in the note's frames)
        double tpf = s.Ppq * s.Tempo / 60.0 / Midi2Nes.Fps;
        int first = Enumerable.Range(0, frames).FirstOrDefault(f => On(run.Latch, f, stream));
        double t0 = ins.Notes.Where(n => !n.Slide).Min(n => n.Start);
        var rows = new List<string> { "start_s,key,fl_hz,vrun_period,vrun_hz,cents_vrun_minus_fl" };
        foreach (var n in ins.Notes.Where(n => !n.Slide && n.End - n.Start >= 2 * tpf).OrderBy(n => n.Start))
        {
            int f0 = first + (int)Math.Round((n.Start - t0) / tpf) + 1, f1 = first + (int)Math.Round((n.End - t0) / tpf) - 1;
            if (f1 >= frames || f1 <= f0) break;
            var periods = Enumerable.Range(f0, f1 - f0).Where(f => On(run.Latch, f, stream)).Select(f => Period(run.Latch, f, stream)).ToList();
            if (periods.Count == 0) continue;
            int p = periods.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
            double key = n.Key + (n.Fine - 120) / 100.0 + ins.Bn2.Param(3) + ins.Bn2.Param(4) / 100.0;
            double fl = 440 * Math.Pow(2, (key - 69) / 12), hz = 1789773.0 / (32 * (p + 1));
            rows.Add(FormattableString.Invariant($"{(n.Start - t0) / s.Ppq * 60 / s.Tempo:0.000},{key:0.##},{fl:0.00},{p},{hz:0.00},{1200 * Math.Log2(hz / fl):0.0}"));
        }
        File.WriteAllLines(Path.Combine(args[2], name + ".tri.notes.csv"), rows);
        var c = rows.Skip(1).Select(r => double.Parse(r.Split(',')[5], CultureInfo.InvariantCulture)).ToList();
        Console.WriteLine(FormattableString.Invariant($"  {c.Count} notes: VRUN minus FL's key: median {c.OrderBy(x => x).ElementAt(c.Count / 2):0.0} cents, min {c.Min():0.0}, max {c.Max():0.0}"));
        foreach (var r in rows.Take(9)) Console.WriteLine("  " + r);
        return 0;
    }

    private static int Period(byte[] latch, int f, int stream) { var l = latch.AsSpan(f * MidiModel.LatchBytes, MidiModel.LatchBytes); return (l[stream * 4 + 3] & 7) << 8 | l[stream * 4 + 2]; }
    private static bool On(byte[] latch, int f, int stream)
    {
        if ((f + 1) * MidiModel.LatchBytes > latch.Length) return false;
        var l = latch.AsSpan(f * MidiModel.LatchBytes, MidiModel.LatchBytes);
        return (l[0x15] & (1 << stream)) != 0 && (l[stream * 4] & 0x7F) != 0 && Period(latch, f, stream) >= 2;
    }
}
