using System;
using System.Collections.Generic;
using System.IO;
using NesEmulator.Gb;
using NesEmulator.Sega;
using NesEmulator.Snes;

namespace NesEmulator.Mix
{
    // =================================================================================================================================================================================
    // THE SOUND HALF OF THE SHARED STEP (docs/projects/enhanced-cores.md 5.3): a guest console's sound reaches another console's ENHANCED sound chip directly, with no NES registers in
    // between. Each bridge keeps the guest's real sound unit running in front (its registers, its driver, everything the game can see) and, about 960 times a second, hands that unit's voice
    // list (the hub's language, HubVoice) to an IEnhancedSoundChip, which plays every voice on a channel of its own in its own rules (looped samples on the S-DSP, FM and squares on the
    // Genesis, squares on the PSG) with the guest's pan where the chip has stereo. The NES path (the *OnNes bridges and NesVoiceWriter) is untouched: these are new routes beside it.
    // Levels: each bridge puts its voices on one scale, 1 = the guest's loudest single voice (a full FM carrier on the Genesis, a full PSG tone on the Master System, ...), so the guest's
    // balance is kept; the chip turns that into its own volume steps.
    // =================================================================================================================================================================================

    /// <summary>A Genesis game's sound (its real Z80, YM2612 and PSG in front: <see cref="MdSoundUnit"/>) replayed on another console's enhanced sound chip: menu ids <c>SNES:SFCS</c>, <c>SMS:SMSS</c>, <c>GG:GGS</c>.
    /// The eleven voices (FM 1-6, the DAC, PSG tones 1-3, the noise) each get a channel; FM timbre (the chip plays a sample or a square at the FM voice's pitch) and the DAC's samples (a noise hit) are not carried, and counted.</summary>
    public sealed class MdSoundOnChip : IGenesisSound, IHubVoiceSource
    {
        private readonly MdSoundUnit inner;
        private readonly IEnhancedSoundChip chip;
        private readonly double masterHz;
        private readonly long syncEvery;
        private readonly HubVoice[] voices = new HubVoice[MdSoundUnit.VoiceCount];
        private readonly bool[] wasAudible = new bool[MdSoundUnit.VoiceCount];
        private readonly short[] drain = new short[4096];
        private long nextSync;

        public string Route { get; }
        public IEnhancedSoundChip Chip => chip;
        public MdSoundUnit Inner => inner;
        public long Syncs { get; private set; }
        /// <summary>FM notes played without their FM timbre (a chip without FM plays the pitch), counted at each key-on.</summary>
        public long FmNotes { get; private set; }
        /// <summary>Syncs in which the DAC sounded as a noise hit (its samples are not carried).</summary>
        public long DacSyncs { get; private set; }

        public MdSoundOnChip(SegaMachine machine, string route)
        {
            Route = route;
            inner = new MdSoundUnit(machine);
            chip = EnhancedChips.Sound(route, machine.SampleRate);        // an unknown route throws ArgumentException: never a quiet substitute
            masterHz = machine.Timeline.MasterHz;
            syncEvery = (long)(masterHz / 960);
            inner.DescribeVoices(voices);
            chip.EnsureExtBanks(chip.BanksFor(voices) - 1);              // the banks all eleven voices need (each made when a voice first plays on it)
        }

        public string CoreName => $"{Route}+{inner.CoreName}";
        public int SampleRate => chip.SampleRate;
        public void Attach(IGenesisSoundHost host) => inner.Attach(host);
        public void Reset() { inner.Reset(); nextSync = 0; chip.Silence(); Array.Clear(wasAudible); }
        public void WriteBusRequest(bool requested, long masterClock) => inner.WriteBusRequest(requested, masterClock);
        public bool IsBusGranted(long masterClock) => inner.IsBusGranted(masterClock);
        public void WriteReset(bool released, long masterClock) => inner.WriteReset(released, masterClock);
        public bool IsInReset => inner.IsInReset;
        public byte Read(int offset, long masterClock) => inner.Read(offset, masterClock);
        public void Write(int offset, byte value, long masterClock) => inner.Write(offset, value, masterClock);
        public void SetInterrupt(bool asserted, long masterClock) => inner.SetInterrupt(asserted, masterClock);
        public void WritePsg(byte value, long masterClock) => inner.WritePsg(value, masterClock);

