// [cer64plus] SessionProbe: drives SM64 only through N64Session, the way BrokenNes will.
//   SessionProbe <rom> check [state]          contract checks (frames, audio, state round trip, reset, battery)
//   SessionProbe <rom> golden <state> <frames> writes telemetry/framehash.txt + audio.wav (golden.sh format; set COR64_FRAMEHASH_EVERY=10)
//   SessionProbe <rom> bench <state> <frames>  wall time for N frames through RunFrame
//   SessionProbe <rom> compat <script.txt> <outdir>  scripted compatibility run -> outdir/report.json (Compat.cs, compat/README.md)
// Env: COR64_RDP=software|parallel as for RunN64.
using System;
using System.Diagnostics;
using System.IO;
using cor64;
using cor64.PIF;
using cor64.Session;

static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr OpenThread(int access, bool inherit, int id);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [System.Runtime.InteropServices.DllImport("ntdll.dll")] static extern int NtQueryInformationThread(IntPtr h, int cls, out IntPtr info, int len, IntPtr retLen);
    // PROBE_THREADS: thread start address (ThreadQuerySetWin32StartAddress) as module+offset.
    static string ThreadStart(Process proc, int tid)
    {
        IntPtr h = OpenThread(0x0040, false, tid); if (h == IntPtr.Zero) return "?";
        try {
            if (NtQueryInformationThread(h, 9, out IntPtr a, IntPtr.Size, IntPtr.Zero) != 0) return "?";
            long v = a.ToInt64();
            foreach (ProcessModule m in proc.Modules) { long b = m.BaseAddress.ToInt64(); if (v >= b && v < b + m.ModuleMemorySize) return $"{m.ModuleName}+0x{v - b:X}"; }
            return $"0x{v:X}";
        } finally { CloseHandle(h); }
    }

    static int s_Fail;
    static readonly int s_BootLog = int.TryParse(Environment.GetEnvironmentVariable("PROBE_BOOTLOG"), out var bl) ? bl : 0; // [boot_fix1] cold-boot trace
    static long s_FirstChance;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> s_FirstChanceTypes = new();
    static Program() {
        AppDomain.CurrentDomain.FirstChanceException += (o, e) => {
            System.Threading.Interlocked.Increment(ref s_FirstChance);
            var key = e.Exception.GetType().Name + " @ " + (e.Exception.TargetSite?.DeclaringType?.Name ?? "?") + "." + (e.Exception.TargetSite?.Name ?? "?");
            s_FirstChanceTypes.AddOrUpdate(key, 1, (_, v) => v + 1);
        };
    }
    static System.Collections.Generic.List<ulong> s_FrameHashes;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) s_Fail++;
    }

    static N64Session Open(string rom)
    {
        var rdp = Environment.GetEnvironmentVariable("COR64_RDP");
        var opt = new N64SessionOptions { Renderer = rdp == "software" ? N64Renderer.Software : rdp == "parallel" ? N64Renderer.Parallel : N64Renderer.Auto, Ppu = Environment.GetEnvironmentVariable("PROBE_PPU") };
        return new N64Session(File.ReadAllBytes(rom), opt);
    }

    // 32-bit top-down BMP straight from the ARGB frame, to check what a host would show.
    static void WriteBmp(string path, uint[] px, int w, int h)
    {
        using var f = new BinaryWriter(File.Create(path));
        int size = 54 + w * h * 4;
        f.Write((byte)'B'); f.Write((byte)'M'); f.Write(size); f.Write(0); f.Write(54);
        f.Write(40); f.Write(w); f.Write(-h); f.Write((short)1); f.Write((short)32); f.Write(0); f.Write(w * h * 4); f.Write(2835); f.Write(2835); f.Write(0); f.Write(0);
        for (int i = 0; i < w * h; i++) f.Write(px[i]);
    }

    static ulong Hash(uint[] px, int n, ulong h = 1469598103934665603UL)
    {
        for (int i = 0; i < n; i++) { h ^= px[i]; h *= 1099511628211UL; }
        return h;
    }

    // Runs n frames, returning a hash over every picture and every sample produced (optionally continuing earlier hashes).
    static double s_MaxFrame; static int s_Over16, s_Over33, s_MaxAt, s_FrameNo; // frame-time stats (stutter)
    static (ulong video, ulong audio, int samples) Run(N64Session s, int n, N64Pad pad = default, bool pulse = false,
        ulong hv = 1469598103934665603UL, ulong ha = 1469598103934665603UL, int total = 0)
    {
        var buf = new short[8192];
        for (int i = 0; i < n; i++) {
            var p = pad; if (pulse && i % 10 >= 5) p.Buttons = JoyController.ButtonPress.None; // dialogs need a fresh press
            s.SetPad(0, p);
            long tf0 = Stopwatch.GetTimestamp();
            s.RunFrame();
            { double fm = (Stopwatch.GetTimestamp() - tf0) * 1e3 / Stopwatch.Frequency; if (fm > s_MaxFrame) { s_MaxFrame = fm; s_MaxAt = s_FrameNo; } s_FrameNo++; if (fm > 33.3 && Environment.GetEnvironmentVariable("PROBE_SLOWLIST") == "1") Console.WriteLine($"SLOW frame={s_FrameNo - 1} ms={fm:F1}"); if (fm > 16.67) s_Over16++; if (fm > 33.3) s_Over33++; }
            hv = Hash(s.Frame, s.FrameWidth * s.FrameHeight, hv);
            s_FrameHashes?.Add(Hash(s.Frame, s.FrameWidth * s.FrameHeight));
            if (s_BootLog > 0 && s_FrameNo % s_BootLog == 0) { var cs = new System.Collections.Generic.HashSet<uint>(); int np = s.FrameWidth * s.FrameHeight; for (int q = 0; q < np; q++) cs.Add(s.Frame[q]);
                Console.WriteLine($"BOOTLOG frame={s_FrameNo} colours={cs.Count} w={s.FrameWidth} h={s.FrameHeight} ticks={Telemetry.Ticks} pc={(uint)s.System.DeviceCPU.ReadPC():X8}"); }
            int got;
            while ((got = s.ReadSamples(buf)) > 0) { total += got; for (int k = 0; k < got; k++) { ha ^= (ushort)buf[k]; ha *= 1099511628211UL; } }
        }
        return (hv, ha, total);
    }

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: SessionProbe <rom> check [state] | golden <state> <frames> | bench <state> <frames>"); return 2; }
        string rom = args[0], mode = args[1];
        if (mode == "compat") return CompatMode.Run(args); // compat harness (Compat.cs); writes only under its outdir
        Directory.CreateDirectory("telemetry");
        using var s = Open(rom);
        Console.WriteLine($"title='{s.Title}' id={s.GameId} fps={s.FramesPerSecond} {s.Description}");

        if (mode == "cpumicro") {
            s.LoadState(File.ReadAllBytes(args[2]));
            var cpu = (cor64.Mips.R4300I.Interpreter)s.System.DeviceCPU;
            if (args[3] == "parts") { foreach (var (nm, ns) in cpu.MicroParts(10_000_000)) Console.WriteLine($"CPUPART {nm,-38} {ns,6:F1} ns"); return 0; }
            if (args[3] == "recloop") { Console.WriteLine($"CPUREC loop: {cpu.MicroRecLoop(5_000_000, args.Length > 4 ? args[4] : "addiu"):F1} ns per step"); return 0; }
            if (args[3] == "steploop") { Console.WriteLine($"CPUSTEP loop of addiu: {cpu.MicroStepLoop(5_000_000):F1} ns per Step()"); return 0; }
            foreach (var hex in args[3].Split(',')) {
                var r = cpu.MicroBench(Convert.ToUInt32(hex, 16), 2_000_000);
                Console.WriteLine($"CPUMICRO {r.name,-10} {hex} handler={r.handlerNs:F1}ns");
            }
            return 0;
        }

        if (mode == "micro") {
            s.LoadState(File.ReadAllBytes(args[2]));
            var rsp = (cor64.Mips.Rsp.RspInterpreter)s.System.DeviceRcp.DeviceRsp;
            foreach (var hex in args[3].Split(',')) {
                var r = rsp.MicroBench(Convert.ToUInt32(hex, 16), 5_000_000);
                Console.WriteLine($"MICRO {r.name,-8} {hex} handler={r.handlerNs:F1}ns step={r.stepNs:F1}ns");
            }
            return 0;
        }

        if (mode == "golden" || mode == "bench") {
            if (args[2] != "boot") s.LoadState(File.ReadAllBytes(args[2])); // "boot": measure from power-on (no savestate)
            int frames = int.Parse(args[3]);
            if (mode == "bench") {
                // Pin to one core at high priority (less scheduler/clock jitter), and warm up so JIT compilation is not timed.
                // PROBE_PIN=1: pin to one logical CPU (old default; parallel-rdp's worker threads then share the emulator's
                // CPU, ~25% slower than any real host). Default: unpinned, as RunN64 and BrokenNes run.
                // PROBE_LOWEND=1: 7th-gen i3 shape = 4 logical CPUs (2 cores with SMT); launch with DOTNET_EnableAVX512F=0 (lowend.sh)
                try { var me = Process.GetCurrentProcess();
                      if (Environment.GetEnvironmentVariable("PROBE_LOWEND") == "1") // PROBE_AFFINITY=<hex> picks which 4 CPUs (abpair.sh)
                          me.ProcessorAffinity = (IntPtr)(Environment.GetEnvironmentVariable("PROBE_AFFINITY") is string af ? Convert.ToInt64(af, 16) : 0xF);
                      else if (Environment.GetEnvironmentVariable("PROBE_PIN") == "1") me.ProcessorAffinity = (IntPtr)(1L << 2);
                      me.PriorityClass = ProcessPriorityClass.High; } catch { }
                Run(s, int.TryParse(Environment.GetEnvironmentVariable("PROBE_WARMUP"), out var wu) ? wu : 120);
                cor64.N64System.ResetSplit();
            }
            if (mode == "golden" && int.TryParse(Environment.GetEnvironmentVariable("COR64_WAV"), out int wav) && wav > 0) ((AiDac)s.System.Apu).OpenWav("telemetry/audio.wav", wav);
            long t0 = Telemetry.Ticks, i0 = cor64.Mips.R4300I.Interpreter.IdleSkippedSteps;
            long rb0 = cor64.Mips.Rsp.RspInterpreter.JitBlockRuns, fb0 = cor64.Mips.Rsp.RspInterpreter.JitFallbackSteps;
            long tc0 = N64System.s_TCpu, tr0 = N64System.s_TRsp, ti0 = N64System.s_TIo, rs0 = cor64.Mips.Rsp.RspInterpreter.RspSteps;
            long alloc0 = GC.GetTotalAllocatedBytes(); int g0 = GC.CollectionCount(0), g2 = GC.CollectionCount(2);
            long exc0 = s_FirstChance, lock0 = System.Threading.Monitor.LockContentionCount;
            long jit0 = System.Runtime.JitInfo.GetCompiledMethodCount(false);
            var jitT0 = System.Runtime.JitInfo.GetCompilationTime(false);
            var proc = Process.GetCurrentProcess();
            var cpu0 = proc.TotalProcessorTime;
            var thr0 = new System.Collections.Generic.Dictionary<int, double>();
            foreach (ProcessThread t in proc.Threads) try { thr0[t.Id] = t.TotalProcessorTime.TotalMilliseconds; } catch { }
            { // PROBE_PIN=main:others (hex masks), measurement only
                var pin = Environment.GetEnvironmentVariable("PROBE_PIN");
                if (pin != null) {
                    var parts = pin.Split(':'); long mm = Convert.ToInt64(parts[0], 16), om = Convert.ToInt64(parts[1], 16);
#pragma warning disable CS0618
                    int me = AppDomain.GetCurrentThreadId();
#pragma warning restore CS0618
                    foreach (ProcessThread t in proc.Threads) try { t.ProcessorAffinity = (IntPtr)(t.Id == me ? mm : om); } catch { }
                }
            }
            var lm0 = LoadProofMetric.Take();
            var sw = Stopwatch.StartNew();
            var r = Run(s, frames);
            double ms = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine(LoadProofMetric.Report(lm0, LoadProofMetric.Take(), frames));
            proc.Refresh();
            Console.WriteLine($"cpums={(proc.TotalProcessorTime - cpu0).TotalMilliseconds:F0}");
            if (Environment.GetEnvironmentVariable("PROBE_THREADS") == "1")
                foreach (ProcessThread t in proc.Threads) try { double d = t.TotalProcessorTime.TotalMilliseconds - (thr0.TryGetValue(t.Id, out var v) ? v : 0); if (d > 50) Console.WriteLine($"THREAD id={t.Id} ms={d:F0} prio={t.CurrentPriority} start={ThreadStart(proc, t.Id)}"); } catch { }
            Console.WriteLine($"STUTTER maxFrameMs={s_MaxFrame:F1} at={s_MaxAt} over16ms={s_Over16} over33ms={s_Over33} (incl. warm-up) bgCompiled={cor64.Mips.R4300I.Interpreter.CpuRecBgCompiled} emitMs={cor64.Mips.R4300I.Interpreter.BgEmitTicks * 1e3 / Stopwatch.Frequency:F0} jitMs={cor64.Mips.R4300I.Interpreter.BgJitTicks * 1e3 / Stopwatch.Frequency:F0} ilKB={cor64.Mips.R4300I.Interpreter.BgIlBytes / 1024} rspBg={cor64.Mips.Rsp.RspInterpreter.RecBgCompiled}");
            Console.WriteLine($"FRAME msPerFrame={ms / frames:F2} budget60=16.67 realtime={16.667 / (ms / frames) * 100:F0}% avx512={System.Runtime.Intrinsics.X86.Avx512F.IsSupported}");
            Console.WriteLine($"cpuTicks={Telemetry.Ticks - t0} idleSkipped={cor64.Mips.R4300I.Interpreter.IdleSkippedSteps - i0} rspBlockRuns={cor64.Mips.Rsp.RspInterpreter.JitBlockRuns - rb0} rspFallback={cor64.Mips.Rsp.RspInterpreter.JitFallbackSteps - fb0}");
            { double f = Stopwatch.Frequency / 1e9; long ci = (Telemetry.Ticks - t0) - (cor64.Mips.R4300I.Interpreter.IdleSkippedSteps - i0); long rsn = cor64.Mips.Rsp.RspInterpreter.RspSteps - rs0;
              Console.WriteLine($"SPLIT cpuMs={(N64System.s_TCpu - tc0) / f / 1e6:F0} rspMs={(N64System.s_TRsp - tr0) / f / 1e6:F0} ioMs={(N64System.s_TIo - ti0) / f / 1e6:F0} cpuInstr={ci} rspSteps={rsn} cpuNs={(N64System.s_TCpu - tc0) / f / ci:F1} rspNs={(N64System.s_TRsp - tr0) / f / rsn:F1}"); }
            { double f = Stopwatch.Frequency / 1e3; Console.WriteLine($"BUSY batches={N64System.s_BusyBatches} rspHalted={N64System.s_BusyHaltedBatches} (incl. warm-up)"); Console.WriteLine($"SPAN cpuRealMs={N64System.s_TCpuReal / f:F0} totalMs={N64System.s_TSpan / f:F0} rspMs={N64System.s_TSpanRsp / f:F0} (incl. warm-up)"); Console.WriteLine($"RSPXFER spMs={N64System.s_TRspSp / f:F0} dpMs={N64System.s_TRspDp / f:F0} syncWaitMs={cor64.Rdp.ParallelRdp.WaitTicks / f:F0} scanWaitMs={cor64.Rdp.ParallelRdp.ScanWaitTicks / f:F0} scanSkips={cor64.Rdp.ParallelRdp.ScanSkips} cmds={cor64.Rdp.ParallelRdp.Commands} syncs={cor64.Rdp.ParallelRdp.FullSyncs} (incl. warm-up)"); }
            { double f = Stopwatch.Frequency / 1e3; Console.WriteLine($"IDLE batches={N64System.s_IdleBatches} totalMs={N64System.s_TIdle / f:F0} rspPartMs={N64System.s_TIdleRsp / f:F0} spans={N64System.IdleSpans} spanBatches={N64System.IdleSpanBatches} nsPerBatch={N64System.s_TIdle * 1e9 / Stopwatch.Frequency / Math.Max(1, N64System.s_IdleBatches):F0} (incl. warm-up)"); }
            if (cor64.Mips.R4300I.Interpreter.s_OpMix)
            {
                var mix = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, long>>(cor64.Mips.R4300I.Interpreter.OpMix);
                mix.Sort((a, c) => c.Value.CompareTo(a.Value));
                for (int mi = 0; mi < Math.Min(25, mix.Count); mi++) Console.WriteLine($"CPUOPMIX {mix[mi].Key} {mix[mi].Value}");
            }
            if (cor64.Mips.R4300I.Interpreter.s_StepTiming) { double f = Stopwatch.Frequency / 1e3; Console.WriteLine($"STEPT entryMs={cor64.Mips.R4300I.Interpreter.TEntry / f:F0} blockMs={cor64.Mips.R4300I.Interpreter.TBlock / f:F0} interpMs={cor64.Mips.R4300I.Interpreter.TInterp / f:F0} blockSteps={cor64.Mips.R4300I.Interpreter.TBlockSteps} firstCallMs={cor64.Mips.R4300I.Interpreter.TFirst / f:F0} firstCalls={cor64.Mips.R4300I.Interpreter.TFirstN}"); Console.Write(cor64.Mips.R4300I.Interpreter.TimedReport(25)); }
            if (cor64.N64System.s_SplitTiming) { double f = Stopwatch.Frequency / 1e3; double wall = (Stopwatch.GetTimestamp() - cor64.N64System.SplitT0) / f;
                Console.WriteLine($"SPLIT2 wallMs={wall:F0} spanMs={cor64.N64System.SplitSpan / f:F0} spanRspMs={cor64.N64System.SplitSpanRsp / f:F0} spanBatches={cor64.N64System.SplitSpanBatches} spanHalted={cor64.N64System.SplitSpanHaltedBatches} busyBatches={cor64.N64System.SplitBusyBatches} busyHalted={cor64.N64System.SplitBusyHaltedBatches}"); }
            if (cor64.Mips.R4300I.Interpreter.s_X64) {
                var I = typeof(cor64.Mips.R4300I.Interpreter);
                Console.WriteLine($"X64 blocks={cor64.Mips.R4300I.Interpreter.X64Blocks} rejected={cor64.Mips.R4300I.Interpreter.X64Rejected} calls={cor64.Mips.R4300I.Interpreter.X64Calls} steps={cor64.Mips.R4300I.Interpreter.X64Steps} reentries={cor64.Mips.R4300I.Interpreter.X64Reentries} codeBytes={cor64.Mips.X64.ExecMem.BytesUsed} freed={cor64.Mips.X64.ExecMem.Freed} reused={cor64.Mips.X64.ExecMem.Reused} linkedSwitch={cor64.Mips.R4300I.Interpreter.X64Linked} linksMade={cor64.Mips.R4300I.Interpreter.X64LinkMade}");
                unsafe { if (cor64.Mips.R4300I.Interpreter.s_Cat17Cnt) Console.WriteLine($"CAT17CNT chain={cor64.Mips.R4300I.Interpreter.s_Cat17CntBuf[0]} words={cor64.Mips.R4300I.Interpreter.s_Cat17CntBuf[1]}"); }
                if (cor64.Mips.R4300I.Interpreter.s_X64Exits) Console.WriteLine("X64EXITS exit/done/check/slow/dslow/muldiv/stale = " + string.Join(" ", cor64.Mips.R4300I.Interpreter.X64ExitCount) + " qhist(take,<16,<256,..)=" + string.Join(" ", cor64.Mips.R4300I.Interpreter.X64QHist) + " entryQ0(invalid,mi,elapsed)=" + string.Join(" ", cor64.Mips.R4300I.Interpreter.X64QZero) + " interp=" + cor64.Mips.R4300I.Interpreter.CpuRecInterp + " qkeep(invalid,valid,unsafe)=" + string.Join(" ", cor64.Mips.R4300I.Interpreter.QKeep));
                var rj = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, long>>(cor64.Mips.R4300I.Interpreter.X64RejectOps);
                rj.Sort((a, c) => c.Value.CompareTo(a.Value));
                if (cor64.Mips.R4300I.Interpreter.s_X64SlowOps) { var so = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, long>>(cor64.Mips.R4300I.Interpreter.X64SlowOpCount); so.Sort((a, c) => c.Value.CompareTo(a.Value)); Console.WriteLine("X64SLOW " + string.Join(" ", so.GetRange(0, Math.Min(20, so.Count)).ConvertAll(kv => kv.Key + "=" + kv.Value))); }
                Console.WriteLine("X64REJECT " + string.Join(" ", rj.GetRange(0, Math.Min(20, rj.Count)).ConvertAll(kv => kv.Key + "=" + kv.Value)));
            }
            Console.WriteLine($"CPUREC compiled={cor64.Mips.R4300I.Interpreter.CpuRecCompiled} steps={cor64.Mips.R4300I.Interpreter.CpuRecSteps} calls={cor64.Mips.R4300I.Interpreter.CpuRecCalls} interpSteps={cor64.Mips.R4300I.Interpreter.CpuRecInterp}");
            if (cor64.Mips.R4300I.Interpreter.s_OpStats) {
                var oc = cor64.Mips.R4300I.Interpreter.OpCounts; long tot = 0; foreach (var x in oc) tot += x;
                var idx = new System.Collections.Generic.List<int>(); for (int i = 0; i < oc.Length; i++) if (oc[i] > 0) idx.Add(i);
                idx.Sort((x, y) => oc[y].CompareTo(oc[x]));
                foreach (var i in idx.GetRange(0, Math.Min(40, idx.Count))) Console.WriteLine($"CPUOP {cor64.Mips.R4300I.Interpreter.OpNames[i],-8} {100.0 * oc[i] / tot,6:F2}%");
            }
            if (cor64.Mips.Rsp.RspInterpreter.s_C0Hist) {
                var hh = cor64.Mips.Rsp.RspInterpreter.C0Hist; for (int i = 0; i < 16; i++) if (hh[i] > 0) Console.WriteLine($"C0W reg{i} {hh[i]}");
                var ph = cor64.Mips.Rsp.RspInterpreter.C0PcHist; for (int i = 0; i < 1024; i++) if (ph[i] > 5000) Console.WriteLine($"C0PC {0x1000 + i * 4:X4} {ph[i]}");
            }
            if (cor64.Mips.Rsp.RspInterpreter.s_RspTiming) { double f = 1000.0 / Stopwatch.Frequency; var R = typeof(cor64.Mips.Rsp.RspInterpreter);
                Console.WriteLine($"RSPT runCreditMs={cor64.Mips.Rsp.RspInterpreter.TRunCredit * f:F0} recMs={cor64.Mips.Rsp.RspInterpreter.TRec * f:F0} emptyPairMs={cor64.Mips.Rsp.RspInterpreter.TEmpty * f:F0} recCalls={cor64.Mips.Rsp.RspInterpreter.NRec} runs={cor64.Mips.Rsp.RspInterpreter.NRun} self={cor64.Mips.Rsp.RspInterpreter.NSelf} cont={cor64.Mips.Rsp.RspInterpreter.NCont} interpBlk={cor64.Mips.Rsp.RspInterpreter.NInterpBlk} interpSteps={cor64.Mips.Rsp.RspInterpreter.NInterpSteps} rejected={cor64.Mips.Rsp.RspInterpreter.RecRejected} compiled={cor64.Mips.Rsp.RspInterpreter.RecCompiled}"); }
            if (cor64.Mips.Rsp.RspInterpreter.DpSpinSkips > 0) Console.WriteLine($"RSPDPSPIN skips={cor64.Mips.Rsp.RspInterpreter.DpSpinSkips} runs={cor64.Mips.Rsp.RspInterpreter.DpSpinRuns}"); // [bt_speed]
            if (Environment.GetEnvironmentVariable("PROBE_RDPGUARD") == "1") Console.WriteLine($"RDPGUARD hits={cor64.Rdp.RdpGuard.Hits} dmaHits={cor64.Rdp.RdpGuard.DmaHits} waits={cor64.Rdp.ParallelRdp.GuardWaits} midFlushes={cor64.Rdp.ParallelRdp.MidFlushes} syncs={cor64.Rdp.ParallelRdp.FullSyncs} scanSkips={cor64.Rdp.ParallelRdp.ScanSkips} pwaits={cor64.Rdp.ParallelRdp.PartialWaits} gwaits={cor64.Rdp.ParallelRdp.GenWaits} gskips={cor64.Rdp.ParallelRdp.GenSkips} guardWaitMs={cor64.Rdp.ParallelRdp.GuardWaitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0}"); // [rdp_readback]
            if (cor64.Mips.Rsp.RspInterpreter.s_RspTiming) {
                Console.WriteLine($"DPH calls={cor64.Rdp.ParallelRdp.HCalls} handlerMs={cor64.Rdp.ParallelRdp.HTicks * 1000.0 / Stopwatch.Frequency:F0} enqMs={cor64.Rdp.ParallelRdp.EnqTicks * 1000.0 / Stopwatch.Frequency:F0} syncWaitMs={cor64.Rdp.ParallelRdp.WaitTicks * 1000.0 / Stopwatch.Frequency:F0} cmds={cor64.Rdp.ParallelRdp.Commands}");
                Console.WriteLine($"SPDMA n={cor64.RCP.SPInterface.DmaN} ms={cor64.RCP.SPInterface.DmaTicks * 1000.0 / Stopwatch.Frequency:F0} bytes={cor64.RCP.SPInterface.DmaBytes} fastRows={cor64.RCP.SPInterface.DmaFastRows} slowRows={cor64.RCP.SPInterface.DmaSlowRows}");
                double f = 1e9 / Stopwatch.Frequency; var bl = cor64.Mips.Rsp.RspInterpreter.AllBlocks; bl.Sort((x, y) => y.TTicks.CompareTo(x.TTicks));
                long all = 0; foreach (var x in bl) all += x.TTicks;
                foreach (var x in bl.GetRange(0, Math.Min(25, bl.Count))) {
                    var ops = string.Join(" ", System.Linq.Enumerable.Select(x.Insts, d => d.Op.Op));
                    Console.WriteLine($"RSPBLK {x.StartPc:X3} {100.0 * x.TTicks / all,5:F1}% runs={x.TRuns} len={x.Length} nsPerRun={x.TTicks * f / x.TRuns:F0} nsPerStep={x.TTicks * f / Math.Max(1, x.TSteps):F1} | {ops}" + (Environment.GetEnvironmentVariable("PROBE_RSPRAW") == "1" ? " | " + string.Join(" ", System.Linq.Enumerable.Select(x.Raw, w => w.ToString("X8"))) : ""));
                }
            }
            if (cor64.Mips.Rsp.RspInterpreter.s_RspTiming) { var R = cor64.Mips.Rsp.RspInterpreter.IWhyBlk; var S = cor64.Mips.Rsp.RspInterpreter.IWhySteps;
                string[] nm = { "noRec/notMax", "hasRec?", "firstRun", "belowThreshold", "rejected", "queued" };
                for (int i = 0; i < 6; i++) Console.WriteLine($"RSPIWHY {nm[i]} blk={R[i]} steps={S[i]}");
                Console.WriteLine($"RSPIWHY jitCompiles={cor64.Mips.Rsp.RspInterpreter.NVariantCompiles} distinctInterpBlocks={cor64.Mips.Rsp.RspInterpreter.IBlocks.Count}");
                double f = 1e9 / Stopwatch.Frequency; var bl = cor64.Mips.Rsp.RspInterpreter.IBlocks; bl.Sort((x, y) => y.ISteps.CompareTo(x.ISteps));
                foreach (var x in bl.GetRange(0, Math.Min(30, bl.Count))) {
                    var ops = string.Join(" ", System.Linq.Enumerable.Select(x.Insts, d => d.Op.Op));
                    Console.WriteLine($"RSPIBLK {x.StartPc:X3} iruns={x.IRuns} isteps={x.ISteps} runs={x.Runs} len={x.Length} tried={x.RecTried} rec={(x.Rec != null)} why={x.RejWhy} nsPerStep={x.ITicks * f / Math.Max(1, x.ISteps):F1} | {ops}");
                } }
            if (cor64.Mips.Rsp.RspInterpreter.s_OpMix) {
                var mix = cor64.Mips.Rsp.RspInterpreter.OpMix; long tot = 0; foreach (var x in mix) tot += x;
                var idx = new System.Collections.Generic.List<int>(); for (int i = 0; i < mix.Length; i++) if (mix[i] > 0) idx.Add(i);
                idx.Sort((x, y) => mix[y].CompareTo(mix[x]));
                foreach (var i in idx.GetRange(0, Math.Min(60, idx.Count))) Console.WriteLine($"RSPOP {cor64.Mips.Rsp.RspInterpreter.OpMixName[i],-8} {100.0 * mix[i] / tot,6:F2}%");
            }
            // [idle_speed] PROBE_DIS=<hexPhys>:<words>[,...]: disassemble RDRAM after the bench (measurement only)
            if (Environment.GetEnvironmentVariable("PROBE_DIS") is string dis) {
                var da = new cor64.Mips.R4300I.Disassembler("o32");
                foreach (var part in dis.Split(',')) { var pp = part.Split(':'); long a0 = Convert.ToInt64(pp[0], 16) & 0x1FFFFFFF; int nw = int.Parse(pp[1]);
                    for (int k = 0; k < nw; k++) { uint w = s.System.SystemMemory.U32(a0 + k * 4); string txt; try { txt = da.Disassemble(da.Decode(w)); } catch { txt = "?"; } Console.WriteLine($"DIS {0x80000000 | (a0 + k * 4):X8} {w:X8} {txt}"); } }
            }
            if (cor64.Mips.R4300I.Interpreter.s_PcHist) {
                var h = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<uint, long>>(cor64.Mips.R4300I.Interpreter.PcHist);
                h.Sort((x, y) => y.Value.CompareTo(x.Value)); long tot = 0; foreach (var kv in h) tot += kv.Value;
                var dah = new cor64.Mips.R4300I.Disassembler("o32");
                foreach (var kv in h.GetRange(0, Math.Min(int.TryParse(Environment.GetEnvironmentVariable("PROBE_PCHIST_N"), out var pn) ? pn : 40, h.Count))) {
                    cor64.Mips.R4300I.Interpreter.PcInst.TryGetValue(kv.Key, out uint ins); string txt; try { txt = dah.Disassemble(dah.Decode(ins)); } catch { txt = "?"; }
                    Console.WriteLine($"CPUPC {kv.Key:X8} {100.0 * kv.Value / tot,6:F2}% {ins:X8} {txt}"); }
            }
            Console.WriteLine($"POLL found={cor64.Mips.R4300I.Interpreter.PollFound} recFail={cor64.Mips.R4300I.Interpreter.PollRecFail} spans={cor64.Mips.R4300I.Interpreter.PollSpans} why={string.Join(",", cor64.Mips.R4300I.Interpreter.PollFailWhy)} match={string.Join(",", cor64.Mips.R4300I.Interpreter.MatchWhy)} (incl. warm-up)");
            if (cor64.Mips.R4300I.Interpreter.ViWaitPc != 0) Console.WriteLine($"VIWAIT batches={cor64.Mips.R4300I.Interpreter.ViWaitBatches} skipped={cor64.Mips.R4300I.Interpreter.ViWaitSkipped}"); // [speed_cat9]
            Console.WriteLine($"REDFLAGS exceptions={s_FirstChance - exc0} lockContention={System.Threading.Monitor.LockContentionCount - lock0} jitMethods={System.Runtime.JitInfo.GetCompiledMethodCount(false) - jit0} jitMs={(System.Runtime.JitInfo.GetCompilationTime(false) - jitT0).TotalMilliseconds:F0}");
            if (s_FirstChanceTypes.Count > 0) foreach (var kv in s_FirstChanceTypes) Console.WriteLine($"EXCEPTION {kv.Key} x{kv.Value}");
            Console.WriteLine($"allocMB={(GC.GetTotalAllocatedBytes() - alloc0) / 1048576.0:F1} gen0={GC.CollectionCount(0) - g0} gen2={GC.CollectionCount(2) - g2}");
            Console.WriteLine($"outputHash video={r.video:X16} audio={r.audio:X16}");
            Console.WriteLine($"frames={frames} ms={ms:F0} fps={frames * 1000.0 / ms:F2} ({frames * 100000.0 / ms / s.FramesPerSecond:F1}% of {s.FramesPerSecond} fps) samples={r.samples}");
            return 0;
        }

        // ---- contract checks ----
        var r0 = Run(s, 120);
        Check(s.FrameWidth > 0 && s.FrameHeight > 0 && s.Frame.Length >= s.FrameWidth * s.FrameHeight, $"frame {s.FrameWidth}x{s.FrameHeight}, display width {s.DisplayWidth}");
        Check(r0.samples > 0, $"audio after boot: {r0.samples / 2} stereo frames at {s.SampleRate} Hz");

        if (args.Length > 2) s.LoadState(File.ReadAllBytes(args[2]));
        Run(s, 30);
        var sw2 = Stopwatch.StartNew();
        var state = s.SaveState();
        Console.WriteLine($"     state {state.Length / 1024} KB, save {sw2.ElapsedMilliseconds} ms");
        { var drain = new short[8192]; while (s.ReadSamples(drain) > 0) { } } // sound from before the snapshot
        s_FrameHashes = new(); var a = Run(s, 120); var ha = s_FrameHashes;
        sw2.Restart(); s.LoadState(state); long loadMs = sw2.ElapsedMilliseconds;
        s_FrameHashes = new(); var b = Run(s, 120); var hb = s_FrameHashes; s_FrameHashes = null;
        var diff = new System.Collections.Generic.List<int>(); for (int i = 0; i < ha.Count; i++) if (ha[i] != hb[i]) diff.Add(i);
        if (diff.Count > 0) Console.WriteLine($"     {diff.Count} of {ha.Count} frames differ; first ones: {string.Join(",", diff.GetRange(0, Math.Min(12, diff.Count)))}");
        s.LoadState(state);
        var c = Run(s, 120);
        Check(b.video == c.video && b.audio == c.audio, "two replays from the same loaded state are bit-identical");
        Check(a.video == b.video && a.audio == b.audio && a.samples == b.samples, $"save/load/replay 120 frames bit-identical (load {loadMs} ms, {a.samples / 2} stereo frames;" +
            $" video {(a.video == b.video ? "same" : "differs")}, audio {(a.audio == b.audio ? "same" : "differs")}, samples {a.samples}/{b.samples})");

        WriteBmp("telemetry/session_frame.bmp", s.Frame, s.FrameWidth, s.FrameHeight);
        bool lit = false; for (int i = 0; i < s.FrameWidth * s.FrameHeight; i++) if ((s.Frame[i] & 0xFFFFFF) != 0) { lit = true; break; }
        Check(lit, "picture is not blank");

        // Input reaches the game: pressing Start/A on the title changes what is drawn versus not pressing.
        s.LoadState(state);
        // Cutscenes (the intro camera on the castle grounds) ignore input for a while, so allow up to 900 frames.
        int inputFrames = int.TryParse(Environment.GetEnvironmentVariable("PROBE_INPUT_FRAMES"), out var pif) ? pif : 900;
        var idle = Run(s, inputFrames);
        WriteBmp("telemetry/input_idle.bmp", s.Frame, s.FrameWidth, s.FrameHeight);
        s.LoadState(state);
        var pressed = Run(s, inputFrames, new N64Pad(JoyController.ButtonPress.A, 0, 70), pulse: true);
        WriteBmp("telemetry/input_pressed.bmp", s.Frame, s.FrameWidth, s.FrameHeight);
        Check(idle.video != pressed.video, $"input changes the game (A pulsed + stick up vs nothing, {inputFrames} frames)");

        var save = s.ExportSave();
        Check(save.Length == 512, $"battery save is {save.Length} bytes (4 Kbit EEPROM)");
        var marked = (byte[])save.Clone(); marked[511] ^= 0x5A;
        s.ImportSave(marked);
        s.Reset();
        Check(s.ExportSave()[511] == marked[511], "reset keeps the battery save");
        var afterReset = Run(s, 60);
        Check(afterReset.samples >= 0 && s.FrameWidth > 0, "runs after reset");
        s.ImportSave(save);

        bool refused = false; try { s.LoadState(new byte[64]); } catch (InvalidDataException) { refused = true; } catch (EndOfStreamException) { refused = true; }
        Check(refused, "damaged state is refused");
        Check(!s.TrySwapCore(cor64.Cores.N64Slot.Cpu, "COR64") && s.SwapRefusal(cor64.Cores.N64Slot.Cpu, "COR64").Length > 0, "CPU core swap refused with a reason");

        // PPU live swap: CPU/RSP/RDRAM carry on exactly, so the sound must match a run without the swap.
        s.LoadState(state);
        var straight = Run(s, 60);
        s.LoadState(state);
        var first = Run(s, 30);
        string from = s.Ppu.Id, to = from == cor64.Cores.N64Cores.PpuParallel ? cor64.Cores.N64Cores.PpuSoftware : cor64.Cores.N64Cores.PpuParallel;
        if (cor64.Cores.N64Cores.IsAvailable(cor64.Cores.N64Slot.Ppu, to)) {
            bool swapped = s.TrySwapCore(cor64.Cores.N64Slot.Ppu, to) && s.Ppu.Id == to;
            var rest = Run(s, 30, default, false, first.video, first.audio, first.samples);
            bool litAfter = false; for (int i = 0; i < s.FrameWidth * s.FrameHeight; i++) if ((s.Frame[i] & 0xFFFFFF) != 0) { litAfter = true; break; }
            Check(swapped && rest.audio == straight.audio && rest.samples == straight.samples && litAfter,
                $"PPU live swap {from} -> {to}: sound bit-identical to no swap, picture shown");
            WriteBmp("telemetry/after_swap.bmp", s.Frame, s.FrameWidth, s.FrameHeight);
            bool back = s.TrySwapCore(cor64.Cores.N64Slot.Ppu, from) && s.Ppu.Id == from;
            Run(s, 10);
            Check(back, $"PPU swap back {to} -> {from}");
        }
        Check(s.TrySwapCore(cor64.Cores.N64Slot.Apu, cor64.Cores.N64Cores.ApuAi), "APU core swap (AI)");

        // Two sessions in one process, interleaved frame by frame, must each match a solo run: no shared emulation state.
        s.LoadState(state);
        var solo = Run(s, 60);
        using (var s2 = Open(rom)) {
            s.LoadState(state); s2.LoadState(state);
            ulong v1 = 1469598103934665603UL, a1 = v1, v2 = v1, a2 = v1; int n1 = 0, n2 = 0;
            for (int f = 0; f < 60; f++) {
                var r1 = Run(s, 1, default, false, v1, a1, n1); v1 = r1.video; a1 = r1.audio; n1 = r1.samples;
                var r2 = Run(s2, 1, default, false, v2, a2, n2); v2 = r2.video; a2 = r2.audio; n2 = r2.samples;
            }
            Check(v1 == solo.video && a1 == solo.audio && v2 == solo.video && a2 == solo.audio,
                "two sessions in one process, interleaved, each bit-identical to a solo run");
        }

        Console.WriteLine(s_Fail == 0 ? "ALL PASS" : $"{s_Fail} FAILED");
        return s_Fail == 0 ? 0 : 1;
    }
}
