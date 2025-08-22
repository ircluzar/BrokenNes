using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Services;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using BrokenNes.Models;
using BrokenNes.CorruptorModels;
using NesEmulator;

namespace BrokenNes
{
    /// <summary>
    /// Standalone class intended to host logic migrated from Nes.razor @code block.
    /// This is an initial scaffold; large methods & fields should be pasted/re-homed here.
    /// </summary>
    public partial class Emulator : IDisposable
    {
        // --- Injected dependencies (mirrors Nes.razor @inject list) ---
    private readonly ILogger Logger;
        private readonly IJSRuntime JS;
        private readonly HttpClient Http;
        private readonly StatusService Status;
        private readonly NesEmulator.Shaders.IShaderProvider ShaderProvider;
        private readonly NavigationManager Nav;

        // --- Controller instances (migrated) ---
        private NesController nesController = new();
        private Corruptor corruptor = new();

        // Constructor
    public Emulator(ILogger logger,
                        IJSRuntime js,
                        HttpClient http,
                        StatusService status,
                        NesEmulator.Shaders.IShaderProvider shaderProvider,
                        NavigationManager nav)
        {
            Logger = logger;
            JS = js;
            Http = http;
            Status = status;
            ShaderProvider = shaderProvider;
            Nav = nav;
        }

        // Public surface to expose internal state if needed by UI after refactor
        public NesController Controller => nesController;
        public Corruptor Corruptor => corruptor;
        public Emulator UI => this;

        // Consumer (Razor page) can assign to receive change notifications
        public Action? OnStateChanged { get; set; }
        private void StateHasChanged() => OnStateChanged?.Invoke();

        // ================= Migrated Fields from Nes.razor =================
        private const int SaveChunkCharSize = 900_000; // chunk size for save state persistence
        private NesEmulator.NES? nes { get => nesController.nes; set => nesController.nes = value; }
        private bool[] inputState => nesController.inputState;
    // moved to UI.cs partial: mobileFsView, touchControllerInitialized
        private const string SaveKey = "nes_state_slot0";
        private bool stateBusy = false;
        private string debugDump = string.Empty;
    // Benchmark subsystem is moved to Benchmark.cs partial (fields retained there)
    private bool eventSchedulerOn = false; // still accessed by UI toggles
        private bool soundFontMode = false;
        private bool sampleFont = true; private bool soundFontLayering = false; private bool sfDevLogging = false; private bool sfOverlay = false; private string activeSfCore = string.Empty; private string activeSfCoreDisplay => string.IsNullOrEmpty(activeSfCore) ? (soundFontMode ? "(compat)" : "None") : activeSfCore;
        private DotNetObjectReference<Emulator>? _selfRef;
        private ElementReference fileInput; // NOTE: cannot be set outside component; keep placeholder
    // mobileFsViewPending removed (handled in UI partial if needed)
        private IEnumerable<RomOption> FilteredRomOptions => string.IsNullOrWhiteSpace(nesController.RomSearch)
            ? nesController.RomOptions.OrderBy(o=>o.BuiltIn ? 0 : 1).ThenBy(o=>o.Label)
            : nesController.RomOptions.Where(o=>o.Label.Contains(nesController.RomSearch, StringComparison.OrdinalIgnoreCase) || o.Key.Contains(nesController.RomSearch, StringComparison.OrdinalIgnoreCase))
                .OrderBy(o=>o.BuiltIn ? 0 : 1).ThenBy(o=>o.Label);
        private bool IsBuiltInSelected => nesController.RomOptions.FirstOrDefault(o => o.Key == nesController.RomFileName)?.BuiltIn ?? true;

        // Hover / comparison state
    // Benchmark hover/tooltips moved to Benchmark.cs

        // ================== Migrated Methods (adapted) ==================
        // NOTE: Some methods referencing Blazor component life-cycle have been converted.

