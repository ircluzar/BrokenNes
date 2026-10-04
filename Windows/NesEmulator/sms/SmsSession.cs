using System;
using System.IO;
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
        // every Game Gear runs at 60 Hz whatever market its cartridge was sold in: a "(E)" in the file name does not make it a 50 Hz machine (only an explicit user choice does)
        var region = console == ConsoleKind.GameGear && (regionChoice is null or RegionChoice.Auto) ? SegaRegion.Ntsc : SegaSettings.ResolveRegion(regionChoice, console, rom, fileName);
        variant = model is MasterSystemModel.Sg1000 or MasterSystemModel.Sc3000 ? PsgVariant.Ti : PsgVariant.Sega;
        var cart = new SmsCartridge(rom, SmsCartridge.DetectMapper(rom, model));
        mapperName = cart.Mapper.ToString();
        cpuId = cpu; ppuId = ppu; apuId = apu;
        var parts = new SegaBoardParts<SmsBus, Z80<SmsBus>, SmsVdp, ISmsSound>
        {
            Apu = m => SmsSoundFactories.Create(apu, new SmsSoundRequest(m, console, variant)),
        };
        board = new BOARD_SMS(cart, console, model, region, Rate, parts);
        Title = !string.IsNullOrEmpty(fileName) ? Path.GetFileNameWithoutExtension(fileName) : $"{Consoles.DisplayName(console)} game";
        Model = model;
    }

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

    public bool TrySwapCore(CoreSlot slot, string id)
    {
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
            case CoreSlot.Ppu: ppuId = id; return true;   // one picture chip per console; the id is a label
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
        var src = board.Vdp.Frame;
        int h = board.Vdp.FrameHeight;
        int top = Math.Max(0, (h - LcdHeight) / 2);
        for (int y = 0; y < LcdHeight; y++)
            Array.Copy(src, (top + y) * 256 + 48, lcd, y * LcdWidth, LcdWidth);
    }

    public override uint[] Frame => lcd;
    public override int FrameWidth => LcdWidth;
    public override int FrameHeight => LcdHeight;
    public override int DisplayWidth => LcdWidth;
}
