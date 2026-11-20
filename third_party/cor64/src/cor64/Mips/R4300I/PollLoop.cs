using System;

namespace cor64.Mips.R4300I
{
    // [idle_speed] COR64_IDLE_POLL (default on): exact skip of storeless poll loops (e.g. Mario Tennis `lw v0,X; beq v0,zero`
    // at 80031D90, Dr. Mario's main loop calling a function pointer that tests two RAM flags).
    //
    // Discovery (real execution): at a sampled batch start the CPU is stepped by the interpreter while every step is
    // recorded: the full branch-unit state, PC and GPRs before the step, and every RDRAM word the step fetches, loads or
    // stores. Only plain integer instructions are accepted (no COP0/COP1, HI/LO, LL/SC, traps; loads and stores must hit
    // KSEG0/KSEG1 RDRAM, aligned). Each recorded word must hold one value for the whole cycle and stores must leave
    // memory unchanged. When the state returns to the first snapshot after K steps, the K-step cycle is a pure function of
    // (registers, those words): as long as the words keep their values, every further step repeats the cycle and changes
    // nothing but Count, TLB Random and the interrupt-check cadence (no interrupt can be taken: IdleSlicePossible).
    //
    // Skip (N64System.TryPollFastForward): when a batch starts in a recorded phase q (state equal to snapshot q) and the
    // words are unchanged, whole batches run as RSP credit + VI/AI ticks only; the words are re-checked after every
    // batch (RSP/DMA can write RAM) and the span stops after the first batch that changes them, the MI interrupt or the
    // VI count (that batch's CPU part still saw the old values, as in real stepping). The CPU then takes snapshot
    // (q + steps) mod K and the same counter updates as the idle skip.
    public partial class Interpreter
    {
        public static readonly bool s_PollSkip = Environment.GetEnvironmentVariable("COR64_IDLE_POLL") != "0";
        // [speed_poll1] COR64_SPDPOLL1 (per game, see Spd1For; =0 off, =all every cart): poll loops that never idle, e.g.
        // Doom 64 / Snowboard Kids spinning on non-blocking osRecvMesg inside __osDisableInt/__osRestoreInt. Adds to the
        // recording: (1) mfc0/mtc0 of Status, with Status part of every phase snapshot (it must return to its value, so
        // the cycle stays a pure function of the state); (2) write-first words: a word whose first access in the
        // recording is a full-word store (its old value is never read) is recorded with the stored value, which every
        // later cycle writes again (the stored register is part of the state), so memory after a skip equals stepping.
        // A span may start only when no enabled interrupt line is pending regardless of IE (the loop re-enables it).
        static readonly string s_Spd1Env = Environment.GetEnvironmentVariable("COR64_SPDPOLL1");
        public static bool Spd1Active;
        // [speed_poll2] COR64_SPDPOLL2 (default on; =0 off): more games with the COR64_SPDPOLL1 skip (numbers in FINDINGS.md)
        static readonly string s_Spd2Env = Environment.GetEnvironmentVariable("COR64_SPDPOLL2");
        // [speed_poll2] COR64_SPDPOLL2_REREC (opt-in =1, tried: no gain for SK/DM): a batch that starts inside the known
        // loop's PC range but matches no phase drops the loop and records again from this batch (no sampling delay), so a
        // loop whose registers change between game frames is re-found at once instead of after 16 misses + sampling.
        static readonly bool s_Spd2Rerec = Environment.GetEnvironmentVariable("COR64_SPDPOLL2_REREC") == "1";
        [NonSerialized] private bool m_PollForceRec;
        // [speed_cat4] COR64_SPDCAT4_POLL (default on; =0 off): the rest of the catalog sweep (games after "M" and the ones
        // with no archive name), enabled where Mcyc/frame fell 7%+ with spans > 0 and an identical video hash (FINDINGS.md)
        static readonly string s_Cat4PollEnv = Environment.GetEnvironmentVariable("COR64_SPDCAT4_POLL");
        // [climb4 speed_cat7] COR64_SPDCAT7_RT=1 (opt-in; no measurable gain): Rat Attack (RT) with the SPDPOLL2_LONG set; Monopoly is a GameDb row.
        static readonly bool s_Cat7Rt = Environment.GetEnvironmentVariable("COR64_SPDCAT7_RT") == "1";
        public static bool Spd1For(string gameCode) {
            if (s_Spd1Env == "0") return false;
            if (s_Spd1Env == "all") return true;
            if (gameCode == null || gameCode.Length < 3) return false;
            var id = gameCode.Substring(1, 2);
            if (s_Cat7Rt && id == "RT") return true; // [climb4 speed_cat7] opt-in
            return cor64.Session.GameDb.Has(cor64.Session.GameDb.Setting.PollSkip, id); // rows gated by COR64_* flags (GameDb.cs)
        }
        static readonly bool s_PollDbg = Environment.GetEnvironmentVariable("COR64_POLL_DEBUG") == "1"; static readonly int s_PollDbgMax = int.TryParse(Environment.GetEnvironmentVariable("COR64_POLL_DEBUG_MAX"), out var pdm) ? pdm : 6;
        public static readonly long[] MatchWhy = new long[6]; public static long PollSpans, PollFound, PollRecFail; static ulong s_DbgVa;
        const int PollKMax0 = 96, PollWMax0 = 96, PollKMax1 = 320, PollWMax1 = 256, PollSMax = 512;
        // [speed_poll2] COR64_SPDPOLL2_KMAX=<n> / _SMAX=<n>: longer cycles / store logs with the SPDPOLL1 skip (e.g. two threads
        // yielding to each other); also COR64_SPDPOLL2_WMAX
        // COR64_SPDPOLL2_LONG (default on; =0 off): per game (Spd2LongFor) 1024 / 2048 / 512; these games also get
        // COR64_SPDPOLL2_EPC, _EXL and _RESAMPLE below (SK measured slower with them).
        static readonly string s_Spd2LongEnv = Environment.GetEnvironmentVariable("COR64_SPDPOLL2_LONG");
        public static bool Spd2Long;
        public static bool Spd2LongFor(string gameCode) {
            if (s_Spd2LongEnv == "0" || gameCode == null || gameCode.Length < 3) return false;
            if (s_Spd2LongEnv == "all") return true;
            var id = gameCode.Substring(1, 2);
            if (s_Cat7Rt && id == "RT") return true; // [climb4 speed_cat7]
            return cor64.Session.GameDb.Has(cor64.Session.GameDb.Setting.PollLong, id);
        }
        static readonly int s_Spd2KMaxE = int.TryParse(Environment.GetEnvironmentVariable("COR64_SPDPOLL2_KMAX"), out var km) ? km : 0;
        static readonly int s_Spd2SMaxE = int.TryParse(Environment.GetEnvironmentVariable("COR64_SPDPOLL2_SMAX"), out var sm) ? sm : 0;
        static readonly int s_Spd2WMaxE = int.TryParse(Environment.GetEnvironmentVariable("COR64_SPDPOLL2_WMAX"), out var wm) ? wm : 0;
        static int s_Spd2KMax => s_Spd2KMaxE > 0 ? s_Spd2KMaxE : Spd2Long ? 1024 : PollKMax1;
        static int s_Spd2SMax => s_Spd2SMaxE > 0 ? s_Spd2SMaxE : Spd2Long ? 2048 : PollSMax;
        static int s_Spd2WMax => s_Spd2WMaxE > 0 ? s_Spd2WMaxE : Spd2Long ? 512 : PollWMax1;
        private static int PollKMax => Spd1Active ? s_Spd2KMax : PollKMax0;
        private static int PollWMax => Spd1Active ? s_Spd2WMax : PollWMax0;
        // [speed_poll1] volatile words (stored inside the cycle): per-phase values; store log while recording
        [NonSerialized] private uint[] m_VolAddr, m_VolVal; [NonSerialized] private int m_VolN, m_PollCurQ;
        [NonSerialized] private uint[] m_StLogAddr, m_StLogVal; [NonSerialized] private int[] m_StLogStep; [NonSerialized] private int m_StLogN;
        [NonSerialized] private uint m_RecMiMask; [NonSerialized] private bool m_RecMiSeen;

