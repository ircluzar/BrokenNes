using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>
/// A Master System (or SG-1000 / SC-3000 / Mark III) game on the real board: the Z80, the VDP, the SN76489 (and whatever sound unit the APU menu picked) and the cartridge, behind the
/// shape every console session has. Model and region come from <see cref="SegaSettings"/> (the file name carries the region tag; the user can override).
/// Input: A and B are buttons 1 and 2, Select is the Pause button (an NMI), Mode is the Reset button, the direction pad is the pad; player 2 has its own pad.
/// </summary>
public class SmsSession : IConsoleSession
{
    protected const int Rate = 44100;
    protected readonly BOARD_SMS board;
    protected string cpuId, ppuId, apuId;
    private readonly string mapperName;
    private readonly PsgVariant variant;

    /// <param name="modelChoice">Overrides the board revision (SMS 1, SMS 2, Mark III, SG-1000, SC-3000); null takes it from the file extension.</param>
    /// <param name="regionChoice">Overrides the video standard; null takes the region tag in the file name (NTSC when it has none).</param>
    public SmsSession(byte[] rom, ConsoleKind console, string cpu, string ppu, string apu, string fileName = "", MasterSystemModel? modelChoice = null, RegionChoice? regionChoice = null)
    {
        rom = SegaRomFormat.StripCopierHeader(rom);
        GameId = ConsoleSessions.Sha1(rom);
        Console = console;
        var model = SegaSettings.ResolveMasterSystemModel(modelChoice, console, fileName);
        // a game that needs the original VDP (see SmsCartridge.KnownModels) gets it unless the user chose a board
        if ((modelChoice is null or MasterSystemModel.Auto) && console == ConsoleKind.MasterSystem && model == MasterSystemModel.Sms2 && SmsCartridge.KnownModel(rom) is { } needed) model = needed;
        // every Game Gear runs at 60 Hz whatever market its cartridge was sold in: a "(E)" in the file name does not make it a 50 Hz machine (only an explicit user choice does)
        var region = console == ConsoleKind.GameGear && (regionChoice is null or RegionChoice.Auto) ? SegaRegion.Ntsc : SegaSettings.ResolveRegion(regionChoice, console, rom, fileName);
        variant = model is MasterSystemModel.Sg1000 or MasterSystemModel.Sc3000 ? PsgVariant.Ti : PsgVariant.Sega;
        // A BIOS image (a file whose name says so: "[BIOS] ..." in the common dump sets) is not a cartridge: it is run as the console's BIOS with an empty slot, from a cold power-on, which is what it is for. Nothing in the app
        // ever needs a BIOS; this only lets the user's own BIOS images (and the games built into some of them) run as what they are.
        bool biosImage = IsBiosImage(fileName);
        SmsCartridge? bios = null;
        SmsCartridge cart;
        if (biosImage) { bios = new SmsCartridge(rom, SmsMapperKind.Sega); cart = new SmsCartridge(new byte[0x4000], SmsMapperKind.Plain); Array.Fill(cart.Rom, (byte)0xFF); mapperName = "BIOS"; }
        else { cart = new SmsCartridge(rom, SmsCartridge.DetectMapper(rom, model)); mapperName = cart.Mapper.ToString(); }
        cpuId = cpu; ppuId = ppu; apuId = apu;
        var parts = new SegaBoardParts<SmsBus, Z80<SmsBus>, SmsVdp, ISmsSound>
        {
            Apu = m => SmsSoundFactories.Create(apu, new SmsSoundRequest(m, console, variant)),
        };
        board = new BOARD_SMS(cart, console, model, region, Rate, parts, bios);
        Title = !string.IsNullOrEmpty(fileName) ? Path.GetFileNameWithoutExtension(fileName) : $"{Consoles.DisplayName(console)} game";
        Model = model;
    }

