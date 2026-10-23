using System;
using System.IO;
using NesEmulator.Mix;

namespace NesEmulator.Sega
{
    /// <summary>
    /// MIX LAB (Sega track C, milestone 2): a Genesis sound unit whose sound comes from a NES APU core, so a Genesis game's music can be heard on NES chips (sound unit id <c>NES</c> in
    /// <see cref="GenesisSoundFactories"/>; <c>NES:FIX</c>, <c>NES:FIXS</c> ... name the NES chip). The real <see cref="MdSoundUnit"/> runs inside it, untouched: the Z80 and its sound driver, the YM2612 (registers,
    /// timers, busy flag, DAC), the PSG, the bus handshake the 68000 sees, so a game's sound driver works as it does on the stock unit. About 960 times a second its eleven voices
    /// (<see cref="MdSoundUnit.DescribeVoices"/>: six FM channels, the DAC, three PSG tones, the PSG noise) are replayed on the NES chip through <see cref="NesVoiceWriter"/>:
    /// <list type="bullet">
    /// <item><b>Tonal voices</b> (FM 1 to 6 and PSG tones 1 to 3, nine of them) go to the NES chip's pulse slots (two per bank), and to its triangle slots (one per bank) when a voice is low or the pulses are full:
    /// <c>NES:FIX</c> has one bank (three tonal slots), <c>NES:FIXS</c> two (six). A voice keeps its slot while it sounds, a new voice takes a free one, and when none is free the quietest pulse's voice gives way if the newcomer is at least 6 dB louder; otherwise the newcomer is not played
    /// (<see cref="VoicesWithoutChannel"/> counts voice-syncs lost this way, <see cref="VoicesStolen"/> the takeovers). A triangle has no volume control, so only a voice loud enough for its fixed level goes there. The pitch is the voice's (an FM voice's loudest carrier, no vibrato), exact to the NES timer's step.</item>
    /// <item><b>Noise voices</b> (the DAC, the PSG noise) go to the noise slots, allocated the same way. The DAC is played as noise hits: its swing sets the level of a noise at a fixed rate (the DAC's samples themselves are not carried, <see cref="DacSyncs"/> counts what was replaced); the PSG noise keeps its white or periodic mode and its rate.</item>
    /// <item><b>Level</b>: each voice is replayed at the loudness (AC RMS) the stock unit gives it, in the NES's 15 volume steps (nearest in decibels), less <see cref="LevelScale"/> (the NES pulse's loudest step is 4.0 dB under a full-scale FM channel: everything is scaled alike so the balance is kept and the loud end does not clip). An FM voice's loudness is read from its carriers' total level and envelope, so it follows the game's own volume and envelope writes.</item>
    /// <item><b>Notes below the NES pulse's range</b> (54.6 Hz; the FM chip goes lower) follow <see cref="LowNotes"/> as in <see cref="APU_SMS"/>: an octave up (the default for a game) or dropped, counted.</item>
    /// </list>
    /// What it cannot carry, it counts and says so in <see cref="Describe"/>: FM timbre (every FM note is a plain pulse, <see cref="FmNotes"/>), the DAC's samples, panning (a voice on one side only plays on both, <see cref="OneSidedNotes"/>),
    /// the LFO's vibrato and tremolo (<see cref="LfoSyncs"/>; SSG-EG and CSM are not counted), voices beyond the NES chip's channels, a triangle's fixed level (<see cref="TriangleLevelsLost"/>), and voices below the NES's quietest step (<see cref="VoicesBelowNesFloor"/>). The inner unit's own output is
    /// discarded; its Z80 and chips are paused and resumed exactly as on the stock unit.
    /// </summary>
    public sealed class MdSoundOnNes : IGenesisSound, IHubVoiceSource
    {
        public const string DefaultBackApu = "FIX";
        private const int StepCycles = 32, SyncCycles = 1864;

