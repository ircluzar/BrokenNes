using System;
using System.Text;

namespace BrokenNes.Workshop.Debug;

/// <summary>
/// The hardware checks' test ROMs (BrokenDev milestone D4), assembled here like the debugger's (<see cref="DebugTestRoms"/>): one clean
/// template per console that breaks no rule, with slots a test fills with a few instructions that break one rule (the trigger) or do the
/// same thing the right way (the negative control). Each slot starts at a label (pre, init, main, nmi / vblank) so a finding can be checked
/// to name its place.
/// </summary>
internal static class CheckTestRoms
{
    /// <summary>What a test puts in the template. Bytes are raw machine code; <see cref="Extra"/> assembles more labels after the template.</summary>
    public sealed class Slots
    {
        public byte[] Pre = Array.Empty<byte>(), Init = Array.Empty<byte>(), Main = Array.Empty<byte>(), Nmi = Array.Empty<byte>(), Irq = Array.Empty<byte>();
        public Action<Asm>? Extra;
        /// <summary>Bytes placed at a fixed CPU address of the ROM (SNES bank 0, NES the fixed bank), e.g. an HDMA table at $F000.</summary>
        public (uint Address, byte[] Data)[] Fixed = Array.Empty<(uint, byte[])>();
        public bool ClearVram = true, ClearCgram = true, ClearOam = true, Cgb, Mapper30;
        public byte Tm;          // SNES: the main screen layers once the display is on
    }

    // ================================================================================================================================ NES
    /// <summary>NROM-128 at $C000 (or a self-flashing mapper-30 board): RAM cleared, frame IRQ off, NMI on, a main loop that waits for the
    /// NMI's frame counter ($20) in a spin loop.</summary>
    public static (byte[] rom, Asm asm, string mlb) Nes(Slots s)
    {
        var a = new Asm(0xC000);
        a.L("reset").B(0x78, 0xD8, 0xA2, 0xFF, 0x9A)                       // SEI CLD LDX #$FF TXS
         .B(0xA9, 0x40, 0x8D, 0x17, 0x40)                                  // LDA #$40 STA $4017 (frame IRQ off)
         .B(0xA9, 0x00, 0x8D, 0x00, 0x20, 0x8D, 0x01, 0x20, 0x8D, 0x10, 0x40, 0x8D, 0x15, 0x40)
         .L("vbl1").B(0x2C, 0x02, 0x20, 0x10).R("vbl1")                   // BIT $2002 BPL
         .L("pre").B(s.Pre)
         .B(0xA9, 0x00, 0xAA)                                              // LDA #0 TAX
         .L("clear").B(0x95, 0x00, 0x9D, 0x00, 0x01, 0x9D, 0x00, 0x02, 0x9D, 0x00, 0x03, 0x9D, 0x00, 0x04, 0x9D, 0x00, 0x05, 0x9D, 0x00, 0x06, 0x9D, 0x00, 0x07)
         .B(0xE8, 0xD0).R("clear")                                         // INX BNE clear
         .L("vbl2").B(0x2C, 0x02, 0x20, 0x10).R("vbl2")
         .L("init").B(s.Init)
         .B(0xA9, 0x80, 0x8D, 0x00, 0x20)                                  // NMI on
         .L("main").B(s.Main)
         .L("main_end").B(0xA5, 0x20)                                      // LDA $20
         .L("wait").B(0xC5, 0x20, 0xF0).R("wait")                          // CMP $20 BEQ wait (the frame wait: a spin)
         .B(0x4C).A("main")
         .L("nmi").B(0x48, 0x8A, 0x48, 0x98, 0x48, 0xE6, 0x20)             // PHA TXA PHA TYA PHA INC $20
         .L("nmi_slot").B(s.Nmi)
         .L("nmi_end").B(0x68, 0xA8, 0x68, 0xAA, 0x68, 0x40)               // PLA TAY PLA TAX PLA RTI
         .L("irq").B(s.Irq.Length > 0 ? s.Irq : new byte[] { 0x40 })
         .L("irq_end").B(0xEA);
        s.Extra?.Invoke(a);
        a.L("end");
        var code = a.Build();
        int banks = s.Mapper30 ? 2 : 1;
        var prg = new byte[0x4000 * banks];
        Array.Fill(prg, (byte)0xEA);
        int fixedAt = prg.Length - 0x4000;
        code.CopyTo(prg, fixedAt);
        void Vec(int at, string l) { prg[fixedAt + at] = (byte)a[l]; prg[fixedAt + at + 1] = (byte)(a[l] >> 8); }
        Vec(0x3FFA, "nmi"); Vec(0x3FFC, "reset"); Vec(0x3FFE, "irq");
        var rom = new byte[16 + prg.Length + (s.Mapper30 ? 0 : 0x2000)];
        rom[0] = (byte)'N'; rom[1] = (byte)'E'; rom[2] = (byte)'S'; rom[3] = 0x1A; rom[4] = (byte)banks; rom[5] = (byte)(s.Mapper30 ? 0 : 1);
        if (s.Mapper30) { rom[6] = 0xE2; rom[7] = 0x10; }   // mapper 30, battery (the self-flashing board), CHR RAM
        prg.CopyTo(rom, 16);
        var mlb = new StringBuilder();
        foreach (var l in new[] { "reset", "pre", "clear", "init", "main", "main_end", "wait", "nmi", "nmi_slot", "nmi_end", "irq", "irq_end" })
            mlb.Append($"NesPrgRom:{a[l] - 0xC000 + fixedAt:X6}:{l}:\n");
        mlb.Append("R:0020:frame_counter:\n");
        return (rom, a, mlb.ToString());
    }

