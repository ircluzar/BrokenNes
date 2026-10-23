using System;
using System.IO;
using NesEmulator.Mix;
using NesEmulator.Sega;

// =================================================================================================================================================================================
// GENESIS ENHANCED (docs/projects/enhanced-cores.md): the Genesis's most accurate chips with the cross-console help, reached from every other console.
//   picture  MdVdpS : MdVdp      - the stock VDP (a Genesis game draws on its pipeline, untouched) + the enhanced picture contract: another console's frame at the guest's own size (a NES game's 256 x 240,
//                                  a SNES game's 256 x 224 or 239, a Game Boy's 160 x 144), as many layers as the guest uses - more than plane A + plane B + the window (a SNES mode 0 game's four) - in the
//                                  Genesis's 9-bit colours, and where the guest's colour math asks the Genesis's own: shadow (subtract) and highlight (add), at the VDP's DAC levels.
//   sound    MdVoiceChip         - sets of a YM2612 and a PSG (6 FM channels, 3 squares, a noise each), as many as the guest's voices need: a guest's square (an SN76489 tone) on the PSG, every other tonal voice
//                                  an FM voice with the chosen patch (the triangle a sine), the noise on the PSG's noise; the pan on the FM channels' L/R bits. MdSoundUnitS : MdSoundUnit is the Genesis's own
//                                  enhanced unit: the game's Z80, YM2612 and PSG untouched as bank 0, extra sets for a bridge (none on its own console: a Genesis game sounds bit-identical).
//   NES board  PPU_MDS / APU_MDS - a NES game on them (ids MDS), beside the stock PPU_MD / APU_MD.
// Routes: other consoles reach the chips as "MD:MDS".
// =================================================================================================================================================================================

namespace NesEmulator.Mix
{
    internal static partial class EnhancedChipRoutes
    {
        static partial void RegisterGenesis() => EnhancedChips.Register("MD:MDS", () => new NesEmulator.Sega.MdVdpS(), rate => new MdVoiceChip(rate));
    }

    /// <summary>
    /// The Genesis enhanced sound chip's voice side: sets of a YM2612 (the Genesis's own, <see cref="Ym2612"/>) and an SN76489 (<see cref="PsgSet"/>), as many as the guest's voices need. Kinds of channel a set
    /// has: 6 FM (every tonal voice that is not a PSG square: the pulse patch the user chose for the Genesis bridges, <see cref="APU_MD.DefaultPatch"/>, or the sine for a triangle; at its pitch to the cent of the
    /// FM chip's frequency registers; its level through the carriers' total level), 3 PSG tones (an SN76489 tone of the guest: the very same square), 1 PSG noise (white or periodic). The pan goes on the FM channels'
    /// L/R bits (the Genesis PSG is mono). The chips run on the Genesis's NTSC clocks. Allocation-free once warm.
    /// </summary>
    public sealed class MdVoiceChip : HubVoiceChip
    {
        public const int MaxBankCount = 6;
        public const double YmClockHz = 53_693_175.0 / 7, PsgClockHz = 53_693_175.0 / 15;
        private const int KindFm = 0, KindTone = 1, KindNoise = 2;
        /// <summary>The loudness (AC RMS) a voice at level 1 plays at: a full FM carrier, a little under (several voices at full stay clear of clipping).</summary>
        public static readonly double FullRms = FmPatchBank.CarrierRms * 0.7;

