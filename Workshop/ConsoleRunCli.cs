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
        using var s = ConsoleSessions.Create(console, rom, cpu, ppu, apu);
        Console.WriteLine($"detected {Consoles.DisplayName(detected)}, running {s.Description}");
        var buf = new short[32768]; long samples = 0; var held = PadButtons.None;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 1; f <= frames; f++)
        {
            if (script.TryGetValue(f - 1, out var h)) held = h;
            s.SetPad(0, held);
            s.RunFrame();
            int n; while ((n = s.ReadSamples(buf)) > 0) samples += n;
            if (pngAt.Contains(f)) MixLabCli.SaveArgb(s.Frame, s.FrameWidth, s.FrameHeight, Path.Combine(outDir, $"{tag}_{Consoles.Key(console)}_f{f:D5}.png"));
        }
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
