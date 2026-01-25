using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        private TimeJumpManager? _timeJumpManager;

        private void RegisterTimeJumpEndpoints(WebApplication app)
        {
            // POST /api/timejump/capture - Capture current state
            app.MapPost("/api/timejump/capture", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "NES not initialized" });
                }

                try
                {
                    // Lazy initialize TimeJumpManager
                    if (_timeJumpManager == null)
                    {
                        _timeJumpManager = new TimeJumpManager();
                        Console.WriteLine("[WebApi] TimeJumpManager initialized");
                    }

                    var result = _timeJumpManager.CaptureState(nes);
                    
                    if (result == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Failed to capture state" });
                    }

                    var stats = _timeJumpManager.GetStats();
                    return Results.Ok(new
                    {
                        success = true,
                        stateHash = result.Value.hash,
                        thumbnail = result.Value.thumbnail,
                        totalStates = stats.TotalStatesStored,
                        availableStates = stats.AvailableStates,
                        burnedStates = stats.BurnedStates
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebApi] TimeJump capture error: {ex.Message}");
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/timejump/jump - Perform a time jump
            app.MapPost("/api/timejump/jump", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "NES not initialized" });
                }

                if (_timeJumpManager == null)
                {
                    return Results.BadRequest(new { success = false, error = "No states captured yet" });
                }

                try
                {
                    var result = _timeJumpManager.Jump(nes);
                    
                    if (result == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Failed to perform jump - no available states" });
                    }

                    var stats = _timeJumpManager.GetStats();
                    return Results.Ok(new
                    {
                        success = true,
                        loadedHash = result.Value.loadedHash,
                        burnedHashes = result.Value.burnedHashes,
                        totalStates = stats.TotalStatesStored,
                        availableStates = stats.AvailableStates,
                        burnedStates = stats.BurnedStates
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebApi] TimeJump jump error: {ex.Message}");
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/timejump/stats - Get TimeJump statistics
            app.MapGet("/api/timejump/stats", () =>
            {
                try
                {
                    if (_timeJumpManager == null)
                    {
                        return Results.Ok(new
                        {
                            success = true,
                            totalStates = 0,
                            availableStates = 0,
                            burnedStates = 0
                        });
                    }

                    var stats = _timeJumpManager.GetStats();
                    return Results.Ok(new
                    {
                        success = true,
                        totalStates = stats.TotalStatesStored,
                        availableStates = stats.AvailableStates,
                        burnedStates = stats.BurnedStates
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebApi] TimeJump stats error: {ex.Message}");
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/timejump/reset - Reset TimeJump system
            app.MapPost("/api/timejump/reset", () =>
            {
                try
                {
                    if (_timeJumpManager != null)
                    {
                        _timeJumpManager.Reset();
                    }

                    return Results.Ok(new { success = true, message = "TimeJump system reset" });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebApi] TimeJump reset error: {ex.Message}");
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });
        }
    }
}
