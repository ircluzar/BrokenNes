using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86speed --work W [--cpu MODEL] [--video vga|s3] [--machine PRESET] [--runs N] [--scale N] [--frames N] [--jitstat] [--hot] [--emm]</c>:
/// the x86 PC's speed in percent of real time, per machine config, on a whole board (CPU + devices + picture), headless and unthrottled.
/// A FreeDOS disk runs the workload after "READY"; the clock starts there and stops at "ALLDONE". Real time is the machine's own clock
/// (instructions / InstructionsPerSecond of the CPU model), so 200% means a P166 runs twice as fast as a real P166 would.
/// Workloads: dhry (DHRY386 real-mode Dhrystone), bench16 / bench32 (tools/x86-bench BENCH16 / BENCH32 under DOS/32A), v13 (mode 13h pixel
/// loop, tools/x86-speed), vlfb (VBE 800x600x16 linear framebuffer pixel loop, needs --video s3), fpu (x87 matrix / divide / sqrt loop, tools/x86-speed), idle (the DOS prompt, HLT).
/// Every line of output is "x86speed: key=value ..." so scripts can parse it; tools/x86-speed/speed.ps1 runs the whole table.
/// </summary>
internal static class X86SpeedCli
{
    public const string WorkDir = @"C:\BrokenNes-work\x86_speed_work";

