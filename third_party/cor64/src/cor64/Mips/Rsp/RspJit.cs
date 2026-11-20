// [cer64plus] RSP dynarec, phase 1a: block cache with pre-decoded, call-threaded execution.
//
// A block is the straight-line IMEM sequence from a start PC up to and including the first branch/jump and its delay slot
// (or a BREAK, or MaxBlock instructions, or the end of IMEM). Running a block performs exactly the same Step() sequence
// the interpreter would: each step reproduces ExecuteNextInst/ExecuteInst with fetch and decode replaced by the cached
// DecodedInstruction and handler. So the instruction stream, and with it the whole emulator, stays deterministic and the
// JIT can be checked bit for bit against the interpreter (COR64_RSP_JIT=0 vs 1).
//
// Savestate-safe: blocks hold no architectural state; they are a cache derived from IMEM bytes. The cache is cleared after
// a savestate load (InvalidateDecodeCache) and whenever IMEM changes (generation counter + raw-word validation).
using System;
using cor64.RCP;
using cor64.IO;

namespace cor64.Mips.Rsp
{
    public sealed class RspJitBlock
    {
        public int StartPc;
        public int Length;
        public uint[] Raw;
        public DecodedInstruction[] Insts;
        public Action<DecodedInstruction>[] Handlers;
        public long Generation;
        public long Runs;
        public RspBlockFn Compiled; // phase 2 (RspJitIL.cs), built once the block is hot
        public volatile RspRecFn Rec; // recompiled block (RspRecompiler.cs); published last by the compile worker
        public bool RecTried;
        public uint[] RawHost; // COR64_RSPBE_FASTVAL: Raw as host-order IMEM words
        public RspJitBlock Next; // COR64_RSPBE_LINK: last block that followed this one (still validated before use)
        public long TTicks, TSteps, TRuns; public bool Listed; public volatile bool RecFailed; public string RejWhy; public long IRuns, ISteps, ITicks; public bool IListed; // COR64_RSP_TIMING only
    }

    public partial class RspInterpreter
    {
        public static readonly bool JitEnabled = Environment.GetEnvironmentVariable("COR64_RSP_JIT") != "0"; // on by default; verified bit-identical to the interpreter
        private const int MaxBlock = 64;
        // Up to 4 variants per start address, so blocks of the graphics and audio microcodes (which swap in and out of
        // IMEM every task) stay cached side by side.
        private const int Variants = 4;
        private readonly RspJitBlock[] m_JitBlocks = new RspJitBlock[1024 * Variants];

        public static long JitBlocksCompiled, JitBlockRuns, JitFallbackSteps, RspSteps;
        /// <summary>[session_leak] RSP block steps of this machine (saved in states). RspSteps above is process-wide (stats only):
        /// DPC_CLOCK used it, so a second session in the same process (or a state loaded mid-process) saw a different clock.</summary>
        public long StepsLocal;

        private void JitClear() { Array.Clear(m_JitBlocks); FlatClear(); }

        // [climb4 rsp_glue] COR64_RSPGLUE_FLAT: flat per-IMEM-word table of compiled front blocks. One 32-byte entry holds
        // the generation the block was validated at plus everything the call needs, so the hot loop does one load chain
        // into a small contiguous array instead of block-slot -> block object -> fields (cache misses in the profile).
        // An entry is used only when its generation equals the current IMEM generation: the block was validated against
        // the IMEM bytes at that generation, so running it is exactly what the slow path would run.
        private struct FlatEnt { public long Gen; public RspRecFn Rec; public DecodedInstruction[] Insts; public Action<DecodedInstruction>[] H; }
        [NonSerialized] private readonly FlatEnt[] m_Flat = NewFlat();
        private static FlatEnt[] NewFlat() { var f = new FlatEnt[1024]; for (int i = 0; i < f.Length; i++) f[i].Gen = long.MinValue; return f; }
        private static readonly bool s_GlueFlat = Environment.GetEnvironmentVariable("COR64_RSPGLUE_FLAT") != "0";
        private void FlatClear() { var f = m_Flat; if (f == null) return; for (int i = 0; i < f.Length; i++) f[i] = new FlatEnt { Gen = long.MinValue }; }
        private void FlatFill(RspJitBlock c, RspRecFn f, long gen) { ref var e = ref m_Flat[c.StartPc >> 2]; e.Rec = f; e.Insts = c.Insts; e.H = c.Handlers; e.Gen = gen; }

