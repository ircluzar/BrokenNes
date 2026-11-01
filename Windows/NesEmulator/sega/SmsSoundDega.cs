using System;

namespace NesEmulator.Sega;

/// <summary>
/// Master System / Game Gear sound unit <c>DEGA</c>: the stock PSG keeps running; what is heard is Dega 1.16's PSG (<see cref="DegaPsg"/>; Dave / FinalBurn author,
/// non-commercial - a documented exception, see THIRD_PARTY_NOTICES.md section 2) in its Enhanced PSG mode: tone channels 0 and 1 play sampled instruments
/// (DspSample, MoogSample), channel 2 a soft square, channels 0-1 get Dega's echo, and a volume jump retriggers the note. Rendered at 44,100 Hz (Dega's
/// frontend rate) when the mixer asks, so writes are heard from the next chunk; converted to the host rate by zero-order hold. Game Gear stereo is honoured
/// the Dega way (a channel switched off on one side plays at 1/4 there). A state load or a swap re-feeds the stock PSG's registers (Dega's MsndRefresh).
/// </summary>
public sealed class SmsSoundDega : SmsSound, ISmsSound
{
    private const int DegaRate = 44100;
    private readonly bool enhance;
    private DegaPsg? eng;
    private double acc;
    private short curL, curR;

    public SmsSoundDega(SmsSoundRequest r, bool enhance = true) : base(r.CpuClockHz, r.Machine.SampleRate, r.Variant)
    {
        this.enhance = enhance;
    }

    public new string CoreName => enhance ? "DEGA (Dega 1.16 Enhanced PSG)" : "DEGA (Dega 1.16 PSG)";

    private DegaPsg Eng => eng ??= Build();

    private DegaPsg Build()
    {
        var e = new DegaPsg(DegaRate, enhance);
        var p = Psg;
        // MsndRefresh: stereo, the four volumes, then the four periods
        e.WriteStereo((byte)p.StereoMask);
        for (int c = 0; c < 4; c++) e.Write((byte)(0x90 | c << 5 | p.Volume(c) & 0x0f));
        for (int c = 0; c < 3; c++) { e.Write((byte)(0x80 | c << 5 | p.Period(c) & 0x0f)); e.Write((byte)(p.Period(c) >> 4 & 0x3f)); }
        e.Write((byte)(0xe0 | p.NoiseControl & 7)); e.Write(0);
        return e;
    }

    protected override void OnReset() { eng = null; acc = 0; curL = curR = 0; }

    protected override void OnPsgWrite(byte value) => Eng.Write(value);

    protected override void OnStereoWrite(byte value) => Eng.WriteStereo(value);

    protected override void OnPsgSamples(Span<short> stereo)
    {
        int host = SampleRate;
        if (host <= 0) return;
        var e = Eng;
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            acc += DegaRate;
            while (acc >= host) { acc -= host; e.Calc(out curL, out curR); }
            stereo[i] = curL; stereo[i + 1] = curR;
        }
    }
}
