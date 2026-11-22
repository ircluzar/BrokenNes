using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NesEmulator.X86;

// X86_WAITSKIP=1 (lever 2 of the Pentium II speed work, 2026-11-21; off by default): the wait-loop skip.
// A game's main loop that only polls (StarCraft: PeekMessage + the timer, ~3.6k instructions a pass, ~1570 passes a frame) brings the
// whole machine back to the very same state at the end of each pass until an interrupt changes something. Generic proof, no game list:
//   at a batch start in ring 3 (IF set) the CPU state is taken as the candidate C (everything a save state holds but the cycle count);
//   the batches then run in the interpreter with every RAM write logged per 64-byte line (the code-line hook, _codeMark swapped for an
//   all-ones map) and the I/O ports / device windows behind a recording bus. The first return to C ends pass A: its lines are copied.
//   The next return to C ends pass B: when B wrote only lines A wrote and all of those hold what they held at the end of A, pass B
//   changed nothing at all, so every later pass repeats it exactly until something outside the CPU changes. Any port or device-window
//   access, RDTSC / RDPMC, a delivered interrupt, HLT or a budget overrun ends the proof (then it backs off).
// Proven: the CPU parks at C like HLT (PcMachine: guest time goes on as idle, devices run, the next interrupt unparks it), instead of
// running passes that cannot change anything. Deterministic (the decisions depend on guest state only; the proof is interpreted with
// and without the JIT) and save-state exact (the whole detector state is the "WAIT" block).
public sealed partial class I386
{
    public static readonly bool WaitSkipOn = Environment.GetEnvironmentVariable("X86_WAITSKIP") == "1";
    const int WsBudget = 20000, WsMaxGap = 1024, WsRetries = 4;

    int _wsPhase;            // 0 off, 1 pass A, 2 pass B, 3 parked
    int _wsGap = 1, _wsWait, _wsLeft, _wsTries;
    uint _wsIp, _wsCs;
    readonly uint[] _wsR = new uint[9];
    byte[] _wsSig = Array.Empty<byte>();
    readonly List<uint> _wsLinesA = new(), _wsLinesB = new();
    readonly HashSet<uint> _wsSetA = new(), _wsSetB = new();
    byte[] _wsSnap = Array.Empty<byte>();
    int _wsFail;
    byte[]? _wsOnes, _wsRealMark;
    IX86Bus? _wsRealBus;
    readonly MemoryStream _wsMs = new();

    public static long WsProofs, WsParks, WsParkedBatches, WsSkippedInstr;
    public static readonly long[] WsFails = new long[8];   // 1 io/mmio, 2 tsc/pmc, 3 irq, 4 budget, 5 halt, 6 retries, 7 exception/other
    static readonly string[] WsFailName = { "", "io", "tsc", "irq", "budget", "halt", "retries", "other" };
    static bool _wsDumpHooked;

    public bool WsParked => _wsPhase == 3;
    bool WsLogging => _wsPhase is 1 or 2;

    /// <summary>PcMachine, a parked CPU with an interrupt to take: back to running at C.</summary>
    public void WsUnpark() { if (_wsPhase == 3) { _wsPhase = 0; _wsGap = 1; _wsWait = 0; } }
    public void WsParkedBatch(long idleInstr) { WsParkedBatches++; WsSkippedInstr += idleInstr; }