        public override void InvalidateDecodeCache() { base.InvalidateDecodeCache(); JitClear(); }

        private static bool EndsBlock(in DecodedInstruction d)
        {
            var fam = d.Op.Family;
            return fam == OperationFamily.Branch || d.Op.Op == "break";
        }

        private RspJitBlock JitCompile(int pc)
        {
            int start = pc;
            var raws = new System.Collections.Generic.List<uint>();
            var insts = new System.Collections.Generic.List<DecodedInstruction>();
            var hs = new System.Collections.Generic.List<Action<DecodedInstruction>>();
            int delay = -1;
            while (raws.Count < MaxBlock && pc < 0x1000) {
                uint raw = FetchInstruction(pc);
                var d = Disassembler.Decode(raw);
                if (d.IsInvalid) break;
                var h = CallTable[d];
                if (h == null) break;
                raws.Add(raw); insts.Add(d); hs.Add(h);
                pc += 4;
                if (delay >= 0) break;                 // just added the delay slot
                if (d.Op.Op == "break") break;
                if (d.Op.Family == OperationFamily.Branch) delay = raws.Count;
            }
            if (raws.Count == 0) return null;
            JitBlocksCompiled++;
            return new RspJitBlock {
                StartPc = start, Length = raws.Count, Raw = raws.ToArray(), Insts = insts.ToArray(), Handlers = hs.ToArray(),
                Generation = Interface.ImemGeneration
            };
        }

        // Always compares the raw words: CPU writes to IMEM are not tracked, and IMEM fetches are a direct pointer read.
        private bool JitValidate(RspJitBlock b)
        {
            // COR64_RSPIG_GEN: every write into IMEM bumps ImemGeneration (N64MemoryDevice write watch, SP DMA, IMEM
            // stream, savestate load), so a block validated at the current generation is still valid.
            if (!s_IgGen) return JitValidateWords(b);
            long g = Interface.ImemGeneration;
            if (b.Generation == g) {
                if (s_IgVerify && !JitValidateWords(b)) Environment.FailFast($"COR64_RSPIG_VERIFY: stale RSP block at {b.StartPc:X3} gen {g}");
                if (s_IgVerify) IgVerified++;
                return true;
            }
            if (!JitValidateWords(b)) return false;
            b.Generation = g;
            return true;
        }
        private static readonly bool s_IgGen = cor64.RCP.SPInterface.s_ImemGenWatch;
        private static readonly bool s_IgVerify = InitIgVerify();
        private static bool InitIgVerify() {
            bool on = Environment.GetEnvironmentVariable("COR64_RSPIG_VERIFY") == "1";
            if (on) AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.Error.WriteLine("RSPIG_VERIFY ok: " + IgVerified + " generation hits matched a full compare");
            return on;
        }
        public static long IgVerified;

        private unsafe bool JitValidateWords(RspJitBlock b)
        {
            // COR64_RSPBE_FASTVAL: once FetchInstruction's fast pointer path has passed its self-check (and is never turned off
            // again unless it found a mismatch), compare the host words directly; same values as FetchInstruction returns.
            if (s_BeFastVal && !m_FastFetchOff && m_FastFetchChecks >= 200000 && m_ImemPtr != IntPtr.Zero) {
                var host = b.RawHost;
                if (host == null) { host = new uint[b.Length]; for (int i = 0; i < host.Length; i++) host[i] = N64Endianess.U32(b.Raw[i]); b.RawHost = host; }
                uint* p = (uint*)((byte*)m_ImemPtr + b.StartPc);
                for (int i = 0; i < host.Length; i++) if (p[i] != host[i]) return false;
                return true;
            }
            for (int i = 0; i < b.Length; i++)
                if (FetchInstruction(b.StartPc + i * 4) != b.Raw[i]) return false;
            return true;
        }

