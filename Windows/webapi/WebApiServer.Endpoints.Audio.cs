using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public sealed class VolumeRequest
    {
        public int? Volume { get; set; }
        public bool? Muted { get; set; }
        public bool? RemoveHighPitched { get; set; }
        public int? HighPitchCeilingNote { get; set; }
    }

    public partial class WebApiServer
    {
        /// <summary>Emulation volume (percent), mute and "Remove high-pitched", as the menu bar's volume panel shows them.</summary>
        public Func<object>? GetEmulationVolume { get; set; }
        /// <summary>Change any of the volume panel's settings; null fields are left alone.</summary>
        public Action<VolumeRequest>? SetEmulationVolume { get; set; }

        /// <summary>
        /// Register Audio Engine API endpoints
        /// </summary>
        private void RegisterAudioEndpoints(WebApplication app)
        {
            // GET /api/audio/emulation-volume - emulation output volume (0-100), mute, and the high-pitch guard
            app.MapGet("/api/audio/emulation-volume", () =>
                GetEmulationVolume == null ? Results.BadRequest(new { success = false, error = "not available" })
                                           : Results.Ok(new { success = true, state = GetEmulationVolume() }));

            // POST /api/audio/emulation-volume {"volume": 0-100, "muted": bool, "removeHighPitched": bool, "highPitchCeilingNote": MIDI note}
            // - any field may be left out
            app.MapPost("/api/audio/emulation-volume", async (HttpContext context) =>
            {
                if (GetEmulationVolume == null || SetEmulationVolume == null) return Results.BadRequest(new { success = false, error = "not available" });
                var body = await context.Request.ReadFromJsonAsync<VolumeRequest>();
                if (body == null || (body.Volume == null && body.Muted == null && body.RemoveHighPitched == null && body.HighPitchCeilingNote == null))
                    return Results.BadRequest(new { success = false, error = "volume, muted, removeHighPitched and/or highPitchCeilingNote is required" });
                SetEmulationVolume(body);
                return Results.Ok(new { success = true, state = GetEmulationVolume() });
            });

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
