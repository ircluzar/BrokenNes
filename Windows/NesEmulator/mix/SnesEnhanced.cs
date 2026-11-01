using System;
using System.IO;
using NesEmulator.Mix;
using NesEmulator.Sega;

// =================================================================================================================================================================================
// SNES ENHANCED (docs/projects/enhanced-cores.md): the SNES's most accurate chips with the cross-console help, reached from every other console.
//   picture  PPU_SFCS : PPU_SFC  - the stock S-PPU (a SNES game draws on its pipeline, untouched) + the enhanced picture contract: another console's frame (ExtPicture) at the guest's own size
//                                  (a NES game's 240 lines, where the SNES shows 224 or 239; a Genesis game's 320 columns), as many layers as the guest uses, in the SNES's 15-bit colours with
//                                  its colour math done the SNES's way (in 15-bit, add / subtract, half).
//   sound    SfcVoiceChip        - S-DSPs (8 voices each) in as many banks as the guest's voices need, every guest voice a looped sample at its pitch (pulses of the guest's duty, a triangle, a
//                                  white or a short noise), the guest's pan on the DSP's own left and right volumes. APU_SFCS : APU_SFC is the SNES's own enhanced unit: the game's SPC700 + S-DSP
//                                  untouched as bank 0, extra DSP banks for a bridge (none on its own console, so a SNES game sounds bit-identical).
//   NES board  PPU_SNESS / APU_SNESS - a NES game on the SNES enhanced chips (ids SNESS), beside the stock PPU_SNES / APU_SNES.
// Routes: other consoles reach the chips as "SNES:SFCS" (EnhancedChips).
// This file lives in mix/ (not snes/) because it uses the cross-console contracts (ExtPicture, HubVoice): snes/ is also built alone (SubProjects/SnesWasmBench).
// =================================================================================================================================================================================

namespace NesEmulator.Mix
{
    internal static partial class EnhancedChipRoutes
    {
        static partial void RegisterSnes() => EnhancedChips.Register("SNES:SFCS", () => new NesEmulator.Snes.PPU_SFCS(), rate => new SfcVoiceChip(rate));
    }

    /// <summary>
    /// The SNES enhanced sound chip's voice side: S-DSPs (<see cref="NesEmulator.Snes.DSP_SFC"/>, the SNES's own, each with its 64 KB of sound RAM) in banks of 8 voices, playing the hub's voices directly: every voice a
    /// looped sample of the waveform it is (a pulse of its own duty - 12.5, 25, 50 or 75% -, a PSG tone a 50% square, a triangle, white noise, the NES's 93-step short noise) at its pitch, at its level, panned by
    /// the DSP's left and right volumes. Pitches up to about 4 kHz play from a 32-sample period, higher ones from an 8-sample one (up to 16 kHz); noise is one sample a shift (up to 128 kHz). No echo, no pitch
    /// modulation. Allocation-free once warm.
    /// </summary>
    public sealed class SfcVoiceChip : HubVoiceChip
    {
        public const int VoicesPerBank = 8, MaxBankCount = 8, DspRate = 32000;
        /// <summary>The DSP volume a voice at level 1 plays at (of 127; about a tenth of full scale a voice, so a bank of eight at full stays clear of clipping most of the time).</summary>
        public const int VolumeMax = 40;
        private const int DirAddr = 0x0200, DataAddr = 0x0300;
        private const int SrcPulseLong = 0, SrcPulseShort = 4, SrcTriLong = 8, SrcTriShort = 9, SrcWhite = 10, SrcShortNoise = 11;

        private sealed class Bank
        {
            public readonly byte[] Aram = new byte[0x10000];
            public readonly NesEmulator.Snes.DSP_SFC Dsp;
            public short L, R;
            public int Kon;
            public readonly int[] Srcn = { -1, -1, -1, -1, -1, -1, -1, -1 };
            public readonly bool[] OutOfRange = new bool[8];
            public Bank() { Dsp = new NesEmulator.Snes.DSP_SFC(Aram) { Output = Take }; }
            private void Take(short l, short r) { L = l; R = r; }
        }

        private readonly Bank?[] banks = new Bank?[MaxBankCount];
        private readonly Bank[] live = new Bank[MaxBankCount];
        private int liveCount;
        private long samplesDone = -1;
        private readonly NesEmulator.Sega.ChipRateConverter? convL, convR;
        private readonly float[] blockL = new float[512], blockR = new float[512], pullL = new float[1024], pullR = new float[1024];
        private readonly short[] ring = new short[1 << 16];
        private int ringHead, ringCount;

