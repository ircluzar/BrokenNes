namespace NesEmulator.Snes;

/// <summary>
/// The SNES sound unit with the Snes9x 1.51 (2007) sound engine (<see cref="S951Dsp"/>, ported from Snes9x 1.51 - Snes9x
/// non-commercial license, a documented exception, see THIRD_PARTY_NOTICES.md section 2). The SPC700, the timers and the stock
/// S-DSP run as in <see cref="APU_SFC"/>, so the game's sound driver reads correct DSP registers and every bridge and tool keeps
/// working; what is heard comes from the Snes9x engine, fed the same DSP register writes. It mixes stereo at 32,000 Hz in
/// 533-frame blocks, one engine frame per S-DSP tick.
/// Quick port: a state load or a swap re-feeds the engine the DSP registers (re-keying the voices that were keyed on), the engine's
/// own envelope/sample position is not saved.
/// </summary>
public sealed class APU_S951 : APU_SFC, ISnesApu
{
    public override string CoreName => "S951";

    private S951Dsp? s9;                    // created on the first hook (the base constructor runs Reset before this class's fields exist)
    private S951Dsp S9 => s9 ??= new S951Dsp(Aram);
    private short[] blockBuf = new short[S951Dsp.BlockFrames * 2];
    private int blockPos = S951Dsp.BlockFrames;  // render on the first tick

    protected override void OnDspSample(short l, short r)
    {
        if (++blockPos >= S951Dsp.BlockFrames)
        {
            S9.RenderBlock().CopyTo(blockBuf, 0);
            blockPos = 0;
        }
        PushSample(blockBuf[blockPos * 2], blockBuf[blockPos * 2 + 1]);
    }

    protected override void OnDspRegWrite(byte reg, byte value) => S9.Write(reg, value);

    protected override void OnReset()
    {
        S9.Reset();
        blockBuf = new short[S951Dsp.BlockFrames * 2];
        blockPos = S951Dsp.BlockFrames;
        // replay the S-DSP's registers (the way spcplay loads a song: everything, then key-on last)
        for (int i = 0; i < 0x80; i++) if (i != 0x4C && i != 0x7C) S9.Write(i, Dsp.Regs[i]);
        if (Dsp.Regs[0x4C] != 0) S9.Write(0x4C, Dsp.Regs[0x4C]);
    }
}
