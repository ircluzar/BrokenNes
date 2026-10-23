using System;
using NesEmulator.Mix;
using NesEmulator.Sega;

// =================================================================================================================================================================================
// MASTER SYSTEM / GAME GEAR ENHANCED (docs/projects/enhanced-cores.md): the Master System family's most accurate chips with the cross-console help, reached from every other console.
//   picture  SmsVdpS : SmsVdp    - the stock VDP (a Master System or Game Gear game draws on its pipeline, untouched) + the enhanced picture contract: another console's frame at the guest's own size (a NES
//                                  game's 256 x 240, a SNES game's 256 x 224 or 239, a Genesis game's 320 x 224 or 240 - beyond the Master System's 256 x 192 / 224 and the Game Gear's 160 x 144), as many
//                                  layers as the guest uses (the Master System has one background), every sprite of the guest's own rules; in the Master System's 6-bit colours (64) or the Game Gear's 12-bit (4,096).
//   sound    PsgVoiceChip        - SN76489 sets (3 squares and a noise each), as many as the guest's voices need; on the Game Gear the guest's pan on the stereo port (the Master System is mono). SmsSoundS :
//                                  SmsSound is the console's own enhanced unit: the game's PSG untouched as bank 0, extra sets for a bridge (none on its own console: a game sounds bit-identical).
//   NES board  PPU_SMSS / APU_SMSS - a NES game on the Master System's (ids SMSS), beside the stock PPU_SMS / APU_SMS.
// Routes: other consoles reach the chips as "SMS:SMSS" (the Master System's) and "GG:GGS" (the Game Gear's).
// =================================================================================================================================================================================

namespace NesEmulator.Mix
{
    internal static partial class EnhancedChipRoutes
    {
        static partial void RegisterMasterSystem()
        {
            EnhancedChips.Register("SMS:SMSS", () => new NesEmulator.Sega.SmsVdpS(gameGear: false), rate => new PsgVoiceChip(rate, gameGear: false));
            EnhancedChips.Register("GG:GGS", () => new NesEmulator.Sega.SmsVdpS(gameGear: true), rate => new PsgVoiceChip(rate, gameGear: true));
        }
    }

    /// <summary>
    /// The Master System / Game Gear enhanced sound chip's voice side: SN76489 sets (<see cref="PsgSet"/>, the console's own PSG on its NTSC clock), as many as the guest's voices need. Every tonal voice is a square at its
    /// pitch on one of a set's three tones (the PSG's only waveform: a pulse's duty and a triangle's shape are not carried), every noise voice the set's noise (white or periodic, at the exact rate through tone 3 when
    /// tone 3 is free). On the Game Gear the guest's pan goes on the stereo port; the Master System is mono. Allocation-free once warm.
    /// </summary>
    public sealed class PsgVoiceChip : HubVoiceChip
    {
        public const int MaxBankCount = 8;
        public const double PsgClockHz = 53_693_175.0 / 15;
        private const int KindTone = 0, KindNoise = 1;
        private readonly bool gameGear;

        private sealed class Set
        {
            public readonly PsgSet Psg; public long Clock, Pos;
            public double Start = double.NaN;      // the guest time the set started at (NaN: not yet; it can be below 0 after a rebase)
            public bool NoiseWanted; public double NoiseHz; public bool NoisePeriodic; public float NoiseLevel, NoisePan;
            public Set(int rate, bool stereo) { Psg = new PsgSet(PsgClockHz, rate, PsgVariant.Sega, stereo); }
        }

        private readonly Set?[] sets = new Set?[MaxBankCount];
        private readonly Set[] live = new Set[MaxBankCount];
        private int liveCount;
        private double now = -1;
        private readonly BankMixRing ring = new();
        private readonly short[] pcm = new short[1024];

        public override string EnhancedName => gameGear ? "Game Gear enhanced" : "Master System enhanced";
        public override string BankShape => gameGear ? "3 SN76489 squares + 1 noise, on the stereo port" : "3 SN76489 squares + 1 noise";
        public bool Stereo => gameGear;

