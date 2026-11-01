namespace NesEmulator.Snes;

/// <summary>
/// The SNES sound unit with the ZSNES 0.15 (1997) sound engine (<see cref="Zs15Dsp"/>, ported from zsnes-asm2c - GPL, a documented
/// exception, see THIRD_PARTY_NOTICES.md section 2). The SPC700, the timers and the stock S-DSP run as in <see cref="APU_SFC"/>, so the
/// game's sound driver reads correct DSP registers and every bridge and tool keeps working; what is heard comes from the ZSNES engine,
/// fed the same DSP register writes. It mixes mono at 44.1 kHz in 320-sample blocks; each 32 kHz S-DSP tick takes the nearest
/// ZSNES sample (crude, on purpose).
/// Quick port: a state load or a swap re-feeds the engine the DSP registers (re-keying the voices that were keyed on), the engine's
/// own envelope/sample position is not saved.
/// </summary>
public sealed class APU_ZS15 : APU_SFC, ISnesApu
{
    public override string CoreName => "ZS15";

    private Zs15Dsp? zs;                    // created on the first hook (the base constructor runs Reset before this class's fields exist)
    private Zs15Dsp Zs => zs ??= new Zs15Dsp(Aram);
    private short[] blockBuf = new short[Zs15Dsp.BlockSamples];
    private int blockPos = Zs15Dsp.BlockSamples;  // render on the first tick
    private int phase;

    protected override void OnDspSample(short l, short r)
    {
        // 44,100 Hz engine samples consumed at 32,000 Hz: step the read position, zero-order hold
        phase += Zs15Dsp.Rate;
        while (phase >= 32000)
        {
            phase -= 32000;
            if (++blockPos >= Zs15Dsp.BlockSamples)
            {
                Zs.RenderBlock().CopyTo(blockBuf);
                blockPos = 0;
            }
        }
        short s = blockBuf[blockPos];
        PushSample(s, s);
    }

    protected override void OnDspRegWrite(byte reg, byte value) => Zs.Write(reg, value);

    protected override void OnReset()
    {
        Zs.Reset();
        blockBuf = new short[Zs15Dsp.BlockSamples];
        blockPos = Zs15Dsp.BlockSamples; phase = 0;
        // replay the S-DSP's registers (the way spcplay loads a song: everything, then key-on last)
        for (int i = 0; i < 0x80; i++) if (i != 0x4C && i != 0x7C) Zs.Write(i, Dsp.Regs[i]);
        if (Dsp.Regs[0x4C] != 0) Zs.Write(0x4C, Dsp.Regs[0x4C]);
    }
}
