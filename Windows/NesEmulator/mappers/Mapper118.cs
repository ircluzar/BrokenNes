namespace NesEmulator
{
// Mapper 118 (TxSROM: Alien Syndrome, Goal! Two, Pro Sport Hockey, NES Play Action Football): an MMC3
// whose CIRAM A10 comes from bit 7 of the CHR bank registers instead of $A000. In 2KB-first CHR mode
// (bit 7 of $8000 clear) nametables 0/1 follow R0 and 2/3 follow R1; in the inverted mode nametable n
// follows R(2+n). Banking, PRG and the scanline IRQ are the stock MMC3 in the base class (CHR-ROM is
// at most 128KB, so its modulo already drops bit 7); this class only shadows the bank registers and
// answers the per-quadrant CIRAM page through IMapper.GetMmc5NtModeForAddress (0 = CIRAM A, 1 = B),
// which PPU_FIX honours. Mesen 2 TxSRom.
public sealed class Mapper118 : Mapper4, IMapper
{
    private byte sel;
    private readonly byte[] d = new byte[8];

    public Mapper118(Cartridge cart) : base(cart) { }

    public new void Reset() { base.Reset(); sel = 0; System.Array.Clear(d); }

    public new void CPUWrite(ushort address, byte value)
    {
        if (address >= 0x8000 && (address & 0xE001) == 0xA000) return;   // no $A000 mirroring on TxSROM
        base.CPUWrite(address, value);
        if (address < 0x8000) return;
        switch (address & 0xE001) { case 0x8000: sel = value; break; case 0x8001: d[sel & 7] = value; break; }
    }

    public int GetMmc5NtModeForAddress(ushort address)
    {
        if (address < 0x2000 || address >= 0x3F00) return -1;
        int q = (address >> 10) & 3;
        int r = (sel & 0x80) == 0 ? d[q >> 1] : d[2 + q];
        return r >> 7;
    }

    public class Mapper118State { public object? m4; public int sel; public int[] d = System.Array.Empty<int>(); }
    public new object GetMapperState()
    {
        var s = new Mapper118State { m4 = base.GetMapperState(), sel = sel, d = new int[8] };
        for (int i = 0; i < 8; i++) s.d[i] = d[i];
        return s;
    }
    public new void SetMapperState(object state)
    {
        if (state is Mapper118State s)
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
    }
}
}
