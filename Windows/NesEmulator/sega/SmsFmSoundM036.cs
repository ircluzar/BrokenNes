// Foreign APU project unit (docs/projects/foreign-apu-project.md): the glue around sega/Mame036Opl.cs + Mame036Ym2413.cs (MAME 0.36, MAME non-commercial license); it may follow the original's
// driving code (clocking, mixing) as described in that engine file's header. A documented exception to the license policy: THIRD_PARTY_NOTICES.md section 2a.
using System;

namespace NesEmulator.Sega;

/// <summary>
/// The Japanese Master System's FM as MAME 0.36 played a YM2413: Paul Leaman's register translator (<see cref="Mame036Ym2413"/>) driving MAME 0.36's YM3812 core
/// (<see cref="Mame036Opl"/>, fmopl.c v0.36f) - a hand-made patch set on an OPL2, no volumes, no feedback, a guessed rhythm section. Menu id <c>M036FM</c>.
/// The stock PSG and the stock YM2413 keep running (the game's ports and the stock state are untouched); only the FM that is heard is replaced.
/// The OPL runs at MAME's default 22,050 Hz from a 3,579,545 Hz clock (3812intf.c's standard clock) and is held sample-and-hold up to the chip rate.
/// </summary>
public sealed class SmsFmSoundM036 : SmsFmSound, IPortedEngineState
{
    public const int OplClock = 3579545;
    public const int OplRate = 22050;      // MAME 0.36's default sample rate
    private const int OutShift = 2;        // fmopl's 16-bit full-scale carrier (8,192) down to about the stock chip's level (2,042)

    private readonly double chipRate;
    private Mame036Opl? opl;
    private Mame036Ym2413? ym;
    private double phase;
    private int held;

    public SmsFmSoundM036(double cpuClockHz, int sampleRate, PsgVariant variant = PsgVariant.Sega) : base(cpuClockHz, sampleRate, variant)
    {
        chipRate = cpuClockHz / Ym2413.ClocksPerSample;
        Engine();
    }

    private Mame036Ym2413 Engine()
    {
        if (ym == null) { opl = new Mame036Opl(OplClock, OplRate); ym = new Mame036Ym2413(opl); }
        return ym;
    }

    protected override void OnReset()
    {
        // a fresh YM2413_sh_start (a new chip: tables, registers, the LFO cache)
        Engine();
        opl!.ResetChip();
        ym!.Start();
        phase = 0; held = 0;
        ReplayFmRegisters();      // a state load / core swap: the game's instruments and held notes, from the stock chip
    }

    protected override void OnFmAddress(byte value) => Engine().WriteRegister(value);
    protected override void OnFmData(byte value) => Engine().WriteData(value);

    protected override int OnFmSample(int sample)
    {
        var y = Engine();
        double cr = chipRate > 0 ? chipRate : 49716;
        phase += OplRate;
        while (phase >= cr) { phase -= cr; held = y.Opl.Update(); }
        return held >> OutShift;
    }
}
