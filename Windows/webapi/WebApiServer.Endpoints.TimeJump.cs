using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    // Request model for query endpoint
    public class QueryRequest
    {
        public string hash { get; set; } = string.Empty;
    }

    public partial class WebApiServer
    {
        private TimeJumpManager? _timeJumpManager;

        private void RegisterTimeJumpEndpoints(WebApplication app)
        {
            // POST /api/timejump/start - Start TimeJump mode (hides menu, resets game)
            app.MapPost("/api/timejump/start", () =>
            {
                try
                {
                    // Reset the game when starting TimeJump
                    if (_resetGame != null)
                    {
                        if (_uiControl != null && _uiControl.InvokeRequired)
                        {
                            _uiControl.Invoke(_resetGame);
                        }
                        else
                        {
                            _resetGame();
                        }
                        Console.WriteLine("[WebApi] Game reset for TimeJump");
                    }
                    
                    // Hide the menu bar when starting TimeJump
                    if (_hideMenu != null)
                    {
                        if (_uiControl != null && _uiControl.InvokeRequired)
                        {
                            _uiControl.Invoke(_hideMenu);
                        }
                        else
                        {
                            _hideMenu();
                        }
                    }

                    Console.WriteLine("[WebApi] TimeJump mode started - menu hidden, game reset");
                    return Results.Ok(new { success = true, message = "TimeJump mode started" });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebApi] TimeJump start error: {ex.Message}");
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/timejump/reset - Reset TimeJump (clear states, reset game, back to level 0)
            app.MapPost("/api/timejump/reset", () =>
            {
                try
                {
                    // Clear TimeJump manager caches
                    if (_timeJumpManager != null)
                    {
                        _timeJumpManager.Reset();
                        Console.WriteLine("[WebApi] TimeJump caches cleared for reset");
                    }
                    
                    // Reset the game
                    if (_resetGame != null)
                    {
                        if (_uiControl != null && _uiControl.InvokeRequired)
                        {
                            _uiControl.Invoke(_resetGame);
                        }
                        else
                        {
                            _resetGame();
                        }
                        Console.WriteLine("[WebApi] Game reset for TimeJump reset");
                    }

                    Console.WriteLine("[WebApi] TimeJump reset complete");
                    return Results.Ok(new { success = true, message = "TimeJump reset complete" });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebApi] TimeJump reset error: {ex.Message}");
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/timejump/stop - Stop TimeJump mode (shows menu, clears caches)
            app.MapPost("/api/timejump/stop", () =>
            {
                try
                {
                    // Clear caches
                    if (_timeJumpManager != null)
                    {
                        _timeJumpManager.Reset();
                        Console.WriteLine("[WebApi] TimeJump caches cleared");
                    }

                    // Show the menu bar when stopping TimeJump
                    if (_showMenu != null)
                    {
                        if (_uiControl != null && _uiControl.InvokeRequired)
                        {
                            _uiControl.Invoke(_showMenu);
                        }
                        else
                        {
                            _showMenu();
                        }
                    }

                    Console.WriteLine("[WebApi] TimeJump mode stopped - menu shown, caches cleared");
                    return Results.Ok(new { success = true, message = "TimeJump mode stopped, caches cleared" });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebApi] TimeJump stop error: {ex.Message}");
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/timejump/validate-rom - Validate that a valid ROM is loaded
            app.MapGet("/api/timejump/validate-rom", () =>
            {
                var nes = _getNes();
                
                // Check if NES is initialized
                if (nes == null)
                {
                    return Results.Ok(new 
                    { 
                        valid = false, 
                        error = "No emulator instance found" 
                    });
                }

                // Check if a ROM is loaded
                if (string.IsNullOrEmpty(nes.RomPath))
                {
                    return Results.Ok(new 
                    { 
                        valid = false, 
                        error = "No ROM is currently loaded" 
                    });
                }

                // Check if it's the test ROM (you can adjust this check based on your test ROM naming)
                var romName = System.IO.Path.GetFileName(nes.RomPath).ToLowerInvariant();
                if (romName.Contains("test") || romName.Contains("demo"))
                {
                    return Results.Ok(new 
                    { 
                        valid = false, 
                        error = "TimeJump cannot be used with test ROMs" 
                    });
                }

                return Results.Ok(new { valid = true });
            });

            // POST /api/timejump/capture - Capture current state atomically at frame boundary
            app.MapPost("/api/timejump/capture", async () =>
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

                    // Use atomic frame-boundary capture to prevent desync
                    var result = await _timeJumpManager.CaptureStateAsync(nes);
                    
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

            // POST /api/timejump/query - Query and load a state similar to a specific state
            app.MapPost("/api/timejump/query", async (HttpContext context) =>
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
                    // Parse request body
                    var request = await context.Request.ReadFromJsonAsync<QueryRequest>();
                    
                    if (request == null || string.IsNullOrEmpty(request.hash))
                    {
                        return Results.BadRequest(new { success = false, error = "Missing hash parameter" });
                    }

                    var result = _timeJumpManager.QueryState(nes, request.hash);
                    
                    if (result == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Failed to perform query - state not found or insufficient available states" });
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
                    Console.WriteLine($"[WebApi] TimeJump query error: {ex.Message}");
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
        }
    }
}
