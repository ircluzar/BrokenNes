using System;
using System.Reflection;
using NesEmulator;

namespace NesEmulator.Mix;

/// <summary>Everything a NES PPU can hold for one still picture: two pattern tables, two nametables (vertical
/// mirroring, 512x240 of map), 32 palette bytes, 64 sprites, fine scroll and the blank flag.</summary>
internal sealed class NesPicture
{
    public byte[] ChrBg = new byte[4096], ChrObj = new byte[4096], Nt0 = new byte[1024], Nt1 = new byte[1024], Pal = new byte[32], Oam = new byte[256];
    public int FineX, FineY;
    public bool Blank;
    /// <summary>Show sprites from pattern table 1 (the default) - off keeps the picture BG-only.</summary>
    public bool Sprites = true;
    /// <summary>TESTS: PPUMASK as written (null, the default: $1E with sprites, $0E without, both shown in the left column as always).</summary>
    public byte? Mask;

    /// <summary>Back to what a new picture is: every table zero, no fine scroll, rendering on, sprites shown. A bridge keeps one picture and clears it for each frame instead of making a new one.</summary>
    public void Clear()
    {
        Array.Clear(ChrBg); Array.Clear(ChrObj); Array.Clear(Nt0); Array.Clear(Nt1); Array.Clear(Pal); Array.Clear(Oam);
        FineX = 0; FineY = 0; Blank = false; Sprites = true; Mask = null;
    }

    /// <summary>The picture of a frame that shows nothing (the backdrop only): rendering off, every palette entry colour $0F, every sprite off the screen.</summary>
    public void ClearToBlank()
    {
        Clear();
        Array.Fill(Pal, (byte)0x0F); Array.Fill(Oam, (byte)0xFF);
        Blank = true;
    }
}

/// <summary>
/// The picture a cross-console translator made, as a session shows it: 0xFFRRGGBB, its size, and whether it is the guest's own size (an enhanced chip drew the
/// guest's layers: <see cref="IEnhancedPictureChip"/>) or the NES's 256 x 240 (a stock chip drew a NES picture). One per translator, reused: no allocation.
/// </summary>
internal sealed class BridgeFrame
{
    public readonly uint[] Argb = new uint[ExtPicture.MaxWidth * ExtPicture.MaxLines];
    public int Width = 256, Height = 240;
    /// <summary>True when an enhanced chip drew the guest's own picture size; false for a NES chip's 256 x 240.</summary>
    public bool GuestSize;

    /// <summary>A NES chip's 256 x 240 RGBA picture.</summary>
    public BridgeFrame FromRgba(byte[] rgba)
    {
        Width = 256; Height = 240; GuestSize = false;
        int n = Math.Min(256 * 240, rgba.Length / 4);
        for (int i = 0; i < n; i++) { int o = i * 4; Argb[i] = 0xFF000000u | (uint)rgba[o] << 16 | (uint)rgba[o + 1] << 8 | rgba[o + 2]; }
        return this;
    }

    /// <summary>What an enhanced chip just drew, at its size.</summary>
    public BridgeFrame From(IEnhancedPictureChip chip)
    {
        Width = chip.PictureWidth; Height = chip.PictureHeight; GuestSize = true;
        Array.Copy(chip.Picture, Argb, Width * Height);
        return this;
    }

    /// <summary>The picture as RGBA bytes (tools that save it).</summary>
    public byte[] ToRgba()
    {
        var rgba = new byte[Width * Height * 4];
        for (int i = 0; i < Width * Height; i++) { uint c = Argb[i]; rgba[i * 4] = (byte)(c >> 16); rgba[i * 4 + 1] = (byte)(c >> 8); rgba[i * 4 + 2] = (byte)c; rgba[i * 4 + 3] = 255; }
        return rgba;
    }
}

/// <summary>
/// Where a cross-console translator sends its picture (the shared step of the enhanced roster, docs/projects/enhanced-cores.md 5.3): a chip that takes the guest's layers
/// (<see cref="Enhanced"/>: the layered path at the guest's size), or a NES picture chip that draws a flat NES picture (<see cref="Present"/>). Two routes implement it:
/// <see cref="NesPictureSink"/> (a NES board's PPU, any NES PPU core by id: stock chips draw the flat picture, FIX enhanced and Game Boy enhanced take the layers) and
/// <see cref="ChipPictureSink"/> (an enhanced picture chip of another console reached directly, with no NES in between: the SNES, Master System / Game Gear and Genesis enhanced chips).
/// A translator never knows which of the two it has.
/// </summary>
internal interface IPictureSink
{
    /// <summary>What the route is called in the menus and the counters ("FIXS", "SNES:SFCS").</summary>
    string PpuId { get; }
    /// <summary>The chip that takes the guest's layers (<see cref="IEnhancedPictureChip"/>), or null for a stock NES chip (the flat path).</summary>
    IEnhancedPictureChip? Enhanced { get; }
    /// <summary>The flat path: a NES picture drawn by a NES chip, RGBA 256 x 240. Only a route without <see cref="Enhanced"/> is asked.</summary>
    byte[] Present(NesPicture p);
}

/// <summary>
/// An enhanced picture chip reached directly (not through a NES board): the guest's translator fills the chip's layers and the chip draws them at the guest's size in its own colours.
/// There is no flat path: a chip that takes the layers is never handed a NES picture, so <see cref="Present"/> refuses (never a quiet substitute).
/// </summary>
internal sealed class ChipPictureSink : IPictureSink
{
    public ChipPictureSink(IEnhancedPictureChip chip, string id) { Enhanced = chip ?? throw new ArgumentNullException(nameof(chip)); PpuId = id; }
    public string PpuId { get; }
    public IEnhancedPictureChip? Enhanced { get; }
    public byte[] Present(NesPicture p) => throw new InvalidOperationException($"{PpuId} is an enhanced picture chip: it draws the guest's layers, never a flat NES picture");
}

