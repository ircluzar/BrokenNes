using System.IO;

namespace NesEmulator.Snes;

/// <summary>
/// The S-DSP's save state: the 128 registers, each of the eight voices (the twelve decoded BRR samples it interpolates over, its position in the sample and in the BRR block, its key-on delay, envelope and envelope mode),
/// the key-on / key-off latches, the noise generator and the global rate counter (with the phase table derived from it, kept so that a loaded DSP is the saved one field for field), and the echo unit (position, length, the eight-tap
/// history and the last echo output). The 64 KB of RAM the DSP reads its samples from and writes its echo into is the audio unit's (<see cref="APU_SFC"/>).
/// </summary>
public sealed partial class DSP_SFC
{
    private const byte StateVersion = 1;

    public void SaveState(BinaryWriter w)
    {
        SnesStateIo.Begin(w, StateVersion);
        SnesStateIo.Put(w, Regs);
        foreach (var v in voices)
        {
            SnesStateIo.Put(w, v.Buf);
            w.Write(v.BufPos); w.Write(v.InterpPos); w.Write(v.BrrAddr); w.Write(v.BrrOffset); w.Write(v.KonDelay); w.Write(v.Env); w.Write(v.HiddenEnv);
            w.Write((int)v.Mode);
        }
        w.Write(newKon); w.Write(kon); w.Write(koff); w.Write(everyOther); w.Write(counter); w.Write(noise);
        w.Write(echoOffset); w.Write(echoLength); w.Write(echoHistPos);
        for (int i = 0; i < 16; i++) { w.Write(echoHist[i, 0]); w.Write(echoHist[i, 1]); }
        w.Write(echoOutL); w.Write(echoOutR);
        SnesStateIo.Put(w, ratePhase);
    }

    public void LoadState(BinaryReader r)
    {
        SnesStateIo.Expect(r, StateVersion, "S-DSP");
        SnesStateIo.Get(r, Regs);
        foreach (var v in voices)
        {
            SnesStateIo.Get(r, v.Buf);
            v.BufPos = r.ReadInt32(); v.InterpPos = r.ReadInt32(); v.BrrAddr = r.ReadInt32(); v.BrrOffset = r.ReadInt32(); v.KonDelay = r.ReadInt32(); v.Env = r.ReadInt32(); v.HiddenEnv = r.ReadInt32();
            int mode = r.ReadInt32();
            if (mode is < 0 or > 3) throw new InvalidDataException($"The S-DSP part of the state holds an envelope mode of {mode}.");
            v.Mode = (EnvMode)mode;
        }
        newKon = r.ReadByte(); kon = r.ReadByte(); koff = r.ReadByte(); everyOther = r.ReadBoolean(); counter = r.ReadInt32(); noise = r.ReadInt32();
        echoOffset = r.ReadInt32(); echoLength = r.ReadInt32(); echoHistPos = r.ReadInt32();
        for (int i = 0; i < 16; i++) { echoHist[i, 0] = r.ReadInt32(); echoHist[i, 1] = r.ReadInt32(); }
        echoOutL = r.ReadInt32(); echoOutR = r.ReadInt32();
        SnesStateIo.Get(r, ratePhase);
    }
}
