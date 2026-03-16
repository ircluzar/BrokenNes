using System.Collections.Generic;

namespace OfflineRA.Models
{
    public class MetaGameData
    {
        public string? Title { get; set; }
        public List<MetaGameAchievement> Achievements { get; set; } = new List<MetaGameAchievement>();
    }

    public class MetaGameAchievement
    {
        // Description from the achievement metadata
        public string? Description { get; set; }

        // Copy of the MemAddr / memory formula stored under 'Formula'
        public string? Formula { get; set; }
    }
}
