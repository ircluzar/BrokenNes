namespace NesEmulator
{
    // Central configuration toggles for speed-oriented hacks.
    // Keep fields simple public bools for low overhead access from hot paths.
    public class SpeedConfig
    {
    // === Stepwise APU feature migration flags ===
    // Step 1: Absolute-cycle frame sequencer enable
    public bool ApuFeat_FrameSequencer = true; // enable first migrated feature
    // Step 2: Immediate Quarter+Half tick on $4017 write with bit7=1 (5-step mode)
    public bool ApuFeat_4017ImmediateTick = true;
    // Step 3: Sweep mute prediction (mute pulse channel if sweep target invalid)
    public bool ApuFeat_SweepMutePrediction = true;
    // Step 4: DMC channel core (delta counter, sample fetch, loop) – IRQ gated separately later
    public bool ApuFeat_DmcChannel = true;
    // Step 5: DMC IRQ enable (separate so we can validate channel mixing first without interrupt timing side-effects)
    public bool ApuFeat_DmcIrq = true; // enabled after validation of core channel
    // Phase 2: Nonlinear LUT mixing (pulse + TND) to remove per-sample divides
    public bool ApuFeat_LutMixing = true; // enable LUT path by default
    // Optional soft clip (legacy tanh). When false, output is un-clipped (relies on LUT normalization)
    public bool ApuFeat_SoftClip = true; // keep legacy sound character until validated

        // APU: Skip per-cycle channel stepping & mixing when all channels are silent.
        // Fast-forwards frame sequencer and accumulates silence samples in bulk.
        public bool ApuSilentChannelSkip = true; // default enabled (safe; no audible difference when channels off)

    // APU: Skip envelope decay processing when constant volume flag set (value is static).
    public bool ApuSkipEnvelopeOnConstantVolume = true;

    // Toggle for APU hot path optimizations (batched sample generation, block silence fill,
    // inlined pulse output, single DMC fetch). Disable to isolate regressions.
    public bool ApuOpt_NewHotPaths = false; // set false to fall back to per-sample path

    // Granular toggles for isolating individual optimizations (override umbrella flag when false)
    public bool ApuOpt_BatchSampleMix = false;      // batched GenerateAudioSamplesBatch
    public bool ApuOpt_BlockSilenceFill = false;    // block-based WriteSilenceSamples
    public bool ApuOpt_InlinePulseOutput = false;   // inline ComputePulseOutput logic in ClockPulse
    public bool ApuOpt_SingleDmcFetch = false;      // remove duplicate TryDmcFetch call

    // Minimum CPU cycles in a batch before attempting silent fast-forward (avoids overhead on tiny batches)
    public int ApuSilentSkipMinCycles = 128; // tuned experimentally; adjust via UI

        // PPU: Enable pattern line expansion cache (per tile row 2-bit packing)
        public bool PpuPatternCache = true; // safe; invalidated on pattern writes
        // PPU: Batch prefetch of 33 tiles per scanline (metadata first pass, render second)
        public bool PpuTileBatching = true; // pairs well with pattern cache
        // PPU: Skip fully blank scanlines (all background color & no sprites) via batch detection
        public bool PpuSkipBlankScanlines = true; // requires batching to detect
    // Formerly gated whether the 8-sprites-per-scanline cap/overflow flag was evaluated at all
    // (PPU_SPD/PPU_EIL); that's real hardware behavior, not a speed trade-off, so it is now
    // unconditional in both. Field kept (unused) to avoid breaking anything that still sets it.
    public bool PpuSpriteLineEvaluation = true;
    // PPU: Use unsafe pointer-based scanline renderer (avoids bounds checks)
    public bool PpuUnsafeScanline = true; // default enabled (guards ensure buffer allocated)
    // PPU: Defer attribute fetch until non-zero tile bits known (saves reads on blank tiles)
    public bool PpuDeferAttributeFetch = true; // default on; small gain in blank-heavy scenes

    // PPU: Cache paletteRAM entries expanded to packed RGBA (updates on writes)
    public bool PpuPaletteCache = true; // trivial & safe
    // PPU: Reuse pattern row cache for sprites too (currently only BG path uses it)
    public bool PpuSpritePatternCache = true; // safe: same invalidation as BG
    // PPU: Fast sprite color mapping (preload three RGBA entries per sprite row)
    public bool PpuSpriteFastPath = true;

        // Future toggles (placeholders):
    // CPU: Detect (instrument only) tight idle loops polling $2002 (PPU status) and spinning on a branch.
    // Safe default is off; when enabled it only annotates state (no timing changes) so other systems can observe it.
    public bool CpuIdleLoopDetect = true; // extremely safe: detection only, no skipping/fast-forward
    // Idle loop skip (PPU status loops) now ready for testing (safe subset with heavy guards)
    public bool CpuIdleLoopSkip = true; // enabled by default for testing
    public int CpuIdleLoopSkipMaxIterations = 32; // redline: matches internal PPU burst cap
    public int CpuIdleLoopMaxSpanBytes = 32; // redline: wider loop body allowance (still reset on unrelated writes)
    // Formerly gated whether the OAM DMA CPU stall was applied at all; the stall is real
    // hardware behavior (see Bus.WriteSlow's $4014 handler), not a speed trade-off, so it is
    // now unconditional. Field kept (unused) to avoid breaking anything that still sets it.
    public bool CpuFastOamDmaStall = true;
    // CPU: Allow skipping confirmed APU status ($4015) idle loops (higher risk; disabled by default)
    public bool CpuIdleLoopSkipApuStatus = true; // now enabled after completing safeguards
    // CPU: Separate conservative cap for APU status loop bursts (APU IRQ flags may appear unpredictably)
    public int CpuIdleLoopSkipApuMaxIterations = 8; // hit internal APU burst cap (8) for max gain
    // CPU: Adaptive burst sizing for idle loop skip (ramps iterations on long stable loops)
    public bool CpuIdleLoopSkipAdaptive = true;
    // CPU: Branch hotness instrumentation (taken/total counters per hashed PC)
    public bool CpuBranchHotness = false; // disabled for max performance (remove instrumentation overhead)
    // CPU: Direct zero-page RAM access in inlined opcodes (bypass bus.Read)
    public bool CpuZeroPageDirect = true;
    // CPU: Adaptive batching (dynamically adjust cycle threshold)
    public bool CpuAdaptiveBatching = true;
    public int CpuAdaptiveBatchTargetCycles = 64; // redline: larger batches to amortize overhead
    public int CpuAdaptiveBatchMinCycles = 24;
    public int CpuAdaptiveBatchMaxCycles = 128;

    // Not a speed hack (lives here for convenience, alongside the other opt-in per-session
    // toggles) - an ACCURACY correction. NES.RunFrame() otherwise targets an exact 60.000fps
    // (29829 CPU cycles/frame), which is a deliberate simplification that does not match real
    // NTSC hardware's true ~60.0988fps (89341.5 PPU dots/frame average = 29780.5 CPU
    // cycles/frame, from the well-known "PPU skips one dot every other frame" quirk). The ~48.5
    // cycle/frame gap is invisible for ordinary play but compounds into a real, growing desync
    // over a TAS movie's tens of thousands of frames - this is what breaks .fm2 portability with
    // FCEUX. Off by default: every other consumer (AccuracyCoin, benchmarks, existing
    // savestate-based tooling) is tuned around and verified against the exact-60fps baseline, and
    // changing it globally risks exactly the batch-boundary-timing regressions this project has
    // already hit before (see project_fceux_movie_parity / project_dmc_dma_stall_gap memories).
    // Only movie replay/export paths (TasCli et al.) opt in. See NES.RunFrame for the mechanism.
    public bool NtscAccurateFrameRate = false;
        // public bool CpuBatchExecute;
        // public bool PpuBackgroundTileBatching;
    }
}
