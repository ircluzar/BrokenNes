using NesEmulator;
using NesEmulator.Plugin;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes2;

/// <summary>A game running inside an emulator in ROM mode, whatever its console: it is advanced one frame at a time, its sound read
/// at the host's rate, its picture copied out. Used under the emulator's lock only.</summary>
internal interface IGame : IDisposable
{
    /// <summary>0 NES, 1 Game Boy, 2 SNES, 3 Master System / Game Gear (<see cref="Cx"/>).</summary>
    int Console { get; }
    /// <summary>The NES machine when the game is a NES one: it gives per-channel stems by replaying the register writes.</summary>
    NesAudioMachine? Nes { get; }
    /// <summary>The stems of a Sega game's sound unit (its PSG, FM and DAC voices on their own), or null for every other game.</summary>
    IGameStems? Stems { get; }
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
    /// <summary>A copy of a Master System, Game Gear or Genesis game's sound chips: the PSG's registers, the YM2413's, or the YM2612's channel patches and the DAC sounds found in its recent writes (null for every other game).</summary>
    SegaSnapshot? CaptureSega();
}

/// <summary>A running game's voices on their own, by plugin channel (<see cref="Ch"/>), at the host's rate and in step with the game's whole mix.</summary>
internal interface IGameStems
{
    /// <summary>Whether the game's sound unit has this channel's voice (a Game Gear game has no FM; a Master System game on the PSG chip has none either).</summary>
    bool Has(int channel);
    /// <summary>Which channels are wanted (a bit per <see cref="Ch"/> id); the unit makes only those, and a newly wanted one starts in step with the mix from the next frame.</summary>
    void Want(int channelMask);
    /// <summary>Moves the finished frames of a wanted channel out (after a frame has run).</summary>
    int Read(int channel, Span<float> dest);
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
    public IGameStems? Stems => null;
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
    public SegaSnapshot? CaptureSega() => null;

    public void Dispose() { }
}

/// <summary>A Game Boy or SNES game on BrokenNes's console sessions: the console's own CPU, picture chip and sound unit (the best of its
/// family). Its stereo sound is mixed to mono and resampled to the host's rate.</summary>
internal sealed class SessionGame : IGame, IGameStems
{
    private readonly IConsoleSession s;
    private readonly StreamResampler resampler = new();
    private readonly short[] pcm = new short[16384];
    private readonly float[] mono = new float[8192];
    private int hostRate;
    private long frames;
    private string? crash;

    // ---- stems (Master System, Game Gear and Genesis games): the sound unit's voices on their own, brought to the host rate by a resampler each that starts in step with the mix's ----
    private readonly StreamResampler?[] stemRes = new StreamResampler?[Ch.Total];
    private readonly float[] stemTmp = new float[8192];
    private IVoiceStems? armedUnit;
    private int wantedMask;

    public SessionGame(byte[] rom, ConsoleKind kind, Chip chip, int hostRate, string fileName = "")
    {
        Console = Cx.FromKind(kind);
        // the file name matters to the Master System family: its region tag and its SG-1000 / SC-3000 extensions come from it
        s = ConsoleSessions.Create(kind, rom, CoreCatalog.Default(kind, CoreSlot.Cpu), CoreCatalog.Default(kind, CoreSlot.Ppu), ApuFor(kind, chip), null, fileName);
        HostSampleRate = hostRate;
        HookDacRecorder();
    }

    // ---- Instrument Runaway on a Genesis game: the writes the game makes to the YM2612's DAC register, kept so the sounds in them can be found ----
    private readonly DacRecorder dacRecorder = new();
    private MdSoundUnit? dacHooked;

    /// <summary>Taps the Genesis sound unit's port writes (a unit that was swapped for another gets tapped when it first runs).</summary>
    private void HookDacRecorder()
    {
        if (s is not NesEmulator.Sega.MdSession md || md.Board.Sound is not MdSoundUnit unit || ReferenceEquals(unit, dacHooked)) return;
        if (dacHooked != null) dacHooked.YmPortTap = null;
        unit.YmPortTap = dacRecorder.OnWrite;
        dacHooked = unit;
    }

