// Deliberate bugs FruityHost `flp-test` switches on to prove its checks can fail (zero in normal use: each is one test of this mask at the place named).
namespace BrokenNes2.Music.Flp;

public static class FlpMutants
{
    public const int SlideSignFlipped = 1, NoteFineIgnored = 2, OldestNoteWins = 4, TuningIgnored = 8, ReaderDropsLastNote = 16, DutyIgnored = 32, SlideSignLost = 64, ChannelVolumeSilent = 128, NoiseVolumeIgnored = 256;
    public static int Mask;
    public static bool On(int bit) => (Mask & bit) != 0;
}