    sealed class WsBus : IX86Bus
    {
        readonly I386 _c; readonly IX86Bus _b;
        public WsBus(I386 c, IX86Bus b) { _c = c; _b = b; }
        public byte In8(int p) { _c._wsFail = 1; return _b.In8(p); }
        public void Out8(int p, byte v) { _c._wsFail = 1; _b.Out8(p, v); }
        public ushort In16(int p) { _c._wsFail = 1; return _b.In16(p); }
        public void Out16(int p, ushort v) { _c._wsFail = 1; _b.Out16(p, v); }
        public uint In32(int p) { _c._wsFail = 1; return _b.In32(p); }
        public void Out32(int p, uint v) { _c._wsFail = 1; _b.Out32(p, v); }
        public bool MapsIoMem => _b.MapsIoMem;
        public byte MemRead8(uint a) { _c._wsFail = 1; return _b.MemRead8(a); }
        public void MemWrite8(uint a, byte v) { _c._wsFail = 1; _b.MemWrite8(a, v); }
        public ushort MemRead16(uint a) { _c._wsFail = 1; return _b.MemRead16(a); }
        public void MemWrite16(uint a, ushort v) { _c._wsFail = 1; _b.MemWrite16(a, v); }
        public uint MemRead32(uint a) { _c._wsFail = 1; return _b.MemRead32(a); }
        public void MemWrite32(uint a, uint v) { _c._wsFail = 1; _b.MemWrite32(a, v); }
        public int AckIrq() { _c._wsFail = 3; return _b.AckIrq(); }
    }

    byte[] WsSigNow()
    {
        _wsMs.SetLength(0);
        var w = new BinaryWriter(_wsMs);
        foreach (var r in R) w.Write(r);
        w.Write(Ip); w.Write(NextIp); w.Write(Flags); w.Write(Cpl); w.Write(Code16); w.Write(SpMask); w.Write(Halt);
        for (int i = 0; i < 8; i++) { w.Write(SegSel[i]); w.Write(SegBase[i]); w.Write(SegLimit[i]); w.Write(SegFlags[i]); }
        w.Write(IdtBase); w.Write(IdtLimit); w.Write(GdtBase); w.Write(GdtLimit);
        w.Write(Cr0); w.Write(Cr2); w.Write(Cr3);
        foreach (var d in Dr) w.Write(d);
        w.Write(Excno); w.Write(Excerr); w.Write(Intr);
        w.Write(_ccOp); w.Write(_ccDst); w.Write(_ccDst2); w.Write(_ccSrc1); w.Write(_ccSrc2); w.Write(_ccMask);
        if (!_legacy) SaveModelState(w);
        w.Flush();
        return _wsMs.ToArray();
    }

    bool WsQuick() { for (int i = 0; i < 8; i++) if (R[i] != _wsR[i]) return false; return Flags == _wsR[8]; }

    void WsLogOn()
    {
        _wsOnes ??= new byte[_codeMark.Length];
        if (_wsOnes[0] == 0) Array.Fill(_wsOnes, (byte)1);
        _wsRealMark = _codeMark; _codeMark = _wsOnes;
        _wsRealBus = _bus; _bus = new WsBus(this, _wsRealBus);
    }

    void WsLogOff()
    {
        if (_wsRealMark != null) { _codeMark = _wsRealMark; _wsRealMark = null; }
        if (_wsRealBus != null) { _bus = _wsRealBus; _wsRealBus = null; }
    }

    /// <summary>The swapped code map sends every RAM store here (CodeWrite): log its lines; true = a real code line, go on invalidating.</summary>
    bool WsLog(uint a, uint n)
    {
        var lines = _wsPhase == 1 ? _wsLinesA : _wsLinesB; var set = _wsPhase == 1 ? _wsSetA : _wsSetB;
        bool real = false;
        for (uint l = a >> 6, e = (a + n - 1) >> 6; l <= e; l++)
        {
            if (set.Add(l)) lines.Add(l);
            if (_wsRealMark![l] != 0) real = true;
        }
        return real;
    }

    void WsFailNow(int why)
    {
        WsLogOff();
        WsFails[why]++;
        _wsPhase = 0;
        _wsGap = Math.Min(_wsGap * 2, WsMaxGap); _wsWait = _wsGap;
        WsClear();
    }

    void WsClear() { _wsLinesA.Clear(); _wsSetA.Clear(); _wsLinesB.Clear(); _wsSetB.Clear(); _wsFail = 0; }

