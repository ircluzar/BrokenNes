// The GeForce 256's VBE 2.0 (INT 10h AH=4Fh): our own BIOS code (the user's rule), programming the card through its own registers as a
// real BIOS would: the NV extended CRTC behind the CR1F lock, PCRTC START and PRAMDAC GENERAL_CONTROL through BAR0. The mode list and
// the option ROM are the S3's (PcMachine.Vbe.cs); the linear framebuffer is BAR1.

using System;

namespace NesEmulator.X86;

public sealed partial class PcMachine
{
    private uint NvLfb => Nv?.Pci?.MemBase1 ?? 0xf0000000;
    private void NvW32(uint off, uint v) { if (Nv!.Pci?.MemBase is uint b and not 0) RawWrite32(b + off, v); else Nv.MmioWrite(off, v, 4); }
    private uint NvR32(uint off) => Nv!.Pci?.MemBase is uint b and not 0 ? RawRead32(b + off) : Nv.MmioRead(off, 4);

    private void VbeNv()
    {
        var nv = Nv!;
        int vram = nv.VramBytes;
        switch (AL)
        {
            case 0x00:
            {
                uint b = EsDi;
                bool v2 = (Lin8(b) | (uint)Lin8(b + 1) << 8 | (uint)Lin8(b + 2) << 16 | (uint)Lin8(b + 3) << 24) == 0x32454256;
                for (uint i = 0; i < (v2 ? 512u : 256u); i++) LinW8(b + i, 0);
                LinW32(b, 0x41534556);
                LinW16(b + 4, 0x0200);
                LinW16(b + 6, OemOff); LinW16(b + 8, (ushort)VRomSeg);
                LinW32(b + 10, 0);
                LinW16(b + 14, ModeListOff); LinW16(b + 16, (ushort)VRomSeg);
                LinW16(b + 18, (ushort)(vram / 65536));
                LinW16(b + 20, 0x0100);
                LinW16(b + 22, VendorOff); LinW16(b + 24, (ushort)VRomSeg);
                LinW16(b + 26, ProductOff); LinW16(b + 28, (ushort)VRomSeg);
                LinW16(b + 30, RevOff); LinW16(b + 32, (ushort)VRomSeg);
                VbeOk();
                break;
            }
            case 0x01:
            {
                var vm = FindVbe((int)(CX & 0x1ff));
                if (vm == null) { VbeFail(); break; }
                var v = vm.Value;
                uint b = EsDi;
                for (uint i = 0; i < 256; i++) LinW8(b + i, 0);
                int bpl = v.W * BytesPP(v.Bpp);
                LinW16(b + 0x00, 0x009b);
                LinW8(b + 0x02, 7); LinW8(b + 0x03, 0);
                LinW16(b + 0x04, 64); LinW16(b + 0x06, 64);
                LinW16(b + 0x08, 0xa000); LinW16(b + 0x0a, 0);
                LinW16(b + 0x0c, WinFuncOff); LinW16(b + 0x0e, (ushort)VRomSeg);
                LinW16(b + 0x10, (ushort)bpl);
                LinW16(b + 0x12, (ushort)v.W); LinW16(b + 0x14, (ushort)v.H);
                LinW8(b + 0x16, 8); LinW8(b + 0x17, 16);
                LinW8(b + 0x18, 1); LinW8(b + 0x19, (byte)v.Bpp); LinW8(b + 0x1a, 1);
                LinW8(b + 0x1b, (byte)(v.Bpp == 8 ? 4 : 6));
                LinW8(b + 0x1d, (byte)Math.Min(255, Math.Max(0, vram / (bpl * v.H) - 1)));
                LinW8(b + 0x1e, 1);
                (int rs, int rp, int gs, int gp, int bs, int bp, int xs, int xp) = v.Bpp switch
                {
                    15 => (5, 10, 5, 5, 5, 0, 1, 15),
                    16 => (5, 11, 6, 5, 5, 0, 0, 0),
                    32 => (8, 16, 8, 8, 8, 0, 8, 24),
                    _ => (0, 0, 0, 0, 0, 0, 0, 0),
                };
                LinW8(b + 0x1f, (byte)rs); LinW8(b + 0x20, (byte)rp); LinW8(b + 0x21, (byte)gs); LinW8(b + 0x22, (byte)gp);
                LinW8(b + 0x23, (byte)bs); LinW8(b + 0x24, (byte)bp); LinW8(b + 0x25, (byte)xs); LinW8(b + 0x26, (byte)xp);
                LinW32(b + 0x28, NvLfb);
                VbeOk();
                break;
            }
            case 0x02:
            {
                int n = (int)(BX & 0x1ff);
                bool noClear = (BX & 0x8000) != 0;
                if (n < 0x100) { SetMode(n | (noClear ? 0x80 : 0)); VbeOk(); break; }
                var vm = FindVbe(n);
                if (vm == null) { VbeFail(); break; }
                NvSetMode(vm.Value, noClear);
                VbeOk();
                break;
            }
            case 0x03:
                BX = nv.VbeMode != 0 ? (uint)(nv.VbeMode | 0x4000) : (uint)VMode;
                VbeOk();
                break;
            case 0x05:
                if (nv.VbeMode == 0) { VbeFail(); break; }
                if ((BH & 1) == 0) { if (BL != 0) { VbeFail(); break; } nv.Bank = (int)DX & 0x1ff; NvCr(0x1d, (int)DX); }
                else DX = (uint)nv.Bank;
                VbeOk();
                break;
            case 0x06:
            {
                if (nv.VbeMode == 0) { VbeFail(); break; }
                int bpp = nv.BytesPerPixel, stride = nv.Stride;
                switch (BL)
                {
                    case 0: stride = ((int)CX * bpp + 7) & ~7; break;
                    case 2: stride = ((int)CX + 7) & ~7; break;
                    case 3: stride = Math.Min(4095 * 8, vram / Math.Max(1, nv.Height)) & ~7; BX = (uint)stride; CX = (uint)(stride / bpp); DX = (uint)(vram / stride); VbeOk(); return;
                }
                if (BL is 0 or 2) NvSetStride(stride);
                stride = nv.Stride;
                BX = (uint)stride; CX = (uint)(stride / bpp); DX = (uint)Math.Min(0xffff, vram / Math.Max(1, stride));
                VbeOk();
                break;
            }
            case 0x07:
                if (nv.VbeMode == 0) { VbeFail(); break; }
                if ((BL & 0x7f) == 0) NvW32(0x600800, (uint)((int)DX * nv.Stride + (int)CX * nv.BytesPerPixel));
                else if (BL == 1)
                {
                    int sb = nv.StartByte, st = Math.Max(1, nv.Stride);
                    BH = 0; DX = (uint)(sb / st); CX = (uint)(sb % st / nv.BytesPerPixel);
                }
                else { VbeFail(); break; }
                VbeOk();
                break;
            case 0x08: BH = 6; VbeOk(); break;
            case 0x09:
            {
                int start = (int)DX & 0xff, count = (int)CX;
                uint b = EsDi;
                for (int i = 0; i < count && start + i < 256; i++, b += 4)
                {
                    int p = (start + i) * 3;
                    if ((BL & 0x7f) == 0) { Vga.Palette[p] = (byte)(Lin8(b + 2) & 63); Vga.Palette[p + 1] = (byte)(Lin8(b + 1) & 63); Vga.Palette[p + 2] = (byte)(Lin8(b) & 63); }
                    else if (BL == 1) { LinW8(b + 2, Vga.Palette[p]); LinW8(b + 1, Vga.Palette[p + 1]); LinW8(b, Vga.Palette[p + 2]); LinW8(b + 3, 0); }
                }
                VbeOk();
                break;
            }
            default: VbeFail(); break;
        }
    }

