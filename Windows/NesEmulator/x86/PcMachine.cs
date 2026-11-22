// The x86 console's board: a 486-class PC/AT (CPU, 8259 PICs, 8254 PIT, 8042 keyboard controller, CMOS clock, VGA, the BIOS in C#,
// disks through INT 13h). Written for BrokenNes; the chips are ported from tiny386 where their files say so. The x86 console is
// ISOLATED (docs/projects/next-pool-plans.md, P3): none of these parts is a slot of another console, nor the other way round.
//
// Time is deterministic: one clock counted in CPU instructions (plus the instructions a halted CPU sleeps through), turned into PIT
// ticks at InstructionsPerSecond. The same disk image and the same input give the same run.

using System;
using System.Collections.Generic;
using System.IO;

namespace NesEmulator.X86;

public sealed partial class PcMachine : IX86Bus
{
    /// <summary>X86_NVSYSPAGES=1: system-memory pages the NV card's DMA wrote (page -> dword writes), a stray-DMA hunt.</summary>
    public Dictionary<uint, int>? NvSysPages;

    public const int DefaultMemory = 16 * 1024 * 1024;
    public readonly I386 Cpu;
    public readonly Pic8259 Pic = new();
    public readonly Pit8254 Pit;
    public readonly Kbc8042 Kbc;
    public readonly Cmos Cmos;
    public readonly Vga Vga;
    public readonly DiskImage?[] Floppy = new DiskImage?[2];
    public readonly DiskImage?[] Hdd = new DiskImage?[2];
    public readonly int MemKb;

    /// <summary>How fast the machine runs (instructions per emulated second). About a 486DX-33 for typical code.</summary>
    public long InstructionsPerSecond = 20_000_000;
    private long _idle;                       // instructions a halted CPU slept through
    private byte _port61;
    private bool _port61Refresh;

    /// <summary>Set when the machine stopped (a CPU abort / triple fault, or a BIOS boot failure): the reason.</summary>
    public string? Stopped;
    /// <summary>Text the BIOS wrote with INT 10h teletype (handy for tests: what DOS printed).</summary>
    public readonly System.Text.StringBuilder Teletype = new();
    /// <summary>Port E9h output (the Bochs debug port): a test program's log.</summary>
    public readonly System.Text.StringBuilder DebugPort = new();
    /// <summary>A whole line written to port E9h (guest-initiated status, e.g. the nano98 launcher's "N98 ..." lines): raised at the newline.
    /// Host side only (not in the savestate); the guest decides what and when, so a replay sees the same lines at the same instructions.</summary>
    public event Action<string>? StatusLine;
    /// <summary>nano98 COMMAND PORT (host -> guest, read-only, set before power-on): OUT EAh = rewind, IN EAh = the next byte (0 past the end).
    /// Only the guest's reads move it, so runs replay exactly; saved as block "N98C" when set (older states: none).</summary>
    public byte[]? Command;
    /// <summary>nano98 upload port EBh (guest -> host, host side only): the bytes of the file the last "N98 FILE" status line announced.</summary>
    public Action<byte>? UploadByte;
    private int _cmdPos;
    private int _statusFrom;
    /// <summary>Called for a port the board does not answer (for devices added later: sound cards).</summary>
    public Func<int, int, int>? ExtraIn;     // (port, size) -> value or -1
    public Func<int, int, int, bool>? ExtraOut; // (port, size, value) -> handled

    /// <summary>Speed harness only (Workshop --x86speed): time spent drawing the picture, in Stopwatch ticks. Never part of a state.</summary>
    public bool Profiling; public long RenderTicks;
    public long Clock => Cpu.Cycle + _idle;
    public long PitNow => _clkEpoch == 0 && _pitEpoch == 0 ? (long)((Int128)Clock * Pit8254.Freq / InstructionsPerSecond)
        : _pitEpoch + (long)((Int128)(Clock - _clkEpoch) * Pit8254.Freq / InstructionsPerSecond);
    /// <summary>Where the current instruction rate began (Clock, PIT time): 0/0 = from power-on. Set when the rate changes under a
    /// running machine (SetIps: a state loaded with another X86_P2CPI), so guest time stays continuous; saved as the "IPSE" block.</summary>
    private long _clkEpoch, _pitEpoch;
    /// <summary>Change the instruction rate of a running machine without a jump in guest time (PIT, FDC clock, TSC).</summary>
    public void SetIps(long ips)
    {
        if (ips == InstructionsPerSecond || ips <= 0) return;
        long clk = Clock, pit = PitNow;
        Cpu.RebaseTsc(clk, InstructionsPerSecond, ips);
        _clkEpoch = clk; _pitEpoch = pit; InstructionsPerSecond = ips;
    }
    private long MicrosNow => _clkEpoch == 0 && _pitEpoch == 0 ? (long)((Int128)Clock * 1_000_000 / InstructionsPerSecond) : (long)((Int128)PitNow * 1_000_000 / Pit8254.Freq);

    /// <summary>What the board is built with (docs/projects/x86-modular-pc.md). Fixed for the machine's life: a change is a new machine.</summary>
    public readonly PcConfig Config;
    /// <summary>The PCI network card (an RTL8029 / NE2000; PCI boards only). Its cable (Nic.Link) is host-side and starts unplugged.</summary>
    public readonly Rtl8029? Nic;
    /// <summary>The 3dfx Voodoo Graphics add-in card (PCI board with PcConfig.Voodoo only; off by default).</summary>
    public readonly Voodoo1? Voodoo;
    private static readonly bool AcceptOtherVoodooScale = Environment.GetEnvironmentVariable("X86_VOODOO_ACCEPT_STOCK") == "1";
    /// <summary>The 82077AA floppy controller (the PCI 430TX board only; the other boards serve floppies through the BIOS alone).</summary>
    public readonly Fdc82077? Fdc;
    private bool BmPort(int port, out int off) { off = 0; if (Ata == null || Pci == null || !Pci.Functions.TryGetValue(PciBus.IdeDevFn, out var f)) return false; int b = f.Bar4Base; if (b < 0 || port < b || port >= b + 16) return false; off = port - b; return true; }
    private bool NicPort(int port, out int off) { off = 0; if (Nic == null) return false; if (Pci!.NicFn == null) return false; int b = Pci.NicFn.IoBase; if (b < 0 || port < b || port >= b + Rtl8029.IoSize) return false; off = port - b; return true; }
    /// <summary>The IDE controller (null on a BIOS-only machine: the default).</summary>
    public readonly AtaController? Ata;
    /// <summary>The ATAPI CD-ROM's disc (secondary master; IDE machines only). Read-only media: not part of a save state.</summary>
    public CdImage? Cd;

    /// <param name="video">The video card: the plain VGA (the default, unchanged) or an S3 Trio64 (VBE 2.0, linear framebuffer, drawing engine).</param>
    /// <param name="cpu">A CPU model (I386.Model.cs); null or the legacy model = the original console's 486-class tiny386 core.</param>
    public PcMachine(int memBytes = DefaultMemory, CpuModel? cpu = null, PcVideo video = PcVideo.Vga) : this(PcConfig.Default, memBytes, cpu, video) { }

    public PcMachine(PcConfig config, CpuModel? cpu = null, PcVideo video = PcVideo.Vga) : this(config, config.MemoryMB * 1024 * 1024, cpu, video) { }

