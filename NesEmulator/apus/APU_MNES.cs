using System;

namespace NesEmulator
{
    public class APU_MNES : IAPU
    {
        // Core metadata
        public string CoreName => "UNIMPLEMENTED";
        public string Description => "UNIMPLEMENTED";
        public int Performance => 0;
        public int Rating => 1;
        private Bus bus;
    public APU_MNES(Bus bus)
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
    // DPCM (DMC) channel minimal state for SoundFont note-event representation
        private bool dpcm_loop = false;
        private int dpcm_rateIndex = 0; // 0..15
        private byte dpcm_outputLevel = 0; // 0..127 (raw DAC load approximation)
        private ushort dpcm_sampleAddress = 0; // base CPU address (not fully emulated)
        private int dpcm_sampleLengthBytes = 0; // in bytes (value*16+1)
        private bool dpcm_active = false; // currently “playing” sample
        private long dpcm_remainingCycles = 0; // countdown until stop/loop
        private const int ProgramDpcm = 0; // Bank 128 Program 0 (bank not yet emitted; future: CC0=128)
        private int? activeDpcmNote; // synthetic MIDI note representing DPCM playback

        // ================== SoundFont / WebAudio NOTE EVENT MODE ==================
        // When enabled, the APU stops producing PCM samples and instead emits high-level
        // note on/off events per logical channel so the host (Blazor + JS) can map them
        // to SoundFont or oscillator-based instruments. This avoids pushing large audio
        // buffers across the JS interop boundary and lets the browser handle mixing.
        private bool soundFontMode = false; // backing field
        public bool SoundFontMode
        {
            get => soundFontMode;
            set
            {
                if (soundFontMode == value) return;
                soundFontMode = value;
                if (soundFontMode)
                {
                    // Drop any queued PCM to prevent stale playback overlap
                    ClearAudioBuffers();
                    // Reset previous state trackers so first frame re-triggers notes cleanly
                    ResetNoteTracking();
                }
                else
                {
                    // Ensure any lingering active notes are released to prevent stuck sound
                    EmitAllNoteOff();
                }
            }
        }

        // Public event consumers can subscribe to for note events.
        // Keep allocation minimal by reusing a struct-like record for each raise.
        public record struct NesNoteEvent(string Channel, int MidiNote, int Velocity, bool On, int Program);
        public event Action<NesNoteEvent>? NoteEvent;

    // MNES.sf2 program layout (Bank 0 unless noted):
    // 0: Pulse 12.5%  1: Pulse 25%  2: Pulse 50%  3: Pulse 75%  4: Triangle  5: Noise
    // (Bank 128 Program 0 reserved for future DPCM implementation.)
    // We dynamically map pulse duty cycles to program numbers instead of static GM leads.
    private static int ProgramForPulseDuty(byte duty) => duty switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, _ => 2 };
    private const int ProgramTriangle = 4;
    private const int ProgramNoise = 5;

    // Active note tracking so we send balanced NoteOff messages and support pitch bend
    private int? activePulse1Note, activePulse2Note, activeTriangleNote;
    // Track last program used per channel so we can re-trigger if the duty (instrument) changes mid-note
    private int lastPulse1Program = -1, lastPulse2Program = -1;
    // Base frequencies captured at note-on for pitch bend reference
    private double basePulse1Freq, basePulse2Freq, baseTriangleFreq;
    // Last sent pitch bend values (MIDI 14-bit center = 0 range -8192..8191)
    private int lastPulse1Pitch = 0, lastPulse2Pitch = 0, lastTrianglePitch = 0;
    // Pitch bend configuration (+/- semitone range we map into full wheel)
    private const double PitchBendRangeSemitones = 2.0; // typical default
    private const double ReTriggerThresholdSemitones = 2.2; // if sweep exceeds range, re-trigger with new note
    private const double MinBendDeltaSemitones = 1.0 / 64.0; // vibrato sensitivity

