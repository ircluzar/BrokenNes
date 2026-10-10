using System;
using System.Collections.Generic;
using NesEmulator.Mix;
using NesEmulator.Sega;

namespace NesEmulator
{
    /// <summary>
    /// MIX LAB (Sega track C, round 3): a NES APU whose sound comes from the Master System's FM chip, the Yamaha YM2413, with the SN76489 PSG beside it. Built like <see cref="APU_MD"/>: a NES APU core
    /// (<see cref="MixConfig.NesFrontApu"/>, FIX by default) stays in front for $4015 reads, the frame IRQ and the DMC's DMA timing and its own sound is discarded; the NES channels are rebuilt from the register
    /// writes (<see cref="NesChannelModel"/>) and about 960 times a second replayed on the chips:
    /// <list type="bullet">
    /// <item>pulse 1 and 2 become FM channels 1 and 2 of a real <see cref="Ym2413"/>, played with the chosen <see cref="Patch"/> of the <see cref="OpllPatchBank"/>: one of the chip's own 15 instruments (a violin, a guitar, a
    /// synthesizer bass ...) or a designed one in the chip's user instrument. The pitch is the NES note's (the block and F-number are chosen by nearest in cents), the loudness the NES channel's own, set through the
    /// channel's volume register (3 dB a step) from the patch's measured level. The chip's instruments have envelopes of their own (a guitar fades by itself; a violin holds), and the NES's volume writes shape the note on top of them.
    /// One entry of the bank is not FM: <c>PSG square</c> plays the pulses on the PSG's square tones (50% duty, 109 Hz and up).</item>
    /// <item>the triangle becomes FM channel 3 on a sine (the designed sine in the user instrument), at the triangle's fixed loudness; while a designed patch is chosen the user instrument is that patch, and the triangle plays it too.</item>
    /// <item>the noise becomes the PSG's noise channel, as in <see cref="APU_SMS"/> (white and periodic modes, the NES shift rate exact through tone 3, which nothing else uses here).</item>
    /// <item>the DMC has nowhere to go: samples and $4011 writes are dropped and counted (<see cref="DmcStartsDropped"/>, <see cref="DmcDacWritesDropped"/>).</item>
    /// </list>
    /// The chips run on the NES clock: the PSG and the YM2413 from exactly twice the NES CPU clock (the Z80's, 3.58 MHz), the YM2413 making one sample every 72 of those clocks, which is 36 NES cycles, so a note is the pitch the NES
    /// asks for up to the F-number's 9 bits. Its 49.7 kHz output is brought to the host rate by the converter of emu2413 (<see cref="Ym2413Resampler"/>). Discovered by CoreRegistry as APU id "SMSFM". Allocation-free while running.
    /// </summary>
    public sealed class APU_SMSFM : IAPU, IDmcDmaSchedulable
    {
        /// <summary>The PSG's and the YM2413's clock: exactly twice the NES CPU clock.</summary>
        public const double ClockHz = 2 * NesVoiceWriter.NesCpuHz;
        /// <summary>One YM2413 sample is 72 chip clocks, which is 36 NES CPU cycles.</summary>
        public const int CyclesPerFmSample = Ym2413.ClocksPerSample / 2;
        /// <summary>The gain between the chip's output and the bridge's. The chip's instruments are quiet at their loudest volume (a ROM violin or synthesizer bass is 0.7% to 1% of full scale in RMS against a NES pulse's 7.5%), so the FM goes up by this much
        /// and the volume register (3 dB a step, 45 dB in all) brings each instrument down to the level the NES channel has: even the quietest instrument reaches the NES's loudest pulse, and the loudest needs 7 steps of attenuation to do it.</summary>
        public const float FmGain = 9.0f;

        private const int SyncCycles = 1864;   // about 960 times a second, like the other bridges

