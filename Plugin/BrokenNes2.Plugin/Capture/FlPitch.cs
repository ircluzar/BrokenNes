// Round 9 (capture): FL Studio's note pitch, in ONE place. FL hands a native generator two sets of levels per voice (TVoiceParams): InitLevels (the note as the piano roll holds it) and FinalLevels
// (what FL wants the voice to play now: the note with its slides, per-note pitch, fine pitch and the channel's own pitch, rewritten by FL while the note sounds). Their pitch fields do NOT share an
// origin:
//
//   InitLevels.Pitch   cents from MIDI note 0. Evidence: the user's real FL host log (2026-10-18, FL 26.0.1, `Documents\BrokenNes2 host log.txt`): the notes of imported tracks whose keys are
//                      71, 73 and 75 arrived as 7100, 7300 and 7500 (the log, which assumed C5, printed them as "key 131, 133, 135").
//   FinalLevels.Pitch  cents from C5 (MIDI 60). Evidence: Direct mode has always read it that way (60 + cents / 100) and was certified in real FL 2026 (UAT/plugin/fl: pitch within 0.1 cent on 15 of 15
//                      notes, slides 2 to 3 cents), and the user plays BrokenNes2 channels in tune.
//
// Everything that turns FL's pitch into a MIDI key goes through here (the capture's notes; whoever else reads InitLevels should too). Not measured: whether FL's per-note fine pitch is in InitLevels
// (the capture does not depend on it: a key is the nearest one, the rest is pitch bend), and whether a channel's root note other than C5 moves FinalLevels' origin.
namespace BrokenNes2.Capture;

public static class FlPitch
{
    /// <summary>The MIDI key FinalLevels.Pitch = 0 stands for (C5).</summary>
    public const int FinalOriginKey = 60;
    /// <summary>The MIDI key InitLevels.Pitch = 0 stands for (MIDI note 0).</summary>
    public const int InitOriginKey = 0;

    /// <summary>A FinalLevels pitch (cents from C5) as a fractional MIDI key.</summary>
    public static double MidiOfFinal(double cents) => FinalOriginKey + cents / 100.0;

    /// <summary>An InitLevels pitch (cents from MIDI note 0) as a fractional MIDI key.</summary>
    public static double MidiOfInit(double cents) => (CaptureMutants.On(CaptureMutants.InitFromC5) ? FinalOriginKey : InitOriginKey) + cents / 100.0;

    /// <summary>The FinalLevels pitch (cents from C5) of a fractional MIDI key.</summary>
    public static double FinalOfMidi(double key) => (key - FinalOriginKey) * 100.0;

    /// <summary>The InitLevels pitch (cents from MIDI note 0) of a fractional MIDI key (test hosts that hand notes over as FL does).</summary>
    public static double InitOfMidi(double key) => (key - InitOriginKey) * 100.0;

    /// <summary>The MIDI key of a note FL starts: its InitLevels pitch plus the instance's own tuning (Coarse x 100 + Fine, cents), to the nearest key, 0..127.</summary>
    public static int KeyOfInit(double initCents, double tuningCents = 0) => Math.Clamp((int)Math.Round(MidiOfInit(initCents) + tuningCents / 100.0, MidpointRounding.AwayFromZero), 0, 127);
}
