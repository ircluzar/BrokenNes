// [cer64plus] RSP recompiler, stage 1: each hot block becomes one DynamicMethod that RyuJIT compiles to native code.
//
// Unlike the phase-2 scaffold (RspJitIL.cs), which wrapped every handler call in the full per-step bookkeeping, this
// emits the work itself:
//  - scalar ALU ops, LUI, shifts, set-on-less-than, DMEM loads/stores: inline IL on the GPR file and DMEM through raw
//    pointers (both are unmanaged buffers that never move for the life of the interpreter);
//  - the branch or jump that ends a block and its delay slot: resolved at compile time (condition, target, link), so no
//    WillJump/BranchDelay round trip per step;
//  - everything else (vector unit, CP0/CP2 transfers, BREAK): a direct, non-virtual call of the interpreter's handler.
// Halts and IMEM overlays can only start from a CP0 transfer or BREAK, so only those are followed by the checks.
//
// Exactness: the block leaves the interpreter in the same architectural state as running its steps one by one
// (GPRs, DMEM, PC, TargetAddress, BranchDelay/TakeBranch/UnconditionalJump), and returns the number of steps run.
// Blocks the stage cannot express (a branch without its delay slot, a delay slot that could halt or jump) keep the
// interpreter path. COR64_RSP_REC=0 turns the recompiler off.
using System;
using System.Reflection;
using System.Reflection.Emit;

namespace cor64.Mips.Rsp
{
    public delegate int RspRecFn(RspInterpreter self, DecodedInstruction[] insts, Action<DecodedInstruction>[] handlers);

    public partial class RspInterpreter
    {
        public static readonly bool s_C0Hist = Environment.GetEnvironmentVariable("COR64_RSP_C0HIST") == "1";
        public static readonly long[] C0Hist = new long[16], C0PcHist = new long[1024];
        public static readonly bool RecEnabled = Environment.GetEnvironmentVariable("COR64_RSP_REC") != "0";
        private static readonly int RecAfterRuns = int.TryParse(Environment.GetEnvironmentVariable("COR64_RSP_REC_AFTER"), out var ra) && ra >= 1 ? ra : 4; // 1 = compile on first run (tests)
        public static long RecCompiled, RecRejected, RecBgCompiled;
        // [cer64plus] Background compilation (COR64_RSP_REC_BG=0: inline, as before). The block runs step by step until its
        // compiled form is ready (identical results); the worker also calls it once with insts = null, which returns at
        // once, so the ~ms JIT compile happens off the emulation thread.
        private static readonly bool s_RecBg = Environment.GetEnvironmentVariable("COR64_RSP_REC_BG") != "0";
        [NonSerialized] private System.Collections.Concurrent.BlockingCollection<RspJitBlock> m_RecQueue;
        private unsafe void RecQueue(RspJitBlock b)
        {
            if (m_RecQueue == null) {
                m_RecQueue = new();
                var q = m_RecQueue;
                var t = new System.Threading.Thread(() => {
                    foreach (var blk in q.GetConsumingEnumerable()) {
                        try {
                            var fn = RecCompile(blk);
                            if (fn == null) { blk.RecFailed = true; continue; }
                            fn(this, null, null); // JIT now
                            System.Threading.Interlocked.Increment(ref RecBgCompiled);
                            blk.Rec = fn;
                        }
                        catch { }
                    }
                }) { IsBackground = true, Name = "cor64 RSP block compiler", Priority = System.Threading.ThreadPriority.BelowNormal };
                t.Start();
            }
            m_RecQueue.Add(b);
        }

        private static readonly bool s_GlueMem = Environment.GetEnvironmentVariable("COR64_RSPGLUE_MEM") != "0";
        private static readonly MethodInfo s_RdU8 = typeof(RspInterpreter).GetMethod("DmU8", Any, null, new[] { typeof(long) }, null);
        private static readonly MethodInfo s_WrU8 = typeof(RspInterpreter).GetMethod("DmU8", Any, null, new[] { typeof(long), typeof(byte) }, null);
        private static readonly MethodInfo s_RdU16 = typeof(RspInterpreter).GetMethod("RdU16", Any);
        private static readonly MethodInfo s_RdU32 = typeof(RspInterpreter).GetMethod("RdU32", Any);
        private static readonly MethodInfo s_WrU16 = typeof(RspInterpreter).GetMethod("WrU16", Any);
        private static readonly MethodInfo s_WrU32 = typeof(RspInterpreter).GetMethod("WrU32", Any);
        private static readonly MethodInfo s_SetTarget = typeof(InterpreterBaseRsp).GetProperty("TargetAddress", Any).GetSetMethod(true);

