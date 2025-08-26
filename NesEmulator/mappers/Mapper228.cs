namespace NesEmulator
{
// Mapper 228: Active Enterprises Action 52 / Multi-Game (MLT-ACTION52)
// Notes:
// - PRG banking is either 32KB or 16KB (repeated across $8000-$FFFF) depending on prg_mode.
// - CHR selects an 8KB bank using 6 bits composed of address low nibble and data low 2 bits on write.
// - For 1.5MB multicart, the PRG is split across multiple 512KB chips selected by address bits at write time.
public class Mapper228 : IMapper
{
    private readonly Cartridge cart;

    // PRG control
    private bool prgMode16k; // false: 32KB mode, true: 16KB mode mirrored
    private int prgReg;      // 5-bit register from address bits

    // CHR control
    private int chrReg;      // 6-bit bank (8KB)

    // For multicart (> 256KB) PRG is split into multiple 512KB chips
    private int chipOffset; // 0, 0x80000, (0x100000 for chip3); chip2 is open bus (ignored)

    // Masks
    private int chrBankMask8k;
    private int prgBankMask16k;
    private int prgBankMask32k;

    private bool cheetahmen256k; // Special case used by BizHawk when PRG size is exactly 256KB

    public Mapper228(Cartridge c)
    {
        cart = c;
        ConfigureMasks();
    }

    private void ConfigureMasks()
    {
        int chrSpan = cart.chrBanks > 0 ? cart.chrROM.Length : cart.chrRAM.Length; // bytes
        chrBankMask8k = (chrSpan > 0) ? (chrSpan / 0x2000 - 1) : 0;

        // Detect the 256KB variant (Cheetahmen II) where no chip selection is used
        cheetahmen256k = cart.prgROM.Length == 256 * 1024;

        if (cheetahmen256k)
        {
            prgBankMask16k = (cart.prgROM.Length / 0x4000) - 1;
            prgBankMask32k = (cart.prgROM.Length / 0x8000) - 1;
        }
        else
        {
            // Action 52 style: use up to 512KB per chip; 5 bits for 16KB and 4 bits for 32KB
            prgBankMask16k = 0x1F; // 32 x 16KB = 512KB
            prgBankMask32k = 0x0F; // 16 x 32KB = 512KB
        }
    }

    public void Reset()
    {
        prgMode16k = false;
        prgReg = 0;
        chrReg = 0;
        chipOffset = 0;
        // Keep header mirroring until a write configures it
        cart.SetMirroring(cart.mirroringMode);
    }

    public byte CPURead(ushort address)
    {
        if (address < 0x8000) return 0;

        if (!prgMode16k)
        {
            // 32KB mode: select 32KB bank using prgReg >> 1
            int bank32 = ((prgReg >> 1) & prgBankMask32k);
            int index = chipOffset + (bank32 * 0x8000) + (address - 0x8000);
            return (index >= 0 && index < cart.prgROM.Length) ? cart.prgROM[index] : (byte)0xFF;
        }
        else
        {
            // 16KB mode: select 16KB bank; both $8000-$BFFF and $C000-$FFFF map to same bank (mirrored)
            int bank16 = (prgReg & prgBankMask16k);
            int index = chipOffset + (bank16 * 0x4000) + ((address - 0x8000) & 0x3FFF);
            return (index >= 0 && index < cart.prgROM.Length) ? cart.prgROM[index] : (byte)0xFF;
        }
    }

    public void CPUWrite(ushort address, byte value)
    {
        if (address < 0x8000) return;

        // Mirroring: A13 selects H/V
        bool horiz = (address & (1 << 13)) != 0;
        cart.SetMirroring(horiz ? Mirroring.Horizontal : Mirroring.Vertical);

        // PRG mode from A5
        prgMode16k = (address & (1 << 5)) != 0;

        // PRG reg from A6-A10 (5 bits)
        prgReg = (address >> 6) & 0x1F;

        // CHR reg: CCCC from A0-A3 (low nibble of address), low 2 bits from value
        chrReg = ((address & 0x0F) << 2) | (value & 0x03);

        // Chip selection for large multicart: bits A11-A12 choose 512KB region (chip 2 is open bus and ignored)
        if (!cheetahmen256k)
        {
            int chip = (address >> 11) & 0x03; // values 0,1,2,3
            switch (chip)
            {
                case 0: chipOffset = 0x00000; break;
                case 1: chipOffset = 0x80000; break; // +512KB
                case 2: // chip doesn't exist -> open bus. We'll keep previous chipOffset but nothing prevents out-of-range reads; those return 0xFF
                    // Choose an offset that's deliberately out of range to produce 0xFF reads without exceptions
                    chipOffset = cart.prgROM.Length; // force out-of-range
                    break;
                case 3: chipOffset = 0x100000; break; // +1MB
            }
        }
        else
        {
            chipOffset = 0;
        }
    }

    public byte PPURead(ushort address)
    {
        if (address >= 0x2000) return 0;
        int bank8k = chrReg & chrBankMask8k;
        int idx = (bank8k * 0x2000) + address;
        if (cart.chrBanks > 0)
        {
            if (idx >= 0 && idx < cart.chrROM.Length) return cart.chrROM[idx];
            return 0xFF;
        }
        else
        {
            if (idx >= 0 && idx < cart.chrRAM.Length) return cart.chrRAM[idx];
            return 0xFF;
        }
    }

    public void PPUWrite(ushort address, byte value)
    {
        if (address >= 0x2000) return;
        if (cart.chrBanks == 0)
        {
            int bank8k = chrReg & chrBankMask8k;
            int idx = (bank8k * 0x2000) + address;
            if (idx >= 0 && idx < cart.chrRAM.Length) cart.chrRAM[idx] = value;
        }
    }

    public bool TryCpuToPrgIndex(ushort address, out int prgIndex)
    {
        prgIndex = -1; if (address < 0x8000) return false;
        if (!prgMode16k)
        {
            int bank32 = ((prgReg >> 1) & prgBankMask32k);
            int idx = chipOffset + (bank32 * 0x8000) + (address - 0x8000);
            if (idx >= 0 && idx < cart.prgROM.Length) { prgIndex = idx; return true; }
            return false;
        }
        else
        {
            int bank16 = (prgReg & prgBankMask16k);
            int idx = chipOffset + (bank16 * 0x4000) + ((address - 0x8000) & 0x3FFF);
            if (idx >= 0 && idx < cart.prgROM.Length) { prgIndex = idx; return true; }
            return false;
        }
    }

    private class Mapper228State { public bool pm; public int pr; public int cr; public int co; }
    public object GetMapperState() => new Mapper228State { pm = prgMode16k, pr = prgReg, cr = chrReg, co = chipOffset };
    public void SetMapperState(object state)
    {
        if (state is Mapper228State s)
        {
            prgMode16k = s.pm; prgReg = s.pr; chrReg = s.cr; chipOffset = s.co; return;
        }
        if (state is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            try
            {
                if (je.TryGetProperty("pm", out var pm)) prgMode16k = pm.GetBoolean();
                if (je.TryGetProperty("pr", out var pr)) prgReg = pr.GetInt32();
                if (je.TryGetProperty("cr", out var cr)) chrReg = cr.GetInt32();
                if (je.TryGetProperty("co", out var co)) chipOffset = co.GetInt32();
            }
            catch { }
        }
    }

    public uint GetChrBankSignature()
    {
        return (uint)(byte)chrReg;
    }
}
}
