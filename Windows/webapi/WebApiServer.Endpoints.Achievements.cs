using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        /// <summary>
        /// Helper to serialize achievement operands for API responses
        /// </summary>
        private static object SerializeOperand(NesEmulator.RetroAchievements.Operand operand)
        {
            if (operand.Kind == NesEmulator.RetroAchievements.OperandKind.Constant)
            {
                return new
                {
                    kind = "Constant",
                    value = operand.Const.Kind == NesEmulator.RetroAchievements.ValueKind.Float 
                        ? (object)operand.Const.F64 
                        : operand.Const.I64
                };
            }
            else if (operand.Kind == NesEmulator.RetroAchievements.OperandKind.Memory && operand.Mem != null)
            {
                return new
                {
                    kind = "Memory",
                    prefix = operand.Mem.Prefix.ToString(),
                    address = operand.Mem.Address,
                    useDelta = operand.Mem.UseDelta,
                    usePrior = operand.Mem.UsePrior,
                    useBcd = operand.Mem.UseBcd,
                    useInvert = operand.Mem.UseInvert
                };
            }
            else if (operand.Kind == NesEmulator.RetroAchievements.OperandKind.Recall)
            {
                return new { kind = "Recall" };
            }
            else
            {
                return new { kind = operand.Kind.ToString() };
            }
        }

        private sealed class ContinueDbGame
        {
            public string? Id { get; set; }
            public string? Title { get; set; }
            public string? CommonName { get; set; }
            public string? RomKey { get; set; }
        }

        private sealed class ContinueDbAchievement
        {
            public string? Id { get; set; }
            public string? GameId { get; set; }
            public string? Title { get; set; }
            public string? MetaAchievementName { get; set; }
            public List<object>? Requirements { get; set; }
        }

        private sealed class MetaGamesAchievement
        {
            public string? Description { get; set; }
            public string? Formula { get; set; }
        }

        private sealed class MetaGamesEntry
        {
            public string? Title { get; set; }
            public List<MetaGamesAchievement>? Achievements { get; set; }
        }

        private class AchievementMetadata
        {
            public string Title { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public int Points { get; set; } = 0;
        }

        private Dictionary<string, AchievementMetadata> _achievementMetadata = new Dictionary<string, AchievementMetadata>();

        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static string? FindContinueDbPath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "Webmodules", "shared", "models", "default-db.json"),
                Path.Combine(baseDir, "wwwroot", "models", "default-db.json"),
                Path.Combine(baseDir, "models", "default-db.json")
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private static string? FindMetaGamesPath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "Webmodules", "shared", "models", "meta_games.json"),
                Path.Combine(baseDir, "wwwroot", "models", "meta_games.json"),
                Path.Combine(baseDir, "models", "meta_games.json")
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>
        /// Register Achievement API endpoints
        /// </summary>
        private void RegisterAchievementsEndpoints(WebApplication app)
        {
            // POST /api/achievements/init - Initialize achievements engine
            app.MapPost("/api/achievements/init", async (HttpContext context) =>
            {
                var existing = _getAchievementsEngine();
                if (existing != null)
                {
                    return Results.Ok(new
                    {
                        success = true,
                        initialized = true,
                        alreadyInitialized = true,
                        count = existing.GetAll().Count()
                    });
                }

                // Get current ROM name from emulator
                var nes = _getNes();
                if (nes == null || string.IsNullOrWhiteSpace(nes.RomName))
                {
                    return Results.BadRequest(new { success = false, error = "No ROM is currently loaded" });
                }

                var currentRomName = nes.RomName;

                // Load continueDb data
                var continueDbPath = FindContinueDbPath();
                if (string.IsNullOrWhiteSpace(continueDbPath))
                {
                    return Results.BadRequest(new { success = false, error = "default-db.json (continueDb) not found" });
                }

                // Load meta_games for formula mapping
                var metaGamesPath = FindMetaGamesPath();
                if (string.IsNullOrWhiteSpace(metaGamesPath))
                {
                    return Results.BadRequest(new { success = false, error = "meta_games.json not found" });
                }

                try
                {
                    // Parse continueDb
                    using var continueDbStream = File.OpenRead(continueDbPath);
                    var continueDbRoot = await JsonSerializer.DeserializeAsync<JsonElement>(continueDbStream, s_jsonOptions);
                    
                    if (!continueDbRoot.TryGetProperty("data", out var dataElement))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid continueDb structure" });
                    }

                    // Find the game by romKey
                    ContinueDbGame? matchedGame = null;
                    if (dataElement.TryGetProperty("games", out var gamesArray))
                    {
                        foreach (var gameEl in gamesArray.EnumerateArray())
                        {
                            var romKey = gameEl.TryGetProperty("romKey", out var rk) ? rk.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(romKey) && 
                                string.Equals(romKey, currentRomName, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedGame = new ContinueDbGame
                                {
                                    Id = gameEl.TryGetProperty("id", out var id) ? id.GetString() : null,
                                    Title = gameEl.TryGetProperty("title", out var t) ? t.GetString() : null,
                                    CommonName = gameEl.TryGetProperty("commonName", out var cn) ? cn.GetString() : null,
                                    RomKey = romKey
                                };
                                break;
                            }
                        }
                    }

                    if (matchedGame == null || string.IsNullOrWhiteSpace(matchedGame.Id))
                    {
                        return Results.BadRequest(new { success = false, error = $"No game found in continueDb for ROM '{currentRomName}'" });
                    }

                    // Get achievements for this game from continueDb
                    var gameAchievements = new List<ContinueDbAchievement>();
                    if (dataElement.TryGetProperty("achievements", out var achievementsArray))
                    {
                        foreach (var achEl in achievementsArray.EnumerateArray())
                        {
                            var gameId = achEl.TryGetProperty("gameId", out var gid) ? gid.GetString() : null;
                            if (string.Equals(gameId, matchedGame.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                gameAchievements.Add(new ContinueDbAchievement
                                {
                                    Id = achEl.TryGetProperty("id", out var id) ? id.GetString() : null,
                                    GameId = gameId,
                                    Title = achEl.TryGetProperty("title", out var t) ? t.GetString() : null,
                                    MetaAchievementName = achEl.TryGetProperty("metaAchievementName", out var man) ? man.GetString() : null
                                });
                            }
                        }
                    }

                    if (gameAchievements.Count == 0)
                    {
                        return Results.BadRequest(new { success = false, error = $"No achievements found for game '{matchedGame.Title}' in continueDb" });
                    }

                    // Load meta_games to get formulas
                    using var metaGamesStream = File.OpenRead(metaGamesPath);
                    var metaGamesList = await JsonSerializer.DeserializeAsync<List<MetaGamesEntry>>(metaGamesStream, s_jsonOptions)
                           ?? new List<MetaGamesEntry>();

                    // Build a mapping from achievement description to formula
                    var formulaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var metaGame in metaGamesList)
                    {
                        if (metaGame.Achievements == null) continue;
                        foreach (var metaAch in metaGame.Achievements)
                        {
                            if (!string.IsNullOrWhiteSpace(metaAch.Description) && !string.IsNullOrWhiteSpace(metaAch.Formula))
                            {
                                formulaMap[metaAch.Description] = metaAch.Formula;
                            }
                        }
                    }

                    // Create achievements with formulas
                    var list = new List<(string id, string formula)>();
                    _achievementMetadata.Clear();

                    foreach (var ach in gameAchievements)
                    {
                        var metaName = ach.MetaAchievementName ?? ach.Title ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(metaName)) continue;

                        // Try to find formula in meta_games
                        if (!formulaMap.TryGetValue(metaName, out var formula) || string.IsNullOrWhiteSpace(formula))
                        {
                            // Skip achievements without formulas
                            continue;
                        }

                        var achId = ach.Id ?? $"ach_{list.Count + 1}";
                        list.Add((achId, formula));

                        _achievementMetadata[achId] = new AchievementMetadata
                        {
                            Title = ach.Title ?? metaName,
                            Description = metaName,
                            Points = 10
                        };
                    }

                    if (list.Count == 0)
                    {
                        return Results.BadRequest(new { success = false, error = $"No achievements with formulas found for '{matchedGame.Title}'" });
                    }

                    // Initialize the engine
                    var engine = new NesEmulator.RetroAchievements.AchievementsEngine(
                        new NesEmulator.RetroAchievements.NesRamDomainRef(_getNes));
                    engine.Load(list);
                    _setAchievementsEngine(engine);

                    var gameDisplayName = !string.IsNullOrWhiteSpace(matchedGame.CommonName) 
                        ? matchedGame.CommonName 
                        : matchedGame.Title ?? currentRomName;

                    return Results.Ok(new
                    {
                        success = true,
                        initialized = true,
                        alreadyInitialized = false,
                        count = list.Count,
                        gameTitle = gameDisplayName,
                        gameId = matchedGame.Id,
                        romKey = matchedGame.RomKey,
                        source = "continueDb + meta_games"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = $"Failed to initialize achievements: {ex.Message}" });
                }
            });

            // GET /api/achievements/list - Retrieve achievements for current game
            app.MapGet("/api/achievements/list", () =>
            {
                var achEngine = _getAchievementsEngine();
                if (achEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Achievements engine not initialized" });
                }

                try
                {
                    var achievements = achEngine.GetAll().Select(a => {
                        var meta = _achievementMetadata.TryGetValue(a.Id, out var m) ? m : new AchievementMetadata();
                        
                        return new
                        {
                            id = a.Id,
                            title = !string.IsNullOrEmpty(meta.Title) ? meta.Title : $"Achievement {a.Id}",
                            description = meta.Description,
                            points = meta.Points,
                            isCompleted = a.Unlocked,
                            
                            formula = a.Formula,
                            unlocked = a.Unlocked,
                            primed = a.Primed,
                            measuredActive = a.MeasuredActive,
                            measuredCurrent = a.MeasuredCurrent,
                            measuredTarget = a.MeasuredTarget,
                            measuredIsPercent = a.MeasuredIsPercent
                        };
                    }).ToList();

                    return Results.Ok(new
                    {
                        success = true,
                        achievements = achievements,
                        count = achievements.Count
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/achievements/state/{id} - Check unlock status
            app.MapGet("/api/achievements/state/{id}", (string id) =>
            {
                var achEngine = _getAchievementsEngine();
                if (achEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Achievements engine not initialized" });
                }

                try
                {
                    var ach = achEngine.Get(id);
                    if (ach == null)
                    {
                        return Results.NotFound(new { success = false, error = $"Achievement '{id}' not found" });
                    }

                    return Results.Ok(new
                    {
                        success = true,
                        id = ach.Id,
                        unlocked = ach.Unlocked,
                        primed = ach.Primed
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/achievements/progress/{id} - Read progress (hits, measured values, etc.)
            app.MapGet("/api/achievements/progress/{id}", (string id) =>
            {
                var achEngine = _getAchievementsEngine();
                if (achEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Achievements engine not initialized" });
                }

                try
                {
                    var ach = achEngine.Get(id);
                    if (ach == null)
                    {
                        return Results.NotFound(new { success = false, error = $"Achievement '{id}' not found" });
                    }

                    return Results.Ok(new
                    {
                        success = true,
                        id = ach.Id,
                        unlocked = ach.Unlocked,
                        primed = ach.Primed,
                        measuredActive = ach.MeasuredActive,
                        measuredCurrent = ach.MeasuredCurrent,
                        measuredTarget = ach.MeasuredTarget,
                        measuredIsPercent = ach.MeasuredIsPercent,
                        conditionCount = ach.Conditions.Count,
                        conditions = ach.Conditions.Select(c => new
                        {
                            hits = c.Hits,
                            isMet = c.IsMet
                        }).ToList()
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/achievements/conditions/{id} - Read condition details for debugging
            app.MapGet("/api/achievements/conditions/{id}", (string id) =>
            {
                var achEngine = _getAchievementsEngine();
                if (achEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Achievements engine not initialized" });
                }

                try
                {
                    var ach = achEngine.Get(id);
                    if (ach == null)
                    {
                        return Results.NotFound(new { success = false, error = $"Achievement '{id}' not found" });
                    }

                    return Results.Ok(new
                    {
                        success = true,
                        id = ach.Id,
                        formula = ach.Formula,
                        conditions = ach.Conditions.Select(c => new
                        {
                            flag = c.Flag.ToString(),
                            left = SerializeOperand(c.Left),
                            op = c.Op.ToString(),
                            right = SerializeOperand(c.Right),
                            hitTarget = c.HitTarget,
                            hits = c.Hits,
                            isMet = c.IsMet
                        }).ToList()
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/achievements/force-complete - Debug: manually unlock achievement
            app.MapPost("/api/achievements/force-complete", async (HttpContext context) =>
            {
                var achEngine = _getAchievementsEngine();
                if (achEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Achievements engine not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<AchievementIdRequest>();
                    if (form == null || string.IsNullOrEmpty(form.Id))
                    {
                        return Results.BadRequest(new { success = false, error = "Achievement ID is required" });
                    }

                    bool completed = achEngine.ForceComplete(form.Id);
                    if (!completed)
                    {
                        return Results.NotFound(new { success = false, error = $"Achievement '{form.Id}' not found" });
                    }

                    return Results.Ok(new
                    {
                        success = true,
                        id = form.Id,
                        message = "Achievement force completed"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/achievements/evaluate-frame - Run achievement evaluation step
            app.MapPost("/api/achievements/evaluate-frame", () =>
            {
                var achEngine = _getAchievementsEngine();
                if (achEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Achievements engine not initialized" });
                }

                try
                {
                    var unlocked = achEngine.EvaluateFrame();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        unlockedThisFrame = unlocked,
                        unlockedCount = unlocked.Count
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
