using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace NesEmulator.X86;

/// <summary>
/// X86_JITCACHE=&lt;dir&gt; (experimental, off by default; lane E 2026-11-20): a persistent translation cache for the IL JIT.
/// Each compiled region is keyed by SHA-256 (first 128 bits) of EVERYTHING its emitted code depends on: this build (module MVID), the
/// X86_* settings, the region's instructions (address, decoded form, the clock JitExtraCost charges, every guest byte of each instruction
/// and of a folded JZ / JNZ) and every compile-time assumption Emit takes (code size, paging, RAM size, I/O memory, segment specialization
/// constants, live immediates via the decoded form). Same key = the same arguments to Emit = the same IL, so a hit is the code a compile
/// would make now; the installed code is still checked against memory before every run like any other block (JitSame).
/// Misses are compiled as usual and also queued; packs of them are emitted again into a PersistedAssemblyBuilder DLL (+ an index) in the
/// directory. A later process loads the packs and, right away in the background, has the .NET JIT compile (RuntimeHelpers.PrepareMethod)
/// the entries whose bytes are in memory now, in the order they were first compiled: when the CPU asks for such a region, it installs at
/// once (no pending wait, no IL build, no JIT on the critical path). X86_JITCACHE_TIER=1: no AggressiveOptimization on the stored methods
/// (tier-0 first, then tiered; A/B). X86_JITCACHE_NOPRE=1: no prefetch (hits compile on demand: measures what the IL build alone saves).
/// X86_JITCACHE_RO=1: read only (write no packs).
/// </summary>
public sealed partial class I386
{
    private static readonly string? JcDir = Environment.GetEnvironmentVariable("X86_JITCACHE") is { Length: > 0 } d ? d : null;
    private static readonly bool JcOn = JcDir != null && JitOn && !JitTier;
    private static readonly bool JcTierOpt = Environment.GetEnvironmentVariable("X86_JITCACHE_TIER") != "1";
    private static readonly bool JcNoPre = Environment.GetEnvironmentVariable("X86_JITCACHE_NOPRE") == "1";
    private static readonly bool JcReadOnly = Environment.GetEnvironmentVariable("X86_JITCACHE_RO") == "1";
    private const int JcPack = 1024;   // misses per pack

    /// <summary>One request of the CPU: the key, the clock costs (taken now, so the stored code is exactly what Emit makes from these
    /// arguments), and a hit's delegate (Ready: already compiled by the .NET JIT; Hit: still to be compiled at its first call).</summary>
    private sealed class JcReq
    {
        public I386 Owner = null!; public UInt128 Key; public int[] Extra = null!; public JitFn? Hit, Ready; public JcEnt? Ent;
        public uint Start, Lo, Hi, MemSize; public uint[] Pas = null!; public DEntry[] Ins = null!; public bool C16, IoMem, Pg; public SegSpec? Spec;
        public (uint a, byte n)[] Spans = null!; public ulong BytesHash; public long Seq;
    }
    /// <summary>A stored entry: where it lives, the guest bytes it was made from (for the prefetch's guess), and its delegate once made.</summary>
    private sealed class JcEnt
    {
        public string Pack = ""; public string Type = ""; public long Order; public (uint a, byte n)[] Spans = Array.Empty<(uint, byte)>(); public ulong BytesHash;
        public volatile JitFn? Fn; public volatile bool Prepared; public int Claimed;
    }

    private static readonly object JcLock = new();
    private static Dictionary<UInt128, JcEnt>? _jcIndex;
    private static readonly Dictionary<string, Assembly> JcAsms = new();
    private static readonly List<JcReq> JcNew = new();
    private static byte[]? _jcSalt;
    private static long _jcSeq;
    private static bool _jcExitHooked;
    private bool _jcPrefetched;
    public static long JcHitsReady, JcHitsCold, JcMisses, JcStored, JcLoaded, JcPrepared, JcPrepTicks, JcKeyTicks, JcFlushTicks, JcFlushErrors;

