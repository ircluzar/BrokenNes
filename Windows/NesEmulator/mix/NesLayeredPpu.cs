using System;
using NesEmulator.Mix;

namespace NesEmulator;

/// <summary>
/// A NES game on another console's ENHANCED picture chip (the NES board's PPU ids <c>SNESS</c>, <c>SMSS</c>, <c>MDS</c>: <see cref="PPU_SNESS"/> and its siblings). A FIX-timed NES PPU stays in front and owns
/// everything a NES program can see (<see cref="NesFrontedPpu"/>: registers, timing, NMI, the sprite-0 hit); each frame the captured picture - every line's scroll and mask, each 8-line group's pattern tables,
/// the nametables, the palette and OAM - becomes the cross-console layers (<see cref="NesToLayers"/>: one background layer over the whole 2 x 2 nametable map and all 64 sprites with the NES's 8 a line) and the
/// enhanced chip draws them at the NES's own 256 x 240 in its own colours. A stock chip of the same console (PPU_SNES, PPU_SMS, PPU_MD) replays the picture through its own pipeline and keeps that console's limits
/// (the SNES's 239 lines, the Master System's 256 tiles, ...); the enhanced one keeps the NES's. The chip is <see cref="IEnhancedPictureChip"/> itself too (through <see cref="Chip"/>), so a translator on a NES board
/// can reach it the way it reaches FIX enhanced.
/// </summary>
public abstract class NesLayeredPpu : NesFrontedPpu, IEnhancedPictureChip
{
    private readonly IEnhancedPictureChip chip;
    private readonly NesToLayers nes = new();
    private readonly byte[][] rowChr = new byte[30][];

    protected NesLayeredPpu(Bus bus, IEnhancedPictureChip chip) : base(bus) { this.chip = chip; }

    /// <summary>The enhanced chip itself (tests).</summary>
    public IEnhancedPictureChip Chip => chip;
    /// <summary>Background tiles past the layer's 1023 in the last frame.</summary>
    public int TileOverflow => nes.TileOverflow;

    public ExtPicture Layers => chip.Layers;
    public void DrawLayers() => chip.DrawLayers();
    public uint[] Picture => chip.Picture;
    public int PictureWidth => chip.PictureWidth;
    public int PictureHeight => chip.PictureHeight;
    public string EnhancedName => chip.EnhancedName;
    public string ColourLimit => chip.ColourLimit;

    protected override void Present()
    {
        GroupChr(rowChr);
        nes.Fill(chip.Layers, Nt, Pal, rowChr, ChrAtLine(120).Data, FrameOam, LineCtrl[0], LineX, LineY, LineMask);
        chip.DrawLayers();
        var src = chip.Picture; int w = chip.PictureWidth, h = chip.PictureHeight;
        for (int y = 0; y < 240; y++)
            for (int x = 0; x < 256; x++) StoreRgb(x, y, x < w && y < h ? src[y * w + x] : 0xFF000000u);
        if (nes.TileOverflow > 0) Counters.Add("background tiles past the layer's 1023", nes.TileOverflow);
    }
}
