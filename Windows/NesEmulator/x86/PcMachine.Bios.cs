// The x86 console's BIOS, written in C# for BrokenNes (no BIOS ROM file, no proprietary code): the power-on self test, the interrupt
// vectors and the BIOS data area, and the services DOS and games use (INT 10h video, 13h disk, 15h system, 16h keyboard, 1Ah clock,
// 11h / 12h equipment and memory, 19h boot), answered by the console itself the way DOSBox does.
//
// How a call reaches C#: every BIOS vector points at a stub in the ROM segment F000h whose first instruction is OUT E0h, AL. The board
// sees the write to port E0h, looks up which stub the CPU is executing, and runs the C# service on the CPU's registers; the stub's
// IRET then returns to the caller (the service changes the FLAGS image on the stack for CF / ZF results). Hardware interrupt stubs
// also send the PIC's EOI in machine code, INT 08h calls INT 1Ch, and the INT 16h stub enables interrupts first so a wait for a key
// can halt until the keyboard interrupt.

using System;
using System.Collections.Generic;
using System.Text;

namespace NesEmulator.X86;

public sealed partial class PcMachine
{
    public const int BiosTrapPort = 0xe0;
    private const uint RomSeg = 0xf000, Rom = 0xf0000;
    private const uint StubBase = 0xe000;          // F000:E000 + 4n: the generic stub for vector n
    private const uint FontOff = 0xa000;           // F000:A000: the 8x16 font
    private const uint DisketteTable = 0xefc7, ConfigTable = 0xe6f5, FixedDiskTable = 0xe401;
    private const uint Int08Stub = 0xe800, Int09Stub = 0xe810, Int16Stub = 0xe820, IrqMasterStub = 0xe830, IrqSlaveStub = 0xe840,
        IretStub = 0xff53, Int19Stub = 0xe850;
    private readonly Dictionary<uint, int> _special = new();   // ROM offset of an OUT E0 -> pseudo-vector (0x100 + n)

    private const int I_INT08 = 0x108, I_INT09 = 0x109, I_INT16 = 0x116, I_INT19 = 0x119, I_RESET = 0x1ff, I_INT09A = 0x1f9;

    /// <summary>The drive booted from (0x00 = A:, 0x80 = C:).</summary>
    public int BootDrive = -1;
    /// <summary>Debug tools: called with (service, true) before and (service, false) after each BIOS service (n &lt; 0x100 = INT n).</summary>
    public Action<int, bool>? BiosLog;

    // ================= registers of the caller =================

    private I386 C => Cpu;
    private uint AX { get => C.R[0] & 0xffff; set => C.SReg(16, 0, value); }
    private uint BX { get => C.R[3] & 0xffff; set => C.SReg(16, 3, value); }
    private uint CX { get => C.R[1] & 0xffff; set => C.SReg(16, 1, value); }
    private uint DX { get => C.R[2] & 0xffff; set => C.SReg(16, 2, value); }
    private uint SI => C.R[6] & 0xffff;
    private uint DI { get => C.R[7] & 0xffff; set => C.SReg(16, 7, value); }
    private uint BP { get => C.R[5] & 0xffff; set => C.SReg(16, 5, value); }
    private uint AL { get => C.LReg(8, 0); set => C.SReg(8, 0, value); }
    private uint CL { get => C.LReg(8, 1); set => C.SReg(8, 1, value); }
    private uint DL { get => C.LReg(8, 2); set => C.SReg(8, 2, value); }
    private uint BL { get => C.LReg(8, 3); set => C.SReg(8, 3, value); }
    private uint AH { get => C.LReg(8, 4); set => C.SReg(8, 4, value); }
    private uint CH { get => C.LReg(8, 5); set => C.SReg(8, 5, value); }
    private uint DH { get => C.LReg(8, 6); set => C.SReg(8, 6, value); }
    private uint BH { get => C.LReg(8, 7); set => C.SReg(8, 7, value); }
    private uint SegB(int s) => C.SegBase[s];
    private void SetEs(uint v) => C.LoadSeg(I386.SEG_ES, v);

    private uint FrameFlags => SegB(I386.SEG_SS) + ((C.R[4] + 4) & 0xffff);
    private void SetFrameFlag(uint bit, bool on)
    {
        uint a = FrameFlags;
        ushort f = Phys16(a);
        PhysW16(a, (ushort)(on ? f | bit : f & ~bit));
    }
    private void SetCF(bool on) => SetFrameFlag(I386.CF, on);
    private void SetZF(bool on) => SetFrameFlag(I386.ZF, on);
    private void Fail(uint ah) { AH = ah; SetCF(true); }
    private void Ok() { SetCF(false); }

    // ================= the trap =================

    private void BiosTrap()
    {
        if ((C.Cr0 & 1) != 0 && (C.Flags & I386.VM) == 0) return;   // protected-mode code writing port E0h: not a BIOS call
        uint lin = SegB(I386.SEG_CS) + C.Ip;
        if (lin < Rom || lin >= Rom + 0x10000) return;
        uint off = lin - Rom;
        int n;
        if (off >= StubBase && off < StubBase + 0x400 && (off & 3) == 0) n = (int)((off - StubBase) / 4);
        else if (!_special.TryGetValue(off, out n)) return;
        BiosLog?.Invoke(n, true);
        try { Service(n); }
        catch (I386AbortException) { throw; }
        catch (Exception e) { Stopped = $"BIOS INT {n & 0xff:X2}h AX={AX:X4} failed: {e.Message}"; C.EndBatch(); }
        BiosLog?.Invoke(n, false);
    }

