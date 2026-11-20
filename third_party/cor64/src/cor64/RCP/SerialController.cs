using System;
using cor64.Mips;
using cor64.IO;
using NLog;
using cor64.Debugging;
using cor64.PIF;

/*
 0x0480 0000 to 0x048F FFFF  Serial interface (SI) registers:
 ------------------------------------------------------------
        SI_BASE_REG - 0x04800000

        0x0480 0000 to 0x0480 0003  SI_DRAM_ADDR_REG
                 SI DRAM address
          (R/W): [23:0] starting RDRAM address
        0x0480 0004 to 0x0480 0007  SI_PIF_ADDR_RD64B_REG
                 SI address read 64B
            (W): [] any write causes a 64B DMA write
        0x0480 0008 to 0x0480 000B  Reserved
        0x0480 000C to 0x0480 000F  Reserved
        0x0480 0010 to 0x0480 0013  SI_PIF_ADDR_WR64B_REG
                 SI address write 64B
            (W): [] any write causes a 64B DMA read
        0x0480 0014 to 0x0480 0017  Reserved
        0x0480 0018 to 0x0480 001B  SI_STATUS_REG
                 SI status
            (W): [] any write clears interrupt
            (R): [0] DMA busy
                 [1] IO read busy
                 [2] reserved
                 [3] DMA error
                 [12] interrupt
        0x0480 001C to 0x048F FFFF  Unused
*/

namespace cor64.RCP {
    public class SerialController : N64MemoryDevice{
        private MipsInterface m_RcpInterface;

        private readonly static Logger Log = LogManager.GetCurrentClassLogger();

        private readonly MemMappedBuffer m_DramAddressReg = new MemMappedBuffer();
        private readonly MemMappedBuffer m_PifRead64Reg = new MemMappedBuffer(4, MemMappedBuffer.MemModel.SINGLE_WRITEONLY);
        private readonly MemMappedBuffer m_PifWrite64Reg = new MemMappedBuffer(4, MemMappedBuffer.MemModel.SINGLE_WRITEONLY);
        private readonly MemMappedBuffer m_StatusReg = new MemMappedBuffer();

        private readonly DmaEngine m_DmaEngine = new DmaEngine("SI");
        private readonly PIFController m_PIF;

        private const int SIZE = 64;
        private const uint ADDR = 0x1FC007C0;
        private const int OFFSET = 0x7C0;

        public event Action ReadJoycons;

        public SerialController(N64MemoryController controller) : base(controller, 0x100000)
        {
            m_PIF = controller.PIF;

            StaticMap(m_DramAddressReg, m_PifRead64Reg);
            StaticMap(8);
            StaticMap(m_PifWrite64Reg);
            StaticMap(4);
            StaticMap(m_StatusReg);

            m_StatusReg.Write += StatusChanged;

            // PIF RAM -> DRAM
            m_PifRead64Reg.Write += PifDmaRead;

            // DRAM -> PIF RAM
            m_PifWrite64Reg.Write += PifDmaWrite;
        }

        public override string Name => "Serial Interface";

        public void AttachInterfaces(MipsInterface rcpInterface) {
            m_RcpInterface = rcpInterface;
        }

        public override void AttachDma() {
            m_DmaEngine.AttachMemory(ParentController.RDRAM, ParentController.PIF);
        }

        public void Init() {
           
        }

        public void SignalSiReady() {
            m_RcpInterface.SetInterrupt(MipsInterface.INT_SI, true);
            m_StatusReg.ReadonlyRegisterValue |= 0x1000;
        }

        private void StatusChanged() {
            m_RcpInterface.ClearInterrupt(MipsInterface.INT_SI);
            m_StatusReg.ReadonlyRegisterValue &= ~0x1000U;
        }

        private void PifDmaWrite() {
            // RDRAM -> PIF RAM

            uint dram = m_DramAddressReg.RegisterValue & 0x1FFFFFFF;

            // Log.Debug("WR64 = {0:X8}", m_PifWrite64Reg.RegisterValue);

            m_DmaEngine.StartMonitoring(true, dram, OFFSET, SIZE);

            m_DmaEngine.DirectCopy_DramToRcp((int)dram, OFFSET, SIZE);
            // m_PIF.ReadCommandByte();
            // PrintRam();

            m_DmaEngine.StopMonitoring();

            SiComplete();
        }