        private static bool IsBranchOrJump(in DecodedInstruction d) => d.Op.Family == OperationFamily.Branch;
        private static bool MayHalt(in DecodedInstruction d) => d.Op.Op == "break" || d.Op.Op == "mtc0";

        private unsafe RspRecFn RecCompile(RspJitBlock b)
        {
            int n = b.Length;
            int br = -1; bool tailBranch = false, dsStep = false;
            for (int i = 0; i < n; i++) if (IsBranchOrJump(b.Insts[i])) { br = i; break; }
            if (br >= 0) {
                // the branch must be second to last, followed by a delay slot that cannot halt, branch or reach for PC
                // COR64_RSPIR_TAIL: a block cut at MaxBlock right after its branch (no delay slot) runs the branch through
                // StepPredecoded as its last step, leaving the delay slot pending exactly like the step loop
                if (br == n - 1 && s_IrTail) tailBranch = true;
                else if (br != n - 2) { RecRejected++; b.RejWhy = "branch-not-penultimate"; return null; }
                var ds = b.Insts[n - 1];
                if (tailBranch) { }
                else if (IsBranchOrJump(ds) || ds.Op.Op == "break" || (IsXfer(ds) && !(s_IrDsXfer && ds.Op.Op != "mtc0") && !Spd1InlineDsMtc0(ds))) {
                    // COR64_RSPIR_DSSTEP: an mtc0/break delay slot runs through StepPredecoded with the branch state set
                    if (s_IrDsStep && !IsBranchOrJump(ds)) dsStep = true;
                    else { RecRejected++; b.RejWhy = "delay:" + ds.Op.Op; return null; } }
            }

            var dm = new DynamicMethod("rsprec_" + b.StartPc.ToString("X3"), typeof(int),
                CallStubArg.Params(typeof(RspInterpreter), typeof(DecodedInstruction[]), typeof(Action<DecodedInstruction>[])),
                typeof(RspInterpreter), skipVisibility: true);
            dm.InitLocals = false;
            t_RcpDP = -1; // [climb4 rsp_top_blocks] m_lastDoublePrecision unknown at block entry
            var il = dm.GetILGenerator();
            m_SmAccLo = null; m_SmAccHi = null; m_SmCached = false; m_SmNextMul = false; // [climb4 sm64_300]
            { var go = il.DefineLabel(); CallStubArg.Ld(il, 1); il.Emit(OpCodes.Brtrue, go); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret); il.MarkLabel(go); } // insts = null: JIT warm-up
            long gpr = (long)State.GetGprRef();
            short* vregs = RecVectorBase();
            var gen = il.DeclareLocal(typeof(long));
            var ma = il.DeclareLocal(typeof(long));
            bool needGen = false;
            for (int i = 0; i < n; i++) if (MayHalt(b.Insts[i])) needGen = true;
            if (needGen) { CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_GetInterface); il.Emit(OpCodes.Ldfld, s_ImemGen); il.Emit(OpCodes.Stloc, gen); }

