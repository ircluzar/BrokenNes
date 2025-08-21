using System;
using System.Collections.Generic;
using System.Linq;
using BrokenNes.Models;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using BrokenNes.CorruptorModels;

namespace BrokenNes
{
    public partial class Emulator
    {
        public record BenchHistoryItem(string Id, DateTime TimestampUtc, string Rom, string CpuCore, string PpuCore, string ApuCore, string Display);
        public record BenchDiffRowView(string Name,double CurMs,double PrevMs,double DeltaMs,double DeltaPct,long ReadsDelta,long WritesDelta,long ApuDelta,long OamDelta);
        public record BenchTimelinePoint(DateTime When,double MsPerIter,long Reads,long Writes,long Apu,long Oam,string CpuCore,string PpuCore,string ApuCore,string Rom);

        public bool BenchRunning => benchRunning;
        public bool BenchModalOpen => benchModalOpen;
        public string BenchResultsText => benchResultsText;
        public int BenchWeight { get => benchWeight; set => benchWeight = Math.Clamp(value,1,9); }
        public bool BenchAutoLoadState { get => benchAutoLoadState; set => benchAutoLoadState = value; }
        public bool BenchSimple5x { get => benchSimple5x; set => benchSimple5x = value; }
        public bool CompareModalOpen => compareModalOpen;
        public bool CompareNormalize { get => compareNormalize; set { compareNormalize = value; StateHasChanged(); } }
        public string? CurrentBenchHistoryId => currentBenchHistoryId;
        public IEnumerable<BenchHistoryItem> BenchHistory => benchHistory.Select(e=>new BenchHistoryItem(e.Id,e.TimestampUtc,e.Rom,e.CpuCore,e.PpuCore,e.ApuCore,e.Display));
        public IEnumerable<string> VisibleTargets => visibleTargets;        
        public IEnumerable<string> AllTargets => allTargets;
        public IEnumerable<BenchDiffRowView> RecentDiffRows => recentDiffRows.Select(r=>new BenchDiffRowView(r.Name,r.CurMs,r.PrevMs,r.DeltaMs,r.DeltaPct,r.ReadsDelta,r.WritesDelta,r.ApuDelta,r.OamDelta));
        public IReadOnlyDictionary<string,List<BenchTimelinePoint>> TimelineSeries => timelineSeries.ToDictionary(k=>k.Key,v=>v.Value.Select(tp=>new BenchTimelinePoint(tp.When,tp.MsPerIter,tp.Reads,tp.Writes,tp.Apu,tp.Oam,tp.CpuCore,tp.PpuCore,tp.ApuCore,tp.Rom)).ToList());
        public IReadOnlyList<DateTime> TimelineOrder => timelineOrder;

        // Additional benchmark UI state exposure (for phased Nes.razor refactor)
        public bool DiffAnimating => diffAnimating;
        public string? HighlightMetricName => highlightMetricName;
        public int? HoverIndex => hoverIndex;
        public string? HoverTarget => hoverTarget;
        public (string Target,string TimeLabel,double MsPerIter,long Reads,long Writes,long ApuCycles,long OamWrites,string CpuCore,string PpuCore,string ApuCore,string Rom)? HoverPointTooltipData
            => hoverPointTooltip == null ? null : (hoverPointTooltip.Target, hoverPointTooltip.TimeLabel, hoverPointTooltip.MsPerIter, hoverPointTooltip.Reads, hoverPointTooltip.Writes, hoverPointTooltip.ApuCycles, hoverPointTooltip.OamWrites, hoverPointTooltip.CpuCore, hoverPointTooltip.PpuCore, hoverPointTooltip.ApuCore, hoverPointTooltip.Rom);
        public string? EditingBenchRomId => editingBenchRomId;
        public string EditingBenchRomValue => editingBenchRomValue;
        public void StartBenchRomEditPublic(string id)
        {
            var e = benchHistory.FirstOrDefault(x=>x.Id==id); if (e==null) return; StartBenchRomEdit(e);
        }
        public Task CommitBenchRomEditPublic(string id) => CommitBenchRomEdit(id);
        public void CancelBenchRomEditPublic() { editingBenchRomId=null; editingBenchRomValue=string.Empty; StateHasChanged(); }