        // snapshot layout per phase: 32 GPRs, PC, BU_TargetAddress, flags, lastFetch
        // [climb4 speed_cat7] COR64_SPDCAT7_FPU (default on; =0 off): with the SPDPOLL1 skip, a cycle may contain FPU moves and
        // FPU loads/stores (cfc1/ctc1 FCSR, mfc1/mtc1/dmfc1/dmtc1, lwc1/swc1/ldc1/sdc1): the thread switch in an idle yield loop
        // saves and restores the FPU context (Rat Attack, Hybrid Heaven). The 32 FPRs and FCSR join the phase state
        // (compared and restored only for a cycle that ran an FPU op), so the match stays exact.
        static readonly bool s_Cat7Fpu = Environment.GetEnvironmentVariable("COR64_SPDCAT7_FPU") != "0";
        static readonly int SnapW = s_Cat7Fpu ? 73 : 40; // + Status, EPC, HI, LO (speed_poll1); + 32 FPRs, FCSR (speed_cat7)
        [NonSerialized] private bool m_RecFpu, m_PollFpu;
        [NonSerialized] private ulong[] m_PollSnap;
        [NonSerialized] private int m_PollK;              // >0: a loop is ready
        [NonSerialized] private uint m_PollLo, m_PollHi;  // PC range of the ready loop
        [NonSerialized] private uint[] m_PollWAddr, m_PollWVal; [NonSerialized] private int m_PollWN;
        // recording
        [NonSerialized] private ulong[] m_RecSnap;
        [NonSerialized] private uint[] m_RecWAddr, m_RecWVal; [NonSerialized] private int m_RecWN;
        [NonSerialized] private int m_RecK = -1;          // -1: not recording
        [NonSerialized] private uint m_RecStartPc;
        [NonSerialized] private int m_PollSampleCtr, m_PollSampleIdx, m_PollShift = 4;
        [NonSerialized] private uint[] m_PollSamples;
        [NonSerialized] private uint[] m_PollFailPc; [NonSerialized] private int[] m_PollFailCool;

