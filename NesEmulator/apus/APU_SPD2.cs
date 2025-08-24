using System;

namespace NesEmulator
{
    // Legacy / famiclone-style APU core (renamed from APU_JANK.cs / class APUJANK)
    // THIS IS A BACKUP OF THE PREVIOUS APU WHICH HAD ISSUES. DO NOT DELETE WITHOUT REVIEW.
    public class APU_SPD2 : IAPU
    {
        // Core metadata
        public string CoreName => "EXP. Spd JNK";
        public string Description => "Based on the Low Power (SPD) core, this failed optimization experiment added jank to sound emulation.";
        public int Performance => 10;
        public int Rating => 2;
        public string Category => "Degraded";
        private Bus bus;
    public APU_SPD2(Bus bus)
    {
            this.bus = bus;
        }

        // APU Registers
        private byte pulse1_duty, pulse1_length, pulse1_sweep;
        private ushort pulse1_timer;
        private byte pulse2_duty, pulse2_length, pulse2_sweep;
        private ushort pulse2_timer;
        private byte triangle_linear, triangle_length;
        private ushort triangle_timer;
        private byte noise_length, noise_period;

        private byte status;

        // Audio generation (ring buffer so JS can pull variably sized chunks without losing samples)
        private const int audioSampleRate = 44100; // Target host sample rate (matches WebAudio param)
        private const int AudioRingSize = 32768;   // ~0.74s of audio at 44.1kHz (power-of-two for mod efficiency)
        private float[] audioRing = new float[AudioRingSize];
        private int ringWrite = 0;
        private int ringRead = 0;
        private int ringCount = 0; // number of samples available
        private double fractionalSampleAccumulator = 0; // carries fractional sample parts between Step batches
        // Perf counters (not serialized)
        private long perf_silentSkipBatches = 0;
        private long perf_silentSkipCycles = 0;
        private long perf_silentSkipSamples = 0;
        private long perf_envelopeSkipEvents = 0;

        // Pulse channel sequencer and timers
        private int pulse1_sequenceIndex = 0; // 0..7
        private int pulse2_sequenceIndex = 0;
        private int pulse1_timerCounter = 0;  // counts down in CPU cycles
        private int pulse2_timerCounter = 0;

        // Triangle channel
        private int triangle_sequenceIndex = 0; // 0..31
        private int triangle_timerCounter = 0;
        private int triangle_linearCounter = 0;
        private bool triangle_linearReloadFlag = false;

        // Noise channel
        private int noise_timerCounter = 0; // counts down in CPU cycles

        // Envelope state (per pulse + noise)
        private bool pulse1_envelopeStart = false;
        private int pulse1_envelopeDivider = 0;
        private int pulse1_decayLevel = 0;
        private bool pulse2_envelopeStart = false;
        private int pulse2_envelopeDivider = 0;
        private int pulse2_decayLevel = 0;
        private bool noise_envelopeStart = false;
        private int noise_envelopeDivider = 0;
        private int noise_decayLevel = 0;

        // Volume / control flags derived from register bits
        private bool pulse1_constantVolume = false;
        private int pulse1_volumeParam = 0; // 0..15
        private bool pulse1_lengthHalt = false; // envelope loop flag
        private bool pulse2_constantVolume = false;
        private int pulse2_volumeParam = 0;
        private bool pulse2_lengthHalt = false;
        private bool noise_constantVolume = false;
        private int noise_volumeParam = 0;
        private bool noise_lengthHalt = false;
        private bool triangle_lengthHalt = false; // control flag bit 7 of 0x4008

        // Output latch for each channel (integer amplitude units matching NES mixing formulas)
        private int pulse1_output = 0; // 0..15 (or 0 when muted)
        private int pulse2_output = 0;
        private int triangle_output = 0; // 0..15
        private int noise_output = 0;    // 0 or volume

        // Length counters
        private int pulse1_lengthCounter, pulse2_lengthCounter, triangle_lengthCounter, noise_lengthCounter;
        // Sweep units
        private int pulse1_sweepDivider, pulse2_sweepDivider; // countdown dividers
        private bool pulse1_sweepNegate, pulse2_sweepNegate;
        private int pulse1_sweepShift, pulse2_sweepShift;
        private int pulse1_sweepPeriod, pulse2_sweepPeriod; // (period+1) effective
        private bool pulse1_sweepReload, pulse2_sweepReload;
    // DMC channel (added stepwise)
    private byte dmc_ctrl, dmc_directLoad, dmc_addrReg, dmc_lenReg;
    private bool dmc_enabled, dmc_loop, dmc_irqEnable, dmc_irqFlag;
    private int dmc_timer, dmc_timerPeriod; private int dmc_sampleAddress, dmc_sampleLengthRemaining;
    private int dmc_shiftReg, dmc_bitsRemaining, dmc_deltaCounter = 64; private bool dmc_silence; private int dmc_sampleBuffer; private bool dmc_sampleBufferFilled;
    private int dmc_output;
    private static readonly int[] DmcRateTable = { 428,380,340,320,286,254,226,214,190,160,142,128,106,84,72,54 }; // NTSC
    // Frame sequencer (legacy relative + optional absolute-cycle feature)
    private bool frameSequencerMode5 = false; // default 4-step
    private bool frameIRQInhibit = false;
    private int frameSequenceStep = 0;
    private int cyclesToNextFrameEvent = 3729; // legacy relative countdown path
    // Absolute-cycle mode fields (enabled when SpeedConfig.ApuFeat_FrameSequencer true)
    private int abs_frameStep = 0;
    private int abs_frameCycle = 0;
    private int abs_nextEventCycle = 7457; // NTSC quarter/half events
    private const int ABS_EV0 = 7457;
    private const int ABS_EV1 = 14913;
    private const int ABS_EV2 = 22371;
    private const int ABS_EV3 = 29829;
    private const int ABS_EV4 = 37281; // 5-step extra
                                                   // Noise LFSR
        private ushort noiseShiftRegister = 1;
        // Filters state
        private float lpLast = 0f, dcLastIn = 0f, dcLastOut = 0f;
        // Channel enables
        private bool pulse1_enabled = false, pulse2_enabled = false, triangle_enabled = false, noise_enabled = false;
    // Cached optimization toggles (set each Step)
    private bool _optInlinePulse = true, _optSingleDmcFetch = true, _optBlockSilence = true;
        // Constants
        private const float LowPassCoeff = 0.15f; // simple 1-pole low-pass coefficient
                                                  // Standard NES length table (32 entries). Previous version missed the final value (30) causing
                                                  // IndexOutOfRangeException when games used length index 31 (e.g. Kirby's Adventure / MMC3 titles).
        private static readonly int[] LengthTable = new int[]
        { 10,254,20, 2,40, 4,80, 6,160, 8,60,10,14,12,26,14,12,16,24,18,48,20,96,22,192,24,72,26,16,28,32,30 };
        private static readonly int[] NoisePeriods = new int[]
        { 4,8,16,32,64,96,128,160,202,254,380,508,762,1016,2034,4068 }; // NTSC noise periods