        /// <summary>The NES pulse's loudest step against a full-scale FM carrier, as an amplitude ratio (-4.0 dB): every voice is replayed this much quieter than the stock unit plays it.</summary>
        public static readonly double LevelScale = APU_MD.PulseRms(2, 15) / FmPatchBank.CarrierRms;
        /// <summary>AC RMS the DAC's swing (0 to 1 of full scale) is replayed at, on the stock scale: a sample stream is noise-like, about 0.6 of its peak, and a full-scale 9-bit swing is 0.166 of the 16-bit range.</summary>
        public const double DacRmsPerSwing = 0.6 * 0.166;
        /// <summary>A tonal voice may take a NES triangle (a fixed level, no volume) only when it is at least this fraction of that level (0.35: not more than 9 dB below it); a quieter voice would play far too loud there and is left to the pulses.</summary>
        public const double TriangleFloor = HubReplayer.TriangleFloor;

        private readonly MdSoundUnit inner;
        private readonly NesApuHost host;
        private readonly IAPU back;
        private readonly NesVoiceWriter writer;
        private readonly double nesPerMaster;
        private readonly HubVoice[] voices = new HubVoice[MdSoundUnit.VoiceCount];
        private readonly short[] drain = new short[4096];
        private readonly NesChipOutput output;
        private double nesAcc;
        private long lastMaster;
        private int nesDue, sinceSync;

        // the allocator and replayer (shared with the Master System FM bridge): which voice sits on which NES channel, and the registers that follow; the voices' state at this and the last sync
        private readonly HubReplayer replay;
        private readonly bool[] audible = new bool[MdSoundUnit.VoiceCount], wasAudible = new bool[MdSoundUnit.VoiceCount];
        private bool dacWasOn;

        /// <summary>The NES APU core the sound comes from.</summary>
        public string BackApuId { get; }
        /// <summary>What a note below the NES pulse's range does (the same two policies as <see cref="APU_SMS"/>); <see cref="APU_MD.DefaultLowNotes"/> when the unit is made.</summary>
        public LowNotePolicy LowNotes { get => replay.LowNotes; set => replay.LowNotes = value; }
        /// <summary>The real sound unit inside (its Z80, YM2612 and PSG), for tools and tests.</summary>
        public MdSoundUnit Inner => inner;
        public NesVoiceWriter Writer => writer;

        // ---- what could not be carried over (counted, never silent) ----
        public long Syncs { get; private set; }
        /// <summary>FM notes replayed as plain pulses or triangles (the FM timbre is not carried); counted at each key-on.</summary>
        public long FmNotes { get; private set; }
        /// <summary>FM notes struck on one speaker only (the NES is mono: they play on both); counted at each key-on.</summary>
        public long OneSidedNotes { get; private set; }
        /// <summary>Syncs in which the DAC was playing something that became a noise hit (its samples are not carried).</summary>
        public long DacSyncs { get; private set; }
        /// <summary>DAC streams started (the DAC switched on).</summary>
        public long DacStreams { get; private set; }
        /// <summary>Voice-syncs in which an FM voice with vibrato or tremolo sensitivity played with the LFO on (the NES has no LFO: the voice plays steady).</summary>
        public long LfoSyncs { get; private set; }
        /// <summary>Notes below the NES range played whole octaves up, once per note.</summary>
        public long LowNotesTransposed => replay.LowNotesTransposed;
        /// <summary>Notes below the NES range that were silenced, once per note.</summary>
        public long LowNotesDropped => replay.LowNotesDropped;
        /// <summary>Voice-syncs in which an audible voice found no free NES channel and was not played.</summary>
        public long VoicesWithoutChannel => replay.VoicesWithoutChannel;
        /// <summary>Times a louder newcomer took the channel of a quieter voice.</summary>
        public long VoicesStolen => replay.VoicesStolen;
        /// <summary>Voice-syncs in which a tonal voice on a NES triangle (no volume control) was more than 1.5 dB from the triangle's fixed level.</summary>
        public long TriangleLevelsLost => replay.TriangleLevelsLost;
        /// <summary>Voice-syncs in which a voice was more than 6 dB under the NES's quietest volume step and was not played.</summary>
        public long VoicesBelowNesFloor => replay.VoicesBelowNesFloor;

