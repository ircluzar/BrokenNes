using System;
using System.IO;

namespace NesEmulator.Sega;

public sealed partial class M68k<TBus>
{
    // ---- save state

    private const int StateVersion = 1;

    /// <summary>
    /// Writes the processor's state: the registers and flags, the prefetch queue, the stopped / halted / trace latches, the half-finished address-register update of an access and the counters. The bus is not part of it
    /// (the board saves its own clock), and neither are <see cref="AddressErrors"/> and <see cref="TasWritesBack"/>, which are the board's configuration. The processor is always between instructions when this is called.
    /// </summary>
    public void SaveState(BinaryWriter w)
    {
        w.Write(StateVersion);
        foreach (uint v in D) w.Write(v);
        foreach (uint v in A) w.Write(v);
        w.Write(SpOther); w.Write(Pc);
        w.Write(C); w.Write(V); w.Write(Z); w.Write(N); w.Write(X);
        w.Write(IntMask); w.Write(Supervisor); w.Write(Trace);
        w.Write(Irc); w.Write(Ir); w.Write(Ird); w.Write(frameIr);
        w.Write(Stopped); w.Write(Halted); w.Write(traceDue);
        w.Write(Cycles); w.Write(Instructions); w.Write(pend); w.Write(pendReg); w.Write(pendValue);
        w.Write(AddressErrorCount);
        foreach (long c in ExceptionCounts) w.Write(c);
    }

    /// <summary>Restores what <see cref="SaveState"/> wrote.</summary>
    public void LoadState(BinaryReader r)
    {
        int version = r.ReadInt32();
        if (version != StateVersion) throw new InvalidDataException($"68000 state version {version} is not supported");
        for (int i = 0; i < D.Length; i++) D[i] = r.ReadUInt32();
        for (int i = 0; i < A.Length; i++) A[i] = r.ReadUInt32();
        SpOther = r.ReadUInt32(); Pc = r.ReadUInt32();
        C = r.ReadBoolean(); V = r.ReadBoolean(); Z = r.ReadBoolean(); N = r.ReadBoolean(); X = r.ReadBoolean();
        IntMask = r.ReadInt32(); Supervisor = r.ReadBoolean(); Trace = r.ReadBoolean();
        Irc = r.ReadUInt16(); Ir = r.ReadUInt16(); Ird = r.ReadUInt16(); frameIr = r.ReadUInt16();
        Stopped = r.ReadBoolean(); Halted = r.ReadBoolean(); traceDue = r.ReadBoolean();
        Cycles = r.ReadInt64(); Instructions = r.ReadInt64(); pend = r.ReadInt32(); pendReg = r.ReadInt32(); pendValue = r.ReadUInt32();
        AddressErrorCount = r.ReadInt64();
        for (int i = 0; i < ExceptionCounts.Length; i++) ExceptionCounts[i] = r.ReadInt64();
    }
}