        public void RunTo(long masterClock)
        {
            inner.RunTo(masterClock);
            while (nextSync <= masterClock)
            {
                chip.RunTo(nextSync / masterHz);
                Replay();
                nextSync += syncEvery;
            }
            chip.RunTo(masterClock / masterHz);
        }

        private double FmLevel(int ch)
        {
            var fm = inner.Fm;
            int choffs = ch % 3 + 0x100 * (ch / 3);
            int carriers = FmPatchBank.CarriersOf(fm.Register(0xB0 + choffs) & 7);
            double power = 0;
            for (int n = 0; n < 4; n++)
            {
                if ((carriers >> n & 1) == 0) continue;
                int tl = fm.Register(0x40 + choffs + FmPatchBank.SlotOffset(n)) & 0x7F;
                int att = Math.Min(0x3FF, fm.DebugEnvelope(ch, n) + (tl << 3));
                double a = Math.Pow(10.0, -att * 0.09375 / 20.0);
                power += a * a;
            }
            return Math.Min(1.0, Math.Sqrt(power));
        }

        private void Replay()
        {
            Syncs++;
            while (inner.ReadSamples(drain) > 0) { }          // the inner unit's own sound is discarded
            inner.DescribeVoices(voices);
            // one scale, 1 = a full FM carrier: the PSG at its share of that (0.5 x its gain against the carrier's RMS), the DAC's swing as noise of the same loudness the NES bridge gives it
            double fullFm = FmPatchBank.CarrierRms * inner.FmGain, psg = 0.5 * inner.Psg.Gain / Math.Max(1e-9, fullFm), dac = MdSoundOnNes.DacRmsPerSwing * inner.FmGain / Math.Max(1e-9, fullFm);
            for (int i = 0; i < voices.Length; i++)
            {
                var v = voices[i];
                if (!v.Audible) { wasAudible[i] = false; continue; }
                float level = i < 6 ? (float)FmLevel(i) : i == 6 ? (float)Math.Min(1.0, dac * v.Level) : (float)Math.Min(1.0, psg * v.Level);
                voices[i] = v with { Level = level };
                if (i < 6 && !wasAudible[i]) FmNotes++;
                if (i == 6) DacSyncs++;
                wasAudible[i] = true;
            }
            chip.Apply(voices);
        }

        public int ReadSamples(Span<short> dest) => chip.ReadSamples(dest);
        public int DescribeVoices(Span<HubVoice> dest) => inner.DescribeVoices(dest);
        public void SaveState(BinaryWriter w) => inner.SaveState(w);
        public void LoadState(BinaryReader r)
        {
            inner.LoadState(r);
            // the chip is a listener, not part of the machine: it starts again from the loaded unit's clock and an empty output, then plays the loaded voices
            nextSync = inner.Clock;
            chip.Silence();
            Array.Clear(wasAudible);
        }
        public string Describe() => $"{CoreName}: {inner.Describe()} | {chip.Describe()} | not carried: FM timbre ({FmNotes} FM notes), DAC samples ({DacSyncs} syncs as noise hits); syncs {Syncs}";
    }

    /// <summary>A Master System / Game Gear game's PSG (the real SN76489 in front: its latch, volumes, periods, noise, the Game Gear's stereo port) replayed on another console's enhanced sound chip:
    /// menu ids <c>SNES:SFCS</c>, <c>MD:MDS</c>. Four voices, each on a channel of its own; the Game Gear's stereo port is the pan. No FM chip (the unit says so; writes to it are counted).</summary>
    public sealed class SmsSoundOnChip : ISmsSound, IHubVoiceSource
    {
        private readonly Sn76489 front;
        private readonly IEnhancedSoundChip chip;
        private readonly double cpuHz;
        private readonly long syncEvery;
        private readonly HubVoice[] voices = new HubVoice[4];
        private long clocks, nextSync;

        public string Route { get; }
        public IEnhancedSoundChip Chip => chip;
        public Sn76489 Psg => front;
        public long Syncs { get; private set; }
        public long FmWritesDropped { get; private set; }

