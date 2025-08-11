namespace NesEmulator
{
// Minimal/partial MMC5 (Mapper 5) implementation.
// Focus: basic PRG/CHR bank switching + crude mirroring interpretation.
// Unsupported: scanline IRQ, extended attribute / ExRAM modes, split screen, audio, fill modes.
// Registers implemented (subset):
//  $5100 PRG Mode (supporting only practical mode 3)
//  $5101 CHR Mode (supporting only mode 4 -> eight 1KB banks)
//  $5105 Nametable mapping heuristic
//  $5114-$5117 PRG bank numbers (8KB units)
//  $5120-$5127 CHR bank numbers (1KB units)
// All others ignored.
public class Mapper5 : IMapper
{
    private readonly Cartridge cart;

    // Registers
    private int prgMode; // $5100 low 2 bits
    private int chrMode; // $5101 low 3 bits (we only use 0-3)
    private byte nametableControl; // $5105 raw value (heuristic mirroring only)

    // Raw bank register bytes
    private int[] prgRegs = new int[4]; // $5114-$5117
    private int[] chrRegs = new int[8]; // $5120-$5127

    // Resolved bank offsets (byte offsets inside ROM/RAM)
    private int[] prgSlotOffsets = new int[4]; // each 8KB slot
    private int[] chrSlotOffsets = new int[8]; // each 1KB slot

    public Mapper5(Cartridge c) { cart = c; }

    public void Reset()
    {
        prgMode = 3; // default 4x8KB
        chrMode = 3; // default 8x1KB
        for (int i = 0; i < 4; i++) prgRegs[i] = i;
        int prg8kBanks = Math.Max(1, cart.prgROM.Length / 0x2000);
        prgRegs[3] = prg8kBanks - 1; // last bank fixed (common)
        for (int i = 0; i < 8; i++) chrRegs[i] = i;
        nametableControl = 0;
        ApplyMirroringFrom5105();
        RebuildPrgMapping();
        RebuildChrMapping();
    }

    // --- CPU ---
    public byte CPURead(ushort address)
    {
        if (address >= 0x6000 && address <= 0x7FFF)
            return cart.prgRAM[(address - 0x6000) % cart.prgRAM.Length];
        if (address >= 0x8000)
        {
            int slot = (address - 0x8000) / 0x2000; if ((uint)slot > 3) slot = 3;
            int baseOffset = prgSlotOffsets[slot];
            int final = baseOffset + (address & 0x1FFF);
            if ((uint)final < cart.prgROM.Length) return cart.prgROM[final];
            return 0xFF;
        }
        return 0;
    }

    public void CPUWrite(ushort address, byte value)
    {
        if (address >= 0x6000 && address <= 0x7FFF)
        {
            cart.prgRAM[(address - 0x6000) % cart.prgRAM.Length] = value; return;
        }
        if (address >= 0x5000 && address <= 0x5FFF)
        {
            switch (address)
            {
                case 0x5100:
                    prgMode = value & 0x03; RebuildPrgMapping(); break;
                case 0x5101:
                    chrMode = value & 0x07; // we handle 0-3; others treated as 3
                    if (chrMode > 3) chrMode = 3; RebuildChrMapping(); break;
                case 0x5105:
                    nametableControl = value; ApplyMirroringFrom5105(); break;
                case 0x5114: prgRegs[0] = value; RebuildPrgMapping(); break;
                case 0x5115: prgRegs[1] = value; RebuildPrgMapping(); break;
                case 0x5116: prgRegs[2] = value; RebuildPrgMapping(); break;
                case 0x5117: prgRegs[3] = value; RebuildPrgMapping(); break;
                default:
                    if (address >= 0x5120 && address <= 0x5127)
                    {
                        int idx = address - 0x5120; chrRegs[idx] = value; RebuildChrMapping();
                    }
                    // ignore other MMC5 registers for now
                    break;
            }
        }
    }

    // --- PPU ---
    public byte PPURead(ushort address)
    {
        if (address < 0x2000)
        {
            int slot = address / 0x0400; if ((uint)slot > 7) slot = 7;
            int baseOffset = chrSlotOffsets[slot];
            int final = baseOffset + (address & 0x03FF);
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
            int slot = address / 0x0400; if ((uint)slot > 7) slot = 7;
            int baseOffset = chrSlotOffsets[slot];
            int final = baseOffset + (address & 0x03FF);
            if ((uint)final < cart.chrRAM.Length) cart.chrRAM[final] = value;
        }
    }

    // --- Mapping logic ---
    private void RebuildPrgMapping()
    {
        int prg8kBanks = Math.Max(1, cart.prgROM.Length / 0x2000);
        int lastBank = prg8kBanks - 1;
        switch (prgMode)
        {
            case 0: // 32KB: use $5117 as bank (value selects 8KB unit; align to 4 * 8KB)
                {
                    int bank = prgRegs[3] & 0xFF;
                    bank &= ~0x03; // align to 32KB
                    for (int i = 0; i < 4; i++) prgSlotOffsets[i] = ((bank + i) % prg8kBanks) * 0x2000;
                }
                break;
            case 1: // 16KB switch at $8000 ($5116 supplies even pair), last 16KB fixed
                {
                    int bank16 = prgRegs[2] & 0xFF; bank16 &= ~0x01; // even
                    int b0 = bank16 % prg8kBanks;
                    int b1 = (bank16 + 1) % prg8kBanks;
                    int b2 = (lastBank - 1) & 0xFF; if (b2 < 0) b2 = 0; if (b2 >= prg8kBanks) b2 = prg8kBanks - 2;
                    int b3 = lastBank;
                    prgSlotOffsets[0] = b0 * 0x2000;
                    prgSlotOffsets[1] = b1 * 0x2000;
                    prgSlotOffsets[2] = b2 * 0x2000;
                    prgSlotOffsets[3] = b3 * 0x2000;
                }
                break;
            case 2: // 16KB fixed first, 16KB switch at $C000 ($5116)
                {
                    int bank16 = prgRegs[2] & 0xFF; bank16 &= ~0x01;
                    int b0 = 0;
                    int b1 = 1 % prg8kBanks;
                    int b2 = bank16 % prg8kBanks;
                    int b3 = (bank16 + 1) % prg8kBanks;
                    prgSlotOffsets[0] = b0 * 0x2000;
                    prgSlotOffsets[1] = b1 * 0x2000;
                    prgSlotOffsets[2] = b2 * 0x2000;
                    prgSlotOffsets[3] = b3 * 0x2000;
                }
                break;
            case 3: // 4x8KB fully switchable ($5114-$5117) (last usually fixed by games manually)
            default:
                for (int i = 0; i < 4; i++)
                {
                    int bank = prgRegs[i] % prg8kBanks;
                    prgSlotOffsets[i] = bank * 0x2000;
                }
                break;
        }
    }

    private void RebuildChrMapping()
    {
        int unitCount1k = (cart.chrBanks > 0 ? cart.chrROM.Length : cart.chrRAM.Length) / 0x400;
        if (unitCount1k <= 0) unitCount1k = 1;
        switch (chrMode)
        {
            case 0: // single 8KB bank -> use chrRegs[0] aligned to 8KB
                {
                    int bank = chrRegs[0] & 0xFF;
                    bank &= ~0x07; // align to 8 * 1KB
                    for (int i = 0; i < 8; i++)
                        chrSlotOffsets[i] = ((bank + i) % unitCount1k) * 0x400;
                }
                break;
            case 1: // two 4KB banks: chrRegs[0] and chrRegs[4] (aligned to 4 * 1KB)
                {
                    int b0 = chrRegs[0] & ~0x03;
                    int b4 = chrRegs[4] & ~0x03;
                    for (int i = 0; i < 4; i++) chrSlotOffsets[i] = ((b0 + i) % unitCount1k) * 0x400;
                    for (int i = 0; i < 4; i++) chrSlotOffsets[4 + i] = ((b4 + i) % unitCount1k) * 0x400;
                }
                break;
            case 2: // four 2KB banks: regs 0,2,4,6 (aligned to 2 * 1KB)
                {
                    int[] regs = { chrRegs[0] & ~0x01, chrRegs[2] & ~0x01, chrRegs[4] & ~0x01, chrRegs[6] & ~0x01 };
                    for (int seg = 0; seg < 4; seg++)
                    {
                        for (int i = 0; i < 2; i++)
                            chrSlotOffsets[seg * 2 + i] = ((regs[seg] + i) % unitCount1k) * 0x400;
                    }
                }
                break;
            case 3: // eight 1KB banks
            default:
                for (int i = 0; i < 8; i++)
                    chrSlotOffsets[i] = (chrRegs[i] % unitCount1k) * 0x400;
                break;
        }
    }

    private void ApplyMirroringFrom5105()
    {
        int nt0 = nametableControl & 0x03;
        int nt1 = (nametableControl >> 2) & 0x03;
        int nt2 = (nametableControl >> 4) & 0x03;
        int nt3 = (nametableControl >> 6) & 0x03;
        if (nt0 == nt1 && nt1 == nt2 && nt2 == nt3)
            cart.SetMirroring(nt0 == 0 ? Mirroring.SingleScreenA : Mirroring.SingleScreenB);
        else if (nt0 == 0 && nt1 == 1 && nt2 == 0 && nt3 == 1)
            cart.SetMirroring(Mirroring.Vertical);
        else if (nt0 == 0 && nt1 == 0 && nt2 == 1 && nt3 == 1)
            cart.SetMirroring(Mirroring.Horizontal);
        // else leave existing (MMC5 offers per-NT mapping we don't emulate yet)
    }

    // --- State ---
    private class Mapper5State { public int prgMode, chrMode; public int[] prgRegs = new int[4]; public int[] chrRegs = new int[8]; public byte nametableControl; }
    public object GetMapperState() => new Mapper5State { prgMode = prgMode, chrMode = chrMode, prgRegs = (int[])prgRegs.Clone(), chrRegs = (int[])chrRegs.Clone(), nametableControl = nametableControl };
    public void SetMapperState(object state)
    {
        if (state is Mapper5State s)
        { prgMode = s.prgMode; chrMode = s.chrMode; s.prgRegs.CopyTo(prgRegs,0); s.chrRegs.CopyTo(chrRegs,0); nametableControl = s.nametableControl; ApplyMirroringFrom5105(); RebuildPrgMapping(); RebuildChrMapping(); return; }
        if (state is System.Text.Json.JsonElement je)
        {
            try { var s2 = System.Text.Json.JsonSerializer.Deserialize<Mapper5State>(je.GetRawText(), new System.Text.Json.JsonSerializerOptions{ IncludeFields = true}); if (s2!=null) { prgMode = s2.prgMode; chrMode = s2.chrMode; s2.prgRegs.CopyTo(prgRegs,0); s2.chrRegs.CopyTo(chrRegs,0); nametableControl = s2.nametableControl; ApplyMirroringFrom5105(); RebuildPrgMapping(); RebuildChrMapping(); } } catch {}
        }
    }
}
}
