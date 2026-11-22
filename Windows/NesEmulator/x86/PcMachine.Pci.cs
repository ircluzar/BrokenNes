// The PCI board (docs/projects/x86-modular-pc.md, M4): an i430TX host bridge and a PIIX4 (ISA bridge, IDE) on PCI bus 0, configuration
// mechanism #1 (CF8h / CFCh), the PIIX4's PIRQ router and ELCR, the S3 Trio64 when it is the card; and the BIOS's Windows 98 feature set
// that switches on with it: the PCI BIOS (INT 1Ah B1xxh, real mode and the 32-bit interface found through the BIOS32 service directory),
// the $PIR routing table, APM 1.2 (INT 15h 53xxh, real mode and the 16/32-bit protected-mode interfaces) and the PnP BIOS ($PnP, system
// device nodes). Written for BrokenNes from the PCI BIOS 2.1, BIOS32, PCI IRQ Routing Table 1.0, APM 1.2 and Plug and Play BIOS 1.0A
// specifications. Only a Pci430TX-config machine builds any of this: the classic machine's ROM and ports are byte for byte unchanged.
//
// Protected-mode entry points cannot use the real-mode trap (port E0h, located by CS:IP), so each one is OUT to its own port, then RETF:
// E1h the BIOS32 directory, E2h the 32-bit PCI BIOS, E3h the APM protected-mode interface, E4h the PnP BIOS (real and protected mode).
// Those calls return CF in the live EFLAGS (no IRET frame).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NesEmulator.X86;

/// <summary>One PCI function's 256-byte configuration space (header type 0); the writable bytes are the command, latency, interrupt line and the device-specific area.</summary>
/// <summary>A PCI device's memory-mapped window (BAR0). Offsets are relative to the BAR base; sizes 1, 2 or 4 bytes.</summary>
public interface IPciMmio
{
    uint MmioRead(uint off, int size);
    void MmioWrite(uint off, uint v, int size);
}

public sealed class PciFunction
{
    public readonly byte[] Cfg = new byte[256];
    public readonly string Name;
    public PciFunction(string name, ushort vendor, ushort device, uint classCode, byte rev, bool multi = false)
    {
        Name = name;
        W16(0, vendor); W16(2, device); Cfg[8] = rev;
        Cfg[9] = (byte)classCode; Cfg[10] = (byte)(classCode >> 8); Cfg[11] = (byte)(classCode >> 16);
        Cfg[0x0e] = (byte)(multi ? 0x80 : 0);
    }
    public void W16(int o, int v) { Cfg[o] = (byte)v; Cfg[o + 1] = (byte)(v >> 8); }
    public uint Read32(int reg) { reg &= 0xfc; return (uint)(Cfg[reg] | Cfg[reg + 1] << 8 | Cfg[reg + 2] << 16 | Cfg[reg + 3] << 24); }
    /// <summary>Size of the I/O window behind BAR0 (0: no BAR). The low bits read back as zero (bit 0 = I/O space), so sizing works.</summary>
    public int IoBarSize;
    /// <summary>Size of the I/O window behind BAR4 (the PIIX4 IDE's bus-master registers; 0: none).</summary>
    public int Bar4Size;
    public int Bar4Base => (Cfg[4] & 1) != 0 && Bar4Size != 0 && (Read32(0x20) & 0xfffc) != 0 ? (int)(Read32(0x20) & 0xfffc & ~(uint)(Bar4Size - 1)) : -1;
    /// <summary>Size of the memory window behind BAR0 (a power of two, 0: none). The general PCI MMIO facility (Voodoo now, a GeForce later).</summary>
    public uint MemBarSize;
    /// <summary>The device behind the memory BAR.</summary>
    public IPciMmio? Mmio;
    /// <summary>BAR0's memory base, or 0 when memory decode (command bit 1) is off or the BAR is unplaced.</summary>
    public uint MemBase => (Cfg[4] & 2) != 0 && MemBarSize != 0 ? Read32(0x10) & ~(MemBarSize - 1) : 0;
    public int IoBase => (Cfg[4] & 1) != 0 && IoBarSize != 0 ? (int)(Read32(0x10) & 0xfffc & ~(uint)(IoBarSize - 1)) : -1;
    public void WriteByte(int reg, byte v)
    {
        if (reg is >= 0x10 and <= 0x13 && MemBarSize != 0)
        {
            Cfg[reg] = v;
            uint b = Read32(0x10) & ~(MemBarSize - 1);   // memory space, 32-bit, not prefetchable
            for (int i = 0; i < 4; i++) Cfg[0x10 + i] = (byte)(b >> (8 * i));
        }
        else if (reg is >= 0x10 and <= 0x13 && IoBarSize != 0)
        {
            Cfg[reg] = v;
            uint b = (Read32(0x10) & ~(uint)(IoBarSize - 1) & 0xffff) | 1;
            for (int i = 0; i < 4; i++) Cfg[0x10 + i] = (byte)(b >> (8 * i));
        }
        else if (reg is >= 0x20 and <= 0x23 && Bar4Size != 0)
        {
            Cfg[reg] = v;
            uint b = (Read32(0x20) & ~(uint)(Bar4Size - 1) & 0xffff) | 1;
            for (int i = 0; i < 4; i++) Cfg[0x20 + i] = (byte)(b >> (8 * i));
        }
        else if (reg == 0x04) Cfg[4] = (byte)(v & 0x07);
        else if (reg is 0x05 or 0x0c or 0x0d or 0x3c || reg >= 0x40) Cfg[reg] = v;
    }
}