        public Task RunBenchmarksAsync() => RunBenchmarks();
        public Task RunBenchmarks5xAsync() => RunBenchmarks5x();
        public void OpenBenchmarks() => OpenBenchModal();
        public void CloseBenchmarks() => CloseBenchModal();
        public void OpenComparison() => OpenCompareModal();
        public void CloseComparison() => CloseCompareModal();
        public void ToggleBenchAutoLoadState() => ToggleBenchAutoLoad();
        public void ToggleBenchSimpleMode() => ToggleBenchSimple5x();
        public Task CopyBenchResultsAsync() => CopyBenchResults();
        public Task ClearBenchHistoryAsync() => ClearBenchHistory();
        public Task DeleteBenchEntryAsync(string id) => DeleteBenchEntry(id);
        public void ShowHistoryEntryToggle(string id) => ShowHistoryEntry(id);
        public void ToggleTargetVisibility(string target) => ToggleTarget(target);
        public Task PlayDiffAnimationAsync() => PlayDiffAnimation();
        public void CancelDiffAnimation() => CancelDiffAnim();

        public bool GhHasSelectedBase => corruptor.GhHasSelectedBase;
        public Task GhCorruptAndStashAsync() => GhCorruptAndStash();
        public Task GhReplayEntryAsync(HarvestEntry e, bool fromStockpile) => GhReplayEntry(e, fromStockpile);
        public Task GhExportStockpileAsync() => GhExportStockpile();
        public Task GhImportStockpileAsync(ChangeEventArgs e) => GhImportStockpile(e);
        public void GhAddBase() => GhAddBaseState();
        public void GhOnBaseChangedPublic(ChangeEventArgs e) => GhOnBaseChanged(e);
        public void GhLoadSelected() => GhLoadSelectedBase();
        public void GhDeleteSelected() => GhDeleteSelectedBase();
        public void GhClearStashPublic() => GhClearStash();
        public void GhPromote(HarvestEntry e) => GhPromoteEntry(e);
        public void GhDeleteStashPublic(string id) => GhDeleteStash(id);
        public void GhDeleteStockPublic(string id) => GhDeleteStock(id);
        public void GhBeginRenamePublic(HarvestEntry e) => GhBeginRename(e);
        public void GhCancelRenamePublic() => GhCancelRename();
        public void GhRenameChangePublic(ChangeEventArgs e) => GhRenameChange(e);
        public void GhCommitRenamePublic(string id) => GhCommitRename(id);
        public bool GhIsRenamingPublic(string id) => GhIsRenaming(id);
        public string GhFindBaseNamePublic(string id) => GhFindBaseName(id);

        public bool AutoCorrupt => corruptor.AutoCorrupt;
        public int CorruptIntensity { get => corruptor.CorruptIntensity; set => corruptor.CorruptIntensity = Math.Clamp(value,1,65535); }
        public string BlastType { get => corruptor.BlastType; set => corruptor.BlastType = value; }
        public string LastBlastInfo => corruptor.LastBlastInfo;
        public void ToggleAutoCorrupt() { corruptor.AutoCorrupt = !corruptor.AutoCorrupt; corruptor.LastBlastInfo = corruptor.AutoCorrupt ? "Auto-corrupt enabled" : "Auto-corrupt disabled"; }
        public Task BlastAsync() => Blast();
    public void LetItRipPublic() { corruptor.LetItRip(); StateHasChanged(); }
        public void SetBlastTypePublic(string t) { if(!string.IsNullOrWhiteSpace(t)) corruptor.BlastType = t.Trim().ToUpperInvariant(); }

    // --- ROM / Core / Shader public surface (new) ---
    public IEnumerable<RomOption> RomOptions => Controller.RomOptions;
    public string RomSearch { get => Controller.RomSearch; set { Controller.RomSearch = value; StateHasChanged(); } }
    public string ActiveShaderKey => Controller.ActiveShaderKey;
    public IEnumerable<ShaderOption> ShaderOptions => Controller.ShaderOptions;
    public IEnumerable<string> CpuCoreOptions => Controller.CpuCoreOptions;
    public IEnumerable<string> PpuCoreOptions => Controller.PpuCoreOptions;
    public IEnumerable<string> ApuCoreOptions => Controller.ApuCoreOptions;
    public string CpuCoreSel => Controller.CpuCoreSel;
    public string PpuCoreSel => Controller.PpuCoreSel;
    public string ApuCoreSel => Controller.ApuCoreSel;
    public bool IsFullscreen => Controller.IsFullscreen;
    public double EmuScale => Controller.EmuScale;
    public int FrameCount => Controller.FrameCount;
    // Fps & CurrentRomName already exposed in core partial; avoid duplicate definitions
    public int LastLoadedRomSize => Controller.LastLoadedRomSize;
    public bool IsRunningPublic => Controller.IsRunning;
    public string ErrorMessage => Controller.ErrorMessage;

