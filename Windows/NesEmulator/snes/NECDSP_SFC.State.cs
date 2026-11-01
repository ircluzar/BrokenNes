using System.IO;

namespace NesEmulator.Snes;

/// <summary>
/// The firmware-driven DSP-1's save state (the µPD77C25): the data RAM, the four-level stack, the program counter, the pointer registers, both accumulators with their flag sets, the multiplier and its operands, the data
/// and status registers the SNES talks to, the instruction clock and the counters. The firmware itself (program and data ROM) is the chip's identity, not its state: <see cref="StateId"/> carries a hash of it, so a state saved
/// with one firmware image is refused by a session running another (or the homemade chip).
/// </summary>
public sealed partial class NECDSP_SFC
{
    private const byte StateVersion = 1;

    /// <summary>The name and a hash of the firmware image (FNV-1a over the program words and the data ROM).</summary>
    public string StateId
    {
        get
        {
            uint h = 2166136261;
            foreach (uint w in program) { h = (h ^ w) * 16777619; }
            foreach (ushort d in dataRom) { h = (h ^ d) * 16777619; }
            return $"{Name} firmware {h:X8}";
        }
    }

    public void SaveState(BinaryWriter w)
    {
        SnesStateIo.Begin(w, StateVersion);
        SnesStateIo.Put(w, dataRam); SnesStateIo.Put(w, stack);
        w.Write(pc); w.Write(sp); w.Write(rp); w.Write(dp);
        w.Write(a); w.Write(b); w.Write(tr); w.Write(trb); w.Write(dr); w.Write(sr); w.Write(k); w.Write(l); w.Write(m); w.Write(n); w.Write(so);
        WriteFlags(w, fa); WriteFlags(w, fb);
        w.Write(dspClock); w.Write(lastMaster); w.Write(Instructions);
    }

    public void LoadState(BinaryReader r)
    {
        SnesStateIo.Expect(r, StateVersion, "µPD77C25");
        SnesStateIo.Get(r, dataRam); SnesStateIo.Get(r, stack);
        pc = r.ReadInt32(); sp = r.ReadInt32(); rp = r.ReadInt32(); dp = r.ReadInt32();
        a = r.ReadUInt16(); b = r.ReadUInt16(); tr = r.ReadUInt16(); trb = r.ReadUInt16(); dr = r.ReadUInt16(); sr = r.ReadUInt16(); k = r.ReadUInt16(); l = r.ReadUInt16(); m = r.ReadUInt16(); n = r.ReadUInt16(); so = r.ReadUInt16();
        fa = ReadFlags(r); fb = ReadFlags(r);
        dspClock = r.ReadInt64(); lastMaster = r.ReadInt64(); Instructions = r.ReadInt64();
        if (pc < 0 || pc >= program.Length || sp < 0 || sp >= stack.Length || rp < 0 || rp > 0x3FF || dp < 0 || dp > 0xFF)
            throw new InvalidDataException("The µPD77C25 part of the state holds a pointer outside the chip's memories.");
    }

    private static void WriteFlags(BinaryWriter w, Flags f) { w.Write(f.Ov0); w.Write(f.Ov1); w.Write(f.Z); w.Write(f.C); w.Write(f.S0); w.Write(f.S1); }
    private static Flags ReadFlags(BinaryReader r) => new() { Ov0 = r.ReadBoolean(), Ov1 = r.ReadBoolean(), Z = r.ReadBoolean(), C = r.ReadBoolean(), S0 = r.ReadBoolean(), S1 = r.ReadBoolean() };
}
