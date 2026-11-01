namespace NesEmulator.Snes;

/// <summary>
/// The SNES sound unit heard through SNEmulDS's ARM7 DSP (<see cref="SnemulDsDsp"/>, the PocketSPC sound core by gladius, ported
/// from github.com/cotodevel/snemulds - GPL-2.0, a documented exception, see THIRD_PARTY_NOTICES.md section 2). The SPC700, the
/// timers and the stock S-DSP run as in <see cref="APU_SFC"/>, so the game's sound driver reads correct DSP registers; what is heard
/// comes from the SNEmulDS mixer, fed the same DSP register writes. It mixes 63 frames at 32 kHz per Timer2 IRQ and plays 62 of
/// them (the DS channel length), so each 32 kHz S-DSP tick takes the next played frame.
/// Quick port: a state load or a swap re-feeds the engine the DSP registers (re-keying the voices that were keyed on); the engine's
/// own envelope/sample position is not saved.
/// </summary>
public sealed class APU_SNDS : APU_SFC, ISnesApu
{
    public override string CoreName => "SNDS";

    private SnemulDsDsp? ds;                 // created on the first hook (the base constructor runs Reset before this class's fields exist)
    private SnemulDsDsp Ds => ds ??= new SnemulDsDsp(Aram);
    private short[] blockBuf = new short[SnemulDsDsp.MixBufSize * 2];
    private int blockPos = SnemulDsDsp.PlayFrames;   // mix on the first tick

    protected override void OnDspSample(short l, short r)
    {
        if (blockPos >= SnemulDsDsp.PlayFrames)
        {
            Ds.MixSamplesStereo(SnemulDsDsp.MixBufSize, blockBuf);   // 63 mixed, the 63rd never played
            blockPos = 0;
        }
        PushSample(blockBuf[blockPos * 2], blockBuf[blockPos * 2 + 1]);
        blockPos++;
    }

    protected override void OnDspRegWrite(byte reg, byte value) => Ds.Write(reg, value);

    protected override void OnReset()
    {
        Ds.Reset();
        blockBuf = new short[SnemulDsDsp.MixBufSize * 2];
        blockPos = SnemulDsDsp.PlayFrames;
        // replay the S-DSP's registers (everything, then key-on last)
        for (int i = 0; i < 0x80; i++) if (i != 0x4C && i != 0x7C) Ds.Write(i, Dsp.Regs[i]);
        if (Dsp.Regs[0x4C] != 0) Ds.Write(0x4C, Dsp.Regs[0x4C]);
    }
}
