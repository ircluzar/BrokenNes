using NesEmulator.Mix;
using NesEmulator.Sega;

namespace BrokenNes2;

/// <summary>
/// Instrument Runaway on a Sega game: a private set of the game's sound chips, set up from a <see cref="SegaSnapshot"/> and played by FL's notes. One per FL instance (the instances never have to agree on timing); polyphonic
/// (the PSG's three tone voices and its noise, the YM2413's nine channels, the YM2612's six FM channels, eight DAC voices); pitch and level follow FL's live values, so slides and bends work. Allocation-free once made.
/// <para>Notes are given to <b>slots</b>: each sampler has a fixed set (see <see cref="SegaSnapshot.SlotBase"/>) and the caller (the plugin's voice loop) picks a free one for a note in the range of its instrument.</para>
/// </summary>
internal abstract class SegaSampler
{
    public abstract void NoteOn(int slot, int instrument, double hz, double velocity);
    public abstract void Update(int slot, double hz, double velocity);
    public abstract void NoteOff(int slot);
    /// <summary>Fills <paramref name="dest"/> with <paramref name="frames"/> interleaved stereo frames at the host rate.</summary>
    public abstract void Render(Span<float> dest, int frames);

    /// <summary>Notes the chips could not play (the PSG's lowest note is 109 Hz; the FM chips stop near 6 kHz), counted once per note: nothing is dropped silently.</summary>
    public long NotesDropped { get; protected set; }

    /// <summary>The decibels of attenuation a linear level asks for (0 at full level).</summary>
    protected static double AttenuationDb(double velocity) => velocity >= 1 ? 0 : -20 * Math.Log10(Math.Max(velocity, 1e-5));

    /// <summary>A queue of stereo frames filled by pulling a chip in small steps until a block's worth is there.</summary>
    protected sealed class FrameRing
    {
        private readonly float[] l = new float[1 << 13], r = new float[1 << 13];
        private int head, count;
        public int Count => count;
        public void Push(float a, float b)
        {
            if (count == l.Length) { head = (head + 1) & (l.Length - 1); count--; }
            int at = (head + count) & (l.Length - 1);
            l[at] = a; r[at] = b; count++;
        }
        public void Pop(Span<float> dest, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                if (count == 0) { dest[2 * i] = dest[2 * i + 1] = 0; continue; }
                dest[2 * i] = l[head]; dest[2 * i + 1] = r[head]; head = (head + 1) & (l.Length - 1); count--;
            }
        }
        public void Clear() { head = count = 0; }
    }

    /// <summary>Adds <paramref name="src"/> into <paramref name="dest"/> (both interleaved stereo).</summary>
    protected static void Add(Span<float> dest, ReadOnlySpan<float> src, int frames)
    {
        for (int i = 0; i < frames * 2; i++) dest[i] += src[i];
    }
}

/// <summary>
/// The SN76489 as a sampler: slots 0 to 2 are the three square tones, slot 3 the noise voice (white or periodic as the game had set it). The noise voice's pitch is its shift rate: the third tone register sets the rate (so
/// that voice's tone is unavailable while the noise plays), and a periodic noise buzzes at the note. The chip's lowest tone is 109 Hz (its 10-bit period); a lower note is silent and counted.
/// </summary>
internal sealed class PsgSampler : SegaSampler
{
    public const int Slots = 4, NoiseSlot = 3;
    private readonly Sn76489 psg;
    private readonly SegaSnapshot snap;
    private readonly int hostRate;
    private readonly FrameRing ring = new();
    private readonly short[] pcm = new short[1024];
    private double clockCarry;
    private readonly int[] period = [-1, -1, -1];
    private readonly int[] volume = [-1, -1, -1, -1];
    private int noiseControl = -1;
    private bool noiseWhite;
    private readonly bool[] outOfRange = new bool[Slots];

