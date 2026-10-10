using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NesEmulator;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--picture-bridge-test [--only catalog|nes2sega|sega2nes|session|classify|games] [--nes-roms dir] [--sms-roms dir] [--gg-roms dir] [--md-roms dir] [--verbose]</c>: the picture bridges, by measurement.
/// <list type="bullet">
/// <item><b>catalog</b>: the menus (a Sega console lists the NES chips and, through them, the Game Boy / SNES / other Sega chips, never its own family's through a bridge; the NES lists SMS and MD under their families).</item>
/// <item><b>nes2sega</b>: <c>PPU_SMS</c> and <c>PPU_MD</c> with synthetic NES pictures (a NES chip in front, the Sega chip drawing): exact colours by the documented rule, tile and sprite positions, flips, behind-the-background sprites, fine scroll, the sprite limits, 8x16, the tile budget and the counters that say so.</item>
/// <item><b>sega2nes</b>: <c>SmsToNes</c> and <c>MdToNes</c> with synthetic Master System, Game Gear and Genesis scenes poked into a real VDP: the bridged picture is compared with the VDP's own, pixel for pixel on which pixels are drawn and exact on the colour of each layer.</item>
/// <item><b>session</b>: the wrapper session: in-place chip swaps, the status text, the counters, a static scene staying steady from frame to frame.</item>
/// <item><b>classify</b>: the sweep's classifier with synthetic measurements (frozen, dead, static, lossy, hung, refused, crashed, alive).</item>
/// <item><b>games</b>: real games from the user's libraries (when the directories are given): recognisable, tracked against the source chip's own picture, and the NES game untouched by its bridge.</item>
/// </list>
/// Exit code 0 = every check passed. Every group is guarded: a bridge that throws is a failed check, not a crashed run.
/// </summary>
internal static class PictureBridgeTestCli
{
    internal static int Passes, Fails;
    internal static bool Verbose;

    internal static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Passes++; else Fails++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    internal static void Info(string text) { if (Verbose) Console.WriteLine("        " + text); }