        /// <summary>The patch every new core starts with (an index into <see cref="OpllPatchBank.Patches"/>); a plugin instance sets <see cref="Patch"/> on its own chip.</summary>
        public static int DefaultPatch = OpllPatchBank.DefaultIndex;
        /// <summary>What a PSG-square note below the PSG's range does (see <see cref="LowNotePolicy"/>): <see cref="LowNotePolicy.OctaveUp"/> for a NES game, <see cref="LowNotePolicy.Drop"/> in the plugin. FM notes reach below any NES note.</summary>
        public static LowNotePolicy DefaultLowNotes = LowNotePolicy.OctaveUp;
        /// <summary>Whether the front NES core is stepped (see <see cref="APU_SMS.DefaultStepFront"/>).</summary>
        public static bool DefaultStepFront = true;

        private readonly IAPU front;
        private readonly Sn76489 psg;
        private readonly Ym2413 fm;
        private readonly Ym2413Resampler resampler;
        private NesChannelModel model = new();
        private int channelMask = 0x1F;
        private int sinceSync;
        private int fmRemainder;               // NES cycles carried toward the next chip sample

        // the FM channels (0 pulse 1, 1 pulse 2, 2 triangle) and what each register holds now (-1: not written yet)
        private sealed class FmChan
        {
            public int Block, Fnum, Inst, Att;          // wanted
            public bool Keyed, Retrigger;               // Retrigger: the instrument under a held note changed, so the key goes up and down again (the envelope belongs to the key-on)
            public int W10 = -1, W20 = -1, W30 = -1;    // last written
            public double Cents, Hz;
            public bool Above;
        }
        private readonly FmChan[] ch = { new(), new(), new() };
        private readonly byte[] userWritten = new byte[8];
        private bool userValid;
        private int patchIndex;
        private OpllPatch patch = OpllPatchBank.Patches[OpllPatchBank.DefaultIndex];
        private OpllPatch triPatch = OpllPatchBank.Patches[OpllPatchBank.SineIndex];
        private bool patchDirty = true;
        private volatile int requestedPatch = NoRequest;
        private const int NoRequest = -2;
        private readonly double[,] pulseDb = new double[4, 16];     // how far the patch's loudest volume step is above each NES pulse level, at C4
        private double triDb;

        // what the PSG is set to now (-1: not written yet)
        private readonly int[] period = { -1, -1, -1 };
        private readonly int[] volume = { -1, -1, -1, -1 };
        private int noiseControl = -1;
        private readonly bool[] outOfRange = new bool[2];

        // the audio out: the PSG's frames and the FM's frames wait in their own rings until both have a sample, then go to the mono ring (allocation-free)
        private readonly short[] pcm = new short[512];
        private const int QMask = (1 << 14) - 1;
        private readonly float[] psgQ = new float[QMask + 1], fmQ = new float[QMask + 1];
        private long psgRead, psgWrite, fmRead, fmWrite;
        private readonly float[] ring = new float[1 << 16];
        private int ringHead, ringCount;
        private readonly float[] drainScratch = new float[2048];

        // ---- what the bridge could not carry over (counted, never silent) ----
        public long Syncs { get; private set; }
        /// <summary>DMC sample starts that went nowhere (the DMC is dropped).</summary>
        public long DmcStartsDropped => model.DmcStarts;
        /// <summary>Writes to the DMC's direct DAC register ($4011) that went nowhere.</summary>
        public long DmcDacWritesDropped { get; private set; }
        /// <summary>Notes the FM chip started (a key-on).</summary>
        public long FmNotes { get; private set; }
        /// <summary>Pulse or triangle notes above the FM chip's top (about 6.2 kHz, 3.1 kHz for an instrument whose carrier is at half the base) that were silenced, counted once per note.</summary>
        public long NotesAboveFmRange { get; private set; }
        /// <summary>Syncs at which a note was quieter than the NES channel's because even the chip's loudest volume step is under it (the patch is too quiet for the level asked).</summary>
        public long TooQuietSyncs { get; private set; }
        /// <summary>PSG-square notes below the PSG's range that were silenced (<see cref="LowNotePolicy.Drop"/>), once per note.</summary>
        public long LowNotesDropped { get; private set; }
        /// <summary>PSG-square notes below the PSG's range that played whole octaves up (<see cref="LowNotePolicy.OctaveUp"/>), once per note.</summary>
        public long LowNotesTransposed { get; private set; }
        public LowNotePolicy LowNotes { get; set; } = DefaultLowNotes;
        public bool StepFront { get; set; } = DefaultStepFront;

