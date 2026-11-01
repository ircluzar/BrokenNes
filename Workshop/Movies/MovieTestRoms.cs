using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BrokenNes.Workshop.Debug;

namespace BrokenNes.Workshop.Movies;

/// <summary>
/// The movie tests' NES game (milestone D5), assembled here so no toolchain is needed: a title that waits for a button, then a game that
/// loses a life every 64 frames down to a floor and scores while A is held. Its build settings are a small text file (the game's
/// "source" in the planted-regression repository bisect is proven on):
/// <code>
///   version 3          a byte stored at version (a harmless change between commits)
///   start Start        the title's button (Select: a different menu, the movie goes off course)
///   floor 1            lives stop at this (0: the regression, the game ends with no lives)
///   bonus 1            score per frame A is held
/// </code>
/// Anything else in the file (an unknown key, a bad value) fails the build, as a syntax error would.
/// </summary>
internal static class MovieTestRoms
{
    public sealed class Settings
    {
        public int Version = 1, Floor = 1, Bonus = 1;
        public string Start = "Start";
    }

    public static Settings ParseSettings(string text)
    {
        var s = new Settings();
        int n = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            n++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 2) throw new FormatException($"line {n}: '{line}' (a key and a value)");
            int Num() => int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v is >= 0 and <= 255 ? v : throw new FormatException($"line {n}: '{p[1]}' is not a byte");
            switch (p[0])
            {
                case "version": s.Version = Num(); break;
                case "floor": s.Floor = Num(); break;
                case "bonus": s.Bonus = Num(); break;
                case "start": s.Start = p[1] is "Start" or "Select" or "A" or "B" ? p[1] : throw new FormatException($"line {n}: start '{p[1]}' (Start, Select, A or B)"); break;
                default: throw new FormatException($"line {n}: unknown key '{p[0]}'");
            }
        }
        return s;
    }

    // RAM
    private const byte AppState = 0x11, Lives = 0x12, Score = 0x13, Pad = 0x14, Frames = 0x15, NmiFlag = 0x16, NmiCount = 0x17, VersionAt = 0x18;

    /// <summary>The ROM (NROM-128) and its Mesen / NESFab .mlb.</summary>
    public static (byte[] rom, string mlb) PlantedGame(Settings s)
    {
        byte mask = s.Start switch { "Start" => 0x10, "Select" => 0x20, "A" => 0x80, _ => 0x40 };
        var a = new Asm(0xC000);
        a.L("reset").B(0x78, 0xD8, 0xA2, 0xFF, 0x9A)                  // SEI CLD LDX #$FF TXS
         .L("vbl1").B(0x2C, 0x02, 0x20, 0x10).R("vbl1")               // BIT $2002 BPL vbl1
         .B(0xA9, 0x00, 0xAA)                                         // LDA #0 TAX
         .L("clear").B(0x9D, 0x00, 0x00, 0x9D, 0x00, 0x01, 0x9D, 0x00, 0x02, 0x9D, 0x00, 0x03)
         .B(0x9D, 0x00, 0x04, 0x9D, 0x00, 0x05, 0x9D, 0x00, 0x06, 0x9D, 0x00, 0x07)   // STA $0000-$0700,X
         .B(0xE8, 0xD0).R("clear")                                    // INX BNE clear
         .L("vbl2").B(0x2C, 0x02, 0x20, 0x10).R("vbl2")
         .B(0xA9, (byte)s.Version, 0x85, VersionAt)                   // LDA #version STA version
         .B(0xA9, 0x80, 0x8D, 0x00, 0x20)                             // LDA #$80 STA $2000 (NMI on)
         .L("main").B(0xA5, NmiFlag, 0xF0).R("main")                  // LDA nmi_flag BEQ main
         .B(0xA9, 0x00, 0x85, NmiFlag)                                // LDA #0 STA nmi_flag
         .B(0x20).A("read_pad")                                       // JSR read_pad
         .B(0xA5, AppState, 0xD0).R("game")                           // LDA app_state BNE game
         .L("title").B(0xA5, Pad, 0x29, mask, 0xF0).R("main")         // LDA pad AND #mask BEQ main
         .B(0xA9, 0x01, 0x85, AppState, 0xA9, 0x03, 0x85, Lives)      // app_state = 1, lives = 3
         .B(0x4C).A("main")
         .L("game").B(0x20).A("update")                               // JSR update
         .B(0x4C).A("main")
         .L("update").B(0xE6, Frames)                                 // INC frames
         .B(0xA5, Frames, 0x29, 0x3F, 0xD0).R("scoring")              // every 64 frames
         .L("lose_life").B(0xA5, Lives, 0xC9, (byte)s.Floor, 0xF0).R("scoring")   // LDA lives CMP #floor BEQ scoring
         .B(0xC6, Lives)                                              // DEC lives
         .L("scoring").B(0xA5, Pad, 0x29, 0x80, 0xF0).R("done")       // A held?
         .B(0xA5, Score, 0x18, 0x69, (byte)s.Bonus, 0x85, Score)      // LDA score CLC ADC #bonus STA score
         .L("done").B(0x60)
         .L("read_pad").B(0xA9, 0x01, 0x8D, 0x16, 0x40, 0xA9, 0x00, 0x8D, 0x16, 0x40, 0xA2, 0x08)
         .L("pad_bit").B(0xAD, 0x16, 0x40, 0x4A, 0x26, Pad, 0xCA, 0xD0).R("pad_bit")
         .B(0x60)
         .L("nmi").B(0x48, 0xA9, 0x01, 0x85, NmiFlag, 0xE6, NmiCount, 0x68, 0x40)
         .L("irq").B(0x40);
        var code = a.Build();
        var prg = new byte[0x4000];
        Array.Fill(prg, (byte)0xEA);
        code.CopyTo(prg, 0);
        void Vec(int at, string l) { prg[at] = (byte)a[l]; prg[at + 1] = (byte)(a[l] >> 8); }
        Vec(0x3FFA, "nmi"); Vec(0x3FFC, "reset"); Vec(0x3FFE, "irq");
        var rom = new byte[16 + 0x4000 + 0x2000];
        rom[0] = (byte)'N'; rom[1] = (byte)'E'; rom[2] = (byte)'S'; rom[3] = 0x1A; rom[4] = 1; rom[5] = 1;
        prg.CopyTo(rom, 16);
        var mlb = new StringBuilder();
        foreach (var l in new[] { "reset", "main", "title", "game", "update", "lose_life", "scoring", "done", "read_pad", "nmi" })
            mlb.Append($"NesPrgRom:{a[l] - 0xC000:X6}:{l}:\n");
        foreach (var (addr, name) in new[] { (AppState, "app_state"), (Lives, "lives"), (Score, "score"), (Pad, "pad"), (Frames, "frames"), (NmiFlag, "nmi_flag"), (NmiCount, "nmi_count"), (VersionAt, "version") })
            mlb.Append($"R:{addr:X4}:{name}:\n");
        return (rom, mlb.ToString());
    }

    /// <summary><c>--movie build-planted --settings FILE --out FILE.nes</c>: the planted game's build (its .mlb next to it). Exit 1 on a bad settings file.</summary>
    public static int Build(string settingsPath, string outPath)
    {
        Settings s;
        try { s = ParseSettings(File.ReadAllText(settingsPath)); }
        catch (FormatException e) { Console.Error.WriteLine($"{Path.GetFileName(settingsPath)}: {e.Message}"); return 1; }
        var (rom, mlb) = PlantedGame(s);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllBytes(outPath, rom);
        File.WriteAllText(Path.ChangeExtension(outPath, ".mlb"), mlb);
        Console.WriteLine($"built {outPath}: version {s.Version}, start {s.Start}, floor {s.Floor}, bonus {s.Bonus}");
        return 0;
    }
}
