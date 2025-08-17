namespace NesEmulator
{
// Mapper 7 (AxROM / AOROM): 32KB PRG bank switching, single-screen mirroring select
//  - PRG: switch 32KB at $8000 via bits 0-2 (allows up to 8 * 32KB = 256KB)
//  - CHR: typically CHR RAM; if CHR ROM present treat as fixed 8KB
//  - Mirroring: bit 4 chooses SingleScreenA/B (nametable 0 or 1)
public class Mapper7 : IMapper
{
    private readonly Cartridge cart;
    private int prgBank; // 32KB bank index
    private bool highNametable;

    public Mapper7(Cartridge c) { cart = c; }

    public void Reset() { prgBank = 0; highNametable = false; ApplyMirroring(); }

    private void ApplyMirroring() => cart.SetMirroring(highNametable ? Mirroring.SingleScreenB : Mirroring.SingleScreenA);

    public byte CPURead(ushort address)
    {
        if (address >= 0x6000 && address <= 0x7FFF)
            return cart.prgRAM[address - 0x6000];
        if (address >= 0x8000)
        {
            int bank = prgBank % (cart.prgBanks == 0 ? 1 : cart.prgBanks);
            int offset = bank * 0x8000 + (address - 0x8000);
            if (offset >= 0 && offset < cart.prgROM.Length) return cart.prgROM[offset];
            return 0xFF;
        }
        return 0;
    }

    public void CPUWrite(ushort address, byte value)
    {
        if (address >= 0x6000 && address <= 0x7FFF)
        {
            cart.prgRAM[address - 0x6000] = value; // generic RAM access
            return;
        }
        if (address >= 0x8000)
        {
            prgBank = value & 0x07;
            highNametable = (value & 0x10) != 0;
            ApplyMirroring();
        }
    }

    public byte PPURead(ushort address)
    {
        if (address < 0x2000)
        {
            if (cart.chrBanks > 0)
                return cart.chrROM[address % cart.chrROM.Length];
            return cart.chrRAM[address];
        }
        return 0;
    }

    public void PPUWrite(ushort address, byte value)
    {
        if (address < 0x2000 && cart.chrBanks == 0)
            cart.chrRAM[address] = value;
    }

    private class Mapper7State { public int prgBank; public bool highNametable; }
    public object GetMapperState() => new Mapper7State { prgBank = prgBank, highNametable = highNametable };
    public void SetMapperState(object state)
    {
        if (state is Mapper7State s) { prgBank = s.prgBank; highNametable = s.highNametable; ApplyMirroring(); return; }
        if (state is System.Text.Json.JsonElement je)
        {
            if (je.TryGetProperty("prgBank", out var pb)) prgBank = pb.GetInt32();
            if (je.TryGetProperty("highNametable", out var hn)) highNametable = hn.GetBoolean();
            ApplyMirroring();
        }
    }
    public uint GetChrBankSignature() => 0;
}
}
