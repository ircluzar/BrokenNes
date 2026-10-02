using NesEmulator.Gb;
using NesEmulator.Plugin;

namespace BrokenNes2;

/// <summary>
/// A copy of a Game Boy game's sound settings taken when Instrument Runaway starts: the sound registers NR10-NR52 as the game left them (what can be
/// read back: duty, envelopes, the wave channel's volume code, the noise settings) and the 16 bytes of wave RAM, which is the wave channel's instrument.
/// Immutable once made.
/// </summary>
public sealed class GbSnapshot
{
    public const int RegCount = 32, WaveSize = 16;
    /// <summary>NR10..NR51 at index reg - $10.</summary>
    public readonly byte[] Regs = new byte[RegCount];
    public readonly byte[] Wave = new byte[WaveSize];
    public readonly bool Cgb;

    public GbSnapshot(byte[] regs, byte[] wave, bool cgb)
    {
        Array.Copy(regs, Regs, Math.Min(regs.Length, RegCount));
        Array.Copy(wave, Wave, Math.Min(wave.Length, WaveSize));
        Cgb = cgb;
    }

    public static GbSnapshot FromApu(APU_GB apu)
    {
        var regs = new byte[RegCount];
        for (int r = 0x10; r < 0x10 + RegCount; r++) regs[r - 0x10] = apu.ReadRegister(r);
        return new GbSnapshot(regs, (byte[])apu.WaveRam.Clone(), apu.Model == GbModel.Cgb);
    }

    /// <summary>The duty (0..3) the game had on pulse channel <paramref name="channel"/> (0 or 1).</summary>
    public int Duty(int channel) => Regs[(channel == 0 ? 0x11 : 0x16) - 0x10] >> 6;

    /// <summary>The envelope bits (direction and period) the game had on a pulse (0, 1) or the noise (3) channel.</summary>
    public int Envelope(int channel) => Regs[(channel switch { 0 => 0x12, 1 => 0x17, _ => 0x21 }) - 0x10] & 0x0F;

    public byte[] ToBytes()
    {
        var b = new byte[RegCount + WaveSize + 1];
        Regs.CopyTo(b, 0);
        Wave.CopyTo(b, RegCount);
        b[^1] = (byte)(Cgb ? 1 : 0);
        return b;
    }

    public static GbSnapshot? FromBytes(byte[] b) =>
        b.Length == RegCount + WaveSize + 1 ? new GbSnapshot(b.AsSpan(0, RegCount).ToArray(), b.AsSpan(RegCount, WaveSize).ToArray(), b[^1] != 0) : null;
}

/// <summary>
/// A private Game Boy sound unit (the real <see cref="APU_GB"/> core) set up from a <see cref="GbSnapshot"/>: it plays one channel, driven by FL notes,
/// with the game's own settings for it: the pulses' duty and envelope, the wave channel's waveform, the noise channel's envelope. The unit is clocked by
/// hand (4.19 MHz T-cycles and the 512 Hz frame sequencer) and its 48 kHz stereo is resampled to the host rate. One per instance.
/// </summary>
internal sealed class GbSampler
{
    private readonly APU_GB apu;
    private readonly GbSnapshot snap;
    private readonly StreamResampler left = new(), right = new();
    private readonly short[] pcm = new short[4096];
    private readonly float[] one = new float[1], tmpL, tmpR;
    private int frameSequencer;

    public GbSampler(GbSnapshot snapshot, int hostRate, int maxBlock = 4096)
    {
        snap = snapshot;
        apu = new APU_GB(snapshot.Cgb ? GbModel.Cgb : GbModel.Dmg);
        apu.WriteRegister(0x26, 0x80);                         // power on
        apu.WriteRegister(0x24, 0x77);                         // master volume
        apu.WriteRegister(0x25, 0xFF);                         // every channel to both speakers
        Array.Copy(snapshot.Wave, apu.WaveRam, GbSnapshot.WaveSize);
        tmpL = new float[maxBlock]; tmpR = new float[maxBlock];
        left.SetRates(apu.SampleRate, hostRate); right.SetRates(apu.SampleRate, hostRate);
    }

    private void W(int reg, int value) => apu.WriteRegister(reg, (byte)value);

    private static int PulseX(double hz) => Math.Clamp(2048 - (int)Math.Round(131072.0 / Math.Max(1.0, hz)), 0, 2047);
    private static int WaveX(double hz) => Math.Clamp(2048 - (int)Math.Round(65536.0 / Math.Max(1.0, hz)), 0, 2047);

