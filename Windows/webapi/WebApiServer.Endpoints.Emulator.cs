using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NesEmulator;
using PngPayloadEmbedding;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        private void RegisterEmulatorEndpoints(WebApplication app)
        {
            // POST /api/emulator/pause - Pause emulation (used by overlay modules)
            app.MapPost("/api/emulator/pause", () =>
            {
                if (_pauseEmulation == null)
                {
                    return Results.BadRequest(new { success = false, error = "Pause emulation not available" });
                }

                try
                {
                    // Invoke on UI thread if we have a UI control
                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke(_pauseEmulation);
                    }
                    else
                    {
                        _pauseEmulation();
                    }

                    Console.WriteLine("[WebApi] Emulation paused via API");
                    return Results.Ok(new { success = true });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/emulator/resume - Resume emulation (used by Story mode and other overlays)
            app.MapPost("/api/emulator/resume", () =>
            {
                if (_resumeEmulation == null)
                {
                    return Results.BadRequest(new { success = false, error = "Resume emulation not available" });
                }

                try
                {
                    // Invoke on UI thread if we have a UI control
                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke(_resumeEmulation);
                    }
                    else
                    {
                        _resumeEmulation();
                    }

                    Console.WriteLine("[WebApi] Emulation resumed via API");
                    return Results.Ok(new { success = true });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/emulator/load-builtin-rom - Load a built-in ROM file (for story mode)
            app.MapPost("/api/emulator/load-builtin-rom", async (HttpContext context) =>
            {
                if (_loadBuiltInRom == null)
                {
                    return Results.BadRequest(new { success = false, error = "ROM loading not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<LoadBuiltInRomRequest>();
                    if (body == null || string.IsNullOrWhiteSpace(body.Filename))
                    {
                        return Results.BadRequest(new { success = false, error = "Filename is required" });
                    }

                    bool success = await _loadBuiltInRom(body.Filename, body.PreserveShader);

                    return Results.Ok(new
                    {
                        success,
                        filename = body.Filename
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/emulator/close-rom - Close current ROM and return to embedded test ROM
            app.MapPost("/api/emulator/close-rom", () =>
            {
                if (_closeRom == null)
                {
                    return Results.BadRequest(new { success = false, error = "Close ROM not available" });
                }

                try
                {
                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke(_closeRom);
                    }
                    else
                    {
                        _closeRom();
                    }

                    return Results.Ok(new { success = true });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/emulator/backgrounds - List available backgrounds
            app.MapGet("/api/emulator/backgrounds", () =>
            {
                if (_getAvailableBackgrounds == null)
                {
                    return Results.BadRequest(new { success = false, error = "Backgrounds not available" });
                }

                try
                {
                    var backgrounds = _getAvailableBackgrounds()?.ToList() ?? new System.Collections.Generic.List<string>();
                    return Results.Ok(new { success = true, backgrounds });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/emulator/background - Set active background
            app.MapPost("/api/emulator/background", async (HttpContext context) =>
            {
                if (_setBackground == null)
                {
                    return Results.BadRequest(new { success = false, error = "Background setting not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<SetBackgroundRequest>();
                    if (body == null || string.IsNullOrWhiteSpace(body.Name))
                    {
                        return Results.BadRequest(new { success = false, error = "Name is required" });
                    }

                    var save = await _progressionSave.LoadAsync();
                    if (!_progressionSave.IsBackgroundUnlocked(save, body.Name))
                    {
                        return Results.BadRequest(new { success = false, error = "Background is locked" });
                    }

                    await _progressionSave.SetPreferredBackgroundAsync(body.Name);

                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke(() => _setBackground(body.Name));
                    }
                    else
                    {
                        _setBackground(body.Name);
                    }

                    return Results.Ok(new { success = true, name = body.Name });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/emulator/null-providers - List available null providers
            app.MapGet("/api/emulator/null-providers", () =>
            {
                if (_getAvailableNullProviders == null)
                {
                    return Results.BadRequest(new { success = false, error = "Null providers not available" });
                }

                try
                {
                    var providers = _getAvailableNullProviders()?.ToList() ?? new System.Collections.Generic.List<string>();
                    return Results.Ok(new { success = true, providers });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/emulator/null-provider - Set active null provider
            app.MapPost("/api/emulator/null-provider", async (HttpContext context) =>
            {
                if (_setNullProvider == null)
                {
                    return Results.BadRequest(new { success = false, error = "Null provider setting not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<SetNullProviderRequest>();
                    if (body == null || string.IsNullOrWhiteSpace(body.Name))
                    {
                        return Results.BadRequest(new { success = false, error = "Name is required" });
                    }

                    var save = await _progressionSave.LoadAsync();
                    if (!_progressionSave.IsNullProviderUnlocked(save, body.Name))
                    {
                        return Results.BadRequest(new { success = false, error = "Null provider is locked" });
                    }

                    await _progressionSave.SetPreferredNullProviderAsync(body.Name);

                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke(() => _setNullProvider(body.Name));
                    }
                    else
                    {
                        _setNullProvider(body.Name);
                    }

                    return Results.Ok(new { success = true, name = body.Name });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/emulator/current-rom - Get current ROM info
            app.MapGet("/api/emulator/current-rom", () =>
            {
                if (_getCurrentRomPath == null && _getCurrentRomName == null)
                {
                    return Results.BadRequest(new { success = false, error = "ROM info not available" });
                }

                try
                {
                    var path = _getCurrentRomPath?.Invoke();
                    var name = _getCurrentRomName?.Invoke();
                    var isTestRom = string.Equals(name, "test.nes", StringComparison.OrdinalIgnoreCase);
                    return Results.Ok(new { success = true, path, name, isTestRom });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/emulator/load-rom - Load ROM from disk path
            app.MapPost("/api/emulator/load-rom", async (HttpContext context) =>
            {
                if (_loadRomFromPath == null)
                {
                    return Results.BadRequest(new { success = false, error = "ROM loading not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<LoadRomRequest>();
                    if (body == null || string.IsNullOrWhiteSpace(body.Path))
                    {
                        return Results.BadRequest(new { success = false, error = "Path is required" });
                    }

                    bool success;
                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        success = (bool)_uiControl.Invoke(() => _loadRomFromPath(body.Path));
                    }
                    else
                    {
                        success = _loadRomFromPath(body.Path);
                    }

                    return Results.Ok(new { success, path = body.Path });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/emulator/load-rom-key - Load ROM from browser storage or built-ins using the ROM key/name
            app.MapPost("/api/emulator/load-rom-key", async (HttpContext context) =>
            {
                if (_loadRomByKey == null)
                {
                    return Results.BadRequest(new { success = false, error = "ROM key loading not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<LoadRomKeyRequest>();
                    if (body == null || string.IsNullOrWhiteSpace(body.RomKey))
                    {
                        return Results.BadRequest(new { success = false, error = "RomKey is required" });
                    }

                    bool success = await _loadRomByKey(body.RomKey);
                    return Results.Ok(new { success, romKey = body.RomKey });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/emulator/load-rom-base64 - Load ROM bytes passed directly from the webmodule
            app.MapPost("/api/emulator/load-rom-base64", async (HttpContext context) =>
            {
                try
                {
                    var body = await context.Request.ReadFromJsonAsync<LoadRomBase64Request>();
                    if (body == null || string.IsNullOrWhiteSpace(body.Base64))
                    {
                        return Results.BadRequest(new { success = false, error = "Base64 payload is required" });
                    }

                    var romBytes = Convert.FromBase64String(body.Base64);
                    var romName = string.IsNullOrWhiteSpace(body.Name) ? "Imported.nes" : body.Name.Trim();

                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke((Action)(() =>
                        {
                            if (_loadRomFromBytes == null)
                            {
                                throw new InvalidOperationException("Direct ROM-byte loading not available");
                            }

                            _loadRomFromBytes(romName, romBytes);
                        }));
                    }
                    else
                    {
                        if (_loadRomFromBytes == null)
                        {
                            return Results.BadRequest(new { success = false, error = "Direct ROM-byte loading not available" });
                        }

                        _loadRomFromBytes(romName, romBytes);
                    }

                    return Results.Ok(new { success = true, name = romName, size = romBytes.Length });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/emulator/save-continue-state - Capture the persistent continue checkpoint
            app.MapPost("/api/emulator/save-continue-state", () =>
            {
                if (_saveContinueState == null)
                {
                    return Results.BadRequest(new { success = false, error = "Continue-state capture not available" });
                }

                try
                {
                    bool success;
                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        success = (bool)_uiControl.Invoke(_saveContinueState);
                    }
                    else
                    {
                        success = _saveContinueState();
                    }

                    return Results.Ok(new { success });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/emulator/load-continue-state - Restore the persistent continue checkpoint
            app.MapPost("/api/emulator/load-continue-state", async (HttpContext context) =>
            {
                if (_loadContinueState == null)
                {
                    return Results.BadRequest(new { success = false, error = "Continue-state load not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<LoadContinueStateRequest>();
                    var expectedRomName = body?.ExpectedRomName?.Trim();
                    if (!string.IsNullOrWhiteSpace(expectedRomName))
                    {
                        var currentRomName = _getCurrentRomName?.Invoke()?.Trim();
                        if (!string.IsNullOrWhiteSpace(currentRomName)
                            && !string.Equals(currentRomName, expectedRomName, StringComparison.OrdinalIgnoreCase))
                        {
                            return Results.BadRequest(new
                            {
                                success = false,
                                error = $"Current ROM '{currentRomName}' does not match expected continue ROM '{expectedRomName}'"
                            });
                        }
                    }

                    bool success;
                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        success = (bool)_uiControl.Invoke(_loadContinueState, expectedRomName);
                    }
                    else
                    {
                        success = _loadContinueState(expectedRomName);
                    }

                    return Results.Ok(new { success });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/emulator/quick-save-state - Trigger quick save (F7 equivalent)
            app.MapPost("/api/emulator/quick-save-state", () =>
            {
                if (_quickSaveState == null)
                {
                    return Results.BadRequest(new { success = false, error = "Quick-save not available" });
                }

                try
                {
                    bool success;
                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        success = (bool)_uiControl.Invoke(_quickSaveState);
                    }
                    else
                    {
                        success = _quickSaveState();
                    }

                    return Results.Ok(new { success, gated = !success });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/emulator/quick-load-state - Trigger quick load (F5 equivalent)
            app.MapPost("/api/emulator/quick-load-state", () =>
            {
                if (_quickLoadState == null)
                {
                    return Results.BadRequest(new { success = false, error = "Quick-load not available" });
                }

                try
                {
                    bool success;
                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        success = (bool)_uiControl.Invoke(_quickLoadState);
                    }
                    else
                    {
                        success = _quickLoadState();
                    }

                    return Results.Ok(new { success, gated = !success });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });
        }
    }
}