        // [cer64plus] Block scheduling (default with the JIT; COR64_RSP_SCHED=step keeps exact per-batch budgets).
        // The RSP gets n steps of credit per batch and runs whole blocks while it has credit; the overshoot (less than one
        // block) is carried as negative credit. A halted RSP drops its credit, as Step() would consume it doing nothing.
        // Deterministic, and identical with or without IL blocks; interleaving with the CPU differs from `step` by at most
        // one block. m_RspCredit is a field, so savestates carry it.
        public static readonly bool BlockSched = JitEnabled && Environment.GetEnvironmentVariable("COR64_RSP_SCHED") != "step";
        private long m_RspCredit;
        // measurement only: COR64_RSPBE_CEIL=1 runs the dispatch path (lookup + validate + delegate call) twice per block
        private static readonly int s_BeCeil = int.TryParse(Environment.GetEnvironmentVariable("COR64_RSPBE_CEIL"), out var c) ? c : 0; // 1 lookup, 2 call, 3 both
        private static long s_BeSink;
        private static readonly bool s_BeFastVal = Environment.GetEnvironmentVariable("COR64_RSPBE_FASTVAL") != "0";
        private static readonly bool s_BeLink = Environment.GetEnvironmentVariable("COR64_RSPBE_LINK") == "1"; // off: measured no gain (CYC 1.013)

        private static readonly bool s_HbFast = Environment.GetEnvironmentVariable("COR64_RSPHB_FAST") != "0";
        public static readonly bool s_RspTiming = Environment.GetEnvironmentVariable("COR64_RSP_TIMING") == "1";
        public static readonly System.Collections.Generic.List<RspJitBlock> AllBlocks = new();
        public static long NSelf, NCont, NRun, TRunCredit, TRec, NRec, TEmpty;
        // [climb4 idle_span] Credit access for the idle-span merge (N64System.TryIdleFastForward).
        public long Credit { get => m_RspCredit; set => m_RspCredit = value; }
        public static bool SpanRunSupported => BlockSched && !s_RspTiming && !s_BeLink && s_BeCeil == 0;
        // Adds `budget` credit and runs exactly what RunCredit2 runs, but returns right after the block/step that halts
        // the RSP or changes MI_INTR (credit is left as is, not zeroed). lastK = steps of the last block/step run.
        // [climb4 rsp_speed1] COR64_RSPS1_MIPTR: compare MI_INTR's raw word through its fixed pointer (one load per block
        // instead of a three-load chase); equal raw words <=> equal values.
        private static readonly bool s_MiPtr = Environment.GetEnvironmentVariable("COR64_RSPS1_MIPTR") == "1";
        public unsafe void RunSpan(long budget, MipsInterface mi, uint int0, out int lastK)
        {
            lastK = 0;
            if (IsHalted) return;
            uint* mip = s_MiPtr && mi.Interrupt == int0 ? (uint*)mi.InterruptPtr : null; uint raw0 = mip != null ? *mip : 0;
            m_RspCredit += budget;
            bool fastOk = s_HbFast && s_IgGen && !s_IgVerify && RecEnabled;
            var blocks = m_JitBlocks; var iface = Interface;
            bool flat = fastOk && s_GlueFlat; var flatT = m_Flat;
            while (m_RspCredit > 0) {
                if (IsHalted) return;
                int k; int spc = (int)(PC & 0xFFC);
                if (WillJump || m_InjectedInst.HasValue || CallHack != null) { Step(); k = 1; JitFallbackSteps++; }
                else {
                    int pi = (int)(PC & 0xFFC) >> 2;
                    if (flat) { ref var e = ref flatT[pi]; if (e.Gen == iface.ImemGeneration) { JitBlockRuns++; k = e.Rec(this, e.Insts, e.H); RspSteps += k; StepsLocal += k; goto ran; } }
                    var c = fastOk ? blocks[pi * Variants] : null;
                    RspRecFn f = null;
                    if (c != null && c.Generation == iface.ImemGeneration && (f = c.Rec) != null) { if (flat) FlatFill(c, f, c.Generation); JitBlockRuns++; c.Runs++; k = f(this, c.Insts, c.Handlers); RspSteps += k; StepsLocal += k; }
                    else {
                        var b = JitLookup((int)(PC & 0xFFC));
                        if (b == null) { Step(); k = 1; JitFallbackSteps++; }
                        else { k = RunBlock(b, int.MaxValue); RspSteps += k; StepsLocal += k; }
                    }
                }
                ran:
                m_RspCredit -= k; lastK = k;
                if (IsHalted || (mip != null ? *mip != raw0 : mi.Interrupt != int0)) return;
                if (k == 3 && m_RspCredit > 0 && (!s_SpinPre || (int)(PC & 0xFFC) == spc)) DpSpinSkip(spc);
            }
        }

