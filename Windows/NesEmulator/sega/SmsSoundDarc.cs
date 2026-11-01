using System;

namespace NesEmulator.Sega;

/// <summary>
/// Master System / Game Gear sound unit <c>DARC</c>: the stock PSG keeps running; what is heard is DarcNES's sms_psg.c 1.14 (<see cref="DarcSmsPsg"/>; Alastair Bridgewater,
/// credit required, non-commercial - a documented exception, see THIRD_PARTY_NOTICES.md section 2): one 735-sample block per frame (sms_psg_vsync every 228 * 262 Z80 cycles,
/// ~44.05 kHz in emulated time), a 5-bit noise register, linear volume, unipolar mono (the Game Gear stereo port is ignored). Blocks are rendered when the mixer reaches them,
/// so writes are heard from the next block; converted to the host rate by zero-order hold. A state load or a swap re-feeds the stock PSG's registers.
/// </summary>
public sealed class SmsSoundDarc : SmsSound, ISmsSound
{
    private readonly double engineRate;
    private DarcSmsPsg? eng;
    private int pos = DarcSmsPsg.SamplesPerSync;
    private double acc;
    private int cur;

    public SmsSoundDarc(SmsSoundRequest r) : base(r.CpuClockHz, r.Machine.SampleRate, r.Variant)
    {
        engineRate = DarcSmsPsg.SamplesPerSync * r.CpuClockHz / DarcSmsPsg.CyclesPerSync;
    }

    public new string CoreName => "DARC (DarcNES 1.14 SMS PSG)";

    private DarcSmsPsg Eng => eng ??= Build();

    private DarcSmsPsg Build()
    {
        var e = new DarcSmsPsg();
        var p = Psg;
        for (int c = 0; c < 3; c++)
        {
            e.Write((byte)(0x80 | c << 5 | p.Period(c) & 0x0f)); e.Write((byte)(p.Period(c) >> 4 & 0x3f));
            e.Write((byte)(0x90 | c << 5 | p.Volume(c) & 0x0f));
        }
        e.Write((byte)(0xe0 | p.NoiseControl & 7)); e.Write((byte)(0xf0 | p.Volume(3) & 0x0f));
        return e;
    }

    protected override void OnReset() { eng = null; pos = DarcSmsPsg.SamplesPerSync; cur = 0; acc = 0; }

    protected override void OnPsgWrite(byte value) => Eng.Write(value);

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
                if (pos >= DarcSmsPsg.SamplesPerSync) { e.Vsync(); pos = 0; }
                cur = e.Mix[pos++];
            }
            short v = (short)cur;
            stereo[i] = v; stereo[i + 1] = v;
        }
    }
}
