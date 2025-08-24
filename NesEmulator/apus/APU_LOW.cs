using System;

namespace NesEmulator
{
    public class APU_LOW : IAPU
    {
        // Core metadata
        public string CoreName => "Low Power";
        public string Description => "Based on the Famiclone (FMC) core, this variant optimizes performance and power consumption.";
        public int Performance => 10;
        public int Rating => 4;
        public string Category => "Improved";
        private readonly Bus bus;
        public APU_LOW(Bus bus) { this.bus = bus; }

        // Precompute nonlinear audio mixing lookup tables to remove per-sample divides
        // and keep the path float-only. Pulse channels (p1+p2) sum 0..30. Triangle (0..15),
        // Noise (0..15), DMC (0..127) -> 16*16*128 = 32768 combinations. This LUT replicates
        // the canonical NES mixing approximation exactly. Memory cost ~128KB (32768 * 4 bytes).
        // If later a smaller footprint is desired for WASM, we can optionally collapse to a
        // single (t+n+d) sum table (approximation) or generate on-demand. For now we prioritize
        // accuracy + removing divides per sample.
        private static readonly float[] PulseMixLut = new float[31];
        private static readonly float[] TndMixLut = new float[16 * 16 * 128];
        static APU_LOW()
        {
            // Pulse LUT
            for (int sum = 0; sum < PulseMixLut.Length; sum++)
            {
                PulseMixLut[sum] = sum == 0 ? 0f : (95.88f / (8128f / sum + 100f));
            }
            // TND LUT (triangle, noise, dmc)
            int idx = 0;
            for (int t = 0; t < 16; t++)
            {
                float tf = t / 8227f;
                for (int n = 0; n < 16; n++)
                {
                    float nf = n / 12241f;
                    for (int d = 0; d < 128; d++, idx++)
                    {
                        if (t == 0 && n == 0 && d == 0)
                        {
                            TndMixLut[idx] = 0f;
                        }
                        else
                        {
                            float df = d / 22638f;
                            float inv = (1.0f / (tf + nf + df)) + 100f;
                            TndMixLut[idx] = 159.79f / inv;
                        }
                    }
                }
            }
        }

        // ===== Channel core registers & state =====
        // Pulse 1
        private byte pulse1_duty, pulse1_lengthIdx, pulse1_sweepRaw; private ushort pulse1_timer; // registers
        // Pulse 2
        private byte pulse2_duty, pulse2_lengthIdx, pulse2_sweepRaw; private ushort pulse2_timer;
        // Triangle
        private byte triangle_linearReg, triangle_lengthIdx; private ushort triangle_timer;
        // Noise
        private byte noise_lengthIdx, noise_periodReg;
        // DMC ($4010-$4013)
        private byte dmc_ctrl, dmc_directLoad, dmc_addrReg, dmc_lenReg;

        // Status ($4015 write latch + runtime flags)
        private byte statusWriteLatch; // last written to $4015 (enable flags)

        // Enable flags derived from $4015 (bit cleared -> clear length counter)
        private bool pulse1_enabled, pulse2_enabled, triangle_enabled, noise_enabled, dmc_enabled;

        // Length counters
        private int pulse1_lengthCounter, pulse2_lengthCounter, triangle_lengthCounter, noise_lengthCounter;

        // Envelope (pulse + noise)
        private bool pulse1_envStart, pulse2_envStart, noise_envStart;
        private int pulse1_envDivider, pulse1_envDecay, pulse2_envDivider, pulse2_envDecay, noise_envDivider, noise_envDecay;
        private bool pulse1_constantVolume, pulse2_constantVolume, noise_constantVolume;
        private bool pulse1_lengthHalt, pulse2_lengthHalt, noise_lengthHalt, triangle_lengthHalt; // length counter halt (envelope loop / control flag)
        private int pulse1_volumeParam, pulse2_volumeParam, noise_volumeParam; // 0..15

        // Sweep units
        private bool pulse1_sweepNegate, pulse2_sweepNegate; private int pulse1_sweepShift, pulse2_sweepShift; private int pulse1_sweepPeriod, pulse2_sweepPeriod; private bool pulse1_sweepReload, pulse2_sweepReload; private int pulse1_sweepDivider, pulse2_sweepDivider;

        // Triangle linear counter
        private int triangle_linearCounter; private bool triangle_linearReloadFlag;

        // Noise LFSR & timer
        private ushort noiseShiftRegister = 1; private int noise_timerCounter;
        // Pulse timers & sequencer indices
        private int pulse1_timerCounter, pulse2_timerCounter, pulse1_seqIndex, pulse2_seqIndex;
        // Triangle timer & seq
        private int triangle_timerCounter, triangle_seqIndex;
        // Outputs (raw amplitude 0..15 or DMC 0..127)
        private int pulse1_output, pulse2_output, triangle_output, noise_output, dmc_output;

        // DMC runtime
        private int dmc_timer; private int dmc_timerPeriod; private int dmc_sampleAddress; private int dmc_sampleLengthRemaining; private bool dmc_irqEnable, dmc_loop; private int dmc_shiftReg; private int dmc_bitsRemaining; private int dmc_deltaCounter = 64; private bool dmc_silence; private int dmc_sampleBuffer; private bool dmc_sampleBufferFilled; private bool dmc_irqFlag;

        // Frame sequencer
        private int frameCycle; // counts CPU cycles since last frame sequence reset
        private bool frameMode5; private bool frameIRQInhibit; private bool frameIRQFlag; // flag set when frame IRQ pending
        private int frameStep; // current step in sequence (0..3 or 0..4)
        private int nextFrameEventCycle = 7457; // next CPU cycle (relative to frameCycle) at which a quarter/half frame event occurs

        // Audio mixing / buffering
        private const int audioSampleRate = 44100; private const int AudioRingSize = 32768; private readonly float[] audioRing = new float[AudioRingSize]; private int ringWrite, ringRead, ringCount; private double fractionalSampleAccumulator;

