using System;
using System.Linq;
using NesEmulator.Sega;

namespace NesEmulator.Mix
{
    /// <summary>
    /// THE ENHANCED SOUND CHIP OF A CONSOLE THAT IS NOT THE NES (docs/projects/enhanced-cores.md, section 4.2 and 5): the SNES's S-DSP, the Master System's / Game Gear's SN76489, the
    /// Genesis's YM2612 + PSG, each in as many banks as a guest's voices need, playing the hub's voice list (<see cref="HubVoice"/>, the description every guest already gives through
    /// <see cref="IHubVoiceSource"/>) directly on the chip's own channels: no NES registers in between, so what the NES hub loses (the pan, the duty of a pulse on a chip that has it, a
    /// channel for every voice) is kept where the chip can keep it. Bank 0 is the chip's first set; banks 1.. are more sets of the same channels (<see cref="IApuExtChannels"/>), made when
    /// a voice first plays on them. A guest's bridge (<see cref="MdSoundOnChip"/>, <see cref="SmsSoundOnChip"/>, <see cref="GbApuOnChip"/>, <see cref="SnesApuOnChip"/>, <see cref="NesApuOnChip"/>)
    /// describes its voices about 960 times a second and hands them to <see cref="Apply"/>; the chip runs on the guest's time (<see cref="RunTo"/>) and delivers stereo at the rate it was made for.
    /// </summary>
    public interface IEnhancedSoundChip : IApuExtChannels
    {
        /// <summary>The chip's name as the menus say it ("SNES enhanced").</summary>
        string EnhancedName { get; }
        /// <summary>What one bank holds, in words ("8 S-DSP voices").</summary>
        string BankShape { get; }
        /// <summary>Banks on offer now (1 + <see cref="IApuExtChannels.ExtBanks"/>).</summary>
        int Banks { get; }
        /// <summary>The most banks the chip can have.</summary>
        int MaxBanks { get; }
        /// <summary>Banks a voice has played on (or that were written): the ones the chip runs.</summary>
        int BanksInUse { get; }
        /// <summary>The banks that carry every voice of <paramref name="inventory"/> on a channel of its own (each voice counted as sounding): the bank arithmetic of the chip's channel kinds.</summary>
        int BanksFor(ReadOnlySpan<HubVoice> inventory);
        /// <summary>Plays the voices: a voice keeps its channel while it sounds, a new one takes a free channel of its kind (a bank is added when none is free, up to <see cref="MaxBanks"/>); only past the
        /// last bank is a voice dropped, and counted (<see cref="Dropped"/>).</summary>
        void Apply(ReadOnlySpan<HubVoice> voices);
        /// <summary>Runs every bank to <paramref name="seconds"/> of the guest's time (never backwards).</summary>
        void RunTo(double seconds);
        /// <summary>The rate <see cref="ReadSamples"/> delivers (the rate the chip was made for).</summary>
        int SampleRate { get; }
        /// <summary>Stereo interleaved 16-bit samples made since the last call; returns the number of shorts written (always even).</summary>
        int ReadSamples(Span<short> dest);
        /// <summary>Voice-syncs in which an audible voice found no channel (past the last bank).</summary>
        long Dropped { get; }
        /// <summary>Voices on a channel in the last <see cref="Apply"/>, and the most at once.</summary>
        int VoicesPlaying { get; }
        int VoicesPlayingMax { get; }
        /// <summary>Notes the chip could not reach (a pitch above its range: silent) and notes moved whole octaves up into its range, counted once per note.</summary>
        long NotesOutOfRange { get; }
        long NotesTransposed { get; }
        /// <summary>Silences every channel and forgets which voice sits where (a reset or a loaded state); the banks stay.</summary>
        void Silence();
        /// <summary>The guest's clock jumped (a state was loaded, the machine was reset): the chip goes on from <paramref name="seconds"/> without running the time between.</summary>
        void Rebase(double seconds);
        string Describe();
    }

    /// <summary>
    /// The shared half of the enhanced sound chips: which voice sits on which channel, as many banks as that takes, and the counters. Channels come in kinds (<see cref="ClassOf"/>: the S-DSP has one, eight a bank;
    /// the PSG two, three tones and a noise; the Genesis three, six FM channels, three PSG tones and a noise); a voice keeps its channel while it sounds and while its kind does not change, a newcomer (loudest first)
    /// takes the lowest free channel of its kind, and a bank is added (up to <see cref="MaxBanks"/>) only when no channel of that kind is free. Allocation-free once warm.
    /// </summary>
    public abstract class HubVoiceChip : IEnhancedSoundChip
    {
        public const int MaxVoices = 128;
        private readonly int[] perBank;
        private readonly int[][] owner;                    // [kind][bank * perBank + channel] -> voice index, -1 free
        private readonly int[] voiceKind = new int[MaxVoices], voiceChannel = new int[MaxVoices], wantKind = new int[MaxVoices];
        private readonly bool[] fresh = new bool[MaxVoices];
        private readonly bool[] made;
        private int banks = 1, madeCount;