    private PcMachine(PcConfig config, int memBytes, CpuModel? cpu, PcVideo video)
    {
        Video = video;
        config.Validate();
        if (video is PcVideo.GeForce256 or PcVideo.GeForce4Mx or PcVideo.GeForceFx5200 && config.Bus != PcBus.Pci430TX) throw new ArgumentException("the GeForce needs the PCI board");
        Config = config with { MemoryMB = Math.Max(1, memBytes >> 20) };
        MemKb = memBytes / 1024;
        Cpu = cpu == null || cpu.Legacy ? new I386(config.CpuGen, memBytes, this) : new I386(cpu, memBytes, this);
        if (cpu != null && !cpu.Legacy)
        {
            InstructionsPerSecond = cpu.Ips;
            Cpu.InstructionClock = () => Clock;     // RDTSC counts HLT time too
            Cpu.Ferr = () => Pic.Pulse(13);         // FERR# -> IRQ 13 (CR0.NE clear: DOS / Windows 9x style FPU errors)
        }
        Pit = new Pit8254(() => PitNow, () => Pic.Pulse(0));
        Kbc = new Kbc8042(Pic, config.Bus == PcBus.Pci430TX && Environment.GetEnvironmentVariable("X86_NOMOUSE") == null);   // (X86_NOMOUSE: a diagnostic)   // the Windows 98 board has the PS/2 mouse
        Cmos = new Cmos(() => PitNow, MemKb);
        Vga = new Vga(() => PitNow, video == PcVideo.S3Trio64, video switch { PcVideo.GeForce256 => NvChip.NV10, PcVideo.GeForce4Mx => NvChip.NV17, PcVideo.GeForceFx5200 => NvChip.NV34, _ => null }, video == PcVideo.VBoxVga, config.VramMB * 1024 * 1024);
        S3 = Vga.S3;
        Nv = Vga.Nv;
        _vramNs = Cpu.Model.Legacy ? 0 : S3 != null || Nv != null ? 150 : Cpu.Model.Gen <= 3 ? 650 : 450;
        Speaker = new PcSpeaker(this);
        Opl = new Opl2(() => PitNow);
        Dma = new Dma8237(this);
        if (config.Storage == PcStorage.Ide) Ata = new AtaController(Pic);
        if (config.Bus != PcBus.Isa)
        {
            if (!config.NoNic) Nic = new Rtl8029();
            if (Nic != null) Nic.SetIrq = on => { int irq = Pci!.Isa.Cfg[0x61]; if (irq < 16) Pic.SetIrq(irq, on ? 1 : 0); };
            if (config.Voodoo) Voodoo = new Voodoo1(Math.Max(1, config.VoodooScale));
            Pci = new PciBus(this);
            if (Nv != null)
            {
                Nv.SetIrq = on => { int irq = Pci!.Isa.Cfg[0x60]; if (irq < 16) Pic.SetIrq(irq, on ? 1 : 0); };
                if (Environment.GetEnvironmentVariable("X86_NVLOG") is string nvl and not "")
                {
                    var lw = new System.IO.StreamWriter(nvl) { AutoFlush = true }; long nlog = 0;
                    // X86_NVLOGMAX: line cap (default 300000); X86_NVLOGNORAMIN: leave PRAMIN (700000-7FFFFF) accesses out
                    long nmax = long.TryParse(Environment.GetEnvironmentVariable("X86_NVLOGMAX"), out var nm) ? nm : 300_000;
                    bool noRamin = Environment.GetEnvironmentVariable("X86_NVLOGNORAMIN") != null;
                    bool noReads = Environment.GetEnvironmentVariable("X86_NVLOGNOREADS") != null;
                    bool noRegs = Environment.GetEnvironmentVariable("X86_NVLOGNOREGS") != null;   // methods + ops only   // leave register reads out (the driver's polling)
                    Nv.OpLog = t => { if (nlog++ < nmax) lw.WriteLine($"{Vga.FrameCount} OP {t}"); };
                    Nv.MethodLog = (ch, sc, cl, mt, d) => { if (nlog++ < nmax) lw.WriteLine($"{Vga.FrameCount} M ch{ch} s{sc} c{cl:X3} {mt:X4} {d:X8}"); };
                    Nv.Log = (o, v, sz, wr) => { if (noRegs) return; if (noRamin && o is >= 0x700000 and < 0x800000) return; if (noReads && !wr) return; if (nlog++ < nmax) lw.WriteLine($"{Vga.FrameCount} {(wr ? 'W' : 'R')}{sz} {o:X6} {v:X8} {Cpu.SegSel[1]:X4}:{Cpu.Ip:X8}"); };
                }
                // a DMA object can point at the card's own BARs (the 45.23 FX driver flips by an M2MF copy into PCRTC_START at BAR0 + 0x600800)
                Nv.SysRead32 = a =>
                {
                    if (Nv.Pci is { } pf)
                    {
                        if (pf.MemBase != 0 && a - pf.MemBase < NvGeForce.Bar0Size) return Nv.MmioRead(a - pf.MemBase, 4);
                        if (pf.MemBase1 != 0 && a - pf.MemBase1 < NvGeForce.Bar1Size) return Nv.Aperture.MmioRead(a - pf.MemBase1, 4);
                    }
                    return a <= Cpu.MemSize - 4 ? BitConverter.ToUInt32(Cpu.Mem, (int)a) : 0xffffffff;
                };
                if (Environment.GetEnvironmentVariable("X86_NVSYSPAGES") == "1") NvSysPages = new();
                Nv.SysWrite32 = (a, v) =>
                {
                    if (Nv.Pci is { } pf)
                    {
                        if (pf.MemBase != 0 && a - pf.MemBase < NvGeForce.Bar0Size) { Nv.MmioWrite(a - pf.MemBase, v, 4); return; }
                        if (pf.MemBase1 != 0 && a - pf.MemBase1 < NvGeForce.Bar1Size) { Nv.Aperture.MmioWrite(a - pf.MemBase1, v, 4); return; }
                    }
                    if (NvSysPages != null) NvSysPages[a >> 12] = NvSysPages.GetValueOrDefault(a >> 12) + 1;
                    if (a <= Cpu.MemSize - 4) { BitConverter.TryWriteBytes(Cpu.Mem.AsSpan((int)a), v); Cpu.MemTouched(); }
                };
                Nv.RomByte = i => i < 0x8000 && VRom + i < Cpu.MemSize ? Cpu.Mem[VRom + i] : (byte)0xff;
            }
        }
        if (config.Bus == PcBus.Pci430TX && !config.NoFloppy)
            Fdc = new Fdc82077(n => Floppy[n], Dma[2], on => Pic.SetIrq(6, on ? 1 : 0), () => MicrosNow);
        Pic.Raise = () => Cpu.RaiseIrq();
        RegisterTraps();
    }

    // ================= IX86Bus =================

    public bool MapsIoMem => true;

