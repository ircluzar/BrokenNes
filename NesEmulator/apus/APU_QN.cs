using System;

namespace NesEmulator
{
    // QuickNES-inspired APU core.
    // Notes:
    // - Structure and behavior are informed by publicly known NES APU behavior and the QuickNES approach.
    // - Comments avoid referring to any specific original source files.
    // - Implementation aims for reasonable fidelity and stable mixing; not guaranteed cycle-accurate.
    public class APU_QN : IAPU
    {
        private readonly Bus bus;

    // --- Core state ---
        // Channel objects (to be implemented)
        private QnSquare square1, square2;
        private QnTriangle triangle;
        private QnNoise noise;
        private QnDmc dmc;

        // Channel base class
        private abstract class QnChannel { public abstract void Reset(); }

        // Square channel
        private class QnSquare : QnChannel {
            public byte[] regs = new byte[4];
            public bool[] regWritten = new bool[4];
            // Envelope
            public int envDivider, envDecay; public bool envStart, constantVolume, lengthHalt; public int volumeParam;
            // Sweep
            public bool sweepNegate, sweepReload; public int sweepShift, sweepPeriod, sweepDivider; public bool sweepMute;
            // Length counter
            public int lengthCounter;
            // Timer / duty
            public int timerPeriod, timerCounter; public int duty; public int dutyStep;
            // Output
            public int lastAmp;
            public override void Reset() {
                Array.Clear(regs,0,4); Array.Clear(regWritten,0,4);
                envDivider = envDecay = 0; envStart = constantVolume = lengthHalt = false; volumeParam = 0;
                sweepNegate = sweepReload = false; sweepShift = sweepPeriod = sweepDivider = 0; sweepMute=false;
                lengthCounter = 0; timerPeriod = timerCounter = 0; duty = dutyStep = 0; lastAmp = 0;
            }
        }

        // Triangle channel
        private class QnTriangle : QnChannel {
            public byte[] regs = new byte[4]; public bool[] regWritten = new bool[4];
            public int linearCounter; public bool linearReloadFlag, lengthHalt; public int linearReg;
            public int lengthCounter; public int lastAmp; public int timerPeriod, timerCounter; public int seqStep;
            public override void Reset(){ Array.Clear(regs,0,4); Array.Clear(regWritten,0,4); linearCounter=0; linearReloadFlag=false; lengthHalt=false; linearReg=0; lengthCounter=0; lastAmp=0; timerPeriod=timerCounter=0; seqStep=0; }
        }

        // Noise channel
        private class QnNoise : QnChannel {
            public byte[] regs = new byte[4]; public bool[] regWritten = new bool[4];
            public int envDivider, envDecay; public bool envStart, constantVolume, lengthHalt; public int volumeParam;
            public int lengthCounter; public int lastAmp; public ushort shiftRegister; public int timerPeriod, timerCounter; public bool modeFlag; // mode flag bit 7
            public override void Reset(){ Array.Clear(regs,0,4); Array.Clear(regWritten,0,4); envDivider=envDecay=0; envStart=constantVolume=lengthHalt=false; volumeParam=0; lengthCounter=0; lastAmp=0; shiftRegister=1; timerPeriod=timerCounter=0; modeFlag=false; }
        }

        // DMC channel
        private class QnDmc : QnChannel {
            public byte[] regs = new byte[4]; public bool[] regWritten = new bool[4];
            public int lastAmp; public int timerPeriod, timerCounter; public int sampleAddress, sampleLengthRemaining;
            public int shiftReg, bitsRemaining, deltaCounter = 64, sampleBuffer; public bool sampleBufferFilled, silence, irqEnable, loop, irqFlag;
            public int dac; public bool palMode; public bool nonlinear; public int nextIrqCycle; public int startAddress, startLength; public override void Reset(){ Array.Clear(regs,0,4); Array.Clear(regWritten,0,4); lastAmp=0; timerPeriod=timerCounter=0; sampleAddress=sampleLengthRemaining=0; startAddress=0; startLength=0; shiftReg=0; bitsRemaining=0; deltaCounter=64; sampleBuffer=0; sampleBufferFilled=false; silence=true; irqEnable=false; loop=false; irqFlag=false; dac=0; palMode=false; nonlinear=false; nextIrqCycle=noIrq; }
        }

        // IRQ and frame state
    private bool irqFlag; // frame IRQ
    private int framePeriod; // (legacy) kept for Reset compatibility (will be removed once PAL table exact)
    private int frameDelay; // legacy countdown (unused once precise sequencer active)
    private int frame; // legacy frame step index (unused)
    private int frameMode; // $4017 value (raw write)
    // Frame sequencer mode/flags
    private bool frame5StepMode; // bit7 of $4017
    private bool frameIrqInhibit; // bit6 of $4017 (true = inhibit IRQ)
    // C++-style scheduler fields
    private int nextIrqCycle = noIrq; // APU frame IRQ schedule (cycle when it should assert)
    private int earliestIrqCycle = noIrq; // aggregation placeholder (frame vs DMC)
    private int lastDmcTime; // kept for parity but unused in per-cycle model
    private int lastTime; // last CPU time advanced to
    private int oscEnables; // $4015 enables
    private const int noIrq = int.MaxValue; // sentinel for none (retained for mapping)
    // NOTE: Quick port currently omits earliest_irq_, next_irq, irq_changed() full fidelity.

