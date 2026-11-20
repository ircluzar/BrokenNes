using System.Runtime.InteropServices;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using cor64.Debugging;
using cor64.Mips;
using cor64.PIF;
using cor64.RCP;
using NLog;
using static cor64.N64CTS;
using cor64.IO;

/*
    0x0000 0000 to 0x03EF FFFF RDRAM Memory
    0x03F0 0000 to 0x03FF FFFF RDRAM Registers
    0x0400 0000 to 0x040F FFFF SP Registers
    0x0410 0000 to 0x041F FFFF DP Command Registers
    0x0420 0000 to 0x042F FFFF DP Span Registers
    0x0430 0000 to 0x043F FFFF MIPS Interface (MI) Registers
    0x0440 0000 to 0x044F FFFF Video Interface (VI) Registers
    0x0450 0000 to 0x045F FFFF Audio Interface (AI) Registers
    0x0460 0000 to 0x046F FFFF Peripheral Interface (PI) Registers
    0x0470 0000 to 0x047F FFFF RDRAM Interface (RI) Registers
    0x0480 0000 to 0x048F FFFF Serial Interface (SI) Registers
    0x0490 0000 to 0x04FF FFFF Unused
    0x0500 0000 to 0x05FF FFFF Cartridge Domain 2 Address 1
    0x0600 0000 to 0x07FF FFFF Cartridge Domain 1 Address 1
    0x0800 0000 to 0x0FFF FFFF Cartridge Domain 2 Address 2
    0x1000 0000 to 0x1FBF FFFF Cartridge Domain 1 Address 2
    0x1FC0 0000 to 0x1FC0 07BF PIF ROM
    0x1FC0 07C0 to 0x1FC0 07FF PIF RAM
    0x1FC0 0800 to 0x1FCF FFFF Reserved
    0x1FD0 0000 to 0x7FFF FFFF Cartridge Domain 1 Address 3
    0x8000 0000 to 0xFFFF FFFF External SysAD Device
*/

namespace cor64
{
    public class N64MemoryController : IMemoryAccess
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private MemMappedCart m_CartRom; // Domain 1, Address 2 region
        private readonly static MemoryDebugger s_MemDebugger = new MemoryDebugger();
        private readonly N64MemoryDevice[] m_MemMap = new N64MemoryDevice[0xFFFF];
        private readonly uint[] m_BaseMap = new uint[0xFFFF];
        // private readonly UnifiedMemModel<N64MemoryDevice> m_MemModel = new();
        private readonly object m_WriteLock = new();

        [ThreadStatic] private readonly static byte[] s_ReadSingle = { 0 };
        [ThreadStatic] private readonly static byte[] s_WriteSingle = { 0 };

        private readonly UnmanagedBuffer m_ValueBuffer = new UnmanagedBuffer(8);

        public N64MemoryController()
        {
            DummyMemory internalDummy = new DummyMemory(8, "Internal dummy (should never hit this)", this);
            for (int i = 0; i < m_MemMap.Length; i++)
            {
                m_MemMap[i] = internalDummy;
            }

            Cart = new DummyMemory(0xFC00000, "Dummy cartridge", this);
            RDRAM = new Rdram(this);
            RIRegs = new RdramInterface(this);
            RDRAMRegs = new RdramRegisters(this);
            PIF = new PIFController(this);
            DiskDriveRegisters = OpenBusMemory.Enabled ? new OpenBusMemory(0x1000000, "Cartridge Domain 2, Address 1 (no 64DD)", this) : new DummyMemory(0x1000000, "Cartridge Domain 2, Address 1", this);
            Unused_0490 = new DummyMemory(0x00700000, "Unused 0490", this);
            Unused_0600 = OpenBusMemory.Enabled ? new OpenBusMemory(0x02000000, "Cartridge Domain 1 Address 1 (no 64DD IPL)", this) : new DummyMemory(0x02000000, "Cartridge Domain 1 Address 1", this);
            // [keys_leak] domain 1 address 3 (0x1FD00000-0x7FFFFFFF) is PI bus with nothing attached on a retail console: open bus
            // like the 64DD ranges (ares memory/bus.hpp routes it to pi.read; pi/bus.hpp busAddress). SF64 reads 0x1FF00000.
            // COR64_KEYS_DOM1A3OPENBUS=0 = old (reads 0).
            Unused_1FD0 = OpenBusMemory.Enabled && Environment.GetEnvironmentVariable("COR64_KEYS_DOM1A3OPENBUS") != "0" ? new OpenBusMemory(0x1000000, "Cartridge Domain 1 Address 3 (open bus)", this) : new DummyMemory(0x1000000, "Cartridge Domain 1 Address 3", this);  /* 0x60300000 is too large */
            // [jfg_crash] domain 2 with no SRAM/FlashRAM (EEPROM or no save): open bus, writes dropped (ares pi/bus.hpp).
            // It used to fall into the 8-byte internal dummy, so JFG's FlashRAM probe store at 0x08010000 threw.
            Dom2Empty = OpenBusMemory.Enabled ? new OpenBusMemory(0x10000, "Cartridge Domain 2 Address 2 (no SRAM/FlashRAM)", this) : new DummyMemory(0x10000, "Cartridge Domain 2 Address 2", this);
        }