        private sealed class Set : IYm2612Sink
        {
            public readonly Ym2612 Ym;
            public readonly PsgSet Psg;
            public readonly BandLimitedMixer FmL, FmR;
            public long FmSamples; private float lastL, lastR;
            public long YmClock, PsgClock; public double Start = -1;
            public long Pos;                  // the output position this set writes next (BankMixRing)
            public readonly int[] Block = new int[6], Fnum = new int[6], Att = new int[6], PatchOf = new int[6], Pan = new int[6];
            public readonly bool[] Keyed = new bool[6], OutOfRange = new bool[6];
            public bool NoiseWanted; public double NoiseHz; public bool NoisePeriodic; public float NoiseLevel;
            public Set(GenesisModel model, int rate)
            {
                Ym = new Ym2612(model, YmClockHz) { Sink = this }; Ym.PowerOn();
                FmL = new BandLimitedMixer(YmClockHz / Ym2612.ClocksPerSample, rate); FmR = new BandLimitedMixer(YmClockHz / Ym2612.ClocksPerSample, rate);
                Psg = new PsgSet(PsgClockHz, rate, PsgVariant.MegaDrive, stereo: false);
                Array.Fill(Block, -1); Array.Fill(Fnum, -1); Array.Fill(Att, -1); Array.Fill(PatchOf, -1); Array.Fill(Pan, -1);
            }
            public void Reset() { lastL = lastR = 0; }
            public void Sample(int left, int right)
            {
                float l = left * (1f / 32768f), r = right * (1f / 32768f);
                FmSamples++;
                if (l != lastL) { FmL.Step(FmSamples, l - lastL); lastL = l; }
                if (r != lastR) { FmR.Step(FmSamples, r - lastR); lastR = r; }
            }
        }

        private readonly GenesisModel model;
        private readonly Set?[] sets = new Set?[MaxBankCount];
        private readonly Set[] live = new Set[MaxBankCount];
        private int liveCount;
        private double now = -1;
        private readonly BankMixRing ring = new();
        private readonly float[] fl = new float[512], fr = new float[512];
        private readonly short[] pcm = new short[1024];

        public override string EnhancedName => "Genesis enhanced";
        public override string BankShape => "6 YM2612 FM channels + 3 PSG squares + 1 PSG noise";

        public MdVoiceChip(int sampleRate) : base(sampleRate, MaxBankCount, 6, 3, 1)
        {
            model = APU_MD.DefaultModel ?? SegaSettings.ResolveGenesisModel(null, Array.Empty<byte>());
            MakeFirstBank();
        }

        private static FmPatch Pulse => FmPatchBank.Patches[FmPatchBank.Clamp(APU_MD.DefaultPatch)];
        private static bool PsgSquares => Pulse.Psg;      // the user chose the "PSG square" patch for the Genesis bridges: squares on the PSG

        protected override int ClassOf(in HubVoice v) => v.Kind == HubVoiceKind.Noise ? KindNoise : v.Psg || (v.Kind == HubVoiceKind.Pulse && PsgSquares) ? KindTone : KindFm;

        protected override void MakeBank(int b)
        {
            var s = new Set(model, SampleRate);
            if (now >= 0) { s.Start = now; s.Pos = Math.Max(ring.BasePos, ring.ReadPos) + BandLimitedMixer.Taps / 2; }      // a set made later starts where the others are (its mixers' latency counted), with no catching up
            sets[b] = s; live[liveCount++] = s;
        }

        private static void YmReg(Set s, int reg, int value, int part)
        {
            s.Ym.Write(part * 2, (byte)reg, s.YmClock);
            s.Ym.Write(part * 2 + 1, (byte)value, s.YmClock);
        }

        private static void LoadChannel(Set s, int ch, FmPatch p, int index)
        {
            int part = ch / 3, c = ch % 3;
            for (int n = 0; n < 4; n++)
            {
                var o = p.Operators[n]; int off = c + FmPatchBank.SlotOffset(n);
                YmReg(s, 0x30 + off, o.Dt << 4 | o.Mul, part); YmReg(s, 0x40 + off, o.Tl, part); YmReg(s, 0x50 + off, o.Rs << 6 | o.Ar, part);
                YmReg(s, 0x60 + off, o.D1r, part); YmReg(s, 0x70 + off, o.D2r, part); YmReg(s, 0x80 + off, o.Sl << 4 | o.Rr, part); YmReg(s, 0x90 + off, 0, part);
            }
            YmReg(s, 0xB0 + c, p.Feedback << 3 | p.Algorithm, part);
            s.PatchOf[ch] = index; s.Att[ch] = -1; s.Pan[ch] = -1;
        }

        private static void Key(Set s, int ch, bool on)
        {
            if (s.Keyed[ch] == on) return;
            s.Keyed[ch] = on;
            YmReg(s, 0x28, (on ? 0xF0 : 0x00) | (ch / 3) << 2 | ch % 3, 0);
        }

