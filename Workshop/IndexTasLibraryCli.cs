using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BrokenNes.Workshop.Tas;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// Builds ground-truth (movie, rom) pairings for the FCEUX-vs-BrokenNes comparison harness by
/// checksum, not by filename. The TAS/*.bat launcher scripts pair games by name, but a movie's
/// header carries the exact MD5 of PRG+CHR it was recorded against (see fm2.txt's "romChecksum"),
/// and a ROM library can hold a different revision/dump of "the same" game under an identical or
/// similar filename - confirmed for real on Abadox (2194M): the movie expects MD5
/// 84f06d39c96d4b875ba558654cd1b464, the library's "Abadox (U).nes" is c096127c895bb7904c810007119a0225.
/// A name-based pairing would silently run the wrong revision (or hang FCEUX on its own
/// ROM-mismatch confirmation dialog, which nothing auto-dismisses in a scripted run).
///
/// Usage: --index-tas-library --roms-dir <dir> --movies-dir <dir> --out manifest.json
/// </summary>
internal static class IndexTasLibraryCli
{
    public static int Run(string[] args)
    {
        string? romsDir = null, moviesDir = null, outPath = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--roms-dir": romsDir = args[++i]; break;
                case "--movies-dir": moviesDir = args[++i]; break;
                case "--out": outPath = args[++i]; break;
            }
        }
        if (romsDir == null || moviesDir == null || outPath == null)
        {
            Console.Error.WriteLine("Usage: --index-tas-library --roms-dir <dir> --movies-dir <dir> --out manifest.json");
            return 2;
        }

        _ = new NES(); // force CoreRegistry init, same rationale as AccuracyCoinCli

        Console.Error.WriteLine("Indexing ROMs...");
        var romsByMd5 = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int romFail = 0;
        var romFiles = Directory.GetFiles(romsDir, "*.nes", SearchOption.TopDirectoryOnly);
        foreach (var romPath in romFiles)
        {
            try
            {
                var nes = new NES { RomName = Path.GetFileName(romPath) };
                nes.LoadROM(File.ReadAllBytes(romPath));
                string md5 = Convert.ToHexString(nes.ComputeRomMd5()).ToLowerInvariant();
                if (!romsByMd5.TryGetValue(md5, out var list)) romsByMd5[md5] = list = new List<string>();
                list.Add(romPath);
            }
            catch { romFail++; }
        }
        Console.Error.WriteLine($"  {romFiles.Length} files, {romsByMd5.Count} distinct MD5s, {romFail} failed to load.");

        Console.Error.WriteLine("Indexing movies...");
        var movieFiles = Directory.GetFiles(moviesDir, "*.fm2", SearchOption.TopDirectoryOnly);
        var matched = new List<MoviePairing>();
        var unmatched = new List<string>();
        int movieFail = 0;
        foreach (var moviePath in movieFiles)
        {
            try
            {
                var (header, frames) = Fm2Movie.Load(moviePath);
                if (header.RomChecksumMd5 == null)
                {
                    unmatched.Add(Path.GetFileName(moviePath));
                    continue;
                }
                string md5 = Convert.ToHexString(header.RomChecksumMd5).ToLowerInvariant();
                if (romsByMd5.TryGetValue(md5, out var candidates))
                {
                    matched.Add(new MoviePairing(Path.GetFileName(moviePath), header.RomFilename, md5, candidates[0], frames.Count));
                }
                else
                {
                    unmatched.Add(Path.GetFileName(moviePath));
                }
            }
            catch (Exception ex) { movieFail++; Console.Error.WriteLine($"  PARSE FAIL {Path.GetFileName(moviePath)}: {ex.GetType().Name}: {ex.Message}"); }
        }
        Console.Error.WriteLine($"  {movieFiles.Length} files, {matched.Count} matched, {unmatched.Count} unmatched, {movieFail} failed to parse.");

        var manifest = new TasLibraryManifest(
            RomsDir: romsDir, MoviesDir: moviesDir,
            TotalRoms: romFiles.Length, DistinctRomMd5s: romsByMd5.Count, RomLoadFailures: romFail,
            TotalMovies: movieFiles.Length, MovieParseFailures: movieFail,
            Matched: matched, Unmatched: unmatched);

        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outPath, json);
        Console.WriteLine($"Wrote {outPath}: {matched.Count} matched pairs, {unmatched.Count} unmatched movies.");
        return 0;
    }
}

internal sealed record MoviePairing(string Movie, string MovieRomFilename, string ExpectedMd5, string RomPath, int FrameCount);

internal sealed record TasLibraryManifest(
    string RomsDir, string MoviesDir,
    int TotalRoms, int DistinctRomMd5s, int RomLoadFailures,
    int TotalMovies, int MovieParseFailures,
    List<MoviePairing> Matched, List<string> Unmatched);
