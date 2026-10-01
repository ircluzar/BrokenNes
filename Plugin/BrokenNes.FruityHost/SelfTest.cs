// The BrokenNes2 certification suite. Every test plays real notes into the loaded plugin DLL through the
// host simulation and measures what comes out: register values read back from the plugin, and pitch,
// loudness and stereo balance measured in the audio itself.
using System.Diagnostics;
using BrokenNes.Fruity;

namespace BrokenNes.FruityHost;

public enum Outcome { Pass, Fail, Warn }

public sealed record TestResult(string Id, string Name, Outcome Outcome, string Detail, double Ms);

/// <summary>Collects the checks made by one test.</summary>
public sealed class T
{
    public List<string> Failures { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Info { get; } = new();

    public void Expect(bool ok, string what) { if (!ok) Failures.Add(what); }
    public void Warn(bool ok, string what) { if (!ok) Warnings.Add(what); }
    public void Note(string s) => Info.Add(s);
    public void NearCents(string what, double hz, double expectedHz, double tolCents)
    {
        double e = double.IsNaN(hz) ? double.NaN : Audio.Cents(hz, expectedHz);
        if (double.IsNaN(e) || Math.Abs(e) > tolCents) Failures.Add($"{what}: measured {hz:0.00} Hz vs {expectedHz:0.00} Hz ({e:+0.0;-0.0} cents, tolerance {tolCents})");
    }
}

public sealed unsafe class SelfTest
{
    private readonly PluginLibrary lib;
    private readonly string dllPath;

    public SelfTest(string dllPath)
    {
        this.dllPath = dllPath;
        lib = new PluginLibrary(dllPath);
    }

    private static readonly string[] ParamNames = ["Sound chip", "Volume", "Pan", "Coarse", "Fine", "Pulse duty", "Channel", "Noise mode"];