    // ================================================================================================================================ SNES
    /// <summary>LoROM 32 KB: native mode, S = $1FFF, forced blank while work RAM, VRAM, CGRAM and OAM are cleared by DMA from a zero byte,
    /// then the display on (TM from the slots), NMI on and a main loop that waits with WAI.</summary>
    public static (byte[] rom, Asm asm, string sym) Snes(Slots s, byte[]? spcProgram = null)
    {
        var a = new Asm(0x8000);
        a.L("reset").B(0x78, 0x18, 0xFB)                                    // SEI CLC XCE (native)
         .B(0xC2, 0x10, 0xA2, 0xFF, 0x1F, 0x9A, 0xE2, 0x30)                 // REP #$10 LDX #$1FFF TXS SEP #$30
         .B(0xA9, 0x80, 0x8D, 0x00, 0x21, 0x9C, 0x00, 0x42, 0x9C, 0x0C, 0x42)   // forced blank, NMI off, HDMA off
         .L("pre").B(s.Pre)
         // work RAM: 2 x 64 KB through $2180 from a fixed zero byte
         .B(0x9C, 0x81, 0x21, 0x9C, 0x82, 0x21, 0x9C, 0x83, 0x21)           // STZ $2181-$2183
         .B(0xA9, 0x08, 0x8D, 0x00, 0x43, 0xA9, 0x80, 0x8D, 0x01, 0x43)
         .B(0xA9).Lo("zero").B(0x8D, 0x02, 0x43, 0xA9).Hi("zero").B(0x8D, 0x03, 0x43, 0x9C, 0x04, 0x43)
         .B(0x9C, 0x05, 0x43, 0x9C, 0x06, 0x43, 0xA9, 0x01, 0x8D, 0x0B, 0x42)
         .B(0x9C, 0x05, 0x43, 0x9C, 0x06, 0x43, 0xA9, 0x01, 0x8D, 0x0B, 0x42);
        if (s.ClearVram)
            a.B(0xA9, 0x80, 0x8D, 0x15, 0x21, 0x9C, 0x16, 0x21, 0x9C, 0x17, 0x21)
             .B(0xA9, 0x09, 0x8D, 0x00, 0x43, 0xA9, 0x18, 0x8D, 0x01, 0x43, 0x9C, 0x05, 0x43, 0x9C, 0x06, 0x43, 0xA9, 0x01, 0x8D, 0x0B, 0x42);
        if (s.ClearCgram)
            a.B(0x9C, 0x21, 0x21, 0xA9, 0x08, 0x8D, 0x00, 0x43, 0xA9, 0x22, 0x8D, 0x01, 0x43)
             .B(0x9C, 0x05, 0x43, 0xA9, 0x02, 0x8D, 0x06, 0x43, 0xA9, 0x01, 0x8D, 0x0B, 0x42);
        if (s.ClearOam)
            a.B(0x9C, 0x02, 0x21, 0x9C, 0x03, 0x21, 0xA9, 0x08, 0x8D, 0x00, 0x43, 0xA9, 0x04, 0x8D, 0x01, 0x43)
             .B(0xA9, 0x20, 0x8D, 0x05, 0x43, 0xA9, 0x02, 0x8D, 0x06, 0x43, 0xA9, 0x01, 0x8D, 0x0B, 0x42);
        if (spcProgram != null) SpcUpload(a, spcProgram);
        a.L("init").B(s.Init)
         .B(0xA9, s.Tm, 0x8D, 0x2C, 0x21)                                   // TM
         .B(0xA9, 0x0F, 0x8D, 0x00, 0x21, 0xA9, 0x80, 0x8D, 0x00, 0x42)     // display on, NMI on
         .L("main").B(s.Main)
         .L("main_end").B(0xCB)                                             // WAI (the frame wait)
         .B(0x4C).A("main")
         .L("nmi").B(0x48, 0xAD, 0x10, 0x42, 0xE6, 0x20)                    // PHA LDA $4210 INC $20
         .L("nmi_slot").B(s.Nmi)
         .L("nmi_end").B(0x68, 0x40)                                        // PLA RTI
         .L("irq").B(0x40)
         .L("zero").B(0x00);
        s.Extra?.Invoke(a);
        if (spcProgram != null) a.L("spcprog").B(spcProgram);
        a.L("end");
        var code = a.Build();
        var rom = new byte[0x8000];
        code.CopyTo(rom, 0);
        foreach (var (addr, data) in s.Fixed) data.CopyTo(rom, (int)(addr - 0x8000));
        void Vec(int at, string l) { rom[at] = (byte)a[l]; rom[at + 1] = (byte)(a[l] >> 8); }
        Vec(0x7FEA, "nmi"); Vec(0x7FEE, "irq"); Vec(0x7FFC, "reset"); Vec(0x7FFA, "nmi"); Vec(0x7FFE, "irq");
        Array.Copy(Encoding.ASCII.GetBytes("BROKENNES CHECK TEST "), 0, rom, 0x7FC0, 21);
        rom[0x7FD5] = 0x20; rom[0x7FD7] = 0x05; rom[0x7FD9] = 0x01;
        int sum = 0; rom[0x7FDC] = 0xFF; rom[0x7FDD] = 0xFF; rom[0x7FDE] = 0; rom[0x7FDF] = 0;
        foreach (byte b in rom) sum += b;
        rom[0x7FDE] = (byte)sum; rom[0x7FDF] = (byte)(sum >> 8); rom[0x7FDC] = (byte)~sum; rom[0x7FDD] = (byte)(~sum >> 8);
        var sym = new StringBuilder("; WLA-style symbols for the checks' test ROM\n");
        foreach (var l in new[] { "reset", "pre", "init", "main", "main_end", "nmi", "nmi_slot", "nmi_end" }) sym.Append($"0000{a[l]:x4} {l}\n");
        sym.Append("[labels]\n00:0020 frame_counter\n");
        return (rom, a, sym.ToString());
    }

