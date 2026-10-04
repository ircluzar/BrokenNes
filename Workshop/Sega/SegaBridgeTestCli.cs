using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--sega-bridge-test [--only apu|unit|catalog|game] [--verbose]</c>: the Sega sound bridges, by measurement. No board is needed: register scripts go in, pitch and level are measured
/// out of the audio, and every number is printed.
/// <list type="bullet">
/// <item><b>apu</b>: <c>APU_SMS</c>, the NES-facing core (NES registers -> SN76489). Pitch of pulses and triangle in cents against the NES formula (the same registers on APU_FIX are measured beside it),
/// the PSG registers themselves, level against APU_FIX in dB, noise (white and periodic, fixed and tone-3 rates), the DMC and the notes the PSG cannot play (dropped and counted), channel masks,
/// a NES game's timing with the core against APU_FIX, and 0 bytes allocated while running.</item>
/// <item><b>unit</b>: <c>SmsSoundOnNes</c>, the Sega sound unit built on a NES chip (SN76489 registers -> NES). Pitch in cents, level in dB against the stock unit, noise mode, the Game Gear stereo mask, the
/// DC trick, PAL, FM writes counted, the factory ids, state, and 0 bytes allocated while running.</item>
/// <item><b>catalog</b>: the menu entries (Master System and Game Gear APU slot, the NES menu).</item>
/// </list>
/// Exit code 0 = every check passed. Proof that it can fail: see docs/projects/sega-tracks suites (a bug injected into each bridge is flagged, then reverted).
/// </summary>
internal static class SegaBridgeTestCli
{
    private static int failures, passes;
    private static bool verbose;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    private static void Info(string text) { if (verbose) Console.WriteLine("        " + text); }

    private static string Arg(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
    }

