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
        ConsoleKind.WideMess => new SnesSession(rom, cpu, ppu, apu, loadFirmware, wideMess: true),
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
        string own = ppu.Contains(':') ? CoreCatalog.Default(console, CoreSlot.Ppu) : ppu;     // a NES chip or another console's enhanced chip: the board keeps its own
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
/// which keeps every layer and sprite on PPU_FIXS), its sound on the SFC unit, the HLE unit or any NES APU.
/// <para>The same session is the <b>WideMess</b> console (<c>wideMess: true</c>, docs/projects/widemess-spec.md): the SFC board with the WideMess picture chip (<see cref="PPU_WMS"/>) and its registers
/// (<see cref="WmsExtension"/>), running a .wms game or a SNES game. Locked, the WideMess is a SNES, so a SNES game runs exactly as on the SNES console.</para></summary>
public sealed partial class SnesSession : IConsoleSession, IButcherHost
{
    // ===================================================================== Butchering mode (corruption/Butcher.cs)
    public Butcher? Butcher { get; private set; }
    public void ApplyButcher(ButcherSettings settings)
    {
        var b = Butcher ?? new Butcher { Profile = ButcherProfile.Snes };
        b.Apply(settings, 0);
        Butcher = b;
    }
    public ButcherReach GetButcherReach() => new(
        PpuCore: "SNES PPU", ConsolePins: true, ConsolePinsPartial: false, CartPins: false, CartAddressLines: 0, ChrIsRam: true, Mapper: -1,
        SpritesDma: true, Sprites2004: false, Sound: ButcherApu() != null, Bands: true, Ram: true)
    { CpuPins = true, CpuAddressLines = 24, CpuDataLines = 8, CpuPinsTitle = "CPU bus · cartridge pins (A bus: A0-A23, D0-D7)",
      PadOrder = Butcher.PadSnes, PadNames = new[] { "Up", "Down", "Left", "Right", "B", "Y", "Select", "Start", "A", "X", "L", "R" },
      DmaLines = 24, DmaDataLines = 8,
      CoproName = board.Coprocessor switch { SA1_SFC => "SA-1", GSU_SFC => "Super FX", DSP1_SFC => "DSP-1", _ => "" },
      SoundCpuLines = ButcherApu() != null ? 16 : 0, SoundBusLines = 7, SampleLines = 16, SoundRam = ButcherApu() != null, SamplePinsTitle = "Sample fetch · BRR reads from ARAM (A0-A15, D0-D7)", SoundCpuTitle = "Sound CPU bus · SPC700 over its 64 KB ARAM (A0-A15, D0-D7)",
      Console = "snes", ConsoleAddressLines = ButcherProfile.Snes.ConsoleLines, ConsoleDataLines = 16, AttrNames = ButcherProfile.Snes.AttrNames, Channels = ButcherProfile.Snes.Channels,
      BandKinds = new[] { "shift", "slip", "patternSwap", "nametableSwap", "bgOff", "spritesOff", "bend" },
      ExtraBands = new[] { "Mode 7 wobble|The Mode 7 matrix is pushed out of shape inside the band.", "BG mode flip|The band is drawn in another background mode.", "Colour math flip|Colour addition becomes subtraction inside the band." },
      PinsTitle = "Picture bus · VRAM pins", PinsWhy = "The word-wide bus between the PPU and its two 32 KB video RAM chips: tile maps, tile graphics (D0-D7 the low chip, D8-D15 the high one).",
      SoundNote = ButcherApu() != null ? "The S-DSP's voice registers, as the SPC700 writes them (echo, sample directory and key-on are left alone)." : board.Apu is APU_HLE ? "This sound core makes no sound (HLE only fakes the boot-time upload): nothing to bend." : "Not reachable with this sound core." };
    /// <summary>The real SPC700 + S-DSP unit, wherever it sits: the board's own, or inside the adapter / the NES-chip bridge (the guest's own writes are bent, whatever chip plays them).</summary>
    private APU_SFC? ButcherApu() => board.Apu switch
    {
        APU_SFC a => a,
        NesEmulator.Mix.SfcApuAdapter ad => ad.Inner,
        NesEmulator.Mix.NesApuOnSnes nb => nb.Front as APU_SFC,
        _ => null,
    };

