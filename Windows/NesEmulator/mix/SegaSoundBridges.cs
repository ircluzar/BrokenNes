using System;
using System.IO;
using NesEmulator.Mix;
using NesEmulator.Sega;

namespace NesEmulator
{
    /// <summary>What <see cref="APU_SMS"/> does with a pulse or triangle note below the PSG's range (its 10-bit tone period reaches down to 109 Hz, whatever the NES channel).</summary>
    public enum LowNotePolicy
    {
        /// <summary>The note is silent. Nothing is invented: the PSG cannot play it. Counted in <see cref="APU_SMS.LowNotesDropped"/>. The plugin uses this (a keyboard note must be the pitch asked for, or nothing).</summary>
        Drop,
        /// <summary>The note plays the octave above, as many octaves up as it takes to fit (a bass line stays a bass line, an octave or two high). Counted in <see cref="APU_SMS.LowNotesTransposed"/>. The default for a NES game, whose bass often sits below the PSG.</summary>
        OctaveUp,
    }

    /// <summary>
    /// MIX LAB (Sega track C): a NES APU whose sound comes from a Master System / Game Gear SN76489 PSG, so a NES game can be heard on Sega sound chips.
    /// Built like <see cref="APU_DMG"/>: a NES APU core (<see cref="MixConfig.NesFrontApu"/>, FIX by default) stays in front for $4015 reads, the frame IRQ and the DMC's
    /// DMA timing, and its own sound is discarded. The NES channels are rebuilt from the register writes (<see cref="NesChannelModel"/>: length counters, envelopes, sweep,
    /// the triangle's linear counter) and about 960 times a second replayed on the PSG:
    /// <list type="bullet">
    /// <item>pulse 1 and 2 become tones 1 and 2. The PSG clock is exactly twice the NES CPU clock, so a pulse timer value t is PSG period t + 1 at the very same pitch
    /// (zero error); the PSG's 10-bit period reaches down to 109 Hz, so a pulse note below that cannot be played as it is: by <see cref="LowNotes"/> it is dropped (silent) or played whole octaves up, and counted either way.
    /// The PSG has one waveform, a 50% square: the four pulse duty cycles all sound the same here.</item>
    /// <item>the triangle becomes tone 3, period 2 (t + 1) (pitch kept, down to 109 Hz; lower ones as for the pulses). The PSG has no triangle: it is a square wave, at the loudness the NES triangle has.</item>
    /// <item>noise becomes the PSG noise: the NES long mode is white noise, the short (93-step) mode is periodic noise. The PSG has three fixed shift rates and a fourth that
    /// follows tone 3: when the triangle is silent that fourth rate gives the NES rate exactly (and a short-mode buzz keeps its pitch); while the triangle sounds the nearest of
    /// the three fixed rates is used and the compromise is counted (<see cref="NoiseRateCompromises"/>).</item>
    /// <item>volume: the NES's 15 linear steps become the PSG's 2 dB steps (the loud ones merge), each channel at the loudness (AC RMS) the NES mixer gives it, a thin pulse quieter than a fat one, so the balance of a NES song is kept.</item>
    /// <item>the DMC has nowhere to go. Its samples and $4011 writes are dropped, counted and reported (<see cref="DmcStartsDropped"/>, <see cref="DmcDacWritesDropped"/>); never silently.</item>
    /// </list>
    /// Discovered by CoreRegistry as APU id "SMS". Allocation-free while running (the plugin plays it on the audio thread).
    /// </summary>
    public sealed class APU_SMS : IAPU, IDmcDmaSchedulable
    {
        /// <summary>The PSG runs from exactly twice the NES CPU clock: a NES timer value t is PSG period t + 1 at the same pitch.</summary>
        public const double PsgClockHz = 2 * NesVoiceWriter.NesCpuHz;

        private const int SyncCycles = 1864;   // about 960 times a second, like the Game Boy and SNES bridges