    private void Service(int n)
    {
        switch (n)
        {
            case I_INT08: Timer(); break;
            case I_INT09: KeyboardIrq(Kbc.In(0x60)); break;   // (the old stub, in states saved before I_INT09A)
            case I_INT09A: KeyboardIrq((byte)AL); break;
            case I_INT16: Int16(); break;
            case I_INT19: Boot(); break;
            case I_RESET: CpuResetRequest(); break;   // a jump to FFFF:0000 (Windows 9x restarting): a real reset, POST included
            case 0x10: Int10(); break;
            case 0x11: AX = Phys16(0x410); break;
            case 0x12: AX = Phys16(0x413); break;
            case 0x13: Int13(); break;
            case 0x14: AH = 0x80; break;
            case 0x15: Int15(); break;
            case 0x17: AH = 0x80; break;
            case 0x18: Stopped = "INT 18h: no bootable disk (no ROM BASIC)"; C.EndBatch(); break;
            case 0x1a: Int1A(); break;
        }
    }

    // ================= POST =================

    /// <summary>The stubs whose OUT E0h is not at a generic slot (fixed ROM layout: a loaded state needs them too).</summary>
    private void RegisterTraps()
    {
        _special[Int08Stub] = I_INT08; _special[Int09Stub] = I_INT09; _special[Int09Stub + 3] = I_INT09A; _special[Int16Stub + 1] = I_INT16; _special[Int19Stub] = I_INT19; _special[0xfff0] = I_RESET;
        _special[Int13Pio] = 0x13;   // (only reached on an IDE machine whose POST put the PIO code there)
    }