        private N64MemoryDevice Dom2Empty;

        public void Init()
        {
            RDRAM?.Init();
            RDRAMRegs?.Init();
            SPRegs?.Init();
            DPCmdRegs?.Init();
            DPSpanRegs?.Init();
            MIRegs?.Init();
            VIRegs?.Init();
            AIRegs?.Init();
            PIRegs?.Init();
            RIRegs?.Init();
            SIRegs?.Init();
            Cart?.Init();
            PIF?.Init();
            DiskDriveRegisters?.Init();
            Unused_0490.Init();
            Unused_0600.Init();
            Unused_1FD0.Init();
            Dom2Empty.Init();

            Map(RDRAM, 0x0000, 0x03EF);
            Map(RDRAMRegs, 0x03F0, 0x03FF);
            Map(SPRegs, 0x0400, 0x040F);
            Map(DPCmdRegs, 0x0410, 0x041F);
            Map(DPSpanRegs, 0x0420, 0x042F);
            Map(MIRegs, 0x0430, 0x043F);
            Map(VIRegs, 0x0440, 0x044F);
            Map(AIRegs, 0x0450, 0x045F);
            Map(PIRegs, 0x0460, 0x046F);
            Map(RIRegs, 0x0470, 0x047F);
            Map(SIRegs, 0x0480, 0x048F);
            Map(Unused_0490, 0x0490, 0x04FF);
            Map(DiskDriveRegisters, 0x0500, 0x05FF);
            Map(Unused_0600, 0x0600, 0x07FF);
            Map(Cart, 0x1000, 0x1FBF);
            Map(PIF, 0x1FC0, 0x1FCF);
            MapMirror(Unused_1FD0, 0x1FD0, 0x7FFF, 0x100); // [jfg_crash] 16 MB device mirrored, no out-of-range offsets
            MapMirror(Dom2Empty, 0x0800, 0x0FFF, 1);
        }

        // [jfg_crash] map [start,end] 64 KB pages onto a device of `pages` pages, repeating it (offsets stay in range).
        private void MapMirror(N64MemoryDevice t, int start, int end, int pages)
        {
            for (int i = start; i <= end; i++)
            {
                m_MemMap[i] = t;
                m_BaseMap[i] = (uint)(i - (i - start) % pages) << 16;
            }
        }

        private void Map(N64MemoryDevice t, int start, int end)
        {
            uint b = (uint)start << 16;

            for (int i = start; i <= end; i++)
            {
                m_MemMap[i] = t;
                m_BaseMap[i] = b;
            }
        }

        public void WriteLockEnter()
        {
            Monitor.Enter(m_WriteLock);
        }

        public void WriteLockExit()
        {
            Monitor.Exit(m_WriteLock);
        }

        [Conditional("SAFE_MEMORY_ACCESS")]
        private void ThrowOnBadIndex(int index)
        {
            if (index >= m_MemMap.Length) throw new InvalidOperationException("N64 memory access is out of range");
        }

