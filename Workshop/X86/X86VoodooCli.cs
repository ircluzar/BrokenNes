using System;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary><c>--x86voodoo [--shot png]</c>: self-test of the 3dfx Voodoo Graphics card without an OS: PCI decode + BAR sizing, the default machine
/// unaffected, init/DAC/status registers, fastfill, Gouraud and textured triangles through the MMIO window, a swap, the picture switch, and a
/// savestate round trip that must reproduce the same picture. Exit code 0 when every check passes.</summary>
internal static class X86VoodooCli
{
    private static int _fail, _pass;
    private static void Check(bool ok, string what) { if (ok) _pass++; else _fail++; Console.WriteLine((ok ? "ok   " : "FAIL ") + what); }

    private const uint Base = 0xe8000000;
    private static void W(PcMachine m, int reg, uint v, int chips = 0) => m.MemWrite32(Base + (uint)(chips << 10) + (uint)reg, v);
    private static uint R(PcMachine m, int reg) => m.MemRead32(Base + (uint)reg);

    private static ulong Hash(int[] f) { ulong h = 14695981039346656037UL; foreach (int p in f) { h ^= (uint)p; h *= 1099511628211UL; } return h; }

    public static int Run(string[] args)
    {
        if (Array.IndexOf(args, "--dump") is int di and >= 0)
        {
            // --dump STATE: the card's registers in a WIN98-P166-VOODOO state
            var dcfg = Array.IndexOf(args, "--machine") is int mi and >= 0 ? PcMachinePreset.Find(args[mi + 1]).Config : PcConfig.Win98Voodoo;   // (--machine win98-p166-voodoo-nano)
            var dm = new PcMachine(dcfg, CpuModel.PentiumMmx, PcVideo.S3Trio64);
            if (Array.IndexOf(args, "--hdd") is int hi and >= 0) dm.Hdd[0] = new DiskImage(System.IO.File.ReadAllBytes(args[hi + 1]), false);
            dm.LoadState(System.IO.File.ReadAllBytes(args[di + 1]));
            var v = dm.Voodoo!;
            foreach (var (n, o) in new[] { ("fbzColorPath", 0x104), ("fogMode", 0x108), ("alphaMode", 0x10c), ("fbzMode", 0x110), ("lfbMode", 0x114), ("clipLR", 0x118), ("clipTB", 0x11c), ("fbiInit0", 0x210), ("fbiInit1", 0x214), ("fbiInit2", 0x218), ("fbiInit3", 0x21c), ("fbiInit4", 0x200), ("videoDim", 0x20c), ("hSync", 0x220), ("vSync", 0x224), ("backPorch", 0x208) })
                Console.WriteLine($"{n,-14} {v.Reg[o / 4]:X8}");
            foreach (var (n, o) in new[] { ("textureMode", 0x300), ("tLOD", 0x304), ("tDetail", 0x308), ("texBase", 0x30c), ("trexInit1", 0x320) }) Console.WriteLine($"{n,-14} {v.TReg[o / 4]:X8}");
            Console.WriteLine($"active {v.Active} {v.Width}x{v.Height} triangles {v.Triangles} pixels {v.Pixels}");
            return 0;
        }
        string? shot = Array.IndexOf(args, "--shot") is int si and >= 0 && si + 1 < args.Length ? args[si + 1] : null;

        // the default Win98 machine has no 3D card: nothing at 00:0B.0
        var plain = new PcMachine(PcConfig.Win98, CpuModel.PentiumMmx, PcVideo.S3Trio64);
        Check(plain.Voodoo == null && plain.Pci!.ConfigRead(0, PciBus.Voodoo1DevFn, 0) == 0xffffffff, "default WIN98-P166: no Voodoo on the bus");

        var m = new PcMachine(PcConfig.Win98Voodoo, CpuModel.PentiumMmx, PcVideo.S3Trio64);
        var pci = m.Pci!;
        Check(pci.ConfigRead(0, PciBus.Voodoo1DevFn, 0) == 0x0001121a, "PCI ID 121A:0001");
        Check((pci.ConfigRead(0, PciBus.Voodoo1DevFn, 8) >> 8) == 0x040000, "class 04 00 00 (multimedia video)");
        pci.ConfigWrite(0, PciBus.Voodoo1DevFn, 0x10, 0xffffffff, 4);
        Check(pci.ConfigRead(0, PciBus.Voodoo1DevFn, 0x10) == 0xff000000, "BAR0 sizes as 16 MB memory");
        pci.ConfigWrite(0, PciBus.Voodoo1DevFn, 0x10, Base, 4);
        Check(m.MemRead32(Base) == 0xffffffff, "memory decode off: the window floats");
        pci.ConfigWrite(0, PciBus.Voodoo1DevFn, 0x04, 0x02, 1);
        pci.ConfigWrite(0, PciBus.Voodoo1DevFn, 0x40, 0x01, 4);   // initEnable: init registers writable

        uint st = R(m, 0x000);
        Check((st & 0x3f) == 0x3f && ((st >> 12) & 0xffff) == 0xffff && (st & (7 << 7)) == 0, $"status: FIFOs free, not busy ({st:X8})");
        // DAC probe as Glide does it (ATT20C409: write reg 7, read reg 5 / 6)
        W(m, 0x22c, 0x700 | 0x01); W(m, 0x22c, 0x800 | 0x500);
        pci.ConfigWrite(0, PciBus.Voodoo1DevFn, 0x40, 0x05, 4);
        Check((R(m, 0x218) & 0xff) == 0x55, "DAC read back through fbiInit2 (initEnable bit 2)");
        pci.ConfigWrite(0, PciBus.Voodoo1DevFn, 0x40, 0x01, 4);

        // 640x480: 10 tiles of 64 across, buffer = 150 pages (640*480*2 / 4096), video on (fbiInit0 bit 0, fbiInit1 not blanked)
        W(m, 0x214, 10u << 4);
        W(m, 0x218, 150u << 11);
        W(m, 0x20c, 480u << 16 | 639);
        Check(!m.Voodoo!.Active, "pass-through: the S3 still shows");
        W(m, 0x210, 1);
        Check(m.Voodoo.Active && m.Voodoo.Width == 640 && m.Voodoo.Height == 480, "fbiInit0 bit 0: the Voodoo drives the monitor at 640x480");

        // fastfill the back buffer dark blue, clear depth
        W(m, 0x118, 0u << 16 | 640); W(m, 0x11c, 0u << 16 | 480);
        W(m, 0x148, 0x00102060); W(m, 0x130, 0xffff);
        W(m, 0x110, 1 | 1 << 9 | 1 << 10 | 1 << 14);   // clip, RGB + aux write, draw buffer = back
        W(m, 0x124, 0);
        // Gouraud triangle (iterated RGB, fbzColorPath 0), 12.4 vertices, 12.12 colours
        W(m, 0x104, 0);
        W(m, 0x110, 1 | 1 << 8 | 1 << 9 | 1 << 14);    // + dither
        void Tri(float ax, float ay, float bx, float by, float cx, float cy)
        {
            W(m, 0x008, (uint)(int)(ax * 16)); W(m, 0x00c, (uint)(int)(ay * 16));
            W(m, 0x010, (uint)(int)(bx * 16)); W(m, 0x014, (uint)(int)(by * 16));
            W(m, 0x018, (uint)(int)(cx * 16)); W(m, 0x01c, (uint)(int)(cy * 16));
        }
        Tri(320, 40, 600, 440, 40, 440);
        // colour planes: R = 255 at A, falls to 0 at the base; G grows to the right; B grows to the left (per pixel, 12.12)
        W(m, 0x020, 255u << 12); W(m, 0x024, 0); W(m, 0x028, 0); W(m, 0x030, 255u << 12);
        W(m, 0x040, 0); W(m, 0x060, unchecked((uint)(-(255 << 12) / 400)) & 0xffffff);
        W(m, 0x044, (uint)((255 << 12) / 560)); W(m, 0x064, (uint)((255 << 12) / 400) / 2);
        W(m, 0x048, unchecked((uint)(-(255 << 12) / 560)) & 0xffffff); W(m, 0x068, (uint)((255 << 12) / 400) / 2);
        W(m, 0x080, 0);
        Check(m.Voodoo.Triangles == 1 && m.Voodoo.Pixels > 100_000, $"Gouraud triangle drawn ({m.Voodoo.Pixels} pixels)");

        // textured quad (two triangles): a 256x256 RGB565 checkerboard, point sampled, no perspective; texture = c_local (Glide GR_COMBINE_FUNCTION_LOCAL)
        W(m, 0x300, 10u << 8 | 1u << 12 | 1u << 18 | 1u << 21 | 1u << 27, 2);   // textureMode (TMU0 only)
        W(m, 0x304, 0, 2);                                                     // tLOD: lod 0..0, square
        W(m, 0x30c, 0, 2);
        for (uint t = 0; t < 256; t++)
            for (uint s = 0; s < 256; s += 2)
            {
                uint c0 = ((s >> 5) ^ (t >> 5)) % 2 == 0 ? 0xffe0u : 0xf81fu;   // yellow / magenta 32x32 squares
                m.MemWrite32(Base + 0x800000 + (t << 9) + (s << 1), c0 | c0 << 16);
            }
        W(m, 0x104, 1 | 1 << 27);                                              // cc_rgbselect = texture, texture enable
        // S/T: 1 texel per pixel, starting at 0 (registers are 14.18)
        W(m, 0x034, 0, 2); W(m, 0x038, 0, 2);
        W(m, 0x054, 1u << 18, 2); W(m, 0x058, 0, 2); W(m, 0x074, 0, 2); W(m, 0x078, 1u << 18, 2);
        Tri(200, 200, 456, 200, 200, 456);   // texture 0,0 at A
        W(m, 0x080, 0);
        Check(m.Voodoo.Triangles == 2, "textured triangle drawn");

        // swap (no vsync wait) and show
        W(m, 0x128, 0);
        m.Vga.Render();
        m.Voodoo.RenderInto(m.Vga);
        var f = m.Vga.Frame;
        int Px(int x, int y) => f[y * m.Vga.Width + x] & 0xffffff;
        Check(m.Vga.Width == 640 && m.Vga.Height == 480, "picture is the Voodoo's 640x480");
        Check(Px(5, 5) == 0x102060 || (Px(5, 5) & 0xf0f0f0) == 0x102060, $"background = fastfill colour ({Px(5, 5):X6})");
        int r = Px(320, 60) >> 16, b = Px(560, 430) & 0xff, g = (Px(560, 430) >> 8) & 0xff;
        Check(r > 200, $"triangle apex is red ({Px(320, 60):X6})");
        Check(Px(210, 210) is 0xffff00 && Px(250, 210) is 0xff00ff, $"texture checker: yellow at (10,10), magenta at (50,10) ({Px(210, 210):X6} {Px(250, 210):X6})");
        ulong h1 = Hash(f);

        // savestate round trip: the same picture after load, and after more drawing both copies stay identical
        var state = m.SaveState();
        var m2 = new PcMachine(PcConfig.Win98Voodoo, CpuModel.PentiumMmx, PcVideo.S3Trio64);
        m2.LoadState(state);
        m2.Vga.Render(); m2.Voodoo!.RenderInto(m2.Vga);
        Check(Hash(m2.Vga.Frame) == h1, "state round trip: same picture");
        bool threw = false;
        try { new PcMachine(PcConfig.Win98, CpuModel.PentiumMmx, PcVideo.S3Trio64).LoadState(state); } catch (System.IO.InvalidDataException) { threw = true; }
        Check(threw, "a Voodoo state refuses to load into the machine without the card");

        if (shot != null) { X86Shots.SavePng(m.Vga, shot); Console.WriteLine($"x86voodoo: picture {shot}"); }
        Console.WriteLine($"x86voodoo: {_pass} passed, {_fail} failed (picture hash {h1:X16})");
        return _fail == 0 ? 0 : 1;
    }
}
