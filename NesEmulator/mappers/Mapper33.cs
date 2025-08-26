namespace NesEmulator
{
// Mapper 33: Taito TC0190/TC0350 (no IRQ). PRG: 2x8KB switchable + last 2 fixed. CHR: 2x2KB + 4x1KB.
public class Mapper33 : IMapper
{
    private readonly Cartridge cart;

    // PRG: two 8KB switchable banks
    private int prgBank0; // $8000-$9FFF
    private int prgBank1; // $A000-$BFFF

    // CHR: two 2KB banks and four 1KB banks
    private int chr2k0; // $0000-$07FF (2KB units)
    private int chr2k1; // $0800-$0FFF (2KB units)
    private int chr1k2; // $1000-$13FF (1KB units)
    private int chr1k3; // $1400-$17FF
    private int chr1k4; // $1800-$1BFF
    private int chr1k5; // $1C00-$1FFF

    private int prgBankCount; // in 8KB units

    public Mapper33(Cartridge c)
    {
        cart = c;
        prgBankCount = cart.prgROM.Length / 0x2000;
    }

    public void Reset()
    {
        prgBank0 = 0;
        prgBank1 = 1 % (prgBankCount == 0 ? 1 : prgBankCount);

        chr2k0 = 0;
        chr2k1 = 0;
        chr1k2 = 0;
        chr1k3 = 0;
        chr1k4 = 0;
        chr1k5 = 0;

        // use header mirroring initially; will be overridden by writes to $8000
        cart.SetMirroring(cart.mirroringMode);
    }

    public byte CPURead(ushort address)
    {
        if (address >= 0x6000 && address <= 0x7FFF)
        {
            return cart.prgRAM[(address - 0x6000) % cart.prgRAM.Length];
        }

        if (address >= 0x8000)
        {
            int bankIndex;
            int ofsInBank = address & 0x1FFF;
            if (address < 0xA000)
                bankIndex = prgBank0;
            else if (address < 0xC000)
                bankIndex = prgBank1;
            else if (address < 0xE000)
                bankIndex = (prgBankCount - 2 + prgBankCount) % prgBankCount; // -2
            else
                bankIndex = (prgBankCount - 1 + prgBankCount) % prgBankCount; // -1

            bankIndex %= (prgBankCount == 0 ? 1 : prgBankCount);
            int baseAddr = bankIndex * 0x2000;
            int idx = (baseAddr + ofsInBank) % cart.prgROM.Length;
            return cart.prgROM[idx];
        }

        return 0;
    }

    public void CPUWrite(ushort address, byte value)
    {
        if (address >= 0x6000 && address <= 0x7FFF)
        {
            cart.prgRAM[(address - 0x6000) % cart.prgRAM.Length] = value;
            return;
        }

        // Registers at $8000-$8003 and $A000-$A003 (mirrored across $8000-$BFFF)
        ushort region = (ushort)(address & 0xE000);
        int reg = address & 0x0003;

        if (region == 0x8000)
        {
            switch (reg)
            {
                case 0: // $8000 [.MPP PPPP] : M=mirroring bit6, P=PRG bank 0
                    if ((value & 0x40) != 0) cart.SetMirroring(Mirroring.Horizontal); else cart.SetMirroring(Mirroring.Vertical);
                    SetPrgBank0(value & 0x1F);
                    break;
                case 1: // $8001 [..PP PPPP] : PRG bank 1
                    SetPrgBank1(value & 0x1F);
                    break;
                case 2: // $8002 [CCCC CCCC] : CHR 2KB @ $0000
                    chr2k0 = value; // 2KB units
                    break;
                case 3: // $8003 [CCCC CCCC] : CHR 2KB @ $0800
                    chr2k1 = value; // 2KB units
                    break;
            }
        }
        else if (region == 0xA000)
        {
            switch (reg)
            {
                case 0: // $A000 : CHR 1KB @ $1000
                    chr1k2 = value;
                    break;
                case 1: // $A001 : CHR 1KB @ $1400
                    chr1k3 = value;
                    break;
                case 2: // $A002 : CHR 1KB @ $1800
                    chr1k4 = value;
                    break;
                case 3: // $A003 : CHR 1KB @ $1C00
                    chr1k5 = value;
                    break;
            }
        }
        // Writes to $C000-$FFFF are ignored (no IRQ)
    }

    public byte PPURead(ushort address)
    {
        if (address >= 0x2000) return 0;

        // Calculate CHR source index depending on bank sizes
        int idx;
        if (address < 0x0800)
        {
            int baseOff = chr2k0 * 0x800;
            int total = cart.chrBanks > 0 ? cart.chrROM.Length : cart.chrRAM.Length;
            idx = (baseOff + address) % total;
        }
        else if (address < 0x1000)
        {
            int baseOff = chr2k1 * 0x800;
            idx = (baseOff + (address - 0x0800)) % (cart.chrBanks > 0 ? cart.chrROM.Length : cart.chrRAM.Length);
        }
        else if (address < 0x1400)
        {
            int baseOff = chr1k2 * 0x400;
            idx = (baseOff + (address - 0x1000)) % (cart.chrBanks > 0 ? cart.chrROM.Length : cart.chrRAM.Length);
        }
        else if (address < 0x1800)
        {
            int baseOff = chr1k3 * 0x400;
            idx = (baseOff + (address - 0x1400)) % (cart.chrBanks > 0 ? cart.chrROM.Length : cart.chrRAM.Length);
        }
        else if (address < 0x1C00)
        {
            int baseOff = chr1k4 * 0x400;
            idx = (baseOff + (address - 0x1800)) % (cart.chrBanks > 0 ? cart.chrROM.Length : cart.chrRAM.Length);
        }
        else
        {
            int baseOff = chr1k5 * 0x400;
            idx = (baseOff + (address - 0x1C00)) % (cart.chrBanks > 0 ? cart.chrROM.Length : cart.chrRAM.Length);
        }

        if (cart.chrBanks > 0)
            return cart.chrROM[idx];
        else
            return cart.chrRAM[idx];
    }

    public void PPUWrite(ushort address, byte value)
    {
        if (address < 0x2000 && cart.chrBanks == 0)
        {
            // Respect the same mapping when writing to CHR-RAM
            int idx;
            if (address < 0x0800)
                idx = (chr2k0 * 0x800 + address) % cart.chrRAM.Length;
            else if (address < 0x1000)
                idx = (chr2k1 * 0x800 + (address - 0x0800)) % cart.chrRAM.Length;
            else if (address < 0x1400)
                idx = (chr1k2 * 0x400 + (address - 0x1000)) % cart.chrRAM.Length;
            else if (address < 0x1800)
                idx = (chr1k3 * 0x400 + (address - 0x1400)) % cart.chrRAM.Length;
            else if (address < 0x1C00)
                idx = (chr1k4 * 0x400 + (address - 0x1800)) % cart.chrRAM.Length;
            else
                idx = (chr1k5 * 0x400 + (address - 0x1C00)) % cart.chrRAM.Length;
            cart.chrRAM[idx] = value;
        }
    }

    public bool TryCpuToPrgIndex(ushort address, out int prgIndex)
    {
        prgIndex = -1; if (address < 0x8000) return false;
        int bankIndex; int ofs = address & 0x1FFF;
        if (address < 0xA000) bankIndex = prgBank0;
        else if (address < 0xC000) bankIndex = prgBank1;
        else if (address < 0xE000) bankIndex = (prgBankCount - 2 + prgBankCount) % prgBankCount;
        else bankIndex = (prgBankCount - 1 + prgBankCount) % prgBankCount;
        bankIndex %= (prgBankCount == 0 ? 1 : prgBankCount);
        int idx = bankIndex * 0x2000 + ofs;
        if (idx >= 0 && idx < cart.prgROM.Length) { prgIndex = idx; return true; }
        return false;
    }

    private void SetPrgBank0(int value)
    {
        if (prgBankCount > 0) prgBank0 = value % prgBankCount;
    }

    private void SetPrgBank1(int value)
    {
        if (prgBankCount > 0) prgBank1 = value % prgBankCount;
    }

    private class Mapper33State
    {
        public int prg0, prg1, c2k0, c2k1, c1k2, c1k3, c1k4, c1k5;
    }
    public object GetMapperState() => new Mapper33State { prg0 = prgBank0, prg1 = prgBank1, c2k0 = chr2k0, c2k1 = chr2k1, c1k2 = chr1k2, c1k3 = chr1k3, c1k4 = chr1k4, c1k5 = chr1k5 };
    public void SetMapperState(object state)
    {
        if (state is Mapper33State s)
        {
            prgBank0 = s.prg0; prgBank1 = s.prg1; chr2k0 = s.c2k0; chr2k1 = s.c2k1; chr1k2 = s.c1k2; chr1k3 = s.c1k3; chr1k4 = s.c1k4; chr1k5 = s.c1k5; return;
        }
        if (state is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            try
            {
                if (je.TryGetProperty("prg0", out var p0)) prgBank0 = p0.GetInt32();
                if (je.TryGetProperty("prg1", out var p1)) prgBank1 = p1.GetInt32();
                if (je.TryGetProperty("c2k0", out var v0)) chr2k0 = v0.GetInt32();
                if (je.TryGetProperty("c2k1", out var v1)) chr2k1 = v1.GetInt32();
                if (je.TryGetProperty("c1k2", out var w2)) chr1k2 = w2.GetInt32();
                if (je.TryGetProperty("c1k3", out var w3)) chr1k3 = w3.GetInt32();
                if (je.TryGetProperty("c1k4", out var w4)) chr1k4 = w4.GetInt32();
                if (je.TryGetProperty("c1k5", out var w5)) chr1k5 = w5.GetInt32();
            }
            catch { }
        }
    }

    public uint GetChrBankSignature()
    {
        unchecked
        {
            uint sig = 2166136261u;
            sig = (sig * 16777619u) ^ (uint)(byte)chr2k0;
            sig = (sig * 16777619u) ^ (uint)(byte)chr2k1;
            sig = (sig * 16777619u) ^ (uint)(byte)chr1k2;
            sig = (sig * 16777619u) ^ (uint)(byte)chr1k3;
            sig = (sig * 16777619u) ^ (uint)(byte)chr1k4;
            sig = (sig * 16777619u) ^ (uint)(byte)chr1k5;
            return sig;
        }
    }
}
}
