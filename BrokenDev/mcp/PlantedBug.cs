using System.Text;
using BrokenNes.Workshop.Debug;

namespace BrokenNes.Mcp;

/// <summary>
/// The D3 "done when" subject: a small NES game (NROM-128) with two planted bugs, and its fixed build (the negative control).
/// <list type="bullet">
/// <item>Bug 1, a variable read before it is written: the start-up clears lives, score, game_over and the NMI flag, but not
/// <c>bonus</c>; every 60 frames <c>add_bonus</c> does <c>bonus = bonus + 1</c> from whatever the RAM powered on with.</item>
/// <item>Bug 2, a wrong branch: <c>lose_life</c> does <c>DEC lives</c> then <c>BEQ alive</c> where it meant <c>BNE alive</c>, so the
/// game is over at the first hit (lives 2) instead of at 0.</item>
/// </list>
/// It also plays a square tone (the sound tools have something to hear) and reads the pad's A into <c>pad_a</c> each frame.
/// </summary>
internal static class PlantedBug
{
    public static (byte[] rom, Asm asm, string mlb) Build(bool fixedBuild)
    {
        var a = new Asm(0xC000);
        a.L("reset").B(0x78, 0xD8, 0xA2, 0xFF, 0x9A)                 // SEI CLD LDX #$FF TXS
         .B(0xA9, 0x00, 0x8D, 0x00, 0x20, 0x8D, 0x01, 0x20)          // LDA #0 STA $2000 STA $2001
         .B(0xA9, 0x01, 0x8D, 0x15, 0x40)                             // LDA #1 STA $4015 (pulse 1 on)
         .B(0xA9, 0xBF, 0x8D, 0x00, 0x40)                             // LDA #$BF STA $4000 (duty 2, constant volume 15)
         .B(0xA9, 0xFD, 0x8D, 0x02, 0x40)                             // LDA #$FD STA $4002
         .B(0xA9, 0x00, 0x8D, 0x03, 0x40)                             // LDA #0 STA $4003 (a tone near 440 Hz)
         .L("init").B(0xA9, 0x03, 0x85, 0x40)                         // LDA #3 STA lives
         .B(0xA9, 0x00, 0x85, 0x41)                                   // LDA #0 STA score
         .B(0x85, 0x43, 0x85, 0x10, 0x85, 0x44);                      // STA game_over STA nmi_flag STA pad_a
        if (fixedBuild) a.B(0x85, 0x42);                              // STA bonus (the fix: the planted build forgets it)
        a.B(0xA9, 0x80, 0x8D, 0x00, 0x20)                             // LDA #$80 STA $2000 (NMI on)
         .L("main_loop").B(0xA5, 0x10, 0xF0).R("main_loop")          // LDA nmi_flag BEQ main_loop (wait for the frame)
         .B(0xA9, 0x00, 0x85, 0x10)                                   // LDA #0 STA nmi_flag
         .B(0x20).A("update")                                         // JSR update
         .B(0x4C).A("main_loop")                                      // JMP main_loop
         .L("update").B(0x20).A("read_pad")                           // JSR read_pad
         .B(0xE6, 0x41)                                               // INC score
         .B(0xA5, 0x41, 0xC9, 0x3C, 0xD0).R("upd_done")               // LDA score CMP #60 BNE upd_done
         .B(0xA9, 0x00, 0x85, 0x41)                                   // LDA #0 STA score
         .B(0x20).A("add_bonus")                                      // JSR add_bonus
         .B(0x20).A("lose_life")                                      // JSR lose_life
         .L("upd_done").B(0x60)                                       // RTS
         .L("add_bonus").B(0xA5, 0x42)                                // LDA bonus   <- bug 1 in the planted build: never written before this
         .B(0x18, 0x69, 0x01, 0x85, 0x42)                             // CLC ADC #1 STA bonus
         .B(0x60)                                                     // RTS
         .L("lose_life").B(0xC6, 0x40)                                // DEC lives
         .L("lose_branch").B(fixedBuild ? (byte)0xD0 : (byte)0xF0).R("alive")   // BNE alive (fixed) / BEQ alive (bug 2)
         .L("set_game_over").B(0xA9, 0x01, 0x85, 0x43)                // LDA #1 STA game_over
         .L("alive").B(0x60)                                          // RTS
         .L("read_pad").B(0xA9, 0x01, 0x8D, 0x16, 0x40, 0xA9, 0x00, 0x8D, 0x16, 0x40)   // strobe the pad
         .B(0xAD, 0x16, 0x40, 0x29, 0x01, 0x85, 0x44)                 // LDA $4016 AND #1 STA pad_a
         .B(0x60)                                                     // RTS
         .L("nmi").B(0x48, 0xA9, 0x01, 0x85, 0x10, 0x68, 0x40)        // PHA LDA #1 STA nmi_flag PLA RTI
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
        foreach (var l in new[] { "reset", "init", "main_loop", "update", "upd_done", "add_bonus", "lose_life", "lose_branch", "set_game_over", "alive", "read_pad", "nmi", "irq" })
            mlb.Append($"NesPrgRom:{a[l] - 0xC000:X6}:{l}:\n");
        mlb.Append("R:0010:nmi_flag:\nR:0040:lives:\nR:0041:score:\nR:0042:bonus:\nR:0043:game_over:\nR:0044:pad_a:\n");
        return (rom, a, mlb.ToString());
    }

    /// <summary>A Master System cartridge that does nothing but run (32 KB of NOPs looping back to 0 at the end, with the header): the Sega path of the headless sessions.</summary>
    public static byte[] SmsIdle()
    {
        var r = new byte[0x8000];
        // $0000: DI; IM 1; then NOPs ... $7FE0: JP $0003
        r[0] = 0xF3; r[1] = 0xED; r[2] = 0x56;
        r[0x7FE0] = 0xC3; r[0x7FE1] = 0x03; r[0x7FE2] = 0x00;
        var tmr = Encoding.ASCII.GetBytes("TMR SEGA");
        tmr.CopyTo(r, 0x7FF0);
        r[0x7FFF] = 0x4C;   // region 4 (export), 32 KB
        return r;
    }
}
