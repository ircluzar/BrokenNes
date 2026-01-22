namespace NesEmulator
{
// Mapper 9 (MMC2):
// - PRG: switchable 8KB at $8000-$9FFF via $F000 writes; $A000-$FFFF are the last three 8KB banks fixed
// - CHR: two 4KB regions with latch-controlled bank selection triggered by PPU reads
//   Latch A (for $0000-$0FFF):
//     - Read $0FD0-$0FDF -> latchA = 0 (use reg $B000)
//     - Read $0FE0-$0FEF -> latchA = 1 (use reg $C000)
//   Latch B (for $1000-$1FFF):
//     - Read $1FD0-$1FDF -> latchB = 0 (use reg $D000)
//     - Read $1FE0-$1FEF -> latchB = 1 (use reg $E000)
public class Mapper9 : IMapper
{
    private readonly Cartridge cart;

    // PRG (8KB banks)
    private int prgBank; // selected for $8000-$9FFF

    // CHR (4KB banks) - registers
    private int chrA0; // $B000
    private int chrA1; // $C000
    private int chrB0; // $D000
    private int chrB1; // $E000

    // Latches (false = 0/FD, true = 1/FE)
    private bool latchA;
    private bool latchB;

    // Masks
    private int prg8kBankCount;
    private int chr4kBankCount;

    public Mapper9(Cartridge c)
    {
        cart = c;
        prg8kBankCount = (cart.prgROM.Length >> 13); // 8KB banks
        chr4kBankCount = (cart.chrBanks > 0) ? (cart.chrROM.Length >> 12) : 0; // 4KB banks
    }

    public void Reset()
    {
        prgBank = 0;
        chrA0 = chrA1 = chrB0 = chrB1 = 0;
        latchA = false; // power-on default, will be driven by PPU reads
        latchB = false;
    }

    public byte CPURead(ushort address)
    {
        if (address >= 0x6000 && address <= 0x7FFF)
            return cart.prgRAM[address - 0x6000];

        if (address >= 0x8000)
        {
            int bankIndex8k;
            ushort offs = (ushort)(address & 0x1FFF); // within 8KB window
            if (address < 0xA000)
            {
                bankIndex8k = prgBank;
            }
            else if (address < 0xC000)
            {
                bankIndex8k = prg8kBankCount - 3;
            }
            else if (address < 0xE000)
            {
                bankIndex8k = prg8kBankCount - 2;
            }
            else
            {
                bankIndex8k = prg8kBankCount - 1;
            }

            int index = (bankIndex8k << 13) + offs;
            return (index >= 0 && index < cart.prgROM.Length) ? cart.prgROM[index] : (byte)0xFF;
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

        if (address < 0x8000) return;

        // Decode by upper nibble per MMC2 spec
        switch (address & 0xF000)
        {
            case 0xA000: // PRG bank at $8000-$9FFF (8KB)
                {
                    int selectable = prg8kBankCount - 3; // last 3 are fixed
                    if (selectable < 1) selectable = 1;
                    prgBank = value % selectable;
                }
                break;
            case 0xB000: // CHR $0000-$0FFF when latchA=0
                chrA0 = value % (chr4kBankCount == 0 ? 1 : chr4kBankCount);
                break;
            case 0xC000: // CHR $0000-$0FFF when latchA=1
                chrA1 = value % (chr4kBankCount == 0 ? 1 : chr4kBankCount);
                break;
            case 0xD000: // CHR $1000-$1FFF when latchB=0
                chrB0 = value % (chr4kBankCount == 0 ? 1 : chr4kBankCount);
                break;
            case 0xE000: // CHR $1000-$1FFF when latchB=1
                chrB1 = value % (chr4kBankCount == 0 ? 1 : chr4kBankCount);
                break;
            case 0xF000: // Mirroring control
                cart.SetMirroring((value & 1) != 0 ? Mirroring.Horizontal : Mirroring.Vertical);
                break;
            default:
                // $8000-$AFFF not used for registers here
                break;
        }
    }

    public byte PPURead(ushort address)
    {
        if (address >= 0x2000)
            return 0;

        if (cart.chrBanks == 0)
        {
            // CHR RAM: ignore latches, map directly
            return cart.chrRAM[address];
        }

        // Determine bank based on latches and region
        int bank4k;
        if (address < 0x1000)
        {
            bank4k = latchA ? chrA1 : chrA0;
        }
        else
        {
            bank4k = latchB ? chrB1 : chrB0;
        }
        int idx = (bank4k << 12) + (address & 0x0FFF);
        byte ret = (idx >= 0 && idx < cart.chrROM.Length) ? cart.chrROM[idx] : (byte)0;

        // Update latches AFTER fetching, matching hardware behavior
        if (address >= 0x0FD0 && address <= 0x0FDF)
            latchA = false;
        else if (address >= 0x0FE0 && address <= 0x0FEF)
            latchA = true;
        else if (address >= 0x1FD0 && address <= 0x1FDF)
            latchB = false;
        else if (address >= 0x1FE0 && address <= 0x1FEF)
            latchB = true;

        return ret;
    }

    public void PPUWrite(ushort address, byte value)
    {
        if (address < 0x2000 && cart.chrBanks == 0)
        {
            cart.chrRAM[address] = value;
        }
    }

    public bool TryCpuToPrgIndex(ushort address, out int prgIndex)
    {
        prgIndex = -1;
        if (address < 0x8000) return false;

        int bankIndex8k;
        int offs = address & 0x1FFF;
        if (address < 0xA000)
            bankIndex8k = prgBank;
        else if (address < 0xC000)
            bankIndex8k = prg8kBankCount - 3;
        else if (address < 0xE000)
            bankIndex8k = prg8kBankCount - 2;
        else
            bankIndex8k = prg8kBankCount - 1;

        int index = (bankIndex8k << 13) + offs;
        if (index >= 0 && index < cart.prgROM.Length)
        { prgIndex = index; return true; }
        return false;
    }

    private class Mapper9State
    {
        public int prgBank;
        public int chrA0, chrA1, chrB0, chrB1;
        public bool latchA, latchB;
    }

    public object GetMapperState() => new Mapper9State
    {
        prgBank = prgBank,
        chrA0 = chrA0, chrA1 = chrA1, chrB0 = chrB0, chrB1 = chrB1,
        latchA = latchA, latchB = latchB
    };

    public void SetMapperState(object state)
    {
        if (state is Mapper9State s)
        {
            prgBank = s.prgBank;
            chrA0 = s.chrA0; chrA1 = s.chrA1; chrB0 = s.chrB0; chrB1 = s.chrB1;
            latchA = s.latchA; latchB = s.latchB; return;
        }
        if (state is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            try
            {
                if (je.TryGetProperty("prgBank", out var p)) prgBank = p.GetInt32();
                if (je.TryGetProperty("chrA0", out var a0)) chrA0 = a0.GetInt32();
                if (je.TryGetProperty("chrA1", out var a1)) chrA1 = a1.GetInt32();
                if (je.TryGetProperty("chrB0", out var b0)) chrB0 = b0.GetInt32();
                if (je.TryGetProperty("chrB1", out var b1)) chrB1 = b1.GetInt32();
                if (je.TryGetProperty("latchA", out var la)) latchA = la.GetBoolean();
                if (je.TryGetProperty("latchB", out var lb)) latchB = lb.GetBoolean();
            }
            catch { }
        }
    }

    public uint GetChrBankSignature()
    {
        int a = latchA ? chrA1 : chrA0;
        int b = latchB ? chrB1 : chrB0;
        return (uint)((a & 0xFFFF) << 16 | (b & 0xFFFF));
    }
}
}