    private void Post()
    {
        var m = Cpu.Mem;
        Array.Clear(m, (int)Rom, 0x10000);
        // the generic stubs: OUT E0h,AL / IRET / NOP
        for (uint n = 0; n < 256; n++) { uint o = Rom + StubBase + n * 4; m[o] = 0xe6; m[o + 1] = 0xe0; m[o + 2] = 0xcf; m[o + 3] = 0x90; }
        void Code(uint off, int id, params byte[] b) { Array.Copy(b, 0, m, Rom + off, b.Length); if (id >= 0) _special[off] = id; }
        // INT 08h: the C# tick, INT 1Ch, EOI
        Code(Int08Stub, I_INT08, 0xe6, 0xe0, 0xcd, 0x1c, 0x50, 0xb0, 0x20, 0xe6, 0x20, 0x58, 0xcf);
        // INT 09h: IN AL,60h in machine code (so a V86 monitor sees the key read: Windows 98's VKD re-sent IRQ 1 forever when the C# handler read
        // the port behind its back, and DOS's STACKS overflowed), then the C# key handler on AL, EOI
        Code(Int09Stub, -1, 0x50, 0xe4, 0x60, 0xe6, 0xe0, 0xb0, 0x20, 0xe6, 0x20, 0x58, 0xcf);
        _special[Int09Stub + 3] = I_INT09A;
        // INT 16h: STI first, so waiting for a key can halt until IRQ 1
        Code(Int16Stub, -1, 0xfb); _special[Int16Stub + 1] = I_INT16; Code(Int16Stub + 1, -1, 0xe6, 0xe0, 0xcf);
        Code(IrqMasterStub, -1, 0x50, 0xb0, 0x20, 0xe6, 0x20, 0x58, 0xcf);
        Code(IrqSlaveStub, -1, 0x50, 0xb0, 0x20, 0xe6, 0xa0, 0xe6, 0x20, 0x58, 0xcf);
        Code(Int19Stub, I_INT19, 0xe6, 0xe0, 0xcf);
        Code(IretStub, -1, 0xcf);
        InstallInt10Replay();
        if (Ata != null) InstallInt13Pio();
        // reset vector: OUT E0h = a full reset (POST, then boot) like a real BIOS. A jump to FFFF:0000 used to go straight to INT 19h, which kept the
        // old OS's hooked vectors and hung Windows 98 Setup's restart ("Waiting for system shutdown").
        Code(0xfff0, I_RESET, 0xe6, 0xe0, 0xeb, 0xfc);
        Code(0xfff5, -1, Encoding.ASCII.GetBytes("06/01/94"));
        m[Rom + 0xfffe] = 0xfc;
        Array.Copy(VgaFont.Font16, 0, m, Rom + FontOff, 4096);
        Code(ConfigTable, -1, 0x08, 0x00, 0xfc, 0x01, 0x00, 0x70, 0x00, 0x00, 0x00, 0x00);
        Code(DisketteTable, -1, 0xaf, 0x02, 0x25, 0x02, 0x12, 0x1b, 0xff, 0x6c, 0xf6, 0x0f, 0x08);
        Code(0xe010, -1, Encoding.ASCII.GetBytes("BrokenNes x86 BIOS (C#)"));
        if (S3 != null) VideoRomInit();

        // interrupt vectors
        void Vec(int n, uint off) { PhysW16((uint)n * 4, (ushort)off); PhysW16((uint)n * 4 + 2, (ushort)RomSeg); }
        for (int n = 0; n < 256; n++) Vec(n, IretStub);
        foreach (int n in new[] { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x17, 0x18, 0x1a }) Vec(n, StubBase + (uint)n * 4);
        Vec(0x08, Int08Stub); Vec(0x09, Int09Stub); Vec(0x16, Int16Stub); Vec(0x19, Int19Stub);
        if (Ata != null) Vec(0x13, Int13Pio);
        for (int n = 0x0a; n <= 0x0f; n++) Vec(n, IrqMasterStub);
        for (int n = 0x70; n <= 0x77; n++) Vec(n, IrqSlaveStub);
        Vec(0x1e, DisketteTable);
        PhysW32(0x1d * 4, 0); PhysW32(0x1f * 4, 0);
        Vec(0x43, FontOff);
        for (int n = 0x60; n <= 0x67; n++) PhysW32((uint)n * 4, 0);

        // BIOS data area
        Array.Clear(m, 0x400, 0x100);
        ushort equip = 0x0020;                                  // 80x25 colour
        int nfl = (Floppy[0] != null ? 1 : 0) + (Floppy[1] != null ? 1 : 0);
        if (nfl > 0) equip |= (ushort)(1 | ((nfl - 1) << 6));
        if (Kbc.HasMouse) equip |= 0x0004;                     // pointing device (the PS/2 mouse)
        if (Cpu.Model.Fpu) equip |= 0x0002;                     // math coprocessor installed (Windows 98 Setup checks INT 11h)
        PhysW16(0x410, equip);
        PhysW16(0x413, 640);
        PhysW16(0x41a, 0x1e); PhysW16(0x41c, 0x1e);
        PhysW16(0x480, 0x1e); PhysW16(0x482, 0x3e);
        PhysW8(0x475, (byte)((Hdd[0] != null ? 1 : 0) + (Hdd[1] != null ? 1 : 0)));
        PhysW8(0x496, 0x10);                                    // 101-key keyboard
        // the clock: ticks since midnight from the CMOS start time
        var t = Cmos.Now.TimeOfDay;
        PhysW32(0x46c, (uint)(t.TotalSeconds * 1193182.0 / 65536.0));

        if (Ata != null) PostIde();
        if (Pci != null) PostPci();

        // chips
        Out8(0x20, 0x11); Out8(0x21, 0x08); Out8(0x21, 0x04); Out8(0x21, 0x01);
        Out8(0xa0, 0x11); Out8(0xa1, 0x70); Out8(0xa1, 0x02); Out8(0xa1, 0x01);
        Out8(0x21, 0xb8); Out8(0xa1, Ata != null ? (byte)0x0f : (byte)0x8f);   // IRQ 0, 1, 2, 6 (and 8 / 12-14 on the slave; 15 too with IDE) open
        Out8(0x43, 0x36); Out8(0x40, 0); Out8(0x40, 0);
        SetMode(3);

        Cpu.Flags = 0x2;
        BootNow();
    }

    private void BootNow()
    {
        if (Ata != null) { BootIde(); return; }
        foreach (int drive in new[] { 0x00, 0x80 })
        {
            var d = Disk(drive);
            if (d == null) continue;
            var sec = new byte[512];
            if (!d.Read(0, 1, sec)) continue;
            if (sec[510] != 0x55 || sec[511] != 0xaa) continue;
            Array.Copy(sec, 0, Cpu.Mem, 0x7c00, 512);
            BootDrive = drive;
            C.LoadSeg(I386.SEG_SS, 0); C.R[4] = 0x7c00;
            C.LoadSeg(I386.SEG_DS, 0); C.LoadSeg(I386.SEG_ES, 0);
            C.R[2] = (uint)drive;
            C.Flags = 0x202;
            C.SetCsIp(0, 0x7c00);
            return;
        }
        TeletypeString("No bootable disk.\r\n");
        Stopped = "no bootable disk";
    }

    private void Boot() { BootNow(); C.EndBatch(); }

    // ================= INT 08h / 09h / 16h: timer and keyboard =================

    private void Timer()
    {
        uint t = Phys32(0x46c) + 1;
        if (t >= 0x1800b0) { t = 0; PhysW8(0x470, 1); }
        PhysW32(0x46c, t);
        byte motor = Phys8(0x440);
        if (motor > 0) PhysW8(0x440, (byte)(motor - 1));
    }

    // set-1 make codes 0x00-0x58 -> ASCII: normal, shifted
    private const string KeyNormal = "\0\x1b" + "1234567890-=\b\t" + "qwertyuiop[]\r\0" + "asdfghjkl;'`\0\\" + "zxcvbnm,./\0*\0 \0";
    private const string KeyShift = "\0\x1b" + "!@#$%^&*()_+\b\0" + "QWERTYUIOP{}\r\0" + "ASDFGHJKL:\"~\0|" + "ZXCVBNM<>?\0*\0 \0";