            void GprAddr(int r) { il.Emit(OpCodes.Ldc_I8, gpr + r * 8L); il.Emit(OpCodes.Conv_U); }
            void LdGpr(int r) { if (r == 0) il.Emit(OpCodes.Ldc_I4_0); else { GprAddr(r); il.Emit(OpCodes.Ldind_U4); } }
            void SetPcConst(long pc) { CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I8, pc); il.Emit(OpCodes.Stfld, s_PcField); }
            void SetBool(PropertyInfo p, bool v) { CallStubArg.Ld(il, 0); il.Emit(v ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, p.GetSetMethod(true)); }

            // after a step that may halt or overlay IMEM: if (IsHalted) { PC = next; SetPC; return k } if (gen changed) { PC = next; return k }
            void HaltCheck(int k, bool pcDynamic, long next)
            {
                var go = il.DefineLabel(); var same = il.DefineLabel();
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Callvirt, s_GetHalted); il.Emit(OpCodes.Brfalse, go);
                if (!pcDynamic) SetPcConst(next);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_GetInterface);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldfld, s_PcField); il.Emit(OpCodes.Conv_U4);
                il.Emit(OpCodes.Callvirt, s_SetPC);
                il.Emit(OpCodes.Ldc_I4, k); il.Emit(OpCodes.Ret);
                il.MarkLabel(go);
                if (k < n) {
                    CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_GetInterface); il.Emit(OpCodes.Ldfld, s_ImemGen); il.Emit(OpCodes.Ldloc, gen); il.Emit(OpCodes.Beq, same);
                    if (!pcDynamic) SetPcConst(next);
                    il.Emit(OpCodes.Ldc_I4, k); il.Emit(OpCodes.Ret);
                    il.MarkLabel(same);
                }
            }

            // emits instruction i; returns false if it is not expressible (caller then rejects the block)
            bool EmitInst(int i)
            {
                var d = b.Insts[i];
                long pc = (b.StartPc + i * 4) & PC_ALIGN_MASK;
                string op = d.Op.Op;
                int rs = d.Source, rt = d.Target, rd = d.Destination, sa = d.ShiftAmount;
                uint imm = d.Immediate;
                void Wr(int r, Action value) { if (r == 0) return; GprAddr(r); value(); il.Emit(OpCodes.Stind_I4); }
                void Bin(int dst, OpCode code, bool immOperand, uint immVal) => Wr(dst, () => { LdGpr(rs); if (immOperand) il.Emit(OpCodes.Ldc_I4, (int)immVal); else LdGpr(rt); il.Emit(code); });
                void Addr() { LdGpr(rs); il.Emit(OpCodes.Ldc_I4, (int)(short)imm); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldc_I4, 0xFFF); il.Emit(OpCodes.And); il.Emit(OpCodes.Conv_U8); }
                // [climb4 rsp_glue] COR64_RSPGLUE_MEM: scalar DMEM loads/stores inline (same pointer math as DmU8/RdU16/RdU32/
                // WrU16/WrU32 with COR64_RSP_FAST_DMEM; unaligned halfwords/words still call the helper).
                if (s_GlueMem && s_FastDmem) {
                    long dmb = (long)DmemBytes;
                    void Mem(int size, bool store, OpCode acc, MethodInfo slow, Action val) {
                        Addr(); il.Emit(OpCodes.Stloc, ma);
                        Label sl = il.DefineLabel(), dn = il.DefineLabel();
                        if (size > 1) { il.Emit(OpCodes.Ldloc, ma); il.Emit(OpCodes.Ldc_I8, (long)(size - 1)); il.Emit(OpCodes.And); il.Emit(OpCodes.Brtrue, sl); }
                        il.Emit(OpCodes.Ldc_I8, dmb); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldloc, ma);
                        int x = size == 1 ? 3 : size == 2 ? 2 : 0;
                        if (x != 0) { il.Emit(OpCodes.Ldc_I8, (long)x); il.Emit(OpCodes.Xor); }
                        il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Add);
                        if (store) val();
                        il.Emit(acc);
                        if (size > 1) {
                            il.Emit(OpCodes.Br, dn); il.MarkLabel(sl);
                            CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldloc, ma); if (store) val(); il.Emit(OpCodes.Call, slow);
                            il.MarkLabel(dn);
                        }
                    }
                    switch (op) {
                        case "lb": Wr(rt, () => { Mem(1, false, OpCodes.Ldind_U1, null, null); il.Emit(OpCodes.Conv_I1); }); return true;
                        case "lbu": Wr(rt, () => Mem(1, false, OpCodes.Ldind_U1, null, null)); return true;
                        case "lh": Wr(rt, () => { Mem(2, false, OpCodes.Ldind_U2, s_RdU16, null); il.Emit(OpCodes.Conv_I2); }); return true;
                        case "lhu": Wr(rt, () => Mem(2, false, OpCodes.Ldind_U2, s_RdU16, null)); return true;
                        case "lw": case "lwu": Wr(rt, () => Mem(4, false, OpCodes.Ldind_U4, s_RdU32, null)); return true;
                        case "sb": Mem(1, true, OpCodes.Stind_I1, null, () => { LdGpr(rt); il.Emit(OpCodes.Conv_U1); }); return true;
                        case "sh": Mem(2, true, OpCodes.Stind_I2, s_WrU16, () => { LdGpr(rt); il.Emit(OpCodes.Conv_U2); }); return true;
                        case "sw": Mem(4, true, OpCodes.Stind_I4, s_WrU32, () => LdGpr(rt)); return true;
                    }
                }
                switch (op) {
                    case "add": case "addu": Bin(rd, OpCodes.Add, false, 0); return true;
                    case "addi": case "addiu": Bin(rt, OpCodes.Add, true, (uint)(short)imm); return true;
                    case "sub": case "subu": Bin(rd, OpCodes.Sub, false, 0); return true;
                    case "and": Bin(rd, OpCodes.And, false, 0); return true;
                    case "or": Bin(rd, OpCodes.Or, false, 0); return true;
                    case "xor": Bin(rd, OpCodes.Xor, false, 0); return true;
                    case "nor": Wr(rd, () => { LdGpr(rs); LdGpr(rt); il.Emit(OpCodes.Or); il.Emit(OpCodes.Not); }); return true;
                    case "andi": Bin(rt, OpCodes.And, true, imm); return true;
                    case "ori": Bin(rt, OpCodes.Or, true, imm); return true;
                    case "xori": Bin(rt, OpCodes.Xor, true, imm); return true;
                    case "lui": Wr(rt, () => il.Emit(OpCodes.Ldc_I4, (int)(imm << 16))); return true;
                    case "slt": Wr(rd, () => { LdGpr(rs); LdGpr(rt); il.Emit(OpCodes.Clt); }); return true;
                    case "sltu": Wr(rd, () => { LdGpr(rs); LdGpr(rt); il.Emit(OpCodes.Clt_Un); }); return true;
                    case "slti": Wr(rt, () => { LdGpr(rs); il.Emit(OpCodes.Ldc_I4, (int)(short)imm); il.Emit(OpCodes.Clt); }); return true;
                    case "sltiu": Wr(rt, () => { LdGpr(rs); il.Emit(OpCodes.Ldc_I4, (int)(short)imm); il.Emit(OpCodes.Clt_Un); }); return true;
                    case "sll": Wr(rd, () => { LdGpr(rt); il.Emit(OpCodes.Ldc_I4, sa); il.Emit(OpCodes.Shl); }); return true;
                    case "srl": Wr(rd, () => { LdGpr(rt); il.Emit(OpCodes.Ldc_I4, sa); il.Emit(OpCodes.Shr_Un); }); return true;
                    case "sra": Wr(rd, () => { LdGpr(rt); il.Emit(OpCodes.Ldc_I4, sa); il.Emit(OpCodes.Shr); }); return true;
                    case "sllv": Wr(rd, () => { LdGpr(rt); LdGpr(rs); il.Emit(OpCodes.Ldc_I4, 31); il.Emit(OpCodes.And); il.Emit(OpCodes.Shl); }); return true;
                    case "srlv": Wr(rd, () => { LdGpr(rt); LdGpr(rs); il.Emit(OpCodes.Ldc_I4, 31); il.Emit(OpCodes.And); il.Emit(OpCodes.Shr_Un); }); return true;
                    case "srav": Wr(rd, () => { LdGpr(rt); LdGpr(rs); il.Emit(OpCodes.Ldc_I4, 31); il.Emit(OpCodes.And); il.Emit(OpCodes.Shr); }); return true;
                    case "lb": Wr(rt, () => { CallStubArg.Ld(il, 0); Addr(); il.Emit(OpCodes.Call, s_RdU8); il.Emit(OpCodes.Conv_I1); }); return true;
                    case "lbu": Wr(rt, () => { CallStubArg.Ld(il, 0); Addr(); il.Emit(OpCodes.Call, s_RdU8); }); return true;
                    case "lh": Wr(rt, () => { CallStubArg.Ld(il, 0); Addr(); il.Emit(OpCodes.Call, s_RdU16); il.Emit(OpCodes.Conv_I2); }); return true;
                    case "lhu": Wr(rt, () => { CallStubArg.Ld(il, 0); Addr(); il.Emit(OpCodes.Call, s_RdU16); }); return true;
                    case "lw": case "lwu": Wr(rt, () => { CallStubArg.Ld(il, 0); Addr(); il.Emit(OpCodes.Call, s_RdU32); }); return true;
                    case "sb": CallStubArg.Ld(il, 0); Addr(); LdGpr(rt); il.Emit(OpCodes.Conv_U1); il.Emit(OpCodes.Call, s_WrU8); return true;
                    case "sh": CallStubArg.Ld(il, 0); Addr(); LdGpr(rt); il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Call, s_WrU16); return true;
                    case "sw": CallStubArg.Ld(il, 0); Addr(); LdGpr(rt); il.Emit(OpCodes.Call, s_WrU32); return true;
                }
                // [climb4 rsp_gen3_flags] CFC2 inline: TransferReg's Cp2Ctl read (VCO / VCC / VCE, zero-extended)
                if (op == "cfc2" && s_RecCfc2 && d.Op.XferSource == RegBoundType.Cp2Ctl && d.Op.XferTarget == RegBoundType.Gpr) {
                    RegTransferGprHelper(d, out _, out int gd);
                    string rd2 = (d.Destination & 3) switch { 0 => nameof(ReadVCO), 1 => nameof(ReadVCC), _ => nameof(ReadVCE) };
                    var mi = typeof(RspInterpreter).GetMethod(rd2, BindingFlags.Instance | BindingFlags.Public);
                    Wr(gd, () => { CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, mi); });
                    return true;
                }
                // [climb4 rsp_speed1] COR64_RSPSPD1_C0: SP register mfc0 ($c0-$c7) / mtc0 of the DMA address registers ($c0/$c1)
                // inline, as raw loads/stores on the registers' fixed buffers. Same values and side effects as TransferReg ->
                // ReadRegForRsp / RegWriteFromRsp: $c0-$c6 have no read handlers, the semaphore's read handler zeroes it before
                // the value is taken (so the read is 0 and the register becomes 0), and $c0/$c1 have no write handlers; neither
                // can halt the RSP or touch IMEM, so no PC store or halt check is needed.
                if (s_Spd1C0 && !s_C0Hist && !cor64.N64System.s_PhaseTiming && (op == "mfc0" || op == "mtc0")) {
                    RegTransferGprHelper(d, out int c0s, out int c0d);
                    var ex = Interface.ExportPointers();
                    if (op == "mfc0" && d.Op.XferSource == RegBoundType.Cp0 && d.Op.XferTarget == RegBoundType.Gpr && c0s >= 0 && c0s < 8) {
                        if (c0s == 7) {
                            il.Emit(OpCodes.Ldc_I8, (long)ex.SemaphorePtr); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stind_I4);
                            Wr(c0d, () => il.Emit(OpCodes.Ldc_I4_0));
                            return true;
                        }
                        long rp = c0s switch { 0 => (long)ex.SpMemAddressPtr, 1 => (long)ex.DramAddressPtr, 2 => (long)ex.ReadLenPtr, 3 => (long)ex.WriteLenPtr,
                            4 => (long)ex.StatusPtr, 5 => (long)ex.FullPtr, _ => (long)ex.BusyPtr };
                        Wr(c0d, () => { il.Emit(OpCodes.Ldc_I8, rp); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldind_U4); });
                        return true;
                    }
                    if (op == "mtc0" && d.Op.XferSource == RegBoundType.Gpr && d.Op.XferTarget == RegBoundType.Cp0 && (c0d == 0 || c0d == 1)) {
                        CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stfld, s_SkipForcedWait);
                        il.Emit(OpCodes.Ldc_I8, c0d == 0 ? (long)ex.SpMemAddressPtr : (long)ex.DramAddressPtr); il.Emit(OpCodes.Conv_U);
                        LdGpr(c0s); il.Emit(OpCodes.Stind_I4);
                        if (i == n - 1) SetPcConst((pc + 4) & PC_ALIGN_MASK); // the block tail leaves PC to a halting op's own store
                        return true;
                    }
                    // DMA length writes ($c2/$c3) as the delay slot of the block's final branch: the DMA cannot halt the RSP, and an
                    // IMEM overlay it makes is seen by the dispatcher (the block ends here). Direct RegWriteFromRsp call.
                    if (op == "mtc0" && s_Spd1Dma && br >= 0 && i == br + 1 && d.Op.XferSource == RegBoundType.Gpr && d.Op.XferTarget == RegBoundType.Cp0 && (c0d == 2 || c0d == 3)) {
                        CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stfld, s_SkipForcedWait);
                        CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_GetInterface); il.Emit(OpCodes.Ldc_I4, c0d); LdGpr(c0s); il.Emit(OpCodes.Call, s_SpRegWrite);
                        return true;
                    }
                }
                // [climb4 rdp_handoff1] COR64_RDPHO1_DPC=1 (opt-in; within noise on ASB99/SPR): mtc0 to DPC_START / DPC_END ($c8 / $c9) as a direct
                // DPCInterface.RegWriteFromRsp call (what TransferReg does for them). Neither write can halt the RSP, read its
                // PC or touch IMEM (the list is handed to the RDP and at most the DP interrupt is raised for the CPU).
                if (s_Ho1Dpc && !s_C0Hist && !cor64.N64System.s_PhaseTiming && op == "mtc0" && d.Op.XferSource == RegBoundType.Gpr && d.Op.XferTarget == RegBoundType.Cp0) {
                    RegTransferGprHelper(d, out int dps, out int dpd);
                    if (dpd == 8 || dpd == 9) {
                        CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stfld, s_SkipForcedWait);
                        CallStubArg.Ld(il, 0); il.Emit(OpCodes.Call, s_GetRdpInterface); il.Emit(OpCodes.Ldc_I4, dpd - 8); LdGpr(dps); il.Emit(OpCodes.Call, s_DpRegWrite);
                        if (i == n - 1) SetPcConst((pc + 4) & PC_ALIGN_MASK); // the block tail leaves PC to a halting op's own store
                        return true;
                    }
                }
                if (IsBranchOrJump(d)) return false; // handled by the block tail
                // handler call; ops that may read PC (BREAK, transfers) get it first
                bool pcSensitive = MayHalt(d) || IsXfer(d);
                if (pcSensitive) SetPcConst(pc);
                var h = b.Handlers[i];
                void CallHandler()
                {
                    if (ReferenceEquals(h.Target, this) && !h.Method.IsStatic && h.Method.DeclaringType.IsAssignableFrom(typeof(RspInterpreter))) {
                        CallStubArg.Ld(il, 0); CallStubArg.Ld(il, 1); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem, typeof(DecodedInstruction));
                        il.Emit(OpCodes.Call, h.Method);
                    }
                    else {
                        CallStubArg.Ld(il, 2); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem_Ref);
                        CallStubArg.Ld(il, 1); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem, typeof(DecodedInstruction));
                        il.Emit(OpCodes.Callvirt, s_Invoke);
                    }
                }
                if (!pcSensitive && RecEmitVector(il, d, h, vregs, LdGpr, CallHandler)) return true;
                CallHandler();
                if (MayHalt(d)) {
                    // PC = (PC + 4) & mask from the field: BREAK may have moved it
                    CallStubArg.Ld(il, 0); CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldfld, s_PcField);
                    il.Emit(OpCodes.Ldc_I4_4); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Ldc_I4, (int)PC_ALIGN_MASK); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.And);
                    il.Emit(OpCodes.Stfld, s_PcField);
                    HaltCheck(i + 1, true, 0);
                }
                return true;
            }

            int straight = br >= 0 ? br : n;
            for (int i = 0; i < straight; i++) {
                m_SmNextMul = s_Sm300Acc && vregs != null && i + 1 < straight && b.Handlers[i + 1].Method.Name == nameof(VectorMultiply); // [climb4 sm64_300]
                if (!EmitInst(i)) { RecRejected++; b.RejWhy = "op:" + b.Insts[i].Op.Op; return null; }
            }

            if (tailBranch) {
                SetPcConst((b.StartPc + br * 4) & PC_ALIGN_MASK);
                CallStubArg.Ld(il, 0);
                CallStubArg.Ld(il, 1); il.Emit(OpCodes.Ldc_I4, br); il.Emit(OpCodes.Ldelema, typeof(DecodedInstruction));
                CallStubArg.Ld(il, 2); il.Emit(OpCodes.Ldc_I4, br); il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Call, s_StepPre);
                il.Emit(OpCodes.Ldc_I4, n); il.Emit(OpCodes.Ret);
            }
            else if (br < 0) {
                // no branch: unless the last step was a halting op (which stored PC itself), PC moves past the block
                if (!MayHalt(b.Insts[n - 1])) SetPcConst((b.StartPc + n * 4) & PC_ALIGN_MASK);
                il.Emit(OpCodes.Ldc_I4, n); il.Emit(OpCodes.Ret);
            }
            else {
                var d = b.Insts[br];
                long pc = (b.StartPc + br * 4) & PC_ALIGN_MASK;
                string op = d.Op.Op;
                var taken = il.DeclareLocal(typeof(bool));
                var tgt = il.DeclareLocal(typeof(long));
                long link = (pc + 8) & PC_ALIGN_MASK;
                void Link(int r) { if (r == 0) return; GprAddr(r); il.Emit(OpCodes.Ldc_I4, (int)link); il.Emit(OpCodes.Stind_I4); }
                bool isJump = false;
                long constTarget = (((int)pc + (int)CoreUtils.ComputeBranchTargetOffset(d.Immediate)) & 0xFFFFFFFFL) + 4 & PC_ALIGN_MASK;
                switch (op) {
                    case "beq": LdGpr(d.Source); LdGpr(d.Target); il.Emit(OpCodes.Ceq); break;
                    case "bne": LdGpr(d.Source); LdGpr(d.Target); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); break;
                    case "bgtz": LdGpr(d.Source); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt); break;
                    case "blez": LdGpr(d.Source); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); break;
                    case "bltz": case "bltzal": LdGpr(d.Source); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Clt); break;
                    case "bgez": case "bgezal": LdGpr(d.Source); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Clt); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); break;
                    case "j": case "jal": isJump = true; constTarget = ((long)(d.Inst.target << 2) | (pc & 0xF0000000)) & PC_ALIGN_MASK; il.Emit(OpCodes.Ldc_I4_1); break;
                    case "jr": case "jalr": isJump = true; il.Emit(OpCodes.Ldc_I4_1); break;
                    default: RecRejected++; b.RejWhy = "br:" + op; return null;
                }
                il.Emit(OpCodes.Stloc, taken);
                if (op == "jr" || op == "jalr") { LdGpr(d.Source); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Ldc_I4, (int)PC_ALIGN_MASK); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.And); }
                else il.Emit(OpCodes.Ldc_I8, constTarget);
                il.Emit(OpCodes.Stloc, tgt);
                // links (after the target/condition read their sources, like the handlers)
                if (op == "bltzal" || op == "bgezal" || op == "jal") Link(31);
                else if (op == "jalr") Link(d.Destination != 0 ? d.Destination : 31);
                // branch-unit state as the handler leaves it: TargetAddress set, BranchDelay set
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldloc, tgt); il.Emit(OpCodes.Call, s_SetTarget);
                SetBool(s_BranchDelay, true);
                // delay slot
                if (dsStep) {
                    long dpc = (pc + 4) & PC_ALIGN_MASK;
                    SetPcConst(dpc);
                    if (isJump) SetBool(s_Unconditional, true);
                    else { CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldloc, taken); il.Emit(OpCodes.Call, s_TakeBranch.GetSetMethod(true)); }
                    CallStubArg.Ld(il, 0);
                    CallStubArg.Ld(il, 1); il.Emit(OpCodes.Ldc_I4, br + 1); il.Emit(OpCodes.Ldelema, typeof(DecodedInstruction));
                    CallStubArg.Ld(il, 2); il.Emit(OpCodes.Ldc_I4, br + 1); il.Emit(OpCodes.Ldelem_Ref);
                    il.Emit(OpCodes.Call, s_StepPre);
                    il.Emit(OpCodes.Ldc_I4, n); il.Emit(OpCodes.Ret);
                    goto done;
                }
                m_SmNextMul = false;
                if (!EmitInst(br + 1)) { RecRejected++; b.RejWhy = "dsop:" + b.Insts[br + 1].Op.Op; return null; }
                // commit: taken -> PC = target, BranchDelay = false; not taken -> PC = delay + 4 (BranchDelay stays set)
                var notTaken = il.DefineLabel();
                if (!isJump) { il.Emit(OpCodes.Ldloc, taken); il.Emit(OpCodes.Brfalse, notTaken); }
                SetBool(s_BranchDelay, false);
                CallStubArg.Ld(il, 0); il.Emit(OpCodes.Ldloc, tgt); il.Emit(OpCodes.Stfld, s_PcField);
                il.Emit(OpCodes.Ldc_I4, n); il.Emit(OpCodes.Ret);
                if (!isJump) {
                    il.MarkLabel(notTaken);
                    SetPcConst((pc + 8) & PC_ALIGN_MASK);
                    il.Emit(OpCodes.Ldc_I4, n); il.Emit(OpCodes.Ret);
                }
            }
            done:
            System.Threading.Interlocked.Increment(ref RecCompiled);
            return CallStubArg.Bind<RspRecFn>(dm);
        }

        private static readonly bool s_IrTail = Environment.GetEnvironmentVariable("COR64_RSPIR_TAIL") != "0";
        private static readonly bool s_IrDsStep = Environment.GetEnvironmentVariable("COR64_RSPIR_DSSTEP") != "0";
        private static readonly bool s_IrDsXfer = Environment.GetEnvironmentVariable("COR64_RSPIR_DSXFER") != "0";
        private static readonly MethodInfo s_StepPre = typeof(RspInterpreter).GetMethod("StepPredecoded", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly bool s_Spd1C0 = Environment.GetEnvironmentVariable("COR64_RSPSPD1_C0") != "0";
        private static readonly FieldInfo s_SkipForcedWait = typeof(RspInterpreter).GetField("m_SkipForcedWait", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly bool s_Spd1Dma = Environment.GetEnvironmentVariable("COR64_RSPSPD1_DMA") != "0";
        private static readonly bool s_Ho1Dpc = Environment.GetEnvironmentVariable("COR64_RDPHO1_DPC") == "1";
        private static readonly MethodInfo s_GetRdpInterface = typeof(InterpreterBaseRsp).GetProperty("RdpInterface", BindingFlags.Instance | BindingFlags.NonPublic).GetGetMethod(true);
        private static readonly MethodInfo s_DpRegWrite = typeof(cor64.RCP.DPCInterface).GetMethod("RegWriteFromRsp", BindingFlags.Instance | BindingFlags.Public);
        private static readonly MethodInfo s_SpRegWrite = typeof(cor64.RCP.SPInterface).GetMethod("RegWriteFromRsp", BindingFlags.Instance | BindingFlags.Public);
        // COR64_RSPSPD1_C0 / _DMA: an mtc0 delay slot that EmitInst inlines (SP $c0/$c1, or $c2/$c3 with _DMA) skips the StepPredecoded path
        private static bool Spd1InlineDsMtc0(in DecodedInstruction ds)
        {
            if (!s_Spd1C0 || s_C0Hist || cor64.N64System.s_PhaseTiming || !s_IrDsXfer || ds.Op.Op != "mtc0") return false;
            if (ds.Op.XferSource != RegBoundType.Gpr || ds.Op.XferTarget != RegBoundType.Cp0) return false;
            RegTransferGprHelper(ds, out _, out int c0d);
            return c0d == 0 || c0d == 1 || (s_Spd1Dma && (c0d == 2 || c0d == 3));
        }
        private static readonly bool s_RecCfc2 = Environment.GetEnvironmentVariable("COR64_RSPG3F_CFC2") != "0";

        private static bool IsXfer(in DecodedInstruction d)
        {
            switch (d.Op.Op) { case "mtc0": case "mfc0": case "mtc2": case "mfc2": case "ctc2": case "cfc2": return true; default: return false; }
        }
    }
}