    /// <summary>An extended CRTC write through the ports, unlocking first and putting the lock back as it was.</summary>
    private void NvCr(int i, int v)
    {
        bool was = Nv!.Unlocked;
        CrOut(0x1f, 0x57); CrOut(i, v);
        if (!was) CrOut(0x1f, 0x99);
    }

    private void NvSetStride(int bytes)
    {
        int q = bytes / 8;
        Vga.Cr[0x13] = (byte)q;
        NvCr(0x19, (Vga.Cr[0x19] & 0x1f) | ((q >> 3) & 0xe0));
        NvCr(0x42, (Vga.Cr[0x42] & ~0x40) | ((q >> 5) & 0x40));
    }

    private void NvSetMode(VbeMode v, bool noClear)
    {
        var nv = Nv!;
        SetMode(0x13 | 0x80);                                    // chain 4, 256-colour attribute setup
        var cr = Vga.Cr;
        int h = v.H - 1;
        cr[0x11] &= 0x7f;
        cr[0x01] = (byte)(v.W / 8 - 1);
        cr[0x12] = (byte)h;
        cr[0x07] = (byte)((cr[0x07] & ~0x42) | ((h >> 7) & 0x02) | ((h >> 3) & 0x40) | 0x10);
        cr[0x09] = 0x40;
        cr[0x18] = 0xff;
        NvCr(0x25, (h >> 9) & 0x02);
        NvCr(0x2d, ((v.W / 8 - 1) >> 7) & 0x02);
        NvCr(0x28, v.Bpp switch { 8 => 1, 15 or 16 => 2, _ => 3 });
        uint gc = NvR32(0x680600);
        NvW32(0x680600, v.Bpp == 16 ? gc | 0x1000 : gc & ~0x1000u);
        NvSetStride(v.W * BytesPP(v.Bpp));
        NvW32(0x600800, 0);
        nv.Bank = 0; NvCr(0x1d, 0);
        if (v.Bpp == 8) Vga.LoadDefaultDac256();
        if (!noClear) Array.Clear(Vga.Ram, 0, Math.Min(Vga.Ram.Length, v.W * BytesPP(v.Bpp) * v.H));
        nv.VbeMode = v.Number;
        PhysW8(0x449, 0x62);
        PhysW16(0x44a, (ushort)(v.W / 8));
        PhysW8(0x484, (byte)(v.H / 16 - 1));
        PhysW16(0x485, 16);
    }
}
