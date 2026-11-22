using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NesEmulator.X86;

// X86_WAITSKIP (lever 2 of the Pentium II speed work, 2026-11-21; off by default): the wait-loop skip.
//   1 = park on no-change passes, 2 = also the counter skip (2026-11-22, user: "Yes, build it (off by default)").
// A game's main loop that only polls brings the machine back to the same CPU state at the end of each pass. Generic proof, no game list:
//   at a batch start in ring 3 (IF set) the CPU state is taken as the candidate C (everything a save state holds but the cycle count);
//   the batches then run in the interpreter with every RAM write logged per 64-byte line (the code-line hook, _codeMark swapped for an
//   all-ones map) and the I/O ports / device windows behind a recording bus. The first return to C ends pass A: its lines are copied.
//   The next return to C ends pass B: when B wrote only lines A wrote and all of those hold what they held at the end of A, pass B
//   changed nothing at all -> park at C like HLT until an interrupt (or a device write to RAM).
// Counter skip (mode 2): StarCraft's idle pass instead adds a constant to a few dwords (a tick, stamps equal to it, a 100-pass counter).
//   Pass B's per-dword change d (at most 64 dwords) must repeat exactly in pass C (same pass length). Then pass D runs from C with every
//   such dword moved on by K*d first (K from 1024 down, then bisected), with an undo log of every RAM line it writes: D must again end
//   at C having added exactly d (same control flow K passes later; a threshold like the 100-pass counter makes it differ -> smaller K).
//   A failed D is rolled back (lines + CPU state; the time it took stays spent), a good D has its K*d taken back off (= one normal pass).
//   Then the CPU parks for at most K passes of guest time; on unpark the dwords move on by (idle instructions / pass length) * d.
//   This is not equal to a run without the skip (interrupts then arrive at a pass boundary), so parity is skip-on vs skip-on.
// Any port or device-window access, RDTSC / RDPMC, a delivered interrupt, HLT, a device write to RAM or a budget overrun ends a proof.
// Deterministic (decisions depend on guest state only; the proof is interpreted with and without the JIT) and save-state exact
// (the mode and the whole detector state are the "WAIT" block, written whenever the mode is on).
public sealed partial class I386
{
    public static readonly int WaitSkipEnv = int.TryParse(Environment.GetEnvironmentVariable("X86_WAITSKIP"), out int m) && m is >= 0 and <= 2 ? m : 0;
    public static bool WaitSkipOn => WaitSkipEnv != 0;
    const int WsBudget = 20000, WsMaxGap = 1024, WsRetries = 4, WsKMax = 1024, WsMaxAff = 64, WsKTriesMax = 12, WsDBudget = 400000;

    int _wsMode = WaitSkipEnv;
    /// <summary>0 off, 1 park, 2 park + counter skip. Kept in save states ("WAIT"); a state without the block takes X86_WAITSKIP.</summary>
    public int WaitSkipMode { get => _wsMode; set { if (_wsMode != value) { WsReset(); _wsMode = value; } } }

    int _wsPhase;            // 0 rest, 1 pass A, 2 pass B, 3 parked, 4 pass C (counter check), 5 pass D (offset test, undo on)
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
    // counter skip
    readonly List<uint> _wsAffA = new(), _wsAffD = new();
    readonly Dictionary<uint, uint> _wsAff = new();
    int _wsLen, _wsPassStart, _wsK, _wsKOk, _wsKBad, _wsKTries;
    long _wsParkCap, _wsParkAcc;     // parked: passes allowed (0 = no cap, a pure park), idle instructions so far
    bool _wsUndoOn;
    readonly List<uint> _wsUndoL = new();
    readonly HashSet<uint> _wsUndoS = new();
    byte[] _wsUndoBuf = new byte[64 * 64];

    public static long WsProofs, WsParks, WsParkedBatches, WsSkippedInstr, WsCountParks, WsSkippedPasses, WsDTests, WsDGood;
    public static readonly long[] WsFails = new long[11];   // 1 io/mmio, 2 tsc/pmc, 3 irq, 4 budget, 5 halt, 6 retries, 7 other, 8 affine, 9 kfail, 10 extern
    static readonly string[] WsFailName = { "", "io", "tsc", "irq", "budget", "halt", "retries", "other", "affine", "kfail", "extern" };
    static bool _wsDumpHooked;