        // [dbg_mem] debugger memory watches; null = none armed (then the access paths pay one not-taken branch).
        // Set through cor64.Debug.MemoryWatchSet only. off by default; COR64_DBG_WATCH=1 enables watches.
        [NonSerialized] private cor64.Debug.MemoryWatchSet m_Watches;
        public static readonly bool s_DbgWatch = Environment.GetEnvironmentVariable("COR64_DBG_WATCH") == "1"; // default off
        public cor64.Debug.MemoryWatchSet Watches { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => s_DbgWatch ? m_Watches : null; internal set => m_Watches = value; }

        /// <summary>[dbg_mem] Device and offset for a physical address, with no logging, latch, RDP wait or watch.</summary>
        public N64MemoryDevice PeekDevice(long address, out long deviceOffset)
        {
            int index = (int)((uint)address >> 16);
            if ((uint)index >= (uint)m_MemMap.Length || m_MemMap[index] == null) { deviceOffset = 0; return null; }
            deviceOffset = (uint)address - m_BaseMap[index];
            return m_MemMap[index];
        }

        /// <summary>[dbg_mem] Side-effect-free big-endian read of len (1, 2, 4, 8) bytes; 0 where nothing is mapped.</summary>
        public ulong DebugPeek(long address, int len)
        {
            if (len == 8) return ((ulong)DebugPeek(address, 4) << 32) | DebugPeek(address + 4, 4);
            var d = PeekDevice(address & ~3L, out long off);
            if (d == null || off + 4 > d.Size) return 0;
            uint w = N64Endianess.U32(d.Read32((int)off));
            int sh = 32 - 8 * (int)(address & 3) - 8 * len;
            if (sh < 0) return 0;
            return len == 4 ? w : (w >> sh) & ((1u << (8 * len)) - 1);
        }

        private N64MemoryDevice GetDevice(long address, out long deviceOffset)
        {
            int index = (int)(address >> 16);

            ThrowOnBadIndex(index);
            if (CompatLog.On && !(m_MemMap[index] is OpenBusMemory)) CompatLog.MemAccess(address); // [keys_leak] open bus is emulated

            deviceOffset = address - m_BaseMap[index];

            return m_MemMap[index];
        }

        // private void DebugValue32(long address, byte[] buffer, uint testValue, bool isRead) {
        //     unsafe {
        //         fixed (byte * ptr = &buffer[0]) {
        //             IntPtr p = (IntPtr)ptr;
        //             var v = p.AsType_32Swp();

        //             if (v == testValue) {
        //                 if (isRead) {
        //                     Console.WriteLine("MEM32 DEBUG READ: {0:X8} {1:X8}", (uint)address, v);
        //                 }
        //                 else {
        //                     Console.WriteLine("MEM32 DEBUG WRITE: {0:X8} {1:X8}", (uint)address, v);
        //                 }
        //             }
        //         }
        //     }
        // }

        // public byte ReadPhysical8(long address) {
        //     var device = GetDevice((int)address, out int deviceOffset);
        //     return device.SmartRead<byte>(deviceOffset);
        // }

        // public void WritePhysical(long address, byte value) {
        //     var device = GetDevice((int)address, out int deviceOffset);
        //     device.SmartWrite(deviceOffset, value);
        // }

        // public ushort ReadPhysical16(long address) {
        //     var device = GetDevice((int)address, out int deviceOffset);
        //     return device.SmartRead<ushort>(deviceOffset);
        // }

        // public void WritePhysical(long address, ushort value) {
        //     var device = GetDevice((int)address, out int deviceOffset);
        //     device.SmartWrite(deviceOffset, value);
        // }

        // public uint ReadPhysical32(long address) {
        //     var device = GetDevice((int)address, out int deviceOffset);
        //     return device.SmartRead<uint>(deviceOffset);
        // }

        // public void WritePhysical(long address, uint value) {
        //     var device = GetDevice((int)address, out int deviceOffset);
        //     device.SmartWrite(deviceOffset, value);
        // }

