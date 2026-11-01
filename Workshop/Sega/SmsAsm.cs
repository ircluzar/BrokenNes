using System;
using System.Collections.Generic;
using System.Linq;

namespace BrokenNes.Workshop.Sega;

/// <summary>A very small Z80 assembler: bytes, labels, and the relative / absolute jumps that need them. Enough for the hand-written programs of the verifiers and the desktop smoke test (no instruction set is modelled).</summary>
internal sealed class Z80Asm
{
    private readonly List<byte> code = new();
    private readonly Dictionary<string, int> labels = new();
    private readonly List<(int At, string Label, bool Relative)> fixups = new();
    public int Origin { get; }
    public Z80Asm(int origin) { Origin = origin; }

    public int Here => Origin + code.Count;
    public Z80Asm Db(params int[] bytes) { foreach (int b in bytes) code.Add((byte)b); return this; }
    public Z80Asm Bytes(IEnumerable<byte> bytes) { code.AddRange(bytes); return this; }
    public Z80Asm Label(string name) { labels[name] = Here; return this; }

    /// <summary>An instruction with a 16-bit operand that is a label's address (LD HL,label = 21 nn nn: opcode then the label).</summary>
    public Z80Asm Abs(int opcode, string label) { code.Add((byte)opcode); fixups.Add((code.Count, label, false)); code.Add(0); code.Add(0); return this; }
    /// <summary>A relative jump (JR, JR cc, DJNZ) to a label.</summary>
    public Z80Asm Rel(int opcode, string label) { code.Add((byte)opcode); fixups.Add((code.Count, label, true)); code.Add(0); return this; }

    public Z80Asm Jp(string label) => Abs(0xC3, label);
    public Z80Asm Jr(string label) => Rel(0x18, label);
    public Z80Asm JrNz(string label) => Rel(0x20, label);
    public Z80Asm JrZ(string label) => Rel(0x28, label);
    public Z80Asm Djnz(string label) => Rel(0x10, label);
    public Z80Asm LdHl(string label) => Abs(0x21, label);
    public Z80Asm OutN(int port) => Db(0xD3, port);
    public Z80Asm InN(int port) => Db(0xDB, port);
    public Z80Asm LdA(int n) => Db(0x3E, n);
    /// <summary>LD A,value; OUT (port),A</summary>
    public Z80Asm OutImm(int port, int value) => LdA(value).OutN(port);

    public byte[] Assemble()
    {
        var bytes = code.ToArray();
        foreach (var (at, label, relative) in fixups)
        {
            int target = labels[label];
            if (relative)
            {
                int off = target - (Origin + at + 1);
                if (off < -128 || off > 127) throw new InvalidOperationException($"relative jump to {label} is {off} bytes");
                bytes[at] = (byte)off;
            }
            else { bytes[at] = (byte)(target & 0xFF); bytes[at + 1] = (byte)(target >> 8); }
        }
        return bytes;
    }
}

/// <summary>The programs of the desktop smoke test: real Master System / Game Gear programs small enough to write by hand, with something to see and a way to see the pad and the battery.</summary>
internal static class SmokeProgs
{
    private static readonly int[] Regs = { 0x04, 0x80, 0x80, 0x81, 0xFF, 0x82, 0xFF, 0x83, 0xFF, 0x84, 0xFF, 0x85, 0xFF, 0x86, 0x00, 0x87, 0x00, 0x88, 0x00, 0x89, 0xFF, 0x8A };

    private static byte[] Palette(bool gg)
    {
        // colours 0..3: black, blue, green, red. Master System: one byte --BBGGRR; Game Gear: two bytes GGGGRRRR, ----BBBB
        if (!gg) { var p = new byte[32]; p[1] = 0x30; p[2] = 0x0C; p[3] = 0x03; return p; }
        var q = new byte[64];
        q[2] = 0x00; q[3] = 0x0F;       // entry 1: blue
        q[4] = 0xF0; q[5] = 0x00;       // entry 2: green
        q[6] = 0x0F; q[7] = 0x00;       // entry 3: red
        return q;
    }

    private static byte[] Tiles()
    {
        var t = new byte[128];                    // tile 0 is blank; tile n is solid colour n (bit planes 0 and 1)
        for (int tile = 1; tile <= 3; tile++)
            for (int row = 0; row < 8; row++)
            {
                t[tile * 32 + row * 4 + 0] = (byte)((tile & 1) != 0 ? 0xFF : 0);
                t[tile * 32 + row * 4 + 1] = (byte)((tile & 2) != 0 ? 0xFF : 0);
            }
        return t;
    }

    /// <summary>The program of a smoke ROM as hex, from $0100 to its last non-zero byte: what the desktop smoke script carries (it rebuilds the 32 KB image around it: a JMP $0100 at $0000, zeros elsewhere).</summary>
    public static string Hex(byte[] rom)
    {
        int end = rom.Length; while (end > 0x100 && rom[end - 1] == 0) end--;
        return Convert.ToHexString(rom.AsSpan(0x100, end - 0x100));
    }

