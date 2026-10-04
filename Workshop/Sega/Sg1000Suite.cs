using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// The SG-1000 (TMS9918A) with no game in the libraries: the eight programs of <see cref="Sg1000Progs"/> (each of the four modes with 8x8 sprites and with 16x16 magnified sprites, pseudo-random data, the display on) run as .sg
/// cartridges. Checked: the console is the SG-1000 (the TMS part of the board, the Texas Instruments PSG), the picture is not blank, the sprite flags the program saw (RAM $C001) and the SHA-1 of the picture at frame 12 are equal to what the
/// oracle (Mesen 2.1.1) reported for the same bytes (the constants below; <c>--smstest --sg-hash dir</c> prints this emulator's, <c>--png-hash file</c> the oracle's picture's).
/// </summary>
internal static class Sg1000Suite
{
    /// <summary>The frame the picture and the flags are taken at: the programs copy 15 KB to VRAM at about 50 cycles a byte before they turn the display on (about 13 frames).</summary>
    private const int Frames = 30;

    // name -> (oracle's picture hash at frame 30, the flags its run of the program accumulated at $C001); filled from the oracle's runs
    internal static readonly Dictionary<string, (string Hash, int Flags)> Oracle = new()
    {
        ["sg-graphics1-big.sg"] = ("2E61338C0107C4DA1AFDFED50701A5172AA2F286", 0xFF),
        ["sg-graphics1-small.sg"] = ("D6BC2E825465FF1BCA9C766EEB35EBFA5C9FB9FB", 0xF3),
        ["sg-graphics2-big.sg"] = ("ED6B5C24DC8BBFF141CE8E99F9749044EC91CE55", 0xFF),
        ["sg-graphics2-small.sg"] = ("5A5C6ADB112FD39E48AE4F0663B2017EF859BA13", 0xF3),
        ["sg-multicolor-big.sg"] = ("032EC6E867C01191C8534FD4AE09AA9756FE2004", 0xFF),
        ["sg-multicolor-small.sg"] = ("2FE3A66049BD583137598E6E87AC9CB9F4E6C59C", 0xF3),
        ["sg-text-big.sg"] = ("62B607C8BC17733BDB106A7A0AAF7C5D7C856B48", 0x80),
        ["sg-text-small.sg"] = ("0424774B8876FE7D3AA0B5AFAE431C3D99C0C94F", 0x80),
    };

    public static string PngHash(string path)
    {
        var px = SmsPng.Load(path, out int w, out int h, 192);   // the oracle's whole video frame cut to the 192 lines of the picture
        return Convert.ToHexString(SHA1.HashData(MemoryMarshal.AsBytes(px.AsSpan(0, w * h))));
    }

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        foreach (Sg1000Progs.Mode m in Enum.GetValues<Sg1000Progs.Mode>())
            foreach (bool big in new[] { false, true })
            {
                string file = Sg1000Progs.FileName(m, big);
                var s = ctx.Session(Sg1000Progs.Build(m, big), file);
                for (int f = 0; f < Frames; f++) s.RunFrame();
                string hash = GoldenSuite.Hash(s);
                int colours = s.Frame.Take(s.FrameWidth * s.FrameHeight).Distinct().Count();
                int flags = s.Board.Ram[1];
                bool sg = s.Model == MasterSystemModel.Sg1000 && s.Board.IsSg;
                yield return new SmsCheck($"{file}: an SG-1000 cartridge shows a picture ({colours} colours)", sg && colours >= 2, $"model {s.Model}");
                if (Oracle.TryGetValue(file, out var o))
                {
                    yield return new SmsCheck($"{file}: the picture equals the oracle's (frame {Frames}, SHA-1)", hash == (ctx.Poison == "sg1000" ? "0" : o.Hash), $"{hash[..12]} vs {o.Hash[..12]}");
                    yield return new SmsCheck($"{file}: the status flags the program saw equal the oracle's", flags == o.Flags, $"${flags:X2} vs ${o.Flags:X2}");
                }
                else yield return SmsCheck.Skipped($"{file}: the picture equals the oracle's", "no oracle value recorded for this program");
            }
    }

    /// <summary>--sg-hash dir: this emulator's picture hash and flags per program, after 12 frames.</summary>
    public static int Print(SmsSuiteContext ctx, string dir)
    {
        foreach (Sg1000Progs.Mode m in Enum.GetValues<Sg1000Progs.Mode>())
            foreach (bool big in new[] { false, true })
            {
                string file = Sg1000Progs.FileName(m, big);
                var s = ctx.Session(Sg1000Progs.Build(m, big), file);
                for (int f = 0; f < Frames; f++) s.RunFrame();
                Console.WriteLine($"{file}\t{GoldenSuite.Hash(s)}\t{s.Board.Ram[1]}");
                SmsPng.Save(s.Frame, s.FrameWidth, s.FrameHeight, Path.Combine(dir, file.Replace(".sg", "_mine.png")));
            }
        return 0;
    }
}
