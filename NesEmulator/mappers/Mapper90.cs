using System;

namespace NesEmulator
{
// Mapper 90 / 209 / 211 family (implemented for mapper ID 90 primary use).
// Notes:
// - PRG/CHR banking and mirroring follow BizHawk's Mapper090 logic (simplified to core behaviors).
// - IRQ features are stubbed; no global IRQ bus hook exists here. Modes that rely on CPU-cycle IRQs are not supported.
// - Advanced nametable mapping (mapper 211) isn't supported by this emulator's PPU pipeline; only standard mirroring is applied.
public class Mapper90 : IMapper
{
    private readonly Cartridge cart;

    // Registers
    private byte[] prgRegs = new byte[4];
    private int[] chrRegs = new int[8];
    private int[] ntRegs = new int[4]; // currently not used for advanced nametable mapping
    private int[] chrLatches = new int[2]; // MMC2-like latches for mode 1

    // Derived bank offsets (byte offsets inside ROM/RAM)
    private int[] prgBankOffsets = new int[4]; // 4 x 8KB
    private int[] chrBankOffsets = new int[8]; // 8 x 1KB

    // Modes / flags
    private byte prgModeSelect = 0; // 0..7
    private byte chrModeSelect = 0; // 0..3
    private bool sramPrg = false;   // PRG RAM reads map to PRG ROM bank
    private bool mirrorChr = false;
    private bool chrBlockMode = true; // true = block/mask active
    private int chrBlock = 0;
    private int prgBlock = 0;
    private bool ntAdvancedControl = false; // not supported in pipeline, left false
    private bool ntRamDisable = false;      // ignored
    private bool ntRamSelect = false;       // ignored

    // RAM bank used when sramPrg=true
    private int ramBank = 0;

    // Expansion features (multiplication / dips) present in some dumps; no CPU bus route here <0x6000.
    private int multiplicator = 0;
    private int multiplicand = 0;
    private int multiplicationResult = 0;

    // IRQ fields (stubbed)
    private bool irqEnable = false;
    private bool irqPending = false;
    private bool irqCountDown = false;
    private bool irqCountUp = false;
    private int irqPrescalerSize = 256;
    private byte irqSource = 0; // 0=CPU cycles (unsupported), 1=PPU A12, 2=PPU reads
    private byte prescaler = 0;
    private byte irqCounter = 0;
    private byte xorReg = 0;
    private int a12Old = 0;

    public Mapper90(Cartridge c) { cart = c; }

    public void Reset()
    {
    for (int i = 0; i < 4; i++) { prgRegs[i] = 0xFF; ntRegs[i] = 0; }
    for (int i = 0; i < 8; i++) chrRegs[i] = 0xFFFF;
        chrLatches[0] = 0; chrLatches[1] = 4;
    prgModeSelect = 0; chrModeSelect = 0; sramPrg = false;
    mirrorChr = false; chrBlockMode = true; chrBlock = 0; prgBlock = 0;
        ntAdvancedControl = false; ntRamDisable = false; ntRamSelect = false;
        multiplicator = multiplicand = multiplicationResult = 0;
        irqEnable = irqPending = false; irqCountDown = irqCountUp = false; irqPrescalerSize = 256; irqSource = 0; prescaler = 0; irqCounter = 0; xorReg = 0; a12Old = 0;
        RebuildPrgBanks();
        RebuildChrBanks();
        ApplyMirroring(0);
    }

    // CPU space
    public byte CPURead(ushort address)
    {
        if (address >= 0x6000 && address <= 0x7FFF)
        {
            if (sramPrg)
            {
                int idx = (ramBank * 0x2000) + (address & 0x1FFF);
                if ((uint)idx < cart.prgROM.Length) return cart.prgROM[idx];
                return 0xFF;
            }
            else
            {
                return cart.prgRAM[(address - 0x6000) % cart.prgRAM.Length];
            }
        }
        if (address >= 0x8000)
        {
            int bank = (address - 0x8000) / 0x2000; if ((uint)bank > 3) bank = 3;
            int baseOff = prgBankOffsets[bank];
            int final = baseOff + (address & 0x1FFF);
            if ((uint)final < cart.prgROM.Length) return cart.prgROM[final];
            return 0xFF;
        }
        // $4020-$5FFF not routed to mapper in this emulator; return open bus-ish
        return 0x00;
    }