        // ====== SAVE STATE SUPPORT ======
        private class ApuState
        {
            public byte pulse1_duty, pulse1_length, pulse1_sweep; public ushort pulse1_timer;
            public byte pulse2_duty, pulse2_length, pulse2_sweep; public ushort pulse2_timer;
            public byte triangle_linear, triangle_length; public ushort triangle_timer;
            public byte noise_length, noise_period, status;
            public int pulse1_sequenceIndex, pulse2_sequenceIndex, pulse1_timerCounter, pulse2_timerCounter;
            public int triangle_sequenceIndex, triangle_timerCounter, triangle_linearCounter, noise_timerCounter;
            public bool triangle_linearReloadFlag; public ushort noiseShiftRegister;
            public int pulse1_envelopeDivider, pulse1_decayLevel, pulse2_envelopeDivider, pulse2_decayLevel, noise_envelopeDivider, noise_decayLevel;
            public bool pulse1_envelopeStart, pulse2_envelopeStart, noise_envelopeStart;
            public bool pulse1_constantVolume, pulse1_lengthHalt, pulse2_constantVolume, pulse2_lengthHalt, noise_constantVolume, noise_lengthHalt, triangle_lengthHalt;
            public int pulse1_volumeParam, pulse2_volumeParam, noise_volumeParam;
            public int pulse1_output, pulse2_output, triangle_output, noise_output;
            public int pulse1_lengthCounter, pulse2_lengthCounter, triangle_lengthCounter, noise_lengthCounter;
            public int pulse1_sweepDivider, pulse1_sweepShift, pulse1_sweepPeriod, pulse2_sweepDivider, pulse2_sweepShift, pulse2_sweepPeriod;
            public bool pulse1_sweepNegate, pulse1_sweepReload, pulse2_sweepNegate, pulse2_sweepReload;
            public bool frameSequencerMode5, frameIRQInhibit; public int frameSequenceStep, cyclesToNextFrameEvent;
            public float lpLast, dcLastIn, dcLastOut; public bool pulse1_enabled, pulse2_enabled, triangle_enabled, noise_enabled;
            public double fractionalSampleAccumulator; public int ringWrite, ringRead, ringCount; // ring meta only
            // DMC state (added incrementally)
            public byte dmc_ctrl, dmc_directLoad, dmc_addrReg, dmc_lenReg;
            public bool dmc_enabled, dmc_loop, dmc_irqEnable, dmc_irqFlag;
            public int dmc_timer, dmc_timerPeriod, dmc_sampleAddress, dmc_sampleLengthRemaining;
            public int dmc_shiftReg, dmc_bitsRemaining, dmc_deltaCounter, dmc_sampleBuffer;
            public bool dmc_sampleBufferFilled, dmc_silence;
            public int dmc_output;
        }
        public object GetState() => new ApuState
        {
            pulse1_duty = pulse1_duty, pulse1_length = pulse1_length, pulse1_sweep = pulse1_sweep, pulse1_timer = pulse1_timer,
            pulse2_duty = pulse2_duty, pulse2_length = pulse2_length, pulse2_sweep = pulse2_sweep, pulse2_timer = pulse2_timer,
            triangle_linear = triangle_linear, triangle_length = triangle_length, triangle_timer = triangle_timer,
            noise_length = noise_length, noise_period = noise_period, status = status,
            pulse1_sequenceIndex = pulse1_sequenceIndex, pulse2_sequenceIndex = pulse2_sequenceIndex, pulse1_timerCounter = pulse1_timerCounter, pulse2_timerCounter = pulse2_timerCounter,
            triangle_sequenceIndex = triangle_sequenceIndex, triangle_timerCounter = triangle_timerCounter, triangle_linearCounter = triangle_linearCounter, noise_timerCounter = noise_timerCounter,
            triangle_linearReloadFlag = triangle_linearReloadFlag, noiseShiftRegister = noiseShiftRegister,
            pulse1_envelopeStart = pulse1_envelopeStart, pulse1_envelopeDivider = pulse1_envelopeDivider, pulse1_decayLevel = pulse1_decayLevel,
            pulse2_envelopeStart = pulse2_envelopeStart, pulse2_envelopeDivider = pulse2_envelopeDivider, pulse2_decayLevel = pulse2_decayLevel,
            noise_envelopeStart = noise_envelopeStart, noise_envelopeDivider = noise_envelopeDivider, noise_decayLevel = noise_decayLevel,
            pulse1_constantVolume = pulse1_constantVolume, pulse1_lengthHalt = pulse1_lengthHalt, pulse2_constantVolume = pulse2_constantVolume, pulse2_lengthHalt = pulse2_lengthHalt,
            noise_constantVolume = noise_constantVolume, noise_lengthHalt = noise_lengthHalt, triangle_lengthHalt = triangle_lengthHalt,
            pulse1_volumeParam = pulse1_volumeParam, pulse2_volumeParam = pulse2_volumeParam, noise_volumeParam = noise_volumeParam,
            pulse1_output = pulse1_output, pulse2_output = pulse2_output, triangle_output = triangle_output, noise_output = noise_output,
            pulse1_lengthCounter = pulse1_lengthCounter, pulse2_lengthCounter = pulse2_lengthCounter, triangle_lengthCounter = triangle_lengthCounter, noise_lengthCounter = noise_lengthCounter,
            pulse1_sweepDivider = pulse1_sweepDivider, pulse1_sweepShift = pulse1_sweepShift, pulse1_sweepPeriod = pulse1_sweepPeriod,
            pulse2_sweepDivider = pulse2_sweepDivider, pulse2_sweepShift = pulse2_sweepShift, pulse2_sweepPeriod = pulse2_sweepPeriod,
            pulse1_sweepNegate = pulse1_sweepNegate, pulse1_sweepReload = pulse1_sweepReload, pulse2_sweepNegate = pulse2_sweepNegate, pulse2_sweepReload = pulse2_sweepReload,
            frameSequencerMode5 = frameSequencerMode5, frameIRQInhibit = frameIRQInhibit, frameSequenceStep = frameSequenceStep, cyclesToNextFrameEvent = cyclesToNextFrameEvent,
            lpLast = lpLast, dcLastIn = dcLastIn, dcLastOut = dcLastOut, pulse1_enabled = pulse1_enabled, pulse2_enabled = pulse2_enabled, triangle_enabled = triangle_enabled, noise_enabled = noise_enabled,
            fractionalSampleAccumulator = fractionalSampleAccumulator, ringWrite = ringWrite, ringRead = ringRead, ringCount = ringCount
            , dmc_ctrl = dmc_ctrl, dmc_directLoad = dmc_directLoad, dmc_addrReg = dmc_addrReg, dmc_lenReg = dmc_lenReg,
            dmc_enabled = dmc_enabled, dmc_loop = dmc_loop, dmc_irqEnable = dmc_irqEnable, dmc_irqFlag = dmc_irqFlag,
            dmc_timer = dmc_timer, dmc_timerPeriod = dmc_timerPeriod, dmc_sampleAddress = dmc_sampleAddress, dmc_sampleLengthRemaining = dmc_sampleLengthRemaining,
            dmc_shiftReg = dmc_shiftReg, dmc_bitsRemaining = dmc_bitsRemaining, dmc_deltaCounter = dmc_deltaCounter, dmc_sampleBuffer = dmc_sampleBuffer,
            dmc_sampleBufferFilled = dmc_sampleBufferFilled, dmc_silence = dmc_silence, dmc_output = dmc_output
        };
        public void SetState(object st)
        {
            if (st is ApuState s) { ApplyState(s); PostRestore(); return; }
            if (st is System.Text.Json.JsonElement je)
            {
                try {
                    if(je.ValueKind==System.Text.Json.JsonValueKind.Object){
                        var tmp = new ApuState(); System.Text.Json.JsonElement v;
                        if(je.TryGetProperty("pulse1_duty", out v)) tmp.pulse1_duty=(byte)v.GetByte();
                        if(je.TryGetProperty("pulse1_length", out v)) tmp.pulse1_length=(byte)v.GetByte();
                        if(je.TryGetProperty("pulse1_sweep", out v)) tmp.pulse1_sweep=(byte)v.GetByte();
                        if(je.TryGetProperty("pulse1_timer", out v)) tmp.pulse1_timer=(ushort)v.GetUInt16();
                        if(je.TryGetProperty("pulse2_duty", out v)) tmp.pulse2_duty=(byte)v.GetByte();
                        if(je.TryGetProperty("pulse2_length", out v)) tmp.pulse2_length=(byte)v.GetByte();
                        if(je.TryGetProperty("pulse2_sweep", out v)) tmp.pulse2_sweep=(byte)v.GetByte();
                        if(je.TryGetProperty("pulse2_timer", out v)) tmp.pulse2_timer=(ushort)v.GetUInt16();
                        if(je.TryGetProperty("triangle_linear", out v)) tmp.triangle_linear=(byte)v.GetByte();
                        if(je.TryGetProperty("triangle_length", out v)) tmp.triangle_length=(byte)v.GetByte();
                        if(je.TryGetProperty("triangle_timer", out v)) tmp.triangle_timer=(ushort)v.GetUInt16();
                        if(je.TryGetProperty("noise_length", out v)) tmp.noise_length=(byte)v.GetByte();
                        if(je.TryGetProperty("noise_period", out v)) tmp.noise_period=(byte)v.GetByte();
                        if(je.TryGetProperty("status", out v)) tmp.status=(byte)v.GetByte();
                        if(je.TryGetProperty("noiseShiftRegister", out v)) tmp.noiseShiftRegister=(ushort)v.GetUInt16();
                        if(je.TryGetProperty("pulse1_lengthCounter", out v)) tmp.pulse1_lengthCounter=v.GetInt32();
                        if(je.TryGetProperty("pulse2_lengthCounter", out v)) tmp.pulse2_lengthCounter=v.GetInt32();
                        if(je.TryGetProperty("triangle_lengthCounter", out v)) tmp.triangle_lengthCounter=v.GetInt32();
                        if(je.TryGetProperty("noise_lengthCounter", out v)) tmp.noise_lengthCounter=v.GetInt32();
                        if(je.TryGetProperty("fractionalSampleAccumulator", out v)) tmp.fractionalSampleAccumulator=v.GetDouble();
                        if(je.TryGetProperty("dmc_ctrl", out v)) tmp.dmc_ctrl=(byte)v.GetByte();
                        if(je.TryGetProperty("dmc_directLoad", out v)) tmp.dmc_directLoad=(byte)v.GetByte();
                        if(je.TryGetProperty("dmc_addrReg", out v)) tmp.dmc_addrReg=(byte)v.GetByte();
                        if(je.TryGetProperty("dmc_lenReg", out v)) tmp.dmc_lenReg=(byte)v.GetByte();
                        if(je.TryGetProperty("dmc_enabled", out v)) tmp.dmc_enabled=v.GetBoolean();
                        if(je.TryGetProperty("dmc_loop", out v)) tmp.dmc_loop=v.GetBoolean();
                        if(je.TryGetProperty("dmc_irqEnable", out v)) tmp.dmc_irqEnable=v.GetBoolean();
                        if(je.TryGetProperty("dmc_irqFlag", out v)) tmp.dmc_irqFlag=v.GetBoolean();
                        if(je.TryGetProperty("dmc_timer", out v)) tmp.dmc_timer=v.GetInt32();
                        if(je.TryGetProperty("dmc_timerPeriod", out v)) tmp.dmc_timerPeriod=v.GetInt32();
                        if(je.TryGetProperty("dmc_sampleAddress", out v)) tmp.dmc_sampleAddress=v.GetInt32();
                        if(je.TryGetProperty("dmc_sampleLengthRemaining", out v)) tmp.dmc_sampleLengthRemaining=v.GetInt32();
                        if(je.TryGetProperty("dmc_shiftReg", out v)) tmp.dmc_shiftReg=v.GetInt32();
                        if(je.TryGetProperty("dmc_bitsRemaining", out v)) tmp.dmc_bitsRemaining=v.GetInt32();
                        if(je.TryGetProperty("dmc_deltaCounter", out v)) tmp.dmc_deltaCounter=v.GetInt32();
                        if(je.TryGetProperty("dmc_sampleBuffer", out v)) tmp.dmc_sampleBuffer=v.GetInt32();
                        if(je.TryGetProperty("dmc_sampleBufferFilled", out v)) tmp.dmc_sampleBufferFilled=v.GetBoolean();
                        if(je.TryGetProperty("dmc_silence", out v)) tmp.dmc_silence=v.GetBoolean();
                        if(je.TryGetProperty("dmc_output", out v)) tmp.dmc_output=v.GetInt32();
                        ApplyState(tmp); PostRestore();
                    }
                } catch { }
            }
        }
        private void ApplyState(ApuState s)
        {
            pulse1_duty = s.pulse1_duty; pulse1_length = s.pulse1_length; pulse1_sweep = s.pulse1_sweep; pulse1_timer = s.pulse1_timer;
            pulse2_duty = s.pulse2_duty; pulse2_length = s.pulse2_length; pulse2_sweep = s.pulse2_sweep; pulse2_timer = s.pulse2_timer;
            triangle_linear = s.triangle_linear; triangle_length = s.triangle_length; triangle_timer = s.triangle_timer;
            noise_length = s.noise_length; noise_period = s.noise_period; status = s.status;
            pulse1_sequenceIndex = s.pulse1_sequenceIndex; pulse2_sequenceIndex = s.pulse2_sequenceIndex; pulse1_timerCounter = s.pulse1_timerCounter; pulse2_timerCounter = s.pulse2_timerCounter;
            triangle_sequenceIndex = s.triangle_sequenceIndex; triangle_timerCounter = s.triangle_timerCounter; triangle_linearCounter = s.triangle_linearCounter; triangle_linearReloadFlag = s.triangle_linearReloadFlag;
            noise_timerCounter = s.noise_timerCounter; noiseShiftRegister = s.noiseShiftRegister;
            pulse1_envelopeStart = s.pulse1_envelopeStart; pulse1_envelopeDivider = s.pulse1_envelopeDivider; pulse1_decayLevel = s.pulse1_decayLevel;
            pulse2_envelopeStart = s.pulse2_envelopeStart; pulse2_envelopeDivider = s.pulse2_envelopeDivider; pulse2_decayLevel = s.pulse2_decayLevel;
            noise_envelopeStart = s.noise_envelopeStart; noise_envelopeDivider = s.noise_envelopeDivider; noise_decayLevel = s.noise_decayLevel;
            pulse1_constantVolume = s.pulse1_constantVolume; pulse1_lengthHalt = s.pulse1_lengthHalt; pulse2_constantVolume = s.pulse2_constantVolume; pulse2_lengthHalt = s.pulse2_lengthHalt;
            noise_constantVolume = s.noise_constantVolume; noise_lengthHalt = s.noise_lengthHalt; triangle_lengthHalt = s.triangle_lengthHalt; pulse1_output = s.pulse1_output; pulse2_output = s.pulse2_output; triangle_output = s.triangle_output; noise_output = s.noise_output;
            pulse1_lengthCounter = s.pulse1_lengthCounter; pulse2_lengthCounter = s.pulse2_lengthCounter; triangle_lengthCounter = s.triangle_lengthCounter; noise_lengthCounter = s.noise_lengthCounter;
            pulse1_sweepDivider = s.pulse1_sweepDivider; pulse1_sweepNegate = s.pulse1_sweepNegate; pulse1_sweepShift = s.pulse1_sweepShift; pulse1_sweepPeriod = s.pulse1_sweepPeriod; pulse1_sweepReload = s.pulse1_sweepReload;
            pulse2_sweepDivider = s.pulse2_sweepDivider; pulse2_sweepNegate = s.pulse2_sweepNegate; pulse2_sweepShift = s.pulse2_sweepShift; pulse2_sweepPeriod = s.pulse2_sweepPeriod; pulse2_sweepReload = s.pulse2_sweepReload;
            frameSequencerMode5 = s.frameSequencerMode5; frameIRQInhibit = s.frameIRQInhibit; frameSequenceStep = s.frameSequenceStep; cyclesToNextFrameEvent = s.cyclesToNextFrameEvent;
            lpLast = s.lpLast; dcLastIn = s.dcLastIn; dcLastOut = s.dcLastOut; pulse1_enabled = s.pulse1_enabled; pulse2_enabled = s.pulse2_enabled; triangle_enabled = s.triangle_enabled; noise_enabled = s.noise_enabled;
            fractionalSampleAccumulator = s.fractionalSampleAccumulator; ringWrite = s.ringWrite; ringRead = s.ringRead; ringCount = s.ringCount;
            dmc_ctrl = s.dmc_ctrl; dmc_directLoad = s.dmc_directLoad; dmc_addrReg = s.dmc_addrReg; dmc_lenReg = s.dmc_lenReg;
            dmc_enabled = s.dmc_enabled; dmc_loop = s.dmc_loop; dmc_irqEnable = s.dmc_irqEnable; dmc_irqFlag = s.dmc_irqFlag;
            dmc_timer = s.dmc_timer; dmc_timerPeriod = s.dmc_timerPeriod; dmc_sampleAddress = s.dmc_sampleAddress; dmc_sampleLengthRemaining = s.dmc_sampleLengthRemaining;
            dmc_shiftReg = s.dmc_shiftReg; dmc_bitsRemaining = s.dmc_bitsRemaining; dmc_deltaCounter = s.dmc_deltaCounter; dmc_sampleBuffer = s.dmc_sampleBuffer;
            dmc_sampleBufferFilled = s.dmc_sampleBufferFilled; dmc_silence = s.dmc_silence; dmc_output = s.dmc_output;
        }
        private void PostRestore()
        {
            pulse1_output = ComputePulseOutput(1);
            pulse2_output = ComputePulseOutput(2);
            triangle_output = TriangleSequenceValue(triangle_sequenceIndex);
            noise_output = ((noiseShiftRegister & 0x01) == 0) ? (noise_constantVolume ? noise_volumeParam : noise_decayLevel) : 0;
            // Clear audio buffers and filters so JS scheduling can restart without stale samples
            ringRead = ringWrite = ringCount = 0; fractionalSampleAccumulator = 0; lpLast = 0; dcLastIn = 0; dcLastOut = 0;
        }
        
