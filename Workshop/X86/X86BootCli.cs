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
        if (Arg(args, "--list") is string dir)   // --x86disk --list DOSDIR [--base img]: what a disk holds
        {
            var fi = new FatImage(File.ReadAllBytes(Arg(args, "--base") ?? FreeDosLite));
            foreach (var n in fi.List(dir)) Console.WriteLine(n);
            return 0;
        }
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

    public static byte[] BuildDisk(string basePath, string config, string autoexec, IEnumerable<(string host, string dos)> puts, IEnumerable<string>? drop = null) =>
        PcDisks.BuildDisk(File.ReadAllBytes(basePath), config, autoexec, puts.Select(p => (File.ReadAllBytes(p.host), p.dos)), drop);

    /// <summary>--allocs: the runtime's allocation ticks (one per ~100 KB allocated) by type, printed at the end: where the GC pressure comes from.</summary>
    private sealed class AllocListener : System.Diagnostics.Tracing.EventListener
    {
        public readonly Dictionary<string, long> ByType = new();
        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource s)
        { if (s.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(s, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1); }
        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick") || e.PayloadNames == null) return;
            int i = e.PayloadNames.IndexOf("TypeName"); int a = e.PayloadNames.IndexOf("AllocationAmount64");
            string t = i >= 0 ? e.Payload![i]?.ToString() ?? "?" : "?"; long n = a >= 0 ? Convert.ToInt64(e.Payload![a]) : 100_000;
            lock (ByType) ByType[t] = ByType.GetValueOrDefault(t) + n;
        }
    }
    private static AllocListener? _allocs;

    public static int Boot(string[] args)
    {
        if (args.Contains("--allocs")) _allocs = new AllocListener();
        var cfg = Arg(args, "--machine") is string mp ? PcConfig.Preset(mp) : PcConfig.Default;
        if (Arg(args, "--mem") is string mem) cfg = cfg with { MemoryMB = int.Parse(mem) };
        if (Arg(args, "--bootorder") is string bo) cfg = cfg with { BootOrder = bo.ToUpperInvariant() };
        // --machine names a whole machine (the desktop's presets): its CPU model and video card, unless --cpu / --video say otherwise
        var preset = Arg(args, "--machine") is string mk && PcMachinePreset.Known(mk) ? PcMachinePreset.Find(mk) : null;
        var model = Arg(args, "--cpu") is string cs ? CpuModel.Parse(cs) ?? throw new ArgumentException($"no CPU model {cs}") : preset?.Cpu;
        var video = Arg(args, "--video") is string vs ? (vs.ToLowerInvariant() is "s3" or "s3trio64" ? PcVideo.S3Trio64 : PcVideo.Vga) : preset?.Video ?? PcVideo.Vga;
        var m = cfg == PcConfig.Default ? new PcMachine(PcMachine.DefaultMemory, model, video) : new PcMachine(cfg, model, video);
        if (Arg(args, "--ips") is string ips) m.InstructionsPerSecond = long.Parse(ips);
        if (Arg(args, "--wav") != null) _wav = new System.IO.MemoryStream();
        if (Arg(args, "--cdwav") != null) _cdWav = new System.IO.MemoryStream();
        if (Arg(args, "--apu") is string apu && !PcSound.Apply(m, apu)) { Console.WriteLine($"x86boot: no sound card {apu}"); return 1; }
        if (Arg(args, "--hdd") is string h) m.Hdd[0] = new DiskImage(File.ReadAllBytes(h), false);
        if (Arg(args, "--hdd2") is string h2) m.Hdd[1] = new DiskImage(File.ReadAllBytes(h2), false);
        if (Arg(args, "--cd") is string cdp) { m.Cd = CdImage.Open(cdp); Console.WriteLine(m.Cd); }
        if (Arg(args, "--floppy") is string f) m.Floppy[0] = new DiskImage(File.ReadAllBytes(f), true);
        int frames = int.Parse(Arg(args, "--frames") ?? "3000");
        string? until = Arg(args, "--until"), type = Arg(args, "--type"), shot = Arg(args, "--shot");
        // --mac: the card's PROM address (a cold boot reads it; a loaded state keeps the address saved in it)
        if (Arg(args, "--mac") is string macs && m.Nic != null) { m.Nic.Mac = PcMac.Parse(macs); m.Nic.Reset(); }
        var sw = Stopwatch.StartNew();
        m.PowerOn();
        // --loadstate: carry on from a --savestate of the same machine (its disks come from the state; the CD stays the one given)
        if (Arg(args, "--loadstate") is string ls) m.LoadState(File.ReadAllBytes(ls));
        // --net: plug the network card into the built-in NAT (DHCP, DNS, the HTTPS bridge); off by default
        PcNat? nat = null;
        if (Arg(args, "--niclog") is string nl && m.Nic != null)
        {
            var nw = new StreamWriter(nl); int nn = 0;
            m.Nic.Trace = (o, w, v, wd) => { if (nn++ < 200000) nw.WriteLine($"{m.Clock} {(w ? "OUT" : "IN ")} {o:X2} {(wd ? v.ToString("X4") : v.ToString("X2"))}"); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => nw.Flush();
        }
        if (args.Contains("--net") && m.Nic != null) { nat = new PcNat { Log = l => Console.WriteLine("net: " + l) }; m.Nic.Link = nat; }
        // X86-02 LAN: --mac N|AA:BB:.. (the card's PROM address: give each PC its own, then cold boot), --lan host:PORT | join:HOST:PORT
        PcLanUdp? lanLink = null;
        if (Arg(args, "--lan") is string lans && m.Nic != null)
        {
            lanLink = PcLanUdp.FromSpec(lans); Console.WriteLine("lan: " + lanLink.Description);
            if (nat != null) nat.LanShared = true;
            m.Nic.Link = m.Nic.Link != null ? new PcLinkPair(lanLink, m.Nic.Link) : lanLink;
        }
        // --pair STATE|cold [--pair-mac N] [--pair-shot png]: a second PC in this same process (same machine, same --hdd base), its card and this one's on an
        // in-process hub (each keeps its own NAT if --net): the X86-02 LAN proof on one heavy slot, both PCs advancing frame by frame together
        if (Arg(args, "--pair") is string pairSpec && m.Nic != null)
        {
            var m2 = cfg == PcConfig.Default ? new PcMachine(PcMachine.DefaultMemory, model, video) : new PcMachine(cfg, model, video);
            m2.InstructionsPerSecond = m.InstructionsPerSecond;
            if (Arg(args, "--hdd") is string ph) m2.Hdd[0] = new DiskImage(File.ReadAllBytes(ph), false);
            if (Arg(args, "--pair-mac") is string pm) { m2.Nic!.Mac = PcMac.Parse(pm); m2.Nic.Reset(); }
            m2.PowerOn();
            if (!pairSpec.Equals("cold", StringComparison.OrdinalIgnoreCase)) m2.LoadState(File.ReadAllBytes(pairSpec));
            var hub = new PcLanHub();
            INetLink Cable(INetLink port, PcNat? own) { if (own != null) own.LanShared = true; return own != null ? new PcLinkPair(port, own) : port; }
            m.Nic.Link = Cable(hub.Connect(), nat);
            _pairNat = args.Contains("--net") ? new PcNat { Log = l => Console.WriteLine("net2: " + l) } : null;
            m2.Nic!.Link = Cable(hub.Connect(), _pairNat);
            _partner = m2; _pairHub = hub;
            Console.WriteLine($"pair: second PC {(pairSpec == "cold" ? "cold boots" : "from " + pairSpec)}, both on an in-process hub");
        }
        // --exclog N: the last N CPU exceptions other than page faults (printed at the end), with a count per vector
        var excRing = new Queue<string>(); var excCount = new long[32]; int excKeep = int.Parse(Arg(args, "--exclog") ?? "0");
        string biosIn = "";
        // --portlog: I/O accesses per port and direction (printed at the end, the busiest first)
        var portCount = new long[0x20000];
        if (args.Contains("--portlog")) m.PortLog = (p, w) => portCount[(p & 0xffff) | (w ? 0x10000 : 0)]++;
        if (Arg(args, "--portseq") is string ps)   // --portseq LO-HI[:N]: the first N (default 600) byte accesses to ports LO..HI, with values, in order (consecutive repeats folded)
        {
            var pp = ps.Split(':'); var lh = pp[0].Split('-'); int lo = Convert.ToInt32(lh[0], 16), hi = Convert.ToInt32(lh[^1], 16), max = pp.Length > 1 ? int.Parse(pp[1]) : 600, seqN = 0;
            string last = ""; int rep = 0;
            m.PortTrace = (p, w, v) =>
            {
                if (p < lo || p > hi || seqN >= max) return;
                string k = $"{(w ? "out" : "in ")} {p:X3} {v:X2}";
                if (k == last) { rep++; return; }
                if (rep > 0) Console.WriteLine($"portseq   (x{rep + 1})");
                rep = 0; last = k; seqN++;
                Console.WriteLine($"portseq {k} @{m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8}");
            };
        }
        var portWho = new Dictionary<string, long>(); int whoPort = Arg(args, "--portwho") is string pw ? Convert.ToInt32(pw, 16) : -1;   // --portwho HEXPORT: where the accesses come from
        if (whoPort >= 0) { var prev = m.PortLog; m.PortLog = (p, w) => { prev?.Invoke(p, w); if ((p & 0xffff) == whoPort) { string k = $"{m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8}"; portWho[k] = portWho.GetValueOrDefault(k) + 1; } }; }
        if (excKeep > 0 && args.Contains("--bioslog"))   // BIOS services called from V86 mode (under Windows), with the registers before and after
            m.BiosLog = (n, before) =>
            {
                if ((m.Cpu.Cr0 & 1) == 0 || n is 0x108 or 0x109) return;
                var c = m.Cpu.R;
                string regs = $"AX {c[0] & 0xffff:X4} BX {c[3] & 0xffff:X4} CX {c[1] & 0xffff:X4} DX {c[2] & 0xffff:X4} ES {m.Cpu.SegSel[0]:X4} DI {c[7] & 0xffff:X4}";
                if (before) { biosIn = regs; return; }
                excRing.Enqueue($"BIOS {n:X3} in [{biosIn}] out [{regs}] cyc {m.Cpu.Cycle}");
                if (excRing.Count > excKeep) excRing.Dequeue();
            };
        if (excKeep > 0)
            m.Cpu.ExceptionLog = (v, e) =>
            {
                excCount[v & 31]++;
                if (v == 14 || (((m.Cpu.Flags >> 17) & 1) != 0 && v is 6 or 13 && !(v == 13 && m.Cpu.SegSel[1] == 0xf000 && m.Cpu.Ip < 0xe800))) return;   // (V86 monitor traps: the usual traffic)
                excRing.Enqueue($"#{v} err {e:X} at {m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8} CR0 {m.Cpu.Cr0:X} VM {(m.Cpu.Flags >> 17) & 1} EAX {m.Cpu.R[0]:X8} ESI {m.Cpu.R[6]:X8} EDI {m.Cpu.R[7]:X8} EBX {m.Cpu.R[3]:X8} DS {m.Cpu.SegSel[3]:X4}/{m.Cpu.SegBase[3]:X}/{m.Cpu.SegLimit[3]:X} ES {m.Cpu.SegSel[0]:X4}/{m.Cpu.SegLimit[0]:X} CSbase {m.Cpu.SegBase[1]:X} cyc {m.Cpu.Cycle} | " + LinBytes(m, m.Cpu.SegBase[1] + m.Cpu.Ip, 10));
                if (excRing.Count > excKeep) excRing.Dequeue();
            };
        if (excKeep > 0)
            m.Cpu.Cr0Log = (o, nv) =>
            {
                if (((o ^ nv) & 0x80000001) == 0) return;
                var c = m.Cpu.R;
                if ((o & 0x80000000) != 0 && (nv & 0x80000000) == 0)   // paging going off: what the registers point at, through the page tables
                {
                    uint Lin(uint la) { uint pde = m.Phys32((m.Cpu.Cr3 & ~0xfffu) + (la >> 22) * 4); if ((pde & 1) == 0) return 0xffffffff; if ((pde & 0x80) != 0) return (pde & 0xffc00000) | (la & 0x3fffff); uint pte = m.Phys32((pde & ~0xfffu) + ((la >> 12) & 0x3ff) * 4); return (pte & 1) == 0 ? 0xffffffff : (pte & ~0xfffu) | (la & 0xfff); }
                    foreach (var (nm, la) in new[] { ("EAX", c[0]), ("EBX", c[3]), ("ECX", c[1]), ("EDX", c[2]), ("ESI", c[6]), ("EDI", c[7]), ("EBP", c[5]) })
                    {
                        var sb = new StringBuilder();
                        for (uint k = 0; k < 96; k++) { uint p = Lin(la + k); if (p == 0xffffffff) { sb.Append('?'); break; } byte b = m.Phys8(p); sb.Append(b is >= 32 and < 127 ? (char)b : '.'); }
                        excRing.Enqueue($"   [{nm} {la:X8}] {sb}");
                    }
                    // --memgrep TEXT (repeatable): where physical memory holds it, with the text around it
                    foreach (var pat in Args(args, "--memgrep"))
                    {
                        var pb = Encoding.ASCII.GetBytes(pat); var mem = m.Cpu.Mem; int hits = 0;
                        for (int a = 0; a + pb.Length < mem.Length && hits < 12; a++)
                        {
                            if (mem[a] != pb[0] || !mem.AsSpan(a, pb.Length).SequenceEqual(pb)) continue;
                            hits++;
                            var sb = new StringBuilder();
                            for (int k = Math.Max(0, a - 64); k < Math.Min(mem.Length, a + 160); k++) sb.Append(mem[k] is >= 32 and < 127 ? (char)mem[k] : '.');
                            excRing.Enqueue($"   grep {pat} @{a:X}: {sb}");
                        }
                    }
                    // the stack: 24 dwords
                    excRing.Enqueue("   stack " + string.Join(" ", Enumerable.Range(0, 24).Select(k => { uint p = Lin(m.Cpu.SegBase[2] + c[4] + (uint)k * 4); return p == 0xffffffff ? "?" : m.Phys32(p).ToString("X8"); })));
                }
                excRing.Enqueue($"CR0 {o:X}->{nv:X} at {m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8} EAX {c[0]:X8} EBX {c[3]:X8} ECX {c[1]:X8} EDX {c[2]:X8} ESI {c[6]:X8} EDI {c[7]:X8} EBP {c[5]:X8} ESP {c[4]:X8} cyc {m.Cpu.Cycle}");
                if (excRing.Count > excKeep) excRing.Dequeue();
            };
        // --poke "HEXADDR=B1 B2 ...": patch physical memory (e.g. a state's copy of the BIOS ROM after the ROM changed)
        foreach (var pk in Args(args, "--poke"))
        {
            int i = pk.IndexOf('='); uint a = Convert.ToUInt32(pk[..i], 16);
            foreach (var b in pk[(i + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)) m.PhysW8(a++, Convert.ToByte(b, 16));
        }
        var shotAt = Args(args, "--shotat").Select(x => { int i = x.IndexOf('='); return (f: int.Parse(x[..i]), p: x[(i + 1)..]); }).OrderBy(x => x.f).ToList();
        int n = 0;
        foreach (var (sf, sp) in shotAt) { n += RunUntil(m, null, sf - n); X86Shots.SavePng(m.Vga, sp); Console.WriteLine($"x86boot: frame {n} at {sw.Elapsed.TotalSeconds:F1} s, cycle {m.Cpu.Cycle}"); }
        var hot = new Dictionary<string, int>();
        bool sampling = args.Contains("--hot");
        var sampler = sampling ? new System.Threading.Thread(() =>
        {
            while (sampling)
            {
                System.Threading.Thread.Sleep(1);
                string k = $"{m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip & ~0x3fu:X8} vm{(m.Cpu.Flags >> 17) & 1} pe{m.Cpu.Cr0 & 1}";
                lock (hot) hot[k] = hot.GetValueOrDefault(k) + 1;
            }
        }) { IsBackground = true } : null;
        sampler?.Start();
        if (Arg(args, "--watchlin") is string wl)   // --watchlin HEXLIN: run --frames frames; once the page is present, in small slices, printing each change of the dword there
        {
            uint wa = Convert.ToUInt32(wl, 16); int slice = int.Parse(Arg(args, "--watchslice") ?? "40"); uint last = 0; bool have = false; int changes = 0;
            long target = m.Cpu.Cycle; 
            for (; n < frames && changes < 400; n++)
            {
                uint p0 = LinPhys(m, wa);
                if (!have && (p0 == 0xffffffff || (m.Cpu.Cr0 & 0x80000000) == 0)) { if (!Frame(m)) break; continue; }
                // the frame in slices
                long end = m.Clock + m.InstructionsPerSecond / 70;
                while (m.Clock < end && m.Stopped == null)
                {
                    m.RunClock(slice);
                    uint p = (m.Cpu.Cr0 & 0x80000000) != 0 ? LinPhys(m, wa) : 0xffffffff;
                    if (p == 0xffffffff) continue;
                    uint v = m.Phys32(p);
                    if (!have || v != last) { Console.WriteLine($"watch {wa:X8} (phys {p:X8}) = {v:X8} (was {last:X8}) at {m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8} frame {n} cyc {m.Cpu.Cycle}"); last = v; have = true; changes++; }
                }
            }
        }
        n += RunUntil(m, until, frames - n);
        sampling = false;
        if (sampler != null) foreach (var kv in hot.OrderByDescending(x => x.Value).Take(30)) Console.WriteLine($"  hot {kv.Key} {kv.Value}");
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
        // --step "UNTIL=>KEYS" (repeatable, after the rest): run until the screen shows UNTIL (at most --stepframes, default 20000), type KEYS
        int stepMax = int.Parse(Arg(args, "--stepframes") ?? "20000"), si = 0;
        foreach (var st in Args(args, "--step"))
        {
            int i = st.IndexOf("=>"); string u = st[..i], keys = st[(i + 2)..].Replace("\\n", "\n").Replace("\\e", "\x1b").Replace("\\t", "\t")
                .Replace("\\u", "\x01").Replace("\\d", "\x02").Replace("\\l", "\x03").Replace("\\r", "\x04").Replace("\\w", "\x05");
            // "@N=>KEYS": graphics screens have no text to match, so wait N frames then type
            bool wait = u.StartsWith('@') && int.TryParse(u[1..], out _);
            int ns = wait ? RunUntil(m, null, int.Parse(u[1..])) : RunUntil(m, u, stepMax, true); n += ns;   // (the screen only: the teletype log keeps earlier screens' text)
            bool ok = wait || Screen(m).Contains(u);
            Console.WriteLine($"x86boot: step {si} \"{u}\" {(ok ? "found" : "NOT found")} after {ns} frames (total {n}, {sw.Elapsed.TotalSeconds:F0} s, heap {GC.GetTotalMemory(false) >> 20} MB){(m.Stopped != null ? ", STOPPED: " + m.Stopped : "")}");
            if (shot != null) X86Shots.SavePng(m.Vga, Path.ChangeExtension(shot, null) + $"_step{si}.png");
            si++;
            if (!ok) break;
            n += RunUntil(m, null, int.Parse(Arg(args, "--stepdelay") ?? "0"));   // let a menu settle before typing
            TypeText(m, keys);
            n += RunUntil(m, null, 35);   // let the keys land before looking for the next screen
        }
        if (Arg(args, "--after") is string aft) { n += RunUntil(m, null, int.Parse(aft)); }
        // --tracewhen "VEC:SEL" (with --trace N): run on in small clock slices until exception VEC is raised with CS = SEL, then trace from the handler
        if (Arg(args, "--tracewhen") is string tw)
        {
            var tp = tw.Split(':'); int tv = Convert.ToInt32(tp[0]); uint tsel = Convert.ToUInt32(tp[1], 16); bool hit = false;
            var prevLog = m.Cpu.ExceptionLog;
            m.Cpu.ExceptionLog = (v, e) => { prevLog?.Invoke(v, e); if (v == tv && m.Cpu.SegSel[1] == tsel) { if (!hit) Console.WriteLine($"tracewhen: #{v} err {e:X} at {m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8} EAX {m.Cpu.R[0]:X8} EDX {m.Cpu.R[2]:X8} | {LinBytes(m, m.Cpu.SegBase[1] + m.Cpu.Ip, 8)}"); hit = true; } };
            long slices = 0;
            while (!hit && slices++ < 20_000_000) m.RunClock(20);
            m.Cpu.ExceptionLog = prevLog;
            Console.WriteLine($"tracewhen: {(hit ? "hit" : "not hit")} after {slices} slices");
        }
        // --traceat HEXLO-HEXHI (with --trace N): run on in 1-instruction clock slices until the linear CS:EIP is inside the range, then trace
        if (Arg(args, "--traceat") is string ta)
        {
            var tp = ta.Split('-'); uint lo = Convert.ToUInt32(tp[0], 16), hi = Convert.ToUInt32(tp[1], 16); long sl = 0; int tslice = int.Parse(Arg(args, "--traceslice") ?? "1");
            while (sl++ < 400_000_000) { uint l = m.Cpu.SegBase[1] + m.Cpu.Ip; if (l >= lo && l < hi) break; m.RunClock(tslice); }
            Console.WriteLine($"traceat: at {m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8} after {sl} slices");
        }
        if (Arg(args, "--trace") is string trc)   // one instruction at a time: where a hang loops
        {
            Console.WriteLine("  IVT " + string.Join(" ", Enumerable.Range(0, 32).Select(i => $"{i:X2}={m.Phys16((uint)i * 4 + 2):X4}:{m.Phys16((uint)i * 4):X4}")) + $" | 40:67 {m.Phys32(0x467):X8} CMOS0F {m.Cmos.Ram[0x0f]:X2} 40:72 {m.Phys16(0x472):X4}");
        }
        if (Arg(args, "--trace") is string trc2)
            for (int k = 0; k < int.Parse(trc2); k++)
            {
                uint lin = m.Cpu.SegBase[1] + m.Cpu.Ip, sp = m.Cpu.SegBase[2] + (m.Cpu.R[4] & 0xffff);
                var bytes = LinBytes(m, lin, 8);
                var stk = string.Join(" ", Enumerable.Range(0, 6).Select(i => m.Phys16(sp + (uint)i * 2).ToString("X4")));
                Console.WriteLine($"  {m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8} SS {m.Cpu.SegSel[2]:X4} ESP {m.Cpu.R[4]:X8} EAX {m.Cpu.R[0]:X8} EBX {m.Cpu.R[3]:X8} ECX {m.Cpu.R[1]:X8} EDX {m.Cpu.R[2]:X8} ESI {m.Cpu.R[6]:X8} EDI {m.Cpu.R[7]:X8} DS {m.Cpu.SegSel[3]:X4} ES {m.Cpu.SegSel[0]:X4} FL {m.Cpu.Flags:X} | {bytes} | stk {stk}");
                m.Cpu.Step(1);
            }
        foreach (var kv in portWho.OrderByDescending(x => x.Value).Take(20)) Console.WriteLine($"portwho {kv.Key} {kv.Value}");
        if (args.Contains("--portlog"))
            Console.WriteLine("ports: " + string.Join(" ", Enumerable.Range(0, 0x20000).Where(i => portCount[i] > 0).OrderByDescending(i => portCount[i]).Take(40)
                .Select(i => $"{((i & 0x10000) != 0 ? "out" : "in")}{i & 0xffff:X}={portCount[i]}")));
        if (args.Contains("--vgadump"))   // why a picture is blank: the registers, the palette and how much video memory is non-zero
        {
            var v = m.Vga;
            string H(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));
            Console.WriteLine($"vga SR {H(v.Sr)}\nvga GR {H(v.Gr[..9])}\nvga AR {H(v.Ar)}\nvga CR {H(v.Cr[..0x19])}\nvga DAC0-15 {H(v.Palette[..48])}");
            Console.WriteLine($"vga ram non-zero bytes {v.Ram.Count(b => b != 0)} of {v.Ram.Length}; S3 {(m.S3 != null ? "yes" : "no")}; CR30-6F {H(v.Cr[0x30..0x70])}");
            { int cb = ((v.Cr[0x4c] & 0x0f) << 8 | v.Cr[0x4d]) * 1024; for (int l = 0; l < 4; l++) Console.WriteLine($"vga cursor@{cb:X} line {l}: {H(v.Ram[(cb + l * 16)..(cb + l * 16 + 16)])}"); }
            { var nz = new List<string>(); int run = -1; for (int k = 0x4b000; k < v.Ram.Length; k += 256) { bool any = false; for (int q = 0; q < 256; q++) if (v.Ram[k + q] != 0) { any = true; break; } if (any && run < 0) run = k; if (!any && run >= 0) { nz.Add($"{run:X}-{k:X}"); run = -1; } } Console.WriteLine("vga offscreen non-zero: " + string.Join(" ", nz.Take(30))); }
            Console.WriteLine($"system RAM A0000-BFFFF non-zero bytes {m.Cpu.Mem.AsSpan(0xa0000, 0x20000).ToArray().Count(b => b != 0)} (should stay 0: the VGA owns it)");
        }
        if (Arg(args, "--sample") is string smp)
            for (int k = 0; k < int.Parse(smp); k++)
            {
                m.RunClock(997);
                uint lin = m.Cpu.SegBase[1] + m.Cpu.Ip;
                var bytes = LinBytes(m, lin, 12);
                Console.WriteLine($"  {m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8} lin {lin:X8} EAX {m.Cpu.R[0]:X8} ECX {m.Cpu.R[1]:X8} EDX {m.Cpu.R[2]:X8} ESP {m.Cpu.R[4]:X8} IF {(m.Cpu.Flags >> 9) & 1} | {bytes}");
            }
        foreach (var pg in Args(args, "--physgrep"))   // --physgrep "B1 B2 ..": where physical memory holds these bytes (with 24 bytes before and after)
        {
            var pb = pg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray(); var pmem = m.Cpu.Mem; int hits = 0;
            for (int a = 0; a + pb.Length < pmem.Length && hits < 200; a++)
            {
                if (pmem[a] != pb[0] || !pmem.AsSpan(a, pb.Length).SequenceEqual(pb)) continue;
                hits++;
                Console.WriteLine($"physgrep @{a:X8}: {string.Join(" ", pmem.AsSpan(Math.Max(0, a - 24), 24 + pb.Length + 24).ToArray().Select(b => b.ToString("X2")))}");
            }
        }
        foreach (var va in Args(args, "--vramdump")) { var pp = va.Split(':'); int a0 = Convert.ToInt32(pp[0], 16), ln = int.Parse(pp[1]); for (int k = 0; k < ln; k += 32) Console.WriteLine($"vram {a0 + k:X6}: " + string.Join(" ", m.Vga.Ram.Skip(a0 + k).Take(32).Select(b => b.ToString("X2")))); }
        foreach (var da in Args(args, "--dump"))   // --dump HEXLIN:LEN: bytes at a linear address (through the page tables)
        {
            var parts = da.Split(':'); uint a0 = Convert.ToUInt32(parts[0], 16); int len = parts.Length > 1 ? int.Parse(parts[1]) : 64;
            for (int k = 0; k < len; k += 16) Console.WriteLine($"dump {a0 + (uint)k:X8}: {LinBytes(m, a0 + (uint)k, Math.Min(16, len - k))}");
        }
        foreach (var la in Args(args, "--linmap"))   // --linmap HEXLIN[:COUNTPAGES]: where linear pages land (paging on)
        {
            var parts = la.Split(':'); uint a0 = Convert.ToUInt32(parts[0], 16); int np = parts.Length > 1 ? int.Parse(parts[1]) : 1;
            Console.WriteLine($"linmap {a0:X8}: " + string.Join(" ", Enumerable.Range(0, np).Select(k => { uint p = LinPhys(m, a0 + (uint)k * 4096); return p == 0xffffffff ? "--" : (p >> 12).ToString("X"); })) + $" | {LinBytes(m, a0, 16)}");
        }
        if (args.Contains("--frametimes")) Console.WriteLine(FrameTimeReport());
        if (_allocs != null) lock (_allocs.ByType) foreach (var kv in _allocs.ByType.OrderByDescending(x => x.Value).Take(15)) Console.WriteLine($"  alloc {kv.Value / 1048576,6} MB {kv.Key}");
        if (lanLink != null) Console.WriteLine($"lan: sent {lanLink.Sent} (IPX {lanLink.SentIpx}), received {lanLink.Received} (IPX {lanLink.ReceivedIpx}){(lanLink.Members > 0 ? $", members {lanLink.Members}" : "")}");
        if (m.Ata != null) Console.WriteLine($"ide: bus-master DMA transfers {m.Ata.Primary.DmaTransfers + m.Ata.Secondary.DmaTransfers}, {(m.Ata.Primary.DmaBytes + m.Ata.Secondary.DmaBytes) / 1024} KB");
        if (m.Nic != null) Console.WriteLine($"nic: sent {m.Nic.FramesSent} frames, received {m.Nic.FramesReceived}; BAR0 {m.Pci!.NicFn!.Read32(0x10):X8} cmd {m.Pci.NicFn.Cfg[4]:X2} line {m.Pci.NicFn.Cfg[0x3c]} PIRQB {m.Pci.Isa.Cfg[0x61]:X2}");
        if (args.Contains("--jitstat")) Console.WriteLine($"jit: compiled {m.Cpu.JitCompiled} reused {m.Cpu.JitReused} runs {m.Cpu.JitRuns} invalid {m.Cpu.JitInvalid} cycle {m.Cpu.Cycle} compiled-instrs {m.Cpu.JitInstrsTotal} ({100.0 * m.Cpu.JitInstrsTotal / Math.Max(1, m.Cpu.Cycle):F1}%) (set X86_JITSTAT for the per-block counts)");
        if (args.Contains("--jitstat")) foreach (var l in m.Cpu.JitTop(25)) Console.WriteLine("  blk " + l);
        if (args.Contains("--jitstat")) foreach (var l in m.Cpu.InterpTop(40)) Console.WriteLine("  interp " + l);
        if (args.Contains("--jitstat")) foreach (var kv in m.Cpu.OolAt.OrderByDescending(x => x.Value).Take(40)) Console.WriteLine($"  oolat {kv.Key:X8} {kv.Value} : {string.Join(" ", Enumerable.Range(0, 8).Select(i => m.Phys8(kv.Key + (uint)i).ToString("X2")))}");
        if (args.Contains("--irqstat")) Console.WriteLine($"batches {m.Batches} halted {m.HaltBatches} cycle {m.Cpu.Cycle} idle-clock {m.Clock - m.Cpu.Cycle}");
        if (args.Contains("--irqstat")) Console.WriteLine("irqs: " + string.Join(" ", Enumerable.Range(0, 256).Where(i => m.IrqCount[i] > 0).Select(i => $"{i:X2}h={m.IrqCount[i]}")) + $"; PIC imr {m.Pic.MasterImr:X2}/{m.Pic.SlaveImr:X2}; CR0 {m.Cpu.Cr0:X}; CS:EIP {m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8}; IF {(m.Cpu.Flags >> 9) & 1}; halt {m.Cpu.Halt}");
        Console.WriteLine("---- screen ----");
        Console.WriteLine(Screen(m).TrimEnd());
        Console.WriteLine("----------------");
        if (shot != null) X86Shots.SavePng(m.Vga, shot);
        if (_partner != null)
        {
            if (Arg(args, "--pair-shot") is string ps2) X86Shots.SavePng(_partner.Vga, ps2);
            Console.WriteLine($"pair: hub frames {_pairHub!.Frames} (IPX {_pairHub.IpxFrames}, IP {_pairHub.IpFrames}); PC 1 nic sent {m.Nic!.FramesSent} received {m.Nic.FramesReceived}; PC 2 nic sent {_partner.Nic!.FramesSent} received {_partner.Nic.FramesReceived}");
            if (Arg(args, "--pair-save") is string pss) File.WriteAllBytes(pss, _partner.SaveState());
        }
        if (Arg(args, "--wav") is string wavp) WriteWav(wavp);
        if (Arg(args, "--cdwav") is string cdwp) WriteWav(cdwp, _cdWav!.ToArray());
        if (Arg(args, "--savestate") is string ss) File.WriteAllBytes(ss, m.SaveState());
        if (excKeep > 0)
        {
            Console.WriteLine("exceptions: " + string.Join(" ", Enumerable.Range(0, 32).Where(i => excCount[i] > 0).Select(i => $"#{i}={excCount[i]}")));
            foreach (var l in excRing) Console.WriteLine("  " + l);
        }
        if (Arg(args, "--save") is string save && m.Hdd[0] != null) File.WriteAllBytes(save, m.Hdd[0]!.ToArray());
        foreach (var g in Args(args, "--get"))
        {
            int i = g.IndexOf('=');
            var data = new FatImage(m.Hdd[0]!.ToArray()).ReadFile(g[..i]);
            if (data == null) { Console.WriteLine($"x86boot: {g[..i]} not on the disk"); found = false; continue; }
            File.WriteAllBytes(g[(i + 1)..], data);
        }
        return found && m.Stopped == null ? 0 : 1;
    }

    /// <summary>--wav FILE: the sound card's output (44.1 kHz stereo 16-bit) is collected after every frame and written at the end.</summary>
    private static System.IO.MemoryStream? _wav;
    /// <summary>--cdwav FILE: the CD drive's audio output (CD-DA, 44.1 kHz stereo), collected after every frame.</summary>
    private static System.IO.MemoryStream? _cdWav; private static long _cdLast = -1;
    private static readonly short[] _wavBuf = new short[44100];
    /// <summary>Wall-clock time of every frame run (X86-02 speed climb: the worst 1% matters, not only the average); printed at the end.</summary>
    private static readonly List<double> _frameMs = new();
    private static readonly System.Diagnostics.Stopwatch _frameSw = new();
    public static string FrameTimeReport()
    {
        if (_frameMs.Count == 0) return "frametimes: none";
        var a = _frameMs.ToArray(); Array.Sort(a);
        double P(double q) => a[Math.Min(a.Length - 1, (int)(q * a.Length))];
        double sum = 0; foreach (var v in a) sum += v;
        const double rt = 1000.0 / 70;   // a VGA frame of machine time
        return $"frametimes: {a.Length} frames, mean {sum / a.Length:F2} ms ({rt / (sum / a.Length) * 100:F0}% real time), p50 {P(.5):F2}, p99 {P(.99):F2} ({rt / P(.99) * 100:F0}%), max {a[^1]:F1} ms, frames over 50 ms {a.Count(v => v > 50)}; GC gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, GC pause total {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms, allocated {GC.GetTotalAllocatedBytes() / 1048576} MB";
    }
    private static PcMachine? _partner; private static PcLanHub? _pairHub; private static PcNat? _pairNat;
    private static bool Frame(PcMachine m)
    {
        _frameSw.Restart();
        bool r = m.RunFrame();
        if (_partner != null && m != _partner) _partner.RunFrame();
        _frameMs.Add(_frameSw.Elapsed.TotalMilliseconds);
        if (_cdWav != null && m.Ata != null)
        {
            long now = (long)((Int128)m.PitNow * 44100 / Pit8254.Freq);
            if (_cdLast < 0 || now - _cdLast > 44100) _cdLast = now;
            int n = (int)Math.Min(now - _cdLast, _wavBuf.Length / 2) * 2; _cdLast += n / 2;
            if (n > 0) { Array.Clear(_wavBuf, 0, n); m.Ata.Secondary.MixCdAudio(_wavBuf, n); for (int i = 0; i < n; i++) { _cdWav.WriteByte((byte)_wavBuf[i]); _cdWav.WriteByte((byte)(_wavBuf[i] >> 8)); } }
        }
        if (_wav != null && m.Card is { } c)
        {
            c.Advance();
            int n = Math.Min(c.Pending, _wavBuf.Length) & ~1;
            if (n > 0) { Array.Clear(_wavBuf, 0, n); int got = c.MixInto(_wavBuf, n); for (int i = 0; i < got; i++) { _wav.WriteByte((byte)_wavBuf[i]); _wav.WriteByte((byte)(_wavBuf[i] >> 8)); } }
        }
        return r;
    }
    private static long _wavMark = -1;
    private static void WriteWav(string path)
    {
        if (_wavMark >= 0) { var all = _wav!.ToArray(); var tail = new System.IO.MemoryStream(); tail.Write(all, (int)_wavMark, all.Length - (int)_wavMark); WriteWav(Path.ChangeExtension(path, null) + ".after.wav", tail.ToArray()); }
        WriteWav(path, _wav!.ToArray());
    }
    private static void WriteWav(string path, byte[] d)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + d.Length); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(44100); w.Write(44100 * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(d.Length); w.Write(d);
        int peak = 0; for (int i = 0; i + 1 < d.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)(short)(d[i] | d[i + 1] << 8)));
        Console.WriteLine($"x86boot: wav {path}: {d.Length / 4} samples, peak {peak}");
    }

    public static int RunUntil(PcMachine m, string? until, int frames, bool screenOnly = false)
    {
        int n = 0;
        for (; n < frames; n++)
        {
            if (!Frame(m)) break;
            if (until != null && n % 10 == 0 && (Screen(m).Contains(until) || (!screenOnly && m.Teletype.ToString().Contains(until)))) break;
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
        for (int ci = 0; ci < text.Length; ci++)
        {
            char ch = text[ci];
            // {combo} tokens: {win}, {win+r}, {alt+f4}, {ctrl+esc}, {tab}, {f1}, {enter}, {del}, {home}, {pgdn}, {wait:N} (N frames), {{ = a literal '{'
            if (ch == '{' && ci + 1 < text.Length && text[ci + 1] != '{' && text.IndexOf('}', ci) is int ce and > 0)
            {
                string tok = text[(ci + 1)..ce].ToLowerInvariant(); ci = ce;
                if (tok.StartsWith("m:")) { var mv = tok[2..].Split(','); m.Kbc.MouseInput(int.Parse(mv[0]), int.Parse(mv[1]), mv.Length > 2 ? int.Parse(mv[2]) : 0); Frame(m); Frame(m); continue; }   // {m:dx,dy[,buttons]}: the PS/2 mouse
                if (tok.StartsWith("click:")) { var mv = tok[6..].Split(','); m.Kbc.MouseInput(int.Parse(mv[0]), int.Parse(mv[1]), 0); Frame(m); Frame(m); m.Kbc.MouseInput(0, 0, 1); Frame(m); Frame(m); m.Kbc.MouseInput(0, 0, 0); Frame(m); Frame(m); continue; }   // {click:dx,dy}: move then left click
                if (tok.StartsWith("save:")) { File.WriteAllBytes(text.Substring(ci - tok.Length + 5, tok.Length - 5), m.SaveState()); _wavMark = _wav?.Length ?? 0; continue; }   // {save:PATH}: a savestate right here (the run goes on; --wav also writes the sound from here on as FILE.after.wav)
                if (tok.StartsWith("load:")) { m.LoadState(File.ReadAllBytes(text.Substring(ci - tok.Length + 5, tok.Length - 5))); continue; }   // {load:PATH}: load a savestate right here, in this process (the run goes on from it)
                if (tok.StartsWith("wait:")) { int wn = int.Parse(tok[5..]); for (int q = 0; q < wn; q++) Frame(m); continue; }
                var parts = tok.Split('+'); var mods = new List<byte[]>();
                foreach (var p in parts[..^1]) mods.Add(p switch { "ctrl" => new byte[] { 0x1d }, "alt" => new byte[] { 0x38 }, "shift" => new byte[] { 0x2a }, "win" => new byte[] { 0xe0, 0x5b }, _ => throw new ArgumentException("key mod " + p) });
                byte[] key = TokenKey(parts[^1]);
                foreach (var md in mods) { m.Kbc.KeyBytes(md); Frame(m); }
                m.Kbc.KeyBytes(key); Frame(m);
                var brk = (byte[])key.Clone(); brk[^1] |= 0x80; m.Kbc.KeyBytes(brk); Frame(m);
                for (int q = mods.Count - 1; q >= 0; q--) { var mb = (byte[])mods[q].Clone(); mb[^1] |= 0x80; m.Kbc.KeyBytes(mb); Frame(m); }
                Frame(m);
                continue;
            }
            if (ch == '{') ci++;
            if (ch == '\x05') { m.Kbc.KeyBytes(0xe0, 0x5b); m.Kbc.KeyBytes(0xe0, 0xdb); Frame(m); Frame(m); continue; }   // \w in --step: the Windows key
            if (ch is >= '\x01' and <= '\x04')   // arrow keys (\u \d \l \r in --step): E0-prefixed set-1 codes
            {
                byte c = ch switch { '\x01' => 0x48, '\x02' => 0x50, '\x03' => 0x4b, _ => 0x4d };
                m.Kbc.KeyBytes(0xe0, c); m.Kbc.KeyBytes(0xe0, (byte)(c | 0x80));
                Frame(m); Frame(m);
                continue;
            }
            if (!Keys.TryGetValue(ch, out var k)) continue;
            if (k.shift) m.Kbc.KeyBytes(0x2a);
            m.Kbc.KeyBytes(k.code, (byte)(k.code | 0x80));
            if (k.shift) m.Kbc.KeyBytes(0xaa);
            Frame(m); Frame(m);
        }
    }

    private static byte[] TokenKey(string k)
    {
        if (k.Length > 1 && k[0] == 'f' && int.TryParse(k[1..], out int fn)) return new[] { (byte)(fn <= 10 ? 0x3a + fn : 0x57 + fn - 11) };
        return k switch
        {
            "win" => new byte[] { 0xe0, 0x5b }, "menu" => new byte[] { 0xe0, 0x5d }, "esc" => new byte[] { 0x01 }, "tab" => new byte[] { 0x0f },
            "enter" => new byte[] { 0x1c }, "space" => new byte[] { 0x39 }, "bs" => new byte[] { 0x0e }, "alt" => new byte[] { 0x38 },
            "ctrl" => new byte[] { 0x1d }, "shift" => new byte[] { 0x2a },
            "up" => new byte[] { 0xe0, 0x48 }, "down" => new byte[] { 0xe0, 0x50 }, "left" => new byte[] { 0xe0, 0x4b }, "right" => new byte[] { 0xe0, 0x4d },
            "home" => new byte[] { 0xe0, 0x47 }, "end" => new byte[] { 0xe0, 0x4f }, "pgup" => new byte[] { 0xe0, 0x49 }, "pgdn" => new byte[] { 0xe0, 0x51 },
            "del" => new byte[] { 0xe0, 0x53 }, "ins" => new byte[] { 0xe0, 0x52 }, "kpmul" => new byte[] { 0x37 },
            _ when k.Length == 1 && Keys.TryGetValue(k[0], out var kk) => new[] { kk.code },
            _ => throw new ArgumentException("key token " + k),
        };
    }

    /// <summary>A linear address through the page tables (when paging is on), for the tools: 0xffffffff when not present.</summary>
    public static uint LinPhys(PcMachine m, uint la)
    {
        if ((m.Cpu.Cr0 & 0x80000000) == 0) return la;
        uint pde = m.Phys32((m.Cpu.Cr3 & ~0xfffu) + (la >> 22) * 4); if ((pde & 1) == 0) return 0xffffffff;
        if ((pde & 0x80) != 0 && (m.Cpu.Cr4 & 0x10) != 0) return (pde & 0xffc00000) | (la & 0x3fffff);
        uint pte = m.Phys32((pde & ~0xfffu) + ((la >> 12) & 0x3ff) * 4); return (pte & 1) == 0 ? 0xffffffff : (pte & ~0xfffu) | (la & 0xfff);
    }
    public static string LinBytes(PcMachine m, uint la, int n) => string.Join(" ", Enumerable.Range(0, n).Select(i => { uint p = LinPhys(m, la + (uint)i); return p == 0xffffffff ? "??" : m.Phys8(p).ToString("X2"); }));

    private static string? Arg(string[] a, string k) { int i = Array.FindIndex(a, x => x.Equals(k, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    private static IEnumerable<string> Args(string[] a, string k) { for (int i = 0; i + 1 < a.Length; i++) if (a[i].Equals(k, StringComparison.OrdinalIgnoreCase)) yield return a[i + 1]; }
}
