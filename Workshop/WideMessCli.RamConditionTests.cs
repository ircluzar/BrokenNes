using System;
using System.IO;
using System.Linq;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>Scene conditions on work RAM (spec 6.1, the climb's part 2): E2g on a synthetic scene, E8 on A Link to the Past's indoors flag; each beside its negative control.</summary>
internal static partial class WideMessCli
{
    /// <summary>E2g: a mask whose condition is a byte of work RAM, on a raw picture chip: BG1 extended over a 64-tile map, a fill mask when work RAM $7E:0010 is 1.</summary>
    private static void SyntheticRamCondition()
    {
        var p = new PPU_WMS();
        p.Reset();
        for (int w = 16; w < 24; w++) p.Vram[w] = 0x00FF;   // tile 1: colour 1
        p.Cgram[1] = 0x7FFF;
        for (int a = 0x1000; a < 0x1800; a++) p.Vram[a] = 1;
        p.WriteRegister(0x05, 0x01); p.WriteRegister(0x07, 0x11); p.WriteRegister(0x2C, 0x01); p.WriteRegister(0x00, 0x0F);
        var rules = WmsWidescreen.AllExtend();
        rules.Masks = new[] { new WmsMask { Layers = 0x01, Top = 0, Bottom = 223, Rule = WmsSideRule.Fill, From = 0, To = 72, Left = true, Right = true,
            When = new[] { new WmsCondition(WmsRegister.Ram, 0xFF, 0x01, 0x0010) } } };
        var ram = new byte[0x20000];
        int Side(uint[] f) { int n = 0; for (int r = 0; r < 224; r++) for (int c = 0; c < 400; c++) { if (c == 72) c = 328; if (f[r * 400 + c] != 0xFF000000u) n++; } return n; }
        p.SetEnhancements(rules, false);
        p.WorkRam = ram;
        ram[0x10] = 0; int off = Side(DrawFrame(p, out _));
        ram[0x10] = 1; int on = Side(DrawFrame(p, out _));
        ram[0x10] = 3; int other = Side(DrawFrame(p, out _));
        bool unchanged = ram.Take(0x20).All(b => b == 0 || b == 3) && ram[0x10] == 3;   // the picture chip only reads
        p.WorkRam = null; ram[0x10] = 1; int none = Side(DrawFrame(p, out _));
        Check(off == 2 * 224 * 72 && on == 0 && other == 2 * 224 * 72 && unchanged,
            "a scene condition on work RAM: the mask applies while the byte is 1 (the sides filled) and not while it is 0 or 3 (extended); work RAM is only read",
            $"sides drawn: byte 0 {off:N0}, 1 {on:N0}, 3 {other:N0} pixels");
        Check(none == 2 * 224 * 72, "control: without work RAM (no console) a work RAM condition never holds (the mask stays off)", $"{none:N0} side pixels");
        p.SetEnhancements(null, false);
    }

    /// <summary>E8: A Link to the Past's indoors mask (the game's own flag, work RAM $7E:001B). On every indoor frame the room's layers draw nothing in the sides; without the mask (the
    /// negative control) they show the map beside the room; on every outdoor frame the picture is the same as without the mask (every layer extended).</summary>
    private static void IndoorMaskCheck(EnhanceGame g, string path, string profiles)
    {
        byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(path), path, out _);
        var pad = ParsePad(g.Script);
        int frames = g.Moments.Max();
        var controls = new (long drawn, int frames)[2];
        foreach (int layer in new[] { 0, 1 })
        {
            using var a = Session(ConsoleKind.WideMess, rom);
            using var b = Session(ConsoleKind.WideMess, rom);
            var prof = a.EnhanceProfile.Profile;
            if (prof?.Widescreen is not { } ws || !ws.Masks.Any(m => m.When.Any(c => c.Register == WmsRegister.Ram))) { Check(false, $"{g.Key}: its profile has an indoors mask on work RAM", a.EnhanceProfile.Describe()); return; }
            var ws1 = ws.Copy(); ws1.Masks = ws.Masks.Where(m => !m.When.Any(c => c.Register == WmsRegister.Ram)).ToArray();
            b.SetScratchProfile(new WmsProfile { Source = prof.Source + " without its indoors mask", Title = prof.Title, Sha1 = prof.Sha1, Widescreen = ws1, Verdicts = prof.Verdicts, VerifiedHow = "scratch" });
            a.SetEnhancements(widescreen: true); b.SetEnhancements(widescreen: true);
            a.WideMessPpu!.DebugCaptureLayer = layer; b.WideMessPpu!.DebugCaptureLayer = layer;
            int indoor = 0, outdoor = 0, indoorDrawn = 0, outdoorDiffer = 0; long controlDrawn = 0;
            PadButtons held = 0; int atA = 0, atB = 0; PadButtons heldB = 0;
            for (int f = 1; f <= frames; f++)
            {
                PlayTo(a, pad, ref atA, f, ref held); PlayTo(b, pad, ref atB, f, ref heldB);
                if (!a.WideMessPpu.FrameIsWide) continue;
                bool inside = a.Board.Wram[0x1B] == 1;
                var ca = a.WideMessPpu.DebugCapture; var cb = b.WideMessPpu.DebugCapture;
                if (ca == null || cb == null) continue;
                int h = a.WideMessPpu.VisibleHeight;
                long sa = 0, sb = 0;
                for (int r = 0; r < h; r++)
                    for (int c = 0; c < PPU_WMS.WideWidth; c++)
                    {
                        if (c == PPU_WMS.SnesOrigin) c = PPU_WMS.SnesOrigin + 256;
                        if (ca[r * PPU_WMS.WideWidth + c] != PPU_WMS.CaptureClear) sa++;
                        if (cb[r * PPU_WMS.WideWidth + c] != PPU_WMS.CaptureClear) sb++;
                    }
                if (inside) { indoor++; if (sa > 0) indoorDrawn++; controlDrawn += sb; }
                else
                {
                    outdoor++;
                    if (!a.WideMessPpu.GetWideFrame().AsSpan(0, PPU_WMS.WideWidth * h).SequenceEqual(b.WideMessPpu.GetWideFrame().AsSpan(0, PPU_WMS.WideWidth * h))) outdoorDiffer++;
                }
            }
            string bg = $"BG{layer + 1}";
            Check(indoor >= 500 && indoorDrawn == 0, $"{g.Key}: indoors ($7E:001B = 1), {bg} draws nothing in the sides on every indoor frame (the room's map holds other rooms beside it)",
                $"{indoor:N0} indoor frames, {indoorDrawn:N0} with side pixels");
            controls[layer] = (controlDrawn, indoor);
            if (layer == 0)
                Check(outdoor >= 500 && outdoorDiffer == 0, $"{g.Key}: outdoors ($7E:001B = 0) the picture is the same with and without the indoors mask (every layer extended)", $"{outdoor:N0} outdoor frames, {outdoorDiffer:N0} differ");
        }
        // the negative control: without the mask the room's layers show the map beside the room (in Link's house BG2, the floor and walls; BG1 is empty beside this room)
        Check(controls[0].drawn + controls[1].drawn > controls[1].frames * 1000L, $"{g.Key}: negative control, the same without the indoors mask: the room's layers show the map beside the room indoors",
            $"BG1 {controls[0].drawn:N0}, BG2 {controls[1].drawn:N0} side pixels over {controls[1].frames:N0} indoor frames");
    }
}
