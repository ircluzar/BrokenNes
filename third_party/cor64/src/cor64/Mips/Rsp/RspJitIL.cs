// [cer64plus] RSP dynarec, phase 2: each hot block becomes one DynamicMethod.
//
// The emitted method performs, for each instruction of the block, exactly what StepPredecoded does: the handler call
// (direct `call` of the bound instance method, no delegate, no loop), PC advance, halt check, and on the delay slot the
// branch commit. A `max` argument limits how many steps run, so a partial run is still exactly k interpreter steps and
// the instruction stream (and the whole emulator) stays bit-identical to the interpreter.
//
// Savestate-safe like phase 1: the method is derived from IMEM words and holds no state; it is dropped with its block.
using System;
using System.Reflection;
using System.Reflection.Emit;

namespace cor64.Mips.Rsp
{
    public delegate int RspBlockFn(RspInterpreter self, DecodedInstruction[] insts, int max, Action<DecodedInstruction>[] handlers);

    public partial class RspInterpreter
    {
        // Off by default: calling the existing handlers from IL measured slower than the phase-1 loop (castle, 600 VI:
        // 8953 vs 8148 ms median). It is the scaffold for inlining handlers as IL; COR64_RSP_JIT_IL=1 enables it.
        public static readonly bool JitIL = Environment.GetEnvironmentVariable("COR64_RSP_JIT_IL") == "1";
        private const int CompileAfterRuns = 8;
        public static long JitILCompiled;

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo s_PcField = typeof(BaseInterpreter).GetField("m_Pc", Any);
        private static readonly FieldInfo s_LastFetch = typeof(RspInterpreter).GetField("m_LastFetchAddress", Any);
        private static readonly MethodInfo s_GetHalted = typeof(InterpreterBaseRsp).GetProperty("IsHalted", Any).GetGetMethod(true);
        private static readonly MethodInfo s_GetWillJump = typeof(InterpreterBaseRsp).GetProperty("WillJump", Any).GetGetMethod(true);
        private static readonly PropertyInfo s_BranchDelay = typeof(InterpreterBaseRsp).GetProperty("BranchDelay", Any);
        private static readonly PropertyInfo s_TakeBranch = typeof(InterpreterBaseRsp).GetProperty("TakeBranch", Any);
        private static readonly PropertyInfo s_Unconditional = typeof(InterpreterBaseRsp).GetProperty("UnconditionalJump", Any);
        private static readonly PropertyInfo s_Nullify = typeof(InterpreterBaseRsp).GetProperty("NullifyNext", Any);
        private static readonly MethodInfo s_GetTarget = typeof(InterpreterBaseRsp).GetProperty("TargetAddress", Any).GetGetMethod(true);
        private static readonly MethodInfo s_GetInterface = typeof(InterpreterBaseRsp).GetProperty("Interface", Any).GetGetMethod(true);
        private static readonly MethodInfo s_SetPC = typeof(cor64.RCP.SPInterface).GetMethod("SetPC", Any, null, new[] { typeof(uint) }, null);
        private static readonly FieldInfo s_ImemGen = typeof(cor64.RCP.SPInterface).GetField("ImemGeneration", BindingFlags.Instance | BindingFlags.Public);
        private static readonly MethodInfo s_Invoke = typeof(Action<DecodedInstruction>).GetMethod("Invoke");

        private RspBlockFn JitCompileIL(RspJitBlock b)
        {
            var dm = new DynamicMethod("rsp_" + b.StartPc.ToString("X3"), typeof(int),
                new[] { typeof(RspInterpreter), typeof(DecodedInstruction[]), typeof(int), typeof(Action<DecodedInstruction>[]) },
                typeof(RspInterpreter), skipVisibility: true);
            dm.InitLocals = false; // only `gen` is a real local; it is assigned before use
            var il = dm.GetILGenerator();
            var gen = il.DeclareLocal(typeof(long));
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, s_GetInterface); il.Emit(OpCodes.Ldfld, s_ImemGen); il.Emit(OpCodes.Stloc, gen);

