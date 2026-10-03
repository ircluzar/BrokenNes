using System;
using System.IO;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>The cartridge wiring: which mapper chip (or none) sits between the Z80 and the ROM.</summary>
public enum SmsMapperKind
{
    /// <summary>The Sega mapper: registers at $FFFC-$FFFF, three 16 KB slots, the first 1 KB fixed, optional cartridge RAM in slot 2. What nearly every Master System and Game Gear game uses.</summary>
    Sega,
    /// <summary>Codemasters: a page register at the first byte of each 16 KB slot ($0000, $4000, $8000); no protected first 1 KB; some carts have 8 KB of RAM at $A000.</summary>
    Codemasters,
    /// <summary>Korean 16 KB mapper: slots 0 and 1 fixed, one register at $A000 selects the page of slot 2.</summary>
    KoreanA000,
    /// <summary>The MSX-style Korean mapper: four 8 KB slots selected by registers at $0000-$0003.</summary>
    MsxKorean,
    /// <summary>The Nemesis (Korean MSX port) variant: as <see cref="MsxKorean"/> with the last 8 KB of the ROM at $0000-$1FFF.</summary>
    Nemesis,
    /// <summary>No mapper: the ROM is wired straight into $0000-$BFFF (SG-1000, SC-3000, and any small cartridge).</summary>
    Plain,
}

/// <summary>
/// A Master System / Game Gear / SG-1000 cartridge: the ROM, the mapper and the cartridge RAM, behind a page table the board reads through. The board owns the
/// work RAM at $C000-$FFFF; the mapper chip lives in the cartridge, so its registers ($FFFC-$FFFF on the Sega mapper) are written through <see cref="Write"/> even though
/// the same writes also land in work RAM.
/// </summary>
/// <remarks>
/// Memory map from the SMS Power "Mappers" and "Memory Map" pages and the Sega mapper's documented register behaviour; the 8 KB slot variants and the Codemasters RAM follow the
/// behaviour BizHawk's MIT-licensed SMS memory map implements (ledger row in sega/THIRD_PARTY_NOTICES.md). Page numbers wrap modulo the ROM's page count, as address-line
/// mirroring does on a smaller ROM. No ROM or firmware is ever stored in the repository.
/// </remarks>
public sealed class SmsCartridge
{
    private const int Slot = 0x400;   // the page table is 1 KB granular (the Sega mapper keeps the first KB fixed)

    public SmsMapperKind Mapper { get; }
    public byte[] Rom { get; }
    public int RomPages16 { get; }

    /// <summary>The cartridge RAM (Sega mapper: two 16 KB banks that can replace slot 2; Codemasters: 8 KB), or null.</summary>
    public byte[]? Ram { get; private set; }
    /// <summary>The ROM header, when one was found ($7FF0 / $3FF0 / $1FF0).</summary>
    public SmsHeader? Header { get; }
    /// <summary>True when the game is believed to keep a save in the cartridge RAM: it maps RAM in, which the scan of the ROM sees, or the RAM was written while mapped.</summary>
    public bool HasBattery { get; private set; }
    /// <summary>The cartridge RAM was written since it was last exported or imported.</summary>
    public bool RamDirty { get; private set; }

    // page tables: each 1 KB of $0000-$BFFF points at a byte array and an offset into it
    private readonly byte[][] rd = new byte[48][];
    private readonly int[] rdBase = new int[48];
    private readonly byte[]?[] wr = new byte[48][];   // writable pages (cartridge RAM), else null
    private readonly int[] wrBase = new int[48];
    private static readonly byte[] OpenBus = MakeOpenBus();
    private static byte[] MakeOpenBus() { var b = new byte[Slot]; Array.Fill(b, (byte)0xFF); return b; }

    // mapper registers
    private byte ctl;                       // Sega $FFFC
    private readonly int[] pages = new int[4];   // slot pages (16 KB units; MSX variants use 8 KB units)
    private bool cmRamOn;                   // Codemasters RAM enabled by bit 7 of the $4000 register

