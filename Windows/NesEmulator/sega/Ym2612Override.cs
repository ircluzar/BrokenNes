namespace NesEmulator.Sega;

/// <summary>
/// Hooks into a <see cref="Ym2612"/> for the plugin's "push an edited instrument into the running game" (<see cref="Ym2612.Override"/>; null by default, then the chip is exactly as it always was). A Genesis sound driver
/// rewrites a channel's patch whenever it likes, so an edited patch cannot simply be written into the registers: the hook sees every key-on and every write to the DAC data register as the game makes it and may change what
/// the chip takes. It runs on whatever thread runs the chip, inside the chip's write: no allocation, no locks, no waiting.
/// </summary>
public interface IYm2612Override
{
    /// <summary>A key-on (any operator) is about to take effect on channel 0 to 5. The hook may rewrite the channel's registers with <see cref="Ym2612.OverrideRegister"/>; the note then sounds with them.</summary>
    void OnKeyOn(Ym2612 chip, int channel);

    /// <summary>The game wrote <paramref name="value"/> to the DAC data register ($2A) at chip clock <paramref name="chipClock"/>; returns what the chip takes instead.</summary>
    byte OnDacWrite(byte value, long chipClock);
}