        // [bt_speed] RDP-wait spin skip. Banjo-Tooie's gfx ucode waits for the RDP with a 3-instruction self-loop
        // (IMEM 0x1298: mfc0 t3, $c10 (DPC_CURRENT); beq t3, t8, self; nop) while the CPU holds the DP frozen; ~90% of
        // its RSP time went there (~1M loop runs per frame). DPC_CURRENT only changes when START/END is written (by the RSP,
        // which is in the loop, or the CPU, which does not run inside one RSP credit run), so once such a block has looped
        // back to itself every further run in this credit run is identical: the remaining runs are charged in one go
        // (same steps, same final credit, same PC). COR64_RSP_DPSPIN=0 = run them one by one.
        private static readonly bool s_DpSpin = Environment.GetEnvironmentVariable("COR64_RSP_DPSPIN") != "0";
        // [climb4 rsp_speed1] COR64_RSPS1_SPINPRE: test "block ended at its own start" inline before the (not inlined) call
        private static readonly bool s_SpinPre = Environment.GetEnvironmentVariable("COR64_RSPS1_SPINPRE") == "1";
        public static long DpSpinSkips, DpSpinRuns;
        private long[] m_SpinGen; private bool[] m_SpinOk;
        private unsafe void DpSpinSkip(int startPc)
        {
            if (!s_DpSpin || !s_IgGen || WillJump || m_InjectedInst.HasValue || CallHack != null || IsHalted) return;
            int pc = (int)(PC & 0xFFC);
            if (pc != startPc) return;
            var iface = Interface; long g = iface.ImemGeneration;
            m_SpinGen ??= new long[1024]; m_SpinOk ??= new bool[1024];
            int pi = pc >> 2;
            if (m_SpinGen[pi] != g + 1) { m_SpinOk[pi] = IsDpSpinLoop(pc); m_SpinGen[pi] = g + 1; }
            if (!m_SpinOk[pi]) return;
            // The block just ran ended back at its own start, so the branch was taken: the loop condition holds.
            long n = (m_RspCredit + 2) / 3, st = n * 3;
            m_RspCredit -= st; RspSteps += st; StepsLocal += st; JitBlockRuns += n;
            DpSpinSkips++; DpSpinRuns += n;
        }
        private unsafe bool IsDpSpinLoop(int pc)
        {
            if (m_ImemPtr == IntPtr.Zero || pc > 0xFF4) return false;
            byte* im = (byte*)m_ImemPtr;
            uint w0 = N64Endianess.U32(*(uint*)(im + pc)), w1 = N64Endianess.U32(*(uint*)(im + pc + 4)), w2 = N64Endianess.U32(*(uint*)(im + pc + 8));
            if ((w0 >> 26) != 0x10 || ((w0 >> 21) & 31) != 0 || (w0 & 0x7FF) != 0) return false; // mfc0
            uint rt = (w0 >> 16) & 31, rd = (w0 >> 11) & 31;
            if (rt == 0 || rd != 10) return false; // DPC_CURRENT only
            uint op1 = w1 >> 26, a = (w1 >> 21) & 31, b = (w1 >> 16) & 31;
            if ((op1 != 4 && op1 != 5) || (w1 & 0xFFFF) != 0xFFFE) return false; // beq/bne back to pc
            if (!((a == rt) ^ (b == rt))) return false; // compared with another register (not written in the loop)
            return w2 == 0; // nop delay slot
        }

