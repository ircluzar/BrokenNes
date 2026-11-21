// NVIDIA GeForce 256 DDR (NV10, PCI 10DE:0101, 32 MB): the card's 2D/VGA/PCI skeleton (the G0 rung of C:\BrokenNes-work\geforce\SCOPE.md).
// The Vga class stays the card's VGA core (as on the S3); this class adds the NV extended CRTC registers (CR19-CR4F behind the CR1F lock),
// BAR0 (16 MB of registers: PMC, PBUS, PFIFO, PTIMER, PFB, straps, PROM, PGRAPH, PCRTC, PRAMDAC, the VGA mirrors PRMVIO/PRMCIO/PRMDIO,
// PRAMIN) and BAR1 (the VRAM aperture). Written for BrokenNes from the envytools register docs (MIT, read as reference); no code taken
// from another emulator. The video BIOS (VBE 2.0) is ours: PcMachine.Nv.cs.
//
// Modelled now: identity (PMC_BOOT_0, PFB memory amount, straps), the VGA mirrors (bytes at the address: the ViRGE dword-read lesson),
// PCRTC vblank interrupt + START, PRAMDAC general control (565 / 8-bit DAC) with the PLLs stored, PRAMIN = the top 1 MB of VRAM,
// PTIMER as a deterministic function of the frame index + read count (no machine clock in MMIO reads: the Voodoo JIT-parity lesson),
// the picture (8/15/16/32 bpp from CR28 + PCRTC START + the NV pitch bits).
// Store-only (reads give back what was written, status says idle/empty): PFIFO, PGRAPH, USER channels - the command path is G2/G3.

using System;
using System.Collections.Generic;
using System.IO;

namespace NesEmulator.X86;

public sealed partial class NvGeForce : IPciMmio
{
    public const ushort Vendor = 0x10de, Device = 0x0101;
    public const string Id = "NV10";
    public const int VramBytes = 32 * 1024 * 1024;
    public const uint Bar0Size = 16 * 1024 * 1024, Bar1Size = 128 * 1024 * 1024;
    public const uint Boot0 = 0x010000a5;
    public const int RaminBase = VramBytes - 0x100000;

    private readonly Vga _v;
    /// <summary>The PCI function (set by the bus): the PBUS config mirror reads it.</summary>
    public PciFunction? Pci;
    /// <summary>Reads a byte of the video BIOS image (the option ROM at C0000): the PROM block.</summary>
    public Func<int, byte>? RomByte;
    /// <summary>The BAR1 window: video memory, mirrored over the 128 MB aperture.</summary>
    public readonly IPciMmio Aperture;

    // registers that keep what was written (sorted: a deterministic savestate)
    private readonly SortedDictionary<uint, uint> _reg = new();
    public bool Unlocked;
    public long Frame;                 // frames since power on (PTIMER, vblank)
    private long _timerReads;          // PTIMER reads in this frame (time moves forward on every read, deterministically)
    public uint PcrtcIntr, PcrtcIntrEn, PcrtcStart, PmcIntrEn, PmcEnable;
    public int Bank;                   // the 64 KB A0000 bank of our VBE (CR1D)
    public int VbeMode;
    public long MmioReads, MmioWrites; // diagnostics
    /// <summary>Diagnostics: every BAR0 access (offset, value, size, write) when set (the driver bring-up log).</summary>
    public Action<uint, uint, int, bool>? Log;

    public NvGeForce(Vga v) { _v = v; Aperture = new Bar1(this); Reset(); }

    public void Reset()
    {
        _reg.Clear();
        ResetFifo();
        Unlocked = false; Frame = 0; _timerReads = 0;
        PcrtcIntr = PcrtcIntrEn = PcrtcStart = PmcIntrEn = 0;
        PmcEnable = 0x00000000;
        Bank = 0; VbeMode = 0;
        // power-on values the driver reads back
        _reg[0x100200] = 0x00000000;   // PFB CFG0
        _reg[0x101000] = 0x00000040;   // straps: crystal 14.318 MHz, PCI
        _reg[0x680500] = 0x00011a0c;   // NVPLL (stored only: the picture keeps the board's frame clock)
        _reg[0x680504] = 0x00011a0c;   // MPLL
        _reg[0x680508] = 0x00010c0d;   // VPLL
        _reg[0x680600] = 0x00100000;   // PRAMDAC_GENERAL_CONTROL: 8-bit DAC off, 555 (bit 12 off)
        for (int i = 0x19; i < 0x100; i++) _v.Cr[i] = 0;
        _v.Cr[0x37] = _v.Cr[0x3f] = _v.Cr[0x51] = 0x31;   // DDC lines released
    }