        // Optional hook for bus resets/hot-swaps
        public void ClearAudioBuffers()
        {
            ringRead = ringWrite = ringCount = 0;
            fractionalSampleAccumulator = 0;
            lpLast = 0; dcLastIn = 0; dcLastOut = 0;
        }

        // Minimal Reset hook: drop queued audio and pacing
        public void Reset()
        {
            ClearAudioBuffers();
        }

        // Step 1 CPU cycle convenience
        public void Step() => Step(1);
        public void Step(int cpuCycles)
        {
            const double CpuFreq = 1789773.0;
            var sc = bus?.SpeedConfig;
            bool useHotPaths = sc?.ApuOpt_NewHotPaths != false; // umbrella
            // Granular sub-flags defaulting to umbrella unless explicitly disabled
            bool optBatchMix = useHotPaths && sc?.ApuOpt_BatchSampleMix != false;
            bool optBlockSilence = useHotPaths && sc?.ApuOpt_BlockSilenceFill != false;
            bool optInlinePulse = useHotPaths && sc?.ApuOpt_InlinePulseOutput != false; // used in ClockPulse
            bool optSingleDmcFetch = useHotPaths && sc?.ApuOpt_SingleDmcFetch != false; // used in ClockDmc
            // store for later methods (could make fields if needed)
            _optInlinePulse = optInlinePulse; _optSingleDmcFetch = optSingleDmcFetch; _optBlockSilence = optBlockSilence;
            // Fast path: only if enabled, batch size large enough, and all channels silent.
            if (optBlockSilence && sc?.ApuSilentChannelSkip == true && cpuCycles >= (sc?.ApuSilentSkipMinCycles ?? 0) && !IsAnyChannelAudible())
            {
                FastForwardSilent(cpuCycles, CpuFreq);
                return;
            }
            // Normal per-cycle stepping (kept simple for determinism)
            for (int c = 0; c < cpuCycles; c++)
            {
                StepFrameSequencer();
                ClockPulse(ref pulse1_timer, ref pulse1_timerCounter, ref pulse1_sequenceIndex, 1);
                ClockPulse(ref pulse2_timer, ref pulse2_timerCounter, ref pulse2_sequenceIndex, 2);
                ClockTriangle();
                ClockNoise();
                ClockDmc();
            }
            double samplesFloat = cpuCycles * audioSampleRate / CpuFreq;
            fractionalSampleAccumulator += samplesFloat;
            int samplesToProduce = (int)fractionalSampleAccumulator;
            if (samplesToProduce <= 0) return;
            fractionalSampleAccumulator -= samplesToProduce;
            if (optBatchMix)
            {
                // Batch generate samples with cached flags to reduce per-sample overhead
                GenerateAudioSamplesBatch(samplesToProduce);
            }
            else
            {
                for (int i = 0; i < samplesToProduce; i++) GenerateAudioSample_Legacy();
            }
        }

