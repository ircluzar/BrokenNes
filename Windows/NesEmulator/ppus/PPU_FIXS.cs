using System;

namespace NesEmulator
{
// FIX ENHANCED (PPU id "FIXS"): PPU_FIX itself - the same dot-accurate NES PPU, the same code (a subclass, not a fork: every accuracy fix to FIX is
// FIX enhanced's too) - plus the enhanced picture contract (IEnhancedPictureChip): another console's frame, translated into an ExtPicture
// (SNES, Game Boy, Master System, Game Gear, Genesis), is drawn at the guest's own size with as many layers as the guest uses, in the 2C02's
// colours. For a NES game the chip is PPU_FIX: nothing fills the layers.
//
// Until 2026-10-22 FIXS was a copy of PPU_FIX (2026-09-27) that composed a SNES-sized (256 x 224) extension into its NES picture dot by dot, so a
// Genesis picture lost 32 columns each side and 240-line pictures were cut to 224. The layers are now drawn apart from the NES picture, at the guest's size.
public class PPU_FIXS : PPU_FIX, IPPU, IEnhancedPictureChip
{
	public new string CoreName => "FixS";
	public new string Description => "FIX enhanced: PPU_FIX plus the cross-console layers - another console's game drawn at its own size (up to 320 x 240) with all its layers and sprites, in the 2C02's colours. A NES game is drawn exactly as on PPU_FIX.";
	public new int Performance => 0;
	public new int Rating => 3;
	public new string Category => "Experimental";

	public PPU_FIXS(Bus bus) : base(bus) { }

	// =========================================================================================== the enhanced picture
	private readonly ExtPicture layers = new();
	private readonly uint[] picture = new uint[ExtPicture.MaxWidth * ExtPicture.MaxLines];
	private int pictureWidth = 256, pictureHeight = 224;
	private readonly byte[] z = new byte[ExtPicture.MaxWidth], key = new byte[ExtPicture.MaxWidth];
	private readonly bool[] math = new bool[ExtPicture.MaxWidth];
	/// <summary>NES colour of every key 0x20-0x7F, re-derived when the layers' palettes change.</summary>
	private readonly byte[] keyNes = new byte[128];
	private int keyNesVersion = -1;
	/// <summary>Blended colours (fixed-colour math) as NES colours, by RGB.</summary>
	private readonly System.Collections.Generic.Dictionary<uint, byte> blendNes = new();

	public ExtPicture Layers => layers;
	public uint[] Picture => picture;
	public int PictureWidth => pictureWidth;
	public int PictureHeight => pictureHeight;
	public string EnhancedName => "FIX enhanced";
	public string ColourLimit => "the 2C02's colours";

	/// <summary>Pieces the layers' per-line sprite limit dropped in the last drawn frame.</summary>
	public int ExtSpriteLineDrops => layers.SpriteLineDrops;

	private static byte Nes(uint rgb) => NesEmulator.Mix.NesPalette.Nearest((int)(rgb >> 16) & 0xFF, (int)(rgb >> 8) & 0xFF, (int)rgb & 0xFF);
	private static uint Argb(byte nes) => 0xFF000000u | (uint)NesEmulator.Mix.NesFixPalette.Rgb(nes);

	/// <summary>
	/// Draws the layers at their size: each line composed (layers, Mode 7, sprites on the ladder), every pixel the NES colour nearest its colour (the translators hand
	/// over the textbook colour of the index they chose, so it lands on exactly that index), shown in PPU_FIX's own palette. Fixed-colour math is blended, then matched.
	/// </summary>
	public void DrawLayers()
	{
		var e = layers;
		int w = e.Width, h = e.Height;
		pictureWidth = w; pictureHeight = h;
		if (!e.Active) { Array.Fill(picture, 0xFF000000u, 0, w * h); return; }
		if (keyNesVersion != e.PaletteVersion)
		{
			for (int k = 0x20; k < 0x80; k++) keyNes[k] = Nes(e.Rgb((byte)k, 0));
			keyNesVersion = e.PaletteVersion;
		}
		for (int line = 0; line < h; line++)
		{
			int row = line * w;
			if (!e.ComposeLine(line, 0, w, z, key, math)) { Array.Fill(picture, 0xFF000000u, row, w); continue; }
			uint back = Argb(Nes(e.BackdropRgb[line])), backAlt = Argb(Nes(e.BackdropAltRgb[line])), black = Argb(0x0F);
			for (int x = 0; x < w; x++)
			{
				byte k = key[x];
				uint c = k switch { ExtPicture.KeyBackdrop => back, ExtPicture.KeyBackdropAlt => backAlt, ExtPicture.KeyBlack => black, _ => Argb(keyNes[k & 0x7F]) };
				if (math[x])
				{
					uint rgb = e.Blend(e.Rgb(k, line), line);
					if (!blendNes.TryGetValue(rgb, out byte n)) { n = Nes(rgb); if (blendNes.Count < 65536) blendNes[rgb] = n; }
					c = Argb(n);
				}
				picture[row + x] = c;
			}
		}
	}
}
}