        public override string EnhancedName => "SNES enhanced";
        public override string BankShape => "8 S-DSP voices";

        public SfcVoiceChip(int sampleRate) : base(sampleRate, MaxBankCount, VoicesPerBank)
        {
            if (sampleRate != DspRate) { convL = new NesEmulator.Sega.ChipRateConverter(DspRate, sampleRate); convR = new NesEmulator.Sega.ChipRateConverter(DspRate, sampleRate); }
            MakeFirstBank();
        }

        // ---- the sample set every bank holds: directory at $0200, BRR data from $0300 ----
        private static readonly (byte[] image, int[] period) Samples = BuildSamples();

        private static (byte[], int[]) BuildSamples()
        {
            var dir = new System.Collections.Generic.List<byte>(); var data = new System.Collections.Generic.List<byte>(); var period = new System.Collections.Generic.List<int>();
            int addr = DataAddr;
            void Add(int[] nibbles, int per)
            {
                var b = Brr.Encode(nibbles, 12);
                dir.Add((byte)addr); dir.Add((byte)(addr >> 8)); dir.Add((byte)addr); dir.Add((byte)(addr >> 8));     // start and loop: the whole sample loops
                data.AddRange(b); addr += b.Length; period.Add(per);
            }
            int[] Repeat(int[] cycle, int length) { var n = new int[length]; for (int i = 0; i < length; i++) n[i] = cycle[i % cycle.Length]; return n; }
            foreach (int high in new[] { 4, 8, 16, 24 }) { var c = new int[32]; for (int i = 0; i < 32; i++) c[i] = i < high ? 7 : -7; Add(c, 32); }        // pulses, 32-sample period
            foreach (int high in new[] { 1, 2, 4, 6 }) { var c = new int[8]; for (int i = 0; i < 8; i++) c[i] = i < high ? 7 : -7; Add(Repeat(c, 16), 8); }  // pulses, 8-sample period (two in a block)
            { var c = new int[32]; for (int i = 0; i < 32; i++) c[i] = i < 16 ? 7 - i : i - 24; Add(c, 32); }                                              // triangle (the NES's 16 steps)
            Add(Repeat(new[] { -6, -2, 2, 6, 6, 2, -2, -6 }, 16), 8);                                                                                       // triangle, 8-sample period
            { var n = new int[1024]; uint s = 0x1234567u; for (int i = 0; i < n.Length; i++) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; n[i] = (int)(s % 15) - 7; } Add(n, 1); }   // white noise, a sample a shift
            {
                // the NES's short (93-step) noise: the 15-bit register with the tap on bit 6, one step a sample, the 93 steps 16 times over (a whole number of BRR blocks)
                var steps = new int[93]; int reg = 1;
                for (int i = 0; i < 93; i++) { steps[i] = (reg & 1) != 0 ? -7 : 7; int fb = (reg ^ (reg >> 6)) & 1; reg = (reg >> 1) | (fb << 14); }
                Add(Repeat(steps, 93 * 16), 1);
            }
            var image = new byte[DataAddr - DirAddr + data.Count];
            dir.CopyTo(image, 0); data.CopyTo(image, DataAddr - DirAddr);
            return (image, period.ToArray());
        }

        protected override int ClassOf(in HubVoice v) => 0;      // every DSP voice plays anything

        protected override void MakeBank(int b)
        {
            var k = new Bank();
            Array.Copy(Samples.image, 0, k.Aram, DirAddr, Samples.image.Length);
            var d = k.Dsp;
            d.Write(0x6C, 0x20);                              // FLG: no reset, not muted, echo writes off
            d.Write(0x0C, 0x60); d.Write(0x1C, 0x60);         // main volume
            d.Write(0x2C, 0); d.Write(0x3C, 0); d.Write(0x0D, 0); d.Write(0x2D, 0); d.Write(0x3D, 0); d.Write(0x4D, 0); d.Write(0x7D, 0); d.Write(0x6D, 0xFF);
            d.Write(0x5D, DirAddr >> 8); d.Write(0x5C, 0);
            for (int v = 0; v < 8; v++)
            {
                int r = v << 4;
                d.Write((byte)r, 0); d.Write((byte)(r + 1), 0); d.Write((byte)(r + 2), 0); d.Write((byte)(r + 3), 0);
                d.Write((byte)(r + 4), 0); d.Write((byte)(r + 5), 0x00); d.Write((byte)(r + 7), 0x7F);     // GAIN mode, direct, full
            }
            banks[b] = k; live[liveCount++] = k;
        }

