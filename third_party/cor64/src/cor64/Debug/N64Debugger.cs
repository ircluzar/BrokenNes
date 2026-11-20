// [dbg_exec] IDebugExecution + IDebugTrace over an N64Session. Hooks: DebugHooks.Active is tested once per
// N64System.TickBatch (the block dispatcher's entry, above the IL/x64 block runner, whose codegen is untouched) and
// once per RSP task start. While Active, TickBatch runs DebugTickBatch below instead: the CPU is stepped through the
// interpreter one instruction at a time (exact breakpoints, steps, trace, call stack), then the RSP is stepped one
// instruction at a time for the same credit, then VI/AI are ticked by the CPU steps done. Idle and poll
// fast-forward are off while Active. Timing in debug mode equals batch stepping with no fast paths, but a stop mid-batch
// ends the batch early, so debug-mode runs are not bit-exact with normal runs (normal runs are unchanged).
using System;
using System.Collections.Generic;
using cor64.Mips.R4300I;
using cor64.Mips.Rsp;
using cor64.Session;

namespace cor64.Debug
{
    public static class DebugHooks
    {
        /// <summary>True while an N64Debugger is enabled. Tested at batch boundaries and RSP task start only.</summary>
        public static bool Active;
        public static N64Debugger Current;
        /// <summary>True while the enabled debugger is paused (N64Session.RunFrame returns early).</summary>
        public static bool Paused => Active && Current != null && Current.IsPaused;
        internal static void RspTaskStart(RspInterpreter rsp, bool hle) => Current?.OnRspTaskStart(rsp, hle);
    }

    public sealed class N64Debugger : IDebugExecution, IDebugTrace
    {
        readonly N64Session m_Session;
        readonly N64DebugTarget m_Target;
        readonly List<Breakpoint> m_Bps = new List<Breakpoint>();
        readonly Dictionary<uint, List<Breakpoint>> m_CpuExec = new Dictionary<uint, List<Breakpoint>>();
        readonly Dictionary<uint, List<Breakpoint>> m_RspExec = new Dictionary<uint, List<Breakpoint>>();
        readonly List<CallFrame> m_Stack = new List<CallFrame>();
        int m_NextBp = 1;
        bool m_Enabled, m_Paused;
        // skip the exec breakpoint check for the first instruction after a resume (so a resume leaves the breakpoint)
        bool m_SkipCpuBp, m_SkipRspBp;
        int m_RspOwed; // RSP steps owed from a batch cut short by an RSP stop

        // run mode
        enum Mode { Run, StepCpu, StepRsp, Until, Over }
        Mode m_Mode = Mode.Run;
        long m_Remain; uint m_UntilPc; string m_UntilCpu; int m_OverDepth;
        long m_CpuStepsThisRun;
        bool m_WatchHit; int m_WatchBpId;

        // trace
        TraceEntry[] m_CpuRing, m_RspRing; int m_CpuRingPos, m_RspRingPos; long m_CpuRingN, m_RspRingN;
        readonly ulong[] m_Gpr = new ulong[32];
        // task log
        List<RspTaskRecord> m_Tasks; int m_TaskMax; long m_TaskIndex;

        public StopInfo LastStop { get; private set; }
        public N64DebugTarget Target => m_Target;
        public const int MaxCallDepth = 256;

        public N64Debugger(N64Session session, N64DebugTarget target = null)
        {
            m_Session = session ?? throw new ArgumentNullException(nameof(session));
            m_Target = target ?? new N64DebugTarget(session);
            m_Target.WatchSet.OnHit += OnWatchHit;
        }

        N64System Sys => m_Session.System;
        Interpreter Cpu => Sys.DeviceCPU as Interpreter;
        RspInterpreter Rsp => Sys.DeviceRcp.DeviceRsp as RspInterpreter;

        public bool Enabled
        {
            get => m_Enabled;
            set
            {
                if (value == m_Enabled) return;
                m_Enabled = value;
                if (value) { DebugHooks.Current = this; DebugHooks.Active = true; m_Stack.Clear(); }
                else
                {
                    m_Paused = false; m_Mode = Mode.Run;
                    if (DebugHooks.Current == this) { DebugHooks.Active = false; DebugHooks.Current = null; }
                    m_Stack.Clear();
                }
            }
        }

        public bool IsPaused => m_Paused;
        public void Pause() { Enabled = true; m_Paused = true; LastStop = new StopInfo(StopReason.Paused, "cpu", CpuPc(), 0, 0); }
        public void Resume() { Enabled = true; m_Mode = Mode.Run; Unpause(); }