        // Filters state
        private float lpLast, dcLastIn, dcLastOut; private const float LowPassCoeff = 0.15f; private const float DC_HPF_R = 0.995f;

        // Lookup tables
        private static readonly int[] LengthTable = { 10,254,20,2,40,4,80,6,160,8,60,10,14,12,26,14,12,16,24,18,48,20,96,22,192,24,72,26,16,28,32,30 };
        private static readonly int[] NoisePeriods = { 4,8,16,32,64,96,128,160,202,254,380,508,762,1016,2034,4068 };
        private static readonly int[] DMCRatesNTSC = { 428,380,340,320,286,254,226,214,190,160,142,128,106,84,72,54 }; // CPU cycles per bit fetch
        private static readonly byte[][] PulseDutyTable = {
            new byte[]{0,1,0,0,0,0,0,0}, new byte[]{0,1,1,0,0,0,0,0}, new byte[]{0,1,1,1,1,0,0,0}, new byte[]{1,0,0,1,1,1,1,1}
        };

        // ===== Public API =====
        public void Step() => Step(1);
        public void Step(int cpuCycles)
        {
            if (cpuCycles <= 0) return;
            const double CpuFreq = 1789773.0; // NTSC CPU frequency
            double sampleIncrement = audioSampleRate / CpuFreq; // samples per CPU cycle (~0.02466)

            int remaining = cpuCycles;
            // Process any immediately due frame sequencer events (rare) before starting
            ClockFrameSequencer();

            while (remaining > 0)
            {
                // Emit any pending samples first (can happen if accumulator carried over >1)
                if (fractionalSampleAccumulator >= 1.0)
                {
                    int emitPre = (int)fractionalSampleAccumulator;
                    fractionalSampleAccumulator -= emitPre;
                    for (int s = 0; s < emitPre; s++) MixAndStore();
                }

                // Determine cycles until next sample boundary
                int cyclesToSample;
                if (fractionalSampleAccumulator <= 0)
                {
                    // Need enough cycles so that (frac + delta*inc) >= 1 => delta >= (1-frac)/inc
                    double needed = (1.0 - fractionalSampleAccumulator) / sampleIncrement;
                    cyclesToSample = (int)Math.Ceiling(needed);
                    if (cyclesToSample <= 0) cyclesToSample = 1;
                }
                else
                {
                    double needed = (1.0 - fractionalSampleAccumulator) / sampleIncrement;
                    cyclesToSample = needed <= 0 ? 1 : (int)Math.Ceiling(needed);
                }

                // Cycles until next frame sequencer event
                int cyclesToFrameEvent = nextFrameEventCycle - frameCycle;
                if (cyclesToFrameEvent <= 0) cyclesToFrameEvent = 0; // will trigger immediately after delta advance (or immediate if all others large)

                // Gather per-channel timer counters (already count down to 0)
                int p1 = pulse1_timerCounter > 0 ? pulse1_timerCounter : 0;
                int p2 = pulse2_timerCounter > 0 ? pulse2_timerCounter : 0;
                int tri = triangle_timerCounter > 0 ? triangle_timerCounter : 0;
                int noi = noise_timerCounter > 0 ? noise_timerCounter : 0;
                int dmc = dmc_enabled && dmc_timer > 0 ? dmc_timer : 0;

                int delta = remaining; // start with remaining, tighten below
                if (p1 > 0 && p1 < delta) delta = p1;
                if (p2 > 0 && p2 < delta) delta = p2;
                if (tri > 0 && tri < delta) delta = tri;
                if (noi > 0 && noi < delta) delta = noi;
                if (dmc > 0 && dmc < delta) delta = dmc;
                if (cyclesToFrameEvent > 0 && cyclesToFrameEvent < delta) delta = cyclesToFrameEvent;
                if (cyclesToSample > 0 && cyclesToSample < delta) delta = cyclesToSample;

                if (delta <= 0)
                {
                    // An immediate event (some counter already zero). Force delta = 0 path.
                    delta = 0;
                }

                if (delta > 0)
                {
                    // Fast-forward all counters by delta
                    if (pulse1_timerCounter > 0) pulse1_timerCounter -= delta;
                    if (pulse2_timerCounter > 0) pulse2_timerCounter -= delta;
                    if (triangle_timerCounter > 0) triangle_timerCounter -= delta;
                    if (noise_timerCounter > 0) noise_timerCounter -= delta;
                    if (dmc_timer > 0) dmc_timer -= delta;
                    frameCycle += delta;
                    fractionalSampleAccumulator += sampleIncrement * delta;
                    remaining -= delta;
                }

                // Process frame sequencer events (may chain). Keep semantics: events occur when frameCycle >= nextFrameEventCycle
                ClockFrameSequencer();

                // Channel timer events (those that reached <=0)
                if (pulse1_timerCounter <= 0)
                {
                    // Each underflow advances sequence and reloads until counter >0
                    int period = (pulse1_timer + 1) * 2;
                    do { pulse1_timerCounter += period; pulse1_seqIndex = (pulse1_seqIndex + 1) & 7; } while (pulse1_timerCounter <= 0);
                }
                if (pulse2_timerCounter <= 0)
                {
                    int period = (pulse2_timer + 1) * 2;
                    do { pulse2_timerCounter += period; pulse2_seqIndex = (pulse2_seqIndex + 1) & 7; } while (pulse2_timerCounter <= 0);
                }
                if (triangle_timerCounter <= 0)
                {
                    int period = triangle_timer + 1;
                    do { triangle_timerCounter += period; triangle_seqIndex = (triangle_seqIndex + 1) & 31; } while (triangle_timerCounter <= 0);
                }
                if (noise_timerCounter <= 0)
                {
                    do
                    {
                        int period = NoisePeriods[noise_periodReg & 0x0F];
                        noise_timerCounter += period;
                        // Tick LFSR once per timer underflow
                        int bit0 = noiseShiftRegister & 1;
                        int tap = ((noise_periodReg & 0x80) != 0) ? ((noiseShiftRegister >> 6) & 1) : ((noiseShiftRegister >> 1) & 1);
                        int fb = bit0 ^ tap;
                        noiseShiftRegister = (ushort)((noiseShiftRegister >> 1) | (fb << 14));
                    } while (noise_timerCounter <= 0);
                }
                if (dmc_enabled && dmc_timer <= 0)
                {
                    do
                    {
                        dmc_timer += dmc_timerPeriod;
                        if (!dmc_silence)
                        {
                            if ((dmc_shiftReg & 1) != 0) { if (dmc_deltaCounter <= 125) dmc_deltaCounter += 2; }
                            else { if (dmc_deltaCounter >= 2) dmc_deltaCounter -= 2; }
                        }
                        dmc_shiftReg >>= 1;
                        dmc_bitsRemaining--;
                        if (dmc_bitsRemaining == 0)
                        {
                            if (dmc_sampleBufferFilled)
                            {
                                dmc_silence = false; dmc_shiftReg = dmc_sampleBuffer; dmc_bitsRemaining = 8; dmc_sampleBufferFilled = false;
                            }
                            else
                            {
                                dmc_silence = true; dmc_bitsRemaining = 8;
                            }
                            TryDmcFetch();
                        }
                        // Fetch attempt each bit event
                        TryDmcFetch();
                    } while (dmc_timer <= 0);
                    dmc_output = dmc_deltaCounter;
                }
                else if (dmc_enabled)
                {
                    // Periodic prefetch (less frequent than per-cycle fetch). Attempt once per outer loop.
                    TryDmcFetch();
                    dmc_output = dmc_deltaCounter;
                }

                // Recompute channel outputs (similar conditions as per-cycle loop)
                bool p1SweepMute = SweepWouldMute(pulse1_timer, pulse1_sweepNegate, pulse1_sweepShift, true);
                bool p2SweepMute = SweepWouldMute(pulse2_timer, pulse2_sweepNegate, pulse2_sweepShift, false);
                UpdatePulseOutput(true, p1SweepMute);
                UpdatePulseOutput(false, p2SweepMute);
                UpdateTriangleOutput();
                UpdateNoiseOutput();
            }

            // Emit trailing samples if accumulator exceeded 1.0 during final delta
            if (fractionalSampleAccumulator >= 1.0)
            {
                int emit = (int)fractionalSampleAccumulator;
                fractionalSampleAccumulator -= emit;
                for (int s = 0; s < emit; s++) MixAndStore();
            }
        }

