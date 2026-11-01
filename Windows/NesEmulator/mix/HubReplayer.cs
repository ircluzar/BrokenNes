using System;
using NesEmulator.Sega;

namespace NesEmulator.Mix
{
    /// <summary>
    /// The NES-channel allocator and replayer shared by the sound bridges that carry more voices than a NES chip has channels (<see cref="MdSoundOnNes"/>: eleven Genesis voices; <see cref="SmsFmSoundOnNes"/>: eighteen
    /// Master System voices). Each sync (about 960 times a second) the unit tells it which of its voices sound, at what pitch and loudness; it gives them NES channels by a fixed, deterministic policy and writes the result
    /// through the <see cref="NesVoiceWriter"/>. What it cannot carry it counts, never hides:
    /// <list type="number">
    /// <item><b>Held voices keep their channel</b> for as long as they sound (no flutter between slots), and a channel is freed the sync its voice stops.</item>
    /// <item><b>Newcomers are placed loudest first</b> (a tie goes to the lower voice number). A noise voice takes a free noise channel; a tonal voice takes a free pulse, except that a note below the pulse range (54.6 Hz) takes a free triangle first when the
    /// voice is loud enough for the triangle's one fixed level (at least <see cref="TriangleFloor"/> of it) and above the triangle's range (27.3 Hz); a loud-enough voice also takes a free triangle when the pulses are full.</item>
    /// <item><b>Stealing:</b> with no channel free, a newcomer takes the channel of the quietest voice on the same kind of channel (pulses for a tonal newcomer, noises for a noise one; the lowest channel on a tie) when it is more than 6 dB (twice the amplitude) louder; otherwise
    /// the newcomer is not played and <see cref="Allocator"/>'s <c>WithoutChannel</c> counts the sync. Triangles are never stolen from (their level cannot follow the voice).</item>
    /// <item><b>Level:</b> the voice's target loudness (AC RMS on the NES mixer's scale, set by the unit) becomes the NES volume step whose loudness is nearest in decibels; a voice more than 6 dB under the quietest step is not played (<see cref="VoicesBelowNesFloor"/>);
    /// a triangle plays at its fixed level (<see cref="TriangleLevelsLost"/> counts syncs where that is more than 1.5 dB off the voice).</item>
    /// <item><b>Notes below the channel's range</b> follow <see cref="LowNotes"/> (whole octaves up, the default for a game, or dropped), counted once per note.</item>
    /// </list>
    /// </summary>
    public sealed class HubReplayer
    {
        /// <summary>A tonal voice may take a NES triangle (a fixed level, no volume) only when it is at least this fraction of that level (0.35: not more than 9 dB below it).</summary>
        public const double TriangleFloor = 0.35;
        private static readonly double PulseMinHz = NesVoiceWriter.NesCpuHz / (16.0 * 2048), TriangleMinHz = NesVoiceWriter.NesCpuHz / (32.0 * 2048);

        private readonly NesVoiceWriter writer;
        private readonly HubVoice[] output;
        private readonly bool[] noise;
        private readonly bool[] lowFlag;

        /// <summary>Per voice, set by the unit before <see cref="Replay"/>: sounding, pitch (Hz), loudness (AC RMS on the NES mixer's scale) and, for a noise voice, whether it is the short, buzzy mode.</summary>
        public bool[] Wanted { get; }
        public double[] VoiceHz { get; }
        public double[] TargetRms { get; }
        public bool[] ShortNoise { get; }
        public HubSlotAllocator Allocator { get; }

        /// <summary>What a note below the NES pulse's range does.</summary>
        public LowNotePolicy LowNotes { get; set; }
        public long LowNotesTransposed { get; private set; }
        public long LowNotesDropped { get; private set; }
        public long VoicesBelowNesFloor { get; private set; }
        public long TriangleLevelsLost { get; private set; }
        public long VoicesWithoutChannel => Allocator.WithoutChannel;
        public long VoicesStolen => Allocator.Stolen;

        /// <param name="isNoiseVoice">Which of the unit's voices are noise voices (they use the noise channels).</param>
        /// <param name="triangleRms">The NES triangle's fixed AC RMS on the same scale as the voices' targets.</param>
        public HubReplayer(NesVoiceWriter writer, int voiceCount, Func<int, bool> isNoiseVoice, double triangleRms, LowNotePolicy lowNotes)
        {
            this.writer = writer;
            LowNotes = lowNotes;
            Wanted = new bool[voiceCount]; VoiceHz = new double[voiceCount]; TargetRms = new double[voiceCount]; ShortNoise = new bool[voiceCount];
            noise = new bool[voiceCount]; for (int i = 0; i < voiceCount; i++) noise[i] = isNoiseVoice(i);
            lowFlag = new bool[voiceCount];
            Allocator = new HubSlotAllocator(writer.PulseSlots, writer.TriangleSlots, writer.NoiseSlots, PulseMinHz, TriangleMinHz, triangleRms, TriangleFloor, voiceCount, i => noise[i]);
            output = new HubVoice[writer.PulseSlots + writer.TriangleSlots + writer.NoiseSlots];
            triRms = triangleRms;
        }

