using System;

namespace NesEmulator
{
    // Legacy / famiclone-style APU core (renamed from APU_JANK.cs / class APUJANK)
    // THIS IS A BACKUP OF THE PREVIOUS APU WHICH HAD ISSUES. DO NOT DELETE WITHOUT REVIEW.
    public class APU_SPD : IAPU
    {
        // Core metadata
        public string CoreName => "Speed";
        public string Description => "Based on the Low Power (LOW) core, this variant adds speedhacks for faster emulation.";
        public int Performance => 0;
        public int Rating => 5;
        private Bus bus;
    public APU_SPD(Bus bus)
    {
            this.bus = bus;
        }

        // APU Registers
        private byte pulse1_duty, pulse1_length, pulse1_sweep; // removed unused raw envelope field
        private ushort pulse1_timer;
        private byte pulse2_duty, pulse2_length, pulse2_sweep; // removed unused raw envelope field
        private ushort pulse2_timer;
        private byte triangle_linear, triangle_length;
        private ushort triangle_timer;
        private byte noise_length, noise_period; // removed unused noise_envelope & noise_shift
                                                 // Removed unused prelim DMC channel placeholder fields (dmc_*) pending future implementation
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
        // Frame sequencer
        private bool frameSequencerMode5 = false; // default 4-step
        private bool frameIRQInhibit = false;
        private int frameSequenceStep = 0;
        private int cyclesToNextFrameEvent = 3729; // first quarter-frame tick
                                                   // Noise LFSR
        private ushort noiseShiftRegister = 1;
        // Filters state
        private float lpLast = 0f, dcLastIn = 0f, dcLastOut = 0f;
        // Channel enables
        private bool pulse1_enabled = false, pulse2_enabled = false, triangle_enabled = false, noise_enabled = false;
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
            // Fast path: only if enabled, batch size large enough, and all channels silent.
            if (bus?.SpeedConfig?.ApuSilentChannelSkip == true && cpuCycles >= (bus.SpeedConfig?.ApuSilentSkipMinCycles ?? 0) && !IsAnyChannelAudible())
            {
                FastForwardSilent(cpuCycles, CpuFreq);
                return;
            }
            // Normal per-cycle stepping (kept simple for determinism)
            for (int c = 0; c < cpuCycles; c++)
            {
                StepFrameSequencer();
                ClockPulse(ref pulse1_timer, ref pulse1_timerCounter, ref pulse1_sequenceIndex);
                ClockPulse(ref pulse2_timer, ref pulse2_timerCounter, ref pulse2_sequenceIndex);
                ClockTriangle();
                ClockNoise();
            }
            double samplesFloat = cpuCycles * audioSampleRate / CpuFreq;
            fractionalSampleAccumulator += samplesFloat;
            int samplesToProduce = (int)fractionalSampleAccumulator;
            if (samplesToProduce <= 0) return;
            fractionalSampleAccumulator -= samplesToProduce;
            for (int i = 0; i < samplesToProduce; i++) GenerateAudioSample();
        }