        // ---- loudness: each NES channel is replayed at the loudness it has on a NES, measured as AC RMS (what the ear and the plugin's tests hear), in the PSG's 2 dB steps ----
        // What a channel adds to the NES mixer when it plays alone (NESdev's mixer formulas): a pulse or the noise at volume v, the triangle at its fixed full swing.
        internal static double PulseOut(int v) => v <= 0 ? 0 : 95.88 / (8128.0 / v + 100);
        internal static double NoiseOut(int v) => v <= 0 ? 0 : 159.79 / (1.0 / (v / 12241.0) + 100);
        private static readonly double TriangleOut = 159.79 / (1.0 / (15 / 8227.0) + 100);

        /// <summary>Full scale of the PSG output (volume 0): the loudest thing a NES channel can ask for, the noise at volume 15, as the PSG's unipolar amplitude.</summary>
        public static readonly float PsgGain = (float)NoiseOut(15);

        // AC RMS of a pulse with a duty cycle d, per unit of amplitude: sqrt(d (1 - d)). The PSG tone is a 50% square (0.5), so a thinner NES pulse is played quieter to sound as loud.
        private static readonly double[] DutyRms = { Math.Sqrt(0.125 * 0.875), Math.Sqrt(0.25 * 0.75), 0.5, Math.Sqrt(0.25 * 0.75) };

        private static readonly byte[,] PulseVolume = new byte[4, 16];
        private static readonly byte[] NoiseVolume = new byte[16];
        private static readonly byte TriangleVolume;

        static APU_SMS()
        {
            for (int v = 1; v < 16; v++)
            {
                for (int d = 0; d < 4; d++) PulseVolume[d, v] = Nearest(PulseOut(v) * DutyRms[d] / 0.5);
                NoiseVolume[v] = Nearest(NoiseOut(v));
            }
            for (int d = 0; d < 4; d++) PulseVolume[d, 0] = 15;
            NoiseVolume[0] = 15;
            TriangleVolume = Nearest(TriangleOut / Math.Sqrt(3));   // a triangle's AC RMS is peak-to-peak / (2 sqrt 3); a 50% square's is amplitude / 2
        }

        /// <summary>The PSG volume register value (0 loudest, 14 quietest audible) whose amplitude is nearest, in decibels, to a wanted amplitude.</summary>
        private static byte Nearest(double amplitude)
        {
            int best = 0; double bd = double.MaxValue;
            for (int k = 0; k <= 14; k++)
            {
                double amp = PsgGain * Math.Pow(10, -k / 10.0);   // 2 dB per step
                double d = Math.Abs(Math.Log(amp / amplitude));
                if (d < bd) { bd = d; best = k; }
            }
            return (byte)best;
        }
        private readonly IAPU front;
        private readonly Sn76489 psg;
        private NesChannelModel model = new();
        private int channelMask = 0x1F;
        private int sinceSync;

        // what the PSG is set to now (-1: not written yet), so a held note writes nothing and the noise shift register is not reloaded for nothing
        private readonly int[] period = { -1, -1, -1 };
        private readonly int[] volume = { -1, -1, -1, -1 };
        private int noiseControl = -1;
        private readonly bool[] outOfRange = new bool[3];

        // the audio out: mono floats, drained from the PSG at every sync (allocation-free)
        private readonly short[] pcm = new short[512];
        private readonly float[] ring = new float[1 << 16];
        private int ringHead, ringCount;
        private readonly float[] drainScratch = new float[2048];