        private static ulong BuFlags(bool a, bool b, bool c, bool d, bool e) =>
            (a ? 1UL : 0) | (b ? 2UL : 0) | (c ? 4UL : 0) | (d ? 8UL : 0) | (e ? 16UL : 0);
        private ulong CurBuFlags() => BuFlags(BU_ConditionalPending, BU_UnconditionalPending, BU_ExecuteBranchDelay, BU_DelaySlotNext, BU_NullifyNext) | (Spd1Active && m_TakenException ? 32UL : 0);
        private bool AnyBranch() => BU_ConditionalPending || BU_UnconditionalPending || BU_ExecuteBranchDelay || BU_DelaySlotNext || BU_NullifyNext;

        private unsafe void PollCapture(ulong[] dst, int k) {
            ulong* g = (ulong*)State.GetGprRef(); int o = k * SnapW;
            for (int i = 0; i < 32; i++) dst[o + i] = g[i];
            dst[o + 32] = (ulong)PC; dst[o + 33] = (ulong)BU_TargetAddress; dst[o + 34] = CurBuFlags(); dst[o + 35] = (ulong)m_LastFetchAddress;
            dst[o + 36] = Cop0State.Status.Read(); dst[o + 37] = Cop0.CpuRegisterRead(CTS.CP0_REG_EPC);
            { ulong* hl = (ulong*)State.GetHiRef(); dst[o + 38] = hl[0]; dst[o + 39] = hl[1]; }
            if (s_Cat7Fpu) { ulong* f = (ulong*)StateR4000I.FPR.RawPointer; for (int i = 0; i < 32; i++) dst[o + 40 + i] = f[i]; dst[o + 72] = State.FCR.RawValueRef; }
        }
        private unsafe bool FpuEquals(ulong[] s, int o) {
            ulong* f = (ulong*)StateR4000I.FPR.RawPointer;
            if (s[o + 72] != State.FCR.RawValueRef) return false;
            for (int i = 0; i < 32; i++) if (s[o + 40 + i] != f[i]) return false;
            return true;
        }
        // equality on architectural state; BU_TargetAddress only matters while a branch is in flight
        // [speed_poll2] COR64_SPDPOLL2_EPC (default on with the SPDPOLL1 skip; =0 off): EPC is dead at a phase when, going
        // round the cycle from it, mtc0 EPC comes before eret. An interrupt taken inside the loop leaves a stale EPC there
        // (Doom 64: the yield loop's context save runs with the interrupt's EPC until the dispatcher's mtc0), which made
        // every such batch miss. A dead EPC is not compared; on landing at a dead phase it takes the recorded value only
        // when the span ran past the mtc0 (else it keeps the current one), as stepping does.
        static readonly bool s_Spd2Epc = Environment.GetEnvironmentVariable("COR64_SPDPOLL2_EPC") != "0";
        // [speed_poll2] COR64_SPDPOLL2_EXL (default on with the SPDPOLL1 skip; =0 off): a span may start at a recorded phase
        // with Status.EXL set (inside the cycle's exception-return path; Status is part of the phase state, so the match is
        // still exact, and no enabled line may be pending regardless of IE/EXL).
        static readonly bool s_Spd2Exl = Environment.GetEnvironmentVariable("COR64_SPDPOLL2_EXL") != "0";
        [NonSerialized] private byte[] m_RecEv; [NonSerialized] private int[] m_EpcDist; // dist to the mtc0 EPC, -1 = live
        private unsafe bool PollEquals(ulong[] s, int k, bool deadOk = false) {
            int o = k * SnapW;
            if (s[o + 32] != (ulong)PC || s[o + 34] != CurBuFlags()) return false;
            if (s[o + 34] != 0 && s[o + 33] != (ulong)BU_TargetAddress) return false;
            if (Spd1Active) {
                if (s[o + 36] != Cop0State.Status.Read()) return false;
                if (s[o + 37] != Cop0.CpuRegisterRead(CTS.CP0_REG_EPC) && !(deadOk && m_EpcDist != null && m_EpcDist[k] >= 0)) return false;
                ulong* hl = (ulong*)State.GetHiRef(); if (s[o + 38] != hl[0] || s[o + 39] != hl[1]) return false;
                if ((ReferenceEquals(s, m_RecSnap) ? m_RecFpu : m_PollFpu) && !FpuEquals(s, o)) return false; // [speed_cat7]
            }
            ulong* g = (ulong*)State.GetGprRef();
            for (int i = 1; i < 32; i++) if (s[o + i] != g[i]) return false;
            return true;
        }
        private unsafe void PollRestore(ulong[] s, int k) {
            ulong* g = (ulong*)State.GetGprRef(); int o = k * SnapW;
            for (int i = 0; i < 32; i++) g[i] = s[o + i];
            PC = (long)s[o + 32]; BU_TargetAddress = (long)s[o + 33]; ulong f = s[o + 34];
            BU_ConditionalPending = (f & 1) != 0; BU_UnconditionalPending = (f & 2) != 0; BU_ExecuteBranchDelay = (f & 4) != 0;
            BU_DelaySlotNext = (f & 8) != 0; BU_NullifyNext = (f & 16) != 0;
            if (Spd1Active) m_TakenException = (f & 32) != 0;
            m_LastFetchAddress = (long)s[o + 35];
            if (Spd1Active) { Cop0State.Status.Write((uint)s[o + 36]); Cop0.CpuRegisterWrite(CTS.CP0_REG_EPC, s[o + 37]); ulong* hl = (ulong*)State.GetHiRef(); hl[0] = s[o + 38]; hl[1] = s[o + 39]; }
            if (Spd1Active && m_PollFpu) { ulong* fp = (ulong*)StateR4000I.FPR.RawPointer; for (int i = 0; i < 32; i++) fp[i] = s[o + 40 + i]; State.FCR.RawValueRef = (uint)s[o + 72]; } // [speed_cat7]
            m_QuietValid = false; m_DelayFromRec = false;
        }

