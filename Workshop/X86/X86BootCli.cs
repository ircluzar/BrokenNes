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
    private static readonly int SampleFrom = int.TryParse(Environment.GetEnvironmentVariable("X86_SAMPLEFROM"), out int sf) ? sf : 0, SampleSlice = int.TryParse(Environment.GetEnvironmentVariable("X86_SAMPLESLICE"), out int ss) ? ss : 997;

    public static int Boot(string[] args)
    {
        try { args = LibraryArgs(args); }
        catch (Exception e) when (e is FileNotFoundException or UnauthorizedAccessException or IOException or System.Text.Json.JsonException) { Console.WriteLine("x86boot: " + e.Message); return 1; }
        if (args.Contains("--allocs")) _allocs = new AllocListener();
        if (Environment.GetEnvironmentVariable("X86_GCLAT") != "0") System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        var cfg = Arg(args, "--machine") is string mp ? PcConfig.Preset(mp) : PcConfig.Default;
        if (Arg(args, "--mem") is string mem) cfg = cfg with { MemoryMB = int.Parse(mem) };
        if (args.Contains("--voodoo")) cfg = cfg with { Voodoo = true };
        if (Arg(args, "--bootorder") is string bo) cfg = cfg with { BootOrder = bo.ToUpperInvariant() };
        // --machine names a whole machine (the desktop's presets): its CPU model and video card, unless --cpu / --video say otherwise
        var preset = Arg(args, "--machine") is string mk && PcMachinePreset.Known(mk) ? PcMachinePreset.Find(mk) : null;
        var model = Arg(args, "--cpu") is string cs ? CpuModel.Parse(cs) ?? throw new ArgumentException($"no CPU model {cs}") : preset?.Cpu;
        var video = Arg(args, "--video") is string vs ? (vs.ToLowerInvariant() switch { "s3" or "s3trio64" => PcVideo.S3Trio64, "virge" or "s3virge" => PcVideo.S3Virge, "geforce" or "nv10" => PcVideo.GeForce256, "gf4mx" or "nv17" => PcVideo.GeForce4Mx, "fx5200" or "nv34" => PcVideo.GeForceFx5200, _ => PcVideo.Vga }) : preset?.Video ?? PcVideo.Vga;
        var m = cfg == PcConfig.Default ? new PcMachine(PcMachine.DefaultMemory, model, video) : new PcMachine(cfg, model, video);
        if (Arg(args, "--ips") is string ips) m.InstructionsPerSecond = long.Parse(ips);
        if (Arg(args, "--wav") != null) _wav = new System.IO.MemoryStream();
        if (Arg(args, "--mp4") is string mp4p) { var sz = (Arg(args, "--mp4size") ?? "640x480").Split('x'); _mp4 = new BrokenNes.Workshop.Movies.Mp4Writer(mp4p, int.Parse(sz[0]), int.Parse(sz[1]), 25175000, 449 * 800, audioRate: _mp4Rate = int.Parse(Arg(args, "--mp4rate") ?? "44100"), videoKbps: int.Parse(Arg(args, "--mp4kbps") ?? "4000")); }   // --mp4rate 48000: the 44.1 kHz card sound resampled (linear) for the AAC track
        if (Arg(args, "--cdwav") != null) _cdWav = new System.IO.MemoryStream();
        if (Arg(args, "--apu") is string apu && !PcSound.Apply(m, apu)) { Console.WriteLine($"x86boot: no sound card {apu}"); return 1; }
        if (Arg(args, "--hdd") is string h) m.Hdd[0] = new DiskImage(DiskImage.ReadImageFile(h), false);
        if (Arg(args, "--hdd2") is string h2) m.Hdd[1] = new DiskImage(h2.Equals("blank", StringComparison.OrdinalIgnoreCase) ? N98Seat.MakeBlankDrive(N98Seat.StandardDriveMb) : DiskImage.ReadImageFile(h2), false);   // blank = the nano98 seat's empty FAT D:
        if (Arg(args, "--cd") is string cdp) { m.Cd = CdImage.Open(cdp); Console.WriteLine(m.Cd); }
        if (Arg(args, "--floppy") is string f) m.Floppy[0] = new DiskImage(File.ReadAllBytes(f), true);
        int frames = int.Parse(Arg(args, "--frames") ?? "3000");
        string? until = Arg(args, "--until"), type = Arg(args, "--type"), shot = Arg(args, "--shot");
        _capEvery = int.Parse(Arg(args, "--bugcapture") ?? "0");
        // --mac: the card's PROM address (a cold boot reads it; a loaded state keeps the address saved in it)
        if (Arg(args, "--mac") is string macs && m.Nic != null) { m.Nic.Mac = PcMac.Parse(macs); m.Nic.Reset(); }
        var sw = Stopwatch.StartNew();
        m.PowerOn();
        // --loadstate: carry on from a --savestate of the same machine (its disks come from the state; the CD stays the one given)
        if (Arg(args, "--loadstate") is string ls) { var lsw = System.Diagnostics.Stopwatch.StartNew(); var lsb = File.ReadAllBytes(ls); double lsr = lsw.Elapsed.TotalMilliseconds; m.LoadState(lsb); Console.WriteLine($"x86boot: loadstate {lsb.Length / 1024 / 1024} MB: read {lsr:F0} ms + load {lsw.Elapsed.TotalMilliseconds - lsr:F0} ms"); if (_allocs != null) lock (_allocs.ByType) _allocs.ByType.Clear();   /* (count the run, not the load) */ if (args.Contains("--cdchanged")) m.Ata?.Secondary.MediaChanged(0); if (Arg(args, "--floppy") is string lf) m.Floppy[0] = new DiskImage(File.ReadAllBytes(lf), true); }   // (a floppy inserted into a loaded state)   // --cdchanged: the disc was swapped since the state was saved (UNIT ATTENTION)
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
            if ((Arg(args, "--pair-hdd") ?? Arg(args, "--hdd")) is string ph) m2.Hdd[0] = new DiskImage(File.ReadAllBytes(ph), false);   // --pair-hdd: the second PC's own disk (its own computer name)
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
        if (Environment.GetEnvironmentVariable("X86_DBGRING") == "1") m.Cpu.Debug = new X86Debug(int.Parse(Environment.GetEnvironmentVariable("X86_DBGRINGN") ?? "65536"));   // instruction ring for the ring-0 #6 dump
        if (excKeep > 0)
            m.Cpu.ExceptionLog = (v, e) =>
            {
                excCount[v & 31]++;
                bool pf0 = v is 14 or 6 && m.Cpu.SegSel[1] == 0x28 && Environment.GetEnvironmentVariable("X86_PFLOG0") == "1";   // ring-0 page faults too (a fatal 0E hunt)
                if (pf0) excRing.Enqueue($"#X0 v{v} stackphys {LinPhys(m, m.Cpu.SegBase[2] + m.Cpu.R[4]):X8} CR2 {m.Cpu.Cr2:X8} EBP {m.Cpu.R[5]:X8} ESP {m.Cpu.R[4]:X8} ECX {m.Cpu.R[1]:X8} EDX {m.Cpu.R[2]:X8} stk " + string.Join(" ", Enumerable.Range(0, 40).Select(k => { uint p = LinPhys(m, m.Cpu.SegBase[2] + m.Cpu.R[4] + (uint)k * 4); return p == 0xffffffff ? "?" : m.Phys32(p).ToString("X8"); })));
                if (pf0 && v == 6 && m.Cpu.Debug is { } dbg) { int L = dbg.TrCs.Length; long q0 = dbg.TrCount - 1; while (q0 > dbg.TrCount - L + 1 && dbg.TrEip[(int)((q0 - 1) % L)] + 16 > dbg.TrEip[(int)(q0 % L)] && dbg.TrEip[(int)((q0 - 1) % L)] < dbg.TrEip[(int)(q0 % L)]) q0--;   // back to the start of the sled
                    if (Environment.GetEnvironmentVariable("X86_DBGESP") is string de) { var dr = de.Split('-'); uint elo = Convert.ToUInt32(dr[0], 16), ehi = Convert.ToUInt32(dr[1], 16); bool inE = false; long last = -1; int shown = 0;
                        for (long q = Math.Max(0, dbg.TrCount - L + 1); q < dbg.TrCount; q++) { int t = (int)(q % L); bool e2 = dbg.TrEsp[t] >= elo && dbg.TrEsp[t] < ehi; if (e2 != inE && shown < 400) { excRing.Enqueue($"   esp {(e2 ? "IN " : "OUT")} #{q - dbg.TrCount} {dbg.TrCs[t]:X4}:{dbg.TrEip[t]:X8} ESP {dbg.TrEsp[t]:X8} | {LinBytes(m, dbg.TrLin[t], 8)}"); shown++; } inE = e2; if (e2) last = q; }
                        if (last >= 0) for (long q = Math.Max(0, last - 400); q <= last; q++) { int t = (int)(q % L); excRing.Enqueue($"   espl {dbg.TrCs[t]:X4}:{dbg.TrEip[t]:X8} ESP {dbg.TrEsp[t]:X8} | {LinBytes(m, dbg.TrLin[t], 8)}"); } }
                    for (long q = Math.Max(0, q0 - int.Parse(Environment.GetEnvironmentVariable("X86_DBGBACK") ?? "120")); q < q0 + 3; q++) { int t = (int)(q % L); excRing.Enqueue($"   tr {dbg.TrCs[t]:X4}:{dbg.TrEip[t]:X8} | {LinBytes(m, dbg.TrLin[t], 8)}"); } }
                if ((v == 14 && !pf0) || (((m.Cpu.Flags >> 17) & 1) != 0 && v is 6 or 13 && !(v == 13 && m.Cpu.SegSel[1] == 0xf000 && m.Cpu.Ip < 0xe800))) return;   // (V86 monitor traps: the usual traffic)
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
        // guest status lines (port E9h, the nano98 launcher): printed as they come; --until-status TEXT stops the run at the first line containing TEXT
        string? untilStatus = Arg(args, "--until-status");
        if (Arg(args, "--n98cmd") is string ncf && m.Command == null) m.Command = File.ReadAllBytes(ncf);   // the nano98 command port's text (a loaded state keeps its own)
        m.StatusLine += l => { Console.WriteLine($"x86boot: status \"{l}\" at cycle {m.Cpu.Cycle}"); if (untilStatus != null && l.Contains(untilStatus)) m.Stopped ??= "status: " + l; };
        var shotAt = Args(args, "--shotat").Select(x => { int i = x.IndexOf('='); return (f: int.Parse(x[..i]), p: x[(i + 1)..]); }).OrderBy(x => x.f).ToList();
        int n = 0;
        foreach (var (sf, sp) in shotAt) { n += RunUntil(m, null, sf - n); X86Shots.SavePng(m.Vga, sp); Console.WriteLine($"x86boot: frame {n} at {sw.Elapsed.TotalSeconds:F1} s, cycle {m.Cpu.Cycle}"); }
        var hot = new Dictionary<string, int>();
        bool hotVm = args.Contains("--hotvm"), hotMod = args.Contains("--hotmod"), sampling = args.Contains("--hot") || hotVm || hotMod;
        var hotLin = new Dictionary<uint, int>();   // --hotmod: flat 32-bit samples by 64 KB page of the linear address (named by the PE image holding it at the end)
        var sampler = sampling ? new System.Threading.Thread(() =>
        {
            while (sampling)
            {
                System.Threading.Thread.Sleep(1);
                if (hotMod)
                {
                    string mk = m.Cpu.Halt ? "idle (HLT)" : (m.Cpu.Flags & 0x20000) != 0 ? "V86" : (m.Cpu.Cr0 & 1) == 0 ? "real mode" : m.Cpu.Code16 ? $"pm16 CS={m.Cpu.SegSel[1]:X4}" : "";
                    lock (hot) { if (mk != "") hot[mk] = hot.GetValueOrDefault(mk) + 1; else { uint pg = (m.Cpu.SegBase[1] + m.Cpu.Ip) & 0xffff0000; hotLin[pg] = hotLin.GetValueOrDefault(pg) + 1; } }
                    continue;
                }
                if (hotVm && (m.Cpu.Flags & 0x20000) == 0) continue;   // --hotvm: V86 samples only (what a DOS box is doing)
                string k = $"{m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip & (hotVm ? ~0u : ~0x3fu):X8} vm{(m.Cpu.Flags >> 17) & 1} pe{m.Cpu.Cr0 & 1}";
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
        long v86a = m.Cpu.V86Enters; int n0 = n; var irqa = (long[])m.IrqCount.Clone(); var swa = sw.Elapsed;
        n += RunUntil(m, until, frames - n);
        sampling = false;
        if (hotMod) HotModReport(m, hot, hotLin, n - n0, m.Cpu.V86Enters - v86a, irqa, sw.Elapsed - swa);
        else if (sampler != null) foreach (var kv in hot.OrderByDescending(x => x.Value).Take(30)) Console.WriteLine($"  hot {kv.Key} {kv.Value}");
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
        // --stepsfile FILE: more --step values, one per line (keeps "=>" and spaces away from remote shells' quoting)
        var steps = Args(args, "--step").ToList();
        if (Arg(args, "--stepsfile") is string stepsFile) steps.AddRange(File.ReadAllLines(stepsFile).Where(l => l.Contains("=>")));
        foreach (var st in steps)
        {
            int i = st.IndexOf("=>"); string u = st[..i], keys = st[(i + 2)..].Replace("\\n", "\n").Replace("\\e", "\x1b").Replace("\\t", "\t")
                .Replace("\\u", "\x01").Replace("\\d", "\x02").Replace("\\l", "\x03").Replace("\\r", "\x04").Replace("\\w", "\x05");
            // "@N=>KEYS": graphics screens have no text to match, so wait N frames then type
            bool wait = u.StartsWith('@') && int.TryParse(u[1..], out _);
            // "#RRGGBB@X,Y[/N]=>KEYS": a graphics screen check: wait (up to N frames, else --stepmax) until pixel X,Y has that colour, then hold 60 frames
            // "...?=>KEYS": optional: when the check fails the keys are skipped and the script goes on (e.g. a hang that only sometimes happens)
            bool optional = u.EndsWith('?'); if (optional) u = u[..^1];
            // "...!=>KEYS": the keys only when the check FAILED (e.g. "desktop within N frames, else {powercycle}"); the script goes on either way
            bool unless = u.EndsWith('!'); if (unless) u = u[..^1];
            bool pix = u.StartsWith('#') && u.Contains('@');
            bool PixelIs()
            {
                var pp = u[1..].Split('@'); var xy = pp[1].Split('/')[0].Split(','); int x = int.Parse(xy[0]), y = int.Parse(xy[1]);
                int want = Convert.ToInt32(pp[0], 16); var v = m.Vga;
                return x < v.Width && y < v.Height && (v.Frame[y * v.Width + x] & 0xffffff) == want;
            }
            int ns;
            if (pix)
            {
                int lim = u.Contains('/') ? int.Parse(u[(u.IndexOf('/') + 1)..]) : stepMax; ns = 0;
                while (ns < lim)   // the colour must hold for 60 frames (a stale or passing picture does not count)
                {
                    if (PixelIs()) { ns += RunUntil(m, null, 60); if (PixelIs()) break; }
                    else ns += RunUntil(m, null, 30);
                }
            }
            else ns = wait ? RunUntil(m, null, int.Parse(u[1..])) : RunUntil(m, u, stepMax, true);
            n += ns;   // (the screen only: the teletype log keeps earlier screens' text)
            bool ok = wait || (pix ? PixelIs() : Screen(m).Contains(u));
            Console.WriteLine($"x86boot: step {si} \"{u}\" {(ok ? "found" : "NOT found")} after {ns} frames (total {n}, {sw.Elapsed.TotalSeconds:F0} s, heap {GC.GetTotalMemory(false) >> 20} MB){(m.Stopped != null ? ", STOPPED: " + m.Stopped : "")}");
            if (shot != null) X86Shots.SavePng(m.Vga, Path.ChangeExtension(shot, null) + $"_step{si}.png");
            si++;
            if (unless)
            {
                if (ok) continue;
                ok = true; Console.WriteLine("x86boot: check failed: running its keys");
            }
            if (!ok && optional) { Console.WriteLine($"x86boot: optional step skipped"); continue; }
            if (!ok) break;
            n += RunUntil(m, null, int.Parse(Arg(args, "--stepdelay") ?? "0"));   // let a menu settle before typing
            // "{powercycle}": a cold boot with the disks kept (the nano98 base build: Win98 Setup's safe recovery skips the detection probe that hung)
            if (keys.Trim() == "{powercycle}") { m.PowerOn(true); Console.WriteLine($"x86boot: power cycle at frame {n}"); continue; }
            TypeText(m, keys);
            n += RunUntil(m, null, 35);   // let the keys land before looking for the next screen
        }
        foreach (var dl in Args(args, "--dumplin")) { int eq = dl.IndexOf('='); var rg = dl[..eq].Split('-'); uint dlo = Convert.ToUInt32(rg[0], 16), dhi = Convert.ToUInt32(rg[1], 16); var buf = new byte[dhi - dlo]; for (uint q = 0; q < buf.Length; q++) { uint pp = LinPhys(m, dlo + q); buf[q] = pp == 0xffffffff ? (byte)0 : m.Phys8(pp); } File.WriteAllBytes(dl[(eq + 1)..], buf); Console.WriteLine($"x86boot: dumplin {dlo:X8}-{dhi:X8} -> {dl[(eq + 1)..]}"); }   // --dumplin HEXLO-HEXHI=FILE: guest linear memory after the steps (unmapped = 0)
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
        if (Arg(args, "--bp") is string bpx)   // --bp HEXLIN[,HEXLIN]: run (up to --bpframes, default 3000) until execution reaches one of these linear addresses; then --trace N steps from there
        {
            m.Cpu.Debug ??= new X86Debug(4096);
            foreach (var b in bpx.Split(',')) m.Cpu.Debug.Exec.Add(Convert.ToUInt32(b, 16));
            int bf = int.Parse(Arg(args, "--bpframes") ?? "3000"), fr = 0;
            int bph = int.Parse(Arg(args, "--bphit") ?? "1");
            for (int bh = 1; ; bh++)
            {
                while (!m.Cpu.Debug.Stop && fr++ < bf) Frame(m);
                if (!m.Cpu.Debug.Stop || bh >= bph) break;
                if (args.Contains("--bplog")) { uint esp = m.Cpu.SegBase[2] + m.Cpu.R[4]; Console.WriteLine($"bphit {bh} frame {m.Vga.FrameCount} EAX {m.Cpu.R[0]:X8} ESP {esp:X8} stk {LinBytes(m, esp, 32)}"); foreach (var bd in Args(args, "--bpderef")) { int k = int.Parse(bd); uint pv = 0; for (int q = 0; q < 4; q++) { uint pp = LinPhys(m, esp + (uint)k * 4 + (uint)q); if (pp != 0xffffffff) pv |= (uint)m.Phys8(pp) << (8 * q); } Console.WriteLine($"   [esp+{k * 4:X}] -> {pv:X8}: {LinBytes(m, pv, 48)}"); } }
                m.Cpu.Debug.Stop = false; var saved = m.Cpu.Debug.Exec.ToArray(); m.Cpu.Debug.Exec.Clear(); m.Cpu.Step(1); m.Cpu.Debug.Stop = false; foreach (var sv in saved) m.Cpu.Debug.Exec.Add(sv);
            }
            Console.WriteLine($"bp: {(m.Cpu.Debug.Stop ? "hit " + m.Cpu.Debug.Reason : "not hit")} after {fr} frames");
            m.Cpu.Debug.Exec.Clear(); m.Cpu.Debug.Stop = false;
        }
        if (Arg(args, "--traceat") is string ta)
        {
            var tp = ta.Split('-'); uint lo = Convert.ToUInt32(tp[0], 16), hi = Convert.ToUInt32(tp[1], 16); long sl = 0; int tslice = int.Parse(Arg(args, "--traceslice") ?? "1");
            int thits = int.Parse(Arg(args, "--tracehit") ?? "1"), th = 0; bool inR = false;   // --tracehit N: the Nth entry into the range (not the first)
            var tring = new Queue<string>(); bool tr = args.Contains("--tracering");   // --tracering: print the last 80 instructions before the stop
            while (sl++ < 400_000_000) { uint l = m.Cpu.SegBase[1] + m.Cpu.NextIp; if (tr) { tring.Enqueue($"{m.Cpu.SegSel[1]:X4}:{m.Cpu.NextIp:X8} SS:SP {m.Cpu.SegSel[2]:X4}:{m.Cpu.R[4]:X4} AX {m.Cpu.R[0]:X4} vm{(m.Cpu.Flags >> 17) & 1} | {LinBytes(m, l, 6)}"); if (tring.Count > 400) tring.Dequeue(); } bool r = l >= lo && l < hi; if (r && !inR) { th++; if (args.Contains("--tracehitlog")) Console.WriteLine($"hit {th}: SS:SP {m.Cpu.SegSel[2]:X4}:{m.Cpu.R[4]:X8} frame {LinBytes(m, m.Cpu.SegBase[2] + (m.Cpu.R[4] & 0xffff), 16)} AX {m.Cpu.R[0]:X8}"); if (th >= thits) break; }
                if (Arg(args, "--tracestopcs") is string tsc && m.Cpu.SegSel[1] == Convert.ToUInt32(tsc, 16)) break;
                if (Arg(args, "--tracestopbytes") is string tsb && LinBytes(m, l, tsb.Length / 3 + 1) == tsb) break;   /* stop when the code at CS:EIP starts with these bytes ("20 07 20 07") */ inR = r; m.RunClock(tslice); }
            Console.WriteLine($"traceat: at {m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8} after {sl} slices");
            foreach (var t in tring) Console.WriteLine("  ring " + t);
        }
        if (Arg(args, "--nvtrace") is string nvt && m.Nv != null)   // --nvtrace HEXPUT:N:FILE: run until the driver writes that DMA PUT, then log N instructions
        {
            var np = nvt.Split(':', 3); m.Nv.TracePut = Convert.ToUInt32(np[0], 16); long sl = 0;
            while (!m.Nv.PutHit && sl++ < 50_000_000) m.RunClock(2000);
            using var tw2 = new StreamWriter(np[2]);
            tw2.WriteLine($"nvtrace: PUT hit after {sl} slices");
            for (int k = 0; k < int.Parse(np[1]); k++)
            {
                uint lin = m.Cpu.SegBase[1] + m.Cpu.Ip;
                tw2.WriteLine($"{m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8} EAX {m.Cpu.R[0]:X8} EBX {m.Cpu.R[3]:X8} ECX {m.Cpu.R[1]:X8} EDX {m.Cpu.R[2]:X8} ESI {m.Cpu.R[6]:X8} EDI {m.Cpu.R[7]:X8} EBP {m.Cpu.R[5]:X8} ESP {m.Cpu.R[4]:X8} | {LinBytes(m, lin, 8)}");
                m.Cpu.Step(1);
            }
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
        foreach (var dl in Args(args, "--dumplin2")) { int eq = dl.IndexOf('='); var rg = dl[..eq].Split('-'); uint dlo = Convert.ToUInt32(rg[0], 16), dhi = Convert.ToUInt32(rg[1], 16); var buf = new byte[dhi - dlo]; for (uint q = 0; q < buf.Length; q++) { uint pp = LinPhys(m, dlo + q); buf[q] = pp == 0xffffffff ? (byte)0 : m.Phys8(pp); } File.WriteAllBytes(dl[(eq + 1)..], buf); Console.WriteLine($"x86boot: dumplin2 {dlo:X8}-{dhi:X8} -> {dl[(eq + 1)..]}"); }   // --dumplin2: the same as --dumplin, after --bp/--traceat/--trace (the VM that stopped there is mapped)
        foreach (var kv in portWho.OrderByDescending(x => x.Value).Take(20)) Console.WriteLine($"portwho {kv.Key} {kv.Value}");
        if (args.Contains("--portlog"))
            Console.WriteLine("ports: " + string.Join(" ", Enumerable.Range(0, 0x20000).Where(i => portCount[i] > 0).OrderByDescending(i => portCount[i]).Take(40)
                .Select(i => $"{((i & 0x10000) != 0 ? "out" : "in")}{i & 0xffff:X}={portCount[i]}")));
        if (Arg(args, "--nvstart") is string nvs && m.Nv != null) { m.Nv.PcrtcStart = Convert.ToUInt32(nvs, 16); m.Vga.Render(); X86Shots.SavePng(m.Vga, Arg(args, "--shot") + ".start.png"); }   // a diagnostic: show another buffer
        if (Arg(args, "--nvfind") is string nvf && m.Nv != null) foreach (var l in m.Nv.FindDma(Convert.ToUInt32(nvf, 16))) Console.WriteLine("nvfind " + l);
        if (args.Contains("--vgadump"))   // why a picture is blank: the registers, the palette and how much video memory is non-zero
        {
            if (m.Vga.S3 is { VBox: true } vb) Console.WriteLine($"dispi on {vb.DispiOn} regs " + string.Join(" ", vb.Dispi.Select(x => x.ToString("X4"))) + $" lfb {vb.LfbBase:X8} lfbwrites {vb.LfbWrites} bankwrites {vb.BankWrites} pcibar {vb.PciRead(0x10):X8} w {vb.Width} h {vb.Height} stride {vb.Stride} bpp {vb.BytesPerPixel}");
            var v = m.Vga;
            string H(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));
            Console.WriteLine($"vga SR {H(v.Sr)}\nvga GR {H(v.Gr[..9])}\nvga AR {H(v.Ar)}\nvga CR {H(v.Cr[..0x19])}\nvga DAC0-15 {H(v.Palette[..48])}");
            Console.WriteLine($"vga ram non-zero bytes {v.Ram.Count(b => b != 0)} of {v.Ram.Length}; S3 {(m.S3 != null ? "yes" : "no")}; CR30-6F {H(v.Cr[0x30..0x70])}");
            Console.WriteLine($"cpu CS:EIP {m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8} csbase {m.Cpu.SegBase[1]:X8} ES {m.Cpu.SegSel[0]:X4} base {m.Cpu.SegBase[0]:X8} EBP {m.Cpu.R[5]:X8}");
            if (m.Nv is { } nv2) Console.WriteLine("nv fifo " + string.Join(" ", new uint[] { 0x2100, 0x2140, 0x2500, 0x2504, 0x3200, 0x3204, 0x3210, 0x3220, 0x3240, 0x3244, 0x3250, 0x3270, 0x400100, 0x400140 }.Select(r => $"{r:X}={nv2.MmioRead(r, 4):X}")));
            if (m.Nv is { } nv3) { var hist = new Dictionary<int, int>(); for (int yy = 0; yy < nv3.Height; yy++) for (int xx = 0; xx < nv3.Width; xx++) { int o = nv3.StartByte + yy * nv3.Stride + xx * 2; int pv = v.Ram[o] | v.Ram[o + 1] << 8; hist[pv] = hist.GetValueOrDefault(pv) + 1; } Console.WriteLine("nv screen colours " + string.Join(" ", hist.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key:X4}x{kv.Value}"))); }
            if (m.Nv is { } nv) Console.WriteLine($"nv CR19-4F {H(v.Cr[0x19..0x50])} depth {nv.Depth} {nv.Width}x{nv.Height} stride {nv.Stride} start {nv.StartByte:X} 565 {nv.Is565} methods {nv.Methods} push {nv.PushWords} tris {nv.Triangles3D} px3d {nv.Pixels3D} nonzero-first-2MB {v.Ram.Take(2 << 20).Count(b => b != 0)}");
            { int cb = ((v.Cr[0x4c] & 0x0f) << 8 | v.Cr[0x4d]) * 1024; for (int l = 0; l < 4; l++) Console.WriteLine($"vga cursor@{cb:X} line {l}: {H(v.Ram[(cb + l * 16)..(cb + l * 16 + 16)])}"); }
            { var nz = new List<string>(); int run = -1; for (int k = 0x4b000; k < v.Ram.Length; k += 256) { bool any = false; for (int q = 0; q < 256; q++) if (v.Ram[k + q] != 0) { any = true; break; } if (any && run < 0) run = k; if (!any && run >= 0) { nz.Add($"{run:X}-{k:X}"); run = -1; } } Console.WriteLine("vga offscreen non-zero: " + string.Join(" ", nz.Take(30))); }
            Console.WriteLine($"system RAM A0000-BFFFF non-zero bytes {m.Cpu.Mem.AsSpan(0xa0000, 0x20000).ToArray().Count(b => b != 0)} (should stay 0: the VGA owns it)");
        }
        if (Arg(args, "--sample") is string smp)
            for (int k = 0; k < int.Parse(smp); k++)
            {
                if (k < SampleFrom) { m.RunClock(997); continue; }   // (X86_SAMPLEFROM: run to there in 997-unit slices, quietly; then X86_SAMPLESLICE units)
                m.RunClock(k == SampleFrom ? 0 : SampleSlice);
                uint lin = m.Cpu.SegBase[1] + m.Cpu.Ip;
                var bytes = LinBytes(m, lin, 12);
                Console.WriteLine($"  cyc {m.Cpu.Cycle} {m.Cpu.SegSel[1]:X4}:{m.Cpu.Ip:X8} lin {lin:X8} EAX {m.Cpu.R[0]:X8} ECX {m.Cpu.R[1]:X8} EDX {m.Cpu.R[2]:X8} ESP {m.Cpu.R[4]:X8} IF {(m.Cpu.Flags >> 9) & 1} | {bytes}");
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
        if (Arg(args, "--ipsync") is string isy)   // --ipsync LO-HI (hex linear): run small slices until EIP is in that range, so --dump reads that process's address space (Win98: per-process low memory)
        {
            var r = isy.Split('-'); uint lo = Convert.ToUInt32(r[0], 16), hi = Convert.ToUInt32(r[1], 16); int k = 0;
            for (; k < 200000; k++) { uint lin = m.Cpu.SegBase[1] + m.Cpu.Ip; if (lin >= lo && lin < hi) break; m.RunClock(97); }
            Console.WriteLine($"ipsync: {k} slices, EIP lin {m.Cpu.SegBase[1] + m.Cpu.Ip:X8}");
        }
        foreach (var ds in Args(args, "--disasm"))   // --disasm HEXLIN:N: N instructions of 32-bit code at a linear address (through the page tables)
        {
            var dp = ds.Split(':'); uint a = Convert.ToUInt32(dp[0], 16);
            for (int k = 0; k < int.Parse(dp[1]); k++)
            {
                var (len, text) = X86Disasm.Decode(x => { uint p = LinPhys(m, x); return p == 0xffffffff ? 0 : m.Phys8(p); }, a, a, true);
                Console.WriteLine($"dis {a:X8}: {LinBytes(m, a, Math.Max(1, len))} {text}"); a += (uint)Math.Max(1, len);
            }
        }
        foreach (var da in Args(args, "--dump"))   // --dump HEXLIN:LEN: bytes at a linear address (through the page tables)
        {
            var parts = da.Split(':'); uint a0 = Convert.ToUInt32(parts[0], 16); int len = parts.Length > 1 ? int.Parse(parts[1]) : 64;
            for (int k = 0; k < len; k += 16) Console.WriteLine($"dump {a0 + (uint)k:X8}: {LinBytes(m, a0 + (uint)k, Math.Min(16, len - k))}");
        }
        foreach (var fp in Args(args, "--findphys"))   // --findphys HEXPHYS[:BYTES]: which linear pages map onto a physical range (paging on)
        {
            var parts = fp.Split(':'); uint p0 = Convert.ToUInt32(parts[0], 16), plen = parts.Length > 1 ? Convert.ToUInt32(parts[1], 16) : 0x1000; int hits = 0;
            for (ulong la = 0; la < 0x100000000UL && hits < 64; la += 4096) { uint p = LinPhys(m, (uint)la); if (p != 0xffffffff && p - p0 < plen) { Console.WriteLine($"findphys lin {la:X8} -> phys {p:X8}"); hits++; } }
            Console.WriteLine($"findphys {p0:X8}+{plen:X}: {hits} page(s)");
        }
        foreach (var la in Args(args, "--linmap"))   // --linmap HEXLIN[:COUNTPAGES]: where linear pages land (paging on)
        {
            var parts = la.Split(':'); uint a0 = Convert.ToUInt32(parts[0], 16); int np = parts.Length > 1 ? int.Parse(parts[1]) : 1;
            Console.WriteLine($"linmap {a0:X8}: " + string.Join(" ", Enumerable.Range(0, np).Select(k => { uint p = LinPhys(m, a0 + (uint)k * 4096); return p == 0xffffffff ? "--" : (p >> 12).ToString("X"); })) + $" | {LinBytes(m, a0, 16)}");
        }
        CaptureReport();
        if (args.Contains("--frametimes")) Console.WriteLine(FrameTimeReport());
        if (args.Contains("--frametimes")) foreach (var (w0, w1) in new[] { (0, 600), (600, 1800), (0, 1800) }) if (_frameMs.Count >= w1) Console.WriteLine(FrameWinReport(w0, w1));   // (after a load: the warm-up window first)
        if (args.Contains("--dacdump")) for (int r = 0; r < 256; r += 16) Console.WriteLine($"dac {r:X2}: " + string.Join(" ", Enumerable.Range(r, 16).Select(i => $"{m.Vga.Palette[i * 3]:X2}{m.Vga.Palette[i * 3 + 1]:X2}{m.Vga.Palette[i * 3 + 2]:X2}")));
        Console.WriteLine($"x86boot: guest clock {m.InstructionsPerSecond:N0} instruction clocks / s (CPU model {m.Cpu.Model.Name}, CPI {m.Cpu.Model.Cpi})");   // a loaded state carries its own
        if (Environment.GetEnvironmentVariable("X86_VGADUMP") == "1")   // the VGA's register state at the end (text vs graphics decisions)
            Console.WriteLine($"vgadump: misc {m.Vga.Msr:X2} SR {Convert.ToHexString(m.Vga.Sr)} GR {Convert.ToHexString(m.Vga.Gr)} AR {Convert.ToHexString(m.Vga.Ar)} CR {Convert.ToHexString(m.Vga.Cr.AsSpan(0, 0x19))} S3 enh {m.S3?.Enhanced} BDA mode {m.Phys8(0x449):X2}");
        Console.WriteLine($"vga: DAC writes {m.Vga.DacWrites}, above 3Fh {m.Vga.DacWritesOver63}; S3 CR55 {(m.S3 != null ? m.Vga.Cr[0x55] : 0):X2} CR67 {(m.S3 != null ? m.Vga.Cr[0x67] : 0):X2} SR18 {(m.S3 != null ? m.S3.SrRead(0x18) : 0):X2}; VGA-by-MMIO writes {m.S3?.VgaMmioWrites}, CR53 {(m.S3 != null ? m.Vga.Cr[0x53] : 0):X2}");
        if (args.Contains("--frametimes")) { var gi = GC.GetGCMemoryInfo(GCKind.Ephemeral); Console.WriteLine($"gc last ephemeral: gen {gi.Generation}, pause {(gi.PauseDurations.Length > 0 ? gi.PauseDurations[0].TotalMilliseconds : 0):F1} ms, promoted {gi.PromotedBytes / 1048576} MB, heap {gi.HeapSizeBytes / 1048576} MB, fragmented {gi.FragmentedBytes / 1048576} MB; gen0 budget? total committed {gi.TotalCommittedBytes / 1048576} MB"); }
        if (Environment.GetEnvironmentVariable("X86_SAVEBENCH") == "1")
        {   // the bug-capture state's cost on this machine, by stage (diagnostics)
            for (int rep = 0; rep < 3; rep++)
            {
                var sbw = System.Diagnostics.Stopwatch.StartNew();
                var st = m.SaveState(); double tSave = sbw.Elapsed.TotalMilliseconds; sbw.Restart();
                uint crc = NesEmulator.Systems.ConsoleStateFile.Crc32Public(st); double tCrc = sbw.Elapsed.TotalMilliseconds; sbw.Restart();
                var wrapped = NesEmulator.Systems.ConsoleStateFile.Wrap(NesEmulator.Systems.ConsoleKind.Pc, "g", "t", new Dictionary<string, string>(), st); double tWrap = sbw.Elapsed.TotalMilliseconds; sbw.Restart();
                ulong fh = 14695981039346656037UL; var smem = m.Cpu.Mem; for (int i = 0; i < smem.Length; i++) { fh ^= smem[i]; fh *= 1099511628211UL; } double tFnv = sbw.Elapsed.TotalMilliseconds;
                uint rc = 0xFFFFFFFF; foreach (byte cb in st) { rc ^= cb; for (int k = 0; k < 8; k++) rc = (rc & 1) != 0 ? (rc >> 1) ^ 0xEDB88320u : rc >> 1; }
                Console.WriteLine($"savebench: crc {(~rc == crc ? "EQUALS" : "DIFFERS FROM")} the bitwise definition");
                var into = NesEmulator.Systems.ConsoleStateFile.WrapInto(NesEmulator.Systems.ConsoleKind.Pc, "g", "t", new Dictionary<string, string>(), st, new byte[wrapped.Length + 4096]);
                Console.WriteLine($"savebench: WrapInto a reused buffer {(into.AsSpan().SequenceEqual(wrapped) ? "EQUALS" : "DIFFERS FROM")} Wrap");
                bool same = NesEmulator.Systems.ConsoleStateFile.PayloadOf(wrapped).AsSpan().SequenceEqual(st);
                var ms2 = new MemoryStream(); m.SaveState(ms2); bool streamSame = ms2.ToArray().AsSpan().SequenceEqual(st);
                Console.WriteLine($"savebench: state {st.Length >> 20} MB {tSave:F0} ms, crc {tCrc:F0} ms, wrap(crc+deflate) {tWrap:F0} ms -> {wrapped.Length >> 20} MB, fnv over RAM {smem.Length >> 20} MB {tFnv:F0} ms; round trip {(same ? "EQUAL" : "DIFFERENT")}, stream save {(streamSame ? "EQUAL" : "DIFFERENT")}");
            }
        }
        if (Environment.GetEnvironmentVariable("X86_LATENCY") is { Length: > 0 } latSpec)
        {   // guest input latency (diagnostics): frames from a PS/2 mouse move (or a key, "key") to the first changed picture, 5 tries 30 frames apart
            static ulong Hash(PcMachine pm) { ulong h = 14695981039346656037UL; var f = pm.Vga.Frame; for (int i = 0; i < f.Length; i++) { h ^= (uint)f[i]; h *= 1099511628211UL; } return h; }
            for (int t = 0; t < 5; t++)
            {
                for (int k = 0; k < 30; k++) m.RunFrame();
                ulong h0 = Hash(m); m.RunFrame(); bool idle = Hash(m) == h0; h0 = Hash(m);
                if (latSpec == "key") m.Kbc.KeyBytes(0x39); else m.Kbc.MouseInput(t % 2 == 0 ? 40 : -40, 0, 0);
                int ln = 0; while (ln < 30) { m.RunFrame(); ln++; if (Hash(m) != h0) break; }
                if (latSpec == "key") m.Kbc.KeyBytes(0xb9);
                Console.WriteLine($"latency: try {t} {(idle ? "screen idle before" : "screen was changing")}: first change after {ln} frame(s)");
            }
        }
        if (m.Card is Dosbox072SoundBlaster sbm) Console.WriteLine("sbmixer: " + sbm.MixerSummary());
        if (PcMachine.AllocProbe) Console.WriteLine($"allocprobe: devices {m.AllocDevices / 1048576} MB, cpu {m.AllocCpu / 1048576} MB, render {m.AllocRender / 1048576} MB, total thread {GC.GetAllocatedBytesForCurrentThread() / 1048576} MB");
        if (_allocs != null) lock (_allocs.ByType) foreach (var kv in _allocs.ByType.OrderByDescending(x => x.Value).Take(15)) Console.WriteLine($"  alloc {kv.Value / 1048576,6} MB {kv.Key}");
        if (lanLink != null) Console.WriteLine($"lan: sent {lanLink.Sent} (IPX {lanLink.SentIpx}), received {lanLink.Received} (IPX {lanLink.ReceivedIpx}){(lanLink.Members > 0 ? $", members {lanLink.Members}" : "")}");
        if (m.Ata != null && m.Pci != null) Console.WriteLine($"ide: PCI cmd {m.Pci.ConfigRead(0, PciBus.IdeDevFn, 4) & 0xffff:X4} BAR4 {m.Pci.ConfigRead(0, PciBus.IdeDevFn, 0x20):X8} IDETIM {m.Pci.ConfigRead(0, PciBus.IdeDevFn, 0x40):X8} UDMA {m.Pci.ConfigRead(0, PciBus.IdeDevFn, 0x48):X8}; bus-master register accesses {m.Ata.Primary.BmAccesses}/{m.Ata.Secondary.BmAccesses}");
        if (m.Ata != null) Console.WriteLine($"ide: bus-master DMA transfers {m.Ata.Primary.DmaTransfers + m.Ata.Secondary.DmaTransfers}, {(m.Ata.Primary.DmaBytes + m.Ata.Secondary.DmaBytes) / 1024} KB");
        if (m.Nic != null && m.Pci?.NicFn != null) Console.WriteLine($"nic: sent {m.Nic.FramesSent} frames, received {m.Nic.FramesReceived}; BAR0 {m.Pci!.NicFn!.Read32(0x10):X8} cmd {m.Pci.NicFn.Cfg[4]:X2} line {m.Pci.NicFn.Cfg[0x3c]} PIRQB {m.Pci.Isa.Cfg[0x61]:X2}");
        if (args.Contains("--jitstat")) Console.WriteLine($"jit: compiled {m.Cpu.JitCompiled} reused {m.Cpu.JitReused} variant-hits {m.Cpu.JitVariantHits} runs {m.Cpu.JitRuns} invalid {m.Cpu.JitInvalid} cycle {m.Cpu.Cycle} compiled-instrs {m.Cpu.JitInstrsTotal} ({100.0 * m.Cpu.JitInstrsTotal / Math.Max(1, m.Cpu.Cycle):F1}%) (set X86_JITSTAT for the per-block counts)");
        if (args.Contains("--jitstat")) Console.WriteLine($"jit: declined at entry {m.Cpu.JitDeclines} (unusable segment at the time: {string.Join(" ", m.Cpu.JitDeclineSeg)}); why: {string.Join(" ", Enumerable.Range(0, 64).Where(i => m.Cpu.DeclineWhy[i] > 0).Select(i => $"{i}={m.Cpu.DeclineWhy[i]}"))}");
        if (args.Contains("--jitstat")) { Console.WriteLine(m.Cpu.JitPipeStat()); Console.WriteLine(NesEmulator.X86.I386.JitIlKindStat()); }
        if (args.Contains("--jitstat")) foreach (var l in m.Cpu.JitTop(int.TryParse(Environment.GetEnvironmentVariable("X86_JITTOP"), out int jtn) ? jtn : 25)) Console.WriteLine("  blk " + l);
        if (PcMachine.TimeProbe) Console.WriteLine($"timeprobe: cpu batches {m.TicksCpu * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms (Exec1 {m.Cpu.TicksExec1 * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms), between batches {m.TicksDevices * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms, batches {m.Batches}");
        if (PcMachine.TimeProbe) Console.WriteLine($"timeprobe: out-of-line instructions {m.Cpu.OolCalls}, {m.Cpu.TicksOol * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms inside them");
        if (PcMachine.TimeProbe) Console.WriteLine($"timeprobe: compiled block runs {m.Cpu.JitTimedRuns}, {m.Cpu.TicksJit * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms inside them (out-of-line included); Exec1 steps {m.Cpu.Step1Calls}");
        if (PcMachine.TimeProbe)
        {
            double tf = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Console.WriteLine($"timeprobe: parts: sound card (Advance: DMA + mixing) {m.TicksCard * tf:F0} ms, VGA/S3 render {m.RenderTicks * tf:F0} ms");
            Console.WriteLine($"timeprobe: steps without a compiled block {m.Cpu.InterpCount.Sum()}, {m.Cpu.InterpTicks.Sum() * tf:F0} ms; top by time (first byte, 100 = fetch off the cached page):");
            Console.WriteLine("timeprobe: by decline path (none,invalid,failed,pending,compile,counting,new): " + string.Join(" ", Enumerable.Range(0, 7).Select(i => $"{m.Cpu.WhyCount[i]}/{m.Cpu.WhyTicks[i] * tf:F0}ms")));
            foreach (int i in Enumerable.Range(0, 257).OrderByDescending(i => m.Cpu.InterpTicks[i]).Take(16))
                Console.WriteLine($"  {(i == 256 ? "page" : i.ToString("X2"))}: {m.Cpu.InterpCount[i]} steps, {m.Cpu.InterpTicks[i] * tf:F0} ms, {m.Cpu.InterpTicks[i] * tf * 1e6 / Math.Max(1, m.Cpu.InterpCount[i]):F0} ns each");
        }
        if (args.Contains("--jitstat")) Console.WriteLine($"  exec1 steps {m.Cpu.Step1Calls} of which compiled-block entries {m.Cpu.Step1Jit}; halt batches {m.HaltBatches}");
        if (args.Contains("--jitstat")) Console.WriteLine("  linkpage nopg/pageend/limit/notlb/notag/io/ok: " + string.Join(" ", m.Cpu.LinkPageWhy));
        if (args.Contains("--jitstat")) Console.WriteLine("  linkwhy budget/depth/linked/nocode/otherpage: " + string.Join(" ", m.Cpu.LinkWhy));
        if (args.Contains("--jitstat")) Console.WriteLine("  jcond op*16+cc: " + string.Join(" ", Enumerable.Range(0, 512).Where(i => m.Cpu.JCondOps[i] > 0).OrderByDescending(i => m.Cpu.JCondOps[i]).Take(12).Select(i => $"{i / 16}/{i % 16:X}={m.Cpu.JCondOps[i]}")));
        if (args.Contains("--jitstat")) foreach (var l in m.Cpu.InterpModes()) Console.WriteLine("  interpmode " + l);
        if (args.Contains("--jitstat")) foreach (var l in m.Cpu.InterpTop(40)) Console.WriteLine("  interp " + l);
        if (args.Contains("--jitstat")) foreach (var kv in m.Cpu.InterpAt.OrderByDescending(x => x.Value).Take(30)) Console.WriteLine($"  interpat {kv.Key:X8} {kv.Value} : {string.Join(" ", Enumerable.Range(0, 8).Select(i => m.Phys8(kv.Key + (uint)i).ToString("X2")))} [{m.Cpu.JitWhy(kv.Key)}]");
        if (args.Contains("--jitstat")) foreach (var kv in m.Cpu.OolTicksAt.OrderByDescending(x => x.Value).Take(25)) Console.WriteLine($"  oolms {kv.Key:X8} {kv.Value * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms / {m.Cpu.OolAt.GetValueOrDefault(kv.Key)} : {string.Join(" ", Enumerable.Range(0, 8).Select(i => m.Phys8(kv.Key + (uint)i).ToString("X2")))}");
        if (args.Contains("--jitstat")) foreach (var kv in m.Cpu.OolAt.OrderByDescending(x => x.Value).Take(40)) Console.WriteLine($"  oolat {kv.Key:X8} {kv.Value} : {string.Join(" ", Enumerable.Range(0, 8).Select(i => m.Phys8(kv.Key + (uint)i).ToString("X2")))}");
        if (m.Voodoo != null && NesEmulator.X86.Voodoo1.Log) foreach (var kv in m.Voodoo.LogCounts.OrderByDescending(x => x.Value).Take(20)) Console.WriteLine($"  vdo {kv.Key} {kv.Value}");
        if (m.NvSysPages != null) Console.WriteLine("nvsyspages: " + string.Join(" ", m.NvSysPages.OrderBy(x => x.Key).Select(x => $"{x.Key:X5}={x.Value}")));
        if (args.Contains("--nvfifo") && m.Nv != null) Console.WriteLine("nvfifo: " + m.Nv.FifoDump());
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
        if (_mp4 != null) { _mp4.Finish(); Console.WriteLine($"x86boot: mp4 {Arg(args, "--mp4")}: {_mp4.Frames} frames, {_mp4.Seconds:F1} s, {new FileInfo(Arg(args, "--mp4")!).Length >> 20} MB"); _mp4 = null; }
        if (Arg(args, "--wav") is string wavp) WriteWav(wavp);
        if (Arg(args, "--cdwav") is string cdwp) WriteWav(cdwp, _cdWav!.ToArray());
        if (Arg(args, "--savestate") is string ss) File.WriteAllBytes(ss, m.SaveState());
        if (excKeep > 0)
        {
            Console.WriteLine("exceptions: " + string.Join(" ", Enumerable.Range(0, 32).Where(i => excCount[i] > 0).Select(i => $"#{i}={excCount[i]}")));
            foreach (var l in excRing) Console.WriteLine("  " + l);
        }
        if (Arg(args, "--save") is string save && m.Hdd[0] != null) File.WriteAllBytes(save, m.Hdd[0]!.ToArray());
        if (Arg(args, "--save2") is string save2 && m.Hdd[1] != null) File.WriteAllBytes(save2, m.Hdd[1]!.ToArray());   // the second disk (primary slave)
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
    public static string FrameWinReport(int w0, int w1)
    {
        var a = _frameMs.Skip(w0).Take(w1 - w0).ToArray(); Array.Sort(a);
        double sum = a.Sum();
        return $"framewin {w0}-{w1}: mean {sum / a.Length:F2} ms, p50 {a[a.Length / 2]:F2}, p99 {a[Math.Min(a.Length - 1, (int)(.99 * a.Length))]:F2}, over 50 ms {a.Count(v => v > 50)}, over 100 ms {a.Count(v => v > 100)}, total {sum / 1000:F2} s";
    }
    private static PcMachine? _partner; private static PcLanHub? _pairHub; private static PcNat? _pairNat;
    // --bugcapture N: the desktop's bug capture as a soak sees it: every N frames the machine's state is copied between frames (timed with
    // the frame) into one of two alternating buffers, and packed (ConsoleStateFile.Wrap: CRC + parallel deflate) on a worker; the previous pack
    // is awaited first, as BugCaptureRing does. Counts and times are printed at the end.
    private static int _capEvery, _capFrame, _capFlip, _capCount;
    private static readonly MemoryStream?[] _capBuf = new MemoryStream?[2];
    private static System.Threading.Tasks.Task<byte[]>? _capPack;
    private static readonly byte[]?[] _capKeep = new byte[]?[32];
    private static double _capCopyMs, _capCopyMax, _capWaitMs; private static long _capBytes;
    private static void CaptureTick(PcMachine m)
    {
        var sw = Stopwatch.StartNew();
        if (_capPack != null) { _capBytes = _capPack.Result.Length; }   // (the buffer's length: WrapInto may leave spare room)
        _capWaitMs += sw.Elapsed.TotalMilliseconds;
        _capFlip ^= 1;
        var ms = _capBuf[_capFlip] ??= new MemoryStream();
        ms.SetLength(0);
        m.SaveState(ms);
        var seg = new ArraySegment<byte>(ms.GetBuffer(), 0, (int)ms.Length);
        // packed into the evicted one of 32 kept buffers (as BugCaptureRing's slots reuse theirs since 8d4e62fb): memory flat, like the desktop
        int keep = _capCount % _capKeep.Length; var reuse = _capKeep[keep];
        _capPack = System.Threading.Tasks.Task.Run(() =>
        {
            var w = NesEmulator.Systems.ConsoleStateFile.WrapInto(NesEmulator.Systems.ConsoleKind.Pc, "soak", "soak", new Dictionary<string, string> { ["format"] = "x86-1" }, seg, reuse);
            _capKeep[keep] = w.Array; return w.Array!;
        });
        double t = sw.Elapsed.TotalMilliseconds; _capCopyMs += t; if (t > _capCopyMax) _capCopyMax = t; _capCount++;
        if (_capCount % 60 == 0 && Environment.GetEnvironmentVariable("X86_MEMLOG") == "1")
        {   // the soak's memory, managed vs the rest (diagnostics)
            var gi = GC.GetGCMemoryInfo(); var pr = Process.GetCurrentProcess();
            long kept = 0; foreach (var b in _capKeep) kept += b?.Length ?? 0;
            Console.WriteLine($"memlog: states {_capCount} ws {pr.WorkingSet64 >> 20} MB private {pr.PrivateMemorySize64 >> 20} MB managed heap {gi.HeapSizeBytes >> 20} MB committed {gi.TotalCommittedBytes >> 20} MB fragmented {gi.FragmentedBytes >> 20} MB loh {gi.GenerationInfo[3].SizeAfterBytes >> 20} MB kept buffers {kept >> 20} MB payload bufs {(_capBuf[0]?.Capacity ?? 0) / 1048576}+{(_capBuf[1]?.Capacity ?? 0) / 1048576} MB gc2 {GC.CollectionCount(2)}");
        }
    }
    private static void CaptureReport()
    {
        if (_capEvery <= 0) return;
        if (_capPack != null) _capBytes = _capPack.Result.Length;
        Console.WriteLine($"bugcapture: {_capCount} states every {_capEvery} frames, between-frames mean {(_capCount > 0 ? _capCopyMs / _capCount : 0):F1} ms max {_capCopyMax:F1} ms (of it waiting on the last pack {_capWaitMs:F0} ms total), last packed {_capBytes >> 20} MB");
    }

    /// <summary>X86_SLOWFRAMES=ms (diagnostic): one line per frame slower than that: GC collections / pause and JIT pipeline moves since the last frame.</summary>
    private static readonly int SlowMs = int.TryParse(Environment.GetEnvironmentVariable("X86_SLOWFRAMES"), out int sm) ? sm : 0;
    private static (int g0, int g1, int g2, double pause, long posted, int done, long clears, long alloc) _sfPrev;
    private static void SlowFrame(PcMachine m, double ms)
    {
        var c = m.Cpu;
        var now = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration().TotalMilliseconds, c.JitPosted, c.JitCompiled, c.JitKeepClears, GC.GetTotalAllocatedBytes());
        var p = _sfPrev; _sfPrev = now;
        if (ms < SlowMs || p.alloc == 0) return;
        Console.WriteLine($"slowframe {_frameMs.Count - 1}: {ms:F0} ms | gc {now.Item1 - p.g0}/{now.Item2 - p.g1}/{now.Item3 - p.g2} pause {now.Item4 - p.pause:F0} ms alloc {(now.Item8 - p.alloc) >> 20} MB | jit posted {now.Item5 - p.posted} installed {now.Item6 - p.done} keep-clears {now.Item7 - p.clears} | ws {Environment.WorkingSet >> 20} MB, host memory load {GC.GetGCMemoryInfo().MemoryLoadBytes >> 20} of {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 20} MB");
    }

    private static bool Frame(PcMachine m)
    {
        _frameSw.Restart();
        bool r = m.RunFrame();
        if (_partner != null && m != _partner) _partner.RunFrame();
        if (_capEvery > 0 && ++_capFrame % _capEvery == 0) CaptureTick(m);
        _frameMs.Add(_frameSw.Elapsed.TotalMilliseconds);
        if (SlowMs > 0) SlowFrame(m, _frameMs[^1]);
        if (_cdWav != null && m.Ata != null)
        {
            long now = (long)((Int128)m.PitNow * 44100 / Pit8254.Freq);
            if (_cdLast < 0 || now - _cdLast > 44100) _cdLast = now;
            int n = (int)Math.Min(now - _cdLast, _wavBuf.Length / 2) * 2; _cdLast += n / 2;
            if (n > 0) { Array.Clear(_wavBuf, 0, n); m.Ata.Secondary.MixCdAudio(_wavBuf, n); for (int i = 0; i < n; i++) { _cdWav.WriteByte((byte)_wavBuf[i]); _cdWav.WriteByte((byte)(_wavBuf[i] >> 8)); } }
        }
        if ((_wav != null || _mp4 != null) && m.Card is { } c)
        {
            c.Advance();
            int n = Math.Min(c.Pending, _wavBuf.Length) & ~1;
            if (n > 0)
            {
                Array.Clear(_wavBuf, 0, n); int got = c.MixInto(_wavBuf, n);
                if (_wav != null) for (int i = 0; i < got; i++) { _wav.WriteByte((byte)_wavBuf[i]); _wav.WriteByte((byte)(_wavBuf[i] >> 8)); }
                if (_mp4 != null) Mp4Audio(_wavBuf, got & ~1);
            }
        }
        if (_mp4 != null)
        {
            if (m.Card == null) { long want = (long)Math.Round((_mp4.Frames + 1) * _mp4Rate * Vga.FrameTicks / Pit8254.Freq); _mp4.AddAudio(new byte[(int)(want - _mp4Silence) * 4]); _mp4Silence = want; }   // (no sound card: a silent track)
            _mp4.AddFrame(m.Vga.Frame, m.Vga.Width, m.Vga.Height);
            if (_mp4.Frames % 4200 == 0) Console.WriteLine($"x86boot: mp4 {_mp4.Frames} frames ({_mp4.Seconds:F0} s of video)");
        }
        return r;
    }
    /// <summary>--mp4 OUT.mp4 [--mp4size WxH]: every emulated frame + the sound card's output as H.264/AAC at the VGA's true ~70.09 Hz (Media Foundation; timestamps from the machine clock, so any host speed gives a lag-free video).</summary>
    private static BrokenNes.Workshop.Movies.Mp4Writer? _mp4; private static long _mp4Silence;
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
                if (tok.StartsWith("save:")) { string svp = text.Substring(ci - tok.Length + 5, tok.Length - 5); X86Library.GuardWrite(svp, "save a state to"); File.WriteAllBytes(svp, m.SaveState()); _wavMark = _wav?.Length ?? 0; continue; }   // {save:PATH}: a savestate right here (the run goes on; --wav also writes the sound from here on as FILE.after.wav)
                if (tok.StartsWith("cd:")) { var cd = CdImage.Open(X86Library.Resolve(text.Substring(ci - tok.Length + 3, tok.Length - 3))); m.Cd = cd; if (m.Ata != null) { m.Ata.Secondary.Cd[0] = cd; m.Ata.Secondary.MediaChanged(0); } continue; }   // {cd:PATH}: change the disc (the drive reports the media change)
                if (tok.StartsWith("load:")) { var lw = System.Diagnostics.Stopwatch.StartNew(); m.LoadState(File.ReadAllBytes(X86Library.Resolve(text.Substring(ci - tok.Length + 5, tok.Length - 5)))); Console.WriteLine($"x86boot: {{load}} (warm, same process) {lw.Elapsed.TotalMilliseconds:F0} ms"); continue; }   // {load:PATH}: load a savestate right here, in this process (the run goes on from it)
                if (tok.StartsWith("until:")) { var ua = tok[6..].Split(','); var uc = ua[0].Split(">="); uint uad = Convert.ToUInt32(uc[0], 16); long uv = long.Parse(uc[1]); int umax = ua.Length > 1 ? int.Parse(ua[1]) : 100000, uw = 0; while (uw < umax && LinDword(m, uad) is long cur && (cur < 0 || cur < uv)) { Frame(m); uw++; } Console.WriteLine($"x86boot: {{until}} {uad:X8}>={uv} after {uw} frames (value {LinDword(m, uad)})"); continue; }   // {until:HEXLIN>=N[,MAX]}: run frames until the guest dword (current page tables) reaches N (scripted bots: wait for minerals)
                if (tok.StartsWith("peek:")) { foreach (var pa in tok[5..].Split(',')) { uint pad = Convert.ToUInt32(pa, 16); Console.WriteLine($"x86boot: peek {pad:X8} = {LinDword(m, pad)}"); } continue; }   // {peek:HEXLIN[,HEXLIN...]}: print guest dwords
                if (tok.StartsWith("wait:")) { int wn = int.Parse(tok[5..]); for (int q = 0; q < wn; q++) Frame(m); continue; }
                if (tok.StartsWith("typefile:")) { TypeText(m, File.ReadAllText(tok[9..]).Trim()); continue; }   // {typefile:PATH}: type a file's text (a key kept out of command lines and logs)
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

    /// <summary>--hotmod report: samples by guest module (PE image name found by walking down from the 64 KB page to an MZ/PE header), plus V86 entries and IRQ vectors per frame.</summary>
    static void HotModReport(PcMachine m, Dictionary<string, int> hot, Dictionary<uint, int> hotLin, int frames, long v86, long[] irqa, TimeSpan el)
    {
        uint R32(uint la) { uint p = LinPhys(m, la); return p == 0xffffffff ? 0xffffffff : m.Phys32(p); }
        string ModName(uint pg)
        {
            for (uint b = pg, k = 0; k < 256 && b >= 0x10000; k++, b -= 0x10000)
            {
                uint mz = R32(b); if (mz == 0xffffffff || (mz & 0xffff) != 0x5a4d) continue;
                uint pe = R32(b + 0x3c); if (pe > 0x1000 || R32(b + pe) != 0x4550) continue;
                uint size = R32(b + pe + 0x50); if (pg >= b + size) return "?";
                uint exp = R32(b + pe + 0x78); string nm = "";
                if (exp != 0 && exp != 0xffffffff) { uint na = R32(b + exp + 12); if (na != 0xffffffff) for (int i = 0; i < 32; i++) { uint p = LinPhys(m, b + na + (uint)i); if (p == 0xffffffff) break; byte c = m.Phys8(p); if (c == 0) break; nm += (char)c; } }
                return (nm == "" ? "PE" : nm) + $"@{b:X8}";
            }
            return pg >= 0xc0000000 ? "ring0 (VMM/VxDs)" : "?";
        }
        var all = new Dictionary<string, int>(hot);
        foreach (var kv in hotLin) { string k = kv.Key >= 0xc0000000 ? "ring0 (VMM/VxDs)" : ModName(kv.Key); if (k == "?") k = $"unknown {kv.Key:X8}"; all[k] = all.GetValueOrDefault(k) + kv.Value; }
        int tot = Math.Max(1, all.Values.Sum());
        Console.WriteLine($"hotmod: {frames} frames, {el.TotalSeconds:F1} s host, {tot} samples");
        foreach (var kv in all.OrderByDescending(x => x.Value).Take(40)) Console.WriteLine($"  mod {kv.Value * 100.0 / tot,5:F1}%  {kv.Key}");
        foreach (var kv in hotLin.Where(x => x.Key >= 0xc0000000).OrderByDescending(x => x.Value).Take(12)) Console.WriteLine($"  ring0 page {kv.Key:X8} {kv.Value * 100.0 / tot:F1}%");
        double f = Math.Max(1, frames);
        Console.WriteLine($"  V86 entries {v86} = {v86 / f:F1}/frame");
        Console.WriteLine("  irq vectors/frame: " + string.Join(" ", Enumerable.Range(0, 256).Where(i => m.IrqCount[i] - irqa[i] > 0).Select(i => $"{i:X2}h={(m.IrqCount[i] - irqa[i]) / f:F2}")));
    }

    /// <summary>A linear address through the page tables (when paging is on), for the tools: 0xffffffff when not present.</summary>
    static int _mp4Rate = 44100; static double _rsPos; static short _rsL, _rsR;
    /// <summary>Card sound (stereo 44.1 kHz) into the MP4, resampled linearly to --mp4rate (state carries across calls).</summary>
    static void Mp4Audio(short[] src, int count)
    {
        if (_mp4Rate == 44100) { _mp4!.AddAudio(System.Runtime.InteropServices.MemoryMarshal.AsBytes(src.AsSpan(0, count))); return; }
        int frames = count / 2; double step = 44100.0 / _mp4Rate; var o = new List<short>(frames * 2 * _mp4Rate / 44100 + 4);
        while (_rsPos < frames)
        {
            int i = (int)Math.Floor(_rsPos); double f = _rsPos - i;
            short l0 = i - 1 < 0 ? _rsL : src[(i - 1) * 2], r0 = i - 1 < 0 ? _rsR : src[(i - 1) * 2 + 1], l1 = src[i * 2], r1 = src[i * 2 + 1];
            o.Add((short)(l0 + (l1 - l0) * f)); o.Add((short)(r0 + (r1 - r0) * f)); _rsPos += step;
        }
        _rsPos -= frames; if (frames > 0) { _rsL = src[(frames - 1) * 2]; _rsR = src[(frames - 1) * 2 + 1]; }
        _mp4!.AddAudio(System.Runtime.InteropServices.MemoryMarshal.AsBytes(o.ToArray().AsSpan()));
    }
    public static uint LinPhys(PcMachine m, uint la)
    {
        if ((m.Cpu.Cr0 & 0x80000000) == 0) return la;
        uint pde = m.Phys32((m.Cpu.Cr3 & ~0xfffu) + (la >> 22) * 4); if ((pde & 1) == 0) return 0xffffffff;
        if ((pde & 0x80) != 0 && (m.Cpu.Cr4 & 0x10) != 0) return (pde & 0xffc00000) | (la & 0x3fffff);
        uint pte = m.Phys32((pde & ~0xfffu) + ((la >> 12) & 0x3ff) * 4); return (pte & 1) == 0 ? 0xffffffff : (pte & ~0xfffu) | (la & 0xfff);
    }
    /// <summary>A guest dword at a linear address (current page tables), -1 when not mapped.</summary>
    public static long LinDword(PcMachine m, uint la) { long v = 0; for (int i = 3; i >= 0; i--) { uint p = LinPhys(m, la + (uint)i); if (p == 0xffffffff) return -1; v = (v << 8) | m.Phys8(p); } return v; }
    public static string LinBytes(PcMachine m, uint la, int n) => string.Join(" ", Enumerable.Range(0, n).Select(i => { uint p = LinPhys(m, la + (uint)i); return p == 0xffffffff ? "??" : m.Phys8(p).ToString("X2"); }));

    /// <summary>The asset library (X86Library): every disk / CD / state argument may be "lib:&lt;id&gt;" (resolved to its file; disks stay copy-on-write in
    /// memory, the library file is only read); --loadstate lib:&lt;id&gt; without --cd mounts the CD the catalogue names for that state; a state's disks
    /// are found by their hash in the catalogue when no --hdd gives them. Every output argument is refused when it points inside the library.</summary>
    private static readonly string[] LibInputs = { "--hdd", "--hdd2", "--cd", "--floppy", "--loadstate", "--pair", "--pair-hdd" };
    private static readonly string[] LibOutputs = { "--save", "--save2", "--savestate", "--pair-save", "--shot", "--pair-shot", "--wav", "--cdwav", "--mp4", "--niclog" };
    private static string[] LibraryArgs(string[] args)
    {
        X86Library.Install();
        var a = (string[])args.Clone();
        string? stateId = null;
        for (int i = 0; i + 1 < a.Length; i++)
        {
            string k = a[i];
            if (Array.Exists(LibOutputs, o => o.Equals(k, StringComparison.OrdinalIgnoreCase))) X86Library.GuardWrite(a[i + 1], k + " to");
            else if (k.Equals("--get", StringComparison.OrdinalIgnoreCase) && a[i + 1].IndexOf('=') is int gi and >= 0) X86Library.GuardWrite(a[i + 1][(gi + 1)..], "--get to");
            else if (Array.Exists(LibInputs, o => o.Equals(k, StringComparison.OrdinalIgnoreCase)) && X86Library.IsLibId(a[i + 1]))
            {
                if (k.Equals("--loadstate", StringComparison.OrdinalIgnoreCase)) stateId ??= a[i + 1];
                string r = X86Library.Resolve(a[i + 1]);
                Console.WriteLine($"x86boot: {k} {a[i + 1]} -> {r}");
                a[i + 1] = r; i++;
            }
        }
        // {save:PATH} in --type / --step / --stepsfile: refused before the run starts, not after minutes of frames
        var texts = Args(a, "--type").Concat(Args(a, "--step")).ToList();
        if (Arg(a, "--stepsfile") is string sf && File.Exists(sf)) texts.AddRange(File.ReadAllLines(sf));
        foreach (var t in texts)
            foreach (System.Text.RegularExpressions.Match mt in System.Text.RegularExpressions.Regex.Matches(t, @"\{save:([^}]*)\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                X86Library.GuardWrite(mt.Groups[1].Value, "save a state ({save:}) to");
        if (stateId != null && Arg(a, "--cd") == null && X86Library.CdFor(stateId) is string cd)
        {
            Console.WriteLine($"x86boot: --cd {cd} (the CD the library names for {stateId})");
            a = a.Concat(new[] { "--cd", cd }).ToArray();
        }
        return a;
    }

    private static string? Arg(string[] a, string k) { int i = Array.FindIndex(a, x => x.Equals(k, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    private static IEnumerable<string> Args(string[] a, string k) { for (int i = 0; i + 1 < a.Length; i++) if (a[i].Equals(k, StringComparison.OrdinalIgnoreCase)) yield return a[i + 1]; }
}
