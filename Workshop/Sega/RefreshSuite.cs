using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// The Z80's refresh register R, read with <c>LD A,R</c> after prefixed instructions, a repeating block instruction, the acknowledge of an interrupt and a HALT. The expected steps are the
/// Zilog manual's (one per opcode fetch, so two for a prefixed instruction, two per repeat of a block instruction) and were also what the oracle (Mesen 2.1.1) measured on the same program, with ONE exception that
/// matters: the oracle's R does not count while the CPU is halted (it moves by 1 for the HALT and 1 for the interrupt acknowledge and nothing for the thousands of NOP cycles between), a real Z80 refreshes on every halted
/// NOP cycle. This board follows the real chip; the tool-only setting <c>OracleHaltNoRefresh</c> follows the oracle so that games that take random numbers from R can be compared with it frame for frame
/// (Sonic 2 on the Game Gear went from 617 of 631 stable frames identical to 636 of 636 and from 27 to 262 of 267 frames in which the oracle disagrees with itself).
/// </summary>
internal static class RefreshSuite
{
    private static byte[] St(int address) => SmsProg.B(0xED, 0x5F, 0x32, address & 0xFF, 0xC0);        // LD A,R ; LD ($C0aa),A

    /// <summary>The probe program (the bytes the oracle ran): R is stored at $C000.. $C009 (see the checks for which is which).</summary>
    public static byte[] Probe()
    {
        var code = SmsProg.Cat(
            SmsProg.Prolog,
            St(0x00), St(0x01),
            SmsProg.B(0xDD, 0x7E, 0x00), St(0x02),                                   // LD A,(IX+0)
            SmsProg.B(0xCB, 0x40), St(0x03),                                         // BIT 0,B
            SmsProg.B(0x21, 0x00, 0xC1, 0x11, 0x00, 0xC2, 0x01, 0x05, 0x00, 0xED, 0xB0), St(0x04),   // LDIR with BC = 5
            SmsProg.B(0x01, 0x03, 0x00, 0xED, 0xB1), St(0x08),                       // CPIR with BC = 3, nothing matches
            SmsProg.B(0xDD, 0xCB, 0x00, 0x46), St(0x09),                             // BIT 0,(IX+0)
            SmsProg.B(0x3E, 0xE2, 0xD3, 0xBF, 0x3E, 0x81, 0xD3, 0xBF, 0xDB, 0xBF),   // the frame interrupt on, the status read clears it
            St(0x05),
            SmsProg.B(0xFB, 0x76),                                                   // EI ; HALT
            St(0x07),
            SmsProg.B(0xF3, 0x18, 0xFE));                                            // DI ; JR $
        var handler = SmsProg.Cat(St(0x06), SmsProg.B(0xDB, 0xBF, 0xED, 0x4D));     // R in the handler ; read the status ; RETI (interrupts stay off)
        return SmsProg.Rom(code, handler);
    }

    private static byte[] Run(SmsSuiteContext ctx, bool oracleHalt)
    {
        var s = ctx.Session(Probe(), "r-probe.sms");
        if (oracleHalt) s.Board.OracleHaltNoRefresh = 1;      // the default is left to the context (the proof-of-failure run sets it to 1 there)
        for (int f = 0; f < 5; f++) s.RunFrame();
        return s.Board.Ram.AsSpan(0, 10).ToArray();
    }

    /// <summary>A program that stores every register as the cartridge finds it at its first instruction (main set, shadow set, SP, IX, IY) to $C100 and up.</summary>
    private static byte[] RegisterProbe() => SmsProg.Rom(SmsProg.Cat(
        SmsProg.B(0xED, 0x73, 0x00, 0xC1, 0x22, 0x02, 0xC1, 0xED, 0x53, 0x04, 0xC1, 0xED, 0x43, 0x06, 0xC1),          // LD ($C100),SP ; LD ($C102),HL ; LD ($C104),DE ; LD ($C106),BC
        SmsProg.B(0x31, 0xF0, 0xDF, 0xF5, 0xE1, 0x22, 0x08, 0xC1),                                                      // LD SP,$DFF0 ; PUSH AF ; POP HL ; LD ($C108),HL
        SmsProg.B(0xD9, 0x22, 0x0A, 0xC1, 0xED, 0x53, 0x0C, 0xC1, 0xED, 0x43, 0x0E, 0xC1),                              // EXX ; LD ($C10A),HL' ; LD ($C10C),DE' ; LD ($C10E),BC'
        SmsProg.B(0x08, 0xF5, 0xE1, 0x22, 0x10, 0xC1, 0xDD, 0x22, 0x14, 0xC1, 0xFD, 0x22, 0x16, 0xC1, 0x18, 0xFE)));   // EX AF,AF' ; PUSH AF ; POP HL ; LD ($C110),HL ; LD ($C114),IX ; LD ($C116),IY ; JR $

