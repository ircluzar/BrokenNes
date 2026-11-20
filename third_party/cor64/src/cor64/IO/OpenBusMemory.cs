using System;

namespace cor64.IO
{
    // [dev_dma] A PI bus range with nothing attached (no 64DD: 0x05000000-0x07FFFFFF). On hardware the PI keeps the low
    // 16 bits of the bus address in its latch, so a CPU word read at A returns (A & 0xFFFF) in both halves and writes are
    // dropped (ares pi/bus.hpp busAddress/busReadHalf; mupen64plus read_open_bus behaves the same). cor64 used to return
    // 0, which games that probe for a 64DD (dk64, fzx, mk64) can read as "drive present".
    // COR64_DEVDMA_OPENBUS=0 = old (DummyMemory, reads 0).
    public unsafe class OpenBusMemory : N64MemoryDevice
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("COR64_DEVDMA_OPENBUS") != "0";
        private static readonly UnmanagedBuffer s_Pattern = MakePattern();
        private readonly string m_Name;

        private static UnmanagedBuffer MakePattern()
        {
            var b = new UnmanagedBuffer(0x10000);
            uint* p = (uint*)b.GetPointer();
            for (uint a = 0; a < 0x10000; a += 4) p[a >> 2] = (a << 16) | a; // host-order word, as RDRAM stores words
            return b;
        }

        public OpenBusMemory(int size, string name, N64MemoryController controller) : base(controller, size) { m_Name = name; }

        public override string Name => m_Name;

        // The device's base is 64 KB aligned, so the low 16 bits of the bus address equal those of the offset.
        protected override N64AlignedPtr DynamicReadMap(int offset) => N64AlignedPtr.FromPtr(s_Pattern.GetPointer() + (offset & 0xFFFC));

        // PI DMA from an open-bus address: every halfword is the latched address's low 16 bits; the latch is set at the
        // start of the DMA and again at each PI page boundary (ares pi/dma.cpp dmaWrite). Page size 2^(PGS+2); libultra
        // uses PGS=7 (512 bytes). start = the DMA's first bus address, cur = this block's first bus address.
        public static void FillDma(uint start, uint cur, byte[] block, int count, int pageSize = 0x200)
        {
            start &= ~1u;
            for (int i = 0; i < count; i += 2)
            {
                uint a = (cur & ~1u) + (uint)i;
                uint page = a & ~(uint)(pageSize - 1);
                uint latch = page > start ? page : start;
                block[i] = (byte)(latch >> 8);
                if (i + 1 < block.Length) block[i + 1] = (byte)latch;
            }
        }
    }
}
