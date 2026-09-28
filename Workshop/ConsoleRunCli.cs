using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes.Workshop.MixLab;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// Headless checks of the BrokenNes 2 console layer (NesEmulator.Systems), the same code the desktop and Lite apps run:
///   --console-catalog                      every console's core menus, own family first
///   --console-run --rom X [--console snes|gb|gbc] [--cpu id --ppu id --apu id] [--frames N] [--png-at f1,f2]
///                 [--input "f:A+Start,f:"] [--out dir] [--tag name]
/// The console is detected from the ROM unless given. Prints frame size, audio sample count and a picture hash.
/// </summary>
internal static class ConsoleRunCli
{
    public static int Catalog()
    {
        foreach (var c in Consoles.All)
        {
            Console.WriteLine($"== {Consoles.DisplayName(c)}");
            foreach (var slot in new[] { CoreSlot.Cpu, CoreSlot.Ppu, CoreSlot.Apu })
                Console.WriteLine($"  {slot}: " + string.Join(" | ", CoreCatalog.Options(c, slot).GroupBy(o => o.Family).Select(g => $"[{g.Key}] " + string.Join(", ", g.Select(o => o.Id)))));
        }
        return 0;
    }

    public static int Run(string[] args)
    {
        string Opt(string name, string dflt) { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : dflt; }
        string romPath = Opt("rom", "");
        byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(romPath), romPath, out string inner);
        var detected = RomDetect.Detect(rom, inner) ?? throw new InvalidDataException("Unknown ROM type");
        var console = Opt("console", "") is { Length: > 0 } c ? Consoles.FromKey(c) : detected;
        string cpu = CoreCatalog.Resolve(console, CoreSlot.Cpu, Opt("cpu", "")), ppu = CoreCatalog.Resolve(console, CoreSlot.Ppu, Opt("ppu", "")), apu = CoreCatalog.Resolve(console, CoreSlot.Apu, Opt("apu", ""));
        int frames = int.Parse(Opt("frames", "600"));
        var pngAt = Opt("png-at", frames.ToString()).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        string outDir = Opt("out", "."), tag = Opt("tag", Path.GetFileNameWithoutExtension(inner));
        Directory.CreateDirectory(outDir);
        var script = ParseInput(Opt("input", ""));
        // --gb-look green|grey|pocket|color, --gb-invert-bg, --gb-invert-obj: the Game Boy look (before the session is built:
        // Color decides the Game Boy model).
        NesEmulator.Mix.GbLook.Palette = NesEmulator.Mix.GbLook.FromKey(Opt("gb-look", "green"));
        NesEmulator.Mix.GbLook.InvertBackground = args.Contains("--gb-invert-bg");
        NesEmulator.Mix.GbLook.InvertSprites = args.Contains("--gb-invert-obj");
        using var s = ConsoleSessions.Create(console, rom, cpu, ppu, apu);
        Console.WriteLine($"detected {Consoles.DisplayName(detected)}, running {s.Description}");
        // --battery <file>: start from a copy of a battery save (read only). --wav <file> [--wav-from <frame>]: record the output as mono.
        string battery = Opt("battery", ""), wav = Opt("wav", "");
        int wavFrom = int.Parse(Opt("wav-from", "1"));
        if (battery != "") { s.ImportSave(File.ReadAllBytes(battery)); Console.WriteLine($"battery: {battery}"); }
        // --pitch-ceiling <hz>: turn on "Remove high-pitched" at that ceiling. --pitch-census: count the tonal notes the bridges play.
        string ceiling = Opt("pitch-ceiling", "");
        NesEmulator.Mix.PitchGuard.Enabled = ceiling != "";
        if (ceiling != "") NesEmulator.Mix.PitchGuard.CeilingHz = float.Parse(ceiling, System.Globalization.CultureInfo.InvariantCulture);
        NesEmulator.Mix.PitchGuard.BlockedNotes = 0;
        NesEmulator.Mix.PitchGuard.Census = args.Contains("--pitch-census") ? new long[128] : null;
        // --first-chance: report hardware-origin exceptions (null, divide by zero, overflow) even when caught.
        if (args.Contains("--first-chance"))
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (e.Exception is NullReferenceException or DivideByZeroException or OverflowException or IndexOutOfRangeException)
                    Console.Error.WriteLine($"FIRST-CHANCE {e.Exception.GetType().Name}: {e.Exception.Message}\n{new System.Diagnostics.StackTrace(1, false)}");
            };
        var pcm = new List<short>();
        int dumpLines = int.Parse(Opt("dump-lines", "0"));
        var swaps = Opt("swap", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(':', 3))
            .Select(x => (frame: int.Parse(x[0]), slot: Enum.Parse<CoreSlot>(x[1], true), id: x[2])).ToList();
        // --sfc-layers <mask>: the native SNES picture with only these layers (bit 0-3 BG1-4, bit 4 OBJ) - diagnosis.
        if (Opt("sfc-layers", "") is { Length: > 0 } lm && s is SnesSession lms) lms.Ppu.DebugLayerMask = Convert.ToInt32(lm, 16);
        var buf = new short[32768]; long samples = 0; var held = PadButtons.None;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 1; f <= frames; f++)
        {
            if (script.TryGetValue(f - 1, out var h)) held = h;
            // --swap "frame:slot:id,...": hot-swap a core at that frame (slot cpu / ppu / apu), the game running on.
            foreach (var sw2 in swaps) if (sw2.frame == f) Console.WriteLine($"frame {f}: swap {sw2.slot} -> {sw2.id}: {(s.TrySwapCore(sw2.slot, sw2.id) ? "in place" : "NOT in place")} | {s.Description}");
            // --dump-lines <frame>: that frame's per-line SNES registers, printed wherever they change from the line above.
            if (dumpLines == f && s is SnesSession dss) dss.Ppu.BridgeLines = new NesEmulator.Snes.PPU_SFC.BridgeLineState[240];
            s.SetPad(0, held);
            s.RunFrame();
            if (dumpLines == f && s is SnesSession dumped && dumped.Ppu.BridgeLines is { } lines)
            {
                Console.WriteLine($"--- frame {f}: per-line SNES registers (printed where they change)");
                string prev = "";
                for (int ln = 1; ln <= 224; ln++)
                {
                    var b = lines[ln];
                    string cur = $"{b.Regs} W12={b.W12sel:X2} W34={b.W34sel:X2} WOBJ={b.Wobjsel:X2} WH={b.Wh0},{b.Wh1},{b.Wh2},{b.Wh3} " +
                                 $"WLOG={b.Wbglog:X2},{b.Wobjlog:X2} COLDATA={b.Coldata:X4} BACKDROP={b.Backdrop:X4} " +
                                 $"M7=[{b.M7a},{b.M7b},{b.M7c},{b.M7d} c={b.M7x},{b.M7y} s={b.M7hofs},{b.M7vofs}]";
                    if (cur != prev) Console.WriteLine($"  line {ln,3}: {cur}");
                    prev = cur;
                }
                dumped.Ppu.BridgeLines = null;
                // The picture translation's own report for this frame (when a NES/Game Boy picture chip is in use).
                var down = typeof(SnesSession).GetField("down", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(dumped);
                if (down?.GetType().GetField("Last")?.GetValue(down) is { } stats) Console.WriteLine($"  translation: {stats}");
            }
            int n; while ((n = s.ReadSamples(buf)) > 0)
            {
                samples += n;
                if (wav != "" && f >= wavFrom) for (int i = 0; i + 1 < n; i += 2) pcm.Add((short)((buf[i] + buf[i + 1]) / 2));
            }
            if (pngAt.Contains(f)) MixLabCli.SaveArgb(s.Frame, s.FrameWidth, s.FrameHeight, Path.Combine(outDir, $"{tag}_{Consoles.Key(console)}_f{f:D5}.png"));
        }
        if (NesEmulator.Mix.PitchGuard.Enabled) Console.WriteLine($"pitch guard: ceiling {NesEmulator.Mix.PitchGuard.CeilingHz} Hz, blocked {NesEmulator.Mix.PitchGuard.BlockedNotes} note-syncs");
        if (NesEmulator.Mix.PitchGuard.Census is { } census)
        {
            Console.WriteLine("pitch census (note: syncs):");
            for (int m = 0; m < 128; m++)
                if (census[m] > 0) Console.WriteLine($"  {NesEmulator.Mix.PitchGuard.NoteName(440 * Math.Pow(2, (m - 69) / 12.0)),-4} {440 * Math.Pow(2, (m - 69) / 12.0),8:F0} Hz  {census[m]}");
        }
        if (wav != "") { MixAudioCli.WriteWav(wav, pcm.ToArray(), s.SampleRate); Console.WriteLine($"wav: {wav} ({pcm.Count / (double)s.SampleRate:F1} s)"); }
        ulong hash = 1469598103934665603;
        for (int i = 0; i < s.FrameWidth * s.FrameHeight; i++) hash = (hash ^ s.Frame[i]) * 1099511628211;
        Console.WriteLine($"frames={frames} last={s.FrameWidth}x{s.FrameHeight} display-width={s.DisplayWidth} audio={samples / 2:N0} stereo samples @ {s.SampleRate} Hz " +
                          $"battery={s.HasBattery} hash={hash:X16} {frames / sw.Elapsed.TotalSeconds:F0} fps");
        return 0;
    }

    private static Dictionary<int, PadButtons> ParseInput(string spec)
    {
        var d = new Dictionary<int, PadButtons>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':'); PadButtons b = 0;
            if (kv.Length > 1) foreach (var name in kv[1].Split('+', StringSplitOptions.RemoveEmptyEntries)) b |= Enum.Parse<PadButtons>(name, true);
            d[int.Parse(kv[0])] = b;
        }
        return d;
    }
}