    public PsgSampler(SegaSnapshot snapshot, int hostRate)
    {
        snap = snapshot; this.hostRate = hostRate;
        psg = new Sn76489(snapshot.PsgClock, hostRate, snapshot.PsgVariant) { Gain = NesEmulator.APU_SMS.PsgGain };
        psg.WriteStereo(0xFF);
        for (int c = 0; c < 4; c++) SetVolume(c, 15);
    }

    private void SetPeriod(int ch, int n)
    {
        if (period[ch] == n) return;
        period[ch] = n;
        psg.Write((byte)(0x80 | ch << 5 | (n & 15))); psg.Write((byte)((n >> 4) & 0x3F));
    }

    private void SetVolume(int ch, int v)
    {
        if (volume[ch] == v) return;
        volume[ch] = v;
        psg.Write((byte)(0x90 | ch << 5 | v));
    }

    private void SetNoise(int control)
    {
        if (noiseControl == control) return;
        noiseControl = control;
        psg.Write((byte)(0xE0 | control));      // reloads the noise shift register: only when the mode or rate changes
    }

    /// <summary>The attenuator (0 loudest to 14; 15 is off) nearest to a level, in the chip's 2 dB steps.</summary>
    private static int Attenuation(double velocity) => velocity <= 0.003 ? 15 : (int)Math.Clamp(Math.Round(AttenuationDb(velocity) / 2), 0, 14);

    /// <summary>The periodic noise buzzes at the shift rate over 16: the shift rate that makes a note.</summary>
    private int NoiseTone(double hz) => Math.Clamp((int)Math.Round(snap.PsgClock / (32.0 * Math.Max(1.0, hz * 16))), 1, 1023);

    public override void NoteOn(int slot, int instrument, double hz, double velocity)
    {
        outOfRange[slot] = false;
        if (slot == NoiseSlot) noiseWhite = snap.IsWhiteNoise(instrument);      // the instrument says which noise: white or periodic
        Update(slot, hz, velocity);
    }

    public override void Update(int slot, double hz, double velocity)
    {
        if (slot == NoiseSlot)
        {
            SetPeriod(2, NoiseTone(hz)); SetVolume(2, 15);
            SetNoise((noiseWhite ? 4 : 0) | 3);
            SetVolume(3, Attenuation(velocity));
            return;
        }
        int n = (int)Math.Round(snap.PsgClock / (32.0 * Math.Max(hz, 1e-3)));
        if (n > 1023 || n < 2)
        {
            if (!outOfRange[slot]) { outOfRange[slot] = true; NotesDropped++; }
            SetVolume(slot, 15);
            return;
        }
        outOfRange[slot] = false;
        SetPeriod(slot, n); SetVolume(slot, Attenuation(velocity));
    }

    public override void NoteOff(int slot) => SetVolume(slot, 15);

    public override void Render(Span<float> dest, int frames)
    {
        int guard = 0;
        while (ring.Count < frames && guard++ < 64)
        {
            // the chip runs by its clock; the host frames wanted, a little more when the converter is behind
            clockCarry += Math.Max(1, frames - ring.Count) * (double)snap.PsgClock / hostRate;
            int clocks = (int)clockCarry; clockCarry -= clocks;
            psg.Run(clocks);
            int got;
            while ((got = psg.ReadSamples(pcm)) > 0)
                for (int i = 0; i + 1 < got; i += 2) ring.Push(pcm[i] * (1f / 32768f), pcm[i + 1] * (1f / 32768f));
        }
        ring.Pop(dest, frames);
    }
}

