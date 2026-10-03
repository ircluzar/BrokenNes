using System;
using System.IO;
using System.Text;

namespace NesEmulator.Sega;

/// <summary>
/// A Genesis / Mega Drive cartridge: the ROM (an interleaved .smd is normalised first), the header the console and the tools read, the battery SRAM the header describes and the
/// mapper registers at $A130xx: the SRAM enable at $A130F1 and the SSF2-style bank registers at $A130F3-$A130FF for ROMs above 4 MB.
/// </summary>
/// <remarks>
/// Header layout from the Sega Genesis technical overview and Plutiedev's "ROM header": "SEGA" at $100, titles at $120 and $150, serial at $180, checksum at $18E, ROM and RAM
/// ranges at $1A0-$1AF, the SRAM header at $1B0 ("RA", a type byte, start and end), the region letters at $1F0. The checksum is never verified by the hardware. The SRAM type byte:
/// bit 7 set (RAM), bit 6 backup, bits 4 and 3 (11: odd addresses only, 10: even only, 00: both bytes); $F820 is the usual odd-byte value.
/// </remarks>
public sealed class MdCartridge
{
    /// <summary>The ROM as the console sees it, padded with $FF to a power of two (so addresses mirror) up to 4 MB.</summary>
    public readonly byte[] Rom;
    /// <summary>The ROM's real length in bytes.</summary>
    public readonly int RomLength;
    /// <summary>The battery SRAM image: one byte per byte the cartridge actually holds (odd addresses only: the bytes at $200001, $200003, ...; both: every byte).</summary>
    public readonly byte[] Sram;

    public readonly string DomesticTitle, OverseasTitle, Serial, Regions, Device, Copyright;
    public readonly ushort HeaderChecksum, ComputedChecksum;
    public readonly uint RomStart, RomEnd, RamStart, RamEnd;
    public readonly bool HasSramHeader;
    /// <summary>First and last byte address of the SRAM window, and which bytes of it exist.</summary>
    public readonly uint SramStart, SramEnd;
    public readonly bool SramOdd, SramEven;

    /// <summary>The cartridge has battery-backed memory the session should save.</summary>
    public bool HasBattery => Sram.Length > 0;

    /// <summary>$A130F1 bit 0: the SRAM is mapped over the ROM at its addresses. Set at power-on for a ROM of 2 MB or less that has SRAM (the SRAM then owns the window); a bigger ROM needs the register.</summary>
    public bool SramMapped { get; private set; }
    /// <summary>$A130F1 bit 1: SRAM writes are ignored.</summary>
    public bool SramWriteProtected { get; private set; }

    private readonly int romMask;
    private readonly bool bankMapper;
    private readonly int[] banks = { 0, 1, 2, 3, 4, 5, 6, 7 };

    /// <summary>A short description for status lines.</summary>
    public string Mapper => bankMapper ? "SSF2 banked (ROM above 4 MB)" : HasBattery ? $"plain ROM + {Sram.Length} byte SRAM ({(SramOdd && SramEven ? "both bytes" : SramOdd ? "odd bytes" : "even bytes")})" : "plain ROM";