        public MdSoundOnNes(SegaMachine machine, string? detail = null)
        {
            BackApuId = string.IsNullOrWhiteSpace(detail) ? DefaultBackApu : detail!;
            inner = new MdSoundUnit(machine);
            host = new NesApuHost(BackApuId);   // throws ArgumentException for an unknown NES chip: never a quiet substitute
            back = host.Apu;
            writer = new NesVoiceWriter(NesRegisterPorts.For(back), NesVoiceWriter.BanksForTonal(MdSoundUnit.VoiceCount - 2, 2));     // nine tonal voices, two noises: an enhanced chip gives 5 banks
            nesPerMaster = NesVoiceWriter.NesCpuHz / machine.Timeline.MasterHz;
            output = new NesChipOutput(back, machine.SampleRate);   // a chip with a rate of its own (SNES 32 kHz, Game Boy 48 kHz) is converted to the machine's
            replay = new HubReplayer(writer, MdSoundUnit.VoiceCount, IsNoiseVoice, APU_MD.TriangleRms, APU_MD.DefaultLowNotes);
        }

        public string CoreName => $"NES:{back.CoreName}+{inner.CoreName}";
        public int SampleRate => output.SampleRate;

        public void Attach(IGenesisSoundHost host) => inner.Attach(host);

        public void Reset()
        {
            inner.Reset();
            lastMaster = 0; nesAcc = 0; nesDue = 0; sinceSync = 0;
            output.Reset();
            ForgetVoices();
            Replay();
        }

        private void ForgetVoices()
        {
            replay.Forget();
            Array.Clear(wasAudible); dacWasOn = false;
        }

        // ---- the bus handshake and the 68000's window: the real unit's, unchanged ----
        public void WriteBusRequest(bool requested, long masterClock) => inner.WriteBusRequest(requested, masterClock);
        public bool IsBusGranted(long masterClock) => inner.IsBusGranted(masterClock);
        public void WriteReset(bool released, long masterClock) => inner.WriteReset(released, masterClock);
        public bool IsInReset => inner.IsInReset;
        public byte Read(int offset, long masterClock) => inner.Read(offset, masterClock);
        public void Write(int offset, byte value, long masterClock) => inner.Write(offset, value, masterClock);
        public void SetInterrupt(bool asserted, long masterClock) => inner.SetInterrupt(asserted, masterClock);
        public void WritePsg(byte value, long masterClock) => inner.WritePsg(value, masterClock);

        // ---- time ----

        public void RunTo(long masterClock)
        {
            inner.RunTo(masterClock);
            if (masterClock <= lastMaster) return;
            nesAcc += (masterClock - lastMaster) * nesPerMaster;
            lastMaster = masterClock;
            int c = (int)nesAcc;
            if (c <= 0) return;
            nesAcc -= c;
            nesDue += c; sinceSync += c;
            while (nesDue >= StepCycles) { back.Step(StepCycles); nesDue -= StepCycles; }
            if (sinceSync >= SyncCycles) { sinceSync = 0; Replay(); }
        }

        // ---- the replay ----

        /// <summary>The AC RMS an FM voice has on the stock unit, from its carriers' total level and envelope (the same quantities the chip attenuates them by); the carriers are taken as independent sines at full modulation-free power, so a modulator at a ratio of 1 or 2 moves the real figure by a few dB either way.</summary>
        private double FmRms(int ch)
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
            double rms = FmPatchBank.CarrierRms * Math.Sqrt(power);
            if (fm.Ladder) rms = FmPatchBank.LadderActual(rms);    // the discrete chip's ladder adds to a quiet channel: replay the loudness the unit really has
            return rms * inner.FmGain;
        }

        private static bool IsNoiseVoice(int i) => i == 6 || i == 10;     // the DAC and the PSG noise