    public SmsCartridge(byte[] rom, SmsMapperKind mapper, string? title = null)
    {
        // pad to whole 16 KB pages so a short last page reads as open bus rather than throwing
        int len = Math.Max(rom.Length, 0x4000);
        int padded = (len + 0x3FFF) & ~0x3FFF;
        if (rom.Length != padded) { Rom = new byte[padded]; Array.Fill(Rom, (byte)0xFF); Array.Copy(rom, Rom, rom.Length); }
        else Rom = rom;
        RomPages16 = Rom.Length / 0x4000;
        Mapper = mapper;
        Header = SmsHeader.Find(Rom);
        Title = title ?? "";
        if (mapper == SmsMapperKind.Sega)
        {
            Ram = new byte[0x8000];
            HasBattery = ScansForRamMapping(Rom);
        }
        else if (mapper == SmsMapperKind.Codemasters) Ram = null;   // allocated when the first bit-7 write arrives
        Reset();
    }

    public string Title { get; }

    /// <summary>The mapper a ROM image needs, from what the image itself says. The ROM's name is not consulted (a name is no wiring diagram); a game the heuristics cannot place gets the Sega
    /// mapper, which is what nearly every game uses.</summary>
    public static SmsMapperKind DetectMapper(byte[] rom, MasterSystemModel model)
    {
        if (model is MasterSystemModel.Sg1000 or MasterSystemModel.Sc3000) return rom.Length <= 0xC000 ? SmsMapperKind.Plain : SmsMapperKind.Sega;
        if (IsCodemasters(rom)) return SmsMapperKind.Codemasters;
        return SmsMapperKind.Sega;
    }

    /// <summary>Codemasters cartridges carry a checksum at $7FE6 and its two's complement at $7FE8 (the sum of the two words is $10000), beside a build date; no Sega header uses that pair.</summary>
    public static bool IsCodemasters(byte[] rom)
    {
        if (rom.Length < 0x8000) return false;
        int sum = rom[0x7FE6] | rom[0x7FE7] << 8;
        int inv = rom[0x7FE8] | rom[0x7FE9] << 8;
        if (sum == 0 && inv == 0) return false;
        if (((sum + inv) & 0xFFFF) != 0) return false;
        // the build date at $7FE0-$7FE4 is BCD (day, month, year, hour, minute); a genuine Codemasters header has sane values
        byte day = rom[0x7FE1], month = rom[0x7FE2];
        return IsBcd(day) && IsBcd(month) && Bcd(month) is >= 1 and <= 12 && Bcd(day) is >= 1 and <= 31;
    }

