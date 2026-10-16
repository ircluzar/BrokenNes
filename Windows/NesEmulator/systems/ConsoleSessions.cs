using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using NesEmulator.Gb;
using NesEmulator.Mix;
using NesEmulator.Snes;

namespace NesEmulator.Systems;

/// <summary>Creates the session for a ROM on a console with the chosen cores (ids from <see cref="CoreCatalog"/>).</summary>
public static class ConsoleSessions
{
    /// <param name="loadFirmware">SNES coprocessor firmware lookup (null: none; DSP-1 games then use the homemade chip).</param>
    /// <param name="fileName">The ROM's file name, when the app has one (the Master System family takes its region tag from it).</param>
    /// <param name="sega">The machine settings of the Sega consoles (region, Genesis model, Master System board); null or Auto takes them from the game (the file name's region tag, the header) as before.</param>
    public static IConsoleSession Create(ConsoleKind console, byte[] rom, string cpu, string ppu, string apu,
                                         Func<string[], (string name, byte[] data)?>? loadFirmware = null, string fileName = "", NesEmulator.Sega.SegaOptions? sega = null) => console switch
    {
        ConsoleKind.Snes => new SnesSession(rom, cpu, ppu, apu, loadFirmware),
        ConsoleKind.GameBoy or ConsoleKind.GameBoyColor => new GbSession(rom, console, cpu, ppu, apu),
        ConsoleKind.MasterSystem or ConsoleKind.GameGear or ConsoleKind.Genesis => CreateSega(console, rom, cpu, ppu, apu, fileName, sega),
        _ => throw new ArgumentException("The NES runs on the NES class, not a console session"),
    };

    /// <summary>
    /// The Sega consoles. While a console has no core, only the foundation preview can start it, and what it starts is a clearly labelled placeholder. A
    /// track replaces the placeholder with its session in the same change that sets the console's Ready flag; a Ready console that still has no session is a
    /// bug, so it refuses rather than quietly running the placeholder.
    /// </summary>
    private static IConsoleSession CreateSega(ConsoleKind console, byte[] rom, string cpu, string ppu, string apu, string fileName, NesEmulator.Sega.SegaOptions? options)
    {
        options ??= NesEmulator.Sega.SegaOptions.Default;
        // Track C3a: the picture can be drawn by a NES chip ("NES:<chip>", through the NES also a Game Boy or SNES one). The board session always runs with the console's own chip; the wrapper turns its VDP state into the
        // other chip's picture (and is a pass-through while the console's own chip is selected).
        string own = ppu.StartsWith("NES:", StringComparison.OrdinalIgnoreCase) ? CoreCatalog.Default(console, CoreSlot.Ppu) : ppu;
        if (console == ConsoleKind.Genesis && Consoles.GenesisReady) return new NesEmulator.Mix.SegaPictureSession(new NesEmulator.Sega.MdSession(rom, cpu, own, apu, options.Region, options.GenesisModel), ppu);
        // Track A: the Master System family. Its sessions are the real board once the console is marked ready.
        if (console == ConsoleKind.MasterSystem && Consoles.MasterSystemReady) return new NesEmulator.Mix.SegaPictureSession(new NesEmulator.Sega.SmsSession(rom, console, cpu, own, apu, fileName, options.MasterSystemModel, options.Region), ppu);
        if (console == ConsoleKind.GameGear && Consoles.GameGearReady) return new NesEmulator.Mix.SegaPictureSession(new NesEmulator.Sega.GgSession(rom, cpu, own, apu, fileName, options.Region), ppu);
        if (Consoles.IsReady(console))
            throw new InvalidOperationException($"{Consoles.DisplayName(console)} is marked ready but ConsoleSessions.CreateSega has no session for it");
        if (!Consoles.IsAvailable(console))
            throw new NotSupportedException($"{Consoles.DisplayName(console)} is not supported yet");
        return new NesEmulator.Sega.SegaPlaceholderSession(console, rom, cpu, ppu, apu);
    }