    // Pitch (bend) event for consumers wanting continuous pitch articulation
    public event Action<string,int>? PitchEvent; // (channel, bendValue -8192..8191)
        // Previous counters to detect (re)trigger conditions & release
        private int prevPulse1Length, prevPulse2Length, prevTriangleLength, prevTriangleLinear, prevNoiseLength;
        // Cached envelope values last used for velocities to detect significant change (optional future use)
        private int lastP1Velocity, lastP2Velocity;

        private void ResetNoteTracking()
        {
            activePulse1Note = activePulse2Note = activeTriangleNote = null;
            prevPulse1Length = prevPulse2Length = prevTriangleLength = prevTriangleLinear = prevNoiseLength = 0;
            lastP1Velocity = lastP2Velocity = 0;
            basePulse1Freq = basePulse2Freq = baseTriangleFreq = 0;
            lastPulse1Pitch = lastPulse2Pitch = lastTrianglePitch = 0;
            lastPulse1Program = lastPulse2Program = -1;
            activeDpcmNote = null; dpcm_active = false; dpcm_remainingCycles = 0;
        }
        private void EmitAllNoteOff()
        {
            if (activePulse1Note.HasValue) NoteEvent?.Invoke(new NesNoteEvent("P1", activePulse1Note.Value, 0, false, lastPulse1Program >= 0 ? lastPulse1Program : ProgramForPulseDuty(pulse1_duty)));
            if (activePulse2Note.HasValue) NoteEvent?.Invoke(new NesNoteEvent("P2", activePulse2Note.Value, 0, false, lastPulse2Program >= 0 ? lastPulse2Program : ProgramForPulseDuty(pulse2_duty)));
            if (activeTriangleNote.HasValue) NoteEvent?.Invoke(new NesNoteEvent("TRI", activeTriangleNote.Value, 0, false, ProgramTriangle));
            if (activeDpcmNote.HasValue) NoteEvent?.Invoke(new NesNoteEvent("DPCM", activeDpcmNote.Value, 0, false, ProgramDpcm));
            activePulse1Note = activePulse2Note = activeTriangleNote = null;
            activeDpcmNote = null; dpcm_active = false; dpcm_remainingCycles = 0;
        }
        private static int VolumeToVelocity(int vol4bit)
        {
            if (vol4bit <= 0) return 0;
            return (int)Math.Round(20 + (vol4bit / 15.0) * (127 - 20)); // map 0..15 -> 20..127
        }
        private static int FreqToMidi(double freq)
        {
            if (freq <= 0) return 0;
            int n = (int)Math.Round(69.0 + 12.0 * Math.Log(freq / 440.0, 2.0));
            if (n < 0) n = 0; else if (n > 127) n = 127;
            return n;
        }
        private void EmitNoteOn(string ch, ref int? active, int midi, int vel, int program, double baseFreq = 0, bool forceRetrigger = false)
        {
            if (midi < 0 || midi > 127) return;
            if (!forceRetrigger && active.HasValue && active.Value == midi)
            {
                // Could refresh velocity if amplitude changed; skip to reduce event spam
                return;
            }
            // Turn off previous if different
            if (active.HasValue)
                NoteEvent?.Invoke(new NesNoteEvent(ch, active.Value, 0, false, program));
            NoteEvent?.Invoke(new NesNoteEvent(ch, midi, vel, true, program));
            active = midi;
            // Reset pitch wheel to center for this channel
            switch(ch)
            {
                case "P1": basePulse1Freq = baseFreq; lastPulse1Pitch = 0; PitchEvent?.Invoke("P1", 0); break;
                case "P2": basePulse2Freq = baseFreq; lastPulse2Pitch = 0; PitchEvent?.Invoke("P2", 0); break;
                case "TRI": baseTriangleFreq = baseFreq; lastTrianglePitch = 0; PitchEvent?.Invoke("TRI", 0); break;
            }
        }
        private void EmitNoteOff(string ch, ref int? active, int program)
        {
            if (!active.HasValue) return;
            NoteEvent?.Invoke(new NesNoteEvent(ch, active.Value, 0, false, program));
            active = null;
        }
    private void ProcessSoundFontNotes()
        {
            // Improved SoundFont translation (accuracy pass referencing QN & LOW cores):
            // We approximate each channel's contribution using the commonly cited linear mixer
            // coefficients (square=0.1128, triangle=0.12765, noise=0.0741) and duty cycle high-time
            // to derive a perceptual velocity. Triangle amplitude is fixed; pulses vary by envelope
            // and duty pattern occupancy; noise now intentionally reduced further (real hardware
            // perceived loudness is lower vs current SF implementation). A mild sqrt (gamma 0.5)
            // curve is applied to better match ear perception when mapping small NES volume steps
            // to 7‑bit MIDI velocities.

            static double DutyHighFraction(byte dutyIdx)
            {
                return dutyIdx switch { 0 => 1.0/8.0, 1 => 2.0/8.0, 2 => 4.0/8.0, 3 => 6.0/8.0, _ => 0.5 };
            }
            static int AmplitudeToVelocity(double amp, double ampMax)
            {
                if (amp <= 0) return 0;
                double norm = amp / ampMax; if (norm > 1) norm = 1;
                // perceptual shaping (gamma compression)
                double perceptual = Math.Sqrt(norm); // gamma 0.5
                int vel = (int)Math.Round(8 + perceptual * 119); // reserve 0..7 for silence safety
                if (vel > 127) vel = 127; return vel;
            }
            const double PulseGain = 0.25; // gain at full volume (vol=15, duty fraction 1.0)
            const double TriangleGain = 0.25; // reference max (used as normalization reference)
            const double TriangleAttenuation = 0.69; // pre-velocity amplitude attenuation (feeds perceptual curve)
            const double TriangleVelocityScale = 0.69; // post-velocity hard scaling to guarantee ~50% perceived cut
            const double NoiseGain = 0.069; // reference before intentional reduction
            const double NoiseAttenuation = 0.25; // pre-velocity attenuation (feeds perceptual curve)
            const double NoiseVelocityScale = 0.69; // post-velocity scaling (extra safety if perceptual curve reduces effect)
            const double MaxRefGain = TriangleGain; // keep normalization reference so other channel scaling unchanged

            // Capture attack flags early (they'll clear on envelope tick)
            bool p1Attack = pulse1_envelopeStart;
            bool p2Attack = pulse2_envelopeStart;
            bool noiAttack = noise_envelopeStart;

            // ---------- Pulse 1 ----------
            if (pulse1_enabled && pulse1_lengthCounter > 0 && pulse1_timer >= 8)
            {
                double freq = 1789773.0 / (16.0 * (pulse1_timer + 1));
                int targetNote = FreqToMidi(freq);
                int rawVol = pulse1_constantVolume ? pulse1_volumeParam : pulse1_decayLevel; // 0..15
                // instantaneous amplitude approximation considers duty occupancy
                double amp = PulseGain * (rawVol / 15.0) * DutyHighFraction(pulse1_duty);
                int vel = AmplitudeToVelocity(amp, MaxRefGain);
                int program = ProgramForPulseDuty(pulse1_duty);
                // Trigger if no active note, note number changed beyond bend range, or explicit attack
                bool velocityChanged = Math.Abs(vel - lastP1Velocity) >= 10; // retrigger threshold
                bool programChanged = program != lastPulse1Program;
                if (!activePulse1Note.HasValue || p1Attack || velocityChanged || programChanged)
                {
                    lastP1Velocity = vel;
                    EmitNoteOn("P1", ref activePulse1Note, targetNote, vel, program, freq, forceRetrigger: programChanged);
                    lastPulse1Program = program;
                }
                else
                {
                    // Evaluate semitone delta vs base frequency for pitch bend
                    if (basePulse1Freq > 0)
                    {
                        double semis = 12.0 * Math.Log(freq / basePulse1Freq, 2.0);
                        if (Math.Abs(semis) > ReTriggerThresholdSemitones)
                        {
                            // New center note (sweep outside bend window)
                            EmitNoteOn("P1", ref activePulse1Note, targetNote, vel, program, freq);
                            lastPulse1Program = program;
                        }
                        else if (Math.Abs(semis) >= MinBendDeltaSemitones)
                        {
                            int bend = (int)Math.Round((semis / PitchBendRangeSemitones) * 8192.0);
                            if (bend < -8192) bend = -8192; if (bend > 8191) bend = 8191;
                            if (bend != lastPulse1Pitch) { lastPulse1Pitch = bend; PitchEvent?.Invoke("P1", bend); }
                        }
                    }
                }
            }
            else if (activePulse1Note.HasValue)
            {
                EmitNoteOff("P1", ref activePulse1Note, lastPulse1Program >= 0 ? lastPulse1Program : ProgramForPulseDuty(pulse1_duty));
            }

            // ---------- Pulse 2 ----------
            if (pulse2_enabled && pulse2_lengthCounter > 0 && pulse2_timer >= 8)
            {
                double freq = 1789773.0 / (16.0 * (pulse2_timer + 1));
                int targetNote = FreqToMidi(freq);
                int rawVol = pulse2_constantVolume ? pulse2_volumeParam : pulse2_decayLevel;
                double amp = PulseGain * (rawVol / 15.0) * DutyHighFraction(pulse2_duty);
                int vel = AmplitudeToVelocity(amp, MaxRefGain);
                int program = ProgramForPulseDuty(pulse2_duty);
                bool velocityChanged = Math.Abs(vel - lastP2Velocity) >= 10;
                bool programChanged = program != lastPulse2Program;
                if (!activePulse2Note.HasValue || p2Attack || velocityChanged || programChanged)
                {
                    lastP2Velocity = vel;
                    EmitNoteOn("P2", ref activePulse2Note, targetNote, vel, program, freq, forceRetrigger: programChanged);
                    lastPulse2Program = program;
                }
                else
                {
                    if (basePulse2Freq > 0)
                    {
                        double semis = 12.0 * Math.Log(freq / basePulse2Freq, 2.0);
                        if (Math.Abs(semis) > ReTriggerThresholdSemitones)
                        {
                            EmitNoteOn("P2", ref activePulse2Note, targetNote, vel, program, freq);
                            lastPulse2Program = program;
                        }
                        else if (Math.Abs(semis) >= MinBendDeltaSemitones)
                        {
                            int bend = (int)Math.Round((semis / PitchBendRangeSemitones) * 8192.0);
                            if (bend < -8192) bend = -8192; if (bend > 8191) bend = 8191;
                            if (bend != lastPulse2Pitch) { lastPulse2Pitch = bend; PitchEvent?.Invoke("P2", bend); }
                        }
                    }
                }
            }
            else if (activePulse2Note.HasValue)
            {
                EmitNoteOff("P2", ref activePulse2Note, lastPulse2Program >= 0 ? lastPulse2Program : ProgramForPulseDuty(pulse2_duty));
            }

            // ---------- Triangle ----------
            // For musical sustain, ignore transient linear counter drops; only gate on length or enable
            if (triangle_enabled && triangle_lengthCounter > 0 && triangle_timer >= 2)
            {
                double freq = 1789773.0 / (32.0 * (triangle_timer + 1));
                int targetNote = FreqToMidi(freq);
                // Apply attenuation factor so triangle no longer always maps to max velocity (was 127)
                int triVel = AmplitudeToVelocity(TriangleGain * TriangleAttenuation, MaxRefGain);
                // Enforce final velocity scaling (after perceptual curve) to ensure ~50% reduction regardless of sqrt shaping
                triVel = (int)Math.Round(triVel * TriangleVelocityScale);
                if (triVel > 0 && triVel < 1) triVel = 1; // guard (probably redundant)
                if (!activeTriangleNote.HasValue)
                {
                    EmitNoteOn("TRI", ref activeTriangleNote, targetNote, triVel, ProgramTriangle, freq);
                }
                else
                {
                    if (baseTriangleFreq > 0)
                    {
                        double semis = 12.0 * Math.Log(freq / baseTriangleFreq, 2.0);
                        if (Math.Abs(semis) > ReTriggerThresholdSemitones)
                        {
                            EmitNoteOn("TRI", ref activeTriangleNote, targetNote, triVel, ProgramTriangle, freq);
                        }
                        else if (Math.Abs(semis) >= MinBendDeltaSemitones)
                        {
                            int bend = (int)Math.Round((semis / PitchBendRangeSemitones) * 8192.0);
                            if (bend < -8192) bend = -8192; if (bend > 8191) bend = 8191;
                            if (bend != lastTrianglePitch) { lastTrianglePitch = bend; PitchEvent?.Invoke("TRI", bend); }
                        }
                    }
                }
            }
            else if (activeTriangleNote.HasValue)
            {
                EmitNoteOff("TRI", ref activeTriangleNote, ProgramTriangle);
            }

            // ---------- Noise (drums) ----------
        if (noise_enabled && noise_lengthCounter > 0)
            {
                bool trigger = noiAttack || noise_lengthCounter > prevNoiseLength || prevNoiseLength == 0;
                if (trigger)
                {
                    int periodIdx = noise_period & 0x0F;
                    int drumNote = periodIdx < 4 ? 35 : (periodIdx < 8 ? 38 : 42);
            int rawVol = noise_constantVolume ? noise_volumeParam : noise_decayLevel;
            double amp = NoiseGain * (rawVol / 15.0) * NoiseAttenuation * NoiseAttenuationFactor; // attenuated + user factor
            int vel = AmplitudeToVelocity(amp, MaxRefGain);
            // Post perceptual scaling for guaranteed 50% cut
            vel = (int)Math.Round(vel * NoiseVelocityScale);
            if (vel > 0) vel = Math.Max(vel - 8, 1); // slight bias down after scaling
                    if (vel < 1) vel = 1;
                    NoteEvent?.Invoke(new NesNoteEvent("NOI", drumNote, vel, true, ProgramNoise));
                    NoteEvent?.Invoke(new NesNoteEvent("NOI", drumNote, 0, false, ProgramNoise));
                }
            }

            // Update previous counters for next frame
            prevPulse1Length = pulse1_lengthCounter;
            prevPulse2Length = pulse2_lengthCounter;
            prevTriangleLength = triangle_lengthCounter;
            prevTriangleLinear = triangle_linearCounter; // kept for potential future use
            prevNoiseLength = noise_lengthCounter;

            // ---------- DPCM (simplified) ----------
            if (dpcm_active && dpcm_remainingCycles <= 0)
            {
                if (activeDpcmNote.HasValue)
                {
                    NoteEvent?.Invoke(new NesNoteEvent("DPCM", activeDpcmNote.Value, 0, false, ProgramDpcm));
                    activeDpcmNote = null;
                }
                if (!dpcm_loop)
                {
                    dpcm_active = false;
                }
                else
                {
                    dpcm_remainingCycles = EstimateDpcmCycles();
                }
            }
            if (dpcm_active && !activeDpcmNote.HasValue)
            {
                int midi = 60 + (dpcm_rateIndex - 8); // center around middle C
                if (midi < 36) midi = 36; if (midi > 84) midi = 84;
                int vel = 30 + (int)Math.Round((dpcm_outputLevel / 127.0) * 90.0); if (vel > 127) vel = 127; if (vel < 1) vel = 1;
                NoteEvent?.Invoke(new NesNoteEvent("DPCM", midi, vel, true, ProgramDpcm));
                activeDpcmNote = midi;
            }
        }

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
            ResetNoteTracking();
        }

        // Step 1 CPU cycle convenience
        public void Step() => Step(1);
        public void Step(int cpuCycles)
        {
            const double CpuFreq = 1789773.0;
            for (int c = 0; c < cpuCycles; c++)
            {
                StepFrameSequencer();
                ClockPulse(ref pulse1_timer, ref pulse1_timerCounter, ref pulse1_sequenceIndex);
                ClockPulse(ref pulse2_timer, ref pulse2_timerCounter, ref pulse2_sequenceIndex);
                ClockTriangle();
                ClockNoise();
                if (dpcm_active && dpcm_remainingCycles > 0) dpcm_remainingCycles--; // countdown
            }
            if (soundFontMode)
            {
                ProcessSoundFontNotes();
                return;
            }
            double samplesFloat = cpuCycles * audioSampleRate / CpuFreq;
            fractionalSampleAccumulator += samplesFloat;
            int samplesToProduce = (int)fractionalSampleAccumulator;
            if (samplesToProduce <= 0) return;
            fractionalSampleAccumulator -= samplesToProduce;
            for (int i = 0; i < samplesToProduce; i++) GenerateAudioSample();
        }

        private void GenerateAudioSample()
        {
            if (ringCount >= AudioRingSize)
            { ringRead = (ringRead + 1) & (AudioRingSize - 1); ringCount--; }
            double p1 = (pulse1_enabled && (status & 0x01) != 0 && pulse1_lengthCounter > 0 && pulse1_timer >= 8) ? pulse1_output : 0.0;
            double p2 = (pulse2_enabled && (status & 0x02) != 0 && pulse2_lengthCounter > 0 && pulse2_timer >= 8) ? pulse2_output : 0.0;
            double t = (triangle_enabled && (status & 0x04) != 0 && triangle_lengthCounter > 0 && triangle_linearCounter > 0 && triangle_timer >= 2) ? triangle_output : 0.0;
            double n = (noise_enabled && (status & 0x08) != 0 && noise_lengthCounter > 0) ? noise_output : 0.0;
            // Apply PCM attenuation scaling (independent of SoundFont note-event path)
            if (t != 0.0) t *= PcmTriangleGainScale;
            if (n != 0.0) n *= PcmNoiseGainScale * NoiseAttenuationFactor;
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

        // ================= NES CONSTANT TABLES & FILTER COEFFICIENTS =================
        // Standard length counter table (NTSC) – values in frames. See NesDev APU docs.
        private static readonly int[] LengthTable = new int[32]
        {
            10, 254, 20,  2, 40,  4, 80,  6,
            160,  8, 60, 10, 14, 12, 26, 14,
            12, 16, 24, 18, 48, 20, 96, 22,
            192, 24, 72, 26, 16, 28, 32, 30
        };

        // Noise channel period table in CPU cycles (NTSC).
        private static readonly int[] NoisePeriods = new int[16]
        { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };

        // DPCM (DMC) rate table in CPU cycles per bit (NTSC).
        private static readonly int[] DpcmRateTable = new int[16]
        { 428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 85, 72, 54 };

        // Simple one-pole low-pass smoothing coefficient used before DC high-pass.
        // Tuned by ear (rough ~4-5 kHz corner at 44.1k) – not cycle accurate, just taming aliasing harshness.
        private const float LowPassCoeff = 0.045f;
    // PCM output per-channel gain scaling (post envelope but pre non-linear mix) for balancing
    private const float PcmTriangleGainScale = 0.50f; // 50% triangle reduction in PCM path
    private const float PcmNoiseGainScale = 0.50f;    // 50% noise reduction in PCM path
    // Runtime-adjustable attenuation for noise channel (applied multiplicatively on top of PcmNoiseGainScale for PCM
    // and integrated into SoundFont velocity mapping). 1.0 = keep current scaling, <1 further reduces loudness.
    public double NoiseAttenuationFactor { get; set; } = 0.5; // start at additional 50% cut

        private long EstimateDpcmCycles()
        {
            if (dpcm_sampleLengthBytes <= 0) return 0;
            int rateCycles = DpcmRateTable[dpcm_rateIndex & 0x0F];
            // Each sample byte encodes 8 delta bits emitted at the selected rate.
            return (long)rateCycles * dpcm_sampleLengthBytes * 8L;
        }

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
                case 0x4010: // DPCM control
                    dpcm_loop = (value & 0x40) != 0; dpcm_rateIndex = value & 0x0F; break; // ignore IRQ flag
                case 0x4011: // DPCM direct load
                    dpcm_outputLevel = (byte)(value & 0x7F); break;
                case 0x4012: // DPCM sample address
                    dpcm_sampleAddress = (ushort)(0xC000 + value * 64); break;
                case 0x4013: // DPCM sample length
                    dpcm_sampleLengthBytes = value * 16 + 1; break;
                case 0x4015:
                    status = value; if ((value & 0x01) == 0) pulse1_enabled = false; if ((value & 0x02) == 0) pulse2_enabled = false; if ((value & 0x04) == 0) triangle_enabled = false; if ((value & 0x08) == 0) noise_enabled = false;
                    bool dEnable = (value & 0x10) != 0;
                    if (dEnable && !dpcm_active && dpcm_sampleLengthBytes > 0)
                    {
                        dpcm_active = true; dpcm_remainingCycles = EstimateDpcmCycles();
                    }
                    else if (!dEnable && dpcm_active)
                    {
                        if (activeDpcmNote.HasValue)
                        {
                            NoteEvent?.Invoke(new NesNoteEvent("DPCM", activeDpcmNote.Value, 0, false, ProgramDpcm));
                            activeDpcmNote = null;
                        }
                        dpcm_active = false; dpcm_remainingCycles = 0;
                    }
                    break;
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
        { ClockEnvelope(ref pulse1_envelopeStart, ref pulse1_envelopeDivider, ref pulse1_decayLevel, pulse1_volumeParam, pulse1_lengthHalt); ClockEnvelope(ref pulse2_envelopeStart, ref pulse2_envelopeDivider, ref pulse2_decayLevel, pulse2_volumeParam, pulse2_lengthHalt); ClockEnvelope(ref noise_envelopeStart, ref noise_envelopeDivider, ref noise_decayLevel, noise_volumeParam, noise_lengthHalt); if (triangle_linearReloadFlag) triangle_linearCounter = triangle_linear & 0x7F; else if (triangle_linearCounter > 0) triangle_linearCounter--; if ((triangle_linear & 0x80) == 0) triangle_linearReloadFlag = false; }
        private void HalfFrameTick()
        { if (!pulse1_lengthHalt && pulse1_lengthCounter > 0) pulse1_lengthCounter--; if (!pulse2_lengthHalt && pulse2_lengthCounter > 0) pulse2_lengthCounter--; if (!triangle_lengthHalt && triangle_lengthCounter > 0) triangle_lengthCounter--; if (!noise_lengthHalt && noise_lengthCounter > 0) noise_lengthCounter--; DoSweep(ref pulse1_timer, pulse1_sweepNegate, pulse1_sweepShift, pulse1_sweepPeriod, ref pulse1_sweepDivider, ref pulse1_sweepReload); DoSweep(ref pulse2_timer, pulse2_sweepNegate, pulse2_sweepShift, pulse2_sweepPeriod, ref pulse2_sweepDivider, ref pulse2_sweepReload); }
        private void DoSweep(ref ushort timer, bool negate, int shift, int period, ref int divider, ref bool reload)
        { if (shift == 0 || period == 0) { if (reload) reload = false; return; } if (reload) { divider = period; reload = false; return; } if (divider > 0) { divider--; } else { divider = period; int change = timer >> shift; int target = negate ? (timer - change - 1) : (timer + change); if (target >= 8 && target <= 0x7FF) timer = (ushort)target; } }
        private void ClockEnvelope(ref bool start, ref int divider, ref int decayLevel, int volumeParam, bool loop)
        { if (start) { start = false; decayLevel = 15; divider = volumeParam + 1; } else { if (divider > 0) divider--; else { divider = volumeParam + 1; if (decayLevel > 0) decayLevel--; else if (loop) decayLevel = 15; } } }
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