    // Audio sampling
    private const int AudioRingSize = 32768; private readonly float[] audioRing = new float[AudioRingSize];
    private int ringWrite, ringRead, ringCount; private double sampleFrac; private const int SampleRate = 44100; 
    private double cpuFreq = 1789773.0; // NTSC default; PAL uses ~1662607.0
    private double samplesPerCpu = 44100.0 / 1789773.0; // recalculated on reset
    private bool nonlinearMixing = true; // default to accurate nonlinear mix
    // Output gain scaling for QN mixer (reduce volume ~17%)
    private const float OutputGain = 0.83f;

        public APU_QN(Bus bus)
        {
            this.bus = bus;
            square1 = new QnSquare();
            square2 = new QnSquare();
            triangle = new QnTriangle();
            noise = new QnNoise();
            dmc = new QnDmc();
            Reset(false);
        }

        public void Step(int cpuCycles)
        {
            // Advance CPU cycles, update frame scheduler, channels, DMC, and audio.
            for(int i=0;i<cpuCycles;i++)
            {
                ClockFrameSequencerCxx();
                ClockChannelsOneCpu();
                // Scheduled frame IRQ trigger (when cycle reached)
                if(nextIrqCycle != noIrq && lastTime >= nextIrqCycle)
                {
                    if(!frameIrqInhibit) { irqFlag = true; bus.cpu.RequestIRQ(true); }
                    nextIrqCycle = noIrq; IrqChanged();
                }
                sampleFrac += samplesPerCpu; // samples per CPU cycle
                if(sampleFrac >= 1.0)
                {
                    int emit = (int)sampleFrac; sampleFrac -= emit;
                    for(int s=0;s<emit;s++) MixAndStoreOutput();
                }
                lastTime++;
            }
        }

        // === Channel per-CPU clocking (simplified) ===
        private static readonly byte[][] PulseDutyTable = {
            new byte[]{0,1,0,0,0,0,0,0}, new byte[]{0,1,1,0,0,0,0,0}, new byte[]{0,1,1,1,1,0,0,0}, new byte[]{1,0,0,1,1,1,1,1}
        };

        private void ClockChannelsOneCpu()
        {
            // Squares
            ClockSquare(square1, true);
            ClockSquare(square2, false);
            // Triangle
            ClockTriangle(triangle);
            // Noise
            ClockNoise(noise);
            // DMC (placeholder)
            ClockDmc();
        }

    // === Frame Sequencer (classic 4/5-step scheduling) ===
        private void ClockFrameSequencerCxx()
        {
            // Decrement legacy frameDelay; when it hits zero, run a frame event
            if(--frameDelay > 0) return;

            // Reset delay to base period
            frameDelay = framePeriod;

            // Take frame-specific actions based on current step (0..3)
            // Note: In C++ code, case 0 falls through to case 2 to also clock length/sweep.
            if(frame == 0)
            {
                // Schedule next frame IRQ for 4-step mode when not inhibited
                if((frameMode & 0xC0) == 0)
                {
                    nextIrqCycle = lastTime + framePeriod * 4 + 1;
                    irqFlag = true; // flag becomes set; actual CPU line asserted when nextIrqCycle occurs
                    IrqChanged();
                }
                // fall-through to length/sweep clocking handled below
            }

            // Clock length and sweep on frames 0 and 2
            if(frame == 0 || frame == 2)
            {
                DecrementLength(square1); DecrementLength(square2); DecrementLength(triangle); DecrementLength(noise);
                Sweep(square1,true); Sweep(square2,false);
            }

            if(frame == 1)
            {
                // frame 1 is slightly shorter
                frameDelay -= 2;
            }
            else if(frame == 3)
            {
                // wrap to 0
                frame = 0;
                // in mode 1 (5-step), frame 3 is almost twice as long
                if((frameMode & 0x80) != 0)
                    frameDelay += framePeriod - 6;
                // Clock envelopes/linear happens below for every frame
            }
            else
            {
                // advance to next frame step
                frame++;
            }

            // Envelopes and linear counter clock every frame
            TriangleLinearAndEnvelopesTick();
        }

        private void TriangleLinearAndEnvelopesTick()
        {
            LinearCounterTriangle(triangle);
            EnvelopeSquare(square1);
            EnvelopeSquare(square2);
            EnvelopeNoise(noise);
        }