/// <summary>PCI bus 0 behind configuration mechanism #1, plus the PIIX4 I/O it owns (CF9h reset control, 4D0h/4D1h ELCR).</summary>
public sealed class PciBus
{
    public const byte HostDevFn = 0x00, IsaDevFn = 0x38, IdeDevFn = 0x39, VgaDevFn = 0x48, NicDevFn = 0x50, Voodoo1DevFn = 0x58;   // 00.0, 07.0, 07.1, 09.0, 0A.0
    private readonly PcMachine _m;
    public readonly SortedDictionary<byte, PciFunction> Functions = new();
    public uint Address;
    public byte Elcr1, Elcr2, ResetControl;
    public PciFunction Isa => Functions[IsaDevFn];
    public PciFunction? NicFn;
    /// <summary>The functions with a memory BAR (looked up on accesses at 0xE0000000 and up).</summary>
    public PciFunction[] MmioFns = Array.Empty<PciFunction>();
    public PciFunction? MmioAt(uint a)
    {
        foreach (var f in MmioFns) { uint b = f.MemBase; if (b != 0 && a - b < f.MemBarSize) return f; }
        return null;
    }

    public PciBus(PcMachine m) { _m = m; Reset(); }

    public void Reset()
    {
        Functions.Clear();
        Functions[HostDevFn] = new PciFunction("i430TX host bridge", 0x8086, 0x7100, 0x060000, 0x01);
        var isa = Functions[IsaDevFn] = new PciFunction("PIIX4 ISA bridge", 0x8086, 0x7110, 0x060100, 0x02, multi: true);
        for (int i = 0; i < 4; i++) isa.Cfg[0x60 + i] = 0x80;     // PIRQA-D routing: disabled until POST routes them
        if (_m.Ata != null)
        {
            var ide = Functions[IdeDevFn] = new PciFunction("PIIX4 IDE", 0x8086, 0x7111, 0x010180, 0x01) { Bar4Size = 16 };
            ide.Cfg[0x20] = 1;                                    // BAR4: the bus-master registers (I/O), placed by POST
            ide.Cfg[4] = 0x01;                                    // I/O decode on, legacy ports (no bus master DMA yet)
            ide.Cfg[0x41] = 0x80; ide.Cfg[0x43] = 0x80;           // IDETIM: both channels' decode enabled
        }
        if (_m.Nic != null && Environment.GetEnvironmentVariable("X86_NONIC") is null or "" or "0")   // (X86_NONIC: the card left off the bus, a diagnostic)
        {
            var nic = NicFn = Functions[NicDevFn] = new PciFunction("RTL8029 Ethernet", Rtl8029.Vendor, Rtl8029.Device, 0x020000, 0x00) { IoBarSize = Rtl8029.IoSize };
            nic.Cfg[0x10] = 1; nic.Cfg[0x3d] = 1;                 // I/O BAR, INTA#
            nic.W16(0x2c, Rtl8029.Vendor); nic.W16(0x2e, Rtl8029.Device);
        }
        if (_m.Voodoo != null)
        {
            var vf = Functions[Voodoo1DevFn] = new PciFunction("3dfx Voodoo Graphics", Voodoo1.Vendor, Voodoo1.Device, 0x040000, 0x02) { MemBarSize = Voodoo1.MemSize, Mmio = _m.Voodoo };
            vf.Cfg[0x3d] = 1;                                     // INTA# (present on the card, unused by the Voodoo 1 drivers)
            _m.Voodoo.Pci = vf;
        }
        MmioFns = Functions.Values.Where(f => f.Mmio != null).ToArray();
        Functions[HostDevFn].Cfg[4] = 0x06;
        isa.Cfg[4] = 0x07;
        Address = 0; Elcr1 = Elcr2 = 0; ResetControl = 0;
    }

    private bool Has(byte devFn) => Functions.ContainsKey(devFn) || devFn == VgaDevFn && _m.S3 != null;

    private static readonly bool PciLog = Environment.GetEnvironmentVariable("X86_PCILOG") == "1";   // diagnosis: the VGA function's config accesses
    private int _pciLogN;
    public uint ConfigRead(int bus, byte devFn, int reg)
    {
        if (bus != 0 || !Has(devFn)) return 0xffffffff;
        if (devFn == VgaDevFn && !Functions.ContainsKey(devFn)) { uint r = _m.S3!.PciRead(reg); if (PciLog && _pciLogN++ < 600) Console.WriteLine($"pcilog rd {reg:X2} = {r:X8}"); return r; }
        return Functions[devFn].Read32(reg);
    }

