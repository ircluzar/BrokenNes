namespace NesEmulator
{
    // Central configuration toggles for speed-oriented hacks.
    // Keep fields simple public bools for low overhead access from hot paths.
    public class SpeedConfig
    {
        // APU: Skip per-cycle channel stepping & mixing when all channels are silent.
        // Fast-forwards frame sequencer and accumulates silence samples in bulk.
        public bool ApuSilentChannelSkip = true; // default enabled (safe; no audible difference when channels off)

    // APU: Skip envelope decay processing when constant volume flag set (value is static).
    public bool ApuSkipEnvelopeOnConstantVolume = true; // low-risk micro-optimization

    // Minimum CPU cycles in a batch before attempting silent fast-forward (avoids overhead on tiny batches)
    public int ApuSilentSkipMinCycles = 128; // tuned experimentally; adjust via UI

        // Future toggles (placeholders):
        // public bool CpuIdleLoopSkip;
        // public bool CpuBatchExecute;
        // public bool PpuBackgroundTileBatching;
    }
}
