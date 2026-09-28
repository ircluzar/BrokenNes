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
                bool swapped;
                lock (emulationLock) swapped = session.TrySwapCore(slot, id);
                if (!swapped) { StartSession(console, sessionRom, sessionRomName, currentRomPath); return; }
                Diagnostics.ShutdownDiagnostics.Log($"Core hot-swap: {console} {slot} -> {id} ({session.Description})");
                UpdateCoresMenus();
                UpdateConsoleTitle();
            }
            else UpdateCoresMenus();
        }

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
                };
            };
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
            server.SelectConsole = key => Invoke(() => SwitchConsole(Consoles.FromKey(key)));
            server.GetGbLook = () => (config.GbLook, config.GbInvertBackground, config.GbInvertSprites);
            server.SetGbLook = (look, bg, obj) => Invoke(() => SetGbLook(look == null ? null : GbLook.FromKey(look), bg, obj));
            server.SelectConsoleCore = (slotName, id) =>
            {
                var slot = slotName.ToLowerInvariant() switch { "cpu" => CoreSlot.Cpu, "ppu" => CoreSlot.Ppu, _ => CoreSlot.Apu };
                var console = session?.Console ?? SelectedConsole;
                var opt = CoreCatalog.Options(console, slot).FirstOrDefault(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
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

        /// <summary>Apply the saved Game Boy look to the engine (at start-up, after the config loads). Also moves picks of the
        /// retired DMGXI / DMGSI picture chips to DMGX / DMGS with sprite inversion on.</summary>
        private void ApplyGbLook()
        {
            bool migrated = false;
            string Retire(string? id)
            {
                if (id == null) return id!;
                var up = id.ToUpperInvariant();
                if (up.EndsWith("DMGXI") || up.EndsWith("DMGSI")) { migrated = true; return id[..^1]; }
                return id;
            }
            config.SelectedPpuCore = Retire(config.SelectedPpuCore);
            foreach (var sel in config.ConsoleCores.Values) if (sel.Ppu != null) sel.Ppu = Retire(sel.Ppu);
            if (migrated) { config.GbInvertSprites = true; Helpers.ConfigHelper.Save(config); }
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
                if (session.Console is ConsoleKind.GameBoy or ConsoleKind.GameBoyColor || SelectedCore(session.Console, CoreSlot.Ppu).Contains("DMG", StringComparison.OrdinalIgnoreCase))
                    StartSession(session.Console, sessionRom, sessionRomName, currentRomPath);
            }
            else if (nes != null && config.SelectedPpuCore.StartsWith("DMG", StringComparison.OrdinalIgnoreCase))
                SetPpuCore(config.SelectedPpuCore, bypassProgression: true);
        }

        private void RefreshConsoleMenu()
        {
            if (consoleMenu == null) return;
            foreach (var item in consoleMenu.DropDownItems.OfType<ToolStripMenuItem>())
                if (item.Tag is ConsoleKind k) item.Checked = k == SelectedConsole;
        }

        /// <summary>Switch the base console. A game the new console can run (Game Boy <-> Game Boy Color) restarts on it;
        /// any other game is closed and the console waits for a ROM.</summary>
        private void SwitchConsole(ConsoleKind console)
        {
            if (console == SelectedConsole && (session != null || nes != null && console == ConsoleKind.Nes)) { RefreshConsoleMenu(); return; }
            Helpers.ConfigHelper.Update(config, c => c.SelectedConsole = Consoles.Key(console));
            RefreshConsoleMenu();
            if (session != null && sessionRom != null && RomDetect.Runs(console, session.Console))
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
            Text = game.Length > 0 ? $"BrokenNes 2 - {console} - {game}" : $"BrokenNes 2 - {console} - Emulator > Load ROM";
        }

        // ------------------------------------------------------------------ loading a SNES / Game Boy game
        /// <summary>Everything a ROM load goes through first: unpack a .zip, find the console, and either return the bytes
        /// for the NES path or start a session. Returns null when a session took the ROM.</summary>
        private byte[]? RouteRom(ref string romName, byte[] romData, string? romPath)
        {
            byte[] rom = RomDetect.Unwrap(romData, romName, out string inner);
            var kind = RomDetect.Detect(rom, inner);
            if (kind == null || kind == ConsoleKind.Nes)
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
                                           SelectedCore(console, CoreSlot.Apu), names => FindSnesFirmware(romPath, names));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not start {Consoles.DisplayName(console)}: {ex.Message}", "BrokenNes 2", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LoadEmbeddedRom(allowHomeWebModule: false);
                return;
            }
            session = s; sessionRom = rom; sessionRomName = romName; sessionFrames = 0;
            currentRomPath = romPath ?? romName;
            Helpers.ConfigHelper.Update(config, c => c.SelectedConsole = Consoles.Key(console));
            LoadSessionSave();
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

        private string? SessionSavePath(IConsoleSession s) => s.Console == ConsoleKind.Snes
            ? Path.Combine(AppDataDir, "SnesSaves", s.GameId + ".srm")
            : Path.Combine(AppDataDir, "GbSaves", s.GameId + ".sav");

        private void LoadSessionSave()
        {
            var s = session;
            if (s == null || !s.HasBattery) return;
            try
            {
                string path = SessionSavePath(s)!;
                string? from = File.Exists(path) ? path : null;
                if (from == null && s.Console == ConsoleKind.Snes)
                {
                    string legacy = Path.Combine(AppDataDir, "BatterySaves", s.GameId + ".srm");
                    if (File.Exists(legacy)) from = legacy;
                }
                if (from != null) s.ImportSave(File.ReadAllBytes(from));
            }
            catch (Exception ex) { Console.WriteLine($"[Console] save load failed: {ex.Message}"); }
            sessionLastSave = s.ExportSave();
        }

        private void SaveSessionBattery(bool force)
        {
            var s = session;
            if (s == null || !s.HasBattery) return;
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
        private static PadButtons ToPad(InputManager? m)
        {
            if (m == null) return PadButtons.None;
            PadButtons b = 0;
            // InputManager indices: 0 A, 1 B, 2 Select, 3 Start, 4 Up, 5 Down, 6 Left, 7 Right, 8 X, 9 Y, 10 L, 11 R.
            if (m.GetButton(0)) b |= PadButtons.A; if (m.GetButton(1)) b |= PadButtons.B;
            if (m.GetButton(2)) b |= PadButtons.Select; if (m.GetButton(3)) b |= PadButtons.Start;
            if (m.GetButton(4)) b |= PadButtons.Up; if (m.GetButton(5)) b |= PadButtons.Down;
            if (m.GetButton(6)) b |= PadButtons.Left; if (m.GetButton(7)) b |= PadButtons.Right;
            if (m.GetButton(8)) b |= PadButtons.X; if (m.GetButton(9)) b |= PadButtons.Y;
            if (m.GetButton(10)) b |= PadButtons.L; if (m.GetButton(11)) b |= PadButtons.R;
            return b;
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
            webModuleInputManager?.Poll();

            lock (emulationLock)
            {
                s.SetPad(0, p1); s.SetPad(1, p2);
                s.RunFrame();
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

            int w = s.FrameWidth, h = s.FrameHeight;
            if (backBuffer == null || backBuffer.Width != w || backBuffer.Height != h)
            {
                RequestDisplaySize(w, h, s.DisplayWidth);
                return;   // this frame is dropped while the display is rebuilt at the new size
            }
            lock (emulationLock)
            {
                var src = s.Frame; var dst = backBuffer.Bits;
                for (int i = 0; i < w * h; i++) dst[i] = (int)src[i];
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
            if (backBuffer != null && backBuffer.Width == NES_WIDTH && backBuffer.Height == NES_HEIGHT) return;
            SetDisplaySize(NES_WIDTH, NES_HEIGHT, NES_WIDTH);
        }

        // ------------------------------------------------------------------ core menus, per console
        /// <summary>One core menu for the selected console: the console's own family first, then the other consoles'
        /// parts, each family under its own heading.</summary>
        private void BuildCoreMenu(ToolStripMenuItem menu, CoreSlot slot, GameSave progressionSave)
        {
            menu.DropDownItems.Clear();
            var console = session?.Console ?? SelectedConsole;
            string current = console == ConsoleKind.Nes
                ? (slot switch { CoreSlot.Cpu => config.SelectedCpuCore, CoreSlot.Ppu => config.SelectedPpuCore, _ => config.SelectedApuCore })
                : SelectedCore(console, slot);
            string own = Consoles.Family(console);
            foreach (var group in CoreCatalog.Options(console, slot).GroupBy(o => o.Family))
            {
                if (menu.DropDownItems.Count > 0) menu.DropDownItems.Add(new ToolStripSeparator());
                string heading = group.Key == own ? $"{group.Key} family ({Consoles.DisplayName(console)})" : $"From the {group.Key}";
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
