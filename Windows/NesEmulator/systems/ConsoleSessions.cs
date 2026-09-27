using System;
using System.Security.Cryptography;
using NesEmulator.Gb;
using NesEmulator.Mix;
using NesEmulator.Snes;

namespace NesEmulator.Systems;

/// <summary>Creates the session for a ROM on a console with the chosen cores (ids from <see cref="CoreCatalog"/>).</summary>
public static class ConsoleSessions
{
    /// <param name="loadFirmware">SNES coprocessor firmware lookup (null: none; DSP-1 games then use the homemade chip).</param>
    public static IConsoleSession Create(ConsoleKind console, byte[] rom, string cpu, string ppu, string apu,
                                         Func<string[], (string name, byte[] data)?>? loadFirmware = null) => console switch
    {
        ConsoleKind.Snes => new SnesSession(rom, cpu, ppu, apu, loadFirmware),
        ConsoleKind.GameBoy or ConsoleKind.GameBoyColor => new GbSession(rom, console, cpu, ppu, apu),
        _ => throw new ArgumentException("The NES runs on the NES class, not a console session"),
    };

    internal static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    /// <summary>RGBA bytes (a NES picture) to 0xAARRGGBB.</summary>
    internal static void RgbaToArgb(byte[] rgba, uint[] dst)
    {
        int n = Math.Min(dst.Length, rgba.Length / 4);
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            dst[i] = 0xFF000000u | (uint)rgba[o] << 16 | (uint)rgba[o + 1] << 8 | rgba[o + 2];
        }
    }
}

/// <summary>A SNES game: the SFC board, its picture on the SFC chip or any NES chip (through the SNES->NES downgrade,
/// which keeps every layer and sprite on PPU_FIXS), its sound on the SFC unit, the HLE unit or any NES APU.</summary>
public sealed class SnesSession : IConsoleSession
{
    public const double NtscFps = 21477272.0 / (1364 * 262);

    private readonly BOARD_SFC board;
    private readonly SnesToNes? down;
    private readonly ISnesPpuCore? snesPpu;
    private SnesPpuSnapshot? mid;
    private readonly uint[] frame = new uint[PPU_SFC.HiResWidth * 240];
    private int width = PPU_SFC.Width, height = PPU_SFC.Height;
    private readonly byte[] romBytes;

    public SnesSession(byte[] rom, string cpu, string ppu, string apu, Func<string[], (string name, byte[] data)?>? loadFirmware)
    {
        romBytes = rom;
        GameId = "sfc" + ConsoleSessions.Sha1(rom);
        var cart = SnesCartridge.Load(rom);
        ISnesApu unit;
        if (apu.StartsWith("NES:", StringComparison.OrdinalIgnoreCase)) { MixConfig.NesBackApu = apu[4..]; unit = SnesCores.CreateApu("NES"); }
        else if (apu.Equals("HLE", StringComparison.OrdinalIgnoreCase)) unit = new APU_HLE();
        else unit = new APU_SFC();
        var chip = SnesChips.Create(cart, loadFirmware, false, out string chipNote);
        board = new BOARD_SFC(cart, unit, chip);
        if (ppu.StartsWith("NES:", StringComparison.OrdinalIgnoreCase))
        {
            down = new SnesToNes(ppu[4..]);
            snesPpu = SnesCores.Wrap(board.Ppu);
            // The picture is sampled mid-frame (games force-blank in their NMI), as the downgrade lab does.
            board.InstructionHook = _ => { if (mid == null && board.Scanline == 112) mid = snesPpu.Snapshot(); };
        }
        Title = cart.Title.Trim();
        Description = $"SNES {Title} | CPU {cpu} | PPU {ppu} | APU {apu}" + (chip != null ? $" | {chip.Name}" : chipNote != "" ? $" | {chipNote}" : "");
    }

    public ConsoleKind Console => ConsoleKind.Snes;
    public string Title { get; }
    public string Description { get; }
    public double FramesPerSecond => NtscFps;
    public string GameId { get; }