        public int MaxBanks { get; }
        public int SampleRate { get; }
        public int Banks => banks;
        public int BanksInUse => madeCount;
        public long Dropped { get; private set; }
        public int VoicesPlaying { get; private set; }
        public int VoicesPlayingMax { get; private set; }
        public long NotesOutOfRange { get; protected set; }
        public long NotesTransposed { get; protected set; }
        public long Applies { get; private set; }
        public abstract string EnhancedName { get; }
        public abstract string BankShape { get; }

        /// <param name="channelsPerBank">The channels of each kind a bank has.</param>
        protected HubVoiceChip(int sampleRate, int maxBanks, params int[] channelsPerBank)
        {
            SampleRate = sampleRate; MaxBanks = maxBanks; perBank = channelsPerBank;
            owner = new int[channelsPerBank.Length][];
            for (int k = 0; k < owner.Length; k++) { owner[k] = new int[maxBanks * channelsPerBank[k]]; Array.Fill(owner[k], -1); }
            made = new bool[maxBanks];
            Array.Fill(voiceKind, -1);
        }

        /// <summary>The kind of channel a voice needs (0 .. kinds - 1).</summary>
        protected abstract int ClassOf(in HubVoice v);
        /// <summary>Make bank <paramref name="bank"/>'s chips (its first use; bank 0 is made by the constructor of the derived chip, through <see cref="MakeFirstBank"/>).</summary>
        protected abstract void MakeBank(int bank);
        /// <summary>A voice newly on channel (bank, kind, channel): key it on.</summary>
        protected abstract void Start(int bank, int kind, int channel, in HubVoice v);
        /// <summary>A voice on its channel, every sync while it sounds (right after <see cref="Start"/> the first time): pitch, level, pan.</summary>
        protected abstract void Play(int bank, int kind, int channel, in HubVoice v);
        /// <summary>The channel's voice stopped (or moved): silence it.</summary>
        protected abstract void Stop(int bank, int kind, int channel);
        /// <summary>After every channel of a sync was written (the S-DSP keys its new voices on together).</summary>
        protected virtual void EndApply() { }
        /// <summary>A raw register write of a bank, addressed as the chip's own (<see cref="IApuExtChannels.WriteExtRegister"/>; bank 0 too).</summary>
        protected abstract void WriteRaw(int bank, ushort address, byte value);
        public abstract void RunTo(double seconds);
        public abstract void Rebase(double seconds);
        public abstract int ReadSamples(Span<short> dest);
        /// <summary>The derived chip silences its channels' registers (the base has already forgotten its owners).</summary>
        protected abstract void SilenceAll();

        protected bool IsMade(int bank) => made[bank];

        /// <summary>Called by the derived constructor once its fields are ready: bank 0 is the chip's own set, there from the start.</summary>
        protected void MakeFirstBank() { if (!made[0]) { MakeBank(0); made[0] = true; madeCount = 1; } }

        private void Ensure(int bank)
        {
            if (made[bank]) return;
            MakeBank(bank); made[bank] = true; madeCount++;
        }

        // ---- IApuExtChannels ----
        public int ExtBanks => banks - 1;
        public int MaxExtBanks => MaxBanks - 1;
        public int EnsureExtBanks(int wanted) { banks = Math.Clamp(Math.Max(banks, wanted + 1), 1, MaxBanks); return banks - 1; }
        public void WriteExtRegister(int bank, ushort address, byte value)
        {
            if (bank < 1 || bank >= banks) throw new ArgumentOutOfRangeException(nameof(bank), $"{EnhancedName} has extra banks 1 to {banks - 1} (EnsureExtBanks adds more, up to {MaxBanks - 1})");
            Ensure(bank);
            WriteRaw(bank, address, value);
        }
        /// <summary>A raw register write of bank 0 (tests, tools).</summary>
        public void WriteRegister(ushort address, byte value) { Ensure(0); WriteRaw(0, address, value); }

        public int BanksFor(ReadOnlySpan<HubVoice> inventory)
        {
            Span<int> count = stackalloc int[perBank.Length];
            foreach (var v in inventory) count[ClassOf(v)]++;
            int need = 1;
            for (int k = 0; k < perBank.Length; k++) need = Math.Max(need, (count[k] + perBank[k] - 1) / perBank[k]);
            return Math.Min(need, MaxBanks);
        }

