using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using OfflineRA.Models;

class RetroAchievementsFetcher
{
    private static readonly string apiKey = "cVsc8qQQGHpKOmqEs6wT3A6yHn2rF840";
    private static readonly string username = "ircluzar";
    private static readonly string baseUrl = "https://retroachievements.org/API/";
    private static readonly string[] PriorityKeywords = new[] { "mario", "zelda", "kirby", "mega", "ice", "island" };

    static async Task Main()
    {
        Console.Write("This will download NES achievement data. Continue? (y/n): ");
        var input = Console.ReadLine();
        if (!string.Equals(input, "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Operation cancelled by user.");
            return;
        }

        // Ensure output directory exists
        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "achievements");
        Directory.CreateDirectory(outDir);

        var nesGames = FetchNESGameIds();

        // Reorder games to prioritize titles matching select keywords (left-to-right priority)
        nesGames = OrderByKeywordPriority(nesGames).ToList();

        int total = nesGames.Count;
        for (int index = 0; index < nesGames.Count; index++)
        {
            var game = nesGames[index];
            string fileName = $"NES_Game_{game.Id}_Achievements.json";
            var outPath = Path.Combine(outDir, fileName);
            string progress = $"{index + 1}/{total} {(((index + 1) * 100.0) / Math.Max(1, total)):F1}%";

            if (File.Exists(outPath))
            {
                Console.WriteLine($"{progress} Already exists: {fileName}, skipping download.");
                continue;
            }

            var achievementData = FetchGameAchievements(game.Id);
            if (achievementData != null)
            {
                File.WriteAllText(outPath, achievementData);
                Console.WriteLine($"{progress} Saved: {fileName}");

                // Pause 2 seconds between downloads to avoid hitting rate limits
                await Task.Delay(2000);
            }
        }

        // After fetching achievements, load and parse all formulas JSON files
        int failed;
        var formulaFiles = LoadFormulaFiles(out failed);
        Console.WriteLine($"Parsed {formulaFiles.Count} formulas file(s); {failed} failed.");

        var invalid = formulaFiles.Where(it => it.PatchData == null).ToList();
        new object();


        // Optional small summary
        int totalAchievements = formulaFiles.Sum(f => f.PatchData?.Achievements?.Count ?? 0);
        int totalLeaderboards = formulaFiles.Sum(f => f.PatchData?.Leaderboards?.Count ?? 0);
        int totalCodeNotes = formulaFiles.Sum(f => f.CodeNotes?.Count ?? 0);
        Console.WriteLine($"Totals - Achievements: {totalAchievements}, Leaderboards: {totalLeaderboards}, CodeNotes: {totalCodeNotes}");

        // Parse all downloaded achievement JSON files into a dictionary keyed by Title
        var parsedAchievementsByTitle = LoadAchievementFilesByTitle(out var parseFailedCount);
        Console.WriteLine($"Parsed {parsedAchievementsByTitle.Count} achievement file(s) by title; {parseFailedCount} failed.");

        // Show a quick sample to verify the model alignment
        if (parsedAchievementsByTitle.Count > 0)
        {
            var sample = parsedAchievementsByTitle.First().Value;
            Console.WriteLine($"Sample -> Title: '{sample.Title}', Achievements: {sample.Achievements?.Count ?? 0}, Leaderboards: {sample.Leaderboards?.Count ?? 0}");
        }


        // Build final container list
        var metaGames = new List<MetaGameData>();

        //the buildup
        foreach (var formula in formulaFiles)
        {
            var title = formula.PatchData?.Title;
            if (string.IsNullOrWhiteSpace(title))
                continue;

            // Use the achievements embedded in the formulas file (AchievementFormula), not the downloaded GameExtended achievements
            var formulaAchievements = formula.PatchData?.Achievements;
            if (formulaAchievements == null || formulaAchievements.Count == 0)
            {
                // nothing to export for this formula
                continue;
            }

            var metaGame = new MetaGameData { Title = title };

            foreach (var fa in formulaAchievements)
            {
                // Copy Description and MemAddr -> Formula from the formula file
                metaGame.Achievements.Add(new MetaGameAchievement
                {
                    Description = fa.Description,
                    Formula = fa.MemAddr
                });
            }

            metaGames.Add(metaGame);
        }

        // Serialize to JSON file
        Console.WriteLine($"Built {metaGames.Count} MetaGameData object(s).");
        try
        {
            var outPath = Path.Combine(Directory.GetCurrentDirectory(), "meta_games.json");
            var json = JsonSerializer.Serialize(metaGames, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outPath, json);
            Console.WriteLine($"Wrote: {outPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to write meta_games.json: {ex.Message}");
        }

