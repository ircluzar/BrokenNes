using System;

namespace NesEmulator.Sega;

/// <summary>
/// The Master System / Game Gear sound unit with Genesis Plus GX 1.3.0's PSG (<see cref="GpgxSn76489"/>, Maxim's SN76489 as GX configured it; Genesis Plus terms, non-commercial - a documented
/// exception, see THIRD_PARTY_NOTICES.md section 2). GX 1.3.0 was a Genesis-only emulator: its Genesis PSG is used as is, clocked by the Z80 clock, rendered at the host rate, x1.5 (psg_preamp)
/// and through GX's default one-pole output filter. Mono: the Game Gear stereo byte is ignored, as GX's Update ignored it. Register writes land on the next drained chunk.
/// </summary>
public sealed class SmsSoundGpgx : SmsSound, IPortedEngineState
{
    private readonly double clock;
    private readonly bool ready;
    private GpgxSn76489? sn;
    private int ll;

    public SmsSoundGpgx(double cpuClockHz, int sampleRate, PsgVariant variant = PsgVariant.Sega) : base(cpuClockHz, sampleRate, variant)
    {
        clock = cpuClockHz;
        ready = true;
        OnReset();
    }

    private GpgxSn76489 Sn => sn ??= new GpgxSn76489(clock, SampleRate);

    protected override void OnPsgWrite(byte value) { if (ready) Sn.Write(value); }
    protected override void OnStereoWrite(byte value) { if (ready) Sn.GGStereoWrite(value); }

    protected override void OnPsgSamples(Span<short> stereo)
    {
        if (!ready) return;
        var p = Sn;
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            int s = p.Next() * 150 / 100;   // psg_preamp
            s = (ll + s) >> 1; ll = s;       // GX's output filter
            short o = (short)Math.Clamp(s, short.MinValue, short.MaxValue);
            stereo[i] = o; stereo[i + 1] = o;
        }
    }

    protected override void OnReset()
    {
        if (!ready) return;
        sn = null; ll = 0;
        Sn.ReplayFrom(Psg);
    }
}
