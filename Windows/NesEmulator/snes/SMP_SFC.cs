using System;

namespace NesEmulator.Snes;

/// <summary>
/// Sony SPC700 (the S-SMP inside the SNES audio unit) - SFC family.
///
/// Instruction-level core: each <see cref="Step"/> runs one instruction and returns its cycle
/// count at 1.024 MHz (table below; branches give 2 back when not taken). Memory and the $F0-$FF I/O block
/// belong to <see cref="APU_SFC"/>.
///
/// Spec verifier: Windows/Resources/snes-test-roms/spctest (Workshop --snestest --apu SFC).
/// Stores perform the hardware's dummy read first (MOV d,A reads d), which matters only for the
/// read-to-clear timer counters at $FD-$FF.
/// </summary>
public sealed class SMP_SFC
{
    private readonly APU_SFC apu;

    public byte A, X, Y, SP, PSW;
    public ushort PC;
    public bool Halted { get; private set; }   // SLEEP / STOP: only reset recovers
    private int extraCycles;

    private const byte FN = 0x80, FV = 0x40, FP = 0x20, FB = 0x10, FH = 0x08, FI = 0x04, FZ = 0x02, FC = 0x01;

    // Cycle counts per opcode (branches: the taken cost; see Branch). Copied verbatim from snes9x's
    // apu/bapu/smp/core.cpp (bsnes-derived); see THIRD_PARTY_NOTICES.md. DIV, ADDW/SUBW and DAA/DAS
    // below follow bsnes's approach from memory.
    private static readonly byte[] Cycles =
    {
        2,8,4,7, 3,4,3,6, 2,6,5,4, 5,4,6,8,   // 0
        4,8,4,7, 4,5,5,6, 5,5,6,5, 2,2,4,6,   // 1
        2,8,4,7, 3,4,3,6, 2,6,5,4, 5,4,7,4,   // 2
        4,8,4,7, 4,5,5,6, 5,5,6,5, 2,2,3,8,   // 3
        2,8,4,7, 3,4,3,6, 2,6,4,4, 5,4,6,6,   // 4
        4,8,4,7, 4,5,5,6, 5,5,4,5, 2,2,4,3,   // 5
        2,8,4,7, 3,4,3,6, 2,6,4,4, 5,4,7,5,   // 6
        4,8,4,7, 4,5,5,6, 5,5,5,5, 2,2,3,6,   // 7
        2,8,4,7, 3,4,3,6, 2,6,5,4, 5,2,4,5,   // 8
        4,8,4,7, 4,5,5,6, 5,5,5,5, 2,2,12,5,  // 9
        3,8,4,7, 3,4,3,6, 2,6,4,4, 5,2,4,4,   // A
        4,8,4,7, 4,5,5,6, 5,5,5,5, 2,2,3,4,   // B
        3,8,4,7, 4,5,4,7, 2,5,6,4, 5,2,4,9,   // C
        4,8,4,7, 5,6,6,7, 4,5,5,5, 2,2,8,3,   // D
        2,8,4,7, 3,4,3,6, 2,4,5,3, 4,3,4,1,   // E
        4,8,4,7, 4,5,5,6, 3,4,5,4, 2,2,6,1,   // F
    };

    public SMP_SFC(APU_SFC apu) { this.apu = apu; }

    public void Reset()
    {
        A = X = Y = 0; SP = 0xEF; PSW = 0x02;
        Halted = false;
        PC = (ushort)(Rd(0xFFFE) | Rd(0xFFFF) << 8);
    }

    public int Step()
    {
        if (Halted) return 2;
        extraCycles = 0;
        byte op = Fetch();
        Execute(op);
        return Cycles[op] + extraCycles;
    }

    // =====================================================================================
    //  Bus + flag helpers
    // =====================================================================================

    private byte Rd(int a) => apu.SmpRead((ushort)a);
    private void Wr(int a, byte v) => apu.SmpWrite((ushort)a, v);
    private byte Fetch() => Rd(PC++);
    private ushort Fetch16() { int lo = Fetch(); return (ushort)(lo | Fetch() << 8); }

