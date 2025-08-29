using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrokenNes.Services;

/// <summary>
/// Read-only, lazy-loading accessor for wwwroot/models/meta_games.json.
/// Provides query methods to get achievements by game title.
/// </summary>
public sealed class MetaGamesService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private List<MetaGame>? _cache; // set on first load
    private Task? _loadingTask;     // coalesce concurrent loads
    private readonly object _gate = new object();

    public MetaGamesService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Ensure the JSON is loaded and cached. Safe to call multiple times.
    /// </summary>
    private Task EnsureLoadedAsync()
    {
        if (_cache is not null) return Task.CompletedTask;
        lock (_gate)
        {
            _loadingTask ??= LoadAsync();
            return _loadingTask;
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            // The file lives under wwwroot/models
            using var stream = await _http.GetStreamAsync("models/meta_games.json");
            var data = await JsonSerializer.DeserializeAsync<List<MetaGame>>(stream, _jsonOptions)
                       ?? new List<MetaGame>();

            // Normalize nulls and strings; System.Text.Json already decodes \uXXXX sequences.
            foreach (var g in data)
            {
                g.Title ??= string.Empty;
                g.Achievements ??= new List<MetaAchievement>();
                foreach (var a in g.Achievements)
                {
                    a.Description ??= string.Empty;
                    a.Formula ??= string.Empty;
                }
            }
            _cache = data;
        }
        catch
        {
            _cache = new List<MetaGame>();
        }
    }

    /// <summary>
    /// All game titles available, sorted.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetTitlesAsync()
    {
        await EnsureLoadedAsync();
        return _cache!
            .Select(g => g.Title ?? string.Empty)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Achievements for a specific game title (case-insensitive exact match).
    /// Returns Description + Formula pairs.
    /// </summary>
    public async Task<IReadOnlyList<AchievementDto>> GetAchievementsByTitleAsync(string title)
    {
        await EnsureLoadedAsync();
        if (string.IsNullOrWhiteSpace(title)) return Array.Empty<AchievementDto>();
        var comparer = StringComparer.OrdinalIgnoreCase;
        var rows = _cache!
            .Where(g => comparer.Equals(g.Title, title))
            .SelectMany(g => g.Achievements ?? Enumerable.Empty<MetaAchievement>())
            .Select(a => new AchievementDto(a.Description ?? string.Empty, a.Formula ?? string.Empty))
            .ToList();
        return rows;
    }

    /// <summary>
    /// Flattened view across all games: GameTitle, Description, Formula.
    /// </summary>
    public async Task<IReadOnlyList<AchievementWithGame>> GetAllAchievementsFlatAsync()
    {
        await EnsureLoadedAsync();
        var list = new List<AchievementWithGame>();
        foreach (var g in _cache!)
        {
            var title = g.Title ?? string.Empty;
            if (g.Achievements == null || g.Achievements.Count == 0)
            {
                continue;
            }
            foreach (var a in g.Achievements)
            {
                list.Add(new AchievementWithGame(title, a.Description ?? string.Empty, a.Formula ?? string.Empty));
            }
        }
        return list;
    }

    // ==== Models ====
    private sealed class MetaGame
    {
        public string? Title { get; set; }
        public List<MetaAchievement>? Achievements { get; set; }
    }

    private sealed class MetaAchievement
    {
        public string? Description { get; set; }
        public string? Formula { get; set; }
    }

    public readonly record struct AchievementDto(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("formula")] string Formula
    );

    public readonly record struct AchievementWithGame(
        [property: JsonPropertyName("gameTitle")] string GameTitle,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("formula")] string Formula
    );
}