    /// <summary>Writes <paramref name="size"/> bytes at <paramref name="reg"/> (any alignment inside one dword).</summary>
    public void ConfigWrite(int bus, byte devFn, int reg, uint v, int size)
    {
        if (bus != 0 || !Has(devFn)) return;
        if (devFn == VgaDevFn && !Functions.ContainsKey(devFn))
        {
            int sh = (reg & 3) * 8;
            uint mask = size == 4 ? 0xffffffff : (uint)((1L << (size * 8)) - 1) << sh;
            uint old = _m.S3!.PciRead(reg);
            if (PciLog && _pciLogN++ < 600) Console.WriteLine($"pcilog wr {reg:X2} size {size} = {v:X8}");
            _m.S3.PciWrite(reg, (old & ~mask) | ((v << sh) & mask));
            return;
        }
        var f = Functions[devFn];
        for (int i = 0; i < size; i++) f.WriteByte((reg & 0xfc) + (reg & 3) + i, (byte)(v >> (8 * i)));
    }

    public bool In(int port, int size, out uint v)
    {
        v = 0;
        if (port == 0xcf8 && size == 4) { v = Address; return true; }
        if (port is >= 0xcfc and <= 0xcff)
        {
            if ((Address & 0x80000000) == 0) { v = size == 4 ? 0xffffffff : size == 2 ? 0xffffu : 0xffu; return true; }
            uint d = ConfigRead((int)(Address >> 16) & 0xff, (byte)(Address >> 8), (int)(Address & 0xfc));
            d >>= (port & 3) * 8;
            v = size == 4 ? d : size == 2 ? d & 0xffff : d & 0xff;
            return true;
        }
        if (port == 0xcf9 && size == 1) { v = ResetControl; return true; }
        if (port == 0x4d0 && size == 1) { v = Elcr1; return true; }
        if (port == 0x4d1 && size == 1) { v = Elcr2; return true; }
        return false;
    }

    public bool Out(int port, int size, uint v)
    {
        if (port == 0xcf8 && size == 4) { Address = v & 0x80fffffc; return true; }
        if (port is >= 0xcfc and <= 0xcff)
        {
            if ((Address & 0x80000000) != 0) ConfigWrite((int)(Address >> 16) & 0xff, (byte)(Address >> 8), (int)(Address & 0xfc) + (port & 3), v, size);
            return true;
        }
        if (port == 0xcf9 && size == 1)
        {
            bool go = (v & 4) != 0 && (ResetControl & 4) == 0;
            ResetControl = (byte)(v & 0x0a);
            if (go) _m.PciResetRequest();
            return true;
        }
        if (port == 0x4d0 && size == 1) { Elcr1 = (byte)(v & 0xf8); return true; }
        if (port == 0x4d1 && size == 1) { Elcr2 = (byte)(v & 0xde); return true; }
        return false;
    }

    public void Save(BinaryWriter w)
    {
        w.Write(Address); w.Write(Elcr1); w.Write(Elcr2); w.Write(ResetControl);
        w.Write(Functions.Count);
        foreach (var (df, f) in Functions) { w.Write(df); w.Write(f.Cfg); }
    }

    public void Load(BinaryReader r)
    {
        Address = r.ReadUInt32(); Elcr1 = r.ReadByte(); Elcr2 = r.ReadByte(); ResetControl = r.ReadByte();
        int n = r.ReadInt32();
        // a state saved before the network card: the card stays as a cold boot left it (unconfigured), everything else must match
        bool oldNoNic = Functions.ContainsKey(NicDevFn) && n == Functions.Count - 1;
        if (n != Functions.Count && !oldNoNic) throw new InvalidDataException($"this x86 state's PCI bus has {n} functions, this machine has {Functions.Count}");
        for (int i = 0; i < n; i++)
        {
            byte df = r.ReadByte();
            if (!Functions.TryGetValue(df, out var f)) throw new InvalidDataException($"this x86 state has PCI function {df:X2}, this machine does not");
            r.Read(f.Cfg, 0, 256);
        }
    }
}

public sealed partial class PcMachine
{
    /// <summary>The PCI bus (null on an ISA board: the default).</summary>
    public readonly PciBus? Pci;

    public const int Bios32Port = 0xe1, Pci32Port = 0xe2, ApmPmPort = 0xe3, PnpPort = 0xe4;
    // ROM offsets in F000h (the classic machine leaves 0000-9FFF empty)
    private const uint Bios32Dir = 0x9000, PirTable = 0x9020, PnpHeader = 0x9100,
        Bios32Entry = 0x9200, Pci32Entry = 0x9210, ApmEntry = 0x9220, PnpRealEntry = 0x9230, PnpPmEntry = 0x9240;
    private const int SvgaIrq = 11, NicIrq = 10, NicIoBase = 0xe000, IdeBmBase = 0xe040;
    private bool _apmConnected;
    private byte _apmInterface;               // 0 none, 1 real, 2 16-bit PM, 3 32-bit PM

    internal void PciResetRequest() => CpuResetRequest();

    private void SavePci(BinaryWriter w) { Pci!.Save(w); w.Write(_apmConnected); w.Write(_apmInterface); }
    private void LoadPci(BinaryReader r) { Pci!.Load(r); _apmConnected = r.ReadBoolean(); _apmInterface = r.ReadByte(); }