        private static int Source(in HubVoice v, out int period, out double pitch)
        {
            if (v.Kind == HubVoiceKind.Noise)
            {
                period = 1; pitch = v.Hz * 4096.0 / DspRate;
                return v.ShortNoise ? SrcShortNoise : SrcWhite;
            }
            bool square = v.Kind == HubVoiceKind.Pulse || v.Psg;
            double longPitch = v.Hz * 32 * 4096.0 / DspRate;
            if (longPitch <= 0x3FFF) { period = 32; pitch = longPitch; return square ? SrcPulseLong + (v.Psg ? 2 : v.Duty & 3) : SrcTriLong; }
            period = 8; pitch = v.Hz * 8 * 4096.0 / DspRate;
            return square ? SrcPulseShort + (v.Psg ? 2 : v.Duty & 3) : SrcTriShort;
        }

        protected override void Start(int b, int kind, int ch, in HubVoice v) { var k = banks[b]!; k.Srcn[ch] = -1; k.OutOfRange[ch] = false; }

        protected override void Play(int b, int kind, int ch, in HubVoice v)
        {
            var k = banks[b]!; var d = k.Dsp; int r = ch << 4;
            int src = Source(v, out _, out double pitch);
            int p = (int)Math.Round(pitch);
            bool tonal = v.Kind != HubVoiceKind.Noise;
            if (tonal && (p > 0x3FFF || PitchGuard.Blocks(v.Hz)))
            {
                if (!k.OutOfRange[ch] && p > 0x3FFF) NotesOutOfRange++;
                k.OutOfRange[ch] = true;
                Vol(d, r, 0, 0); return;
            }
            k.OutOfRange[ch] = false;
            p = Math.Clamp(p, 1, 0x3FFF);
            if (k.Srcn[ch] != src) { k.Srcn[ch] = src; d.Write((byte)(r + 4), (byte)src); k.Kon |= 1 << ch; }
            d.Write((byte)(r + 2), (byte)p); d.Write((byte)(r + 3), (byte)(p >> 8));
            float level = Math.Clamp(v.Level, 0f, 1f);
            float pan = Math.Clamp(v.Pan, -1f, 1f);
            int left = (int)Math.Round(VolumeMax * level * (pan > 0 ? 1 - pan : 1)), right = (int)Math.Round(VolumeMax * level * (pan < 0 ? 1 + pan : 1));
            Vol(d, r, left, right);
        }

        private static void Vol(NesEmulator.Snes.DSP_SFC d, int r, int left, int right) { if (d.Regs[r] != (byte)left) d.Write((byte)r, (byte)left); if (d.Regs[r + 1] != (byte)right) d.Write((byte)(r + 1), (byte)right); }

        protected override void Stop(int b, int kind, int ch) { var k = banks[b]; if (k != null) Vol(k.Dsp, ch << 4, 0, 0); }

        protected override void EndApply()
        {
            for (int i = 0; i < liveCount; i++) { var k = live[i]; if (k.Kon != 0) { k.Dsp.Write(0x4C, (byte)k.Kon); k.Kon = 0; } }
        }

        protected override void SilenceAll()
        {
            for (int i = 0; i < liveCount; i++) { var k = live[i]; for (int v = 0; v < 8; v++) { Vol(k.Dsp, v << 4, 0, 0); k.Srcn[v] = -1; } k.Kon = 0; }
        }

        protected override void WriteRaw(int b, ushort address, byte value) => banks[b]!.Dsp.Write(address & 0x7F, value);

        /// <summary>The DSP registers of a bank (tests: what the chip was told).</summary>
        public byte[] Registers(int bank) => banks[bank]?.Dsp.Regs ?? Array.Empty<byte>();

        public override void Rebase(double seconds) { if (samplesDone >= 0) samplesDone = (long)Math.Floor(seconds * DspRate); }

        public override void RunTo(double seconds)
        {
            long target = (long)Math.Floor(seconds * DspRate);
            if (samplesDone < 0) { samplesDone = target; return; }      // the chip starts where it is first run: no catching up from the guest's power-on
            int fill = 0;
            while (samplesDone < target)
            {
                int sumL = 0, sumR = 0;
                for (int i = 0; i < liveCount; i++) { var k = live[i]; k.Dsp.RunSample(); sumL += k.L; sumR += k.R; }
                blockL[fill] = sumL * (1f / 32768f); blockR[fill] = sumR * (1f / 32768f);
                samplesDone++;
                if (++fill == blockL.Length) { Push(fill); fill = 0; }
            }
            if (fill > 0) Push(fill);
        }

