using System;

namespace NesEmulator.Sega;

/// <summary>
/// The Genesis sound unit with MAME 0.36's (1999) sound: Tatsuyuki Satoh's fm.c 0.36e YM2612 path (<see cref="Mame036Opn2"/>) and Nicola Salmoria's sn76496.c (<see cref="Mame036Sn76496"/>),
/// both at MAME's default 22,050 Hz (MAME license, non-commercial - a documented exception, see THIRD_PARTY_NOTICES.md section 2). The Z80, the stock YM2612 (timers, busy, status) and the stock PSG
/// run as in <see cref="MdSoundUnit"/>; what is heard comes from the MAME engines, fed the same writes. Each stock 53 kHz FM sample takes the latest 22,050 Hz MAME sample (zero-order hold), the PSG's
/// host-rate samples are replaced the same way. Quick port: a state load or swap re-feeds the engines the stock chips' registers (notes resume at their next key-on).
/// </summary>
public sealed class MdSoundUnitM036 : MdSoundUnit
{
    private readonly int ymClock, hostRate;
    private readonly double fmRate, psgClock;
    private readonly bool ready;
    private Mame036Opn2? opn;
    private Mame036Sn76496? sn;
    private double fmPhase;
    private short heldL, heldR;
    private int frameCount;

    public MdSoundUnitM036(SegaMachine machine) : base(machine)
    {
        var t = machine.Timeline;
        ymClock = (int)(t.MasterHz / MasterPerYmClock);
        fmRate = t.Ym2612SampleRate;
        psgClock = t.MasterHz / t.Psg.Divider;
        hostRate = machine.SampleRate;
        ready = true;
        OnReset();
    }

    private Mame036Opn2 Opn => opn ??= new Mame036Opn2(ymClock);
    private Mame036Sn76496 Sn => sn ??= new Mame036Sn76496(psgClock);

    protected override void OnYmWrite(int port, byte value) { if (ready) Opn.Write(port, value); }
    protected override void OnPsgWrite(byte value) { if (ready) Sn.Write(value); }

    protected override void OnFmSample(ref int left, ref int right)
    {
        if (!ready) return;
        fmPhase += Mame036Opn2.DefaultRate;
        while (fmPhase >= fmRate)
        {
            fmPhase -= fmRate;
            // MAME flushed its streams at least once a frame: a new update chunk (and LFO step) every 1/60 s
            if (++frameCount >= Mame036Opn2.DefaultRate / 60) { frameCount = 0; Opn.Flush(); }
            Opn.Sample(out heldL, out heldR);
        }
        left = heldL; right = heldR;
    }

    protected override void OnPsgSamples(Span<short> stereo) { if (ready) Sn.FillHost(stereo, hostRate); }

    protected override void OnReset()
    {
        if (!ready) return;
        opn = null; sn = null; fmPhase = 0; heldL = heldR = 0; frameCount = 0;
        var o = Opn;
        // re-feed the stock chip's registers: modes, then the operator and channel registers of both parts (frequency high byte before low)
        o.Write(0, 0x22); o.Write(1, Fm.Register(0x22));
        o.Write(0, 0x27); o.Write(1, Fm.Register(0x27));
        o.Write(0, 0x2b); o.Write(1, Fm.Register(0x2b));
        for (int part = 0; part < 2; part++)
        {
            int ap = part * 2, dp = ap + 1, b = part * 0x100;
            for (int r = 0x30; r < 0xa0; r++) { o.Write(ap, r); o.Write(dp, Fm.Register(b + r)); }
            for (int r = 0xa4; r < 0xa7; r++) { o.Write(ap, r); o.Write(dp, Fm.Register(b + r)); o.Write(ap, r - 4); o.Write(dp, Fm.Register(b + r - 4)); }
            if (part == 0) for (int r = 0xac; r < 0xaf; r++) { o.Write(ap, r); o.Write(dp, Fm.Register(r)); o.Write(ap, r - 4); o.Write(dp, Fm.Register(r - 4)); }
            for (int r = 0xb0; r < 0xb7; r++) { o.Write(ap, r); o.Write(dp, Fm.Register(b + r)); }
        }
        foreach (var v in FmKeyOnValues()) { o.Write(0, 0x28); o.Write(1, v); }   // re-key the held notes (--apu-channels --state)
        Sn.ReplayFrom(Psg);
    }
}