    public void CPUWrite(ushort address, byte value)
    {
        if (address >= 0x6000 && address <= 0x7FFF)
        {
            if (!sramPrg)
            {
                int ramOffset = (address - 0x6000) % cart.prgRAM.Length;
                cart.prgRAM[ramOffset] = value;
            }
            return;
        }

        if (address < 0x8000) return; // not handled by mapper here

        // Decode register by (addr & 0x7007) like BizHawk
        int reg = address & 0x7007;
        switch (reg)
        {
            // 0x8000-0x8007: PRG registers (4 x 8KB entries mirrored)
            case 0x0000: case 0x0001: case 0x0002: case 0x0003:
            case 0x0004: case 0x0005: case 0x0006: case 0x0007:
                prgRegs[address & 3] = (byte)(value & 0x3F);
                RebuildPrgBanks();
                break;

            // 0x9000-0x9007: CHR low bytes
            case 0x1000: case 0x1001: case 0x1002: case 0x1003:
            case 0x1004: case 0x1005: case 0x1006: case 0x1007:
                chrRegs[address & 7] &= 0xFF00;
                chrRegs[address & 7] |= value;
                RebuildChrBanks();
                break;

            // 0xA000-0xA007: CHR high bytes
            case 0x2000: case 0x2001: case 0x2002: case 0x2003:
            case 0x2004: case 0x2005: case 0x2006: case 0x2007:
                chrRegs[address & 7] &= 0x00FF;
                chrRegs[address & 7] |= (value << 8);
                RebuildChrBanks();
                break;

            // 0xB000-0xB007: Nametable regs (ignored for advanced mapping)
            case 0x3000: case 0x3001: case 0x3002: case 0x3003:
                ntRegs[address & 3] &= 0xFF00; ntRegs[address & 3] |= value; break;
            case 0x3004: case 0x3005: case 0x3006: case 0x3007:
                ntRegs[address & 3] &= 0x00FF; ntRegs[address & 3] |= (value << 8); break;

            // 0xC000-0xC007: IRQ control/counters
            case 0x4000:
                // if bit0 set, enable; else disable & ack
                if ((value & 1) != 0) goto case 0x4003; else goto case 0x4002;
            case 0x4001:
                irqCountDown = (value & 0x80) != 0;
                irqCountUp = (value & 0x40) != 0;
                irqPrescalerSize = ((value & 0x04) != 0) ? 8 : 256;
                irqSource = (byte)(value & 0x03);
                break;
            case 0x4002: // ack + disable
                irqPending = false; irqEnable = false; break;
            case 0x4003: // enable
                irqEnable = true; break;
            case 0x4004: // prescaler
                prescaler = (byte)(value ^ xorReg); break;
            case 0x4005: // counter
                irqCounter = (byte)(value ^ xorReg); break;
            case 0x4006: // XOR
                xorReg = value; break;
            case 0x4007: // prescaler adjust (ignored)
                break;

            // 0xD000-0xD007: Banking control and mirroring
            case 0x5000: case 0x5004:
                // bit6: nt_ram_disable; bit7: sram_prg; bits 0-2: prg mode; bits 3-4: chr mode
                ntRamDisable = (value & 0x40) != 0;
                prgModeSelect = (byte)(value & 0x07);
                chrModeSelect = (byte)((value >> 3) & 0x03);
                sramPrg = (value & 0x80) != 0;
                RebuildPrgBanks(); RebuildChrBanks();
                break;
            case 0x5001: case 0x5005:
                ApplyMirroring(value & 0x03);
                break;
            case 0x5002: case 0x5006:
                ntRamSelect = (value & 0x80) != 0; // ignored
                break;
            case 0x5003: case 0x5007:
                mirrorChr = (value & 0x80) != 0;
                chrBlockMode = (value & 0x20) == 0; // 0 = block mode enabled
                chrBlock = ((value & 0x18) >> 2) | (value & 0x01);
                prgBlock = (value & 0x06) >> 1;
                RebuildPrgBanks(); RebuildChrBanks();
                break;
        }
    }

