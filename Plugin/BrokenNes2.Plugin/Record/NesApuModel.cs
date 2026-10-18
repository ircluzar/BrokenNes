// Round 8 (record-nes): a model of the NES sound chip that follows a game's register writes and says, at any moment, what each channel SOUNDS like: whether it is audible, its period
// (after the hardware sweep moved it), the volume in force (the constant volume, or the decay envelope's level), the pulse duty, the noise's mode and period. It is event-driven (only the
// register writes and the frame sequencer's quarter / half frames change anything it models), so a whole recording runs through it in milliseconds; the waveforms themselves (duty position,
// triangle step, the noise's shift register) are not modelled: nothing the transcription needs depends on them.
//
// It follows APU_FIX, the plugin's accurate chip, rule for rule (APU_FIX.cs: WriteAPURegister, ClockFrameSequencer, QuarterFrameTick, HalfFrameTick, Sweep, ClockEnvelope, SweepWouldMute, the
// channels' mute conditions), and starts from the game chip's own counters (APU_FIX.ProbeControl) taken between two frames when a recording begins. The `nes-model` check feeds it the writes
// of real games and compares it with the game's chip (APU_FIX.ProbeApuState) at the end of every frame: it must agree on every field it models, every frame.
//
// Times are APU clocks (CPU cycles, 1,789,773 a second): a write at clock W happens after the cycles 0 .. W-1 were stepped, which is where the bus's write tap reports it.
using NesEmulator;

namespace BrokenNes2.Record;

public sealed class NesApuModel
{
    public static readonly int[] LengthTable = { 10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14, 12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30 };
    public static readonly int[] NoisePeriods = { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };
    public static readonly int[] DmcRates = { 428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54 };

    /// <summary>A pulse channel's registers and counters (APU_FIX's pulseN_* fields).</summary>
    public sealed class Pulse
    {
        public int Duty, Vol, SweepRaw, SweepPeriod = 1, SweepShift, SweepDivider, Timer, LengthIdx, Length, EnvDivider, EnvDecay;
        public bool Halt, Const, SweepNegate, SweepReload, Enabled, EnvStart;
        public readonly bool First;
        public Pulse(bool first) { First = first; }
        public int VolumeInForce => Const ? Vol : EnvDecay;
        public bool SweepMutes
        {
            get
            {
                if (SweepShift == 0) return false;
                int change = Timer >> SweepShift;
                int target = SweepNegate ? Timer - change - (First ? 1 : 0) : Timer + change;
                return target > 0x7FF || target < 8;
            }
        }
        /// <summary>Whether the channel makes a sound (APU_FIX.ClockPulse's mute conditions) at a volume above 0.</summary>
        public bool Audible => Enabled && Length > 0 && Timer >= 8 && !SweepMutes && VolumeInForce > 0;
    }

    public sealed class Triangle
    {
        public int LinearReg, Timer, LengthIdx, Length, Linear;
        public bool Halt, Enabled, LinearReload;
        /// <summary>The sequencer runs and the output is not the ultrasonic guard's (APU_FIX.ClockTriangle).</summary>
        public bool Audible => Enabled && Length > 0 && Linear > 0 && Timer >= 2;
    }

    public sealed class Noise
    {
        public int Vol, PeriodReg, LengthIdx, Length, EnvDivider, EnvDecay;
        public bool Halt, Const, Enabled, EnvStart;
        public int VolumeInForce => Const ? Vol : EnvDecay;
        public int Index => PeriodReg & 15;
        public bool Short => (PeriodReg & 0x80) != 0;
        public bool Audible => Enabled && Length > 0 && VolumeInForce > 0;
    }

    public readonly Pulse P1 = new(true), P2 = new(false);
    public readonly Triangle T = new();
    public readonly Noise N = new();
    public int DmcCtrl, DmcLoad, DmcAddrReg, DmcLenReg, Status;
    public bool DmcEnabled;
    /// <summary>The DMC sample in progress, as far as the model follows it: its start, the bytes it will play, and the clock its last byte is fetched (the model counts whole bytes at the rate in force at the start; a rate changed while it plays is counted by the transcription).</summary>
    public long DmcStart = -1, DmcEnd = -1;
    public int DmcBytes;

