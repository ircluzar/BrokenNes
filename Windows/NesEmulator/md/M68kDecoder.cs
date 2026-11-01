// Opcode decoding. The set of legal opcodes and their addressing-mode restrictions are the ones ares' m68000 instruction.cpp binds (ISC; see M68k.cs); the table form is new:
// one byte of handler number per 16-bit opcode, with the operand fields decoded from the opcode when the instruction runs.

namespace NesEmulator.Sega;

/// <summary>Handler numbers of the 68000 instruction groups. Zero (<see cref="Illegal"/>) is what every unbound opcode decodes to.</summary>
internal static class M68kH
{
    public const byte Illegal = 0, Abcd = 1, AddEaDn = 2, AddDnEa = 3, Adda = 4, Addi = 5, AddqEa = 6, AddqAn = 7, Addx = 8, AndEaDn = 9, AndDnEa = 10, Andi = 11, AndiCcr = 12, AndiSr = 13,
        ShiftReg = 14, ShiftEa = 15, Bcc = 16, Bra = 17, Bsr = 18, BitReg = 19, BitImm = 20, Chk = 21, Clr = 22, Cmp = 23, Cmpa = 24, Cmpi = 25, Cmpm = 26, Dbcc = 27, Divs = 28, Divu = 29,
        Eor = 30, Eori = 31, EoriCcr = 32, EoriSr = 33, ExgDd = 34, ExgAa = 35, ExgDa = 36, Ext = 37, Jmp = 38, Jsr = 39, Lea = 40, Link = 41, Move = 42, Movea = 43, MovemToMem = 44,
        MovemToReg = 45, MovepToMem = 46, MovepToReg = 47, Moveq = 48, MoveFromSr = 49, MoveToCcr = 50, MoveToSr = 51, MoveFromUsp = 52, MoveToUsp = 53, Muls = 54, Mulu = 55, Nbcd = 56,
        Neg = 57, Negx = 58, Nop = 59, Not = 60, OrEaDn = 61, OrDnEa = 62, Ori = 63, OriCcr = 64, OriSr = 65, Pea = 66, Reset = 67, Rte = 68, Rtr = 69, Rts = 70, Sbcd = 71, Scc = 72,
        Stop = 73, SubEaDn = 74, SubDnEa = 75, Suba = 76, Subi = 77, SubqEa = 78, SubqAn = 79, Subx = 80, Swap = 81, Tas = 82, Trap = 83, Trapv = 84, Tst = 85, Unlk = 86;
}

public sealed partial class M68k<TBus> where TBus : struct, IM68kBus
{
    private static readonly byte[] Table = BuildDecodeTable();

    private void BuildTable() { }   // the table is static; kept as a hook for per-instance variants