    internal static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    /// <summary>True when the console's menu for <paramref name="slot"/> lists <paramref name="id"/>: a swap to anything else is refused (never accepted, never replaced by another core).</summary>
    internal static bool Offers(ConsoleKind console, CoreSlot slot, string id) => System.Linq.Enumerable.Any(CoreCatalog.Options(console, slot), o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    internal static string NotOffered(ConsoleKind console, CoreSlot slot, string id) => $"the {Consoles.DisplayName(console)} {slot} menu does not offer '{id}'";

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
    private SnesToNes? down;
    private ISnesPpuCore? snesPpu;
    private SnesPpuSnapshot? mid;
    private readonly uint[] frame = new uint[PPU_SFC.HiResWidth * 240];
    private int width = PPU_SFC.Width, height = PPU_SFC.Height;
    private readonly byte[] romBytes;
    private string cpuId, ppuId, apuId;
    private readonly string chipLabel;
    /// <summary>The game's real SPC700 + DSP while the silent HLE unit stands in (swapped back in, it resumes).</summary>
    private APU_SFC? parkedSpc;

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
        cpuId = cpu; ppuId = ppu; apuId = apu;
        chipLabel = chip != null ? $" | {chip.Name}" : chipNote != "" ? $" | {chipNote}" : "";
        Title = cart.Title.Trim();
        SetPicture(ppu);
    }

    /// <summary>The picture path: the SFC chip itself, or a NES / Game Boy chip through the SNES->NES translation (a view
    /// of the SFC chip's state - the SFC chip keeps running either way, so this swaps freely).</summary>
    private void SetPicture(string ppu)
    {
        if (ppu.StartsWith("NES:", StringComparison.OrdinalIgnoreCase))
        {
            down = new SnesToNes(ppu[4..]);
            snesPpu = SnesCores.Wrap(board.Ppu);
            // The SNES-support chips translate line by line: capture every line's registers as the SNES draws it.
            board.Ppu.BridgeLines = down.Layered ? new PPU_SFC.BridgeLineState[240] : null;
            // The picture is sampled mid-frame (games force-blank in their NMI), as the downgrade lab does.
            var sp = snesPpu;
            board.InstructionHook = _ => { if (mid == null && board.Scanline == 112) mid = sp.Snapshot(); };
        }
        else
        {
            down = null; snesPpu = null; board.Ppu.BridgeLines = null; board.InstructionHook = null;
        }
    }

    public string SwapRefusal(CoreSlot slot, string id) => ConsoleSessions.NotOffered(ConsoleKind.Snes, slot, id);

    public bool TrySwapCore(CoreSlot slot, string id)
    {
        if (!ConsoleSessions.Offers(ConsoleKind.Snes, slot, id)) return false;     // an id the menu does not list is refused, never accepted
        switch (slot)
        {
            case CoreSlot.Ppu:
                SetPicture(id); ppuId = id; return true;
            case CoreSlot.Apu:
            {
                // The game's own SPC700 + DSP, wherever it is now (native, inside a NES bridge, or parked behind HLE).
                var current = board.Apu;
                var real = current as APU_SFC ?? (current as NesApuOnSnes)?.Front as APU_SFC ?? parkedSpc;
                if (id.Equals("HLE", StringComparison.OrdinalIgnoreCase))
                {
                    if (real != null) parkedSpc = real;
                    board.SwapApu(new APU_HLE());
                }
                else
                {
                    if (real == null) real = new APU_SFC();   // no running driver to keep (HLE from the start): a fresh unit
                    real.SyncTo(board.MasterClock);           // a parked unit resumes from here, not from when it stopped
                    parkedSpc = null;
                    if (id.StartsWith("NES:", StringComparison.OrdinalIgnoreCase)) { MixConfig.NesBackApu = id[4..]; board.SwapApu(new NesApuOnSnes(real, board.MasterClock)); }
                    else board.SwapApu(real);
                }
                apuId = id; return true;
            }
            default:
                cpuId = id; return true;   // one SNES CPU (SFC)
        }
    }

    public ConsoleKind Console => ConsoleKind.Snes;
    public string Title { get; }
    /// <summary>The game's audio unit (plugin hosts read its sample RAM and DSP registers: APU_SFC when the sound is the SFC unit).</summary>
    public ISnesApu Apu => board.Apu;
    /// <summary>The SNES picture chip (tools: per-line register capture).</summary>
    public PPU_SFC Ppu => board.Ppu;
    /// <summary>The SFC board (work RAM, cartridge, the chips): for tools that read or write the machine's memory (the corruption domains).</summary>
    public BOARD_SFC Board => board;
    /// <summary>The game's real SPC700 + DSP while it is the sound unit that is running (native, or inside a NES sound-chip bridge); null while the silent HLE stand-in is in its place.</summary>
    public APU_SFC? Spc => board.Apu as APU_SFC ?? (board.Apu as NesApuOnSnes)?.Front as APU_SFC;
    public string Description => $"SNES {Title} | CPU {cpuId} | PPU {ppuId} | APU {apuId}{chipLabel}";
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

