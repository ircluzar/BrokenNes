namespace NesEmulator;

/// <summary>A NES APU core that keeps another NES APU in front for the register semantics (the bridges onto other consoles' sound chips: APU_SNES, APU_DMG, APU_MD,
/// APU_SMS, APU_SMSFM): the NES reads the front's registers back after a state load, to rebuild the bus's register latch that a live core swap replays.</summary>
public interface IFrontedApu
{
    IAPU Front { get; }
}