    private void KeyboardIrq(byte sc)
    {
        byte f1 = Phys8(0x417), f2 = Phys8(0x418), f3 = Phys8(0x496);
        bool e0 = (f3 & 2) != 0;
        f3 &= unchecked((byte)~2);
        if (sc == 0xe0) { PhysW8(0x496, (byte)(f3 | 2)); return; }
        if (sc == 0xe1 || sc == 0xfa) { PhysW8(0x496, f3); return; }
        bool brk = (sc & 0x80) != 0;
        int code = sc & 0x7f;
        PhysW8(0x496, f3);
        if (e0 && (code == 0x2a || code == 0x36)) return;        // the fake shifts around E0 keys
        switch (code)
        {
            case 0x2a: f1 = Bit(f1, 2, !brk); PhysW8(0x417, f1); return;
            case 0x36: f1 = Bit(f1, 1, !brk); PhysW8(0x417, f1); return;
            case 0x1d: f1 = Bit(f1, 4, !brk); if (!e0) f2 = Bit(f2, 1, !brk); PhysW8(0x417, f1); PhysW8(0x418, f2); return;
            case 0x38: f1 = Bit(f1, 8, !brk); if (!e0) f2 = Bit(f2, 2, !brk); PhysW8(0x417, f1); PhysW8(0x418, f2); return;
            case 0x3a: if (!brk && (f2 & 0x40) == 0) f1 ^= 0x40; f2 = Bit(f2, 0x40, !brk); PhysW8(0x417, f1); PhysW8(0x418, f2); return;
            case 0x45: if (!brk && (f2 & 0x20) == 0) f1 ^= 0x20; f2 = Bit(f2, 0x20, !brk); PhysW8(0x417, f1); PhysW8(0x418, f2); return;
            case 0x46: if (!brk && (f2 & 0x10) == 0) f1 ^= 0x10; f2 = Bit(f2, 0x10, !brk); PhysW8(0x417, f1); PhysW8(0x418, f2); return;
            case 0x52: if (!brk && (f2 & 0x80) == 0) f1 ^= 0x80; f2 = Bit(f2, 0x80, !brk); PhysW8(0x417, f1); PhysW8(0x418, f2); if (brk) return; break;
        }
        if (brk) return;
        bool shift = (f1 & 3) != 0, ctrl = (f1 & 4) != 0, alt = (f1 & 8) != 0, caps = (f1 & 0x40) != 0;
        int ascii = 0, scan = code;
        if (e0)
        {
            if (code == 0x1c) { ascii = 0x0d; scan = 0xe0; }
            else if (code == 0x35) { ascii = '/'; scan = 0xe0; }
            else ascii = 0xe0;
        }
        else if (code >= 0x3b && code <= 0x44)
        {
            ascii = 0;
            scan = alt ? code + 0x2d : ctrl ? code + 0x23 : shift ? code + 0x19 : code;
        }
        else if (code == 0x57 || code == 0x58)
        {
            scan = (code == 0x57 ? 0x85 : 0x86) + (alt ? 6 : ctrl ? 4 : shift ? 2 : 0);
        }
        else if (code < KeyNormal.Length)
        {
            char ch = KeyNormal[code];
            if (alt) ascii = 0;
            else if (ctrl) ascii = ch >= 'a' && ch <= 'z' ? ch - 'a' + 1 : ch == '\r' ? 10 : ch == '\b' ? 0x7f : ch == '[' ? 0x1b : ch == '\x1b' ? 0x1b : 0;
            else
            {
                bool letter = ch >= 'a' && ch <= 'z';
                bool up = letter ? shift ^ caps : shift;
                ascii = up ? KeyShift[code] : ch;
            }
            if (code == 0x0f && shift) ascii = 0;
        }
        else if (code >= 0x47 && code <= 0x53)
        {
            bool num = ((f1 & 0x20) != 0) ^ shift;
            const string pad = "789-456+1230.";
            ascii = num ? pad[code - 0x47] : (code == 0x4a ? '-' : code == 0x4e ? '+' : 0);
        }
        else return;
        if (ascii == 0 && scan == 0) return;
        PushKey((ushort)((scan << 8) | ascii));
    }

    private static byte Bit(byte v, int bit, bool on) => (byte)(on ? v | bit : v & ~bit);

    private bool PushKey(ushort k)
    {
        ushort head = Phys16(0x41a), tail = Phys16(0x41c), start = Phys16(0x480), end = Phys16(0x482);
        ushort next = (ushort)(tail + 2);
        if (next >= end) next = start;
        if (next == head) return false;
        PhysW16(0x400u + tail, k);
        PhysW16(0x41c, next);
        return true;
    }

    private bool PeekKey(out ushort k)
    {
        ushort head = Phys16(0x41a), tail = Phys16(0x41c);
        k = 0;
        if (head == tail) return false;
        k = Phys16(0x400u + head);
        return true;
    }

    private void PopKey()
    {
        ushort head = Phys16(0x41a), start = Phys16(0x480), end = Phys16(0x482);
        head += 2;
        if (head >= end) head = start;
        PhysW16(0x41a, head);
    }

