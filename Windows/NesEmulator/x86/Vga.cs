// VGA adapter: registers, the four memory planes and the picture. The register model and the CPU memory access (chain 4, odd/even,
// the latched write modes 0-3 and read modes 0-1) are ported from tiny386 vga.c (commit 2c1205c), itself from QEMU, MIT licence,
// Copyright (c) 2003-2017 Fabrice Bellard; the graphics picture follows its vga_graphic_refresh (line offset, double scan, split
// screen, panning). Left out: VBE / Bochs extensions, the PCI device, the terminal mirror, the scalers. The text picture is written
// for BrokenNes. See x86/THIRD_PARTY_NOTICES.md.

using System;
using System.IO;

namespace NesEmulator.X86;

/// <summary>Standard VGA (256 KB): text modes, mode 13h, Mode X / unchained 256 colours, the 16-colour planar modes.</summary>
public sealed class Vga
{
    public const int RamSize = 256 * 1024;
    public readonly byte[] Ram = new byte[RamSize];     // interleaved: byte 4*a+p = plane p at address a (tiny386 / QEMU layout)
    public readonly byte[] Sr = new byte[8], Gr = new byte[16], Cr = new byte[256], Ar = new byte[21];
    public readonly byte[] Palette = new byte[768];
    public byte Msr, SrIndex, GrIndex, CrIndex, ArIndex, ArFlipFlop, DacMask = 0xff, DacState, DacReadIndex, DacWriteIndex, DacSub, Fcr, St00;
    private readonly byte[] _dacCache = new byte[3];
    private uint _latch;
    private readonly Func<long> _pitNow;