    // ================= POST =================

    private void PostPci()
    {
        var m = Cpu.Mem;
        void Code(uint off, params byte[] b) => Array.Copy(b, 0, m, Rom + off, b.Length);
        void Sum(uint off, int len, int at) { byte s = 0; m[Rom + off + at] = 0; for (int i = 0; i < len; i++) s += m[Rom + off + i]; m[Rom + off + at] = (byte)-s; }

        // the protected-mode / far-call entry points: OUT port,AL / RETF
        Code(Bios32Entry, 0xe6, Bios32Port, 0xcb);
        Code(Pci32Entry, 0xe6, Pci32Port, 0xcb);
        Code(ApmEntry, 0xe6, ApmPmPort, 0xcb);
        Code(PnpRealEntry, 0xe6, PnpPort, 0xcb);
        Code(PnpPmEntry, 0xe6, PnpPort, 0xcb);

        // BIOS32 service directory: "_32_", entry (physical), revision 0, length in paragraphs (1), checksum
        Code(Bios32Dir, Encoding.ASCII.GetBytes("_32_"));
        PhysW32(Rom + Bios32Dir + 4, Rom + Bios32Entry); m[Rom + Bios32Dir + 8] = 0; m[Rom + Bios32Dir + 9] = 1;
        Sum(Bios32Dir, 16, 10);

        // route PIRQA (the S3's INTA#) to IRQ 11
        var pci = Pci!;
        if (S3 != null)
        {
            pci.Isa.Cfg[0x60] = SvgaIrq;
            pci.ConfigWrite(0, PciBus.VgaDevFn, 0x3c, SvgaIrq, 1);
            pci.Elcr2 = 1 << (SvgaIrq - 8);
        }
        if (pci.Functions.TryGetValue(PciBus.IdeDevFn, out var ide))
        {
            ide.Cfg[0x3c] = 14;
            pci.ConfigWrite(0, PciBus.IdeDevFn, 0x20, IdeBmBase | 1, 4);   // bus-master DMA registers at E040h
            pci.ConfigWrite(0, PciBus.IdeDevFn, 0x04, 0x05, 1);            // I/O + bus master
            // the BIOS marks each present drive "DMA capable" in its channel's bus-master status (bits 5 / 6): Windows' ESDI_506 looks there before it uses DMA
            if (Ata != null) { Ata.Primary.BmWrite(2, (byte)((Hdd[0] != null ? 0x20 : 0) | (Hdd[1] != null ? 0x40 : 0))); Ata.Secondary.BmWrite(2, 0x20); }
            ide.Cfg[0x40] = 0x07; ide.Cfg[0x41] = 0xa3; ide.Cfg[0x42] = 0x07; ide.Cfg[0x43] = 0xa3;
            if (Environment.GetEnvironmentVariable("X86_UDMA") == "1") { ide.Cfg[0x48] = 0x01; ide.Cfg[0x4a] = 0x02; }   // (test: UDMACTL drive 0 on, UDMATIM mode 2, as a BIOS that set Ultra DMA)   // IDETIM: decode on, fast timing + prefetch + IORDY for drive 0 (as a BIOS leaves it for DMA mode 2)
        }
        // the network card: I/O at E000h (32 ports), INTA# -> PIRQB -> IRQ 10 (level-triggered)
        if (pci.Functions.TryGetValue(PciBus.NicDevFn, out var nic))
        {
            pci.ConfigWrite(0, PciBus.NicDevFn, 0x10, NicIoBase | 1, 4);
            pci.ConfigWrite(0, PciBus.NicDevFn, 0x04, 0x01, 1);
            pci.Isa.Cfg[0x61] = NicIrq;
            nic.Cfg[0x3c] = NicIrq;
            pci.Elcr2 |= 1 << (NicIrq - 8);
        }

        // the Voodoo: its 16 MB window at E8000000h (clear of the S3's 64 MB at E0000000h), memory decode on, no I/O
        if (pci.Functions.ContainsKey(PciBus.Voodoo1DevFn))
        {
            pci.ConfigWrite(0, PciBus.Voodoo1DevFn, 0x10, 0xe8000000, 4);
            pci.ConfigWrite(0, PciBus.Voodoo1DevFn, 0x04, 0x02, 1);
        }

        // $PIR: the routing table (one slot entry per device that has an interrupt pin)
        var pir = BuildPir();
        Array.Copy(pir, 0, m, Rom + PirTable, pir.Length);

        // $PnP installation check structure
        Code(PnpHeader, Encoding.ASCII.GetBytes("$PnP"));
        m[Rom + PnpHeader + 4] = 0x10; m[Rom + PnpHeader + 5] = 0x21;
        PhysW16(Rom + PnpHeader + 0x0d, (ushort)PnpRealEntry); PhysW16(Rom + PnpHeader + 0x0f, (ushort)RomSeg);
        PhysW16(Rom + PnpHeader + 0x11, (ushort)PnpPmEntry); PhysW32(Rom + PnpHeader + 0x13, Rom);
        PhysW16(Rom + PnpHeader + 0x1b, (ushort)RomSeg); PhysW32(Rom + PnpHeader + 0x1d, Rom);
        Sum(PnpHeader, 0x21, 8);

        _apmConnected = false; _apmInterface = 0;
    }