        public SmsSoundOnChip(SmsSoundRequest request, string route)
        {
            Route = route;
            cpuHz = request.CpuClockHz;
            front = new Sn76489(cpuHz, 44100, request.Variant);
            chip = EnhancedChips.Sound(route, request.Machine.SampleRate);
            syncEvery = (long)(cpuHz / 960);
            front.DescribeVoices(voices);
            chip.EnsureExtBanks(chip.BanksFor(voices) - 1);
        }

        public string CoreName => $"{Route}+SN76489";
        public int SampleRate => chip.SampleRate;
        public void Reset() { front.Reset(); clocks = 0; nextSync = 0; chip.Silence(); }

        public void Run(int cpuClocks)
        {
            if (cpuClocks <= 0) return;
            clocks += cpuClocks;
            while (nextSync <= clocks) { chip.RunTo(nextSync / cpuHz); Replay(); nextSync += syncEvery; }
            chip.RunTo(clocks / cpuHz);
        }

        private void Replay()
        {
            Syncs++;
            front.DescribeVoices(voices);       // levels are the PSG's own amplitudes: 1 = a full tone
            chip.Apply(voices);
        }

        public void WritePsg(byte value) => front.Write(value);
        public void WriteStereo(byte value) => front.WriteStereo(value);
        public bool HasFm => false;
        public void WriteFmAddress(byte value) => FmWritesDropped++;
        public void WriteFmData(byte value) => FmWritesDropped++;
        public void WriteAudioControl(byte value) { }
        public byte ReadAudioControl() => 0xFF;
        public int ReadSamples(Span<short> dest) => chip.ReadSamples(dest);
        public int DescribeVoices(Span<HubVoice> dest) => front.DescribeVoices(dest);
        public void SaveState(BinaryWriter w) => front.SaveState(w);
        public void LoadState(BinaryReader r) { front.LoadState(r); chip.Silence(); Replay(); }
        public string Describe() => $"{CoreName}: PSG tones {front.Period(0)}/{front.Period(1)}/{front.Period(2)} volumes {front.Volume(0)}/{front.Volume(1)}/{front.Volume(2)}/{front.Volume(3)} stereo ${front.StereoMask:X2} | {chip.Describe()} | FM writes dropped {FmWritesDropped}; syncs {Syncs}";
    }

    /// <summary>The Game Boy's four voices as the hub's voice list (the same description <see cref="GbApuOnNes"/> gives): pulse 1 and 2 with their duty, the wave channel (a triangle), the noise; level the
    /// channel's volume (0 to 1), the pan from NR51.</summary>
    internal static class GbVoices
    {
        public static int Describe(APU_GB front, Span<HubVoice> voices)
        {
            int nr51 = front.ReadRegister(0x25), n = Math.Min(4, voices.Length);
            for (int i = 0; i < n; i++)
            {
                var ch = front.Channel(i);
                bool on = ch.Enabled && ch.DacOn && ch.Volume > 0;
                float pan = HubVoice.PanOf((nr51 >> (4 + i) & 1) != 0, (nr51 >> i & 1) != 0);
                double hz = i switch
                {
                    0 or 1 => ch.Period < 2048 ? 131072.0 / (2048 - ch.Period) : 0,
                    2 => ch.Period < 2048 ? 65536.0 / (2048 - ch.Period) : 0,
                    _ => 262144.0 / ((ch.NoiseDivisorCode == 0 ? 0.5 : ch.NoiseDivisorCode) * Math.Pow(2, ch.NoiseShift)),
                };
                var kind = i < 2 ? HubVoiceKind.Pulse : i == 2 ? HubVoiceKind.Triangle : HubVoiceKind.Noise;
                voices[i] = new HubVoice(kind, on ? hz : 0, on ? ch.Volume / 15f : 0f, Duty: ch.Duty & 3, ShortNoise: i == 3 && ch.NoiseShortMode, Pan: pan);
            }
            return n;
        }
    }

