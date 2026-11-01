namespace NesEmulator
{
    /// <summary>
    /// An APU core that plays the cartridge's expansion sound itself (the foreign ports whose source has its own expansion chips, e.g. APU_FCEU's
    /// FCE Ultra 0.96 MMC5 sound). The Bus forwards the expansion chip's register writes here and then leaves its own expansion mix
    /// (<c>MMC5Audio</c>) out of the output; MMC5Audio still runs for the $5010 / $5015 reads and its IRQ.
    /// </summary>
    public interface IApuExpansionAudio
    {
        /// <summary>A CPU write to the MMC5 sound registers ($5000-$5015).</summary>
        void WriteExpansionAudio(ushort address, byte value);
    }
}
