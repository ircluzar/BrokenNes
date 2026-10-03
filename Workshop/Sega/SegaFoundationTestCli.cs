using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--sega-test</c>: headless checks of the Sega foundation (plumbing only; the chips have their own verifiers).
///   1. With the preview OFF the Sega consoles are invisible and everything that existed before behaves exactly as before (menus, extensions, detection).
///   2. With the preview ON: ROM detection for all three machines (headers, extension fallbacks, the interleaved .smd, the copier header, 32X refused),
///      the catalog ids, key round-trips, the region-parameterised timing arithmetic, and the placeholder session (size, rate, pad, silence).
/// Exit code 0 = every check passed.
/// </summary>
internal static class SegaFoundationTestCli
{
    private static int failures, passes;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 && !ok ? "  -> " + detail : "")}");
    }

    public static int Run(string[] args)
    {
        bool initialPreview = Consoles.SegaPreview;
        try
        {
            Console.WriteLine("== preview OFF: the Sega consoles are invisible and nothing else changed");
            Consoles.SegaPreview = false;
            Hidden();
            Console.WriteLine("== preview ON");
            Consoles.SegaPreview = true;
            Detection();
            Catalog();
            Keys();
            Timing();
            Placeholder();
            Console.WriteLine("== preview OFF again: refusing, and the original behaviour is back");
            Consoles.SegaPreview = false;
            Hidden();
        }
        finally { Consoles.SegaPreview = initialPreview; }
        Console.WriteLine($"\n{passes} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------- synthetic ROMs

    private static byte[] Sms(int region, int size = 0x10000)
    {
        var rom = new byte[size];
        "TMR SEGA"u8.CopyTo(rom.AsSpan(0x7FF0));
        rom[0x7FFF] = (byte)(region << 4 | 0x0C);
        return rom;
    }

    private static byte[] Genesis(string consoleName = "SEGA MEGA DRIVE ", int size = 0x8000)
    {
        var rom = new byte[size];
        System.Text.Encoding.ASCII.GetBytes(consoleName.PadRight(16)).CopyTo(rom, 0x100);
        for (int i = 0x200; i < size; i++) rom[i] = (byte)(i * 31 + 7);   // something that is not constant, so de-interleaving can be wrong
        return rom;
    }

    /// <summary>A Genesis ROM in Super Magic Drive format (the inverse of NormalizeGenesis).</summary>
    private static byte[] Interleave(byte[] rom)
    {
        int blocks = rom.Length / 0x4000;
        var smd = new byte[0x200 + blocks * 0x4000];
        smd[0] = (byte)blocks; smd[1] = 3; smd[8] = 0xAA; smd[9] = 0xBB;
        for (int b = 0; b < blocks; b++)
            for (int i = 0; i < 0x2000; i++)
            {
                smd[0x200 + b * 0x4000 + i] = rom[b * 0x4000 + 2 * i + 1];
                smd[0x200 + b * 0x4000 + 0x2000 + i] = rom[b * 0x4000 + 2 * i];
            }
        return smd;
    }

    private static byte[] Snes()
    {
        var rom = new byte[0x8000];
        int a = 0x7FC0;                                       // LoROM header: checksum + complement = $FFFF
        rom[a + 0x1C] = 0x34; rom[a + 0x1D] = 0x12; rom[a + 0x1E] = 0xCB; rom[a + 0x1F] = 0xED;
        return rom;
    }

    private static byte[] Nes()
    {
        var rom = new byte[16 + 0x4000 + 0x2000];
        "NES\x1A"u8.CopyTo(rom);
        return rom;
    }

    private static byte[] GameBoy(bool color)
    {
        var rom = new byte[0x8000];
        new byte[] { 0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B }.CopyTo(rom, 0x104);
        rom[0x143] = (byte)(color ? 0x80 : 0);
        return rom;
    }

    private static byte[] Zip(string entryName, byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = z.CreateEntry(entryName);
            using var s = e.Open(); s.Write(data);
        }
        return ms.ToArray();
    }

    // ---------------------------------------------------------------------------------------------- the checks

    private static void Hidden()
    {
        var orig = new[] { ConsoleKind.Nes, ConsoleKind.Snes, ConsoleKind.GameBoy, ConsoleKind.GameBoyColor };
        Check("Consoles.All lists only the four finished consoles, in the original order", Consoles.All.SequenceEqual(orig), string.Join(",", Consoles.All));
        var exts = new[] { ".nes", ".sfc", ".smc", ".gb", ".gbc", ".zip" };
        Check("AllRomExtensions is exactly the original six", Consoles.AllRomExtensions.SequenceEqual(exts), string.Join(" ", Consoles.AllRomExtensions));
        Check("a Sega ROM is not recognised", RomDetect.Detect(Sms(4), "game.sms") == null && RomDetect.Detect(Genesis(), "game.md") == null && RomDetect.Detect(Sms(6), "game.gg") == null);
        Check("the other consoles are detected as before",
            RomDetect.Detect(Nes(), "a.nes") == ConsoleKind.Nes && RomDetect.Detect(Snes(), "a.sfc") == ConsoleKind.Snes
            && RomDetect.Detect(GameBoy(false), "a.gb") == ConsoleKind.GameBoy && RomDetect.Detect(GameBoy(true), "a.gbc") == ConsoleKind.GameBoyColor
            && RomDetect.Detect(Snes(), "noextension") == ConsoleKind.Snes);
        Check("a zip holding a Sega ROM is not unwrapped", Throws<InvalidDataException>(() => RomDetect.Unwrap(Zip("game.md", Genesis()), "x.zip", out _)));
        bool refused = true;
        foreach (var k in new[] { ConsoleKind.MasterSystem, ConsoleKind.GameGear, ConsoleKind.Genesis })
            refused &= Throws<NotSupportedException>(() => ConsoleSessions.Create(k, Genesis(), "", "", ""));
        Check("no Sega session can be created", refused);
        Check("no Sega console is ready yet (a Ready console must have a real session)",
            !Consoles.IsReady(ConsoleKind.MasterSystem) && !Consoles.IsReady(ConsoleKind.GameGear) && !Consoles.IsReady(ConsoleKind.Genesis));
    }

    private static bool Throws<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } catch { return false; } }

    private static void Detection()
    {
        Check("Consoles.All now lists seven consoles", Consoles.All.Length == 7, string.Join(",", Consoles.All));
        Check("AllRomExtensions gained the Sega extensions", new[] { ".sms", ".sg", ".sc", ".gg", ".md", ".gen", ".smd", ".bin" }.All(e => Consoles.AllRomExtensions.Contains(e)) && Consoles.AllRomExtensions[^1] == ".zip");

        Check("SMS header, region 4 (export) -> Master System", RomDetect.Detect(Sms(4), "a.bin") == ConsoleKind.MasterSystem);
        Check("SMS header, region 3 (Japan) -> Master System", RomDetect.Detect(Sms(3), "a.sms") == ConsoleKind.MasterSystem);
        Check("GG header, regions 5, 6 and 7 -> Game Gear", new[] { 5, 6, 7 }.All(r => RomDetect.Detect(Sms(r), "a.bin") == ConsoleKind.GameGear));
        Check("a Game Gear ROM named .sms is still a Game Gear (the header decides)", RomDetect.Detect(Sms(6), "a.sms") == ConsoleKind.GameGear);
        var small = new byte[0x4000]; "TMR SEGA"u8.CopyTo(small.AsSpan(0x3FF0)); small[0x3FFF] = 0x4C;
        Check("the header at $3FF0 (a 16 KB ROM) is found", RomDetect.Detect(small, "a.bin") == ConsoleKind.MasterSystem);
        var tiny = new byte[0x2000]; "TMR SEGA"u8.CopyTo(tiny.AsSpan(0x1FF0)); tiny[0x1FFF] = 0x5C;
        Check("the header at $1FF0 (an 8 KB ROM) is found", RomDetect.Detect(tiny, "a.bin") == ConsoleKind.GameGear);
        Check("a 512-byte copier header does not hide the SMS header", RomDetect.Detect(new byte[0x200].Concat(Sms(4)).ToArray(), "a.bin") == ConsoleKind.MasterSystem);
        Check("StripCopierHeader removes exactly the 512 bytes", SegaRomFormat.StripCopierHeader(new byte[0x200].Concat(Sms(4)).ToArray()).SequenceEqual(Sms(4)) && SegaRomFormat.StripCopierHeader(Sms(4)).SequenceEqual(Sms(4)));
        Check("extension fallback without a header: .sms .sg .sc -> Master System, .gg -> Game Gear",
            new[] { "a.sms", "a.sg", "a.sc" }.All(n => RomDetect.Detect(new byte[0x8000], n) == ConsoleKind.MasterSystem) && RomDetect.Detect(new byte[0x8000], "a.gg") == ConsoleKind.GameGear);
        Check("an unknown .bin is not a ROM", RomDetect.Detect(new byte[0x8000], "a.bin") == null);

        Check("Genesis header 'SEGA MEGA DRIVE' -> Genesis (any extension)", RomDetect.Detect(Genesis(), "a.bin") == ConsoleKind.Genesis && RomDetect.Detect(Genesis(), "a.md") == ConsoleKind.Genesis);
        Check("Genesis header 'SEGA GENESIS' and ' SEGA' (leading space) -> Genesis", RomDetect.Detect(Genesis("SEGA GENESIS"), "a.bin") == ConsoleKind.Genesis && RomDetect.Detect(Genesis(" SEGA MEGA DRIVE"), "a.bin") == ConsoleKind.Genesis);
        Check("32X and Pico cartridges are refused (not detected)", RomDetect.Detect(Genesis("SEGA 32X"), "a.bin") == null && RomDetect.Detect(Genesis("SEGA PICO"), "a.md") == null);
        Check("extension fallback: .md .gen .smd without a header -> Genesis", new[] { "a.md", "a.gen", "a.smd" }.All(n => RomDetect.Detect(new byte[0x8000], n) == ConsoleKind.Genesis));

        var g = Genesis(size: 0x10000);
        var smd = Interleave(g);
        Check("an interleaved .smd is detected as Genesis from its de-interleaved header", RomDetect.Detect(smd, "a.smd") == ConsoleKind.Genesis && RomDetect.Detect(smd, "a.bin") == ConsoleKind.Genesis);
        Check("IsInterleavedSmd is true for the .smd and false for the plain ROM", SegaRomFormat.IsInterleavedSmd(smd) && !SegaRomFormat.IsInterleavedSmd(g));
        Check("NormalizeGenesis de-interleaves back to the original bytes", SegaRomFormat.NormalizeGenesis(smd).SequenceEqual(g) && SegaRomFormat.NormalizeGenesis(g).SequenceEqual(g));

        Check("the existing consoles are still detected with the preview on",
            RomDetect.Detect(Nes(), "a.nes") == ConsoleKind.Nes && RomDetect.Detect(Snes(), "a.sfc") == ConsoleKind.Snes && RomDetect.Detect(Snes(), "a.smc") == ConsoleKind.Snes
            && RomDetect.Detect(GameBoy(false), "a.gb") == ConsoleKind.GameBoy && RomDetect.Detect(GameBoy(true), "a.gbc") == ConsoleKind.GameBoyColor && RomDetect.Detect(Snes(), "noext") == ConsoleKind.Snes);
        var inner = RomDetect.Unwrap(Zip("sonic.md", Genesis()), "x.zip", out string innerName);
        Check("a zip holding a .md is unwrapped and detected", innerName == "sonic.md" && RomDetect.Detect(inner, innerName) == ConsoleKind.Genesis);
        Check("Runs: Sega consoles run only their own cartridges", RomDetect.Runs(ConsoleKind.Genesis, ConsoleKind.Genesis) && !RomDetect.Runs(ConsoleKind.Genesis, ConsoleKind.MasterSystem) && !RomDetect.Runs(ConsoleKind.MasterSystem, ConsoleKind.GameGear));
    }

    private static void Catalog()
    {
        var expect = new (ConsoleKind, CoreSlot, string)[]
        {
            (ConsoleKind.MasterSystem, CoreSlot.Cpu, "Z80"), (ConsoleKind.MasterSystem, CoreSlot.Ppu, "SMS"), (ConsoleKind.MasterSystem, CoreSlot.Apu, "SMS"),
            (ConsoleKind.GameGear, CoreSlot.Cpu, "Z80"), (ConsoleKind.GameGear, CoreSlot.Ppu, "GG"), (ConsoleKind.GameGear, CoreSlot.Apu, "GG"),
            (ConsoleKind.Genesis, CoreSlot.Cpu, "M68K"), (ConsoleKind.Genesis, CoreSlot.Ppu, "MD"), (ConsoleKind.Genesis, CoreSlot.Apu, "MD"),
        };
        Check("catalog defaults follow the id plan (CPU by chip, PPU/APU by console)", expect.All(e => CoreCatalog.Default(e.Item1, e.Item2) == e.Item3),
            string.Join(" ", expect.Where(e => CoreCatalog.Default(e.Item1, e.Item2) != e.Item3).Select(e => $"{e.Item1}/{e.Item2}={CoreCatalog.Default(e.Item1, e.Item2)}")));
        Check("a stored id the menu no longer offers falls back to the default", expect.All(e => CoreCatalog.Resolve(e.Item1, e.Item2, "BOGUS") == e.Item3 && CoreCatalog.Resolve(e.Item1, e.Item2, null) == e.Item3));
        Check("a stored id the menu offers is kept (case-insensitive)", CoreCatalog.Resolve(ConsoleKind.Genesis, CoreSlot.Cpu, "m68k") == "m68k");
        Check("every Sega option names its own family", expect.All(e => CoreCatalog.Options(e.Item1, e.Item2).All(o => o.Family == Consoles.Family(e.Item1))));
        Check("the NES, SNES and Game Boy menus are unchanged by the Sega kinds", CoreCatalog.Default(ConsoleKind.Snes, CoreSlot.Cpu) == "SFC" && CoreCatalog.Default(ConsoleKind.GameBoy, CoreSlot.Cpu) == "GB" && CoreCatalog.Default(ConsoleKind.Nes, CoreSlot.Cpu) == "FIX");
    }

    private static void Keys()
    {
        bool round = Consoles.All.All(k => Consoles.FromKey(Consoles.Key(k)) == k);
        Check("Key / FromKey round-trip for all seven consoles", round, string.Join(",", Consoles.All.Select(k => $"{k}={Consoles.Key(k)}")));
        Check("the stored keys of the old consoles did not change", Consoles.Key(ConsoleKind.Nes) == "nes" && Consoles.Key(ConsoleKind.Snes) == "snes" && Consoles.Key(ConsoleKind.GameBoy) == "gb" && Consoles.Key(ConsoleKind.GameBoyColor) == "gbc");
        Check("Sega keys are sms, gg, md", Consoles.Key(ConsoleKind.MasterSystem) == "sms" && Consoles.Key(ConsoleKind.GameGear) == "gg" && Consoles.Key(ConsoleKind.Genesis) == "md");
        Check("aliases resolve", Consoles.FromKey("genesis") == ConsoleKind.Genesis && Consoles.FromKey("Mega Drive") == ConsoleKind.Genesis && Consoles.FromKey("gamegear") == ConsoleKind.GameGear && Consoles.FromKey("mastersystem") == ConsoleKind.MasterSystem);
        Check("an unknown key is still the NES", Consoles.FromKey("??") == ConsoleKind.Nes && Consoles.FromKey(null) == ConsoleKind.Nes);
        Check("enum values of the old consoles did not move", (int)ConsoleKind.Nes == 0 && (int)ConsoleKind.Snes == 1 && (int)ConsoleKind.GameBoy == 2 && (int)ConsoleKind.GameBoyColor == 3);
        Check("families: SMS and GG share one, Genesis has its own", Consoles.Family(ConsoleKind.MasterSystem) == Consoles.Family(ConsoleKind.GameGear) && Consoles.Family(ConsoleKind.Genesis) == "Genesis" && Consoles.Family(ConsoleKind.GameBoyColor) == "Game Boy");
        var bits = new[] { PadButtons.Up, PadButtons.Down, PadButtons.Left, PadButtons.Right, PadButtons.A, PadButtons.B, PadButtons.X, PadButtons.Y, PadButtons.L, PadButtons.R, PadButtons.Start, PadButtons.Select, PadButtons.C, PadButtons.Z, PadButtons.Mode };
        ushort all = 0; bool distinct = true;
        foreach (var b in bits) { if ((all & (ushort)b) != 0 || BitOperations.PopCount((ushort)b) != 1) distinct = false; all |= (ushort)b; }
        Check("PadButtons: 15 distinct single bits, bit 15 still free", distinct && (all & 0x8000) == 0 && all == 0x7FFF);
    }

    private static void Timing()
    {
        double Near(double v, double e, double tol) => Math.Abs(v - e) / e;
        Check("Genesis NTSC frame rate is 59.9227 Hz", Near(SegaTiming.GenesisFramesPerSecond(SegaRegion.Ntsc), 59.9227, 0) < 1e-5, SegaTiming.GenesisFramesPerSecond(SegaRegion.Ntsc).ToString("0.0000"));
        Check("Genesis PAL frame rate is 49.7014 Hz", Near(SegaTiming.GenesisFramesPerSecond(SegaRegion.Pal), 49.7014, 0) < 1e-5, SegaTiming.GenesisFramesPerSecond(SegaRegion.Pal).ToString("0.0000"));
        Check("Master System NTSC 59.9227 Hz, PAL 49.7015 Hz", Near(SegaTiming.MasterSystemFramesPerSecond(SegaRegion.Ntsc), 59.9227, 0) < 1e-5 && Near(SegaTiming.MasterSystemFramesPerSecond(SegaRegion.Pal), 49.7015, 0) < 1e-5,
            $"{SegaTiming.MasterSystemFramesPerSecond(SegaRegion.Ntsc):0.0000} / {SegaTiming.MasterSystemFramesPerSecond(SegaRegion.Pal):0.0000}");
        Check("Genesis line = 3420 master clocks = 488.57 68K cycles = 228 Z80 cycles", SegaTiming.GenesisMasterClocksPerLine == 3420 && Math.Abs(3420.0 / SegaTiming.GenesisM68kDivider - 488.571) < 0.001 && 3420 / SegaTiming.GenesisZ80Divider == 228);
        double fm = SegaTiming.GenesisMasterHz(SegaRegion.Ntsc) / SegaTiming.GenesisYm2612MasterClocksPerSample;
        Check("YM2612 sample rate is 53,267 Hz (1008 master clocks)", Math.Abs(fm - 53267) < 1, fm.ToString("0.0"));
        Check("every table has both regions (no NTSC-only constant)", SegaTiming.GenesisMasterHz(SegaRegion.Ntsc) != SegaTiming.GenesisMasterHz(SegaRegion.Pal)
            && SegaTiming.MasterSystemCpuHz(SegaRegion.Ntsc) != SegaTiming.MasterSystemCpuHz(SegaRegion.Pal)
            && SegaTiming.GenesisLinesPerFrame(SegaRegion.Pal) == 313 && SegaTiming.MasterSystemLinesPerFrame(SegaRegion.Pal) == 313);
        Check("frame sizes: SMS 256x192, GG 160x144, Genesis 320x224 (240 PAL)", SegaTiming.FrameSize(ConsoleKind.MasterSystem, SegaRegion.Ntsc) == (256, 192) && SegaTiming.FrameSize(ConsoleKind.GameGear, SegaRegion.Ntsc) == (160, 144)
            && SegaTiming.FrameSize(ConsoleKind.Genesis, SegaRegion.Ntsc) == (320, 224) && SegaTiming.FrameSize(ConsoleKind.Genesis, SegaRegion.Pal) == (320, 240));
    }

    private static void Placeholder()
    {
        foreach (var (kind, w, h, rom) in new[] { (ConsoleKind.MasterSystem, 256, 192, Sms(4)), (ConsoleKind.GameGear, 160, 144, Sms(6)), (ConsoleKind.Genesis, 320, 224, Genesis()) })
        {
            string name = Consoles.DisplayName(kind);
            using var s = ConsoleSessions.Create(kind, rom, CoreCatalog.Default(kind, CoreSlot.Cpu), CoreCatalog.Default(kind, CoreSlot.Ppu), CoreCatalog.Default(kind, CoreSlot.Apu));
            Check($"{name}: a placeholder session says what it is", s is SegaPlaceholderSession && s.Description.Contains("PLACEHOLDER") && s.Console == kind);
            Check($"{name}: frame is {w}x{h}, ARGB, ~59.92 fps", s.FrameWidth == w && s.FrameHeight == h && s.Frame.Length >= w * h && Math.Abs(s.FramesPerSecond - 59.9227) < 0.001 && s.Frame.All(p => p >> 24 == 0xFF));
            Check($"{name}: GameId is the ROM's SHA-1 (40 hex)", s.GameId.Length == 40 && s.GameId == ConsoleSessions.Sha1(rom));
            var before = (uint[])s.Frame.Clone();
            s.SetPad(0, PadButtons.Right | PadButtons.Down);
            for (int i = 0; i < 10; i++) s.RunFrame();
            Check($"{name}: the pad moves the square (input reaches the session)", !before.SequenceEqual(s.Frame));
            s.SetPad(1, PadButtons.Left);   // player 2 is ignored
            int total = 0; var buf = new short[4096]; bool silent = true;
            while (s.ReadSamples(buf) > 0) { }   // the ten warm-up frames above left their audio waiting: drain it before measuring
            for (int i = 0; i < 60; i++)
            {
                s.RunFrame();
                int n = s.ReadSamples(buf);
                total += n; silent &= buf.Take(n).All(x => x == 0) && n % 2 == 0;
            }
            double expected = 44100.0 / s.FramesPerSecond * 2 * 60;
            Check($"{name}: ~1 second of silent stereo audio per 60 frames", silent && Math.Abs(total - expected) <= 4, $"{total} shorts, expected {expected:0}");
            Check($"{name}: no battery, empty save", !s.HasBattery && s.ExportSave().Length == 0);
            Check($"{name}: swapping a core in place is accepted", s.TrySwapCore(CoreSlot.Ppu, "SMS") && s.Description.Contains("PPU SMS"));
            s.Reset();
        }
    }
}