        // public unsafe ulong ReadPhysical64(long address) {
        //     var device = GetDevice((int)address, out int deviceOffset);
        //     return device.SmartRead<ulong>(deviceOffset);
        // }

        // public unsafe void WritePhysical(long address, ulong value) {
        //     var device = GetDevice((int)address, out int deviceOffset);
        //     device.SmartWrite(deviceOffset, value);
        // }

        // public unsafe void Read(long address, byte[] buffer, int offset, int count)
        // {
        //     var device = GetDevice((int)address, out int deviceOffset);

        //     // try {

        //     fixed (byte * bufferPtr = &buffer[offset]) {
        //         nint ptr = (nint)bufferPtr;

        //         switch (count) {
        //             case 1: buffer[offset] = device.Read8(N64Endianess.Address8(deviceOffset)); break;
        //             case 2: ptr.AsType_16(device.SmartRead<ushort>(N64Endianess.Address16(deviceOffset))); break;
        //             case 4: ptr.AsType_32(device.SmartRead<uint>(deviceOffset)); break;
        //             case 8: ptr.AsType_64(device.SmartRead<ulong>(deviceOffset)); break;
        //             default: {
        //                 for (int i = 0; i < count; i++) {
        //                     buffer[i + offset] = device.Read8(deviceOffset + i);
        //                 }
        //                 break;
        //             }
        //         }
        //     }
        //     // }
        //     // catch {
        //     //     throw new Exception(String.Format("N64Controller Bad Read: {0:X8} @{1}", address, device));
        //     // }

        //     device.DispatchReadNotifications(deviceOffset, count);
        // }

        // public unsafe void Write(long address, byte[] buffer, int offset, int count)
        // {
        //     var device = GetDevice((int)address, out int deviceOffset);

        //     fixed (byte * bufferPtr = &buffer[offset]) {
        //         nint ptr = (nint)bufferPtr;

        //         switch (count) {
        //             case 1: device.Write8(N64Endianess.Address8(deviceOffset), buffer[offset]); break;
        //             case 2: device.SmartWrite(N64Endianess.Address16(deviceOffset), ptr.AsType_16()); break;
        //             case 4: device.SmartWrite(deviceOffset, ptr.AsType_32()); break;
        //             case 8: device.SmartWrite(deviceOffset, ptr.AsType_64()); break;
        //             default: {
        //                 for (int i = 0; i < count; i++) {
        //                     device.Write8(deviceOffset + i, buffer[i + offset]);
        //                 }
        //                 break;
        //             }
        //         }
        //     }
        //     device.DispatchWriteNotifications(deviceOffset, count);
        // }

        // public N64MemoryStream CreateMemoryStream()
        // {
        //     var stream = new N64MemoryStream(this, 0, 0xFFFFFFFFU);
        //     return stream;
        // }

        // public N64MemoryStream CreateMemoryStream(long baseAdress, long size)
        // {
        //     var stream = new N64MemoryStream(this, baseAdress, size);
        //     return stream;
        // }

