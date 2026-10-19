// Round 8 `reload` (2026-10-18): how FL hands a note to a generator, in ONE place. Every reading of a voice's pitch and velocity goes through here: the synth paths (Direct mode, the samplers,
// the presets), the matcher that finds a channel's track, the host log, and the test host that stands in for FL.
//
// The evidence (the user's FL 2026 session of 2026-10-18, `Documents\BrokenNes2 host log.txt`, read against the recordings' MIDI files):
//   pitch      TVoiceParams.InitLevels.Pitch is cents ABOVE A BASE KEY, and the base is the channel's, not FL's: a channel FL made by importing a MIDI file, then replaced by BrokenNes2, handed
//              7100, 7100, 7300, 7500, 7100 for the keys 71, 71, 73, 75, 71 of "Wario's Woods - 02 Pulse 2, 25%" (base = MIDI note 0); every other replaced channel of that session did the same.
//              A channel made new in FL hands cents from C5 (MIDI 60): the SDK's own reading (Bendy, an FL plugin that sends MIDI notes, computes note = Pitch / 100 + 60), and the FL
//              certification of this plugin (UAT/plugin/fl: notes placed on FL's piano roll, rendered by FL, measured within 0.1 cent at that base). FL's channel "root note" (C5 by default) is the
//              likely reason: the SDK has no call that tells a plugin its channel's root note, so the base is FOUND, per instance, from a note it knows (Record/RecTracks.cs: the channel's track).
//   velocity   FPV_GetVelocity answers the note's MIDI velocity / 128 (0.5 for 64, 0.632 for 81, 0.992 for 127 in that log). The transcription's levels assume velocity / 127 (RecTranscribe.PlayedLevel):
//              a channel playing a recorded track therefore plays the recorded note's own velocity, not FL's float, whenever it knows which recorded note FL started (RecTracks.cs).
//   colour     FPV_GetColor answers the imported note's MIDI channel - 1 (colours 0, 1, 2, 3, 4, 5 for the tracks on MIDI channels 1-6). FL's MIDI Out has an option "Map note color to MIDI channel":
//              the import keeps the channel in the colour on purpose.
//   position   FHD_GetMixingTime: "Value is a pointer to a TFPTime, which is filled with an optional offset in samples" (fp_plugclass.h). The plugin used to pass T = NaN, which FL read as the
//              offset: every note of that session logged no song position. The plugin now passes offset 0 and checks the answer (two offsets one second apart must be a second of beats apart);
//              nothing depends on it any more (RecTracks.cs follows the notes by their order, keys and the time between them).
namespace BrokenNes2.Record;

public static class FlNotes
{
    /// <summary>The base key of a channel FL made itself (root note C5): cents from MIDI 60.</summary>
    public const int BaseC5 = 60;
    /// <summary>The base key of a channel FL made by importing a MIDI file (then replaced by BrokenNes2): cents from MIDI note 0 (measured in FL 2026, 2026-10-18).</summary>
    public const int BaseImported = 0;
    /// <summary>The bases a channel's notes are tried at when it is not known yet (the imported channel's first: that is the workflow this serves).</summary>
    public static readonly int[] Bases = [BaseImported, BaseC5];

    /// <summary>FL's pitch of a voice as the (fractional) MIDI key it names, on a channel whose base key is <paramref name="baseKey"/>.</summary>
    public static double Key(double flPitch, int baseKey) => baseKey + flPitch / 100.0;

    /// <summary>FL's pitch as cents from C5 (MIDI 60): what every engine of the plugin takes (SnesSampler, NesDirect, the Sega and Game Boy samplers, the presets).</summary>
    public static double ToC5Cents(double flPitch, int baseKey) => flPitch + (baseKey - BaseC5) * 100.0;

    /// <summary>The pitch FL hands for MIDI key <paramref name="key"/> (fractional: a bend in it) on a channel whose base key is <paramref name="baseKey"/> (the test host plays FL with this).</summary>
    public static float Pitch(double key, int baseKey) => (float)((key - baseKey) * 100.0);

    /// <summary>The MIDI velocity (1-127) of FL's FPV_GetVelocity answer (velocity / 128); -1 when FL gave none.</summary>
    public static int MidiVelocity(float flVelocity) => flVelocity > 0 ? Math.Clamp((int)Math.Round(flVelocity * 128.0), 1, 127) : -1;

    /// <summary>What FPV_GetVelocity answers for MIDI velocity <paramref name="velocity"/> (the test host).</summary>
    public static float FlVelocity(int velocity) => velocity / 128f;

    /// <summary>The level a recorded note's own velocity stands for, as the transcription computed every level (velocity / 127: RecTranscribe.PlayedLevel, NesTranscribe.PlayedLevel).</summary>
    public static float RecordedLevel(int velocity) => (float)(velocity / 127.0);

    /// <summary>The MIDI channel (0-15) FL's note colour stands for (FPV_GetColor: the imported note's channel - 1, i.e. the 0-based channel); -1 when out of range.</summary>
    public static int ChannelOfColour(int colour) => colour is >= 0 and <= 15 ? colour : -1;
}
