using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes.Workshop.Sega;
using NesEmulator;
using NesEmulator.Corruption;
using NesEmulator.Gb;
using NesEmulator.Mix;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.EnhancedCores;

/// <summary>
/// <c>--enhanced-test [--only menu|migrate|invert|...]</c>: the stock / enhanced core roster (docs/projects/enhanced-cores.md), by measurement.
/// <list type="bullet">
/// <item><b>menu</b>: every console's PPU and APU menu is the console's own best cores (stock, enhanced), the other consoles' best cores (stock, enhanced; NES, SNES, Game Boy,
/// Master System, Genesis), then the rest as before; the labels say stock / enhanced; the ids and the defaults did not move. The order checker is shown able to fail (shuffled menus).</item>
/// <item><b>migrate</b>: the retired big-screen Game Boy chip DMGX (and the older DMGXI / DMGSI) reaches its successor DMGS wherever a pick can be stored: a settings store (the one function the
/// desktop's config.json and Lite's IndexedDB prefs go through), the catalog's Resolve and Find (the HTTP API, stockpiles, the Workshop), the NES board, a NES save state's core type name, a stockpile
/// written and read back. Negative controls: without the migration the same picks are lost (a NES state falls back to another chip, the menu does not offer the id).</item>
/// <item><b>invert</b>: the Game Boy pictures are upright on every path (a bright source colour is a light shade, a dark one a dark shade; the backdrop by its own brightness) and the two inversions
/// flip the background (the backdrop with it) and the sprites apart, the same way on every path: a NES game (Game Boy stock and enhanced), a SNES game, a Master System, Game Gear and Genesis game
/// (through the NES, stock and enhanced), a Game Boy game drawn by a NES and a SNES chip, Game Boy Color colours (lightness mirrored, hue kept). Negative controls: the old index-as-shade mapping
/// (<see cref="PPU_DMG.IndexShadesForTests"/>) draws the scenes inverted and is caught; the old CGB negative changes the hue and is caught.</item>
/// <item><b>layers</b> and <b>sound</b>: the two enhanced contracts (<see cref="EnhancedContractTests"/>).</item>
/// </list>
/// Exit code 0 = every check passed.
/// </summary>
internal static class EnhancedCoresTestCli
{
    internal static int Passes, Fails;
    internal static bool Verbose;

