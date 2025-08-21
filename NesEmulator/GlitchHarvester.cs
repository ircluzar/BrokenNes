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
                nesController.framebuffer = nes.GetFrameBuffer();
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
                nes.LoadState(baseState.State);
                nesController.AutoStaticSuppressed = true;
                var writes = corruptor.GenerateBlastLayer(corruptor.CorruptIntensity);
                corruptor.ApplyBlastLayer(writes, nes);
                var entry = new HarvestEntry { Name = $"Stash {++corruptor.GhStashCounter}", BaseStateId = baseState.Id, Writes = writes };
                corruptor.GhStash.Add(entry);
                Status.Set($"Stashed {writes.Count} writes based on '{baseState.Name}'");
            }
            catch (Exception ex) { Status.Set("GH corrupt err: " + ex.Message); }
            await InvokeAsync(StateHasChanged);
        }

        private string GhFindBaseName(string id) => corruptor.GhBaseStates.FirstOrDefault(b => b.Id == id)?.Name ?? "?";
        private void GhDeleteStash(string id) { corruptor.GhDeleteStash(id); }
        private void GhPromoteEntry(HarvestEntry e) { corruptor.GhPromoteEntry(e); }
        private async Task GhReplayEntry(HarvestEntry e, bool fromStockpile)
        {
            if (nes == null) return;
            var baseState = corruptor.GhBaseStates.FirstOrDefault(b => b.Id == e.BaseStateId);
            if (baseState == null) return;
            try
            {
                nes.LoadState(baseState.State);
                nesController.AutoStaticSuppressed = true;
                corruptor.ApplyBlastLayer(e.Writes, nes);
                nesController.framebuffer = nes.GetFrameBuffer();
                await JS.InvokeVoidAsync("nesInterop.drawFrame", "nes-canvas", nesController.framebuffer);
                Status.Set($"Replayed {(fromStockpile ? "stockpile" : "stash")} '{e.Name}'");
            }
            catch (Exception ex) { Status.Set("GH replay err: " + ex.Message); }
        }
        private void GhDeleteStock(string id) { corruptor.GhDeleteStock(id); }
        private void GhClearStash() { corruptor.GhClearStash(); }
        private bool GhIsRenaming(string id) => corruptor.GhRenamingId == id;
        private void GhBeginRename(HarvestEntry e) { corruptor.GhBeginRename(e); }
        private void GhCancelRename() { corruptor.GhCancelRename(); }
        private void GhRenameChange(ChangeEventArgs e) { if (e.Value is string v) corruptor.GhRenameText = v; }
        private void GhCommitRename(string id) { corruptor.GhCommitRename(id); }

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

        private async Task GhImportStockpile(ChangeEventArgs e)
        {
            try
            {
                if (e.Value == null) return;
                // placeholder: depends on JS helper to read file(s); adapt as needed
                var filesJson = await JS.InvokeAsync<string>("nesInterop.readSelectedFilesAsText", e.Value?.ToString());
                if (string.IsNullOrWhiteSpace(filesJson)) return;
                var list = System.Text.Json.JsonSerializer.Deserialize<List<ImportHarvestEntry>>(filesJson);
                if (list == null) return;
                int added = 0;
                foreach (var it in list)
                {
                    if (string.IsNullOrWhiteSpace(it.BaseState)) continue;
                    var existingBase = corruptor.GhBaseStates.FirstOrDefault(b => b.Id == it.BaseStateId);
                    if (existingBase == null)
                    {
                        corruptor.GhBaseStates.Add(new HarvesterBaseState { Id = it.BaseStateId ?? Guid.NewGuid().ToString(), Name = $"ImpBase {corruptor.GhBaseStates.Count + 1}", State = it.BaseState });
                    }
                    var entry = new HarvestEntry
                    {
                        Id = it.Id ?? Guid.NewGuid().ToString(),
                        Name = string.IsNullOrWhiteSpace(it.Name) ? $"ImpEntry {++corruptor.GhStockpileCounter}" : it.Name,
                        BaseStateId = it.BaseStateId ?? corruptor.GhBaseStates.Last().Id,
                        Writes = it.Writes ?? new(),
                        Created = it.Created == default ? DateTime.UtcNow : it.Created
                    };
                    corruptor.GhStockpile.Add(entry); added++;
                }
                Status.Set($"Imported {added} stock item(s)");
            }
            catch (Exception ex) { Status.Set("Import failed: " + ex.Message); }
        }

        private class ImportHarvestEntry
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public string? BaseStateId { get; set; }
            public List<BlastInstruction>? Writes { get; set; }
            public DateTime Created { get; set; }
            public string? BaseState { get; set; }
        }
    }
}