    /// <summary>A Game Boy game's sound (the real APU_GB in front: registers, NR52, length counters, envelopes) replayed on another console's enhanced sound chip: menu ids <c>SNES:SFCS</c>, <c>SMS:SMSS</c>,
    /// <c>GG:GGS</c>, <c>MD:MDS</c>. Its four voices each on a channel; the pulses keep their duty where the chip has duties (the S-DSP), NR51 is the pan where the chip has stereo; the wave channel's own waveform is
    /// not carried (a triangle at its pitch).</summary>
    public sealed class GbApuOnChip : IGbApu, IHubVoiceSource
    {
        private const double TClock = 4194304.0;
        private readonly APU_GB front;
        private readonly IEnhancedSoundChip chip;
        private readonly HubVoice[] voices = new HubVoice[4];
        private readonly short[] scratch = new short[4096];
        private long tCycles; private int sinceSync;

        public string Route { get; }
        public IEnhancedSoundChip Chip => chip;
        public long Syncs { get; private set; }

        public GbApuOnChip(GbModel model, string route)
        {
            Route = route;
            front = new APU_GB(model);
            chip = EnhancedChips.Sound(route, front.SampleRate);
            GbVoices.Describe(front, voices);
            chip.EnsureExtBanks(chip.BanksFor(voices) - 1);
        }

        public string CoreName => $"{Route}+GB";
        public int SampleRate => chip.SampleRate;
        public void ResetPostBoot() => front.ResetPostBoot();
        public void FrameSequencerStep() => front.FrameSequencerStep();
        public byte ReadRegister(int reg) => front.ReadRegister(reg);
        public void WriteRegister(int reg, byte v) => front.WriteRegister(reg, v);
        public void SaveState(BinaryWriter w) => front.SaveState(w);
        public void LoadState(BinaryReader r) { front.LoadState(r); chip.Silence(); }

        public void Tick(int t)
        {
            front.Tick(t);
            tCycles += t;
            if ((sinceSync += t) >= 4369) { sinceSync = 0; chip.RunTo(tCycles / TClock); Syncs++; GbVoices.Describe(front, voices); chip.Apply(voices); }
        }

        public int DescribeVoices(Span<HubVoice> dest) => GbVoices.Describe(front, dest);

        public int ReadSamples(short[] buffer)
        {
            while (front.ReadSamples(scratch) > 0) { }       // the front's own sound is discarded
            chip.RunTo(tCycles / TClock);
            return chip.ReadSamples(buffer);
        }
    }

    /// <summary>The SNES's eight DSP voices as the hub's voice list (shared by <see cref="NesApuOnSnes"/>'s description and <see cref="SnesApuOnChip"/>): a looped sample is a pulse at the sample's pitch (its own
    /// waveform period found by autocorrelation once per sample), a noise voice or a one-shot sample a noise; the level is the envelope times the voice's volume (0 to 1); the pan from VOL L / VOL R.</summary>
    internal sealed class SnesVoiceReader
    {
        private readonly ISnesApuProbe probe;
        private readonly Dictionary<int, double> periodCache = new();
        public SnesVoiceReader(ISnesApuProbe probe) { this.probe = probe; }

        private static readonly int[] DspNoiseHz = { 0, 16, 21, 25, 31, 42, 50, 63, 83, 100, 125, 167, 200, 250, 333, 400, 500, 667, 800, 1000, 1300, 1600, 2000, 2700, 3200, 4000, 5300, 6400, 8000, 10700, 16000, 32000 };

        public int Describe(Span<HubVoice> voices)
        {
            var r = probe.DspRegs;
            int n = Math.Min(8, voices.Length);
            for (int v = 0; v < n; v++)
            {
                int b = v * 16, envx = r[b + 8] & 0x7F;
                int l = Math.Abs((sbyte)r[b]), rt = Math.Abs((sbyte)r[b + 1]);
                double loud = envx * (l + rt) / 2.0;
                bool isNoise = (r[0x3D] & (1 << v)) != 0;
                int pitch = r[b + 2] | (r[b + 3] & 0x3F) << 8;
                double period = isNoise ? 0 : Period(r[b + 4]);
                float level = loud < 40 ? 0f : (float)Math.Min(1.0, loud / 16129.0);
                float pan = l + rt == 0 ? 0f : (float)(rt - l) / (l + rt);
                voices[v] = isNoise || period == 0
                    ? new HubVoice(HubVoiceKind.Noise, isNoise ? DspNoiseHz[r[0x6C] & 31] : 4000, level, Pan: pan)
                    : new HubVoice(HubVoiceKind.Pulse, 32000.0 * pitch / 4096.0 / period, level, Pan: pan);
            }
            return n;
        }

