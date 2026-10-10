using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BrokenNes.Workshop.MixLab;
using NesEmulator;
using NesEmulator.Mix;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--picture-bridge</c>: the lab for the picture bridges, one game at a time, with the fidelity numbers and the bridge's own counters.
/// <code>
///   --picture-bridge nes   --rom a.nes --ppu SMS|MD [--frames N] [--input "f:A+Start,f:"] [--every K] [--png-at a,b] [--out dir] [--tag t]
///   --picture-bridge sega  --rom a.sms|a.gg|a.md --ppu NES:FIXS [--frames N] [--input ...] [--every K] [--png-at a,b] [--out dir]
/// </code>
/// <c>nes</c>: a NES game on a Sega picture chip (PPU_SMS / PPU_MD); the NES chip in front draws the picture a NES would show, the Sega chip draws the bridged one, and the two are compared every K frames.
/// <c>sega</c>: a Sega game on a NES (or Game Boy / SNES, through the NES) picture chip; the Sega chip's own picture is the reference. PNGs are reference | bridge side by side.
/// </summary>
internal static class PictureBridgeCli
{
    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        try
        {
            switch (mode)
            {
                case "nes": return NesOnSega(Opt);
                case "sega": return SegaOnNes(Opt);
                default: Console.Error.WriteLine("usage: --picture-bridge nes|sega ..."); return 2;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 5; }
    }

