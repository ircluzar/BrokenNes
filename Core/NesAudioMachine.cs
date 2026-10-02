namespace NesEmulator.Plugin;

/// <summary>
/// A headless NES for audio hosts (plugin mode): one ROM - typically a sound-driver ROM such as
/// VRUN's live AudioPlayer - run one frame at a time on the audio thread, with the sound pulled
/// at the host's sample rate.
/// <list type="bullet">
/// <item>Accuracy first: CPU/PPU/APU default to the FIX cores, the SpeedConfig shortcuts are off
/// (as Workshop's <c>--strict</c>), and frames follow real NTSC timing (60.0988 Hz).</item>
/// <item>Memory by name: <see cref="Symbols"/> from the ROM's .mlb; <see cref="PokeRam(string, byte)"/>
/// between frames is how a host talks to the driver.</item>
/// <item>Any NES APU core, swapped while running (<see cref="SetApuCore"/>): the bus replays the
/// latched $4000-$4017 writes into the new core, so held notes keep sounding.</item>
/// </list>
/// Not thread-safe: call everything from one thread (the host's audio thread), between frames.
/// </summary>
public sealed class NesAudioMachine
{
    private readonly NES nes = new();
    private readonly StreamResampler resampler = new();
    private readonly float[] pull = new float[4096];
    private int hostRate = 44100;

    public NesAudioMachine(byte[] rom, string? mlbText = null, string cpu = "FIX", string ppu = "FIX", string apu = "FIX")
    {
        nes.RomName = "driver";
        nes.LoadROM(rom);
        if (nes.IsCrashed()) throw new InvalidOperationException("ROM did not load: " + nes.GetCrashInfo());
        if (!nes.SetCpuCore(cpu)) throw new ArgumentException($"unknown CPU core '{cpu}'");
        if (!nes.SetPpuCore(ppu)) throw new ArgumentException($"unknown PPU core '{ppu}'");
        SetApuCore(apu);
        var cfg = nes.GetSpeedConfig();
        if (cfg != null)
        {
            cfg.CpuFastOamDmaStall = false;
            cfg.CpuIdleLoopDetect = false;
            cfg.CpuIdleLoopSkip = false;
            cfg.CpuIdleLoopSkipApuStatus = false;
            cfg.CpuAdaptiveBatching = false;
            cfg.PpuSkipBlankScanlines = false;
            cfg.PpuUnsafeScanline = false;
            cfg.PpuDeferAttributeFetch = false;
            cfg.NtscAccurateFrameRate = true;
        }
        Symbols = mlbText != null ? MlbSymbols.Parse(mlbText) : MlbSymbols.Parse("");
    }

    /// <summary>Every NES APU core id the registry knows (FIX, QN, DMG = Game Boy chip, ...).</summary>
    public static IReadOnlyList<string> ApuCoreIds => CoreRegistry.ApuIds;
    public static IReadOnlyList<string> CpuCoreIds => CoreRegistry.CpuIds;
    public static IReadOnlyList<string> PpuCoreIds => CoreRegistry.PpuIds;

    /// <summary>Called on the thread that runs the frame, right after each frame: a host that shows the picture copies
    /// <see cref="FrameBuffer"/> here (it is the emulator's own array, valid until the next frame).</summary>
    public Action? AfterFrame { get; set; }

    /// <summary>The last frame as RGBA, 256 x 240 x 4 bytes.</summary>
    public byte[] FrameBuffer => nes.GetFrameBuffer();
    public long FramesRun { get; private set; }
    public string CpuCoreId => nes.GetCpuCoreId();
    public string PpuCoreId => nes.GetPpuCoreId();
    public string? CrashInfo => nes.IsCrashed() ? nes.GetCrashInfo() : null;

    public const double FramesPerSecond = 1789773.0 / (262 * 341 / 3.0);

    public NES Nes => nes;
    public MlbSymbols Symbols { get; }
    public string ApuCoreId => nes.GetApuCoreId();
    public bool Crashed => nes.IsCrashed();
    public int NativeSampleRate => nes.ActiveApu?.GetSampleRate() ?? 44100;

    /// <summary>The rate <see cref="ReadSamples"/> produces (the host's).</summary>
    public int HostSampleRate
    {
        get => hostRate;
        set { hostRate = value; resampler.SetRates(NativeSampleRate, hostRate); }
    }

