// Block instructions (LDI/LDD/LDIR/LDDR, CPI/CPD/CPIR/CPDR, INI/IND/INIR/INDR, OUTI/OUTD/OTIR/OTDR): the flag and repeat logic of the last slot of each.
// Structure from BizHawk's Z80A Execute.cs (MIT, Copyright (c) BizHawk team); the OUTI/OUTD order of events and the repeat-iteration flag rules differ, see the ledger.

namespace NesEmulator.Sega;

public sealed partial class Z80<TBus>
{
    /// <summary>Last slot of LDI/LDD/LDIR/LDDR: BC--, flags, then either another round (PC back to the opcode) or DE and finish.</summary>
    private void SetFlLdR()
    {
        DEC16_Func(rC, rB);
        bool bcNonZero = (Regs[rC] | Regs[rB]) != 0;
        int n = Regs[rALU] + Regs[rA];
        SetFlags((Regs[rF] & (FS | FZ | FC)) | (bcNonZero ? FPV : 0) | (n & FX) | ((n << 4) & FY));

        Ztemp1 = cur_instr[instr_pntr++];
        Ztemp2 = cur_instr[instr_pntr++];
        Ztemp3 = cur_instr[instr_pntr++];

        if (bcNonZero && Ztemp3 > 0)
        {
            RepeatXY();
            PopulateCURINSTR
                (DEC16, rPCl, rPCh,
                    DEC16, rPCl, rPCh,
                    TR16, rZ, rW, rPCl, rPCh,
                    INC16, rZ, rW,
                    Ztemp2, rE, rD);
            IRQS = 5;
            instr_pntr = irq_pntr = 0;
            I_skip = true;
        }
        else
        {
            if (Ztemp2 == INC16) INC16_Func(rE, rD); else DEC16_Func(rE, rD);
        }
    }

    /// <summary>Last slot of CPI/CPD/CPIR/CPDR: flags from A - (HL), then either another round or finish.</summary>
    private void SetFlCpR()
    {
        int a = Regs[rA], m = Regs[rALU];
        int r = (a - m) & 0xFF;
        int h = (a ^ m ^ r) & FH;
        int n = r - (h != 0 ? 1 : 0);
        bool bcNonZero = (Regs[rC] | Regs[rB]) != 0;
        SetFlags((Regs[rF] & FC) | (r & FS) | (r == 0 ? FZ : 0) | h | FN | (bcNonZero ? FPV : 0) | (n & FX) | ((n << 4) & FY));

        Ztemp1 = cur_instr[instr_pntr++];
        Ztemp2 = cur_instr[instr_pntr++];
        Ztemp3 = cur_instr[instr_pntr++];

        if (bcNonZero && Ztemp3 > 0 && r != 0)
        {
            RepeatXY();
            PopulateCURINSTR
                (DEC16, rPCl, rPCh,
                    DEC16, rPCl, rPCh,
                    TR16, rZ, rW, rPCl, rPCh,
                    INC16, rZ, rW,
                    Ztemp2, rL, rH);
            IRQS = 5;
            instr_pntr = irq_pntr = 0;
            I_skip = true;
        }
        else
        {
            if (Ztemp2 == INC16) INC16_Func(rL, rH); else DEC16_Func(rL, rH);
        }
    }

    /// <summary>Last slot of INI/IND/INIR/INDR: the byte read is in rALU; write it to (HL), step B and HL, set the flags, maybe repeat.</summary>
    private void RepOpI()
    {
        Write_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
        Ztemp4 = cur_instr[instr_pntr++];

        int data = Regs[rALU];
        int k;
        TR16_Func(rZ, rW, rC, rB);
        if (Ztemp4 == DEC16)
        {
            DEC16_Func(rZ, rW);
            k = data + ((Regs[rC] - 1) & 0xFF);
        }
        else
        {
            INC16_Func(rZ, rW);
            k = data + ((Regs[rC] + 1) & 0xFF);
        }
        DEC8_Func(rB);
        BlockIoFlags(data, k);

        Ztemp1 = cur_instr[instr_pntr++];
        Ztemp2 = cur_instr[instr_pntr++];
        Ztemp3 = cur_instr[instr_pntr++];

        if (Regs[rB] != 0 && Ztemp3 > 0)
        {
            RepeatIoFlags(data);
            WZ = (ushort)(RepeatPc() + 1);
            PopulateCURINSTR
                (IDLE,
                    IDLE,
                    DEC16, rPCl, rPCh,
                    DEC16, rPCl, rPCh,
                    Ztemp2, rL, rH);
            IRQS = 5;
            instr_pntr = irq_pntr = 0;
            I_skip = true;
        }
        else
        {
            if (Ztemp2 == INC16) INC16_Func(rL, rH); else DEC16_Func(rL, rH);
        }
    }