    public async Task SetShaderPublic(string key){ if(string.IsNullOrWhiteSpace(key)) return; await SetShader(key); try { await JS.InvokeVoidAsync("nesInterop.idbSetItem","pref_shader", key); } catch {} StateHasChanged(); }
    public async Task SetCpuCorePublic(string id){ if (string.IsNullOrWhiteSpace(id)) return; Controller.CpuCoreSel = id; try { await JS.InvokeVoidAsync("nesInterop.idbSetItem","pref_cpuCore", id); } catch {} ApplySelectedCores(); StateHasChanged(); }
    public async Task SetPpuCorePublic(string id){ if (string.IsNullOrWhiteSpace(id)) return; Controller.PpuCoreSel = id; try { await JS.InvokeVoidAsync("nesInterop.idbSetItem","pref_ppuCore", id); } catch {} ApplySelectedCores(); StateHasChanged(); }
    public async Task SetApuCorePublic(string id){ if (string.IsNullOrWhiteSpace(id)) return; Controller.ApuCoreSel = id; try { await JS.InvokeVoidAsync("nesInterop.idbSetItem","pref_apuCore", id); } catch {} ApplySelectedCores(); StateHasChanged(); }
    public void SetScalePublic(double scale){ Controller.EmuScale = scale; StateHasChanged(); }
    public async Task ToggleFullscreenPublic(){ try { var newState = await JS.InvokeAsync<bool>("nesInterop.toggleFullscreen"); Controller.IsFullscreen = newState; StateHasChanged(); } catch {} }
    public async Task ReloadCurrentRomPublic(){ await LoadRomFromServer(); }

    // Simple hard reset: pause, reload current ROM, reset counters, resume if was running
    public async Task HardResetAsync(){ bool wasRunning = Controller.IsRunning; if (wasRunning) await PauseEmulation(); Controller.FrameCount=0; Controller.LastFrameCount=0; Controller.Fps=0; await LoadRomFromServer(); if (wasRunning) await StartEmulation(); }

    // Event scheduler toggle
    public bool EventSchedulerOn { get => eventSchedulerOn; set { eventSchedulerOn = value; if(nes!=null) nes.EnableEventScheduler = value; try { JS.InvokeVoidAsync("nesInterop.idbSetItem","pref_eventScheduler", value?"1":"0"); } catch {} StateHasChanged(); } }

    // SoundFont public projections
    public bool SoundFontMode => soundFontMode;
    public bool SampleFont => sampleFont;
    public bool SoundFontLayering => soundFontLayering;
    public bool SfDevLogging => sfDevLogging;
    public bool SfOverlay => sfOverlay;
    public string ActiveSfCoreDisplay => activeSfCoreDisplay;
    public void ToggleSoundFontModePublic(){ try { if (nes==null) return; if(!soundFontMode){ soundFontMode = nes.EnableSoundFontMode(true,(ch,prog,midi,vel,on,_)=>{ try { JS.InvokeVoidAsync("nesInterop.noteEvent", ch,prog,midi,vel,on); } catch {} }); } else { nes.EnableSoundFontMode(false,null); soundFontMode=false; } _ = UpdateActiveSoundFontCoreAsync(); StateHasChanged(); } catch {} }
    public void ToggleSampleFontPublic(){ sampleFont = !sampleFont; try { JS.InvokeVoidAsync("eval", $"window.nesSoundFont && nesSoundFont.setPreferSampleBased && nesSoundFont.setPreferSampleBased({sampleFont.ToString().ToLowerInvariant()});"); } catch {} StateHasChanged(); }
    public async Task ToggleSoundFontLayeringPublic(){ soundFontLayering = !soundFontLayering; try { await JS.InvokeVoidAsync("nesInterop.setSoundFontLayering", soundFontLayering); } catch {} StateHasChanged(); }
    public void ToggleSfDevLoggingPublic(){ sfDevLogging = !sfDevLogging; try { JS.InvokeVoidAsync("nesInterop.enableSoundFontDevLogging", sfDevLogging); } catch {} StateHasChanged(); }
    public void ToggleSfOverlayPublic(){ sfOverlay = !sfOverlay; try { if (sfOverlay) JS.InvokeVoidAsync("nesInterop.startSoundFontAudioOverlay"); else JS.InvokeVoidAsync("nesInterop.stopSoundFontAudioOverlay"); } catch {} StateHasChanged(); }
    public async Task FlushSoundFontPublic(){ try { await JS.InvokeVoidAsync("nesInterop.flushSoundFont"); nes?.FlushSoundFont(); } catch {} }
    public async Task ShowSfDebugPublic(){ try { var rep = await JS.InvokeAsync<object>("nesInterop.debugReport"); Logger.LogInformation($"SF Debug: {System.Text.Json.JsonSerializer.Serialize(rep)}"); } catch {} }