        /// <summary>The sample's own waveform period in samples (0 = no loop: a one-shot, treated as percussion).</summary>
        public double Period(int srcn)
        {
            var r = probe.DspRegs; var ar = probe.Aram;
            int d = (r[0x5D] << 8) + srcn * 4;
            int start = ar[d & 0xFFFF] | ar[(d + 1) & 0xFFFF] << 8, loop = ar[(d + 2) & 0xFFFF] | ar[(d + 3) & 0xFFFF] << 8;
            int key = (start << 16) | loop;
            if (periodCache.TryGetValue(key, out var cached)) return cached;
            var (smp, loopIdx) = Brr.Decode(ar, start, loop);
            double period = 0;
            if (loopIdx >= 0 && smp.Length - loopIdx >= 16)
            {
                int L = smp.Length - loopIdx;
                var w = new double[L * 3]; for (int i = 0; i < w.Length; i++) w[i] = smp[loopIdx + i % L];
                double best = 0; var corr = new double[Math.Min(L, 2048) + 1];
                for (int lag = 4; lag < corr.Length; lag++)
                {
                    double s = 0, e = 0; for (int i = 0; i < L; i++) { s += w[i] * w[i + lag]; e += w[i] * w[i]; }
                    corr[lag] = e > 0 ? s / e : 0; if (corr[lag] > best) best = corr[lag];
                }
                for (int lag = 4; lag < corr.Length; lag++)
                    if (corr[lag] >= 0.9 * best && corr[lag] > 0.3 && (lag + 1 >= corr.Length || corr[lag] >= corr[lag + 1])) { period = lag; break; }
                if (period == 0) period = L;
            }
            periodCache[key] = period;
            return period;
        }
    }

    /// <summary>A SNES game's sound (its real SPC700 + S-DSP in front, running the game's sound driver) replayed on another console's enhanced sound chip: menu ids <c>SMS:SMSS</c>, <c>GG:GGS</c>, <c>MD:MDS</c>.
    /// The eight DSP voices each on a channel (a looped sample is a tone at the sample's pitch, a noise voice or a one-shot a noise), VOL L / VOL R the pan where the chip has stereo. The samples themselves and the
    /// echo are not carried (the chip plays its own waveform at their pitch).</summary>
    public sealed class SnesApuOnChip : ISnesApu, IHubVoiceSource
    {
        private const double MasterHz = 21_477_272.0;
        private readonly ISnesApu front;
        private readonly ISnesApuProbe probe;
        private readonly SnesVoiceReader reader;
        private readonly IEnhancedSoundChip chip;
        private readonly HubVoice[] voices = new HubVoice[8];
        private readonly short[] scratch = new short[8192];
        private long lastClock, nextSync;

        public string Route { get; }
        public IEnhancedSoundChip Chip => chip;
        public long Syncs { get; private set; }
        /// <summary>The real SNES audio unit inside (APU_SFC), for swapping it back in and for save states.</summary>
        public ISnesApu Front => front is SfcApuAdapter ad ? ad.Inner : front;
        internal ISnesApuProbe Probe => probe;

        /// <param name="runningFront">The game's own SPC700 + DSP (a hot swap keeps it running); null makes a fresh one.</param>
        /// <param name="masterClock">The board's time the bridge starts listening at.</param>
        public SnesApuOnChip(string route, ISnesApu? runningFront, long masterClock)
        {
            Route = route;
            front = runningFront is APU_SFC sfc ? new SfcApuAdapter(sfc) : runningFront ?? new SfcApuAdapter(new APU_SFC());
            probe = front as ISnesApuProbe ?? throw new NotSupportedException($"SNES APU '{front.CoreName}' exposes no DSP state to listen to");
            reader = new SnesVoiceReader(probe);
            chip = EnhancedChips.Sound(route, 32000);
            lastClock = masterClock; nextSync = masterClock;
            chip.EnsureExtBanks(chip.BanksFor(AllTonal()) - 1);      // the eight voices as tones (the most a SNES game sounds of one kind); a noise grows its own channels when one sounds
        }

