using System;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>
/// A Genesis / Mega Drive game on <see cref="BOARD_MD"/>: the 68000 core, the VDP and the sound unit the console's menus selected. <b>This milestone has no picture renderer</b>:
/// <see cref="Frame"/> is the VDP's debug view (VRAM as tiles in the palette line <see cref="DebugPalette"/>, with the colour RAM under it), which says so in <see cref="Description"/>. The console stays
/// behind the preview switch (<see cref="Consoles.GenesisReady"/> is false) until a real picture exists.
/// </summary>
public sealed class MdSession : IConsoleSession
{
    public const int DebugWidth = 512, DebugHeight = 272;

    public BOARD_MD Board { get; }
    private readonly uint[] frame = new uint[DebugWidth * DebugHeight];
    private readonly string cpuId, ppuId;
    private string apuId;
    private readonly short[] scratch = new short[0];
    private readonly string title;
    private readonly string gameId;
    private readonly int sampleRate;

    /// <summary>Which of the four palette lines the debug view draws the tiles in.</summary>
    public int DebugPalette { get; set; }

    public MdSession(byte[] rom, string cpu, string ppu, string apu, RegionChoice? region = null, GenesisModelChoice? model = null, int sampleRate = 44100)
    {
        cpuId = cpu; ppuId = ppu; apuId = apu;
        this.sampleRate = sampleRate;
        gameId = ConsoleSessions.Sha1(rom);
        var cart = new MdCartridge(rom);
        var r = SegaSettings.ResolveRegion(region, ConsoleKind.Genesis, rom);
        var m = SegaSettings.ResolveGenesisModel(model, rom);
        var parts = new SegaBoardParts<MdBus, M68k<MdBus>, MdVdp, IGenesisSound> { Apu = machine => GenesisSoundFactories.Create(apu, machine) };
        Board = new BOARD_MD(cart, r, m, sampleRate, parts);
        title = cart.OverseasTitle.Length > 0 ? cart.OverseasTitle : cart.DomesticTitle;
        FramesPerSecond = Board.Timeline.FramesPerSecond;
    }

    public ConsoleKind Console => ConsoleKind.Genesis;
    public string Title => title;
    public string Description =>
        $"Genesis {title} ({Board.Cart.Mapper}) | {Board.Region} {Board.Model} | CPU {cpuId} | PPU {ppuId} (NO PICTURE RENDERER YET: the frame is the VDP's VRAM/CRAM debug view) | APU {apuId}: {Board.Sound.CoreName}";
    public double FramesPerSecond { get; }
    public string GameId => gameId;

    public void SetPad(int player, PadButtons buttons) => Board.SetPad(player, buttons);

    public void RunFrame()
    {
        Board.RunFrame();
        Board.Vdp.RenderDebugView(frame, DebugPalette);
    }

    public void Reset() => Board.Reset();

    public uint[] Frame => frame;
    public int FrameWidth => DebugWidth;
    public int FrameHeight => DebugHeight;
    public int DisplayWidth => DebugWidth;
    public int SampleRate => sampleRate;

    public int ReadSamples(short[] buffer) => Board.Sound.ReadSamples(buffer);

    public bool HasBattery => Board.Cart.HasBattery;
    public byte[] ExportSave() => (byte[])Board.Cart.Sram.Clone();
    public void ImportSave(byte[] data) => Array.Copy(data, Board.Cart.Sram, Math.Min(data.Length, Board.Cart.Sram.Length));

    /// <summary>Swapping a part needs a restart: the 68000 and the VDP have one choice each, and the sound unit's state cannot move between units yet.</summary>
    public bool TrySwapCore(CoreSlot slot, string id) => slot switch
    {
        CoreSlot.Cpu => id.Equals(cpuId, StringComparison.OrdinalIgnoreCase),
        CoreSlot.Ppu => id.Equals(ppuId, StringComparison.OrdinalIgnoreCase),
        _ => id.Equals(apuId, StringComparison.OrdinalIgnoreCase),
    };

    public void Dispose() { }
}