    private static ushort Plain(ushort k)
    {
        int asc = k & 0xff, sc = k >> 8;
        if (sc == 0xe0) sc = asc == 0x0d ? 0x1c : 0x35;
        else if (asc == 0xe0 && sc != 0) asc = 0;
        if (sc >= 0x85) return 0xffff;                           // F11 / F12: only for the enhanced calls
        return (ushort)((sc << 8) | asc);
    }

    private void Int16()
    {
        switch (AH)
        {
            case 0x00: case 0x10:
                for (; ; )
                {
                    if (!PeekKey(out ushort k)) { WaitForKey(); return; }
                    PopKey();
                    if (AH == 0x00) { k = Plain(k); if (k == 0xffff) continue; }
                    AX = k;
                    return;
                }
            case 0x01: case 0x11:
                for (; ; )
                {
                    if (!PeekKey(out ushort k)) { SetZF(true); return; }
                    if (AH == 0x01) { ushort p = Plain(k); if (p == 0xffff) { PopKey(); continue; } k = p; }
                    AX = k; SetZF(false);
                    return;
                }
            case 0x02: AL = Phys8(0x417); break;
            case 0x12: AL = Phys8(0x417); AH = (uint)(Phys8(0x418) & 0x73) | (uint)(Phys8(0x417) & 0x80) ; break;
            case 0x05: AL = PushKey((ushort)CX) ? 0u : 1u; break;
            case 0x03: break;
            case 0x09: AL = 0x20; break;                         // supports AH=10h-12h
            case 0x0a: BX = 0x41ab; break;
        }
    }

    /// <summary>No key yet: execute the OUT again after the next interrupt (the stub enabled them), and let the CPU halt till then.</summary>
    private void WaitForKey()
    {
        C.NextIp = C.Ip;
        // In V86 mode (a DOS VM under Windows) a native halt hides the wait from the VMM: the VM never traps, so Windows
        // cannot switch away from it (setup froze on a hidden VM's INT 16h). Spin instead; the timer preempts it.
        if ((C.Flags & I386.VM) == 0) C.Halt = true;
        C.EndBatch();
    }

    // ================= INT 1Ah: clock =================

    private static uint Bcd(int v) => (uint)(((v / 10) << 4) | (v % 10));

    private void Int1A()
    {
        if (AH == 0xb1 && Pci != null) { PciBiosCall(frame: true); return; }
        var now = Cmos.Now;
        switch (AH)
        {
            case 0x00: { uint t = Phys32(0x46c); CX = t >> 16; DX = t & 0xffff; AL = Phys8(0x470); PhysW8(0x470, 0); break; }
            case 0x01: PhysW32(0x46c, (CX << 16) | DX); PhysW8(0x470, 0); break;
            case 0x02: CH = Bcd(now.Hour); CL = Bcd(now.Minute); DH = Bcd(now.Second); DL = 0; Ok(); break;
            case 0x04: CH = Bcd(now.Year / 100); CL = Bcd(now.Year % 100); DH = Bcd(now.Month); DL = Bcd(now.Day); Ok(); break;
            case 0x03: case 0x05: Ok(); break;
            default: SetCF(true); break;
        }
    }

    // ================= INT 15h: system =================

    // INT 15h C2xxh: the PS/2 pointing device services (Windows 98's MSMOUSE.VXD finds the mouse through them: without them the "PS/2 Compatible
    // Mouse Port" sat at Code 24). Status in AH: 0 ok, 1 invalid function, 3 interface error, 4 resend, 5 no handler. The far-call packet handler
    // (C207h) is kept but not called: DOS mouse drivers that need it are not supported yet.
    private uint _ps2Handler;
    private void Int15Mouse()
    {
        if (!Kbc.HasMouse) { AH = 0x86; SetCF(true); return; }
        bool Ack(System.Collections.Generic.List<byte> r) => r.Count > 0 && r[0] == 0xfa;
        switch (AL)
        {
            case 0x00:   // enable (BH 1) / disable (BH 0)
                if (BH == 1 && _ps2Handler == 0) { AH = 5; SetCF(true); return; }
                if (!Ack(Kbc.MouseDirect(BH == 1 ? (byte)0xf4 : (byte)0xf5))) { AH = 3; SetCF(true); return; }
                Kbc.MousePort(BH == 1); AH = 0; Ok(); return;
            case 0x01:   // reset: BH = device id, BL = AAh
            {
                var r = Kbc.MouseDirect(0xff);
                if (!Ack(r) || r.Count < 3) { AH = 3; SetCF(true); return; }
                BL = r[1]; BH = r[2]; AH = 0; Ok(); return;
            }
            case 0x02:   // sample rate code BH 0-6: 10 20 40 60 80 100 200
            {
                if (BH > 6) { AH = 2; SetCF(true); return; }
                int[] rates = { 10, 20, 40, 60, 80, 100, 200 };
                Kbc.MouseDirect(0xf3, rates[BH]); AH = 0; Ok(); return;
            }
            case 0x03: if (BH > 3) { AH = 2; SetCF(true); return; } Kbc.MouseDirect(0xe8, (int)BH); AH = 0; Ok(); return;
            case 0x04: { var r = Kbc.MouseDirect(0xf2); BH = r.Count > 1 ? r[1] : 0u; AH = 0; Ok(); return; }
            case 0x05: if (BH is < 1 or > 8) { AH = 2; SetCF(true); return; } Kbc.MouseDirect(0xf5); Kbc.MouseDirect(0xf6); AH = 0; Ok(); return;
            case 0x06:
                if (BH == 0) { var r = Kbc.MouseDirect(0xe9); BL = r.Count > 1 ? r[1] : 0u; CL = r.Count > 2 ? r[2] : 0u; DL = r.Count > 3 ? r[3] : 0u; AH = 0; Ok(); return; }
                if (BH is 1 or 2) { Kbc.MouseDirect(BH == 1 ? (byte)0xe6 : (byte)0xe7); AH = 0; Ok(); return; }
                AH = 1; SetCF(true); return;
            case 0x07: _ps2Handler = ((C.SegSel[I386.SEG_ES] & 0xffff) << 16) | BX; AH = 0; Ok(); return;
        }
        AH = 1; SetCF(true);
    }