        private void UpdatePulseOutput(bool first, bool sweepMute)
        {
            if (first)
            {
                if (!pulse1_enabled || pulse1_lengthCounter == 0 || pulse1_timer < 8 || sweepMute) { pulse1_output = 0; return; }
                var pattern = PulseDutyTable[pulse1_duty & 3];
                int bit = pattern[pulse1_seqIndex];
                int vol = pulse1_constantVolume ? pulse1_volumeParam : pulse1_envDecay;
                pulse1_output = bit == 1 ? vol : 0;
            }
            else
            {
                if (!pulse2_enabled || pulse2_lengthCounter == 0 || pulse2_timer < 8 || sweepMute) { pulse2_output = 0; return; }
                var pattern = PulseDutyTable[pulse2_duty & 3];
                int bit = pattern[pulse2_seqIndex];
                int vol = pulse2_constantVolume ? pulse2_volumeParam : pulse2_envDecay;
                pulse2_output = bit == 1 ? vol : 0;
            }
        }
        private void UpdateTriangleOutput()
        {
            if (!triangle_enabled || triangle_lengthCounter == 0 || triangle_linearCounter == 0 || triangle_timer < 2) { triangle_output = 0; return; }
            triangle_output = (triangle_seqIndex < 16) ? (15 - triangle_seqIndex) : (triangle_seqIndex - 16);
        }
        private void UpdateNoiseOutput()
        {
            if (!noise_enabled || noise_lengthCounter == 0) { noise_output = 0; return; }
            int vol = noise_constantVolume ? noise_volumeParam : noise_envDecay;
            noise_output = ((noiseShiftRegister & 1) == 0) ? vol : 0;
        }

