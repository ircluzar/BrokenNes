using System;
using System.Collections.Generic;
using System.Text;

namespace BrokenNes.Workshop;

/// <summary>
/// A SNES program written for the state tests and the app smoke tests (no game data): a 32 KB LoROM whose 65816 code waits for vertical blanking every frame, adds one to a 16-bit counter in work RAM ($0010/$0011) for every frame
/// the pad's Right button is held, and writes the counter as the backdrop colour (CGRAM entry 0). With no button held the picture is still; with Right held it changes every frame; after a state load it is the counter's saved value, so
/// "the colour comes back" and "the counter resumes" are both visible, from the framebuffer alone (the apps) and from work RAM (the Workshop). The code is the hex in <see cref="CounterHex"/>, which
/// <c>UAT/entrypoints/snes-state-smoke.ps1</c> carries and <c>--snes-state-test --only smoke</c> compares with what this class builds.
/// </summary>
internal static class SnesProgs
{
    /// <summary>The program: a few dozen bytes of 65816 code at $8000 (the reset vector points there).</summary>
    public static byte[] CounterCode()
    {
        var code = new List<byte>();
        int Pc() => 0x8000 + code.Count;
        void B(params byte[] b) => code.AddRange(b);
        void Branch(byte op, int target) { code.Add(op); code.Add((byte)(target - (Pc() + 1))); }
        B(0x78);                                  // SEI
        B(0xA9, 0x01, 0x8D, 0x00, 0x42);          // LDA #$01 ; STA $4200   (auto joypad read on, NMI off)
        B(0xA9, 0x0F, 0x8D, 0x00, 0x21);          // LDA #$0F ; STA $2100   (screen on, full brightness)
        B(0xA9, 0x00, 0x85, 0x10, 0x85, 0x11);    // LDA #0 ; STA $10 ; STA $11   (counter = 0)
        int top = Pc();
        B(0xAD, 0x12, 0x42); Branch(0x30, top);   // loop: LDA $4212 ; BMI loop        (wait for the end of vblank)
        int start = Pc();
        B(0xAD, 0x12, 0x42); Branch(0x10, start); // LDA $4212 ; BPL                   (wait for vblank to start)
        int busy = Pc();
        B(0xAD, 0x12, 0x42, 0x29, 0x01); Branch(0xD0, busy);   // auto-read busy: LDA $4212 ; AND #1 ; BNE
        B(0xAD, 0x19, 0x42, 0x29, 0x01);          // LDA $4219 ; AND #$01   (pad 1: Right)
        int beq = code.Count; B(0xF0, 0x00);      // BEQ skip
        B(0xE6, 0x10);                            // INC $10
        int bne = code.Count; B(0xD0, 0x00);      // BNE skip2
        B(0xE6, 0x11);                            // INC $11
        code[bne + 1] = (byte)(Pc() - (0x8000 + bne + 2));
        code[beq + 1] = (byte)(Pc() - (0x8000 + beq + 2));
        B(0xA9, 0x00, 0x8D, 0x21, 0x21);          // LDA #0 ; STA $2121   (CGRAM address 0)
        B(0xA5, 0x10, 0x8D, 0x22, 0x21);          // LDA $10 ; STA $2122  (colour, low byte)
        B(0xA5, 0x11, 0x29, 0x7F, 0x8D, 0x22, 0x21);   // LDA $11 ; AND #$7F ; STA $2122  (colour, high byte)
        B(0x4C, (byte)top, (byte)(top >> 8));     // JMP top
        return code.ToArray();
    }

    public static string CounterHex => Convert.ToHexString(CounterCode());

    /// <summary>The cartridge: LoROM, 32 KB, the header with a valid checksum and the program at $8000.</summary>
    public static byte[] CounterRom() => Rom(CounterCode());

    public static byte[] Rom(byte[] code)
    {
        var rom = new byte[0x8000];
        Array.Copy(code, 0, rom, 0, code.Length);
        const int h = 0x7FC0;
        var title = Encoding.ASCII.GetBytes("BROKENNES SNES SMOKE ");
        Array.Copy(title, 0, rom, h, 21);
        rom[h + 0x15] = 0x20;                    // LoROM, slow
        rom[h + 0x16] = 0x00;                    // ROM only
        rom[h + 0x17] = 0x05;                    // 32 KB
        rom[h + 0x18] = 0x00;                    // no RAM
        rom[h + 0x19] = 0x01;                    // North America
        rom[h + 0x1A] = 0x33;
        rom[h + 0x3C] = 0x00; rom[h + 0x3D] = 0x80;   // reset vector $8000 (emulation mode)
        rom[h + 0x2C] = 0x00; rom[h + 0x2D] = 0x80;   // (the native vectors point at the same code; they are not used)
        // checksum: the sum of every byte, with the checksum field read as $FFFF + $0000
        rom[h + 0x1C] = 0xFF; rom[h + 0x1D] = 0xFF; rom[h + 0x1E] = 0x00; rom[h + 0x1F] = 0x00;
        int sum = 0; foreach (byte b in rom) sum += b;
        ushort checksum = (ushort)sum;
        rom[h + 0x1E] = (byte)checksum; rom[h + 0x1F] = (byte)(checksum >> 8);
        rom[h + 0x1C] = (byte)~checksum; rom[h + 0x1D] = (byte)(~checksum >> 8);
        return rom;
    }
}