            for (int i = 0; i < b.Length; i++) {
                // budget: stop after `max` steps
                var go = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Bgt, go);
                il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ret);
                il.MarkLabel(go);

                bool isDelaySlot = i > 0 && b.Insts[i - 1].Op.Family == OperationFamily.Branch;
                var normal = il.DefineLabel();
                var next = il.DefineLabel();

                if (isDelaySlot) {
                    // if (WillJump) { if (BranchDelay) { exec; if (IsHalted) { SetPC; return i+1 } BranchDelay = false }
                    //                 TakeBranch = false; UnconditionalJump = false; PC = TargetAddress; }
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, s_GetWillJump); il.Emit(OpCodes.Brfalse, normal);
                    var noDelay = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, s_BranchDelay.GetGetMethod(true)); il.Emit(OpCodes.Brfalse, noDelay);
                    EmitExec(il, b, i);
                    EmitHaltReturn(il, i + 1);
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, s_BranchDelay.GetSetMethod(true));
                    il.MarkLabel(noDelay);
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, s_TakeBranch.GetSetMethod(true));
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, s_Unconditional.GetSetMethod(true));
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, s_GetTarget); il.Emit(OpCodes.Stfld, s_PcField);
                    il.Emit(OpCodes.Br, next);
                }

                // normal: exec; PC = (PC + 4) & 0xFFC; if (IsHalted) { SetPC; return i+1 }
                il.MarkLabel(normal);
                EmitExec(il, b, i);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, s_PcField);
                il.Emit(OpCodes.Ldc_I4_4); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, (int)PC_ALIGN_MASK); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.And);
                il.Emit(OpCodes.Stfld, s_PcField);
                EmitHaltReturn(il, i + 1);

                il.MarkLabel(next);
                // A DMA into IMEM (an overlay) may have replaced the rest of this block: same check as the phase-1 loop.
                if (i + 1 < b.Length) {
                    var same = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, s_GetInterface); il.Emit(OpCodes.Ldfld, s_ImemGen); il.Emit(OpCodes.Ldloc, gen); il.Emit(OpCodes.Beq, same);
                    il.Emit(OpCodes.Ldc_I4, i + 1); il.Emit(OpCodes.Ret);
                    il.MarkLabel(same);
                }
            }
            il.Emit(OpCodes.Ldc_I4, b.Length); il.Emit(OpCodes.Ret);
            JitILCompiled++;
            return (RspBlockFn)dm.CreateDelegate(typeof(RspBlockFn));
        }

        // m_LastFetchAddress = PC; if (!NullifyNext) handler(insts[i]); else NullifyNext = false;
        private void EmitExec(ILGenerator il, RspJitBlock b, int i)
        {
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, s_PcField); il.Emit(OpCodes.Stfld, s_LastFetch);
            var run = il.DefineLabel(); var done = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, s_Nullify.GetGetMethod(true)); il.Emit(OpCodes.Brfalse, run);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, s_Nullify.GetSetMethod(true));
            il.Emit(OpCodes.Br, done);
            il.MarkLabel(run);
            var h = b.Handlers[i];
            if (ReferenceEquals(h.Target, this) && !h.Method.IsStatic && h.Method.DeclaringType.IsAssignableFrom(typeof(RspInterpreter))) {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem, typeof(DecodedInstruction));
                il.Emit(OpCodes.Call, h.Method); // the bound override itself, called non-virtually
            }
            else {
                il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem, typeof(DecodedInstruction));
                il.Emit(OpCodes.Callvirt, s_Invoke);
            }
            il.MarkLabel(done);
        }

        // if (IsHalted) { Interface.SetPC((uint)PC); return k; }
        private static void EmitHaltReturn(ILGenerator il, int k)
        {
            var go = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, s_GetHalted); il.Emit(OpCodes.Brfalse, go);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, s_GetInterface);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, s_PcField); il.Emit(OpCodes.Conv_U4);
            il.Emit(OpCodes.Callvirt, s_SetPC);
            il.Emit(OpCodes.Ldc_I4, k); il.Emit(OpCodes.Ret);
            il.MarkLabel(go);
        }
    }
}
