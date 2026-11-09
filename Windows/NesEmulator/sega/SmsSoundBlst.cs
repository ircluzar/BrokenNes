// Foreign APU project unit (docs/projects/foreign-apu-project.md): the glue around sega/BlastemPsg.cs (BlastEm 0.3.1, GPL-3.0-or-later); it may follow the original's
// driving code (clocking, mixing) as described in that engine file's header. A documented exception to the license policy: THIRD_PARTY_NOTICES.md section 2a.
using System;

namespace NesEmulator.Sega;

/// <summary>
/// A Master System / Game Gear sound unit whose PSG is heard through BlastEm 0.3.1's (<see cref="BlastemPsg"/>, GPL-3.0-or-later, Copyright 2013 Michael Pavone;
/// SMS APU id <c>BLST</c>; the foreign APU project). The stock SN76489 keeps running; BlastEm's takes the same writes, clocked at the Z80 clock / 16 and point-sampled
/// at the host rate (writes land at the block the host pulls). Mono: BlastEm 0.3.1 has no Game Gear stereo port, so stereo writes are ignored.
/// </summary>
public sealed class SmsSoundBlst : SmsSound, IPortedEngineState
{
    private readonly double cpuClockHz;
    private BlastemPsg? bpsg;

    public SmsSoundBlst(double cpuClockHz, int sampleRate, PsgVariant variant) : base(cpuClockHz, sampleRate, variant) { this.cpuClockHz = cpuClockHz; }

    private BlastemPsg Psg2 => bpsg ??= new BlastemPsg(SampleRate, (uint)cpuClockHz, 16);

    protected override void OnPsgWrite(byte value) => Psg2.Write(value);

    protected override void OnPsgSamples(Span<short> stereo)
    {
        var p = Psg2;
        for (int i = 0; i + 1 < stereo.Length; i += 2) stereo[i] = stereo[i + 1] = p.NextSample();
    }

    protected override void OnReset()
    {
        if (cpuClockHz == 0) return;   // a base-constructor Reset, before the fields exist
        Psg2.Reset();
        foreach (var v in MdSoundUnit.PsgReplayBytes(Psg)) Psg2.Write(v);   // a state load / core swap: the held tones (--apu-channels --state)
    }
}
