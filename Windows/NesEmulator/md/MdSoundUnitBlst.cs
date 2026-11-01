// Foreign APU project unit (docs/projects/foreign-apu-project.md): the glue around sega/BlastemYm2612.cs + BlastemPsg.cs (BlastEm 0.3.1, GPL-3.0-or-later); it may follow the original's
// driving code (clocking, mixing) as described in that engine file's header. A documented exception to the license policy: THIRD_PARTY_NOTICES.md section 2a.
using System;

namespace NesEmulator.Sega;

/// <summary>
/// The Genesis sound unit with BlastEm 0.3.1's YM2612 and PSG heard in place of the stock chips (Genesis APU id <c>BLST</c>; the foreign APU project).
/// The stock YM2612 and PSG keep running for the game (status, timers, busy); BlastEm's engines (<see cref="BlastemYm2612"/>, <see cref="BlastemPsg"/>,
/// GPL-3.0-or-later, Copyright 2013 Michael Pavone) take the same writes and replace what is heard: the FM one sample per stock sample (1008 master clocks),
/// the PSG point-sampled at the host rate (writes land at the block the host pulls, not at their clock).
/// </summary>
public sealed class MdSoundUnitBlst : MdSoundUnit
{
    private readonly SegaMachine machine;
    private BlastemYm2612? fm;
    private BlastemPsg? bpsg;

    public MdSoundUnitBlst(SegaMachine machine) : base(machine) { this.machine = machine; }

    private BlastemYm2612 Fm2 => fm ??= new BlastemYm2612();
    // blastem.c: psg_init(rate, master clock, MCLKS_PER_PSG = 15 * 16)
    private BlastemPsg Psg2 => bpsg ??= new BlastemPsg(machine.SampleRate, (uint)machine.Timeline.MasterHz, 15 * 16);

    protected override void OnYmWrite(int port, byte value) => Fm2.WritePort(port, value);
    protected override void OnPsgWrite(byte value) => Psg2.Write(value);
    protected override void OnFmSample(ref int left, ref int right) => Fm2.RunSample(out left, out right);

    protected override void OnPsgSamples(Span<short> stereo)
    {
        var p = Psg2;
        for (int i = 0; i + 1 < stereo.Length; i += 2) stereo[i] = stereo[i + 1] = p.NextSample();
    }

    protected override void OnReset()
    {
        if (machine == null) return;   // a base-constructor Reset, before the fields exist
        Fm2.Reset();
        Psg2.Reset();
    }
}