    private byte[] BuildPir()
    {
        var slots = new List<byte[]>();
        // devices 07 (PIIX4: IDE is legacy, USB absent) and 09 (the S3): links 60h-63h = PIRQA-D, all ISA IRQs but 0-2 / 8 / 13 possible
        const ushort map = 0xdef8;
        byte[] Slot(byte dev, byte slot, params byte[] links)
        {
            var e = new byte[16];
            e[0] = 0; e[1] = (byte)(dev << 3);
            for (int i = 0; i < 4; i++) { e[2 + i * 3] = links[i]; e[3 + i * 3] = map & 0xff; e[4 + i * 3] = map >> 8; }
            e[14] = slot;
            return e;
        }
        slots.Add(Slot(7, 0, 0x60, 0x61, 0x62, 0x63));
        if (S3 != null) slots.Add(Slot(PciBus.VgaDevFn >> 3, 1, 0x60, 0x61, 0x62, 0x63));
        if (Nic != null) slots.Add(Slot(PciBus.NicDevFn >> 3, 2, 0x61, 0x62, 0x63, 0x60));
        int size = 32 + 16 * slots.Count;
        var t = new byte[size];
        Encoding.ASCII.GetBytes("$PIR").CopyTo(t, 0);
        t[4] = 0; t[5] = 1;                                    // version 1.0
        t[6] = (byte)size; t[7] = (byte)(size >> 8);
        t[8] = 0; t[9] = PciBus.IsaDevFn;                      // the router: 00:07.0
        t[12] = 0x86; t[13] = 0x80; t[14] = 0x10; t[15] = 0x71;   // compatible router 8086:7110
        for (int i = 0; i < slots.Count; i++) slots[i].CopyTo(t, 32 + 16 * i);
        byte s = 0; foreach (byte b in t) s += b;
        t[31] = (byte)-s;
        return t;
    }

    // ================= the protected-mode trap ports =================

    private bool PmTrap(int port)
    {
        if (Pci == null || port is < Bios32Port or > PnpPort) return false;
        try
        {
            switch (port)
            {
                case Bios32Port: Bios32Call(); break;
                case Pci32Port: PciBiosCall(frame: false); break;
                case ApmPmPort: ApmCall(frame: false); break;
                case PnpPort: PnpCall(); break;
            }
        }
        catch (I386AbortException) { throw; }
        catch (Exception e) { Stopped = $"BIOS service at port {port:X2}h AX={AX:X4} failed: {e.Message}"; C.EndBatch(); }
        return true;
    }

    /// <summary>CF for the caller: in the IRET frame (an INT call) or in the live EFLAGS (a far call).</summary>
    private void Carry(bool frame, bool on)
    {
        if (frame) SetCF(on);
        else C.SetFlags(on ? I386.CF : 0, on ? 0 : I386.CF);
    }

    private void Bios32Call()
    {
        // EAX = service id, BL = 0 (the only function)
        if ((C.R[3] & 0xff) != 0) { C.R[0] = (C.R[0] & ~0xffu) | 0x81; return; }
        if (C.R[0] == 0x49435024)   // "$PCI"
        {
            C.R[0] &= ~0xffu; C.R[3] = Rom; C.R[1] = 0x10000; C.R[2] = Pci32Entry;
            return;
        }
        C.R[0] = (C.R[0] & ~0xffu) | 0x80;
    }

    // ================= PCI BIOS (INT 1Ah B1xxh and the 32-bit interface) =================

