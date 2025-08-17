namespace NesEmulator
{
public class Mapper2 : IMapper { //UxROM (Experimental)
    private Cartridge cartridge;
    private byte prgBank;

    public Mapper2(Cartridge cart) {
        cartridge = cart;
        prgBank = 0;
    }

    public void Reset() {
        prgBank = 0;
    }

    public byte CPURead(ushort addr) {
        if (addr >= 0x8000 && addr <= 0xBFFF) {
            int index = (prgBank * 0x4000) + (addr - 0x8000);
            return index < cartridge.prgROM.Length ? cartridge.prgROM[index] : (byte)0xFF;
        } else if (addr >= 0xC000 && addr <= 0xFFFF) {
            int fixedBankStart = cartridge.prgROM.Length - 0x4000;
            int index = fixedBankStart + (addr - 0xC000);
            return index < cartridge.prgROM.Length ? cartridge.prgROM[index] : (byte)0xFF;
        }
        return 0;
    }

    public void CPUWrite(ushort addr, byte val) {
        if (addr >= 0x8000) {
            prgBank = (byte)(val & 0x0F);
        }
    }

    public byte PPURead(ushort addr) {
        if (addr < 0x2000) {
            if (cartridge.chrBanks == 0)
                return cartridge.chrRAM[addr];
            return cartridge.chrROM[addr % cartridge.chrROM.Length];
        }
        return 0;
    }

    public void PPUWrite(ushort addr, byte val) {
        if (cartridge.chrBanks == 0 && addr < 0x2000) {
            cartridge.chrRAM[addr] = val;
        }
    }

    private class Mapper2State { public byte prgBank; }
    public object GetMapperState() => new Mapper2State { prgBank = this.prgBank };
    public void SetMapperState(object state) {
        if (state is Mapper2State s) { prgBank = s.prgBank; return; }
        if (state is System.Text.Json.JsonElement je) {
            try {
                if(je.ValueKind==System.Text.Json.JsonValueKind.Object && je.TryGetProperty("prgBank", out var pb)) prgBank = (byte)pb.GetByte();
            } catch { }
        }
    }
        public uint GetChrBankSignature() => 0;
}
}