        void Unpause() { if (m_Paused) { m_SkipCpuBp = true; m_SkipRspBp = true; } m_Paused = false; m_WatchHit = false; m_CpuStepsThisRun = 0; }

        StopInfo Drive(int maxFrames)
        {
            Unpause();
            for (int f = 0; f < maxFrames && !m_Paused; f++) m_Session.RunFrame();
            if (!m_Paused)
            {
                m_Paused = true; m_Mode = Mode.Run;
                LastStop = new StopInfo(StopReason.FrameLimit, "cpu", CpuPc(), 0, m_CpuStepsThisRun);
            }
            return LastStop;
        }

        public StopInfo Step(string cpu, int n = 1, int maxFrames = 600)
        {
            if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
            Enabled = true;
            m_Mode = cpu == "rsp" ? Mode.StepRsp : Mode.StepCpu; m_Remain = n;
            return Drive(maxFrames);
        }

        public StopInfo StepOver(int maxFrames = 600)
        {
            Enabled = true;
            ulong pc = CpuPc();
            if (!IsCall(ReadCpuWord(pc))) return Step("cpu", 1, maxFrames);
            m_Mode = Mode.Over; m_UntilPc = (uint)pc + 8; m_UntilCpu = "cpu"; m_OverDepth = m_Stack.Count;
            return Drive(maxFrames);
        }

        public StopInfo RunUntil(string cpu, ulong pc, int maxFrames = 600)
        {
            Enabled = true;
            m_Mode = Mode.Until; m_UntilCpu = cpu == "rsp" ? "rsp" : "cpu";
            m_UntilPc = cpu == "rsp" ? (uint)pc & 0xFFC : (uint)pc;
            return Drive(maxFrames);
        }

        public StopInfo Continue(int maxFrames = 600) { Enabled = true; m_Mode = Mode.Run; return Drive(maxFrames); }

        // ---------------- breakpoints ----------------
        public int AddBreakpoint(string cpu, BreakpointKind kind, ulong address, string condition = null, int length = 4,
            WatchCondition valueCondition = WatchCondition.Any, ulong value = 0, ulong mask = ulong.MaxValue)
        {
            cpu = cpu == "rsp" ? "rsp" : "cpu";
            var bp = new Breakpoint { Cpu = cpu, Kind = kind, Address = address, Condition = condition, Length = length, ValueCondition = valueCondition, Value = value, Mask = mask };
            bp.CompiledCondition = condition == null ? null : CompileCondition(cpu, condition);
            if (kind == BreakpointKind.Exec)
            {
                var map = cpu == "rsp" ? m_RspExec : m_CpuExec;
                uint key = cpu == "rsp" ? (uint)address & 0xFFC : (uint)address;
                bp.Address = key;
                if (!map.TryGetValue(key, out var l)) map[key] = l = new List<Breakpoint>();
                l.Add(bp);
            }
            else
            {
                if (cpu != "cpu" || !N64MemoryController.s_DbgWatch) return -1;
                int wid = m_Target.AddWatch(address, length, kind == BreakpointKind.Read ? WatchKind.Read : WatchKind.Write, valueCondition, value, mask);
                if (wid < 0) return -1;
                bp.WatchId = wid;
            }
            bp.Id = m_NextBp++;
            m_Bps.Add(bp);
            Enabled = true;
            return bp.Id;
        }

        public bool RemoveBreakpoint(int id)
        {
            var bp = m_Bps.Find(b => b.Id == id);
            if (bp == null) return false;
            m_Bps.Remove(bp);
            if (bp.Kind == BreakpointKind.Exec)
            {
                var map = bp.Cpu == "rsp" ? m_RspExec : m_CpuExec;
                if (map.TryGetValue((uint)bp.Address, out var l)) { l.Remove(bp); if (l.Count == 0) map.Remove((uint)bp.Address); }
            }
            else m_Target.RemoveWatch(bp.WatchId);
            return true;
        }

        public IReadOnlyList<Breakpoint> ListBreakpoints() => m_Bps.ToArray();

        void OnWatchHit(WatchHit h)
        {
            if (!m_Enabled) return;
            var bp = m_Bps.Find(b => b.WatchId == h.WatchId);
            if (bp == null || !bp.Enabled) return;
            if (bp.CompiledCondition != null && !bp.CompiledCondition()) return;
            bp.HitCount++;
            m_WatchHit = true; m_WatchBpId = bp.Id;
        }