    private int Dp(int offset) => ((PSW & FP) != 0 ? 0x100 : 0) | (offset & 0xFF);
    private ushort ReadDpWord(int offset) => (ushort)(Rd(Dp(offset)) | Rd(Dp(offset + 1)) << 8);

    private void Push(byte v) { Wr(0x100 | SP, v); SP--; }
    private byte Pop() { SP++; return Rd(0x100 | SP); }

    private void SetFlag(byte f, bool on) { if (on) PSW |= f; else PSW = (byte)(PSW & ~f); }
    private bool Flag(byte f) => (PSW & f) != 0;
    private byte NZ(int v) { SetFlag(FZ, (v & 0xFF) == 0); SetFlag(FN, (v & 0x80) != 0); return (byte)v; }

    // The table holds the TAKEN cost of every branch (BPL 4, BBS 7, CBNE 7, DBNZ 6, BRA 4), so a
    // branch that falls through gives 2 back. (The first version added 2 on taken instead: every
    // SPC branch ran 2 cycles long, and sound-program uploads ran ~16% slow against Mesen 2.)
    private void Branch(bool cond)
    {
        sbyte rel = (sbyte)Fetch();
        if (!cond) { extraCycles -= 2; return; }
        PC = (ushort)(PC + rel);
    }

    // =====================================================================================
    //  ALU
    // =====================================================================================

    private byte Adc(byte a, byte b)
    {
        int r = a + b + (Flag(FC) ? 1 : 0);
        SetFlag(FV, (~(a ^ b) & (a ^ r) & 0x80) != 0);
        SetFlag(FH, ((a ^ b ^ r) & 0x10) != 0);
        SetFlag(FC, r > 0xFF);
        return NZ(r);
    }

    private byte Sbc(byte a, byte b) => Adc(a, (byte)~b);

    private void Compare(byte a, byte b)
    {
        int r = a - b;
        SetFlag(FC, r >= 0);
        NZ(r);
    }

    /// <summary>The six ALU rows: 0 OR, 1 AND, 2 EOR, 3 CMP (returns a unchanged), 4 ADC, 5 SBC.</summary>
    private byte Alu(int op, byte a, byte b)
    {
        switch (op)
        {
            case 0: return NZ(a | b);
            case 1: return NZ(a & b);
            case 2: return NZ(a ^ b);
            case 3: Compare(a, b); return a;
            case 4: return Adc(a, b);
            default: return Sbc(a, b);
        }
    }

    /// <summary>Shift/count rows: 0 ASL, 1 ROL, 2 LSR, 3 ROR, 4 DEC, 5 INC.</summary>
    private byte Modify(int kind, byte v)
    {
        switch (kind)
        {
            case 0: SetFlag(FC, (v & 0x80) != 0); return NZ(v << 1);
            case 1: { int c = Flag(FC) ? 1 : 0; SetFlag(FC, (v & 0x80) != 0); return NZ(v << 1 | c); }
            case 2: SetFlag(FC, (v & 1) != 0); return NZ(v >> 1);
            case 3: { int c = Flag(FC) ? 0x80 : 0; SetFlag(FC, (v & 1) != 0); return NZ(v >> 1 | c); }
            case 4: return NZ(v - 1);
            default: return NZ(v + 1);
        }
    }

    // =====================================================================================
    //  Decode
    // =====================================================================================

