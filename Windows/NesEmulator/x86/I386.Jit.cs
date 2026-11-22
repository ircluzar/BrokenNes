// Intel 80386 / 80486 core: the block compiler (our own addition, not in tiny386).
//
// A hot address (reached JitHot times) gets the region of everyday instructions around it (DcDecode's forms, I386.Cache.cs) that it
// reaches by falling through and by near jumps, up to JitMaxInstr instructions, compiled to one .NET method (System.Reflection.Emit,
// then the .NET JIT): the decode is done once, operands are constants, registers / RAM / the lazy flags are read and written inline
// (registers as the bytes of R[], RAM as the bytes of Mem[]), jumps inside the region stay inside the method. Every instruction of a
// region is an entry point, so a batch that ends in the middle of one resumes in the same code.
//
// The compiled code does exactly what Exec1 would do, instruction by instruction: the same state changes in the same order (the same
// lazy-flag fields, the same reads of memory-mapped I/O, the out-of-line forms through the interpreter's own handlers), the same
// instruction clock (Cycle) and step budget (_steps), the same CMP / TEST + JZ / JNZ folding and spin skip, the same Ip / NextIp at
// every exit. It leaves whenever it cannot be sure: at the end of the step budget, on a jump out of the region, after a store into the
// region's bytes, on a fault (the state then is the interpreter's at that fault). Before each run the region's bytes are compared with
// memory (any write to code, by anyone, is seen: the region is compiled again when hot again), and with paging on its data accesses go through the TLB
// (I386.cs _tlbTag), and it runs only when every segment it uses has a usable selector (so no address translation inside it can fault). Off when the runtime cannot
// compile code (AOT), or with the environment variable X86_NOJIT set (X86_JITMAX / X86_JITHOT / X86_JITSTAT tune and count it).

using System.Reflection;
using System.Reflection.Emit;
using Label = System.Reflection.Emit.Label;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NesEmulator.X86;

public sealed partial class I386
{
    private delegate int JitFn(I386 c, int entry);

    /// <summary>A compiled block: its bytes (checked before every run) and its code, which can be entered at any of its instructions.</summary>
    private sealed class JitCode
    {
        public uint Start;
        public byte[] Bytes = Array.Empty<byte>();
        public JitFn Fn = null!;
        public int Count;
        public int[] RunAt = Array.Empty<int>(), RunLen = Array.Empty<int>();   // the byte ranges Bytes holds
        public uint[] Addrs = Array.Empty<uint>();   // the instructions, by address (entry k = Addrs[k])
        public bool C16, Pg;   // compiled for this code size, and with paging on or off (the memory accesses differ)
        public long Runs, Instrs, Ticks, Retired;
        public long NXlat, NSlow, NOol, NCond;   // JITSTAT: TLB misses (JXlat), non-RAM / split accesses, out-of-line instructions, Cond calls
        public uint[] Pages = Array.Empty<uint>(), Gens = Array.Empty<uint>();   // the 4 KB pages Bytes come from, and their code write generations when last checked
        public uint Epoch = 0xffffffff;   // _memEpoch when last checked (FFFFFFFF: never: compare the bytes)
        public bool SpecFail;
        public uint SegKey = 0xffffffff;   // the JSegKey its last full segment check passed with (segment generation)   // its segment specialization no longer held at a run: compiled again without it
    }

    /// <summary>One instruction address: how often it was reached, and the compiled block (and instruction in it) that starts there.</summary>
    private struct JitEntry   // (16 bytes, no reference: a cold instruction's lookup touches 16 bytes of a 16 MB table, not 32 of 32 MB; the block is in _jcd)
    {
        public uint Start;
        public int Visits;
        public ushort K;
        public bool Used;
        public bool C16, Pg;
        public bool Failed;
        public bool Pending;   // being compiled in the background (interpreted meanwhile)
    }

    private static readonly bool JitOn = RuntimeFeature.IsDynamicCodeCompiled && Environment.GetEnvironmentVariable("X86_NOJIT") == null;
    private const int JitBits = 20, JitMask = (1 << JitBits) - 1, JIT_DECLINE = 3, JIT_END = 4, JitKeep = 16384;
    private static readonly int JitMaxInstr = int.TryParse(Environment.GetEnvironmentVariable("X86_JITMAX"), out int jm) ? jm : 128;
    /// <summary>X86_NOKEEPSEGS: reload the segments after every out-of-line instruction (A/B of DcKeepsSegs).</summary>
    private static readonly bool NoKeepSegs = Environment.GetEnvironmentVariable("X86_NOKEEPSEGS") != null;
    /// <summary>X86_NOHELPEROPS: flag ops and stack ops out of line again (A/B).</summary>
    private static readonly bool NoRetImm = Environment.GetEnvironmentVariable("X86_NORETIMM") != null, NoPushI = Environment.GetEnvironmentVariable("X86_NOPUSHI") != null, NoRdSeg = Environment.GetEnvironmentVariable("X86_NORDSEG") != null;
    /// <summary>X86_NOPAGEMISS: the old fetch-page miss path (the target's first instruction always interpreted, fetched byte by byte through
    /// the slow path, no CMP + Jcc fold there). Default since 2026-11-20: the miss path caches the page, tries the compiled block, and fetches fast.</summary>
    private static readonly bool NoPageMissJit = Environment.GetEnvironmentVariable("X86_NOPAGEMISS") != null;
    private static readonly bool NoMmxJit = Environment.GetEnvironmentVariable("X86_NOMMXJIT") != null;
    private static readonly bool NoHelperOps = Environment.GetEnvironmentVariable("X86_NOHELPEROPS") != null;
    private static readonly bool NoStkOp = Environment.GetEnvironmentVariable("X86_NOSTKOP") != null, NoFlagOp = Environment.GetEnvironmentVariable("X86_NOFLAGOP") != null;
    private static readonly bool NoFwaitOp = Environment.GetEnvironmentVariable("X86_NOFWAITOP") != null;
    private static readonly int StkMask = int.TryParse(Environment.GetEnvironmentVariable("X86_STKMASK"), out int sm) ? sm : 15;   // (bit 3 PUSH Sreg: was broken by an Op truncation, PUSH FS pushed GS: Win98 fatal 0D at boot)
    private static readonly int JitHot = int.TryParse(Environment.GetEnvironmentVariable("X86_JITHOT"), out int jh) ? jh : 8192;   // (8192 since 2026-11-21 cpuwarm2: Garage2 suite 3 rounds vs 2048 -4.7% geomean (desktop -19, boot -4, aoe -1, camp 0, sc +2.5; 4096 was -2.1%); PEARL 2-core 3 rounds -4.8% (desktop -17, boot -5, camp -3, aoe 0, sc +1.5); a lower threshold on pages with code (X86_JITHOTPG=256) +12% worse. 2048 since 2026-11-21 cpuwarm: suite Garage2 -7.3% geomean (aoe -9, boot -11, desktop -10, camp -7, sc 0), PEARL 2-core aoe -12 boot -15 desktop -16 camp -6 sc -1.5; 36k soaks unchanged. 512 before that; 32 before 2026-11-21: after a state load the background compiler fell ~100 M instructions behind - 85-90% of all interpreted instructions were pending; suite geomean -16.7%, every case faster; an adaptive 32-when-idle variant lost 6.8%)
    private JitCode? _jitCur;   // the block running now
    private readonly JitEntry[] _je = new JitEntry[1 << JitBits];
    private readonly JitCode?[] _jcd = new JitCode?[1 << JitBits];   // the compiled block of _je[i] (same index)
    private readonly List<JitCode> _jitAll = new();
    private readonly Dictionary<ulong, JitCode[]> _jitCodes = new();   // every block compiled, by start address and code size: a block evicted from the table is not compiled twice;
    // up to JitVariants versions per address (code a program rewrites in place, Win98's GDI blit code, comes back to the same few byte patterns)
    private static readonly int JitVariants = int.TryParse(Environment.GetEnvironmentVariable("X86_JITVARIANTS"), out int jv) ? Math.Clamp(jv, 1, 64) : 8;
    public long JitVariantHits;
    private JitCode? JitFindVariant(ulong key)
    {
        if (!_jitCodes.TryGetValue(key, out var vs)) return null;
        foreach (var v in vs) if (v != null && !v.SpecFail && JitSame(v)) return v;
        return null;
    }
    public int JitReused { get; private set; }
    /// <summary>--jitstat pipeline counters: regions posted to the compiler, dropped at install (bytes changed meanwhile / failed),
    /// worker ticks inside Emit + the .NET JIT, compiles of a key compiled before (churn), variant-table clears.</summary>
    public long JitPosted, JitDropped, JitRecompiles, JitKeepClears, JitInstrCompiled;
    public static long JitEmitTicks;
    private readonly HashSet<ulong> _jitSeenKeys = new();
    public string JitPipeStat() => $"jitpipe: posted {JitPosted} installed {JitCompiled} dropped {JitDropped} recompiles(same key) {JitRecompiles} keep-clears {JitKeepClears} unseen-cut {JitUnseenCut} instrs-compiled {JitInstrCompiled} worker-compile {JitEmitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms ({JitEmitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, JitPosted):F2} ms/region; IL build {JitBuildTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms, IL {JitIlBytes / Math.Max(1, JitPosted)} bytes/region)" + (JcOn ? " | " + JcStat() : "");

