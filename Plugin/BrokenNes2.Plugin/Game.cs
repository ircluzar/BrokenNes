using NesEmulator;
using NesEmulator.Plugin;
using NesEmulator.Systems;

namespace BrokenNes2;

/// <summary>A game running inside an emulator in ROM mode, whatever its console: it is advanced one frame at a time, its sound read
/// at the host's rate, its picture copied out. Used under the emulator's lock only.</summary>
internal interface IGame : IDisposable
{
    /// <summary>0 NES, 1 Game Boy, 2 SNES (<see cref="Cx"/>).</summary>
    int Console { get; }
    /// <summary>The NES machine when the game is a NES one: only it can give per-channel stems.</summary>
    NesAudioMachine? Nes { get; }
    /// <summary>The sound chip (index into <see cref="Cx.Chips"/>) the game was built with.</summary>
    int ChipIndex { get; set; }
    int HostSampleRate { get; set; }
    double FramesPerSecond { get; }
    long FramesRun { get; }
    bool Crashed { get; }
    string? CrashInfo { get; }
    /// <summary>The cores and chips it runs on, for a status line.</summary>
    string Describe();
    void RunFrame();
    /// <summary>Mono samples at the host rate that the last frames made, without running another.</summary>
    int ReadAvailable(Span<float> dest);
    /// <summary>The last picture as RGBA into <paramref name="rgba"/> (at most 512 x 480); <c>dispW</c> x <c>dispH</c> is the shape it is shown in.</summary>
    void CopyFrame(byte[] rgba, out int w, out int h, out int dispW, out int dispH);
    /// <summary>Switches the sound chip while the game runs; false when the game has to start again on it.</summary>
    bool TrySetChip(Chip chip);
    /// <summary>Player 1's buttons for the next frames (<see cref="NesEmulator.Systems.PadButtons"/> flags).</summary>
    void SetPad(int buttons);
    /// <summary>A copy of the SNES game's sound memory and DSP registers (null for every other game).</summary>
    SpcSnapshot? CaptureSpc();
    /// <summary>A copy of the Game Boy game's sound registers and wave RAM (null for every other game).</summary>
    GbSnapshot? CaptureGb();
}

/// <summary>A NES game on <see cref="NesAudioMachine"/>: the best CPU and PPU (FIX), the chosen NES sound chip.</summary>
internal sealed class NesGame : IGame
{
    private readonly NesAudioMachine m;

    public NesGame(byte[] rom, Chip chip, int hostRate)
    {
        m = new NesAudioMachine(rom, null, Cx.BestCpu(Cx.Nes), Cx.BestPpu(Cx.Nes), chip.Id) { HostSampleRate = hostRate };
    }

    public int Console => Cx.Nes;
    public int ChipIndex { get; set; }
    public NesAudioMachine? Nes => m;
    public int HostSampleRate { get => m.HostSampleRate; set => m.HostSampleRate = value; }
    public double FramesPerSecond => NesAudioMachine.FramesPerSecond;
    public long FramesRun => m.FramesRun;
    public bool Crashed => m.Crashed;
    public string? CrashInfo => m.CrashInfo;
    public string Describe() => $"CPU {m.CpuCoreId.Replace("CPU_", "")}   PPU {m.PpuCoreId.Replace("PPU_", "")}   sound {m.ApuCoreId.Replace("APU_", "")} ({m.NativeSampleRate} Hz)";
    public void RunFrame() => m.RunFrame();
    public int ReadAvailable(Span<float> dest) => m.ReadAvailable(dest);

    public void CopyFrame(byte[] rgba, out int w, out int h, out int dispW, out int dispH)
    {
        var src = m.FrameBuffer;
        Buffer.BlockCopy(src, 0, rgba, 0, Math.Min(src.Length, 256 * 240 * 4));
        w = dispW = 256; h = dispH = 240;
    }

    public bool TrySetChip(Chip chip)
    {
        m.SetApuCore(chip.Id);
        return true;
    }

    private readonly bool[] padNes = new bool[8];

    public void SetPad(int buttons)
    {
        Inputs.ToNes((PadButtons)buttons, padNes);
        m.Nes.SetInput(padNes);
    }

    public SpcSnapshot? CaptureSpc() => null;
    public GbSnapshot? CaptureGb() => null;

    public void Dispose() { }
}

/// <summary>A Game Boy or SNES game on BrokenNes's console sessions: the console's own CPU, picture chip and sound unit (the best of its
/// family). Its stereo sound is mixed to mono and resampled to the host's rate.</summary>
internal sealed class SessionGame : IGame
{
    private readonly IConsoleSession s;
    private readonly StreamResampler resampler = new();
    private readonly short[] pcm = new short[16384];
    private readonly float[] mono = new float[8192];
    private int hostRate;
    private long frames;
    private string? crash;