        public void RunCredit(int n)
        {
            if (s_RspTiming && !IsHalted) { NRun++; long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); RunCredit2(n); TRunCredit += System.Diagnostics.Stopwatch.GetTimestamp() - t0; return; }
            RunCredit2(n);
        }
        // [rsp_compat] RSP instructions per CPU step. FASTER_VI makes a VI field ViSpeedup times shorter in CPU steps, so with
        // one RSP step per CPU step the RSP gets ~142k instructions per field instead of the ~1M of hardware (62.5 MHz).
        // Wave Race 64 then never finishes a gfx task between audio yields (resume, yield, resume ...): the picture freezes.
        // Hardware is ~7 (62.5 MHz RSP against 142k CPU steps per field), but SM64's goldens are bound to 1, so the scale is
        // per game: games that need RSP time get it, the rest keep 1 (and their exact timing). Set when a cart is loaded.
        public static int CreditScale = 1;
        private static readonly int s_ScaleEnv = int.TryParse(System.Environment.GetEnvironmentVariable("COR64_RSP_SCALE"), out var cs) && cs >= 1 ? cs : 0;
        private static readonly System.Collections.Generic.Dictionary<string, int> s_ScaleDb = new() {
            ["WR"] = 7, // Wave Race 64: gfx task yields to audio forever at 1 (title frozen; ucode runs off to IMEM 0xFFC)
            ["ZS"] = 7, // Majora's Mask: picture black by 40 s at 1 (attract demo starved); at 7 Clock Town draws
            ["B7"] = 7, // Banjo-Tooie: needs 7 together with the DP freeze fix to reach the title
        };
        public static int ScaleFor(string gameCode)
        {
            if (s_ScaleEnv != 0) return s_ScaleEnv;
            return gameCode != null && gameCode.Length >= 3 && s_ScaleDb.TryGetValue(gameCode.Substring(1, 2), out var k) ? k : 1;
        }
        private void RunCredit2(int n)
        {
            n *= CreditScale;
            if (!BlockSched) { RunSteps(n); return; }
            if (IsHalted) { m_RspCredit = 0; return; }
            m_RspCredit += n;
            RspJitBlock prev = null;
            // [climb4 rsp_hot_blocks] COR64_RSPHB_FAST: front-variant block already validated at the current IMEM generation
            // and compiled: call it straight from the loop (same counters and state as JitLookup + RunBlock).
            bool fastOk = s_HbFast && !s_RspTiming && !s_BeLink && s_BeCeil == 0 && s_IgGen && !s_IgVerify && RecEnabled;
            fast:
            if (fastOk) {
                var blocks = m_JitBlocks; var iface = Interface;
                bool flat = s_GlueFlat; var flatT = m_Flat;
                while (m_RspCredit > 0) {
                    if (IsHalted) { m_RspCredit = 0; return; }
                    if (!(WillJump || m_InjectedInst.HasValue || CallHack != null)) {
                        int pi = (int)(PC & 0xFFC) >> 2;
                        if (flat) { ref var e = ref flatT[pi]; if (e.Gen == iface.ImemGeneration) { JitBlockRuns++; int k = e.Rec(this, e.Insts, e.H); m_RspCredit -= k; RspSteps += k; StepsLocal += k; if (k == 3 && m_RspCredit > 0 && (!s_SpinPre || (int)(PC & 0xFFC) == pi << 2)) DpSpinSkip(pi << 2); continue; } }
                        var c = blocks[pi * Variants];
                        if (c != null && c.Generation == iface.ImemGeneration) {
                            var f = c.Rec;
                            if (f != null) { if (flat) FlatFill(c, f, c.Generation); JitBlockRuns++; c.Runs++; int k = f(this, c.Insts, c.Handlers); m_RspCredit -= k; RspSteps += k; StepsLocal += k; if (k == 3 && m_RspCredit > 0 && (!s_SpinPre || (int)(PC & 0xFFC) == pi << 2)) DpSpinSkip(pi << 2); continue; }
                        }
                    }
                    break;
                }
            }
            while (m_RspCredit > 0) {
                if (IsHalted) { m_RspCredit = 0; return; }
                if (WillJump || m_InjectedInst.HasValue || CallHack != null) { Step(); m_RspCredit--; JitFallbackSteps++; if (fastOk) goto fast; continue; }
                int pcw = (int)(PC & 0xFFC);
                RspJitBlock b;
                if (s_BeLink) {
                    var nx = prev?.Next;
                    if (nx != null && nx.StartPc == pcw && JitValidate(nx)) b = nx;
                    else { b = JitLookup(pcw); if (prev != null) prev.Next = b; }
                    prev = b;
                }
                else b = JitLookup(pcw);
                if (b == null) { Step(); m_RspCredit--; JitFallbackSteps++; if (fastOk) goto fast; continue; }
                if (s_BeCeil != 0) { var b2 = (s_BeCeil & 1) != 0 ? JitLookup((int)(PC & 0xFFC)) : b; var f = b2.Rec; if ((s_BeCeil & 2) != 0 && f != null) s_BeSink += f(this, null, null); }
                { int k = RunBlock(b, int.MaxValue); m_RspCredit -= k; RspSteps += k; StepsLocal += k;
                  if (k == 3 && m_RspCredit > 0 && (!s_SpinPre || (int)(PC & 0xFFC) == pcw)) DpSpinSkip(pcw);
                  if (s_RspTiming) { if ((int)(PC & 0xFFC) == b.StartPc) NSelf++; if (m_RspCredit > 0) NCont++; } }
                if (fastOk) goto fast;
            }
        }

