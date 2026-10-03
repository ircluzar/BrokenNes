using System;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>
/// What a Sega ROM file is, from its own bytes first and its extension second: Master System / Mark III / SG-1000 / SC-3000, Game Gear, or Genesis /
/// Mega Drive, and the format quirks that stand between a file and the bytes the machine sees (the 512-byte copier header some Master System dumps carry,
/// the interleaved .smd Genesis format).
/// </summary>
/// <remarks>
/// Written from the public documentation (SMS Power "ROM header", Sega Genesis technical overview, Plutiedev "ROM header"); nothing is copied from an emulator.
/// Master System / Game Gear: the header "TMR SEGA" sits at $7FF0 (also seen at $3FF0 and $1FF0) and the high nibble of the byte at +$0F is the system
/// and region (3 SMS Japan, 4 SMS export, 5 GG Japan, 6 GG export, 7 GG international). Genesis: "SEGA" at $100 (or " SEGA" at $100), the only part of
/// the header the console itself checks. 32X and Pico carry "SEGA 32X" / "SEGA PICO" there and are not supported, so they are not detected.
/// </remarks>
public static class SegaRomFormat
{
    private static readonly byte[] TmrSega = "TMR SEGA"u8.ToArray();

    /// <summary>The Sega console this ROM is for, or null when it looks like none (or like a 32X / Pico cartridge).</summary>
    public static ConsoleKind? Detect(byte[] rom, string fileName)
    {
        string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();

        // Master System / Game Gear: the specific 8-byte string, tried first
        byte[] sms = StripCopierHeader(rom);
        foreach (int at in new[] { 0x7FF0, 0x3FF0, 0x1FF0 })
        {
            if (at + 0x10 > sms.Length || !sms.AsSpan(at, TmrSega.Length).SequenceEqual(TmrSega)) continue;
            int system = sms[at + 0x0F] >> 4;
            if (system is 3 or 4) return ConsoleKind.MasterSystem;
            if (system is 5 or 6 or 7) return ConsoleKind.GameGear;
            break;   // a header with another system code: decide by extension below
        }

        // Genesis / Mega Drive: "SEGA" at $100 (after de-interleaving an .smd)
        byte[] md = IsInterleavedSmd(rom) ? DeinterleaveSmd(rom) : rom;
        if (md.Length >= 0x110)
        {
            var head = md.AsSpan(0x100, 16);
            bool sega = head[..4].SequenceEqual("SEGA"u8) || head.Slice(1, 4).SequenceEqual("SEGA"u8);
            if (sega)
            {
                bool unsupported = head.IndexOf("32X"u8) >= 0 || head.IndexOf("PICO"u8) >= 0;
                return unsupported ? null : ConsoleKind.Genesis;
            }
        }

        return ext switch
        {
            ".sms" or ".sg" or ".sc" => ConsoleKind.MasterSystem,
            ".gg" => ConsoleKind.GameGear,
            ".md" or ".gen" or ".smd" => ConsoleKind.Genesis,   // .bin is ambiguous: only the header decides
            _ => null,
        };
    }

    /// <summary>The bytes a Master System / Game Gear / SG-1000 cartridge holds: some dumps carry a 512-byte copier header (length % 16 KB == 512).</summary>
    public static byte[] StripCopierHeader(byte[] rom) =>
        rom.Length > 0x200 && rom.Length % 0x4000 == 0x200 ? rom.AsSpan(0x200).ToArray() : rom;

    /// <summary>The Super Magic Drive format: a 512-byte header (byte 1 = 3, bytes 8-9 = $AA $BB), then 16 KB blocks with odd and even bytes split.</summary>
    public static bool IsInterleavedSmd(byte[] rom) =>
        rom.Length > 0x200 && (rom.Length - 0x200) % 0x4000 == 0 && rom[8] == 0xAA && rom[9] == 0xBB;

    /// <summary>A Genesis ROM as the console reads it. An .smd is de-interleaved (each 16 KB block: the first 8 KB are the odd bytes, the second 8 KB the even
    /// ones); any other file comes back as it is.</summary>
    public static byte[] NormalizeGenesis(byte[] rom) => IsInterleavedSmd(rom) ? DeinterleaveSmd(rom) : rom;

    private static byte[] DeinterleaveSmd(byte[] rom)
    {
        int blocks = (rom.Length - 0x200) / 0x4000;
        var output = new byte[blocks * 0x4000];
        for (int b = 0; b < blocks; b++)
        {
            int src = 0x200 + b * 0x4000, dst = b * 0x4000;
            for (int i = 0; i < 0x2000; i++)
            {
                output[dst + 2 * i + 1] = rom[src + i];
                output[dst + 2 * i] = rom[src + 0x2000 + i];
            }
        }
        return output;
    }
}
