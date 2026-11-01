using System;
using System.IO;
using NesEmulator.Mix;
using NesEmulator.Sega;

namespace NesEmulator
{
    /// <summary>
    /// MIX LAB (Sega track C, milestone 2): a NES APU whose sound comes from the Genesis / Mega Drive sound chips, so a NES game can be heard on a YM2612 and an SN76489. Built like
    /// <see cref="APU_SMS"/>: a NES APU core (<see cref="MixConfig.NesFrontApu"/>, FIX by default) stays in front for $4015 reads, the frame IRQ and the DMC's DMA timing and its own sound is
    /// discarded; the NES channels are rebuilt from the register writes (<see cref="NesChannelModel"/>) and about 960 times a second replayed on the chips:
    /// <list type="bullet">
    /// <item>pulse 1 and 2 become FM voices 1 and 2 of a real <see cref="Ym2612"/>, played with the chosen <see cref="Patch"/> of the built-in <see cref="FmPatchBank"/> (a synth brass, a bell, a bass...): the
    /// pitch is the NES note's (the frequency registers are chosen by nearest in cents), the loudness the NES channel's own (a thinner duty is quieter), set through the carriers' total level in 0.75 dB steps.
    /// The patch holds its carriers' level, so the NES's own envelope and volume writes shape the note. One patch is not FM: <c>PSG square</c> plays the pulses on the PSG's square tones instead (the NES pulse's own 50% sound,
    /// 109 Hz and up, notes below that follow <see cref="LowNotes"/>).</item>
    /// <item>the triangle becomes FM voice 3 on a sine (one carrier, algorithm 7), at the triangle's fixed loudness.</item>
    /// <item>the noise becomes the PSG's noise channel, as in <see cref="APU_SMS"/> (white and periodic modes, the NES shift rate exact through tone 3, which nothing else uses here).</item>
    /// <item>the DMC has nowhere to go (the YM2612's DAC could carry it; not built yet): samples and $4011 writes are dropped and counted (<see cref="DmcStartsDropped"/>, <see cref="DmcDacWritesDropped"/>).</item>
    /// </list>
    /// The chips run on the NES clock: the PSG at exactly twice the NES CPU clock and the YM2612 at 30/7 of it (Genesis master = 30 NES CPU clocks, the YM2612 takes master/7), both in whole-number steps, so a note
    /// is the pitch the NES asks for. The FM chip runs at its own 53,267 Hz and is resampled to the host rate by the same band-limited mixer the Genesis sound unit uses. Discovered by CoreRegistry as APU id "MD".
    /// Allocation-free while running (the plugin plays it on the audio thread).
    /// </summary>
    public sealed class APU_MD : IAPU, IDmcDmaSchedulable
    {
        /// <summary>The PSG runs from exactly twice the NES CPU clock.</summary>
        public const double PsgClockHz = 2 * NesVoiceWriter.NesCpuHz;
        /// <summary>The YM2612's input clock: master / 7 where the master clock is 30 NES CPU clocks, so 30/7 per NES cycle.</summary>
        public const double YmClockHz = NesVoiceWriter.NesCpuHz * 30.0 / 7.0;

        private const int SyncCycles = 1864;   // about 960 times a second, like the other bridges

        // ---- loudness: each NES channel is replayed at the loudness (AC RMS) the NES mixer gives it, as a fraction of full scale like the FM chip's own output ----
        /// <summary>AC RMS of a NES pulse with this duty (0 to 3) and volume (0 to 15) on the NES mixer's scale.</summary>
        public static double PulseRms(int duty, int volume) => APU_SMS.PulseOut(volume) * APU_SMS.DutyRms[duty & 3];
        /// <summary>AC RMS of the NES triangle (fixed: it has no volume): peak to peak over 2 sqrt 3.</summary>
        public static readonly double TriangleRms = APU_SMS.TriangleOut / (2 * Math.Sqrt(3));

