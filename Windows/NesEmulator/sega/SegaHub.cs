using System;
using NesEmulator.Mix;

namespace NesEmulator.Sega;

// The NES register model is the hub every Sega chip is bridged to and from, once, instead of N x N pairwise bridges (plan decision 3). This file is the Sega side of
// that hub: the language a chip uses to say what it is playing (HubVoice) and the one writer that turns it into NES register writes on any NES APU core. Sega-to-NES
// sound needs nothing else: SN76489 and YM2612 bridges describe their voices and hand them to a NesVoiceWriter, exactly as GbApuOnNes and NesApuOnSnes replay their
// chips today, but with the register-writing, the pitch maths and the extension-bank layout in one tested place.

/// <summary>The three kinds of NES voice a Sega voice can become. A DAC stream is not a voice: the bridge that sees one turns it into noise hits itself.</summary>
public enum HubVoiceKind { Pulse, Triangle, Noise }

/// <summary>One voice of a sound chip as the hub understands it, sampled at a bridge sync point.</summary>
/// <param name="Kind">Which NES voice it should become.</param>
/// <param name="Hz">Pitch of a pulse or triangle; the shift rate of a noise voice.</param>
/// <param name="Level">Loudness, 0 to 1, linear.</param>
/// <param name="Duty">Pulse duty, 0 to 3 (12.5%, 25%, 50%, 75% as the NES has them); ignored by the others.</param>
/// <param name="ShortNoise">A noise voice in its short, buzzy mode (the NES 93-step register; the SN76489's periodic noise).</param>
/// <param name="Pan">Where the guest puts the voice, where it says: -1 the left speaker only, +1 the right only, 0 both (or not known). The NES hub is mono, so a NES chip plays every
/// voice on both sides; the field is the guest's own word for tools, tests and the stems, and for an enhanced chip with stereo of its own to come.</param>
public readonly record struct HubVoice(HubVoiceKind Kind, double Hz, float Level, int Duty = 2, bool ShortNoise = false, float Pan = 0f)
{
    public bool Audible => Level > 0.001f && Hz > 0;

    /// <summary>The pan of a voice from its left and right enables (both or neither: 0).</summary>
    public static float PanOf(bool left, bool right) => left == right ? 0f : left ? -1f : 1f;
}

/// <summary>A chip that can say what it is playing right now.</summary>
public interface IHubVoiceSource
{
    /// <summary>Writes the chip's voices into <paramref name="voices"/> (silent ones included, in a stable order) and returns how many it wrote.</summary>
    int DescribeVoices(Span<HubVoice> voices);
}

/// <summary>Where NES register writes go: bank 0 is a NES APU's own registers, banks 1 and up are the extra channel banks some APU cores carry (<see cref="IApuExtChannels"/>).</summary>
public interface INesRegisterPort
{
    int Banks { get; }
    void Write(int bank, ushort address, byte value);
    /// <summary>Asks for at least <paramref name="wanted"/> banks: an enhanced chip adds extra banks up to its most (<see cref="IApuExtChannels.EnsureExtBanks"/>), a stock one stays at its one. Never fewer than now; returns <see cref="Banks"/>.</summary>
    int EnsureBanks(int wanted);
}

public static class NesRegisterPorts
{
    /// <summary>A port onto a NES APU core: bank 0 through <c>WriteAPURegister</c>, the others through the core's extension bank when it has one.</summary>
    public static INesRegisterPort For(IAPU apu) => new ApuPort(apu);

    private sealed class ApuPort : INesRegisterPort
    {
        private readonly IAPU apu;
        private readonly IApuExtChannels? ext;
        public ApuPort(IAPU apu) { this.apu = apu; ext = apu as IApuExtChannels; }
        public int Banks => 1 + (ext?.ExtBanks ?? 0);
        public int EnsureBanks(int wanted)
        {
            if (ext != null && wanted - 1 > ext.ExtBanks) ext.EnsureExtBanks(wanted - 1);
            return Banks;
        }
        public void Write(int bank, ushort address, byte value)
        {
            if (bank == 0) apu.WriteAPURegister(address, value);
            else if (ext != null && bank <= ext.ExtBanks) ext.WriteExtRegister(bank, address, value);
        }
    }
}