    public bool WsParked => _wsPhase == 3;
    bool WsLogging => _wsPhase is 1 or 2 or 4 or 5;

    /// <summary>PcMachine, a parked CPU with an interrupt to take (or at its pass cap): back to running at C, the counters moved on.</summary>
    public void WsUnpark()
    {
        if (_wsPhase != 3) return;
        WsApply();
        _wsPhase = 0; _wsGap = 1; _wsWait = 0;
    }
    public void WsParkedBatch(long idleInstr)
    {
        WsParkedBatches++; WsSkippedInstr += idleInstr; _wsParkAcc += idleInstr;
        if (_wsParkCap != 0 && _wsParkAcc >= _wsParkCap * _wsLen) WsUnpark();
    }
    /// <summary>Parked: guest instructions this park may still idle (a counter park ends after its K passes).</summary>
    public long WsParkLeft => _wsPhase != 3 || _wsParkCap == 0 ? long.MaxValue : Math.Max(1, _wsParkCap * _wsLen - _wsParkAcc);

    void WsApply()
    {
        if (_wsParkCap != 0 && _wsLen > 0)
        {
            long n = Math.Min(_wsParkAcc / _wsLen, _wsParkCap);
            if (n > 0)
            {
                for (int i = 0; i < _wsAffA.Count; i++) { uint a = _wsAffA[i]; PS32(a, unchecked(P32(a) + (uint)n * _wsAffD[i])); }
                WsSkippedPasses += n;
            }
        }
        _wsParkCap = 0; _wsParkAcc = 0; WsAffClear();
    }

    void WsAffClear() { _wsAffA.Clear(); _wsAffD.Clear(); _wsAff.Clear(); }

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