        private void Replay()
        {
            Syncs++;
            while (inner.ReadSamples(drain) > 0) { }       // the inner unit's own sound is discarded; keep its buffers from filling
            inner.DescribeVoices(voices);
            var fm = inner.Fm;
            double psgAmp = inner.Psg.Gain;
            bool lfoOn = (fm.Register(0x22) & 8) != 0;

            // ---- what each voice is doing now: audible, at what pitch, and how loud (AC RMS on the NES's scale) ----
            for (int i = 0; i < voices.Length; i++)
            {
                var v = voices[i];
                audible[i] = v.Audible;
                replay.Wanted[i] = v.Audible;
                replay.VoiceHz[i] = v.Hz;
                replay.ShortNoise[i] = v.ShortNoise;
                replay.TargetRms[i] = 0;
                if (!v.Audible) continue;
                double rms;
                if (i < 6) rms = FmRms(i);
                else if (i == 6) { rms = DacRmsPerSwing * v.Level * inner.FmGain; DacSyncs++; }
                else rms = 0.5 * psgAmp * v.Level;
                replay.TargetRms[i] = rms * LevelScale;
                if (i < 6)
                {
                    int choffs = i % 3 + 0x100 * (i / 3);
                    if (!wasAudible[i])
                    {
                        FmNotes++;
                        if ((fm.Register(0xB4 + choffs) >> 6 & 3) != 3) OneSidedNotes++;
                    }
                    if (lfoOn && (fm.Register(0xB4 + choffs) & 0x37) != 0) LfoSyncs++;
                }
            }
            bool dacOn = fm.DacEnabled;
            if (dacOn && !dacWasOn) DacStreams++;
            dacWasOn = dacOn;
            for (int i = 0; i < voices.Length; i++) wasAudible[i] = audible[i];

            replay.Replay();       // the NES channels (HubReplayer: the allocation policy, levels, low notes) and the registers
        }

        // ---- sound out: the NES chip's, mono, on both sides ----

        public int ReadSamples(Span<short> dest) => output.ReadStereo(dest);

        /// <summary>The inner unit's eleven voices (what the bridge replays); reading them clears the DAC's swing, so a probe should not interleave with the running bridge.</summary>
        public int DescribeVoices(Span<HubVoice> dest) => inner.DescribeVoices(dest);

        public void SaveState(BinaryWriter w) => inner.SaveState(w);

        public void LoadState(BinaryReader r)
        {
            inner.LoadState(r);
            // the NES side has no state of its own: it starts from the clock the loaded unit stands at (not from 0, which would run the chip through all the time since power-on at the first RunTo and hand the reader
            // that much extra sound after a live swap or a load) and from an empty output, then replays the voices
            lastMaster = inner.Clock; nesAcc = 0; nesDue = 0; sinceSync = 0;
            output.Reset();
            // a chip's sample pipeline has a start-up of its own (a few samples, 0.2 ms for the Master System's): it is spent here, silently, so that the first frames after a swap or a load deliver as many samples as the
            // ones after them (a session that is built fresh spends it before the first frame is read)
            for (int i = 0, spent = 0; i < 2048 && spent < 64; i++)         // until the chip has made 64 samples (a few hundred micro-seconds of its time at most for the chips that run at the machine's rate)
            {
                back.Step(StepCycles);
                int n; while ((n = output.ReadStereo(drain)) > 0) spent += n / 2;
            }
            ForgetVoices();
            Replay();
        }

        public string Describe() =>
            $"{CoreName}: {inner.Describe()} | not carried: FM timbre ({FmNotes} FM notes played as plain pulses), DAC samples ({DacSyncs} syncs as noise hits, {DacStreams} streams), one-sided pan ({OneSidedNotes} notes), LFO ({LfoSyncs} voice-syncs), " +
            $"voices without a NES channel {VoicesWithoutChannel} (stolen {VoicesStolen}), triangle fixed level {TriangleLevelsLost}, below the NES's quietest step {VoicesBelowNesFloor}, low notes transposed {LowNotesTransposed} / dropped {LowNotesDropped}; syncs {Syncs}, NES register writes {writer.Writes}";
    }
}