    /// <summary>A one-note scenario where 1 beat = 1 second.</summary>
    private static Scenario One(double key, int color, double seconds = 0.8, double velocity = 0.78, double pan = 0)
    {
        var s = new Scenario { Name = "one", Tempo = 60, LengthBeats = seconds + 0.2 };
        s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = seconds, Key = key, Color = color, Velocity = velocity, Pan = pan });
        return s;
    }

    private static float[] Mono(float[] stereo) => Audio.Mono(stereo, 2);

    /// <summary>Plays a scenario into a fresh plugin and returns (stereo audio, mixer log, plugin still alive for probing).</summary>
    private (float[] Audio, Mixer Mixer, PluginInstance Plugin) Play(Scenario s, int rate = 44100, int block = 256,
        Action<PluginInstance>? setup = null, Action<PluginInstance, int>? probe = null)
    {
        var plugin = lib.Create();
        setup?.Invoke(plugin);
        Mixer mixer = null!;
        mixer = new Mixer { SampleRate = rate, BlockSize = block, AfterBlock = probe == null ? null : sample => probe(plugin, sample) };
        var audio = mixer.Run(plugin, s);
        return (audio, mixer, plugin);
    }

    /// <summary>Steady-state frequency of the (only) note: skips the first 150 ms and the last 100 ms of it.</summary>
    private static double SteadyHz(float[] mono, int rate, NoteLog log, double fromMs = 150, double tailMs = 100)
    {
        int a = log.StartSample + (int)(fromMs * rate / 1000), b = log.EndSample - (int)(tailMs * rate / 1000);
        return Audio.Frequency(mono, rate, a, b);
    }

    private static string SetChip(PluginInstance p, string id)
    {
        for (int i = 0; ; i++)
        {
            string n = p.GetName(Fpn.ParamValue, 0, i);
            if (n == "?" || n == "") throw new ArgumentException($"no sound chip '{id}'");
            if (n == id) { p.SetParam(0, i); return n; }
        }
    }

    private static List<string> Chips(PluginInstance p)
    {
        var list = new List<string>();
        for (int i = 0; ; i++)
        {
            string n = p.GetName(Fpn.ParamValue, 0, i);
            if (n == "?" || n == "") break;
            list.Add(n);
        }
        return list;
    }

    // ------------------------------------------------------------------------------------------------

    public List<TestResult> Run(string? only = null, Action<TestResult>? onResult = null)
    {
        var tests = new (string Id, string Name, Action<T> Body)[]
        {
            ("load", "DLL exports CreatePlugInstance; plugin identity and flags", Load),
            ("params", "Parameters: names, value text, info flags, automation, clamping", Params),
            ("state", "Saved state round-trips; foreign, truncated and future state is survived", State),
            ("silence", "No voices: exact silence, nothing non-finite", Silence),
            ("pitch", "Pitch accuracy: pulse 1/2 and triangle across the range (register and audio)", PitchAccuracy),
            ("range", "Pitch outside the chip's range clamps without breaking", PitchRange),
            ("coarse-fine", "Coarse and Fine tuning shift the pitch by exactly that much", CoarseFine),
            ("slide", "Piano-roll slide: the pitch follows the glide up and down", Slide),
            ("bend", "Per-note pitch automation (vibrato) is followed", Bend),
            ("volume", "Note velocity and master volume set the level; zero is silent", Volume),
            ("pan", "Master pan places the sound left and right", Pan),
            ("routing", "Note colour (and the Channel parameter) picks the NES channel", Routing),
            ("mono", "Each channel is monophonic: the newest note wins and the older one resumes", Mono_),
            ("lifecycle", "Voices: released voices are handed back (Voice_Kill), bad handles are harmless", Lifecycle),
            ("cores", "Every NES sound chip produces the note; accurate ones at the right pitch", Cores),
            ("swap", "Switching the sound chip while a note is held keeps it sounding at pitch", Swap),
            ("rates", "Sample rates 22.05-96 kHz and odd block sizes (1..4096) keep pitch and continuity", RatesAndBlocks),
            ("realtime", "Real-time: well faster than real time, and no allocation while playing", Realtime),
            ("editor", "Editor opens, shows the parameters, reports changes to the host, closes", Editor),
            ("robust", "Many instances, concurrent instances, garbage calls, destroy with voices playing", Robust),
            ("log", "BROKENNES_PLUGIN_LOG records the host's calls", LogTest),
        };

        var results = new List<TestResult>();
        foreach (var (id, name, body) in tests)
        {
            if (only != null && !only.Split(',').Contains(id)) continue;
            var t = new T();
            var sw = Stopwatch.StartNew();
            Outcome outcome; string detail;
            try
            {
                body(t);
                outcome = t.Failures.Count > 0 ? Outcome.Fail : t.Warnings.Count > 0 ? Outcome.Warn : Outcome.Pass;
                detail = string.Join("; ", t.Failures.Concat(t.Warnings.Select(w => "warning: " + w)).Concat(t.Info));
            }
            catch (Exception e)
            {
                outcome = Outcome.Fail;
                detail = $"exception {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}";
            }
            var r = new TestResult(id, name, outcome, detail, sw.Elapsed.TotalMilliseconds);
            results.Add(r);
            onResult?.Invoke(r);
        }
        return results;
    }

    // ------------------------------------------------------------------------------------------------

    private void Load(T t)
    {
        t.Expect(lib.HasExport("CreatePlugInstance"), "no CreatePlugInstance export");
        using var p = lib.Create();
        t.Expect(p.SdkVersion == 1, $"SDKVersion {p.SdkVersion}");
        t.Expect(p.LongName == "BrokenNes2", $"LongName '{p.LongName}'");
        t.Expect(p.ShortName == "BrokenNes2", $"ShortName '{p.ShortName}'");
        t.Expect((p.Flags & Fpf.Generator) != 0, "FPF_Generator not set");
        t.Expect((p.Flags & Fpf.NewVoiceParams) != 0, "FPF_NewVoiceParams not set (the host would use the old voice struct)");
        t.Expect((p.Flags & Fpf.WantNewTick) != 0, "FPF_WantNewTick not set");
        t.Expect(p.NumParams == ParamNames.Length, $"NumParams {p.NumParams}, expected {ParamNames.Length}");
        t.Expect(p.EditorHandle == 0, "EditorHandle non-zero before the editor was shown");
        t.Note($"{p.LongName}, flags 0x{p.Flags:X}, {p.NumParams} params");
    }

    private void Params(T t)
    {
        using var p = lib.Create();
        for (int i = 0; i < ParamNames.Length; i++)
            t.Expect(p.GetName(Fpn.Param, i, 0) == ParamNames[i], $"param {i} name '{p.GetName(Fpn.Param, i, 0)}'");
        t.Expect(p.GetName(Fpn.Param, 99, 0) == "", "name for an out-of-range parameter is not empty");
        t.Expect(p.GetName(Fpn.ParamValue, 1, 500) == "50.0%", $"Volume 500 -> '{p.GetName(Fpn.ParamValue, 1, 500)}'");
        t.Expect(p.GetName(Fpn.ParamValue, 2, -30) == "30% L", $"Pan -30 -> '{p.GetName(Fpn.ParamValue, 2, -30)}'");
        t.Expect(p.GetName(Fpn.ParamValue, 3, 12) == "+12 st", $"Coarse 12 -> '{p.GetName(Fpn.ParamValue, 3, 12)}'");
        t.Expect(p.GetName(Fpn.ParamValue, 5, 1) == "25%", $"Duty 1 -> '{p.GetName(Fpn.ParamValue, 5, 1)}'");
        var chips = Chips(p);
        t.Expect(chips.Count >= 20 && chips.Contains("FIX") && chips.Contains("DMG") && chips.Contains("QN"), $"sound chip list: {string.Join(",", chips)}");
        t.Expect(p.GetName(Fpn.ParamValue, 0, p.GetParam(0)) == "FIX", "default sound chip is not FIX");
        t.Note($"{chips.Count} sound chips");

        t.Expect((p.Dispatcher(Fpd.GetParamInfo, 2) & Pi.Centered) != 0, "Pan not 'centered'");
        t.Expect((p.Dispatcher(Fpd.GetParamInfo, 0) & Pi.CantInterpolate) != 0, "Sound chip should not interpolate");
        t.Expect(p.Dispatcher(Fpd.GetParamInfo, 1) == 0, "Volume info flags");

        // set / get / clamp
        p.SetParam(1, 321); t.Expect(p.GetParam(1) == 321, "set/get volume");
        p.SetParam(1, 99999); t.Expect(p.GetParam(1) == 1000, $"volume clamps high: {p.GetParam(1)}");
        p.SetParam(2, -99999); t.Expect(p.GetParam(2) == -100, $"pan clamps low: {p.GetParam(2)}");
        p.ProcessParam(77, 1, Rec.UpdateValue);   // out of range: must not crash
        // automation arrives as 0..2^30 with FromMIDI
        p.SetParamFromAutomation(3, 0.5); t.Expect(p.GetParam(3) == 0, $"automation 0.5 -> coarse {p.GetParam(3)}");
        p.SetParamFromAutomation(3, 1.0); t.Expect(p.GetParam(3) == 24, $"automation 1.0 -> coarse {p.GetParam(3)}");
        p.SetParamFromAutomation(3, 0.0); t.Expect(p.GetParam(3) == -24, $"automation 0.0 -> coarse {p.GetParam(3)}");
        p.SetParamFromAutomation(6, 0.5); t.Expect(p.GetParam(6) == 2, $"automation 0.5 -> channel {p.GetParam(6)}");
    }

    private void State(T t)
    {
        using var a = lib.Create();
        int[] want = [3, 612, -41, 7, -23, 2, 3, 1];
        for (int i = 0; i < want.Length; i++) a.SetParam(i, want[i]);
        var blob = a.SaveState();
        t.Expect(blob.Length > 8, $"state blob is {blob.Length} bytes");
        using (var b = lib.Create())
        {
            b.LoadState(blob);
            for (int i = 0; i < want.Length; i++) t.Expect(b.GetParam(i) == want[i], $"restored param {i}: {b.GetParam(i)} != {want[i]}");
            t.Expect(b.SaveState().SequenceEqual(blob), "save -> load -> save is not stable");
        }
        // hostile streams must leave the defaults and never crash
        foreach (var (name, data) in new (string, byte[])[]
        {
            ("empty", Array.Empty<byte>()),
            ("garbage", Enumerable.Range(0, 200).Select(i => (byte)(i * 37)).ToArray()),
            ("another plugin's state", new byte[6671]),
            ("truncated", blob[..(blob.Length / 2)]),
            ("future version", PatchU32(blob, 4, 99)),
            ("absurd count", PatchU32(blob, 8, 0x7FFFFFFF)),
        })
        {
            using var c = lib.Create();
            c.LoadState(data);
            if (name == "truncated") continue;   // a truncated stream may apply the parameters it did contain
            for (int i = 0; i < want.Length; i++)
            {
                bool isDefault = i == 0 ? c.GetName(Fpn.ParamValue, 0, c.GetParam(0)) == "FIX" : c.GetParam(i) == DefaultValue(i);
                t.Expect(isDefault, $"{name} state changed param {i} to {c.GetParam(i)}");
            }
        }
        // values restored out of range are clamped
        var bad = (byte[])blob.Clone(); BitConverter.GetBytes(99999).CopyTo(bad, 12 + 4);
        using (var d = lib.Create()) { d.LoadState(bad); t.Expect(d.GetParam(1) == 1000, $"restored out-of-range volume = {d.GetParam(1)}"); }
    }

    private static byte[] PatchU32(byte[] src, int offset, uint value)
    {
        var b = (byte[])src.Clone();
        BitConverter.GetBytes(value).CopyTo(b, offset);
        return b;
    }

    /// <summary>Default of parameter i (the sound chip, 0, is compared by name instead).</summary>
    private static int DefaultValue(int i) => i switch { 1 => 1000, 5 => 1, _ => 0 };

    private void Silence(T t)
    {
        var s = new Scenario { Name = "silence", Tempo = 60, LengthBeats = 2 };
        var (audio, _, plugin) = Play(s);
        using (plugin)
        {
            double peak = Audio.Peak(audio, 0, audio.Length);
            t.Expect(peak < 1e-4, $"peak {peak:0.0000} with no voices");
            t.Expect(audio.All(float.IsFinite), "non-finite samples");
        }
    }

    private void PitchAccuracy(T t)
    {
        double worst = 0;
        foreach (var (ch, color, triangle, name) in new[] { (0, 0, false, "pulse1"), (1, 1, false, "pulse2"), (2, 2, true, "triangle") })
            foreach (int key in new[] { 36, 48, 57, 60, 69, 72, 84 })
            {
                int expectedPeriod = Nes.Period((key - 60) * 100.0, triangle);
                var (audio, mixer, plugin) = Play(One(key, color, 0.6), probe: null);
                using (plugin)
                {
                    var mono = Mono(audio);
                    double hz = SteadyHz(mono, 44100, mixer.Log[0]);
                    double want = Nes.PeriodHz(expectedPeriod, triangle);
                    t.NearCents($"{name} key {key}", hz, want, 2.5);
                    if (!double.IsNaN(hz)) worst = Math.Max(worst, Math.Abs(Audio.Cents(hz, want)));
                    t.Warn(Math.Abs(Audio.Cents(want, Nes.KeyHz(key))) < 40, $"{name} key {key}: the chip's nearest pitch is {Audio.Cents(want, Nes.KeyHz(key)):+0;-0} cents from the note");
                }
                // the same note read at register level, mid-note
                var s = One(key, color, 0.6);
                int mid = -1;
                var (_, _, p2) = Play(s, probe: (pl, sample) => { if (sample >= 22050 && mid < 0) mid = (int)pl.Test(1, ch); });
                using (p2) t.Expect(mid == expectedPeriod, $"{name} key {key}: period register {mid}, expected {expectedPeriod}");
            }
        t.Note($"worst audio error {worst:0.00} cents");
    }

    private void PitchRange(T t)
    {
        // key 12 (16 Hz): below the lowest pulse pitch -> clamps to period 0x7FF; key 140 (26 kHz): period < 8 -> the pulse mutes
        var (audio, mixer, plugin) = Play(One(12, 0, 0.6));
        using (plugin)
        {
            double hz = SteadyHz(Mono(audio), 44100, mixer.Log[0]);
            t.NearCents("clamped low pulse", hz, Nes.PeriodHz(0x7FF, false), 3);
        }
        var (hiAudio, hiMixer, hiPlugin) = Play(One(140, 0, 0.5));
        using (hiPlugin)
        {
            double rms = Audio.Rms(Mono(hiAudio), hiMixer.Log[0].StartSample + 4000, hiMixer.Log[0].EndSample);
            t.Expect(rms < 0.002 || double.IsNaN(rms), $"ultrasonic pulse should mute, rms {rms:0.0000}");
            t.Expect(hiAudio.All(float.IsFinite), "non-finite output at extreme pitch");
        }
        // extreme bend values from the host must not break anything
        var s = One(60, 0, 0.5);
        s.Notes[0].Bend = new() { new[] { 0.0, 100000 }, new[] { 0.25, -100000 }, new[] { 0.5, 0 } };
        var (audio2, _, p2) = Play(s);
        using (p2) t.Expect(audio2.All(float.IsFinite), "non-finite output after extreme bends");
    }

    private void CoarseFine(T t)
    {
        foreach (var (coarse, fine) in new[] { (12, 0), (-12, 0), (7, 0), (0, 50), (0, -37), (5, 20) })
        {
            var s = One(69, 0, 0.6);
            s.Params[3] = coarse; s.Params[4] = fine;
            var (audio, mixer, plugin) = Play(s);
            using (plugin)
            {
                int expectedPeriod = Nes.Period(900, false, coarse * 100 + fine);
                t.NearCents($"coarse {coarse} fine {fine}", SteadyHz(Mono(audio), 44100, mixer.Log[0]), Nes.PeriodHz(expectedPeriod, false), 2.5);
            }
        }
    }

    /// <summary>Compares a measured pitch track to the ideal curve of a note, leaving out the windows around
    /// waveform restarts (period high-byte changes on a pulse channel).</summary>
    private static (double Rms, double Max, int Windows, int Restarts) CompareTrack(float[] mono, int rate, NoteLog log, Scenario s, double fromFrac, double toFrac, bool triangle, double latencyMs = 0)
    {
        double startMs = log.StartSample * 1000.0 / rate;
        double lenMs = (log.EndSample - log.StartSample) * 1000.0 / rate;
        var restarts = Certify.RestartTimes(log.Note, s, startMs, lenMs, triangle);
        var (rms, max, n) = Certify.TrackError(mono, rate, startMs, lenMs, log.Note, s, fromFrac, toFrac, latencyMs, restarts);
        return (rms, max, n, restarts.Count);
    }

    private void Slide(T t)
    {
        foreach (var (from, to, color, name) in new[] { (60.0, 72.0, 0, "pulse1 up"), (69.0, 57.0, 1, "pulse2 down"), (57.0, 69.0, 2, "triangle up") })
        {
            var s = new Scenario { Name = "slide", Tempo = 60, LengthBeats = 3 };
            s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1.8, Key = from, Color = color, Slides = new() { new SlideSeg { StartBeat = 0.3, LengthBeats = 1.5, ToKey = to } } });
            var (audio, mixer, plugin) = Play(s);
            using (plugin)
            {
                var mono = Mono(audio);
                var (rms, max, n, restarts) = CompareTrack(mono, 44100, mixer.Log[0], s, 0.05, 0.97, color == 2, latencyMs: 4);
                t.Expect(n > 100, $"{name}: only {n} measurable windows");
                t.Expect(rms < 12, $"{name}: rms pitch error {rms:0.0} cents (limit 12)");
                t.Expect(max < 35, $"{name}: max pitch error {max:0.0} cents (limit 35)");
                // direction: the track must move the right way, monotonic within jitter
                double st = mixer.Log[0].StartSample * 1000.0 / 44100;
                var rs = Certify.RestartTimes(mixer.Log[0].Note, s, st, (mixer.Log[0].EndSample - mixer.Log[0].StartSample) * 1000.0 / 44100, color == 2);
                var track = Audio.Track(mono, 44100, st + 400, st + 1650, 40, 20).Where(x => !double.IsNaN(x.Hz) && !rs.Any(r => Math.Abs(x.Ms - r) < 50)).ToList();
                int wrong = 0; double dir = Math.Sign(to - from);
                for (int i = 1; i < track.Count; i++) if (dir * Audio.Cents(track[i].Hz, track[i - 1].Hz) < -20) wrong++;
                t.Expect(wrong == 0, $"{name}: pitch moved the wrong way {wrong} times");
                t.Note($"{name}: rms {rms:0.0} / max {max:0.0} cents over {n} windows" + (restarts > 0 ? $", {restarts} waveform restart(s) at a period high-byte change excluded" : ""));
            }
        }
    }

    private void Bend(T t)
    {
        var s = new Scenario { Name = "bend", Tempo = 60, LengthBeats = 3 };
        s.Notes.Add(new SimNote
        {
            StartBeat = 0, LengthBeats = 2.4, Key = 69, Color = 0,
            Bend = new() { new[] { 0.0, 0 }, new[] { 0.3, 0 }, new[] { 0.6, 60 }, new[] { 0.9, 0 }, new[] { 1.2, -60 }, new[] { 1.5, 0 }, new[] { 1.8, 0 } },
        });
        var (audio, mixer, plugin) = Play(s);
        using (plugin)
        {
            var (rms, max, n, restarts) = CompareTrack(Mono(audio), 44100, mixer.Log[0], s, 0.12, 0.8, false, latencyMs: 4);
            t.Expect(n > 100, $"only {n} measurable windows");
            t.Expect(rms < 10, $"rms error {rms:0.0} cents (limit 10)");
            t.Expect(max < 28, $"max error {max:0.0} cents (limit 28)");
            t.Note($"vibrato +-60 cents tracked to rms {rms:0.0} / max {max:0.0} cents" + (restarts > 0 ? $", {restarts} waveform restart(s) excluded" : ""));
        }
    }

    private void Volume(T t)
    {
        double RmsOf(double vel, int master = 1000, int ch = 0)
        {
            var s = One(69, ch, 0.7, vel); s.Params[1] = master;
            var (audio, mixer, plugin) = Play(s);
            using (plugin) return Audio.Rms(Mono(audio), mixer.Log[0].StartSample + 8000, mixer.Log[0].EndSample - 2000);
        }
        double r1 = RmsOf(1.0), r2 = RmsOf(0.66), r3 = RmsOf(0.33), r4 = RmsOf(0.1), r0 = RmsOf(0.0);
        t.Expect(r1 > r2 && r2 > r3 && r3 > r4 && r4 > 0, $"level must fall with velocity: {r1:0.000} {r2:0.000} {r3:0.000} {r4:0.000}");
        t.Expect(r0 < 1e-4, $"velocity 0 is not silent: {r0:0.0000}");
        double h = RmsOf(1.0, 500);
        t.Expect(h < r1 * 0.75 && h > r1 * 0.3, $"master volume 50% gives {h / r1:P0} of full level");
        t.Expect(RmsOf(1.0, 0) < 1e-4, "master volume 0 is not silent");
        // register level: velocity 0.78 at full master -> round(15 x 0.78) = 12
        int level = -1;
        var (_, _, pl) = Play(One(69, 0, 0.6, 0.78), probe: (p, sm) => { if (sm >= 22050 && level < 0) level = (int)p.Test(2, 0); });
        using (pl) t.Expect(level == 12, $"velocity 0.78 -> level register {level}, expected 12");
        double tri = RmsOf(1.0, 1000, 2), tri2 = RmsOf(0.4, 1000, 2);
        t.Warn(Math.Abs(tri - tri2) < 0.01, $"the triangle has no volume control, but level changed {tri:0.000} -> {tri2:0.000}");
        t.Note($"rms by velocity 1.0/.66/.33/.1: {r1:0.000}/{r2:0.000}/{r3:0.000}/{r4:0.000}");
    }

    private void Pan(T t)
    {
        foreach (var (pan, wantL, wantR) in new[] { (-100, true, false), (100, false, true), (0, true, true) })
        {
            var s = One(69, 0, 0.5); s.Params[2] = pan;
            var (audio, mixer, plugin) = Play(s);
            using (plugin)
            {
                var l = Audio.Channel(audio, 2, 0); var r = Audio.Channel(audio, 2, 1);
                int a = mixer.Log[0].StartSample + 6000, b = mixer.Log[0].EndSample - 2000;
                double rl = Audio.Rms(l, a, b), rr = Audio.Rms(r, a, b);
                t.Expect((rl > 0.005) == wantL && (rr > 0.005) == wantR, $"pan {pan}: left {rl:0.000} right {rr:0.000}");
                if (pan == 0) t.Expect(Math.Abs(rl - rr) < 1e-5, $"pan 0 not balanced: {rl:0.0000} vs {rr:0.0000}");
            }
        }
    }

    private void Routing(T t)
    {
        // colour -> channel (colour & 3), read from the plugin's gate state mid-note
        for (int color = 0; color < 16; color++)
        {
            int gates = 0;
            var (_, _, p) = Play(One(69, color, 0.5), probe: (pl, sm) => { if (sm >= 11025 && gates == 0) for (int c = 0; c < 4; c++) if (pl.Test(8, c) == 1) gates |= 1 << c; });
            using (p) t.Expect(gates == 1 << (color & 3), $"colour {color}: channel mask 0x{gates:X}, expected 0x{1 << (color & 3):X}");
        }
        // the Channel parameter overrides the colour
        for (int mode = 1; mode <= 4; mode++)
        {
            int gates = 0;
            var s = One(69, 0, 0.5); s.Params[6] = mode;
            var (_, _, p) = Play(s, probe: (pl, sm) => { if (sm >= 11025 && gates == 0) for (int c = 0; c < 4; c++) if (pl.Test(8, c) == 1) gates |= 1 << c; });
            using (p) t.Expect(gates == 1 << (mode - 1), $"Channel={mode}: mask 0x{gates:X}, expected 0x{1 << (mode - 1):X}");
        }
        // four colours at once: four channels
        var s4 = new Scenario { Name = "four", Tempo = 60, LengthBeats = 1.2 };
        for (int c = 0; c < 4; c++) s4.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1, Key = 60 + c * 4, Color = c });
        int all = 0;
        var (_, _, p4) = Play(s4, probe: (pl, sm) => { if (sm >= 11025 && all == 0) for (int c = 0; c < 4; c++) if (pl.Test(8, c) == 1) all |= 1 << c; });
        using (p4) t.Expect(all == 0xF, $"four colours: channel mask 0x{all:X}");
    }

    private void Mono_(T t)
    {
        // note A (A5) held 0..1.5 s; note B (E6) 0.5..1.0 s on the same channel: B wins while it lasts, A resumes after
        var s = new Scenario { Name = "mono", Tempo = 60, LengthBeats = 2 };
        s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1.5, Key = 69, Color = 0 });
        s.Notes.Add(new SimNote { StartBeat = 0.5, LengthBeats = 0.5, Key = 76, Color = 0 });
        var (audio, _, plugin) = Play(s);
        using (plugin)
        {
            var mono = Mono(audio);
            double a = Audio.Frequency(mono, 44100, 8000, 20000), b = Audio.Frequency(mono, 44100, 24000, 42000), c = Audio.Frequency(mono, 44100, 50000, 62000);
            t.NearCents("before the second note", a, Nes.PeriodHz(Nes.Period(900, false), false), 3);
            t.NearCents("while the second note sounds", b, Nes.PeriodHz(Nes.Period(1600, false), false), 3);
            t.NearCents("after it ends (first note resumes)", c, Nes.PeriodHz(Nes.Period(900, false), false), 3);
        }
        // a note that starts while the previous one has just ended must retrigger (a $4003 write)
        var s2 = new Scenario { Name = "retrigger", Tempo = 60, LengthBeats = 1.2 };
        s2.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 0.5, Key = 69, Color = 0 });
        s2.Notes.Add(new SimNote { StartBeat = 0.5, LengthBeats = 0.5, Key = 69, Color = 0 });
        var (audio2, _, p2) = Play(s2);
        using (p2) t.Expect(Audio.Rms(Mono(audio2), 25000, 40000) > 0.01, "back-to-back identical notes: the second is silent");
    }

    private void Lifecycle(T t)
    {
        var s = new Scenario { Name = "lifecycle", Tempo = 60, LengthBeats = 3 };
        for (int i = 0; i < 12; i++) s.Notes.Add(new SimNote { StartBeat = i * 0.15, LengthBeats = 0.4, Key = 60 + i, Color = i });
        var (_, _, plugin) = Play(s);
        using (plugin)
        {
            t.Expect(plugin.Host.VoiceKillCalls == 12, $"plugin handed back {plugin.Host.VoiceKillCalls} of 12 voices with Voice_Kill");
            t.Expect(plugin.Test(3) == 0, $"plugin still holds {plugin.Test(3)} voices after everything ended");
            t.Expect(plugin.Host.LiveVoices == 0, $"host still has {plugin.Host.LiveVoices} live voices");
            // hostile calls
            plugin.VoiceRelease(0x7777); plugin.VoiceKill(0x7777); plugin.VoiceKill(0); plugin.VoiceRelease(-1);
            t.Expect(plugin.Test(3) == 0, "bad handles changed the voice count");
        }
        // many voices at once (more than there are channels)
        var big = new Scenario { Name = "many", Tempo = 60, LengthBeats = 2 };
        for (int i = 0; i < 64; i++) big.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 0.5 + i * 0.01, Key = 40 + i, Color = i });
        var (audio, _, p2) = Play(big);
        using (p2)
        {
            t.Expect(audio.All(float.IsFinite), "non-finite output with 64 voices");
            t.Expect(p2.Host.VoiceKillCalls == 64, $"{p2.Host.VoiceKillCalls} of 64 voices handed back");
        }
    }

    private void Cores(T t)
    {
        using var probe = lib.Create();
        var chips = Chips(probe);
        // cores that are accurate by design must be on pitch; degraded / speed-hack cores are reported, not required
        var accurate = new HashSet<string> { "FIX", "FIXS", "FMC", "HI", "HI2", "HI2X", "LOW", "LQ2", "QN", "QLOW", "ULQ", "SPD", "SPD2", "EIL", "LQ", "QLQ", "QLQ2", "MNES" };
        var table = new List<string>();
        foreach (var chip in chips)
        {
            var s = One(69, 0, 0.6);
            var (audio, mixer, plugin) = Play(s, setup: p => SetChip(p, chip));
            using (plugin)
            {
                var mono = Mono(audio);
                var log = mixer.Log[0];
                double rms = Audio.Rms(mono, log.StartSample + 8000, log.EndSample - 2000);
                double hz = SteadyHz(mono, 44100, log);
                double err = double.IsNaN(hz) ? double.NaN : Audio.Cents(hz, Nes.PeriodHz(Nes.Period(900, false), false));
                table.Add($"{chip}:{(double.IsNaN(err) ? "n/a" : err.ToString("+0.0;-0.0"))}c");
                if (chip == "WF") { t.Warn(rms > 0.001, $"{chip}: silent (a MIDI-style core: no PCM)"); continue; }
                t.Expect(rms > 0.003, $"{chip}: no sound (rms {rms:0.0000})");
                t.Expect(audio.All(float.IsFinite), $"{chip}: non-finite output");
                if (accurate.Contains(chip))
                {
                    if (double.IsNaN(err) || Math.Abs(err) > 6) t.Warn(false, $"{chip}: pitch {(double.IsNaN(err) ? "unmeasurable" : err.ToString("+0.0;-0.0") + " cents")}");
                }
            }
        }
        t.Note(string.Join(" ", table));
    }

    private void Swap(T t)
    {
        foreach (var (from, to) in new[] { ("FIX", "QN"), ("FIX", "DMG"), ("QN", "FIXS"), ("DMG", "FIX") })
        {
            var s = new Scenario { Name = "swap", Tempo = 60, LengthBeats = 3 };
            s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 2.4, Key = 69, Color = 0 });
            int swapAt = (int)(1.2 * 44100);
            bool done = false;
            var (audio, mixer, plugin) = Play(s, setup: p => SetChip(p, from), probe: (pl, sm) => { if (!done && sm >= swapAt) { SetChip(pl, to); done = true; } });
            using (plugin)
            {
                var mono = Mono(audio);
                double before = Audio.Frequency(mono, 44100, 8000, 40000);
                double after = Audio.Frequency(mono, 44100, swapAt + 12000, swapAt + 60000);
                double rmsAfter = Audio.Rms(mono, swapAt + 12000, swapAt + 60000);
                double want = Nes.PeriodHz(Nes.Period(900, false), false);
                t.NearCents($"{from}->{to} before", before, want, 3);
                t.NearCents($"{from}->{to} after", after, want, 3);
                t.Expect(rmsAfter > 0.01, $"{from}->{to}: silent after the swap (rms {rmsAfter:0.0000})");
                t.Expect(audio.All(float.IsFinite), $"{from}->{to}: non-finite output");
            }
        }
    }

    private void RatesAndBlocks(T t)
    {
        foreach (int rate in new[] { 22050, 44100, 48000, 88200, 96000 })
        {
            var (audio, mixer, plugin) = Play(One(69, 0, 0.6), rate: rate);
            using (plugin)
            {
                var mono = Mono(audio);
                double hz = SteadyHz(mono, rate, mixer.Log[0]);
                t.NearCents($"{rate} Hz", hz, Nes.PeriodHz(Nes.Period(900, false), false), 3);
                // continuity: no gaps in a sustained note
                int a = mixer.Log[0].StartSample + rate / 5, b = mixer.Log[0].EndSample - rate / 10;
                int quiet = 0;
                for (int w = a; w + 256 <= b; w += 256) if (Audio.Rms(mono, w, w + 256) < 0.01) quiet++;
                t.Expect(quiet == 0, $"{rate} Hz: {quiet} silent gaps inside a sustained note");
            }
        }
        // the host may use any block size
        foreach (int block in new[] { 1, 7, 33, 64, 100, 513, 1024, 4096 })
        {
            var s = One(69, 0, 0.5);
            var (audio, mixer, plugin) = Play(s, block: block);
            using (plugin)
            {
                var mono = Mono(audio);
                t.NearCents($"block {block}", SteadyHz(mono, 44100, mixer.Log[0], 120, 80), Nes.PeriodHz(Nes.Period(900, false), false), 3.5);
                t.Expect(audio.All(float.IsFinite), $"block {block}: non-finite output");
            }
        }
    }

    private void Realtime(T t)
    {
        var s = new Scenario { Name = "rt", Tempo = 60, LengthBeats = 11 };
        for (int c = 0; c < 4; c++) s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 9, Key = 57 + c * 5, Color = c, Slides = new() { new SlideSeg { StartBeat = 1, LengthBeats = 8, ToKey = 69 + c } } });
        using var plugin = lib.Create();
        plugin.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        long allocBefore = 0, allocAfter = 0;
        var mixer = new Mixer { SampleRate = 48000, BlockSize = 512, AfterBlock = sm => { if (sm >= 48000 * 2 && allocBefore == 0) allocBefore = plugin.Test(4); if (sm >= 48000 * 9 && allocAfter == 0) allocAfter = plugin.Test(4); } };
        var sw = Stopwatch.StartNew();
        var audio = mixer.Run(plugin, s, 0);
        sw.Stop();
        double seconds = audio.Length / 2 / 48000.0;
        double speed = seconds / sw.Elapsed.TotalSeconds;
        t.Expect(speed >= 8, $"{speed:0.0}x real time (needs 8x or better)");
        long alloc = allocAfter - allocBefore;
        t.Expect(alloc == 0, $"{alloc:N0} bytes allocated by the plugin while playing 4 sliding voices for 7 s (must be 0: garbage collection is audible)");
        t.Note($"{speed:0.0}x real time, 4 sliding voices, 48 kHz / 512-sample blocks; steady-state allocation {alloc} B");
    }

    private void Editor(T t)
    {
        using var p = lib.Create();
        nint parent = Win32Host.CreateParent();
        try
        {
            p.ShowEditor(parent);
            t.Expect(p.EditorHandle != 0, "EditorHandle still 0 after FPD_ShowEditor");
            if (p.EditorHandle == 0) return;
            var sliders = Win32Host.Sliders(p.EditorHandle);
            t.Expect(sliders.Count == ParamNames.Length, $"editor has {sliders.Count} sliders, expected {ParamNames.Length}");
            Win32Host.Pump(150);
            // the editor shows the current value; moving a slider changes the parameter and tells the host
            p.SetParam(1, 250);
            Win32Host.Pump(100);
            t.Expect(Win32Host.SliderPos(sliders[1]) == 250, $"slider shows {Win32Host.SliderPos(sliders[1])} after the parameter became 250");
            int before = p.Host.ParamChanges.Count;
            Win32Host.DragSlider(p.EditorHandle, sliders[1], 400);
            t.Expect(p.GetParam(1) == 400, $"parameter is {p.GetParam(1)} after the slider moved to 400");
            t.Expect(p.Host.ParamChanges.Count == before + 1 && p.Host.ParamChanges[^1] == (1, 400), "the host was not told (OnParamChanged) about the slider move");
            // the plugin's own readout text is on screen
            t.Expect(Win32Host.AllText(p.EditorHandle).Contains("Chip: FIX"), "readout text not shown");
            p.ShowEditor(parent);   // showing again re-parents, must not create a second editor
            nint first = p.EditorHandle;
            p.HideEditor();
            t.Expect(p.EditorHandle == 0, "EditorHandle not cleared after hiding");
            p.ShowEditor(parent);
            t.Expect(p.EditorHandle != 0 && p.EditorHandle != first || p.EditorHandle != 0, "editor did not reopen");
            p.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); }
    }

    private void Robust(T t)
    {
        // 60 instances created and destroyed
        for (int i = 0; i < 60; i++)
        {
            using var p = lib.Create();
            if (i % 10 == 0) p.SetParam(0, i % 20);
        }
        // two instances playing at once on different threads with different chips
        var errors = new List<string>();
        var threads = new[] { "FIX", "QN", "DMG" }.Select(chip => new Thread(() =>
        {
            try
            {
                var s = One(69, 0, 0.8);
                var (audio, mixer, plugin) = Play(s, setup: p => SetChip(p, chip));
                using (plugin)
                {
                    double hz = SteadyHz(Mono(audio), 44100, mixer.Log[0]);
                    double err = double.IsNaN(hz) ? 999 : Math.Abs(Audio.Cents(hz, Nes.PeriodHz(Nes.Period(900, false), false)));
                    if (err > 6) lock (errors) errors.Add($"{chip}: {err:0.0} cents off when running concurrently");
                }
            }
            catch (Exception e) { lock (errors) errors.Add($"{chip}: {e.GetType().Name} {e.Message}"); }
        })).ToList();
        threads.ForEach(x => x.Start()); threads.ForEach(x => x.Join());
        t.Expect(errors.Count == 0, string.Join("; ", errors));
        // garbage calls
        using (var p = lib.Create())
        {
            foreach (int id in new[] { -5, 1, 2, 9999, 0x7FFFFF, 33, 44, 55 }) p.Dispatcher(id, -1, 12345);
            foreach (int idx in new[] { -1, 8, 100, int.MaxValue }) { p.GetName(Fpn.Param, idx, 0); p.GetName(Fpn.ParamValue, idx, 7); p.ProcessParam(idx, 5, Rec.GetValue); }
            p.GetName(99, 0, 0); p.ProcessEvent(Fpe.MidiPitch, 100); p.ProcessEvent(-3, 0); p.Idle();
            p.Dispatcher(Fpd.SetSampleRate, 0, -5); p.Dispatcher(Fpd.SetSampleRate, 0, 0);
            var a = new float[512]; fixed (float* f = a) p.GenRender(f, 256);
            t.Expect(a.All(float.IsFinite), "non-finite output after garbage calls");
        }
        // destroy while voices are sounding
        var s2 = One(69, 0, 5);
        var plugin2 = lib.Create();
        var mx = new Mixer { SampleRate = 44100, BlockSize = 256 };
        var v = plugin2.Host.NewVoice(0, 0.8f); v.Params->FinalLevels.Vol = 0.8f; v.Handle = plugin2.TriggerVoice(v.Params, v.Tag);
        var buf = new float[512]; fixed (float* f = buf) { plugin2.GenRender(f, 256); }
        plugin2.Destroy();
        t.Note("created/destroyed 60 instances, ran 3 concurrently, survived garbage calls and destroy with a voice playing");
    }

    private void LogTest(T t)
    {
        // the log path is read once, when the plugin DLL initialises, so a fresh process is needed: the host re-runs itself
        string log = Path.Combine(Path.GetTempPath(), $"bn2_log_{Environment.ProcessId}.txt");
        File.Delete(log);
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"\"{typeof(SelfTest).Assembly.Location}\" logprobe \"{dllPath}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) != "dotnet") psi.Arguments = $"logprobe \"{dllPath}\"";
        psi.Environment["BROKENNES_PLUGIN_LOG"] = log;
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(30000);
        t.Expect(File.Exists(log), "no log file was written");
        if (!File.Exists(log)) return;
        var text = File.ReadAllText(log);
        t.Expect(text.Contains("CreatePlugInstance BrokenNes2"), "log has no CreatePlugInstance line");
        t.Expect(text.Contains("Dispatcher id=4"), "log has no SetSampleRate dispatcher line");
        t.Expect(!text.Contains("exception", StringComparison.OrdinalIgnoreCase), "the log reports an exception");
        File.Delete(log);
    }

    /// <summary>The body of the `logprobe` sub-process: create a plugin, poke it, destroy it.</summary>
    public static void LogProbe(string dll)
    {
        var lib = new PluginLibrary(dll);
        using var p = lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        p.SetParam(1, 500);
    }
}