        private static HubVoice[] AllTonal() { var a = new HubVoice[8]; for (int i = 0; i < 8; i++) a[i] = new HubVoice(HubVoiceKind.Pulse, 440, 1f); return a; }

        public string CoreName => $"{Route}+{front.CoreName}";
        public void Reset() { front.Reset(); chip.Silence(); }
        public byte ReadPort(int port) => front.ReadPort(port);
        public void WritePort(int port, byte value) => front.WritePort(port, value);
        public int SampleRate => chip.SampleRate;
        public string Describe() => $"{CoreName}: {chip.Describe()}; syncs {Syncs} | {front.Describe()}";

        /// <summary>The board's clock jumped (a save state was loaded into the unit behind the bridge): listen from <paramref name="masterClock"/> on.</summary>
        public void Rebase(long masterClock) { lastClock = masterClock; nextSync = masterClock; chip.Silence(); }

        public void RunTo(long masterClock)
        {
            const long SyncEvery = 21_477_272 / 960;
            while (lastClock < masterClock)
            {
                long t = Math.Min(masterClock, nextSync);
                front.RunTo(t);
                lastClock = t;
                chip.RunTo(t / MasterHz);
                if (t >= nextSync) { Syncs++; reader.Describe(voices); chip.Apply(voices); nextSync += SyncEvery; }
            }
            while (front.ReadSamples(scratch) > 0) { }        // the front's own sound is discarded
        }

        public int ReadSamples(Span<short> dest) => chip.ReadSamples(dest);
        public int DescribeVoices(Span<HubVoice> dest) => reader.Describe(dest);

        /// <summary>TESTS: one sync on the DSP state as it is (a state the test poked into the front's registers), without running the front.</summary>
        internal void SyncForTests() { Syncs++; reader.Describe(voices); chip.Apply(voices); }
    }
}

namespace NesEmulator
{
    using NesEmulator.Mix;
    using NesEmulator.Sega;

    /// <summary>
    /// A NES game's sound on another console's ENHANCED sound chip (the NES board's APU ids <c>SNESS</c>, <c>SMSS</c>, <c>MDS</c>: <see cref="APU_SNESS"/> and its siblings). Built like <see cref="APU_SMS"/>: a NES APU core
    /// (<see cref="MixConfig.NesFrontApu"/>, FIX) stays in front for $4015 reads, the frame IRQ and the DMC's DMA timing (its own sound is discarded); the NES channels are rebuilt from the register writes
    /// (<see cref="NesChannelModel"/>) and about 960 times a second played on the chip: pulse 1 and 2 with their duty, the triangle, the noise (its short mode where the chip has one), each on a channel of
    /// its own. The DMC has nowhere to go: its sample starts and $4011 writes are dropped and counted. Allocation-free while running.
    /// </summary>
    public abstract class NesApuOnChip : IAPU, IDmcDmaSchedulable
    {
        private const int SyncCycles = 1864;
        private const double NesHz = 1_789_773.0;
        private readonly IAPU front;
        private readonly IEnhancedSoundChip chip;
        private NesChannelModel model = new();
        private readonly HubVoice[] voices = new HubVoice[4];
        private readonly short[] pcm = new short[2048];
        private readonly float[] ring = new float[1 << 16];
        private int ringHead, ringCount, sinceSync, channelMask = 0x1F;
        private long cycles;

        public IEnhancedSoundChip Chip => chip;
        public IAPU Front => front;
        public long Syncs { get; private set; }
        public long DmcStartsDropped => model.DmcStarts;
        public long DmcDacWritesDropped { get; private set; }

        protected NesApuOnChip(Bus bus, string route)
        {
            string frontId = MixConfig.NesFrontApu;
            var t = CoreRegistry.ApuTypes.TryGetValue(frontId, out var ft) ? ft : throw new ArgumentException($"No NES APU '{frontId}'");
            if (typeof(NesApuOnChip).IsAssignableFrom(t)) throw new ArgumentException($"{GetType().Name} cannot be its own front APU");
            front = CoreRegistry.CreateInstance<IAPU>(t, bus) ?? throw new InvalidOperationException("front APU");
            chip = EnhancedChips.Sound(route, front.GetSampleRate());
            Describe(voices);
            chip.EnsureExtBanks(chip.BanksFor(voices) - 1);
        }