        // what the bridge could not carry over (counted, never silent)
        /// <summary>Syncs the bridge has made (about 960 a second).</summary>
        public long Syncs { get; private set; }
        /// <summary>DMC sample starts ($4015 written with the DMC bit set) that went nowhere: the PSG has no sample channel.</summary>
        public long DmcStartsDropped => model.DmcStarts;
        /// <summary>Writes to the DMC's direct DAC register ($4011) that went nowhere.</summary>
        public long DmcDacWritesDropped { get; private set; }
        /// <summary>What happens to a note below the PSG's range. <see cref="DefaultLowNotes"/> when the core is made.</summary>
        public LowNotePolicy LowNotes { get; set; } = DefaultLowNotes;
        /// <summary>The policy a new core starts with: <see cref="LowNotePolicy.OctaveUp"/> (a game's bass line is kept an octave up); the plugin sets <see cref="LowNotePolicy.Drop"/> before it makes one.</summary>
        public static LowNotePolicy DefaultLowNotes = LowNotePolicy.OctaveUp;
        /// <summary>
        /// Whether the front NES core is stepped. It must be when a NES game runs on this core (its frame IRQ, its $4015 status, its DMC and the DMC's DMA timing are what the game sees). A host with
        /// no CPU that never reads the chip back (the plugin's Direct mode, which only writes registers and listens) turns it off: the front's own sound is thrown away anyway, so stepping it only costs
        /// time. Off, a read of $4015 shows nothing real. <see cref="DefaultStepFront"/> when the core is made.
        /// </summary>
        public bool StepFront { get; set; } = DefaultStepFront;
        public static bool DefaultStepFront = true;
        /// <summary>Pulse or triangle notes too low for the PSG's 10-bit period that were silenced (policy <see cref="LowNotePolicy.Drop"/>; counted once per note, when it starts).</summary>
        public long LowNotesDropped { get; private set; }
        /// <summary>The same notes played whole octaves up instead (policy <see cref="LowNotePolicy.OctaveUp"/>).</summary>
        public long LowNotesTransposed { get; private set; }
        /// <summary>Syncs where the noise had to use a fixed PSG rate close to, not at, the NES rate because tone 3 was busy with the triangle.</summary>
        public long NoiseRateCompromises { get; private set; }

        /// <summary>The PSG the sound comes from (its registers, for probes and the plugin's tests; do not write to it).</summary>
        public Sn76489 Psg => psg;
        /// <summary>The NES APU core kept in front (registers, frame IRQ, DMC timing).</summary>
        public IAPU Front => front;

        public APU_SMS(Bus bus)
        {
            string frontId = MixConfig.NesFrontApu;
            if (frontId.Equals("SMS", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("APU_SMS cannot be its own front APU");
            var t = CoreRegistry.ApuTypes.TryGetValue(frontId, out var ft) ? ft : throw new ArgumentException($"No NES APU '{frontId}'");
            front = CoreRegistry.CreateInstance<IAPU>(t, bus) ?? throw new InvalidOperationException("front APU");
            psg = new Sn76489(PsgClockHz, front.GetSampleRate()) { Gain = PsgGain };
            PowerOn();
        }

        public string CoreName => $"SMS:SN76489+{front.CoreName}";
        public string Description => "MIX LAB: NES channels played on a Master System / Game Gear PSG (pulses -> tones 1 and 2 at 50% duty, triangle -> tone 3, noise -> noise; the DMC is dropped and counted)";
        public int Performance => 0; public int Rating => 1; public string Category => "Experimental";

        private void PowerOn()
        {
            psg.Reset();
            Array.Fill(period, -1); Array.Fill(volume, -1); noiseControl = -1; Array.Clear(outOfRange);
            for (int c = 0; c < 4; c++) SetVolume(c, 15);
            SetNoise(0);
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

        // ---- the replay ----

        /// <summary>
        /// The period the PSG plays for a wanted tone period <paramref name="n"/> (its 10-bit register stops at 1023), or 0 for silence. A note that does not fit is silenced
        /// or moved whole octaves up by <see cref="LowNotes"/>, and counted once when it starts.
        /// </summary>
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
            bool toneThreeBusy = false;

            for (int i = 0; i < 2; i++)
            {
                var p = m.P[i];
                int n = Fit(i, p.Timer + 1, p.Audible(i == 0) && (channelMask & (1 << i)) != 0 && p.Volume > 0);
                if (n > 0 && !PitchGuard.Blocks(PsgClockHz / (32.0 * n)))
                {
                    SetPeriod(i, n);
                    SetVolume(i, PulseVolume[p.Duty & 3, p.Volume]);
                }
                else SetVolume(i, 15);
            }

            {
                int n = Fit(2, 2 * (m.TriTimer + 1), m.TriAudible && (channelMask & 4) != 0);
                if (n > 0 && !PitchGuard.Blocks(PsgClockHz / (32.0 * n)))
                {
                    SetPeriod(2, n);
                    SetVolume(2, TriangleVolume);
                    toneThreeBusy = true;
                }
                else SetVolume(2, 15);
            }

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
                    double hz = PsgClockHz / (512.0 * (1 << r));
                    double d = Math.Abs(Math.Log(hz / shift));
                    if (d < bd) { bd = d; rate = r; }
                }
                if (bd > 0.03 && !toneThreeBusy)
                {
                    // the fourth rate follows tone 3, which the triangle is not using: shift rate = PsgClock / (32 period)
                    int p3 = Math.Clamp((int)Math.Round(PsgClockHz / (32.0 * shift)), 1, 1023);
                    SetPeriod(2, p3);
                    SetVolume(2, 15);
                    rate = 3;
                }
                else if (bd > 0.03) NoiseRateCompromises++;
                SetNoise(fb | rate);
                SetVolume(3, NoiseVolume[m.NoiseVolume]);
            }
            else SetVolume(3, 15);

            Drain();
        }