    private static ushort Map(PadButtons b)
    {
        SnesButtons s = 0;
        if ((b & PadButtons.Up) != 0) s |= SnesButtons.Up; if ((b & PadButtons.Down) != 0) s |= SnesButtons.Down;
        if ((b & PadButtons.Left) != 0) s |= SnesButtons.Left; if ((b & PadButtons.Right) != 0) s |= SnesButtons.Right;
        if ((b & PadButtons.A) != 0) s |= SnesButtons.A; if ((b & PadButtons.B) != 0) s |= SnesButtons.B;
        if ((b & PadButtons.X) != 0) s |= SnesButtons.X; if ((b & PadButtons.Y) != 0) s |= SnesButtons.Y;
        if ((b & PadButtons.L) != 0) s |= SnesButtons.L; if ((b & PadButtons.R) != 0) s |= SnesButtons.R;
        if ((b & PadButtons.Start) != 0) s |= SnesButtons.Start; if ((b & PadButtons.Select) != 0) s |= SnesButtons.Select;
        return (ushort)s;
    }

    public void SetPad(int player, PadButtons buttons) { if (player is 0 or 1) board.Pads[player] = Map(buttons); }

    public void RunFrame()
    {
        mid = null;
        board.RunFrame();
        if (down != null)
        {
            var rgba = down.Render(snesPpu!, mid);
            ConsoleSessions.RgbaToArgb(rgba, frame);
            width = 256; height = 240;
            return;
        }
        height = board.Ppu.VisibleHeight;
        bool hires = board.Ppu.FrameHasHiRes;
        width = hires ? PPU_SFC.HiResWidth : PPU_SFC.Width;
        Array.Copy(hires ? board.Ppu.GetHiResFrame() : board.Ppu.FrameBuffer, frame, width * height);
    }

    public void Reset() => board.Reset();
    public uint[] Frame => frame;
    public int FrameWidth => width;
    public int FrameHeight => height;
    public int DisplayWidth => 256;
    public int SampleRate => board.Apu.SampleRate;
    public int ReadSamples(short[] buffer) => board.Apu.ReadSamples(buffer);
    public bool HasBattery => board.Cart.HasBattery && board.Cart.Sram.Length > 0;
    public byte[] ExportSave() => (byte[])board.Cart.Sram.Clone();
    public void ImportSave(byte[] data) => Array.Copy(data, board.Cart.Sram, Math.Min(data.Length, board.Cart.Sram.Length));
    public void Dispose() { }
}

/// <summary>A Game Boy (DMG) or Game Boy Color game: the GB board with the SM83 at its own or another console's clock,
/// or the 65816; the picture on the Game Boy chip, any NES chip or the SNES chip; the sound on the Game Boy unit or any
/// NES APU.</summary>
public sealed class GbSession : IConsoleSession
{
    private readonly BOARD_GB board;
    private readonly GbLineCapture? cap;
    private readonly GbToNes? toNes;
    private readonly GbToSnes? toSnes;
    private readonly uint[] frame = new uint[256 * 240];
    private int width = PPU_GB.Width, height = PPU_GB.Height;

    public GbSession(byte[] rom, ConsoleKind console, string cpu, string ppu, string apu)
    {
        GameId = ConsoleSessions.Sha1(rom);
        var cart = GbCartridge.Load(rom);
        var model = console == ConsoleKind.GameBoyColor ? GbModel.Cgb : GbModel.Dmg;
        Func<GbModel, IGbApu>? apuFactory = null;
        if (apu.StartsWith("NES:", StringComparison.OrdinalIgnoreCase)) { MixConfig.GbBackNesApu = apu[4..]; apuFactory = m => new GbApuOnNes(m); }
        else if (apu.Equals("GBS", StringComparison.OrdinalIgnoreCase)) apuFactory = m => new APU_GBS(m);
        Func<IGbCpuBus, IGbCpu>? cpuFactory = cpu.Equals("65816", StringComparison.OrdinalIgnoreCase) ? bus => new Cpu65816OnGb(bus) : null;
        board = new BOARD_GB(cart, model, apuFactory, cpuFactory);
        board.CpuClockFactor = cpu.ToUpperInvariant() switch
        {
            "GB-HALF" => 0.5,
            "GB-2X" => 2.0,
            "GB-NESCLOCK" => 1789773.0 / 1048576.0,
            "GB-SNESCLOCK" => 3579545.0 / 1048576.0,
            _ => 1.0,
        };
        if (ppu.StartsWith("NES:", StringComparison.OrdinalIgnoreCase)) { cap = new GbLineCapture(board.Ppu); toNes = new GbToNes(ppu[4..]); }
        else if (ppu.StartsWith("SNES:", StringComparison.OrdinalIgnoreCase)) { cap = new GbLineCapture(board.Ppu); toSnes = new GbToSnes(ppu[5..]); }
        Console = console;
        Title = cart.Title.Trim();
        Description = $"{Consoles.DisplayName(console)} {Title} ({cart.MapperName}) | CPU {cpu} | PPU {ppu} | APU {apu}";
    }

