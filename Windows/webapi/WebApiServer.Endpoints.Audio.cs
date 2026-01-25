using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        /// <summary>
        /// Register Audio Engine API endpoints
        /// </summary>
        private void RegisterAudioEndpoints(WebApplication app)
        {
            // GET /api/audio/music/current - Get currently playing music
            app.MapGet("/api/audio/music/current", () =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                return Results.Ok(new
                {
                    success = true,
                    currentFile = audioEngine.CurrentMusicFile,
                    isPlaying = audioEngine.IsMusicPlaying
                });
            });

            // GET /api/audio/music/list - List available music files
            app.MapGet("/api/audio/music/list", () =>
            {
                try
                {
                    var musicFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "music");
                    if (!Directory.Exists(musicFolder))
                    {
                        return Results.Ok(new { success = true, files = Array.Empty<string>() });
                    }

                    var files = Directory.GetFiles(musicFolder)
                        .Select(Path.GetFileName)
                        .Where(f => f != null && (f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(f => f)
                        .ToArray();

                    return Results.Ok(new { success = true, files });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/audio/sfx/list - List available SFX files
            app.MapGet("/api/audio/sfx/list", () =>
            {
                try
                {
                    var sfxFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "sfx");
                    if (!Directory.Exists(sfxFolder))
                    {
                        return Results.Ok(new { success = true, files = Array.Empty<string>() });
                    }

                    var files = Directory.GetFiles(sfxFolder)
                        .Select(Path.GetFileName)
                        .Where(f => f != null && (f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(f => f)
                        .ToArray();

                    return Results.Ok(new { success = true, files });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/audio/sfx/play - Play a sound effect
            app.MapPost("/api/audio/sfx/play", async (HttpContext context) =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<AudioPlayRequest>();
                    if (body == null || string.IsNullOrWhiteSpace(body.Filename))
                    {
                        return Results.BadRequest(new { success = false, error = "Filename is required" });
                    }

                    await audioEngine.PlaySfxAsync(body.Filename);
                    return Results.Ok(new { success = true, filename = body.Filename });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/audio/music/play - Play music directly
            app.MapPost("/api/audio/music/play", async (HttpContext context) =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<AudioPlayRequest>();
                    if (body == null || string.IsNullOrWhiteSpace(body.Filename))
                    {
                        return Results.BadRequest(new { success = false, error = "Filename is required" });
                    }

                    var loop = body.Loop ?? true;
                    await audioEngine.PlayMusicAsync(body.Filename, loop);
                    return Results.Ok(new { success = true, filename = body.Filename, loop });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/audio/music/request - Request music with crossfade
            app.MapPost("/api/audio/music/request", async (HttpContext context) =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<AudioPlayRequest>();
                    if (body == null || string.IsNullOrWhiteSpace(body.Filename))
                    {
                        return Results.BadRequest(new { success = false, error = "Filename is required" });
                    }

                    var loop = body.Loop ?? true;
                    var fadeDurationMs = body.FadeDurationMs ?? 1000;
                    await audioEngine.RequestMusicAsync(body.Filename, loop, fadeDurationMs);
                    return Results.Ok(new { success = true, filename = body.Filename, loop, fadeDurationMs });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/audio/music/stop - Stop music with fade-out
            app.MapPost("/api/audio/music/stop", async (HttpContext context) =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<AudioPlayRequest>();
                    var fadeDurationMs = body?.FadeDurationMs ?? 1000;
                    await audioEngine.StopMusicAsync(fadeDurationMs);
                    return Results.Ok(new { success = true, fadeDurationMs });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/audio/volume - Get current volume levels
            app.MapGet("/api/audio/volume", () =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                return Results.Ok(new
                {
                    success = true,
                    musicVolume = audioEngine.MusicVolume,
                    sfxVolume = audioEngine.SfxVolume
                });
            });

            // GET /api/audio/status - Get audio engine status
            app.MapGet("/api/audio/status", () =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                return Results.Ok(new
                {
                    success = true,
                    currentMusicFile = audioEngine.CurrentMusicFile,
                    isMusicPlaying = audioEngine.IsMusicPlaying,
                    musicVolume = audioEngine.MusicVolume,
                    sfxVolume = audioEngine.SfxVolume
                });
            });

            // POST /api/audio/volume - Set volume levels
            app.MapPost("/api/audio/volume", async (HttpContext context) =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<AudioVolumeRequest>();
                    if (body == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Volume data is required" });
                    }

                    if (body.MusicVolume.HasValue)
                        audioEngine.MusicVolume = body.MusicVolume.Value;
                    
                    if (body.SfxVolume.HasValue)
                        audioEngine.SfxVolume = body.SfxVolume.Value;

                    return Results.Ok(new
                    {
                        success = true,
                        musicVolume = audioEngine.MusicVolume,
                        sfxVolume = audioEngine.SfxVolume
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