        private bool IsAnyChannelAudible()
        {
            if (pulse1_enabled && pulse1_lengthCounter > 0 && pulse1_timer >= 8) return true;
            if (pulse2_enabled && pulse2_lengthCounter > 0 && pulse2_timer >= 8) return true;
            if (triangle_enabled && triangle_lengthCounter > 0 && triangle_linearCounter > 0 && triangle_timer >= 2) return true;
            if (noise_enabled && noise_lengthCounter > 0) return true;
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
            for (int i = 0; i < count; i++)
            {
                if (ringCount >= AudioRingSize)
                {
                    ringRead = (ringRead + 1) & (AudioRingSize - 1);
                    ringCount--;
                }
                audioRing[ringWrite] = 0f;
                ringWrite = (ringWrite + 1) & (AudioRingSize - 1);
                ringCount++;
            }
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

        private void GenerateAudioSample()
        {
            if (ringCount >= AudioRingSize)
            { ringRead = (ringRead + 1) & (AudioRingSize - 1); ringCount--; }
            double p1 = (pulse1_enabled && (status & 0x01) != 0 && pulse1_lengthCounter > 0 && pulse1_timer >= 8) ? pulse1_output : 0.0;
            double p2 = (pulse2_enabled && (status & 0x02) != 0 && pulse2_lengthCounter > 0 && pulse2_timer >= 8) ? pulse2_output : 0.0;
            double t = (triangle_enabled && (status & 0x04) != 0 && triangle_lengthCounter > 0 && triangle_linearCounter > 0 && triangle_timer >= 2) ? triangle_output : 0.0;
            double n = (noise_enabled && (status & 0x08) != 0 && noise_lengthCounter > 0) ? noise_output : 0.0;
            double pulseMix = (p1 + p2) == 0 ? 0.0 : 95.88 / (8128.0 / (p1 + p2) + 100.0);
            double tnd = (t + n) == 0 ? 0.0 : 159.79 / (1.0 / (t / 8227.0 + n / 12241.0) + 100.0);
            float mixed = (float)(pulseMix + tnd);
            mixed = (float)Math.Tanh(mixed * 2.2f);
            lpLast += (mixed - lpLast) * LowPassCoeff; float filtered = lpLast;
            const float R = 0.995f; float hp = filtered - dcLastIn + R * dcLastOut; dcLastIn = filtered; dcLastOut = hp;
            audioRing[ringWrite] = hp * 0.79515f; ringWrite = (ringWrite + 1) & (AudioRingSize - 1); ringCount++;
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
                    pulse1_duty = (byte)((value >> 6) & 0x03); pulse1_lengthHalt = (value & 0x20) != 0; pulse1_constantVolume = (value & 0x10) != 0; pulse1_volumeParam = value & 0x0F; pulse1_envelopeStart = true; pulse1_enabled = true; break;
                case 0x4001:
                    pulse1_sweep = value; pulse1_sweepPeriod = ((value >> 4) & 0x07) + 1; pulse1_sweepNegate = (value & 0x08) != 0; pulse1_sweepShift = value & 0x07; pulse1_sweepReload = true; break;
                case 0x4002: pulse1_timer = (ushort)((pulse1_timer & 0xFF00) | value); break;
                case 0x4003:
                    pulse1_timer = (ushort)((pulse1_timer & 0x00FF) | ((value & 0x07) << 8)); pulse1_length = (byte)((value >> 3) & 0x1F); if (pulse1_length < LengthTable.Length) pulse1_lengthCounter = LengthTable[pulse1_length]; pulse1_sequenceIndex = 0; pulse1_envelopeStart = true; pulse1_timerCounter = pulse1_timer + 1; break;
                case 0x4004:
                    pulse2_duty = (byte)((value >> 6) & 0x03); pulse2_lengthHalt = (value & 0x20) != 0; pulse2_constantVolume = (value & 0x10) != 0; pulse2_volumeParam = value & 0x0F; pulse2_envelopeStart = true; pulse2_enabled = true; break;
                case 0x4005:
                    pulse2_sweep = value; pulse2_sweepPeriod = ((value >> 4) & 0x07) + 1; pulse2_sweepNegate = (value & 0x08) != 0; pulse2_sweepShift = value & 0x07; pulse2_sweepReload = true; break;
                case 0x4006: pulse2_timer = (ushort)((pulse2_timer & 0xFF00) | value); break;
                case 0x4007:
                    pulse2_timer = (ushort)((pulse2_timer & 0x00FF) | ((value & 0x07) << 8)); pulse2_length = (byte)((value >> 3) & 0x1F); if (pulse2_length < LengthTable.Length) pulse2_lengthCounter = LengthTable[pulse2_length]; pulse2_sequenceIndex = 0; pulse2_envelopeStart = true; pulse2_timerCounter = pulse2_timer + 1; break;
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
                case 0x4015:
                    status = value; if ((value & 0x01) == 0) pulse1_enabled = false; if ((value & 0x02) == 0) pulse2_enabled = false; if ((value & 0x04) == 0) triangle_enabled = false; if ((value & 0x08) == 0) noise_enabled = false; break;
                case 0x4017:
                    frameSequencerMode5 = (value & 0x80) != 0; frameIRQInhibit = (value & 0x40) != 0; break;
            }
        }

