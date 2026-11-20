using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using cor64.Mips.R4300I.TLB;

namespace cor64.Mips.R4300I
{
    public class MemoryManager
    {
        private readonly SystemController m_Cop0;
        private readonly TLBCache m_TranslationBuffer;
        private bool m_Disabled;

        public MemoryManager(SystemController controller) {
            m_Cop0 = controller;
            m_TranslationBuffer = new TLBCache(m_Cop0);
        }

        public void Disable(bool disable) {
            m_Disabled = disable;
        }

        public bool IsDisabled => m_Disabled;

        public TLBCache TLB => m_TranslationBuffer;

        public void Init() {
            m_TranslationBuffer.Initialize();
        }

        public bool VirtualToPhysical(bool isWrite, long virtualAddress, out long physicalAddress)
        {
            if (m_Disabled) {
                physicalAddress = virtualAddress;
                return true;
            }

            /* Force 32-bit addresses */
            virtualAddress &= 0xFFFFFFFF;

            int mode = m_Cop0.ModeBits; // [cer64plus] read once
            if (mode == 1)
            {
                throw new NotSupportedException("Supervisor vmap not yet supported");
            }
            else if (mode == 2)
            {
                throw new NotSupportedException("User vmap not yet supported");
            }
            else
            {
                /* Kernel 32-bit memory addressing */
                byte index = (byte)((uint)virtualAddress >> 28);

                switch (index) {
                    case 0x8: case 0x9: physicalAddress = virtualAddress - 0x80000000; break;
                    case 0xA: case 0xB: physicalAddress = virtualAddress & 0x1FFFFFFF; break;
                    default:
                        // [tlb_exc] mapped (KUSEG, KSSEG, KSEG3): a failed lookup raised a TLB exception in COP0
                        if (!m_TranslationBuffer.TryTranslate(virtualAddress, isWrite, out physicalAddress)) return false;
                        break;
                }
            }

            /* Check if memory access has thrown a non-maskable exception */
            if (m_Cop0.IsNonMaskablePending) {
                return false;
            }

            return true;
        }
    }
}