namespace NesEmulator
{
// Mapper 71 (Camerica BF9093/BF9097, Codemasters): UNROM-like. $C000-$FFFF selects the 16KB bank at
// $8000 ($C000 fixed to the last bank); CHR is 8KB RAM. The BF9097 (Fire Hawk, submapper 1) also
// takes a one-screen select at $9000-$9FFF bit 4 - only obeyed once the game writes there, so the
// header mirroring of the BF9093 games stays untouched (Mesen does the same).
public sealed class Mapper71 : RegisterMapperBase
{
    public Mapper71(Cartridge c) : base(c, 2) { }   // r0 PRG bank, r1 mirroring (0 = header, 2/3 = one-screen)
    protected override bool PrgRamPresent => false;
    protected override void Apply()
    {
        SetPrg16(0, r[0]); SetPrg16(1, PrgLen / 0x4000 - 1); SetChr8(0);
        if (r[1] >= 2) Mirror(r[1]);
    }
    public override void CPUWrite(ushort address, byte value)
    {
        if (address >= 0xC000) { r[0] = value; Apply(); }
        else if (address >= 0x9000 && address < 0xA000) { r[1] = (value & 0x10) != 0 ? 3 : 2; Apply(); }
    }
}
}