        private void Push(int n)
        {
            if (convL == null) { for (int i = 0; i < n; i++) Put(blockL[i], blockR[i]); return; }
            convL.Push(blockL.AsSpan(0, n)); convR!.Push(blockR.AsSpan(0, n));
            while (true)
            {
                int a = convL.Pull(pullL), b = convR.Pull(pullR.AsSpan(0, a));
                if (a <= 0) break;
                for (int i = 0; i < Math.Min(a, b); i++) Put(pullL[i], pullR[i]);
            }
        }

        private void Put(float l, float r)
        {
            if (ringCount >= ring.Length - 2) { ringHead = (ringHead + 2) & (ring.Length - 1); ringCount -= 2; }
            int w = (ringHead + ringCount) & (ring.Length - 1);
            ring[w] = (short)Math.Clamp(l * 32767f, -32768f, 32767f); ring[(w + 1) & (ring.Length - 1)] = (short)Math.Clamp(r * 32767f, -32768f, 32767f);
            ringCount += 2;
        }

        public override int ReadSamples(Span<short> dest)
        {
            int n = Math.Min(dest.Length & ~1, ringCount);
            for (int i = 0; i < n; i++) dest[i] = ring[(ringHead + i) & (ring.Length - 1)];
            ringHead = (ringHead + n) & (ring.Length - 1); ringCount -= n;
            return n;
        }
    }
}

namespace NesEmulator.Snes
{
    /// <summary>
    /// SNES ENHANCED picture chip: <see cref="PPU_SFC"/> itself (a subclass, not a fork: a SNES game draws on the stock pipeline, untouched) plus the enhanced picture contract (<see cref="IEnhancedPictureChip"/>):
    /// another console's frame, as layers (<see cref="ExtPicture"/>), drawn at the guest's own size - a NES game's whole 256 x 240 (the SNES shows 224 or 239 lines), a Genesis game's 320 x 224 or 240 (the SNES is 256
    /// wide), a Game Boy's 160 x 144, a Master System's 256 x 192 to 240, a Game Gear's 160 x 144 - with as many layers as the guest uses (a Genesis plane B, plane A and the window; a Game Boy's background and window)
    /// in the SNES's 15-bit colours (32,768: every guest colour within half a step of 8 levels), the guest's colour math done in them the SNES's way.
    /// </summary>
    public sealed class PPU_SFCS : PPU_SFC, IEnhancedPictureChip
    {
        private readonly ExtPicture layers = new();
        private readonly LayerPainter painter = new(new Rules555());

        public ExtPicture Layers => layers;
        public void DrawLayers() => painter.Draw(layers);
        public uint[] Picture => painter.Picture;
        public int PictureWidth => painter.Width;
        public int PictureHeight => painter.Height;
        public string EnhancedName => "SNES enhanced";
        public string ColourLimit => "the SNES's 15-bit colours (32,768), colour math in them";
        /// <summary>Pixels the last picture drew with the guest's colour math (tests).</summary>
        public int MathPixels => painter.MathPixels;

        /// <summary>An RGB colour as a SNES colour (5 bits a channel, rounded).</summary>
        public static ushort To555(uint rgb) => (ushort)(Ch(rgb >> 16) | Ch(rgb >> 8) << 5 | Ch(rgb) << 10);
        private static int Ch(uint c) => ((int)(c & 0xFF) * 31 + 127) / 255;
        /// <summary>A SNES colour as the S-PPU shows it at full brightness (0xFFRRGGBB).</summary>
        public static uint Argb555(ushort c) { int r = c & 0x1F, g = (c >> 5) & 0x1F, b = (c >> 10) & 0x1F; return 0xFF000000u | (uint)((r * 255 / 31) << 16 | (g * 255 / 31) << 8 | (b * 255 / 31)); }

        private sealed class Rules555 : LayerPainter.Rules
        {
            public override uint Colour(uint rgb) => Argb555(To555(rgb));
            /// <summary>The SNES's own colour math: the pixel and the fixed colour in 15-bit, added or subtracted, halved when asked (<see cref="PPU_SFC.Blend"/>).</summary>
            public override uint MathPixel(ExtPicture e, uint rgb, int line)
            {
                int mode = e.MathMode[line];
                return Argb555(Blend(To555(rgb), To555(e.MathRgb[line]), (mode & 0x80) != 0, (mode & 0x40) != 0));
            }
        }
    }