    // ------------------------------------------------------------------------------------------------------------------ a NES game on a Sega chip
    private static int NesOnSega(Func<string, string, string> opt)
    {
        string rom = opt("rom", ""), ppu = opt("ppu", "MD"), outDir = opt("out", "."), tag = opt("tag", Path.GetFileNameWithoutExtension(rom) + "_" + ppu);
        int frames = int.Parse(opt("frames", "600")), every = int.Parse(opt("every", "10"));
        var pngAt = opt("png-at", frames.ToString()).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        var script = MixLabCli.ParseNesInput(opt("input", ""));
        Directory.CreateDirectory(outDir);
        var nes = new NES { RomName = Path.GetFileName(rom) };
        nes.LoadROM(File.ReadAllBytes(rom));
        if (!nes.SetPpuCore(ppu) || !nes.GetPpuCoreId().EndsWith("_" + ppu, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"REFUSED: PPU {ppu} not applied (got {nes.GetPpuCoreId()})"); return 3; }
        var bus = (Bus)typeof(NES).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(nes)!;
        if (bus.ppu is not NesFrontedPpu core) { Console.WriteLine($"PPU {ppu} is not a Sega picture bridge"); return 3; }
        var held = new bool[8]; var res = new List<PictureMetrics.Result>(); int crashed = -1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 1; f <= frames; f++)
        {
            if (script.TryGetValue(f - 1, out var h)) held = h;
            nes.SetInputs(held, null);
            nes.RunFrame();
            if (nes.IsCrashed()) { crashed = f; break; }
            if (f > 30 && f % every == 0)
            {
                var m = PictureMetrics.Compare(core.ReferenceCanvas, core.BridgeCanvas, 256, 0, 0, 256, 240);
                res.Add(m);
            }
            if (pngAt.Contains(f)) SideBySide(core.ReferenceCanvas, core.BridgeCanvas, 256, 240, Path.Combine(outDir, $"{tag}_f{f:D5}.png"));
        }
        Console.WriteLine($"{tag}: {Path.GetFileName(rom)} on {core.CoreName}, {frames} frames in {sw.Elapsed.TotalSeconds:F1}s{(crashed > 0 ? $"  CRASHED at {crashed}: {nes.GetCrashInfo()}" : "")}");
        Report(res);
        Console.WriteLine($"  counters: {core.Counters}");
        return crashed > 0 ? 1 : 0;
    }

    // ------------------------------------------------------------------------------------------------------------------ a Sega game on a NES chip
    private static int SegaOnNes(Func<string, string, string> opt)
    {
        string romPath = opt("rom", ""), ppu = opt("ppu", "NES:FIX"), outDir = opt("out", "."), tag = opt("tag", Path.GetFileNameWithoutExtension(romPath) + "_" + ppu.Replace(':', '-'));
        int frames = int.Parse(opt("frames", "600")), every = int.Parse(opt("every", "10"));
        var pngAt = opt("png-at", frames.ToString()).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        var script = ParseInput(opt("input", ""));
        Directory.CreateDirectory(outDir);
        byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(romPath), romPath, out string inner);
        var console = opt("console", "") is { Length: > 0 } c ? Consoles.FromKey(c) : RomDetect.Detect(rom, inner) ?? throw new InvalidDataException("unknown ROM");
        using var s = ConsoleSessions.Create(console, rom, CoreCatalog.Default(console, CoreSlot.Cpu), ppu, CoreCatalog.Default(console, CoreSlot.Apu), null, inner);
        Console.WriteLine($"{tag}: {s.Description}");
        var info = s as IPictureBridgeInfo;
        var res = new List<PictureMetrics.Result>(); var held = PadButtons.None; var buf = new short[65536];
        uint[]? prev = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 1; f <= frames; f++)
        {
            if (script.TryGetValue(f - 1, out var h)) held = h;
            s.SetPad(0, held);
            s.RunFrame();
            while (s.ReadSamples(buf) > 0) { }
            if (info != null)
            {
                var (rx, ry, rw, rh) = info.Region;
                if (f > 30 && f % every == 0)
                {
                    // a Genesis frame's picture is made from the state its NEXT frame draws from: compare the bridge of the frame before with the reference of this one when the reference is not the same frame
                    var bridge = info.ReferenceIsSameFrame ? info.BridgeCanvas : prev;
                    if (bridge != null) res.Add(PictureMetrics.Compare(info.ReferenceCanvas, bridge, info.CanvasWidth, rx, ry, rw, rh));
                }
                if (pngAt.Contains(f)) SideBySide(info.ReferenceCanvas, info.BridgeCanvas, info.CanvasWidth, info.CanvasHeight, Path.Combine(outDir, $"{tag}_f{f:D5}.png"));
                if (!info.ReferenceIsSameFrame) prev = (uint[])info.BridgeCanvas.Clone();
            }
            else if (pngAt.Contains(f)) MixLabCli.SaveArgb(s.Frame, s.FrameWidth, s.FrameHeight, Path.Combine(outDir, $"{tag}_f{f:D5}.png"));
        }
        Console.WriteLine($"  {frames} frames in {sw.Elapsed.TotalSeconds:F1}s ({frames / sw.Elapsed.TotalSeconds:F0} fps)");
        if (info != null) { Report(res); Console.WriteLine($"  {info.BridgeName}: {info.Counters}"); }
        return 0;
    }

    private static void Report(List<PictureMetrics.Result> res)
    {
        if (res.Count == 0) { Console.WriteLine("  no fidelity samples"); return; }
        double Avg(Func<PictureMetrics.Result, double> f) => res.Average(f);
        Console.WriteLine($"  fidelity over {res.Count} frames: colour error {Avg(r => r.ColourError):F1}%  block error {Avg(r => r.BlockError):F1}%  structure (SSIM) {Avg(r => r.Ssim):F3} (worst {res.Min(r => r.Ssim):F3})  match {Avg(r => r.Match) * 100:F0}%  recognisable {res.Count(r => r.Recognisable)}/{res.Count}");
    }

    internal static void SideBySide(uint[] a, uint[] b, int w, int h, string path)
    {
        var both = new uint[2 * w * h];
        for (int y = 0; y < h; y++) { Array.Copy(a, y * w, both, y * 2 * w, w); Array.Copy(b, y * w, both, y * 2 * w + w, w); }
        MixLabCli.SaveArgb(both, 2 * w, h, path);
    }

    internal static Dictionary<int, PadButtons> ParseInput(string spec)
    {
        var d = new Dictionary<int, PadButtons>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':'); PadButtons b = 0;
            if (kv.Length > 1) foreach (var name in kv[1].Split('+', StringSplitOptions.RemoveEmptyEntries)) b |= Enum.Parse<PadButtons>(name, true);
            d[int.Parse(kv[0])] = b;
        }
        return d;
    }
}