    // the CPU's accesses to the video card's memory (and the S3's registers) wait for the bus: ISA VGA ~650 ns on a 386 board, ~450 ns on a 486, the PCI S3 ~150 ns per
    // access, charged as machine time on a CPU model (the classic machine: no wait, as before)
    public byte MemRead8(uint a) { VramWait(); return RawRead8(a); }
    public void MemWrite8(uint a, byte v) { VramWait(); RawWrite8(a, v); }
    public ushort MemRead16(uint a) { VramWait(); return RawRead16(a); }
    public void MemWrite16(uint a, ushort v) { VramWait(); RawWrite16(a, v); }
    public uint MemRead32(uint a) { VramWait(); return RawRead32(a); }
    public void MemWrite32(uint a, uint v) { VramWait(); RawWrite32(a, v); }
    private readonly int _vramNs;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    // An ISA bus cycle to the timer (8254 behind the PIIX4 on the Windows 98 board): about 1 us, as on a real 430TX. Without it a PIT read cost one
    // instruction, so Windows 98's VMM delay loop (latch + read counter 0 until ECX ticks pass, C00528C9) spun ~2 million times a second of
    // emulated time and the boot ran at a third of real time. Only the PCI board (the other machines keep their timing byte for byte).
    private void IsaWait() { if (Pci != null) _idle += Math.Max(1, InstructionsPerSecond / 1_000_000); }
    private void VramWait() { if (_vramNs != 0) _idle += Math.Max(1, _vramNs * InstructionsPerSecond / 1_000_000_000); }
    private IPciMmio? Mmio(uint a, out uint off) { if (a >= 0xe0000000 && Pci != null && Pci.MmioFns.Length != 0) return Pci.MmioAt(a, out off); off = 0; return null; }
    private IPciMmio? Mmio(uint a) => Mmio(a, out _);
    private byte RawRead8(uint a) => Mmio(a, out uint mo) is { } md ? (byte)md.MmioRead(mo, 1) : a < 0xc0000 ? Vga.MemRead(a) : S3 != null && S3.InLfb(a) ? S3.LfbRead8(a) : (byte)0xff;
    private void RawWrite8(uint a, byte v) { if (Mmio(a, out uint mo) is { } md) md.MmioWrite(mo, v, 1); else if (a < 0xc0000) Vga.MemWrite(a, v); else if (S3 != null && S3.InLfb(a)) S3.LfbWrite8(a, v); }
    private ushort RawRead16(uint a) => Mmio(a, out uint mo) is { } md && Mmio(a + 1) == md ? (ushort)md.MmioRead(mo, 2) : (ushort)(RawRead8(a) | (RawRead8(a + 1) << 8));
    private static readonly bool DispiLog = Environment.GetEnvironmentVariable("X86_DISPILOG") == "1";   // diagnosis: DISPI port writes
    private int _dispiLogN;
    private void RawWrite16(uint a, ushort v)
    {
        if (Mmio(a, out uint mo) is { } md && Mmio(a + 1) == md) { md.MmioWrite(mo, v, 2); return; }
        if (S3 != null && (S3.InLfb(a) && S3.InLfb(a + 1) ? WriteLfb16(a, v) : a < 0xc0000 && S3.MmioWrite(a, v, 16))) return;
        RawWrite8(a, (byte)v); RawWrite8(a + 1, (byte)(v >> 8));
    }
    private uint RawRead32(uint a) => Mmio(a, out uint mo) is { } md && Mmio(a + 3) == md ? md.MmioRead(mo, 4) : S3 != null && S3.InLfb(a) ? S3.LfbRead32(a) : (uint)(RawRead16(a) | (RawRead16(a + 2) << 16));
    private void RawWrite32(uint a, uint v)
    {
        if (Mmio(a, out uint mo) is { } md && Mmio(a + 3) == md) { md.MmioWrite(mo, v, 4); return; }
        if (S3 != null)
        {
            if (S3.InLfb(a)) { S3.LfbWrite32(a, v); return; }
            if (a < 0xc0000 && S3.MmioWrite(a, v, 32)) return;
        }
        RawWrite16(a, (ushort)v); RawWrite16(a + 2, (ushort)(v >> 16));
    }
    private bool WriteLfb16(uint a, ushort v) { S3!.LfbWrite8(a, (byte)v); S3.LfbWrite8(a + 1, (byte)(v >> 8)); return true; }

    /// <summary>How many times each vector was taken as a hardware interrupt (diagnostics).</summary>
    public readonly long[] IrqCount = new long[256];
    public int AckIrq() { int v = Pic.ReadIrq(); IrqCount[v & 0xff]++; return v; }

    /// <summary>Debug tools: called with (port, isWrite) on every I/O access (null unless a tool sets it).</summary>
    public Action<int, bool>? PortLog;

    /// <summary>Debug tools: (port, isWrite, value) for byte accesses (null unless a tool sets it).</summary>
    public Action<int, bool, int>? PortTrace;

    public byte In8(int port)
    {
        if (PortTrace == null) return In8Core(port);
        byte r = In8Core(port); PortTrace(port, false, r); return r;
    }

    private byte In8Core(int port)
    {
        PortLog?.Invoke(port, false);
        if (Pci != null && Pci.In(port, 1, out uint pv)) return (byte)pv;
        if (BmPort(port, out int bo)) return (bo < 8 ? Ata!.Primary : Ata!.Secondary).BmRead(bo & 7);
        if (NicPort(port, out int no)) return (byte)Nic!.In(no, false);
        if (Ata != null && Ata.In(port, false, out int av)) return (byte)av;
        if (Fdc != null && Fdc82077.IsPort(port)) return Fdc.In(port);
        if (Card != null && Card.In(port, false, out int cv)) return (byte)cv;
        if (S3 != null && S3Trio64.IsEnginePort(port)) return (byte)S3.EngineIn(port & 0xfffe, (port & 1) != 0);
        if (S3 is { VBox: true } && S3Trio64.IsDispiPort(port)) return port == 0x1ce ? (byte)S3.DispiIndex : (byte)(port == 0x1cf ? S3.DispiRead() : S3.DispiRead() >> 8);
        switch (port)
        {
            case 0x20: case 0x21: case 0xa0: case 0xa1: return Pic.In(port);
            case 0x40: case 0x41: case 0x42: case 0x43: IsaWait(); return Pit.In(port);
            case 0x60: { byte v = Kbc.In(0x60); return v; }
            case 0x64: return Kbc.In(0x64);
            case 0x61:
                // bit 4: the refresh toggle (flips every 15 us), bit 5: PIT channel 2's output
                _port61Refresh = ((PitNow / 18) & 1) != 0;
                return (byte)((_port61 & 0x0f) | (_port61Refresh ? 0x10 : 0) | (Pit.GetOut(2) != 0 ? 0x20 : 0));
            case 0x70: case 0x71: return Cmos.In(port);
            case 0x92: return (byte)(Kbc.A20 ? 2 : 0);
            case 0xea when Command != null: return _cmdPos < Command.Length ? Command[_cmdPos++] : (byte)0;   // nano98 command port
            case 0x201: return 0xff;                          // no joystick
            case 0x388: AdlibWait(); return AdlibPresent ? Opl.ReadStatus() : (byte)0xff;   // AdLib status (FFh: no AdLib)
            case 0x389: AdlibWait(); return 0xff;
            case >= 0x3b0 and <= 0x3df: return Vga.In(port);
        }
        if (Dma.In(port, out byte dv)) return dv;
        if (ExtraIn != null) { int v = ExtraIn(port, 8); if (v >= 0) return (byte)v; }
        return 0xff;
    }

