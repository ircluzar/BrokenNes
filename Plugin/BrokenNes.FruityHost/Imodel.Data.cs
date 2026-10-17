// Round 6 (imodel): made-up snapshots with known content, and the rendering helpers the instrument-model checks use. No ROM is involved; the real-game checks build their own snapshots (Imodel.Games.cs).
using System.Security.Cryptography;
using BrokenNes2;
using NesEmulator.Mix;
using NesEmulator.Sega;

namespace BrokenNes.FruityHost;

internal static class ImodelData
{
    // ---- SNES ------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>One BRR block: header (range, filter, loop, end) and sixteen signed 4-bit samples (high nibble first).</summary>
    public static byte[] Brr(int range, int filter, bool loop, bool end, params int[] nibbles)
    {
        var b = new byte[9];
        b[0] = (byte)(range << 4 | filter << 2 | (loop ? 2 : 0) | (end ? 1 : 0));
        for (int i = 0; i < 16; i++)
        {
            int n = (nibbles.Length == 0 ? 0 : nibbles[i % nibbles.Length]) & 15;
            b[1 + i / 2] |= (byte)(i % 2 == 0 ? n << 4 : n);
        }
        return b;
    }

    /// <summary>A SNES sound memory with four samples and two game voices set up: slot 0 a looping square (one block), slot 1 a four-block one-shot ramp with the BRR filters in use, slot 2 a three-block loop that loops from its
    /// second block, slot 3 empty; voice 0 plays slot 0 with an ADSR envelope, voice 2 plays slot 1 with a GAIN envelope; slot 2 has no voice (so it gets the organ-like default). Echo is on for voice 2.</summary>
    public static SpcSnapshot SnesWithSamples()
    {
        var aram = new byte[SpcSnapshot.AramSize];
        var dsp = new byte[SpcSnapshot.DspSize];
        dsp[0x5D] = 0x01;                                                  // directory at $0100
        void Dir(int slot, int start, int loop) { int a = 0x100 + slot * 4; aram[a] = (byte)start; aram[a + 1] = (byte)(start >> 8); aram[a + 2] = (byte)loop; aram[a + 3] = (byte)(loop >> 8); }
        void Put(int at, byte[] block) => Array.Copy(block, 0, aram, at, block.Length);
        Dir(0, 0x200, 0x200);
        Put(0x200, Brr(9, 0, true, true, 7, 7, 7, 7, 7, 7, 7, 7, 9, 9, 9, 9, 9, 9, 9, 9));
        Dir(1, 0x240, 0x240);
        Put(0x240, Brr(8, 0, false, false, 0, 1, 2, 3, 4, 5, 6, 7, 6, 5, 4, 3));
        Put(0x249, Brr(8, 1, false, false, 1, 2, 3, 2, 1, 0, 15, 14, 13, 14, 15, 0));
        Put(0x252, Brr(9, 2, false, false, 3, 5, 7, 5, 3, 1, 15, 13, 11, 13, 15, 1));
        Put(0x25B, Brr(7, 3, false, true, 2, 4, 6, 4, 2, 0, 14, 12, 10, 12, 14, 0));
        Dir(2, 0x280, 0x289);
        Put(0x280, Brr(10, 0, false, false, 0, 4, 7, 4, 0, 12, 9, 12, 0, 4, 7, 4, 0, 12, 9, 12));
        Put(0x289, Brr(10, 0, false, false, 7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8));
        Put(0x292, Brr(10, 0, true, true, 3, 3, 3, 3, 12, 12, 12, 12, 3, 3, 3, 3, 12, 12, 12, 12));
        // the game's DSP state
        dsp[0x0C] = dsp[0x1C] = 0x70;                                      // master volume
        dsp[0x2C] = dsp[0x3C] = 0x28;                                      // echo volume
        dsp[0x0D] = 0x20;                                                  // echo feedback
        dsp[0x4D] = 0x04;                                                  // echo on voice 2
        dsp[0x6D] = 0x80; dsp[0x7D] = 0x01;                                // echo buffer at $8000, 16 ms
        dsp[0x0F] = 0x7F;                                                  // FIR tap 0 = pass-through
        dsp[0x6C] = 0x00;                                                  // echo writes on
        // voice 0: sample 0, ADSR (instant attack, sustain level 7, rate 8)
        dsp[0x00] = 0x40; dsp[0x01] = 0x40; dsp[0x02] = 0x00; dsp[0x03] = 0x10; dsp[0x04] = 0; dsp[0x05] = 0x8F; dsp[0x06] = 0xE8; dsp[0x07] = 0; dsp[0x08] = 0x40;
        // voice 2: sample 1, GAIN direct 100 (ADSR off)
        dsp[0x20] = 0x30; dsp[0x21] = 0x30; dsp[0x22] = 0x00; dsp[0x23] = 0x10; dsp[0x24] = 1; dsp[0x25] = 0x00; dsp[0x26] = 0x00; dsp[0x27] = 100; dsp[0x28] = 0x00;
        return new SpcSnapshot(aram, dsp);
    }