        public PsgVoiceChip(int sampleRate, bool gameGear) : base(sampleRate, MaxBankCount, 3, 1)
        {
            this.gameGear = gameGear;
            MakeFirstBank();
        }

        protected override int ClassOf(in HubVoice v) => v.Kind == HubVoiceKind.Noise ? KindNoise : KindTone;

        protected override void MakeBank(int b)
        {
            var s = new Set(SampleRate, gameGear);
            if (now >= 0) { s.Start = now; s.Pos = Math.Max(ring.BasePos, ring.ReadPos) + BandLimitedMixer.Taps / 2; }
            sets[b] = s; live[liveCount++] = s;
        }

        protected override void Start(int b, int kind, int ch, in HubVoice v) { }

        protected override void Play(int b, int kind, int ch, in HubVoice v)
        {
            var s = sets[b]!;
            float level = Math.Clamp(v.Level, 0f, 1f);
            if (kind == KindTone) { s.Psg.Tone(ch, v.Hz, level, v.Pan); return; }
            s.NoiseWanted = true; s.NoiseHz = v.Hz; s.NoisePeriodic = v.ShortNoise; s.NoiseLevel = level; s.NoisePan = v.Pan;      // written after the tones (tone 3 may be the noise's rate)
        }

        protected override void Stop(int b, int kind, int ch)
        {
            var s = sets[b]; if (s == null) return;
            if (kind == KindTone) s.Psg.ToneOff(ch); else { s.NoiseWanted = false; s.Psg.NoiseOff(); }
        }

        protected override void EndApply()
        {
            long t = 0;
            for (int i = 0; i < liveCount; i++)
            {
                var s = live[i];
                if (s.NoiseWanted) s.Psg.Noise(s.NoiseHz, s.NoisePeriodic, s.NoiseLevel, s.NoisePan);
                s.NoiseWanted = false;
                t += s.Psg.Transposed;
            }
            NotesTransposed = t;
        }

        protected override void SilenceAll() { for (int i = 0; i < liveCount; i++) { live[i].Psg.Silence(); live[i].NoiseWanted = false; } }

        /// <summary>A raw write: address 0 the PSG's port, address 1 the Game Gear's stereo port.</summary>
        protected override void WriteRaw(int b, ushort address, byte value) => sets[b]!.Psg.Raw(address, value);

        public override void Rebase(double seconds)
        {
            if (now < 0) return;
            now = seconds;
            for (int i = 0; i < liveCount; i++) { var s = live[i]; if (!double.IsNaN(s.Start)) s.Start = seconds - s.Clock / PsgClockHz; }
        }

        public override void RunTo(double seconds)
        {
            if (now < 0) { now = seconds; for (int i = 0; i < liveCount; i++) if (double.IsNaN(live[i].Start)) live[i].Start = seconds; return; }
            if (seconds <= now) return;
            now = seconds;
            long minPos = long.MaxValue;
            for (int i = 0; i < liveCount; i++)
            {
                var s = live[i];
                if (double.IsNaN(s.Start)) s.Start = seconds;
                long clock = (long)((seconds - s.Start) * PsgClockHz);
                if (clock > s.Clock) { long d = clock - s.Clock; s.Clock = clock; while (d > 0) { int step = (int)Math.Min(d, int.MaxValue); s.Psg.Psg.Run(step); d -= step; } }
                int got;
                while ((got = s.Psg.Psg.ReadSamples(pcm)) > 0)
                    for (int k = 0; k + 1 < got; k += 2) ring.Add(s.Pos++, pcm[k] * (1f / 32768f), pcm[k + 1] * (1f / 32768f));
                minPos = Math.Min(minPos, s.Pos);
            }
            ring.BasePos = minPos == long.MaxValue ? ring.BasePos : minPos;
            ring.Trim();
        }