    private void Int15()
    {
        uint ah = AH;
        if (ah == 0x53 && Pci != null) { ApmCall(frame: true); return; }
        switch (ah)
        {
            case 0x24:
                switch (AL)
                {
                    case 0: Kbc.A20 = false; AH = 0; Ok(); return;
                    case 1: Kbc.A20 = true; AH = 0; Ok(); return;
                    case 2: AL = Kbc.A20 ? 1u : 0u; AH = 0; Ok(); return;
                    case 3: BX = 3; AH = 0; Ok(); return;
                }
                break;
            case 0x4f: SetCF(true); return;
            case 0x86: Ok(); AH = 0; return;
            case 0x87:
            {
                uint gdt = SegB(I386.SEG_ES) + SI;
                uint src = Phys16(gdt + 0x12) | ((uint)Phys8(gdt + 0x14) << 16) | ((uint)Phys8(gdt + 0x17) << 24);
                uint dst = Phys16(gdt + 0x1a) | ((uint)Phys8(gdt + 0x1c) << 16) | ((uint)Phys8(gdt + 0x1f) << 24);
                uint n = CX * 2;
                for (uint i = 0; i < n; i++) PhysW8(dst + i, Phys8(src + i));
                AH = 0; Ok(); return;
            }
            case 0x88: AX = (uint)Math.Min(MemKb - 1024, 0xfc00); Ok(); return;
            case 0x90: case 0x91: AH = 0; Ok(); return;
            case 0xc0: SetEs(RomSeg); BX = ConfigTable; AH = 0; Ok(); return;
            case 0xc2: Int15Mouse(); return;
            case 0xe8:
                if (AL == 0x01)
                {
                    int ext = MemKb - 1024;
                    AX = CX = (uint)Math.Min(ext, 15 * 1024);
                    BX = DX = (uint)Math.Max(0, (ext - 15 * 1024) / 64);
                    Ok(); return;
                }
                if (AL == 0x20 && C.R[2] == 0x534d4150)
                {
                    var map = new (ulong b, ulong l, uint t)[] { (0, 0x9fc00, 1), (0x9fc00, 0x400, 2), (0xf0000, 0x10000, 2), (0x100000, (ulong)MemKb * 1024 - 0x100000, 1) };
                    uint i = C.R[3];
                    if (i >= map.Length) { Fail(0x86); return; }
                    uint p = SegB(I386.SEG_ES) + DI;
                    PhysW32(p, (uint)map[i].b); PhysW32(p + 4, (uint)(map[i].b >> 32));
                    PhysW32(p + 8, (uint)map[i].l); PhysW32(p + 12, (uint)(map[i].l >> 32));
                    PhysW32(p + 16, map[i].t);
                    C.R[0] = 0x534d4150; C.R[1] = 20; C.R[3] = i + 1 < map.Length ? i + 1 : 0;
                    Ok(); return;
                }
                break;
        }
        Fail(0x86);
    }

    // ================= INT 13h: disks =================

    private DiskImage? Disk(int dl) => dl switch { 0 => _cdEmu ?? Floppy[0], 1 => Floppy[1], 0x80 => Hdd[0], 0x81 => Hdd[1], _ => null };

