using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NesEmulator.Snes;

/// <summary>
/// The audio unit's save state: the 64 KB of RAM, the four ports in each direction (the latches of the boot handshake), the boot-loader switch, the DSP address register, the three timers with their dividers, the SPC700, the S-DSP,
/// the unit's own clock (SPC cycles run since reset, and toward the next DSP sample) and the samples produced but not yet read - only the unread part of the output ring is state; where in the 64K-sample ring they happen to sit is
/// not, so a loaded unit holds them from the start of its ring.
/// </summary>
public sealed partial class APU_SFC
{
    private const byte StateVersion = 1;

    public void SaveState(BinaryWriter w)
    {
        SnesStateIo.Begin(w, StateVersion);
        SnesStateIo.Put(w, Aram);
        SnesStateIo.Put(w, cpuToApu); SnesStateIo.Put(w, apuToCpu);
        w.Write(iplEnabled); w.Write(dspAddr);
        SnesStateIo.Put(w, timerTarget); SnesStateIo.Put(w, timerStage); SnesStateIo.Put(w, timerOut); SnesStateIo.Put(w, timerOn);
        w.Write(timerDiv8k); w.Write(timerDiv64k);
        w.Write(smpCycles); w.Write(dspCycles);
        w.Write(SamplesProduced);
        w.Write(ringCount);
        int first = Math.Min(ringCount, ring.Length - ringRead);
        w.Write(MemoryMarshal.AsBytes(ring.AsSpan(ringRead, first)));
        w.Write(MemoryMarshal.AsBytes(ring.AsSpan(0, ringCount - first)));
        Smp.SaveState(w);
        Dsp.SaveState(w);
    }

    public void LoadState(BinaryReader r)
    {
        SnesStateIo.Expect(r, StateVersion, "audio unit");
        SnesStateIo.Get(r, Aram);
        SnesStateIo.Get(r, cpuToApu); SnesStateIo.Get(r, apuToCpu);
        iplEnabled = r.ReadBoolean(); dspAddr = r.ReadByte();
        SnesStateIo.Get(r, timerTarget); SnesStateIo.Get(r, timerStage); SnesStateIo.Get(r, timerOut); SnesStateIo.Get(r, timerOn);
        timerDiv8k = r.ReadInt32(); timerDiv64k = r.ReadInt32();
        smpCycles = r.ReadInt64(); dspCycles = r.ReadInt32();
        SamplesProduced = r.ReadInt64();
        int n = r.ReadInt32();
        if (n < 0 || n > ring.Length) throw new InvalidDataException($"The audio unit part of the state holds {n} unread samples; the output ring holds {ring.Length}.");
        ringRead = 0; ringCount = n;
        SnesStateIo.Get(r, ring.AsSpan(0, n));
        Smp.LoadState(r);
        Dsp.LoadState(r);
    }
}
