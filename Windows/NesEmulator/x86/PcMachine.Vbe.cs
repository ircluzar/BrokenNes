// The x86 console's video BIOS for the SVGA cards: VBE 2.0 (INT 10h AH=4Fh) and the option ROM at C0000. Written for BrokenNes
// (the user's decision: every BIOS on this board is our own code, no downloaded card ROM). Covers 4F00-4F09 on the S3 Trio64:
// 8, 15, 16 and 32 bpp modes from 640x400 to 1024x768, banked (64 KB window at A000h, WinFuncPtr in the ROM) and linear.

using System;
using System.Text;

namespace NesEmulator.X86;

/// <summary>The video card on the board.</summary>
public enum PcVideo { Vga, S3Trio64, GeForce256 }

public sealed partial class PcMachine
{
    public readonly PcVideo Video;
    /// <summary>The S3 Trio64's extensions (null on the plain VGA).</summary>
    public readonly S3Trio64? S3;
    /// <summary>The GeForce 256's extensions (null unless the board has that card).</summary>
    public readonly NvGeForce? Nv;

    private const uint VRom = 0xc0000, VRomSeg = 0xc000;
    private const ushort WinFuncOff = 0x0040, OemOff = 0x0050, VendorOff = 0x0080, ProductOff = 0x0090, RevOff = 0x00a0, ModeListOff = 0x00c0;

    public readonly record struct VbeMode(int Number, int W, int H, int Bpp);
    public static readonly VbeMode[] VbeModes =
    {
        new(0x100, 640, 400, 8), new(0x101, 640, 480, 8), new(0x103, 800, 600, 8), new(0x105, 1024, 768, 8),
        new(0x110, 640, 480, 15), new(0x111, 640, 480, 16), new(0x112, 640, 480, 32),
        new(0x113, 800, 600, 15), new(0x114, 800, 600, 16), new(0x115, 800, 600, 32),
        new(0x116, 1024, 768, 15), new(0x117, 1024, 768, 16), new(0x118, 1024, 768, 32),
        new(0x107, 1280, 1024, 8), new(0x119, 1280, 1024, 15), new(0x11a, 1280, 1024, 16),   // (4 MB: a Trio64 offers 1280x1024 up to 16 bpp)
    };
    private static int BytesPP(int bpp) => (bpp + 7) / 8;

    /// <summary>The option ROM: 55 AA, 32 KB (so DOS memory managers leave C0000-C7FFF alone), the bank-switch far routine, the strings, the mode list.</summary>
    private void VideoRomInit()
    {
        var m = Cpu.Mem;
        if (VRom + 0x8000 > Cpu.MemSize) return;
        Array.Clear(m, (int)VRom, 0x8000);
        m[VRom] = 0x55; m[VRom + 1] = 0xaa; m[VRom + 2] = 0x40;
        m[VRom + 3] = 0xcb;                                       // the init entry: RETF (the C# BIOS set the card up already)
        // WinFuncPtr: MOV AX,4F05h / INT 10h / RETF
        byte[] wf = { 0xb8, 0x05, 0x4f, 0xcd, 0x10, 0xcb };
        Array.Copy(wf, 0, m, VRom + WinFuncOff, wf.Length);
        void Str(ushort off, string s) { var b = Encoding.ASCII.GetBytes(s + "\0"); Array.Copy(b, 0, m, VRom + off, b.Length); }
        Str(OemOff, Nv != null ? "BrokenNes GeForce 256 VBE 2.0" : "BrokenNes S3 Trio64 VBE 2.0");
        Str(VendorOff, "BrokenNes");
        Str(ProductOff, Nv != null ? "GeForce 256 DDR" : "S3 Trio64");
        Str(RevOff, "1.0");
        uint o = VRom + ModeListOff;
        foreach (var vm in VbeModes) { m[o] = (byte)vm.Number; m[o + 1] = (byte)(vm.Number >> 8); o += 2; }
        m[o] = 0xff; m[o + 1] = 0xff;
        // the checksum: the 32 KB sum to 0
        int sum = 0; for (int i = 0; i < 0x8000; i++) sum += m[VRom + i];
        m[VRom + 0x7fff] = (byte)(m[VRom + 0x7fff] - sum);
    }

    private static VbeMode? FindVbe(int n) { foreach (var vm in VbeModes) if (vm.Number == n) return vm; return null; }

