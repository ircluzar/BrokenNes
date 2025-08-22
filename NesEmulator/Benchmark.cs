using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components.Web; // for MouseEventArgs / KeyboardEventArgs

namespace BrokenNes
{
    public partial class Emulator
    {
        // ================= Benchmark Subsystem (migrated) =================
        private bool benchRunning = false; private string benchResultsText = string.Empty; private bool benchModalOpen = false; private int benchWeight = 1; private bool benchAutoLoadState = true; private bool benchSimple5x = true; private string benchBaselineRomName = string.Empty;
        public class BenchHistoryEntry { public string Id {get;set;}=Guid.NewGuid().ToString(); public DateTime TimestampUtc {get;set;}=DateTime.UtcNow; public string Rom {get;set;}=""; public string CpuCore {get;set;}=""; public string PpuCore {get;set;}=""; public string ApuCore {get;set;}=""; public string Display {get;set;}=""; }
        private List<BenchHistoryEntry> benchHistory = new();
        private string? currentBenchHistoryId = null;
        public const string BenchHistoryKey = "bench_history_v1";
        private bool compareModalOpen = false;
        private const int MaxTimelineTrendPoints = 69;
        private HashSet<string> visibleTargets = new();
        private List<string> allTargets = new();
        private List<DateTime> timelineOrder = new();
        private record TimelinePoint(DateTime When,double MsPerIter,long Reads,long Writes,long Apu,long Oam,string CpuCore,string PpuCore,string ApuCore,string Rom);
        private Dictionary<string,List<TimelinePoint>> timelineSeries = new();
        private record DiffRow(string Name,double CurMs,double PrevMs,double DeltaMs,double DeltaPct,long ReadsDelta,long WritesDelta,long ApuDelta,long OamDelta);
        private List<DiffRow> recentDiffRows = new();
        private bool diffAnimating = false; private string? highlightMetricName = null; private CancellationTokenSource? diffAnimCts;
        private bool compareNormalize = true;
        private int? hoverIndex = null; private string? hoverTarget = null; private HoverTooltip? hoverPointTooltip = null; private double hoverTooltipLeftPx=0; private double hoverTooltipTopPx=0; private record HoverTooltip(string Target,string TimeLabel,double MsPerIter,long Reads,long Writes,long ApuCycles,long OamWrites,string CpuCore,string PpuCore,string ApuCore,string Rom);
        private string? editingBenchRomId = null; private string editingBenchRomValue = string.Empty;

        private async Task PersistBenchHistory()
        {
            try { var payload = System.Text.Json.JsonSerializer.Serialize(benchHistory); await JS.InvokeVoidAsync("nesInterop.idbSetItem", BenchHistoryKey, payload); } catch {}
        }

        private async Task LoadBenchHistory()
        {
            try
            {
                var json = await JS.InvokeAsync<string>("nesInterop.idbGetItem", BenchHistoryKey);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var list = System.Text.Json.JsonSerializer.Deserialize<List<BenchHistoryEntry>>(json);
                    if (list != null) benchHistory = list.OrderByDescending(e=>e.TimestampUtc).ToList();
                }
            }
            catch { }
        }

        // =============== Benchmark Execution & Modal Control (migrated) ===============
        private void OpenBenchModal(){ benchModalOpen = true; StateHasChanged(); }
        private void CloseBenchModal(){ benchModalOpen = false; }
        private void ToggleBenchAutoLoad(){ benchAutoLoadState = !benchAutoLoadState; }
        private void ToggleBenchSimple5x(){ benchSimple5x = !benchSimple5x; }