/// <summary>
/// A real NES (idle NROM program) whose PPU - any NES PPU core by id - draws a <see cref="NesPicture"/>.
/// Shared by every "downgrade to NES" bridge (SNES -> NES, Game Boy -> NES) so each NES PPU core joins them all.
/// </summary>
internal sealed class NesPictureSink : IPictureSink
{
    private readonly NES nes;
    private readonly Bus bus;
    private readonly bool backdoor;
    public string PpuId { get; }

    /// <summary>How the sink's NES passes the frames a picture takes. 2 (default): the PPU alone is stepped from the vertical blank through one whole picture and back to the blank (<see cref="NES.RunIdleToVblank"/>): one
    /// frame of PPU work. 1: two frames of the PPU alone, the first only latching the new state (<see cref="NES.RunIdleFrame"/>). 0: two frames of the full NES loop with its idle CPU, the original path. The output is the same in
    /// all three (the speed suite's goldens, <c>--ab idle-frame</c>).</summary>
    public static int IdleMode = 2;

    public NesPictureSink(string nesPpuId)
    {
        PpuId = nesPpuId;
        backdoor = Environment.GetEnvironmentVariable("MIX_BACKDOOR") != "0";      // read once: reading it for every frame made a string for every frame
        nes = new NES { RomName = "mixlab-idle.nes" };
        nes.LoadROM(SnesToNes.IdleRom());
        if (!nes.SetPpuCore(nesPpuId) || !string.Equals(nes.GetPpuCoreId(), "PPU_" + nesPpuId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"NES PPU '{nesPpuId}' not applied (got {nes.GetPpuCoreId()})");
        bus = (Bus)typeof(NES).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(nes)!;
    }

    /// <summary>The NES PPU core drawing the picture (for chips with extensions, e.g. PPU_FIXS layers).</summary>
    public IPPU Ppu => bus.ppu;

    /// <summary>The picture chip when it is an enhanced one (FIX enhanced, Game Boy enhanced): a translator then fills its layers at the guest's size instead of a NES picture.</summary>
    public IEnhancedPictureChip? Enhanced => bus.ppu as IEnhancedPictureChip;

    private void W(ushort a, byte v) => bus.Write(a, v);

    /// <summary>Program the NES PPU (rendering off while uploading), run two frames, return RGBA 256x240.</summary>
    public byte[] Present(NesPicture p)
    {
        if (IdleMode == 2) nes.AlignIdleToVblank();   // the registers are written in the vertical blank, so the picture drawn next is a whole one
        // $3F10/$14/$18/$1C mirror $3F00/$04/$08/$0C: keep them equal or the last write (a sprite palette's unused
        // entry 0) replaces the universal background colour.
        for (int i = 0; i < 16; i += 4) p.Pal[16 + i] = p.Pal[i];
        W(0x2001, 0x00); W(0x2000, 0x08);
        _ = bus.Read(0x2002);
        // Instant register writes are fine for cores without a $2006 -> v delay, but PPU_FIX models the real 4-dot
        // delay: with no time passing between writes, every $2007 byte lands at the old address. Where the core has
        // a PPU-bus backdoor (IPpuProbe) and MIX_BACKDOOR is not 0, upload through it instead.
        if (bus.ppu is IPpuProbe pb && backdoor)
        {
            for (int i = 0; i < 4096; i++) pb.ProbePpuBusWrite((ushort)i, p.ChrBg[i]);
            for (int i = 0; i < 4096; i++) pb.ProbePpuBusWrite((ushort)(0x1000 + i), p.ChrObj[i]);
            for (int i = 0; i < 1024; i++) { pb.ProbePpuBusWrite((ushort)(0x2000 + i), p.Nt0[i]); pb.ProbePpuBusWrite((ushort)(0x2400 + i), p.Nt1[i]); }
            for (int i = 0; i < 32; i++) pb.ProbePpuBusWrite((ushort)(0x3F00 + i), p.Pal[i]);
        }
        else
        {
            W(0x2006, 0x00); W(0x2006, 0x00);
            foreach (var b in p.ChrBg) W(0x2007, b);
            foreach (var b in p.ChrObj) W(0x2007, b);
            W(0x2006, 0x20); W(0x2006, 0x00);
            foreach (var b in p.Nt0) W(0x2007, b);
            foreach (var b in p.Nt1) W(0x2007, b);
            W(0x2006, 0x3F); W(0x2006, 0x00);
            foreach (var b in p.Pal) W(0x2007, b);
        }
        W(0x2003, 0x00);
        foreach (var b in p.Oam) W(0x2004, b);
        _ = bus.Read(0x2002);
        W(0x2000, 0x08); W(0x2005, (byte)p.FineX); W(0x2005, (byte)p.FineY);
        W(0x2001, p.Blank ? (byte)0x00 : p.Mask ?? (byte)(p.Sprites ? 0x1E : 0x0E));
        if (IdleMode == 2) nes.RunIdleToVblank();
        else if (IdleMode == 1) { nes.RunIdleFrame(); nes.RunIdleFrame(); }   // one frame to latch the new picture, one to present it
        else { nes.RunFrame(); nes.RunFrame(); }
        return nes.GetFrameBuffer();
    }
}