    private uint EsDi => SegB(I386.SEG_ES) + DI;   // a LINEAR address: under Windows 98 a DOS box (V86) is paged, so the buffers go through LinW / Lin8 (PhysW wrote into whatever page sat at ES*16+DI: Quake in a DOS box read a garbage VBE block, 2026-11-20)
    private void VbeOk() => AX = 0x004f;
    private void VbeFail() => AX = 0x014f;

    private void Vbe()
    {
        var s3 = S3!;
        switch (AL)
        {
            case 0x00:
            {
                uint b = EsDi;
                bool v2 = (Lin8(b) | (uint)Lin8(b + 1) << 8 | (uint)Lin8(b + 2) << 16 | (uint)Lin8(b + 3) << 24) == 0x32454256;                   // "VBE2": the caller has room for the 2.0 fields
                for (uint i = 0; i < (v2 ? 512u : 256u); i++) LinW8(b + i, 0);
                LinW32(b, 0x41534556);                              // "VESA"
                LinW16(b + 4, 0x0200);
                LinW16(b + 6, OemOff); LinW16(b + 8, (ushort)VRomSeg);
                LinW32(b + 10, 0);                                  // capabilities: 6-bit DAC, VGA compatible
                LinW16(b + 14, ModeListOff); LinW16(b + 16, (ushort)VRomSeg);
                LinW16(b + 18, (ushort)(S3Trio64.VramBytes / 65536));
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
                LinW16(b + 0x00, 0x009b);                           // supported, colour, graphics, linear framebuffer
                LinW8(b + 0x02, 7); LinW8(b + 0x03, 0);            // window A: read/write, no window B
                LinW16(b + 0x04, 64); LinW16(b + 0x06, 64);
                LinW16(b + 0x08, 0xa000); LinW16(b + 0x0a, 0);
                LinW16(b + 0x0c, WinFuncOff); LinW16(b + 0x0e, (ushort)VRomSeg);
                LinW16(b + 0x10, (ushort)bpl);
                LinW16(b + 0x12, (ushort)v.W); LinW16(b + 0x14, (ushort)v.H);
                LinW8(b + 0x16, 8); LinW8(b + 0x17, 16);
                LinW8(b + 0x18, 1); LinW8(b + 0x19, (byte)v.Bpp); LinW8(b + 0x1a, 1);
                LinW8(b + 0x1b, (byte)(v.Bpp == 8 ? 4 : 6));        // packed pixel / direct colour
                LinW8(b + 0x1c, 0);
                LinW8(b + 0x1d, (byte)Math.Max(0, S3Trio64.VramBytes / (bpl * v.H) - 1));
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
                LinW8(b + 0x27, 0);
                LinW32(b + 0x28, s3.LfbBase);
                VbeOk();
                break;
            }
            case 0x02:
            {
                int n = (int)(BX & 0x1ff);
                bool linear = (BX & 0x4000) != 0, noClear = (BX & 0x8000) != 0;
                if (n < 0x100) { SetMode(n | (noClear ? 0x80 : 0)); VbeOk(); break; }
                var vm = FindVbe(n);
                if (vm == null) { VbeFail(); break; }
                VbeSetMode(vm.Value, linear, noClear);
                VbeOk();
                break;
            }
            case 0x03:
                BX = s3.VbeMode != 0 ? (uint)(s3.VbeMode | (s3.LinearOn ? 0x4000 : 0)) : (uint)VMode;
                VbeOk();
                break;
            case 0x04: VbeFail(); break;                             // save/restore state: not offered
            case 0x05:
                if (s3.VbeMode == 0) { VbeFail(); break; }
                if ((BH & 1) == 0) { if (BL != 0) { VbeFail(); break; } SetBank((int)DX); }
                else DX = (uint)s3.Bank;
                VbeOk();
                break;
            case 0x06:
            {
                if (s3.VbeMode == 0) { VbeFail(); break; }
                int bpp = s3.BytesPerPixel, stride = s3.Stride;
                switch (BL)
                {
                    case 0: stride = ((int)CX * bpp + 7) & ~7; break;
                    case 2: stride = ((int)CX + 7) & ~7; break;
                    case 3: stride = Math.Min(4088 * 2, S3Trio64.VramBytes / Math.Max(1, s3.Height)) & ~7; BX = (uint)stride; CX = (uint)(stride / bpp); DX = (uint)(S3Trio64.VramBytes / stride); VbeOk(); return;
                }
                if (BL is 0 or 2) SetStride(stride);
                stride = s3.Stride;
                BX = (uint)stride; CX = (uint)(stride / bpp); DX = (uint)(S3Trio64.VramBytes / Math.Max(1, stride));
                VbeOk();
                break;
            }
            case 0x07:
                if (s3.VbeMode == 0) { VbeFail(); break; }
                if ((BL & 0x7f) == 0) SetStart((int)DX * s3.Stride + (int)CX * s3.BytesPerPixel);
                else if (BL == 1)
                {
                    int sb = s3.StartByte, st = Math.Max(1, s3.Stride);
                    BH = 0; DX = (uint)(sb / st); CX = (uint)(sb % st / s3.BytesPerPixel);
                }
                else { VbeFail(); break; }
                VbeOk();
                break;
            case 0x08:                                               // DAC width: 6 bits only
                BH = 6;
                VbeOk();
                break;
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
            default: VbeFail(); break;                               // 4F0A (protected-mode interface) and later: not offered
        }
    }