    public string JcStat() => $"jitcache: hits ready {JcHitsReady} cold {JcHitsCold} misses {JcMisses} | index {JcLoaded} prefetched {JcPrepared} ({JcPrepTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms JIT) stored {JcStored} (flush {JcFlushTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms, errors {JcFlushErrors}) key {JcKeyTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms";

    private static byte[] JcSalt()
    {
        if (_jcSalt != null) return _jcSalt;
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(e => ((string)e.Key).ToUpperInvariant() + "=" + e.Value)
            .Where(s => s.StartsWith("X86_") && !s.StartsWith("X86_JITCACHE") && !JcIgnoredEnv.Any(x => s.StartsWith(x + "=")))
            .OrderBy(s => s, StringComparer.Ordinal);
        string cfg = "jc1|" + typeof(I386).Module.ModuleVersionId + "|" + RuntimeInformation.FrameworkDescription + "|" + string.Join("|", env);
        return _jcSalt = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cfg));
    }
    /// <summary>Settings that change how a run is driven, never what Emit makes (checked: Emit reads none of them).</summary>
    private static readonly string[] JcIgnoredEnv = { "X86_JITSYNC", "X86_JITTHREADS", "X86_NONIC", "X86_JITSTAT", "X86_TIMEPROBE", "X86_D" };

    private readonly MemoryStream _jcBlob = new();
    private JcReq? JcRequest(uint start, uint[] pas, DEntry[] ins, uint lo, uint hi, bool c16, uint memSize, bool ioMem, bool pg, SegSpec? spec)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        JcInit();
        JcPrefetchStart();
        int n = pas.Length;
        var extra = new int[n];
        var spans = new (uint a, byte n)[n];
        var b = _jcBlob; b.SetLength(0);
        var w = new BinaryWriter(b);
        w.Write(JcSalt());
        w.Write(start); w.Write(n); w.Write(lo); w.Write(hi); w.Write(c16); w.Write(memSize); w.Write(ioMem); w.Write(pg);
        ulong bh = 14695981039346656037UL;
        for (int k = 0; k < n; k++)
        {
            var e = ins[k];
            extra[k] = JitExtraCost(pas[k], e.Kind, e.Reg, e.Bits, DcHasModrm(e.Kind) && e.Mod != 3);
            w.Write(pas[k]);
            w.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref e, 1)));
            w.Write(extra[k]);
            int len = e.Len + (e.Folds ? 2 : 0);   // (every byte, a live immediate's too: conservative)
            spans[k] = (pas[k], (byte)len);
            var mem = Mem.AsSpan((int)pas[k], len);
            w.Write((byte)len); w.Write(mem);
            foreach (byte x in mem) bh = (bh ^ x) * 1099511628211UL;
        }
        if (spec == null) w.Write(-1);
        else { w.Write(spec.Mask); w.Write(spec.Spm); for (int s = 0; s < 6; s++) { w.Write(spec.Base[s]); w.Write(spec.Span[s]); w.Write(spec.Lo[s]); } }
        w.Flush();
        var h = SHA256.HashData(b.GetBuffer().AsSpan(0, (int)b.Length));
        var key = MemoryMarshal.Read<UInt128>(h);
        var r = new JcReq { Owner = this, Key = key, Extra = extra, Start = start, Lo = lo, Hi = hi, MemSize = memSize, Pas = pas, Ins = ins, C16 = c16, IoMem = ioMem, Pg = pg, Spec = spec, Spans = spans, BytesHash = bh };
        JcEnt? ent; lock (JcLock) _jcIndex!.TryGetValue(key, out ent);
        if (ent != null)
        {
            r.Ent = ent;
            var fn = ent.Fn ?? JcDelegate(ent);
            if (fn != null) { if (ent.Prepared) r.Ready = fn; else { r.Hit = fn; JcHitsCold++; } }
        }
        if (r.Hit == null && r.Ready == null) JcMisses++;
        JcKeyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        return r;
    }

    /// <summary>The worker compiled a request: a hit's code is now ready for the next time; a miss is queued for the next pack.</summary>
    private void JcCompiled(JcReq r, JitFn fn)
    {
        if (fn == null) return;
        if (r.Ent != null) { r.Ent.Fn = fn; if (r.Hit != null) r.Ent.Prepared = true; return; }
        if (JcReadOnly) return;
        bool flush;
        lock (JcLock)
        {
            if (_jcIndex!.ContainsKey(r.Key)) return;
            _jcIndex[r.Key] = r.Ent = new JcEnt { Fn = fn, Prepared = true };   // (this process: the code is already there)
            r.Seq = _jcSeq++;
            JcNew.Add(r);
            flush = JcNew.Count >= JcPack;
        }
        if (flush) ThreadPool.QueueUserWorkItem(_ => JcFlush());
    }

    private static void JcInit()
    {
        if (_jcIndex != null) return;
        lock (JcLock)
        {
            if (_jcIndex != null) return;
            var idx = new Dictionary<UInt128, JcEnt>();
            Directory.CreateDirectory(JcDir!);
            var salt = JcSalt();
            foreach (var f in Directory.GetFiles(JcDir!, "p_*.idx").OrderBy(x => x, StringComparer.Ordinal))
            {
                try
                {
                    using var rd = new BinaryReader(File.OpenRead(f));
                    if (!rd.ReadBytes(32).AsSpan().SequenceEqual(salt)) continue;   // another build / other settings: none of it applies
                    string dll = Path.ChangeExtension(f, ".dll");
                    if (!File.Exists(dll)) continue;
                    int cnt = rd.ReadInt32();
                    for (int i = 0; i < cnt; i++)
                    {
                        var key = MemoryMarshal.Read<UInt128>(rd.ReadBytes(16));
                        var e = new JcEnt { Pack = dll, Type = rd.ReadString(), Order = rd.ReadInt64(), BytesHash = rd.ReadUInt64() };
                        int ns = rd.ReadInt32(); e.Spans = new (uint, byte)[ns];
                        for (int s = 0; s < ns; s++) e.Spans[s] = (rd.ReadUInt32(), rd.ReadByte());
                        idx.TryAdd(key, e);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"jitcache: {Path.GetFileName(f)} skipped: {ex.Message}"); }
            }
            JcLoaded = idx.Count;
            _jcIndex = idx;
            if (!_jcExitHooked && !JcReadOnly) { _jcExitHooked = true; AppDomain.CurrentDomain.ProcessExit += (_, _) => JcFlush(); }
        }
    }

    private static JitFn? JcDelegate(JcEnt e)
    {
        if (e.Fn != null) return e.Fn;
        try
        {
            Assembly? a;
            lock (JcAsms)
                if (!JcAsms.TryGetValue(e.Pack, out a)) JcAsms[e.Pack] = a = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(e.Pack));
            var m = a.GetType(e.Type)!.GetMethod("x86blk")!;
            return e.Fn = (JitFn)m.CreateDelegate(typeof(JitFn));
        }
        catch (Exception ex) { Console.Error.WriteLine($"jitcache: {e.Type} unusable: {ex.Message}"); return null; }
    }

    /// <summary>After the first compile request of this CPU (a load or a boot is done by then): compile, in the background, the stored entries
    /// whose bytes memory holds now, oldest first (a guess: a wrong one costs only background JIT time; hits are decided by the full key).</summary>
    private void JcPrefetchStart()
    {
        if (_jcPrefetched || JcNoPre) return;
        _jcPrefetched = true;
        List<JcEnt> todo;
        lock (JcLock) todo = _jcIndex!.Values.Where(e => e.Fn == null && e.Pack.Length > 0).OrderBy(e => e.Order).ToList();
        if (todo.Count == 0) return;
        int next = -1;
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        if (int.TryParse(Environment.GetEnvironmentVariable("X86_JITCACHE_PRE"), out int pt)) threads = Math.Max(1, pt);
        for (int t = 0; t < threads; t++)
            new Thread(() =>
            {
                for (int i; (i = Interlocked.Increment(ref next)) < todo.Count;)
                {
                    var e = todo[i];
                    if (e.Prepared || !JcBytesNow(e)) continue;
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var fn = JcDelegate(e);
                    if (fn == null) continue;
                    try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(fn); System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(fn.Method.MethodHandle); e.Prepared = true; Interlocked.Increment(ref JcPrepared); }
                    catch (Exception) { }
                    Interlocked.Add(ref JcPrepTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                }
            }) { IsBackground = true, Name = "x86 jitcache prefetch", Priority = ThreadPriority.BelowNormal }.Start();
    }
    private bool JcBytesNow(JcEnt e)
    {   // (read while the CPU runs: only a guess)
        ulong bh = 14695981039346656037UL;
        foreach (var (a, n) in e.Spans)
        {
            if (a + n > MemSize) return false;
            foreach (byte x in Mem.AsSpan((int)a, n)) bh = (bh ^ x) * 1099511628211UL;
        }
        return bh == e.BytesHash;
    }

    /// <summary>Write the queued misses as one pack: a DLL of static methods (emitted again from the very same arguments) + its index.</summary>
    public static void JcFlush()
    {
        List<JcReq> batch;
        lock (JcLock) { if (JcNew.Count == 0) return; batch = new List<JcReq>(JcNew); JcNew.Clear(); }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (JcFlushLock)
        {
            try
            {
                string id = $"p_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
                var ab = new PersistedAssemblyBuilder(new AssemblyName("x86jc_" + id), typeof(object).Assembly);
                ab.SetCustomAttribute(new CustomAttributeBuilder(typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructor(new[] { typeof(string) })!,
                    new object[] { typeof(I386).Assembly.GetName().Name! }));
                var mod = ab.DefineDynamicModule("x86jc_" + id);
                var ok = new List<(JcReq r, string type)>();
                foreach (var r in batch)
                {
                    string type = "b" + r.Key.ToString("X32");
                    try { r.Owner.Emit(r.Start, r.Pas, r.Ins, r.Lo, r.Hi, r.C16, r.MemSize, r.IoMem, r.Pg, r.Spec, r.Extra, mod, type, JcTierOpt); ok.Add((r, type)); }
                    catch (Exception ex) { JcFlushErrors++; Console.Error.WriteLine($"jitcache: {r.Start:X8} not stored: {ex.Message}"); }
                }
                if (ok.Count == 0) return;
                string dll = Path.Combine(JcDir!, id + ".dll"), idx = Path.Combine(JcDir!, id + ".idx");
                using (var fs = File.Create(dll + ".tmp")) ab.Save(fs);
                using (var fs = File.Create(idx + ".tmp"))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(JcSalt()); bw.Write(ok.Count);
                    foreach (var (r, type) in ok)
                    {
                        Span<byte> kb = stackalloc byte[16]; MemoryMarshal.Write(kb, in r.Key); bw.Write(kb);
                        bw.Write(type); bw.Write(DateTime.UtcNow.Ticks / 10000 * 100000 + r.Seq); bw.Write(r.BytesHash);
                        bw.Write(r.Spans.Length); foreach (var (a, n) in r.Spans) { bw.Write(a); bw.Write(n); }
                    }
                }
                File.Move(dll + ".tmp", dll); File.Move(idx + ".tmp", idx);   // (the index last: a reader sees a pack only when it is whole)
                JcStored += ok.Count;
            }
            catch (Exception ex) { JcFlushErrors++; Console.Error.WriteLine($"jitcache: flush failed: {ex.GetType().Name} {ex.Message}"); }
            finally { JcFlushTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
        }
    }
    private static readonly object JcFlushLock = new();
}