    private void PciBiosCall(bool frame)
    {
        var pci = Pci!;
        void Done(uint ah) { AH = ah; Carry(frame, ah != 0); }
        if (AH != 0xb1) { Done(0x81); return; }
        int bus = (int)BH; byte devFn = (byte)BL; int reg = (int)DI & 0xff;
        switch (AL)
        {
            case 0x01:
                C.R[2] = 0x20494350;   // "PCI "
                AL = 0x01; BH = 0x02; BL = 0x10; CL = 0x00;
                C.R[7] = Rom + Pci32Entry;
                Done(0); return;
            case 0x02:
            case 0x03:
            {
                int index = (int)SI, n = 0;
                foreach (byte df in Devices())
                {
                    uint id = pci.ConfigRead(0, df, 0), cls = pci.ConfigRead(0, df, 8) >> 8;
                    bool hit = AL == 0x02 ? (id & 0xffff) == DX && id >> 16 == CX : cls == (C.R[1] & 0xffffff);
                    if (hit && n++ == index) { BH = 0; BL = df; Done(0); return; }
                }
                Done(0x86); return;
            }
            case 0x06: Done(0x81); return;
            case 0x08: if (bus != 0) { Done(0x86); return; } CL = (pci.ConfigRead(0, devFn, reg) >> ((reg & 3) * 8)) & 0xff; Done(0); return;
            case 0x09: if ((reg & 1) != 0) { Done(0x87); return; } CX = (pci.ConfigRead(0, devFn, reg) >> ((reg & 2) * 8)) & 0xffff; Done(0); return;
            case 0x0a: if ((reg & 3) != 0) { Done(0x87); return; } C.R[1] = pci.ConfigRead(0, devFn, reg); Done(0); return;
            case 0x0b: pci.ConfigWrite(bus, devFn, reg, CL, 1); Done(0); return;
            case 0x0c: if ((reg & 1) != 0) { Done(0x87); return; } pci.ConfigWrite(bus, devFn, reg, CX, 2); Done(0); return;
            case 0x0d: if ((reg & 3) != 0) { Done(0x87); return; } pci.ConfigWrite(bus, devFn, reg, C.R[1], 4); Done(0); return;
            case 0x0e:
            {
                // ES:DI (E)DI -> { WORD size; data pointer } (16-bit: offset, segment; 32-bit: DWORD offset, WORD selector)
                uint p = frame ? DI : C.R[7];
                if (!C.CpuLoad(16, I386.SEG_ES, p, out uint have)) { Done(0x89); return; }
                uint off, sel;
                if (frame) { C.CpuLoad(16, I386.SEG_ES, p + 2, out off); C.CpuLoad(16, I386.SEG_ES, p + 4, out sel); }
                else { C.CpuLoad(32, I386.SEG_ES, p + 2, out off); C.CpuLoad(16, I386.SEG_ES, p + 6, out sel); }
                var pir = BuildPir();
                int n = (pir.Length - 32);
                C.CpuStore(16, I386.SEG_ES, p, (uint)n);
                BX = 0;                                         // no IRQ is PCI-exclusive
                if (have < n) { Done(0x89); return; }
                var data = new byte[n]; Array.Copy(pir, 32, data, 0, n);
                if (!FarWrite(sel, off, data)) { Done(0x89); return; }
                Done(0); return;
            }
            case 0x0f:
            {
                // CL = pin (0Ah-0Dh = INTA#-INTD#), CH = IRQ
                int pin = (int)CL - 0x0a; uint irq = CH;
                if (pin is < 0 or > 3 || irq > 15) { Done(0x88); return; }
                int link = (pin + (devFn >> 3 == PciBus.NicDevFn >> 3 ? 1 : 0)) & 3;   // the $PIR: slot 0A rotates its pins by one link
                pci.Isa.Cfg[0x60 + link] = (byte)(irq == 0 ? 0x80 : irq);
                pci.ConfigWrite(0, devFn, 0x3c, irq, 1);
                Done(0); return;
            }
        }
        Done(0x81);
    }

    private IEnumerable<byte> Devices()
    {
        var list = new List<byte>(Pci!.Functions.Keys);
        if (S3 != null && !list.Contains(PciBus.VgaDevFn)) list.Add(PciBus.VgaDevFn);
        list.Sort();
        return list;
    }

    /// <summary>Writes host bytes at selector:offset (a real-mode segment in real / V86 mode), through ES, which is put back.</summary>
    private bool FarWrite(uint sel, uint off, byte[] data)
    {
        uint oldEs = C.SegSel[I386.SEG_ES], oldBase = C.SegBase[I386.SEG_ES], oldLim = C.SegLimit[I386.SEG_ES], oldFl = C.SegFlags[I386.SEG_ES];
        bool ok = C.LoadSeg(I386.SEG_ES, sel);
        for (int i = 0; ok && i < data.Length; i++) ok = C.CpuStore(8, I386.SEG_ES, off + (uint)i, data[i]);
        C.SegGen++; C.SegSel[I386.SEG_ES] = oldEs; C.SegBase[I386.SEG_ES] = oldBase; C.SegLimit[I386.SEG_ES] = oldLim; C.SegFlags[I386.SEG_ES] = oldFl;
        return ok;
    }

    // ================= APM 1.2 (INT 15h 53xxh and the protected-mode interface) =================

