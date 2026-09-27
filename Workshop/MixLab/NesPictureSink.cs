using System;
using System.Reflection;
using NesEmulator;

namespace BrokenNes.Workshop.MixLab;

/// <summary>Everything a NES PPU can hold for one still picture: two pattern tables, two nametables (vertical
/// mirroring, 512x240 of map), 32 palette bytes, 64 sprites, fine scroll and the blank flag.</summary>
internal sealed class NesPicture
{
    public byte[] ChrBg = new byte[4096], ChrObj = new byte[4096], Nt0 = new byte[1024], Nt1 = new byte[1024], Pal = new byte[32], Oam = new byte[256];
    public int FineX, FineY;
    public bool Blank;
    /// <summary>Show sprites from pattern table 1 (the default) - off keeps the picture BG-only.</summary>
    public bool Sprites = true;
}

/// <summary>
/// A real NES (idle NROM program) whose PPU - any NES PPU core by id - draws a <see cref="NesPicture"/>.
/// Shared by every "downgrade to NES" bridge (SNES -> NES, Game Boy -> NES) so each NES PPU core joins them all.
/// </summary>
internal sealed class NesPictureSink
{
    private readonly NES nes;
    private readonly Bus bus;
    public string PpuId { get; }

    public NesPictureSink(string nesPpuId)
    {
        PpuId = nesPpuId;
        nes = new NES { RomName = "mixlab-idle.nes" };
        nes.LoadROM(SnesToNes.IdleRom());
        if (!nes.SetPpuCore(nesPpuId) || !string.Equals(nes.GetPpuCoreId(), "PPU_" + nesPpuId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"NES PPU '{nesPpuId}' not applied (got {nes.GetPpuCoreId()})");
        bus = (Bus)typeof(NES).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(nes)!;
    }

    private void W(ushort a, byte v) => bus.Write(a, v);

    /// <summary>Program the NES PPU (rendering off while uploading), run two frames, return RGBA 256x240.</summary>
    public byte[] Present(NesPicture p)
    {
        // $3F10/$14/$18/$1C mirror $3F00/$04/$08/$0C: keep them equal or the last write (a sprite palette's unused
        // entry 0) replaces the universal background colour.
        for (int i = 0; i < 16; i += 4) p.Pal[16 + i] = p.Pal[i];
        W(0x2001, 0x00); W(0x2000, 0x08);
        _ = bus.Read(0x2002);
        // Instant register writes are fine for cores without a $2006 -> v delay, but PPU_FIX models the real 4-dot
        // delay: with no time passing between writes, every $2007 byte lands at the old address. Where the core has
        // a PPU-bus backdoor (IPpuProbe) and MIX_BACKDOOR is not 0, upload through it instead.
        if (bus.ppu is IPpuProbe pb && Environment.GetEnvironmentVariable("MIX_BACKDOOR") != "0")
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
        W(0x2001, p.Blank ? (byte)0x00 : (byte)(p.Sprites ? 0x1E : 0x0E));
        nes.RunFrame(); nes.RunFrame();   // one frame to latch the new picture, one to present it
        return nes.GetFrameBuffer();
    }
}