    public static int Run(string[] args)
    {
        string work = (Arg(args, "--work") ?? "dhry").ToLowerInvariant();
        var model = Arg(args, "--cpu") is string cs ? CpuModel.Parse(cs) ?? throw new ArgumentException($"no CPU model {cs}") : null;
        var video = (Arg(args, "--video") ?? "vga").ToLowerInvariant() switch { "s3" or "s3trio64" => PcVideo.S3Trio64, _ => PcVideo.Vga };
        var cfg = Arg(args, "--machine") is string mp ? PcConfig.Preset(mp) : PcConfig.Default;
        var m = cfg == PcConfig.Default ? new PcMachine(PcMachine.DefaultMemory, model, video) : new PcMachine(cfg, model, video);
        if (model == null) m.InstructionsPerSecond = PcSession.DefaultIps;   // the console's own default CPU speed
        if (Arg(args, "--ips") is string ips) m.InstructionsPerSecond = long.Parse(ips);
        long ipsv = m.InstructionsPerSecond;

        string tools = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools");
        string Find(string rel, params string[] alt)
        {
            foreach (var p in new[] { Path.Combine(tools, rel) }.Concat(alt)) if (File.Exists(p)) return Path.GetFullPath(p);
            throw new FileNotFoundException($"x86speed: workload file {rel} not found");
        }
        var puts = new List<(string host, string dos)>();
        string cmd;
        Directory.CreateDirectory(WorkDir);
        switch (work)
        {
            case "dhry":
            {
                long runs = Arg(args, "--runs") is string r ? long.Parse(r) : Math.Max(20000, ipsv / 600 * 6);
                string rf = Path.Combine(WorkDir, $"runs_{runs}.txt");
                File.WriteAllText(rf, runs + "\r\n");
                puts.Add((Find(@"x86-speed\DHRY386.EXE", @"C:\BrokenNes-tools\x86\tests\dhrystone\build\DHRY386.EXE"), @"\DHRY386.EXE"));
                puts.Add((rf, @"\RUNS.TXT"));
                cmd = @"DHRY386 < \RUNS.TXT > \OUT.TXT";
                break;
            }
            case "bench16":
            case "bench32":
            {
                int scale = Arg(args, "--scale") is string s ? int.Parse(s) : (int)Math.Max(1, ipsv / 15_000_000);
                string exe = work == "bench16" ? "BENCH16.EXE" : "BENCH32.EXE";
                puts.Add((Find(@"x86-speed\" + exe, @"C:\BrokenNes-work\x86_bench_out\t\" + exe), @"\" + exe));
                cmd = $@"{exe[..^4]} {scale} > \OUT.TXT";
                break;
            }
            case "v13": puts.Add((Find(@"x86-speed\V13.COM"), @"\V13.COM")); cmd = @"V13 > \OUT.TXT"; break;
            case "fpu": puts.Add((Find(@"x86-speed\FPU.COM"), @"\FPU.COM")); cmd = @"FPU > \OUT.TXT"; break;
            case "vlfb": puts.Add((Find(@"x86-speed\VLFB.COM"), @"\VLFB.COM")); cmd = @"VLFB > \OUT.TXT"; break;
            case "idle": cmd = "REM"; break;
            default: Console.WriteLine($"x86speed: no workload {work}"); return 1;
        }
        string auto = "@ECHO OFF\r\nSET DOSDIR=\\FREEDOS\r\nSET PATH=%DOSDIR%\\BIN\r\nECHO READY\r\n" + cmd + "\r\nECHO ALLDONE\r\n";
        // --emm: JEMMEX (XMS + EMS) loads first, so DOS runs in V86 mode under paging and a DOS extender goes through VCPI: the paged path
        string config = args.Contains("--emm") ? @"DEVICE=\FREEDOS\BIN\JEMMEX.EXE" + "\r\n" + X86BootCli.DefaultConfig : X86BootCli.DefaultConfig;
        m.Hdd[0] = new DiskImage(X86BootCli.BuildDisk(X86BootCli.FreeDosLite, config, auto, puts), false);

        m.Profiling = true;
        if (Arg(args, "--batch") is string bt) m.Batch = int.Parse(bt);
        var swBoot = Stopwatch.StartNew();
        m.PowerOn();
        int maxFrames = int.Parse(Arg(args, "--frames") ?? "200000");
        int n = X86BootCli.RunUntil(m, "READY", 3000);
        double bootWall = swBoot.Elapsed.TotalSeconds;
        m.Cpu.JitStatReset();
        long c0 = m.Clock, cy0 = m.Cpu.Cycle, r0 = m.RenderTicks;
        var hot = new Dictionary<string, int>();
        bool sampling = args.Contains("--hot");
        var sampler = sampling ? new System.Threading.Thread(() =>
        {
            while (sampling)
            {
                System.Threading.Thread.Sleep(1);
                string k = $"{m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip & ~0x3fu:X8} pe{m.Cpu.Cr0 & 1} pg{m.Cpu.Cr0 >> 31}";
                lock (hot) hot[k] = hot.GetValueOrDefault(k) + 1;
            }
        }) { IsBackground = true } : null;
        sampler?.Start();
        var sw = Stopwatch.StartNew();
        double cpu0 = ThreadCpuSeconds();
        int frames = 0;
        if (work == "idle") frames = X86BootCli.RunUntil(m, null, 700);
        else
            for (; frames < maxFrames; frames++)
            {
                if (!m.RunFrame()) break;
                if (frames % 10 == 0 && m.Teletype.ToString().Contains("ALLDONE")) break;
            }
        double wall = sw.Elapsed.TotalSeconds;
        double cpuT = ThreadCpuSeconds() - cpu0;   // the emulation thread's own CPU time: steadier than wall time on a shared PC
        sampling = false;
        double machine = (m.Clock - c0) / (double)ipsv;
        double busy = (m.Cpu.Cycle - cy0) / (double)Math.Max(1, m.Clock - c0);
        double render = (m.RenderTicks - r0) / (double)Stopwatch.Frequency;
        bool ok = work == "idle" || m.Teletype.ToString().Contains("ALLDONE");
        string? outTxt = null;
        try { var d = new FatImage(m.Hdd[0]!.Data).ReadFile(@"\OUT.TXT"); if (d != null) outTxt = System.Text.Encoding.ASCII.GetString(d); } catch { }
        string cpuName = model?.Id ?? "LEGACY";
        Console.WriteLine($"x86speed: cpu={cpuName} video={(video == PcVideo.S3Trio64 ? "S3" : "VGA")} machine={cfg.ToString().Replace(" ", "")} work={work} ips={ipsv} " +
            $"ok={(ok ? 1 : 0)} frames={frames} machine_s={machine:F2} wall_s={wall:F2} pct={100 * machine / Math.Max(1e-9, wall):F0} cpu_s={cpuT:F2} pct_cpu={100 * machine / Math.Max(1e-9, cpuT):F0} " +
            $"busy={100 * busy:F0} host_mips={(m.Cpu.Cycle - cy0) / Math.Max(1e-9, wall) / 1e6:F1} render_pct={100 * render / Math.Max(1e-9, wall):F0} boot_wall_s={bootWall:F1}{(m.Stopped != null ? " STOPPED=" + m.Stopped.Replace(' ', '_') : "")}");
        if (sampler != null) foreach (var kv in hot.OrderByDescending(x => x.Value).Take(20)) Console.WriteLine($"  hot {kv.Key} {kv.Value}");
        if (args.Contains("--jitstat"))
        {
            Console.WriteLine($"jit: compiled {m.Cpu.JitCompiled} reused {m.Cpu.JitReused} runs {m.Cpu.JitRuns} invalid {m.Cpu.JitInvalid} compiled-instrs {100.0 * m.Cpu.JitInstrsTotal / Math.Max(1, m.Cpu.Cycle - cy0):F1}% of the workload");
            Console.WriteLine($"jit: declined at entry {m.Cpu.JitDeclines} (unusable segment: {string.Join(" ", m.Cpu.JitDeclineSeg)}); segs " +
                string.Join(" ", Enumerable.Range(0, 6).Select(i => $"{i}:{m.Cpu.SegSel[i]:X4}/{m.Cpu.SegLimit[i]:X}/{m.Cpu.SegFlags[i]:X}")));
            foreach (var l in m.Cpu.JitTop(15)) Console.WriteLine("  blk " + l);
            foreach (var l in m.Cpu.InterpTop(30)) Console.WriteLine("  interp " + l);
            foreach (var l in m.Cpu.OutOfLineTop(30)) Console.WriteLine("  ool " + l);
        }
        if (args.Contains("--out") && outTxt != null) Console.WriteLine(outTxt.TrimEnd());
        return ok && m.Stopped == null ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    private static double ThreadCpuSeconds() => OperatingSystem.IsWindows() && GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u) ? (k + u) / 1e7 : 0;

    private static string? Arg(string[] a, string k) { int i = Array.FindIndex(a, x => x.Equals(k, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
