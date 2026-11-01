using System;

namespace NesEmulator.Sega;

/// <summary>
/// The Japanese Master System's FM as emu2413 v0.55 (Mitsutaka Okazaki, 2002) played a YM2413 (<see cref="Emu2413v055"/>): a very early OPLL
/// emulator ("Its quality is extremely low"), with its log-curve attack, two-sample averaging "LPF", early drum models and its own instrument ROM.
/// Menu id <c>E055</c>. The stock PSG and the stock YM2413 keep running (the game's ports and the stock state are untouched); only the FM that is heard is replaced.
/// The engine runs at its sample player's default, OPLL_new(3,579,545, 44,100) with quality 0, and is held sample-and-hold up to the chip rate.
/// </summary>
public sealed class SmsFmSoundE055 : SmsFmSound
{
    public const uint OpllClock = 3579545;
    public const uint OpllRate = 44100;    // sample2413.c's SAMPLERATE; quality 0 (OPLL_new's calloc default)

    private readonly double chipRate;
    private Emu2413v055? opll;
    private double phase;
    private int held;

    public SmsFmSoundE055(double cpuClockHz, int sampleRate, PsgVariant variant = PsgVariant.Sega) : base(cpuClockHz, sampleRate, variant)
    {
        chipRate = cpuClockHz / Ym2413.ClocksPerSample;
        Engine();
    }

    private Emu2413v055 Engine() => opll ??= new Emu2413v055(OpllClock, OpllRate);

    protected override void OnReset()
    {
        var o = Engine();
        o.Reset(); o.ResetPatch();
        phase = 0; held = 0;
    }

    protected override void OnFmAddress(byte value) => Engine().WriteIO(0, value);
    protected override void OnFmData(byte value) => Engine().WriteIO(1, value);

    protected override int OnFmSample(int sample)
    {
        var o = Engine();
        double cr = chipRate > 0 ? chipRate : 49716;
        phase += OpllRate;
        while (phase >= cr) { phase -= cr; held = o.Calc(); }
        return held;   // 2,047 full scale per carrier, about the stock chip's level
    }
}