        // [cer64plus] measurement aid: ns per call of the handler for `raw`, and ns per full pre-decoded step.
        public (double handlerNs, double stepNs, string name) MicroBench(uint raw, int iters)
        {
            var d = Disassembler.Decode(raw); var h = CallTable[d];
            if (h == null) return (0, 0, "?");
            IsHalted = false;
            for (int i = 0; i < 1000; i++) h(d);
            double hn = double.MaxValue, sn = double.MaxValue;
            var sw = new System.Diagnostics.Stopwatch();
            for (int rep = 0; rep < 5; rep++) {
                sw.Restart();
                for (int i = 0; i < iters; i++) h(d);
                hn = Math.Min(hn, sw.Elapsed.TotalMilliseconds * 1e6 / iters);
                sw.Restart();
                for (int i = 0; i < iters; i++) { PC = 0x100; StepPredecoded(d, h); }
                sn = Math.Min(sn, sw.Elapsed.TotalMilliseconds * 1e6 / iters);
            }
            return (hn, sn, d.Op.Op);
        }

        public static readonly bool s_OpMix = Environment.GetEnvironmentVariable("COR64_RSP_OPMIX") == "1";
        public static readonly long[] OpMix = new long[512];
        public static readonly string[] OpMixName = new string[512];
        private static readonly System.Collections.Generic.Dictionary<string, int> s_OpMixIdx = new();
        // keyed by op name (opcode IDs collide between the scalar, COP and vector tables)
        private static void OpMixCount(string name)
        {
            if (!s_OpMixIdx.TryGetValue(name, out int i)) { i = s_OpMixIdx.Count; if (i >= OpMix.Length) return; s_OpMixIdx[name] = i; OpMixName[i] = name; }
            OpMix[i]++;
        }

        private RspJitBlock JitLookup(int pcw)
        {
            int baseIdx = (pcw >> 2) * Variants;
            for (int v = 0; v < Variants; v++) {
                var c = m_JitBlocks[baseIdx + v];
                if (c == null) break;
                if (JitValidate(c)) {
                    if (v > 0) { m_JitBlocks[baseIdx + v] = m_JitBlocks[baseIdx]; m_JitBlocks[baseIdx] = c; }
                    return c;
                }
            }
            var b = JitCompile(pcw);
            if (s_RspTiming) NVariantCompiles++;
            if (b != null) {
                Array.Copy(m_JitBlocks, baseIdx, m_JitBlocks, baseIdx + 1, Variants - 1);
                m_JitBlocks[baseIdx] = b;
            }
            return b;
        }

