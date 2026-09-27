using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes.Workshop.Gb;
using NesEmulator.Gb;

namespace BrokenNes.Workshop.MixLab;

/// <summary>
/// MIX LAB, Game Boy side.
///   gbaudio --rom game.gb|zip [--model dmg|cgb] --apu GB|NES [--nes-apu FIX|SNES|...] [--frames N] [--input ...] --wav out.wav
///   gbpic   --rom game.gb|zip [--model ..] --target NES:FIX|NES:LQ|...|SNES:SFC [--frames N] [--png-at ...] [--input ...] --out-dir D --tag T
///   nes2gb  --rom game.nes [--gb-model dmg|cgb] [--frames N] [--png-at ...] --out-dir D   (NES picture on the Game Boy PPU)
///   snes2gb --rom game.sfc [--gb-model ..] [--frames N] [--png-at ...] --out-dir D        (SNES -> NES -> Game Boy chain)
///   gbcpu   --rom game.gb|zip --speed nes|snes|half|normal ...                            (Game Boy CPU at another console's clock)
///   gbcart  --rom game.gb|zip [--nes-ppu FIX] [--nes-apu FIX] ...                         (a Game Boy inside a NES cartridge)
/// </summary>
internal static class GbMixCli
{
    internal static GbModel Model(string s) => s.Equals("cgb", StringComparison.OrdinalIgnoreCase) ? GbModel.Cgb : GbModel.Dmg;

    public static int Audio(Func<string, string, string> opt)
    {
        string rom = opt("rom", ""), wav = opt("wav", "out.wav");
        MixConfig.GbBackNesApu = opt("nes-apu", "FIX");
        var model = Model(opt("model", "dmg"));
        int frames = int.Parse(opt("frames", "1800"));
        var script = GbRunCli.ParseInput(opt("input", ""));
        string apuId = opt("apu", "NES");
        var board = new BOARD_GB(GbCartridge.Load(GbRunCli.LoadRom(rom)), model, m => GbCores.CreateApu(apuId, m));
        var buf = new short[16384]; var all = new List<short>();
        for (int f = 0; f < frames; f++)
        {
            if (script.TryGetValue(f, out var b)) { board.Buttons = b; board.UpdateJoypadIrq(); }
            board.RunFrame();
            int n; while ((n = board.Apu.ReadSamples(buf)) > 0) for (int i = 0; i + 1 < n; i += 2) all.Add((short)((buf[i] + buf[i + 1]) / 2));
        }
        MixAudioCli.WriteWav(wav, all.ToArray(), board.Apu.SampleRate);
        Console.WriteLine($"{board.Cart.Title} apu={board.Apu.CoreName} frames={frames} samples={all.Count:N0} rate={board.Apu.SampleRate}");
        return 0;
    }

    /// <summary>A Game Boy game drawn by other consoles' PPUs: targets "GB" (native), "NES:&lt;id&gt;", "SNES:&lt;id&gt;".</summary>
    public static int Picture(Func<string, string, string> opt)
    {
        string rom = opt("rom", ""), outDir = opt("out-dir", "."), tag = opt("tag", Path.GetFileNameWithoutExtension(rom));
        var model = Model(opt("model", "dmg"));
        int frames = int.Parse(opt("frames", "900"));
        var pngAt = opt("png-at", frames.ToString()).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        var targets = opt("targets", "GB,NES:FIX,SNES:SFC").Split(',', StringSplitOptions.RemoveEmptyEntries);
        bool bleed = opt("bleed", "1") != "0";
        var script = GbRunCli.ParseInput(opt("input", ""));
        Directory.CreateDirectory(outDir);
        var board = new BOARD_GB(GbCartridge.Load(GbRunCli.LoadRom(rom)), model);
        var cap = new GbLineCapture(board.Ppu);
        var nes = targets.Where(t => t.StartsWith("NES:", StringComparison.OrdinalIgnoreCase)).Select(t => new GbToNes(t[4..]) { Bleed = bleed }).ToList();
        var snes = targets.Where(t => t.StartsWith("SNES:", StringComparison.OrdinalIgnoreCase)).Select(t => new GbToSnes(t[5..]) { Bleed = bleed }).ToList();
        var buf = new short[16384];
        for (int f = 1; f <= frames; f++)
        {
            if (script.TryGetValue(f - 1, out var b)) { board.Buttons = b; board.UpdateJoypadIrq(); }
            board.RunFrame();
            while (board.Apu.ReadSamples(buf) > 0) { }
            if (!pngAt.Contains(f)) continue;
            string stem = Path.Combine(outDir, $"{tag}_f{f:D5}");
            if (targets.Any(t => t.Equals("GB", StringComparison.OrdinalIgnoreCase))) MixLabCli.SaveArgb(board.Ppu.FrameBuffer, 160, 144, stem + "_gb.png");
            foreach (var n in nes)
            {
                var rgba = n.Render(board, cap);
                MixLabCli.SaveRgba(rgba, 256, 240, $"{stem}_nes-{n.PpuId}.png");
                Console.WriteLine($"f{f} NES:{n.PpuId}: {n.Stats}");
            }
            foreach (var s in snes)
            {
                var argb = s.Render(board, cap);
                MixLabCli.SaveArgb(argb, 256, 224, $"{stem}_snes-{s.PpuId}.png");
            }
        }
        Console.WriteLine($"{board.Cart.Title} model={model} frames={frames} targets={string.Join(",", targets)}");
        return 0;
    }
    public static int NesOnGb(Func<string, string, string> opt) => throw new NotImplementedException("nes2gb comes next");
    public static int SnesOnGb(Func<string, string, string> opt) => throw new NotImplementedException("snes2gb comes next");
    public static int CpuSpeed(Func<string, string, string> opt) => throw new NotImplementedException("gbcpu comes next");
    public static int GbOnNesCart(Func<string, string, string> opt) => throw new NotImplementedException("gbcart comes next");
}

internal static class GbMixSweep
{
    public static int Run(Func<string, string, string> opt) => throw new NotImplementedException("gbsweep comes last");
}
