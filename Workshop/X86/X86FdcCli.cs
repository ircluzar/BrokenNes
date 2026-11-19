using System;
using System.IO;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86fdc [--floppy img]</c>: the 82077AA floppy controller of the WIN98-P166 board driven through its ports from C# (no CPU run: machine time
/// passes with <see cref="PcMachine.IdleFor"/>): reset + the four SENSE INTERRUPTs, VERSION / DUMPREG / LOCK / CONFIGURE, SPECIFY, RECALIBRATE,
/// SEEK, READ DATA through DMA channel 2 (one sector, a multi-track run ending at terminal count, an implied seek), WRITE DATA round trip, READ ID,
/// FORMAT, the disk-change line, a wrong data rate, an empty drive (the read never ends until reset), a save state taken mid-command, and the
/// classic board (no controller: the ports float). Exit code 0 = every check passed.
/// </summary>
internal static class X86FdcCli
{
    private const long Ips = 20_000_000;
    private static int _fail, _pass;

    private static void Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {what}");
    }

    private static PcMachine _m = null!;
    private static byte In(int p) => _m.In8(p);
    private static void Out(int p, int v) => _m.Out8(p, (byte)v);
    private static void Idle(double ms) => _m.IdleFor((long)(ms * Ips / 1000));

    private static bool Cmd(params int[] b)
    {
        foreach (int v in b)
        {
            int guard = 0;
            while ((In(0x3f4) & 0xc0) != 0x80) { if (++guard > 1000) return false; Idle(0.01); }
            Out(0x3f5, v);
        }
        return true;
    }

    private static byte[] Results(int max = 16)
    {
        var r = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < 2000 && r.Count < max; i++)
        {
            byte s = In(0x3f4);
            if ((s & 0xd0) == 0xd0) { r.Add(In(0x3f5)); continue; }
            if ((s & 0xd0) == 0x80) break;
            Idle(0.05);
        }
        return r.ToArray();
    }

    private static bool WaitIrq(double ms = 50)
    {
        for (double t = 0; t < ms; t += 0.1) { if (_m.Fdc!.Irq) return true; Idle(0.1); }
        return _m.Fdc!.Irq;
    }

    private static void Dma(uint addr, int len, bool toMem)
    {
        Out(0x0a, 0x06);                       // mask channel 2
        Out(0x0c, 0);
        Out(0x0b, toMem ? 0x46 : 0x4a);        // single, increment, write (device -> memory) / read, channel 2
        Out(0x04, (int)(addr & 0xff)); Out(0x04, (int)((addr >> 8) & 0xff));
        Out(0x81, (int)(addr >> 16));
        Out(0x05, (len - 1) & 0xff); Out(0x05, (len - 1) >> 8);
        Out(0x0a, 0x02);                       // unmask
    }

    private static void Reset()
    {
        Out(0x3f2, 0x00); Idle(0.1); Out(0x3f2, 0x1c);   // reset, then drive A, motor A, DMA+IRQ on
    }

    private static string Hex(byte[] b) => BitConverter.ToString(b);

    public static int Run(string[] args)
    {
        byte[] img;
        string? path = null;
        for (int i = 1; i < args.Length - 1; i++) if (args[i] == "--floppy") path = args[i + 1];
        if (path != null) img = File.ReadAllBytes(path);
        else { img = new byte[1474560]; for (int i = 0; i < img.Length; i++) img[i] = (byte)((i / 512 * 7) ^ (i * 13) ^ (i >> 9)); }
        var orig = (byte[])img.Clone();

        _m = new PcMachine(PcConfig.Win98) { InstructionsPerSecond = Ips };
        _m.Floppy[0] = new DiskImage(img, true);
        _m.PowerOn();
        var d = _m.Floppy[0]!;
        Check(_m.Fdc != null, "WIN98-P166 has the floppy controller");

        // reset: IRQ, four SENSE INTERRUPTs C0-C3, then an invalid one (80h)
        Reset();
        Check(WaitIrq(), "IRQ 6 after reset");
        bool four = true;
        for (int i = 0; i < 4; i++) { Cmd(0x08); var r = Results(); four &= r.Length == 2 && r[0] == 0xc0 + i && r[1] == 0; }
        Check(four, "four post-reset SENSE INTERRUPTs (C0h-C3h, PCN 0)");
        Check(!_m.Fdc!.Irq, "IRQ cleared by SENSE INTERRUPT");
        Cmd(0x08); Check(Hex(Results()) == "80", "SENSE INTERRUPT with nothing pending: 80h");
        Cmd(0x10); Check(Hex(Results()) == "90", "VERSION = 90h");
        Cmd(0x18); Check(Hex(Results()) == "80", "unknown command: 80h");
        Cmd(0x03, 0xdf, 0x02); Check(In(0x3f4) == 0x80, "SPECIFY (no result phase)");
        Cmd(0x13, 0x00, 0x57, 0x00); Check(In(0x3f4) == 0x80, "CONFIGURE (implied seek on, FIFO on)");
        Cmd(0x94); Check(Hex(Results()) == "10", "LOCK = 10h");
        Cmd(0x12, 0x00);
        Cmd(0x0e); var dump = Results();
        Check(dump.Length == 10 && dump[4] == 0xdf && dump[5] == 0x02 && dump[7] == 0x80 && dump[8] == 0x57, "DUMPREG " + Hex(dump));
        Reset(); WaitIrq(); for (int i = 0; i < 4; i++) { Cmd(0x08); Results(); }
        Cmd(0x0e); dump = Results(); Check(dump.Length == 10 && dump[8] == 0x57, "LOCK keeps CONFIGURE across a reset");
        Cmd(0x14); Results();

        Out(0x3f7, 0);                         // 500 kb/s
        Check((In(0x3f7) & 0x80) != 0, "disk-change line set after power-on");
        Cmd(0x07, 0x00); Check(WaitIrq(), "RECALIBRATE raises IRQ 6");
        Cmd(0x08); var sr = Results(); Check(Hex(sr) == "20-00", "SENSE INTERRUPT after recalibrate: " + Hex(sr));
        Cmd(0x04, 0x00); var st3 = Results(); Check(st3.Length == 1 && (st3[0] & 0x10) != 0, "SENSE DRIVE STATUS: track 0 " + Hex(st3));
        Cmd(0x0f, 0x00, 5); WaitIrq(); Cmd(0x08); sr = Results(); Check(Hex(sr) == "20-05", "SEEK to 5: " + Hex(sr));
        Check((In(0x3f7) & 0x80) == 0, "disk-change line cleared by a step");
        Cmd(0x8f | 0x40, 0x00, 3); WaitIrq(); Cmd(0x08); sr = Results(); Check(Hex(sr) == "20-08", "relative SEEK +3: " + Hex(sr));
        Cmd(0x8f, 0x00, 8); WaitIrq(); Cmd(0x08); Results();

        // READ DATA C0 H0 R1, one sector
        const uint buf = 0x20000;
        Dma(buf, 512, true);
        Cmd(0xe6, 0x00, 0, 0, 1, 2, 18, 0x1b, 0xff);
        Check(WaitIrq(), "READ DATA raises IRQ 6");
        var rr = Results();
        Check(Hex(rr) == "00-00-00-00-00-02-02", "READ DATA result (TC after sector 1): " + Hex(rr));
        Check(!_m.Fdc.Irq, "IRQ cleared by reading the result");
        Check(_m.Cpu.Mem.AsSpan((int)buf, 512).SequenceEqual(img.AsSpan(0, 512)), "sector 0 bytes = image");

        // multi-track: C2 H0 R17 for 4 sectors -> 17,18 head 0, 1,2 head 1
        Dma(buf, 2048, true);
        Cmd(0xe6, 0x00, 2, 0, 17, 2, 18, 0x1b, 0xff);   // implied seek from 0 to 2
        WaitIrq(); rr = Results();
        Check(Hex(rr) == "04-00-00-02-01-03-02", "multi-track READ (implied seek) result: " + Hex(rr));
        long lba = (2 * 2 + 0) * 18 + 16;
        Check(_m.Cpu.Mem.AsSpan((int)buf, 2048).SequenceEqual(img.AsSpan((int)lba * 512, 2048)), "multi-track bytes = image");

        // read past the end of the cylinder without TC: end of cylinder error
        Dma(buf, 8192, true);
        Cmd(0x46, 0x00, 2, 0, 17, 2, 18, 0x1b, 0xff);
        WaitIrq(); rr = Results();
        Check(rr.Length == 7 && rr[0] == 0x40 && rr[1] == 0x80 && rr[3] == 3 && rr[5] == 1, "READ to end of track without TC: EN " + Hex(rr));
        Out(0x0a, 0x06);

        // sector out of range: no data
        Dma(buf, 512, true);
        Cmd(0x46, 0x00, 2, 0, 19, 2, 19, 0x1b, 0xff);
        WaitIrq(); rr = Results();
        Check(rr.Length == 7 && rr[0] == 0x40 && rr[1] == 0x04, "missing sector: no data " + Hex(rr));

        // WRITE DATA then READ back (C10 H1 R5)
        var pat = new byte[512]; for (int i = 0; i < 512; i++) pat[i] = (byte)(255 - i);
        Array.Copy(pat, 0, _m.Cpu.Mem, 0x30000, 512);
        Dma(0x30000, 512, false);
        Cmd(0xc5, 0x04, 10, 1, 5, 2, 18, 0x1b, 0xff);
        WaitIrq(); rr = Results();
        Check(Hex(rr) == "04-00-00-0A-01-06-02", "WRITE DATA result: " + Hex(rr));
        lba = (10 * 2 + 1) * 18 + 4;
        Check(_m.Floppy[0]!.ToArray().AsSpan((int)lba * 512, 512).SequenceEqual(pat) && d.Dirty, "written sector landed in the image");

        // READ ID on head 1
        Cmd(0x4a, 0x04); WaitIrq(); rr = Results();
        Check(rr.Length == 7 && rr[0] == 0x04 && rr[3] == 10 && rr[4] == 1 && rr[6] == 2, "READ ID: " + Hex(rr));

        // FORMAT cylinder 10 head 1 with E5h (DMA feeds 18 C/H/R/N headers)
        for (int s = 0; s < 18; s++) { _m.Cpu.Mem[0x31000 + s * 4] = 10; _m.Cpu.Mem[0x31001 + s * 4] = 1; _m.Cpu.Mem[0x31002 + s * 4] = (byte)(s + 1); _m.Cpu.Mem[0x31003 + s * 4] = 2; }
        Dma(0x31000, 72, false);
        Cmd(0x4d, 0x04, 2, 18, 0x54, 0xe5); WaitIrq(); rr = Results();
        Check(rr.Length == 7 && (rr[0] & 0xc0) == 0, "FORMAT TRACK: " + Hex(rr));
        lba = (10 * 2 + 1) * 18;
        var fmt = _m.Floppy[0]!.ToArray(); bool allE5 = true; for (int i = 0; i < 18 * 512; i++) allE5 &= fmt[lba * 512 + i] == 0xe5;
        Check(allE5, "formatted track is E5h");
        _m.Floppy[0]!.Write(lba, 18, orig.AsSpan((int)lba * 512, 18 * 512));   // (the base image is never written: copy-on-write)

        // a wrong data rate: no address mark
        Out(0x3f7, 2);
        Dma(buf, 512, true);
        Cmd(0x46, 0x00, 2, 0, 1, 2, 18, 0x1b, 0xff);
        Idle(1000); rr = Results();
        Check(rr.Length == 7 && rr[0] == 0x40 && rr[1] == 0x01, "250 kb/s on a 1.44 MB disk: missing address mark " + Hex(rr));
        Out(0x3f7, 0);

        // a save state taken in the middle of a read, loaded into a fresh machine, finishes the same
        Dma(buf, 512, true);
        Cmd(0x46, 0x00, 3, 0, 4, 2, 18, 0x1b, 0xff);
        var state = _m.SaveState();
        var keep = _m;
        _m = new PcMachine(PcConfig.Win98) { InstructionsPerSecond = Ips };
        _m.LoadState(state);
        Array.Clear(_m.Cpu.Mem, (int)buf, 512);
        WaitIrq(); rr = Results();
        lba = (3 * 2) * 18 + 3;
        Check(Hex(rr) == "00-00-00-03-00-05-02" && _m.Cpu.Mem.AsSpan((int)buf, 512).SequenceEqual(orig.AsSpan((int)lba * 512, 512)), "save state mid-READ finishes after a load: " + Hex(rr));
        _m = keep;

        // disk swap: the change line comes back; an empty drive: a read never ends, reset recovers
        _m.Floppy[0] = null;
        Check((In(0x3f7) & 0x80) != 0, "disk-change line after the disk is removed");
        Dma(buf, 512, true);
        Cmd(0x46, 0x00, 0, 0, 1, 2, 18, 0x1b, 0xff);
        Idle(3000);
        Check(!_m.Fdc.Irq && (In(0x3f4) & 0x80) == 0, "empty drive: READ stays in the execution phase");
        Reset(); WaitIrq(); for (int i = 0; i < 4; i++) { Cmd(0x08); Results(); }
        Check(In(0x3f4) == 0x80, "reset recovers from the stalled read");
        _m.Floppy[0] = new DiskImage(orig, true);
        Check((In(0x3f7) & 0x80) != 0, "disk-change line after a disk is inserted");
        Cmd(0x0f, 0x00, 1); WaitIrq(); Cmd(0x08); Results();
        Check((In(0x3f7) & 0x80) == 0, "a seek clears it with the new disk in");

        // a state without the FDC block (an older state) loads with the controller reset
        var plain = new PcMachine(PcConfig.Win98) { InstructionsPerSecond = Ips };
        plain.PowerOn();
        Check(plain.In8(0x3f4) == 0x00, "fresh board: controller held in reset (DOR 0)");

        // the classic machine has no controller: ports float, the save state is unchanged in shape
        var classic = new PcMachine(4 * 1024 * 1024) { InstructionsPerSecond = Ips };
        Check(classic.Fdc == null && classic.In8(0x3f4) == 0xff, "classic board: no controller, 3F4h reads FFh");

        Console.WriteLine($"x86fdc: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
