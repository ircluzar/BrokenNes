// Interrupt response programs. NMI_, INTERRUPT_1 and INTERRUPT_2 are BizHawk's Z80A Interrupts.cs programs (MIT, Copyright (c) BizHawk team) with the bus
// register-usage columns removed; INTERRUPT_0 is new (BizHawk assumed a NOP on the data bus).

namespace NesEmulator.Sega;

public sealed partial class Z80<TBus>
{
    /// <summary>Non-maskable interrupt: 11 T-states, push PC, jump to $0066.</summary>
    private void NMI_()
    {
        PopulateCURINSTR
            (IDLE,
                IDLE,
                IDLE,
                IDLE,
                DEC16, rSPl, rSPh,
                TR, rALU, rPCl,
                WAIT,
                WR_DEC, rSPl, rSPh, rPCh,
                TR16, rPCl, rPCh, rNMI_V, rZERO,
                WAIT,
                WR, rSPl, rSPh, rALU);
        IRQS = 11;
    }

    /// <summary>
    /// IM 0: the device puts an instruction on the data bus. An <c>RST n</c> (what the Master System's $FF is) costs 13 T-states, the same as IM 1 with the
    /// vector taken from the opcode. Anything else is treated as a NOP with the two interrupt wait states: multi-byte instructions on the bus are not modelled.
    /// </summary>
    private void INTERRUPT_0()
    {
        PopulateCURINSTR
            (IDLE,
                IDLE,
                IORQ,
                WAIT,
                INT0_OP,
                TR, rALU, rPCl,
                DEC16, rSPl, rSPh,
                IDLE,
                WAIT,
                WR_DEC, rSPl, rSPh, rPCh,
                TR16, rPCl, rPCh, rIRQ_V, rZERO,
                WAIT,
                WR, rSPl, rSPh, rALU);
        IRQS = 13;
        // the vector register is reloaded when the opcode is read; INTERRUPT_1 leaves it at $38
    }

    /// <summary>The slot where IM 0 reads the instruction from the data bus.</summary>
    private void Int0Execute()
    {
        byte b = _bus.InterruptVector();
        Regs[rDB] = b;
        if ((b & 0xC7) == 0xC7)
        {
            Regs[rIRQ_V] = (byte)(b & 0x38);
        }
        else
        {
            // not an RST: nothing is pushed or jumped to. Replace the rest of the response with idle slots (6 T-states in all).
            PopulateCURINSTR(IDLE, IDLE, IDLE, IDLE, IDLE, IDLE);
            IRQS = 6;
        }
    }

    /// <summary>IM 1: 13 T-states, push PC, jump to $0038.</summary>
    private void INTERRUPT_1()
    {
        Regs[rIRQ_V] = 0x38;
        PopulateCURINSTR
            (IDLE,
                IDLE,
                IORQ,
                WAIT,
                IDLE,
                TR, rALU, rPCl,
                DEC16, rSPl, rSPh,
                IDLE,
                WAIT,
                WR_DEC, rSPl, rSPh, rPCh,
                TR16, rPCl, rPCh, rIRQ_V, rZERO,
                WAIT,
                WR, rSPl, rSPh, rALU);
        IRQS = 13;
    }

    /// <summary>IM 2: 19 T-states, push PC, read the handler address from the table at (I &lt;&lt; 8 | byte on the data bus).</summary>
    private void INTERRUPT_2()
    {
        PopulateCURINSTR
            (IDLE,
                IDLE,
                IORQ,
                WAIT,
                FTCH_DB,
                IDLE,
                DEC16, rSPl, rSPh,
                TR16, rZ, rW, rDB, rI,
                WAIT,
                WR_DEC, rSPl, rSPh, rPCh,
                IDLE,
                WAIT,
                WR, rSPl, rSPh, rPCl,
                IDLE,
                WAIT,
                RD_INC, rPCl, rZ, rW,
                IDLE,
                WAIT,
                RD, rPCh, rZ, rW);
        IRQS = 19;
    }
}