        /// <summary>The patch every new core starts with (an index into <see cref="FmPatchBank.Patches"/>); the desktop's setting and the plugin's first instance use it, a plugin instance then sets <see cref="Patch"/> on its own chip.</summary>
        public static int DefaultPatch = FmPatchBank.DefaultIndex;
        /// <summary>What a PSG-square note below the PSG's range does (see <see cref="LowNotePolicy"/>): <see cref="LowNotePolicy.OctaveUp"/> for a NES game, <see cref="LowNotePolicy.Drop"/> in the plugin. FM notes never go below any NES note.</summary>
        public static LowNotePolicy DefaultLowNotes = LowNotePolicy.OctaveUp;
        /// <summary>Whether the front NES core is stepped (see <see cref="APU_SMS.DefaultStepFront"/>: a host that never reads the chip back turns it off to save time).</summary>
        public static bool DefaultStepFront = true;
        /// <summary>The YM2612's board model for new cores: null takes the process default (<see cref="SegaSettings"/>, Model 1 unless chosen), the discrete chip's ladder effect included.</summary>
        public static GenesisModel? DefaultModel;

        private readonly IAPU front;
        private readonly Sn76489 psg;
        private readonly Ym2612 ym;
        private readonly BandLimitedMixer fmLeft, fmRight;
        private readonly FmSink sink;
        private NesChannelModel model = new();
        private int channelMask = 0x1F;
        private int sinceSync;
        private long ymClock;          // chip clocks run so far
        private int ymRemainder;       // thirtieths... sevenths of a chip clock carried (30 per NES cycle, 7 per chip clock)
        private long fmSamples;        // FM samples handed to the mixers

        // the FM channels (voices 1 to 3 of part 1) and what each is set to now (-1 / false: not written yet)
        private sealed class FmChan { public int Block = -1, Fnum = -1, Att = -1; public bool Keyed; public double Cents; public double Hz; }
        private readonly FmChan[] fm = { new(), new(), new() };
        private int patchIndex;
        private FmPatch patch = FmPatchBank.Patches[FmPatchBank.DefaultIndex];
        private bool patchDirty = true;
        private volatile int requestedPatch = NoRequest;     // a patch asked for from any thread, taken up at the next sync
        private const int NoRequest = -2;
        private readonly int[,] pulseAtt = new int[4, 16];     // carrier attenuation (total-level steps) for each pulse duty and volume under the current patch
        private int triAtt;

        // what the PSG is set to now (-1: not written yet)
        private readonly int[] period = { -1, -1, -1 };
        private readonly int[] volume = { -1, -1, -1, -1 };
        private int noiseControl = -1;
        private readonly bool[] outOfRange = new bool[2];

        // the audio out: mono floats, drained from the chips at every sync (allocation-free)
        private readonly short[] pcm = new short[512];
        private readonly float[] fl = new float[256], fr = new float[256];
        private readonly float[] ring = new float[1 << 16];
        private int ringHead, ringCount;
        private readonly float[] drainScratch = new float[2048];

        // ---- what the bridge could not carry over (counted, never silent) ----
        /// <summary>Syncs the bridge has made (about 960 a second).</summary>
        public long Syncs { get; private set; }
        /// <summary>DMC sample starts that went nowhere (the DMC is dropped).</summary>
        public long DmcStartsDropped => model.DmcStarts;
        /// <summary>Writes to the DMC's direct DAC register ($4011) that went nowhere.</summary>
        public long DmcDacWritesDropped { get; private set; }
        /// <summary>Notes the FM chip started (a key-on).</summary>
        public long FmNotes { get; private set; }
        /// <summary>Pulse or triangle notes above the FM chip's top (about 6.6 kHz) that were silenced, counted once per note.</summary>
        public long NotesAboveFmRange { get; private set; }
        /// <summary>PSG-square notes below the PSG's range that were silenced (<see cref="LowNotePolicy.Drop"/>), once per note.</summary>
        public long LowNotesDropped { get; private set; }
        /// <summary>PSG-square notes below the PSG's range that played whole octaves up (<see cref="LowNotePolicy.OctaveUp"/>), once per note.</summary>
        public long LowNotesTransposed { get; private set; }
        /// <summary>What happens to a PSG-square note below the PSG's range. <see cref="DefaultLowNotes"/> when the core is made.</summary>
        public LowNotePolicy LowNotes { get; set; } = DefaultLowNotes;
        /// <summary>Whether the front NES core is stepped; <see cref="DefaultStepFront"/> when the core is made.</summary>
        public bool StepFront { get; set; } = DefaultStepFront;
        private bool aboveRange0, aboveRange1, aboveRange2;