        private bool IsAnyChannelAudible()
        {
            if (pulse1_enabled && pulse1_lengthCounter > 0 && pulse1_timer >= 8) return true;
            if (pulse2_enabled && pulse2_lengthCounter > 0 && pulse2_timer >= 8) return true;
            if (triangle_enabled && triangle_lengthCounter > 0 && triangle_linearCounter > 0 && triangle_timer >= 2) return true;
            if (noise_enabled && noise_lengthCounter > 0) return true;
            if (bus?.SpeedConfig?.ApuFeat_DmcChannel == true && dmc_enabled && dmc_sampleLengthRemaining > 0) return true;
            return false;
        }

        private void FastForwardSilent(int cpuCycles, double cpuFreq)
        {
            perf_silentSkipBatches++;
            perf_silentSkipCycles += cpuCycles;
            int remaining = cpuCycles;
            while (remaining > 0)
            {
                if (cyclesToNextFrameEvent > 0)
                {
                    int advance = cyclesToNextFrameEvent < remaining ? cyclesToNextFrameEvent : remaining;
                    remaining -= advance;
                    cyclesToNextFrameEvent -= advance;
                    fractionalSampleAccumulator += advance * audioSampleRate / cpuFreq;
                    if (cyclesToNextFrameEvent > 0) continue;
                }
                ProcessFrameSequencerEvent();
            }
            int silenceSamples = (int)fractionalSampleAccumulator;
            if (silenceSamples <= 0) return;
            fractionalSampleAccumulator -= silenceSamples;
            WriteSilenceSamples(silenceSamples);
            perf_silentSkipSamples += silenceSamples;
        }