    private void ButcherFrame()
    {
        var b = Butcher;
        var apu = ButcherApu();
        if (b != null)
        {
            b.AttachRam(board.Wram); b.AttachVramWords(board.Ppu.Vram); b.AttachSoundRam(apu?.Aram);
            b.GhostNmiLine = board.ButcherGhostNmi; b.HoldIrqLine = board.ButcherHoldIrq;
            b.EchoInject = apu != null ? (r, v) => apu.ButcherDspWrite((ushort)r, v) : null; b.DspReadReg = apu != null ? r => apu.Dsp.Read(r) : null;
            b.FrameStart(apu != null ? apu.ButcherDspWrite : (_, _) => { });
        }
        var on = b != null && !b.Idle ? b : null;
        board.Ppu.Butcher = on; if (apu != null) { apu.Butcher = on; apu.Dsp.Butcher = on; }
        board.ButcherCpu = on is { CpuPins: not null } || on is { Dma: not null } || on is { VideoBusFaults: not null } || on is { IrqActive: true } ? on : null;
        switch (board.Coprocessor) { case SA1_SFC sa: sa.Butcher = on; break; case GSU_SFC g: g.Butcher = on; break; case DSP1_SFC d: d.Butcher = on; break; }
    }

    public const double NtscFps = 21477272.0 / (1364 * 262);

    private readonly BOARD_SFC board;
    /// <summary>The WideMess picture chip when this session is the WideMess console (null on the SNES).</summary>
    private readonly PPU_WMS? wms;
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

    public SnesSession(byte[] rom, string cpu, string ppu, string apu, Func<string[], (string name, byte[] data)?>? loadFirmware, bool wideMess = false)
    {
        romBytes = rom;
        // a .wms game is its header + a SNES-format image (checked: a damaged file or one needing more than this console has is refused here, with the reason); a SNES game is itself and keeps its SNES id, so its saves are the SNES console's
        WmsFile.Header? wmsHeader = null;
        byte[] image = wideMess ? WmsFile.Payload(rom, out wmsHeader) : rom;
        bool wmsGame = wideMess && wmsHeader != null;
        GameId = (wmsGame ? "wms" : "sfc") + ConsoleSessions.Sha1(rom);
        var cart = wmsGame ? WmsFile.Cartridge(image, wmsHeader!) : SnesCartridge.Load(image);   // a .wms game's map is its header's (the WideMess map, or LoROM / HiROM checked against the image)
        ISnesApu unit;
        // another console's sound chip (through the NES registers, or directly): on the WideMess console the game's own unit inside the bridge is the WideMess's, so its sound RAM port and unlock line still work
        if (apu.StartsWith("NES:", StringComparison.OrdinalIgnoreCase)) { MixConfig.NesBackApu = apu[4..]; unit = wideMess ? new NesApuOnSnes(new APU_WMS(), 0) : SnesCores.CreateApu("NES"); }
        else if (apu.Equals("HLE", StringComparison.OrdinalIgnoreCase)) unit = new APU_HLE();
        else if (CoreCatalog.IsDirectRoute(apu)) unit = new SnesApuOnChip(apu, wideMess ? new APU_WMS() : null, 0);          // another console's enhanced sound chip, directly
        else if (apu.Equals("SFCS", StringComparison.OrdinalIgnoreCase)) unit = new APU_SFCS();   // the SNES's own, enhanced: the stock unit for a SNES game
        else if (apu.Equals("WMS", StringComparison.OrdinalIgnoreCase)) unit = new APU_WMS();     // the WideMess sound chip: the SNES's until a game unlocks it
        else if (apu.Equals("ZS15", StringComparison.OrdinalIgnoreCase)) unit = new APU_ZS15();   // the SNES unit heard through ZSNES 0.15's sound engine (foreign-apu-project)
        else if (apu.Equals("OSPC", StringComparison.OrdinalIgnoreCase)) unit = new APU_OSPC();   // the SNES unit heard through libOpenSPC's (2002) sound engine (foreign-apu-project)
        else if (apu.Equals("S9X1", StringComparison.OrdinalIgnoreCase)) unit = new APU_S9X1();   // the SNES unit heard through Snes9x 1.43's sound engine (foreign-apu-project)
        else if (apu.Equals("S951", StringComparison.OrdinalIgnoreCase)) unit = new APU_S951();   // the SNES unit heard through Snes9x 1.51 sound engine (foreign-apu-project)
        else if (apu.Equals("ZS151", StringComparison.OrdinalIgnoreCase)) unit = new APU_ZS151(); // the SNES unit heard through ZSNES 1.51's sound engine (foreign-apu-project)
        else if (apu.Equals("S916", StringComparison.OrdinalIgnoreCase)) unit = new APU_S916();
        else if (apu.Equals("SNDS", StringComparison.OrdinalIgnoreCase)) unit = new APU_SNDS();   // the SNES unit heard through SNEmulDS's ARM7 DSP (foreign-apu-project)   // the SNES unit heard through Snes9x 1.16a sound engine (foreign-apu-project)
        else unit = new APU_SFC();
        var chip = SnesChips.Create(cart, loadFirmware, false, out string chipNote);
        if (wideMess)
        {
            board = new BOARD_SFC(cart, unit, chip, cpuFactory: null, ppuFactory: () => new PPU_WMS());
            wms = (PPU_WMS)board.Ppu;
            board.WideMess = new WmsExtension(wms);
            board.WideMess.Clock = () => board.MasterClock;   // the sound RAM port lands at the CPU's moment (spec 4)
            board.WideMess.Sound = Spc as APU_WMS;             // the WideMess sound chip, itself or inside another console's chip's bridge
        }
        else board = new BOARD_SFC(cart, unit, chip);
        cpuId = cpu; ppuId = ppu; apuId = apu;
        chipLabel = chip != null ? $" | {chip.Name}" : chipNote != "" ? $" | {chipNote}" : "";
        Title = wmsGame && wmsHeader!.Title.Length > 0 ? wmsHeader.Title : cart.Title.Trim();
        SetPicture(ppu);
    }