        public abstract string CoreName { get; }
        public abstract string Description { get; }
        public int Performance => 0; public int Rating => 1; public string Category => "Experimental";

        /// <summary>The NES's four voices as the hub's voice list, from the rebuilt channels.</summary>
        private void Describe(Span<HubVoice> v)
        {
            var m = model;
            for (int i = 0; i < 2; i++)
            {
                var p = m.P[i];
                bool on = p.Audible(i == 0) && (channelMask & (1 << i)) != 0 && p.Volume > 0;
                v[i] = new HubVoice(HubVoiceKind.Pulse, on ? NesHz / (16.0 * (p.Timer + 1)) : 0, on ? p.Volume / 15f : 0f, Duty: p.Duty & 3);
            }
            bool tri = m.TriAudible && (channelMask & 4) != 0;
            v[2] = new HubVoice(HubVoiceKind.Triangle, tri ? NesHz / (32.0 * (m.TriTimer + 1)) : 0, tri ? 0.8f : 0f);     // the triangle's one level: about a pulse at volume 12 on the NES mixer
            bool noise = m.NoiseAudible && (channelMask & 8) != 0 && m.NoiseVolume > 0;
            v[3] = new HubVoice(HubVoiceKind.Noise, noise ? NesHz / NesChannelModel.NoisePeriods[m.NoisePeriodIdx] : 0, noise ? m.NoiseVolume / 15f : 0f, ShortNoise: m.NoiseShort);
        }

        public void Step(int cpuCycles)
        {
            if (cpuCycles <= 0) return;
            front.Step(cpuCycles);
            model.ClockBulk(cpuCycles);
            cycles += cpuCycles;
            if ((sinceSync += cpuCycles) >= SyncCycles)
            {
                sinceSync = 0; Syncs++;
                chip.RunTo(cycles / NesHz);
                Describe(voices); chip.Apply(voices);
                Drain();
            }
            if (front.GetQueuedSampleCount() > 4096) { Span<float> junk = stackalloc float[1024]; while (front.ReadSamples(junk) > 0) { } }
        }

        private void Drain()
        {
            int n;
            while ((n = chip.ReadSamples(pcm)) > 0)
                for (int i = 0; i + 1 < n; i += 2)
                {
                    float v = (pcm[i] + pcm[i + 1]) * (0.5f / 32768f);
                    if (ringCount == ring.Length) { ringHead = (ringHead + 1) & (ring.Length - 1); ringCount--; }
                    ring[(ringHead + ringCount) & (ring.Length - 1)] = v;
                    ringCount++;
                }
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
            chip.RunTo(cycles / NesHz); Drain();
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
        public int GetSampleRate() => chip.SampleRate;
        public void SetEnabledChannels(int mask) { channelMask = mask; front.SetEnabledChannels(mask); }
        public object GetState() => front.GetState();
        public void SetState(object state) { front.SetState(state); model = NesChannelModelFrom(front, model); chip.Silence(); }
        public void ClearAudioBuffers() { Drain(); ringHead = ringCount = 0; front.ClearAudioBuffers(); }
        public void Reset() { front.Reset(); model = new NesChannelModel(); sinceSync = 0; ringHead = ringCount = 0; chip.Silence(); }

        /// <summary>After a state load the channel model is rebuilt from the front core's state probe (as <see cref="APU_SMS"/> does), so the chip plays what the loaded state was playing.</summary>
        internal static NesChannelModel NesChannelModelFrom(IAPU front, NesChannelModel old)
        {
            if (front is not IApuStateProbe probe) return old;
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
            m.DmcStarts = old.DmcStarts;
            return m;
        }

        public string Describe() => $"{CoreName}: {chip.Describe()} | dropped: DMC starts {DmcStartsDropped}, $4011 writes {DmcDacWritesDropped}; syncs {Syncs}";
    }
}
