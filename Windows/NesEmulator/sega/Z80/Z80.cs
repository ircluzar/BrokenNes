// Z80 core. Derived from BizHawk's Z80A (BizHawk.Emulation.Cores/CPUs/Z80A, by Alyosha and the BizHawk team), MIT License, Copyright (c) BizHawk team.
// What was kept, changed and added is recorded in sega/THIRD_PARTY_NOTICES.md. Verified by Workshop --z80test (SingleStepTests/z80, zexdoc/zexall).

using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>
/// A Zilog Z80 (NMOS, undocumented behaviour included), stepped one T-state at a time. Generic over a struct <typeparamref name="TBus"/> (see <see cref="IZ80Bus"/>)
/// so the bus calls are direct and the core stays AOT/WebAssembly friendly.
/// </summary>
/// <remarks>
/// <para><b>Design (from BizHawk's Z80A).</b> Each instruction is a small "program" of micro-operations, one slot per T-state, built when the opcode has been
/// fetched (the decode tables are the <c>Decode*</c> methods, the programs the <c>*_</c> builders in <c>Z80.Programs.cs</c>). <see cref="Tick"/> runs one slot.
/// Memory and I/O cycles have the hardware shape: T1 (address out), T2 (WAIT sampled), T3 (the access; I/O has one extra wait state before T2). So a board sees every
/// read and write at the T-state where it really happens (<see cref="TotalCycles"/> inside the bus call is that T-state) and can insert wait states through <see cref="Wait"/>.</para>
/// <para><b>Running it.</b> <see cref="Tick"/> for a board that interleaves other chips every T-state; <see cref="ExecuteInstruction"/> for one that syncs after each instruction.
/// To make the CPU wait for the bus (the Genesis Z80 reading 68000 space), either hold <see cref="Wait"/> for as many T-states as it must wait (it is sampled at T2 of the next
/// memory or I/O cycle), or add the delay to the board's own Z80 clock after the access returns. To pause it (BUSREQ), stop calling <see cref="Tick"/>; <see cref="Reset"/>
/// and <see cref="ResetPin"/> are the power-on and the RESET-pin states.</para>
/// <para><b>Interrupts.</b> <see cref="Irq"/> is a level (the line stays asserted until the device releases it, as the VDP does when the status port is read);
/// the CPU looks at it, and at <see cref="RaiseNmi"/>'s edge, at the end of every instruction (never between the bytes of a prefixed opcode, and not after EI until one more
/// instruction has run). IM 0 supports an <c>RST n</c> on the data bus (the Master System puts $FF there), IM 1 jumps to $0038, IM 2 reads the vector table. NMI jumps to $0066.</para>
/// <para><b>Not modelled:</b> the BUSREQ/BUSACK handshake itself (the real chip finishes its current machine cycle before it lets go of the bus), multi-byte instructions on the data
/// bus in IM 0 (only <c>RST n</c>, which is what the hardware sends in practice; anything else costs the two wait states and does nothing), the exact value of WZ after an interrupt
/// response, the CMOS parts' differences (<c>OUT (C),0</c> writing 255, no LD A,I interrupt bug beyond the <see cref="NmosInterruptBug"/> switch), and the address and data
/// bus levels during T-states with no bus access.</para>
/// </remarks>
public sealed partial class Z80<TBus> where TBus : struct, IZ80Bus
{
    // ---- micro-operations (one slot each; some take operands from the following slots)
    private const ushort IDLE = 0;
    private const ushort OP = 1;
    private const ushort OP_F = 2;          // fetch the opcode of the next instruction and decode it
    private const ushort HALT = 3;
    private const ushort RD = 4;
    private const ushort WR = 5;
    private const ushort RD_INC = 6;        // read and increment the address register
    private const ushort WR_INC = 7;        // write and increment
    private const ushort WR_DEC = 8;        // write and decrement (stack pointer)
    private const ushort TR = 9;
    private const ushort TR16 = 10;
    private const ushort ADD16 = 11;
    private const ushort ADD8 = 12;
    private const ushort SUB8 = 13;
    private const ushort ADC8 = 14;
    private const ushort SBC8 = 15;
    private const ushort SBC16 = 16;
    private const ushort ADC16 = 17;
    private const ushort INC16 = 18;
    private const ushort INC8 = 19;
    private const ushort DEC16 = 20;
    private const ushort DEC8 = 21;
    private const ushort RLC = 22;
    private const ushort RL = 23;
    private const ushort RRC = 24;
    private const ushort RR = 25;
    private const ushort CPL = 26;
    private const ushort DA = 27;
    private const ushort SCF = 28;
    private const ushort CCF = 29;
    private const ushort AND8 = 30;
    private const ushort XOR8 = 31;
    private const ushort OR8 = 32;
    private const ushort CP8 = 33;
    private const ushort SLA = 34;
    private const ushort SRA = 35;
    private const ushort SRL = 36;
    private const ushort SLL = 37;
    private const ushort BIT = 38;
    private const ushort RES = 39;
    private const ushort SET = 40;
    private const ushort EI = 41;
    private const ushort DI = 42;
    private const ushort EXCH = 43;
    private const ushort EXX = 44;
    private const ushort EXCH_16 = 45;
    private const ushort PREFIX = 46;
    private const ushort ASGN = 48;
    private const ushort ADDS = 49;         // signed 16-bit add used by relative jumps and indexed addressing
    private const ushort INT_MODE = 50;
    private const ushort EI_RETN = 51;      // RETN and RETI: IFF1 = IFF2
    private const ushort OUT = 53;
    private const ushort IN = 54;
    private const ushort NEG = 55;
    private const ushort RRD = 56;
    private const ushort RLD = 57;
    private const ushort SET_FL_LD_R = 58;
    private const ushort SET_FL_CP_R = 59;
    private const ushort SET_FL_IR = 60;
    private const ushort I_BIT = 61;
    private const ushort FTCH_DB = 63;
    private const ushort WAIT = 64;         // T2 of a memory or I/O cycle: repeats while the WAIT pin is held
    private const ushort RST = 65;
    private const ushort REP_OP_I = 66;
    private const ushort REP_OP_O = 67;
    private const ushort IN_A_N_INC = 68;
    private const ushort RD_INC_TR_PC = 69; // transfer WZ to PC after read
    private const ushort WR_TR_PC = 70;     // transfer WZ to PC after write
    private const ushort OUT_INC = 71;
    private const ushort IN_INC = 72;
    private const ushort WR_INC_WA = 73;    // A -> W after WR_INC
    private const ushort RD_OP = 74;
    private const ushort IORQ = 75;         // interrupt acknowledge cycle
    private const ushort HALT_R = 76;       // the refresh of a halted CPU's NOP cycle
    private const ushort INT0_OP = 77;      // IM 0: execute the instruction the device put on the data bus