        public void MountCartridge(Cartridge cartridge)
        {
            // CLEANUP: Should just keep a single copy of ROM
            var romMemory = new UnmanagedBuffer((int)cartridge.RomStream.Length);
            byte[] buffer = new byte[romMemory.Size];
            cartridge.RomStream.Position = 0;
            cartridge.RomStream.Read(buffer, 0, buffer.Length);
            romMemory.CopyInto(buffer);
            m_CartRom = new MemMappedCart(romMemory, this);
            Cart = m_CartRom;

            // [rsp_compat] RSP instructions per CPU step for this game (COR64_RSP_SCALE overrides; see RspInterpreter.CreditScale)
            cor64.Mips.Rsp.RspInterpreter.CreditScale = cor64.Mips.Rsp.RspInterpreter.ScaleFor(SaveDb.GameCode(buffer));
            { int sw = buffer[0] == 0x80 ? 0 : buffer[0] == 0x37 ? 1 : buffer[0] == 0x40 ? 3 : 0; uint crc1 = 0; for (int i = 0; i < 4; i++) crc1 = crc1 << 8 | buffer[(0x10 + i) ^ sw];
              cor64.Telemetry.ViSpeedup = cor64.Telemetry.ViSpeedupFor(crc1, SaveDb.GameCode(buffer)); } // [visp_fix1] + COR64_VISPFIX1_CAP // [ingame_fix2] per-cart VI speed-up floor (CRC1, any byte order)
            cor64.Mips.R4300I.Interpreter.Spd1Active = cor64.Mips.R4300I.Interpreter.Spd1For(SaveDb.GameCode(buffer)); // [speed_poll1]
            cor64.Mips.R4300I.Interpreter.ViWaitFor(SaveDb.GameCode(buffer)); // [speed_cat9]
            cor64.Mips.R4300I.Interpreter.Spd2Long = cor64.Mips.R4300I.Interpreter.Spd2LongFor(SaveDb.GameCode(buffer)); // [speed_poll2]
            cor64.RCP.Video.ConfigureForRegion(cartridge.Region); // [catalog_fix1] COR64_PAL_VI field rate
            cor64.RCP.Video.ConfigureLineWrap(SaveDb.GameCode(buffer)); // [catalog_fix7] COR64_CATFIX7_VIWRAP
            cor64.Mips.R4300I.SystemController.CountScale = cor64.Mips.R4300I.SystemController.CountScaleFor(SaveDb.GameCode(buffer)); // [catalog_fix2]
            cor64.Mips.R4300I.SystemController.Boot1Arm(SaveDb.GameCode(buffer)); // [boot_fix1] COR64_BOOT1_VI
            cor64.RCP.SerialController.SiDurationGame = cor64.RCP.SerialController.SiDurationFor(SaveDb.GameCode(buffer)); // [catalog_fix4]
            cor64.PIF.JoyController.LatchGame = cor64.PIF.JoyController.LatchFor(SaveDb.GameCode(buffer)); // [visp_fix2] COR64_VISPFIX2_LATCH
            cor64.RCP.ParallelInterface.DeferGame = cor64.RCP.ParallelInterface.DeferFor(SaveDb.GameCode(buffer)); // [ingame_fix3] COR64_INGFIX3_PIDEFER
            cor64.Rdp.ParallelRdp.ConfigureForGame(SaveDb.GameCode(buffer)); // [vi_scanout] per-game RDP sync (see ParallelRdp)
            // [timing] finish the parallel-rdp batch on DP-interrupt ack for this game (COR64_TIMING_DPACK=1/0 overrides)
            cor64.Rdp.ParallelRdp.DpAckOn = cor64.Rdp.ParallelRdp.DpAckFor(SaveDb.GameCode(buffer));

            // [cer64plus] cartridge save type (COR64_CARTSAVES=0: none on domain 2, EEPROM 4 Kbit)
            Dom2 = null;
            if (SaveDb.Enabled) {
                GameCode = SaveDb.GameCode(buffer);
                SaveKind = SaveDb.Lookup(GameCode, SaveDb.Revision(buffer));
                if (SaveKind != CartSaveType.Eeprom4K && SaveKind != CartSaveType.Eeprom16K)
                    Dom2 = new CartSave(SaveKind); // SRAM / FlashRAM, or None = picked on first access
                PIF.Eeprom16K = SaveKind == CartSaveType.Eeprom16K;
                PIF.Ports.ApplyGameDefaults(GameCode); // [quirk_fix1] COR64_PAK_GAMEDB
                cor64.RCP.SerialController.ApplyGameDefaults(GameCode, cor64.PIF.JoybusPorts.IsCertified(GameCode)); // [catalog_fix3] COR64_SI_DMADUR
                PIF.Rtc = cor64.PIF.CartRtc.WantedFor(GameCode) ? new cor64.PIF.CartRtc() : null; // [catalog_fix3] COR64_CART_RTC
                PIF.NoCartEeprom = SaveKind == CartSaveType.Sram32K || SaveKind == CartSaveType.Sram96K || SaveKind == CartSaveType.FlashRam;
            }
        }

