namespace BrokenNes.Models;

public class GameSave
{
    // Current level (starts at 1)
    public int Level { get; set; } = 1;

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

    // Trusted DeckBuilder resume marker
    // When an achievement unlock creates a savestate from DeckBuilder flow,
    // we store a trusted marker so Continue page can offer "Continue game".
    // The marker is dropped when the user saves a state in Emulator mode without achievements active.
    public bool PendingDeckContinue { get; set; } = false;
    public string? PendingDeckContinueRom { get; set; } // romKey/filename of the game
    public string? PendingDeckContinueTitle { get; set; } // optional display title
    public DateTime? PendingDeckContinueAtUtc { get; set; } // optional timestamp
}