        // ===== Writes =====
        public void WriteAPURegister(ushort address, byte value)
        {
            switch(address)
            {
                case 0x4000: // Pulse1 envelope/duty
                    pulse1_duty = (byte)((value>>6)&3); pulse1_lengthHalt = (value & 0x20)!=0; pulse1_constantVolume = (value & 0x10)!=0; pulse1_volumeParam = value & 0x0F; pulse1_envStart = true; break;
                case 0x4001: // Pulse1 sweep
                    pulse1_sweepRaw = value; pulse1_sweepPeriod = ((value>>4)&7)+1; pulse1_sweepNegate = (value & 0x08)!=0; pulse1_sweepShift = value & 0x07; pulse1_sweepReload = true; break;
                case 0x4002: pulse1_timer = (ushort)((pulse1_timer & 0x0700) | value); break;
                case 0x4003: pulse1_timer = (ushort)((pulse1_timer & 0x00FF) | ((value & 0x07)<<8)); pulse1_lengthIdx = (byte)((value>>3)&0x1F); if(pulse1_enabled) LoadLength(ref pulse1_lengthCounter, pulse1_lengthIdx); pulse1_seqIndex=0; pulse1_envStart=true; pulse1_timerCounter = (pulse1_timer+1)*2; break;
                case 0x4004: pulse2_duty = (byte)((value>>6)&3); pulse2_lengthHalt = (value & 0x20)!=0; pulse2_constantVolume = (value & 0x10)!=0; pulse2_volumeParam = value & 0x0F; pulse2_envStart = true; break;
                case 0x4005: pulse2_sweepRaw = value; pulse2_sweepPeriod = ((value>>4)&7)+1; pulse2_sweepNegate = (value & 0x08)!=0; pulse2_sweepShift = value & 0x07; pulse2_sweepReload = true; break;
                case 0x4006: pulse2_timer = (ushort)((pulse2_timer & 0x0700) | value); break;
                case 0x4007: pulse2_timer = (ushort)((pulse2_timer & 0x00FF) | ((value & 0x07)<<8)); pulse2_lengthIdx = (byte)((value>>3)&0x1F); if(pulse2_enabled) LoadLength(ref pulse2_lengthCounter, pulse2_lengthIdx); pulse2_seqIndex=0; pulse2_envStart=true; pulse2_timerCounter = (pulse2_timer+1)*2; break;
                case 0x4008: triangle_linearReg = value; triangle_lengthHalt = (value & 0x80)!=0; triangle_linearReloadFlag = true; break;
                case 0x400A: triangle_timer = (ushort)((triangle_timer & 0x0700) | value); break;
                case 0x400B: triangle_timer = (ushort)((triangle_timer & 0x00FF) | ((value & 0x07)<<8)); triangle_lengthIdx = (byte)((value>>3)&0x1F); if(triangle_enabled) LoadLength(ref triangle_lengthCounter, triangle_lengthIdx); triangle_seqIndex=0; triangle_linearReloadFlag=true; triangle_timerCounter = triangle_timer+1; break;
                case 0x400C: noise_lengthHalt = (value & 0x20)!=0; noise_constantVolume = (value & 0x10)!=0; noise_volumeParam = value & 0x0F; noise_envStart = true; break;
                case 0x400E: noise_periodReg = value; break;
                case 0x400F: noise_lengthIdx = (byte)((value>>3)&0x1F); if(noise_enabled) LoadLength(ref noise_lengthCounter, noise_lengthIdx); noise_envStart=true; break;
                case 0x4010: dmc_ctrl = value; dmc_irqEnable = (value & 0x80)!=0; if(!dmc_irqEnable) dmc_irqFlag=false; dmc_loop = (value & 0x40)!=0; dmc_timerPeriod = DMCRatesNTSC[value & 0x0F]; break;
                case 0x4011: dmc_directLoad = value; dmc_deltaCounter = value & 0x7F; break;
                case 0x4012: dmc_addrReg = value; break; // base address $C000 + value*64
                case 0x4013: dmc_lenReg = value; break; // length = value*16+1
                case 0x4015:
                    statusWriteLatch = value;
                    // enable bits
                    pulse1_enabled = (value & 0x01)!=0; if(!pulse1_enabled) pulse1_lengthCounter=0;
                    pulse2_enabled = (value & 0x02)!=0; if(!pulse2_enabled) pulse2_lengthCounter=0;
                    triangle_enabled = (value & 0x04)!=0; if(!triangle_enabled) triangle_lengthCounter=0;
                    noise_enabled = (value & 0x08)!=0; if(!noise_enabled) noise_lengthCounter=0;
                    bool enableDMC = (value & 0x10)!=0;
                    if(enableDMC && !dmc_enabled) StartDMC();
                    dmc_enabled = enableDMC; if(!dmc_enabled) { dmc_sampleLengthRemaining=0; }
                    dmc_irqFlag = false; frameIRQFlag = false; break;
                case 0x4017: // frame counter
                    frameMode5 = (value & 0x80)!=0; frameIRQInhibit = (value & 0x40)!=0; if(frameIRQInhibit) frameIRQFlag=false; 
                    // Reset sequencer timing per hardware approximation: counter cleared immediately
                    frameCycle = 0; frameStep = 0; nextFrameEventCycle = 7457; // first event
                    if(frameMode5){
                        // In 5-step mode, writing with bit7=1 immediately clocks a quarter and half frame (no IRQ ever)
                        QuarterFrameTick(); HalfFrameTick();
                    }
                    // Writing 4017 clears frame IRQ flag
                    frameIRQFlag = false;
                    break;
            }
        }

        // ===== Reads =====
        public byte ReadAPURegister(ushort address)
        {
            if(address==0x4015)
            {
                byte result = 0;
                if(pulse1_lengthCounter>0) result |= 0x01; if(pulse2_lengthCounter>0) result |= 0x02; if(triangle_lengthCounter>0) result |= 0x04; if(noise_lengthCounter>0) result |= 0x08; if(dmc_sampleLengthRemaining>0) result |= 0x10; if(frameIRQFlag) result |= 0x40; if(dmc_irqFlag) result |= 0x80; frameIRQFlag = false; return result;
            }
            return 0;
        }

        // ===== Internal helpers =====
        private void LoadLength(ref int counter, byte idx){ if(idx < LengthTable.Length) counter = LengthTable[idx]; }

        private void ClockPulse(ref ushort timer, ref int timerCounter, ref int seqIndex, ref int output, bool enabled, int lengthCounter, bool timerMute, bool sweepMute, byte duty, bool constantVol, int volumeParam, int envDecay, bool isFirst)
        {
            if(!enabled || lengthCounter==0 || timerMute || sweepMute){ output = 0; return; }
            if(--timerCounter <= 0){ timerCounter = (timer+1)*2; seqIndex = (seqIndex+1)&7; }
            var pattern = PulseDutyTable[duty & 3]; int bit = pattern[seqIndex]; int vol = constantVol ? volumeParam : envDecay; output = bit==1 ? vol : 0;
        }

        private void ClockTriangle()
        {
            if(!triangle_enabled || triangle_lengthCounter==0 || triangle_linearCounter==0 || triangle_timer < 2) { triangle_output=0; return; }
            if(--triangle_timerCounter <= 0){ triangle_timerCounter = triangle_timer + 1; triangle_seqIndex = (triangle_seqIndex+1) & 31; }
            triangle_output = (triangle_seqIndex < 16) ? (15 - triangle_seqIndex) : (triangle_seqIndex - 16);
        }

        private void ClockNoise()
        {
            if(!noise_enabled || noise_lengthCounter==0){ noise_output=0; return; }
            if(--noise_timerCounter <= 0){
                int period = NoisePeriods[noise_periodReg & 0x0F]; noise_timerCounter = period;
                int bit0 = noiseShiftRegister & 1; int tap = ((noise_periodReg & 0x80)!=0) ? ((noiseShiftRegister >> 6)&1) : ((noiseShiftRegister >> 1)&1); int fb = bit0 ^ tap; noiseShiftRegister = (ushort)((noiseShiftRegister >> 1) | (fb<<14));
            }
            int vol = noise_constantVolume ? noise_volumeParam : noise_envDecay; noise_output = ((noiseShiftRegister & 1)==0) ? vol : 0;
        }