    /// <summary>Last slot of OUTI/OUTD/OTIR/OTDR: the byte read from (HL) is in rALU. B is decremented before the port address is put on the bus.</summary>
    private void RepOpO()
    {
        int data = Regs[rALU];
        ushort destL = cur_instr[instr_pntr++], destH = cur_instr[instr_pntr++], src = cur_instr[instr_pntr++];
        Ztemp4 = cur_instr[instr_pntr++];

        DEC8_Func(rB);
        OUT_Func(destL, destH, src);
        TR16_Func(rZ, rW, rC, rB);
        int k;
        if (Ztemp4 == DEC16)
        {
            DEC16_Func(rL, rH);
            DEC16_Func(rZ, rW);
        }
        else
        {
            INC16_Func(rL, rH);
            INC16_Func(rZ, rW);
        }
        k = data + Regs[rL];
        BlockIoFlags(data, k);

        Ztemp1 = cur_instr[instr_pntr++];
        Ztemp2 = cur_instr[instr_pntr++];
        Ztemp3 = cur_instr[instr_pntr++];

        if (Regs[rB] != 0 && Ztemp3 > 0)
        {
            RepeatIoFlags(data);
            WZ = (ushort)(RepeatPc() + 1);
            PopulateCURINSTR
                (IDLE,
                    IDLE,
                    DEC16, rPCl, rPCh,
                    DEC16, rPCl, rPCh,
                    IDLE);
            IRQS = 5;
            instr_pntr = irq_pntr = 0;
            I_skip = true;
        }
    }

    /// <summary>
    /// The flags INI/IND/OUTI/OUTD leave beyond what B's decrement gave (S, Z, 5 and 3 come from the new B): N is bit 7 of the data, H and C are set by the carry
    /// out of <paramref name="k"/> (the data plus the low byte of C+1, C-1 or L), P/V is the parity of (k and 7) xor B (Sean Young, The Undocumented Z80 Documented).
    /// </summary>
    private void BlockIoFlags(int data, int k)
    {
        int f = Regs[rF] & (FS | FZ | FY | FX);
        if ((data & 0x80) != 0) f |= FN;
        if (k > 0xFF) f |= FH | FC;
        f |= Z80Tables.Parity[(k & 7) ^ Regs[rB]];
        SetFlags(f);
    }

    /// <summary>The address of the repeating instruction: PC points past its two opcode bytes while the extra iteration is set up.</summary>
    private int RepeatPc() => ((Regs[rPCl] | Regs[rPCh] << 8) - 2) & 0xFFFF;

    /// <summary>
    /// When LDIR, LDDR, CPIR or CPDR go round again, flag bits 5 and 3 show the high byte of the instruction's own address instead of the data-derived values, and
    /// WZ becomes that address plus one (MEMPTR, as documented for the block instructions).
    /// </summary>
    private void RepeatXY()
    {
        int pc = RepeatPc();
        Regs[rF] = (byte)((Regs[rF] & ~(FY | FX)) | ((pc >> 8) & (FY | FX)));
    }

    /// <summary>
    /// The extra flag changes when INIR, INDR, OTIR or OTDR repeat (found on real Zilog parts after the original documentation; z80test checks them): bits 5 and 3 come from
    /// the high byte of the instruction address and WZ = that address + 1, and H and P/V are modified again from the new B and the data byte.
    /// </summary>
    private void RepeatIoFlags(int data)
    {
        int pc = RepeatPc();
        int f = Regs[rF];
        int b = Regs[rB];
        f = (f & ~(FY | FX)) | ((pc >> 8) & (FY | FX));
        if ((f & FC) != 0)
        {
            int x;
            if ((data & 0x80) != 0) { f = (f & ~FH) | ((b & 0x0F) == 0x00 ? FH : 0); x = (b - 1) & 7; }
            else { f = (f & ~FH) | ((b & 0x0F) == 0x0F ? FH : 0); x = (b + 1) & 7; }
            f ^= Z80Tables.Parity[x] ^ FPV;
        }
        else
        {
            f ^= Z80Tables.Parity[b & 7] ^ FPV;
        }
        Regs[rF] = (byte)f;
    }
}