    // Legacy helpers (kept if needed elsewhere)
    private void QuarterFrameTick() { TriangleLinearAndEnvelopesTick(); }
    private void HalfFrameTick() { DecrementLength(square1); DecrementLength(square2); DecrementLength(triangle); DecrementLength(noise); Sweep(square1,true); Sweep(square2,false); }

        private void EnvelopeSquare(QnSquare sq){ if(sq.envStart){ sq.envStart=false; sq.envDecay=15; sq.envDivider = sq.volumeParam+1; } else { if(--sq.envDivider <=0){ sq.envDivider = sq.volumeParam+1; if(sq.envDecay>0) sq.envDecay--; else if(sq.lengthHalt) sq.envDecay=15; } } }
        private void EnvelopeNoise(QnNoise ch){ if(ch.envStart){ ch.envStart=false; ch.envDecay=15; ch.envDivider = ch.volumeParam+1; } else { if(--ch.envDivider <=0){ ch.envDivider = ch.volumeParam+1; if(ch.envDecay>0) ch.envDecay--; else if(ch.lengthHalt) ch.envDecay=15; } } }
    private void LinearCounterTriangle(QnTriangle t){ if(t.linearReloadFlag){ t.linearCounter = t.linearReg & 0x7F; } else if(t.linearCounter>0) t.linearCounter = Math.Max(0, t.linearCounter-1); if((t.linearReg & 0x80)==0) t.linearReloadFlag=false; }
        private void DecrementLength(QnSquare sq){ if(!sq.lengthHalt && sq.lengthCounter>0) sq.lengthCounter--; }
        private void DecrementLength(QnTriangle t){ if(!t.lengthHalt && t.lengthCounter>0) t.lengthCounter--; }
        private void DecrementLength(QnNoise n){ if(!n.lengthHalt && n.lengthCounter>0) n.lengthCounter--; }
        private void Sweep(QnSquare sq, bool channel1){
            // Accurate-ish sweep behavior per NES APU docs.
            if(sq.sweepReload){ // reload divider regardless of shift
                sq.sweepDivider = sq.sweepPeriod;
                sq.sweepReload = false;
                if(sq.sweepShift==0) return; // do not apply change this instant if shift zero
            } else {
                if(--sq.sweepDivider > 0) return; // wait until divider hits zero
                sq.sweepDivider = sq.sweepPeriod; // reload
                if(sq.sweepShift==0) return; // no change if shift zero
            }

            int change = sq.timerPeriod >> sq.sweepShift;
            int target = sq.sweepNegate ? (sq.timerPeriod - change - (channel1?1:0)) : (sq.timerPeriod + change);
            // Do not update if overflow beyond 11 bits
            if(target <= 0x7FF && target >= 0){
                if(!sq.sweepMute && target >= 8) {
                    sq.timerPeriod = target & 0x7FF; // 11-bit mask
                    // Recompute mute after update
                    RecomputeSweepMute(sq, channel1);
                }
            }
        }

        private void RecomputeSweepMute(QnSquare sq, bool channel1){
            // Evaluate future target for mute decision (sweepShift >0)
            if(sq.timerPeriod < 8){ sq.sweepMute = true; return; }
            if(sq.sweepShift==0){ sq.sweepMute = false; return; }
            int change = sq.timerPeriod >> sq.sweepShift;
            int target = sq.sweepNegate ? (sq.timerPeriod - change - (channel1?1:0)) : (sq.timerPeriod + change);
            sq.sweepMute = (target > 0x7FF) || (target < 8);
        }
    // (removed stray brace that prematurely closed class)