        public void Apply(ReadOnlySpan<HubVoice> voices)
        {
            Applies++;
            int n = Math.Min(voices.Length, MaxVoices);
            // 1. the kind each voice wants now (-1: silent); a voice that went quiet, changed kind or left the list gives its channel back
            for (int i = 0; i < MaxVoices; i++)
            {
                int want = i < n && voices[i].Audible ? ClassOf(voices[i]) : -1;
                wantKind[i] = want;
                fresh[i] = false;
                if (voiceKind[i] >= 0 && voiceKind[i] != want) Release(i);
            }
            // 2. newcomers, loudest first, to the lowest free channel of their kind (a bank more when none is free)
            for (int pass = 0; pass < n; pass++)
            {
                int pick = -1;
                for (int i = 0; i < n; i++)
                    if (wantKind[i] >= 0 && voiceKind[i] < 0 && (pick < 0 || voices[i].Level > voices[pick].Level)) pick = i;
                if (pick < 0) break;
                int k = wantKind[pick], ch = FreeChannel(k);
                if (ch < 0 && banks < MaxBanks) { banks++; ch = FreeChannel(k); }
                if (ch < 0) { Dropped++; wantKind[pick] = -1; continue; }      // past the last bank: dropped and counted
                owner[k][ch] = pick; voiceKind[pick] = k; voiceChannel[pick] = ch; fresh[pick] = true;
            }
            // 3. every voice on a channel: started if it is new there, then its pitch, level and pan
            int playing = 0;
            for (int i = 0; i < n; i++)
            {
                int k = voiceKind[i];
                if (k < 0) continue;
                int ch = voiceChannel[i], bank = ch / perBank[k], c = ch % perBank[k];
                Ensure(bank);
                if (fresh[i]) Start(bank, k, c, voices[i]);
                Play(bank, k, c, voices[i]);
                playing++;
            }
            VoicesPlaying = playing; if (playing > VoicesPlayingMax) VoicesPlayingMax = playing;
            EndApply();
        }

        private int FreeChannel(int kind)
        {
            var o = owner[kind]; int limit = banks * perBank[kind];
            for (int ch = 0; ch < limit; ch++) if (o[ch] < 0) return ch;
            return -1;
        }

        private void Release(int voice)
        {
            int k = voiceKind[voice], ch = voiceChannel[voice];
            owner[k][ch] = -1; voiceKind[voice] = -1;
            Stop(ch / perBank[k], k, ch % perBank[k]);
        }

        public void Silence()
        {
            for (int i = 0; i < MaxVoices; i++) voiceKind[i] = -1;
            foreach (var o in owner) Array.Fill(o, -1);
            SilenceAll();
        }

        /// <summary>The voice on a channel (tests): -1 when free.</summary>
        public int OwnerOf(int kind, int bank, int channel) => owner[kind][bank * perBank[kind] + channel];

        public virtual string Describe() =>
            $"{EnhancedName}: {banks} bank(s) of {BankShape} on offer, {madeCount} made; voices playing {VoicesPlaying} (most {VoicesPlayingMax}), dropped {Dropped}, out of range {NotesOutOfRange}, transposed {NotesTransposed}";

        // ---- helpers the chips share ----

        /// <summary>The 2 dB attenuation step (0 loudest .. 14 quietest audible, 15 off) of the SN76489 nearest to a linear level, in decibels.</summary>
        public static int PsgAttenuation(float level)
        {
            if (!(level > 0.0012f)) return 15;
            int att = (int)Math.Round(-20.0 * Math.Log10(Math.Min(1f, level)) / 2.0);
            return Math.Clamp(att, 0, 14);
        }
    }

    /// <summary>
    /// A stereo float ring at an output rate, indexed by absolute sample number, that the banks of a chip add into: each bank keeps its own write position (a bank made later starts at the
    /// position the first bank is at, less its mixers' latency, so the banks line up), and what every bank has written (the first bank's position) is read out as 16-bit pairs.
    /// </summary>
    internal sealed class BankMixRing
    {
        private const int Size = 1 << 15, Mask = Size - 1;
        private readonly float[] left = new float[Size], right = new float[Size];
        private long readPos;
        /// <summary>How far the base bank (bank 0) has written: what can be read.</summary>
        public long BasePos;

        public long ReadPos => readPos;

        /// <summary>Adds one frame at absolute position <paramref name="pos"/> (dropped when already read, or so far ahead it would wrap).</summary>
        public void Add(long pos, float l, float r)
        {
            if (pos < readPos || pos - readPos >= Size) return;
            int i = (int)(pos & Mask);
            left[i] += l; right[i] += r;
        }