        private void WriteSilenceSamples(int count)
        {
            if (!_optBlockSilence)
            {
                // legacy per-sample silence writer
                for (int i = 0; i < count; i++)
                {
                    if (ringCount >= AudioRingSize)
                    { ringRead = (ringRead + 1) & (AudioRingSize - 1); ringCount--; }
                    audioRing[ringWrite] = 0f; ringWrite = (ringWrite + 1) & (AudioRingSize - 1); ringCount++;
                }
                return;
            }
            // optimized block writer
            if (count <= 0) return;
            if (count >= AudioRingSize)
            { Array.Clear(audioRing, 0, AudioRingSize); ringRead = 0; ringWrite = 0; ringCount = AudioRingSize; return; }
            int neededDrop = ringCount + count - AudioRingSize;
            if (neededDrop > 0)
            { ringRead = (ringRead + neededDrop) & (AudioRingSize - 1); ringCount -= neededDrop; if (ringCount < 0) ringCount = 0; }
            int first = Math.Min(count, AudioRingSize - ringWrite);
            if (first > 0)
            { Array.Clear(audioRing, ringWrite, first); ringWrite = (ringWrite + first) & (AudioRingSize - 1); }
            int remaining = count - first;
            if (remaining > 0)
            { Array.Clear(audioRing, 0, remaining); ringWrite = remaining & (AudioRingSize - 1); }
            ringCount += count;
        }

        private void ProcessFrameSequencerEvent()
        {
            switch (frameSequenceStep)
            {
                case 0: QuarterFrameTick(); cyclesToNextFrameEvent = 3728; break;
                case 1: QuarterFrameTick(); HalfFrameTick(); cyclesToNextFrameEvent = 3729; break;
                case 2: QuarterFrameTick(); cyclesToNextFrameEvent = 2730; break;
                case 3: QuarterFrameTick(); HalfFrameTick(); cyclesToNextFrameEvent = 3729; break;
            }
            frameSequenceStep = (frameSequenceStep + 1) & 0x03;
        }

        private void GenerateAudioSamplesBatch(int count)
        {
            if (count <= 0) return;
            var sc = bus?.SpeedConfig;
            bool featDmc = sc?.ApuFeat_DmcChannel == true;
            bool lutMix = sc?.ApuFeat_LutMixing == true;
            bool softClip = sc?.ApuFeat_SoftClip == true;
            if (lutMix) BuildMixLuts();
            int localWrite = ringWrite;
            int localRead = ringRead;
            int localCount = ringCount;
            float localLp = lpLast, localDcIn = dcLastIn, localDcOut = dcLastOut;
            float[] ring = audioRing;
            byte s = status;
            for (int i = 0; i < count; i++)
            {
                if (localCount >= AudioRingSize)
                { localRead = (localRead + 1) & (AudioRingSize - 1); localCount--; }
                double p1 = (pulse1_enabled && (s & 0x01) != 0 && pulse1_lengthCounter > 0 && pulse1_timer >= 8) ? pulse1_output : 0.0;
                double p2 = (pulse2_enabled && (s & 0x02) != 0 && pulse2_lengthCounter > 0 && pulse2_timer >= 8) ? pulse2_output : 0.0;
                double pSum = p1 + p2;
                double t = (triangle_enabled && (s & 0x04) != 0 && triangle_lengthCounter > 0 && triangle_linearCounter > 0 && triangle_timer >= 2) ? triangle_output : 0.0;
                double n = (noise_enabled && (s & 0x08) != 0 && noise_lengthCounter > 0) ? noise_output : 0.0;
                double d = (featDmc && dmc_enabled) ? dmc_output : 0.0;
                float mixed;
                if (lutMix)
                {
                    int pulseIndex = (int)pSum; if (pulseIndex > 30) pulseIndex = 30;
                    float pulseMix = PulseMixLut[pulseIndex];
                    int tInt = (int)t; if (tInt > 15) tInt = 15;
                    int nInt = (int)n; if (nInt > 15) nInt = 15;
                    int dInt = (int)d; if (dInt > 127) dInt = 127;
                    int tndIndex = (tInt << T_SHIFT) | (nInt << N_SHIFT) | dInt;
                    float tndMix = TndMixLut[tndIndex];
                    mixed = pulseMix + tndMix;
                    if (softClip) mixed = (float)Math.Tanh(mixed * 2.2f);
                }
                else
                {
                    double pulseMix = pSum == 0 ? 0.0 : 95.88 / (8128.0 / pSum + 100.0);
                    double tndSum = t + n + d;
                    double tnd = tndSum == 0 ? 0.0 : 159.79 / (1.0 / ((t / 8227.0) + (n / 12241.0) + (d / 22638.0)) + 100.0);
                    mixed = (float)(pulseMix + tnd);
                    if (softClip) mixed = (float)Math.Tanh(mixed * 2.2f);
                }
                localLp += (mixed - localLp) * LowPassCoeff; float filtered = localLp;
                const float R = 0.995f; float hp = filtered - localDcIn + R * localDcOut; localDcIn = filtered; localDcOut = hp;
                ring[localWrite] = hp * 0.79515f; localWrite = (localWrite + 1) & (AudioRingSize - 1); localCount++;
            }
            ringWrite = localWrite; ringRead = localRead; ringCount = localCount;
            lpLast = localLp; dcLastIn = localDcIn; dcLastOut = localDcOut;
        }

        // Legacy single-sample generation retained for regression isolation
        private void GenerateAudioSample_Legacy()
        {
            if (ringCount >= AudioRingSize)
            { ringRead = (ringRead + 1) & (AudioRingSize - 1); ringCount--; }
            double p1 = (pulse1_enabled && (status & 0x01) != 0 && pulse1_lengthCounter > 0 && pulse1_timer >= 8) ? pulse1_output : 0.0;
            double p2 = (pulse2_enabled && (status & 0x02) != 0 && pulse2_lengthCounter > 0 && pulse2_timer >= 8) ? pulse2_output : 0.0;
            double pSum = p1 + p2;
            double t = (triangle_enabled && (status & 0x04) != 0 && triangle_lengthCounter > 0 && triangle_linearCounter > 0 && triangle_timer >= 2) ? triangle_output : 0.0;
            double n = (noise_enabled && (status & 0x08) != 0 && noise_lengthCounter > 0) ? noise_output : 0.0;
            double d = (bus?.SpeedConfig?.ApuFeat_DmcChannel == true && dmc_enabled) ? dmc_output : 0.0;
            float mixed;
            if (bus?.SpeedConfig?.ApuFeat_LutMixing == true)
            {
                BuildMixLuts();
                int pulseIndex = (int)pSum; if (pulseIndex > 30) pulseIndex = 30;
                float pulseMix = PulseMixLut[pulseIndex];
                int tInt = (int)t; if (tInt > 15) tInt = 15;
                int nInt = (int)n; if (nInt > 15) nInt = 15;
                int dInt = (int)d; if (dInt > 127) dInt = 127;
                int tndIndex = (tInt << T_SHIFT) | (nInt << N_SHIFT) | dInt;
                float tndMix = TndMixLut[tndIndex];
                mixed = pulseMix + tndMix;
                if (bus.SpeedConfig.ApuFeat_SoftClip)
                    mixed = (float)Math.Tanh(mixed * 2.2f);
            }
            else
            {
                double pulseMix = pSum == 0 ? 0.0 : 95.88 / (8128.0 / pSum + 100.0);
                double tndSum = t + n + d;
                double tnd = tndSum == 0 ? 0.0 : 159.79 / (1.0 / ((t / 8227.0) + (n / 12241.0) + (d / 22638.0)) + 100.0);
                mixed = (float)(pulseMix + tnd);
                if (bus?.SpeedConfig?.ApuFeat_SoftClip == true)
                    mixed = (float)Math.Tanh(mixed * 2.2f);
            }
            lpLast += (mixed - lpLast) * LowPassCoeff; float filtered = lpLast;
            const float R = 0.995f; float hp = filtered - dcLastIn + R * dcLastOut; dcLastIn = filtered; dcLastOut = hp;
            audioRing[ringWrite] = hp * 0.79515f; ringWrite = (ringWrite + 1) & (AudioRingSize - 1); ringCount++;
        }