    public byte PPURead(ushort address)
    {
        if (irqSource == 2)
        {
            ClockIRQ(); // poorly used in practice; included for parity
        }

        if (address < 0x2000)
        {
            // CHR read
            int bank = chrBankOffsets[address >> 10]; // 1KB bank index (byte offset)
            int offset = address & 0x03FF;

            // IRQ on PPU A12 rising edge
            int a12 = (address >> 12) & 1;
            bool rising = (a12 == 1 && a12Old == 0);
            if (rising && irqSource == 1) ClockIRQ();
            a12Old = a12;

            int final = bank + offset;
            if (cart.chrBanks > 0)
            {
                if ((uint)final < cart.chrROM.Length) return cart.chrROM[final];
            }
            else
            {
                if ((uint)final < cart.chrRAM.Length) return cart.chrRAM[final];
            }
            return 0;
        }
        return 0;
    }

    public void PPUWrite(ushort address, byte value)
    {
        if (address < 0x2000 && cart.chrBanks == 0)
        {
            int bank = chrBankOffsets[address >> 10];
            int offset = address & 0x03FF;
            int final = bank + offset;
            if ((uint)final < cart.chrRAM.Length) cart.chrRAM[final] = value;
        }

        // Track A12 for IRQ when source==1
        int a12 = (address >> 12) & 1;
        bool rising = (a12 == 1 && a12Old == 0);
        if (rising && irqSource == 1) ClockIRQ();
        a12Old = a12;
    }

    public bool TryCpuToPrgIndex(ushort address, out int prgIndex)
    {
        prgIndex = -1; if (address < 0x8000) return false;
        int bank = (address - 0x8000) / 0x2000; if ((uint)bank > 3) bank = 3;
        int baseOff = prgBankOffsets[bank];
        int final = baseOff + (address & 0x1FFF);
        if ((uint)final < cart.prgROM.Length) { prgIndex = final; return true; }
        return false;
    }

    private static byte BitRev6(int v)
    {
        int n = 0;
        n |= (v & 0x20) >> 5;
        n |= (v & 0x10) >> 3;
        n |= (v & 0x08) >> 1;
        n |= (v & 0x04) << 1;
        n |= (v & 0x02) << 3;
        n |= (v & 0x01) << 5;
        return (byte)n;
    }

    private void SetBanks(int[] target, int offset, int size, int value)
    {
        int prg8kBanks = Math.Max(1, cart.prgROM.Length / 0x2000);
        // Align to 'size' boundary like BizHawk's SetBank (value &= ~(size-1))
        int aligned = value & ~(size - 1);
        for (int i = 0; i < size; i++)
        {
            int idx = offset + i;
            int bank6 = (aligned + i) & 0x3F; // 6-bit within block
            int finalBank = ((prgBlock << 6) | bank6);
            // Fold into actual PRG size
            finalBank %= prg8kBanks;
            target[idx] = finalBank * 0x2000;
        }
    }

