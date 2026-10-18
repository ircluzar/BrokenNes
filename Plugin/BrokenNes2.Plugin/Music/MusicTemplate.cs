// The template ROM of "Test MIDI export": VRUN's AudioPlayer (the game's own sound engine and player screen) built with a one-song roster whose four
// opcode-stream arrays are padded to the converter's maximum (BrokenDev/tools/music/build-musictest.ps1). A song is put in by writing its four streams over
// those arrays by PRG offset, its name over the 24-character title, and its loop length where the player reads it: no build tools, a few kilobytes of writes.
//
// The manifest beside the ROM (Resources/musictest.txt) says where everything is and which format it is. A manifest of another format or version, a ROM
// that is not the one the manifest describes, an array that does not end with its filler, a period table other than the converter's: each is refused
// with the reason, never used.
//
// Pure code (no UI, no emulator): the plugin and the test bench (FruityHost midi-test) use the same file.
using System.Globalization;
using System.Security.Cryptography;
using Vrun.Audio;

namespace BrokenNes2.Music;

public sealed class MusicTemplate
{
    public const string FormatName = "bn2-musictest";
    public const int FormatVersion = 1;
    /// <summary>The opcode format the converter writes and the template's decoders read (Midi2Nes.cs header).</summary>
    public const string Opcodes = "vrun-music-1";
    public const int HeaderBytes = 16;
    /// <summary>The AudioPlayer's charmap: a character's tile is its ASCII code - 32, and the string ends with the charmap's '\0', tile 95.</summary>
    public const byte StringEnd = 95;
    public const int TitleMax = 24;
    /// <summary>Frames the template runs from power-on before its first decode: the song's first opcodes are read in the frame after these (measured by the
    /// engine check, FruityHost midi-test: on the plugin's machine the decode begins in the 7th frame, index 6, for every song; the boot does not depend on the song).</summary>
    public const int Preroll = 6;

    public static readonly string[] ChannelNames = ["square1", "square2", "triangle", "noise"];

    public byte[] Rom { get; }
    public int Region { get; }
    public int[] Offsets { get; }
    public byte[] Fillers { get; }
    public int TitleOffset { get; }
    public int TitleBytes { get; }
    public int[] LoopLo { get; }
    public int[] LoopHi { get; }
    public string RomSha256 { get; }
    /// <summary>The manifest's text as read (the template-equality check compares it).</summary>
    public string Manifest { get; }

    private MusicTemplate(byte[] rom, string manifest, int region, int[] offsets, byte[] fillers, int titleOffset, int titleBytes, int[] lo, int[] hi, string sha)
    {
        Rom = rom; Manifest = manifest; Region = region; Offsets = offsets; Fillers = fillers; TitleOffset = titleOffset; TitleBytes = titleBytes; LoopLo = lo; LoopHi = hi; RomSha256 = sha;
    }

