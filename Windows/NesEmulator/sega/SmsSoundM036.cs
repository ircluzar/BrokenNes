using System;

namespace NesEmulator.Sega;

/// <summary>
/// The Master System / Game Gear sound unit with MAME 0.36's (1999) sn76496.c (<see cref="Mame036Sn76496"/>; Nicola Salmoria, MAME license, non-commercial - a documented exception, see
/// THIRD_PARTY_NOTICES.md section 2): the stock PSG keeps running, what is heard is the MAME 0.36 PSG at 22,050 Hz, mono (the Game Gear stereo port is ignored, as MAME 0.36 had none), held up to the host rate.
/// Quick port: register writes land on the next drained chunk; a state load or swap re-feeds the stock PSG's registers.
/// </summary>
public sealed class SmsSoundM036 : SmsSound
{
    private readonly double clock;
    private readonly bool ready;
    private Mame036Sn76496? sn;

    public SmsSoundM036(double cpuClockHz, int sampleRate, PsgVariant variant = PsgVariant.Sega) : base(cpuClockHz, sampleRate, variant)
    {
        clock = cpuClockHz;
        ready = true;
        OnReset();
    }

    private Mame036Sn76496 Sn => sn ??= new Mame036Sn76496(clock);

    protected override void OnPsgWrite(byte value) { if (ready) Sn.Write(value); }
    protected override void OnPsgSamples(Span<short> stereo) { if (ready) Sn.FillHost(stereo, SampleRate); }
    protected override void OnReset()
    {
        if (!ready) return;
        sn = null;
        Sn.ReplayFrom(Psg);
    }
}