    public void Out8(int port, byte v)
    {
        PortLog?.Invoke(port, true);
        PortTrace?.Invoke(port, true, v);
        if (Pci != null && (Pci.Out(port, 1, v) || PmTrap(port))) return;
        if (BmPort(port, out int bo)) { (bo < 8 ? Ata!.Primary : Ata!.Secondary).BmWrite(bo & 7, v); return; }
        if (NicPort(port, out int no)) { Nic!.Out(no, v, false); return; }
        if (Ata != null && Ata.Out(port, false, v)) return;
        if (Fdc != null && Fdc82077.IsPort(port)) { Fdc.Out(port, v); return; }
        if (Card != null && Card.Out(port, false, v)) return;
        if (S3 != null && S3Trio64.IsEnginePort(port)) { S3.EngineOut8(port, v); return; }
        if (S3 is { VBox: true } && S3Trio64.IsDispiPort(port)) { if (DispiLog && _dispiLogN++ < 400) Console.WriteLine($"dispilog out {port:X3} {v:X4} idx {S3.DispiIndex:X} @{Cpu.SegSel[1]:X4}:{Cpu.Ip:X8}"); if (port == 0x1ce) S3.DispiIndex = v; else S3.DispiWrite(v); return; }
        switch (port)
        {
            case 0x20: case 0x21: case 0xa0: case 0xa1: Pic.Out(port, v); return;
            case 0x40: case 0x41: IsaWait(); Pit.Out(port, v); return;
            case 0x42: case 0x43: IsaWait(); Speaker.CatchUp(); Pit.Out(port, v); return;   // channel 2 is the speaker's tone
            case 0x60: case 0x64: Kbc.Out(port, v); if (Kbc.ResetRequested) CpuResetRequest(); return;
            case 0x61: Speaker.CatchUp(); _port61 = v; Pit.SetGate(2, v & 1); return;
            case 0x70: case 0x71: Cmos.Out(port, v); return;
            case 0x92: Kbc.A20 = (v & 2) != 0; if ((v & 1) != 0) CpuResetRequest(); return;
            case 0x80: return;                                // POST code
            case 0xea: _cmdPos = 0; return;                   // nano98 command port: rewind
            case 0xeb: UploadByte?.Invoke(v); return;         // nano98 upload port: a saved file's bytes (after its "N98 FILE" status line)
            case 0xe9:
                DebugPort.Append((char)v);
                if (v == 10) { var l = DebugPort.ToString(_statusFrom, DebugPort.Length - _statusFrom).TrimEnd((char)13, (char)10); _statusFrom = DebugPort.Length; StatusLine?.Invoke(l); }
                return;
            case 0x388: AdlibWait(); if (AdlibPresent) Opl.WriteAddress(v); return;   // AdLib (YM3812) address
            case 0x389: AdlibWait(); if (AdlibPresent) Opl.WriteData(v); return;      // AdLib data
            case BiosTrapPort: BiosTrap(); Cpu.MemTouched(); return;   // (our BIOS writes memory directly: compiled blocks check their bytes)
            case >= 0x3b0 and <= 0x3df: Vga.Out(port, v); return;
        }
        if (Dma.Out(port, v)) return;
        ExtraOut?.Invoke(port, 8, v);
    }

    public ushort In16(int port)
    {
        PortLog?.Invoke(port, false);
        if (Pci != null && Pci.In(port, 2, out uint pv)) return (ushort)pv;
        if (NicPort(port, out int no)) return (ushort)(no is >= 0x10 and < 0x18 ? Nic!.In(no, true) : Nic!.In(no, false) | Nic.In(no + 1, false) << 8);
        if (Ata != null && Ata.In(port, true, out int av)) return (ushort)av;
        if (Card != null && Card.In(port, true, out int cv)) return (ushort)cv;
        if (S3 != null && S3Trio64.IsEnginePort(port)) return (ushort)(S3.EngineIn(port, false) | S3.EngineIn(port, true) << 8);
        if (S3 is { VBox: true } && S3Trio64.IsDispiPort(port)) return port == 0x1ce ? (ushort)S3.DispiIndex : S3.DispiRead();
        if (ExtraIn != null) { int v = ExtraIn(port, 16); if (v >= 0) return (ushort)v; }
        return (ushort)(In8(port) | (In8(port + 1) << 8));
    }
    public void Out16(int port, ushort v)
    {
        PortLog?.Invoke(port, true);
        if (Pci != null && Pci.Out(port, 2, v)) return;
        if (NicPort(port, out int no)) { if (no is >= 0x10 and < 0x18) Nic!.Out(no, v, true); else { Nic!.Out(no, (byte)v, false); Nic.Out(no + 1, v >> 8, false); } return; }
        if (Ata != null && Ata.Out(port, true, v)) return;
        if (Card != null && Card.Out(port, true, v)) return;
        if (S3 != null && S3Trio64.IsEnginePort(port)) { S3.EngineOut16(port, v); return; }
        if (S3 is { VBox: true } && S3Trio64.IsDispiPort(port)) { if (DispiLog && _dispiLogN++ < 400) Console.WriteLine($"dispilog out {port:X3} {v:X4} idx {S3.DispiIndex:X} @{Cpu.SegSel[1]:X4}:{Cpu.Ip:X8}"); if (port == 0x1ce) S3.DispiIndex = v; else S3.DispiWrite(v); return; }
        if (ExtraOut != null && ExtraOut(port, 16, v)) return;
        Out8(port, (byte)v); Out8(port + 1, (byte)(v >> 8));
    }
    // a 32-bit access to an IDE data port is two 16-bit cycles on that same port
    private bool IsAtaData(int port) => Ata != null && (port == 0x1f0 || port == 0x170);
    public uint In32(int port) => Pci != null && Pci.In(port, 4, out uint pv) ? pv : S3 is { VBox: true } && port == 0x1cf ? (uint)S3.VramBytes : IsAtaData(port) ? (uint)(In16(port) | (In16(port) << 16)) : (uint)(In16(port) | (In16(port + 2) << 16));
    public void Out32(int port, uint v)
    {
        if (Pci != null && Pci.Out(port, 4, v)) return;
        if (S3 != null && (port & 0xfffc) == 0xe2e8) { S3.PixelData(v, 32); return; }   // a 32-bit image transfer
        Out16(port, (ushort)v); Out16(IsAtaData(port) ? port : port + 2, (ushort)(v >> 16));
    }

    /// <summary>Port 61h bit 1 (speaker data) and bit 0 (PIT channel 2 gate).</summary>
    public int Port61 => _port61;
    /// <summary>The PC speaker's sound (off until a host enables it).</summary>
    public readonly PcSpeaker Speaker;
    /// <summary>The AdLib's YM3812 (OPL2) on ports 388h / 389h: its timers always run, its sound only while a host enables it.</summary>
    public readonly Opl2 Opl;
    /// <summary>The two 8237 DMA controllers (the sound cards' DMA).</summary>
    public readonly Dma8237 Dma;
    /// <summary>False when the sound choice has no AdLib (SPEAKER, COVOX, TANDY, GUS): 388h reads FFh and writes go nowhere.</summary>
    public bool AdlibPresent = true;
    /// <summary>The sound card in the slot (null: none beyond the PC speaker and the AdLib). Not part of the state's choice: the session picks it.</summary>
    public IPcSoundCard? Card { get; private set; }

    /// <summary>Puts <paramref name="card"/> in the slot (the old one's DMA handlers dropped). The new card starts cold.</summary>
    public void SetCard(IPcSoundCard? card)
    {
        Dma.ClearCallbacks();
        Card = card;
        Dma.SecondOpen = card is not Dosbox072Tandy;
        card?.Reset();
    }

