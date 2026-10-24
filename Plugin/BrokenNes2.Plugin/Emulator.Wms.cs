// WideMess milestone 7a: the emulator's side of the WideMess sound chip (chip WMS on the SNES console). Direct mode: every instance plays the chip's built-in instruments, many notes at once.
// ROM mode: the game runs on the WideMess console (a .wms game, or a SNES game: locked, the WideMess is a SNES). Instrument Runaway on a WideMess game whose sound is unlocked: the game's own instruments.
namespace BrokenNes2;

public sealed partial class Emulator
{
    private WmsSnapshot? wmsState;

    /// <summary>The WideMess sound chip's memory captured when Runaway started on a WideMess game whose sound unit is unlocked (null otherwise: a SNES game, or a locked WideMess, is the SNES sampler's).</summary>
    public WmsSnapshot? WmsState => Volatile.Read(ref wmsState);

    /// <summary>Runaway on a WideMess game: every instance plays the game's instruments (the new voices' directory and the S-DSP's) on a WideMess sound chip of its own.</summary>
    public bool WmsSamplerMode => Runaway && WmsState != null;

    /// <summary>Direct mode on the WMS chip: every instance plays the chip's built-in 16-bit instruments, 24 notes at once.</summary>
    public bool WmsDirectMode => Mode == 0 && IsWmsChip(Chip);

    /// <summary>Whether a chip index is the WideMess sound chip (read on the audio thread: a field test, no allocation).</summary>
    public static bool IsWmsChip(int chip) => (uint)chip < (uint)Cx.Chips.Count && Cx.Chips[chip].IsWms;

    /// <summary>Test hook: puts a WideMess snapshot in place of the captured one (a test-made game, a hostile project), freezing the game.</summary>
    internal void ReplaceWmsForTests(WmsSnapshot snap)
    {
        lock (EmulatorHub.Gate)
        {
            PrepareWmsSamplers(snap);
            Volatile.Write(ref snapshot, null);
            Volatile.Write(ref wmsState, snap);
            Volatile.Write(ref runaway, 1);
            EmulatorHub.ReResolveAll(this);
            Changed();
        }
    }

    /// <summary>Tests: the console the running game's session is (0 SNES, 1 WideMess; -1 no game, another console).</summary>
    internal int RunningSessionKindForTests()
    {
        lock (romLock) { ApplyPending(); return (game as SessionGame)?.Kind switch { NesEmulator.Systems.ConsoleKind.Snes => 0, NesEmulator.Systems.ConsoleKind.WideMess => 1, _ => -1 }; }
    }

    /// <summary>Tests: whether the running game's sound unit is the WideMess one and a game unlocked it.</summary>
    internal bool RunningWmsUnlockedForTests()
    {
        lock (romLock) { ApplyPending(); return (game as SessionGame)?.Session is NesEmulator.Systems.SnesSession { Apu: NesEmulator.Snes.APU_WMS { Unlocked: true } }; }
    }

    /// <summary>Builds every instance's WideMess sampler for <paramref name="snap"/> here (the UI thread), so the audio threads only take it (no allocation there: a sampler copies 64 KB).</summary>
    private void PrepareWmsSamplers(WmsSnapshot snap)
    {
        int rate = EmulatorHub.HostRate;
        foreach (var m in members.ToArray()) m.PrepareSamplerFor(snap, rate);
    }
}