    private static bool IsBcd(byte b) => (b & 0x0F) <= 9 && (b >> 4) <= 9;
    private static int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);

    /// <summary>
    /// Looks for the instruction pair a game uses to switch the cartridge RAM into slot 2: <c>LD A,n</c> with bit 3 set, then <c>LD ($FFFC),A</c> (<c>3E nn 32 FC FF</c>), or
    /// <c>LD HL,$FFFC / LD (HL),n</c> (<c>21 FC FF 36 nn</c>). A heuristic: the header does not say whether a cartridge has a battery, so this decides whether to offer a save file.
    /// </summary>
    public static bool ScansForRamMapping(byte[] rom)
    {
        for (int i = 0; i + 5 <= rom.Length; i++)
        {
            if (rom[i] == 0x3E && (rom[i + 1] & 0x08) != 0 && rom[i + 2] == 0x32 && rom[i + 3] == 0xFC && rom[i + 4] == 0xFF) return true;
            if (rom[i] == 0x21 && rom[i + 1] == 0xFC && rom[i + 2] == 0xFF && rom[i + 3] == 0x36 && (rom[i + 4] & 0x08) != 0) return true;
        }
        return false;
    }

    // =================================================================================== reset and page tables

    public void Reset()
    {
        ctl = 0; cmRamOn = false;
        switch (Mapper)
        {
            case SmsMapperKind.Codemasters: pages[0] = 0; pages[1] = 1; pages[2] = 0; break;
            case SmsMapperKind.KoreanA000: pages[0] = 0; pages[1] = 1; pages[2] = 2; break;
            case SmsMapperKind.MsxKorean: case SmsMapperKind.Nemesis: pages[0] = 0; pages[1] = 0; pages[2] = 0; pages[3] = 0; break;
            default: pages[0] = 0; pages[1] = 1; pages[2] = 2; break;
        }
        Remap();
    }

    private byte[] RomPage(int page16, out int baseOffset)
    {
        baseOffset = (page16 % RomPages16) * 0x4000;
        return Rom;
    }

    private void MapRom(int firstSlot, int slots, int romOffset)
    {
        for (int i = 0; i < slots; i++)
        {
            rd[firstSlot + i] = Rom; rdBase[firstSlot + i] = romOffset + i * Slot - (firstSlot + i) * Slot;
            wr[firstSlot + i] = null;
        }
    }

    private void MapOpen(int firstSlot, int slots)
    {
        for (int i = 0; i < slots; i++) { rd[firstSlot + i] = OpenBus; rdBase[firstSlot + i] = -(firstSlot + i) * Slot; wr[firstSlot + i] = null; }
    }

    private void MapRam(int firstSlot, int slots, byte[] ram, int ramOffset, bool writable = true)
    {
        for (int i = 0; i < slots; i++)
        {
            rd[firstSlot + i] = ram; rdBase[firstSlot + i] = ramOffset + i * Slot - (firstSlot + i) * Slot;
            wr[firstSlot + i] = writable ? ram : null; wrBase[firstSlot + i] = rdBase[firstSlot + i];
        }
    }

    // The tables hold "array + (offset - slotAddress)" so a read is rd[a >> 10][rdBase[a >> 10] + a] with no masking.
    private void Remap()
    {
        switch (Mapper)
        {
            case SmsMapperKind.Sega:
            {
                // $0000-$03FF fixed to the first KB of the ROM; $0400-$3FFF slot 0; $4000-$7FFF slot 1; $8000-$BFFF slot 2 or RAM
                MapRom(0, 1, 0);
                int p0 = (pages[0] % RomPages16) * 0x4000;
                MapRom(1, 15, p0 + Slot);
                int p1 = (pages[1] % RomPages16) * 0x4000;
                MapRom(16, 16, p1);
                if ((ctl & 0x08) != 0 && Ram != null) MapRam(32, 16, Ram, (ctl & 0x04) != 0 ? 0x4000 : 0);
                else MapRom(32, 16, (pages[2] % RomPages16) * 0x4000);
                break;
            }
            case SmsMapperKind.Codemasters:
                MapRom(0, 16, (pages[0] % RomPages16) * 0x4000);
                MapRom(16, 16, (pages[1] % RomPages16) * 0x4000);
                MapRom(32, 16, (pages[2] % RomPages16) * 0x4000);
                if (cmRamOn && Ram != null) MapRam(40, 8, Ram, 0);    // $A000-$BFFF
                break;
            case SmsMapperKind.KoreanA000:
                MapRom(0, 16, 0);
                MapRom(16, 16, 0x4000 % Rom.Length);
                MapRom(32, 16, (pages[2] % RomPages16) * 0x4000);
                break;
            case SmsMapperKind.MsxKorean:
            case SmsMapperKind.Nemesis:
            {
                // four 8 KB slots at $4000, $6000, $8000, $A000 (registers 2, 3, 0, 1); $0000-$3FFF is the first 16 KB, or (Nemesis) the last 8 KB then the second
                int pages8 = Rom.Length / 0x2000;
                if (Mapper == SmsMapperKind.Nemesis) MapRom(0, 8, ((pages8 - 1) * 0x2000));
                else MapRom(0, 8, 0);
                MapRom(8, 8, Mapper == SmsMapperKind.Nemesis ? 0x2000 % Rom.Length : 0x2000 % Rom.Length);
                MapRom(16, 8, (pages[0] % pages8) * 0x2000);    // register 2 -> $4000
                MapRom(24, 8, (pages[1] % pages8) * 0x2000);    // register 3 -> $6000
                MapRom(32, 8, (pages[2] % pages8) * 0x2000);    // register 0 -> $8000
                MapRom(40, 8, (pages[3] % pages8) * 0x2000);    // register 1 -> $A000
                break;
            }
            default:   // Plain: the ROM at $0000, open bus past its end
                for (int i = 0; i < 48; i++)
                {
                    int off = i * Slot;
                    if (off + Slot <= Rom.Length) { rd[i] = Rom; rdBase[i] = 0; wr[i] = null; }
                    else MapOpen(i, 1);
                }
                break;
        }
    }

    // =================================================================================== access

    /// <summary>Reads $0000-$BFFF.</summary>
    public byte Read(ushort address) => rd[address >> 10][rdBase[address >> 10] + address];

    /// <summary>A write to $0000-$FFFF as the cartridge sees it: cartridge RAM, and the mapper registers.</summary>
    public void Write(ushort address, byte value)
    {
        if (address < 0xC000)
        {
            int slot = address >> 10;
            var w = wr[slot];
            if (w != null)
            {
                int idx = wrBase[slot] + address;
                if (w[idx] != value) { w[idx] = value; RamDirty = true; HasBattery |= Mapper == SmsMapperKind.Sega; }
                return;
            }
        }
        switch (Mapper)
        {
            case SmsMapperKind.Sega:
                if (address >= 0xFFFC)
                {
                    switch (address & 3)
                    {
                        case 0: ctl = value; break;
                        case 1: pages[0] = value; break;
                        case 2: pages[1] = value; break;
                        default: pages[2] = value; break;
                    }
                    Remap();
                }
                break;
            case SmsMapperKind.Codemasters:
                if (address == 0x0000) { pages[0] = value; Remap(); }
                else if (address == 0x4000)
                {
                    pages[1] = value & 0x7F;
                    bool on = (value & 0x80) != 0;
                    if (on && Ram == null) Ram = new byte[0x2000];
                    cmRamOn = on;
                    Remap();
                }
                else if (address == 0x8000) { pages[2] = value; Remap(); }
                break;
            case SmsMapperKind.KoreanA000:
                if (address == 0xA000) { pages[2] = value; Remap(); }
                break;
            case SmsMapperKind.MsxKorean:
            case SmsMapperKind.Nemesis:
                if (address <= 3)
                {
                    // registers 0..3 select the slots at $8000, $A000, $4000, $6000
                    int which = address switch { 0 => 2, 1 => 3, 2 => 0, _ => 1 };
                    pages[which] = value;
                    Remap();
                }
                break;
        }
    }

    // =================================================================================== battery

    /// <summary>The bytes a save file holds: the cartridge RAM (the 32 KB of the Sega mapper), or empty when the cartridge has none.</summary>
    public byte[] ExportSave() => Ram != null && Mapper == SmsMapperKind.Sega ? (byte[])Ram.Clone() : Array.Empty<byte>();

    public void ImportSave(byte[] data)
    {
        if (Ram == null || Mapper != SmsMapperKind.Sega) return;
        Array.Copy(data, Ram, Math.Min(data.Length, Ram.Length));
        RamDirty = false;
    }

    public void ClearDirty() => RamDirty = false;

    // =================================================================================== state

    public void SaveState(BinaryWriter w)
    {
        w.Write(ctl); w.Write(cmRamOn);
        for (int i = 0; i < 4; i++) w.Write(pages[i]);
        w.Write(Ram != null); if (Ram != null) { w.Write(Ram.Length); w.Write(Ram); }
    }

    public void LoadState(BinaryReader r)
    {
        ctl = r.ReadByte(); cmRamOn = r.ReadBoolean();
        for (int i = 0; i < 4; i++) pages[i] = r.ReadInt32();
        if (r.ReadBoolean()) { int n = r.ReadInt32(); var b = r.ReadBytes(n); if (Ram == null || Ram.Length != n) Ram = new byte[n]; Array.Copy(b, Ram, n); }
        Remap();
    }
}

