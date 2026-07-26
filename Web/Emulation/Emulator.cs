using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using NesEmulator;

namespace BrokenNes;

/// <summary>
/// Minimal web host for the shared NES cores.
///
/// The class name, its namespace, and the nested <see cref="FramePayload"/> type are NOT free
/// choices: NesEmulator.IClockHost (Windows/NesEmulator/clocks/IClock.cs) names
/// BrokenNes.Emulator.FramePayload directly in its signatures. Renaming any of them breaks the
/// clock system.
///
/// This deliberately replaces the old Blazor shell (Windows/NesEmulator/board/Emulator*.cs)
/// rather than reviving it: that shell is a 9-file partial whose constructor hard-requires
/// IShaderProvider, GameSaveService and InputSettingsService, and which drags in achievements,
/// the corruptor, Imagine and the benchmark UI. None of that belongs in a plain web emulator.
/// </summary>
public sealed partial class Emulator : IAsyncDisposable
{
    /// <summary>One frame's output. JSON names are consumed by nesInterop.js.</summary>
    public sealed class FramePayload
    {
        [JsonPropertyName("fb"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public byte[]? Framebuffer { get; set; }
        [JsonPropertyName("audio"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Audio { get; set; }
        [JsonPropertyName("sr")] public int SampleRate { get; set; }
    }

    /// <summary>APU cores that only work on the desktop host (winmm MIDI out / filesystem SF2 probe).</summary>
    public static readonly string[] DesktopOnlyApus = { "WF", "MNES" };

    /// <summary>Clocks that busy-wait and can wedge the browser when a frame overruns its budget.</summary>
    public static readonly string[] ExperimentalClocks = { "CLR", "TRB" };

    private const string CanvasId = "nes-canvas";
    private const string ClockPrefKey = "web_pref_clockCore";

    private readonly IJSRuntime JS;
    private DotNetObjectReference<Emulator>? _selfRef;
    private readonly ClockHostFacade _clockHost;

    private NES? _nes;
    private byte[]? _romBytes;
    private IClock? _activeClock;
    private CancellationTokenSource? _clockCts;

    // Fixed by the cores: 0=Up 1=Down 2=Left 3=Right 4=A 5=B 6=Select 7=Start
    private readonly bool[] _p1 = new bool[8];
    private readonly bool[] _p2 = new bool[8];

    public bool IsRunning { get; private set; }
    public string RomName { get; private set; } = string.Empty;
    public long FrameCount { get; private set; }
    public double Fps { get; private set; }
    public string? ErrorMessage { get; private set; }

    public List<string> ClockCoreOptions { get; private set; } = new();
    public string ClockCoreSel { get; private set; } = "FMC";
    public List<string> CpuCoreOptions { get; private set; } = new();
    public List<string> PpuCoreOptions { get; private set; } = new();
    public List<string> ApuCoreOptions { get; private set; } = new();

    // NES.Get*CoreId() returns the raw CLR type name ("CPU_SPD"), but Set*Core() and
    // CoreRegistry.*Ids both use the bare suffix ("SPD"). Normalise so the <select>
    // values actually match the roster.
    public string CpuCoreSel => StripCorePrefix(_nes?.GetCpuCoreId(), "CPU_");
    public string PpuCoreSel => StripCorePrefix(_nes?.GetPpuCoreId(), "PPU_");
    public string ApuCoreSel => StripCorePrefix(_nes?.GetApuCoreId(), "APU_");

    private static string StripCorePrefix(string? typeName, string prefix)
        => string.IsNullOrEmpty(typeName) ? string.Empty
         : typeName.StartsWith(prefix, StringComparison.Ordinal) ? typeName.Substring(prefix.Length)
         : typeName;

    public Action? OnStateChanged;

    private DateTime _fpsMark = DateTime.UtcNow;
    private long _fpsFrames;

    public Emulator(IJSRuntime js)
    {
        JS = js;
        _clockHost = new ClockHostFacade(this);
    }

    // ---- Core / clock roster ------------------------------------------------

    /// <summary>
    /// Populates the core rosters. Every registry here discovers by reflection over a name
    /// prefix and swallows its own failures, so each call is guarded and has a fallback.
    /// </summary>
    public void Initialize()
    {
        try { ClockRegistry.Initialize(); } catch { }
        try { ClockCoreOptions = ClockRegistry.Ids.ToList(); } catch { ClockCoreOptions = new(); }
        if (ClockCoreOptions.Count == 0) ClockCoreOptions.Add("FMC");
        if (!ClockCoreOptions.Contains(ClockCoreSel))
            ClockCoreSel = ClockCoreOptions.Contains("FMC") ? "FMC" : ClockCoreOptions[0];

        try { CpuCoreOptions = CoreRegistry.CpuIds.ToList(); } catch { }
        try { PpuCoreOptions = CoreRegistry.PpuIds.ToList(); } catch { }
        try { ApuCoreOptions = CoreRegistry.ApuIds.ToList(); } catch { }
    }

    /// <summary>Restores the persisted clock choice. Separate from Initialize because it needs JS.</summary>
    public async Task RestorePreferencesAsync()
    {
        try
        {
            var saved = await JS.InvokeAsync<string?>("nesInterop.idbGetItem", ClockPrefKey);
            if (!string.IsNullOrWhiteSpace(saved) && ClockCoreOptions.Contains(saved))
                ClockCoreSel = saved;
        }
        catch { }
    }

    public static bool IsDesktopOnlyApu(string id) =>
        DesktopOnlyApus.Any(s => id.Equals(s, StringComparison.OrdinalIgnoreCase)
                              || id.Equals("APU_" + s, StringComparison.OrdinalIgnoreCase));

    public static bool IsExperimentalClock(string id) =>
        ExperimentalClocks.Any(s => id.Equals(s, StringComparison.OrdinalIgnoreCase));

    // ---- ROM loading --------------------------------------------------------

    public bool IsLoading { get; private set; }

    /// <summary>
    /// Guarded against overlapping calls: picking a second ROM while a first is still
    /// streaming in (slow disk/network) previously raced, and whichever CopyToAsync finished
    /// last silently won regardless of pick order. Nes.razor also disables the file picker
    /// while IsLoading is true, but the guard here is authoritative either way.
    /// </summary>
    public async Task LoadRomAsync(string name, byte[] rom)
    {
        if (IsLoading) return;
        IsLoading = true;
        OnStateChanged?.Invoke();
        try
        {
            if (IsRunning) await PauseAsync();
            try
            {
                ErrorMessage = null;
                _nes = new NES { RomName = name };
                _nes.LoadROM(rom);
                _romBytes = rom;
                RomName = name;
                FrameCount = 0;
                // Run one frame so the canvas shows something before the clock starts.
                _nes.RunFrame();
                await JS.InvokeVoidAsync("nesInterop.drawFrame", CanvasId, _nes.GetFrameBuffer());
            }
            catch (Cartridge.UnsupportedMapperException ex)
            {
                _nes = null;
                ErrorMessage = $"Unsupported mapper {ex.MapperId} ({ex.MapperName}).";
            }
            catch (Exception ex)
            {
                _nes = null;
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            IsLoading = false;
        }
        OnStateChanged?.Invoke();
    }

    // ---- Transport ----------------------------------------------------------

    public async Task StartAsync()
    {
        if (_nes == null || IsRunning) return;
        try { await JS.InvokeVoidAsync("nesInterop.ensureAudioContext"); } catch { }
        IsRunning = true;
        await StartClockAsync();
        OnStateChanged?.Invoke();
    }

    public async Task PauseAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        await StopClockAsync();
        OnStateChanged?.Invoke();
    }

    public Task ToggleAsync() => IsRunning ? PauseAsync() : StartAsync();

    /// <summary>NES exposes no public Reset(); the old shell reloaded the ROM bytes instead.</summary>
    public async Task ResetAsync()
    {
        if (_romBytes == null) return;
        bool wasRunning = IsRunning;
        await PauseAsync();
        try { await JS.InvokeVoidAsync("nesInterop.resetAudioTimeline"); } catch { }
        await LoadRomAsync(RomName, _romBytes);
        if (wasRunning) await StartAsync();
    }

    // ---- Core switching -----------------------------------------------------

    public async Task SetClockCoreAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !ClockCoreOptions.Contains(id)) return;
        bool wasRunning = IsRunning;
        await PauseAsync();
        ClockCoreSel = id;
        try { await JS.InvokeVoidAsync("nesInterop.idbSetItem", ClockPrefKey, id); } catch { }
        if (wasRunning) await StartAsync();
        OnStateChanged?.Invoke();
    }

    public void SetCpuCore(string id) { try { _nes?.SetCpuCore(id); } catch { } OnStateChanged?.Invoke(); }
    public void SetPpuCore(string id) { try { _nes?.SetPpuCore(id); } catch { } OnStateChanged?.Invoke(); }
    public void SetApuCore(string id) { try { _nes?.SetApuCore(id); } catch { } OnStateChanged?.Invoke(); }

    // ---- Clock plumbing (mirrors the old Emulator.cs Start/StopClockAsync) ---

    private async Task StartClockAsync()
    {
        await StopClockAsync();
        _activeClock = ClockRegistry.Create(ClockCoreSel) ?? ClockRegistry.Create("FMC");
        _clockCts = new CancellationTokenSource();
        try { await JS.InvokeVoidAsync("nesInterop.flushAudioOutput"); } catch { }
        try { await JS.InvokeVoidAsync("nesInterop.setActiveClockId", _activeClock?.CoreId ?? string.Empty); } catch { }
        try { if (_activeClock != null) await _activeClock.StartAsync(_clockHost, _clockCts.Token); } catch { }
    }

    private async Task StopClockAsync()
    {
        try { _activeClock?.Stop(); } catch { }
        _activeClock = null;
        try { _clockCts?.Cancel(); } catch { }
        _clockCts = null;
        try { await JS.InvokeVoidAsync("nesInterop.stopEmulationLoop"); } catch { }
        try { await JS.InvokeVoidAsync("nesInterop.flushAudioOutput"); } catch { }
        try { await JS.InvokeVoidAsync("nesInterop.setActiveClockId", string.Empty); } catch { }
    }

    /// <summary>The surface every IClock drives. Contract fixed by NesEmulator.IClockHost.</summary>
    private sealed class ClockHostFacade : IClockHost
    {
        private readonly Emulator _e;
        public ClockHostFacade(Emulator e) { _e = e; }

        public bool IsRunning => _e.IsRunning;

        public async ValueTask RequestStartJsLoopAsync()
        {
            _e._selfRef ??= DotNetObjectReference.Create(_e);
            try { await _e.JS.InvokeVoidAsync("nesInterop.startEmulationLoop", _e._selfRef!); } catch { }
        }

        public async ValueTask RequestStopJsLoopAsync()
        { try { await _e.JS.InvokeVoidAsync("nesInterop.stopEmulationLoop"); } catch { } }

        public void RunFrame() { _e.BuildFrame(); }

        public BrokenNes.Emulator.FramePayload RunFrameAndBuildPayload() => _e.BuildFrame();

        public async ValueTask PresentAsync(BrokenNes.Emulator.FramePayload payload)
        {
            try
            {
                await _e.JS.InvokeVoidAsync("nesInterop.presentFrame", CanvasId,
                    payload.Framebuffer, payload.Audio, payload.SampleRate);
            }
            catch { }
        }
    }

    // ---- The frame body -----------------------------------------------------

    private FramePayload BuildFrame()
    {
        var nes = _nes;
        if (nes == null || !IsRunning)
            return new FramePayload { Framebuffer = null, Audio = null, SampleRate = 0 };

        nes.SetInputs(_p1, _p2);
        nes.RunFrame();
        FrameCount++;

        float[] audio = nes.GetAudioBuffer();
        bool haveAudio = audio.Length > 0;

        _fpsFrames++;
        var now = DateTime.UtcNow;
        var elapsed = (now - _fpsMark).TotalSeconds;
        if (elapsed >= 0.5)
        {
            Fps = _fpsFrames / elapsed;
            _fpsFrames = 0;
            _fpsMark = now;
            OnStateChanged?.Invoke();
        }

        return new FramePayload
        {
            Framebuffer = nes.GetFrameBuffer(),
            Audio = haveAudio ? audio : null,
            SampleRate = haveAudio ? nes.GetAudioSampleRate() : 0
        };
    }

    // ---- JS -> C# -----------------------------------------------------------

    [JSInvokable] public Task<FramePayload> FrameTick() => Task.FromResult(BuildFrame());

    /// <summary>
    /// Diagnostic only - isolates pure emulation cost (SetInputs+RunFrame, no framebuffer/audio
    /// fetch, no interop marshaling of a payload back to JS) from the rest of FrameTick, so a
    /// single JS-side round-trip measurement of FrameTick can be decomposed into "emulation",
    /// "array fetch", and "interop marshaling" instead of guessing. Runs entirely server-side
    /// (on the WASM thread) in a loop, so its own dispatch overhead is paid once, not per frame.
    /// </summary>
    [JSInvokable]
    public double BenchEmulationOnlyMs(int frames)
    {
        var nes = _nes;
        if (nes == null || frames <= 0) return 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            nes.SetInputs(_p1, _p2);
            nes.RunFrame();
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / frames;
    }

    /// <summary>Diagnostic only - same as above, but also fetches the framebuffer/audio arrays
    /// (mirrors everything BuildFrame() does except the FramePayload allocation), to isolate
    /// array-fetch cost from interop marshaling cost.</summary>
    [JSInvokable]
    public double BenchEmulationPlusFetchMs(int frames)
    {
        var nes = _nes;
        if (nes == null || frames <= 0) return 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            nes.SetInputs(_p1, _p2);
            nes.RunFrame();
            _ = nes.GetFrameBuffer();
            _ = nes.GetAudioBuffer();
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / frames;
    }

    [JSInvokable]
    public void UpdateInput(bool[] state)
    {
        if (state == null) return;
        for (int i = 0; i < 8 && i < state.Length; i++) _p1[i] = state[i];
    }

    [JSInvokable]
    public void UpdateInputForPlayer(int player, bool[] state)
    {
        if (state == null) return;
        var target = player == 2 ? _p2 : _p1;
        for (int i = 0; i < 8 && i < state.Length; i++) target[i] = state[i];
    }

    [JSInvokable]
    public void JsVisibilityChanged(bool visible)
    { try { _activeClock?.OnVisibilityChanged(visible); } catch { } }

    public DotNetObjectReference<Emulator> SelfRef => _selfRef ??= DotNetObjectReference.Create(this);

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;
        try { await StopClockAsync(); } catch { }
        _selfRef?.Dispose();
        _selfRef = null;
    }
}