        private async Task RunBenchmarks()
        {
            if (nes == null || benchRunning) { benchModalOpen = true; return; }
            benchModalOpen = true; benchRunning = true; benchResultsText = string.Empty; benchBaselineRomName = string.Empty; StateHasChanged();
            try {
                bool wasRunning = nesController.IsRunning; // pause emulator for deterministic timing
                if (wasRunning) await PauseEmulation();
                if (benchAutoLoadState) await TryLoadBaselineStateForBenchmarks();
                int w = benchWeight; if (w<1) w=1; if (w>9) w=9; benchWeight = w;
                var results = nes.RunBenchmarks(w);
                benchResultsText = nes.FormatBenchmarksForDisplay(results);
                var displayRom = string.IsNullOrWhiteSpace(benchBaselineRomName) ? nesController.CurrentRomName : benchBaselineRomName;
                benchResultsText = $"ROM: {displayRom}\n" + benchResultsText;
                var entry = new BenchHistoryEntry {
                    TimestampUtc = DateTime.UtcNow,
                    Rom = displayRom,
                    CpuCore = nes.GetCpuCoreId(),
                    PpuCore = nes.GetPpuCoreId(),
                    ApuCore = nes.GetApuCoreId(),
                    Display = benchResultsText
                };
                benchHistory.Insert(0, entry);
                currentBenchHistoryId = entry.Id;
                await PersistBenchHistory();
                if (wasRunning) await StartEmulation();
            } catch (Exception ex) { benchResultsText = "Benchmark error: "+ex.Message; }
            finally { benchRunning = false; StateHasChanged(); }
        }

        private async Task RunBenchmarks5x()
        {
            if (nes == null || benchRunning) { benchModalOpen = true; return; }
            benchModalOpen = true; benchRunning = true; benchResultsText = string.Empty; benchBaselineRomName = string.Empty; StateHasChanged();
            bool wasRunning = nesController.IsRunning;
            try {
                if (wasRunning) await PauseEmulation();
                int w = benchWeight; if (w<1) w=1; if (w>9) w=9; benchWeight = w;
                const int passes = 5;
                var passResults = new List<IReadOnlyList<NesEmulator.NES.BenchResult>>();
                for (int i=1;i<=passes;i++)
                {
                    if (benchAutoLoadState) await TryLoadBaselineStateForBenchmarks();
                    var res = nes.RunBenchmarks(w);
                    passResults.Add(res);
                    benchResultsText = $"Running pass {i}/{passes}..."; StateHasChanged();
                }
                var averaged = new List<NesEmulator.NES.BenchResult>();
                if (passResults.Count>0)
                {
                    var first = passResults[0];
                    for (int idx=0; idx<first.Count; idx++)
                    {
                        var name = first[idx].Name; int iters = first[idx].Iterations;
                        double msTotalAvg = passResults.Average(p=>p[idx].MsTotal);
                        double msPerIterAvg = passResults.Average(p=>p[idx].MsPerIter);
                        long cpuReadsAvg = (long)passResults.Average(p=>p[idx].CpuReads);
                        long cpuWritesAvg = (long)passResults.Average(p=>p[idx].CpuWrites);
                        long apuCyclesAvg = (long)passResults.Average(p=>p[idx].ApuCycles);
                        long oamDmaWritesAvg = (long)passResults.Average(p=>p[idx].OamDmaWrites);
                        long batchFlushesAvg = (long)passResults.Average(p=>p[idx].BatchFlushes);
                        averaged.Add(new NesEmulator.NES.BenchResult(name, iters, msTotalAvg, msPerIterAvg, cpuReadsAvg, cpuWritesAvg, apuCyclesAvg, oamDmaWritesAvg, batchFlushesAvg));
                    }
                }
                var sb = new System.Text.StringBuilder();
                var displayRom = string.IsNullOrWhiteSpace(benchBaselineRomName) ? nesController.CurrentRomName : benchBaselineRomName;
                sb.AppendLine($"ROM: {displayRom}");
                sb.AppendLine("Benchmark Results (5-pass average)");
                sb.AppendLine($"Each pass weight={w}; fields averaged over {passes} passes.");
                sb.AppendLine();
                sb.AppendLine("Target Cat\tIter\tTot(ms)\tPer(ms)\tReads\tWrites\tAPU Cyc\tOAM DMA\tBatches\tAvgBatch");
                foreach (var r in averaged)
                    sb.AppendLine($"{r.Name}\t{r.Iterations}\t{r.MsTotal:F2}\t{r.MsPerIter:F3}\t{r.CpuReads}\t{r.CpuWrites}\t{r.ApuCycles}\t{r.OamDmaWrites}\t{r.BatchFlushes}\t{r.AvgBatchSize:F1}");
                if (!benchSimple5x)
                {
                    sb.AppendLine(); sb.AppendLine("Raw Pass Summaries:");
                    for (int i=0;i<passes;i++)
                    {
                        sb.AppendLine($"-- Pass {i+1} --");
                        foreach (var r in passResults[i])
                            sb.AppendLine($"{r.Name}\t{r.Iterations}\t{r.MsTotal:F2}\t{r.MsPerIter:F3}\t{r.CpuReads}\t{r.CpuWrites}\t{r.ApuCycles}\t{r.OamDmaWrites}\t{r.BatchFlushes}\t{r.AvgBatchSize:F1}");
                        sb.AppendLine();
                    }
                }
                benchResultsText = sb.ToString().TrimEnd();
                var historyEntry = new BenchHistoryEntry {
                    TimestampUtc = DateTime.UtcNow,
                    Rom = displayRom,
                    CpuCore = nes.GetCpuCoreId(),
                    PpuCore = nes.GetPpuCoreId(),
                    ApuCore = nes.GetApuCoreId(),
                    Display = benchResultsText
                };
                benchHistory.Insert(0, historyEntry);
                currentBenchHistoryId = historyEntry.Id; await PersistBenchHistory();
            } catch (Exception ex) { benchResultsText = "Benchmark 5x error: "+ex.Message; }
            finally { benchRunning = false; if (wasRunning) await StartEmulation(); StateHasChanged(); }
        }