    // frame sequencer (APU_FIX: frameCycle, frameStep, nextFrameEventCycle, frameMode5, frameIRQInhibit)
    public int FrameCycle, FrameStep, NextEvent = 7457;
    public bool Mode5, IrqInhibit;
    /// <summary>Cycles stepped so far.</summary>
    public long Clock;
    /// <summary>What the last <see cref="StepToNextEvent"/> clocked: 1 a quarter frame, 2 a half frame (with its quarter), 0 nothing (the 5-step sequence's silent step).</summary>
    public int LastTick;

    public Pulse PulseOf(int c) => c == 0 ? P1 : P2;

    /// <summary>The model at power-on (APU_FIX's initial values).</summary>
    public NesApuModel() { }

    /// <summary>The model of a game's chip as APU_FIX had it (between two frames) at <paramref name="clock"/>.</summary>
    public static NesApuModel From(in APU_FIX.ApuControlSnapshot c, long clock)
    {
        var m = new NesApuModel();
        m.Set(c, clock);
        return m;
    }

    /// <summary>This model becomes the game chip's state (allocation-free: the live MIDI out does it on the audio thread, between frames).</summary>
    public void Set(in APU_FIX.ApuControlSnapshot c, long clock)
    {
        var m = this;
        m.Reset();
        m.Clock = clock;
        void P(Pulse p, int duty, bool halt, bool cnst, int vol, int raw, int per, bool neg, int shift, bool reload, int div, int timer, int li, int len, bool en, bool es, int ed, int dec)
        {
            p.Duty = duty; p.Halt = halt; p.Const = cnst; p.Vol = vol; p.SweepRaw = raw; p.SweepPeriod = per; p.SweepNegate = neg; p.SweepShift = shift; p.SweepReload = reload; p.SweepDivider = div;
            p.Timer = timer; p.LengthIdx = li; p.Length = len; p.Enabled = en; p.EnvStart = es; p.EnvDivider = ed; p.EnvDecay = dec;
        }
        P(m.P1, c.P1Duty, c.P1Halt, c.P1Const, c.P1Vol, c.P1SweepRaw, c.P1SweepPeriod, c.P1SweepNegate, c.P1SweepShift, c.P1SweepReload, c.P1SweepDivider, c.P1Timer, c.P1LengthIdx, c.P1Length, c.P1Enabled, c.P1EnvStart, c.P1EnvDivider, c.P1EnvDecay);
        P(m.P2, c.P2Duty, c.P2Halt, c.P2Const, c.P2Vol, c.P2SweepRaw, c.P2SweepPeriod, c.P2SweepNegate, c.P2SweepShift, c.P2SweepReload, c.P2SweepDivider, c.P2Timer, c.P2LengthIdx, c.P2Length, c.P2Enabled, c.P2EnvStart, c.P2EnvDivider, c.P2EnvDecay);
        m.T.LinearReg = c.TLinearReg; m.T.Halt = c.THalt; m.T.Timer = c.TTimer; m.T.LengthIdx = c.TLengthIdx; m.T.Length = c.TLength; m.T.Enabled = c.TEnabled; m.T.Linear = c.TLinear; m.T.LinearReload = c.TLinearReload;
        m.N.Halt = c.NHalt; m.N.Const = c.NConst; m.N.Vol = c.NVol; m.N.PeriodReg = c.NPeriodReg; m.N.LengthIdx = c.NLengthIdx; m.N.Length = c.NLength; m.N.Enabled = c.NEnabled; m.N.EnvStart = c.NEnvStart; m.N.EnvDivider = c.NEnvDivider; m.N.EnvDecay = c.NEnvDecay;
        m.DmcCtrl = c.DmcCtrl; m.DmcLoad = c.DmcLoad; m.DmcAddrReg = c.DmcAddrReg; m.DmcLenReg = c.DmcLenReg; m.DmcEnabled = c.DmcEnabled; m.Status = c.Status;
        if (c.DmcRemaining > 0) { m.DmcStart = clock; m.DmcBytes = c.DmcRemaining; m.DmcEnd = clock + (long)c.DmcRemaining * 8 * DmcRates[c.DmcCtrl & 15]; }
        m.FrameCycle = c.FrameCycle; m.FrameStep = c.FrameStep; m.NextEvent = c.NextFrameEvent; m.Mode5 = c.FrameMode5; m.IrqInhibit = c.FrameIrqInhibit;
    }

