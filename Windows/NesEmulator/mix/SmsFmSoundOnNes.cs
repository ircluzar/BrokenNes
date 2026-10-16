using System;
using System.IO;
using NesEmulator.Mix;

namespace NesEmulator.Sega
{
    /// <summary>
    /// A Master System sound unit with the FM chip whose sound comes from a NES APU core: the Japanese console's PSG <b>and YM2413</b> replayed on NES chips (sound unit id <c>NES-FM</c> in <see cref="SmsSoundFactories"/>;
    /// <c>NES-FM:FIX</c>, <c>NES-FM:FIXS</c> ... name the NES chip, a bare <c>NES-FM</c> means FIX). The real <see cref="SmsFmSound"/> runs inside it, untouched (<see cref="HasFm"/> is true, so a game's FM detection finds the chip and plays its FM music; the audio
    /// control register's FM-on and PSG-mute bits are honoured); its own output is discarded. About 960 times a second its eighteen voices (<see cref="SmsFmSound.DescribeVoices"/>: the PSG's three tones and noise, the YM2413's nine melodic channels and its
    /// five rhythm voices) are given NES channels by <see cref="HubReplayer"/> (the shared policy: held voices keep their channel, newcomers loudest first, a 6 dB louder newcomer replaces the quietest pulse, the rest are dropped and counted) and written to the chip.
    /// <para>What it cannot carry, it counts (<see cref="Describe"/>): FM timbre (every FM note is a plain pulse: <see cref="FmNotes"/>; the rhythm voices become a triangle, a pulse and noises), voices beyond the chip's channels (<see cref="VoicesWithoutChannel"/>: <c>NES:FIX</c> has three tonal
    /// channels and one noise for eighteen voices, <c>NES:FIXS</c> six and two), a triangle's fixed level, voices under the NES's quietest step, notes below its range, the YM2413's vibrato and tremolo, the Game Gear's stereo (there is no FM on a Game Gear).</para>
    /// </summary>
    public sealed class SmsFmSoundOnNes : ISmsSound, IHubVoiceSource
    {
        public const string DefaultBackApu = "FIX";
        private const int StepCycles = 32, SyncCycles = 1864;
        private const int PsgVoices = 4, VoiceTotal = PsgVoices + Ym2413.VoiceCount;

        /// <summary>The AC RMS of a full-volume YM2413 channel on the stock unit's scale (it peaks at about 2,042 of 32,768; read as a sine).</summary>
        public const double FmFullRms = 2042.0 / 32768.0 / 1.41421356237;

        private readonly SmsFmSound inner;
        private readonly NesApuHost host;
        private readonly IAPU back;
        private readonly NesVoiceWriter writer;
        private readonly double nesPerClock;
        private readonly HubVoice[] voices = new HubVoice[VoiceTotal];
        private readonly HubReplayer replay;
        private readonly bool[] wasAudible = new bool[VoiceTotal];
        private readonly short[] drain = new short[4096];
        private readonly NesChipOutput output;
        private double nesAcc;
        private int nesDue, sinceSync;

        /// <summary>The NES APU core the sound comes from.</summary>
        public string BackApuId { get; }
        /// <summary>The real unit inside (the PSG and the YM2413), for tools and tests.</summary>
        public SmsFmSound Inner => inner;
        public NesVoiceWriter Writer => writer;
        public HubReplayer Replayer => replay;
        /// <summary>What a note below the NES pulse's range does (the same two policies as <see cref="APU_SMS"/>).</summary>
        public LowNotePolicy LowNotes { get => replay.LowNotes; set => replay.LowNotes = value; }

        public long Syncs { get; private set; }
        /// <summary>FM notes replayed as plain pulses or triangles (the FM timbre is not carried); counted at each key-on.</summary>
        public long FmNotes { get; private set; }
        /// <summary>Voice-syncs in which a rhythm voice sounded (it is replayed as a triangle, a pulse or a noise).</summary>
        public long RhythmSyncs { get; private set; }
        /// <summary>Game Gear stereo-port writes that left some voice on one side only (the NES is mono).</summary>
        public long StereoWritesLossy { get; private set; }
        public long VoicesWithoutChannel => replay.VoicesWithoutChannel;
        public long VoicesStolen => replay.VoicesStolen;
        public long TriangleLevelsLost => replay.TriangleLevelsLost;
        public long VoicesBelowNesFloor => replay.VoicesBelowNesFloor;
        public long LowNotesTransposed => replay.LowNotesTransposed;
        public long LowNotesDropped => replay.LowNotesDropped;

        public SmsFmSoundOnNes(SmsSoundRequest request)
        {
            BackApuId = string.IsNullOrWhiteSpace(request.Detail) ? DefaultBackApu : request.Detail!;
            inner = new SmsFmSound(request.CpuClockHz, 44100, request.Variant);
            host = new NesApuHost(BackApuId);   // throws ArgumentException for an unknown NES chip: never a quiet substitute
            back = host.Apu;
            writer = new NesVoiceWriter(NesRegisterPorts.For(back));
            nesPerClock = NesVoiceWriter.NesCpuHz / request.CpuClockHz;
            output = new NesChipOutput(back, request.Machine.SampleRate);   // a chip with a rate of its own (SNES 32 kHz, Game Boy 48 kHz) is converted to the machine's
            replay = new HubReplayer(writer, VoiceTotal, IsNoiseVoice, APU_MD.TriangleRms, APU_SMS.DefaultLowNotes);
        }

