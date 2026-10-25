using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BrokenNes.Workshop.Tas;
using NesEmulator;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Movies;

/// <summary>
/// A <see cref="Movie"/> to the other emulators' formats, for cross-emulator replays (the plan's decision: our own format, with exports):
/// FCEUX's <c>.fm2</c> (the NES) and BizHawk's <c>.bk2</c> (the NES on NesHawk, the SNES on BSNESv115+, the Game Boy and Color on Gambatte),
/// and an <c>.fm2</c> back to a movie. Each export says what it cannot carry (a start state, the frame timing) instead of dropping it quietly.
/// </summary>
internal static class MovieExport
{
    /// <summary>What an export wrote, and what the reader should know.</summary>
    public sealed record Result(string Path, long Frames, List<string> Notes);

    private static List<string> CommonNotes(Movie mv, string emu)
    {
        var n = new List<string>();
        if (mv.Start == MovieStart.State) throw new NotSupportedException($"the movie starts from a BrokenNes save state, which {emu} cannot load: only a movie that starts at power-on exports");
        if (mv.Console == "nes" && !mv.NtscTiming)
            n.Add($"the movie was recorded with BrokenNes's 60 fps frames (timing 60); {emu} runs NTSC frames (60.0988 fps), so the same input can land a few CPU cycles off: record with --ntsc for frame-exact replays");
        if (mv.Console == "nes" && mv.PowerOn != "fceux")
            n.Add($"the movie powers the NES on with RAM {mv.PowerOn}; {emu} uses its own power-on RAM");
        return n;
    }

    // ------------------------------------------------------------------------------------------------ FCEUX .fm2

    public static Result ToFm2(Movie mv, byte[] romFile, string path)
    {
        if (mv.Console != "nes") throw new NotSupportedException($".fm2 is FCEUX's NES format; this movie is for the {mv.Console}");
        var notes = CommonNotes(mv, "FCEUX");
        var nes = new NES();
        nes.LoadROM(romFile);
        var w = new Fm2Writer
        {
            EmuVersion = 22020, RomFilename = Path.GetFileNameWithoutExtension(mv.RomName), RomChecksumMd5 = nes.ComputeRomMd5(), ChecksumBase64 = true,
            Port1 = (int)Fm2InputDevice.Gamepad,
        };
        w.Comments.Add($"author {MovieFormat.Name} export ({MovieMachine.EmulatorBuild()})");
        if (mv.Build.Length > 0) w.Comments.Add("build " + mv.Build);
        var c = new MoviePlayer.Cursor(mv);
        for (long f = 0; f < mv.Frames; f++) { c.At(f); w.RecordFrame(false, DebugPad(c.P1), DebugPad(c.P2)); }
        w.Save(path);
        return new Result(path, mv.Frames, notes);
    }

    private static bool[] BrokenNesPad(PadButtons b) => BrokenNes.Workshop.Debug.DebugHost.NesPad(b);
    private static bool[] DebugPad(PadButtons b) => BrokenNesPad(b);

    /// <summary>An .fm2 (power-on, gamepads) as a movie on this ROM: its frames replayed here to fill the keyframes.</summary>
    public static Movie FromFm2(string fm2Path, MovieMachine m, int keyEvery, string build)
    {
        var (header, frames) = Fm2Movie.Load(fm2Path);
        if (m.ConsoleKey != "nes") throw new NotSupportedException("an .fm2 is a NES movie");
        if (header.RawFields.TryGetValue("savestate", out _)) throw new NotSupportedException("this .fm2 starts from an FCEUX savestate, which BrokenNes cannot load");
        var nes = new NES(); nes.LoadROM(m.RomFile);
        var mv = MoviePlayer.NewFor(m, frames.Count, keyEvery, build);
        mv.Comments.Add($"imported from {Path.GetFileName(fm2Path)} (FCEUX emuVersion {header.EmuVersion}, rom {header.RomFilename})");
        if (header.RomChecksumMd5 != null && !header.RomChecksumMd5.SequenceEqual(nes.ComputeRomMd5()))
            mv.Comments.Add($"the .fm2 names another ROM (md5 {Convert.ToHexString(header.RomChecksumMd5).ToLowerInvariant()}, this one {Convert.ToHexString(nes.ComputeRomMd5()).ToLowerInvariant()})");
        for (int f = 0; f < frames.Count; f++)
        {
            var fr = frames[f];
            if (fr.Reset) throw new NotSupportedException($"frame {f} of the .fm2 presses reset; a {MovieFormat.Name} has no resets yet");
            var p1 = FromNes(fr.P1); var p2 = fr.P2 != null ? FromNes(fr.P2) : PadButtons.None;
            mv.SetPads(f, p1, p2);
            m.Frame(p1, p2);
            if (m.Crashed) throw new InvalidOperationException($"the game crashed at frame {f + 1}: {m.CrashInfo}");
            if (Movie.IsKeyFrame(f + 1, frames.Count, keyEvery)) mv.Keys.Add(new MovieKey { Frame = f + 1, Picture = m.Picture(), Ram = m.Ram() });
        }
        if (mv.Inputs.Count == 0) mv.SetPads(0, PadButtons.None, PadButtons.None);
        return mv;
    }

    private static PadButtons FromNes(bool[] b)
    {
        var order = new[] { PadButtons.A, PadButtons.B, PadButtons.Select, PadButtons.Start, PadButtons.Up, PadButtons.Down, PadButtons.Left, PadButtons.Right };
        PadButtons p = PadButtons.None;
        for (int i = 0; i < 8; i++) if (b[i]) p |= order[i];
        return p;
    }

