using System;
using System.Text;
using BrokenNes.Workshop.Debug;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>
/// T4's search test game (docs/projects/brokendev-timeflow-plan.md, "T4 as built"): a small NES game (NROM-128, CHR ROM with one solid tile)
/// with an enemy sprite and a planted "enemy alive and not visible" bug, and a planted event and a near miss for every condition kind. With
/// <c>ct</c> the frame counter after its increment (one update a frame, after the NMI):
/// <list type="bullet">
/// <item>The enemy is alive while <c>(ct &amp; $40) == 0</c> (64 frames alive, 64 dead) and drawn at line 100 (red) while alive.</item>
/// <item><b>The planted bug:</b> alive and <c>(ct &amp; $3F)</c> in $20-$27: <c>hide_enemy</c> runs: the sprite goes off screen ($F0) while the
/// enemy is still alive, <c>hide_count</c> counts it, <c>bug_mark</c> ($0420) is written $5A and <c>secret</c> ($0410) is read.</item>
/// <item>The near misses: dead, the sprite is off screen too (<c>hide_enemy_dead</c>, a neighbouring routine), and in the same window of a dead
/// period <c>bug_mark</c> is written $A5 (the same address, another value).</item>
/// <item><c>same_byte</c> ($0530) is written 7 every 16th frame (<c>(ct &amp; 15) == 0</c>): writes that never change it.</item>
/// <item>Pulse 1 plays while <c>(ct &amp; $7F)</c> is $10-$1F (keyed on at $10, volume 0 at $20).</item>
/// <item>At <c>ct == $50</c> (dead) it reads $0300, which nothing wrote: the D4 check <c>nes.ram-uninit</c> (first seen once).</item>
/// <item>The reset clears pages 0, 2, 4 and 5 (frame 0 writes every byte there, and reads each first: STA abs,X's dummy read).</item>
/// <item><c>never_called</c> and <c>never</c> ($0600, a page nothing touches) exist and are never run / written: conditions that never hold.</item>
/// </list>
/// </summary>
internal static class TimeflowSearchRoms
{
    public const ushort NmiFlag = 0x10, FrameCt = 0x20, Alive = 0x21, EnemyY = 0x22, HideCount = 0x23, Never = 0x0600,
                        Secret = 0x0410, BugMark = 0x0420, SameByte = 0x0530, Uninit = 0x0300;

