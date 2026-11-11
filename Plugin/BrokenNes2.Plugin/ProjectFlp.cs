// "This FL project" (Test MIDI export window): which .flp is the project this plugin instance sits in, so the button can take the project's own LAST SAVED file.
//
// FL does not tell a native plugin its project's path (docs/projects/flp-music-pipeline.md section 6: no SDK call returns it, FPD_ProjLoaded carries none). Until 2026-11-10 the plugin guessed
// the project's name from FL's main window title; the user chose a file picker instead (safer than parsing a title whose form was never verified in real FL):
//   1. the first press of "This FL project" opens the file dialog (starting in the folder of the last pick, preference "FlpFolder");
//   2. the pick is kept IN THE PLUGIN'S SAVED STATE (section FLP1 below), so it rides in the FL project itself: the project remembers its own .flp, another project asks again;
//   3. "Change .flp" opens the dialog again and replaces the pick. A remembered file that is gone asks again.
// The file is read when the button is pressed: it is the project as FL LAST SAVED it (unsaved changes in FL are not in it: the window says the file's save time).
using System.Buffers.Binary;
using System.Text;

using BrokenNes2.Music;

namespace BrokenNes2;

internal static class ProjectFlp
{
    public const uint Tag = 0x31504C46;                 // "FLP1" (little-endian, like "TRK1")
    public const int Version = 1, MaxBytes = 2 + 2 + 4096 + 4;

    /// <summary>u16 version (1) | u16 + UTF-8 path | u32 CRC-32 of everything before it. Null: nothing picked (no section).</summary>
    public static byte[]? SectionBytes(string? path)
    {
        if (string.IsNullOrEmpty(path) || MusicMutate.Has(MusicMutate.FlpPickNotSaved)) return null;
        var p = Encoding.UTF8.GetBytes(path);
        if (p.Length > 4096) return null;
        var s = new byte[2 + 2 + p.Length + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(s, Version);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(2), (ushort)p.Length);
        p.CopyTo(s, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(4 + p.Length), Crc32.Of(s.AsSpan(0, 4 + p.Length)));
        return s;
    }

    /// <summary>The path in an FLP1 section, or null when it is damaged or a newer version (the button then asks again).</summary>
    public static string? ReadSection(ReadOnlySpan<byte> s)
    {
        if (s.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(s) != Version) return null;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
        if (s.Length != 4 + n + 4 || BinaryPrimitives.ReadUInt32LittleEndian(s[(4 + n)..]) != Crc32.Of(s[..(4 + n)])) return null;
        return n > 0 ? Encoding.UTF8.GetString(s.Slice(4, n)) : null;
    }
}