        private void ClockDMC()
        {
            if(!dmc_enabled){ dmc_output = dmc_deltaCounter; return; }
            if(--dmc_timer <= 0){
                dmc_timer = dmc_timerPeriod;
                if(!dmc_silence){
                    if((dmc_shiftReg & 1)!=0){ if(dmc_deltaCounter <= 125) dmc_deltaCounter +=2; }
                    else { if(dmc_deltaCounter >= 2) dmc_deltaCounter -=2; }
                }
                dmc_shiftReg >>=1;
                dmc_bitsRemaining--;
                if(dmc_bitsRemaining==0){
                    if(dmc_sampleBufferFilled){
                        dmc_silence=false; dmc_shiftReg = dmc_sampleBuffer; dmc_bitsRemaining = 8; dmc_sampleBufferFilled=false;
                    } else {
                        dmc_silence=true; dmc_bitsRemaining=8;
                    }
                    TryDmcFetch();
                }
            }
            // Preload next sample if needed (once per CPU cycle)
            TryDmcFetch();
            dmc_output = dmc_deltaCounter;
        }

        private void TryDmcFetch()
        {
            if(dmc_sampleBufferFilled) return; if(dmc_sampleLengthRemaining==0) return; // nothing to fetch
            byte sample = bus.Read((ushort)dmc_sampleAddress);
            dmc_sampleAddress++; if(dmc_sampleAddress > 0xFFFF) dmc_sampleAddress = 0x8000; // wrap
            dmc_sampleLengthRemaining--; dmc_sampleBuffer = sample; dmc_sampleBufferFilled = true;
            if(dmc_sampleLengthRemaining==0){ if(dmc_loop){ RestartDMC(); } else if(dmc_irqEnable){ dmc_irqFlag = true; bus.cpu.RequestIRQ(true); } }
        }

        private void StartDMC(){ RestartDMC(); dmc_timer = 1; dmc_bitsRemaining = 8; dmc_silence = !dmc_sampleBufferFilled; }
        private void RestartDMC(){ dmc_sampleAddress = 0xC000 + (dmc_addrReg << 6); dmc_sampleLengthRemaining = (dmc_lenReg << 4) + 1; }

        private void QuarterFrameTick()
        {
            // Envelopes
            ClockEnvelope(ref pulse1_envStart, ref pulse1_envDivider, ref pulse1_envDecay, pulse1_volumeParam, pulse1_lengthHalt);
            ClockEnvelope(ref pulse2_envStart, ref pulse2_envDivider, ref pulse2_envDecay, pulse2_volumeParam, pulse2_lengthHalt);
            ClockEnvelope(ref noise_envStart, ref noise_envDivider, ref noise_envDecay, noise_volumeParam, noise_lengthHalt);
            // Triangle linear counter
            if(triangle_linearReloadFlag){ triangle_linearCounter = triangle_linearReg & 0x7F; }
            else if(triangle_linearCounter>0) triangle_linearCounter--;
            if((triangle_linearReg & 0x80)==0) triangle_linearReloadFlag = false;
        }
        private void HalfFrameTick()
        {
            // Length counters
            if(!pulse1_lengthHalt && pulse1_lengthCounter>0) pulse1_lengthCounter--;
            if(!pulse2_lengthHalt && pulse2_lengthCounter>0) pulse2_lengthCounter--;
            if(!triangle_lengthHalt && triangle_lengthCounter>0) triangle_lengthCounter--;
            if(!noise_lengthHalt && noise_lengthCounter>0) noise_lengthCounter--;
            // Sweeps
            Sweep(ref pulse1_timer, pulse1_sweepNegate, pulse1_sweepShift, pulse1_sweepPeriod, ref pulse1_sweepDivider, ref pulse1_sweepReload, true);
            Sweep(ref pulse2_timer, pulse2_sweepNegate, pulse2_sweepShift, pulse2_sweepPeriod, ref pulse2_sweepDivider, ref pulse2_sweepReload, false);
        }
        private void Sweep(ref ushort timer, bool negate, int shift, int period, ref int divider, ref bool reload, bool channel1)
        {
            if(shift==0){ if(reload){ divider=period; reload=false; } return; }
            if(reload){ divider = period; reload=false; }
            else if(--divider <=0){ divider = period; int change = timer >> shift; int target = negate ? (timer - change - (channel1?1:0)) : (timer + change); if(target >=8 && target <= 0x7FF) timer = (ushort)target; }
        }
        private void ClockEnvelope(ref bool start, ref int divider, ref int decay, int volumeParam, bool loop){ if(start){ start=false; decay=15; divider=volumeParam+1; } else { if(--divider <=0){ divider = volumeParam+1; if(decay>0) decay--; else if(loop) decay=15; } } }

        // Frame sequencer (NTSC): 4-step: 0:Q, 1:Q+H,2:Q,3:Q+H+IRQ; 5-step: 0:Q+H,1:Q,2:Q+H,3:Q,4:--- (no IRQ)
        private void ClockFrameSequencer()
        {
            while(frameCycle >= nextFrameEventCycle)
            {
                if(!frameMode5)
                {
                    switch(frameStep)
                    {
                        case 0: QuarterFrameTick(); nextFrameEventCycle = 14913; break;
                        case 1: QuarterFrameTick(); HalfFrameTick(); nextFrameEventCycle = 22371; break;
                        case 2: QuarterFrameTick(); nextFrameEventCycle = 29829; break;
                        case 3:
                            QuarterFrameTick(); HalfFrameTick();
                            if(!frameIRQInhibit){ frameIRQFlag = true; bus.cpu.RequestIRQ(true); }
                            frameStep = -1; 
                            frameCycle -= 29830; 
                            nextFrameEventCycle = 7457; 
                            break;
                    }
                }
                else
                {
                    switch(frameStep)
                    {
                        case 0: QuarterFrameTick(); HalfFrameTick(); nextFrameEventCycle = 14913; break;
                        case 1: QuarterFrameTick(); nextFrameEventCycle = 22371; break;
                        case 2: QuarterFrameTick(); HalfFrameTick(); nextFrameEventCycle = 29829; break;
                        case 3: QuarterFrameTick(); nextFrameEventCycle = 37281; break;
                        case 4:
                            QuarterFrameTick();
                            frameStep = -1; 
                            frameCycle -= 37282; 
                            nextFrameEventCycle = 7457;
                            break;
                    }
                }
                frameStep++;
            }
        }

