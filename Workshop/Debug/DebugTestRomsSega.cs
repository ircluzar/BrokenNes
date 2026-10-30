using System;
using System.Collections.Generic;
using System.Text;

namespace BrokenNes.Workshop.Debug;

/// <summary>A tiny 68000 assembler for the Genesis test ROM: big-endian words, labels, and the fixups the program needs (an absolute long, an 8-bit branch
/// displacement in the opcode word, a 16-bit displacement in an extension word).</summary>
internal sealed class M68kAsm
{
    private readonly List<byte> bytes = new();
    private readonly Dictionary<string, uint> labels = new();
    private readonly List<(int at, string label, char kind)> fixups = new();
    public uint Pc => (uint)bytes.Count;
    public M68kAsm L(string name) { labels[name] = Pc; return this; }
    public M68kAsm W(params int[] words) { foreach (int w in words) { bytes.Add((byte)(w >> 8)); bytes.Add((byte)w); } return this; }
    public M68kAsm Long(uint v) => W((int)(v >> 16), (int)(v & 0xFFFF));
    /// <summary>A 32-bit absolute address of a label.</summary>
    public M68kAsm AbsL(string label) { fixups.Add((bytes.Count, label, 'L')); return W(0, 0); }
    /// <summary>An opcode word with an 8-bit displacement to a label (BRA.S, BSR.S, Bcc.S): the displacement is from the opcode + 2.</summary>
    public M68kAsm Short(int opcode, string label) { fixups.Add((bytes.Count, label, 'b')); return W(opcode); }
    /// <summary>A 16-bit displacement extension word (DBcc, Bcc.W): from the extension word's own address.</summary>
    public M68kAsm Disp16(string label) { fixups.Add((bytes.Count, label, 'w')); return W(0); }
    public M68kAsm Org(uint at) { while (Pc < at) bytes.Add(0); return this; }
    public M68kAsm Text(string s) { bytes.AddRange(Encoding.ASCII.GetBytes(s)); return this; }
    public M68kAsm Bytes(byte[] b) { bytes.AddRange(b); return this; }
    public uint this[string label] => labels[label];
    public IReadOnlyDictionary<string, uint> Labels => labels;

    public byte[] Build(int size)
    {
        foreach (var (at, label, kind) in fixups)
        {
            uint v = labels.TryGetValue(label, out var x) ? x : throw new InvalidOperationException($"undefined label {label}");
            switch (kind)
            {
                case 'L': bytes[at] = (byte)(v >> 24); bytes[at + 1] = (byte)(v >> 16); bytes[at + 2] = (byte)(v >> 8); bytes[at + 3] = (byte)v; break;
                case 'b': { int d = (int)v - (at + 2); if (d < -128 || d > 127 || d == 0) throw new InvalidOperationException($"short branch to {label}: {d}"); bytes[at + 1] = (byte)d; break; }
                case 'w': { int d = (int)v - at; bytes[at] = (byte)(d >> 8); bytes[at + 1] = (byte)d; break; }
            }
        }
        var rom = new byte[size];
        bytes.CopyTo(rom);
        return rom;
    }
}

/// <summary>
/// The debugger's Sega test ROMs (BrokenDev Timeflow T0), with known behaviour. The Master System / Game Gear program: an IM 1 frame interrupt handler that counts, a main loop
/// that counts its turns in HL (stored at $C000), calls sub1 -> sub2 (a busy DJNZ, where the frame interrupt usually lands), writes a counter through the work RAM mirror
/// ($E011 is $C011) and the VDP data port through a mirror ($80 is $BE), and calls a function in a ROM bank it maps into slot 1 (bank 2 at $4000). The Genesis program: the 68000
/// loads a Z80 program into the Z80's RAM, starts it, enables the vertical interrupt (its handler counts at $FF0004) and loops: JSR sub1 -> BSR sub2 (a busy DBRA), a counter
/// at $FF0000 written to $E00010 (a mirror of $FF0010). The Z80: IM 1 with a counting handler, a loop that counts its turns in HL (stored at $1000), calls zsub, which reads
/// the 68000's ROM through the bank window ($8100 = ROM $000100) and stores it at $1100.
/// </summary>
internal static class SegaDebugTestRoms
{
    private static void Org(Asm a, uint at) { while (a.Pc < at) a.B(0x00); }