/// <summary>The "TMR SEGA" header every licensed Master System and Game Gear ROM carries at $7FF0 (some at $3FF0 or $1FF0): checksum, product code, region and size.</summary>
public readonly record struct SmsHeader(int Offset, ushort Checksum, int ProductCode, int Version, int RegionNibble, int SizeNibble)
{
    private static readonly byte[] Magic = "TMR SEGA"u8.ToArray();

    public static SmsHeader? Find(byte[] rom)
    {
        foreach (int at in new[] { 0x7FF0, 0x3FF0, 0x1FF0 })
        {
            if (at + 0x10 > rom.Length || !rom.AsSpan(at, Magic.Length).SequenceEqual(Magic)) continue;
            ushort cs = (ushort)(rom[at + 0x0A] | rom[at + 0x0B] << 8);
            int product = rom[at + 0x0C] | rom[at + 0x0D] << 8 | (rom[at + 0x0E] >> 4) << 16;
            int version = rom[at + 0x0E] & 0x0F;
            return new SmsHeader(at, cs, product, version, rom[at + 0x0F] >> 4, rom[at + 0x0F] & 0x0F);
        }
        return null;
    }

    /// <summary>The system code: 3 SMS Japan, 4 SMS export, 5 GG Japan, 6 GG export, 7 GG international.</summary>
    public string RegionName => RegionNibble switch { 3 => "SMS Japan", 4 => "SMS export", 5 => "GG Japan", 6 => "GG export", 7 => "GG international", _ => "?" };
}