    public static int Run(string[] args)
    {
        verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        string only = Arg(args, "--only");
        bool Want(string s) => only.Length == 0 || only.Equals(s, StringComparison.OrdinalIgnoreCase);
        if (Want("catalog")) { Console.WriteLine("== catalog"); Catalog(); }
        if (Want("apu")) { Console.WriteLine("\n== APU_SMS: NES registers to the PSG"); ApuSms(); }
        if (Want("game")) { Console.WriteLine("\n== APU_SMS under a running NES game"); Game(Arg(args, "--rom")); }
        if (Want("unit")) { Console.WriteLine("\n== SmsSoundOnNes: the PSG replayed on a NES chip"); Unit(); }
        Console.WriteLine($"\n{passes} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------- measuring

    private const double NesHz = NesVoiceWriter.NesCpuHz;
    private static double Cents(double hz, double want) => double.IsNaN(hz) ? double.NaN : 1200 * Math.Log2(hz / want);
    private static double Db(double a, double b) => 20 * Math.Log10(a / b);

    /// <summary>Frequency from the rising zero crossings (linear interpolation between samples), after taking the mean out.</summary>
    private static double MeasureHz(float[] x, int rate)
    {
        if (x.Length < 100) return double.NaN;
        double mean = 0; foreach (float v in x) mean += v; mean /= x.Length;
        double first = 0, last = 0; int count = 0;
        for (int i = 1; i < x.Length; i++)
        {
            double a = x[i - 1] - mean, b = x[i] - mean;
            if (a < 0 && b >= 0)
            {
                double pos = (i - 1) + -a / (b - a);
                if (count == 0) first = pos;
                last = pos; count++;
            }
        }
        return count < 4 ? double.NaN : (count - 1) * rate / (last - first);
    }

    private static double Rms(float[] x)
    {
        if (x.Length == 0) return 0;
        double mean = 0; foreach (float v in x) mean += v; mean /= x.Length;
        double s = 0; foreach (float v in x) s += (v - mean) * (v - mean);
        return Math.Sqrt(s / x.Length);
    }

    /// <summary>The lag (in samples) of the first strong peak of the normalised autocorrelation between <paramref name="minLag"/> and <paramref name="maxLag"/>, and the peak's value.</summary>
    private static (int Lag, double Peak) Periodicity(float[] x, int minLag, int maxLag)
    {
        double mean = 0; foreach (float v in x) mean += v; mean /= x.Length;
        var y = new double[x.Length]; for (int i = 0; i < x.Length; i++) y[i] = x[i] - mean;
        int n = Math.Min(y.Length - maxLag, 8000);
        double e = 0; for (int i = 0; i < n; i++) e += y[i] * y[i];
        if (e <= 0) return (0, 0);
        var r = new double[maxLag + 1];
        double best = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double s = 0; for (int i = 0; i < n; i++) s += y[i] * y[i + lag];
            r[lag] = s / e; best = Math.Max(best, r[lag]);
        }
        for (int lag = minLag; lag <= maxLag; lag++)
            if (r[lag] >= 0.9 * best && (lag == maxLag || r[lag] >= r[lag + 1]) && (lag == minLag || r[lag] >= r[lag - 1])) return (lag, r[lag]);
        return (0, best);
    }

    /// <summary>A NES APU core on its own, driven by register writes and stepped by hand, collecting its mono output.</summary>
    private sealed class Rig
    {
        public readonly IAPU Apu;
        private readonly float[] buf = new float[8192];
        private readonly List<float> samples = new();
        public int Rate => Apu.GetSampleRate();

        public Rig(string id)
        {
            Apu = new NesApuHost(id).Apu;
            W(0x4017, 0x40); W(0x4015, 0x0F); W(0x4001, 0x08); W(0x4005, 0x08);
        }

        public void W(ushort address, byte value) => Apu.WriteAPURegister(address, value);

        public void Run(double seconds, bool keep)
        {
            long cycles = (long)(NesHz * seconds), done = 0;
            while (done < cycles)
            {
                int chunk = (int)Math.Min(1024, cycles - done);
                for (int k = 0; k < chunk; k += 32) Apu.Step(Math.Min(32, chunk - k));
                done += chunk;
                int n;
                while ((n = Apu.ReadSamples(buf)) > 0) if (keep) for (int i = 0; i < n; i++) samples.Add(buf[i]);
            }
        }

        public float[] Take() { var a = samples.ToArray(); samples.Clear(); return a; }

        /// <summary>Lets the note settle (0.25 s, unrecorded) and records <paramref name="seconds"/>.</summary>
        public float[] Capture(double seconds = 0.5) { Run(0.25, false); Run(seconds, true); return Take(); }

        public void Pulse(int ch, int timer, int volume, int duty = 2)
        {
            ushort b = (ushort)(0x4000 + ch * 4);
            W(b, (byte)(duty << 6 | 0x30 | volume)); W((ushort)(b + 2), (byte)timer); W((ushort)(b + 3), (byte)(0xF8 | timer >> 8));
        }

        public void Triangle(int timer) { W(0x4008, 0xFF); W(0x400A, (byte)timer); W(0x400B, (byte)(0xF8 | timer >> 8)); }
        public void Noise(int volume, int index, bool shortMode) { W(0x400C, (byte)(0x30 | volume)); W(0x400E, (byte)((shortMode ? 0x80 : 0) | index)); W(0x400F, 0xF8); }
        public void Silence() { W(0x4000, 0x30); W(0x4004, 0x30); W(0x4008, 0x80); W(0x400C, 0x30); }
    }

    // ---------------------------------------------------------------------------------------------- catalog

    private static void Catalog()
    {
        Check("CoreRegistry discovers the NES-facing core as APU id SMS", CoreRegistry.ApuIds.Contains("SMS", StringComparer.OrdinalIgnoreCase) && CoreRegistry.ApuTypes["SMS"] == typeof(APU_SMS));
        Check("the NES APU menu lists SMS under the Master System family, with an honest label",
            CoreCatalog.Options(ConsoleKind.Nes, CoreSlot.Apu).FirstOrDefault(o => o.Id == "SMS") is { } sms && sms.Family == "Master System" && sms.Label.Contains("DMC is dropped"));
        Check("... and the NES-family sound chip list (used by the other consoles' menus) does not include it", CoreCatalog.NesFamilyOf(CoreSlot.Apu, "SMS") == "Master System" && CoreCatalog.NesFamilyOf(CoreSlot.Apu, "FIX") == "NES");
        foreach (var console in new[] { ConsoleKind.MasterSystem, ConsoleKind.GameGear })
        {
            var apu = CoreCatalog.Options(console, CoreSlot.Apu);
            string stock = console == ConsoleKind.GameGear ? "GG" : "SMS";
            int own = console == ConsoleKind.GameGear ? 1 : 2;   // the stock unit, and on the Master System the Japanese console's SMS-FM (the PSG and the YM2413) right after it
            Check($"{Consoles.DisplayName(console)} APU menu: the stock unit {stock} first{(own == 2 ? ", then SMS-FM" : "")}, then the PSG on NES chips ({apu.Count - own} of them)", apu[0].Id == stock && (own == 1 || apu[1].Id == "SMS-FM") && apu.Count > 5 && apu.Skip(own).All(o => o.Id.StartsWith("NES:") && o.Family == "NES"));
            Check($"{Consoles.DisplayName(console)}: NES:FIX and NES:FIXS are offered; the labels say mono and no FM", apu.Any(o => o.Id == "NES:FIX") && apu.Any(o => o.Id == "NES:FIXS") && apu.Skip(own).All(o => o.Label.Contains("mono") && o.Label.Contains("no YM2413 FM")));
            Check($"{Consoles.DisplayName(console)}: no bridge core is listed as a NES chip (the Master System core itself, and the Game Boy and SNES ones, stay out)", apu.All(o => o.Id != "NES:SMS" && o.Id != "NES:DMG" && o.Id != "NES:SNES"));
            Check($"{Consoles.DisplayName(console)}: a stored NES:FIX is kept, a stored id the menu lacks falls back to the stock unit", CoreCatalog.Resolve(console, CoreSlot.Apu, "NES:FIX") == "NES:FIX" && CoreCatalog.Resolve(console, CoreSlot.Apu, "NES:NOPE") == stock);
        }
        Check("the CPU and picture slots of the Master System are unchanged (own cores only)", CoreCatalog.Options(ConsoleKind.MasterSystem, CoreSlot.Cpu).Count == 1 && CoreCatalog.Options(ConsoleKind.MasterSystem, CoreSlot.Ppu).Count == 1);
    }

    // ---------------------------------------------------------------------------------------------- APU_SMS

    private static void ApuSms()
    {
        var rig = new Rig("SMS");
        var sms = rig.Apu as APU_SMS;
        Check("the NES host applies the core: a NES APU named SMS is APU_SMS", sms != null && sms.CoreName.StartsWith("SMS:SN76489+"), sms?.CoreName ?? "(not APU_SMS)");
        if (sms == null) return;
        Check("its sample rate is the front core's (44100) and it says what it is", rig.Rate == 44100 && sms.Description.Contains("Master System"));

        // ---- pitch: pulses ----
        double worst = 0; var rows = new List<string>();
        foreach (int t in new[] { 1022, 800, 508, 253, 127, 63, 31 })
        {
            var r = new Rig("SMS"); var p = (APU_SMS)r.Apu;
            r.Pulse(0, t, 15);
            var x = r.Capture();
            double want = NesHz / (16.0 * (t + 1)), got = MeasureHz(x, r.Rate), err = Cents(got, want);
            bool exact = p.Psg.Period(0) == t + 1;
            worst = Math.Max(worst, Math.Abs(err));
            var f = new Rig("FIX"); f.Pulse(0, t, 15); double fixErr = Cents(MeasureHz(f.Capture(), f.Rate), want);
            rows.Add($"t={t}: {want:F1} Hz {err:+0.000;-0.000} c (FIX {fixErr:+0.000;-0.000} c)");
            Check($"pulse timer {t} ({want:F1} Hz): PSG period is {t + 1} and the audio measures {got:F2} Hz = {err:+0.000;-0.000} cents", exact && Math.Abs(err) < 0.5, $"period {p.Psg.Period(0)}");
        }
        Info(string.Join("; ", rows));
        Console.WriteLine($"        pulse pitch: worst error {worst:F3} cents over 7 notes (109 Hz to 3.5 kHz), PSG period = NES timer + 1 exactly");

        // pulse 2 and the sweep unit's quirk: negate set so a high timer does not mute (as the plugin writes it)
        {
            var r = new Rig("SMS"); var p = (APU_SMS)r.Apu;
            r.Pulse(1, 253, 15); var x = r.Capture();
            double want = NesHz / (16.0 * 254);
            Check($"pulse 2 plays tone 2: period {p.Psg.Period(1)}, {MeasureHz(x, r.Rate):F2} Hz ({Cents(MeasureHz(x, r.Rate), want):+0.000;-0.000} cents)", p.Psg.Period(1) == 254 && Math.Abs(Cents(MeasureHz(x, r.Rate), want)) < 0.5);
        }

        // ---- pitch: triangle on tone 3 (period 2 (t + 1), an octave lower range) ----
        worst = 0; rows.Clear();
        foreach (int t in new[] { 510, 400, 253, 126, 63, 31 })
        {
            var r = new Rig("SMS"); var p = (APU_SMS)r.Apu;
            r.Triangle(t);
            var x = r.Capture();
            double want = NesHz / (32.0 * (t + 1)), got = MeasureHz(x, r.Rate), err = Cents(got, want);
            worst = Math.Max(worst, Math.Abs(err));
            var f = new Rig("FIX"); f.Triangle(t); double fixErr = Cents(MeasureHz(f.Capture(), f.Rate), want);
            rows.Add($"t={t}: {want:F1} Hz {err:+0.000;-0.000} c (FIX {fixErr:+0.000;-0.000} c)");
            Check($"triangle timer {t} ({want:F1} Hz): tone 3 period {2 * (t + 1)}, audio {got:F2} Hz = {err:+0.000;-0.000} cents", p.Psg.Period(2) == 2 * (t + 1) && Math.Abs(err) < 0.5, $"period {p.Psg.Period(2)}");
        }
        Info(string.Join("; ", rows));
        Console.WriteLine($"        triangle pitch: worst error {worst:F3} cents over 6 notes (109 Hz to 1.7 kHz), tone 3 period = 2 (NES timer + 1) exactly");

        // ---- notes the PSG cannot play (its 10-bit period stops at 1023 = 109 Hz; tone 3 as a triangle stops there too): by policy silent or an octave up, always counted ----
        Check("the default policy for a NES game is to keep a low note, an octave up; the plugin's is to drop it", APU_SMS.DefaultLowNotes == LowNotePolicy.OctaveUp && ((APU_SMS)new Rig("SMS").Apu).LowNotes == LowNotePolicy.OctaveUp);
        {
            var r = new Rig("SMS"); var p = (APU_SMS)r.Apu; p.LowNotes = LowNotePolicy.Drop;
            r.Pulse(0, 1500, 15); r.W(0x4001, 0x08);   // 74 Hz: below the PSG's lowest tone (109 Hz)
            var x = r.Capture(0.3);
            Check("policy Drop: a pulse note below the PSG's range (74 Hz) is silent, not moved to another pitch", Rms(x) < 1e-4, $"rms {Rms(x):E1}");
            Check("... and it is counted, once, as a dropped low note", p.LowNotesDropped == 1 && p.LowNotesTransposed == 0, $"{p.LowNotesDropped}");
            r.Run(0.1, false);
            Check("... held longer it is still one note", p.LowNotesDropped == 1);
            r.Pulse(0, 600, 15); r.Capture(0.1); r.Pulse(0, 1500, 15); r.Run(0.1, false);
            Check("a second low note is a second drop", p.LowNotesDropped == 2, $"{p.LowNotesDropped}");
        }
        {
            var r = new Rig("SMS"); var p = (APU_SMS)r.Apu; p.LowNotes = LowNotePolicy.Drop;
            r.Triangle(900);   // 31 Hz
            var x = r.Capture(0.3);
            Check("policy Drop: a triangle note below the PSG's range (31 Hz, tone 3 would need period 1802) is silent and counted", Rms(x) < 1e-4 && p.LowNotesDropped == 1, $"rms {Rms(x):E1}, dropped {p.LowNotesDropped}");
        }
        {
            var r = new Rig("SMS"); var p = (APU_SMS)r.Apu; p.LowNotes = LowNotePolicy.OctaveUp;
            r.Pulse(0, 1500, 15); r.W(0x4001, 0x08);
            var x = r.Capture(0.5);
            double ideal = NesHz / (16.0 * 1501), got = MeasureHz(x, r.Rate), octave = Cents(got, ideal * 2);
            Check($"policy OctaveUp: a 74 Hz pulse plays the octave above, {got:F2} Hz ({octave:+0.00;-0.00} cents from exactly 2x; the half step of an odd period is under 1.2 cents), counted as transposed, not dropped", Math.Abs(octave) < 1.5 && p.LowNotesTransposed == 1 && p.LowNotesDropped == 0);
        }
        {
            var r = new Rig("SMS"); var p = (APU_SMS)r.Apu; p.LowNotes = LowNotePolicy.OctaveUp;
            r.Triangle(900);   // 31 Hz: two octaves would not be needed, one is enough for the triangle (period 1802 -> 901)
            var x = r.Capture(0.5);
            double ideal = NesHz / (32.0 * 901), got = MeasureHz(x, r.Rate), octave = Cents(got, ideal * 2);
            Check($"policy OctaveUp: a 31 Hz triangle plays the octave above, {got:F2} Hz ({octave:+0.00;-0.00} cents from exactly 2x), counted", Math.Abs(octave) < 0.5 && p.LowNotesTransposed == 1);
            r.Triangle(1900);  // 29.4 Hz: tone 3 would need period 3802, then 1901: still too big, so two octaves
            r.Capture(0.2); x = r.Capture(0.5);
            double ideal2 = NesHz / (32.0 * 1901), got2 = MeasureHz(x, r.Rate);
            Check($"... and a 29.4 Hz one two octaves up: {got2:F2} Hz ({Cents(got2, ideal2 * 4):+0.00;-0.00} cents from exactly 4x)", Math.Abs(Cents(got2, ideal2 * 4)) < 1.5);
        }

        // ---- level against APU_FIX, in dB (AC RMS of the whole capture; a low note so FIX's own 1 kHz low-pass hardly touches it) ----
        Console.WriteLine("        level against APU_FIX (110 Hz notes, AC RMS):");
        var levelRows = new List<string>(); double worstDb = 0;
        foreach (var (duty, dutyName) in new[] { (0, "12.5%"), (1, "25%"), (2, "50%"), (3, "75%") })
            foreach (int v in new[] { 15, 12, 9, 6, 3, 1 })
            {
                var a = new Rig("SMS"); a.Pulse(0, 1000, v, duty); var xa = a.Capture();
                var b = new Rig("FIX"); b.Pulse(0, 1000, v, duty); var xb = b.Capture();
                double d = Db(Rms(xa), Rms(xb));
                if (v >= 3) worstDb = Math.Max(worstDb, Math.Abs(d));
                levelRows.Add($"duty {dutyName} vol {v}: {d:+0.0;-0.0} dB");
                Check($"pulse duty {dutyName} volume {v}: PSG output is {d:+0.0;-0.0} dB from APU_FIX (limit +-2.0, +-3.0 for the quietest)", Math.Abs(d) <= (v >= 3 ? 2.0 : 3.0), $"{Rms(xa):F5} vs {Rms(xb):F5}");
            }
        Console.WriteLine($"        pulse level: worst {worstDb:F2} dB from APU_FIX over volumes 3-15 and all four duty cycles");
        {
            var a = new Rig("SMS"); a.Triangle(508); var xa = a.Capture();
            var b = new Rig("FIX"); b.Triangle(508); var xb = b.Capture();
            double d = Db(Rms(xa), Rms(xb));
            Check($"triangle (110 Hz): the square on tone 3 is {d:+0.0;-0.0} dB from the NES triangle (limit +-2.0)", Math.Abs(d) <= 2.0, $"{Rms(xa):F5} vs {Rms(xb):F5}");
        }
        {
            double prev = double.MaxValue; bool nonIncreasing = true; var seq = new List<double>();
            for (int v = 15; v >= 1; v--)
            {
                var a = new Rig("SMS"); a.Pulse(0, 253, v); double r = Rms(a.Capture(0.2));
                nonIncreasing &= r <= prev * 1.001; prev = r; seq.Add(r);
            }
            int distinct = seq.Select(s => Math.Round(s, 4)).Distinct().Count();
            Info("rms by volume 15..1: " + string.Join(" ", seq.Select(s => s.ToString("F4"))));
            Check($"pulse level never rises as the NES volume falls from 15 to 1, and covers {Db(seq[0], seq[^1]):F1} dB in {distinct} PSG levels (15 linear NES steps become the PSG's 2 dB steps: the loud ones merge)", nonIncreasing && distinct >= 9 && Db(seq[0], seq[^1]) > 20);
            var z = new Rig("SMS"); z.Pulse(0, 253, 0);
            Check("volume 0 is silent", Rms(z.Capture(0.2)) < 1e-4);
        }

        // ---- noise ----
        {
            var r = new Rig("SMS"); var p = (APU_SMS)r.Apu;
            r.Noise(15, 9, false);   // 7.05 kHz shift rate, long mode: PSG fixed rate 0 (6991 Hz), white noise
            var x = r.Capture(0.4);
            var (_, peak) = Periodicity(x, 20, 3000);
            Check($"NES long-mode noise at index 9 is white noise at the PSG's fastest fixed rate: noise register ${p.Psg.NoiseControl:X}, audible (rms {Rms(x):F3}), no periodicity (peak {peak:F2})", p.Psg.NoiseControl == 0x4 && Rms(x) > 0.02 && peak < 0.4);
        }
        {
            var r = new Rig("SMS"); var p = (APU_SMS)r.Apu;
            r.Noise(15, 9, true);    // short mode: periodic noise
            var x = r.Capture(0.4);
            var (_, peak) = Periodicity(x, 20, 3000);
            Check($"NES short-mode noise is periodic noise: FB bit clear, strongly periodic (peak {peak:F2}), audible", (p.Psg.NoiseControl & 4) == 0 && peak > 0.7 && Rms(x) > 0.02, $"noise register ${p.Psg.NoiseControl:X}");
        }
        Console.WriteLine("        short-mode buzz pitch (NES: shift rate / 93; PSG periodic noise: shift rate / 16), APU_SMS against APU_FIX:");
        {
            double worstBuzz = 0; var parts = new List<string>();
            foreach (int idx in new[] { 5, 6, 7, 8, 9, 10, 11 })
            {
                var a = new Rig("SMS"); a.Noise(15, idx, true); var xa = a.Capture(0.5);
                var b = new Rig("FIX"); b.Noise(15, idx, true); var xb = b.Capture(0.5);
                var pa = Periodicity(xa, 30, 3000); var pb = Periodicity(xb, 30, 3000);
                double hzA = pa.Lag > 0 ? 44100.0 / pa.Lag : double.NaN, hzB = pb.Lag > 0 ? 44100.0 / pb.Lag : double.NaN;
                double err = Cents(hzA, hzB);
                if (!double.IsNaN(err)) worstBuzz = Math.Max(worstBuzz, Math.Abs(err));
                parts.Add($"idx {idx}: SMS {hzA:F1} Hz, FIX {hzB:F1} Hz ({err:+0;-0} c)");
            }
            Console.WriteLine("        " + string.Join("; ", parts));
            Check($"short-mode buzz keeps its pitch within {worstBuzz:F0} cents over noise indices 5-11 (limit 60: whole autocorrelation lags and the PSG's integer tone-3 periods)", worstBuzz < 60);
        }
        {
            // tone 3 is free: any NES noise rate is exact through the PSG's fourth rate (shift rate follows tone 3); the triangle takes it away
            var r = new Rig("SMS"); var p = (APU_SMS)r.Apu;
            r.Noise(15, 7, false);   // 11.2 kHz: not one of the three fixed rates
            r.Capture(0.2);
            double hz = NesHz / 160.0; int expect = (int)Math.Round(APU_SMS.PsgClockHz / (32.0 * hz));
            Check($"noise index 7 (11.2 kHz) with tone 3 free uses the fourth rate: noise register ${p.Psg.NoiseControl:X} (white, rate 3), tone 3 period {p.Psg.Period(2)} (expect {expect}), no compromise", p.Psg.NoiseControl == 7 && p.Psg.Period(2) == expect && p.NoiseRateCompromises == 0);
            r.Triangle(253); r.Run(0.2, false);
            Check("... when the triangle takes tone 3 the noise falls back to the nearest fixed rate and the compromise is counted", (p.Psg.NoiseControl & 3) != 3 && p.NoiseRateCompromises > 0 && p.Psg.Period(2) == 2 * 254, $"noise ${p.Psg.NoiseControl:X}, compromises {p.NoiseRateCompromises}");
        }
        {
            var r = new Rig("SMS"); var p = (APU_SMS)r.Apu;
            r.Noise(15, 9, false); r.Run(0.1, false);
            long before = p.Syncs; int control = p.Psg.NoiseControl;
            r.Run(0.2, false);
            Check("a held noise does not rewrite the noise register (that would reload the shift register every sync)", p.Psg.NoiseControl == control && p.Syncs > before + 100);
        }

        // ---- the DMC has nowhere to go: dropped and counted ----
        {
            var r = new Rig("SMS"); var p = (APU_SMS)r.Apu;
            r.W(0x4010, 0x0F); r.W(0x4012, 0x00); r.W(0x4013, 0x10); r.W(0x4015, 0x1F);
            r.W(0x4011, 0x40); r.W(0x4011, 0x20); r.W(0x4011, 0x60);
            r.Run(0.05, false);
            Check($"a DMC sample start and three $4011 writes are counted as dropped ({p.DmcStartsDropped} start, {p.DmcDacWritesDropped} DAC writes) and reported in Describe()", p.DmcStartsDropped == 1 && p.DmcDacWritesDropped == 3 && p.Describe().Contains("DMC starts 1") && p.Describe().Contains("$4011 writes 3"));
            Check("the front core still runs the DMC: a $4015 read shows it playing (bit 4)", (r.Apu.ReadAPURegister(0x4015) & 0x10) != 0);
        }
        {
            // what a game can read stays real: the front core is stepped, so the frame IRQ flag rises and a short DMC sample plays out
            var r = new Rig("SMS"); r.W(0x4017, 0x00); r.Run(0.02, false);
            bool irq = (r.Apu.ReadAPURegister(0x4015) & 0x40) != 0;
            var q = new Rig("SMS"); q.W(0x4010, 0x0F); q.W(0x4012, 0x00); q.W(0x4013, 0x01); q.W(0x4015, 0x1F);
            bool on = (q.Apu.ReadAPURegister(0x4015) & 0x10) != 0; q.Run(0.03, false); bool off = (q.Apu.ReadAPURegister(0x4015) & 0x10) == 0;
            Check("the front core keeps what a game can read real: the frame IRQ flag rises ($4015 bit 6), a short DMC sample plays out (bit 4 set, then clear)", irq && on && off, $"irq {irq}, started {on}, finished {off}");
        }

        // ---- the plugin's two switches: the PSG sound does not depend on the front core, and the frame sequencer jumps give the same state as counting every cycle ----
        {
            float[] Render(bool stepFront)
            {
                var r = new Rig("SMS"); ((APU_SMS)r.Apu).StepFront = stepFront;
                r.Pulse(0, 253, 12); r.Pulse(1, 190, 9, 0); r.Triangle(126); r.Noise(8, 7, false);
                return r.Capture(0.4);
            }
            var withFront = Render(true); var without = Render(false);
            double maxDiff = 0; for (int i = 0; i < withFront.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(withFront[i] - without[i]));
            Check($"the PSG's sound is bit-identical whether the front NES core is stepped or not (largest difference {maxDiff:E1}; a host that never reads the chip back skips the front to save time)", withFront.Length == without.Length && maxDiff == 0);
            var off = new Rig("SMS"); ((APU_SMS)off.Apu).StepFront = false; off.W(0x4017, 0x00); off.Run(0.02, false);
            Check("... and with the front off, $4015 shows no frame IRQ (documented: the front's status is not real then)", (off.Apu.ReadAPURegister(0x4015) & 0x40) == 0);

            var rng = new Random(20261003); bool same = true; int steps = 0; string where = "";
            for (int script = 0; script < 40 && same; script++)
            {
                var a = new NesChannelModel(); var b = new NesChannelModel();
                for (int k = 0; k < 400 && same; k++)
                {
                    if (rng.Next(3) == 0)
                    {
                        ushort reg = rng.Next(5) switch { 0 => (ushort)(0x4000 + rng.Next(8)), 1 => (ushort)(0x4008 + rng.Next(8)), 2 => (ushort)0x4015, 3 => (ushort)0x4017, _ => (ushort)(0x4000 + rng.Next(0x18)) };
                        byte value = (byte)rng.Next(256);
                        a.Write(reg, value); b.Write(reg, value);
                    }
                    int cycles = rng.Next(4) switch { 0 => rng.Next(1, 40), 1 => rng.Next(1, 2000), 2 => rng.Next(1, 9000), _ => rng.Next(1, 40000) };
                    a.Clock(cycles); b.ClockBulk(cycles); steps++;
                    string sa = ModelState(a), sb = ModelState(b);
                    if (sa != sb) { same = false; where = $"script {script} step {k} after {cycles} cycles: {sa} vs {sb}"; }
                }
            }
            Check($"the frame-sequencer jump (ClockBulk) matches counting every cycle over {steps} random register scripts and cycle counts (all four step lengths, both modes)", same, where);
        }

        // ---- a loaded state: the model is rebuilt from the front core, so the PSG plays what the state was playing ----
        {
            var a = new Rig("SMS"); var pa = (APU_SMS)a.Apu;
            a.Pulse(0, 253, 12); a.Pulse(1, 190, 7, 1); a.Triangle(126); a.Noise(9, 9, false);
            a.Run(0.2, false);
            object state = a.Apu.GetState();
            var b = new Rig("SMS"); var pb = (APU_SMS)b.Apu;
            b.Apu.SetState(state);
            b.Run(0.05, false);
            bool same = pb.Psg.Period(0) == pa.Psg.Period(0) && pb.Psg.Period(1) == pa.Psg.Period(1) && pb.Psg.Period(2) == pa.Psg.Period(2)
                && Enumerable.Range(0, 4).All(c => pb.Psg.Volume(c) == pa.Psg.Volume(c)) && pb.Psg.NoiseControl == pa.Psg.NoiseControl;
            Check($"a state loaded into a fresh APU_SMS plays what the state was playing: tones {pb.Psg.Period(0)}/{pb.Psg.Period(1)}/{pb.Psg.Period(2)} volumes {pb.Psg.Volume(0)}/{pb.Psg.Volume(1)}/{pb.Psg.Volume(2)}/{pb.Psg.Volume(3)} noise ${pb.Psg.NoiseControl:X} (the original: {pa.Psg.Period(0)}/{pa.Psg.Period(1)}/{pa.Psg.Period(2)}, {pa.Psg.Volume(0)}/{pa.Psg.Volume(1)}/{pa.Psg.Volume(2)}/{pa.Psg.Volume(3)}, ${pa.Psg.NoiseControl:X})", same);
        }

        // ---- channel masks, reset, polyphony ----
        {
            var r = new Rig("SMS");
            r.Pulse(0, 253, 15); r.Pulse(1, 126, 15); r.Triangle(253); r.Noise(8, 9, false);
            var all = Rms(r.Capture(0.4));
            var maskTests = new List<string>(); bool ok = true;
            foreach (var (mask, name) in new[] { (0x1E, "pulse 1"), (0x1D, "pulse 2"), (0x1B, "triangle"), (0x17, "noise") })
            {
                r.Apu.SetEnabledChannels(mask); var x = r.Capture(0.3);
                double rr = Rms(x); maskTests.Add($"{name} off {rr / all:P0}"); ok &= rr < all * 0.97 && rr > 0;
            }
            r.Apu.SetEnabledChannels(0x00); var none = Rms(r.Capture(0.3));
            Info(string.Join(", ", maskTests));
            Check($"SetEnabledChannels removes each voice from the PSG ({string.Join(", ", maskTests)}) and 0 silences it ({none:E1})", ok && none < 1e-4);
        }
        {
            var r = new Rig("SMS"); var p = (APU_SMS)r.Apu;
            r.Pulse(0, 253, 15); r.Pulse(1, 190, 12); r.Triangle(126); r.Noise(10, 9, false);
            r.Run(0.1, false);
            var v = Enumerable.Range(0, 4).Select(c => p.Psg.Volume(c)).ToArray();
            Check($"four voices at once (polyphony): tones {p.Psg.Period(0)}/{p.Psg.Period(1)}/{p.Psg.Period(2)} noise ${p.Psg.NoiseControl:X}, volumes {string.Join("/", v)} all audible", p.Psg.Period(0) == 254 && p.Psg.Period(1) == 191 && p.Psg.Period(2) == 254 && v.All(k => k < 15));
            r.Apu.Reset();
            Check("Reset puts the PSG back to silence", Enumerable.Range(0, 4).All(c => p.Psg.Volume(c) == 15));
        }
        {
            // hot swap from the real chip: a note held on APU_FIX goes on sounding when the bus replays the registers into APU_SMS (NES.SetApuCore)
            var nes = new NES { RomName = "idle.nes" }; nes.LoadROM(SnesToNes.IdleRom());
            bool a = nes.SetApuCore("FIX");
            nes.ActiveApu!.WriteAPURegister(0x4017, 0x40); nes.ActiveApu.WriteAPURegister(0x4015, 0x0F); nes.ActiveApu.WriteAPURegister(0x4001, 0x08);
            nes.ActiveApu.WriteAPURegister(0x4000, 0xBF); nes.ActiveApu.WriteAPURegister(0x4002, 253); nes.ActiveApu.WriteAPURegister(0x4003, 0xF8);
            bool b = nes.SetApuCore("SMS");
            Check("NES.SetApuCore swaps APU_FIX for APU_SMS while a note is held (the application path of the desktop menu)", a && b && nes.GetApuCoreId() == "APU_SMS", nes.GetApuCoreId());
        }

        // ---- no allocation while playing (the plugin renders this on the audio thread) ----
        {
            var r = new Rig("SMS");
            r.Pulse(0, 253, 15); r.Pulse(1, 190, 12); r.Triangle(126); r.Noise(10, 7, false);
            r.Run(0.3, false);
            var buf = new float[512];
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++) { r.Apu.Step(32); r.Apu.ReadSamples(buf); if (i % 500 == 0) { r.Pulse(0, 200 + i % 100, 1 + i % 15); r.Noise(1 + i % 15, i % 16, i % 2 == 0); } }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check($"stepping 640,000 NES cycles with notes changing and the audio drained allocates {bytes} bytes (limit 0)", bytes == 0);
        }
    }

