using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using BrokenNes.Models;
using BrokenNes.Windows.Rendering;
using NesEmulator;
using NesEmulator.Mix;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    /// <summary>
    /// BrokenNes 2: the console selector (NES, SNES, Game Boy, Game Boy Color), SNES and Game Boy games running through
    /// an <see cref="IConsoleSession"/> in the same emulation loop, per-console core menus (own family first, the other
    /// consoles' parts after) and per-console battery saves. The NES keeps its original path (the NES object).
    /// </summary>
    public partial class MainForm
    {
        private IConsoleSession? session;
        private byte[]? sessionRom;
        private string sessionRomName = "";
        private readonly MonoResampler sessionResampler = new();
        private readonly short[] sessionAudio = new short[65536];
        private byte[]? sessionLastSave;
        private long sessionFrames;
        private int displayWidth = NES_WIDTH, displayHeight = NES_HEIGHT;
        private ToolStripMenuItem? consoleMenu;

        private ConsoleKind SelectedConsole => Consoles.FromKey(config.SelectedConsole);

        // ------------------------------------------------------------------ cores per console
        private string SelectedCore(ConsoleKind console, CoreSlot slot)
        {
            if (console == ConsoleKind.Nes)
                return slot switch { CoreSlot.Cpu => config.SelectedCpuCore, CoreSlot.Ppu => config.SelectedPpuCore, _ => config.SelectedApuCore };
            config.ConsoleCores.TryGetValue(Consoles.Key(console), out var sel);
            string? stored = slot switch { CoreSlot.Cpu => sel?.Cpu, CoreSlot.Ppu => sel?.Ppu, _ => sel?.Apu };
            return CoreCatalog.Resolve(console, slot, stored);
        }

        /// <summary>A SNES / Game Boy core pick: stored for that console and hot-swapped into the running game, like the
        /// NES's (the session carries memory, picture and sound state over).</summary>
        private void SetConsoleCore(ConsoleKind console, CoreSlot slot, string id)
        {
            Helpers.ConfigHelper.Update(config, c =>
            {
                var key = Consoles.Key(console);
                if (!c.ConsoleCores.TryGetValue(key, out var sel)) c.ConsoleCores[key] = sel = new ConsoleCoreSelection();
                switch (slot) { case CoreSlot.Cpu: sel.Cpu = id; break; case CoreSlot.Ppu: sel.Ppu = id; break; default: sel.Apu = id; break; }
            });
            if (session != null && sessionRom != null && session.Console == console)
            {
                // Hot swap: the game keeps running on the new part (memory, picture and sound state carry over). Only a
                // swap the session cannot do in place restarts the game.
                bool swapped; string why = "";
                lock (emulationLock) { swapped = session.TrySwapCore(slot, id); if (!swapped) why = session.SwapRefusal(slot, id); }
                if (!swapped)
                {
                    // the game restarts on the new part, and the title says why (never a silent restart)
                    Console.WriteLine($"[Console] {slot} -> {id} restarts the game: {why}");
                    Diagnostics.ShutdownDiagnostics.Log($"Core swap restarts the game: {console} {slot} -> {id}: {why}");
                    StartSession(console, sessionRom, sessionRomName, currentRomPath);
                    consoleNote = $"restarted on {slot.ToString().ToUpperInvariant()} {id}: {why}";
                    UpdateConsoleTitle();
                    return;
                }
                consoleNote = "";
                if (slot != CoreSlot.Ppu) FarmPoison($"the {slot} was swapped to {id}");   // (a picture chip is a view of the machine; the others change what it does)
                FarmCoresChanged(session);
                Diagnostics.ShutdownDiagnostics.Log($"Core hot-swap: {console} {slot} -> {id} ({session.Description})");
                UpdateCoresMenus();
                UpdateConsoleTitle();
            }
            else UpdateCoresMenus();
        }

        /// <summary>The picture bridge in play: a Sega session whose picture a NES chip draws, or the NES's own PPU core when it is the Master System / Genesis chip.</summary>
        private NesEmulator.Mix.IPictureBridgeInfo? BridgeOf(IConsoleSession? s) => (s as NesEmulator.Mix.IPictureBridgeInfo) ?? (s == null ? nes?.GetPictureBridge() : null);

        /// <summary>The NES bridge picture chips (SNES / Game Boy chips on a NES) need the board's cycle-precise PPU
        /// stepping, as in the mix lab; every other chip keeps the timing the user picked.</summary>
        private void ApplyBridgeTiming()
        {
            var sc = nes?.GetSpeedConfig();
            if (sc == null || nes == null) return;
            string ppu = nes.GetPpuCoreId().Replace("PPU_", "", StringComparison.OrdinalIgnoreCase);
            bool bridge = CoreCatalog.NesPpuNeedsPreciseTiming(ppu);
            sc.CpuCyclePrecisePpu = bridge;
            sc.NtscAccurateFrameRate = bridge || config.NtscAccurateFrameRate;
        }

        // ------------------------------------------------------------------ HTTP API (automation, UAT)
        private void WireConsoleApi(WebApi.WebApiServer server)
        {
            // the debugger (BrokenDev D1): the running session (the NES is _getNes), the opt-in for its control routes, and the emulation
            // lock a parked emulation thread releases
            server.GetConsoleSession = () => session;
            server.DebuggerControlEnabled = () => config.EnableDebuggerControl;
            server.Debuggers.HostLock = emulationLock;
            server.GetConsoleStatus = () =>
            {
                var s = session; var console = s?.Console ?? SelectedConsole;
                return new WebApi.ConsoleStatus
                {
                    Console = Consoles.Key(console), ConsoleName = Consoles.DisplayName(console),
                    Game = s?.Title ?? (nes != null && !string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase) ? nes.RomName : null),
                    Description = s?.Description,
                    Cpu = console == ConsoleKind.Nes ? nes?.GetCpuCoreId() : SelectedCore(console, CoreSlot.Cpu),
                    Ppu = console == ConsoleKind.Nes ? nes?.GetPpuCoreId() : SelectedCore(console, CoreSlot.Ppu),
                    Apu = console == ConsoleKind.Nes ? nes?.GetApuCoreId() : SelectedCore(console, CoreSlot.Apu),
                    FrameWidth = s?.FrameWidth ?? NES_WIDTH, FrameHeight = s?.FrameHeight ?? NES_HEIGHT, Fps = currentFps,
                    // a picture that crosses a bridge says what it is and what it lost (Sega game on a NES chip, NES game on the Master System / Genesis chip)
                    PictureBridge = BridgeOf(s) is { } bi && bi.BridgeName != "none" ? bi.BridgeName : null,
                    PictureBridgeCounters = BridgeOf(s) is { } bc && bc.BridgeName != "none" ? new Dictionary<string, long>(bc.Counters.Totals) : null,
                    PictureBridgeSummary = BridgeOf(s) is { } bs && bs.BridgeName != "none" ? bs.Counters.Summary(12) : null,
                    // the machine a Sega game runs as, and what the session can do with saves and states (the reason when it cannot)
                    Region = SegaMachineOf(s)?.region, Model = SegaMachineOf(s)?.model,
                    HasBattery = s?.HasBattery, SupportsState = s?.SupportsState, StateUnsupportedReason = s != null && !s.SupportsState ? s.StateUnsupportedReason : null,
                    Note = s != null && consoleNote.Length > 0 ? consoleNote : null,
                };
            };
            server.QuickStateReason = () => quickStateReason;
            WireWideMessApi(server);   // WideMess milestone 6 (MainForm.WideMess.cs)
            server.GetSegaSettings = () => new
            {
                region = NesEmulator.Sega.SegaOptions.Key(NesEmulator.Sega.SegaOptions.RegionFromKey(config.SegaRegion)),
                genesisModel = NesEmulator.Sega.SegaOptions.Key(NesEmulator.Sega.SegaOptions.GenesisModelFromKey(config.GenesisModel)),
                masterSystemModel = NesEmulator.Sega.SegaOptions.Key(NesEmulator.Sega.SegaOptions.MasterSystemModelFromKey(config.MasterSystemModel)),
                running = SegaMachineOf(session) is { } m ? new { region = m.region, model = m.model } : null,
                choices = new
                {
                    region = NesEmulator.Sega.SegaOptions.RegionChoices.Select(c => new { key = c.Key, label = c.Label }),
                    genesisModel = NesEmulator.Sega.SegaOptions.GenesisModelChoices.Select(c => new { key = c.Key, label = c.Label }),
                    masterSystemModel = NesEmulator.Sega.SegaOptions.MasterSystemModelChoices.Select(c => new { key = c.Key, label = c.Label }),
                },
            };
            server.SetSegaSettings = (region, genesisModel, masterSystemModel) => { bool ok = false; Invoke(() => ok = SetSegaOptions(region, genesisModel, masterSystemModel)); return ok; };
            server.GetConsoleFrame = () =>
            {
                var s = session;
                if (s == null) return null;
                lock (emulationLock)
                {
                    int w = s.FrameWidth, h = s.FrameHeight; var src = s.Frame; var rgba = new byte[w * h * 4];
                    for (int i = 0; i < w * h; i++) { uint c = src[i]; rgba[i * 4] = (byte)(c >> 16); rgba[i * 4 + 1] = (byte)(c >> 8); rgba[i * 4 + 2] = (byte)c; rgba[i * 4 + 3] = 255; }
                    return (w, h, rgba);
                }
            };
            // the Glitch Harvester on console games and the stockpile file
            server.GetConsoleGh = () => ConsoleGh;
            server.GetFarmInfo = FarmInfo;
            server.PickStockpilePath = save => PickStockpilePath(save);
            server.GetStockpileFolder = () => StockpileFolder;
            server.GetTrueDomainSizes = () => config.CorruptionTrueDomainSizes;
            server.SetTrueDomainSizes = on => { Helpers.ConfigHelper.Update(config, c => c.CorruptionTrueDomainSizes = on); RefreshMemoryDomainsRequested(); };
            server.GetConsoleDomains = () => farm.Attached ? farm.Domains() : System.Array.Empty<NesEmulator.Corruption.IMemoryDomain>();
            server.ConsoleBetweenFrames = work => { lock (emulationLock) return work(); };
            server.SelectConsole = key => Invoke(() => SwitchConsole(Consoles.FromKey(key)));
            server.GetGbLook = () => (config.GbLook, config.GbInvertBackground, config.GbInvertSprites);
            server.SetGbLook = (look, bg, obj) => Invoke(() => SetGbLook(look == null ? null : GbLook.FromKey(look), bg, obj));
            server.SelectConsoleCore = (slotName, id) =>
            {
                var slot = slotName.ToLowerInvariant() switch { "cpu" => CoreSlot.Cpu, "ppu" => CoreSlot.Ppu, _ => CoreSlot.Apu };
                var console = session?.Console ?? SelectedConsole;
                var opt = CoreCatalog.Find(console, slot, id);     // a retired id (DMGX, NES:DMGX) is its successor
                if (opt == null) return false;
                Invoke(() =>
                {
                    if (console != ConsoleKind.Nes) SetConsoleCore(console, slot, opt.Id);
                    else if (slot == CoreSlot.Cpu) SetCpuCore(opt.Id);
                    else if (slot == CoreSlot.Ppu) SetPpuCore(opt.Id);
                    else SetApuCore(opt.Id);
                });
                return true;
            };
            server.GetConsoleCoreMenus = () =>
            {
                var console = session?.Console ?? SelectedConsole;
                return new
                {
                    console = Consoles.Key(console),
                    cpu = CoreCatalog.Options(console, CoreSlot.Cpu),
                    ppu = CoreCatalog.Options(console, CoreSlot.Ppu),
                    apu = CoreCatalog.Options(console, CoreSlot.Apu),
                };
            };
        }

        // ------------------------------------------------------------------ the Legacy menu
        /// <summary>The BrokenNes 1 campaign: Deck Builder (through its home hub), and the old behaviours as opt-ins.</summary>
        private ToolStripMenuItem BuildLegacyMenu()
        {
            var legacy = new ToolStripMenuItem("&Legacy");
            legacy.DropDownItems.Add(new ToolStripMenuItem("&Deck Builder (the BrokenNes 1 campaign)", null, (s, e) =>
            {
                if (session != null) { StopSession(); LoadEmbeddedRom(allowHomeWebModule: false); }
                LoadHomeWebModule();
            }));
            legacy.DropDownItems.Add(new ToolStripSeparator());
            var bootHome = new ToolStripMenuItem("Start on the BrokenNes 1 home screen") { CheckOnClick = true };
            bootHome.Click += (s, e) => Helpers.ConfigHelper.Update(config, c => c.BootToEmulator = !bootHome.Checked);
            var locks = new ToolStripMenuItem("BrokenNes 1 unlock locks (cores, shaders, tools)") { CheckOnClick = true };
            locks.Click += (s, e) =>
            {
                Helpers.ConfigHelper.Update(config, c => c.LegacyProgressionLocks = locks.Checked);
                RefreshProgressionUi();
                UpdateCoresMenus();
            };
            legacy.DropDownItems.Add(bootHome);
            legacy.DropDownItems.Add(locks);
            legacy.DropDownOpening += (s, e) => { bootHome.Checked = !config.BootToEmulator; locks.Checked = config.LegacyProgressionLocks; };
            return legacy;
        }

        // ------------------------------------------------------------------ the Console menu
        private ToolStripMenuItem BuildConsoleMenu()
        {
            consoleMenu = new ToolStripMenuItem("C&onsole");
            foreach (var k in Consoles.All)
            {
                var kind = k;
                var item = new ToolStripMenuItem(Consoles.DisplayName(kind), null, (s, e) => SwitchConsole(kind)) { Tag = kind };
                consoleMenu.DropDownItems.Add(item);
            }
            consoleMenu.DropDownItems.Add(new ToolStripSeparator());
            consoleMenu.DropDownItems.Add(BuildGbLookMenu());
            consoleMenu.DropDownItems.Add(BuildSegaMenu());
            consoleMenu.DropDownItems.Add(BuildWideMessMenu());   // WideMess milestone 6: the SNES games' enhancements (MainForm.WideMess.cs)
            consoleMenu.DropDownOpening += (s, e) => RefreshConsoleMenu();
            RefreshConsoleMenu();
            return consoleMenu;
        }

        // ------------------------------------------------------------------ the Game Boy look
        private ToolStripMenuItem? gbLookMenu, gbInvertBgItem, gbInvertObjItem;

        /// <summary>Console > Game Boy look: the screen colours wherever a Game Boy picture is shown, and the lightness
        /// inversions for cross-console pictures (a game drawn for another console can have light and dark swapped).</summary>
        private ToolStripMenuItem BuildGbLookMenu()
        {
            gbLookMenu = new ToolStripMenuItem("Game Boy look");
            (GbPaletteChoice look, string label)[] looks =
            {
                (GbPaletteChoice.Green, "Original green (DMG)"), (GbPaletteChoice.Grey, "Greyscale"),
                (GbPaletteChoice.Pocket, "Game Boy Pocket"), (GbPaletteChoice.Color, "Game Boy Color"),
            };
            foreach (var (look, label) in looks)
            {
                var l = look;
                gbLookMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (s, e) => SetGbLook(l, null, null)) { Tag = l });
            }
            gbLookMenu.DropDownItems.Add(new ToolStripSeparator());
            gbLookMenu.DropDownItems.Add(new ToolStripMenuItem("Cross-console pictures:") { Enabled = false });
            gbInvertBgItem = new ToolStripMenuItem("Invert background lightness", null, (s, e) => SetGbLook(null, !config.GbInvertBackground, null))
            { ToolTipText = "Game Boy pictures of NES / SNES games, and NES / SNES pictures of Game Boy games: background light and dark swapped" };
            gbInvertObjItem = new ToolStripMenuItem("Invert sprite lightness", null, (s, e) => SetGbLook(null, null, !config.GbInvertSprites))
            { ToolTipText = "Game Boy pictures of NES / SNES games, and NES / SNES pictures of Game Boy games: sprite light and dark swapped" };
            gbLookMenu.DropDownItems.Add(gbInvertBgItem);
            gbLookMenu.DropDownItems.Add(gbInvertObjItem);
            gbLookMenu.DropDownOpening += (s, e) => RefreshGbLookMenu();
            return gbLookMenu;
        }

        private void RefreshGbLookMenu()
        {
            if (gbLookMenu == null) return;
            var look = GbLook.FromKey(config.GbLook);
            foreach (var item in gbLookMenu.DropDownItems.OfType<ToolStripMenuItem>())
                if (item.Tag is GbPaletteChoice p) item.Checked = p == look;
            if (gbInvertBgItem != null) gbInvertBgItem.Checked = config.GbInvertBackground;
            if (gbInvertObjItem != null) gbInvertObjItem.Checked = config.GbInvertSprites;
        }

        /// <summary>Apply the saved Game Boy look to the engine (at start-up, after the config loads). Also moves the stored picks of retired
        /// cores to their successors and writes the config back (<see cref="CoreIdMigration"/>: the big-screen Game Boy chip DMGX is now the
        /// enhanced Game Boy chip DMGS; the retired DMGXI / DMGSI also switch the sprite inversion on) - never a silent reset to the default.</summary>
        private void ApplyGbLook()
        {
            bool migrated = false, invertSprites = false;
            config.SelectedCpuCore = CoreIdMigration.Migrate(ConsoleKind.Nes, CoreSlot.Cpu, config.SelectedCpuCore, ref migrated, ref invertSprites)!;
            config.SelectedPpuCore = CoreIdMigration.Migrate(ConsoleKind.Nes, CoreSlot.Ppu, config.SelectedPpuCore, ref migrated, ref invertSprites)!;
            config.SelectedApuCore = CoreIdMigration.Migrate(ConsoleKind.Nes, CoreSlot.Apu, config.SelectedApuCore, ref migrated, ref invertSprites)!;
            foreach (var (key, sel) in config.ConsoleCores)
            {
                var console = Consoles.FromKey(key);
                sel.Cpu = CoreIdMigration.Migrate(console, CoreSlot.Cpu, sel.Cpu, ref migrated, ref invertSprites);
                sel.Ppu = CoreIdMigration.Migrate(console, CoreSlot.Ppu, sel.Ppu, ref migrated, ref invertSprites);
                sel.Apu = CoreIdMigration.Migrate(console, CoreSlot.Apu, sel.Apu, ref migrated, ref invertSprites);
            }
            if (invertSprites) config.GbInvertSprites = true;
            if (migrated)
            {
                Console.WriteLine($"[Config] retired core picks moved to their successors (PPU {config.SelectedPpuCore}{(invertSprites ? ", sprite inversion on" : "")})");
                Helpers.ConfigHelper.Save(config);
            }
            GbLook.Palette = GbLook.FromKey(config.GbLook);
            GbLook.InvertBackground = config.GbInvertBackground;
            GbLook.InvertSprites = config.GbInvertSprites;
            RefreshGbLookMenu();
        }

        /// <summary>Change the Game Boy look (null = unchanged). Green / grey / pocket and the inversions apply live; going to
        /// or from Game Boy Color changes the Game Boy model, so a game using a Game Boy picture restarts.</summary>
        private void SetGbLook(GbPaletteChoice? look, bool? invertBackground, bool? invertSprites)
        {
            bool wasColor = GbLook.Color;
            Helpers.ConfigHelper.Update(config, c =>
            {
                if (look is GbPaletteChoice l) c.GbLook = GbLook.Key(l);
                if (invertBackground is bool b) c.GbInvertBackground = b;
                if (invertSprites is bool o) c.GbInvertSprites = o;
            });
            GbLook.Palette = GbLook.FromKey(config.GbLook);
            GbLook.InvertBackground = config.GbInvertBackground;
            GbLook.InvertSprites = config.GbInvertSprites;
            RefreshGbLookMenu();
            if (wasColor == GbLook.Color) return;
            if (session != null && sessionRom != null)
            {
                if (CoreCatalog.IsGameBoyPicture(session.Console, SelectedCore(session.Console, CoreSlot.Ppu)))
                    StartSession(session.Console, sessionRom, sessionRomName, currentRomPath);
            }
            else if (nes != null && CoreCatalog.IsGameBoyPicture(ConsoleKind.Nes, config.SelectedPpuCore))
                SetPpuCore(config.SelectedPpuCore, bypassProgression: true);
        }

        private void RefreshConsoleMenu()
        {
            if (consoleMenu == null) return;
            foreach (var item in consoleMenu.DropDownItems.OfType<ToolStripMenuItem>())
                if (item.Tag is ConsoleKind k) item.Checked = k == SelectedConsole;
        }

        // ------------------------------------------------------------------ the Sega machine settings
        private ToolStripMenuItem? segaMenu;

        /// <summary>What the user chose for the Sega machines (Auto reads each from the game), as the sessions take it.</summary>
        private NesEmulator.Sega.SegaOptions SegaOptionsFromConfig() =>
            new(NesEmulator.Sega.SegaOptions.RegionFromKey(config.SegaRegion), NesEmulator.Sega.SegaOptions.GenesisModelFromKey(config.GenesisModel), NesEmulator.Sega.SegaOptions.MasterSystemModelFromKey(config.MasterSystemModel));

        /// <summary>Console > Sega machine: the video standard, the Genesis board and the Master System board. Each is "Automatic" until chosen; a change restarts a running game of the console it concerns (the boards' states include them).</summary>
        private ToolStripMenuItem BuildSegaMenu()
        {
            segaMenu = new ToolStripMenuItem("Sega machine");
            void Group(string heading, (string Key, string Label)[] choices, Func<string> current, Action<string> set)
            {
                if (segaMenu.DropDownItems.Count > 0) segaMenu.DropDownItems.Add(new ToolStripSeparator());
                segaMenu.DropDownItems.Add(new ToolStripMenuItem(heading) { Enabled = false, Font = new Font(segaMenu.Font ?? SystemFonts.MenuFont!, FontStyle.Bold) });
                foreach (var (key, label) in choices)
                {
                    string k = key;
                    segaMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (s, e) => set(k)) { Tag = (heading, k), Padding = new Padding(8, 0, 0, 0) });
                }
                segaMenu.DropDownOpening += (s, e) => { foreach (var it in segaMenu.DropDownItems.OfType<ToolStripMenuItem>()) if (it.Tag is ValueTuple<string, string> t && t.Item1 == heading) it.Checked = t.Item2 == current(); };
            }
            Group("Video standard (Master System, Genesis; the Game Gear is always 60 Hz)", NesEmulator.Sega.SegaOptions.RegionChoices, () => NesEmulator.Sega.SegaOptions.Key(NesEmulator.Sega.SegaOptions.RegionFromKey(config.SegaRegion)), k => SetSegaOptions(k, null, null));
            Group("Genesis board", NesEmulator.Sega.SegaOptions.GenesisModelChoices, () => NesEmulator.Sega.SegaOptions.Key(NesEmulator.Sega.SegaOptions.GenesisModelFromKey(config.GenesisModel)), k => SetSegaOptions(null, k, null));
            Group("Master System board", NesEmulator.Sega.SegaOptions.MasterSystemModelChoices, () => NesEmulator.Sega.SegaOptions.Key(NesEmulator.Sega.SegaOptions.MasterSystemModelFromKey(config.MasterSystemModel)), k => SetSegaOptions(null, null, k));
            return segaMenu;
        }

        /// <summary>Change the Sega machine settings (null leaves one alone; an unknown key is refused and false is returned). A running game of a console the change concerns restarts on it, and the title says why.</summary>
        private bool SetSegaOptions(string? region, string? genesisModel, string? masterSystemModel)
        {
            bool Known((string Key, string Label)[] choices, string? v) => v == null || choices.Any(c => c.Key.Equals(v, StringComparison.OrdinalIgnoreCase));
            if (!Known(NesEmulator.Sega.SegaOptions.RegionChoices, region) || !Known(NesEmulator.Sega.SegaOptions.GenesisModelChoices, genesisModel) || !Known(NesEmulator.Sega.SegaOptions.MasterSystemModelChoices, masterSystemModel)) return false;
            var before = SegaOptionsFromConfig();
            Helpers.ConfigHelper.Update(config, c =>
            {
                if (region != null) c.SegaRegion = NesEmulator.Sega.SegaOptions.Key(NesEmulator.Sega.SegaOptions.RegionFromKey(region));
                if (genesisModel != null) c.GenesisModel = NesEmulator.Sega.SegaOptions.Key(NesEmulator.Sega.SegaOptions.GenesisModelFromKey(genesisModel));
                if (masterSystemModel != null) c.MasterSystemModel = NesEmulator.Sega.SegaOptions.Key(NesEmulator.Sega.SegaOptions.MasterSystemModelFromKey(masterSystemModel));
            });
            var after = SegaOptionsFromConfig();
            if (session != null && sessionRom != null && Consoles.IsSega(session.Console))
            {
                bool concerns = session.Console switch
                {
                    ConsoleKind.Genesis => before.Region != after.Region || before.GenesisModel != after.GenesisModel,
                    ConsoleKind.MasterSystem => before.Region != after.Region || before.MasterSystemModel != after.MasterSystemModel,
                    _ => before.Region != after.Region,
                };
                if (concerns)
                {
                    StartSession(session.Console, sessionRom, sessionRomName, currentRomPath);
                    consoleNote = "restarted on the new machine settings";
                    UpdateConsoleTitle();
                }
            }
            return true;
        }

        /// <summary>The machine a Sega session runs as (region, board), read from the board itself, for the status line and the HTTP API; null for any other session.</summary>
        private static (string region, string model)? SegaMachineOf(IConsoleSession? s)
        {
            var inner = (s as NesEmulator.Mix.SegaPictureSession)?.Inner ?? s;
            return inner switch
            {
                NesEmulator.Sega.SmsSession sms => (sms.Board.Region.ToString(), sms.Model.ToString()),
                NesEmulator.Sega.MdSession md => (md.Board.Region.ToString(), md.Board.Model.ToString()),
                _ => null,
            };
        }

        /// <summary>Switch the base console. A game the new console can run (Game Boy <-> Game Boy Color) restarts on it;
        /// any other game is closed and the console waits for a ROM.</summary>
        private void SwitchConsole(ConsoleKind console)
        {
            if (console == SelectedConsole && (session != null || nes != null && console == ConsoleKind.Nes)) { RefreshConsoleMenu(); return; }
            Helpers.ConfigHelper.Update(config, c => c.SelectedConsole = Consoles.Key(console));
            RefreshConsoleMenu();
            // the game itself decides where it can go (a SNES game on the WideMess goes back to the SNES; a WideMess game cannot)
            if (session != null && sessionRom != null && RomDetect.Runs(console, RomDetect.Detect(sessionRom, sessionRomName) ?? session.Console))
            {
                StartSession(console, sessionRom, sessionRomName, currentRomPath);
                return;
            }
            if (console == ConsoleKind.Nes)
            {
                if (session != null) { StopSession(); LoadEmbeddedRom(allowHomeWebModule: false); }
                UpdateCoresMenus();
                UpdateConsoleTitle();
                return;
            }
            // Another console, nothing to run on it yet: show the idle screen and wait for a ROM.
            StopSession();
            if (nes != null && !string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase)) LoadEmbeddedRom(allowHomeWebModule: false);
            UpdateCoresMenus();
            UpdateConsoleTitle();
        }

        private void UpdateConsoleTitle()
        {
            string game = session != null ? session.Title
                : nes != null && !string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(nes.RomName) : "";
            string console = Consoles.DisplayName(session?.Console ?? SelectedConsole);
            string note = session != null && consoleNote.Length > 0 ? $" ({consoleNote})" : "";
            Text = game.Length > 0 ? $"BrokenNes 2 - {console} - {game}{note}{WideMessTitleNote()}" : $"BrokenNes 2 - {console} - Emulator > Load ROM";
        }

        /// <summary>A short remark the title carries after an event the user should know about (a core swap that restarted the game, and why); cleared by the next swap or ROM.</summary>
        private string consoleNote = "";

        // ------------------------------------------------------------------ loading a SNES / Game Boy game
        /// <summary>Everything a ROM load goes through first: unpack a .zip, find the console, and either return the bytes
        /// for the NES path or start a session. Returns null when a session took the ROM.</summary>
        private byte[]? RouteRom(ref string romName, byte[] romData, string? romPath)
        {
            byte[] rom = RomDetect.Unwrap(romData, romName, out string inner);
            var kind = RomDetect.Detect(rom, inner);
            if (kind == null) throw new InvalidDataException(RomDetect.UnrecognisedMessage(inner, rom.Length));      // not a NES message from the NES loader: say what this file is not and what is played
            if (kind == ConsoleKind.Nes)
            {
                if (kind == ConsoleKind.Nes && SelectedConsole != ConsoleKind.Nes) Helpers.ConfigHelper.Update(config, c => c.SelectedConsole = "nes");
                StopSession();
                romName = inner;
                return rom;
            }
            // A Game Boy cartridge runs on the Game Boy or the Game Boy Color, whichever is selected; a Game Boy Color-only
            // cartridge goes to the Game Boy Color.
            var console = kind.Value;
            if (Consoles.IsGameBoy(console) && Consoles.IsGameBoy(SelectedConsole) && !(console == ConsoleKind.GameBoyColor && (rom[0x143] & 0xC0) == 0xC0))
                console = SelectedConsole;
            // a SNES game runs on the WideMess when the WideMess is the selected console (it is a superset of the SNES)
            if (console == ConsoleKind.Snes && SelectedConsole == ConsoleKind.WideMess && Consoles.IsAvailable(ConsoleKind.WideMess)) console = ConsoleKind.WideMess;
            StartSession(console, rom, inner, romPath ?? inner);
            if (romPath != null) config.AddRecentRom(romPath);
            return null;
        }

        private void StartSession(ConsoleKind console, byte[] rom, string romName, string? romPath)
        {
            StopEmulation();
            StopSession();
            lock (emulationLock)
            {
                SaveBatteryRamForNes(nes);
                nes = null;   // the NES idle screen / game stops; NES-only tools see "no NES" while another console runs
            }
            IConsoleSession s;
            try
            {
                s = ConsoleSessions.Create(console, rom, SelectedCore(console, CoreSlot.Cpu), SelectedCore(console, CoreSlot.Ppu),
                                           SelectedCore(console, CoreSlot.Apu), names => FindSnesFirmware(romPath, names), romName, SegaOptionsFromConfig());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not start {Consoles.DisplayName(console)}: {ex.Message}", "BrokenNes 2", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LoadEmbeddedRom(allowHomeWebModule: false);
                return;
            }
            session = s; sessionRom = rom; sessionRomName = romName; sessionFrames = 0; consoleNote = "";
            currentRomPath = romPath ?? romName;
            Helpers.ConfigHelper.Update(config, c => c.SelectedConsole = Consoles.Key(console));
            FarmAttach(s, rom, romName);        // record the game from its power-on (before the save RAM is loaded, so the farm can note it)
            LoadSessionSave();
            ApplySavedWideMessEnhancements();   // WideMess milestone 6: the enhancements kept for this SNES game (MainForm.WideMess.cs)
            RefreshConsoleMenu();
            UpdateCoresMenus();
            UpdateConsoleTitle();
            Console.WriteLine($"[Console] {s.Description}");
            RefreshRecentRomsMenu();
            StartEmulation();
        }

        private void StopSession()
        {
            if (session == null) return;
            StopEmulation();
            SaveSessionBattery(force: true);
            FarmDetach();
            session.Dispose();
            session = null; sessionRom = null; sessionLastSave = null;
        }

        private void RefreshRecentRomsMenu()
        {
            var fileMenu = MainMenuStrip?.Items.OfType<ToolStripMenuItem>().FirstOrDefault(m => m.Text == "&Emulator");
            var recentMenu = fileMenu?.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(m => m.Text.Contains("Recent"));
            if (recentMenu != null) UpdateRecentRomsMenu(recentMenu);
        }

        /// <summary>SNES coprocessor firmware (optional - DSP-1 has a homemade stand-in): beside the ROM, its firmware\
        /// folder, %APPDATA%\BrokenNes\Firmware, the app folder's firmware\.</summary>
        private static (string name, byte[] data)? FindSnesFirmware(string? romPath, string[] names)
        {
            var dirs = new List<string>();
            string? env = Environment.GetEnvironmentVariable("BROKENNES_SNES_FIRMWARE");
            if (!string.IsNullOrEmpty(env)) dirs.Add(env);
            if (romPath != null && Path.GetDirectoryName(Path.GetFullPath(romPath)) is { } romDir) { dirs.Add(romDir); dirs.Add(Path.Combine(romDir, "firmware")); }
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes", "Firmware"));
            dirs.Add(Path.Combine(AppContext.BaseDirectory, "firmware"));
            foreach (var d in dirs) foreach (var n in names)
                {
                    string p = Path.Combine(d, n);
                    try { if (File.Exists(p)) return (p, File.ReadAllBytes(p)); } catch { }
                }
            return null;
        }

        // ------------------------------------------------------------------ battery saves
        // SNES: %APPDATA%\BrokenNes\SnesSaves\sfc<sha1>.srm (a save left in BatterySaves\ by an older player is only read).
        // Game Boy: %APPDATA%\BrokenNes\GbSaves\<sha1>.sav (with the RTC trailer; BGB/VBA-compatible).
        private static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes");

        // Sega: SmsSaves\<sha1>.sav, GgSaves\<sha1>.sav (cartridge RAM / EEPROM), MdSaves\<sha1>.srm (SRAM / EEPROM, kept in the Genesis cartridge's own layout).
        private string? SessionSavePath(IConsoleSession s) => s.Console switch
        {
            ConsoleKind.Snes => Path.Combine(AppDataDir, "SnesSaves", s.GameId + ".srm"),
            // a SNES game on the WideMess keeps its SNES save (same id, same file); a WideMess game has its own folder
            ConsoleKind.WideMess => Path.Combine(AppDataDir, s.GameId.StartsWith("sfc", StringComparison.Ordinal) ? "SnesSaves" : "WideMessSaves", s.GameId + ".srm"),
            ConsoleKind.MasterSystem => Path.Combine(AppDataDir, "SmsSaves", s.GameId + ".sav"),
            ConsoleKind.GameGear => Path.Combine(AppDataDir, "GgSaves", s.GameId + ".sav"),
            ConsoleKind.Genesis => Path.Combine(AppDataDir, "MdSaves", s.GameId + ".srm"),
            _ => Path.Combine(AppDataDir, "GbSaves", s.GameId + ".sav"),
        };

        private void LoadSessionSave()
        {
            var s = session;
            // a Genesis cartridge with no SRAM header reports a battery only after its game has written to it, and a Master System / Game Gear game whose RAM mapping the ROM scan missed still gets its
            // save back: the file is looked for whatever the scan said, so a save made on an earlier run is loaded whenever the file exists
            if (s == null || (!s.HasBattery && s.Console is not (ConsoleKind.MasterSystem or ConsoleKind.GameGear) && !(s.Console == ConsoleKind.Genesis && File.Exists(SessionSavePath(s) ?? "")))) return;
            try
            {
                string path = SessionSavePath(s)!;
                string? from = File.Exists(path) ? path : null;
                if (from == null && s.Console == ConsoleKind.Snes)
                {
                    string legacy = Path.Combine(AppDataDir, "BatterySaves", s.GameId + ".srm");
                    if (File.Exists(legacy)) from = legacy;
                }
                if (from != null) { var saved = File.ReadAllBytes(from); s.ImportSave(saved); farm.SetInitialBattery(s.ExportSave()); }   // (a replay from power-on has to start with the same save RAM)
            }
            catch (Exception ex) { Console.WriteLine($"[Console] save load failed: {ex.Message}"); }
            sessionLastSave = s.ExportSave();
        }

        private void SaveSessionBattery(bool force)
        {
            var s = session;
            if (s == null || !s.HasBattery) return;
            if (farm.SaveRamTouched) return;     // a corruption wrote the game's save RAM, or the machine is a replayed or loaded one: never over the player's save
            try
            {
                byte[] data;
                lock (emulationLock) data = s.ExportSave();
                if (!force && sessionLastSave != null && data.AsSpan().SequenceEqual(sessionLastSave)) return;
                string path = SessionSavePath(s)!;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, path, overwrite: true);
                sessionLastSave = data;
            }
            catch (Exception ex) { Console.WriteLine($"[Console] save write failed: {ex.Message}"); }
        }

        // ------------------------------------------------------------------ one frame (emulation thread)
        // The human player's pad (keyboard and gamepad, with the d-pad protection of the player's config): see InputManager.Pad.
        private static PadButtons ToPad(InputManager? m) => m == null ? PadButtons.None : m.Pad();

        /// <summary>
        /// The twelve-button pad as one console wants it. The Master System has a Pause button and no Start (the keyboard's Return, the pad's Start, mean Pause there), and the Game Gear has a Start
        /// and no Pause (Select would raise the Z80's non-maskable interrupt, which is what the Master System's Pause is, and pause games that never asked to be). Other consoles read the pad as it is.
        /// </summary>
        internal static PadButtons ForConsole(ConsoleKind console, PadButtons b) => console switch
        {
            ConsoleKind.MasterSystem => (b & PadButtons.Start) != 0 ? b | PadButtons.Select : b,
            ConsoleKind.GameGear => b & ~PadButtons.Select,
            _ => b,
        };

        private readonly bool[] observedPad1 = new bool[WebApi.ApiInputInjector.ButtonCount], observedPad2 = new bool[WebApi.ApiInputInjector.ButtonCount];

        /// <summary>A pad as the API's button list (A, B, Select, Start, Up, Down, Left, Right, X, Y, L, R, C, Z, Mode).</summary>
        private static void PadToButtons(PadButtons p, bool[] b)
        {
            b[0] = (p & PadButtons.A) != 0; b[1] = (p & PadButtons.B) != 0; b[2] = (p & PadButtons.Select) != 0; b[3] = (p & PadButtons.Start) != 0;
            b[4] = (p & PadButtons.Up) != 0; b[5] = (p & PadButtons.Down) != 0; b[6] = (p & PadButtons.Left) != 0; b[7] = (p & PadButtons.Right) != 0;
            b[8] = (p & PadButtons.X) != 0; b[9] = (p & PadButtons.Y) != 0; b[10] = (p & PadButtons.L) != 0; b[11] = (p & PadButtons.R) != 0;
            b[12] = (p & PadButtons.C) != 0; b[13] = (p & PadButtons.Z) != 0; b[14] = (p & PadButtons.Mode) != 0;
        }

        private static PadButtons FromNesOrder(bool[] b)
        {
            PadButtons p = 0;
            if (b[0]) p |= PadButtons.A; if (b[1]) p |= PadButtons.B; if (b[2]) p |= PadButtons.Select; if (b[3]) p |= PadButtons.Start;
            if (b[4]) p |= PadButtons.Up; if (b[5]) p |= PadButtons.Down; if (b[6]) p |= PadButtons.Left; if (b[7]) p |= PadButtons.Right;
            return p;
        }

        /// <summary>Runs one frame of the SNES / Game Boy session: input, the frame, sound, the picture.</summary>
        private void RunSessionFrame(IConsoleSession s)
        {
            inputManager?.Poll();
            inputManager2?.Poll();
            var p1 = ToPad(inputManager); var p2 = ToPad(inputManager2);
            // Buttons held by the Web API (automation) are added, as on the NES.
            bool[] a1 = new bool[8], a2 = new bool[8];
            WebApi.ApiInputInjector.Apply(a1, a2);
            p1 |= FromNesOrder(a1) | WebApi.ApiInputInjector.ExtraButtons(0); p2 |= FromNesOrder(a2) | WebApi.ApiInputInjector.ExtraButtons(1);
            p1 = ForConsole(s.Console, p1); p2 = ForConsole(s.Console, p2);
            // GET /api/input/state reports what the game was handed (real devices merged with the injection), for the consoles as it does for the NES
            PadToButtons(p1, observedPad1); PadToButtons(p2, observedPad2);
            WebApi.ApiInputInjector.PublishObserved(observedPad1, observedPad2);
            webModuleInputManager?.Poll();

            lock (emulationLock)
            {
                FarmBeforeFrame(p1, p2);
                s.SetPad(0, p1); s.SetPad(1, p2);
                s.RunFrame();
                FarmAfterFrame();
            }
            sessionFrames++;
            if (sessionFrames % 600 == 0) SaveSessionBattery(force: false);

            fpsFrameCount++;
            if (fpsStopwatch != null && fpsStopwatch.Elapsed.TotalSeconds >= 0.5)
            {
                currentFps = fpsFrameCount / fpsStopwatch.Elapsed.TotalSeconds;
                fpsFrameCount = 0;
                fpsStopwatch.Restart();
            }

            if (audioManager != null)
            {
                int n;
                while ((n = s.ReadSamples(sessionAudio)) > 0)
                {
                    var mono = sessionResampler.Convert(sessionAudio, n, s.SampleRate, config.SoundQuality);
                    if (mono.Length > 0) audioManager.QueueSamples(mono);
                }
            }

            // The size, the picture and the buffer are read together under the lock a core swap takes: a swap between reading the size (256 x 240, a NES chip drawing the game) and reading the picture
            // (160 x 144, the console's own chip again) read past the end of the picture, threw, and the loop below treats an exception as the end of the emulation - the game stopped for good
            // (found by driving the swaps through the HTTP API: about one swap back in three).
            int w, h, aw; bool resize = false;
            lock (emulationLock)
            {
                w = s.FrameWidth; h = s.FrameHeight; aw = s.DisplayWidth;
                var bb = backBuffer;
                // the aspect width is part of the size: a Genesis game in its 256-wide mode (PAL, H32) has the NES's pixel size but not its shape
                if (bb == null || bb.Width != w || bb.Height != h || displayWidth != aw) resize = true;
                else
                {
                    var src = s.Frame; var dst = bb.Bits;
                    int n = Math.Min(w * h, Math.Min(src.Length, dst.Length));
                    for (int i = 0; i < n; i++) dst[i] = (int)src[i];
                }
            }
            if (resize)
            {
                RequestDisplaySize(w, h, aw);
                return;   // this frame is dropped while the display is rebuilt at the new size
            }
            RequestRenderFrame();
        }

        // ------------------------------------------------------------------ display size
        private int pendingResize;

        /// <summary>Rebuild the picture buffers and the renderer for a new frame size (on the UI thread).</summary>
        private void RequestDisplaySize(int w, int h, int aspectWidth)
        {
            if (Interlocked.Exchange(ref pendingResize, 1) != 0) return;
            void Apply()
            {
                try { SetDisplaySize(w, h, aspectWidth); }
                finally { Interlocked.Exchange(ref pendingResize, 0); }
            }
            if (InvokeRequired) BeginInvoke((Action)Apply); else Apply();
        }

        private void SetDisplaySize(int w, int h, int aspectWidth)
        {
            lock (emulationLock)
            {
                if (backBuffer == null || backBuffer.Width != w || backBuffer.Height != h)
                {
                    backBuffer?.Dispose(); frameBuffer?.Dispose();
                    backBuffer = new DirectBitmap(w, h);
                    frameBuffer = new DirectBitmap(w, h);
                }
            }
            displayWidth = aspectWidth; displayHeight = h;
            if (useDirectX && dxRenderer != null)
            {
                dxRenderer.Initialize(w, h);
                dxRenderer.AspectWidth = aspectWidth == w ? 0 : aspectWidth;
                if (!string.IsNullOrEmpty(config.CurrentShader)) NesShaderControl.SwitchShader(config.CurrentShader);
            }
        }

        /// <summary>Back to the NES picture size when the NES runs again.</summary>
        private void EnsureNesDisplaySize()
        {
            if (backBuffer != null && backBuffer.Width == NES_WIDTH && backBuffer.Height == NES_HEIGHT && displayWidth == NES_WIDTH) return;
            SetDisplaySize(NES_WIDTH, NES_HEIGHT, NES_WIDTH);
        }

        // ------------------------------------------------------------------ core menus, per console
        /// <summary>One core menu for the selected console, in the catalog's three sections, each under its heading: the console's own best cores
        /// (stock, enhanced), the other consoles' best cores (stock, enhanced), then the rest (the NES gimmick cores and variants).</summary>
        private void BuildCoreMenu(ToolStripMenuItem menu, CoreSlot slot, GameSave progressionSave)
        {
            menu.DropDownItems.Clear();
            var console = session?.Console ?? SelectedConsole;
            string current = console == ConsoleKind.Nes
                ? (slot switch { CoreSlot.Cpu => config.SelectedCpuCore, CoreSlot.Ppu => config.SelectedPpuCore, _ => config.SelectedApuCore })
                : SelectedCore(console, slot);
            foreach (var group in CoreCatalog.Options(console, slot).GroupBy(o => o.Section))
            {
                if (menu.DropDownItems.Count > 0) menu.DropDownItems.Add(new ToolStripSeparator());
                string heading = CoreCatalog.SectionTitle(console, group.Key);
                menu.DropDownItems.Add(new ToolStripMenuItem(heading) { Enabled = false, Font = new Font(menu.Font ?? SystemFonts.MenuFont!, FontStyle.Bold) });
                foreach (var opt in group)
                {
                    string id = opt.Id;
                    bool unlocked = console != ConsoleKind.Nes || slot switch
                    {
                        CoreSlot.Cpu => IsCpuCoreUnlocked(id, progressionSave),
                        CoreSlot.Ppu => IsPpuCoreUnlocked(id, progressionSave),
                        _ => IsApuCoreUnlocked(id, progressionSave),
                    };
                    if (!unlocked && !config.ShowLockedItems) continue;
                    var item = new ToolStripMenuItem(unlocked ? opt.Label : $"{opt.Label} [Locked]", null, (s, e) =>
                    {
                        if (console != ConsoleKind.Nes) { SetConsoleCore(console, slot, id); return; }
                        switch (slot)
                        {
                            case CoreSlot.Cpu: SetCpuCore(id); break;
                            case CoreSlot.Ppu: SetPpuCore(id); break;
                            default: SetApuCore(id); break;
                        }
                    })
                    {
                        Enabled = unlocked,
                        Checked = id.Equals(current, StringComparison.OrdinalIgnoreCase),
                        Padding = new Padding(8, 0, 0, 0),
                    };
                    if (console == ConsoleKind.Nes)
                    {
                        string card = slot switch { CoreSlot.Cpu => "cpu", CoreSlot.Ppu => "ppu", _ => "apu" };
                        item.MouseEnter += (s, e) => RequestOverlayDisplayCard(card, id);
                    }
                    else item.ToolTipText = CoreCatalog.Tip(console, slot, opt);
                    menu.DropDownItems.Add(item);
                }
            }
            menu.DropDownOpening -= BeginOverlayPreviewForMenu;
            menu.DropDownOpening += BeginOverlayPreviewForMenu;
            menu.DropDownClosed -= EndOverlayPreviewForMenu;
            menu.DropDownClosed += EndOverlayPreviewForMenu;
        }
    }
}
