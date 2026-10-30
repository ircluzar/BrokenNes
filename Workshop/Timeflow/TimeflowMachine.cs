using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Movies;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>Where a recording's input comes from: a pad script, a movie, or a seeded wandering player.</summary>
internal sealed class TimeflowInput
{
    private readonly SortedDictionary<long, PadButtons>? s1, s2;
    private readonly Movie? movie;
    private readonly int seed = -1;
    private PadButtons p1, p2;

    private TimeflowInput(SortedDictionary<long, PadButtons>? a, SortedDictionary<long, PadButtons>? b, Movie? mv, int seed) { s1 = a; s2 = b; movie = mv; this.seed = seed; }

    /// <summary>"auto[:seed]" (a seeded player: Start now and then, walking right / left, A and B), a pad script ("120:Start,132:"), or "" (nothing pressed).</summary>
    public static TimeflowInput Parse(string script1, string script2, string? moviePath)
    {
        if (!string.IsNullOrEmpty(moviePath)) return new TimeflowInput(null, null, Movie.Load(moviePath), -1);
        if (script1.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
        {
            int seed = script1.Length > 5 && int.TryParse(script1[5..], out int s) ? s : 1;
            return new TimeflowInput(null, null, null, seed);
        }
        return new TimeflowInput(Movie.ParseScript(script1), Movie.ParseScript(script2), null, -1);
    }

    public long? MovieFrames => movie?.Frames;

    public (PadButtons, PadButtons) At(long f)
    {
        if (movie != null) return f < movie.Frames ? movie.PadsAt(f) : (PadButtons.None, PadButtons.None);
        if (seed >= 0) return Auto(f);
        if (s1!.TryGetValue(f, out var a)) p1 = a;
        if (s2!.TryGetValue(f, out var b)) p2 = b;
        return (p1, p2);
    }

    // a deterministic wanderer: every 20 frames a new choice from the frame number and the seed; Start pressed briefly every 5 s
    private (PadButtons, PadButtons) Auto(long f)
    {
        if (f % 300 is >= 60 and < 66) return (PadButtons.Start, PadButtons.None);
        ulong h = (ulong)(f / 20) * 0x9E3779B97F4A7C15UL ^ (ulong)seed * 0xBF58476D1CE4E5B9UL;
        h ^= h >> 31; h *= 0x94D049BB133111EBUL; h ^= h >> 29;
        PadButtons p = (h % 4) switch { 0 => PadButtons.Right, 1 => PadButtons.Right, 2 => PadButtons.Left, _ => PadButtons.None };
        if ((h >> 8) % 3 == 0) p |= PadButtons.A;
        if ((h >> 12) % 4 == 0) p |= PadButtons.B;
        if ((h >> 16) % 8 == 0) p |= PadButtons.Up; else if ((h >> 20) % 8 == 0) p |= PadButtons.Down;
        return (p, PadButtons.None);
    }
}

/// <summary>A Workshop machine (the D5 movie machine: every console, its cores, the debugger with no hook armed) with its Timeflow source.</summary>
internal static class TimeflowMachines
{
    public static TimeflowSource SourceFor(MovieMachine m) =>
        m.Host.Nes != null ? TimeflowSource.For(m.Host.Nes, () => m.Cores) : TimeflowSource.For(m.Host.Session!, () => m.Cores);

    /// <summary>Opens the machine a timeline was recorded on: its ROM from the store, its console, cores, power-on and timing.</summary>
    public static MovieMachine OpenFor(string timelineDir, TimelineMeta meta, bool debugger, IEnumerable<string>? symbols = null)
    {
        byte[] rom = TimeflowStore.RomOf(timelineDir, meta);
        // the movie machine opens a file: a scratch copy of the stored ROM with its own name
        string tmp = Path.Combine(Path.GetTempPath(), "brokennes-timeflow-" + Environment.ProcessId, meta.RomSha256[..16], meta.RomName.Length > 0 ? meta.RomName : "rom" + meta.RomExt);
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        if (!File.Exists(tmp)) File.WriteAllBytes(tmp, rom);
        return MovieMachine.Open(tmp, meta.Console, meta.Cores, meta.Console == "nes" ? meta.PowerOn : "fceux", meta.Ntsc, debugger, symbols);
    }
}
