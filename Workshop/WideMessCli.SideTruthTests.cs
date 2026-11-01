using System;
using NesEmulator.Snes;

namespace BrokenNes.Workshop;

/// <summary>E2e: <c>--wms-side-truth</c>'s judge (spec 6.3) on a synthetic game, each check beside its negative control: palette animation is never wrong, a real change of the map still is.</summary>
internal static partial class WideMessCli
{
    /// <summary>One run of the synthetic palette game: BG1 on a 64-tile map (512 pixels) holding the whole level, stripes of white (colour 1) and green (colour 3) tiles 64 pixels wide,
    /// so every layer extended shows the level truly in the sides. Each frame the layer scrolls 2 pixels right; with <paramref name="fade"/> CGRAM entry 1 changes every frame (a fade,
    /// as Super Metroid's spores); with <paramref name="changeAt"/> the game, on that frame, turns the map columns 8-72 pixels past the right edge into red tiles (colour 2) before
    /// they scroll in: what the sides showed there before is then really wrong. The judge is the tool's own (<see cref="SideJudge"/>), on palette entries or on colours.</summary>
    private static (long right, long empty, long wrong) PaletteScene(bool fade, int changeAt, bool indices, int frames = 240)
    {
        var p = new PPU_WMS();
        p.Reset();
        for (int w = 16; w < 24; w++) p.Vram[w] = 0x00FF;   // tile 1: colour 1
        for (int w = 32; w < 40; w++) p.Vram[w] = 0xFF00;   // tile 2: colour 2
        for (int w = 48; w < 56; w++) p.Vram[w] = 0xFFFF;   // tile 3: colour 3
        p.Cgram[0] = 0; p.Cgram[1] = 0x7FFF; p.Cgram[2] = 0x001F; p.Cgram[3] = 0x03E0;
        for (int col = 0; col < 64; col++)
            for (int row = 0; row < 32; row++) p.Vram[0x1000 + (col & 31) + ((col & 32) != 0 ? 0x400 : 0) + row * 32] = (ushort)((col / 8) % 2 == 0 ? 1 : 3);
        p.WriteRegister(0x05, 0x01);   // BGMODE 1
        p.WriteRegister(0x07, 0x11);   // BG1 map at word $1000, 64 tiles wide
        p.WriteRegister(0x0B, 0x00);
        p.WriteRegister(0x2C, 0x01);   // TM: BG1
        p.WriteRegister(0x00, 0x0F);
        p.SetEnhancements(WmsWidescreen.AllExtend(), false);
        p.CompareEnhanced = true;   // as the tool: the SNES chip draws each line too, which fills DebugLineRegisters
        p.DebugLineRegisters = new PPU_SFC.RegisterSnapshot[PPU_SFC.MaxHeight + 2];
        p.DebugCaptureLayer = 0; p.DebugCaptureIndices = indices;
        var judge = new SideJudge(0, 8);
        int x = 0;
        for (int f = 1; f <= frames; f++)
        {
            x += 2;
            if (fade) { p.Cgram[1] = (ushort)(0x7FFF - (f % 31) * 0x0421); p.Cgram[3] = (ushort)((31 - f % 31) << 5); }   // both stripes fading, a step every frame
            if (f == changeAt)
                for (int px = x + 256 + 8; px < x + 256 + 72; px += 8)
                {
                    int col = (px >> 3) & 63;
                    for (int row = 0; row < 32; row++) p.Vram[0x1000 + (col & 31) + ((col & 32) != 0 ? 0x400 : 0) + row * 32] = 2;
                }
            p.WriteRegister(0x0D, (byte)(x & 0xFF)); p.WriteRegister(0x0D, (byte)((x >> 8) & 3));
            DrawFrame(p, out _);
            judge.Frame(f, p.DebugLineRegisters, 224, p.FrameIsWide ? PPU_WMS.WideWidth : 256, p.DebugCapture);
        }
        return (judge.Right, judge.Empty, judge.Wrong);
    }

    private static void SyntheticSideTruth()
    {
        var a = PaletteScene(fade: true, changeAt: -1, indices: true);
        var b = PaletteScene(fade: true, changeAt: -1, indices: false);
        Check(a.right >= 1000 && a.wrong == 0 && a.empty == 0, "side truth on palette entries: a layer whose palette fades every frame while it scrolls, every layer extended over a map that holds the whole level: 0 wrong",
            $"{a.right:N0} right, {a.empty:N0} empty, {a.wrong:N0} wrong");
        Check(b.right + b.empty + b.wrong >= 1000 && b.wrong * 2 >= b.right + b.empty + b.wrong, "negative control, the same game judged on colours (--compare colour): the fade counts as wrong (at least half)", $"{b.right:N0} right, {b.wrong:N0} wrong");
        var c = PaletteScene(fade: false, changeAt: -1, indices: false);
        Check(c.wrong == 0 && c.right >= 1000, "control: without the fade the colours agree too (the extended sides are the level)", $"{c.right:N0} right, {c.wrong:N0} wrong");
        var d = PaletteScene(fade: true, changeAt: 120, indices: true);
        Check(d.wrong >= 64, "a real change of the map (the columns ahead turned red before they scroll in), with the fade on: palette entries still count it wrong",
            $"{d.right:N0} right, {d.wrong:N0} wrong (64 columns changed)");
    }
}
