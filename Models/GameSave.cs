namespace BrokenNes.Models;

public class GameSave
{
    // Current level (starts at 1)
    public int Level { get; set; } = 1;

    // Achievement ids (each entry counts as one star)
    public List<string> Achievements { get; set; } = new();

    // Owned core ids per category (Ids are suffixes like "FMC", "FIX", etc.)
    public List<string> OwnedCpuIds { get; set; } = new();
    public List<string> OwnedPpuIds { get; set; } = new();
    public List<string> OwnedApuIds { get; set; } = new();
    public List<string> OwnedClockIds { get; set; } = new();
    public List<string> OwnedShaderIds { get; set; } = new();
}
