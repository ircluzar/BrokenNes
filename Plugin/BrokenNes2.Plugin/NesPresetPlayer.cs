// Round 6 (presets): the NES preset player. The NES has no sampler (Instrument Runaway freezes and silences a NES game; its sound is not captured), so a NES preset is played the way Direct mode plays the NES chip: a bare chip of the plugin's own, driven by
// register writes, one per instance. What differs from Direct mode's channels is whose registers they are: the pulse duty, the envelope (decay or constant volume, loop, length), the sweep, the triangle's linear counter and the noise's mode and period are the
// PRESET's, as the game had them. The note's pitch and loudness are FL's (pitch writes the timer, velocity scales the output). Not played: the DMC (it reads its sample from the game's memory and the bare chip has no game), said on screen.
using BrokenNes2.Instruments;
using BrokenNes2.Record;
using NesEmulator.Plugin;

namespace BrokenNes2;

internal sealed class NesPresetPlayer
{
    private readonly NesApuInstrument chip;
    private readonly InstrumentKind kind;
    private readonly int duty, halt, constant, volume, sweepOn, sweepPeriod, sweepNegate, sweepShift, length, control, linear, mode, period;
    private bool gate;
    private int curPeriod;

    /// <summary>A preset recorded from a game (its note block says so, PresetNote.NesTrack): FL's notes write the registers the way Direct mode does (Record/NesDirect.cs: velocity x controller 7 is the
    /// volume register, constant volume, no sweep), with the preset's duty or noise mode, so a recording's notes write the game's registers back. The plain preset's own volume and envelope are not used then.</summary>
    public readonly bool Track;
    private readonly int channel;

    /// <summary>The bare chip it plays (tests read its writes through <c>WriteTap</c>).</summary>
    public NesApuInstrument Chip => chip;

    /// <summary>A preset extracted from a game (PresetNote.Captured): the captured life plays frame by frame from FL's note: level (constant volume), duty / noise mode and the pitch relative to
    /// the key-on, the note silent after a key-off the game made (Silenced); held on its last frame otherwise.</summary>
    public readonly BrokenNes2.Extract.CapturedNote? Envelope;
    private long envSamples;
    private const double FrameHz = 60.0988;

    public NesPresetPlayer(Instrument inst, int hostRate, bool track = false, BrokenNes2.Extract.CapturedNote? envelope = null)
    {
        Envelope = !track && envelope != null && envelope.Console == ConsoleId.Nes && envelope.Channel <= 3 && envelope.Frames > 0 ? envelope : null;
        chip = new NesApuInstrument("FIX", hostRate);
        kind = inst.Kind;
        Track = track && kind is InstrumentKind.NesPulse1 or InstrumentKind.NesPulse2 or InstrumentKind.NesTriangle or InstrumentKind.NesNoise;
        channel = kind switch { InstrumentKind.NesPulse1 => 0, InstrumentKind.NesPulse2 => 1, InstrumentKind.NesTriangle => 2, _ => 3 };
        int Get(string id) => inst.Field(id)?.Value ?? 0;
        duty = Get("duty"); halt = Get("halt"); constant = Get("const"); volume = Get("vol");
        sweepOn = Get("sweep.on"); sweepPeriod = Get("sweep.period"); sweepNegate = Get("sweep.neg"); sweepShift = Get("sweep.shift");
        length = Get("length"); control = Get("control"); linear = Get("linear"); mode = Get("mode"); period = Get("period");
    }

    public int HostRate { get => chip.HostSampleRate; set { if (chip.HostSampleRate != value) chip.HostSampleRate = value; } }

    private void W(int reg, int value)
    {
        if (chip.Shadow(reg) != (byte)value) chip.Write(reg, (byte)value);
    }

    private int PulseBase => kind == InstrumentKind.NesPulse2 ? 4 : 0;