        // LUTs for nonlinear mixing (optional)
    private static float[] PulseMixLut = null!; // 31 entries
    private static float[] TndMixLut = null!;   // 16*16*128 entries
        private static bool mixLutsBuilt;
        private const int T_SHIFT = 11; // triangle component bits position
        private const int N_SHIFT = 7;  // noise component bits position
        private static void BuildMixLuts()
        {
            if (mixLutsBuilt) return;
            PulseMixLut = new float[31];
            for (int sum = 0; sum < PulseMixLut.Length; sum++)
                PulseMixLut[sum] = sum == 0 ? 0f : (95.88f / (8128f / sum + 100f));
            TndMixLut = new float[16 * 16 * 128];
            int idx = 0;
            for (int tt = 0; tt < 16; tt++)
            {
                float tf = tt / 8227f;
                for (int nn = 0; nn < 16; nn++)
                {
                    float nf = nn / 12241f;
                    for (int dd = 0; dd < 128; dd++, idx++)
                    {
                        if (tt == 0 && nn == 0 && dd == 0) { TndMixLut[idx] = 0f; continue; }
                        float df = dd / 22638f;
                        float inv = (1.0f / (tf + nf + df)) + 100f;
                        TndMixLut[idx] = 159.79f / inv;
                    }
                }
            }
            mixLutsBuilt = true;
        }

        private static readonly byte[][] PulseDutyTable = new byte[][]
        {
            new byte[8]{0,1,0,0,0,0,0,0},
            new byte[8]{0,1,1,0,0,0,0,0},
            new byte[8]{0,1,1,1,1,0,0,0},
            new byte[8]{1,0,0,1,1,1,1,1},
        };

        public void WriteAPURegister(ushort address, byte value)
        {
            switch (address)
            {
                case 0x4000:
                    pulse1_duty = (byte)((value >> 6) & 0x03); pulse1_lengthHalt = (value & 0x20) != 0; pulse1_constantVolume = (value & 0x10) != 0; pulse1_volumeParam = value & 0x0F; pulse1_envelopeStart = true; pulse1_enabled = true; pulse1_output = ComputePulseOutput(1); break;
                case 0x4001:
                    pulse1_sweep = value; pulse1_sweepPeriod = ((value >> 4) & 0x07) + 1; pulse1_sweepNegate = (value & 0x08) != 0; pulse1_sweepShift = value & 0x07; pulse1_sweepReload = true; pulse1_output = ComputePulseOutput(1); break;
                case 0x4002: pulse1_timer = (ushort)((pulse1_timer & 0xFF00) | value); break;
                case 0x4003:
                    pulse1_timer = (ushort)((pulse1_timer & 0x00FF) | ((value & 0x07) << 8)); pulse1_length = (byte)((value >> 3) & 0x1F); if (pulse1_length < LengthTable.Length) pulse1_lengthCounter = LengthTable[pulse1_length]; pulse1_sequenceIndex = 0; pulse1_envelopeStart = true; pulse1_timerCounter = pulse1_timer + 1; pulse1_output = ComputePulseOutput(1); break;
                case 0x4004:
                    pulse2_duty = (byte)((value >> 6) & 0x03); pulse2_lengthHalt = (value & 0x20) != 0; pulse2_constantVolume = (value & 0x10) != 0; pulse2_volumeParam = value & 0x0F; pulse2_envelopeStart = true; pulse2_enabled = true; pulse2_output = ComputePulseOutput(2); break;
                case 0x4005:
                    pulse2_sweep = value; pulse2_sweepPeriod = ((value >> 4) & 0x07) + 1; pulse2_sweepNegate = (value & 0x08) != 0; pulse2_sweepShift = value & 0x07; pulse2_sweepReload = true; pulse2_output = ComputePulseOutput(2); break;
                case 0x4006: pulse2_timer = (ushort)((pulse2_timer & 0xFF00) | value); break;
                case 0x4007:
                    pulse2_timer = (ushort)((pulse2_timer & 0x00FF) | ((value & 0x07) << 8)); pulse2_length = (byte)((value >> 3) & 0x1F); if (pulse2_length < LengthTable.Length) pulse2_lengthCounter = LengthTable[pulse2_length]; pulse2_sequenceIndex = 0; pulse2_envelopeStart = true; pulse2_timerCounter = pulse2_timer + 1; pulse2_output = ComputePulseOutput(2); break;
                case 0x4008:
                    triangle_linear = value; triangle_lengthHalt = (value & 0x80) != 0; triangle_linearReloadFlag = true; triangle_enabled = true; break;
                case 0x400A: triangle_timer = (ushort)((triangle_timer & 0xFF00) | value); break;
                case 0x400B:
                    triangle_timer = (ushort)((triangle_timer & 0x00FF) | ((value & 0x07) << 8)); triangle_length = (byte)((value >> 3) & 0x1F); if (triangle_length < LengthTable.Length) triangle_lengthCounter = LengthTable[triangle_length]; triangle_sequenceIndex = 0; triangle_linearReloadFlag = true; triangle_timerCounter = triangle_timer + 1; break;
                case 0x400C:
                    noise_lengthHalt = (value & 0x20) != 0; noise_constantVolume = (value & 0x10) != 0; noise_volumeParam = value & 0x0F; noise_envelopeStart = true; noise_enabled = true; break;
                case 0x400E: noise_period = value; break;
                case 0x400F:
                    noise_length = value; int nIdx = (value >> 3) & 0x1F; if (nIdx < LengthTable.Length) noise_lengthCounter = LengthTable[nIdx]; noise_envelopeStart = true; break;
                case 0x4010:
                    if (bus?.SpeedConfig?.ApuFeat_DmcChannel == true)
                    {
                        dmc_ctrl = value; dmc_irqEnable = (value & 0x80) != 0; dmc_loop = (value & 0x40) != 0; int rateIndex = value & 0x0F; dmc_timerPeriod = DmcRateTable[rateIndex];
                        dmc_silence = !dmc_sampleBufferFilled; // maintain state
                    }
                    break;
                case 0x4011:
                    if (bus?.SpeedConfig?.ApuFeat_DmcChannel == true)
                    {
                        dmc_directLoad = value; dmc_deltaCounter = value & 0x7F; dmc_output = dmc_deltaCounter;
                    }
                    break;
                case 0x4012:
                    if (bus?.SpeedConfig?.ApuFeat_DmcChannel == true)
                    { dmc_addrReg = value; }
                    break;
                case 0x4013:
                    if (bus?.SpeedConfig?.ApuFeat_DmcChannel == true)
                    { dmc_lenReg = value; }
                    break;
                case 0x4015:
                    status = value; if ((value & 0x01) == 0) pulse1_enabled = false; if ((value & 0x02) == 0) pulse2_enabled = false; if ((value & 0x04) == 0) triangle_enabled = false; if ((value & 0x08) == 0) noise_enabled = false;
                    if (bus?.SpeedConfig?.ApuFeat_DmcChannel == true)
                    {
                        bool en = (value & 0x10) != 0;
                        if (en && !dmc_enabled) StartDmc();
                        dmc_enabled = en; if (!dmc_enabled) { dmc_sampleLengthRemaining = 0; }
                        dmc_irqFlag = false;
                    }
                    break;
                case 0x4017:
                    frameSequencerMode5 = (value & 0x80) != 0; frameIRQInhibit = (value & 0x40) != 0;
                    if (bus?.SpeedConfig?.ApuFeat_FrameSequencer == true)
                    {
                        // Reset absolute sequencer counters
                        abs_frameStep = 0; abs_frameCycle = 0; abs_nextEventCycle = ABS_EV0;
                        if (frameSequencerMode5 && (value & 0x80) != 0 && bus.SpeedConfig.ApuFeat_4017ImmediateTick)
                        {
                            // Immediate Quarter + Half tick like hardware behavior in 5-step mode
                            QuarterFrameTick(); HalfFrameTick();
                        }
                    }
                    else
                    {
                        frameSequenceStep = 0; cyclesToNextFrameEvent = 3729; // legacy reset
                    }
                    break;
            }
        }