    void WsSnapA()
    {
        if (_wsSnap.Length < _wsLinesA.Count * 64) _wsSnap = new byte[Math.Max(_wsLinesA.Count * 64, 2 * _wsSnap.Length)];
        for (int i = 0; i < _wsLinesA.Count; i++) Buffer.BlockCopy(Mem, (int)(_wsLinesA[i] << 6), _wsSnap, i * 64, 64);
    }

    bool WsSameAsSnap()
    {
        foreach (uint l in _wsLinesB) if (!_wsSetA.Contains(l)) return false;
        for (int i = 0; i < _wsLinesA.Count; i++)
            if (!Mem.AsSpan((int)(_wsLinesA[i] << 6), 64).SequenceEqual(_wsSnap.AsSpan(i * 64, 64))) return false;
        return true;
    }

    /// <summary>Step: a pending interrupt is about to be taken (before CallIsr).</summary>
    void WsOnIrq() { if (WsLogging) WsFailNow(3); else if (_wsPhase == 3) WsUnpark(); }

    /// <summary>Step, after the interrupt: true = this batch runs in the proof (WsExec).</summary>
    bool WsBegin()
    {
        if (_wsPhase == 3) _wsPhase = 0;   // someone stepped a parked CPU: it simply runs on
        if (_wsPhase == 0)
        {
            if (_wsWait > 0) { _wsWait--; return false; }
            if (Cpl != 3 || (Flags & IF) == 0 || Halt) { _wsWait = 1; return false; }
            if (!_wsDumpHooked) { _wsDumpHooked = true; AppDomain.CurrentDomain.ProcessExit += (_, _) => WsDump(); }
            WsProofs++;
            WsClear();
            _wsIp = NextIp; _wsCs = SegSel[SEG_CS];
            for (int i = 0; i < 8; i++) _wsR[i] = R[i]; _wsR[8] = Flags;
            _wsSig = WsSigNow();
            _wsPhase = 1; _wsLeft = WsBudget; _wsTries = 0;
        }
        WsLogOn();
        return true;
    }

    bool WsExec(int stepcount)
    {
        _steps = stepcount;
        for (; _steps > 0; _steps--)
        {
            int r = Step1(false);
            if (_wsFail != 0) { int f = _wsFail; WsFailNow(f); return WsRest(r); }
            if (r == FAIL) return false;          // Step delivers the fault with the log still on, then turns it off
            if (r != OK || Halt) { WsFailNow(5); return true; }
            if (--_wsLeft <= 0) { WsFailNow(4); return WsRest(r); }
            if (NextIp != _wsIp || SegSel[SEG_CS] != _wsCs || !WsQuick()) continue;
            if (!WsSigNow().AsSpan().SequenceEqual(_wsSig)) continue;
            if (_wsPhase == 2 && WsSameAsSnap())
            {
                WsLogOff(); WsClear();
                _wsPhase = 3; WsParks++;
                return true;                       // parked: like HLT, the rest of this batch is idle time (PcMachine)
            }
            if (_wsPhase == 2)
            {
                if (WsDiagOn) WsDiagPass();
                if (++_wsTries > WsRetries) { WsFailNow(6); return WsRest(r); }
                _wsLinesA.Clear(); _wsSetA.Clear();
                foreach (uint l in _wsLinesB) { _wsLinesA.Add(l); _wsSetA.Add(l); }
            }
            WsSnapA();
            _wsLinesB.Clear(); _wsSetB.Clear();
            _wsPhase = 2; _wsPassStart = _wsLeft;
        }
        return true;
    }

    // the proof ended inside a batch: the batch goes on as usual
    bool WsRest(int r)
    {
        if (r == FAIL) return false;
        if (r != OK) return true;
        int left = _steps - 1;
        return left <= 0 || Exec1(left);
    }