    public MdCartridge(byte[] file)
    {
        byte[] rom = SegaRomFormat.NormalizeGenesis(file);
        if (rom.Length < 0x200) throw new InvalidDataException("Not a Genesis ROM: shorter than the header.");
        RomLength = rom.Length;
        int size = 0x20000; while (size < rom.Length && size < 0x400000) size <<= 1;
        if (rom.Length > 0x400000) size = rom.Length;
        Rom = new byte[size];
        Array.Copy(rom, Rom, rom.Length);
        for (int i = rom.Length; i < size; i++) Rom[i] = 0xFF;
        romMask = size - 1;
        bankMapper = rom.Length > 0x400000;

        string Ascii(int at, int len)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < len && at + i < rom.Length; i++) { byte c = rom[at + i]; sb.Append(c >= 0x20 && c < 0x7F ? (char)c : ' '); }
            return sb.ToString().Trim();
        }
        uint U32(int at) => (uint)(rom[at] << 24 | rom[at + 1] << 16 | rom[at + 2] << 8 | rom[at + 3]);

        Device = Ascii(0x100, 16);
        Copyright = Ascii(0x110, 16);
        DomesticTitle = Ascii(0x120, 48);
        OverseasTitle = Ascii(0x150, 48);
        Serial = Ascii(0x180, 14);
        HeaderChecksum = (ushort)(rom[0x18E] << 8 | rom[0x18F]);
        RomStart = U32(0x1A0); RomEnd = U32(0x1A4); RamStart = U32(0x1A8); RamEnd = U32(0x1AC);
        Regions = Ascii(0x1F0, 3);

        ushort sum = 0;
        for (int i = 0x200; i + 1 < rom.Length; i += 2) sum += (ushort)(rom[i] << 8 | rom[i + 1]);
        ComputedChecksum = sum;

        // SRAM header at $1B0: "RA", the type byte, $20, then start and end addresses
        if (rom[0x1B0] == (byte)'R' && rom[0x1B1] == (byte)'A')
        {
            byte type = rom[0x1B2];
            uint start = U32(0x1B4), end = U32(0x1B8);
            if (end >= start && end - start < 0x100000)
            {
                HasSramHeader = true;
                SramStart = start; SramEnd = end;
                int mode = type >> 3 & 3;
                SramOdd = mode == 3 || mode == 0; SramEven = mode == 2 || mode == 0;
                int bytes = (int)(end - start + 1);
                int held = SramOdd && SramEven ? bytes : (bytes + 1) / 2;
                Sram = new byte[held];
                SramMapped = rom.Length <= 0x200000;
            }
            else Sram = Array.Empty<byte>();
        }
        else Sram = Array.Empty<byte>();
        if (!HasSramHeader) Sram = Array.Empty<byte>();
    }

    /// <summary>Region the header lists, as the version register's overseas flag wants it: true when it names a market outside Japan (U, E, or a mask bit for them).</summary>
    public bool Overseas
    {
        get
        {
            foreach (char c in Regions)
            {
                if (c is 'U' or 'E') return true;
                if (Uri.IsHexDigit(c)) { int mask = Convert.ToInt32(c.ToString(), 16); if ((mask & 0xC) != 0) return true; }
            }
            return false;
        }
    }

    /// <summary>The bytes of the SRAM window the 68000 reads at <paramref name="address"/>, or -1 when SRAM does not answer there (ROM does).</summary>
    private int SramIndex(uint address)
    {
        if (!SramMapped || !HasSramHeader || address < (SramStart & ~1u) || address > (SramEnd | 1u)) return -1;
        uint offset = address - SramStart;
        if (SramOdd && SramEven) return (int)offset;
        // one byte per word: the odd or even byte of each
        bool odd = (address & 1) != 0;
        if (SramOdd && odd) return (int)(((address | 1) - (SramStart | 1)) >> 1);
        if (SramEven && !odd) return (int)((address - (SramStart & ~1u)) >> 1);
        return -2;     // the other byte of a half-populated window: open bus
    }

    private int RomAddress(uint address)
    {
        if (bankMapper)
        {
            int page = (int)(address >> 19) & 7;
            return banks[page] << 19 | (int)(address & 0x7FFFF);
        }
        return (int)address & romMask;
    }

    /// <summary>A byte of cartridge space ($000000-$3FFFFF).</summary>
    public byte ReadByte(uint address)
    {
        int s = SramIndex(address);
        if (s >= 0) return Sram[s];
        if (s == -2) return 0xFF;
        int a = RomAddress(address);
        return a < Rom.Length ? Rom[a] : (byte)0xFF;
    }

    /// <summary>A word of cartridge space (even address).</summary>
    public ushort ReadWord(uint address)
    {
        if (HasSramHeader && SramMapped && address >= (SramStart & ~1u) && address <= (SramEnd | 1u)) return (ushort)(ReadByte(address) << 8 | ReadByte(address | 1));
        int a = RomAddress(address);
        if (a + 1 < Rom.Length) return (ushort)(Rom[a] << 8 | Rom[a + 1]);
        return 0xFFFF;
    }

    /// <summary>A byte written to cartridge space: only the SRAM window takes writes (and only when mapped and not protected).</summary>
    public void WriteByte(uint address, byte value)
    {
        if (SramWriteProtected) return;
        int s = SramIndex(address);
        if (s >= 0) Sram[s] = value;
    }

    /// <summary>A write to the mapper registers ($A130F1 and the SSF2 bank registers $A130F3-$A130FF; <paramref name="address"/> is the 68000 address).</summary>
    public void WriteMapper(uint address, byte value)
    {
        uint reg = address & 0xFF;
        if (reg == 0xF1)
        {
            SramMapped = (value & 1) != 0 && HasSramHeader;
            SramWriteProtected = (value & 2) != 0;
        }
        else if (bankMapper && reg >= 0xF3 && reg <= 0xFF && (reg & 1) != 0)
        {
            int page = (int)((reg - 0xF1) >> 1);   // $F3 -> 1 ... $FF -> 7
            banks[page] = value & 0x3F;
        }
    }

    public void SaveState(BinaryWriter w) { w.Write(Sram.Length); w.Write(Sram); w.Write(SramMapped); w.Write(SramWriteProtected); foreach (int b in banks) w.Write(b); }

    public void LoadState(BinaryReader r)
    {
        int n = r.ReadInt32(); var data = r.ReadBytes(n);
        Array.Copy(data, Sram, Math.Min(data.Length, Sram.Length));
        SramMapped = r.ReadBoolean(); SramWriteProtected = r.ReadBoolean();
        for (int i = 0; i < banks.Length; i++) banks[i] = r.ReadInt32();
    }
}