    // ---- saved form (a recording keeps the state it began from) ----

    public const int StateVersion = 1, StateInts = 80;

    /// <summary>The saved form of APU_FIX's state, written straight from its probe into <paramref name="dest"/> (allocation-free: the capture calls it on the audio thread). The same layout as
    /// <see cref="Save"/> of the model <see cref="From"/> makes (the `model` check holds the two to each other).</summary>
    public static void Fill(in APU_FIX.ApuControlSnapshot c, int[] dest)
    {
        int k = 0;
        dest[k++] = StateVersion;
        dest[k++] = c.P1Duty; dest[k++] = B(c.P1Halt); dest[k++] = B(c.P1Const); dest[k++] = c.P1Vol; dest[k++] = c.P1SweepRaw; dest[k++] = c.P1SweepPeriod; dest[k++] = B(c.P1SweepNegate); dest[k++] = c.P1SweepShift; dest[k++] = B(c.P1SweepReload); dest[k++] = c.P1SweepDivider;
        dest[k++] = c.P1Timer; dest[k++] = c.P1LengthIdx; dest[k++] = c.P1Length; dest[k++] = B(c.P1Enabled); dest[k++] = B(c.P1EnvStart); dest[k++] = c.P1EnvDivider; dest[k++] = c.P1EnvDecay;
        dest[k++] = c.P2Duty; dest[k++] = B(c.P2Halt); dest[k++] = B(c.P2Const); dest[k++] = c.P2Vol; dest[k++] = c.P2SweepRaw; dest[k++] = c.P2SweepPeriod; dest[k++] = B(c.P2SweepNegate); dest[k++] = c.P2SweepShift; dest[k++] = B(c.P2SweepReload); dest[k++] = c.P2SweepDivider;
        dest[k++] = c.P2Timer; dest[k++] = c.P2LengthIdx; dest[k++] = c.P2Length; dest[k++] = B(c.P2Enabled); dest[k++] = B(c.P2EnvStart); dest[k++] = c.P2EnvDivider; dest[k++] = c.P2EnvDecay;
        dest[k++] = c.TLinearReg; dest[k++] = B(c.THalt); dest[k++] = c.TTimer; dest[k++] = c.TLengthIdx; dest[k++] = c.TLength; dest[k++] = B(c.TEnabled); dest[k++] = c.TLinear; dest[k++] = B(c.TLinearReload);
        dest[k++] = B(c.NHalt); dest[k++] = B(c.NConst); dest[k++] = c.NVol; dest[k++] = c.NPeriodReg; dest[k++] = c.NLengthIdx; dest[k++] = c.NLength; dest[k++] = B(c.NEnabled); dest[k++] = B(c.NEnvStart); dest[k++] = c.NEnvDivider; dest[k++] = c.NEnvDecay;
        dest[k++] = c.DmcCtrl; dest[k++] = c.DmcLoad; dest[k++] = c.DmcAddrReg; dest[k++] = c.DmcLenReg; dest[k++] = B(c.DmcEnabled); dest[k++] = c.Status;
        dest[k++] = c.DmcRemaining > 0 ? c.DmcRemaining : 0; dest[k++] = c.DmcRemaining > 0 ? (int)Math.Min(int.MaxValue, (long)c.DmcRemaining * 8 * DmcRates[c.DmcCtrl & 15]) : -1;
        dest[k++] = c.FrameCycle; dest[k++] = c.FrameStep; dest[k++] = c.NextFrameEvent; dest[k++] = B(c.FrameMode5); dest[k++] = B(c.FrameIrqInhibit);
        while (k < dest.Length) dest[k++] = 0;
    }

