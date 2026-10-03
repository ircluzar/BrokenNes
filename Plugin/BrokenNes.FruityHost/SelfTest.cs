// The BrokenNes2 certification suite. Every test plays real notes into the loaded plugin DLL through the
// host simulation and measures what comes out: register values read back from the plugin, and pitch,
// loudness and stereo balance measured in the audio itself.
using System.Diagnostics;
using BrokenNes.Fruity;
using BrokenNes.SynthHost;

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
    private readonly string? romPath;
    private readonly string AboutFile;

    public SelfTest(string dllPath, string? romPath = null)
    {
        this.dllPath = dllPath;
        this.romPath = romPath;
        AboutFile = Path.Combine(Path.GetTempPath(), $"bn2_about_{Environment.ProcessId}.txt");
        File.WriteAllText(AboutFile, "AboutRevisionSeen=99\n");              // the About window counts as seen (the "about" test says otherwise); input settings start at their defaults
        Environment.SetEnvironmentVariable("BROKENNES_PREFS_FILE", AboutFile);
        lib = new PluginLibrary(dllPath);
    }

    private static readonly string[] ParamNames = ["Sound chip", "Volume", "Pan", "Coarse", "Fine", "Pulse duty", "Channel", "Noise mode", "Mode", "Emulator", "Console", "Instrument"];

    // parameter indices
    private const int PCore = 0, PVolume = 1, PPan = 2, PChannel = 6, PMode = 8, PEmulator = 9, PConsole = 10, PInstrument = 11;
    private const int PDuty = 5, PNoiseMode = 7;

    /// <summary>A one-note scenario where 1 beat = 1 second.</summary>
    private static Scenario One(double key, int color, double seconds = 0.8, double velocity = 0.78, double pan = 0)
    {
        var s = new Scenario { Name = "one", Tempo = 60, LengthBeats = seconds + 0.2 };
        s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = seconds, Key = key, Channel = color, Velocity = velocity, Pan = pan });
        return s;
    }

    private static float[] Mono(float[] stereo) => Audio.Mono(stereo, 2);

    /// <summary>Plays a scenario into a fresh rack of plugin instances, one per NES channel the notes use (BrokenNes2 is one instance per
    /// channel; the instances share an emulator unless <paramref name="emulator"/> names one). Returns (stereo audio, mixer log, the first
    /// instance: it owns the others and destroys them with itself). <paramref name="setup"/> and <paramref name="probe"/> get that instance.</summary>
    private (float[] Audio, Mixer Mixer, PluginInstance Plugin) Play(Scenario s, int rate = 44100, int block = 256,
        Action<PluginInstance>? setup = null, Action<PluginInstance, int>? probe = null, int emulator = 0)
    {
        var rack = MakeRack(s.Notes.Select(n => n.Channel & 3).Distinct().OrderBy(c => c).DefaultIfEmpty(0).ToList(), emulator);
        var plugin = rack[0];
        setup?.Invoke(plugin);
        var mixer = new Mixer { SampleRate = rate, BlockSize = block, AfterBlock = probe == null ? null : sample => probe(plugin, sample) };
        var audio = mixer.Run(rack, s);
        return (audio, mixer, plugin);
    }

    /// <summary>One instance per channel, each set to its channel; the first leads the rest (see <see cref="PluginInstance.Companions"/>).</summary>
    private List<PluginInstance> MakeRack(IReadOnlyList<int> channels, int emulator = 0)
    {
        var rack = new List<PluginInstance>();
        foreach (int c in channels)
        {
            var p = lib.Create();
            p.RackChannel = c;
            if (emulator > 0) p.SetParam(PEmulator, emulator);
            p.SetParam(PChannel, c + 1);
            rack.Add(p);
        }
        rack[0].Companions = rack.Skip(1).ToList();
        return rack;
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
            ("routing", "The Channel parameter picks the NES channel (note colour is ignored); four instances share one emulator", Routing),
            ("hub-share", "One instance = one channel: Auto fills one emulator, then opens the next; config is shared", HubShare),
            ("hub-explicit", "Explicit emulators: a Game Boy chip next to the NES one; Auto never fills an explicit emulator", HubExplicit),
            ("hub-conflict", "A channel taken by another instance is refused, and handed over when it frees up", HubConflict),
            ("hub-state", "Saved projects keep each instance's emulator, channel and the emulator's chip", HubState),
            ("consoles", "Console selector: each console lists its own chips, picking either way keeps them in step, every console plays", Consoles),
            ("mono", "Each channel is monophonic: the newest note wins and the older one resumes", Mono_),
            ("lifecycle", "Voices: released voices are handed back (Voice_Kill), bad handles are harmless", Lifecycle),
            ("cores", "Every NES sound chip produces the note; accurate ones at the right pitch", Cores),
            ("swap", "Switching the sound chip while a note is held keeps it sounding at pitch", Swap),
            ("rates", "Sample rates 22.05-96 kHz and odd block sizes (1..4096) keep pitch and continuity", RatesAndBlocks),
            ("standalone-rack", "Standalone synth engine: four channels played live (pitch, bend, note off, any callback size, real time, no allocation, saved settings)", StandaloneRack),
            ("standalone-midi", "Standalone synth: MIDI bytes to notes, bends and all-notes-off; channel routing; noise on the wire is harmless", StandaloneMidi),
            ("realtime", "Real-time: well faster than real time, and no allocation while playing", Realtime),
            ("rom-mode", "ROM mode: a real NES game runs inside the plugin: sound, picture, saved path", RomMode),
            ("rom-chips", "ROM mode: the sound chip switches while the game runs", RomChips),
            ("rom-audio", "A loaded Game Boy or SNES game's whole sound reaches the first instance (Mix), whatever channel it was on", RomAudio),
            ("runaway-gb", "Instrument Runaway on a Game Boy game: the frozen game's channels play from FL notes with its settings", RunawayGb),
            ("runaway-snes", "Instrument Runaway on a SNES game: the picker lists the game's instruments, the chosen one plays", RunawaySnesPicker),
            ("inputs", "Inputs: the pad drives NES, Game Boy and SNES games; mappings are remembered; the modal works", InputsTest),
            ("runaway", "Instrument Runaway: the game freezes and goes silent; a SNES game's samples play from FL notes, polyphonically, and survive a saved project", RunawayTest),
            ("reset", "Reset Console: held notes and chips are cleared for every instance, a ROM-mode game restarts from power-on", ResetConsole),
            ("editor", "Editor: dropdowns, mode switch, channel strip and sliders work through real mouse input", Editor),
            ("rom-stems", "ROM mode: five instances on one game: the whole mix and each channel on its own, in step", RomStems),
            ("rom-consoles", "ROM mode on Game Boy and SNES games: runs, picture size, Mix only, files of any console, bad files refused", RomConsoles),
            ("about", "About window: opens by itself the first time only, is modal, closes, reopens from the button", AboutWindow),
            ("rom-editor", "Editor in ROM mode: picture, mode switch loads the game, Direct shows the bypass message", RomEditor),
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
        t.Expect(p.LongName == "Bogue :: BrokenNes 2", $"LongName '{p.LongName}'");
        t.Expect(p.ShortName == "Bogue :: BrokenNes 2", $"ShortName '{p.ShortName}'");
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
        p.SetParamFromAutomation(6, 1.0); t.Expect(p.GetParam(6) == 5, $"automation 1.0 -> channel {p.GetParam(6)} (Mix)");
        t.Expect(p.GetName(Fpn.ParamValue, 6, 0) == "Auto" && p.GetName(Fpn.ParamValue, 6, 3) == "Triangle" && p.GetName(Fpn.ParamValue, 6, 5).StartsWith("Mix"), "channel names");
        t.Expect(p.GetName(Fpn.ParamValue, PEmulator, 0) == "Auto" && p.GetName(Fpn.ParamValue, PEmulator, 3) == "#3", "emulator names");
        t.Expect(p.GetName(Fpn.ParamValue, PConsole, 0) == "NES" && p.GetName(Fpn.ParamValue, PConsole, 1) == "Game Boy" && p.GetName(Fpn.ParamValue, PConsole, 2) == "SNES", "console names");
        t.Expect(!chips.Contains("MNES") && !chips.Contains("WF"), "the MNES and WF cores should not be offered");
        t.Expect(p.GetName(Fpn.ParamValue, 8, 1) == "ROM" && p.GetName(Fpn.ParamValue, 8, 0) == "Direct", "mode names");
    }

    private void State(T t)
    {
        using var a = lib.Create();
        int[] want = [Chips(a).IndexOf("QN"), 612, -41, 7, -23, 2, 3, 1, 1, 2, 0, 37];   // chip QN, volume, pan, coarse, fine, duty, channel (Triangle), noise mode, mode (ROM), emulator #2, console NES
        // the emulator first: an instance that moves to another emulator takes that emulator's chip, mode and cores
        a.SetParam(PEmulator, want[PEmulator]);
        for (int i = 0; i < want.Length; i++) a.SetParam(i, want[i]);
        var blob = a.SaveState();
        a.Dispose();   // the restored instance takes its place: a channel is held by one instance only
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
                bool isDefault = i == 0 ? c.GetName(Fpn.ParamValue, i, c.GetParam(i)) == "FIX" : c.GetParam(i) == DefaultValue(i);
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
            s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1.8, Key = from, Channel = color, Slides = new() { new SlideSeg { StartBeat = 0.3, LengthBeats = 1.5, ToKey = to } } });
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
            StartBeat = 0, LengthBeats = 2.4, Key = 69, Channel = 0,
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

    /// <summary>Which instance sits on which channel: the Channel parameter alone decides (the note colour does not).</summary>
    private void Routing(T t)
    {
        // each channel value plays that channel and no other (gate state read mid-note)
        for (int ch = 0; ch < 4; ch++)
        {
            int gates = 0;
            var (_, _, p) = Play(One(69, ch, 0.5), probe: (pl, sm) => { if (sm >= 11025 && gates == 0) for (int c = 0; c < 4; c++) if (pl.Test(8, c) == 1) gates |= 1 << c; });
            using (p) t.Expect(gates == 1 << ch, $"Channel={ch + 1}: channel mask 0x{gates:X}, expected 0x{1 << ch:X}");
        }
        // the piano-roll note colour is ignored: a note of colour 3 on a pulse 1 instance plays pulse 1
        using (var inst = lib.Create())
        {
            inst.SetParam(PChannel, 1);
            var v = inst.Host.NewVoice(3, 0.8f); v.Params->FinalLevels.Vol = 0.8f; v.Handle = inst.TriggerVoice(v.Params, v.Tag);
            var buf = new float[1024]; fixed (float* f = buf) { inst.NewTick(); inst.GenRender(f, 512); }
            t.Expect(inst.Test(8, 0) == 1 && inst.Test(8, 3) == 0, "a colour-3 note should still play the instance's own channel (pulse 1)");
        }
        // four instances at once: four channels, one emulator
        var s4 = new Scenario { Name = "four", Tempo = 60, LengthBeats = 1.2 };
        for (int c = 0; c < 4; c++) s4.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1, Key = 60 + c * 4, Channel = c });
        int all = 0;
        long emus = 0, members = 0;
        var (audio4, _, p4) = Play(s4, probe: (pl, sm) => { if (sm >= 11025 && all == 0) { for (int c = 0; c < 4; c++) if (pl.Test(8, c) == 1) all |= 1 << c; emus = pl.Test(18); members = pl.Test(19); } });
        using (p4)
        {
            t.Expect(all == 0xF, $"four instances: channel mask 0x{all:X}");
            t.Expect(emus == 1 && members == 4, $"four instances should share ONE emulator: {emus} emulators, {members} instances on the first");
            t.Expect(Audio.Rms(Mono(audio4), 12000, 40000) > 0.01, "four instances: no sound");
        }
    }

    private void Mono_(T t)
    {
        // note A (A5) held 0..1.5 s; note B (E6) 0.5..1.0 s on the same channel: B wins while it lasts, A resumes after
        var s = new Scenario { Name = "mono", Tempo = 60, LengthBeats = 2 };
        s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1.5, Key = 69, Channel = 0 });
        s.Notes.Add(new SimNote { StartBeat = 0.5, LengthBeats = 0.5, Key = 76, Channel = 0 });
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
        s2.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 0.5, Key = 69, Channel = 0 });
        s2.Notes.Add(new SimNote { StartBeat = 0.5, LengthBeats = 0.5, Key = 69, Channel = 0 });
        var (audio2, _, p2) = Play(s2);
        using (p2) t.Expect(Audio.Rms(Mono(audio2), 25000, 40000) > 0.01, "back-to-back identical notes: the second is silent");
    }

    private void Lifecycle(T t)
    {
        var s = new Scenario { Name = "lifecycle", Tempo = 60, LengthBeats = 3 };
        for (int i = 0; i < 12; i++) s.Notes.Add(new SimNote { StartBeat = i * 0.15, LengthBeats = 0.4, Key = 60 + i, Channel = i });
        var (_, _, plugin) = Play(s);
        using (plugin)
        {
            t.Expect(plugin.TotalKilled == 12, $"the instances handed back {plugin.TotalKilled} of 12 voices with Voice_Kill");
            t.Expect(plugin.Test(3) == 0, $"the plugins still hold {plugin.Test(3)} voices after everything ended");
            t.Expect(plugin.TotalLive == 0, $"host still has {plugin.TotalLive} live voices");
            // hostile calls
            plugin.VoiceRelease(0x7777); plugin.VoiceKill(0x7777); plugin.VoiceKill(0); plugin.VoiceRelease(-1);
            t.Expect(plugin.Test(3) == 0, "bad handles changed the voice count");
        }
        // many voices at once on one instance (more than a channel can play): all handed back
        var big = new Scenario { Name = "many", Tempo = 60, LengthBeats = 2 };
        for (int i = 0; i < 64; i++) big.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 0.5 + i * 0.01, Key = 40 + i, Channel = 0 });
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
            s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 2.4, Key = 69, Channel = 0 });
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

    // ---- the standalone synth's engine (BrokenNes.SynthHost): what the desktop's Synthesizer Mode plays through ----

    private static float[] RenderRack(SynthRack rack, int ms, int chunk)
    {
        int frames = rack.SampleRate * ms / 1000;
        var all = new float[frames * 2];
        for (int done = 0; done < frames; done += chunk)
        {
            int n = Math.Min(chunk, frames - done);
            rack.Render(all.AsSpan(done * 2, n * 2));
        }
        return all;
    }

    private static double Level(float[] stereo, int rate, int fromMs, int toMs) => Audio.Rms(Mono(stereo), fromMs * rate / 1000, toMs * rate / 1000);

    /// <summary>The pitch a NES channel actually plays for <paramref name="cents"/> from C5: the chip only has whole period steps (about 8 cents apart at 500 Hz).</summary>
    private static double Playable(double cents, bool triangle) => Nes.PeriodHz(Nes.Period(cents, triangle), triangle);

    private void StandaloneRack(T t)
    {
        const int rate = 48000;
        double HzOf(float[] stereo, int fromMs, int toMs) => Audio.Frequency(Mono(stereo), rate, fromMs * rate / 1000, toMs * rate / 1000);

        // every tone channel on its own plays the right pitch (A4 = 440 Hz), the notes of one channel do not leak into the others
        foreach (var (ch, key) in new[] { (0, 69), (1, 69), (2, 69), (0, 57), (1, 76), (2, 57) })
        {
            using var rack = new SynthRack(dllPath, rate);
            rack.NoteOn(ch, key, 0.8f);
            var a = RenderRack(rack, 700, 480);
            string name = $"{SynthRack.ChannelNames[ch]} key {key}";
            t.NearCents(name, HzOf(a, 200, 650), Playable((key - 60) * 100.0, ch == 2), 3);
            var live = rack.Instances.Select(p => p.Host.LiveVoices).ToArray();
            t.Expect(live.Sum() == 1 && live[ch] == 1, $"{name}: voices per channel {string.Join(",", live)} (only channel {ch} should hold one)");
        }

        // pitch bend: +1 is +2 semitones, -0.5 is -1 semitone; releasing ends the note and the plugin hands the voice back
        using (var rack = new SynthRack(dllPath, rate))
        {
            rack.NoteOn(0, 69, 0.8f);
            RenderRack(rack, 300, 480);
            rack.PitchBend(0, 1f);
            var up = RenderRack(rack, 500, 480);
            rack.PitchBend(0, -0.5f);
            var down = RenderRack(rack, 500, 480);
            rack.PitchBend(0, 0f);
            var back = RenderRack(rack, 500, 480);
            t.NearCents("bend +1 (+2 st)", HzOf(up, 150, 480), Playable(1100, false), 3);
            t.NearCents("bend -0.5 (-1 st)", HzOf(down, 150, 480), Playable(800, false), 3);
            t.NearCents("bend back to centre", HzOf(back, 150, 480), Playable(900, false), 3);
            rack.NoteOff(0, 69);
            var tail = RenderRack(rack, 500, 480);
            t.Expect(Level(tail, rate, 300, 500) < 0.002, $"still sounding after the note off (rms {Level(tail, rate, 300, 500):0.0000})");
            t.Expect(rack.LiveVoices == 0, $"{rack.LiveVoices} voice(s) not handed back after the note off");
        }

        // the block size of the audio device must not matter: odd and huge callbacks keep pitch and stay finite
        foreach (int chunk in new[] { 64, 441, 4096 })
        {
            using var rack = new SynthRack(dllPath, rate);
            rack.NoteOn(1, 69, 0.8f);
            var a = RenderRack(rack, 800, chunk);
            t.NearCents($"callback of {chunk} frames", HzOf(a, 250, 750), Playable(900, false), 3);
            t.Expect(a.All(float.IsFinite), $"non-finite output with {chunk}-frame callbacks");
        }

        // all four at once, then everything off
        using (var rack = new SynthRack(dllPath, rate))
        {
            rack.NoteOn(0, 60, 0.8f); rack.NoteOn(1, 64, 0.8f); rack.NoteOn(2, 55, 0.8f); rack.NoteOn(3, 60, 0.8f);
            var a = RenderRack(rack, 600, 480);
            var live = rack.Instances.Select(p => p.Host.LiveVoices).ToArray();
            t.Expect(live.All(v => v == 1), $"voices per channel {string.Join(",", live)}, expected one on each");
            t.Expect(Level(a, rate, 200, 600) > 0.01, $"four channels together are nearly silent (rms {Level(a, rate, 200, 600):0.0000})");
            t.Expect(a.All(s => float.IsFinite(s) && Math.Abs(s) <= 1f), "four channels clip or are non-finite");
            rack.AllNotesOff();
            var tail = RenderRack(rack, 500, 480);
            t.Expect(Level(tail, rate, 300, 500) < 0.002 && rack.LiveVoices == 0, $"all notes off left rms {Level(tail, rate, 300, 500):0.0000}, {rack.LiveVoices} voice(s)");
        }

        // real time and allocation, as the editor and the audio device would have it: 4 channels sounding, 10 ms callbacks
        using (var rack = new SynthRack(dllPath, rate))
        {
            rack.NoteOn(0, 57, 0.8f); rack.NoteOn(1, 64, 0.8f); rack.NoteOn(2, 45, 0.8f); rack.NoteOn(3, 60, 0.8f);
            var buf = new float[480 * 2];
            for (int i = 0; i < 100; i++) rack.Render(buf);   // warm up
            long m0 = GC.GetAllocatedBytesForCurrentThread(), p0 = rack.Instances[0].Test(4);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 600; i++) { rack.Render(buf); if (i % 100 == 0) rack.PitchBend(0, (i / 100) % 2 == 0 ? 0.3f : -0.3f); }
            sw.Stop();
            long managed = GC.GetAllocatedBytesForCurrentThread() - m0, native = rack.Instances[0].Test(4) - p0;
            double speed = 6.0 / sw.Elapsed.TotalSeconds;
            t.Expect(speed >= 5, $"{speed:0.0}x real time with 4 channels (needs 5x or better)");
            t.Expect(managed <= 2048, $"{managed:N0} bytes allocated by the host while rendering (only bend events may allocate, and only a queue segment)");
            t.Expect(native == 0, $"{native:N0} bytes allocated by the plugin while rendering");
            t.Note($"{speed:0.0}x real time with 4 channels in 10 ms callbacks; host allocation {managed} B, plugin {native} B");
        }

        // saved settings: what the editors change (volume, tuning) survives a restart of the rack
        byte[][] states;
        using (var rack = new SynthRack(dllPath, rate))
        {
            rack.Instances[0].SetParam(PVolume, 400);
            rack.Instances[2].SetParam(3, 7);
            states = rack.SaveStates();
        }
        t.Expect(states.Length == SynthRack.ChannelCount && states.All(s => s.Length > 0), "SaveStates did not return a state per instance");
        using (var rack = new SynthRack(dllPath, rate))
        {
            rack.LoadStates(states);
            t.Expect(rack.Instances[0].GetParam(PVolume) == 400, $"volume came back as {rack.Instances[0].GetParam(PVolume)}");
            t.Expect(rack.Instances[2].GetParam(3) == 7, $"coarse tuning came back as {rack.Instances[2].GetParam(3)}");
            rack.LoadStates([[1, 2, 3], [], [0xFF, 0xFF]]);      // damaged state is survived, not obeyed
            t.Expect(rack.Instances[0].GetParam(PVolume) >= 0, "damaged state broke the instance");
        }
    }

    private void StandaloneMidi(T t)
    {
        const int rate = 48000;
        double HzOf(float[] stereo, int fromMs, int toMs) => Audio.Frequency(Mono(stereo), rate, fromMs * rate / 1000, toMs * rate / 1000);
        int[] Live(SynthRack r) => r.Instances.Select(p => p.Host.LiveVoices).ToArray();

        using var rack = new SynthRack(dllPath, rate);
        var midi = new MidiRouter(rack);

        // MIDI channel n plays rack channel n-1 (here channel 2 -> Pulse 2); channels beyond 4 are ignored
        midi.Handle(0x91, 69, 100);
        var a = RenderRack(rack, 600, 480);
        t.Expect(Live(rack).SequenceEqual([0, 1, 0, 0]), $"note on MIDI channel 2 gave voices {string.Join(",", Live(rack))}");
        t.NearCents("MIDI note 69", HzOf(a, 200, 550), Playable(900, false), 3);
        midi.Handle(0x95, 60, 100);
        RenderRack(rack, 100, 480);
        t.Expect(Live(rack).Sum() == 1, "a note on MIDI channel 6 should be ignored");

        // a raw Windows short message, pitch bend (14 bit: 16383 is full up = +2 semitones, 8192 is the centre)
        midi.Handle(0xE1 | (0x7F << 8) | (0x7F << 16));
        var up = RenderRack(rack, 500, 480);
        t.NearCents("MIDI bend full up", HzOf(up, 150, 480), Playable(1100, false), 3);
        midi.Handle(0xE1, 0x00, 0x40);
        var centre = RenderRack(rack, 500, 480);
        t.NearCents("MIDI bend centre", HzOf(centre, 150, 480), Playable(900, false), 3);

        // a note on with velocity 0 is a note off
        midi.Handle(0x91, 69, 0);
        var tail = RenderRack(rack, 500, 480);
        t.Expect(rack.LiveVoices == 0 && Level(tail, rate, 300, 500) < 0.002, $"velocity-0 note on left {rack.LiveVoices} voice(s), rms {Level(tail, rate, 300, 500):0.0000}");

        // velocity sets the loudness
        midi.Handle(0x90, 69, 127); var loud = Level(RenderRack(rack, 500, 480), rate, 150, 500); midi.Handle(0x90, 69, 0); RenderRack(rack, 400, 480);
        midi.Handle(0x90, 69, 30); var soft = Level(RenderRack(rack, 500, 480), rate, 150, 500); midi.Handle(0x90, 69, 0); RenderRack(rack, 400, 480);
        t.Expect(loud > soft * 1.5 && soft > 0.001, $"velocity 127 rms {loud:0.0000} vs velocity 30 rms {soft:0.0000}");

        // "everything to the selected channel": a keyboard that only knows channel 1 can play the triangle
        midi.Routing = MidiRouting.AllToSelected; midi.Selected = 2;
        midi.Handle(0x90, 57, 100);
        var tri = RenderRack(rack, 600, 480);
        t.Expect(Live(rack).SequenceEqual([0, 0, 1, 0]), $"AllToSelected gave voices {string.Join(",", Live(rack))}");
        t.NearCents("selected triangle A3", HzOf(tri, 200, 550), Playable(-300, true), 3);
        midi.Handle(0xB0, 123, 0);                       // CC 123: all notes off
        var quiet = RenderRack(rack, 500, 480);
        t.Expect(rack.LiveVoices == 0 && Level(quiet, rate, 300, 500) < 0.002, "CC 123 did not end the note");

        // noise on the wire is harmless and not counted as music
        long before = midi.Messages;
        foreach (byte b in new byte[] { 0xF8, 0xFE, 0x00, 0x7F, 0xA0, 0xC0, 0xD0 }) midi.Handle(b, 0x10, 0x10);
        t.Expect(midi.Messages == before, "system and unsupported messages were counted as handled");
        midi.Handle(0x90, 200, 100);                     // a data byte above 127 is masked, not an exception
        t.Expect(RenderRack(rack, 100, 480).All(float.IsFinite), "output went non-finite after odd MIDI");
    }

    private void Realtime(T t)
    {
        var s = new Scenario { Name = "rt", Tempo = 60, LengthBeats = 11 };
        for (int c = 0; c < 4; c++) s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 9, Key = 57 + c * 5, Channel = c, Slides = new() { new SlideSeg { StartBeat = 1, LengthBeats = 8, ToKey = 69 + c } } });
        var rack = MakeRack([0, 1, 2, 3]);
        var plugin = rack[0];
        long allocBefore = 0, allocAfter = 0;
        var mixer = new Mixer { SampleRate = 48000, BlockSize = 512, AfterBlock = sm => { if (sm >= 48000 * 2 && allocBefore == 0) allocBefore = plugin.Test(4); if (sm >= 48000 * 9 && allocAfter == 0) allocAfter = plugin.Test(4); } };
        var sw = Stopwatch.StartNew();
        var audio = mixer.Run(rack, s, 0);
        sw.Stop();
        plugin.Dispose();
        double seconds = audio.Length / 2 / 48000.0;
        double speed = seconds / sw.Elapsed.TotalSeconds;
        t.Expect(speed >= 5, $"{speed:0.0}x real time with 4 instances (needs 5x or better: each instance runs its own chip)");
        long alloc = allocAfter - allocBefore;
        t.Expect(alloc == 0, $"{alloc:N0} bytes allocated by the plugin while playing 4 sliding voices for 7 s (must be 0: garbage collection is audible)");
        t.Note($"{speed:0.0}x real time, 4 instances with a sliding voice each, 48 kHz / 512-sample blocks; steady-state allocation {alloc} B");

        using var rp = RomPlugin();
        RenderBlocks(rp, 1.0);
        long a0 = rp.Test(4);
        var sw2 = Stopwatch.StartNew();
        RenderBlocks(rp, 8.0);
        sw2.Stop();
        long a1 = rp.Test(4);
        double rspeed = 8.0 / sw2.Elapsed.TotalSeconds;
        t.Expect(rspeed >= 2.5, $"ROM mode runs at {rspeed:0.0}x real time (needs 2.5x or better)");
        t.Warn(a1 - a0 < 1000, $"ROM mode allocated {(a1 - a0) / 1024:N0} KB in 8 s while playing (garbage collection is audible)");
        rp.Dispose();                                  // its emulator would otherwise be the one the next instances share
        // a Game Boy and a SNES game on the same budget (the test games are small: a real game costs somewhat more)
        var speeds = new List<string> { $"NES {rspeed:0.0}x" };
        string dir = Path.GetTempPath(), tag = Guid.NewGuid().ToString("N");
        string gb = Path.Combine(dir, $"bn2_{tag}.gb"), sfc = Path.Combine(dir, $"bn2_{tag}.sfc");
        File.WriteAllBytes(gb, MakeGbRom(tone: true));
        File.WriteAllBytes(sfc, MakeSnesRom());
        try
        {
            foreach (var (name, path, console) in new[] { ("Game Boy", gb, 1), ("SNES", sfc, 2) })
            {
                using var g = OpenGame(path, console);
                RenderBlocks(g, 1.0);
                var sw3 = Stopwatch.StartNew();
                RenderBlocks(g, 6.0);
                sw3.Stop();
                double speed3 = 6.0 / sw3.Elapsed.TotalSeconds;
                t.Expect(speed3 >= 2.5, $"{name} game runs at {speed3:0.0}x real time (needs 2.5x or better)");
                speeds.Add($"{name} {speed3:0.0}x");
            }
        }
        finally { try { File.Delete(gb); File.Delete(sfc); } catch { } }
        t.Note($"ROM mode: {string.Join(", ", speeds)} real time (test games), allocation {(a1 - a0):N0} B in 8 s");
    }

    // ------------------------------------------------------------------------------------------------
    // ROM mode

    private static void WaitForRom(PluginInstance p, int timeoutMs = 30000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (p.Test(9) != 1 && Environment.TickCount64 < until) Thread.Sleep(10);
    }

    private static byte[] StateWithRom(byte[] defaultState, string path)
    {
        // a state is [magic, version, count] + count int parameters + the ROM-path trailer: keep the parameters, replace the trailer
        var b = defaultState[..(12 + 4 * ParamNames.Length)].ToList();
        var utf8 = System.Text.Encoding.UTF8.GetBytes(path);
        b.AddRange(BitConverter.GetBytes(utf8.Length));
        b.AddRange(utf8);
        return b.ToArray();
    }

    /// <summary>A plugin in ROM mode running the test ROM (the --rom file when given, else the built-in game), restored from a saved
    /// state the way a project loads it.</summary>
    private PluginInstance RomPlugin(int rate = 48000, Action<PluginInstance>? before = null)
    {
        byte[] state;
        using (var seed = lib.Create())
        {
            seed.SetParam(PMode, 1);
            before?.Invoke(seed);
            state = seed.SaveState();
        }
        var p = lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, rate);
        p.LoadState(romPath != null ? StateWithRom(state, romPath) : state);
        WaitForRom(p);
        return p;
    }

    private static float[] RenderBlocks(PluginInstance p, double seconds, int rate = 48000, Action<int>? atBlock = null)
    {
        int blocks = (int)(seconds * rate / 512);
        var mono = new float[blocks * 512];
        var buf = new float[1024];
        fixed (float* f = buf)
            for (int b = 0; b < blocks; b++)
            {
                atBlock?.Invoke(b);
                p.NewTick();
                p.GenRender(f, 512);
                for (int i = 0; i < 512; i++) mono[b * 512 + i] = (buf[i * 2] + buf[i * 2 + 1]) / 2;
            }
        return mono;
    }

    private bool NeedRom(T t) => true;   // the built-in game is inside the plugin

    private void RomMode(T t)
    {
        using var p = RomPlugin();
        var audio = RenderBlocks(p, 3.0);
        t.Expect(p.Test(9) == 1, "the ROM did not load");
        double rms = Audio.Rms(audio, 48000, audio.Length);
        t.Expect(rms > 0.003, $"no sound from the ROM (rms {rms:0.0000})");
        t.Expect(audio.All(float.IsFinite), "non-finite samples");
        long frames = p.Test(11);
        t.Expect(Math.Abs(frames - 3.0 * 60.0988) < 8, $"{frames} NES frames in 3.0 s (expected about {3.0 * 60.0988:0})");
        t.Expect(p.Test(12) == 0, "the ROM crashed");
        long h1 = p.Test(10), v1 = p.Test(13);
        t.Expect(h1 != 0, "no picture was captured");
        t.Expect(v1 >= frames - 2, $"picture version {v1} after {frames} frames");
        // the picture changes over time (the title screen animates / the game runs)
        RenderBlocks(p, 2.0);
        long h2 = p.Test(10);
        t.Warn(h1 != h2, "the picture did not change in 2 seconds (a static screen?)");
        t.Note($"{(romPath != null ? "--rom file" : "built-in game")}: {frames} frames, rms {rms:0.000}, picture hash {(h1 != 0 ? "ok" : "none")}");

        // the project remembers the game: save, close, load into a new plugin
        var saved = p.SaveState();
        p.Dispose();
        using (var q = lib.Create())
        {
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.LoadState(saved);
            WaitForRom(q);
            RenderBlocks(q, 0.5);
            t.Expect(q.Test(9) == 1, "the game was not restored from the saved project");
            t.Expect(q.GetParam(PMode) == 1, "the mode was not restored");
        }

        // a project whose ROM has gone missing opens without a crash, silent
        using (var m = lib.Create())
        {
            m.LoadState(StateWithRom(saved, Path.Combine(Path.GetTempPath(), "no_such_rom_" + Guid.NewGuid().ToString("N") + ".nes")));
            var a2 = RenderBlocks(m, 0.3);
            t.Expect(m.Test(9) == 0 && a2.All(f => f == 0f), "a missing ROM should leave the plugin silent and empty");
        }

        // switching to ROM mode with nothing chosen loads the built-in game and plays it; switching back plays notes again
        using (var e = lib.Create())
        {
            e.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            e.SetParam(PMode, 1);
            WaitForRom(e);
            var ea = RenderBlocks(e, 2.5);
            t.Expect(e.Test(9) == 1 && Audio.Rms(ea, 24000, ea.Length) > 0.003, "switching to ROM mode did not load and play the built-in game");
            t.Expect(e.Test(10) != 0, "the built-in game made no picture");
            e.SetParam(PMode, 0);
            var mixer = new Mixer { SampleRate = 48000 };
            var direct = Mono(mixer.Run(e, One(69, 0, 0.6)));
            t.Expect(Audio.Rms(direct, 8000, direct.Length - 8000) > 0.01, "Direct mode does not play after switching back");
        }

        // a ROM file that is not a ROM: no exception, no sound, the plugin lives on
        string junk = Path.Combine(Path.GetTempPath(), "bn2_junk_" + Guid.NewGuid().ToString("N") + ".nes");
        File.WriteAllBytes(junk, new byte[100]);
        try
        {
            byte[] st;
            using (var seed = lib.Create()) { seed.SetParam(PMode, 1); st = seed.SaveState(); }
            using var j = lib.Create();
            j.LoadState(StateWithRom(st, junk));
            Thread.Sleep(400);
            var a3 = RenderBlocks(j, 0.3);
            t.Expect(a3.All(float.IsFinite) && j.Test(9) == 0, "a junk ROM should leave the plugin empty and quiet");
        }
        finally { File.Delete(junk); }
    }

    private void RomChips(T t)
    {
        using var p = RomPlugin();
        RenderBlocks(p, 1.0);
        var chips = Chips(p);
        t.Expect(p.Test(14) == 0 && p.Test(15) == 0, $"the built-in game should run on the NES console (console {p.Test(14)}, game {p.Test(15)})");
        var log = new List<string>();
        foreach (var id in new[] { "QN", "HI", "FIXS", "LOW", "FIX" })
        {
            long versionBefore = p.Test(13);
            int idx = chips.IndexOf(id);
            p.SetParam(PCore, idx);
            var a = RenderBlocks(p, 1.0);
            double rms = Audio.Rms(a, 4800, a.Length);
            t.Expect(p.Test(5) == idx, $"{id}: the sound chip did not change");
            t.Expect(rms > 0.003, $"{id}: silent after the switch (rms {rms:0.0000})");
            t.Expect(p.Test(12) == 0, $"{id}: the game crashed");
            t.Expect(p.Test(13) > versionBefore + 30, $"{id}: the picture stopped updating");
            t.Expect(a.All(float.IsFinite), $"{id}: non-finite audio");
            log.Add($"{id} ok");
        }
        t.Note(string.Join(", ", log) + "; CPU and PPU are not choices any more: the game runs on FIX");
    }

    // ------------------------------------------------------------------------------------------------
    // The emulator hub: one instance = one channel, instances share emulators

    private void HubShare(T t)
    {
        var all = new List<PluginInstance>();
        try
        {
            // Auto fills the first emulator channel by channel
            for (int i = 0; i < 4; i++) all.Add(lib.Create());
            t.Expect(all.All(p => p.Test(16) == 1), $"four Auto instances should share emulator #1: {string.Join(",", all.Select(p => p.Test(16)))}");
            t.Expect(all.Select(p => (int)p.Test(17)).SequenceEqual([0, 1, 2, 3]), $"channels {string.Join(",", all.Select(p => p.Test(17)))}, expected 0,1,2,3 (Auto takes the first free channel)");
            t.Expect(all[0].Test(18) == 1 && all[0].Test(19) == 4, $"{all[0].Test(18)} emulators, {all[0].Test(19)} instances on #1");
            // a fifth needs a second emulator
            var fifth = lib.Create(); all.Add(fifth);
            t.Expect(fifth.Test(16) == 2 && fifth.Test(17) == 0 && fifth.Test(18) == 2, $"fifth instance: emulator {fifth.Test(16)}, channel {fifth.Test(17)}, {fifth.Test(18)} emulators");
            // closing one frees its channel for the next Auto instance: no new emulator
            all[1].Destroy();   // pulse 2 of #1
            var sixth = lib.Create(); all.Add(sixth);
            t.Expect(sixth.Test(16) == 1 && sixth.Test(17) == 1, $"after closing pulse 2, a new Auto instance should take it: emulator {sixth.Test(16)}, channel {sixth.Test(17)}");
            // closing the last instance of #2 closes the emulator
            fifth.Destroy();
            t.Expect(sixth.Test(18) == 1, $"emulator #2 should close with its last instance: {sixth.Test(18)} emulators");
            // shared configuration: a chip or mode change on one instance shows on the others
            var chips = Chips(all[2]);
            int qn = chips.IndexOf("QN");
            all[2].SetParam(PCore, qn);
            t.Expect(all[0].GetParam(PCore) == qn && all[3].GetParam(PCore) == qn && sixth.GetParam(PCore) == qn, "a sound-chip change did not reach the other instances of the emulator");
            all[2].SetParam(PMode, 1);
            t.Expect(all[0].GetParam(PMode) == 1 && sixth.GetParam(PMode) == 1, "a mode change did not reach the other instances of the emulator");
            t.Expect(all[0].Test(21) == 1 && all[3].Test(21) == 1, "the emulator's mode does not read ROM on every instance");
            all[2].SetParam(PMode, 0);
            // ... and a Mix instance goes back to a tone channel when the emulator returns to Direct
            t.Expect(all.Where(p => p.Test(16) == 1 && p.Test(19) > 0).All(p => p.Test(17) is >= 0 and < 4), "after returning to Direct no instance may sit on Mix");
        }
        finally { foreach (var p in all) p.Destroy(); }
    }

    private void HubExplicit(T t)
    {
        var all = new List<PluginInstance>();
        try
        {
            var a = lib.Create(); all.Add(a);                          // Auto: emulator #1, NES chip
            var gb = lib.Create(); all.Add(gb);
            gb.SetParam(PEmulator, 2);                                 // explicit: an emulator of its own
            gb.SetParam(PCore, Chips(gb).IndexOf("DMG"));              // ... with the Game Boy chip
            t.Expect(a.Test(16) == 1 && gb.Test(16) == 2 && a.Test(18) == 2, $"emulators {a.Test(16)} / {gb.Test(16)}, {a.Test(18)} in total");
            t.Expect(a.GetParam(PCore) == Chips(a).IndexOf("FIX") && gb.GetParam(PCore) == Chips(gb).IndexOf("DMG"), "emulators #1 and #2 should keep their own sound chips");
            // both play, each on its own chip, at the right pitch
            foreach (var (inst, name) in new[] { (a, "NES chip"), (gb, "Game Boy chip") })
            {
                var m = new Mixer { SampleRate = 44100 };
                var audio = Mono(m.Run(inst, One(69, 0, 0.6)));
                double hz = SteadyHz(audio, 44100, m.Log[0]);
                t.NearCents(name, hz, Nes.PeriodHz(Nes.Period(900, false), false), 6);
            }
            // Auto instances never fill an explicit emulator: #1 takes four, the next opens #3
            for (int i = 0; i < 3; i++) all.Add(lib.Create());
            var next = lib.Create(); all.Add(next);
            t.Expect(next.Test(16) == 3, $"the fifth Auto instance should open emulator #3 (not use the explicit #2): got #{next.Test(16)}");
            // moving an Auto instance to an explicit emulator, and back to Auto
            var mover = all[2];
            mover.SetParam(PEmulator, 2);
            t.Expect(mover.Test(16) == 2 && mover.Test(17) == 1, $"explicit move to #2 (pulse 1 is taken there): emulator {mover.Test(16)} channel {mover.Test(17)}, expected pulse 2");
            mover.SetParam(PEmulator, 0);
            t.Expect(mover.Test(16) == 1, $"back to Auto: emulator {mover.Test(16)} (the first with room)");
        }
        finally { foreach (var p in all) p.Destroy(); }
    }

    private void HubConflict(T t)
    {
        var all = new List<PluginInstance>();
        try
        {
            var a = lib.Create(); all.Add(a);
            a.SetParam(PEmulator, 1); a.SetParam(PChannel, 1);           // explicit emulator #1, pulse 1
            var b = lib.Create(); all.Add(b);
            b.SetParam(PEmulator, 1); b.SetParam(PChannel, 1);           // the same channel
            t.Expect(a.Test(17) == 0 && a.Test(20) == 0, $"the first claim: channel {a.Test(17)}, problem {a.Test(20)}");
            t.Expect(b.Test(17) == -1 && b.Test(20) == 1, $"the second claim of pulse 1 should be refused with a problem: channel {b.Test(17)}, problem {b.Test(20)}");
            var silent = Mono(new Mixer { SampleRate = 44100 }.Run(b, One(69, 0, 0.5)));
            t.Expect(Audio.Peak(silent, 0, silent.Length) < 1e-4, "an instance without a channel must be silent");
            // Mix exists in ROM mode only
            var c = lib.Create(); all.Add(c);
            c.SetParam(PEmulator, 1); c.SetParam(PChannel, 5);
            t.Expect(c.Test(17) == -1 && c.Test(20) == 1, $"Mix in Direct mode: channel {c.Test(17)}, problem {c.Test(20)}");
            // when pulse 1 frees up, the waiting instance gets it
            a.Destroy();
            t.Expect(b.Test(17) == 0 && b.Test(20) == 0, $"the waiting instance should get pulse 1 when it frees up: channel {b.Test(17)}, problem {b.Test(20)}");
            var audio = Mono(new Mixer { SampleRate = 44100 }.Run(b, One(69, 0, 0.5)));
            t.Expect(Audio.Rms(audio, 8000, audio.Length - 8000) > 0.01, "the instance that got pulse 1 does not sound");
        }
        finally { foreach (var p in all) p.Destroy(); }
    }

    private void HubState(T t)
    {
        var all = new List<PluginInstance>();
        byte[][] saved;
        try
        {
            var a = lib.Create(); all.Add(a);
            var b = lib.Create(); all.Add(b);
            var gb = lib.Create(); all.Add(gb);
            gb.SetParam(PEmulator, 2); gb.SetParam(PChannel, 3);         // emulator #2, triangle
            gb.SetParam(PCore, Chips(gb).IndexOf("DMG"));
            b.SetParam(PVolume, 345);
            saved = all.Select(p => p.SaveState()).ToArray();
        }
        finally { foreach (var p in all) p.Destroy(); all.Clear(); }

        try
        {
            foreach (var blob in saved) { var p = lib.Create(); p.LoadState(blob); all.Add(p); }
            var (ra, rb, rg) = (all[0], all[1], all[2]);
            t.Expect(ra.Test(16) == 1 && ra.Test(17) == 0, $"restored 1st: emulator {ra.Test(16)} channel {ra.Test(17)}");
            t.Expect(rb.Test(16) == 1 && rb.Test(17) == 1, $"restored 2nd: emulator {rb.Test(16)} channel {rb.Test(17)}");
            t.Expect(rg.Test(16) == 2 && rg.Test(17) == 2, $"restored 3rd: emulator {rg.Test(16)} channel {rg.Test(17)}");
            t.Expect(rg.GetParam(PCore) == Chips(rg).IndexOf("DMG") && ra.GetParam(PCore) == Chips(ra).IndexOf("FIX"), "the emulators did not get their saved sound chips back");
            t.Expect(rb.GetParam(PVolume) == 345, "the instance's own volume was not restored");
            t.Expect(ra.Test(18) == 2 && ra.Test(19) == 2, $"{ra.Test(18)} emulators, {ra.Test(19)} instances on #1 after restoring");
        }
        finally { foreach (var p in all) p.Destroy(); all.Clear(); }

        // restored in another order, each still gets its own place back
        try
        {
            foreach (int i in new[] { 2, 1, 0 }) { var p = lib.Create(); p.LoadState(saved[i]); all.Add(p); }
            t.Expect(all[0].Test(16) == 2 && all[0].Test(17) == 2 && all[1].Test(16) == 1 && all[1].Test(17) == 1 && all[2].Test(16) == 1 && all[2].Test(17) == 0,
                $"restored in reverse order: ({all[0].Test(16)},{all[0].Test(17)}) ({all[1].Test(16)},{all[1].Test(17)}) ({all[2].Test(16)},{all[2].Test(17)})");
        }
        finally { foreach (var p in all) p.Destroy(); }
    }

    private void RomStems(T t)
    {
        const int rate = 48000;
        var rack = new List<PluginInstance>();
        try
        {
            var first = lib.Create();
            first.Dispatcher(Fpd.SetSampleRate, 0, rate);
            first.SetParam(PMode, 1);                                   // the first Auto instance in ROM mode takes the whole mix
            rack.Add(first);
            for (int i = 0; i < 4; i++) { var p = lib.Create(); p.Dispatcher(Fpd.SetSampleRate, 0, rate); rack.Add(p); }
            t.Expect(rack.Select(p => (int)p.Test(17)).SequenceEqual([4, 0, 1, 2, 3]), $"channels {string.Join(",", rack.Select(p => p.Test(17)))}, expected Mix then the four tone channels");
            t.Expect(rack.All(p => p.Test(16) == 1), "all five instances should share emulator #1");
            WaitForRom(first);

            // all five listen to the same game in lockstep, block by block, from several threads like FL's mixer does
            const int seconds = 6, block = 512;
            int blocks = seconds * rate / block;
            var outs = rack.Select(_ => new float[blocks * block]).ToArray();
            var bufs = rack.Select(_ => new float[block * 2]).ToArray();
            for (int b = 0; b < blocks; b++)
            {
                int bb = b;
                Parallel.For(0, rack.Count, i =>
                {
                    fixed (float* f = bufs[i])
                    {
                        rack[i].NewTick();
                        rack[i].GenRender(f, block);
                    }
                    for (int k = 0; k < block; k++) outs[i][bb * block + k] = (bufs[i][k * 2] + bufs[i][k * 2 + 1]) / 2;
                });
            }
            int from = rate;   // skip the first second
            double[] rms = outs.Select(o => Audio.Rms(o, from, o.Length)).ToArray();
            t.Expect(rms[0] > 0.003, $"the Mix instance is silent (rms {rms[0]:0.0000})");
            int sounding = rms.Skip(1).Count(r => r > 0.0005);
            t.Expect(sounding >= 3, $"only {sounding} of the 4 channel instances sound (rms {string.Join(", ", rms.Skip(1).Select(r => r.ToString("0.0000")))})");
            // the channels are distinct sounds, and together they are the game's mix
            var sum = new float[outs[0].Length];
            for (int i = 1; i < 5; i++) for (int k = 0; k < sum.Length; k++) sum[k] += outs[i][k];
            double corr = Correlation(outs[0], sum, from);
            t.Expect(corr > 0.9, $"the four channel stems summed should match the whole mix (correlation {corr:0.000}, expected > 0.9)");
            double c12 = Correlation(outs[1], outs[2], from);
            t.Expect(Math.Abs(c12) < 0.9, $"pulse 1 and pulse 2 stems are the same sound (correlation {c12:0.000})");
            t.Note($"rms mix {rms[0]:0.000}, channels {string.Join("/", rms.Skip(1).Select(r => r.ToString("0.000")))}; stems-sum vs mix correlation {corr:0.000}");

            // the sound chip changes under the running game: every channel keeps sounding
            rack[0].SetParam(PCore, Chips(rack[0]).IndexOf("QN"));
            var after = rack.Select(_ => new float[(rate / block) * block * 2]).ToArray();
            for (int b = 0; b < rate / block * 2; b++)
                for (int i = 0; i < rack.Count; i++)
                {
                    var buf = bufs[i];
                    fixed (float* f = buf) { rack[i].NewTick(); rack[i].GenRender(f, block); }
                    if (b >= rate / block) for (int k = 0; k < block; k++) after[i][(b - rate / block) * block + k] = (buf[k * 2] + buf[k * 2 + 1]) / 2;
                }
            double[] rms2 = after.Select(o => Audio.Rms(o, 0, o.Length / 2)).ToArray();
            t.Expect(rms2[0] > 0.003, $"the mix is silent after switching the sound chip (rms {rms2[0]:0.0000})");
            t.Expect(rms2.Skip(1).Count(r => r > 0.0005) >= 3, $"channel stems silent after switching the sound chip (rms {string.Join(", ", rms2.Skip(1).Select(r => r.ToString("0.0000")))})");
            t.Expect(rack[0].Test(12) == 0, "the game crashed");
        }
        finally { foreach (var p in rack) p.Destroy(); }
    }

    /// <summary>Pearson correlation of two signals from sample <paramref name="from"/> to the end.</summary>
    private static double Correlation(float[] a, float[] b, int from)
    {
        int n = Math.Min(a.Length, b.Length) - from;
        double ma = 0, mb = 0;
        for (int i = from; i < from + n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;
        double sab = 0, saa = 0, sbb = 0;
        for (int i = from; i < from + n; i++) { double x = a[i] - ma, y = b[i] - mb; sab += x * y; saa += x * x; sbb += y * y; }
        return saa == 0 || sbb == 0 ? 0 : sab / Math.Sqrt(saa * sbb);
    }

    // ------------------------------------------------------------------------------------------------
    // The editor, driven through real mouse messages

    private static (int X, int Y, int W, int H) RectOf(long packed) => ((int)(packed & 0xFFFF), (int)((packed >> 16) & 0xFFFF), (int)((packed >> 32) & 0xFFFF), (int)((packed >> 48) & 0xFFFF));

    /// <summary>The centre of a control, from the editor's own record of where it drew it (null when it is not on screen).</summary>
    private static (int X, int Y)? Centre(PluginInstance p, int id)
    {
        long r = p.Test(30, id);
        if (r == 0) return null;
        var (x, y, w, h) = RectOf(r);
        return (x + w / 2, y + h / 2);
    }

    private bool ClickControl(T t, PluginInstance p, int id, string what)
    {
        var c = Centre(p, id);
        if (c == null) { t.Expect(false, $"{what}: control not on screen"); return false; }
        Win32Host.Click(p.EditorHandle, c.Value.X, c.Value.Y);
        return true;
    }

    private void Editor(T t)
    {
        using var p = lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        nint parent = Win32Host.CreateParent();
        try
        {
            p.ShowEditor(parent);
            t.Expect(p.EditorHandle != 0, "EditorHandle still 0 after FPD_ShowEditor");
            if (p.EditorHandle == 0) return;
            Win32Host.Pump(150);
            var hwnd = p.EditorHandle;

            // every control is on screen
            foreach (var (id, name) in new[] { (1, "Emulator"), (2, "Direct"), (3, "ROM"), (4, "Sound chip"), (7, "Game"), (9, "Channel"), (40, "About"), (80, "Inputs"), (60, "Reset Console"), (50, "console NES"), (51, "console Game Boy"), (52, "console SNES"),
                                               (10, "cell Pulse 1"), (11, "cell Pulse 2"), (12, "cell Triangle"), (13, "cell Noise"), (21, "Volume"), (22, "Pan"), (23, "Coarse"), (24, "Fine"), (25, "Pulse duty") })
                t.Expect(p.Test(30, id) != 0, $"control '{name}' is not drawn");
            t.Expect(p.Test(30, 14) == 0, "the Mix cell should be unavailable in Direct mode");

            // Direct mode shows the bypass message on the screen, not a picture
            t.Expect(p.Test(35) == 0, "a picture is shown in Direct mode");
            var shot = Win32Host.CapturePixels(hwnd, out int w, out int h);
            int text = BrightPixels(shot, w, 40, 260, 480, 100);
            t.Expect(text > 150, $"no message text on the screen in Direct mode ({text} bright pixels)");

            // the sound chip dropdown: opens, lists every chip, picking one changes the parameter and tells the host
            var chips = Chips(p);
            int qn = chips.IndexOf("QN");
            ClickControl(t, p, 4, "chip dropdown");
            t.Expect(p.Test(31) == 4, "the chip list did not open");
            t.Expect(p.Test(32) == chips.Count - 3, $"the chip list has {p.Test(32)} items, expected the {chips.Count - 3} NES chips (the Game Boy and SNES chips are under their own consoles)");
            int before = p.Host.ParamChanges.Count;
            // scroll the list until QN is visible, then click it
            for (int guard = 0; guard < 40 && p.Test(30, 100 + qn) == 0; guard++) Win32Host.Wheel(hwnd, -1);
            var item = Centre(p, 100 + qn);
            t.Expect(item != null, "QN is not reachable in the open list");
            if (item != null) Win32Host.Click(hwnd, item.Value.X, item.Value.Y);
            t.Expect(p.Test(31) == 0, "the list did not close after a pick");
            t.Expect(p.GetParam(PCore) == qn, $"picking QN left the sound chip at {p.GetParam(PCore)}");
            t.Expect(p.Host.ParamChanges.Count == before + 1 && p.Host.ParamChanges[^1] == (PCore, qn), "the host was not told (OnParamChanged) about the chip change");
            // a click outside closes an open list without changing anything
            ClickControl(t, p, 4, "chip dropdown");
            Win32Host.Click(hwnd, 20, 20);
            t.Expect(p.Test(31) == 0 && p.GetParam(PCore) == qn, "a click outside should close the list and change nothing");

            // the emulator dropdown: pick #2 (a new emulator), then back to Auto
            PickItem(t, p, 1, 2, "Emulator #2");
            t.Expect(p.GetParam(PEmulator) == 2 && p.Test(16) == 2, $"emulator pick: parameter {p.GetParam(PEmulator)}, sitting on #{p.Test(16)}");
            PickItem(t, p, 1, 0, "Emulator Auto");
            t.Expect(p.GetParam(PEmulator) == 0, "back to Auto did not stick");

            // the channel dropdown and the channel strip
            PickItem(t, p, 9, 3, "Channel Triangle");           // list items: Auto, Pulse 1, Pulse 2, Triangle...
            t.Expect(p.Test(17) == 2, $"channel pick: sitting on channel {p.Test(17)}, expected 2 (triangle)");
            ClickControl(t, p, 13, "noise cell");
            t.Expect(p.Test(17) == 3 && p.GetParam(PChannel) == 4, $"clicking the noise cell: channel {p.Test(17)}");
            t.Expect(p.Test(30, 25) == 0 && p.Test(30, 27) != 0, "on the noise channel the Noise mode slider replaces Pulse duty");

            // the console selector: Game Boy lists only its own chips, SNES has one, NES again
            ClickControl(t, p, 51, "Game Boy segment");
            t.Expect(p.GetParam(PConsole) == 1 && p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == "DMG", $"Game Boy segment: console {p.GetParam(PConsole)}, chip {p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore))}");
            ClickControl(t, p, 4, "chip dropdown (Game Boy)");
            t.Expect(p.Test(32) == 2, $"the Game Boy chip list has {p.Test(32)} items, expected DMG and DMGS");
            Win32Host.Click(hwnd, 20, 20);
            ClickControl(t, p, 52, "SNES segment");
            t.Expect(p.GetParam(PConsole) == 2 && p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == "SNES", "SNES segment");
            ClickControl(t, p, 50, "NES segment");
            t.Expect(p.GetParam(PConsole) == 0 && p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == "FIX", "NES segment");

            // the mode switch
            ClickControl(t, p, 3, "ROM segment");
            t.Expect(p.GetParam(PMode) == 1 && p.Test(21) == 1, "the ROM segment did not switch the mode");
            ClickControl(t, p, 2, "Direct segment");
            t.Expect(p.GetParam(PMode) == 0 && p.Test(21) == 0, "the Direct segment did not switch the mode back");

            // a slider: a drag sets the value, a double-click resets it
            var vol = RectOf(p.Test(30, 21));
            int y = vol.Y + vol.H / 2;
            Win32Host.Drag(hwnd, vol.X + vol.W / 2, y, vol.X + 8, y);
            t.Expect(p.GetParam(PVolume) < 150, $"dragging the volume slider to the left end left it at {p.GetParam(PVolume)}");
            Win32Host.DoubleClick(hwnd, vol.X + vol.W / 2, y);
            t.Expect(p.GetParam(PVolume) == 1000, $"double-click should reset the volume: {p.GetParam(PVolume)}");
            Win32Host.Wheel(hwnd, -1, vol.X + vol.W / 2, y);

            // external changes show up (automation / another instance)
            p.SetParam(PVolume, 250);
            Win32Host.Pump(100);
            t.Expect(p.Test(33) > 0, "the editor never painted");

            p.ShowEditor(parent);   // showing again re-parents, must not create a second editor
            p.HideEditor();
            t.Expect(p.EditorHandle == 0, "EditorHandle not cleared after hiding");
            p.ShowEditor(parent);
            t.Expect(p.EditorHandle != 0, "editor did not reopen");
            p.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); }
    }

    /// <summary>Opens a dropdown and clicks item <paramref name="index"/> (scrolling the list with the wheel when needed).</summary>
    private void PickItem(T t, PluginInstance p, int dropdownId, int index, string what)
    {
        if (!ClickControl(t, p, dropdownId, what)) return;
        t.Expect(p.Test(31) == dropdownId, $"{what}: the list did not open");
        for (int guard = 0; guard < 40 && p.Test(30, 100 + index) == 0; guard++) Win32Host.Wheel(p.EditorHandle, -1);
        var c = Centre(p, 100 + index);
        t.Expect(c != null, $"{what}: item {index} not reachable");
        if (c != null) Win32Host.Click(p.EditorHandle, c.Value.X, c.Value.Y);
    }

    private static int BrightPixels(byte[] bgra, int w, int x0, int y0, int rw, int rh)
    {
        int n = 0;
        for (int y = y0; y < y0 + rh; y++)
            for (int x = x0; x < x0 + rw; x++)
            {
                int o = (y * w + x) * 4;
                if (o + 2 >= bgra.Length) continue;
                int lum = (bgra[o] + bgra[o + 1] + bgra[o + 2]) / 3;
                if (lum > 150) n++;
            }
        return n;
    }

    private void RomEditor(T t)
    {
        using var p = lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        nint parent = Win32Host.CreateParent();
        try
        {
            p.ShowEditor(parent);
            if (p.EditorHandle == 0) { t.Expect(false, "no editor"); return; }
            Win32Host.Pump(100);
            var hwnd = p.EditorHandle;
            t.Expect(p.Test(35) == 0, "a picture is shown before ROM mode");

            // switching to ROM loads the built-in game: the message gives way to the picture
            ClickControl(t, p, 3, "ROM segment");
            t.Expect(p.GetParam(PMode) == 1, "the ROM segment did not switch the mode");
            WaitForRom(p);
            long v0 = p.Test(13);
            for (int k = 0; k < 8; k++) { RenderBlocks(p, 0.2); Win32Host.Pump(60); }
            t.Expect(p.Test(9) == 1, "the built-in game did not load when the mode switched to ROM");
            t.Expect(p.Test(13) > v0, "the picture version did not advance while the editor was open");
            t.Expect(p.Test(35) == 1, "the editor does not show the game's picture");
            var shot = Win32Host.CapturePixels(hwnd, out int w, out int h);
            t.Expect(DistinctColours(shot, w, 20, 68, 512, 480) >= 6, "the screen area does not look like a game picture (too few colours)");
            // nobody pulls audio (FL stopped): the open editor still keeps the picture moving
            long vIdle = p.Test(13);
            Win32Host.Pump(700);
            t.Expect(p.Test(13) > vIdle + 15, $"with no audio running the picture should keep moving while the editor is open ({p.Test(13) - vIdle} new frames in 0.7 s)");
            // channel strip: in ROM mode the Mix cell exists, and Auto put this instance on it
            t.Expect(p.Test(30, 14) != 0 && p.Test(17) == 4, $"ROM mode: Mix cell present {p.Test(30, 14) != 0}, sitting on channel {p.Test(17)}");

            // the sound chip switches from the dropdown while the game runs
            var chipNames = Chips(p);
            int qn = chipNames.IndexOf("QN");
            PickItem(t, p, 4, qn, "Sound chip QN");
            RenderBlocks(p, 0.5);
            t.Expect(p.GetParam(PCore) == qn && p.Test(5) == qn, $"the chip pick did not reach the running game ({p.Test(5)} vs {qn})");
            t.Expect(p.Test(12) == 0, "the game crashed on the chip switch");

            // Reset Console restarts the game: the picture gives way to the loading message, then the new game's picture comes back
            ClickControl(t, p, 60, "Reset Console button");
            t.Expect(p.Test(35) == 0, "the picture should give way to the loading message while the game restarts");
            bool back = false;
            for (int k = 0; k < 200 && !back; k++) { RenderBlocks(p, 0.1); Win32Host.Pump(20); back = p.Test(35) == 1; }
            t.Expect(back, "the picture did not come back after Reset Console");

            // the Game list offers the built-in game; Direct brings the bypass message back
            PickItem(t, p, 7, 0, "Game: built-in");
            ClickControl(t, p, 2, "Direct segment");
            Win32Host.Pump(80);
            t.Expect(p.Test(35) == 0 && BrightPixels(Win32Host.CapturePixels(hwnd, out w, out h), w, 40, 260, 480, 100) > 150, "Direct mode should show the bypass message again");
            p.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); }
    }

    private static int DistinctColours(byte[] bgra, int w, int x0, int y0, int rw, int rh)
    {
        var set = new HashSet<int>();
        for (int y = y0; y < y0 + rh; y += 3)
            for (int x = x0; x < x0 + rw; x += 3)
            {
                int o = (y * w + x) * 4;
                if (o + 2 < bgra.Length) set.Add((bgra[o] << 16) | (bgra[o + 1] << 8) | bgra[o + 2]);
            }
        return set.Count;
    }

    // ------------------------------------------------------------------------------------------------
    // Consoles

    private void Consoles(T t)
    {
        var all = new List<PluginInstance>();
        try
        {
            var p = lib.Create(); all.Add(p);
            var chips = Chips(p);
            t.Expect(chips.Count == 20 && chips[0] == "FIX", $"{chips.Count} chips, first {chips[0]} (expected the 17 NES chips with FIX first, then DMG, DMGS, SNES)");
            // each console starts with its own chip
            foreach (var (console, chip) in new[] { (1, "DMG"), (2, "SNES"), (0, "FIX") })
            {
                p.SetParam(PConsole, console);
                t.Expect(p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == chip && p.Test(14) == console, $"console {console}: chip {p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore))}, console {p.Test(14)}");
            }
            // picking a chip of another console switches the console
            p.SetParam(PCore, chips.IndexOf("DMGS"));
            t.Expect(p.GetParam(PConsole) == 1, $"picking DMGS left the console at {p.GetParam(PConsole)}");
            p.SetParam(PCore, chips.IndexOf("QN"));
            t.Expect(p.GetParam(PConsole) == 0, $"picking QN left the console at {p.GetParam(PConsole)}");
            // the console is shared by every instance of the emulator
            var q = lib.Create(); all.Add(q);
            q.SetParam(PConsole, 2);
            t.Expect(p.GetParam(PConsole) == 2 && p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == "SNES", "a console change did not reach the other instance");
            q.Destroy();
            p.SetParam(PConsole, 0);
        }
        finally { foreach (var p in all) p.Destroy(); }

        // every console plays in Direct mode, and a console switch under a held note keeps it sounding
        var table = new List<string>();
        foreach (var (console, name) in new[] { (0, "NES"), (1, "Game Boy"), (2, "SNES") })
        {
            var s = One(69, 0, 0.8);
            var (audio, mixer, plugin) = Play(s, setup: p => p.SetParam(PConsole, console));
            using (plugin)
            {
                var mono = Mono(audio);
                double rms = Audio.Rms(mono, mixer.Log[0].StartSample + 8000, mixer.Log[0].EndSample - 2000);
                double hz = SteadyHz(mono, 44100, mixer.Log[0]);
                double err = double.IsNaN(hz) ? double.NaN : Audio.Cents(hz, Nes.PeriodHz(Nes.Period(900, false), false));
                t.Expect(rms > 0.003, $"{name}: no sound in Direct mode (rms {rms:0.0000})");
                t.Warn(!double.IsNaN(err) && Math.Abs(err) < 8, $"{name}: pitch {(double.IsNaN(err) ? "unmeasurable" : err.ToString("+0.0;-0.0") + " cents")}");
                table.Add($"{name}:{(double.IsNaN(err) ? "n/a" : err.ToString("+0.0;-0.0"))}c");
            }
        }
        t.Note(string.Join(" ", table));
        var sw = new Scenario { Name = "console-swap", Tempo = 60, LengthBeats = 3 };
        sw.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 2.4, Key = 69, Channel = 0 });
        int swapAt = (int)(1.2 * 44100); bool done = false;
        var (a2, _, p2) = Play(sw, probe: (pl, sm) => { if (!done && sm >= swapAt) { pl.SetParam(PConsole, 1); done = true; } });
        using (p2)
        {
            double after = Audio.Rms(Mono(a2), swapAt + 12000, swapAt + 60000);
            t.Expect(after > 0.01, $"silent after switching the console under a held note (rms {after:0.0000})");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // ROM mode on Game Boy and SNES games (the plugin carries no such games: tiny ROMs are made here)

    /// <summary>A 32 KB Game Boy ROM with a valid header that does nothing forever (the Game Boy leaves its boot logo on the screen).</summary>
    internal static byte[] MakeGbRom(bool tone = false)
    {
        var rom = new byte[0x8000];
        byte[] logo = [0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D, 0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
                       0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99, 0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E];
        logo.CopyTo(rom, 0x104);
        rom[0x100] = 0x00; rom[0x101] = 0xC3; rom[0x102] = 0x50; rom[0x103] = 0x01;          // NOP; JP $0150
        System.Text.Encoding.ASCII.GetBytes("BN2TEST").CopyTo(rom, 0x134);
        byte sum = 0;
        for (int i = 0x134; i <= 0x14C; i++) sum = (byte)(sum - rom[i] - 1);
        rom[0x14D] = sum;
        rom[0x150] = 0x18; rom[0x151] = 0xFE;                                                 // JR -2
        if (tone)
        {
            // sound on, full volume, both speakers; pulse 1: 50% duty, volume 15 without envelope, period $700 = 512 Hz, trigger; then idle
            byte[] code = [0x3E, 0x80, 0xE0, 0x26, 0x3E, 0x77, 0xE0, 0x24, 0x3E, 0xFF, 0xE0, 0x25, 0x3E, 0x80, 0xE0, 0x11, 0x3E, 0xF0, 0xE0, 0x12,
                           0x3E, 0x00, 0xE0, 0x13, 0x3E, 0x87, 0xE0, 0x14,
                           // wave RAM: 16 bytes, half $FF half $00 (a square wave, the wave channel's instrument), then idle
                           0x21, 0x30, 0xFF, 0x06, 0x08, 0x3E, 0xFF, 0x22, 0x05, 0x20, 0xFC, 0x06, 0x08, 0xAF, 0x22, 0x05, 0x20, 0xFC, 0x18, 0xFE];
            code.CopyTo(rom, 0x150);
        }
        return rom;
    }

    /// <summary>A 32 KB LoROM SNES ROM that does nothing forever.</summary>
    internal static byte[] MakeSnesRom()
    {
        var rom = new byte[0x8000];
        rom[0] = 0x78; rom[1] = 0x18; rom[2] = 0xFB; rom[3] = 0x80; rom[4] = 0xFE;           // SEI; CLC; XCE; BRA -2
        System.Text.Encoding.ASCII.GetBytes("BN2 TEST             ").CopyTo(rom, 0x7FC0);
        rom[0x7FD5] = 0x20; rom[0x7FD7] = 0x05; rom[0x7FDA] = 0x33;
        rom[0x7FDC] = 0xFF; rom[0x7FDD] = 0xFF; rom[0x7FDE] = 0x00; rom[0x7FDF] = 0x00;       // complement and checksum add up to $FFFF
        rom[0x7FFC] = 0x00; rom[0x7FFD] = 0x80;                                               // reset vector $8000
        return rom;
    }

    /// <summary>Renders until the game of the given console is the one running (a console change loads its game in the background).</summary>
    private static bool WaitConsole(PluginInstance p, int console, double seconds = 25)
    {
        var until = Environment.TickCount64 + (long)(seconds * 1000);
        while (Environment.TickCount64 < until)
        {
            RenderBlocks(p, 0.1);
            if (p.Test(15) == console) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    private unsafe int LoadFileInto(PluginInstance p, string path)
    {
        var ptr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(path);
        try { return (int)p.Test(42, 0, ptr); }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
    }

    private void RomConsoles(T t)
    {
        string dir = Path.GetTempPath(), tag = Guid.NewGuid().ToString("N");
        string gb = Path.Combine(dir, $"bn2_{tag}.gb"), sfc = Path.Combine(dir, $"bn2_{tag}.sfc"), junk = Path.Combine(dir, $"bn2_{tag}.bin");
        File.WriteAllBytes(gb, MakeGbRom());
        File.WriteAllBytes(sfc, MakeSnesRom());
        File.WriteAllBytes(junk, Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray());
        try
        {
            List<string> chips;
            using (var probe = lib.Create()) chips = Chips(probe);
            foreach (var (console, name, path, wantW) in new[] { (1, "Game Boy", gb, 160), (2, "SNES", sfc, 256) })
            {
                // a project saved on that console, opened: its game loads from the saved path
                byte[] st;
                using (var seed = lib.Create()) { seed.SetParam(PConsole, console); seed.SetParam(PMode, 1); st = seed.SaveState(); }
                using var p = lib.Create();
                p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                p.LoadState(StateWithRom(st, path));
                t.Expect(WaitConsole(p, console), $"{name}: the game did not load");
                var audio = RenderBlocks(p, 1.5);
                t.Expect(p.Test(12) == 0, $"{name}: the game crashed");
                t.Expect(audio.All(float.IsFinite), $"{name}: non-finite audio");
                t.Expect(p.Test(11) >= 60, $"{name}: only {p.Test(11)} frames in 1.5 s");
                t.Expect(p.Test(10) != 0, $"{name}: no picture");
                t.Expect(p.Test(17) == 4, $"{name}: a game of this console plays through Mix, instance is on channel {p.Test(17)}");
                // its picture has the console's own size in the editor
                nint parent = Win32Host.CreateParent();
                try
                {
                    p.ShowEditor(parent);
                    for (int k = 0; k < 4; k++) { RenderBlocks(p, 0.1); Win32Host.Pump(60); }
                    long dims = p.Test(37);
                    t.Expect((int)(dims & 0xFFFF) == wantW, $"{name}: the picture is {(int)(dims & 0xFFFF)} x {(int)(dims >> 16)} wide, expected {wantW}");
                    // the channel strip: the tone channels are NES-only
                    t.Expect(p.Test(30, 10) == 0 && p.Test(30, 14) != 0, $"{name}: the channel strip should offer only Mix");
                    p.HideEditor();
                }
                finally { Win32Host.DestroyParent(parent); }
                // the sound chip switches under the running game (or the game restarts on it)
                int other = chips.IndexOf(console == 1 ? "DMGS" : "SNES");
                p.SetParam(PCore, other);
                RenderBlocks(p, 1.0);
                t.Expect(p.Test(12) == 0 && Math.Abs(p.Test(15) - console) == 0, $"{name}: switching the sound chip broke the game");
            }

            // files of any console: loading one switches the emulator to its console; a file that is not a ROM is refused
            using var q = RomPlugin();
            RenderBlocks(q, 0.3);
            t.Expect(q.Test(15) == 0, "the built-in game should be a NES game");
            t.Expect(LoadFileInto(q, gb) == 0 && WaitConsole(q, 1), "loading a .gb file into a NES emulator should switch it to Game Boy");
            t.Expect(q.GetParam(PConsole) == 1 && q.GetName(Fpn.ParamValue, PCore, q.GetParam(PCore)) == "DMG", "the console parameter and chip did not follow the loaded file");
            t.Expect(LoadFileInto(q, sfc) == 0 && WaitConsole(q, 2) && q.GetParam(PConsole) == 2, "loading a .sfc file should switch to SNES");
            int status = LoadFileInto(q, junk);
            RenderBlocks(q, 0.3);
            t.Expect(status != 0 && q.Test(15) == 2 && q.Test(12) == 0, "a file that is not a ROM should be refused and leave the running game alone");
            // back to the NES: the built-in game returns by itself
            q.SetParam(PConsole, 0);
            t.Expect(WaitConsole(q, 0) && q.GetName(Fpn.ParamValue, PCore, q.GetParam(PCore)) == "FIX", "switching back to the NES should load the built-in game");
            var back = RenderBlocks(q, 1.5);
            t.Expect(Audio.Rms(back, 24000, back.Length) > 0.003, "no sound after switching back to the NES");
            // the Game Boy game chosen earlier is remembered for that console
            q.SetParam(PConsole, 1);
            t.Expect(WaitConsole(q, 1) && q.Test(12) == 0, "the Game Boy game chosen earlier should come back when the console is chosen again");
        }
        finally { foreach (var f in new[] { gb, sfc, junk }) File.Delete(f); }
    }

    // ------------------------------------------------------------------------------------------------
    // The About window

    private void AboutWindow(T t)
    {
        nint parent = Win32Host.CreateParent();
        try
        {
            using var p = lib.Create();
            p.Test(40);                                  // forget everything: the first run on this machine
            p.ShowEditor(parent);
            Win32Host.Pump(150);
            var hwnd = p.EditorHandle;
            t.Expect(p.Test(36) == 1, "the About window must open by itself the first time");
            // modal: nothing behind it answers, and it has real text in it
            Win32Host.Click(hwnd, 742, 210);             // where the sound chip dropdown is
            t.Expect(p.Test(31) == 0 && p.Test(30, 4) == 0, "something behind the About window answered a click");
            var shot = Win32Host.CapturePixels(hwnd, out int w, out int h);
            int readFirst = BrightPixels(shot, w, 120, 130, 640, 200);
            t.Expect(readFirst > 500, $"the read-this-first text is missing ({readFirst} bright pixels)");
            int shout = BrightPixels(shot, w, 120, 390, 640, 130);
            t.Expect(shout > 250, $"the Plogue shoutout is missing or too faint ({shout} bright pixels in its panel)");
            t.Expect(p.Test(30, 70) != 0, "the Plogue link in the About text is not on screen / clickable (it is not clicked here: it would open a browser)");
            Win32Host.Wheel(hwnd, -3);                   // scrolling the text must be harmless
            Win32Host.Wheel(hwnd, 5);
            t.Expect(p.Test(36) == 1, "the About window closed by itself");
            ClickControl(t, p, 41, "Close button");
            t.Expect(p.Test(36) == 0 && p.Test(30, 4) != 0, "Close did not close the About window");
            // the About button opens it again
            ClickControl(t, p, 40, "About button");
            t.Expect(p.Test(36) == 1, "the About button did not open the window");
            ClickControl(t, p, 41, "Close button");
            p.HideEditor();

            // a second instance in the same FL session does not open it again
            using var q = lib.Create();
            q.ShowEditor(parent);
            Win32Host.Pump(100);
            t.Expect(q.Test(36) == 0, "another instance's editor opened the About window again");
            q.HideEditor();
            // a new FL session on a machine that has seen it: not shown
            q.Test(41);
            using var r = lib.Create();
            r.ShowEditor(parent);
            Win32Host.Pump(100);
            t.Expect(r.Test(36) == 0, "the About window opened although this machine has seen it");
            r.HideEditor();
            // ... but a machine that never has: shown
            r.Test(40);
            using var u = lib.Create();
            u.ShowEditor(parent);
            Win32Host.Pump(100);
            t.Expect(u.Test(36) == 1, "the About window did not open on a machine that has not seen it");
            u.HideEditor();
        }
        finally
        {
            Win32Host.DestroyParent(parent);
            File.WriteAllText(AboutFile, "AboutRevisionSeen=99\n");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Reset Console

    private void ResetConsole(T t)
    {
        // Direct mode: two instances hold notes (as if jammed); a reset from one clears both, and they work again
        var all = new List<PluginInstance>();
        try
        {
            var a = lib.Create(); all.Add(a);
            var b = lib.Create(); all.Add(b);
            long emuBefore = a.Test(16);
            var buf = new float[1024];
            foreach (var inst in all)
            {
                var v = inst.Host.NewVoice(0, 0.8f);
                v.Params->FinalLevels.Vol = 0.8f;
                v.Handle = inst.TriggerVoice(v.Params, v.Tag);
            }
            double Block(PluginInstance inst, int blocks = 20)
            {
                double sum = 0; int n = 0;
                fixed (float* f = buf)
                    for (int i = 0; i < blocks; i++) { inst.NewTick(); inst.GenRender(f, 512); for (int k = 0; k < 1024; k++) { sum += buf[k] * buf[k]; n++; } }
                return Math.Sqrt(sum / n);
            }
            double held = Block(a);
            Block(b);
            t.Expect(held > 0.01, $"a held note should sound before the reset (rms {held:0.0000})");
            t.Expect(a.Test(3) == 2 && a.Test(6, 2) != 0, $"before the reset: {a.Test(3)} voices held, period register {a.Test(6, 2)}");

            a.Test(43);                       // Reset Console, as the button does
            double after = Block(a, 4);        // the next blocks carry the reset out
            Block(b, 4);
            t.Expect(a.Test(3) == 0, $"the reset left {a.Test(3)} voices held (a jammed note must end)");
            t.Expect(a.Host.VoiceKillCalls == 1 && b.Host.VoiceKillCalls == 1, $"FL should get both voices back with Voice_Kill ({a.Host.VoiceKillCalls}, {b.Host.VoiceKillCalls})");
            t.Expect(after < 1e-3, $"the chip still sounds after the reset (rms {after:0.0000})");
            t.Expect(a.Test(6, 2) == 0, "the chip was not rebuilt (its registers still hold the old note)");
            t.Expect(a.Test(16) == emuBefore && a.Test(19) == 2 && a.Test(17) == 0 && b.Test(17) == 1, "the reset must not move instances or change the emulator");
            // and notes play again
            var m = new Mixer { SampleRate = 44100 };
            var audio = Mono(m.Run(a, One(69, 0, 0.6)));
            t.Expect(Audio.Rms(audio, 8000, audio.Length - 8000) > 0.01, "an instance does not play after a reset");
        }
        finally { foreach (var p in all) p.Destroy(); }

        // ROM mode: the game restarts from power-on, for every instance listening; sound goes on
        var rack = new List<PluginInstance>();
        try
        {
            var first = RomPlugin(); rack.Add(first);
            var second = lib.Create(); second.Dispatcher(Fpd.SetSampleRate, 0, 48000); rack.Add(second);
            RenderBlocks(first, 3.0);
            long framesBefore = first.Test(11);
            t.Expect(framesBefore > 150, $"the game ran only {framesBefore} frames before the reset");
            first.Test(43);
            bool restarted = false;
            for (int k = 0; k < 200 && !restarted; k++) { RenderBlocks(first, 0.1); restarted = first.Test(11) < framesBefore; }
            t.Expect(restarted, $"the game did not restart ({first.Test(11)} frames, {framesBefore} before)");
            var a2 = RenderBlocks(first, 1.5);
            t.Expect(first.Test(12) == 0 && Audio.Rms(a2, 4800, a2.Length) > 0.003, "no sound or a crash after the game restarted");
            t.Expect(second.Test(11) == first.Test(11) && second.Test(16) == first.Test(16), "the other instance on the emulator does not see the restarted game");
        }
        finally { foreach (var p in rack) p.Destroy(); }
    }

    // ------------------------------------------------------------------------------------------------
    // Game sound reaches FL, and Instrument Runaway on Game Boy and SNES

    private PluginInstance OpenGame(string path, int console, int channelParam = 0, int rate = 48000)
    {
        byte[] st;
        using (var seed = lib.Create())
        {
            seed.SetParam(PConsole, console);
            seed.SetParam(PMode, 1);
            if (channelParam != 0) seed.SetParam(PChannel, channelParam);
            st = seed.SaveState();
        }
        var p = lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, rate);
        p.LoadState(StateWithRom(st, path));
        if (!WaitConsole(p, console)) throw new Exception($"the console {console} game did not load");
        return p;
    }

    private void RomAudio(T t)
    {
        string dir = Path.GetTempPath(), tag = Guid.NewGuid().ToString("N");
        string gb = Path.Combine(dir, $"bn2_{tag}.gb"), sfc = Path.Combine(dir, $"bn2_{tag}.sfc");
        File.WriteAllBytes(gb, MakeGbRom(tone: true));
        File.WriteAllBytes(sfc, MakeSnesRom());
        try
        {
            // Game Boy: a 512 Hz tone from the game's own sound unit, to the instance that hosts it, on whichever channel that instance had
            foreach (int channelParam in new[] { 0, 1, 3, 4 })
            {
                using var g = OpenGame(gb, 1, channelParam);
                var a = RenderBlocks(g, 1.5);
                double rms = Audio.Rms(a, 12000, a.Length);
                t.Expect(g.Test(17) == 4, $"Game Boy game, Channel param {channelParam}: the instance should host the whole sound (Mix), it sits on channel {g.Test(17)}");
                t.Expect(rms > 0.01, $"Game Boy game, Channel param {channelParam}: no sound reached the instance (rms {rms:0.0000})");
                if (channelParam == 0) t.NearCents("Game Boy tone", Audio.Frequency(a, 48000, 12000, a.Length - 2000), 512, 40);
            }
            // SNES: a tone on the game's sound unit
            foreach (int channelParam in new[] { 0, 2 })
            {
                using var g = OpenGame(sfc, 2, channelParam);
                RenderBlocks(g, 0.3);
                g.Test(51);                          // the game's sound driver plays a sample
                var a = RenderBlocks(g, 1.5);
                double rms = Audio.Rms(a, 12000, a.Length);
                t.Expect(g.Test(17) == 4, $"SNES game, Channel param {channelParam}: the instance should host the whole sound (Mix), it sits on channel {g.Test(17)}");
                t.Expect(rms > 0.005, $"SNES game, Channel param {channelParam}: no sound reached the instance (rms {rms:0.0000})");
                if (channelParam == 0) t.NearCents("SNES tone", Audio.Frequency(a, 48000, 12000, a.Length - 2000), 2000, 60);
            }
            // a Direct-mode instance (pulse 1) that then loads a Game Boy file: the file's sound comes out of that same instance
            using var d = lib.Create();
            d.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            t.Expect(LoadFileInto(d, gb) == 0 && WaitConsole(d, 1), "loading the Game Boy file did not work");
            var ad = RenderBlocks(d, 1.5);
            t.Expect(Audio.Rms(ad, 12000, ad.Length) > 0.01, "a Direct-mode instance that loaded a Game Boy file is silent");
        }
        finally { File.Delete(gb); File.Delete(sfc); }
    }

    private void RunawayGb(T t)
    {
        string gb = Path.Combine(Path.GetTempPath(), $"bn2_{Guid.NewGuid():N}.gb");
        File.WriteAllBytes(gb, MakeGbRom(tone: true));
        try
        {
            using var p = OpenGame(gb, 1);
            RenderBlocks(p, 0.5);
            p.Test(47, 0, 1);
            t.Expect(p.Test(45) == 1 && p.Test(50) == 1, "Runaway on a Game Boy game should capture its sound settings (Game Boy sampler mode)");
            t.Expect(p.Test(48) == 0, "a Game Boy game must not be in SNES sampler mode");
            t.Expect(p.Test(17) == 5, $"in Game Boy Runaway the instance sits on the Sampler channel, not {p.Test(17)}");
            var frozen = RenderBlocks(p, 1.0);
            t.Expect(Audio.Peak(frozen, 0, frozen.Length) < 1e-3, "the frozen game's own tone should be silent");

            // FL notes play the Game Boy's pulse 1 at the right pitch (A5 = 440 Hz, A6 = 880 Hz)
            foreach (var (key, hz) in new[] { (69.0, 440.0), (81.0, 880.0), (57.0, 220.0) })
            {
                var m = new Mixer { SampleRate = 48000 };
                var audio = Mono(m.Run(p, One(key, 0, 0.5)));
                double f = Audio.Frequency(audio, 48000, m.Log[0].StartSample + 6000, m.Log[0].EndSample - 3000);
                t.NearCents($"Game Boy pulse 1 key {key}", f, hz, 25);
                t.Expect(Audio.Rms(audio, m.Log[0].StartSample + 6000, m.Log[0].EndSample - 3000) > 0.01, $"key {key}: no sound");
            }
            // the other channels: pulse 2, wave (the game's own waveform from its wave RAM), noise
            foreach (var (param, name, mustSound) in new[] { (2, "pulse 2", true), (3, "wave", true), (4, "noise", true) })
            {
                p.SetParam(PChannel, param);
                var m = new Mixer { SampleRate = 48000 };
                var audio = Mono(m.Run(p, One(60, 0, 0.5)));
                t.Expect(audio.All(float.IsFinite), $"{name}: non-finite audio");
                if (mustSound) t.Expect(Audio.Rms(audio, m.Log[0].StartSample + 6000, m.Log[0].EndSample - 3000) > 0.005, $"Game Boy {name}: no sound");
            }
            // the wave channel plays the game's waveform at the right pitch (A5 = 440 Hz)
            p.SetParam(PChannel, 3);
            var mw = new Mixer { SampleRate = 48000 };
            var aw = Mono(mw.Run(p, One(69, 0, 0.5)));
            t.NearCents("Game Boy wave key 69", Audio.Frequency(aw, 48000, mw.Log[0].StartSample + 6000, mw.Log[0].EndSample - 3000), 440, 25);
            // the duty can be the game's or chosen
            p.SetParam(PChannel, 1);
            p.SetParam(PInstrument, 3);
            var d = Mono(new Mixer { SampleRate = 48000 }.Run(p, One(69, 0, 0.4)));
            t.Expect(Audio.Rms(d, 8000, d.Length - 4000) > 0.01, "a chosen duty should still sound");
            p.SetParam(PInstrument, 0);
            t.Expect(p.Test(3) == 0, "voices were not handed back");

            // the editor: the Channel dropdown lists the Game Boy's channels and the Duty dropdown belongs to the pulses
            p.SetParam(PChannel, 0);
            nint parent = Win32Host.CreateParent();
            try
            {
                p.ShowEditor(parent);
                for (int k = 0; k < 4; k++) { RenderBlocks(p, 0.1); Win32Host.Pump(40); }
                var hwnd = p.EditorHandle;
                void Pick(int dropdown, int item, string what)
                {
                    if (!ClickControl(t, p, dropdown, what)) return;
                    var c = Centre(p, 100 + item);
                    if (c != null) Win32Host.Click(hwnd, c.Value.X, c.Value.Y);
                    else t.Expect(false, $"{what}: no item {item}");
                }
                t.Expect(p.Test(30, 90) == 0 && p.Test(30, 91) != 0, "a Game Boy game in Runaway should show the Duty dropdown (no SNES instrument picker)");
                ClickControl(t, p, 9, "channel dropdown");
                t.Expect(p.Test(31) == 9 && p.Test(32) == 5, $"the channel dropdown lists {p.Test(32)} entries, expected Auto, Pulse 1, Pulse 2, Wave, Noise");
                Win32Host.Click(hwnd, 1, 1);                                  // closes it again
                Pick(9, 3, "channel dropdown (Wave)");
                t.Expect(p.GetParam(PChannel) == 3, $"picking Wave left the Channel at {p.GetParam(PChannel)}");
                t.Expect(p.Test(30, 91) == 0, "the wave channel has no duty: the Duty dropdown should be gone");
                Pick(9, 4, "channel dropdown (Noise)");
                t.Expect(p.GetParam(PChannel) == 4 && p.Test(30, 91) == 0, "Noise should be picked and have no duty either");
                Pick(9, 2, "channel dropdown (Pulse 2)");
                t.Expect(p.GetParam(PChannel) == 2 && p.Test(30, 91) != 0, "Pulse 2 should be picked and have a Duty dropdown");
                ClickControl(t, p, 91, "duty dropdown");
                t.Expect(p.Test(31) == 91 && p.Test(32) == 5, $"the duty dropdown lists {p.Test(32)} entries, expected the game's, 12.5, 25, 50, 75");
                Win32Host.Click(hwnd, 1, 1);
                Pick(91, 4, "duty dropdown (75%)");
                t.Expect(p.GetParam(PInstrument) == 4, $"picking 75% left the duty at {p.GetParam(PInstrument)}");
                p.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }
            p.SetParam(PChannel, 0);
            p.SetParam(PInstrument, 0);

            // a project saved in Runaway comes back with the game's settings
            byte[] saved = p.SaveState();
            p.Dispose();                                 // the project is closed and opened again
            using var q = lib.Create();
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.LoadState(saved);
            t.Expect(q.Test(45) == 1 && q.Test(50) == 1, "the saved project did not come back in Game Boy Runaway");
            var mq = new Mixer { SampleRate = 48000 };
            var aq = Mono(mq.Run(q, One(69, 0, 0.5)));
            t.NearCents("restored Game Boy pulse 1", Audio.Frequency(aq, 48000, mq.Log[0].StartSample + 6000, mq.Log[0].EndSample - 3000), 440, 25);
            // releasing hands the whole game back
            q.Test(47, 0, 0);
            var back = RenderBlocks(q, 1.5);
            t.Expect(q.Test(45) == 0 && q.Test(50) == 0 && q.Test(17) == 4, $"after releasing: runaway {q.Test(45)}, gb sampler {q.Test(50)}, channel {q.Test(17)} (p on {p.Test(17)}), mode {q.Test(21)}, console {q.Test(14)}, emulators {q.Test(16)}/{p.Test(16)}, problem {q.Test(20)}");
        }
        finally { File.Delete(gb); }
    }

    private void RunawaySnesPicker(T t)
    {
        string sfc = Path.Combine(Path.GetTempPath(), $"bn2_{Guid.NewGuid():N}.sfc");
        File.WriteAllBytes(sfc, MakeSnesRom());
        try
        {
            using var p = OpenGame(sfc, 2, rate: 44100);
            RenderBlocks(p, 0.3, 44100);
            p.Test(51);                                  // the game's sound driver loads a sample and plays it
            RenderBlocks(p, 0.3, 44100);
            p.Test(47, 0, 1);
            t.Expect(p.Test(45) == 1 && p.Test(48) == 1, "Runaway on a SNES game should capture its sound memory");
            nint parent = Win32Host.CreateParent();
            try
            {
                p.ShowEditor(parent);
                for (int k = 0; k < 4; k++) { RenderBlocks(p, 0.1, 44100); Win32Host.Pump(40); }
                var hwnd = p.EditorHandle;
                // the picker replaces the slider and lists what the game has loaded: here the one sample the driver put in memory
                t.Expect(p.Test(30, 90) != 0, "no instrument picker while a SNES game is frozen");
                t.Expect(p.Test(30, 31) == 0, "the raw Instrument slider should be gone (the picker replaced it)");
                ClickControl(t, p, 90, "instrument picker");
                t.Expect(p.Test(31) == 90 && p.Test(32) == 1, $"the picker lists {p.Test(32)} instruments, expected the game's 1");
                var first = Centre(p, 100);
                if (first != null) Win32Host.Click(hwnd, first.Value.X, first.Value.Y);
                t.Expect(p.GetParam(PInstrument) == 0, "picking the instrument did not set it");
                p.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }
            // the picked instrument is what plays: the game's own sample (a 2 kHz square wave at pitch $1000)
            var m = new Mixer { SampleRate = 44100 };
            var audio = Mono(m.Run(p, One(60, 0, 0.5)));
            t.NearCents("picked instrument", Audio.Frequency(audio, 44100, m.Log[0].StartSample + 6000, m.Log[0].EndSample - 3000), 2000, 10);
            // a slot the game has nothing in (a new instance starts on slot 0, which many games do not use) plays the game's instrument instead of nothing
            p.SetParam(PInstrument, 7);
            var other = new Mixer { SampleRate = 44100 };
            var again = Mono(other.Run(p, One(60, 0, 0.5)));
            t.NearCents("unused slot falls back to the game's instrument", Audio.Frequency(again, 44100, other.Log[0].StartSample + 6000, other.Log[0].EndSample - 3000), 2000, 10);
        }
        finally { File.Delete(sfc); }
    }

    // ------------------------------------------------------------------------------------------------
    // Inputs

    private string PrefsText() => File.Exists(AboutFile) ? File.ReadAllText(AboutFile) : "";

    private void InputsTest(T t)
    {
        const int Start = 1 << 10, A = 1 << 4;
        // the pad reaches the NES game: pressing Start on VRUN's title screen changes the picture, against an identical run that presses nothing
        long Run(bool press)
        {
            using var p = RomPlugin();
            RenderBlocks(p, 2.0);
            if (press) p.Test(44, 0, Start);
            RenderBlocks(p, 0.3);
            if (press) p.Test(44, 0, 0);
            RenderBlocks(p, 1.0);
            return p.Test(10);
        }
        long idle = Run(false), pressed = Run(true), idle2 = Run(false);
        t.Expect(idle == idle2 && idle != 0, "the game is not deterministic between runs, so a pad test is meaningless");
        t.Expect(pressed != idle, "pressing Start made no difference to the game (the pad does not reach it)");

        // Game Boy and SNES games take the pad too (their test ROMs do nothing, so only "no crash, still running" can be seen)
        string dir = Path.GetTempPath(), tag = Guid.NewGuid().ToString("N");
        string gb = Path.Combine(dir, $"bn2_{tag}.gb"), sfc = Path.Combine(dir, $"bn2_{tag}.sfc");
        File.WriteAllBytes(gb, MakeGbRom()); File.WriteAllBytes(sfc, MakeSnesRom());
        try
        {
            foreach (var (console, path) in new[] { (1, gb), (2, sfc) })
            {
                byte[] st;
                using (var seed = lib.Create()) { seed.SetParam(PConsole, console); seed.SetParam(PMode, 1); st = seed.SaveState(); }
                using var g = lib.Create();
                g.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                g.LoadState(StateWithRom(st, path));
                WaitConsole(g, console);
                g.Test(44, 0, 0xFFF);
                RenderBlocks(g, 0.5);
                g.Test(44, 0, 0);
                RenderBlocks(g, 0.3);
                t.Expect(g.Test(12) == 0 && g.Test(11) > 20, $"console {console}: the game stopped after pad input");
            }
        }
        finally { File.Delete(gb); File.Delete(sfc); }

        // the Inputs window
        using var q = RomPlugin();
        nint parent = Win32Host.CreateParent();
        try
        {
            q.ShowEditor(parent);
            for (int k = 0; k < 3; k++) { RenderBlocks(q, 0.1); Win32Host.Pump(40); }
            var hwnd = q.EditorHandle;
            t.Expect(q.Test(30, 80) != 0, "no Inputs button");
            ClickControl(t, q, 80, "Inputs button");
            t.Expect(q.Test(38) == 1, "the Inputs window did not open");
            t.Expect(q.Test(30, 4) == 0, "controls behind the Inputs window are still reachable");
            int rows = Enumerable.Range(0, 12).Count(i => q.Test(30, 200 + i) != 0 && q.Test(30, 220 + i) != 0);
            t.Expect(rows == 12, $"{rows} of 12 button rows are on screen");
            var shot = Win32Host.CapturePixels(hwnd, out int w, out int h);
            t.Expect(BrightPixels(shot, w, 90, 330, 400, 340) > 400, "the Inputs window shows no binding text");

            // the on-screen pad: holding a button with the mouse presses it, letting go releases it
            var a = RectOf(q.Test(30, 244));
            Win32Host.MouseMove(hwnd, a.X + a.W / 2, a.Y + a.H / 2);
            Win32Host.MouseDown(hwnd, a.X + a.W / 2, a.Y + a.H / 2);
            Win32Host.Pump(120);
            t.Expect((q.Test(39) & A) != 0 && (q.Test(49) & A) != 0, $"the on-screen A button did not press A (live {q.Test(39):X}, emulator {q.Test(49):X})");
            Win32Host.MouseUp(hwnd, a.X + a.W / 2, a.Y + a.H / 2);
            Win32Host.Pump(120);
            t.Expect(q.Test(49) == 0, $"the pad stayed pressed after the mouse was released ({q.Test(49):X})");

            // switching a device off is remembered; right-click clears a binding; defaults come back
            ClickControl(t, q, 83, "Keyboard toggle");
            t.Expect(PrefsText().Contains("InputKeyboard=0"), "turning the keyboard off was not remembered");
            ClickControl(t, q, 83, "Keyboard toggle");
            t.Expect(PrefsText().Contains("InputKeyboard=1"), "turning the keyboard on was not remembered");
            var kc = RectOf(q.Test(30, 200 + 4));
            Win32Host.RightClick(hwnd, kc.X + kc.W / 2, kc.Y + kc.H / 2);
            t.Expect(PrefsText().Contains("InputKeys=38,40,37,39,0,"), $"clearing the A key was not remembered: {PrefsText().Replace("\n", " ")}");
            ClickControl(t, q, 85, "Reset to defaults");
            t.Expect(PrefsText().Contains("InputKeys=38,40,37,39,88,90,83,65,81,87,13,161"), "the defaults did not come back");
            // clicking a binding starts waiting for a key; Close ends the Inputs window and the waiting
            ClickControl(t, q, 200 + 5, "B key cell");
            Win32Host.Pump(60);
            ClickControl(t, q, 82, "Close button");
            t.Expect(q.Test(38) == 0 && q.Test(30, 4) != 0, "the Inputs window did not close");
            q.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); }
    }

    // ------------------------------------------------------------------------------------------------
    // Instrument Runaway

    private void RunawayTest(T t)
    {
        // NES: the game freezes (frames, picture) and goes silent; releasing it carries on
        using (var p = RomPlugin())
        {
            RenderBlocks(p, 2.0);
            long frames = p.Test(11), hash = p.Test(10);
            p.Test(47, 0, 1);
            t.Expect(p.Test(45) == 1, "Runaway did not start");
            t.Expect(p.Test(48) == 0, "a NES game should not be in sampler mode");
            var frozen = RenderBlocks(p, 1.5);
            t.Expect(p.Test(11) == frames && p.Test(10) == hash, $"the game is not frozen ({p.Test(11)} frames, was {frames})");
            t.Expect(Audio.Peak(frozen, 0, frozen.Length) < 1e-4, "a frozen game still makes sound");
            p.Test(47, 0, 0);
            t.Expect(p.Test(45) == 0, "Runaway did not end");
            var back = RenderBlocks(p, 1.5);
            t.Expect(p.Test(11) > frames + 60 && Audio.Rms(back, 4800, back.Length) > 0.003, "the game did not carry on after Runaway");
            // Reset Console also ends Runaway
            p.Test(47, 0, 1);
            p.Test(43);
            RenderBlocks(p, 1.0);
            t.Expect(p.Test(45) == 0, "Reset Console should end Runaway");
        }
        // Runaway needs ROM mode
        using (var d = lib.Create())
        {
            d.Test(47, 0, 1);
            t.Expect(d.Test(45) == 0, "Runaway started in Direct mode");
        }

        // SNES: the sound memory is captured and FL notes play the game's samples
        string sfc = Path.Combine(Path.GetTempPath(), $"bn2_{Guid.NewGuid():N}.sfc");
        File.WriteAllBytes(sfc, MakeSnesRom());
        try
        {
            byte[] st;
            using (var seed = lib.Create()) { seed.SetParam(PConsole, 2); seed.SetParam(PMode, 1); st = seed.SaveState(); }
            byte[] saved;
            using (var p = lib.Create())
            {
                p.Dispatcher(Fpd.SetSampleRate, 0, 44100);
                p.LoadState(StateWithRom(st, sfc));
                t.Expect(WaitConsole(p, 2), "the SNES test game did not load");
                RenderBlocks(p, 0.5, 44100);
                p.Test(47, 0, 1);
                t.Expect(p.Test(45) == 1 && p.Test(48) == 1, "Runaway on a SNES game should capture its sound memory (sampler mode)");
                t.Expect(p.Test(17) == 5, $"in sampler mode the instance should sit on the Sampler channel, not {p.Test(17)}");
                p.Test(46);                                 // a made-up sound memory with one square-wave sample (the test ROM has none)
                // notes: C5 plays the sample at its own rate (2 kHz), an octave up doubles it
                foreach (var (key, hz) in new[] { (60.0, 2000.0), (72.0, 4000.0), (48.0, 1000.0) })
                {
                    var m = new Mixer { SampleRate = 44100 };
                    var audio = Mono(m.Run(p, One(key, 0, 0.5)));
                    double f = Audio.Frequency(audio, 44100, m.Log[0].StartSample + 6000, m.Log[0].EndSample - 3000);
                    t.NearCents($"sampler key {key}", f, hz, 8);
                    t.Expect(Audio.Rms(audio, m.Log[0].StartSample + 6000, m.Log[0].EndSample - 3000) > 0.02, $"key {key}: no sound");
                }
                // three notes at once
                var poly = new Scenario { Name = "poly", Tempo = 60, LengthBeats = 1.5 };
                foreach (var k in new[] { 60.0, 64.0, 67.0 }) poly.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1, Key = k, Channel = 0 });
                var one = new Mixer { SampleRate = 44100 };
                var single = Mono(one.Run(p, One(60, 0, 1.0)));
                var mx = new Mixer { SampleRate = 44100 };
                var chord = Mono(mx.Run(p, poly));
                double r1 = Audio.Rms(single, 8000, 30000), r3 = Audio.Rms(chord, 8000, 30000);
                t.Expect(r3 > r1 * 1.3, $"a chord should be louder than one note ({r3:0.000} vs {r1:0.000}): not polyphonic?");
                t.Expect(p.Host.VoiceKillCalls == p.Host.VoiceKillCalls && p.Test(3) == 0, "voices were not handed back");
                // an instrument slot with nothing in it is harmless: the instance plays the game's own instrument instead
                p.SetParam(PInstrument, 9);
                var empty = Mono(new Mixer { SampleRate = 44100 }.Run(p, One(60, 0, 0.4)));
                t.Expect(empty.All(float.IsFinite) && Audio.Peak(empty, 0, empty.Length) > 0.02, "an unused instrument slot should play the game's instrument, not nothing");
                p.SetParam(PInstrument, 0);
                saved = p.SaveState();
            }

            // the project remembers it: the game comes back frozen with its instruments, and plays
            using (var q = lib.Create())
            {
                q.Dispatcher(Fpd.SetSampleRate, 0, 44100);
                q.LoadState(saved);
                t.Expect(q.Test(45) == 1 && q.Test(48) == 1, "the saved project did not come back in Instrument Runaway");
                t.Expect(q.GetParam(PConsole) == 2 && q.GetParam(PMode) == 1, "console/mode not restored");
                var m = new Mixer { SampleRate = 44100 };
                var audio = Mono(m.Run(q, One(60, 0, 0.5)));
                t.NearCents("restored sampler key 60", Audio.Frequency(audio, 44100, m.Log[0].StartSample + 6000, m.Log[0].EndSample - 3000), 2000, 8);
                // releasing hands the game back its channels
                q.Test(47, 0, 0);
                RenderBlocks(q, 0.3, 44100);
                t.Expect(q.Test(45) == 0 && q.Test(48) == 0 && q.Test(17) == 4, $"after releasing: runaway {q.Test(45)}, sampler {q.Test(48)}, channel {q.Test(17)}");
            }
        }
        finally { File.Delete(sfc); }

        // the editor: the buttons and the banner
        using var e = RomPlugin();
        nint parent = Win32Host.CreateParent();
        try
        {
            e.ShowEditor(parent);
            for (int k = 0; k < 3; k++) { RenderBlocks(e, 0.1); Win32Host.Pump(40); }
            t.Expect(e.Test(30, 81) != 0, "no Instrument Runaway button in ROM mode");
            ClickControl(t, e, 81, "Instrument Runaway button");
            t.Expect(e.Test(45) == 1, "the button did not start Runaway");
            RenderBlocks(e, 0.2); Win32Host.Pump(60);
            ClickControl(t, e, 81, "Runaway release");
            t.Expect(e.Test(45) == 0, "the button did not release Runaway");
            e.SetParam(PMode, 0);
            Win32Host.Pump(60);
            t.Expect(e.Test(30, 81) == 0, "the Runaway button should be unavailable in Direct mode");
            e.HideEditor();
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
        var threads = new[] { "FIX", "QN", "DMG" }.Select((chip, n) => new Thread(() =>
        {
            try
            {
                var s = One(69, 0, 0.8);
                var (audio, mixer, plugin) = Play(s, setup: p => SetChip(p, chip), emulator: n + 1);   // three emulators, one per thread
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
        t.Expect(text.Contains("CreatePlugInstance Bogue :: BrokenNes 2"), "log has no CreatePlugInstance line");
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