    // ================= the extended CRTC (3D4/3D5) =================

    public static bool IsExt(int i) => i >= 0x19;

    public byte CrRead(int i)
    {
        if (i == 0x1f) return (byte)(Unlocked ? 3 : 0);
        if (!Unlocked) return 0;
        return i switch
        {
            // DDC buses (CR36/37, CR3E/3F, CR50/51: read / drive): open drain, no monitor on them, so each line reads what the card drives
            // (bit 5 of the drive register = SCL -> bit 2, bit 4 = SDA -> bit 3); a released line reads high
            0x36 or 0x3e or 0x50 => (byte)(((_v.Cr[i + 1] & 0x20) != 0 ? 4 : 0) | ((_v.Cr[i + 1] & 0x10) != 0 ? 8 : 0)),
            _ => _v.Cr[i],
        };
    }

    public void CrWrite(int i, byte v)
    {
        if (i == 0x1f) { if (v == 0x57) Unlocked = true; else if (v == 0x99) Unlocked = false; return; }
        if (!Unlocked) return;
        _v.Cr[i] = v;
    }

    // ================= the picture =================

    public int Depth => _v.Cr[0x28] & 3;              // 0 VGA, 1 8 bpp, 2 15/16 bpp, 3 32 bpp
    public bool Enhanced => Depth != 0;
    public bool Is565 => (Reg(0x680600) & 0x1000) != 0;
    public int BytesPerPixel => Depth switch { 1 => 1, 2 => 2, _ => 4 };
    public int Width => ((_v.Cr[1] + 1) | ((_v.Cr[0x2d] & 2) << 7)) * 8;
    public int Height => (_v.Cr[0x12] | ((_v.Cr[7] & 2) << 7) | ((_v.Cr[7] & 0x40) << 3) | ((_v.Cr[0x25] & 2) << 9)) + 1;
    public int Stride => (_v.Cr[0x13] | ((_v.Cr[0x19] >> 5) << 8) | (((_v.Cr[0x42] >> 6) & 1) << 11)) * 8;
    public int StartByte => (int)(PcrtcStart & (VramBytes - 1));

    private readonly int[] _pal = new int[256];

    public void Render()
    {
        int w = Width, h = Height, stride = Stride, bpp = BytesPerPixel;
        _v.Ensure(w, h);
        w = _v.Width; h = _v.Height;
        var fr = _v.Frame; var ram = _v.Ram;
        int start = StartByte;
        if (bpp == 1) for (int i = 0; i < 256; i++) _pal[i] = _v.Dac(i & _v.DacMask);
        bool r565 = Is565;
        for (int y = 0; y < h; y++)
        {
            int src = start + y * stride, o = y * w;
            if (src < 0 || src + w * bpp > VramBytes) { Array.Fill(fr, unchecked((int)0xff000000), o, w); continue; }
            switch (bpp)
            {
                case 1: for (int x = 0; x < w; x++) fr[o + x] = _pal[ram[src + x]]; break;
                case 2:
                    for (int x = 0; x < w; x++)
                    {
                        int p = ram[src + 2 * x] | ram[src + 2 * x + 1] << 8, r, g, b;
                        if (!r565) { r = (p >> 10) & 31; g = (p >> 5) & 31; b = p & 31; r = r << 3 | r >> 2; g = g << 3 | g >> 2; }
                        else { r = (p >> 11) & 31; g = (p >> 5) & 63; b = p & 31; r = r << 3 | r >> 2; g = g << 2 | g >> 4; }
                        b = b << 3 | b >> 2;
                        fr[o + x] = unchecked((int)0xff000000) | r << 16 | g << 8 | b;
                    }
                    break;
                default: for (int x = 0; x < w; x++) fr[o + x] = unchecked((int)0xff000000) | (BitConverter.ToInt32(ram, src + 4 * x) & 0xffffff); break;
            }
        }
    }