    // ------------------------------------------------------------------------------------------------ BizHawk .bk2

    /// <summary>A BizHawk pad: the system, the core, the buttons in the log's order (BizHawk's names, without the player prefix), their letters.</summary>
    private sealed record Bk2Pad(string Platform, string Core, string Prefix, string[] Console, (string Name, PadButtons Bit, char Letter)[] Buttons, bool TwoPads);

    private static Bk2Pad PadFor(string console) => console switch
    {
        // the names are those of BizHawk 2.11.1's controllers (its defctrl.json): "NES Controller", "SNES Controller", "Gameboy Controller"
        "nes" => new Bk2Pad("NES", "NesHawk", "P{0} ", new[] { "Reset", "Power" },
            new[] { ("Up", PadButtons.Up, 'U'), ("Down", PadButtons.Down, 'D'), ("Left", PadButtons.Left, 'L'), ("Right", PadButtons.Right, 'R'),
                    ("Start", PadButtons.Start, 'S'), ("Select", PadButtons.Select, 's'), ("B", PadButtons.B, 'B'), ("A", PadButtons.A, 'A') }, true),
        "snes" => new Bk2Pad("SNES", "BSNESv115+", "P{0} ", new[] { "Reset", "Power" },
            new[] { ("Up", PadButtons.Up, 'U'), ("Down", PadButtons.Down, 'D'), ("Left", PadButtons.Left, 'L'), ("Right", PadButtons.Right, 'R'),
                    ("Select", PadButtons.Select, 's'), ("Start", PadButtons.Start, 'S'), ("Y", PadButtons.Y, 'Y'), ("B", PadButtons.B, 'B'),
                    ("X", PadButtons.X, 'X'), ("A", PadButtons.A, 'A'), ("L", PadButtons.L, 'l'), ("R", PadButtons.R, 'r') }, true),
        "gb" or "gbc" => new Bk2Pad(console == "gb" ? "GB" : "GBC", "Gambatte", "", new[] { "Power" },
            new[] { ("Up", PadButtons.Up, 'U'), ("Down", PadButtons.Down, 'D'), ("Left", PadButtons.Left, 'L'), ("Right", PadButtons.Right, 'R'),
                    ("Start", PadButtons.Start, 'S'), ("Select", PadButtons.Select, 's'), ("B", PadButtons.B, 'B'), ("A", PadButtons.A, 'A') }, false),
        _ => throw new NotSupportedException($"BizHawk .bk2 export covers the NES, SNES and Game Boy / Color, not the {console}"),
    };

    /// <summary>The SHA-1 BizHawk's movie header names: the NES's PRG + CHR (no iNES header), any other console's whole ROM.</summary>
    public static string BizHawkSha1(string console, byte[] romFile)
    {
        byte[] data = romFile;
        if (console == "nes" && romFile.Length > 16 && romFile[0] == 'N' && romFile[1] == 'E' && romFile[2] == 'S' && romFile[3] == 0x1A)
        {
            int skip = 16 + ((romFile[6] & 4) != 0 ? 512 : 0);
            data = romFile[skip..];
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(data));
    }

    public static Result ToBk2(Movie mv, byte[] romFile, string path)
    {
        var pad = PadFor(mv.Console);
        var notes = CommonNotes(mv, "BizHawk");
        if (!pad.TwoPads && mv.UsesPad2) throw new NotSupportedException($"the movie presses pad 2, which the {pad.Platform} does not have");
        var header = new StringBuilder();
        void H(string k, string v) => header.Append(k).Append(' ').Append(v).Append('\n');
        H("MovieVersion", "BizHawk v2.0.0");
        H("Author", MovieFormat.Name + " export");
        H("emuVersion", "Version 2.11.1");
        H("OriginalEmuVersion", "Version 2.11.1");
        H("Platform", pad.Platform);
        H("GameName", Path.GetFileNameWithoutExtension(mv.RomName));
        H("SHA1", BizHawkSha1(mv.Console, romFile));
        H("Core", pad.Core);
        H("rerecordCount", "0");
        int players = pad.TwoPads ? 2 : 1;
        var key = new StringBuilder("LogKey:#").Append(string.Join("|", pad.Console)).Append('|');
        for (int p = 1; p <= players; p++)
        {
            key.Append('#');
            key.Append(string.Join("|", pad.Buttons.Select(b => string.Format(pad.Prefix, p) + b.Name))).Append('|');
        }
        var log = new StringBuilder("[Input]\n").Append(key).Append('\n');
        var c = new MoviePlayer.Cursor(mv);
        for (long f = 0; f < mv.Frames; f++)
        {
            c.At(f);
            log.Append('|').Append(new string('.', pad.Console.Length)).Append('|');
            for (int p = 1; p <= players; p++)
            {
                var held = p == 1 ? c.P1 : c.P2;
                foreach (var b in pad.Buttons) log.Append((held & b.Bit) != 0 ? b.Letter : '.');
                log.Append('|');
            }
            log.Append('\n');
        }
        log.Append("[/Input]\n");
        if (File.Exists(path)) File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Entry(string name, string text) { using var s = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); s.Write(text); }
            Entry("Header.txt", header.ToString());
            Entry("Input Log.txt", log.ToString());
            Entry("Comments.txt", $"exported from a {MovieFormat.Name} ({MovieFormat.Extension}) by {MovieMachine.EmulatorBuild()}; recorded on BrokenNes {mv.Cores}\n");
            Entry("Subtitles.txt", "");
        }
        return new Result(path, mv.Frames, notes);
    }
}
