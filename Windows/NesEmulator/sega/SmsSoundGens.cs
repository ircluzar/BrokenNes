using System;

namespace NesEmulator.Sega;

/// <summary>
/// The Master System / Game Gear sound unit with Gens 2.10's PSG (<see cref="GensPsg"/>, Gens' default 22,050 Hz square renderer),
/// APU-menu id <c>GENS</c>. The stock SN76489 keeps running; the hooks feed every PSG write to the Gens PSG and replace the samples.
/// </summary>
/// <remarks>
/// Gens 2.10 has no Master System or Game Gear: its Genesis PSG is used as is, clocked by the Z80 clock (the same 3.58 MHz Gens gives
/// it). The Game Gear stereo port is ignored (Gens' PSG is mono). The hook carries no write times, so writes land when they happen
/// and the samples are rendered when the mixer asks: the effect of a write is not placed inside the audio chunk it falls in.
/// Gens' level is halved (as in <see cref="MdSoundUnitGens"/>); the output stays unipolar like Gens'.
/// </remarks>
public sealed class SmsSoundGens : SmsSound
{
    private const int GensRate = 22050;
    private readonly double cpuClockHz;
    private GensPsg? psg;
    private GensPsg Gens => psg ??= new GensPsg((int)cpuClockHz, GensRate);
    private readonly int[] one = new int[1], oneR = new int[1];
    private double phase;
    private int hold;

    public SmsSoundGens(double cpuClockHz, int sampleRate, PsgVariant variant) : base(cpuClockHz, sampleRate, variant)
    {
        this.cpuClockHz = cpuClockHz;
    }

    protected override void OnPsgWrite(byte value) => Gens.Write(value);

    protected override void OnReset()
    {
        if (cpuClockHz == 0) return;   // the base constructor's Reset, before this class's fields
        psg = null; phase = 0; hold = 0;
        // re-seed from the stock PSG's registers (after a state load or a swap onto this unit); the Gens PSG's counters start from 0
        var st = Psg; var p = Gens;
        for (int ch = 0; ch < 3; ch++)
        {
            p.Write(0x80 | ch << 5 | (st.Period(ch) & 0xF)); p.Write(st.Period(ch) >> 4 & 0x3F);
            p.Write(0x90 | ch << 5 | (st.Volume(ch) & 0xF));
        }
        p.Write(0xE0 | (st.NoiseControl & 7)); p.Write(0xF0 | (st.Volume(3) & 0xF));
    }

    protected override void OnPsgSamples(Span<short> stereo)
    {
        var p = Gens;
        double step = (double)GensRate / SampleRate;
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            phase += step;
            while (phase >= 1)
            {
                phase -= 1;
                one[0] = 0; oneR[0] = 0;
                p.Update(one, oneR, 0, 1);
                hold = one[0] >> 1;
            }
            short s = (short)Math.Clamp(hold, short.MinValue, short.MaxValue);
            stereo[i] = s; stereo[i + 1] = s;
        }
    }
}