        /// <summary>Moves the PSG's finished samples into the mono ring.</summary>
        private void Drain()
        {
            int n;
            while ((n = psg.ReadSamples(pcm)) > 0)
            {
                int frames = n / 2;
                for (int i = 0; i < frames; i++)
                {
                    float v = (pcm[2 * i] + pcm[2 * i + 1]) * (0.5f / 32768f);
                    if (ringCount == ring.Length) { ringHead = (ringHead + 1) & (ring.Length - 1); ringCount--; }   // a listener that is not pulling: the oldest goes
                    ring[(ringHead + ringCount) & (ring.Length - 1)] = v;
                    ringCount++;
                }
            }
        }

        // ---- IAPU ----

        public void Step(int cpuCycles)
        {
            if (cpuCycles <= 0) return;
            if (StepFront) front.Step(cpuCycles);
            model.ClockBulk(cpuCycles);
            psg.Run(cpuCycles * 2);
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

        /// <summary>
        /// After a state load the front core holds the new state but the channel model still holds the old one. When the front core can describe itself (<see cref="IApuStateProbe"/>: FIX can) the model is
        /// rebuilt from that, so the PSG plays what the loaded state was playing (a pulse's sweep period and envelope divider are not in the description: a sweep starts again from its first step). A front core without
        /// the probe leaves the model to catch up as the game rewrites its registers.
        /// </summary>
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
        public void ClearAudioBuffers() { Drain(); ringHead = ringCount = 0; front.ClearAudioBuffers(); }

        public void Reset()
        {
            front.Reset();
            model = new NesChannelModel();
            sinceSync = 0;
            ringHead = ringCount = 0;
            PowerOn();
        }

        /// <summary>One line for probes and logs: what the PSG is playing and what the bridge dropped.</summary>
        public string Describe() =>
            $"{CoreName}: tones {Math.Max(0, period[0])}/{Math.Max(0, period[1])}/{Math.Max(0, period[2])} volumes {volume[0]}/{volume[1]}/{volume[2]}/{volume[3]} noise ${Math.Max(0, noiseControl):X1} | " +
            $"dropped: DMC starts {DmcStartsDropped}, $4011 writes {DmcDacWritesDropped}, low notes silenced {LowNotesDropped}; transposed up {LowNotesTransposed}; noise rate compromises {NoiseRateCompromises}; syncs {Syncs}";
    }
}

namespace NesEmulator.Sega
{
    /// <summary>
    /// MIX LAB (Sega track C): a Master System / Game Gear sound unit whose sound comes from a NES APU core, so a Sega game can be heard on NES chips (sound unit id <c>NES</c> in
    /// <see cref="SmsSoundFactories"/>; <c>NES:FIX</c>, <c>NES:FIXS</c> ... name the NES chip). The real SN76489 stays in front, so everything a game can see behaves as on the chip: the
    /// latch and data bytes, volumes, the tone periods, the noise register, the Game Gear stereo mask. About 960 times a second its voices (<see cref="Sn76489.DescribeVoices"/>) are
    /// replayed on the NES chip through <see cref="NesVoiceWriter"/>: tones 1 and 2 on the pulses (50% duty), tone 3 on the triangle, the noise on the noise channel (periodic noise on
    /// the NES short mode), with the quietest voices dropped when the NES chip has fewer channels than the PSG has voices (it never does: four voices fit one bank). A tone period of
    /// 0 or 1 (the DC trick behind PCM playback on the Sega part) is not a note and is silent here, as the hub reports it.
    /// What this cannot carry, it counts: Game Gear stereo (the NES is mono: <see cref="StereoWritesLossy"/>), the YM2413 FM chip (<see cref="FmWritesDropped"/>; the unit
    /// says it has none, so a game's detection routine sees a plain console), and pitches a NES channel cannot reach (<see cref="NesVoiceWriter.Dropped"/> counts the voices lost to
    /// missing channels; a note below the triangle's or pulse's range is silenced by the writer). The PSG itself is not run: only its registers matter, the sound is the NES chip's.
    /// </summary>
    public sealed class SmsSoundOnNes : ISmsSound
    {
        public const string DefaultBackApu = "FIX";