    private static readonly bool ApmLog = Environment.GetEnvironmentVariable("X86_APMLOG") == "1";
    private int _apmEvent;   // a pending APM event for 53h/0Bh (not saved: a state is never taken inside the standby call)
    private void ApmCall(bool frame)
    {
        void Done(uint ah) { if (ah != 0) AH = ah; Carry(frame, ah != 0); }
        if (AH != 0x53) { AH = 0x86; Carry(frame, true); return; }
        uint fn = AL;
        if (ApmLog) Console.WriteLine($"apm: fn {fn:X2} BX {BX:X4} CX {CX:X4} frame {frame} vm {(C.Flags >> 17) & 1} conn {_apmConnected}");
        bool needConn = fn is not (0x00 or 0x01 or 0x02 or 0x03);
        if (needConn && !_apmConnected && fn != 0x04) { Done(0x03); return; }
        switch (fn)
        {
            case 0x00:
                if (BX != 0) { Done(0x09); return; }
                AH = 0x01; AL = 0x02; BH = (uint)'P'; BL = (uint)'M'; CX = 0x0003;   // 1.2, 16- and 32-bit PM interfaces
                Carry(frame, false); return;
            case 0x01: case 0x02: case 0x03:
                if (BX != 0) { Done(0x09); return; }
                if (_apmConnected) { Done(0x02); return; }
                _apmConnected = true; _apmInterface = (byte)fn;
                if (fn == 0x02) { AX = RomSeg; BX = ApmEntry; CX = RomSeg; C.R[6] = 0xffff; DI = 0xffff; }
                if (fn == 0x03) { AX = RomSeg; C.R[3] = ApmEntry; CX = RomSeg; DX = RomSeg; C.R[6] = 0xffffffff; DI = 0xffff; }
                Carry(frame, false); return;
            case 0x04:
                if (BX != 0) { Done(0x09); return; }
                if (!_apmConnected) { Done(0x03); return; }
                _apmConnected = false; _apmInterface = 0;
                Done(0); return;
            case 0x05:
                Done(0);
                if ((C.Flags & I386.IF) != 0 && (C.Flags & I386.VM) == 0) { C.Halt = true; C.EndBatch(); }   // idle: sleep till the next interrupt, then the RETF / IRET
                return;
            case 0x06: Done(0); return;
            case 0x07:
                if (CX == 0x0003 && (BX == 0x0001 || BX == 0x0000 || (BX & 0xff00) == 0x0100))
                {
                    Done(0);
                    Stopped = "APM: the machine powered off";
                    C.EndBatch();
                    return;
                }
                // standby (1) / suspend (2) of the whole system: we resume at once; the next Get PM Event reports the resume (APM 1.2: 000Bh standby
                // resume, 0003h normal resume), as a BIOS does when the user presses a key. (Windows 98's "Stand by" in the Shut Down dialog.)
                if ((BX == 0x0001 || BX == 0x0000 || (BX & 0xff00) == 0x0100) && (CX == 1 || CX == 2)) _apmEvent = CX == 1 ? 0x000b : 0x0003;
                Done(0); return;
            case 0x08: case 0x09: case 0x0d: case 0x0f: Done(0); return;
            case 0x0a:
                BH = 0x01; BL = 0xff; CH = 0x80; CL = 0xff; DX = 0xffff;   // on AC, no battery
                Done(0); return;
            case 0x0b: if (_apmEvent != 0) { BX = (uint)_apmEvent; _apmEvent = 0; CX = 0; Done(0); return; } Done(0x80); return;   // no events (but a resume after standby/suspend)
            case 0x0c: CX = 0; Done(0); return;
            case 0x0e: AH = 0x01; AL = 0x02; Carry(frame, false); return;
            case 0x10: BL = 0; CX = 0x0003; Done(0); return;   // capabilities: global standby + global suspend (Windows 98 then offers "Stand by")
        }
        Done(0x0c);
    }

    // ================= PnP BIOS =================

    private void PnpCall()
    {
        // a C-style far call: [SS:SP] return IP, CS (16-bit), then Function and its arguments as words
        uint sp = C.R[4] & C.SpMask;
        uint Arg(int i) { C.CpuLoad(16, I386.SEG_SS, (sp + 4 + (uint)i * 2) & C.SpMask, out uint v); return v; }
        uint fn = Arg(0);
        uint result = 0x82;                                     // FUNCTION_NOT_SUPPORTED
        var nodes = PnpNodes();
        switch (fn)
        {
            case 0x00:
            {
                int max = 0; foreach (var n in nodes) max = Math.Max(max, n.Length);
                bool ok = FarWrite(Arg(2), Arg(1), new[] { (byte)nodes.Count }) && FarWrite(Arg(4), Arg(3), new[] { (byte)max, (byte)(max >> 8) });
                result = ok ? 0u : 0x8fu;
                break;
            }
            case 0x01:
            {
                uint nodeOff = Arg(1), nodeSel = Arg(2);
                if (!ReadFarByte(nodeSel, nodeOff, out byte handle)) { result = 0x8f; break; }
                int idx = nodes.FindIndex(n => n[2] == handle);
                if (idx < 0) { result = 0x8a; break; }                       // INVALID_HANDLE
                byte next = idx + 1 < nodes.Count ? nodes[idx + 1][2] : (byte)0xff;
                result = FarWrite(Arg(4), Arg(3), nodes[idx]) && FarWrite(nodeSel, nodeOff, new[] { next }) ? 0u : 0x8fu;
                break;
            }
            case 0x02: result = 0; break;                                     // accepted (the nodes are fixed)
            case 0x40:
                result = FarWrite(Arg(2), Arg(1), new byte[] { 1, 0, 0, 0, 0, 0 }) ? 0u : 0x8fu;   // no ISA PnP cards
                break;
        }
        AX = result;
    }

    private bool ReadFarByte(uint sel, uint off, out byte b)
    {
        uint oldEs = C.SegSel[I386.SEG_ES], oldBase = C.SegBase[I386.SEG_ES], oldLim = C.SegLimit[I386.SEG_ES], oldFl = C.SegFlags[I386.SEG_ES];
        bool ok = C.LoadSeg(I386.SEG_ES, sel);
        uint v = 0;
        if (ok) ok = C.CpuLoad(8, I386.SEG_ES, off, out v);
        C.SegGen++; C.SegSel[I386.SEG_ES] = oldEs; C.SegBase[I386.SEG_ES] = oldBase; C.SegLimit[I386.SEG_ES] = oldLim; C.SegFlags[I386.SEG_ES] = oldFl;
        b = (byte)v;
        return ok;
    }

