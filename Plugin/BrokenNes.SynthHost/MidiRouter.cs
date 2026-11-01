namespace BrokenNes.SynthHost;

public enum MidiRouting
{
    /// <summary>MIDI channel 1-4 plays Pulse 1, Pulse 2, Triangle, Noise; other channels are ignored. (A keyboard that only sends on channel 1 plays Pulse 1.)</summary>
    ByMidiChannel,
    /// <summary>Whatever channel the message is on, it plays the selected rack channel (the tab you are looking at).</summary>
    AllToSelected,
}

/// <summary>Raw MIDI bytes to <see cref="SynthRack"/> events: note on/off, pitch bend, all notes off. Anything else is ignored.</summary>
public sealed class MidiRouter
{
    private readonly SynthRack rack;
    private long messages;

    public MidiRouter(SynthRack rack) { this.rack = rack; }

    public MidiRouting Routing { get; set; } = MidiRouting.ByMidiChannel;

    /// <summary>The rack channel that <see cref="MidiRouting.AllToSelected"/> and the computer keyboard play.</summary>
    public int Selected { get; set; }

    /// <summary>Messages handled so far (a counter for a status line and for tests).</summary>
    public long Messages => Interlocked.Read(ref messages);

    /// <summary>What the last handled message was, in words.</summary>
    public string LastEvent { get; private set; } = "";

    /// <summary>Other sources of notes (the computer keyboard) say what they did here, so one status line tells the whole story.</summary>
    public void Report(string what) => LastEvent = what;

    /// <summary>A short message as Windows delivers it: status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16.</summary>
    public void Handle(int raw) => Handle((byte)(raw & 0xFF), (byte)((raw >> 8) & 0x7F), (byte)((raw >> 16) & 0x7F));

    public void Handle(byte status, byte data1, byte data2)
    {
        if (status < 0x80 || status >= 0xF0) return;   // running-status data and system messages
        data1 &= 0x7F; data2 &= 0x7F;                  // MIDI data bytes are 7 bits; a stray high bit is dropped, not obeyed
        int kind = status & 0xF0, midiChannel = status & 0x0F;
        int target = Routing == MidiRouting.AllToSelected ? Selected : midiChannel < SynthRack.ChannelCount ? midiChannel : -1;
        if (target < 0) return;
        string name = SynthRack.ChannelNames[target];

        switch (kind)
        {
            case 0x90 when data2 > 0:
                rack.NoteOn(target, data1, data2 / 127f);
                LastEvent = $"{name}: note {data1} on, velocity {data2}";
                break;
            case 0x90:                       // note on with velocity 0 is a note off
            case 0x80:
                rack.NoteOff(target, data1);
                LastEvent = $"{name}: note {data1} off";
                break;
            case 0xE0:
                {
                    int value = data1 | (data2 << 7);   // 14 bits, centre 8192
                    float bend = (value - 8192) / (value >= 8192 ? 8191f : 8192f);
                    rack.PitchBend(target, bend);
                    LastEvent = $"{name}: pitch bend {bend:+0.00;-0.00;0.00}";
                    break;
                }
            case 0xB0 when data1 is 120 or 123:  // all sound off / all notes off
                rack.AllNotesOff(target);
                LastEvent = $"{name}: all notes off";
                break;
            default:
                return;
        }
        Interlocked.Increment(ref messages);
    }
}