        protected override void Start(int b, int kind, int ch, in HubVoice v)
        {
            var s = sets[b]!;
            if (kind == KindFm) { Key(s, ch, false); s.OutOfRange[ch] = false; }
        }

        protected override void Play(int b, int kind, int ch, in HubVoice v)
        {
            var s = sets[b]!;
            float level = Math.Clamp(v.Level, 0f, 1f);
            switch (kind)
            {
                case KindTone: s.Psg.Tone(ch, v.Hz, level, 0f); return;
                case KindNoise: s.NoiseWanted = true; s.NoiseHz = v.Hz; s.NoisePeriodic = v.ShortNoise; s.NoiseLevel = level; return;      // written after the tones (EndApply): tone 3 may be the noise's rate
            }
            int index = v.Kind == HubVoiceKind.Triangle ? FmPatchBank.SineIndex : PsgSquares ? FmPatchBank.DefaultIndex : FmPatchBank.Clamp(APU_MD.DefaultPatch);
            var patch = FmPatchBank.Patches[index];
            if (s.PatchOf[ch] != index) { Key(s, ch, false); LoadChannel(s, ch, patch, index); }
            if (PitchGuard.Blocks(v.Hz) || !FmPatchBank.TryPitch(v.Hz, YmClockHz, out int block, out int fnum, out _))
            {
                if (!s.OutOfRange[ch] && !PitchGuard.Blocks(v.Hz)) NotesOutOfRange++;
                s.OutOfRange[ch] = true; Key(s, ch, false); return;
            }
            s.OutOfRange[ch] = false;
            int part = ch / 3, c = ch % 3;
            if (s.Block[ch] != block || s.Fnum[ch] != fnum) { s.Block[ch] = block; s.Fnum[ch] = fnum; YmReg(s, 0xA4 + c, block << 3 | fnum >> 8, part); YmReg(s, 0xA0 + c, fnum & 0xFF, part); }
            int pan = v.Pan < -0.33f ? 0x80 : v.Pan > 0.33f ? 0x40 : 0xC0;
            if (s.Pan[ch] != pan) { s.Pan[ch] = pan; YmReg(s, 0xB4 + c, pan, part); }
            int att = level <= 0.0005f ? 127 : patch.AttenuationFor(level * FullRms, s.Ym.Ladder);
            if (s.Att[ch] != att)
            {
                s.Att[ch] = att;
                for (int n = 0; n < 4; n++) if ((patch.CarrierMask >> n & 1) != 0) YmReg(s, 0x40 + c + FmPatchBank.SlotOffset(n), Math.Min(127, patch.Operators[n].Tl + att), part);
            }
            Key(s, ch, true);
        }

        protected override void Stop(int b, int kind, int ch)
        {
            var s = sets[b]; if (s == null) return;
            switch (kind)
            {
                case KindFm: Key(s, ch, false); break;
                case KindTone: s.Psg.ToneOff(ch); break;
                default: s.NoiseWanted = false; s.Psg.NoiseOff(); break;
            }
        }

        protected override void EndApply()
        {
            for (int i = 0; i < liveCount; i++)
            {
                var s = live[i];
                if (s.NoiseWanted) s.Psg.Noise(s.NoiseHz, s.NoisePeriodic, s.NoiseLevel, 0f);
                s.NoiseWanted = false;
            }
            long t = 0; for (int i = 0; i < liveCount; i++) t += live[i].Psg.Transposed;
            NotesTransposed = t;
        }

        protected override void SilenceAll()
        {
            for (int i = 0; i < liveCount; i++) { var s = live[i]; for (int ch = 0; ch < 6; ch++) Key(s, ch, false); s.Psg.Silence(); s.NoiseWanted = false; }
        }

        protected override void WriteRaw(int b, ushort address, byte value)
        {
            var s = sets[b]!;
            if (address >= 0x400) { s.Psg.Raw((ushort)(address - 0x400), value); return; }       // $400 the PSG's port, $401 (ignored: the Genesis PSG is mono)
            YmReg(s, address & 0xFF, value, (address >> 8) & 1);                                    // $000-$0FF part 1, $100-$1FF part 2
        }

