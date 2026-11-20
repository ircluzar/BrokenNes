using cor64.IO;
using cor64.Debugging;
using System;
using cor64.Rdp;
using NLog;
using cor64.Mips;

namespace cor64.RCP {
/*
 0x0410 0000 to 0x041F FFFF  DP command registers:
-------------------------------------------------
    DPC_BASE_REG - 0x04100000

    0x0410 0000 to 0x0410 0003  DPC_START_REG
             DP CMD DMA start
       (RW): [23:0] DMEM/RDRAM start address
    0x0410 0004 to 0x0410 0007  DPC_END_REG
             DP CMD DMA end
       (RW): [23:0] DMEM/RDRAM end address
    0x0410 0008 to 0x0410 000B  DPC_CURRENT_REG
             DP CMD DMA end
        (R): [23:0] DMEM/RDRAM current address
    0x0410 000C to 0x0410 000F  DPC_STATUS_REG
             DP CMD status
        (W): [0] clear xbus_dmem_dma  (R): [0]  xbus_dmem_dma
             [1] set xbus_dmem_dma         [1]  freeze
             [2] clear freeze              [2]  flush
             [3] set freeze                [3]  start gclk
             [4] clear flush               [4]  tmem busy
             [5] set flush                 [5]  pipe busy
             [6] clear tmem ctr            [6]  cmd busy
             [7] clear pipe ctr            [7]  cbuf ready
             [8] clear cmd ctr             [8]  dma busy
             [9] clear clock ctr           [9]  end valid
                                           [10] start valid
    0x0410 0010 to 0x0410 0013  DPC_CLOCK_REG
             DP clock counter
        (R): [23:0] clock counter
    0x0410 0014 to 0x0410 0017  DPC_BUFBUSY_REG
             DP buffer busy counter
        (R): [23:0] clock counter
    0x0410 0018 to 0x0410 001B  DPC_PIPEBUSY_REG
             DP pipe busy counter
        (R): [23:0] clock counter
    0x0410 001C to 0x0410 001F  DPC_TMEM_REG
             DP TMEM load counter
        (R): [23:0] clock counter
    0x0410 0020 to 0x041F FFFF  Unused
*/
    public class DPCInterface : N64MemoryDevice {
        [Flags]
        public enum WriteStatusFlags : uint {
            ClearXbusDmemDma = 1,
            SetXbusDmemDma = 0b10,
            ClearFreeze = 0b100,
            SetFreeze   = 0b1000,
            ClearFlush  = 0b10000,
            SetFlush    = 0b100000,
            ClearTmemCtr= 0b1000000,
            ClearPipeCtr= 0b10000000,
            ClearCmdCtr = 0b100000000,
            ClearClockCtr=0b1000000000
        }

        [Flags]
        public enum ReadStatusFlags : uint {
            XbusDmemDma = 1,
            Freeze = 0b10,
            Flush =   0b100,
            StartGClk=0b1000,
            TmemBusy= 0b10000,
            PipeBusy= 0b100000,
            CmdBusy = 0b1000000,
            CommandBufferReady = 0b10000000,
            DmaBusy = 0b100000000,
            EndValid=  0b1000000000,
            StartValid=0b10000000000
        }

        private readonly static Logger Log = LogManager.GetCurrentClassLogger();
        private readonly MemMappedBuffer m_Start = new MemMappedBuffer();
        private readonly MemMappedBuffer m_End = new MemMappedBuffer();
        private readonly MemMappedBuffer m_Current = new MemMappedBuffer(4, MemMappedBuffer.MemModel.SINGLE_READONLY);
        private readonly MemMappedBuffer m_Status = new MemMappedBuffer(4,  MemMappedBuffer.MemModel.DUAL_READ_WRITE);
        private readonly MemMappedBuffer m_Clock = new MemMappedBuffer(4, MemMappedBuffer.MemModel.SINGLE_READONLY);
        private readonly MemMappedBuffer m_BufferBusyCounter = new MemMappedBuffer(4, MemMappedBuffer.MemModel.SINGLE_READONLY);
        private readonly MemMappedBuffer m_PipeBusyCounter = new MemMappedBuffer(4, MemMappedBuffer.MemModel.SINGLE_READONLY);
        private readonly MemMappedBuffer m_TmemLoadCounter = new MemMappedBuffer(4, MemMappedBuffer.MemModel.SINGLE_READONLY);
        private readonly DrawProcessor m_Rdp;
        private readonly MipsInterface m_RcpInterface;

