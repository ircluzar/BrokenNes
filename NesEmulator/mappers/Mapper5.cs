namespace NesEmulator
{
// MMC5 (Mapper 5) implementation adapted from BizHawk ExROM, simplified for this emulator.
// Supported:
//  - $5100 PRG mode (0..3)
//  - $5101 CHR mode (0..3)
//  - $5104 ExRAM mode (stored, limited effect)
//  - $5105 Nametable mapping heuristic -> sets mirroring when pattern matches common modes
//  - $5106/$5107 Fill tile/attrib (stored only)
//  - $5113 WRAM bank select (modulo available PRG RAM)
//  - $5114-$5117 PRG bank numbers (8KB)
//  - $5120-$5127 CHR bank numbers (1KB), with $5130 high bits
//  - $5203/$5204 IRQ target/enable/status and scanline counter (PPU hook added)
//  - $5205/$5206 multiplier
// Partial/Unsupported:
//  - ExRAM-as-NT and extended attributes (PPU owns nametables here)
//  - MMC5 audio ($5010/$5015)
public class Mapper5 : IMapper
{
    private readonly Cartridge cart;

    // Registers
    private int prgMode; // $5100 low 2 bits
    private int chrMode; // $5101 low 2 bits (we use 0-3)
    private byte nametableControl; // $5105 raw value (heuristic mirroring only)
    private int exramMode; // $5104 (0..3)
    private byte fillTile; // $5106
    private byte fillAttrib; // $5107 (low 2 bits replicated)
    private int wramBank; // $5113 (8KB banks)
    private int chrHigh; // $5130 low 2 bits

    // Raw bank register bytes
    private int[] prgRegs = new int[4]; // $5114-$5117
    // MMC5 has separate CHR banks for sprites (A: $5120-$5127) and background (B: $5128-$512B)
    private int[] chrRegsSprite = new int[8]; // A set (always 8 regs)
    private int[] chrRegsBgRaw = new int[4];  // B set raw regs as written ($5128-$512B)

    // Resolved bank offsets (byte offsets inside ROM/RAM)
    private int[] prgSlotOffsets = new int[4]; // each 8KB slot (PRG ROM)
    private int[] prgSlotOffsetsRam = new int[4]; // each 8KB slot (PRG RAM)
    private bool[] prgSlotIsRom = new bool[4]; // per 8KB slot: true=ROM, false=RAM (bit7 of $5114-$5117)
    // Precomputed CHR slot offsets for sprite and background fetches (each 1KB slot view)
    private int[] chrSlotOffsetsSprite = new int[8];
    private int[] chrSlotOffsetsBg = new int[8];

    // Current PPU phase hint (BG vs Sprite) provided by PPUs
    private bool ppuFetchIsSprite;
    // Track whether PPU is in 8x16 sprite height mode (affects A/B CHR bank split semantics)
    private bool objSize16Mode;
    // Track whether rendering is enabled (used for ambiguous/non-pattern CHR accesses)
    private bool renderingOn;
    // $2007 I/O follows the last CHR register set written (A sprite: $5120-$5127, B background: $5128-$512B)
    private bool lastChrIoSprite;

    // EXRAM 1KB (used for attributes or NT by MMC5; here exposed via CPU $5C00-$5FFF)
    private readonly byte[] exram = new byte[1024];
    // Simple 2KB CIRAM proxy for MMC5 NT redirection when PPU asks through mapper (the PPU owns its own VRAM; we only need ExRAM/fill here)
    // We won't mirror CIRAM here; PPU will fall back to its own VRAM for mode 0/1.

    // Track the last nametable tile index address read (coarse tile fetch), for MMC5 Mode 1
    private int lastNtReadIndex = 0; // 0..959 within a single NT (we'll mod by 960)

    // Multiplier
    private byte multiplicand, multiplier; private byte productLo, productHi;

    // IRQ
    private int irqTarget; private int irqCounter; private bool irqEnabled; private bool irqPending; private bool inFrame;

    public Mapper5(Cartridge c) { cart = c; }

    public void Reset()
    {
        prgMode = 3; // default 4x8KB
        chrMode = 3; // default 8x1KB
    for (int i = 0; i < 4; i++) prgRegs[i] = i;
    int prg8kBanks = Math.Max(1, cart.prgROM.Length / 0x2000);
    prgRegs[3] = (0x80 | ((prg8kBanks - 1) & 0x7F)); // last bank fixed to ROM by default
    for (int i = 0; i < 4; i++) prgRegs[i] |= 0x80; // default PRG windows to ROM
    for (int i = 0; i < 8; i++) chrRegsSprite[i] = i;
    for (int i = 0; i < 4; i++) chrRegsBgRaw[i] = i; // BG raw regs default
        nametableControl = 0; exramMode = 0; fillTile = 0; fillAttrib = 0; wramBank = 0; chrHigh = 0;
        multiplicand = multiplier = productLo = productHi = 0;
        irqTarget = irqCounter = 0; irqEnabled = irqPending = inFrame = false;
    ppuFetchIsSprite = false; objSize16Mode = false; renderingOn = false; lastChrIoSprite = false;
    lastNtReadIndex = 0;
        ApplyMirroringFrom5105();
        RebuildPrgMapping();
    RebuildChrMapping();
    }

    // --- CPU ---
    public byte CPURead(ushort address)
    {
        if (address >= 0x5000 && address <= 0x5FFF)
        {
            switch (address)
            {
                case 0x5010: // MMC5 audio control read (IRQ flag)
                    return 0x00;
                case 0x5015: // MMC5 audio status (length flags)
                    return 0x00;
                case 0x5204: // IRQ status: bit7 pending, bit6 in-frame (rendering)
                    {
                        byte v = (byte)((irqPending ? 0x80 : 0) | (inFrame ? 0x40 : 0));
                        irqPending = false; // reading clears pending
                        return v;
                    }
                case 0x5205: return productLo; // multiplier low
                case 0x5206: return productHi; // multiplier high
                // MMC5 audio/status ($5010/$5015) handled in Bus via expansion audio merge
                default:
                    // EXRAM readable at $5C00-$5FFF when mode >=2; otherwise open bus
                    if (address >= 0x5C00)
                    {
                        if (exramMode < 2) return 0xFF;
                        int idx = (address - 0x5C00) & 0x3FF; return exram[idx];
                    }
                    return 0xFF;
            }
        }
        if (address >= 0x6000 && address <= 0x7FFF)
        {
            int banks = Math.Max(1, cart.prgRAM.Length / 0x2000);
            int bank = banks > 0 ? (wramBank % banks + banks) % banks : 0;
            return cart.prgRAM[(bank * 0x2000) + ((address - 0x6000) & 0x1FFF)];
        }
        if (address >= 0x8000)
        {
            int slot = (address - 0x8000) / 0x2000; if ((uint)slot > 3) slot = 3;
            int inner = address & 0x1FFF;
            if (prgSlotIsRom[slot])
            {
                int final = prgSlotOffsets[slot] + inner;
                if ((uint)final < cart.prgROM.Length) return cart.prgROM[final];
            }
            else
            {
                int final = prgSlotOffsetsRam[slot] + inner;
                if ((uint)final < cart.prgRAM.Length) return cart.prgRAM[final];
            }
            return 0xFF;
        }
        return 0;
    }

    public void CPUWrite(ushort address, byte value)
    {
        if (address >= 0x5000 && address <= 0x5FFF)
        {
            switch (address)
            {
                case 0x5100: prgMode = value & 0x03; RebuildPrgMapping(); break;
                case 0x5101: chrMode = value & 0x03; RebuildChrMapping(); break;
                case 0x5102: case 0x5103: /* PRG-RAM Protect A/B (ignored) */ break;
                case 0x5104: exramMode = value & 0x03; break;
                case 0x5105: nametableControl = value; ApplyMirroringFrom5105(); break;
                case 0x5106: fillTile = value; break;
                case 0x5107:
                    fillAttrib = (byte)(value & 0x03);
                    fillAttrib |= (byte)(fillAttrib << 2);
                    fillAttrib |= (byte)(fillAttrib << 4);
                    break;
                case 0x5113: wramBank = value & 0x0F; break; // mask to 4 bits (MMC5 up to 128KB)
                case 0x5114: prgRegs[0] = value & 0xFF; RebuildPrgMapping(); break;
                case 0x5115: prgRegs[1] = value & 0xFF; RebuildPrgMapping(); break;
                case 0x5116: prgRegs[2] = value & 0xFF; RebuildPrgMapping(); break;
                case 0x5117: prgRegs[3] = value & 0xFF; RebuildPrgMapping(); break;
                default:
                    if (address >= 0x5120 && address <= 0x5127)
                    {
                        int idx = address - 0x5120;
                        chrRegsSprite[idx] = (value | (chrHigh << 8)) & 0x3FF;
                        lastChrIoSprite = true;
                        RebuildChrMapping();
                    }
                    else if (address >= 0x5128 && address <= 0x512B)
                    {
                        int idx = address - 0x5128; // 0..3
                        chrRegsBgRaw[idx] = (value | (chrHigh << 8)) & 0x3FF;
                        lastChrIoSprite = false;
                        RebuildChrMapping();
                    }
                    else if (address == 0x5130)
                    {
                        // Theory 2: Do NOT retroactively modify previously-written CHR regs.
                        // Only store high bits; they apply when $5120-$512B are written next.
                        chrHigh = value & 0x03;
                        // No rebuild here; mapping remains as previously written.
                    }
                    else if (address == 0x5203)
                    { irqTarget = value; }
                    else if (address == 0x5204)
                    { irqEnabled = (value & 0x80) != 0; }
                    else if (address == 0x5205)
                    { multiplicand = value; ComputeProduct(); }
                    else if (address == 0x5206)
                    { multiplier = value; ComputeProduct(); }
                    else if (address >= 0x5C00)
                    { if (exramMode != 3) exram[(address - 0x5C00) & 0x3FF] = value; }
                    // $5010-$5015 MMC5 audio not implemented
                    break;
            }
            return;
        }
        if (address >= 0x6000 && address <= 0x7FFF)
        {
            int banks = Math.Max(1, cart.prgRAM.Length / 0x2000);
            int bank = banks > 0 ? (wramBank % banks + banks) % banks : 0;
            cart.prgRAM[(bank * 0x2000) + ((address - 0x6000) & 0x1FFF)] = value; return;
        }
        if (address >= 0x8000)
        {
            int slot = (address - 0x8000) / 0x2000; if ((uint)slot > 3) slot = 3;
            if (!prgSlotIsRom[slot])
            {
                int inner = address & 0x1FFF;
                int final = prgSlotOffsetsRam[slot] + inner;
                if ((uint)final < cart.prgRAM.Length) cart.prgRAM[final] = value;
            }
            return;
        }
    }

    // --- PPU ---
    public byte PPURead(ushort address)
    {
        if (address < 0x2000)
        {
            // MMC5 Mode 1: override BG CHR mapping per tile using ExRAM table
            if (renderingOn && !ppuFetchIsSprite && exramMode == 1)
            {
                // Compute 1KB base offset from ExRAM-selected 4KB bank plus $5130 hi bits
                int bank4k = exram[lastNtReadIndex & 0x3FF] & 0x3F; // 0..63
                int bank = (chrHigh << 6) | bank4k; // include high bits (2)
                int bank1k = (bank << 2) | ((address >> 10) & 0x3); // 1KB segment within 4KB
                int baseOffsetM1 = (bank1k * 0x400);
                int final = baseOffsetM1 + (address & 0x03FF);
                if (cart.chrBanks > 0)
                {
                    int len = cart.chrROM.Length; if (len == 0) return 0;
                    final %= len; if (final < 0) final += len;
                    return cart.chrROM[final];
                }
                else
                {
                    int len = cart.chrRAM.Length; if (len == 0) return 0;
                    final %= len; if (final < 0) final += len;
                    return cart.chrRAM[final];
                }
            }

            int slot = address / 0x0400; if ((uint)slot > 7) slot = 7;
            // Theory 1 + 4:
            // - In 8x8 sprite mode (objSize16 OFF), route both BG and OBJ to A-bank.
            // - When 8x16 is ON and rendering is active, split by phase (sprite vs BG).
            // - When not rendering (CPU $2007 path), use last-written bank set (A when lastChrIoSprite=true).
            int baseOffsetNorm;
            if (!objSize16Mode)
            {
                baseOffsetNorm = chrSlotOffsetsSprite[slot];
            }
            else if (renderingOn)
            {
                baseOffsetNorm = (ppuFetchIsSprite ? chrSlotOffsetsSprite : chrSlotOffsetsBg)[slot];
            }
            else
            {
                baseOffsetNorm = (lastChrIoSprite ? chrSlotOffsetsSprite : chrSlotOffsetsBg)[slot];
            }
            int final2 = baseOffsetNorm + (address & 0x03FF);
            if (cart.chrBanks > 0)
            {
                if ((uint)final2 < cart.chrROM.Length) return cart.chrROM[final2];
            }
            else
            {
                if ((uint)final2 < cart.chrRAM.Length) return cart.chrRAM[final2];
            }
            return 0;
        }
        return 0;
    }

    public void PPUWrite(ushort address, byte value)
    {
        if (address < 0x2000 && cart.chrBanks == 0)
        {
            if (renderingOn && !ppuFetchIsSprite && exramMode == 1)
            {
                int bank4k = exram[lastNtReadIndex & 0x3FF] & 0x3F;
                int bank = (chrHigh << 6) | bank4k;
                int bank1k = (bank << 2) | ((address >> 10) & 0x3);
                int baseOffsetM1 = (bank1k * 0x400);
                int final = baseOffsetM1 + (address & 0x03FF);
                if ((uint)final < cart.chrRAM.Length) cart.chrRAM[final] = value;
                return;
            }
            int slot = address / 0x0400; if ((uint)slot > 7) slot = 7;
            int baseOffsetNorm;
            if (!objSize16Mode)
            {
                baseOffsetNorm = chrSlotOffsetsSprite[slot];
            }
            else if (renderingOn)
            {
                baseOffsetNorm = (ppuFetchIsSprite ? chrSlotOffsetsSprite : chrSlotOffsetsBg)[slot];
            }
            else
            {
                baseOffsetNorm = (lastChrIoSprite ? chrSlotOffsetsSprite : chrSlotOffsetsBg)[slot];
            }
            int final2 = baseOffsetNorm + (address & 0x03FF);
            if ((uint)final2 < cart.chrRAM.Length) cart.chrRAM[final2] = value;
        }
    }

    public bool TryCpuToPrgIndex(ushort address, out int prgIndex)
    {
        prgIndex = -1; if (address < 0x8000) return false;
        int slot = (address - 0x8000) / 0x2000; if ((uint)slot > 3) slot = 3;
    if (!prgSlotIsRom[slot]) return false;
    int baseOffset = prgSlotOffsets[slot];
    int final = baseOffset + (address & 0x1FFF);
    if ((uint)final < cart.prgROM.Length) { prgIndex = final; return true; }
        return false;
    }

    // --- Mapping logic ---
    private void RebuildPrgMapping()
    {
        int prg8kBanks = Math.Max(1, cart.prgROM.Length / 0x2000);
        int ram8kBanks = Math.Max(1, cart.prgRAM.Length / 0x2000);
        void SetSlot(int slotIndex, int regVal)
        {
            bool rom = (regVal & 0x80) != 0;
            int bank = (regVal & 0x7F);
            prgSlotIsRom[slotIndex] = rom;
            if (rom)
                prgSlotOffsets[slotIndex] = ((bank % prg8kBanks) * 0x2000);
            else
                prgSlotOffsetsRam[slotIndex] = ((bank % ram8kBanks) * 0x2000);
        }
        switch (prgMode)
        {
            case 0: // 32KB from $5117 aligned to 32KB
                {
                    int raw = prgRegs[3];
                    int bank = (raw & 0x7F) & ~0x03;
                    for (int i = 0; i < 4; i++) SetSlot(i, (raw & 0x80) | ((bank + i) & 0x7F));
                }
                break;
            case 1: // 2x16KB: first from $5115 aligned to 16KB, second from $5117 aligned to 16KB
                {
                    int raw0 = prgRegs[1];
                    int raw2 = prgRegs[3];
                    int b0 = (raw0 & 0x7F) & ~0x01;
                    int b2 = (raw2 & 0x7F) & ~0x01;
                    SetSlot(0, (raw0 & 0x80) | ((b0 + 0) & 0x7F));
                    SetSlot(1, (raw0 & 0x80) | ((b0 + 1) & 0x7F));
                    SetSlot(2, (raw2 & 0x80) | ((b2 + 0) & 0x7F));
                    SetSlot(3, (raw2 & 0x80) | ((b2 + 1) & 0x7F));
                }
                break;
            case 2: // 16KB + 2x8KB: first 16KB from $5115, then $5116, $5117
                {
                    int raw0 = prgRegs[1];
                    int b0 = (raw0 & 0x7F) & ~0x01;
                    SetSlot(0, (raw0 & 0x80) | ((b0 + 0) & 0x7F));
                    SetSlot(1, (raw0 & 0x80) | ((b0 + 1) & 0x7F));
                    SetSlot(2, prgRegs[2]);
                    SetSlot(3, prgRegs[3]);
                }
                break;
            case 3: // 4x8KB from $5114-$5117
            default:
                for (int i = 0; i < 4; i++) SetSlot(i, prgRegs[i]);
                break;
        }
    }

    private void RebuildChrMapping()
    {
        int unitCount1k = (cart.chrBanks > 0 ? cart.chrROM.Length : cart.chrRAM.Length) / 0x400;
        if (unitCount1k <= 0) unitCount1k = 1;

        // Helper local to compute 8x1KB slot offsets from a provided 8-reg array
        void BuildFromEightRegs(int[] regs, int[] dst)
        {
            switch (chrMode)
            {
                case 0:
                    {
                        int bank = regs[0] & 0x3FF; bank &= ~0x07;
                        for (int i = 0; i < 8; i++) dst[i] = ((bank + i) % unitCount1k) * 0x400;
                    }
                    break;
                case 1:
                    {
                        int b0 = (regs[0] & 0x3FF) & ~0x03;
                        int b4 = (regs[4] & 0x3FF) & ~0x03;
                        for (int i = 0; i < 4; i++) dst[i] = ((b0 + i) % unitCount1k) * 0x400;
                        for (int i = 0; i < 4; i++) dst[4 + i] = ((b4 + i) % unitCount1k) * 0x400;
                    }
                    break;
                case 2:
                    {
                        int[] r = { (regs[0] & 0x3FF) & ~0x01, (regs[2] & 0x3FF) & ~0x01, (regs[4] & 0x3FF) & ~0x01, (regs[6] & 0x3FF) & ~0x01 };
                        for (int seg = 0; seg < 4; seg++)
                            for (int i = 0; i < 2; i++) dst[seg * 2 + i] = ((r[seg] + i) % unitCount1k) * 0x400;
                    }
                    break;
                case 3:
                default:
                    for (int i = 0; i < 8; i++) dst[i] = ((regs[i] & 0x3FF) % unitCount1k) * 0x400;
                    break;
            }
        }

        // Build sprite mapping directly from sprite regs
        BuildFromEightRegs(chrRegsSprite, chrSlotOffsetsSprite);

        // Build background mapping from BG raw regs depending on mode
        // Common case: mode 3 (1KB) used by most games; map pairs: (0,4)=reg0, (1,5)=reg1, (2,6)=reg2, (3,7)=reg3
        if (chrMode == 3)
        {
            int[] tmp = new int[8];
            tmp[0] = chrRegsBgRaw[0]; tmp[4] = chrRegsBgRaw[0];
            tmp[1] = chrRegsBgRaw[1]; tmp[5] = chrRegsBgRaw[1];
            tmp[2] = chrRegsBgRaw[2]; tmp[6] = chrRegsBgRaw[2];
            tmp[3] = chrRegsBgRaw[3]; tmp[7] = chrRegsBgRaw[3];
            BuildFromEightRegs(tmp, chrSlotOffsetsBg);
        }
        else
        {
            // When not in 1KB mode, mirror ExROM's BG register usage nuances.
            // Mode 1 (4KB): BG uses regs_b[3] for both halves.
            // Mode 2 (2KB): BG uses regs_b[1] for first 4KB (two 2KB segments) and regs_b[3] for second 4KB.
            int[] tmp = new int[8];
            if (chrMode == 2)
            {
                // Fill even indices that BuildFromEightRegs consumes in mode 2 (0,2,4,6)
                tmp[0] = chrRegsBgRaw[1]; // slots 0-1
                tmp[2] = chrRegsBgRaw[1]; // slots 2-3
                tmp[4] = chrRegsBgRaw[3]; // slots 4-5
                tmp[6] = chrRegsBgRaw[3]; // slots 6-7
                // Unused odd indices can remain default
            }
            else if (chrMode == 1)
            {
                // BuildFromEightRegs in mode 1 reads indices 0 and 4; set both to reg_b[3]
                tmp[0] = chrRegsBgRaw[3]; // first 4KB
                tmp[4] = chrRegsBgRaw[3]; // second 4KB
            }
            else // chrMode == 0 (8KB)
            {
                // Use reg_b[3] as coarse 8KB selection
                for (int i = 0; i < 8; i++) tmp[i] = chrRegsBgRaw[3];
            }
            BuildFromEightRegs(tmp, chrSlotOffsetsBg);
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

    // Map NT quadrant 0..3 to mode: 0=CIRAM A,1=CIRAM B,2=ExRAM,3=Fill
    private int GetNtModeForAddress(ushort address)
    {
        int ntPage = ((address - 0x2000) >> 10) & 0x3; // which 1KB nametable page
        int mode = (nametableControl >> (ntPage * 2)) & 0x3;
        return mode;
    }

    // Expose per-quadrant NT mode to PPU so it can route CIRAM A/B without guessing mirroring
    public int GetMmc5NtModeForAddress(ushort address)
    {
        if (address < 0x2000 || address >= 0x3000) return -1;
        return GetNtModeForAddress(address);
    }

    public bool TryPpuNametableRead(ushort address, out byte value)
    {
        value = 0;
    if (address < 0x2000 || address >= 0x3F00) return false;
        // Only intercept $2000-$2FFF; attribute and tile regions handled uniformly for ExRAM/fill
        int mode = GetNtModeForAddress(address);
        int inner = address & 0x03FF;
        if (mode == 2)
        {
            // ExRAM as nametable (subject to exramMode: allow reads when mode>=2 similar to CPU path)
            if (exramMode < 2) { value = 0xFF; return true; }
            value = exram[inner & 0x3FF];
            return true;
        }
        if (mode == 3)
        {
            // Fill mode: tiles return $5106, attribute bytes return packed fillAttrib
            if (inner >= 0x3C0) value = fillAttrib; else value = fillTile;
            return true;
        }
        // Mode 0/1: CIRAM A/B — handled by PPU's own VRAM and mirroring logic.
        return false;
    }

    public bool TryPpuNametableWrite(ushort address, byte value)
    {
        if (address < 0x2000 || address >= 0x3F00) return false;
        int mode = GetNtModeForAddress(address);
        int inner = address & 0x03FF;
        if (mode == 2)
        {
            // ExRAM as nametable: allow writes unless exramMode==3 (CPU writes inhibited; we mirror that for PPU NT path too)
            if (exramMode == 3) return true; // drop write
            exram[inner & 0x3FF] = value; return true;
        }
        if (mode == 3)
        {
            // Fill mode ignores writes
            return true;
        }
        return false; // PPU should write to its CIRAM
    }

    // --- State ---
    private class Mapper5State { public int prgMode, chrMode, exramMode, wramBank, chrHigh; public int irqTarget, irqCounter; public bool irqEnabled, irqPending, inFrame; public int[] prgRegs = new int[4]; public int[] chrRegsSprite = new int[8]; public int[] chrRegsBgRaw = new int[4]; public byte nametableControl, fillTile, fillAttrib; public byte[] exram = new byte[1024]; public byte multiplicand, multiplier, productLo, productHi; }
    public object GetMapperState() => new Mapper5State { prgMode = prgMode, chrMode = chrMode, exramMode = exramMode, wramBank = wramBank, chrHigh = chrHigh, irqTarget = irqTarget, irqCounter = irqCounter, irqEnabled = irqEnabled, irqPending = irqPending, inFrame = inFrame, prgRegs = (int[])prgRegs.Clone(), chrRegsSprite = (int[])chrRegsSprite.Clone(), chrRegsBgRaw = (int[])chrRegsBgRaw.Clone(), nametableControl = nametableControl, fillTile = fillTile, fillAttrib = fillAttrib, exram = (byte[])exram.Clone(), multiplicand = multiplicand, multiplier = multiplier, productLo = productLo, productHi = productHi };
    public void SetMapperState(object state)
    {
        if (state is Mapper5State s)
        { prgMode = s.prgMode; chrMode = s.chrMode; exramMode = s.exramMode; wramBank = s.wramBank; chrHigh = s.chrHigh; irqTarget = s.irqTarget; irqCounter = s.irqCounter; irqEnabled = s.irqEnabled; irqPending = s.irqPending; inFrame = s.inFrame; s.prgRegs.CopyTo(prgRegs,0); s.chrRegsSprite.CopyTo(chrRegsSprite,0); s.chrRegsBgRaw.CopyTo(chrRegsBgRaw,0); nametableControl = s.nametableControl; fillTile = s.fillTile; fillAttrib = s.fillAttrib; System.Array.Copy(s.exram, exram, exram.Length); multiplicand = s.multiplicand; multiplier = s.multiplier; productLo = s.productLo; productHi = s.productHi; ApplyMirroringFrom5105(); RebuildPrgMapping(); RebuildChrMapping(); return; }
        if (state is System.Text.Json.JsonElement je)
        {
            try {
                if(je.ValueKind==System.Text.Json.JsonValueKind.Object){
                    if(je.TryGetProperty("prgMode", out var pm)) prgMode = pm.GetInt32();
                    if(je.TryGetProperty("chrMode", out var cm)) chrMode = cm.GetInt32();
                    if(je.TryGetProperty("exramMode", out var xm)) exramMode = xm.GetInt32();
                    if(je.TryGetProperty("wramBank", out var wb)) wramBank = wb.GetInt32();
                    if(je.TryGetProperty("chrHigh", out var ch)) chrHigh = ch.GetInt32();
                    if(je.TryGetProperty("prgRegs", out var pr) && pr.ValueKind==System.Text.Json.JsonValueKind.Array){ int i=0; foreach(var el in pr.EnumerateArray()){ if(i<4) prgRegs[i++] = el.GetInt32(); else break; } }
                    if(je.TryGetProperty("chrRegsSprite", out var crs) && crs.ValueKind==System.Text.Json.JsonValueKind.Array){ int i=0; foreach(var el in crs.EnumerateArray()){ if(i<8) chrRegsSprite[i++] = el.GetInt32(); else break; } }
                    if(je.TryGetProperty("chrRegsBgRaw", out var crr) && crr.ValueKind==System.Text.Json.JsonValueKind.Array){ int i=0; foreach(var el in crr.EnumerateArray()){ if(i<4) chrRegsBgRaw[i++] = el.GetInt32(); else break; } }
                    if(je.TryGetProperty("nametableControl", out var nt)) nametableControl = (byte)nt.GetByte();
                    if(je.TryGetProperty("fillTile", out var ft)) fillTile = (byte)ft.GetByte();
                    if(je.TryGetProperty("fillAttrib", out var fa)) fillAttrib = (byte)fa.GetByte();
                    if(je.TryGetProperty("exram", out var xr) && xr.ValueKind==System.Text.Json.JsonValueKind.Array){ int i=0; foreach(var el in xr.EnumerateArray()){ if(i<exram.Length) exram[i++] = (byte)el.GetInt32(); else break; } }
                    if(je.TryGetProperty("irqTarget", out var it)) irqTarget = it.GetInt32();
                    if(je.TryGetProperty("irqCounter", out var ic)) irqCounter = ic.GetInt32();
                    if(je.TryGetProperty("irqEnabled", out var ie)) irqEnabled = ie.GetBoolean();
                    if(je.TryGetProperty("irqPending", out var ip)) irqPending = ip.GetBoolean();
                    if(je.TryGetProperty("inFrame", out var inf)) inFrame = inf.GetBoolean();
                    if(je.TryGetProperty("multiplicand", out var md)) multiplicand = (byte)md.GetByte();
                    if(je.TryGetProperty("multiplier", out var ml)) multiplier = (byte)ml.GetByte();
                    ComputeProduct();
                    ApplyMirroringFrom5105(); RebuildPrgMapping(); RebuildChrMapping();
                }
            } catch { }
        }
    }
    public uint GetChrBankSignature() {
        unchecked {
            uint sig = (uint)((chrMode & 0x03) | (chrHigh << 8));
            for(int i=0;i<8;i++) sig = (sig * 16777619u) ^ (uint)(chrRegsSprite[i] & 0x3FF);
            for(int i=0;i<4;i++) sig = (sig * 16777619u) ^ (uint)(chrRegsBgRaw[i] & 0x3FF);
            return sig;
        }
    }

    private void ComputeProduct(){ int prod = multiplicand * multiplier; productLo = (byte)(prod & 0xFF); productHi = (byte)((prod >> 8) & 0xFF); }

    // --- PPU hook from core: called when scanlineCycle==3 for visible scanlines
    public void PpuScanlineHook(int scanline, bool renderingEnabled)
    {
        if (!renderingEnabled || scanline >= 241)
        {
            inFrame = false; irqCounter = 0; irqPending = false; return;
        }
        if (!inFrame)
        {
            inFrame = true; irqCounter = 0; irqPending = false; return;
        }
        irqCounter++;
        if (irqCounter == (irqTarget + 1)) irqPending = true;
    }
    public bool IsIrqAsserted() => irqEnabled && irqPending;

    // PPUs tell us whether pattern fetches are for sprites or background.
    public void PpuPhaseHint(bool isSpriteFetch, bool objSize16, bool renderingEnabled)
    {
        // Latch PPU phase and rendering status
        ppuFetchIsSprite = isSpriteFetch;
        objSize16Mode = objSize16;
        renderingOn = renderingEnabled;
    }

    // Expose MMC5 Mode 1 helpers to PPU via IMapper default hooks
    public void PpuNtFetch(ushort ntAddress)
    {
        // ntAddress is in $2000-$2FFF range; compute tile index within the selected nametable
        // Each NT: 32x30 tiles => indices 0..959. Attribute table region ($23C0-$23FF etc.) is not counted here.
        int ntBase = (ntAddress - 0x2000) & 0x03FF; // within 1KB page
        if (ntBase < 0x03C0)
        {
            int index = ntBase; // 0..959
            lastNtReadIndex = index % 960;
        }
    }

    public int GetMmc5Mode1BgPaletteIndex()
    {
        if (!IsMode1Active()) return -1;
        // ExRAM entry selected by last NT read; top 2 bits form attribute/palette select
        int exIndex = lastNtReadIndex & 0x3FF; // safety
        byte ex = exram[exIndex];
        return (ex >> 6) & 0x03; // palette 0..3
    }

    private bool IsMode1Active()
    {
        // Mode 1 when $5104 == 1 and background fetches (not sprites) and rendering is on
        return exramMode == 1 && !ppuFetchIsSprite && renderingOn;
    }

    public bool IsMmc5Mode1BgActive()
    {
        return exramMode == 1 && !ppuFetchIsSprite && renderingOn;
    }
}
}
