using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using NesEmulator.Snes;

namespace BrokenNes.Workshop;

/// <summary>
/// Speed + determinism gate for the SFC cores: `--snesbench`.
///
/// Runs a fixed scripted workload and measures only the time spent inside BOARD_SFC.RunFrame.
/// Alongside, it hashes every frame's pixels and every audio sample. With --golden it records those
/// hashes (first time) or checks them, so an optimization that changes a single pixel or sample
/// fails loudly and names the first divergent 250-frame checkpoint.
///
/// Usage:
///   --snesbench --rom game.sfc [--preset smw] [--input "..."] [--frames N] [--apu SFC|HLE]
///               [--repeat N] [--breakdown] [--golden file.json]
/// Exit codes: 0 ok (and golden matched/recorded) | 1 golden mismatch | 2 usage/IO | 5 unexpected
/// </summary>
internal static class SnesBenchCli
{
    private const string Usage =
        "Usage: --snesbench --rom <game.sfc> [--preset smw] [--input \"frame:Buttons,...\"] [--frames N]\n" +
        "                   [--apu SFC|HLE] [--repeat N] [--breakdown] [--golden file.json] [--reference-paths]";

    /// <summary>Title -> file select -> Yoshi's House -> overworld (reached ~frame 4000 with APU_SFC).</summary>
    private static string SmwScript()
    {
        var steps = new List<string> { "500:Start", "508:", "700:A", "708:", "900:A", "908:", "1100:A", "1108:", "1300:A", "1308:" };
        for (int f = 1600; f <= 2600; f += 100) { steps.Add($"{f}:B"); steps.Add($"{f + 10}:"); steps.Add($"{f + 50}:A"); steps.Add($"{f + 60}:"); }
        steps.AddRange(new[] { "2800:Right", "3200:Right+B", "3240:Right", "3900:" });
        return string.Join(",", steps);
    }

    /// <summary>
    /// Performance experiments that exist as a toggle in the cores: --ab NAME alternates the switch
    /// on (even runs) and off (odd runs) so both variants see the same machine conditions, which
    /// matters when other work shares the CPU. Output must be identical either way (checked).
    /// </summary>
    private static readonly Dictionary<string, Action<bool>> AbSwitches = new()
    {
        // Settled experiments are removed once decided; add new toggles here while measuring.
        ["fast-paths"] = SetFastPaths,   // every optimized path vs its reference twin
    };

    /// <summary>All cores' optimized-vs-reference switches together.</summary>
    private static void SetFastPaths(bool on) { PPU_SFC.FastPaths = on; DSP_SFC.FastPaths = on; }