        private void ClockSquare(QnSquare sq, bool channel1)
        {
            if((oscEnables & (channel1?0x01:0x02))==0 || sq.lengthCounter==0 || sq.timerPeriod < 8 || sq.sweepMute){ sq.lastAmp=0; return; }
            if(--sq.timerCounter <=0){ sq.timerCounter = (sq.timerPeriod+1)*2; sq.dutyStep = (sq.dutyStep+1)&7; }
            int vol = sq.constantVolume ? sq.volumeParam : sq.envDecay; if(vol<0) vol=0; if(vol>15) vol=15;
            int bit = PulseDutyTable[sq.duty & 3][sq.dutyStep]; sq.lastAmp = (bit==1)? vol : 0;
        }
        private void ClockTriangle(QnTriangle tri)
        {
            if((oscEnables & 0x04)==0 || tri.lengthCounter==0 || tri.linearCounter==0 || tri.timerPeriod < 2){ tri.lastAmp=0; return; }
            if(--tri.timerCounter <=0){ tri.timerCounter = tri.timerPeriod+1; tri.seqStep = (tri.seqStep+1)&31; }
            tri.lastAmp = (tri.seqStep<16)? (15 - tri.seqStep) : (tri.seqStep - 16);
        }
        private static readonly int[] NoisePeriods = {4,8,16,32,64,96,128,160,202,254,380,508,762,1016,2034,4068};
        private void ClockNoise(QnNoise n)
        {
            if((oscEnables & 0x08)==0 || n.lengthCounter==0){ n.lastAmp=0; return; }
            if(--n.timerCounter <=0){
                n.timerCounter = n.timerPeriod;
                int bit0 = n.shiftRegister & 1;
                int tap = n.modeFlag ? ((n.shiftRegister>>6)&1) : ((n.shiftRegister>>1)&1);
                int fb = bit0 ^ tap;
                n.shiftRegister = (ushort)(((n.shiftRegister >> 1) | (fb << 14)) & 0x7FFF); // 15-bit mask
                if(n.shiftRegister==0) n.shiftRegister = 1; // avoid lock
            }
            int vol = n.constantVolume ? n.volumeParam : n.envDecay; n.lastAmp = ((n.shiftRegister & 1)==0)? vol : 0;
        }
        private void ClockDmc()
        {
            // DMC bit clock and sample refill
            if((oscEnables & 0x10) == 0) return; // DMC disabled
            if(dmc.timerPeriod <= 0) return;
            if(--dmc.timerCounter > 0) return;
            dmc.timerCounter = dmc.timerPeriod;

            // Refill buffer if needed
            if(!dmc.sampleBufferFilled && dmc.sampleLengthRemaining > 0)
            {
                byte b = bus.Read((ushort)dmc.sampleAddress);
                dmc.sampleBuffer = b;
                dmc.sampleBufferFilled = true;
                dmc.sampleAddress = (dmc.sampleAddress + 1) & 0xFFFF;
                if(dmc.sampleAddress == 0) dmc.sampleAddress = 0x8000; // wrap to PRG area on overflow
                dmc.sampleLengthRemaining--;
                if(dmc.sampleLengthRemaining == 0)
                {
                    if(dmc.loop)
                    {
                        dmc.sampleAddress = dmc.startAddress;
                        dmc.sampleLengthRemaining = dmc.startLength;
                    }
                    else if(dmc.irqEnable)
                    {
                        dmc.irqFlag = true; dmc.nextIrqCycle = lastTime; IrqChanged();
                    }
                }
            }

            // Load new shift register if none pending
            if(dmc.bitsRemaining == 0)
            {
                if(dmc.sampleBufferFilled)
                {
                    dmc.silence = false;
                    dmc.shiftReg = dmc.sampleBuffer;
                    dmc.bitsRemaining = 8;
                    dmc.sampleBufferFilled = false;
                }
                else
                {
                    dmc.silence = true;
                }
            }

            // Consume one bit
            if(dmc.bitsRemaining > 0)
            {
                int bit = dmc.shiftReg & 1;
                if(!dmc.silence)
                {
                    dmc.deltaCounter += (bit == 1) ? 2 : -2;
                    if(dmc.deltaCounter < 0) dmc.deltaCounter = 0; else if(dmc.deltaCounter > 127) dmc.deltaCounter = 127;
                    dmc.lastAmp = dmc.deltaCounter;
                }
                dmc.shiftReg >>= 1;
                dmc.bitsRemaining--;
            }
        }

        // === DMC scaffolding (Task 1 partial) ===
        private static readonly int[] DmcRatesNTSC = {428,380,340,320,286,254,226,214,190,160,142,128,106, 85, 72, 54};
        private static readonly int[] DmcRatesPAL  = {398,354,316,298,276,236,210,198,176,148,132,118, 98, 78, 66, 50};
        private void DmcUpdateTimerFromRateIndex(int idx)
        {
            var table = dmc.palMode ? DmcRatesPAL : DmcRatesNTSC;
            idx &= 0x0F; dmc.timerPeriod = table[idx]; if(dmc.timerCounter<=0) dmc.timerCounter = dmc.timerPeriod;
        }

        private void MixAndStoreOutput()
        {
            double p1 = square1.lastAmp; double p2 = square2.lastAmp; double t = triangle.lastAmp; double n = noise.lastAmp; double d = dmc.lastAmp;
            float mixed;
            if(nonlinearMixing)
            {
                double pulseMix = (p1 + p2)==0 ? 0.0 : 95.88 / (8128.0 / (p1 + p2) + 100.0);
                double tnd = (t + n + d)==0 ? 0.0 : 159.79 / (1.0 / (t/8227.0 + n/12241.0 + d/22638.0) + 100.0);
                mixed = (float)(pulseMix + tnd);
            }
            else
            {
                // Linear gains approximating common mixer constants
                // square: 0.1128/15, triangle: 0.12765/15, noise: 0.0741/15, dmc: 0.42545/127
                double sum = (0.1128 / 15.0) * (p1 + p2)
                           + (0.12765 / 15.0) * t
                           + (0.0741 / 15.0) * n
                           + (0.42545 / 127.0) * d;
                mixed = (float)sum;
            }
            // Apply master output gain
            mixed *= OutputGain;
            if(ringCount >= AudioRingSize){ ringRead = (ringRead+1) & (AudioRingSize-1); ringCount--; }
            audioRing[ringWrite]=mixed; ringWrite = (ringWrite+1)&(AudioRingSize-1); ringCount++;
        }