    private void RebuildPrgBanks()
    {
        int prg8kBanks = Math.Max(1, cart.prgROM.Length / 0x2000);
        int bankMask6 = (prg8kBanks - 1) & 0x3F;
        int bankMode = prgBlock << 6;
        switch (prgModeSelect)
        {
            case 0:
                // 32KB: map to the last banks within block
                SetBanks(prgBankOffsets, 0, 4, bankMode | bankMask6);
                ramBank = bankMode | (((prgRegs[3] << 2) + 3) & 0x3F);
                break;
            case 1:
                SetBanks(prgBankOffsets, 0, 2, bankMode | (prgRegs[1] & 0x1F));
                SetBanks(prgBankOffsets, 2, 2, bankMode | (bankMask6));
                ramBank = bankMode | (((prgRegs[3] << 1) + 1) & 0x3F);
                break;
            case 2:
                SetBanks(prgBankOffsets, 0, 1, bankMode | prgRegs[0]);
                SetBanks(prgBankOffsets, 1, 1, bankMode | prgRegs[1]);
                SetBanks(prgBankOffsets, 2, 1, bankMode | prgRegs[2]);
                SetBanks(prgBankOffsets, 3, 1, bankMode | bankMask6);
                ramBank = bankMode | prgRegs[3];
                break;
            case 3:
                SetBanks(prgBankOffsets, 0, 1, bankMode | BitRev6(prgRegs[0]));
                SetBanks(prgBankOffsets, 1, 1, bankMode | BitRev6(prgRegs[1]));
                SetBanks(prgBankOffsets, 2, 1, bankMode | BitRev6(prgRegs[2]));
                SetBanks(prgBankOffsets, 3, 1, bankMode | bankMask6);
                ramBank = bankMode | BitRev6(prgRegs[3]);
                break;
            case 4:
                SetBanks(prgBankOffsets, 0, 4, bankMode | (prgRegs[3] & 0x3F));
                ramBank = bankMode | (((prgRegs[3] << 2) + 3) & 0x3F);
                break;
            case 5:
                SetBanks(prgBankOffsets, 0, 2, bankMode | (prgRegs[1] & 0x1F));
                SetBanks(prgBankOffsets, 2, 2, bankMode | (prgRegs[3] & 0x1F));
                ramBank = bankMode | (((prgRegs[3] << 1) + 1) & 0x3F);
                break;
            case 6:
                SetBanks(prgBankOffsets, 0, 1, bankMode | prgRegs[0]);
                SetBanks(prgBankOffsets, 1, 1, bankMode | prgRegs[1]);
                SetBanks(prgBankOffsets, 2, 1, bankMode | prgRegs[2]);
                SetBanks(prgBankOffsets, 3, 1, bankMode | prgRegs[3]);
                ramBank = bankMode | prgRegs[3];
                break;
            case 7:
                SetBanks(prgBankOffsets, 0, 1, bankMode | BitRev6(prgRegs[0]));
                SetBanks(prgBankOffsets, 1, 1, bankMode | BitRev6(prgRegs[1]));
                SetBanks(prgBankOffsets, 2, 1, bankMode | BitRev6(prgRegs[2]));
                SetBanks(prgBankOffsets, 3, 1, bankMode | BitRev6(prgRegs[3]));
                ramBank = bankMode | BitRev6(prgRegs[3]);
                break;
        }
    // Clamp RAM bank to available 8KB PRG banks
    int prg8 = Math.Max(1, cart.prgROM.Length / 0x2000);
    ramBank %= prg8;
    }

