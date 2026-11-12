// `FruityHost ctwsms-bends <game.sms> <music_data.fab> <song> <loop frames> [frames]`: a CTW SMS build that plays <song> (VRUN_CTW_SMS_dev build.ps1 -Define SHIM_TITLE_TRACK=TRACK_<SONG>:
// the title screen starts it) against VRUN NES's own engine playing the same streams (the bundled sound-test template: VRUN's bend decoders, its APU registers per NMI), mapped
// as nes_apu.c maps them (FlpSmsChecks.NesToPsg). Prints, per tone channel, the share of sounding frames whose PSG period and attenuation match, and the same over the frames
// where VRUN NES's period moves without a new note (the bends). A development tool (2026-11-10, fab2c's SSF types): no pass/fail threshold.
using System.Text.RegularExpressions;

namespace BrokenNes.FruityHost;

internal static class FlpSmsBends
{
    public static int Main(string[] args)
    {
        if (args.Length < 5) { Console.WriteLine("usage: ctwsms-bends <game.sms> <music_data.fab> <song> <loop frames> [frames]"); return 1; }
        var sms = File.ReadAllBytes(args[1]);
        string text = File.ReadAllText(args[2]), song = args[3];
        int loop = int.Parse(args[4]), frames = args.Length > 5 ? int.Parse(args[5]) : Math.Min(loop, 3000);
        string[] keys = ["square1", "square2", "triangle", "noise"];
        var ch = new byte[4][];
        for (int c = 0; c < 4; c++)
        {
            var m = Regex.Match(text, $@"\[\]\s+music_{song}_{keys[c]}\s*\n\s*U\[\d+\]\(([^)]*)\)");
            if (!m.Success) { Console.WriteLine($"no music_{song}_{keys[c]} in {args[2]}"); return 1; }
            ch[c] = m.Groups[1].Value.Split(',').Select(s => (byte)int.Parse(s.Trim())).ToArray();
        }
        // the semitone periods (VRUN's PERIOD tables), mapped: a frame whose NES period is none of them is a bent frame
        var lo = Table(text, "PERIOD_LO"); var hi = Table(text, "PERIOD_HI");
        var semis = new HashSet<int>[3];
        for (int c = 0; c < 3; c++) { semis[c] = new(); for (int k = 0; k < Math.Min(lo.Length, hi.Length); k++) semis[c].Add(BrokenNes2.Music.Flp.CtwSmsBackend.PsgPeriod(hi[k] << 8 | lo[k], c == 2).N); }
        var nesT = MidiChecks.BundledTemplate();
        var (_, _, mlb) = MidiFixtures.Bundled();
        var nes = MidiRun.Of(nesT.Patch(ch, loop, song), frames + 60, mlb);
        var want = FlpSmsChecks.NesToPsg(nes, frames + 60);
        var run = FlpSmsChecks.RunSms(sms, frames + 1500);
        // line up: the first frame each side sounds a tone, then the best offset around it
        int nf = Enumerable.Range(0, want.Length).FirstOrDefault(f => want[f].Att.Take(3).Any(a => a != 15), -1);
        int sf = Enumerable.Range(31, run.Length - 31).FirstOrDefault(f => run[f].Att.Take(3).Any(a => a != 15), -1);
        if (nf < 0 || sf < 0) { Console.WriteLine($"no sound to line up (NES {nf}, SMS {sf})"); return 1; }
        int best = -1, bs = 0, bn = 0;
        for (int dn = -6; dn <= 6; dn++)
            for (int ds = -6; ds <= 6; ds++)
            {
                int a0 = nf + dn, b0 = sf + ds, a = 0;
                if (a0 < 0 || b0 < 0) continue;
                for (int f = 0; f < 600 && a0 + f < want.Length && b0 + f < run.Length; f++) for (int c = 0; c < 3; c++) a += Same(want[a0 + f], run[b0 + f], c) ? 1 : 0;
                if (a > best) { best = a; bs = b0; bn = a0; }
            }
        int len = Math.Min(frames, Math.Min(want.Length - bn, run.Length - bs));
        Console.WriteLine($"{song}: SMS from frame {bs}, NES from {bn}, {len} frames");
        int tot = 0, totOk = 0, totB = 0, totBOk = 0;
        for (int c = 0; c < 3; c++)
        {
            int snd = 0, ok = 0, bend = 0, bendOk = 0; string first = "";
            for (int f = 1; f < len; f++)
            {
                var w = want[bn + f]; var p = want[bn + f - 1]; var g = run[bs + f];
                if (w.Att[c] == 15) continue;
                snd++; bool s = Same(w, g, c); if (s) ok++;
                if (!semis[c].Contains(w.N[c])) { bend++; if (s) bendOk++; }
                if (!s && first.Length == 0) first = $" first miss frame {f}: NES->PSG period {w.N[c]} att {w.Att[c]}, PSG {g.Period[c]} att {g.Att[c]}";
            }
            tot += snd; totOk += ok; totB += bend; totBOk += bendOk;
            Console.WriteLine($"  {CtwSmsName(c)}: {ok}/{snd} sounding frames match ({Pct(ok, snd)}), bent frames (period off every semitone) {bendOk}/{bend} ({Pct(bendOk, bend)}){first}");
        }
        Console.WriteLine($"  all tones: {Pct(totOk, tot)} of {tot} sounding frames, bends {Pct(totBOk, totB)} of {totB}");
        return 0;
    }

    private static string CtwSmsName(int c) => BrokenNes2.Music.Flp.CtwSmsBackend.Names[c];
    private static int[] Table(string text, string name)
    {
        var m = Regex.Match(text, $@"\b{name}\s*=\s*U\[\d+\]\(([^)]*)\)");
        if (!m.Success) m = Regex.Match(text, $@"\[\]\s*{name}\s*\n\s*U\[\d+\]\(([^)]*)\)");
        if (!m.Success) throw new InvalidOperationException($"no {name} table");
        return m.Groups[1].Value.Split(',').Select(s => int.Parse(s.Trim())).ToArray();
    }
    private static string Pct(int a, int b) => b == 0 ? "-" : $"{100.0 * a / b:0.0}%";
    private static bool Same((int[] N, int[] Att, int Rate) w, (int[] Period, int[] Att, int Noise) g, int c) => g.Att[c] == w.Att[c] && g.Period[c] == w.N[c];
}