    // Windows 98's ESDI_506.PDR (the protected-mode IDE driver) traps the controller's ports and checks an INT 13h per drive against them:
    // a BIOS that reaches the disk without the ports never calls its trap ("ESDI trap handler not called" in IOS.LOG), one that touches
    // them without the real command gets "ESDI sector mismatch", and either way the drives stay in MS-DOS compatibility mode (the primary
    // channel shows Code 10). So on an IDE machine INT 13h enters at F000:E880, and from a V86 VM a hard disk read / write / verify is done
    // the way a real BIOS does it: C# only fills the task file at F000:DF00 (LBA addressing) and the code after the trap issues the command
    // and moves the data with REP INSW / OUTSW. Everything else (and anything outside V86 mode) is still the C# service.
    private const uint Int13Pio = 0xe880, Int13Task = 0xdf00;
    private static readonly byte[] Int13PioCode =
    {
        0xe6, 0xe0, 0x1e, 0x06, 0x60, 0x0e, 0x1f, 0xbe, 0x00, 0xdf, 0xac, 0x84, 0xc0, 0x74, 0x4f, 0x88, 0xc3, 0xba, 0xf1, 0x01, 0xb9, 0x06, 0x00, 0xac,
        0xee, 0x42, 0xe2, 0xfb, 0xac, 0xee, 0xad, 0x89, 0xc5, 0xad, 0x89, 0xc7, 0xad, 0x8e, 0xc0, 0xba, 0xf7, 0x01, 0xec, 0xa8, 0x80, 0x75, 0xfb, 0x80,
        0xfb, 0x03, 0x74, 0x2a, 0xa8, 0x08, 0x74, 0x26, 0xba, 0xf0, 0x01, 0xb9, 0x00, 0x01, 0x80, 0xfb, 0x01, 0x75, 0x04, 0xf3, 0x6d, 0xeb, 0x0c, 0x1e,
        0x06, 0x1f, 0x89, 0xfe, 0xf3, 0x6f, 0x1f, 0x81, 0xc7, 0x00, 0x02, 0x4d, 0x75, 0xd1, 0xba, 0xf7, 0x01, 0xec, 0xa8, 0x80, 0x75, 0xfb, 0x61, 0x07,
        0x1f, 0xcf,
        // push ds, es / pusha / ds = cs / si = task / lodsb mode: 0 = nothing; regs 1F1-1F6, command, sector count, buffer off:seg; per sector:
        // wait BSY clear, DRQ, REP INSW (mode 1) or OUTSW (2), mode 3 = no data; wait BSY clear; popa / pop es, ds / iret
    };

    private void InstallInt13Pio() { Array.Copy(Int13PioCode, 0, Cpu.Mem, Rom + Int13Pio, Int13PioCode.Length); _special[Int13Pio] = 0x13; }

    /// <summary>True when the read / write / verify was handed to the PIO code (V86 mode, IDE hard disk): the task file is filled, nothing is moved here.</summary>
    private bool Int13PioSetup(int dl, uint fn)
    {
        var m = Cpu.Mem; uint t = Rom + Int13Task;
        m[t] = 0;
        if (Ata == null || (C.Flags & I386.VM) == 0 || dl is not (0x80 or 0x81) || Hdd[dl - 0x80] is not { } d) return false;
        if (SegB(I386.SEG_CS) + C.Ip != Rom + Int13Pio) return false;   // (entered through the old stub)
        long lba; int count; uint seg, off;
        if (fn is 0x02 or 0x03 or 0x04)
        {
            count = (int)AL;
            int cyl = (int)(CH | ((CL & 0xc0) << 2)), sec = (int)(CL & 0x3f), head = (int)DH;
            if (sec == 0 || sec > d.Sectors || head >= d.Heads || count == 0) return false;
            lba = ((long)cyl * d.Heads + head) * d.Sectors + sec - 1;
            seg = C.SegSel[I386.SEG_ES]; off = BX;
        }
        else if (fn is 0x42 or 0x43 or 0x44)
        {
            uint p = SegB(I386.SEG_DS) + SI;
            count = Lin16(p + 2); off = Lin16(p + 4); seg = Lin16(p + 6);
            lba = Lin32(p + 8) | ((long)Lin32(p + 12) << 32);
            if (Lin32(p + 4) == 0xffffffff || count is 0 or > 127) return false;
        }
        else return false;
        if (count > 255 || lba + count > d.TotalSectors || lba >= 1 << 28 || off + count * 512 > 0x10000 && fn != 0x04 && fn != 0x44) return false;
        bool write = fn is 0x03 or 0x43, verify = fn is 0x04 or 0x44;
        m[t] = (byte)(verify ? 3 : write ? 2 : 1);
        m[t + 1] = 0; m[t + 2] = (byte)count; m[t + 3] = (byte)lba; m[t + 4] = (byte)(lba >> 8); m[t + 5] = (byte)(lba >> 16);
        m[t + 6] = (byte)(0xe0 | (dl == 0x81 ? 0x10 : 0) | ((lba >> 24) & 15));
        m[t + 7] = (byte)(verify ? 0x40 : write ? 0x30 : 0x20);
        m[t + 8] = (byte)count; m[t + 9] = 0; m[t + 10] = (byte)off; m[t + 11] = (byte)(off >> 8); m[t + 12] = (byte)seg; m[t + 13] = (byte)(seg >> 8);
        if (fn is 0x02 or 0x03 or 0x04) AL = (uint)count;
        DiskStatus(dl, 0);
        return true;
    }

    private void DiskStatus(int dl, uint st) { LinW8(dl >= 0x80 ? 0x474u : 0x441u, (byte)st); AH = st; SetCF(st != 0); }

