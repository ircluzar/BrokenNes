using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// CLI front-end for AccuracyCoinRunner. Two modes:
///   --accuracycoin --rom AccuracyCoin.nes [--cpu ID --ppu ID --apu ID] [--trace] [--wait-frames N]
///     Single diagnostic run (defaults to whatever core NES.cs picks if none given). Prints a
///     full per-test breakdown to stdout. --trace logs RunningAllTests/PostAllTestTally every
///     200 frames while waiting, to help tune --wait-frames.
///   --accuracycoin --rom AccuracyCoin.nes --matrix --out results.json [--wait-frames N] [--cpus a,b,c] [--ppus ...] [--apus ...]
///     Full cross-product across CoreRegistry.CpuIds x PpuIds x ApuIds (or the given subsets),
///     all within one process to avoid per-combo process-launch overhead. Writes one JSON array.
/// </summary>
internal static class AccuracyCoinCli
{
    public static int Run(string[] args)
    {
        string? romPath = null, outPath = null;
        string? cpu = null, ppu = null, apu = null;
        string? cpuList = null, ppuList = null, apuList = null;
        bool matrix = false, trace = false, includeUnofficialOpcodes = false, strict = false;
        int waitFrames = 4000;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rom": romPath = args[++i]; break;
                case "--cpu": cpu = args[++i]; break;
                case "--ppu": ppu = args[++i]; break;
                case "--apu": apu = args[++i]; break;
                case "--cpus": cpuList = args[++i]; break;
                case "--ppus": ppuList = args[++i]; break;
                case "--apus": apuList = args[++i]; break;
                case "--matrix": matrix = true; break;
                case "--out": outPath = args[++i]; break;
                case "--trace": trace = true; break;
                case "--wait-frames": waitFrames = int.Parse(args[++i]); break;
                case "--include-unofficial-opcodes": includeUnofficialOpcodes = true; break;
                case "--strict": strict = true; break;
            }
        }

        // Every CPU core crashes on some unofficial opcode (verified: all 7), which would
        // otherwise truncate the automated run within the first ~20 of 141 tests for every
        // single combination, before almost all PPU/APU/timing tests get a chance to run. Skip
        // those 66 tests by default (via the ROM's own pre-existing-$FF skip mechanism) so the
        // other 75 can be measured for every core; --include-unofficial-opcodes disables this
        // to see the raw crash behavior instead.
        var preSkip = includeUnofficialOpcodes ? null : AccuracyCoinTests.UnofficialOpcodeTestAddresses;

        if (romPath == null)
        {
            Console.Error.WriteLine("Usage: --accuracycoin --rom <AccuracyCoin.nes> [--cpu ID --ppu ID --apu ID] [--trace] [--wait-frames N]");
            Console.Error.WriteLine("   or: --accuracycoin --rom <AccuracyCoin.nes> --matrix --out results.json [--wait-frames N] [--cpus a,b,...] [--ppus ...] [--apus ...]");
            return 2;
        }

        byte[] romBytes;
        try { romBytes = File.ReadAllBytes(romPath); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to read ROM: {ex.Message}"); return 2; }

        // CoreRegistry needs a NES instance constructed first to guarantee the assembly scan
        // (Initialize()) has run - CpuIds/PpuIds/ApuIds are otherwise empty.
        _ = new NES();

        if (matrix)
        {
            var cpus = ParseList(cpuList) ?? CoreRegistry.CpuIds.ToList();
            var ppus = ParseList(ppuList) ?? CoreRegistry.PpuIds.ToList();
            var apus = ParseList(apuList) ?? CoreRegistry.ApuIds.ToList();
            outPath ??= "accuracycoin_matrix.json";
            return RunMatrix(romBytes, cpus, ppus, apus, waitFrames, outPath, preSkip, strict);
        }

        // --trace uses the raw single-attempt runner (retries would make the per-frame trace
        // confusing to read); otherwise use the adaptive skip-and-retry wrapper by default,
        // since a bare crash/hang mid-run is rarely what you actually want to see - see
        // RunSingleComboRobust's own comment for why pre-skipping only the "Unofficial *"
        // suites isn't sufficient on its own.
        var result = trace
            ? AccuracyCoinRunner.RunSingleCombo(romBytes, cpu, ppu, apu, waitFrames, trace, preSkip, strict)
            : AccuracyCoinRunner.RunSingleComboRobust(romBytes, cpu, ppu, apu, waitFrames, baseSkipAddresses: preSkip, strict: strict);
        PrintSingleResult(result);
        if (outPath != null)
            File.WriteAllText(outPath, JsonSerializer.Serialize(result, JsonOpts));
        return result.Crashed ? 1 : 0;
    }

    private static List<string>? ParseList(string? csv) =>
        csv == null ? null : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static void PrintSingleResult(AccuracyCoinRunResult r)
    {
        Console.WriteLine($"CPU={r.Cpu} PPU={r.Ppu} APU={r.Apu}");
        Console.WriteLine($"Crashed={r.Crashed}{(r.CrashInfo != null ? " (" + r.CrashInfo + ")" : "")}");
        Console.WriteLine($"AutomatedRunObserved={r.AutomatedRunObserved} CompletedNaturally={r.CompletedNaturally} PostAllTestTally={r.PostAllTestTally} FramesRun={r.FramesRun}");
        Console.WriteLine($"PASS={r.PassCount} FAIL={r.FailCount} NotRun={r.NotRunCount} Skipped={r.SkippedCount} InProgress={r.InProgressCount} / {r.Tests.Count} total");
        if (r.RetryCount > 0)
            Console.WriteLine($"Auto-skipped {r.RetryCount} additional test(s) after a crash/hang: {string.Join("; ", r.AutoSkippedTests)}");
        Console.WriteLine();
        string? lastSuite = null;
        foreach (var t in r.Tests)
        {
            if (t.Suite != lastSuite) { Console.WriteLine($"-- {t.Suite} --"); lastSuite = t.Suite; }
            string tag = t.Status switch
            {
                AccuracyCoinTestStatus.Pass => "PASS",
                AccuracyCoinTestStatus.Fail => $"FAIL({t.ErrorCode})",
                AccuracyCoinTestStatus.NotRun => "NOTRUN",
                AccuracyCoinTestStatus.Skipped => "SKIP",
                _ => "MIDRUN",
            };
            Console.WriteLine($"  [{tag,-8}] {t.Name} (${t.Address:X4}=${t.RawValue:X2})");
        }
    }

    private static int RunMatrix(byte[] romBytes, List<string> cpus, List<string> ppus, List<string> apus, int waitFrames, string outPath, IReadOnlyCollection<ushort>? preSkip, bool strict = false)
    {
        var combos = new List<(string Cpu, string Ppu, string Apu)>();
        foreach (var c in cpus) foreach (var p in ppus) foreach (var a in apus) combos.Add((c, p, a));
        int total = combos.Count;

        // Each combo is an independent NES instance (CoreRegistry builds a fresh type-map per
        // Bus, verified during Workshop's design research) EXCEPT two known process-wide
        // mutable statics: APU_WF's shared MidiOut/init-attempted flag and APU_SPD2's unsynchronized
        // lazy mix-LUT build (CPU_Z80's shared Random was a third, before that core was retired). Pre-warm both
        // single-threaded before going parallel, so their one-time lazy init can't race.
        PreWarmSharedStatics(romBytes);

        int parallelism = Math.Max(1, Environment.ProcessorCount - 2);
        Console.Error.WriteLine($"Matrix: {cpus.Count} CPU x {ppus.Count} PPU x {apus.Count} APU = {total} combinations, {parallelism}-way parallel");

        var all = new System.Collections.Concurrent.ConcurrentBag<AccuracyCoinRunResult>();
        var sw = Stopwatch.StartNew();
        int done = 0;

        System.Threading.Tasks.Parallel.ForEach(
            combos,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = parallelism },
            combo =>
            {
                var (c, p, a) = combo;
                AccuracyCoinRunResult? result = null;
                try
                {
                    result = AccuracyCoinRunner.RunSingleComboRobust(romBytes, c, p, a, waitFrames, baseSkipAddresses: preSkip, strict: strict);
                    all.Add(result);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  EXCEPTION {c}/{p}/{a}: {ex.Message}");
                }
                int d = System.Threading.Interlocked.Increment(ref done);
                if (d % 25 == 0 || d == total)
                {
                    double perCombo = sw.Elapsed.TotalSeconds / d;
                    double etaSec = perCombo * (total - d);
                    string tag = result == null ? "EXCEPTION" : $"pass={result.PassCount} fail={result.FailCount} crashed={result.Crashed}";
                    Console.Error.WriteLine($"  {d}/{total} ({100.0 * d / total:F1}%) last={c}/{p}/{a} {tag} | {perCombo * 1000:F0}ms/combo avg, ETA {etaSec:F0}s");
                }
                // Checkpoint every 50 completions so a multi-hour run doesn't lose everything
                // if interrupted - overwrites the same file, so the final write is just the
                // last (complete) checkpoint.
                if (d % 50 == 0)
                {
                    try { File.WriteAllText(outPath, JsonSerializer.Serialize(all.ToList(), JsonOpts)); }
                    catch (Exception ex) { Console.Error.WriteLine($"  checkpoint write failed: {ex.Message}"); }
                }
            });

        sw.Stop();
        Console.Error.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F1}s ({all.Count} results).");

        File.WriteAllText(outPath, JsonSerializer.Serialize(all.ToList(), JsonOpts));
        Console.WriteLine($"Wrote {outPath}");
        return 0;
    }

    private static void PreWarmSharedStatics(byte[] romBytes)
    {
        foreach (var apu in new[] { "WF", "MNES", "SPD2" })
        {
            try
            {
                var nes = new NES { RomName = "warmup" };
                nes.LoadROM(romBytes);
                if (nes.SetApuCore(apu)) nes.RunFrame();
            }
            catch { /* best-effort warmup only */ }
        }
    }
}