    /// <summary>The key = value lines of a manifest (comments and blank lines skipped). Null when a line is not of that shape.</summary>
    public static Dictionary<string, string>? ParseManifest(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) return null;
            d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    private static bool Hex(string s, out int v) => int.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);

    private static int[]? HexList(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var r = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++) if (!Hex(parts[i], out r[i])) return null;
        return r;
    }

    public static string Sha256Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>The SHA-256 of the converter's period tables (PERIOD_LO then PERIOD_HI): the template's decoders turn a note number into a period through
    /// the tables baked into it, so they must be the ones the converter that writes the notes assumes.</summary>
    public static string PeriodsSha256()
    {
        var (lo, hi) = Midi2Nes.BuildPeriodTable();
        return Sha256Hex([.. lo, .. hi]);
    }

    /// <summary>Reads and checks a template. Null with the reason (for the user) when it cannot be used.</summary>
    public static MusicTemplate? Load(byte[] rom, string manifest, out string? problem)
    {
        problem = null;
        var m = ParseManifest(manifest);
        string? Get(string k) => m != null && m.TryGetValue(k, out var v) ? v : null;
        if (m == null || Get("format") != FormatName) { problem = "the music test template's description is not a BrokenNes2 music test manifest"; return null; }
        if (!int.TryParse(Get("version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int version)) { problem = "the music test template's description has no version"; return null; }
        if (version != FormatVersion) { problem = $"the music test template is version {version}; this build of BrokenNes2 understands version {FormatVersion} only"; return null; }
        if (Get("opcodes") != Opcodes) { problem = $"the music test template plays the opcode format '{Get("opcodes")}'; this build's converter writes '{Opcodes}'"; return null; }
        if (!int.TryParse(Get("region"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int region) || region != Midi2Nes.MaxChannelBytes)
        { problem = $"the music test template's stream regions are {Get("region")} bytes; the converter writes up to {Midi2Nes.MaxChannelBytes}"; return null; }
        if (!int.TryParse(Get("rom-bytes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int romBytes) || romBytes != rom.Length) { problem = $"the music test template ROM is {rom.Length} bytes, its description says {Get("rom-bytes")}"; return null; }
        string sha = Sha256Hex(rom);
        if (!string.Equals(Get("rom-sha256"), sha, StringComparison.OrdinalIgnoreCase)) { problem = "the music test template ROM is not the one its description was made for (its SHA-256 differs)"; return null; }
        if (rom.Length < HeaderBytes + 16384 || rom[0] != 'N' || rom[1] != 'E' || rom[2] != 'S' || rom[3] != 0x1A) { problem = "the music test template is not an iNES ROM"; return null; }
        int mapper = (rom[7] & 0xF0) | (rom[6] >> 4);
        if (mapper != 30) { problem = $"the music test template is a mapper {mapper} ROM; the AudioPlayer is mapper 30"; return null; }
        int prg = rom[4] * 16384;
        if (HeaderBytes + prg > rom.Length) { problem = "the music test template's PRG is shorter than its header says"; return null; }
        if (Get("periods-sha256") != PeriodsSha256()) { problem = "the music test template's note-period table is not the one this build's converter assumes"; return null; }

        var offsets = new int[4];
        var fillers = HexList(Get("filler"));
        if (fillers == null || fillers.Length != 4 || fillers.Any(f => f is < 0x81 or > 0xFE)) { problem = "the music test template's description has no valid filler bytes"; return null; }
        for (int c = 0; c < 4; c++)
        {
            if (!Hex(Get(ChannelNames[c]) ?? "", out offsets[c]) || offsets[c] < 0 || offsets[c] + region > prg) { problem = $"the music test template's {ChannelNames[c]} region is outside the ROM"; return null; }
            // the region holds the template's own song: a stream that ends with the loop opcode, then this channel's filler up to the end
            var r = rom.AsSpan(HeaderBytes + offsets[c], region);
            int end = r.Length;
            while (end > 0 && r[end - 1] == fillers[c]) end--;
            if (end == r.Length || end == 0 || r[end - 1] != 0xFF) { problem = $"the music test template's {ChannelNames[c]} region does not hold a song stream followed by its filler: the description does not match the ROM"; return null; }
        }
        for (int a = 0; a < 4; a++) for (int b = a + 1; b < 4; b++)
            if (offsets[a] < offsets[b] + region && offsets[b] < offsets[a] + region) { problem = "the music test template's stream regions overlap"; return null; }
        if (!Hex(Get("title") ?? "", out int titleOff) || !int.TryParse(Get("title-bytes"), out int titleBytes) || titleBytes != TitleMax + 1 || titleOff < 0 || titleOff + titleBytes > prg || rom[HeaderBytes + titleOff + TitleMax] != StringEnd)
        { problem = "the music test template's title is not where its description says"; return null; }
        var lo = HexList(Get("loop-lo")); var hi = HexList(Get("loop-hi"));
        if (!int.TryParse(Get("loop-template"), out int loopTemplate) || lo == null || hi == null || lo.Length == 0 || lo.Length != hi.Length
            || lo.Concat(hi).Any(o => o < 0 || o >= prg) || lo.Any(o => rom[HeaderBytes + o] != (loopTemplate & 0xFF)) || hi.Any(o => rom[HeaderBytes + o] != (loopTemplate >> 8 & 0xFF)))
        { problem = "the music test template's loop-length bytes are not where its description says"; return null; }
        return new MusicTemplate(rom, manifest, region, offsets, fillers.Select(f => (byte)f).ToArray(), titleOff, titleBytes, lo, hi, sha);
    }

    /// <summary>The title as the AudioPlayer's roster would have it (gen_music_decoders.py sanitize_title): upper case, only the characters its font has,
    /// runs of spaces made one, at most 24; "MIDI TEST" when nothing is left.</summary>
    public static string Title(string? name)
    {
        const string allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -.!?':()&+/#,";
        var sb = new System.Text.StringBuilder();
        foreach (char ch in (name ?? "").ToUpperInvariant())
            if (allowed.IndexOf(ch) >= 0 && !(ch == ' ' && (sb.Length == 0 || sb[^1] == ' '))) sb.Append(ch);
        string s = sb.ToString().Trim();
        if (s.Length == 0) s = "MIDI TEST";
        return s.Length > TitleMax ? s[..TitleMax] : s;
    }

    /// <summary>A copy of the template with the song in it: each channel's stream over its region (the rest of the region filled with the channel's filler, so
    /// nothing of the template's own song is left), the title, the loop length. Throws when a stream does not fit (the converter never makes one that long).</summary>
    public byte[] Patch(byte[][] channels, int loopFrames, string title)
    {
        if (channels.Length != 4) throw new ArgumentException("four channel streams are needed");
        var rom = (byte[])Rom.Clone();
        for (int c = 0; c < 4; c++)
        {
            var s = channels[c];
            int skew = MusicMutate.Has(MusicMutate.PatchSkew) && c == 0 ? 1 : 0;           // (the self-test's deliberate bug: square 1 written one byte late)
            if (s.Length == 0 || s.Length + skew > Region) throw new ArgumentException($"{ChannelNames[c]}: {s.Length} bytes do not fit the template's {Region}");
            var r = rom.AsSpan(HeaderBytes + Offsets[c], Region);
            r.Fill(Fillers[c]);
            s.CopyTo(r[skew..]);
        }
        var t = Title(title);
        var tr = rom.AsSpan(HeaderBytes + TitleOffset, TitleBytes);
        tr.Fill(StringEnd);
        for (int i = 0; i < t.Length; i++) tr[i] = (byte)(t[i] - 32);
        int loop = loopFrames is > 0 and <= 0xFFFF ? loopFrames : 0;                         // 0 = unknown to the player (it then does not count loops)
        foreach (int o in LoopLo) rom[HeaderBytes + o] = (byte)(loop & 0xFF);
        foreach (int o in LoopHi) rom[HeaderBytes + o] = (byte)(loop >> 8);
        return rom;
    }

    // ---- the template built into the plugin ----

    private static readonly object gate = new();
    private static MusicTemplate? builtin;
    private static string? builtinProblem;
    private static bool builtinRead;
    /// <summary>Tests only: a template read from files instead of the built-in one (null: the built-in one).</summary>
    public static (byte[] Rom, string Manifest)? Override;

    /// <summary>The template inside the plugin (read and checked once), or null with the reason.</summary>
    public static MusicTemplate? Builtin(out string? problem)
    {
        lock (gate)
        {
            if (Override is { } o) return Load(o.Rom, o.Manifest, out problem);
            if (!builtinRead)
            {
                builtinRead = true;
                try
                {
                    var rom = ReadResource("musictest.nes");
                    var text = System.Text.Encoding.UTF8.GetString(ReadResource("musictest.txt"));
                    builtin = Load(rom, text, out builtinProblem);
                }
                catch (Exception e) { builtinProblem = "the music test template is missing from this build of BrokenNes2: " + e.Message; }
            }
            problem = builtinProblem;
            return builtin;
        }
    }

    private static byte[] ReadResource(string name)
    {
        using var s = typeof(MusicTemplate).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"no resource {name}");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>Deliberate bugs the self-test switches on (plugin hook 616, FruityHost midi-test) to prove its checks can fail. Zero in normal use: each is one test of
/// this mask at the place named.</summary>
public static class MusicMutate
{
    public static int Mask;
    public const int PatchSkew = 1 << 0;          // the template is patched with square 1 one byte late (engine checks)
    public const int SectionDropsMidi = 1 << 1;   // a project section is written without the MIDI bytes (project check)
    public const int ReloadCached = 1 << 2;       // Reload converts the bytes it already had instead of reading the file again (reload check)
    public const int TransportIgnored = 1 << 3;   // FL's transport start does not restart the song (transport check)
    public const int RefusalSilent = 1 << 4;      // a MIDI that cannot be used leaves no message (refusal check)
    public const int RestartAllocates = 1 << 5;   // a restart drops the prepared stems, so the audio thread makes new ones (allocation check)
    public const int PlayCallsStop = 1 << 6;      // the Play button stops (button-flow check)
    public const int FactsHideDropped = 1 << 7;   // the facts say no note was dropped (facts check)
    public const int ConverterOffByOne = 1 << 8;  // every note one semitone up after conversion (converter identity check, midi-test)
    public const int StemsShifted = 1 << 9;       // after a restart each channel gets the next channel's prepared stem (stems check)
    public static bool Has(int bit) => (Mask & bit) != 0;
}