    public SessionGame(byte[] rom, ConsoleKind kind, Chip chip, int hostRate)
    {
        Console = Cx.FromKind(kind);
        s = ConsoleSessions.Create(kind, rom, CoreCatalog.Default(kind, CoreSlot.Cpu), CoreCatalog.Default(kind, CoreSlot.Ppu), Cx.RomApu(chip));
        HostSampleRate = hostRate;
    }

    public int Console { get; }
    public int ChipIndex { get; set; }
    public NesAudioMachine? Nes => null;
    public int HostSampleRate { get => hostRate; set { hostRate = value; resampler.SetRates(s.SampleRate, value); } }
    public double FramesPerSecond => s.FramesPerSecond;
    public long FramesRun => frames;
    public bool Crashed => crash != null;
    public string? CrashInfo => crash;
    public string Describe() => s.Description;

    public void RunFrame()
    {
        if (crash != null) return;
        try
        {
            s.RunFrame();
            frames++;
            int n;
            while ((n = s.ReadSamples(pcm)) > 0)
            {
                int count = n / 2;
                for (int i = 0; i < count; i += mono.Length)
                {
                    int take = Math.Min(Math.Min(mono.Length, count - i), resampler.FreeSpace);
                    if (take <= 0) break;
                    for (int k = 0; k < take; k++) mono[k] = (pcm[(i + k) * 2] + pcm[(i + k) * 2 + 1]) * (0.5f / 32768f);
                    resampler.Write(mono.AsSpan(0, take));
                }
            }
        }
        catch (Exception e)
        {
            crash = e.Message;
            BrokenNes.Fruity.Diag.Log("game crashed: " + e);
        }
    }

    public int ReadAvailable(Span<float> dest) => resampler.Read(dest);

    public void CopyFrame(byte[] rgba, out int w, out int h, out int dispW, out int dispH)
    {
        var f = s.Frame;
        w = Math.Min(s.FrameWidth, 512);
        h = Math.Min(s.FrameHeight, 480);
        int stride = s.FrameWidth;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint c = f[y * stride + x];
                int o = (y * w + x) * 4;
                rgba[o] = (byte)(c >> 16); rgba[o + 1] = (byte)(c >> 8); rgba[o + 2] = (byte)c; rgba[o + 3] = 255;
            }
        dispW = s.DisplayWidth;
        dispH = h >= 400 ? h / 2 : h;     // a SNES hi-res / interlaced frame has twice the lines it is shown with
    }

    public void SetPad(int buttons) => s.SetPad(0, (PadButtons)buttons);

    public SpcSnapshot? CaptureSpc() =>
        s is SnesSession snes && snes.Apu is NesEmulator.Snes.APU_SFC spc ? new SpcSnapshot(spc.Aram, spc.Dsp.Regs) : null;

    public GbSnapshot? CaptureGb() =>
        s is GbSession gb && gb.Apu is NesEmulator.Gb.APU_GB apu ? GbSnapshot.FromApu(apu) : null;

    /// <summary>Test hook: starts a tone on the running SNES sound unit, as a game's sound driver would (a test ROM has no sound driver).</summary>
    internal void PokeSnesTone()
    {
        if (s is not SnesSession snes || snes.Apu is not NesEmulator.Snes.APU_SFC spc) return;
        var t = SpcSnapshot.TestSample();
        Array.Copy(t.Aram, 0x100, spc.Aram, 0x100, 0x120);
        for (int i = 0; i < SpcSnapshot.DspSize; i++) spc.Dsp.Write(i, t.Dsp[i]);
        spc.Dsp.Write(0x00, 0x50); spc.Dsp.Write(0x01, 0x50);      // voice 0: volume
        spc.Dsp.Write(0x02, 0x00); spc.Dsp.Write(0x03, 0x10);      // pitch $1000
        spc.Dsp.Write(0x04, 0x00);                                 // sample 0
        spc.Dsp.Write(0x05, 0xFF); spc.Dsp.Write(0x06, 0xE0);      // envelope
        spc.Dsp.Write(0x4C, 0x01);                                 // key on
    }

    public bool TrySetChip(Chip chip)
    {
        if (!s.TrySwapCore(CoreSlot.Apu, Cx.RomApu(chip))) return false;
        resampler.SetRates(s.SampleRate, hostRate);
        return true;
    }

    public void Dispose() => s.Dispose();
}
