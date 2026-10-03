using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>Which SN76489: the Sega part (Master System, Game Gear, Genesis; 16-bit noise register) or the discrete Texas Instruments one (SG-1000, SC-3000; 15-bit).</summary>
public enum PsgVariant { Sega, Ti }

/// <summary>
/// The SN76489 programmable sound generator: three square-wave tone channels and a noise channel, each with a 4-bit attenuator. It sits behind port $7F (Master System,
/// Game Gear) or inside the VDP (Genesis) and is clocked at the CPU clock divided by 16.
/// </summary>
/// <remarks>
/// Written from the SMS Power SN76489 documentation (Maxim) and the Sega Game Gear Hardware Reference Manual (the stereo port); see sega/THIRD_PARTY_NOTICES.md for what else was read.
/// <list type="bullet">
/// <item>Writes: a latch byte <c>1cctdddd</c> selects a register (cc = channel 0-3, t = 1 volume / 0 tone or noise) and gives its low four bits; a data byte <c>0-dddddd</c> gives the high
/// six bits of a latched tone register, or replaces a latched volume or noise register. The latch is never cleared by a data byte.</item>
/// <item>Tone: a 10-bit period N; the output flips every N PSG clocks, so the frequency is clock / (32 N). On the Sega part, period 0 and 1 hold the output high and do not oscillate
/// (a constant level that software exploits to play samples through the volume register); the TI part treats 0 as 1024.</item>
/// <item>Noise: bits 1-0 pick the shift rate (clock/512, /1024, /2048 of the CPU clock, or the rate of tone channel 3); bit 2 picks white (feedback from bits 0 and 3 of the Sega
/// register, bits 0 and 1 of the TI's 15-bit one) or periodic (bit 0 only). Any write to the noise register reloads the shift register (Sega $8000, TI $4000).</item>
/// <item>Volume 15 is off; each step is 2 dB.</item>
/// <item>Game Gear stereo (port $06): bits 7-4 enable noise, tone 3, tone 2, tone 1 on the left; bits 3-0 on the right. Reset $FF (everything on both).</item>
/// </list>
/// Output is unipolar like the chip's; <see cref="BandLimitedMixer"/> makes it band-limited audio and removes the DC offset.
/// </remarks>
public sealed class Sn76489 : IHubVoiceSource
{
    /// <summary>One PSG clock is this many CPU clocks.</summary>
    public const int Divider = 16;

    /// <summary>The 2 dB attenuation table, as 16-bit amplitudes (volume 15 is silent).</summary>
    private static readonly int[] Table = { 32767, 26028, 20675, 16422, 13045, 10362, 8231, 6568, 5193, 4125, 3277, 2603, 2067, 1642, 1304, 0 };
    private static readonly float[] Level = BuildLevels();
    private static float[] BuildLevels() { var l = new float[16]; for (int i = 0; i < 16; i++) l[i] = Table[i] / 32767f; return l; }

    /// <summary>Output scale: one full-volume channel is about this fraction of full scale (four at once stay clear of clipping after the DC removal).</summary>
    public float Gain = 0.2f;

    private readonly PsgVariant variant;
    private readonly double cpuClockHz;
    private readonly BandLimitedMixer left, right;

    // registers
    private readonly int[] period = new int[3];
    private readonly int[] volume = { 15, 15, 15, 15 };
    private int noise;            // bit 2 white / periodic, bits 1-0 rate
    private int latched;          // bits 6-4 of the last latch byte
    private int stereo = 0xFF;

    // running state
    private readonly int[] counter = new int[4];
    private readonly bool[] tone = new bool[3];
    private bool noiseDivider;     // the flip-flop between the noise counter and the shift register
    private int shift;             // the noise shift register
    private int cpuRemainder;
    private long clock;            // PSG clocks run so far
    private float lastLeft, lastRight;

    public PsgVariant Variant => variant;

    /// <param name="cpuClockHz">The clock the chip runs from (the Z80 clock); the PSG ticks at a sixteenth of it.</param>
    /// <param name="sampleRate">The audio rate of <see cref="ReadSamples"/>.</param>
    public Sn76489(double cpuClockHz, int sampleRate, PsgVariant variant = PsgVariant.Sega)
    {
        this.variant = variant;
        this.cpuClockHz = cpuClockHz;
        left = new BandLimitedMixer(cpuClockHz / Divider, sampleRate);
        right = new BandLimitedMixer(cpuClockHz / Divider, sampleRate);
        Reset();
    }