        public void Initialize()
        {
            // Content formerly in OnInitialized
            nesController.RomOptions = new() { new RomOption{ Key="test.nes", Label="Test ROM (test.nes)", BuiltIn=true} };
            try { Nav.LocationChanged += OnLocationChanged; } catch {}
            nesController.CpuCoreOptions = NesEmulator.CoreRegistry.CpuIds.ToList();
            if (string.IsNullOrEmpty(nesController.CpuCoreSel) || !nesController.CpuCoreOptions.Contains(nesController.CpuCoreSel))
            {
                if (nesController.CpuCoreOptions.Contains("FMC")) nesController.CpuCoreSel = "FMC";
                else if (nesController.CpuCoreOptions.Contains("FIX")) nesController.CpuCoreSel = "FIX";
                else if (nesController.CpuCoreOptions.Count>0) nesController.CpuCoreSel = nesController.CpuCoreOptions[0];
            }
            nesController.PpuCoreOptions = NesEmulator.CoreRegistry.PpuIds.ToList();
            var desiredOrder = new List<string>{"FMC","SPD","CUBE","EIL","BFR","LQ","LOW"};
            nesController.PpuCoreOptions = nesController.PpuCoreOptions.OrderBy(id => desiredOrder.IndexOf(id) >=0 ? desiredOrder.IndexOf(id) : 99).ToList();
            if (!nesController.PpuCoreOptions.Contains(nesController.PpuCoreSel) && nesController.PpuCoreOptions.Count>0)
            {
                if (nesController.PpuCoreOptions.Contains("FMC")) nesController.PpuCoreSel = "FMC"; else if (nesController.PpuCoreOptions.Contains("NGTV")) nesController.PpuCoreSel = "NGTV"; else if (nesController.PpuCoreOptions.Contains("CUBE")) nesController.PpuCoreSel = "CUBE"; else nesController.PpuCoreSel = nesController.PpuCoreOptions[0];
            }
            nesController.ApuCoreOptions = NesEmulator.CoreRegistry.ApuIds.ToList();
            nesController.ApuCoreOptions = nesController.ApuCoreOptions.OrderBy(id => id switch { "FMC" => 0, "FIX" => 1, "QN" => 2, _ => 3 }).ThenBy(id=>id).ToList();
            if (string.IsNullOrEmpty(nesController.ApuCoreSel) || !nesController.ApuCoreOptions.Contains(nesController.ApuCoreSel))
            {
                if (nesController.ApuCoreOptions.Contains("FMC")) nesController.ApuCoreSel = "FMC";
                else if (nesController.ApuCoreOptions.Contains("FIX")) nesController.ApuCoreSel = "FIX";
                else if (nesController.ApuCoreOptions.Count>0) nesController.ApuCoreSel = nesController.ApuCoreOptions[0];
            }
            _ = Task.Run(async () => { try { var val = await JS.InvokeAsync<string>("nesInterop.idbGetItem", "pref_eventScheduler"); if (!string.IsNullOrEmpty(val)) { bool on = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase); await InvokeAsync(()=>{ eventSchedulerOn = on; if (nes!=null) nes.EnableEventScheduler = on; StateHasChanged(); }); } } catch { } });
        }

        // Helper to dispatch back on sync context (simulate ComponentBase.InvokeAsync)
        private Task InvokeAsync(Action work)
        {
            try { work(); } catch {} return Task.CompletedTask;
        }