    /// <summary>The note sequence every SNES sampler golden and audibility render plays: eight voices, a pitch glide, a note-off, the tail. 48 kHz, 256-frame blocks. Returns the interleaved stereo PCM.</summary>
    public static float[] RenderSnes(SpcSnapshot snap, int[]? srcn = null, int blocksPerStep = 3)
    {
        srcn ??= [0, 1, 2];
        var s = new SnesSampler(snap, 48000);
        var all = new List<float>();
        var buf = new float[256 * 2];
        void Run(int blocks) { for (int i = 0; i < blocks; i++) { s.Render(buf, 256); all.AddRange(buf); } }
        s.NoteOn(0, srcn[0], SnesSampler.PitchFor(0), 90, 90); Run(blocksPerStep);
        s.NoteOn(1, srcn[1 % srcn.Length], SnesSampler.PitchFor(400), 100, 60); Run(blocksPerStep);
        s.Update(0, SnesSampler.PitchFor(300), 70, 70); Run(blocksPerStep);
        s.NoteOn(2, srcn[2 % srcn.Length], SnesSampler.PitchFor(-500), 127, 127); s.NoteOn(3, srcn[0], SnesSampler.PitchFor(1200), 50, 50); Run(blocksPerStep * 2);
        s.NoteOff(1); Run(blocksPerStep);
        s.NoteOff(0); s.NoteOff(2); s.NoteOff(3); Run(blocksPerStep * 3);
        return all.ToArray();
    }

    // ---- Game Boy -------------------------------------------------------------------------------------------------------------------------------------------

    public static GbSnapshot GbWithSettings(bool cgb = false)
    {
        var regs = new byte[GbSnapshot.RegCount];
        regs[0x10 - 0x10] = 0x00;                 // NR10: no sweep
        regs[0x11 - 0x10] = 0x80;                 // NR11: duty 2 (50%)
        regs[0x12 - 0x10] = 0xF3;                 // NR12: volume 15, decrease, pace 3
        regs[0x16 - 0x10] = 0x40;                 // NR21: duty 1 (25%)
        regs[0x17 - 0x10] = 0xA5;                 // NR22: volume 10, increase, pace 5
        regs[0x1A - 0x10] = 0x80;                 // NR30: wave DAC on
        regs[0x1C - 0x10] = 0x20;                 // NR32: output level 100%
        regs[0x21 - 0x10] = 0xF1;                 // NR42: volume 15, decrease, pace 1
        regs[0x22 - 0x10] = 0x35;                 // NR43
        var wave = new byte[GbSnapshot.WaveSize];
        for (int i = 0; i < 16; i++) wave[i] = (byte)((i * 2 & 15) << 4 | ((i * 2 + 1) & 15));     // a rising saw over the first half, wrapping
        return new GbSnapshot(regs, wave, cgb);
    }