    public static (byte[] rom, Asm asm, string sym) MasterSystem(bool gameGear)
    {
        var a = new Asm(0);
        a.L("reset").B(0xF3).B(0xED, 0x56).B(0x31, 0xF0, 0xDF).B(0xC3).A("main");     // DI; IM 1; LD SP,$DFF0; JP main
        Org(a, 0x38);
        a.L("irq").B(0xF5).B(0xDB, 0xBF).B(0x3A, 0x00, 0xC1).B(0x3C).B(0x32, 0x00, 0xC1).B(0xF1).B(0xFB).B(0xED, 0x4D);   // PUSH AF; IN A,($BF); LD A,($C100); INC A; LD ($C100),A; POP AF; EI; RETI
        Org(a, 0x66);
        a.L("nmi").B(0xED, 0x45);                                                        // RETN
        Org(a, 0x80);
        a.L("main");
        a.B(0x3E, 0x20, 0xD3, 0xBF, 0x3E, 0x81, 0xD3, 0xBF);                             // VDP register 1 = $20: the frame interrupt on, the display off
        a.B(0x3E, 0x00, 0xD3, 0xBF, 0x3E, 0x78, 0xD3, 0xBF);                             // the VDP address $3800, VRAM write mode
        a.B(0x21, 0x00, 0x00);                                                           // LD HL,0
        a.B(0xFB);                                                                       // EI
        a.L("loop").B(0x23);                                                             // INC HL
        a.L("store_turns").B(0x22, 0x00, 0xC0);                                         // LD ($C000),HL
        a.L("call_sub1").B(0xCD).A("sub1");                                             // CALL sub1
        a.L("after_call").B(0x3A, 0x10, 0xC0).B(0x3C).B(0x32, 0x10, 0xC0);             // LD A,($C010); INC A; LD ($C010),A
        a.L("mirror_write").B(0x32, 0x11, 0xE0);                                        // LD ($E011),A   (the mirror of $C011)
        a.L("port_write").B(0xD3, 0x80);                                                // OUT ($80),A    (a mirror of the data port $BE)
        a.B(0x3E, 0x02, 0x32, 0xFE, 0xFF);                                              // LD A,2; LD ($FFFE),A: bank 2 into slot 1
        a.L("call_banked").B(0xCD, 0x00, 0x40);                                         // CALL $4000
        a.B(0x18).R("loop");                                                            // JR loop
        a.L("sub1").B(0xCD).A("sub2");                                                  // CALL sub2
        a.L("sub1_ret").B(0xC9);                                                        // RET
        a.L("sub2").B(0x06, 0x40);                                                      // LD B,$40
        a.L("sub2_loop").B(0x10).R("sub2_loop");                                        // DJNZ sub2_loop
        a.L("sub2_ret").B(0xC9);                                                        // RET
        // a known stretch for the disassembler (never run): one of each operand shape
        a.L("known");
        a.B(0xDD, 0x36, 0x05, 0x7F);        // LD (IX+$05),$7F
        a.B(0xFD, 0xCB, 0xFE, 0x46);        // BIT 0,(IY-$02)
        a.B(0xED, 0xB0);                    // LDIR
        a.B(0xED, 0x43, 0x34, 0x12);        // LD ($1234),BC
        a.B(0xCB, 0x3F);                    // SRL A
        a.B(0xDD, 0x7C);                    // LD A,IXH
        a.B(0xFD, 0xE9);                    // JP (IY)
        a.B(0xC3, 0x80, 0x00);              // JP main
        a.L("known_end");
        Org(a, 0x4000);
        a.L("bank1_func").B(0x3E, 0x11, 0x32, 0x20, 0xC0, 0xC9);                         // bank 1: LD A,$11; LD ($C020),A; RET  (never mapped while it runs)
        Org(a, 0x8000);
        a.L("bank2_func").B(0x3E, 0x22, 0x32, 0x20, 0xC0, 0xC9);                         // bank 2 (mapped at $4000): LD A,$22; LD ($C020),A; RET
        var code = a.Build();
        var rom = new byte[0x10000];
        Array.Copy(code, rom, code.Length);
        // the header the BIOS would check (not needed with no BIOS; it makes the ROM a well-formed cartridge)
        var tmr = Encoding.ASCII.GetBytes("TMR SEGA");
        Array.Copy(tmr, 0, rom, 0x7FF0, tmr.Length);
        rom[0x7FFF] = (byte)(gameGear ? 0x6C : 0x4C);
        // WLA-DX style symbols: bank:address
        var sym = new StringBuilder("; WLA-DX symbolic information for the debugger's Master System test ROM\n[labels]\n");
        foreach (var name in new[] { "reset", "irq", "nmi", "main", "loop", "store_turns", "call_sub1", "after_call", "mirror_write", "port_write", "call_banked", "sub1", "sub1_ret", "sub2", "sub2_loop", "sub2_ret", "known", "known_end" })
            sym.Append($"00:{a[name]:X4} {name}\n");
        sym.Append("01:4000 bank1_func\n02:4000 bank2_func\n");
        sym.Append("00:C000 turns\n00:C010 counter\n00:C011 mirror_target\n00:C100 irq_count\n");
        return (rom, a, sym.ToString());
    }

