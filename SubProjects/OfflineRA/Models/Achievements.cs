using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OfflineRA.Models
{
    // Represents the response from API_GetGameExtended.php
    public class GameExtended
    {
        // Basic game info
        public int GameID { get; set; }
        public string? Title { get; set; }
        // Some payloads use GameTitle; alias it to Title
        [JsonPropertyName("GameTitle")] public string? GameTitle { get => Title; set => Title = value; }
        public int ConsoleID { get; set; }
        public string? ConsoleName { get; set; }
        public string? ImageIcon { get; set; }
        public string? ImageIconURL { get; set; }
        public string? Developer { get; set; }
        public string? Publisher { get; set; }
        public string? Genre { get; set; }
        public string? Released { get; set; }
        public string? ReleasedAt { get; set; }
        public string? RichPresencePatch { get; set; }

        // Achievements and Leaderboards can be either a dictionary keyed by id or an array
        [JsonConverter(typeof(DictOrListConverter<AchievementExt>))]
        public List<AchievementExt>? Achievements { get; set; }
        [JsonConverter(typeof(DictOrListConverter<LeaderboardExt>))]
        public List<LeaderboardExt>? Leaderboards { get; set; }

        // Other optional fields frequently present
        public int NumAchievements { get; set; }
        public int NumDistinctPlayers { get; set; }
        public int ForumTopicID { get; set; }

        public static GameExtended? Parse(string json)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
            return JsonSerializer.Deserialize<GameExtended>(json, options);
        }
    }

    public class AchievementExt
    {
        public int ID { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int Points { get; set; }
        public string? Author { get; set; }
        public string? BadgeName { get; set; }
        public string? BadgeURL { get; set; }
        public string? BadgeLockedURL { get; set; }

        // RA sometimes uses these names for dates
        public string? DateCreated { get; set; }
        public string? DateModified { get; set; }

        // Memory/logic for the achievement
        public string? MemAddr { get; set; }

        // Misc
        public int DisplayOrder { get; set; }
        public int Flags { get; set; }
        public string? Type { get; set; }
        public double? Rarity { get; set; }
        public double? RarityHardcore { get; set; }
    }

    public class LeaderboardExt
    {
        public int ID { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Format { get; set; }
        public string? Mem { get; set; }

        // Common variations
        public int LowerIsBetter { get; set; } // 0/1
        public bool Hidden { get; set; }
    }

    // Converter that accepts either { "id": { ... }, ... } or [ { ... }, ... ] and produces a List<T>
    public class DictOrListConverter<T> : JsonConverter<List<T>>
    {
        public override List<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                return JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? new List<T>();
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                var list = new List<T>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var item = prop.Value.Deserialize<T>(options);
                    if (item != null) list.Add(item);
                }
                return list;
            }

            // Fallback: try to deserialize whatever it is as a List<T>
            try
            {
                return JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? new List<T>();
            }
            catch
            {
                return new List<T>();
            }
        }

        public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }
}