    public static float[] RenderGb(GbSnapshot snap)
    {
        var s = new GbSampler(snap, 48000);
        var all = new List<float>();
        var buf = new float[256 * 2];
        void Run(int blocks) { for (int i = 0; i < blocks; i++) { s.Render(buf, 256); all.AddRange(buf); } }
        s.NoteOn(0, 440, 12, -1, false); Run(20);
        s.Pitch(0, 494, false); Run(10);
        s.NoteOff(0); s.NoteOn(1, 330, 9, 3, false); Run(20);
        s.NoteOff(1); s.NoteOn(2, 220, 15, -1, false); Run(20);
        s.Pitch(2, 247, false); Run(10);
        s.NoteOff(2); s.NoteOn(3, 500, 11, -1, true); Run(20);
        s.NoteOff(3); Run(10);
        return all.ToArray();
    }

    // ---- Sega -----------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The 30 bytes of a YM2612 channel patch as <see cref="SegaSnapshot.OpnPatch"/> holds them: four operators in algorithm order, seven registers each ($30 $40 $50 $60 $70 $80 $90), then $B0 and $B4.</summary>
    public static byte[] OpnPatch(int alg, int fb, int pan, int ams, int fms, params FmOperator[] ops)
    {
        var p = new byte[SegaSnapshot.OpnPatchBytes];
        for (int n = 0; n < 4; n++)
        {
            var o = ops[n];
            p[n * 7 + 0] = (byte)(o.Dt << 4 | o.Mul);
            p[n * 7 + 1] = (byte)o.Tl;
            p[n * 7 + 2] = (byte)(o.Rs << 6 | o.Ar);
            p[n * 7 + 3] = (byte)o.D1r;
            p[n * 7 + 4] = (byte)o.D2r;
            p[n * 7 + 5] = (byte)(o.Sl << 4 | o.Rr);
            p[n * 7 + 6] = 0;
        }
        p[28] = (byte)(fb << 3 | alg);
        p[29] = (byte)(pan << 6 | ams << 4 | fms);
        return p;
    }

    public static SegaSnapshot OpnWithPatches()
    {
        // the Genesis NTSC clocks: the PSG runs at master / 15, the YM2612 at master / 7
        var snap = new SegaSnapshot(SegaSnapshot.KindOpn, 3_579_545, PsgVariant.MegaDrive) { OpnClock = 7_670_454 };
        // channel 0: an electric piano-ish 2-op pair (algorithm 4: operators 2 and 4 carry), channel 1: a sine (algorithm 7), channel 2: the "Bass growl"-like chain (algorithm 0), channels 3-5 never set up
        snap.OpnPatch[0] = OpnPatch(4, 3, 3, 0, 0, new FmOperator(1, 30, 0, 31, 4, 3, 2, 6), new FmOperator(1, 0, 0, 31, 6, 2, 2, 8), new FmOperator(14, 40, 0, 31, 8, 4, 3, 7), new FmOperator(1, 0, 0, 31, 3, 2, 1, 6));
        snap.OpnPatch[1] = OpnPatch(7, 0, 3, 0, 0, new FmOperator(1, 0, 0, 31, 0, 0, 0, 10), new FmOperator(2, 127, 0, 31, 0, 0, 0, 10), new FmOperator(3, 127, 0, 31, 0, 0, 0, 10), new FmOperator(4, 127, 0, 31, 0, 0, 0, 10));
        snap.OpnPatch[2] = OpnPatch(0, 6, 2, 1, 3, new FmOperator(1, 24, 1, 28, 2, 2, 1, 5, 1), new FmOperator(1, 30, 0, 31, 5, 3, 2, 6), new FmOperator(1, 20, 5, 30, 4, 2, 2, 7), new FmOperator(1, 0, 0, 31, 3, 1, 1, 8));
        snap.OpnKeyed[0] = true;
        snap.OpnLfo = 0x0B;                                              // LFO on, frequency 3
        // the DAC: a looping 100-sample cycle sampled at 11025 Hz and a one-shot decaying click
        var loopData = new byte[600];
        for (int i = 0; i < loopData.Length; i++) loopData[i] = (byte)(128 + 100 * Math.Sin(2 * Math.PI * i / 100.0));
        var shot = new byte[800];
        for (int i = 0; i < shot.Length; i++) shot[i] = (byte)(128 + (int)(110 * Math.Exp(-i / 160.0) * Math.Sin(i * 0.31)));
        snap.Dac = [new DacSample(11025, loopData, 500, 600, 3) { CycleHz = 110.25 }, new DacSample(11025, shot, -1, -1, 5)];
        return snap;
    }