    private void CpuResetRequest()
    {
        Kbc.ResetRequested = false;
        // a warm reset: the BIOS checks CMOS 0Fh (shutdown code) / 40:67 like an AT BIOS; DOS extenders leaving to real mode use it.
        byte code = Cmos.Ram[0x0f];
        if (code is 0x05 or 0x0a)
        {
            Cmos.Ram[0x0f] = 0;
            Cpu.Reset();
            uint vec = Phys16(0x467) | ((uint)Phys16(0x469) << 4);
            Cpu.SetCsIp(Phys16(0x469), Phys16(0x467));
            if (code == 0x05) Pic.Out(0x20, 0x20);
            return;
        }
        PowerOn(keepDisks: true);
    }

    // ================= physical memory helpers (for the BIOS and the host) =================

    public byte Phys8(uint a) => a is >= 0xa0000 and < 0xc0000 ? Vga.MemRead(a) : a < Cpu.MemSize ? Cpu.Mem[a] : S3 != null && S3.InLfb(a) ? S3.LfbRead8(a) : (byte)0xff;
    public ushort Phys16(uint a) => (ushort)(Phys8(a) | (Phys8(a + 1) << 8));
    public uint Phys32(uint a) => (uint)(Phys16(a) | (Phys16(a + 2) << 16));
    /// <summary>Bus-master DMA into guest physical memory: RAM in one copy (the JIT told once), anything else byte by byte.</summary>
    private void DmaToMem(uint a, byte[] src, int off, int n)
    {
        if (a >= 0x100000 && (long)a + n <= Cpu.MemSize || (long)a + n <= 0xa0000) { Array.Copy(src, off, Cpu.Mem, a, n); Cpu.RamWritten(a, n); return; }
        for (int i = 0; i < n; i++) PhysW8(a + (uint)i, src[off + i]);
    }
    private void DmaFromMem(uint a, byte[] dst, int off, int n)
    {
        if (a >= 0x100000 && (long)a + n <= Cpu.MemSize || (long)a + n <= 0xa0000) { Array.Copy(Cpu.Mem, a, dst, off, n); return; }
        for (int i = 0; i < n; i++) dst[off + i] = Phys8(a + (uint)i);
    }
    public void PhysW8(uint a, byte v) { if (a is >= 0xa0000 and < 0xc0000) Vga.MemWrite(a, v); else if (a < Cpu.MemSize) { Cpu.Mem[a] = v; Cpu.MemTouched(); } else if (S3 != null && S3.InLfb(a)) S3.LfbWrite8(a, v); }
    public void PhysW16(uint a, ushort v) { PhysW8(a, (byte)v); PhysW8(a + 1, (byte)(v >> 8)); }
    public void PhysW32(uint a, uint v) { PhysW16(a, (ushort)v); PhysW16(a + 2, (ushort)(v >> 16)); }
    // Linear (guest-virtual) access for BIOS services: under Windows the V86 VM's 0-1 MB is paged, not identity-mapped,
    // so a buffer at ES:BX must go through the page tables (an INT 13h read written to the physical address corrupted DLL loads).
    public uint LinToPhys(uint la)
    {
        if ((Cpu.Cr0 & 0x80000000) == 0) return la;
        uint pde = Phys32((Cpu.Cr3 & ~0xfffu) + (la >> 22) * 4); if ((pde & 1) == 0) return 0xffffffff;
        if ((pde & 0x80) != 0 && (Cpu.Cr4 & 0x10) != 0) return (pde & 0xffc00000) | (la & 0x3fffff);
        uint pte = Phys32((pde & ~0xfffu) + ((la >> 12) & 0x3ff) * 4); return (pte & 1) == 0 ? 0xffffffff : (pte & ~0xfffu) | (la & 0xfff);
    }
    public byte Lin8(uint a) { uint p = LinToPhys(a); return p == 0xffffffff ? (byte)0xff : Phys8(p); }
    public ushort Lin16(uint a) => (ushort)(Lin8(a) | (Lin8(a + 1) << 8));
    public uint Lin32(uint a) => (uint)(Lin16(a) | (Lin16(a + 2) << 16));
    public void LinW8(uint a, byte v) { uint p = LinToPhys(a); if (p != 0xffffffff) PhysW8(p, v); }
    public void LinW16(uint a, ushort v) { LinW8(a, (byte)v); LinW8(a + 1, (byte)(v >> 8)); }
    public void LinW32(uint a, uint v) { LinW16(a, (ushort)v); LinW16(a + 2, (ushort)(v >> 16)); }

    // ================= running =================

    private long _lastFrame = -1;
    /// <summary>Instructions per batch between device updates (an interrupt waits at most this long).</summary>
    public int Batch = int.TryParse(Environment.GetEnvironmentVariable("X86_BATCH"), out int xb) ? xb : 1024;   // (X86_BATCH; 256 before 2026-11-21: suite geomean -5.4% at 1024 - device polling + Step entry per batch; interrupts wait at most ~7 us of guest time)

    /// <summary>Runs until the VGA starts its next frame (vertical retrace), renders the picture. False when the machine has stopped.</summary>
    public bool RunFrame()
    {
        if (Stopped != null) return false;
        Cpu.MemTouched();   // (a tool may have written memory between frames)
        long frame = Vga.FrameIndex(PitNow);
        if (_lastFrame < 0) _lastFrame = frame;
        long target = _lastFrame + 1;
        // the clock value where that frame begins
        long guard = 0;
        while (Vga.FrameIndex(PitNow) < target)
        {
            if (!RunBatch()) return false;
            if (Cpu.Debug is { Stop: true }) return true;   // the debugger stopped mid-frame (I386.Debug.cs): the frame carries on at the next call
            Vga.Picture?.Poll();
            if (++guard > 100_000_000) { Stopped = "frame never ended"; return false; }
        }
        _lastFrame = target;
        Voodoo?.TickFrame();
        Nv?.TickFrame();
        if (Profiling || TimeProbe) { long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); Vga.Render(); RenderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
        else if (AllocProbe) { long a0 = GC.GetAllocatedBytesForCurrentThread(); Vga.Render(); AllocRender += GC.GetAllocatedBytesForCurrentThread() - a0; }
        else Vga.Render();
        if (Voodoo is { Active: true }) Voodoo.RenderInto(Vga);   // the pass-through switch: the 3D card drives the monitor
        return Stopped == null;
    }

    /// <summary>Runs for <paramref name="instr"/> machine-clock instructions (headless tests).</summary>
    public bool RunClock(long instr)
    {
        long end = Clock + instr;
        while (Clock < end && Stopped == null) if (!RunBatch()) return false;
        return Stopped == null;
    }

    /// <summary>Diagnostics: batches run (instructions per batch = cycles / batches).</summary>
    public long Batches, HaltBatches;
    /// <summary>X86_ALLOCPROBE=1: bytes allocated per part of a batch (devices / CPU / picture), for the GC hunt (diagnostics).</summary>
    public static readonly bool AllocProbe = Environment.GetEnvironmentVariable("X86_ALLOCPROBE") == "1";
    public long AllocDevices, AllocCpu, AllocRender;
    /// <summary>X86_TIMEPROBE=1: wall ticks in the CPU batches vs everything between them (devices, frame work), diagnostics.</summary>
    public static readonly bool TimeProbe = Environment.GetEnvironmentVariable("X86_TIMEPROBE") == "1";
    public long TicksCpu, TicksDevices, TicksCard; private long _tp0;
    private bool RunBatch()
    {
        Batches++;
        if (TimeProbe && _tp0 == 0) _tp0 = System.Diagnostics.Stopwatch.GetTimestamp();
        long a0 = AllocProbe ? GC.GetAllocatedBytesForCurrentThread() : 0;
        if (TimeProbe) { long c0 = System.Diagnostics.Stopwatch.GetTimestamp(); Card?.Advance(); TicksCard += System.Diagnostics.Stopwatch.GetTimestamp() - c0; }
        else Card?.Advance();
        Nic?.Poll();
        Fdc?.Poll();
        Pit.UpdateIrq();
        Kbc.Pump();
        if (AllocProbe) { long a1 = GC.GetAllocatedBytesForCurrentThread(); AllocDevices += a1 - a0; try { return RunBatchCpu(); } finally { AllocCpu += GC.GetAllocatedBytesForCurrentThread() - a1; } }
        if (TimeProbe) { long t1 = System.Diagnostics.Stopwatch.GetTimestamp(); TicksDevices += t1 - _tp0; bool ok = RunBatchCpu(); long t2 = System.Diagnostics.Stopwatch.GetTimestamp(); TicksCpu += t2 - t1; _tp0 = t2; return ok; }
        return RunBatchCpu();
    }

