namespace NesEmulator
{
// Mapper 47 (NES-QJ: Super Spike V'Ball + Nintendo World Cup): two 128KB PRG / 128KB CHR MMC3 games on
// one board. A write to $6000-$7FFF (when the MMC3's PRG-RAM is enabled and not write-protected; the
// board has no PRG-RAM) latches bit 0 as the outer block: PRG 8KB bank = (R & $0F) | block << 4,
// CHR 1KB bank = (R & $7F) | block << 7. The scanline IRQ and mirroring are the stock MMC3 in the base
// class; this class shadows the bank registers and re-implements the PRG/CHR side. Mesen 2 Mapper47.
public sealed class Mapper47 : Mapper4, IMapper
{
    private readonly Cartridge c;
    private byte sel, a001;
    private int block;
    private readonly byte[] d = new byte[8];
    private readonly int[] prgOfs = new int[4];
    private readonly int[] chrOfs = new int[8];

    public Mapper47(Cartridge cart) : base(cart) { c = cart; }

    public new void Reset() { base.Reset(); sel = 0; a001 = 0x80; block = 0; System.Array.Clear(d); Map(); }

    public new void CPUWrite(ushort address, byte value)
    {
        if (address >= 0x6000 && address < 0x8000)
        {
            if ((a001 & 0xC0) == 0x80) { block = value & 1; Map(); }
            return;
        }
        base.CPUWrite(address, value);
        if (address < 0x8000) return;
        switch (address & 0xE001)
        {
            case 0x8000: sel = value; Map(); break;
            case 0x8001: d[sel & 7] = value; Map(); break;
            case 0xA001: a001 = value; break;
        }
    }

    private void Map()
    {
        int pb = block << 4;
        int p6 = (d[6] & 0x0F) | pb, p7 = (d[7] & 0x0F) | pb, pE = 0x0E | pb, pF = 0x0F | pb;
        if ((sel & 0x40) != 0) { prgOfs[0] = pE; prgOfs[1] = p7; prgOfs[2] = p6; prgOfs[3] = pF; }
        else { prgOfs[0] = p6; prgOfs[1] = p7; prgOfs[2] = pE; prgOfs[3] = pF; }
        int prgLen = c.prgROM.Length;
        for (int i = 0; i < 4; i++) prgOfs[i] = (prgOfs[i] * 0x2000) % prgLen;

        int x = (sel & 0x80) != 0 ? 4 : 0;
        Set(0 ^ x, d[0] & 0xFE); Set(1 ^ x, d[0] | 1); Set(2 ^ x, d[1] & 0xFE); Set(3 ^ x, d[1] | 1);
        Set(4 ^ x, d[2]); Set(5 ^ x, d[3]); Set(6 ^ x, d[4]); Set(7 ^ x, d[5]);
    }
    private byte[] Chr => c.chrBanks > 0 ? c.chrROM : c.chrRAM;
    private void Set(int slot, int v) { chrOfs[slot] = (((v & 0x7F) | (block << 7)) * 0x400) % Chr.Length; }

    public new byte CPURead(ushort address)
    {
        if (address >= 0x8000) return c.prgROM[prgOfs[(address - 0x8000) >> 13] + (address & 0x1FFF)];
        return 0;
    }
    public new bool IsCpuReadOpenBus(ushort address) => address < 0x8000;
    public new bool TryCpuToPrgIndex(ushort address, out int prgIndex)
    {
        prgIndex = -1; if (address < 0x8000) return false;
        prgIndex = prgOfs[(address - 0x8000) >> 13] + (address & 0x1FFF); return true;
    }
    public new bool TryPpuToChrIndex(ushort address, out int index)
    {
        index = 0; if (address >= 0x2000) return false;
        index = chrOfs[address >> 10] + (address & 0x3FF); return true;
    }
    public new byte PPURead(ushort address) => address >= 0x2000 ? (byte)0 : Chr[chrOfs[address >> 10] + (address & 0x3FF)];
    public new void PPUWrite(ushort address, byte value)
    {
        if (address < 0x2000 && c.chrBanks == 0) c.chrRAM[chrOfs[address >> 10] + (address & 0x3FF)] = value;
    }

    public class Mapper47State { public object? m4; public int sel, a001, block; public int[] d = System.Array.Empty<int>(); }
    public new object GetMapperState()
    {
        var s = new Mapper47State { m4 = base.GetMapperState(), sel = sel, a001 = a001, block = block, d = new int[8] };
        for (int i = 0; i < 8; i++) s.d[i] = d[i];
        return s;
    }
    public new void SetMapperState(object state)
    {
        if (state is Mapper47State s)
        {
            if (s.m4 != null) base.SetMapperState(s.m4);
            sel = (byte)s.sel; a001 = (byte)s.a001; block = s.block & 1;
            for (int i = 0; i < 8 && i < s.d.Length; i++) d[i] = (byte)s.d[i];
        }
        else if (state is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (je.TryGetProperty("m4", out var m4)) base.SetMapperState(m4);
            if (je.TryGetProperty("sel", out var se)) sel = (byte)se.GetInt32();
            if (je.TryGetProperty("a001", out var aa)) a001 = (byte)aa.GetInt32();
            if (je.TryGetProperty("block", out var bl)) block = bl.GetInt32() & 1;
            if (je.TryGetProperty("d", out var da) && da.ValueKind == System.Text.Json.JsonValueKind.Array) { int i = 0; foreach (var el in da.EnumerateArray()) { if (i >= 8) break; d[i++] = (byte)el.GetInt32(); } }
        }
        Map();
    }
    public new uint GetChrBankSignature() { unchecked { uint h = 2166136261u; for (int i = 0; i < 8; i++) h = (h * 16777619u) ^ (uint)chrOfs[i]; return h; } }
}
}
