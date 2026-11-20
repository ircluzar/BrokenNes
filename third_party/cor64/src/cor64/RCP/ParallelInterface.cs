using System.Diagnostics;
using cor64.Debugging;
using cor64.IO;
using cor64.Mips;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace cor64.RCP
{
    /* Parallel Interface for Cartridge and DiskDrive */

    /*
     0x0460 0000 to 0x046F FFFF  Peripheral interface (PI) registers:
     ----------------------------------------------------------------
        PI_BASE_REG - 0x04600000

        0x0460 0000 to 0x0460 0003  PI_DRAM_ADDR_REG
                 PI DRAM address
           (RW): [23:0] starting RDRAM address
        0x0460 0004 to 0x0460 0007  PI_CART_ADDR_REG
                 PI pbus (cartridge) address
           (RW): [31:0] starting AD16 address
        0x0460 0008 to 0x0460 000B  PI_RD_LEN_REG
                 PI read length
           (RW): [23:0] read data length
        0x0460 000C to 0x0460 000F  PI_WR_LEN_REG
                 PI write length
           (RW): [23:0] write data length
        0x0460 0010 to 0x0460 0013 PI_STATUS_REG
                 PI status
            (R): [0] DMA busy             (W): [0] reset controller
                 [1] IO busy                       (and abort current op)
                 [2] error                     [1] clear intr
        0x0460 0014 to 0x0460 0017  PI_BSD_DOM1_LAT_REG or PI_DOMAIN1_REG
                 PI dom1 latency
           (RW): [7:0] domain 1 device latency
        0x0460 0018 to 0x0460 001B  PI_BSD_DOM1_PWD_REG
                 PI dom1 pulse width
           (RW): [7:0] domain 1 device R/W strobe pulse width
        0x0460 001C to 0x0460 001F  PI_BSD_DOM1_PGS_REG
                 PI dom1 page size
           (RW): [3:0] domain 1 device page size
        0x0460 0020 to 0x0460 0023  PI_BSD_DOM1_RLS_REG
                 PI dom1 release
           (RW): [1:0] domain 1 device R/W release duration
        0x0460 0024 to 0x0460 0027  PI_BSD_DOM2_LAT_REG or PI_DOMAIN2_REG
                 PI dom2 latency
           (RW): [7:0] domain 2 device latency
        0x0460 0028 to 0x0460 002B  PI_BSD_DOM2_PWD_REG
                 PI dom2 pulse width
           (RW): [7:0] domain 2 device R/W strobe pulse width
        0x0460 002C to 0x0460 002F  PI_BSD_DOM2_PGS_REG
                 PI dom2 page size
           (RW): [3:0] domain 2 device page size
        0x0460 0030 to 0x0460 0033  PI_BSD_DOM2_RLS_REG
                 PI dom2 release
           (RW): [1:0] domain 2 device R/W release duration
        0x0460 0034 to 0x046F FFFF  Unused
    */
    public class ParallelInterface : N64MemoryDevice
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private MipsInterface m_RcpInterface;
        private MemMappedBuffer m_DramAddress = new MemMappedBuffer(4, MemMappedBuffer.MemModel.SINGLE_READ_WRITE);
        private MemMappedBuffer m_CartAddress = new MemMappedBuffer(4);
        private MemMappedBuffer m_ReadLen = new MemMappedBuffer(4);
        private MemMappedBuffer m_WriteLen = new MemMappedBuffer(4);
        private MemMappedBuffer m_Status = new MemMappedBuffer(4, MemMappedBuffer.MemModel.DUAL_READ_WRITE);
        private MemMappedBuffer m_Dom1Lat = new MemMappedBuffer(4);
        private MemMappedBuffer m_Dom1Pwd = new MemMappedBuffer(4);
        private MemMappedBuffer m_Dom1Pgs = new MemMappedBuffer(4);
        private MemMappedBuffer m_Dom1Rls = new MemMappedBuffer(4);
        private MemMappedBuffer m_Dom2Lat = new MemMappedBuffer(4);
        private MemMappedBuffer m_Dom2Pwd = new MemMappedBuffer(4);
        private MemMappedBuffer m_Dom2Pgs = new MemMappedBuffer(4);
        private MemMappedBuffer m_Dom2Rls = new MemMappedBuffer(4);

        private readonly DmaEngine m_DmaEngine = new DmaEngine("PI");

        public ParallelInterface(N64MemoryController controller) : base(controller, 0x100000)
        {
            m_ReadLen.Write += ReadLengthWrite;
            m_WriteLen.Write += WriteLengthWrite;
            m_Status.Write += StatusWrite;

            StaticMap(
                m_DramAddress, m_CartAddress, m_ReadLen, m_WriteLen,
                m_Status,
                m_Dom1Lat, m_Dom1Pwd, m_Dom1Pgs, m_Dom1Rls,
                m_Dom2Lat, m_Dom2Pwd, m_Dom2Pgs, m_Dom2Rls);
        }

        public void AttachInterface(MipsInterface rcpInterface) {
            m_RcpInterface = rcpInterface;
        }

        public override void AttachDma() {
            m_DmaEngine.AttachMemory(ParentController.RDRAM, ParentController.Cart);
        }

        private void StatusWrite() {

            // Reset controller
            if ((m_Status.RegisterValue & 1) != 0) {
                m_Status.ReadonlyRegisterValue = 0;
            }

            if ((m_Status.RegisterValue & 0b10) != 0) {
                // Clear PI interrupt bit write
                m_RcpInterface.ClearInterrupt(MipsInterface.INT_PI);
                m_Status.ReadonlyRegisterValue &= ~8U;
                // Log.Debug("Clear PI interrupt");
            }

            // Undocumented behavior
            if ((m_Status.RegisterValue & 8) != 0) {
                m_Status.ReadonlyRegisterValue |=8;
            }

            m_Status.RegisterValue = 0;
        }

        private static readonly bool s_PiLog = System.Environment.GetEnvironmentVariable("COR64_CPUDIFF_PILOG") == "1"; // [cpu_diff] debug: log every cart->RDRAM DMA
        // [cpu_diff] PI cart->RDRAM DMA whose cart and RDRAM addresses differ mod 4 (e.g. Mario Party's file table at
        // cart 0x10ACC36A -> RDRAM 0x1F8050) copied raw host bytes, so the data came out shifted and scrambled. Such blocks
        // now copy a big-endian byte stream. COR64_CPUDIFF_PIBE=0 = old.
        private static readonly bool s_PiBe = System.Environment.GetEnvironmentVariable("COR64_CPUDIFF_PIBE") != "0";
        private static readonly int s_PiStall = int.TryParse(System.Environment.GetEnvironmentVariable("COR64_DEVDMA_PISTALL"), out var ps) ? ps : 0;
        // [keys_leak] A PI DMA from domain 2 address 2 (0x08000000-0x0FFFFFFF) on a cart with no SRAM/FlashRAM (EEPROM games,
        // e.g. JFG's FlashRAM status probe) read the ROM device at a bogus offset; on hardware nothing answers, so it is
        // open bus like the 64DD ranges (ares pi/dma.cpp + pi/bus.hpp). COR64_KEYS_DOM2OPENBUS=0 = old.
        private static readonly bool s_Dom2OpenBus = System.Environment.GetEnvironmentVariable("COR64_KEYS_DOM2OPENBUS") != "0";
        // [ingame_fix3] COR64_INGFIX3_PIDEFER (default on for the listed games; =0 off; =all every game): a cart->RDRAM DMA
        // writes RDRAM when its modelled transfer time (PiComplete's ares-based formula) has passed, not on the PI_WR_LEN
        // store. Automobili Lamborghini (NLC) queues its last menu display list (which calls a sprite DL at 0x801F5540) and at
        // once DMAs the race data (0x96450 bytes) over the menu data. On hardware the RSP has long finished that list before
        // the PI gets there (~130 ms); with an instant copy the list ran on race texture bytes, the RDP took them as commands
        // and drew 32-bit images over low RDRAM (exception vectors, the decompressor at 0x80068400): the race-load crash.
        public static bool DeferGame;
        private static readonly string s_DeferEnv = System.Environment.GetEnvironmentVariable("COR64_INGFIX3_PIDEFER");
        public static bool DeferFor(string gameCode) {
            if (s_DeferEnv == "0") return false;
            if (s_DeferEnv == "all") return true;
            return gameCode != null && gameCode.Length >= 3 && gameCode.Substring(1, 2) == "LC";
        }
        private bool m_InDefer;
        private void WriteLengthWrite()
        {
            if (DeferGame && !m_InDefer && Dom2For(m_CartAddress.RegisterValue) == null) {
                uint sd = m_DramAddress.RegisterValue, sc = m_CartAddress.RegisterValue, sl = m_WriteLen.RegisterValue;
                int len = ((int)sl & 0xFFFFFF) + 1;
                long rcp = 80 * (1 + (len + 0x7FF) / 0x800) + len * 27L / 2;
                m_Status.ReadonlyRegisterValue |= 1; // DMA busy
                m_RcpInterface.SchedulePi(rcp * 3 / 2 / cor64.Telemetry.ViSpeedupEff, () => {
                    m_DramAddress.RegisterValue = sd; m_CartAddress.RegisterValue = sc; m_WriteLen.RegisterValue = sl;
                    m_InDefer = true; try { WriteLengthWrite(); } finally { m_InDefer = false; }
                });
                return;
            }
            uint dest = m_DramAddress.RegisterValue & 0x7FFFFE;
            uint source = m_CartAddress.RegisterValue & 0xFFFFFFE;
            int length = ((int)m_WriteLen.RegisterValue & 0xFFFFFF) + 1;
            int dmaTotal = length;

            m_DmaEngine.StartMonitoring(false, source, dest, length);
            var dom2 = Dom2For(m_CartAddress.RegisterValue);
            // [keys_leak] open-bus PI DMA (nothing attached) is emulated: not an unimplemented feature
            bool openBusDma = OpenBusMemory.Enabled && dom2 == null && (m_CartAddress.RegisterValue - 0x05000000u < (s_Dom2OpenBus ? 0x0B000000u : 0x03000000u));
            if (CompatLog.On && dom2 == null && !openBusDma) CompatLog.PiDma(m_CartAddress.RegisterValue, dest, length, false);
            if (s_PiLog) { System.IO.Directory.CreateDirectory("telemetry"); System.IO.File.AppendAllText("telemetry/pi.txt", $"cart={m_CartAddress.RegisterValue:X8} dram={m_DramAddress.RegisterValue:X8} len={length:X} ra={(uint)(cor64.Mips.R4300I.InterpreterBaseR4300I.DbgInst?.ReadGPR64(31) ?? 0):X8} sp={(uint)(cor64.Mips.R4300I.InterpreterBaseR4300I.DbgInst?.ReadGPR64(29) ?? 0):X8} vi={cor64.Telemetry.Ticks}\n"); }

            // PI_WR_LEN_REG has a weird behavior when read back. It almost always
            // reads as 0x7F, with the only exception of very short transfers (<= 8
            // bytes) where the actual value is affected by the DRAM alignment. This
            // is just for full accuracy, nobody is probably relying on this value.
            m_WriteLen.RegisterValue = 0x7F;
            if (length <= 8)
                m_WriteLen.RegisterValue -= m_DramAddress.RegisterValue & 7;

            // PI DMA has an internal cache of 128 bytes ("a block"). Data is fetched
            // from ROM and then copied to RDRAM. The first block is handled "specially":
            // if the RDRAM address is not a multiple of 8, the block is shorter so
            // that the RDRAM address becomes a multiple of 8 afterwards, and a faster
            // code-path is triggered (possibly, 64-bit transfers to RDRAM).
            // This is visible because this feature is actually broken: there are two
            // bugs lingering, so that in the end Nintendo documented that only
            // 8-bytes aligned transfers were possible.
            byte[] block = new byte[128];
            bool firstBlock = true;

            int register_dram = (int)m_DramAddress.RegisterValue;
            int register_cart = (int)m_CartAddress.RegisterValue;
            uint dmaStart = m_CartAddress.RegisterValue;
            bool openBus = openBusDma;

            while (length > 0)
            {
                dest = (uint)register_dram & 0x7FFFFEU;
                int misalign = (int)dest & 0x7;

                int cur_len = length;
                int block_len = 128 - misalign;
                if (cur_len > block_len)
                    cur_len = block_len;

                // Decrease length (for next block). After first block, odd sizes
                // are round up.
                length -= cur_len;
                if ((length & 1) == 1) length += 1;

                // Fetch block from ROM. ROM is always fetched as 16-bit words,
                // so round up the actual transfer.
                source = (uint)register_cart & 0xFFFFFFEU;
                int rom_fetch_len = (cur_len + 1) & ~1;
                // Writeback length to RDRAM. Here come the lions.
                int wr_len = cur_len;
                if (firstBlock)
                {
                    // HARDWARE BUG #1: in the first block, there's an off-by-one, so the
                    // length is actually rounded up to even size just for the last byte.
                    // Notice that ROM transfers are rounded up anyway, so this additional
                    // byte was already fetched from ROM.
                    if (wr_len == block_len - 1)
                        wr_len++;

                    // HARDWARE BUG #2: the length of data written back is decreased by the
                    // RDRAM misalignment. This is wrong because cur_len was already
                    // clamped to the block length, so this actually ends up leaving a
                    // hole in RDRAM of non-transferred data at the end of the first block.
                    wr_len -= misalign;
                    if (wr_len < 0)
                        wr_len = 0;
                }

                // [cpu_diff] copy a big-endian byte stream unless the block is whole words on both sides (the raw host
                // copy is only right then). [pibe_saves] this also covers same-alignment blocks that are not whole
                // words (e.g. SSB's 2-byte ROM reads at word-aligned addresses), and SRAM/FlashRAM, which store BE bytes.
                bool beCopy = s_PiBe && (dom2 != null || ((source | dest) & 3) != 0 || ((rom_fetch_len | wr_len) & 3) != 0);
                if (openBus) { OpenBusMemory.FillDma(dmaStart, (uint)register_cart, block, rom_fetch_len); beCopy = true; } // [dev_dma] no 64DD
                else if (dom2 != null) dom2.DmaRead(source, block, 0, rom_fetch_len, dmaStart & 0xFFFFFFEU); // [cer64plus] SRAM / FlashRAM (BE bytes)
                else if (beCopy) m_DmaEngine.ReadRcpBE((int)source, block, rom_fetch_len);
                else m_DmaEngine.ReadRcp((int)source, block, 0, rom_fetch_len);
                register_cart += rom_fetch_len;
                cur_len = wr_len;

                if (beCopy) m_DmaEngine.WriteDramBE((int)dest, block, cur_len);
                else m_DmaEngine.WriteDram((int)dest, block, 0, cur_len);
                register_dram += cur_len;
                register_dram = (register_dram + 7) & ~7;

                firstBlock = false;
            }

            if (s_PiLog) { var chk = new byte[0x80]; m_DmaEngine.ReadDram((int)(m_DramAddress.RegisterValue & 0x7FFFF8), chk, 0, chk.Length); System.IO.File.AppendAllText("telemetry/pi.txt", "  ram " + Convert.ToHexString(chk) + "\n"); }
            m_DramAddress.RegisterValue = (uint)register_dram;
            m_CartAddress.RegisterValue = (uint)register_cart;
            // [dev_dma] EXPERIMENT (off by default): COR64_DEVDMA_PISTALL=<count ticks per byte> advances CP0 Count as if the
            // cart->RDRAM DMA took real time (~6 MB/s on hardware = ~8 ticks/byte). Count only: VI/AI/scheduler do not move.
            if (s_PiStall > 0) cor64.Mips.R4300I.ControlRegisters.s_LastForDmaExperiment?.AddCount((uint)(s_PiStall * dmaTotal));

            m_DmaEngine.StopMonitoring();
            PiComplete((int)m_WriteLen.RegisterValue + 1);
        }

        // [cer64plus] the save device when the cart address is on domain 2, address 2 (0x08000000-0x0FFFFFFF)
        private CartSave Dom2For(uint cart) => (cart - 0x08000000u) < 0x08000000u ? ParentController.Dom2 : null;

        private void ReadLengthWrite()
        {
            var dom2 = Dom2For(m_CartAddress.RegisterValue);
            if (dom2 != null) { Dom2Write(dom2); return; }
            // [keys_leak] RDRAM->cart DMA to a PI range with nothing attached (no 64DD, or domain 2 on an EEPROM cart, e.g. JFG's
            // FlashRAM command writes) is dropped on hardware; it used to be written into the ROM device. Timing/interrupt unchanged.
            bool openBusDma = OpenBusMemory.Enabled && s_Dom2OpenBus && m_CartAddress.RegisterValue - 0x05000000u < 0x0B000000u;
            if (CompatLog.On && !openBusDma) CompatLog.PiDma(m_CartAddress.RegisterValue, m_DramAddress.RegisterValue, (int)m_WriteLen.RegisterValue + 1, true);
            m_DramAddress.RegisterValue &= ~0x80000000U;
            m_CartAddress.RegisterValue &= ~0x10000000U;

            m_DramAddress.RegisterValue &= ~1U;
            m_CartAddress.RegisterValue &= ~1U;

            SourceAddress = m_DramAddress.RegisterValue;
            DestAddress = m_CartAddress.RegisterValue;
            int size = (int)m_WriteLen.RegisterValue + 1;

            m_DmaEngine.StartMonitoring(true, SourceAddress, DestAddress, size);

            if (!openBusDma) {
                byte[] buffer = new byte[size];
                m_DmaEngine.ReadDram((int)SourceAddress, buffer, 0, buffer.Length);
                m_DmaEngine.WriteRcp((int)DestAddress, buffer, 0, buffer.Length);
            }

            // Force length alignment
            // size = (size + 7U) & ~7U;

            // if ((size % 8) == 0) {
            //     var off = (int)(DestAddress % 8);

            //     if (off != 0) {
            //         TransferBytesUnaligned(size - off);
            //     }
            //     else {
            //         TransferBytes(size);
            //     }
            // }
            // else {
            //     TransferBytesUnaligned(size);
            // }


            m_DmaEngine.StopMonitoring();

            m_DramAddress.RegisterValue += (uint)size;
            m_CartAddress.RegisterValue += (uint)size;

            // This helps pass some DMA unalignement tests..
            m_DramAddress.RegisterValue++;
            m_CartAddress.RegisterValue++;

            m_DramAddress.WriteNotify();
            m_CartAddress.WriteNotify();

            // Some strange address alignment/shift happens here

            PiComplete(size);
        }

        // [cer64plus] RDRAM -> SRAM/FlashRAM: length from PI_RD_LEN (the ROM path below keeps its historic behaviour).
        private void Dom2Write(CartSave dom2)
        {
            uint dram = m_DramAddress.RegisterValue & 0x7FFFFEu;
            uint cart = m_CartAddress.RegisterValue & ~1u;
            int size = (int)(m_ReadLen.RegisterValue & 0xFFFFFF) + 1;
            size = (size + 1) & ~1;
            byte[] buffer = new byte[size];
            if (s_PiBe) m_DmaEngine.ReadDramBE((int)dram, buffer, size); // [pibe_saves] save devices hold BE bytes
            else m_DmaEngine.ReadDram((int)dram, buffer, 0, size);
            dom2.DmaWrite(cart, buffer, size);
            m_DramAddress.RegisterValue = (dram + (uint)size + 7) & ~7u;
            m_CartAddress.RegisterValue = cart + (uint)size;
            PiComplete(size);
        }

        // [timing] DMA busy until the transfer time has passed (COR64_TIMING_DMADUR). Duration after ares pi/dma.cpp with the
        // usual domain-1 timing (latency 0x40, pulse 0x12, release 0x07): ~80 RCP cycles per page setup plus 13.5 per byte;
        // CPU ticks = RCP cycles * 1.5, divided by the VI speed-up so it keeps its share of a frame.
        private void PiComplete(int len)
        {
            if (m_InDefer) { m_Status.ReadonlyRegisterValue &= ~1u; m_RcpInterface.SetInterrupt(MipsInterface.INT_PI, true); return; } // [ingame_fix3] time already spent
            if (!MipsInterface.DmaDuration && !SerialController.SiDurationGame) { m_RcpInterface.SetInterrupt(MipsInterface.INT_PI, true); return; }
            long pages = 1 + (len + 0x7FF) / 0x800;
            long rcp = 80 * pages + len * 27L / 2;
            m_Status.ReadonlyRegisterValue |= 1; // DMA busy
            m_RcpInterface.SchedulePi(rcp * 3 / 2 / cor64.Telemetry.ViSpeedupEff, () => {
                m_Status.ReadonlyRegisterValue &= ~1u;
                m_RcpInterface.SetInterrupt(MipsInterface.INT_PI, true);
            });
        }

        public override string Name => "Parallel Interface";
    }
}
