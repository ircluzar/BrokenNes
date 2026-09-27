using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Configuration model for the BrokenNes emulator
    /// </summary>
    public class EmulatorConfig
    {
        /// <summary>
        /// List of recently opened ROM file paths
        /// </summary>
        [JsonPropertyName("recentRoms")]
        public List<string> RecentRoms { get; set; } = new List<string>();
        
        /// <summary>
        /// Maximum number of recent ROMs to track
        /// </summary>
        [JsonPropertyName("maxRecentRoms")]
        public int MaxRecentRoms { get; set; } = 10;
        
        /// <summary>
        /// Whether to use DirectX rendering
        /// </summary>
        [JsonPropertyName("useDirectX")]
        public bool UseDirectX { get; set; } = true;
        
        /// <summary>
        /// Whether shaders are enabled
        /// </summary>
        [JsonPropertyName("shadersEnabled")]
        public bool ShadersEnabled { get; set; } = true;
        
        /// <summary>
        /// Last selected shader
        /// </summary>
        [JsonPropertyName("currentShader")]
        public string? CurrentShader { get; set; } = "PX";
        
        /// <summary>
        /// Shader strength multiplier
        /// </summary>
        [JsonPropertyName("shaderStrength")]
        public float ShaderStrength { get; set; } = 1.0f;
        
        /// <summary>
        /// Selected CPU core (defaults to FIX, the accuracy-certified family)
        /// </summary>
        [JsonPropertyName("selectedCpuCore")]
        public string SelectedCpuCore { get; set; } = "FIX";
        
        /// <summary>
        /// Selected PPU core (defaults to FIX, the accuracy-certified family)
        /// </summary>
        [JsonPropertyName("selectedPpuCore")]
        public string SelectedPpuCore { get; set; } = "FIX";
        
        /// <summary>
        /// Selected APU core (defaults to FIX, the accuracy-certified family)
        /// </summary>
        [JsonPropertyName("selectedApuCore")]
        public string SelectedApuCore { get; set; } = "FIX";
        
        // Image configuration
        /// <summary>
        /// Force pixel perfect rendering
        /// </summary>
        [JsonPropertyName("forcePixelPerfect")]
        public bool ForcePixelPerfect { get; set; } = false;
        
        /// <summary>
        /// Force native aspect ratio
        /// </summary>
        [JsonPropertyName("forceNativeAspectRatio")]
        public bool ForceNativeAspectRatio { get; set; } = false;
        
        /// <summary>
        /// Use nearest neighbor scaling
        /// </summary>
        [JsonPropertyName("scalingNearestNeighbor")]
        public bool ScalingNearestNeighbor { get; set; } = true;
        
        /// <summary>
        /// Window zoom level (1, 2, or 4)
        /// </summary>
        [JsonPropertyName("windowZoom")]
        public int WindowZoom { get; set; } = 2;
        
        // Sound configuration
        /// <summary>
        /// Enabled sound channels (bitflags: 0=Square1, 1=Square2, 2=Triangle, 3=Noise, 4=DMC)
        /// </summary>
        [JsonPropertyName("enabledChannels")]
        public int EnabledChannels { get; set; } = 0x1F; // All channels enabled by default
        
        /// <summary>
        /// Sound quality/sample rate (22050, 44100, 48000)
        /// </summary>
        [JsonPropertyName("soundQuality")]
        public int SoundQuality { get; set; } = 44100;
        
        /// <summary>
        /// Sound buffer size in samples (affects latency)
        /// </summary>
        [JsonPropertyName("soundBuffer")]
        public int SoundBuffer { get; set; } = 2048;
        
        /// <summary>
        /// Run emulation as fast as possible (no speed limit)
        /// </summary>
        [JsonPropertyName("noSpeedLimit")]
        public bool NoSpeedLimit { get; set; } = false;
        
        /// <summary>
        /// Emulate NTSC's true frame length (89341.5 PPU dots = 29780.5 CPU cycles) instead of the
        /// exact-60fps simplification (29829.55). The ~49-cycle-per-frame surplus is invisible in
        /// ordinary play but it hands a frame-budget-critical game cycles real hardware would not,
        /// which can mask the very overruns such a game is written to avoid. Off by default because
        /// AccuracyCoin, the benchmarks and the existing savestate tooling are all tuned against the
        /// exact-60 baseline - see Bus.SpeedConfig.NtscAccurateFrameRate for the full reasoning.
        /// </summary>
        [JsonPropertyName("ntscAccurateFrameRate")]
        public bool NtscAccurateFrameRate { get; set; } = false;

        /// <summary>
        /// Display FPS counter on screen
        /// </summary>
        [JsonPropertyName("showFps")]
        public bool ShowFps { get; set; } = false;
        
        /// <summary>
        /// Enable V-Sync (DirectX only)
        /// </summary>
        [JsonPropertyName("enableVSync")]
        public bool EnableVSync { get; set; } = false;
        
        /// <summary>
        /// Enable performance profiling/telemetry
        /// </summary>
        [JsonPropertyName("profilingEnabled")]
        public bool ProfilingEnabled { get; set; } = false;
        
        /// <summary>
        /// Auto-scramble cores for testing (randomly swaps one core every 420ms)
        /// </summary>
        [JsonPropertyName("autoScrambleCores")]
        public bool AutoScrambleCores { get; set; } = false;
        
        /// <summary>
        /// Crash behavior when the emulator encounters an error (RedScreen, IgnoreErrors, ImagineFix)
        /// </summary>
        [JsonPropertyName("crashBehavior")]
        public string CrashBehavior { get; set; } = "IgnoreErrors";
        
        /// <summary>
        /// Show the Windows console window
        /// </summary>
        [JsonPropertyName("showConsole")]
        public bool ShowConsole { get; set; } = false;
        
        /// <summary>
        /// Boot directly to emulator mode instead of loading the Home webmodule (the BrokenNes 1 campaign hub).
        /// BrokenNes 2: the emulator is the default; the Deck Builder campaign is reached from the Legacy menu.
        /// </summary>
        [JsonPropertyName("bootToEmulator")]
        public bool BootToEmulator { get; set; } = true;

        /// <summary>BrokenNes 2: the console the emulator runs ("nes", "snes", "gb", "gbc").</summary>
        [JsonPropertyName("selectedConsole")]
        public string SelectedConsole { get; set; } = "nes";

        /// <summary>BrokenNes 2: the CPU/PPU/APU picked for each non-NES console (the NES keeps SelectedCpuCore etc.).</summary>
        [JsonPropertyName("consoleCores")]
        public Dictionary<string, ConsoleCoreSelection> ConsoleCores { get; set; } = new();

        /// <summary>BrokenNes 1 behaviour: cores, shaders and tools are locked until the Deck Builder campaign unlocks them.
        /// Off in BrokenNes 2, where the emulator comes first and everything is available.</summary>
        [JsonPropertyName("legacyProgressionLocks")]
        public bool LegacyProgressionLocks { get; set; } = false;

        /// <summary>Set once the BrokenNes 1 -> 2 settings migration has run (emulator-first boot).</summary>
        [JsonPropertyName("brokenNes2Migrated")]
        public bool BrokenNes2Migrated { get; set; } = true;
        
        /// <summary>
        /// Show the Webmodules menu in the menu bar
        /// </summary>
        [JsonPropertyName("showWebmodulesMenu")]
        public bool ShowWebmodulesMenu { get; set; } = false;

        /// <summary>
        /// Show locked menu entries instead of hiding them
        /// </summary>
        [JsonPropertyName("showLockedItems")]
        public bool ShowLockedItems { get; set; } = false;

        /// <summary>
        /// Continue accepting keyboard input when emulator window is not focused
        /// </summary>
        [JsonPropertyName("acceptBackgroundInput")]
        public bool AcceptBackgroundInput { get; set; } = false;

        /// <summary>
        /// Allow webmodules to trigger quick savestate shortcuts for debugging
        /// </summary>
        [JsonPropertyName("enableWebmoduleSavestateDebugShortcuts")]
        public bool EnableWebmoduleSavestateDebugShortcuts { get; set; } = false;
        
        // Controller configuration - NEW SYSTEM
        /// <summary>
        /// Player controller configurations (supports multiple players with keyboard and XInput)
        /// </summary>
        [JsonPropertyName("playerControllers")]
        public List<PlayerControllerConfig> PlayerControllers { get; set; } = new List<PlayerControllerConfig>
        {
            PlayerControllerConfig.CreateDefaultPlayer1()
        };

        // Controller configuration - LEGACY (kept for backwards compatibility)
        /// <summary>
        /// Player 1 Controller key bindings (LEGACY - use PlayerControllers instead)
        /// </summary>
        [JsonPropertyName("p1KeyA")]
        public string P1KeyA { get; set; } = "X";
        
        [JsonPropertyName("p1KeyB")]
        public string P1KeyB { get; set; } = "Z";
        
        [JsonPropertyName("p1KeySelect")]
        public string P1KeySelect { get; set; } = "Space";
        
        [JsonPropertyName("p1KeyStart")]
        public string P1KeyStart { get; set; } = "Return";
        
        [JsonPropertyName("p1KeyUp")]
        public string P1KeyUp { get; set; } = "Up";
        
        [JsonPropertyName("p1KeyDown")]
        public string P1KeyDown { get; set; } = "Down";
        
        [JsonPropertyName("p1KeyLeft")]
        public string P1KeyLeft { get; set; } = "Left";
        
        [JsonPropertyName("p1KeyRight")]
        public string P1KeyRight { get; set; } = "Right";

        /// <summary>
        /// Get or create player controller config for a specific player number
        /// </summary>
        public PlayerControllerConfig GetPlayerController(int playerNumber)
        {
            var controller = PlayerControllers.FirstOrDefault(p => p.PlayerNumber == playerNumber);
            if (controller == null)
            {
                controller = playerNumber == 1 
                    ? PlayerControllerConfig.CreateDefaultPlayer1() 
                    : PlayerControllerConfig.CreateDefaultGamepad(playerNumber);
                controller.PlayerNumber = playerNumber;
                PlayerControllers.Add(controller);
            }
            return controller;
        }

        /// <summary>
        /// Migrate legacy keyboard bindings to new system
        /// </summary>
        public void MigrateLegacyBindings()
        {
            if (PlayerControllers.Count == 0 || PlayerControllers[0].PlayerNumber != 1)
            {
                var p1 = GetPlayerController(1);
                p1.A = ButtonBinding.FromKey(P1KeyA);
                p1.B = ButtonBinding.FromKey(P1KeyB);
                p1.Select = ButtonBinding.FromKey(P1KeySelect);
                p1.Start = ButtonBinding.FromKey(P1KeyStart);
                p1.Up = ButtonBinding.FromKey(P1KeyUp);
                p1.Down = ButtonBinding.FromKey(P1KeyDown);
                p1.Left = ButtonBinding.FromKey(P1KeyLeft);
                p1.Right = ButtonBinding.FromKey(P1KeyRight);
            }
        }

        public bool NormalizePlayer1DefaultKeyboardBindings()
        {
            var changed = false;

            if (HasLegacyDefaultKeyboardBindings())
            {
                P1KeyA = "X";
                P1KeyB = "Z";
                changed = true;
            }

            var player1 = PlayerControllers.FirstOrDefault(p => p.PlayerNumber == 1);
            if (player1 != null && HasOldDefaultPlayer1Bindings(player1))
            {
                player1.A = ButtonBinding.FromKey("X");
                player1.B = ButtonBinding.FromKey("Z");
                changed = true;
            }

            return changed;
        }

        private bool HasLegacyDefaultKeyboardBindings()
        {
            return string.Equals(P1KeyA, "Z", StringComparison.OrdinalIgnoreCase)
                && string.Equals(P1KeyB, "X", StringComparison.OrdinalIgnoreCase)
                && string.Equals(P1KeySelect, "Space", StringComparison.OrdinalIgnoreCase)
                && string.Equals(P1KeyStart, "Return", StringComparison.OrdinalIgnoreCase)
                && string.Equals(P1KeyUp, "Up", StringComparison.OrdinalIgnoreCase)
                && string.Equals(P1KeyDown, "Down", StringComparison.OrdinalIgnoreCase)
                && string.Equals(P1KeyLeft, "Left", StringComparison.OrdinalIgnoreCase)
                && string.Equals(P1KeyRight, "Right", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasOldDefaultPlayer1Bindings(PlayerControllerConfig player)
        {
            return player.DeviceType == InputDeviceType.Keyboard
                && HasKeyboardBinding(player.A, "Z")
                && HasKeyboardBinding(player.B, "X")
                && HasKeyboardBinding(player.Select, "Space")
                && HasKeyboardBinding(player.Start, "Return")
                && HasKeyboardBinding(player.Up, "Up")
                && HasKeyboardBinding(player.Down, "Down")
                && HasKeyboardBinding(player.Left, "Left")
                && HasKeyboardBinding(player.Right, "Right")
                && HasKeyboardBinding(player.X, "A")
                && HasKeyboardBinding(player.Y, "S");
        }

        private static bool HasKeyboardBinding(ButtonBinding? binding, string expectedKey)
        {
            return binding?.GamepadButton == null
                && string.Equals(binding?.Key, expectedKey, StringComparison.OrdinalIgnoreCase);
        }
        
        // Background configuration
        /// <summary>
        /// Selected background renderer (Wave, Bubble, Gradient, None)
        /// </summary>
        [JsonPropertyName("selectedBackground")]
        public string SelectedBackground { get; set; } = "Gradient (Default)";
        
        /// <summary>
        /// Render scanlines on the background for a more authentic look
        /// </summary>
        [JsonPropertyName("renderScanlines")]
        public bool RenderScanlines { get; set; } = true;
        
        /// <summary>
        /// Render a shadow/glow behind the emulator viewport
        /// </summary>
        [JsonPropertyName("renderViewportShadow")]
        public bool RenderViewportShadow { get; set; } = true;
        
        /// <summary>
        /// Selected null provider (TV Static, Rainbow Plasma, etc) - used when test ROM is loaded
        /// </summary>
        [JsonPropertyName("selectedNullProvider")]
        public string SelectedNullProvider { get; set; } = "Static";
        
        /// <summary>
        /// Hide the menu bar when in full screen mode
        /// </summary>
        [JsonPropertyName("hideMenuBarInFullscreen")]
        public bool HideMenuBarInFullscreen { get; set; } = true;

        /// <summary>
        /// Which core-default migration this config has been through. Absent (null) in configs written
        /// before FIX became the default; those still on the old FMC/FMC/FMC default move to FIX once.
        /// </summary>
        [JsonPropertyName("coreDefaultsVersion")]
        public int? CoreDefaultsVersion { get; set; }
        public const int CurrentCoreDefaultsVersion = 1;

        /// <summary>
        /// One-time move off the old FMC default: FIX is the family the game certification campaign
        /// fixes and verifies against Mesen (FMC still hangs Zelda II's pause menu, for one). Only an
        /// untouched FMC/FMC/FMC selection is moved - any other combination was a deliberate choice.
        /// </summary>
        private bool MigrateCoreDefaults()
        {
            if (CoreDefaultsVersion >= CurrentCoreDefaultsVersion) return false;
            if (SelectedCpuCore == "FMC" && SelectedPpuCore == "FMC" && SelectedApuCore == "FMC")
            {
                SelectedCpuCore = SelectedPpuCore = SelectedApuCore = "FIX";
            }
            CoreDefaultsVersion = CurrentCoreDefaultsVersion;
            return true;
        }
        
        private static readonly string ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
            "BrokenNes");
        private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, "config.json");
        
        /// <summary>
        /// Load configuration from config.json
        /// </summary>
        public static EmulatorConfig Load()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string json = File.ReadAllText(ConfigFilePath);
                    var config = JsonSerializer.Deserialize<EmulatorConfig>(json);
                    if (config != null)
                    {
                        // Migrate legacy bindings if needed
                        config.MigrateLegacyBindings();
                        var bindingsChanged = config.NormalizePlayer1DefaultKeyboardBindings();
                        var coreDefaultsChanged = config.MigrateCoreDefaults();
                        // BrokenNes 1 -> 2: the emulator becomes the start screen (the campaign moves to the Legacy menu).
                        // A config written by BrokenNes 1 has no brokenNes2Migrated field, which reads as false here.
                        if (!json.Contains("\"brokenNes2Migrated\""))
                        {
                            config.BootToEmulator = true;
                            config.BrokenNes2Migrated = true;
                            coreDefaultsChanged = true;
                        }
                        var shaderConfigChanged = false;

                        // Shaders are always-on; keep persisted configs aligned.
                        if (!config.ShadersEnabled)
                        {
                            config.ShadersEnabled = true;
                            shaderConfigChanged = true;
                        }

                        if (string.IsNullOrWhiteSpace(config.CurrentShader))
                        {
                            config.CurrentShader = "PX";
                            shaderConfigChanged = true;
                        }
                        
                        // Validate that recent ROMs still exist
                        config.RecentRoms = config.RecentRoms
                            .Where(File.Exists)
                            .Take(config.MaxRecentRoms)
                            .ToList();

                        if (bindingsChanged || shaderConfigChanged || coreDefaultsChanged)
                        {
                            config.Save();
                        }

                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading config: {ex.Message}");
            }
            
            return new EmulatorConfig { CoreDefaultsVersion = CurrentCoreDefaultsVersion };
        }
        
        /// <summary>
        /// Save configuration to config.json
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(ConfigFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving config: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Add a ROM to the recent list
        /// </summary>
        public void AddRecentRom(string romPath)
        {
            if (!File.Exists(romPath))
                return;
            
            // Remove if already in list
            RecentRoms.Remove(romPath);
            
            // Add to front of list
            RecentRoms.Insert(0, romPath);
            
            // Trim to max size
            if (RecentRoms.Count > MaxRecentRoms)
            {
                RecentRoms = RecentRoms.Take(MaxRecentRoms).ToList();
            }
            
            Save();
        }
        
        /// <summary>
        /// Clear all recent ROMs
        /// </summary>
        public void ClearRecentRoms()
        {
            RecentRoms.Clear();
            Save();
        }
    }

    /// <summary>BrokenNes 2: the cores picked for one console.</summary>
    public class ConsoleCoreSelection
    {
        [JsonPropertyName("cpu")] public string? Cpu { get; set; }
        [JsonPropertyName("ppu")] public string? Ppu { get; set; }
        [JsonPropertyName("apu")] public string? Apu { get; set; }
    }
}
