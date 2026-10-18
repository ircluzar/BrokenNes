// Round 8 (record-nes): the EVIDENCE behind the NES recording's design. `FruityHost nesrec-survey <folder|rom> [--seconds 40] [--every 1] [--limit n] [--start 300]` runs NES games headless on the plugin's own
// machine (NesAudioMachine, FIX cores), presses Start once (most games' title music plays without it, many need it for the first level; a second press would pause many), and counts, per game, how its sound driver drives the
// chip: how many notes (a write to a channel's fourth register) were struck in constant-volume mode or with the hardware decay envelope, with the length counter running or halted, with the
// hardware sweep on; how often the duty changed under a sounding pulse; whether the triangle's linear counter ended notes by itself; whether the DMC played samples ($4015 bit 4 with bytes to
// play) or raw PCM ($4011 written many times a frame). The summary says how many games use each feature, which is what decides how a recording carries it into FL (round8-record.md, NES).
using NesEmulator;
using NesEmulator.Plugin;

namespace BrokenNes.FruityHost;

internal static class NesRecSurvey
{
    private sealed class Stats
    {
        public string Name = "";
        public long Writes;
        public readonly int[] Triggers = new int[4], TrigConst = new int[4], TrigEnv = new int[4], TrigHaltOff = new int[4], TrigSweep = new int[4], DutyChanges = new int[2], VolWrites = new int[4];
        public readonly int[] FramesAudible = new int[4], FramesEnvMode = new int[4], FramesSweep = new int[2];
        public int TriLinearOff, TriLinearNotes, Frames, DmcFrames, DmcStarts, Pcm4011, Frames4011Heavy, Writes4017, RetrigSameHi;
        public string? Error;
    }