        /// <summary>The YM2413 the FM voices come from (its registers, for probes and tests; do not write to it).</summary>
        public Ym2413 Fm => fm;
        /// <summary>The PSG (noise, and the tones of the PSG-square patch).</summary>
        public Sn76489 Psg => psg;
        public IAPU Front => front;
        /// <summary>The frequency the carrier is set to play on FM channel <paramref name="channel"/> (0 pulse 1, 1 pulse 2, 2 triangle) as a note (the base frequency times the instrument's fundamental), 0 when it is not keyed.</summary>
        public double FmHz(int channel) => ch[channel].Keyed ? ch[channel].Hz : 0;
        /// <summary>How far that frequency is from the NES note's, in cents (the rounding of the 9-bit F-number).</summary>
        public double FmCents(int channel) => ch[channel].Cents;
        public bool FmKeyed(int channel) => ch[channel].Keyed;
        /// <summary>The volume register (0 loudest, 15 quietest, 3 dB a step) the channel is set to, -1 before its first note.</summary>
        public int FmAttenuation(int channel) => ch[channel].Att;
        /// <summary>The instrument number (0 user, 1 to 15 ROM) the channel plays.</summary>
        public int FmInstrument(int channel) => ch[channel].Inst;

        /// <summary>The patch pulses 1 and 2 play (an index into <see cref="OpllPatchBank.Patches"/>; out of range is the last). Takes effect at the next sync, held notes included; may be set from any thread.</summary>
        public int Patch
        {
            get { int r = requestedPatch; return r != NoRequest ? r : patchIndex; }
            set => requestedPatch = OpllPatchBank.Clamp(value);
        }

