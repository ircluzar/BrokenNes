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
        "                 [--input \"frame:Buttons,...\"] [--wav out.wav] [--sram file.srm]";

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string? romPath = null, outDir = null, input = null, apuChoice = null, wavPath = null, sramPath = null;
        int frames = 600;
        bool keepDisplayVramWrites = false;
        int layerMask = 0x1F;
        int chipTrace = 0;
        string? dumpWram = null, gsuDis = null;
        string? gsuWatch = null, regLog = null, dspLog = null;
        bool spcHot = false;
        var spcHits = new Dictionary<ushort, int>();
        bool lineRegs = false;
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
                    case "--sram": sramPath = args[++i]; break;
                    case "--keep-display-vram-writes": keepDisplayVramWrites = true; break;   // diagnosis only
                    case "--layers": layerMask = Convert.ToInt32(args[++i], 16); break;          // hex: 1/2/4/8 = BG1-4, 10 = OBJ
                    case "--line-regs": lineRegs = true; break;                                     // per-line PPU registers, last frame
                    case "--chip-trace": chipTrace = int.Parse(args[++i]); break;                        // last N Super FX instructions
                    case "--no-dram-refresh": BOARD_SFC.DramRefresh = false; break;                    // A/B timing
                    case "--spc-start-aligned": APU_SFC.EndAligned = false; break;                     // A/B timing
                    case "--no-sa1-conflicts": SA1_SFC.BusConflicts = false; break;                    // A/B timing
                    case "--dsp1-homemade": SnesFirmware.PreferHomemade = true; break;                 // ignore real DSP firmware
                    case "--dsp-log": dspLog = args[++i]; break;                                      // DSP-1 port conversation -> file
                    case "--spc-hot": spcHot = true; break;                                           // SPC700 PC histogram, last 30 frames
                    case "--reg-log": regLog = args[++i]; break;                                      // hexaddrs:file - timing fingerprint
                    case "--gsu-dis": gsuDis = args[++i]; break;                                      // hexaddr:count
                    case "--gsu-watch": gsuWatch = args[++i]; break;                                   // pbr:pc hex list[@fromInstruction]
                    case "--dump-wram": dumpWram = args[++i]; break;                                  // hexaddr:len, e.g. 4F30:32
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
            // --sram: battery RAM survives between runs, like the player's .srm file.
            if (sramPath != null && File.Exists(sramPath) && cart.Sram.Length > 0)
            {
                byte[] sramImage = File.ReadAllBytes(sramPath);
                Array.Copy(sramImage, cart.Sram, Math.Min(sramImage.Length, cart.Sram.Length));
            }
            var chip = SnesFirmware.CreateCoprocessor(cart, romPath, out string chipNote);
            var board = new BOARD_SFC(cart, SnesApuChoice.Create(apuChoice), chip);
            if (keepDisplayVramWrites) board.Ppu.DropVramWritesDuringDisplay = false;
            if (chipTrace > 0 && chip is GSU_SFC gsuTrace) gsuTrace.Trace = new long[chipTrace];
            // --reg-log 2100,4200:out.txt -> "vblankCount scanline lineClock addr value" per write,
            // the same columns mesen\regwrites.lua writes, for timing comparisons.
            System.IO.StreamWriter? regLogWriter = null, dspLogWriter = null;
            if (dspLog != null && chip is NECDSP_SFC necDsp)
            {
                dspLogWriter = new System.IO.StreamWriter(dspLog);
                necDsp.PortLog = (kind, v) => dspLogWriter.WriteLine($"{kind} {v:X4}");
            }
            if (regLog != null)
            {
                int colon = regLog.IndexOf(':');   // addresses first; the path may contain a drive colon
                var watched = regLog[..colon].Split(',').Select(a => Convert.ToUInt32(a, 16)).ToHashSet();
                regLogWriter = new System.IO.StreamWriter(regLog[(colon + 1)..]);
                board.WriteWatch = (bank, offset, value) =>
                {
                    if ((bank & 0x40) == 0 && watched.Contains(offset))
                        regLogWriter.WriteLine($"{board.VBlankCount} {board.Scanline} {board.LineClock} {offset:X4} {value:X2}");
                };
            }
            if (gsuWatch != null && chip is GSU_SFC gsuWatched)
            {
                var at = gsuWatch.Split('@');
                gsuWatched.WatchPcs = at[0].Split(',').Select(p => p == "all" ? -1 : Convert.ToInt32(p, 16)).ToHashSet();
                // "all@arm:018D14:7" = log everything from the 7th execution of 01:8D14; "all@123456" = from instruction #.
                if (at.Length > 1 && at[1].StartsWith("arm:"))
                {
                    var arm = at[1].Split(':');
                    gsuWatched.WatchArmPc = Convert.ToInt32(arm[1], 16);
                    gsuWatched.WatchArmCount = arm.Length > 2 ? int.Parse(arm[2]) : 1;
                }
                else if (at.Length > 1) gsuWatched.WatchFrom = long.Parse(at[1]);
                if (Environment.GetEnvironmentVariable("GSU_MAX") is { Length: > 0 } gmax) gsuWatched.WatchMax = int.Parse(gmax);
                if (Environment.GetEnvironmentVariable("GSU_RAM") is { Length: > 0 } ramFrom)
                {
                    gsuWatched.WatchRamFrom = Convert.ToInt32(ramFrom, 16);
                    gsuWatched.WatchRamLength = int.Parse(Environment.GetEnvironmentVariable("GSU_RAM_LEN") ?? "16");
                }
            }
            board.Ppu.DebugLayerMask = layerMask;
            if (lineRegs) board.Ppu.DebugLineRegisters = new PPU_SFC.RegisterSnapshot[PPU_SFC.MaxHeight + 1];
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
                if (spcHot && frame == sampleFrom + 1 && board.Apu is APU_SFC spcApu)
                    spcApu.SmpHook = pc => spcHits[pc] = spcHits.GetValueOrDefault(pc) + 1;
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

            regLogWriter?.Dispose();
            dspLogWriter?.Dispose();
            if (sramPath != null && cart.Sram.Length > 0) File.WriteAllBytes(sramPath, cart.Sram);
            var c = board.Cpu;
            var sb = new StringBuilder();
            sb.AppendLine($"{Path.GetFileName(romPath)}: \"{cart.Title}\" {(cart.HiRom ? "HiROM" : "LoROM")} map=${cart.MapMode:X2} rom={cart.Rom.Length / 1024}KB sram={cart.Sram.Length / 1024}KB");
            sb.AppendLine($"frames={board.FrameCount} instructions={c.InstructionCount:N0} nmis={board.NmiCount} nmitimen=${board.NmiTimen:X2} forcedBlank={board.Ppu.ForcedBlank} {sw.Elapsed.TotalSeconds:F2}s");
            sb.AppendLine($"cpu PC=${c.PBR:X2}:{c.PC:X4} A=${c.A:X4} X=${c.X:X4} Y=${c.Y:X4} S=${c.S:X4} D=${c.D:X4} DBR=${c.DBR:X2} P=${c.P:X2} E={(c.E ? 1 : 0)} wai={c.Waiting}");
            sb.AppendLine($"ppu: {board.Ppu.GetRegisterSnapshot()}");
            sb.AppendLine($"hdma: {board.DescribeHdma()}");
            if (board.Ppu.DebugLineRegisters is { } regs)
            {
                // Runs of identical lines, so a 224-line frame collapses to a handful of bands.
                int start = 1;
                for (int ln = 2; ln <= board.Ppu.VisibleHeight + 1; ln++)
                {
                    if (ln <= board.Ppu.VisibleHeight && regs[ln] == regs[start]) continue;
                    sb.AppendLine($"  lines {start,3}-{ln - 1,3}: {regs[start]}");
                    start = ln;
                }
            }
            if (chip is GSU_SFC gw && gw.WatchPcs != null)
                foreach (var line in gw.WatchLog) sb.AppendLine($"watch {line}");
            if (gsuDis != null && chip is GSU_SFC gd)
            {
                var dis = gsuDis.Split(':');
                sb.Append(gd.Disassemble(Convert.ToUInt32(dis[0], 16), int.Parse(dis[1])));
            }
            if (dumpWram != null)
            {
                var parts = dumpWram.Split(':');
                int start = Convert.ToInt32(parts[0], 16), len = int.Parse(parts[1]);
                for (int row = 0; row < len; row += 16)
                    sb.AppendLine($"wram {start + row:X5}: " + string.Join(" ", Enumerable.Range(start + row, Math.Min(16, len - row)).Select(a => board.Wram[a & 0x1FFFF].ToString("X2"))));
            }
            if (cart.Sram.Length > 0) sb.AppendLine($"sram writes: {cart.SramWrites}");
            sb.AppendLine($"apu: {board.Apu.Describe()}");
            if (chipNote != "") sb.AppendLine($"chip: {chipNote}");
            if (chip != null) sb.AppendLine($"chip state: {chip.Describe()}");
            if (chip is GSU_SFC g && g.Trace is { } ring)
            {
                sb.AppendLine($"gsu regs: {g.DescribeRegs()}");
                var hist = new Dictionary<long, int>();
                int count = Math.Min(ring.Length, g.TracePos);
                for (int k = 0; k < count; k++) { long pcKey = (ring[k] >> 8) & 0xFFFFFF; hist[pcKey] = hist.GetValueOrDefault(pcKey) + 1; }
                sb.AppendLine($"gsu hot pcs: " + string.Join(" ", hist.OrderByDescending(h => h.Value).Take(12).Select(h => $"{h.Key >> 16:X2}:{h.Key & 0xFFFF:X4}x{h.Value}")));
                for (int k = Math.Max(0, g.TracePos - 48); k < g.TracePos; k++)
                {
                    long e = ring[k % ring.Length];
                    sb.AppendLine($"  gsu {(e >> 24) & 0xFF:X2}:{(e >> 8) & 0xFFFF:X4}  op={e & 0xFF:X2}  sfr={(e >> 32) & 0xFFFF:X4}");
                }
            }
            if (audio.Frames > 0) sb.AppendLine($"audio: {audio}{(wavPath != null ? $"  wav: {wavPath}" : "")}");
            int total = pcHits.Values.Sum();
            sb.AppendLine($"hottest PCs over the last {frames - sampleFrom} frames ({total:N0} instructions):");
            foreach (var kv in pcHits.OrderByDescending(k => k.Value).Take(8))
                sb.AppendLine($"  ${kv.Key >> 16:X2}:{kv.Key & 0xFFFF:X4}  {100.0 * kv.Value / Math.Max(1, total),5:F1}%");
            if (spcHot && board.Apu is APU_SFC spcDump)
            {
                int spcTotal = spcHits.Values.Sum();
                sb.AppendLine($"hottest SPC700 PCs ({spcTotal:N0} instructions):");
                foreach (var kv in spcHits.OrderByDescending(k => k.Value).Take(16))
                    sb.AppendLine($"  spc ${kv.Key:X4} {100.0 * kv.Value / Math.Max(1, spcTotal),5:F1}%  op={spcDump.Aram[kv.Key]:X2} {spcDump.Aram[(ushort)(kv.Key + 1)]:X2} {spcDump.Aram[(ushort)(kv.Key + 2)]:X2}");
            }
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

    internal static SortedDictionary<int, ushort> ParseInput(string? script)
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