        private async Task<bool> TryLoadBaselineStateForBenchmarks()
        {
            try
            {
                if (stateBusy) return false;
                var manifestJson = await JS.InvokeAsync<string>("nesInterop.getStateChunk", SaveKey + ".manifest");
                string full = string.Empty;
                if (!string.IsNullOrWhiteSpace(manifestJson) && manifestJson.Contains("parts"))
                {
                    int parts = ExtractInt(manifestJson, "parts");
                    bool compressed = manifestJson.Contains("\"compressed\":true");
                    var sb = new System.Text.StringBuilder();
                    for (int i=0;i<parts;i++)
                    {
                        var part = await JS.InvokeAsync<string>("nesInterop.getStateChunk", SaveKey + $".part{i}");
                        if (string.IsNullOrEmpty(part)) return false;
                        sb.Append(part);
                    }
                    full = sb.ToString();
                    if (compressed && full.StartsWith("GZ:")) full = DecompressString(full.Substring(3));
                }
                else
                {
                    var single = await JS.InvokeAsync<string>("nesInterop.getStateChunk", SaveKey);
                    if (string.IsNullOrWhiteSpace(single)) return false;
                    full = single.StartsWith("GZ:") ? DecompressString(single.Substring(3)) : single;
                }
                if (string.IsNullOrEmpty(full)) return false;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(full);
                    var root = doc.RootElement;
                    try { if (root.TryGetProperty("romName", out var rnEl) && rnEl.ValueKind==System.Text.Json.JsonValueKind.String) benchBaselineRomName = rnEl.GetString() ?? benchBaselineRomName; } catch {}
                    if (root.TryGetProperty("romData", out var romEl) && romEl.ValueKind==System.Text.Json.JsonValueKind.Array)
                    {
                        int len = romEl.GetArrayLength(); if (len>0)
                        {
                            var romBytes = new byte[len]; int idx=0; foreach (var v in romEl.EnumerateArray()){ if(idx>=len) break; romBytes[idx++] = (byte)v.GetByte(); }
                            nes = new NesEmulator.NES { RomName = nesController.CurrentRomName };
                            nes.LoadROM(romBytes);
                            try { nes.SetCrashBehavior(NesEmulator.NES.CrashBehavior.IgnoreErrors); } catch {}
                            try { nes.RunFrame(); } catch {}
                            try { nesController.framebuffer = nes.GetFrameBuffer(); await JS.InvokeVoidAsync("nesInterop.drawFrame", "nes-canvas", nesController.framebuffer); } catch {}
                            BuildMemoryDomains();
                            try { if (root.TryGetProperty("romName", out var rnEl2) && rnEl2.ValueKind==System.Text.Json.JsonValueKind.String) { nes.RomName = rnEl2.GetString() ?? nes.RomName; } } catch {}
                        }
                    }
                }
                catch {}
                nes?.LoadState(full);
                try { ApplySelectedCores(); } catch {}
                try { nes?.RunFrame(); } catch {}
                try { if(nes!=null){ var savedName = nes.GetSavedRomName(full); if(!string.IsNullOrWhiteSpace(savedName)) { nes.RomName = savedName; nesController.CurrentRomName = savedName; nesController.RomFileName = savedName; } } } catch {}
                nesController.AutoStaticSuppressed = true;
                try { nes?.SetCrashBehavior(NesEmulator.NES.CrashBehavior.IgnoreErrors); } catch {}
                try { await JS.InvokeVoidAsync("nesInterop.resetAudioTimeline"); } catch {}
                try { nesController.framebuffer = nes!.GetFrameBuffer(); await JS.InvokeVoidAsync("nesInterop.drawFrame", "nes-canvas", nesController.framebuffer); } catch {}
                return true;
            }
            catch { return false; }
        }

        private async Task CopyBenchResults(){ try { if (!string.IsNullOrEmpty(benchResultsText)) await JS.InvokeVoidAsync("navigator.clipboard.writeText", benchResultsText); } catch { } }
        private async Task ClearBenchHistory(){ benchHistory.Clear(); currentBenchHistoryId=null; await PersistBenchHistory(); }
        private void ShowHistoryEntry(string id){ currentBenchHistoryId = id == currentBenchHistoryId ? null : id; }
        private async Task DeleteBenchEntry(string id){ var e = benchHistory.FirstOrDefault(x=>x.Id==id); if (e!=null){ benchHistory.Remove(e); if (currentBenchHistoryId==id) currentBenchHistoryId=null; await PersistBenchHistory(); } }

        private void OpenCompareModal(){ BuildComparisonDatasets(); compareModalOpen = true; StateHasChanged(); }
        private void CloseCompareModal(){ compareModalOpen = false; CancelDiffAnim(); }

        private void BuildComparisonDatasets()
        {
            recentDiffRows.Clear(); timelineSeries.Clear(); allTargets.Clear(); timelineOrder.Clear(); visibleTargets.Clear(); highlightMetricName=null; hoverIndex=null; hoverTarget=null; hoverPointTooltip=null;
            var chrono = benchHistory.OrderBy(e=>e.TimestampUtc).ToList();
            if (chrono.Count > MaxTimelineTrendPoints) chrono = chrono.Skip(chrono.Count - MaxTimelineTrendPoints).ToList();
            if (chrono.Count < 2) return;
            var parsed = new List<(BenchHistoryEntry Entry, Dictionary<string,(double MsPerIter,long Reads,long Writes,long Apu,long Oam)>)>();
            foreach (var h in chrono)
            {
                var dict = new Dictionary<string,(double,long,long,long,long)>();
                try {
                    using var sr = new System.IO.StringReader(h.Display); string? line; bool inTable=false;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.StartsWith("Target Cat")) { inTable=true; continue; }
                        if (!inTable) continue; if (string.IsNullOrWhiteSpace(line)) break;
                        var parts = line.Split('\t'); if (parts.Length < 8) continue; string name = parts[0].Trim();
                        if (!double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var msPer)) continue;
                        long.TryParse(parts[4], out var reads); long.TryParse(parts[5], out var writes); long.TryParse(parts[6], out var apu); long.TryParse(parts[7], out var oam);
                        dict[name]=(msPer,reads,writes,apu,oam);
                    }
                } catch { }
                if (dict.Count>0) parsed.Add((h, dict));
            }
            if (parsed.Count < 2) return;
            timelineOrder = parsed.Select(p=>p.Entry.TimestampUtc).ToList();
            allTargets = parsed.SelectMany(p=>p.Item2.Keys).Distinct().OrderBy(k=>k).ToList();
            foreach (var t in allTargets)
            {
                timelineSeries[t] = new List<TimelinePoint>();
                foreach (var entry in parsed)
                {
                    if (entry.Item2.TryGetValue(t, out var v))
                        timelineSeries[t].Add(new TimelinePoint(entry.Entry.TimestampUtc, v.MsPerIter, v.Reads, v.Writes, v.Apu, v.Oam, entry.Entry.CpuCore, entry.Entry.PpuCore, entry.Entry.ApuCore, entry.Entry.Rom));
                    else
                        timelineSeries[t].Add(new TimelinePoint(entry.Entry.TimestampUtc, double.NaN, 0,0,0,0, entry.Entry.CpuCore, entry.Entry.PpuCore, entry.Entry.ApuCore, entry.Entry.Rom));
                }
                visibleTargets.Add(t);
            }
            var last = parsed[^1]; var prev = parsed[^2];
            foreach (var kv in last.Item2)
            {
                var name = kv.Key; var cur = kv.Value; if (!prev.Item2.TryGetValue(name, out var p)) continue;
                double deltaMs = cur.MsPerIter - p.MsPerIter; double deltaPct = p.MsPerIter != 0 ? (deltaMs / p.MsPerIter) * 100.0 : 0;
                recentDiffRows.Add(new DiffRow(name, cur.MsPerIter, p.MsPerIter, deltaMs, deltaPct, cur.Reads - p.Reads, cur.Writes - p.Writes, cur.Apu - p.Apu, cur.Oam - p.Oam));
            }
            recentDiffRows = recentDiffRows.OrderBy(r=>r.Name).ToList();
        }

        private void ToggleTarget(string target){ if (visibleTargets.Contains(target)) visibleTargets.Remove(target); else visibleTargets.Add(target); StateHasChanged(); }
        private void TimelineMouseLeave(){ hoverIndex=null; hoverTarget=null; hoverPointTooltip=null; }
        private void TimelineMouseMove(MouseEventArgs e)
        {
            if (timelineOrder.Count==0 || visibleTargets.Count==0) return; double plotW=960, plotH=200, left=30, top=20; int count = timelineOrder.Count; double mouseX = e.OffsetX; double mouseY = e.OffsetY; if (mouseX < left-5 || mouseX > left+plotW+5 || mouseY < top-5 || mouseY > top+plotH+15) { TimelineMouseLeave(); StateHasChanged(); return; }
            var frac = (mouseX - left)/Math.Max(1,plotW); frac = Math.Clamp(frac,0,1); int idx = (int)Math.Round(frac*(count-1)); if (idx<0 || idx>=count) { TimelineMouseLeave(); StateHasChanged(); return; }
            string? bestTarget=null; double bestDy=double.MaxValue; double bestY=0; double globalMax=0;
            if (!compareNormalize){ foreach (var t in visibleTargets) foreach (var v in timelineSeries[t]) if (!double.IsNaN(v.MsPerIter) && v.MsPerIter>globalMax) globalMax=v.MsPerIter; if (globalMax<=0) globalMax=1; }
            foreach (var t in visibleTargets)
            {
                var pt = timelineSeries[t][idx]; if (double.IsNaN(pt.MsPerIter)) continue; double y; if (compareNormalize){ double minT=double.MaxValue,maxT=double.MinValue; foreach (var v in timelineSeries[t]) if(!double.IsNaN(v.MsPerIter)){ if(v.MsPerIter<minT) minT=v.MsPerIter; if(v.MsPerIter>maxT) maxT=v.MsPerIter; } if (minT==double.MaxValue){ minT=0; maxT=1; } if (Math.Abs(maxT-minT) < 1e-9) maxT = minT+1; var norm = (pt.MsPerIter - minT)/(maxT-minT); y = top+plotH - norm*plotH; } else { y = top+plotH - (pt.MsPerIter/globalMax)*plotH; } var dy = Math.Abs(y - mouseY); if (dy < bestDy){ bestDy=dy; bestTarget=t; bestY=y; }
            }
            if (bestTarget==null){ TimelineMouseLeave(); StateHasChanged(); return; }
            hoverIndex = idx; hoverTarget = bestTarget; var hpt = timelineSeries[bestTarget][idx]; var timeLabel = timelineOrder[idx].ToLocalTime().ToString("HH:mm:ss"); hoverPointTooltip = new HoverTooltip(bestTarget,timeLabel,hpt.MsPerIter,hpt.Reads,hpt.Writes,hpt.Apu,hpt.Oam,hpt.CpuCore,hpt.PpuCore,hpt.ApuCore,hpt.Rom); double hoverX = left + plotW*(idx/(double)Math.Max(1,count-1)); hoverTooltipLeftPx = Math.Clamp(hoverX + 12, 0, 1000-190); hoverTooltipTopPx = Math.Clamp(bestY - 10, 0, 240-120); StateHasChanged();
        }
        private string ColorForTarget(string target){ int h=0; foreach (var c in target){ h = (h*31 + c) & 0xFFFFFF; } int r = (h & 0xFF0000)>>16; int g=(h & 0x00FF00)>>8; int b=h&0xFF; r = (r+128)/2; g=(g+128)/2; b=(b+128)/2; return $"rgb({r},{g},{b})"; }
        private async Task PlayDiffAnimation(){ if (diffAnimating || recentDiffRows.Count==0) return; CancelDiffAnim(); diffAnimating=true; diffAnimCts = new CancellationTokenSource(); try { foreach (var row in recentDiffRows){ highlightMetricName = row.Name; StateHasChanged(); await Task.Delay(900, diffAnimCts.Token); } } catch { } finally { diffAnimating=false; highlightMetricName=null; StateHasChanged(); } }
        private void CancelDiffAnim(){ try { diffAnimCts?.Cancel(); } catch {} diffAnimating=false; highlightMetricName=null; }

        // Inline editing of benchmark ROM/note
        private void StartBenchRomEdit(BenchHistoryEntry entry){ editingBenchRomId = entry.Id; editingBenchRomValue = entry.Rom; StateHasChanged(); }
        private async Task CommitBenchRomEdit(string id)
        {
            if (editingBenchRomId != id) { editingBenchRomId=null; return; }
            var e = benchHistory.FirstOrDefault(x=>x.Id==id); if (e!=null)
            {
                var newVal = editingBenchRomValue?.Trim() ?? string.Empty; if (newVal.Length>0 && newVal != e.Rom)
                {
                    e.Rom = newVal;
                    if (!string.IsNullOrEmpty(e.Display))
                    {
                        var lines = e.Display.Split('\n'); if (lines.Length>0 && lines[0].StartsWith("ROM:")) { lines[0] = $"ROM: {newVal}"; e.Display = string.Join('\n', lines); }
                    }
                    await PersistBenchHistory(); if (compareModalOpen) BuildComparisonDatasets();
                }
            }
            editingBenchRomId=null; editingBenchRomValue=string.Empty; StateHasChanged();
        }
        private async void HandleBenchRomEditKey(KeyboardEventArgs e, string id){ if (e.Key=="Enter") await CommitBenchRomEdit(id); else if (e.Key=="Escape") { editingBenchRomId=null; StateHasChanged(); } }
    }
}