/// <summary>
/// The YM2413 as a sampler. Slots 0 to 8 are the chip's nine channels, 9 to 12 the PSG (the unit has one). Instrument 0 is the game's user instrument (its eight registers loaded as the game left them) and 1 to 15 the chip's own;
/// a note's pitch is set from its F-number and block by nearest (the instrument's fundamental, the common divisor of its two frequency multiples, is the note) and its level from the volume register (3 dB a step). Percussive instruments
/// (a guitar, a piano) fade by themselves, as they do on the chip.
/// </summary>
internal sealed class OpllSampler : SegaSampler
{
    public const int FmSlots = 9, PsgBase = 9, Slots = 13;
    private readonly Ym2413 fm;
    private readonly Ym2413Resampler res;
    private readonly SegaSnapshot snap;
    private readonly FrameRing ring = new();
    private readonly PsgSampler psg;
    private readonly float[] psgBuf = new float[512];
    private readonly int hostRate;
    private readonly int[] inst = new int[FmSlots], blockOf = new int[FmSlots], fnumOf = new int[FmSlots], volOf = new int[FmSlots];
    private readonly bool[] keyed = new bool[FmSlots], outOfRange = new bool[FmSlots];
    private readonly double userFundamental;
    private int held, psgHeld, quiet;

    public OpllSampler(SegaSnapshot snapshot, int hostRate)
    {
        snap = snapshot; this.hostRate = hostRate;
        fm = new Ym2413(snapshot.PsgClock); fm.Reset(); fm.ResetPatch();
        res = new Ym2413Resampler(snapshot.PsgClock / (double)Ym2413.ClocksPerSample, hostRate);
        for (int r = 0; r < 8; r++) fm.WriteRegister(r, snapshot.Opll[r]);
        fm.WriteRegister(0x0E, 0);                                                    // no rhythm mode: nine melodic channels
        userFundamental = FundamentalOf(snapshot.Opll[0] & 15, snapshot.Opll[1] & 15);
        psg = new PsgSampler(snapshot, hostRate);
        Array.Fill(volOf, -1); Array.Fill(blockOf, -1);
    }

    private static double FundamentalOf(int modMl, int carMl)
    {
        int a = OpllPatchBank.MultipleTimesTwo(modMl), b = OpllPatchBank.MultipleTimesTwo(carMl);
        while (b != 0) (a, b) = (b, a % b);
        return a / 2.0;
    }

    private double Fundamental(int instrument) => instrument <= 0 ? userFundamental : OpllPatchBank.Patches[Math.Min(instrument, 15) - 1].Fundamental;

    private static int VolumeRegister(double velocity) => velocity <= 0.003 ? 15 : (int)Math.Clamp(Math.Round(AttenuationDb(velocity) / OpllPatchBank.StepDb), 0, 15);

    public override void NoteOn(int slot, int instrument, double hz, double velocity)
    {
        quiet = 0;
        if (slot >= PsgBase) { psg.NoteOn(slot - PsgBase, instrument, hz, velocity); psgHeld++; return; }
        inst[slot] = Math.Clamp(instrument, 0, 15);
        outOfRange[slot] = false;
        if (keyed[slot]) { fm.WriteRegister(0x20 + slot, (byte)(Math.Max(0, blockOf[slot]) << 1)); keyed[slot] = false; }   // key up first: a channel taken over starts its envelope again
        else held++;
        volOf[slot] = -1; blockOf[slot] = -1;
        Apply(slot, hz, velocity);
    }

    private void Apply(int slot, double hz, double velocity)
    {
        if (!OpllPatchBank.TryPitch(hz / Fundamental(inst[slot]), snap.PsgClock, out int blk, out int fnum, out _))
        {
            if (!outOfRange[slot]) { outOfRange[slot] = true; NotesDropped++; }
            if (keyed[slot]) { fm.WriteRegister(0x20 + slot, (byte)(Math.Max(0, blockOf[slot]) << 1)); keyed[slot] = false; }
            return;
        }
        outOfRange[slot] = false;
        int vol = VolumeRegister(velocity);
        if (vol != volOf[slot]) { volOf[slot] = vol; fm.WriteRegister(0x30 + slot, (byte)(inst[slot] << 4 | vol)); }
        if (blk != blockOf[slot] || fnum != fnumOf[slot] || !keyed[slot])
        {
            blockOf[slot] = blk; fnumOf[slot] = fnum;
            fm.WriteRegister(0x10 + slot, (byte)(fnum & 0xFF));
            fm.WriteRegister(0x20 + slot, (byte)(0x10 | blk << 1 | fnum >> 8));
            keyed[slot] = true;
        }
    }

