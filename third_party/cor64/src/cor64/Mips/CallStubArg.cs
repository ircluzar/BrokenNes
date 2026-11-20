using System;
using System.Reflection.Emit;

namespace cor64.Mips
{
    /// <summary>
    /// [climb4 call_stub] Compiled blocks are static DynamicMethods. An open delegate over a static method makes every
    /// call go through a runtime shuffle thunk (arguments moved one register down). With COR64_CALLSTUB_CLOSED (default
    /// on) each DynamicMethod gets a dummy leading object parameter and the delegate is closed over a sentinel, so the
    /// delegate's target lands in that parameter and the call is a direct jump (no thunk). Delegate types are unchanged.
    /// </summary>
    internal static class CallStubArg
    {
        public static readonly bool Closed = Environment.GetEnvironmentVariable("COR64_CALLSTUB_CLOSED") != "0";
        private static readonly object s_Target = new object();
        private static readonly int s_Shift = Closed ? 1 : 0;

        public static Type[] Params(params Type[] p)
        {
            if (!Closed) return p;
            var r = new Type[p.Length + 1];
            r[0] = typeof(object);
            Array.Copy(p, 0, r, 1, p.Length);
            return r;
        }

        public static void Ld(ILGenerator il, int i)
        {
            switch (i + s_Shift) {
                case 0: il.Emit(OpCodes.Ldarg_0); break;
                case 1: il.Emit(OpCodes.Ldarg_1); break;
                case 2: il.Emit(OpCodes.Ldarg_2); break;
                case 3: il.Emit(OpCodes.Ldarg_3); break;
                default: il.Emit(OpCodes.Ldarg_S, (byte)(i + s_Shift)); break;
            }
        }

        public static T Bind<T>(DynamicMethod dm) where T : Delegate
            => Closed ? (T)dm.CreateDelegate(typeof(T), s_Target) : (T)dm.CreateDelegate(typeof(T));
    }
}
