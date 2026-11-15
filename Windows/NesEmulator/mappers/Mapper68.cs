namespace NesEmulator
{
// Mapper 68: Sunsoft-4 (After Burner, Maharaja). $8000/$9000/$A000/$B000: 2KB CHR at $0000/$0800/$1000/$1800;
// $C000/$D000: 1KB CHR-ROM page (|$80) used as nametable 0/1; $E000 [...R ..MM]: MM 0 V/1 H/2 A/3 B,
// R = nametables come from CHR-ROM; $F000 [...E PPPP]: 16KB PRG at $8000, E = PRG-RAM enable. $C000 fixed to last.
public class Mapper68 : DiscreteMapperBase, IMapper   // IMapper again: re-implements the nametable hooks
{
    private int nt0, nt1, ctrl, prgRamOn;
    public Mapper68(Cartridge c) : base(c) { }
    protected override void ResetBoard() { nt0 = nt1 = ctrl = 0; prgRamOn = 0; SetPrg16(0, 0); SetPrg16(1, LastPrg16); }
    public override byte CPURead(ushort address)
    {
        if (address >= 0x6000 && address < 0x8000) return cart.prgRAM[address - 0x6000];
        return base.CPURead(address);
    }
    public override bool IsCpuReadOpenBus(ushort address) => address < 0x6000 || (address < 0x8000 && prgRamOn == 0);
    public override void CPUWrite(ushort address, byte value)
    {
        if (address >= 0x6000 && address < 0x8000) { if (prgRamOn != 0) cart.prgRAM[address - 0x6000] = value; return; }
        base.CPUWrite(address, value);
    }
    protected override void WriteRegister(ushort address, byte value)
    {
        switch (address & 0xF000)
        {
            case 0x8000: SetChr2(0, value); break;
            case 0x9000: SetChr2(1, value); break;
            case 0xA000: SetChr2(2, value); break;
            case 0xB000: SetChr2(3, value); break;
            case 0xC000: nt0 = value | 0x80; break;
            case 0xD000: nt1 = value | 0x80; break;
            case 0xE000:
                ctrl = value;
                SetMirror((value & 3) switch { 0 => Mirroring.Vertical, 1 => Mirroring.Horizontal, 2 => Mirroring.SingleScreenA, _ => Mirroring.SingleScreenB });
                break;
            case 0xF000: SetPrg16(0, value & 0x0F); prgRamOn = (value >> 4) & 1; break;
        }
    }
    // Nametables from CHR-ROM (R bit): which of nt0/nt1 a quadrant uses follows the mirroring.
    private bool RomNt => (ctrl & 0x10) != 0 && cart.chrBanks > 0;
    private int NtPage(ushort address)
    {
        int q = (address >> 10) & 3;
        int sel = (ctrl & 3) switch { 0 => q & 1, 1 => q >> 1, 2 => 0, _ => 1 };
        return sel == 0 ? nt0 : nt1;
    }
    public bool TryPpuNametableRead(ushort address, out byte value)
    {
        value = 0;
        if (!RomNt || address < 0x2000 || address >= 0x3F00) return false;
        int idx = (NtPage(address) * 0x400 + (address & 0x3FF)) % cart.chrROM.Length;
        value = cart.chrROM[idx]; return true;
    }
    public bool TryPpuNametableWrite(ushort address, byte value) => RomNt && address >= 0x2000 && address < 0x3F00;
    protected override int[] ExtraState { get => new[] { nt0, nt1, ctrl, prgRamOn }; set { if (value.Length == 4) { nt0 = value[0]; nt1 = value[1]; ctrl = value[2]; prgRamOn = value[3]; } } }
}
}