    /// <summary>Back to the CPU state of a signature (pass D rolled back); Cycle and the pending-interrupt line stay as they are.</summary>
    void WsRestoreSig(byte[] sig)
    {
        bool intr = Intr;
        using var r = new BinaryReader(new MemoryStream(sig));
        for (int i = 0; i < 8; i++) R[i] = r.ReadUInt32();
        Ip = r.ReadUInt32(); NextIp = r.ReadUInt32(); Flags = r.ReadUInt32(); Cpl = r.ReadInt32(); Code16 = r.ReadBoolean(); SpMask = r.ReadUInt32(); Halt = r.ReadBoolean();
        SegGen++;
        for (int i = 0; i < 8; i++) { SegSel[i] = r.ReadUInt32(); SegBase[i] = r.ReadUInt32(); SegLimit[i] = r.ReadUInt32(); SegFlags[i] = r.ReadUInt32(); }
        IdtBase = r.ReadUInt32(); IdtLimit = r.ReadUInt32(); GdtBase = r.ReadUInt32(); GdtLimit = r.ReadUInt32();
        Cr0 = r.ReadUInt32(); Cr2 = r.ReadUInt32(); Cr3 = r.ReadUInt32();
        for (int i = 0; i < 8; i++) Dr[i] = r.ReadUInt32();
        Excno = r.ReadInt32(); Excerr = r.ReadUInt32(); r.ReadBoolean();
        _ccOp = r.ReadInt32(); _ccDst = r.ReadUInt32(); _ccDst2 = r.ReadUInt32(); _ccSrc1 = r.ReadUInt32(); _ccSrc2 = r.ReadUInt32(); _ccMask = r.ReadUInt32();
        if (!_legacy) LoadModelState(r);
        Intr = intr;
        TlbClear();
        _ifLaddr = ulong.MaxValue; _ifPaddr = 0;
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

    /// <summary>Pass D: a store is about to change [a, a+n): keep each line's old bytes once (the undo log).</summary>
    void WsPre(uint a, uint n)
    {
        if (a >= MemSize || n == 0) return;
        uint e = Math.Min(a + n - 1, MemSize - 1);
        for (uint l = a >> 6; l <= e >> 6; l++)
        {
            if (!_wsUndoS.Add(l)) continue;
            int i = _wsUndoL.Count; _wsUndoL.Add(l);
            if (_wsUndoBuf.Length < (i + 1) * 64) Array.Resize(ref _wsUndoBuf, _wsUndoBuf.Length * 2);
            Buffer.BlockCopy(Mem, (int)(l << 6), _wsUndoBuf, i * 64, 64);
        }
    }

    /// <summary>RAM changed from outside the CPU (DMA, BIOS, a tool): a proof is void, a park ends; pass D keeps those bytes through its rollback.</summary>
    void WsExternal(uint a, int n)
    {
        if (_wsPhase == 3) { WsUnpark(); return; }
        if (_wsPhase == 5 && n > 0)
            for (int i = 0; i < _wsUndoL.Count; i++)
            {
                uint lb = _wsUndoL[i] << 6, s = Math.Max(lb, a), e = Math.Min(lb + 64, a + (uint)n);
                if (s < e) Buffer.BlockCopy(Mem, (int)s, _wsUndoBuf, i * 64 + (int)(s - lb), (int)(e - s));
            }
        _wsFail = 10;
    }

    void WsFailNow(int why)
    {
        if (_wsPhase == 5) WsDRollback();
        WsLogOff();
        WsFails[why]++;
        _wsPhase = 0;
        _wsGap = Math.Min(_wsGap * 2, WsMaxGap); _wsWait = _wsGap;
        WsClear(); WsAffClear();
    }

    void WsClear() { _wsLinesA.Clear(); _wsSetA.Clear(); _wsLinesB.Clear(); _wsSetB.Clear(); _wsFail = 0; }
    void WsClearB() { _wsLinesB.Clear(); _wsSetB.Clear(); }

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

    /// <summary>End of pass B: the dwords it changed, each by a constant to be checked in pass C.</summary>
    bool WsAffineFind()
    {
        if (_legacy) return false;   // the rollback needs the FPU in the signature
        foreach (uint l in _wsLinesB) if (!_wsSetA.Contains(l)) return false;
        WsAffClear();
        for (int i = 0; i < _wsLinesA.Count; i++)
        {
            int b = (int)(_wsLinesA[i] << 6);
            for (int o = 0; o < 64; o += 4)
            {
                uint d = unchecked(BitConverter.ToUInt32(Mem, b + o) - BitConverter.ToUInt32(_wsSnap, i * 64 + o));
                if (d == 0) continue;
                if (_wsAffA.Count >= WsMaxAff) { WsAffClear(); return false; }
                _wsAffA.Add((uint)(b + o)); _wsAffD.Add(d); _wsAff[(uint)(b + o)] = d;
            }
        }
        return _wsAffA.Count > 0;
    }

    /// <summary>End of pass C / D: every logged dword changed by exactly its d (0 for the others), no new lines.</summary>
    bool WsAffineOk()
    {
        foreach (uint l in _wsLinesB) if (!_wsSetA.Contains(l)) return false;
        for (int i = 0; i < _wsLinesA.Count; i++)
        {
            int b = (int)(_wsLinesA[i] << 6);
            for (int o = 0; o < 64; o += 4)
            {
                uint d = unchecked(BitConverter.ToUInt32(Mem, b + o) - BitConverter.ToUInt32(_wsSnap, i * 64 + o));
                uint want = _wsAff.TryGetValue((uint)(b + o), out uint x) ? x : 0;
                if (d != want) return false;
            }
        }
        return true;
    }

    /// <summary>Start pass D at C: the counters K passes on (undo log on), the snapshot taken after that.</summary>
    void WsDStart()
    {
        WsDTests++;
        _wsUndoL.Clear(); _wsUndoS.Clear(); _wsUndoOn = true;
        for (int i = 0; i < _wsAffA.Count; i++) { uint a = _wsAffA[i]; PS32(a, unchecked(P32(a) + (uint)_wsK * _wsAffD[i])); }
        WsSnapA(); WsClearB();
        _wsPhase = 5; _wsPassStart = _wsLeft;
    }

    /// <summary>Pass D did not repeat: every line it wrote back as it was, the CPU back at C (the time it took stays spent).</summary>
    void WsDRollback()
    {
        _wsUndoOn = false;
        for (int i = 0; i < _wsUndoL.Count; i++)
        {
            uint l = _wsUndoL[i];
            Buffer.BlockCopy(_wsUndoBuf, i * 64, Mem, (int)(l << 6), 64);
            _pgGen[l >> 6]++;
        }
        _wsUndoL.Clear(); _wsUndoS.Clear();
        WsRestoreSig(_wsSig);
        _wsFail = 0; _wsKBad = _wsK;
        WsClearB(); _wsPassStart = _wsLeft;
    }

    /// <summary>Pass D repeated K passes on: take the K*d back off (the state is one normal pass after C).</summary>
    void WsDGoodEnd()
    {
        WsDGood++;
        _wsUndoOn = false; _wsUndoL.Clear(); _wsUndoS.Clear();
        for (int i = 0; i < _wsAffA.Count; i++) { uint a = _wsAffA[i]; PS32(a, unchecked(P32(a) - (uint)_wsK * _wsAffD[i])); }
        _wsKOk = _wsK;
        WsClearB(); _wsPassStart = _wsLeft;
    }

    /// <summary>After a pass D: park, give up, or try the next K. True = the proof is over (parked or failed).</summary>
    bool WsDNext()
    {
        _wsKTries++;
        bool close = _wsKBad != 0 && _wsKBad - _wsKOk <= Math.Max(1, _wsKOk / 8);
        if (_wsKOk > 0 && (_wsKBad == 0 || close || _wsKTries >= WsKTriesMax)) { WsPark(_wsKOk); return true; }
        if (_wsKOk == 0 && (_wsK <= 2 || _wsKTries >= WsKTriesMax)) { WsFailNow(9); return true; }
        _wsK = _wsKOk == 0 ? _wsK / 2 : (_wsKOk + _wsKBad) / 2;
        WsDStart();
        return false;
    }

    void WsPark(long cap)
    {
        WsLogOff(); WsClear();
        _wsPhase = 3; _wsParkCap = cap; _wsParkAcc = 0;
        WsParks++; if (cap > 0) WsCountParks++; else WsAffClear();
    }

    /// <summary>Step: a pending interrupt is about to be taken (before CallIsr).</summary>
    void WsOnIrq() { if (WsLogging) WsFailNow(3); else if (_wsPhase == 3) WsUnpark(); }

    /// <summary>Step, after the interrupt: true = this batch runs in the proof (WsExec).</summary>
    bool WsBegin()
    {
        if (_wsPhase == 3) WsUnpark();   // someone stepped a parked CPU: it simply runs on
        if (_wsPhase == 0)
        {
            if (_wsWait > 0) { _wsWait--; return false; }
            if (Cpl != 3 || (Flags & IF) == 0 || Halt) { _wsWait = 1; return false; }
            if (!_wsDumpHooked) { _wsDumpHooked = true; AppDomain.CurrentDomain.ProcessExit += (_, _) => WsDump(); }
            WsProofs++;
            WsClear(); WsAffClear();
            _wsIp = NextIp; _wsCs = SegSel[SEG_CS];
            for (int i = 0; i < 8; i++) _wsR[i] = R[i]; _wsR[8] = Flags;
            _wsSig = WsSigNow();
            _wsPhase = 1; _wsLeft = WsBudget; _wsTries = 0; _wsPassStart = _wsLeft;
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
            if (_wsPhase == 5)
            {
                if (_wsFail != 0 || r != OK || Halt) { WsDRollback(); if (WsDNext()) return true; continue; }
                if (--_wsLeft <= 0) { WsDRollback(); if (_wsKOk > 0) WsPark(_wsKOk); else WsFailNow(4); return true; }
            }
            else
            {
                if (_wsFail != 0) { int f = _wsFail; WsFailNow(f); return WsRest(r); }
                if (r == FAIL) return false;          // Step delivers the fault with the log still on, then turns it off
                if (r != OK || Halt) { WsFailNow(5); return true; }
                if (--_wsLeft <= 0) { WsFailNow(4); return WsRest(r); }
            }
            if (NextIp != _wsIp || SegSel[SEG_CS] != _wsCs || !WsQuick()) continue;
            if (!WsSigNow().AsSpan().SequenceEqual(_wsSig)) continue;
            int len = _wsPassStart - _wsLeft; _wsPassStart = _wsLeft;
            switch (_wsPhase)
            {
                case 1:
                    WsSnapA(); WsClearB(); _wsPhase = 2;
                    break;
                case 2:
                    if (WsDiagOn) WsDiagPass(len);
                    if (WsSameAsSnap()) { WsPark(0); return true; }
                    if (_wsMode == 2 && WsAffineFind())
                    {
                        _wsLen = len; WsSnapA(); WsClearB(); _wsPhase = 4;
                        _wsLeft = Math.Max(_wsLeft, WsDBudget); _wsPassStart = _wsLeft;
                        break;
                    }
                    if (++_wsTries > WsRetries) { WsFailNow(6); return WsRest(r); }
                    _wsLinesA.Clear(); _wsSetA.Clear();
                    foreach (uint l in _wsLinesB) { _wsLinesA.Add(l); _wsSetA.Add(l); }
                    WsSnapA(); WsClearB();
                    break;
                case 4:
                    if (len != _wsLen || !WsAffineOk()) { WsFailNow(8); return WsRest(r); }
                    _wsKOk = 0; _wsKBad = 0; _wsKTries = 0; _wsK = WsKMax;
                    WsDStart();
                    break;
                case 5:
                    if (len == _wsLen && WsAffineOk()) WsDGoodEnd(); else WsDRollback();
                    if (WsDNext()) return true;
                    break;
            }
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

    const int WsVersion = 2;

    void WsSave(BinaryWriter w)
    {
        w.Write(-WsVersion); w.Write(_wsMode);
        w.Write(_wsPhase); w.Write(_wsGap); w.Write(_wsWait); w.Write(_wsLeft); w.Write(_wsTries);
        w.Write(_wsIp); w.Write(_wsCs); foreach (uint v in _wsR) w.Write(v);
        w.Write(_wsSig.Length); w.Write(_wsSig);
        w.Write(_wsLinesA.Count); foreach (uint l in _wsLinesA) w.Write(l);
        w.Write(_wsLinesB.Count); foreach (uint l in _wsLinesB) w.Write(l);
        bool snap = _wsPhase is 2 or 4 or 5;
        w.Write(snap ? _wsLinesA.Count * 64 : 0); if (snap) w.Write(_wsSnap, 0, _wsLinesA.Count * 64);
        w.Write(_wsLen); w.Write(_wsPassStart); w.Write(_wsK); w.Write(_wsKOk); w.Write(_wsKBad); w.Write(_wsKTries);
        w.Write(_wsParkCap); w.Write(_wsParkAcc);
        w.Write(_wsAffA.Count); for (int i = 0; i < _wsAffA.Count; i++) { w.Write(_wsAffA[i]); w.Write(_wsAffD[i]); }
        w.Write(_wsUndoOn);
        w.Write(_wsUndoL.Count); foreach (uint l in _wsUndoL) w.Write(l);
        w.Write(_wsUndoBuf, 0, _wsUndoL.Count * 64);
    }

    void WsLoad(BinaryReader r)
    {
        WsLogOff(); WsClear(); WsAffClear();
        int ver = -r.ReadInt32();
        if (ver != WsVersion) throw new InvalidDataException($"x86 state: wait-skip block version {ver}, this build reads {WsVersion}");
        _wsMode = r.ReadInt32();
        _wsPhase = r.ReadInt32(); _wsGap = r.ReadInt32(); _wsWait = r.ReadInt32(); _wsLeft = r.ReadInt32(); _wsTries = r.ReadInt32();
        _wsIp = r.ReadUInt32(); _wsCs = r.ReadUInt32(); for (int i = 0; i < _wsR.Length; i++) _wsR[i] = r.ReadUInt32();
        _wsSig = r.ReadBytes(r.ReadInt32());
        int na = r.ReadInt32(); for (int i = 0; i < na; i++) { uint l = r.ReadUInt32(); _wsLinesA.Add(l); _wsSetA.Add(l); }
        int nb = r.ReadInt32(); for (int i = 0; i < nb; i++) { uint l = r.ReadUInt32(); _wsLinesB.Add(l); _wsSetB.Add(l); }
        int ns = r.ReadInt32(); if (_wsSnap.Length < ns) _wsSnap = new byte[ns]; if (r.Read(_wsSnap, 0, ns) != ns) throw new EndOfStreamException();
        _wsLen = r.ReadInt32(); _wsPassStart = r.ReadInt32(); _wsK = r.ReadInt32(); _wsKOk = r.ReadInt32(); _wsKBad = r.ReadInt32(); _wsKTries = r.ReadInt32();
        _wsParkCap = r.ReadInt64(); _wsParkAcc = r.ReadInt64();
        int nf = r.ReadInt32(); for (int i = 0; i < nf; i++) { uint a = r.ReadUInt32(), d = r.ReadUInt32(); _wsAffA.Add(a); _wsAffD.Add(d); _wsAff[a] = d; }
        _wsUndoOn = r.ReadBoolean();
        _wsUndoL.Clear(); _wsUndoS.Clear();
        int nu = r.ReadInt32(); for (int i = 0; i < nu; i++) { uint l = r.ReadUInt32(); _wsUndoL.Add(l); _wsUndoS.Add(l); }
        if (_wsUndoBuf.Length < nu * 64) _wsUndoBuf = new byte[Math.Max(nu * 64, 64 * 64)];
        if (r.Read(_wsUndoBuf, 0, nu * 64) != nu * 64) throw new EndOfStreamException();
    }

    void WsReset()
    {
        if (_wsPhase == 5) WsDRollback();
        WsLogOff(); WsClear(); WsAffClear();
        _wsUndoOn = false; _wsUndoL.Clear(); _wsUndoS.Clear();
        _wsPhase = 0; _wsGap = 1; _wsWait = 0; _wsParkCap = 0; _wsParkAcc = 0;
    }

    /// <summary>The machine's save state: the "WAIT" block, written whenever the mode is on or the detector is not at rest.</summary>
    public bool WsActive => _wsMode != 0 || _wsPhase != 0 || _wsGap != 1 || _wsWait != 0;
    public void WsSaveBlock(BinaryWriter w) => WsSave(w);
    public void WsLoadBlock(BinaryReader r) => WsLoad(r);
    /// <summary>A state without the block: the detector at rest (nothing to roll back: the state's RAM is already loaded), the mode from X86_WAITSKIP.</summary>
    public void WsResetState() { _wsPhase = 0; WsReset(); _wsMode = WaitSkipEnv; }

    static void WsDump()
    {
        var sb = new System.Text.StringBuilder($"WAITSKIP proofs {WsProofs} parks {WsParks} counterParks {WsCountParks} skippedPasses {WsSkippedPasses} dTests {WsDTests} dGood {WsDGood} parkedBatches {WsParkedBatches} skippedInstr {WsSkippedInstr} fails");
        for (int i = 1; i < WsFails.Length; i++) sb.Append($" {WsFailName[i]}={WsFails[i]}");
        Console.Error.WriteLine(sb.ToString());
        WsDiagDump();
    }

    static readonly bool WsDiagOn = Environment.GetEnvironmentVariable("X86_WSDIAG") == "1";
    sealed class WsD { public long N; public long Delta; public bool Const = true; public uint Ip; public long Same; }
    static readonly Dictionary<uint, WsD> WsDiag = new();
    static readonly Dictionary<uint, long> WsDiagLoops = new();
    static long WsDiagPasses, WsDiagNewLines, WsDiagDiffDw, WsDiagPassLen;

    void WsDiagPass(int len)
    {
        WsDiagPasses++;
        WsDiagPassLen += len;
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
