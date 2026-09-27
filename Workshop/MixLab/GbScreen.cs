using System;
using NesEmulator.Gb;

namespace BrokenNes.Workshop.MixLab;

/// <summary>
/// The Game Boy picture chip as the NES-side bridges (PPU_DMG / PPU_DMGX) drive it: the stock 160x144 PPU_GB, or the
/// off-spec PPU_GBX with a bigger screen. A thin forwarder, so the stock core keeps its public fields untouched.
/// </summary>
internal abstract class GbScreen
{
    public abstract int Width { get; }
    public abstract int Height { get; }
    /// <summary>Dots in one whole frame (for the bridge's run-a-frame guard).</summary>
    public abstract int FrameDots { get; }
    public abstract byte[] Vram { get; }
    public abstract byte[] Oam { get; }
    public abstract byte[] BgPalRam { get; }
    public abstract byte[] ObjPalRam { get; }
    public abstract uint[] DmgColors { get; }
    public abstract uint[] FrameBuffer { get; }
    public abstract byte Scx { set; }
    public abstract byte Scy { set; }
    public abstract byte Lcdc { set; }
    public abstract byte Bgp { set; }
    public abstract byte Obp0 { set; }
    public abstract byte Obp1 { set; }
    public abstract long FrameCount { get; }
    public abstract void Tick(int dots);
    public abstract event Action<int>? LineStarted;
    /// <summary>The SNES-support chip behind this screen, when it is one (PPU_GBXS).</summary>
    public virtual PPU_GBXS? LayerChip => null;

    public static GbScreen Create(GbModel model, int width, int height, bool layered = false)
        => layered ? new Layered(model, width, height)
         : width == PPU_GB.Width && height == PPU_GB.Height ? new Stock(model) : new Extended(model, width, height);

    private sealed class Stock : GbScreen
    {
        private readonly PPU_GB p;
        public Stock(GbModel model) { p = new PPU_GB(model) { CompatMode = false }; p.ResetPostBoot(); }
        public override int Width => PPU_GB.Width;
        public override int Height => PPU_GB.Height;
        public override int FrameDots => PPU_GB.DotsPerLine * PPU_GB.Lines;
        public override byte[] Vram => p.Vram;
        public override byte[] Oam => p.Oam;
        public override byte[] BgPalRam => p.BgPalRam;
        public override byte[] ObjPalRam => p.ObjPalRam;
        public override uint[] DmgColors => p.DmgColors;
        public override uint[] FrameBuffer => p.FrameBuffer;
        public override byte Scx { set => p.Scx = value; }
        public override byte Scy { set => p.Scy = value; }
        public override byte Lcdc { set => p.Lcdc = value; }
        public override byte Bgp { set => p.Bgp = value; }
        public override byte Obp0 { set => p.Obp0 = value; }
        public override byte Obp1 { set => p.Obp1 = value; }
        public override long FrameCount => p.FrameCount;
        public override void Tick(int dots) => p.Tick(dots);
        public override event Action<int>? LineStarted { add => p.LineStarted += value; remove => p.LineStarted -= value; }
    }

    private sealed class Layered : GbScreen
    {
        private readonly PPU_GBXS p;
        public Layered(GbModel model, int w, int h) { p = new PPU_GBXS(model, w, h) { CompatMode = false }; p.ResetPostBoot(); }
        public override PPU_GBXS? LayerChip => p;
        public override int Width => p.Width;
        public override int Height => p.Height;
        public override int FrameDots => p.DotsPerLine * p.Lines;
        public override byte[] Vram => p.Vram;
        public override byte[] Oam => p.Oam;
        public override byte[] BgPalRam => p.BgPalRam;
        public override byte[] ObjPalRam => p.ObjPalRam;
        public override uint[] DmgColors => p.DmgColors;
        public override uint[] FrameBuffer => p.FrameBuffer;
        public override byte Scx { set => p.Scx = value; }
        public override byte Scy { set => p.Scy = value; }
        public override byte Lcdc { set => p.Lcdc = value; }
        public override byte Bgp { set => p.Bgp = value; }
        public override byte Obp0 { set => p.Obp0 = value; }
        public override byte Obp1 { set => p.Obp1 = value; }
        public override long FrameCount => p.FrameCount;
        public override void Tick(int dots) => p.Tick(dots);
        public override event Action<int>? LineStarted { add => p.LineStarted += value; remove => p.LineStarted -= value; }
    }

    private sealed class Extended : GbScreen
    {
        private readonly PPU_GBX p;
        public Extended(GbModel model, int w, int h) { p = new PPU_GBX(model, w, h) { CompatMode = false }; p.ResetPostBoot(); }
        public override int Width => p.Width;
        public override int Height => p.Height;
        public override int FrameDots => p.DotsPerLine * p.Lines;
        public override byte[] Vram => p.Vram;
        public override byte[] Oam => p.Oam;
        public override byte[] BgPalRam => p.BgPalRam;
        public override byte[] ObjPalRam => p.ObjPalRam;
        public override uint[] DmgColors => p.DmgColors;
        public override uint[] FrameBuffer => p.FrameBuffer;
        public override byte Scx { set => p.Scx = value; }
        public override byte Scy { set => p.Scy = value; }
        public override byte Lcdc { set => p.Lcdc = value; }
        public override byte Bgp { set => p.Bgp = value; }
        public override byte Obp0 { set => p.Obp0 = value; }
        public override byte Obp1 { set => p.Obp1 = value; }
        public override long FrameCount => p.FrameCount;
        public override void Tick(int dots) => p.Tick(dots);
        public override event Action<int>? LineStarted { add => p.LineStarted += value; remove => p.LineStarted -= value; }
    }
}