    /// <summary>Every field the channel model computes, as text, for comparing two models.</summary>
    private static string ModelState(NesChannelModel m)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in m.P) sb.Append($"[{p.Duty},{p.Vol},{p.Timer},{p.Len},{p.EnvDiv},{p.EnvDecay},{p.SweepDiv},{p.SweepPeriod},{p.SweepShift},{p.Halt},{p.Const},{p.EnvStart},{p.SweepOn},{p.SweepNeg},{p.SweepReload},{p.Enabled}]");
        sb.Append($"T[{m.TriTimer},{m.TriLen},{m.TriLinear},{m.TriReload},{m.TriControl},{m.TriReloadFlag},{m.TriEnabled}]");
        sb.Append($"N[{m.NoiseVol},{m.NoisePeriodIdx},{m.NoiseLen},{m.NoiseEnvDiv},{m.NoiseEnvDecay},{m.NoiseHalt},{m.NoiseConst},{m.NoiseEnvStart},{m.NoiseEnabled},{m.NoiseShort}]");
        sb.Append($"D[{m.DmcEnabled},{m.DmcStarts}] F[{m.FrameCycle}]");
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------- a NES game on APU_SMS

    private static string FindRom(string given)
    {
        if (given.Length > 0 && File.Exists(given)) return given;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string c = Path.Combine(dir.FullName, "Plugin", "BrokenNes2.Plugin", "Resources", "default.nes");
            if (File.Exists(c)) return c;
        }
        string here = Path.Combine(Directory.GetCurrentDirectory(), "Plugin", "BrokenNes2.Plugin", "Resources", "default.nes");
        return File.Exists(here) ? here : "";
    }

    private static string Fnv(byte[] data) { ulong h = 14695981039346656037UL; foreach (byte b in data) { h ^= b; h *= 1099511628211UL; } return h.ToString("x16"); }

    private static (string Hash, float[] Audio, APU_SMS? Sms) RunGame(byte[] rom, string apu, int frames, LowNotePolicy? policy = null)
    {
        var saved = APU_SMS.DefaultLowNotes;
        if (policy != null) APU_SMS.DefaultLowNotes = policy.Value;
        try
        {
            var nes = new NES { RomName = "game.nes" };
            nes.LoadROM(rom);
            var speed = nes.GetSpeedConfig(); if (speed != null) speed.NtscAccurateFrameRate = true;
            if (!nes.SetApuCore(apu)) throw new InvalidOperationException($"APU core {apu} not applied");
            var audio = new List<float>(); var buf = new float[8192];
            for (int f = 0; f < frames; f++)
            {
                nes.RunFrame();
                int n; while ((n = nes.ActiveApu!.ReadSamples(buf)) > 0) for (int i = 0; i < n; i++) audio.Add(buf[i]);
            }
            return (Fnv(nes.GetFrameBuffer()), audio.ToArray(), nes.ActiveApu as APU_SMS);
        }
        finally { APU_SMS.DefaultLowNotes = saved; }
    }

    /// <summary>Correlation of the loudness envelopes (RMS per 20 ms window) of two recordings of the same game.</summary>
    private static (double Corr, int Windows, int SilentWindows) EnvelopeCorrelation(float[] a, float[] b)
    {
        const int win = 882;
        int nw = Math.Min(a.Length, b.Length) / win;
        var ea = new double[nw]; var eb = new double[nw];
        for (int w = 0; w < nw; w++) { ea[w] = Rms(a.AsSpan(w * win, win).ToArray()); eb[w] = Rms(b.AsSpan(w * win, win).ToArray()); }
        double ma = ea.Average(), mb = eb.Average(), cov = 0, va = 0, vb = 0; int silent = 0;
        for (int w = 0; w < nw; w++) { cov += (ea[w] - ma) * (eb[w] - mb); va += (ea[w] - ma) * (ea[w] - ma); vb += (eb[w] - mb) * (eb[w] - mb); if (eb[w] > 0.01 && ea[w] < 0.002) silent++; }
        return (cov / Math.Sqrt(va * vb), nw, silent);
    }

    private static void Game(string romArg)
    {
        string path = FindRom(romArg);
        if (path.Length == 0) { Check("a NES ROM to run (Plugin/BrokenNes2.Plugin/Resources/default.nes, or --rom)", false, "not found"); return; }
        var rom = File.ReadAllBytes(path);
        const int frames = 900;
        var fix = RunGame(rom, "FIX", frames);
        var sms = RunGame(rom, "SMS", frames);                                    // the default policy: a low note plays an octave up
        var drop = RunGame(rom, "SMS", frames, LowNotePolicy.Drop);
        Check($"the game's picture is bit-identical on APU_SMS and APU_FIX after {frames} frames (the sound chip does not change the game's timing): {sms.Hash}", fix.Hash == sms.Hash && fix.Hash == drop.Hash, $"FIX {fix.Hash}, SMS {sms.Hash}");
        double rf = Rms(fix.Audio), rs = Rms(sms.Audio);
        Check($"the game's music is audible on the PSG: rms {rs:F4} (APU_FIX {rf:F4}, {Db(rs, rf):+0.0;-0.0} dB), {sms.Audio.Length} samples (APU_FIX {fix.Audio.Length})", rs > 0.005 && Math.Abs(sms.Audio.Length - fix.Audio.Length) < 600);
        var up = EnvelopeCorrelation(sms.Audio, fix.Audio); var dr = EnvelopeCorrelation(drop.Audio, fix.Audio);
        Check($"it is the same music: the loudness envelope of the PSG's version correlates {up.Corr:F3} with APU_FIX's over {up.Windows} windows of 20 ms (limit 0.8); {up.SilentWindows} windows are silent where FIX sounds", up.Corr > 0.8);
        Check($"keeping the game's low bass notes an octave up is closer to the original than dropping them: correlation {up.Corr:F3} vs {dr.Corr:F3} ({dr.SilentWindows} silent windows when dropped)", up.Corr > dr.Corr && dr.SilentWindows > up.SilentWindows);
        Console.WriteLine("        octave up: " + sms.Sms!.Describe());
        Console.WriteLine("        drop:      " + drop.Sms!.Describe());
        Check("the counters say what happened: low notes were transposed (policy OctaveUp) or silenced (Drop), none of the other kind", sms.Sms.LowNotesTransposed > 0 && sms.Sms.LowNotesDropped == 0 && drop.Sms.LowNotesDropped > 0 && drop.Sms.LowNotesTransposed == 0, $"{sms.Sms.LowNotesTransposed} transposed; {drop.Sms.LowNotesDropped} dropped");
    }

    // ---------------------------------------------------------------------------------------------- SmsSoundOnNes

    private static SmsSoundRequest Request(SegaRegion region = SegaRegion.Ntsc, string? detail = null, PsgVariant variant = PsgVariant.Sega, ConsoleKind console = ConsoleKind.MasterSystem) =>
        new(new SegaMachine(SegaTimeline.MasterSystem(region, console), GenesisModel.Model1, 44100), console, variant, detail);

    private static void Tone(ISmsSound u, int channel, int period) { u.WritePsg((byte)(0x80 | channel << 5 | (period & 0xF))); u.WritePsg((byte)(period >> 4 & 0x3F)); }
    private static void Vol(ISmsSound u, int channel, int k) => u.WritePsg((byte)(0x90 | channel << 5 | k));

    /// <summary>Runs a unit for <paramref name="seconds"/> of machine time and returns its mono samples.</summary>
    private static float[] Play(ISmsSound u, double z80Hz, double seconds, bool keep = true)
    {
        var samples = new List<float>(); var buf = new short[4096];
        long clocks = (long)(z80Hz * seconds), done = 0;
        while (done < clocks)
        {
            int chunk = (int)Math.Min(228, clocks - done);   // a scanline at a time, like a board
            u.Run(chunk); done += chunk;
            if (done % 22800 < 228)
            {
                int n; while ((n = u.ReadSamples(buf)) > 0) if (keep) for (int i = 0; i < n; i += 2) samples.Add((buf[i] + buf[i + 1]) / 65536f);
            }
        }
        { int n; while ((n = u.ReadSamples(buf)) > 0) if (keep) for (int i = 0; i < n; i += 2) samples.Add((buf[i] + buf[i + 1]) / 65536f); }
        return samples.ToArray();
    }

    private static float[] Settled(float[] x, int rate, double skipSeconds = 0.25) => x.Skip((int)(rate * skipSeconds)).ToArray();

    private static void Unit()
    {
        const double ntscZ80 = 3_579_545.0;
        var req = Request();
        // ---- factory ----
        var plain = SmsSoundFactories.Create("NES", req);
        Check("the factory builds the unit under id NES (FIX behind it) and NES:FIXS names the chip", plain is SmsSoundOnNes { BackApuId: "FIX" } && SmsSoundFactories.Create("NES:FIXS", req) is SmsSoundOnNes { BackApuId: "FIXS" } && SmsSoundFactories.Create("nes:qn", req) is SmsSoundOnNes { BackApuId: "qn" });
        bool threw1 = false, threw2 = false;
        try { SmsSoundFactories.Create("NES:NOPE", req); } catch (ArgumentException) { threw1 = true; }
        try { SmsSoundFactories.Create("NOPE:FIX", req); } catch (ArgumentException) { threw2 = true; }
        Check("an unknown NES chip, or an unknown unit with a colon, is an error (never a quiet substitute)", threw1 && threw2);
        Check("the stock unit is still what SMS builds", SmsSoundFactories.Create("SMS", req).CoreName == "SN76489" && SmsSoundFactories.Ids.Contains("NES"));

        var unit = (SmsSoundOnNes)plain;
        Check($"it says what it is ({unit.CoreName}), reports its rate ({unit.SampleRate} Hz), has no FM chip and the audio-control port reads $FF", unit.CoreName.StartsWith("NES:") && unit.SampleRate == 44100 && !unit.HasFm && unit.ReadAudioControl() == 0xFF);

        // ---- pitch ----
        double worst = 0; var rows = new List<string>();
        foreach (var (channel, periods) in new[] { (0, new[] { 1023, 508, 254, 127, 64, 32 }), (1, new[] { 254 }), (2, new[] { 1023, 511, 254, 127, 64 }) })
            foreach (int n in periods)
            {
                var u = (SmsSoundOnNes)SmsSoundFactories.Create("NES", req);
                Tone(u, channel, n); Vol(u, channel, 0);
                var x = Settled(Play(u, ntscZ80, 0.75), u.SampleRate);
                double want = ntscZ80 / (32.0 * n), got = MeasureHz(x, u.SampleRate), err = Cents(got, want);
                // a NES timer has whole steps: the pulse timer t+1 = N exactly (NTSC); the triangle's t+1 = N/2, so an odd period is half a step off
                double allowed = channel < 2 ? 0.5 : (n % 2 == 1 ? 1200 * Math.Log2(1 + 1.0 / n) + 0.5 : 0.5);
                worst = Math.Max(worst, Math.Abs(err));
                rows.Add($"tone {channel + 1} N={n}: {want:F1} Hz {err:+0.00;-0.00} c");
                Check($"tone {channel + 1} period {n} ({want:F1} Hz) on the NES {(channel < 2 ? "pulse" : "triangle")}: audio {got:F2} Hz = {err:+0.00;-0.00} cents (limit {allowed:F2})", Math.Abs(err) <= allowed);
            }
        Console.WriteLine($"        tone pitch through the NES chip: worst {worst:F2} cents (the NES triangle has half the PSG's period steps)");

        // ---- level against the stock unit (tone 1; the stock output tops out 2.5 dB above the NES pulse's loudest step, and below -23.5 dB the NES has no step) ----
        Console.WriteLine("        level of tone 1 on the NES chip against the stock PSG unit:");
        double worstLevel = 0; var levelRows = new List<string>();
        foreach (int k in new[] { 0, 2, 4, 6, 8, 10, 12, 14 })
        {
            var a = SmsSoundFactories.Create("NES", req); Tone(a, 0, 508); Vol(a, 0, k);
            var b = new SmsSound(ntscZ80, 44100); Tone(b, 0, 508); Vol(b, 0, k);
            double ra = Rms(Settled(Play(a, ntscZ80, 0.6), 44100)), rb = Rms(Settled(Play(b, ntscZ80, 0.6), 44100));
            double d = Db(ra, rb);
            worstLevel = Math.Max(worstLevel, Math.Abs(d));
            levelRows.Add($"vol {k}: {d:+0.0;-0.0} dB");
            Check($"PSG volume {k} ({-2 * k} dB): the NES pulse plays {d:+0.0;-0.0} dB against the stock unit (limit +-3.5: the NES pulse's loudest step is 2.5 dB under the stock unit's, and its quietest, volume 1, is 23.5 dB under it)", Math.Abs(d) <= 3.5, $"{ra:F5} vs {rb:F5}");
        }
        Console.WriteLine($"        worst {worstLevel:F1} dB; " + string.Join(", ", levelRows));
        {
            // the NES triangle has no volume: tone 3 at -10 dB still plays at the triangle's one level, which the bridge counts
            var a = (SmsSoundOnNes)SmsSoundFactories.Create("NES", req); Tone(a, 2, 254); Vol(a, 2, 0);
            var b = (SmsSoundOnNes)SmsSoundFactories.Create("NES", req); Tone(b, 2, 254); Vol(b, 2, 5);
            double ra = Rms(Settled(Play(a, ntscZ80, 0.5), 44100)), rb = Rms(Settled(Play(b, ntscZ80, 0.5), 44100));
            Check($"the NES triangle has no volume control: tone 3 at 0 dB and at -10 dB play within {Math.Abs(Db(ra, rb)):F2} dB of each other, and the loss is counted ({b.TriangleLevelsLost} syncs at a reduced level, {a.TriangleLevelsLost} at full)", Math.Abs(Db(ra, rb)) < 0.5 && b.TriangleLevelsLost > 100 && a.TriangleLevelsLost == 0);
        }

        // ---- noise: white on the long mode, periodic on the short mode ----
        foreach (var (reg, name, wantPeriodic) in new[] { (0xE4, "white", false), (0xE0, "periodic", true) })
        {
            var u = SmsSoundFactories.Create("NES", req);
            u.WritePsg((byte)reg); Vol(u, 3, 0);
            var x = Settled(Play(u, ntscZ80, 0.6), 44100);
            var (lag, peak) = Periodicity(x, 20, 3000);
            Check($"PSG {name} noise (register ${reg:X2}) is {(wantPeriodic ? "periodic" : "white")} on the NES noise channel: rms {Rms(x):F3}, periodicity {peak:F2} at lag {lag}", Rms(x) > 0.02 && (wantPeriodic ? peak > 0.7 : peak < 0.4));
        }
        {
            var u = SmsSoundFactories.Create("NES", req); u.WritePsg(0xE4); Vol(u, 3, 15);
            Check("noise at volume 15 (off) is silent", Rms(Settled(Play(u, ntscZ80, 0.4), 44100)) < 1e-4);
        }

        // ---- the Game Gear stereo mask, the DC trick, voices ----
        {
            var u = (SmsSoundOnNes)SmsSoundFactories.Create("NES", Request(console: ConsoleKind.GameGear)); Tone(u, 0, 254); Vol(u, 0, 0);
            u.WriteStereo(0x00);
            var muted = Rms(Settled(Play(u, ntscZ80, 0.4), 44100));
            u.WriteStereo(0xFF);
            var open = Rms(Settled(Play(u, ntscZ80, 0.4), 44100));
            Check($"a voice the Game Gear stereo port mutes on both sides is silent on the NES chip ({muted:E1}) and plays again when unmuted ({open:F3})", muted < 1e-4 && open > 0.02);
            long before = u.StereoWritesLossy;
            u.WriteStereo(0xF0);   // everything left only
            u.WriteStereo(0xFF); u.WriteStereo(0x00);
            Check($"stereo that cannot be kept (a voice on one side only) is counted: {u.StereoWritesLossy - before} of 3 writes", u.StereoWritesLossy - before == 1);
        }
        {
            var u = SmsSoundFactories.Create("NES", req); Tone(u, 0, 1); Vol(u, 0, 0);
            Check("tone period 1 (the Sega part's DC trick behind PCM playback) is not a note: silent on the NES chip", Rms(Settled(Play(u, ntscZ80, 0.4), 44100)) < 1e-4);
            var v = new HubVoice[4]; u.DescribeVoices(v);
            Check("... and the hub says so too", !v[0].Audible);
        }
        {
            var u = (SmsSoundOnNes)SmsSoundFactories.Create("NES", req);
            u.WriteFmAddress(0x30); u.WriteFmData(0x10); u.WriteFmAddress(0x37); u.WriteAudioControl(0x03);
            Check($"writes to the YM2413 ports are counted as dropped ({u.FmWritesDropped}) and the unit still reports no FM chip; Describe() says so", u.FmWritesDropped == 3 && !u.HasFm && u.Describe().Contains("FM writes 3"));
        }

        // ---- four voices at once on one NES chip bank, and the extension-bank chip ----
        foreach (string chip in new[] { "FIX", "FIXS" })
        {
            var u = (SmsSoundOnNes)SmsSoundFactories.Create("NES:" + chip, req);
            Tone(u, 0, 254); Vol(u, 0, 0); Tone(u, 1, 190); Vol(u, 1, 2); Tone(u, 2, 508); Vol(u, 2, 0); u.WritePsg(0xE5); Vol(u, 3, 6);
            var x = Settled(Play(u, ntscZ80, 0.6), 44100);
            Check($"all four voices play together on {chip} (rms {Rms(x):F3}), none dropped for want of a channel ({u.Writer.Dropped}); the writer has {u.Writer.Banks} bank(s)", Rms(x) > 0.05 && u.Writer.Dropped == 0 && u.Writer.Banks == (chip == "FIXS" ? 2 : 1));
        }

        // ---- PAL: the Z80 clock is slower, so every note is lower; the NES chip is told the real pitch ----
        {
            double palZ80 = 3_546_895.0;
            var u = SmsSoundFactories.Create("NES", Request(SegaRegion.Pal)); Tone(u, 0, 254); Vol(u, 0, 0);
            var x = Settled(Play(u, palZ80, 0.75), 44100);
            double want = palZ80 / (32.0 * 254), got = MeasureHz(x, 44100), err = Cents(got, want);
            // the NES pulse timer steps are 0.9% of a PAL period: allow the rounding
            Check($"PAL: tone 1 period 254 plays {got:F2} Hz (expect {want:F2}, {err:+0.00;-0.00} cents; NTSC would be {ntscZ80 / (32.0 * 254):F2})", Math.Abs(err) < 1200 * Math.Log2(1 + 0.5 / 254) + 0.5);
        }

        // ---- timing: audio keeps the Z80 clock ----
        {
            var u = SmsSoundFactories.Create("NES", req); Tone(u, 0, 254); Vol(u, 0, 0);
            var x = Play(u, ntscZ80, 2.0);
            Check($"2.0 s of Z80 time gives {x.Length} samples (expect 88200 +-2%)", Math.Abs(x.Length - 88200) < 88200 * 0.02);
        }

        // ---- state and reset ----
        {
            var u = (SmsSoundOnNes)SmsSoundFactories.Create("NES", req); Tone(u, 0, 300); Vol(u, 0, 1); u.WritePsg(0xE6);
            var ms = new MemoryStream(); u.SaveState(new BinaryWriter(ms)); string before = u.Describe();
            u.Reset();
            bool quiet = u.Psg.Volume(0) == 15;
            ms.Position = 0; u.LoadState(new BinaryReader(ms));
            Check("state round-trips (registers come back) and Reset silences the unit", quiet && u.Psg.Period(0) == 300 && u.Psg.Volume(0) == 1 && u.Psg.NoiseControl == 6, u.Describe());
        }

        // ---- no allocation while playing ----
        {
            var u = SmsSoundFactories.Create("NES", req);
            Tone(u, 0, 254); Vol(u, 0, 0); Tone(u, 1, 190); Vol(u, 1, 2); Tone(u, 2, 508); Vol(u, 2, 0); u.WritePsg(0xE5); Vol(u, 3, 6);
            Play(u, ntscZ80, 0.3, keep: false);
            var buf = new short[4096];
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 3000; i++) { u.Run(228); if (i % 100 == 0) { Tone(u, 0, 200 + i % 300); u.ReadSamples(buf); } }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check($"3000 scanlines of Z80 time with notes changing and the audio drained allocate {bytes} bytes (limit 0)", bytes == 0);
        }
    }
}