    /// <summary>The saved form of a chip that has no state probe (another core than FIX): a chip at power-on given the registers the game last wrote ($4015, then $4000-$4013, then $4017), so the
    /// notes already sounding begin as if just struck. Approximate, and said so (<see cref="NesSession.BaseExact"/>).</summary>
    public static void FillFromLatch(byte[] latch, uint written, int[] dest, NesApuModel scratch)
    {
        scratch.SetFromLatch(latch, written, 0);
        scratch.SaveInto(dest);
    }

    /// <summary>This model becomes a chip at power-on given the registers last written (see <see cref="FillFromLatch"/>). Allocation-free.</summary>
    public void SetFromLatch(byte[] latch, uint written, long clock)
    {
        Reset();
        Clock = clock;
        if ((written >> 0x15 & 1) != 0) Write(0x15, latch[0x15]);
        for (int r = 0; r < 0x14; r++) if ((written >> r & 1) != 0) Write(r, latch[r]);
        if ((written >> 0x17 & 1) != 0) Write(0x17, latch[0x17]);
    }

    public int[] Save() { var s = new int[StateInts]; SaveInto(s); return s; }

    /// <summary>The saved form into <paramref name="s"/> (<see cref="StateInts"/> long). Allocation-free.</summary>
    public void SaveInto(int[] s)
    {
        int k = 1;
        s[0] = StateVersion;
        SavePulse(P1, s, ref k);
        SavePulse(P2, s, ref k);
        s[k++] = T.LinearReg; s[k++] = B(T.Halt); s[k++] = T.Timer; s[k++] = T.LengthIdx; s[k++] = T.Length; s[k++] = B(T.Enabled); s[k++] = T.Linear; s[k++] = B(T.LinearReload);
        s[k++] = B(N.Halt); s[k++] = B(N.Const); s[k++] = N.Vol; s[k++] = N.PeriodReg; s[k++] = N.LengthIdx; s[k++] = N.Length; s[k++] = B(N.Enabled); s[k++] = B(N.EnvStart); s[k++] = N.EnvDivider; s[k++] = N.EnvDecay;
        s[k++] = DmcCtrl; s[k++] = DmcLoad; s[k++] = DmcAddrReg; s[k++] = DmcLenReg; s[k++] = B(DmcEnabled); s[k++] = Status; s[k++] = DmcBytes; s[k++] = DmcEnd >= 0 ? (int)Math.Clamp(DmcEnd - Clock, 0, int.MaxValue) : -1;       // (cycles until the sample in progress runs out, from the state's own clock)
        s[k++] = FrameCycle; s[k++] = FrameStep; s[k++] = NextEvent; s[k++] = B(Mode5); s[k++] = B(IrqInhibit);
        while (k < s.Length) s[k++] = 0;
    }

    private static void SavePulse(Pulse p, int[] s, ref int k)
    {
        s[k++] = p.Duty; s[k++] = B(p.Halt); s[k++] = B(p.Const); s[k++] = p.Vol; s[k++] = p.SweepRaw; s[k++] = p.SweepPeriod; s[k++] = B(p.SweepNegate); s[k++] = p.SweepShift; s[k++] = B(p.SweepReload); s[k++] = p.SweepDivider;
        s[k++] = p.Timer; s[k++] = p.LengthIdx; s[k++] = p.Length; s[k++] = B(p.Enabled); s[k++] = B(p.EnvStart); s[k++] = p.EnvDivider; s[k++] = p.EnvDecay;
    }

    private static int B(bool b) => b ? 1 : 0;