    // (Removed obsolete RunFrameSequencer skeleton; integrated in ClockFrameSequencerInternal)

        // Length table (same values as NES spec)
        private static readonly int[] LengthTable = { 10,254,20,2,40,4,80,6,160,8,60,10,14,12,26,14,12,16,24,18,48,20,96,22,192,24,72,26,16,28,32,30 };

        public void WriteAPURegister(ushort address, byte value)
        {
            if(address < 0x4000 || address > 0x4017) return;
            if(address < 0x4014)
            {
                int oscIndex = (address - 0x4000) >> 2; int reg = address & 3;
                switch(oscIndex)
                {
                    case 0: WriteSquare(square1, reg, value); break;
                    case 1: WriteSquare(square2, reg, value); break;
                    case 2: WriteTriangle(reg, value); break;
                    case 3: WriteNoise(reg, value); break;
                    case 4: WriteDmc(reg, value); break; // placeholder
                }
            }
            else if(address == 0x4015) 
            {
                int old = oscEnables;
                oscEnables = value;

                // Clear length counters only on disable (1->0)
                if(((old & 0x01)!=0) && ((value & 0x01)==0)) square1.lengthCounter = 0;
                if(((old & 0x02)!=0) && ((value & 0x02)==0)) square2.lengthCounter = 0;
                if(((old & 0x04)!=0) && ((value & 0x04)==0)) triangle.lengthCounter = 0;
                if(((old & 0x08)!=0) && ((value & 0x08)==0)) noise.lengthCounter = 0;

                // DMC enable transition handling (simplified)
                bool dmcOld = (old & 0x10) != 0; bool dmcNew = (value & 0x10) != 0;
                // Clear DMC IRQ flag on any $4015 write
                bool recalc = dmc.irqFlag;
                dmc.irqFlag = false;
                if(!dmcNew && dmcOld)
                {
                    // Disable: stop playback, clear IRQ flag
                    dmc.sampleLengthRemaining = 0;
                    dmc.nextIrqCycle = noIrq;
                    recalc = true;
                }
                else if(dmcNew && !dmcOld)
                {
                    // Enable: start sample if not already
                    if(dmc.sampleLengthRemaining == 0)
                    {
                        dmc.sampleAddress = dmc.startAddress;
                        dmc.sampleLengthRemaining = dmc.startLength;
                    }
                    // If buffer empty, next DMC tick will fetch
                }
                if (recalc) IrqChanged();
                // Note: frame IRQ cleared on $4015 read or $4017 write, not here.
            }
            else if(address == 0x4017)
            {
                // Frame mode write
                frameMode = value;
                bool irqEnabled = (value & 0x40) == 0;
                frame5StepMode = (value & 0x80)!=0;
                frameIrqInhibit = !irqEnabled;
                irqFlag &= irqEnabled; // keep flag only if IRQs enabled
                nextIrqCycle = noIrq;

                // Mode 1 baseline
                frameDelay = (frameDelay & 1);
                frame = 0;

                if(!frame5StepMode)
                {
                    // Mode 0
                    frame = 1;
                    frameDelay += framePeriod;
                    if(irqEnabled)
                        nextIrqCycle = lastTime + frameDelay + framePeriod * 3;
                }
                IrqChanged();
            }
        }

