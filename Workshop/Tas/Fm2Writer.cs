using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BrokenNes.Workshop.Tas;

/// <summary>
/// Writes FCEUX-compatible FM2 movies. Companion to Fm2Movie (the reader) - together they let a
/// movie recorded/replayed here round-trip through Fm2Movie.Load() and through any other FM2-aware
/// tool (TASEditor, the original FCEUX fork this whole TAS port mirrors, etc.).
///
/// Deliberately emits only the base fm2.txt spec's key set (version, emuVersion, rerecordCount,
/// palFlag, romFilename, romChecksum, guid, fourscore, port0/1/2, comment). Real files produced by
/// the ML_NesPlayer project's FCEUX fork also carry fork-specific extras (microphone, FDS, NewPPU,
/// length) that aren't part of the documented format and aren't required for a movie to be valid -
/// Fm2Movie's reader tolerates them via RawFields, but this writer doesn't manufacture fake values
/// for keys that don't mean anything in BrokenNes.
///
/// romChecksum is written in the documented "0x<hex>" form (not the "base64:...." form observed in
/// some real-world files - see Fm2Movie's class doc) since that's what the format spec itself
/// describes as canonical.
/// </summary>
public sealed class Fm2Writer
{
    public int Version = 3;
    public int EmuVersion;
    public int RerecordCount;
    public bool PalFlag;
    public bool Fourscore;
    public int Port0 = (int)Fm2InputDevice.Gamepad;
    public int Port1 = (int)Fm2InputDevice.Gamepad;
    public int Port2;
    public string RomFilename = string.Empty;
    public byte[]? RomChecksumMd5;
    public string Guid = System.Guid.NewGuid().ToString("D").ToUpperInvariant();
    public List<string> Comments { get; } = new();

    private readonly List<Fm2Frame> _frames = new();
    public int FrameCount => _frames.Count;

    /// <summary>Records one frame's input. Buttons are in BrokenNes's own order (see Input.cs:10) -
    /// the RLDUTSBA text translation happens entirely inside this writer, same as decoding does in
    /// Fm2Movie's reader.</summary>
    public void RecordFrame(bool reset, bool[] p1, bool[]? p2 = null)
    {
        _frames.Add(new Fm2Frame { Reset = reset, P1 = p1, P2 = p2 });
    }

    public void Save(string path)
    {
        var sb = new StringBuilder();
        void Line(string key, string value) => sb.Append(key).Append(' ').Append(value).Append('\n');

        Line("version", Version.ToString(CultureInfo.InvariantCulture));
        Line("emuVersion", EmuVersion.ToString(CultureInfo.InvariantCulture));
        Line("rerecordCount", RerecordCount.ToString(CultureInfo.InvariantCulture));
        Line("palFlag", PalFlag ? "1" : "0");
        Line("romFilename", RomFilename);
        Line("romChecksum", "0x" + Convert.ToHexString(RomChecksumMd5 ?? Array.Empty<byte>()));
        Line("guid", Guid);
        Line("fourscore", Fourscore ? "1" : "0");
        Line("port0", Port0.ToString(CultureInfo.InvariantCulture));
        Line("port1", Port1.ToString(CultureInfo.InvariantCulture));
        Line("port2", Port2.ToString(CultureInfo.InvariantCulture));
        foreach (var comment in Comments) Line("comment", comment);

        foreach (var frame in _frames)
        {
            sb.Append('|').Append(frame.Reset ? '1' : '0').Append('|');
            sb.Append(EncodeGamepadField(frame.P1));
            sb.Append('|');
            if (Fourscore)
            {
                sb.Append(EncodeGamepadField(frame.P2 ?? EmptyButtons)).Append('|');
                sb.Append(EncodeGamepadField(frame.P3 ?? EmptyButtons)).Append('|');
                sb.Append(EncodeGamepadField(frame.P4 ?? EmptyButtons)).Append('|');
            }
            else
            {
                sb.Append(frame.P2 != null ? EncodeGamepadField(frame.P2) : string.Empty).Append('|');
            }
            sb.Append('\n');
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static readonly bool[] EmptyButtons = new bool[8];

    /// <summary>Inverse of Fm2Movie's DecodeGamepadField: BrokenNes order back to an 8-char RLDUTSBA
    /// field, using each button's own mnemonic letter for "pressed" and '.' for "not pressed" -
    /// matches the character convention observed in real recorded movies.</summary>
    private static string EncodeGamepadField(bool[] brokenNesOrder)
    {
        const string mnemonics = "RLDUTSBA";
        Span<char> chars = stackalloc char[8];
        for (int fm2Index = 0; fm2Index < 8; fm2Index++)
            chars[fm2Index] = brokenNesOrder[7 - fm2Index] ? mnemonics[fm2Index] : '.';
        return new string(chars);
    }
}