    public static (byte[] rom, Asm asm, string mlb) EnemyGame()
    {
        var a = new Asm(0xC000);
        byte L(ushort x) => (byte)x; byte H(ushort x) => (byte)(x >> 8);
        a.L("reset").B(0x78, 0xD8, 0xA2, 0xFF, 0x9A)                         // SEI CLD LDX #$FF TXS
         .B(0xA9, 0x00, 0x8D, 0x00, 0x20, 0x8D, 0x01, 0x20)                  // LDA #0 STA $2000 STA $2001
         .B(0xA9, 0x40, 0x8D, 0x17, 0x40)                                     // LDA #$40 STA $4017 (no frame IRQ)
         .B(0xA9, 0x01, 0x8D, 0x15, 0x40)                                     // LDA #1 STA $4015 (pulse 1 enabled)
         .B(0xA9, 0x00, 0xA2, 0x00).L("clear").B(0x95, 0x00, 0x9D, 0x00, 0x02, 0x9D, 0x00, 0x04, 0x9D, 0x00, 0x05, 0xE8, 0xD0).R("clear")   // zero page, OAM page, pages 4 and 5
         .L("vw1").B(0x2C, 0x02, 0x20, 0x10).R("vw1")                         // BIT $2002 BPL vw1
         .L("vw2").B(0x2C, 0x02, 0x20, 0x10).R("vw2")
         .B(0xAD, 0x02, 0x20)                                                 // LDA $2002 (the address latch)
         .B(0xA9, 0x3F, 0x8D, 0x06, 0x20, 0xA9, 0x00, 0x8D, 0x06, 0x20)      // $2006 = $3F00
         .B(0xA2, 0x00).L("pal_loop").B(0xBD).A("pal").B(0x8D, 0x07, 0x20, 0xE8, 0xE0, 0x20, 0xD0).R("pal_loop")
         .B(0xA9, 0xF0, 0x85, L(EnemyY))                                      // enemy_y = $F0
         .B(0xA9, 0x01, 0x8D, 0x01, 0x02, 0xA9, 0x00, 0x8D, 0x02, 0x02, 0xA9, 0x78, 0x8D, 0x03, 0x02)   // sprite 0: tile 1, palette 0, x 120
         .B(0xA2, 0x04, 0xA9, 0xF0).L("hide_rest").B(0x9D, 0x00, 0x02, 0xE8, 0xD0).R("hide_rest")   // the other sprites off screen
         .B(0xA9, 0xF0, 0x8D, 0x00, 0x02)
         .B(0xA9, 0x80, 0x8D, 0x00, 0x20)                                     // NMI on
         .B(0xA9, 0x10, 0x8D, 0x01, 0x20)                                     // sprites on
         .L("main_loop").B(0xA5, L(NmiFlag), 0xF0).R("main_loop")
         .B(0xA9, 0x00, 0x85, L(NmiFlag), 0x20).A("update").B(0x4C).A("main_loop")
         .L("update").B(0xE6, L(FrameCt))                                     // INC frame_ct
         .B(0xA5, L(FrameCt), 0x29, 0x40, 0xD0).R("dead")                     // (ct & $40) != 0: dead
         .B(0xA9, 0x01, 0x85, L(Alive))                                       // alive = 1
         .B(0xA5, L(FrameCt), 0x29, 0x3F, 0xC9, 0x20, 0x90).R("visible").B(0xC9, 0x28, 0xB0).R("visible")
         .B(0x20).A("hide_enemy").B(0x4C).A("after")                          // THE BUG: alive, hidden
         .L("visible").B(0xA9, 0x64, 0x85, L(EnemyY), 0x4C).A("after")        // y = 100
         .L("dead").B(0xA9, 0x00, 0x85, L(Alive), 0x20).A("hide_enemy_dead")
         .B(0xA5, L(FrameCt), 0x29, 0x3F, 0xC9, 0x20, 0x90).R("no_near").B(0xC9, 0x28, 0xB0).R("no_near")
         .B(0xA9, 0xA5, 0x8D, L(BugMark), H(BugMark))                         // the near miss: bug_mark = $A5
         .L("no_near").B(0xA5, L(FrameCt), 0xC9, 0x50, 0xD0).R("after")
         .B(0xAD, L(Uninit), H(Uninit))                                       // LDA $0300: read before it was ever written (D4)
         .L("after").B(0xA5, L(EnemyY), 0x8D, 0x00, 0x02)                     // OAM Y = enemy_y
         .B(0xA5, L(FrameCt), 0x29, 0x0F, 0xD0).R("snd")
         .B(0xA9, 0x07, 0x8D, L(SameByte), H(SameByte))                       // same_byte = 7 (never changes it)
         .L("snd").B(0xA5, L(FrameCt), 0x29, 0x7F, 0xC9, 0x10, 0xD0).R("snd_off")
         .B(0xA9, 0xBF, 0x8D, 0x00, 0x40, 0xA9, 0xFD, 0x8D, 0x02, 0x40, 0xA9, 0x08, 0x8D, 0x03, 0x40)   // pulse 1: duty 2, volume 15, ~440 Hz
         .L("snd_off").B(0xA5, L(FrameCt), 0x29, 0x7F, 0xC9, 0x20, 0xD0).R("upd_done")
         .B(0xA9, 0xB0, 0x8D, 0x00, 0x40)                                     // volume 0
         .L("upd_done").B(0x60)
         .L("hide_enemy").B(0xA9, 0xF0, 0x85, L(EnemyY), 0xE6, L(HideCount))
         .B(0xA9, 0x5A, 0x8D, L(BugMark), H(BugMark))                         // bug_mark = $5A
         .B(0xAD, L(Secret), H(Secret))                                       // LDA secret
         .B(0x60)
         .L("hide_enemy_dead").B(0xA9, 0xF0, 0x85, L(EnemyY), 0x60)
         .L("never_called").B(0xA9, 0x99, 0x8D, L(Never), H(Never), 0x60)
         .L("nmi").B(0x48, 0xA9, 0x02, 0x8D, 0x14, 0x40, 0xA9, 0x01, 0x85, L(NmiFlag), 0x68, 0x40)   // OAM DMA, nmi_flag
         .L("irq").B(0x40)
         .L("pal");
        for (int i = 0; i < 32; i++) a.B(i == 0x11 ? (byte)0x16 : (byte)0x0F);   // all black, sprite palette 0 colour 1 red ($16)
        var code = a.Build();
        var prg = new byte[0x4000];
        Array.Fill(prg, (byte)0xEA);
        code.CopyTo(prg, 0);
        void Vec(int at, string l) { prg[at] = (byte)a[l]; prg[at + 1] = (byte)(a[l] >> 8); }
        Vec(0x3FFA, "nmi"); Vec(0x3FFC, "reset"); Vec(0x3FFE, "irq");
        var rom = new byte[16 + 0x4000 + 0x2000];
        rom[0] = (byte)'N'; rom[1] = (byte)'E'; rom[2] = (byte)'S'; rom[3] = 0x1A; rom[4] = 1; rom[5] = 1;
        prg.CopyTo(rom, 16);
        for (int i = 0; i < 8; i++) rom[16 + 0x4000 + 16 + i] = 0xFF;          // CHR tile 1: plane 0 solid (colour 1)
        var mlb = new StringBuilder();
        foreach (var l in new[] { "reset", "main_loop", "update", "visible", "dead", "no_near", "after", "snd", "snd_off", "upd_done", "hide_enemy", "hide_enemy_dead", "never_called", "nmi", "irq", "pal" })
            mlb.Append($"NesPrgRom:{a[l] - 0xC000:X6}:{l}:\n");
        foreach (var (at, name) in new[] { (NmiFlag, "nmi_flag"), (FrameCt, "frame_ct"), (Alive, "enemy_alive"), (EnemyY, "enemy_y"), (HideCount, "hide_count"), (Never, "never"),
                                           (Secret, "secret"), (BugMark, "bug_mark"), (SameByte, "same_byte") })
            mlb.Append($"R:{at:X4}:{name}:\n");
        return (rom, a, mlb.ToString());
    }
}
