using System;
using System.Text;

namespace NesEmulator.Snes;

/// <summary>
/// SNES cartridge: LoROM or HiROM, optional battery SRAM. Plays the role the NES IMapper plays: it
/// owns the ROM/SRAM address decoding for every region the board does not claim for itself.
/// Coprocessors are separate <see cref="ISnesCoprocessor"/> objects the board consults first;
/// <see cref="Chip"/> only reports what the header asks for.
/// </summary>
public sealed class SnesCartridge
{
    public byte[] Rom { get; }
    public byte[] Sram { get; }
    public bool HiRom { get; }
    public string Title { get; }
    public byte MapMode { get; }
    public bool HasBattery { get; }

    private SnesCartridge(byte[] rom, bool hiRom)
    {
        Rom = rom;
        HiRom = hiRom;
        int h = hiRom ? 0xFFC0 : 0x7FC0;
        Title = Encoding.ASCII.GetString(rom, h, 21).TrimEnd(' ', '\0');
        MapMode = rom[h + 0x15];
        byte chipset = rom[h + 0x16];
        int sramShift = rom[h + 0x18];
        int sramSize = sramShift is > 0 and <= 8 ? 1024 << sramShift : 0;
        Sram = new byte[sramSize];
        HasBattery = sramSize > 0 && (chipset & 0x0F) is 0x02 or 0x05 or 0x06 or 0x0A;
        ChipsetByte = chipset;
        // Low nibble 3-6 means "ROM + coprocessor (+RAM, +battery)"; the high nibble names the chip.
        Chip = (chipset & 0x0F) < 3 ? SnesChip.None : (chipset >> 4) switch
        {
            0 => SnesChip.Dsp,
            1 => SnesChip.SuperFx,
            3 => SnesChip.Sa1,
            _ => SnesChip.Other,
        };
    }

    public byte ChipsetByte { get; }
    public SnesChip Chip { get; }

    public static SnesCartridge Load(byte[] file)
    {
        byte[] rom = file;
        if (file.Length % 1024 == 512) rom = file.AsSpan(512).ToArray();   // strip copier header
        if (rom.Length < 0x8000) throw new InvalidOperationException($"SNES ROM too small ({rom.Length} bytes)");
        bool hi = rom.Length >= 0x10000 && Score(rom, 0xFFC0, hiRom: true) > Score(rom, 0x7FC0, hiRom: false);
        return new SnesCartridge(rom, hi);
    }

    /// <summary>How plausible a header is at this offset. Ties go to LoROM.</summary>
    private static int Score(byte[] rom, int h, bool hiRom)
    {
        int score = 0;
        int complement = rom[h + 0x1C] | rom[h + 0x1D] << 8;
        int checksum = rom[h + 0x1E] | rom[h + 0x1F] << 8;
        if ((checksum ^ complement) == 0xFFFF) score += 4;
        int mode = rom[h + 0x15] & 0x0F;
        if (hiRom ? mode is 1 or 5 : mode is 0 or 2) score += 2;
        int reset = rom[h + 0x3C] | rom[h + 0x3D] << 8;
        if (reset >= 0x8000) score += 1;
        bool printable = true;
        for (int i = 0; i < 21; i++) { byte c = rom[h + i]; if (c < 0x20 || c > 0x7E) { printable = false; break; } }
        if (printable) score += 1;
        return score;
    }

    /// <summary>Returns false when nothing on the cartridge answers (the board supplies open bus).</summary>
    public bool TryRead(uint bank, uint offset, out byte value)
    {
        int i = Decode(bank, offset, out bool sram);
        if (i < 0) { value = 0; return false; }
        value = sram ? Sram[i] : Rom[i];
        return true;
    }

    /// <summary>
    /// True when [offset, offset+length) in this bank maps to ROM (not SRAM, not unmapped) as one
    /// contiguous run, e.g. so the board can serve a whole 4KB page straight from the ROM array.
    /// </summary>
    public bool TryMapRomLinear(uint bank, uint offset, int length, out int romIndex)
    {
        romIndex = Decode(bank, offset, out bool sram);
        if (romIndex < 0 || sram) return false;
        int last = Decode(bank, offset + (uint)length - 1, out bool sramLast);
        return !sramLast && last == romIndex + length - 1;
    }

    public void Write(uint bank, uint offset, byte value)
    {
        int i = Decode(bank, offset, out bool sram);
        if (i >= 0 && sram) Sram[i] = value;
    }

    private int Decode(uint bank, uint offset, out bool sram)
    {
        sram = false;
        if (!HiRom)
        {
            uint b = bank & 0x7F;
            if (b >= 0x70 && b <= 0x7D && offset < 0x8000)
            {
                if (Sram.Length == 0) return -1;
                sram = true;
                return (int)(((b - 0x70) * 0x8000 + offset) % (uint)Sram.Length);
            }
            if (offset < 0x8000 && b < 0x40) return -1;  // system area, claimed by the board
            return (int)((b * 0x8000 + (offset & 0x7FFF)) % (uint)Rom.Length);
        }
        else
        {
            uint b = bank & 0x7F;
            if (b < 0x40)
            {
                if (offset >= 0x6000 && offset < 0x8000 && b >= 0x20)
                {
                    if (Sram.Length == 0) return -1;
                    sram = true;
                    return (int)(((b - 0x20) * 0x2000 + (offset - 0x6000)) % (uint)Sram.Length);
                }
                if (offset < 0x8000) return -1;
            }
            return (int)(((b & 0x3F) * 0x10000 + offset) % (uint)Rom.Length);
        }
    }
}
