using System;
using System.IO;
using System.Text.Json;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// Butchering mode in the headless runners (--romtest / --session-test): <c>--butcher &lt;settings.json&gt; [--butcher-at N]</c>
/// applies a Butchering-mode settings file (the panel's own JSON, camelCase) at frame N (default 60), so UAT/cases/butcher.json
/// can hold golden hashes for a fixed set of effects per console, and an all-off file must reproduce the plain run exactly.
/// </summary>
public static class ButcherCli
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static (ButcherSettings? settings, int at) Parse(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--butcher", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return (null, -1);
        var s = JsonSerializer.Deserialize<ButcherSettings>(File.ReadAllText(args[i + 1]), Json) ?? throw new InvalidDataException("empty Butchering settings");
        int j = Array.FindIndex(args, a => a.Equals("--butcher-at", StringComparison.OrdinalIgnoreCase));
        int at = j >= 0 && j + 1 < args.Length ? int.Parse(args[j + 1]) : 60;
        return (s, at);
    }

    /// <summary>
    /// <c>--butcher-soundcheck --out &lt;dir&gt; [--nes rom] [--gb rom] [--sms rom] [--gg rom] [--md rom] [--snes rom] [--seconds 8]</c>:
    /// per console a clean clip and one clip per sound effect (each alone, strong), as WAV files, for a quick listening test.
    /// Every clip starts from the same frame of the same game with Start pressed early, so the music is comparable.
    /// </summary>
    public static int SoundCheck(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string outDir = Opt("out", "butcher-soundcheck"); Directory.CreateDirectory(outDir);
        int seconds = int.Parse(Opt("seconds", "8"));
        PinGroupSettings P(int lines, int line, int kind, int data = 8, int dline = -1, int dkind = 0, int flicker = 60) { var g = new PinGroupSettings { Address = new int[lines], Data = new int[data], Flicker = flicker }; if (line >= 0 && line < lines) g.Address[line] = kind; if (dline >= 0) g.Data[dline] = dkind; return g; }
        var effects = new (string name, Action<ButcherSettings> on)[] {
            ("clean", _ => { }),
            ("bit-flips", x => { x.Sound.BitFlip.Bits = new[] { true, true, true, true, true }; x.Sound.BitFlip.Rate = 70; }),
            ("detune-1", x => { x.Sound.Nudge.On = true; x.Sound.Nudge.Rate = 80; }),
            ("drop-outs", x => { x.Sound.Drops.Channels = Enumerable16(true); x.Sound.Drops.Rate = 70; x.Sound.Drops.Duration = 20; }),
            ("stutter", x => { x.Sound.Stutter.On = true; x.Sound.Stutter.Rate = 80; x.Sound.Stutter.Duration = 60; }),
            ("drone", x => { x.Sound.Drone.On = true; x.Sound.Drone.Rate = 70; x.Sound.Drone.Duration = 40; }),
            ("duty-force", x => { x.Sound.Duty.On = true; x.Sound.Duty.Rate = 80; x.Sound.Duty.Duration = 60; x.Sound.Duty.Value = 3; }),
            ("detune-xor", x => { x.Sound.Detune.On = true; x.Sound.Detune.Rate = 70; x.Sound.Detune.Duration = 40; x.Sound.Detune.Value = 5; }),
            ("register-bus-pins", x => x.Sound.BusPins = P(8, 1, 1, 8, 6, 3)),
            ("sample-pins", x => x.Sound.SamplePins = P(16, 3, 1, 8, 2, 2)),
            ("dac-pins", x => x.Sound.DacPins = P(0, -1, 0, 16, 13, 3, 0)),
            ("clock-wobble", x => { x.Sound.Wobble.On = true; x.Sound.Wobble.Depth = 50; x.Sound.Wobble.Speed = 10; }),
            ("dried-out-caps", x => { x.Sound.Caps.On = true; x.Sound.Caps.Age = 75; }),
            ("mains-hum", x => { x.Sound.Hum.On = true; x.Sound.Hum.Level = 25; }),
            ("tone-ring-mod", x => { x.Sound.Inject.On = true; x.Sound.Inject.Mix = 1; x.Sound.Inject.Freq = 220; x.Sound.Inject.Level = 70; }),
            ("test-sweep", x => { x.Sound.Sweep.On = true; x.Sound.Sweep.Level = 20; }),
            ("sound-ram-rot", x => { x.Unsafe.SoundRamRot.On = true; x.Unsafe.SoundRamRot.Rate = 60; x.Unsafe.SoundRamRot.Bits = 4; }),
            ("echo-abuse", x => { x.Unsafe.EchoAbuse.On = true; x.Unsafe.EchoAbuse.Rate = 100; x.Unsafe.EchoAbuse.Duration = 600; }),
        };
        var index = new System.Text.StringBuilder("# Butchering mode sound self-check\n\nEach clip: the same game from the same frame, one sound effect alone (strong). Compare with `clean`.\n\n");
        foreach (var console in new[] { "nes", "gb", "sms", "gg", "md", "snes" })
        {
            string rom = Opt(console, ""); if (rom == "" || !File.Exists(rom)) continue;
            index.Append($"## {console}: {Path.GetFileName(rom)}\n\n");
            foreach (var (name, on) in effects)
            {
                if (name == "duty-force" && console != "nes" && console != "gb") continue;   // only pulse channels with a duty
                if (name == "echo-abuse" && console != "snes") continue;
                if (name == "sample-pins" && console is "sms" or "gg") continue;   // the PSG has no sample memory
                var b = new ButcherSettings { Enabled = true }; on(b);
                var (pcm, rate, ch) = Render(console, rom, name == "clean" ? null : b, seconds);
                string file = Path.Combine(outDir, $"{console}-{name}.wav");
                WriteWav(file, pcm, rate, ch);
                index.Append($"- `{Path.GetFileName(file)}`\n");
            }
            index.Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, "README.md"), index.ToString());
        Console.WriteLine($"written to {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static bool[] Enumerable16(bool v) { var a = new bool[16]; Array.Fill(a, v); return a; }

    private static (short[] pcm, int rate, int channels) Render(string console, string rom, ButcherSettings? s, int seconds)
    {
        const int warm = 240, applyAt = 300;
        var pcm = new System.Collections.Generic.List<short>();
        if (console == "nes")
        {
            var n = new NES(); n.LoadROM(File.ReadAllBytes(rom)); n.SetCpuCore("FIX"); n.SetPpuCore("FIX"); n.SetApuCore("FIX");
            int frames = applyAt + seconds * 60;
            for (int f = 0; f < frames; f++)
            {
                if (f == applyAt && s != null) n.ApplyButcher(s);
                n.SetInput(new[] { false, false, false, f >= warm && f < warm + 6, false, false, false, false }); n.RunFrame();
                var buf = n.GetAudioBuffer();
                if (f >= applyAt) foreach (var x in buf) pcm.Add((short)Math.Clamp(x * 32767, -32768, 32767));
            }
            return (pcm.ToArray(), n.GetAudioSampleRate(), 1);
        }
        var kind = console switch { "gb" => NesEmulator.Systems.ConsoleKind.GameBoy, "sms" => NesEmulator.Systems.ConsoleKind.MasterSystem, "gg" => NesEmulator.Systems.ConsoleKind.GameGear, "md" => NesEmulator.Systems.ConsoleKind.Genesis, _ => NesEmulator.Systems.ConsoleKind.Snes };
        byte[] data = NesEmulator.Systems.RomDetect.Unwrap(File.ReadAllBytes(rom), rom, out string inner);
        var slot = NesEmulator.Systems.CoreSlot.Apu;
        string Def(NesEmulator.Systems.CoreSlot sl) => NesEmulator.Systems.CoreCatalog.Default(kind, sl);
        using var session = NesEmulator.Systems.ConsoleSessions.Create(kind, data, Def(NesEmulator.Systems.CoreSlot.Cpu), Def(NesEmulator.Systems.CoreSlot.Ppu), Def(slot), null, inner);
        var host = Host(session);
        var sb = new short[65536];
        int total = applyAt + (int)(seconds * session.FramesPerSecond);
        for (int f = 0; f < total; f++)
        {
            if (f == applyAt && s != null) host?.ApplyButcher(s);
            session.SetPad(0, f >= warm && f < warm + 6 ? NesEmulator.Systems.PadButtons.Start : NesEmulator.Systems.PadButtons.None);
            session.RunFrame();
            int got; while ((got = session.ReadSamples(sb)) > 0) if (f >= applyAt) for (int i = 0; i < got; i++) pcm.Add(sb[i]);
        }
        return (pcm.ToArray(), session.SampleRate, 2);
    }

    private static void WriteWav(string path, short[] pcm, int rate, int channels)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
        foreach (var x in pcm) w.Write(x);
    }

    /// <summary>The host a session's Butchering mode drives (the Sega session under its picture bridge).</summary>
    public static IButcherHost? Host(object session) =>
        (session is NesEmulator.Mix.SegaPictureSession sp ? sp.Inner : session) as IButcherHost;
}