        private readonly MemMappedBuffer[] m_RegSelects;

        public event EventHandler<DisplayList> DisplayListReady;

        public event Action OnStatusWrite;

        public struct DisplayList {
            internal DisplayList(uint start, uint end) {
                Start = start;
                End = end;
            }

            public uint Start { get; }
            public uint End { get; }
        }

        public class MemExports {
            public IntPtr StartPtr { get; }
            public IntPtr EndPtr { get; }
            public IntPtr CurrentPtr { get; }
            public IntPtr StatusPtr { get; }
            public IntPtr ClockPtr { get; }
            public IntPtr BufferBusyCounterPtr { get; }
            public IntPtr PipeBusyCounterPtr { get; }
            public IntPtr TmemLoadCounterPtr { get; }

            public MemExports(DPCInterface iface) {
                StartPtr = iface.m_Start.ReadPtr;
                EndPtr = iface.m_End.ReadPtr;
                CurrentPtr = iface.m_Current.ReadPtr;
                StatusPtr = iface.m_Status.ReadPtr;
                ClockPtr = iface.m_Clock.ReadPtr;
                BufferBusyCounterPtr = iface.m_BufferBusyCounter.ReadPtr;
                PipeBusyCounterPtr = iface.m_PipeBusyCounter.ReadPtr;
                TmemLoadCounterPtr = iface.m_TmemLoadCounter.ReadPtr;
            }
        }

        public DPCInterface(N64MemoryController controller, MipsInterface rcpInterface) : base (controller, 0x100000) {
            m_RcpInterface = rcpInterface;

            StaticMap(m_Start, m_End, m_Current);
            StaticMap(m_Status);
            StaticMap(m_Clock, m_BufferBusyCounter, m_PipeBusyCounter, m_TmemLoadCounter);

            m_Start.Write += DisplayListStart;
            m_End.Write += DisplayListEnd;
            m_Status.Write += StatusWrite;
            // [jfg_crash] DPC_CLOCK: a free-running 24-bit counter (ares rdp/io.cpp: RCP clock / 3 since the last clear).
            // It always read 0 here; Rare's RSP boot (Jet Force Gemini, on resuming a yielded gfx task) treats a zero clock
            // as a fault and the ucode then writes SP_STATUS 0xC0 (interrupt-on-break off): no SP interrupt, "SP CRASHED".
            // Updated on RSP reads (mfc0 $c12; CPU reads do not notify here and see the last value); COR64_DPC_CLOCK=0: old 0.
            if (s_ClockOn) m_Clock.Read += UpdateClock;

            m_RegSelects = new MemMappedBuffer[] {
                m_Start,              // $c8
                m_End,                // $c9
                m_Current,            // $c10
                m_Status,             // $c11
                m_Clock,              // $c12
                m_BufferBusyCounter,  // $c13
                m_PipeBusyCounter,    // $c14
                m_TmemLoadCounter     // $c15
            };
        }