    private static void ResetPulse(Pulse p)
    {
        p.Duty = p.Vol = p.SweepRaw = p.SweepShift = p.SweepDivider = p.Timer = p.LengthIdx = p.Length = p.EnvDivider = p.EnvDecay = 0;
        p.SweepPeriod = 1; p.Halt = p.Const = p.SweepNegate = p.SweepReload = p.Enabled = p.EnvStart = false;
    }

    /// <summary>Back to power-on (APU_FIX's initial values), keeping the object (the capture reuses one on the audio thread).</summary>
    public void Reset()
    {
        ResetPulse(P1);
        ResetPulse(P2);
        T.LinearReg = T.Timer = T.LengthIdx = T.Length = T.Linear = 0; T.Halt = T.Enabled = T.LinearReload = false;
        N.Vol = N.PeriodReg = N.LengthIdx = N.Length = N.EnvDivider = N.EnvDecay = 0; N.Halt = N.Const = N.Enabled = N.EnvStart = false;
        DmcCtrl = DmcLoad = DmcAddrReg = DmcLenReg = Status = DmcBytes = 0; DmcEnabled = false; DmcStart = DmcEnd = -1;
        FrameCycle = FrameStep = 0; NextEvent = 7457; Mode5 = IrqInhibit = false; Clock = 0; LastTick = 0;
    }

    /// <summary>The model a saved state describes, at clock 0; null when the ints are not one (every value range-checked: a damaged recording must be refused, never half trusted).</summary>
    public static NesApuModel? Load(int[] s)
    {
        if (s.Length != StateInts || s[0] != StateVersion) return null;
        var m = new NesApuModel();
        int k = 1;
        bool ok = true;
        int R(int lo, int hi) { int v = s[k++]; if (v < lo || v > hi) ok = false; return v; }
        bool Bo() => R(0, 1) == 1;
        foreach (var p in new[] { m.P1, m.P2 })
        {
            p.Duty = R(0, 3); p.Halt = Bo(); p.Const = Bo(); p.Vol = R(0, 15); p.SweepRaw = R(0, 255); p.SweepPeriod = R(1, 8); p.SweepNegate = Bo(); p.SweepShift = R(0, 7); p.SweepReload = Bo(); p.SweepDivider = R(-1, 8);
            p.Timer = R(0, 0x7FF); p.LengthIdx = R(0, 31); p.Length = R(0, 254); p.Enabled = Bo(); p.EnvStart = Bo(); p.EnvDivider = R(-1, 16); p.EnvDecay = R(0, 15);
        }
        m.T.LinearReg = R(0, 255); m.T.Halt = Bo(); m.T.Timer = R(0, 0x7FF); m.T.LengthIdx = R(0, 31); m.T.Length = R(0, 254); m.T.Enabled = Bo(); m.T.Linear = R(0, 127); m.T.LinearReload = Bo();
        m.N.Halt = Bo(); m.N.Const = Bo(); m.N.Vol = R(0, 15); m.N.PeriodReg = R(0, 255); m.N.LengthIdx = R(0, 31); m.N.Length = R(0, 254); m.N.Enabled = Bo(); m.N.EnvStart = Bo(); m.N.EnvDivider = R(-1, 16); m.N.EnvDecay = R(0, 15);
        m.DmcCtrl = R(0, 255); m.DmcLoad = R(0, 255); m.DmcAddrReg = R(0, 255); m.DmcLenReg = R(0, 255); m.DmcEnabled = Bo(); m.Status = R(0, 255); m.DmcBytes = R(0, 4081);
        int left = R(-1, int.MaxValue);
        if (left >= 0) { m.DmcStart = 0; m.DmcEnd = left; } else { m.DmcStart = m.DmcEnd = -1; }
        m.FrameCycle = R(-1, 40000); m.FrameStep = R(0, 4); m.NextEvent = R(7457, 37281); m.Mode5 = Bo(); m.IrqInhibit = Bo();
        if (m.FrameCycle > m.NextEvent) ok = false;
        return ok ? m : null;
    }

    // ---- time ----

