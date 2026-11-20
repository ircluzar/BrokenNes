using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace NesEmulator.Sega;

/// <summary>
/// The SVP (Sega Virtua Processor) of Virtua Racing: a Samsung SSP1601 DSP with its two 256-word internal RAM banks, 1 KW of instruction RAM (IRAM),
/// 128 KB of DRAM shared with the 68000 ($300000-$31FFFF, with "cell" read views at $390000 and $3A0000), its programmable memory registers PM0-PM4 and
/// the 68000 mailbox at $A15000 (XST) / $A15004 (status). The DSP runs its program from the cartridge ROM ($800 onwards) and writes the 3D view into DRAM,
/// which the 68000 DMAs into the VDP.
/// </summary>
/// <remarks>
/// A C# port of Genesis Plus GX's core/cart_hw/svp (ssp16.c, svp.c): "basic, incomplete SSP160x (SSP1601?) interpreter with SVP memory controller emu,
/// (c) Copyright 2008, Grazvydas "notaz" Ignotas. Free for non-commercial use. For commercial use, separate licencing terms must be obtained.
/// Modified for Genesis Plus GX (Eke-Eke)". The non-commercial licence was accepted by the user on 2026-11-19; the sources are kept with their headers
/// in C:\BrokenNes-tools\sega\port-sources\genplus-svp. Behaviour (including the idle-loop detection by program address and the per-line cycle budget)
/// follows Genplus so the picture matches BizHawk's Genplus-gx core.
/// </remarks>
public sealed class MdSvp
{
    /// <summary>SSP1601 instructions run per scanline (Genplus-gx: SVP_cycles).</summary>
    public const int CyclesPerLine = 800;

    // memory: words, as the DSP sees them
    private readonly ushort[] iramRom = new ushort[0x10000];   // IRAM (words 0-$3FF) and the program ROM (words $400-$FFFF = bytes $800-$1FFFF)
    public readonly ushort[] Dram = new ushort[0x10000];        // 128 KB
    private readonly ushort[] ram = new ushort[512];            // RAM0 (0-255), RAM1 (256-511)
    private readonly ushort[] romWords;                          // the cartridge ROM as words (2 MB)

    // registers (the "h" halves Genplus keeps in gr[].byte.h)
    private ushort rX, rY, rST, rSTACK, rPC, rPM0, rPM1, rPM2, rXST, rPM4;
    private uint rA, rP, rPMC;                                   // A 32 bit; P 32 bit; PMC: addr in low half, mode in high half
    private readonly byte[] rIJ = new byte[8];
    private readonly ushort[] stack = new ushort[6];
    private readonly uint[] pmac = new uint[12];                 // [write*6 + reg]
    private uint emuStatus;

    private const uint PMC_HAVE_ADDR = 0x0001, PMC_SET = 0x0002, WAIT_PM0 = 0x2000, WAIT_30FE06 = 0x4000, WAIT_30FE08 = 0x8000, WAIT_MASK = 0xF000;
    private const ushort FLAG_Z = 1 << 13, FLAG_N = 1 << 15, FLAG_L = 1 << 12, FLAG_V = 1 << 14;

    private int pc;          // word index into iramRom while running
    private int cycles;

    public long Instructions;
    /// <summary>The last scanline (frames * lines + line) the SVP ran for (the board's bookkeeping, kept in the state).</summary>
    public long LineMark = -1;

    public MdSvp(byte[] rom)
    {
        romWords = new ushort[0x100000];
        for (int i = 0; i < romWords.Length && 2 * i + 1 < rom.Length; i++) romWords[i] = (ushort)(rom[2 * i] << 8 | rom[2 * i + 1]);
        Reset();
    }

    public void Reset()
    {
        Array.Clear(iramRom); Array.Clear(Dram); Array.Clear(ram);
        Array.Copy(romWords, 0x400, iramRom, 0x400, 0x10000 - 0x400);
        rX = rY = rST = rSTACK = rPM0 = rPM1 = rPM2 = rXST = rPM4 = 0; rA = rP = rPMC = 0;
        Array.Clear(rIJ); Array.Clear(stack); Array.Clear(pmac);
        emuStatus = 0; LineMark = -1;
        rPC = 0x400;
        rSTACK = 0; rST = 0;
    }

    // ---- the 68000's side

