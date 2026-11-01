using System;
using System.IO;
using System.Text.Json;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// Scriptable wrapper around NES.RunBenchmarks() (WorkshopForm's own "Run Benchmarks" button,
/// previously GUI-only). Exists specifically so the FIX-core accuracy-vs-speed tradeoff process
/// (see project_fix_core_family memory) can measure the speed cost of a candidate fix without
/// UI automation - every fix must be justified on compatibility/jank grounds AND checked against
/// this before being accepted, per the user's explicit policy: accuracy fixes that meaningfully
/// regress speed for marginal accuracy gain should be rejected, not merged.
///
/// Usage: --benchmark --rom path.nes [--cpu ID --ppu ID --apu ID] [--weight N] [--out result.json]
/// </summary>
internal static class BenchmarkCli
{
    public static int Run(string[] args)
    {
        string? romPath = null, cpu = null, ppu = null, apu = null, outPath = null;
        int weight = 1;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rom": romPath = args[++i]; break;
                case "--cpu": cpu = args[++i]; break;
                case "--ppu": ppu = args[++i]; break;
                case "--apu": apu = args[++i]; break;
                case "--weight": weight = int.Parse(args[++i]); break;
                case "--out": outPath = args[++i]; break;
            }
        }
        if (romPath == null)
        {
            Console.Error.WriteLine("Usage: --benchmark --rom <path.nes> [--cpu ID --ppu ID --apu ID] [--weight N] [--out result.json]");
            return 2;
        }

        byte[] romBytes;
        try { romBytes = File.ReadAllBytes(romPath); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to read ROM: {ex.Message}"); return 2; }

        try
        {
            var nes = new NES { RomName = Path.GetFileName(romPath) };
            nes.LoadROM(romBytes);
            if (cpu != null && !nes.SetCpuCore(cpu)) { Console.Error.WriteLine($"Unknown CPU core: {cpu}"); return 3; }
            if (ppu != null && !nes.SetPpuCore(ppu)) { Console.Error.WriteLine($"Unknown PPU core: {ppu}"); return 3; }
            if (apu != null && !nes.SetApuCore(apu)) { Console.Error.WriteLine($"Unknown APU core: {apu}"); return 3; }

            // Run once unmeasured first (JIT warmup - otherwise the first real result is skewed).
            nes.RunBenchmarks();
            var results = nes.RunBenchmarks(weight);

            var payload = new
            {
                Rom = Path.GetFileName(romPath),
                Cpu = nes.GetCpuCoreId(), Ppu = nes.GetPpuCoreId(), Apu = nes.GetApuCoreId(),
                Weight = weight,
                Results = results,
            };
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            if (outPath != null) File.WriteAllText(outPath, json);
            Console.WriteLine(json);
            return 0;
        }
        catch (Cartridge.UnsupportedMapperException ume)
        {
            Console.Error.WriteLine($"Unsupported mapper {ume.MapperId} ({ume.MapperName}).");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 5;
        }
    }
}