    public override void Update(int slot, double hz, double velocity)
    {
        if (slot >= PsgBase) { psg.Update(slot - PsgBase, hz, velocity); return; }
        Apply(slot, hz, velocity);
    }

    public override void NoteOff(int slot)
    {
        if (slot >= PsgBase) { psg.NoteOff(slot - PsgBase); psgHeld = Math.Max(0, psgHeld - 1); return; }
        if (keyed[slot]) { fm.WriteRegister(0x20 + slot, (byte)(Math.Max(0, blockOf[slot]) << 1 | fnumOf[slot] >> 8)); keyed[slot] = false; }
        held = Math.Max(0, held - 1);
    }

    public override void Render(Span<float> dest, int frames)
    {
        dest.Slice(0, frames * 2).Clear();
        if (held > 0 || quiet < hostRate / 5)
        {
            // the chip makes a sample every 72 clocks; each goes through the converter to the host rate
            int guard = 0;
            while (ring.Count < frames && guard++ < 4096)
            {
                res.Push((short)Math.Clamp(fm.Calc(), short.MinValue, short.MaxValue));
                while (res.TryRead(out short o)) { float v = o * (NesEmulator.APU_SMSFM.FmGain / 32768f); ring.Push(v, v); }
            }
            ring.Pop(dest, frames);
            float peak = 0;
            for (int i = 0; i < frames; i++) peak = Math.Max(peak, Math.Abs(dest[2 * i]));
            quiet = peak < 1e-5f && held == 0 ? quiet + frames : 0;
        }
        if (psgHeld > 0)
            for (int done = 0; done < frames; done += 256)
            {
                int n = Math.Min(256, frames - done);
                psg.Render(psgBuf, n);
                for (int i = 0; i < n * 2; i++) dest[done * 2 + i] += psgBuf[i];
            }
    }
}

/// <summary>
/// The YM2612 as a sampler. Slots 0 to 5 are the chip's six FM channels (each loaded with one of the game's channel patches), 6 to 13 eight DAC voices, 14 to 17 the PSG. The FM pitch is set from the F-number and block (the
/// carriers' common multiple is the note); the level is the carriers' total level plus the attenuation the velocity asks for (0.75 dB a step). The DAC voices are software: the game's DAC has one voice, a stream of 8-bit values the Z80
/// writes at a steady rate, and a sound the analyser found in that stream plays at the note's pitch with linear interpolation (a looped sound loops on its cycle and plays at the note; a one-shot plays at its own speed at middle C
/// and through to its end).
/// </summary>
internal sealed class OpnSampler : SegaSampler, IYm2612Sink
{
    public const int FmSlots = 6, DacBase = 6, DacVoices = 8, PsgBase = 14, Slots = 18;
    /// <summary>The note a one-shot DAC sound plays at its own speed on (middle C, FL's default note).</summary>
    public const double OneShotRootHz = 261.6256;
    private const double TotalLevelStepDb = 0.75;
    private static readonly int[] OperatorOffset = [0, 8, 4, 12];

    private readonly SegaSnapshot snap;
    private readonly int hostRate;
    private readonly Ym2612 ym;
    private readonly BandLimitedMixer mixL, mixR;
    private readonly float[] bufL = new float[256], bufR = new float[256];
    private readonly PsgSampler psg;
    private readonly float[] psgBuf = new float[512];
    private float lastL, lastR;
    private long samples, ymClock;
    private readonly double dacUnit;

    private readonly int[] patchOf = new int[FmSlots], loaded = new int[FmSlots], blockOf = new int[FmSlots], fnumOf = new int[FmSlots], attOf = new int[FmSlots];
    private readonly bool[] keyed = new bool[FmSlots], outOfRange = new bool[FmSlots];
    private readonly double[] fundamental = new double[FmSlots];
    private readonly long[] lastOff = Enumerable.Repeat(-1_000_000L, FmSlots).ToArray();
    private int held, psgHeld, quiet;

