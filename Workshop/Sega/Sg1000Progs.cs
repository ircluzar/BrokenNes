using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// Hand-assembled SG-1000 programs for the TMS9918A modes: no SG-1000 game is in the libraries, so each of the four modes (Graphics I, Graphics II, Text, Multicolor) is shown with
/// pseudo-random pattern, colour and name data and a table of 20 sprites (8x8, or 16x16 magnified), with the display on and the VDP's status read every frame into work RAM ($C000: the last status read,
/// $C001: the flags seen so far). The oracle (Mesen 2.1.1) runs the same bytes as a .sg file; the picture and the RAM must be equal. <c>--smstest --write-sg-programs dir</c> writes them.
/// </summary>
internal static class Sg1000Progs
{
    public enum Mode { Graphics1, Graphics2, Text, Multicolor }

    /// <summary>A reproducible byte stream (an LFSR), the same bytes every build.</summary>
    private static byte[] Noise(int seed, int count)
    {
        var b = new byte[count];
        uint x = (uint)seed * 2654435761u | 1;
        for (int i = 0; i < count; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; b[i] = (byte)(x >> 11); }
        return b;
    }

    /// <summary>Debug switch for building variants (what the oracle needs to show a Graphics II picture): 0 = as the suite, 1 = no sprites, 2 = regular tables, 3 = R7 backdrop 1.</summary>
    public static int Variant;

