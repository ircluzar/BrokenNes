// [dbg_rsp] Debugger-only views of RSP state that has no public accessor (DIV unit). Read by N64DebugTarget between frames;
// nothing here is called on the emulation path.
namespace cor64.Mips.Rsp
{
    public partial class RspInterpreter
    {
        /// <summary>DIV unit input (DivIn), result (DivOut) and the double-precision latch (set by VRCPH/VRSQH).</summary>
        public int DebugDivIn => m_DivIn;
        public int DebugDivOut => m_DivOut;
        public bool DebugDivDoublePrecision => m_lastDoublePrecision;
    }
}
