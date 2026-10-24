using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BrokenNes.Workshop.Debug;

/// <summary>A tiny assembler for the debugger's test ROMs: bytes, labels and their fixups (absolute 16-bit, 24-bit long, 8-bit relative).</summary>
internal sealed class Asm
{
    private readonly List<byte> bytes = new();
    private readonly Dictionary<string, uint> labels = new();
    private readonly List<(int at, string label, char kind, uint bank)> fixups = new();
    public uint Origin { get; }
    public Asm(uint origin) { Origin = origin; }
    public uint Pc => Origin + (uint)bytes.Count;
    public Asm L(string name) { labels[name] = Pc; return this; }
    public Asm B(params byte[] b) { bytes.AddRange(b); return this; }
    /// <summary>A 16-bit absolute operand: a label (low, high).</summary>
    public Asm A(string label) { fixups.Add((bytes.Count, label, 'a', 0)); bytes.Add(0); bytes.Add(0); return this; }
    /// <summary>A 24-bit long operand: a label in <paramref name="bank"/>.</summary>
    public Asm Long(string label, uint bank) { fixups.Add((bytes.Count, label, 'l', bank)); bytes.Add(0); bytes.Add(0); bytes.Add(0); return this; }
    /// <summary>An 8-bit relative branch operand (relative to the byte after it).</summary>
    public Asm R(string label) { fixups.Add((bytes.Count, label, 'r', 0)); bytes.Add(0); return this; }
    public Asm Lo(string label) { fixups.Add((bytes.Count, label, '<', 0)); bytes.Add(0); return this; }
    public Asm Hi(string label) { fixups.Add((bytes.Count, label, '>', 0)); bytes.Add(0); return this; }
    public uint this[string label] => labels[label];
    public IReadOnlyDictionary<string, uint> Labels => labels;

    public byte[] Build()
    {
        foreach (var (at, label, kind, bank) in fixups)
        {
            uint v = labels.TryGetValue(label, out var x) ? x : throw new InvalidOperationException($"undefined label {label}");
            switch (kind)
            {
                case 'a': bytes[at] = (byte)v; bytes[at + 1] = (byte)(v >> 8); break;
                case 'l': bytes[at] = (byte)v; bytes[at + 1] = (byte)(v >> 8); bytes[at + 2] = (byte)bank; break;
                case '<': bytes[at] = (byte)v; break;
                case '>': bytes[at] = (byte)(v >> 8); break;
                default:
                    int rel = (int)v - (int)(Origin + at + 1);
                    if (rel < -128 || rel > 127) throw new InvalidOperationException($"branch to {label} out of range");
                    bytes[at] = (byte)(sbyte)rel; break;
            }
        }
        return bytes.ToArray();
    }
}

/// <summary>
/// The debugger conformance suite's test ROMs, assembled here so the suite needs no toolchain. Each has known behaviour (what runs, what
/// it writes where, how it calls) and a symbol file in its console's format.
/// </summary>
internal static class DebugTestRoms
{
    // ------------------------------------------------------------------------------------------------------------------ NES
    /// <summary>NROM-128 at $C000: a main loop that calls sub1 -> sub2, counts in $10, writes RAM through the $0812 mirror, an NMI
    /// handler that counts in $20 and runs an OAM DMA from page 2, and an instruction nothing ever reaches.</summary>
    public static (byte[] rom, Asm asm, string mlb) Nes()
    {
        var a = new Asm(0xC000);
        a.L("reset").B(0x78, 0xD8, 0xA2, 0xFF, 0x9A)            // SEI CLD LDX #$FF TXS
         .B(0xA9, 0x00, 0x8D, 0x00, 0x20, 0x8D, 0x01, 0x20)    // LDA #0 STA $2000 STA $2001
         .B(0xA9, 0x80, 0x8D, 0x00, 0x20)                       // LDA #$80 STA $2000 (NMI on)
         .L("loop").B(0x20).A("sub1")                           // JSR sub1
         .L("inc10").B(0xE6, 0x10)                              // INC $10
         .B(0xA5, 0x10)                                         // LDA $10
         .L("sta_mirror").B(0x8D, 0x12, 0x08)                   // STA $0812  (RAM $0012 through a mirror)
         .B(0xA5, 0x11)                                         // LDA $11
         .B(0x4C).A("loop")                                     // JMP loop
         .L("sub1").B(0x20).A("sub2")                           // JSR sub2
         .L("sub1_rts").B(0x60)                                 // RTS
         .L("sub2").B(0xA2, 0x03, 0x8E, 0x13, 0x00)             // LDX #3 STX $0013
         .B(0x60)                                               // RTS
         .L("nmi").B(0xE6, 0x20)                                // INC $20
         .B(0xA9, 0x02, 0x8D, 0x14, 0x40)                       // LDA #2 STA $4014 (OAM DMA from $0200)
         .B(0x40)                                               // RTI
         .L("irq").B(0x40)
         .L("never").B(0xEA, 0x4C).A("never");                  // NOP JMP never (nothing jumps here)
        var code = a.Build();
        var prg = new byte[0x4000];
        Array.Fill(prg, (byte)0xEA);
        code.CopyTo(prg, 0);
        void Vec(int at, string l) { prg[at] = (byte)a[l]; prg[at + 1] = (byte)(a[l] >> 8); }
        Vec(0x3FFA, "nmi"); Vec(0x3FFC, "reset"); Vec(0x3FFE, "irq");
        var rom = new byte[16 + 0x4000 + 0x2000];
        rom[0] = (byte)'N'; rom[1] = (byte)'E'; rom[2] = (byte)'S'; rom[3] = 0x1A; rom[4] = 1; rom[5] = 1;
        prg.CopyTo(rom, 16);
        // Mesen / NESFab labels: code by PRG ROM offset (NROM-128 at $C000 is offset 0), variables by RAM address, NESFab-style @n bytes
        var mlb = new StringBuilder();
        foreach (var l in new[] { "loop", "inc10", "sta_mirror", "sub1", "sub1_rts", "sub2", "nmi", "never" })
            mlb.Append($"NesPrgRom:{a[l] - 0xC000:X6}:{l}{(l == "nmi" ? "_handler@31_0" : "")}:\n");
        mlb.Append("R:0010:counter@0:\nR:0012:mirror_target@0:\nR:0013:xval@0:\nR:0020:frames@0:\nR:0020:frames_copy@0:\nR:0200:oam@0:\nR:0201:oam@1:\nR:0202:oam@2:\n");
        mlb.Append("R:0030:split@0:\nR:0032:split@1:\nG:2000:PPUCTRL:\n");
        return (rom, a, mlb.ToString());
    }

