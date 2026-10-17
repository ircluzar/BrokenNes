// Round 6 (presets): the NES preset player. The NES has no sampler (Instrument Runaway freezes and silences a NES game; its sound is not captured), so a NES preset is played the way Direct mode plays the NES chip: a bare chip of the plugin's own, driven by
// register writes, one per instance. What differs from Direct mode's channels is whose registers they are: the pulse duty, the envelope (decay or constant volume, loop, length), the sweep, the triangle's linear counter and the noise's mode and period are the
// PRESET's, as the game had them. The note's pitch and loudness are FL's (pitch writes the timer, velocity scales the output). Not played: the DMC (it reads its sample from the game's memory and the bare chip has no game), said on screen.
using BrokenNes2.Instruments;
using NesEmulator.Plugin;

namespace BrokenNes2;

internal sealed class NesPresetPlayer
{
    private readonly NesApuInstrument chip;
    private readonly InstrumentKind kind;
    private readonly int duty, halt, constant, volume, sweepOn, sweepPeriod, sweepNegate, sweepShift, length, control, linear, mode, period;
    private bool gate;
    private int curPeriod;

    public NesPresetPlayer(Instrument inst, int hostRate)
    {
        chip = new NesApuInstrument("FIX", hostRate);
        kind = inst.Kind;
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

    public void Render(Span<float> mono) => chip.Render(mono);
}
