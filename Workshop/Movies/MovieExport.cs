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
/// FCEUX's <c>.fm2</c> (the NES) and BizHawk's <c>.bk2</c> (the NES on NesHawk, the SNES on BSNESv115+, the Game Boy and Color on SameBoy or
/// Gambatte, the Master System on Genplus-gx or SMSHawk, the Game Gear on SMSHawk or Genplus-gx, the Genesis on Genplus-gx with a three- or
/// six-button pad), and an <c>.fm2</c> or a <c>.bk2</c> back to a movie (checked against EmuHawk 2.11.1 by BrokenDev/tests/movie-cross.ps1
/// and BrokenDev/tests/bk2-import.ps1). Each export says what it cannot carry (a start state, the frame timing) instead of dropping it quietly.
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

    /// <summary>A BizHawk pad: the system, the core, the console buttons (no player prefix; each pressed by a pad-1 bit, or None for never), the
    /// pad buttons in the log's order (BizHawk's names, without the player prefix) with their letters, and pad 2's buttons (null: no pad 2).</summary>
    private sealed record Bk2Pad(string Platform, string Core, string Prefix, (string Name, PadButtons Bit, char Letter)[] Console,
        (string Name, PadButtons Bit, char Letter)[] Buttons, (string Name, PadButtons Bit, char Letter)[]? Pad2)
    {
        public bool TwoPads => Pad2 != null;
    }

    private static readonly (string Name, PadButtons Bit, char Letter)[] NesButtons =
        { ("Up", PadButtons.Up, 'U'), ("Down", PadButtons.Down, 'D'), ("Left", PadButtons.Left, 'L'), ("Right", PadButtons.Right, 'R'),
          ("Start", PadButtons.Start, 'S'), ("Select", PadButtons.Select, 's'), ("B", PadButtons.B, 'B'), ("A", PadButtons.A, 'A') };
    private static readonly (string Name, PadButtons Bit, char Letter)[] SnesButtons =
        { ("Up", PadButtons.Up, 'U'), ("Down", PadButtons.Down, 'D'), ("Left", PadButtons.Left, 'L'), ("Right", PadButtons.Right, 'R'),
          ("Select", PadButtons.Select, 's'), ("Start", PadButtons.Start, 'S'), ("Y", PadButtons.Y, 'Y'), ("B", PadButtons.B, 'B'),
          ("X", PadButtons.X, 'X'), ("A", PadButtons.A, 'A'), ("L", PadButtons.L, 'l'), ("R", PadButtons.R, 'r') };
    // the Master System / Game Gear pad (SMSHawk's "SMS Controller" / "GG Controller"): BrokenNes's A / B are the pad's 1 / 2 (SmsSession.SetPad)
    private static readonly (string Name, PadButtons Bit, char Letter)[] SmsButtons =
        { ("Up", PadButtons.Up, 'U'), ("Down", PadButtons.Down, 'D'), ("Left", PadButtons.Left, 'L'), ("Right", PadButtons.Right, 'R'),
          ("B1", PadButtons.A, '1'), ("B2", PadButtons.B, '2') };
    // the Genesis pad (Genplus-gx's "GPGX Genesis Controller"): three buttons and Start, then the six-button pad's X, Y, Z and Mode
    private static readonly (string Name, PadButtons Bit, char Letter)[] Md3Buttons =
        { ("Up", PadButtons.Up, 'U'), ("Down", PadButtons.Down, 'D'), ("Left", PadButtons.Left, 'L'), ("Right", PadButtons.Right, 'R'),
          ("A", PadButtons.A, 'A'), ("B", PadButtons.B, 'B'), ("C", PadButtons.C, 'C'), ("Start", PadButtons.Start, 'S') };
    private static readonly (string Name, PadButtons Bit, char Letter)[] Md6Buttons =
        Md3Buttons.Concat(new[] { ("X", PadButtons.X, 'X'), ("Y", PadButtons.Y, 'Y'), ("Z", PadButtons.Z, 'Z'), ("Mode", PadButtons.Mode, 'M') }).ToArray();

    private static readonly (string Name, PadButtons Bit, char Letter)[] ResetPower = { ("Reset", PadButtons.None, 'r'), ("Power", PadButtons.None, 'P') };

    private static Bk2Pad PadFor(string console) => console switch
    {
        // the names are those of BizHawk 2.11.1's controllers (its defctrl.json): "NES Controller", "SNES Controller", "Gameboy Controller",
        // "SMS Controller", "GG Controller", "GPGX Genesis Controller"
        "nes" => new Bk2Pad("NES", "NesHawk", "P{0} ", ResetPower, NesButtons, NesButtons),
        "snes" => new Bk2Pad("SNES", "BSNESv115+", "P{0} ", ResetPower, SnesButtons, SnesButtons),
        // BizHawk 2.11.1 checks a movie's platform against the loaded one, which is GB for a Color game on Gambatte too (measured: "GBC" is refused)
        "gb" or "gbc" => new Bk2Pad("GB", "Gambatte", "", new[] { ("Power", PadButtons.None, 'P') }, NesButtons, null),
        // the Master System on Genplus-gx: Power, Reset and Pause are console buttons (BrokenNes: Reset and Pause are pad 1's Mode and Select, SmsSession.SetPad)
        "sms" => new Bk2Pad("SMS", "Genplus-gx", "P{0} ", new[] { ("Power", PadButtons.None, 'P'), ("Reset", PadButtons.Mode, 'r'), ("Pause", PadButtons.Select, 'p') }, SmsButtons, SmsButtons),
        // the Game Gear: one pad with its own Start (BrokenNes: Start), Reset on the console (BrokenNes: Mode)
        "gg" => new Bk2Pad("GG", "SMSHawk", "P{0} ", new[] { ("Reset", PadButtons.Mode, 'r') }, SmsButtons.Concat(new[] { ("Start", PadButtons.Start, 'S') }).ToArray(), null),
        // the Genesis: BrokenNes's pad 1 is a six-button pad, pad 2 a three-button one (MdSession); Genplus-gx's UseSixButton is for both ports
        "md" => new Bk2Pad("GEN", "Genplus-gx", "P{0} ", new[] { ("Power", PadButtons.None, 'P'), ("Reset", PadButtons.None, 'r') }, Md6Buttons, Md6Buttons),
        _ => throw new NotSupportedException($"BizHawk .bk2 covers the NES, SNES, Game Boy / Color, Master System, Game Gear and Genesis, not the {console}"),
    };

    /// <summary>A pad's held buttons as the console sees them: the Genesis's R / L / Select are C / Z / Mode on BrokenNes (MdSession.SetPad).</summary>
    private static PadButtons Canon(string console, PadButtons b)
    {
        if (console != "md") return b;
        if ((b & PadButtons.R) != 0) b |= PadButtons.C;
        if ((b & PadButtons.L) != 0) b |= PadButtons.Z;
        if ((b & PadButtons.Select) != 0) b |= PadButtons.Mode;
        return b & ~(PadButtons.R | PadButtons.L | PadButtons.Select);
    }

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

    /// <summary>The BizHawk cores a .bk2 can name per console (the first is the default). The Game Boy's default is SameBoy: EmuHawk 2.11.1 plays a
    /// Gambatte movie only with the Game Boy boot ROM ("Missing Firmware", measured even with the movie's sync settings saying EnableBIOS false),
    /// which BrokenNes does not have and never asks for (the no-firmware rule); SameBoy runs its own boot ROM.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Bk2Cores = new Dictionary<string, string[]>
    {
        ["nes"] = new[] { "NesHawk" }, ["snes"] = new[] { "BSNESv115+" }, ["gb"] = new[] { "SameBoy", "Gambatte" }, ["gbc"] = new[] { "SameBoy", "Gambatte" },
        // the Master System's default is Genplus-gx: SMSHawk stops on "Missing Firmware ... SMS+Export" for an export cartridge (BrokenDev's
        // emulators.json, measured 2026-11-02), and BrokenNes has no BIOS (the no-firmware rule)
        // (the Game Gear on Genplus-gx is not offered: its pad layout there is not measured yet)
        ["sms"] = new[] { "Genplus-gx", "SMSHawk" }, ["gg"] = new[] { "SMSHawk" }, ["md"] = new[] { "Genplus-gx" },
    };

    public static Result ToBk2(Movie mv, byte[] romFile, string path, string core = "", string mdPad = "")
    {
        var pad = PadFor(mv.Console);
        bool sixButton = mdPad.Length == 0 || mdPad == "6";
        if (mv.Console == "md")
        {
            if (mdPad.Length > 0 && mdPad != "3" && mdPad != "6") throw new NotSupportedException($"--pad is 3 or 6 (the Genesis pad), not '{mdPad}'");
            if (!sixButton) pad = pad with { Buttons = Md3Buttons, Pad2 = Md3Buttons };
        }
        else if (mdPad.Length > 0) throw new NotSupportedException("--pad (3 or 6 buttons) is for the Genesis");
        if (Bk2Cores.TryGetValue(mv.Console, out var cores))
        {
            if (core.Length == 0) core = cores[0];
            if (!cores.Contains(core)) throw new NotSupportedException($"a {mv.Console} .bk2 names one of {string.Join(", ", cores)}, not '{core}'");
            pad = pad with { Core = core };
            // SMSHawk's Master System console has Reset and Pause; Genplus-gx's has Power, Reset and Pause (measured with joypad.get and a
            // recording on EmuHawk 2.11.1: "|P r p|" order)
            if (mv.Console == "sms" && core == "SMSHawk") pad = pad with { Console = new[] { ("Reset", PadButtons.Mode, 'r'), ("Pause", PadButtons.Select, 'p') } };
        }
        var notes = CommonNotes(mv, "BizHawk " + pad.Core);
        if (pad.Core == "Gambatte") notes.Add("EmuHawk 2.11.1 plays a Gambatte movie only with the Game Boy boot ROM, which BrokenDev does not provide: name SameBoy (the default) to play it without one");
        if (pad.Core == "SameBoy") notes.Add("SameBoy plays its boot animation before the game (about 189 frames), BrokenNes starts after the boot ROM: the same input lands that much earlier in the game; compare on BizHawk with the same input given frame by frame (tests/movie-cross.ps1)");
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
        // the Game Boy cores log one group, the pad then Power (measured: SameBoy records its lines as "|....S....|", Start fifth, Power last);
        // the other cores a console group, then one group per pad
        bool oneGroup = pad.Prefix.Length == 0;
        var key = new StringBuilder("LogKey:#");
        if (oneGroup) key.Append(string.Join("|", pad.Buttons.Select(b => b.Name).Concat(pad.Console.Select(b => b.Name)))).Append('|');
        else key.Append(string.Join("|", pad.Console.Select(b => b.Name))).Append('|');
        for (int p = 1; p <= players && !oneGroup; p++)
        {
            key.Append('#');
            key.Append(string.Join("|", (p == 1 ? pad.Buttons : pad.Pad2!).Select(b => string.Format(pad.Prefix, p) + b.Name))).Append('|');
        }
        var log = new StringBuilder("[Input]\n").Append(key).Append('\n');
        var c = new MoviePlayer.Cursor(mv);
        var dropped = new HashSet<(int, PadButtons)>();
        var consoleBits = pad.Console.Aggregate(PadButtons.None, (a, b) => a | b.Bit);
        for (long f = 0; f < mv.Frames; f++)
        {
            c.At(f);
            var p1 = Canon(mv.Console, c.P1);
            log.Append('|');
            string consoleCells = new string(pad.Console.Select(b => b.Bit != PadButtons.None && (p1 & b.Bit) != 0 ? b.Letter : '.').ToArray());
            if (!oneGroup) log.Append(consoleCells).Append('|');
            for (int p = 1; p <= players; p++)
            {
                var held = p == 1 ? p1 : Canon(mv.Console, c.P2);
                var buttons = p == 1 ? pad.Buttons : pad.Pad2!;
                var unsent = held & ~buttons.Aggregate(PadButtons.None, (a, b) => a | b.Bit) & ~(p == 1 ? consoleBits : PadButtons.None);
                if (unsent != PadButtons.None && dropped.Add((p, unsent))) notes.Add($"frame {f}: pad {p} holds {unsent}, which BizHawk's {pad.Platform} pad has no button for");
                foreach (var b in buttons) log.Append((held & b.Bit) != 0 ? b.Letter : '.');
                if (oneGroup) log.Append(consoleCells);
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
            if (pad.Core == "Gambatte")
            {
                // A movie plays with the sync settings it carries; without them EmuHawk 2.11.1 asks Gambatte for the boot ROM and stops ("Missing
                // Firmware": measured). BrokenNes has no boot ROM (the no-firmware rule), so the movie names Gambatte's own defaults without one:
                // the settings EmuHawk itself writes back after a run without a movie (EnableBIOS false, ConsoleMode 0 = the cartridge decides).
                Entry("SyncSettings.json", "{\"o\":{\"$type\":\"BizHawk.Emulation.Cores.Nintendo.Gameboy.Gameboy+GambatteSyncSettings, BizHawk.Emulation.Cores\",\"EnableBIOS\":false,\"ConsoleMode\":0,"
                    + "\"CartBusPullUpTime\":8,\"RealTimeRTC\":false,\"RTCDivisorOffset\":0,\"InitialTime\":0,\"EnableRemote\":false,\"FrameLength\":0,\"DisplayBG\":true,\"DisplayOBJ\":true,\"DisplayWindow\":true}}");
                notes.Add("the movie asks Gambatte to run without a boot ROM (its sync settings: EnableBIOS false), as BrokenNes does");
            }
            if (pad.Core == "Genplus-gx" && mv.Console == "md")
            {
                // Genplus-gx's defaults as EmuHawk 2.11.1 writes them back after a Genesis run (measured), with UseSixButton set to the pad: BrokenNes's
                // pad 1 is a six-button pad (MdSession), so by default a game that probes for one sees one on both emulators
                Entry("SyncSettings.json", "{\"o\":{\"$type\":\"BizHawk.Emulation.Cores.Consoles.Sega.gpgx.GPGX+GPGXSyncSettings, BizHawk.Emulation.Cores\",\"UseSixButton\":" + (sixButton ? "true" : "false")
                    + ",\"ControlTypeLeft\":1,\"ControlTypeRight\":1,\"Region\":0,\"ForceVDP\":0,\"LoadBIOS\":false,\"Overscan\":0,\"GGExtra\":false,\"SMSFMSoundChip\":1,\"GenesisFMSoundChip\":0,"
                    + "\"Filter\":1,\"LowPassRange\":26214,\"LowFreq\":880,\"HighFreq\":5000,\"LowGain\":1.0,\"MidGain\":1.0,\"HighGain\":1.0,\"BackdropColor\":4294902015,\"SpritesAlwaysOnTop\":false}}");
                notes.Add(sixButton ? "six-button pads on Genplus-gx (BrokenNes's pad 1 is one; its pad 2 is a three-button pad, Genplus-gx makes both six)"
                                    : "three-button pads on Genplus-gx: BrokenNes's pad 1 is a six-button pad, so a game that probes for one can play differently");
            }
        }
        return new Result(path, mv.Frames, notes);
    }

    /// <summary>The BizHawk platform a .bk2 names -> the BrokenNes consoles it can be (GB covers the Color too).</summary>
    private static string[] ConsolesOf(string platform) => platform switch
    {
        "NES" => new[] { "nes" }, "SNES" => new[] { "snes" }, "GB" or "GBC" => new[] { "gb", "gbc" },
        "SMS" => new[] { "sms" }, "GG" => new[] { "gg" }, "GEN" => new[] { "md" },
        _ => throw new NotSupportedException($"a .bk2 for BizHawk's {platform} has no BrokenNes console"),
    };

    /// <summary>A BizHawk .bk2 (power-on, the consoles' standard pads) as a movie on this ROM: its frames replayed here to fill the keyframes. The
    /// log is read by the movie's own LogKey (as BizHawk reads it), so the column order does not matter; a button BrokenNes has no place for, a
    /// press of Power or of the NES / SNES Reset, or a save-state start, is refused instead of dropped.</summary>
    public static Movie FromBk2(string bk2Path, MovieMachine m, int keyEvery, string build, List<string>? notes = null)
    {
        notes ??= new List<string>();
        Dictionary<string, string> header = new();
        List<string> log;
        using (var z = ZipFile.OpenRead(bk2Path))
        {
            string Text(string name) { var e = z.GetEntry(name) ?? throw new InvalidDataException($"{Path.GetFileName(bk2Path)} has no {name}: not a BizHawk .bk2"); using var s = new StreamReader(e.Open()); return s.ReadToEnd(); }
            foreach (var line in Text("Header.txt").Split('\n'))
            {
                var l = line.TrimEnd('\r'); int sp = l.IndexOf(' ');
                if (sp > 0) header[l[..sp]] = l[(sp + 1)..];
            }
            log = Text("Input Log.txt").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (z.GetEntry("Core.bin") != null || (header.TryGetValue("StartsFromSavestate", out var ss) && ss.Equals("True", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException("this .bk2 starts from a BizHawk savestate, which BrokenNes cannot load");
            if (header.TryGetValue("StartsFromSaveRam", out var sr) && sr.Equals("True", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("this .bk2 starts from BizHawk save RAM, which BrokenNes does not load into a movie");
        }
        string platform = header.GetValueOrDefault("Platform", "?");
        if (!ConsolesOf(platform).Contains(m.ConsoleKey)) throw new NotSupportedException($"the .bk2 is for BizHawk's {platform}; the ROM runs on the {m.ConsoleKey}");
        string sha = BizHawkSha1(m.ConsoleKey, m.RomFile);
        if (header.TryGetValue("SHA1", out var hs) && !hs.Equals(sha, StringComparison.OrdinalIgnoreCase))
            notes.Add($"the .bk2 names another ROM (SHA1 {hs}, this one {sha})");
        int keyLine = log.FindIndex(l => l.StartsWith("LogKey:", StringComparison.Ordinal));
        if (keyLine < 0) throw new InvalidDataException("the .bk2's input log has no LogKey");
        // the LogKey: '#' starts a group (one per '|'-separated group in each frame line); the first group is the console's
        var groups = log[keyLine]["LogKey:".Length..].Split('#', StringSplitOptions.RemoveEmptyEntries)
            .Select(g => g.Split('|', StringSplitOptions.RemoveEmptyEntries)).ToList();
        string console = m.ConsoleKey;
        // each column -> (player 0 or 1, bit); a console button presses a pad-1 bit (the Master System's Pause = Select, Reset = Mode)
        var flat = groups.SelectMany(g => g.Select(name => Column(console, name))).ToArray();
        var frames = new List<(PadButtons P1, PadButtons P2)>();
        for (int i = keyLine + 1; i < log.Count; i++)
        {
            string l = log[i];
            if (l.StartsWith("[/Input]", StringComparison.Ordinal)) break;
            if (!l.StartsWith('|')) continue;
            // the cells in the LogKey's order. A line is read by the LogKey's groups when its groups fit them (a missing last group = nothing
            // pressed: NesHawk records only the pad it has plugged in, "|..|........|", under an exported key that names two pads), else as one
            // run of cells ('|'s left out: SameBoy records "|....S....|" under a key that grouped Power apart)
            var lg = l.Split('|'); var lineGroups = lg.Skip(1).Take(Math.Max(0, lg.Length - 2)).ToArray();
            string cells;
            if (lineGroups.Length <= groups.Count && lineGroups.Select((g, i) => g.Length == groups[i].Length).All(x => x))
                cells = string.Concat(lineGroups).PadRight(flat.Length, '.');
            else
            {
                cells = l.Replace("|", "");
                if (cells.Length != flat.Length) throw new InvalidDataException($"frame {frames.Count} of the .bk2 has {cells.Length} buttons, its LogKey {flat.Length}");
            }
            PadButtons a = PadButtons.None, b = PadButtons.None;
            {
                for (int k = 0; k < flat.Length; k++)
                {
                    if (cells[k] == '.' || cells[k] == ' ') continue;
                    var (player, bit, name) = flat[k];
                    if (bit == PadButtons.None)
                    {
                        if (name == "Power" && frames.Count == 0) continue;   // power at frame 0: the power-on the movie starts from anyway
                        throw new NotSupportedException($"frame {frames.Count} of the .bk2 presses {name}; a {MovieFormat.Name} has no {name} button on the {console}");
                    }
                    if (player == 0) a |= bit; else b |= bit;
                }
            }
            frames.Add((a, b));
        }
        if (frames.Count == 0) throw new InvalidDataException("the .bk2's input log has no frames");
        var mv = MoviePlayer.NewFor(m, frames.Count, keyEvery, build);
        mv.Comments.Add($"imported from {Path.GetFileName(bk2Path)} (BizHawk {header.GetValueOrDefault("emuVersion", "?")}, core {header.GetValueOrDefault("Core", "?")}, {platform}, game {header.GetValueOrDefault("GameName", "?")})");
        mv.Comments.AddRange(notes);
        for (int f = 0; f < frames.Count; f++)
        {
            var (p1, p2) = frames[f];
            mv.SetPads(f, p1, p2);
            m.Frame(p1, p2);
            if (m.Crashed) throw new InvalidOperationException($"the game crashed at frame {f + 1}: {m.CrashInfo}");
            if (Movie.IsKeyFrame(f + 1, frames.Count, keyEvery)) mv.Keys.Add(new MovieKey { Frame = f + 1, Picture = m.Picture(), Ram = m.Ram() });
        }
        if (mv.Inputs.Count == 0) mv.SetPads(0, PadButtons.None, PadButtons.None);
        return mv;
    }

    /// <summary>A .bk2 column's BizHawk name -> the BrokenNes pad (0 or 1) and button (None: a console button BrokenNes cannot press).</summary>
    private static (int Player, PadButtons Bit, string Name) Column(string console, string name)
    {
        int player = 0; string b = name;
        if (name.Length > 3 && name[0] == 'P' && char.IsDigit(name[1]) && name[2] == ' ')
        {
            player = name[1] - '1';
            if (player is < 0 or > 1) throw new NotSupportedException($"the .bk2 has a pad {name[1]}, BrokenNes has two");
            b = name[3..];
        }
        bool sega = console is "sms" or "gg";
        PadButtons bit = b switch
        {
            "Up" => PadButtons.Up, "Down" => PadButtons.Down, "Left" => PadButtons.Left, "Right" => PadButtons.Right, "Start" => PadButtons.Start,
            "Select" when !sega => PadButtons.Select, "A" when !sega => PadButtons.A, "B" when !sega => PadButtons.B,
            "X" => PadButtons.X, "Y" => PadButtons.Y, "L" => PadButtons.L, "R" => PadButtons.R,
            "C" when console == "md" => PadButtons.C, "Z" when console == "md" => PadButtons.Z, "Mode" when console == "md" => PadButtons.Mode,
            "B1" when sega => PadButtons.A, "B2" when sega => PadButtons.B,
            "Pause" when console == "sms" => PadButtons.Select,
            "Reset" when sega => PadButtons.Mode,
            "Power" or "Reset" => PadButtons.None,
            _ => throw new NotSupportedException($"the .bk2's '{name}' is not a button of BrokenNes's {console} pad"),
        };
        return (player, bit, b);
    }
}