    public void SetApuCore(string id)
    {
        if (!nes.SetApuCore(id)) throw new ArgumentException($"unknown APU core '{id}'");
        resampler.Clear();
        resampler.SetRates(NativeSampleRate, hostRate);
    }

    /// <summary>Switches the CPU core while the game runs (between frames).</summary>
    public void SetCpuCore(string id)
    {
        if (!nes.SetCpuCore(id)) throw new ArgumentException($"unknown CPU core '{id}'");
    }

    /// <summary>Switches the picture core while the game runs (between frames).</summary>
    public void SetPpuCore(string id)
    {
        if (!nes.SetPpuCore(id)) throw new ArgumentException($"unknown PPU core '{id}'");
    }

    /// <summary>One CPU write to an APU register during a frame: <c>Clock</c> is the APU clock at that moment.</summary>
    public struct ApuWrite { public long Clock; public byte Reg, Value; }

    private ApuWrite[] apuLog = new ApuWrite[1024];
    private int apuLogCount;
    private bool captureApu;

    /// <summary>When on, <see cref="ApuLog"/> holds every write the game made to $4000-$4017 during the last frame (a host
    /// replays them on one-channel chips to get each channel's own sound). Off by default; costs nothing then.</summary>
    public bool CaptureApuWrites
    {
        get => captureApu;
        set
        {
            captureApu = value;
            nes.ApuWriteTap = value ? (addr, val, clock) =>
            {
                if (apuLogCount == apuLog.Length) Array.Resize(ref apuLog, apuLog.Length * 2);
                apuLog[apuLogCount++] = new ApuWrite { Clock = clock, Reg = (byte)(addr - 0x4000), Value = val };
            } : null;
        }
    }

    public ReadOnlySpan<ApuWrite> ApuLog => apuLog.AsSpan(0, apuLogCount);
    /// <summary>APU clock when the last frame started and ended.</summary>
    public long FrameStartClock { get; private set; }
    public long ApuClock => nes.ApuClock;
    /// <summary>The last value the game wrote to each APU register, and the mask of those it has written.</summary>
    public uint CopyApuLatch(byte[] regs) => nes.CopyApuLatch(regs);
    /// <summary>Output samples the last frames made, without running another frame.</summary>
    public int ReadAvailable(Span<float> dest) => resampler.Read(dest);

    /// <summary>Runs one NES frame and moves its sound into the resampler.</summary>
    public void RunFrame()
    {
        apuLogCount = 0;
        FrameStartClock = nes.ApuClock;
        nes.RunFrame();
        FramesRun++;
        AfterFrame?.Invoke();
        var apu = nes.ActiveApu;
        if (apu == null) return;
        int n;
        while ((n = apu.ReadSamples(pull.AsSpan(0, Math.Min(pull.Length, resampler.FreeSpace)))) > 0)
            resampler.Write(pull.AsSpan(0, n));
    }

    /// <summary>Samples ready at the host rate without running another frame.</summary>
    public int Available => resampler.Available;

    /// <summary>Fills <paramref name="dest"/> at the host rate, running frames as needed. Always fills
    /// all of dest: silence after a crash, or for a core that makes no PCM (e.g. WF).</summary>
    public int ReadSamples(Span<float> dest)
    {
        int done = 0, idleFrames = 0;
        while (done < dest.Length)
        {
            int got = resampler.Read(dest.Slice(done));
            done += got;
            if (done == dest.Length) break;
            idleFrames = got > 0 ? 0 : idleFrames + 1;
            if (nes.IsCrashed() || idleFrames > 4) { dest.Slice(done).Clear(); break; }
            RunFrame();
        }
        return dest.Length;
    }

    public byte PeekRam(ushort address) => nes.PeekSystemRam(address & 0x7FF);
    public void PokeRam(ushort address, byte value) => nes.PokeSystemRam(address & 0x7FF, value);
    public byte PeekRam(string symbol, int index = 0) => PeekRam((ushort)(Symbols.Ram(symbol) + index));
    public void PokeRam(string symbol, byte value, int index = 0) => PokeRam((ushort)(Symbols.Ram(symbol) + index), value);

    /// <summary>Writes PRG-ROM by file offset (after the iNES header): not a CPU-bus write, so a
    /// flash mapper (UNROM-512) does not see it. For data regions a host fills, e.g. VRUN's
    /// Exact-mode song region.</summary>
    public void PokePrg(int offset, byte value) => nes.PokePrg(offset, value);
}