/// <summary>
/// Replays a set of <see cref="HubVoice"/>s on a NES APU: pulses on the pulse channels, triangles on the triangles, noises on the noise channels, across as many register
/// banks as the port has (2 pulses, 1 triangle and 1 noise per bank). A voice keeps its slot; the quietest are dropped when a kind has more voices than slots; every slot nothing
/// claims is silenced. Registers are only rewritten when their value changes, so a held note does not restart its phase and a quiet chip costs nothing.
/// A bridge says how many banks its guest's voices need (<see cref="BanksFor"/>): an enhanced chip (APU_FIXS, APU_DMGS) adds that many, a stock chip has its one bank and drops the rest, counted.
/// </summary>
/// <remarks>Tonal pitch: a pulse plays <c>1789773 / (16 (t + 1))</c> Hz and a triangle <c>1789773 / (32 (t + 1))</c> Hz from an 11-bit timer value t; pitches the timer cannot reach are silenced, and the shared <see cref="PitchGuard"/> ceiling applies.
/// Noise: the nearest of the 16 NES noise rates by log distance.</remarks>
public sealed class NesVoiceWriter
{
    /// <summary>The NTSC NES CPU clock the NES timers count.</summary>
    public const double NesCpuHz = 1_789_773.0;

    /// <summary>NES noise periods in CPU cycles per shift (NTSC).</summary>
    public static readonly int[] NoisePeriods = { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };

    private readonly INesRegisterPort port;
    private readonly int[] cache;
    private readonly int[] order = new int[64];
    private readonly Action<int, HubVoice> playPulse, playTriangle, playNoise;
    private readonly Action<int> silencePulse, silenceTriangle, silenceNoise;

    public int Banks { get; }
    public int PulseSlots => Banks * 2;
    public int TriangleSlots => Banks;
    public int NoiseSlots => Banks;

    /// <summary>Voices that found no free slot of their kind (the quietest ones, dropped), summed over all syncs.</summary>
    public long Dropped { get; private set; }

    /// <summary>Register writes actually sent (changes only).</summary>
    public long Writes { get; private set; }

    public NesVoiceWriter(INesRegisterPort port) : this(port, 1) { }

    /// <param name="wantBanks">The banks the guest's voices need (<see cref="BanksFor"/>): the port is asked for them (an enhanced chip grows to them; never below the banks it already has).</param>
    public NesVoiceWriter(INesRegisterPort port, int wantBanks)
    {
        this.port = port;
        playPulse = PlayPulse; playTriangle = PlayTriangle; playNoise = PlayNoise;
        silencePulse = SilencePulse; silenceTriangle = SilenceTriangle; silenceNoise = SilenceNoise;
        int had = Math.Max(1, port.Banks);
        Banks = Math.Max(1, port.EnsureBanks(Math.Max(1, wantBanks)));
        cache = new int[Banks * 0x18];
        Array.Fill(cache, -1);
        opened = new bool[Banks];
        // the banks the chip had are opened now, as always; the ones it grew by open when a voice first plays on them (a bank costs the chip nothing until it is written)
        for (int b = 0; b < Math.Min(had, Banks); b++) Open(b);
    }

    private readonly bool[] opened;

    /// <summary>The banks a voice has played on so far (or that the chip had to start with): the ones the chip runs.</summary>
    public int BanksOpen { get { int n = 0; foreach (bool o in opened) if (o) n++; return n; } }

    private void Open(int b)
    {
        opened[b] = true;
        if (b == 0) W(0, 0x4017, 0x40);   // frame IRQ inhibited: the bridge's APU has no CPU to interrupt
        W(b, 0x4015, 0x0F);               // pulses, triangle and noise on; no DMC
        W(b, 0x4001, 0x00); W(b, 0x4005, 0x00);   // sweeps off
    }

    /// <summary>The banks that carry <paramref name="pulses"/> pulse voices, <paramref name="triangles"/> triangle voices and <paramref name="noises"/> noise voices, each on a channel of its own kind (2 pulses, 1 triangle, 1 noise a bank).</summary>
    public static int BanksFor(int pulses, int triangles, int noises) => Math.Max(1, Math.Max((pulses + 1) / 2, Math.Max(triangles, noises)));

    /// <summary>The banks that carry <paramref name="tonal"/> tonal voices on pulses (the channel with a volume: <see cref="HubReplayer"/> sends a voice to a triangle only when it is low or the pulses are full) and <paramref name="noises"/> noise voices.</summary>
    public static int BanksForTonal(int tonal, int noises) => BanksFor(tonal, 0, noises);

    private void W(int bank, ushort address, byte value)
    {
        int i = bank * 0x18 + (address - 0x4000);
        if (cache[i] == value) return;
        cache[i] = value;
        port.Write(bank, address, value);
        Writes++;
    }