    internal static string Arg(string[] args, string name) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }

    /// <summary>Run a group of checks; an exception is a failed check named for the group.</summary>
    internal static void Guard(string title, Action body)
    {
        Console.WriteLine($"\n== {title}");
        try { body(); }
        catch (Exception ex) { Check($"{title}: the group ran to the end", false, ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); Info(ex.ToString()); }
    }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        Verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        string only = Arg(args, "--only");
        bool Want(string s) => only.Length == 0 || only.Equals(s, StringComparison.OrdinalIgnoreCase);
        if (Want("catalog")) Guard("catalog: the picture menus", Catalog);
        if (Want("nes2sega")) Guard("PPU_SMS and PPU_MD: a NES picture drawn by a Sega chip", NesOnSega.Run);
        if (Want("sega2nes")) Guard("SmsToNes / MdToNes: a Sega scene drawn by NES chips, against the VDP's own picture", SegaScenes.Run);
        if (Want("session")) Guard("the Sega picture session", SessionChecks);
        if (Want("classify")) Guard("the sweep's classifier", Classifier);
        if (Want("games")) Guard("real games", () => Games(args));
        Console.WriteLine($"\n{Passes} passed, {Fails} failed");
        return Fails == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------------------------------------ catalog
    private static void Catalog()
    {
        bool Has(ConsoleKind c, CoreSlot s, string id) => CoreCatalog.Options(c, s).Any(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        string[] nesChips = { "FIXS", "FIX", "BFR", "CUBE", "EIL", "FMC", "LOW", "SPD" };
        foreach (var console in new[] { ConsoleKind.MasterSystem, ConsoleKind.GameGear })
        {
            string n = Consoles.DisplayName(console);
            Check($"{n}: the own chip is first and the NES chips follow", CoreCatalog.Options(console, CoreSlot.Ppu)[0].Id == (console == ConsoleKind.GameGear ? "GG" : "SMS") && nesChips.All(c => Has(console, CoreSlot.Ppu, "NES:" + c)));
            Check($"{n}: the Game Boy, SNES and Genesis chips are offered through the NES", new[] { "NES:DMG", "NES:DMGX", "NES:DMGS", "NES:SNES", "NES:MD" }.All(id => Has(console, CoreSlot.Ppu, id)));
            Check($"{n}: its own family's NES-side chip (SMS) is not offered", !Has(console, CoreSlot.Ppu, "NES:SMS"));
        }
        Check("Genesis: the NES chips, the Game Boy / SNES chips and the Master System chip are offered, its own family's is not", nesChips.All(c => Has(ConsoleKind.Genesis, CoreSlot.Ppu, "NES:" + c)) && Has(ConsoleKind.Genesis, CoreSlot.Ppu, "NES:SMS") && Has(ConsoleKind.Genesis, CoreSlot.Ppu, "NES:DMGS") && !Has(ConsoleKind.Genesis, CoreSlot.Ppu, "NES:MD"));
        var nesPpu = CoreCatalog.Options(ConsoleKind.Nes, CoreSlot.Ppu);
        Check("the NES picture menu lists SMS under Master System and MD under Genesis", nesPpu.Any(o => o.Id == "SMS" && o.Family == "Master System") && nesPpu.Any(o => o.Id == "MD" && o.Family == "Genesis"));
        Check("their labels say what they are", nesPpu.First(o => o.Id == "SMS").Label.Contains("Master System picture chip") && nesPpu.First(o => o.Id == "MD").Label.Contains("Genesis picture chip"));
        Check("both need the board's precise PPU timing switch only through the family table (they claim FIX timing themselves)", CoreCatalog.NesPpuNeedsPreciseTiming("SMS") && CoreCatalog.NesPpuNeedsPreciseTiming("MD"));
        foreach (var console in new[] { ConsoleKind.GameBoy, ConsoleKind.GameBoyColor, ConsoleKind.Snes })
            Check($"{Consoles.DisplayName(console)}: the NES menu offers the Master System and Genesis chips", Has(console, CoreSlot.Ppu, "NES:SMS") && Has(console, CoreSlot.Ppu, "NES:MD"));
        Check("a stored Sega picture id resolves to itself, a wrong one to the default", CoreCatalog.Resolve(ConsoleKind.MasterSystem, CoreSlot.Ppu, "NES:FIXS") == "NES:FIXS" && CoreCatalog.Resolve(ConsoleKind.MasterSystem, CoreSlot.Ppu, "NES:SMS") == "SMS" && CoreCatalog.Resolve(ConsoleKind.Genesis, CoreSlot.Ppu, "NES:NOPE") == "MD");
        var labels = CoreCatalog.Options(ConsoleKind.MasterSystem, CoreSlot.Ppu).Where(o => o.Id.StartsWith("NES:")).ToList();
        Check("every Sega picture entry has a label that says more than its id", labels.All(o => o.Label.Length > o.Id.Length + 10));
        Check("the layered chip's label says losses are counted, the flat chips' say what they cannot hold", labels.First(o => o.Id == "NES:FIXS").Label.Contains("losses counted") && labels.First(o => o.Id == "NES:FIX").Label.Contains("256-tile"));
        Check("CoreRegistry discovers PPU_SMS and PPU_MD under those ids", CoreRegistry.PpuIds.Contains("SMS", StringComparer.OrdinalIgnoreCase) && CoreRegistry.PpuIds.Contains("MD", StringComparer.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------------------------------------------------------ session
    private static void SessionChecks() => SegaScenes.Session();

    // ------------------------------------------------------------------------------------------------------------------ classifier
    private static void Classifier()
    {
        BridgeSweepCli.Measure M(Action<BridgeSweepCli.Measure>? f = null) { var m = new BridgeSweepCli.Measure { Frames = 400, Colours = 8, Changes = 12, Samples = 37, AudioRms = 0.05 }; f?.Invoke(m); return m; }
        string C(BridgeSweepCli.Measure m, BridgeSweepCli.Measure? control, bool sound = false) => BridgeSweepCli.Classify(m, control, sound);
        var ok = M();
        Check("a moving, coloured picture is alive", C(M(), ok) == "alive");
        Check("a combination that was not built is refused", C(M(m => m.Refused = true), ok) == "refused");
        Check("an exception is crashed", C(M(m => m.Crashed = true), ok) == "crashed");
        Check("no result in time is hung", C(M(m => m.Timeout = true), ok) == "hung");
        Check("a one-colour picture is dead when the game itself shows more", C(M(m => m.Colours = 1), ok) == "dead");
        Check("a picture that never changes is frozen when the game itself moves", C(M(m => m.Changes = 0), ok) == "frozen");
        Check("... and static when the game itself idles (nothing to judge)", C(M(m => m.Changes = 0), M(m => m.Changes = 0)) == "static");
        Check("an alive picture with low structural similarity to the source is lossy", C(M(m => { m.FidSamples = 30; m.Ssim = 0.3; }), ok) == "lossy");
        Check("... and alive with good similarity", C(M(m => { m.FidSamples = 30; m.Ssim = 0.9; }), ok) == "alive");
        Check("sound: silent where the game's own sound is not is dead", C(M(m => m.AudioRms = 0), ok, true) == "dead");
        Check("sound: silent where the game is silent too is static", C(M(m => m.AudioRms = 0), M(m => m.AudioRms = 0), true) == "static");
        Check("sound: any real sound is alive", C(M(), ok, true) == "alive");
    }

    // ------------------------------------------------------------------------------------------------------------------ games
    private static void Games(string[] args) => SegaScenes.Games(args);
}