    /// <summary>Back to plain VGA (a standard BIOS mode set).</summary>
    public void LeaveEnhanced()
    {
        _v.Cr[0x28] = 0; _v.Cr[0x19] = 0; _v.Cr[0x25] = 0; _v.Cr[0x2d] = 0; _v.Cr[0x42] = 0; _v.Cr[0x1d] = 0;
        Bank = 0; VbeMode = 0; PcrtcStart = 0;
    }

    // ================= A0000-BFFFF in an enhanced mode: our VBE's 64 KB bank (CR1D) =================

    private bool EnhancedMap => Enhanced && (_v.Sr[4] & 0x08) != 0;

    public bool MemWrite(uint addr, byte v)
    {
        if (!EnhancedMap) return false;
        uint off = addr - 0xa0000;
        if (off >= 0x10000) return true;
        uint a = (uint)(Bank << 16) + off;
        if (a < VramBytes) _v.Ram[a] = v;
        return true;
    }

    public bool MemRead(uint addr, out byte v)
    {
        v = 0xff;
        if (!EnhancedMap) return false;
        uint off = addr - 0xa0000;
        if (off >= 0x10000) return true;
        uint a = (uint)(Bank << 16) + off;
        if (a < VramBytes) v = _v.Ram[a];
        return true;
    }

    // ================= once per frame =================

    // PTIMER: frames x 1/70 s + 1 us per read, plus the base the driver set by writing TIME_0/TIME_1 (kept in the register store at
    // 9404h/9414h, so it is in the savestate); ALARM_0 (9420h) raises PTIMER_INTR bit 0 when the low word passes it
    private long TimeBase => (long)((ulong)Reg(0x009414) << 32 | Reg(0x009404));
    private long TimeNs(long reads) => TimeBase + Frame * 14_285_714 + reads * 1000;

    public void TickFrame()
    {
        uint lo0 = (uint)TimeNs(0);
        Frame++; _timerReads = 0;
        uint lo1 = (uint)TimeNs(0), alarm = Reg(0x009420);
        if (unchecked(alarm - lo0) < unchecked(lo1 - lo0) || alarm == lo1) { _reg[0x009100] = Reg(0x009100) | 1; }
        PcrtcIntr |= 1;   // vblank
        UpdateIrq();
    }

    /// <summary>Drives INTA# (the board routes it through PIRQA).</summary>
    public Action<bool>? SetIrq;
    private void UpdateIrq() => SetIrq?.Invoke(Irq);

    /// <summary>The card's interrupt line (INTA#): PMC's enabled sources.</summary>
    public bool Irq => (PmcIntrEn & 1) != 0 && PmcIntr != 0;
    private uint PmcIntr => ((PcrtcIntr & PcrtcIntrEn) != 0 ? 1u << 24 : 0)
        | ((Reg(0x002100) & Reg(0x002140)) != 0 ? 1u << 8 : 0) | ((Reg(0x400100) & Reg(0x400140)) != 0 ? 1u << 12 : 0)
        | ((Reg(0x009100) & Reg(0x009140)) != 0 ? 1u << 20 : 0);

    // ================= BAR0 =================

    private uint Reg(uint a) => _reg.TryGetValue(a, out var v) ? v : 0;

    // a VGA mirror: the port at the address (3B0-3DF)
    private static int MirrorPort(uint off, uint blk) => off - blk is >= 0x3b0 and <= 0x3df ? (int)(off - blk) : -1;

    private byte Read8(uint off)
    {
        if (off >= 0x700000 && off < 0x800000) return _v.Ram[RaminBase + (int)(off - 0x700000)];
        if (off >= 0x300000 && off < 0x310000) return RomByte?.Invoke((int)(off - 0x300000)) ?? 0xff;
        int p;
        if (off is >= 0x0c0000 and < 0x0c0400 && (p = MirrorPort(off, 0x0c0000)) >= 0) return _v.In(p);
        if (off is >= 0x601000 and < 0x601400 && (p = MirrorPort(off, 0x601000)) >= 0) return _v.In(p);
        if (off is >= 0x681000 and < 0x681400 && (p = MirrorPort(off, 0x681000)) >= 0) return _v.In(p);
        uint d = Read32(off & ~3u);
        return (byte)(d >> (int)((off & 3) * 8));
    }