        private void WriteSquare(QnSquare sq, int reg, byte v)
        {
            sq.regs[reg]=v; sq.regWritten[reg]=true;
            switch(reg)
            {
                case 0:
                    sq.duty = (v>>6)&3; sq.lengthHalt = (v & 0x20)!=0; sq.constantVolume = (v & 0x10)!=0; sq.volumeParam = v & 0x0F; sq.envStart=true; break;
                case 1:
                    sq.sweepPeriod = ((v>>4)&7)+1; sq.sweepNegate = (v & 0x08)!=0; sq.sweepShift = v & 7; sq.sweepReload=true; RecomputeSweepMute(sq, sq==square1); break;
                case 2:
                    sq.timerPeriod = ((sq.timerPeriod & 0x0700) | v) & 0x7FF; RecomputeSweepMute(sq, sq==square1); break;
                case 3:
                    sq.timerPeriod = ((sq.timerPeriod & 0x00FF) | ((v & 0x07)<<8)) & 0x7FF; int lengthIdx = (v>>3)&0x1F; if((oscEnables & (sq==square1?0x01:0x02))!=0 && lengthIdx < LengthTable.Length) sq.lengthCounter = LengthTable[lengthIdx]; sq.envStart=true; sq.timerCounter = (sq.timerPeriod+1)*2; sq.dutyStep = 0; RecomputeSweepMute(sq, sq==square1); break;
            }
        }
        private void WriteTriangle(int reg, byte v)
        {
            triangle.regs[reg]=v; triangle.regWritten[reg]=true;
            switch(reg)
            {
                case 0: triangle.lengthHalt = (v & 0x80)!=0; triangle.linearReg = v; triangle.linearReloadFlag=true; break;
                case 2: triangle.timerPeriod = (triangle.timerPeriod & 0x0700) | v; break;
                case 3: triangle.timerPeriod = (triangle.timerPeriod & 0x00FF) | ((v & 0x07)<<8); int lengthIdx = (v>>3)&0x1F; if((oscEnables & 0x04)!=0 && lengthIdx < LengthTable.Length) triangle.lengthCounter = LengthTable[lengthIdx]; triangle.seqStep=0; triangle.timerCounter = triangle.timerPeriod+1; triangle.linearReloadFlag=true; break;
            }
        }
        private void WriteNoise(int reg, byte v)
        {
            noise.regs[reg]=v; noise.regWritten[reg]=true;
            switch(reg)
            {
                case 0: noise.lengthHalt = (v & 0x20)!=0; noise.constantVolume = (v & 0x10)!=0; noise.volumeParam = v & 0x0F; noise.envStart=true; break;
                case 2: noise.modeFlag = (v & 0x80)!=0; int periodIdx = v & 0x0F; noise.timerPeriod = NoisePeriods[periodIdx]; break;
                case 3: int lengthIdx = (v>>3)&0x1F; if((oscEnables & 0x08)!=0 && lengthIdx < LengthTable.Length) noise.lengthCounter = LengthTable[lengthIdx]; noise.envStart=true; break;
            }
        }
        private void WriteDmc(int reg, byte v)
        {
            dmc.regs[reg]=v; dmc.regWritten[reg]=true;
            switch(reg)
            {
                case 0: // $4010 control: IL--RRRR
                    dmc.irqEnable = (v & 0x80)!=0;
                    dmc.loop = (v & 0x40)!=0;
                    DmcUpdateTimerFromRateIndex(v & 0x0F);
                    if(!dmc.irqEnable) { dmc.irqFlag=false; dmc.nextIrqCycle = noIrq; IrqChanged(); }
                    break;
                case 1: // $4011 direct load (update DAC immediately)
                    dmc.dac = v & 0x7F; dmc.deltaCounter = dmc.dac; dmc.lastAmp = dmc.deltaCounter;
                    break;
                case 2: // $4012 sample address
                    dmc.startAddress = 0xC000 + (v << 6);
                    if(dmc.sampleLengthRemaining == 0 && dmc.bitsRemaining == 0 && !dmc.sampleBufferFilled)
                        dmc.sampleAddress = dmc.startAddress;
                    break;
                case 3: // $4013 sample length
                    dmc.startLength = (v << 4) + 1;
                    if(dmc.sampleLengthRemaining == 0 && dmc.bitsRemaining == 0 && !dmc.sampleBufferFilled)
                        dmc.sampleLengthRemaining = dmc.startLength;
                    break;
            }
        }

        public byte ReadAPURegister(ushort address)
        {
            if(address==0x4015)
            {
                byte result = 0;
                if(square1.lengthCounter>0) result |= 0x01; if(square2.lengthCounter>0) result |= 0x02; if(triangle.lengthCounter>0) result |= 0x04; if(noise.lengthCounter>0) result |= 0x08; if(dmc.sampleLengthRemaining>0 || dmc.bitsRemaining>0) result |= 0x10; if(irqFlag) result |= 0x40; if(dmc.irqFlag) result |= 0x80;
                irqFlag = false; dmc.irqFlag=false; // reading clears
                IrqChanged();
                return result;
            }
            return 0;
        }

        public float[] GetAudioSamples(int maxSamples = 0)
        {
            if(ringCount==0) return Array.Empty<float>();
            int toRead = ringCount;
            if(maxSamples>0 && maxSamples < toRead) toRead = maxSamples;
            // safety cap default
            if(maxSamples==0 && toRead > 4096) toRead = 4096;
            float[] buf = new float[toRead];
            int first = Math.Min(toRead, AudioRingSize - ringRead);
            Array.Copy(audioRing, ringRead, buf, 0, first);
            int rem = toRead - first; if(rem>0) Array.Copy(audioRing,0,buf,first,rem);
            ringRead = (ringRead + toRead) & (AudioRingSize-1); ringCount -= toRead;
            return buf;
        }

    public int GetQueuedSampleCount() => ringCount;
    public int GetSampleRate() => SampleRate;

        // Optional hook for bus resets/hot-swaps to drop any queued audio and fractional pacing
        public void ClearAudioBuffers()
        {
            ringRead = ringWrite = ringCount = 0;
            sampleFrac = 0;
        }

        // Minimal Reset: clear audio buffers and pacing; keep registers and timers.
        public void Reset()
        {
            ClearAudioBuffers();
        }