    /// <summary>The WideMess picture chip (null unless this is the WideMess console).</summary>
    public PPU_WMS? WideMessPpu => wms;
    /// <summary>The WideMess registers (null unless this is the WideMess console).</summary>
    public WmsExtension? WideMess => board.WideMess;

    /// <summary>The picture path: the SFC chip itself, or a NES / Game Boy chip through the SNES->NES translation (a view
    /// of the SFC chip's state - the SFC chip keeps running either way, so this swaps freely).</summary>
    private void SetPicture(string ppu)
    {
        bool route = CoreCatalog.IsDirectRoute(ppu);     // another console's enhanced picture chip, reached directly: the SNES picture as layers at its own size
        if (ppu.StartsWith("NES:", StringComparison.OrdinalIgnoreCase) || route)
        {
            down = route ? new SnesToNes(EnhancedChips.Picture(ppu), ppu) : new SnesToNes(ppu[4..]);
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

    public string SwapRefusal(CoreSlot slot, string id) => ConsoleSessions.NotOffered(Console, slot, id);

    public bool TrySwapCore(CoreSlot slot, string id)
    {
        if (!ConsoleSessions.Offers(Console, slot, id)) return false;     // an id the menu does not list is refused, never accepted
        switch (slot)
        {
            case CoreSlot.Ppu:
                SetPicture(id); ppuId = id; return true;
            case CoreSlot.Apu:
            {
                // The game's own SPC700 + DSP, wherever it is now (native, inside a NES bridge or an enhanced chip's, or parked behind HLE).
                var current = board.Apu;
                var real = current as APU_SFC ?? (current as NesApuOnSnes)?.Front as APU_SFC ?? (current as SnesApuOnChip)?.Front as APU_SFC ?? parkedSpc;
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
                    // the SNES's own unit, stock or enhanced, or the WideMess's: the same SPC700 + S-DSP state moves into the other kind of unit (the enhanced one's extra banks are empty on its own console;
                    // the WideMess's voices 8-31 and its sound RAM above 64 KB start empty, unlocked if the console is: a SNES unit has none to carry)
                    if (!id.Contains(':') && !IsUnitKind(real, id)) real = MoveSpc(real, NewUnit(id));
                    if (id.StartsWith("NES:", StringComparison.OrdinalIgnoreCase)) { MixConfig.NesBackApu = id[4..]; board.SwapApu(new NesApuOnSnes(real, board.MasterClock)); }
                    else if (CoreCatalog.IsDirectRoute(id)) board.SwapApu(new SnesApuOnChip(id, real, board.MasterClock));
                    else board.SwapApu(real);
                }
                if (board.WideMess != null) board.WideMess.Sound = Spc as APU_WMS;   // the sound RAM port and the unlock line follow the WideMess sound chip; a SNES unit has neither
                apuId = id; return true;
            }
            default:
                cpuId = id; return true;   // one SNES CPU (SFC)
        }
    }

    /// <summary>True when the unit is the kind the menu id names (SFC, SFCS or WMS).</summary>
    private static bool IsUnitKind(APU_SFC unit, string id) => id.ToUpperInvariant() switch
    {
        "SFCS" => unit is APU_SFCS,
        "WMS" => unit is APU_WMS,
        "ZS15" => unit is APU_ZS15,
        "OSPC" => unit is APU_OSPC,
        "S9X1" => unit is APU_S9X1,
        "S951" => unit is APU_S951,
        "ZS151" => unit is APU_ZS151,
        "S916" => unit is APU_S916,
        "SNDS" => unit is APU_SNDS,
        _ => unit.GetType() == typeof(APU_SFC),
    };

    private static APU_SFC NewUnit(string id) => id.ToUpperInvariant() switch { "SFCS" => new APU_SFCS(), "WMS" => new APU_WMS(), "ZS15" => new APU_ZS15(), "OSPC" => new APU_OSPC(), "S9X1" => new APU_S9X1(), "S951" => new APU_S951(), "ZS151" => new APU_ZS151(), "S916" => new APU_S916(), "SNDS" => new APU_SNDS(), _ => new APU_SFC() };

    /// <summary>The same SPC700 + S-DSP in a unit of the other kind (stock / enhanced / WideMess): its whole state moves, at the board's time.</summary>
    private static APU_SFC MoveSpc(APU_SFC from, APU_SFC to)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) from.SaveState(w);
        ms.Position = 0;
        using (var r = new System.IO.BinaryReader(ms, System.Text.Encoding.UTF8)) to.LoadState(r);
        return to;
    }

    public ConsoleKind Console => wms != null ? ConsoleKind.WideMess : ConsoleKind.Snes;
    public string Title { get; }
    /// <summary>The game's audio unit (plugin hosts read its sample RAM and DSP registers: APU_SFC when the sound is the SFC unit).</summary>
    public ISnesApu Apu => board.Apu;
    /// <summary>The SNES picture chip (tools: per-line register capture).</summary>
    public PPU_SFC Ppu => board.Ppu;
    /// <summary>The SFC board (work RAM, cartridge, the chips): for tools that read or write the machine's memory (the corruption domains).</summary>
    public BOARD_SFC Board => board;
    /// <summary>The game's real SPC700 + DSP while it is the sound unit that is running (native, or inside a NES sound-chip bridge); null while the silent HLE stand-in is in its place.</summary>
    public APU_SFC? Spc => board.Apu as APU_SFC ?? (board.Apu as NesApuOnSnes)?.Front as APU_SFC ?? (board.Apu as SnesApuOnChip)?.Front as APU_SFC;
    public string Description => $"{(wms != null ? "WideMess" : "SNES")} {Title} | CPU {cpuId} | PPU {ppuId} | APU {apuId}{chipLabel}{(board.WideMess is { Unlocked: true } ? " | unlocked" : "")}{(board.WideMess is { Unlocked: true, Sound: null } ? $" (no WideMess sound: APU {apuId} is a SNES sound chip)" : "")}{NotCarried}{(down?.Enhanced is WmsWideChip wide ? " | " + wide.SidesSummary : "")}";
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

    public void SetPad(int player, PadButtons buttons) { if (player == 0 && Butcher is { PadActive: true }) buttons = (PadButtons)Butcher.FilterPad((ushort)buttons, Butcher.PadSnes); if (player is 0 or 1) board.Pads[player] = Map(buttons); }

    public void RunFrame()
    {
        if (Butcher != null || board.Ppu.Butcher != null || ButcherApu()?.Butcher != null) ButcherFrame();
        mid = null;
        if (wms != null) BeforeFrameEnhance();   // milestone 6: the console's enhancements (SnesSession.Enhance.cs)
        board.RunFrame();
        if (wms != null) CountWideMessNotCarried();
        if (down != null)
        {
            var f = down.Render(snesPpu!, mid);     // an enhanced chip: the SNES's own 256 x 224 / 239, all its layers (the WideMess widescreen chip: 400 wide); a stock one: the NES's 256 x 240
            Array.Copy(f.Argb, frame, f.Width * f.Height);
            width = f.Width; height = f.Height; wideBridge = f.Side > 0;
            ButcherPalette();
            return;
        }
        wideBridge = false;
        PublishNativePicture();
        ButcherPalette();
    }

    /// <summary>Butchering mode's palette: the finished picture's colours only.</summary>
    private void ButcherPalette() { if (Butcher is { PaletteActive: true } bp) bp.ApplyPalette(frame, width * height); }

    /// <summary>True while the WideMess widescreen chip draws the picture (route WMS:WMSS): the frame is 400 wide with square pixels.</summary>
    private bool wideBridge;

    // ---- the WideMess console on the other consoles' chips (milestone 7): what of a WideMess game they do not carry, counted (never a silent loss)
    /// <summary>Frames a WideMess game drew in its own picture (the 8-layer mode or 400 wide) while another console's picture chip showed the game: that chip shows what the SNES registers say.</summary>
    public long WideMessPictureFramesNotCarried { get; private set; }
    /// <summary>Frames in which a WideMess game sounded its voices 8-31 while another console's sound chip played the game: those chips replay the S-DSP's eight voices.</summary>
    public long WideMessVoiceFramesNotCarried { get; private set; }

    private void CountWideMessNotCarried()
    {
        if (down != null && wms!.FrameIsWide) WideMessPictureFramesNotCarried++;
        if ((board.Apu is NesApuOnSnes || board.Apu is SnesApuOnChip) && Spc is APU_WMS { Unlocked: true } w && w.Dsp.WideVoicesSounding > 0) WideMessVoiceFramesNotCarried++;
    }

    private string NotCarried => wms == null || (WideMessPictureFramesNotCarried == 0 && WideMessVoiceFramesNotCarried == 0) ? ""
        : $" | not carried by the other console's chips: {(WideMessPictureFramesNotCarried > 0 ? $"{WideMessPictureFramesNotCarried} frames of the WideMess picture (8 layers or 400 wide: the {ppuId} chip shows the SNES registers)" : "")}{(WideMessPictureFramesNotCarried > 0 && WideMessVoiceFramesNotCarried > 0 ? ", " : "")}{(WideMessVoiceFramesNotCarried > 0 ? $"{WideMessVoiceFramesNotCarried} frames of WideMess voices 8-31 ({apuId} replays the S-DSP's eight)" : "")}";

    /// <summary>The SNES picture chip's own picture (256 wide, or 512 when a line was drawn in a hi-res mode) as the frame the apps show.</summary>
    private void PublishNativePicture()
    {
        height = board.Ppu.VisibleHeight;
        if (wms is { FrameIsWide: true })
        {
            // a WideMess game drew 400 wide (square pixels): lines it drew in a SNES mode sit in the middle of the wide frame
            width = PPU_WMS.WideWidth;
            Array.Copy(wms.GetWideFrame(), frame, width * height);
            return;
        }
        bool hires = board.Ppu.FrameHasHiRes;
        width = hires ? PPU_SFC.HiResWidth : PPU_SFC.Width;
        Array.Copy(hires ? board.Ppu.GetHiResFrame() : board.Ppu.FrameBuffer, frame, width * height);
    }

    public void Reset() => board.Reset();
    public uint[] Frame => frame;
    public int FrameWidth => width;
    public int FrameHeight => height;
    /// <summary>256 for a SNES picture (a 512-wide hi-res frame shows at 256); 400 for a WideMess wide frame, whose pixels are square.</summary>
    public int DisplayWidth => wideBridge || (width == PPU_WMS.WideWidth && wms != null) ? width : 256;
    public int SampleRate => board.Apu.SampleRate;
    public int ReadSamples(short[] buffer) { int n = board.Apu.ReadSamples(buffer); if (Butcher is { DacActive: true } bd) bd.DacShorts(buffer.AsSpan(0, n), 2); return n; }
    public bool HasBattery => board.Cart.HasBattery && board.Cart.Sram.Length > 0;
    public byte[] ExportSave() => (byte[])board.Cart.Sram.Clone();
    public void ImportSave(byte[] data) => Array.Copy(data, board.Cart.Sram, Math.Min(data.Length, board.Cart.Sram.Length));
    public void Dispose() { }

    // save states: SnesSession.State.cs (the board, the 65816, the S-PPU, the SPC700 and S-DSP and the cartridge chips each save their own part)
}