    private static byte[] BuildDecodeTable()
    {
        var t = new byte[65536];

        void Bind(int op, byte h) { t[op] = h; }
        // the addressing-mode legality filters, as ares writes them
        static bool NotMemAlt(int mode, int reg) => mode <= 1 || (mode == 7 && reg >= 2);      // data alterable without Dn... (mode<=1 excluded)
        static bool NotDataAlt(int mode, int reg) => mode == 1 || (mode == 7 && reg >= 2);     // data alterable: Dn allowed, An not
        static bool NotDataSrc(int mode, int reg) => mode == 1 || (mode == 7 && reg >= 5);     // data readable: An not allowed, PC-relative and immediate allowed
        static bool NotAnySrc(int mode, int reg) => mode == 7 && reg >= 5;                     // anything readable, An allowed
        static bool NotControl(int mode, int reg) => mode <= 1 || mode == 3 || mode == 4 || (mode == 7 && reg >= 4);   // control addressing

        // ABCD, SBCD
        for (int tr = 0; tr < 8; tr++)
            for (int sr = 0; sr < 8; sr++)
                for (int rm = 0; rm < 2; rm++)
                {
                    Bind(0xC100 | tr << 9 | rm << 3 | sr, M68kH.Abcd);
                    Bind(0x8100 | tr << 9 | rm << 3 | sr, M68kH.Sbcd);
                }

        for (int dreg = 0; dreg < 8; dreg++)
            for (int mode = 0; mode < 8; mode++)
                for (int reg = 0; reg < 8; reg++)
                {
                    int ea = mode << 3 | reg;
                    // ADD / SUB / CMP / AND / OR: EA to Dn
                    for (int size = 0; size < 3; size++)
                    {
                        if (!NotAnySrc(mode, reg) && !(size == 0 && mode == 1)) { Bind(0xD000 | dreg << 9 | size << 6 | ea, M68kH.AddEaDn); Bind(0x9000 | dreg << 9 | size << 6 | ea, M68kH.SubEaDn); Bind(0xB000 | dreg << 9 | size << 6 | ea, M68kH.Cmp); }
                        if (!NotDataSrc(mode, reg)) { Bind(0xC000 | dreg << 9 | size << 6 | ea, M68kH.AndEaDn); Bind(0x8000 | dreg << 9 | size << 6 | ea, M68kH.OrEaDn); }
                        // Dn to EA
                        if (!NotMemAlt(mode, reg))
                        {
                            Bind(0xD100 | dreg << 9 | size << 6 | ea, M68kH.AddDnEa); Bind(0x9100 | dreg << 9 | size << 6 | ea, M68kH.SubDnEa);
                            Bind(0xC100 | dreg << 9 | size << 6 | ea, M68kH.AndDnEa); Bind(0x8100 | dreg << 9 | size << 6 | ea, M68kH.OrDnEa);
                        }
                        // EOR Dn,EA (mode 0 allowed)
                        if (!NotDataAlt(mode, reg)) Bind(0xB100 | dreg << 9 | size << 6 | ea, M68kH.Eor);
                    }
                    // ADDA, SUBA, CMPA
                    for (int l = 0; l < 2; l++)
                        if (!NotAnySrc(mode, reg)) { Bind(0xD0C0 | l << 8 | dreg << 9 | ea, M68kH.Adda); Bind(0x90C0 | l << 8 | dreg << 9 | ea, M68kH.Suba); Bind(0xB0C0 | l << 8 | dreg << 9 | ea, M68kH.Cmpa); }
                    // CHK, DIVS, DIVU, MULS, MULU
                    if (!NotDataSrc(mode, reg))
                    {
                        Bind(0x4180 | dreg << 9 | ea, M68kH.Chk);
                        Bind(0x81C0 | dreg << 9 | ea, M68kH.Divs); Bind(0x80C0 | dreg << 9 | ea, M68kH.Divu);
                        Bind(0xC1C0 | dreg << 9 | ea, M68kH.Muls); Bind(0xC0C0 | dreg << 9 | ea, M68kH.Mulu);
                    }
                    // LEA
                    if (!NotControl(mode, reg)) Bind(0x41C0 | dreg << 9 | ea, M68kH.Lea);
                    // dynamic bit operations
                    if (!NotDataSrc(mode, reg)) Bind(0x0100 | dreg << 9 | ea, M68kH.BitReg);      // BTST
                    if (!NotDataAlt(mode, reg)) { Bind(0x0140 | dreg << 9 | ea, M68kH.BitReg); Bind(0x0180 | dreg << 9 | ea, M68kH.BitReg); Bind(0x01C0 | dreg << 9 | ea, M68kH.BitReg); }
                }

        // ADDX, SUBX
        for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
                for (int size = 0; size < 3; size++)
                    for (int rm = 0; rm < 2; rm++)
                    {
                        Bind(0xD100 | x << 9 | size << 6 | rm << 3 | y, M68kH.Addx);
                        Bind(0x9100 | x << 9 | size << 6 | rm << 3 | y, M68kH.Subx);
                    }

        // immediate arithmetic: ORI ANDI SUBI ADDI EORI CMPI
        for (int mode = 0; mode < 8; mode++)
            for (int reg = 0; reg < 8; reg++)
            {
                if (NotDataAlt(mode, reg)) continue;
                int ea = mode << 3 | reg;
                for (int size = 0; size < 3; size++)
                {
                    Bind(0x0000 | size << 6 | ea, M68kH.Ori); Bind(0x0200 | size << 6 | ea, M68kH.Andi); Bind(0x0400 | size << 6 | ea, M68kH.Subi);
                    Bind(0x0600 | size << 6 | ea, M68kH.Addi); Bind(0x0A00 | size << 6 | ea, M68kH.Eori); Bind(0x0C00 | size << 6 | ea, M68kH.Cmpi);
                    // single-operand: NEGX CLR NEG NOT TST
                    Bind(0x4000 | size << 6 | ea, M68kH.Negx); Bind(0x4200 | size << 6 | ea, M68kH.Clr); Bind(0x4400 | size << 6 | ea, M68kH.Neg);
                    Bind(0x4600 | size << 6 | ea, M68kH.Not); Bind(0x4A00 | size << 6 | ea, M68kH.Tst);
                }
                // static bit operations (BTST #n allows PC-relative, not immediate beyond reg 3)
                Bind(0x0840 | ea, M68kH.BitImm); Bind(0x0880 | ea, M68kH.BitImm); Bind(0x08C0 | ea, M68kH.BitImm);
                // MOVE from SR, NBCD, TAS, Scc
                Bind(0x40C0 | ea, M68kH.MoveFromSr); Bind(0x4800 | ea, M68kH.Nbcd); Bind(0x4AC0 | ea, M68kH.Tas);
                // memory shifts and rotates
                if (mode > 1)
                    for (int tt = 0; tt < 4; tt++) for (int d = 0; d < 2; d++) Bind(0xE0C0 | tt << 9 | d << 8 | ea, M68kH.ShiftEa);
            }
        for (int mode = 0; mode < 8; mode++)
            for (int reg = 0; reg < 8; reg++)
            {
                int ea = mode << 3 | reg;
                if (!(mode == 1 || (mode == 7 && reg >= 4))) Bind(0x0800 | ea, M68kH.BitImm);   // BTST #n,EA
                if (!NotDataAlt(mode, reg)) for (int cc = 0; cc < 16; cc++) Bind(0x50C0 | cc << 8 | ea, M68kH.Scc);
                if (!NotDataSrc(mode, reg)) { Bind(0x44C0 | ea, M68kH.MoveToCcr); Bind(0x46C0 | ea, M68kH.MoveToSr); }
                if (!NotControl(mode, reg)) { Bind(0x4EC0 | ea, M68kH.Jmp); Bind(0x4E80 | ea, M68kH.Jsr); }
                if (!(mode <= 1 || mode == 3 || mode == 4 || (mode == 7 && reg >= 4))) Bind(0x4840 | ea, M68kH.Pea);
                // MOVEM
                for (int l = 0; l < 2; l++)
                {
                    if (!(mode <= 1 || mode == 3 || (mode == 7 && reg >= 2))) Bind(0x4880 | l << 6 | ea, M68kH.MovemToMem);
                    if (!(mode <= 1 || mode == 4 || (mode == 7 && reg >= 4))) Bind(0x4C80 | l << 6 | ea, M68kH.MovemToReg);
                }
                // ADDQ / SUBQ on EA
                if (!(mode == 7 && reg >= 2))
                    for (int data = 0; data < 8; data++)
                        for (int size = 0; size < 3; size++)
                        {
                            if (mode == 1 && size == 0) continue;
                            Bind(0x5000 | data << 9 | size << 6 | ea, mode == 1 ? M68kH.AddqAn : M68kH.AddqEa);
                            Bind(0x5100 | data << 9 | size << 6 | ea, mode == 1 ? M68kH.SubqAn : M68kH.SubqEa);
                        }
            }
        // MOVE and MOVEA (size in bits 13-12: 1 byte, 3 word, 2 long)
        for (int toReg = 0; toReg < 8; toReg++)
            for (int toMode = 0; toMode < 8; toMode++)
                for (int fromMode = 0; fromMode < 8; fromMode++)
                    for (int fromReg = 0; fromReg < 8; fromReg++)
                    {
                        if (fromMode == 7 && fromReg >= 5) continue;
                        int low = toReg << 9 | toMode << 6 | fromMode << 3 | fromReg;
                        if (toMode == 1)
                        {
                            Bind(0x3000 | low, M68kH.Movea); Bind(0x2000 | low, M68kH.Movea);
                            continue;
                        }
                        if (toMode == 7 && toReg >= 2) continue;
                        if (fromMode != 1) Bind(0x1000 | low, M68kH.Move);
                        Bind(0x3000 | low, M68kH.Move); Bind(0x2000 | low, M68kH.Move);
                    }

        // register shifts and rotates: 1110 ccc d ss i tt rrr
        for (int cnt = 0; cnt < 8; cnt++)
            for (int d = 0; d < 2; d++)
                for (int size = 0; size < 3; size++)
                    for (int i = 0; i < 2; i++)
                        for (int tt = 0; tt < 4; tt++)
                            for (int reg = 0; reg < 8; reg++)
                                Bind(0xE000 | cnt << 9 | d << 8 | size << 6 | i << 5 | tt << 3 | reg, M68kH.ShiftReg);

        // branches
        for (int disp = 0; disp < 256; disp++)
            for (int cc = 0; cc < 16; cc++)
                Bind(0x6000 | cc << 8 | disp, cc == 0 ? M68kH.Bra : cc == 1 ? M68kH.Bsr : M68kH.Bcc);

        // DBcc
        for (int cc = 0; cc < 16; cc++) for (int d = 0; d < 8; d++) Bind(0x50C8 | cc << 8 | d, M68kH.Dbcc);

        // CMPM
        for (int x = 0; x < 8; x++) for (int y = 0; y < 8; y++) for (int size = 0; size < 3; size++) Bind(0xB108 | x << 9 | size << 6 | y, M68kH.Cmpm);

        // EXG
        for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
            {
                Bind(0xC140 | x << 9 | y, M68kH.ExgDd);
                Bind(0xC148 | x << 9 | y, M68kH.ExgAa);
                Bind(0xC188 | x << 9 | y, M68kH.ExgDa);
            }

        for (int d = 0; d < 8; d++)
        {
            for (int l = 0; l < 2; l++)
            {
                Bind(0x4880 | l << 6 | d, M68kH.Ext);
                for (int dd = 0; dd < 8; dd++)
                {
                    Bind(0x0188 | dd << 9 | l << 6 | d, M68kH.MovepToMem);
                    Bind(0x0108 | dd << 9 | l << 6 | d, M68kH.MovepToReg);
                }
            }
            Bind(0x4E50 | d, M68kH.Link);
            Bind(0x4E58 | d, M68kH.Unlk);
            Bind(0x4E60 | d, M68kH.MoveToUsp);
            Bind(0x4E68 | d, M68kH.MoveFromUsp);
            Bind(0x4840 | d, M68kH.Swap);
            for (int imm = 0; imm < 256; imm++) Bind(0x7000 | d << 9 | imm, M68kH.Moveq);
        }
        for (int v = 0; v < 16; v++) Bind(0x4E40 | v, M68kH.Trap);

        Bind(0x003C, M68kH.OriCcr); Bind(0x007C, M68kH.OriSr);
        Bind(0x023C, M68kH.AndiCcr); Bind(0x027C, M68kH.AndiSr);
        Bind(0x0A3C, M68kH.EoriCcr); Bind(0x0A7C, M68kH.EoriSr);
        Bind(0x4AFC, M68kH.Illegal);
        Bind(0x4E70, M68kH.Reset); Bind(0x4E71, M68kH.Nop); Bind(0x4E72, M68kH.Stop); Bind(0x4E73, M68kH.Rte);
        Bind(0x4E75, M68kH.Rts); Bind(0x4E76, M68kH.Trapv); Bind(0x4E77, M68kH.Rtr);
        return t;
    }
}
