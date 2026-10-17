using System;
using System.IO;
using System.Text.Json;

namespace NesEmulator.Corruption;

/// <summary>Reads a NES savestate's idea of the game (the SHA-256 of its ROM and the ROM's file name) into a stockpile game reference. The NES's states carry no SHA-1 and no ROM bytes; this is the identity they have.</summary>
public static class NesStateIdentity
{
    public static void Fill(GameRef game, string stateJson)
    {
        try
        {
            using var d = JsonDocument.Parse(stateJson);
            if (d.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var p in d.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) continue;
                string v = p.Value.GetString() ?? "";
                if (string.Equals(p.Name, "romHash", StringComparison.OrdinalIgnoreCase) && v.Length > 0 && game.Sha256.Length == 0) game.Sha256 = v.ToLowerInvariant();
                else if (string.Equals(p.Name, "romName", StringComparison.OrdinalIgnoreCase) && v.Length > 0)
                {
                    if (game.File.Length == 0) game.File = Path.GetFileName(v);
                    if (game.Title.Length == 0) game.Title = Path.GetFileNameWithoutExtension(v);
                }
            }
        }
        catch (JsonException) { /* the state is the NES's to judge when it is loaded */ }
    }
}