    // Optional helper to compact internal timers at a frame boundary.
        // It compacts internal time counters to keep them small and, when nonlinear mixing is enabled,
        // zeroes channel last_amp values at the boundary to avoid discontinuities.
        public void EndFrame()
        {
            // Zero last_amp at frame boundary for nonlinear path (matches C++ behavior intent)
            if (nonlinearMixing)
            {
                square1.lastAmp = 0; square2.lastAmp = 0; triangle.lastAmp = 0; noise.lastAmp = 0; dmc.lastAmp = 0;
            }

            // Compact times to be relative to new zero
            int delta = lastTime;
            if (delta <= 0) return;

            lastTime = 0;
            lastDmcTime = Math.Max(0, lastDmcTime - delta);
            if (nextIrqCycle != noIrq)
            {
                long t = (long)nextIrqCycle - delta;
                nextIrqCycle = t <= 0 ? 0 : (int)t;
            }
            if (dmc.nextIrqCycle != noIrq)
            {
                long t = (long)dmc.nextIrqCycle - delta;
                dmc.nextIrqCycle = t <= 0 ? 0 : (int)t;
            }
            // Recompute earliest aggregation after compaction
            IrqChanged();
        }

    // Minimal helper to query next DMC timer event time
        // Returns the cycle count (relative to lastTime) when the next DMC timer event would occur,
        // which is when the DMC would either consume a bit or fetch a byte if the sample buffer is empty.
        private int NextDmcReadTime()
        {
            if ((oscEnables & 0x10) == 0 || dmc.timerPeriod <= 0)
                return noIrq;
            int ticks = dmc.timerCounter > 0 ? dmc.timerCounter : dmc.timerPeriod;
            return lastTime + ticks;
        }

    // === Save / Load (expanded) ===
    private class State { public int v=3; public int oscEnables, frameMode, frameDelay, frame, framePeriod; public bool irqFlag; public double sampleFrac; public QnSquare? s1,s2; public QnTriangle? tri; public QnNoise? noi; public QnDmc? d; public int ringWrite,ringRead,ringCount; public bool frame5StepMode, frameIrqInhibit; public bool nonlinear; public bool pal; public int nextIrqCycle, earliestIrqCycle, lastTime, lastDmcTime; }
        public object GetState()=> new State { oscEnables=oscEnables, frameMode=frameMode, frameDelay=frameDelay, frame=frame, framePeriod=framePeriod, irqFlag=irqFlag, sampleFrac=sampleFrac,
            s1=Clone(square1), s2=Clone(square2), tri=Clone(triangle), noi=Clone(noise), d=Clone(dmc), ringWrite=ringWrite, ringRead=ringRead, ringCount=ringCount,
            frame5StepMode=frame5StepMode, frameIrqInhibit=frameIrqInhibit, nonlinear=nonlinearMixing, pal=dmc.palMode, nextIrqCycle=nextIrqCycle, earliestIrqCycle=earliestIrqCycle, lastTime=lastTime, lastDmcTime=lastDmcTime };
        public void SetState(object state){ if(state is State s && s.s1!=null && s.s2!=null && s.tri!=null && s.noi!=null && s.d!=null){ oscEnables=s.oscEnables; frameMode=s.frameMode; frameDelay=s.frameDelay; frame=s.frame; framePeriod=s.framePeriod; irqFlag=s.irqFlag; sampleFrac=0; square1 = s.s1; square2 = s.s2; triangle = s.tri; noise = s.noi; dmc = s.d; // clear any serialized audio backlog to avoid stutter
            ringWrite=ringRead=ringCount=0; frame5StepMode=s.frame5StepMode; frameIrqInhibit=s.frameIrqInhibit; nonlinearMixing=s.nonlinear; nextIrqCycle=s.nextIrqCycle; earliestIrqCycle=s.earliestIrqCycle; lastTime=s.lastTime; lastDmcTime=s.lastDmcTime; if(dmc.palMode!=s.pal){ SetRegion(s.pal); } } }