        // condition: "lhs op rhs [&& lhs op rhs ...]"; operands are register names or numbers (0x.. hex)
        Func<bool> CompileCondition(string cpu, string cond)
        {
            var parts = cond.Split(new[] { "&&" }, StringSplitOptions.RemoveEmptyEntries);
            var tests = new List<Func<bool>>();
            string[] ops = { "==", "!=", "<=", ">=", "<", ">" };
            foreach (var raw in parts)
            {
                string p = raw.Trim(); string op = null; int at = -1;
                foreach (var o in ops) { at = p.IndexOf(o, StringComparison.Ordinal); if (at > 0) { op = o; break; } }
                if (op == null) throw new FormatException("condition needs ==, !=, <, >, <= or >=: " + p);
                var l = Operand(cpu, p.Substring(0, at).Trim()); var r = Operand(cpu, p.Substring(at + op.Length).Trim());
                tests.Add(op switch
                {
                    "==" => () => l() == r(), "!=" => () => l() != r(), "<=" => () => l() <= r(),
                    ">=" => () => l() >= r(), "<" => () => l() < r(), _ => (Func<bool>)(() => l() > r()),
                });
            }
            return () => { foreach (var t in tests) if (!t()) return false; return true; };
        }

        Func<ulong> Operand(string cpu, string s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { ulong v = Convert.ToUInt64(s.Substring(2), 16); return () => v; }
            if (ulong.TryParse(s, out var d)) return () => d;
            if (long.TryParse(s, out var neg)) return () => (ulong)neg;
            int gi = Array.FindIndex(MipsNames.Gpr, g => string.Equals(g, s, StringComparison.OrdinalIgnoreCase));
            if (gi >= 0)
            {
                if (cpu == "rsp") return () => Rsp.ReadGPR(gi);
                return () => Sys.DeviceCPU.StateR4000I.GetGpr64(gi);
            }
            if (!m_Target.TryGetRegister(cpu, s, out _)) throw new FormatException("unknown register: " + s);
            return () => m_Target.TryGetRegister(cpu, s, out var v) ? v : 0;
        }

        bool ExecHit(Dictionary<uint, List<Breakpoint>> map, uint key, out int id)
        {
            id = 0;
            if (!map.TryGetValue(key, out var l)) return false;
            foreach (var bp in l)
            {
                if (!bp.Enabled) continue;
                if (bp.CompiledCondition != null && !bp.CompiledCondition()) continue;
                bp.HitCount++; id = bp.Id; return true;
            }
            return false;
        }

        // ---------------- call stack ----------------
        public IReadOnlyList<CallFrame> CallStack(string cpu = "cpu")
        {
            if (cpu != "cpu") return Array.Empty<CallFrame>();
            var syms = m_Target.Symbols;
            var r = new CallFrame[m_Stack.Count];
            for (int i = 0; i < r.Length; i++)
            {
                var f = m_Stack[i];
                r[i] = new CallFrame(f.CallSite, f.Target, f.Return, f.Sp, syms.Count > 0 ? syms.Label(f.Target) : null);
            }
            return r;
        }

        static bool IsCall(uint w)
        {
            uint op = w >> 26;
            if (op == 3) return true;                                  // jal
            if (op == 0 && (w & 0x3F) == 9) return true;               // jalr
            if (op == 1) { uint rt = (w >> 16) & 0x1F; return rt == 0x10 || rt == 0x11 || rt == 0x12 || rt == 0x13; } // bltzal/bgezal(l)
            return false;
        }

        void TrackCall(ulong pc, uint w, ulong[] gprBefore)
        {
            uint op = w >> 26;
            if (IsCall(w))
            {
                ulong target;
                if (op == 3) target = (ulong)(long)(int)(((uint)pc + 4) & 0xF0000000u | ((w & 0x03FFFFFF) << 2));
                else if (op == 0) target = gprBefore[(w >> 21) & 0x1F];
                else target = (ulong)((long)pc + 4 + ((long)(short)(w & 0xFFFF) << 2));
                if (m_Stack.Count >= MaxCallDepth) m_Stack.RemoveAt(0);
                m_Stack.Add(new CallFrame(pc, target, (ulong)((long)pc + 8), gprBefore[29], null));
            }
            else if (op == 0 && (w & 0x3F) == 8 && ((w >> 21) & 0x1F) == 31) // jr ra
            {
                uint ra = (uint)gprBefore[31];
                for (int i = m_Stack.Count - 1; i >= 0; i--)
                    if ((uint)m_Stack[i].Return == ra) { m_Stack.RemoveRange(i, m_Stack.Count - i); return; }
            }
        }

