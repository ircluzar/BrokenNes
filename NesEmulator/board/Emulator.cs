using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using NesEmulator.RetroAchievements;

namespace BrokenNes
{
    /// <summary>
    /// Standalone class intended to host logic migrated from Nes.razor @code block.
    /// This is a scaffold; large methods & fields should be pasted/re-homed here.
    /// </summary>
    public partial class Emulator : IDisposable
    {
        // Compact payload returned to JS once per frame to reduce interop crossings
        public sealed class FramePayload
        {
            [JsonPropertyName("fb"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public byte[]? Framebuffer { get; set; }
            [JsonPropertyName("audio"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public float[]? Audio { get; set; }
            [JsonPropertyName("sr")] public int SampleRate { get; set; }
        }
        // --- Injected dependencies (mirrors Nes.razor @inject list) ---
    private readonly ILogger Logger;
        private readonly IJSRuntime JS;
        private readonly HttpClient Http;
    private readonly StatusService Status;
    private readonly BrokenNes.Services.InputSettingsService _inputSettingsService;
    private readonly NesEmulator.Shaders.IShaderProvider ShaderProvider;
    private readonly BrokenNes.Services.GameSaveService _gameSaveService;
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
                        NavigationManager nav,
                        BrokenNes.Services.InputSettingsService inputSettingsService,
                        BrokenNes.Services.GameSaveService gameSaveService)
        {
            Logger = logger;
            JS = js;
            Http = http;
            Status = status;
            ShaderProvider = shaderProvider;
            Nav = nav;
            _inputSettingsService = inputSettingsService;
            _gameSaveService = gameSaveService;
            _clockHost = new ClockHostFacade(this);
        }

        // Public surface to expose internal state if needed by UI after refactor
        public NesController Controller => nesController;
        public Corruptor Corruptor => corruptor;
        public Emulator UI => this;

        // Consumer (Razor page) can assign to receive change notifications
        public Action? OnStateChanged { get; set; }
        private void StateHasChanged() => OnStateChanged?.Invoke();

        // Achievements integration (optional)
        private AchievementsEngine? _achEngine;
        private readonly Dictionary<string, string> _achTitles = new(StringComparer.OrdinalIgnoreCase);
    // Random source for UX effects
    private static readonly Random s_rng = new();
    // Achievement unlock flow state
    private bool _achFlowActive = false;
    private bool _achModalOpen = false;
    private string _achModalTitle = string.Empty;
    private string _achModalId = string.Empty;
    public bool AchievementModalOpen => _achModalOpen;
    public string AchievementModalTitle => _achModalTitle;
    public string AchievementModalId => _achModalId;
        public void ConfigureAchievements(AchievementsEngine engine, IDictionary<string, string> titles)
        {
            _achEngine = engine;
            _achTitles.Clear();
            foreach (var kv in titles) _achTitles[kv.Key] = kv.Value ?? kv.Key;
        }

        // ================= Migrated Fields from Nes.razor =================
        private const int SaveChunkCharSize = 900_000; // chunk size for save state persistence
    private NesEmulator.NES? nes { get => nesController.nes; set => nesController.nes = value; }
    private bool[] inputState => nesController.inputState;
    private bool[] inputStateP2 => nesController.inputStateP2;
    private BrokenNes.Models.InputSettings? inputSettings;
    public BrokenNes.Models.InputSettings? InputSettingsPublic => inputSettings;
    // mobileFsView, touchControllerInitialized moved to UI.cs partial
        private const string SaveKey = "nes_state_slot0";
        private bool stateBusy = false;
        private string debugDump = string.Empty;
    private BrokenNes.Models.GameSave? _gameSave; // Loaded game save for DeckBuilder and feature gates
    public bool SavestatesUnlocked => _gameSave?.SavestatesUnlocked == true;
    public bool RtcUnlocked => _gameSave?.RtcUnlocked == true;
    public bool GhUnlocked => _gameSave?.GhUnlocked == true;
    public bool ImagineUnlocked => _gameSave?.ImagineUnlocked == true;
    public bool DebugUnlocked => _gameSave?.DebugUnlocked == true;
    // Benchmark subsystem is moved to Benchmark.cs partial (fields retained there)
    private bool eventSchedulerOn = false; // still accessed by UI toggles
        private bool soundFontMode = false;
        private bool sampleFont = true; private bool soundFontLayering = false; private bool sfDevLogging = false; private bool sfOverlay = false; private string activeSfCore = string.Empty; private string activeSfCoreDisplay => string.IsNullOrEmpty(activeSfCore) ? (soundFontMode ? "(compat)" : "None") : activeSfCore;
    private DotNetObjectReference<Emulator>? _selfRef;
        // Clock core integration
        private NesEmulator.IClock? _activeClock;
        private CancellationTokenSource? _clockCts;
        private sealed class ClockHostFacade : NesEmulator.IClockHost
        {
            private readonly Emulator _emu;
            public ClockHostFacade(Emulator emu) { _emu = emu; }
            public bool IsRunning => _emu.nesController.IsRunning;
            public async ValueTask RequestStartJsLoopAsync()
            {
                _emu._selfRef ??= DotNetObjectReference.Create(_emu);
                try { await _emu.JS.InvokeVoidAsync("nesInterop.startEmulationLoop", _emu._selfRef!); } catch {}
            }
            public async ValueTask RequestStopJsLoopAsync()
            { try { await _emu.JS.InvokeVoidAsync("nesInterop.stopEmulationLoop"); } catch {} }
            public void RunFrame() { _emu.RunFrameAndBuildPayload(); }
            public BrokenNes.Emulator.FramePayload RunFrameAndBuildPayload() => _emu.RunFrameAndBuildPayload();
            public async ValueTask PresentAsync(BrokenNes.Emulator.FramePayload payload)
            {
                try { await _emu.JS.InvokeVoidAsync("nesInterop.presentFrame", "nes-canvas", payload.Framebuffer, payload.Audio, payload.SampleRate); }
                catch { }
            }
        }
        private readonly ClockHostFacade _clockHost;
    // mobileFsViewPending handled in UI partial
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
            // Content from OnInitialized
            nesController.RomOptions = new() { new RomOption{ Key="test.nes", Label="Test ROM (test.nes)", BuiltIn=true} };
            // Provide hooks for corruptor -> imagine bridge
            try { corruptor.EmulatorHooks = new CorruptorImagineHooks(this); } catch {}
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
            // Initialize Clock Core registry and options
            try { NesEmulator.ClockRegistry.Initialize(); } catch {}
            try { nesController.ClockCoreOptions = NesEmulator.ClockRegistry.Ids.ToList(); } catch { nesController.ClockCoreOptions = new List<string>{"FMC"}; }
            if (string.IsNullOrWhiteSpace(nesController.ClockCoreSel) || !nesController.ClockCoreOptions.Contains(nesController.ClockCoreSel))
            {
                nesController.ClockCoreSel = nesController.ClockCoreOptions.Contains("FMC") ? "FMC" : (nesController.ClockCoreOptions.FirstOrDefault() ?? "FMC");
            }
        }

        private sealed class CorruptorImagineHooks : BrokenNes.CorruptorModels.ICorruptorEmulatorHooks
        {
            private readonly Emulator _emu;
            public CorruptorImagineHooks(Emulator emu) { _emu = emu; }
            public async void ImagineFromPc(ushort pc, int bytesToGenerate)
            {
                try
                {
                    if (!_emu.ImagineModelLoaded) { _emu.Status.Set("Imagine: model not loaded"); return; }
                    if (pc < 0x8000 || pc > 0xFFFF) { _emu.Status.Set("Imagine: PC not in PRG ROM"); return; }
                    int L = Math.Clamp(bytesToGenerate, 1, 32);
                    var tokens = _emu.BuildTokens128AroundPc(pc, L, out int hs, out int he);
                    var bytes = await _emu.ImaginePredictSpanAsync(tokens, hs, he, _emu.ImagineTemperature, _emu.ImagineTopK);
                    await _emu.ApplyImaginePatchAsync(pc, bytes);
                }
                catch (Exception ex) { _emu.Status.Set($"Imagine error: {ex.Message}"); }
            }
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
                        var pClk = await JS.InvokeAsync<string>("nesInterop.idbGetItem", "pref_clockCore"); if(!string.IsNullOrWhiteSpace(pClk) && nesController.ClockCoreOptions.Contains(pClk)) nesController.ClockCoreSel = pClk;
                        await LoadBenchHistory();
                    } catch {}
                    // Load DeckBuilder save and filter core options to owned items
                    try { _gameSave = await _gameSaveService.LoadAsync(); FilterCoreOptionsBySave(_gameSave); } catch {}
                    await RefreshShaderOptions();
                    // Filter shader options to owned items
                    try { if (_gameSave != null) FilterShaderOptionsBySave(_gameSave); } catch {}
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
                    // Load saved input settings and configure JS input handlers for P1/P2
                    try
                    {
                        inputSettings = await _inputSettingsService.LoadAsync();
                        if (_selfRef != null && inputSettings != null)
                        {
                            var cfg = new
                            {
                                player1 = new
                                {
                                    device = inputSettings.Player1.Device.ToString(),
                                    keyboard = inputSettings.Player1.Keyboard,
                                    gamepad = inputSettings.Player1.Gamepad,
                                    gamepadIndex = inputSettings.Player1.GamepadIndex
                                },
                                player2 = new
                                {
                                    device = inputSettings.Player2.Device.ToString(),
                                    keyboard = inputSettings.Player2.Keyboard,
                                    gamepad = inputSettings.Player2.Gamepad,
                                    gamepadIndex = inputSettings.Player2.GamepadIndex
                                }
                            };
                            await JS.InvokeVoidAsync("nesInterop.configureInput", _selfRef, cfg);
                        }
                    }
                    catch (Exception ex) { Logger.LogWarning(ex, "Failed to configure input settings"); }
                    BuildMemoryDomains();
                    await RegisterShadersFromCSharp();
                    await RefreshShaderOptions();
                    try { if (_gameSave != null) FilterShaderOptionsBySave(_gameSave); } catch {}
                    await SetShader(nesController.ActiveShaderKey);
                    Logger.LogInformation("NES Emulator initialized successfully");
                    try { await JS.InvokeVoidAsync("nesInterop.ensureLayoutStyles"); } catch {}
                    try { await JS.InvokeVoidAsync("nesInterop.ensureAudioContext"); } catch {}
                    if (nes != null && !nesController.IsRunning) { try { await StartEmulation(); } catch { } }
                    // Register visibility events for CLR throttling and general lifecycle
                    if (_selfRef != null) { try { await JS.InvokeVoidAsync("nesInterop.registerVisibility", _selfRef); } catch { } }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, $"Failed to initialize NES emulator. Exception: {ex}");
                    nesController.ErrorMessage = $"Initialization failed: {ex.Message}\n{ex.StackTrace}";
                    Status.Set("Failed to load default ROM. Please upload a ROM file above.");
                }
            }
            if (nesController.IsFullscreen && MobileFullscreenView == "controller")
            {
                // Idempotent init each render while in controller view; JS guards per-element via _nesBound
                try { await JS.InvokeVoidAsync("nesInterop.initTouchController", "touch-controller"); } catch { }
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

        // Restrict shader options to owned shader ids from save; ensure sensible fallback
        private void FilterShaderOptionsBySave(BrokenNes.Models.GameSave save)
        {
            try
            {
                var owned = new HashSet<string>(save.OwnedShaderIds ?? new(), StringComparer.OrdinalIgnoreCase);
                if (owned.Count == 0) return;
                nesController.ShaderOptions = nesController.ShaderOptions.Where(s => owned.Contains(s.Key)).ToList();
                // Default to PX if available; else first owned
                if (!nesController.ShaderOptions.Any(o => o.Key == nesController.ActiveShaderKey))
                {
                    nesController.ActiveShaderKey = nesController.ShaderOptions.Any(o => o.Key == "PX") ? "PX" : (nesController.ShaderOptions.FirstOrDefault()?.Key ?? "PX");
                }
            }
            catch { }
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
        public Task<FramePayload> FrameTick()
        {
            if (!nesController.IsRunning)
            {
                return Task.FromResult(new FramePayload { Framebuffer = null, Audio = null, SampleRate = 0 });
            }
            var payload = RunFrameAndBuildPayload();
            return Task.FromResult(payload);
        }

        private FramePayload RunFrameAndBuildPayload()
        {
            if (nes == null || !nesController.IsRunning)
                return new FramePayload { Framebuffer = null, Audio = null, SampleRate = 0 };
            try
            {
                bool autoStatic = string.Equals(nesController.CurrentRomName, "test.nes", StringComparison.OrdinalIgnoreCase) && !nesController.AutoStaticSuppressed;
                nes.EnableStatic(autoStatic);
                nes.SetInputs(inputState, inputStateP2);
                if (nesController.FastForward) nes.RunFrames(3); else nes.RunFrame();
                if (corruptor.AutoCorrupt) { _ = Blast(); }
                // Evaluate achievements after the frame
                try
                {
                    if (_achEngine != null)
                    {
                        var unlocked = _achEngine.EvaluateFrame();
                        if (unlocked != null && unlocked.Count > 0)
                        {
                            // Trigger flow for the first unlocked id only (guard against duplicates/same-frame multiples)
                            var id = unlocked[0];
                            string title = _achTitles.TryGetValue(id, out var t) ? t : id;
                            if (!_achFlowActive)
                            {
                                _ = TriggerAchievementUnlockFlowAsync(id, title);
                            }
                        }
                    }
                }
                catch { }
                nesController.FrameCount++;
                int queued = nes.GetQueuedAudioSamples();
                float[] audioBuffer = nes.GetAudioBuffer();
                bool queuedAudio = audioBuffer.Length > 0; int sampleRate = queuedAudio ? nes.GetAudioSampleRate() : 0;
                nesController.framebuffer = nes.GetFrameBuffer();
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
                return new FramePayload
                {
                    Framebuffer = nesController.framebuffer,
                    Audio = queuedAudio ? audioBuffer : null,
                    SampleRate = sampleRate
                };
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Error in RunFrame. Exception: {ex}");
                nesController.ErrorMessage = $"Runtime error: {ex.Message}\n{ex.StackTrace}";
                _ = PauseEmulation();
                StateHasChanged();
                return new FramePayload { Framebuffer = null, Audio = null, SampleRate = 0 };
            }
        }

        private async Task TriggerAchievementUnlockFlowAsync(string id, string title)
        {
            _achFlowActive = true;
            try
            {
                // 1) Save state immediately to the default slot
                try { await SaveStateAsync(); } catch { }
                // 2) Pause emulation
                try { await PauseEmulation(); } catch { }
                // 2.5) Play a random victory song
                try
                {
                    try { await JS.InvokeVoidAsync("nesInterop.ensureAudioContext"); } catch { }
                    int pick = 1 + s_rng.Next(0, 5); // 1..5
                    string src = $"music/VictorySong{pick}.mp3";
                    var srcJson = System.Text.Json.JsonSerializer.Serialize(src);
                    await JS.InvokeVoidAsync("eval",
                        "(function(){ try{ if(window.music){ if(typeof window.music.stop==='function') window.music.stop(); if(typeof window.music.play==='function') window.music.play(" + srcJson + ", { fadeInMs: 600 }); } }catch(e){} })();");
                }
                catch { }
                // 3) Register achievement in game save (by unique ID)
                try
                {
                    var save = await _gameSaveService.LoadAsync();
                    save.Achievements ??= new();
                    if (!save.Achievements.Contains(id, StringComparer.OrdinalIgnoreCase))
                    {
                        save.Achievements.Add(id);
                        await _gameSaveService.SaveAsync(save);
                    }
                }
                catch { }
                // 3b) Mark trusted DeckBuilder continue for this rom
                try
                {
                    var romKey = nesController.RomFileName ?? nesController.CurrentRomName ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(romKey))
                    {
                        await SetTrustedContinueAsync(romKey, title);
                    }
                }
                catch { }
                // 4) Open modal with title and id; 5) Auto-redirect after 5s
                _achModalId = id;
                _achModalTitle = title;
                _achModalOpen = true;
                StateHasChanged();
                try
                {
                    // Use JS to wait 5s then navigate to Continue page
                    await JS.InvokeVoidAsync("eval", @"(function(){ setTimeout(function(){ try{ window.location.href = './continue'; }catch(e){} }, 5000); })();");
                }
                catch { }
            }
            finally
            {
                // Keep modal open until redirect; mark flow as inactive for subsequent unlocks
                _achFlowActive = false;
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
                await StartClockAsync();
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
                await StopClockAsync();
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

        private async Task StartClockAsync()
        {
            // Ensure prior clock stopped
            await StopClockAsync();
            // Create selected clock; fallback to FMC
            var id = nesController.ClockCoreSel;
            _activeClock = NesEmulator.ClockRegistry.Create(id) ?? NesEmulator.ClockRegistry.Create("FMC");
            _clockCts = new CancellationTokenSource();
            // Reset audio timeline to avoid desync when swapping clocks
            try { await JS.InvokeVoidAsync("nesInterop.flushAudioOutput"); } catch {}
            // Inform JS of active clock id for per-core audio routing
            try { await JS.InvokeVoidAsync("nesInterop.setActiveClockId", _activeClock?.CoreId ?? string.Empty); } catch {}
            try { if (_activeClock != null) await _activeClock.StartAsync(_clockHost, _clockCts.Token); } catch {}
        }

        private async Task StopClockAsync()
        {
            try { _activeClock?.Stop(); } catch {}
            _activeClock = null;
            try { _clockCts?.Cancel(); } catch {}
            _clockCts = null;
            // Defensive: ensure JS rAF loop is stopped
            try { await JS.InvokeVoidAsync("nesInterop.stopEmulationLoop"); } catch {}
            // Flush audio to clear any scheduled buffers/ring tail
            try { await JS.InvokeVoidAsync("nesInterop.flushAudioOutput"); } catch {}
            // Clear JS clock id to default routing
            try { await JS.InvokeVoidAsync("nesInterop.setActiveClockId", string.Empty); } catch {}
        }

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

        // Filter CPU/PPU/APU/Clock options using the save's owned lists and preserve ordering/fallbacks
        private void FilterCoreOptionsBySave(BrokenNes.Models.GameSave save)
        {
            try
            {
                var ownedCpu = new HashSet<string>(save.OwnedCpuIds ?? new(), StringComparer.OrdinalIgnoreCase);
                var ownedPpu = new HashSet<string>(save.OwnedPpuIds ?? new(), StringComparer.OrdinalIgnoreCase);
                var ownedApu = new HashSet<string>(save.OwnedApuIds ?? new(), StringComparer.OrdinalIgnoreCase);
                var ownedClock = new HashSet<string>(save.OwnedClockIds ?? new(), StringComparer.OrdinalIgnoreCase);

                if (ownedCpu.Count > 0)
                {
                    nesController.CpuCoreOptions = nesController.CpuCoreOptions.Where(id => ownedCpu.Contains(id)).ToList();
                    if (!nesController.CpuCoreOptions.Contains(nesController.CpuCoreSel))
                    {
                        nesController.CpuCoreSel = nesController.CpuCoreOptions.Contains("FMC") ? "FMC" : (nesController.CpuCoreOptions.FirstOrDefault() ?? nesController.CpuCoreSel);
                    }
                }
                if (ownedPpu.Count > 0)
                {
                    nesController.PpuCoreOptions = nesController.PpuCoreOptions.Where(id => ownedPpu.Contains(id)).ToList();
                    if (!nesController.PpuCoreOptions.Contains(nesController.PpuCoreSel))
                    {
                        nesController.PpuCoreSel = nesController.PpuCoreOptions.Contains("FMC") ? "FMC" : (nesController.PpuCoreOptions.FirstOrDefault() ?? nesController.PpuCoreSel);
                    }
                }
                if (ownedApu.Count > 0)
                {
                    nesController.ApuCoreOptions = nesController.ApuCoreOptions.Where(id => ownedApu.Contains(id)).ToList();
                    if (!nesController.ApuCoreOptions.Contains(nesController.ApuCoreSel))
                    {
                        nesController.ApuCoreSel = nesController.ApuCoreOptions.Contains("FMC") ? "FMC" : (nesController.ApuCoreOptions.FirstOrDefault() ?? nesController.ApuCoreSel);
                    }
                }
                if (ownedClock.Count > 0)
                {
                    nesController.ClockCoreOptions = nesController.ClockCoreOptions.Where(id => ownedClock.Contains(id)).ToList();
                    if (!nesController.ClockCoreOptions.Contains(nesController.ClockCoreSel))
                    {
                        nesController.ClockCoreSel = nesController.ClockCoreOptions.Contains("FMC") ? "FMC" : (nesController.ClockCoreOptions.FirstOrDefault() ?? nesController.ClockCoreSel);
                    }
                }
            }
            catch { }
        }

        private void AutoConfigureForApuCore()
        {
            if (nes == null) return;
            try
            {
                // Always flush JS-side soundfont when changing cores to avoid stale processors/ports
                try { JS.InvokeVoidAsync("nesInterop.flushSoundFont"); } catch {}
                if (string.Equals(nesController.ApuCoreSel, "WF", StringComparison.OrdinalIgnoreCase))
                {
                    // Force rebind even if previously enabled to ensure switching WF<->MNES reattaches delegates
                    soundFontMode = nes.EnableSoundFontMode(true, (ch, prog, midi, vel, on, _) => { try { JS.InvokeVoidAsync("nesInterop.noteEvent", ch, prog, midi, vel, on); } catch { } });
                    sampleFont = true;
                    try { JS.InvokeVoidAsync("eval", "window.nesSoundFont && nesSoundFont.setPreferSampleBased && nesSoundFont.setPreferSampleBased(true);"); } catch {}
                    try { JS.InvokeVoidAsync("eval", "window.nesSoundFont && nesSoundFont.enableSampleMode && nesSoundFont.enableSampleMode();"); } catch {}
                }
                else if (string.Equals(nesController.ApuCoreSel, "MNES", StringComparison.OrdinalIgnoreCase))
                {
                    soundFontMode = nes.EnableSoundFontMode(true, (ch, prog, midi, vel, on, _) => { try { JS.InvokeVoidAsync("nesInterop.noteEvent", ch, prog, midi, vel, on); } catch { } });
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
                // Wire Imagine Fix callback: when NES requests an Imagine shot, run with current settings.
                // Guard against overlapping predictions; allow periodic retries from NES core.
                try {
                    bool imagineInFlight = false;
                    nes.ImagineShot = pc => {
                        try {
                            if (imagineInFlight) return; // skip if a prior shot is still running
                            if (!ImagineModelLoaded) return;
                            imagineInFlight = true;
                            var L = Math.Clamp(ImagineBytesToGenerate, 1, 32);
                            var tokens = BuildTokens128AroundPc(pc, L, out int hs, out int he);
                            var _ = ImaginePredictSpanAsync(tokens, hs, he, ImagineTemperature, ImagineTopK)
                                .ContinueWith(async t =>
                                {
                                    try { if (t.Status == TaskStatus.RanToCompletion && t.Result != null) await ApplyImaginePatchAsync(pc, t.Result); }
                                    catch { }
                                    finally { imagineInFlight = false; }
                                });
                        } catch { imagineInFlight = false; }
                    };
                } catch { }
                nes.RomName = nesController.RomFileName; var prevApuSuffix = nesController.ApuCoreSel; nes.LoadROM(romData); if (!string.IsNullOrEmpty(prevApuSuffix)) { try { nes.SetApuCore(prevApuSuffix); } catch {} }
                // Apply currently selected crash behavior (preserve user choice)
                try { ApplySelectedCrashBehavior(); } catch {}
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
        [JSInvokable] public void UpdateInputForPlayer(int player, bool[] state)
        {
            try
            {
                if (state == null || state.Length != 8) return;
                var dst = (player == 2) ? inputStateP2 : inputState;
                for (int i = 0; i < 8; i++) dst[i] = state[i];
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Error updating input for player {player}");
            }
        }

        // Load a ROM from wwwroot by filename for narration pages without registering it in the ROM manager
        // Returns true on success. This is intentionally not modifying RomOptions.
        [JSInvokable]
        public async Task<bool> JsLoadBuiltInRom(string filename)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filename)) return false;
                Logger.LogInformation($"[Story] Loading built-in page ROM: {filename}");
                var romData = await LoadRomFromWwwroot(filename);
                if (romData == null || romData.Length == 0)
                {
                    Logger.LogWarning($"[Story] Page ROM not found or empty: {filename}");
                    return false;
                }
                bool wasRunning = nesController.IsRunning; if (wasRunning) await PauseEmulation();
                if (nes == null) nes = new NesEmulator.NES();
                var prevApuSuffix = nesController.ApuCoreSel;
                nes.RomName = filename;
                nes.LoadROM(romData);
                if (!string.IsNullOrEmpty(prevApuSuffix)) { try { nes.SetApuCore(prevApuSuffix); } catch { } }
                try { ApplySelectedCrashBehavior(); } catch { }
                SetApuCoreSelFromEmu();
                ApplySelectedCores();
                // Update current ROM tracking (do not add to RomOptions; no registration)
                nesController.RomFileName = filename;
                nesController.CurrentRomName = filename;
                nesController.LastLoadedRomSize = romData.Length;
                if (!nesController.UploadedRoms.ContainsKey(filename)) nesController.BuiltInRomSizes[filename] = romData.Length;
                // Warm-up a frame to avoid stale canvas
                try { nes.RunFrame(); nesController.framebuffer = nes.GetFrameBuffer(); await JS.InvokeVoidAsync("nesInterop.drawFrame", "nes-canvas", nesController.framebuffer); } catch { }
                if (nesController.HasBooted && wasRunning) { await StartEmulation(); }
                StateHasChanged();
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"[Story] Failed to load page ROM: {filename}");
                return false;
            }
        }

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