    /// <summary>A word read at $300000-$3FFFFF (DRAM and its cell views); <paramref name="value"/> is set when the SVP answers.</summary>
    public bool ReadWord(uint address, out ushort value)
    {
        uint bank = address >> 16 & 0xFF;
        if (bank == 0x30 || bank == 0x31) { value = Dram[(address & 0x1FFFE) >> 1]; return true; }
        if (bank == 0x39) { uint a = (address & 0xE002) | ((address & 0x7C) << 6) | ((address & 0x1F80) >> 5); value = Dram[a >> 1]; return true; }
        if (bank == 0x3A) { uint a = (address & 0xF002) | ((address & 0x3C) << 6) | ((address & 0xFC0) >> 4); value = Dram[a >> 1]; return true; }
        value = 0; return false;
    }

    /// <summary>A write at $300000-$31FFFF (the DRAM). Returns false outside it.</summary>
    public bool Write(uint address, bool upper, bool lower, ushort data)
    {
        uint bank = address >> 16 & 0xFF;
        if (bank != 0x30 && bank != 0x31) return false;
        int i = (int)((address & 0x1FFFE) >> 1);
        if (upper && lower)
        {
            Dram[i] = data;
            if (bank == 0x30 && data != 0)
            {
                uint a = address & 0xFFFFFE;
                if (a == 0x30FE06) emuStatus &= ~WAIT_30FE06;
                else if (a == 0x30FE08) emuStatus &= ~WAIT_30FE08;
            }
        }
        else if (upper) Dram[i] = (ushort)(Dram[i] & 0x00FF | data & 0xFF00);
        else if (lower) Dram[i] = (ushort)(Dram[i] & 0xFF00 | data & 0x00FF);
        return true;
    }

    /// <summary>The 68000 reads $A15000-$A150FF (word): XST at $A15000/$A15002, the status at $A15004 (bit 0 clears on read).</summary>
    public bool ReadReg(uint address, out ushort value)
    {
        if ((address & 0xFD) == 0) { value = rXST; return true; }
        if ((address & 0xFF) == 4) { value = rPM0; rPM0 &= unchecked((ushort)~1); return true; }
        value = 0; return false;
    }

    public bool WriteReg(uint address, ushort data)
    {
        if ((address & 0xFD) != 0) return false;
        rXST = data; rPM0 |= 2; emuStatus &= ~WAIT_PM0;
        return true;
    }

    // ---- register i/o

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint RegRead(int r) => r switch
    {
        0 => 0xFFFF, 1 => rX, 2 => rY, 3 => rA >> 16, 4 => rST,
        _ => RegReadSlow(r),
    };