    private void Int13()
    {
        int dl = (int)DL;
        uint fn = AH;
        if (Int13PioSetup(dl, fn)) return;
        if (Ata != null && Int13Ide(dl, fn)) return;
        var d = Disk(dl);
        if (fn == 0x01) { AH = Lin8(dl >= 0x80 ? 0x474u : 0x441u); SetCF(AH != 0); return; }
        if (fn == 0x08 && dl < 0x80 && d == null) { Fail(0x01); return; }
        if (fn == 0x15 && d == null) { AH = 0; Ok(); return; }
        if (d == null) { DiskStatus(dl, dl >= 0x80 ? 0x01u : 0x80u); return; }
        switch (fn)
        {
            case 0x00: case 0x04: case 0x05: case 0x0c: case 0x0d: case 0x10: case 0x11: case 0x14: case 0x16: case 0x17: case 0x18:
                DiskStatus(dl, 0); return;
            case 0x02: case 0x03:
            {
                int count = (int)AL;
                int cyl = (int)(CH | ((CL & 0xc0) << 2)), sec = (int)(CL & 0x3f), head = (int)DH;
                if (sec == 0 || sec > d.Sectors || head >= d.Heads || count == 0) { AL = 0; DiskStatus(dl, 0x04); return; }
                long lba = ((long)cyl * d.Heads + head) * d.Sectors + sec - 1;
                uint buf = SegB(I386.SEG_ES) + BX;
                if (!Transfer(d, lba, count, buf, fn == 0x03)) { AL = 0; DiskStatus(dl, 0x04); return; }
                AL = (uint)count; DiskStatus(dl, 0); return;
            }
            case 0x08:
                if (d.IsFloppy)
                {
                    BL = d.Sectors switch { 9 => d.Cylinders == 40 ? 1u : 3u, 15 => 2u, 36 => 5u, _ => 4u };
                    CH = (uint)(d.Cylinders - 1); CL = (uint)d.Sectors; DH = (uint)(d.Heads - 1);
                    DL = (uint)((Floppy[0] != null || _cdEmu != null ? 1 : 0) + (Floppy[1] != null ? 1 : 0));
                    SetEs(RomSeg); DI = DisketteTable; AX = 0;
                }
                else
                {
                    int c = d.Cylinders - 1;
                    CH = (uint)(c & 0xff); CL = (uint)(d.Sectors | ((c >> 2) & 0xc0)); DH = (uint)(d.Heads - 1);
                    DL = (uint)((Hdd[0] != null ? 1 : 0) + (Hdd[1] != null ? 1 : 0));
                    AX = 0; BX = 0;
                }
                Ok(); return;
            case 0x15:
                if (d.IsFloppy) { AH = 0x01; Ok(); return; }
                { long n = d.TotalSectors; CX = (uint)(n >> 16) & 0xffff; DX = (uint)n & 0xffff; AH = 3; Ok(); return; }
            case 0x41:
                if (BX != 0x55aa || d.IsFloppy) { Fail(0x01); return; }
                BX = 0xaa55; AH = 0x21; CX = 0x0001; Ok(); return;
            case 0x42: case 0x43:
            {
                uint p = SegB(I386.SEG_DS) + SI;
                int count = Lin16(p + 2);
                uint buf = (uint)(Lin16(p + 6) << 4) + Lin16(p + 4);
                long lba = Lin32(p + 8) | ((long)Lin32(p + 12) << 32);
                if (Lin32(p + 4) == 0xffffffff && Lin8(p) >= 0x18) buf = Lin32(p + 16);
                if (!Transfer(d, lba, count, buf, fn == 0x43)) { LinW16(p + 2, 0); DiskStatus(dl, 0x04); return; }
                DiskStatus(dl, 0); return;
            }
            case 0x44: case 0x47: DiskStatus(dl, 0); return;
            case 0x48:
            {
                uint p = SegB(I386.SEG_DS) + SI;
                if (Lin16(p) < 0x1a) { Fail(0x01); return; }
                LinW16(p, 0x1a); LinW16(p + 2, 0x02);
                LinW32(p + 4, (uint)d.Cylinders); LinW32(p + 8, (uint)d.Heads); LinW32(p + 12, (uint)d.Sectors);
                LinW32(p + 16, (uint)d.TotalSectors); LinW32(p + 20, (uint)(d.TotalSectors >> 32)); LinW16(p + 24, 512);
                DiskStatus(dl, 0); return;
            }
        }
        DiskStatus(dl, 0x01);
    }

    private bool Transfer(DiskImage d, long lba, int count, uint buf, bool write)
    {
        var tmp = new byte[count * 512];
        if (write)
        {
            for (int i = 0; i < tmp.Length; i++) tmp[i] = Lin8(buf + (uint)i);
            return d.Write(lba, count, tmp);
        }
        if (!d.Read(lba, count, tmp)) return false;
        bool paged = (Cpu.Cr0 & 0x80000000) != 0;
        if (!paged && (buf + tmp.Length <= 0xa0000 || buf >= 0xc0000 && buf + tmp.Length <= Cpu.MemSize)) { Array.Copy(tmp, 0, Cpu.Mem, buf, tmp.Length); Cpu.MemTouched(); }
        else for (int i = 0; i < tmp.Length; i++) LinW8(buf + (uint)i, tmp[i]);
        return true;
    }
}