        public override void RunTo(double seconds)
        {
            if (now < 0) { now = seconds; for (int i = 0; i < liveCount; i++) if (live[i].Start < 0) live[i].Start = seconds; return; }
            if (seconds <= now) return;
            now = seconds;
            long minPos = long.MaxValue;
            for (int i = 0; i < liveCount; i++)
            {
                var s = live[i];
                if (s.Start < 0) s.Start = seconds;
                double t = seconds - s.Start;
                long ym = (long)(t * YmClockHz); if (ym > s.YmClock) { s.YmClock = ym; s.Ym.RunTo(ym); }
                long psg = (long)(t * PsgClockHz); if (psg > s.PsgClock) { long d = psg - s.PsgClock; s.PsgClock = psg; while (d > 0) { int step = (int)Math.Min(d, int.MaxValue); s.Psg.Psg.Run(step); d -= step; } }
                Drain(s);
                minPos = Math.Min(minPos, s.Pos);
            }
            ring.BasePos = minPos == long.MaxValue ? ring.BasePos : minPos;
            ring.Trim();
        }

        private void Drain(Set s)
        {
            s.FmL.Advance(s.FmSamples); s.FmR.Advance(s.FmSamples);
            while (true)
            {
                int frames = Math.Min(Math.Min(s.FmL.Available, s.FmR.Available), fl.Length);
                if (frames <= 0) break;
                int got = s.Psg.Psg.ReadSamples(pcm.AsSpan(0, frames * 2)) / 2;
                if (got < frames) frames = got;
                if (frames <= 0) break;
                s.FmL.Read(fl.AsSpan(0, frames)); s.FmR.Read(fr.AsSpan(0, frames));
                for (int i = 0; i < frames; i++) ring.Add(s.Pos++, fl[i] + pcm[2 * i] * (1f / 32768f), fr[i] + pcm[2 * i + 1] * (1f / 32768f));
            }
        }

        public override int ReadSamples(Span<short> dest) => ring.Read(dest, 32767f);
    }
}

namespace NesEmulator.Sega
{
    /// <summary>
    /// GENESIS ENHANCED sound unit on the Genesis's own menu (id <c>MDS</c>): <see cref="MdSoundUnit"/> itself - the game's Z80 and its sound driver, YM2612 and PSG, untouched, bank 0 - plus extra YM2612 + PSG sets
    /// (<see cref="IApuExtChannels"/>: written as the chips' registers, <c>$000-$1FF</c> the YM2612's two parts, <c>$400</c> the PSG's port; made when first written) that only a cross-console bridge drives. Nothing on the
    /// Genesis's own console writes them, so a Genesis game sounds bit-identical to <c>MD</c>; its state is MdSoundUnit's.
    /// </summary>
    public sealed class MdSoundUnitS : MdSoundUnit, IGenesisSound, IApuExtChannels
    {
        private readonly double masterHz;
        private readonly int rate;
        private MdVoiceChip? extra;
        private int extBanks = 1;
        private short[] mix = Array.Empty<short>();

        public MdSoundUnitS(SegaMachine machine) : base(machine) { masterHz = machine.Timeline.MasterHz; rate = machine.SampleRate; }

        public new string CoreName => "MDS (" + base.CoreName + " + extra YM2612 / PSG sets)";
        public int ExtBanks => extBanks;
        public int MaxExtBanks => MdVoiceChip.MaxBankCount;
        public int EnsureExtBanks(int wanted) => extBanks = Math.Clamp(Math.Max(extBanks, wanted), 1, MaxExtBanks);
        public int ExtBanksInUse => extra?.BanksInUse ?? 0;

        public void WriteExtRegister(int bank, ushort address, byte value)
        {
            if (bank < 1 || bank > extBanks) throw new ArgumentOutOfRangeException(nameof(bank), $"MdSoundUnitS has extra banks 1 to {extBanks} (EnsureExtBanks adds more, up to {MaxExtBanks})");
            extra ??= new MdVoiceChip(rate);
            if (bank == 1) extra.WriteRegister(address, value);
            else { extra.EnsureExtBanks(bank - 1); extra.WriteExtRegister(bank - 1, address, value); }
        }