    private void Write8(uint off, byte v)
    {
        if (off >= 0x700000 && off < 0x800000) { _v.Ram[RaminBase + (int)(off - 0x700000)] = v; return; }
        if (off >= 0x300000 && off < 0x310000) return;
        int p;
        if (off is >= 0x0c0000 and < 0x0c0400 && (p = MirrorPort(off, 0x0c0000)) >= 0) { _v.Out(p, v); return; }
        if (off is >= 0x601000 and < 0x601400 && (p = MirrorPort(off, 0x601000)) >= 0) { _v.Out(p, v); return; }
        if (off is >= 0x681000 and < 0x681400 && (p = MirrorPort(off, 0x681000)) >= 0) { _v.Out(p, v); return; }
        // a narrow write to a 32-bit register: merged into the register's current value
        uint a = off & ~3u; int sh = (int)(off & 3) * 8;
        Write32(a, (Read32Raw(a) & ~(0xffu << sh)) | ((uint)v << sh));
    }

    private static bool ByteBlock(uint off) => off is >= 0x700000 and < 0x800000 or >= 0x300000 and < 0x310000
        or >= 0x0c0000 and < 0x0c0400 or >= 0x601000 and < 0x601400 or >= 0x681000 and < 0x681400;

    public uint MmioRead(uint off, int size)
    {
        MmioReads++;
        uint v;
        if (size == 4 && (off & 3) == 0 && !ByteBlock(off)) v = Read32(off);
        else if (size == 4 && (off & 3) == 0 && off is >= 0x700000 and < 0x800000) v = BitConverter.ToUInt32(_v.Ram, RaminBase + (int)(off - 0x700000));
        else { v = 0; for (int i = 0; i < size; i++) v |= (uint)Read8(off + (uint)i) << (8 * i); }   // the bytes AT the address
        Log?.Invoke(off, v, size, false);
        return v;
    }

    public void MmioWrite(uint off, uint v, int size)
    {
        MmioWrites++;
        Log?.Invoke(off, v, size, true);
        if (size == 4 && (off & 3) == 0 && !ByteBlock(off)) { Write32(off, v); return; }
        if (size == 4 && (off & 3) == 0 && off is >= 0x700000 and < 0x800000) { BitConverter.TryWriteBytes(_v.Ram.AsSpan(RaminBase + (int)(off - 0x700000)), v); return; }
        for (int i = 0; i < size; i++) Write8(off + (uint)i, (byte)(v >> (8 * i)));
    }

    // the stored value, for a merge (no side effects)
    private uint Read32Raw(uint a) => a switch
    {
        0x000140 => PmcIntrEn, 0x000200 => PmcEnable, 0x600100 => PcrtcIntr, 0x600140 => PcrtcIntrEn, 0x600800 => PcrtcStart,
        _ => Reg(a),
    };

    private uint Read32(uint a)
    {
        switch (a)
        {
            case 0x000000: return Boot0;
            case 0x000100: return PmcIntr;
            case 0x000140: return PmcIntrEn;
            case 0x000200: return PmcEnable;
            case 0x002400: return 0x00000010;                    // PFIFO RUNOUT_STATUS: empty
            case 0x003214: return 0x00000010;                    // CACHE1_STATUS: empty
            case 0x009400:                                       // PTIMER TIME_0 / TIME_1: frames x 1/70 s + 1 us per read (deterministic)
            case 0x009410:
            {
                long ns = TimeNs(++_timerReads);
                return a == 0x009400 ? (uint)ns & 0xffffffe0 : (uint)(ns >> 32);
            }
            case 0x100200: return Reg(a);
            case 0x10020c: return VramBytes;                     // PFB MEM_AMOUNT
            case 0x400700: return 0;                             // PGRAPH STATUS: idle
            case 0x600100: return PcrtcIntr;
            case 0x600140: return PcrtcIntrEn;
            case 0x600800: return PcrtcStart;
        }
        if (a is >= 0x001800 and < 0x001900) return Pci?.Read32((int)(a - 0x001800)) ?? 0;   // PBUS: the PCI configuration mirror
        if (a is >= 0x800000 and < 0x1000000) return UserRead(a);                             // USER: the channels' submission areas
        return Reg(a);
    }

