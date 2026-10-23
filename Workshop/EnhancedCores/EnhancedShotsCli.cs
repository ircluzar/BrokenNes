using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes.Workshop.MixLab;
using NesEmulator;
using NesEmulator.Mix;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.EnhancedCores;

/// <summary>
/// <c>--enhanced-shots --out dir [--label before|after] [--snes rom] [--md rom] [--sms rom] [--nes rom] [--only snes,md,sms,nes]</c>: pictures of real games on the Game Boy picture chips
/// (stock <c>DMG</c>, enhanced <c>DMGS</c>), each with the Game Boy look's default and with the inversions on, and a Genesis game on the NES chip FIX enhanced (<c>FIXS</c>), for a before / after record of the
/// enhanced-cores work. Every console game runs to the same frame once with its own chip, its state is kept, and each picture chip is shown from that state (so every picture of one game is the same frame of it).
/// The NES game runs on the NES board with each picture chip from power-on (the same frames and pad every time). Prints the size and a hash of every picture.
/// </summary>
internal static class EnhancedShotsCli
{
    private sealed record Game(string Key, ConsoleKind Console, string Path, int Frames, string Input);

    private static readonly (string Key, bool Bg, bool Obj)[] Looks = { ("default", false, false), ("invbg", true, false), ("invboth", true, true) };

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string name, string dflt) { int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : dflt; }
        string outDir = Opt("out", "."), label = Opt("label", "shot");
        Directory.CreateDirectory(outDir);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var games = new List<Game>
        {
            new("snes-smw", ConsoleKind.Snes, Opt("snes", @"X:\EMULATION\Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc"), 700, ""),
            new("md-sonic", ConsoleKind.Genesis, Opt("md", @"X:\EMULATION\Sega Genesis\Sonic the Hedgehog (JUE) [!].bin"), 900, "450:Start,460:"),
            new("sms-sonic", ConsoleKind.MasterSystem, Opt("sms", System.IO.Path.Combine(local, @"VRUN_Nes_Dev_work\sega_roster\sms\Sonic The Hedgehog (USA, Europe).sms")), 900, "300:A,310:"),
            new("nes-smb", ConsoleKind.Nes, Opt("nes", @"X:\EMULATION\Nintendo Entertainment System\Super Mario Bros. (JU) (PRG0) [!].nes"), 400, "100:Start,106:"),
        };
        var only = Opt("only", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        int made = 0, failed = 0;
        foreach (var g in games)
        {
            if (only.Length > 0 && !only.Any(o => g.Key.StartsWith(o, StringComparison.OrdinalIgnoreCase))) continue;
            if (!File.Exists(g.Path)) { Console.WriteLine($"SKIP {g.Key}: no ROM at {g.Path}"); continue; }
            try { made += g.Console == ConsoleKind.Nes ? NesGame(g, outDir, label) : SessionGame(g, outDir, label); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL {g.Key}: {ex.GetType().Name}: {ex.Message}"); }
        }
        GbLook.InvertBackground = GbLook.InvertSprites = false;
        Console.WriteLine($"{made} pictures written to {outDir}, {failed} games failed");
        return failed == 0 ? 0 : 1;
    }

    private static Dictionary<int, PadButtons> Script(string spec)
    {
        var d = new Dictionary<int, PadButtons>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':'); PadButtons b = 0;
            if (kv.Length > 1) foreach (var n in kv[1].Split('+', StringSplitOptions.RemoveEmptyEntries)) b |= Enum.Parse<PadButtons>(n, true);
            d[int.Parse(kv[0])] = b;
        }
        return d;
    }

    private static string Hash(uint[] argb, int w, int h)
    {
        ulong hash = 1469598103934665603;
        for (int i = 0; i < w * h; i++) hash = (hash ^ (argb[i] & 0xFFFFFFu)) * 1099511628211;
        return hash.ToString("X16");
    }

    private static int SessionGame(Game g, string outDir, string label)
    {
        GbLook.Palette = GbPaletteChoice.Green; GbLook.InvertBackground = GbLook.InvertSprites = false;
        byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(g.Path), System.IO.Path.GetFileName(g.Path), out string inner);
        string own = CoreCatalog.Default(g.Console, CoreSlot.Ppu);
        using var s = ConsoleSessions.Create(g.Console, rom, CoreCatalog.Default(g.Console, CoreSlot.Cpu), own, CoreCatalog.Default(g.Console, CoreSlot.Apu), null, inner);
        var script = Script(g.Input); var held = PadButtons.None; var buf = new short[32768];
        for (int f = 1; f <= g.Frames - 3; f++)
        {
            if (script.TryGetValue(f - 1, out var h)) held = h;
            s.SetPad(0, held); s.RunFrame();
            while (s.ReadSamples(buf) > 0) { }
        }
        byte[] state = s.SaveState();
        var chips = new List<string> { own, "NES:DMG", "NES:DMGS" };
        if (g.Console == ConsoleKind.Genesis) chips.Add("NES:FIXS");
        int n = 0;
        foreach (var chip in chips)
            foreach (var look in chip == own || chip == "NES:FIXS" ? Looks.Take(1) : Looks)
            {
                s.LoadState(state);
                if (!s.TrySwapCore(CoreSlot.Ppu, chip)) { Console.WriteLine($"  {g.Key}: the PPU menu refused {chip}"); continue; }
                GbLook.InvertBackground = look.Bg; GbLook.InvertSprites = look.Obj;
                for (int f = g.Frames - 2; f <= g.Frames; f++) { s.SetPad(0, held); s.RunFrame(); while (s.ReadSamples(buf) > 0) { } }
                string name = $"{label}_{g.Key}_{chip.Replace("NES:", "nes-").ToLowerInvariant()}_{look.Key}.png";
                MixLabCli.SaveArgb(s.Frame, s.FrameWidth, s.FrameHeight, System.IO.Path.Combine(outDir, name));
                Console.WriteLine($"  {name}: {s.FrameWidth}x{s.FrameHeight} hash={Hash(s.Frame, s.FrameWidth, s.FrameHeight)}");
                n++;
            }
        return n;
    }

    private static int NesGame(Game g, string outDir, string label)
    {
        string[] names = { "A", "B", "SELECT", "START", "UP", "DOWN", "LEFT", "RIGHT" };
        var script = new Dictionary<int, bool[]>();
        foreach (var step in g.Input.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = step.Split(':'); var held = new bool[8];
            foreach (var b in p[1].Split('+', StringSplitOptions.RemoveEmptyEntries)) held[Array.IndexOf(names, b.ToUpperInvariant())] = true;
            script[int.Parse(p[0])] = held;
        }
        int n = 0;
        foreach (var chip in new[] { "FIX", "DMG", "DMGS" })
            foreach (var look in chip == "FIX" ? Looks.Take(1) : Looks)
            {
                GbLook.Palette = GbPaletteChoice.Green; GbLook.InvertBackground = look.Bg; GbLook.InvertSprites = look.Obj;
                var nes = new NES { RomName = System.IO.Path.GetFileName(g.Path) };
                nes.LoadROM(File.ReadAllBytes(g.Path));
                nes.SetCpuCore("FIX"); nes.SetApuCore("FIX");
                if (!nes.SetPpuCore(chip)) { Console.WriteLine($"  {g.Key}: no NES PPU {chip}"); continue; }
                if (chip != "FIX" && nes.GetSpeedConfig() is { } sc) { sc.CpuCyclePrecisePpu = true; sc.NtscAccurateFrameRate = true; }
                var pad = new bool[8];
                for (int f = 0; f < g.Frames; f++)
                {
                    if (script.TryGetValue(f, out var h)) pad = h;
                    nes.SetInputs(pad, null); nes.RunFrame(); nes.GetAudioBuffer();
                }
                var rgba = nes.GetFrameBuffer(); var argb = new uint[256 * 240];
                for (int i = 0; i < argb.Length; i++) argb[i] = 0xFF000000u | (uint)rgba[i * 4] << 16 | (uint)rgba[i * 4 + 1] << 8 | rgba[i * 4 + 2];
                string name = $"{label}_{g.Key}_{chip.ToLowerInvariant()}_{look.Key}.png";
                MixLabCli.SaveArgb(argb, 256, 240, System.IO.Path.Combine(outDir, name));
                Console.WriteLine($"  {name}: 256x240 ppu={nes.GetPpuCoreId()} hash={Hash(argb, 256, 240)}");
                n++;
            }
        return n;
    }
}