        private const int StepCycles = 32;
        private const int SyncCycles = 1864;

        private readonly Sn76489 front;
        private readonly NesEmulator.Mix.NesApuHost host;
        private readonly IAPU back;
        private readonly NesVoiceWriter writer;
        private readonly double nesPerClock;
        private readonly HubVoice[] voices = new HubVoice[4];
        private readonly float[] pull = new float[2048];
        private double nesAcc;
        private int nesDue, sinceSync;

        /// <summary>The NES APU core the sound comes from.</summary>
        public string BackApuId { get; }
        /// <summary>Syncs the bridge has made.</summary>
        public long Syncs { get; private set; }
        /// <summary>Writes to the YM2413 ports ($F0-$F2 audio control excluded) that went nowhere: the NES chip has no FM.</summary>
        public long FmWritesDropped { get; private set; }
        /// <summary>Game Gear stereo-port writes that left some voice on one side only: the NES is mono, so that voice plays on both.</summary>
        public long StereoWritesLossy { get; private set; }
        /// <summary>Syncs where tone 3 sounded below full volume: the NES triangle has no volume control, so it played at its one level.</summary>
        public long TriangleLevelsLost { get; private set; }
        public NesVoiceWriter Writer => writer;
        public Sn76489 Psg => front;

        public SmsSoundOnNes(SmsSoundRequest request)
        {
            BackApuId = string.IsNullOrWhiteSpace(request.Detail) ? DefaultBackApu : request.Detail!;
            front = new Sn76489(request.CpuClockHz, 44100, request.Variant);
            host = new NesEmulator.Mix.NesApuHost(BackApuId);   // throws ArgumentException for an unknown NES chip: never a quiet substitute
            back = host.Apu;
            writer = new NesVoiceWriter(NesRegisterPorts.For(back));
            nesPerClock = NesVoiceWriter.NesCpuHz / request.CpuClockHz;
        }

        public string CoreName => $"NES:{back.CoreName}+SN76489";
        public int SampleRate => back.GetSampleRate();