        public Task SaveStateAsyncPublic() => SaveStateAsync();
        public Task LoadStateAsyncPublic() => LoadStateAsync();
        public Task DumpStateAsyncPublic() => DumpStateAsync();
        public string DebugDumpText => debugDump;
    public Task ResetAsyncFacade() => ResetAsyncPublic();

        // --- ROM management public wrappers ---
        // Load the currently selected ROM (uploaded or built-in) using controller plumbing and present a warm-up frame.
        public async Task LoadSelectedRomPublic()
        {
            await Controller.LoadSelectedRom(
                async fn => await Controller.LoadRomFromWwwroot(fn, f => Http.GetByteArrayAsync(f), s => Logger.LogInformation(s), s => Logger.LogError(new Exception(s), s)),
                s => Status.Set(s),
                () => StateHasChanged(),
                async _ => await JS.InvokeVoidAsync("nesInterop.drawFrame", "nes-canvas", Controller.framebuffer),
                async () => { BuildMemoryDomains(); await Task.CompletedTask; },
                () => PauseAsync(),
                () => StartAsync()
            );
        }

        // Delete a specific uploaded ROM and reload a fallback if necessary.
        public async Task DeleteRomPublic(string key)
        {
            await Controller.DeleteRom(
                key,
                s => Status.Set(s),
                () => StateHasChanged(),
                async removed => await JS.InvokeVoidAsync("nesInterop.removeStoredRom", removed),
                async rk => { Controller.RomFileName = rk; await LoadSelectedRomPublic(); },
                () => Controller.GetDefaultBuiltInRomKey()
            );
        }

        // Clear all uploaded ROMs and reload fallback if needed.
        public async Task ClearAllUploadedPublic()
        {
            await Controller.ClearAllUploaded(
                s => Status.Set(s),
                () => StateHasChanged(),
                async k => await JS.InvokeVoidAsync("nesInterop.removeStoredRom", k),
                async rk => { Controller.RomFileName = rk; await LoadSelectedRomPublic(); },
                () => Controller.GetDefaultBuiltInRomKey(),
                () => IsBuiltInSelected
            );
        }

        // Import ROMs selected in the hidden <input type="file"> and auto-load the last one.
        public async Task ImportRomsFromInputAsync(ElementReference fileInput)
        {
            await Controller.LoadRomUpload(
                async () => await JS.InvokeAsync<UploadedRom[]>("nesInterop.readSelectedRoms", fileInput),
                s => Status.Set(s),
                () => StateHasChanged(),
                async romKey => { Controller.RomFileName = romKey; await LoadSelectedRomPublic(); }
            );
        }

        // Open the hidden import dialog in the UI by clicking the input element via JS.
        public async Task TriggerRomImportDialogPublic()
        {
            try { await JS.InvokeVoidAsync("eval", "document.getElementById('rom-upload')?.click()"); } catch { }
        }

    // Expose memory domain rebuild for UI triggers (avoids duplicated logic in Razor)
    public void RebuildMemoryDomainsPublic() => BuildMemoryDomains();
    }
}
