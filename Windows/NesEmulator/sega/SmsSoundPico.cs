// Foreign APU project unit (docs/projects/foreign-apu-project.md): the glue around sega/PicoSn76496.cs (PicoDrive v1.85, non-commercial + MAME license); it may follow the original's
// driving code (clocking, mixing) as described in that engine file's header. A documented exception to the license policy: THIRD_PARTY_NOTICES.md section 2a.
using System;

namespace NesEmulator.Sega;

/// <summary>
/// Master System / Game Gear sound unit <c>PICO</c>: the stock PSG keeps running; what is heard is PicoDrive 1.85's SN76496 (<see cref="PicoSn76496"/>) run the way pico/sound/sound.c's
/// PsndGetSamplesMS runs it: 44.1 kHz mono, one whole frame (735 / 882 samples) at a time, upmixed to both sides. PicoDrive 1.85 has no Game Gear, so the stereo port ($06) is ignored.
/// Converted to the host rate by zero-order hold. Quick port; see <see cref="PicoSn76496"/> for the license (PicoDrive non-commercial / MAME) and the kept quirks.
/// </summary>
public sealed class SmsSoundPico : SmsSound, ISmsSound, IPortedEngineState
{
    private readonly double clock;
    private readonly int frameLen;
    private PicoSn76496? psn;
    private readonly int[] buf;
    private int pos;
    private double acc;
    private int cur;

    public SmsSoundPico(SmsSoundRequest r) : base(r.CpuClockHz, r.Machine.SampleRate, r.Variant)
    {
        clock = r.CpuClockHz;   // OSC/15, the Z80 clock
        frameLen = PicoSn76496.Rate / (r.Machine.Timeline.Region == SegaRegion.Pal ? 50 : 60);
        buf = new int[frameLen];
        pos = frameLen;
    }

    public new string CoreName => "PICO (PicoDrive 1.85 SN76496)";

    /// <summary>Built on first use with the stock PSG's registers replayed into it.</summary>
    private PicoSn76496 Sn => psn ??= Build();

    private PicoSn76496 Build()
    {
        var e = new PicoSn76496(clock);
        var p = Psg;
        for (int c = 0; c < 3; c++)
        {
            e.Write(0x80 | c << 5 | p.Period(c) & 0x0f); e.Write(p.Period(c) >> 4 & 0x3f);
            e.Write(0x90 | c << 5 | p.Volume(c) & 0x0f);
        }
        e.Write(0xe0 | p.NoiseControl & 7); e.Write(0xf0 | p.Volume(3) & 0x0f);
        return e;
    }

    protected override void OnReset() { psn = null; pos = frameLen; cur = 0; }

    protected override void OnPsgWrite(byte value) => Sn.Write(value);

    protected override void OnPsgSamples(Span<short> stereo)
    {
        int host = SampleRate;
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            acc += PicoSn76496.Rate;
            while (acc >= host)
            {
                acc -= host;
                if (pos >= frameLen) { Array.Clear(buf); Sn.Update(buf, 0, frameLen); pos = 0; }
                cur = buf[pos++];
            }
            short v = (short)(cur > 32767 ? 32767 : cur);
            stereo[i] = v; stereo[i + 1] = v;
        }
    }
}
