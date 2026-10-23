using System;
using NesEmulator.Gb;

namespace NesEmulator.Mix;

/// <summary>
/// The Game Boy picture chip as the NES-side bridges drive it: the stock 160x144 <see cref="PPU_GB"/> (PPU_DMG, Game Boy stock), or the
/// enhanced <see cref="PPU_GBE"/> with a screen of the guest's size and the cross-console layers (PPU_DMGS, Game Boy enhanced). A thin forwarder,
/// so the stock core keeps its public fields untouched.
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
    /// <summary>The enhanced chip behind this screen, when it is one (its cross-console layers).</summary>
    public virtual PPU_GBE? Enhanced => null;

    /// <summary>The stock chip at 160x144, or the enhanced chip at <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static GbScreen Create(GbModel model, int width, int height, bool enhanced)
        => enhanced ? new EnhancedScreen(model, width, height) : new Stock(model);

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

    private sealed class EnhancedScreen : GbScreen
    {
        private readonly PPU_GBE p;
        public EnhancedScreen(GbModel model, int w, int h) { p = new PPU_GBE(model, w, h) { CompatMode = false }; p.ResetPostBoot(); }
        public override PPU_GBE? Enhanced => p;
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
