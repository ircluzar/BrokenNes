using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using BrokenNes.CorruptorModels;
using NesEmulator;

namespace BrokenNes
{
    public partial class Emulator
    {
        // ================= Glitch Harvester / RTC UI bridge =================

        private void GhAddBaseState()
        {
            if (nes == null) return;
            try
            {
                corruptor.GhAddBaseState(nes);
                Status.Set("Added base state");
            }
            catch (Exception ex) { Status.Set("Add base failed: " + ex.Message); }
            StateHasChanged();
        }

        private void GhOnBaseChanged(ChangeEventArgs e)
        {
            if (e.Value is string v) corruptor.GhSelectedBaseId = v;
        }

        private void GhLoadSelectedBase()
        {
            if (nes == null || !corruptor.GhHasSelectedBase) return;
            try
            {
                var b = corruptor.GhBaseStates.FirstOrDefault(x => x.Id == corruptor.GhSelectedBaseId);
                if (b == null) return;
                nes.LoadState(b.State);
                nesController.AutoStaticSuppressed = true;
                // Post-load sync to mirror top-level LoadState: update core selections, audio, and domains
                try
                {
                    nesController.CpuCoreSel = NesEmulator.CoreRegistry.ExtractSuffix(nes.GetCpuCoreId(), "CPU_");
                    nesController.PpuCoreSel = NesEmulator.CoreRegistry.ExtractSuffix(nes.GetPpuCoreId(), "PPU_");
                    nesController.ApuCoreSel = NesEmulator.CoreRegistry.ExtractSuffix(nes.GetApuCoreId(), "APU_");
                    SetApuCoreSelFromEmu();
                    AutoConfigureForApuCore();
                }
                catch { }
                try { nes?.SetCrashBehavior(NesEmulator.NES.CrashBehavior.IgnoreErrors); } catch { }
                // Reset audio pipeline like top Load does (fire-and-forget to keep method signature)
                try { _ = JS.InvokeVoidAsync("nesInterop.resetAudioTimeline"); } catch { }
                try { var _ = nes?.GetAudioBuffer(); } catch { }
                BuildMemoryDomains();
                nesController.framebuffer = nes!.GetFrameBuffer();
                _ = JS.InvokeVoidAsync("nesInterop.drawFrame", "nes-canvas", nesController.framebuffer);
                Status.Set($"Loaded base '{b.Name}'");
            }
            catch (Exception ex) { Status.Set("Load base err: " + ex.Message); }
        }

        private void GhDeleteSelectedBase()
        {
            try { corruptor.GhDeleteSelectedBase(); Status.Set("Deleted base"); } catch { }
        }

        private async Task GhCorruptAndStash()
        {
            if (nes == null || !corruptor.GhHasSelectedBase) return;
            var baseState = corruptor.GhBaseStates.FirstOrDefault(b => b.Id == corruptor.GhSelectedBaseId);
            if (baseState == null) return;
            try
            {
                nes!.LoadState(baseState.State);
                nesController.AutoStaticSuppressed = true;
                // Post-load sync to ensure cores/UI/audio are consistent before corruption writes
                try
                {
                    nesController.CpuCoreSel = NesEmulator.CoreRegistry.ExtractSuffix(nes.GetCpuCoreId(), "CPU_");
                    nesController.PpuCoreSel = NesEmulator.CoreRegistry.ExtractSuffix(nes.GetPpuCoreId(), "PPU_");
                    nesController.ApuCoreSel = NesEmulator.CoreRegistry.ExtractSuffix(nes.GetApuCoreId(), "APU_");
                    SetApuCoreSelFromEmu();
                    AutoConfigureForApuCore();
                }
                catch { }
                try { nes?.SetCrashBehavior(NesEmulator.NES.CrashBehavior.IgnoreErrors); } catch { }
                try { await JS.InvokeVoidAsync("nesInterop.resetAudioTimeline"); } catch { }
                try { var _ = nes?.GetAudioBuffer(); } catch { }
                BuildMemoryDomains();
                var writes = corruptor.GenerateBlastLayer(corruptor.CorruptIntensity);
                corruptor.ApplyBlastLayer(writes, nes!);
                var entry = new HarvestEntry { Name = $"Stash {++corruptor.GhStashCounter}", BaseStateId = baseState.Id, Writes = writes };
                corruptor.GhStash.Add(entry);
                Status.Set($"Stashed {writes.Count} writes based on '{baseState.Name}'");
            }
            catch (Exception ex) { Status.Set("GH corrupt err: " + ex.Message); }
            await InvokeAsync(StateHasChanged);
        }

        private void GhPromoteEntry(HarvestEntry e) { corruptor.GhPromoteEntry(e); }
        private async Task GhReplayEntry(HarvestEntry e, bool fromStockpile)
        {
            if (nes == null) return;
            var baseState = corruptor.GhBaseStates.FirstOrDefault(b => b.Id == e.BaseStateId);
            if (baseState == null) return;
            try
            {
                nes!.LoadState(baseState.State);
                nesController.AutoStaticSuppressed = true;
                // Post-load sync to mirror top-level LoadState before applying writes
                try
                {
                    nesController.CpuCoreSel = NesEmulator.CoreRegistry.ExtractSuffix(nes.GetCpuCoreId(), "CPU_");
                    nesController.PpuCoreSel = NesEmulator.CoreRegistry.ExtractSuffix(nes.GetPpuCoreId(), "PPU_");
                    nesController.ApuCoreSel = NesEmulator.CoreRegistry.ExtractSuffix(nes.GetApuCoreId(), "APU_");
                    SetApuCoreSelFromEmu();
                    AutoConfigureForApuCore();
                }
                catch { }
                try { nes?.SetCrashBehavior(NesEmulator.NES.CrashBehavior.IgnoreErrors); } catch { }
                try { await JS.InvokeVoidAsync("nesInterop.resetAudioTimeline"); } catch { }
                try { var _ = nes?.GetAudioBuffer(); } catch { }
                BuildMemoryDomains();
                corruptor.ApplyBlastLayer(e.Writes, nes!);
                nesController.framebuffer = nes!.GetFrameBuffer();
                await JS.InvokeVoidAsync("nesInterop.drawFrame", "nes-canvas", nesController.framebuffer);
                Status.Set($"Replayed {(fromStockpile ? "stockpile" : "stash")} '{e.Name}'");
            }
            catch (Exception ex) { Status.Set("GH replay err: " + ex.Message); }
        }
        private void GhClearStash() { corruptor.GhClearStash(); }

        private async Task GhExportStockpile()
        {
            try
            {
                var exportObj = corruptor.GhStockpile.Select(e => new { e.Id, e.Name, e.BaseStateId, e.Created, Writes = e.Writes, BaseState = corruptor.GhBaseStates.FirstOrDefault(b => b.Id == e.BaseStateId)?.State }).ToList();
                var json = System.Text.Json.JsonSerializer.Serialize(exportObj);
                await JS.InvokeVoidAsync("nesInterop.downloadText", $"stockpile_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json", json);
                Status.Set("Exported stockpile");
            }
            catch (Exception ex) { Status.Set("Export failed: " + ex.Message); }
        }
    }
}
