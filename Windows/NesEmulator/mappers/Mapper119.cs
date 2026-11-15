namespace NesEmulator
{
// Mapper 119 (TQROM; High Speed, Pin*Bot): an MMC3 with both CHR-ROM and 8KB CHR-RAM. Bit 6 of a
// CHR bank value picks the RAM (bank & 7) instead of the ROM (bank & $3F). Everything else - PRG,
// mirroring, the scanline IRQ the PPU clocks through Mapper4 - is the stock MMC3 in the base class;
// this class only shadows the bank registers and re-implements the pattern-table side.
public sealed class Mapper119 : Mapper4, IMapper
{
    private readonly Cartridge c;
    private byte sel;
    private readonly byte[] d = new byte[8];
    private readonly int[] chrOfs = new int[8];   // >= 0: CHR-ROM byte offset; < 0: ~(CHR-RAM offset)

    public Mapper119(Cartridge cart) : base(cart) { c = cart; }

    public new void Reset() { base.Reset(); sel = 0; System.Array.Clear(d); Map(); }

    public new void CPUWrite(ushort address, byte value)
    {
        base.CPUWrite(address, value);
        if (address < 0x8000) return;
        switch (address & 0xE001) { case 0x8000: sel = value; Map(); break; case 0x8001: d[sel & 7] = value; Map(); break; }
    }

    private void Map()
    {
        int x = (sel & 0x80) != 0 ? 4 : 0;   // CHR A12 inversion
        Set(0 ^ x, d[0] & 0xFE); Set(1 ^ x, d[0] | 1); Set(2 ^ x, d[1] & 0xFE); Set(3 ^ x, d[1] | 1);
        Set(4 ^ x, d[2]); Set(5 ^ x, d[3]); Set(6 ^ x, d[4]); Set(7 ^ x, d[5]);
    }
    private void Set(int slot, int v)
    {
        if ((v & 0x40) != 0) chrOfs[slot] = ~(((v & 7) * 0x400) % c.chrRAM.Length);
        else chrOfs[slot] = c.chrROM.Length == 0 ? 0 : ((v & 0x3F) * 0x400) % c.chrROM.Length;
    }

    public new bool TryPpuToChrIndex(ushort address, out int index)
    {
        index = 0; if (address >= 0x2000) return false;
        int o = chrOfs[address >> 10]; if (o < 0) return false;   // the RAM half is not the CHR chip
        index = o + (address & 0x3FF); return true;
    }
    public new byte PPURead(ushort address)
    {
        if (address >= 0x2000) return 0;
        int o = chrOfs[address >> 10];
        return o < 0 ? c.chrRAM[~o + (address & 0x3FF)] : c.chrROM[o + (address & 0x3FF)];
    }
    public new void PPUWrite(ushort address, byte value)
    {
        if (address >= 0x2000) return;
        int o = chrOfs[address >> 10];
        if (o < 0) c.chrRAM[~o + (address & 0x3FF)] = value;
    }

    public class Mapper119State { public object? m4; public int sel; public int[] d = System.Array.Empty<int>(); }
    public new object GetMapperState()
    {
        var s = new Mapper119State { m4 = base.GetMapperState(), sel = sel, d = new int[8] };
        for (int i = 0; i < 8; i++) s.d[i] = d[i];
        return s;
    }
    public new void SetMapperState(object state)
    {
        if (state is Mapper119State s)
        {
            if (s.m4 != null) base.SetMapperState(s.m4);
            sel = (byte)s.sel; for (int i = 0; i < 8 && i < s.d.Length; i++) d[i] = (byte)s.d[i];
        }
        else if (state is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (je.TryGetProperty("m4", out var m4)) base.SetMapperState(m4);
            if (je.TryGetProperty("sel", out var se)) sel = (byte)se.GetInt32();
            if (je.TryGetProperty("d", out var da) && da.ValueKind == System.Text.Json.JsonValueKind.Array) { int i = 0; foreach (var el in da.EnumerateArray()) { if (i >= 8) break; d[i++] = (byte)el.GetInt32(); } }
        }
        Map();
    }
    public new uint GetChrBankSignature() { unchecked { uint h = 2166136261u; for (int i = 0; i < 8; i++) h = (h * 16777619u) ^ (uint)chrOfs[i]; return h; } }
}
}
