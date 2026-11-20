using System;
using System.IO;
using cor64.IO;

namespace cor64.Rdp.LLE {
    // [cer64plus] Fast path: direct pointer access to RDRAM (host little-endian 32-bit words, so big-endian byte a is at
    // host byte a ^ 3). The old stream path allocated twice per access. The fast path checks itself against the stream path
    // for the first 500k reads and permanently falls back to it on any mismatch.
    public unsafe class Rdram {
        private readonly byte* m_Ptr;
        private const uint FastLimit = 0x800000;
        private long m_Checks;
        private bool m_FastOff;
        private readonly uint m_IdxLim8;
        private readonly uint m_IdxLim16;
        private readonly uint m_IdxLim32;
        private readonly Stream m_MemoryStream;
        private readonly Stream m_BaseStream;
        private readonly BinaryReader m_Reader;
        private readonly BinaryWriter m_Writer;
        private readonly RdramHidden m_Hidden;

        private const uint RDRAM_MASK = 0x00FFFFFF;

        public Rdram(N64MemoryStream rdramStream) {
            m_Hidden = rdramStream;
            m_IdxLim8 = (uint)(rdramStream.Length - 1);
            m_IdxLim16 = (m_IdxLim8 >> 1) & 0xFFFFFFFFU;
            m_IdxLim32 = (m_IdxLim8 >> 2) & 0xFFFFFFFFU;
            m_BaseStream = rdramStream;

            // CLEANUP: little-endian mode is bugged
            // #if HOST_LITTLE_ENDIAN
            // m_MemoryStream = rdramStream;
            // #else
            m_MemoryStream = new SwapAutoStream(rdramStream);
            // #endif

            m_Reader = new BinaryReader(m_MemoryStream);
            m_Writer = new BinaryWriter(m_MemoryStream);
            m_Ptr = (byte*)rdramStream.Controller.RDRAM.GetRamPointer(0);
            m_FastOff = System.Environment.GetEnvironmentVariable("COR64_RDP_SLOW_RDRAM") == "1";
        }

        private bool Fast(uint address, uint size) => !m_FastOff && address + size <= FastLimit;

        private void Mismatch(string what, uint address, uint fast, uint slow) {
            m_FastOff = true;
            Console.WriteLine("RDP fast RDRAM {0} disagrees at {1:X6}: fast {2:X} slow {3:X}; using the slow path", what, address, fast, slow);
        }

        public void Init() {
            for (int i = 0; i < m_Hidden.HiddenLength; i++) {
                m_Hidden.HiddenWrite(i, 3);
            }
        }

        private byte SlowRead8(uint address) { m_MemoryStream.Position = address; return (byte)m_BaseStream.ReadByte(); }
        private ushort SlowRead16(uint address) { m_MemoryStream.Position = address; return m_Reader.ReadUInt16(); }
        private uint SlowRead32(uint address) { m_MemoryStream.Position = address; return m_Reader.ReadUInt32(); }

        private byte ReadRdram8(uint address) {
            if (!Fast(address, 1)) return SlowRead8(address);
            byte v = m_Ptr[address ^ 3];
            if (m_Checks < 500000) { m_Checks++; byte s = SlowRead8(address); if (s != v) { Mismatch("read8", address, v, s); return s; } }
            return v;
        }

        public void WriteRdram8(uint address, byte value) {
            if (!Fast(address, 1)) { m_MemoryStream.Position = address; m_BaseStream.WriteByte(value); return; }
            m_Ptr[address ^ 3] = value;
            if (m_Checks < 500000) { m_Checks++; byte r = SlowRead8(address); if (r != value) Mismatch("write8", address, value, r); }
        }

        private ushort ReadRdram16(uint address) {
            if (!Fast(address, 2) || (address & 1) != 0) return SlowRead16(address);
            ushort v = *(ushort*)(m_Ptr + (address ^ 2));
            if (m_Checks < 500000) { m_Checks++; ushort s = SlowRead16(address); if (s != v) { Mismatch("read16", address, v, s); return s; } }
            return v;
        }

        public void WriteRdram16(uint address, ushort value) {
            if (!Fast(address, 2) || (address & 1) != 0) { m_MemoryStream.Position = address; m_Writer.Write(value); return; }
            *(ushort*)(m_Ptr + (address ^ 2)) = value;
            if (m_Checks < 500000) { m_Checks++; ushort r = SlowRead16(address); if (r != value) Mismatch("write16", address, value, r); }
        }