    private static uint EisaId(string id)
    {
        int v = ((id[0] - '@') << 10) | ((id[1] - '@') << 5) | (id[2] - '@');
        int p = Convert.ToInt32(id.Substring(3), 16);
        return (uint)((v >> 8) | (v & 0xff) << 8 | (p >> 8) << 16 | (p & 0xff) << 24);
    }

    /// <summary>The system device nodes (fixed: the board's legacy devices).</summary>
    private List<byte[]> PnpNodes()
    {
        var list = new List<byte[]>();
        byte handle = 0;
        void Node(string id, byte b, byte s, byte i, Action<List<byte>> res)
        {
            var d = new List<byte> { 0, 0, handle++ };
            uint e = EisaId(id);
            d.AddRange(BitConverter.GetBytes(e));
            d.Add(b); d.Add(s); d.Add(i);
            d.Add(0x03); d.Add(0x00);                           // attributes: cannot be disabled, not configurable
            res(d);
            d.Add(0x79); d.Add(0);                              // end of allocated resources
            d.Add(0x79); d.Add(0);                              // possible resources: none
            d.Add(0x79); d.Add(0);                              // compatible ids: none
            d[0] = (byte)d.Count; d[1] = (byte)(d.Count >> 8);
            list.Add(d.ToArray());
        }
        void Io(List<byte> d, int port, int len) { d.AddRange(new byte[] { 0x47, 1, (byte)port, (byte)(port >> 8), (byte)port, (byte)(port >> 8), 1, (byte)len }); }
        void Irq(List<byte> d, int irq) { d.Add(0x22); d.Add((byte)(1 << irq)); d.Add((byte)((1 << irq) >> 8)); }
        void Mem(List<byte> d, uint b, uint len, byte info) { d.AddRange(new byte[] { 0x86, 9, 0, info }); d.AddRange(BitConverter.GetBytes(b)); d.AddRange(BitConverter.GetBytes(len)); }
        Node("PNP0000", 8, 0, 1, d => { Io(d, 0x20, 2); Io(d, 0xa0, 2); Irq(d, 2); });
        Node("PNP0200", 8, 1, 0, d => { Io(d, 0x00, 16); Io(d, 0x81, 15); Io(d, 0xc0, 32); d.AddRange(new byte[] { 0x2a, 0x10, 0x00 }); });
        Node("PNP0100", 8, 2, 0, d => { Io(d, 0x40, 4); Irq(d, 0); });
        Node("PNP0B00", 8, 3, 0, d => { Io(d, 0x70, 2); Irq(d, 8); });
        Node("PNP0303", 9, 0, 0, d => { Io(d, 0x60, 1); Io(d, 0x64, 1); Irq(d, 1); });
        if (Kbc.HasMouse) Node("PNP0F13", 9, 2, 0, d => Irq(d, 12));   // the PS/2 mouse on the 8042
        // the 82077AA floppy controller: 3F0h-3F5h, 3F7h, IRQ 6, DMA 2 (8-bit)
        if (Fdc != null) Node("PNP0700", 1, 2, 0, d => { Io(d, 0x3f0, 6); Io(d, 0x3f7, 1); Irq(d, 6); d.AddRange(new byte[] { 0x2a, 0x04, 0x00 }); });
        Node("PNP0800", 4, 1, 0, d => Io(d, 0x61, 1));
        if (Cpu.Model.Fpu) Node("PNP0C04", 0x0b, 0x80, 0, d => { Io(d, 0xf0, 16); Irq(d, 13); });
        Node("PNP0A03", 6, 4, 0, d => Io(d, 0xcf8, 8));
        Node("PNP0C01", 5, 0, 0, d =>
        {
            Mem(d, 0, 0x9fc00, 0x03);
            Mem(d, 0xf0000, 0x10000, 0x00);
            Mem(d, 0x100000, (uint)MemKb * 1024 - 0x100000, 0x03);
        });
        // (LAST: a node added in the middle renumbers the nodes after it - Windows 98 then sees a "new" PCI bus (*PNP0A03) and its S3
        // child, and the next boot says "the adapter type is incorrect"; appended, the other nodes keep their numbers)
        // the SB16 (Windows 98's SB16AWE.INF knows the legacy SB16 as *PNPB003; there is no SB Pro driver in Windows 98): 220h, 388h, IRQ 5, DMA 1 + 5
        if (Card?.Id == "SB16_D072") Node("PNPB003", 4, 1, 0, d => { Io(d, 0x220, 16); Io(d, 0x388, 4); Irq(d, 5); d.AddRange(new byte[] { 0x2a, 0x02, 0x08 }); d.AddRange(new byte[] { 0x2a, 0x20, 0x16 }); });
        return list;
    }
}
