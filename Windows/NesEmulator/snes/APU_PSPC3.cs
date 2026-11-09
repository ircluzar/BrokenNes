namespace NesEmulator.Snes;

/// <summary>
/// The SNES sound unit heard through PocketSPC 0.3's ARM7 DSP (<see cref="PocketSpc03Dsp"/>, gladius's Nintendo DS SPC player,
/// GameBrew https://www.gamebrew.org/wiki/PocketSPC, licence "Mixed": a documented exception, see THIRD_PARTY_NOTICES.md section 2a).
/// The SPC700, the timers and the stock S-DSP run as in <see cref="APU_SFC"/>, so the game's sound driver reads correct DSP
/// registers; what is heard comes from the PocketSPC mixer, fed the same DSP register writes. It mixes 8-frame blocks at
/// 24 kHz (one per Timer1 IRQ), played at the DS's 23972.8 Hz; each 32 kHz S-DSP tick takes the current frame (zero-order hold).
/// Quick port: a state load or a swap re-feeds the engine the DSP registers (re-keying the voices that were keyed on); the engine's
/// own envelope/sample position is not saved.
/// </summary>
public sealed class APU_PSPC3 : APU_SFC, ISnesApu, IPortedEngineState
{
    public override string CoreName => "PSPC3";

    private PocketSpc03Dsp? ps;              // created on the first hook (the base constructor runs Reset before this class's fields exist)
    private PocketSpc03Dsp Ps => ps ??= new PocketSpc03Dsp(Aram);
    private short[] blockBuf = new short[PocketSpc03Dsp.MixBufSize * 2];
    private int blockPos = PocketSpc03Dsp.MixBufSize;   // mix on the first tick
    private int phase;

    protected override void OnDspSample(short l, short r)
    {
        phase += PocketSpc03Dsp.PlayRateX10;
        while (phase >= 320000) { phase -= 320000; blockPos++; }
        while (blockPos >= PocketSpc03Dsp.MixBufSize)
        {
            Ps.MixSamplesStereo(blockBuf);
            blockPos -= PocketSpc03Dsp.MixBufSize;
        }
        PushSample(blockBuf[blockPos * 2], blockBuf[blockPos * 2 + 1]);
    }

    protected override void OnDspRegWrite(byte reg, byte value) => Ps.Write(reg, value);

    protected override void OnReset()
    {
        Ps.Reset();
        blockBuf = new short[PocketSpc03Dsp.MixBufSize * 2];
        blockPos = PocketSpc03Dsp.MixBufSize; phase = 0;
        // replay the S-DSP's registers (everything, then key-on last)
        for (int i = 0; i < 0x80; i++) if (i != 0x4C && i != 0x7C) Ps.Write(i, Dsp.Regs[i]);
        if (Dsp.Regs[0x4C] != 0) Ps.Write(0x4C, Dsp.Regs[0x4C]);
    }
}
