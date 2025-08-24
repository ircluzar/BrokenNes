namespace BrokenNes.Models;

public class GameSave
{
    // Current level (starts at 1)
    public int Level { get; set; } = 1;

    // Achievement ids (each entry counts as one star)
    public List<string> Achievements { get; set; } = new();
}
