using System;

namespace NesEmulator.Sega;

/// <summary>
/// The Genesis sound unit with retro-go's Gwenesis sound (commit a0f857bf, upstream gwenesis by bzhxx): the MAME fm.c 1.4 YM2612 with Eke-Eke's GPGX changes as trimmed for
/// microcontrollers (<see cref="GwenYm2612"/>, mono, at the chip's own ~53 kHz rate) and Maxim's SN76489 cut down "to fit on MCU" (<see cref="GwenSn76489"/>, rendered at the host rate).
/// AGPL-3.0 - a documented exception, see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md. The Z80, the stock YM2612 (timers, busy, status) and the stock
/// PSG run as in <see cref="MdSoundUnit"/>; what is heard comes from the Gwenesis engines, fed the same writes, one Gwenesis FM sample per stock FM sample. Mono, as Gwenesis is:
/// the one int16 FM sum goes to both sides, the PSG too. No output filter (gwenesis has none). A state load or swap re-feeds the engines the stock chips' registers.
/// </summary>
public sealed class MdSoundUnitGwen : MdSoundUnit
{
    private readonly int hostRate;
    private readonly double psgClock;
    private readonly bool ready;
    private GwenYm2612? opn;
    private GwenSn76489? sn;

    public MdSoundUnitGwen(SegaMachine machine) : base(machine)
    {
        var t = machine.Timeline;
        psgClock = t.MasterHz / t.Psg.Divider;
        hostRate = machine.SampleRate;
        ready = true;
        OnReset();
    }

    private GwenYm2612 Opn => opn ??= new GwenYm2612();
    private GwenSn76489 Sn => sn ??= new GwenSn76489(psgClock, hostRate);

    protected override void OnYmWrite(int port, byte value) { if (ready) Opn.Write(port, value); }
    protected override void OnPsgWrite(byte value) { if (ready) Sn.Write(value); }

    protected override void OnFmSample(ref int left, ref int right)
    {
        if (!ready) return;
        int s = Opn.Sample();
        left = s; right = s;
    }

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
        opn = null; sn = null;
        _ = Opn; _ = Sn;
        ReplaySoundState();
    }
}