        private unsafe uint* RamWords => (uint*)PhysicalMemory.RDRAM.GetRamPointer(0);
        private static bool Kseg01(ulong va, out uint phys) {
            phys = (uint)va & 0x1FFFFFFF;
            return (va >> 32) == 0xFFFFFFFFUL && ((uint)va >> 30) == 2 && phys < 0x800000;
        }
        // word (phys, aligned) must keep a single value across the recording
        private bool Stored(uint phys) { for (int i = 0; i < m_StLogN; i++) if (m_StLogAddr[i] == phys) return true; return false; }
        private unsafe bool RecWord(uint phys) {
            cor64.Rdp.RdpGuard.Read(phys, 4); // [rdp_readback]
            uint v = RamWords[phys >> 2];
            for (int i = 0; i < m_RecWN; i++) if (m_RecWAddr[i] == phys) return m_RecWVal[i] == v;
            if (m_RecWN == PollWMax) return false;
            m_RecWAddr[m_RecWN] = phys; m_RecWVal[m_RecWN] = v; m_RecWN++;
            return true;
        }

        // raw decode: 0 = rejected, 1 = no memory access, else access size in bytes (memory op)
        private static int PollClass(uint w, out bool store) {
            store = false;
            uint op = w >> 26;
            switch (op) {
                case 0: {
                    uint f = w & 63;
                    switch (f) {
                        case 0: case 2: case 3: case 4: case 6: case 7: case 8: case 9:
                        case 0x21: case 0x23: case 0x24: case 0x25: case 0x26: case 0x27: case 0x2A: case 0x2B: return 1;
                        case 0x10: case 0x11: case 0x12: case 0x13: case 0x14: case 0x16: case 0x17: case 0x18: case 0x19: case 0x1A: case 0x1B:
                            return Spd1Active ? 1 : 0; // hi/lo moves, dsllv dsrlv dsrav, mult multu div divu (speed_poll1)
                        default: return 0;
                    }
                }
                case 1: { uint rt = (w >> 16) & 31; return rt <= 3 ? 1 : 0; }
                case 0x10: { uint rs = (w >> 21) & 31; if (!Spd1Active) return 0; if (w == 0x42000018) return 1; // eret
                    uint rd = (w >> 11) & 31; return (rs == 0 || rs == 4) && (rd == 12 || (rs == 4 && rd == 14)) && (w & 0x7FF) == 0 ? 1 : 0; } // mfc0/mtc0 Status, mtc0 EPC
                case 2: case 3: case 4: case 5: case 6: case 7: case 9: case 0xA: case 0xB: case 0xC: case 0xD: case 0xE: case 0xF:
                case 0x14: case 0x15: case 0x16: case 0x17: return 1;
                case 0x11: { if (!Spd1Active || !s_Cat7Fpu || (w & 0x7FF) != 0) return 0; uint rs = (w >> 21) & 31; // [speed_cat7] FPU moves
                    return rs <= 1 || rs == 4 || rs == 5 || ((rs == 2 || rs == 6) && ((w >> 11) & 31) == 31) ? 1 | 0x200 : 0; }
                case 0x31: return Spd1Active && s_Cat7Fpu ? 4 | 0x200 : 0;                 // lwc1
                case 0x35: return Spd1Active && s_Cat7Fpu ? 8 | 0x200 : 0;                 // ldc1
                case 0x39: store = Spd1Active && s_Cat7Fpu; return store ? 4 | 0x200 : 0;  // swc1
                case 0x3D: store = Spd1Active && s_Cat7Fpu; return store ? 8 | 0x200 : 0;  // sdc1
                case 0x20: case 0x24: return 2 | 0x100;   // lb lbu: size 1 (encoded below)
                case 0x21: case 0x25: return 2;           // lh lhu
                case 0x23: case 0x27: return 4;           // lw lwu
                case 0x37: return 8;                      // ld
                case 0x28: store = true; return 2 | 0x100; // sb
                case 0x29: store = true; return 2;        // sh
                case 0x2B: store = true; return 4;        // sw
                case 0x3F: store = true; return 8;        // sd
                default: return 0;
            }
        }