    // ------------------------------------------------------------------------------------------------------------------ SNES
    /// <summary>LoROM, 32 KB: forced blank, a DMA of 16 bytes to VRAM word $1000, an upload of a small SPC700 program through the IPL (it
    /// calls a subroutine and counts in its direct page $40), then a loop that calls lsub (JSL) -> ssub (JSR), counts in $10, writes
    /// $7E:0012 and the $80:0013 mirror of $0013, and an NMI handler that counts in $20.</summary>
    public static (byte[] rom, Asm asm, Asm spc, string sym) Snes()
    {
        var s = new Asm(0x0200);
        s.L("spc_start").B(0xE8, 0x00)                  // MOV A,#0
         .L("spc_loop").B(0x3F).A("spc_sub")            // CALL spc_sub
         .L("spc_inc").B(0xAB, 0x40)                     // INC $40
         .B(0xC4, 0x41)                                  // MOV $41,A
         .B(0x2F).R("spc_loop")                          // BRA spc_loop
         .L("spc_sub").B(0xE4, 0x40)                     // MOV A,$40
         .L("spc_ret").B(0x6F);                          // RET
        var spcProg = s.Build();

        var a = new Asm(0x8000);
        a.L("reset").B(0x78, 0x18, 0xFB, 0xE2, 0x30)                              // SEI CLC XCE SEP #$30
         .B(0xA9, 0x80, 0x8D, 0x00, 0x21)                                          // forced blank
         .B(0xA9, 0x80, 0x8D, 0x15, 0x21)                                          // VMAIN: increment after $2119
         .B(0xA9, 0x00, 0x8D, 0x16, 0x21, 0xA9, 0x10, 0x8D, 0x17, 0x21)            // VRAM word $1000
         .B(0xA9, 0x01, 0x8D, 0x00, 0x43, 0xA9, 0x18, 0x8D, 0x01, 0x43)            // DMA0: 2 registers, $2118
         .B(0xA9).Lo("table").B(0x8D, 0x02, 0x43).B(0xA9).Hi("table").B(0x8D, 0x03, 0x43)
         .B(0xA9, 0x00, 0x8D, 0x04, 0x43)                                          // source bank 0
         .B(0xA9, 0x10, 0x8D, 0x05, 0x43, 0xA9, 0x00, 0x8D, 0x06, 0x43)            // 16 bytes
         .L("dma_go").B(0xA9, 0x01, 0x8D, 0x0B, 0x42)                              // MDMAEN
         // the SPC700 upload (the IPL's protocol)
         .L("w1").B(0xAD, 0x40, 0x21, 0xC9, 0xAA, 0xD0).R("w1")
         .B(0xA9, 0x00, 0x8D, 0x42, 0x21, 0xA9, 0x02, 0x8D, 0x43, 0x21)            // destination $0200
         .B(0xA9, 0x01, 0x8D, 0x41, 0x21, 0xA9, 0xCC, 0x8D, 0x40, 0x21)            // transfer, kick
         .L("w2").B(0xAD, 0x40, 0x21, 0xC9, 0xCC, 0xD0).R("w2")
         .B(0xA2, 0x00)                                                            // LDX #0
         .L("send").B(0xBD).A("spcprog").B(0x8D, 0x41, 0x21, 0x8A, 0x8D, 0x40, 0x21)   // LDA spcprog,X STA $2141 TXA STA $2140
         .L("w3").B(0xCD, 0x40, 0x21, 0xD0).R("w3")                                // CMP $2140 BNE
         .B(0xE8, 0xE0, (byte)spcProg.Length, 0xD0).R("send")                      // INX CPX #len BNE send
         .B(0xA9, 0x00, 0x8D, 0x42, 0x21, 0xA9, 0x02, 0x8D, 0x43, 0x21)            // entry $0200
         .B(0x9C, 0x41, 0x21, 0x8A, 0x1A, 0x8D, 0x40, 0x21)                        // STZ $2141 TXA INC A STA $2140: execute
         .B(0xA9, 0x80, 0x8D, 0x00, 0x42)                                          // NMI on
         .L("loop").B(0x22).Long("lsub", 0x00)                                     // JSL lsub
         .L("inc10").B(0xE6, 0x10)                                                 // INC $10
         .B(0xA5, 0x10)                                                            // LDA $10
         .L("sta_long").B(0x8F, 0x12, 0x00, 0x7E)                                  // STA $7E0012
         .L("sta_mirror").B(0x8F, 0x13, 0x00, 0x80)                                // STA $800013 (work RAM $0013 through a mirror)
         .B(0xA5, 0x11)                                                            // LDA $11
         .B(0x4C).A("loop")                                                        // JMP loop
         .L("lsub").B(0x20).A("ssub")                                              // JSR ssub
         .L("lsub_rtl").B(0x6B)                                                    // RTL
         .L("ssub").B(0xA9, 0x03, 0x8D, 0x14, 0x00)                                // LDA #3 STA $0014
         .B(0x60)                                                                  // RTS
         .L("nmi").B(0x48, 0xE6, 0x20, 0xAD, 0x10, 0x42, 0x68, 0x40)               // PHA INC $20 LDA $4210 PLA RTI
         .L("irq").B(0x40)
         .L("never").B(0xEA, 0x4C).A("never")
         .L("table").B(0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xF1, 0xF2)
         .L("spcprog").B(spcProg);
        var code = a.Build();
        var rom = new byte[0x8000];
        code.CopyTo(rom, 0);
        void Vec(int at, string l) { rom[at] = (byte)a[l]; rom[at + 1] = (byte)(a[l] >> 8); }
        Vec(0x7FEA, "nmi"); Vec(0x7FEE, "irq"); Vec(0x7FFC, "reset"); Vec(0x7FFA, "nmi"); Vec(0x7FFE, "irq");
        var title = Encoding.ASCII.GetBytes("BROKENNES DEBUG TEST  ");
        Array.Copy(title, 0, rom, 0x7FC0, 21);
        rom[0x7FD5] = 0x20; rom[0x7FD6] = 0x00; rom[0x7FD7] = 0x05; rom[0x7FD8] = 0x00; rom[0x7FD9] = 0x01;
        rom[0x7FDC] = 0xFF; rom[0x7FDD] = 0xFF; rom[0x7FDE] = 0; rom[0x7FDF] = 0;
        int sum = 0; foreach (byte b in rom) sum += b;
        rom[0x7FDE] = (byte)sum; rom[0x7FDF] = (byte)(sum >> 8); rom[0x7FDC] = (byte)~sum; rom[0x7FDD] = (byte)(~sum >> 8);
        // WLA-DX style: the no$snes flat form for code, the [labels] form for variables (both are what wlalink writes)
        var sym = new StringBuilder("; wlalink no$snes symbols (hand-written for the debugger checks)\n");
        foreach (var l in new[] { "reset", "loop", "inc10", "sta_long", "sta_mirror", "lsub", "lsub_rtl", "ssub", "nmi", "never", "dma_go" })
            sym.Append($"0000{a[l]:x4} {l}\n");
        sym.Append("[labels]\n00:0010 counter\n00:0012 long_target\n00:0013 mirror_target\n00:0014 xval\n00:0020 frames\n7e:0021 after_frames\n");
        sym.Append("[definitions]\n00000010 COUNTER_CONST\n");
        return (rom, a, s, sym.ToString());
    }