    /// <summary>The control-rate step: <paramref name="cents"/> is FL's pitch (0 = middle C), <paramref name="fresh"/> a note that has just started, <paramref name="hasNote"/> whether a note is held.</summary>
    public void Update(bool hasNote, bool fresh, double cents)
    {
        if (!hasNote)
        {
            if (gate)
            {
                switch (kind)
                {
                    case InstrumentKind.NesPulse1: case InstrumentKind.NesPulse2: W(PulseBase, duty << 6 | 0x30); break;
                    case InstrumentKind.NesTriangle: W(8, 0x80); break;
                    default: W(0x0C, 0x30); break;
                }
                gate = false;
            }
            return;
        }
        if (Envelope != null) { UpdateEnvelope(fresh, cents); return; }
        double freq = 440.0 * Math.Pow(2.0, (60.0 + cents / 100.0 - 69.0) / 12.0);
        bool retrigger = fresh || !gate;
        switch (kind)
        {
            case InstrumentKind.NesPulse1:
            case InstrumentKind.NesPulse2:
            {
                int b = PulseBase;
                int t = (int)Math.Round(Bn2Plugin.CpuHz / (16.0 * freq) - 1.0);
                if (t > 0x7FF) t = 0x7FF;
                bool muted = t < 8;                                   // the pulse channel mutes below period 8
                t = Math.Max(t, 8);
                W(b, duty << 6 | halt << 5 | constant << 4 | (muted ? 0 : volume));
                W(b + 1, sweepOn << 7 | sweepPeriod << 4 | sweepNegate << 3 | sweepShift);
                int hi = t >> 8 & 7, lo = t & 0xFF;
                if (retrigger || hi != (curPeriod >> 8 & 7)) { W(b + 2, lo); chip.Write(b + 3, (byte)(hi | length << 3)); }     // a $4003 write restarts the waveform and the envelope: only on a new note or a high-bit change
                else W(b + 2, lo);
                curPeriod = t;
                break;
            }
            case InstrumentKind.NesTriangle:
            {
                int t = Math.Clamp((int)Math.Round(Bn2Plugin.CpuHz / (32.0 * freq) - 1.0), 2, 0x7FF);
                W(8, control << 7 | linear);
                int hi = t >> 8 & 7, lo = t & 0xFF;
                if (retrigger || hi != (curPeriod >> 8 & 7)) { W(0x0A, lo); chip.Write(0x0B, (byte)(hi | length << 3)); }
                else W(0x0A, lo);
                curPeriod = t;
                break;
            }
            default:
            {
                // the noise's pitch is its period index; FL's pitch moves it a step for every three semitones from middle C (higher notes, shorter periods)
                int idx = Math.Clamp(period - (int)Math.Round(cents / 300.0), 0, 15);
                W(0x0C, halt << 5 | constant << 4 | volume);
                W(0x0E, mode << 7 | idx);
                if (retrigger) chip.Write(0x0F, (byte)(length << 3));
                curPeriod = idx;
                break;
            }
        }
        gate = true;
    }

    /// <summary>The control-rate step of a recorded track (<see cref="Track"/>): Direct mode's registers for the note held (<paramref name="level"/> = NesDirect.Level of FL's level and the Volume
    /// parameter), or the channel silent. Returns the level written (0 for a muted pulse).</summary>
    public int UpdateTrack(bool hasNote, bool fresh, double cents, int level)
    {
        if (!hasNote)
        {
            if (gate) { NesDirect.Silence(chip, channel, duty); gate = false; }
            return 0;
        }
        bool retrigger = fresh || !gate;
        curPeriod = NesDirect.Write(chip, channel, duty, mode, ref level, cents, retrigger, curPeriod);
        gate = true;
        return level;
    }

    /// <summary>The frame of the captured life the note is on (from the samples rendered since its key-on).</summary>
    public int EnvelopeFrame => (int)(envSamples * FrameHz / Math.Max(1, chip.HostSampleRate));

    private void UpdateEnvelope(bool fresh, double cents)
    {
        var e = Envelope!;
        bool retrigger = fresh || !gate;
        if (retrigger) envSamples = 0;
        int i = EnvelopeFrame;
        bool over = i >= e.Frames;
        var f = e.Life[Math.Min(i, e.Frames - 1)];
        int level = over && e.End == BrokenNes2.Extract.CaptureEnd.Silenced ? 0 : f.Level;
        double c = cents + e.CentsAt(Math.Min(i, e.Frames - 1));
        double freq = 440.0 * Math.Pow(2.0, (60.0 + c / 100.0 - 69.0) / 12.0);
        switch (kind)
        {
            case InstrumentKind.NesPulse1:
            case InstrumentKind.NesPulse2:
            {
                int b = PulseBase;
                int t = (int)Math.Round(Bn2Plugin.CpuHz / (16.0 * freq) - 1.0);
                if (t > 0x7FF) t = 0x7FF;
                bool muted = t < 8;
                t = Math.Max(t, 8);
                W(b, (f.Duty & 3) << 6 | 0x30 | (muted ? 0 : level));
                W(b + 1, 0x08);                                       // sweep off (the captured periods already carry it)
                int hi = t >> 8 & 7, lo = t & 0xFF;
                if (retrigger || hi != (curPeriod >> 8 & 7)) { W(b + 2, lo); chip.Write(b + 3, (byte)(hi | 31 << 3)); }
                else W(b + 2, lo);
                curPeriod = t;
                break;
            }
            case InstrumentKind.NesTriangle:
            {
                int t = Math.Clamp((int)Math.Round(Bn2Plugin.CpuHz / (32.0 * freq) - 1.0), 2, 0x7FF);
                bool wasOff = chip.Shadow(8) == 0x80;
                W(8, level > 0 ? 0xFF : 0x80);
                int hi = t >> 8 & 7, lo = t & 0xFF;
                if (retrigger || hi != (curPeriod >> 8 & 7) || (level > 0 && wasOff)) { W(0x0A, lo); chip.Write(0x0B, (byte)(hi | 31 << 3)); }
                else W(0x0A, lo);
                curPeriod = t;
                break;
            }
            default:
            {
                int idx = Math.Clamp((f.Period & 15) - (int)Math.Round(cents / 300.0), 0, 15);
                W(0x0C, 0x30 | level);
                W(0x0E, (f.Duty & 1) << 7 | idx);
                if (retrigger) chip.Write(0x0F, 31 << 3);
                curPeriod = idx;
                break;
            }
        }
        gate = true;
    }

    public void Render(Span<float> mono) { chip.Render(mono); envSamples += mono.Length; }
}
