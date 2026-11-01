// Micro-operation executors and ALU. The slot structure (Read_Func, Write_Func, ..., the *_Func names and signatures) follows BizHawk's Z80A Operations.cs
// (MIT, Copyright (c) BizHawk team); the flag arithmetic is rewritten here on a single F byte so each instruction writes the flags once (and so the Q latch
// SCF/CCF need can be tracked), and the block-instruction flag rules were checked against SingleStepTests/z80, z80test and zexall.

namespace NesEmulator.Sega;

/// <summary>Constant tables shared by every <see cref="Z80{TBus}"/> instantiation.</summary>
internal static class Z80Tables
{
    /// <summary>The parity flag (0x04 when the byte has an even number of set bits) for each byte.</summary>
    internal static readonly byte[] Parity = BuildParity();

    private static byte[] BuildParity()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int bits = 0;
            for (int j = 0; j < 8; j++) bits += (i >> j) & 1;
            t[i] = (bits & 1) == 0 ? (byte)0x04 : (byte)0;
        }
        return t;
    }
}

public sealed partial class Z80<TBus>
{
    // ---------------------------------------------------------------------------------------------------------------- bus micro-operations

    private ushort AddrOf(ushort lo, ushort hi) => (ushort)(Regs[lo] | Regs[hi] << 8);

    private void Read_Func(ushort dest, ushort src_l, ushort src_h)
    {
        byte v = _bus.Read(AddrOf(src_l, src_h));
        Regs[dest] = v;
        Regs[rDB] = v;
    }

    private void Read_INC_Func(ushort dest, ushort src_l, ushort src_h)
    {
        byte v = _bus.Read(AddrOf(src_l, src_h));
        Regs[dest] = v;
        Regs[rDB] = v;
        INC16_Func(src_l, src_h);
    }

    private void Read_INC_TR_PC_Func(ushort dest_l, ushort dest_h, ushort src_l, ushort src_h)
    {
        byte v = _bus.Read(AddrOf(src_l, src_h));
        Regs[dest_h] = v;
        Regs[rDB] = v;
        INC16_Func(src_l, src_h);
        TR16_Func(rPCl, rPCh, dest_l, dest_h);
    }

    private void Write_Func(ushort dest_l, ushort dest_h, ushort src)
    {
        Regs[rDB] = Regs[src];
        _bus.Write(AddrOf(dest_l, dest_h), Regs[src]);
    }

    private void Write_INC_Func(ushort dest_l, ushort dest_h, ushort src)
    {
        Regs[rDB] = Regs[src];
        _bus.Write(AddrOf(dest_l, dest_h), Regs[src]);
        INC16_Func(dest_l, dest_h);
    }

    private void Write_DEC_Func(ushort dest_l, ushort dest_h, ushort src)
    {
        Regs[rDB] = Regs[src];
        _bus.Write(AddrOf(dest_l, dest_h), Regs[src]);
        DEC16_Func(dest_l, dest_h);
    }

    private void Write_TR_PC_Func(ushort dest_l, ushort dest_h, ushort src)
    {
        Regs[rDB] = Regs[src];
        _bus.Write(AddrOf(dest_l, dest_h), Regs[src]);
        TR16_Func(rPCl, rPCh, rZ, rW);
    }

    private void OUT_Func(ushort dest_l, ushort dest_h, ushort src)
    {
        Regs[rDB] = Regs[src];
        _bus.Out(AddrOf(dest_l, dest_h), Regs[src]);
    }

    private void OUT_INC_Func(ushort dest_l, ushort dest_h, ushort src)
    {
        Regs[rDB] = Regs[src];
        _bus.Out(AddrOf(dest_l, dest_h), Regs[src]);
        INC16_Func(dest_l, dest_h);
    }

    private void IN_Func(ushort dest, ushort src_l, ushort src_h)
    {
        byte v = _bus.In(AddrOf(src_l, src_h));
        Regs[dest] = v;
        Regs[rDB] = v;
        InFlags(v);
    }

