namespace NesEmulator.Sega;

/// <summary>
/// A hook into <see cref="Ym2413"/> for the plugin's "push an edited instrument into the running game" (<see cref="Ym2413.Override"/>; null by default, then the chip is exactly as it always was). It sees every key-on of a channel that plays
/// the user instrument (instrument 0, registers $00-$07) before the note starts, and may rewrite those eight registers with <see cref="Ym2413.WriteRegister"/>. It runs inside the register write: no allocation, no locks, no waiting.
/// </summary>
public interface IYm2413Override
{
    /// <summary>A key-on on channel 0 to 8 that plays the user instrument is about to take effect.</summary>
    void OnKeyOn(Ym2413 chip, int channel);
}