    public int SampleRate => left.SampleRate;

    // ---- the registers, for the bridges and the plugin ----
    public int Period(int channel) => period[channel];
    public int Volume(int channel) => volume[channel];
    public int NoiseControl => noise;
    public int StereoMask => stereo;
    public int NoiseRegisterValue => shift;
    /// <summary>The level a channel is at right now (0 when low or silent), 0..1.</summary>
    public float ChannelLevel(int channel) => channel < 3 ? (tone[channel] ? Level[volume[channel]] : 0f) : ((shift & 1) != 0 ? Level[volume[3]] : 0f);

    public void Reset()
    {
        Array.Clear(period); for (int i = 0; i < 4; i++) volume[i] = 15;
        noise = 0; latched = 0; stereo = 0xFF; noiseDivider = false;
        ReloadNoise();
        for (int i = 0; i < 3; i++) { counter[i] = 1; tone[i] = true; }
        counter[3] = NoiseRate();
        cpuRemainder = 0;
        lastLeft = lastRight = 0;
        left.Reset(); right.Reset();
        SetOutput();
    }

    /// <summary>A byte written to the PSG port.</summary>
    public void Write(byte value)
    {
        if ((value & 0x80) != 0)
        {
            latched = (value >> 4) & 7;
            int channel = latched >> 1;
            if ((latched & 1) != 0) volume[channel] = value & 0x0F;
            else if (channel < 3) period[channel] = (period[channel] & 0x3F0) | (value & 0x0F);
            else { noise = value & 7; ReloadNoise(); }
        }
        else
        {
            int channel = latched >> 1;
            if ((latched & 1) != 0) volume[channel] = value & 0x0F;
            else if (channel < 3) period[channel] = (period[channel] & 0x00F) | ((value & 0x3F) << 4);
            else { noise = value & 7; ReloadNoise(); }
        }
        SetOutput();
    }

    /// <summary>The Game Gear stereo port ($06).</summary>
    public void WriteStereo(byte value) { stereo = value; SetOutput(); }

    private void ReloadNoise() => shift = variant == PsgVariant.Sega ? 0x8000 : 0x4000;

    private int NoiseRate() => (noise & 3) switch
    {
        0 => 0x10,
        1 => 0x20,
        2 => 0x40,
        _ => ToneReload(2),
    };

    private int ToneReload(int channel)
    {
        int p = period[channel];
        return p == 0 ? (variant == PsgVariant.Ti ? 0x400 : 1) : p;
    }

    /// <summary>Runs the chip for <paramref name="cpuClocks"/> CPU clocks (a sixteenth as many PSG clocks, the remainder carried).</summary>
    public void Run(int cpuClocks)
    {
        cpuRemainder += cpuClocks;
        while (cpuRemainder >= Divider) { cpuRemainder -= Divider; Tick(); }
        left.Advance(clock); right.Advance(clock);
    }

    /// <summary>One PSG clock.</summary>
    private void Tick()
    {
        clock++;
        for (int i = 0; i < 3; i++)
        {
            if (--counter[i] > 0) continue;
            counter[i] = ToneReload(i);
            // a period of 0 or 1 holds the Sega part's output high instead of oscillating
            tone[i] = (variant == PsgVariant.Sega && period[i] <= 1) || !tone[i];
        }
        if (--counter[3] <= 0)
        {
            counter[3] = NoiseRate();
            noiseDivider = !noiseDivider;
            if (noiseDivider) ShiftNoise();   // the register moves on every second expiry: the divide-by-two between counter and register
        }
        SetOutput();
    }

    private void ShiftNoise()
    {
        bool white = (noise & 4) != 0;
        if (variant == PsgVariant.Sega)
        {
            int feedback = white ? ((shift ^ (shift >> 3)) & 1) : (shift & 1);   // taps 0 and 3
            shift = (shift >> 1) | (feedback << 15);
        }
        else
        {
            int feedback = white ? ((shift ^ (shift >> 1)) & 1) : (shift & 1);   // taps 0 and 1
            shift = (shift >> 1) | (feedback << 14);
        }
    }