        private void MixAndStore()
        {
            // LUT-based nonlinear mixing (float-only)
            int p1 = pulse1_output;
            int p2 = pulse2_output;
            int pulseSum = p1 + p2; // 0..30
            float pulseMix = PulseMixLut[pulseSum];

            int t = triangle_output; // 0..15
            int n = noise_output;    // 0..15
            int d = dmc_enabled ? dmc_output : 0; // 0..127
            // Index layout: (t << (4+7)) | (n << 7) | d
            int tndIndex = (t << 11) | (n << 7) | d;
            float tnd = TndMixLut[tndIndex];

            float mixed = pulseMix + tnd;
            // Existing simple low-pass + DC high-pass chain kept intact
            lpLast += (mixed - lpLast) * LowPassCoeff; float lp = lpLast; float hp = lp - dcLastIn + DC_HPF_R * dcLastOut; dcLastIn = lp; dcLastOut = hp;
            StoreSample(hp * 1.05f);
        }

        private bool SweepWouldMute(ushort timer, bool negate, int shift, bool channel1)
        {
            if(shift == 0) return false;
            int change = timer >> shift;
            int target = negate ? (timer - change - (channel1?1:0)) : (timer + change);
            if(target > 0x7FF) return true;
            if(target < 8) return true;
            return false;
        }
        private void StoreSample(float sample){ if(ringCount >= AudioRingSize){ ringRead = (ringRead+1) & (AudioRingSize-1); ringCount--; } audioRing[ringWrite]=sample; ringWrite=(ringWrite+1)&(AudioRingSize-1); ringCount++; }

        public float[] GetAudioSamples(int maxSamples=0){ if(ringCount==0) return Array.Empty<float>(); int toRead = ringCount; if(maxSamples>0 && maxSamples<toRead) toRead=maxSamples; if(toRead>4096 && maxSamples==0) toRead=4096; float[] result=new float[toRead]; int first = Math.Min(toRead, AudioRingSize - ringRead); Array.Copy(audioRing, ringRead, result,0, first); int rem=toRead-first; if(rem>0) Array.Copy(audioRing,0,result,first,rem); ringRead=(ringRead+toRead)&(AudioRingSize-1); ringCount-=toRead; return result; }
        public float[] GetAudioBuffer()=>GetAudioSamples(); public int GetQueuedSampleCount()=>ringCount; public int GetSampleRate()=>audioSampleRate;

        private void ResetInternal(){ ringRead=ringWrite=ringCount=0; fractionalSampleAccumulator=0; lpLast=dcLastIn=dcLastOut=0; frameCycle=0; frameIRQFlag=false; dmc_irqFlag=false; }