    /// <summary>True for a file that is a BIOS image by its name ("[BIOS]" in the dump sets' names, or the word BIOS as a word).</summary>
    public static bool IsBiosImage(string fileName) =>
        !string.IsNullOrEmpty(fileName) && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(fileName), @"(^|[\s\[\(_-])BIOS($|[\s\]\)_-])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public MasterSystemModel Model { get; }
    public BOARD_SMS Board => board;
    public ConsoleKind Console { get; }
    public string Title { get; }
    public string Description => $"{Consoles.DisplayName(Console)} {Title} ({Model}, {mapperName}, {board.Region}) | CPU {cpuId} | PPU {ppuId} | APU {apuId} ({board.Sound.CoreName})";
    public double FramesPerSecond => board.FramesPerSecond;
    public string GameId { get; }

    public void SetPad(int player, PadButtons b)
    {
        if (player > 1) return;
        SmsButtons s = 0;
        if ((b & PadButtons.Up) != 0) s |= SmsButtons.Up; if ((b & PadButtons.Down) != 0) s |= SmsButtons.Down;
        if ((b & PadButtons.Left) != 0) s |= SmsButtons.Left; if ((b & PadButtons.Right) != 0) s |= SmsButtons.Right;
        if ((b & PadButtons.A) != 0) s |= SmsButtons.One; if ((b & PadButtons.B) != 0) s |= SmsButtons.Two;
        if (player == 0)
        {
            if ((b & PadButtons.Select) != 0) s |= SmsButtons.Pause;
            if ((b & PadButtons.Mode) != 0) s |= SmsButtons.Reset;
            if ((b & PadButtons.Start) != 0) s |= SmsButtons.Start;
        }
        board.SetPad(player, s);
    }

    public virtual void RunFrame() => board.RunFrame();

    public void Reset() => board.Reset();
    public virtual uint[] Frame => board.Vdp.Frame;
    public virtual int FrameWidth => 256;
    public virtual int FrameHeight => board.Vdp.FrameHeight;
    public virtual int DisplayWidth => 256;
    public int SampleRate => board.Sound.SampleRate;
    public int ReadSamples(short[] buffer) => board.Sound.ReadSamples(buffer);

    public bool HasBattery => board.Cart.HasBattery;
    public byte[] ExportSave() => board.Cart.ExportSave();
    public void ImportSave(byte[] data) => board.Cart.ImportSave(data);

    // ---- save states (the shared contract in ConsoleSystems.cs; the container in ConsoleStates.cs) ----

    /// <summary>The layout of the board's payload: bump it whenever the board, the VDP, the cartridge, the Z80 or a sound unit changes what it writes, so an old state is refused rather than misread.</summary>
    public const string StateFormat = "sms-2";

    public bool SupportsState => true;

    /// <summary>The parts a state depends on and that must be equal for it to load: the sound unit (a state saved with the stock unit does not fit the FM unit), the board model and the timing region, and the payload layout.</summary>
    private Dictionary<string, string> StateUnits() => new() { ["apu"] = apuId, ["model"] = Model.ToString(), ["region"] = board.Region.ToString(), ["format"] = StateFormat };