    private void RebuildChrBanks()
    {
        // Compute mask/block window
        int mask = 0xFFFF;
        int block = 0;
        if (chrBlockMode)
        {
            mask = 0xFF >> (chrModeSelect ^ 3);
            block = chrBlock << (chrModeSelect + 5);
        }

        int mirror_chr_9002 = mirrorChr ? 0 : 2;
        int mirror_chr_9003 = mirrorChr ? 1 : 3;

        switch (chrModeSelect)
        {
            case 0: // one 8KB bank
            {
                int base1k = ((chrRegs[0] & mask) | block) << 3;
                for (int i = 0; i < 8; i++) chrBankOffsets[i] = Chr1kToOffset(base1k + i);
                break;
            }
            case 1: // two 4KB banks (Mapper090 uses regs[0] and regs[4])
            {
                int reg0 = chrRegs[0];
                int reg4 = chrRegs[4];
                int b0 = ((reg0 & mask) | block) << 2;
                int b4 = ((reg4 & mask) | block) << 2;
                for (int i = 0; i < 4; i++) chrBankOffsets[i] = Chr1kToOffset(b0 + i);
                for (int i = 0; i < 4; i++) chrBankOffsets[4 + i] = Chr1kToOffset(b4 + i);
                break;
            }
            case 2: // four 2KB banks (0,2,4,6 pairs)
            {
                int[] regs = new int[] { chrRegs[0], chrRegs[mirror_chr_9002], chrRegs[4], chrRegs[6] };
                for (int seg = 0; seg < 4; seg++)
                {
                    int base2k = ((regs[seg] & mask) | block) << 1;
                    chrBankOffsets[seg * 2 + 0] = Chr1kToOffset(base2k + 0);
                    chrBankOffsets[seg * 2 + 1] = Chr1kToOffset(base2k + 1);
                }
                break;
            }
            case 3: // eight 1KB banks
            default:
            {
                int[] regs = new int[] { chrRegs[0], chrRegs[1], chrRegs[mirror_chr_9002], chrRegs[mirror_chr_9003], chrRegs[4], chrRegs[5], chrRegs[6], chrRegs[7] };
                for (int i = 0; i < 8; i++)
                {
                    int b = (regs[i] & mask) | block;
                    chrBankOffsets[i] = Chr1kToOffset(b);
                }
                break;
            }
        }
    }

    private int Chr1kToOffset(int bank1k)
    {
        int unitCount1k = (cart.chrBanks > 0 ? cart.chrROM.Length : cart.chrRAM.Length) / 0x400;
        if (unitCount1k <= 0) unitCount1k = 1;
        int b = bank1k % unitCount1k;
        return b * 0x400;
    }

    private void ApplyMirroring(int val)
    {
        switch (val & 0x3)
        {
            case 0: cart.SetMirroring(Mirroring.Vertical); break;
            case 1: cart.SetMirroring(Mirroring.Horizontal); break;
            case 2: cart.SetMirroring(Mirroring.SingleScreenA); break;
            case 3: cart.SetMirroring(Mirroring.SingleScreenB); break;
        }
    }

    private void ClockIRQ()
    {
        int mask = irqPrescalerSize - 1;
        if (irqCountUp && !irqCountDown)
        {
            prescaler++;
            if ((prescaler & mask) == 0)
            {
                irqCounter++;
                if (irqCounter == 0) irqPending = irqEnable;
            }
        }
        else if (irqCountDown && !irqCountUp)
        {
            prescaler--;
            if ((prescaler & mask) == mask)
            {
                irqCounter--;
                if (irqCounter == 0xFF) irqPending = irqEnable;
            }
        }
        // No bus to assert CPU IRQ here; pending flag preserved for save state / debugging.
    }