        // ---- discovery ----
        public bool PollWantRecord() {
            if (m_RecK >= 0) return true;
            if (m_PollForceRec) { m_PollForceRec = false; uint fpc = (uint)PC; m_PollFailPc ??= new uint[256]; m_PollFailCool ??= new int[256];
                int fh = (int)((fpc >> 2) & 255);
                if (!(m_PollFailPc[fh] == fpc && m_PollFailCool[fh] > 0)) { PollBeginRec(fpc); return true; } }
            // sample every 2^m_PollShift busy batches; each failed recording doubles the interval (up to 1024 batches),
            // a found loop resets it, so code without poll loops pays almost nothing
            if ((++m_PollSampleCtr & ((1 << m_PollShift) - 1)) != 0) return false;
            // candidate: the PC is near one of the last 8 sampled batch-start PCs (a loop longer than a batch starts its
            // batches in different phases, so exact PC repeats are rare)
            m_PollSamples ??= new uint[8];
            uint pc = (uint)PC; bool hit = false;
            for (int i = 0; i < 8; i++) if (pc - m_PollSamples[i] + 0x200u < 0x400u) hit = true;
            m_PollSamples[m_PollSampleIdx++ & 7] = pc;
            if (!hit) return false;
            if (m_PollK > 0 && pc - m_PollLo <= m_PollHi - m_PollLo) return false; // already known
            m_PollFailPc ??= new uint[256]; m_PollFailCool ??= new int[256];
            int h = (int)((pc >> 2) & 255);
            if (m_PollFailPc[h] == pc && m_PollFailCool[h] > 0) { m_PollFailCool[h]--; return false; }
            PollBeginRec(pc);
            return true;
        }
        private void PollBeginRec(uint pc) {
            m_RecSnap ??= new ulong[PollKMax * SnapW]; m_RecWAddr ??= new uint[PollWMax]; m_RecWVal ??= new uint[PollWMax];
            m_RecK = 0; m_RecWN = 0; m_RecStartPc = pc; m_StLogN = 0; m_RecMiSeen = false; m_RecFpu = false;
            if (Spd1Active) {
                if (m_StLogAddr == null || m_StLogAddr.Length < s_Spd2SMax) { m_StLogAddr = new uint[s_Spd2SMax]; m_StLogVal = new uint[s_Spd2SMax]; m_StLogStep = new int[s_Spd2SMax]; }
                if (m_RecSnap.Length < PollKMax * SnapW) m_RecSnap = new ulong[PollKMax * SnapW];
                if (m_RecWAddr.Length < PollWMax) { m_RecWAddr = new uint[PollWMax]; m_RecWVal = new uint[PollWMax]; }
            }
        }

        public static readonly long[] PollFailWhy = new long[12];
        private void RecFail(int why = 0) {
            PollFailWhy[why]++;
            if (s_PollDbg && PollFailWhy[why] < s_PollDbgMax) Console.Error.WriteLine($"POLLFAIL why={why} start={m_RecStartPc:X8} k={m_RecK} pc={(uint)PC:X8} w={FetchInstruction((uint)PC & 0x1FFFFFFF):X8} va={s_DbgVa:X16}");
            int h = (int)((m_RecStartPc >> 2) & 255);
            m_PollFailPc[h] = m_RecStartPc; m_PollFailCool[h] = 64;
            m_RecK = -1; PollRecFail++;
            if (m_PollShift < 10) m_PollShift++;
        }