    private static readonly byte[] SrMask = { 0x03, 0x3d, 0x0f, 0x3f, 0x0e, 0x00, 0x00, 0xff };
    private static readonly byte[] GrMask = { 0x0f, 0x0f, 0x0f, 0x1f, 0x03, 0x7b, 0x0f, 0x0f, 0xff, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly uint[] Mask16 = BuildMask16();
    private static uint[] BuildMask16()
    {
        var m = new uint[16];
        for (int i = 0; i < 16; i++)
            m[i] = ((i & 1) != 0 ? 0xffu : 0) | ((i & 2) != 0 ? 0xff00u : 0) | ((i & 4) != 0 ? 0xff0000u : 0) | ((i & 8) != 0 ? 0xff000000u : 0);
        return m;
    }

    /// <summary>The picture, 0xAARRGGBB, <see cref="Width"/> x <see cref="Height"/> (text 720x400 / 640x400, mode 13h 640x400, mode 12h 640x480).</summary>
    public int[] Frame = new int[720 * 400];
    public int Width = 720, Height = 400;
    public int FrameCount;

    // 70 Hz timing: 449 lines of 800 dots at 25.175 MHz, counted in PIT ticks
    public const double FrameTicks = Pit8254.Freq * 449.0 * 800.0 / 25175000.0;

    public Vga(Func<long> pitNow) { _pitNow = pitNow; InitMode3Defaults(); }

    /// <summary>The picture chip drawing this card's memory (the VGA harvest: another emulator's renderer, x86/VgaPictures.cs), or null for the stock picture.
    /// It sees the card's port writes and memory accesses as they happen and replaces <see cref="Render"/>; the registers and memory stay this card's.</summary>
    public IVgaPicture? Picture;
    /// <summary>The machine clock in PIT ticks (for a picture chip's own beam timing).</summary>
    public long PitNow => _pitNow();

    // ================= ports =================

    public byte In(int addr)
    {
        if ((addr >= 0x3b0 && addr <= 0x3bf && (Msr & 1) != 0) || (addr >= 0x3d0 && addr <= 0x3df && (Msr & 1) == 0)) return 0xff;
        switch (addr)
        {
            case 0x3c0: return ArFlipFlop == 0 ? ArIndex : (byte)0;
            case 0x3c1: { int i = ArIndex & 0x1f; return i < 21 ? Ar[i] : (byte)0; }
            case 0x3c2: return St00;
            case 0x3c4: return SrIndex;
            case 0x3c5: return Sr[SrIndex];
            case 0x3c6: return DacMask;
            case 0x3c7: return DacState;
            case 0x3c8: return DacWriteIndex;
            case 0x3c9:
            {
                byte v = Palette[DacReadIndex * 3 + DacSub];
                if (++DacSub == 3) { DacSub = 0; DacReadIndex++; }
                return v;
            }
            case 0x3ca: return Fcr;
            case 0x3cc: return Msr;
            case 0x3ce: return GrIndex;
            case 0x3cf: return Gr[GrIndex];
            case 0x3b4: case 0x3d4: return CrIndex;
            case 0x3b5: case 0x3d5: return Cr[CrIndex];
            case 0x3ba: case 0x3da: ArFlipFlop = 0; return Status1();
            default: return 0;
        }
    }

    /// <summary>Input status 1: bit 3 vertical retrace, bit 0 display disabled (horizontal or vertical blank), from the machine clock.</summary>
    public byte Status1()
    {
        double pos = _pitNow() % FrameTicks;
        double linef = pos * 449.0 / FrameTicks;
        int line = (int)linef;
        bool vr = line >= 412 && line < 414;
        bool hblank = linef - line >= 0.8 || line >= 400;
        return (byte)((vr ? 8 : 0) | (hblank ? 1 : 0));
    }

    /// <summary>Which frame the clock is in (a new frame starts at the end of vertical retrace's start line, 412).</summary>
    public long FrameIndex(long pitNow) => (long)((pitNow + FrameTicks * (449 - 412) / 449.0) / FrameTicks);

    public void Out(int addr, byte v)
    {
        Picture?.NoteOut(addr, v);
        if ((addr >= 0x3b0 && addr <= 0x3bf && (Msr & 1) != 0) || (addr >= 0x3d0 && addr <= 0x3df && (Msr & 1) == 0)) return;
        int val = v;
        switch (addr)
        {
            case 0x3c0:
                if (ArFlipFlop == 0) ArIndex = (byte)(val & 0x3f);
                else
                {
                    int index = ArIndex & 0x1f;
                    switch (index)
                    {
                        case <= 0x0f: Ar[index] = (byte)(val & 0x3f); break;
                        case 0x10: Ar[index] = (byte)(val & ~0x10); break;
                        case 0x11: Ar[index] = (byte)val; break;
                        case 0x12: Ar[index] = (byte)(val & ~0xc0); break;
                        case 0x13: case 0x14: Ar[index] = (byte)(val & ~0xf0); break;
                    }
                }
                ArFlipFlop ^= 1;
                break;
            case 0x3c2: Msr = (byte)(val & ~0x10); break;
            case 0x3c4: SrIndex = (byte)(val & 7); break;
            case 0x3c5: Sr[SrIndex] = (byte)(val & SrMask[SrIndex]); break;
            case 0x3c6: DacMask = (byte)val; break;
            case 0x3c7: DacReadIndex = (byte)val; DacSub = 0; DacState = 3; break;
            case 0x3c8: DacWriteIndex = (byte)val; DacSub = 0; DacState = 0; break;
            case 0x3c9:
                _dacCache[DacSub] = (byte)(val & 0x3f);
                if (++DacSub == 3)
                {
                    Buffer.BlockCopy(_dacCache, 0, Palette, DacWriteIndex * 3, 3);
                    DacSub = 0; DacWriteIndex++;
                }
                break;
            case 0x3ce: GrIndex = (byte)(val & 0x0f); break;
            case 0x3cf: Gr[GrIndex] = (byte)(val & GrMask[GrIndex]); break;
            case 0x3b4: case 0x3d4: CrIndex = (byte)val; break;
            case 0x3b5: case 0x3d5:
                if ((Cr[0x11] & 0x80) != 0 && CrIndex <= 7)
                {
                    if (CrIndex == 7) Cr[7] = (byte)((Cr[7] & ~0x10) | (val & 0x10));
                    return;
                }
                Cr[CrIndex] = (byte)val;
                break;
            case 0x3ba: case 0x3da: Fcr = (byte)(val & 0x10); break;
        }
    }

    // ================= memory (0xA0000-0xBFFFF) =================

    private bool MapAddr(ref uint addr)
    {
        int mmm = (Gr[6] >> 2) & 3;
        addr &= 0x1ffff;
        switch (mmm)
        {
            case 0: return true;
            case 1: return addr < 0x10000;
            case 2: addr -= 0x10000; return addr < 0x8000;
            default: addr -= 0x18000; return addr < 0x8000;
        }
    }

    public void MemWrite(uint addr, byte val8)
    {
        if ((Msr & 2) == 0) return;
        Picture?.NoteWrite(addr, val8);
        if (!MapAddr(ref addr)) return;
        uint val = val8;
        if ((Sr[4] & 8) != 0)
        {
            int plane = (int)(addr & 3);
            if ((Sr[2] & (1 << plane)) != 0 && addr < RamSize) Ram[addr] = val8;
        }
        else if ((Gr[5] & 0x10) != 0)
        {
            int plane = (Gr[4] & 2) | (int)(addr & 1);
            if ((Sr[2] & (1 << plane)) != 0)
            {
                addr = ((addr & ~1u) << 1) | (uint)plane;
                if (addr < RamSize) Ram[addr] = val8;
            }
        }
        else
        {
            uint bitMask;
            int b;
            switch (Gr[5] & 3)
            {
                default:
                    b = Gr[3] & 7;
                    val = ((val >> b) | (val << (8 - b))) & 0xff;
                    val |= val << 8; val |= val << 16;
                    uint setMask = Mask16[Gr[1]];
                    val = (val & ~setMask) | (Mask16[Gr[0]] & setMask);
                    bitMask = Gr[8];
                    break;
                case 1: val = _latch; goto doWrite;
                case 2: val = Mask16[val & 0x0f]; bitMask = Gr[8]; break;
                case 3:
                    b = Gr[3] & 7;
                    val = (val >> b) | (val << (8 - b));
                    bitMask = Gr[8] & val & 0xff;
                    val = Mask16[Gr[0]];
                    break;
            }
            switch (Gr[3] >> 3)
            {
                case 1: val &= _latch; break;
                case 2: val |= _latch; break;
                case 3: val ^= _latch; break;
            }
            bitMask |= bitMask << 8; bitMask |= bitMask << 16;
            val = (val & bitMask) | (_latch & ~bitMask);
        doWrite:
            uint writeMask = Mask16[Sr[2] & 0xf];
            if (addr * 4 >= RamSize) return;
            int o = (int)addr * 4;
            uint cur = (uint)(Ram[o] | (Ram[o + 1] << 8) | (Ram[o + 2] << 16) | (Ram[o + 3] << 24));
            cur = (cur & ~writeMask) | (val & writeMask);
            Ram[o] = (byte)cur; Ram[o + 1] = (byte)(cur >> 8); Ram[o + 2] = (byte)(cur >> 16); Ram[o + 3] = (byte)(cur >> 24);
        }
    }

    public byte MemRead(uint addr)
    {
        if ((Msr & 2) == 0) return 0xff;
        Picture?.NoteRead(addr);
        if (!MapAddr(ref addr)) return 0xff;
        if ((Sr[4] & 8) != 0) return addr < RamSize ? Ram[addr] : (byte)0xff;
        if ((Gr[5] & 0x10) != 0)
        {
            int plane = (Gr[4] & 2) | (int)(addr & 1);
            addr = ((addr & ~1u) << 1) | (uint)plane;
            return addr < RamSize ? Ram[addr] : (byte)0xff;
        }
        if (addr * 4 >= RamSize) return 0xff;
        int o = (int)addr * 4;
        _latch = (uint)(Ram[o] | (Ram[o + 1] << 8) | (Ram[o + 2] << 16) | (Ram[o + 3] << 24));
        if ((Gr[5] & 0x08) == 0) return (byte)(_latch >> ((Gr[4] & 3) * 8));
        uint ret = (_latch ^ Mask16[Gr[2] & 0xf]) & Mask16[Gr[7] & 0xf];
        ret |= ret >> 16; ret |= ret >> 8;
        return (byte)~ret;
    }

    // ================= mode tables (the standard IBM VGA register values) =================

    public static readonly (byte msr, byte[] seq, byte[] crtc, byte[] gc, byte[] ac) Mode3 = (0x67,
        new byte[] { 0x03, 0x00, 0x03, 0x00, 0x02 },
        new byte[] { 0x5f, 0x4f, 0x50, 0x82, 0x55, 0x81, 0xbf, 0x1f, 0x00, 0x4f, 0x0d, 0x0e, 0x00, 0x00, 0x00, 0x00, 0x9c, 0x8e, 0x8f, 0x28, 0x1f, 0x96, 0xb9, 0xa3, 0xff },
        new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x0e, 0x0f, 0xff },
        new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x14, 0x07, 0x38, 0x39, 0x3a, 0x3b, 0x3c, 0x3d, 0x3e, 0x3f, 0x0c, 0x00, 0x0f, 0x08, 0x00 });
    public static readonly (byte msr, byte[] seq, byte[] crtc, byte[] gc, byte[] ac) Mode13 = (0x63,
        new byte[] { 0x03, 0x01, 0x0f, 0x00, 0x0e },
        new byte[] { 0x5f, 0x4f, 0x50, 0x82, 0x54, 0x80, 0xbf, 0x1f, 0x00, 0x41, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x9c, 0x8e, 0x8f, 0x28, 0x40, 0x96, 0xb9, 0xa3, 0xff },
        new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x05, 0x0f, 0xff },
        new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x41, 0x00, 0x0f, 0x00, 0x00 });
    public static readonly (byte msr, byte[] seq, byte[] crtc, byte[] gc, byte[] ac) Mode12 = (0xe3,
        new byte[] { 0x03, 0x01, 0x0f, 0x00, 0x06 },
        new byte[] { 0x5f, 0x4f, 0x50, 0x82, 0x54, 0x80, 0x0b, 0x3e, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xea, 0x8c, 0xdf, 0x28, 0x00, 0xe7, 0x04, 0xe3, 0xff },
        new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x0f, 0xff },
        new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x14, 0x07, 0x38, 0x39, 0x3a, 0x3b, 0x3c, 0x3d, 0x3e, 0x3f, 0x01, 0x00, 0x0f, 0x00, 0x00 });

    public static readonly byte[] PalEga =
    {
        0x00,0x00,0x00, 0x00,0x00,0x2a, 0x00,0x2a,0x00, 0x00,0x2a,0x2a, 0x2a,0x00,0x00, 0x2a,0x00,0x2a, 0x2a,0x2a,0x00, 0x2a,0x2a,0x2a,
        0x00,0x00,0x15, 0x00,0x00,0x3f, 0x00,0x2a,0x15, 0x00,0x2a,0x3f, 0x2a,0x00,0x15, 0x2a,0x00,0x3f, 0x2a,0x2a,0x15, 0x2a,0x2a,0x3f,
        0x00,0x15,0x00, 0x00,0x15,0x2a, 0x00,0x3f,0x00, 0x00,0x3f,0x2a, 0x2a,0x15,0x00, 0x2a,0x15,0x2a, 0x2a,0x3f,0x00, 0x2a,0x3f,0x2a,
        0x00,0x15,0x15, 0x00,0x15,0x3f, 0x00,0x3f,0x15, 0x00,0x3f,0x3f, 0x2a,0x15,0x15, 0x2a,0x15,0x3f, 0x2a,0x3f,0x15, 0x2a,0x3f,0x3f,
        0x15,0x00,0x00, 0x15,0x00,0x2a, 0x15,0x2a,0x00, 0x15,0x2a,0x2a, 0x3f,0x00,0x00, 0x3f,0x00,0x2a, 0x3f,0x2a,0x00, 0x3f,0x2a,0x2a,
        0x15,0x00,0x15, 0x15,0x00,0x3f, 0x15,0x2a,0x15, 0x15,0x2a,0x3f, 0x3f,0x00,0x15, 0x3f,0x00,0x3f, 0x3f,0x2a,0x15, 0x3f,0x2a,0x3f,
        0x15,0x15,0x00, 0x15,0x15,0x2a, 0x15,0x3f,0x00, 0x15,0x3f,0x2a, 0x3f,0x15,0x00, 0x3f,0x15,0x2a, 0x3f,0x3f,0x00, 0x3f,0x3f,0x2a,
        0x15,0x15,0x15, 0x15,0x15,0x3f, 0x15,0x3f,0x15, 0x15,0x3f,0x3f, 0x3f,0x15,0x15, 0x3f,0x15,0x3f, 0x3f,0x3f,0x15, 0x3f,0x3f,0x3f,
    };

    /// <summary>Loads a mode's registers (the BIOS's mode set).</summary>
    public void SetRegs((byte msr, byte[] seq, byte[] crtc, byte[] gc, byte[] ac) m)
    {
        Msr = m.msr;
        Array.Copy(m.seq, Sr, 5);
        Array.Copy(m.crtc, Cr, 25);
        Array.Copy(m.gc, Gr, 9);
        Array.Copy(m.ac, Ar, 21);
        ArIndex = 0x20; ArFlipFlop = 0; DacMask = 0xff;
    }

    public void LoadFont()
    {
        for (int i = 0; i < 256; i++)
            for (int j = 0; j < 32; j++)
                Ram[i * 32 * 4 + j * 4 + 2] = j < 16 ? VgaFont.Font16[i * 16 + j] : (byte)0;
    }

    /// <summary>The 256-colour default DAC (16 EGA colours, 16 greys, then the hue rings), as the VGA BIOS loads it for mode 13h.</summary>
    public void LoadDefaultDac256()
    {
        for (int i = 0; i < 16; i++) Array.Copy(PalEga, DefaultIdx(i), Palette, i * 3, 3);
        byte[] grey = { 0, 5, 8, 11, 14, 17, 20, 24, 28, 32, 36, 40, 45, 50, 56, 63 };
        for (int i = 0; i < 16; i++) { Palette[(16 + i) * 3] = grey[i]; Palette[(16 + i) * 3 + 1] = grey[i]; Palette[(16 + i) * 3 + 2] = grey[i]; }
        // 9 rings of 24 hues (3 brightness x 3 saturation), like the IBM table
        int[][] levels = { new[] { 63, 31, 0 }, new[] { 63, 47, 31 }, new[] { 63, 55, 45 }, new[] { 28, 14, 0 }, new[] { 28, 21, 14 }, new[] { 28, 24, 20 },
            new[] { 16, 8, 0 }, new[] { 16, 12, 8 }, new[] { 16, 13, 11 } };
        int n = 32;
        foreach (var l in levels)
        {
            int hi = l[0], mid = l[1], lo = l[2];
            int[] ramp = { lo, mid, hi };
            // 24 steps around the colour wheel: blue -> magenta -> red -> yellow -> green -> cyan -> blue
            for (int k = 0; k < 24; k++)
            {
                int seg = k / 4, s = k % 4;
                int up = s == 0 ? lo : s == 1 ? mid : s == 2 ? (mid + hi) / 2 : hi;
                int dn = s == 0 ? hi : s == 1 ? (mid + hi) / 2 : s == 2 ? mid : lo;
                int r, g, b;
                switch (seg)
                {
                    case 0: r = up; g = lo; b = hi; break;
                    case 1: r = hi; g = lo; b = dn; break;
                    case 2: r = hi; g = up; b = lo; break;
                    case 3: r = dn; g = hi; b = lo; break;
                    case 4: r = lo; g = hi; b = up; break;
                    default: r = lo; g = dn; b = hi; break;
                }
                if (n < 248) { Palette[n * 3] = (byte)r; Palette[n * 3 + 1] = (byte)g; Palette[n * 3 + 2] = (byte)b; }
                n++;
            }
        }
        for (; n < 256; n++) { Palette[n * 3] = 0; Palette[n * 3 + 1] = 0; Palette[n * 3 + 2] = 0; }
    }
    private static int DefaultIdx(int i) => (new[] { 0, 1, 2, 3, 4, 5, 20, 7, 56, 57, 58, 59, 60, 61, 62, 63 })[i] * 3;

    public void LoadEgaDac() { Array.Copy(PalEga, Palette, PalEga.Length); }

    private void InitMode3Defaults()
    {
        SetRegs(Mode3);
        LoadEgaDac();
        for (int i = 0; i < RamSize / 4; i++) { Ram[i * 4] = 0x20; Ram[i * 4 + 1] = 0x07; }
        LoadFont();
    }

    // ================= the picture =================

    private static int C6(int v) => (v << 2) | (v >> 4);
    public int Dac(int i) => unchecked((int)0xff000000) | (C6(Palette[i * 3] & 63) << 16) | (C6(Palette[i * 3 + 1] & 63) << 8) | C6(Palette[i * 3 + 2] & 63);

    private void Palette16(int[] pal)
    {
        for (int i = 0; i < 16; i++)
        {
            int v = Ar[i];
            v = (Ar[0x10] & 0x80) != 0 ? ((Ar[0x14] & 0xf) << 4) | (v & 0xf) : ((Ar[0x14] & 0xc) << 4) | (v & 0x3f);
            pal[i] = Dac(v);
        }
    }

    private readonly int[] _pal = new int[256];

    /// <summary>Draws the current picture into <see cref="Frame"/> (once per frame, at vertical retrace).</summary>
    public void Render()
    {
        FrameCount++;
        if (Picture != null) { Picture.Render(); return; }
        if ((Ar[0x10] & 1) == 0 || (Gr[6] & 1) == 0) RenderText(); else RenderGraphics();
    }

    /// <summary>Sizes <see cref="Frame"/> (clamped to 8..1024 x 8..768).</summary>
    public void Ensure(int w, int h)
    {
        if (w < 8) w = 8; if (h < 8) h = 8;
        if (w > 1024) w = 1024; if (h > 768) h = 768;
        Width = w; Height = h;
        if (Frame.Length != w * h) Frame = new int[w * h];
    }

    private void RenderText()
    {
        int cols = Cr[1] + 1;
        int cheight = (Cr[9] & 0x1f) + 1;
        int dots = (Sr[1] & 1) != 0 ? 8 : 9;
        int vdisp = (Cr[0x12] | ((Cr[7] & 2) << 7) | ((Cr[7] & 0x40) << 3)) + 1;
        if ((Cr[9] & 0x80) != 0) vdisp /= 2;
        int rows = Math.Max(1, vdisp / cheight);
        int w = cols * dots, h = rows * cheight;
        Ensure(w, h);
        Palette16(_pal);
        int start = Cr[0x0d] | (Cr[0x0c] << 8);
        int offset = Cr[0x13] * 2;
        int cursor = (Cr[0x0f] | (Cr[0x0e] << 8)) - start;
        int cs = Cr[0x0a] & 0x1f, ce = Cr[0x0b] & 0x1f;
        bool cursorOn = (Cr[0x0a] & 0x20) == 0 && (FrameCount & 16) != 0;
        bool blink = (Ar[0x10] & 8) != 0;
        bool blinkPhase = (FrameCount & 32) != 0;
        // character map A from sequencer register 3 (bits 5, 3-2)
        int mapA = ((Sr[3] >> 2) & 3) | ((Sr[3] >> 3) & 4);
        int fontBase = (mapA switch { 0 => 0, 1 => 0x4000, 2 => 0x8000, 3 => 0xc000, 4 => 0x2000, 5 => 0x6000, 6 => 0xa000, _ => 0xe000 }) * 4;
        bool lineGraphics = (Ar[0x10] & 4) != 0;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int cell = (start + r * offset + c) & 0xffff;
                int a = cell * 4;        // odd/even: the cell's character (CPU byte 2n) is plane 0 at address 2n -> index 4n, its attribute plane 1
                if (a + 1 >= RamSize) continue;
                int ch = Ram[a], at = Ram[a + 1];
                int fg = at & 0xf, bg = at >> 4;
                if (blink) { bg &= 7; if ((at & 0x80) != 0 && blinkPhase) fg = bg; }
                int fgc = _pal[fg], bgc = _pal[bg];
                bool isCursor = cursorOn && cursor == r * offset + c;
                for (int y = 0; y < cheight; y++)
                {
                    int bits = y < 32 ? Ram[fontBase + ch * 32 * 4 + y * 4 + 2] : 0;
                    if (isCursor && y >= cs && y <= ce) bits = 0xff;
                    int o = (r * cheight + y) * w + c * dots;
                    for (int x = 0; x < 8; x++) Frame[o + x] = (bits & (0x80 >> x)) != 0 ? fgc : bgc;
                    if (dots == 9)
                        Frame[o + 8] = lineGraphics && ch >= 0xc0 && ch <= 0xdf && (bits & 1) != 0 ? fgc : (isCursor && y >= cs && y <= ce ? fgc : bgc);
                }
            }
    }

    private void RenderGraphics()
    {
        int w = (Cr[1] + 1) * 8;
        int h = (Cr[0x12] | ((Cr[7] & 2) << 7) | ((Cr[7] & 0x40) << 3)) + 1;
        int shift = (Gr[5] >> 5) & 3;
        int doubleScan = Cr[9] >> 7;
        int multiScan = doubleScan == 0 ? (Cr[9] & 0x1f) : 1;
        int multiRun = multiScan;
        uint startAddr = (uint)(Cr[0x0d] | (Cr[0x0c] << 8));
        uint lineOffset = (uint)Cr[0x13] << 3;
        uint addr1 = 4 * startAddr;
        int xdiv = 1;
        if (shift <= 1) { Palette16(_pal); if ((Sr[1] & 8) != 0) { xdiv = 2; w *= 2; } }
        else { for (int i = 0; i < 256; i++) _pal[i] = Dac(i); xdiv = 2; }
        Ensure(w, h);
        w = Width; h = Height;
        int planeMask = Ar[0x12];
        int pan = shift >= 2 ? (Ar[0x13] >> 1) & 3 : Ar[0x13] & 7;
        int lineCompare = Cr[0x18] | ((Cr[7] & 0x10) << 4) | ((Cr[9] & 0x40) << 3);
        int splitAfter = (lineCompare + 1) / (multiScan + 1) * (multiScan + 1) - 1;
        if (splitAfter < 0) { addr1 = 0; if ((Ar[0x10] & 0x20) != 0) pan = 0; }
        uint lineBytes = shift <= 1 ? (uint)(4 * ((((w - 1) / xdiv + pan) >> 3) + 1)) : (uint)((w - 1) / xdiv + pan + 1);
        int y1 = 0;
        var vram = Ram;
        for (int y = 0; y < h; y++)
        {
            uint addr = addr1;
            if ((Cr[0x17] & 1) == 0) { int sh = 14 + ((Cr[0x17] >> 6) & 1); addr = (addr & ~(1u << sh)) | ((uint)(y1 & 1) << sh); }
            if ((Cr[0x17] & 2) == 0) addr = (addr & ~0x8000u) | ((uint)(y1 & 2) << 14);
            addr &= 0x3ffff;
            int o = y * w;
            if (addr >= RamSize || lineBytes > RamSize - addr) Array.Fill(Frame, unchecked((int)0xff000000), o, w);
            else if (shift == 0 && xdiv <= 2)
            {   // planar 16 colours, a byte of each plane (8 pixels) at a time: the same pixels as the general loop below
                var pal = _pal; var fr = Frame;
                int x = 0, x1 = pan, pm = planeMask & 0xf;
                while (x < w)
                {
                    uint b = addr + 4 * (uint)(x1 >> 3);
                    int p0 = vram[b], p1 = 0, p2 = 0, p3 = 0;
                    if (planeMask != 1) { p1 = vram[b + 1]; p2 = vram[b + 2]; p3 = vram[b + 3]; }
                    for (int bit = 7 - (x1 & 7); bit >= 0 && x < w; bit--, x1++)
                    {
                        int k = (((p0 >> bit) & 1) | (((p1 >> bit) & 1) << 1) | (((p2 >> bit) & 1) << 2) | (((p3 >> bit) & 1) << 3)) & pm;
                        int c = pal[k];
                        fr[o + x++] = c;
                        if (xdiv == 2 && x < w) fr[o + x++] = c;
                    }
                }
            }
            else if (shift >= 2 && xdiv == 2)
            {   // 256 colours, each pixel twice
                var pal = _pal; var fr = Frame;
                for (int x = 0; x < w; x += 2)
                {
                    int c = pal[vram[addr + (uint)(x / 2 + pan)]];
                    fr[o + x] = c;
                    if (x + 1 < w) fr[o + x + 1] = c;
                }
            }
            else
                for (int x = 0; x < w; x++)
                {
                    int x1 = x / xdiv + pan;
                    int k;
                    if (shift == 0)
                    {
                        uint b = addr + 4 * (uint)(x1 >> 3);
                        int bit = 7 - (x1 & 7);
                        k = (vram[b] >> bit) & 1;
                        if (planeMask != 1)
                            k |= (((vram[b + 1] >> bit) & 1) << 1) | (((vram[b + 2] >> bit) & 1) << 2) | (((vram[b + 3] >> bit) & 1) << 3);
                        k &= planeMask & 0xf;
                    }
                    else if (shift == 1) k = (vram[addr + 4 * (x1 >> 3) + ((x1 & 4) >> 2)] >> (6 - 2 * (x1 & 3))) & 3;
                    else k = vram[addr + x1];
                    Frame[o + x] = _pal[k];
                }
            if (multiRun == 0)
            {
                int mask = (Cr[0x17] & 3) ^ 3;
                if ((y1 & mask) == mask) addr1 += lineOffset;
                y1++;
                multiRun = multiScan;
            }
            else multiRun--;
            if (y == splitAfter) { addr1 = 0; y1 = 0; multiRun = multiScan; if ((Ar[0x10] & 0x20) != 0) pan = 0; }
        }
    }

    // ================= save state =================

    public void Save(BinaryWriter w)
    {
        w.Write(Ram); w.Write(Sr); w.Write(Gr); w.Write(Cr); w.Write(Ar); w.Write(Palette); w.Write(_dacCache);
        w.Write(new[] { Msr, SrIndex, GrIndex, CrIndex, ArIndex, ArFlipFlop, DacMask, DacState, DacReadIndex, DacWriteIndex, DacSub, Fcr, St00 });
        w.Write(_latch); w.Write(FrameCount);
    }
    public void Load(BinaryReader r)
    {
        r.Read(Ram, 0, Ram.Length); r.Read(Sr, 0, 8); r.Read(Gr, 0, 16); r.Read(Cr, 0, 256); r.Read(Ar, 0, 21); r.Read(Palette, 0, 768); r.Read(_dacCache, 0, 3);
        var b = r.ReadBytes(13);
        Msr = b[0]; SrIndex = b[1]; GrIndex = b[2]; CrIndex = b[3]; ArIndex = b[4]; ArFlipFlop = b[5]; DacMask = b[6]; DacState = b[7];
        DacReadIndex = b[8]; DacWriteIndex = b[9]; DacSub = b[10]; Fcr = b[11]; St00 = b[12];
        _latch = r.ReadUInt32(); FrameCount = r.ReadInt32();
    }
}