/// <summary>A Game Boy (DMG) or Game Boy Color game: the GB board with the SM83 at its own or another console's clock,
/// or the 65816; the picture on the Game Boy chip, any NES chip or the SNES chip; the sound on the Game Boy unit or any
/// NES APU.</summary>
public sealed class GbSession : IConsoleSession, IButcherHost
{
    private readonly BOARD_GB board;
    /// <summary>The game's sound unit (plugin hosts read its registers and wave RAM: APU_GB when the sound is the Game Boy unit).</summary>
    public IGbApu Apu => board.Apu;
    private GbLineCapture? cap;
    private GbToNes? toNes;
    /// <summary>The picture bridge onto a NES chip, when the picture is on one (tests: the enhanced chip's layers).</summary>
    internal GbToNes? PictureBridge => toNes;
    private GbToSnes? toSnes;
    private readonly uint[] frame = new uint[ExtPicture.MaxViewWidth * ExtPicture.MaxLines];
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
        if (CoreCatalog.IsDirectRoute(apu)) return m => new GbApuOnChip(m, apu);          // another console's enhanced sound chip, directly
        if (apu.Equals("GBS", StringComparison.OrdinalIgnoreCase)) return m => new APU_GBS(m);
        if (apu.Equals("GNUB", StringComparison.OrdinalIgnoreCase)) return m => new APU_GNUB(m);   // gnuboy 1.0.3's sound engine (foreign-apu-project)
        if (apu.Equals("VBA", StringComparison.OrdinalIgnoreCase)) return m => new APU_VBA(m);   // VisualBoyAdvance 1.7.2's sound engine (foreign-apu-project)
        if (apu.Equals("VBAM", StringComparison.OrdinalIgnoreCase)) return m => new APU_VBAM(m); // VBA-M 2007 pre-Gb_Apu sound engine (foreign-apu-project)
        if (apu.Equals("TGBD", StringComparison.OrdinalIgnoreCase)) return m => new APU_TGBD(m);   // TGB Dual's apu.cpp (foreign-apu-project)
        if (apu.Equals("GYOB", StringComparison.OrdinalIgnoreCase)) return m => new APU_GYOB(m);   // GameYob's DS sound mapping (foreign-apu-project)
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
        else if (CoreCatalog.IsDirectRoute(ppu)) { cap ??= new GbLineCapture(board.Ppu); toNes = new GbToNes(EnhancedChips.Picture(ppu), ppu); }     // another console's enhanced chip: the Game Boy's layers at its 160 x 144
        else if (ppu.StartsWith("SNES:", StringComparison.OrdinalIgnoreCase)) { cap ??= new GbLineCapture(board.Ppu); toSnes = new GbToSnes(ppu[5..]); }
    }

    public string SwapRefusal(CoreSlot slot, string id) => ConsoleSessions.NotOffered(Console, slot, id);

    /// <summary>
    /// A swap between two different Game Boy sound chips (the stock one and the foreign APU ports: GNUB, VBA, VBAM, TGBD, GYOB) carries the registers, but a
    /// port's own channel state does not survive the trip, so notes that were sounding would stay silent until the game keyed them again
    /// (docs/KNOWN_GAPS.md FA-4). Every channel the old chip had sounding is triggered again on the new one (NRx4 bit 7 with the period's high bits;
    /// the length counter is left off so the note does not cut out early).
    /// </summary>
    internal static void RetriggerSounding(IGbApu old, IGbApu next)
    {
        var ports = old as IGbSoundingChannels;
        int sounding = ports?.SoundingMask ?? old.ReadRegister(0x26) & 0x0F;
        if (sounding == 0) return;
        ReadOnlySpan<int> nrx4 = stackalloc int[] { 0x14, 0x19, 0x1E, 0x23 };
        for (int ch = 0; ch < 4; ch++)
        {
            if ((sounding & (1 << ch)) == 0) continue;
            int hi = ch == 3 ? 0 : ports != null ? ports.PeriodHigh(ch) : old is APU_GB g ? (g.Channel(ch).Period >> 8) & 7 : old.ReadRegister(nrx4[ch]) & 7;
            next.WriteRegister(nrx4[ch], (byte)(0x80 | hi));
        }
    }

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
                var old = board.Apu;
                using (var ms = new System.IO.MemoryStream())
                {
                    using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) old.SaveState(w);
                    ms.Position = 0;
                    using var r = new System.IO.BinaryReader(ms);
                    next.LoadState(r);
                }
                if (old.GetType() != next.GetType()) RetriggerSounding(old, next);
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

    /// <summary>The Game Boy board (memories, cartridge, the chips): for tools that read or write the machine's memory (the corruption domains).</summary>
    public BOARD_GB Board => board;

    public ConsoleKind Console { get; }
    public string Title { get; }
    public string Description => $"{Consoles.DisplayName(Console)} {Title} ({mapperName}) | CPU {cpuId} | PPU {ppuId} | APU {apuId}{(toNes?.Enhanced is WmsWideChip wide ? " | " + wide.SidesSummary : "")}";
    public double FramesPerSecond => BOARD_GB.DmgFps;
    public string GameId { get; }
    /// <summary>The cartridge's accelerometer (MBC7), for apps that feed tilt.</summary>
    public bool HasTilt => board.Cart.HasTilt;
    public void SetTilt(float x, float y) => board.Cart.SetTilt(x, y);

    public void SetPad(int player, PadButtons b)
    {
        if (player != 0) return;
        if (Butcher is { PadActive: true }) b = (PadButtons)Butcher.FilterPad((ushort)b, Butcher.PadGb);
        GbButtons g = 0;
        if ((b & PadButtons.Up) != 0) g |= GbButtons.Up; if ((b & PadButtons.Down) != 0) g |= GbButtons.Down;
        if ((b & PadButtons.Left) != 0) g |= GbButtons.Left; if ((b & PadButtons.Right) != 0) g |= GbButtons.Right;
        if ((b & PadButtons.A) != 0) g |= GbButtons.A; if ((b & PadButtons.B) != 0) g |= GbButtons.B;
        if ((b & PadButtons.Start) != 0) g |= GbButtons.Start; if ((b & PadButtons.Select) != 0) g |= GbButtons.Select;
        if (g != board.Buttons) { board.Buttons = g; board.UpdateJoypadIrq(); }
    }

    // ===================================================================== Butchering mode (corruption/Butcher.cs)
    public Butcher? Butcher { get; private set; }
    public void ApplyButcher(ButcherSettings settings)
    {
        var b = Butcher ?? new Butcher { Profile = ButcherProfile.Gb };
        b.Apply(settings, 0);
        Butcher = b;
    }
    public ButcherReach GetButcherReach() => new(
        PpuCore: "Game Boy PPU", ConsolePins: true, ConsolePinsPartial: false, CartPins: false, CartAddressLines: 0, ChrIsRam: true, Mapper: -1,
        SpritesDma: true, Sprites2004: false, Sound: true, Bands: true, Ram: true)
    { ExtraBands = new[] { "Window off|The window layer vanishes inside the band.", "Window shift|The window slides sideways inside the band.", "Window map swap|The window draws from the other tile map." },
      CpuPins = true, CpuAddressLines = 15, CpuDataLines = 8, CpuPinsTitle = "CPU bus · cartridge pins (A0-A14, D0-D7; MBC writes with bend writes)",
      SoundBusLines = 6, SampleLines = 4, SoundRam = board.Apu is APU_GB, DmaLines = 16, DmaDataLines = 8, SamplePinsTitle = "Sample fetch · wave RAM (A0-A3, D0-D7)",
      PadOrder = Butcher.PadGb, PadNames = new[] { "A", "B", "Select", "Start", "Up", "Down", "Left", "Right" },
      Console = "gb", ConsoleAddressLines = ButcherProfile.Gb.ConsoleLines, AttrNames = ButcherProfile.Gb.AttrNames, Channels = ButcherProfile.Gb.Channels,
      PinsTitle = "Picture bus · VRAM pins", PinsWhy = "The bus between the picture chip and its video RAM: tile maps, tile graphics and sprite graphics (A13 is the Color bank line)." };
    private void ButcherFrame()
    {
        var b = Butcher;
        if (b != null) { b.GhostNmiLine = () => board.ButcherInterrupt(0); b.HoldIrqLine = l => { if (l) board.ButcherInterrupt(2); }; b.AttachRam(board.Wram); b.AttachVram(board.Ppu.Vram); b.AttachSoundRam((board.Apu as APU_GB)?.WaveRam); b.FrameStart(board.ButcherSoundWrite); }
        var on = b != null && !b.Idle ? b : null;
        board.Butcher = on; board.Ppu.Butcher = on;
        if (board.Apu is APU_GB ag) ag.Butcher = on;
    }

    public void RunFrame()
    {
        if (Butcher != null || board.Butcher != null) ButcherFrame();
        var shades = board.Ppu.DmgColors;   // the Game Boy look, live (DMG games on a DMG)
        for (int i = 0; i < 4; i++) shades[i] = GbLook.Argb(i);
        board.RunFrame();
        if (toNes != null) { var f = toNes.Render(board, cap!); Array.Copy(f.Argb, frame, f.Width * f.Height); width = f.Width; height = f.Height; }   // an enhanced chip: 160 x 144; a stock one: 256 x 240
        else if (toSnes != null) { var f = toSnes.Render(board, cap!); Array.Copy(f, frame, Math.Min(f.Length, frame.Length)); width = 256; height = f.Length / 256; }
        else { Array.Copy(board.Ppu.FrameBuffer, frame, PPU_GB.Width * PPU_GB.Height); width = PPU_GB.Width; height = PPU_GB.Height; }
        if (Butcher is { PaletteActive: true } bp) bp.ApplyPalette(frame, width * height);
    }

    public void Reset() => board.Reset();
    public uint[] Frame => frame;
    public int FrameWidth => width;
    public int FrameHeight => height;
    public int DisplayWidth => width;
    public int SampleRate => board.Apu.SampleRate;
    public int ReadSamples(short[] buffer) { int n = board.Apu.ReadSamples(buffer); if (Butcher is { DacActive: true } bd) bd.DacShorts(buffer.AsSpan(0, n), 2); return n; }
    public bool HasBattery => board.HasBattery;
    public byte[] ExportSave() => board.ExportSave();
    public void ImportSave(byte[] data) => board.ImportSave(data);
    public void Dispose() { }

    /// <summary>The work RAM the CPU sees at $C000-$DFFF (8 KB; a Color game's other banks are reached through the bank register): a host that corrupts or inspects memory reads and writes it by offset. -1 / false outside.</summary>
    public int WorkRamSize => 0x2000;
    public int PeekWorkRam(int offset) => (uint)offset < 0x2000u ? board.Peek((ushort)(0xC000 + offset)) : -1;
    public bool PokeWorkRam(int offset, byte value) { if ((uint)offset >= 0x2000u) return false; board.Write((ushort)(0xC000 + offset), value); return true; }

    // ---- save states (the shared contract in ConsoleSystems.cs). The board's own state covers the Game Boy's SM83, picture chip, sound chip, cartridge and memory; the parts that are not the Game Boy's own (the 65816 in the
    // SM83's place, a NES sound chip standing in for the Game Boy's) have no state of their own to carry, so a session using one says so rather than saving half a machine.

    /// <summary>The layout of the payload: bump it whenever the board, the cartridge, the SM83, the picture chip or a Game Boy sound chip changes what it writes.</summary>
    public const string StateFormat = "gb-1";

    private bool OwnParts => !cpuId.Equals("65816", StringComparison.OrdinalIgnoreCase) && !apuId.Contains(':');
    public bool SupportsState => OwnParts;
    public string StateUnsupportedReason => OwnParts ? "" : "Save states need the Game Boy's own CPU and sound chip: the 65816 and the other consoles' sound chips standing in for them (NES, SNES, Master System, Genesis) have no state to save. Switch them back in the Console menu (CPU / APU) to use save states.";

    private Dictionary<string, string> StateUnits() => new() { ["cpu"] = cpuId, ["apu"] = apuId, ["model"] = board.Model.ToString(), ["format"] = StateFormat };

    public byte[] SaveState()
    {
        if (!SupportsState) throw new NotSupportedException(StateUnsupportedReason);
        var units = StateUnits();
        units["ppu"] = ppuId;   // information only: a picture chip other than the Game Boy's is a view of the Game Boy chip's state
        return ConsoleStateFile.Wrap(Console, GameId, Title, units, board.SaveState());
    }

    /// <summary>The payload <see cref="SaveState"/> wraps (no container, no compression): what BrokenDev Timeflow takes every frame.</summary>
    public byte[] SaveStatePayload()
    {
        if (!SupportsState) throw new NotSupportedException(StateUnsupportedReason);
        return board.SaveState();
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