        public override int ReadSamples(Span<short> dest) => ring.Read(dest, 32767f);
    }
}

namespace NesEmulator.Sega
{
    /// <summary>
    /// MASTER SYSTEM / GAME GEAR ENHANCED sound unit on the console's own menu (ids <c>SMSS</c>, <c>GGS</c>): <see cref="SmsSound"/> itself - the game's SN76489 (and the Game Gear's stereo port), untouched, bank 0 - plus
    /// extra SN76489 sets (<see cref="IApuExtChannels"/>: address 0 a set's PSG port, 1 its stereo port; made when first written) that only a cross-console bridge drives. Nothing on the console's own menu writes them, so a
    /// game sounds bit-identical to the stock unit; its state is SmsSound's.
    /// </summary>
    public sealed class SmsSoundS : SmsSound, ISmsSound, IApuExtChannels
    {
        private readonly int rate;
        private readonly bool gameGear;
        private readonly double cpuHz;
        private long clocks;
        private PsgVoiceChip? extra;
        private int extBanks = 1;
        private short[] mix = Array.Empty<short>();

        public SmsSoundS(double cpuClockHz, int sampleRate, PsgVariant variant, bool gameGear) : base(cpuClockHz, sampleRate, variant) { rate = sampleRate; this.gameGear = gameGear; cpuHz = cpuClockHz; }

        public new string CoreName => (gameGear ? "GGS" : "SMSS") + " (SN76489 + extra SN76489 sets)";
        public int ExtBanks => extBanks;
        public int MaxExtBanks => PsgVoiceChip.MaxBankCount;
        public int EnsureExtBanks(int wanted) => extBanks = Math.Clamp(Math.Max(extBanks, wanted), 1, MaxExtBanks);
        public int ExtBanksInUse => extra?.BanksInUse ?? 0;

        public void WriteExtRegister(int bank, ushort address, byte value)
        {
            if (bank < 1 || bank > extBanks) throw new ArgumentOutOfRangeException(nameof(bank), $"SmsSoundS has extra banks 1 to {extBanks} (EnsureExtBanks adds more, up to {MaxExtBanks})");
            extra ??= new PsgVoiceChip(rate, gameGear);
            if (bank == 1) extra.WriteRegister(address, value);
            else { extra.EnsureExtBanks(bank - 1); extra.WriteExtRegister(bank - 1, address, value); }
        }

        public new void Run(int cpuClocks) { base.Run(cpuClocks); clocks += cpuClocks; extra?.RunTo(clocks / cpuHz); }

        public new int ReadSamples(Span<short> dest)
        {
            int n = base.ReadSamples(dest);
            if (extra == null || n == 0) return n;
            if (mix.Length < n) mix = new short[n];
            int m = extra.ReadSamples(mix.AsSpan(0, n));
            for (int i = 0; i < m; i++) dest[i] = (short)Math.Clamp(dest[i] + mix[i], -32768, 32767);
            return n;
        }

        public new void Reset() { base.Reset(); extra?.Silence(); }
    }

    /// <summary>
    /// MASTER SYSTEM / GAME GEAR ENHANCED picture chip: <see cref="SmsVdp"/> itself (a subclass, not a fork: a Master System or Game Gear game draws on the stock pipeline, untouched) plus the enhanced picture contract
    /// (<see cref="IEnhancedPictureChip"/>): another console's frame, as layers, drawn at the guest's own size - a NES game's 256 x 240, a SNES game's 256 x 224 or 239, a Genesis game's 320 x 224 or 240, a Game Boy's
    /// 160 x 144 - with every layer the guest uses (the Master System has one background) and every sprite under the guest's own rules, in the Master System's 6-bit colours (2 bits a channel, 64) or the Game Gear's
    /// 12-bit (4 bits a channel, 4,096); the guest's colour math blended and shown in them.
    /// </summary>
    public sealed class SmsVdpS : SmsVdp, IEnhancedPictureChip
    {
        private readonly ExtPicture layers = new();
        private readonly LayerPainter painter;
        private readonly bool gameGear;