    public static byte[] Build(Mode mode, bool bigSprites)
    {
        int r1 = 0xC0 | (mode == Mode.Text ? 0x10 : 0) | (mode == Mode.Multicolor ? 0x08 : 0) | (bigSprites ? 0x03 : 0);
        int r0 = mode == Mode.Graphics2 ? 0x02 : 0x00;
        // R2 name table $3800, R5 sprite attributes $3B00, R6 sprite patterns $1800 ($0800 x 3), R7 text colours / backdrop
        int[] regs = mode switch
        {
            Mode.Graphics1 => new[] { r0, 0x80, 0x0E, 0x80, 0x00, 0x76, 0x03, 0x04 },
            Mode.Graphics2 => new[] { r0, 0x80, 0x0E, 0xFF, 0x03, 0x76, 0x03, 0x05 },
            Mode.Text => new[] { r0, 0x80, 0x0E, 0x00, 0x00, 0x76, 0x03, 0xF4 },
            _ => new[] { r0, 0x80, 0x0E, 0x00, 0x00, 0x76, 0x03, 0x03 },
        };
        // VRAM contents: (address, bytes)
        var blocks = new List<(int Addr, byte[] Data)>();
        blocks.Add((0x0000, Noise(1, 0x1800)));                                   // pattern generator ($0000-$17FF: Graphics II uses all three banks; the others use $0000-$07FF)
        if (mode == Mode.Graphics2) blocks.Add((0x2000, Noise(2, 0x1800)));       // colour table, 8 bytes per tile row; colours of the low nibble 1..15
        if (mode == Mode.Graphics1) blocks.Add((0x2000, Noise(3, 32)));           // colour table: one byte per 8 patterns
        blocks.Add((0x3800, Noise(4, mode == Mode.Text ? 960 : 768)));            // name table
        blocks.Add((0x1800, Noise(5, 0x800)));                                    // sprite patterns
        // sprite attribute table: 20 sprites (Y, X, pattern, colour), then the end marker $D0; some share lines (more than four on a line sets the fifth-sprite flag)
        var sat = new List<byte>();
        var rnd = Noise(6, 20 * 4);
        for (int i = 0; i < 20; i++)
        {
            sat.Add((byte)(8 + (rnd[4 * i] % 150)));                              // Y
            sat.Add((byte)(rnd[4 * i + 1] & 0xFF));                               // X
            sat.Add((byte)(rnd[4 * i + 2] & (bigSprites ? 0xFC : 0xFF)));         // pattern
            sat.Add((byte)((rnd[4 * i + 3] & 0x0F) | (i % 5 == 0 ? 0x80 : 0)));   // colour, and the early-clock bit on every fifth
        }
        sat.Add(0xD0);
        blocks.Add((0x3B00, sat.ToArray()));
        // colours must not all be transparent: force the low nibble of the colour data to 1..15 where it is a colour table
        if (mode == Mode.Graphics2) { var d = blocks.First(x => x.Addr == 0x2000).Data; for (int i = 0; i < d.Length; i++) { if ((d[i] & 0x0F) == 0) d[i] |= 0x0A; if ((d[i] >> 4) == 0) d[i] |= 0x40; } }
        if (mode == Mode.Graphics1) { var d = blocks.First(x => x.Addr == 0x2000).Data; for (int i = 0; i < d.Length; i++) { if ((d[i] & 0x0F) == 0) d[i] |= 0x01; if ((d[i] >> 4) == 0) d[i] |= 0x50; } }

        if (Variant == 1) { var sat1 = blocks.First(x => x.Addr == 0x3B00).Data; sat1[0] = 0xD0; }
        if (Variant == 2)
        {
            // regular data: pattern = 8 bytes of $AA/$55 stripes, colour = one fixed pair per row, names 0..255 in each third
            var pat = blocks.First(x => x.Addr == 0x0000).Data; for (int i = 0; i < pat.Length; i++) pat[i] = (byte)((i & 1) != 0 ? 0xAA : 0x55);
            if (mode == Mode.Graphics2) { var col = blocks.First(x => x.Addr == 0x2000).Data; for (int i = 0; i < col.Length; i++) col[i] = 0x4F; }
            var nm = blocks.First(x => x.Addr == 0x3800).Data; for (int i = 0; i < nm.Length; i++) nm[i] = (byte)(i & 0xFF);
        }
        if (Variant == 3) regs[7] = 0x01;
        var a = new Z80Asm(0x100);
        a.Db(0xF3, 0xED, 0x56, 0x31, 0xF0, 0xDF);                                    // DI; IM 1; LD SP,$DFF0 (SP is $C3F0 on the real thing: the RAM is mirrored; this is inside it either way)
        foreach (var (v, i) in regs.Select((v, i) => (v, i))) a.OutImm(0xBF, v).OutImm(0xBF, 0x80 | i);
        for (int k = 0; k < blocks.Count; k++)
        {
            var (addr, data) = blocks[k];
            a.OutImm(0xBF, addr & 0xFF).OutImm(0xBF, 0x40 | (addr >> 8));            // VRAM write address
            a.LdHl("blk" + k).Db(0x01, data.Length & 0xFF, data.Length >> 8).Abs(0xCD, "vcopy");
        }
        a.OutImm(0xBF, r1).OutImm(0xBF, 0x81);                                       // R1: display on (and the mode, sprite size)
        // poll the status port (the TMS9918A has no V counter): every read is OR-ed into $C001; when the frame flag (bit 7) is seen, the status is also kept in $C000
        a.Label("frame").InN(0xBF).Db(0x4F, 0x3A, 0x01, 0xC0, 0xB1, 0x32, 0x01, 0xC0);   // IN A,($BF); LD C,A; LD A,($C001); OR C; LD ($C001),A
        a.Db(0xCB, 0x79).JrZ("frame");                                               // BIT 7,C; JR Z,frame
        a.Db(0x79, 0x32, 0x00, 0xC0).Jr("frame");                                    // LD A,C; LD ($C000),A; JR frame
        a.Label("vcopy");                                                            // HL = source, BC = count: copy to the data port
        a.Db(0x7E).OutN(0xBE).Db(0x23, 0x0B, 0x78, 0xB1).JrNz("vcopy").Db(0xC9);
        for (int k = 0; k < blocks.Count; k++) a.Label("blk" + k).Bytes(blocks[k].Data);
        return SmsProg.Rom(a.Assemble(), null, null, 0x100);
    }

