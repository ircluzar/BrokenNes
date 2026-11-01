// Foreign APU project unit (docs/projects/foreign-apu-project.md): the glue around sega/PicoYm2612.cs + PicoSn76496.cs (PicoDrive v1.85, non-commercial + MAME license); it may follow the original's
// driving code (clocking, mixing) as described in that engine file's header. A documented exception to the license policy: THIRD_PARTY_NOTICES.md section 2a.
using System;

namespace NesEmulator.Sega;

/// <summary>
/// Genesis sound unit <c>PICO</c>: the stock unit (Z80, YM2612 and PSG keep running for the game: status, timers, busy) with what is heard replaced by PicoDrive 1.85's
/// engines (<see cref="PicoYm2612"/>, <see cref="PicoSn76496"/>), run the way pico/sound/sound.c runs them: 44.1 kHz, one whole frame (735 / 882 samples) rendered at a time, so register
/// writes land on frame boundaries; the DAC is <c>(data - 0x80) &lt;&lt; 6</c> added to both sides while $2B bit 7 is set. Converted to the hooks' rates by zero-order hold.
/// Quick port; see the engine files for the license (PicoDrive non-commercial / MAME) and the kept quirks.
/// </summary>
public sealed class MdSoundUnitPico : MdSoundUnit, IGenesisSound
{
    private readonly double ymRate, ymClock, psgClock;
    private readonly int frameLen;
    private PicoYm2612? pym;
    private PicoSn76496? psn;
    private readonly int[] fmBuf, psgBuf;
    private int fmPos, psgPos;
    private double fmAcc, psgAcc;
    private int curL, curR, curPsg;

    public MdSoundUnitPico(SegaMachine machine) : base(machine)
    {
        var t = machine.Timeline;
        ymRate = t.Ym2612SampleRate;
        ymClock = t.MasterHz / 7;      // OSC/7
        psgClock = t.MasterHz / 15;    // OSC/15
        frameLen = PicoYm2612.Rate / (t.Region == SegaRegion.Pal ? 50 : 60);   // PsndLen (no fractional part at 44.1 kHz)
        fmBuf = new int[frameLen * 2];
        psgBuf = new int[frameLen];
        fmPos = psgPos = frameLen;
    }

    public new string CoreName => "PICO (PicoDrive 1.85 YM2612 + SN76496, " + base.CoreName + " underneath)";

    /// <summary>The engines, built on first use with the stock chips' registers replayed into them (PicoDrive's own state load also replays the register file).</summary>
    private PicoYm2612 Ym => pym ??= BuildYm();
    private PicoSn76496 Sn => psn ??= BuildSn();

    private PicoYm2612 BuildYm()
    {
        var e = new PicoYm2612((int)ymClock);
        var fm = Fm;
        void W(int reg, int v) { if (reg < 0x100) { e.Write(0, reg); e.Write(1, v); } else { e.Write(2, reg & 0xff); e.Write(3, v); } }
        W(0x22, fm.Register(0x22)); W(0x27, fm.Register(0x27));
        for (int part = 0; part < 0x200; part += 0x100)
        {
            for (int r = 0x30; r < 0xa0; r++) if ((r & 3) != 3) W(part | r, fm.Register(part | r));
            for (int c = 0; c < 3; c++) { W(part | 0xa4 + c, fm.Register(part | 0xa4 + c)); W(part | 0xa0 + c, fm.Register(part | 0xa0 + c)); }
            if (part == 0) for (int c = 0; c < 3; c++) { W(0xac + c, fm.Register(0xac + c)); W(0xa8 + c, fm.Register(0xa8 + c)); }
            for (int c = 0; c < 3; c++) { W(part | 0xb0 + c, fm.Register(part | 0xb0 + c)); W(part | 0xb4 + c, fm.Register(part | 0xb4 + c)); }
        }
        W(0x2a, fm.Register(0x2a)); W(0x2b, fm.Register(0x2b));
        e.Write(0, 0x2b);   // leave the address latch off $2A
        return e;
    }

    private PicoSn76496 BuildSn()
    {
        var e = new PicoSn76496(psgClock);
        var p = Psg;
        for (int c = 0; c < 3; c++)
        {
            e.Write(0x80 | c << 5 | p.Period(c) & 0x0f); e.Write(p.Period(c) >> 4 & 0x3f);
            e.Write(0x90 | c << 5 | p.Volume(c) & 0x0f);
        }
        e.Write(0xe0 | p.NoiseControl & 7); e.Write(0xf0 | p.Volume(3) & 0x0f);
        return e;
    }

    protected override void OnReset()
    {
        pym = null; psn = null;
        fmPos = psgPos = frameLen;
        if (fmBuf != null) curL = curR = curPsg = 0;
    }

    protected override void OnYmWrite(int port, byte value) => Ym.Write(port, value);

    protected override void OnPsgWrite(byte value) => Sn.Write(value);

    protected override void OnFmSample(ref int left, ref int right)
    {
        fmAcc += PicoYm2612.Rate;
        while (fmAcc >= ymRate)
        {
            fmAcc -= ymRate;
            if (fmPos >= frameLen) { Ym.Render(fmBuf, frameLen); fmPos = 0; }   // PsndRender: the whole frame with the registers as they are now
            curL = fmBuf[2 * fmPos]; curR = fmBuf[2 * fmPos + 1];
            fmPos++;
        }
        int dac = Ym.DacLevel;   // PsndDoDAC writes it into both sides (left copied to right by mix_32_to_16l_stereo)
        left = Clip(curL + dac);
        right = Clip(curR + dac);
    }

    protected override void OnPsgSamples(Span<short> stereo)
    {
        int host = SampleRate;
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            psgAcc += PicoSn76496.Rate;
            while (psgAcc >= host)
            {
                psgAcc -= host;
                if (psgPos >= frameLen) { Array.Clear(psgBuf); Sn.Update(psgBuf, 0, frameLen); psgPos = 0; }
                curPsg = psgBuf[psgPos++];
            }
            short v = (short)(curPsg > 32767 ? 32767 : curPsg);
            stereo[i] = v; stereo[i + 1] = v;
        }
    }

    private static int Clip(int v) => v > 32767 ? 32767 : v < -32768 ? -32768 : v;
}
