using System;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>
/// A Genesis / Mega Drive game on <see cref="BOARD_MD"/>: the 68000 core, the VDP and the sound unit the console's menus selected. <see cref="Frame"/> is the VDP's picture at its real size (256 or 320 wide,
/// 224 or 240 high; the width can change from frame to frame when a game switches modes), or, with <see cref="DebugView"/> set, the VDP's memory view (VRAM as tiles in the palette line
/// <see cref="DebugPalette"/> with the colour RAM under it).
/// </summary>
/// <remarks>
/// Pad mapping: the apps send the twelve generic buttons, the Genesis pad has eight. A, B, X, Y, Start and the directions are themselves; C is <see cref="PadButtons.C"/> or R, Z is <see cref="PadButtons.Z"/> or L and
/// Mode is <see cref="PadButtons.Mode"/> or Select (so the six-button pad is reachable from the layout the apps already have). Player 1 has a six-button pad, player 2 a three-button one.
/// </remarks>
public sealed class MdSession : IConsoleSession
{
    public const int DebugWidth = 512, DebugHeight = 272;

    public BOARD_MD Board { get; }
    private readonly uint[] frame = new uint[Math.Max(DebugWidth * DebugHeight, MdVdp.PictureStride * MdVdp.PictureMaxHeight * 2)];
    private int frameWidth = 320, frameHeight = 224;
    /// <summary>Show the VDP's memory view instead of the picture.</summary>
    public bool DebugView { get; set; }
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
        $"Genesis {title} ({Board.Cart.Mapper}) | {Board.Region} {Board.Model} | CPU {cpuId} | PPU {ppuId} | APU {apuId}: {Board.Sound.CoreName}";
    public double FramesPerSecond { get; }
    public string GameId => gameId;

    public void SetPad(int player, PadButtons buttons)
    {
        if ((buttons & PadButtons.R) != 0) buttons |= PadButtons.C;
        if ((buttons & PadButtons.L) != 0) buttons |= PadButtons.Z;
        if ((buttons & PadButtons.Select) != 0) buttons |= PadButtons.Mode;
        Board.SetPad(player, buttons);
    }

    public void RunFrame()
    {
        Board.RunFrame();
        var v = Board.Vdp;
        if (DebugView)
        {
            v.RenderDebugView(frame, DebugPalette);
            frameWidth = DebugWidth; frameHeight = DebugHeight;
            return;
        }
        CopyPicture(v);
    }

    /// <summary>The picture the VDP finished: in interlace mode 2 the double-height one with both fields woven (448 or 480 rows, shown at twice the width so the proportions hold: see <see cref="DisplayWidth"/>), else the normal one.</summary>
    private void CopyPicture(MdVdp v)
    {
        if (v.WovenValid) { frameWidth = v.PictureWidth; frameHeight = v.WovenHeight; v.CopyWovenPicture(frame); }
        else { frameWidth = v.PictureWidth; frameHeight = v.PictureHeight; v.CopyPicture(frame); }
    }

    /// <summary>Run to scanline <paramref name="line"/> (see <see cref="BOARD_MD.RunUntilLine"/>) instead of to the end of the frame, and show the picture as it stands: for comparisons with an emulator that is stepped a frame at a time.</summary>
    public void RunToLine(int line)
    {
        Board.RunUntilLine(line);
        CopyPicture(Board.Vdp);
    }

    public void Reset() => Board.Reset();

    public uint[] Frame => frame;
    public int FrameWidth => frameWidth;
    public int FrameHeight => frameHeight;
    /// <summary>The picture fills the same width of screen in both modes (a 256-wide line is stretched to the 320 of the wide one); the double-height picture of interlace mode 2 (more than 240 rows) is shown at twice that width so that it keeps its proportions.</summary>
    public int DisplayWidth => DebugView ? DebugWidth : frameHeight > 240 ? 640 : 320;
    public int SampleRate => sampleRate;

    public int ReadSamples(short[] buffer) => Board.Sound.ReadSamples(buffer);

    public bool HasBattery => Board.Cart.HasBattery;
    public byte[] ExportSave() => (byte[])Board.Cart.Sram.Clone();
    public void ImportSave(byte[] data) { Array.Copy(data, Board.Cart.Sram, Math.Min(data.Length, Board.Cart.Sram.Length)); Board.Cart.SramImported(); }

    /// <summary>Swapping a part needs a restart: the 68000 and the VDP have one choice each, and the sound unit's state cannot move between units yet.</summary>
    public bool TrySwapCore(CoreSlot slot, string id) => slot switch
    {
        CoreSlot.Cpu => id.Equals(cpuId, StringComparison.OrdinalIgnoreCase),
        CoreSlot.Ppu => id.Equals(ppuId, StringComparison.OrdinalIgnoreCase),
        _ => id.Equals(apuId, StringComparison.OrdinalIgnoreCase),
    };

    public void Dispose() { }
}
