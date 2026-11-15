namespace NesEmulator
{
// Mapper 64 (Tengen RAMBO-1; Klax, Shinobi, Skull & Crossbones, Hard Drivin'). An MMC3 relative:
//   $8000 even: bits 0-3 register index, bit 5 K (1KB CHR mode for R0/R1), bit 6 PRG mode, bit 7 CHR invert
//   $8001 odd : register data. R0-R5 CHR, R6/R7 PRG, R8/R9 the extra 1KB CHR banks of K mode, RF third PRG bank
//   $A000 even: mirroring (0 V, 1 H)
//   $C000 even: IRQ latch; $C001 odd: bit 0 = count CPU cycles /4 instead of scanlines, and reload
//   $E000 even: IRQ disable + acknowledge; $E001 odd: IRQ enable
// The counter follows Mesen 2's Rambo1 (the reload quirks Hard Drivin' needs, and the IRQ raised a
// couple of CPU cycles after the counter hits 0). Subclassing Mapper4 is only so the PPUs' existing
// MMC3 hook clocks the scanline counter; the IRQ itself goes out as a level through ICpuClockedMapper.
public sealed class Mapper64 : Mapper4, IMapper, ICpuClockedMapper
{
    private readonly Cartridge c;
    // r[0..15] bank registers, then: 16 sel, 17 mirroring, 18 latch, 19 counter, 20 reload, 21 cycle mode,
    // 22 enabled, 23 cpu prescaler, 24 irq delay, 25 irq line
    private readonly int[] r = new int[26];
    private readonly int[] prgOfs = new int[4];
    private readonly int[] chrOfs = new int[8];

    public Mapper64(Cartridge cart) : base(cart) { c = cart; }

    public new void Reset() { base.Reset(); System.Array.Clear(r); r[17] = c.mirroringMode == Mirroring.Vertical ? 0 : 1; Map(); }

    private int Prg8(int b) { int n = System.Math.Max(1, c.prgROM.Length / 0x2000); return ((b % n + n) % n) * 0x2000; }
    private int Chr1(int b) { int len = c.chrBanks > 0 ? c.chrROM.Length : c.chrRAM.Length; int n = System.Math.Max(1, len / 0x400); return ((b % n + n) % n) * 0x400; }

    private void Map()
    {
        int sel = r[16]; bool k = (sel & 0x20) != 0; int x = (sel & 0x80) != 0 ? 4 : 0;
        if (k) { chrOfs[0 ^ x] = Chr1(r[0]); chrOfs[1 ^ x] = Chr1(r[8]); chrOfs[2 ^ x] = Chr1(r[1]); chrOfs[3 ^ x] = Chr1(r[9]); }
        else { chrOfs[0 ^ x] = Chr1(r[0] & 0xFE); chrOfs[1 ^ x] = Chr1(r[0] | 1); chrOfs[2 ^ x] = Chr1(r[1] & 0xFE); chrOfs[3 ^ x] = Chr1(r[1] | 1); }
        chrOfs[4 ^ x] = Chr1(r[2]); chrOfs[5 ^ x] = Chr1(r[3]); chrOfs[6 ^ x] = Chr1(r[4]); chrOfs[7 ^ x] = Chr1(r[5]);
        if ((sel & 0x40) != 0) { prgOfs[0] = Prg8(r[15]); prgOfs[1] = Prg8(r[6]); prgOfs[2] = Prg8(r[7]); }
        else { prgOfs[0] = Prg8(r[6]); prgOfs[1] = Prg8(r[7]); prgOfs[2] = Prg8(r[15]); }
        prgOfs[3] = Prg8(-1);
        c.SetMirroring(r[17] == 0 ? Mirroring.Vertical : Mirroring.Horizontal);
    }

    public new byte CPURead(ushort address)
    {
        if (address >= 0x8000) return c.prgROM[prgOfs[(address >> 13) & 3] + (address & 0x1FFF)];
        return 0;
    }
    public bool IsCpuReadOpenBus(ushort address) => address < 0x8000;
    public new bool TryCpuToPrgIndex(ushort address, out int prgIndex)
    {
        prgIndex = -1; if (address < 0x8000) return false;
        prgIndex = prgOfs[(address >> 13) & 3] + (address & 0x1FFF); return true;
    }

    public new void CPUWrite(ushort address, byte value)
    {
        if (address < 0x8000) return;
        switch (address & 0xE001)
        {
            case 0x8000: r[16] = value; Map(); break;
            case 0x8001: r[r[16] & 0x0F] = value; Map(); break;
            case 0xA000: r[17] = value & 1; Map(); break;
            case 0xC000: r[18] = value; break;
            case 0xC001: r[21] = value & 1; if (r[21] != 0) r[23] = 0; r[20] = 1; break;
            case 0xE000: r[22] = 0; r[25] = 0; r[24] = 0; break;
            case 0xE001: r[22] = 1; break;
        }
    }

    private void ClockCounter(int delay)
    {
        // Mesen 2's Rambo1 reload rule (its "Hard Drivin'" fix). Klax matches Mesen frame for frame with it; Skull & Crossbones'
        // bottom-border split then lands one scanline below Mesen's (row 226 vs 225) - open, see the P0 mappersB report.
        if (r[20] != 0) { r[19] = r[18] <= 1 ? r[18] + 1 : r[18] + 2; r[20] = 0; }
        else if (r[19] == 0) r[19] = r[18] + 1;
        r[19] = (r[19] - 1) & 0xFF;
        if (r[19] == 0 && r[22] != 0) r[24] = delay;
    }

    // The PPUs' MMC3 scanline hook (A12 rise): RAMBO-1 counts it only in scanline mode.
    public override void ClockScanlineCounterHardware() { if (r[21] == 0) ClockCounter(2); }
    public override void RunScanlineIRQ() { if (r[21] == 0) ClockCounter(2); }
    public override bool IRQPending() => false;   // raised through IrqLine instead
    public override void ClearIRQ() { }

    public void CpuClock(int cpuCycles)
    {
        for (int i = 0; i < cpuCycles; i++)
        {
            if (r[24] > 0 && --r[24] == 0) r[25] = 1;
            if (r[21] != 0) { r[23] = (r[23] + 1) & 3; if (r[23] == 0) ClockCounter(1); }
        }
    }
    public bool IrqLine => r[25] != 0;

    public new bool TryPpuToChrIndex(ushort address, out int index)
    {
        index = 0; if (address >= 0x2000) return false;
        index = chrOfs[address >> 10] + (address & 0x3FF); return true;
    }
    public new byte PPURead(ushort address)
    {
        if (address >= 0x2000) return 0;
        int i = chrOfs[address >> 10] + (address & 0x3FF);
        return c.chrBanks > 0 ? c.chrROM[i] : c.chrRAM[i];
    }
    public new void PPUWrite(ushort address, byte value)
    {
        if (address < 0x2000 && c.chrBanks == 0) c.chrRAM[chrOfs[address >> 10] + (address & 0x3FF)] = value;
    }

    public class Mapper64State { public int[] r = System.Array.Empty<int>(); }
    public new object GetMapperState() => new Mapper64State { r = (int[])r.Clone() };
    public new void SetMapperState(object state)
    {
        if (state is Mapper64State s) System.Array.Copy(s.r, r, System.Math.Min(r.Length, s.r.Length));
        else if (state is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object && je.TryGetProperty("r", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Array)
        { int i = 0; foreach (var el in a.EnumerateArray()) { if (i >= r.Length) break; r[i++] = el.GetInt32(); } }
        Map();
    }
    public new uint GetChrBankSignature() { unchecked { uint h = 2166136261u; for (int i = 0; i < 8; i++) h = (h * 16777619u) ^ (uint)chrOfs[i]; return h; } }
}
}
