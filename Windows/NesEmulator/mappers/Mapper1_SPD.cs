namespace NesEmulator
{
public class Mapper1_SPD : IMapper { //MMC1 (Experimenal)
    private Cartridge cartridge;

    private byte shiftRegister = 0x10;
    private byte control = 0x0C;
    private byte chrBank0, chrBank1, prgBank;
    private int shiftCount = 0;

    private int prgBankOffset0, prgBankOffset1;
    private int chrBankOffset0, chrBankOffset1;

    public Mapper1_SPD(Cartridge cart) {
        cartridge = cart;
    }

    public void Reset() {
        shiftRegister = 0x10;
        control = 0x0C;
        chrBank0 = chrBank1 = prgBank = 0;
        shiftCount = 0;
        ApplyMirroring();
        ApplyBanks();
    }

    public byte CPURead(ushort addr) {
        if (addr >= 0x6000 && addr <= 0x7FFF) {
            return cartridge.prgRAM[addr - 0x6000];
        } else if (addr >= 0x8000 && addr <= 0xBFFF) {
            int index = prgBankOffset0 + (addr - 0x8000);
            return cartridge.prgROM[index];
        } else if (addr >= 0xC000 && addr <= 0xFFFF) {
            int index = prgBankOffset1 + (addr - 0xC000);
            return cartridge.prgROM[index];
        }
        return 0;
    }

    public void CPUWrite(ushort addr, byte val) {
        if (addr >= 0x6000 && addr <= 0x7FFF) {
            cartridge.prgRAM[addr - 0x6000] = val;
            return;
        }

        if (addr < 0x8000) return;

        if ((val & 0x80) != 0) {
            shiftRegister = 0x10;
            control |= 0x0C;
            shiftCount = 0;
            ApplyBanks();
            return;
        }

        shiftRegister = (byte)((shiftRegister >> 1) | ((val & 1) << 4));
        shiftCount++;

        if (shiftCount == 5) {
            int reg = (addr >> 13) & 0x03;
            switch (reg) {
                case 0:
                    control = (byte)(shiftRegister & 0x1F);
                    ApplyMirroring();
                    break;
                case 1:
                    // CHR bank 0 update (4KB or lower 8KB slice). Does not affect mirroring.
                    chrBank0 = (byte)(shiftRegister & 0x1F);
                    break;
                case 2:
                    chrBank1 = (byte)(shiftRegister & 0x1F);
                    break;
                case 3:
                    prgBank = (byte)(shiftRegister & 0x0F);
                    break;
            }
            shiftRegister = 0x10;
            shiftCount = 0;
            ApplyBanks();
        }
    }

    public byte PPURead(ushort addr) {
        if (addr < 0x2000) {
            if (cartridge.chrBanks == 0) {
                return cartridge.chrRAM[addr];
            }
            
            int chrMode = (control >> 4) & 1;
            if (chrMode == 0) {
                int offset = (chrBank0 & 0x1E) * 0x1000;
                return cartridge.chrROM[(addr + offset) % cartridge.chrROM.Length];
            } else {
                if (addr < 0x1000) {
                    return cartridge.chrROM[(addr + chrBankOffset0) % cartridge.chrROM.Length];
                } else {
                    return cartridge.chrROM[((addr - 0x1000) + chrBankOffset1) % cartridge.chrROM.Length];
                }
            }
        }
        return 0;
    }

    public void PPUWrite(ushort addr, byte val) {
        if (addr < 0x2000 && cartridge.chrBanks == 0) {
            cartridge.chrRAM[addr] = val;
        }
    }

    private void ApplyMirroring() {
        switch (control & 0x03) {
            case 0: cartridge.SetMirroring(Mirroring.SingleScreenA); break;
            case 1: cartridge.SetMirroring(Mirroring.SingleScreenB); break;
            case 2: cartridge.SetMirroring(Mirroring.Vertical); break;
            case 3: cartridge.SetMirroring(Mirroring.Horizontal); break;
        }
    }

    private void ApplyBanks() {
        int chrMode = (control >> 4) & 1;
        if (chrMode == 0) {
            // 8KB CHR mode: ignore low bit, select even 4KB pair.
            int maxPairs = (cartridge.chrROM.Length / 0x2000); // number of 8KB chunks
            int evenBank = (chrBank0 & 0x1E) >> 1; // pair index
            if (maxPairs > 0) evenBank = Math.Min(evenBank, maxPairs - 1);
            chrBankOffset0 = evenBank * 0x2000;        // full 8KB window start
            // Internally still treat as two 4KB slices for renderer logic
            chrBankOffset1 = chrBankOffset0 + 0x1000;
        } else {
            // 4KB CHR banking
            int max4k = cartridge.chrROM.Length / 0x1000;
            int b0 = chrBank0 % Math.Max(1, max4k);
            int b1 = chrBank1 % Math.Max(1, max4k);
            chrBankOffset0 = b0 * 0x1000;
            chrBankOffset1 = b1 * 0x1000;
        }

        if (cartridge.chrBanks > 0) {
            chrBankOffset0 %= cartridge.chrROM.Length;
            chrBankOffset1 %= cartridge.chrROM.Length;
        }

        int prgMode = (control >> 2) & 0x03;
        int prgBankCount = cartridge.prgROM.Length / 0x4000;

        switch (prgMode) {
            case 0:
            case 1:
                // 32KB mode: ignore low bit; map selected even bank and the next one.
                if (prgBankCount == 0) { prgBankOffset0 = prgBankOffset1 = 0; break; }
                int even = (prgBank & 0x0E) >> 1; // pair index
                // Maximum pair index is prgBankCount/2 - 1 when prgBankCount even.
                int maxPair = (prgBankCount / 2) - 1;
                if (maxPair < 0) maxPair = 0;
                if (even > maxPair) even = maxPair;
                // Derive 32KB window start (two 16KB banks)
                prgBankOffset0 = even * 2 * 0x4000;
                prgBankOffset1 = prgBankOffset0 + 0x4000;
                break;
            case 2:
                prgBankOffset0 = 0;
                prgBankOffset1 = (prgBank % Math.Max(1, prgBankCount)) * 0x4000;
                break;
            case 3:
                prgBankOffset0 = (prgBank % Math.Max(1, prgBankCount)) * 0x4000;
                prgBankOffset1 = (prgBankCount - 1) * 0x4000;
                break;
        }
        
        prgBankOffset0 %= cartridge.prgROM.Length;
        prgBankOffset1 %= cartridge.prgROM.Length;
    }

    public bool TryCpuToPrgIndex(ushort address, out int prgIndex)
    {
        prgIndex = -1;
        if (address < 0x8000) return false;
        if (address <= 0xBFFF)
        {
            prgIndex = prgBankOffset0 + (address - 0x8000);
        }
        else // 0xC000..0xFFFF
        {
            prgIndex = prgBankOffset1 + (address - 0xC000);
        }
        if (prgIndex >= 0 && prgIndex < cartridge.prgROM.Length) return true;
        prgIndex = -1; return false;
    }

    private class Mapper1State {
        public byte shiftRegister, control, chrBank0, chrBank1, prgBank; public int shiftCount;
    }
    public object GetMapperState() => new Mapper1State { shiftRegister=this.shiftRegister, control=this.control, chrBank0=this.chrBank0, chrBank1=this.chrBank1, prgBank=this.prgBank, shiftCount=this.shiftCount };
    public void SetMapperState(object stateObj) {
        if (stateObj is Mapper1State s) { shiftRegister = s.shiftRegister; control = s.control; chrBank0 = s.chrBank0; chrBank1 = s.chrBank1; prgBank = s.prgBank; shiftCount = s.shiftCount; ApplyMirroring(); ApplyBanks(); return; }
        if (stateObj is System.Text.Json.JsonElement je) {
            try {
                if(je.ValueKind==System.Text.Json.JsonValueKind.Object){
                    if(je.TryGetProperty("shiftRegister", out var sr)) shiftRegister = (byte)sr.GetByte();
                    if(je.TryGetProperty("control", out var c)) control = (byte)c.GetByte();
                    if(je.TryGetProperty("chrBank0", out var cb0)) chrBank0 = (byte)cb0.GetByte();
                    if(je.TryGetProperty("chrBank1", out var cb1)) chrBank1 = (byte)cb1.GetByte();
                    if(je.TryGetProperty("prgBank", out var pb)) prgBank = (byte)pb.GetByte();
                    if(je.TryGetProperty("shiftCount", out var sc)) shiftCount = sc.GetInt32();
                    ApplyMirroring(); ApplyBanks();
                }
            } catch { }
        }
    }
    public uint GetChrBankSignature() {
        int chrMode = (control >> 4) & 1;
        unchecked {
            uint sig = (uint)(chrMode | ((control & 0x03) << 1));
            sig = (sig * 16777619u) ^ (uint)(chrBankOffset0 >> 10);
            sig = (sig * 16777619u) ^ (uint)(chrBankOffset1 >> 10);
            return sig;
        }
    }
}
}