        /// <summary>The YM2612 the FM voices come from (its registers, for probes and tests; do not write to it).</summary>
        public Ym2612 Fm => ym;
        /// <summary>The PSG (noise, and the tones of the PSG-square patch).</summary>
        public Sn76489 Psg => psg;
        /// <summary>The NES APU core kept in front.</summary>
        public IAPU Front => front;
        /// <summary>The frequency the FM chip is set to play on voice <paramref name="channel"/> (0 pulse 1, 1 pulse 2, 2 triangle), 0 when it is not keyed.</summary>
        public double FmHz(int channel) => fm[channel].Keyed ? fm[channel].Hz : 0;
        /// <summary>How far that frequency is from the NES note's, in cents (the rounding of the chip's frequency registers).</summary>
        public double FmCents(int channel) => fm[channel].Cents;
        public bool FmKeyed(int channel) => fm[channel].Keyed;
        /// <summary>The carrier attenuation (total-level steps added to the patch's own) voice <paramref name="channel"/> is set to, -1 before its first note.</summary>
        public int FmAttenuation(int channel) => fm[channel].Att;

        /// <summary>The patch pulses 1 and 2 play (an index into <see cref="FmPatchBank.Patches"/>; out of range is the last). Takes effect at the next sync, held notes included; may be set from any thread (the desktop's panel does).</summary>
        public int Patch
        {
            get { int r = requestedPatch; return r != NoRequest ? r : patchIndex; }
            set => requestedPatch = FmPatchBank.Clamp(value);
        }

        /// <summary>Every live core, so a setting made in the app's UI reaches the cores already running (weakly held: a core that is gone is forgotten).</summary>
        private static readonly System.Collections.Generic.List<WeakReference<APU_MD>> Live = new();

        /// <summary>The patch every new core starts on and every running core switches to: the desktop's "Genesis FM patch" setting, which Lite's picker shares.</summary>
        public static void SetPatchEverywhere(int index)
        {
            index = FmPatchBank.Clamp(index);
            DefaultPatch = index;
            lock (Live)
            {
                for (int i = Live.Count - 1; i >= 0; i--)
                    if (Live[i].TryGetTarget(out var core)) core.Patch = index; else Live.RemoveAt(i);
            }
        }

        /// <summary>For the verifier and the patch lab: plays a patch that is not in the bank (a test patch with chosen operators). <see cref="Patch"/> reads -1 until a bank patch is chosen again.</summary>
        internal void UseCustomPatch(FmPatch custom) { requestedPatch = NoRequest; patchIndex = -1; patch = custom; patchDirty = true; }