    private void CrOut(int i, int v) { Out8(0x3d4, (byte)i); Out8(0x3d5, (byte)v); }

    private void SetBank(int b) { CrOut(0x6a, b & 0x3f); }

    private void SetStride(int bytes)
    {
        int q = bytes / 8;
        Vga.Cr[0x13] = (byte)q;
        CrOut(0x51, (Vga.Cr[0x51] & ~0x30) | ((q >> 4) & 0x30));
    }

    private void SetStart(int bytes)
    {
        int d = bytes / 4;
        Vga.Cr[0x0c] = (byte)(d >> 8); Vga.Cr[0x0d] = (byte)d;
        CrOut(0x31, (Vga.Cr[0x31] & ~0x30) | ((d >> 12) & 0x30));
        CrOut(0x69, (d >> 16) & 0x1f);
    }

    /// <summary>A VBE mode set on the S3, done through the card's own ports as a real BIOS would (unlock, then the extended registers).</summary>
    private void VbeSetMode(VbeMode v, bool linear, bool noClear)
    {
        var s3 = S3!;
        SetMode(0x13 | 0x80);                                    // chain 4, 256-colour attribute setup, A0000 64 KB window
        CrOut(0x38, 0x48); CrOut(0x39, 0xa5); Out8(0x3c4, 8); Out8(0x3c5, 6);
        var cr = Vga.Cr;
        int h = v.H - 1;
        cr[0x11] &= 0x7f;                                       // CRTC write protect off
        cr[0x01] = (byte)(v.W / 8 - 1);
        cr[0x12] = (byte)h;
        cr[0x07] = (byte)((cr[0x07] & ~0x42) | ((h >> 7) & 0x02) | ((h >> 3) & 0x40) | 0x10);
        cr[0x09] = 0x40;                                        // no double scan, line compare bit 9
        cr[0x18] = 0xff;                                        // no split screen
        CrOut(0x5e, (h >> 9) & 0x02);
        CrOut(0x5d, ((v.W / 8 - 1) >> 7) & 0x02);
        CrOut(0x67, v.Bpp switch { 15 => 0x30, 16 => 0x50, 32 => 0xd0, _ => 0x00 });
        CrOut(0x31, 0x08);                                       // enhanced memory mapping
        CrOut(0x53, 0);
        SetStride(v.W * BytesPP(v.Bpp));
        SetStart(0);
        SetBank(0);
        CrOut(0x58, linear ? 0x13 : 0x03);                       // linear window 4 MB, on for a linear mode set
        CrOut(0x45, 0);
        if (v.Bpp == 8) Vga.LoadDefaultDac256();
        if (!noClear) Array.Clear(Vga.Ram, 0, Math.Min(Vga.Ram.Length, v.W * BytesPP(v.Bpp) * v.H));
        s3.VbeMode = v.Number;
        PhysW8(0x449, 0x62);                                     // not a standard mode: the teletype draws nothing
        PhysW16(0x44a, (ushort)(v.W / 8));
        PhysW8(0x484, (byte)(v.H / 16 - 1));
        PhysW16(0x485, 16);
    }
}