        public byte ReadAPURegister(ushort address)
        {
            if (address == 0x4015)
            {
                byte s = 0;
                if (pulse1_lengthCounter > 0) s |= 0x01;
                if (pulse2_lengthCounter > 0) s |= 0x02;
                if (triangle_lengthCounter > 0) s |= 0x04;
                if (noise_lengthCounter > 0) s |= 0x08;
                if (bus?.SpeedConfig?.ApuFeat_DmcChannel == true && dmc_sampleLengthRemaining > 0) s |= 0x10;
                if (bus?.SpeedConfig?.ApuFeat_DmcChannel == true && dmc_irqFlag) s |= 0x80;
                dmc_irqFlag = false; // reading clears DMC IRQ flag
                return s;
            }
            return 0;
        }

        public float[] GetAudioSamples(int maxSamples = 0)
        {
            if (ringCount == 0) return Array.Empty<float>();
            int toRead = ringCount; if (maxSamples > 0 && maxSamples < toRead) toRead = maxSamples; if (toRead > 4096 && maxSamples <= 0) toRead = 4096;
            float[] result = new float[toRead]; int first = Math.Min(toRead, AudioRingSize - ringRead); Array.Copy(audioRing, ringRead, result, 0, first); int remaining = toRead - first; if (remaining > 0) Array.Copy(audioRing, 0, result, first, remaining);
            ringRead = (ringRead + toRead) & (AudioRingSize - 1); ringCount -= toRead; if (ringCount < 0) ringCount = 0; return result;
        }
        public float[] GetAudioBuffer() => GetAudioSamples();
        public int GetQueuedSampleCount() => ringCount;
        public int GetSampleRate() => audioSampleRate;

        private void QuarterFrameTick()
        {
            ClockEnvelope(ref pulse1_envelopeStart, ref pulse1_envelopeDivider, ref pulse1_decayLevel, pulse1_volumeParam, pulse1_lengthHalt, pulse1_constantVolume);
            ClockEnvelope(ref pulse2_envelopeStart, ref pulse2_envelopeDivider, ref pulse2_decayLevel, pulse2_volumeParam, pulse2_lengthHalt, pulse2_constantVolume);
            ClockEnvelope(ref noise_envelopeStart, ref noise_envelopeDivider, ref noise_decayLevel, noise_volumeParam, noise_lengthHalt, noise_constantVolume);
            if (triangle_linearReloadFlag) triangle_linearCounter = triangle_linear & 0x7F; else if (triangle_linearCounter > 0) triangle_linearCounter--; if ((triangle_linear & 0x80) == 0) triangle_linearReloadFlag = false;
        }
        private void HalfFrameTick()
        { if (!pulse1_lengthHalt && pulse1_lengthCounter > 0) pulse1_lengthCounter--; if (!pulse2_lengthHalt && pulse2_lengthCounter > 0) pulse2_lengthCounter--; if (!triangle_lengthHalt && triangle_lengthCounter > 0) triangle_lengthCounter--; if (!noise_lengthHalt && noise_lengthCounter > 0) noise_lengthCounter--; DoSweep(ref pulse1_timer, pulse1_sweepNegate, pulse1_sweepShift, pulse1_sweepPeriod, ref pulse1_sweepDivider, ref pulse1_sweepReload); DoSweep(ref pulse2_timer, pulse2_sweepNegate, pulse2_sweepShift, pulse2_sweepPeriod, ref pulse2_sweepDivider, ref pulse2_sweepReload); }
        private void DoSweep(ref ushort timer, bool negate, int shift, int period, ref int divider, ref bool reload)
        { if (shift == 0 || period == 0) { if (reload) reload = false; return; } if (reload) { divider = period; reload = false; return; } if (divider > 0) { divider--; } else { divider = period; int change = timer >> shift; int target = negate ? (timer - change - 1) : (timer + change); if (target >= 8 && target <= 0x7FF) timer = (ushort)target; } }
        private void ClockEnvelope(ref bool start, ref int divider, ref int decayLevel, int volumeParam, bool loop, bool constantVolume)
        {
            // If constant volume and skip toggle enabled, envelope does not decay; keep decayLevel fixed at volumeParam.
            if (constantVolume && bus?.SpeedConfig?.ApuSkipEnvelopeOnConstantVolume == true)
            {
                // Ensure decay level matches param (initialization) and clear start flag.
                if (start)
                {
                    start = false;
                    decayLevel = volumeParam; // mirror static volume
                    divider = volumeParam + 1; // keep stable (not used further)
                }
                perf_envelopeSkipEvents++;
                return;
            }
            if (start)
            {
                start = false; decayLevel = 15; divider = volumeParam + 1;
            }
            else
            {
                if (divider > 0) divider--;
                else
                {
                    divider = volumeParam + 1;
                    if (decayLevel > 0) decayLevel--;
                    else if (loop) decayLevel = 15;
                }
            }
        }

