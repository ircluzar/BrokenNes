using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BrokenNes.Workshop.Tas;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// The BrokenNes-side counterpart to run_fceux_baseline.ps1: replays every checksum-matched
/// (movie, rom) pair from IndexTasLibraryCli's manifest through the FIX cores, dumping a
/// nesreflex-raw-v2 trace for each and classifying whether it played the whole movie cleanly
/// (OK), crashed partway (CRASHED), or fell short some other way (INCOMPLETE). Runs all games
/// in one process (unlike the FCEUX side, which needs a fresh GUI process per game) since
/// BrokenNes's headless CLI has no such requirement - much faster for 192 games.
///
/// Usage: --tas-baseline-batch --manifest manifest.json --movies-dir <dir> --dump-dir <dir>
///        --out results.csv [--cpu FIX --ppu FIX --apu FIX]
/// </summary>
internal static class TasBaselineBatchCli
{
    public static int Run(string[] args)
    {
        string? manifestPath = null, moviesDir = null, dumpDir = null, outCsv = null;
        string cpu = "FIX", ppu = "FIX", apu = "FIX";
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--manifest": manifestPath = args[++i]; break;
                case "--movies-dir": moviesDir = args[++i]; break;
                case "--dump-dir": dumpDir = args[++i]; break;
                case "--out": outCsv = args[++i]; break;
                case "--cpu": cpu = args[++i]; break;
                case "--ppu": ppu = args[++i]; break;
                case "--apu": apu = args[++i]; break;
            }
        }
        if (manifestPath == null || moviesDir == null || dumpDir == null || outCsv == null)
        {
            Console.Error.WriteLine("Usage: --tas-baseline-batch --manifest manifest.json --movies-dir <dir> --dump-dir <dir> --out results.csv [--cpu FIX --ppu FIX --apu FIX]");
            return 2;
        }
        Directory.CreateDirectory(dumpDir);
        _ = new NES();

        var manifest = JsonSerializer.Deserialize<TasLibraryManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("manifest parsed to null");

        var rows = new List<string> { "Index,Movie,Rom,ExpectedFrames,FramesWritten,ExitReason,Status,ElapsedSec,Error" };
        int idx = -1, okCount = 0, crashCount = 0, incompleteCount = 0, errorCount = 0;
        foreach (var g in manifest.Matched)
        {
            idx++;
            string status = "UNKNOWN", exitReason = "", error = "";
            int framesWritten = -1;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string fm2Path = Path.Combine(moviesDir, g.Movie);
                var (header, frames) = Fm2Movie.Load(fm2Path);
                byte[] romBytes = File.ReadAllBytes(g.RomPath);

                var nes = new NES { RomName = Path.GetFileName(g.RomPath) };
                nes.LoadROM(romBytes);
                if (!nes.SetCpuCore(cpu) || !nes.SetPpuCore(ppu) || !nes.SetApuCore(apu))
                    throw new InvalidOperationException($"Unknown core combo {cpu}/{ppu}/{apu}");

                string dumpPath = Path.Combine(dumpDir, g.Movie + ".raw");
                using (var dumper = new NesReflexDumpWriter(dumpPath))
                {
                    int framesPlayed = 0;
                    bool crashedMidMovie = false;
                    for (; framesPlayed < frames.Count; framesPlayed++)
                    {
                        var frame = frames[framesPlayed];
                        if (frame.Reset) nes.Reset();
                        nes.SetInputs(frame.P1, frame.P2);
                        nes.RunFrame();
                        dumper.WriteFrame(nes, framesPlayed, (byte)(frame.Reset ? 1 : 0));
                        if (nes.IsCrashed()) { crashedMidMovie = true; break; }
                    }
                    framesWritten = dumper.FramesWritten;
                    exitReason = crashedMidMovie ? "crashed" : framesPlayed >= frames.Count ? "movie_finished" : "unknown_stop";
                }

                if (exitReason == "movie_finished" && framesWritten >= frames.Count) { status = "OK"; okCount++; }
                else if (exitReason == "crashed") { status = "CRASHED"; crashCount++; }
                else { status = "INCOMPLETE"; incompleteCount++; }
            }
            catch (Cartridge.UnsupportedMapperException ume)
            {
                status = "UNSUPPORTED_MAPPER"; error = $"mapper {ume.MapperId} ({ume.MapperName})"; errorCount++;
            }
            catch (Exception ex)
            {
                status = "ERROR"; error = ex.Message.Replace(',', ';'); errorCount++;
            }
            sw.Stop();

            Console.WriteLine($"[{idx}/{manifest.Matched.Count}] {g.Movie} -> {status} (frames {framesWritten}/{g.FrameCount}, {sw.Elapsed.TotalSeconds:F1}s)");
            rows.Add(string.Join(",", new[] {
                idx.ToString(), g.Movie, Path.GetFileName(g.RomPath), g.FrameCount.ToString(),
                framesWritten.ToString(), exitReason, status, sw.Elapsed.TotalSeconds.ToString("F2"), error
            }.Select(CsvField)));
        }

        File.WriteAllLines(outCsv, rows);
        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"OK: {okCount}");
        Console.WriteLine($"CRASHED: {crashCount}");
        Console.WriteLine($"INCOMPLETE: {incompleteCount}");
        Console.WriteLine($"ERROR/UNSUPPORTED: {errorCount}");
        Console.WriteLine($"Wrote {outCsv}");
        return 0;

        static string CsvField(string s) =>
            s.Contains(',') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
