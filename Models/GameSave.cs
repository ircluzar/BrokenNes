namespace BrokenNes.Models;

public class GameSave
{
    // Current level (starts at 1)
    public int Level { get; set; } = 1;

    // Achievement ids (each entry counts as one star)
    public List<string> Achievements { get; set; } = new();

    // Feature unlocks (gates advanced emulator tooling in UI)
    public bool RtcUnlocked { get; set; } = false; // Real-Time Corruptor
    public bool GhUnlocked { get; set; } = false;  // Glitch Harvester
    public bool ImagineUnlocked { get; set; } = false; // Imagine (AI model)
    public bool DebugUnlocked { get; set; } = false; // Developer/Debug tools (opt-in)

    // Owned core ids per category (Ids are suffixes like "FMC", "FIX", etc.)
    public List<string> OwnedCpuIds { get; set; } = new();
    public List<string> OwnedPpuIds { get; set; } = new();
    public List<string> OwnedApuIds { get; set; } = new();
    public List<string> OwnedClockIds { get; set; } = new();
    public List<string> OwnedShaderIds { get; set; } = new();
}