    // statistics (Workshop --x86boot --jitstat)
    public int JitCompiled { get; private set; }
    public long JitRuns, JitInvalid, JitDeclines;
    public readonly long[] JitDeclineSeg = new long[6];
    public IEnumerable<string> JitTop(int n) => _jitAll.OrderByDescending(b => b.Ticks).Take(n)
        .Select(b => $"{b.Start:X8} n{b.Count} runs {b.Runs} instrs {b.Instrs} retired {b.Retired} ns/retired {b.Ticks * 1e9 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, b.Retired):F2} xlat {b.NXlat} slow {b.NSlow} ool {b.NOol} cond {b.NCond} | {Convert.ToHexString(b.Bytes)}");
    private readonly long[] _interpOps = new long[65536];
    public readonly Dictionary<uint, long> InterpAt = new();
    /// <summary>JITSTAT: why an interpreted address is not compiled: its entry's state.</summary>
    public string JitWhy(uint pa) { ref var en = ref _je[JitIndex(pa)]; return !en.Used ? "no entry" : en.Start != pa ? $"slot holds {en.Start:X8}" : $"c16 {en.C16} pg {en.Pg} visits {en.Visits} failed {en.Failed} pending {en.Pending} code {(_jcd[JitIndex(pa)] != null)}"; }
    /// <summary>JITSTAT: interpreted instructions by mode (VM / 16-bit code / ring 0) and by the address's JIT entry state.</summary>
    private readonly long[,] _interpMode = new long[8, 6];
    public IEnumerable<string> InterpModes()
    {
        string[] st = { "no entry", "other start", "warming", "failed", "pending", "has code" };
        for (int md = 0; md < 8; md++)
        {
            long t = 0; for (int s = 0; s < 6; s++) t += _interpMode[md, s];
            if (t == 0) continue;
            yield return $"{((md & 4) != 0 ? "V86" : (md & 2) != 0 ? "16-bit" : "32-bit")} {((md & 1) != 0 ? "ring0" : "ring>0")}: {t}  (" + string.Join(", ", Enumerable.Range(0, 6).Where(s => _interpMode[md, s] > 0).Select(s => $"{st[s]} {_interpMode[md, s]}")) + ")";
        }
    }
    private void JitCountInterp(uint pa)
    {
        int md = ((Flags & VM) != 0 ? 4 : 0) | (Code16 ? 2 : 0) | (Cpl == 0 ? 1 : 0);
        ref var en = ref _je[JitIndex(pa)];
        int s = !en.Used ? 0 : en.Start != pa ? 1 : en.Failed ? 3 : en.Pending ? 4 : _jcd[JitIndex(pa)] != null ? 5 : 2;
        _interpMode[md, s]++;
        JitCountInterp0(pa);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void JitCountInterp0(uint pa) { InterpAt[pa] = InterpAt.GetValueOrDefault(pa) + 1; if (pa + 2 < MemSize) { int k = Mem[pa] << 8 | Mem[pa + 1]; if (++_interpOps[k] == 100000 && JitWatch == k) Console.Error.WriteLine($"jw: op {k:X4} at {pa:X8}"); } }
    private readonly long[] _oolOps = new long[65536];
    /// <summary>The out-of-line instructions inside compiled blocks (JInterp), by first two bytes.</summary>
    public IEnumerable<string> OutOfLineTop(int n) => Enumerable.Range(0, 65536).Where(i => _oolOps[i] > 0).OrderByDescending(i => _oolOps[i]).Take(n)
        .Select(i => $"{i >> 8:X2} {i & 255:X2}: {_oolOps[i]}");
    public IEnumerable<string> InterpTop(int n) => Enumerable.Range(0, 65536).Where(i => _interpOps[i] > 0).OrderByDescending(i => _interpOps[i]).Take(n)
        .Select(i => $"{i >> 8:X2} {i & 255:X2}: {_interpOps[i]}");
    /// <summary>Zero the statistics (Workshop --x86speed: count the workload only, not the boot).</summary>
    public void JitStatReset() { Array.Clear(_interpOps); Array.Clear(_oolOps); foreach (var b in _jitAll) { b.Runs = 0; b.Instrs = 0; } JitRuns = 0; }
    public long JitInstrsTotal => _jitAll.Sum(b => b.Instrs);
    public static readonly bool JitStats = Environment.GetEnvironmentVariable("X86_JITSTAT") != null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int JitIndex(uint paddr) => (int)((paddr ^ (paddr >> JitBits)) & JitMask);

    /// <summary>Exec1's hook, on the fast fetch path: runs a compiled block from <paramref name="paddr"/> if there is a valid one
    /// (OK / FAIL as Exec1's own), else counts the visit and returns JIT_DECLINE (interpret this instruction).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int JitTry(uint paddr)
    {
        int ji = JitIndex(paddr);
        ref var en = ref _je[ji];
        if (en.Used && en.Start == paddr && en.C16 == _c16 && en.Pg == ((Cr0 & CR0_PG) != 0))
        {
            var code = _jcd[ji];
            if (code != null)
            {
                if (!code.SpecFail && JitSame(code))
                {
                    _jitCur = code;
                    if (PcMachine.TimeProbe) return JitRunTimed(code, en.K);
                    if (!JitStats) return code.Fn(this, en.K);
                    return JitRunCounted(code, en.K);
                }
                if (PcMachine.TimeProbe) _jtWhy = 1;
                return JitInvalidated(ref en, ji);
            }
            if (en.Pending && JitHotQ) en.Visits++;   // (X86_JITHOTQ: the queue compiles the most-run waiting region first)
            if (en.Failed || en.Pending) { if (PcMachine.TimeProbe) _jtWhy = en.Failed ? 2 : 3; return JIT_DECLINE; }
            if (++en.Visits >= _jitHotNow) { if (PcMachine.TimeProbe) _jtWhy = 4; JitCompile(ref en); }
            else { if (PcMachine.TimeProbe) _jtWhy = 5; _seqArm = true; }
            return JIT_DECLINE;
        }
        if (PcMachine.TimeProbe) _jtWhy = 6;
        _seqArm = true;
        return JitVisit(paddr);
    }

    /// <summary>Sequential skip (X86_NOSEQSKIP: off): after an interpreted instruction whose own entry is still counting toward JITHOT (or new),
    /// the instruction right after it (fall-through, same cached page) skips the entry lookup: a region compiled from that cold start will
    /// cover it, and the lookup is a cache miss in a 32 MB table on every cold instruction. Any jump, block exit, batch start, page change
    /// or failed/pending/compiled entry looks up again. Only which code runs compiled can change, never what it does.</summary>
    private static readonly bool JitSeqSkip = Environment.GetEnvironmentVariable("X86_NOSEQSKIP") == null;
    private bool _seqArm;

    private static readonly uint JitWatch = Convert.ToUInt32(Environment.GetEnvironmentVariable("X86_JITWATCH") ?? "0", 16);
    private int _jwN;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void JitWatchLog(uint paddr)
    {
        if (++_jwN % 100000 != 1) return;
        ref var en = ref _je[JitIndex(paddr)];
        Console.Error.WriteLine(!en.Used ? "jw: no entry" : $"jw: start {en.Start:X8} c16 {en.C16}/{_c16} visits {en.Visits} failed {en.Failed} pending {en.Pending} code {(_jcd[JitIndex(paddr)] is not JitCode jc0 ? "-" : jc0.Start.ToString("X8") + " same " + JitSame(jc0))} k {en.K} cr0 {Cr0:X} segs " +
            string.Join(",", Enumerable.Range(0, 6).Select(s => JSegOk(s) ? "1" : "0")));
    }
    /// <summary>X86_TIMEPROBE: wall ticks inside compiled blocks (out-of-line instructions included) and the block runs.</summary>
    public long TicksJit, JitTimedRuns;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int JitRunTimed(JitCode code, int k)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int rc = code.Fn(this, k);
        TicksJit += System.Diagnostics.Stopwatch.GetTimestamp() - t0; JitTimedRuns++;
        return rc;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int JitRunCounted(JitCode code, int k)
    {
        long c0 = Cycle, s0 = _steps, t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int rc = code.Fn(this, k);
        if (rc == OK) code.Retired += s0 - _steps + 1;   // (steps the block and the blocks it linked to retired; a skipped spin counts once)
        code.Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        JitRuns++; code.Runs++; code.Instrs += Cycle - c0;
        if (rc == JIT_DECLINE)
        {
            JitDeclines++; DeclineWhy[Math.Min(63, _declWhy)]++; _declWhy = 0;
            for (int s = 0; s < 6; s++) if (!JSegOk(s)) JitDeclineSeg[s]++;
            if (JitDeclines % 2000000 == 1) Console.Error.WriteLine($"jitdecl: blk {code.Start:X8} k {k} vm {(Flags >> 17) & 1} cpl {Cpl} c16 {Code16} " + string.Join(" ", Enumerable.Range(0, 6).Select(s => $"{s}:{SegSel[s]:X4}/{SegFlags[s]:X4}/{SegLimit[s]:X}")));
        }
        return rc;
    }

    /// <summary>Live immediates (lane D 2026-11-20; X86_NOLIVEIMM: off): a MOV r32, imm32 whose immediate code keeps patching (StarCraft's campaign:
    /// a Win98 VxD rewrites the imm32 of `mov edx, imm32` at 006C7982 about once a frame, which threw away and recompiled every block on
    /// that page) is compiled to read its immediate from memory at run time; its 4 bytes leave the block's byte ranges, so the patch no
    /// longer invalidates the block. The interpreter reads the same bytes when it runs the instruction, so nothing but the speed changes.</summary>
    private static readonly bool JitLiveImmOn = Environment.GetEnvironmentVariable("X86_NOLIVEIMM") == null;
    private readonly Dictionary<uint, int> _smcW = new();
    private void SmcNote(uint a)
    {   // a 4-byte store into a code line: counted by address (bounded; only the JIT reads it)
        if (_smcW.TryGetValue(a, out int c)) _smcW[a] = c + 1;
        else if (_smcW.Count < 4096) _smcW[a] = 1;
    }
    private bool JitLiveImm(uint immAt) => JitLiveImmOn && _smcW.TryGetValue(immAt, out int c) && c >= 2;
    public long JitLiveImms;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int JitInvalidated(ref JitEntry en, int ji)
    {   // the block's bytes changed: a version compiled earlier from these very bytes runs at once; else interpret, and compile again once hot again
        JitInvalid++;
        ulong key = en.Start | (en.C16 ? 1UL << 32 : 0) | (en.Pg ? 1UL << 33 : 0);
        if (JitVariants > 1 && JitFindVariant(key) is JitCode v && v.Start == en.Start)
        {
            JitVariantHits++;
            JitRegister(v);
            int j2 = JitIndex(en.Start);
            ref var e2 = ref _je[j2];
            if (e2.Used && _jcd[j2] == v) { _jitCur = v; return v.Fn(this, e2.K); }
        }
        _jcd[ji] = null; en.Visits = 0; en.Failed = false; en.Pending = false;
        return JIT_DECLINE;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int JitVisit(uint paddr)
    {
        int ji = JitIndex(paddr);
        ref var en = ref _je[ji];
        en.Used = true;
        en.Start = paddr; en.C16 = _c16; en.Pg = (Cr0 & CR0_PG) != 0; en.Visits = 1; en.Failed = false; en.Pending = false; _jcd[ji] = null; en.K = 0;
        return JIT_DECLINE;
    }

    // ================= helpers the compiled code calls =================

    [MethodImpl(MethodImplOptions.AggressiveInlining)] private bool JRam(uint la) => IsRam(la);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private uint JRdSlow(int bits, uint la)
    {
        if (JitStats && _jitCur != null) _jitCur.NSlow++;
        var r = new OptAddr { A1 = la };
        return bits == 8 ? Load8Slow(ref r) : bits == 16 ? Load16Slow(ref r) : Load32Slow(ref r);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void JWrSlow(int bits, uint la, uint v)
    {
        if (JitStats && _jitCur != null) _jitCur.NSlow++;
        var r = new OptAddr { A1 = la };
        if (bits == 8) Store8Slow(ref r, v); else if (bits == 16) Store16Slow(ref r, v); else Store32Slow(ref r, v);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool JSegOk(int seg) => ((Cr0 & 1) == 0 || SegLimit[seg] != 0 || (SegSel[seg] & ~3u) != 0) && !(ExpandDown(seg) && (NoExpandDown || SegLimit[seg] == 0xffffffff)) && JSegSpan(seg) >= 3 && ((SegFlags[seg] & 0x10) == 0 || (SegFlags[seg] & 0x1a) == 0x12);
    // expand-down data segments (Win98's flat Win32 DS / ES / SS is one: 0167h, limit EE60h, B = 1): valid offsets are limit + 1 .. FFFFh / FFFFFFFFh.
    // The compiled limit check is (off - lo) <= span - (size - 1): lo = 0, span = limit for the usual kind; lo = limit + 1, span = top - lo here
    /// <summary>Usable for reads only: a readable code segment (a CS: override, jump tables in 16-bit code; Win98's ring-0 code reads through CS too).</summary>
    private bool JSegRdOk(int seg) => JSegOk(seg) || (Cr0 & 1) != 0 && (Flags & VM) == 0 && ((SegFlags[seg] & 0x1a) == 0x1a || !NoRdSeg && (SegFlags[seg] & 0x1e) == 0x10) && SegLimit[seg] >= 3;   // (+ read-only data, expand-up: Win98's 0123h, limit 3 - StarCraft's GS: / 16-bit ES:)
    private bool ExpandDown(int seg) => (Cr0 & 1) != 0 && (Flags & VM) == 0 && (SegFlags[seg] & 0x1c) == 0x14;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JSegLo(int seg) => ExpandDown(seg) ? SegLimit[seg] + 1 : 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JSegSpan(int seg) => ExpandDown(seg) ? ((SegFlags[seg] & 0x4000) != 0 ? 0xffffffffu : 0xffffu) - (SegLimit[seg] + 1) : SegLimit[seg];
    private static readonly bool NoExpandDown = Environment.GetEnvironmentVariable("X86_NOEXPANDDOWN") != null;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JSegLim(int seg) => SegLimit[seg];
    // a compiled block's prologue (and the reload after an out-of-line instruction): every segment it uses in ONE call - the same
    // JSegOk / JSegRdOk / JSegBase / JSegSpan / JSegLo values, read back through inlined getters (was up to 8 calls per segment)
    private readonly uint[] _jsBase = new uint[6], _jsSpan = new uint[6], _jsLo = new uint[6];
    private int _jsOk, _jsRd;
    private uint _jsGen = 0xffffffffu, _jsMode = 0xffffffffu;   // JSegLoad's memo key (not saved: SegGen moves on at every state load)
    private static readonly bool NoSegMemo = Environment.GetEnvironmentVariable("X86_NOSEGMEMO") != null;
    private static readonly bool NoSegInl = Environment.GetEnvironmentVariable("X86_NOSEGINL") != null;
    private static readonly bool NoSegLoad = Environment.GetEnvironmentVariable("X86_NOSEGLOAD") != null;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void JSegLoad(int mask)
    {   // memo (X86_NOSEGMEMO: off): every value below depends only on the segment registers (any real change bumps SegGen), PE and VM,
        // so while those are unchanged the last computation (made for all six segments) still holds
        uint mode = ((Flags >> 16) & 2) | (Cr0 & 1);
        if (SegGen == _jsGen && mode == _jsMode && !NoSegMemo) return;
        if (!NoSegMemo) { mask = 63; _jsGen = SegGen; _jsMode = mode; }
        int ok = 0, rd = 0;
        for (int s = 0; s < 6; s++)
        {
            if ((mask >> s & 1) == 0) continue;
            if (JSegOk(s)) { ok |= 1 << s; rd |= 1 << s; }
            else if (JSegRdOk(s)) rd |= 1 << s;
            _jsBase[s] = SegBase[s]; _jsSpan[s] = JSegSpan(s); _jsLo[s] = JSegLo(s);
        }
        _jsOk = ok; _jsRd = rd;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private bool JSOk(int s) => (_jsOk >> s & 1) != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private bool JSRd(int s) => (_jsRd >> s & 1) != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JSBase(int s) => _jsBase[s];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JSSpan(int s) => _jsSpan[s];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JSLo(int s) => _jsLo[s];
    /// <summary>Segment specialization: the values a block was compiled with (bit per segment in Mask), and SpMask.</summary>
    private sealed class SegSpec { public int Mask; public uint Spm; public uint[] Base = new uint[6], Span = new uint[6], Lo = new uint[6]; }
    /// <summary>X86_PEND: pending clock / step counts in compiled blocks (see Emit). Opt-in: A/B 2026-11-20 in StarCraft MEASURED SLOWER
    /// (+38% ns per retired instruction in the hot blocks: the per-exit constants grow the code more than the per-instruction adds cost).</summary>
    private static readonly bool UsePend = Environment.GetEnvironmentVariable("X86_PEND") != null;
    private static readonly bool NoRamRange = Environment.GetEnvironmentVariable("X86_NORAMRANGE") != null;
    /// <summary>X86_SHAREDEXIT: one shared budget-out exit per block (opt-in: A/B 2026-11-20 +5.9% ns/retired, no gain).</summary>
    private static readonly bool NoSharedExit = Environment.GetEnvironmentVariable("X86_SHAREDEXIT") == null;
    /// <summary>X86_SEGGEN: the segment-generation prologue fast path (opt-in: Garage2 6 rounds AoE +2.7%, StarCraft +0.9%, no gain).</summary>
    private static readonly bool NoSegGen = Environment.GetEnvironmentVariable("X86_SEGGEN") == null;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JSegKey() => SegGen << 2 | ((Flags >> 16) & 2) | (Cr0 & 1);
    private static readonly bool NoSegSpec = Environment.GetEnvironmentVariable("X86_NOSEGSPEC") != null;
    private readonly HashSet<ulong> _segSpecOff = new();   // blocks whose specialization failed once: compiled without it from then on
    public long JitSpecFails;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private bool JSSame(int s, uint b, uint span, uint lo) => (_jsOk >> s & 1) != 0 && _jsBase[s] == b && _jsSpan[s] == span && _jsLo[s] == lo;
    [MethodImpl(MethodImplOptions.NoInlining)] private bool JSSameLive(int s, uint b, uint span, uint lo) => JSegOk(s) && SegBase[s] == b && JSegSpan(s) == span && JSegLo(s) == lo;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void JSpecFail(int entry)
    {   // (entry -1: the compile's own trial call, not a real run)
        if (entry < 0 || _jitCur == null) return;
        JitSpecFails++;
        _jitCur.SpecFail = true;
        _segSpecOff.Add(_jitCur.Start | (_jitCur.C16 ? 1UL << 32 : 0) | (_jitCur.Pg ? 1UL << 33 : 0));
    }
    private SegSpec? JitSegSpec(ulong key, DEntry[] ins)
    {
        if (NoSegSpec || NoSegLoad || _segSpecOff.Contains(key)) return null;
        var sp = new SegSpec { Spm = _spm };
        int used = 0;
        foreach (var e in ins) used |= InsSegs(e);
        for (int s = 0; s < 6; s++)
            if ((used >> s & 1) != 0 && JSegOk(s)) { sp.Mask |= 1 << s; sp.Base[s] = SegBase[s]; sp.Span[s] = JSegSpan(s); sp.Lo[s] = JSegLo(s); }
        return sp.Mask == 0 ? null : sp;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JSegBase(int seg) => SegBase[seg];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private bool JCond(uint c) { if (JitStats) { JCondOps[(_ccOp & 31) * 16 + (c & 15)]++; if (_jitCur != null) _jitCur.NCond++; } return Cond(c); }
    /// <summary>JITSTAT: the condition tests compiled code made through Cond, by lazy-flag op and condition.</summary>
    public readonly long[] JCondOps = new long[32 * 16];
    [MethodImpl(MethodImplOptions.NoInlining)] private uint JAlu(int op, int bits, uint a, uint b) => Alu(op, bits, a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void JIncDec(int bits, int reg, bool dec) => IncDecReg(bits, reg, dec);
    [MethodImpl(MethodImplOptions.NoInlining)] private int JGetCF() => GetCF();
    /// <summary>X86_NOINCINLINE: INC / DEC r through the JIncDec call again (A/B).</summary>
    private static readonly bool NoIncInline = Environment.GetEnvironmentVariable("X86_NOINCINLINE") != null;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JNextIp() => NextIp;

    /// <summary>The decode state an out-of-line handler reads (what Exec1's fetch and DecodeModrm would have left).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)] private void JPrep(uint ip, uint nextIp, uint ifp, bool o16, bool a16, int curSeg, int mod, int reg, int rm, uint addr)
    {
        Ip = ip; NextIp = nextIp; _ifPaddr = ifp; _opsz16 = o16; _adsz16 = a16; _rep = 0; _curSeg = curSeg;
        _mod = mod; _reg = reg; _rm = rm; _addr = addr;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private bool JGrp2(int bits, int op, uint count)
    {
        if (!TranslateE(bits, 3)) return false;
        if (bits == 32) DGrp2_32(op, count); else if (bits == 16) DGrp2_16(op, count); else DGrp2_8(op, count);
        return true;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private bool JGrp3(int bits) => Grp3Body(bits);
    /// <summary>FE / FF: INC / DEC Eb / Ev, CALL / JMP / PUSH Ev (after JPrep; the far forms are never compiled). False = a fault.</summary>
    private static readonly bool CmOne = Environment.GetEnvironmentVariable("X86_CMONE") != null;
    private static readonly bool NoGrp5Inl = Environment.GetEnvironmentVariable("X86_NOGRP5INL") != null;
    private static bool Grp5Inline(in DEntry e) => !NoGrp5Inl && e.Reg is 2 or 4 or 6 && e.Bits != 8;
    [MethodImpl(MethodImplOptions.NoInlining)] private bool JGrp5(int bits)
    {
        if (_reg < 2) { if (!TranslateE(bits, 3)) return false; IncDec(bits, _reg == 1); return true; }
        if (!TranslateE(bits, 1)) return false;
        uint v = ReadE(bits);
        if (_reg == 2) return CallNear(v, false);
        if (_reg == 4) { NextIp = v; return true; }
        return Push(bits, v);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private bool JLeave() => Leave();
    /// <summary>MOV Sw, Ew (after JPrep): the interpreter's checked segment load. False = a fault (raised).</summary>
    /// <summary>CMC / CLC / STC / CLI / CLD / STD, exactly as the interpreter (CLI with IOPL &lt; CPL: #GP, false).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool JFlagOp(int op)
    {
        switch (op)
        {
            case 0xf5: { int cf = GetCF(); _ccMask &= ~CF; Flags = SetBit(Flags, cf == 0, CF); return true; }
            case 0xf8: _ccMask &= ~CF; Flags &= ~CF; return true;
            case 0xf9: _ccMask &= ~CF; Flags |= CF; return true;
            case 0xfa: if (GetIOPL() < Cpl) return Throw(EX_GP, 0); Flags &= ~IF; return true;
            case 0x9b: if ((Cr0 & 0xa) == 0xa) return Throw0(EX_NM); return !_hasFpu || FWaitCheck() == OK;   // FWAIT, as the interpreter
            case 0xfc: Flags &= ~DF; return true;
            default: Flags |= DF; return true;
        }
    }
    /// <summary>PUSHF / PUSHA / POPA / PUSH Sreg through the interpreter's own code (after JPrep: the operand size); false = a fault.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool JStk(int op) => op switch
    {
        0x9c => PushF(), 0x60 => Pusha(), 0x61 => Popa(),
        0x06 => PushSeg(SEG_ES), 0x0e => PushSeg(SEG_CS), 0x16 => PushSeg(SEG_SS), 0x1e => PushSeg(SEG_DS),
        0xf0 => PushSeg(SEG_FS), _ => PushSeg(SEG_GS),
    };
    [MethodImpl(MethodImplOptions.NoInlining)] private bool JMovSeg(int seg, uint v) => LoadSegChecked(seg, v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JSegSel(int seg) => SegSel[seg];
    /// <summary>A REP / REPNE string instruction (after JPrep), the interpreter's own loop (it charges the iterations to Cycle). False = a fault.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool JRepStr(int opc, int bits, int rep, long cyc)
    {
        Cycle = cyc; _rep = rep;
        return opc switch { 0xa4 or 0xa5 => Movs(bits), 0xa6 or 0xa7 => Cmps(bits), 0xaa or 0xab => Stos(bits), 0xac or 0xad => Lods(bits), _ => Scas(bits) };
    }
    /// <summary>After a store the compiled code cannot see (REP MOVS / STOS): is the running block's code unchanged?</summary>
    [MethodImpl(MethodImplOptions.NoInlining)] private bool JCodeSame() => JitSame(_jitCur!);
    [MethodImpl(MethodImplOptions.NoInlining)] private void JImul2(int bits, int reg, uint a, uint b) => Imul2(bits, reg, a, b);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool JCall(uint rel) => CallNear(rel, true);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool JRet(int n) => K(Ret((uint)n)) != FAIL;

    /// <summary>An out-of-line instruction: exactly one iteration of Exec1's loop (without the compiled code), from Ip <paramref name="ip"/>.</summary>
    /// <summary>JITSTAT: out-of-line instructions by physical address (the top ones, disassembled by the caller from memory).</summary>
    public readonly Dictionary<uint, long> OolAt = new();
    private void JitOolAt(uint pa) { OolAt[pa] = OolAt.GetValueOrDefault(pa) + 1; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int JInterp(uint ip, long cyc, int st)
    {
        Cycle = cyc; _steps = st; NextIp = ip;
        uint opa = 0xffffffff;
        if (JitStats && _jitCur != null) _jitCur.NOol++;
        if (JitStats && _jitCur != null) { uint pa = (_jitCur.Start & ~0xfffu) | ((SegBase[SEG_CS] + ip) & 0xfff); if (pa + 2 < MemSize) { _oolOps[Mem[pa] << 8 | Mem[pa + 1]]++; JitOolAt(pa); opa = pa; } }   // (the region never leaves its 4 KB page)
        if (!PcMachine.TimeProbe) return Step1(false);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int r = Step1(false);
        long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        TicksOol += dt; OolCalls++;
        if (opa != 0xffffffff) OolTicksAt[opa] = OolTicksAt.GetValueOrDefault(opa) + dt;   // (JITSTAT + TIMEPROBE: which out-of-line instructions cost the time)
        return r;
    }
    /// <summary>X86_TIMEPROBE: wall ticks inside out-of-line instructions (JInterp) and their count.</summary>
    public long TicksOol, OolCalls;
    public readonly Dictionary<uint, long> OolTicksAt = new();
    /// <summary>After an out-of-line instruction: can the compiled code go on (the same code size, paging still off, the fetch still
    /// on this code page, every segment it uses still usable)? Leaves _spm as Exec1's next iteration would.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool JStill(bool c16, bool pg, uint pa, int segs, bool wrote)
    {
        _c16 = Code16; _spm = SpMask;
        // (the fetch page: the physical page of the cached linear one; the same address with paging off)
        if (_c16 != c16 || ((Cr0 & CR0_PG) != 0) != pg || _ifLaddr == ulong.MaxValue || (((uint)_ifLaddr ^ _ifXaddr) & ~4095u) != (pa & ~4095u)) return false;
        if (wrote && !JitSame(_jitCur!)) return false;   // (it may have written to the code)
        for (int s = 0; s < 6; s++) if ((segs >> s & 1) != 0 && !JSegOk(s)) return false;
        return true;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JSpm() => _spm;

    // ---- paging on: the compiled code translates a linear address inline on a TLB hit (_tlbTag: allowed at this CPL), else here
    private uint _jpa;
    /// <summary>The interpreter's translation of a data access (the same faults, accessed / dirty bits): false = a page fault (raised).
    /// _jpa = the physical address, or FFFFFFFF for an access split over two pages (it then goes through JRdLin / JWrLin).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool JXlat(uint la, int rwm, int size)
    {
        if (JitStats && _jitCur != null) _jitCur.NXlat++;
        var r = new OptAddr();
        if (!TranslateLaddr(ref r, rwm, la, size, Cpl)) return false;
        _jpa = r.Two ? 0xffffffff : r.A1;
        return true;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JPa() => _jpa;
    /// <summary>A load / store not on plain RAM (or split over two pages), from its linear address (translated already: no fault).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private uint JRdLin(int bits, uint la)
    {
        if (JitStats && _jitCur != null) _jitCur.NSlow++;
        var r = new OptAddr();
        if (!TranslateLaddr(ref r, 1, la, bits / 8, Cpl)) return 0;
        return bits == 8 ? Load8Slow(ref r) : bits == 16 ? Load16Slow(ref r) : Load32Slow(ref r);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void JWrLin(int bits, uint la, uint v)
    {
        if (JitStats && _jitCur != null) _jitCur.NSlow++;
        var r = new OptAddr();
        if (!TranslateLaddr(ref r, 2, la, bits / 8, Cpl)) return;
        if (bits == 8) Store8Slow(ref r, v); else if (bits == 16) Store16Slow(ref r, v); else Store32Slow(ref r, v);
    }
    /// <summary>The physical address a helper's access just used (its TLB entry), FFFFFFFF if not one page / not cached.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private uint JPhys(uint la, int size)
    {
        uint lp = la >> 12; int e = TlbIdx(lp);
        if (_tlbLpgno[e] != lp || (la & 0xfff) > 4096 - size) return 0xffffffff;
        return _tlbXaddr[e] ^ la;
    }
    /// <summary>The spin skip's plain-RAM test (TryJcc8E) for a paged operand.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool JSpinRam(uint la, int size)
    {
        var r = new OptAddr();
        if (!TranslateLaddr(ref r, 1, la, size, Cpl)) return false;
        return IsRam(r.A1) && (!r.Two || IsRam(r.A2));
    }

    /// <summary>Block linking (X86_NOLINK turns it off): leave as JExitOk, but when the next instruction starts a valid compiled block
    /// that Exec1's next iteration would run (the same fetch-page test, entry lookup and code check as Step1), run it from here.</summary>
    private int JFpuNmRefund() => (Cr0 & 0xc) != 0 ? _memCost : 0;
    /// <summary>The same for a compiled MMX instruction: #UD / #NM / #MF come before the interpreter decodes the ModR/M (MmxExec).</summary>
    private int JMmxNmRefund() => (Cr0 & 0xc) != 0 || (_fsw & FSW_ES) != 0 && (Cr0 & 0x20) != 0 ? _memCost : 0;
    private static readonly bool NoPageLink = Environment.GetEnvironmentVariable("X86_PAGELINK") == null;   // opt-in: links 7M -> 23M per 10 s in StarCraft but no wall-clock gain (A/B 2026-11-20)
    /// <summary>Block linking across pages: what Peek8 would do at the next fetch, without a page walk or fault - only when the TLB already
    /// holds a read translation for this CPL (paging on), the target is 16+ bytes inside its page and the CS limit, and the page is plain RAM.</summary>
    public readonly long[] LinkPageWhy = new long[7];
    private void LPW(int i) { if (JitStats) LinkPageWhy[i]++; }
    private void LinkPage(uint laddr, uint ip)
    {
        if ((Cr0 & CR0_PG) == 0) { LPW(0); return; } if ((laddr & 0xfff) >= 4096 - 16) { LPW(1); return; } if ((ulong)ip + 16 > SegLimit[SEG_CS]) { LPW(2); return; }
        uint lpg = laddr >> 12; int e = TlbIdx(lpg);
        if (_tlbLpgno[e] != lpg) { LPW(3); return; } if (PteLookup[_tlbLookup[e] * 4 + (Cpl == 3 ? 2 : 0)] != 0) { LPW(4); return; }   // (a read at this CPL: the page walk's own check)
        uint pa = _tlbXaddr[e] ^ laddr;
        if (MemSize <= 4096 || pa >= MemSize - 4096 || InIoMem(pa) || InIoMem(pa + 16)) { LPW(5); return; } LPW(6);
        _ifLaddr = laddr & ~4095ul; _ifXaddr = pa ^ laddr;
    }
    /// <summary>JITSTAT: block exits - 0 step budget out, 1 link depth, 2 linked, 3 no compiled block at the target, 4 target on another fetch page.</summary>
    public readonly long[] LinkWhy = new long[5];
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int JExitLink(uint ip, uint nextIp, long cyc, int st)
    {
        Ip = ip; NextIp = nextIp; Cycle = cyc; _steps = st + 1;
        if (st <= 0 || !JitLink || _linkDepth >= 64) { if (JitStats) LinkWhy[st <= 0 ? 0 : 1]++; return OK; }   // (each link nests a call: at most 64 deep)
        _steps = st;   // (Exec1's loop step for the block that just ran)
        _c16 = Code16; _spm = SpMask;
        uint laddr = SegBase[SEG_CS] + nextIp;
        if (((ulong)laddr ^ _ifLaddr) >= 4096 - 16 && !NoPageLink) LinkPage(laddr, nextIp);   // a target on another page: cache its fetch page when the TLB already allows it
        if (((ulong)laddr ^ _ifLaddr) < 4096 - 16)
        {
            uint paddr = _ifXaddr ^ laddr;
            int ji = JitIndex(paddr);
            ref var en = ref _je[ji];
            if (en.Used && en.Start == paddr && en.C16 == _c16 && en.Pg == ((Cr0 & CR0_PG) != 0) && _jcd[ji] is JitCode code && JitSame(code))
            {
                Ip = nextIp;
                _jitCur = code;
                _linkDepth++;
                int rc = code.Fn(this, en.K);
                _linkDepth--;
                if (JitStats) LinkWhy[2]++;
                if (rc != JIT_DECLINE) return rc;
                // (declined in its prologue, nothing changed: back to Exec1, which would interpret the caller's start address again
                // if this returned JIT_DECLINE)
                Ip = ip; Cycle = cyc;
            }
        }
        if (JitStats) LinkWhy[((ulong)(SegBase[SEG_CS] + nextIp) ^ _ifLaddr) < 4096 - 16 ? 3 : 4]++;
        _steps = st + 1;
        return OK;
    }
    private int _linkDepth;
    /// <summary>JITSTAT: why the last compiled block declined (2 entry index, 1x segment at the prologue, 3x segment at the entry instruction).</summary>
    private int _declWhy;
    public readonly long[] DeclineWhy = new long[64];
    /// <summary>Block linking: on by default since X86-02 round 4 (A/B Win98 boot: mean 36.7 -> 34.3 ms/frame, p99 170 -> 148 ms; on dhry it had
    /// measured no faster). X86_NOLINK turns it off.</summary>
    private static readonly bool JitLink = Environment.GetEnvironmentVariable("X86_NOLINK") == null;
    [MethodImpl(MethodImplOptions.NoInlining)] private int JExitOk(uint ip, uint nextIp, long cyc, int st) { Ip = ip; NextIp = nextIp; Cycle = cyc; _steps = st + 1; return OK; }
    [MethodImpl(MethodImplOptions.NoInlining)] private int JExitFail(uint ip, long cyc, int st) { Ip = ip; Cycle = cyc; _steps = st; return FAIL; }
    [MethodImpl(MethodImplOptions.NoInlining)] private bool JLimitFault(int seg) => LimitFault(seg);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private long JCycle() => Cycle;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private int JSteps() => _steps;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JIp() => Ip;

    // ================= the compiler =================

    private static FieldInfo Fld(string name) => typeof(I386).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new MissingFieldException(nameof(I386), name);
    private static readonly FieldInfo FSrc1 = Fld("_ccSrc1"), FSrc2 = Fld("_ccSrc2"), FDst = Fld("_ccDst"), FDst2 = Fld("_ccDst2"), FOp = Fld("_ccOp"),
        FMask = Fld("_ccMask"), FFlags = Fld("Flags"), FR = Fld("R"), FMem = Fld("Mem"), FSpm = Fld("_spm"),
        FTlbL = Fld("_tlbLpgno"), FTlbX = Fld("_tlbXaddr"), FTlbT = Fld("_tlbTag"), FCpl = Fld("Cpl"), FCodeMark = Fld("_codeMark"), FDeclWhy = Fld("_declWhy"), FJitCur = Fld("_jitCur"),
        FSegKey = typeof(JitCode).GetField("SegKey")!,
        FSegGen = Fld("SegGen"), FJsGen = Fld("_jsGen"), FJsMode = Fld("_jsMode"), FCr0 = Fld("Cr0");

    private static MethodInfo M(string name) => typeof(I386).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new MissingMethodException(nameof(I386), name);
    private static readonly Lazy<Dictionary<string, MethodInfo>> JitMethods = new(() =>
    {
        var d = new Dictionary<string, MethodInfo>();
        foreach (var n in new[] { "JRam", "JSegKey", "JGetCF", "JSegLoad", "JSSame", "JSSameLive", "JSpecFail", "JSOk", "JSRd", "JSBase", "JSSpan", "JSLo", "JSegOk", "JSegRdOk", "JSegBase", "JSegLim", "JSegLo", "JSegSpan", "JCond", "JAlu", "JIncDec", "JNextIp", "JPrep", "JGrp2", "JGrp3", "JImul2", "JGrp5", "JLeave", "JMovSeg", "JSegSel", "JRepStr", "JFpu", "JFpuNmRefund", "JMmxNmRefund", "JMmx", "JCodeSame", "JCall", "JRet",
                     "JExitOk", "JExitLink", "JExitFail", "JLimitFault", "JCycle", "JSteps", "JIp", "JRdSlow", "JWrSlow", "JInterp", "JStill", "JSpm",
                     "JXlat", "JFlagOp", "JStk", "JPa", "JRdLin", "JWrLin", "JPhys", "JSpinRam", "CodeWrite" })
            d[n] = M(n);
        return d;
    });

    /// <summary>Is the compiled code's code still what is in memory (every byte range it was compiled from)? No store has touched its code
    /// lines since it was last checked (the same page generations, no outside write): yes at once; else its bytes are compared (and the
    /// generations taken again when they are still the same).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool JitSame(JitCode code)
    {
        if (code.Epoch == _memEpoch)
        {
            uint[] p = code.Pages, g = code.Gens;
            if (p.Length == 1 ? _pgGen[p[0]] == g[0] : JitGensSame(p, g)) return true;
        }
        return JitSameSlow(code);
    }
    private bool JitGensSame(uint[] p, uint[] g)
    {
        for (int i = 0; i < p.Length; i++) if (_pgGen[p[i]] != g[i]) return false;
        return true;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool JitSameSlow(JitCode code)
    {
        byte[] b = code.Bytes;
        int[] ro = code.RunAt, rl = code.RunLen;
        int at = 0;
        for (int i = 0; i < ro.Length; i++)
        {
            if (!Mem.AsSpan(ro[i], rl[i]).SequenceEqual(b.AsSpan(at, rl[i]))) return false;
            at += rl[i];
        }
        JitStamp(code);
        return true;
    }
    /// <summary>The code lines are marked (a store there moves the page on) and the block takes the generations of now.</summary>
    private void JitStamp(JitCode code)
    {
        if (code.Pages.Length == 0)
        {
            var pages = new SortedSet<uint>();
            for (int i = 0; i < code.RunAt.Length; i++)
            {
                uint a = (uint)code.RunAt[i], e = a + (uint)code.RunLen[i] - 1;
                for (uint l = a >> 6; l <= e >> 6; l++) _codeMark[l] = 1;
                for (uint pg = a >> 12; pg <= e >> 12; pg++) pages.Add(pg);
            }
            code.Pages = pages.ToArray(); code.Gens = new uint[code.Pages.Length];
        }
        for (int i = 0; i < code.Pages.Length; i++) code.Gens[i] = _pgGen[code.Pages[i]];
        code.Epoch = _memEpoch;
    }

    /// <summary>Compile the region of cached-form instructions reachable from <paramref name="en"/>'s address: the fall-throughs and the
    /// targets of the near jumps (JMP / Jcc / a folded JZ), close by, up to JitMaxInstr instructions. Jumps between them stay inside the
    /// compiled code; everything else leaves it.</summary>
    /// <summary>Default (X86_NOJITSEEN turns it off): a region takes only instructions the CPU has already run (they have a JIT entry: every fast-path fetch makes
    /// one); paths never taken are left out of it, so the .NET JIT compiles less cold code (they leave the block, the interpreter runs
    /// them, and if they get hot they get a region of their own).</summary>
    private static readonly bool JitSeenOnly = Environment.GetEnvironmentVariable("X86_NOJITSEEN") == null;   // (default since 2026-11-20; X86_NOJITSEEN: the whole reachable region again)
    public long JitUnseenCut;
    private bool JitSeen(uint pa, bool pg) { ref var e = ref _je[JitIndex(pa)]; return e.Used && e.Start == pa && e.C16 == _c16 && e.Pg == pg; }
    private readonly List<(uint pa, DEntry e)> _jcFound = new();
    private readonly HashSet<uint> _jcSeen = new();
    private readonly Queue<uint> _jcTodo = new();
    private readonly HashSet<uint> _jcFall = new();
    private void JitCompile(ref JitEntry en)
    {
        uint start = en.Start;
        bool pg = (Cr0 & CR0_PG) != 0;
        ulong key = start | (_c16 ? 1UL << 32 : 0) | (pg ? 1UL << 33 : 0);
        if (JitFindVariant(key) is JitCode old)
        {
            JitReused++;
            JitRegister(old);
            return;
        }
        var found = _jcFound; found.Clear(); var seen = _jcSeen; seen.Clear();
        var todo = _jcTodo; todo.Clear();
        var fall = _jcFall; fall.Clear();
        todo.Enqueue(start);
        bool anyCompiled = false;
        while (todo.Count > 0 && found.Count < JitMaxInstr)
        {
            uint pa = todo.Dequeue();
            if (!seen.Add(pa)) continue;
            if (JitSeenOnly && pa != start && !JitSeen(pa, pg) && !(JitSeqSkip && fall.Contains(pa))) { if (JitStats) JitUnseenCut++; continue; }   // never run yet: leave it out (it exits there)
            // one 4 KB page (the compiled code never goes on into another one: see Continue)
            if ((pa & 0xfff) >= 4096 - 16 || pa + 32 >= MemSize || (pa >> 12) != (start >> 12)) continue;
            var e = new DEntry();
            DcDecode(ref e, pa);
            if (e.Kind == DK_SLOW) continue;
            uint end = pa + e.Len;
            if (e.Kind == DK_MOVRI && e.Bits == 32 && e.Len >= 5 && JitLiveImm(end - 4)) { e.Live = true; JitLiveImms++; }   // (live immediate: read from memory)
            if (JitFoldsAt(e, end)) { e.Folds = true; e.FoldNb = (byte)U8(end); e.FoldRel = Sext8(U8(end + 1)); }
            found.Add((pa, e)); if (e.Kind != DK_INTERP) anyCompiled = true;
            if (e.Kind is not (DK_JMP or DK_CALL or DK_RET) && !(e.Kind == DK_INTERP && e.Op == 1)) { todo.Enqueue(end); if (e.Kind != DK_JCC && !e.Folds) fall.Add(end); }   // (fall: run right after it, its lookup maybe skipped - sequential skip)
            if (e.Kind is DK_JMP or DK_JCC) todo.Enqueue(end + e.Imm);
            if (e.Folds) todo.Enqueue(end + 2 + e.FoldRel);
        }
        if (!anyCompiled) { en.Failed = true; return; }
        found.Sort((x, y) => x.pa.CompareTo(y.pa));
        var pas = new uint[found.Count]; var ins = new DEntry[found.Count];
        for (int i = 0; i < pas.Length; i++) { pas[i] = found[i].pa; ins[i] = found[i].e; }
        // the byte ranges the code depends on: each instruction, plus the JZ / JNZ a CMP / TEST folds with
        var ranges = new List<(uint a, uint b)>();
        for (int k = 0; k < pas.Length; k++)
        {
            uint a = pas[k], b = a + ins[k].Len;
            if (ins[k].Folds) b += 2;
            if (ins[k].Live) b -= 4;   // (a live immediate: its bytes are read at run time, not compared)
            if (ranges.Count > 0 && a <= ranges[^1].b) ranges[^1] = (ranges[^1].a, Math.Max(ranges[^1].b, b));
            else ranges.Add((a, b));
        }
        // the bytes it is compiled from, as they are now (the compiled code runs only while memory still holds them)
        int nb = 0; foreach (var (a, b) in ranges) nb += (int)(b - a);
        var bytes = new byte[nb]; nb = 0;
        foreach (var (a, b) in ranges) { Mem.AsSpan((int)a, (int)(b - a)).CopyTo(bytes.AsSpan(nb)); nb += (int)(b - a); }
        var code = new JitCode
        {
            Start = start, Count = pas.Length, C16 = _c16, Pg = pg, Bytes = bytes,
            RunAt = ranges.Select(r => (int)r.a).ToArray(), RunLen = ranges.Select(r => (int)(r.b - r.a)).ToArray(),
            Addrs = pas,
        };
        JitStamp(code);   // (a store to these bytes from now on moves their pages on: the install compares them then)
        uint lo = ranges[0].a, hi = ranges[^1].b;
        bool c16 = _c16, ioMem = _ioMem;
        uint memSize = MemSize;
        var spec = JitSegSpec(key, ins);
        // X86_JITCACHE (I386.JitCache.cs): the same region compiled before (this run or an earlier one, same bytes, same assumptions) -> its code
        JcReq? jc = JcOn ? JcRequest(start, pas, ins, lo, hi, c16, memSize, ioMem, pg, spec) : null;
        if (jc != null && jc.Ready != null) { code.Fn = jc.Ready; JcHitsReady++; JitInstall(code, key); return; }
        int[]? xc = jc?.Extra;
        if (!JitAsync)
        {
            try { code.Fn = jc?.Hit ?? Emit(start, pas, ins, lo, hi, c16, memSize, ioMem, pg, spec, xc); code.Fn(this, -1); if (jc != null) JcCompiled(jc, code.Fn); }   // (entry -1: only compiles it, the .NET JIT, and declines)
            catch (Exception) { en.Failed = true; return; }
            JitInstall(code, key);
            return;
        }
        // in the background: the interpreter runs this code meanwhile (it does exactly what the compiled code will do, so when the
        // compiled code takes over changes nothing but the speed); the result is installed by the CPU's own thread, between batches
        // every address of the region waits for it (else each would get hot and ask for its own region meanwhile)
        foreach (uint pa in pas)
        {
            int ji = JitIndex(pa);
            ref var e = ref _je[ji];
            e.Used = true;
            if (e.Start == pa && e.C16 == c16 && e.Pg == pg && _jcd[ji] != null) continue;
            e.Start = pa; e.C16 = c16; e.Pg = pg; _jcd[ji] = null; e.Failed = false; e.Pending = true;
        }
        en.Pending = true;
        if (JitWatch != 0 && pas.Contains(JitWatch)) Console.Error.WriteLine($"jw: post region {start:X8} n {pas.Length}");
        _jitPending++; JitHotUpdate(); JitPosted++; JitInstrCompiled += pas.Length;
        if (!_jitSeenKeys.Add(key)) JitRecompiles++;
        if (JitHotQ) en.Visits = 0;
        JitWorker.Post(this, start, () =>
        {
            if (JitDelay > 0) { int ms; lock (_jitDelayRng) ms = _jitDelayRng.Next(4); if (ms > 0) System.Threading.Thread.Sleep(ms); }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { code.Fn = jc?.Hit ?? Emit(start, pas, ins, lo, hi, c16, memSize, ioMem, pg, spec, xc); System.Threading.Interlocked.Add(ref JitBuildTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0); code.Fn(this, -1); if (JitDelay > 0) for (int r = 0; r < 2000; r++) code.Fn(this, -1); if (jc != null) JcCompiled(jc, code.Fn); }
            catch (Exception ex) { code.Fn = null!; if (JitStats) Console.Error.WriteLine($"jit: {start:X8} failed: {ex.GetType().Name} {ex.Message}"); }
            System.Threading.Interlocked.Add(ref JitEmitTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            _jitDone.Enqueue((code, key));
        });
    }

    private void JitInstall(JitCode code, ulong key)
    {
        JitCompiled++;
        if (JitStats) _jitAll.Add(code);
        if (_jitCodes.Count >= JitKeep) { _jitCodes.Clear(); JitKeepClears++; }   // a long session that has run a lot of different code: let the old code go
        if (!_jitCodes.TryGetValue(key, out var vs) || vs.Length != JitVariants) _jitCodes[key] = vs = new JitCode[JitVariants];
        Array.Copy(vs, 0, vs, 1, vs.Length - 1); vs[0] = code;   // (newest first; the oldest falls off)
        JitRegister(code);
    }

    /// <summary>Install the blocks the background compiler has finished (the CPU's thread, before a batch): each one only if memory
    /// still holds the bytes it was compiled from.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void JitDrain()
    {
        if (JitDelay > 0) { int r; lock (_jitDelayRng) r = _jitDelayRng.Next(JitDelay); if (r != 0) return; }   // X86_JITDELAY: install at a random later batch
        while (_jitDone.TryDequeue(out var d))
        {
            _jitPending--; JitHotUpdate();
            var (code, key) = d;
            bool ok = code.Fn != null && JitSame(code);
            if (JitWatch != 0 && code.Addrs.Contains(JitWatch)) Console.Error.WriteLine($"jw: drain region {code.Start:X8} ok {ok} fn {code.Fn != null}");
            foreach (uint pa in code.Addrs)
            {   // the addresses that waited for it: interpreted again, counted again from 0 (a failed start: never again)
                ref var e = ref _je[JitIndex(pa)];
                if (!e.Used || e.Start != pa || e.C16 != code.C16 || e.Pg != code.Pg || !e.Pending) continue;
                e.Pending = false; e.Visits = 0;
                if (code.Fn == null && pa == code.Start) e.Failed = true;
            }
            if (ok) JitInstall(code, key); else JitDropped++;
        }
    }

    /// <summary>Wait for the background compiler, then install what it made (tests: the same blocks as a synchronous compile).</summary>
    public void JitFlush()
    {
        while (_jitPending > 0) { System.Threading.Thread.Yield(); JitDrain(); }
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<(JitCode, ulong)> _jitDone = new();
    /// <summary>X86_JITDELAY=N (tests): the background compiler sleeps 0-3 ms per block, repeats its trial call 2000 times (it must not touch the
    /// running CPU) and each drain installs only 1 time in N, so compiled
    /// code takes over at random moments (the jit-parity gate's async-vs-NOJIT run: async JIT must be exact whatever the timing).</summary>
    private static readonly int JitDelay = int.TryParse(Environment.GetEnvironmentVariable("X86_JITDELAY"), out int jd) ? Math.Max(0, jd) : 0;
    private readonly Random _jitDelayRng = new();
    private int _jitPending;
    /// <summary>X86_JITBACKP=k (warm-up): the hot threshold is JitHot + k per region still waiting for the background compiler (at most
    /// X86_JITBACKMAX, default 8192): a burst of new code (a state load, a level) only queues what stays hot while the compiler is behind.
    /// Never changes what compiled code does (only when a region is asked for); X86_JITSYNC: nothing is ever pending, so JitHot as before.</summary>
    private static readonly int JitBackP = int.TryParse(Environment.GetEnvironmentVariable("X86_JITBACKP"), out int bp) ? Math.Max(0, bp) : 0;
    private static readonly int JitBackMax = int.TryParse(Environment.GetEnvironmentVariable("X86_JITBACKMAX"), out int bm) ? bm : 8192;
    private int _jitHotNow = JitHot;
    private void JitHotUpdate() { if (JitBackP > 0) _jitHotNow = (int)Math.Min(JitBackMax, JitHot + (long)JitBackP * _jitPending); }
    /// <summary>X86_JITSYNC: compile on the CPU's own thread, at once (as before: a hitch, but the compiled code runs from the 32nd visit).</summary>
    private static readonly bool JitAsync = Environment.GetEnvironmentVariable("X86_JITSYNC") == null;

    /// <summary>The background compiler, shared by every CPU in the process.</summary>
    /// <summary>X86_JITHOTQ=1: the background compiler takes the waiting region the interpreter has run most often since it was queued
    /// (its start's visit count, kept counting while pending), newest first on ties; default: newest first. Only the moment a region
    /// installs changes (the async-JIT parity gate covers any timing).</summary>
    private static readonly bool JitHotQ = Environment.GetEnvironmentVariable("X86_JITHOTQ") == "1";
    private int JitPendingHeat(uint start)
    {
        ref var e = ref _je[JitIndex(start)];
        return e.Start == start && e.Pending ? System.Threading.Volatile.Read(ref e.Visits) : -1;
    }

    /// <summary>The background compiler, shared by every CPU in the process.</summary>
    private static class JitWorker
    {
        // newest first: the block hot right now matters more than one queued while a burst of new code went by
        private static readonly System.Collections.Concurrent.BlockingCollection<Action> Q = new(new System.Collections.Concurrent.ConcurrentStack<Action>());
        private static readonly List<(I386 cpu, uint start, Action a)> HQ = new();
        private static readonly System.Threading.SemaphoreSlim HSem = new(0);
        public static readonly int Threads = int.TryParse(Environment.GetEnvironmentVariable("X86_JITTHREADS"), out int jt) ? Math.Max(1, jt) : Math.Clamp(Math.Min(Environment.ProcessorCount / 2, Environment.ProcessorCount - 2), 1, 6);
        /// <summary>The compilers run below normal priority (a game load's ~1000 regions must not starve the CPU / sound threads);
        /// X86_JITPRIONORMAL: normal priority (before 2026-11-21).</summary>
        private static readonly bool NormalPrio = Environment.GetEnvironmentVariable("X86_JITPRIONORMAL") != null;
        static JitWorker()
        {
            // half the host threads (at most 6): a game's start asks for ~1000 blocks at once
            for (int i = 0; i < Threads; i++)
                new System.Threading.Thread(() => { if (JitHotQ) for (;;) TakeHot()(); else foreach (var a in Q.GetConsumingEnumerable()) a(); })
                { IsBackground = true, Name = "x86 block compiler", Priority = NormalPrio ? System.Threading.ThreadPriority.Normal : System.Threading.ThreadPriority.BelowNormal }.Start();
        }
        private static Action TakeHot()
        {
            HSem.Wait();
            lock (HQ)
            {
                int best = HQ.Count - 1, bh = int.MinValue;
                for (int i = HQ.Count - 1; i >= 0; i--) { int h = HQ[i].cpu.JitPendingHeat(HQ[i].start); if (h > bh) { bh = h; best = i; } }
                var a = HQ[best].a; HQ.RemoveAt(best);
                return a;
            }
        }
        public static void Post(I386 cpu, uint start, Action a)
        {
            if (!JitHotQ) { Q.Add(a); return; }
            lock (HQ) HQ.Add((cpu, start, a));
            HSem.Release();
        }
    }

    /// <summary>A CMP / TEST whose next bytes are JZ / JNZ rel8 (tiny386's try_jcc8 folds them).</summary>
    private bool JitFoldsAt(in DEntry e, uint end)
    {
        bool cmpTest = e.Kind is DK_TESTEG or DK_G3TEST || (e.Kind is DK_ALUEG or DK_ALUGE or DK_ALUAI && e.Op == 7) || (e.Kind == DK_GRP1 && e.Reg == 7);
        return cmpTest && (U8(end) & ~1u) == 0x74;
    }

    /// <summary>Every instruction of a region is an entry point (a batch may end anywhere in it).</summary>
    private void JitRegister(JitCode code)
    {
        for (int k = 0; k < code.Addrs.Length; k++)
        {
            uint pa = code.Addrs[k];
            int ji = JitIndex(pa);
            ref var e = ref _je[ji];
            e.Used = true;
            if (pa != code.Start && e.Start == pa && e.C16 == code.C16 && e.Pg == code.Pg && _jcd[ji] is JitCode ec && ec.Start == pa) continue;   // keep a region that starts there itself
            e.Start = pa; e.C16 = code.C16; e.Pg = code.Pg; e.Failed = false; e.Pending = false; _jcd[ji] = code; e.K = (ushort)k;
        }
    }

    private JitFn Emit(uint start, uint[] pas, DEntry[] ins, uint lo, uint hi, bool c16, uint memSize, bool ioMem, bool pg, SegSpec? spec,
        int[]? extraCost = null, System.Reflection.Emit.ModuleBuilder? into = null, string? intoName = null, bool intoOpt = false)
    {   // (extraCost: JitExtraCost per instruction, taken on the CPU's thread; into: emit a static method into that module instead, X86_JITCACHE, I386.JitCache.cs)
        var mm = JitMethods.Value;
        DynamicMethod? dm = null; System.Reflection.Emit.TypeBuilder? tb = null;
        ILGenerator il;
        if (into != null)
        {   // (the translation cache's persisted copy: a static method x86blk of type intoName)
            var mbp = (tb = into.DefineType(intoName!, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract))
                .DefineMethod("x86blk", MethodAttributes.Public | MethodAttributes.Static, typeof(int), new[] { typeof(I386), typeof(int) });
            if (intoOpt) mbp.SetImplementationFlags(MethodImplAttributes.AggressiveOptimization);
            il = mbp.GetILGenerator();
        }
        else if (JitTier)
        {   // a static method of a (collectible) emitted type: the runtime's tiered compilation applies (DynamicMethod is always fully
            // optimized): a quick tier-0 compile now, the optimized one in the background once the block has run 30 times
            var mb0 = (tb = JitTierModule().DefineType("x86blk" + System.Threading.Interlocked.Increment(ref _jitTierN), TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract))
                .DefineMethod("x86blk", MethodAttributes.Public | MethodAttributes.Static, typeof(int), new[] { typeof(I386), typeof(int) });
            il = mb0.GetILGenerator();
        }
        else
        {
            dm = new DynamicMethod("x86blk", typeof(int), new[] { typeof(I386), typeof(int) }, typeof(I386), true);
            il = dm.GetILGenerator();
        }
        var ip0 = il.DeclareLocal(typeof(uint));   // the Ip of the region's start address
        var cyc = il.DeclareLocal(typeof(long));
        var st = il.DeclareLocal(typeof(int));
        var la = il.DeclareLocal(typeof(uint));
        var t = il.DeclareLocal(typeof(uint));
        var ea = il.DeclareLocal(typeof(uint));
        var sb = new LocalBuilder?[6];
        var sl = new LocalBuilder?[6];
        var slo = new LocalBuilder?[6];   // (expand-down: the lowest valid offset; 0 for the usual kind)
        var lfOff = il.DeclareLocal(typeof(uint)); var lfSeg = il.DeclareLocal(typeof(int)); var limFail = il.DefineLabel();
        int n = ins.Length;
        var index = new Dictionary<uint, int>();
        for (int k = 0; k < n; k++) index[pas[k]] = k;

        var rref = il.DeclareLocal(typeof(uint).MakeByRefType());   // ref R[0]
        var mref = il.DeclareLocal(typeof(byte).MakeByRefType());   // ref Mem[0]
        var cmref = il.DeclareLocal(typeof(byte).MakeByRefType());   // ref _codeMark[0]
        var spm = il.DeclareLocal(typeof(uint));
        var A = il.DeclareLocal(typeof(uint));
        var B = il.DeclareLocal(typeof(uint));
        var D = il.DeclareLocal(typeof(uint));
        var V = il.DeclareLocal(typeof(uint));
        var Y = il.DeclareLocal(typeof(int));
        var pax = il.DeclareLocal(typeof(uint));   // paging on: the physical address of `la` (FFFFFFFF: split over two pages)
        var lpn = il.DeclareLocal(typeof(uint));
        var te = il.DeclareLocal(typeof(nuint));
        var tlbL = il.DeclareLocal(typeof(uint).MakeByRefType()); var tlbX = il.DeclareLocal(typeof(uint).MakeByRefType());
        var permR = il.DeclareLocal(typeof(nuint));   // the _tlbTag half for this CPL
        var tlbR = il.DeclareLocal(typeof(uint).MakeByRefType()); var tlbW = il.DeclareLocal(typeof(uint).MakeByRefType());   // ref _tlbTag[permR] (reads), [permR + TlbSize] (writes)
        void LdPerm()
        {
            Ld0(); il.Emit(OpCodes.Ldfld, FCpl); I4(3); Op(OpCodes.Ceq); I4(2 * TlbSize); Op(OpCodes.Mul); Op(OpCodes.Conv_U); Stl(permR);
            Ld(tlbL); Ld(permR); I4(4); Op(OpCodes.Conv_U); Op(OpCodes.Mul); Op(OpCodes.Add); Stl(tlbR);
            Ld(tlbR); I4(4 * TlbSize); Op(OpCodes.Conv_U); Op(OpCodes.Add); Stl(tlbW);
        }

        void Call(string name) => il.Emit(OpCodes.Call, mm[name]);
        void Ld0() => il.Emit(OpCodes.Ldarg_0);
        void I4(int v) => il.Emit(OpCodes.Ldc_I4, v);
        void U4(uint v) => il.Emit(OpCodes.Ldc_I4, unchecked((int)v));
        void IpAt(uint pa) { il.Emit(OpCodes.Ldloc, ip0); U4(pa - start); il.Emit(OpCodes.Add); }   // the Ip of address pa (wraps like the CPU's)
        void Bool(bool b) => il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        // pending clock / step counts (X86_PEND: on; off by default, measured slower): the instruction clock and the step budget are counted at compile time along the
        // fall-through path and added to the locals only where they are read (exits, out-of-line calls) or where paths join (a jump to an
        // instruction, an entry: each instruction k has a canonical pending pair P[k] / Q[k]). cyc + pc = the clock; st - ps = the budget.
        long pc = 0; int ps = 0;
        void StDec() { if (UsePend) ps++; else { il.Emit(OpCodes.Ldloc, st); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, st); } }
        void LdCyc() { il.Emit(OpCodes.Ldloc, cyc); if (pc != 0) { il.Emit(OpCodes.Ldc_I8, pc); il.Emit(OpCodes.Add); } }
        void LdSt() { il.Emit(OpCodes.Ldloc, st); if (ps != 0) { il.Emit(OpCodes.Ldc_I4, ps); il.Emit(OpCodes.Sub); } }
        void Adj(long tc, int ts)
        {   // the locals as instruction code expecting pending (tc, ts) wants them (runtime only: pc / ps stay)
            if (pc != tc) { il.Emit(OpCodes.Ldloc, cyc); il.Emit(OpCodes.Ldc_I8, pc - tc); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, cyc); }
            if (ps != ts) { il.Emit(OpCodes.Ldloc, st); il.Emit(OpCodes.Ldc_I4, ps - ts); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, st); }
        }
        void Ld(LocalBuilder l) => il.Emit(OpCodes.Ldloc, l);
        void Stl(LocalBuilder l) => il.Emit(OpCodes.Stloc, l);
        void Op(OpCode o) => il.Emit(o);
        void LdF(FieldInfo f) { Ld0(); il.Emit(OpCodes.Ldfld, f); }
        void StF(FieldInfo f, Action v) { Ld0(); v(); il.Emit(OpCodes.Stfld, f); }
        // sign-extend the uint on the stack from `bits` (as Sext)
        void SextTop(int bits) { if (bits == 8) Op(OpCodes.Conv_I1); else if (bits == 16) Op(OpCodes.Conv_I2); }

        // registers, as LReg / SReg: the bytes of R[] (little-endian), so a 16 / 8-bit write leaves the other bits alone
        void RegAddr(int byteOff) { Ld(rref); if (byteOff != 0) { I4(byteOff); Op(OpCodes.Add); } }
        void LdReg(int bits, int i)
        {
            if (bits == 32) { RegAddr(4 * i); Op(OpCodes.Ldind_U4); }
            else if (bits == 16) { RegAddr(4 * i); Op(OpCodes.Ldind_U2); }
            else { RegAddr(i > 3 ? 4 * (i - 4) + 1 : 4 * i); Op(OpCodes.Ldind_U1); }
        }
        void StReg(int bits, int i, Action v)
        {
            if (bits == 32) { RegAddr(4 * i); v(); Op(OpCodes.Stind_I4); }
            else if (bits == 16) { RegAddr(4 * i); v(); Op(OpCodes.Stind_I2); }
            else { RegAddr(i > 3 ? 4 * (i - 4) + 1 : 4 * i); v(); Op(OpCodes.Stind_I1); }
        }
        // memory at physical address `la` (paging off: = linear; paging on: pax, translated by Xlat): plain RAM inline, the rest through Load*Slow / Store*Slow
        void IfRam(LocalBuilder addr, Label notRam)
        {
            if (ioMem && memSize > 0xc0000 && !NoRamRange)
            {   // [C0000, memSize) in one compare (the usual case), else below A0000 (X86_NORAMRANGE: the old two tests)
                var ram = il.DefineLabel();
                Ld(addr); U4(0xc0000); Op(OpCodes.Sub); U4(memSize - 0xc0000); il.Emit(OpCodes.Blt_Un, ram);
                Ld(addr); U4(0xa0000); il.Emit(OpCodes.Bge_Un, notRam);
                il.MarkLabel(ram);
                return;
            }
            Ld(addr); U4(memSize); il.Emit(OpCodes.Bge_Un, notRam);
            if (ioMem) { Ld(addr); U4(0xa0000); Op(OpCodes.Sub); U4(0x20000); il.Emit(OpCodes.Blt_Un, notRam); }
        }
        void LdMem(int bits, LocalBuilder addr)   // leaves the value in V
        {
            var slow = il.DefineLabel(); var done = il.DefineLabel();
            var phys = pg ? pax : addr;   // (paging on: `addr` is linear, translated to pax)
            IfRam(phys, slow);
            Ld(mref); Ld(phys); Op(OpCodes.Conv_U); Op(OpCodes.Add);
            if (bits != 8) il.Emit(OpCodes.Unaligned, (byte)1);
            Op(bits == 8 ? OpCodes.Ldind_U1 : bits == 16 ? OpCodes.Ldind_U2 : OpCodes.Ldind_U4);
            Stl(V); il.Emit(OpCodes.Br, done);
            il.MarkLabel(slow);
            Ld0(); I4(bits); Ld(addr); Call(pg ? "JRdLin" : "JRdSlow"); Stl(V);
            il.MarkLabel(done);
        }
        void StMem(int bits, LocalBuilder addr, LocalBuilder val)
        {
            var slow = il.DefineLabel(); var done = il.DefineLabel();
            var phys = pg ? pax : addr;
            IfRam(phys, slow);
            Ld(mref); Ld(phys); Op(OpCodes.Conv_U); Op(OpCodes.Add); Ld(val);
            if (bits != 8) il.Emit(OpCodes.Unaligned, (byte)1);
            Op(bits == 8 ? OpCodes.Stind_I1 : bits == 16 ? OpCodes.Stind_I2 : OpCodes.Stind_I4);
            // a code line (I386.cs CodeWr): the page moves on
            Ld(cmref); Ld(phys); I4(6); Op(OpCodes.Shr_Un); Op(OpCodes.Conv_U); Op(OpCodes.Add); Op(OpCodes.Ldind_U1);
            if (bits != 8 && !CmOne) { Ld(cmref); Ld(phys); I4(bits / 8 - 1); Op(OpCodes.Add); I4(6); Op(OpCodes.Shr_Un); Op(OpCodes.Conv_U); Op(OpCodes.Add); Op(OpCodes.Ldind_U1); Op(OpCodes.Or); }
            if (bits != 8 && CmOne)
            {   // (X86_CMONE: the second line only when the store crosses into it)
                var code = il.DefineLabel();
                il.Emit(OpCodes.Brtrue, code);
                Ld(phys); I4(63); Op(OpCodes.And); I4(64 - bits / 8); il.Emit(OpCodes.Ble_Un, done);
                Ld(cmref); Ld(phys); I4(bits / 8 - 1); Op(OpCodes.Add); I4(6); Op(OpCodes.Shr_Un); Op(OpCodes.Conv_U); Op(OpCodes.Add); Op(OpCodes.Ldind_U1);
                il.Emit(OpCodes.Brfalse, done);
                il.MarkLabel(code);
                Ld0(); Ld(phys); I4(bits / 8); Call("CodeWrite");
                il.Emit(OpCodes.Br, done);
            }
            else il.Emit(OpCodes.Brfalse, done);
            Ld0(); Ld(phys); I4(bits / 8); Call("CodeWrite");
            il.Emit(OpCodes.Br, done);
            il.MarkLabel(slow);
            Ld0(); I4(bits); Ld(addr); Ld(val); Call(pg ? "JWrLin" : "JWrSlow");
            il.MarkLabel(done);
        }
        // Alu(op, bits, A, B) -> D, with the same lazy-flag fields (ADC / SBB read CF: through Alu itself)
        void AluAB(int op, int bits)
        {
            switch (op)
            {
                case 0: case 5: case 7:
                    Ld(A); SextTop(bits); Stl(A); Ld(B); SextTop(bits); Stl(B);
                    StF(FSrc1, () => Ld(A)); StF(FSrc2, () => Ld(B));
                    Ld(A); Ld(B); Op(op == 0 ? OpCodes.Add : OpCodes.Sub); SextTop(bits); Stl(D);
                    StF(FDst, () => Ld(D)); StF(FOp, () => I4(op == 0 ? (int)Cc.ADD : (int)Cc.SUB)); StF(FMask, () => U4(ALL));
                    break;
                case 1: case 4: case 6:
                    Ld(A); Ld(B); Op(op == 1 ? OpCodes.Or : op == 4 ? OpCodes.And : OpCodes.Xor); SextTop(bits); Stl(D);
                    StF(FDst, () => Ld(D)); StF(FOp, () => I4(op == 1 ? (int)Cc.OR : op == 4 ? (int)Cc.AND : (int)Cc.XOR)); StF(FMask, () => U4(LOGIC));
                    break;
                default:
                    Ld0(); I4(op); I4(bits); Ld(A); Ld(B); Call("JAlu"); Stl(D);
                    break;
            }
        }
        // Cond(c) on the stack as a bool: ZF / SF inline, the rest through Cond
        void CondTop(uint c)
        {
            uint cc = c & 0xf;
            if (cc is 4 or 5 or 8 or 9)
            {
                uint flag = cc < 8 ? ZF : SF;
                var lazy = il.DefineLabel(); var done = il.DefineLabel();
                LdF(FMask); U4(flag); Op(OpCodes.And); il.Emit(OpCodes.Brtrue, lazy);
                LdF(FFlags); U4(flag); Op(OpCodes.And); Op(OpCodes.Ldc_I4_0); Op(OpCodes.Cgt_Un); il.Emit(OpCodes.Br, done);
                il.MarkLabel(lazy);
                if (cc < 8) { LdF(FDst); Op(OpCodes.Ldc_I4_0); Op(OpCodes.Ceq); }
                else { LdF(FDst); I4(31); Op(OpCodes.Shr_Un); }
                il.MarkLabel(done);
                if ((cc & 1) != 0) { Op(OpCodes.Ldc_I4_0); Op(OpCodes.Ceq); }
                return;
            }
            {   // CF / OF conditions: after SUB / CMP (from the operands) or AND / OR / XOR / TEST (CF = OF = 0) inline, the rest through Cond
                uint need = cc switch { 0 or 1 => OF, 2 or 3 => CF, 6 or 7 => CF | ZF, 0xc or 0xd => SF | OF, 0xa or 0xb => 0, _ => ZF | SF | OF };
                if (need == 0) { Ld0(); U4(c); Call("JCond"); return; }
                var slow = il.DefineLabel(); var notSub = il.DefineLabel(); var done = il.DefineLabel();
                LdF(FMask); U4(need); Op(OpCodes.And); U4(need); il.Emit(OpCodes.Bne_Un, slow);
                LdF(FOp); I4((int)Cc.SUB); il.Emit(OpCodes.Bne_Un, notSub);
                uint bc = cc & 0xe;   // the taken-when-true form; odd cc negates
                if (bc == 0) { LdF(FSrc1); LdF(FSrc2); Op(OpCodes.Xor); LdF(FDst); LdF(FSrc1); Op(OpCodes.Xor); Op(OpCodes.And); I4(31); Op(OpCodes.Shr_Un); }
                else if (bc == 2) { LdF(FSrc1); LdF(FSrc2); Op(OpCodes.Clt_Un); }
                else if (bc == 6) { LdF(FSrc1); LdF(FSrc2); Op(OpCodes.Cgt_Un); Op(OpCodes.Ldc_I4_0); Op(OpCodes.Ceq); }
                else if (bc == 0xc) { LdF(FSrc1); LdF(FSrc2); Op(OpCodes.Clt); }
                else { LdF(FSrc1); LdF(FSrc2); Op(OpCodes.Cgt); Op(OpCodes.Ldc_I4_0); Op(OpCodes.Ceq); }
                il.Emit(OpCodes.Br, done);
                il.MarkLabel(notSub);
                LdF(FOp); I4((int)Cc.AND); Op(OpCodes.Sub); I4(2); il.Emit(OpCodes.Bgt_Un, slow);
                if (bc == 0 || bc == 2) Op(OpCodes.Ldc_I4_0);
                else if (bc == 6) { LdF(FDst); Op(OpCodes.Ldc_I4_0); Op(OpCodes.Ceq); }
                else if (bc == 0xc) { LdF(FDst); I4(31); Op(OpCodes.Shr_Un); }
                else { LdF(FDst); Op(OpCodes.Ldc_I4_1); Op(OpCodes.Clt); }   // (int)dst <= 0
                il.Emit(OpCodes.Br, done);
                il.MarkLabel(slow);
                Ld0(); U4(c & 0xe); Call("JCond");
                il.MarkLabel(done);
                if ((cc & 1) != 0) { Op(OpCodes.Ldc_I4_0); Op(OpCodes.Ceq); }
            }
        }

        // entry -1 (the compile's trial call, which only makes .NET JIT the method): leave before touching ANY CPU state. The trial runs on the
        // background compiler's thread: the prologue's JSegLoad wrote the live CPU's _jsOk / _jsBase / ... meanwhile (a block running at that
        // moment then saw another block's segment values: the rare async-only divergence, 2026-11-21), and segGen read _jitCur
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4_0); var notTrial = il.DefineLabel(); il.Emit(OpCodes.Bge, notTrial);
        il.Emit(OpCodes.Ldc_I4, JIT_DECLINE); il.Emit(OpCodes.Ret);
        il.MarkLabel(notTrial);
        // prologue: decline when a segment the region uses has a null selector (the translation would fault)
        var usedSegs = new SortedSet<int>();
        foreach (var e in ins) { int m = InsSegs(e); for (int sg = 0; sg < 6; sg++) if ((m >> sg & 1) != 0) usedSegs.Add(sg); }
        // a segment the region uses but that is not usable now: only the instructions that use it leave (before running; at the entry
        // instruction: decline, the interpreter runs it and raises what it must); the rest of the region still runs compiled (X86_SEGLAZY=0: decline)
        var segOkL = new LocalBuilder?[6]; var segRdL = new LocalBuilder?[6];
        var ran = il.DeclareLocal(typeof(bool));
        int segMask = 0;
        foreach (int s in usedSegs) segMask |= 1 << s;
        bool segLoad = !NoSegLoad && segMask != 0;
        // JSegLoad with its memo test inline (X86_NOSEGINL: always the call): SegGen / PE / VM unchanged since the last computation = no call
        void SegLoadCall()
        {
            if (NoSegInl || NoSegMemo) { Ld0(); I4(segMask); Call("JSegLoad"); return; }
            var skip = il.DefineLabel(); var call = il.DefineLabel();
            Ld0(); il.Emit(OpCodes.Ldfld, FSegGen); Ld0(); il.Emit(OpCodes.Ldfld, FJsGen); il.Emit(OpCodes.Bne_Un, call);
            LdF(FFlags); I4(16); Op(OpCodes.Shr_Un); I4(2); Op(OpCodes.And); Ld0(); il.Emit(OpCodes.Ldfld, FCr0); I4(1); Op(OpCodes.And); Op(OpCodes.Or);
            Ld0(); il.Emit(OpCodes.Ldfld, FJsMode); il.Emit(OpCodes.Beq, skip);
            il.MarkLabel(call); Ld0(); I4(segMask); Call("JSegLoad");
            il.MarkLabel(skip);
        }
        // segment generation (X86_SEGGEN: on; off by default): when every segment the block uses is specialized, the block remembers the SegGen / VM / PE
        // key its last full check passed with; the same key again = nothing segment-related changed: no JSegLoad, no per-segment checks
        bool segGenFast = !NoSegGen && segLoad && spec != null && spec.Mask != 0 && (segMask & ~spec.Mask) == 0;
        var segKeyL = segGenFast ? il.DeclareLocal(typeof(uint)) : null; var segFastOk = il.DefineLabel(); var segAfter = il.DefineLabel();
        if (segGenFast)
        {
            Ld0(); Call("JSegKey"); Stl(segKeyL!);
            Ld0(); il.Emit(OpCodes.Ldfld, FJitCur); il.Emit(OpCodes.Ldfld, FSegKey); Ld(segKeyL!); il.Emit(OpCodes.Beq, segFastOk);
        }
        if (segLoad) SegLoadCall();
        // segment specialization (X86_NOSEGSPEC: off): a segment usable when the block was compiled has its base / span / lo as constants and
        // no per-instruction usability test; the prologue checks it still holds those values (else: decline, and the block is compiled again
        // without the assumption); likewise SpMask
        bool IsSpec(int s) => segLoad && spec != null && (spec.Mask >> s & 1) != 0;
        bool spSpec = segLoad && spec != null && spec.Mask != 0;
        void LdBase(int sg) { if (IsSpec(sg)) U4(spec!.Base[sg]); else il.Emit(OpCodes.Ldloc, sb[sg]!); }
        void LdSpm() { if (spSpec) U4(spec!.Spm); else Ld(spm); }
        if (spSpec)
        {
            var good = il.DefineLabel(); var bad = il.DefineLabel();
            foreach (int s in usedSegs)
            {
                if (!IsSpec(s)) continue;
                Ld0(); I4(s); U4(spec!.Base[s]); U4(spec.Span[s]); U4(spec.Lo[s]); Call("JSSame"); il.Emit(OpCodes.Brfalse, bad);
            }
            LdF(FSpm); U4(spec!.Spm); il.Emit(OpCodes.Beq, good);
            il.MarkLabel(bad);
            Ld0(); il.Emit(OpCodes.Ldarg_1); Call("JSpecFail");
            Ld0(); I4(40); il.Emit(OpCodes.Stfld, FDeclWhy); I4(JIT_DECLINE); il.Emit(OpCodes.Ret);
            il.MarkLabel(good);
            if (segGenFast)
            {   // (a real run only: entry -1 is the compile's trial call, _jitCur is some other block then)
                il.Emit(OpCodes.Ldarg_1); Op(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Blt, segAfter);
                Ld0(); il.Emit(OpCodes.Ldfld, FJitCur); Ld(segKeyL!); il.Emit(OpCodes.Stfld, FSegKey);
                il.Emit(OpCodes.Br, segAfter);
                il.MarkLabel(segFastOk);
                LdF(FSpm); U4(spec!.Spm); il.Emit(OpCodes.Bne_Un, bad);
            }
        }
        il.MarkLabel(segAfter);
        if (!segGenFast) il.MarkLabel(segFastOk);
        foreach (int s in usedSegs)
        {
            if (IsSpec(s)) continue;
            if (SegLazy)
            {
                segOkL[s] = il.DeclareLocal(typeof(bool));
                Ld0(); I4(s); Call(segLoad ? "JSOk" : "JSegOk"); Stl(segOkL[s]!);
                segRdL[s] = il.DeclareLocal(typeof(bool));
                Ld0(); I4(s); Call(segLoad ? "JSRd" : "JSegRdOk"); Stl(segRdL[s]!);
            }
            else
            {
                var ok = il.DefineLabel();
                Ld0(); I4(s); Call(segLoad ? "JSOk" : "JSegOk"); il.Emit(OpCodes.Brtrue, ok);
                Ld0(); I4(10 + s); il.Emit(OpCodes.Stfld, FDeclWhy);
                I4(JIT_DECLINE); il.Emit(OpCodes.Ret);
                il.MarkLabel(ok);
            }
            sb[s] = il.DeclareLocal(typeof(uint));
            Ld0(); I4(s); Call(segLoad ? "JSBase" : "JSegBase"); il.Emit(OpCodes.Stloc, sb[s]!);
            sl[s] = il.DeclareLocal(typeof(uint));
            Ld0(); I4(s); Call(segLoad ? "JSSpan" : "JSegSpan"); il.Emit(OpCodes.Stloc, sl[s]!);
            slo[s] = il.DeclareLocal(typeof(uint));
            Ld0(); I4(s); Call(segLoad ? "JSLo" : "JSegLo"); il.Emit(OpCodes.Stloc, slo[s]!);
        }
        bool anyLazy = segOkL.Any(x => x != null);   // (`ran` is read only by the lazy segment tests)
        Ld0(); Call("JCycle"); il.Emit(OpCodes.Stloc, cyc);
        Ld0(); Call("JSteps"); il.Emit(OpCodes.Stloc, st);
        LdF(FR); I4(0); il.Emit(OpCodes.Ldelema, typeof(uint)); Stl(rref);
        LdF(FMem); I4(0); il.Emit(OpCodes.Ldelema, typeof(byte)); Stl(mref);
        LdF(FCodeMark); I4(0); il.Emit(OpCodes.Ldelema, typeof(byte)); Stl(cmref);
        if (!spSpec) { LdF(FSpm); Stl(spm); }
        if (pg)
        {
            LdF(FTlbT); I4(0); il.Emit(OpCodes.Ldelema, typeof(uint)); Stl(tlbL);
            LdF(FTlbX); I4(0); il.Emit(OpCodes.Ldelema, typeof(uint)); Stl(tlbX);
            LdPerm();
        }
        {   // the shared segment-limit fault exit (LimitCheck): raise the fault and leave before that instruction, as the interpreter would
            var skip = il.DefineLabel();
            il.Emit(OpCodes.Br, skip);
            il.MarkLabel(limFail);
            Ld0(); Ld(lfSeg); Call("JLimitFault"); Op(OpCodes.Pop);
            Ld0(); Ld(ip0); Ld(lfOff); Op(OpCodes.Add); il.Emit(OpCodes.Ldloc, cyc); il.Emit(OpCodes.Ldloc, st); Call("JExitFail"); il.Emit(OpCodes.Ret);
            il.MarkLabel(skip);
        }

        var cost = new int[n]; var P = new long[n]; var Q = new int[n];
        for (int k = 0; k < n; k++)
        {
            var e = ins[k];
            cost[k] = 1 + (extraCost != null ? extraCost[k] : JitExtraCost(pas[k], e.Kind, e.Reg, e.Bits, DcHasModrm(e.Kind) && e.Mod != 3));
            if (!UsePend || k == 0 || pas[k - 1] + ins[k - 1].Len != pas[k] || ins[k - 1].Kind is DK_JMP or DK_CALL or DK_RET or DK_INTERP or DK_REPSTR) continue;
            P[k] = P[k - 1] + cost[k - 1]; Q[k] = Q[k - 1] + 1;
            if (P[k] > 1 << 20) { P[k] = 0; Q[k] = 0; }
        }
        LocalBuilder? xOff = null, xNext = null; Label sharedLeave = default;
        var body = new Label[n];
        var pre = new Label[n];
        for (int k = 0; k < n; k++) { body[k] = il.DefineLabel(); pre[k] = il.DefineLabel(); }
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Switch, pre);
        Ld0(); I4(2); il.Emit(OpCodes.Stfld, FDeclWhy);
        il.Emit(OpCodes.Ldc_I4, JIT_DECLINE); il.Emit(OpCodes.Ret);
        for (int k = 0; k < n; k++)
        {   // entered at instruction k (Exec1 checked the step budget)
            il.MarkLabel(pre[k]);
            Ld0(); Call("JIp"); U4(pas[k] - start); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, ip0);
            Adj(P[k], Q[k]);
            il.Emit(OpCodes.Br, body[k]);
        }

        // after instruction k (st already counted down): go on at Ip `next` (on the stack-free action), inside the region when it is the
        // region's address `target` and the step budget allows, else leave with Ip = instruction k, NextIp = next
        void Continue(int k, Action next, uint? target, bool fallsInto)
        {
            var leave = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, st); il.Emit(OpCodes.Ldc_I4, ps); il.Emit(OpCodes.Ble, leave);
            // inside the region only on the same 4 KB page: the interpreter fetches the first instruction on another page the slow way,
            // where a CMP / TEST does not fold with the next JZ / JNZ
            if (target is uint tg && (tg >> 12) == (pas[k] >> 12) && index.TryGetValue(tg, out int j))
            {
                if (!fallsInto) { next(); IpAt(tg); il.Emit(OpCodes.Bne_Un, leave); }   // a jump: the target Ip (with a 16-bit wrap) must be that address
                if (!(fallsInto && j == k + 1)) { Adj(P[j], Q[j]); il.Emit(OpCodes.Br, body[j]); }
                else
                {
                    var go = il.DefineLabel();
                    Adj(P[j], Q[j]);
                    il.Emit(OpCodes.Br, go);
                    il.MarkLabel(leave);
                    LeaveFall(k, tg);
                    il.MarkLabel(go);
                    return;
                }
            }
            il.MarkLabel(leave);
            if (fallsInto && target is uint ft) LeaveFall(k, ft);
            else { Ld0(); IpAt(pas[k]); next(); LdCyc(); LdSt(); Call("JExitLink"); il.Emit(OpCodes.Ret); }
        }
        // the budget-out exit of a fall-through (Ip = instruction k, NextIp = the next address): one shared JExitLink call per block,
        // each site only sets two offsets (X86_SHAREDEXIT opt-in, default a call per site; a call site makes the .NET JIT spill around it)
        void LeaveFall(int k, uint nx)
        {
            if (NoSharedExit || UsePend) { Ld0(); IpAt(pas[k]); IpAt(nx); LdCyc(); LdSt(); Call("JExitLink"); il.Emit(OpCodes.Ret); return; }
            if (xOff == null) { xOff = il.DeclareLocal(typeof(uint)); xNext = il.DeclareLocal(typeof(uint)); sharedLeave = il.DefineLabel(); }
            U4(pas[k] - start); Stl(xOff); U4(nx - start); Stl(xNext!); il.Emit(OpCodes.Br, sharedLeave);
        }
        void LeaveAfterDec(int k, Action next) { var (c0, s0) = (pc, ps); StDec(); LeaveAfter(k, next); pc = c0; ps = s0; }   // (an exit path: the pending counts of the path that goes on stay)
        void LeaveAfter(int k, Action next)
        {
            Ld0(); IpAt(pas[k]); next(); LdCyc(); LdSt(); Call("JExitLink"); il.Emit(OpCodes.Ret);
        }
        // the interpreter's segment limit check (LimitOk, expand-up only: a region with an expand-down segment is declined):
        // past the limit = leave before this instruction with the fault, as the interpreter would
        void LimitCheck(int k, int seg, LocalBuilder off, int size)
        {
            // one compare inline; the fault exit is shared by the whole region (a per-site exit made the bench region ~2.5x slower)
            var okL = il.DefineLabel();
            if (IsSpec(seg)) { Ld(off); if (spec!.Lo[seg] != 0) { U4(spec.Lo[seg]); Op(OpCodes.Sub); } U4(unchecked(spec.Span[seg] - (uint)(size - 1))); }
            else { Ld(off); Ld(slo[seg]!); Op(OpCodes.Sub); Ld(sl[seg]!); U4((uint)(size - 1)); Op(OpCodes.Sub); }
            il.Emit(OpCodes.Ble_Un, okL);
            U4(pas[k] - start); Stl(lfOff); I4(seg); Stl(lfSeg); Adj(0, 0); il.Emit(OpCodes.Br, limFail);
            il.MarkLabel(okL);
        }
        void ExitFail(int k)
        {
            Ld0(); IpAt(pas[k]); LdCyc(); LdSt(); Call("JExitFail"); il.Emit(OpCodes.Ret);
        }
        // paging on: la -> pax, as the interpreter's TranslateLaddr (rwm 1 read, 3 read + write): a TLB hit that allows the access at
        // this CPL inline (a write sets the PTE's dirty bit, as the interpreter's hit does), else JXlat (a page fault: leave with it)
        void Xlat(int k, int size, bool write)
        {
            if (!pg) return;
            var slow = il.DefineLabel(); var done = il.DefineLabel();
            // (_tlbTag: one compare = a TLB hit allowed at this CPL, a write's dirty bit already set; the arrays through refs taken once in
            // the prologue, no bounds checks: the index < 4 * TlbSize)
            Ld(la); I4(12); Op(OpCodes.Shr_Un); Stl(lpn);
            Ld(lpn); if (TlbHash) { Ld(lpn); I4(9); Op(OpCodes.Shr_Un); Op(OpCodes.Xor); Ld(lpn); I4(18); Op(OpCodes.Shr_Un); Op(OpCodes.Xor); } I4(TlbSize - 1); Op(OpCodes.And); Op(OpCodes.Conv_U); Stl(te);
            Ld(write ? tlbW : tlbR); Ld(te); I4(4); Op(OpCodes.Mul); Op(OpCodes.Add); Op(OpCodes.Ldind_U4);
            Ld(lpn); il.Emit(OpCodes.Bne_Un, slow);
            if (size > 1) { Ld(la); I4(0xfff); Op(OpCodes.And); I4(4096 - size); il.Emit(OpCodes.Bgt_Un, slow); }
            Ld(tlbX); Ld(te); I4(4); Op(OpCodes.Mul); Op(OpCodes.Add); Op(OpCodes.Ldind_U4); Ld(la); Op(OpCodes.Xor); Stl(pax); il.Emit(OpCodes.Br, done);
            il.MarkLabel(slow);
            var ok = il.DefineLabel();
            Ld0(); Ld(la); I4(write ? 3 : 1); I4(size); Call("JXlat"); il.Emit(OpCodes.Brtrue, ok);
            ExitFail(k);
            il.MarkLabel(ok);
            Ld0(); Call("JPa"); Stl(pax);
            il.MarkLabel(done);
        }

        for (int k = 0; k < n; k++)
        {
            var e = ins[k];
            uint pa = pas[k], pe = pa + e.Len;
            int bits = e.Bits;
            bool mem = DcHasModrm(e.Kind) && e.Mod != 3;
            il.MarkLabel(body[k]);
            pc = P[k]; ps = Q[k];
            if (e.Kind == DK_INTERP)
            {   // out of line, through the interpreter's own loop body (it counts the instruction clock itself)
                if (SegLazy && anyLazy) { I4(1); Stl(ran); }   // (something ran: a later unusable segment must leave, not decline)
                var notFail = il.DefineLabel(); var notEnd = il.DefineLabel(); var leaveG = il.DefineLabel();
                Ld0(); IpAt(pa); LdCyc(); LdSt(); Call("JInterp"); Stl(Y);
                Ld(Y); I4(FAIL); il.Emit(OpCodes.Bne_Un, notFail); I4(FAIL); Op(OpCodes.Ret);
                il.MarkLabel(notFail);
                Ld(Y); I4(END); il.Emit(OpCodes.Bne_Un, notEnd); I4(JIT_END); Op(OpCodes.Ret);
                il.MarkLabel(notEnd);
                Ld0(); Call("JCycle"); Stl(cyc);
                Ld0(); Call("JSteps"); Op(OpCodes.Ldc_I4_1); Op(OpCodes.Sub); Stl(st);
                pc = 0; ps = 0;
                Ld0(); Bool(c16); Bool(pg); U4(pa); I4(segMask); Bool(e.Mod != 0); Call("JStill"); il.Emit(OpCodes.Brfalse, leaveG);
                if (e.Rm == 0 || NoKeepSegs)   // (DcKeepsSegs: segments, SS size and CPL stay as they were: nothing to reload)
                {
                    if (segLoad)
                    {
                        SegLoadCall();
                        foreach (int s in usedSegs)
                        {
                            if (IsSpec(s)) { Ld0(); I4(s); U4(spec!.Base[s]); U4(spec.Span[s]); U4(spec.Lo[s]); Call("JSSame"); il.Emit(OpCodes.Brfalse, leaveG); continue; }   // (changed: leave after it)
                            Ld0(); I4(s); Call("JSBase"); Stl(sb[s]!); Ld0(); I4(s); Call("JSSpan"); Stl(sl[s]!); Ld0(); I4(s); Call("JSLo"); Stl(slo[s]!);
                        }
                    }
                    else foreach (int s in usedSegs) { Ld0(); I4(s); Call("JSegBase"); Stl(sb[s]!); Ld0(); I4(s); Call("JSegSpan"); Stl(sl[s]!); Ld0(); I4(s); Call("JSegLo"); Stl(slo[s]!); }
                    if (spSpec) { Ld0(); Call("JSpm"); U4(spec!.Spm); il.Emit(OpCodes.Bne_Un, leaveG); }
                    else { Ld0(); Call("JSpm"); Stl(spm); }
                    if (pg) LdPerm();   // (an out-of-line instruction may have changed the CPL)
                }
                var notRep = il.DefineLabel();   // a REP string instruction that goes on: run it again
                Ld0(); Call("JNextIp"); IpAt(pa); il.Emit(OpCodes.Bne_Un, notRep);
                Ld(st); Op(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ble, leaveG);
                Adj(P[k], Q[k]); il.Emit(OpCodes.Br, body[k]);
                il.MarkLabel(notRep);
                var fall = il.DefineLabel();
                Ld0(); Call("JNextIp"); IpAt(pe); il.Emit(OpCodes.Beq, fall);
                il.MarkLabel(leaveG);
                Ld0(); Ld0(); Call("JIp"); Ld0(); Call("JNextIp"); LdCyc(); LdSt(); Call("JExitOk"); Op(OpCodes.Ret);
                il.MarkLabel(fall);
                Continue(k, () => IpAt(pe), pe, true);   // (falls into the next instruction's code, which follows)
                continue;
            }
            if (SegLazy)
            {   // the segments this instruction uses must be usable (see the prologue): else leave before it
                int my = InsSegs(e);
                bool readsOnly = InsReadsOnly(e);
                for (int sg = 0; sg < 6; sg++)
                {
                    if ((my >> sg & 1) == 0 || segOkL[sg] == null) continue;
                    var usable = il.DefineLabel(); var declineL = il.DefineLabel();
                    Ld(readsOnly ? segRdL[sg]! : segOkL[sg]!); il.Emit(OpCodes.Brtrue, usable);
                    Ld(ran); il.Emit(OpCodes.Brfalse, declineL);
                    Ld0(); IpAt(pa); IpAt(pa); LdCyc(); LdSt(); Call("JExitOk"); Op(OpCodes.Ret);
                    il.MarkLabel(declineL); Ld0(); I4(30 + sg); il.Emit(OpCodes.Stfld, FDeclWhy); I4(JIT_DECLINE); Op(OpCodes.Ret);
                    il.MarkLabel(usable);
                }
                if (anyLazy) { I4(1); Stl(ran); }
            }
            if (UsePend) pc += cost[k];
            else { il.Emit(OpCodes.Ldloc, cyc); il.Emit(OpCodes.Ldc_I4, cost[k]); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, cyc); }

            // effective address and linear address of a memory operand
            if (mem)
            {
                U4(e.Disp);
                if (e.Base < 8) { LdReg(32, e.Base); il.Emit(OpCodes.Add); }
                if (e.Index < 8) { LdReg(32, e.Index); if (e.Scale != 0) { I4(e.Scale); il.Emit(OpCodes.Shl); } il.Emit(OpCodes.Add); }
                if (e.A16) { I4(0xffff); il.Emit(OpCodes.And); }
                il.Emit(OpCodes.Stloc, ea);
                if (e.Kind is not (DK_LEA or DK_FPU or DK_MMX))   // (an ESC: the helper checks the limit after #NM, as the interpreter)
                {
                    LimitCheck(k, e.Seg, ea, Math.Max(1, bits / 8));
                    LdBase(e.Seg); il.Emit(OpCodes.Ldloc, ea); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, la);
                    // (the forms that run through a helper translate there themselves)
                    bool helper = e.Kind is DK_GRP3 || (e.Kind == DK_GRP5 && !Grp5Inline(e)) || (e.Kind == DK_GRP2 && e.Reg is not (4 or 5 or 6 or 7));
                    bool write = e.Kind is DK_MOVEG or DK_MOVSR or DK_MOVEI or DK_SETCC or DK_GRP2 or DK_XCHG ||
                                 (e.Kind == DK_ALUEG && e.Op != 7) || (e.Kind == DK_GRP1 && e.Reg != 7);
                    if (!helper) Xlat(k, Math.Max(1, bits / 8), write);
                }
            }

            void ReadE() { if (mem) { LdMem(bits, la); Ld(V); } else LdReg(bits, e.Rm); }
            void WriteET()
            {
                if (!mem) { StReg(bits, e.Rm, () => Ld(t)); return; }
                StMem(bits, la, t);
                SmcCheck(pg ? pax : la, bits / 8);
            }
            // a store that touched [lo, hi): leave right after this instruction (the code may have changed)
            void SmcCheck(LocalBuilder addr, int size)
            {
                var fine = il.DefineLabel();
                if (pg)
                {   // the physical address (a split store, FFFFFFFF: leave, it may have touched the code)
                    if (!ReferenceEquals(addr, pax)) { Ld0(); Ld(addr); I4(size); Call("JPhys"); Stl(pax); }
                    var one = il.DefineLabel();
                    Ld(pax); I4(-1); il.Emit(OpCodes.Bne_Un, one);
                    LeaveAfterDec(k, () => IpAt(pe));
                    il.MarkLabel(one);
                    addr = pax;
                }
                il.Emit(OpCodes.Ldloc, addr); U4(lo - (uint)size + 1); il.Emit(OpCodes.Sub); U4(hi - lo + (uint)size - 1); il.Emit(OpCodes.Bge_Un, fine);
                LeaveAfterDec(k, () => IpAt(pe));
                il.MarkLabel(fine);
            }
            // CMP / TEST followed (in memory) by JZ / JNZ rel8: tiny386's try_jcc8 folding, with the spin skip
            void Fold(bool pureConst)
            {
                if (!e.Folds) return;
                uint nb = e.FoldNb;
                uint rel = e.FoldRel;
                var notTaken = il.DefineLabel();
                LdF(FDst); if ((nb & 1) != 0) il.Emit(OpCodes.Brfalse, notTaken); else il.Emit(OpCodes.Brtrue, notTaken);
                IpAt(pe); U4(rel + 2); il.Emit(OpCodes.Add);
                if (c16) { I4(0xffff); il.Emit(OpCodes.And); }
                il.Emit(OpCodes.Stloc, t);
                // spin: if (t == Ip && pure && st > 1) { cyc += st - 1; st = 1; }
                var noSpin = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, t); IpAt(pa); il.Emit(OpCodes.Bne_Un, noSpin);
                if (!pureConst)
                {
                    if (pg) { Ld0(); il.Emit(OpCodes.Ldloc, la); I4(Math.Max(1, bits / 8)); Call("JSpinRam"); }
                    else { Ld0(); il.Emit(OpCodes.Ldloc, la); Call("JRam"); }
                    il.Emit(OpCodes.Brfalse, noSpin);
                }
                LdSt(); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ble, noSpin);
                il.Emit(OpCodes.Ldloc, cyc); LdSt(); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_I8);
                il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, cyc);   // (cyc + pc: the add commutes with the pending clock)
                il.Emit(OpCodes.Ldc_I4, 1 + ps); il.Emit(OpCodes.Stloc, st);
                il.MarkLabel(noSpin);
                { var (c0, s0) = (pc, ps); StDec(); Continue(k, () => il.Emit(OpCodes.Ldloc, t), pe + 2 + rel, false); pc = c0; ps = s0; }
                il.MarkLabel(notTaken);
            }
            void Prep()
            {
                Ld0(); IpAt(pa); IpAt(pe); U4(pe); Bool(e.O16); Bool(e.A16); I4(mem ? e.Seg : e.SegRaw);
                I4(e.Mod); I4(e.Reg); I4(e.Rm);
                if (mem) il.Emit(OpCodes.Ldloc, ea); else il.Emit(OpCodes.Ldc_I4_0);
                Call("JPrep");
            }
            void FailIfFalse()
            {
                var ok = il.DefineLabel();
                il.Emit(OpCodes.Brtrue, ok);
                ExitFail(k);
                il.MarkLabel(ok);
            }

            int op = e.Op;
            bool ends = false;   // the instruction leaves the region itself
            switch (e.Kind)
            {
                case DK_MOVGE:
                    ReadE(); Stl(t); StReg(bits, e.Reg, () => Ld(t));
                    break;
                case DK_STKOP:
                    Prep();
                    Ld0(); I4(op); Call("JStk"); FailIfFalse();
                    if (op != 0x61)
                    {   // the store went to SS base + the new ESP (PUSHA: eight of them)
                        Ld0(); I4(SEG_SS); Call("JSegBase"); LdReg(32, 4); LdSpm(); Op(OpCodes.And); Op(OpCodes.Add); Stl(la);
                        SmcCheck(la, (e.O16 ? 2 : 4) * (op == 0x60 ? 8 : 1));
                    }
                    break;
                case DK_FLAGOP:
                    if (op is 0xfa or 0x9b) Prep();   // (CLI / FWAIT may fault)
                    Ld0(); I4(op); Call("JFlagOp");
                    if (op is 0xfa or 0x9b) FailIfFalse(); else Op(OpCodes.Pop);
                    break;
                case DK_XCHG when mem:   // with memory (as the interpreter: the register gets E, then E gets the old register; no flags)
                    LdReg(bits, e.Reg); Stl(t);
                    LdMem(bits, la); StReg(bits, e.Reg, () => Ld(V));
                    WriteET();
                    break;
                case DK_XCHG:   // register with register (Mod 3): no memory, no flags
                    LdReg(bits, e.Rm); Stl(t);
                    StReg(bits, e.Rm, () => LdReg(bits, e.Reg));
                    StReg(bits, e.Reg, () => Ld(t));
                    break;
                case DK_MOVEG:
                    LdReg(bits, e.Reg); Stl(t); WriteET();
                    break;
                case DK_MOVEI:
                    U4(e.Imm); Stl(t); WriteET();
                    break;
                case DK_MOVRI:
                    if (e.Live) { uint ia = pas[k] + e.Len - 4; StReg(32, op, () => { Ld(mref); U4(ia); Op(OpCodes.Conv_U); Op(OpCodes.Add); il.Emit(OpCodes.Unaligned, (byte)1); Op(OpCodes.Ldind_U4); }); break; }
                    StReg(bits, op, () => U4(e.Imm));
                    break;
                case DK_LEA:
                    StReg(e.O16 ? 16 : 32, e.Reg, () => Ld(ea));
                    break;
                case DK_ALUAI:
                    LdReg(bits, 0); Stl(A); U4(e.Imm); Stl(B); AluAB(op, bits);
                    if (op != 7) StReg(bits, 0, () => Ld(D));
                    else Fold(true);
                    break;
                case DK_ALUEG:
                case DK_GRP1:
                {
                    int aop = e.Kind == DK_GRP1 ? e.Reg : op;
                    ReadE(); Stl(A);
                    if (e.Kind == DK_GRP1) U4(e.Imm); else LdReg(bits, e.Reg);
                    Stl(B);
                    AluAB(aop, bits);
                    if (aop != 7) { Ld(D); Stl(t); WriteET(); }
                    else Fold(!mem);
                    break;
                }
                case DK_ALUGE:
                    LdReg(bits, e.Reg); Stl(A); ReadE(); Stl(B); AluAB(op, bits);
                    if (op != 7) StReg(bits, e.Reg, () => Ld(D));
                    else Fold(!mem);
                    break;
                case DK_TESTEG:
                    ReadE(); Stl(A); LdReg(bits, e.Reg); Stl(B); AluAB(4, bits);
                    Fold(!mem);
                    break;
                case DK_G3TEST:
                    ReadE(); Stl(A); U4(e.Imm); Stl(B); AluAB(4, bits);
                    Fold(!mem);
                    break;
                case DK_INC:
                case DK_DEC:
                    if (NoIncInline) { Ld0(); I4(e.O16 ? 16 : 32); I4(op); Bool(e.Kind == DK_DEC); Call("JIncDec"); break; }
                    {   // IncDecReg inline: CF is kept (made concrete in Flags when it is lazy: SUB / ADD / logic inline, the rest through GetCF)
                        int ib = e.O16 ? 16 : 32;
                        var skipCf = il.DefineLabel(); var notSub = il.DefineLabel(); var notAdd = il.DefineLabel(); var slowCf = il.DefineLabel(); var haveCf = il.DefineLabel();
                        LdF(FMask); U4(CF); Op(OpCodes.And); il.Emit(OpCodes.Brfalse, skipCf);
                        LdF(FOp); I4((int)Cc.SUB); il.Emit(OpCodes.Bne_Un, notSub);
                        LdF(FSrc1); LdF(FSrc2); Op(OpCodes.Clt_Un); il.Emit(OpCodes.Br, haveCf);
                        il.MarkLabel(notSub);
                        LdF(FOp); I4((int)Cc.ADD); il.Emit(OpCodes.Bne_Un, notAdd);
                        LdF(FDst); LdF(FSrc2); Op(OpCodes.Clt_Un); il.Emit(OpCodes.Br, haveCf);
                        il.MarkLabel(notAdd);
                        LdF(FOp); I4((int)Cc.AND); Op(OpCodes.Sub); I4(2); il.Emit(OpCodes.Bgt_Un, slowCf);
                        Op(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Br, haveCf);
                        il.MarkLabel(slowCf);
                        Ld0(); Call("JGetCF");
                        il.MarkLabel(haveCf);
                        Stl(Y);
                        StF(FFlags, () => { LdF(FFlags); U4(~CF); Op(OpCodes.And); Ld(Y); Op(OpCodes.Or); });
                        il.MarkLabel(skipCf);
                        LdReg(ib, op); SextTop(ib); Op(OpCodes.Ldc_I4_1); Op(e.Kind == DK_DEC ? OpCodes.Sub : OpCodes.Add); SextTop(ib); Stl(D);
                        StF(FDst, () => Ld(D));
                        StF(FOp, () => I4((int)(e.Kind == DK_DEC ? (ib == 16 ? Cc.DEC16 : Cc.DEC32) : (ib == 16 ? Cc.INC16 : Cc.INC32))));
                        StF(FMask, () => U4(PF | AF | ZF | SF | OF));
                        StReg(ib, op, () => Ld(D));
                    }
                    break;
                case DK_JCC:
                case DK_JMP:
                {
                    var notTaken = il.DefineLabel();
                    if (e.Kind == DK_JCC && op >= 0xe0)
                    {   // LOOPcc / JCXZ (as Step1): the count is CX or ECX by the address size
                        int cb = e.A16 ? 16 : 32;
                        if (op != 0xe3) StReg(cb, 1, () => { LdReg(cb, 1); Op(OpCodes.Ldc_I4_1); Op(OpCodes.Sub); });
                        LdReg(cb, 1);
                        il.Emit(op == 0xe3 ? OpCodes.Brtrue : OpCodes.Brfalse, notTaken);
                        if (op is 0xe0 or 0xe1) { CondTop(op == 0xe1 ? 0x74u : 0x75u); il.Emit(OpCodes.Brfalse, notTaken); }
                    }
                    else if (e.Kind == DK_JCC) { CondTop((uint)op); il.Emit(OpCodes.Brfalse, notTaken); }
                    IpAt(pe); U4(e.Imm); il.Emit(OpCodes.Add);
                    if (c16) { I4(0xffff); il.Emit(OpCodes.And); }
                    il.Emit(OpCodes.Stloc, t);
                    { var (c0, s0) = (pc, ps); StDec(); Continue(k, () => il.Emit(OpCodes.Ldloc, t), pe + e.Imm, false); pc = c0; ps = s0; }
                    il.MarkLabel(notTaken);
                    ends = e.Kind == DK_JMP;
                    if (ends) { il.Emit(OpCodes.Ldc_I4, FAIL); il.Emit(OpCodes.Ret); }   // not reached
                    break;
                }
                case DK_GRP2 when (e.Reg is 4 or 5 or 6 or 7):
                {
                    // SHL / SHR / SAR inline (as Grp2): x = E, y = count & 31, nothing at all when y is 0
                    int ck = op & 3;
                    var skip = il.DefineLabel();
                    if (ck == 0) U4(e.Imm); else if (ck == 1) Op(OpCodes.Ldc_I4_1); else LdReg(8, 1);
                    I4(0x1f); Op(OpCodes.And); Stl(Y);
                    ReadE(); Stl(A);   // read even when nothing shifts (as Grp2)
                    Ld(Y); il.Emit(OpCodes.Brfalse, skip);
                    if (e.Reg is 4 or 6)
                    {   // _ccDst = Sext(x << y); _ccDst2 = (x >> (bits - y)) & 1
                        Ld(A); Ld(Y); I4(31); Op(OpCodes.And); Op(OpCodes.Shl); SextTop(bits); Stl(D);
                        StF(FDst, () => Ld(D));
                        StF(FDst2, () => { Ld(A); I4(bits); Ld(Y); Op(OpCodes.Sub); I4(31); Op(OpCodes.And); Op(OpCodes.Shr_Un); Op(OpCodes.Ldc_I4_1); Op(OpCodes.And); });
                        StF(FOp, () => I4((int)Cc.SHL));
                    }
                    else if (e.Reg == 5)
                    {   // _ccSrc1 = Sext(x); _ccDst = Sext(x >> y); _ccDst2 = (x >> (y - 1)) & 1
                        StF(FSrc1, () => { Ld(A); Ld(Y); I4(31); Op(OpCodes.And); Op(OpCodes.Shr_Un); Op(OpCodes.Ldc_I4_1); Op(OpCodes.Shl); SextTop(bits); });
                        Ld(A); Ld(Y); I4(31); Op(OpCodes.And); Op(OpCodes.Shr_Un); SextTop(bits); Stl(D);
                        StF(FDst, () => Ld(D));
                        StF(FDst2, () => { Ld(A); Ld(Y); Op(OpCodes.Ldc_I4_1); Op(OpCodes.Sub); I4(31); Op(OpCodes.And); Op(OpCodes.Shr_Un); Op(OpCodes.Ldc_I4_1); Op(OpCodes.And); });
                        StF(FOp, () => I4((int)Cc.SHR));
                    }
                    else
                    {   // sx = (int)Sext(x); _ccDst = sx >> y; _ccDst2 = (sx >> (y - 1)) & 1
                        Ld(A); SextTop(bits); Stl(A);
                        Ld(A); Ld(Y); I4(31); Op(OpCodes.And); Op(OpCodes.Shr); Stl(D);
                        StF(FDst, () => Ld(D));
                        StF(FDst2, () => { Ld(A); Ld(Y); Op(OpCodes.Ldc_I4_1); Op(OpCodes.Sub); I4(31); Op(OpCodes.And); Op(OpCodes.Shr); Op(OpCodes.Ldc_I4_1); Op(OpCodes.And); });
                        StF(FOp, () => I4((int)Cc.SAR));
                    }
                    StF(FMask, () => U4(LOGIC));
                    Ld(D); Stl(t); WriteET();
                    il.MarkLabel(skip);
                    break;
                }
                case DK_GRP2:
                {
                    Prep();
                    int ck = op & 3;
                    Ld0(); I4(bits); I4(e.Reg);
                    if (ck == 0) U4(e.Imm); else if (ck == 1) il.Emit(OpCodes.Ldc_I4_1); else LdReg(8, 1);
                    Call("JGrp2"); FailIfFalse();
                    if (mem) SmcCheck(la, bits / 8);
                    break;
                }
                case DK_GRP3:
                    Prep();
                    Ld0(); I4(bits); Call("JGrp3"); FailIfFalse();
                    if (mem) SmcCheck(la, bits / 8);
                    break;
                case DK_PUSH:
                {
                    // Push(bits, R[reg]): la = SS base + ((sp - n) & spm); SetSp(sp - n); store
                    int pb = e.O16 ? 16 : 32;
                    if (op == 8) U4(e.Imm); else LdReg(pb, op); Stl(t);   // (Op 8: PUSH imm)
                    LdReg(32, 4); I4(pb / 8); Op(OpCodes.Sub); Stl(A);
                    Ld(A); LdSpm(); Op(OpCodes.And); Stl(ea); LimitCheck(k, SEG_SS, ea, pb / 8);
                    LdBase(SEG_SS); Ld(A); LdSpm(); Op(OpCodes.And); Op(OpCodes.Add); Stl(la);
                    Xlat(k, pb / 8, true);
                    StReg(32, 4, () => { Ld(A); LdSpm(); Op(OpCodes.And); LdReg(32, 4); LdSpm(); Op(OpCodes.Not); Op(OpCodes.And); Op(OpCodes.Or); });
                    StMem(pb, la, t);
                    SmcCheck(pg ? pax : la, pb / 8);
                    break;
                }
                case DK_LEAVE when !NoIncInline:
                case DK_POP:
                {
                    // PopReg(bits, reg): v = [SS base + (sp & spm)]; SetSp(sp + n); SReg(reg, v)
                    // (LEAVE, as Leave: the same with sp = EBP, into EBP)
                    int pb = e.O16 ? 16 : 32;
                    bool leave = e.Kind == DK_LEAVE;
                    if (leave) op = 5;
                    LdReg(32, leave ? 5 : 4); Stl(A);
                    Ld(A); LdSpm(); Op(OpCodes.And); Stl(ea); LimitCheck(k, SEG_SS, ea, pb / 8);
                    LdBase(SEG_SS); Ld(A); LdSpm(); Op(OpCodes.And); Op(OpCodes.Add); Stl(la);
                    Xlat(k, pb / 8, false);
                    LdMem(pb, la);
                    StReg(32, 4, () => { Ld(A); I4(pb / 8); Op(OpCodes.Add); LdSpm(); Op(OpCodes.And); LdReg(32, 4); LdSpm(); Op(OpCodes.Not); Op(OpCodes.And); Op(OpCodes.Or); });
                    StReg(pb, op, () => Ld(V));
                    break;
                }
                case DK_CALL:
                {
                    // CallNear inline: Push(operand size, the next Ip) as DK_PUSH, then NextIp = next Ip + rel (16-bit code: wraps); leave
                    // (no SMC check: the region is left right after it anyway)
                    int pb = e.O16 ? 16 : 32;
                    IpAt(pe); Stl(t);
                    LdReg(32, 4); I4(pb / 8); Op(OpCodes.Sub); Stl(A);
                    Ld(A); LdSpm(); Op(OpCodes.And); Stl(ea); LimitCheck(k, SEG_SS, ea, pb / 8);
                    LdBase(SEG_SS); Ld(A); LdSpm(); Op(OpCodes.And); Op(OpCodes.Add); Stl(la);
                    Xlat(k, pb / 8, true);
                    StReg(32, 4, () => { Ld(A); LdSpm(); Op(OpCodes.And); LdReg(32, 4); LdSpm(); Op(OpCodes.Not); Op(OpCodes.And); Op(OpCodes.Or); });
                    StMem(pb, la, t);
                    StDec();
                    LeaveAfter(k, () => { Ld(t); U4(e.Imm); Op(OpCodes.Add); if (c16) { I4(0xffff); Op(OpCodes.And); } });
                    ends = true;
                    break;
                }
                case DK_RET:
                {
                    // Ret(0) inline: NextIp = [SS base + (sp & spm)], SetSp(sp + n); a 32-bit return past the CS limit through JRet (its #GP)
                    int pb = e.O16 ? 16 : 32;
                    LdReg(32, 4); Stl(A);
                    Ld(A); LdSpm(); Op(OpCodes.And); Stl(ea); LimitCheck(k, SEG_SS, ea, pb / 8);
                    LdBase(SEG_SS); Ld(A); LdSpm(); Op(OpCodes.And); Op(OpCodes.Add); Stl(la);
                    Xlat(k, pb / 8, false);
                    LdMem(pb, la);
                    if (pb == 32)
                    {
                        var inLim = il.DefineLabel();
                        Ld(V); Ld0(); I4(SEG_CS); Call("JSegLim"); il.Emit(OpCodes.Ble_Un, inLim);
                        Prep(); Ld0(); I4((int)e.Imm); Call("JRet"); FailIfFalse(); LeaveAfterDec(k, () => { Ld0(); Call("JNextIp"); });
                        il.MarkLabel(inLim);
                    }
                    StReg(32, 4, () => { Ld(A); I4(pb / 8 + (int)e.Imm); Op(OpCodes.Add); LdSpm(); Op(OpCodes.And); LdReg(32, 4); LdSpm(); Op(OpCodes.Not); Op(OpCodes.And); Op(OpCodes.Or); });
                    StDec();
                    LeaveAfter(k, () => Ld(V));
                    ends = true;
                    break;
                }
                case DK_MOVX:
                    // MOVZX / MOVSX: the source (Bits wide) zero / sign extended into the operand-size register
                    ReadE(); if (op == 1) SextTop(bits); Stl(t);
                    StReg(e.O16 ? 16 : 32, e.Reg, () => Ld(t));
                    break;
                case DK_CBW:
                    if (op == 0)
                    {
                        if (e.O16) StReg(16, 0, () => { LdReg(8, 0); SextTop(8); });
                        else StReg(32, 0, () => { LdReg(16, 0); SextTop(16); });
                    }
                    else
                    {   // DX / EDX = 0 - (sign bit)
                        if (e.O16) StReg(16, 2, () => { Op(OpCodes.Ldc_I4_0); LdReg(16, 0); SextTop(16); I4(31); Op(OpCodes.Shr_Un); Op(OpCodes.Sub); });
                        else StReg(32, 2, () => { Op(OpCodes.Ldc_I4_0); LdReg(32, 0); I4(31); Op(OpCodes.Shr_Un); Op(OpCodes.Sub); });
                    }
                    break;
                case DK_SETCC:
                    CondTop((uint)op); Stl(t); WriteET();
                    break;
                case DK_CMOV:
                {   // CMOVcc r, E (P6): the source is read (and may fault) whatever the condition, the register written only when it holds
                    ReadE(); Stl(t);
                    var skip = il.DefineLabel();
                    CondTop((uint)op); il.Emit(OpCodes.Brfalse, skip);
                    StReg(bits, e.Reg, () => Ld(t));
                    il.MarkLabel(skip);
                    break;
                }
                case DK_IMUL:
                    // Imul2(bits, reg, a, b): 0F AF a = reg, b = E; 69 / 6B a = E, b = imm
                    if (op == 0) { LdReg(bits, e.Reg); Stl(A); ReadE(); Stl(B); }
                    else { ReadE(); Stl(A); U4(e.Imm); Stl(B); }
                    if (NoIncInline || bits == 8) { Ld0(); I4(bits); I4(e.Reg); Ld(A); Ld(B); Call("JImul2"); break; }
                    // Imul2 inline (X86_NOINCINLINE: the call): the same lazy-flag fields
                    StF(FSrc1, () => { Ld(A); SextTop(bits); }); StF(FSrc2, () => { Ld(B); SextTop(bits); });
                    if (bits == 16) { LdF(FSrc1); LdF(FSrc2); Op(OpCodes.Mul); Stl(D); StF(FOp, () => I4((int)Cc.IMUL16)); }
                    else
                    {
                        var res = il.DeclareLocal(typeof(long));
                        Ld(A); Op(OpCodes.Conv_I8); Ld(B); Op(OpCodes.Conv_I8); Op(OpCodes.Mul); Stl(res);
                        Ld(res); Op(OpCodes.Conv_U4); Stl(D);
                        StF(FDst2, () => { Ld(res); I4(32); Op(OpCodes.Shr); Op(OpCodes.Conv_U4); });
                        StF(FOp, () => I4((int)Cc.IMUL32));
                    }
                    StF(FDst, () => Ld(D)); StF(FMask, () => U4(ALL));
                    StReg(bits, e.Reg, () => Ld(D));
                    break;
                case DK_GRP5 when Grp5Inline(e):
                {
                    // CALL / JMP / PUSH Ev inline (X86_NOGRP5INL: through JGrp5): v = E (read first, as the interpreter), then CALL / PUSH as
                    // DK_CALL / DK_PUSH, JMP: NextIp = v; leave
                    int pb = e.O16 ? 16 : 32;
                    ReadE(); Stl(B);
                    if (e.Reg == 4) { StDec(); LeaveAfter(k, () => Ld(B)); ends = true; break; }
                    if (e.Reg == 2) IpAt(pe); else Ld(B);
                    Stl(t);
                    LdReg(32, 4); I4(pb / 8); Op(OpCodes.Sub); Stl(A);
                    Ld(A); LdSpm(); Op(OpCodes.And); Stl(ea); LimitCheck(k, SEG_SS, ea, pb / 8);
                    LdBase(SEG_SS); Ld(A); LdSpm(); Op(OpCodes.And); Op(OpCodes.Add); Stl(la);
                    Xlat(k, pb / 8, true);
                    StReg(32, 4, () => { Ld(A); LdSpm(); Op(OpCodes.And); LdReg(32, 4); LdSpm(); Op(OpCodes.Not); Op(OpCodes.And); Op(OpCodes.Or); });
                    StMem(pb, la, t);
                    if (e.Reg == 2) { StDec(); LeaveAfter(k, () => Ld(B)); ends = true; }
                    else SmcCheck(pg ? pax : la, pb / 8);
                    break;
                }
                case DK_GRP5:
                    Prep();
                    Ld0(); I4(bits); Call("JGrp5"); FailIfFalse();
                    if (e.Reg is 2 or 4)
                    {   // CALL / JMP Ev: leave at the new Ip
                        StDec();
                        LeaveAfter(k, () => { Ld0(); Call("JNextIp"); });
                        ends = true;
                    }
                    else if (e.Reg == 6)
                    {   // PUSH: the store went to SS base + the new ESP
                        Ld0(); I4(SEG_SS); Call("JSegBase"); LdReg(32, 4); LdSpm(); Op(OpCodes.And); Op(OpCodes.Add); Stl(la);
                        SmcCheck(la, bits / 8);
                    }
                    else if (mem) SmcCheck(la, bits / 8);
                    break;
                case DK_STOS:
                {
                    // Stos(bits) without REP: [ES:eDI] = AL / eAX, eDI += (DF ? -n : n) (address size); the video card's memory through JWrSlow / JWrLin
                    int n1 = bits / 8, asz = e.A16 ? 16 : 32;
                    LdReg(asz, 7); Stl(ea);
                    LimitCheck(k, SEG_ES, ea, n1);
                    LdBase(SEG_ES); Ld(ea); Op(OpCodes.Add); Stl(la);
                    Xlat(k, n1, true);
                    LdReg(bits, 0); Stl(t);
                    StMem(bits, la, t);
                    var up = il.DefineLabel(); var adv = il.DefineLabel();
                    LdF(FFlags); U4(DF); Op(OpCodes.And); il.Emit(OpCodes.Brfalse, up);
                    I4(-n1); il.Emit(OpCodes.Br, adv);
                    il.MarkLabel(up); I4(n1);
                    il.MarkLabel(adv); Stl(Y);
                    StReg(asz, 7, () => { Ld(ea); Ld(Y); Op(OpCodes.Add); });
                    SmcCheck(pg ? pax : la, n1);
                    break;
                }
                case DK_MOVSR:
                    // MOV Ew, Sw: the selector (a register: zero-extended to the operand size; memory: 16 bits)
                    Ld0(); I4(e.Reg); Call("JSegSel"); I4(0xffff); Op(OpCodes.And); Stl(t); WriteET();
                    break;
                case DK_SRMOV:
                {
                    // MOV Sw, Ew (DS / ES / FS / GS): the interpreter's load (a fault: leave with it); a segment the region uses is taken again
                    // (no longer usable: leave right after it)
                    ReadE(); Stl(A);
                    Prep(); Ld0(); I4(e.Reg); Ld(A); Call("JMovSeg"); FailIfFalse();
                    if (IsSpec(e.Reg))
                    {   // (specialized: the new segment must have the very same values, else leave right after it)
                        var same = il.DefineLabel();
                        Ld0(); I4(e.Reg); U4(spec!.Base[e.Reg]); U4(spec.Span[e.Reg]); U4(spec.Lo[e.Reg]); Call("JSSameLive"); il.Emit(OpCodes.Brtrue, same);
                        LeaveAfterDec(k, () => IpAt(pe));
                        il.MarkLabel(same);
                    }
                    else if (sb[e.Reg] != null)
                    {
                        var usable = il.DefineLabel();
                        Ld0(); I4(e.Reg); Call("JSegOk"); il.Emit(OpCodes.Brtrue, usable);
                        LeaveAfterDec(k, () => IpAt(pe));
                        il.MarkLabel(usable);
                        Ld0(); I4(e.Reg); Call("JSegBase"); Stl(sb[e.Reg]!); Ld0(); I4(e.Reg); Call("JSegSpan"); Stl(sl[e.Reg]!); Ld0(); I4(e.Reg); Call("JSegLo"); Stl(slo[e.Reg]!);
                        if (segOkL[e.Reg] != null) { I4(1); Stl(segOkL[e.Reg]!); }   // (usable now, whatever it was at entry)
                    }
                    break;
                }
                case DK_STR:
                {
                    // MOVS / CMPS / LODS / SCAS without REP, as Movs / Cmps / Lods / Scas with _rep 0: the source [seg:eSI] (MOVS / CMPS / LODS),
                    // the destination [ES:eDI] (MOVS / CMPS / SCAS); eSI / eDI += (DF ? -n : n); CMPS / SCAS: the flags of a SUB
                    int n1 = bits / 8, asz = e.A16 ? 16 : 32;
                    var up = il.DefineLabel(); var adv = il.DefineLabel();
                    void At(int seg, int reg, bool write)
                    {
                        LdReg(asz, reg); Stl(ea);
                        LimitCheck(k, seg, ea, n1);
                        LdBase(seg); Ld(ea); Op(OpCodes.Add); Stl(la);
                        Xlat(k, n1, write);
                    }
                    if (op != 3) { At(e.Seg, 6, false); LdMem(bits, la); Ld(V); Stl(A); }   // A = the source
                    if (op == 0) { At(SEG_ES, 7, true); Ld(A); Stl(t); StMem(bits, la, t); }
                    else if (op != 2) { At(SEG_ES, 7, false); LdMem(bits, la); Ld(V); Stl(B); }
                    LdF(FFlags); U4(DF); Op(OpCodes.And); il.Emit(OpCodes.Brfalse, up);
                    I4(-n1); il.Emit(OpCodes.Br, adv);
                    il.MarkLabel(up); I4(n1);
                    il.MarkLabel(adv); Stl(Y);
                    if (op != 3) StReg(asz, 6, () => { LdReg(asz, 6); Ld(Y); Op(OpCodes.Add); });
                    if (op != 2) StReg(asz, 7, () => { LdReg(asz, 7); Ld(Y); Op(OpCodes.Add); });
                    if (op == 2) StReg(bits, 0, () => Ld(A));
                    else if (op == 1) AluAB(7, bits);
                    else if (op == 3) { LdReg(bits, 0); Stl(A); AluAB(7, bits); }
                    if (op == 0) SmcCheck(pg ? pax : la, n1);
                    break;
                }
                case DK_REPSTR:
                {
                    Prep();
                    Ld0(); I4(op); I4(bits); I4(e.Reg); LdCyc(); Call("JRepStr"); Stl(Y);
                    Ld0(); Call("JCycle"); Stl(cyc);   // (with the iterations past the first)
                    pc = 0;
                    Ld(Y); FailIfFalse();
                    if (op is 0xa4 or 0xa5 or 0xaa or 0xab)
                    {   // it may have written to this code: leave right after it
                        var same = il.DefineLabel();
                        Ld0(); Call("JCodeSame"); il.Emit(OpCodes.Brtrue, same);
                        LeaveAfterDec(k, () => IpAt(pe));
                        il.MarkLabel(same);
                    }
                    break;
                }
                case DK_MMX:
                {   // (a fault before the ModR/M: no memory operand cost, as the interpreter - Age of Empires' JIT parity, 2026-11-20)
                    if (mem) { il.Emit(OpCodes.Ldloc, cyc); Ld0(); Call("JMmxNmRefund"); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, cyc); }
                    Prep();
                    Ld0(); I4(op); I4(op is >= 0x71 and <= 0x73 ? (int)e.Imm : -1); Call("JMmx"); FailIfFalse();
                    if (mem && op is 0x7e or 0x7f)
                    {   // a store: it may have written to this code, leave right after it
                        var same = il.DefineLabel();
                        Ld0(); Call("JCodeSame"); il.Emit(OpCodes.Brtrue, same);
                        LeaveAfterDec(k, () => IpAt(pe));
                        il.MarkLabel(same);
                    }
                    break;
                }
                case DK_FPU:
                {
                    // #NM (CR0.EM / TS: Win98's lazy FPU switch) comes before the interpreter decodes the ModR/M, so it charges no memory
                    // operand then: take back the cost the instruction was charged with (savestates: JIT == interpreter)
                    if (mem) { il.Emit(OpCodes.Ldloc, cyc); Ld0(); Call("JFpuNmRefund"); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, cyc); }
                    Prep();
                    Ld0(); I4(op); Call("JFpu"); FailIfFalse();
                    bool load = op is 0 or 2 or 4 or 6 || (op == 1 && e.Reg is 0 or 4 or 5) || (op == 3 && e.Reg is 0 or 5) || (op == 5 && e.Reg is 0 or 4)
                        || (op == 7 && e.Reg is 0 or 4 or 5);
                    if (mem && !load)
                    {   // a store: it may have written to this code, leave right after it
                        var same = il.DefineLabel();
                        Ld0(); Call("JCodeSame"); il.Emit(OpCodes.Brtrue, same);
                        LeaveAfterDec(k, () => IpAt(pe));
                        il.MarkLabel(same);
                    }
                    break;
                }
                case DK_LEAVE:
                    Prep();
                    Ld0(); Call("JLeave"); FailIfFalse();
                    break;
                default:
                    throw new InvalidOperationException("kind " + e.Kind);
            }
            if (!ends)
            {   // fall through to the next instruction
                StDec();
                Continue(k, () => IpAt(pe), pe, true);
            }
        }
        if (xOff != null)
        {
            il.MarkLabel(sharedLeave);
            Ld0(); Ld(ip0); Ld(xOff); Op(OpCodes.Add); Ld(ip0); Ld(xNext!); Op(OpCodes.Add); Ld(cyc); Ld(st); Call("JExitLink"); il.Emit(OpCodes.Ret);
        }
        System.Threading.Interlocked.Add(ref JitIlBytes, il.ILOffset);
        if (into != null) { tb!.CreateType(); return null!; }
        if (tb != null) return (JitFn)tb.CreateType()!.GetMethod("x86blk")!.CreateDelegate(typeof(JitFn));
        return (JitFn)dm!.CreateDelegate(typeof(JitFn));
    }
    /// <summary>X86_JITTIER: compile blocks as emitted-type methods (tiered: quick first, optimized when hot) instead of DynamicMethods.</summary>
    private static readonly bool JitTier = Environment.GetEnvironmentVariable("X86_JITTIER") == "1";
    private static int _jitTierN;
    private static System.Reflection.Emit.ModuleBuilder? _jitTierMod;
    private static int _jitTierModN;
    private static readonly object _jitTierLock = new();
    /// <summary>A collectible dynamic assembly that may use I386's private members; a new one every 256 blocks (so old code can be collected).</summary>
    private static System.Reflection.Emit.ModuleBuilder JitTierModule()
    {
        lock (_jitTierLock)
        {
            if (_jitTierMod == null || ++_jitTierModN % 256 == 0)
            {
                var ab = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("x86blocks" + _jitTierModN), System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect);
                ab.SetCustomAttribute(new System.Reflection.Emit.CustomAttributeBuilder(typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructor(new[] { typeof(string) })!,
                    new object[] { typeof(I386).Assembly.GetName().Name! }));
                _jitTierMod = ab.DefineDynamicModule("x86blocks");
            }
            return _jitTierMod;
        }
    }
    public static long JitIlBytes, JitBuildTicks;

    /// <summary>The segments a compiled instruction reads or writes through (bit per segment).</summary>
    private static int InsSegs(DEntry e)
    {
        int m = 0;
        if (e.Kind is DK_PUSH or DK_POP or DK_CALL or DK_RET or DK_LEAVE) m |= 1 << SEG_SS;
        if (e.Kind == DK_STOS || (e.Kind == DK_STR && e.Op != 2)) m |= 1 << SEG_ES;
        if (e.Kind == DK_STR && e.Op != 3) m |= 1 << e.Seg;
        if (DcHasModrm(e.Kind) && e.Mod != 3 && e.Kind != DK_LEA) m |= 1 << e.Seg;
        return m;
    }
    /// <summary>Does this compiled instruction only read its memory operand (so a readable code segment will do)? Conservative: false when unsure.</summary>
    private static bool InsReadsOnly(DEntry e) => e.Kind is DK_MOVGE or DK_ALUGE or DK_TESTEG or DK_G3TEST or DK_MOVX or DK_IMUL or DK_SRMOV or DK_CMOV
        || (e.Kind == DK_ALUEG && e.Op == 7) || (e.Kind == DK_GRP1 && e.Reg == 7) || (e.Kind == DK_GRP5 && e.Reg is 2 or 4 or 6);   // (MOV Sreg / CALL / JMP / PUSH [mem]: reads)
    private static readonly bool SegLazy = Environment.GetEnvironmentVariable("X86_SEGLAZY") != "0";

    private static bool DcHasModrm(byte kind) => kind is DK_MOVEG or DK_MOVGE or DK_ALUEG or DK_ALUGE or DK_GRP1 or DK_GRP2 or DK_TESTEG
        or DK_G3TEST or DK_GRP3 or DK_MOVEI or DK_LEA or DK_MOVX or DK_SETCC or DK_IMUL or DK_GRP5 or DK_MOVSR or DK_SRMOV or DK_FPU or DK_MMX or DK_XCHG or DK_CMOV;
    /// <summary>X86_XCHGMEM: compile XCHG r, mem inline (opt-in: Garage2 A/B 2026-11-20 StarCraft -0.6%, AoE +2.1%, within noise).</summary>
    private static readonly bool NoCmovJit = Environment.GetEnvironmentVariable("X86_NOCMOVJIT") != null;
    private static readonly bool NoXchgMem = Environment.GetEnvironmentVariable("X86_XCHGMEM") == null;
}

