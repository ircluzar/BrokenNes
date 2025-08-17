namespace NesEmulator
{
// Mapper 3 (CNROM): Fixed PRG, switchable CHR via writes to $8000-$FFFF
public class Mapper3 : IMapper
{
    private readonly Cartridge cart;
    private int chrBank; // selected 8KB CHR bank
    private int chrBankMask; // mask for bank select (handles non power-of-two by modulo fallback)

    public Mapper3(Cartridge c)
    {
        cart = c;
        chrBankMask = (c.chrBanks > 0) ? (c.chrBanks - 1) : 0;
    }

    public void Reset()
    {
        chrBank = 0;
    }

    public byte CPURead(ushort address)
    {
        if (address >= 0x6000 && address <= 0x7FFF)
            return cart.prgRAM[address - 0x6000];
        if (address >= 0x8000 && address <= 0xFFFF)
        {
            if (cart.prgBanks == 1)
                return cart.prgROM[address & 0x3FFF]; // 16KB mirror
            else
                return cart.prgROM[address - 0x8000]; // 32KB
        }
        return 0;
    }

    public void CPUWrite(ushort address, byte value)
    {
        if (address >= 0x6000 && address <= 0x7FFF)
        {
            cart.prgRAM[address - 0x6000] = value;
            return;
        }
        if (address >= 0x8000)
        {
            // Select CHR bank (low bits). CNROM boards often have bus conflicts; ignore for now.
            if (cart.chrBanks > 0)
            {
                int bank = value & 0x03; // spec: usually 2 bits (max 4 banks -> 32KB CHR)
                // Allow >4 banks by masking with (chrBanks-1) if more CHR present (some variants)
                if (chrBankMask != 0)
                    bank &= chrBankMask;
                else
                    bank %= (cart.chrBanks == 0 ? 1 : cart.chrBanks);
                chrBank = bank;
            }
        }
    }

    public byte PPURead(ushort address)
    {
        if (address < 0x2000)
        {
            if (cart.chrBanks > 0)
            {
                int idx = chrBank * 0x2000 + address;
                if (idx >= 0 && idx < cart.chrROM.Length) return cart.chrROM[idx];
                return 0;
            }
            // CHR RAM fallback
            return cart.chrRAM[address];
        }
        return 0;
    }

    public void PPUWrite(ushort address, byte value)
    {
        if (address < 0x2000 && cart.chrBanks == 0)
        {
            cart.chrRAM[address] = value;
        }
    }

    private class Mapper3State { public int chrBank; }
    public object GetMapperState() => new Mapper3State { chrBank = chrBank };
    public void SetMapperState(object state)
    {
        if (state is Mapper3State s) { chrBank = s.chrBank; return; }
        if (state is System.Text.Json.JsonElement je && je.TryGetProperty("chrBank", out var cb)) chrBank = cb.GetInt32();
    }
    public uint GetChrBankSignature() => (uint)chrBank;
}
}