    public byte[] SaveState()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) board.SaveState(w);
        var units = StateUnits();
        units["cpu"] = cpuId; units["ppu"] = ppuId;   // information only: the Z80 and the picture chip are what they are
        return ConsoleStateFile.Wrap(Console, GameId, Title, units, ms.ToArray());
    }

    /// <summary>The payload <see cref="SaveState"/> wraps (no container, no compression): what BrokenDev Timeflow takes every frame.</summary>
    public byte[] SaveStatePayload()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        using (NesEmulator.Sega.BandLimitedMixer.HostIndependent()) board.SaveState(w);   // the machine's state, not its host's (the sound it has not read: see there)
        return ms.ToArray();
    }

    public void LoadState(byte[] state)
    {
        // the header is checked before anything is touched; a refusal leaves the machine as it was
        byte[] payload = ConsoleStateFile.Unwrap(state, Console, GameId, Title, StateUnits());
        byte[] before = SnapshotForUndo();
        try
        {
            using var r = new BinaryReader(new MemoryStream(payload), System.Text.Encoding.UTF8);
            board.LoadState(r);
            if (r.BaseStream.Position != payload.Length) throw new InvalidDataException("The state has more data than this machine reads (it was made by another layout).");
            AfterStateLoad();
        }
        catch (Exception e)
        {
            // a payload that passed the checksum but does not fit (a layout the format name did not catch): put the machine back as it was and say so, never run on half a state
            using var r = new BinaryReader(new MemoryStream(before), System.Text.Encoding.UTF8);
            board.LoadState(r);
            AfterStateLoad();
            throw new InvalidDataException("The state could not be loaded and the game was left as it was: " + e.Message, e);
        }
    }

    private byte[] SnapshotForUndo()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) board.SaveState(w);
        return ms.ToArray();
    }

    /// <summary>What a session derives from the board after a state load (the Game Gear's window of the picture).</summary>
    protected virtual void AfterStateLoad() { }

    /// <summary>
    /// Swaps one core while the game runs. Only an id the console's own menu offers (<see cref="CoreCatalog"/>) is accepted: anything else is refused, never quietly taken (the app then restarts the game on it, which says so).
    /// The sound slot builds the unit and hands it the PSG as it is; the picture slot has one chip in the board (the console's own), so naming it is the only in-place choice here (a NES picture chip is drawn by the wrapper,
    /// <c>SegaPictureSession</c>, which this session does not know); the CPU slot has one Z80.
    /// </summary>
    public bool TrySwapCore(CoreSlot slot, string id)
    {
        if (string.IsNullOrEmpty(id) || !CoreCatalog.Options(Console, slot).Any(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return false;
        switch (slot)
        {
            case CoreSlot.Apu:
            {
                ISmsSound next;
                try { next = SmsSoundFactories.Create(id, new SmsSoundRequest(new SegaMachine(board.Timeline, GenesisModel.Model1, Rate), Console, variant)); }
                catch (ArgumentException) { return false; }   // not a sound unit this console has: the app restarts the game, which says so
                // the new unit picks up the channels as they are
                using (var ms = new MemoryStream())
                {
                    using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) board.Sound.SaveState(w);
                    ms.Position = 0;
                    using var r = new BinaryReader(ms);
                    try { next.LoadState(r); } catch (Exception) { /* a unit with a different state layout starts from reset */ }
                }
                board.SwapSound(next);
                apuId = id;
                return true;
            }
            case CoreSlot.Ppu:
                if (id.Contains(':')) return false;   // drawing the picture on a NES chip (or another console's enhanced chip) is the wrapper's work, not the board's
                ppuId = id; return true;   // one picture chip per console: the one running
            default: cpuId = id; return true;
        }
    }

    public void Dispose() { }
}

/// <summary>A Game Gear game: the same board with the Game Gear VDP (12-bit colour), ports $00-$06, the PSG's stereo port, and the picture cropped to the LCD's 160 x 144 window of the 256 x 192 render.</summary>
public sealed class GgSession : SmsSession
{
    public const int LcdWidth = 160, LcdHeight = 144;
    private readonly uint[] lcd = new uint[LcdWidth * LcdHeight];

    public GgSession(byte[] rom, string cpu, string ppu, string apu, string fileName = "", RegionChoice? regionChoice = null)
        : base(rom, ConsoleKind.GameGear, cpu, ppu, apu, fileName, null, regionChoice) { }

    public override void RunFrame()
    {
        board.RunFrame();
        CropLcd();
    }

    private void CropLcd()
    {
        var src = board.Vdp.Frame;
        int h = board.Vdp.FrameHeight;
        int top = Math.Max(0, (h - LcdHeight) / 2);
        for (int y = 0; y < LcdHeight; y++)
            Array.Copy(src, (top + y) * 256 + 48, lcd, y * LcdWidth, LcdWidth);
    }

    protected override void AfterStateLoad() => CropLcd();

    public override uint[] Frame => lcd;
    public override int FrameWidth => LcdWidth;
    public override int FrameHeight => LcdHeight;
    public override int DisplayWidth => LcdWidth;
}