        public APU_SMSFM(Bus bus)
        {
            string frontId = MixConfig.NesFrontApu;
            if (frontId.Equals("SMSFM", StringComparison.OrdinalIgnoreCase) || frontId.Equals("MD", StringComparison.OrdinalIgnoreCase) || frontId.Equals("SMS", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("APU_SMSFM cannot be its own front APU");
            var t = CoreRegistry.ApuTypes.TryGetValue(frontId, out var ft) ? ft : throw new ArgumentException($"No NES APU '{frontId}'");
            front = CoreRegistry.CreateInstance<IAPU>(t, bus) ?? throw new InvalidOperationException("front APU");
            int rate = front.GetSampleRate();
            psg = new Sn76489(ClockHz, rate, PsgVariant.Sega) { Gain = APU_SMS.PsgGain };
            fm = new Ym2413(ClockHz);
            resampler = new Ym2413Resampler(ClockHz / Ym2413.ClocksPerSample, rate);
            patchIndex = OpllPatchBank.Clamp(DefaultPatch); patch = OpllPatchBank.Patches[patchIndex]; patchDirty = true;
            PowerOn();
        }

        public string CoreName => $"SMSFM:YM2413+SN76489+{front.CoreName}";
        public string Description => "MIX LAB: NES channels played on the Master System's FM chip (pulses -> YM2413 channels with a chosen instrument, triangle -> an FM sine, noise -> the PSG noise; the DMC is dropped and counted)";
        public int Performance => 0; public int Rating => 1; public string Category => "Experimental";

        private void PowerOn()
        {
            psg.Reset();
            fm.Reset(); fm.ResetPatch(); resampler.Reset();
            fmRemainder = 0;
            psgRead = psgWrite = fmRead = fmWrite = 0;
            Array.Fill(period, -1); Array.Fill(volume, -1); noiseControl = -1; Array.Clear(outOfRange);
            userValid = false;
            foreach (var c in ch) { c.Block = c.Fnum = 0; c.Inst = 0; c.Att = -1; c.Keyed = false; c.Cents = 0; c.Hz = 0; c.Above = false; c.W10 = c.W20 = c.W30 = -1; }
            for (int c = 0; c < 4; c++) SetVolume(c, 15);
            SetNoise(0);
            patchDirty = true;
        }

        // ---- the PSG's registers (only changes are written) ----

        private void SetPeriod(int channel, int n)
        {
            if (period[channel] == n) return;
            period[channel] = n;
            psg.Write((byte)(0x80 | channel << 5 | (n & 0x0F)));
            psg.Write((byte)((n >> 4) & 0x3F));
        }

        private void SetVolume(int channel, int k)
        {
            if (volume[channel] == k) return;
            volume[channel] = k;
            psg.Write((byte)(0x90 | channel << 5 | k));
        }

        private void SetNoise(int control)
        {
            if (noiseControl == control) return;
            noiseControl = control;
            psg.Write((byte)(0xE0 | control));
        }

        // ---- the YM2413's registers ----

        /// <summary>Loads the user instrument (registers $00 to $07) when it differs from what the chip has.</summary>
        private bool LoadUser(byte[] bytes)
        {
            bool changed = false;
            for (int r = 0; r < 8; r++)
                if (!userValid || userWritten[r] != bytes[r]) { fm.WriteRegister(r, bytes[r]); userWritten[r] = bytes[r]; changed = true; }
            userValid = true;
            return changed;
        }

        /// <summary>Writes a channel's registers that differ from what is on the chip: the volume and instrument first, then the frequency, then the key, so a new note starts at the right level and pitch.</summary>
        private void Flush(int c)
        {
            var q = ch[c];
            int r30 = (q.Inst << 4) | (q.Att < 0 ? 15 : q.Att);
            int r10 = q.Fnum & 0xFF;
            int r20 = (q.Keyed ? 0x10 : 0) | (q.Block << 1) | (q.Fnum >> 8);
            if (r30 != q.W30) { fm.WriteRegister(0x30 + c, (byte)r30); q.W30 = r30; }
            if (r10 != q.W10) { fm.WriteRegister(0x10 + c, (byte)r10); q.W10 = r10; }
            if (r20 != q.W20)
            {
                bool wasKeyed = q.W20 >= 0 && (q.W20 & 0x10) != 0;
                fm.WriteRegister(0x20 + c, (byte)r20); q.W20 = r20;
                if (q.Keyed && !wasKeyed) FmNotes++;
            }
        }

        // ---- the replay ----

        private int Fit(int channel, int n, bool playing)
        {
            if (!playing) { outOfRange[channel] = false; return 0; }
            if (n <= 1023) { outOfRange[channel] = false; return n; }
            if (!outOfRange[channel]) { if (LowNotes == LowNotePolicy.Drop) LowNotesDropped++; else LowNotesTransposed++; }
            outOfRange[channel] = true;
            if (LowNotes == LowNotePolicy.Drop) return 0;
            while (n > 1023) n = (n + 1) >> 1;
            return n;
        }

        private void BuildLevels()
        {
            for (int d = 0; d < 4; d++)
                for (int v = 0; v < 16; v++) pulseDb[d, v] = patch.ExcessDb(APU_MD.PulseRms(d, v), FmGain);
            triDb = triPatch.ExcessDb(APU_MD.TriangleRms, FmGain);
        }

        private void Sync()
        {
            Syncs++;
            var m = model;
            int asked = requestedPatch;
            if (asked != NoRequest)
            {
                requestedPatch = NoRequest;
                if (asked != patchIndex || patchDirty) { patchIndex = asked; patch = OpllPatchBank.Patches[asked]; patchDirty = true; }
            }
            if (patchDirty)
            {
                patchDirty = false;
                // the chip has one user instrument: a designed patch loads it (and the triangle plays it too); a ROM instrument or the PSG entry leaves it to the designed sine, the triangle's voice
                triPatch = patch.User != null ? patch : OpllPatchBank.Patches[OpllPatchBank.SineIndex];
                bool userChanged = LoadUser(triPatch.User!);
                BuildLevels();
                // the envelope belongs to the key-on, so a note held under the old instrument is keyed again to hear the new one
                int pulseInst = patch.Psg ? 0 : patch.Rom;
                for (int c = 0; c < 2; c++)
                {
                    if (ch[c].Keyed && (ch[c].Inst != pulseInst || (userChanged && pulseInst == 0))) ch[c].Retrigger = true;
                    ch[c].Inst = pulseInst;
                }
                if (userChanged && ch[2].Keyed) ch[2].Retrigger = true;
                ch[2].Inst = 0;
                if (patch.Psg) { ch[0].Keyed = ch[1].Keyed = false; ch[0].Retrigger = ch[1].Retrigger = false; Flush(0); Flush(1); }
            }

            for (int i = 0; i < 2; i++)
            {
                var p = m.P[i];
                bool playing = p.Audible(i == 0) && (channelMask & (1 << i)) != 0 && p.Volume > 0;
                double hz = NesVoiceWriter.NesCpuHz / (16.0 * (p.Timer + 1));
                if (patch.Psg)
                {
                    FmVoice(i, patch, false, hz, double.NegativeInfinity);
                    int n = Fit(i, p.Timer + 1, playing);
                    if (n > 0 && !PitchGuard.Blocks(ClockHz / (32.0 * n)))
                    {
                        SetPeriod(i, n);
                        SetVolume(i, APU_SMS.PsgPulseVolume(p.Duty & 3, p.Volume));
                    }
                    else SetVolume(i, 15);
                }
                else
                {
                    SetVolume(i, 15);
                    FmVoice(i, patch, playing, hz, pulseDb[p.Duty & 3, p.Volume]);
                }
            }

            // the triangle: always an FM sine (or the designed patch while one is chosen)
            {
                bool playing = m.TriAudible && (channelMask & 4) != 0;
                FmVoice(2, triPatch, playing, NesVoiceWriter.NesCpuHz / (32.0 * (m.TriTimer + 1)), triDb);
            }

            bool noiseOn = m.NoiseAudible && (channelMask & 8) != 0 && m.NoiseVolume > 0;
            if (noiseOn)
            {
                double shift = NesVoiceWriter.NesCpuHz / NesChannelModel.NoisePeriods[m.NoisePeriodIdx];
                if (m.NoiseShort) shift *= 16.0 / 93.0;
                int fb = m.NoiseShort ? 0 : 4;
                int rate = 0; double bd = double.MaxValue;
                for (int r = 0; r < 3; r++)
                {
                    double hzr = ClockHz / (512.0 * (1 << r));
                    double d = Math.Abs(Math.Log(hzr / shift));
                    if (d < bd) { bd = d; rate = r; }
                }
                if (bd > 0.03)
                {
                    int p3 = Math.Clamp((int)Math.Round(ClockHz / (32.0 * shift)), 1, 1023);
                    SetPeriod(2, p3);
                    SetVolume(2, 15);
                    rate = 3;
                }
                SetNoise(fb | rate);
                SetVolume(3, APU_SMS.PsgNoiseVolume(m.NoiseVolume));
            }
            else SetVolume(3, 15);

            Drain();
        }

        /// <summary>One FM voice for one sync: keyed on at the NES note's pitch and loudness while it plays, keyed off when it does not (or when the chip cannot reach the pitch: counted once per note).</summary>
        private void FmVoice(int c, OpllPatch p, bool playing, double hz, double excessDb)
        {
            var q = ch[c];
            if (!playing || PitchGuard.Blocks(hz)) { q.Above = false; q.Keyed = false; Flush(c); return; }
            if (!OpllPatchBank.TryPitch(hz / p.Fundamental, ClockHz, out int block, out int fnum, out double cents))
            {
                if (!q.Above) NotesAboveFmRange++;
                q.Above = true;
                q.Keyed = false; Flush(c);
                return;
            }
            q.Above = false;
            q.Block = block; q.Fnum = fnum; q.Cents = cents;
            q.Hz = OpllPatchBank.Hz(block, fnum, ClockHz) * p.Fundamental;
            q.Att = p.AttenuationAt(excessDb, block, fnum);
            if (!p.Reaches(excessDb, block, fnum)) TooQuietSyncs++;
            if (q.Retrigger)
            {
                q.Retrigger = false;
                if (q.W20 >= 0 && (q.W20 & 0x10) != 0) { q.Keyed = false; Flush(c); }      // key up, then down again below: the chip starts the new instrument's envelope
            }
            q.Keyed = true;
            Flush(c);
        }

        /// <summary>Runs the chip one sample and hands the converted output to the FM ring.</summary>
        private void FmTick()
        {
            int s = fm.Calc();
            resampler.Push((short)Math.Clamp(s, short.MinValue, short.MaxValue));
            while (resampler.TryRead(out short o))
            {
                fmQ[fmWrite & QMask] = o * (FmGain / 32768f);
                fmWrite++;
            }
        }

        /// <summary>Moves the finished samples of both chips into the mono ring (the PSG's two sides averaged, added to the FM's centre).</summary>
        private void Drain()
        {
            int n;
            while ((n = psg.ReadSamples(pcm)) > 0)
            {
                int frames = n / 2;
                for (int i = 0; i < frames; i++) { psgQ[psgWrite & QMask] = (pcm[2 * i] + pcm[2 * i + 1]) * (0.5f / 32768f); psgWrite++; }
            }
            if (psgWrite - psgRead > QMask) psgRead = psgWrite - QMask;
            if (fmWrite - fmRead > QMask) fmRead = fmWrite - QMask;
            long pairs = Math.Min(psgWrite - psgRead, fmWrite - fmRead);
            for (long i = 0; i < pairs; i++)
            {
                float v = psgQ[(psgRead + i) & QMask] + fmQ[(fmRead + i) & QMask];
                if (ringCount == ring.Length) { ringHead = (ringHead + 1) & (ring.Length - 1); ringCount--; }
                ring[(ringHead + ringCount) & (ring.Length - 1)] = v;
                ringCount++;
            }
            psgRead += pairs; fmRead += pairs;
        }

        // ---- IAPU ----

        public void Step(int cpuCycles)
        {
            if (cpuCycles <= 0) return;
            if (StepFront) front.Step(cpuCycles);
            model.ClockBulk(cpuCycles);
            psg.Run(cpuCycles * 2);
            fmRemainder += cpuCycles;
            while (fmRemainder >= CyclesPerFmSample) { fmRemainder -= CyclesPerFmSample; FmTick(); }
            if ((sinceSync += cpuCycles) >= SyncCycles) { sinceSync = 0; Sync(); }
            if (front.GetQueuedSampleCount() > 4096) { while (front.ReadSamples(drainScratch) > 0) { } }
        }

        public void WriteAPURegister(ushort address, byte value)
        {
            if (address == 0x4011) DmcDacWritesDropped++;
            model.Write(address, value);
            front.WriteAPURegister(address, value);
        }

        public byte ReadAPURegister(ushort address) => front.ReadAPURegister(address);

        public int CyclesUntilDmcFetch() => (front as IDmcDmaSchedulable)?.CyclesUntilDmcFetch() ?? int.MaxValue;

        public float[] GetAudioSamples(int maxSamples = 0)
        {
            Drain();
            int n = maxSamples > 0 ? Math.Min(maxSamples, ringCount) : ringCount;
            var r = new float[n];
            ReadSamples(r);
            return r;
        }

        public int ReadSamples(Span<float> dest)
        {
            Drain();
            int n = Math.Min(dest.Length, ringCount);
            for (int i = 0; i < n; i++) dest[i] = ring[(ringHead + i) & (ring.Length - 1)];
            ringHead = (ringHead + n) & (ring.Length - 1);
            ringCount -= n;
            return n;
        }

        public int GetQueuedSampleCount() { Drain(); return ringCount; }
        public int GetSampleRate() => psg.SampleRate;
        public void SetEnabledChannels(int mask) { channelMask = mask; front.SetEnabledChannels(mask); }
        public object GetState() => front.GetState();
        public void SetState(object state) { front.SetState(state); ResyncModel(); }

        /// <summary>After a state load the model is rebuilt from the front core's state probe (see <see cref="APU_MD"/>'s equivalent), so the chips play what the state was playing.</summary>
        private void ResyncModel()
        {
            if (front is not IApuStateProbe probe) return;
            var s = probe.ProbeApuState();
            var m = new NesChannelModel();
            m.Write(0x4017, (byte)(s.FrameMode5 ? 0x80 : 0));
            void Pulse(NesChannelModel.Pulse p, ushort timer, byte volume, byte length, byte duty, bool enabled, bool halt, bool constant, bool sweepOn, bool negate, byte shift)
            {
                p.Timer = timer; p.Duty = duty; p.Len = length; p.Enabled = enabled; p.Halt = halt; p.Const = constant;
                p.Vol = volume; p.EnvDecay = volume; p.SweepOn = sweepOn; p.SweepNeg = negate; p.SweepShift = shift;
            }
            Pulse(m.P[0], s.Pulse1Period, s.Pulse1Volume, s.Pulse1Length, s.Pulse1Duty, s.Pulse1Enabled, s.Pulse1Halt, s.Pulse1ConstVol, s.Pulse1SweepEnabled, s.Pulse1SweepNegate, s.Pulse1SweepShift);
            Pulse(m.P[1], s.Pulse2Period, s.Pulse2Volume, s.Pulse2Length, s.Pulse2Duty, s.Pulse2Enabled, s.Pulse2Halt, s.Pulse2ConstVol, s.Pulse2SweepEnabled, s.Pulse2SweepNegate, s.Pulse2SweepShift);
            m.TriTimer = s.TrianglePeriod; m.TriLen = s.TriangleLength; m.TriLinear = s.TriangleLinear; m.TriControl = s.TriangleHalt; m.TriEnabled = s.TriangleEnabled;
            m.NoisePeriodIdx = Math.Max(0, Array.IndexOf(NesChannelModel.NoisePeriods, (int)s.NoisePeriod));
            m.NoiseVol = s.NoiseVolume; m.NoiseEnvDecay = s.NoiseVolume; m.NoiseLen = s.NoiseLength; m.NoiseHalt = s.NoiseHalt; m.NoiseConst = s.NoiseConstVol; m.NoiseShort = s.NoiseMode; m.NoiseEnabled = s.NoiseEnabled;
            m.DmcStarts = model.DmcStarts;
            model = m;
        }

        public void ClearAudioBuffers() { Drain(); ringHead = ringCount = 0; front.ClearAudioBuffers(); }

        public void Reset()
        {
            front.Reset();
            model = new NesChannelModel();
            sinceSync = 0;
            ringHead = ringCount = 0;
            PowerOn();
        }

        /// <summary>One line for probes and logs: the patch, what the chips are playing and what the bridge dropped.</summary>
        public string Describe() =>
            $"{CoreName}: patch {patchIndex} '{patch.Name}', FM {(ch[0].Keyed ? ch[0].Hz.ToString("F1") : "-")}/{(ch[1].Keyed ? ch[1].Hz.ToString("F1") : "-")}/{(ch[2].Keyed ? ch[2].Hz.ToString("F1") : "-")} Hz, PSG tones {Math.Max(0, period[0])}/{Math.Max(0, period[1])}/{Math.Max(0, period[2])} volumes {volume[0]}/{volume[1]}/{volume[2]}/{volume[3]} noise ${Math.Max(0, noiseControl):X1} | " +
            $"dropped: DMC starts {DmcStartsDropped}, $4011 writes {DmcDacWritesDropped}, notes above the FM range {NotesAboveFmRange}, too-quiet syncs {TooQuietSyncs}, PSG-square low notes silenced {LowNotesDropped} / transposed up {LowNotesTransposed}; FM notes {FmNotes}; syncs {Syncs}";
    }
}