    private uint RegReadSlow(int r)
    {
        switch (r)
        {
            case 5: // STACK
                rSTACK--;
                if ((short)rSTACK < 0) rSTACK = 5;
                return stack[rSTACK];
            case 6: return (uint)pc;
            case 7: return ReadP();
            case 8: return ReadPM0();
            case 9: { uint d = PmIo(1, 0, 0); return d != uint.MaxValue ? d : rPM1; }
            case 10: { uint d = PmIo(2, 0, 0); return d != uint.MaxValue ? d : rPM2; }
            case 11: { uint d = PmIo(3, 0, 0); return d != uint.MaxValue ? d : rXST; }
            case 12: return ReadPM4();
            case 14: return ReadPMC();
            case 15:
                if (iramRom[(pc - 1) & 0xFFFF] == 0x000F) emuStatus &= ~(PMC_SET | PMC_HAVE_ADDR);
                return rA & 0xFFFF;
            default: return 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RegWrite(int r, uint d)
    {
        if (r >= 4) RegWriteSlow(r, d);
        else if (r == 1) rX = (ushort)d;
        else if (r == 2) rY = (ushort)d;
        else if (r == 3) rA = rA & 0xFFFF | d << 16;
    }

    private void RegWriteSlow(int r, uint d)
    {
        switch (r)
        {
            case 4: rST = (ushort)d; break;
            case 5: WriteStack(d); break;
            case 6: pc = (int)(d & 0xFFFF); cycles--; break;
            case 8: if (PmIo(0, 1, d) == uint.MaxValue) rPM0 = (ushort)d; break;
            case 9: if (PmIo(1, 1, d) == uint.MaxValue) rPM1 = (ushort)d; break;
            case 10: if (PmIo(2, 1, d) == uint.MaxValue) rPM2 = (ushort)d; break;
            case 11: if (PmIo(3, 1, d) == uint.MaxValue) { rPM0 |= 1; rXST = (ushort)d; } break;
            case 12: if (PmIo(4, 1, d) == uint.MaxValue) rPM4 = (ushort)d; break;
            case 14: // PMC
                if ((emuStatus & PMC_HAVE_ADDR) != 0) { emuStatus |= PMC_SET; emuStatus &= ~PMC_HAVE_ADDR; rPMC = rPMC & 0xFFFF | (d & 0xFFFF) << 16; }
                else { emuStatus |= PMC_HAVE_ADDR; rPMC = rPMC & 0xFFFF0000 | d & 0xFFFF; }
                break;
            case 15: rA = rA & 0xFFFF0000 | d & 0xFFFF; break;
        }
    }

    private void WriteStack(uint d)
    {
        if (rSTACK >= 6) rSTACK = 0;
        stack[rSTACK++] = (ushort)d;
    }

    private uint ReadP()
    {
        int m1 = (short)rX, m2 = (short)rY;
        rP = (uint)(m1 * m2 * 2);
        return rP >> 16;
    }

    private uint ReadPM0()
    {
        uint d = PmIo(0, 0, 0);
        if (d != uint.MaxValue) return d;
        d = rPM0;
        int ppc = (pc - 1) << 1;
        if ((d & 2) == 0 && (ppc == 0x800 || ppc == 0x1851E)) emuStatus |= WAIT_PM0;
        rPM0 &= unchecked((ushort)~2);
        return d;
    }

    private uint ReadPM4()
    {
        uint d = PmIo(4, 0, 0);
        if (d == 0)
        {
            int ppc = (pc - 1) << 1;
            if (ppc == 0x0854) emuStatus |= WAIT_30FE08;
            else if (ppc == 0x4F12) emuStatus |= WAIT_30FE06;
        }
        return d != uint.MaxValue ? d : rPM4;
    }

    private uint ReadPMC()
    {
        uint l = rPMC & 0xFFFF;
        if ((emuStatus & PMC_HAVE_ADDR) != 0)
        {
            emuStatus |= PMC_SET; emuStatus &= ~PMC_HAVE_ADDR;
            return ((l << 4) & 0xFFF0) | ((l >> 4) & 0xF);
        }
        emuStatus |= PMC_HAVE_ADDR;
        return l;
    }

    private static int GetInc(int mode)
    {
        int inc = (mode >> 11) & 7;
        if (inc != 0)
        {
            if (inc != 7) inc--;
            inc = 1 << inc;
            if ((mode & 0x8000) != 0) inc = -inc;
        }
        return inc;
    }

    private static ushort Overwrite(ushort dst, uint d)
    {
        if ((d & 0xF000) != 0) dst = (ushort)(dst & ~0xF000 | d & 0xF000);
        if ((d & 0x0F00) != 0) dst = (ushort)(dst & ~0x0F00 | d & 0x0F00);
        if ((d & 0x00F0) != 0) dst = (ushort)(dst & ~0x00F0 | d & 0x00F0);
        if ((d & 0x000F) != 0) dst = (ushort)(dst & ~0x000F | d & 0x000F);
        return dst;
    }

    private uint PmIo(int reg, int write, uint d)
    {
        if ((emuStatus & PMC_SET) != 0)
        {
            int prev = iramRom[(pc - 1) & 0xFFFF];
            if ((prev & 0xFF0F) != 0 && (prev & 0xFFF0) != 0) { emuStatus &= ~PMC_SET; return 0; }
            pmac[write * 6 + reg] = rPMC;
            emuStatus &= ~PMC_SET;
            return 0;
        }
        if ((emuStatus & PMC_HAVE_ADDR) != 0) emuStatus &= ~PMC_HAVE_ADDR;

        if (reg == 4 || (rST & 0x60) != 0)
        {
            if (write != 0)
            {
                uint pm = pmac[6 + reg];
                int addr = (int)(pm & 0xFFFF), mode = (int)(pm >> 16);
                if ((mode & 0x43FF) == 0x0018)
                {
                    int inc = GetInc(mode);
                    if ((mode & 0x0400) != 0) Dram[addr] = Overwrite(Dram[addr], d); else Dram[addr] = (ushort)d;
                    pmac[6 + reg] = (uint)(pm + inc);
                }
                else if ((mode & 0xFBFF) == 0x4018)
                {
                    if ((mode & 0x0400) != 0) Dram[addr] = Overwrite(Dram[addr], d); else Dram[addr] = (ushort)d;
                    pmac[6 + reg] = pm + ((addr & 1) != 0 ? 31u : 1u);
                }
                else if ((mode & 0x47FF) == 0x001C)
                {
                    int inc = GetInc(mode);
                    iramRom[addr & 0x3FF] = (ushort)d;
                    pmac[6 + reg] = (uint)(pm + inc);
                }
            }
            else
            {
                uint pm = pmac[reg];
                int addr = (int)(pm & 0xFFFF), mode = (int)(pm >> 16);
                if ((mode & 0xFFF0) == 0x0800) { pmac[reg] = pm + 1; d = romWords[addr | ((mode & 0xF) << 16)]; }
                else if ((mode & 0x47FF) == 0x0018) { int inc = GetInc(mode); d = Dram[addr]; pmac[reg] = (uint)(pm + inc); }
                else d = 0;
            }
            rPMC = pmac[write * 6 + reg];
            return d;
        }
        return uint.MaxValue;
    }

    // ---- pointer registers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Ptr1Read(int op) => Ptr1ReadT(op & 3, (op >> 6) & 4, (op << 1) & 0x18);

    private uint Ptr1ReadT(int ri, int isj2, int modi3)
    {
        int t = ri | isj2 | modi3;
        int bank = (t & 4) != 0 ? 256 : 0;
        int r = t & 3;
        int mod = t >> 3;
        if (r == 3) return ram[bank + mod];
        int p = (t & 4) + r;
        switch (mod)
        {
            case 0: return ram[bank + rIJ[p]];
            case 1: return ram[bank + rIJ[p]++];
            default:
                {
                    uint v = ram[bank + rIJ[p]];
                    int add = mod == 2 ? -1 : 1;
                    if ((rST & 7) == 0) { rIJ[p] = (byte)(rIJ[p] + add); return v; }
                    int mask = (1 << (rST & 7)) - 1;
                    rIJ[p] = (byte)((rIJ[p] & ~mask) | ((rIJ[p] + add) & mask));
                    return v;
                }
        }
    }

    private void Ptr1Write(int op, uint d)
    {
        int t = (op & 3) | ((op >> 6) & 4) | ((op << 1) & 0x18);
        int bank = (t & 4) != 0 ? 256 : 0;
        int r = t & 3, mod = t >> 3;
        if (r == 3) { ram[bank + mod] = (ushort)d; return; }
        int p = (t & 4) + r;
        switch (mod)
        {
            case 0: ram[bank + rIJ[p]] = (ushort)d; return;
            case 1: case 3: ram[bank + rIJ[p]++] = (ushort)d; return;
            default: ram[bank + rIJ[p]--] = (ushort)d; return;
        }
    }

    private uint Ptr2Read(int op)
    {
        int t = (op & 3) | ((op >> 6) & 4) | ((op << 1) & 0x18);
        int bank = (t & 4) != 0 ? 256 : 0;
        int r = t & 3, mod = t >> 3;
        int mv;
        if (r == 3) mv = ram[bank + mod]++;
        else if (mod == 0) mv = ram[bank + rIJ[(t & 4) + r]]++;
        else return 0;
        return iramRom[mv & 0xFFFF];
    }

    // ---- flags

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdZn()
    {
        rST &= unchecked((ushort)~(FLAG_Z | FLAG_N));
        if (rA == 0) rST |= FLAG_Z; else rST |= (ushort)((rA >> 16) & FLAG_N);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdLzvn()
    {
        rST &= unchecked((ushort)~(FLAG_L | FLAG_Z | FLAG_V | FLAG_N));
        if (rA == 0) rST |= FLAG_Z; else rST |= (ushort)((rA >> 16) & FLAG_N);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Cmp(uint t)
    {
        rST &= unchecked((ushort)~(FLAG_L | FLAG_Z | FLAG_V | FLAG_N));
        if (t == 0) rST |= FLAG_Z; else rST |= (ushort)((t >> 16) & FLAG_N);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Cond(int op) => (op & 0xF0) switch
    {
        0x00 => true,
        0x50 => ((rST ^ (op << 5)) & FLAG_Z) == 0,
        0x70 => ((rST ^ (op << 7)) & FLAG_N) == 0,
        _ => false,
    };

    /// <summary>The ALU op (0 sub, 1 cmp, 2 add, 3 and, 4 or, 5 eor) on A with a 32-bit operand.</summary>
    private void Alu32(int kind, uint x)
    {
        switch (kind)
        {
            case 0: rA -= x; UpdLzvn(); break;
            case 1: Cmp(rA - x); break;
            case 2: rA += x; UpdLzvn(); break;
            case 3: rA &= x; UpdZn(); break;
            case 4: rA |= x; UpdZn(); break;
            case 5: rA ^= x; UpdZn(); break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AluKind(int hi) => hi switch { 0x10 => 0, 0x30 => 1, 0x40 => 2, 0x50 => 3, 0x60 => 4, _ => 5 };

    // ---- running

    /// <summary>Run one scanline's budget (Genplus: ssp1601_run(SVP_cycles)).</summary>
    public void Run(int budget)
    {
        pc = rPC;
        cycles = budget;
        do
        {
            int op = iramRom[pc & 0xFFFF]; pc = (pc + 1) & 0xFFFF;
            Instructions++;
            uint tmpv;
            switch (op >> 9)
            {
                case 0x00:
                    if (op == 0) break;
                    if (op == ((3 << 4) | 7)) { ReadP(); rA = rP; }
                    else { tmpv = RegRead(op & 0x0F); RegWrite((op & 0xF0) >> 4, tmpv); }
                    break;
                case 0x01: tmpv = Ptr1Read(op); RegWrite((op & 0xF0) >> 4, tmpv); break;
                case 0x02: tmpv = RegRead((op & 0xF0) >> 4); Ptr1Write(op, tmpv); break;
                case 0x04: tmpv = iramRom[pc]; pc = (pc + 1) & 0xFFFF; RegWrite((op & 0xF0) >> 4, tmpv); break;
                case 0x05: tmpv = Ptr2Read(op); RegWrite((op & 0xF0) >> 4, tmpv); break;
                case 0x06: tmpv = iramRom[pc]; pc = (pc + 1) & 0xFFFF; Ptr1Write(op, tmpv); break;
                case 0x07: ram[op & 0x1FF] = (ushort)(rA >> 16); break;
                case 0x09: tmpv = rIJ[(op & 3) | ((op >> 6) & 4)]; RegWrite((op & 0xF0) >> 4, tmpv); break;
                case 0x0A: rIJ[(op & 3) | ((op >> 6) & 4)] = (byte)RegRead((op & 0xF0) >> 4); break;
                case 0x0C: case 0x0D: case 0x0E: case 0x0F: rIJ[(op >> 8) & 7] = (byte)op; break;
                case 0x24:
                    if (Cond(op)) { int np = iramRom[pc]; pc = (pc + 1) & 0xFFFF; WriteStack((uint)pc); pc = np; cycles--; }
                    else pc = (pc + 1) & 0xFFFF;
                    break;
                case 0x25: tmpv = iramRom[rA >> 16]; RegWrite((op & 0xF0) >> 4, tmpv); break;
                case 0x26:
                    if (Cond(op)) { int np = iramRom[pc]; pc = np; cycles--; }
                    else pc = (pc + 1) & 0xFFFF;
                    break;
                case 0x48:
                    if (Cond(op))
                    {
                        switch (op & 7)
                        {
                            case 2: rA = (uint)((int)rA >> 1); break;
                            case 3: rA <<= 1; break;
                            case 6: rA = (uint)(-(int)rA); break;
                            case 7: if ((int)rA < 0) rA = (uint)(-(int)rA); break;
                        }
                        UpdZn();
                    }
                    break;
                case 0x1B:
                    ReadP(); rA -= rP; UpdZn();
                    rX = (ushort)Ptr1ReadT(op & 3, 0, (op << 1) & 0x18);
                    rY = (ushort)Ptr1ReadT((op >> 4) & 3, 4, (op >> 3) & 0x18);
                    break;
                case 0x4B:
                    ReadP(); rA += rP; UpdZn();
                    rX = (ushort)Ptr1ReadT(op & 3, 0, (op << 1) & 0x18);
                    rY = (ushort)Ptr1ReadT((op >> 4) & 3, 4, (op >> 3) & 0x18);
                    break;
                case 0x5B:
                    rA = 0; rST &= 0x0FFF;
                    rX = (ushort)Ptr1ReadT(op & 3, 0, (op << 1) & 0x18);
                    rY = (ushort)Ptr1ReadT((op >> 4) & 3, 4, (op >> 3) & 0x18);
                    break;

                // OP a, s
                case 0x10: case 0x30: case 0x40: case 0x50: case 0x60: case 0x70:
                    {
                        int k = AluKind((op >> 9) & 0x70);
                        int s = op & 0x0F;
                        if (s == 7) { ReadP(); Alu32(k, rP); break; }
                        if (s == 3) { Alu32(k, rA); break; }
                        tmpv = RegRead(s); Alu32(k, tmpv << 16);
                        break;
                    }
                // OP a, (ri)
                case 0x11: case 0x31: case 0x41: case 0x51: case 0x61: case 0x71:
                    tmpv = Ptr1Read(op); Alu32(AluKind((op >> 9) & 0x70), tmpv << 16); break;
                // OP a, adr  (0x03 = ld a, adr)
                case 0x03: rA = rA & 0xFFFF | (uint)ram[op & 0x1FF] << 16; break;
                case 0x13: case 0x33: case 0x43: case 0x53: case 0x63: case 0x73:
                    Alu32(AluKind((op >> 9) & 0x70), (uint)ram[op & 0x1FF] << 16); break;
                // OP a, imm
                case 0x14: case 0x34: case 0x44: case 0x54: case 0x64: case 0x74:
                    tmpv = iramRom[pc]; pc = (pc + 1) & 0xFFFF; Alu32(AluKind((op >> 9) & 0x70), tmpv << 16); break;
                // OP a, ((ri))
                case 0x15: case 0x35: case 0x45: case 0x55: case 0x65: case 0x75:
                    tmpv = Ptr2Read(op); Alu32(AluKind((op >> 9) & 0x70), tmpv << 16); break;
                // OP a, ri
                case 0x19: case 0x39: case 0x49: case 0x59: case 0x69: case 0x79:
                    tmpv = rIJ[((op >> 6) & 4) | (op & 3)]; Alu32(AluKind((op >> 9) & 0x70), tmpv << 16); break;
                // OP simm
                case 0x1C: case 0x3C: case 0x4C: case 0x5C: case 0x6C: case 0x7C:
                    Alu32(AluKind((op >> 9) & 0x70), (uint)(op & 0xFF) << 16); break;
            }
        }
        while (--cycles > 0 && (emuStatus & WAIT_MASK) == 0);
        ReadP();
        rPC = (ushort)pc;
    }

    // ---- state

    public void SaveState(BinaryWriter w)
    {
        for (int i = 0; i < 0x400; i++) w.Write(iramRom[i]);
        foreach (ushort v in Dram) w.Write(v);
        foreach (ushort v in ram) w.Write(v);
        w.Write(rX); w.Write(rY); w.Write(rST); w.Write(rSTACK); w.Write(rPC); w.Write(rPM0); w.Write(rPM1); w.Write(rPM2); w.Write(rXST); w.Write(rPM4);
        w.Write(rA); w.Write(rP); w.Write(rPMC);
        w.Write(rIJ);
        foreach (ushort v in stack) w.Write(v);
        foreach (uint v in pmac) w.Write(v);
        w.Write(emuStatus); w.Write(Instructions); w.Write(LineMark);
    }

    public void LoadState(BinaryReader r)
    {
        for (int i = 0; i < 0x400; i++) iramRom[i] = r.ReadUInt16();
        for (int i = 0; i < Dram.Length; i++) Dram[i] = r.ReadUInt16();
        for (int i = 0; i < ram.Length; i++) ram[i] = r.ReadUInt16();
        rX = r.ReadUInt16(); rY = r.ReadUInt16(); rST = r.ReadUInt16(); rSTACK = r.ReadUInt16(); rPC = r.ReadUInt16(); rPM0 = r.ReadUInt16(); rPM1 = r.ReadUInt16(); rPM2 = r.ReadUInt16(); rXST = r.ReadUInt16(); rPM4 = r.ReadUInt16();
        rA = r.ReadUInt32(); rP = r.ReadUInt32(); rPMC = r.ReadUInt32();
        MdVdp.ReadAll(r, rIJ);
        for (int i = 0; i < stack.Length; i++) stack[i] = r.ReadUInt16();
        for (int i = 0; i < pmac.Length; i++) pmac[i] = r.ReadUInt32();
        emuStatus = r.ReadUInt32(); Instructions = r.ReadInt64(); LineMark = r.ReadInt64();
    }
}
