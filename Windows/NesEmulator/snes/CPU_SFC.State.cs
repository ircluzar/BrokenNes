using System.IO;

namespace NesEmulator.Snes;

/// <summary>
/// The 65816's save state: the register file, the mode, the stopped / waiting / pending-interrupt latches and the instruction counter, and the operand address register (<c>ea</c>) so that a state taken between two
/// instructions is the machine field for field. The SA-1 carries a second 65816 and saves it through this same code.
/// </summary>
public sealed partial class CPU_SFC
{
    private const byte StateVersion = 1;

    public void SaveState(BinaryWriter w)
    {
        SnesStateIo.Begin(w, StateVersion);
        w.Write(A); w.Write(X); w.Write(Y); w.Write(S); w.Write(D); w.Write(PC);
        w.Write(DBR); w.Write(PBR); w.Write(P); w.Write(E);
        w.Write(Stopped); w.Write(Waiting); w.Write(nmiPending); w.Write(irqLine);
        w.Write(InstructionCount);
        w.Write(ea); w.Write(eaWrap);
    }

    public void LoadState(BinaryReader r)
    {
        SnesStateIo.Expect(r, StateVersion, "65816");
        A = r.ReadUInt16(); X = r.ReadUInt16(); Y = r.ReadUInt16(); S = r.ReadUInt16(); D = r.ReadUInt16(); PC = r.ReadUInt16();
        DBR = r.ReadByte(); PBR = r.ReadByte(); P = r.ReadByte(); E = r.ReadBoolean();
        Stopped = r.ReadBoolean(); Waiting = r.ReadBoolean(); nmiPending = r.ReadBoolean(); irqLine = r.ReadBoolean();
        InstructionCount = r.ReadInt64();
        ea = r.ReadUInt32(); eaWrap = r.ReadBoolean();
    }
}