    // ------------------------------------------------------------------------------------------------------------------ Game Boy
    /// <summary>MBC1, 64 KB: selects ROM bank 2, then loops calling sub1 -> sub2 and a function at $4000 in bank 2 (bank 1 has a different
    /// one there, never mapped), counts in $C010, writes $C011 through the echo at $E011; a VBlank handler counts in $C020 and runs an OAM
    /// DMA from $C100.</summary>
    public static (byte[] rom, Asm asm, string sym, string noi) GameBoy()
    {
        var a = new Asm(0x0150);
        a.L("start").B(0xF3, 0x31, 0xFE, 0xFF)          // DI; LD SP,$FFFE
         .B(0x3E, 0x02, 0xEA, 0x00, 0x20)               // LD A,2; LD ($2000),A  (ROM bank 2)
         .B(0x3E, 0x01, 0xE0, 0xFF, 0xAF, 0xE0, 0x0F)   // IE = VBlank; IF = 0
         .B(0xFB)                                       // EI
         .L("loop").B(0xCD).A("sub1")                   // CALL sub1
         .L("after_call").B(0x21, 0x10, 0xC0, 0x34)     // LD HL,$C010; INC (HL)
         .B(0x7E)                                       // LD A,(HL)
         .L("echo_write").B(0xEA, 0x11, 0xE0)           // LD ($E011),A (work RAM $C011 through the echo)
         .B(0xCD, 0x00, 0x40)                           // CALL $4000 (bank 2)
         .B(0x18).R("loop")                             // JR loop
         .L("sub1").B(0xCD).A("sub2").L("sub1_ret").B(0xC9)
         .L("sub2").B(0x3E, 0x03, 0xEA, 0x12, 0xC0, 0xC9)
         .L("never").B(0x00, 0xC3).A("never");
        var vb = new Asm(0x0180);
        vb.L("vblank").B(0xF5, 0xFA, 0x20, 0xC0, 0x3C, 0xEA, 0x20, 0xC0)   // PUSH AF; LD A,($C020); INC A; LD ($C020),A
          .B(0x3E, 0xC1, 0xE0, 0x46)                                       // OAM DMA from $C100
          .B(0xF1, 0xD9);                                                  // POP AF; RETI
        var rom = new byte[0x10000];
        rom[0x0100] = 0x00; rom[0x0101] = 0xC3; rom[0x0102] = 0x50; rom[0x0103] = 0x01;
        rom[0x0040] = 0xC3; rom[0x0041] = 0x80; rom[0x0042] = 0x01;          // JP vblank
        var title = Encoding.ASCII.GetBytes("DBGTEST");
        title.CopyTo(rom, 0x134);
        rom[0x147] = 0x01; rom[0x148] = 0x01; rom[0x149] = 0x00;
        byte chk = 0; for (int i = 0x134; i <= 0x14C; i++) chk = (byte)(chk - rom[i] - 1);
        rom[0x14D] = chk;
        a.Build().CopyTo(rom, 0x150);
        vb.Build().CopyTo(rom, 0x180);
        // bank 1 at $4000 (ROM $4000): never mapped; bank 2 at $4000 (ROM $8000): called every loop
        new byte[] { 0x3E, 0x09, 0xEA, 0x14, 0xC0, 0xC9 }.CopyTo(rom, 0x4000);
        new byte[] { 0x3E, 0x05, 0xEA, 0x13, 0xC0, 0xC9 }.CopyTo(rom, 0x8000);
        var sym = new StringBuilder("; bgb / rgbds / GBDK style symbols (hand-written for the debugger checks)\n");
        foreach (var l in new[] { "start", "loop", "after_call", "echo_write", "sub1", "sub1_ret", "sub2", "never" }) sym.Append($"00:{a[l]:X4} {l}\n");
        sym.Append("00:0180 vblank_handler\n01:4000 bank1_func\n02:4000 bank2_func\n00:C010 counter\n00:C011 echo_target\n00:C020 frames\n");
        // GBDK's .noi (SDCC linker): banked addresses carry the bank above bit 16; area starts, lengths and bank numbers are constants
        var noi = "DEF _sub2 0x" + a["sub2"].ToString("X") + "\nDEF _bank2_func 0x24000\nDEF _bank1_func 0x14000\nDEF _counter 0xC010\nDEF l__DATA 0x20\nDEF s__CODE 0x200\nDEF ___bank_main 0x2\nDEF b_bank2_func 0x2\n";
        return (rom, a, sym.ToString(), noi);
    }

    public static string Write(string dir, string name, byte[] data) { string p = Path.Combine(dir, name); File.WriteAllBytes(p, data); return p; }
    public static string Write(string dir, string name, string text) { string p = Path.Combine(dir, name); File.WriteAllText(p, text); return p; }
}
