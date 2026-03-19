namespace BrokenNes.Models;

public class ContinueStateSlot
{
    public string RomKey { get; set; } = string.Empty;
    public string? Title { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? PreviewImagePath { get; set; }
}

public class UnlockRewardItem
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Description { get; set; }
    public bool CanEquip { get; set; } = false;
    public bool IsEquipped { get; set; } = false;
    public string? EquipAction { get; set; }
}

public class PendingUnlockBundle
{
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? AchievementId { get; set; }
    public int? LevelIndex { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool Presented { get; set; } = false;
    public List<UnlockRewardItem> Items { get; set; } = new();
}

public class GameSave
{
    // Current level (starts at 1)
    public int Level { get; set; } = 1;
    
    // Whether the current level has been cleared (set when any achievement is earned at this level)
    public bool LevelCleared { get; set; } = false;

    // Achievement ids (each entry counts as one star)
    public List<string> Achievements { get; set; } = new();

    // Feature unlocks (gates advanced emulator tooling in UI)
    public bool SavestatesUnlocked { get; set; } = false; // Savestates (Save/Load)
    public bool RtcUnlocked { get; set; } = false; // Real-Time Corruptor
    public bool GhUnlocked { get; set; } = false;  // Glitch Harvester
    public bool ImagineUnlocked { get; set; } = false; // Imagine (AI model)
    public bool DebugUnlocked { get; set; } = false; // Developer/Debug tools (opt-in)

    // Story progression
    public bool SeenStory { get; set; } = false; // Set after Story has been viewed at least once

    // Owned core ids per category (Ids are suffixes like "FMC", "FIX", etc.)
    public List<string> OwnedCpuIds { get; set; } = new();
    public List<string> OwnedPpuIds { get; set; } = new();
    public List<string> OwnedApuIds { get; set; } = new();
    public List<string> OwnedClockIds { get; set; } = new();
    public List<string> OwnedShaderIds { get; set; } = new();

    // User preferred core selections (persisted across sessions)
    // Store only player-selected ids, not enforced ones.
    public string? PreferredCpuId { get; set; }
    public string? PreferredPpuId { get; set; }
    public string? PreferredApuId { get; set; }
    public string? PreferredShaderId { get; set; }

    // Progression-owned modules and cosmetic/equipment unlocks
    public List<string> UnlockedWebmodules { get; set; } = new();
    public List<string> UnlockedBackgrounds { get; set; } = new();
    public List<string> UnlockedNullProviders { get; set; } = new();
    public string? PreferredBackgroundId { get; set; }
    public string? PreferredNullProviderId { get; set; }
    public List<PendingUnlockBundle> PendingUnlocks { get; set; } = new();

    // Trusted DeckBuilder resume marker
    // When an achievement unlock creates a savestate from DeckBuilder flow,
    // we store a trusted marker so Continue page can offer "Continue game".
    // The marker is dropped when the user saves a state in Emulator mode without achievements active.
    public bool PendingDeckContinue { get; set; } = false;
    public string? PendingDeckContinueRom { get; set; } // romKey/filename of the game
    public string? PendingDeckContinueTitle { get; set; } // optional display title
    public DateTime? PendingDeckContinueAtUtc { get; set; } // optional timestamp
    public Dictionary<string, ContinueStateSlot> ContinueSlots { get; set; } = new();

    // One-time acknowledgements
    public bool UnderConstructionAcknowledged { get; set; } = false; // Set after user accepts Under Construction notice

    // One-time: player has unlocked every core across all categories and saw the congratulations modal
    public bool AllCoresUnlockedCongrats { get; set; } = false;

    // ROM masquerades: map a ROM key (filename) to a target gameId whose achievements to use
    // This enables ROM hacks/variants to pretend to be another game for compatibility.
    public Dictionary<string, string> MasqueradeRomToGameId { get; set; } = new();
}