    // ---- register file indices. 8-bit registers live in Regs; the micro-ops address them by index so 16-bit pairs and the shadow set need no special cases.
    private const ushort rPCl = 0, rPCh = 1, rSPl = 2, rSPh = 3, rA = 4, rF = 5, rB = 6, rC = 7, rD = 8, rE = 9, rH = 10, rL = 11, rW = 12, rZ = 13;
    private const ushort rAim = 14;         // marks RLCA/RLA/RRCA/RRA, whose flags differ from the CB-prefixed forms
    private const ushort rIxl = 15, rIxh = 16, rIyl = 17, rIyh = 18, rInt = 19, rR = 20, rI = 21;
    private const ushort rZERO = 22;        // always zero, so instructions can be shared
    private const ushort rALU = 23;         // temporary
    private const ushort rA_s = 24, rF_s = 25, rB_s = 26, rC_s = 27, rD_s = 28, rE_s = 29, rH_s = 30, rL_s = 31;
    private const ushort rDB = 32;          // the data bus
    private const ushort rscratch = 33;
    private const ushort rIRQ_V = 34;       // IM 1 vector
    private const ushort rNMI_V = 35;       // NMI vector
    private const int RegCount = 36;

    // ---- flag bits
    private const int FS = 0x80, FZ = 0x40, FY = 0x20, FH = 0x10, FX = 0x08, FPV = 0x04, FN = 0x02, FC = 0x01;

