using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using BrokenNes.Models;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        private void RegisterSaveEndpoints(WebApplication app)
        {
            // GET /api/save - Get game save data (owned cores, etc.)
            app.MapGet("/api/save", async () =>
            {
                try
                {
                    var save = await _progressionSave.LoadAsync();
                    return Results.Ok(save);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });

            // POST /api/save - Merge and persist game save through the native progression authority.
            app.MapPost("/api/save", async (HttpContext context) =>
            {
                try
                {
                    var incoming = await context.Request.ReadFromJsonAsync<GameSave>();
                    if (incoming == null)
                    {
                        return Results.BadRequest(new
                        {
                            success = false,
                            error = "Request body is required"
                        });
                    }

                    var save = await _progressionSave.MergeAndSaveAsync(
                        incoming,
                        _getAvailableBackgrounds?.Invoke(),
                        _getAvailableNullProviders?.Invoke());
                    RefreshProgressionUi();
                    return Results.Ok(save);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });

            // POST /api/save/reset - Reset progression to a fresh canonical save and clear trusted continue artifacts.
            app.MapPost("/api/save/reset", async () =>
            {
                try
                {
                    var save = await _progressionSave.ResetAsync();
                    RefreshProgressionUi();
                    return Results.Ok(save);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });

            // GET /api/save/continue-preview?romKey=... - Serve the trusted continue screenshot for a ROM.
            app.MapGet("/api/save/continue-preview", async (string? romKey) =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(romKey))
                    {
                        return Results.BadRequest(new
                        {
                            success = false,
                            error = "romKey is required"
                        });
                    }

                    var appDataRoot = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "BrokenNes"
                    );
                    var savePath = System.IO.Path.Combine(appDataRoot, "gamesave.json");

                    string? previewPath = null;
                    var save = await _progressionSave.LoadAsync();
                    var normalizedRomKey = romKey.Trim().ToLowerInvariant();

                    if (save?.ContinueSlots != null)
                    {
                        if (save.ContinueSlots.TryGetValue(normalizedRomKey, out var slot))
                        {
                            previewPath = slot?.PreviewImagePath;
                        }
                        else
                        {
                            foreach (var entry in save.ContinueSlots.Values)
                            {
                                if (entry == null || string.IsNullOrWhiteSpace(entry.RomKey))
                                {
                                    continue;
                                }

                                if (string.Equals(entry.RomKey.Trim(), romKey.Trim(), StringComparison.OrdinalIgnoreCase))
                                {
                                    previewPath = entry.PreviewImagePath;
                                    break;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(previewPath) || !System.IO.File.Exists(previewPath))
                    {
                        previewPath = GetContinuePreviewFallbackPath(appDataRoot, romKey);
                    }

                    if (string.IsNullOrWhiteSpace(previewPath) || !System.IO.File.Exists(previewPath))
                    {
                        return Results.NotFound(new
                        {
                            success = false,
                            error = "Continue preview not found"
                        });
                    }

                    return Results.File(previewPath, "image/png");
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });

            static string? GetContinuePreviewFallbackPath(string appDataRoot, string romKey)
            {
                if (string.IsNullOrWhiteSpace(romKey))
                {
                    return null;
                }

                var continueDir = System.IO.Path.Combine(appDataRoot, "DeckContinueStates");
                var normalized = romKey.Trim().ToLowerInvariant();
                var fileName = System.IO.Path.GetFileName(normalized);
                var sb = new System.Text.StringBuilder(fileName.Length);
                foreach (var ch in fileName)
                {
                    sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
                }

                var prefix = sb.ToString().Trim('_');
                if (prefix.Length > 48)
                {
                    prefix = prefix[..48];
                }

                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
                var safeName = string.IsNullOrWhiteSpace(prefix)
                    ? hash[..16]
                    : $"{prefix}-{hash[..16]}";
                return System.IO.Path.Combine(continueDir, $"{safeName}.png");
            }
        }
    }
}