        // Set region without full logical reset; updates timing tables and pacing
        public void SetRegion(bool pal)
        {
            dmc.palMode = pal;
            cpuFreq = pal ? 1662607.0 : 1789773.0;
            samplesPerCpu = SampleRate / cpuFreq;
            framePeriod = pal ? 8314 : 7458;
        }
        // Shallow clone helpers (classes hold only value fields except arrays which we copy manually where needed)
        private static QnSquare Clone(QnSquare src){ return new QnSquare{ regs=(byte[])src.regs.Clone(), regWritten=(bool[])src.regWritten.Clone(), envDivider=src.envDivider, envDecay=src.envDecay, envStart=src.envStart, constantVolume=src.constantVolume, lengthHalt=src.lengthHalt, volumeParam=src.volumeParam, sweepNegate=src.sweepNegate, sweepReload=src.sweepReload, sweepShift=src.sweepShift, sweepPeriod=src.sweepPeriod, sweepDivider=src.sweepDivider, lengthCounter=src.lengthCounter, timerPeriod=src.timerPeriod, timerCounter=src.timerCounter, duty=src.duty, dutyStep=src.dutyStep, lastAmp=src.lastAmp }; }
        private static QnTriangle Clone(QnTriangle t){ return new QnTriangle{ regs=(byte[])t.regs.Clone(), regWritten=(bool[])t.regWritten.Clone(), linearCounter=t.linearCounter, linearReloadFlag=t.linearReloadFlag, lengthHalt=t.lengthHalt, linearReg=t.linearReg, lengthCounter=t.lengthCounter, lastAmp=t.lastAmp, timerPeriod=t.timerPeriod, timerCounter=t.timerCounter, seqStep=t.seqStep }; }
        private static QnNoise Clone(QnNoise n){ return new QnNoise{ regs=(byte[])n.regs.Clone(), regWritten=(bool[])n.regWritten.Clone(), envDivider=n.envDivider, envDecay=n.envDecay, envStart=n.envStart, constantVolume=n.constantVolume, lengthHalt=n.lengthHalt, volumeParam=n.volumeParam, lengthCounter=n.lengthCounter, lastAmp=n.lastAmp, shiftRegister=n.shiftRegister, timerPeriod=n.timerPeriod, timerCounter=n.timerCounter, modeFlag=n.modeFlag }; }
    private static QnDmc Clone(QnDmc d){ return new QnDmc{ regs=(byte[])d.regs.Clone(), regWritten=(bool[])d.regWritten.Clone(), lastAmp=d.lastAmp, timerPeriod=d.timerPeriod, timerCounter=d.timerCounter, sampleAddress=d.sampleAddress, sampleLengthRemaining=d.sampleLengthRemaining, startAddress=d.startAddress, startLength=d.startLength, shiftReg=d.shiftReg, bitsRemaining=d.bitsRemaining, deltaCounter=d.deltaCounter, sampleBuffer=d.sampleBuffer, sampleBufferFilled=d.sampleBufferFilled, silence=d.silence, irqEnable=d.irqEnable, loop=d.loop, irqFlag=d.irqFlag, dac=d.dac, palMode=d.palMode, nonlinear=d.nonlinear, nextIrqCycle=d.nextIrqCycle }; }

        public void Reset(bool palMode, int initialDmcDac = 0)
        {
            framePeriod = palMode ? 8314 : 7458; // legacy
            dmc.palMode = palMode; square1.Reset(); square2.Reset(); triangle.Reset(); noise.Reset(); dmc.Reset();
            lastTime = 0; lastDmcTime = 0; oscEnables = 0; irqFlag=false; frameDelay=1; frame=0; frameMode=0; nextIrqCycle=noIrq; earliestIrqCycle=noIrq; sampleFrac=0; ringRead=ringWrite=ringCount=0;
            frame5StepMode=false; frameIrqInhibit=false;
            dmc.dac = initialDmcDac; // initialize DAC
            // Configure CPU frequency and sample pacing for region
            cpuFreq = palMode ? 1662607.0 : 1789773.0;
            samplesPerCpu = SampleRate / cpuFreq;
            // Perform reset writes to registers to achieve muted power-on state
            WriteAPURegister(0x4017, 0x00);
            WriteAPURegister(0x4015, 0x00);
            for(ushort addr = 0x4000; addr <= 0x4013; addr++)
            {
                byte v = ((addr & 3) == 0) ? (byte)0x10 : (byte)0x00;
                WriteAPURegister(addr, v);
            }
        }
        public void Reset(bool palMode) => Reset(palMode, 0);

    // Nonlinear mixing toggle
        public void SetNonlinearMixing(bool enabled)
        {
            nonlinearMixing = enabled;
            dmc.nonlinear = enabled;
            if (enabled)
            {
                // Reset last_amp when enabling nonlinear path
                square1.lastAmp = 0; square2.lastAmp = 0; triangle.lastAmp = 0; noise.lastAmp = 0; dmc.lastAmp = 0;
            }
            else
            {
                // Match expected triangle behavior when nonlinear disabled
                triangle.lastAmp = 15;
            }
        }

        // IRQ aggregation placeholder to mirror C++ flow
        private void IrqChanged()
        {
            // Only assert CPU IRQ immediately for DMC irqFlag (happens at exact bit/byte timing).
            if (dmc.irqFlag)
            {
                bus.cpu.RequestIRQ(true);
            }
            // Frame IRQs are asserted when nextIrqCycle is reached inside Step().
            // Track earliest for informational purposes.
            int newEarliest = noIrq;
            if (nextIrqCycle != noIrq) newEarliest = nextIrqCycle;
            if (dmc.nextIrqCycle != noIrq)
                newEarliest = (newEarliest == noIrq) ? dmc.nextIrqCycle : Math.Min(newEarliest, dmc.nextIrqCycle);
            earliestIrqCycle = newEarliest;
        }
    }
}