        public APU_MD(Bus bus)
        {
            string frontId = MixConfig.NesFrontApu;
            if (frontId.Equals("MD", StringComparison.OrdinalIgnoreCase) || frontId.Equals("SMS", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("APU_MD cannot be its own front APU");
            var t = CoreRegistry.ApuTypes.TryGetValue(frontId, out var ft) ? ft : throw new ArgumentException($"No NES APU '{frontId}'");
            front = CoreRegistry.CreateInstance<IAPU>(t, bus) ?? throw new InvalidOperationException("front APU");
            int rate = front.GetSampleRate();
            psg = new Sn76489(PsgClockHz, rate, PsgVariant.MegaDrive) { Gain = APU_SMS.PsgGain };
            var m = DefaultModel ?? SegaSettings.ResolveGenesisModel(null, Array.Empty<byte>());
            sink = new FmSink(this);
            ym = new Ym2612(m, YmClockHz) { Sink = sink };
            fmLeft = new BandLimitedMixer(YmClockHz / Ym2612.ClocksPerSample, rate);
            fmRight = new BandLimitedMixer(YmClockHz / Ym2612.ClocksPerSample, rate);
            patchIndex = FmPatchBank.Clamp(DefaultPatch); patch = FmPatchBank.Patches[patchIndex]; patchDirty = true;
            PowerOn();
            lock (Live) { if (Live.Count >= 32) Live.RemoveAll(w => !w.TryGetTarget(out _)); Live.Add(new WeakReference<APU_MD>(this)); }
        }

        public string CoreName => $"MD:YM2612+SN76489+{front.CoreName}";
        public string Description => "MIX LAB: NES channels played on Genesis sound chips (pulses -> YM2612 FM voices with a chosen patch, triangle -> an FM sine, noise -> the PSG noise; the DMC is dropped and counted)";
        public int Performance => 0; public int Rating => 1; public string Category => "Experimental";

        /// <summary>Receives the FM chip's samples and feeds the band-limited mixers (one step of the held level per sample, as the Genesis sound unit does).</summary>
        private sealed class FmSink : IYm2612Sink
        {
            private readonly APU_MD owner;
            private float lastL, lastR;
            public FmSink(APU_MD owner) { this.owner = owner; }
            public void Reset() { lastL = lastR = 0; }
            public void Sample(int left, int right)
            {
                float l = left * (1f / 32768f), r = right * (1f / 32768f);
                owner.fmSamples++;
                if (l != lastL) { owner.fmLeft.Step(owner.fmSamples, l - lastL); lastL = l; }
                if (r != lastR) { owner.fmRight.Step(owner.fmSamples, r - lastR); lastR = r; }
            }
        }

        private void PowerOn()
        {
            psg.Reset();
            ym.PowerOn();
            sink.Reset();
            ymClock = 0; ymRemainder = 0;      // the chip's own clock starts again; the mixers' sample count keeps counting (as the Genesis sound unit's does across a reset)
            fmLeft.Reset(); fmRight.Reset();
            PrimeRing();
            Array.Fill(period, -1); Array.Fill(volume, -1); noiseControl = -1; Array.Clear(outOfRange);
            aboveRange0 = aboveRange1 = aboveRange2 = false;
            foreach (var c in fm) { c.Block = c.Fnum = c.Att = -1; c.Keyed = false; c.Cents = 0; c.Hz = 0; }
            for (int c = 0; c < 4; c++) SetVolume(c, 15);
            SetNoise(0);
            LoadChannel(2, FmPatchBank.Patches[FmPatchBank.SineIndex]);
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
            psg.Write((byte)(0xE0 | control));   // reloads the noise shift register: only done when the mode or rate changes
        }

        // ---- the YM2612's registers (voices 1 to 3 are channels 0 to 2 of part 1: address port 0, data port 1) ----

        private void YmReg(int reg, int value)
        {
            ym.Write(0, (byte)reg, ymClock);
            ym.Write(1, (byte)value, ymClock);
        }

        /// <summary>Programs a channel with a patch: every operator's registers, the algorithm and feedback, both speakers on, no LFO. The key state is left alone; the carriers' attenuation is set by <see cref="FmLevel"/>.</summary>
        private void LoadChannel(int ch, FmPatch p)
        {
            for (int n = 0; n < 4; n++)
            {
                var o = p.Operators[n];
                int off = ch + FmPatchBank.SlotOffset(n);
                YmReg(0x30 + off, o.Dt << 4 | o.Mul);
                YmReg(0x40 + off, o.Tl);
                YmReg(0x50 + off, o.Rs << 6 | o.Ar);
                YmReg(0x60 + off, o.D1r);
                YmReg(0x70 + off, o.D2r);
                YmReg(0x80 + off, o.Sl << 4 | o.Rr);
                YmReg(0x90 + off, 0);
            }
            YmReg(0xB0 + ch, p.Feedback << 3 | p.Algorithm);
            YmReg(0xB4 + ch, 0xC0);
            fm[ch].Att = -1;       // the carriers' total levels were just rewritten at their own values: the level is set again
        }

        private void FmKey(int ch, bool on)
        {
            var c = fm[ch];
            if (c.Keyed == on) return;
            c.Keyed = on;
            YmReg(0x28, (on ? 0xF0 : 0x00) | ch);
            if (on) FmNotes++;
        }

        private void FmPitch(int ch, int block, int fnum, double cents, double hz)
        {
            var c = fm[ch];
            c.Cents = cents; c.Hz = hz;
            if (c.Block == block && c.Fnum == fnum) return;
            c.Block = block; c.Fnum = fnum;
            YmReg(0xA4 + ch, block << 3 | fnum >> 8);   // the high byte only latches; the low byte applies both
            YmReg(0xA0 + ch, fnum & 0xFF);
        }

        /// <summary>Sets the carriers' total level to the patch's own plus <paramref name="att"/> steps.</summary>
        private void FmLevel(int ch, FmPatch p, int att)
        {
            var c = fm[ch];
            if (c.Att == att) return;
            c.Att = att;
            for (int n = 0; n < 4; n++)
                if ((p.CarrierMask >> n & 1) != 0) YmReg(0x40 + ch + FmPatchBank.SlotOffset(n), Math.Min(127, p.Operators[n].Tl + att));
        }

        /// <summary>The carriers' attenuation for each duty and volume under the current patch (rebuilt when the patch changes), and the triangle's under the sine.</summary>
        private void BuildLevels()
        {
            for (int d = 0; d < 4; d++)
                for (int v = 0; v < 16; v++) pulseAtt[d, v] = patch.Psg ? 127 : patch.AttenuationFor(PulseRms(d, v), ym.Ladder);
            triAtt = FmPatchBank.Patches[FmPatchBank.SineIndex].AttenuationFor(TriangleRms, ym.Ladder);
        }

        // ---- the replay ----

        /// <summary>PSG-square patch: the period the PSG plays for a wanted tone period (its register stops at 1023), or 0 for silence; a note that does not fit follows <see cref="LowNotes"/> and is counted once when it starts.</summary>
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

        private void Sync()
        {
            Syncs++;
            var m = model;
            int asked = requestedPatch;
            if (asked != NoRequest)
            {
                requestedPatch = NoRequest;
                if (asked != patchIndex || patchDirty) { patchIndex = asked; patch = FmPatchBank.Patches[asked]; patchDirty = true; }
            }
            if (patchDirty)
            {
                patchDirty = false;
                BuildLevels();
                if (patch.Psg) { FmKey(0, false); FmKey(1, false); }
                else { LoadChannel(0, patch); LoadChannel(1, patch); }
            }

            for (int i = 0; i < 2; i++)
            {
                var p = m.P[i];
                bool playing = p.Audible(i == 0) && (channelMask & (1 << i)) != 0 && p.Volume > 0;
                double hz = NesVoiceWriter.NesCpuHz / (16.0 * (p.Timer + 1));
                if (patch.Psg)
                {
                    FmKey(i, false);
                    int n = Fit(i, p.Timer + 1, playing);
                    if (n > 0 && !PitchGuard.Blocks(PsgClockHz / (32.0 * n)))
                    {
                        SetPeriod(i, n);
                        SetVolume(i, APU_SMS.PsgPulseVolume(p.Duty & 3, p.Volume));
                    }
                    else SetVolume(i, 15);
                }
                else
                {
                    SetVolume(i, 15);
                    FmVoice(i, patch, playing, hz, pulseAtt[p.Duty & 3, p.Volume]);
                }
            }

            // the triangle: always a sine on voice 3
            FmVoice(2, FmPatchBank.Patches[FmPatchBank.SineIndex], m.TriAudible && (channelMask & 4) != 0, NesVoiceWriter.NesCpuHz / (32.0 * (m.TriTimer + 1)), triAtt);

            bool noiseOn = m.NoiseAudible && (channelMask & 8) != 0 && m.NoiseVolume > 0;
            if (noiseOn)
            {
                double shift = NesVoiceWriter.NesCpuHz / NesChannelModel.NoisePeriods[m.NoisePeriodIdx];   // the NES shift register's rate
                // the short (93-step) mode is a buzz at shift / 93; the PSG's periodic noise buzzes at shift / 16: keep the buzz's pitch, not the shift rate
                if (m.NoiseShort) shift *= 16.0 / 93.0;
                int fb = m.NoiseShort ? 0 : 4;   // periodic : white
                // fixed shift rates of the PSG: PsgClock / 512, / 1024, / 2048
                int rate = 0; double bd = double.MaxValue;
                for (int r = 0; r < 3; r++)
                {
                    double hzr = PsgClockHz / (512.0 * (1 << r));
                    double d = Math.Abs(Math.Log(hzr / shift));
                    if (d < bd) { bd = d; rate = r; }
                }
                if (bd > 0.03)
                {
                    // the fourth rate follows tone 3, which nothing else uses here (the triangle is FM): the shift rate is PsgClock / (32 period), exact for any NES rate the period can reach
                    int p3 = Math.Clamp((int)Math.Round(PsgClockHz / (32.0 * shift)), 1, 1023);
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
        private void FmVoice(int ch, FmPatch p, bool playing, double hz, int att)
        {
            ref bool above = ref (ch == 0 ? ref aboveRange0 : ref (ch == 1 ? ref aboveRange1 : ref aboveRange2));
            if (!playing || PitchGuard.Blocks(hz)) { above = false; FmKey(ch, false); return; }
            if (!FmPatchBank.TryPitch(hz, YmClockHz, out int block, out int fnum, out double cents))
            {
                if (!above) NotesAboveFmRange++;
                above = true;
                FmKey(ch, false);
                return;
            }
            above = false;
            FmPitch(ch, block, fnum, cents, FmPatchBank.Hz(block, fnum, YmClockHz));
            FmLevel(ch, p, att);
            FmKey(ch, true);
        }

        /// <summary>Moves the finished samples of both chips into the mono ring (the FM chip's two sides averaged, the PSG's the same).</summary>
        private void Drain()
        {
            fmLeft.Advance(fmSamples); fmRight.Advance(fmSamples);
            while (true)
            {
                int frames = Math.Min(Math.Min(fmLeft.Available, fmRight.Available), fl.Length);
                if (frames <= 0) break;
                int got = psg.ReadSamples(pcm.AsSpan(0, frames * 2)) / 2;
                if (got < frames) frames = got;
                if (frames <= 0) break;
                fmLeft.Read(fl.AsSpan(0, frames)); fmRight.Read(fr.AsSpan(0, frames));
                for (int i = 0; i < frames; i++)
                {
                    float v = (fl[i] + fr[i]) * 0.5f + (pcm[2 * i] + pcm[2 * i + 1]) * (0.5f / 32768f);
                    if (ringCount == ring.Length) { ringHead = (ringHead + 1) & (ring.Length - 1); ringCount--; }   // a listener that is not pulling: the oldest goes
                    ring[(ringHead + ringCount) & (ring.Length - 1)] = v;
                    ringCount++;
                }
            }
        }

        /// <summary>The band-limited mixers hold back half their kernel (8 samples) before the first sample is final, so the stream starts that far behind the time that has passed and a unit started in the middle of a game hands out 8 samples fewer than
        /// the stock units do for the same frames. Starting the ring with that much silence makes the count right (the sound is that much later, as it always was).</summary>
        private void PrimeRing() { ringHead = 0; ringCount = BandLimitedMixer.Taps / 2; Array.Clear(ring, 0, ringCount); }

        // ---- IAPU ----

        public void Step(int cpuCycles)
        {
            if (cpuCycles <= 0) return;
            if (StepFront) front.Step(cpuCycles);
            model.ClockBulk(cpuCycles);
            psg.Run(cpuCycles * 2);
            // 30 / 7 chip clocks per NES cycle, exactly
            int num = ymRemainder + cpuCycles * 30;
            ymClock += num / 7; ymRemainder = num % 7;
            ym.RunTo(ymClock);
            if ((sinceSync += cpuCycles) >= SyncCycles) { sinceSync = 0; Sync(); }
            if (front.GetQueuedSampleCount() > 4096) { while (front.ReadSamples(drainScratch) > 0) { } }   // the front's own sound is discarded
        }

        public void WriteAPURegister(ushort address, byte value)
        {
            if (address == 0x4011) DmcDacWritesDropped++;
            model.Write(address, value);
            front.WriteAPURegister(address, value);
        }

        public byte ReadAPURegister(ushort address) => front.ReadAPURegister(address);

        /// <summary>Forwarded from the front core so the bus arms its precise DMC DMA window exactly as it does on the front core itself: a game's timing does not depend on which sound chip plays.</summary>
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

        /// <summary>After a state load the model is rebuilt from the front core's state probe (see <see cref="APU_SMS"/>'s equivalent), so the chips play what the state was playing.</summary>
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
            m.DmcStarts = model.DmcStarts;   // the count of what was dropped is not part of the game's state
            model = m;
        }

        public void ClearAudioBuffers() { Drain(); PrimeRing(); front.ClearAudioBuffers(); }

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
            $"{CoreName}: patch {patchIndex} '{patch.Name}', FM {(fm[0].Keyed ? fm[0].Hz.ToString("F1") : "-")}/{(fm[1].Keyed ? fm[1].Hz.ToString("F1") : "-")}/{(fm[2].Keyed ? fm[2].Hz.ToString("F1") : "-")} Hz, PSG tones {Math.Max(0, period[0])}/{Math.Max(0, period[1])}/{Math.Max(0, period[2])} volumes {volume[0]}/{volume[1]}/{volume[2]}/{volume[3]} noise ${Math.Max(0, noiseControl):X1} | " +
            $"dropped: DMC starts {DmcStartsDropped}, $4011 writes {DmcDacWritesDropped}, notes above the FM range {NotesAboveFmRange}, PSG-square low notes silenced {LowNotesDropped} / transposed up {LowNotesTransposed}; FM notes {FmNotes}; syncs {Syncs}";
    }
}
