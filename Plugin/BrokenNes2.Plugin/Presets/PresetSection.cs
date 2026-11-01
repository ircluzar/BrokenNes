// Round 6 (presets): the project's own copy of a preset, the saved-state section `PRS1`. A plugin instance that plays a preset (the Presets chips) writes the whole preset file into the FL project, so the project still plays when the library file is moved or gone, and
// when the project is opened on a computer that never had the library. The section follows the rules of the others: tag, length, bytes; an older BrokenNes2 skips it by its length; a project without it opens as before; a damaged or newer one is refused with the reason (the plugin
// puts a note on screen), never half used.
//
//   u8 version (1) | u8 flags (0) | u16 reserved (0) | u32 length of the preset file | the preset file (Presets/PresetFile.cs) | u32 CRC-32 of everything before it
using BrokenNes2.Instruments;

namespace BrokenNes2.Presets;

public static class PresetSection
{
    public const uint Tag = 0x31535250;                 // "PRS1" (little-endian, like "SPC1" and "SPO1")
    public const int Version = 1, HeaderBytes = 8, MinBytes = HeaderBytes + 18 + 4, MaxBytes = HeaderBytes + PresetFile.MaxFileBytes + 4;

    /// <summary>The section's bytes for a preset file (the exact bytes the library holds or the Instruments tab made).</summary>
    public static byte[] Build(byte[] presetFile)
    {
        var b = new byte[HeaderBytes + presetFile.Length + 4];
        b[0] = Version;
        BitConverter.GetBytes((uint)presetFile.Length).CopyTo(b, 4);
        presetFile.CopyTo(b, HeaderBytes);
        BitConverter.GetBytes(InstrumentCrc.Of(b.AsSpan(0, b.Length - 4))).CopyTo(b, b.Length - 4);
        return b;
    }

    /// <summary>The preset a section holds (read completely, as <see cref="PresetFile.Read"/> does), with the file's bytes; or the reason it cannot be used.</summary>
    public static (PresetData? Data, byte[]? File, string? Problem) Read(byte[] s)
    {
        if (s.Length < MinBytes) return (null, null, "cut short");
        if (s.Length > MaxBytes) return (null, null, "too big");
        if (s[0] > Version) return (null, null, "made by a newer BrokenNes2 (section version " + s[0] + ")");
        if (s[0] != Version) return (null, null, "of an unknown version");
        if (s[1] != 0) return (null, null, "it uses a feature this version of BrokenNes2 does not know");
        uint crc = BitConverter.ToUInt32(s, s.Length - 4);
        if (crc != InstrumentCrc.Of(s.AsSpan(0, s.Length - 4)) && !PresetMutants.On(PresetMutants.SectionCrcNotChecked)) return (null, null, "damaged (the checksum does not match)");
        uint len = BitConverter.ToUInt32(s, 4);
        if (len != (uint)(s.Length - HeaderBytes - 4)) return (null, null, "damaged (its length)");
        var file = s.AsSpan(HeaderBytes, (int)len).ToArray();
        var r = PresetFile.Read(file);
        return r.Data == null ? (null, null, r.Problem) : (r.Data, file, null);
    }
}
