namespace NesEmulator
{
// Shared plumbing for the small "discrete logic" boards (Color Dreams, GxROM, NINA, Camerica, Caltron,
// Sunsoft-3/4 ...): PRG in four 8KB slots, CHR in eight 1KB slots, the cartridge's mirroring, optional
// bus conflicts. Each board only decides WHICH banks its registers select (SetPrg32/SetPrg16/SetChr*),
// so the read/write/state/Butchering paths are written once. Banks wrap by modulo, as an undersized ROM
// mirrors on real boards. Ported from the register descriptions Mesen 2 and the NESdev wiki give.
public abstract class DiscreteMapperBase : IMapper
{
    protected readonly Cartridge cart;
    protected readonly Mirroring headerMirroring;
    protected int[] prg8 = new int[4];   // 8KB PRG bank number per $8000/$A000/$C000/$E000 slot
    protected int[] chr1 = new int[8];   // 1KB CHR bank number per $0000..$1C00 slot
    protected Mirroring mirroring;

    protected DiscreteMapperBase(Cartridge c) { cart = c; headerMirroring = c.mirroringMode; }

    protected int Prg8Count => System.Math.Max(1, cart.prgROM.Length / 0x2000);
    protected byte[] Chr => cart.chrBanks > 0 ? cart.chrROM : cart.chrRAM;
    protected int Chr1Count => System.Math.Max(1, Chr.Length / 0x400);
    // Bus conflicts: the ROM drives the data bus during the register write, so the latched value is value AND rom.
    protected virtual bool HasBusConflicts => false;
    protected virtual bool HasPrgRam => true;

    protected void SetPrg32(int bank) { for (int i = 0; i < 4; i++) prg8[i] = bank * 4 + i; }
    protected void SetPrg16(int slot, int bank) { prg8[slot * 2] = bank * 2; prg8[slot * 2 + 1] = bank * 2 + 1; }
    protected void SetChr8(int bank) { for (int i = 0; i < 8; i++) chr1[i] = bank * 8 + i; }
    protected void SetChr2(int slot, int bank) { chr1[slot * 2] = bank * 2; chr1[slot * 2 + 1] = bank * 2 + 1; }
    protected void SetMirror(Mirroring m) { mirroring = m; cart.SetMirroring(m); }
    protected int LastPrg16 => System.Math.Max(1, cart.prgROM.Length / 0x4000) - 1;

    public virtual void Reset()
    {
        SetPrg32(0); SetChr8(0); SetMirror(headerMirroring);
        ResetBoard();
    }
    protected abstract void ResetBoard();
    // A register write. address is $4020-$FFFF (the low range only reaches boards that implement ILowRegisterMapper).
    protected abstract void WriteRegister(ushort address, byte value);

    protected int PrgIndex(ushort address) => (prg8[(address - 0x8000) >> 13] % Prg8Count) * 0x2000 + (address & 0x1FFF);
    protected int ChrIndex(ushort address) => (chr1[address >> 10] % Chr1Count) * 0x400 + (address & 0x3FF);

    public virtual byte CPURead(ushort address)
    {
        if (address >= 0x8000) return cart.prgROM[PrgIndex(address)];
        if (address >= 0x6000 && HasPrgRam) return cart.prgRAM[address - 0x6000];
        return 0;
    }
    public virtual bool IsCpuReadOpenBus(ushort address) => address < 0x6000 || (address < 0x8000 && !HasPrgRam);

    public virtual void CPUWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
        {
            if (HasBusConflicts) value &= cart.prgROM[PrgIndex(address)];
            WriteRegister(address, value);
            return;
        }
        if (address >= 0x6000 && HasPrgRam) { cart.prgRAM[address - 0x6000] = value; }
        WriteLow(address, value);
    }
    // $4020-$7FFF writes (after PRG-RAM). Default: ignored.
    protected virtual void WriteLow(ushort address, byte value) { }

    public virtual byte PPURead(ushort address)
    {
        if (address >= 0x2000) return 0;
        return Chr[ChrIndex(address)];
    }
    public virtual void PPUWrite(ushort address, byte value)
    {
        if (address < 0x2000 && cart.chrBanks == 0) cart.chrRAM[ChrIndex(address)] = value;
    }
    public bool TryPpuToChrIndex(ushort address, out int index)
    {
        index = 0; if (address >= 0x2000) return false;
        index = ChrIndex(address); return true;
    }
    public bool TryCpuToPrgIndex(ushort address, out int prgIndex)
    {
        prgIndex = -1; if (address < 0x8000) return false;
        prgIndex = PrgIndex(address); return true;
    }

    // State: the bank tables + mirroring + whatever the board adds (ExtraState, an int array).
    protected virtual int[] ExtraState { get => System.Array.Empty<int>(); set { } }
    public class DiscreteState { public int[] prg8 = new int[4]; public int[] chr1 = new int[8]; public int mirroring; public int[] extra = System.Array.Empty<int>(); }
    public object GetMapperState() => new DiscreteState { prg8 = (int[])prg8.Clone(), chr1 = (int[])chr1.Clone(), mirroring = (int)mirroring, extra = ExtraState };
    public void SetMapperState(object state)
    {
        if (state is DiscreteState s) { Apply(s.prg8, s.chr1, s.mirroring, s.extra); return; }
        if (state is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            try
            {
                static int[] Ints(System.Text.Json.JsonElement e, string n)
                {
                    if (!e.TryGetProperty(n, out var a) || a.ValueKind != System.Text.Json.JsonValueKind.Array) return System.Array.Empty<int>();
                    var r = new int[a.GetArrayLength()]; int i = 0; foreach (var x in a.EnumerateArray()) r[i++] = x.GetInt32(); return r;
                }
                int m = je.TryGetProperty("mirroring", out var mm) ? mm.GetInt32() : (int)mirroring;
                Apply(Ints(je, "prg8"), Ints(je, "chr1"), m, Ints(je, "extra"));
            }
            catch { }
        }
    }
    private void Apply(int[] p, int[] c, int m, int[] extra)
    {
        if (p.Length == 4) prg8 = (int[])p.Clone();
        if (c.Length == 8) chr1 = (int[])c.Clone();
        SetMirror((Mirroring)m);
        ExtraState = extra ?? System.Array.Empty<int>();
    }
    public uint GetChrBankSignature()
    {
        unchecked { uint sig = 2166136261u; foreach (int b in chr1) sig = (sig * 16777619u) ^ (uint)b; return sig; }
    }
}

// Marker: the board decodes registers in $4020-$5FFF, so Bus forwards writes there (NINA-03/06, Sachen).
// A marker rather than forwarding to every mapper keeps the existing mappers' behaviour (and goldens) untouched.
public interface ILowRegisterMapper { }

// CPU-cycle IRQ boards implement ICpuClockedMapper (declared in RegisterMapperBase.cs).
}
