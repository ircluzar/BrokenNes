// Round 9 (capture): the deliberate bugs of the capture. Each is a single test of `Active` at the place a check watches; `FruityHost capture-test` and `selftest --only capture` switch them on one at
// a time (the DLL through plugin hook 799) and the check that watches that place must fail. Zero (none) in normal use.
namespace BrokenNes2.Capture;

public static class CaptureMutants
{
    public static int Active;

    public const int
        BaseFromMidi0 = 1,         // every channel's pitch read from MIDI note 0 (an imported channel's base) whatever its notes say: FL's own channels 60 keys low -> notes
        VelocityOver127 = 2,       // velocity = FL's value x 127 instead of x 128                                                                           -> notes
        BendRangeUnwritten = 3,    // the bends are scaled for the track's range but no RPN 0 is written (a reader assumes 2 semitones)                      -> bends
        BendSignFlipped = 4,       // every bend the other way                                                                                               -> bends
        Pulse2OnChannel1 = 5,      // Pulse 2's track on MIDI channel 1 (with Pulse 1)                                                                       -> channels
        ClockFixedRate = 6,        // the clock counts samples at 44,100 Hz whatever FL's rate                                                               -> render (FL's export at another rate)
        OpenNotesLost = 7,         // notes still sounding when FL stops are left out                                                                        -> notes (a note held over Stop)
        EndAtLastNote = 8,         // the song ends at its last note-off, not at the bar line after it (the loop VRUN plays)                                  -> loop
        TempoIgnored = 9,          // FL's tempo ignored (120 BPM)                                                                                           -> notes
        CaptureAllocates = 10,     // every captured event allocates (on the audio thread)                                                                   -> alloc
        NotFed = 11,               // the capture is built but not handed to Test MIDI export                                                                -> flow
        EventsLostSilently = 12,   // every 40th pitch event is lost and not counted                                                                         -> bends
        LiveNoBends = 13,          // the live MIDI out sends notes but no pitch bend                                                                        -> live
        SlideIgnored = 14,         // pitch changes of a voice after its note-on are not captured (only the pitch it started with)                           -> bends
        BaseDetectionIgnored = 15; // C5 always, even for channels whose notes only fit MIDI note 0 (imported channels): their keys 60 too high             -> notes (imported)

    public static bool On(int m) => Active == m;
}