        private void PifDmaRead() {
            // PIF RAM -> RDRAM

            uint dram = m_DramAddressReg.RegisterValue & 0x1FFFFFFF;

            // The PIF command is read before DMA starts
            // VR4300 preps PIF RAM (command + argument)
            // PIF process's the request, the response is written back into PIF RAM before the DMA transfer
            m_PIF.ReadCommandByte();
            ReadJoycons?.Invoke();
            m_PIF.ProcessPifCommands();

            m_DmaEngine.StartMonitoring(false, OFFSET, dram, SIZE);

            m_DmaEngine.DirectCopy_RcpToDram(OFFSET, (int)dram, SIZE);

            m_DmaEngine.StopMonitoring();

            SiComplete();
        }

        // [catalog_fix4] Per-game PI/SI DMA duration (COR64_CATFIX4_DMADUR: unset = table, 0 = off, 1 = every game): the
        // COR64_TIMING_DMADUR busy model (PI and SI DMAs take time before their interrupt, ares pi/dma.cpp, si/io.cpp)
        // for games that need it (GT 64 / Body Harvest are covered by catalog_fix3's COR64_SI_DMADUR). Cruis'n USA froze
        // on its copyright screen; with DMA durations it reaches the Controller Pak notice. Rat Attack reads a pool pointer (8017E34C) that another thread sets while the main thread waits for a
        // DMA; instant DMAs reverse that order (mupen64plus patches the pointer with a boot cheat: fact only).
        public static bool SiDurationGame;
        private static readonly string s_SiDurEnv = System.Environment.GetEnvironmentVariable("COR64_CATFIX4_DMADUR");
        private static readonly System.Collections.Generic.HashSet<string> s_SiDurDb = new() { "RT" /* Rat Attack */, "CU" /* Cruis'n USA */ };
        public static bool SiDurationFor(string gameCode) {
            if (s_SiDurEnv == "0") return false;
            if (s_SiDurEnv == "1") return true;
            return gameCode != null && gameCode.Length >= 3 && s_SiDurDb.Contains(gameCode.Substring(1, 2));
        }

        // [timing] SI DMA busy for ~4065 RCP cycles (ares si/io.cpp) before the interrupt (COR64_TIMING_DMADUR).
        // [catalog_fix3] COR64_SI_DMADUR (default on; =0 off; =all every non-certified game): the SI DMA busy time above for
        // games that lose a thread when the SI interrupt arrives on the instruction that starts the DMA. Toukon Road ran
        // osPfsChecker, then its pak thread was left RUNNING but on no queue (pc just after the SI_PIF_ADDR_WR64B store),
        // and the game sat on "checking Controller Pak" for ever. Real hardware takes ~4065 RCP cycles (ares si/io.cpp).
        public static bool SiDmaDurationGame;
        private static readonly string s_SiDur = Environment.GetEnvironmentVariable("COR64_SI_DMADUR") ?? "1";
        private static readonly string[] s_SiDurGames = { "TO", "BH", "GC" }; // Toukon Road - Brave Spirits, Body Harvest, GT 64
        public static void ApplyGameDefaults(string gameCode, bool certified)
        {
            SiDmaDurationGame = false;
            if (s_SiDur == "0" || certified || gameCode == null || gameCode.Length < 3) return;
            SiDmaDurationGame = s_SiDur == "all" || Array.IndexOf(s_SiDurGames, gameCode.Substring(1, 2)) >= 0;
        }

        private void SiComplete()
        {
            if (!MipsInterface.DmaDuration && !SiDmaDurationGame && !SiDurationGame) { SignalSiReady(); return; }
            m_StatusReg.ReadonlyRegisterValue |= 1; // DMA busy
            m_RcpInterface.ScheduleSi(4065L * 3 / 2 / cor64.Telemetry.ViSpeedupEff, () => {
                m_StatusReg.ReadonlyRegisterValue &= ~1u;
                SignalSiReady();
            });
        }

        private void PrintRam() {
            Console.WriteLine("\n------------------------------");
            var ram = m_PIF.ReadRam();
            for (int i = 0; i < 64; i++) {
                Console.Write(ram[i].ToString("X2"));
            }
            Console.WriteLine("\n------------------------------\n");
        }
    }
}