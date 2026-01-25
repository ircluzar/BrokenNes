using System;
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
            app.MapPost("/api/imagine/imagine-a-bug", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
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
        }
    }
}