    private sealed class DacVoice
    {
        public int Sample = -1;
        public double Pos, Step;
        public float Gain, Fade = 1;
        public bool Releasing;
    }
    private readonly DacVoice[] dac = Enumerable.Range(0, DacVoices).Select(_ => new DacVoice()).ToArray();
    private readonly float fadePerFrame;
    private int dacActive;

    public OpnSampler(SegaSnapshot snapshot, int hostRate)
    {
        snap = snapshot; this.hostRate = hostRate;
        ym = new Ym2612(false, snapshot.OpnClock);
        dacUnit = MeasureDacUnit(ym);
        ym.PowerOn();
        ym.Sink = this;
        double chipRate = snapshot.OpnClock / (double)Ym2612.ClocksPerSample;
        mixL = new BandLimitedMixer(chipRate, hostRate); mixR = new BandLimitedMixer(chipRate, hostRate);
        psg = new PsgSampler(snapshot, hostRate);
        fadePerFrame = (float)(1.0 / (0.008 * hostRate));
        for (int ch = 0; ch < FmSlots; ch++)
        {
            fundamental[ch] = CarrierFundamental(snapshot.OpnPatch[ch]);
            patchOf[ch] = -1; loaded[ch] = -1; attOf[ch] = -1; blockOf[ch] = -1;
        }
        Reg(0, 0x22, (byte)snapshot.OpnLfo);
    }

    /// <summary>The chip's DAC as a fraction of full scale for one step of the 8-bit value (measured on the chip itself with the DAC on: the full swing over 255), so a software DAC voice is as loud as the chip's would be.</summary>
    private static double MeasureDacUnit(Ym2612 chip)
    {
        chip.Write(0, 0x2B, 0); chip.Write(1, 0x80, 0);
        chip.Write(0, 0x2A, 0); chip.Write(1, 0xFF, 0);
        chip.RunTo(48L * Ym2612.ClocksPerSample);
        int hi = chip.LastLeft;
        chip.Write(0, 0x2A, 48L * Ym2612.ClocksPerSample); chip.Write(1, 0x00, 48L * Ym2612.ClocksPerSample);
        chip.RunTo(96L * Ym2612.ClocksPerSample);
        int lo = chip.LastLeft;
        return Math.Max(1e-6, (hi - lo) / 255.0 / 32768.0);
    }

    /// <summary>The note a patch's carriers make: the common divisor of their frequency multiples (the multiple field 0 is a half), so a note is set on the chip at the note's frequency over it.</summary>
    internal static double CarrierFundamental(byte[] patch)
    {
        int mask = FmPatchBank.CarriersOf(patch[28] & 7), g = 0;
        for (int n = 0; n < 4; n++)
        {
            if ((mask >> n & 1) == 0) continue;
            int m = patch[n * 7] & 15, two = m == 0 ? 1 : m * 2;
            int a = g, b = two;
            while (b != 0) (a, b) = (b, a % b);
            g = a;
        }
        return g == 0 ? 1 : g / 2.0;
    }

    void IYm2612Sink.Sample(int left, int right)
    {
        float l = left * (1f / 32768f), r = right * (1f / 32768f);
        samples++;
        if (l != lastL) { mixL.Step(samples, l - lastL); lastL = l; }
        if (r != lastR) { mixR.Step(samples, r - lastR); lastR = r; }
    }

    private void Reg(int part, int reg, byte value)
    {
        ym.Write(part * 2, (byte)reg, ymClock);
        ym.Write(part * 2 + 1, value, ymClock);
    }

    private void Key(int ch, bool on) => Reg(0, 0x28, (byte)((on ? 0xF0 : 0) | (ch < 3 ? ch : ch + 1)));