    // ---- prefix states (which decode table the next opcode byte goes through)
    private const int PRE_CB = 0, PRE_ED = 1, PRE_DD = 2, PRE_FD = 3, PRE_DDCB = 4, PRE_FDCB = 5, PRE_NONE = 6;

    private TBus _bus;
    private readonly byte[] Regs = new byte[RegCount];

    // execution state: the current instruction's program and where we are in it
    private readonly ushort[] cur_instr = new ushort[38];
    private int instr_pntr;
    private int irq_pntr;
    private int IRQS;                       // slots in the current program (-1: cannot be interrupted, used by prefixes)
    private byte opcode;
    private int pre = PRE_NONE;
    private int nextPre;
    private bool I_skip;                    // this slot does not count towards the end of the instruction (fetch, wait state, prefix)
    private bool boundary;                  // the last Tick finished an instruction
    private bool halted;
    private int prefixRun;                  // consecutive prefix bytes since the last instruction boundary (see ExecuteInstruction)

    // interrupt and flag-latch state
    private bool iff1, iff2;
    private int interruptMode;
    private bool nmiLine;
    private bool nmiPending;
    private bool ei;                        // the instruction just completed was EI (what the test vectors call "ei")
    private bool eiNew;                     // EI ran in the current instruction: no interrupt is taken at its end
    private bool pFlag;                     // the instruction just completed was LD A,I or LD A,R
    private bool pNext;
    private byte q;                         // F as left by the last instruction if it wrote the flags, else 0 (SCF/CCF need it)
    private bool flagsWritten;              // the current instruction wrote the flags

    /// <summary>Operands and temporaries of the micro-operations (not state).</summary>
    private ushort Ztemp1, Ztemp2, Ztemp3, Ztemp4;

    /// <summary>The level of the INT pin. Keep it asserted until the device releases it; the CPU takes it when IFF1 is set at the end of an instruction.</summary>
    public bool Irq;

    /// <summary>The level of the WAIT pin. Sampled in T2 of every memory and I/O cycle: while it is true the CPU inserts wait states, one T-state per <see cref="Tick"/>.</summary>
    public bool Wait;

    /// <summary>Model the NMOS quirk where an interrupt accepted right after LD A,I or LD A,R clears the parity flag. On for the Zilog and NEC parts the Sega machines used.</summary>
    public bool NmosInterruptBug = true;

    /// <summary>T-states executed since <see cref="Reset"/> (counts wait states and halted cycles).</summary>
    public long TotalCycles;

    public Z80(TBus bus)
    {
        _bus = bus;
        Reset();
    }

    /// <summary>The bus, by reference (it is a struct; its state, if any, lives here).</summary>
    public ref TBus Bus => ref _bus;

    /// <summary>
    /// Power-on: PC, I, R, IFF1, IFF2 and IM cleared, AF and SP set to $FFFF (the NMOS part does that; the rest is undefined and cleared here), the cycle counter zeroed.
    /// </summary>
    public void Reset()
    {
        Array.Clear(Regs);
        Regs[rIRQ_V] = 0x38;                // the IM 1 vector
        Regs[rNMI_V] = 0x66;                // the NMI vector
        Regs[rA] = 0xFF; Regs[rF] = 0xFF;
        Regs[rSPl] = 0xFF; Regs[rSPh] = 0xFF;
        iff1 = iff2 = false;
        interruptMode = 0;
        nmiLine = nmiPending = false;
        ei = eiNew = pFlag = pNext = flagsWritten = false;
        q = 0;
        halted = false;
        pre = PRE_NONE;
        prefixRun = 0;
        TotalCycles = 0;
        I_skip = false;
        boundary = true;
        InstallFetch();
    }

    /// <summary>
    /// The RESET pin (what the Genesis 68000 does through $A11200, and what power-on does to the part's control state): PC, I, R, IFF1, IFF2 and IM are cleared, the instruction in
    /// flight and any pending interrupt are dropped, the HALT state ends. The general registers (including the shadow set, IX, IY and SP) keep their values, as on the chip.
    /// <see cref="TotalCycles"/> is not touched.
    /// </summary>
    public void ResetPin()
    {
        Regs[rPCl] = Regs[rPCh] = 0;
        Regs[rI] = 0; Regs[rR] = 0;
        iff1 = iff2 = false;
        interruptMode = 0;
        nmiPending = false;
        ei = eiNew = pFlag = pNext = flagsWritten = false;
        q = 0;
        halted = false;
        pre = PRE_NONE;
        prefixRun = 0;
        I_skip = false;
        boundary = true;
        InstallFetch();
    }