        public SmsVdpS(bool gameGear) : base(gameGear ? SmsVdpRevision.GameGear : SmsVdpRevision.Sms2, pal: false)
        {
            this.gameGear = gameGear;
            painter = new LayerPainter(new Levels(gameGear ? Level4 : Level2));
        }

        public ExtPicture Layers => layers;
        public void DrawLayers() => painter.Draw(layers);
        public uint[] Picture => painter.Picture;
        public int PictureWidth => painter.Width;
        public int PictureHeight => painter.Height;
        public string EnhancedName => gameGear ? "Game Gear enhanced" : "Master System enhanced";
        public string ColourLimit => gameGear ? "the Game Gear's 12-bit colours (4,096)" : "the Master System's 6-bit colours (64)";
        public int MathPixels => painter.MathPixels;

        /// <summary>Each channel the nearest of the chip's levels (the VDP's own, as its colour RAM shows them).</summary>
        private sealed class Levels : LayerPainter.Rules
        {
            private readonly byte[] map = new byte[256];
            public Levels(byte[] levels)
            {
                for (int x = 0; x < 256; x++)
                {
                    int best = 0;
                    for (int v = 1; v < levels.Length; v++) if (Math.Abs(levels[v] - x) < Math.Abs(levels[best] - x)) best = v;
                    map[x] = levels[best];
                }
            }
            public override uint Colour(uint rgb) => 0xFF000000u | (uint)(map[(rgb >> 16) & 0xFF] << 16 | map[(rgb >> 8) & 0xFF] << 8 | map[rgb & 0xFF]);
        }
    }
}

namespace NesEmulator
{
    /// <summary>A NES game on the MASTER SYSTEM ENHANCED picture chip (NES board PPU id <c>SMSS</c>, "Master System (enhanced)"): the NES picture as layers (<see cref="NesLayeredPpu"/>) drawn by <see cref="NesEmulator.Sega.SmsVdpS"/>
    /// at the NES's 256 x 240 in the Master System's 64 colours, the whole 2 x 2 nametable map (the right edge its own column), each line's own scroll and clips, the NES's 8 sprites a line with their flips and
    /// priorities. The stock Master System chip (<see cref="PPU_SMS"/>, id SMS) replays the picture through its one 32-column map (the right edge repeats the left) and its 448 tiles.</summary>
    public sealed class PPU_SMSS : NesLayeredPpu
    {
        public PPU_SMSS(Bus bus) : base(bus, new NesEmulator.Sega.SmsVdpS(gameGear: false)) { }
        public override string CoreName => $"SMSS:Master System enhanced+{front.CoreName}";
        public override string Description => "The Master System picture chip, enhanced, drawing a NES game whole (256 x 240, the NES's sprite and clip rules) in the Master System's 64 colours, with a NES PPU keeping the register behaviour";
        public override string BridgeName => $"NES game on the Master System enhanced picture chip (front {front.CoreName})";
    }

    /// <summary>A NES game's sound on the MASTER SYSTEM ENHANCED sound chip (NES board APU id <c>SMSS</c>): pulse 1 and 2 and the triangle as squares of an SN76489 set, the noise on its noise channel at the exact rate
    /// (tone 3 is free), each on a channel of its own (<see cref="NesApuOnChip"/>). The stock Master System sound (<see cref="APU_SMS"/>, id SMS) puts them on one PSG (the noise's rate then shares tone 3 with the
    /// triangle). Not in the FL plugin's chip list (its indexes are append-only).</summary>
    public sealed class APU_SMSS : NesApuOnChip
    {
        public APU_SMSS(Bus bus) : base(bus, "SMS:SMSS") { }
        public override string CoreName => $"SMSS:SN76489 sets+{Front.CoreName}";
        public override string Description => "NES channels played on the Master System enhanced sound chip: every channel on an SN76489 channel of its own (the DMC is dropped and counted)";
    }
}