    /// <summary>Steps the model up to the next frame-sequencer event if it falls before clock <paramref name="limit"/> (a write at <paramref name="limit"/> comes after it): true and the event clocked
    /// (<see cref="Clock"/> is then one past it, <see cref="LastTick"/> says what it was); false when there is none before the limit (the model is then at the limit).</summary>
    public bool StepToNextEvent(long limit)
    {
        long at = Clock + (NextEvent - FrameCycle);
        if (at >= limit)
        {
            if (limit > Clock) { FrameCycle += (int)(limit - Clock); Clock = limit; }
            return false;
        }
        FrameCycle = NextEvent;
        Clock = at;
        LastTick = 0;
        while (FrameCycle >= NextEvent)
        {
            if (!Mode5)
            {
                switch (FrameStep)
                {
                    case 0: Quarter(); NextEvent = 14913; break;
                    case 1: Quarter(); Half(); NextEvent = 22371; break;
                    case 2: Quarter(); NextEvent = 29829; break;
                    case 3: Quarter(); Half(); FrameStep = -1; FrameCycle -= 29830; NextEvent = 7457; break;
                }
            }
            else
            {
                switch (FrameStep)
                {
                    case 0: Quarter(); NextEvent = 14913; break;
                    case 1: Quarter(); Half(); NextEvent = 22371; break;
                    case 2: Quarter(); NextEvent = 29829; break;
                    case 3: NextEvent = 37281; break;
                    case 4: Quarter(); Half(); FrameStep = -1; FrameCycle -= 37282; NextEvent = 7457; break;
                }
            }
            FrameStep++;
        }
        FrameCycle++;
        Clock++;
        return true;
    }

    private void Quarter()
    {
        Envelope(ref P1.EnvStart, ref P1.EnvDivider, ref P1.EnvDecay, P1.Vol, P1.Halt);
        Envelope(ref P2.EnvStart, ref P2.EnvDivider, ref P2.EnvDecay, P2.Vol, P2.Halt);
        Envelope(ref N.EnvStart, ref N.EnvDivider, ref N.EnvDecay, N.Vol, N.Halt);
        if (T.LinearReload) T.Linear = T.LinearReg & 0x7F;
        else if (T.Linear > 0) T.Linear--;
        if ((T.LinearReg & 0x80) == 0) T.LinearReload = false;
        if (LastTick < 1) LastTick = 1;
    }

    private void Half()
    {
        if (!P1.Halt && P1.Length > 0) P1.Length--;
        if (!P2.Halt && P2.Length > 0) P2.Length--;
        if (!T.Halt && T.Length > 0) T.Length--;
        if (!N.Halt && N.Length > 0) N.Length--;
        Sweep(P1);
        Sweep(P2);
        LastTick = 2;
    }

    private static void Sweep(Pulse p)
    {
        if (NesModelMutants.On(NesModelMutants.SweepIgnored)) return;
        if (p.SweepDivider <= 0 && (p.SweepRaw & 0x80) != 0 && p.SweepShift != 0)
        {
            int change = p.Timer >> p.SweepShift;
            int target = p.SweepNegate ? p.Timer - change - (p.First ? 1 : 0) : p.Timer + change;
            if (p.Timer >= 8 && target <= 0x7FF) p.Timer = Math.Max(0, target);
        }
        if (p.SweepDivider <= 0 || p.SweepReload) { p.SweepDivider = Math.Max(0, p.SweepPeriod - 1); p.SweepReload = false; }
        else p.SweepDivider--;
    }

    private static void Envelope(ref bool start, ref int divider, ref int decay, int vol, bool loop)
    {
        if (start) { start = false; decay = 15; divider = vol + 1; return; }
        if (--divider <= 0)
        {
            divider = vol + 1;
            if (decay > 0) decay -= NesModelMutants.On(NesModelMutants.EnvelopeTwice) && decay > 1 ? 2 : 1;
            else if (loop) decay = 15;
        }
    }

    // ---- writes ----

