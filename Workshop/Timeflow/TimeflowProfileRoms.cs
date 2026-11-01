using System;
using System.Text;
using BrokenNes.Workshop.Debug;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>
/// T3's planted bug (docs/projects/brokendev-timeflow-plan.md, "T3 as built"): a small NES game (NROM-128) whose <c>hp</c> gets corrupted by a
/// write that has nothing to do with it, and its fixed build (the negative control).
/// <list type="bullet">
/// <item><c>fill_table</c> writes the frame counter into a 16-byte ring <c>table</c> ($0030-$003F) at <c>idx</c>, then <c>INX; CPX #$21</c> where
/// it meant <c>CPX #$10</c>: <c>idx</c> runs to $20, and at $20 the <c>STA table,X</c> lands on $0050, which is <c>hp</c>. The game set
/// <c>hp</c> to 5 at the start and never writes it again on purpose; the 33rd frame's update overwrites it with the frame counter ($21).</item>
/// <item>The fixed build compares with <c>#$10</c>: no write ever reaches <c>hp</c> after the start.</item>
/// </list>
/// Each update is bracketed by two debug-port markers ("fill", "done"): the profile's "between" query has markers to find.
/// </summary>
internal static class TimeflowProfileRoms
{
    public const ushort Hp = 0x50, FrameCt = 0x51, Idx = 0x52, GameOver = 0x53, NmiFlag = 0x10, Table = 0x30;

    public static (byte[] rom, Asm asm, string mlb) Corrupting(bool fixedBuild)
    {
        var a = new Asm(0xC000);
        void Marker(string text)
        {
            foreach (char ch in text) a.B(0xA9, (byte)ch, 0x8D, 0x1C, 0x40);   // LDA #'c' STA $401C (the port's DATA)
            a.B(0xA9, 0x03, 0x8D, 0x1D, 0x40);                                  // LDA #3 STA $401D (COMMAND: marker)
        }
        a.L("reset").B(0x78, 0xD8, 0xA2, 0xFF, 0x9A)                 // SEI CLD LDX #$FF TXS
         .B(0xA9, 0x00, 0x8D, 0x00, 0x20, 0x8D, 0x01, 0x20)          // LDA #0 STA $2000 STA $2001
         .B(0xA2, 0x00).L("clear").B(0x95, 0x00, 0xE8, 0xD0).R("clear")   // LDX #0 clear: STA $00,X INX BNE clear
         .L("init").B(0xA9, 0x05, 0x85, (byte)Hp)                    // LDA #5 STA hp
         .B(0xA9, 0x00, 0x85, (byte)Idx, 0x85, (byte)FrameCt, 0x85, (byte)GameOver)   // LDA #0 STA idx STA frame_ct STA game_over
         .B(0xA9, 0x80, 0x8D, 0x00, 0x20)                             // LDA #$80 STA $2000 (NMI on)
         .L("main_loop").B(0xA5, (byte)NmiFlag, 0xF0).R("main_loop") // LDA nmi_flag BEQ main_loop
         .B(0xA9, 0x00, 0x85, (byte)NmiFlag)                          // LDA #0 STA nmi_flag
         .B(0x20).A("update")                                         // JSR update
         .B(0x4C).A("main_loop")                                      // JMP main_loop
         .L("update").B(0xE6, (byte)FrameCt);                         // INC frame_ct
        Marker("fill");
        a.B(0x20).A("fill_table");                                    // JSR fill_table
        Marker("done");
        a.B(0x20).A("check_hp")                                       // JSR check_hp
         .B(0x60)                                                     // RTS
         .L("fill_table").B(0xA6, (byte)Idx)                          // LDX idx
         .B(0xA5, (byte)FrameCt)                                      // LDA frame_ct
         .L("ft_store").B(0x95, (byte)Table)                          // STA table,X   <- reaches hp ($50) when X = $20 in the planted build
         .B(0xE8)                                                     // INX
         .L("ft_limit").B(0xE0, fixedBuild ? (byte)0x10 : (byte)0x21) // CPX #$10 (fixed) / CPX #$21 (the bug)
         .B(0xD0).R("ft_keep")                                        // BNE ft_keep
         .B(0xA2, 0x00)                                               // LDX #0
         .L("ft_keep").B(0x86, (byte)Idx)                             // STX idx
         .B(0x60)                                                     // RTS
         .L("check_hp").B(0xA5, (byte)Hp, 0xD0).R("ch_ok")           // LDA hp BNE ch_ok
         .B(0xA9, 0x01, 0x85, (byte)GameOver)                         // LDA #1 STA game_over
         .L("ch_ok").B(0x60)                                          // RTS
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
        foreach (var l in new[] { "reset", "clear", "init", "main_loop", "update", "fill_table", "ft_store", "ft_limit", "ft_keep", "check_hp", "ch_ok", "nmi", "irq" })
            mlb.Append($"NesPrgRom:{a[l] - 0xC000:X6}:{l}:\n");
        foreach (var (at, n, name) in new[] { (NmiFlag, 1, "nmi_flag"), (Table, 16, "table"), (Hp, 1, "hp"), (FrameCt, 1, "frame_ct"), (Idx, 1, "idx"), (GameOver, 1, "game_over") })
            mlb.Append($"R:{at:X4}-{at + n - 1:X4}:{name}:\n");
        return (rom, a, mlb.ToString());
    }
}
