// Round 9 (capture): "Send my notes as MIDI out" (the Test MIDI export window, off by default, remembered): each instance forwards the notes FL plays on it, with the pitch it plays as pitch bends,
// as MIDI out (MIDIOut_Delayed: FL sends them when its MIDI clock reaches the block they belong to), on the MIDI channel its role has in a capture (Pulse 1 -> 1, Pulse 2 -> 2, Triangle -> 3,
// Noise -> 10), port 1, pitch-bend range 24 semitones (RPN 0 sent before the first note of each channel). The plugin has had FPF_MIDIOut since round 8; the SDK's words for that flag: "plugin will
// send MIDI out messages (only those will be enabled when rendering to a MIDI file)". What FL does with them is NOT known (FL could not be run here): other FL channels whose MIDI input port is 1
// can play or record them, and FL's own MIDI export may or may not include them. Allocation-free on the audio thread.
using BrokenNes2.Capture;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    public const int LiveBendRange = 24, LivePort = 1;
    private int liveChannel = -1;              // the MIDI channel this instance sends on (set on the UI thread: CaptureHub.SetLive / Register)
    private int liveBend;                      // the last bend sent (-8192..8191)
    private bool liveRpnSent;
    private volatile int liveReset;            // the window switched it on or off: the audio thread starts over (all notes off first)

    internal void SetLiveChannel(int channel) { liveChannel = channel; Volatile.Write(ref liveReset, 1); }

    private void MidiOut(int status, int d1, int d2) => SendMidiOut((uint)(status & 0xFF | (d1 & 0x7F) << 8 | (d2 & 0x7F) << 16 | LivePort << 24));

    private void LiveStartOver()
    {
        if (Interlocked.Exchange(ref liveReset, 0) == 0) return;
        int ch = liveChannel;
        if (ch >= 0) MidiOut(0xB0 | ch, 123, 0);                               // all notes off on the channel it sent on (a switch-off leaves nothing hanging)
        liveRpnSent = false;
        liveBend = 0;
        foreach (var v in voices) v.LiveSent = false;
    }

    partial void LiveNoteOn(Voice v, float played, float vel)
    {
        if (!CaptureHub.LiveOn) return;
        LiveStartOver();
        int ch = liveChannel;
        if (ch < 0) return;
        if (!liveRpnSent)
        {
            MidiOut(0xB0 | ch, 101, 0); MidiOut(0xB0 | ch, 100, 0); MidiOut(0xB0 | ch, 6, LiveBendRange); MidiOut(0xB0 | ch, 38, 0); MidiOut(0xB0 | ch, 101, 127); MidiOut(0xB0 | ch, 100, 127);
            liveRpnSent = true;
        }
        int key = FlPitch.KeyOfInit(v.Params->InitLevels.Pitch, CapTuningNow);
        v.LiveKey = key;
        LiveBend(ch, played - FlPitch.FinalOfMidi(key));
        MidiOut(0x90 | ch, key, Math.Clamp((int)Math.Round(vel * 128), 1, 127));
        v.LiveSent = true;
    }

    partial void LiveNoteOff(Voice v)
    {
        if (!v.LiveSent) return;
        v.LiveSent = false;
        int ch = liveChannel;
        if (ch >= 0) MidiOut(0x80 | ch, v.LiveKey, 64);
    }

    partial void LiveBlock(float tuning)
    {
        if (!CaptureHub.LiveOn) return;
        LiveStartOver();
        int ch = liveChannel;
        if (ch < 0) return;
        for (int i = voices.Count - 1; i >= 0; i--)                                   // the newest sounding note owns the channel's bend (as the instance plays it)
        {
            var v = voices[i];
            if (v.Released || !v.LiveSent) continue;
            LiveBend(ch, v.Params->FinalLevels.Pitch + tuning - FlPitch.FinalOfMidi(v.LiveKey));
            break;
        }
    }

    private void LiveBend(int ch, double cents)
    {
        if (CaptureMutants.On(CaptureMutants.LiveNoBends)) return;                     // (the deliberate bug)
        int b = Math.Clamp((int)Math.Round(cents / (LiveBendRange * 100.0) * 8192), -8192, 8191);
        if (b == liveBend) return;
        liveBend = b;
        int u = b + 8192;
        MidiOut(0xE0 | ch, u & 0x7F, u >> 7);
    }
}