        private readonly double triRms;

        /// <summary>Forget which voice sits where (a reset or a loaded state).</summary>
        public void Forget() { Allocator.Forget(); Array.Clear(lowFlag); }

        /// <summary>The NES pulse volume (1 to 15) whose loudness (AC RMS, 50% duty) is nearest, in decibels, to <paramref name="rms"/>; 0 when it is more than 6 dB under the quietest step.</summary>
        public static int PulseVolumeFor(double rms)
        {
            if (!(rms > 0) || rms < APU_MD.PulseRms(2, 1) * 0.5) return 0;
            int best = 1; double bd = double.MaxValue;
            for (int v = 1; v <= 15; v++) { double d = Math.Abs(Math.Log(APU_MD.PulseRms(2, v) / rms)); if (d < bd) { bd = d; best = v; } }
            return best;
        }

        /// <summary>The same for the NES noise (AC RMS of a binary noise is half its amplitude).</summary>
        public static int NoiseVolumeFor(double rms)
        {
            if (!(rms > 0) || rms < 0.5 * 0.5 * APU_SMS.NoiseOut(1)) return 0;
            int best = 1; double bd = double.MaxValue;
            for (int v = 1; v <= 15; v++) { double d = Math.Abs(Math.Log(0.5 * APU_SMS.NoiseOut(v) / rms)); if (d < bd) { bd = d; best = v; } }
            return best;
        }

        /// <summary>Allocate the sounding voices NES channels and write the chip's registers. Call after filling <see cref="Wanted"/>, <see cref="VoiceHz"/>, <see cref="TargetRms"/> and <see cref="ShortNoise"/>.</summary>
        public void Replay()
        {
            for (int i = 0; i < Wanted.Length; i++) if (!Wanted[i]) lowFlag[i] = false;     // a low note counts once, when it starts
            Allocator.Allocate(Wanted, TargetRms, VoiceHz);
            var tonalOwner = Allocator.TonalOwner; var noiseOwner = Allocator.NoiseOwner;
            int pulses = writer.PulseSlots;
            for (int slot = 0; slot < tonalOwner.Length; slot++)
            {
                bool tri = slot >= pulses;
                var kind = tri ? HubVoiceKind.Triangle : HubVoiceKind.Pulse;
                int owner = tonalOwner[slot];
                if (owner < 0) { output[slot] = new HubVoice(kind, 0, 0f); continue; }
                double hz = VoiceHz[owner], min = tri ? TriangleMinHz : PulseMinHz;
                if (hz < min)
                {
                    // below the NES channel's range: the policy (counted once per note)
                    if (!lowFlag[owner]) { lowFlag[owner] = true; if (LowNotes == LowNotePolicy.Drop) LowNotesDropped++; else LowNotesTransposed++; }
                    if (LowNotes == LowNotePolicy.Drop) { output[slot] = new HubVoice(kind, 0, 0f); continue; }
                    while (hz < min) hz *= 2;
                }
                else lowFlag[owner] = false;
                float level;
                if (tri)
                {
                    level = 1f;
                    if (Math.Abs(Math.Log(TargetRms[owner] / triRms)) > 0.1727) TriangleLevelsLost++;   // 1.5 dB
                }
                else
                {
                    int vol = PulseVolumeFor(TargetRms[owner]);
                    if (vol == 0) { VoicesBelowNesFloor++; output[slot] = new HubVoice(kind, 0, 0f); continue; }
                    level = vol / 15f;
                }
                output[slot] = new HubVoice(kind, hz, level, Duty: 2);
            }
            int at = tonalOwner.Length;
            for (int slot = 0; slot < noiseOwner.Length; slot++)
            {
                int owner = noiseOwner[slot];
                if (owner < 0) { output[at + slot] = new HubVoice(HubVoiceKind.Noise, 0, 0f); continue; }
                int vol = NoiseVolumeFor(TargetRms[owner]);
                if (vol == 0) { VoicesBelowNesFloor++; output[at + slot] = new HubVoice(HubVoiceKind.Noise, 0, 0f); continue; }
                output[at + slot] = new HubVoice(HubVoiceKind.Noise, VoiceHz[owner], vol / 15f, ShortNoise: ShortNoise[owner]);
            }
            writer.Apply(output);
        }
    }

    /// <summary>The allocation half of <see cref="HubReplayer"/> (see its summary for the policy): which voice sits on which NES channel, deterministic, with the drops and takeovers counted.</summary>
    public sealed class HubSlotAllocator
    {
        private readonly int pulses, voiceCount;
        private readonly double pulseMinHz, triangleMinHz, triangleRms, triangleFloor;
        private readonly Func<int, bool> isNoise;