    // State
    private class Mapper90State
    {
        public byte[] prgRegs = new byte[4]; public int[] chrRegs = new int[8]; public int[] ntRegs = new int[4]; public int[] chrLatches = new int[2];
        public byte prgModeSelect, chrModeSelect; public bool sramPrg, mirrorChr, chrBlockMode; public int chrBlock, prgBlock; public int ramBank;
        public int multiplicator, multiplicand, multiplicationResult; public bool irqEnable, irqPending, irqCountDown, irqCountUp; public int irqPrescalerSize; public byte irqSource, prescaler, irqCounter, xorReg; public int a12Old;
    }
    public object GetMapperState()
    {
        return new Mapper90State
        {
            prgRegs = (byte[])prgRegs.Clone(), chrRegs = (int[])chrRegs.Clone(), ntRegs = (int[])ntRegs.Clone(), chrLatches = (int[])chrLatches.Clone(),
            prgModeSelect = prgModeSelect, chrModeSelect = chrModeSelect, sramPrg = sramPrg, mirrorChr = mirrorChr, chrBlockMode = chrBlockMode, chrBlock = chrBlock, prgBlock = prgBlock, ramBank = ramBank,
            multiplicator = multiplicator, multiplicand = multiplicand, multiplicationResult = multiplicationResult, irqEnable = irqEnable, irqPending = irqPending, irqCountDown = irqCountDown, irqCountUp = irqCountUp, irqPrescalerSize = irqPrescalerSize, irqSource = irqSource, prescaler = prescaler, irqCounter = irqCounter, xorReg = xorReg, a12Old = a12Old
        };
    }
    public void SetMapperState(object state)
    {
        if (state is Mapper90State s)
        {
            Array.Copy(s.prgRegs, prgRegs, 4); Array.Copy(s.chrRegs, chrRegs, 8); Array.Copy(s.ntRegs, ntRegs, 4); Array.Copy(s.chrLatches, chrLatches, 2);
            prgModeSelect = s.prgModeSelect; chrModeSelect = s.chrModeSelect; sramPrg = s.sramPrg; mirrorChr = s.mirrorChr; chrBlockMode = s.chrBlockMode; chrBlock = s.chrBlock; prgBlock = s.prgBlock; ramBank = s.ramBank;
            multiplicator = s.multiplicator; multiplicand = s.multiplicand; multiplicationResult = s.multiplicationResult; irqEnable = s.irqEnable; irqPending = s.irqPending; irqCountDown = s.irqCountDown; irqCountUp = s.irqCountUp; irqPrescalerSize = s.irqPrescalerSize; irqSource = s.irqSource; prescaler = s.prescaler; irqCounter = s.irqCounter; xorReg = s.xorReg; a12Old = s.a12Old;
            RebuildPrgBanks(); RebuildChrBanks(); ApplyMirroring(0); return;
        }
        if (state is System.Text.Json.JsonElement je)
        {
            try
            {
                if (je.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (je.TryGetProperty("prgRegs", out var pr) && pr.ValueKind == System.Text.Json.JsonValueKind.Array) { int i = 0; foreach (var el in pr.EnumerateArray()) { if (i < 4) prgRegs[i++] = (byte)el.GetByte(); else break; } }
                    if (je.TryGetProperty("chrRegs", out var cr) && cr.ValueKind == System.Text.Json.JsonValueKind.Array) { int i = 0; foreach (var el in cr.EnumerateArray()) { if (i < 8) chrRegs[i++] = el.GetInt32(); else break; } }
                    if (je.TryGetProperty("chrModeSelect", out var cms)) chrModeSelect = (byte)cms.GetByte();
                    if (je.TryGetProperty("prgModeSelect", out var pms)) prgModeSelect = (byte)pms.GetByte();
                    sramPrg = je.TryGetProperty("sramPrg", out var sp) && sp.GetBoolean();
                    mirrorChr = je.TryGetProperty("mirrorChr", out var mc) && mc.GetBoolean();
                    chrBlockMode = !(je.TryGetProperty("chrBlockMode", out var cbm) && !cbm.GetBoolean());
                    if (je.TryGetProperty("chrBlock", out var cb)) chrBlock = cb.GetInt32();
                    if (je.TryGetProperty("prgBlock", out var pb)) prgBlock = pb.GetInt32();
                }
            }
            catch { }
            RebuildPrgBanks(); RebuildChrBanks();
        }
    }

    public uint GetChrBankSignature()
    {
        unchecked
        {
            uint sig = (uint)(chrModeSelect & 0x03);
            for (int i = 0; i < 8; i++) sig = (sig * 16777619u) ^ (uint)(chrRegs[i] & 0xFFFF);
            return sig;
        }
    }
}
}