    private void IN_INC_Func(ushort dest, ushort src_l, ushort src_h)
    {
        byte v = _bus.In(AddrOf(src_l, src_h));
        Regs[dest] = v;
        Regs[rDB] = v;
        InFlags(v);
        INC16_Func(src_l, src_h);
    }

    private void IN_A_N_INC_Func(ushort dest, ushort src_l, ushort src_h)
    {
        byte v = _bus.In(AddrOf(src_l, src_h));
        Regs[dest] = v;
        Regs[rDB] = v;
        INC16_Func(src_l, src_h);
    }

    private void InFlags(int v)
    {
        SetFlags((Regs[rF] & FC) | (v & (FS | FY | FX)) | (v == 0 ? FZ : 0) | Z80Tables.Parity[v]);
    }

    private void FTCH_DB_Func()
    {
        Regs[rDB] = _bus.InterruptVector();
    }

    // ---------------------------------------------------------------------------------------------------------------- moves

    private void TR_Func(ushort dest, ushort src) => Regs[dest] = Regs[src];

    private void TR16_Func(ushort dest_l, ushort dest_h, ushort src_l, ushort src_h)
    {
        Regs[dest_l] = Regs[src_l];
        Regs[dest_h] = Regs[src_h];
    }

    private void ASGN_Func(ushort dest, ushort val) => Regs[dest] = (byte)val;

    private void EXCH_16_Func(ushort dest_l, ushort dest_h, ushort src_l, ushort src_h)
    {
        byte t = Regs[dest_l]; Regs[dest_l] = Regs[src_l]; Regs[src_l] = t;
        t = Regs[dest_h]; Regs[dest_h] = Regs[src_h]; Regs[src_h] = t;
    }

    private void INC16_Func(ushort lo, ushort hi)
    {
        if (++Regs[lo] == 0) Regs[hi]++;
    }

    private void DEC16_Func(ushort lo, ushort hi)
    {
        if (Regs[lo]-- == 0) Regs[hi]--;
    }

    // signed 8-bit displacement added to a 16-bit register pair (relative jumps, indexed addressing); no flags
    private void ADDS_Func(ushort dest_l, ushort dest_h, ushort src_l, ushort src_h)
    {
        int d = (Regs[dest_l] | Regs[dest_h] << 8) + (sbyte)Regs[src_l];
        Regs[dest_l] = (byte)d;
        Regs[dest_h] = (byte)(d >> 8);
    }

    // ---------------------------------------------------------------------------------------------------------------- flags

    /// <summary>Writes F and notes that this instruction wrote the flags.</summary>
    private void SetFlags(int f)
    {
        Regs[rF] = (byte)f;
        flagsWritten = true;
    }

    // ---------------------------------------------------------------------------------------------------------------- 8-bit arithmetic