    // ------------------------------------------------------------------------------------------------------------------------------------ stepping

    /// <summary>Runs one T-state.</summary>
    public void Tick()
    {
        boundary = false;
        switch (cur_instr[instr_pntr++])
        {
            case IDLE:
                break;
            case OP:
                break;
            case OP_F:
                // Opcode fetch (T3 of M1). The new instruction's own program starts in the slot after this one (T4).
                opcode = _bus.FetchOpcode((ushort)(Regs[rPCl] | Regs[rPCh] << 8));
                IncPC();
                IncR();
                FetchInstruction();
                instr_pntr = irq_pntr = 0;
                I_skip = true;
                break;
            case HALT:
                halted = true;
                break;
            case HALT_R:
                Regs[rDB] = 0xFF;
                IncR();
                break;
            case RD:
                Read_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case WR:
                Write_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case RD_INC:
                Read_INC_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case RD_INC_TR_PC:
                Read_INC_TR_PC_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case RD_OP:
                if (cur_instr[instr_pntr++] == 1) { Read_INC_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]); }
                else { Read_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]); }

                switch (cur_instr[instr_pntr++])
                {
                    case ADD8: ADD8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]); break;
                    case ADC8: ADC8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]); break;
                    case SUB8: SUB8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]); break;
                    case SBC8: SBC8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]); break;
                    case AND8: AND8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]); break;
                    case XOR8: XOR8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]); break;
                    case OR8: OR8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]); break;
                    case CP8: CP8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]); break;
                    case TR: TR_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]); break;
                }
                break;
            case WR_INC:
                Write_INC_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case WR_DEC:
                Write_DEC_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case WR_TR_PC:
                Write_TR_PC_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case WR_INC_WA:
                Write_INC_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                Regs[rW] = Regs[rA];
                break;
            case TR:
                TR_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case TR16:
                TR16_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case ADD16:
                ADD16_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case ADD8:
                ADD8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case SUB8:
                SUB8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case ADC8:
                ADC8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case ADC16:
                ADC_16_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case SBC8:
                SBC8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case SBC16:
                SBC_16_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case INC16:
                INC16_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case INC8:
                INC8_Func(cur_instr[instr_pntr++]);
                break;
            case DEC16:
                DEC16_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case DEC8:
                DEC8_Func(cur_instr[instr_pntr++]);
                break;
            case RLC:
                RLC_Func(cur_instr[instr_pntr++]);
                break;
            case RL:
                RL_Func(cur_instr[instr_pntr++]);
                break;
            case RRC:
                RRC_Func(cur_instr[instr_pntr++]);
                break;
            case RR:
                RR_Func(cur_instr[instr_pntr++]);
                break;
            case CPL:
                CPL_Func(cur_instr[instr_pntr++]);
                break;
            case DA:
                DA_Func(cur_instr[instr_pntr++]);
                break;
            case SCF:
                SCF_Func(cur_instr[instr_pntr++]);
                break;
            case CCF:
                CCF_Func(cur_instr[instr_pntr++]);
                break;
            case AND8:
                AND8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case XOR8:
                XOR8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case OR8:
                OR8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case CP8:
                CP8_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case SLA:
                SLA_Func(cur_instr[instr_pntr++]);
                break;
            case SRA:
                SRA_Func(cur_instr[instr_pntr++]);
                break;
            case SRL:
                SRL_Func(cur_instr[instr_pntr++]);
                break;
            case SLL:
                SLL_Func(cur_instr[instr_pntr++]);
                break;
            case BIT:
                BIT_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case I_BIT:
                I_BIT_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case RES:
                RES_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case SET:
                SET_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case EI:
                // IFF1 and IFF2 are set at once; the interrupt is only held off until the instruction after EI has finished.
                iff1 = iff2 = true;
                eiNew = true;
                break;
            case DI:
                iff1 = iff2 = false;
                break;
            case EXCH:
                EXCH_16_Func(rF_s, rA_s, rF, rA);
                break;
            case EXX:
                EXCH_16_Func(rC_s, rB_s, rC, rB);
                EXCH_16_Func(rE_s, rD_s, rE, rD);
                EXCH_16_Func(rL_s, rH_s, rL, rH);
                break;
            case EXCH_16:
                EXCH_16_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case PREFIX:
                // The prefix byte's own M1 has run; this slot is T3 of the next M1, which fetches the byte after the prefix.
                pre = nextPre;
                prefixRun++;
                // a prefix is an instruction of its own as far as the flag latch is concerned (it does not write the flags)
                q = 0;
                flagsWritten = false;
                // refresh increments for every M1, but the DD CB d op / FD CB d op bytes after the prefixes are plain reads
                // (and that byte, the operation, is an ordinary memory read, not an opcode fetch)
                if (nextPre < PRE_DDCB)
                {
                    IncR();
                    opcode = _bus.FetchOpcode((ushort)(Regs[rPCl] | Regs[rPCh] << 8));
                }
                else
                {
                    opcode = _bus.Read((ushort)(Regs[rPCl] | Regs[rPCh] << 8));
                }
                IncPC();
                FetchInstruction();
                instr_pntr = irq_pntr = 0;
                I_skip = true;
                break;
            case ASGN:
                ASGN_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case ADDS:
                ADDS_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case EI_RETN:
                iff1 = iff2;
                break;
            case OUT:
                OUT_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case OUT_INC:
                OUT_INC_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case IN:
                IN_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case IN_INC:
                IN_INC_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case IN_A_N_INC:
                IN_A_N_INC_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case NEG:
                NEG_8_Func(cur_instr[instr_pntr++]);
                break;
            case INT_MODE:
                interruptMode = cur_instr[instr_pntr++];
                break;
            case RRD:
                RRD_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case RLD:
                RLD_Func(cur_instr[instr_pntr++], cur_instr[instr_pntr++]);
                break;
            case SET_FL_LD_R:
                SetFlLdR();
                break;
            case SET_FL_CP_R:
                SetFlCpR();
                break;
            case SET_FL_IR:
                ushort dest_t = cur_instr[instr_pntr++];
                TR_Func(dest_t, cur_instr[instr_pntr++]);
                SET_FL_IR_Func(dest_t);
                break;
            case FTCH_DB:
                FTCH_DB_Func();
                break;
            case WAIT:
                if (Wait)
                {
                    instr_pntr--;
                    I_skip = true;
                }
                break;
            case RST:
                Regs[rZ] = (byte)cur_instr[instr_pntr++];
                Regs[rW] = 0;
                break;
            case REP_OP_I:
                RepOpI();
                break;
            case REP_OP_O:
                RepOpO();
                break;
            case IORQ:
                // interrupt acknowledge cycle: nothing for the board to do (the vector is read by FTCH_DB / INT0_OP)
                break;
            case INT0_OP:
                Int0Execute();
                break;
        }

        if (I_skip)
        {
            I_skip = false;
        }
        else if (++irq_pntr == IRQS)
        {
            EndOfInstruction();
        }

        TotalCycles++;
    }

    /// <summary>
    /// Runs T-states until the next instruction boundary and returns how many it took. An interrupt response counts as one instruction, and so does one
    /// 4-T-state NOP cycle of a halted CPU. Starts at a boundary (after <see cref="Reset"/> or a previous call).
    /// </summary>
    public int ExecuteInstruction()
    {
        long start = TotalCycles;
        prefixRun = 0;
        do { Tick(); } while (!boundary && prefixRun < MaxPrefixRun);
        return (int)(TotalCycles - start);
    }

    /// <summary>
    /// A run of DD/FD/ED/CB prefix bytes never ends an instruction on the real chip, which keeps executing them (4 T-states each, interrupts held off) for as long as they come,
    /// as when it runs through memory full of $DD. <see cref="ExecuteInstruction"/> returns after this many in a row so a board's loop cannot be trapped there; the next call carries on.
    /// </summary>
    private const int MaxPrefixRun = 32;

    /// <summary>True when the next <see cref="Tick"/> starts an instruction (or an interrupt response, or a halted NOP cycle).</summary>
    public bool AtInstructionBoundary => boundary;

    /// <summary>The CPU is executing HALT (it runs NOP cycles, with refresh, until an interrupt).</summary>
    public bool Halted => halted;

    /// <summary>Request a non-maskable interrupt. It is edge-triggered: call it once per rising edge of the NMI pin; it is taken at the end of the current instruction.</summary>
    public void RaiseNmi()
    {
        nmiPending = true;
    }

    /// <summary>Drive the level of the NMI pin (a rising edge raises the interrupt). A convenience over <see cref="RaiseNmi"/> for boards that model the pin.</summary>
    public bool NmiLine
    {
        get => nmiLine;
        set { if (value && !nmiLine) nmiPending = true; nmiLine = value; }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------ boundaries

    private void EndOfInstruction()
    {
        boundary = true;
        prefixRun = 0;

        // Q: the flags as the instruction left them, if it wrote them
        q = flagsWritten ? Regs[rF] : (byte)0;
        flagsWritten = false;
        pFlag = pNext;
        pNext = false;

        // EI holds interrupts off until the instruction after it has run; the "ei" latch stays visible for one instruction
        bool inhibit = eiNew;
        eiNew = false;
        if (!inhibit) ei = false; else ei = true;

        if (nmiPending)
        {
            nmiPending = false;
            iff2 = iff1;
            iff1 = false;
            NMI_();
            instr_pntr = irq_pntr = 0;
            IncR();
            halted = false;
            q = 0;
        }
        else if (iff1 && Irq && !inhibit)
        {
            iff1 = iff2 = false;
            // NMOS bug: an interrupt accepted right after LD A,I / LD A,R reports IFF2 = 0 in P/V
            if (pFlag && NmosInterruptBug) Regs[rF] &= unchecked((byte)~FPV);
            switch (interruptMode)
            {
                case 0: INTERRUPT_0(); break;
                case 1: INTERRUPT_1(); break;
                default: INTERRUPT_2(); break;
            }
            instr_pntr = irq_pntr = 0;
            IncR();
            halted = false;
        }
        else if (!halted)
        {
            InstallFetch();
        }
        else
        {
            // halted: NOP cycles with refresh, PC stays on the byte after HALT
            PopulateCURINSTR(IDLE, IDLE, IDLE, HALT_R);
            IRQS = 4;
            instr_pntr = irq_pntr = 0;
        }
    }

    /// <summary>Installs the M1 program: T1, T2, then the opcode fetch in T3. The instruction's own program takes over for T4.</summary>
    private void InstallFetch()
    {
        PopulateCURINSTR(IDLE, WAIT, OP_F, OP);
        IRQS = 4;
        instr_pntr = irq_pntr = 0;
    }

    // flag conditions used by the decode tables
    private bool FlagC => (Regs[rF] & FC) != 0;
    private bool FlagP => (Regs[rF] & FPV) != 0;
    private bool FlagZ => (Regs[rF] & FZ) != 0;
    private bool FlagS => (Regs[rF] & FS) != 0;

    private void IncR()
    {
        Regs[rR] = (byte)((Regs[rR] & 0x80) | ((Regs[rR] + 1) & 0x7F));
    }

    private void IncPC()
    {
        if (++Regs[rPCl] == 0) Regs[rPCh]++;
    }

    private void FetchInstruction()
    {
        int table = pre;
        pre = PRE_NONE;
        switch (table)
        {
            case PRE_NONE: DecodeBase(); break;
            case PRE_CB: DecodeCB(); break;
            case PRE_ED: DecodeED(); break;
            case PRE_DD: DecodeDD(); break;
            case PRE_FD: DecodeFD(); break;
            default: DecodeIndexedCB(); break;
        }
    }

    /// <summary>Copies a micro-program into the current-instruction buffer (slots past the program are never read).</summary>
    private void PopulateCURINSTR(params ReadOnlySpan<ushort> program)
    {
        program.CopyTo(cur_instr);
    }
}