    void WsSave(BinaryWriter w)
    {
        w.Write(_wsPhase); w.Write(_wsGap); w.Write(_wsWait); w.Write(_wsLeft); w.Write(_wsTries);
        w.Write(_wsIp); w.Write(_wsCs); foreach (uint v in _wsR) w.Write(v);
        w.Write(_wsSig.Length); w.Write(_wsSig);
        w.Write(_wsLinesA.Count); foreach (uint l in _wsLinesA) w.Write(l);
        w.Write(_wsLinesB.Count); foreach (uint l in _wsLinesB) w.Write(l);
        w.Write(_wsPhase == 2 ? _wsLinesA.Count * 64 : 0); if (_wsPhase == 2) w.Write(_wsSnap, 0, _wsLinesA.Count * 64);
    }

    void WsLoad(BinaryReader r)
    {
        WsLogOff(); WsClear();
        _wsPhase = r.ReadInt32(); _wsGap = r.ReadInt32(); _wsWait = r.ReadInt32(); _wsLeft = r.ReadInt32(); _wsTries = r.ReadInt32();
        _wsIp = r.ReadUInt32(); _wsCs = r.ReadUInt32(); for (int i = 0; i < _wsR.Length; i++) _wsR[i] = r.ReadUInt32();
        _wsSig = r.ReadBytes(r.ReadInt32());
        int na = r.ReadInt32(); for (int i = 0; i < na; i++) { uint l = r.ReadUInt32(); _wsLinesA.Add(l); _wsSetA.Add(l); }
        int nb = r.ReadInt32(); for (int i = 0; i < nb; i++) { uint l = r.ReadUInt32(); _wsLinesB.Add(l); _wsSetB.Add(l); }
        int ns = r.ReadInt32(); if (_wsSnap.Length < ns) _wsSnap = new byte[ns]; if (r.Read(_wsSnap, 0, ns) != ns) throw new EndOfStreamException();
        if (!WaitSkipOn) { _wsPhase = 0; _wsGap = 1; _wsWait = 0; WsClear(); }
    }

    void WsReset() { WsLogOff(); WsClear(); _wsPhase = 0; _wsGap = 1; _wsWait = 0; }

    /// <summary>The machine's save state: the "WAIT" block (only while the detector is not at rest, so states stay as they were).</summary>
    public bool WsActive => _wsPhase != 0 || _wsGap != 1 || _wsWait != 0;
    public void WsSaveBlock(BinaryWriter w) => WsSave(w);
    public void WsLoadBlock(BinaryReader r) => WsLoad(r);
    public void WsResetState() => WsReset();

    static void WsDump()
    {
        var sb = new System.Text.StringBuilder($"WAITSKIP proofs {WsProofs} parks {WsParks} parkedBatches {WsParkedBatches} skippedInstr {WsSkippedInstr} fails");
        for (int i = 1; i < WsFails.Length; i++) sb.Append($" {WsFailName[i]}={WsFails[i]}");
        Console.Error.WriteLine(sb.ToString()); WsDiagDump();
    }

