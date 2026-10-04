using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-render &lt;dir&gt; [--png out.png] [--hash]</c> or <c>--md-render --bstate file.state [--png out.png]</c>: loads a VDP's memories from <c>vram.bin</c> (64 KB), <c>cram.bin</c> (64 big-endian words), <c>vsram.bin</c> (up to 64 words) and <c>regs.bin</c>
/// (the registers 0-23) in <c>dir</c> (what <c>--md-run --vdp-out</c> writes, or what a converter makes from another emulator's saved state), runs the VDP alone for one frame
/// without any processor and writes the picture the renderer draws for that static state. Used to render a state the oracle was in and compare the two pictures. <c>--bstate</c> reads the memories out of a quick-save
/// file of BlastEm 1.0.0 (its native format; the offsets were found by comparing a state file with this emulator's own memories: VRAM at $AE, colour RAM at $100AE, VSRAM at $1012E, the VDP registers at $102EE).
/// </summary>
internal static class MdRenderCli
{
    public static int Run(string[] args)
    {
        string? dir = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        string? bstate = M68kTestCli.Arg(args, "--bstate");
        if (dir == null && bstate == null) { Console.WriteLine("usage: --md-render <dir with vram.bin cram.bin vsram.bin regs.bin> | --bstate <BlastEm quicksave.state> [--png out.png] [--region ntsc|pal]"); return 2; }
        string? png = M68kTestCli.Arg(args, "--png");
        var region = M68kTestCli.Arg(args, "--region")?.ToLowerInvariant() == "pal" ? SegaRegion.Pal : SegaRegion.Ntsc;
        var vdp = new MdVdp(SegaTimeline.Genesis(region));
        if (bstate != null)
        {
            try
            {
                var st = File.ReadAllBytes(bstate);
                if (st.Length < 0x10306 || st[0] != 'B' || st[1] != 'L' || st[2] != 'S' || st[3] != 'T') { Console.WriteLine($"{bstate} is not a BlastEm 1.0.0 state"); return 2; }
                Array.Copy(st, 0xAE, vdp.Vram, 0, 0x10000);
                for (int i = 0; i < 64; i++) vdp.LoadColour(i, (ushort)(st[0x100AE + i * 2] << 8 | st[0x100AF + i * 2]));
                for (int i = 0; i < 64; i++) vdp.Vsram[i] = (ushort)((st[0x1012E + i * 2] << 8 | st[0x1012F + i * 2]) & 0x7FF);
                for (int i = 0; i < 24; i++) vdp.Reg[i] = st[0x102EE + i];
            }
            catch (Exception e) { Console.WriteLine($"cannot load {bstate}: {e.Message}"); return 2; }
        }
        else try
        {
            var vram = File.ReadAllBytes(Path.Combine(dir!, "vram.bin")); Array.Copy(vram, vdp.Vram, Math.Min(vram.Length, vdp.Vram.Length));
            var cram = File.ReadAllBytes(Path.Combine(dir!, "cram.bin"));
            for (int i = 0; i < Math.Min(64, cram.Length / 2); i++) vdp.LoadColour(i, (ushort)(cram[i * 2] << 8 | cram[i * 2 + 1]));
            var vsram = File.ReadAllBytes(Path.Combine(dir!, "vsram.bin"));
            for (int i = 0; i < Math.Min(64, vsram.Length / 2); i++) vdp.Vsram[i] = (ushort)((vsram[i * 2] << 8 | vsram[i * 2 + 1]) & 0x7FF);
            var regs = File.ReadAllBytes(Path.Combine(dir!, "regs.bin"));
            for (int i = 0; i < Math.Min(regs.Length, 24); i++) vdp.Reg[i] = regs[i];
        }
        catch (Exception e) { Console.WriteLine($"cannot load {dir}: {e.Message}"); return 2; }
        vdp.ResetPictureState();
        // two frames: the first line of the first frame begins before the registers were in place on a cold start
        long clocks = (long)vdp.Timeline.MasterClocksPerFrame * 2 + 100;
        vdp.RunTo(clocks);
        Console.WriteLine($"rendered {vdp.PictureWidth}x{vdp.PictureHeight} ({(vdp.H40 ? "H40" : "H32")} {(vdp.V30 ? "V30" : "V28")}) frames rendered {vdp.FramesRendered}");
        if (png != null)
        {
            int w = vdp.PictureWidth, h = vdp.PictureHeight;
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) bmp.SetPixel(x, y, Color.FromArgb((int)vdp.Picture[y * MdVdp.PictureStride + x]));
            bmp.Save(png, ImageFormat.Png);
            Console.WriteLine($"wrote {png}");
        }
        return 0;
    }
}
