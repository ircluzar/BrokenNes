using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NesEmulator;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        /// <summary>
        /// Register Real-Time Corruptor (RTC) API endpoints
        /// </summary>
        private void RegisterRtcEndpoints(WebApplication app)
        {
            // Placeholder endpoints for RTC - these would connect to a Corruptor instance
            // For now, they return minimal responses to satisfy the API contract
            
            // GET /api/rtc/domains - Get memory domains available for corruption
            app.MapGet("/api/rtc/domains", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    return Results.Ok(new
                    {
                        success = true,
                        domains = corruptor.MemoryDomains.Select(d => new
                        {
                            key = d.Key,
                            name = d.Label,
                            size = d.Size,
                            selected = d.Selected
                        })
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/rtc/domains/selection - Set memory domain selection
            app.MapPost("/api/rtc/domains/selection", async (HttpContext context) =>
            {
                try
                {
                    var corruptor = _getCorruptor();
                    if (corruptor == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                    }

                    var form = await context.Request.ReadFromJsonAsync<DomainSelectionRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    // Update domain selection
                    corruptor.DomainsChanged(form.SelectedDomains ?? Array.Empty<string>());
                    
                    return Results.Ok(new
                    {
                        success = true,
                        selectedDomains = form.SelectedDomains
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/rtc/intensity - Get corruption intensity
            app.MapGet("/api/rtc/intensity", () =>
            {
                var corruptor = _getCorruptor();
                return Results.Ok(new
                {
                    success = true,
                    intensity = corruptor?.CorruptIntensity ?? 1
                });
            });

            // POST /api/rtc/intensity - Set corruption intensity
            app.MapPost("/api/rtc/intensity", async (HttpContext context) =>
            {
                try
                {
                    var corruptor = _getCorruptor();
                    if (corruptor == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                    }

                    var form = await context.Request.ReadFromJsonAsync<IntensityRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var intensity = Math.Clamp(form.Intensity, 1, 65535);
                    corruptor.CorruptIntensity = intensity;
                    return Results.Ok(new
                    {
                        success = true,
                        intensity = intensity
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/rtc/blast-type - Get current blast type
            app.MapGet("/api/rtc/blast-type", () =>
            {
                var corruptor = _getCorruptor();
                return Results.Ok(new
                {
                    success = true,
                    blastType = corruptor?.BlastType ?? "RANDOM"
                });
            });

            // POST /api/rtc/blast-type - Set blast type
            app.MapPost("/api/rtc/blast-type", async (HttpContext context) =>
            {
                try
                {
                    var corruptor = _getCorruptor();
                    if (corruptor == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                    }

                    var form = await context.Request.ReadFromJsonAsync<BlastTypeRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var validTypes = new[] { "RANDOM", "TILT", "RANDOMTILT", "NOP", "BITFLIP", "IMAGINE_NEXT", "IMAGINE_RANDOM" };
                    if (!validTypes.Contains(form.BlastType?.ToUpperInvariant()))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid blast type" });
                    }

                    var requestedBlastType = form.BlastType?.ToUpperInvariant() ?? "RANDOM";
                    var imagineBlastType = requestedBlastType == "IMAGINE_NEXT" || requestedBlastType == "IMAGINE_RANDOM";
                    if (imagineBlastType && !await IsImagineUnlockedAsync())
                    {
                        return Results.BadRequest(new { success = false, error = "ImagineBug is locked" });
                    }

                    corruptor.BlastType = requestedBlastType;
                    return Results.Ok(new
                    {
                        success = true,
                        blastType = corruptor.BlastType
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/rtc/blast - Execute a corruption blast
            app.MapPost("/api/rtc/blast", async () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var imagineBlastType = string.Equals(corruptor.BlastType, "IMAGINE_NEXT", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(corruptor.BlastType, "IMAGINE_RANDOM", StringComparison.OrdinalIgnoreCase);
                    if (imagineBlastType && !await IsImagineUnlockedAsync())
                    {
                        return Results.BadRequest(new { success = false, error = "ImagineBug is locked" });
                    }

                    corruptor.Blast(nes);
                    return Results.Ok(new
                    {
                        success = true,
                        message = corruptor.LastBlastInfo,
                        writesApplied = corruptor.CorruptIntensity
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/rtc/auto-corrupt - Get auto-corrupt state
            app.MapGet("/api/rtc/auto-corrupt", () =>
            {
                var corruptor = _getCorruptor();
                return Results.Ok(new
                {
                    success = true,
                    autoCorrupt = corruptor?.AutoCorrupt ?? false
                });
            });

            // POST /api/rtc/auto-corrupt - Toggle auto-corrupt
            app.MapPost("/api/rtc/auto-corrupt", async (HttpContext context) =>
            {
                try
                {
                    var corruptor = _getCorruptor();
                    if (corruptor == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                    }

                    var form = await context.Request.ReadFromJsonAsync<AutoCorruptRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var imagineBlastType = string.Equals(corruptor.BlastType, "IMAGINE_NEXT", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(corruptor.BlastType, "IMAGINE_RANDOM", StringComparison.OrdinalIgnoreCase);
                    if (form.Enabled && imagineBlastType && !await IsImagineUnlockedAsync())
                    {
                        return Results.BadRequest(new { success = false, error = "ImagineBug is locked" });
                    }

                    corruptor.AutoCorrupt = form.Enabled;
                    return Results.Ok(new
                    {
                        success = true,
                        autoCorrupt = corruptor.AutoCorrupt
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/rtc/let-it-rip - Apply "Let It Rip" preset
            app.MapPost("/api/rtc/let-it-rip", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    corruptor.LetItRip();
                    return Results.Ok(new
                    {
                        success = true,
                        message = corruptor.LastBlastInfo,
                        intensity = corruptor.CorruptIntensity,
                        autoCorrupt = corruptor.AutoCorrupt,
                        selectedDomains = corruptor.MemoryDomains
                            .Where(d => d.Selected)
                            .Select(d => d.Label)
                            .ToArray()
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/rtc/crash-behavior - Get current crash handling mode
            app.MapGet("/api/rtc/crash-behavior", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                var corruptor = _getCorruptor();
                var crashed = nes.IsCrashed();
                return Results.Ok(new
                {
                    success = true,
                    crashBehavior = corruptor?.CrashBehavior ?? "IgnoreErrors",
                    crashed = crashed
                });
            });

            // POST /api/rtc/crash-behavior - Set crash handling mode
            app.MapPost("/api/rtc/crash-behavior", async (HttpContext context) =>
            {
                try
                {
                    var corruptor = _getCorruptor();
                    if (corruptor == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                    }

                    var nes = _getNes();
                    if (nes == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                    }

                    var form = await context.Request.ReadFromJsonAsync<CrashBehaviorRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var behavior = form.Behavior ?? "IgnoreErrors";
                    if (string.Equals(behavior, "ImagineFix", StringComparison.OrdinalIgnoreCase) && !await IsImagineUnlockedAsync())
                    {
                        return Results.BadRequest(new { success = false, error = "ImagineBug is locked" });
                    }
                    
                    // Use the callback to properly save config if available
                    if (_setCrashBehavior != null)
                    {
                        _setCrashBehavior(behavior);
                    }
                    else
                    {
                        // Fallback: just update corruptor and emulator
                        corruptor.CrashBehavior = behavior;
                        
                        // Actually apply the crash behavior to the emulator
                        switch (behavior)
                        {
                            case "IgnoreErrors":
                                nes.SetCrashBehavior(NES.CrashBehavior.IgnoreErrors);
                                break;
                            case "ImagineFix":
                                nes.SetCrashBehavior(NES.CrashBehavior.ImagineFix);
                                break;
                            default: // "RedScreen"
                                nes.SetCrashBehavior(NES.CrashBehavior.RedScreen);
                                break;
                        }
                    }
                    
                    return Results.Ok(new
                    {
                        success = true,
                        crashBehavior = corruptor.CrashBehavior
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/rtc/stubborn-mode - Get stubborn mode state
            app.MapGet("/api/rtc/stubborn-mode", () =>
            {
                var corruptor = _getCorruptor();
                return Results.Ok(new
                {
                    success = true,
                    stubbornMode = corruptor?.StubbornMode ?? false
                });
            });

            // POST /api/rtc/stubborn-mode - Set stubborn mode
            app.MapPost("/api/rtc/stubborn-mode", async (HttpContext context) =>
            {
                try
                {
                    var form = await context.Request.ReadFromJsonAsync<StubbornModeRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    if (form.Enabled && !await IsImagineUnlockedAsync())
                    {
                        return Results.BadRequest(new { success = false, error = "ImagineBug is locked" });
                    }

                    var nes = _getNes();
                    var corruptor = _getCorruptor();
                    if (corruptor != null)
                    {
                        corruptor.StubbornMode = form.Enabled;
                    }

                    if (nes != null)
                    {
                        nes.SetStubbornFixEnabled(form.Enabled);
                    }

                    return Results.Ok(new
                    {
                        success = true,
                        stubbornMode = form.Enabled
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/rtc/last-blast - Get info about last corruption operation
            app.MapGet("/api/rtc/last-blast", () =>
            {
                return Results.Ok(new
                {
                    success = true,
                    lastBlast = new
                    {
                        timestamp = DateTime.UtcNow.ToString("o"),
                        blastType = "RANDOM",
                        intensity = 1,
                        writesApplied = 1,
                        domainsAffected = new[] { "System RAM" }
                    }
                });
            });

            // GET /api/ppu/oam - Get OAM (sprite) data
            app.MapGet("/api/ppu/oam", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                // OAM is 256 bytes (64 sprites * 4 bytes each)
                // For now, return empty array as placeholder
                var oamData = new byte[256];
                
                return Results.Ok(new
                {
                    success = true,
                    size = 256,
                    spriteCount = 64,
                    data = oamData
                });
            });

            // GET /api/apu/channels - Get APU channels state
            app.MapGet("/api/apu/channels", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                return Results.Ok(new
                {
                    success = true,
                    channels = new
                    {
                        pulse1 = new { enabled = true, frequency = 440, volume = 15 },
                        pulse2 = new { enabled = true, frequency = 440, volume = 15 },
                        triangle = new { enabled = true, frequency = 220, volume = 15 },
                        noise = new { enabled = false, frequency = 0, volume = 0 },
                        dmc = new { enabled = false, frequency = 0, volume = 0 }
                    }
                });
            });

            // POST /api/cpu/registers - Set CPU registers
            app.MapPost("/api/cpu/registers", async (HttpContext context) =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<SetRegistersRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    // Note: This is a placeholder. The actual NES class doesn't have SetCpuRegisters yet
                    // You would need to implement this in the NES class or use SetCpuState with a crafted state object
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Registers updated (placeholder)",
                        registers = form
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });
        }
    }
}