    internal static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Passes++; else Fails++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    internal static void Guard(string title, Action body)
    {
        Console.WriteLine($"\n== {title}");
        try { body(); }
        catch (Exception ex) { Check($"{title}: the group ran to the end", false, ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); if (Verbose) Console.WriteLine(ex); }
    }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        Verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        string only = PictureBridgeTestCli.Arg(args, "--only");
        bool Want(string s) => only.Length == 0 || only.Split(',').Any(o => o.Equals(s, StringComparison.OrdinalIgnoreCase));
        var savedLook = (GbLook.Palette, GbLook.InvertBackground, GbLook.InvertSprites);
        try
        {
            if (Want("menu")) Guard("menu: stock and enhanced, own best cores first", Menus);
            if (Want("migrate")) Guard("migrate: the retired DMGX reaches DMGS wherever it was stored", Migrations);
            if (Want("invert")) Guard("invert: upright Game Boy pictures and the inversions on every path", Inversions);
            if (Want("layers")) Guard("layers: one layered picture at the guest's own size and layer count, on both enhanced picture chips", EnhancedContractTests.Layers);
            if (Want("sound")) Guard("sound: one voice list per guest, as many channel banks as its voices need on the enhanced sound chips", EnhancedContractTests.Sound);
            if (Want("layers3")) Guard("layers3: the SNES, Master System / Game Gear and Genesis enhanced picture chips: every guest at its own size and layer count, in the chip's colours", EnhancedPhase3Tests.Layers);
            if (Want("sound3")) Guard("sound3: the SNES, Master System / Game Gear and Genesis enhanced sound chips: a channel for every guest voice, the pitch, the pan", EnhancedPhase3Tests.Sound);
            if (Want("states3")) Guard("states3: save states with the other consoles' enhanced chips (loaded, or refused with the reason)", EnhancedPhase3Tests.States);
            if (Want("own3")) Guard("own3: a console's own game on its enhanced chips draws and sounds exactly as on the stock chips", EnhancedPhase3Tests.OwnNeutral);
            if (Want("speed3")) Guard("speed3: frames a second on the enhanced chips of the other consoles (information)", EnhancedPhase3Tests.Speed);
            if (Want("wmsmenu")) Guard("wmsmenu: the WideMess's chips on the other consoles' menus, only where the WideMess is offered", WideMessGuestTests.Menus);
            if (Want("wms")) Guard("wms: the WideMess widescreen chip (400 wide: each layer extended, repeated or filled as its translator's width says) and the WideMess sound chip, on every other console", () => { WideMessGuestTests.Pictures(); WideMessGuestTests.Sound(); });
            if (Want("wmsgames")) Guard("wmsgames: real games on the WideMess chips: the middle identical to the SNES enhanced chip's picture every frame, the sound with no voice dropped, no allocation a frame", WideMessGuestTests.GamesCheck);
        }
        finally { (GbLook.Palette, GbLook.InvertBackground, GbLook.InvertSprites) = savedLook; PPU_DMG.IndexShadesForTests = false; PPU_GBE.BackdropIgnoresInversionForTests = false; PPU_GBE.IndexShadesForTests = false; }
        Console.WriteLine($"\n{Passes} passed, {Fails} failed");
        return Fails == 0 ? 0 : 1;
    }

    // =========================================================================================================== menu
    private static readonly string[] FamilyOrder = { "NES", "SNES", "Game Boy", "Master System", "Genesis", "WideMess" };

    /// <summary>Null when <paramref name="menu"/> is in the three sections in order (own best: stock before enhanced; other consoles' best: by family, stock before enhanced within a route; then the rest)
    /// and every best core's label says its grade; otherwise why not.</summary>
    internal static string? OrderProblem(ConsoleKind console, CoreSlot slot, IReadOnlyList<CoreOption> menu)
    {
        if (menu.Count == 0) return "empty menu";
        if (menu[0].Section != CoreSection.Own || menu[0].Grade != CoreGrade.Stock) return $"the first entry ({menu[0].Id}) is not the console's own stock core";
        for (int i = 1; i < menu.Count; i++)
        {
            var a = menu[i - 1]; var b = menu[i];
            if (b.Section < a.Section) return $"{b.Id} ({b.Section}) comes after {a.Id} ({a.Section})";
            if (a.Section == b.Section && a.Section == CoreSection.Own && b.Grade < a.Grade) return $"own {b.Id} ({b.Grade}) after {a.Id} ({a.Grade})";
            if (a.Section == b.Section && a.Section == CoreSection.OtherConsoles)
            {
                int fa = Array.IndexOf(FamilyOrder, a.Family), fb = Array.IndexOf(FamilyOrder, b.Family);
                if (fb < fa) return $"{b.Id} ({b.Family}) after {a.Id} ({a.Family})";
                bool sameRoute = a.Id.StartsWith("NES-FM:") == b.Id.StartsWith("NES-FM:");
                if (fb == fa && sameRoute && b.Grade < a.Grade) return $"{b.Id} ({b.Grade}) after {a.Id} ({a.Grade}) in the {a.Family} family";
            }
        }
        if (slot != CoreSlot.Cpu)
            foreach (var o in menu)
            {
                bool says = o.Label.Contains("(stock)") || o.Label.Contains("(enhanced)");
                if (o.Grade == CoreGrade.Stock && !o.Label.Contains("(stock)")) return $"{o.Id} is stock but its label does not say so: {o.Label}";
                if (o.Grade == CoreGrade.Enhanced && !o.Label.Contains("(enhanced)")) return $"{o.Id} is enhanced but its label does not say so: {o.Label}";
                if (o.Grade == CoreGrade.Other && says) return $"{o.Id} is not a best core but its label says it is: {o.Label}";
            }
        return null;
    }

    private static void Menus()
    {
        var consoles = new[] { ConsoleKind.Nes, ConsoleKind.Snes, ConsoleKind.GameBoy, ConsoleKind.GameBoyColor, ConsoleKind.MasterSystem, ConsoleKind.GameGear, ConsoleKind.Genesis };
        foreach (var c in consoles)
            foreach (var slot in new[] { CoreSlot.Cpu, CoreSlot.Ppu, CoreSlot.Apu })
            {
                var menu = CoreCatalog.Options(c, slot);
                string? why = OrderProblem(c, slot, menu);
                Check($"{Consoles.DisplayName(c)} {slot}: own best cores, other consoles' best cores, then the rest{(slot == CoreSlot.Cpu ? "" : "; the labels say stock / enhanced")}", why == null, why ?? string.Join(", ", menu.Take(9).Select(o => o.Id)));
            }
        // the exact heads of the menus the request names (ids stable: FIXS = FIX enhanced, DMG / DMGS = Game Boy stock / enhanced, GBS = the Game Boy's enhanced sound)
        void Head(ConsoleKind c, CoreSlot s, params string[] ids)
        {
            var got = CoreCatalog.Options(c, s).Take(ids.Length).Select(o => o.Id).ToArray();
            Check($"{Consoles.DisplayName(c)} {s} begins {string.Join(", ", ids)}", got.SequenceEqual(ids, StringComparer.OrdinalIgnoreCase), string.Join(", ", got));
        }
        // phase 3: the other consoles' enhanced chips (after each console's stock chip in its family), the consoles' own enhanced chips after their stock ones; each appears once its chips exist
        bool snes = EnhancedChips.IsRoute("SNES:SFCS"), sms = EnhancedChips.IsRoute("SMS:SMSS"), gg = EnhancedChips.IsRoute("GG:GGS"), md = EnhancedChips.IsRoute("MD:MDS");
        string[] H(params string?[] ids) => ids.Where(i => i != null).Select(i => i!).ToArray();
        Head(ConsoleKind.Nes, CoreSlot.Ppu, H("FIX", "FIXS", "SNES", snes ? "SNESS" : null, "DMG", "DMGS", "SMS", sms ? "SMSS" : null, "MD", md ? "MDS" : null));
        Head(ConsoleKind.Nes, CoreSlot.Apu, H("FIX", "FIXS", "SNES", snes ? "SNESS" : null, "DMG", "DMGS", "SMS", "SMSFM", sms ? "SMSS" : null, "MD", md ? "MDS" : null));
        Head(ConsoleKind.Snes, CoreSlot.Ppu, H("SFC", snes ? "SFCS" : null, "NES:FIX", "NES:FIXS", "NES:DMG", "NES:DMGS", "NES:SMS", sms ? "SMS:SMSS" : null, gg ? "GG:GGS" : null, "NES:MD", md ? "MD:MDS" : null));
        Head(ConsoleKind.Snes, CoreSlot.Apu, H("SFC", snes ? "SFCS" : null, "NES:FIX", "NES:FIXS", "NES:DMG", "NES:DMGS", "NES:SMS", "NES:SMSFM", sms ? "SMS:SMSS" : null, gg ? "GG:GGS" : null, "NES:MD", md ? "MD:MDS" : null));
        Head(ConsoleKind.GameBoy, CoreSlot.Ppu, H("GB", "NES:FIX", "NES:FIXS", "SNES:SFC", snes ? "SNES:SFCS" : null, "NES:SMS", sms ? "SMS:SMSS" : null, gg ? "GG:GGS" : null, "NES:MD", md ? "MD:MDS" : null));
        Head(ConsoleKind.GameBoy, CoreSlot.Apu, H("GB", "GBS", "NES:FIX", "NES:FIXS", "NES:SNES", snes ? "SNES:SFCS" : null, "NES:SMS", "NES:SMSFM", sms ? "SMS:SMSS" : null, gg ? "GG:GGS" : null, "NES:MD", md ? "MD:MDS" : null));
        Head(ConsoleKind.MasterSystem, CoreSlot.Ppu, H("SMS", sms ? "SMSS" : null, "NES:FIX", "NES:FIXS", "NES:SNES", snes ? "SNES:SFCS" : null, "NES:DMG", "NES:DMGS", "NES:MD", md ? "MD:MDS" : null));
        Head(ConsoleKind.MasterSystem, CoreSlot.Apu, H("SMS", "SMS-FM", sms ? "SMSS" : null, "NES:FIX", "NES:FIXS", "NES-FM:FIX", "NES-FM:FIXS", "NES:SNES", snes ? "SNES:SFCS" : null, "NES:DMG", "NES:DMGS", "NES:MD", md ? "MD:MDS" : null));
        Head(ConsoleKind.GameGear, CoreSlot.Ppu, H("GG", gg ? "GGS" : null, "NES:FIX", "NES:FIXS", "NES:SNES", snes ? "SNES:SFCS" : null, "NES:DMG", "NES:DMGS", "NES:MD", md ? "MD:MDS" : null));
        Head(ConsoleKind.Genesis, CoreSlot.Ppu, H("MD", md ? "MDS" : null, "NES:FIX", "NES:FIXS", "NES:SNES", snes ? "SNES:SFCS" : null, "NES:DMG", "NES:DMGS", "NES:SMS", sms ? "SMS:SMSS" : null, gg ? "GG:GGS" : null));
        Head(ConsoleKind.Genesis, CoreSlot.Apu, H("MD", md ? "MDS" : null, "NES:FIX", "NES:FIXS", "NES:SNES", snes ? "SNES:SFCS" : null, "NES:DMG", "NES:DMGS", "NES:SMS", "NES:SMSFM", sms ? "SMS:SMSS" : null, gg ? "GG:GGS" : null));
        // the labels of the new entries say "<Console> (enhanced)"
        foreach (var (c, s, id, name) in new[] { (ConsoleKind.Nes, CoreSlot.Ppu, "SNESS", "SNES"), (ConsoleKind.Genesis, CoreSlot.Ppu, "SNES:SFCS", "SNES"), (ConsoleKind.GameBoy, CoreSlot.Apu, "SNES:SFCS", "SNES"),
                                               (ConsoleKind.Nes, CoreSlot.Apu, "MDS", "Genesis"), (ConsoleKind.Snes, CoreSlot.Ppu, "MD:MDS", "Genesis"), (ConsoleKind.Snes, CoreSlot.Apu, "SMS:SMSS", "Master System"),
                                               (ConsoleKind.Genesis, CoreSlot.Ppu, "GG:GGS", "Game Gear"), (ConsoleKind.Snes, CoreSlot.Ppu, "SFCS", "SNES"), (ConsoleKind.Genesis, CoreSlot.Apu, "MDS", "Genesis"),
                                               (ConsoleKind.MasterSystem, CoreSlot.Ppu, "SMSS", "Master System"), (ConsoleKind.GameGear, CoreSlot.Apu, "GGS", "Game Gear") })
        {
            var o = CoreCatalog.Options(c, s).FirstOrDefault(x => x.Id == id);
            if (o == null) continue;
            Check($"{Consoles.DisplayName(c)} {s}: {id} is \"{name} (enhanced)\" ({o.Label[..Math.Min(o.Label.Length, 60)]}...)", o.Label.StartsWith($"{name} (enhanced)") && o.Grade == CoreGrade.Enhanced, o.Label);
        }
        Check("no menu but the NES's offers the NES board's ids of the other consoles' enhanced chips (SNESS, SMSS, MDS: the other consoles reach those chips directly)",
            consoles.Where(c => c != ConsoleKind.Nes).All(c => new[] { CoreSlot.Ppu, CoreSlot.Apu }.All(s => CoreCatalog.Options(c, s).All(o => !new[] { "NES:SNESS", "NES:SMSS", "NES:MDS" }.Contains(o.Id, StringComparer.OrdinalIgnoreCase)))));
        // the labels the request gives as examples
        string L(ConsoleKind c, CoreSlot s, string id) => CoreCatalog.Options(c, s).First(o => o.Id == id).Label;
        Check("the NES picture menu says FIX (stock) / FIX (enhanced) / Game Boy (stock) / Game Boy (enhanced)",
            L(ConsoleKind.Nes, CoreSlot.Ppu, "FIX").StartsWith("FIX (stock)") && L(ConsoleKind.Nes, CoreSlot.Ppu, "FIXS").StartsWith("FIX (enhanced)") &&
            L(ConsoleKind.Nes, CoreSlot.Ppu, "DMG").StartsWith("Game Boy (stock)") && L(ConsoleKind.Nes, CoreSlot.Ppu, "DMGS").StartsWith("Game Boy (enhanced)"),
            $"{L(ConsoleKind.Nes, CoreSlot.Ppu, "FIX")} | {L(ConsoleKind.Nes, CoreSlot.Ppu, "FIXS")} | {L(ConsoleKind.Nes, CoreSlot.Ppu, "DMG")} | {L(ConsoleKind.Nes, CoreSlot.Ppu, "DMGS")}");
        Check("the Game Boy's sound menu says Game Boy (stock) / Game Boy (enhanced) for GB / GBS", L(ConsoleKind.GameBoy, CoreSlot.Apu, "GB").StartsWith("Game Boy (stock)") && L(ConsoleKind.GameBoy, CoreSlot.Apu, "GBS").StartsWith("Game Boy (enhanced)"));
        Check("a Sega game's NES picture chips keep what their labels said (losses counted, the flat chip's 256 tiles)", L(ConsoleKind.MasterSystem, CoreSlot.Ppu, "NES:FIXS").Contains("losses counted") && L(ConsoleKind.MasterSystem, CoreSlot.Ppu, "NES:FIX").Contains("256-tile"));
        // the rest keeps its order: the NES gimmick picture cores, alphabetical as before, after the best cores
        var more = CoreCatalog.Options(ConsoleKind.Nes, CoreSlot.Ppu).Where(o => o.Section == CoreSection.More).Select(o => o.Id).ToList();
        Check("the NES gimmick picture cores follow, in their old order", more.Count > 3 && more.SequenceEqual(more.OrderBy(i => i, StringComparer.OrdinalIgnoreCase)) && more.Contains("CUBE") && more.Contains("LQ"), string.Join(", ", more));
        // the defaults (the first entry) did not move
        var defaults = new (ConsoleKind c, string cpu, string ppu, string apu)[]
        {
            (ConsoleKind.Nes, "FIX", "FIX", "FIX"), (ConsoleKind.Snes, "SFC", "SFC", "SFC"), (ConsoleKind.GameBoy, "GB", "GB", "GB"), (ConsoleKind.GameBoyColor, "GB", "GB", "GB"),
            (ConsoleKind.MasterSystem, "Z80", "SMS", "SMS"), (ConsoleKind.GameGear, "Z80", "GG", "GG"), (ConsoleKind.Genesis, "M68K", "MD", "MD"),
        };
        foreach (var (c, cpu, ppu, apu) in defaults)
        {
            string got = $"{CoreCatalog.Default(c, CoreSlot.Cpu)}/{CoreCatalog.Default(c, CoreSlot.Ppu)}/{CoreCatalog.Default(c, CoreSlot.Apu)}";
            Check($"{Consoles.DisplayName(c)}: the defaults are still {cpu}/{ppu}/{apu}", got == $"{cpu}/{ppu}/{apu}", got);
        }
        // ids: the NES board's sound chips (the FL plugin's append-only chip indexes are built from them) and the stable ids
        var apus = CoreRegistry.ApuIds.ToList();
        Check("the NES board still has every sound chip id the plugin's chip list is built from (FIX, FIXS, DMG, DMGS, SNES, SMS, SMSFM, MD ...)", new[] { "FIX", "FIXS", "DMG", "DMGS", "SNES", "SMS", "SMSFM", "MD", "QN", "WF", "MNES" }.All(i => apus.Contains(i, StringComparer.OrdinalIgnoreCase)), string.Join(",", apus));
        Check("the NES board's picture chips: DMG and DMGS stay, DMGX is gone", CoreRegistry.PpuIds.Contains("DMG", StringComparer.OrdinalIgnoreCase) && CoreRegistry.PpuIds.Contains("DMGS", StringComparer.OrdinalIgnoreCase) && !CoreRegistry.PpuIds.Contains("DMGX", StringComparer.OrdinalIgnoreCase), string.Join(",", CoreRegistry.PpuIds));
        Check("no menu offers DMGX in any form", consoles.All(c => CoreCatalog.Options(c, CoreSlot.Ppu).All(o => !o.Id.EndsWith("DMGX", StringComparison.OrdinalIgnoreCase))));
        Check("every menu draws a heading per section (three at most, in order)", consoles.All(c => CoreCatalog.Options(c, CoreSlot.Ppu).Select(o => o.Section).Distinct().SequenceEqual(CoreCatalog.Options(c, CoreSlot.Ppu).Select(o => o.Section).Distinct().OrderBy(s => s))));
        // negative controls: the checker catches a menu out of order and a label that lies
        var nes = CoreCatalog.Options(ConsoleKind.Nes, CoreSlot.Ppu).ToList();
        var swapped = nes.ToList(); (swapped[0], swapped[1]) = (swapped[1], swapped[0]);
        var gimmickFirst = nes.Skip(nes.Count - 1).Concat(nes.Take(nes.Count - 1)).ToList();
        var familiesSwapped = nes.ToList(); int iSnes = familiesSwapped.FindIndex(o => o.Id == "SNES"), iMd = familiesSwapped.FindIndex(o => o.Id == "MD"); (familiesSwapped[iSnes], familiesSwapped[iMd]) = (familiesSwapped[iMd], familiesSwapped[iSnes]);
        var unlabelled = nes.Select(o => o.Id == "FIXS" ? o with { Label = "FIXS - NES picture chip" } : o).ToList();
        Check("negative control: the order checker refuses FIX enhanced before FIX stock", OrderProblem(ConsoleKind.Nes, CoreSlot.Ppu, swapped) != null);
        Check("negative control: ... a gimmick core first", OrderProblem(ConsoleKind.Nes, CoreSlot.Ppu, gimmickFirst) != null);
        Check("negative control: ... the Genesis's chip before the SNES's", OrderProblem(ConsoleKind.Nes, CoreSlot.Ppu, familiesSwapped) != null);
        Check("negative control: ... an enhanced core whose label does not say so", OrderProblem(ConsoleKind.Nes, CoreSlot.Ppu, unlabelled) != null);
    }

    // =========================================================================================================== migrate
    private static void Migrations()
    {
        // ---- the one function every settings store goes through
        bool moved = false, inv = false;
        string? nesPpu = CoreIdMigration.Migrate(ConsoleKind.Nes, CoreSlot.Ppu, "DMGX", ref moved, ref inv);
        Check("a stored NES picture pick DMGX becomes DMGS (the store is written back, the sprite inversion untouched)", nesPpu == "DMGS" && moved && !inv, $"{nesPpu} moved={moved} invert={inv}");
        moved = false; inv = false;
        var stored = new Dictionary<(ConsoleKind, CoreSlot), string>
        {
            [(ConsoleKind.Snes, CoreSlot.Ppu)] = "NES:DMGX", [(ConsoleKind.Genesis, CoreSlot.Ppu)] = "NES:dmgx", [(ConsoleKind.MasterSystem, CoreSlot.Ppu)] = "NES:DMGXI",
            [(ConsoleKind.GameGear, CoreSlot.Ppu)] = "NES:FIXS", [(ConsoleKind.Snes, CoreSlot.Apu)] = "NES:DMGS", [(ConsoleKind.GameBoy, CoreSlot.Apu)] = "GBS",
        };
        var after = stored.ToDictionary(kv => kv.Key, kv => CoreIdMigration.Migrate(kv.Key.Item1, kv.Key.Item2, kv.Value, ref moved, ref inv));
        Check("a console store: NES:DMGX (any case) and the retired NES:DMGXI become NES:DMGS; NES:DMGXI also switches the sprite inversion on; the current ids stay",
            after[(ConsoleKind.Snes, CoreSlot.Ppu)] == "NES:DMGS" && after[(ConsoleKind.Genesis, CoreSlot.Ppu)] == "NES:DMGS" && after[(ConsoleKind.MasterSystem, CoreSlot.Ppu)] == "NES:DMGS"
            && after[(ConsoleKind.GameGear, CoreSlot.Ppu)] == "NES:FIXS" && after[(ConsoleKind.Snes, CoreSlot.Apu)] == "NES:DMGS" && after[(ConsoleKind.GameBoy, CoreSlot.Apu)] == "GBS" && moved && inv,
            string.Join(", ", after.Select(kv => $"{kv.Key.Item1}/{kv.Key.Item2}={kv.Value}")) + $" moved={moved} invert={inv}");
        bool m2 = false, i2 = false;
        foreach (var id in new[] { "FIX", "FIXS", "DMG", "DMGS", "CUBE" }) CoreIdMigration.Migrate(ConsoleKind.Nes, CoreSlot.Ppu, id, ref m2, ref i2);
        foreach (var id in new[] { "NES:DMG", "NES:DMGS", "SFC", "NES:FIX" }) CoreIdMigration.Migrate(ConsoleKind.Snes, CoreSlot.Ppu, id, ref m2, ref i2);
        Check("a store of current ids is left as it is (nothing written back)", !m2 && !i2);
        // ---- the catalog (the HTTP API's lookup, stockpiles, the Workshop's --console-run)
        foreach (var c in new[] { ConsoleKind.Snes, ConsoleKind.MasterSystem, ConsoleKind.GameGear, ConsoleKind.Genesis })
            Check($"{Consoles.DisplayName(c)}: Resolve and Find take a stored NES:DMGX to NES:DMGS", CoreCatalog.Resolve(c, CoreSlot.Ppu, "NES:DMGX") == "NES:DMGS" && CoreCatalog.Find(c, CoreSlot.Ppu, "nes:dmgx")?.Id == "NES:DMGS",
                $"{CoreCatalog.Resolve(c, CoreSlot.Ppu, "NES:DMGX")} / {CoreCatalog.Find(c, CoreSlot.Ppu, "nes:dmgx")?.Id}");
        Check("the NES console: Resolve and Find take DMGX to DMGS", CoreCatalog.Resolve(ConsoleKind.Nes, CoreSlot.Ppu, "DMGX") == "DMGS" && CoreCatalog.Find(ConsoleKind.Nes, CoreSlot.Ppu, "DMGX")?.Id == "DMGS");
        Check("negative control: the menu itself does not offer DMGX (without the migration the pick would fall to the default)", !CoreCatalog.Options(ConsoleKind.Snes, CoreSlot.Ppu).Any(o => o.Id.Equals("NES:DMGX", StringComparison.OrdinalIgnoreCase)));
        // ---- the NES board and a NES save state's core type name
        byte[] idle = SnesToNes.IdleRom();
        var board = new NES { RomName = "idle.nes" }; board.LoadROM(idle);
        Check("the NES board: SetPpuCore(\"DMGX\") gives the enhanced Game Boy chip", board.SetPpuCore("DMGX") && board.GetPpuCoreId() == "PPU_DMGS", board.GetPpuCoreId());
        var a = new NES { RomName = "idle.nes" }; a.LoadROM(idle); a.SetPpuCore("DMGS");
        if (a.GetSpeedConfig() is { } sc) { sc.CpuCyclePrecisePpu = true; sc.NtscAccurateFrameRate = true; }
        for (int i = 0; i < 3; i++) a.RunFrame();
        string json = a.SaveState();
        Check("a NES save state names its picture core by type (PPU_DMGS)", json.Contains("\"PPU_DMGS\""));
        string old = json.Replace("\"PPU_DMGS\"", "\"PPU_DMGX\"");
        var b = new NES { RomName = "idle.nes" }; b.LoadROM(idle); b.SetPpuCore("FIX");
        b.LoadState(old);
        Check("a NES save state made on the retired DMGX loads onto the enhanced Game Boy chip", b.GetPpuCoreId() == "PPU_DMGS", b.GetPpuCoreId());
        var n = new NES { RomName = "idle.nes" }; n.LoadROM(idle); n.SetPpuCore("FIX");
        n.LoadState(json.Replace("\"PPU_DMGS\"", "\"PPU_NOSUCH\""));
        Check("negative control: a state naming a core nobody knows falls back to another chip (what DMGX would do without the migration)", n.GetPpuCoreId() != "PPU_DMGS", n.GetPpuCoreId());
        // ---- a stockpile entry made on NES:DMGX, written and read back, started
        byte[] rom = BrokenNes.Workshop.Corruption.CorruptConsoleChecks.SmsRom(false);
        var pile = new Stockpile { Name = "dmgx" };
        pile.Entries.Add(new StockpileEntry { Name = "made on DMGX", Game = new GameRef { Console = "sms", Sha1 = AnchorStart.Sha1Hex(rom), File = "t.sms" }, Machine = new MachineSpec { Cpu = "Z80", Ppu = "NES:DMGX", Apu = "SMS" }, Anchor = Anchor.NoneAnchor() });
        var read = StockpileJson.Read(StockpileJson.Write(pile));
        var entry = read.Stockpile?.Entries.FirstOrDefault();
        Check("a stockpile keeps the machine's picture chip as it was written (NES:DMGX)", entry?.Machine.Ppu == "NES:DMGX", entry?.Machine.Ppu ?? "no entry");
        if (entry != null)
        {
            var started = AnchorStart.Start(entry, read.Stockpile, rom, "t.sms", ConsoleKind.MasterSystem, null);
            using var session = started.Session;
            string chip = (session as SegaPictureSession)?.PictureChip ?? session?.Description ?? started.Message;
            Check("... and starting it builds the machine on the enhanced Game Boy chip NES:DMGS, not the default", started.Ok && chip == "NES:DMGS", $"{started.Ok} {chip} {started.Message}");
        }
    }

    // =========================================================================================================== invert
    /// <summary>A shade (0 lightest .. 3 darkest) from a pixel of the Grey look (255, 170, 85, 0), by its luma.</summary>
    private static int ShadeOfPixel(uint rgb) => Math.Clamp((int)Math.Round((255 - GbColour.Luma(rgb)) / 85.0), 0, 3);

    /// <summary>The shades of the five regions every scene has: a bright background area, a dark one, the backdrop, the sprites' dark halves and their bright halves (mean over the region, rounded).</summary>
    private readonly record struct Shades(double Bright, double Dark, double Backdrop, double SprDark, double SprBright)
    {
        public override string ToString() => $"bright {Bright:F1}, dark {Dark:F1}, backdrop {Backdrop:F1}, sprite dark {SprDark:F1}, sprite bright {SprBright:F1}";
    }

    /// <summary>Measures the scene layout of every path, on a 256 x 240 canvas: the bright cells at x 56-119, the dark ones at x 136-199, both y 56-95; eight sprites at x 64 + 16 i, y 144-151
    /// (columns 0-3 dark, 4-7 bright); the backdrop at x 60-200, y 176-188. <paramref name="dy"/> moves the rows (a picture that sits lower or higher on its canvas).</summary>
    private static Shades Measure(Func<int, int, uint> px, int dx = 0, int dy = 0)
    {
        double Mean(int x0, int x1, int y0, int y1, Func<int, bool>? col = null)
        {
            double s = 0; int n = 0;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) if (col == null || col(x)) { s += ShadeOfPixel(px(x + dx, y + dy)); n++; }
            return n == 0 ? double.NaN : s / n;
        }
        bool SprDark(int x) { int k = (x - 64) & 15; return k is 1 or 2; }
        bool SprBright(int x) { int k = (x - 64) & 15; return k is 5 or 6; }
        return new Shades(Mean(59, 117, 59, 93), Mean(139, 197, 59, 93), Mean(60, 200, 176, 189), Mean(64, 192, 146, 150, SprDark), Mean(64, 192, 146, 150, SprBright));
    }

    /// <summary>The expected shades: upright by default (bright 0, dark 3), each inversion flipping its side; the backdrop flips with the background.</summary>
    private static string? Expect(Shades s, bool invBg, bool invObj, double backdrop0, bool sprites = true)
    {
        bool Near(double v, double want) => !double.IsNaN(v) && Math.Abs(v - want) < 0.35;
        if (!Near(s.Bright, invBg ? 3 : 0)) return $"bright area {s.Bright:F2}, want {(invBg ? 3 : 0)}";
        if (!Near(s.Dark, invBg ? 0 : 3)) return $"dark area {s.Dark:F2}, want {(invBg ? 0 : 3)}";
        if (!double.IsNaN(backdrop0) && !Near(s.Backdrop, invBg ? 3 - backdrop0 : backdrop0)) return $"backdrop {s.Backdrop:F2}, want {(invBg ? 3 - backdrop0 : backdrop0):F2}";
        if (sprites && !Near(s.SprBright, invObj ? 3 : 0)) return $"sprites' bright halves {s.SprBright:F2}, want {(invObj ? 3 : 0)}";
        if (sprites && !Near(s.SprDark, invObj ? 0 : 3)) return $"sprites' dark halves {s.SprDark:F2}, want {(invObj ? 0 : 3)}";
        return null;
    }

    private static readonly (bool bg, bool obj, string name)[] Looks = { (false, false, "default"), (true, false, "background inverted"), (false, true, "sprites inverted"), (true, true, "both inverted") };

    /// <summary>Renders a scene on a path for each look and checks the shades: <paramref name="render"/> draws it with the current GbLook and returns a pixel reader.</summary>
    private static void PathChecks(string path, Func<Func<int, int, uint>> render, bool sprites = true, int dy = 0)
    {
        GbLook.Palette = GbPaletteChoice.Grey;
        double backdrop0 = double.NaN;
        foreach (var (bg, obj, name) in Looks)
        {
            GbLook.InvertBackground = bg; GbLook.InvertSprites = obj;
            var s = Measure(render(), 0, dy);
            if (!bg && !obj)
            {
                backdrop0 = Math.Round(s.Backdrop);
                Check($"{path}: the backdrop is a shade between the bright and the dark colours (its own brightness)", s.Backdrop is >= 0.6 and <= 2.4, s.ToString());
            }
            string? why = Expect(s, bg, obj, backdrop0, sprites);
            Check($"{path}, {name}: {(bg || obj ? "the inverted side flips, the other stays" : "upright (bright light, dark dark)")}", why == null, why == null ? s.ToString() : why + "; " + s);
        }
        GbLook.InvertBackground = GbLook.InvertSprites = false;
    }

    /// <summary>The control: with <paramref name="mutant"/> on, the default picture of the path is NOT upright (the checks above would fail).</summary>
    private static void Control(string path, Func<Func<int, int, uint>> render, Action<bool> mutant, string what, bool sprites = true, int dy = 0)
    {
        GbLook.Palette = GbPaletteChoice.Grey; GbLook.InvertBackground = GbLook.InvertSprites = false;
        mutant(true);
        try
        {
            var s = Measure(render(), 0, dy);
            Check($"negative control: {path} with {what} is caught", Expect(s, false, false, double.NaN, sprites) != null, s.ToString());
        }
        finally { mutant(false); }
    }

    private static void ControlBackdrop(string path, Func<Func<int, int, uint>> render, int dy = 0)
    {
        GbLook.Palette = GbPaletteChoice.Grey; GbLook.InvertSprites = false;
        GbLook.InvertBackground = false; double b0 = Measure(render(), 0, dy).Backdrop;
        GbLook.InvertBackground = true; PPU_GBE.BackdropIgnoresInversionForTests = true;
        try { var s = Measure(render(), 0, dy); Check($"negative control: {path} with the backdrop left out of the background's inversion (as before) is caught", Expect(s, true, false, Math.Round(b0)) != null, s.ToString()); }
        finally { PPU_GBE.BackdropIgnoresInversionForTests = false; GbLook.InvertBackground = false; }
    }

    private static Func<int, int, uint> Rgba(byte[] f) => (x, y) => { int o = (y * 256 + x) * 4; return (uint)(f[o] << 16 | f[o + 1] << 8 | f[o + 2]); };
    /// <summary>A pixel reader in the scene's 256 x 240 canvas coordinates: a picture at the guest's own size sits at (<paramref name="ox"/>, <paramref name="oy"/>) of that canvas.</summary>
    private static Func<int, int, uint> Argb(uint[] f, int w = 256, int ox = 0, int oy = 0) => (x, y) => f[(y - oy) * w + x - ox] & 0xFFFFFF;
    /// <summary>A translator's picture: the NES's 256 x 240 as it is, the guest's own size (an enhanced chip) placed at (<paramref name="ox"/>, <paramref name="oy"/>).</summary>
    private static Func<int, int, uint> Seen(BridgeFrame f, int ox, int oy) => f.GuestSize ? Argb(f.Argb, f.Width, ox, oy) : Argb(f.Argb);
    private static Func<int, int, uint> Seen(SegaPictureSession w, int ox, int oy) => w.GuestSize ? Argb(w.Frame, w.FrameWidth, ox, oy) : Argb(w.Frame);

    private static void Inversions()
    {
        // ---- the colour maths
        Check("GbColour: white and black swap, a mid-lightness colour stays, dark blue becomes light blue (hue kept)",
            GbColour.FlipLightness(0xFFFFFF) == 0 && GbColour.FlipLightness(0) == 0xFFFFFF && GbColour.FlipLightness(0x0000FF) == 0x0000FF && GbColour.FlipLightness(0x000080) == 0x7F7FFF,
            $"{GbColour.FlipLightness(0x000080):X6}");
        ushort darkBlue = 16 << 10, flipped = GbColour.FlipLightness555(darkBlue), negative = (ushort)(darkBlue ^ 0x7FFF);
        bool BlueLed(ushort c) => (c >> 10 & 31) > (c & 31) && (c >> 10 & 31) > (c >> 5 & 31);
        Check("GbColour (BGR555): dark blue's lightness mirrored is a light blue, brighter, still blue", BlueLed(flipped) && GbColour.Luma(Rgb555(flipped)) > GbColour.Luma(Rgb555(darkBlue)), $"{flipped:X4}");
        Check("negative control: the old CGB inversion (the colour negative) turns dark blue yellow: the hue check catches it", !BlueLed(negative), $"{negative:X4}");

        // ---- a NES game on the Game Boy chips (the NES's own picture on the stock and the enhanced chip)
        foreach (var chip in new[] { "DMG", "DMGS" })
        {
            Func<Func<int, int, uint>> nesScene = () => Rgba(NesScene(chip));
            PathChecks($"NES game on Game Boy {(chip == "DMG" ? "stock" : "enhanced")} ({chip})", nesScene);
            Control($"NES game on {chip}", nesScene, chip == "DMG" ? on => PPU_DMG.IndexShadesForTests = on : on => PPU_GBE.IndexShadesForTests = on, "the old index-as-shade mapping (its palette has dark colour 1 and bright colour 3)");
        }
        GbLook.Palette = GbPaletteChoice.Color;
        foreach (var chip in new[] { "DMG", "DMGS" })
        {
            GbLook.InvertBackground = GbLook.InvertSprites = false;
            var d = Rgba(NesScene(chip)); double bright = Lum(d, 60, 116, 60, 92), dark = Lum(d, 140, 196, 60, 92), sb = Lum(d, 64, 192, 146, 150, x => ((x - 64) & 15) is 5 or 6);
            GbLook.InvertBackground = true;
            var i = Rgba(NesScene(chip)); double ibright = Lum(i, 60, 116, 60, 92), idark = Lum(i, 140, 196, 60, 92), isb = Lum(i, 64, 192, 146, 150, x => ((x - 64) & 15) is 5 or 6);
            Check($"NES game on {chip} as a Game Boy Color: bright is bright, and the background inversion mirrors it (the sprites stay)", bright > dark + 60 && ibright < idark - 60 && Math.Abs(sb - isb) < 8, $"{bright:F0}/{dark:F0} -> {ibright:F0}/{idark:F0}; sprite {sb:F0} -> {isb:F0}");
            GbLook.InvertBackground = false;
        }
        // ---- a SNES game through the NES (the flat translator onto the stock chip, the layered one onto the enhanced chip)
        foreach (var chip in new[] { "DMG", "DMGS" })
        {
            Func<Func<int, int, uint>> snes = () => Seen(SnesScene(chip), 0, 8);       // the SNES picture's line 0 is the NES canvas's line 8
            PathChecks($"SNES game on Game Boy {(chip == "DMG" ? "stock" : "enhanced")} (NES:{chip})", snes);
            if (chip == "DMG") Control("SNES game on NES:DMG", snes, on => PPU_DMG.IndexShadesForTests = on, "the old index-as-shade mapping (the translator's levels run dark to bright)");
            else ControlBackdrop("SNES game on NES:DMGS", snes);
        }
        // ---- the Sega consoles through the NES
        foreach (var chip in new[] { "NES:DMG", "NES:DMGS" })
        {
            var sms = new SegaScenes.SmsRig(chip); SmsScene(sms);
            Func<Func<int, int, uint>> smsR = () => { sms.Frames(3); return Seen(sms.Wrap, 0, 24); };
            PathChecks($"Master System game on Game Boy {(chip == "NES:DMG" ? "stock" : "enhanced")} ({chip})", smsR);
            var gg = new SegaScenes.SmsRig(chip, gg: true); SmsScene(gg, gameGear: true);
            Func<Func<int, int, uint>> ggR = () => { gg.Frames(3); return Seen(gg.Wrap, 48, 48); };
            PathChecks($"Game Gear game on Game Boy {(chip == "NES:DMG" ? "stock" : "enhanced")} ({chip})", ggR);
            var md = new SegaScenes.MdRig(chip); MdScene(md);
            Func<Func<int, int, uint>> mdR = () => { md.Go(3); return Seen(md.Wrap, 0, 8); };
            PathChecks($"Genesis game on Game Boy {(chip == "NES:DMG" ? "stock" : "enhanced")} ({chip})", mdR);
            if (chip == "NES:DMG")
            {
                Control("Master System game on NES:DMG", smsR, on => PPU_DMG.IndexShadesForTests = on, "the old index-as-shade mapping");
                Control("Genesis game on NES:DMG", mdR, on => PPU_DMG.IndexShadesForTests = on, "the old index-as-shade mapping");
            }
            else { ControlBackdrop("Master System game on NES:DMGS", smsR); ControlBackdrop("Genesis game on NES:DMGS", mdR); }
        }
        // ---- a Game Boy game drawn by a NES chip and by the SNES chip (the Game Boy's own shades, upright by construction; the inversions the same way)
        foreach (var ppu in new[] { "NES:FIX", "NES:FIXS", "SNES:SFC" })
        {
            using var s = GbScene(ppu, color: false);
            int oy = ppu == "SNES:SFC" ? 40 - 48 : 0;      // the SNES picture puts the Game Boy screen at (48, 40), the NES one at (48, 48); the enhanced chip draws the Game Boy's own 160 x 144
            Func<Func<int, int, uint>> gbR = () => { for (int f = 0; f < 3; f++) s.RunFrame(); return s.FrameWidth == 160 ? Argb(s.Frame, 160, 48, 48) : Argb(s.Frame, s.FrameWidth); };
            PathChecks($"Game Boy game on {ppu}", gbR, dy: oy);
        }
        using (var s = GbScene("NES:FIX", color: true))
        {
            GbLook.Palette = GbPaletteChoice.Green; GbLook.InvertBackground = false;
            for (int f = 0; f < 3; f++) s.RunFrame();
            var d = Argb(s.Frame); uint before = d(80, 70);
            GbLook.InvertBackground = true;
            for (int f = 0; f < 3; f++) s.RunFrame();
            var i = Argb(s.Frame); uint after = i(80, 70);
            bool blue(uint c) => (c & 0xFF) > (c >> 16 & 0xFF) && (c & 0xFF) > (c >> 8 & 0xFF);
            Check("Game Boy Color game on NES:FIX: the background inversion turns its dark blue into a light blue (hue kept), not a yellow", blue(before) && blue(after) && GbColour.Luma(after) > GbColour.Luma(before) + 40, $"{before:X6} -> {after:X6}");
            GbLook.InvertBackground = false;
        }
    }

    private static uint Rgb555(ushort c) { var (r, g, b) = NesPalette.FromBgr555(c); return (uint)(r << 16 | g << 8 | b); }

    private static double Lum(Func<int, int, uint> px, int x0, int x1, int y0, int y1, Func<int, bool>? col = null)
    {
        double s = 0; int n = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) if (col == null || col(x)) { s += GbColour.Luma(px(x, y)); n++; }
        return s / Math.Max(1, n);
    }

    // ---- the scenes: the same layout on every path (see Measure)

    /// <summary>A NES picture whose colour indices run AGAINST brightness (colour 1 black, colour 3 white, the backdrop light grey), drawn by a NES on <paramref name="chip"/>.</summary>
    private static byte[] NesScene(string chip)
    {
        var p = new NesPicture();
        for (int r = 0; r < 8; r++) { p.ChrBg[16 + r] = 0xFF; p.ChrBg[32 + r] = 0xFF; p.ChrBg[32 + 8 + r] = 0xFF; }     // tile 1 all colour 1, tile 2 all colour 3
        for (int r = 0; r < 8; r++) { p.ChrObj[16 + r] = 0xFF; p.ChrObj[16 + 8 + r] = 0x0F; }                           // sprite tile 1: columns 0-3 colour 1, 4-7 colour 3
        p.Pal[0] = 0x10; p.Pal[1] = 0x0F; p.Pal[2] = 0x00; p.Pal[3] = 0x30;
        p.Pal[17] = 0x0F; p.Pal[18] = 0x00; p.Pal[19] = 0x30;
        for (int row = 7; row < 12; row++) { for (int col = 7; col < 15; col++) p.Nt0[row * 32 + col] = 2; for (int col = 17; col < 25; col++) p.Nt0[row * 32 + col] = 1; }
        Array.Fill(p.Oam, (byte)0xFF);
        for (int i = 0; i < 8; i++) { p.Oam[i * 4] = 143; p.Oam[i * 4 + 1] = 1; p.Oam[i * 4 + 2] = 0; p.Oam[i * 4 + 3] = (byte)(64 + 16 * i); }
        string key = chip + (GbLook.Color ? ":cgb" : ":dmg");      // the Game Boy chip is a DMG or a GBC from when it is made
        var sink = sinks.TryGetValue(key, out var s) ? s : sinks[key] = new NesPictureSink(chip);
        sink.Present(p);
        return sink.Present(p);
    }
    private static readonly Dictionary<string, NesPictureSink> sinks = new();

    /// <summary>A SNES mode 1 picture: BG1 white and black cells, a grey backdrop, eight sprites striped black | white; drawn through the NES on <paramref name="chip"/>.</summary>
    private static BridgeFrame SnesScene(string chip)
    {
        var sp = SnesCores.CreatePpu("SFC");
        var v = sp.Vram; var cg = sp.Cgram; var oam = sp.Oam;
        cg[0] = 16 | 16 << 5 | 16 << 10; cg[1] = 0; cg[2] = 0x7FFF;                       // backdrop grey, colour 1 black, colour 2 white
        cg[128 + 1] = 0; cg[128 + 2] = 0x7FFF;                                              // sprite palette 0: 1 black, 2 white
        void Tile4(int at, Func<int, int> colourOfColumn)
        {
            for (int r = 0; r < 8; r++)
            {
                int p0 = 0, p1 = 0, p2 = 0, p3 = 0;
                for (int x = 0; x < 8; x++) { int c = colourOfColumn(x); int bit = 7 - x; p0 |= (c & 1) << bit; p1 |= (c >> 1 & 1) << bit; p2 |= (c >> 2 & 1) << bit; p3 |= (c >> 3 & 1) << bit; }
                v[at + r] = (ushort)(p0 | p1 << 8); v[at + 8 + r] = (ushort)(p2 | p3 << 8);
            }
        }
        Tile4(1 * 16, _ => 1); Tile4(2 * 16, _ => 2);                                       // BG tiles at word 0: tile 1 black, tile 2 white
        Tile4(0x4000 + 1 * 16, x => x < 4 ? 1 : 2);                                          // sprite tile 1 at the OBJ base (word $4000)
        for (int row = 6; row < 11; row++) { for (int col = 7; col < 15; col++) v[0x1000 + row * 32 + col] = 2; for (int col = 17; col < 25; col++) v[0x1000 + row * 32 + col] = 1; }
        for (int i = 0; i < 128; i++) { oam[i * 4] = 0; oam[i * 4 + 1] = 240; oam[i * 4 + 2] = 0; oam[i * 4 + 3] = 0; }
        for (int i = 0; i < 32; i++) oam[512 + i] = 0;
        for (int i = 0; i < 8; i++) { oam[i * 4] = (byte)(64 + 16 * i); oam[i * 4 + 1] = 136; oam[i * 4 + 2] = 1; oam[i * 4 + 3] = 0x30; }   // priority 3, palette 0
        sp.InvalidateCaches();
        sp.WriteRegister(0x00, 0x0F); sp.WriteRegister(0x05, 0x01); sp.WriteRegister(0x07, 0x10); sp.WriteRegister(0x0B, 0x00);
        sp.WriteRegister(0x01, 0x02); sp.WriteRegister(0x2C, 0x11); sp.WriteRegister(0x2D, 0x00);
        string key = chip + (GbLook.Color ? ":cgb" : ":dmg");
        var down = snesDown.TryGetValue(key, out var d) ? d : snesDown[key] = new SnesToNes(chip);
        down.Render(sp);
        return down.Render(sp);
    }
    private static readonly Dictionary<string, SnesToNes> snesDown = new();

    /// <summary>The scene on a Master System / Game Gear: background colour 1 black and 2 white, the backdrop (and colour 0) light grey, sprite colour 1 black and 2 white.</summary>
    private static void SmsScene(SegaScenes.SmsRig r, bool gameGear = false)
    {
        if (gameGear) { r.Gg(0, 0xAAA); r.Gg(1, 0x000); r.Gg(2, 0xFFF); r.Gg(16, 0xAAA); r.Gg(17, 0x000); r.Gg(18, 0xFFF); }
        else { r.Cram(0, 0x2A); r.Cram(1, 0x00); r.Cram(2, 0x3F); r.Cram(16, 0x2A); r.Cram(17, 0x00); r.Cram(18, 0x3F); }
        r.Tile(1, 1); r.Tile(2, 2); r.Stripe(3, 1, 2);
        // canvas y = 24 + VDP y (the 192-line picture centred; the Game Gear's window too), x = VDP x
        for (int row = 4; row < 9; row++) { for (int col = 7; col < 15; col++) r.Cell(row, col, 2); for (int col = 17; col < 25; col++) r.Cell(row, col, 1); }
        for (int i = 0; i < 8; i++) r.Sprite(i, 64 + 16 * i, 119, 3);           // drawn from line 120: canvas 144
        r.V.Vram[0x3F00 + 8] = 0xD0;
    }

    /// <summary>The scene on a Genesis (H32, 224 lines at canvas line 8): plane A colour 1 black and 2 white, the backdrop (colour 0) grey, sprites on palette line 1 striped black | white.</summary>
    private static void MdScene(SegaScenes.MdRig r)
    {
        r.Color(0, 0x666); r.Color(1, 0x000); r.Color(2, 0xEEE); r.Color(17, 0x000); r.Color(18, 0xEEE);
        r.Tile(1, 1); r.Tile(2, 2); r.Stripe(3, 1, 2);
        for (int row = 6; row < 11; row++) { for (int col = 7; col < 15; col++) r.Cell(SegaScenes.MdRig.PlaneA, 64, row, col, 2); for (int col = 17; col < 25; col++) r.Cell(SegaScenes.MdRig.PlaneA, 64, row, col, 1); }
        for (int i = 0; i < 8; i++) r.Sprite(i, 64 + 16 * i, 136, 1, 1, 3, pal: 1, link: i == 7 ? 0 : i + 1);
    }

    /// <summary>A Game Boy game (a program that only loops) with its memory set to the scene: background colour 1 cells (BGP: shade 0, the lightest) and colour 3 cells (shade 3),
    /// the backdrop colour 0 (shade 1), sprites striped colour 1 | colour 3 (OBP0: shade 3 | shade 0) - or (<paramref name="color"/>) a Game Boy Color whose palette 0 has colour 1 a dark blue.</summary>
    private static IConsoleSession GbScene(string ppu, bool color)
    {
        var rom = BrokenNes.Workshop.Corruption.CorruptConsoleChecks.GbRom(color);
        rom[0x150] = 0x18; rom[0x151] = 0xFE;                                                 // JR $ (the counter program's loop replaced: nothing writes the palettes)
        var s = new GbSession(rom, color ? ConsoleKind.GameBoyColor : ConsoleKind.GameBoy, "GB", ppu, "GB");
        var p = s.Board.Ppu;
        for (int r = 0; r < 8; r++) { p.Vram[16 + r * 2] = 0xFF; p.Vram[32 + r * 2] = 0xFF; p.Vram[32 + r * 2 + 1] = 0xFF; p.Vram[48 + r * 2] = 0xFF; p.Vram[48 + r * 2 + 1] = 0x0F; }
        Array.Clear(p.Vram, 0x1800, 0x400);
        // the Game Boy screen sits at canvas (48, 48): screen x = canvas x - 48
        for (int row = 1; row < 6; row++) { for (int col = 1; col < 9; col++) p.Vram[0x1800 + row * 32 + col] = 1; for (int col = 11; col < 19; col++) p.Vram[0x1800 + row * 32 + col] = 2; }
        Array.Clear(p.Oam);
        for (int i = 0; i < 8; i++) { p.Oam[i * 4] = 96 + 16; p.Oam[i * 4 + 1] = (byte)(16 + 16 * i + 8); p.Oam[i * 4 + 2] = 3; p.Oam[i * 4 + 3] = 0; }
        p.Bgp = 0xE1; p.Obp0 = 0x0C; p.Lcdc = 0x93;           // BGP: colour 0 -> shade 1, 1 -> 0, 2 -> 2, 3 -> 3; OBP0: colour 1 -> shade 3, 3 -> shade 0
        if (color)
        {
            void Pal(byte[] ram, int c, ushort bgr) { ram[c * 2] = (byte)bgr; ram[c * 2 + 1] = (byte)(bgr >> 8); }
            Pal(p.BgPalRam, 0, 0x7FFF); Pal(p.BgPalRam, 1, 16 << 10); Pal(p.BgPalRam, 2, 0x4210); Pal(p.BgPalRam, 3, 0);
        }
        return s;
    }
}