        public new void RunTo(long masterClock) { base.RunTo(masterClock); extra?.RunTo(masterClock / masterHz); }

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
    /// GENESIS ENHANCED picture chip: <see cref="MdVdp"/> itself (a subclass, not a fork: a Genesis game draws on the stock pipeline, untouched) plus the enhanced picture contract (<see cref="IEnhancedPictureChip"/>):
    /// another console's frame, as layers, drawn at the guest's own size - a NES game's 256 x 240, a SNES game's 256 x 224 or 239, a Master System's 256 x 192 to 240, a Game Gear's or a Game Boy's 160 x 144 - with as
    /// many layers as the guest uses (a SNES mode 0 game's four, where the Genesis has plane A, plane B and the window), in the Genesis's 512 colours at the VDP's own DAC levels; where the guest's colour math asks, the
    /// Genesis's own: a subtracted pixel is shadowed, an added one highlighted.
    /// </summary>
    public sealed class MdVdpS : MdVdp, IEnhancedPictureChip
    {
        private readonly ExtPicture layers = new();
        private readonly LayerPainter painter = new(new Rules9());

        public MdVdpS() : base(SegaTimeline.Genesis(SegaRegion.Ntsc)) { }

        public ExtPicture Layers => layers;
        public void DrawLayers() => painter.Draw(layers);
        public uint[] Picture => painter.Picture;
        public int PictureWidth => painter.Width;
        public int PictureHeight => painter.Height;
        public string EnhancedName => "Genesis enhanced";
        public string ColourLimit => "the Genesis's 9-bit colours (512), shadow and highlight for colour math";
        public int MathPixels => painter.MathPixels;

        private sealed class Rules9 : LayerPainter.Rules
        {
            public override uint Colour(uint rgb) => Argb(SegaColour.Md9((int)(rgb & 0xFFFFFF)), 0);
            /// <summary>The Genesis's colour math: shadow (half brightness, the DAC's lower steps) where the guest subtracts, highlight (the upper steps) where it adds.</summary>
            public override uint MathPixel(ExtPicture e, uint rgb, int line) => Argb(SegaColour.Md9((int)(rgb & 0xFFFFFF)), (e.MathMode[line] & 0x80) != 0 ? 1 : 2);
        }
    }
}

namespace NesEmulator
{
    /// <summary>A NES game on the GENESIS ENHANCED picture chip (NES board PPU id <c>MDS</c>, "Genesis (enhanced)"): the NES picture as layers (<see cref="NesLayeredPpu"/>) drawn by <see cref="NesEmulator.Sega.MdVdpS"/> at
    /// the NES's 256 x 240 in the Genesis's 512 colours, every background column of each line's own scroll and the left-column clip of each layer apart, the NES's 8 sprites a line. The stock Genesis chip
    /// (<see cref="PPU_MD"/>, id MD) replays the picture through the VDP's planes (one left-column blank for both layers, 16 sprites a line).</summary>
    public sealed class PPU_MDS : NesLayeredPpu
    {
        public PPU_MDS(Bus bus) : base(bus, new NesEmulator.Sega.MdVdpS()) { }
        public override string CoreName => $"MDS:Genesis enhanced+{front.CoreName}";
        public override string Description => "The Genesis picture chip, enhanced, drawing a NES game whole (256 x 240, the NES's sprite and clip rules) in the Genesis's 512 colours, with a NES PPU keeping the register behaviour";
        public override string BridgeName => $"NES game on the Genesis enhanced picture chip (front {front.CoreName})";
    }

    /// <summary>A NES game's sound on the GENESIS ENHANCED sound chips (NES board APU id <c>MDS</c>): pulse 1 and 2 as FM voices with the chosen patch, the triangle an FM sine, the noise on the PSG, each on a channel of
    /// its own (<see cref="NesApuOnChip"/>). The stock Genesis sound (<see cref="APU_MD"/>, id MD) is the same idea on one set. Not in the FL plugin's chip list (its indexes are append-only).</summary>
    public sealed class APU_MDS : NesApuOnChip
    {
        public APU_MDS(Bus bus) : base(bus, "MD:MDS") { }
        public override string CoreName => $"MDS:YM2612+PSG sets+{Front.CoreName}";
        public override string Description => "NES channels played on the Genesis enhanced sound chips: every channel on a YM2612 or PSG channel of its own (the DMC is dropped and counted)";
    }
}