    private void Execute(byte op)
    {
        int hi = op >> 4, lo = op & 0x0F;

        // Columns 4-9, rows 0-B: the six ALU operations in their addressing modes.
        if (hi < 0xC && lo >= 4 && lo <= 9) { AluGroup(op, hi >> 1, (hi & 1) != 0, lo); return; }
        // Columns B-C, rows 0-B: ASL/ROL/LSR/ROR/DEC/INC.
        if (hi < 0xC && (lo == 0xB || lo == 0xC)) { ShiftGroup(hi >> 1, (hi & 1) != 0, lo); return; }

        switch (lo)
        {
            case 0x0 when (hi & 1) != 0:   // conditional branches
                Branch(hi switch
                {
                    0x1 => !Flag(FN), 0x3 => Flag(FN), 0x5 => !Flag(FV), 0x7 => Flag(FV),
                    0x9 => !Flag(FC), 0xB => Flag(FC), 0xD => !Flag(FZ), _ => Flag(FZ),
                });
                return;
            case 0x1:   // TCALL n
            {
                Push((byte)(PC >> 8)); Push((byte)PC);
                int vec = 0xFFDE - hi * 2;
                PC = (ushort)(Rd(vec) | Rd(vec + 1) << 8);
                return;
            }
            case 0x2:   // SET1 / CLR1 d.bit
            {
                int d = Dp(Fetch()), bit = 1 << (hi >> 1);
                byte v = Rd(d);
                Wr(d, (hi & 1) != 0 ? (byte)(v & ~bit) : (byte)(v | bit));
                return;
            }
            case 0x3:   // BBS / BBC d.bit, rel
            {
                byte v = Rd(Dp(Fetch()));
                bool set = (v & (1 << (hi >> 1))) != 0;
                Branch((hi & 1) != 0 ? !set : set);
                return;
            }
        }

        switch (op)
        {
            // ---- Flags / misc (column 0, even rows) ----
            case 0x00: break;                                               // NOP
            case 0x20: PSW = (byte)(PSW & ~FP); break;                      // CLRP
            case 0x40: PSW |= FP; break;                                    // SETP
            case 0x60: PSW = (byte)(PSW & ~FC); break;                      // CLRC
            case 0x80: PSW |= FC; break;                                    // SETC
            case 0xA0: PSW |= FI; break;                                    // EI
            case 0xC0: PSW = (byte)(PSW & ~FI); break;                      // DI
            case 0xE0: PSW = (byte)(PSW & ~(FV | FH)); break;               // CLRV (clears H too)

            // ---- Column 4-7, rows C-F: MOV A <-> memory ----
            case 0xC4: { int a = Dp(Fetch()); Rd(a); Wr(a, A); break; }
            case 0xD4: { int a = Dp(Fetch() + X); Rd(a); Wr(a, A); break; }
            case 0xC5: { int a = Fetch16(); Rd(a); Wr(a, A); break; }
            case 0xD5: { int a = (Fetch16() + X) & 0xFFFF; Rd(a); Wr(a, A); break; }
            case 0xC6: { int a = Dp(X); Rd(a); Wr(a, A); break; }
            case 0xD6: { int a = (Fetch16() + Y) & 0xFFFF; Rd(a); Wr(a, A); break; }
            case 0xC7: { int d = Fetch() + X; int a = Rd(Dp(d)) | Rd(Dp(d + 1)) << 8; Rd(a); Wr(a, A); break; }
            case 0xD7: { int a = (ReadDpWord(Fetch()) + Y) & 0xFFFF; Rd(a); Wr(a, A); break; }
            case 0xE4: A = NZ(Rd(Dp(Fetch()))); break;
            case 0xF4: A = NZ(Rd(Dp(Fetch() + X))); break;
            case 0xE5: A = NZ(Rd(Fetch16())); break;
            case 0xF5: A = NZ(Rd((Fetch16() + X) & 0xFFFF)); break;
            case 0xE6: A = NZ(Rd(Dp(X))); break;
            case 0xF6: A = NZ(Rd((Fetch16() + Y) & 0xFFFF)); break;
            case 0xE7: { int d = Fetch() + X; A = NZ(Rd(Rd(Dp(d)) | Rd(Dp(d + 1)) << 8)); break; }
            case 0xF7: A = NZ(Rd((ReadDpWord(Fetch()) + Y) & 0xFFFF)); break;

            // ---- Column 8, rows C-F ----
            case 0xC8: Compare(X, Fetch()); break;                          // CMP X,#i
            case 0xD8: { int a = Dp(Fetch()); Rd(a); Wr(a, X); break; }     // MOV d,X
            case 0xE8: A = NZ(Fetch()); break;                              // MOV A,#i
            case 0xF8: X = NZ(Rd(Dp(Fetch()))); break;                      // MOV X,d

            // ---- Column 9, rows C-F ----
            case 0xC9: { int a = Fetch16(); Rd(a); Wr(a, X); break; }       // MOV !a,X
            case 0xD9: { int a = Dp(Fetch() + Y); Rd(a); Wr(a, X); break; } // MOV d+Y,X
            case 0xE9: X = NZ(Rd(Fetch16())); break;                        // MOV X,!a
            case 0xF9: X = NZ(Rd(Dp(Fetch() + Y))); break;                  // MOV X,d+Y

            // ---- Column A: bit ops and 16-bit words ----
            case 0x0A: case 0x2A: case 0x4A: case 0x6A: case 0x8A: case 0xAA: case 0xCA: case 0xEA:
                BitOp(op);
                break;
            case 0x1A: case 0x3A:                                            // DECW / INCW d
            {
                int d = Fetch();
                int w = ReadDpWord(d) + (op == 0x3A ? 1 : -1);
                Wr(Dp(d), (byte)w); Wr(Dp(d + 1), (byte)(w >> 8));
                SetFlag(FZ, (w & 0xFFFF) == 0); SetFlag(FN, (w & 0x8000) != 0);
                break;
            }
            case 0x5A:                                                       // CMPW YA,d
            {
                int w = ReadDpWord(Fetch()), ya = Y << 8 | A;
                int r = ya - w;
                SetFlag(FC, r >= 0); SetFlag(FZ, (r & 0xFFFF) == 0); SetFlag(FN, (r & 0x8000) != 0);
                break;
            }
            case 0x7A: case 0x9A:                                            // ADDW / SUBW YA,d
            {
                // Two chained 8-bit ADC/SBCs, exactly like the chip: V/H/C/N come from the high half.
                int w = ReadDpWord(Fetch());
                SetFlag(FC, op == 0x9A);
                byte l = op == 0x7A ? Adc(A, (byte)w) : Sbc(A, (byte)w);
                byte h = op == 0x7A ? Adc(Y, (byte)(w >> 8)) : Sbc(Y, (byte)(w >> 8));
                A = l; Y = h;
                SetFlag(FZ, (l | h) == 0);
                break;
            }
            case 0xBA:                                                       // MOVW YA,d
            {
                int w = ReadDpWord(Fetch());
                A = (byte)w; Y = (byte)(w >> 8);
                SetFlag(FZ, w == 0); SetFlag(FN, (w & 0x8000) != 0);
                break;
            }
            case 0xDA:                                                       // MOVW d,YA
            {
                int d = Fetch();
                Rd(Dp(d));
                Wr(Dp(d), A); Wr(Dp(d + 1), Y);
                break;
            }
            case 0xFA:                                                       // MOV dd,ds (source byte comes first)
            {
                byte v = Rd(Dp(Fetch()));
                Wr(Dp(Fetch()), v);
                break;
            }

            // ---- Column B/C, rows C-F: Y moves, X/Y counts ----
            case 0xCB: { int a = Dp(Fetch()); Rd(a); Wr(a, Y); break; }
            case 0xDB: { int a = Dp(Fetch() + X); Rd(a); Wr(a, Y); break; }
            case 0xEB: Y = NZ(Rd(Dp(Fetch()))); break;
            case 0xFB: Y = NZ(Rd(Dp(Fetch() + X))); break;
            case 0xCC: { int a = Fetch16(); Rd(a); Wr(a, Y); break; }
            case 0xDC: Y = NZ(Y - 1); break;
            case 0xEC: Y = NZ(Rd(Fetch16())); break;
            case 0xFC: Y = NZ(Y + 1); break;

            // ---- Column D ----
            case 0x0D: Push(PSW); break;
            case 0x1D: X = NZ(X - 1); break;
            case 0x2D: Push(A); break;
            case 0x3D: X = NZ(X + 1); break;
            case 0x4D: Push(X); break;
            case 0x5D: X = NZ(A); break;
            case 0x6D: Push(Y); break;
            case 0x7D: A = NZ(X); break;
            case 0x8D: Y = NZ(Fetch()); break;
            case 0x9D: X = NZ(SP); break;
            case 0xAD: Compare(Y, Fetch()); break;
            case 0xBD: SP = X; break;
            case 0xCD: X = NZ(Fetch()); break;
            case 0xDD: A = NZ(Y); break;
            case 0xED: PSW ^= FC; break;                                     // NOTC
            case 0xFD: Y = NZ(A); break;

            // ---- Column E ----
            case 0x0E: case 0x4E:                                            // TSET1 / TCLR1 !a
            {
                int a = Fetch16();
                byte v = Rd(a);
                NZ(A - v);
                Wr(a, op == 0x0E ? (byte)(v | A) : (byte)(v & ~A));
                break;
            }
            case 0x1E: Compare(X, Rd(Fetch16())); break;
            case 0x3E: Compare(X, Rd(Dp(Fetch()))); break;
            case 0x5E: Compare(Y, Rd(Fetch16())); break;
            case 0x7E: Compare(Y, Rd(Dp(Fetch()))); break;
            case 0x2E: { byte v = Rd(Dp(Fetch())); Branch(A != v); break; }        // CBNE d,rel
            case 0xDE: { byte v = Rd(Dp(Fetch() + X)); Branch(A != v); break; }    // CBNE d+X,rel
            case 0x6E:                                                              // DBNZ d,rel
            {
                int a = Dp(Fetch());
                byte v = (byte)(Rd(a) - 1);
                Wr(a, v);
                Branch(v != 0);
                break;
            }
            case 0xFE: Y--; Branch(Y != 0); break;                                   // DBNZ Y,rel
            case 0x8E: PSW = Pop(); break;
            case 0xAE: A = Pop(); break;
            case 0xCE: X = Pop(); break;
            case 0xEE: Y = Pop(); break;
            case 0x9E: Div(); break;
            case 0xBE:                                                               // DAS
                if (!Flag(FC) || A > 0x99) { A -= 0x60; PSW = (byte)(PSW & ~FC); }
                if (!Flag(FH) || (A & 0x0F) > 0x09) A -= 0x06;
                NZ(A);
                break;

            // ---- Column F ----
            case 0x0F:                                                               // BRK
                Push((byte)(PC >> 8)); Push((byte)PC); Push(PSW);
                PSW = (byte)((PSW | FB) & ~FI);
                PC = (ushort)(Rd(0xFFDE) | Rd(0xFFDF) << 8);
                break;
            case 0x1F: { int a = (Fetch16() + X) & 0xFFFF; PC = (ushort)(Rd(a) | Rd((a + 1) & 0xFFFF) << 8); break; }
            case 0x2F: Branch(true); break;                                          // BRA
            case 0x3F: { ushort a = Fetch16(); Push((byte)(PC >> 8)); Push((byte)PC); PC = a; break; }
            case 0x4F: { byte u = Fetch(); Push((byte)(PC >> 8)); Push((byte)PC); PC = (ushort)(0xFF00 | u); break; }
            case 0x5F: PC = Fetch16(); break;
            case 0x6F: { int l = Pop(); PC = (ushort)(l | Pop() << 8); break; }       // RET
            case 0x7F: { PSW = Pop(); int l = Pop(); PC = (ushort)(l | Pop() << 8); break; }   // RETI
            case 0x8F: { byte imm = Fetch(); int a = Dp(Fetch()); Rd(a); Wr(a, imm); break; }  // MOV d,#i
            case 0x9F: A = NZ(A >> 4 | A << 4); break;                               // XCN
            case 0xAF: Wr(Dp(X), A); X++; break;                                     // MOV (X)+,A
            case 0xBF: A = NZ(Rd(Dp(X))); X++; break;                                // MOV A,(X)+
            case 0xCF:                                                               // MUL YA
            {
                int r = Y * A;
                A = (byte)r; Y = (byte)(r >> 8);
                NZ(Y);
                break;
            }
            case 0xDF:                                                               // DAA
                if (Flag(FC) || A > 0x99) { A += 0x60; PSW |= FC; }
                if (Flag(FH) || (A & 0x0F) > 0x09) A += 0x06;
                NZ(A);
                break;
            case 0xEF: case 0xFF: Halted = true; break;                              // SLEEP / STOP

            default: throw new InvalidOperationException($"SMP_SFC: unhandled opcode ${op:X2}");
        }
    }

