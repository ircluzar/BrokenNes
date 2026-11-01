// Foreign APU project unit (docs/projects/foreign-apu-project.md): the glue around sega/GensYm2612.cs + GensPsg.cs (Gens 2.10, GPL-2.0); it may follow the original's
// driving code (clocking, mixing) as described in that engine file's header. A documented exception to the license policy: THIRD_PARTY_NOTICES.md section 2a.
using System;

namespace NesEmulator.Sega;

/// <summary>
/// The Genesis sound unit with Gens 2.10's sound (<see cref="GensYm2612"/> + <see cref="GensPsg"/>, Gens' defaults: 22,050 Hz,
/// no YM/PSG "improvement"), APU-menu id <c>GENS</c>. The stock Z80, YM2612 and PSG keep running (timers, busy, status for the
/// game); the hooks feed every YM/PSG write to the Gens chips and replace what is heard.
/// </summary>
/// <remarks>
/// Gens' frame loop is reproduced crudely: the FM sample clock (about 53 kHz) is cut into 262 (313 PAL: Gens uses 312) "scanlines"
/// per 1/60 (1/50) s; each scanline gets its <c>Sound_Extrapol</c> slice of a 368 (441) sample segment, the DAC and timers are
/// updated per slice, pending samples are rendered when a register write asks (<c>Special_Update</c>) or at frame end. Each
/// finished segment is clamped to 16 bits (FM + DAC only; Gens clamps FM + DAC + PSG together) and queued; the hooks read the
/// queues back by zero-order hold at their own rate (one Gens frame of latency). Gens' levels are halved so the FM sits near
/// the stock unit's level; Gens' FM:PSG balance is kept.
/// </remarks>
public sealed class MdSoundUnitGens : MdSoundUnit
{
    private const int GensRate = 22050;
    private const double ClockNtsc = 53693175, ClockPal = 53203424;

    private readonly SegaMachine machine;
    private GensYm2612? ym;
    private GensPsg? psg;

    // the frame driver
    private int lines, segLen, line;
    private int[] extOff = Array.Empty<int>(), extLen = Array.Empty<int>();
    private readonly int[] fmL = new int[1024], fmR = new int[1024], psL = new int[1024], psR = new int[1024];
    private double linePhase, linesPerFmSample, fmReadStep, fmReadPhase;
    private bool frameOpen;

    // output queues (interleaved L/R for FM, mono for PSG)
    private readonly Fifo fmQ = new(1 << 15), psgQ = new(1 << 15);
    private int holdL, holdR, holdP;
    private double psgReadPhase;

    public MdSoundUnitGens(SegaMachine machine) : base(machine)
    {
        this.machine = machine;
        Init();
    }

    private void Init()
    {
        var t = machine.Timeline;
        bool pal = t.Region == SegaRegion.Pal;
        double clock = pal ? ClockPal : ClockNtsc;
        ym = new GensYm2612((int)(clock / 7), GensRate);
        psg = new GensPsg((int)(clock / 15), GensRate);
        lines = pal ? 312 : 262;
        segLen = pal ? 441 : 368;
        int fps = pal ? 50 : 60;
        extOff = new int[lines]; extLen = new int[lines];
        for (int i = 0; i < lines; i++)
        {
            extOff[i] = segLen * i / lines;
            extLen[i] = segLen * (i + 1) / lines - extOff[i];
        }
        double fmRate = t.Ym2612SampleRate;
        linesPerFmSample = lines * fps / fmRate;
        fmReadStep = segLen * fps / fmRate;
        line = 0; linePhase = 0; fmReadPhase = 0; psgReadPhase = 0; frameOpen = false;
        fmQ.Clear(); psgQ.Clear(); holdL = holdR = holdP = 0;
        ym.BufL = fmL; ym.BufR = fmR; psg.BufL = psL; psg.BufR = psR;
    }

    private void StartFrame()
    {
        Array.Clear(fmL); Array.Clear(fmR); Array.Clear(psL); Array.Clear(psR);
        ym!.BufOff = 0; ym.Len = 0; psg!.BufOff = 0; psg.Len = 0;
        frameOpen = true;
    }

    private void EndFrame()
    {
        psg!.SpecialUpdate();
        ym!.SpecialUpdate();
        for (int i = 0; i < segLen; i++)
        {
            fmQ.Push(Math.Clamp(fmL[i], -0x7FFF, 0x7FFF) >> 1);
            fmQ.Push(Math.Clamp(fmR[i], -0x7FFF, 0x7FFF) >> 1);
            psgQ.Push(psL[i] >> 1);
        }
        // keep the queues near one frame deep (the FM clock and the host clock drift a little)
        while (fmQ.Count > 6 * segLen) { fmQ.Pop(); fmQ.Pop(); }
        while (psgQ.Count > 3 * segLen) psgQ.Pop();
        frameOpen = false;
    }

    /// <summary>One Gens scanline: the DAC and timers for its slice, and its samples become pending.</summary>
    private void Line()
    {
        if (line == lines) { EndFrame(); line = 0; }
        if (line == 0 && !frameOpen) StartFrame();
        ym!.DacAndTimersUpdate(fmL, fmR, extOff[line], extLen[line]);
        ym.Len += extLen[line];
        psg!.Len += extLen[line];
        line++;
    }

    protected override void OnReset()
    {
        if (ym == null) return;   // the base constructor's Reset: Init runs after it
        Init();
    }

    protected override void OnYmWrite(int port, byte value)
    {
        if (ym == null) return;
        if (!frameOpen) StartFrame();
        ym.Write(port, value);
    }

    protected override void OnPsgWrite(byte value)
    {
        if (psg == null) return;
        if (!frameOpen) StartFrame();
        psg.Write(value);
    }

    protected override void OnFmSample(ref int left, ref int right)
    {
        if (ym == null) return;
        linePhase += linesPerFmSample;
        while (linePhase >= 1) { linePhase -= 1; Line(); }

        fmReadPhase += fmReadStep;
        while (fmReadPhase >= 1)
        {
            fmReadPhase -= 1;
            if (fmQ.Count >= 2) { holdL = fmQ.Pop(); holdR = fmQ.Pop(); }
        }
        left = holdL; right = holdR;
    }

    protected override void OnPsgSamples(Span<short> stereo)
    {
        if (psg == null) return;
        double step = (double)segLen * (machine.Timeline.Region == SegaRegion.Pal ? 50 : 60) / SampleRate;
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            psgReadPhase += step;
            while (psgReadPhase >= 1)
            {
                psgReadPhase -= 1;
                if (psgQ.Count > 0) holdP = psgQ.Pop();
            }
            short s = (short)Math.Clamp(holdP, short.MinValue, short.MaxValue);
            stereo[i] = s; stereo[i + 1] = s;
        }
    }

    /// <summary>A plain int ring buffer.</summary>
    private sealed class Fifo
    {
        private readonly int[] a; private int head, count;
        public Fifo(int size) { a = new int[size]; }
        public int Count => count;
        public void Clear() { head = 0; count = 0; }
        public void Push(int v) { if (count == a.Length) return; a[(head + count) & (a.Length - 1)] = v; count++; }
        public int Pop() { int v = a[head]; head = (head + 1) & (a.Length - 1); count--; return v; }
    }
}
