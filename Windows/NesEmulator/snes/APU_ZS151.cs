namespace NesEmulator.Snes;

/// <summary>
/// The SNES sound unit with the ZSNES 1.51 (2007) sound engine (<see cref="Zs151Dsp"/>, hand-ported from ZSNES's x86 asm - GPL-2.0,
/// a documented exception, see THIRD_PARTY_NOTICES.md section 2). The SPC700, the timers and the stock S-DSP run as in
/// <see cref="APU_SFC"/>, so the game's sound driver reads correct DSP registers and every bridge and tool keeps working; what is
/// heard comes from the ZSNES engine, fed the same DSP register writes. ZSNES's default rate is 32,000 Hz, so each S-DSP tick
/// takes the next frame of a 160-frame stereo block; key-ons go through ZSNES's scanline pipeline, stepped every other sample
/// (a SNES scanline is about 2 samples long).
/// Quick port: a state load or a swap re-feeds the engine the DSP registers (re-keying the voices that were keyed on), the engine's
/// own envelope/sample position is not saved.
/// </summary>
public sealed class APU_ZS151 : APU_SFC, ISnesApu, IPortedEngineState
{
    public override string CoreName => "ZS151";

    private Zs151Dsp? zs;                   // created on the first hook (the base constructor runs Reset before this class's fields exist)
    private Zs151Dsp Zs => zs ??= new Zs151Dsp(Aram);
    private short[] blockBuf = new short[Zs151Dsp.BlockFrames * 2];
    private int blockPos = Zs151Dsp.BlockFrames;  // render on the first tick
    private int tick;

    protected override void OnDspSample(short l, short r)
    {
        if ((++tick & 1) == 0) Zs.Scanline();
        if (++blockPos >= Zs151Dsp.BlockFrames)
        {
            Zs.RenderBlock().CopyTo(blockBuf);
            blockPos = 0;
        }
        PushSample(blockBuf[blockPos * 2], blockBuf[blockPos * 2 + 1]);
    }

    protected override void OnDspRegWrite(byte reg, byte value) => Zs.Write(reg, value);

    protected override void OnReset()
    {
        Zs.Reset();
        blockBuf = new short[Zs151Dsp.BlockFrames * 2];
        blockPos = Zs151Dsp.BlockFrames; tick = 0;
        // replay the S-DSP's registers (the way spcplay loads a song: everything, then key-on last; KOF is left out because
        // a KOF write starts ZSNES's release fade and a KOF of $FF would block the key-on)
        for (int i = 0; i < 0x80; i++) if (i != 0x4C && i != 0x5C && i != 0x7C) Zs.Write(i, Dsp.Regs[i]);
        if (Dsp.Regs[0x4C] != 0) Zs.Write(0x4C, Dsp.Regs[0x4C]);
    }
}
