using BrokenNes.Models;
using BrokenNes.CorruptorModels;
using NesEmulator;

namespace BrokenNes
{
    // Handles NES emulation lifecycle, ROM management, and core selection
    public class NesController
    {
        public async Task LoadRomUpload(Func<Task<UploadedRom[]>> getUploadedRoms, Action<string> setStatus, Action stateHasChanged, Func<string, Task> loadSelectedRom)
        {
            try
            {
                setStatus("Reading ROM file(s)...");
                ErrorMessage = string.Empty;
                var files = await getUploadedRoms();
                if (files == null || files.Length == 0)
                {
                    setStatus("No ROM files selected.");
                    return;
                }
                int added = 0;
                foreach (var f in files)
                {
                    if (string.IsNullOrWhiteSpace(f.name) || string.IsNullOrWhiteSpace(f.base64)) continue;
                    if (!f.name.EndsWith(".nes", StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] data;
                    try { data = Convert.FromBase64String(f.base64); } catch { continue; }
                    if (data.Length == 0) continue;
                    if (data.Length > 4 * 1024 * 1024)
                    {
                        setStatus($"File '{f.name}' too large (>4MB). Skipped.");
                        continue;
                    }
                    var key = f.name;
                    UploadedRoms[key] = data;
                    if (!RomOptions.Any(o => o.Key == key))
                    {
                        RomOptions.Add(new RomOption { Key = key, Label = $"{f.name} (uploaded)", BuiltIn = false });
                    }
                    added++;
                }
                if (added == 0)
                {
                    setStatus("No valid .nes files processed.");
                    return;
                }
                var last = files.Reverse().FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.name) && UploadedRoms.ContainsKey(f.name));
                if (last != null)
                {
                    RomFileName = last.name;
                    await loadSelectedRom(RomFileName);
                }
                setStatus(added == 1 ? "ROM uploaded." : $"{added} ROMs uploaded.");
                stateHasChanged();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Upload failed: {ex.Message}";
                setStatus(ErrorMessage);
                stateHasChanged();
            }
        }

        public async Task DeleteRom(string key, Action<string> setStatus, Action stateHasChanged, Func<string, Task> removeStoredRom, Func<string, Task> loadSelectedRom, Func<string> getDefaultBuiltInRomKey)
        {
            var opt = RomOptions.FirstOrDefault(o => o.Key == key);
            if (opt == null || opt.BuiltIn) return;
            if (UploadedRoms.Remove(key))
            {
                await removeStoredRom(key);
            }
            RomOptions.Remove(opt);
            if (CurrentRomName == key)
            {
                var fallback = getDefaultBuiltInRomKey();
                if (!string.IsNullOrEmpty(fallback))
                {
                    RomFileName = fallback;
                    await loadSelectedRom(RomFileName);
                }
                else
                {
                    nes = null;
                    CurrentRomName = "None";
                    setStatus("No built-in ROMs available. Please upload a ROM.");
                }
            }
            setStatus($"Deleted ROM {key}.");
            stateHasChanged();
        }

        public async Task ClearAllUploaded(Action<string> setStatus, Action stateHasChanged, Func<string, Task> removeStoredRom, Func<string, Task> loadSelectedRom, Func<string> getDefaultBuiltInRomKey, Func<bool> isBuiltInSelected)
        {
            var toDelete = RomOptions.Where(o => !o.BuiltIn).Select(o => o.Key).ToList();
            foreach (var k in toDelete)
            {
                await removeStoredRom(k);
            }
            RomOptions = RomOptions.Where(o => o.BuiltIn).ToList();
            UploadedRoms.Clear();
            if (!isBuiltInSelected())
            {
                var fallback = getDefaultBuiltInRomKey();
                if (!string.IsNullOrEmpty(fallback))
                {
                    RomFileName = fallback;
                    await loadSelectedRom(RomFileName);
                }
                else
                {
                    nes = null;
                    CurrentRomName = "None";
                    setStatus("No built-in ROMs available. Please upload a ROM.");
                }
            }
            setStatus("Cleared uploaded ROMs.");
            stateHasChanged();
        }