        // ---------------- trace ----------------
        public void StartTrace(string cpu, int ringSize = 4096)
        {
            if (ringSize <= 0) throw new ArgumentOutOfRangeException(nameof(ringSize));
            Enabled = true;
            if (cpu == "rsp") { m_RspRing = new TraceEntry[ringSize]; m_RspRingPos = 0; m_RspRingN = 0; }
            else { m_CpuRing = new TraceEntry[ringSize]; m_CpuRingPos = 0; m_CpuRingN = 0; }
        }

        public IReadOnlyList<TraceEntry> StopTrace(string cpu)
        {
            bool rsp = cpu == "rsp";
            var ring = rsp ? m_RspRing : m_CpuRing; long n = rsp ? m_RspRingN : m_CpuRingN; int pos = rsp ? m_RspRingPos : m_CpuRingPos;
            if (rsp) m_RspRing = null; else m_CpuRing = null;
            if (ring == null) return Array.Empty<TraceEntry>();
            int cnt = (int)Math.Min(n, ring.Length);
            var r = new TraceEntry[cnt];
            int start = n > ring.Length ? pos : 0;
            for (int i = 0; i < cnt; i++) r[i] = ring[(start + i) % ring.Length];
            return r;
        }

        public IReadOnlyList<TraceEntry> TraceFrame(string cpu, int ringSize = 1 << 20)
        {
            StartTrace(cpu, ringSize);
            bool wasPaused = m_Paused; Unpause();
            m_Session.RunFrame();
            if (wasPaused && !m_Paused) m_Paused = true;
            return StopTrace(cpu);
        }

        static void Push(TraceEntry[] ring, ref int pos, ref long n, in TraceEntry e)
        {
            ring[pos] = e; pos++; if (pos == ring.Length) pos = 0; n++;
        }

        static readonly (int, ulong)[] s_NoDelta = Array.Empty<(int, ulong)>();

        // ---------------- task log ----------------
        public void StartTaskLog(int max = 64) { m_Tasks = new List<RspTaskRecord>(); m_TaskMax = Math.Max(1, max); Enabled = true; }
        public IReadOnlyList<RspTaskRecord> StopTaskLog() { var t = m_Tasks ?? new List<RspTaskRecord>(); m_Tasks = null; return t; }
        public IReadOnlyList<RspTaskRecord> TaskLog => (IReadOnlyList<RspTaskRecord>)m_Tasks ?? Array.Empty<RspTaskRecord>();

        internal void OnRspTaskStart(RspInterpreter rsp, bool hle)
        {
            if (!m_Enabled) return;
            m_TaskIndex++;
            if (m_Tasks == null) return;
            var bus = Sys.SystemMemory;
            uint D(uint o) => (uint)bus.DebugPeek(0x04000000 + o, 4);
            var t = new RspTaskRecord
            {
                Index = m_TaskIndex, Hle = hle, CpuTicks = Sys.Ticks,
                Type = D(0xFC0), Flags = D(0xFC4), Ucode = D(0xFD0), UcodeSize = D(0xFD4), UcodeData = D(0xFD8), UcodeDataSize = D(0xFDC),
                Dmem = new byte[0x1000],
            };
            for (uint a = 0; a < 0x1000; a += 4) { uint w = D(a); t.Dmem[a] = (byte)(w >> 24); t.Dmem[a + 1] = (byte)(w >> 16); t.Dmem[a + 2] = (byte)(w >> 8); t.Dmem[a + 3] = (byte)w; }
            uint R(uint a) => (uint)bus.DebugPeek(a, 4);
            t.Task = OSTaskInfo.Decode(i => D(0xFC0 + (uint)i * 4)); // [dbg_rsp] crc + ucode id shared with UcodeTable
            t.UcodeCrc = UcodeTable.TextCrc(t.Task, R);
            t.UcodeId = UcodeTable.Identify(t.Task, t.UcodeCrc, R);
            m_Tasks.Add(t);
            if (m_Tasks.Count > m_TaskMax) m_Tasks.RemoveAt(0);
        }

        // ---------------- execution ----------------
        ulong CpuPc() => (ulong)(long)(int)(uint)Sys.DeviceCPU.ReadPC();

        uint ReadCpuWord(ulong pc)
        {
            if (!m_Target.TryTranslate(pc, out uint p)) return 0;
            return (uint)Sys.SystemMemory.DebugPeek(p, 4);
        }

        void Stop(StopReason r, string cpu, ulong pc, int bp)
        {
            m_Paused = true; m_Mode = Mode.Run;
            LastStop = new StopInfo(r, cpu, pc, bp, m_CpuStepsThisRun);
        }