    /// <summary>The IPL upload of an SPC700 program to $0200 and its start (the protocol of D1's debugger test ROM).</summary>
    private static void SpcUpload(Asm a, byte[] prog)
    {
        a.L("ipl_w1").B(0xAD, 0x40, 0x21, 0xC9, 0xAA, 0xD0).R("ipl_w1")
         .B(0xA9, 0x00, 0x8D, 0x42, 0x21, 0xA9, 0x02, 0x8D, 0x43, 0x21)
         .B(0xA9, 0x01, 0x8D, 0x41, 0x21, 0xA9, 0xCC, 0x8D, 0x40, 0x21)
         .L("ipl_w2").B(0xAD, 0x40, 0x21, 0xC9, 0xCC, 0xD0).R("ipl_w2")
         .B(0xA2, 0x00)
         .L("ipl_send").B(0xBD).A("spcprog").B(0x8D, 0x41, 0x21, 0x8A, 0x8D, 0x40, 0x21)
         .L("ipl_w3").B(0xCD, 0x40, 0x21, 0xD0).R("ipl_w3")
         .B(0xE8, 0xE0, (byte)prog.Length, 0xD0).R("ipl_send")
         .B(0xA9, 0x00, 0x8D, 0x42, 0x21, 0xA9, 0x02, 0x8D, 0x43, 0x21)
         .B(0x9C, 0x41, 0x21, 0x8A, 0x1A, 0x8D, 0x40, 0x21);
    }

    /// <summary>An SPC700 program at $0200 that puts the echo buffer at <paramref name="esa"/> x 256 (2 KB), switches echo writes on and spins.</summary>
    public static byte[] SpcEcho(byte esa) => new byte[]
    {
        0x8F, 0x6D, 0xF2, 0x8F, esa, 0xF3,    // ESA
        0x8F, 0x7D, 0xF2, 0x8F, 0x01, 0xF3,   // EDL = 1 (2 KB)
        0x8F, 0x6C, 0xF2, 0x8F, 0x00, 0xF3,   // FLG = 0: echo writes on
        0x2F, 0xFE,                           // BRA *
    };

