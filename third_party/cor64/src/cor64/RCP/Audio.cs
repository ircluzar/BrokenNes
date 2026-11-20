using cor64.IO;
using cor64.Mips;
using NLog;
using System;

namespace cor64.RCP
{
    /*
        0x0450 0000  AI_DRAM_ADDR_REG  (W) [23:0] starting RDRAM address (8B-aligned)
        0x0450 0004  AI_LEN_REG       (RW) [17:0] transfer length, bottom 3 bits ignored
        0x0450 0008  AI_CONTROL_REG    (W) [0] DMA enable
        0x0450 000C  AI_STATUS_REG     (R) [31]/[0] full, [30] busy, [25] enabled  (W) clear AI interrupt
        0x0450 0010  AI_DACRATE_REG    (W) [13:0] vid_clock/(dacrate + 1) is the sample rate
        0x0450 0014  AI_BITRATE_REG    (W) [3:0]
    */

    // [cer64plus] AI rewritten after ares (n64/ai): 2-entry DMA FIFO, interrupt when a buffer is queued into an empty FIFO
    // and when playback advances to the second buffer; samples are clocked against guest time and handed to AudioSink.
    public class Audio : N64MemoryDevice
    {
        // [cer64plus] APU slot: where played samples go (set by N64System; not part of savestates).
        [NonSerialized] public cor64.Cores.IN64ApuCore Apu;

        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private readonly MemMappedBuffer m_Dram = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_Length = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_Control = new MemMappedBuffer(4, MemMappedBuffer.MemModel.SINGLE_WRITEONLY);
        private readonly MemMappedBuffer m_Status = new MemMappedBuffer(4, MemMappedBuffer.MemModel.DUAL_READ_WRITE);
        private readonly MemMappedBuffer m_DigitalToAudioRate = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_BitRate = new MemMappedBuffer(4);
        private readonly MipsInterface m_Interface;

        private const long VideoClockNtsc = 48681812;
#if FASTER_VI
        // FASTER_VI advances video timing ViSpeedup cycles per tick (see Video.Tick), so a guest second is 93.75M / ViSpeedup ticks.
        private static long GuestTicksPerSecond => 93750000 / Telemetry.ViSpeedup; // [ingame_fix2] per-cart (Telemetry.ViSpeedupFor)
#else
        private const long GuestTicksPerSecond = 93750000;
#endif

        private readonly uint[] m_FifoAddr = new uint[2];
        private readonly uint[] m_FifoLen = new uint[2];
        private int m_FifoCount;
        // [catalog_fix7] AI DMA "delayed carry" (COR64_CATFIX7_AICARRY, default on; =0 off): when a DMA that ends on an 8 KB
        // boundary is followed by another, the hardware adds 0x2000 to the next DMA's address; libultra's osAiSetNextBuffer
        // pre-subtracts 0x2000 for that case. Without it Twisted Edge (one 8 KB ring at 803F4000 re-queued forever) plays
        // 3F2000 instead (silence). Behaviour as in mupen64plus/gopher64 ai (delayed_carry), reimplemented here.
        private static readonly bool s_AiCarry = Environment.GetEnvironmentVariable("COR64_CATFIX7_AICARRY") != "0";
        private bool m_DelayedCarry;
        private void StartDma()
        {
            if (!s_AiCarry) return;
            if (m_DelayedCarry) m_FifoAddr[0] = (m_FifoAddr[0] + 0x2000) & 0xFFFFF8;
            m_DelayedCarry = ((m_FifoAddr[0] + m_FifoLen[0]) & 0x1FFF) == 0;
        }
        private bool m_DmaEnable;
        private long m_Frequency = 32000;
        private long m_Acc;

        public long Frequency => m_Frequency;

        private static readonly bool s_Acc2Log = Environment.GetEnvironmentVariable("COR64_ACC2_AILOG") == "1";
        private static readonly bool s_LogBuffers = Environment.GetEnvironmentVariable("COR64_TRACE_AI") == "1";

        public Audio(N64MemoryController controller, MipsInterface mipsInterface) : base(controller, 0x100000)
        {
            StaticMap(m_Dram, m_Length, m_Control, m_Status, m_DigitalToAudioRate, m_BitRate);
            m_Interface = mipsInterface;

            m_Dram.Write += () => {
                if (m_FifoCount < 2) m_FifoAddr[m_FifoCount] = m_Dram.RegisterValue & 0xFFFFF8;
            };
            m_Length.Write += LengthWrite;
            m_Control.Write += () => { m_DmaEnable = (m_Control.RegisterValue & 1) != 0; UpdateStatus(); };
            m_Status.Write += () => { m_Interface.ClearInterrupt(MipsInterface.INT_AI); m_Status.RegisterValue = 0; };
            m_DigitalToAudioRate.Write += () => {
                uint rate = m_DigitalToAudioRate.RegisterValue & 0x3FFF;
                m_Frequency = Math.Max(1, VideoClockNtsc / (rate + 1));
                Telemetry.AiDacRate = rate;
            };
            m_BitRate.Write += () => { Telemetry.AiBitRate = m_BitRate.RegisterValue; };
            UpdateStatus();
        }

