using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace BrokenNes.Workshop.Tas;

/// <summary>
/// FM2 header key-value pairs. Unknown/future keys are preserved in RawFields instead of being
/// dropped, since this format has grown fork-specific extra keys over the years (e.g. "microphone",
/// "FDS", "NewPPU", "length" - none of which are in the base fm2.txt spec but all of which appear
/// in real-world files) - see Fm2Movie's class doc for the exact spec this was built against.
/// </summary>
public sealed class Fm2Header
{
    public int Version;
    public int EmuVersion;
    public int RerecordCount;
    public bool PalFlag;
    public bool Fourscore;
    public int Port0 = 1; // SI_GAMEPAD by default
    public int Port1 = 1;
    public int Port2; // SIFC_NONE
    public string RomFilename = string.Empty;
    public string RomChecksumRaw = string.Empty; // as-written: "0xHEX..." or "base64:...."
    public byte[]? RomChecksumMd5; // decoded, if parseable
    public string Guid = string.Empty;
    public List<string> Comments { get; } = new();
    public Dictionary<string, string> RawFields { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum Fm2InputDevice { None = 0, Gamepad = 1, Zapper = 2 }

/// <summary>One decoded input-log line. Button arrays are already translated into BrokenNes's own
/// index order (0:A 1:B 2:Select 3:Start 4:Up 5:Down 6:Left 7:Right, per Input.cs) - callers never
/// need to know FM2's RLDUTSBA text order.</summary>
public sealed class Fm2Frame
{
    public bool Reset; // MOVIECMD_RESET (c bitfield bit 0)
    public bool[] P1 = new bool[8];
    public bool[]? P2; // null if port1 carried no gamepad (e.g. SIFC_NONE)
    public bool[]? P3; // fourscore only
    public bool[]? P4; // fourscore only
}

/// <summary>
/// Parses FCEUX's FM2 TAS movie format (plain text header + pipe-delimited input log), per the
/// spec shipped with the ML_NesPlayer project's custom FCEUX fork
/// (TAS/fceux_custom/documentation/fm2.txt) and cross-checked against a real recorded movie
/// (TAS/tas/meshuggah-ghostbusters.fm2) in that same project. Read-only playback only for now -
/// no writer yet.
///
/// Known real-world divergence from the shipped spec: romChecksum appears as "base64:...." in
/// actual files even though fm2.txt documents only the "0x...." hex form - both are accepted here.
/// </summary>
public static class Fm2Movie
{
    public static (Fm2Header Header, IReadOnlyList<Fm2Frame> Frames) Load(string path)
    {
        string text = ReadMovieText(path);
        // Newlines may be \r\n or \n per spec; normalize once up front.
        string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        var header = new Fm2Header();
        var frames = new List<Fm2Frame>();
        bool sawVersion = false;

        foreach (string rawLine in lines)
        {
            if (rawLine.Length == 0) continue;
            if (rawLine[0] == '|')
            {
                frames.Add(ParseInputLine(rawLine, header));
                continue;
            }

            int sep = rawLine.IndexOf(' ');
            string key = sep >= 0 ? rawLine[..sep] : rawLine;
            string value = sep >= 0 ? rawLine[(sep + 1)..] : string.Empty;

            if (!sawVersion && !key.Equals("version", StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"FM2 parse error: first key must be 'version', got '{key}'.");

            switch (key.ToLowerInvariant())
            {
                case "version": header.Version = ParseInt(value); sawVersion = true; break;
                case "emuversion": header.EmuVersion = ParseInt(value); break;
                case "rerecordcount": header.RerecordCount = ParseInt(value); break;
                case "palflag": header.PalFlag = ParseInt(value) != 0; break;
                case "fourscore": header.Fourscore = ParseInt(value) != 0; break;
                case "port0": header.Port0 = ParseInt(value); break;
                case "port1": header.Port1 = ParseInt(value); break;
                case "port2": header.Port2 = ParseInt(value); break;
                case "romfilename": header.RomFilename = value; break;
                case "romchecksum": header.RomChecksumRaw = value; header.RomChecksumMd5 = TryDecodeChecksum(value); break;
                case "guid": header.Guid = value; break;
                case "comment": header.Comments.Add(value); break;
                default: header.RawFields[key] = value; break;
            }
        }

        if (!sawVersion)
            throw new FormatException("FM2 parse error: no 'version' key found - not a valid FM2 file.");

        return (header, frames);
    }

    /// <summary>
    /// TASVideos.org publishes movies as a .zip containing the .fm2 (often just renamed with a
    /// .fm2 extension rather than .zip, per real files found in TAS/tas/ - every one of them
    /// starts with the "PK" zip magic despite the extension). FCEUX's own movie loader already
    /// handles this transparently; this mirrors that so the same files work here unmodified. Falls
    /// back to reading the file as plain text if it isn't a zip at all.
    /// </summary>
    private static string ReadMovieText(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[2];
        int read = fs.Read(magic);
        bool isZip = read == 2 && magic[0] == (byte)'P' && magic[1] == (byte)'K';
        fs.Position = 0;
        if (!isZip)
            return File.ReadAllText(path);

        using var archive = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".fm2", StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            // Some older TASVideos submissions ship FCEUX's original binary movie format (.fcm,
            // magic "FCM\x1A") instead of the later plain-text .fm2 - a genuinely different format
            // this parser doesn't support. Name the specific entry so the caller can tell "legacy
            // .fcm" apart from "corrupt zip" instead of getting a confusing downstream parse error.
            var only = archive.Entries.FirstOrDefault();
            string found = only != null ? $"'{only.Name}'" : "no entries";
            throw new NotSupportedException($"'{path}' is a zip archive with no .fm2 entry (found {found}) - likely a legacy .fcm movie, which this parser doesn't support.");
        }
        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream);
        return reader.ReadToEnd();
    }

    private static Fm2Frame ParseInputLine(string line, Fm2Header header)
    {
        // "|c|port0|port1|port2|" (or, with fourscore, "|c|p1|p2|p3|p4|port2|").
        // Split on '|': the string starts and ends with '|', so the first and last elements of a
        // naive split are empty - trim those rather than special-casing the split call.
        string[] parts = line.Split('|');
        // parts[0] == "" (before the leading |), parts[^1] == "" (after the trailing |).
        if (parts.Length < 4 || parts[0].Length != 0)
            throw new FormatException($"FM2 parse error: malformed input line '{line}'.");

        var frame = new Fm2Frame();
        int cmd = ParseInt(parts[1]);
        frame.Reset = (cmd & 0x1) != 0; // MOVIECMD_RESET

        if (header.Fourscore)
        {
            // |c|p1|p2|p3|p4|port2|
            if (parts.Length < 7) throw new FormatException($"FM2 parse error: fourscore input line too short: '{line}'.");
            frame.P1 = DecodeGamepadField(parts[2]);
            frame.P2 = DecodeGamepadField(parts[3]);
            frame.P3 = DecodeGamepadField(parts[4]);
            frame.P4 = DecodeGamepadField(parts[5]);
            return frame;
        }

        // |c|port0|port1|port2|
        if (parts.Length < 5) throw new FormatException($"FM2 parse error: input line too short: '{line}'.");
        frame.P1 = DecodePortField((Fm2InputDevice)header.Port0, parts[2]);
        frame.P2 = header.Port1 == (int)Fm2InputDevice.None ? null : DecodePortField((Fm2InputDevice)header.Port1, parts[3]);
        return frame;
    }

    private static bool[] DecodePortField(Fm2InputDevice device, string field)
    {
        if (device == Fm2InputDevice.Zapper)
            throw new NotSupportedException("FM2 zapper input is not supported by this playback path yet - gamepad movies only.");
        return DecodeGamepadField(field);
    }

    /// <summary>
    /// Decodes one 8-character RLDUTSBA field into BrokenNes's own button order. FM2's text order
    /// (Right,Left,Down,Up,sTart,Select,B,A) happens to be the exact reverse of BrokenNes's
    /// Input.cs order (A,B,Select,Start,Up,Down,Left,Right) - see Input.cs:10 - so translation is
    /// just index reversal, verified against Windows/NesEmulator/board/Input.cs's documented order.
    /// </summary>
    private static bool[] DecodeGamepadField(string field)
    {
        if (field.Length != 8)
            throw new FormatException($"FM2 parse error: expected an 8-character gamepad field, got '{field}' (length {field.Length}).");
        var result = new bool[8];
        for (int fm2Index = 0; fm2Index < 8; fm2Index++)
        {
            bool pressed = field[fm2Index] != ' ' && field[fm2Index] != '.';
            result[7 - fm2Index] = pressed;
        }
        return result;
    }

    private static int ParseInt(string value) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private static byte[]? TryDecodeChecksum(string raw)
    {
        raw = raw.Trim();
        try
        {
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                string hex = raw[2..];
                if (hex.Length % 2 != 0) return null;
                var bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                    bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return bytes;
            }
            if (raw.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
                return Convert.FromBase64String(raw["base64:".Length..]);
        }
        catch { /* malformed checksum shouldn't block loading the movie for playback */ }
        return null;
    }
}
