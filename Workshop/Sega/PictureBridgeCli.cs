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
                case "sega": case "session": return SegaOnNes(Opt);
                case "lockstep": return Lockstep(Opt);
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

    /// <summary>A NES game on PPU_FIX and on a bridge core, frame by frame: where does the game's own state (system RAM) first differ? (A bridge must leave the game alone.)</summary>
    private static int Lockstep(Func<string, string, string> opt)
    {
        string rom = opt("rom", ""), ppu = opt("ppu", "SMS"); int frames = int.Parse(opt("frames", "600"));
        var script = MixLabCli.ParseNesInput(opt("input", ""));
        NES Make(string id) { var n = new NES { RomName = Path.GetFileName(rom) }; n.LoadROM(File.ReadAllBytes(rom)); if (!n.SetPpuCore(id)) throw new Exception("not applied"); return n; }
        var a = Make("FIX"); var b = Make(ppu); var held = new bool[8]; int frameDiffs = 0, lagDiffs = 0; byte[]? prevA = null;
        for (int f = 1; f <= frames; f++)
        {
            if (script.TryGetValue(f - 1, out var h)) held = h;
            a.SetInputs(held, null); b.SetInputs(held, null); a.RunFrame(); b.RunFrame();
            if (b.GetPictureBridge() is NesFrontedPpu core && f > 30)
            {
                var fa = a.GetFrameBuffer(); var fb = core.FrontFrame;
                if (!fa.AsSpan().SequenceEqual(fb)) { frameDiffs++; if (prevA != null && prevA.AsSpan().SequenceEqual(fb)) lagDiffs++; }
                prevA = (byte[])fa.Clone();
            }
            ulong ha = 14695981039346656037UL, hb = ha;
            for (int i = 0; i < 2048; i++) { ha = (ha ^ a.PeekSystemRam(i)) * 1099511628211UL; hb = (hb ^ b.PeekSystemRam(i)) * 1099511628211UL; }
            if (ha != hb)
            {
                int first = -1; for (int i = 0; i < 2048; i++) if (a.PeekSystemRam(i) != b.PeekSystemRam(i)) { first = i; break; }
                Console.WriteLine($"first RAM difference at frame {f}: byte ${first:X3} FIX={a.PeekSystemRam(first):X2} {ppu}={b.PeekSystemRam(first):X2}; scanline/dot FIX {string.Join('/', a.GetPpuTiming().Scanline, a.GetPpuTiming().Dot)} {ppu} {string.Join('/', b.GetPpuTiming().Scanline, b.GetPpuTiming().Dot)}");
                return 1;
            }
        }
        Console.WriteLine($"{frames} frames: system RAM identical on PPU_FIX and PPU_{ppu}; the front chip's completed frame differs from PPU_FIX's on {frameDiffs} frames ({lagDiffs} of them equal to PPU_FIX's frame of the call before)");
        return 0;
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
        var info = FindBridgeInfo(s);
        var res = new List<PictureMetrics.Result>(); var floors = new List<PictureMetrics.Result>(); var held = PadButtons.None; var buf = new short[65536];
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
                    if (bridge != null)
                    {
                        res.Add(PictureMetrics.Compare(info.ReferenceCanvas, bridge, info.CanvasWidth, rx, ry, rw, rh));
                        floors.Add(PictureMetrics.Compare(info.ReferenceCanvas, NesGamut(info.ReferenceCanvas), info.CanvasWidth, rx, ry, rw, rh));
                    }
                }
                if (pngAt.Contains(f))
                {
                    SideBySide(info.ReferenceCanvas, info.BridgeCanvas, info.CanvasWidth, info.CanvasHeight, Path.Combine(outDir, $"{tag}_f{f:D5}.png"));
                    foreach (var pr in opt("probe", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var xy = pr.Split(','); int px = int.Parse(xy[0]), py = int.Parse(xy[1]);
                        Console.WriteLine($"  probe ({px},{py}): reference {info.ReferenceCanvas[py * info.CanvasWidth + px] & 0xFFFFFF:X6} bridge {info.BridgeCanvas[py * info.CanvasWidth + px] & 0xFFFFFF:X6}");
                    }
                }
                if (!info.ReferenceIsSameFrame) prev = (uint[])info.BridgeCanvas.Clone();
            }
            else if (pngAt.Contains(f)) MixLabCli.SaveArgb(s.Frame, s.FrameWidth, s.FrameHeight, Path.Combine(outDir, $"{tag}_f{f:D5}.png"));
        }
        Console.WriteLine($"  {frames} frames in {sw.Elapsed.TotalSeconds:F1}s ({frames / sw.Elapsed.TotalSeconds:F0} fps)");
        if (info != null)
        {
            Report(res);
            if (floors.Count > 0) Console.WriteLine($"  gamut floor (every pixel of the source drawn in its nearest NES colour, no tile or palette limits): colour error {floors.Average(r => r.ColourError):F1}%  block error {floors.Average(r => r.BlockError):F1}%  structure {floors.Average(r => r.Ssim):F3}");
            Console.WriteLine($"  {info.BridgeName}: {info.Counters}");
        }
        return 0;
    }

    /// <summary>A picture with every pixel replaced by the nearest colour the NES chips can draw (the floor of any NES bridge's colour error).</summary>
    internal static uint[] NesGamut(uint[] picture)
    {
        var o = new uint[picture.Length]; var cache = new Dictionary<uint, uint>();
        for (int i = 0; i < picture.Length; i++)
        {
            uint c = picture[i] & 0xFFFFFF;
            if (!cache.TryGetValue(c, out uint n)) { int idx = NesFixPalette.Nearest((int)(c >> 16), (int)(c >> 8 & 0xFF), (int)(c & 0xFF)); n = 0xFF000000u | (uint)NesFixPalette.Rgb(idx); cache[c] = n; }
            o[i] = n;
        }
        return o;
    }

    private static void Report(List<PictureMetrics.Result> res)
    {
        if (res.Count == 0) { Console.WriteLine("  no fidelity samples"); return; }
        double Avg(Func<PictureMetrics.Result, double> f) => res.Average(f);
        Console.WriteLine($"  fidelity over {res.Count} frames: colour error {Avg(r => r.ColourError):F1}%  block error {Avg(r => r.BlockError):F1}%  structure (SSIM) {Avg(r => r.Ssim):F3} (worst {res.Min(r => r.Ssim):F3})  match {Avg(r => r.Match) * 100:F0}%  recognisable {res.Count(r => r.Recognisable)}/{res.Count}");
    }

    /// <summary>The picture bridge a session's picture crosses: the Sega wrapper itself, or the Sega-chip NES core inside a Game Boy / SNES session's NES hop (found by reflection: a tool, not product code).</summary>
    internal static IPictureBridgeInfo? FindBridgeInfo(IConsoleSession s)
    {
        if (s is IPictureBridgeInfo direct) return direct;
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        foreach (var name in new[] { "toNes", "down" })
        {
            var hop = s.GetType().GetField(name, F)?.GetValue(s);
            var sink = hop?.GetType().GetField("sink", F)?.GetValue(hop);
            if (sink?.GetType().GetProperty("Ppu", F)?.GetValue(sink) is IPictureBridgeInfo info) return info;
        }
        return null;
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
