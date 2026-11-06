using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// The x86 console's disk and boot tools.
/// <list type="bullet">
/// <item><c>--x86disk --out img [--base img] [--put host=DOSPATH]... [--drop DOSPATH]... [--autoexec file] [--config file]</c>: a boot disk built from the
/// FreeDOS 1.4 LiteUSB image (C:\BrokenNes-tools\dos\freedos\FD14LITE.img, a 32 MB FAT16 hard disk with the FreeDOS kernel, COMMAND.COM and
/// \FREEDOS\BIN): our own FDCONFIG.SYS / FDAUTO.BAT replace the installer's (boot to a prompt; or run a program), and files are added.</item>
/// <item><c>--x86boot --hdd img | --floppy img [--frames N] [--until text] [--type text] [--shot png] [--save img] [--get DOSPATH=host]...
/// [--ips N]</c>: boots it headless, runs until the screen's text (or what DOS printed) contains <c>--until</c> (or N frames), types keys,
/// writes a screenshot, the changed disk, and files read back out of it.</item>
/// </list>
/// </summary>
internal static class X86BootCli
{
    public const string FreeDosLite = @"C:\BrokenNes-tools\dos\freedos\FD14LITE.img";

    public const string DefaultConfig = "!FILES=40\r\n!BUFFERS=20\r\n!LASTDRIVE=Z\r\nDOS=HIGH\r\nSHELL=\\COMMAND.COM \\ /E:1024 /P=\\FDAUTO.BAT\r\n";
    public const string DefaultAutoexec = "@ECHO OFF\r\nSET DOSDIR=\\FREEDOS\r\nSET PATH=%DOSDIR%\\BIN\r\nVER\r\n";

    public static int Disk(string[] args)
    {
        string? outPath = Arg(args, "--out");
        if (outPath == null) { Console.WriteLine("x86disk: --out img"); return 1; }
        var img = BuildDisk(Arg(args, "--base") ?? FreeDosLite,
            Arg(args, "--config") is string c ? File.ReadAllText(c) : DefaultConfig,
            Arg(args, "--autoexec") is string a ? File.ReadAllText(a) : DefaultAutoexec,
            Args(args, "--put").Select(p => { int i = p.IndexOf('='); return (p[..i], p[(i + 1)..]); }), Args(args, "--drop"));
        File.WriteAllBytes(outPath, img);
        Console.WriteLine($"x86disk: {outPath} ({img.Length / 1024 / 1024} MB)");
        return 0;
    }

    public static byte[] BuildDisk(string basePath, string config, string autoexec, IEnumerable<(string host, string dos)> puts, IEnumerable<string>? drop = null)
    {
        var img = File.ReadAllBytes(basePath);
        var fat = new FatImage(img);
        fat.WriteFile("FDCONFIG.SYS", Encoding.ASCII.GetBytes(config.Replace("\r\n", "\n").Replace("\n", "\r\n")));
        fat.WriteFile("FDAUTO.BAT", Encoding.ASCII.GetBytes(autoexec.Replace("\r\n", "\n").Replace("\n", "\r\n")));
        fat.Delete("SETUP.BAT");
        foreach (var d in drop ?? Array.Empty<string>()) fat.DeleteTree(d);
        foreach (var (host, dos) in puts) fat.WriteFile(dos, File.ReadAllBytes(host));
        return img;
    }

