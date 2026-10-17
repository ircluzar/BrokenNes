namespace NesEmulator.Gb;

/// <summary>
/// A hook into <see cref="APU_GB"/> for the plugin's "push an edited instrument into the running game" (<see cref="APU_GB.Override"/>; null by default, then the unit is exactly as it always was). It sees every channel trigger (a write to
/// NRx4 with bit 7 set) before the channel starts, and may rewrite what the channel is about to play with (the wave RAM). It runs inside the register write: no allocation, no locks, no waiting.
/// </summary>
public interface IGbApuOverride
{
    /// <summary>Channel <paramref name="channel"/> (0 pulse 1, 1 pulse 2, 2 wave, 3 noise) is being triggered.</summary>
    void OnTrigger(APU_GB apu, int channel);
}
