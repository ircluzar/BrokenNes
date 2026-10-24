using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Checks;
using NesEmulator.Systems;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace BrokenNes.Workshop.Debug;

/// <summary>
/// <c>--checks</c> and <c>--profile</c> (BrokenDev milestone D4): a ROM run headless with the hardware checks and the profiler on.
/// <code>
///   --checks  --rom &lt;file&gt; [--sym &lt;file&gt;[,&lt;file&gt;]] [--frames N] [--input "f:Btn+Btn,f:,..."] [--console nes|snes|wms|gb|gbc]
///             [--from F] [--off rule,rule] [--wait symbol,symbol] [--json &lt;file&gt;]
///   --profile (the same options; prints the profile: cycles per frame, lag frames, vblank uploads, DMA bytes, the top functions) [--top N]
/// </code>
/// It replaces the hand-written Mesen Lua profilers. Exit 0; 1 when the checks found anything (--checks); 2 usage.
/// </summary>
internal static class CheckRunCli
{
    public static int Run(string[] args, bool profileOnly)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string romPath = Opt("rom", "");
        if (romPath.Length == 0 || !File.Exists(romPath)) { Console.Error.WriteLine($"usage: {(profileOnly ? "--profile" : "--checks")} --rom <file> [--sym <file>] [--frames N] [--input \"f:Btn,...\"] [--console ...] [--from F] [--json <file>]"); return 2; }
        int frames = int.Parse(Opt("frames", "600"), CultureInfo.InvariantCulture);
        long from = long.Parse(Opt("from", "0"), CultureInfo.InvariantCulture);
        ConsoleKind? console = Opt("console", "") switch
        {
            "nes" => ConsoleKind.Nes, "snes" => ConsoleKind.Snes, "wms" or "widemess" => ConsoleKind.WideMess, "gb" => ConsoleKind.GameBoy, "gbc" => ConsoleKind.GameBoyColor, _ => null,
        };
        if (console == ConsoleKind.WideMess) Consoles.WideMessEnabled = true;
        var opt = new CheckOptions();
        foreach (var r in Opt("off", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) opt.Off.Add(r);
        foreach (var w in Opt("wait", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) opt.FrameWait.Add(w);

        byte[] rom = File.ReadAllBytes(romPath);
        rom = RomDetect.Unwrap(rom, Path.GetFileName(romPath), out string inner);
        using var game = CheckedGame.Open(rom, inner, console);
        foreach (var s in Opt("sym", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Console.WriteLine($"symbols: {game.Dbg.Symbols.Load(s, game.Family)} from {s}");
        var checks = game.Attach(opt);
        var steps = CheckedGame.ParseInput(Opt("input", ""));
        var sw = Stopwatch.StartNew();
        game.Run(frames, steps);
        sw.Stop();
        checks.Finish();
        var p = CheckRun.Summarize(checks, from);
        Console.WriteLine($"{game.Description}: {frames} frames in {sw.Elapsed.TotalSeconds:F1} s ({frames / Math.Max(sw.Elapsed.TotalSeconds, 1e-9):F0} fps with the checks on)");
        if (!profileOnly)
        {
            var list = checks.Findings.OrderBy(f => f.Rule).ThenBy(f => f.FirstFrame).ToList();
            Console.WriteLine($"findings: {list.Count}");
            foreach (var f in list) Console.WriteLine("  " + f);
        }
        foreach (var line in CheckRun.ProfileText(p, int.Parse(Opt("top", "15"), CultureInfo.InvariantCulture))) Console.WriteLine(line);
        foreach (var (name, size, written) in checks.MemoryUse()) Console.WriteLine($"memory {name}: {written} of {size} bytes written ({size - written} never)");
        var (deep, free) = checks.StackUse();
        Console.WriteLine($"stack: deepest {(deep < 0 ? "-" : "$" + deep.ToString("X4"))}, {(free < 0 ? "-" : free.ToString(CultureInfo.InvariantCulture))} bytes free below it");
        foreach (var b in RomBanks.Free(rom, game.Family).Where(b => b.Free < 2048)) Console.WriteLine($"rom bank {b.Index}: {b.Free} of {b.Size} bytes free");
        string csv = Opt("frames-csv", "");
        if (csv.Length > 0) File.WriteAllLines(csv, new[] { "frame,cycles,busy,lag,upload,dma" }.Concat(checks.Frames.Select(f => $"{f.Frame},{f.Cycles},{f.Busy},{(f.Lag ? 1 : 0)},{f.UploadCycles},{f.DmaBytes}")));
        string json = Opt("json", "");
        if (json.Length > 0) File.WriteAllText(json, CheckRun.Json(checks, p).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return profileOnly || !checks.Findings.Any() ? 0 : 1;
    }
}

/// <summary>A game run headless under the checks: the NES (the FIX cores) or a console session, with a pad script.</summary>
internal sealed class CheckedGame : IDisposable
{
    public DebugHost Host { get; }
    public Debugger Dbg => Host.Dbg;
    public string Family => CheckCatalogue.FamilyOf(Host.Machine.Console);
    public string Description => Host.Nes != null ? $"NES {Host.Nes.GetCpuCoreId()}/{Host.Nes.GetPpuCoreId()}/{Host.Nes.GetApuCoreId()}" : Host.Session!.Description;
    private CheckedGame(DebugHost h) { Host = h; }

    public static CheckedGame Open(byte[] rom, string name, ConsoleKind? console = null) => new(DebugHost.Open(rom, name, console));

    public ConsoleChecks Attach(CheckOptions opt) => CheckRun.Attach(Dbg, opt);

    public readonly record struct Step(int Frame, string[] Buttons);

    public static List<Step> ParseInput(string spec)
    {
        var list = new List<Step>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int c = part.IndexOf(':');
            if (c < 0) throw new ArgumentException($"bad input step '{part}' (frame:Buttons)");
            list.Add(new Step(int.Parse(part[..c], CultureInfo.InvariantCulture), part[(c + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }
        return list.OrderBy(s => s.Frame).ToList();
    }

    private static readonly string[] NesOrder = { "A", "B", "Select", "Start", "Up", "Down", "Left", "Right" };

    public void SetButtons(string[] held)
    {
        if (Host.Nes != null)
        {
            var h = new bool[8];
            foreach (var b in held) { int i = Array.FindIndex(NesOrder, n => n.Equals(b, StringComparison.OrdinalIgnoreCase)); if (i < 0) throw new ArgumentException($"unknown NES button '{b}'"); h[i] = true; }
            Host.Nes.SetInputs(h, null);
            return;
        }
        PadButtons p = PadButtons.None;
        foreach (var b in held) p |= Enum.Parse<PadButtons>(b, true);
        Host.Session!.SetPad(0, p);
    }

    /// <summary>Runs the frames on this thread (nothing parks: the checks never break).</summary>
    public void Run(int frames, List<Step> steps)
    {
        int next = 0; string[] held = Array.Empty<string>();
        for (int f = 0; f < frames; f++)
        {
            while (next < steps.Count && steps[next].Frame <= f) { held = steps[next].Buttons; next++; }
            SetButtons(held);
            Host.RunPlain(1);
        }
    }

    public void Dispose() => Host.Dispose();
}
