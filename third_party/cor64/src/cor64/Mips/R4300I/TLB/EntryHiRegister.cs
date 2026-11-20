

using System;

namespace cor64.Mips.R4300I.TLB
{
    public class EntryHiRegister
    {
        private ulong m_Value;

        private readonly BitFiddler64 m_BitFiddler = new BitFiddler64();

        private const int F_ASID = 0;
        private const int F_VPN2 = 1;
        private const int F_REGION = 2;

        private static readonly bool s_BumpAll = System.Environment.GetEnvironmentVariable("COR64_EHI_EPOCH_ALL") == "1";
        public EntryHiRegister() {
            /* ASID */    m_BitFiddler.DefineField(0, 8);
            /* VPN2 */ m_BitFiddler.DefineField(13, 27);
            /* Region */  m_BitFiddler.DefineField(62, 2);
        }

        public void Write(ulong value) {
            value &= 0xC00000FFFFFFE0FF;
            // [f5_speed3] only an ASID change can change a translation (COR64_EHI_EPOCH_ALL=1: bump on every write)
            bool asid = ((m_Value ^ value) & 0xFF) != 0;
            m_Value = value;
            if (asid || s_BumpAll) MapEpoch.Bump();
        }

        public ulong Read() {
            return m_Value;
        }

        /// <summary>[ge_speed] ASID as a plain mask (bits 7:0, the same field as ASID).</summary>
        public uint AsidFast => (uint)m_Value & 0xFF;

        public uint ASID {
            get => (uint)m_BitFiddler.X(F_ASID, m_Value);
            set { ulong old = m_Value; m_BitFiddler.J(F_ASID, ref m_Value, value); if (old != m_Value || s_BumpAll) MapEpoch.Bump(); }
        }

        public uint VPN2 {
            get => (uint)m_BitFiddler.X(F_VPN2, m_Value);
            set => m_BitFiddler.J(F_VPN2, ref m_Value, value);
        }

        public byte Region {
            get => (byte)m_BitFiddler.X(F_REGION, m_Value);
            set => m_BitFiddler.J(F_REGION, ref m_Value, value);
        }

        public override string ToString()
        {
            return $"ASID: {ASID:X4} VPN2: {VPN2:X8} REGION: {Region:X2}";
        }
    }
}