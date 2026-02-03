using System;
using NesEmulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        /// <summary>
        /// Register Imagine (AI-Powered Corruption) API endpoints
        /// </summary>
        private void RegisterImagineEndpoints(WebApplication app)
        {
            // GET /api/imagine/model-loaded - Check if model is loaded
            app.MapGet("/api/imagine/model-loaded", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                return Results.Ok(new
                {
                    success = true,
                    modelLoaded = imagine.ModelLoaded
                });
            });

            // GET /api/imagine/epoch - Get current epoch number
            app.MapGet("/api/imagine/epoch", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                return Results.Ok(new
                {
                    success = true,
                    epoch = imagine.Epoch,
                    label = imagine.EpLabel
                });
            });

            // POST /api/imagine/epoch - Set epoch to load
            app.MapPost("/api/imagine/epoch", async (HttpContext context) =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<EpochRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    imagine.Epoch = form.Epoch;
                    
                    return Results.Ok(new
                    {
                        success = true,
                        epoch = imagine.Epoch
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/imagine/load-model - Load AI model by epoch
            app.MapPost("/api/imagine/load-model", async (HttpContext context) =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<EpochRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    bool loaded = imagine.LoadModel(form.Epoch);
                    
                    return Results.Ok(new
                    {
                        success = loaded,
                        modelLoaded = imagine.ModelLoaded,
                        epoch = imagine.Epoch,
                        label = imagine.EpLabel
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/imagine/generation-params - Get generation parameters
            app.MapGet("/api/imagine/generation-params", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                return Results.Ok(new
                {
                    success = true,
                    bytesToGenerate = imagine.BytesToGenerate,
                    temperature = imagine.Temperature,
                    topK = imagine.TopK
                });
            });

            // POST /api/imagine/generation-params - Set generation parameters
            app.MapPost("/api/imagine/generation-params", async (HttpContext context) =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<GenerationParamsRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    if (form.BytesToGenerate.HasValue)
                        imagine.BytesToGenerate = form.BytesToGenerate.Value;
                    
                    if (form.Temperature.HasValue)
                        imagine.Temperature = form.Temperature.Value;
                    
                    if (form.TopK.HasValue)
                        imagine.TopK = form.TopK.Value;
                    
                    return Results.Ok(new
                    {
                        success = true,
                        bytesToGenerate = imagine.BytesToGenerate,
                        temperature = imagine.Temperature,
                        topK = imagine.TopK
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/imagine/freeze-and-fetch - Capture CPU state snapshot
            app.MapPost("/api/imagine/freeze-and-fetch", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var snapshot = imagine.CaptureSnapshot();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Snapshot captured",
                        snapshot = new
                        {
                            snapshot.CpuCoreId,
                            snapshot.PC,
                            snapshot.A,
                            snapshot.X,
                            snapshot.Y,
                            snapshot.P,
                            snapshot.SP,
                            snapshot.IRQ,
                            snapshot.NMI,
                            snapshot.InPrgRom,
                            prev8 = snapshot.Prev8,
                            next16 = snapshot.Next16
                        }
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/imagine/cpu-snapshot - Read captured CPU state
            app.MapGet("/api/imagine/cpu-snapshot", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                if (imagine.Snapshot == null)
                {
                    return Results.BadRequest(new { success = false, error = "No snapshot captured" });
                }

                var snapshot = imagine.Snapshot;
                return Results.Ok(new
                {
                    success = true,
                    snapshot = new
                    {
                        snapshot.CpuCoreId,
                        snapshot.PC,
                        snapshot.A,
                        snapshot.X,
                        snapshot.Y,
                        snapshot.P,
                        snapshot.SP,
                        snapshot.IRQ,
                        snapshot.NMI,
                        snapshot.InPrgRom,
                        prev8 = snapshot.Prev8,
                        next16 = snapshot.Next16
                    }
                });
            });

            // POST /api/imagine/run-prediction - Generate predicted bytes from current state
            app.MapPost("/api/imagine/run-prediction", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var predictedBytes = imagine.PredictFromSnapshot();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        predictedBytes = predictedBytes,
                        length = predictedBytes.Length
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/imagine/apply-patch - Write predicted bytes to memory
            app.MapPost("/api/imagine/apply-patch", async (HttpContext context) =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<ApplyPatchRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    bool applied = imagine.ApplyPatch(form.Pc, form.Bytes);
                    
                    return Results.Ok(new
                    {
                        success = applied,
                        message = applied ? "Patch applied" : "Failed to apply patch",
                        error = applied ? null : imagine.LastError
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/imagine/imagine-a-bug - Automatic corruption using AI prediction
            app.MapPost("/api/imagine/imagine-a-bug", async (HttpContext context) =>
            {
                var imagine = _getImagineEngine();
                var nes = _getNes();
                var corruptor = _getCorruptor();
                if (imagine == null || nes == null || corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<ImagineBugRequest>();
                    bool loadOnImagine = form?.LoadOnImagine ?? false;

                    // Load base state if requested
                    if (loadOnImagine && corruptor.GhHasSelectedBase)
                    {
                        var baseState = corruptor.GhBaseStates.FirstOrDefault(b => b.Id == corruptor.GhSelectedBaseId);
                        if (baseState != null)
                        {
                            nes.LoadState(baseState.State);
                        }
                    }

                    bool success = imagine.ImagineBug();
                    
                    return Results.Ok(new
                    {
                        success = success,
                        message = success ? "Bug imagined successfully" : "Failed to imagine bug",
                        error = success ? null : imagine.LastError,
                        predictedBytes = imagine.PredictedBytes
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/imagine/imagine-targeted-bug - Targeted scanline corruption using AI prediction
            app.MapPost("/api/imagine/imagine-targeted-bug", async (HttpContext context) =>
            {
                var imagine = _getImagineEngine();
                var nes = _getNes();
                var corruptor = _getCorruptor();
                if (imagine == null || nes == null || corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<TargetedImagineBugRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    // Load base state if requested
                    if (form.LoadOnImagine && corruptor.GhHasSelectedBase)
                    {
                        var baseState = corruptor.GhBaseStates.FirstOrDefault(b => b.Id == corruptor.GhSelectedBaseId);
                        if (baseState != null)
                        {
                            nes.LoadState(baseState.State);
                        }
                    }

                    // Apply targeted config
                    var config = new ImagineTargetConfig
                    {
                        Mode = Enum.TryParse<ImagineTargetMode>(form.Mode, out var mode) ? mode : ImagineTargetMode.SingleScanline,
                        TargetScanline = form.TargetScanline,
                        RangeStart = form.RangeStart,
                        RangeEnd = form.RangeEnd
                    };

                    // Normalize range
                    if (config.RangeStart > config.RangeEnd)
                    {
                        int tmp = config.RangeStart;
                        config.RangeStart = config.RangeEnd;
                        config.RangeEnd = tmp;
                    }

                    // Set the config property (this calls ApplyImagineTargetConfig internally)
                    nes.ImagineTargetConfig = config;

                    // Run one frame to capture and trigger imagine
                    nes.RunFrame();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Targeted bug imagined",
                        predictedBytes = imagine.PredictedBytes,
                        lastCapture = nes.LastImagineCapture != null ? new
                        {
                            scanline = nes.LastImagineCapture.Scanline,
                            pc = nes.LastImagineCapture.PC,
                            framePhase = nes.LastImagineCapture.FramePhase.ToString()
                        } : null
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/imagine/predicted-bytes - Get last AI prediction result
            app.MapGet("/api/imagine/predicted-bytes", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                if (imagine.PredictedBytes == null)
                {
                    return Results.Ok(new
                    {
                        success = true,
                        predictedBytes = (byte[]?)null,
                        length = 0
                    });
                }

                return Results.Ok(new
                {
                    success = true,
                    predictedBytes = imagine.PredictedBytes,
                    length = imagine.PredictedBytes.Length
                });
            });

            // GET /api/imagine/last-error - Get last Imagine error message
            app.MapGet("/api/imagine/last-error", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                return Results.Ok(new
                {
                    success = true,
                    lastError = imagine.LastError
                });
            });

            // POST /api/imagine/set-targeted-mode - Configure scanline targeting
            app.MapPost("/api/imagine/set-targeted-mode", async (HttpContext context) =>
            {
                try
                {
                    var body = await context.Request.ReadFromJsonAsync<TargetedImagineRequest>();
                    if (body == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request body" });
                    }

                    var nes = _getNes();
                    if (nes == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                    }

                    ImagineTargetConfig? config = null;
                    if (body.Enabled)
                    {
                        var mode = Enum.TryParse<ImagineTargetMode>(body.Mode, true, out var parsedMode)
                            ? parsedMode
                            : ImagineTargetMode.SingleScanline;
                        config = new ImagineTargetConfig
                        {
                            Mode = mode,
                            TargetScanline = body.TargetScanline,
                            RangeStart = body.RangeStart,
                            RangeEnd = body.RangeEnd,
                            Enabled = true
                        };

                        if (config.RangeStart > config.RangeEnd)
                        {
                            int tmp = config.RangeStart;
                            config.RangeStart = config.RangeEnd;
                            config.RangeEnd = tmp;
                        }
                    }

                    nes.ImagineTargetConfig = config;

                    var imagine = _getImagineEngine();
                    try { imagine?.SetupTargetedImagine(); } catch { }

                    // Switch to PPU_IMG if targeting enabled and not already using it
                    if (body.Enabled && nes.GetPpuCoreId() != "PPU_IMG")
                    {
                        try
                        {
                            var ppuState = nes.GetPpuState();
                            nes.SetPpuCore("IMG");
                            if (ppuState != null)
                            {
                                nes.SetPpuState(ppuState);
                            }
                        }
                        catch (Exception ex)
                        {
                            return Results.BadRequest(new
                            {
                                success = false,
                                error = $"Failed to switch to PPU_IMG: {ex.Message}"
                            });
                        }
                    }

                    var configDto = config == null ? null : new
                    {
                        mode = config.Mode.ToString(),
                        targetScanline = config.TargetScanline,
                        rangeStart = config.RangeStart,
                        rangeEnd = config.RangeEnd,
                        enabled = config.Enabled
                    };
                    var lastCapture = nes.LastImagineCapture;
                    var lastCaptureDto = lastCapture == null ? null : new
                    {
                        scanline = lastCapture.Scanline,
                        framePhase = lastCapture.FramePhase.ToString(),
                        timestamp = lastCapture.Timestamp,
                        pc = lastCapture.PC,
                        a = lastCapture.A,
                        x = lastCapture.X,
                        y = lastCapture.Y,
                        p = lastCapture.P,
                        sp = lastCapture.SP
                    };

                    return Results.Ok(new
                    {
                        success = true,
                        config = configDto,
                        ppuCore = nes.GetPpuCoreId(),
                        lastCapture = lastCaptureDto
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/imagine/targeted-status - Get current targeted imagine status
            app.MapGet("/api/imagine/targeted-status", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                var config = nes.ImagineTargetConfig;
                var configDto = config == null ? null : new
                {
                    mode = config.Mode.ToString(),
                    targetScanline = config.TargetScanline,
                    rangeStart = config.RangeStart,
                    rangeEnd = config.RangeEnd,
                    enabled = config.Enabled
                };
                var lastCapture = nes.LastImagineCapture;
                var lastCaptureDto = lastCapture == null ? null : new
                {
                    scanline = lastCapture.Scanline,
                    framePhase = lastCapture.FramePhase.ToString(),
                    timestamp = lastCapture.Timestamp,
                    pc = lastCapture.PC,
                    a = lastCapture.A,
                    x = lastCapture.X,
                    y = lastCapture.Y,
                    p = lastCapture.P,
                    sp = lastCapture.SP
                };

                return Results.Ok(new
                {
                    success = true,
                    enabled = config != null,
                    config = configDto,
                    ppuCore = nes.GetPpuCoreId(),
                    isImgCore = nes.GetPpuCoreId() == "PPU_IMG",
                    lastCapture = lastCaptureDto
                });
            });
        }

        private record ImagineBugRequest(bool LoadOnImagine);

        private record TargetedImagineBugRequest(
            bool LoadOnImagine,
            string Mode,
            int TargetScanline,
            int RangeStart,
            int RangeEnd
        );

        private record TargetedImagineRequest(
            bool Enabled,
            string Mode,
            int TargetScanline,
            int RangeStart,
            int RangeEnd
        );
    }
}
