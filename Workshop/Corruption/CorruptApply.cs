using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes2;
using NesEmulator.Corruption;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Corruption;

/// <summary>
/// --corrupt-apply --rom X --stockpile S [--entry id|name|number] [--frames N] [--out dir] [--allow-other-game] [--max-replay N]
/// Starts one entry of a stockpile on a ROM the way the FL plugin does (its anchor, then its writes), runs N more frames, and runs the same anchor without the writes beside it:
/// the report is what the corruption changed (memory by memory, the sound, the picture, and for the SNES what Instrument Runaway would capture).
/// </summary>
internal static class CorruptApply
{
    public static int Run(string[] args)
    {
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string romPath = Opt("rom", ""), stockPath = Opt("stockpile", "");
        if (romPath == "" || stockPath == "") { Console.Error.WriteLine("usage: --corrupt-apply --rom <rom> --stockpile <file> [--entry id|name|number] [--frames N] [--out dir] [--allow-other-game] [--max-replay N]"); return 2; }
        int frames = int.Parse(Opt("frames", "600"));

        var rom = RomDetect.Unwrap(File.ReadAllBytes(romPath), romPath, out string inner);
        var console = RomDetect.Detect(rom, inner) ?? throw new InvalidDataException("not a ROM any console here plays");
        Console.WriteLine($"game: {Path.GetFileName(inner)} ({Consoles.DisplayName(console)}), sha1 {AnchorStart.Sha1Hex(rom)}");

        var read = StockpileJson.LoadFile(stockPath);
        foreach (var w in read.Warnings) Console.WriteLine("warning: " + w);
        foreach (var f in read.Refusals) Console.WriteLine("refused: " + f);
        if (read.Stockpile == null) return 1;
        var sp = read.Stockpile;
        Console.WriteLine($"stockpile: {sp.Entries.Count} entr{(sp.Entries.Count == 1 ? "y" : "ies")}{(read.Legacy ? " (older file format)" : "")}");
        for (int i = 0; i < sp.Entries.Count; i++)
        {
            var e = sp.Entries[i];
            Console.WriteLine($"  {i + 1,3}. {e.Name}  [{e.Game.Console} {e.Game.Title}]  {e.Summary()}  anchor {Describe(e.Anchor)}");
        }
        if (sp.Entries.Count == 0) return 1;

        string which = Opt("entry", "1");
        var entry = int.TryParse(which, out int n) && n >= 1 && n <= sp.Entries.Count ? sp.Entries[n - 1] : sp.Find(which);
        if (entry == null) { Console.Error.WriteLine($"no entry '{which}'"); return 2; }
        Console.WriteLine($"entry: {entry.Name} ({entry.Id})");

        var options = new StartOptions { AllowOtherGame = args.Contains("--allow-other-game"), MaxReplayFrames = int.Parse(Opt("max-replay", "36000")), Status = s => Console.WriteLine("  " + s) };

        // the corrupted machine, and the same anchor without the writes
        var started = AnchorStart.Start(entry, sp, rom, inner, console, null, options);
        foreach (var w in started.Warnings) Console.WriteLine("warning: " + w);
        Console.WriteLine(started.Message);
        if (!started.Ok || started.Session == null) return 1;
        var clean = new StockpileEntry { Id = entry.Id, Name = entry.Name + " (clean)", Game = entry.Game, Machine = entry.Machine, Anchor = entry.Anchor };
        var reference = AnchorStart.Start(clean, sp, rom, inner, console, null, options);
        if (!reference.Ok || reference.Session == null) { Console.WriteLine("the clean run could not be made: " + reference.Message); return 1; }

        using var a = new SessionRun(started.Session);
        using var b = new SessionRun(reference.Session);
        var domains = MachineDomains.For(started.Session);
        var domainsRef = MachineDomains.For(reference.Session);

        Console.WriteLine();
        Console.WriteLine("right after the writes (the machine Instrument Runaway would capture if pressed now):");
        Compare(domains, domainsRef, 5);
        var snapNow = SnesSnapshot(started.Session);
        var snapRef = SnesSnapshot(reference.Session);

        var timeline = new CorruptionTimeline(domains, started.Later);
        for (int f = 0; f < frames; f++)
        {
            var rep = timeline.Tick(f);
            if (rep != null) Console.WriteLine($"  frame +{f}: {rep.Describe()}");
            a.Run(1); b.Run(1);
        }
        Console.WriteLine();
        Console.WriteLine($"after {frames} more frames:");
        Compare(domains, domainsRef, 5);
        Console.WriteLine($"audio:   corrupted {a.AudioHash:X16} (peak {a.Peak})   clean {b.AudioHash:X16} (peak {b.Peak})   {(a.AudioHash == b.AudioHash ? "SAME" : "DIFFERENT")}");
        Console.WriteLine($"picture: corrupted {a.Picture():X16}   clean {b.Picture():X16}   {(a.Picture() == b.Picture() ? "SAME" : "DIFFERENT")}");
        if (snapNow != null && snapRef != null)
        {
            int diffAram = 0; for (int i = 0; i < SpcSnapshot.AramSize; i++) if (snapNow.Aram[i] != snapRef.Aram[i]) diffAram++;
            int diffDsp = 0; for (int i = 0; i < SpcSnapshot.DspSize; i++) if (snapNow.Dsp[i] != snapRef.Dsp[i]) diffDsp++;
            Console.WriteLine($"SpcSnapshot right after the writes: {diffAram} ARAM bytes and {diffDsp} DSP registers differ; instruments listed: corrupted {snapNow.Instruments().Count}, clean {snapRef.Instruments().Count}");
        }
        string outDir = Opt("out", "");
        if (outDir != "")
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "corrupt-apply.txt"), $"entry {entry.Name}\naudio {a.AudioHash:X16} vs {b.AudioHash:X16}\npicture {a.Picture():X16} vs {b.Picture():X16}\n");
        }
        return 0;
    }

    private static string Describe(Anchor a) => a.Kind switch
    {
        AnchorKind.Boot => $"boot({a.Frames:N0} frames)",
        AnchorKind.State => $"state({a.StateData.Length:N0} bytes, {a.StateFormat})",
        AnchorKind.Ref => $"base {a.BaseId}",
        _ => "none",
    } + (a.Alt != null ? " + alt " + Describe(a.Alt) : "");

    private static SpcSnapshot? SnesSnapshot(IConsoleSession s) =>
        s is SnesSession sn && sn.Spc is { } spc ? new SpcSnapshot(spc.Aram, spc.Dsp.Regs) : null;

    private static void Compare(IReadOnlyList<IMemoryDomain> corrupted, IReadOnlyList<IMemoryDomain> clean, int show)
    {
        foreach (var d in corrupted)
        {
            var c = MachineDomains.Find(clean, d.Id);
            if (d.Unavailable != null || c == null || c.Unavailable != null) { Console.WriteLine($"  {d.Id,-7} not available"); continue; }
            int diff = 0; var first = new List<string>();
            for (int i = 0; i < d.Size; i++)
            {
                byte x = d.Peek(i), y = c.Peek(i);
                if (x == y) continue;
                diff++;
                if (first.Count < show) first.Add($"${i:X}: {y:X2}->{x:X2}");
            }
            Console.WriteLine($"  {d.Id,-7} {diff,7:N0} of {d.Size,9:N0} bytes differ" + (first.Count > 0 ? "   " + string.Join(", ", first) : ""));
        }
    }
}
