namespace NesEmulator
{
// Optional mapper capability: boards whose IRQ counter runs off the CPU clock (M2) rather than the
// PPU address bus - Sunsoft-3 (67), Sunsoft FME-7 (69), RAMBO-1's cycle mode (64). Bus.StepAPU clocks
// it with the CPU cycles it steps and forwards IrqLine to the CPU as a level. Kept out of IMapper so
// every other mapper costs Bus nothing but one null test.
public interface ICpuClockedMapper
{
    void CpuClock(int cpuCycles);
    bool IrqLine { get; }
}

// Shared plumbing for the small register-file boards added by the compatibility catalog (P0,
// mappers part B): the whole board state is the int[] `r`, so savestates are one array, and PRG/CHR
// windows are resolved through the 8KB/1KB offset tables the subclass fills in Apply().
public abstract class RegisterMapperBase : IMapper
{
    protected readonly Cartridge cart;
    protected int[] r;                       // the board's registers (subclass defines the meaning)
    protected readonly int[] prg = new int[4];   // byte offset into PRG-ROM of each 8KB window $8000-$FFFF
    protected readonly int[] chr = new int[8];   // byte offset into the CHR chip of each 1KB window $0000-$1FFF

    protected RegisterMapperBase(Cartridge c, int regCount) { cart = c; r = new int[regCount]; }

    protected int PrgLen => cart.prgROM.Length;
    protected bool ChrIsRam => cart.chrBanks == 0;
    protected int ChrLen => ChrIsRam ? cart.chrRAM.Length : cart.chrROM.Length;

    protected void SetPrg8(int slot, int bank8) { int n = System.Math.Max(1, PrgLen / 0x2000); prg[slot] = ((bank8 % n + n) % n) * 0x2000; }
    protected void SetPrg16(int slot16, int bank16) { SetPrg8(slot16 * 2, bank16 * 2); SetPrg8(slot16 * 2 + 1, bank16 * 2 + 1); }
    protected void SetPrg32(int bank32) { for (int i = 0; i < 4; i++) SetPrg8(i, bank32 * 4 + i); }
    protected int LastPrg8 => System.Math.Max(1, PrgLen / 0x2000) - 1;
    protected void SetChr1(int slot, int bank1) { int n = System.Math.Max(1, ChrLen / 0x400); chr[slot] = ((bank1 % n + n) % n) * 0x400; }
    protected void SetChr2(int slot2, int bank2) { SetChr1(slot2 * 2, bank2 * 2); SetChr1(slot2 * 2 + 1, bank2 * 2 + 1); }
    protected void SetChr4(int slot4, int bank4) { for (int i = 0; i < 4; i++) SetChr1(slot4 * 4 + i, bank4 * 4 + i); }
    protected void SetChr8(int bank8) { for (int i = 0; i < 8; i++) SetChr1(i, bank8 * 8 + i); }

    protected abstract void Apply();
    protected virtual void PowerOn() { }

    public virtual void Reset() { System.Array.Clear(r); PowerOn(); Apply(); }

    // $6000-$7FFF: the cartridge's generic 8KB PRG-RAM (battery-backed when the header says so).
    protected virtual bool PrgRamPresent => true;
    protected virtual byte ReadLow(ushort address) => 0;

    public virtual byte CPURead(ushort address)
    {
        if (address >= 0x8000) return cart.prgROM[(prg[(address >> 13) & 3] + (address & 0x1FFF)) % PrgLen];
        if (address >= 0x6000) return PrgRamPresent ? cart.prgRAM[address - 0x6000] : (byte)0;
        return ReadLow(address);
    }
    public virtual bool IsCpuReadOpenBus(ushort address) => address < 0x6000 || (address < 0x8000 && !PrgRamPresent);

    public abstract void CPUWrite(ushort address, byte value);
    protected void WriteRam(ushort address, byte value) { if (PrgRamPresent && address >= 0x6000 && address < 0x8000) cart.prgRAM[address - 0x6000] = value; }

    public bool TryPpuToChrIndex(ushort address, out int index)
    {
        index = 0; if (address >= 0x2000) return false;
        index = (chr[address >> 10] + (address & 0x3FF)) % ChrLen; return true;
    }
    public virtual byte PPURead(ushort address)
    {
        if (address >= 0x2000) return 0;
        int i = (chr[address >> 10] + (address & 0x3FF)) % ChrLen;
        return ChrIsRam ? cart.chrRAM[i] : cart.chrROM[i];
    }
    public virtual void PPUWrite(ushort address, byte value)
    {
        if (address < 0x2000 && ChrIsRam) cart.chrRAM[(chr[address >> 10] + (address & 0x3FF)) % ChrLen] = value;
    }
    public bool TryCpuToPrgIndex(ushort address, out int prgIndex)
    {
        prgIndex = -1; if (address < 0x8000) return false;
        prgIndex = (prg[(address >> 13) & 3] + (address & 0x1FFF)) % PrgLen; return true;
    }

    protected void Mirror(int mode) // 0 vertical, 1 horizontal, 2 single A, 3 single B
        => cart.SetMirroring(mode switch { 0 => Mirroring.Vertical, 1 => Mirroring.Horizontal, 2 => Mirroring.SingleScreenA, _ => Mirroring.SingleScreenB });

    public class RegisterMapperState { public int[] r = System.Array.Empty<int>(); }
    public object GetMapperState() => new RegisterMapperState { r = (int[])r.Clone() };
    public void SetMapperState(object state)
    {
        if (state is RegisterMapperState s) { System.Array.Copy(s.r, r, System.Math.Min(r.Length, s.r.Length)); Apply(); return; }
        if (state is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object && je.TryGetProperty("r", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            int i = 0; foreach (var el in arr.EnumerateArray()) { if (i >= r.Length) break; r[i++] = el.GetInt32(); }
            Apply();
        }
    }
    public uint GetChrBankSignature() { unchecked { uint s = 2166136261u; for (int i = 0; i < 8; i++) s = (s * 16777619u) ^ (uint)chr[i]; return s; } }
}
}