        // Expose perf counters for telemetry UI / diagnostics
        public (long silentBatches, long silentCycles, long silentSamples, long envelopeSkips) GetPerfCounters()
            => (perf_silentSkipBatches, perf_silentSkipCycles, perf_silentSkipSamples, perf_envelopeSkipEvents);
        private void StepFrameSequencer()
        {
            if (bus?.SpeedConfig?.ApuFeat_FrameSequencer != true)
            {
                // Legacy relative timing
                cyclesToNextFrameEvent--; if (cyclesToNextFrameEvent > 0) return;
                switch (frameSequenceStep)
                {
                    case 0: QuarterFrameTick(); cyclesToNextFrameEvent = 3728; break;
                    case 1: QuarterFrameTick(); HalfFrameTick(); cyclesToNextFrameEvent = 3729; break;
                    case 2: QuarterFrameTick(); cyclesToNextFrameEvent = 2730; break;
                    case 3: QuarterFrameTick(); HalfFrameTick(); cyclesToNextFrameEvent = 3729; break;
                }
                frameSequenceStep = (frameSequenceStep + 1) & 0x03; return;
            }
            // Absolute-cycle model
            abs_frameCycle++;
            if (abs_frameCycle < abs_nextEventCycle) return;
            if (!frameSequencerMode5)
            {
                switch (abs_frameStep)
                {
                    case 0: QuarterFrameTick(); abs_nextEventCycle = ABS_EV1; break;
                    case 1: QuarterFrameTick(); HalfFrameTick(); abs_nextEventCycle = ABS_EV2; break;
                    case 2: QuarterFrameTick(); abs_nextEventCycle = ABS_EV3; break;
                    case 3:
                        QuarterFrameTick(); HalfFrameTick();
                        abs_frameStep = -1; abs_frameCycle -= (ABS_EV3 + 1); abs_nextEventCycle = ABS_EV0; break;
                }
            }
            else
            {
                switch (abs_frameStep)
                {
                    case 0: QuarterFrameTick(); HalfFrameTick(); abs_nextEventCycle = ABS_EV1; break;
                    case 1: QuarterFrameTick(); abs_nextEventCycle = ABS_EV2; break;
                    case 2: QuarterFrameTick(); HalfFrameTick(); abs_nextEventCycle = ABS_EV3; break;
                    case 3: QuarterFrameTick(); abs_nextEventCycle = ABS_EV4; break;
                    case 4: QuarterFrameTick(); abs_frameStep = -1; abs_frameCycle -= (ABS_EV4 + 1); abs_nextEventCycle = ABS_EV0; break;
                }
            }
            abs_frameStep++;
        }
        private void ClockPulse(ref ushort timer, ref int counter, ref int seqIndex, int which)
        {
            if (timer < 8) return;
            if (counter > 0) counter--;
            if (counter > 0) return;
            counter += (timer + 1) * 2;
            seqIndex = (seqIndex + 1) & 7;
            // Inline hot ComputePulseOutput logic
            if (_optInlinePulse)
            {
                bool sweepMute = false;
                if (bus?.SpeedConfig?.ApuFeat_SweepMutePrediction == true)
                {
                    if (which == 1) sweepMute = SweepWouldMute(pulse1_timer, pulse1_sweepNegate, pulse1_sweepShift, true);
                    else sweepMute = SweepWouldMute(pulse2_timer, pulse2_sweepNegate, pulse2_sweepShift, false);
                }
                if (which == 1)
                {
                    if (!pulse1_enabled || pulse1_lengthCounter == 0 || pulse1_timer < 8 || sweepMute) { pulse1_output = 0; return; }
                    var pattern = PulseDutyTable[pulse1_duty & 3]; int bit = pattern[pulse1_sequenceIndex]; int vol = pulse1_constantVolume ? pulse1_volumeParam : pulse1_decayLevel; pulse1_output = bit == 1 ? vol : 0;
                }
                else
                {
                    if (!pulse2_enabled || pulse2_lengthCounter == 0 || pulse2_timer < 8 || sweepMute) { pulse2_output = 0; return; }
                    var pattern = PulseDutyTable[pulse2_duty & 3]; int bit = pattern[pulse2_sequenceIndex]; int vol = pulse2_constantVolume ? pulse2_volumeParam : pulse2_decayLevel; pulse2_output = bit == 1 ? vol : 0;
                }
            }
            else
            {
                if (which == 1) pulse1_output = ComputePulseOutput(1); else pulse2_output = ComputePulseOutput(2);
            }
        }
        private int ComputePulseOutput(int which)
        {
            bool sweepMute = false;
            if (bus?.SpeedConfig?.ApuFeat_SweepMutePrediction == true)
            {
                if (which == 1) sweepMute = SweepWouldMute(pulse1_timer, pulse1_sweepNegate, pulse1_sweepShift, true);
                else sweepMute = SweepWouldMute(pulse2_timer, pulse2_sweepNegate, pulse2_sweepShift, false);
            }
            if (which == 1)
            {
                if (!pulse1_enabled || pulse1_lengthCounter == 0 || pulse1_timer < 8 || sweepMute) return 0;
                var pattern = PulseDutyTable[pulse1_duty & 3]; int bit = pattern[pulse1_sequenceIndex]; int vol = pulse1_constantVolume ? pulse1_volumeParam : pulse1_decayLevel; return bit == 1 ? vol : 0;
            }
            else
            {
                if (!pulse2_enabled || pulse2_lengthCounter == 0 || pulse2_timer < 8 || sweepMute) return 0;
                var pattern = PulseDutyTable[pulse2_duty & 3]; int bit = pattern[pulse2_sequenceIndex]; int vol = pulse2_constantVolume ? pulse2_volumeParam : pulse2_decayLevel; return bit == 1 ? vol : 0;
            }
        }
        private bool SweepWouldMute(ushort timer, bool negate, int shift, bool channel1)
        {
            if (shift == 0) return false;
            int change = timer >> shift;
            int adjust = negate ? (timer - change - (channel1 ? 1 : 0)) : (timer + change);
            if (adjust > 0x7FF) return true; if (adjust < 8) return true; return false;
        }
        private void ClockTriangle()
        { if (!triangle_enabled || triangle_lengthCounter == 0 || triangle_linearCounter == 0 || triangle_timer < 2) return; if (triangle_timerCounter > 0) triangle_timerCounter--; if (triangle_timerCounter <= 0) { triangle_timerCounter += (triangle_timer + 1); triangle_sequenceIndex = (triangle_sequenceIndex + 1) & 31; triangle_output = TriangleSequenceValue(triangle_sequenceIndex); } }
        private int TriangleSequenceValue(int index) => index < 16 ? 15 - index : index - 16;
        private void ClockNoise()
        { if (!noise_enabled || noise_lengthCounter == 0) return; if (noise_timerCounter > 0) noise_timerCounter--; if (noise_timerCounter <= 0) { int periodIndex = noise_period & 0x0F; noise_timerCounter += NoisePeriods[periodIndex]; int bit0 = noiseShiftRegister & 0x01; int bit1 = ((noise_period & 0x80) != 0) ? ((noiseShiftRegister >> 6) & 0x01) : ((noiseShiftRegister >> 1) & 0x01); int feedback = bit0 ^ bit1; noiseShiftRegister = (ushort)((noiseShiftRegister >> 1) | (feedback << 14)); int vol = noise_constantVolume ? noise_volumeParam : noise_decayLevel; noise_output = ((noiseShiftRegister & 0x01) == 0) ? vol : 0; } }
        private void ClockDmc()
        {
            if (bus?.SpeedConfig?.ApuFeat_DmcChannel != true || !dmc_enabled) return;
            if (dmc_timer > 0) dmc_timer--; if (dmc_timer > 0) return; dmc_timer += dmc_timerPeriod;
            if (!dmc_silence)
            {
                if ((dmc_shiftReg & 1) != 0) { if (dmc_deltaCounter <= 125) dmc_deltaCounter += 2; }
                else { if (dmc_deltaCounter >= 2) dmc_deltaCounter -= 2; }
            }
            dmc_shiftReg >>= 1; dmc_bitsRemaining--;
            if (dmc_bitsRemaining == 0)
            {
                if (dmc_sampleBufferFilled)
                { dmc_silence = false; dmc_shiftReg = dmc_sampleBuffer; dmc_bitsRemaining = 8; dmc_sampleBufferFilled = false; }
                else { dmc_silence = true; dmc_bitsRemaining = 8; }
                TryDmcFetch();
            }
            if (!_optSingleDmcFetch)
            {
                // legacy second fetch attempt
                TryDmcFetch();
            }
            dmc_output = dmc_deltaCounter;
        }
        private void TryDmcFetch()
        {
            if (dmc_sampleBufferFilled || dmc_sampleLengthRemaining == 0) return;
            byte sample = bus.Read((ushort)dmc_sampleAddress);
            dmc_sampleAddress++; if (dmc_sampleAddress > 0xFFFF) dmc_sampleAddress = 0x8000;
            dmc_sampleLengthRemaining--; dmc_sampleBuffer = sample; dmc_sampleBufferFilled = true;
            if (dmc_sampleLengthRemaining == 0)
            {
                if (dmc_loop) { RestartDmc(); }
                else if (dmc_irqEnable)
                {
                    dmc_irqFlag = true;
                    if (bus?.SpeedConfig?.ApuFeat_DmcIrq == true)
                    {
                        bus?.cpu?.RequestIRQ(true);
                    }
                }
            }
        }
        private void StartDmc()
        { RestartDmc(); dmc_timer = 1; dmc_bitsRemaining = 8; dmc_silence = !dmc_sampleBufferFilled; }
        private void RestartDmc()
        { dmc_sampleAddress = 0xC000 + (dmc_addrReg << 6); dmc_sampleLengthRemaining = (dmc_lenReg << 4) + 1; }
    }
}
