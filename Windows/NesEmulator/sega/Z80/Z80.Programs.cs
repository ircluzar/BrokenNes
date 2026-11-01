// Instruction programs: one builder per addressing/operation shape, each filling the current-instruction buffer with one micro-op slot per T-state.
// Transcribed from BizHawk's Z80A Tables_Direct.cs and Tables_Indirect.cs (MIT, Copyright (c) BizHawk team): the program shapes are BizHawk's, with its per-slot
// bus-request/memory-request annotation arrays removed (they only served ZX Spectrum contention) and the registers renamed. Programs and decode were then
// checked, and corrected where needed, against SingleStepTests/z80.

namespace NesEmulator.Sega;

public sealed partial class Z80<TBus>
{
    private void NOP_()
    {
        PopulateCURINSTR
            (IDLE);

        IRQS = 1;
    }

    // NOTE: In a real Z80, this operation just flips a switch to choose between 2 registers
    // but it's simpler to emulate just by exchanging the register with it's shadow
    private void EXCH_()
    {
        PopulateCURINSTR
            (EXCH);

        IRQS = 1;
    }

    private void EXX_()
    {
        PopulateCURINSTR
            (EXX);

        IRQS = 1;
    }

    // this exchanges 2 16 bit registers
    private void EXCH_16_(ushort dest_l, ushort dest_h, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
            (EXCH_16, dest_l, dest_h, src_l, src_h);

        IRQS = 1;
    }

    private void INC_16(ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
            (INC16, src_l, src_h,
                    IDLE,
                    IDLE);

        IRQS = 3;
    }

    private void DEC_16(ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
            (DEC16, src_l, src_h,
                    IDLE,
                    IDLE);

        IRQS = 3;
    }

    // this is done in two steps technically, but the flags don't work out using existing funcitons
    // so let's use a different function since it's an internal operation anyway
    private void ADD_16(ushort dest_l, ushort dest_h, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
            (IDLE,
                    TR16, rZ, rW, dest_l, dest_h,
                    IDLE,
                    INC16, rZ, rW,
                    IDLE,
                    ADD16, dest_l, dest_h, src_l, src_h,
                    IDLE,
                    IDLE);

        IRQS = 8;
    }

    private void REG_OP(ushort operation, ushort dest, ushort src)
    {
        PopulateCURINSTR
            (operation, dest, src);

        IRQS = 1;
    }

    // Operations using the I and R registers take one T-cycle longer
    private void REG_OP_IR(ushort operation, ushort dest, ushort src)
    {
        PopulateCURINSTR
            (IDLE,
                    SET_FL_IR, dest, src);

        IRQS = 2;
    }

    // note: do not use DEC here since no flags are affected by this operation
    private void DJNZ_()
    {
        if ((Regs[rB] - 1) != 0)
        {
            PopulateCURINSTR
            (IDLE,
                        IDLE,
                        ASGN, rB, (ushort)((Regs[rB] - 1) & 0xFF),
                        WAIT,
                        RD_INC, rZ, rPCl, rPCh,
                        IDLE,
                        IDLE,
                        ASGN, rW, 0,
                        ADDS, rPCl, rPCh, rZ, rW,
                        TR16, rZ, rW, rPCl, rPCh);

            IRQS = 10;
        }
        else
        {
            PopulateCURINSTR
                (IDLE,
                        IDLE,
                        ASGN, rB, (ushort)((Regs[rB] - 1) & 0xFF),
                        WAIT,
                        RD_INC, rALU, rPCl, rPCh);

            IRQS = 5;
        }
    }

    private void HALT_()
    {
        PopulateCURINSTR
                (HALT);

        IRQS = 1;
    }

    private void JR_COND(bool cond)
    {
        if (cond)
        {
            PopulateCURINSTR
                (IDLE,
                        IDLE,
                        WAIT,
                        RD_INC, rZ, rPCl, rPCh,
                        IDLE,
                        ASGN, rW, 0,
                        IDLE,
                        ADDS, rPCl, rPCh, rZ, rW,
                        TR16, rZ, rW, rPCl, rPCh);

            IRQS = 9;
        }
        else
        {
            PopulateCURINSTR
                (IDLE,
                        IDLE,
                        WAIT,
                        RD_INC, rALU, rPCl, rPCh);

            IRQS = 4;
        }
    }