    /// <summary>Mode 4 screen of diagonal stripes in three colours; holding Right scrolls it one pixel per frame (so the picture changes while the pad is held).</summary>
    public static byte[] Picture(bool gg)
    {
        var a = new Z80Asm(0x100);
        a.Db(0xF3, 0xED, 0x56, 0x31, 0xF0, 0xDF);                                              // DI; IM 1; LD SP,$DFF0
        a.Db(0x0E, 0xBF, 0x06, Regs.Length).LdHl("regs").Db(0xED, 0xB3);                       // LD C,$BF; LD B,n; LD HL,regs; OTIR
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0xC0);                                               // CRAM address 0
        a.Db(0x0E, 0xBE, 0x06, gg ? 64 : 32).LdHl("pal").Db(0xED, 0xB3);                       // LD C,$BE; LD B,n; LD HL,pal; OTIR
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x40);                                               // VRAM address 0
        a.Db(0x06, 128).LdHl("tiles").Db(0xED, 0xB3);                                          // LD B,128; LD HL,tiles; OTIR (C is still $BE)
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x78);                                               // VRAM address $3800 (the name table)
        a.Db(0x11, 0x00, 0x03, 0x0E, 0x01);                                                    // LD DE,768; LD C,1
        a.Label("fill").Db(0x79).OutN(0xBE).Db(0xAF).OutN(0xBE).Db(0x0C, 0x79, 0xFE, 0x04).JrNz("nowrap").Db(0x0E, 0x01);
        a.Label("nowrap").Db(0x1B, 0x7A, 0xB3).JrNz("fill");                                   // DEC DE; LD A,D; OR E; JR NZ,fill
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x7F).OutImm(0xBE, 0xD0);                            // sprite table: $D0 ends the list
        a.OutImm(0xBF, 0xC0).OutImm(0xBF, 0x81);                                               // R1 = $C0: display on
        a.Label("main").InN(0x7E).Db(0xFE, 0xC0).JrNz("main");                                 // wait for the V counter to reach 192
        a.InN(0xDC).Db(0xE6, 0x08).JrNz("idle");                                               // IN A,($DC); AND 8; Right is bit 3 (low when pressed)
        a.Db(0x3A, 0x00, 0xC1, 0x3C, 0x32, 0x00, 0xC1).OutN(0xBF).OutImm(0xBF, 0x88);         // scroll++ ; R8 = scroll
        a.Label("idle").InN(0x7E).Db(0xFE, 0xC0).JrZ("idle");                                  // wait for it to move on
        a.Jr("main");
        a.Label("regs").Bytes(Regs.Select(x => (byte)x));
        a.Label("pal").Bytes(Palette(gg));
        a.Label("tiles").Bytes(Tiles());
        return SmsProg.Rom(a.Assemble(), null, null, 0x100);
    }

    /// <summary>Maps cartridge RAM in. If its first two bytes hold $5A $A5 it counts a boot in the third and shows green; otherwise it writes the marker, sets the count to 1 and shows red. The picture is the backdrop colour (display off).</summary>
    public static byte[] Battery(bool gg)
    {
        var a = new Z80Asm(0x100);
        a.Db(0xF3, 0xED, 0x56, 0x31, 0xF0, 0xDF);                                              // DI; IM 1; SP
        a.Db(0x3E, 0x08, 0x32, 0xFC, 0xFF);                                                    // LD A,8; LD ($FFFC),A: cartridge RAM into $8000
        a.Db(0x3A, 0x00, 0x80, 0xFE, 0x5A).JrNz("first");                                      // LD A,($8000); CP $5A
        a.Db(0x3A, 0x01, 0x80, 0xFE, 0xA5).JrNz("first");
        a.Db(0x3A, 0x02, 0x80, 0x3C, 0x32, 0x02, 0x80);                                        // count++
        a.Db(0x16, 0x01).Jr("show");                                                           // LD D,1 (found)
        a.Label("first").Db(0x3E, 0x5A, 0x32, 0x00, 0x80, 0x3E, 0xA5, 0x32, 0x01, 0x80, 0x3E, 0x01, 0x32, 0x02, 0x80, 0x16, 0x00);
        a.Label("show").OutImm(0xBF, 0x04).OutImm(0xBF, 0x80);                                 // R0 = 4
        a.OutImm(0xBF, 0x80).OutImm(0xBF, 0x81);                                               // R1 = $80: display off, the whole picture is the backdrop
        a.OutImm(0xBF, gg ? 0x20 : 0x10).OutImm(0xBF, 0xC0);                                   // CRAM address of entry 16 (a Game Gear entry is two bytes)
        if (gg) a.OutImm(0xBE, 0x0F).OutImm(0xBE, 0x00).OutImm(0xBE, 0xF0).OutImm(0xBE, 0x00); // entry 16 red, entry 17 green (two bytes each)
        else a.OutImm(0xBE, 0x03).OutImm(0xBE, 0x0C);
        a.Db(0x7A).OutN(0xBF).OutImm(0xBF, 0x87);                                              // LD A,D; OUT ($BF),A; R7 = D: backdrop colour 16 + D
        a.Label("idle").Jr("idle");
        return SmsProg.Rom(a.Assemble(), null, null, 0x100);
    }
}