        // Runs up to `max` steps of block b (IL when hot); returns the steps run.
        private int RunBlock(RspJitBlock b, int max)
        {
            JitBlockRuns++;
            b.Runs++;
            if (RecEnabled && max == int.MaxValue) {
                if (b.Rec != null) {
                    if (!s_RspTiming) return b.Rec(this, b.Insts, b.Handlers);
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); long te = System.Diagnostics.Stopwatch.GetTimestamp();
                    int r = b.Rec(this, b.Insts, b.Handlers); long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    TRec += t1 - te; TEmpty += te - t0; NRec++; b.TTicks += t1 - te; b.TSteps += r; b.TRuns++;
                    if (!b.Listed) { b.Listed = true; lock (AllBlocks) AllBlocks.Add(b); }
                    return r;
                }
                if (!b.RecTried && b.Runs >= RecAfterRuns) { b.RecTried = true; if (s_RecBg) RecQueue(b); else { b.Rec = RecCompile(b); if (b.Rec == null) b.RecFailed = true; } }
            }
            if (JitIL) {
                if (b.Compiled == null && b.Runs >= CompileAfterRuns) b.Compiled = JitCompileIL(b);
                if (b.Compiled != null) return b.Compiled(this, b.Insts, max, b.Handlers);
            }
            long gen = Interface.ImemGeneration;
            long ti0 = s_RspTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            int n = Math.Min(b.Length, max), k = 0;
            for (; k < n; k++) {
                if (IsHalted) break;
                if (s_OpMix) OpMixCount(b.Insts[k].Op.Op);
                StepPredecoded(b.Insts[k], b.Handlers[k]);
                if (Interface.ImemGeneration != gen) { k++; break; }
            }
            if (s_RspTiming) { NInterpBlk++; NInterpSteps += k;
                int why = !RecEnabled || max != int.MaxValue ? 0 : b.Rec != null ? 1 : !b.RecTried ? (b.Runs == 1 ? 2 : 3) : b.RecFailed ? 4 : 5;
                IWhyBlk[why]++; IWhySteps[why] += k;
                b.IRuns++; b.ISteps += k; b.ITicks += System.Diagnostics.Stopwatch.GetTimestamp() - ti0;
                if (!b.IListed) { b.IListed = true; lock (IBlocks) IBlocks.Add(b); } }
            return k;
        }
        public static long NInterpBlk, NInterpSteps; public static readonly long[] IWhyBlk = new long[6], IWhySteps = new long[6]; public static readonly System.Collections.Generic.List<RspJitBlock> IBlocks = new(); public static long NVariantCompiles;

        // Executes up to `budget` Step()s and returns how many were consumed (a halted RSP consumes the rest as no-ops,
        // exactly like Step() returning early).
        public int RunSteps(int budget)
        {
            if (!JitEnabled) { for (int i = 0; i < budget; i++) Step(); return budget; }

            int done = 0;
            while (done < budget) {
                if (IsHalted) return budget;
                if (WillJump || m_InjectedInst.HasValue || CallHack != null) { Step(); done++; JitFallbackSteps++; continue; }

                int pcw = (int)(PC & 0xFFC);
                int baseIdx = (pcw >> 2) * Variants;
                RspJitBlock b = null;
                for (int v = 0; v < Variants; v++) {
                    var c = m_JitBlocks[baseIdx + v];
                    if (c == null) break;
                    if (JitValidate(c)) {
                        b = c;
                        if (v > 0) { m_JitBlocks[baseIdx + v] = m_JitBlocks[baseIdx]; m_JitBlocks[baseIdx] = c; } // move to front
                        break;
                    }
                }
                if (b == null) {
                    b = JitCompile(pcw);
                    if (b != null) {
                        Array.Copy(m_JitBlocks, baseIdx, m_JitBlocks, baseIdx + 1, Variants - 1); // evict the last variant
                        m_JitBlocks[baseIdx] = b;
                    }
                }
                if (b == null) { Step(); done++; JitFallbackSteps++; continue; }

                JitBlockRuns++;
                b.Runs++;
                if (JitIL) {
                    if (b.Compiled == null && b.Runs >= CompileAfterRuns) b.Compiled = JitCompileIL(b);
                    if (b.Compiled != null) { done += b.Compiled(this, b.Insts, budget - done, b.Handlers); continue; }
                }
                long gen = Interface.ImemGeneration;
                // running only the first k steps of a block is exactly k interpreter steps
                int n = Math.Min(b.Length, budget - done);
                for (int i = 0; i < n; i++) {
                    if (IsHalted) return budget;
                    StepPredecoded(b.Insts[i], b.Handlers[i]);
                    done++;
                    // microcode overlay: a DMA into IMEM may have replaced the rest of this block
                    if (Interface.ImemGeneration != gen) break;
                }
            }
            return done;
        }

        // Mirrors ExecuteNextInst + ExecuteInst for one step, minus fetch/decode and debug hooks.
        private void StepPredecoded(in DecodedInstruction d, Action<DecodedInstruction> h)
        {
            if (WillJump) {
                if (BranchDelay) {
                    ExecPredecoded(d, h);
                    if (IsHalted) { Interface.SetPC((uint)PC); return; }
                    BranchDelay = false;
                }
                TakeBranch = false;
                UnconditionalJump = false;
                PC = TargetAddress;
            }
            else {
                ExecPredecoded(d, h);
                PC += 4;
                PC &= PC_ALIGN_MASK;
                if (IsHalted) Interface.SetPC((uint)PC);
            }
        }

        private void ExecPredecoded(in DecodedInstruction d, Action<DecodedInstruction> h)
        {
            // LastReadInst/CurrentInst are only read by debug printers, so the JIT path skips those 48-byte copies
            m_LastFetchAddress = PC;
            if (!NullifyNext) h(d);
            else NullifyNext = false;
        }
    }
}
