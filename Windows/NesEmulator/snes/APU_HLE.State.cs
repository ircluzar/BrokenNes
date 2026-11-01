using System;
using System.IO;

namespace NesEmulator.Snes;

/// <summary>The silent stand-in unit's save state: its ports, the RAM the game's uploads landed in, and where the boot-loader emulation stands (waiting for the kick, taking bytes, or running).</summary>
public sealed partial class APU_HLE
{
    private const byte StateVersion = 1;

    public void SaveState(BinaryWriter w)
    {
        SnesStateIo.Begin(w, StateVersion);
        SnesStateIo.Put(w, apuToCpu); SnesStateIo.Put(w, cpuToApu);
        SnesStateIo.Put(w, Aram);
        w.Write((int)state); w.Write(index); w.Write(address);
        w.Write(EntryPoint); w.Write(BytesUploaded);
    }

    public void LoadState(BinaryReader r)
    {
        SnesStateIo.Expect(r, StateVersion, "HLE audio unit");
        SnesStateIo.Get(r, apuToCpu); SnesStateIo.Get(r, cpuToApu);
        SnesStateIo.Get(r, Aram);
        int s = r.ReadInt32();
        if (!Enum.IsDefined(typeof(IplState), s)) throw new InvalidDataException($"The HLE audio unit part of the state holds a loader state of {s}.");
        state = (IplState)s; index = r.ReadByte(); address = r.ReadUInt16();
        EntryPoint = r.ReadInt32(); BytesUploaded = r.ReadInt32();
    }
}