        // Runs the n steps of this batch, recording while discovery is active.
        public unsafe void PollRecordBatch(int n) {
            int i = 0;
            while (i < n && m_RecK >= 0) {
                if (m_InjectedInst.HasValue || m_ExceptionReturn || (!Spd1Active && Cop0State.Status.ExceptionLevel) || Cop0State.Status.ErrorLevel) { RecFail(1); break; }
                bool tk0 = m_TakenException;
                if (m_RecK > 0 && PollEquals(m_RecSnap, 0)) { PollFinish(); break; }
                if (m_RecK == PollKMax) { RecFail(2); break; }
                PollCapture(m_RecSnap, m_RecK);
                if ((ulong)PC >> 32 != 0 || !Kseg01(0xFFFFFFFF00000000UL | (ulong)PC, out uint pphys) || (pphys & 3) != 0 || !RecWord(pphys)) { RecFail(3); break; }
                uint w = FetchInstruction(pphys);
                if (Spd1Active) { if (m_RecEv == null || m_RecEv.Length < PollKMax) m_RecEv = new byte[PollKMax];
                    m_RecEv[m_RecK] = (byte)(w == 0x42000018 ? 2 : (w & 0xFFE0FFFF) == 0x40807000 ? 1 : 0); } // eret / mtc0 rt,EPC
                int c = PollClass(w, out bool store);
                if (c == 0) { RecFail(4); break; }
                if ((c & 0x200) != 0) { c &= ~0x200; m_RecFpu = true; } // [speed_cat7]
                uint a0 = 0, a1 = 0; int nw = 0;
                if (c != 1) {
                    int size = (c & 0x100) != 0 ? 1 : c;
                    ulong* g = (ulong*)State.GetGprRef();
                    ulong va = g[(w >> 21) & 31] + (ulong)(long)(short)w;
                    if (Spd1Active && va == 0xFFFFFFFFA430000CUL && size == 4) { // MI_INTR_MASK: read, or a write leaving it unchanged
                        uint mm = Cop0.MiMask; if (m_RecMiSeen && mm != m_RecMiMask) { RecFail(9); break; } m_RecMiSeen = true; m_RecMiMask = mm; nw = -1;
                    }
                    else if (!Kseg01(va, out uint ph) || (ph & (uint)(size - 1)) != 0) { s_DbgVa = va; RecFail(5); break; }
                    else {
                        a0 = ph & ~3u; nw = 1;
                        if (size == 8) { a1 = a0 + 4; nw = 2; }
                        if (Spd1Active) {
                            // loads see the recorded value unless the word was stored earlier in this recording; stores are logged
                            if (!store && ((!Stored(a0) && !RecWord(a0)) || (nw == 2 && !Stored(a1) && !RecWord(a1)))) { s_DbgVa = a0; RecFail(6); break; }
                        }
                        else if (!RecWord(a0) || (nw == 2 && !RecWord(a1))) { RecFail(6); break; }
                    }
                }
                Step();
                i++; m_RecK++;
                if (Spd1Active) {
                    if ((m_TakenException && !tk0) || Cop0State.Status.ErrorLevel) { RecFail(7); break; } // an exception was taken
                    if (nw == -1) { if (Cop0.MiMask != m_RecMiMask) { RecFail(9); break; } }
                    else if (store) {
                        if (m_StLogN + nw > s_Spd2SMax) { RecFail(8); break; }
                        uint* rw = RamWords;
                        m_StLogAddr[m_StLogN] = a0; m_StLogVal[m_StLogN] = rw[a0 >> 2]; m_StLogStep[m_StLogN++] = m_RecK - 1;
                        if (nw == 2) { m_StLogAddr[m_StLogN] = a1; m_StLogVal[m_StLogN] = rw[a1 >> 2]; m_StLogStep[m_StLogN++] = m_RecK - 1; }
                    }
                }
                else {
                    if (Cop0State.Status.ExceptionLevel || Cop0State.Status.ErrorLevel) { RecFail(7); break; }
                    if (store && (!RecWord(a0) || (nw == 2 && !RecWord(a1)))) { RecFail(8); break; } // store changed memory
                }
            }
            if (i < n) RunSteps(n - i);
        }