        /// <summary>Called by N64System.TickBatch while DebugHooks.Active: at most n CPU steps, exact stops.</summary>
        internal void TickBatch(N64System sys, int n)
        {
            if (m_Paused) return;
            var cpu = sys.DeviceCPU;
            var st = cpu.StateR4000I;
            int done = 0;
            for (; done < n; done++)
            {
                ulong pc = (ulong)(long)(int)(uint)cpu.ReadPC(); // sign-extended, as the GPRs hold it
                uint key = (uint)pc;
                if (!m_SkipCpuBp)
                {
                    if (m_CpuExec.Count != 0 && ExecHit(m_CpuExec, key, out int id)) { Stop(StopReason.Breakpoint, "cpu", pc, id); break; }
                    if (m_Mode == Mode.Until && m_UntilCpu == "cpu" && key == m_UntilPc) { Stop(StopReason.RunUntil, "cpu", pc, 0); break; }
                    if (m_Mode == Mode.Over && key == m_UntilPc && m_Stack.Count <= m_OverDepth) { Stop(StopReason.StepOver, "cpu", pc, 0); break; }
                }
                m_SkipCpuBp = false;
                uint w = ReadCpuWord(pc);
                for (int r = 0; r < 32; r++) m_Gpr[r] = st.GetGpr64(r);
                cpu.Step();
                m_CpuStepsThisRun++;
                TrackCall(pc, w, m_Gpr);
                if (m_CpuRing != null)
                {
                    List<(int, ulong)> d = null;
                    for (int r = 1; r < 32; r++) { ulong v = st.GetGpr64(r); if (v != m_Gpr[r]) (d ??= new List<(int, ulong)>()).Add((r, v)); }
                    Push(m_CpuRing, ref m_CpuRingPos, ref m_CpuRingN, new TraceEntry("cpu", pc, w, d?.ToArray() ?? s_NoDelta));
                }
                if (m_WatchHit) { m_WatchHit = false; done++; Stop(StopReason.Watch, "cpu", CpuPc(), m_WatchBpId); break; }
                if (m_Mode == Mode.StepCpu && --m_Remain <= 0) { done++; Stop(StopReason.Step, "cpu", CpuPc(), 0); break; }
            }
            if (done > 0 || m_RspOwed > 0) RunRsp(sys, done * RspInterpreter.CreditScale + m_RspOwed);
            sys.Ticks += done; Telemetry.Ticks += done;
            if (done > 0) { sys.DeviceRcp.VideoInterface.Tick(done); sys.DeviceRcp.AudioInterface.Tick(done); }
        }

        void RunRsp(N64System sys, int steps)
        {
            m_RspOwed = 0;
            var rsp = Rsp; bool cpuStopped = m_Paused;
            if (rsp == null) { for (int i = 0; i < steps; i++) sys.DeviceRcp.DeviceRsp.Step(); return; }
            for (int i = 0; i < steps; i++)
            {
                if (rsp.IsHalted) return;
                uint pc = (uint)rsp.ReadPC() & 0xFFC;
                if (!m_SkipRspBp && !cpuStopped)
                {
                    if (m_RspExec.Count != 0 && ExecHit(m_RspExec, pc, out int id)) { Stop(StopReason.Breakpoint, "rsp", pc, id); m_RspOwed = steps - i; return; }
                    if (m_Mode == Mode.Until && m_UntilCpu == "rsp" && pc == m_UntilPc) { Stop(StopReason.RunUntil, "rsp", pc, 0); m_RspOwed = steps - i; return; }
                }
                m_SkipRspBp = false;
                uint w = 0;
                if (m_RspRing != null) { w = (uint)sys.SystemMemory.DebugPeek(0x04001000 + pc, 4); for (int r = 0; r < 32; r++) m_Gpr[r] = rsp.ReadGPR(r); }
                rsp.Step();
                if (m_RspRing != null)
                {
                    List<(int, ulong)> d = null;
                    for (int r = 1; r < 32; r++) { ulong v = rsp.ReadGPR(r); if (v != m_Gpr[r]) (d ??= new List<(int, ulong)>()).Add((r, v)); }
                    Push(m_RspRing, ref m_RspRingPos, ref m_RspRingN, new TraceEntry("rsp", pc, w, d?.ToArray() ?? s_NoDelta));
                }
                if (!cpuStopped && m_Mode == Mode.StepRsp && --m_Remain <= 0) { Stop(StopReason.Step, "rsp", (ulong)rsp.ReadPC() & 0xFFC, 0); m_RspOwed = steps - i - 1; return; }
            }
        }
    }
}