    /// <summary>A Graphics I picture of the sixteen colours as blocks (tile row 0, one 8x8 block each), to read the oracle's palette off its picture.</summary>
    public static byte[] PaletteProbe()
    {
        var pat = new byte[128 * 8];                            // tile 8 c: solid
        for (int c = 0; c < 16; c++) for (int r = 0; r < 8; r++) pat[8 * 8 * c + r] = 0xFF;
        var colour = new byte[32]; for (int i = 0; i < 16; i++) colour[i] = (byte)(i << 4 | 1);   // foreground colour i, background black
        var names = new byte[768]; for (int c = 0; c < 16; c++) names[c] = (byte)(8 * c);
        var sat = new byte[] { 0xD0 };
        return Simple(0x00, 0xC0, new[] { (0x0000, pat), (0x2000, colour), (0x3800, names), (0x3B00, sat) }, 0x01);
    }

    /// <summary>A Graphics I picture with <paramref name="count"/> sprites spaced 16 lines apart (never five on a line) and then the end marker, the status read at the end of every frame into $C000.</summary>
    public static byte[] StatusProbe(int count)
    {
        var pat = new byte[2048]; Array.Fill(pat, (byte)0xFF);
        var sat = new List<byte>();
        for (int i = 0; i < count; i++) { sat.Add((byte)(8 + 16 * (i % 10))); sat.Add((byte)(16 * i)); sat.Add(0); sat.Add(0x0F); }
        sat.Add(0xD0);
        return Simple(0x00, 0xC0, new[] { (0x0000, pat), (0x1800, pat), (0x3800, new byte[768]), (0x2000, new byte[32]), (0x3B00, sat.ToArray()) }, 0x04);
    }

    private static byte[] Simple(int r0, int r1, (int Addr, byte[] Data)[] blocks, int r7)
    {
        var regs = new[] { r0, 0x80, 0x0E, 0x80, 0x00, 0x76, 0x03, r7 };
        var a = new Z80Asm(0x100);
        a.Db(0xF3, 0xED, 0x56, 0x31, 0xF0, 0xDF);
        foreach (var (v, i) in regs.Select((v, i) => (v, i))) a.OutImm(0xBF, v).OutImm(0xBF, 0x80 | i);
        for (int k = 0; k < blocks.Length; k++)
        {
            var (addr, data) = blocks[k];
            a.OutImm(0xBF, addr & 0xFF).OutImm(0xBF, 0x40 | (addr >> 8));
            a.LdHl("blk" + k).Db(0x01, data.Length & 0xFF, data.Length >> 8).Abs(0xCD, "vcopy");
        }
        a.OutImm(0xBF, r1).OutImm(0xBF, 0x81);
        a.Label("frame").InN(0xBF).Db(0x4F, 0x3A, 0x01, 0xC0, 0xB1, 0x32, 0x01, 0xC0);
        a.Db(0xCB, 0x79).JrZ("frame");
        a.Db(0x79, 0x32, 0x00, 0xC0).Jr("frame");
        a.Label("vcopy");
        a.Db(0x7E).OutN(0xBE).Db(0x23, 0x0B, 0x78, 0xB1).JrNz("vcopy").Db(0xC9);
        for (int k = 0; k < blocks.Length; k++) a.Label("blk" + k).Bytes(blocks[k].Data);
        return SmsProg.Rom(a.Assemble(), null, null, 0x100);
    }

    public static string FileName(Mode mode, bool big) => $"sg-{mode.ToString().ToLowerInvariant()}-{(big ? "big" : "small")}.sg";

    public static int Write(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (Mode m in Enum.GetValues<Mode>())
            foreach (bool big in new[] { false, true })
                File.WriteAllBytes(Path.Combine(dir, FileName(m, big)), Build(m, big));
        for (int v = 1; v <= 3; v++) { Variant = v; File.WriteAllBytes(Path.Combine(dir, $"dbg-g2-v{v}.sg"), Build(Mode.Graphics2, false)); }
        Variant = 0;
        File.WriteAllBytes(Path.Combine(dir, "probe-palette.sg"), PaletteProbe());
        foreach (int n in new[] { 0, 1, 5, 20, 31 }) File.WriteAllBytes(Path.Combine(dir, $"probe-status-{n}.sg"), StatusProbe(n));
        Console.WriteLine($"wrote 8 SG-1000 programs and the probes to {dir}");
        return 0;
    }
}
