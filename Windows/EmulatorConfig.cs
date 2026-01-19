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
        public bool ShadersEnabled { get; set; } = false;
        
        /// <summary>
        /// Last selected shader
        /// </summary>
        [JsonPropertyName("currentShader")]
        public string? CurrentShader { get; set; }
        
        /// <summary>
        /// Shader strength multiplier
        /// </summary>
        [JsonPropertyName("shaderStrength")]
        public float ShaderStrength { get; set; } = 1.0f;
        
        /// <summary>
        /// Selected CPU core (defaults to FMC)
        /// </summary>
        [JsonPropertyName("selectedCpuCore")]
        public string SelectedCpuCore { get; set; } = "FMC";
        
        /// <summary>
        /// Selected PPU core (defaults to FMC)
        /// </summary>
        [JsonPropertyName("selectedPpuCore")]
        public string SelectedPpuCore { get; set; } = "FMC";
        
        /// <summary>
        /// Selected APU core (defaults to FMC)
        /// </summary>
        [JsonPropertyName("selectedApuCore")]
        public string SelectedApuCore { get; set; } = "FMC";
        
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
        /// Display FPS counter on screen
        /// </summary>
        [JsonPropertyName("showFps")]
        public bool ShowFps { get; set; } = false;
        
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
        public string CrashBehavior { get; set; } = "RedScreen";
        
        // Controller configuration
        /// <summary>
        /// Player 1 Controller key bindings
        /// </summary>
        [JsonPropertyName("p1KeyA")]
        public string P1KeyA { get; set; } = "Z";
        
        [JsonPropertyName("p1KeyB")]
        public string P1KeyB { get; set; } = "X";
        
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
                        // Validate that recent ROMs still exist
                        config.RecentRoms = config.RecentRoms
                            .Where(File.Exists)
                            .Take(config.MaxRecentRoms)
                            .ToList();
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading config: {ex.Message}");
            }
            
            return new EmulatorConfig();
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
}
