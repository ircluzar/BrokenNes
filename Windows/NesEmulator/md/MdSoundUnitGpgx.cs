using System;

namespace NesEmulator.Sega;

/// <summary>
/// The Genesis sound unit with Genesis Plus GX 1.3.0's (2008) sound at its defaults: the MAME fm.c 1.4 YM2612 with Eke-Eke's fixes (<see cref="GpgxYm2612"/>, the "hq_fm" setting: the chip's own
/// ~53 kHz rate) and Maxim's SN76489 (<see cref="GpgxSn76489"/>, rendered at the host rate, x1.5 as system.c's psg_preamp 150). MAME license + Genesis Plus terms (non-commercial) - a documented
/// exception, see THIRD_PARTY_NOTICES.md section 2. The Z80, the stock YM2612 (timers, busy, status) and the stock PSG run as in <see cref="MdSoundUnit"/>; what is heard comes from the GX engines,
/// fed the same writes, one GX FM sample per stock FM sample (both run at YM clock / 144). GX's default output filter (config.filter = 1: out = (prev + out) >> 1) is kept, applied to FM at
/// ~53 kHz and to the PSG at the host rate (GX ran it once on the mix at the output rate). Quick port: a state load or swap re-feeds the engines the stock chips' registers.
/// </summary>
public sealed class MdSoundUnitGpgx : MdSoundUnit
{
    private readonly int hostRate;
    private readonly double psgClock;
    private readonly bool ready;
    private GpgxYm2612? opn;
    private GpgxSn76489? sn;
    private int llF, rrF, llP;

    public MdSoundUnitGpgx(SegaMachine machine) : base(machine)
    {
        var t = machine.Timeline;
        psgClock = t.MasterHz / t.Psg.Divider;
        hostRate = machine.SampleRate;
        ready = true;
        OnReset();
    }

    private GpgxYm2612 Opn => opn ??= new GpgxYm2612();
    private GpgxSn76489 Sn => sn ??= new GpgxSn76489(psgClock, hostRate);

    protected override void OnYmWrite(int port, byte value) { if (ready) Opn.Write(port, value); }
    protected override void OnPsgWrite(byte value) { if (ready) Sn.Write(value); }

    protected override void OnFmSample(ref int left, ref int right)
    {
        if (!ready) return;
        Opn.Sample(out int l, out int r);
        l = (llF + l) >> 1; r = (rrF + r) >> 1; llF = l; rrF = r;   // GX's single-pole low-pass (config.filter)
        left = l; right = r;
    }

    protected override void OnPsgSamples(Span<short> stereo)
    {
        if (!ready) return;
        var p = Sn;
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            int s = p.Next() * 150 / 100;   // psg_preamp
            s = (llP + s) >> 1; llP = s;
            short o = (short)Math.Clamp(s, short.MinValue, short.MaxValue);
            stereo[i] = o; stereo[i + 1] = o;
        }
    }

    protected override void OnReset()
    {
        if (!ready) return;
        opn = null; sn = null; llF = rrF = llP = 0;
        var o = Opn;
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
        Sn.ReplayFrom(Psg);
    }
}