    private void SetOutput()
    {
        float l = 0, r = 0;
        for (int i = 0; i < 4; i++)
        {
            bool high = i < 3 ? tone[i] : (shift & 1) != 0;
            if (!high) continue;
            float a = Level[volume[i]];
            // stereo bits: noise = 7/3, tone3 = 6/2, tone2 = 5/1, tone1 = 4/0
            int bit = i == 3 ? 3 : i;
            if ((stereo & (0x10 << bit)) != 0) l += a;
            if ((stereo & (0x01 << bit)) != 0) r += a;
        }
        if (l != lastLeft) { left.Step(clock, l - lastLeft); lastLeft = l; }
        if (r != lastRight) { right.Step(clock, r - lastRight); lastRight = r; }
    }

    /// <summary>Stereo interleaved 16-bit samples finished since the last call; the number of shorts written (always even).</summary>
    public int ReadSamples(Span<short> buffer)
    {
        int frames = Math.Min(Math.Min(left.Available, right.Available), buffer.Length / 2);
        Span<float> l = frames <= 512 ? stackalloc float[frames] : new float[frames];
        Span<float> r = frames <= 512 ? stackalloc float[frames] : new float[frames];
        left.Read(l); right.Read(r);
        float scale = Gain * 32767f;
        for (int i = 0; i < frames; i++)
        {
            buffer[2 * i] = (short)Math.Clamp(l[i] * scale, -32768f, 32767f);
            buffer[2 * i + 1] = (short)Math.Clamp(r[i] * scale, -32768f, 32767f);
        }
        return frames * 2;
    }

    // ---- the NES hub: what the chip is playing, as voices ----

    /// <summary>The four voices in a stable order (tone 1, tone 2, tone 3, noise). Tones 1 and 2 are pulses at 50% duty, tone 3 is offered as a triangle (the plan's mapping), the
    /// noise is noise. A tone whose period is 0 or 1 is a constant level on the Sega part, not a note, and is reported silent; a channel the Game Gear stereo port mutes on both
    /// sides is silent too.</summary>
    public int DescribeVoices(Span<HubVoice> voices)
    {
        for (int i = 0; i < 4; i++)
        {
            int bit = i;   // stereo bits: tone 1 = 0, tone 2 = 1, tone 3 = 2, noise = 3
            bool heard = (stereo & ((0x10 | 0x01) << bit)) != 0;
            float level = heard ? Level[volume[i]] : 0f;
            if (i < 3)
            {
                int n = period[i] == 0 && variant == PsgVariant.Ti ? 0x400 : period[i];
                if (n <= 1) level = 0f;
                double hz = n > 0 ? cpuClockHz / (32.0 * n) : 0;
                voices[i] = new HubVoice(i < 2 ? HubVoiceKind.Pulse : HubVoiceKind.Triangle, hz, level, Duty: 2);
            }
            else
            {
                double hz = cpuClockHz / 16.0 / (2.0 * NoiseRate());
                voices[i] = new HubVoice(HubVoiceKind.Noise, hz, level, ShortNoise: (noise & 4) == 0);
            }
        }
        return 4;
    }

    // ---- state (registers and counters; the audio buffers are not part of it) ----

    public void SaveState(BinaryWriter w)
    {
        foreach (int p in period) w.Write(p);
        foreach (int v in volume) w.Write(v);
        w.Write(noise); w.Write(latched); w.Write(stereo);
        foreach (int c in counter) w.Write(c);
        foreach (bool t in tone) w.Write(t);
        w.Write(noiseDivider); w.Write(shift); w.Write(cpuRemainder);
    }

    public void LoadState(BinaryReader r)
    {
        for (int i = 0; i < 3; i++) period[i] = r.ReadInt32();
        for (int i = 0; i < 4; i++) volume[i] = r.ReadInt32();
        noise = r.ReadInt32(); latched = r.ReadInt32(); stereo = r.ReadInt32();
        for (int i = 0; i < 4; i++) counter[i] = r.ReadInt32();
        for (int i = 0; i < 3; i++) tone[i] = r.ReadBoolean();
        noiseDivider = r.ReadBoolean(); shift = r.ReadInt32(); cpuRemainder = r.ReadInt32();
        lastLeft = lastRight = 0;
        SetOutput();
    }
}