    public ConsoleKind Console { get; }
    public string Title { get; }
    public string Description { get; }
    public double FramesPerSecond => BOARD_GB.DmgFps;
    public string GameId { get; }
    /// <summary>The cartridge's accelerometer (MBC7), for apps that feed tilt.</summary>
    public bool HasTilt => board.Cart.HasTilt;
    public void SetTilt(float x, float y) => board.Cart.SetTilt(x, y);

    public void SetPad(int player, PadButtons b)
    {
        if (player != 0) return;
        GbButtons g = 0;
        if ((b & PadButtons.Up) != 0) g |= GbButtons.Up; if ((b & PadButtons.Down) != 0) g |= GbButtons.Down;
        if ((b & PadButtons.Left) != 0) g |= GbButtons.Left; if ((b & PadButtons.Right) != 0) g |= GbButtons.Right;
        if ((b & PadButtons.A) != 0) g |= GbButtons.A; if ((b & PadButtons.B) != 0) g |= GbButtons.B;
        if ((b & PadButtons.Start) != 0) g |= GbButtons.Start; if ((b & PadButtons.Select) != 0) g |= GbButtons.Select;
        if (g != board.Buttons) { board.Buttons = g; board.UpdateJoypadIrq(); }
    }

    public void RunFrame()
    {
        board.RunFrame();
        if (toNes != null) { ConsoleSessions.RgbaToArgb(toNes.Render(board, cap!), frame); width = 256; height = 240; }
        else if (toSnes != null) { var f = toSnes.Render(board, cap!); Array.Copy(f, frame, Math.Min(f.Length, frame.Length)); width = 256; height = f.Length / 256; }
        else { Array.Copy(board.Ppu.FrameBuffer, frame, PPU_GB.Width * PPU_GB.Height); width = PPU_GB.Width; height = PPU_GB.Height; }
    }

    public void Reset() => board.Reset();
    public uint[] Frame => frame;
    public int FrameWidth => width;
    public int FrameHeight => height;
    public int DisplayWidth => width;
    public int SampleRate => board.Apu.SampleRate;
    public int ReadSamples(short[] buffer) => board.Apu.ReadSamples(buffer);
    public bool HasBattery => board.HasBattery;
    public byte[] ExportSave() => board.ExportSave();
    public void ImportSave(byte[] data) => board.ImportSave(data);
    public void Dispose() { }
}

/// <summary>Stereo 16-bit -> mono float at another rate (linear interpolation), for audio paths that play mono float.</summary>
public sealed class MonoResampler
{
    private double pos;          // position in the source stream, relative to the first sample kept in `last`
    private float last;
    private readonly System.Collections.Generic.List<float> outBuf = new();

    /// <summary>Converts <paramref name="count"/> interleaved stereo shorts at <paramref name="srcRate"/> to mono floats at
    /// <paramref name="dstRate"/>.</summary>
    public float[] Convert(short[] stereo, int count, int srcRate, int dstRate)
    {
        outBuf.Clear();
        int frames = count / 2;
        if (frames == 0) return Array.Empty<float>();
        double step = (double)srcRate / dstRate;
        // Source sample i (i = -1 is `last`, the final sample of the previous call).
        float Src(int i) => i < 0 ? last : (stereo[i * 2] + stereo[i * 2 + 1]) * (0.5f / 32768f);
        while (pos < frames - 1)
        {
            int i = (int)Math.Floor(pos); double f = pos - i;
            outBuf.Add((float)(Src(i) + (Src(i + 1) - Src(i)) * f));
            pos += step;
        }
        pos -= frames;
        last = Src(frames - 1);
        return outBuf.ToArray();
    }
}
