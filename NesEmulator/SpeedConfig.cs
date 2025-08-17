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

        // PPU: Enable pattern line expansion cache (per tile row 2-bit packing)
        public bool PpuPatternCache = true; // safe; invalidated on pattern writes
        // PPU: Batch prefetch of 33 tiles per scanline (metadata first pass, render second)
        public bool PpuTileBatching = true; // pairs well with pattern cache
        // PPU: Skip fully blank scanlines (all background color & no sprites) via batch detection
        public bool PpuSkipBlankScanlines = true; // requires batching to detect

        // Future toggles (placeholders):
    // CPU: Detect (instrument only) tight idle loops polling $2002 (PPU status) and spinning on a branch.
    // Safe default is off; when enabled it only annotates state (no timing changes) so other systems can observe it.
    public bool CpuIdleLoopDetect = true; // extremely safe: detection only, no skipping/fast-forward
    // Idle loop skip (PPU status loops) now ready for testing (safe subset with heavy guards)
    public bool CpuIdleLoopSkip = true; // enabled by default for testing
    public int CpuIdleLoopSkipMaxIterations = 8; // conservative initial cap for safe testing
    public int CpuIdleLoopMaxSpanBytes = 16; // maximum byte span between poll and branch for eligibility (future safety)
    // CPU: Approximate OAM DMA stall by lumping 513 cycles instead of per-cycle stepping loop.
    // Safe accuracy trade: exact parity (513 vs 514) minor; negligible gameplay impact while saving loop overhead.
    public bool CpuFastOamDmaStall = true; // default enabled
    // CPU: Allow skipping confirmed APU status ($4015) idle loops (higher risk; disabled by default)
    public bool CpuIdleLoopSkipApuStatus = true; // now enabled after completing safeguards
    // CPU: Separate conservative cap for APU status loop bursts (APU IRQ flags may appear unpredictably)
    public int CpuIdleLoopSkipApuMaxIterations = 4; // very conservative initial value
    // CPU: Adaptive burst sizing for idle loop skip (ramps iterations on long stable loops)
    public bool CpuIdleLoopSkipAdaptive = true;
    // CPU: Branch hotness instrumentation (taken/total counters per hashed PC)
    public bool CpuBranchHotness = true; // enable instrumentation by default for profiling
    // CPU: Direct zero-page RAM access in inlined opcodes (bypass bus.Read)
    public bool CpuZeroPageDirect = true;
    // CPU: Adaptive batching (dynamically adjust cycle threshold)
    public bool CpuAdaptiveBatching = true;
    public int CpuAdaptiveBatchTargetCycles = 24; // desired average batch size cycles
    public int CpuAdaptiveBatchMinCycles = 12;
    public int CpuAdaptiveBatchMaxCycles = 64;
        // public bool CpuBatchExecute;
        // public bool PpuBackgroundTileBatching;
    }
}