        private unsafe void PollFinish() {
            int k = m_RecK;
            m_VolN = 0;
            if (Spd1Active && m_StLogN > 0) {
                // [speed_poll1] volatile words: the value at phase 0 is memory now (one whole cycle ran). A word loaded
                // before its first store in the cycle must have read that same value, else the cycle is not periodic.
                uint* r = RamWords;
                var vl = new System.Collections.Generic.List<uint>();
                for (int i = 0; i < m_StLogN; i++) if (!vl.Contains(m_StLogAddr[i])) vl.Add(m_StLogAddr[i]);
                int wn = 0;
                for (int i = 0; i < m_RecWN; i++) {
                    if (vl.Contains(m_RecWAddr[i])) { if (r[m_RecWAddr[i] >> 2] != m_RecWVal[i]) { RecFail(10); return; } }
                    else { m_RecWAddr[wn] = m_RecWAddr[i]; m_RecWVal[wn] = m_RecWVal[i]; wn++; }
                }
                m_RecWN = wn;
                int vn = vl.Count; m_VolN = vn; m_VolAddr = vl.ToArray(); m_VolVal = new uint[vn * k];
                for (int v = 0; v < vn; v++) m_VolVal[v] = r[m_VolAddr[v] >> 2];
                for (int j = 1; j < k; j++) {
                    for (int v = 0; v < vn; v++) m_VolVal[j * vn + v] = m_VolVal[(j - 1) * vn + v];
                    for (int i = 0; i < m_StLogN; i++) if (m_StLogStep[i] == j - 1) m_VolVal[j * vn + Array.IndexOf(m_VolAddr, m_StLogAddr[i])] = m_StLogVal[i];
                }
            }
            m_EpcDist = null;
            if (Spd1Active && Spd2Long && s_Spd2Epc) {
                m_EpcDist = new int[k];
                for (int j = 0; j < k; j++) { m_EpcDist[j] = -1;
                    for (int d = 0; d < k; d++) { byte ev = m_RecEv[(j + d) % k]; if (ev == 2) break; if (ev == 1) { m_EpcDist[j] = d; break; } } }
            }
            m_PollSnap = new ulong[k * SnapW];
            Array.Copy(m_RecSnap, m_PollSnap, k * SnapW);
            m_PollWAddr = new uint[m_RecWN]; m_PollWVal = new uint[m_RecWN];
            Array.Copy(m_RecWAddr, m_PollWAddr, m_RecWN); Array.Copy(m_RecWVal, m_PollWVal, m_RecWN); m_PollWN = m_RecWN;
            uint lo = uint.MaxValue, hi = 0;
            for (int j = 0; j < k; j++) { uint p = (uint)m_PollSnap[j * SnapW + 32]; if (p < lo) lo = p; if (p > hi) hi = p; }
            m_PollLo = lo; m_PollHi = hi; m_PollK = k; m_PollFpu = m_RecFpu;
            m_RecK = -1; PollFound++; m_PollShift = 4;
            if (s_PollDbg) Console.Error.WriteLine($"POLLFOUND k={k} lo={lo:X8} hi={hi:X8} w={m_PollWN} vol={m_VolN}");
        }

        // ---- skip ----
        public bool PollCandidate => m_PollK > 0 && (uint)PC - m_PollLo <= m_PollHi - m_PollLo;

        private unsafe void PollDbgDiff() {
            uint* r = RamWords;
            for (int i = 0; i < m_PollWN; i++) if (r[m_PollWAddr[i] >> 2] != m_PollWVal[i]) Console.Error.WriteLine($"DIFF q={m_PollCurQ} const {m_PollWAddr[i]:X8} exp={m_PollWVal[i]:X8} got={r[m_PollWAddr[i] >> 2]:X8}");
            int vn = m_VolN, o = m_PollCurQ * vn;
            for (int v = 0; v < vn; v++) if (r[m_VolAddr[v] >> 2] != m_VolVal[o + v]) Console.Error.WriteLine($"DIFF q={m_PollCurQ} vol {m_VolAddr[v]:X8} exp={m_VolVal[o + v]:X8} got={r[m_VolAddr[v] >> 2]:X8}");
        }
        static int s_MissDbg;
        private unsafe void PollDbgMiss() { // [speed_poll2] debug: fields that differ from the first phase with this PC
            for (int j = 0; j < m_PollK; j++) if (m_PollSnap[j * SnapW + 32] == (ulong)PC) {
                var tmp = new ulong[SnapW]; PollCapture(tmp, 0); var sb = new System.Text.StringBuilder();
                for (int i = 1; i < SnapW; i++) if (i != 33 && tmp[i] != m_PollSnap[j * SnapW + i]) sb.Append($" f{i}:{m_PollSnap[j * SnapW + i]:X}->{tmp[i]:X}");
                Console.Error.WriteLine($"POLLMISS pc={(uint)PC:X8} q={j}{sb}"); return; }
            Console.Error.WriteLine($"POLLMISS pc={(uint)PC:X8} nophase");
        }
        public unsafe bool PollWordsSame() {
            uint* r = RamWords;
            for (int i = 0; i < m_PollWN; i++) { cor64.Rdp.RdpGuard.Read(m_PollWAddr[i], 4); if (r[m_PollWAddr[i] >> 2] != m_PollWVal[i]) return false; } // [rdp_readback]
            int vn = m_VolN, o = m_PollCurQ * vn;
            for (int v = 0; v < vn; v++) { cor64.Rdp.RdpGuard.Read(m_VolAddr[v], 4); if (r[m_VolAddr[v] >> 2] != m_VolVal[o + v]) return false; }
            return true;
        }

