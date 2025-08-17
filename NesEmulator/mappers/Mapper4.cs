namespace NesEmulator
{
public class Mapper4 : IMapper { //MMC3 (Experimental)
    private Cartridge cartridge;

    private byte bankSelect;
    private byte[] bankData = new byte[8];
    private int[] prgBankOffsets = new int[4];
    private int[] chrBankOffsets = new int[8];

    private bool prgMode;
    private bool chrMode;
    private bool prgRamEnable;
    private bool prgRamWriteProtect;

    private byte irqLatch;
    private byte irqCounter;
    private bool irqEnable;
    private bool irqReloadPending;
    private bool irqAsserted;

    public Mapper4(Cartridge cart) {
        cartridge = cart;
    }

    public void Reset() {
        bankSelect = 0;

        for (int i = 0; i < bankData.Length; i++) {
            bankData[i] = 0;
        }

        prgMode = false;
        chrMode = false;
        prgRamEnable = true;
        prgRamWriteProtect = false;
        
        irqLatch = 0;
        irqCounter = 0;
        irqEnable = false;
        irqReloadPending = false;
        irqAsserted = false;
        
        ApplyBankMapping();
    }

    public void RunScanlineIRQ() {
        if (irqCounter == 0) {
            irqCounter = irqLatch;
        } else {
            irqCounter--;
            if (irqCounter == 0 && irqEnable) {
                irqAsserted = true;
            }
        }

        if (irqReloadPending) {
            irqCounter = irqLatch;
            irqReloadPending = false;
        }
    }

    public bool IRQPending() {
        return irqAsserted;
    }

    public void ClearIRQ() {
        irqAsserted = false;
    }

    public byte CPURead(ushort address) {
        if (address >= 0x6000 && address <= 0x7FFF) {
            if (prgRamEnable) {
                int ramOffset = (address - 0x6000) % cartridge.prgRAM.Length;
                return cartridge.prgRAM[ramOffset];
            }
            return 0xFF;
        }

        if (address >= 0x8000 && address <= 0xFFFF) {
            int bankIndex = (address - 0x8000) / 0x2000;
            int bankOffset = prgBankOffsets[bankIndex];
            int addressOffset = address % 0x2000;
            
            int finalOffset = (bankOffset + addressOffset) % cartridge.prgROM.Length;
            return cartridge.prgROM[finalOffset];
        }

        return 0;
    }

    public void CPUWrite(ushort address, byte value) {
        if (address >= 0x6000 && address <= 0x7FFF) {
            if (prgRamEnable && !prgRamWriteProtect) {
                int ramOffset = (address - 0x6000) % cartridge.prgRAM.Length;
                cartridge.prgRAM[ramOffset] = value;
            }
            return;
        }

        switch (address & 0xE001) {
            case 0x8000:
                bankSelect = value;
                prgMode = (value & 0x40) != 0;
                chrMode = (value & 0x80) != 0;
                ApplyBankMapping();
                break;
            case 0x8001:
                int reg = bankSelect & 0x07;
                bankData[reg] = value;
                ApplyBankMapping();
                break;
            case 0xA000:
                if ((value & 1) == 0)
                    cartridge.SetMirroring(Mirroring.Vertical);
                else
                    cartridge.SetMirroring(Mirroring.Horizontal);
                break;
            case 0xA001:
                prgRamEnable = (value & 0x80) != 0;
                prgRamWriteProtect = (value & 0x40) != 0;
                break;
            case 0xC000:
                irqLatch = value;
                break;
            case 0xC001:
                irqReloadPending = true;
                break;
            case 0xE000:
                irqEnable = false;
                irqAsserted = false;
                break;
            case 0xE001:
                irqEnable = true;
                break;
        }
    }

    public byte PPURead(ushort address) {
        if (address >= 0x2000) return 0;

        if (cartridge.chrBanks == 0) {
            return cartridge.chrRAM[address % cartridge.chrRAM.Length];
        }

        int bank = address / 0x0400;
        int bankOffset = chrBankOffsets[bank];
        int addressOffset = address % 0x0400;
        
        int finalOffset = (bankOffset + addressOffset) % cartridge.chrROM.Length;
        return cartridge.chrROM[finalOffset];
    }

    public void PPUWrite(ushort address, byte value) {
        if (address < 0x2000) {
            if (cartridge.chrBanks == 0) {
                cartridge.chrRAM[address] = value;
            }
        }
    }

    private void ApplyBankMapping() {        
        if (chrMode) {
            chrBankOffsets[0] = bankData[2] * 0x400;
            chrBankOffsets[1] = bankData[3] * 0x400;
            chrBankOffsets[2] = bankData[4] * 0x400;
            chrBankOffsets[3] = bankData[5] * 0x400;

            chrBankOffsets[4] = (bankData[0] & 0xFE) * 0x400;
            chrBankOffsets[5] = chrBankOffsets[4] + 0x400;
            chrBankOffsets[6] = (bankData[1] & 0xFE) * 0x400;
            chrBankOffsets[7] = chrBankOffsets[6] + 0x400;
        } else {
            chrBankOffsets[0] = (bankData[0] & 0xFE) * 0x400;
            chrBankOffsets[1] = chrBankOffsets[0] + 0x400;
            chrBankOffsets[2] = (bankData[1] & 0xFE) * 0x400;
            chrBankOffsets[3] = chrBankOffsets[2] + 0x400;

            chrBankOffsets[4] = bankData[2] * 0x400;
            chrBankOffsets[5] = bankData[3] * 0x400;
            chrBankOffsets[6] = bankData[4] * 0x400;
            chrBankOffsets[7] = bankData[5] * 0x400;
        }

        int bankCount = cartridge.prgROM.Length / 0x2000;
        int lastBank = bankCount - 1;

        int bank6 = bankData[6] % bankCount;
        int bank7 = bankData[7] % bankCount;

        if (prgMode) {
            prgBankOffsets[0] = (lastBank - 1) * 0x2000;
            prgBankOffsets[1] = bank7 * 0x2000;
            prgBankOffsets[2] = bank6 * 0x2000;
            prgBankOffsets[3] = lastBank * 0x2000;
        } else {
            prgBankOffsets[0] = bank6 * 0x2000;
            prgBankOffsets[1] = bank7 * 0x2000;
            prgBankOffsets[2] = (lastBank - 1) * 0x2000;
            prgBankOffsets[3] = lastBank * 0x2000;
        }

        if (cartridge.chrBanks > 0) {
            for (int i = 0; i < 8; i++) {
                chrBankOffsets[i] %= cartridge.chrROM.Length;
            }
        }
        
        for (int i = 0; i < 4; i++) {
            prgBankOffsets[i] %= cartridge.prgROM.Length;
        }
    }

    private class Mapper4State {
        public byte bankSelect; public byte[] bankData = new byte[8]; public bool prgMode, chrMode, prgRamEnable, prgRamWriteProtect; public byte irqLatch, irqCounter; public bool irqEnable, irqReloadPending, irqAsserted;
    }
    public object GetMapperState() {
        return new Mapper4State { bankSelect=this.bankSelect, bankData=(byte[])this.bankData.Clone(), prgMode=this.prgMode, chrMode=this.chrMode, prgRamEnable=this.prgRamEnable, prgRamWriteProtect=this.prgRamWriteProtect, irqLatch=this.irqLatch, irqCounter=this.irqCounter, irqEnable=this.irqEnable, irqReloadPending=this.irqReloadPending, irqAsserted=this.irqAsserted };
    }
    public void SetMapperState(object state) {
        if (state is Mapper4State s) { bankSelect=s.bankSelect; Array.Copy(s.bankData, bankData, 8); prgMode=s.prgMode; chrMode=s.chrMode; prgRamEnable=s.prgRamEnable; prgRamWriteProtect=s.prgRamWriteProtect; irqLatch=s.irqLatch; irqCounter=s.irqCounter; irqEnable=s.irqEnable; irqReloadPending=s.irqReloadPending; irqAsserted=s.irqAsserted; ApplyBankMapping(); return; }
        if (state is System.Text.Json.JsonElement je) {
            try {
                if(je.ValueKind==System.Text.Json.JsonValueKind.Object){
                    if(je.TryGetProperty("bankSelect", out var v)) bankSelect = (byte)v.GetByte();
                    if(je.TryGetProperty("bankData", out var bd) && bd.ValueKind==System.Text.Json.JsonValueKind.Array){ int i=0; foreach(var el in bd.EnumerateArray()){ if(i<8) bankData[i++] = (byte)el.GetByte(); else break; } }
                    if(je.TryGetProperty("prgMode", out var pm)) prgMode = pm.GetBoolean();
                    if(je.TryGetProperty("chrMode", out var cm)) chrMode = cm.GetBoolean();
                    if(je.TryGetProperty("prgRamEnable", out var pre)) prgRamEnable = pre.GetBoolean();
                    if(je.TryGetProperty("prgRamWriteProtect", out var prw)) prgRamWriteProtect = prw.GetBoolean();
                    if(je.TryGetProperty("irqLatch", out var il)) irqLatch = (byte)il.GetByte();
                    if(je.TryGetProperty("irqCounter", out var ic)) irqCounter = (byte)ic.GetByte();
                    if(je.TryGetProperty("irqEnable", out var ie)) irqEnable = ie.GetBoolean();
                    if(je.TryGetProperty("irqReloadPending", out var irp)) irqReloadPending = irp.GetBoolean();
                    if(je.TryGetProperty("irqAsserted", out var ia)) irqAsserted = ia.GetBoolean();
                    ApplyBankMapping();
                }
            } catch { }
        }
    }
    public uint GetChrBankSignature() {
        unchecked {
            uint sig = (uint)(chrMode ? 1 : 0);
            for(int i=0;i<8;i++) {
                sig = (sig * 16777619u) ^ bankData[i];
            }
            return sig;
        }
    }
}
}