    /// <summary>
    /// Replays <paramref name="voices"/>. Call it at the bridge's sync cadence (the GB and SNES bridges use about 960 a second). A voice keeps its slot from one sync to the next:
    /// the n-th voice of a kind (in the order the chip lists them, silent ones included) goes on the n-th slot of that kind, so a quiet voice never makes another one hop channels and
    /// restart its phase. Only a chip with more voices of a kind than the port has slots loses any: the quietest are dropped.
    /// </summary>
    public void Apply(ReadOnlySpan<HubVoice> voices)
    {
        Assign(voices, HubVoiceKind.Pulse, PulseSlots, playPulse, silencePulse);
        Assign(voices, HubVoiceKind.Triangle, TriangleSlots, playTriangle, silenceTriangle);
        Assign(voices, HubVoiceKind.Noise, NoiseSlots, playNoise, silenceNoise);
    }

    private void Assign(ReadOnlySpan<HubVoice> voices, HubVoiceKind kind, int slots, Action<int, HubVoice> play, Action<int> silence)
    {
        int n = 0;
        for (int i = 0; i < voices.Length && n < order.Length; i++) if (voices[i].Kind == kind) order[n++] = i;
        while (n > slots)
        {
            // drop the quietest (the later one on a tie), counting it only if it was audible
            int worst = 0;
            for (int k = 1; k < n; k++) if (voices[order[k]].Level <= voices[order[worst]].Level) worst = k;
            if (voices[order[worst]].Audible) Dropped++;
            for (int k = worst; k < n - 1; k++) order[k] = order[k + 1];
            n--;
        }
        for (int s = 0; s < slots; s++)
        {
            if (s < n && voices[order[s]].Audible) play(s, voices[order[s]]); else silence(s);
        }
    }
    private static byte Volume(float level) => (byte)Math.Clamp((int)Math.Round(level * 15f), 1, 15);

    /// <summary>The 11-bit timer value for a pitch, or -1 when the timer cannot reach it.</summary>
    public static int TimerFor(double hz, int divider, int minimum)
    {
        if (!(hz > 0)) return -1;
        int t = (int)Math.Round(NesCpuHz / (divider * hz)) - 1;
        return t < minimum || t > 2047 ? -1 : t;
    }

    private void PlayPulse(int slot, HubVoice v)
    {
        int t = PitchGuard.Blocks(v.Hz) ? -1 : TimerFor(v.Hz, 16, 8);
        if (t < 0) { SilencePulse(slot); return; }
        int bank = slot / 2; ushort b = (ushort)(0x4000 + (slot & 1) * 4);
        if (!opened[bank]) Open(bank);
        W(bank, b, (byte)((v.Duty & 3) << 6 | 0x30 | Volume(v.Level)));
        W(bank, (ushort)(b + 2), (byte)t);
        W(bank, (ushort)(b + 3), (byte)(0x08 | (t >> 8)));
    }

    private void SilencePulse(int slot) { if (opened[slot / 2]) W(slot / 2, (ushort)(0x4000 + (slot & 1) * 4), 0x30); }      // a bank never opened is silent already

    private void PlayTriangle(int bank, HubVoice v)
    {
        int t = PitchGuard.Blocks(v.Hz) ? -1 : TimerFor(v.Hz, 32, 2);
        if (t < 0) { SilenceTriangle(bank); return; }
        if (!opened[bank]) Open(bank);
        W(bank, 0x4008, 0xFF); W(bank, 0x400A, (byte)t); W(bank, 0x400B, (byte)(0x08 | (t >> 8)));
    }

    private void SilenceTriangle(int bank) { if (opened[bank]) W(bank, 0x4008, 0x80); }

    private void PlayNoise(int bank, HubVoice v)
    {
        int best = 0; double bd = double.MaxValue;
        for (int k = 0; k < NoisePeriods.Length; k++)
        {
            double d = Math.Abs(Math.Log(NesCpuHz / NoisePeriods[k] / v.Hz));
            if (d < bd) { bd = d; best = k; }
        }
        if (!opened[bank]) Open(bank);
        W(bank, 0x400C, (byte)(0x30 | Volume(v.Level)));
        W(bank, 0x400E, (byte)((v.ShortNoise ? 0x80 : 0) | best));
        W(bank, 0x400F, 0x08);
    }

    private void SilenceNoise(int bank) { if (opened[bank]) W(bank, 0x400C, 0x30); }
}
