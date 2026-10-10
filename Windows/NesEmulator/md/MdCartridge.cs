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

    /// <summary>The image is a Sonic &amp; Knuckles cartridge with a game locked on (the SRAM comes from the locked-on game's header).</summary>
    public bool LockedOn { get; private set; }

    /// <summary>$A130F1 bit 0: the SRAM is mapped over the ROM at its addresses. Set at power-on for a ROM of 2 MB or less that has SRAM (the SRAM then owns the window); a bigger ROM needs the register.</summary>
    public bool SramMapped { get; private set; }
    /// <summary>$A130F1 bit 1: SRAM writes are ignored.</summary>
    public bool SramWriteProtected { get; private set; }

    private readonly int romMask;
    private readonly bool bankMapper;
    private readonly int[] banks = { 0, 1, 2, 3, 4, 5, 6, 7 };

    /// <summary>A short description for status lines.</summary>
    public string Mapper => Eeprom != null ? $"plain ROM + {Eeprom.Data.Length} byte EEPROM ({Eeprom.Chip}, {EepromWiring} wiring)" : EepromUnsupported != null ? "plain ROM (" + EepromUnsupported + ")" : bankMapper ? "SSF2 banked (ROM above 4 MB)" : LockedOn ? $"Sonic & Knuckles lock-on image, locked-on game's {Sram.Length} byte SRAM ({(SramOdd && SramEven ? "both bytes" : SramOdd ? "odd bytes" : "even bytes")})" : HasBattery ? $"plain ROM + {Sram.Length} byte SRAM ({(SramOdd && SramEven ? "both bytes" : SramOdd ? "odd bytes" : "even bytes")})" : "plain ROM";

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

        // SRAM header at $1B0: "RA", the type byte, $20, then start and end addresses. A Sonic & Knuckles image with a game locked on (the 4 MB dumps: the 2 MB Sonic & Knuckles cartridge, then the locked-on
        // cartridge's 2 MB at $200000) has no header of its own: the locked-on game's, at $2001B0, describes the battery RAM that cartridge brings, which the pair's code switches on through $A130F1.
        Sram = Array.Empty<byte>();
        int hdr = 0;
        if (!(rom[0x1B0] == (byte)'R' && rom[0x1B1] == (byte)'A') && rom.Length >= 0x400000 && rom.Length <= 0x400000
            && rom[0x200100] == (byte)'S' && rom[0x200101] == (byte)'E' && rom[0x200102] == (byte)'G' && rom[0x200103] == (byte)'A'
            && rom[0x2001B0] == (byte)'R' && rom[0x2001B1] == (byte)'A')
        {
            hdr = 0x200000; LockedOn = true;
        }
        if (rom[hdr + 0x1B0] == (byte)'R' && rom[hdr + 0x1B1] == (byte)'A')
        {
            byte type = rom[hdr + 0x1B2];
            uint start = U32(hdr + 0x1B4), end = U32(hdr + 0x1B8);
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
        }
        if (!HasSramHeader) Sram = Array.Empty<byte>();

        // Serial EEPROM: the header can say a cartridge has one (the SRAM header's type word $E840) but not which chip or how its two lines are wired, and several carts that have one say nothing at all;
        // MdEepromTable (by serial, header checksum and size) knows the ones this emulator has been checked against. A header that declares an EEPROM nobody can place is reported, not mapped as SRAM.
        EepromDeclared = rom[0x1B0] == (byte)'R' && rom[0x1B1] == (byte)'A' && rom[0x1B2] == 0xE8 && rom[0x1B3] == 0x40;
        var found = MdEepromTable.Lookup(Serial, HeaderChecksum, rom.Length);
        if (found != null)
        {
            Eeprom = new MdEeprom(found.Value.chip);
            EepromWiring = found.Value.wiring;
            Sram = Eeprom.Data;
            HasSramHeader = false; SramMapped = false;
        }
        else if (EepromDeclared)
        {
            EepromUnsupported = $"header declares an EEPROM (serial {Serial}, checksum {HeaderChecksum:X4}) that this emulator has no wiring for";
            Sram = Array.Empty<byte>();
            HasSramHeader = false; SramMapped = false;
        }
    }

    /// <summary>The cartridge's serial EEPROM, or null.</summary>
    public MdEeprom? Eeprom { get; }
    public MdEepromWiring EepromWiring { get; }
    /// <summary>The header declares an EEPROM (type word $E840).</summary>
    public bool EepromDeclared { get; }
    /// <summary>Set when the header declares an EEPROM but no wiring is known: the game runs without one (and says why here).</summary>
    public string? EepromUnsupported { get; }

    // the EEPROM's lines. Sega: SDA bit 0 and SCL bit 1 of a write to the odd byte at $200001, SDA read back in bit 0 of the same byte. Acclaim: SCL is bit 0 of the byte at $200000, SDA bit 0 of the byte at $200001
    // (read back there). Codemasters: SDA bit 0 and SCL bit 1 of the byte at $300000, SDA read back in bit 7 of the byte at $380001. Only those bytes: the rest of the space is ROM (or nothing).
    private bool EepromRead(uint address, out byte value, out byte mask)
    {
        value = 0; mask = 0;
        switch (EepromWiring)
        {
            case MdEepromWiring.Sega when address == 0x200001: value = (byte)(Eeprom!.SdaLine ? 1 : 0); mask = 1; return true;
            case MdEepromWiring.Acclaim when address == 0x200001: value = (byte)(Eeprom!.SdaLine ? 1 : 0); mask = 1; return true;
            case MdEepromWiring.Codemasters when address == 0x380001: value = (byte)(Eeprom!.SdaLine ? 0x80 : 0); mask = 0x80; return true;
        }
        return false;
    }

    private bool EepromWrite(uint address, byte value)
    {
        var e = Eeprom!;
        switch (EepromWiring)
        {
            case MdEepromWiring.Sega when address == 0x200001: e.Drive((value & 1) != 0, (value & 2) != 0); return true;
            case MdEepromWiring.Acclaim when address == 0x200000: e.Drive(e.MasterSda, (value & 1) != 0); return true;
            case MdEepromWiring.Acclaim when address == 0x200001: e.Drive((value & 1) != 0, e.MasterScl); return true;
            case MdEepromWiring.Codemasters when address == 0x300000: e.Drive((value & 1) != 0, (value & 2) != 0); return true;
        }
        return false;
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
        byte b = a < Rom.Length ? Rom[a] : (byte)0xFF;
        if (Eeprom != null && address >= 0x200000 && EepromRead(address, out byte ev, out byte mask)) b = (byte)(b & ~mask | ev);
        return b;
    }

    /// <summary>A word of cartridge space (even address).</summary>
    public ushort ReadWord(uint address)
    {
        if (HasSramHeader && SramMapped && address >= (SramStart & ~1u) && address <= (SramEnd | 1u)) return (ushort)(ReadByte(address) << 8 | ReadByte(address | 1));
        if (Eeprom != null && (address == 0x200000 || address == 0x380000)) return (ushort)(ReadByte(address) << 8 | ReadByte(address | 1));
        int a = RomAddress(address);
        if (a + 1 < Rom.Length) return (ushort)(Rom[a] << 8 | Rom[a + 1]);
        return 0xFFFF;
    }

    /// <summary>A byte written to cartridge space: only the SRAM window takes writes (and only when mapped and not protected), and the EEPROM's lines.</summary>
    public void WriteByte(uint address, byte value)
    {
        if (Eeprom != null && address >= 0x200000 && EepromWrite(address, value)) return;
        int s = SramIndex(address);
        if (s < 0) { StrayWrites++; LastStrayWrite = address; return; }
        if (SramWriteProtected) return;
        Sram[s] = value;
    }

    /// <summary>Diagnostics for the whole-library sweep: writes into cartridge space that nothing took (a mapper or a chip the cartridge has and we do not), and the last such address.</summary>
    public long StrayWrites, OtherMapperWrites;
    public uint LastStrayWrite, LastOtherMapperWrite;

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
        else { OtherMapperWrites++; LastOtherMapperWrite = address; }
    }

    public void SaveState(BinaryWriter w) { w.Write(Sram.Length); w.Write(Sram); w.Write(SramMapped); w.Write(SramWriteProtected); foreach (int b in banks) w.Write(b); w.Write(Eeprom != null); Eeprom?.SaveState(w); }

    public void LoadState(BinaryReader r)
    {
        int n = r.ReadInt32(); var data = r.ReadBytes(n);
        Array.Copy(data, Sram, Math.Min(data.Length, Sram.Length));
        SramMapped = r.ReadBoolean(); SramWriteProtected = r.ReadBoolean();
        for (int i = 0; i < banks.Length; i++) banks[i] = r.ReadInt32();
        if (r.ReadBoolean()) Eeprom?.LoadState(r);
    }
}
