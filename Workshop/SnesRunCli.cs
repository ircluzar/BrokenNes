using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Snes;

namespace BrokenNes.Workshop;

/// <summary>
/// Headless "how far does this SNES game get?" probe for the SFC core family - the SNES
/// counterpart of --headless. It runs N frames with optional scripted input, saves screenshots
/// at the requested frames, and reports where the CPU spent the final frames (the top PCs of a hang
/// loop say more than any screenshot) together with the fake SMP's port values.
///
/// Usage:
///   --snesrun --rom &lt;game.sfc&gt; [--frames N] [--png-at 60,300,900] [--out-dir dir]
///             [--input "300:Start,310:,600:A"]
/// Buttons: A B X Y L R Start Select Up Down Left Right, joined with '+'. An empty list releases all.
/// Exit codes: 0 ran | 2 usage/IO | 4 CPU stopped (STP) | 5 unexpected
/// </summary>
internal static class SnesRunCli
{
    private const string Usage =
        "Usage: --snesrun --rom <game.sfc> [--apu SFC|HLE] [--frames N] [--png-at f1,f2,...] [--out-dir dir]\n" +
        "                 [--input \"frame:Buttons,...\"] [--wav out.wav]";

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string? romPath = null, outDir = null, input = null, apuChoice = null, wavPath = null;
        int frames = 600;
        var pngAt = new HashSet<int>();
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--rom": romPath = args[++i]; break;
                    case "--frames": frames = int.Parse(args[++i]); break;
                    case "--png-at": foreach (var f in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)) pngAt.Add(int.Parse(f)); break;
                    case "--out-dir": outDir = args[++i]; break;
                    case "--input": input = args[++i]; break;
                    case "--apu": apuChoice = args[++i]; SnesApuChoice.Create(apuChoice); break;
                    case "--wav": wavPath = args[++i]; break;
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
        outDir ??= Directory.GetCurrentDirectory();

        try
        {
            SortedDictionary<int, ushort> script;
            try { script = ParseInput(input); }
            catch (FormatException fe) { Console.Error.WriteLine($"Bad --input: {fe.Message}"); return 2; }

            byte[] file;
            try { file = File.ReadAllBytes(romPath); }
            catch (Exception ex) { Console.Error.WriteLine($"Cannot read ROM: {ex.Message}"); return 2; }

            var cart = SnesCartridge.Load(file);
            var board = new BOARD_SFC(cart, SnesApuChoice.Create(apuChoice));
            using var wav = wavPath != null ? new WavWriter(wavPath, board.Apu.SampleRate) : null;
            var audioBuf = new short[8192];
            var audio = new AudioStats(board.Apu.SampleRate);
            Directory.CreateDirectory(outDir);
            string stem = Path.GetFileNameWithoutExtension(romPath);

            // Sample PCs over the last 30 frames: a hang shows up as a handful of addresses.
            var pcHits = new Dictionary<uint, int>();
            int sampleFrom = Math.Max(0, frames - 30);
            var saved = new List<string>();
            ushort held = 0;
            int exit = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int frame = 1; frame <= frames; frame++)
            {
                if (script.TryGetValue(frame, out var b)) held = b;
                board.Pads[0] = held;
                board.InstructionHook = frame > sampleFrom
                    ? cpu => { uint pc = (uint)cpu.PBR << 16 | cpu.PC; pcHits[pc] = pcHits.GetValueOrDefault(pc) + 1; }
                    : null;
                board.RunFrame();
                int got;
                while ((got = board.Apu.ReadSamples(audioBuf)) > 0)
                {
                    wav?.Write(audioBuf.AsSpan(0, got));
                    audio.Add(audioBuf.AsSpan(0, got));
                }
                if (pngAt.Contains(frame))
                {
                    string p = Path.Combine(outDir, $"{stem}_f{frame:D5}.png");
                    SnesTestCli.SavePng(board.Ppu, p);
                    saved.Add(p);
                }
                if (board.Cpu.Stopped) { Console.WriteLine($"CPU stopped (STP) at frame {frame}"); exit = 4; break; }
            }

            var c = board.Cpu;
            var sb = new StringBuilder();
            sb.AppendLine($"{Path.GetFileName(romPath)}: \"{cart.Title}\" {(cart.HiRom ? "HiROM" : "LoROM")} map=${cart.MapMode:X2} rom={cart.Rom.Length / 1024}KB sram={cart.Sram.Length / 1024}KB");
            sb.AppendLine($"frames={board.FrameCount} instructions={c.InstructionCount:N0} nmis={board.NmiCount} nmitimen=${board.NmiTimen:X2} forcedBlank={board.Ppu.ForcedBlank} {sw.Elapsed.TotalSeconds:F2}s");
            sb.AppendLine($"cpu PC=${c.PBR:X2}:{c.PC:X4} A=${c.A:X4} X=${c.X:X4} Y=${c.Y:X4} S=${c.S:X4} D=${c.D:X4} DBR=${c.DBR:X2} P=${c.P:X2} E={(c.E ? 1 : 0)} wai={c.Waiting}");
            sb.AppendLine($"apu: {board.Apu.Describe()}");
            if (audio.Frames > 0) sb.AppendLine($"audio: {audio}{(wavPath != null ? $"  wav: {wavPath}" : "")}");
            int total = pcHits.Values.Sum();
            sb.AppendLine($"hottest PCs over the last {frames - sampleFrom} frames ({total:N0} instructions):");
            foreach (var kv in pcHits.OrderByDescending(k => k.Value).Take(8))
                sb.AppendLine($"  ${kv.Key >> 16:X2}:{kv.Key & 0xFFFF:X4}  {100.0 * kv.Value / Math.Max(1, total),5:F1}%");
            foreach (var p in saved) sb.AppendLine($"png: {p}");
            Console.WriteLine(sb.ToString().TrimEnd());
            return exit;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected: {ex}");
            return 5;
        }
    }

    /// <summary>
    /// Enough statistics to tell real audio from silence, DC or noise without listening: overall
    /// RMS/peak, how many one-second windows are audible, and the DC offset. Real game music has a
    /// small DC offset, many audible seconds, and peaks well above the RMS.
    /// </summary>
    private sealed class AudioStats
    {
        private readonly int rate;
        private double sumSq, sum, windowSumSq;
        private int peak, windowFrames, audibleSeconds, seconds;
        public long Frames { get; private set; }
        private long clipped;

        public AudioStats(int rate) { this.rate = rate; }

        public void Add(ReadOnlySpan<short> interleaved)
        {
            for (int i = 0; i + 1 < interleaved.Length; i += 2)
            {
                int m = (interleaved[i] + interleaved[i + 1]) / 2;
                sumSq += (double)m * m; sum += m; windowSumSq += (double)m * m;
                int a = Math.Max(Math.Abs((int)interleaved[i]), Math.Abs((int)interleaved[i + 1]));
                if (a > peak) peak = a;
                if (a >= 32767) clipped++;
                Frames++;
                if (++windowFrames == rate)
                {
                    seconds++;
                    if (Math.Sqrt(windowSumSq / rate) > 100) audibleSeconds++;
                    windowSumSq = 0; windowFrames = 0;
                }
            }
        }

        public override string ToString()
        {
            double rms = Math.Sqrt(sumSq / Math.Max(1, Frames)), dc = sum / Math.Max(1, Frames);
            return $"{Frames / (double)rate:F1}s rms={rms:F0} peak={peak} dc={dc:F1} audible={audibleSeconds}/{seconds}s clipped={clipped}";
        }
    }

    private static SortedDictionary<int, ushort> ParseInput(string? script)
    {
        var map = new SortedDictionary<int, ushort>();
        if (string.IsNullOrWhiteSpace(script)) return map;
        foreach (var step in script.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = step.IndexOf(':');
            if (colon < 0) throw new FormatException($"'{step}' needs frame:buttons");
            int frame = int.Parse(step[..colon]);
            ushort bits = 0;
            foreach (var name in step[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Enum.TryParse<SnesButtons>(name.Trim(), ignoreCase: true, out var btn) || btn == SnesButtons.None)
                    throw new FormatException($"unknown button '{name}'");
                bits |= (ushort)btn;
            }
            map[frame] = bits;
        }
        return map;
    }
}
