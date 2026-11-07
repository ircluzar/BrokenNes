using System;

namespace NesEmulator.Sega;

/// <summary>
/// Master System / Game Gear sound unit <c>SMSA</c>: the stock PSG keeps running; what is heard is SMSAdvance 2.5's PSG (<see cref="SmsaPsg"/>; FluBBa (Fredrik Ahlström),
/// credit requested, no formal licence - a documented exception, see THIRD_PARTY_NOTICES.md section 2a): one 528-sample block per frame (31,536 Hz on the GBA, here 528 per
/// emulated frame), mixed at vblank from the registers as they stand and played during the next frame (the GBA DMA double buffer); SMSAdvance's volume table, Game Gear stereo
/// as on/off masks, SG-1000 noise taps on the TI variant. Converted to the host rate by zero-order hold. A state load or a swap re-feeds the stock PSG's registers.
/// </summary>
public sealed class SmsSoundSmsa : SmsSound, ISmsSound
{
    private readonly double engineRate;
    private readonly bool pal, sg;
    private SmsaPsg? eng;
    private int pos = SmsaPsg.MixLength;
    private double acc;
    private short curL, curR;

    public SmsSoundSmsa(SmsSoundRequest r) : base(r.CpuClockHz, r.Machine.SampleRate, r.Variant)
    {
        var t = r.Machine.Timeline;
        double cyclesPerFrame = (double)t.MasterClocksPerFrame / t.Z80.Divider;
        engineRate = SmsaPsg.MixLength * r.CpuClockHz / cyclesPerFrame;
        pal = t.Region != SegaRegion.Ntsc;
        sg = r.Variant == PsgVariant.Ti;
    }

    public new string CoreName => "SMSA (SMSAdvance 2.5 PSG)";

    private SmsaPsg Eng => eng ??= Build();

    private SmsaPsg Build()
    {
        var e = new SmsaPsg(pal, sg);
        var p = Psg;
        e.WriteStereo((byte)p.StereoMask);
        for (int c = 0; c < 3; c++)
        {
            e.Write((byte)(0x80 | c << 5 | p.Period(c) & 0x0f)); e.Write((byte)(p.Period(c) >> 4 & 0x3f));
            e.Write((byte)(0x90 | c << 5 | p.Volume(c) & 0x0f));
        }
        e.Write((byte)(0xe0 | p.NoiseControl & 7)); e.Write((byte)(0xf0 | p.Volume(3) & 0x0f));
        return e;
    }

    protected override void OnReset() { eng = null; pos = SmsaPsg.MixLength; curL = curR = 0; acc = 0; }

    protected override void OnPsgWrite(byte value) => Eng.Write(value);

    protected override void OnStereoWrite(byte value) => Eng.WriteStereo(value);

    protected override void OnPsgSamples(Span<short> stereo)
    {
        if (engineRate == 0) return;
        var e = Eng;
        int host = SampleRate;
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            acc += engineRate;
            while (acc >= host)
            {
                acc -= host;
                if (pos >= SmsaPsg.MixLength) { e.Vbl(); pos = 0; }
                var b = e.Playing;
                // the 8-bit DirectSound sample above its -126 floor (0..252), scaled up: unipolar like the other ports
                curL = (short)((b[pos * 2] + 126) * 64);
                curR = (short)((b[pos * 2 + 1] + 126) * 64);
                pos++;
            }
            stereo[i] = curL; stereo[i + 1] = curR;
        }
    }
}