    /// <summary>A register write ($4000 + <paramref name="reg"/>) at the model's clock. Returns true for a channel's fourth register (a "trigger": the waveform restarts, the envelope starts again and the length is loaded).</summary>
    public bool Write(int reg, int v)
    {
        switch (reg)
        {
            case 0x00: case 0x04:
            {
                var p = PulseOf(reg >> 2);
                p.Duty = v >> 6 & 3; p.Halt = (v & 0x20) != 0; p.Const = (v & 0x10) != 0; p.Vol = v & 15;
                return false;
            }
            case 0x01: case 0x05:
            {
                var p = PulseOf(reg >> 2);
                p.SweepRaw = v; p.SweepPeriod = (v >> 4 & 7) + 1; p.SweepNegate = (v & 8) != 0; p.SweepShift = v & 7; p.SweepReload = true;
                return false;
            }
            case 0x02: case 0x06: { var p = PulseOf(reg >> 2); p.Timer = p.Timer & 0x700 | v; return false; }
            case 0x03: case 0x07:
            {
                var p = PulseOf(reg >> 2);
                p.Timer = p.Timer & 0xFF | (v & 7) << 8; p.LengthIdx = v >> 3 & 31;
                if (p.Enabled) p.Length = LengthTable[p.LengthIdx];
                p.EnvStart = true;
                return true;
            }
            case 0x08: T.LinearReg = v; T.Halt = (v & 0x80) != 0; return false;
            case 0x0A: T.Timer = T.Timer & 0x700 | v; return false;
            case 0x0B:
                T.Timer = T.Timer & 0xFF | (v & 7) << 8; T.LengthIdx = v >> 3 & 31;
                if (T.Enabled) T.Length = LengthTable[T.LengthIdx];
                T.LinearReload = true;
                return true;
            case 0x0C: N.Halt = (v & 0x20) != 0; N.Const = (v & 0x10) != 0; N.Vol = v & 15; return false;
            case 0x0E: N.PeriodReg = v; return false;
            case 0x0F:
                N.LengthIdx = v >> 3 & 31;
                if (N.Enabled) N.Length = LengthTable[N.LengthIdx];
                N.EnvStart = true;
                return true;
            case 0x10: DmcCtrl = v; return false;
            case 0x11: DmcLoad = v; return false;
            case 0x12: DmcAddrReg = v; return false;
            case 0x13: DmcLenReg = v; return false;
            case 0x15:
            {
                Status = v;
                P1.Enabled = (v & 1) != 0; if (!P1.Enabled) P1.Length = 0;
                P2.Enabled = (v & 2) != 0; if (!P2.Enabled) P2.Length = 0;
                T.Enabled = (v & 4) != 0; if (!T.Enabled) T.Length = 0;
                N.Enabled = (v & 8) != 0; if (!N.Enabled) N.Length = 0;
                bool dmc = (v & 0x10) != 0;
                if (dmc) { if (!DmcPlaying) StartDmc(); }
                else { DmcStart = DmcEnd = -1; DmcBytes = 0; }
                DmcEnabled = dmc;
                return false;
            }
            case 0x17:
                Mode5 = (v & 0x80) != 0; IrqInhibit = (v & 0x40) != 0;
                FrameCycle = 0; FrameStep = 0; NextEvent = 7457;
                if (Mode5) { LastTick = 0; Quarter(); Half(); }
                return false;
        }
        return false;
    }

    /// <summary>A DMC sample is being fetched (the model's count of its bytes has not run out).</summary>
    public bool DmcPlaying => DmcEnd >= 0 && Clock < DmcEnd;

    public int DmcSampleAddress => 0xC000 + (DmcAddrReg << 6);
    public int DmcSampleLength => (DmcLenReg << 4) + 1;

    private void StartDmc()
    {
        DmcBytes = DmcSampleLength;
        DmcStart = Clock;
        DmcEnd = Clock + (long)DmcBytes * 8 * DmcRates[DmcCtrl & 15];
    }