    static readonly bool WsDiagOn = Environment.GetEnvironmentVariable("X86_WSDIAG") == "1";
    sealed class WsD { public long N; public long Delta; public bool Const = true; public uint Ip; public long Same; }
    static readonly Dictionary<uint, WsD> WsDiag = new();
    static readonly Dictionary<uint, long> WsDiagLoops = new();
    static long WsDiagPasses, WsDiagNewLines, WsDiagDiffDw, WsDiagPassLen;
    int _wsPassStart;
    void WsDiagPass()
    {
        WsDiagPasses++;
        WsDiagPassLen += _wsPassStart - _wsLeft; _wsPassStart = _wsLeft;
        WsDiagLoops[_wsIp] = WsDiagLoops.GetValueOrDefault(_wsIp) + 1;
        foreach (uint l in _wsLinesB) if (!_wsSetA.Contains(l)) WsDiagNewLines++;
        for (int i = 0; i < _wsLinesA.Count; i++)
        {
            int b = (int)(_wsLinesA[i] << 6);
            for (int o = 0; o < 64; o += 4)
            {
                uint now = BitConverter.ToUInt32(Mem, b + o), was = BitConverter.ToUInt32(_wsSnap, i * 64 + o);
                if (now == was) continue;
                WsDiagDiffDw++;
                uint a = (uint)(b + o); long d = (int)(now - was);
                if (!WsDiag.TryGetValue(a, out var e)) { e = new WsD { Delta = d, Ip = _wsIp }; WsDiag[a] = e; }
                else if (e.Delta != d) e.Const = false; else e.Same++;
                e.N++;
            }
        }
    }
    static void WsDiagDump()
    {
        if (!WsDiagOn) return;
        var o = Console.Error;
        o.WriteLine($"WSDIAG passes {WsDiagPasses} avgPassInstr {(WsDiagPasses > 0 ? WsDiagPassLen / WsDiagPasses : 0)} newLinesInB {WsDiagNewLines} diffDwords {WsDiagDiffDw} distinctAddr {WsDiag.Count}");
        foreach (var kv in WsDiagLoops.OrderByDescending(k => k.Value).Take(10)) o.WriteLine($"WSDIAG loop ip {kv.Key:X8} passes {kv.Value}");
        foreach (var kv in WsDiag.OrderByDescending(k => k.Value.N).Take(40))
            o.WriteLine($"WSDIAG addr {kv.Key:X8} n {kv.Value.N} firstDelta {kv.Value.Delta} sameDelta {kv.Value.Same} const {kv.Value.Const} loopIp {kv.Value.Ip:X8}");
    }

    // X86_RWATCH=1 (diagnostic): data reads of the 8 counter dwords seen by X86_WSDIAG; per reader CS:EIP, count, in-proof or not, code bytes
    static readonly uint[] RwAddr = { 0x0234F3CC, 0x03942D5C, 0x03942D60, 0x03942D6C, 0x03942D70, 0x03942D90, 0x03942E08, 0x0027B880 };
    static readonly bool RwOn = Environment.GetEnvironmentVariable("X86_RWATCH") == "1";
    sealed class RwR { public long N, InProof; public uint Addr, Cs, LastVal; public string Code = ""; public long FirstCycle; }
    static readonly Dictionary<(uint, uint, uint), RwR> RwReads = new();
    bool _rwBusy, _rwHooked;
    void RwHit(uint a, int n)
    {
        if (_rwBusy) return;
        foreach (uint w in RwAddr)
        {
            if (a + (uint)n <= w || a >= w + 4) continue;
            _rwBusy = true;
            if (!_rwHooked) { _rwHooked = true; AppDomain.CurrentDomain.ProcessExit += (_, _) => RwDump(); }
            var k = (SegSel[SEG_CS], Ip, w);
            if (!RwReads.TryGetValue(k, out var e))
            {
                e = new RwR { Addr = w, Cs = SegSel[SEG_CS], FirstCycle = Cycle };
                var sb = new System.Text.StringBuilder();
                for (uint i = 0; i < 12; i++) { if (((Ip + i) & 0xfff) == 0 && i > 0) break; if (!CpuLoad(8, SEG_CS, Ip + i, out uint b)) break; sb.Append($"{b:X2} "); }
                e.Code = sb.ToString(); RwReads[k] = e;
            }
            e.N++; if (WsLogging) e.InProof++; e.LastVal = BitConverter.ToUInt32(Mem, (int)w);
            _rwBusy = false;
            return;
        }
    }
    static void RwDump()
    {
        foreach (var kv in RwReads.OrderByDescending(k => k.Value.N))
            Console.Error.WriteLine($"RWATCH addr {kv.Value.Addr:X8} cs {kv.Key.Item1:X4} eip {kv.Key.Item2:X8} n {kv.Value.N} inProof {kv.Value.InProof} last {kv.Value.LastVal:X8} firstCycle {kv.Value.FirstCycle} code {kv.Value.Code}");
    }
}