        /// <summary>Per channel: the voice on it, -1 when free. Tonal channels are the pulses, then the triangles; noise channels are separate.</summary>
        public int[] TonalOwner { get; }
        public int[] NoiseOwner { get; }
        /// <summary>Voice-syncs in which an audible voice found no free NES channel (and no quieter voice to replace) and was not played.</summary>
        public long WithoutChannel { get; private set; }
        /// <summary>Times a louder newcomer took the channel of a quieter voice.</summary>
        public long Stolen { get; private set; }

        public HubSlotAllocator(int pulseSlots, int triangleSlots, int noiseSlots, double pulseMinHz, double triangleMinHz, double triangleRms, double triangleFloor, int voiceCount, Func<int, bool> isNoiseVoice)
        {
            pulses = pulseSlots; this.voiceCount = voiceCount;
            this.pulseMinHz = pulseMinHz; this.triangleMinHz = triangleMinHz; this.triangleRms = triangleRms; this.triangleFloor = triangleFloor;
            isNoise = isNoiseVoice;
            TonalOwner = new int[pulseSlots + triangleSlots]; NoiseOwner = new int[noiseSlots];
            Forget();
        }

        public void Forget() { Array.Fill(TonalOwner, -1); Array.Fill(NoiseOwner, -1); }

        private bool[] wanted = Array.Empty<bool>();
        private double[] rms = Array.Empty<double>(), hz = Array.Empty<double>();

        /// <summary>Gives the sounding voices NES channels: held voices keep theirs, new ones take a free one, and with none free the quietest pulse's voice gives way to a newcomer 6 dB louder. <paramref name="wanted"/> is cleared for a voice that got none.</summary>
        public void Allocate(bool[] wanted, double[] targetRms, double[] voiceHz)
        {
            this.wanted = wanted; rms = targetRms; hz = voiceHz;
            // free the slots of voices that stopped
            for (int s = 0; s < TonalOwner.Length; s++) if (TonalOwner[s] >= 0 && !wanted[TonalOwner[s]]) TonalOwner[s] = -1;
            for (int s = 0; s < NoiseOwner.Length; s++) if (NoiseOwner[s] >= 0 && !wanted[NoiseOwner[s]]) NoiseOwner[s] = -1;

            // newcomers, loudest first. Every pass places or drops exactly one voice, so voiceCount passes always suffice: the bound is a guard against a policy bug ever hanging the audio thread.
            for (int pass = 0; pass < voiceCount; pass++)
            {
                int pick = -1;
                for (int i = 0; i < voiceCount; i++)
                    if (wanted[i] && !Holds(i) && (pick < 0 || rms[i] > rms[pick])) pick = i;
                if (pick < 0) break;
                if (!Place(pick)) { WithoutChannel++; wanted[pick] = false; }
            }
        }

        private bool Holds(int voice)
        {
            var owners = isNoise(voice) ? NoiseOwner : TonalOwner;
            foreach (int o in owners) if (o == voice) return true;
            return false;
        }

        private bool Place(int voice)
        {
            if (isNoise(voice))
            {
                for (int s = 0; s < NoiseOwner.Length; s++) if (NoiseOwner[s] < 0) { NoiseOwner[s] = voice; return true; }
                return Steal(NoiseOwner, voice, 0, NoiseOwner.Length);
            }
            bool triangleOk = rms[voice] >= triangleRms * triangleFloor;      // loud enough for the triangle's one level
            // a note below the pulse range goes to a free triangle (exact pitch down to 27 Hz) when it can; everything else prefers a pulse
            if (triangleOk && hz[voice] < pulseMinHz && hz[voice] >= triangleMinHz)
                for (int s = pulses; s < TonalOwner.Length; s++) if (TonalOwner[s] < 0) { TonalOwner[s] = voice; return true; }
            for (int s = 0; s < pulses; s++) if (TonalOwner[s] < 0) { TonalOwner[s] = voice; return true; }
            if (triangleOk)
                for (int s = pulses; s < TonalOwner.Length; s++) if (TonalOwner[s] < 0) { TonalOwner[s] = voice; return true; }
            return Steal(TonalOwner, voice, 0, pulses);           // the pulses are full: take the quietest pulse's channel if this voice is much louder
        }

        private bool Steal(int[] owners, int voice, int from, int to)
        {
            int quiet = -1;
            for (int s = from; s < to; s++) if (owners[s] >= 0 && (quiet < 0 || rms[owners[s]] < rms[owners[quiet]])) quiet = s;
            if (quiet < 0 || !(rms[voice] > 2.0 * rms[owners[quiet]])) return false;     // 6 dB
            owners[quiet] = voice;
            Stolen++;
            return true;
        }
    }
}
