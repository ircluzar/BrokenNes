using System;
using System.Linq;
using System.Text;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86pci</c>: self-tests for the PCI board and the BIOS's Windows 98 feature set (docs/projects/x86-modular-pc.md, M4): config
/// mechanism #1, PCI BIOS INT 1Ah B1xxh (present, find device / class, config reads, IRQ routing options), BIOS32 + "$PCI", the $PIR
/// table, APM 1.2 (install check, 32-bit connect, power status), the PnP BIOS (header checksum, node count, first node), INT 15h E820,
/// the PCI state block, and the classic machine answering none of it. Exit code 0 when every check passes.
/// </summary>
internal static class X86PciCli
{
    private static int _fail, _pass;
    private static void Check(bool ok, string what) { if (ok) _pass++; else { _fail++; Console.WriteLine("FAIL " + what); } }

    public static int Run(string[] args)
    {
        Classic();
        Board();
        Bios();
        State();
        Console.WriteLine($"x86pci: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    private static PcMachine Win98() => new(PcConfig.Win98, CpuModel.PentiumMmx, PcVideo.S3Trio64);

    private static void Classic()
    {
        var m = new PcMachine();
        m.PowerOn();
        Check(m.Pci == null, "classic machine has no PCI bus");
        m.Out32(0xcf8, 0x80000000);
        Check(m.In32(0xcfc) == 0xffffffff, "classic machine: nothing answers CFCh");
        Check(Enumerable.Range(0, 0xa000).All(i => m.Cpu.Mem[0xf0000 + i] == 0), "classic ROM 0000-9FFF stays empty");
    }

    private static void Board()
    {
        var m = Win98();
        m.PowerOn();
        uint Cfg(int devFn, int reg) { m.Out32(0xcf8, 0x80000000u | (uint)devFn << 8 | (uint)reg); return m.In32(0xcfc); }
        Check(Cfg(0x00, 0) == 0x71008086, $"host bridge 8086:7100 ({Cfg(0, 0):X8})");
        Check(Cfg(0x38, 0) == 0x71108086, "PIIX4 ISA 8086:7110");
        Check(Cfg(0x39, 0) == 0x71118086, "PIIX4 IDE 8086:7111");
        Check(Cfg(0x48, 0) == 0x88115333, "S3 Trio64 5333:8811 at 09.0");
        Check(Cfg(0x50, 0) == 0xffffffff, "empty slot reads FFFFFFFFh");
        Check((Cfg(0x38, 0x60) & 0xff) == 11, "PIRQA routed to IRQ 11");
        Check((Cfg(0x48, 0x3c) & 0xff) == 11, "S3 interrupt line 11");
        // BAR sizing on the S3
        m.Out32(0xcf8, 0x80004810); m.Out32(0xcfc, 0xffffffff);
        Check(m.In32(0xcfc) == 0xfc000000, "S3 BAR 0 sizes as 64 MB");
        m.Out32(0xcfc, 0xe0000000);
        // byte access to CFEh
        m.Out32(0xcf8, 0x80003800);
        Check(m.In16(0xcfe) == 0x7110, "16-bit read at CFEh");
    }

    // real-mode program at 0:1000, results in 0:0500..
    private static PcMachine RunReal(byte[] code)
    {
        var m = Win98();
        m.PowerOn();
        m.Stopped = null;
        Array.Copy(code, 0, m.Cpu.Mem, 0x1000, code.Length);
        foreach (int s in new[] { I386.SEG_DS, I386.SEG_ES, I386.SEG_SS }) m.Cpu.LoadSeg(s, 0);
        m.Cpu.R[4] = 0x7000;
        m.Cpu.Flags = 0x2;
        m.Cpu.Halt = false;
        m.Cpu.SetCsIp(0, 0x1000);
        m.RunClock(20_000);
        return m;
    }

    private static void Bios()
    {
        var code = new System.Collections.Generic.List<byte>();
        void B(params int[] b) => code.AddRange(b.Select(x => (byte)x));
        // PCI present
        B(0xb8, 0x01, 0xb1, 0xcd, 0x1a, 0x66, 0x89, 0x16, 0x00, 0x05, 0xa3, 0x04, 0x05, 0x89, 0x1e, 0x06, 0x05, 0x9c, 0x58, 0xa3, 0x08, 0x05);
        // find S3
        B(0xb8, 0x02, 0xb1, 0xb9, 0x11, 0x88, 0xba, 0x33, 0x53, 0x31, 0xf6, 0xcd, 0x1a, 0x89, 0x1e, 0x0a, 0x05, 0x9c, 0x58, 0xa3, 0x0c, 0x05);
        // find class 060100 (ISA bridge)
        B(0x66, 0xb9, 0x00, 0x01, 0x06, 0x00, 0xb8, 0x03, 0xb1, 0x31, 0xf6, 0xcd, 0x1a, 0x89, 0x1e, 0x0e, 0x05);
        // read dword 00 of 00:09.0
        B(0xb8, 0x0a, 0xb1, 0xbb, 0x48, 0x00, 0xbf, 0x00, 0x00, 0xcd, 0x1a, 0x66, 0x89, 0x0e, 0x10, 0x05);
        // APM install check
        B(0xb8, 0x00, 0x53, 0x31, 0xdb, 0xcd, 0x15, 0xa3, 0x14, 0x05, 0x89, 0x1e, 0x16, 0x05, 0x89, 0x0e, 0x18, 0x05);
        // APM 32-bit connect
        B(0xb8, 0x03, 0x53, 0x31, 0xdb, 0xcd, 0x15, 0xa3, 0x1a, 0x05, 0x66, 0x89, 0x1e, 0x1c, 0x05);
        // PnP function 0: (0, &num 0:0520, &size 0:0522, F000)
        B(0x68, 0x00, 0xf0, 0x6a, 0x00, 0x68, 0x22, 0x05, 0x6a, 0x00, 0x68, 0x20, 0x05, 0x6a, 0x00, 0x9a, 0x30, 0x92, 0x00, 0xf0, 0x83, 0xc4, 0x0c, 0xa3, 0x24, 0x05);
        // PnP function 1: node 0 at 0:0526 -> buffer 0:0600
        B(0xc6, 0x06, 0x26, 0x05, 0x00);
        B(0x68, 0x00, 0xf0, 0x6a, 0x01, 0x6a, 0x00, 0x68, 0x00, 0x06, 0x6a, 0x00, 0x68, 0x26, 0x05, 0x6a, 0x01, 0x9a, 0x30, 0x92, 0x00, 0xf0, 0x83, 0xc4, 0x0e, 0xa3, 0x28, 0x05);
        // PCI IRQ routing options: buffer header at 0:0540 { 64, 0560h, 0 }
        B(0xc7, 0x06, 0x40, 0x05, 0x40, 0x00, 0xc7, 0x06, 0x42, 0x05, 0x60, 0x05, 0xc7, 0x06, 0x44, 0x05, 0x00, 0x00);
        B(0xb8, 0x0e, 0xb1, 0x31, 0xdb, 0xbf, 0x40, 0x05, 0xcd, 0x1a, 0xa3, 0x46, 0x05, 0x9c, 0x58, 0xa3, 0x48, 0x05);
        // E820 entry 0 at 0:0700
        B(0x66, 0xb8, 0x20, 0xe8, 0x00, 0x00, 0x66, 0x31, 0xdb, 0x66, 0xb9, 0x14, 0x00, 0x00, 0x00, 0x66, 0xba, 0x50, 0x41, 0x4d, 0x53, 0xbf, 0x00, 0x07, 0xcd, 0x15, 0x66, 0xa3, 0x4c, 0x05);
        B(0xfa, 0xf4);
        var m = RunReal(code.ToArray());
        uint W(uint a) => m.Phys16(a); uint D(uint a) => m.Phys32(a);
        Check(m.Stopped == null, $"program ran ({m.Stopped})");
        Check(D(0x500) == 0x20494350, $"PCI BIOS present 'PCI ' (EDX {D(0x500):X8})");
        Check(W(0x504) == 0x0001 && W(0x506) == 0x0210 && (W(0x508) & 1) == 0, $"PCI BIOS 2.10, mechanism 1 (AX {W(0x504):X4} BX {W(0x506):X4})");
        Check(W(0x50a) == 0x0048 && (W(0x50c) & 1) == 0, $"find device S3 -> 00:09.0 (BX {W(0x50a):X4})");
        Check(W(0x50e) == 0x0038, $"find class 060100 -> 00:07.0 (BX {W(0x50e):X4})");
        Check(D(0x510) == 0x88115333, $"read config dword ({D(0x510):X8})");
        Check(W(0x514) == 0x0102 && W(0x516) == 0x504d && (W(0x518) & 3) == 3, $"APM 1.2 install check (AX {W(0x514):X4} BX {W(0x516):X4})");
        Check(W(0x51a) == 0xf000 && D(0x51c) == 0x9220, $"APM 32-bit connect (AX {W(0x51a):X4} EBX {D(0x51c):X8})");
        Check(W(0x524) == 0 && m.Phys8(0x520) >= 8 && W(0x522) > 9, $"PnP nodes: {m.Phys8(0x520)} of up to {W(0x522)} bytes (AX {W(0x524):X4})");
        Check(W(0x528) == 0 && m.Phys8(0x526) == 1 && W(0x600) > 9 && m.Phys8(0x602) == 0 && D(0x603) == 0x0000d041, $"PnP node 0 = PNP0000 (AX {W(0x528):X4}, id {D(0x603):X8}, next {m.Phys8(0x526)})");
        Check(W(0x546) >> 8 == 0 && (W(0x548) & 1) == 0 && W(0x540) == 32 && m.Phys8(0x561) == 0x38 && m.Phys8(0x562) == 0x60, $"IRQ routing options (AH {W(0x546) >> 8:X2}, size {W(0x540)})");
        Check(D(0x54c) == 0x534d4150 && D(0x708) == 0x9fc00 && D(0x710) == 1, "E820 entry 0 = 0-9FC00 RAM");

        // ROM structures
        int Find(string sig, int step) { var s = Encoding.ASCII.GetBytes(sig); for (int a = 0xe0000; a < 0x100000; a += step) if (m.Cpu.Mem.Skip(a).Take(4).SequenceEqual(s)) return a; return -1; }
        byte Sum(int a, int n) { byte s = 0; for (int i = 0; i < n; i++) s += m.Cpu.Mem[a + i]; return s; }
        int b32 = Find("_32_", 16), pir = Find("$PIR", 16), pnp = Find("$PnP", 16);
        Check(b32 > 0 && Sum(b32, 16) == 0, "BIOS32 directory found, checksum 0");
        Check(pir > 0 && Sum(pir, m.Cpu.Mem[pir + 6]) == 0 && m.Cpu.Mem[pir + 9] == 0x38, "$PIR found, checksum 0, router 00:07.0");
        Check(pnp > 0 && Sum(pnp, 0x21) == 0 && m.Cpu.Mem[pnp + 4] == 0x10, "$PnP found, checksum 0, version 1.0");
        // the BIOS32 directory call (normally a 32-bit far call; the service trap does not care about the mode)
        m.Cpu.R[0] = 0x49435024; m.Cpu.R[3] = 0;
        m.Out8(PcMachine.Bios32Port, 0);
        Check((m.Cpu.R[0] & 0xff) == 0 && m.Cpu.R[3] == 0xf0000 && m.Cpu.R[2] == 0x9210, $"BIOS32 finds $PCI (AL {m.Cpu.R[0] & 0xff:X2}, EBX {m.Cpu.R[3]:X8}, EDX {m.Cpu.R[2]:X8})");
        m.Cpu.R[0] = 0x12345678; m.Cpu.R[3] = 0;
        m.Out8(PcMachine.Bios32Port, 0);
        Check((m.Cpu.R[0] & 0xff) == 0x80, "BIOS32: unknown service = 80h");
        // the 32-bit PCI entry: read the host bridge's id
        m.Cpu.R[0] = 0xb10a; m.Cpu.R[3] = 0x0000; m.Cpu.R[7] = 0;
        m.Out8(PcMachine.Pci32Port, 0);
        Check(m.Cpu.R[1] == 0x71008086 && (m.Cpu.GetFlags() & 1) == 0, "32-bit PCI BIOS reads config");
        // APM power off through the protected-mode entry
        m.Cpu.R[0] = 0x5307; m.Cpu.R[3] = 0x0001; m.Cpu.R[1] = 0x0003;
        m.Out8(PcMachine.ApmPmPort, 0);
        Check(m.Stopped != null && m.Stopped.Contains("powered off"), $"APM set power state off stops the machine ({m.Stopped})");
    }

    private static void State()
    {
        var m = Win98();
        m.PowerOn();
        m.Out32(0xcf8, 0x80003860); m.Out8(0xcfd, 0x05);   // PIRQB -> 5
        var s = m.SaveState();
        var n = Win98();
        n.LoadState(s);
        n.Out32(0xcf8, 0x80003860);
        Check((n.In32(0xcfc) >> 8 & 0xff) == 5, "PCI config survives a state");
        bool refused = false;
        try { var c = new PcMachine(PcConfig.Win98 with { Bus = PcBus.Isa }, CpuModel.PentiumMmx, PcVideo.S3Trio64); c.LoadState(s); } catch (Exception) { refused = true; }
        Check(refused, "a PCI state does not load into an ISA board");
    }
}
