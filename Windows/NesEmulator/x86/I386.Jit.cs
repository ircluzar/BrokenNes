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
// memory (any write to code, by anyone, is seen: the region is compiled again when hot again), and it runs only with paging off and
// when every segment it uses has a usable selector (so no address translation inside it can fault). Off when the runtime cannot
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
        public bool C16;
        public long Runs, Instrs;
    }

    /// <summary>One instruction address: how often it was reached, and the compiled block (and instruction in it) that starts there.</summary>
    private sealed class JitEntry
    {
        public uint Start;
        public bool C16;
        public int Visits;
        public bool Failed;
        public bool Pending;   // being compiled in the background (interpreted meanwhile)
        public JitCode? Code;
        public int K;
    }

    private static readonly bool JitOn = RuntimeFeature.IsDynamicCodeCompiled && Environment.GetEnvironmentVariable("X86_NOJIT") == null;
    private const int JitBits = 16, JitMask = (1 << JitBits) - 1, JIT_DECLINE = 3, JIT_END = 4, JitKeep = 16384;
    private static readonly int JitMaxInstr = int.TryParse(Environment.GetEnvironmentVariable("X86_JITMAX"), out int jm) ? jm : 128;
    private static readonly int JitHot = int.TryParse(Environment.GetEnvironmentVariable("X86_JITHOT"), out int jh) ? jh : 32;
    private JitCode? _jitCur;   // the block running now
    private readonly JitEntry?[] _je = new JitEntry?[1 << JitBits];
    private readonly List<JitCode> _jitAll = new();
    private readonly Dictionary<ulong, JitCode> _jitCodes = new();   // every block compiled, by start address and code size: a block evicted from the table is not compiled twice
    public int JitReused { get; private set; }

    // statistics (Workshop --x86boot --jitstat)
    public int JitCompiled { get; private set; }
    public long JitRuns, JitInvalid;
    public IEnumerable<string> JitTop(int n) => _jitAll.OrderByDescending(b => b.Instrs).Take(n)
        .Select(b => $"{b.Start:X8} n{b.Count} runs {b.Runs} instrs {b.Instrs} | {Convert.ToHexString(b.Bytes)}");
    private readonly long[] _interpOps = new long[65536];
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void JitCountInterp(uint pa) { if (pa + 2 < MemSize) _interpOps[Mem[pa] << 8 | Mem[pa + 1]]++; }
    public IEnumerable<string> InterpTop(int n) => Enumerable.Range(0, 65536).Where(i => _interpOps[i] > 0).OrderByDescending(i => _interpOps[i]).Take(n)
        .Select(i => $"{i >> 8:X2} {i & 255:X2}: {_interpOps[i]}");
    public long JitInstrsTotal => _jitAll.Sum(b => b.Instrs);
    public static bool JitStats = Environment.GetEnvironmentVariable("X86_JITSTAT") != null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int JitIndex(uint paddr) => (int)((paddr ^ (paddr >> JitBits)) & JitMask);

    /// <summary>Exec1's hook, on the fast fetch path: runs a compiled block from <paramref name="paddr"/> if there is a valid one
    /// (OK / FAIL as Exec1's own), else counts the visit and returns JIT_DECLINE (interpret this instruction).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int JitTry(uint paddr)
    {
        var en = _je[JitIndex(paddr)];
        if (en != null && en.Start == paddr && en.C16 == _c16)
        {
            var code = en.Code;
            if (code != null)
            {
                if (JitSame(code))
                {
                    _jitCur = code;
                    if (!JitStats) return code.Fn(this, en.K);
                    return JitRunCounted(code, en.K);
                }
                return JitInvalidated(en);
            }
            if (en.Failed || en.Pending) return JIT_DECLINE;
            if (++en.Visits >= JitHot) JitCompile(en);
            return JIT_DECLINE;
        }
        return JitVisit(paddr);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int JitRunCounted(JitCode code, int k)
    {
        long c0 = Cycle;
        int rc = code.Fn(this, k);
        JitRuns++; code.Runs++; code.Instrs += Cycle - c0;
        return rc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int JitInvalidated(JitEntry en)
    {   // the block's bytes changed: interpret, and compile again once hot again
        JitInvalid++;
        en.Code = null; en.Visits = 0; en.Failed = false; en.Pending = false;
        return JIT_DECLINE;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int JitVisit(uint paddr)
    {
        ref JitEntry? slot = ref _je[JitIndex(paddr)];
        var en = slot ??= new JitEntry();
        en.Start = paddr; en.C16 = _c16; en.Visits = 1; en.Failed = false; en.Pending = false; en.Code = null; en.K = 0;
        return JIT_DECLINE;
    }

    // ================= helpers the compiled code calls =================

    [MethodImpl(MethodImplOptions.AggressiveInlining)] private bool JRam(uint la) => IsRam(la);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private uint JRdSlow(int bits, uint la)
    {
        var r = new OptAddr { A1 = la };
        return bits == 8 ? Load8Slow(ref r) : bits == 16 ? Load16Slow(ref r) : Load32Slow(ref r);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void JWrSlow(int bits, uint la, uint v)
    {
        var r = new OptAddr { A1 = la };
        if (bits == 8) Store8Slow(ref r, v); else if (bits == 16) Store16Slow(ref r, v); else Store32Slow(ref r, v);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool JSegOk(int seg) => (Cr0 & 1) == 0 || SegLimit[seg] != 0 || (SegSel[seg] & ~3u) != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JSegBase(int seg) => SegBase[seg];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private bool JCond(uint c) => Cond(c);
    [MethodImpl(MethodImplOptions.NoInlining)] private uint JAlu(int op, int bits, uint a, uint b) => Alu(op, bits, a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void JIncDec(int bits, int reg, bool dec) => IncDecReg(bits, reg, dec);
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
    [MethodImpl(MethodImplOptions.NoInlining)] private bool JCall(uint rel) => CallNear(rel, true);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool JRet() => OpRet() != FAIL;

    /// <summary>An out-of-line instruction: exactly one iteration of Exec1's loop (without the compiled code), from Ip <paramref name="ip"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int JInterp(uint ip, long cyc, int st)
    {
        Cycle = cyc; _steps = st; NextIp = ip;
        return Step1(false);
    }
    /// <summary>After an out-of-line instruction: can the compiled code go on (the same code size, paging still off, the fetch still
    /// on this code page, every segment it uses still usable)? Leaves _spm as Exec1's next iteration would.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool JStill(bool c16, uint pa, int segs, bool wrote)
    {
        _c16 = Code16; _spm = SpMask;
        if (_c16 != c16 || (Cr0 & CR0_PG) != 0 || _ifLaddr != (pa & ~4095u)) return false;
        if (wrote && !JitSame(_jitCur!)) return false;   // (it may have written to the code)
        for (int s = 0; s < 6; s++) if ((segs >> s & 1) != 0 && !JSegOk(s)) return false;
        return true;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JSpm() => _spm;

    [MethodImpl(MethodImplOptions.NoInlining)] private int JExitOk(uint ip, uint nextIp, long cyc, int st) { Ip = ip; NextIp = nextIp; Cycle = cyc; _steps = st + 1; return OK; }
    [MethodImpl(MethodImplOptions.NoInlining)] private int JExitFail(uint ip, long cyc, int st) { Ip = ip; Cycle = cyc; _steps = st; return FAIL; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private long JCycle() => Cycle;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private int JSteps() => _steps;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint JIp() => Ip;

    // ================= the compiler =================

    private static FieldInfo Fld(string name) => typeof(I386).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new MissingFieldException(nameof(I386), name);
    private static readonly FieldInfo FSrc1 = Fld("_ccSrc1"), FSrc2 = Fld("_ccSrc2"), FDst = Fld("_ccDst"), FDst2 = Fld("_ccDst2"), FOp = Fld("_ccOp"),
        FMask = Fld("_ccMask"), FFlags = Fld("Flags"), FR = Fld("R"), FMem = Fld("Mem"), FSpm = Fld("_spm");

    private static MethodInfo M(string name) => typeof(I386).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new MissingMethodException(nameof(I386), name);
    private static readonly Lazy<Dictionary<string, MethodInfo>> JitMethods = new(() =>
    {
        var d = new Dictionary<string, MethodInfo>();
        foreach (var n in new[] { "JRam", "JSegOk", "JSegBase", "JCond", "JAlu", "JIncDec", "JNextIp", "JPrep", "JGrp2", "JGrp3", "JCall", "JRet",
                     "JExitOk", "JExitFail", "JCycle", "JSteps", "JIp", "JRdSlow", "JWrSlow", "JInterp", "JStill", "JSpm" })
            d[n] = M(n);
        return d;
    });

    /// <summary>Is the compiled code's code still what is in memory (every byte range it was compiled from)?</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool JitSame(JitCode code)
    {
        byte[] b = code.Bytes;
        int[] ro = code.RunAt, rl = code.RunLen;
        int at = 0;
        for (int i = 0; i < ro.Length; i++)
        {
            if (!Mem.AsSpan(ro[i], rl[i]).SequenceEqual(b.AsSpan(at, rl[i]))) return false;
            at += rl[i];
        }
        return true;
    }

    /// <summary>Compile the region of cached-form instructions reachable from <paramref name="en"/>'s address: the fall-throughs and the
    /// targets of the near jumps (JMP / Jcc / a folded JZ), close by, up to JitMaxInstr instructions. Jumps between them stay inside the
    /// compiled code; everything else leaves it.</summary>
    private void JitCompile(JitEntry en)
    {
        uint start = en.Start;
        ulong key = start | (_c16 ? 1UL << 32 : 0);
        if (_jitCodes.TryGetValue(key, out var old) && JitSame(old))
        {
            JitReused++;
            JitRegister(old);
            return;
        }
        var found = new SortedDictionary<uint, DEntry>();
        var todo = new Queue<uint>();
        todo.Enqueue(start);
        while (todo.Count > 0 && found.Count < JitMaxInstr)
        {
            uint pa = todo.Dequeue();
            if (found.ContainsKey(pa)) continue;
            // one 4 KB page (the compiled code never goes on into another one: see Continue)
            if ((pa & 0xfff) >= 4096 - 16 || pa + 32 >= MemSize || (pa >> 12) != (start >> 12)) continue;
            var e = new DEntry();
            DcDecode(ref e, pa);
            if (e.Kind == DK_SLOW) continue;
            uint end = pa + e.Len;
            if (JitFoldsAt(e, end)) { e.Folds = true; e.FoldNb = (byte)U8(end); e.FoldRel = Sext8(U8(end + 1)); }
            found[pa] = e;
            if (e.Kind is not (DK_JMP or DK_CALL or DK_RET) && !(e.Kind == DK_INTERP && e.Op == 1)) todo.Enqueue(end);
            if (e.Kind is DK_JMP or DK_JCC) todo.Enqueue(end + e.Imm);
            if (e.Folds) todo.Enqueue(end + 2 + e.FoldRel);
        }
        if (!found.Values.Any(x => x.Kind != DK_INTERP)) { en.Failed = true; return; }
        var pas = found.Keys.ToArray();
        var ins = found.Values.ToArray();
        // the byte ranges the code depends on: each instruction, plus the JZ / JNZ a CMP / TEST folds with
        var ranges = new List<(uint a, uint b)>();
        for (int k = 0; k < pas.Length; k++)
        {
            uint a = pas[k], b = a + ins[k].Len;
            if (ins[k].Folds) b += 2;
            if (ranges.Count > 0 && a <= ranges[^1].b) ranges[^1] = (ranges[^1].a, Math.Max(ranges[^1].b, b));
            else ranges.Add((a, b));
        }
        // the bytes it is compiled from, as they are now (the compiled code runs only while memory still holds them)
        var bytes = new List<byte>();
        foreach (var (a, b) in ranges) bytes.AddRange(Mem.AsSpan((int)a, (int)(b - a)).ToArray());
        var code = new JitCode
        {
            Start = start, Count = pas.Length, C16 = _c16, Bytes = bytes.ToArray(),
            RunAt = ranges.Select(r => (int)r.a).ToArray(), RunLen = ranges.Select(r => (int)(r.b - r.a)).ToArray(),
            Addrs = pas,
        };
        uint lo = ranges[0].a, hi = ranges[^1].b;
        bool c16 = _c16, ioMem = _ioMem;
        uint memSize = MemSize;
        if (!JitAsync)
        {
            try { code.Fn = Emit(start, pas, ins, lo, hi, c16, memSize, ioMem); code.Fn(this, -1); }   // (entry -1: only compiles it, the .NET JIT, and declines)
            catch (Exception) { en.Failed = true; return; }
            JitInstall(en, code, key);
            return;
        }
        // in the background: the interpreter runs this code meanwhile (it does exactly what the compiled code will do, so when the
        // compiled code takes over changes nothing but the speed); the result is installed by the CPU's own thread, between batches
        // every address of the region waits for it (else each would get hot and ask for its own region meanwhile)
        foreach (uint pa in pas)
        {
            ref JitEntry? slot = ref _je[JitIndex(pa)];
            var e = slot ??= new JitEntry();
            if (e.Start == pa && e.C16 == c16 && e.Code != null) continue;
            e.Start = pa; e.C16 = c16; e.Code = null; e.Failed = false; e.Pending = true;
        }
        en.Pending = true;
        _jitPending++;
        JitWorker.Post(() =>
        {
            try { code.Fn = Emit(start, pas, ins, lo, hi, c16, memSize, ioMem); code.Fn(this, -1); }
            catch (Exception) { code.Fn = null!; }
            _jitDone.Enqueue((en, code, key));
        });
    }

    private void JitInstall(JitEntry en, JitCode code, ulong key)
    {
        JitCompiled++;
        if (JitStats) _jitAll.Add(code);
        if (_jitCodes.Count >= JitKeep) _jitCodes.Clear();   // a long session that has run a lot of different code: let the old code go
        _jitCodes[key] = code;
        JitRegister(code);
    }

    /// <summary>Install the blocks the background compiler has finished (the CPU's thread, before a batch): each one only if memory
    /// still holds the bytes it was compiled from.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void JitDrain()
    {
        while (_jitDone.TryDequeue(out var d))
        {
            _jitPending--;
            var (en, code, key) = d;
            bool ok = code.Fn != null && JitSame(code);
            foreach (uint pa in code.Addrs)
            {   // the addresses that waited for it: interpreted again, counted again from 0 (a failed start: never again)
                var e = _je[JitIndex(pa)];
                if (e == null || e.Start != pa || e.C16 != code.C16 || !e.Pending) continue;
                e.Pending = false; e.Visits = 0;
                if (code.Fn == null && pa == code.Start) e.Failed = true;
            }
            if (ok) JitInstall(en, code, key);
        }
    }

    /// <summary>Wait for the background compiler, then install what it made (tests: the same blocks as a synchronous compile).</summary>
    public void JitFlush()
    {
        while (_jitPending > 0) { System.Threading.Thread.Yield(); JitDrain(); }
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<(JitEntry, JitCode, ulong)> _jitDone = new();
    private int _jitPending;
    /// <summary>X86_JITSYNC: compile on the CPU's own thread, at once (as before: a hitch, but the compiled code runs from the 32nd visit).</summary>
    private static readonly bool JitAsync = Environment.GetEnvironmentVariable("X86_JITSYNC") == null;

    /// <summary>The background compiler, shared by every CPU in the process.</summary>
    private static class JitWorker
    {
        private static readonly System.Collections.Concurrent.BlockingCollection<Action> Q = new();
        static JitWorker()
        {
            // two threads (the CPU's own thread is the third): a game's start asks for ~1000 blocks at once
            for (int i = 0; i < Math.Clamp(Environment.ProcessorCount - 1, 1, 2); i++)
                new System.Threading.Thread(() => { foreach (var a in Q.GetConsumingEnumerable()) a(); })
                { IsBackground = true, Name = "x86 block compiler" }.Start();
        }
        public static void Post(Action a) => Q.Add(a);
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
            ref JitEntry? slot = ref _je[JitIndex(pa)];
            var e = slot ??= new JitEntry();
            if (pa != code.Start && e.Start == pa && e.C16 == code.C16 && e.Code != null && e.Code.Start == pa) continue;   // keep a region that starts there itself
            e.Start = pa; e.C16 = code.C16; e.Failed = false; e.Pending = false; e.Code = code; e.K = k;
        }
    }

    private JitFn Emit(uint start, uint[] pas, DEntry[] ins, uint lo, uint hi, bool c16, uint memSize, bool ioMem)
    {
        var mm = JitMethods.Value;
        var dm = new DynamicMethod("x86blk", typeof(int), new[] { typeof(I386), typeof(int) }, typeof(I386), true);
        var il = dm.GetILGenerator();
        var ip0 = il.DeclareLocal(typeof(uint));   // the Ip of the region's start address
        var cyc = il.DeclareLocal(typeof(long));
        var st = il.DeclareLocal(typeof(int));
        var la = il.DeclareLocal(typeof(uint));
        var t = il.DeclareLocal(typeof(uint));
        var ea = il.DeclareLocal(typeof(uint));
        var sb = new LocalBuilder?[6];
        int n = ins.Length;
        var index = new Dictionary<uint, int>();
        for (int k = 0; k < n; k++) index[pas[k]] = k;

        var rref = il.DeclareLocal(typeof(uint).MakeByRefType());   // ref R[0]
        var mref = il.DeclareLocal(typeof(byte).MakeByRefType());   // ref Mem[0]
        var spm = il.DeclareLocal(typeof(uint));
        var A = il.DeclareLocal(typeof(uint));
        var B = il.DeclareLocal(typeof(uint));
        var D = il.DeclareLocal(typeof(uint));
        var V = il.DeclareLocal(typeof(uint));
        var Y = il.DeclareLocal(typeof(int));

        void Call(string name) => il.Emit(OpCodes.Call, mm[name]);
        void Ld0() => il.Emit(OpCodes.Ldarg_0);
        void I4(int v) => il.Emit(OpCodes.Ldc_I4, v);
        void U4(uint v) => il.Emit(OpCodes.Ldc_I4, unchecked((int)v));
        void IpAt(uint pa) { il.Emit(OpCodes.Ldloc, ip0); U4(pa - start); il.Emit(OpCodes.Add); }   // the Ip of address pa (wraps like the CPU's)
        void Bool(bool b) => il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        void StDec() { il.Emit(OpCodes.Ldloc, st); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, st); }
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
        // memory at linear (= physical, paging is off) address `la`: plain RAM inline, the rest through Load*Slow / Store*Slow
        void IfRam(LocalBuilder addr, Label notRam)
        {
            Ld(addr); U4(memSize); il.Emit(OpCodes.Bge_Un, notRam);
            if (ioMem) { Ld(addr); U4(0xa0000); Op(OpCodes.Sub); U4(0x20000); il.Emit(OpCodes.Blt_Un, notRam); }
        }
        void LdMem(int bits, LocalBuilder addr)   // leaves the value in V
        {
            var slow = il.DefineLabel(); var done = il.DefineLabel();
            IfRam(addr, slow);
            Ld(mref); Ld(addr); Op(OpCodes.Conv_U); Op(OpCodes.Add);
            if (bits != 8) il.Emit(OpCodes.Unaligned, (byte)1);
            Op(bits == 8 ? OpCodes.Ldind_U1 : bits == 16 ? OpCodes.Ldind_U2 : OpCodes.Ldind_U4);
            Stl(V); il.Emit(OpCodes.Br, done);
            il.MarkLabel(slow);
            Ld0(); I4(bits); Ld(addr); Call("JRdSlow"); Stl(V);
            il.MarkLabel(done);
        }
        void StMem(int bits, LocalBuilder addr, LocalBuilder val)
        {
            var slow = il.DefineLabel(); var done = il.DefineLabel();
            IfRam(addr, slow);
            Ld(mref); Ld(addr); Op(OpCodes.Conv_U); Op(OpCodes.Add); Ld(val);
            if (bits != 8) il.Emit(OpCodes.Unaligned, (byte)1);
            Op(bits == 8 ? OpCodes.Stind_I1 : bits == 16 ? OpCodes.Stind_I2 : OpCodes.Stind_I4);
            il.Emit(OpCodes.Br, done);
            il.MarkLabel(slow);
            Ld0(); I4(bits); Ld(addr); Ld(val); Call("JWrSlow");
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
            Ld0(); U4(c); Call("JCond");
        }

        // prologue: decline when a segment the region uses has a null selector (the translation would fault)
        var usedSegs = new SortedSet<int>();
        foreach (var e in ins)
        {
            if (e.Kind is DK_PUSH or DK_POP or DK_CALL or DK_RET) usedSegs.Add(SEG_SS);
            if (DcHasModrm(e.Kind) && e.Mod != 3 && e.Kind != DK_LEA) usedSegs.Add(e.Seg);
        }
        foreach (int s in usedSegs)
        {
            var ok = il.DefineLabel();
            Ld0(); I4(s); Call("JSegOk"); il.Emit(OpCodes.Brtrue, ok);
            I4(JIT_DECLINE); il.Emit(OpCodes.Ret);
            il.MarkLabel(ok);
            sb[s] = il.DeclareLocal(typeof(uint));
            Ld0(); I4(s); Call("JSegBase"); il.Emit(OpCodes.Stloc, sb[s]!);
        }
        int segMask = 0;
        foreach (int s in usedSegs) segMask |= 1 << s;
        Ld0(); Call("JCycle"); il.Emit(OpCodes.Stloc, cyc);
        Ld0(); Call("JSteps"); il.Emit(OpCodes.Stloc, st);
        LdF(FR); I4(0); il.Emit(OpCodes.Ldelema, typeof(uint)); Stl(rref);
        LdF(FMem); I4(0); il.Emit(OpCodes.Ldelema, typeof(byte)); Stl(mref);
        LdF(FSpm); Stl(spm);

        var body = new Label[n];
        var pre = new Label[n];
        for (int k = 0; k < n; k++) { body[k] = il.DefineLabel(); pre[k] = il.DefineLabel(); }
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Switch, pre);
        il.Emit(OpCodes.Ldc_I4, JIT_DECLINE); il.Emit(OpCodes.Ret);
        for (int k = 0; k < n; k++)
        {   // entered at instruction k (Exec1 checked the step budget)
            il.MarkLabel(pre[k]);
            Ld0(); Call("JIp"); U4(pas[k] - start); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, ip0);
            il.Emit(OpCodes.Br, body[k]);
        }

        // after instruction k (st already counted down): go on at Ip `next` (on the stack-free action), inside the region when it is the
        // region's address `target` and the step budget allows, else leave with Ip = instruction k, NextIp = next
        void Continue(int k, Action next, uint? target, bool fallsInto)
        {
            var leave = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, st); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ble, leave);
            // inside the region only on the same 4 KB page: the interpreter fetches the first instruction on another page the slow way,
            // where a CMP / TEST does not fold with the next JZ / JNZ
            if (target is uint tg && (tg >> 12) == (pas[k] >> 12) && index.TryGetValue(tg, out int j))
            {
                if (!fallsInto) { next(); IpAt(tg); il.Emit(OpCodes.Bne_Un, leave); }   // a jump: the target Ip (with a 16-bit wrap) must be that address
                if (!(fallsInto && j == k + 1)) il.Emit(OpCodes.Br, body[j]);
                else
                {
                    var go = il.DefineLabel();
                    il.Emit(OpCodes.Br, go);
                    il.MarkLabel(leave);
                    Ld0(); IpAt(pas[k]); next(); il.Emit(OpCodes.Ldloc, cyc); il.Emit(OpCodes.Ldloc, st); Call("JExitOk"); il.Emit(OpCodes.Ret);
                    il.MarkLabel(go);
                    return;
                }
            }
            il.MarkLabel(leave);
            Ld0(); IpAt(pas[k]); next(); il.Emit(OpCodes.Ldloc, cyc); il.Emit(OpCodes.Ldloc, st); Call("JExitOk"); il.Emit(OpCodes.Ret);
        }
        void LeaveAfter(int k, Action next)
        {
            Ld0(); IpAt(pas[k]); next(); il.Emit(OpCodes.Ldloc, cyc); il.Emit(OpCodes.Ldloc, st); Call("JExitOk"); il.Emit(OpCodes.Ret);
        }
        void ExitFail(int k)
        {
            Ld0(); IpAt(pas[k]); il.Emit(OpCodes.Ldloc, cyc); il.Emit(OpCodes.Ldloc, st); Call("JExitFail"); il.Emit(OpCodes.Ret);
        }

        for (int k = 0; k < n; k++)
        {
            var e = ins[k];
            uint pa = pas[k], pe = pa + e.Len;
            int bits = e.Bits;
            bool mem = DcHasModrm(e.Kind) && e.Mod != 3;
            il.MarkLabel(body[k]);
            if (e.Kind == DK_INTERP)
            {   // out of line, through the interpreter's own loop body (it counts the instruction clock itself)
                var notFail = il.DefineLabel(); var notEnd = il.DefineLabel(); var leaveG = il.DefineLabel();
                Ld0(); IpAt(pa); Ld(cyc); Ld(st); Call("JInterp"); Stl(Y);
                Ld(Y); I4(FAIL); il.Emit(OpCodes.Bne_Un, notFail); I4(FAIL); Op(OpCodes.Ret);
                il.MarkLabel(notFail);
                Ld(Y); I4(END); il.Emit(OpCodes.Bne_Un, notEnd); I4(JIT_END); Op(OpCodes.Ret);
                il.MarkLabel(notEnd);
                Ld0(); Call("JCycle"); Stl(cyc);
                Ld0(); Call("JSteps"); Op(OpCodes.Ldc_I4_1); Op(OpCodes.Sub); Stl(st);
                Ld0(); Bool(c16); U4(pa); I4(segMask); Bool(e.Mod != 0); Call("JStill"); il.Emit(OpCodes.Brfalse, leaveG);
                foreach (int s in usedSegs) { Ld0(); I4(s); Call("JSegBase"); Stl(sb[s]!); }
                Ld0(); Call("JSpm"); Stl(spm);
                var notRep = il.DefineLabel();   // a REP string instruction that goes on: run it again
                Ld0(); Call("JNextIp"); IpAt(pa); il.Emit(OpCodes.Bne_Un, notRep);
                Ld(st); Op(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ble, leaveG);
                il.Emit(OpCodes.Br, body[k]);
                il.MarkLabel(notRep);
                var fall = il.DefineLabel();
                Ld0(); Call("JNextIp"); IpAt(pe); il.Emit(OpCodes.Beq, fall);
                il.MarkLabel(leaveG);
                Ld0(); Ld0(); Call("JIp"); Ld0(); Call("JNextIp"); Ld(cyc); Ld(st); Call("JExitOk"); Op(OpCodes.Ret);
                il.MarkLabel(fall);
                Continue(k, () => IpAt(pe), pe, true);   // (falls into the next instruction's code, which follows)
                continue;
            }
            il.Emit(OpCodes.Ldloc, cyc); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, cyc);

            // effective address and linear address of a memory operand
            if (mem)
            {
                U4(e.Disp);
                if (e.Base < 8) { LdReg(32, e.Base); il.Emit(OpCodes.Add); }
                if (e.Index < 8) { LdReg(32, e.Index); if (e.Scale != 0) { I4(e.Scale); il.Emit(OpCodes.Shl); } il.Emit(OpCodes.Add); }
                if (e.A16) { I4(0xffff); il.Emit(OpCodes.And); }
                il.Emit(OpCodes.Stloc, ea);
                if (e.Kind != DK_LEA) { il.Emit(OpCodes.Ldloc, sb[e.Seg]!); il.Emit(OpCodes.Ldloc, ea); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, la); }
            }

            void ReadE() { if (mem) { LdMem(bits, la); Ld(V); } else LdReg(bits, e.Rm); }
            void WriteET()
            {
                if (!mem) { StReg(bits, e.Rm, () => Ld(t)); return; }
                StMem(bits, la, t);
                SmcCheck(la, bits / 8);
            }
            // a store that touched [lo, hi): leave right after this instruction (the code may have changed)
            void SmcCheck(LocalBuilder addr, int size)
            {
                var fine = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, addr); U4(lo - (uint)size + 1); il.Emit(OpCodes.Sub); U4(hi - lo + (uint)size - 1); il.Emit(OpCodes.Bge_Un, fine);
                StDec(); LeaveAfter(k, () => IpAt(pe));
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
                if (!pureConst) { Ld0(); il.Emit(OpCodes.Ldloc, la); Call("JRam"); il.Emit(OpCodes.Brfalse, noSpin); }
                il.Emit(OpCodes.Ldloc, st); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ble, noSpin);
                il.Emit(OpCodes.Ldloc, cyc); il.Emit(OpCodes.Ldloc, st); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_I8);
                il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, cyc);
                il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, st);
                il.MarkLabel(noSpin);
                StDec();
                Continue(k, () => il.Emit(OpCodes.Ldloc, t), pe + 2 + rel, false);
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
                case DK_MOVEG:
                    LdReg(bits, e.Reg); Stl(t); WriteET();
                    break;
                case DK_MOVEI:
                    U4(e.Imm); Stl(t); WriteET();
                    break;
                case DK_MOVRI:
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
                    Ld0(); I4(e.O16 ? 16 : 32); I4(op); Bool(e.Kind == DK_DEC); Call("JIncDec");
                    break;
                case DK_JCC:
                case DK_JMP:
                {
                    var notTaken = il.DefineLabel();
                    if (e.Kind == DK_JCC) { CondTop((uint)op); il.Emit(OpCodes.Brfalse, notTaken); }
                    IpAt(pe); U4(e.Imm); il.Emit(OpCodes.Add);
                    if (c16) { I4(0xffff); il.Emit(OpCodes.And); }
                    il.Emit(OpCodes.Stloc, t);
                    StDec();
                    Continue(k, () => il.Emit(OpCodes.Ldloc, t), pe + e.Imm, false);
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
                        StF(FSrc1, () => { Ld(A); SextTop(bits); });
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
                    LdReg(pb, op); Stl(t);
                    LdReg(32, 4); I4(pb / 8); Op(OpCodes.Sub); Stl(A);
                    il.Emit(OpCodes.Ldloc, sb[SEG_SS]!); Ld(A); Ld(spm); Op(OpCodes.And); Op(OpCodes.Add); Stl(la);
                    StReg(32, 4, () => { Ld(A); Ld(spm); Op(OpCodes.And); LdReg(32, 4); Ld(spm); Op(OpCodes.Not); Op(OpCodes.And); Op(OpCodes.Or); });
                    StMem(pb, la, t);
                    SmcCheck(la, pb / 8);
                    break;
                }
                case DK_POP:
                {
                    // PopReg(bits, reg): v = [SS base + (sp & spm)]; SetSp(sp + n); SReg(reg, v)
                    int pb = e.O16 ? 16 : 32;
                    LdReg(32, 4); Stl(A);
                    il.Emit(OpCodes.Ldloc, sb[SEG_SS]!); Ld(A); Ld(spm); Op(OpCodes.And); Op(OpCodes.Add); Stl(la);
                    LdMem(pb, la);
                    StReg(32, 4, () => { Ld(A); I4(pb / 8); Op(OpCodes.Add); Ld(spm); Op(OpCodes.And); LdReg(32, 4); Ld(spm); Op(OpCodes.Not); Op(OpCodes.And); Op(OpCodes.Or); });
                    StReg(pb, op, () => Ld(V));
                    break;
                }
                case DK_CALL:
                case DK_RET:
                    Prep();
                    if (e.Kind == DK_CALL) { Ld0(); U4(e.Imm); Call("JCall"); } else { Ld0(); Call("JRet"); }
                    FailIfFalse();
                    StDec();
                    LeaveAfter(k, () => { Ld0(); Call("JNextIp"); });
                    ends = true;
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
        return (JitFn)dm.CreateDelegate(typeof(JitFn));
    }

    private static bool DcHasModrm(byte kind) => kind is DK_MOVEG or DK_MOVGE or DK_ALUEG or DK_ALUGE or DK_GRP1 or DK_GRP2 or DK_TESTEG
        or DK_G3TEST or DK_GRP3 or DK_MOVEI or DK_LEA;
}