    private void LoadPatch(int ch, int patchIndex)
    {
        var p = snap.OpnPatch[patchIndex];
        int part = ch / 3, c = ch % 3;
        for (int n = 0; n < 4; n++)
            for (int k = 0; k < 7; k++) Reg(part, 0x30 + k * 0x10 + OperatorOffset[n] + c, p[n * 7 + k]);
        Reg(part, 0xB0 + c, p[28]);
        Reg(part, 0xB4 + c, (byte)(p[29] | 0xC0));
        loaded[ch] = patchIndex; attOf[ch] = -1;
        fundamental[ch] = CarrierFundamental(p);
    }

    private void SetLevel(int ch, int att)
    {
        if (attOf[ch] == att) return;
        attOf[ch] = att;
        var p = snap.OpnPatch[loaded[ch]];
        int mask = FmPatchBank.CarriersOf(p[28] & 7), part = ch / 3, c = ch % 3;
        for (int n = 0; n < 4; n++)
            if ((mask >> n & 1) != 0) Reg(part, 0x40 + OperatorOffset[n] + c, (byte)Math.Min(127, (p[n * 7 + 1] & 0x7F) + att));
    }

    private static int Attenuation(double velocity) => velocity <= 0.002 ? 127 : (int)Math.Clamp(Math.Round(AttenuationDb(velocity) / TotalLevelStepDb), 0, 127);

    public override void NoteOn(int slot, int instrument, double hz, double velocity)
    {
        quiet = 0;
        if (slot >= PsgBase) { psg.NoteOn(slot - PsgBase, instrument, hz, velocity); psgHeld++; return; }
        if (slot >= DacBase)
        {
            int index = instrument - 6;
            if (index < 0 || index >= snap.Dac.Length) return;
            var v = dac[slot - DacBase];
            if (v.Sample < 0) dacActive++;
            v.Sample = index; v.Pos = 0; v.Fade = 1; v.Releasing = false;
            SetDac(v, hz, velocity);
            return;
        }
        patchOf[slot] = Math.Clamp(instrument, 0, 5);
        outOfRange[slot] = false;
        if (keyed[slot]) { Key(slot, false); keyed[slot] = false; lastOff[slot] = ymClock; }
        else held++;
        // a key that goes up and down inside one chip sample is no new note to the chip: let it run two samples first so the envelope starts again
        if (ymClock - lastOff[slot] < 2L * Ym2612.ClocksPerSample) { ymClock += 2L * Ym2612.ClocksPerSample; ym.RunTo(ymClock); }
        if (loaded[slot] != patchOf[slot]) LoadPatch(slot, patchOf[slot]);
        blockOf[slot] = -1;
        Apply(slot, hz, velocity);
    }

    private void Apply(int ch, double hz, double velocity)
    {
        if (!FmPatchBank.TryPitch(hz / fundamental[ch], snap.OpnClock, out int blk, out int fnum, out _))
        {
            if (!outOfRange[ch]) { outOfRange[ch] = true; NotesDropped++; }
            if (keyed[ch]) { Key(ch, false); keyed[ch] = false; }
            return;
        }
        outOfRange[ch] = false;
        SetLevel(ch, Attenuation(velocity));
        if (blk != blockOf[ch] || fnum != fnumOf[ch])
        {
            blockOf[ch] = blk; fnumOf[ch] = fnum;
            int part = ch / 3, c = ch % 3;
            Reg(part, 0xA4 + c, (byte)(blk << 3 | fnum >> 8));     // the high byte only latches; the low byte applies both
            Reg(part, 0xA0 + c, (byte)(fnum & 0xFF));
        }
        if (!keyed[ch]) { Key(ch, true); keyed[ch] = true; }
    }

    private void SetDac(DacVoice v, double hz, double velocity)
    {
        var d = snap.Dac[v.Sample];
        double ratio = d.Rate / (double)hostRate;
        v.Step = d.Loops && d.CycleHz > 0 ? ratio * hz / d.CycleHz : ratio * hz / OneShotRootHz;
        v.Gain = (float)(dacUnit * Math.Min(1.0, velocity));
    }