    private bool RunBatchCpu()
    {
        try
        {
            if (Cpu.Halt)
            {
                HaltBatches++;
                if (Cpu.Intr && (Cpu.Flags & I386.IF) != 0) { Cpu.Step(1); return true; }
                // sleep until the next timer interrupt, but no further than the end of this frame's batch
                long nextPit = Pit.NextIrq0();
                long nowPit = PitNow;
                long sleepPit = nextPit == long.MaxValue ? 1000 : Math.Max(1, nextPit - nowPit);
                long sleepInstr = Math.Max(1, (long)((Int128)sleepPit * InstructionsPerSecond / Pit8254.Freq));
                _idle += Math.Min(sleepInstr, Batch * 16);
                if ((Cpu.Flags & I386.IF) == 0 && nextPit == long.MaxValue && !Kbc.HasPendingKeys) { }
                return true;
            }
            Cpu.Step(Batch);
        }
        catch (I386AbortException e) { Stopped = "CPU stopped: " + e.Message; return false; }
        return true;
    }

    /// <summary>An access to the AdLib's ports takes ISA bus time: ~0.55 us (the AdLib programming guide: 6 status reads = 3.3 us, 35 = 23 us). Without it a fast CPU ran the
    /// classic detection (timer 1 at FFh = 80 us, then N status reads) before the timer fired: Super VRUN DOS's 2000 reads took ~77 us at 130 M instr/s and found "no AdLib".
    /// Charged as idle machine time (like a halted CPU's sleep), so it holds at every instruction rate.</summary>
    private void AdlibWait() => _idle += Math.Max(1, InstructionsPerSecond * 11 / 20_000_000);

    /// <summary>Headless tests: lets <paramref name="instr"/> instructions of machine time pass without running the CPU (as a halted CPU sleeps), the sound card caught up.</summary>
    public void IdleFor(long instr) { _idle += instr; Card?.Advance(); Fdc?.Poll(); }

    // ================= power =================

    /// <summary>Cold boot: clears memory, resets every chip, runs the BIOS's POST and boots (floppy A: first, then the hard disk).</summary>
    public void PowerOn(bool keepDisks = true)
    {
        Stopped = null;
        Array.Clear(Cpu.Mem);
        Cpu.Reset();   // (also tells the compiled blocks memory changed)
        Pic.Reset();
        Pit.Reset();
        Opl.Reset();
        Dma.Reset();
        Card?.Reset();
        Vga.Picture?.Reset();
        S3?.Reset();
        Nv?.Reset();
        _lastFrame = -1;
        _cdEmu = null; _cdBoot = false;
        WireAta();
        Ata?.Reset();
        Nic?.Reset();
        Fdc?.PowerOn();
        Pci?.Reset();
        Post();
    }

    /// <summary>Puts the board's disks on the IDE cables: primary master / slave = C: / D:, secondary master = the CD-ROM.</summary>
    private void WireAta()
    {
        if (Ata == null) return;
        Ata.Primary.Disk[0] = Hdd[0]; Ata.Primary.Disk[1] = Hdd[1];
        Ata.Secondary.CdPresent[0] = true; Ata.Secondary.Cd[0] = Cd;
        Ata.Primary.Clock = Ata.Secondary.Clock = () => PitNow;
        foreach (var ch in new[] { Ata.Primary, Ata.Secondary }) { ch.DmaToMem = DmaToMem; ch.DmaFromMem = DmaFromMem; }
    }

    /// <summary>A save state carries the config block when the machine is not the classic one (memory is already in the CPU's state).</summary>
    private bool ConfigInState => Config.CpuGen != 4 || Config.Storage != PcStorage.BiosOnly || Config.BootOrder != PcConfig.Default.BootOrder || Config.Bus != PcBus.Isa;

    // ================= save state =================

    public byte[] SaveState()
    {
        var ms = new MemoryStream();
        SaveState(ms);
        return ms.ToArray();
    }

    /// <summary>The state written to <paramref name="stream"/> (left open): the same bytes as <see cref="SaveState()"/> (the bug capture's state a second reuses one buffer).</summary>
    /// <summary>Diagnostics (state round-trip checks): the state's parts one by one, to name the one that differs.</summary>
    public List<(string name, byte[] data)> DebugStateParts()
    {
        var parts = new List<(string, byte[])>();
        void P(string n, Action<BinaryWriter> a) { var ms = new MemoryStream(); using (var w = new BinaryWriter(ms)) a(w); parts.Add((n, ms.ToArray())); }
        P("cpu", w => Cpu.SaveState(w));
        P("core", w => { w.Write(_idle); w.Write(_port61); w.Write(_lastFrame); w.Write(InstructionsPerSecond); w.Write(_clkEpoch); w.Write(_pitEpoch); });
        P("pic", w => Pic.Save(w)); P("pit", w => Pit.Save(w)); P("kbc", w => Kbc.Save(w)); P("cmos", w => Cmos.Save(w)); P("vga", w => Vga.Save(w));
        P("opl", w => Opl.Save(w)); P("oplengine", w => Opl.SaveEngine(w)); P("dma", w => Dma.Save(w));
        if (Card != null) P("card", w => Card.Save(w));
        if (S3 != null) P("s3", w => S3.Save(w));
        if (Ata != null) P("ata", w => Ata.Save(w));
        P("bios", w => SaveBiosExtras(w));
        if (Config.Bus != PcBus.Isa) P("pci", w => SavePci(w));
        if (Kbc.HasMouse) P("mouse", w => Kbc.SaveMouse(w));
        if (Command != null) P("n98cmd", w => { w.Write(Command.Length); w.Write(Command); w.Write(_cmdPos); });
        if (Ata != null) P("ata-bm", w => { Ata.Primary.SaveBm(w); Ata.Secondary.SaveBm(w); });
        for (int i = 0; i < 4; i++) { var d = i < 2 ? Floppy[i] : Hdd[i - 2]; if (d != null) P("disk" + i, w => w.Write(d.ContentKey())); }
        return parts;
    }

    public void SaveState(Stream stream) => SaveState(stream, false);