    /// <summary>A looping DMC sample that ran out at <see cref="DmcEnd"/> starts again there (the chip restarts it without a write): call when the model's clock passes <see cref="DmcEnd"/>. True when it restarted.</summary>
    public bool LoopDmc()
    {
        if (DmcEnd < 0 || Clock < DmcEnd || (DmcCtrl & 0x40) == 0) return false;
        long at = DmcEnd;
        DmcBytes = DmcSampleLength;
        DmcStart = at;
        DmcEnd = at + (long)DmcBytes * 8 * DmcRates[DmcCtrl & 15];
        return true;
    }

    /// <summary>The fields the model and APU_FIX's state probe share, compared (the `nes-model` check): null when they agree, else the first that differs, in words.</summary>
    public string? Differs(in ApuStateSnapshot s)
    {
        if (P1.Timer != s.Pulse1Period) return $"pulse 1 period {P1.Timer:X3} (chip {s.Pulse1Period:X3})";
        if (P2.Timer != s.Pulse2Period) return $"pulse 2 period {P2.Timer:X3} (chip {s.Pulse2Period:X3})";
        if (P1.VolumeInForce != s.Pulse1Volume) return $"pulse 1 volume {P1.VolumeInForce} (chip {s.Pulse1Volume})";
        if (P2.VolumeInForce != s.Pulse2Volume) return $"pulse 2 volume {P2.VolumeInForce} (chip {s.Pulse2Volume})";
        if (Math.Min(P1.Length, 255) != s.Pulse1Length) return $"pulse 1 length {P1.Length} (chip {s.Pulse1Length})";
        if (Math.Min(P2.Length, 255) != s.Pulse2Length) return $"pulse 2 length {P2.Length} (chip {s.Pulse2Length})";
        if (P1.Duty != s.Pulse1Duty || P2.Duty != s.Pulse2Duty) return $"duty {P1.Duty}/{P2.Duty} (chip {s.Pulse1Duty}/{s.Pulse2Duty})";
        if (T.Timer != s.TrianglePeriod) return $"triangle period {T.Timer:X3} (chip {s.TrianglePeriod:X3})";
        if (T.Length != s.TriangleLength) return $"triangle length {T.Length} (chip {s.TriangleLength})";
        if (T.Linear != s.TriangleLinear) return $"triangle linear counter {T.Linear} (chip {s.TriangleLinear})";
        if (NoisePeriods[N.Index] != s.NoisePeriod || N.Short != s.NoiseMode) return $"noise period {N.Index}/{(N.Short ? "short" : "long")} (chip {s.NoisePeriod} cycles, {(s.NoiseMode ? "short" : "long")})";
        if (N.VolumeInForce != s.NoiseVolume) return $"noise volume {N.VolumeInForce} (chip {s.NoiseVolume})";
        if (N.Length != s.NoiseLength) return $"noise length {N.Length} (chip {s.NoiseLength})";
        if (P1.Enabled != s.Pulse1Enabled || P2.Enabled != s.Pulse2Enabled || T.Enabled != s.TriangleEnabled || N.Enabled != s.NoiseEnabled) return "the channels enabled ($4015)";
        if (P1.Halt != s.Pulse1Halt || P2.Halt != s.Pulse2Halt || T.Halt != s.TriangleHalt || N.Halt != s.NoiseHalt || P1.Const != s.Pulse1ConstVol || P2.Const != s.Pulse2ConstVol || N.Const != s.NoiseConstVol) return "a halt or constant-volume flag";
        if (Mode5 != s.FrameMode5 || (FrameStep & 7) != s.FrameStep) return $"frame sequencer step {FrameStep} {(Mode5 ? "5-step" : "4-step")} (chip {s.FrameStep} {(s.FrameMode5 ? "5-step" : "4-step")})";
        return null;
    }
}

/// <summary>Test-only deliberate bugs of the model (the `nes-model` check must catch each; 0 = the real code).</summary>
public static class NesModelMutants
{
    public static int Active;
    public const int SweepIgnored = 1, EnvelopeTwice = 2;
    public static bool On(int m) => Active == m;
}
