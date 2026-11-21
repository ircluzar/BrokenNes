using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace NesEmulator.X86;

// X86_IDLEPROF=PATH (diagnostics, sprint C1 2026-11-20): run with X86_NOJIT; counts instructions per physical address,
// backward taken branches, spin-skipped cycles; written to PATH at exit (with code bytes for every loop) for tools/x86idle.
public sealed partial class I386
{
    public static readonly string? IdleProfPath = Environment.GetEnvironmentVariable("X86_IDLEPROF") is { Length: > 0 } p ? p : null;
    public static readonly bool IdleProf = IdleProfPath != null;
    public static long ProfSpin, ProfSpinHits;
    private uint _profPa;
    private readonly Dictionary<uint, long> _profIns = new();
    private readonly Dictionary<ulong, long> _profEdge = new();
    private static I386? _profCpu; private readonly Dictionary<uint, bool> _profC16 = new();
    public static long ProfHaltIdle, ProfOtherIdle0, ProfClock0, ProfCycle0; public static Func<long>? ProfIdleNow;

    private void ProfStep(uint la0, long c0)
    {
        _profCpu ??= this.ProfInit();
        uint pa = _profPa; if (pa == 0) return;
        ref long v = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_profIns, pa, out _);
        v += Cycle - c0;
        uint la1 = SegBase[SEG_CS] + NextIp;
        if (la1 <= la0 && la0 - la1 < 4096 && (la0 >> 12) == (la1 >> 12))
        {
            uint pto = pa - (la0 - la1);
            ref long e = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_profEdge, ((ulong)pa << 32) | pto, out _);
            e++; _profC16[pa] = Code16;
        }
    }

    private I386 ProfInit() { ProfCycle0 = Cycle; AppDomain.CurrentDomain.ProcessExit += (_, _) => ProfDump(); return this; }

    private void ProfDump()
    {
        using var w = new StreamWriter(IdleProfPath!);
        w.WriteLine($"cycle {Cycle - ProfCycle0} spin {ProfSpin} spinhits {ProfSpinHits} haltidle {ProfHaltIdle} idlenow {ProfIdleNow?.Invoke() ?? -1} idle0 {ProfOtherIdle0}");
        foreach (var (k, v) in _profIns) w.WriteLine($"i {k:x} {v}");
        foreach (var (k, v) in _profEdge)
        {
            uint from = (uint)(k >> 32), to = (uint)k;
            var sb = new System.Text.StringBuilder();
            for (uint a = to; a < from + 16 && a < Mem.Length; a++) sb.Append(Mem[a].ToString("x2"));
            w.WriteLine($"e {from:x} {to:x} {v} {(_profC16.GetValueOrDefault(from) ? 16 : 32)} {sb}");
        }
    }
}