    public static int Boot(string[] args)
    {
        var m = new PcMachine();
        if (Arg(args, "--ips") is string ips) m.InstructionsPerSecond = long.Parse(ips);
        if (Arg(args, "--hdd") is string h) m.Hdd[0] = new DiskImage(File.ReadAllBytes(h), false);
        if (Arg(args, "--floppy") is string f) m.Floppy[0] = new DiskImage(File.ReadAllBytes(f), true);
        int frames = int.Parse(Arg(args, "--frames") ?? "3000");
        string? until = Arg(args, "--until"), type = Arg(args, "--type"), shot = Arg(args, "--shot");
        var sw = Stopwatch.StartNew();
        m.PowerOn();
        var shotAt = Args(args, "--shotat").Select(x => { int i = x.IndexOf('='); return (f: int.Parse(x[..i]), p: x[(i + 1)..]); }).OrderBy(x => x.f).ToList();
        int n = 0;
        foreach (var (sf, sp) in shotAt) { n += RunUntil(m, null, sf - n); X86Shots.SavePng(m.Vga, sp); }
        n += RunUntil(m, until, frames - n);
        bool found = until == null || Screen(m).Contains(until) || m.Teletype.ToString().Contains(until);
        Console.WriteLine($"x86boot: {n} frames ({n / 70.0:F1} s machine time) in {sw.Elapsed.TotalSeconds:F1} s, {(m.Cpu.Cycle / Math.Max(0.001, sw.Elapsed.TotalSeconds)) / 1e6:F1} M instr/s{(m.Stopped != null ? ", STOPPED: " + m.Stopped : "")}");
        if (until != null) Console.WriteLine(found ? $"x86boot: found \"{until}\"" : $"x86boot: \"{until}\" NOT found");
        if (type != null && found)
        {
            TypeText(m, type.Replace("\\n", "\n"));
            string? until2 = Arg(args, "--until2");
            int n2 = RunUntil(m, until2, int.Parse(Arg(args, "--frames2") ?? "1500"));
            if (until2 != null)
            {
                bool f2 = Screen(m).Contains(until2) || m.Teletype.ToString().Contains(until2);
                Console.WriteLine(f2 ? $"x86boot: after typing, found \"{until2}\" ({n2} frames)" : $"x86boot: after typing, \"{until2}\" NOT found ({n2} frames)");
                found &= f2;
            }
        }
        if (Arg(args, "--sample") is string smp)
            for (int k = 0; k < int.Parse(smp); k++)
            {
                m.RunClock(997);
                uint lin = m.Cpu.SegBase[1] + m.Cpu.Ip;
                var bytes = string.Join(" ", Enumerable.Range(0, 12).Select(i => m.Phys8(lin + (uint)i).ToString("X2")));
                Console.WriteLine($"  {m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8} lin {lin:X8} EAX {m.Cpu.R[0]:X8} ECX {m.Cpu.R[1]:X8} EDX {m.Cpu.R[2]:X8} ESP {m.Cpu.R[4]:X8} IF {(m.Cpu.Flags >> 9) & 1} | {bytes}");
            }
        if (args.Contains("--irqstat")) Console.WriteLine("irqs: " + string.Join(" ", Enumerable.Range(0, 256).Where(i => m.IrqCount[i] > 0).Select(i => $"{i:X2}h={m.IrqCount[i]}")) + $"; PIC imr {m.Pic.MasterImr:X2}/{m.Pic.SlaveImr:X2}; CR0 {m.Cpu.Cr0:X}; CS:EIP {m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8}; IF {(m.Cpu.Flags >> 9) & 1}; halt {m.Cpu.Halt}");
        Console.WriteLine("---- screen ----");
        Console.WriteLine(Screen(m).TrimEnd());
        Console.WriteLine("----------------");
        if (shot != null) X86Shots.SavePng(m.Vga, shot);
        if (Arg(args, "--save") is string save && m.Hdd[0] != null) File.WriteAllBytes(save, m.Hdd[0]!.Data);
        foreach (var g in Args(args, "--get"))
        {
            int i = g.IndexOf('=');
            var data = new FatImage(m.Hdd[0]!.Data).ReadFile(g[..i]);
            if (data == null) { Console.WriteLine($"x86boot: {g[..i]} not on the disk"); found = false; continue; }
            File.WriteAllBytes(g[(i + 1)..], data);
        }
        return found && m.Stopped == null ? 0 : 1;
    }

    public static int RunUntil(PcMachine m, string? until, int frames)
    {
        int n = 0;
        for (; n < frames; n++)
        {
            if (!m.RunFrame()) break;
            if (until != null && n % 10 == 0 && (Screen(m).Contains(until) || m.Teletype.ToString().Contains(until))) break;
        }
        return n;
    }

    /// <summary>The text on screen (text modes): 25 lines of 80.</summary>
    public static string Screen(PcMachine m)
    {
        var v = m.Vga;
        if ((v.Gr[6] & 1) != 0) return "(graphics mode)";
        int cols = v.Cr[1] + 1, start = v.Cr[0x0d] | (v.Cr[0x0c] << 8);
        var sb = new StringBuilder();
        for (int r = 0; r < 25; r++)
        {
            var line = new char[cols];
            for (int c = 0; c < cols; c++) { byte ch = v.Ram[((start + r * cols + c) & 0xffff) * 4]; line[c] = ch >= 32 && ch < 127 ? (char)ch : ' '; }
            sb.AppendLine(new string(line).TrimEnd());
        }
        return sb.ToString();
    }

    // set-1 make codes for typing ASCII
    private static readonly Dictionary<char, (byte code, bool shift)> Keys = BuildKeys();
    private static Dictionary<char, (byte, bool)> BuildKeys()
    {
        var d = new Dictionary<char, (byte, bool)>();
        const string normal = "\0\x1b" + "1234567890-=\b\t" + "qwertyuiop[]\n\0" + "asdfghjkl;'`\0\\" + "zxcvbnm,./";
        const string shifted = "\0\x1b" + "!@#$%^&*()_+\b\0" + "QWERTYUIOP{}\n\0" + "ASDFGHJKL:\"~\0|" + "ZXCVBNM<>?";
        for (int i = 1; i < normal.Length; i++) { if (normal[i] != '\0') d.TryAdd(normal[i], ((byte)i, false)); if (shifted[i] != '\0') d.TryAdd(shifted[i], ((byte)i, true)); }
        d[' '] = (0x39, false);
        return d;
    }

    /// <summary>Types text a few keys per frame (make then break codes), so DOS's line editor sees every key.</summary>
    public static void TypeText(PcMachine m, string text)
    {
        foreach (char ch in text)
        {
            if (!Keys.TryGetValue(ch, out var k)) continue;
            if (k.shift) m.Kbc.KeyBytes(0x2a);
            m.Kbc.KeyBytes(k.code, (byte)(k.code | 0x80));
            if (k.shift) m.Kbc.KeyBytes(0xaa);
            m.RunFrame(); m.RunFrame();
        }
    }

    private static string? Arg(string[] a, string k) { int i = Array.FindIndex(a, x => x.Equals(k, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    private static IEnumerable<string> Args(string[] a, string k) { for (int i = 0; i + 1 < a.Length; i++) if (a[i].Equals(k, StringComparison.OrdinalIgnoreCase)) yield return a[i + 1]; }
}