        public uint ReadRdram32(uint address) {
            if (!Fast(address, 4) || (address & 3) != 0) return SlowRead32(address);
            uint v = *(uint*)(m_Ptr + address);
            if (m_Checks < 500000) { m_Checks++; uint s = SlowRead32(address); if (s != v) { Mismatch("read32", address, v, s); return s; } }
            return v;
        }

        public void WriteRdram32(uint address, uint value) {
            if (!Fast(address, 4) || (address & 3) != 0) { m_MemoryStream.Position = address; m_Writer.Write(value); return; }
            *(uint*)(m_Ptr + address) = value;
            if (m_Checks < 500000) { m_Checks++; uint r = SlowRead32(address); if (r != value) Mismatch("write32", address, value, r); }
        }

        public bool ValidIdx8(uint address) {
            return address <= m_IdxLim8;
        }

        public bool ValidIdx16(uint address) {
            return address <= m_IdxLim16;
        }

        public bool ValidIdx32(uint address) {
            return address <= m_IdxLim32;
        }

        public byte ReadIdx8(uint address) {
            address &= RDRAM_MASK;

            if (ValidIdx8(address)) {
                return ReadRdram8(address);
            }
            else {
                return 0;
            }
        }

        // public byte ReadIdx8Fast(uint address) {
        //     #if HOST_LITTLE_ENDIAN
        //     return ReadRdram8(address);
        //     #else
        //     return ReadRdram8(address ^ 3);
        //     #endif
        // }

        public void WriteIdx8(uint address, byte value) {
            address &= RDRAM_MASK;

            if (ValidIdx8(address)) {
                WriteRdram8(address, value);
            }
        }

        public ushort ReadIdx16(uint address) {
            address &= RDRAM_MASK >> 1;

            if (ValidIdx16(address)) {
                return ReadRdram16(address << 1);
            }
            else {
                return 0;
            }
        }

        public ushort ReadIdx16Fast(uint address) {
            return ReadRdram16(address << 1);
        }

        public void WriteIdx16(uint address, ushort value) {
            address &= RDRAM_MASK >> 1;

            if (ValidIdx16(address)) {
                WriteRdram16(address << 1, value);
            }
        }

        public uint ReadIdx32(uint address) {
            address &= RDRAM_MASK >> 2;

            if (ValidIdx32(address)) {
                return ReadRdram32(address << 2);
            }
            else {
                return 0;
            }
        }

        public uint ReadIdx32Fast(uint address) {
            return ReadRdram32(address << 2);
        }

        public void WriteIdx32(uint address, uint value) {
            address &= RDRAM_MASK >> 2;

            if (ValidIdx32(address)) {
                WriteRdram32(address << 2, value);
            }
        }

        public void ReadPair16(ref ushort value, ref byte hiddenBit, uint address) {
            address &= RDRAM_MASK >> 1;

            if (ValidIdx16(address)) {
                value = ReadRdram16(address << 1);
                hiddenBit = m_Hidden.HiddenRead((int)address);
            }
            else {
                value = 0;
                hiddenBit = 0;
            }
        }

        public void WritePair8(uint address, byte value, byte hiddenBit) {
            address &= RDRAM_MASK;

            if (ValidIdx8(address)) {
                WriteRdram8(address, value);

                if ((address & 1) != 0) {
                    m_Hidden.HiddenWrite((int)(address >> 1), hiddenBit);
                }
            }
        }

        public void WritePair16(uint address, ushort value, byte hiddenBit) {
            address &= RDRAM_MASK >> 1;

            if (ValidIdx16(address)) {
                WriteRdram16(address << 1, value);
                m_Hidden.HiddenWrite((int)address, hiddenBit);
            }
        }

        public void WritePair32(uint address, uint value, byte hiddenBit0, byte hiddenBit1) {
            address &= RDRAM_MASK >> 2;

            if (ValidIdx32(address)) {
                WriteRdram32(address << 2, value);

                m_Hidden.HiddenWrite((int)address, hiddenBit0);
                m_Hidden.HiddenWrite((int)address + 1, hiddenBit1);
            }
        }
    }
}