    /// <summary><paramref name="undo"/>: a copy only this process loads back (LoadState's undo): the disks named by object, not by hash (no wait for a hash).</summary>
    private void SaveState(Stream stream, bool undo)
    {
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(0x33384350); // "PC83"
            w.Write(S3 != null ? 5 : 4);   // 5: an S3 Trio64 board (its 4 MB video memory, the SVGA block at the end); 2: the AdLib after the VGA; 3: the DMA controllers and the sound card after the AdLib; 4: the AdLib engine's own state (FA-ST-1)
            Cpu.SaveState(w);
            w.Write(_idle); w.Write(_port61); w.Write(_lastFrame); w.Write(InstructionsPerSecond);
            Pic.Save(w); Pit.Save(w); Kbc.Save(w); Cmos.Save(w); Vga.Save(w); Opl.Save(w); Opl.SaveEngine(w);
            Dma.Save(w);
            w.Write(Card?.Id ?? "");
            if (Card != null)
            {
                var cs = new MemoryStream();
                using (var cw = new BinaryWriter(cs)) Card.Save(cw);
                var blob = cs.ToArray();
                w.Write(blob.Length); w.Write(blob);
            }
            // the disks are copy-on-write: their diffs go in the "DSKD" block at the end (base hash + changed sectors), never the whole image here
            for (int i = 0; i < 4; i++) w.Write(false);
            // the VGA harvest picture chip's own state (its stale screen, caches), after everything else: a stock-VGA state is byte for byte what it was
            if (Vga.Picture != null)
            {
                var ps = new MemoryStream();
                using (var pw = new BinaryWriter(ps)) Vga.Picture.Save(pw);
                var blob = ps.ToArray();
                w.Write(0x50434956); w.Write(Vga.Picture.Id); w.Write(blob.Length); w.Write(blob);   // "VICP"
            }
            // the S3's own registers, after everything else (its video memory is the VGA's, saved above): a plain-VGA state is unchanged
            if (Nv != null)
            {
                var ss = new MemoryStream();
                using (var sw = new BinaryWriter(ss)) Nv.Save(sw);
                var blob = ss.ToArray();
                w.Write(0x41475653); w.Write(Nv.CardId); w.Write(blob.Length); w.Write(blob);   // "SVGA"
            }
            if (S3 != null)
            {
                var ss = new MemoryStream();
                using (var sw = new BinaryWriter(ss)) S3.Save(sw);
                var blob = ss.ToArray();
                w.Write(0x41475653); w.Write(S3.CardId); w.Write(blob.Length); w.Write(blob);   // "SVGA"
            }
            // the machine config and its new devices, last: a classic machine's state is byte for byte what it was
            if (ConfigInState)
            {
                w.Write(0x47464350);   // "PCFG"
                Config.Write(w);
                w.Write(Ata != null);
                Ata?.Save(w);
                SaveBiosExtras(w);
                if (Config.Bus != PcBus.Isa) SavePci(w);
            }
            if (Nic != null) { w.Write(0x3043494e); Nic.Save(w); }   // "NIC0", after the config block
            if (Kbc.HasMouse) { w.Write(0x4d325350); Kbc.SaveMouse(w); }   // "PS2M"
            if (Fdc != null) { w.Write(0x30434446); Fdc.Save(w); }   // "FDC0"
            if (Voodoo != null) { w.Write(0x314f4456); Voodoo.Save(w); }   // "VDO1"
            if (Command != null) { w.Write(0x4338394e); w.Write(Command.Length); w.Write(Command); w.Write(_cmdPos); }   // "N98C": the nano98 command + read position
            if (Ata != null && (Ata.Primary.BmActive || Ata.Secondary.BmActive)) { w.Write(0x4d424449); Ata.Primary.SaveBm(w); Ata.Secondary.SaveBm(w); }   // "IDBM": bus-master DMA
            if (Ata != null && Ata.Secondary.AudioActive) { w.Write(0x41444443); Ata.Secondary.SaveAudio(w); }   // "CDDA": CD audio playing or paused
            if (_clkEpoch != 0 || _pitEpoch != 0) { w.Write(0x45535049); w.Write(_clkEpoch); w.Write(_pitEpoch); }   // "IPSE": the instruction rate changed under the running machine (SetIps)
            if (Ata != null && (Ata.Primary.AnyMediaChanged || Ata.Secondary.AnyMediaChanged)) { w.Write(0x434d4443); Ata.Primary.SaveMediaChanged(w); Ata.Secondary.SaveMediaChanged(w); }   // "CDMC": a disc change not yet reported (UNIT ATTENTION)
            // "DSKD": the four drives (A:, B:, C:, D:) as base hash + overlay of changed sectors (DiskImage.SaveDiff)
            w.Write(0x444b5344);
            foreach (var d in new[] { Floppy[0], Floppy[1], Hdd[0], Hdd[1] })
            {
                w.Write(d != null);
                d?.SaveDiff(w, undo);
            }
        }
    }

    /// <summary>Loads a state; a state that does not fit this machine (another video card) throws and leaves the machine as it was.</summary>
    public void LoadState(byte[] s)
    {
        if (S3 == null && Nv == null) { LoadStateCore(s); return; }       // the plain VGA: exactly as before
        var um = new MemoryStream(); SaveState(um, undo: true); var undo = um.ToArray();
        try { LoadStateCore(s); }
        catch { LoadStateCore(undo); throw; }
    }

    private void LoadStateCore(byte[] s)
    {
        using var r = new BinaryReader(new MemoryStream(s));
        if (r.ReadInt32() != 0x33384350) throw new InvalidDataException("not an x86 console state");
        int version = r.ReadInt32();
        if (version is not (1 or 2 or 3 or 4 or 5)) throw new InvalidDataException($"x86 console state version {version} is not known");
        // the video card must be the one the state was saved on: a mismatch is an error before anything is loaded, never a guess
        if ((version == 5) != (S3 != null))
            throw new InvalidDataException($"this state was saved with the {(version == 5 ? "S3 Trio64" : "VGA")} video card, this machine has the {(S3 != null ? "S3 Trio64" : "VGA")}");
        Cpu.LoadState(r);
        // a state saved before the INT 10h mode-set replay: its ROM gets the new stub (the old one was OUT E0h / IRET)
        if (Cpu.Mem.Length > Rom + 0x10000 - 1 && Cpu.Mem[Rom + StubBase + 0x40] == 0xe6 && Cpu.Mem[Rom + StubBase + 0x42] == 0xcf) InstallInt10Replay();
        // a state with the first replay code (no font op): the newer code, unless the CPU is inside it right now
        else if (Cpu.Mem.Length > Rom + 0x10000 - 1 && Cpu.Mem[Rom + Int10Replay + 14] == 0x89 && !(Cpu.SegSel[1] == RomSeg && Cpu.Ip >= Int10Replay && Cpu.Ip < Int10Replay + 0x90)) InstallInt10Replay();
        // states saved before the V86 INT 16h idle HLT (2026-11-21) have zeros there: put the 3 bytes in, so their DOS boxes stop burning time slices
        if (Cpu.Mem.Length > Rom + 0x10000 - 1 && Cpu.Mem[Rom + Int16Stub + 4] == 0 && Cpu.Mem[Rom + Int16Stub + 5] == 0 && Cpu.Mem[Rom + Int16Stub + 6] == 0) { Cpu.Mem[Rom + Int16Stub + 4] = 0xf4; Cpu.Mem[Rom + Int16Stub + 5] = 0xeb; Cpu.Mem[Rom + Int16Stub + 6] = 0xf9; }
        _idle = r.ReadInt64(); _port61 = r.ReadByte(); _lastFrame = r.ReadInt64(); InstructionsPerSecond = r.ReadInt64();
        Pic.Load(r); Pit.Load(r); Kbc.Load(r); Cmos.Load(r); Vga.Load(r);
        if (version >= 2) Opl.Load(r); else { Opl.Reset(); Opl.Resync(); }
        if (version >= 4) Opl.LoadEngine(r); else Opl.NoEngineState();   // a version 1 state (before the AdLib): the chip starts silent
        if (version >= 3)
        {
            Dma.Load(r);
            Dma.ClearCallbacks();
            string id = r.ReadString();
            byte[]? blob = id.Length > 0 ? r.ReadBytes(r.ReadInt32()) : null;
            // the card saved is restored when the same card is in the slot; another card (or none then) starts cold: a clean re-key, never a guess
            if (Card != null && blob != null && id == Card.Id) { using var cr = new BinaryReader(new MemoryStream(blob)); Card.Load(cr); }
            else Card?.Reset();
        }
        else { Dma.Reset(); Card?.Reset(); }   // before the DMA controllers / cards
        Dma.SecondOpen = Card is not Dosbox072Tandy;
        var disks = new DiskImage?[4];
        var old = new[] { Floppy[0], Floppy[1], Hdd[0], Hdd[1] };
        for (int i = 0; i < 4; i++)
        {
            if (!r.ReadBoolean()) continue;
            var data = r.ReadBytes(r.ReadInt32());
            // an old full-disk state: on the base already in that drive (when the size fits) it becomes base + diff, so the next save is lean
            disks[i] = DiskImage.FromFull(old[i]?.Base, data, i < 2);
        }
        Floppy[0] = disks[0]; Floppy[1] = disks[1]; Hdd[0] = disks[2]; Hdd[1] = disks[3];
        // the picture chip saved is restored when the same chip draws now; another one (or a state without one) starts it cold: a clean re-key, never a guess
        string? picId = null; byte[]? picBlob = null;
        string? svgaId = null; byte[]? svgaBlob = null;
        PcConfig? saved = null; bool mouse = false;
        bool nicLoaded = false, fdcLoaded = false, vdoLoaded = false, cdda = false, idbm = false, cdmc = false, cmdLoaded = false, ipse = false;
        while (r.BaseStream.Position + 4 <= r.BaseStream.Length)
        {
            int magic = r.ReadInt32();
            if (magic == 0x50434956) { picId = r.ReadString(); picBlob = r.ReadBytes(r.ReadInt32()); }
            else if (magic == 0x4d325350 && Kbc.HasMouse) { Kbc.LoadMouse(r); mouse = true; }
            else if (magic == 0x41475653) { svgaId = r.ReadString(); svgaBlob = r.ReadBytes(r.ReadInt32()); }
            else if (magic == 0x47464350)
            {
                saved = PcConfig.Read(r);
                // (VramMB is not in the config block: the SVGA card id, VBOXVGA-16MB, carries the video memory size and is checked below)
                // X86_VOODOO_ACCEPT_STOCK=1 (explicit, test tools only): a Stock Voodoo state loads into an Enhanced Voodoo machine and back (the render
                // scale is presentation only; the guest state is the same); without it the scale is part of the machine and a mismatch is refused
                if (AcceptOtherVoodooScale && saved.Voodoo && Config.Voodoo) saved = saved with { VoodooScale = Config.VoodooScale };
                if (saved with { MemoryMB = Config.MemoryMB, VramMB = Config.VramMB } != Config) throw new InvalidDataException($"this x86 state is for a {saved} machine, this machine is {Config}");
                WireAta();
                if (r.ReadBoolean()) Ata!.Load(r);
                LoadBiosExtras(r);
                if (Config.Bus != PcBus.Isa) LoadPci(r);
            }
            else if (magic == 0x3043494e && Nic != null) { Nic.Load(r); nicLoaded = true; }
            else if (magic == 0x30434446 && Fdc != null) { Fdc.Load(r); fdcLoaded = true; }
            else if (magic == 0x314f4456 && Voodoo != null) { Voodoo.Load(r); vdoLoaded = true; }
            else if (magic == 0x4338394e) { Command = r.ReadBytes(r.ReadInt32()); _cmdPos = r.ReadInt32(); cmdLoaded = true; }
            else if (magic == 0x4d424449 && Ata != null) { Ata.Primary.LoadBm(r); Ata.Secondary.LoadBm(r); idbm = true; }
            else if (magic == 0x41444443 && Ata != null) { Ata.Secondary.LoadAudio(r); cdda = true; }
            else if (magic == 0x45535049) { _clkEpoch = r.ReadInt64(); _pitEpoch = r.ReadInt64(); ipse = true; }
            else if (magic == 0x434d4443 && Ata != null) { Ata.Primary.LoadMediaChanged(r); Ata.Secondary.LoadMediaChanged(r); cdmc = true; }
            else if (magic == 0x444b5344)
            {
                var bases = new List<byte[]?>();
                foreach (var o in old) bases.Add(o?.Base);
                var bigs = old.Select(o => o?.Big).ToList();   // (nano98 large game drives)
                for (int i = 0; i < 4; i++) disks[i] = r.ReadBoolean() ? DiskImage.LoadDiff(r, bases, bigs) : null;
                Floppy[0] = disks[0]; Floppy[1] = disks[1]; Hdd[0] = disks[2]; Hdd[1] = disks[3];
            }
            else throw new InvalidDataException($"unknown block {magic:X8} in an x86 state");
        }
        if (!cmdLoaded) _cmdPos = 0;
        if (!ipse) { _clkEpoch = 0; _pitEpoch = 0; }
        if (Cpu.Model.Gen == 6 && Cpu.Model.Ips != InstructionsPerSecond) SetIps(Cpu.Model.Ips);   // e.g. a P2 state loaded with another X86_P2CPI: time goes on from where it was
        if (!cdda) Ata?.Secondary.ClearAudio();
        if (!cdmc) { Ata?.Primary.ClearAllMediaChanged(); Ata?.Secondary.ClearAllMediaChanged(); }   // no "CDMC" (and every older state): the disc in the drive is the one the guest already knows
        if (!idbm) { Ata?.Primary.ClearBm(); Ata?.Secondary.ClearBm(); }
        if (Nic != null && !nicLoaded) Nic.Reset();   // a state from before the card: it sits there as after a cold boot, unconfigured
        if (!mouse) Kbc.ResetMouse();
        if (Fdc != null && !fdcLoaded) Fdc.PowerOn();
        if (Voodoo != null && !vdoLoaded) throw new InvalidDataException("this x86 state has no Voodoo card state, this machine has the Voodoo");   // a state from before the controller: it starts as after a cold boot
        if (saved == null && ConfigInState) throw new InvalidDataException($"this x86 state is for the classic machine (486, BIOS disks), this machine is {Config}");
        WireAta();
        // the video card must be the one the state was saved on: a mismatch is an error, never a guess
        string cardId = S3 != null ? S3.CardId : Nv != null ? Nv.CardId : "VGA";
        if ((svgaId ?? "VGA") != cardId)
            throw new InvalidDataException($"this state was saved with the {svgaId ?? "VGA"} video card, this machine has the {cardId}");
        if (Nv != null) { using var nr = new BinaryReader(new MemoryStream(svgaBlob!)); Nv.Load(nr); }
        if (S3 != null) { using var sr = new BinaryReader(new MemoryStream(svgaBlob!)); S3.Load(sr); }
        if (Vga.Picture != null)
        {
            if (picBlob != null && picId == Vga.Picture.Id) { using var pr = new BinaryReader(new MemoryStream(picBlob)); Vga.Picture.Load(pr); }
            else Vga.Picture.Reset();
        }
        Stopped = null;
    }
}
