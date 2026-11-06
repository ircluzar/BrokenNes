// Intel 80386 / 80486 core: save states and the board hooks. Written for BrokenNes (not part of the tiny386 port).

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed partial class I386
{
    /// <summary>Ends the current <see cref="Step"/> batch after the instruction now running (the BIOS uses it to wait for a key).</summary>
    public void EndBatch() => _steps = 1;

    /// <summary>The architectural state, the lazy flags and memory. The TLB and the fetch cache are rebuilt, not saved.</summary>
    public void SaveState(BinaryWriter w)
    {
        foreach (var r in R) w.Write(r);
        w.Write(Ip); w.Write(NextIp); w.Write(Flags); w.Write(Cpl); w.Write(Code16); w.Write(SpMask); w.Write(Halt);
        for (int i = 0; i < 8; i++) { w.Write(SegSel[i]); w.Write(SegBase[i]); w.Write(SegLimit[i]); w.Write(SegFlags[i]); }
        w.Write(IdtBase); w.Write(IdtLimit); w.Write(GdtBase); w.Write(GdtLimit);
        w.Write(Cr0); w.Write(Cr2); w.Write(Cr3);
        foreach (var d in Dr) w.Write(d);
        w.Write(Cycle); w.Write(Excno); w.Write(Excerr); w.Write(Intr);
        w.Write(_ccOp); w.Write(_ccDst); w.Write(_ccDst2); w.Write(_ccSrc1); w.Write(_ccSrc2); w.Write(_ccMask);
        w.Write(MemSize);
        w.Write(Mem, 0, (int)MemSize);
    }

    public void LoadState(BinaryReader r)
    {
        for (int i = 0; i < 8; i++) R[i] = r.ReadUInt32();
        Ip = r.ReadUInt32(); NextIp = r.ReadUInt32(); Flags = r.ReadUInt32(); Cpl = r.ReadInt32(); Code16 = r.ReadBoolean(); SpMask = r.ReadUInt32(); Halt = r.ReadBoolean();
        for (int i = 0; i < 8; i++) { SegSel[i] = r.ReadUInt32(); SegBase[i] = r.ReadUInt32(); SegLimit[i] = r.ReadUInt32(); SegFlags[i] = r.ReadUInt32(); }
        IdtBase = r.ReadUInt32(); IdtLimit = r.ReadUInt32(); GdtBase = r.ReadUInt32(); GdtLimit = r.ReadUInt32();
        Cr0 = r.ReadUInt32(); Cr2 = r.ReadUInt32(); Cr3 = r.ReadUInt32();
        for (int i = 0; i < 8; i++) Dr[i] = r.ReadUInt32();
        Cycle = r.ReadInt64(); Excno = r.ReadInt32(); Excerr = r.ReadUInt32(); Intr = r.ReadBoolean();
        _ccOp = r.ReadInt32(); _ccDst = r.ReadUInt32(); _ccDst2 = r.ReadUInt32(); _ccSrc1 = r.ReadUInt32(); _ccSrc2 = r.ReadUInt32(); _ccMask = r.ReadUInt32();
        uint size = r.ReadUInt32();
        if (size != MemSize) throw new InvalidDataException($"state has {size} bytes of memory, this machine {MemSize}");
        if (r.Read(Mem, 0, (int)size) != size) throw new EndOfStreamException();
        TlbClear();
        _ifLaddr = ulong.MaxValue; _ifPaddr = 0;
    }
}
