namespace NesEmulator.Snes;

/// <summary>
/// The SNES sound unit with the libOpenSPC (2002) sound engine (<see cref="OspcDsp"/>, ported from libOpenSPC - LGPL, a documented
/// exception, see THIRD_PARTY_NOTICES.md section 2). The SPC700, the timers and the stock S-DSP run as in <see cref="APU_SFC"/>, so the
/// game's sound driver reads correct DSP registers and every bridge and tool keeps working; what is heard comes from the libOpenSPC
/// engine, fed the same DSP register writes. libOpenSPC runs at 32 kHz stereo, one DSP_Update per S-DSP tick, so its samples go out 1:1.
/// Quick port: a state load or a swap re-feeds the engine the DSP registers (re-keying the voices that were keyed on), the engine's
/// own envelope/sample position and its echo buffer are not saved.
/// </summary>
public sealed class APU_OSPC : APU_SFC, ISnesApu
{
    public override string CoreName => "OSPC";

    private OspcDsp? os;                    // created on the first hook (the base constructor runs Reset before this class's fields exist)
    private OspcDsp Os => os ??= new OspcDsp(Aram);

    protected override void OnDspSample(short l, short r)
    {
        Os.Update(out short ol, out short orr);
        PushSample(ol, orr);
    }

    protected override void OnDspRegWrite(byte reg, byte value) => Os.Write(reg, value);

    protected override void OnReset()
    {
        Os.Reset();
        // replay the S-DSP's registers (the way spcplay loads a song: everything, then key-on last)
        for (int i = 0; i < 0x80; i++) if (i != 0x4C && i != 0x7C) Os.Write(i, Dsp.Regs[i]);
        if (Dsp.Regs[0x4C] != 0) Os.Write(0x4C, Dsp.Regs[0x4C]);
    }
}