    public int Console { get; }
    public int ChipIndex { get; set; }
    public NesAudioMachine? Nes => null;
    public int HostSampleRate { get => hostRate; set { hostRate = value; resampler.SetRates(s.SampleRate, value); foreach (var r in stemRes) r?.SetRates(s.SampleRate, value); } }
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
            HookDacRecorder();
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
            DrainStems();
        }
        catch (Exception e)
        {
            crash = e.Message;
            BrokenNes.Fruity.Diag.Log("game crashed: " + e);
        }
    }

    public int ReadAvailable(Span<float> dest) => resampler.Read(dest);

    // ---- stems ----

    /// <summary>The session's sound unit when it has stems (Master System, Game Gear, Genesis); read each time, since an APU swap replaces the unit.</summary>
    private IVoiceStems? Unit => s switch
    {
        NesEmulator.Sega.SmsSession sms => sms.Board.Sound as IVoiceStems,
        NesEmulator.Sega.MdSession md => md.Board.Sound as IVoiceStems,
        _ => null,
    };

    public IGameStems? Stems => Unit != null ? this : null;

    /// <summary>The unit's stem for a plugin channel, or -1 when the unit has none. The PSG's four voices are channels 0 to 3 everywhere; FM 1 to 9 follow the PSG's stems (the Genesis has six); the Genesis DAC and the
    /// Master System chip's five rhythm voices have their channels.</summary>
    internal static int StemOf(IVoiceStems u, int channel)
    {
        if (channel is >= Ch.Pulse1 and <= Ch.Noise) return channel;
        if (u is NesEmulator.Sega.MdSoundUnit)
        {
            if (channel is >= Ch.Fm1 and < Ch.Fm1 + 6) return NesEmulator.Sega.SegaStems.MdFm1 + (channel - Ch.Fm1);
            if (channel == Ch.Dac) return NesEmulator.Sega.SegaStems.MdDac;
        }
        else if (u is NesEmulator.Sega.SmsFmSound)
        {
            if (channel is >= Ch.Fm1 and <= Ch.Fm9) return NesEmulator.Sega.SegaStems.SmsFm1 + (channel - Ch.Fm1);
            if (channel is >= Ch.Bass and <= Ch.Cymbal) return NesEmulator.Sega.SegaStems.SmsRhythm1 + (channel - Ch.Bass);
        }
        return -1;
    }

    public bool Has(int channel) => (uint)channel < Ch.Total && Unit is { } u && StemOf(u, channel) >= 0;

    public void Want(int channelMask)
    {
        var u = Unit;
        if (u == null) return;
        if (!ReferenceEquals(u, armedUnit))
        {
            // a new unit (the game started, or the sound chip was swapped): it starts with nothing wanted
            armedUnit = u; wantedMask = 0;
            foreach (var r in stemRes) r?.Clear();
        }
        if (channelMask == wantedMask) return;
        long unitMask = 0;
        for (int c = 0; c < Ch.Total; c++)
        {
            int idx = StemOf(u, c);
            if (idx < 0 || (channelMask >> c & 1) == 0) continue;
            unitMask |= 1L << idx;
            if ((wantedMask >> c & 1) == 0)
            {
                stemRes[c] ??= new StreamResampler();
                stemRes[c]!.CopyPhaseFrom(resampler);
            }
        }
        u.StemMask = unitMask;
        wantedMask = channelMask;
    }

    /// <summary>After a frame: every wanted voice's finished frames go through its own resampler (the same rates and phase as the mix's, so they add up to it).</summary>
    private void DrainStems()
    {
        var u = Unit;
        if (u == null || wantedMask == 0 || !ReferenceEquals(u, armedUnit)) return;
        for (int c = 0; c < Ch.Total; c++)
        {
            if ((wantedMask >> c & 1) == 0) continue;
            int idx = StemOf(u, c);
            if (idx < 0) continue;
            var res = stemRes[c]!;
            int n;
            while ((n = u.ReadStem(idx, stemTmp)) > 0)
                for (int off = 0; off < n; )
                {
                    int take = Math.Min(n - off, res.FreeSpace);
                    if (take <= 0) break;
                    res.Write(stemTmp.AsSpan(off, take));
                    off += take;
                }
        }
    }

    public int Read(int channel, Span<float> dest) => (uint)channel < Ch.Total ? stemRes[channel]?.Read(dest) ?? 0 : 0;

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

    public SegaSnapshot? CaptureSega()
    {
        switch (s)
        {
            case NesEmulator.Sega.SmsSession sms:
            {
                var t = sms.Board.Timeline;
                double clock = t.MasterHz / t.Z80.Divider;
                return sms.Board.Sound switch
                {
                    SmsFmSound fm => SegaSnapshot.FromOpll(fm, clock),
                    SmsSound psg => SegaSnapshot.FromPsg(psg.Psg, clock),
                    _ => null,                       // a NES chip playing the Master System's registers (the mix lab): no Sega chip to copy
                };
            }
            case NesEmulator.Sega.MdSession md when md.Board.Sound is MdSoundUnit unit:
                return SegaSnapshot.FromOpn(unit, md.Board.Timeline.MasterHz, dacRecorder);
        }
        return null;
    }

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

    /// <summary>The sound unit a game of this kind runs on: the chip's own (<see cref="Cx.RomApu"/>), except that a Game Gear game takes the Game Gear's menu id (the same unit, with its stereo port).</summary>
    private static string ApuFor(ConsoleKind kind, Chip chip) => kind == ConsoleKind.GameGear ? CoreCatalog.Default(kind, CoreSlot.Apu) : Cx.RomApu(chip);

    public bool TrySetChip(Chip chip)
    {
        if (!s.TrySwapCore(CoreSlot.Apu, ApuFor(s.Console, chip))) return false;
        resampler.SetRates(s.SampleRate, hostRate);
        return true;
    }

    public void Dispose() => s.Dispose();
}