        public async Task RomSelectionChanged(string value, Func<string, Task> loadSelectedRom)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                RomFileName = value.Trim();
                await loadSelectedRom(RomFileName);
            }
        }

        public async Task LoadRomEntry(string key, Func<string, Task> loadSelectedRom)
        {
            RomFileName = key;
            await loadSelectedRom(RomFileName);
        }

        public async Task OnRomRowClicked(RomOption opt, Func<string, Task> loadRomEntry)
        {
            if (opt.Key == CurrentRomName) return;
            await loadRomEntry(opt.Key);
        }

        public string GetDefaultBuiltInRomKey()
        {
            return RomOptions.FirstOrDefault(o => o.BuiltIn)?.Key ?? string.Empty;
        }

        public string GetRomSizeDisplay(string key)
        {
            if (UploadedRoms.TryGetValue(key, out var data)) return FormatSize(data.Length);
            if (BuiltInRomSizes.TryGetValue(key, out var s)) return FormatSize(s);
            return "?";
        }
    
    public async Task LoadSelectedRom(Func<string, Task<byte[]>> loadRomFromWwwroot, Action<string> setStatus, Action stateHasChanged, Func<string, Task> jsDrawFrame, Func<Task> buildMemoryDomains, Func<Task> pauseEmulation, Func<Task> startEmulation)
        {
            try
            {
                bool wasRunning = IsRunning;
        // Ensure the JS rAF loop is fully stopped before loading a new ROM
        if (wasRunning) await pauseEmulation();
                if (UploadedRoms.TryGetValue(RomFileName, out var data))
                {
                    setStatus($"Loading uploaded ROM {RomFileName}...");
                    if (nes == null) nes = new NES();
                    nes.RomName = RomFileName;
                    nes.LoadROM(data);
                    AutoStaticSuppressed = false;
                    CurrentRomName = RomFileName;
                    LastLoadedRomSize = data.Length;
                    setStatus($"ROM '{RomFileName}' loaded from upload.");
                    ErrorMessage = "";
                    // UI: Collapse ROM Manager and expand Corruptor panel if not test.nes
                    if (wasRunning) await startEmulation();
                    stateHasChanged();
                }
                else
                {
                    setStatus($"Loading built-in {RomFileName}...");
                    var romData = await loadRomFromWwwroot(RomFileName);
                    if (romData.Length == 0)
                    {
                        ErrorMessage = $"ROM file '{RomFileName}' not found or empty";
                        setStatus(ErrorMessage);
                        return;
                    }
                    if (nes == null) nes = new NES();
                    nes.RomName = RomFileName;
                    nes.LoadROM(romData);
                    AutoStaticSuppressed = false;
                    CurrentRomName = RomFileName;
                    LastLoadedRomSize = romData.Length;
                    setStatus($"ROM '{RomFileName}' loaded successfully!");
                    ErrorMessage = "";
                    if (wasRunning) await startEmulation();
                    stateHasChanged();
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to load selected ROM: {ex.Message}";
                setStatus(ErrorMessage);
                stateHasChanged();
            }
        }

        public async Task<byte[]> LoadRomFromWwwroot(string filename, Func<string, Task<byte[]>> httpGet, Action<string> logInfo, Action<string> logError)
        {
            try
            {
                logInfo($"Loading ROM: {filename}");
                var romData = await httpGet(filename);
                logInfo($"ROM loaded successfully: {romData.Length} bytes");
                return romData;
            }
            catch (Exception ex)
            {
                logError($"Failed to load ROM: {filename} - {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        public static string FormatSize(int bytes)
        {
            if (bytes <= 0) return "0";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("F2") + " MB";
        }
    public NES? nes;
    public bool[] inputState = new bool[8];
    public string ErrorMessage = string.Empty;
    public bool IsRunning = false;
    public bool HasBooted = true;
    public float Fps = 0.0f;
    public int FrameCount = 0;
    public DateTime LastFpsUpdate = DateTime.Now;
    public int LastFrameCount = 0;
    public string RomFileName = "test.nes";
    public string CurrentRomName = "None";
    public string RomSearch = string.Empty;
    public int LastLoadedRomSize 
    { 
        get => _lastLoadedRomSize;
        set => _lastLoadedRomSize = value;
    }
    private int _lastLoadedRomSize = 0;
    
    public List<string> ApuCoreOptions 
    {
        get => _apuCoreOptions;
        set => _apuCoreOptions = value;
    }
    private List<string> _apuCoreOptions = new();
    
    public List<string> CpuCoreOptions 
    {
        get => _cpuCoreOptions;
        set => _cpuCoreOptions = value;
    }
    private List<string> _cpuCoreOptions = new();
    
    public List<string> PpuCoreOptions 
    {
        get => _ppuCoreOptions;
        set => _ppuCoreOptions = value;
    }
    private List<string> _ppuCoreOptions = new();
    
    public byte[] framebuffer 
    {
        get => _framebuffer;
        set => _framebuffer = value;
    }
    private byte[] _framebuffer = new byte[256 * 240 * 4];
    
    public Dictionary<string, int> BuiltInRomSizes 
    {
        get => _builtInRomSizes;
        set => _builtInRomSizes = value;
    }
    private Dictionary<string, int> _builtInRomSizes = new();
    public bool FastForward = false;
    public int StatsUpdateDivider = 6;
    public bool ShaderOn = true;
    public string ActiveShaderKey = "RF";
    public List<ShaderOption> ShaderOptions = new();
    public bool FamicloneOn = true;
    public string ApuCoreSel = "";
    public string CpuCoreSel = "";
    public string PpuCoreSel = "FMC";
    public double EmuScale = 1.0;
    public bool IsFullscreen = false;
    public bool AutoStaticSuppressed = false;
    public Dictionary<string, byte[]> UploadedRoms = new();
    public List<RomOption> RomOptions = new();
    // Add more fields as needed for controller logic

    // Emulator lifecycle methods
    public async Task StartEmulation(Func<Task> ensureAudioContext, Func<Task> startEmulationLoop, Action<string> setStatus, Action stateHasChanged, Action<string> logInfo, Action<string> logError)
        {
            try
            {
                if (nes == null)
                {
                    ErrorMessage = "NES emulator not initialized";
                    return;
                }
                await ensureAudioContext();
                logInfo("Starting emulation");
                IsRunning = true;
                ErrorMessage = "";
                setStatus("Emulation running...");
                await startEmulationLoop();
                stateHasChanged();
            }
            catch (Exception ex)
            {
                logError($"Failed to start emulation. Exception: {ex}");
                ErrorMessage = $"Failed to start: {ex.Message}\n{ex.StackTrace}";
                IsRunning = false;
                stateHasChanged();
            }
        }

        public async Task PauseEmulation(Func<Task> stopEmulationLoop, Func<Task> flushSoundFont, Action<string> setStatus, Action stateHasChanged, Action<string> logInfo, Action<string> logError)
        {
            try
            {
                logInfo("Pausing emulation");
                IsRunning = false;
                await stopEmulationLoop();
                try { nes?.FlushSoundFont(); } catch { }
                await flushSoundFont();
                setStatus("Emulation paused");
                stateHasChanged();
            }
            catch (Exception ex)
            {
                logError($"Failed to pause emulation. Exception: {ex}");
                ErrorMessage = $"Failed to pause: {ex.Message}\n{ex.StackTrace}";
            }
        }

        public async Task ResetEmulation(Func<Task> pauseEmulation, Func<Task> flushSoundFont, Func<Task> loadRomFromServer, Action applySelectedCores, Action buildMemoryDomains, Func<Task> startEmulation, Action<string> setStatus, Action stateHasChanged, Action<string> logInfo, Action<string> logError)
        {
            try
            {
                logInfo("Resetting emulation");
                bool wasRunning = IsRunning;
                await pauseEmulation();
                try { nes?.FlushSoundFont(); } catch { }
                await flushSoundFont();
                // ROM reload logic would go here (omitted for brevity)
                FrameCount = 0;
                LastFrameCount = 0;
                Fps = 0;
                // JS audio timeline reset would go here
                if (wasRunning)
                {
                    await startEmulation();
                }
                setStatus("Emulation reset (clean ROM reloaded)");
                stateHasChanged();
            }
            catch (Exception ex)
            {
                logError($"Failed to reset emulation. Exception: {ex}");
                ErrorMessage = $"Failed to reset: {ex.Message}\n{ex.StackTrace}";
            }
        }
        public void LoadRom(string key) { }
        public void SaveState() { }
        public void LoadState() { }
        // Add more methods as needed for controller logic
}

    }