        private bool m_FrozenPending;
        private static readonly bool s_ClockOn = System.Environment.GetEnvironmentVariable("COR64_DPC_CLOCK") != "0";
        /// <summary>RCP clocks of the owning machine (set by N64System: CPU ticks * 2/3 plus RSP steps, so it also advances inside a CPU batch
        /// while the RSP runs). Not divided by 3 as in ares: a coarse counter, kept fast so it never reads 0 right after a clear.</summary>
        public Func<long> TickSource;
        private long m_ClockBase;
        private long m_ClockLast;
        // The tick sources only advance between JIT blocks / CPU batches, so a read right after a clear would see 0: every read
        // moves the counter forward at least 4 clocks (an RSP mfc0 pair is a few RCP cycles on hardware too). A jump that crosses
        // a 0x80 / 0x2000 boundary lands on it: Rare's RSP code polls for those low bits to be zero, which a counter that
        // steps by one per cycle always shows to a tight poll.
        private void UpdateClock() { if (TickSource == null) return; long v = TickSource() - m_ClockBase; if (v <= m_ClockLast) v = m_ClockLast + 4; m_ClockLast = v; m_Clock.ReadonlyRegisterValue = (uint)(v & 0xFFFFFF) | 1; }
        private static readonly uint s_XbusMask = System.Environment.GetEnvironmentVariable("COR64_BOOT_DKR_XBUS") == "0" ? 0x3FFu : 0xFFFu; // =0: old 10-bit mask
        private static readonly bool s_DpWrapGuard = System.Environment.GetEnvironmentVariable("COR64_X64CRASH_DPWRAP") != "0";
        public static long DpWrapSkips;
        private static readonly bool s_HonorFreeze = System.Environment.GetEnvironmentVariable("COR64_DP_FREEZE") != "0";

        public void RegWriteFromRsp(int select, uint value) {
            m_RegSelects[select].RegisterValue = value;
            m_RegSelects[select].WriteNotify();
        }

        public uint ReadRegForRsp(int select) {
            m_RegSelects[select].ReadNotify();
            return m_RegSelects[select].ReadonlyRegisterValue;
        }

        private void DisplayListStart() {
            if (EventLog.Enabled) EventLog.E("DP_START", m_Start.RegisterValue.ToString("X"));
            m_Start.RegisterValue &= 0x00FFFFFFF;
            m_Current.ReadonlyRegisterValue = m_Start.RegisterValue;
            // Log.Debug("DPC Start: {0:X8}", m_Start.RegisterValue);
        }

        private void DisplayListEnd() {
            Telemetry.RdpListRuns++;
            if (EventLog.Enabled) EventLog.E("DP_END", m_End.RegisterValue.ToString("X") + " start=" + m_Start.RegisterValue.ToString("X") + " xbus=" + UseXBus);
            if (CompatLog.TraceTasks) System.Console.WriteLine($"DPEND end={m_End.RegisterValue:X} start={m_Start.RegisterValue:X} cur={m_Current.ReadonlyRegisterValue:X} xbus={UseXBus} st={m_Status.ReadonlyRegisterValue:X}");
            m_End.RegisterValue &= 0x00FFFFFFF;

            // [rsp_compat] A frozen RDP takes no commands; the list runs when the freeze is cleared (ares rdp/io.cpp).
            if (s_HonorFreeze && (RFlags & ReadStatusFlags.Freeze) != 0) { m_FrozenPending = true; return; }

            // Log.Debug("DPC End: {0:X8}",  m_End.RegisterValue);

            var start = m_Current.ReadonlyRegisterValue;
            var end =   m_End.RegisterValue;

            // Alignment
            start &= ~7U;
            end &= ~7U;

            // Nothing happens when START and END are set to the same value
            if (m_End.RegisterValue <= m_Start.RegisterValue) {
                // Log.Debug("DPC: End <= Start");
                // m_RcpInterface.SetInterrupt(MipsInterface.INT_DP, true);
                return;
            }

            // [x64_crash] END below CURRENT (END rewritten without a new START, e.g. bt at RSP scale 1 after a freeze):
            // the RDP only reads CURRENT..END while CURRENT < END (ares rdp/render), so nothing runs. Without this the
            // renderer copied a negative length and crashed the process. COR64_X64CRASH_DPWRAP=0 restores the old path.
            if (s_DpWrapGuard && end < start) {
                DpWrapSkips++;
                if (CompatLog.TraceTasks) System.Console.WriteLine($"DPWRAP end={end:X} cur={start:X} start={m_Start.RegisterValue:X}");
                return;
            }

            // XBUS Mode: Convert address to DMEM
            // [boot_dkr] DMEM is 4 KB: the XBUS address is 12 bits (ares rdp/render.cpp reads rsp.dmem, which wraps at
            // 0xFFF). The old 0x3FF mask read DKR's SYNC_FULL at DMEM 0xA10 from 0x210, so no DP interrupt ever came.
            if (UseXBus) {
               uint xm = s_XbusMask;
               start = 0x04000000 + (start & xm);
               end =   0x04000000 + (end   & xm);
            }

            // Log.Debug("Executing DL: {0:X8}:{1:X8}", start, end);

            // Update current before the interrupt
            m_Current.ReadonlyRegisterValue = m_End.RegisterValue;

            DisplayListReady?.Invoke(this, new DisplayList(start, end));
        }

