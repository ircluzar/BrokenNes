// S3 Trio64 (86C764, PCI 5333:8811): the SVGA extensions on top of the Vga class (which stays the card's VGA core). Written for
// BrokenNes from the public register descriptions of the S3 Trio32/Trio64 data book and the 8514/A drawing engine; no code taken
// from another emulator. The video BIOS (INT 10h AH=4Fh, VBE 2.0) is ours too: PcMachine.Vbe.cs.
//
// What is modelled:
//  - the S3 register locks (CR38 = 48h, CR39 = A5h, SR08 = 06h), the chip ID (CR2D/2E = 88h/11h, CR30 = E1h), memory size (CR36: 4 MB)
//  - enhanced (linear chain-4) memory: CR31 bit 3; the 64 KB bank at A0000 (CR35 + CR51 bits 3-2, or CR6A)
//  - the linear framebuffer (CR58 bit 4, base CR59/5A or PCI BAR 0), A0000 disabled while it is on
//  - the extended CRTC: width/height/offset/start overflow bits (CR5D, CR5E, CR51, CR31, CR69), pixel format CR67: 8, 15, 16, 32 bpp
//  - the hardware cursor (CR45-4F, 64x64 AND/XOR in video memory)
//  - the drawing engine (ports xxE8, or old-style MMIO at A8000+port when CR53 bit 4): rectangle fill, BitBLT, line, pattern fill,
//    the 16 mixes, colour sources (background/foreground/CPU data/display memory), mono expansion from CPU data or display memory,
//    scissors, write mask, CPU-to-screen image transfer (port E2E8 / MMIO A0000-A7FFF); screen-to-CPU reads are not done
//  - the PCI configuration space (vendor/device, class 030000h, BAR 0 = 64 MB window); the board wires it to a PCI bus when it has one
// Not modelled: the RAMDAC's 8-bit mode, the PLL clocks (the picture timing stays the VGA's 70 Hz), streams processor, video overlay.

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed class S3Trio64
{
    /// <summary>The Trio64's 4 MB (also the VBOXVGA default).</summary>
    public const int TrioVramBytes = 4 * 1024 * 1024;
    /// <summary>This card's video memory: 4 MB, or 8/16 MB on a VBOXVGA machine with <see cref="PcConfig.VramMB"/> (a power of two: BAR 0 is sized to it).</summary>
    public int VramBytes => _v.Ram.Length;
    public const string Id = "S3TRIO64";
    private readonly Vga _v;
    public readonly byte[] SrExt = new byte[0x20];        // SR08-SR1F (SR00-07 stay the VGA's)
    public readonly byte[] PciConfig = new byte[256];
    public uint LfbBase = 0xE0000000;
    public int Bank;                                      // 64 KB units
    public int VbeMode;                                   // the VBE mode the BIOS set (0: a VGA mode)

    public S3Trio64(Vga v, bool vbox = false, bool virge = false) { _v = v; VBox = vbox; IsVirge = virge; if (virge) Virge = new S3Virge(this, v); Reset(); }

    /// <summary>The same S3 core as the ViRGE (86C325, PCI 5333:5631): chip id, no 8514 engine use, the S3d engine behind the new MMIO window (x86/S3Virge.cs).</summary>
    public readonly bool IsVirge;
    public readonly S3Virge? Virge;
    public const string VirgeId = "S3VIRGE";
    public Vga Core => _v;

    // ================= VirtualBox VGA / Bochs DISPI (the WIN98-P166-WIDE preset) =================
    // An optional identity for the same card: PCI 80EE:BEEF and the Bochs VBE DISPI registers (index port 1CEh, data 1CFh), what
    // the open-source boxv9x Win9x driver programs: any width x height at 8/15/16/24/32 bpp in the linear framebuffer. Our own
    // implementation from the public Bochs VBE register list; the VGA core and the S3 registers stay (unused by that driver).
    public readonly bool VBox;
    public const string VBoxId = "VBOXVGA";
    /// <summary>The card id in a savestate: the Trio64, or VBOXVGA (or S3VIRGE; + "-16MB" etc. when not 4 MB, so states never load into a card of another memory size).</summary>
    public string CardId => IsVirge ? VirgeId : !VBox ? Id : VramBytes == TrioVramBytes ? VBoxId : $"{VBoxId}-{VramBytes >> 20}MB";
    public readonly ushort[] Dispi = new ushort[16];
    public int DispiIndex;
    public bool DispiOn => VBox && (Dispi[4] & 1) != 0 && !DispiDiagOff;
    private static readonly bool DispiDiagOff = Environment.GetEnvironmentVariable("X86_DISPIOFF") == "1";   // diagnosis: show the VGA picture
    public static bool IsDispiPort(int port) => port is 0x1ce or 0x1cf or 0x1d0;
    public ushort DispiRead() => DispiIndex switch
    {
        0 => 0xb0c4,                                                    // VBE_DISPI_ID4
        0x0a => (ushort)(VramBytes / 65536),                             // video memory in 64 KB units (VirtualBox)
        0x0b => (ushort)(LfbBase >> 16),                                 // VBE_DISPI_INDEX_FB_BASE_HI (VirtualBox): the LFB's address (boxv9x reads it; 0 = its E000 default)
        4 => (ushort)(Dispi[4] & 0xc3),
        _ => DispiIndex < 16 ? Dispi[DispiIndex] : (ushort)0,
    };
    public void DispiWrite(ushort v)
    {
        int i = DispiIndex;
        if (i >= 16 || i == 0 || i == 0x0a || i == 0x0b) return;
        if (i == 3 && v is not (4 or 8 or 15 or 16 or 24 or 32)) return;
        Dispi[i] = v;
        if (i == 1 || i == 3) Dispi[6] = Dispi[1];                       // a new width resets the virtual width
        if (i == 2) Dispi[7] = Dispi[2];
        if (i == 4 && (v & 1) != 0)
        {
            if (Dispi[6] < Dispi[1]) Dispi[6] = Dispi[1];
            if ((v & 0x80) == 0) Array.Clear(_v.Ram, 0, Math.Min(VramBytes, Dispi[6] * Dispi[2] * DispiBpp));
            _shadowFrame = null;
        }
        if (i == 5) Bank = v;
        SyncDac8();
    }
    /// <summary>The 8BIT_DAC enable bit switches the VGA DAC to 8-bit entries while DISPI is on.</summary>
    public void SyncDac8() => _v.Dac8 = DispiOn && (Dispi[4] & 0x20) != 0;
    private int DispiBpp => Dispi[3] switch { 15 or 16 => 2, 24 => 3, 32 => 4, _ => 1 };

    public void Reset()
    {
        Array.Clear(SrExt);
        Array.Clear(PciConfig);
        if (VBox) { W16(0x00, 0x80ee); W16(0x02, 0xbeef); } else { W16(0x00, 0x5333); W16(0x02, IsVirge ? 0x5631 : 0x8811); }   // vendor S3, device Trio32/64 or ViRGE (or VirtualBox VGA)
        Array.Clear(Dispi); DispiIndex = 0; Dispi[3] = 8; _v.Dac8 = false;
        W16(0x04, 0x0003);                                // I/O + memory decode on
        PciConfig[0x08] = 0x00; PciConfig[0x0b] = 0x03;  // class 030000: VGA-compatible display
        PciConfig[0x0e] = 0x00;
        LfbBase = 0xE0000000;
        W32(0x10, LfbBase | (VBox ? 8u : 0u));            // (VBOXVGA: prefetchable, as VirtualBox reports it)
        PciConfig[0x3d] = 1;                              // INTA#
        Bank = 0; VbeMode = 0;
        var cr = _v.Cr;
        for (int i = 0x2d; i < 0x100; i++) cr[i] = 0;
        cr[0x2d] = 0x88; cr[0x2e] = 0x11; cr[0x2f] = 0x00; cr[0x30] = 0xe1;
        if (IsVirge) { cr[0x2d] = 0x56; cr[0x2e] = 0x31; cr[0x2f] = 0x06; PciConfig[0x08] = 0x06; }
        cr[0x36] = 0x19;                                  // 4 MB, PCI
        cr[0x37] = 0xff;
        cr[0x59] = (byte)(LfbBase >> 24); cr[0x5a] = (byte)(LfbBase >> 16);
        ResetEngine();
        Virge?.Reset();
    }

    private void W16(int o, int v) { PciConfig[o] = (byte)v; PciConfig[o + 1] = (byte)(v >> 8); }
    private void W32(int o, uint v) { W16(o, (int)(v & 0xffff)); W16(o + 2, (int)(v >> 16)); }

    // ================= PCI configuration space =================

    public uint PciRead(int reg) => (uint)(PciConfig[reg & 0xfc] | PciConfig[(reg & 0xfc) + 1] << 8 | PciConfig[(reg & 0xfc) + 2] << 16 | PciConfig[(reg & 0xfc) + 3] << 24);
    public void PciWrite(int reg, uint v)
    {
        reg &= 0xfc;
        switch (reg)
        {
            case 0x04: W16(0x04, (int)(v & 0x0023)); break;
            case 0x10: LfbBase = v & (VBox ? ~(uint)(VramBytes - 1) : 0xFC000000u);   // (VBOXVGA: BAR 0 = the whole video memory (4/8/16 MB), like VirtualBox; boxv9x takes the devnode's largest memory range)
                 W32(0x10, LfbBase | (VBox ? 8u : 0u)); _v.Cr[0x59] = (byte)(LfbBase >> 24); _v.Cr[0x5a] = (byte)(LfbBase >> 16); break;
            case 0x30: W32(0x30, 0); break;               // no ROM BAR: the video BIOS is the board's own
            case 0x3c: PciConfig[0x3c] = (byte)v; break;
        }
    }

    // ================= registers (called by Vga for indexes it does not own) =================

    private bool S3Unlocked => _v.Cr[0x38] == 0x48;
    private bool SysUnlocked => (_v.Cr[0x39] & 0xf0) == 0xa0;

    public byte SrRead(int i) => i is >= 8 and < 0x20 ? SrExt[i] : (byte)0;
    public void SrWrite(int i, byte v)
    {
        if (i == 8) { SrExt[8] = v; return; }
        if (i is > 8 and < 0x20 && (SrExt[8] & 0x0f) == 0x06) SrExt[i] = v;
    }

    /// <summary>A CRTC write at index >= 2Dh: true when S3 handled (or dropped) it.</summary>
    public bool CrWrite(int i, byte v)
    {
        var cr = _v.Cr;
        switch (i)
        {
            case 0x2d: case 0x2e: case 0x2f: case 0x30: case 0x36: return true;   // read-only
            case 0x38: case 0x39: cr[i] = v; return true;
        }
        if (i < 0x40) { if (!S3Unlocked) return true; }
        else if (!SysUnlocked) return true;
        cr[i] = v;
        switch (i)
        {
            // CR6A (when non-zero) overrides the old CR35 / CR51 bank fields; a zero CR6A leaves them in charge
            case 0x35: case 0x51: case 0x6a:
                Bank = (cr[0x6a] & 0x3f) != 0 ? cr[0x6a] & 0x3f : (cr[0x35] & 0xf) | ((cr[0x51] & 0x0c) << 2); break;
            case 0x58: case 0x59: case 0x5a: LfbBase = (uint)(cr[0x59] << 24 | cr[0x5a] << 16) & ~(uint)(LfbSize - 1); if (i != 0x58) W32(0x10, LfbBase & 0xFC000000u); break;
        }
        return true;
    }

    // ================= memory =================

    public bool Enhanced => (_v.Cr[0x31] & 0x08) != 0;
    /// <summary>CR31 bit 3 (enhanced memory mapping) changes CPU access only together with chain 4 (SR4 bit 3): in a planar/odd-even VGA
    /// mode (a full-screen DOS box, Windows' text-mode screens) the A0000-BFFFF window stays the VGA's (the text was swallowed: garbage).</summary>
    private bool EnhancedMap => Enhanced && (_v.Sr[4] & 0x08) != 0;
    public bool LinearOn => (_v.Cr[0x58] & 0x10) != 0 || DispiOn || (Virge != null && (Virge.AdvFunc & 0x10) != 0);
    /// <summary>CR58 bits 1-0: the linear window's size. The 64 KB one (at A0000) is still banked.</summary>
    public int LfbSize => DispiOn ? VramBytes : (_v.Cr[0x58] & 3) switch { 0 => 0x10000, 1 => 0x100000, 2 => 0x200000, _ => VBox ? VramBytes : 0x400000 };   // (VBOXVGA: the 4 MB setting opens all 8/16 MB, for the wide VBE modes)
    private bool Window64K => !DispiOn && LinearOn && LfbSize == 0x10000;
    public long VgaMmioWrites;   // diagnostics
    public bool OldMmio => ((_v.Cr[0x53] & 0x10) != 0 || (Virge != null && (Virge.AdvFunc & 0x20) != 0)) && (_v.Sr[4] & 0x08) != 0;   // (not in a planar VGA mode: Windows' VDD loads the text font through A0000 with CR53 still set)
    public int BytesPerPixel => DispiOn ? DispiBpp : (_v.Cr[0x67] >> 4) switch { 0x3 or 0x5 => 2, 0x7 when IsVirge => 3, 0xd => 4, _ => 1 };

    /// <summary>A0000-BFFFF access the S3 takes over (MMIO, enhanced banking, the window off while linear is on); false: the VGA's.</summary>
    public bool MemWrite(uint addr, byte v)
    {
        uint off = addr - 0xa0000;
        if (IsVirge && OldMmio && off < 0x10000) { Virge!.Write(off, v, 8); return true; }
        if (OldMmio && off < 0x10000)
        {
            if (off < 0x8000) PixelData(v, 8);
            else if ((addr & 0xffe0) == 0x83c0) { VgaMmioWrites++; _v.Out((int)(addr & 0xffff) - 0x8000, v); }   // the VGA registers 3C0h-3DFh at A83C0h (the DAC: 8-bit palettes)
            else EngineOut8((int)(addr & 0xffff), v);
            return true;
        }
        if (DispiOn && off < 0x10000) { BankWrites++; if ((uint)(Bank << 16) + off < VramBytes) _v.Ram[(Bank << 16) + (int)off] = v; return true; }   // DISPI: A0000 is a 64 KB bank of the linear memory (any VGA map)
        if (!EnhancedMap && !Window64K) return false;
        if (LinearOn && !Window64K) return true;
        if (off >= 0x10000) return true;
        uint a = (uint)(Bank << 16) + off;
        if (a < VramBytes) _v.Ram[a] = v;
        return true;
    }

    public bool MemRead(uint addr, out byte v)
    {
        uint off = addr - 0xa0000;
        v = 0xff;
        if (IsVirge && OldMmio && off < 0x10000) { v = (byte)(Virge!.Read32(off & ~3u) >> (int)(8 * (off & 3))); return true; }
        if (OldMmio && off < 0x10000)
        {
            if ((addr & 0xffe0) == 0x83c0) v = _v.In((int)(addr & 0xffff) - 0x8000);
            else if (off >= 0x8000) v = (byte)EngineIn((int)(addr & 0xfffe), (addr & 1) != 0);
            return true;
        }
        if (DispiOn && off < 0x10000) { if ((uint)(Bank << 16) + off < VramBytes) v = _v.Ram[(Bank << 16) + (int)off]; return true; }
        if (!EnhancedMap && !Window64K) return false;
        if ((LinearOn && !Window64K) || off >= 0x10000) return true;
        uint a = (uint)(Bank << 16) + off;
        if (a < VramBytes) v = _v.Ram[a];
        return true;
    }

    /// <summary>MMIO 16/32-bit writes to the engine and the pixel transfer window keep their width (the transfer width matters).</summary>
    public bool MmioWrite(uint addr, uint v, int bits)
    {
        if (!OldMmio) return false;
        uint off = addr - 0xa0000;
        if (off >= 0x10000) return false;
        if (IsVirge) { Virge!.Write(off, v, bits); return true; }
        if (off < 0x8000) { PixelData(v, bits); return true; }
        int port = (int)(addr & 0xffff);
        if ((port & 0xffe0) == 0x83c0) { VgaMmioWrites++; for (int i = 0; i < bits / 8; i++) _v.Out(port - 0x8000 + i, (byte)(v >> (8 * i))); return true; }   // VGA registers by MMIO, byte by byte
        if (bits == 32) { EngineOut16(port, (ushort)v); EngineOut16(port + 2, (ushort)(v >> 16)); }
        else EngineOut16(port, (ushort)v);
        return true;
    }

    public long LfbWrites, BankWrites;
    /// <summary>ViRGE: CR53 bit 3 maps the new MMIO (64 KB) at the PCI window's base + 16 MB.</summary>
    public bool NewMmio => IsVirge && (_v.Cr[0x53] & 0x08) != 0;
    private uint MmioBase => (uint)(_v.Cr[0x59] << 24 | _v.Cr[0x5a] << 16) + 0x1000000;
    private bool InMmio(uint a) => NewMmio && a - MmioBase < 0x10000;
    /// <summary>Only the frame buffer RAM (not the ViRGE new MMIO window): the bulk REP span path uses this.</summary>
    public bool InLfbRam(uint a) => LinearOn && !Window64K && a - LfbBase < Math.Min(VramBytes, LfbSize);
    public bool InLfb(uint a) => (LinearOn && !Window64K && a - LfbBase < Math.Min(VramBytes, LfbSize)) || InMmio(a);
    public byte LfbRead8(uint a) => InMmio(a) ? (byte)(Virge!.Read32((a - MmioBase) & ~3u) >> (int)(8 * (a & 3))) : _v.Ram[a - LfbBase];
    public Span<byte> LfbSpan(uint a, int bytes) => _v.Ram.AsSpan((int)(a - LfbBase), bytes);
    public void LfbWrite8(uint a, byte v) { if (InMmio(a)) Virge!.Write(a - MmioBase, v, 8); else { LfbWrites++; _v.Ram[a - LfbBase] = v; } }
    public void LfbWrite16(uint a, ushort v) { if (InMmio(a)) Virge!.Write(a - MmioBase, v, 16); else { LfbWrite8(a, (byte)v); LfbWrite8(a + 1, (byte)(v >> 8)); } }
    public uint LfbRead32(uint a) { if (InMmio(a)) return (a & 3) == 0 ? Virge!.Read32(a - MmioBase) : (uint)(LfbRead8(a) | LfbRead8(a + 1) << 8 | LfbRead8(a + 2) << 16 | LfbRead8(a + 3) << 24); uint o = a - LfbBase; return o <= VramBytes - 4 ? BitConverter.ToUInt32(_v.Ram, (int)o) : LfbRead8(a); }
    public void LfbWrite32(uint a, uint v)
    {
        if (InMmio(a)) { Virge!.Write(a - MmioBase, v, 32); return; }
        LfbWrites++;
        uint o = a - LfbBase;
        if (o <= VramBytes - 4) BitConverter.TryWriteBytes(_v.Ram.AsSpan((int)o), v);
        else for (int i = 0; i < 4 && o + i < VramBytes; i++) _v.Ram[o + i] = (byte)(v >> (8 * i));
    }

    /// <summary>Back to plain VGA (a standard BIOS mode set).</summary>
    public void LeaveEnhanced()
    {
        Dispi[4] = 0; SyncDac8();                         // a BIOS mode set also turns the DISPI mode off (as the Bochs VGA BIOS does)
        var cr = _v.Cr;
        cr[0x31] = 0; cr[0x35] = 0; cr[0x51] = 0; cr[0x53] = 0; cr[0x58] = 0; cr[0x5d] = 0; cr[0x5e] = 0; cr[0x67] = 0; cr[0x69] = 0; cr[0x6a] = 0;
        cr[0x45] = 0;
        Bank = 0; VbeMode = 0;
        ResetEngine();
    }

    // ================= the picture =================

    public int Width => DispiOn ? Math.Max(8, (int)Dispi[1]) : ((_v.Cr[1] + 1) | ((_v.Cr[0x5d] & 2) << 7)) * 8;
    /// <summary>Picture lines: the display-end count divided by the scan-line repeat (CR09 max scan line + 1, x2 with double scan).
    /// Low-res enhanced modes (Half-Life's 320x240x16 through DirectDraw: CR09 = 41h) send each line twice; the picture is the 240 distinct lines.</summary>
    public int Height => DispiOn ? Math.Max(8, (int)Dispi[2]) : Math.Max(1, ((_v.Cr[0x12] | ((_v.Cr[7] & 2) << 7) | ((_v.Cr[7] & 0x40) << 3) | ((_v.Cr[0x5e] & 2) << 9)) + 1)
                                     / (((_v.Cr[9] & 0x1f) + 1) * ((_v.Cr[9] >> 7) + 1)));
    public int Stride => DispiOn ? Dispi[6] * DispiBpp : Virge != null && Virge.StreamsOn && Virge.PriStride != 0 ? Virge.PriStride : (_v.Cr[0x13] | ((_v.Cr[0x51] & 0x30) << 4)) * 8;
    public int StartByte => DispiOn ? (Dispi[9] * Dispi[6] + Dispi[8]) * DispiBpp : Virge != null && Virge.StreamsOn ? (int)((Virge.BufferCtrl & 1) != 0 ? Virge.PriFb1 : Virge.PriFb0) : ((_v.Cr[0x0d] | (_v.Cr[0x0c] << 8) | ((_v.Cr[0x31] & 0x30) << 12) | ((_v.Cr[0x69] & 0x1f) << 16))) * 4;

    private readonly int[] _pal = new int[256];

    // dirty lines: the video memory bytes each picture line was last drawn from; a line whose bytes are unchanged (same mode, start,
    // stride, palette, the frame not drawn by another path since) keeps its pixels. The picture is the same as drawing every line.
    private byte[] _shadow = Array.Empty<byte>();
    private readonly int[] _shadowPal = new int[256];
    private int[]? _shadowFrame;
    private long _shadowSerial = -10, _shadowKey;
    private int _curTop = -1, _curBot = -1;   // the lines the hardware cursor covered in the last picture
    public void InvalidateLines() => _shadowFrame = null;

    public void Render()
    {
        int w = Width, h = Height, stride = Stride, bpp = BytesPerPixel;
        _v.Ensure(w, h);
        w = _v.Width; h = _v.Height;
        var fr = _v.Frame; var ram = _v.Ram;
        int start = StartByte;
        if (bpp == 1) for (int i = 0; i < 256; i++) _pal[i] = _v.Dac(i & _v.DacMask);
        bool r555k = DispiOn ? Dispi[3] == 15 : (_v.Cr[0x67] >> 4) == 0x3;
        long key = ((long)w << 48) ^ ((long)h << 36) ^ ((long)stride << 20) ^ ((long)bpp << 16) ^ (r555k ? 1L << 15 : 0) ^ ((long)start << 22);
        int lineBytes = w * bpp;
        bool valid = _shadowFrame == fr && _shadowSerial == _v.RenderSerial - 1 && _shadowKey == key && _shadow.Length == lineBytes * h
                     && (bpp != 1 || _pal.AsSpan().SequenceEqual(_shadowPal));
        if (!valid)
        {
            if (_shadow.Length != lineBytes * h) _shadow = new byte[lineBytes * h];
            _shadowFrame = fr; _shadowKey = key;
        }
        if (bpp == 1) _pal.CopyTo(_shadowPal, 0);
        _shadowSerial = _v.RenderSerial;
        int cTop = -1, cBot = -1;
        if ((_v.Cr[0x45] & 1) != 0) { int cy = (_v.Cr[0x48] & 7) << 8 | _v.Cr[0x49]; cTop = cy - (_v.Cr[0x4f] & 63); cBot = cTop + 64; }
        for (int y = 0; y < h; y++)
        {
            int src = start + y * stride, o = y * w;
            if (src < 0 || src + w * bpp > VramBytes) { Array.Fill(fr, unchecked((int)0xff000000), o, w); continue; }
            var line = ram.AsSpan(src, lineBytes); var sh = _shadow.AsSpan(y * lineBytes, lineBytes);
            bool cursorLine = (y >= cTop && y < cBot) || (y >= _curTop && y < _curBot);
            if (valid && !cursorLine && line.SequenceEqual(sh)) continue;
            line.CopyTo(sh);
            switch (bpp)
            {
                case 1: for (int x = 0; x < w; x++) fr[o + x] = _pal[ram[src + x]]; break;
                case 2:
                    bool r555 = r555k;
                    for (int x = 0; x < w; x++)
                    {
                        int p = ram[src + 2 * x] | ram[src + 2 * x + 1] << 8, r, g, b;
                        if (r555) { r = (p >> 10) & 31; g = (p >> 5) & 31; b = p & 31; r = r << 3 | r >> 2; g = g << 3 | g >> 2; }
                        else { r = (p >> 11) & 31; g = (p >> 5) & 63; b = p & 31; r = r << 3 | r >> 2; g = g << 2 | g >> 4; }
                        b = b << 3 | b >> 2;
                        fr[o + x] = unchecked((int)0xff000000) | r << 16 | g << 8 | b;
                    }
                    break;
                case 3: for (int x = 0; x < w; x++) fr[o + x] = unchecked((int)0xff000000) | ram[src + 3 * x] | ram[src + 3 * x + 1] << 8 | ram[src + 3 * x + 2] << 16; break;   // 24 bpp (DISPI, ViRGE)
                default: for (int x = 0; x < w; x++) fr[o + x] = unchecked((int)0xff000000) | (BitConverter.ToInt32(ram, src + 4 * x) & 0xffffff); break;
            }
        }
        _curTop = cTop; _curBot = cBot;
        DrawCursor(w, h, bpp);
    }

    private void DrawCursor(int w, int h, int bpp)
    {
        var cr = _v.Cr;
        if ((cr[0x45] & 1) == 0) return;
        int cx = ((cr[0x46] & 7) << 8 | cr[0x47]), cy = ((cr[0x48] & 7) << 8 | cr[0x49]);
        int ox = cr[0x4e] & 63, oy = cr[0x4f] & 63;
        int baseAddr = ((cr[0x4c] & 0x0f) << 8 | cr[0x4d]) * 1024;
        int fg, bg;
        if (bpp == 1) { fg = _pal[cr[0x4a]]; bg = _pal[cr[0x4b]]; }
        else { fg = unchecked((int)0xffffffff); bg = unchecked((int)0xff000000); }
        var fr = _v.Frame; var ram = _v.Ram;
        for (int y = oy; y < 64; y++)
        {
            int py = cy + y - oy;
            if (py < 0 || py >= h) continue;
            int line = baseAddr + y * 16;
            for (int x = ox; x < 64; x++)
            {
                int px = cx + x - ox;
                if (px < 0 || px >= w) continue;
                int grp = line + (x >> 4) * 4;   // per 16 pixels: AND word, then XOR word
                if (grp + 3 >= VramBytes) return;
                int bit = 15 - (x & 15);
                int andW = ram[grp] << 8 | ram[grp + 1], xorW = ram[grp + 2] << 8 | ram[grp + 3];
                bool a = ((andW >> bit) & 1) != 0, xo = ((xorW >> bit) & 1) != 0;
                int i = py * w + px;
                if (!a) fr[i] = xo ? fg : bg;
                else if (xo) fr[i] ^= 0x00ffffff;
            }
        }
    }

    // ================= the drawing engine (8514/A-compatible) =================

    private int _curX, _curY, _destX, _destY, _errTerm, _majPcnt, _minPcnt, _cmd;
    private int _scTop, _scLeft, _scBottom, _scRight, _pixCtl;
    private uint _bkColor, _fgColor, _wrtMask = 0xffffffff, _rdMask = 0xffffffff, _colorCmp;
    private int _bkMix, _fgMix, _multiMisc;
    private ushort _subsysCtl, _advCtl;
    // a command waiting for CPU data: where it is
    private bool _waitCpu;
    private int _px, _py, _pw, _ph, _cntX, _cntY;
    private bool _hiWordNext;

    private void ResetEngine()
    {
        _curX = _curY = _destX = _destY = _errTerm = _majPcnt = _minPcnt = _cmd = 0;
        _scTop = _scLeft = 0; _scBottom = _scRight = 0xfff; _pixCtl = 0;
        _bkColor = _fgColor = 0; _wrtMask = _rdMask = 0xffffffff; _colorCmp = 0;
        _bkMix = _fgMix = 0; _multiMisc = 0; _subsysCtl = _advCtl = 0;
        _waitCpu = false; _hiWordNext = false;
    }

    public static bool IsEnginePort(int port) => port >= 0x42e8 && (port & 0x3fe) == 0x2e8;

    public void EngineOut8(int port, byte v)
    {
        // an 8-bit access: low or high byte of the 16-bit register
        int reg = port & 0xfffe;
        int cur = EngineIn(reg, false) | EngineIn(reg, true) << 8;
        if (reg == 0xe2e8) { PixelData(v, 8); return; }
        int nv = (port & 1) != 0 ? (cur & 0xff) | v << 8 : (cur & 0xff00) | v;
        if (reg == 0x9ae8 && (port & 1) == 0) { _cmd = (_cmd & 0xff00) | v; return; }   // the command starts on its high byte
        EngineOut16(reg, (ushort)nv);
    }

    public void EngineOut16(int port, ushort v)
    {
        switch (port & 0xfffe)
        {
            case 0x42e8: _subsysCtl = v; break;
            case 0x4ae8: _advCtl = v; Virge?.SetAdvFunc(v); break;
            case 0x82e8: _curY = v & 0xfff; break;
            case 0x86e8: _curX = v & 0xfff; break;
            case 0x8ae8: _destY = (short)v; break;        // also the axial step
            case 0x8ee8: _destX = (short)v; break;        // also the diagonal step
            case 0x92e8: _errTerm = (short)(v << 2) >> 2; break;
            case 0x96e8: _majPcnt = v & 0xfff; break;
            case 0x9ae8: _cmd = v; Start(); break;
            case 0x9ee8: break;                           // short strokes: not done
            case 0xa2e8: _bkColor = Color16(_bkColor, v); break;
            case 0xa6e8: _fgColor = Color16(_fgColor, v); break;
            case 0xaae8: _wrtMask = Color16(_wrtMask, v); break;
            case 0xaee8: _rdMask = Color16(_rdMask, v); break;
            case 0xb2e8: _colorCmp = Color16(_colorCmp, v); break;
            case 0xb6e8: _bkMix = v; break;
            case 0xbae8: _fgMix = v; break;
            case 0xbee8:
                switch (v >> 12)
                {
                    case 0x0: _minPcnt = v & 0xfff; break;
                    case 0x1: _scTop = v & 0xfff; break;
                    case 0x2: _scLeft = v & 0xfff; break;
                    case 0x3: _scBottom = v & 0xfff; break;
                    case 0x4: _scRight = v & 0xfff; break;
                    case 0xa: _pixCtl = v & 0xfff; break;
                    case 0xe: _multiMisc = v & 0xfff; break;
                }
                break;
            case 0xe2e8: PixelData(v, 16); break;
        }
    }

    // 32-bit colours arrive as two 16-bit writes (low word first, CR53/multifunc E bit 4 style toggle); 8/16 bpp take the low word
    private uint Color16(uint old, ushort v)
    {
        if (BytesPerPixel < 4) return v;
        uint r = _hiWordNext ? (old & 0xffff) | (uint)v << 16 : (old & 0xffff0000) | v;
        _hiWordNext = !_hiWordNext;
        return r;
    }

    public int EngineIn(int port, bool high)
    {
        int v = (port & 0xfffe) switch
        {
            0x42e8 => 0,                                   // no interrupts pending
            0x9ae8 => 0,                                   // GP status: idle, FIFO empty
            0x82e8 => _curY, 0x86e8 => _curX, 0x96e8 => _majPcnt,
            0xa2e8 => (int)_bkColor, 0xa6e8 => (int)_fgColor, 0xaae8 => (int)_wrtMask, 0xaee8 => (int)_rdMask,
            0xb6e8 => _bkMix, 0xbae8 => _fgMix,
            _ => 0xffff,
        };
        return high ? (v >> 8) & 0xff : v & 0xff;
    }

    private int Pitch => Math.Max(1, Stride / BytesPerPixel);
    private uint PixMask => BytesPerPixel switch { 1 => 0xffu, 2 => 0xffffu, _ => 0xffffffffu };

    private uint ReadPix(int x, int y)
    {
        int bpp = BytesPerPixel; long a = ((long)y * Pitch + x) * bpp;
        if (a < 0 || a + bpp > VramBytes) return 0;
        var r = _v.Ram; int i = (int)a;
        return bpp switch { 1 => r[i], 2 => (uint)(r[i] | r[i + 1] << 8), _ => BitConverter.ToUInt32(r, i) };
    }

    private void WritePix(int x, int y, uint c)
    {
        if (x < _scLeft || x > _scRight || y < _scTop || y > _scBottom) return;
        int bpp = BytesPerPixel; long a = ((long)y * Pitch + x) * bpp;
        if (a < 0 || a + bpp > VramBytes) return;
        var r = _v.Ram; int i = (int)a;
        uint old = bpp switch { 1 => r[i], 2 => (uint)(r[i] | r[i + 1] << 8), _ => BitConverter.ToUInt32(r, i) };
        c = (c & _wrtMask) | (old & ~_wrtMask);
        r[i] = (byte)c;
        if (bpp >= 2) r[i + 1] = (byte)(c >> 8);
        if (bpp == 4) { r[i + 2] = (byte)(c >> 16); r[i + 3] = (byte)(c >> 24); }
    }

    private static uint Mix(int op, uint s, uint d) => (op & 0xf) switch
    {
        0x0 => ~d, 0x1 => 0, 0x2 => 0xffffffff, 0x3 => d, 0x4 => ~s, 0x5 => s ^ d, 0x6 => ~(s ^ d), 0x7 => s,
        0x8 => ~(s & d), 0x9 => ~s | d, 0xa => s | ~d, 0xb => s | d, 0xc => s & d, 0xd => s & ~d, 0xe => ~s & d, _ => ~(s | d),
    };

    /// <summary>One pixel: <paramref name="fg"/> picks the foreground or background mix; <paramref name="cpu"/> is CPU colour data, <paramref name="mem"/> the display-memory source.</summary>
    private void Plot(int x, int y, bool fg, uint cpu, uint mem)
    {
        int mix = fg ? _fgMix : _bkMix;
        uint src = ((mix >> 5) & 3) switch { 0 => _bkColor, 1 => _fgColor, 2 => cpu, _ => mem };
        uint d = ReadPix(x, y);
        WritePix(x, y, Mix(mix, src, d) & PixMask);
    }

    private static readonly bool S3Log = Environment.GetEnvironmentVariable("X86_S3LOG") == "1";
    private int PixSel => (_pixCtl >> 6) & 3;   // 0: foreground mix only, 2: CPU data mono, 3: display memory mono

    private void Start()
    {
        _waitCpu = false;
        int op = (_cmd >> 13) & 7;
        if (S3Log) Console.WriteLine($"s3 cmd {_cmd:X4} cur {_curX},{_curY} dst {_destX},{_destY} w {_majPcnt} h {_minPcnt} pix {_pixCtl:X3} fm {_fgMix:X2} bm {_bkMix:X2} fg {_fgColor:X} bg {_bkColor:X} rd {_rdMask:X} wr {_wrtMask:X} mm {_multiMisc:X3} sc {_scLeft},{_scTop}-{_scRight},{_scBottom}");
        bool cpuData = (_cmd & 0x100) != 0;
        switch (op)
        {
            case 0: break;
            case 1: Line(); break;
            case 2:                                       // rectangle fill
            case 7:                                       // pattern fill (8x8 pattern at the source)
            {
                _pw = _majPcnt + 1; _ph = _minPcnt + 1; _px = _curX; _py = _curY; _cntX = 0; _cntY = 0;
                if (cpuData && (_cmd & 0x10) != 0) { _waitCpu = true; return; }
                if ((_cmd & 0x10) == 0) return;
                int dx = (_cmd & 0x20) != 0 ? 1 : -1, dy = (_cmd & 0x80) != 0 ? 1 : -1;
                if (op == 2 && !S3Slow && FastFill(dx, dy)) { _curY += _ph * dy; break; }
                for (int j = 0; j < _ph; j++)
                    for (int i = 0; i < _pw; i++)
                    {
                        int x = _curX + i * dx, y = _curY + j * dy;
                        if (op == 7 || PixSel == 3)
                        {
                            int sx = op == 7 ? _curX + (i & 7) : x, sy = op == 7 ? _curY + (j & 7) : y;
                            int tx = op == 7 ? _destX + i * dx : x, ty = op == 7 ? _destY + j * dy : y;
                            uint s = ReadPix(sx, sy);
                            bool fg = PixSel != 3 || (s & _rdMask) != 0;
                            Plot(tx, ty, fg, 0, s);
                        }
                        else Plot(x, y, true, 0, 0);
                    }
                _curY += _ph * dy;
                break;
            }
            case 6:                                       // BitBLT: source (cur) -> destination (dest)
            {
                int w = _majPcnt + 1, h = _minPcnt + 1;
                int dx = (_cmd & 0x20) != 0 ? 1 : -1, dy = (_cmd & 0x80) != 0 ? 1 : -1;
                if (cpuData) { _pw = w; _ph = h; _px = _destX; _py = _destY; _cntX = _cntY = 0; _waitCpu = true; return; }
                if (!S3Slow && FastCopy(w, h, dx, dy)) { _curY += h * dy; _destY += h * dy; break; }
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        uint s = ReadPix(_curX + i * dx, _curY + j * dy);
                        bool fg = PixSel != 3 || (s & _rdMask) != 0;
                        Plot(_destX + i * dx, _destY + j * dy, fg, 0, s);
                    }
                _curY += h * dy; _destY += h * dy;
                break;
            }
        }
    }

    private static readonly bool S3Slow = Environment.GetEnvironmentVariable("X86_S3SLOW") == "1";
    /// <summary>Is the rectangle (x0..x0+(w-1)*dx, y0..) inside the scissor and video memory? Then no pixel is clipped.</summary>
    private bool Inside(int x0, int y0, int w, int h, int dx, int dy, bool scissor)
    {
        int xl = dx > 0 ? x0 : x0 - w + 1, xr = xl + w - 1, yt = dy > 0 ? y0 : y0 - h + 1, yb = yt + h - 1;
        if (xl < 0 || yt < 0 || xr >= Pitch) return false;
        if (scissor && (xl < _scLeft || xr > _scRight || yt < _scTop || yb > _scBottom)) return false;
        return ((long)yb * Pitch + xr + 1) * BytesPerPixel <= VramBytes;
    }
    /// <summary>BitBLT display memory -> display memory with the plain source mix and every plane written: whole rows (memmove), exactly
    /// what the pixel loop gives (it falls back when a row overlaps itself in the direction the loop would smear).</summary>
    private bool FastCopy(int w, int h, int dx, int dy)
    {
        if (PixSel == 3 || (_fgMix & 0x7f) != 0x67 || (_wrtMask & PixMask) != PixMask) return false;   // (source = display memory, mix = S)
        if (!Inside(_curX, _curY, w, h, dx, dy, false) || !Inside(_destX, _destY, w, h, dx, dy, true)) return false;
        int bpp = BytesPerPixel, pitch = Pitch, sxl = dx > 0 ? _curX : _curX - w + 1, txl = dx > 0 ? _destX : _destX - w + 1;
        for (int j = 0; j < h; j++)
        {
            int sy = _curY + j * dy, ty = _destY + j * dy;
            if (sy == ty && sxl != txl && Math.Abs(sxl - txl) < w && (dx > 0) == (sxl < txl)) return false;
        }
        for (int j = 0; j < h; j++)
        {
            int sy = _curY + j * dy, ty = _destY + j * dy;
            Array.Copy(_v.Ram, (sy * pitch + sxl) * bpp, _v.Ram, (ty * pitch + txl) * bpp, w * bpp);
        }
        return true;
    }
    /// <summary>Rectangle fill with the foreground colour (mix = S, every plane written) at 8 bpp: whole rows.</summary>
    private bool FastFill(int dx, int dy)
    {
        if (BytesPerPixel != 1 || PixSel != 0 || (_fgMix & 0x7f) != 0x27 || (_wrtMask & 0xff) != 0xff) return false;
        if (!Inside(_curX, _curY, _pw, _ph, dx, dy, true)) return false;
        int xl = dx > 0 ? _curX : _curX - _pw + 1;
        for (int j = 0; j < _ph; j++) Array.Fill(_v.Ram, (byte)_fgColor, (_curY + j * dy) * Pitch + xl, _pw);
        return true;
    }

    private void Line()
    {
        // Bresenham as the 8514 does it: axial step in destY (8AE8), diagonal step in destX (8EE8), error term 92E8
        int x = _curX, y = _curY, err = _errTerm, n = _majPcnt;
        bool xPlus = (_cmd & 0x20) != 0, yMajor = (_cmd & 0x40) != 0, yPlus = (_cmd & 0x80) != 0;
        bool draw = (_cmd & 0x10) != 0, lastPel = (_cmd & 0x04) != 0;
        for (int i = 0; i <= n; i++)
        {
            if (draw && !(lastPel && i == n)) Plot(x, y, true, 0, 0);
            if (err >= 0)
            {
                if (yMajor) x += xPlus ? 1 : -1; else y += yPlus ? 1 : -1;
                err += _destX;
            }
            else err += _destY;
            if (yMajor) y += yPlus ? 1 : -1; else x += xPlus ? 1 : -1;
        }
        _curX = x & 0xfff; _curY = y & 0xfff; _errTerm = err;
    }

    /// <summary>CPU data for a waiting rectangle / BitBLT: colour pixels, or mono bits (bit 12 of the command: bytes swapped).</summary>
    public void PixelData(uint v, int bits)
    {
        if (S3Log) Console.WriteLine($"s3 data {v:X8}/{bits}{(_waitCpu ? "" : " (not waiting)")}");
        if (!_waitCpu) return;
        int bus = ((_cmd >> 9) & 3) switch { 0 => 8, 1 => 16, _ => 32 };
        if (bits < bus) bus = bits;
        // a write wider than the bus is several bus transfers, the low part first (a 16-bit-bus glyph sends two rows per dword)
        // byte swap (cmd bit 12, "low byte first") only reorders MONO data: colour pixels already arrive in Intel order (a 16 bpp brush
        // uploaded with 53B1 came out byte-swapped: FreeCell/Hearts green became red)
        bool swap = (_cmd & 0x1000) != 0 && bus >= 16 && PixSel == 2;
        if (swap) v = bits == 16 ? (uint)(((v & 0xff) << 8) | ((v >> 8) & 0xff)) : (v & 0x00ff00ff) << 8 | (v >> 8) & 0x00ff00ff;
        int dx = (_cmd & 0x20) != 0 ? 1 : -1, dy = (_cmd & 0x80) != 0 ? 1 : -1;
        if (PixSel == 2)
        {
            // mono: each 8/16-bit transfer MSB first (a 32-bit bus is two 16-bit transfers); a line's end pads out only its own transfer
            int w = bus == 8 ? 8 : 16;
            for (int part = 0; part < bits / w && _waitCpu; part++)
            {
                uint hv = (v >> (part * w)) & ((1u << w) - 1);
                for (int b = w - 1; b >= 0 && _waitCpu; b--)
                {
                    bool fg = ((hv >> b) & 1) != 0;
                    Plot(_px + _cntX * dx, _py + _cntY * dy, fg, 0, 0);
                    if (Advance(dy)) break;
                }
            }
        }
        else
        {
            // colour: each bus transfer holds whole pixels; a line's end pads out only its own transfer
            int bpp = BytesPerPixel * 8, w = Math.Max(bus, bpp);
            for (int part = 0; part < Math.Max(1, bits / w) && _waitCpu; part++)
                for (int k = 0; k + bpp <= w && _waitCpu; k += bpp)
                {
                    int sh = part * w + k;
                    uint c = bpp == 32 ? v : (v >> sh) & ((1u << bpp) - 1);
                    Plot(_px + _cntX * dx, _py + _cntY * dy, true, c, c);
                    if (Advance(dy)) break;
                }
        }
    }

    private bool Advance(int dy)
    {
        if (++_cntX < _pw) return false;
        _cntX = 0;
        if (++_cntY >= _ph) { _waitCpu = false; _curY = _py + _ph * dy; }
        return true;
    }

    // ================= save state =================

    public void Save(BinaryWriter w)
    {
        w.Write(SrExt); w.Write(PciConfig); w.Write(LfbBase); w.Write(Bank); w.Write(VbeMode);
        foreach (int v in new[] { _curX, _curY, _destX, _destY, _errTerm, _majPcnt, _minPcnt, _cmd, _scTop, _scLeft, _scBottom, _scRight, _pixCtl, _bkMix, _fgMix, _multiMisc,
            _px, _py, _pw, _ph, _cntX, _cntY }) w.Write(v);
        foreach (uint v in new[] { _bkColor, _fgColor, _wrtMask, _rdMask, _colorCmp }) w.Write(v);
        w.Write(_subsysCtl); w.Write(_advCtl); w.Write(_waitCpu); w.Write(_hiWordNext);
        if (VBox) { w.Write(DispiIndex); foreach (var d in Dispi) w.Write(d); }   // (only the VBOXVGA card: S3 states stay as they were)
        Virge?.Save(w);
    }

    public void Load(BinaryReader r)
    {
        r.Read(SrExt, 0, SrExt.Length); r.Read(PciConfig, 0, PciConfig.Length); LfbBase = r.ReadUInt32(); Bank = r.ReadInt32(); VbeMode = r.ReadInt32(); _shadowFrame = null;
        var a = new int[22]; for (int i = 0; i < a.Length; i++) a[i] = r.ReadInt32();
        (_curX, _curY, _destX, _destY, _errTerm, _majPcnt, _minPcnt, _cmd) = (a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]);
        (_scTop, _scLeft, _scBottom, _scRight, _pixCtl, _bkMix, _fgMix, _multiMisc) = (a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);
        (_px, _py, _pw, _ph, _cntX, _cntY) = (a[16], a[17], a[18], a[19], a[20], a[21]);
        _bkColor = r.ReadUInt32(); _fgColor = r.ReadUInt32(); _wrtMask = r.ReadUInt32(); _rdMask = r.ReadUInt32(); _colorCmp = r.ReadUInt32();
        _subsysCtl = r.ReadUInt16(); _advCtl = r.ReadUInt16(); _waitCpu = r.ReadBoolean(); _hiWordNext = r.ReadBoolean();
        if (VBox) { DispiIndex = r.ReadInt32(); for (int i = 0; i < Dispi.Length; i++) Dispi[i] = r.ReadUInt16(); } SyncDac8();
        Virge?.Load(r);
    }
}