    private void JP_COND(bool cond)
    {
        if (cond)
        {
            PopulateCURINSTR
                (IDLE,
                        IDLE,
                        WAIT,
                        RD_INC, rZ, rPCl, rPCh,
                        IDLE,
                        WAIT,
                        RD_INC_TR_PC, rZ, rW, rPCl, rPCh);

            IRQS = 7;
        }
        else
        {
            PopulateCURINSTR
                (IDLE,
                        IDLE,
                        WAIT,
                        RD_INC, rZ, rPCl, rPCh,
                        IDLE,
                        WAIT,
                        RD_INC, rW, rPCl, rPCh);

            IRQS = 7;
        }
    }

    private void RET_()
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_INC, rZ, rSPl, rSPh,
                    IDLE,
                    WAIT,
                    RD_INC_TR_PC, rZ, rW, rSPl, rSPh);

        IRQS = 7;
    }

    private void RETI_()
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_INC, rZ, rSPl, rSPh,
                    EI_RETN,
                    WAIT,
                    RD_INC_TR_PC, rZ, rW, rSPl, rSPh);

        IRQS = 7;
    }

    private void RETN_()
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_INC, rZ, rSPl, rSPh,
                    EI_RETN,
                    WAIT,
                    RD_INC_TR_PC, rZ, rW, rSPl, rSPh);

        IRQS = 7;
    }

    private void RET_COND(bool cond)
    {
        if (cond)
        {
            PopulateCURINSTR
                (IDLE,
                        IDLE,
                        IDLE,
                        WAIT,
                        RD_INC, rZ, rSPl, rSPh,
                        IDLE,
                        WAIT,
                        RD_INC_TR_PC, rZ, rW, rSPl, rSPh);

            IRQS = 8;
        }
        else
        {
            PopulateCURINSTR
                (IDLE,
                        IDLE);

            IRQS = 2;
        }
    }

    private void CALL_COND(bool cond)
    {
        if (cond)
        {
            PopulateCURINSTR
                (IDLE,
                        IDLE,
                        WAIT,
                        RD_INC, rZ, rPCl, rPCh,
                        IDLE,
                        WAIT,
                        RD, rW, rPCl, rPCh,
                        INC16, rPCl, rPCh,
                        DEC16, rSPl, rSPh,
                        WAIT,
                        WR_DEC, rSPl, rSPh, rPCh,
                        IDLE,
                        WAIT,
                        WR_TR_PC, rSPl, rSPh, rPCl);

            IRQS = 14;
        }
        else
        {
            PopulateCURINSTR
                (IDLE,
                        IDLE,
                        WAIT,
                        RD_INC, rZ, rPCl, rPCh,
                        IDLE,
                        WAIT,
                        RD_INC, rW, rPCl, rPCh);

            IRQS = 7;
        }
    }

    private void INT_OP(ushort operation, ushort src)
    {
        PopulateCURINSTR
                (operation, src);

        IRQS = 1;
    }

    private void BIT_OP(ushort operation, ushort bit, ushort src)
    {
        PopulateCURINSTR
                (operation, bit, src);

        IRQS = 1;
    }

    private void PUSH_(ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    DEC16, rSPl, rSPh,
                    IDLE,
                    WAIT,
                    WR_DEC, rSPl, rSPh, src_h,
                    IDLE,
                    WAIT,
                    WR, rSPl, rSPh, src_l);

        IRQS = 8;
    }

    private void POP_(ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_INC, src_l, rSPl, rSPh,
                    IDLE,
                    WAIT,
                    RD_INC, src_h, rSPl, rSPh);

        IRQS = 7;
    }

    private void RST_(ushort n)
    {
        PopulateCURINSTR
                (IDLE,
                    DEC16, rSPl, rSPh,
                    IDLE,
                    WAIT,
                    WR_DEC, rSPl, rSPh, rPCh,
                    RST, n,
                    WAIT,
                    WR_TR_PC, rSPl, rSPh, rPCl);

        IRQS = 8;
    }

    private void PREFIX_(ushort src)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    PREFIX);

        nextPre = src;

        IRQS = -1; // prefix does not get interrupted
    }

    private void PREFETCH_(ushort src)
    {
        if (src == PRE_DDCB)
        {
            Regs[rW] = Regs[rIxh];
            Regs[rZ] = Regs[rIxl];
        }
        else
        {
            Regs[rW] = Regs[rIyh];
            Regs[rZ] = Regs[rIyl];
        }

        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_INC, rALU, rPCl, rPCh,
                    ADDS, rZ, rW, rALU, rZERO,
                    WAIT,
                    PREFIX);

        nextPre = src;

        IRQS = -1; // prefetch does not get interrupted
    }

    private void DI_()
    {
        PopulateCURINSTR
                (DI);

        IRQS = 1;
    }

    private void EI_()
    {
        PopulateCURINSTR
                (EI);

        IRQS = 1;
    }

    private void JP_16(ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (TR16, rPCl, rPCh, src_l, src_h);

        IRQS = 1;
    }

    private void LD_SP_16(ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    TR16, rSPl, rSPh, src_l, src_h);

        IRQS = 3;
    }

    private void OUT_()
    {
        PopulateCURINSTR
                (IDLE,
                    TR, rW, rA,
                    WAIT,
                    RD_INC, rZ, rPCl, rPCh,
                    TR, rALU, rA,
                    IDLE,
                    WAIT,
                    OUT_INC, rZ, rALU, rA);

        IRQS = 8;
    }

    private void OUT_REG_(ushort dest, ushort src)
    {
        PopulateCURINSTR
                (IDLE,
                    TR16, rZ, rW, rC, rB,
                    IDLE,
                    WAIT,
                    OUT_INC, rZ, rW, src);

        IRQS = 5;
    }

    private void IN_()
    {
        PopulateCURINSTR
                (IDLE,
                    TR, rW, rA,
                    WAIT,
                    RD_INC, rZ, rPCl, rPCh,
                    IDLE,
                    IDLE,
                    WAIT,
                    IN_A_N_INC, rA, rZ, rW);

        IRQS = 8;
    }

    private void IN_REG_(ushort dest, ushort src)
    {
        PopulateCURINSTR
                (IDLE,
                    TR16, rZ, rW, rC, rB,
                    IDLE,
                    WAIT,
                    IN_INC, dest, rZ, rW);

        IRQS = 5;
    }

    private void REG_OP_16_(ushort op, ushort dest_l, ushort dest_h, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    IDLE,
                    TR16, rZ, rW, dest_l, dest_h,
                    INC16, rZ, rW,
                    IDLE,
                    IDLE,
                    op, dest_l, dest_h, src_l, src_h);

        IRQS = 8;
    }

    private void INT_MODE_(ushort src)
    {
        PopulateCURINSTR
                (INT_MODE, src);

        IRQS = 1;
    }

    private void RRD_()
    {
        PopulateCURINSTR
                (IDLE,
                    TR16, rZ, rW, rL, rH,
                    WAIT,
                    RD, rALU, rZ, rW,
                    IDLE,
                    RRD, rALU, rA,
                    IDLE,
                    IDLE,
                    IDLE,
                    WAIT,
                    WR_INC, rZ, rW, rALU);

        IRQS = 11;
    }

    private void RLD_()
    {
        PopulateCURINSTR
                (IDLE,
                    TR16, rZ, rW, rL, rH,
                    WAIT,
                    RD, rALU, rZ, rW,
                    IDLE,
                    RLD, rALU, rA,
                    IDLE,
                    IDLE,
                    IDLE,
                    WAIT,
                    WR_INC, rZ, rW, rALU);

        IRQS = 11;
    }
    private void INT_OP_IND(ushort operation, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD, rALU, src_l, src_h,
                    IDLE,
                    operation, rALU,
                    WAIT,
                    WR, src_l, src_h, rALU);

        IRQS = 8;
    }

    private void BIT_OP_IND(ushort operation, ushort bit, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD, rALU, src_l, src_h,
                    operation, bit, rALU,
                    IDLE,
                    WAIT,
                    WR, src_l, src_h, rALU);

        IRQS = 8;
    }

    // Note that this operation uses I_BIT, same as indexed BIT.
    // This is where the strange behaviour in Flag bits 3 and 5 come from.
    // normally WZ contain I* + n when doing I_BIT ops, but here we use that code path
    // even though WZ is not assigned to, letting it's value from other operations show through
    private void BIT_TE_IND(ushort operation, ushort bit, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD, rALU, src_l, src_h,
                    I_BIT, bit, rALU);

        IRQS = 5;
    }

    private void REG_OP_IND_INC(ushort operation, ushort dest, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_OP, 1, rALU, src_l, src_h, operation, dest, rALU);

        IRQS = 4;
    }

    private void REG_OP_IND(ushort operation, ushort dest, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    TR16, rZ, rW, src_l, src_h,
                    WAIT,
                    RD_OP, 1, rALU, rZ, rW, operation, dest, rALU);

        IRQS = 4;
    }

    // different because HL doesn't effect WZ
    private void REG_OP_IND_HL(ushort operation, ushort dest)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_OP, 0, rALU, rL, rH, operation, dest, rALU);

        IRQS = 4;
    }

    private void LD_16_IND_nn(ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_INC, rZ, rPCl, rPCh,
                    IDLE,
                    WAIT,
                    RD_INC, rW, rPCl, rPCh,
                    IDLE,
                    WAIT,
                    WR_INC, rZ, rW, src_l,
                    IDLE,
                    WAIT,
                    WR, rZ, rW, src_h);

        IRQS = 13;
    }

    private void LD_IND_16_nn(ushort dest_l, ushort dest_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_INC, rZ, rPCl, rPCh,
                    IDLE,
                    WAIT,
                    RD_INC, rW, rPCl, rPCh,
                    IDLE,
                    WAIT,
                    RD_INC, dest_l, rZ, rW,
                    IDLE,
                    WAIT,
                    RD, dest_h, rZ, rW);

        IRQS = 13;
    }

    private void LD_8_IND_nn(ushort src)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_INC, rZ, rPCl, rPCh,
                    IDLE,
                    WAIT,
                    RD_INC, rW, rPCl, rPCh,
                    IDLE,
                    WAIT,
                    WR_INC_WA, rZ, rW, src);

        IRQS = 10;
    }

    private void LD_IND_8_nn(ushort dest)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_INC, rZ, rPCl, rPCh,
                    IDLE,
                    WAIT,
                    RD_INC, rW, rPCl, rPCh,
                    IDLE,
                    WAIT,
                    RD_INC, dest, rZ, rW);

        IRQS = 10;
    }

    private void LD_8_IND(ushort dest_l, ushort dest_h, ushort src)
    {
        PopulateCURINSTR
                (IDLE,
                    TR16, rZ, rW, dest_l, dest_h,
                    WAIT,
                    WR_INC_WA, rZ, rW, src);

        IRQS = 4;
    }

    // seperate HL needed since it doesn't effect the WZ pair
    private void LD_8_IND_HL(ushort src)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    WR, rL, rH, src);

        IRQS = 4;
    }

    private void LD_8_IND_IND(ushort dest_l, ushort dest_h, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_INC, rALU, src_l, src_h,
                    IDLE,
                    WAIT,
                    WR, dest_l, dest_h, rALU);

        IRQS = 7;
    }

    private void LD_IND_8_INC(ushort dest, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_INC, dest, src_l, src_h);

        IRQS = 4;
    }

    private void LD_IND_16(ushort dest_l, ushort dest_h, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_INC, dest_l, src_l, src_h,
                    IDLE,
                    WAIT,
                    RD_INC, dest_h, src_l, src_h);

        IRQS = 7;
    }

    private void INC_8_IND(ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD, rALU, src_l, src_h,
                    INC8, rALU,
                    IDLE,
                    WAIT,
                    WR, src_l, src_h, rALU);

        IRQS = 8;
    }

    private void DEC_8_IND(ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD, rALU, src_l, src_h,
                    DEC8, rALU,
                    IDLE,
                    WAIT,
                    WR, src_l, src_h, rALU);

        IRQS = 8;
    }

    // NOTE: WZ implied for the wollowing 3 functions
    private void I_INT_OP(ushort operation, ushort dest)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    IDLE,
                    WAIT,
                    RD, rALU, rZ, rW,
                    operation, rALU,
                    TR, dest, rALU,
                    WAIT,
                    WR, rZ, rW, rALU);

        IRQS = 9;
    }

    private void I_BIT_OP(ushort operation, ushort bit, ushort dest)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    IDLE,
                    WAIT,
                    RD, rALU, rZ, rW,
                    operation, bit, rALU,
                    TR, dest, rALU,
                    WAIT,
                    WR, rZ, rW, rALU);

        IRQS = 9;
    }

    private void I_BIT_TE(ushort bit)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    IDLE,
                    WAIT,
                    RD, rALU, rZ, rW,
                    I_BIT, bit, rALU);

        IRQS = 6;
    }

    private void I_OP_n(ushort operation, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD, rALU, rPCl, rPCh,
                    IDLE,
                    IDLE,
                    TR16, rZ, rW, src_l, src_h,
                    ADDS, rZ, rW, rALU, rZERO,
                    IDLE,
                    INC16, rPCl, rPCh,
                    WAIT,
                    RD, rALU, rZ, rW,
                    operation, rALU,
                    IDLE,
                    WAIT,
                    WR, rZ, rW, rALU);

        IRQS = 16;
    }

    private void I_OP_n_n(ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    TR16, rZ, rW, src_l, src_h,
                    WAIT,
                    RD_INC, rALU, rPCl, rPCh,
                    ADDS, rZ, rW, rALU, rZERO,
                    WAIT,
                    RD, rALU, rPCl, rPCh,
                    IDLE,
                    IDLE,
                    INC16, rPCl, rPCh,
                    WAIT,
                    WR, rZ, rW, rALU);

        IRQS = 12;
    }

    private void I_REG_OP_IND_n(ushort operation, ushort dest, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD, rALU, rPCl, rPCh,
                    IDLE,
                    TR16, rZ, rW, src_l, src_h,
                    IDLE,
                    ADDS, rZ, rW, rALU, rZERO,
                    IDLE,
                    INC16, rPCl, rPCh,
                    WAIT,
                    RD_OP, 0, rALU, rZ, rW, operation, dest, rALU);

        IRQS = 12;
    }

    private void I_LD_8_IND_n(ushort dest_l, ushort dest_h, ushort src)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD, rALU, rPCl, rPCh,
                    IDLE,
                    IDLE,
                    TR16, rZ, rW, dest_l, dest_h,
                    ADDS, rZ, rW, rALU, rZERO,
                    IDLE,
                    INC16, rPCl, rPCh,
                    WAIT,
                    WR, rZ, rW, src);

        IRQS = 12;
    }

    private void LD_OP_R(ushort operation, ushort repeat_instr)
    {
        PopulateCURINSTR
                (IDLE,
                IDLE,
                WAIT,
                RD, rALU, rL, rH,
                operation, rL, rH,
                WAIT,
                WR, rE, rD, rALU,
                IDLE,
                SET_FL_LD_R, 0, operation, repeat_instr);

        IRQS = 9;
    }

    private void CP_OP_R(ushort operation, ushort repeat_instr)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD, rALU, rL, rH,
                    IDLE,
                    DEC16, rC, rB,
                    operation, rZ, rW,
                    IDLE,
                    SET_FL_CP_R, 1, operation, repeat_instr);

        IRQS = 9;
    }

    private void IN_OP_R(ushort operation, ushort repeat_instr)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    IDLE,
                    IDLE,
                    WAIT,
                    IN, rALU, rC, rB,
                    IDLE,
                    WAIT,
                    REP_OP_I, rL, rH, rALU, operation, 2, operation, repeat_instr);

        IRQS = 9;
    }

    private void OUT_OP_R(ushort operation, ushort repeat_instr)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    IDLE,
                    WAIT,
                    RD, rALU, rL, rH,
                    IDLE,
                    IDLE,
                    WAIT,
                    REP_OP_O, rC, rB, rALU, operation, 3, operation, repeat_instr);

        IRQS = 9;
    }

    // this is an indirect change of a a 16 bit register with memory
    private void EXCH_16_IND_(ushort dest_l, ushort dest_h, ushort src_l, ushort src_h)
    {
        PopulateCURINSTR
                (IDLE,
                    IDLE,
                    WAIT,
                    RD_INC, rZ, dest_l, dest_h,
                    IDLE,
                    WAIT,
                    RD, rW, dest_l, dest_h,
                    IDLE,
                    IDLE,
                    WAIT,
                    WR_DEC, dest_l, dest_h, src_h,
                    IDLE,
                    WAIT,
                    WR, dest_l, dest_h, src_l,
                    IDLE,
                    TR16, src_l, src_h, rZ, rW);

        IRQS = 16;
    }
}
