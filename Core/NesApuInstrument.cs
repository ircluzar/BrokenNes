using NesEmulator.Mix;

namespace NesEmulator.Plugin;

/// <summary>
/// A bare NES sound chip as an instrument, no ROM or CPU program involved: the host writes the
/// $4000-$4017 registers and pulls audio at its own sample rate. Any NES APU core can sit behind
/// it (FIX, QN, the Game Boy bridge DMG, ...) and be swapped while sounding.
/// <para>Registers can change as often as the host likes between <see cref="Render"/> calls
/// (the plugin does it every 64 samples, ~1.3 ms), which is what makes pitch bends smooth: a
/// ROM-driven NES can only change the pitch once per 16.6 ms frame.</para>
/// Not thread-safe: use from one thread (the audio thread), except <see cref="ApuCoreIds"/>.
/// </summary>
public sealed class NesApuInstrument
{
    /// <summary>CPU cycles the APU is stepped by at a time. The speed-hack cores (SPD, EIL, ...) sample
    /// at the END of a step, so a large step would collapse the waveform; the CPU itself steps about 24.</summary>
    private const int StepCycles = 32;

    private readonly StreamResampler resampler = new();
    private readonly float[] pull = new float[512];
    private readonly byte[] shadow = new byte[0x18];
    private IAPU apu;
    private NesApuHost? host;
    private int hostRate;

    /// <param name="enableMask">$4015 value: which channels the chip has enabled (bit 0 pulse 1, 1 pulse 2, 2 triangle, 3 noise).
    /// A channel stem enables only its own.</param>
    public NesApuInstrument(string apuId, int hostSampleRate, byte enableMask = 0x0F)
    {
        EnableMask = enableMask;
        hostRate = hostSampleRate;
        apu = Create(apuId);
        CoreId = apuId;
        Attach();
    }

    public static IReadOnlyList<string> ApuCoreIds => CoreRegistry.ApuIds;
    public byte EnableMask { get; }
    public string CoreId { get; private set; }
    public int NativeSampleRate => apu.GetSampleRate();

    public int HostSampleRate
    {
        get => hostRate;
        set { hostRate = value; resampler.SetRates(NativeSampleRate, hostRate); }
    }

    private IAPU Create(string id)
    {
        host = new NesApuHost(id);       // throws ArgumentException for an unknown core
        return host.Apu;
    }

    private void Attach()
    {
        resampler.Clear();
        resampler.SetRates(NativeSampleRate, hostRate);
        apu.WriteAPURegister(0x4017, 0x40);   // frame IRQ inhibited: nothing to raise
        apu.WriteAPURegister(0x4015, EnableMask);   // pulses, triangle, noise on (or just this chip's channel); no DMC
        for (int r = 0; r < 0x14; r++)
            if (shadow[r] != 0) apu.WriteAPURegister((ushort)(0x4000 + r), shadow[r]);
    }

    /// <summary>Switches the sound chip. Register state is carried over, so held notes keep sounding.</summary>
    public void SetCore(string apuId)
    {
        if (string.Equals(apuId, CoreId, StringComparison.OrdinalIgnoreCase)) return;
        apu = Create(apuId);
        CoreId = apuId;
        Attach();
    }

    /// <summary>Write APU register <paramref name="reg"/> (0 = $4000 ... 0x17 = $4017).</summary>
    public void Write(int reg, byte value)
    {
        shadow[reg] = value;
        apu.WriteAPURegister((ushort)(0x4000 + reg), value);
    }

    /// <summary>Last value written to a register (0 until written).</summary>
    public byte Shadow(int reg) => shadow[reg];

    /// <summary>Time-driven use (a ROM's channel stem): runs the chip for <paramref name="cpuCycles"/> CPU cycles, whatever the host
    /// wants; the sound waits in the resampler for <see cref="ReadAvailable"/>.</summary>
    public void Advance(long cpuCycles)
    {
        while (cpuCycles > 0)
        {
            int c = (int)Math.Min(StepCycles, cpuCycles);
            apu.Step(c);
            cpuCycles -= c;
            int n;
            while ((n = apu.ReadSamples(pull.AsSpan(0, Math.Min(pull.Length, resampler.FreeSpace)))) > 0)
                resampler.Write(pull.AsSpan(0, n));
        }
    }

    /// <summary>Output samples ready without stepping the chip further.</summary>
    public int Available => resampler.Available;

    /// <summary>Reads what <see cref="Advance"/> produced (up to dest.Length samples at the host rate); returns how many.</summary>
    public int ReadAvailable(Span<float> dest) => resampler.Read(dest);

    /// <summary>Fills <paramref name="dest"/> with mono samples at the host rate.</summary>
    public void Render(Span<float> dest)
    {
        int done = 0;
        while (done < dest.Length)
        {
            done += resampler.Read(dest.Slice(done));
            if (done >= dest.Length) break;
            apu.Step(StepCycles);
            int n;
            while ((n = apu.ReadSamples(pull.AsSpan(0, Math.Min(pull.Length, resampler.FreeSpace)))) > 0)
                resampler.Write(pull.AsSpan(0, n));
        }
    }
}