    private void ADD8_Func(ushort dest, ushort src)
    {
        int a = Regs[dest], b = Regs[src];
        int sum = a + b;
        int r = sum & 0xFF;
        SetFlags((r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | ((a ^ b ^ r) & FH) | ((((a ^ r) & (b ^ r)) >> 5) & FPV) | (sum >> 8));
        Regs[dest] = (byte)r;
    }

    private void ADC8_Func(ushort dest, ushort src)
    {
        int a = Regs[dest], b = Regs[src];
        int sum = a + b + (Regs[rF] & FC);
        int r = sum & 0xFF;
        SetFlags((r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | ((a ^ b ^ r) & FH) | ((((a ^ r) & (b ^ r)) >> 5) & FPV) | (sum >> 8));
        Regs[dest] = (byte)r;
    }

    private void SUB8_Func(ushort dest, ushort src)
    {
        int a = Regs[dest], b = Regs[src];
        int diff = a - b;
        int r = diff & 0xFF;
        SetFlags((r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | ((a ^ b ^ r) & FH) | ((((a ^ b) & (a ^ r)) >> 5) & FPV) | FN | ((diff >> 8) & FC));
        Regs[dest] = (byte)r;
    }

    private void SBC8_Func(ushort dest, ushort src)
    {
        int a = Regs[dest], b = Regs[src];
        int diff = a - b - (Regs[rF] & FC);
        int r = diff & 0xFF;
        SetFlags((r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | ((a ^ b ^ r) & FH) | ((((a ^ b) & (a ^ r)) >> 5) & FPV) | FN | ((diff >> 8) & FC));
        Regs[dest] = (byte)r;
    }

    private void CP8_Func(ushort dest, ushort src)
    {
        int a = Regs[dest], b = Regs[src];
        int diff = a - b;
        int r = diff & 0xFF;
        // the undocumented flags come from the operand, not the result
        SetFlags((r & FS) | (b & (FY | FX)) | (r == 0 ? FZ : 0) | ((a ^ b ^ r) & FH) | ((((a ^ b) & (a ^ r)) >> 5) & FPV) | FN | ((diff >> 8) & FC));
    }

    private void AND8_Func(ushort dest, ushort src)
    {
        int r = Regs[dest] & Regs[src];
        Regs[dest] = (byte)r;
        SetFlags((r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | FH | Z80Tables.Parity[r]);
    }

    private void OR8_Func(ushort dest, ushort src)
    {
        int r = Regs[dest] | Regs[src];
        Regs[dest] = (byte)r;
        SetFlags((r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | Z80Tables.Parity[r]);
    }

    private void XOR8_Func(ushort dest, ushort src)
    {
        int r = Regs[dest] ^ Regs[src];
        Regs[dest] = (byte)r;
        SetFlags((r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | Z80Tables.Parity[r]);
    }

    private void INC8_Func(ushort src)
    {
        int v = Regs[src];
        int r = (v + 1) & 0xFF;
        Regs[src] = (byte)r;
        SetFlags((Regs[rF] & FC) | (r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | ((v & 0x0F) == 0x0F ? FH : 0) | (v == 0x7F ? FPV : 0));
    }

    private void DEC8_Func(ushort src)
    {
        int v = Regs[src];
        int r = (v - 1) & 0xFF;
        Regs[src] = (byte)r;
        SetFlags((Regs[rF] & FC) | (r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | ((v & 0x0F) == 0 ? FH : 0) | (v == 0x80 ? FPV : 0) | FN);
    }

    private void NEG_8_Func(ushort src)
    {
        int a = Regs[src];
        int r = (-a) & 0xFF;
        SetFlags((r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | ((a ^ r) & FH) | (a == 0x80 ? FPV : 0) | FN | (a != 0 ? FC : 0));
        Regs[src] = (byte)r;
    }

    private void DA_Func(ushort src)
    {
        int a = Regs[src];
        int f = Regs[rF];
        int r = a;
        if ((f & FN) != 0)
        {
            if ((f & FH) != 0 || (a & 0x0F) > 9) r -= 0x06;
            if ((f & FC) != 0 || a > 0x99) r -= 0x60;
        }
        else
        {
            if ((f & FH) != 0 || (a & 0x0F) > 9) r += 0x06;
            if ((f & FC) != 0 || a > 0x99) r += 0x60;
        }
        r &= 0xFF;
        SetFlags((r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | ((a ^ r) & FH) | Z80Tables.Parity[r] | (f & FN) | ((f & FC) != 0 || a > 0x99 ? FC : 0));
        Regs[src] = (byte)r;
    }

    private void CPL_Func(ushort src)
    {
        int r = ~Regs[src] & 0xFF;
        Regs[src] = (byte)r;
        SetFlags((Regs[rF] & (FS | FZ | FPV | FC)) | (r & (FY | FX)) | FH | FN);
    }

    /// <summary>SCF and CCF take bits 5 and 3 of the flags from A, plus (if the previous instruction did not write the flags) from the old F: the Q latch.</summary>
    private int ScfCcfXy(int a) => ((q ^ Regs[rF]) | a) & (FY | FX);

    private void SCF_Func(ushort src)
    {
        int f = Regs[rF];
        SetFlags((f & (FS | FZ | FPV)) | ScfCcfXy(Regs[src]) | FC);
    }

    private void CCF_Func(ushort src)
    {
        int f = Regs[rF];
        SetFlags((f & (FS | FZ | FPV)) | ScfCcfXy(Regs[src]) | ((f & FC) != 0 ? FH : FC));
    }

    // ---------------------------------------------------------------------------------------------------------------- 16-bit arithmetic

    private void ADD16_Func(ushort dest_l, ushort dest_h, ushort src_l, ushort src_h)
    {
        int d = Regs[dest_l] | Regs[dest_h] << 8;
        int s = Regs[src_l] | Regs[src_h] << 8;
        int sum = d + s;
        SetFlags((Regs[rF] & (FS | FZ | FPV)) | ((sum >> 8) & (FY | FX)) | (((d ^ s ^ sum) >> 8) & FH) | (sum >> 16));
        Regs[dest_l] = (byte)sum;
        Regs[dest_h] = (byte)(sum >> 8);
    }

    private void ADC_16_Func(ushort dest_l, ushort dest_h, ushort src_l, ushort src_h)
    {
        int d = Regs[dest_l] | Regs[dest_h] << 8;
        int s = Regs[src_l] | Regs[src_h] << 8;
        int sum = d + s + (Regs[rF] & FC);
        int r = sum & 0xFFFF;
        SetFlags(((r >> 8) & (FS | FY | FX)) | (r == 0 ? FZ : 0) | (((d ^ s ^ sum) >> 8) & FH) | ((((d ^ r) & (s ^ r)) >> 13) & FPV) | (sum >> 16));
        Regs[dest_l] = (byte)r;
        Regs[dest_h] = (byte)(r >> 8);
    }

    private void SBC_16_Func(ushort dest_l, ushort dest_h, ushort src_l, ushort src_h)
    {
        int d = Regs[dest_l] | Regs[dest_h] << 8;
        int s = Regs[src_l] | Regs[src_h] << 8;
        int diff = d - s - (Regs[rF] & FC);
        int r = diff & 0xFFFF;
        SetFlags(((r >> 8) & (FS | FY | FX)) | (r == 0 ? FZ : 0) | (((d ^ s ^ diff) >> 8) & FH) | ((((d ^ s) & (d ^ r)) >> 13) & FPV) | FN | ((diff >> 16) & FC));
        Regs[dest_l] = (byte)r;
        Regs[dest_h] = (byte)(r >> 8);
    }

    // ---------------------------------------------------------------------------------------------------------------- rotates and shifts

    /// <summary>The flags of a CB-prefixed rotate/shift (and of the ED RRD/RLD-style results): S, Z, 5, 3, parity from the result, carry given.</summary>
    private void ShiftFlags(int r, int carry)
    {
        SetFlags((r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | Z80Tables.Parity[r] | carry);
    }

    /// <summary>The flags of RLCA, RLA, RRCA, RRA: only 5, 3 and carry change.</summary>
    private void AccShiftFlags(int r, int carry)
    {
        SetFlags((Regs[rF] & (FS | FZ | FPV)) | (r & (FY | FX)) | carry);
    }

    private void RLC_Func(ushort src)
    {
        bool imm = src == rAim;
        if (imm) src = rA;
        int v = Regs[src];
        int r = ((v << 1) | (v >> 7)) & 0xFF;
        Regs[src] = (byte)r;
        if (imm) AccShiftFlags(r, v >> 7); else ShiftFlags(r, v >> 7);
    }

    private void RRC_Func(ushort src)
    {
        bool imm = src == rAim;
        if (imm) src = rA;
        int v = Regs[src];
        int r = ((v >> 1) | (v << 7)) & 0xFF;
        Regs[src] = (byte)r;
        if (imm) AccShiftFlags(r, v & 1); else ShiftFlags(r, v & 1);
    }

    private void RL_Func(ushort src)
    {
        bool imm = src == rAim;
        if (imm) src = rA;
        int v = Regs[src];
        int r = ((v << 1) | (Regs[rF] & FC)) & 0xFF;
        Regs[src] = (byte)r;
        if (imm) AccShiftFlags(r, v >> 7); else ShiftFlags(r, v >> 7);
    }

    private void RR_Func(ushort src)
    {
        bool imm = src == rAim;
        if (imm) src = rA;
        int v = Regs[src];
        int r = ((v >> 1) | ((Regs[rF] & FC) << 7)) & 0xFF;
        Regs[src] = (byte)r;
        if (imm) AccShiftFlags(r, v & 1); else ShiftFlags(r, v & 1);
    }

    private void SLA_Func(ushort src)
    {
        int v = Regs[src];
        int r = (v << 1) & 0xFF;
        Regs[src] = (byte)r;
        ShiftFlags(r, v >> 7);
    }

    private void SLL_Func(ushort src)
    {
        int v = Regs[src];
        int r = ((v << 1) | 1) & 0xFF;
        Regs[src] = (byte)r;
        ShiftFlags(r, v >> 7);
    }

    private void SRA_Func(ushort src)
    {
        int v = Regs[src];
        int r = (v >> 1) | (v & 0x80);
        Regs[src] = (byte)r;
        ShiftFlags(r, v & 1);
    }

    private void SRL_Func(ushort src)
    {
        int v = Regs[src];
        int r = v >> 1;
        Regs[src] = (byte)r;
        ShiftFlags(r, v & 1);
    }

    private void RRD_Func(ushort dest, ushort src) => RotateDigits(dest, src, right: true);

    private void RLD_Func(ushort dest, ushort src) => RotateDigits(dest, src, right: false);

    // dest holds the memory byte (rALU), src is the accumulator
    private void RotateDigits(ushort mem, ushort acc, bool right)
    {
        int m = Regs[mem], a = Regs[acc];
        int newMem, newA;
        if (right)
        {
            newMem = ((a & 0x0F) << 4) | (m >> 4);
            newA = (a & 0xF0) | (m & 0x0F);
        }
        else
        {
            newMem = ((m & 0x0F) << 4) | (a & 0x0F);
            newA = (a & 0xF0) | (m >> 4);
        }
        Regs[mem] = (byte)newMem;
        Regs[acc] = (byte)newA;
        SetFlags((Regs[rF] & FC) | (newA & (FS | FY | FX)) | (newA == 0 ? FZ : 0) | Z80Tables.Parity[newA]);
    }

    // ---------------------------------------------------------------------------------------------------------------- bit operations

    private void BIT_Func(ushort bit, ushort src)
    {
        int v = Regs[src];
        bool set = (v & (1 << bit)) != 0;
        SetFlags((Regs[rF] & FC) | FH | (set ? 0 : FZ | FPV) | (bit == 7 && set ? FS : 0) | (v & (FY | FX)));
    }

    // BIT n,(HL) and BIT n,(IX+d): bits 5 and 3 come from the high byte of MEMPTR/WZ (for the indexed form that is the high byte of IX+d)
    private void I_BIT_Func(ushort bit, ushort src)
    {
        int v = Regs[src];
        bool set = (v & (1 << bit)) != 0;
        SetFlags((Regs[rF] & FC) | FH | (set ? 0 : FZ | FPV) | (bit == 7 && set ? FS : 0) | (Regs[rW] & (FY | FX)));
    }

    private void SET_Func(ushort bit, ushort src) => Regs[src] |= (byte)(1 << bit);

    private void RES_Func(ushort bit, ushort src) => Regs[src] &= (byte)~(1 << bit);

    // ---------------------------------------------------------------------------------------------------------------- LD A,I / LD A,R

    private void SET_FL_IR_Func(ushort dest)
    {
        if (dest != rA) return;
        int a = Regs[rA];
        SetFlags((Regs[rF] & FC) | (a & (FS | FY | FX)) | (a == 0 ? FZ : 0) | (iff2 ? FPV : 0));
        pNext = true;
    }
}
