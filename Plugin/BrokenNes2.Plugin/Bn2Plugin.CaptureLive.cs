// Round 9 (capture): "Send my notes and pitch bends as MIDI out" (the Test MIDI export window, off by default, remembered): each instance forwards the notes FL plays on it, with the pitch it plays as
// pitch bends, as MIDI out (MIDIOut_Delayed: FL sends them when its MIDI clock reaches the block they belong to), on the MIDI channel its role has in a capture (Pulse 1 -> 1, Pulse 2 -> 2,
// Triangle -> 3, Noise -> 10, others 5-9), port 1, pitch-bend range 48 semitones (RPN 0 before the first note on each channel). The plugin has had FPF_MIDIOut since round 8; the SDK's words for
// that flag: "plugin will send MIDI out messages (only those will be enabled when rendering to a MIDI file)". What FL does with them is NOT known (FL could not be run here): other FL channels
// whose MIDI input port is 1 can play or record them, and FL's own MIDI export may or may not include them. Switching it off ends what sounds (all notes off). Allocation-free on the audio thread.
// The key: FL's pitch above the channel's base key (Record/FlNotes.cs): the base the instance knows, else C5, and MIDI note 0 from the first note that cannot be read from C5 (a key above 127) on.
using BrokenNes2.Capture;
using BrokenNes2.Record;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    /// <summary>The live out's pitch-bend range: 48 semitones (0.29 cents a step), so slides of up to four octaves go out whole; a note that moves further is clamped at the end.</summary>
    public const int LiveBendRange = 48, LivePort = 1;
    private int liveBend, liveBendChannel = -1;    // the last bend sent and its channel
    private bool liveFromMidi0;                    // a note of this instance could not be read from C5: its pitch is read from MIDI note 0 from then on
    private int liveRpnDone;                       // channels (bit mask) that got the bend range since the box was ticked
    private int liveSent = -1;                     // the channel the last note went out on (all notes off there when the box is unticked)
    private volatile int liveReset;                // the window switched it on or off: the audio thread starts over

    /// <summary>The box changed (the UI thread): the audio thread ends what sounds and starts over.</summary>
    internal void LiveSwitched() => Volatile.Write(ref liveReset, 1);
    private bool LivePending => Volatile.Read(ref liveReset) != 0;

    /// <summary>The MIDI channel this instance sends on now: its role's (allocation-free: read on the audio thread).</summary>
    private int LiveChannelNow()
    {
        var a = assignment;
        var e = a.Emulator;
        var role = CapRole.Other;
        if (e != null && e.PresetMode && preset is { } lp)
            role = lp.Play.Kind switch
            {
                Instruments.InstrumentKind.NesPulse1 => CapRole.Pulse1, Instruments.InstrumentKind.NesPulse2 => CapRole.Pulse2, Instruments.InstrumentKind.NesTriangle => CapRole.Triangle,
                Instruments.InstrumentKind.NesNoise => CapRole.Noise, Instruments.InstrumentKind.NesDmc => CapRole.Dmc, _ => CapRole.Other,
            };
        else if (e != null && a.Channel is >= 0 and < Ch.ToneCount && !e.AnySamplerMode) role = (CapRole)a.Channel;
        int ch = CaptureSong.ChannelOf(role);
        return ch >= 0 ? ch : 4 + InstanceNo % 5;
    }

    private void MidiOut(int status, int d1, int d2) => SendMidiOut((uint)(status & 0xFF | (d1 & 0x7F) << 8 | (d2 & 0x7F) << 16 | LivePort << 24));

    private void LiveStartOver()
    {
        if (Interlocked.Exchange(ref liveReset, 0) == 0) return;
        if (liveSent >= 0) MidiOut(0xB0 | liveSent, 123, 0);                    // all notes off where it last sent (a switch-off leaves nothing hanging)
        liveSent = -1;
        liveRpnDone = 0;
        liveBend = 0; liveBendChannel = -1;
        liveFromMidi0 = false;
        foreach (var v in voices) v.LiveSent = false;
    }

    partial void LiveNoteOn(Voice v, float final, float tuning, float vel)
    {
        if (LivePending) LiveStartOver();
        if (!CaptureHub.LiveOn) return;
        int ch = LiveChannelNow();
        if ((liveRpnDone & 1 << ch) == 0)
        {
            MidiOut(0xB0 | ch, 101, 0); MidiOut(0xB0 | ch, 100, 0); MidiOut(0xB0 | ch, 6, LiveBendRange); MidiOut(0xB0 | ch, 38, 0); MidiOut(0xB0 | ch, 101, 127); MidiOut(0xB0 | ch, 100, 127);
            liveRpnDone |= 1 << ch;
        }
        int rel = (int)Math.Round((v.Params->InitLevels.Pitch + tuning) / 100.0, MidpointRounding.AwayFromZero);
        int hint = CaptureBaseHint;
        if (hint < 0 && rel + FlNotes.BaseC5 > 127 && rel >= 0) liveFromMidi0 = true;
        int baseKey = hint >= 0 ? hint : liveFromMidi0 ? FlNotes.BaseImported : FlNotes.BaseC5;
        int key = Math.Clamp(baseKey + rel, 0, 127);
        v.LiveKey = key;
        v.LiveCh = ch;
        v.LiveBase = baseKey;
        LiveBend(ch, FlNotes.ToC5Cents(final, baseKey) + tuning - CaptureSong.C5CentsOfKey(key));
        MidiOut(0x90 | ch, key, Math.Clamp((int)Math.Round(vel * 128), 1, 127));
        v.LiveSent = true;
        liveSent = ch;
    }

    partial void LiveNoteOff(Voice v)
    {
        if (!v.LiveSent) return;
        v.LiveSent = false;
        MidiOut(0x80 | v.LiveCh, v.LiveKey, 64);
    }

    partial void LiveBlock(float tuning)
    {
        if (LivePending) LiveStartOver();
        if (!CaptureHub.LiveOn) return;
        for (int i = voices.Count - 1; i >= 0; i--)                                   // the newest sounding note owns the channel's bend (as the instance plays it)
        {
            var v = voices[i];
            if (v.Released || !v.LiveSent) continue;
            LiveBend(v.LiveCh, FlNotes.ToC5Cents(v.Params->FinalLevels.Pitch, v.LiveBase) + tuning - CaptureSong.C5CentsOfKey(v.LiveKey));
            break;
        }
    }

    private void LiveBend(int ch, double cents)
    {
        if (CaptureMutants.On(CaptureMutants.LiveNoBends)) return;                     // (the deliberate bug)
        int b = Math.Clamp((int)Math.Round(cents / (LiveBendRange * 100.0) * 8192, MidpointRounding.AwayFromZero), -8192, 8191);
        if (b == liveBend && ch == liveBendChannel) return;
        liveBend = b; liveBendChannel = ch;
        int u = b + 8192;
        MidiOut(0xE0 | ch, u & 0x7F, u >> 7);
    }
}