    public override void Update(int slot, double hz, double velocity)
    {
        if (slot >= PsgBase) { psg.Update(slot - PsgBase, hz, velocity); return; }
        if (slot >= DacBase) { var v = dac[slot - DacBase]; if (v.Sample >= 0) SetDac(v, hz, velocity); return; }
        Apply(slot, hz, velocity);
    }

    public override void NoteOff(int slot)
    {
        if (slot >= PsgBase) { psg.NoteOff(slot - PsgBase); psgHeld = Math.Max(0, psgHeld - 1); return; }
        if (slot >= DacBase)
        {
            var v = dac[slot - DacBase];
            // a looped sound fades out; a short one-shot plays through (a drum hit is not cut by a short note)
            if (v.Sample >= 0 && (snap.Dac[v.Sample].Loops || snap.Dac[v.Sample].Seconds > 1.0)) v.Releasing = true;
            return;
        }
        if (keyed[slot]) { Key(slot, false); keyed[slot] = false; lastOff[slot] = ymClock; }
        held = Math.Max(0, held - 1);
    }

    private void RunChip(int frames)
    {
        int guard = 0;
        while (mixL.Available < frames && guard++ < 256)
        {
            ymClock += 8L * Ym2612.ClocksPerSample;
            ym.RunTo(ymClock);
            mixL.Advance(samples); mixR.Advance(samples);
        }
    }

    public override void Render(Span<float> dest, int frames)
    {
        dest.Slice(0, frames * 2).Clear();
        if (held > 0 || quiet < hostRate / 5)
        {
            float peak = 0;
            for (int done = 0; done < frames; done += bufL.Length)
            {
                int n = Math.Min(bufL.Length, frames - done);
                RunChip(n);
                mixL.Read(bufL.AsSpan(0, n)); mixR.Read(bufR.AsSpan(0, n));
                for (int i = 0; i < n; i++)
                {
                    dest[(done + i) * 2] = bufL[i]; dest[(done + i) * 2 + 1] = bufR[i];
                    peak = Math.Max(peak, Math.Abs(bufL[i]));
                }
            }
            quiet = peak < 1e-5f && held == 0 ? quiet + frames : 0;
        }
        if (psgHeld > 0)
            for (int done = 0; done < frames; done += 256)
            {
                int n = Math.Min(256, frames - done);
                psg.Render(psgBuf, n);
                for (int i = 0; i < n * 2; i++) dest[done * 2 + i] += psgBuf[i];
            }
        if (dacActive > 0) RenderDac(dest, frames);
    }

    private void RenderDac(Span<float> dest, int frames)
    {
        foreach (var v in dac)
        {
            if (v.Sample < 0) continue;
            var d = snap.Dac[v.Sample];
            var data = d.Data;
            bool loops = d.Loops;
            for (int i = 0; i < frames; i++)
            {
                int at = (int)v.Pos;
                double frac = v.Pos - at;
                int next = at + 1;
                if (loops && next >= d.LoopEnd) next = d.LoopStart + (next - d.LoopEnd);
                if (at >= data.Length - 1 && !loops) { v.Sample = -1; dacActive--; break; }
                float a = data[Math.Min(at, data.Length - 1)], b = data[Math.Min(next, data.Length - 1)];
                float s = ((a + (b - a) * (float)frac) - 128f) * v.Gain * v.Fade;
                dest[2 * i] += s; dest[2 * i + 1] += s;
                v.Pos += v.Step;
                if (loops) { while (v.Pos >= d.LoopEnd) v.Pos -= d.LoopLength; }
                if (v.Releasing)
                {
                    v.Fade -= fadePerFrame;
                    if (v.Fade <= 0) { v.Sample = -1; v.Releasing = false; dacActive--; break; }
                }
            }
        }
    }
}