        public byte ReadAPURegister(ushort address) => address == 0x4015 ? status : (byte)0;

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
        { cyclesToNextFrameEvent--; if (cyclesToNextFrameEvent > 0) return; switch (frameSequenceStep) { case 0: QuarterFrameTick(); cyclesToNextFrameEvent = 3728; break; case 1: QuarterFrameTick(); HalfFrameTick(); cyclesToNextFrameEvent = 3729; break; case 2: QuarterFrameTick(); cyclesToNextFrameEvent = 2730; break; case 3: QuarterFrameTick(); HalfFrameTick(); cyclesToNextFrameEvent = 3729; break; } frameSequenceStep = (frameSequenceStep + 1) & 0x03; }
        private void ClockPulse(ref ushort timer, ref int counter, ref int seqIndex)
        { if (timer < 8) return; if (counter > 0) counter--; if (counter <= 0) { counter += (timer + 1) * 2; seqIndex = (seqIndex + 1) & 7; pulse1_output = ComputePulseOutput(1); pulse2_output = ComputePulseOutput(2); } }
        private int ComputePulseOutput(int which)
        { if (which == 1) { if (!pulse1_enabled || pulse1_lengthCounter == 0 || pulse1_timer < 8) return 0; var pattern = PulseDutyTable[pulse1_duty & 3]; int bit = pattern[pulse1_sequenceIndex]; int vol = pulse1_constantVolume ? pulse1_volumeParam : pulse1_decayLevel; return bit == 1 ? vol : 0; } else { if (!pulse2_enabled || pulse2_lengthCounter == 0 || pulse2_timer < 8) return 0; var pattern = PulseDutyTable[pulse2_duty & 3]; int bit = pattern[pulse2_sequenceIndex]; int vol = pulse2_constantVolume ? pulse2_volumeParam : pulse2_decayLevel; return bit == 1 ? vol : 0; } }
        private void ClockTriangle()
        { if (!triangle_enabled || triangle_lengthCounter == 0 || triangle_linearCounter == 0 || triangle_timer < 2) return; if (triangle_timerCounter > 0) triangle_timerCounter--; if (triangle_timerCounter <= 0) { triangle_timerCounter += (triangle_timer + 1); triangle_sequenceIndex = (triangle_sequenceIndex + 1) & 31; triangle_output = TriangleSequenceValue(triangle_sequenceIndex); } }
        private int TriangleSequenceValue(int index) => index < 16 ? 15 - index : index - 16;
        private void ClockNoise()
        { if (!noise_enabled || noise_lengthCounter == 0) return; if (noise_timerCounter > 0) noise_timerCounter--; if (noise_timerCounter <= 0) { int periodIndex = noise_period & 0x0F; noise_timerCounter += NoisePeriods[periodIndex]; int bit0 = noiseShiftRegister & 0x01; int bit1 = ((noise_period & 0x80) != 0) ? ((noiseShiftRegister >> 6) & 0x01) : ((noiseShiftRegister >> 1) & 0x01); int feedback = bit0 ^ bit1; noiseShiftRegister = (ushort)((noiseShiftRegister >> 1) | (feedback << 14)); int vol = noise_constantVolume ? noise_volumeParam : noise_decayLevel; noise_output = ((noiseShiftRegister & 0x01) == 0) ? vol : 0; } }
    }
}