    /// <summary>The noise channel's settings nearest to a pitch: the register value (shift, width, divisor).</summary>
    private static int NoiseRegister(double hz, bool shortMode)
    {
        // the noise clock is 262144 / (divisor x 2^shift) Hz (divisor 0 counts as 0.5); the pitch is spread over it like the NES does: higher notes, faster clock
        double target = hz * 32;
        int best = 0; double bestErr = double.MaxValue;
        for (int shift = 0; shift <= 13; shift++)
            for (int div = 0; div <= 7; div++)
            {
                double clock = 262144.0 / ((div == 0 ? 0.5 : div) * (1 << shift));
                double err = Math.Abs(Math.Log(clock / target));
                if (err < bestErr) { bestErr = err; best = (shift << 4) | (shortMode ? 8 : 0) | div; }
            }
        return best;
    }

    /// <summary>Starts a note on a channel (0 pulse 1, 1 pulse 2, 2 wave, 3 noise). <paramref name="duty"/> is 0..3 or -1 for the game's own.</summary>
    public void NoteOn(int channel, double hz, int level, int duty, bool noiseShort)
    {
        level = Math.Clamp(level, 0, 15);
        switch (channel)
        {
            case 0:
            case 1:
            {
                int b = channel == 0 ? 0x10 : 0x15;                       // NR10 / NR20 (unused)
                int d = duty >= 0 ? duty : snap.Duty(channel);
                if (channel == 0) W(0x10, 0x00);                           // no sweep
                W(b + 1, d << 6);                                          // duty, no length limit
                W(b + 2, (level << 4) | snap.Envelope(channel));           // the game's envelope shape, starting at the note's level
                int x = PulseX(hz);
                W(b + 3, x & 0xFF);
                W(b + 4, 0x80 | (x >> 8));                                 // trigger
                break;
            }
            case 2:
            {
                W(0x1A, 0x80);                                             // DAC on
                int code = level >= 11 ? 1 : level >= 6 ? 2 : level >= 2 ? 3 : 0;   // 100%, 50%, 25%, mute
                W(0x1C, code << 5);
                int x = WaveX(hz);
                W(0x1D, x & 0xFF);
                W(0x1E, 0x80 | (x >> 8));
                break;
            }
            default:
                W(0x20, 0x00);
                W(0x21, (level << 4) | snap.Envelope(3));
                W(0x22, NoiseRegister(hz, noiseShort));
                W(0x23, 0x80);
                break;
        }
    }

    /// <summary>Follows a slide or bend: the pitch changes without restarting the note.</summary>
    public void Pitch(int channel, double hz, bool noiseShort)
    {
        switch (channel)
        {
            case 0: case 1:
            {
                int b = channel == 0 ? 0x10 : 0x15, x = PulseX(hz);
                W(b + 3, x & 0xFF); W(b + 4, x >> 8);
                break;
            }
            case 2:
            {
                int x = WaveX(hz);
                W(0x1D, x & 0xFF); W(0x1E, x >> 8);
                break;
            }
            default: W(0x22, NoiseRegister(hz, noiseShort)); break;
        }
    }

    public void NoteOff(int channel)
    {
        switch (channel)
        {
            case 0: W(0x12, 0); break;
            case 1: W(0x17, 0); break;
            case 2: W(0x1A, 0); break;
            default: W(0x21, 0); break;
        }
    }

    /// <summary>Fills <paramref name="dest"/> with <paramref name="frames"/> interleaved stereo frames at the host rate.</summary>
    public void Render(Span<float> dest, int frames)
    {
        while (left.Available < frames)
        {
            apu.Tick(256);
            frameSequencer += 256;
            while (frameSequencer >= 8192) { apu.FrameSequencerStep(); frameSequencer -= 8192; }
            int n;
            while ((n = apu.ReadSamples(pcm)) > 0)
                for (int i = 0; i + 1 < n; i += 2)
                {
                    if (left.FreeSpace < 1) break;
                    one[0] = pcm[i] * (1f / 32768f); left.Write(one);
                    one[0] = pcm[i + 1] * (1f / 32768f); right.Write(one);
                }
        }
        left.Read(tmpL.AsSpan(0, frames));
        right.Read(tmpR.AsSpan(0, frames));
        for (int i = 0; i < frames; i++) { dest[i * 2] = tmpL[i]; dest[i * 2 + 1] = tmpR[i]; }
    }
}