    // ================================================================================================================================ Game Boy
    /// <summary>MBC1 + 8 KB RAM, 32 KB: the LCD off in vblank, work RAM, HRAM and OAM cleared, the OAM DMA routine copied to HRAM ($FF80,
    /// source page in A), the LCD on (BG and sprites), the vblank interrupt on and a main loop that waits with HALT. The stack is in work RAM.</summary>
    public static (byte[] rom, Asm asm, string sym) GameBoy(Slots s)
    {
        var a = new Asm(0x0150);
        a.L("start").B(0xF3, 0x31, 0x00, 0xE0)                              // DI; LD SP,$E000
         .L("vbl1").B(0xF0, 0x44, 0xFE, 0x90, 0x20).R("vbl1")               // LDH A,(LY); CP 144; JR NZ
         .B(0xAF, 0xE0, 0x40)                                               // XOR A; LDH (LCDC),A: off in vblank
         .L("pre").B(s.Pre)
         .B(0x21, 0x00, 0xC0, 0x01, 0x00, 0x20)                             // LD HL,$C000; LD BC,$2000
         .L("clr").B(0xAF, 0x22, 0x0B, 0x78, 0xB1, 0x20).R("clr")           // XOR A; LD (HL+),A; DEC BC; LD A,B; OR C; JR NZ
         .B(0x21, 0x80, 0xFF, 0x06, 0x7F)                                   // LD HL,$FF80; LD B,$7F
         .L("clrh").B(0xAF, 0x22, 0x05, 0x20).R("clrh")
         .B(s.ClearOam ? new byte[] { 0x21, 0x00, 0xFE, 0x06, 0xA0, 0xAF, 0x22, 0x05, 0x20, 0xFB } : Array.Empty<byte>())   // LD HL,$FE00; LD B,$A0; XOR A; LD (HL+),A; DEC B; JR NZ
         .B(0x21).A("dmarout").B(0x11, 0x80, 0xFF, 0x06, 0x08)               // LD HL,dmarout; LD DE,$FF80; LD B,8
         .L("cpy").B(0x2A, 0x12, 0x13, 0x05, 0x20).R("cpy")                 // LD A,(HL+); LD (DE),A; INC DE; DEC B; JR NZ
         .L("init").B(s.Init)
         .B(0x3E, 0x93, 0xE0, 0x40)                                         // LCD on, BG + sprites, tiles at $8000
         .B(0x3E, 0x01, 0xE0, 0xFF, 0xAF, 0xE0, 0x0F, 0xFB)                 // IE = vblank; IF = 0; EI
         .L("main").B(s.Main)
         .L("main_end").B(0x76, 0x00)                                       // HALT; NOP (the frame wait)
         .B(0x18).R("main")
         .L("vblank").B(0xF5)                                               // PUSH AF
         .L("vblank_slot").B(s.Nmi)
         .L("vblank_end").B(0xF1, 0xD9)                                     // POP AF; RETI
         .L("dmarout").B(0xE0, 0x46, 0x3E, 0x28, 0x3D, 0x20, 0xFD, 0xC9);   // LDH ($46),A; LD A,40; DEC A; JR NZ,-3; RET
        s.Extra?.Invoke(a);
        a.L("end");
        var rom = new byte[0x8000];
        rom[0x100] = 0x00; rom[0x101] = 0xC3; rom[0x102] = 0x50; rom[0x103] = 0x01;
        rom[0x40] = 0xC3; rom[0x41] = (byte)a["vblank"]; rom[0x42] = (byte)(a["vblank"] >> 8);
        Encoding.ASCII.GetBytes("CHECKTEST").CopyTo(rom, 0x134);
        if (s.Cgb) rom[0x143] = 0x80;
        rom[0x147] = 0x03; rom[0x148] = 0x00; rom[0x149] = 0x02;            // MBC1 + RAM + battery, 32 KB, 8 KB RAM
        byte chk = 0; for (int i = 0x134; i <= 0x14C; i++) chk = (byte)(chk - rom[i] - 1);
        rom[0x14D] = chk;
        a.Build().CopyTo(rom, 0x150);
        var sym = new StringBuilder("; bgb-style symbols for the checks' test ROM\n");
        foreach (var l in new[] { "start", "pre", "init", "main", "main_end", "vblank", "vblank_slot", "vblank_end", "dmarout" }) sym.Append($"00:{a[l]:X4} {l}\n");
        return (rom, a, sym.ToString());
    }
}
