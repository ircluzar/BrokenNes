using System;

namespace NesEmulator.Gb;

/// <summary>
/// PPU_GBE: the Game Boy picture chip, ENHANCED - the stock chip's colour rules (4 shades, or the Game Boy Color's 5-bit colours) drawing another console's
/// frame with the cross-console layers (<see cref="ExtPicture"/>) at the guest's own size: up to 320 x 240, as many layers as the guest uses, all its sprites.
/// It is what the NES-board bridge PPU_DMGS (picture id "DMGS", "Game Boy (enhanced)") draws with, for a NES game and for every game a translator brings through
/// the NES (SNES, Master System, Game Gear, Genesis). The stock chip is <see cref="PPU_GB"/> (PPU_DMG, "Game Boy (stock)"), which shows the middle 160 x 144 of
/// the picture through the real Game Boy pipeline.
///
/// It replaced the two variants before it (2026-10-22): PPU_GBX (the dot-accurate pipeline on a bigger screen) and PPU_GBXS (that and the SNES-support layers), which
/// were copies of PPU_GB's pipeline. A guest's picture reaches the enhanced chip as layers only, so the enhanced chip is the stock chip's colours and the layer
/// compositor, not a third copy of the pipeline.
///
/// Colours. DMG: every colour becomes the shade of its own brightness (<see cref="GbColour.ShadeOf"/>: bright 0 .. dark 3), the backdrop included, shown in
/// <see cref="DmgColors"/> (the Game Boy look). CGB: the colour itself at 5 bits a channel. <see cref="InvertBackground"/> / <see cref="InvertSprites"/> (the Game
/// Boy look's inversions, set by the bridge): the background - the layers, Mode 7 and the backdrop - and the sprites apart; a DMG shade s becomes 3 - s, a CGB colour
/// has its lightness mirrored with its hue kept (<see cref="GbColour.FlipLightness"/>). The black outside a picture (the Game Gear's bezel, the SNES's clip to black)
/// never flips.
/// </summary>
public sealed class PPU_GBE
{
    public readonly GbModel Model;
    private bool Cgb => Model == GbModel.Cgb;

    /// <summary>The four DMG shades as 0xFFRRGGBB (lightest first): the Game Boy look.</summary>
    public uint[] DmgColors = { 0xFFFFFFFF, 0xFFAAAAAA, 0xFF555555, 0xFF000000 };
    public bool InvertBackground, InvertSprites;

    /// <summary>TEST ONLY (the inversion checks' negative control): the backdrop keeps its shade when the background is inverted, as the layered chip did before 2026-10-22. False in every app.</summary>
    internal static bool BackdropIgnoresInversionForTests;
    /// <summary>TEST ONLY (the negative control of the shade checks): a pixel's shade is its colour index in its palette (the backdrop 0), not its brightness - the inverted pictures the Game Boy chips showed before 2026-10-22. False in every app.</summary>
    internal static bool IndexShadesForTests;

    /// <summary>The layers a translator fills.</summary>
    public readonly ExtPicture Layers = new();
    /// <summary>0xFFRRGGBB, <see cref="Width"/> x <see cref="Height"/> (row stride Width), complete after <see cref="ComposeLayers"/>.</summary>
    public readonly uint[] FrameBuffer = new uint[ExtPicture.MaxWidth * ExtPicture.MaxLines];
    /// <summary>Per pixel: the DMG shade 0-3 (DMG), or the colour level 0-3 (CGB). Handy for tests.</summary>
    public readonly byte[] ShadeBuffer = new byte[ExtPicture.MaxWidth * ExtPicture.MaxLines];
    public int Width { get; private set; } = 256;
    public int Height { get; private set; } = 240;
    public long FrameCount { get; private set; }

    private readonly byte[] z = new byte[ExtPicture.MaxWidth], key = new byte[ExtPicture.MaxWidth];
    private readonly bool[] math = new bool[ExtPicture.MaxWidth];

    public PPU_GBE(GbModel model) { Model = model; }

    /// <summary>Draws <see cref="Layers"/> at its size into <see cref="FrameBuffer"/>.</summary>
    public void ComposeLayers()
    {
        var e = Layers;
        Width = e.Width; Height = e.Height;
        for (int line = 0; line < Height; line++)
        {
            int row = line * Width;
            if (!e.Active || !e.ComposeLine(line, 0, Width, z, key, math))
            {
                uint blank = Cgb ? 0xFFFFFFFFu : DmgColors[0];
                for (int x = 0; x < Width; x++) { FrameBuffer[row + x] = blank; ShadeBuffer[row + x] = 0; }
                continue;
            }
            for (int x = 0; x < Width; x++)
            {
                byte k = key[x];
                bool black = k == ExtPicture.KeyBlack, obj = (k & 0x60) == ExtPicture.KindObj;
                // the backdrop keys (KeyBackdrop, KeyBackdropAlt) are the background's: they flip with it; the black outside the picture never does
                bool inv = !black && (obj ? InvertSprites : InvertBackground) && !(BackdropIgnoresInversionForTests && k < ExtPicture.KindBg);
                uint rgb = black ? 0u : e.Rgb(k, line);
                if (math[x]) rgb = e.Blend(rgb, line);       // fixed-colour math: the blend's own colour (CGB) or the shade of its brightness (DMG)
                if (Cgb)
                {
                    if (inv) rgb = GbColour.FlipLightness(rgb);
                    FrameBuffer[row + x] = Cgb555(rgb);
                    ShadeBuffer[row + x] = (byte)(k >= ExtPicture.KindBg ? k & 3 : 0);
                    continue;
                }
                byte shade = black ? (byte)3 : IndexShadesForTests ? (byte)(k >= ExtPicture.KindBg ? k & 3 : 0) : GbColour.ShadeOf(rgb);
                if (inv) shade = (byte)(3 - shade);
                ShadeBuffer[row + x] = shade;
                FrameBuffer[row + x] = DmgColors[shade];
            }
        }
        FrameCount++;
    }

    /// <summary>An RGB colour as the CGB shows it (5 bits per channel).</summary>
    private static uint Cgb555(uint rgb)
    {
        int r = (int)(rgb >> 19) & 0x1F, g = (int)(rgb >> 11) & 0x1F, b = (int)(rgb >> 3) & 0x1F;
        return 0xFF000000u | (uint)((r << 3 | r >> 2) << 16 | (g << 3 | g >> 2) << 8 | (b << 3 | b >> 2));
    }
}