        /// <summary>The PSG noise (voice 3) and the rhythm snare, cymbal and hi-hat.</summary>
        private static bool IsNoiseVoice(int i) => i == 3 || i == PsgVoices + 10 || i == PsgVoices + 12 || i == PsgVoices + 13;

        public string CoreName => $"NES-FM:{back.CoreName}+{inner.CoreName}";
        public int SampleRate => output.SampleRate;

        public void Reset()
        {
            inner.Reset();
            replay.Forget(); Array.Clear(wasAudible);
            nesAcc = 0; nesDue = 0; sinceSync = 0;
            output.Reset();
            Replay();
        }

        public void Run(int cpuClocks)
        {
            inner.Run(cpuClocks);
            nesAcc += cpuClocks * nesPerClock;
            int c = (int)nesAcc;
            if (c <= 0) return;
            nesAcc -= c;
            nesDue += c; sinceSync += c;
            while (nesDue >= StepCycles) { back.Step(StepCycles); nesDue -= StepCycles; }
            if (sinceSync >= SyncCycles) { sinceSync = 0; Replay(); }
        }

        /// <summary>The scale every voice is replayed by: the NES pulse's loudest step against a full-volume PSG tone (the louder of the unit's two chips), so the balance between the chips is the stock unit's and nothing clips.</summary>
        private double LevelScale => APU_MD.PulseRms(2, 15) / Math.Max(0.5 * inner.Psg.Gain * inner.PsgGain, 1e-9);

        private void Replay()
        {
            Syncs++;
            while (inner.ReadSamples(drain) > 0) { }       // the inner unit's own sound is discarded; keep its buffers from filling
            inner.DescribeVoices(voices);
            double scale = Math.Min(1.0, LevelScale), psgAmp = inner.Psg.Gain * inner.PsgGain, fmAmp = FmFullRms * inner.FmGain;
            for (int i = 0; i < VoiceTotal; i++)
            {
                var v = voices[i];
                replay.Wanted[i] = v.Audible;
                replay.VoiceHz[i] = v.Hz;
                replay.ShortNoise[i] = v.ShortNoise;
                replay.TargetRms[i] = 0;
                if (!v.Audible) { wasAudible[i] = false; continue; }
                double rms = i < PsgVoices ? 0.5 * psgAmp * v.Level : fmAmp * v.Level;
                replay.TargetRms[i] = rms * scale;
                if (!wasAudible[i])
                {
                    if (i >= PsgVoices && i < PsgVoices + 9) FmNotes++;
                    wasAudible[i] = true;
                }
                if (i >= PsgVoices + 9) RhythmSyncs++;
            }
            replay.Replay();
        }

        public void WritePsg(byte value) => inner.WritePsg(value);

        public void WriteStereo(byte value)
        {
            inner.WriteStereo(value);
            for (int bit = 0; bit < 4; bit++)
                if (((value >> (4 + bit)) & 1) != ((value >> bit) & 1)) { StereoWritesLossy++; break; }
        }

        // ---- the FM chip is the real one: the game sees it and writes to it as on the Japanese console ----
        public bool HasFm => inner.HasFm;
        public void WriteFmAddress(byte value) => inner.WriteFmAddress(value);
        public void WriteFmData(byte value) => inner.WriteFmData(value);
        public void WriteAudioControl(byte value) => inner.WriteAudioControl(value);
        public byte ReadAudioControl() => inner.ReadAudioControl();

        public int ReadSamples(Span<short> dest) => output.ReadStereo(dest);

        /// <summary>The inner unit's eighteen voices (what the bridge replays).</summary>
        public int DescribeVoices(Span<HubVoice> dest) => inner.DescribeVoices(dest);

        public void SaveState(BinaryWriter w) => inner.SaveState(w);

        public void LoadState(BinaryReader r)
        {
            inner.LoadState(r);
            replay.Forget(); Array.Clear(wasAudible);
            Replay();
        }

        public string Describe() =>
            $"{CoreName}: {inner.Describe()} | not carried: FM timbre ({FmNotes} FM notes as plain pulses, {RhythmSyncs} rhythm-voice syncs as a triangle, a pulse and noises), one-sided stereo writes {StereoWritesLossy}, " +
            $"voices without a NES channel {VoicesWithoutChannel} (stolen {VoicesStolen}), triangle fixed level {TriangleLevelsLost}, below the NES's quietest step {VoicesBelowNesFloor}, low notes transposed {LowNotesTransposed} / dropped {LowNotesDropped}; syncs {Syncs}, NES register writes {writer.Writes}";
    }
}