        public async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                try
                {
                    Logger.LogInformation("NES Emulator initializing...");
                    Status.Set("Loading default ROM...");
                    _selfRef = DotNetObjectReference.Create(this);
                    try {
                        var pShader = await JS.InvokeAsync<string>("nesInterop.idbGetItem", "pref_shader"); if(!string.IsNullOrWhiteSpace(pShader)) nesController.ActiveShaderKey = pShader;
                        var pCpu = await JS.InvokeAsync<string>("nesInterop.idbGetItem", "pref_cpuCore"); if(!string.IsNullOrWhiteSpace(pCpu)) nesController.CpuCoreSel = pCpu;
                        var pPpu = await JS.InvokeAsync<string>("nesInterop.idbGetItem", "pref_ppuCore"); if(!string.IsNullOrWhiteSpace(pPpu)) nesController.PpuCoreSel = pPpu;
                        var pApu = await JS.InvokeAsync<string>("nesInterop.idbGetItem", "pref_apuCore"); if(!string.IsNullOrWhiteSpace(pApu)) nesController.ApuCoreSel = pApu;
                        await LoadBenchHistory();
                    } catch {}
                    await RefreshShaderOptions();
                    try { await JS.InvokeVoidAsync("nesInterop.migrateLocalStorageRoms"); } catch {}
                    var stored = await JS.InvokeAsync<UploadedRom[]>("nesInterop.getStoredRoms");
                    if (stored != null)
                    {
                        int restored = 0;
                        foreach (var r in stored)
                        {
                            if (string.IsNullOrWhiteSpace(r.name) || string.IsNullOrWhiteSpace(r.base64)) continue;
                            try
                            {
                                var data = Convert.FromBase64String(r.base64);
                                if (data.Length == 0) continue;
                                nesController.UploadedRoms[r.name] = data;
                                if (!nesController.RomOptions.Any(o => o.Key == r.name))
                                {
                                    nesController.RomOptions.Add(new RomOption { Key = r.name, Label = r.name + " (uploaded)", BuiltIn = false });
                                    restored++;
                                }
                            }
                            catch { }
                        }
                        if (restored > 0) Status.Set($"Restored {restored} uploaded ROM(s).");
                    }
                    if (_selfRef != null) await JS.InvokeVoidAsync("nesInterop.initRomDragDrop", "rom-table", _selfRef);
                    await LoadRomFromServer();
                    ApplySelectedCores();
                    if (_selfRef != null) await JS.InvokeVoidAsync("nesInterop.setMainRef", _selfRef);
                    if (_selfRef != null) await JS.InvokeVoidAsync("nesInterop.registerInput", _selfRef);
                    BuildMemoryDomains();
                    await RegisterShadersFromCSharp();
                    await RefreshShaderOptions();
                    await SetShader(nesController.ActiveShaderKey);
                    Logger.LogInformation("NES Emulator initialized successfully");
                    try { await JS.InvokeVoidAsync("nesInterop.ensureLayoutStyles"); } catch {}
                    try { await JS.InvokeVoidAsync("nesInterop.ensureAudioContext"); } catch {}
                    if (nes != null && !nesController.IsRunning) { try { await StartEmulation(); } catch { } }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, $"Failed to initialize NES emulator. Exception: {ex}");
                    nesController.ErrorMessage = $"Initialization failed: {ex.Message}\n{ex.StackTrace}";
                    Status.Set("Failed to load default ROM. Please upload a ROM file above.");
                }
            }
            if (nesController.IsFullscreen && MobileFullscreenView == "controller" && !touchControllerInitialized)
            {
                try { await JS.InvokeVoidAsync("nesInterop.initTouchController", "touch-controller"); touchControllerInitialized = true; } catch { }
            }
        }

    // LoadBenchHistory now in Benchmark partial

        // ================== (Representative subset of migrated methods) ==================
        // Due to size constraints, not every single method from Nes.razor is duplicated here.
        // Continue migration as needed following the established pattern.

        private async Task RegisterShadersFromCSharp()
        {
            try
            {
                foreach (var s in ShaderProvider.All)
                {
                    await JS.InvokeVoidAsync("nesInterop.registerShader", s.Id, s.DisplayName, s.FragmentSource, (object?)null);
                }
            }
            catch (Exception ex) { Logger.LogWarning(ex, "Failed to register shaders from C#"); }
        }

        private async Task RefreshShaderOptions()
        {
            try
            {
                var opts = await JS.InvokeAsync<object>("nesInterop.getShaderOptions");
                var json = System.Text.Json.JsonSerializer.Serialize(opts);
                var parsed = System.Text.Json.JsonDocument.Parse(json).RootElement;
                nesController.ShaderOptions.Clear();
                foreach (var el in parsed.EnumerateArray())
                {
                    string key = ""; string label = "";
                    if(el.TryGetProperty("key", out var keyProp)) key = keyProp.GetString() ?? "";
                    if(el.TryGetProperty("Key", out var keyProp2) && string.IsNullOrEmpty(key)) key = keyProp2.GetString() ?? "";
                    if(el.TryGetProperty("label", out var labProp)) label = labProp.GetString() ?? "";
                    if(el.TryGetProperty("Label", out var labProp2) && string.IsNullOrEmpty(label)) label = labProp2.GetString() ?? "";
                    if(string.IsNullOrEmpty(label)) label = key;
                    if(!string.IsNullOrEmpty(key)) nesController.ShaderOptions.Add(new ShaderOption(key,label));
                }
                if(!nesController.ShaderOptions.Any(o=>o.Key==nesController.ActiveShaderKey) && nesController.ShaderOptions.Count>0) nesController.ActiveShaderKey = nesController.ShaderOptions[0].Key;
            }
            catch (Exception ex) { Logger.LogWarning(ex, "Failed to refresh shader options"); }
        }

        private async Task SetShader(string key)
        {
            try
            {
                var displayName = await JS.InvokeAsync<string>("nesInterop.setShader", key);
                if(!string.IsNullOrEmpty(displayName))
                {
                    nesController.ActiveShaderKey = key;
                    nesController.ShaderOn = key != "PX";
                    Status.Set($"Shader: {displayName}");
                }
            }
            catch (Exception ex) { Logger.LogError(ex, $"SetShader error for {key}"); }
        }

        [JSInvokable]
        public async Task FrameTick()
        {
            if (!nesController.IsRunning) return;
            await RunFrame();
        }

        private async Task RunFrame()
        {
            if (nes == null || !nesController.IsRunning) return;
            try
            {
                bool autoStatic = string.Equals(nesController.CurrentRomName, "test.nes", StringComparison.OrdinalIgnoreCase) && !nesController.AutoStaticSuppressed;
                nes.EnableStatic(autoStatic);
                nes.SetInput(inputState);
                if (nesController.FastForward) nes.RunFrames(3); else nes.RunFrame();
                if (corruptor.AutoCorrupt) await Blast();
                nesController.FrameCount++;
                int queued = nes.GetQueuedAudioSamples();
                int targetChunk = queued > 4096 ? 2048 : (queued > 2048 ? 1024 : 768); // placeholder usage
                float[] audioBuffer = nes.GetAudioBuffer();
                bool queuedAudio = audioBuffer.Length > 0; int sampleRate = queuedAudio ? nes.GetAudioSampleRate() : 0;
                nesController.framebuffer = nes.GetFrameBuffer();
                _ = JS.InvokeVoidAsync("nesInterop.presentFrame", "nes-canvas", nesController.framebuffer, queuedAudio ? audioBuffer : null, sampleRate);
                if (nesController.FrameCount % nesController.StatsUpdateDivider == 0)
                {
                    var now = DateTime.Now;
                    if ((now - nesController.LastFpsUpdate).TotalSeconds >= 0.5)
                    {
                        nesController.Fps = (nesController.FrameCount - nesController.LastFrameCount) / (float)(now - nesController.LastFpsUpdate).TotalSeconds;
                        nesController.LastFrameCount = nesController.FrameCount;
                        nesController.LastFpsUpdate = now;
                    }
                    StateHasChanged();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Error in RunFrame. Exception: {ex}");
                nesController.ErrorMessage = $"Runtime error: {ex.Message}\n{ex.StackTrace}";
                _ = PauseEmulation();
                StateHasChanged();
            }
        }

        private async Task StartEmulation()
        {
            try
            {
                if (nes == null)
                {
                    nesController.ErrorMessage = "NES emulator not initialized"; return; }
                try { await JS.InvokeVoidAsync("nesInterop.ensureAudioContext"); } catch {}
                Logger.LogInformation("Starting emulation");
                nesController.IsRunning = true; nesController.ErrorMessage = ""; Status.Set("Emulation running...");
                _selfRef ??= DotNetObjectReference.Create(this);
                await JS.InvokeVoidAsync("nesInterop.startEmulationLoop", _selfRef);
                StateHasChanged();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to start emulation. Exception: {ex}");
                nesController.ErrorMessage = $"Failed to start: {ex.Message}\n{ex.StackTrace}"; nesController.IsRunning = false; StateHasChanged();
            }
        }

        private async Task PauseEmulation()
        {
            try
            {
                Logger.LogInformation("Pausing emulation");
                nesController.IsRunning = false;
                await JS.InvokeVoidAsync("nesInterop.stopEmulationLoop");
                try { nes?.FlushSoundFont(); } catch {}
                try { await JS.InvokeVoidAsync("nesInterop.flushSoundFont"); } catch {}
                Status.Set("Emulation paused");
                StateHasChanged();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to pause emulation. Exception: {ex}");
                nesController.ErrorMessage = $"Failed to pause: {ex.Message}\n{ex.StackTrace}";
            }
        }

    // ================= Public Wrapper API (for Razor) =================
    public float Fps => nesController.Fps;
    public bool IsRunning => nesController.IsRunning;
    public string CurrentRomName => nesController.CurrentRomName;
    public Task StartAsync() => StartEmulation();
    public Task PauseAsync() => PauseEmulation();
    public Task EnsureInitialRenderAsync(bool firstRender) => OnAfterRenderAsync(firstRender);

        private void ApplySelectedCores()
        {
            if (nes == null) return;
            try
            {
                if (!string.IsNullOrEmpty(nesController.CpuCoreSel))
                {
                    if (!nes.SetCpuCore(nesController.CpuCoreSel))
                        nes.SetCpuCore(nesController.CpuCoreSel == "FIX" ? NesEmulator.Bus.CpuCore.FIX : NesEmulator.Bus.CpuCore.FMC);
                }
                if (!nes.SetPpuCore(nesController.PpuCoreSel))
                {
                    nes.SetPpuCore(nesController.PpuCoreSel switch { "FIX" => NesEmulator.Bus.PpuCore.FIX, "LQ" => NesEmulator.Bus.PpuCore.LQ, "CUBE" => NesEmulator.Bus.PpuCore.CUBE, "BFR" => NesEmulator.Bus.PpuCore.BFR, _ => NesEmulator.Bus.PpuCore.FMC });
                }
                if (!string.IsNullOrEmpty(nesController.ApuCoreSel))
                {
                    if (!nes.SetApuCore(nesController.ApuCoreSel))
                    {
                        switch (nesController.ApuCoreSel)
                        {
                            case "FMC": nes.SetApuCore(NesEmulator.NES.ApuCore.Jank); nesController.FamicloneOn = true; break;
                            case "QN": nes.SetApuCore(NesEmulator.NES.ApuCore.QuickNes); nesController.FamicloneOn = false; break;
                            default: nes.SetApuCore(NesEmulator.NES.ApuCore.Modern); nesController.FamicloneOn = false; break;
                        }
                    }
                    nesController.FamicloneOn = nesController.ApuCoreSel.Equals("FMC", StringComparison.OrdinalIgnoreCase) || nesController.FamicloneOn;
                }
            }
            catch { }
            AutoConfigureForApuCore();
        }

        private void AutoConfigureForApuCore()
        {
            if (nes == null) return;
            try
            {
                if (string.Equals(nesController.ApuCoreSel, "WF", StringComparison.OrdinalIgnoreCase))
                {
                    if (!soundFontMode)
                    {
                        soundFontMode = nes.EnableSoundFontMode(true, (ch, prog, midi, vel, on, _) => { try { JS.InvokeVoidAsync("nesInterop.noteEvent", ch, prog, midi, vel, on); } catch { } });
                    }
                    sampleFont = true;
                    try { JS.InvokeVoidAsync("eval", "window.nesSoundFont && nesSoundFont.setPreferSampleBased && nesSoundFont.setPreferSampleBased(true);"); } catch {}
                    try { JS.InvokeVoidAsync("eval", "window.nesSoundFont && nesSoundFont.enableSampleMode && nesSoundFont.enableSampleMode();"); } catch {}
                }
                else if (string.Equals(nesController.ApuCoreSel, "MNES", StringComparison.OrdinalIgnoreCase))
                {
                    if (!soundFontMode)
                    {
                        soundFontMode = nes.EnableSoundFontMode(true, (ch, prog, midi, vel, on, _) => { try { JS.InvokeVoidAsync("nesInterop.noteEvent", ch, prog, midi, vel, on); } catch { } });
                    }
                    try { JS.InvokeVoidAsync("eval", "window.mnesSf2 && mnesSf2.enable && mnesSf2.enable();"); } catch {}
                }
                else
                {
                    if (soundFontMode) { nes.EnableSoundFontMode(false, null); soundFontMode = false; }
                }
            }
            catch { }
            _ = UpdateActiveSoundFontCoreAsync();
            StateHasChanged();
        }

        private async Task UpdateActiveSoundFontCoreAsync()
        {
            if (!soundFontMode)
            {
                activeSfCore = string.Empty;
                try { await JS.InvokeVoidAsync("nesInterop.setActiveSoundFontCore", (object?)null); } catch {}
                return;
            }
            string? core = null;
            if (string.Equals(nesController.ApuCoreSel, "WF", StringComparison.OrdinalIgnoreCase)) core = "WF";
            else if (string.Equals(nesController.ApuCoreSel, "MNES", StringComparison.OrdinalIgnoreCase)) core = "MNES";
            activeSfCore = core ?? string.Empty;
            try { await JS.InvokeVoidAsync("nesInterop.setActiveSoundFontCore", core, new { eager = true, flush = true }); } catch {}
            try { await JS.InvokeVoidAsync("nesInterop.setSoundFontLayering", soundFontLayering); } catch {}
        }

        private async Task LoadRomFromServer()
        {
            try
            {
                Logger.LogInformation($"Loading ROM from server: {nesController.RomFileName}");
                Status.Set($"Loading {nesController.RomFileName}...");
                var romData = await LoadRomFromWwwroot(nesController.RomFileName);
                if (romData.Length == 0) throw new Exception($"ROM file '{nesController.RomFileName}' not found or empty");
                bool wasRunning = nesController.IsRunning; if (wasRunning) await PauseEmulation();
                if (nes == null) nes = new NesEmulator.NES();
                nes.RomName = nesController.RomFileName; var prevApuSuffix = nesController.ApuCoreSel; nes.LoadROM(romData); if (!string.IsNullOrEmpty(prevApuSuffix)) { try { nes.SetApuCore(prevApuSuffix); } catch {} }
                try { nes.SetCrashBehavior(NesEmulator.NES.CrashBehavior.IgnoreErrors); } catch {}
                SetApuCoreSelFromEmu(); ApplySelectedCores();
                nesController.CurrentRomName = nesController.RomFileName; nesController.LastLoadedRomSize = romData.Length; if (!nesController.UploadedRoms.ContainsKey(nesController.RomFileName)) nesController.BuiltInRomSizes[nesController.RomFileName] = romData.Length;
                try { nes.RunFrame(); nesController.framebuffer = nes.GetFrameBuffer(); await JS.InvokeVoidAsync("nesInterop.drawFrame", "nes-canvas", nesController.framebuffer); } catch {}
                Status.Set($"ROM '{nesController.RomFileName}' loaded successfully!"); nesController.ErrorMessage = "";
                if (!string.Equals(nesController.CurrentRomName, "test.nes", StringComparison.OrdinalIgnoreCase)) { try { await JS.InvokeVoidAsync("nesInterop.focusCorruptorPanel"); } catch {} }
                if (nesController.HasBooted && wasRunning) { await StartEmulation(); }
                StateHasChanged();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to load ROM from server: {nesController.RomFileName}. Exception: {ex}");
                nesController.ErrorMessage = $"Failed to load ROM: {ex.Message}\n{ex.StackTrace}"; Status.Clear(); StateHasChanged();
            }
        }

        private async Task<byte[]> LoadRomFromWwwroot(string filename)
        {
            try
            {
                Logger.LogInformation($"Loading ROM: {filename}");
                var romData = await Http.GetByteArrayAsync(filename);
                Logger.LogInformation($"ROM loaded successfully: {romData.Length} bytes");
                if (romData.Length >= 16)
                {
                    var headerHex = string.Join(" ", romData.Take(16).Select(b => b.ToString("X2")));
                    Logger.LogInformation($"ROM Header: {headerHex}");
                }
                return romData;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to load ROM: {filename}");
                return Array.Empty<byte>();
            }
        }

        private void SetApuCoreSelFromEmu()
        {
            if (nes == null) return; try { var id = nes.GetApuCoreId(); var suffix = NesEmulator.CoreRegistry.ExtractSuffix(id, "APU_"); nesController.ApuCoreSel = suffix; nesController.FamicloneOn = suffix.Equals("FMC", StringComparison.OrdinalIgnoreCase); } catch { }
        }

        private void BuildMemoryDomains()
        {
            corruptor.MemoryDomains.Clear(); if (nes == null) return; try { corruptor.MemoryDomains.Add(new DomainSel{ Key="PRG", Label="PRG ROM", Size=GetApproxSize(idx=>nes.PeekPrg(idx)), Selected=false}); corruptor.MemoryDomains.Add(new DomainSel{ Key="PRGRAM", Label="PRG RAM", Size=GetApproxSize(idx=>nes.PeekPrgRam(idx)), Selected=false}); corruptor.MemoryDomains.Add(new DomainSel{ Key="CHR", Label="CHR", Size=GetApproxSize(idx=>nes.PeekChr(idx)), Selected=false}); corruptor.MemoryDomains.Add(new DomainSel{ Key="RAM", Label="System RAM", Size=2048, Selected=true}); } catch {} StateHasChanged();
        }

        private int GetApproxSize(Func<int,byte> peek)
        { int size = 1024; int lastNonZero = 0; for (int i=0;i<size;i+=128){ if (peek(i)!=0) lastNonZero=i; } for (int i=1024;i<=512*1024;i*=2) { byte v = peek(i-1); if (v!=0) lastNonZero = i-1; else { size = i; break; } } return Math.Max( (lastNonZero+256) & ~255, 0); }

        private Task Blast() { if (nes == null) return Task.CompletedTask; corruptor.Blast(nes); return Task.CompletedTask; }

        [JSInvokable] public void UpdateInput(bool[] state) { try { if (state.Length == 8) for (int i=0;i<8;i++) inputState[i] = state[i]; } catch (Exception ex) { Logger.LogError(ex, "Error updating input"); } }

        private bool _hardUnloadNavActive = false;
        private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
        {
            if (_hardUnloadNavActive) return;
            try
            {
                var dest = new Uri(e.Location);
                if (!string.Equals(dest.AbsolutePath, "/nes", StringComparison.OrdinalIgnoreCase))
                {
                    _hardUnloadNavActive = true;
                    string safe = e.Location.Replace("'", "\\'");
                    _ = JS.InvokeVoidAsync("eval", $"window.location.href='" + safe + "'");
                }
            }
            catch { }
        }

        // ================== End of subset migration ==================

        public void Dispose()
        {
            try
            {
                JS?.InvokeVoidAsync("nesInterop.stopEmulationLoop");
                _selfRef?.Dispose();
                try { Nav.LocationChanged -= OnLocationChanged; } catch {}
                Logger?.LogInformation("Emulator disposed");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error disposing emulator");
            }
        }
    }
}