        public void Reset()
        {
            front.Reset();
            Replay();
        }

        public void Run(int cpuClocks)
        {
            nesAcc += cpuClocks * nesPerClock;
            int c = (int)nesAcc;
            if (c <= 0) return;
            nesAcc -= c;
            nesDue += c;
            sinceSync += c;
            while (nesDue >= StepCycles) { back.Step(StepCycles); nesDue -= StepCycles; }
            if (sinceSync >= SyncCycles) { sinceSync = 0; Replay(); }
        }

        private void Replay()
        {
            Syncs++;
            front.DescribeVoices(voices);
            if (voices[2].Audible && front.Volume(2) > 0) TriangleLevelsLost++;
            // The writer maps a level to the NES's 15 linear volume steps by rounding; the NES mixer is not quite linear and its quiet steps are coarse, so pick the step whose
            // loudness (NESdev's mixer formulas) is nearest, in decibels, to what the stock PSG unit would output for that volume.
            for (int i = 0; i < 4; i++)
                if (i != 2 && voices[i].Audible) voices[i] = voices[i] with { Level = NesVolumeFor(voices[i].Level, noise: i == 3) / 15f };
            writer.Apply(voices);
        }

        /// <summary>The NES volume (1-15) that sounds nearest, in decibels, to a PSG level (0-1) played by the stock unit; the NES cannot go below volume 1 (-23.5 dB).</summary>
        private int NesVolumeFor(float level, bool noise)
        {
            double target = front.Gain * level;
            int best = 1; double bd = double.MaxValue;
            for (int v = 1; v <= 15; v++)
            {
                double d = Math.Abs(Math.Log((noise ? APU_SMS.NoiseOut(v) : APU_SMS.PulseOut(v)) / target));
                if (d < bd) { bd = d; best = v; }
            }
            return best;
        }

        public void WritePsg(byte value) => front.Write(value);

        public void WriteStereo(byte value)
        {
            front.WriteStereo(value);
            for (int bit = 0; bit < 4; bit++)
                if (((value >> (4 + bit)) & 1) != ((value >> bit) & 1)) { StereoWritesLossy++; break; }
        }

        // ---- no FM: the unit says so, and every write to the chip it does not have is counted ----
        public bool HasFm => false;
        public void WriteFmAddress(byte value) => FmWritesDropped++;
        public void WriteFmData(byte value) => FmWritesDropped++;
        public void WriteAudioControl(byte value) { }
        public byte ReadAudioControl() => 0xFF;

        public int ReadSamples(Span<short> dest)
        {
            int written = 0;
            while (written + 1 < dest.Length)
            {
                int want = Math.Min(pull.Length, (dest.Length - written) / 2);
                int got = back.ReadSamples(pull.AsSpan(0, want));
                if (got <= 0) break;
                for (int i = 0; i < got; i++)
                {
                    short s = (short)Math.Clamp(pull[i] * 32767f, -32768f, 32767f);
                    dest[written++] = s; dest[written++] = s;
                }
            }
            return written;
        }

        public int DescribeVoices(Span<HubVoice> dest) => front.DescribeVoices(dest);

        public void SaveState(BinaryWriter w) => front.SaveState(w);

        public void LoadState(BinaryReader r)
        {
            front.LoadState(r);
            Replay();
        }

        public string Describe() =>
            $"{CoreName}: PSG tones {front.Period(0)}/{front.Period(1)}/{front.Period(2)} volumes {front.Volume(0)}/{front.Volume(1)}/{front.Volume(2)}/{front.Volume(3)} noise {front.NoiseControl} stereo ${front.StereoMask:X2} | " +
            $"not carried: FM writes {FmWritesDropped}, one-sided stereo writes {StereoWritesLossy}, tone 3 syncs below full volume (the triangle has no volume) {TriangleLevelsLost}, voices without a NES channel {writer.Dropped}; syncs {Syncs}, NES register writes {writer.Writes}";
    }
}