    public static SegaSnapshot PsgOnly()
    {
        var snap = new SegaSnapshot(SegaSnapshot.KindPsg, 3_579_545, PsgVariant.Sega);
        snap.PsgPeriod[0] = 0x1FC; snap.PsgPeriod[1] = 0x150; snap.PsgPeriod[2] = 0x0A0;
        snap.PsgVolume[0] = 2; snap.PsgVolume[1] = 5; snap.PsgVolume[2] = 15; snap.PsgVolume[3] = 4;
        snap.PsgNoise = 5;
        return snap;
    }

    public static SegaSnapshot OpllWithUser()
    {
        var snap = new SegaSnapshot(SegaSnapshot.KindOpll, 3_579_545, PsgVariant.Sega);
        snap.PsgPeriod[0] = 0x1FC; snap.PsgVolume[0] = 3; snap.PsgNoise = 4;
        // the user instrument: modulator 0x21 (sustained, multiple 1), carrier 0x21; modulator TL 0x1A; carrier waveform 0, feedback 3; attack/decay F4 F3; sustain/release 0x84 0x34
        byte[] user = [0x21, 0x21, 0x1A, 0x03, 0xF4, 0xF3, 0x84, 0x34];
        Array.Copy(user, snap.Opll, 8);
        snap.Opll[0x30] = 0x00; snap.Opll[0x20] = 0x10;                  // channel 0 on the user instrument, keyed
        return snap;
    }

    /// <summary>Renders notes on slots of a Sega sampler: slot, instrument and Hz/velocity per step; returns interleaved stereo PCM at 48 kHz.</summary>
    public static float[] RenderSega(SegaSnapshot snap, params (int Slot, int Instrument, double Hz, double Velocity)[] notes)
    {
        SegaSampler s = snap.Kind switch { SegaSnapshot.KindOpn => new OpnSampler(snap, 48000), SegaSnapshot.KindOpll => new OpllSampler(snap, 48000), _ => new PsgSampler(snap, 48000) };
        var all = new List<float>();
        var buf = new float[256 * 2];
        void Run(int blocks) { for (int i = 0; i < blocks; i++) { s.Render(buf, 256); all.AddRange(buf); } }
        foreach (var n in notes) { s.NoteOn(n.Slot, n.Instrument, n.Hz, n.Velocity); Run(12); }
        Run(10);
        foreach (var n in notes) s.NoteOff(n.Slot);
        Run(12);
        return all.ToArray();
    }

    // ---- hashing and comparison ---------------------------------------------------------------------------------------------------------------------------

    public static string Hash(float[] pcm)
    {
        var bytes = new byte[pcm.Length * 4];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }

    public static double Rms(float[] pcm) { double s = 0; foreach (var v in pcm) s += (double)v * v; return pcm.Length == 0 ? 0 : Math.Sqrt(s / pcm.Length); }

    /// <summary>The RMS of the difference between two renders (the longer one is cut): how far apart they sound.</summary>
    public static double DiffRms(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double s = 0;
        for (int i = 0; i < n; i++) { double d = a[i] - b[i]; s += d * d; }
        return n == 0 ? 0 : Math.Sqrt(s / n);
    }
}