        public void DirectDLSetup(uint address, int size) {
            m_Start.ReadonlyRegisterValue = address;
            m_Start.WriteNotify();
            m_End.RegisterValue = (uint)(address + size);
            m_End.WriteNotify();
        }

        public void DirectDLExecute() {
            DisplayListEnd();
        }

        private void StatusWrite() {
            var status = m_Status.RegisterValue;
            var flags = (WriteStatusFlags)status;

            // Log.Debug("RDP Status Write: {0:X8}", m_Status.RegisterValue);

            if ((flags & WriteStatusFlags.SetXbusDmemDma) == WriteStatusFlags.SetXbusDmemDma) {
                RFlags |= ReadStatusFlags.XbusDmemDma;
            }

            if ((flags & WriteStatusFlags.ClearXbusDmemDma) == WriteStatusFlags.ClearXbusDmemDma) {
                RFlags &= ~ReadStatusFlags.XbusDmemDma;
            }

            if ((flags & WriteStatusFlags.SetFreeze) == WriteStatusFlags.SetFreeze) {
                RFlags |= ReadStatusFlags.Freeze;
            }

            bool unfroze = false;
            if ((flags & WriteStatusFlags.ClearFreeze) == WriteStatusFlags.ClearFreeze) {
                unfroze = (RFlags & ReadStatusFlags.Freeze) != 0;
                RFlags &= ~ReadStatusFlags.Freeze;
            }

            if (s_ClockOn && (flags & WriteStatusFlags.ClearClockCtr) != 0 && TickSource != null) { m_ClockBase = TickSource(); m_ClockLast = 0; }

            m_Status.RegisterValue = 0;

            OnStatusWrite?.Invoke();

            if (unfroze && m_FrozenPending && s_HonorFreeze) { m_FrozenPending = false; DisplayListEnd(); }
        }

        public WriteStatusFlags WFlags => (WriteStatusFlags)m_Status.RegisterValue;

        public ReadStatusFlags RFlags {
            get => (ReadStatusFlags)m_Status.ReadonlyRegisterValue;
            set => m_Status.ReadonlyRegisterValue = (uint)value;
        }

        public bool IsBusy {
            get {
                return
                    (RFlags & ReadStatusFlags.CmdBusy) == ReadStatusFlags.CmdBusy ||
                    (RFlags & ReadStatusFlags.DmaBusy) == ReadStatusFlags.DmaBusy ||
                    (RFlags & ReadStatusFlags.PipeBusy) == ReadStatusFlags.PipeBusy ||
                    (RFlags & ReadStatusFlags.TmemBusy) == ReadStatusFlags.TmemBusy;
            }
        }

        public bool UseXBus => (RFlags & ReadStatusFlags.XbusDmemDma) == ReadStatusFlags.XbusDmemDma;

        public MemExports ExportPointers() => new MemExports(this);

        public void ReflectStatus() {
            m_Status.ReadonlyRegisterValue = m_Status.RegisterValue;
        }

        public override string Name => "RDP Command Interface";
    }

}