        private class ApuState { public byte pulse1_duty,pulse1_lengthIdx,pulse1_sweepRaw; public ushort pulse1_timer; public byte pulse2_duty,pulse2_lengthIdx,pulse2_sweepRaw; public ushort pulse2_timer; public byte triangle_linearReg,triangle_lengthIdx; public ushort triangle_timer; public byte noise_lengthIdx,noise_periodReg; public byte dmc_ctrl,dmc_directLoad,dmc_addrReg,dmc_lenReg; public byte statusWriteLatch; public bool pulse1_enabled,pulse2_enabled,triangle_enabled,noise_enabled,dmc_enabled; public int p1Len,p2Len,tLen,nLen; public bool p1EnvStart,p2EnvStart,nEnvStart; public int p1EnvDiv,p1EnvDecay,p2EnvDiv,p2EnvDecay,nEnvDiv,nEnvDecay; public bool p1Const,p2Const,nConst,p1LenH,p2LenH,nLenH,tLenH; public int p1Vol,p2Vol,nVol; public bool p1SwNeg,p2SwNeg; public int p1SwShift,p2SwShift,p1SwPeriod,p2SwPeriod; public bool p1SwReload,p2SwReload; public int p1SwDiv,p2SwDiv; public int triLinCtr; public bool triLinReload; public ushort noiseShift; public int noiseTimer; public int p1TimerCtr,p2TimerCtr,p1Seq,p2Seq; public int triTimerCtr,triSeq; public int p1Out,p2Out,triOut,noiseOut,dmcOut; public int dmc_timer,dmc_timerPeriod,dmc_sampleAddress,dmc_sampleLengthRemaining,dmc_shiftReg,dmc_bitsRemaining,dmc_deltaCounter,dmc_sampleBuffer; public bool dmc_sampleBufferFilled,dmc_silence,dmc_irqEnable,dmc_loop,dmc_irqFlag; public int frameCycle; public bool frameMode5,frameIRQInhibit,frameIRQFlag; public float lpLast,dcLastIn,dcLastOut; public int ringWrite,ringRead,ringCount; public double frac; }
        public object GetState()=> new ApuState { pulse1_duty=pulse1_duty,pulse1_lengthIdx=pulse1_lengthIdx,pulse1_sweepRaw=pulse1_sweepRaw,pulse1_timer=pulse1_timer,pulse2_duty=pulse2_duty,pulse2_lengthIdx=pulse2_lengthIdx,pulse2_sweepRaw=pulse2_sweepRaw,pulse2_timer=pulse2_timer,triangle_linearReg=triangle_linearReg,triangle_lengthIdx=triangle_lengthIdx,triangle_timer=triangle_timer,noise_lengthIdx=noise_lengthIdx,noise_periodReg=noise_periodReg,dmc_ctrl=dmc_ctrl,dmc_directLoad=dmc_directLoad,dmc_addrReg=dmc_addrReg,dmc_lenReg=dmc_lenReg,statusWriteLatch=statusWriteLatch,pulse1_enabled=pulse1_enabled,pulse2_enabled=pulse2_enabled,triangle_enabled=triangle_enabled,noise_enabled=noise_enabled,dmc_enabled=dmc_enabled,p1Len=pulse1_lengthCounter,p2Len=pulse2_lengthCounter,tLen=triangle_lengthCounter,nLen=noise_lengthCounter,p1EnvStart=pulse1_envStart,p2EnvStart=pulse2_envStart,nEnvStart=noise_envStart,p1EnvDiv=pulse1_envDivider,p1EnvDecay=pulse1_envDecay,p2EnvDiv=pulse2_envDivider,p2EnvDecay=pulse2_envDecay,nEnvDiv=noise_envDivider,nEnvDecay=noise_envDecay,p1Const=pulse1_constantVolume,p2Const=pulse2_constantVolume,nConst=noise_constantVolume,p1LenH=pulse1_lengthHalt,p2LenH=pulse2_lengthHalt,nLenH=noise_lengthHalt,tLenH=triangle_lengthHalt,p1Vol=pulse1_volumeParam,p2Vol=pulse2_volumeParam,nVol=noise_volumeParam,p1SwNeg=pulse1_sweepNegate,p2SwNeg=pulse2_sweepNegate,p1SwShift=pulse1_sweepShift,p2SwShift=pulse2_sweepShift,p1SwPeriod=pulse1_sweepPeriod,p2SwPeriod=pulse2_sweepPeriod,p1SwReload=pulse1_sweepReload,p2SwReload=pulse2_sweepReload,p1SwDiv=pulse1_sweepDivider,p2SwDiv=pulse2_sweepDivider,triLinCtr=triangle_linearCounter,triLinReload=triangle_linearReloadFlag,noiseShift=noiseShiftRegister,noiseTimer=noise_timerCounter,p1TimerCtr=pulse1_timerCounter,p2TimerCtr=pulse2_timerCounter,p1Seq=pulse1_seqIndex,p2Seq=pulse2_seqIndex,triTimerCtr=triangle_timerCounter,triSeq=triangle_seqIndex,p1Out=pulse1_output,p2Out=pulse2_output,triOut=triangle_output,noiseOut=noise_output,dmcOut=dmc_output,dmc_timer=dmc_timer,dmc_timerPeriod=dmc_timerPeriod,dmc_sampleAddress=dmc_sampleAddress,dmc_sampleLengthRemaining=dmc_sampleLengthRemaining,dmc_shiftReg=dmc_shiftReg,dmc_bitsRemaining=dmc_bitsRemaining,dmc_deltaCounter=dmc_deltaCounter,dmc_sampleBuffer=dmc_sampleBuffer,dmc_sampleBufferFilled=dmc_sampleBufferFilled,dmc_silence=dmc_silence,dmc_irqEnable=dmc_irqEnable,dmc_loop=dmc_loop,dmc_irqFlag=dmc_irqFlag,frameCycle=frameCycle,frameMode5=frameMode5,frameIRQInhibit=frameIRQInhibit,frameIRQFlag=frameIRQFlag,lpLast=lpLast,dcLastIn=dcLastIn,dcLastOut=dcLastOut,ringWrite=ringWrite,ringRead=ringRead,ringCount=ringCount,frac=fractionalSampleAccumulator };
        public void SetState(object state){
            if(state is ApuState s){ RestoreState(s); PostRestoreAudioResync(); return; }
            if(state is System.Text.Json.JsonElement je){
                try{
                    if(je.ValueKind==System.Text.Json.JsonValueKind.Object){
                        var tmp = new ApuState(); System.Text.Json.JsonElement v;
                        if(je.TryGetProperty("pulse1_duty", out v)) tmp.pulse1_duty=(byte)v.GetByte();
                        if(je.TryGetProperty("pulse1_lengthIdx", out v)) tmp.pulse1_lengthIdx=(byte)v.GetByte();
                        if(je.TryGetProperty("pulse1_sweepRaw", out v)) tmp.pulse1_sweepRaw=(byte)v.GetByte();
                        if(je.TryGetProperty("pulse1_timer", out v)) tmp.pulse1_timer=(ushort)v.GetUInt16();
                        if(je.TryGetProperty("pulse2_duty", out v)) tmp.pulse2_duty=(byte)v.GetByte();
                        if(je.TryGetProperty("pulse2_lengthIdx", out v)) tmp.pulse2_lengthIdx=(byte)v.GetByte();
                        if(je.TryGetProperty("pulse2_sweepRaw", out v)) tmp.pulse2_sweepRaw=(byte)v.GetByte();
                        if(je.TryGetProperty("pulse2_timer", out v)) tmp.pulse2_timer=(ushort)v.GetUInt16();
                        if(je.TryGetProperty("triangle_linearReg", out v)) tmp.triangle_linearReg=(byte)v.GetByte();
                        if(je.TryGetProperty("triangle_lengthIdx", out v)) tmp.triangle_lengthIdx=(byte)v.GetByte();
                        if(je.TryGetProperty("triangle_timer", out v)) tmp.triangle_timer=(ushort)v.GetUInt16();
                        if(je.TryGetProperty("noise_lengthIdx", out v)) tmp.noise_lengthIdx=(byte)v.GetByte();
                        if(je.TryGetProperty("noise_periodReg", out v)) tmp.noise_periodReg=(byte)v.GetByte();
                        if(je.TryGetProperty("dmc_ctrl", out v)) tmp.dmc_ctrl=(byte)v.GetByte();
                        if(je.TryGetProperty("dmc_directLoad", out v)) tmp.dmc_directLoad=(byte)v.GetByte();
                        if(je.TryGetProperty("dmc_addrReg", out v)) tmp.dmc_addrReg=(byte)v.GetByte();
                        if(je.TryGetProperty("dmc_lenReg", out v)) tmp.dmc_lenReg=(byte)v.GetByte();
                        if(je.TryGetProperty("statusWriteLatch", out v)) tmp.statusWriteLatch=(byte)v.GetByte();
                        if(je.TryGetProperty("noiseShift", out v)) tmp.noiseShift=(ushort)v.GetUInt16();
                        if(je.TryGetProperty("noiseTimer", out v)) tmp.noiseTimer=v.GetInt32();
                        if(je.TryGetProperty("frameCycle", out v)) tmp.frameCycle=v.GetInt32();
                        RestoreState(tmp);
                        PostRestoreAudioResync();
                    }
                }catch{}
            }
        }
        private void RestoreState(ApuState s){ pulse1_duty=s.pulse1_duty; pulse1_lengthIdx=s.pulse1_lengthIdx; pulse1_sweepRaw=s.pulse1_sweepRaw; pulse1_timer=s.pulse1_timer; pulse2_duty=s.pulse2_duty; pulse2_lengthIdx=s.pulse2_lengthIdx; pulse2_sweepRaw=s.pulse2_sweepRaw; pulse2_timer=s.pulse2_timer; triangle_linearReg=s.triangle_linearReg; triangle_lengthIdx=s.triangle_lengthIdx; triangle_timer=s.triangle_timer; noise_lengthIdx=s.noise_lengthIdx; noise_periodReg=s.noise_periodReg; dmc_ctrl=s.dmc_ctrl; dmc_directLoad=s.dmc_directLoad; dmc_addrReg=s.dmc_addrReg; dmc_lenReg=s.dmc_lenReg; statusWriteLatch=s.statusWriteLatch; pulse1_enabled=s.pulse1_enabled; pulse2_enabled=s.pulse2_enabled; triangle_enabled=s.triangle_enabled; noise_enabled=s.noise_enabled; dmc_enabled=s.dmc_enabled; pulse1_lengthCounter=s.p1Len; pulse2_lengthCounter=s.p2Len; triangle_lengthCounter=s.tLen; noise_lengthCounter=s.nLen; pulse1_envStart=s.p1EnvStart; pulse2_envStart=s.p2EnvStart; noise_envStart=s.nEnvStart; pulse1_envDivider=s.p1EnvDiv; pulse1_envDecay=s.p1EnvDecay; pulse2_envDivider=s.p2EnvDiv; pulse2_envDecay=s.p2EnvDecay; noise_envDivider=s.nEnvDiv; noise_envDecay=s.nEnvDecay; pulse1_constantVolume=s.p1Const; pulse2_constantVolume=s.p2Const; noise_constantVolume=s.nConst; pulse1_lengthHalt=s.p1LenH; pulse2_lengthHalt=s.p2LenH; noise_lengthHalt=s.nLenH; triangle_lengthHalt=s.tLenH; pulse1_volumeParam=s.p1Vol; pulse2_volumeParam=s.p2Vol; noise_volumeParam=s.nVol; pulse1_sweepNegate=s.p1SwNeg; pulse2_sweepNegate=s.p2SwNeg; pulse1_sweepShift=s.p1SwShift; pulse2_sweepShift=s.p2SwShift; pulse1_sweepPeriod=s.p1SwPeriod; pulse2_sweepPeriod=s.p2SwPeriod; pulse1_sweepReload=s.p1SwReload; pulse2_sweepReload=s.p2SwReload; pulse1_sweepDivider=s.p1SwDiv; pulse2_sweepDivider=s.p2SwDiv; triangle_linearCounter=s.triLinCtr; triangle_linearReloadFlag=s.triLinReload; noiseShiftRegister=s.noiseShift; noise_timerCounter=s.noiseTimer; pulse1_timerCounter=s.p1TimerCtr; pulse2_timerCounter=s.p2TimerCtr; pulse1_seqIndex=s.p1Seq; pulse2_seqIndex=s.p2Seq; triangle_timerCounter=s.triTimerCtr; triangle_seqIndex=s.triSeq; pulse1_output=s.p1Out; pulse2_output=s.p2Out; triangle_output=s.triOut; noise_output=s.noiseOut; dmc_output=s.dmcOut; dmc_timer=s.dmc_timer; dmc_timerPeriod=s.dmc_timerPeriod; dmc_sampleAddress=s.dmc_sampleAddress; dmc_sampleLengthRemaining=s.dmc_sampleLengthRemaining; dmc_shiftReg=s.dmc_shiftReg; dmc_bitsRemaining=s.dmc_bitsRemaining; dmc_deltaCounter=s.dmc_deltaCounter; dmc_sampleBuffer=s.dmc_sampleBuffer; dmc_sampleBufferFilled=s.dmc_sampleBufferFilled; dmc_silence=s.dmc_silence; dmc_irqEnable=s.dmc_irqEnable; dmc_loop=s.dmc_loop; dmc_irqFlag=s.dmc_irqFlag; frameCycle=s.frameCycle; frameMode5=s.frameMode5; frameIRQInhibit=s.frameIRQInhibit; frameIRQFlag=s.frameIRQFlag; lpLast=s.lpLast; dcLastIn=s.dcLastIn; dcLastOut=s.dcLastOut; ringWrite=s.ringWrite; ringRead=s.ringRead; ringCount=s.ringCount; fractionalSampleAccumulator=s.frac; }

        private void PostRestoreAudioResync()
        {
            ringRead = ringWrite = ringCount = 0;
            fractionalSampleAccumulator = 0;
            lpLast = 0; dcLastIn = 0; dcLastOut = 0;
        }

        public void ClearAudioBuffers()
        {
            PostRestoreAudioResync();
        }

        // Minimal Reset: clear audio buffers and pacing; preserve register latches/state.
        public void Reset()
        {
            ClearAudioBuffers();
        }
    }
}