    private sealed class Golden
    {
        public string Rom { get; set; } = "";
        public string Apu { get; set; } = "";
        public int Frames { get; set; }
        public string Script { get; set; } = "";
        public string Video { get; set; } = "";
        public string Audio { get; set; } = "";
        public List<string> Checkpoints { get; set; } = new();
    }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string? romPath = null, input = null, apuChoice = null, goldenPath = null, preset = null, abSwitch = null;
        int frames = 4300, repeat = 3;
        bool breakdown = false;
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--rom": romPath = args[++i]; break;
                    case "--preset": preset = args[++i]; break;
                    case "--input": input = args[++i]; break;
                    case "--frames": frames = int.Parse(args[++i]); break;
                    case "--apu": apuChoice = args[++i]; SnesApuChoice.Create(apuChoice); break;
                    case "--repeat": repeat = Math.Max(1, int.Parse(args[++i])); break;
                    case "--breakdown": breakdown = true; break;
                    case "--golden": goldenPath = args[++i]; break;
                    case "--reference-paths": SetFastPaths(false); break;
                    case "--ab": abSwitch = args[++i]; if (!AbSwitches.ContainsKey(abSwitch)) throw new FormatException($"unknown --ab switch '{abSwitch}' ({string.Join(", ", AbSwitches.Keys)})"); break;
                    default: Console.Error.WriteLine($"Unknown argument: {args[i]}\n{Usage}"); return 2;
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or FormatException)
        {
            Console.Error.WriteLine($"Bad arguments: {ex.Message}\n{Usage}");
            return 2;
        }
        if (romPath == null) { Console.Error.WriteLine(Usage); return 2; }
        if (preset != null)
        {
            input = preset.ToLowerInvariant() switch
            {
                "smw" => SmwScript(),
                "alttp" => "1300:Start,1308:,1500:Start,1508:",   // intro -> title -> file select -> name entry
                _ => null,
            };
            if (input == null) { Console.Error.WriteLine($"Unknown preset '{preset}' (smw, alttp)"); return 2; }
        }

        try
        {
            byte[] file = File.ReadAllBytes(romPath);
            var script = SnesRunCli.ParseInput(input);
            Golden? result = null;
            var fpsRuns = new List<double>();

            var abFps = new Dictionary<bool, List<double>> { [true] = new(), [false] = new() };
            for (int run = 0; run < repeat; run++)
            {
                bool abOn = run % 2 == 0;
                if (abSwitch != null) AbSwitches[abSwitch](abOn);
                var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
                SnesProfiler.Enabled = breakdown;
                SnesProfiler.Reset();
                var board = new BOARD_SFC(SnesCartridge.Load(file), SnesApuChoice.Create(apuChoice));
                var g = new Golden { Rom = Path.GetFileName(romPath), Apu = board.Apu.CoreName, Frames = frames, Script = input ?? "" };
                ulong video = 14695981039346656037UL, audio = 14695981039346656037UL;
                var samples = new short[8192];
                long emuTicks = 0;
                ushort held = 0;

                for (int frame = 1; frame <= frames; frame++)
                {
                    if (script.TryGetValue(frame, out var b)) held = b;
                    board.Pads[0] = held;
                    long t = Stopwatch.GetTimestamp();
                    board.RunFrame();
                    emuTicks += Stopwatch.GetTimestamp() - t;

                    video = Mix(video, board.Ppu.FrameBuffer.AsSpan(0, PPU_SFC.Width * board.Ppu.VisibleHeight));
                    int n;
                    while ((n = board.Apu.ReadSamples(samples)) > 0) audio = Mix(audio, samples.AsSpan(0, n));
                    if (frame % 250 == 0) g.Checkpoints.Add($"{frame}:{video:x16}:{audio:x16}");
                }

                g.Video = video.ToString("x16");
                g.Audio = audio.ToString("x16");
                double secs = emuTicks / (double)Stopwatch.Frequency;
                double fps = frames / secs;
                fpsRuns.Add(fps);
                double cpuSecs = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalSeconds;
                double cpuFps = frames / cpuSecs;   // CPU time is less disturbed by other processes than wall time
                if (abSwitch != null) abFps[abOn].Add(cpuFps);
                string tag = abSwitch != null ? $"[{abSwitch}={(abOn ? "on " : "off")}] " : "";
                Console.WriteLine($"run {run + 1}: {tag}{fps,7:F1} fps wall  {cpuFps,7:F1} fps cpu  {1000 * secs / frames:F3} ms/frame");
                if (breakdown)
                {
                    double f = Stopwatch.Frequency;
                    double ppu = SnesProfiler.PpuTicks / f, apu = SnesProfiler.ApuTicks / f, dsp = SnesProfiler.DspTicks / f;
                    double cpu = secs - ppu - apu;
                    Console.WriteLine($"        cpu+bus {100 * cpu / secs,5:F1}%  ppu {100 * ppu / secs,5:F1}%  smp {100 * (apu - dsp) / secs,5:F1}%  dsp {100 * dsp / secs,5:F1}%  (timers on: adds overhead)");
                }
                if (result != null && (result.Video != g.Video || result.Audio != g.Audio))
                {
                    Console.WriteLine($"NONDETERMINISTIC: run {run + 1} hashes differ from run 1");
                    return 1;
                }
                result ??= g;
            }
            SnesProfiler.Enabled = false;
            if (abSwitch != null)
            {
                AbSwitches[abSwitch](true);
                static double Median(List<double> v) => v.Count == 0 ? 0 : v.OrderBy(x => x).ElementAt(v.Count / 2);
                double on = Median(abFps[true]), off = Median(abFps[false]);
                Console.WriteLine($"A/B {abSwitch}: on {on:F1} fps cpu (n={abFps[true].Count}) vs off {off:F1} (n={abFps[false].Count}) -> {(on / off - 1) * 100:+0.0;-0.0}% for on");
            }

            Console.WriteLine($"best {fpsRuns.Max():F1} fps, median {fpsRuns.OrderBy(x => x).ElementAt(fpsRuns.Count / 2):F1} fps over {repeat} run(s); video {result!.Video} audio {result.Audio}");

            if (goldenPath == null) return 0;
            if (!File.Exists(goldenPath))
            {
                File.WriteAllText(goldenPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"golden recorded: {goldenPath}");
                return 0;
            }
            var golden = JsonSerializer.Deserialize<Golden>(File.ReadAllText(goldenPath))!;
            if (golden.Frames != result.Frames || golden.Script != result.Script || golden.Apu != result.Apu)
            {
                Console.WriteLine($"golden mismatch: workload differs (golden {golden.Frames} frames, apu {golden.Apu})");
                return 1;
            }
            if (golden.Video == result.Video && golden.Audio == result.Audio) { Console.WriteLine("golden: IDENTICAL video and audio"); return 0; }
            string? first = golden.Checkpoints.Zip(result.Checkpoints).FirstOrDefault(p => p.First != p.Second).First;
            Console.WriteLine($"golden: MISMATCH (video {(golden.Video == result.Video ? "same" : "DIFFERS")}, audio {(golden.Audio == result.Audio ? "same" : "DIFFERS")}); first divergent checkpoint: {first ?? "after last checkpoint"}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected: {ex}");
            return 5;
        }
    }

    // FNV-style 64-bit mix over whole words (fast enough to run every frame without skewing much).
    private static ulong Mix(ulong h, ReadOnlySpan<uint> data)
    {
        foreach (uint v in data) h = (h ^ v) * 1099511628211UL;
        return h;
    }

    private static ulong Mix(ulong h, ReadOnlySpan<short> data)
    {
        foreach (short v in data) h = (h ^ (ushort)v) * 1099511628211UL;
        return h;
    }
}