    // save states: the SNES board (65816, S-PPU, SPC700 and DSP, coprocessors) keeps no save/load of its own yet, so this session reports that through the contract's defaults and says why
    public string StateUnsupportedReason => "The SNES has no save states yet: its CPU, picture chip, sound unit and coprocessors have no state to save.";
}

/// <summary>A Game Boy (DMG) or Game Boy Color game: the GB board with the SM83 at its own or another console's clock,
/// or the 65816; the picture on the Game Boy chip, any NES chip or the SNES chip; the sound on the Game Boy unit or any
/// NES APU.</summary>
public sealed class GbSession : IConsoleSession
{
    private readonly BOARD_GB board;
    /// <summary>The game's sound unit (plugin hosts read its registers and wave RAM: APU_GB when the sound is the Game Boy unit).</summary>
    public IGbApu Apu => board.Apu;
    private GbLineCapture? cap;
    private GbToNes? toNes;
    private GbToSnes? toSnes;
    private readonly uint[] frame = new uint[256 * 240];
    private int width = PPU_GB.Width, height = PPU_GB.Height;
    private string cpuId, ppuId, apuId;
    private readonly string mapperName;
    /// <summary>CPUs swapped out mid-game, by kind: swapped back in, one resumes where it stopped.</summary>
    private readonly Dictionary<string, IGbCpu> parkedCpus = new();

    public GbSession(byte[] rom, ConsoleKind console, string cpu, string ppu, string apu)
    {
        GameId = ConsoleSessions.Sha1(rom);
        var cart = GbCartridge.Load(rom);
        // A Game Boy Color game - or any game when the Game Boy look is Color: a DMG game then runs on a GBC in its
        // compatibility mode, colourised by title as a real GBC does.
        var model = console == ConsoleKind.GameBoyColor || GbLook.Color ? GbModel.Cgb : GbModel.Dmg;
        Func<IGbCpuBus, IGbCpu>? cpuFactory = cpu.Equals("65816", StringComparison.OrdinalIgnoreCase) ? bus => new Cpu65816OnGb(bus) : null;
        board = new BOARD_GB(cart, model, SoundFactory(apu), cpuFactory);
        board.CpuClockFactor = ClockOf(cpu);
        SetPicture(ppu);
        cpuId = cpu; ppuId = ppu; apuId = apu;
        Console = console;
        Title = cart.Title.Trim();
        mapperName = cart.MapperName;
    }

    private static Func<GbModel, IGbApu>? SoundFactory(string apu)
    {
        if (apu.StartsWith("NES:", StringComparison.OrdinalIgnoreCase)) { MixConfig.GbBackNesApu = apu[4..]; return m => new GbApuOnNes(m); }
        if (apu.Equals("GBS", StringComparison.OrdinalIgnoreCase)) return m => new APU_GBS(m);
        return null;
    }

    private static double ClockOf(string cpu) => cpu.ToUpperInvariant() switch
    {
        "GB-HALF" => 0.5,
        "GB-2X" => 2.0,
        "GB-NESCLOCK" => 1789773.0 / 1048576.0,
        "GB-SNESCLOCK" => 3579545.0 / 1048576.0,
        _ => 1.0,
    };

    /// <summary>The picture path: the Game Boy chip itself, or a NES / SNES chip drawing its state (a view - the Game Boy
    /// chip keeps running, so this swaps freely).</summary>
    private void SetPicture(string ppu)
    {
        toNes = null; toSnes = null;
        if (ppu.StartsWith("NES:", StringComparison.OrdinalIgnoreCase)) { cap ??= new GbLineCapture(board.Ppu); toNes = new GbToNes(ppu[4..]); }
        else if (ppu.StartsWith("SNES:", StringComparison.OrdinalIgnoreCase)) { cap ??= new GbLineCapture(board.Ppu); toSnes = new GbToSnes(ppu[5..]); }
    }

    public string SwapRefusal(CoreSlot slot, string id) => ConsoleSessions.NotOffered(Console, slot, id);

