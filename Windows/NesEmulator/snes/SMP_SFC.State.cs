using System.IO;

namespace NesEmulator.Snes;

/// <summary>The SPC700's save state: the register file, the PSW, the halted latch and the extra-cycle counter of the instruction in progress (the 64 KB of RAM, the timers and the ports belong to <see cref="APU_SFC"/>).</summary>
public sealed partial class SMP_SFC
{
    private const byte StateVersion = 1;

    public void SaveState(BinaryWriter w)
    {
        SnesStateIo.Begin(w, StateVersion);
        w.Write(A); w.Write(X); w.Write(Y); w.Write(SP); w.Write(PSW); w.Write(PC);
        w.Write(Halted); w.Write(extraCycles);
    }

    public void LoadState(BinaryReader r)
    {
        SnesStateIo.Expect(r, StateVersion, "SPC700");
        A = r.ReadByte(); X = r.ReadByte(); Y = r.ReadByte(); SP = r.ReadByte(); PSW = r.ReadByte(); PC = r.ReadUInt16();
        Halted = r.ReadBoolean(); extraCycles = r.ReadInt32();
    }
}