    private void AluGroup(byte op, int aluOp, bool odd, int col)
    {
        switch (col)
        {
            case 4: AluA(aluOp, Rd(Dp(Fetch() + (odd ? X : 0)))); return;
            case 5: AluA(aluOp, Rd((Fetch16() + (odd ? X : 0)) & 0xFFFF)); return;
            case 6: AluA(aluOp, odd ? Rd((Fetch16() + Y) & 0xFFFF) : Rd(Dp(X))); return;
            case 7:
                if (odd) AluA(aluOp, Rd((ReadDpWord(Fetch()) + Y) & 0xFFFF));
                else { int d = Fetch() + X; AluA(aluOp, Rd(Rd(Dp(d)) | Rd(Dp(d + 1)) << 8)); }
                return;
            case 8:
                if (!odd) { AluA(aluOp, Fetch()); return; }
                {
                    byte imm = Fetch();                     // d,#i: immediate byte comes first
                    int a = Dp(Fetch());
                    byte r = Alu(aluOp, Rd(a), imm);
                    if (aluOp != 3) Wr(a, r);
                }
                return;
            default:   // 9
            {
                int dst; byte src;
                if (odd) { src = Rd(Dp(Y)); dst = Dp(X); }           // (X),(Y)
                else { src = Rd(Dp(Fetch())); dst = Dp(Fetch()); }   // dd,ds: source byte first
                byte r = Alu(aluOp, Rd(dst), src);
                if (aluOp != 3) Wr(dst, r);
                return;
            }
        }
    }

