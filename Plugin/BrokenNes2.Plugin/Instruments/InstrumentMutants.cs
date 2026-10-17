namespace BrokenNes2.Instruments;

/// <summary>Test-only switches that inject a deliberate bug into the library, so a verifier can be shown able to fail. Zero (the default) is the real code; nothing sets it outside the self-test.
/// Its own file because <c>SnesOverlay.cs</c> and <c>SpcSnapshot.cs</c> are also compiled into the Workshop (corruption tests), which must not need the rest of the model.</summary>
internal static class InstrumentMutants
{
    public static int Active = 0;
    public const int IgnoreLocks = 1, SeedIgnored = 2, UndoLeavesBit = 3, ReplayDropsLast = 4, RoundTripLosesBit = 5, FlipOutOfWidth = 6, BrrHeaderBroken = 7, CrcNotChecked = 8, WriteSkipsLoop = 9,
        OpnSwapsOperators = 10, GbWaveNibbleSwap = 11, SeedNotStored = 13, LoopOutsideSample = 14,
        ProviderSkipsApply = 15, ProviderIgnoresLocks = 16, UndoNotApplied = 17, WriteLeavesTrace = 18;
    public static bool On(int m) => Active == m;
}
