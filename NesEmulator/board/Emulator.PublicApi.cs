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
        public string BenchBaselineRomName { get => benchBaselineRomName ?? ""; set => benchBaselineRomName = value; }
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
        public double HoverTooltipLeftPx => hoverTooltipLeftPx;
        public double HoverTooltipTopPx => hoverTooltipTopPx;
        public (string Target,string TimeLabel,double MsPerIter,long Reads,long Writes,long ApuCycles,long OamWrites,string CpuCore,string PpuCore,string ApuCore,string Rom)? HoverPointTooltipData
            => hoverPointTooltip == null ? null : (hoverPointTooltip.Target, hoverPointTooltip.TimeLabel, hoverPointTooltip.MsPerIter, hoverPointTooltip.Reads, hoverPointTooltip.Writes, hoverPointTooltip.ApuCycles, hoverPointTooltip.OamWrites, hoverPointTooltip.CpuCore, hoverPointTooltip.PpuCore, hoverPointTooltip.ApuCore, hoverPointTooltip.Rom);
        public string? EditingBenchRomId => editingBenchRomId;
        public string EditingBenchRomValue { get => editingBenchRomValue; set { editingBenchRomValue = value; StateHasChanged(); } }
        public void StartBenchRomEditPublic(string id)
        {
            var e = benchHistory.FirstOrDefault(x=>x.Id==id); if (e==null) return; StartBenchRomEdit(e);
        }
        public Task CommitBenchRomEditPublic(string id) => CommitBenchRomEdit(id);
        public void CancelBenchRomEditPublic() { editingBenchRomId=null; editingBenchRomValue=string.Empty; StateHasChanged(); }
        public async Task HandleBenchRomEditKeyPublic(KeyboardEventArgs e, string id)
        {
            if (e.Key=="Enter") await CommitBenchRomEdit(id); 
            else if (e.Key=="Escape") { editingBenchRomId=null; editingBenchRomValue=string.Empty; StateHasChanged(); }
        }



        // Touch controller field exposure
        public bool TouchControllerInitialized => touchControllerInitialized;
        public void SetTouchControllerInitialized(bool value) { touchControllerInitialized = value; StateHasChanged(); }

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
        public void OnTimelineMouseLeave() => TimelineMouseLeave();
        public void OnTimelineMouseMove(MouseEventArgs e) => TimelineMouseMove(e);
        public string GetColorForTarget(string target) => ColorForTarget(target);

        // Add setter methods for benchmark state that Nes.razor needs to modify
        public void SetBenchModalOpen(bool value) { benchModalOpen = value; StateHasChanged(); }
        public void SetBenchRunning(bool value) { benchRunning = value; StateHasChanged(); }
        public void SetBenchResultsText(string value) { benchResultsText = value; StateHasChanged(); }
        public void SetCurrentBenchHistoryId(string? value) { currentBenchHistoryId = value; StateHasChanged(); }
        public void SetCompareModalOpen(bool value) { compareModalOpen = value; StateHasChanged(); }

        // Crash behavior management
        public void SetCrashBehavior(string crashBehavior)
        {
            Corruptor.CrashBehavior = crashBehavior;
            try
            {
                if (nesController.nes != null)
                {
                    if (string.Equals(crashBehavior, "IgnoreErrors", StringComparison.OrdinalIgnoreCase))
                    {
                        nesController.nes.SetCrashBehavior(NesEmulator.NES.CrashBehavior.IgnoreErrors);
                    }
                    else if (string.Equals(crashBehavior, "ImagineFix", StringComparison.OrdinalIgnoreCase))
                    {
                        nesController.nes.SetCrashBehavior(NesEmulator.NES.CrashBehavior.ImagineFix);
                    }
                    else
                    {
                        nesController.nes.SetCrashBehavior(NesEmulator.NES.CrashBehavior.RedScreen);
                    }
                }
            }
            catch { }
        }

        // Helper to apply the currently selected crash behavior to the active NES
        private void ApplySelectedCrashBehavior()
        {
            try
            {
                if (nes == null) return;
                var mode = Corruptor.CrashBehavior ?? "IgnoreErrors";
                if (string.Equals(mode, "IgnoreErrors", StringComparison.OrdinalIgnoreCase))
                    nes.SetCrashBehavior(NesEmulator.NES.CrashBehavior.IgnoreErrors);
                else if (string.Equals(mode, "ImagineFix", StringComparison.OrdinalIgnoreCase))
                    nes.SetCrashBehavior(NesEmulator.NES.CrashBehavior.ImagineFix);
                else
                    nes.SetCrashBehavior(NesEmulator.NES.CrashBehavior.RedScreen);
                // Apply stubborn toggle whenever crash behavior is applied/changed
                try { nes.SetStubbornFixEnabled(Corruptor.StubbornMode); } catch { }
            }
            catch { }
        }

        // Public toggle for Stubborn mode (Imagine Fix periodic retries)
        public void SetStubbornMode(bool on)
        {
            try { Corruptor.StubbornMode = on; nes?.SetStubbornFixEnabled(on); } catch { }
        }
        
        // Helper method to create a bench history entry from the outside
        public void AddBenchHistoryEntry(string rom, string display)
        {
            if (nes == null) return;
            var entry = new BenchHistoryEntry {
                TimestampUtc = DateTime.UtcNow,
                Rom = rom,
                CpuCore = nes.GetCpuCoreId(),
                PpuCore = nes.GetPpuCoreId(),
                ApuCore = nes.GetApuCoreId(),
                Display = display
            };
            benchHistory.Insert(0, entry);
            currentBenchHistoryId = entry.Id;
            StateHasChanged();
        }
        
        public void ClearBenchHistoryItems() { benchHistory.Clear(); currentBenchHistoryId = null; StateHasChanged(); }
        public void RemoveBenchHistoryItem(string id) { benchHistory.RemoveAll(e => e.Id == id); if (currentBenchHistoryId == id) currentBenchHistoryId = benchHistory.FirstOrDefault()?.Id; StateHasChanged(); }
        public void SetBenchHistoryItems(List<BrokenNes.Models.BenchHistoryEntry> items) 
        { 
            benchHistory.Clear(); 
            benchHistory.AddRange(items.Select(item => new BenchHistoryEntry 
            {
                Id = item.Id,
                TimestampUtc = item.TimestampUtc,
                Rom = item.Rom,
                CpuCore = item.CpuCore,
                PpuCore = item.PpuCore,
                ApuCore = item.ApuCore,
                Display = item.Display
            })); 
            StateHasChanged(); 
        }
        public void BuildComparisonDatasetsPublic() => BuildComparisonDatasets();

        public bool GhHasSelectedBase => corruptor.GhHasSelectedBase;
        public Task GhCorruptAndStashAsync() => GhCorruptAndStash();
        public Task GhReplayEntryAsync(HarvestEntry e, bool fromStockpile) => GhReplayEntry(e, fromStockpile);
        public Task GhExportStockpileAsync() => GhExportStockpile();
        public void GhAddBase() => GhAddBaseState();
        public void GhOnBaseChangedPublic(ChangeEventArgs e) => GhOnBaseChanged(e);
        public void GhLoadSelected() => GhLoadSelectedBase();
        public void GhDeleteSelected() => GhDeleteSelectedBase();
        public void GhClearStashPublic() => GhClearStash();
        public void GhPromote(HarvestEntry e) => GhPromoteEntry(e);

        // Expose missing GH methods from GlitchHarvester.cs
        public string GhFindBaseName(string id) => corruptor.GhBaseStates.FirstOrDefault(b => b.Id == id)?.Name ?? "?";
        public void GhDeleteStash(string id) { corruptor.GhDeleteStash(id); }
        public void GhDeleteStock(string id) { corruptor.GhDeleteStock(id); }
        public bool GhIsRenaming(string id) => corruptor.GhRenamingId == id;
        public void GhBeginRename(HarvestEntry e) { corruptor.GhBeginRename(e); }
        public void GhCancelRename() { corruptor.GhCancelRename(); }
        public void GhRenameChange(ChangeEventArgs e) { if (e.Value is string v) corruptor.GhRenameText = v; }
        public void GhCommitRename(string id) { corruptor.GhCommitRename(id); }
        public async Task GhImportStockpile(ChangeEventArgs e)
        {
            try
            {
                await JS.InvokeVoidAsync("eval", $"(async ()=>{{ const f = event.target.files?.[0]; if (!f) return; const t = await f.text(); window.ghStockpileData = t; }})()");
                var json = await JS.InvokeAsync<string>("eval", "window.ghStockpileData || ''");
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var imported = System.Text.Json.JsonSerializer.Deserialize<List<CorruptorModels.HarvestEntry>>(json);
                    if (imported != null)
                    {
                        foreach (var entry in imported) corruptor.GhStockpile.Add(entry);
                        Status.Set($"Imported {imported.Count} stockpile entries");
                    }
                }
            }
            catch (Exception ex) { Status.Set($"Import failed: {ex.Message}"); }
            StateHasChanged();
        }

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
    public IEnumerable<RomOption> FilteredRomOptionsPublic => FilteredRomOptions;
    public string RomSearch { get => Controller.RomSearch; set { Controller.RomSearch = value; StateHasChanged(); } }
    public string ActiveShaderKey => Controller.ActiveShaderKey;
    public IEnumerable<ShaderOption> ShaderOptions => Controller.ShaderOptions;
    public IEnumerable<string> CpuCoreOptions => Controller.CpuCoreOptions;
    public IEnumerable<string> PpuCoreOptions => Controller.PpuCoreOptions;
    public IEnumerable<string> ApuCoreOptions => Controller.ApuCoreOptions;
    public IEnumerable<string> ClockCoreOptions => Controller.ClockCoreOptions;
    public string CpuCoreSel => Controller.CpuCoreSel;
    public string PpuCoreSel => Controller.PpuCoreSel;
    public string ApuCoreSel => Controller.ApuCoreSel;
    public string ClockCoreSel => Controller.ClockCoreSel;
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
    public async Task SetClockCorePublic(string id){
        if (string.IsNullOrWhiteSpace(id)) return; if (!Controller.ClockCoreOptions.Contains(id)) return;
        bool wasRunning = Controller.IsRunning;
        if (wasRunning) await PauseEmulation();
        // Guardrails: flush audio output before switching drivers to avoid drift
        try { await JS.InvokeVoidAsync("nesInterop.flushAudioOutput"); } catch {}
        Controller.ClockCoreSel = id;
        try { await JS.InvokeVoidAsync("nesInterop.idbSetItem","pref_clockCore", id); } catch {}
        if (wasRunning) await StartEmulation();
        // Ensure visibility events wired (CLR benefits; FMC harmless)
        try { if (_selfRef != null) await JS.InvokeVoidAsync("nesInterop.registerVisibility", _selfRef); } catch {}
        StateHasChanged();
    }
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
            // After successful ROM load, ensure a corresponding Game entry exists in continue-db
            try { await EnsureGameInContinueDbAsync(Controller.CurrentRomName); } catch { }
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

        // ROM selection and management methods for UI
        public async Task RomSelectionChangedPublic(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                await Controller.RomSelectionChanged(value, async rk => {
                    Controller.RomFileName = rk;
                    await LoadSelectedRomPublic();
                });
            }
        }

        public async Task LoadRomEntryPublic(string key)
        {
            Controller.RomFileName = key;
            await LoadSelectedRomPublic();
        }

        public async Task OnRomRowClickedPublic(RomOption opt)
        {
            if (opt.Key != Controller.CurrentRomName)
            {
                await Controller.OnRomRowClicked(opt, async k => {
                    Controller.RomFileName = k;
                    await LoadSelectedRomPublic();
                });
            }
        }

        public string GetDefaultBuiltInRomKeyPublic() => Controller.GetDefaultBuiltInRomKey();

        // Open the hidden import dialog in the UI by clicking the input element via JS.
        public async Task TriggerRomImportDialogPublic()
        {
            try { await JS.InvokeVoidAsync("eval", "document.getElementById('rom-upload')?.click()"); } catch { }
        }

                // Ensure a Game record exists in the global continue-db for the currently loaded ROM.
                // Minimal schema: { id, title, system, romKey, builtIn, size, createdAt }
                private async Task EnsureGameInContinueDbAsync(string romKey)
                {
                        try
                        {
                                if (string.IsNullOrWhiteSpace(romKey)) return;
                                var id = romKey; // Temporary: use ROM filename as id; can be upgraded to a hash-based ID later
                                var title = System.IO.Path.GetFileNameWithoutExtension(romKey) ?? romKey;
                                bool builtIn = Controller.RomOptions.FirstOrDefault(o => o.Key == romKey)?.BuiltIn ?? true;
                                int size = Controller.LastLoadedRomSize;
                                var rec = new {
                                        id,
                                        title,
                                        system = "nes",
                                        romKey,
                                        builtIn,
                                        size,
                                        createdAt = DateTime.UtcNow.ToString("o")
                                };
                                var recJson = System.Text.Json.JsonSerializer.Serialize(rec);
                                var idJson = System.Text.Json.JsonSerializer.Serialize(id);
                                // Use a small async IIFE to interact with window.continueDb
                                var script = $@"(async()=>{{
    try {{
        if (!window.continueDb) return; 
        await window.continueDb.open();
        const id = {idJson};
        let g = await window.continueDb.get('games', id);
        if (!g) {{
            const rec = JSON.parse('{recJson.Replace("\\", "\\\\").Replace("'", "\\'")}');
            await window.continueDb.put('games', rec);
        }}
    }} catch(e) {{ console.warn('ensureGameInContinueDb failed', e); }}
}})()";
                                await JS.InvokeVoidAsync("eval", script);
                        }
                        catch { }
                }

        // === JSInvokable methods for mobile fullscreen bottom bar and drag-drop ===
        [JSInvokable]
        public Task JsSaveState() => SaveStateAsyncPublic();
        
        [JSInvokable]
        public Task JsLoadState() => LoadStateAsyncPublic();
        
        [JSInvokable]
        public Task JsResetGame() => ResetAsyncFacade();
        
        [JSInvokable]
        public void JsExitFullscreen()
        {
            Controller.IsFullscreen = false;
            StateHasChanged();
        }

        [JSInvokable]
        public async Task OnRomsDropped(UploadedRom[] roms)
        {
            if (roms == null || roms.Length == 0) return;
            int added = 0;
            foreach (var f in roms)
            {
                if (string.IsNullOrWhiteSpace(f.name) || string.IsNullOrWhiteSpace(f.base64)) continue;
                try
                {
                    var data = Convert.FromBase64String(f.base64);
                    if (data.Length == 0) continue;
                    Controller.UploadedRoms[f.name] = data;
                    if (!Controller.RomOptions.Any(o => o.Key == f.name))
                    {
                        Controller.RomOptions.Add(new RomOption { Key = f.name, Label = f.name + " (uploaded)", BuiltIn = false });
                    }
                    added++;
                }
                catch { }
            }
            Controller.RomFileName = roms.Last().name;
            await LoadSelectedRomPublic();
            Status.Set($"Dropped {added} ROM(s).");
            if (!string.Equals(Controller.CurrentRomName, "test.nes", StringComparison.OrdinalIgnoreCase))
            {
                try { await JS.InvokeVoidAsync("nesInterop.focusCorruptorPanel"); } catch {}
            }
        }

    // Expose memory domain rebuild for UI triggers (avoids duplicated logic in Razor)
    public void RebuildMemoryDomainsPublic() => BuildMemoryDomains();

        // === Visibility forwarding for CLR clock throttling ===
        [JSInvokable]
        public void JsVisibilityChanged(bool visible)
        {
            try { _activeClock?.OnVisibilityChanged(visible); } catch {}
        }
    }
}