    private static int D(byte[] r, int hi, int lo) => (r[hi] - r[lo]) & 0x7F;       // R is a 7-bit counter (bit 7 is only what a program wrote)

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        var z = Run(ctx, false);
        string dump = string.Join(' ', z.Select(b => b.ToString("X2")));
        yield return new SmsCheck("two LD A,R in a row (LD (nn),A and the ED prefix and opcode of LD A,R): R advances by 3", D(z, 1, 0) == 3, dump);
        yield return new SmsCheck("a DD-prefixed instruction costs two fetches: LD A,(IX+0) then store and read: 5", D(z, 2, 1) == 5, dump);
        yield return new SmsCheck("a CB-prefixed instruction costs two: BIT 0,B: 5", D(z, 3, 2) == 5, dump);
        yield return new SmsCheck("LDIR with BC = 5 counts two per repeat: 3 loads (1 each), 10 for the five repeats, the store and the read: 16", D(z, 4, 3) == 16, dump);
        yield return new SmsCheck("CPIR with BC = 3: LD BC (1), 6 for three repeats, the store and the read: 10", D(z, 8, 4) == 10, dump);
        yield return new SmsCheck("a DD CB d op instruction counts two (the last byte is data): BIT 0,(IX+0): 5", D(z, 9, 8) == 5, dump);
        yield return new SmsCheck("the frame interrupt set-up (5 one-fetch instructions) then the store and the read: 8", D(z, 5, 9) == 8, dump);

        // the HALT: this board refreshes on every halted NOP cycle like the chip; the oracle's R does not move while halted
        int halted = D(z, 6, 5);
        var o = Run(ctx, true);
        string dumpO = string.Join(' ', o.Select(b => b.ToString("X2")));
        yield return new SmsCheck("with the oracle's setting (R does not count while halted): store, EI, HALT, acknowledge and the handler's read = 6, then the handler's store, the status read, RETI and the read after the wake-up = 6",
            D(o, 6, 5) == 6 && D(o, 7, 6) == 6, dumpO);
        yield return new SmsCheck("as the chip does (default): the thousands of halted NOP cycles move R, so the handler sees more than the oracle's 6 (here a count of the halted cycles / 4 modulo 128)", halted != 6, $"{halted} vs 6; {dump}");
        // the registers at a cartridge's first instruction, read by the oracle with the same program: SP $DFF0, AF $AB42, BC $0002, DE $A51B, HL 0; shadow BC' $0200, DE' $C700, HL' $C75F, AF' 0; IX and IY 0
        var rs = ctx.Session(RegisterProbe(), "reg-probe.sms");
        if (ctx.Poison == "refresh") { rs.Board.Cpu.BC2 = 0; rs.Board.Cpu.DE2 = 0; rs.Board.Cpu.HL2 = 0; }      // the proof of failure: the shadow set left at zero, as it was before this check existed
        for (int f = 0; f < 3; f++) rs.RunFrame();
        var ram = rs.Board.Ram;
        string regs = string.Join(' ', ram.AsSpan(0x100, 0x18).ToArray().Select(b => b.ToString("X2")));
        const string expect = "F0 DF 00 00 1B A5 02 00 42 AB 5F C7 00 C7 00 02 00 00 00 00 00 00 00 00";
        yield return new SmsCheck("the registers at a cartridge's first instruction, shadow set included, are the oracle's (SP, HL, DE, BC, AF, HL', DE', BC', AF', IX, IY)", regs == expect, regs);
        yield return new SmsCheck("after the acknowledge both settings take the same steps (the handler's store, the status read, RETI and the read: 6)", D(z, 7, 6) == 6, dump);
    }
}