        // [cer64plus] domain-2 save memory (SRAM/FlashRAM); null when the game has none. Not in savestates.
        [NonSerialized] public CartSave Dom2;
        [NonSerialized] public CartSaveType SaveKind;
        [NonSerialized] public string GameCode;

        // [catalog_fix4] PI bus write latch (COR64_CATFIX4_PILATCH, default on): a CPU write to cartridge ROM space
        // (Domain 1 Address 2) starts a PI bus write that the ROM ignores; a CPU read of the cart while that write is still
        // in flight returns the PI bus latch (the written word) instead of ROM data (ares pi/bus.hpp readWord: ioBusy ->
        // return busLatch). Traveller's Tales games (A Bug's Life, Toy Story 2) relocate a header that lives in ROM and
        // only leave their loop because the count read right after the store returns the stored (negative) word.
        private static readonly bool s_PiLatch = Environment.GetEnvironmentVariable("COR64_CATFIX4_PILATCH") != "0";
        private const long PiLatchTicks = 200; // ~400 PI-bus cycles (ares queue PI_BUS_Write) in CPU steps
        [NonSerialized] private long m_PiLatchUntil = long.MinValue; [NonSerialized] private uint m_PiLatch;
        private static bool IsCartRom(long address) => (ulong)(address - 0x10000000) < 0x0FC00000UL;
        private void PiLatchWrite(long address, uint wordValue) { if (s_PiLatch && IsCartRom(address)) { m_PiLatch = wordValue; m_PiLatchUntil = cor64.Telemetry.Ticks + PiLatchTicks; } }
        private bool PiLatchRead(long address, out uint v) {
            v = m_PiLatch;
            if (m_PiLatchUntil == long.MinValue || !IsCartRom(address)) return false;
            bool hit = cor64.Telemetry.Ticks <= m_PiLatchUntil; m_PiLatchUntil = long.MinValue; return hit;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private bool IsDom2(long address) => (ulong)(address - 0x08000000) < 0x08000000UL && Dom2 != null;

        // public void AttachDmaEngine_PI(DmaEngine engine) {
        //     engine.AttachMemory(RDRAM, Cart);
        // }

        // public void AttachDmaEngine_SP(DmaEngine engine) {
        //     engine.AttachMemory(RDRAM, SPRegs);
        // }

        // public void AttachDmaEngine_SI(DmaEngine engine) {
        //     engine.AttachMemory(RDRAM, PIF);
        // }

        // public int MemoryCopy(uint srcAddress, uint destAddress, int len) {
        //     int count = 0;

        //     byte[] buffer = new byte[4];

        //     for (int i = 0; i < (len / 4); i++)
        //     {
        //         var src = srcAddress + (i * 4);
        //         var dst = destAddress + (i * 4);

        //         Read(src, buffer, 0, 4);
        //         Write(dst, buffer, 0, 4);
        //         count += 4;
        //     }

        //     return count;
        // }

        // public int MemoryCopyUnaligned(uint srcAddress, uint destAddress, int len) {
        //     int count = 0;

        //     byte[] buffer = new byte[1];

        //     for (int i = 0; i < len; i++)
        //     {
        //         var src = srcAddress + i;
        //         var dst = destAddress + i;

        //         Read(src, buffer, 0, 1);

        //         Write(dst, buffer, 0, 1);

        //         count++;
        //     }

        //     return count;
        // }

        private static bool AlignmetCheck(long address, int size)
        {
            return (address & (size - 1)) == 0;
        }

        private unsafe T UnalignedRead<T>(N64MemoryDevice device, long offset) where T : unmanaged
        {
            T value = default;
            byte* ptr = (byte*)&value;
            int readOffset = (int)offset;

            for (int i = 0; i < sizeof(T); i++)
            {
                *ptr++ = device.Read8(readOffset++);
            }

            return value;
        }

        private unsafe void UnalignedWrite<T>(N64MemoryDevice device, long offset, T value) where T : unmanaged
        {
            byte* ptr = (byte*)&value;
            int writeOffset = (int)offset;

            for (int i = 0; i < sizeof(T); i++)
            {
                device.Write8(writeOffset++, *ptr++);
            }
        }

        // TODO: Maybe directly access a pointer provided by N64MemoryDevice class
        // TODO: Maybe the endianess controlled part should be done here?
        // TODO: Should the bounds check performed here because we know the real address?

        #region Data Access
        public sbyte S8(long address)
        {
            return (sbyte)U8(address);
        }

        public short S16(long address)
        {
            return (short)U16(address);
        }

        public int S32(long address)
        {
            return (int)U32(address);
        }

        public long S64(long address)
        {
            return (long)U64(address);
        }

        public byte DirectReadByte(long address) {
            return GetDevice(address, out long offset).Read8((int)offset);
        }

        public void DirectWriteByte(long address, byte value) {
            GetDevice(address, out long offset).Write8((int)offset, value);
        }

        public byte U8(long address)
        {
            if (Watches != null) Watches.OnAccess(address, 1, false, 0); // [dbg_mem] COR64_DBG_WATCH
            cor64.Rdp.RdpGuard.Read((uint)address, 1); // [rdp_readback] in-flight RDP pages
            if (IsDom2(address)) return Dom2.Read8((uint)address);
            if (m_PiLatchUntil != long.MinValue && PiLatchRead(address, out uint pl)) return (byte)(pl >> ((~(int)address & 3) * 8));
            return GetDevice(address, out long offset).Read8(N64Endianess.Address8((int)offset));
        }

        public ushort U16(long address)
        {
            if (Watches != null) Watches.OnAccess(address, 2, false, 0); // [dbg_mem] COR64_DBG_WATCH
            cor64.Rdp.RdpGuard.Read((uint)address, 2); // [rdp_readback] in-flight RDP pages
            if (IsDom2(address)) return Dom2.Read16((uint)address);
            if (m_PiLatchUntil != long.MinValue && PiLatchRead(address, out uint pl)) return (ushort)(pl >> ((~(int)address & 2) * 8));
            var device = GetDevice(address, out long offset);

            if (AlignmetCheck(address, 2))
            {
                return N64Endianess.U16(device.Read16(N64Endianess.Address16((int)offset)));
            }
            else
            {
                return N64Endianess.U16(UnalignedRead<ushort>(device, N64Endianess.Address16(offset)));
            }
        }

        public uint U32(long address)
        {
            if (Watches != null) Watches.OnAccess(address, 4, false, 0); // [dbg_mem] COR64_DBG_WATCH
            cor64.Rdp.RdpGuard.Read((uint)address, 4); // [rdp_readback] in-flight RDP pages
            if (IsDom2(address)) return Dom2.Read32((uint)address);
            if (m_PiLatchUntil != long.MinValue && PiLatchRead(address, out uint pl)) return pl;
            var device = GetDevice(address, out long offset);

            if (AlignmetCheck(address, 4))
            {
                return N64Endianess.U32(device.Read32((int)offset));
            }
            else
            {
                return N64Endianess.U32(UnalignedRead<uint>(device, offset));
            }
        }

        public ulong U64(long address)
        {
            if (Watches != null) Watches.OnAccess(address, 8, false, 0); // [dbg_mem] COR64_DBG_WATCH
            cor64.Rdp.RdpGuard.Read((uint)address, 8); // [rdp_readback] in-flight RDP pages
            if (IsDom2(address)) return Dom2.Read64((uint)address);
            var device = GetDevice(address, out long offset);

            if (AlignmetCheck(address, 8))
            {
                return N64Endianess.U64(device.Read64((int)offset));
            }
            else
            {
                return N64Endianess.U64(UnalignedRead<uint>(device, offset));
            }
        }

        public void S8(long address, sbyte value) { U8(address, (byte)value); }
        public void S16(long address, short value) { U16(address, (ushort)value); }
        public void S32(long address, int value) { U32(address, (uint)value); }
        public void S64(long address, long value) { U64(address, (ulong)value); }


        public void U8(long address, byte value)
        {
            if (Watches != null) Watches.OnAccess(address, 1, true, value); // [dbg_mem] COR64_DBG_WATCH
            cor64.Rdp.RdpGuard.Write((uint)address, 1); // [rdp_readback] in-flight RDP pages
            if (IsDom2(address)) { Dom2.Write8((uint)address, value); return; }
            PiLatchWrite(address, value * 0x01010101u);
            GetDevice(address, out long offset).Write8(N64Endianess.Address8((int)offset), value);
        }

        public void U16(long address, ushort value)
        {
            if (Watches != null) Watches.OnAccess(address, 2, true, value); // [dbg_mem] COR64_DBG_WATCH
            cor64.Rdp.RdpGuard.Write((uint)address, 2); // [rdp_readback] in-flight RDP pages
            if (IsDom2(address)) { Dom2.Write16((uint)address, value); return; }
            PiLatchWrite(address, value * 0x00010001u);
            var device = GetDevice(address, out long offset);

            value = N64Endianess.U16(value);

            if (AlignmetCheck(address, 2))
            {
                device.Write16(N64Endianess.Address16((int)offset)
                , value);
            }
            else
            {
                UnalignedWrite(device, N64Endianess.Address16(offset), value);
            }
        }

        public void U32(long address, uint value)
        {
            if (Watches != null) Watches.OnAccess(address, 4, true, value); // [dbg_mem] COR64_DBG_WATCH
            cor64.Rdp.RdpGuard.Write((uint)address, 4); // [rdp_readback] in-flight RDP pages
            if (IsDom2(address)) { Dom2.Write32((uint)address, value); return; }
            PiLatchWrite(address, value);
            var device = GetDevice(address, out long offset);

            value = N64Endianess.U32(value);

            if (AlignmetCheck(address, 4))
            {
                device.Write32((int)offset, value);
            }
            else
            {
                UnalignedWrite(device, offset, value);
            }

            device.DispatchWriteNotifications((int)offset, 4);
        }

        public void U64(long address, ulong value)
        {
            if (Watches != null) Watches.OnAccess(address, 8, true, value); // [dbg_mem] COR64_DBG_WATCH
            cor64.Rdp.RdpGuard.Write((uint)address, 8); // [rdp_readback] in-flight RDP pages
            if (IsDom2(address)) { Dom2.Write64((uint)address, value); return; }
            PiLatchWrite(address, (uint)value);
            var device = GetDevice(address, out long offset);

            value = N64Endianess.U64(value);

            if (AlignmetCheck(address, 8))
            {
                device.Write64((int)offset, value);
            }
            else
            {
                UnalignedWrite(device, offset, value);
            }
        }

        #endregion

        public void MountRcpInterfaces(
            SPInterface sp,
            DPCInterface dpc,
            N64MemoryDevice dps,
            MipsInterface mi,
            Video vi,
            Audio ai,
            ParallelInterface pi,
            SerialController si
        ) {
            SPRegs = sp;
            DPCmdRegs = dpc;
            DPSpanRegs = dps;
            MIRegs = mi;
            VIRegs = vi;
            AIRegs = ai;
            PIRegs = pi;
            SIRegs = si;
        }


        public Rdram RDRAM { get; private set; }

        public RdramRegisters RDRAMRegs { get; private set; }

        public SPInterface SPRegs { get; private set; }

        public DPCInterface DPCmdRegs { get; private set; }

        public N64MemoryDevice DPSpanRegs { get; private set; }

        public MipsInterface MIRegs { get; private set; }

        public Video VIRegs { get; private set; }

        public Audio AIRegs { get; private set; }

        public ParallelInterface PIRegs { get; private set; }

        public RdramInterface RIRegs { get; private set; }

        public SerialController SIRegs { get; private set; }

        public N64MemoryDevice Cart { get; private set; }

        public PIFController PIF { get; private set; }

        public N64MemoryDevice DiskDriveRegisters { get; private set; }

        public DummyMemory Unused_0490 { get; private set; }
        public N64MemoryDevice Unused_0600 { get; private set; }
        public N64MemoryDevice Unused_1FD0 { get; private set; }
    }
}
