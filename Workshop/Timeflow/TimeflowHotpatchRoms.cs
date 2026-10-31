using System;
using System.Text;
using BrokenNes.Workshop.Debug;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>
/// T5's test game (docs/projects/brokendev-timeflow-plan.md, "T5 as built"): T3's planted corruption (<c>fill_table</c>'s <c>CPX #$21</c> where it
/// meant <c>CPX #$10</c>, so the 33rd update's <c>STA table,X</c> lands on <c>hp</c>) with one thing more: every 8th frame <c>update</c> calls
/// <c>slow</c>, a delay loop longer than a frame, so the frame after it starts with the PC inside <c>slow</c> and <c>update</c>'s return address on the
/// stack (the other frames start in <c>main_loop</c>). Its builds, as a project would make them:
/// <list type="bullet">
/// <item><b>planted</b>: the bug (<c>CPX #$21</c>);</item>
/// <item><b>fixed</b>: <c>CPX #$10</c>, every routine where it was (a rebuild-resume is safe at any frame);</item>
/// <item><b>moved</b>: the fix and three bytes more at the start of <c>slow</c> (a NOP sled: it moves <c>slow</c>'s loop, the NMI and IRQ handlers):
/// a frame that starts inside <c>slow</c> is refused, and the next frame that starts in <c>main_loop</c> is the safe resume point.</item>
/// </list>
/// </summary>
internal static class TimeflowHotpatchRoms
{
    public const ushort Hp = 0x50, FrameCt = 0x51, Idx = 0x52, GameOver = 0x53, NmiFlag = 0x10, Table = 0x30;

    public static (byte[] rom, Asm asm, string mlb) Game(string build)
    {
        bool fix = build is "fixed" or "moved";
        int pad = build == "moved" ? 3 : 0;
        var a = new Asm(0xC000);
        a.L("reset").B(0x78, 0xD8, 0xA2, 0xFF, 0x9A)                 // SEI CLD LDX #$FF TXS
         .B(0xA9, 0x00, 0x8D, 0x00, 0x20, 0x8D, 0x01, 0x20)          // LDA #0 STA $2000 STA $2001
         .B(0xA2, 0x00).L("clear").B(0x95, 0x00, 0xE8, 0xD0).R("clear")   // LDX #0 clear: STA $00,X INX BNE clear
         .L("init").B(0xA9, 0x05, 0x85, (byte)Hp)                    // LDA #5 STA hp
         .B(0xA9, 0x00, 0x85, (byte)Idx, 0x85, (byte)FrameCt, 0x85, (byte)GameOver)
         .B(0xA9, 0x80, 0x8D, 0x00, 0x20)                             // LDA #$80 STA $2000 (NMI on)
         .L("main_loop").B(0xA5, (byte)NmiFlag, 0xF0).R("main_loop") // LDA nmi_flag BEQ main_loop
         .B(0xA9, 0x00, 0x85, (byte)NmiFlag)                          // LDA #0 STA nmi_flag
         .B(0x20).A("update")                                         // JSR update
         .B(0x4C).A("main_loop")                                      // JMP main_loop
         .L("update").B(0xE6, (byte)FrameCt)                          // INC frame_ct
         .B(0x20).A("fill_table")                                     // JSR fill_table
         .B(0x20).A("check_hp")                                       // JSR check_hp
         .B(0xA5, (byte)FrameCt, 0x29, 0x07, 0xD0).R("up_done")      // LDA frame_ct AND #7 BNE up_done
         .B(0x20).A("slow")                                           // JSR slow (every 8th frame)
         .L("up_done").B(0x60)                                        // RTS
         .L("fill_table").B(0xA6, (byte)Idx)                          // LDX idx
         .B(0xA5, (byte)FrameCt)                                      // LDA frame_ct
         .L("ft_store").B(0x95, (byte)Table)                          // STA table,X   <- reaches hp ($50) when X = $20 in the planted build
         .B(0xE8)                                                     // INX
         .L("ft_limit").B(0xE0, fix ? (byte)0x10 : (byte)0x21)        // CPX #$10 (fixed) / CPX #$21 (the bug)
         .B(0xD0).R("ft_keep")                                        // BNE ft_keep
         .B(0xA2, 0x00)                                               // LDX #0
         .L("ft_keep").B(0x86, (byte)Idx)                             // STX idx
         .B(0x60)                                                     // RTS
         .L("check_hp").B(0xA5, (byte)Hp, 0xD0).R("ch_ok")           // LDA hp BNE ch_ok
         .B(0xA9, 0x01, 0x85, (byte)GameOver)                         // LDA #1 STA game_over
         .L("ch_ok").B(0x60)                                          // RTS
         .L("slow");
        for (int i = 0; i < pad; i++) a.B(0xEA);                      // (the moved build) NOP NOP NOP
        a.B(0xA0, 26)                                                 // LDY #26
         .L("slow_outer").B(0xA2, 0x00)                               // LDX #0
         .L("slow_inner").B(0xCA, 0xD0).R("slow_inner")              // DEX BNE slow_inner (1280 cycles)
         .B(0x88, 0xD0).R("slow_outer")                               // DEY BNE slow_outer (26 x: longer than a frame)
         .B(0x60)                                                     // RTS
         .L("nmi").B(0x48, 0xA9, 0x01, 0x85, (byte)NmiFlag, 0x68, 0x40)   // PHA LDA #1 STA nmi_flag PLA RTI
         .L("irq").B(0x40);                                           // RTI
        var code = a.Build();
        var prg = new byte[0x4000];
        Array.Fill(prg, (byte)0xEA);
        code.CopyTo(prg, 0);
        void Vec(int at, string l) { prg[at] = (byte)a[l]; prg[at + 1] = (byte)(a[l] >> 8); }
        Vec(0x3FFA, "nmi"); Vec(0x3FFC, "reset"); Vec(0x3FFE, "irq");
        var rom = new byte[16 + 0x4000 + 0x2000];
        rom[0] = (byte)'N'; rom[1] = (byte)'E'; rom[2] = (byte)'S'; rom[3] = 0x1A; rom[4] = 1; rom[5] = 1;
        prg.CopyTo(rom, 16);
        var mlb = new StringBuilder();
        foreach (var l in new[] { "reset", "clear", "init", "main_loop", "update", "up_done", "fill_table", "ft_store", "ft_limit", "ft_keep", "check_hp", "ch_ok", "slow", "slow_outer", "slow_inner", "nmi", "irq" })
            mlb.Append($"NesPrgRom:{a[l] - 0xC000:X6}:{l}:\n");
        foreach (var (at, n, name) in new[] { (NmiFlag, 1, "nmi_flag"), (Table, 16, "table"), (Hp, 1, "hp"), (FrameCt, 1, "frame_ct"), (Idx, 1, "idx"), (GameOver, 1, "game_over") })
            mlb.Append($"R:{at:X4}-{at + n - 1:X4}:{name}:\n");
        return (rom, a, mlb.ToString());
    }
}
