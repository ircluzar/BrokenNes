using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace BrokenNes
{
    public partial class Emulator
    {
        // Public wrappers for UI
        public Task SaveStateAsync() => SaveState();
        public Task LoadStateAsync() => LoadState();
        public Task DumpStateAsync(){ DumpState(); return Task.CompletedTask; }

        private async Task SaveState()
        {
            if (nes == null || stateBusy) return; stateBusy = true;
#if DIAG_LOG
            void Diag(string s){ try { Console.WriteLine($"[SaveStateUI] {DateTime.UtcNow:O} {s}"); } catch {} }
#else
            void Diag(string s) { }
#endif
            DateTime diagStart = DateTime.UtcNow;
            Diag("UI SaveState invoked");
            try
            {
                Diag("Calling nes.SaveState()");
                var raw = nes.SaveState();
                Diag(raw==null?"nes.SaveState returned null":"nes.SaveState returned length="+raw.Length);
                if (string.IsNullOrEmpty(raw)) { Status.Set("Empty state"); Diag("Empty state early return"); return; }
                string payload = raw; bool compressed = false;
                try
                {
                    Diag("Attempting compression");
                    var gz = CompressString(raw);
                    Diag("Compression produced length="+gz.Length);
                    if (gz.Length < raw.Length * 0.95)
                    { payload = "GZ:" + gz; compressed = true; Diag("Using compressed payload"); }
                    else Diag("Compression not beneficial");
                }
                catch (Exception ex){ Diag("Compression failed: "+ex.Message); }
                Diag("Removing existing chunks (if any)");
                await RemoveExistingChunks();
                Diag("Existing chunks removed");
        if (payload.Length <= SaveChunkCharSize)
                {
                    try
                    {
                        await JS.InvokeVoidAsync("nesInterop.saveStateChunk", SaveKey, payload);
                        await JS.InvokeVoidAsync("nesInterop.removeStateKey", SaveKey + ".manifest");
                        Status.Set(compressed ? "State saved (compressed)" : "State saved");
            // If saving outside of achievements mode, drop trusted continue flag
            try { if (_achEngine == null) await ClearTrustedContinueAsync(); } catch {}
                        Diag("Single chunk save complete");
                    }
                    catch (JSException jsex)
                    {
                        nesController.ErrorMessage = "Save error: " + jsex.Message;
                        Diag("Single chunk save JSException: "+jsex.Message);
                    }
                    return;
                }
                var parts = new List<string>();
                for (int i=0;i<payload.Length;i+=SaveChunkCharSize)
                    parts.Add(payload.Substring(i, Math.Min(SaveChunkCharSize, payload.Length - i)));
                try
                {
                    for (int i=0;i<parts.Count;i++)
                        await JS.InvokeVoidAsync("nesInterop.saveStateChunk", SaveKey + $".part{i}", parts[i]);
                    string manifest = $"{{\"version\":1,\"compressed\":{compressed.ToString().ToLowerInvariant()},\"parts\":{parts.Count}}}";
                    await JS.InvokeVoidAsync("nesInterop.saveStateChunk", SaveKey + ".manifest", manifest);
                    await JS.InvokeVoidAsync("nesInterop.removeStateKey", SaveKey);
                    Status.Set(compressed ? $"State saved in {parts.Count} parts (compressed)" : $"State saved in {parts.Count} parts");
                    // If saving outside of achievements mode, drop trusted continue flag
                    try { if (_achEngine == null) await ClearTrustedContinueAsync(); } catch {}
                }
                catch (JSException jsex)
                {
                    nesController.ErrorMessage = "Save error (chunked): " + jsex.Message;
                    try { await RemoveExistingChunks(); } catch {}
                }
            }
            catch (Exception ex)
            {
                nesController.ErrorMessage = "Save error: " + ex.Message;
            }
            finally { stateBusy = false; }
        }

        private async Task LoadState()
        {
            if (stateBusy) return; stateBusy = true; bool wasRunning = nesController.IsRunning;
            try
            {
                if (wasRunning) await PauseEmulation();
                var manifestJson = await JS.InvokeAsync<string>("nesInterop.getStateChunk", SaveKey + ".manifest");
                string full = string.Empty;
                if (!string.IsNullOrWhiteSpace(manifestJson) && manifestJson.Contains("parts"))
                {
                    try
                    {
                        int parts = ExtractInt(manifestJson, "parts");
                        bool compressed = manifestJson.Contains("\"compressed\":true");
                        var sb = new StringBuilder();
                        for (int i=0;i<parts;i++)
                        {
                            var part = await JS.InvokeAsync<string>("nesInterop.getStateChunk", SaveKey + $".part{i}");
                            if (part == null) { nesController.ErrorMessage = "Load error: missing part " + i; stateBusy=false; return; }
                            sb.Append(part);
                        }
                        full = sb.ToString();
                        if (compressed && full.StartsWith("GZ:")) full = DecompressString(full.Substring(3));
                    }
                    catch (Exception ex) { nesController.ErrorMessage = "Load error (chunked): " + ex.Message; return; }
                }
                else
                {
                    var single = await JS.InvokeAsync<string>("nesInterop.getStateChunk", SaveKey);
                    if (string.IsNullOrWhiteSpace(single)) { Status.Set("No saved state"); return; }
                    if (single.StartsWith("GZ:")) { try { full = DecompressString(single.Substring(3)); } catch (Exception ex){ nesController.ErrorMessage = "Decompress error: " + ex.Message; return; } }
                    else full = single;
                }
                if (!string.IsNullOrEmpty(full))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(full);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("romData", out var romEl) && romEl.ValueKind==System.Text.Json.JsonValueKind.Array)
                        {
                            int len = romEl.GetArrayLength(); if (len>0)
                            {
                                var romBytes = new byte[len]; int idx=0; foreach (var v in romEl.EnumerateArray()){ if(idx>=len) break; romBytes[idx++] = (byte)v.GetByte(); }
                                nes = new NesEmulator.NES(); nes.LoadROM(romBytes); try { ApplySelectedCrashBehavior(); } catch {}
                                try { nes.RunFrame(); } catch {}
                                try { nesController.framebuffer = nes.GetFrameBuffer(); await JS.InvokeVoidAsync("nesInterop.drawFrame", "nes-canvas", nesController.framebuffer); } catch {}
                                BuildMemoryDomains();
                                try { if (root.TryGetProperty("romName", out var rnEl) && rnEl.ValueKind==System.Text.Json.JsonValueKind.String) { nes.RomName = rnEl.GetString() ?? nes.RomName; } } catch {}
                                nesController.LastLoadedRomSize = romBytes.Length;
                            }
                        }
                    }
                    catch {}
                    nes?.LoadState(full);
                    // Sync UI core selections and side-effects after LoadState
                    try
                    {
                        if (nes != null)
                        {
                            nesController.CpuCoreSel = NesEmulator.CoreRegistry.ExtractSuffix(nes.GetCpuCoreId(), "CPU_");
                            nesController.PpuCoreSel = NesEmulator.CoreRegistry.ExtractSuffix(nes.GetPpuCoreId(), "PPU_");
                            nesController.ApuCoreSel = NesEmulator.CoreRegistry.ExtractSuffix(nes.GetApuCoreId(), "APU_");
                            // Ensure FamicloneOn flag follows the active APU
                            SetApuCoreSelFromEmu();
                            // Re-apply SoundFont/JS bridges according to selected APU core
                            AutoConfigureForApuCore();
                        }
                    }
                    catch { }
                    try { var savedName = nes?.GetSavedRomName(full); if(!string.IsNullOrWhiteSpace(savedName) && nes!=null) { nes.RomName = savedName; nesController.CurrentRomName = savedName; nesController.RomFileName = savedName; } } catch {}
                    nesController.AutoStaticSuppressed = true;
                    // Re-apply the selected crash behavior after state load
                    try { ApplySelectedCrashBehavior(); } catch {}
                    try { await JS.InvokeVoidAsync("nesInterop.resetAudioTimeline"); } catch {}
                    try { var _ = nes?.GetAudioBuffer(); } catch {}
                    if (nes != null)
                    {
                        nesController.framebuffer = nes.GetFrameBuffer();
                        await JS.InvokeVoidAsync("nesInterop.drawFrame", "nes-canvas", nesController.framebuffer);
                    }
                    Status.Set("State loaded");
                    StateHasChanged();
                    if (wasRunning) await StartEmulation();
                }
            }
            catch (Exception ex) { nesController.ErrorMessage = "Load error: " + ex.Message; }
            finally { stateBusy = false; }
        }

        private void DumpState()
        {
            if (nes == null) return; try { debugDump = nes.GetStateDigest(); } catch (Exception ex) { debugDump = "dump err: "+ex.Message; }
            StateHasChanged();
        }

        private static string CompressString(string input)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(input);
            using var ms = new MemoryStream();
            using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, true))
            { gzip.Write(bytes, 0, bytes.Length); }
            return Convert.ToBase64String(ms.ToArray());
        }
        private static string DecompressString(string base64)
        {
            var data = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(data);
            using var gzip = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream(); gzip.CopyTo(outMs);
            return System.Text.Encoding.UTF8.GetString(outMs.ToArray());
        }
        private int ExtractInt(string json, string prop)
        {
            try { var token = "\""+prop+"\":"; int idx = json.IndexOf(token, StringComparison.Ordinal); if (idx>=0){ idx += token.Length; int end=idx; while(end<json.Length && char.IsDigit(json[end])) end++; if (int.TryParse(json.Substring(idx,end-idx), out var val)) return val; } } catch {}
            return 0;
        }
        private async Task RemoveExistingChunks()
        {
            try
            {
                var manifestJson = await JS.InvokeAsync<string>("nesInterop.getStateChunk", SaveKey + ".manifest");
                if (!string.IsNullOrWhiteSpace(manifestJson) && manifestJson.Contains("parts"))
                {
                    int parts = ExtractInt(manifestJson, "parts");
                    for (int i=0;i<parts;i++)
                        await JS.InvokeVoidAsync("nesInterop.removeStateKey", SaveKey + $".part{i}");
                    await JS.InvokeVoidAsync("nesInterop.removeStateKey", SaveKey + ".manifest");
                }
            }
            catch { }
        }

        // ===== Trusted DeckBuilder Continue helpers =====
        private async Task SetTrustedContinueAsync(string romKey, string? title)
        {
            try
            {
                var save = await _gameSaveService.LoadAsync();
                save.PendingDeckContinue = true;
                save.PendingDeckContinueRom = romKey;
                save.PendingDeckContinueTitle = string.IsNullOrWhiteSpace(title) ? romKey : title;
                save.PendingDeckContinueAtUtc = DateTime.UtcNow;
                await _gameSaveService.SaveAsync(save);
            }
            catch { }
        }

        private async Task ClearTrustedContinueAsync()
        {
            try
            {
                var save = await _gameSaveService.LoadAsync();
                if (save.PendingDeckContinue || !string.IsNullOrWhiteSpace(save.PendingDeckContinueRom) || !string.IsNullOrWhiteSpace(save.PendingDeckContinueTitle))
                {
                    save.PendingDeckContinue = false;
                    save.PendingDeckContinueRom = null;
                    save.PendingDeckContinueTitle = null;
                    save.PendingDeckContinueAtUtc = null;
                    await _gameSaveService.SaveAsync(save);
                }
            }
            catch { }
        }
    }
}
