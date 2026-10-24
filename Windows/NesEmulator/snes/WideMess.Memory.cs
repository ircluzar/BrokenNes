using System;
using System.IO;

namespace NesEmulator.Snes;

// WideMess milestone 4, the memory side of a .wms game (docs/projects/widemess-spec.md sections 2 and 5): the WideMess map's limits, and the cartridge a .wms file
// becomes. The board's side (fast mode, the 1 MB work RAM, the 20-bit WRAM port, fast DMA) is BOARD_SFC.WideMess.cs.

public static partial class WmsFile
{
    /// <summary>The WideMess map's ROM limits: whole 64 KB banks, 64 KB (one bank: the vectors are at $00:FFE0-FFFF) to 4 MB ($C0-$FF).</summary>
    public const int MapRomBank = 0x10000, MapRomMax = 4 << 20;
    /// <summary>The WideMess map's battery RAM limit: 8 KB per bank at $20-$3F:6000-7FFF, 32 banks.</summary>
    public const int MapSramMax = 256 << 10;

    /// <summary>Refuses a WideMess-map header the map cannot hold (spec section 2): the ROM, the battery RAM and the battery flag. Each refusal says what is wrong.</summary>
    private static void CheckWideMessMap(int romSize, int sramSize, bool battery)
    {
        if (romSize < MapRomBank || romSize > MapRomMax || romSize % MapRomBank != 0)
            throw new InvalidDataException($"the WideMess map holds 64 KB to 4 MB of ROM in whole 64 KB banks; this game has {romSize:N0} bytes");
        if (sramSize < 0 || sramSize > MapSramMax || (sramSize != 0 && (sramSize < 2048 || (sramSize & (sramSize - 1)) != 0)))
            throw new InvalidDataException($"the WideMess map's battery RAM is 0, or a power of two from 2 KB to 256 KB; this game asks for {sramSize:N0} bytes");
        if (battery && sramSize == 0)
            throw new InvalidDataException("the WideMess header says the game has a battery but no battery RAM");
    }

    /// <summary>
    /// The cartridge of a checked .wms game: on the WideMess map, the map from the header (spec section 2: ROM at $C0-$FF and HiROM-style mirrors, battery RAM of the header's size at
    /// $20-$3F / $A0-$BF:6000-7FFF, no cartridge chip; the board puts its 1 MB of work RAM at $70-$7F); on the LoROM and HiROM maps, the SNES image as the SNES console loads it, which must
    /// be the map the header names (spec section 5: a file whose map disagrees with its image is refused).
    /// </summary>
    public static SnesCartridge Cartridge(byte[] image, Header header)
    {
        if (header.Map == 2) return SnesCartridge.LoadWideMessMap(image, header.SramSize, header.Battery);
        var cart = SnesCartridge.Load(image);
        if (cart.HiRom != (header.Map == 1))
            throw new InvalidDataException($"the WideMess header says map {header.Map} ({(header.Map == 1 ? "HiROM" : "LoROM")}) but the image is a {(cart.HiRom ? "HiROM" : "LoROM")} image");
        return cart;
    }
}

public sealed partial class SnesCartridge
{
    /// <summary>True for a WideMess game on the WideMess map (spec section 2): HiROM-style ROM decoding, the header's battery RAM, and banks $70-$7D are the board's work RAM, not the cartridge's.</summary>
    public bool WideMessMap { get; }

    /// <summary>A cartridge on the WideMess map: the ROM decodes as HiROM does (ROM at $C0-$FF, mirrors at $40-$6F and $00-$3F / $80-$BF:8000-FFFF; battery RAM at $20-$3F / $A0-$BF:6000-7FFF,
    /// 8 KB per bank); the battery RAM's size and the battery come from the .wms header, never from an internal SNES header; it has no cartridge chip.</summary>
    public static SnesCartridge LoadWideMessMap(byte[] image, int sramSize, bool battery) => new(image, sramSize, battery);

    private SnesCartridge(byte[] rom, int sramSize, bool battery)
    {
        Rom = rom;
        HiRom = true;
        WideMessMap = true;
        // an internal SNES header is optional on this map: its title is used when it has one (the .wms header's title wins in the session)
        var t = rom.AsSpan(0xFFC0, 21);
        bool printable = true;
        foreach (byte c in t) if (c < 0x20 || c > 0x7E) { printable = false; break; }
        Title = printable ? System.Text.Encoding.ASCII.GetString(t).TrimEnd(' ', '\0') : "";
        MapMode = rom[0xFFD5];
        ChipsetByte = 0;
        Chip = SnesChip.None;
        Sram = new byte[sramSize];
        HasBattery = battery && sramSize > 0;
    }
}
