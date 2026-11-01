namespace NesEmulator.Snes;

/// <summary>
/// The SNES sound unit with the Snes9x 1.43 (2004) sound engine (<see cref="S9x1Dsp"/>, ported from snes9x2005 - Snes9x
/// non-commercial license, a documented exception, see THIRD_PARTY_NOTICES.md section 2). The SPC700, the timers and the stock
/// S-DSP run as in <see cref="APU_SFC"/>, so the game's sound driver reads correct DSP registers and every bridge and tool keeps
/// working; what is heard comes from the Snes9x engine, fed the same DSP register writes. It mixes stereo at 32,040 Hz in
/// 534-frame blocks; each 32 kHz S-DSP tick takes the nearest Snes9x frame (crude, on purpose).
/// Quick port: a state load or a swap re-feeds the engine the DSP registers (re-keying the voices that were keyed on), the engine's
/// own envelope/sample position is not saved.
/// </summary>
public sealed class APU_S9X1 : APU_SFC, ISnesApu
{
    public override string CoreName => "S9X1";

    private S9x1Dsp? s9;                    // created on the first hook (the base constructor runs Reset before this class's fields exist)
    private S9x1Dsp S9 => s9 ??= new S9x1Dsp(Aram);
    private short[] blockBuf = new short[S9x1Dsp.BlockFrames * 2];
    private int blockPos = S9x1Dsp.BlockFrames;  // render on the first tick
    private int phase;

    protected override void OnDspSample(short l, short r)
    {
        // 32,040 Hz engine frames consumed at 32,000 Hz: step the read position, zero-order hold
        phase += S9x1Dsp.Rate;
        while (phase >= 32000)
        {
            phase -= 32000;
            if (++blockPos >= S9x1Dsp.BlockFrames)
            {
                S9.RenderBlock().CopyTo(blockBuf, 0);
                blockPos = 0;
            }
        }
        PushSample(blockBuf[blockPos * 2], blockBuf[blockPos * 2 + 1]);
    }

    protected override void OnDspRegWrite(byte reg, byte value) => S9.Write(reg, value);

    protected override void OnReset()
    {
        S9.Reset();
        blockBuf = new short[S9x1Dsp.BlockFrames * 2];
        blockPos = S9x1Dsp.BlockFrames; phase = 0;
        // replay the S-DSP's registers (the way spcplay loads a song: everything, then key-on last)
        for (int i = 0; i < 0x80; i++) if (i != 0x4C && i != 0x7C) S9.Write(i, Dsp.Regs[i]);
        if (Dsp.Regs[0x4C] != 0) S9.Write(0x4C, Dsp.Regs[0x4C]);
    }
}