    /// <summary>
    /// SNES ENHANCED sound unit on the SNES's own menu (id <c>SFCS</c>): <see cref="APU_SFC"/> itself - the game's SPC700 + S-DSP, untouched, bank 0 - plus extra S-DSP banks (<see cref="IApuExtChannels"/>: 8 voices each,
    /// written as DSP registers, made when first written) that only a cross-console bridge drives. Nothing on the SNES's own console writes them, so a SNES game sounds bit-identical to <c>SFC</c>; its state is APU_SFC's.
    /// </summary>
    public sealed class APU_SFCS : APU_SFC, ISnesApu, IApuExtChannels
    {
        private SfcVoiceChip? extra;
        private int extBanks = 1;
        private short[] mix = Array.Empty<short>();

        public new string CoreName => "SFCS";
        public int ExtBanks => extBanks;
        public int MaxExtBanks => SfcVoiceChip.MaxBankCount;
        public int EnsureExtBanks(int wanted) => extBanks = Math.Clamp(Math.Max(extBanks, wanted), 1, MaxExtBanks);
        /// <summary>The extra banks written so far (tests).</summary>
        public int ExtBanksInUse => extra?.BanksInUse ?? 0;

        public void WriteExtRegister(int bank, ushort address, byte value)
        {
            if (bank < 1 || bank > extBanks) throw new ArgumentOutOfRangeException(nameof(bank), $"APU_SFCS has extra banks 1 to {extBanks} (EnsureExtBanks adds more, up to {MaxExtBanks})");
            extra ??= new SfcVoiceChip(SfcVoiceChip.DspRate);
            if (bank == 1) extra.WriteRegister(address, value);
            else { extra.EnsureExtBanks(bank - 1); extra.WriteExtRegister(bank - 1, address, value); }
        }

        public new void RunTo(long masterClock)
        {
            base.RunTo(masterClock);
            extra?.RunTo(masterClock / 21_477_272.0);
        }

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
}

namespace NesEmulator
{
    /// <summary>A NES game on the SNES ENHANCED picture chip (NES board PPU id <c>SNESS</c>, "SNES (enhanced)"): the NES picture as layers (<see cref="NesLayeredPpu"/>) drawn by <see cref="NesEmulator.Snes.PPU_SFCS"/> at the
    /// NES's whole 256 x 240 in the SNES's 15-bit colours, all 64 sprites with the NES's 8 a line. The stock SNES chip (<see cref="PPU_SNES"/>, id SNES) draws it through the S-PPU's own pipeline, 239 lines.</summary>
    public sealed class PPU_SNESS : NesLayeredPpu
    {
        public PPU_SNESS(Bus bus) : base(bus, new NesEmulator.Snes.PPU_SFCS()) { }
        public override string CoreName => $"SNESS:SNES enhanced+{front.CoreName}";
        public override string Description => "The SNES picture chip, enhanced, drawing a NES game whole (256 x 240, every sprite) in the SNES's 15-bit colours, with a NES PPU keeping the register behaviour";
        public override string BridgeName => $"NES game on the SNES enhanced picture chip (front {front.CoreName})";
    }

    /// <summary>A NES game's sound on the SNES ENHANCED sound chip (NES board APU id <c>SNESS</c>): pulse 1 and 2 as S-DSP voices of their own duty, the triangle a triangle sample, the noise (white or the short mode)
    /// a noise sample, each on a voice of its own (<see cref="NesApuOnChip"/>). The stock SNES sound (<see cref="APU_SNES"/>, id SNES) plays them through an SPC700 driver on one DSP.
    /// Not in the FL plugin's chip list (its chip indexes are append-only: Plugin/BrokenNes2.Plugin/Bn2Consoles.cs keeps the enhanced NES-board ids of the other consoles out).</summary>
    public sealed class APU_SNESS : NesApuOnChip
    {
        public APU_SNESS(Bus bus) : base(bus, "SNES:SFCS") { }
        public override string CoreName => $"SNESS:S-DSP banks+{Front.CoreName}";
        public override string Description => "NES channels played on the SNES enhanced sound chip: every channel on an S-DSP voice of its own, pulses with their duty (the DMC is dropped and counted)";
    }
}
