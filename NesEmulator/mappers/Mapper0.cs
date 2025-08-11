namespace NesEmulator
{
public class Mapper0 : IMapper { //NROM
    private Cartridge cartridge;

    public Mapper0(Cartridge cart) {
        cartridge = cart;
    }

    public void Reset() {

    }

    public byte CPURead(ushort address) {
        if (address >= 0x6000 && address <= 0x7FFF) {
            return cartridge.prgRAM[address - 0x6000];
        } else if (address >= 0x8000 && address <= 0xFFFF) {
            if (cartridge.prgBanks == 1) {
                return cartridge.prgROM[address & 0x3FFF];
            } else {
                return cartridge.prgROM[address - 0x8000];
            }
        }
        return 0;
    }

    public void CPUWrite(ushort address, byte value) {
        if (address >= 0x6000 && address <= 0x7FFF) {
            cartridge.prgRAM[address - 0x6000] = value;
        }
    }

    public byte PPURead(ushort address) {
        if (address < 0x2000) {
            if (cartridge.chrBanks != 0) {
                return cartridge.chrROM[address];
            } else {
                return cartridge.chrRAM[address];
            }
            
        }
        return 0;
    }

    public void PPUWrite(ushort address, byte value) {
        if (address < 0x2000 && cartridge.chrBanks == 0) {
            cartridge.chrRAM[address] = value;
        }
    }

    private class Mapper0State { }
    public object GetMapperState() => new Mapper0State();
    public void SetMapperState(object state) { /* stateless */ }
}
}