        private void LengthWrite()
        {
            uint len = m_Length.RegisterValue & 0x3FFF8;
            Telemetry.AiLenWrites++;
            if (s_Acc2Log && Telemetry.AiLenWrites % 32 == 0) { // [accuracy2] diagnostics only
                int peak = 0; uint a0 = m_Dram.RegisterValue & 0x7FFFF8;
                for (uint o = 0; o < len && a0 + o + 4 <= 0x800000; o += 4) { uint w = ParentController.U32(a0 + o); peak = Math.Max(peak, Math.Max(Math.Abs((int)(short)(w >> 16)), Math.Abs((int)(short)w))); }
                Console.WriteLine($"A2AI n={Telemetry.AiLenWrites} vi={Telemetry.ViInterrupts} addr={a0:X6} len={len} peak={peak} rate={m_Frequency} fifo={m_FifoCount} dma={m_DmaEnable} aud={CompatLog.Acc2AudTasks} gfx={CompatLog.Acc2GfxTasks}");
            }
            Telemetry.AiBytes += len;
            if (m_FifoCount < 2) {
                Telemetry.AiDramAddr = m_FifoAddr[m_FifoCount];
                if (s_LogBuffers) {
                    int peak = 0; uint a0 = m_FifoAddr[m_FifoCount] & 0x7FFFFC;
                    for (uint o = 0; o < len; o += 4) { uint w = ParentController.U32(a0 + o); peak = Math.Max(peak, Math.Max(Math.Abs((int)(short)(w >> 16)), Math.Abs((int)(short)w))); }
                    if (EventLog.Enabled) EventLog.E("AI_BUF", "addr=" + a0.ToString("X") + " len=" + len + " peak=" + peak + " rate=" + m_Frequency);
                }
                if (m_FifoCount == 0) m_Interface.SetInterrupt(MipsInterface.INT_AI, true);
                m_FifoLen[m_FifoCount] = len;
                m_FifoCount++;
                if (m_FifoCount == 1) StartDma();
            }
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            uint st = (1u << 20) | (1u << 24);
            if (m_FifoCount > 1) st |= 1u | (1u << 31);
            if (m_FifoCount > 0) st |= 1u << 30;
            if (m_DmaEnable) st |= 1u << 25;
            m_Status.ReadonlyRegisterValue = st;
            m_Length.RegisterValue = m_FifoCount > 0 ? m_FifoLen[0] : 0;
        }

        // [cer64plus] Advance n ticks at once (batched stepping).
        // [climb4 idle_span] batches of n ticks until the first one that takes a sample (>= 1)
        public long BatchesUntilSample(int n)
        {
            long f = m_Frequency * n;
            if (f <= 0) return long.MaxValue;
            long b = FastDiv.Div(GuestTicksPerSecond - m_Acc + f - 1, f);
            return b < 1 ? 1 : b;
        }

        public void Tick(int n)
        {
            m_Acc += m_Frequency * n;
            while (m_Acc >= GuestTicksPerSecond) {
                m_Acc -= GuestTicksPerSecond;
                Sample();
            }
        }

        // Called once per emulated CPU tick.
        public void Tick()
        {
            m_Acc += m_Frequency;
            if (m_Acc < GuestTicksPerSecond) return;
            m_Acc -= GuestTicksPerSecond;
            Sample();
        }

        private void Sample()
        {
            short l = 0, r = 0;
            if (m_FifoCount > 0 && m_FifoLen[0] > 0 && m_DmaEnable) {
                uint w = ParentController.U32(m_FifoAddr[0] & 0x7FFFFC);
                l = (short)(w >> 16);
                r = (short)w;
                m_FifoAddr[0] += 4;
                m_FifoLen[0] -= 4;
                m_Length.RegisterValue = m_FifoLen[0];
            }

            if (m_FifoCount > 0 && m_FifoLen[0] == 0) {
                m_FifoCount--;
                if (m_FifoCount > 0) {
                    m_FifoAddr[0] = m_FifoAddr[1];
                    m_FifoLen[0] = m_FifoLen[1];
                    StartDma();
                    m_Interface.SetInterrupt(MipsInterface.INT_AI, true);
                }
                else m_DelayedCarry = false;
                UpdateStatus();
            }

            Apu?.Push(l, r, (int)m_Frequency);
        }

        public override string Name => "Audio Interface";
    }
}