        public int Read(Span<short> dest, float scale)
        {
            int frames = (int)Math.Min(BasePos - readPos, dest.Length / 2);
            if (frames <= 0) return 0;
            for (int f = 0; f < frames; f++)
            {
                int i = (int)((readPos + f) & Mask);
                dest[2 * f] = (short)Math.Clamp(left[i] * scale, -32768f, 32767f);
                dest[2 * f + 1] = (short)Math.Clamp(right[i] * scale, -32768f, 32767f);
                left[i] = 0; right[i] = 0;
            }
            readPos += frames;
            // a reader that does not keep up: the oldest goes (the ring holds a little under a second at 44.1 kHz)
            return frames * 2;
        }

        /// <summary>Keeps the reader within reach of the writers (drops what nobody read).</summary>
        public void Trim()
        {
            if (BasePos - readPos > Size - 4096)
            {
                long to = BasePos - (Size - 8192);
                for (long p = readPos; p < to; p++) { int i = (int)(p & Mask); left[i] = 0; right[i] = 0; }
                readPos = to;
            }
        }

        public void Reset() { Array.Clear(left); Array.Clear(right); readPos = 0; BasePos = 0; }
    }

    /// <summary>
    /// The enhanced chips of the consoles other than the NES, by the menu id a guest console reaches them with (<c>SNES:SFCS</c>, <c>SMS:SMSS</c>, <c>GG:GGS</c>, <c>MD:MDS</c>): the picture chip
    /// (<see cref="IEnhancedPictureChip"/>) and the sound chip (<see cref="IEnhancedSoundChip"/>) of each. A route this table does not know is refused (an <see cref="ArgumentException"/> the session turns into a
    /// refusal with its reason), never replaced by another chip.
    /// </summary>
    public static class EnhancedChips
    {
        private static readonly System.Collections.Generic.Dictionary<string, (Func<IEnhancedPictureChip>? picture, Func<int, IEnhancedSoundChip>? sound)> routes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Registers a route (the chips' own files do, in their static constructors' place: <see cref="EnsureRegistered"/>).</summary>
        public static void Register(string route, Func<IEnhancedPictureChip>? picture, Func<int, IEnhancedSoundChip>? sound) => routes[route] = (picture, sound);

        private static bool registered;
        private static void EnsureRegistered()
        {
            if (registered) return;
            lock (routes)
            {
                if (registered) return;
                RegisterBuiltIn();
                registered = true;
            }
        }

        private static void RegisterBuiltIn() => EnhancedChipRoutes.Register();

        /// <summary>True when <paramref name="id"/> is a route to another console's enhanced chip (the guest console's menus offer it).</summary>
        public static bool IsRoute(string? id) { EnsureRegistered(); return id != null && routes.ContainsKey(id); }

        /// <summary>The picture chip of a route, new.</summary>
        public static IEnhancedPictureChip Picture(string route)
        {
            EnsureRegistered();
            if (routes.TryGetValue(route, out var r) && r.picture != null) return r.picture();
            throw new ArgumentException($"No enhanced picture chip '{route}' (have: {string.Join(", ", PictureRoutes)})");
        }

        /// <summary>The sound chip of a route, new, delivering at <paramref name="sampleRate"/>.</summary>
        public static IEnhancedSoundChip Sound(string route, int sampleRate)
        {
            EnsureRegistered();
            if (routes.TryGetValue(route, out var r) && r.sound != null) return r.sound(sampleRate);
            throw new ArgumentException($"No enhanced sound chip '{route}' (have: {string.Join(", ", SoundRoutes)})");
        }

        /// <summary>Every route with a picture / sound chip (tests).</summary>
        public static string[] PictureRoutes { get { EnsureRegistered(); return routes.Where(kv => kv.Value.picture != null).Select(kv => kv.Key).ToArray(); } }
        public static string[] SoundRoutes { get { EnsureRegistered(); return routes.Where(kv => kv.Value.sound != null).Select(kv => kv.Key).ToArray(); } }
    }

    /// <summary>The built-in routes: each console's enhanced chips add their line here as they are made (SNES, Genesis, Master System / Game Gear).</summary>
    internal static partial class EnhancedChipRoutes
    {
        public static void Register() { RegisterSnes(); RegisterGenesis(); RegisterMasterSystem(); RegisterWideMess(); }
        static partial void RegisterSnes();
        static partial void RegisterGenesis();
        static partial void RegisterMasterSystem();
        static partial void RegisterWideMess();
    }
}
