using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OfflineRA.Models
{
    // Root type that can represent any formulas JSON file found in bin/.../formulas
    // Some files contain PatchData (with Achievements/Leaderboards/etc.), others contain CodeNotes only.
    public class FormulaFile
    {
        public bool Success { get; set; }
        public PatchData? PatchData { get; set; }
        public List<CodeNote>? CodeNotes { get; set; }

        public static FormulaFile? Parse(string json)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
            return JsonSerializer.Deserialize<FormulaFile>(json, options);
        }
    }

    // Appears when Success + PatchData exist in the formulas file
    public class PatchData
    {
        public int ID { get; set; }
        public int ParentID { get; set; }
        public string? Title { get; set; }
        public string? ImageIcon { get; set; }
        public string? ImageIconURL { get; set; }
        public int ConsoleID { get; set; }
        public string? RichPresencePatch { get; set; }
        public List<AchievementFormula>? Achievements { get; set; }
        public List<LeaderboardFormula>? Leaderboards { get; set; }
    }

    public class AchievementFormula
    {
        public int ID { get; set; }
        public string? MemAddr { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int Points { get; set; }
        public string? Author { get; set; }
        public long Modified { get; set; } // epoch seconds
        public long Created { get; set; }  // epoch seconds
        public string? BadgeName { get; set; }
        public int Flags { get; set; }
        public string? Type { get; set; }
        public double? Rarity { get; set; }
        public double? RarityHardcore { get; set; }
        public string? BadgeURL { get; set; }
        public string? BadgeLockedURL { get; set; }
    }

    public class LeaderboardFormula
    {
        public int ID { get; set; }
        public string? Mem { get; set; }
        public string? Format { get; set; }
        public int LowerIsBetter { get; set; } // value 0/1 in JSON
        public string? Title { get; set; }
        public string? Description { get; set; }
        public bool Hidden { get; set; }
    }

    // Appears in files that only contain CodeNotes (Success + CodeNotes)
    public class CodeNote
    {
        public string? User { get; set; }
        public string? Address { get; set; }
        public string? Note { get; set; }
    }
}