    private void Write32(uint a, uint v)
    {
        switch (a)
        {
            case 0x000000: case 0x000100: case 0x10020c: return;
            case 0x000140: PmcIntrEn = v; UpdateIrq(); return;
            case 0x000200: PmcEnable = v; return;
            case 0x600100: PcrtcIntr &= ~v; UpdateIrq(); return;   // write 1 to clear
            case 0x600140: PcrtcIntrEn = v & 1; UpdateIrq(); return;
            case 0x600800: PcrtcStart = v & (VramBytes - 1) & ~3u; return;
            case 0x009400: case 0x009410:
            {
                long now = TimeNs(_timerReads);
                long want = a == 0x009400 ? (long)(((ulong)now & 0xffffffff00000000) | v) : (long)(((ulong)v << 32) | ((ulong)now & 0xffffffff));
                long nb = TimeBase + (want - now);
                _reg[0x009404] = (uint)nb; _reg[0x009414] = (uint)((ulong)nb >> 32);
                return;
            }
        }
        if (a is >= 0x001800 and < 0x001900) { if (Pci != null) for (int i = 0; i < 4; i++) Pci.WriteByte((int)(a - 0x001800) + i, (byte)(v >> (8 * i))); return; }
        if (a is >= 0x800000 and < 0x1000000) { UserWrite(a, v); return; }
        if (a is 0x001100 or 0x002100 or 0x009100 or 0x400100) { _reg[a] = Reg(a) & ~v; UpdateIrq(); if (a == 0x002100 && (v & 1) != 0) { _reg[0x003270] = Reg(0x003210); RunPusher(); } return; }   // (the NVIDIA driver's ISR does not advance CACHE1 GET: clearing CACHE_ERROR = the method was taken)   // interrupt status: write 1 to clear
        _reg[a] = v;
        if (a is 0x003240 or 0x003220 or 0x003200 or 0x003250 or 0x003270 or 0x002500) RunPusher();
    }

    // ================= BAR1: the VRAM aperture =================

    private sealed class Bar1 : IPciMmio
    {
        private readonly NvGeForce _n;
        public Bar1(NvGeForce n) => _n = n;
        public uint MmioRead(uint off, int size)
        {
            var ram = _n._v.Ram; int o = (int)(off & (VramBytes - 1));
            if (size == 4 && o <= VramBytes - 4) return BitConverter.ToUInt32(ram, o);
            uint v = 0; for (int i = 0; i < size; i++) v |= (uint)ram[(o + i) & (VramBytes - 1)] << (8 * i);
            return v;
        }
        public void MmioWrite(uint off, uint v, int size)
        {
            var ram = _n._v.Ram; int o = (int)(off & (VramBytes - 1));
            if (size == 4 && o <= VramBytes - 4) { BitConverter.TryWriteBytes(ram.AsSpan(o), v); return; }
            for (int i = 0; i < size; i++) ram[(o + i) & (VramBytes - 1)] = (byte)(v >> (8 * i));
        }
    }

    // ================= savestate (the video memory is the VGA's, saved with it) =================

    public void Save(BinaryWriter w)
    {
        w.Write(3);   // version (3: PFIFO/PGRAPH state)
        w.Write(Unlocked); w.Write(Frame); w.Write(_timerReads);
        w.Write(PcrtcIntr); w.Write(PcrtcIntrEn); w.Write(PcrtcStart); w.Write(PmcIntrEn); w.Write(PmcEnable);
        w.Write(Bank); w.Write(VbeMode);
        w.Write(_reg.Count);
        foreach (var (k, v) in _reg) { w.Write(k); w.Write(v); }
        SaveFifo(w);
    }

    public void Load(BinaryReader r)
    {
        int ver = r.ReadInt32();
        if (ver is not (1 or 2 or 3)) throw new InvalidDataException("unknown GeForce state version");
        Unlocked = r.ReadBoolean(); Frame = r.ReadInt64(); _timerReads = r.ReadInt64();
        PcrtcIntr = r.ReadUInt32(); PcrtcIntrEn = r.ReadUInt32(); PcrtcStart = r.ReadUInt32(); PmcIntrEn = r.ReadUInt32(); PmcEnable = r.ReadUInt32();
        Bank = r.ReadInt32(); VbeMode = r.ReadInt32();
        _reg.Clear();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) { uint k = r.ReadUInt32(); _reg[k] = r.ReadUInt32(); }
        if (ver >= 3) LoadFifo(r); else ResetFifo();
    }
}