    public bool TrySwapCore(CoreSlot slot, string id)
    {
        if (!ConsoleSessions.Offers(Console, slot, id)) return false;     // an id the menu does not list is refused: it would otherwise be swapped for the Game Boy's own chip under another name
        switch (slot)
        {
            case CoreSlot.Ppu:
                SetPicture(id); ppuId = id; return true;
            case CoreSlot.Apu:
            {
                // The new sound chip takes over the channels as they are (every Game Boy sound chip here carries APU_GB state).
                var model = board.Model;
                var next = SoundFactory(id)?.Invoke(model) ?? new APU_GB(model);
                using (var ms = new System.IO.MemoryStream())
                {
                    using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) board.Apu.SaveState(w);
                    ms.Position = 0;
                    using var r = new System.IO.BinaryReader(ms);
                    next.LoadState(r);
                }
                board.SwapApu(next);
                apuId = id; return true;
            }
            default:
            {
                // Same CPU kind at another clock: just the clock. Another kind (SM83 <-> 65816): the running one is parked
                // and the other takes over (a parked one resumes where it stopped; a new one starts from its reset).
                string Kind(string c) => c.Equals("65816", StringComparison.OrdinalIgnoreCase) ? "65816" : "SM83";
                string from = Kind(cpuId), to = Kind(id);
                if (from != to)
                {
                    parkedCpus[from] = board.Core;
                    var next = parkedCpus.TryGetValue(to, out var parked) ? parked : to == "65816" ? new Cpu65816OnGb(board) : new CPU_GB(board);
                    parkedCpus.Remove(to);
                    board.SwapCpu(next);
                }
                board.CpuClockFactor = ClockOf(id);
                cpuId = id; return true;
            }
        }
    }

    public ConsoleKind Console { get; }
    public string Title { get; }
    public string Description => $"{Consoles.DisplayName(Console)} {Title} ({mapperName}) | CPU {cpuId} | PPU {ppuId} | APU {apuId}";
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
        var shades = board.Ppu.DmgColors;   // the Game Boy look, live (DMG games on a DMG)
        for (int i = 0; i < 4; i++) shades[i] = GbLook.Argb(i);
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

    // ---- save states (the shared contract in ConsoleSystems.cs). The board's own state covers the Game Boy's SM83, picture chip, sound chip, cartridge and memory; the parts that are not the Game Boy's own (the 65816 in the
    // SM83's place, a NES sound chip standing in for the Game Boy's) have no state of their own to carry, so a session using one says so rather than saving half a machine.

    /// <summary>The layout of the payload: bump it whenever the board, the cartridge, the SM83, the picture chip or a Game Boy sound chip changes what it writes.</summary>
    public const string StateFormat = "gb-1";

    private bool OwnParts => !cpuId.Equals("65816", StringComparison.OrdinalIgnoreCase) && !apuId.StartsWith("NES:", StringComparison.OrdinalIgnoreCase);
    public bool SupportsState => OwnParts;
    public string StateUnsupportedReason => OwnParts ? "" : "Save states need the Game Boy's own CPU and sound chip: the 65816 and the NES sound chips standing in for them have no state to save. Switch them back in the Console menu (CPU / APU) to use save states.";

    private Dictionary<string, string> StateUnits() => new() { ["cpu"] = cpuId, ["apu"] = apuId, ["model"] = board.Model.ToString(), ["format"] = StateFormat };

    public byte[] SaveState()
    {
        if (!SupportsState) throw new NotSupportedException(StateUnsupportedReason);
        var units = StateUnits();
        units["ppu"] = ppuId;   // information only: a picture chip other than the Game Boy's is a view of the Game Boy chip's state
        return ConsoleStateFile.Wrap(Console, GameId, Title, units, board.SaveState());
    }

    public void LoadState(byte[] state)
    {
        if (!SupportsState) throw new NotSupportedException(StateUnsupportedReason);
        byte[] payload = ConsoleStateFile.Unwrap(state, Console, GameId, Title, StateUnits());
        byte[] before = board.SaveState();
        try { board.LoadState(payload); }
        catch (Exception e)
        {
            board.LoadState(before);   // put the machine back as it was, and say so
            throw new System.IO.InvalidDataException("The state could not be loaded and the game was left as it was: " + e.Message, e);
        }
    }
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
