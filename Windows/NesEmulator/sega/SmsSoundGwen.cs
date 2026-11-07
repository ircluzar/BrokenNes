using System;

namespace NesEmulator.Sega;

/// <summary>
/// The Master System / Game Gear sound unit with Gwenesis's PSG (<see cref="GwenSn76489"/>: retro-go a0f857bf, Maxim's SN76489 cut down by bzhxx "to fit on MCU"; AGPL-3.0 - a documented
/// exception, see THIRD_PARTY_NOTICES.md section 2a). Gwenesis is a Genesis emulator: its Genesis PSG is used as is, clocked by the Z80 clock, rendered at the host rate. Mono: the
/// Game Gear stereo byte is ignored (Gwenesis has no stereo PSG). Its flaws come along: white noise dies (feedback mask never set), coarse 6 dB volume ladder, doubled noise.
/// </summary>
public sealed class SmsSoundGwen : SmsSound
{
    private readonly double clock;
    private readonly bool ready;
    private GwenSn76489? sn;

    public SmsSoundGwen(double cpuClockHz, int sampleRate, PsgVariant variant = PsgVariant.Sega) : base(cpuClockHz, sampleRate, variant)
    {
        clock = cpuClockHz;
        ready = true;
        OnReset();
    }

    private GwenSn76489 Sn => sn ??= new GwenSn76489(clock, SampleRate);

    protected override void OnPsgWrite(byte value) { if (ready) Sn.Write(value); }

    protected override void OnPsgSamples(Span<short> stereo)
    {
        if (!ready) return;
        var p = Sn;
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            short o = p.Next();
            stereo[i] = o; stereo[i + 1] = o;
        }
    }

    protected override void OnReset()
    {
        if (!ready) return;
        sn = null;
        Sn.ReplayFrom(Psg);
    }
}