    public static (byte[] rom, M68kAsm m, Asm z, string nm) Genesis()
    {
        // ---- the Z80 program (copied by the 68000 into the Z80's RAM at $0000)
        var z = new Asm(0);
        z.L("zreset").B(0xF3).B(0x31, 0xF0, 0x1F).B(0xED, 0x56);                    // DI; LD SP,$1FF0; IM 1
        z.B(0x3E, 0x00);                                                                 // LD A,0
        for (int i = 0; i < 9; i++) z.B(0x32, 0x00, 0x60);                               // LD ($6000),A x9: bank 0 (the 68000's $000000)
        z.B(0x21, 0x00, 0x00).B(0xFB);                                                   // LD HL,0; EI
        z.B(0x18).R("zloop");                                                            // JR zloop
        Org(z, 0x38);
        z.L("zirq").B(0xF5).B(0x3A, 0x00, 0x12).B(0x3C).B(0x32, 0x00, 0x12).B(0xF1).B(0xFB).B(0xC9);   // PUSH AF; LD A,($1200); INC A; LD ($1200),A; POP AF; EI; RET
        Org(z, 0x48);
        z.L("zloop").B(0x23);                                                            // INC HL
        z.L("zstore").B(0x22, 0x00, 0x10);                                              // LD ($1000),HL
        z.L("zcall").B(0xCD).A("zsub");                                                 // CALL zsub
        z.L("zafter").B(0x18).R("zloop");                                               // JR zloop
        z.L("zsub").B(0x3A, 0x00, 0x81);                                                // LD A,($8100): the 68000's ROM $000100 through the bank window
        z.L("zsub_store").B(0x32, 0x00, 0x11);                                          // LD ($1100),A
        z.L("zsub_ret").B(0xC9);                                                        // RET
        var zcode = z.Build();

        // ---- the 68000 program
        var m = new M68kAsm();
        m.Long(0x00FFFE00).AbsL("start");                                               // SSP, PC
        for (int v = 2; v < 64; v++)
            m.AbsL(v == 30 ? "vint" : v == 28 ? "hint" : "trap_stub");                   // level 6 = vector 30, level 4 = vector 28
        m.Org(0x100).Text("SEGA MEGA DRIVE (C)BRKN 2026.OCTBROKENDEV T0 DEBUGGER TEST ROM");
        m.Org(0x1F0).Text("JUE             ");
        m.Org(0x200);
        m.L("start");
        m.W(0x46FC, 0x2700);                                                            // MOVE #$2700,SR
        m.W(0x33FC, 0x0100).Long(0xA11100);                                              // MOVE.W #$0100,$A11100   (request the Z80's bus)
        m.W(0x33FC, 0x0100).Long(0xA11200);                                              // MOVE.W #$0100,$A11200   (release its reset)
        m.L("wait_bus").W(0x0839, 0x0000).Long(0xA11100);                               // BTST #0,$A11100
        m.Short(0x6600, "wait_bus");                                                    // BNE.S wait_bus
        m.W(0x41FA).Disp16("zcode");                                                    // LEA zcode(PC),A0
        m.W(0x43F9).Long(0xA00000);                                                     // LEA $A00000,A1
        m.W(0x303C, zcode.Length - 1);                                                  // MOVE.W #len-1,D0
        m.L("copy").W(0x12D8);                                                          // MOVE.B (A0)+,(A1)+
        m.W(0x51C8).Disp16("copy");                                                     // DBRA D0,copy
        m.W(0x33FC, 0x0000).Long(0xA11200);                                              // reset the Z80
        m.W(0x33FC, 0x0000).Long(0xA11100);                                              // give its bus back
        m.W(0x33FC, 0x0100).Long(0xA11200);                                              // and let it run
        m.W(0x33FC, 0x8004).Long(0xC00004);                                              // VDP register 0
        m.W(0x33FC, 0x8164).Long(0xC00004);                                              // VDP register 1: display and the vertical interrupt on, mode 5
        m.W(0x46FC, 0x2000);                                                            // MOVE #$2000,SR: interrupts on
        m.L("loop");
        m.L("call_sub1").W(0x4EB9).AbsL("sub1");                                        // JSR sub1
        m.L("after_call").W(0x5279).Long(0xFF0000);                                     // ADDQ.W #1,$FF0000
        m.L("mirror_write").W(0x33F9).Long(0xFF0000).Long(0xE00010);                    // MOVE.W $FF0000,$E00010  (the mirror of $FF0010)
        m.Short(0x6000, "loop");                                                        // BRA.S loop
        m.L("sub1").Short(0x6100, "sub2");                                              // BSR.S sub2
        m.L("sub1_ret").W(0x4E75);                                                      // RTS
        m.L("sub2").W(0x323C, 0x00FF);                                                  // MOVE.W #$00FF,D1
        m.L("sub2_loop").W(0x51C9).Disp16("sub2_loop");                                 // DBRA D1,sub2_loop
        m.L("sub2_ret").W(0x4E75);                                                      // RTS
        m.L("vint").W(0x52B9).Long(0xFF0004);                                           // ADDQ.L #1,$FF0004
        m.W(0x4E73);                                                                    // RTE
        m.L("hint").W(0x4E73);                                                          // RTE
        m.L("trap_stub").W(0x60FE);                                                     // BRA.S * (an exception nobody expects lands here)
        // a known stretch for the disassembler (never run)
        m.L("known");
        m.W(0x2039).Long(0x00FF1234);                                                   // MOVE.L $FF1234,D0
        m.W(0x48E7, 0xC0C0);                                                            // MOVEM.L D0-D1/A0-A1,-(A7)
        m.W(0x4CDF, 0x0303);                                                            // MOVEM.L (A7)+,D0-D1/A0-A1
        m.W(0x0C40, 0x1234);                                                            // CMPI.W #$1234,D0
        m.W(0x3030, 0x1002);                                                            // MOVE.W $02(A0,D1.W),D0
        m.W(0xE548);                                                                    // LSL.W #2,D0
        m.W(0x4E41);                                                                    // TRAP #1
        m.L("known_end");
        m.Org((m.Pc + 1) & ~1u);
        m.L("zcode").Bytes(zcode);
        var rom = m.Build(0x8000);

        var nm = new StringBuilder();
        foreach (var name in new[] { "start", "wait_bus", "copy", "loop", "call_sub1", "after_call", "mirror_write", "sub1", "sub1_ret", "sub2", "sub2_loop", "sub2_ret", "vint", "hint", "trap_stub", "known", "known_end" })
            nm.Append($"{m[name]:x8} T {name}\n");
        nm.Append("00ff0000 00000002 B counter\n00ff0004 00000004 B vint_count\n00ff0010 00000002 B mirror_target\n");
        return (rom, m, z, nm.ToString());
    }
}