    private void AluA(int aluOp, byte b)
    {
        byte r = Alu(aluOp, A, b);
        if (aluOp != 3) A = r;
    }

    private void ShiftGroup(int kind, bool odd, int col)
    {
        if (col == 0xC && odd) { A = Modify(kind, A); return; }                  // ASL A .. INC A
        int a = col == 0xB ? Dp(Fetch() + (odd ? X : 0)) : Fetch16();
        Wr(a, Modify(kind, Rd(a)));
    }

    private void BitOp(byte op)
    {
        ushort w = Fetch16();
        int addr = w & 0x1FFF, bit = w >> 13;
        byte v = Rd(addr);
        bool mb = (v >> bit & 1) != 0, c = Flag(FC);
        switch (op)
        {
            case 0x0A: SetFlag(FC, c | mb); break;       // OR1 C,m.b
            case 0x2A: SetFlag(FC, c | !mb); break;      // OR1 C,/m.b
            case 0x4A: SetFlag(FC, c & mb); break;       // AND1 C,m.b
            case 0x6A: SetFlag(FC, c & !mb); break;      // AND1 C,/m.b
            case 0x8A: SetFlag(FC, c ^ mb); break;       // EOR1 C,m.b
            case 0xAA: SetFlag(FC, mb); break;           // MOV1 C,m.b
            case 0xCA: Wr(addr, c ? (byte)(v | 1 << bit) : (byte)(v & ~(1 << bit))); break;   // MOV1 m.b,C
            default: Wr(addr, (byte)(v ^ 1 << bit)); break;                                   // NOT1 m.b
        }
    }

    /// <summary>DIV YA,X including the chip's odd results when the quotient overflows 9 bits.</summary>
    private void Div()
    {
        int ya = Y << 8 | A;
        SetFlag(FH, (Y & 0x0F) >= (X & 0x0F));
        SetFlag(FV, Y >= X);
        if (Y < X << 1)
        {
            A = (byte)(ya / X);
            Y = (byte)(ya % X);
        }
        else
        {
            A = (byte)(255 - (ya - (X << 9)) / (256 - X));
            Y = (byte)(X + (ya - (X << 9)) % (256 - X));
        }
        NZ(A);
    }
}