        //OUTPUT OBJECTS HERE

    }

    // Build GameData objects from local page1.json .. page6.json
    static List<GameData> FetchNESGameIds()
    {
        var gamesById = new Dictionary<int, GameData>();
        var baseDir = AppContext.BaseDirectory;

        for (int i = 1; i <= 6; i++)
        {
            var filePath = Path.Combine(baseDir, $"page{i}.json");
            if (!File.Exists(filePath))
            {
                var altPath = Path.Combine(Directory.GetCurrentDirectory(), $"page{i}.json");
                if (File.Exists(altPath))
                {
                    filePath = altPath;
                }
                else
                {
                    Console.WriteLine($"Warning: {Path.GetFileName(filePath)} not found. Skipping.");
                    continue;
                }
            }

            try
            {
                var text = File.ReadAllText(filePath);

                // Prefer JSON parsing with tolerant options
                if (!TryCollectGamesFromJsonText(text, gamesById))
                {
                    // Try JSON Lines (NDJSON) format: one JSON object per line
                    bool anyFromLines = false;
                    using var reader = new StringReader(text);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (TryCollectGamesFromJsonText(line, gamesById))
                        {
                            anyFromLines = true;
                        }
                    }

                    if (!anyFromLines)
                    {
                        // Fallback: regex extraction from text (works for HTML or loosely structured text)
                        foreach (var id in ExtractIdsWithRegex(text))
                        {
                            MergeGame(gamesById, new GameData { Id = id });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to read {filePath}: {ex.Message}");
            }
        }

        return new List<GameData>(gamesById.Values);
    }

    private static bool TryCollectGamesFromJsonText(string jsonText, Dictionary<int, GameData> sink)
    {
        try
        {
            var options = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };
            using var doc = JsonDocument.Parse(jsonText, options);
            VisitJsonForGames(doc.RootElement, sink);
            return sink.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void VisitJsonForGames(JsonElement el, Dictionary<int, GameData> sink)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryBuildGameData(el, out var game))
                {
                    MergeGame(sink, game);
                }
                else
                {
                    // Handle object containers like { "Items": [ ... ] } or dictionaries
                    if (el.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var it in items.EnumerateArray())
                        {
                            VisitJsonForGames(it, sink);
                        }
                    }
                    else
                    {
                        foreach (var prop in el.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                            {
                                // If dictionary-like { "123": { ... } }
                                if (int.TryParse(prop.Name, out var idFromKey) && prop.Value.ValueKind == JsonValueKind.Object)
                                {
                                    if (TryBuildGameData(prop.Value, out var g2))
                                    {
                                        if (g2.Id == 0) g2.Id = idFromKey;
                                        MergeGame(sink, g2);
                                    }
                                    else
                                    {
                                        // At least record the id from the key
                                        MergeGame(sink, new GameData { Id = idFromKey });
                                    }
                                }
                                else
                                {
                                    VisitJsonForGames(prop.Value, sink);
                                }
                            }
                        }
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    VisitJsonForGames(item, sink);
                }
                break;
        }
    }

    private static void MergeGame(Dictionary<int, GameData> sink, GameData game)
    {
        if (game.Id == 0) return;
        if (sink.TryGetValue(game.Id, out var existing))
        {
            existing.Title ??= game.Title;
            existing.ReleasedAt ??= game.ReleasedAt;
            existing.BadgeUrl ??= game.BadgeUrl;
        }
        else
        {
            sink[game.Id] = game;
        }
    }

    private static bool TryBuildGameData(JsonElement el, out GameData game)
    {
        game = new GameData();
        if (el.ValueKind != JsonValueKind.Object) return false;

        // Id
        if (!TryGetIntProperty(el, out int id,
                "ID", "Id", "id", "GameID", "GameId", "Game_ID"))
        {
            return false;
        }
        game.Id = id;

        // Title
        game.Title = TryGetStringProperty(el,
            "Title", "title", "GameTitle", "Name", "name");

        // ReleasedAt / ReleaseDate
        game.ReleasedAt = TryGetStringProperty(el,
            "releasedAt", "ReleasedAt", "ReleaseDate", "released", "Released");

        // Badge URL / Icon
        var badge = TryGetStringProperty(el,
            "badgeUrl", "BadgeUrl", "BadgeURL", "ImageIcon", "IconURL", "icon", "imageIcon", "Icon");
        game.BadgeUrl = NormalizeBadgeUrl(badge);

        return true;
    }

    private static bool TryGetIntProperty(JsonElement el, out int value, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value)) return true;
                if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out value)) return true;
            }
        }
        value = 0;
        return false;
    }

    private static string? TryGetStringProperty(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String)
                {
                    var s = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                else if (prop.ValueKind == JsonValueKind.Number)
                {
                    // Sometimes numbers encoded as number; convert to string
                    return prop.ToString();
                }
            }
        }
        return null;
    }

    private static string? NormalizeBadgeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
        // Ensure leading slash
        if (!url.StartsWith("/")) url = "/" + url;
        return "https://retroachievements.org" + url;
    }

    private static IEnumerable<int> ExtractIdsWithRegex(string text)
    {
        // 1) From links in HTML like game.php?ID=1234
        foreach (Match m in Regex.Matches(text, @"game\.php\?ID=(\d+)", RegexOptions.IgnoreCase))
        {
            if (int.TryParse(m.Groups[1].Value, out var id))
                yield return id;
        }
        // 2) From common JSON key patterns: "ID": 123 or "Id": "123"
        foreach (Match m in Regex.Matches(text, "\"(?:GameID|GameId|ID|Id|id)\"\\s*:\\s*\"?(\\d+)\"?", RegexOptions.IgnoreCase))
        {
            if (int.TryParse(m.Groups[1].Value, out var id))
                yield return id;
        }
    }

    static string FetchGameAchievements(int gameId)
    {
        using var client = new HttpClient();
        // Use the correct RA endpoint name API_GetGameExtended.php (not GetGameExtended.php)
        var url = $"{baseUrl}API_GetGameExtended.php?z={username}&y={apiKey}&i={gameId}";
        try
        {
            return client.GetStringAsync(url).GetAwaiter().GetResult();
        }
        catch(Exception ex)
        {
            Console.WriteLine($"Failed to fetch game {gameId}");
            return null;
        }
    }

    private static IEnumerable<GameData> OrderByKeywordPriority(IEnumerable<GameData> games)
    {
        return games
            .Select((g, idx) => new { g, idx, pri = GetPriority(g.Title) })
            .OrderBy(x => x.pri)
            .ThenBy(x => x.idx)
            .Select(x => x.g);
    }

    private static int GetPriority(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return int.MaxValue;
        for (int i = 0; i < PriorityKeywords.Length; i++)
        {
            if (title.IndexOf(PriorityKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                return i;
        }
        return int.MaxValue;
    }

    private static List<FormulaFile> LoadFormulaFiles(out int failed)
    {
        failed = 0;
        var results = new List<FormulaFile>();

        // Probe common locations for a 'formulas' folder
        var candidates = new List<string>();
        var cwd = Directory.GetCurrentDirectory();
        var baseDir = AppContext.BaseDirectory;
        var d1 = Path.Combine(cwd, "formulas");
        var d2 = Path.Combine(baseDir, "formulas");
        if (Directory.Exists(d1)) candidates.Add(d1);
        if (Directory.Exists(d2) && !string.Equals(d1, d2, StringComparison.OrdinalIgnoreCase)) candidates.Add(d2);

        if (candidates.Count == 0)
        {
            Console.WriteLine("No 'formulas' directory found.");
            return results;
        }

        var files = candidates
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var parsed = FormulaFile.Parse(json);
                if (parsed != null)
                {
                    results.Add(parsed);
                }
                else
                {
                    failed++;
                    Console.WriteLine($"Failed to parse formulas file: {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"Error reading formulas file '{file}': {ex.Message}");
            }
        }

        return results;
    }

    private static Dictionary<string, GameExtended> LoadAchievementFilesByTitle(out int failed)
    {
        failed = 0;
        var map = new Dictionary<string, GameExtended>(StringComparer.OrdinalIgnoreCase);

        // Probe common locations for an 'achievements' folder (project root and bin output dir)
        var candidates = new List<string>();
        var cwd = Directory.GetCurrentDirectory();
        var baseDir = AppContext.BaseDirectory; // typically bin/Debug/net8.0
        var d1 = Path.Combine(cwd, "achievements");
        var d2 = Path.Combine(baseDir, "achievements");
        if (Directory.Exists(d1)) candidates.Add(d1);
        if (Directory.Exists(d2) && !string.Equals(d1, d2, StringComparison.OrdinalIgnoreCase)) candidates.Add(d2);

        if (candidates.Count == 0)
        {
            Console.WriteLine("No 'achievements' directory found.");
            return map;
        }

        var files = candidates
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var parsed = GameExtended.Parse(json);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Title))
                {
                    map[parsed.Title!] = parsed;
                }
                else
                {
                    failed++;
                    Console.WriteLine($"Failed to parse achievement file: {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"Error reading achievement file '{file}': {ex.Message}");
            }
        }

        return map;
    }
}

public class GameData
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? ReleasedAt { get; set; }
    public string? BadgeUrl { get; set; }
}