        // phase q whose snapshot equals the current state, or -1; also checks that a span may start
        // [speed_poll1] a stale loop (its words or registers no longer match) is dropped so it can be recorded again
        [NonSerialized] private int m_PollMissN;
        private void PollMiss() {
            if (Spd1Active && s_Spd2Rerec) { m_PollK = 0; m_PollMissN = 0; m_PollForceRec = true; return; }
            if (Spd1Active && ++m_PollMissN >= 16) { m_PollK = 0; m_PollMissN = 0; if (Spd2Long && s_Spd2Resample) m_PollShift = 0; } }
        // [speed_poll2] COR64_SPDPOLL2_RESAMPLE (default on with the SPDPOLL1 skip; =0 off): a dropped loop resets discovery
        // to sample every batch (failed recordings back it off again as before), so the loop is found again at once.
        // Doom 64 lost most of its skip to this: after the yield loop went stale the sampler sat at 1/64..1/1024.
        static readonly bool s_Spd2Resample = Environment.GetEnvironmentVariable("COR64_SPDPOLL2_RESAMPLE") != "0";
        [NonSerialized] public int DbgLastWhy = -1; // [speed_poll2] debug: last PollMatch outcome (index into MatchWhy)
        public string DbgPollState => $"k{(m_PollK > 0 ? 1 : 0)}w{DbgLastWhy}r{(m_RecK >= 0 ? 1 : 0)}s{m_PollShift}";
        public int PollMatch(int n, long cap, out long m) { int r = PollMatch0(n, cap, out m); return r; }
        private int PollMatch0(int n, long cap, out long m) {
            m = 0; DbgLastWhy = -1;
            if (!s_IdleSkip || (n & 1) != 0) return -1;
            if (Spd1Active && Cop0.PendingMasked()) { MatchWhy[0]++; DbgLastWhy = 0; return -1; }
            if (m_InjectedInst.HasValue || m_ExceptionReturn || (Cop0State.Status.ExceptionLevel && !(Spd1Active && Spd2Long && s_Spd2Exl)) || Cop0State.Status.ErrorLevel) { MatchWhy[1]++; DbgLastWhy = 1; return -1; }
            int q = -1;
            for (int j = 0; j < m_PollK; j++) if (m_PollSnap[j * SnapW + 32] == (ulong)PC && PollEquals(m_PollSnap, j, true)) { q = j; break; }
            if (q < 0) { MatchWhy[2]++; DbgLastWhy = 2; if (s_PollDbg && s_MissDbg++ < 60) PollDbgMiss(); PollMiss(); return -1; }
            m_PollCurQ = q;
            if (!PollWordsSame()) { MatchWhy[3]++; DbgLastWhy = 3; PollMiss(); if (s_PollDbg && MatchWhy[3] % 1000000 == 1) PollDbgDiff(); return -1; }
            if (!Cop0.IdleSlicePossible((uint)n)) { MatchWhy[4]++; DbgLastWhy = 4; return -1; }
            MatchWhy[5]++; DbgLastWhy = 5; m_PollMissN = 0;
            m = Cop0.IdleSliceHorizon((uint)n, cap);
            return m >= 1 ? q : -1;
        }

        // with BeginIdleSpan: state after `batches` batches of n steps run from phase q
        public unsafe void PollApply(int n, long batches, int q) {
            long steps = (long)n * batches;
            int q2 = (int)((q + steps) % m_PollK);
            ulong epc0 = Cop0.CpuRegisterRead(CTS.CP0_REG_EPC);
            PollRestore(m_PollSnap, q2);
            if (m_EpcDist != null && m_EpcDist[q] >= 0 && steps <= m_EpcDist[q]) Cop0.CpuRegisterWrite(CTS.CP0_REG_EPC, epc0); // mtc0 not reached
            if (m_VolN > 0) { uint* r = RamWords; int vn = m_VolN; for (int v = 0; v < vn; v++) { cor64.Rdp.RdpGuard.Write(m_VolAddr[v], 4); r[m_VolAddr[v] >> 2] = m_VolVal[q2 * vn + v]; } }
            TLBTick(steps);
            long c = m_IntCheckCounter + steps;
            m_IntCheckCounter = (int)(c % s_IntCheckEvery);
            Cop0.MipsTimerTick((ulong)steps);
            IdleSkippedSteps += steps;
            PollSpans++;
        }
    }
}