    public static int Main(string[] args)
    {
        string path = args.Length > 1 ? args[1] : @"X:\EMULATION\Nintendo Entertainment System";
        double seconds = Opt(args, "--seconds", 40);
        int every = (int)Opt(args, "--every", 1), offset = (int)Opt(args, "--offset", 0), limit = (int)Opt(args, "--limit", 100000), startAt = (int)Opt(args, "--start", 300);
        var files = File.Exists(path) ? [path] : Directory.GetFiles(path, "*.nes").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Where((_, i) => i % every == offset).Take(limit).ToArray();
        var all = new List<Stats>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var f in files)
        {
            var st = Run(f, seconds, startAt);
            all.Add(st);
            Console.WriteLine(Line(st));
        }
        Console.WriteLine();
        Summary(all.Where(s => s.Error == null && s.Triggers.Sum() > 0).ToList(), all.Count(s => s.Error != null), all.Count(s => s.Error == null && s.Triggers.Sum() == 0));
        Console.WriteLine($"({files.Length} files in {sw.Elapsed.TotalSeconds:0} s)");
        return 0;
    }

    private static double Opt(string[] a, string name, double d) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? double.Parse(a[i + 1]) : d; }

    private static Stats Run(string file, double seconds, int startAt)
    {
        var st = new Stats { Name = Path.GetFileNameWithoutExtension(file) };
        try
        {
            var m = new NesAudioMachine(File.ReadAllBytes(file));
            var latch = new byte[0x18];
            var pad = new bool[8];
            int frameWrites4011 = 0;
            bool[] sounding = new bool[4];
            m.Nes.ApuWriteTap = (addr, v, clock) =>
            {
                st.Writes++;
                int r = addr - 0x4000;
                int old = latch[r];
                latch[r] = v;
                switch (r)
                {
                    case 0x00: case 0x04:
                    {
                        int c = r >> 2;
                        if (((old ^ v) & 0xC0) != 0 && sounding[c]) st.DutyChanges[c]++;
                        if (((old ^ v) & 0x0F) != 0) st.VolWrites[c]++;
                        break;
                    }
                    case 0x0C: if (((old ^ v) & 0x0F) != 0) st.VolWrites[3]++; break;
                    case 0x03: case 0x07: case 0x0B: case 0x0F:
                    {
                        int c = r >> 2;
                        st.Triggers[c]++;
                        if (c == 2) { st.TriLinearNotes++; if ((latch[8] & 0x80) == 0) st.TriLinearOff++; break; }
                        int ctl = latch[c * 4];
                        if ((ctl & 0x10) != 0) st.TrigConst[c]++; else st.TrigEnv[c]++;
                        if ((ctl & 0x20) == 0) st.TrigHaltOff[c]++;
                        if (c < 2 && (latch[c * 4 + 1] & 0x80) != 0 && (latch[c * 4 + 1] & 7) != 0) st.TrigSweep[c]++;
                        if (c < 2 && sounding[c] && ((old ^ v) & 7) == 0) st.RetrigSameHi++;
                        break;
                    }
                    case 0x11: frameWrites4011++; st.Pcm4011++; break;
                    case 0x15: if ((v & 0x10) != 0) st.DmcStarts++; break;
                    case 0x17: st.Writes4017++; break;
                }
            };
            int frames = (int)(seconds * NesAudioMachine.FramesPerSecond);
            var probe = m.Nes.ActiveApu as IApuStateProbe;
            var buf = new float[4096];
            for (int f = 0; f < frames; f++)
            {
                bool start = f >= startAt && f < startAt + 4;            // one press: a second one pauses many games
                pad[3] = start;
                m.Nes.SetInput(pad);
                frameWrites4011 = 0;
                m.RunFrame();
                while (m.ReadAvailable(buf) > 0) { }
                if (m.Crashed) { st.Error = "crashed: " + m.CrashInfo; break; }
                if (frameWrites4011 > 20) st.Frames4011Heavy++;
                st.Frames++;
                if (probe == null) continue;
                var s = probe.ProbeApuState();
                sounding[0] = s.Pulse1Enabled && s.Pulse1Length > 0 && s.Pulse1Volume > 0 && s.Pulse1Period >= 8;
                sounding[1] = s.Pulse2Enabled && s.Pulse2Length > 0 && s.Pulse2Volume > 0 && s.Pulse2Period >= 8;
                sounding[2] = s.TriangleEnabled && s.TriangleLength > 0 && s.TriangleLinear > 0 && s.TrianglePeriod >= 2;
                sounding[3] = s.NoiseEnabled && s.NoiseLength > 0 && s.NoiseVolume > 0;
                for (int c = 0; c < 4; c++) if (sounding[c]) st.FramesAudible[c]++;
                if (sounding[0] && !s.Pulse1ConstVol) st.FramesEnvMode[0]++;
                if (sounding[1] && !s.Pulse2ConstVol) st.FramesEnvMode[1]++;
                if (sounding[3] && !s.NoiseConstVol) st.FramesEnvMode[3]++;
                if (sounding[0] && s.Pulse1SweepEnabled && s.Pulse1SweepShift != 0) st.FramesSweep[0]++;
                if (sounding[1] && s.Pulse2SweepEnabled && s.Pulse2SweepShift != 0) st.FramesSweep[1]++;
                if (s.DmcBytesRemaining > 0) st.DmcFrames++;
            }
        }
        catch (Exception e) { st.Error = e.GetType().Name + ": " + e.Message; }
        return st;
    }

    private static string Pct(int a, int b) => b == 0 ? "  -" : $"{100.0 * a / b,3:0}";

    private static string Line(Stats s)
    {
        if (s.Error != null) return $"{s.Name,-48} ERROR {s.Error}";
        int trig = s.Triggers.Sum(), pn = s.Triggers[0] + s.Triggers[1] + s.Triggers[3];
        int env = s.TrigEnv[0] + s.TrigEnv[1] + s.TrigEnv[3], halt = s.TrigHaltOff[0] + s.TrigHaltOff[1] + s.TrigHaltOff[3], sw = s.TrigSweep[0] + s.TrigSweep[1];
        int aud = s.FramesAudible[0] + s.FramesAudible[1] + s.FramesAudible[3], envF = s.FramesEnvMode[0] + s.FramesEnvMode[1] + s.FramesEnvMode[3];
        return $"{s.Name,-48} notes {trig,5} | env {Pct(env, pn)}% of p/n notes, {Pct(envF, aud)}% of audible p/n frames | length-run {Pct(halt, pn)}% | sweep {Pct(sw, s.Triggers[0] + s.Triggers[1])}% notes {Pct(s.FramesSweep[0] + s.FramesSweep[1], s.FramesAudible[0] + s.FramesAudible[1])}% frames"
            + $" | duty chg {s.DutyChanges[0] + s.DutyChanges[1],4} | tri linear {Pct(s.TriLinearOff, s.TriLinearNotes)}% | DMC {Pct(s.DmcFrames, s.Frames)}% frames ({s.DmcStarts} starts), $4011 {s.Pcm4011} ({s.Frames4011Heavy} PCM frames) | same-hi retrig {s.RetrigSameHi} | $4017 {s.Writes4017}";
    }

    private static void Summary(List<Stats> ok, int errors, int silent)
    {
        int n = ok.Count;
        int Games(Func<Stats, bool> f) => ok.Count(f);
        double Share(Func<Stats, double> num, Func<Stats, double> den) => ok.Sum(den) == 0 ? 0 : ok.Sum(num) / ok.Sum(den) * 100;
        Console.WriteLine($"SUMMARY over {n} games that played notes ({errors} would not run, {silent} played none in the time):");
        Console.WriteLine($"  hardware decay envelope: used on 5%+ of a game's pulse/noise notes in {Games(s => s.TrigEnv[0] + s.TrigEnv[1] + s.TrigEnv[3] > 0.05 * (s.Triggers[0] + s.Triggers[1] + s.Triggers[3]))} games; {Share(s => s.TrigEnv[0] + s.TrigEnv[1] + s.TrigEnv[3], s => s.Triggers[0] + s.Triggers[1] + s.Triggers[3]):0.0}% of all pulse/noise notes; {Share(s => s.FramesEnvMode[0] + s.FramesEnvMode[1] + s.FramesEnvMode[3], s => s.FramesAudible[0] + s.FramesAudible[1] + s.FramesAudible[3]):0.0}% of audible pulse/noise frames");
        Console.WriteLine($"  length counter running (halt off) at the note: {Games(s => s.TrigHaltOff[0] + s.TrigHaltOff[1] + s.TrigHaltOff[3] > 0.05 * (s.Triggers[0] + s.Triggers[1] + s.Triggers[3]))} games (5%+ of notes); {Share(s => s.TrigHaltOff[0] + s.TrigHaltOff[1] + s.TrigHaltOff[3], s => s.Triggers[0] + s.Triggers[1] + s.Triggers[3]):0.0}% of pulse/noise notes");
        Console.WriteLine($"  hardware sweep on at the note: {Games(s => s.TrigSweep[0] + s.TrigSweep[1] > 0)} games at all, {Games(s => s.TrigSweep[0] + s.TrigSweep[1] > 0.05 * (s.Triggers[0] + s.Triggers[1]))} on 5%+ of pulse notes; {Share(s => s.TrigSweep[0] + s.TrigSweep[1], s => s.Triggers[0] + s.Triggers[1]):0.0}% of pulse notes; sweeping {Share(s => s.FramesSweep[0] + s.FramesSweep[1], s => s.FramesAudible[0] + s.FramesAudible[1]):0.0}% of audible pulse frames");
        Console.WriteLine($"  duty changed under a sounding pulse: {Games(s => s.DutyChanges[0] + s.DutyChanges[1] > 0)} games; {ok.Sum(s => s.DutyChanges[0] + s.DutyChanges[1])} changes for {ok.Sum(s => s.Triggers[0] + s.Triggers[1])} pulse notes");
        Console.WriteLine($"  triangle notes with the linear counter running (control off): {Games(s => s.TriLinearOff > 0.05 * s.TriLinearNotes && s.TriLinearNotes > 0)} games; {Share(s => s.TriLinearOff, s => s.TriLinearNotes):0.0}% of triangle notes");
        Console.WriteLine($"  DMC samples: {Games(s => s.DmcFrames > 0)} games ({Share(s => s.DmcFrames, s => s.Frames):0.0}% of all frames); raw PCM through $4011 (20+ writes in a frame): {Games(s => s.Frames4011Heavy > 0)} games");
        Console.WriteLine($"  a pulse's 4th register rewritten with the same high bits while sounding: {Games(s => s.RetrigSameHi > 0)} games ({ok.Sum(s => s.RetrigSameHi)} times)");
        Console.WriteLine($"  $4017 written: {Games(s => s.Writes4017 > 1)} games more than once");
    }
}
