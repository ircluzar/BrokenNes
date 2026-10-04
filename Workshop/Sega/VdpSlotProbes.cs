using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// Probes of the VDP's VRAM access timing, to be run on the oracle and on this emulator and compared byte for byte in work RAM.
/// <para><b>Read-buffer sweep</b> (<see cref="ReadSweep"/>): the data port returns a buffer the VDP fills ahead of the program: setting a read address starts a fetch of that byte, and a read of the data port
/// returns the buffer and starts the next fetch. A program that reads too soon after setting the address gets the old buffer. The probe reads one byte (so the buffer holds the next one), sets another address, waits k NOPs, and
/// reads: the result is the fresh byte ($80 + 0) or the stale one. k runs 0..40 in one pass, and eight passes start at different lines, with the display on. A game that copies VRAM to VRAM (Game Gear Shinobi's
/// decompressor, with a PUSH/POP delay between the address and the read) depends on exactly this boundary.</para>
/// </summary>
internal static class VdpSlotProbes
{
    private const int Passes = 8, Ks = 41;

    // fine sweep: the delay between the address write and the read is 4 k + extra[e] cycles, one-cycle steps from 0 to 31
    private static readonly byte[][] Extras = { Array.Empty<byte>(), new byte[] { 0xED, 0x57 }, new byte[] { 0x03 }, new byte[] { 0x3E, 0x00 } };   // nothing; LD A,I (9); INC BC (6); LD A,0 (7)

    public static byte[] ReadSweep(bool displayOn = true, bool fine = false)
    {
        var a = new Z80Asm(0x100);
        a.Db(0xF3, 0xED, 0x56, 0x31, 0xF0, 0xDF);                                                   // DI; IM 1; LD SP,$DFF0
        // registers: R0 = 4 (mode 4), R1 = $80 (display off while the tables are written), R2 = $FF, R5 = $FF, R7 = 0
        foreach (var (reg, val) in new[] { (0, 0x04), (1, 0x80), (2, 0xFF), (5, 0xFF), (7, 0x00), (8, 0x00), (9, 0x00), (10, 0xFF) }) a.OutImm(0xBF, val).OutImm(0xBF, 0x80 | reg);
        // VRAM $0200: 64 bytes of pattern A (3 i + 1), $0300: 64 bytes of pattern B ($80 + i)
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x42).Db(0x0E, 0xBE, 0x06, 64).LdHl("patA").Db(0xED, 0xB3);
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x43).Db(0x0E, 0xBE, 0x06, 64).LdHl("patB").Db(0xED, 0xB3);
        a.OutImm(0xBF, displayOn ? 0xC0 : 0x80).OutImm(0xBF, 0x81);                                  // R1: display on (or stay off)
        a.Db(0x21, 0x00, 0xC1);                                                                     // LD HL,$C100 (results pointer)
        a.Db(0x16, 0x00, 0x1E, 0x08);                                                               // LD D,0 (pass); LD E,8 (the line the pass starts at)
        a.Label("pass");
        a.Label("wait").InN(0x7E).Db(0xBB).JrNz("wait");                                            // IN A,($7E); CP E; JR NZ: wait for the V counter to reach E
        a.Abs(0xCD, "sweep");                                                                       // CALL sweep
        a.Db(0x7B, 0xC6, 0x0B, 0x5F);                                                               // LD A,E; ADD A,11; LD E,A
        a.Db(0x14, 0x7A, 0xFE, Passes).JrNz("pass");                                                // INC D; LD A,D; CP passes; JR NZ,pass
        a.Label("done").Jr("done");

        a.Label("sweep");
        int blocks = fine ? 8 * 4 : Ks;
        for (int b = 0; b < blocks; b++)
        {
            int k = fine ? b / 4 : b, e = fine ? b % 4 : 0;
            a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x02);                                                // read address $0200: the buffer is filled with A0
            a.InN(0xBE);                                                                            // read it: the buffer moves on to A1
            for (int i = 0; i < 12; i++) a.Db(0x00);                                                // let the fetch finish
            a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x03);                                                // read address $0300: a fetch of B0 starts
            for (int i = 0; i < k; i++) a.Db(0x00);                                                 // k NOPs
            if (fine) a.Bytes(Extras[e]);
            a.InN(0xBE);                                                                            // B0 if the fetch is done, else the stale A1
            a.Db(0x77, 0x23);                                                                       // LD (HL),A; INC HL
        }
        a.Db(0xC9);
        a.Label("patA").Bytes(Enumerable.Range(0, 64).Select(i => (byte)(3 * i + 1)));
        a.Label("patB").Bytes(Enumerable.Range(0, 64).Select(i => (byte)(0x80 + i)));
        var code = a.Assemble();
        return SmsProg.Rom(code, null, null, 0x100);
    }

    // ------------------------------------------------------------------------------------------------ the VRAM write sweep
    // Tile row i of the picture (lines 8i..8i+7) is made of its own tile 1+i, whose pattern is all zero (colour 0, black). The program writes $FF to plane 0 of that tile's first row (the picture's line 8i turns white,
    // from the first tile column the chip fetches after the byte has landed on), at a time swept through 24 rows: 4 k + extra cycles after the poll of the V counter that waits for line 8i - 1 (k = 4 i + shift).
    // Where the white starts on the line, against the time the write was made, is the chip's write latency and access-slot pattern as the picture sees them. 16 variants (shift 0..3, extra 0..3) give one-cycle steps.

    // ------------------------------------------------------------------------------------------------ a data write followed by a new address
    // The VDP takes a CPU write to VRAM at its next access slot; a program that sets a new address a few cycles after the write may redirect or lose it. Iteration j (0..31): set the address $0200 + j, write $55 to the data port, wait
    // j NOPs, set the address $0300 + j (no data). Afterwards every $0200 + j and $0300 + j is read back into $C100 + j and $C120 + j: $55 / 0 where the write landed at its own address, 0 / $55 where it was redirected,
    // 0 / 0 where it was lost. The display is on (the access slots are in play) or off.

    public static byte[] MisdirectProbe(bool displayOn)
    {
        var a = new Z80Asm(0x100);
        a.Db(0xF3, 0xED, 0x56, 0x31, 0xF0, 0xDF);
        foreach (var (reg, val) in new[] { (0, 0x04), (1, 0x80), (2, 0xFF), (5, 0xFF), (7, 0x00), (8, 0x00), (9, 0x00), (10, 0xFF) }) a.OutImm(0xBF, val).OutImm(0xBF, 0x80 | reg);
        // zero VRAM $0000-$03FF
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x40).Db(0x11, 0x00, 0x04);
        a.Label("clr").Db(0xAF).OutN(0xBE).Db(0x1B, 0x7A, 0xB3).JrNz("clr");
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x7F).OutImm(0xBE, 0xD0);                                // sprite table end
        if (displayOn) a.OutImm(0xBF, 0xC0).OutImm(0xBF, 0x81);                                    // display on
        a.Label("sync").InN(0x7E).Db(0xFE, 10).JrNz("sync");                                       // wait for line 10
        for (int j = 0; j < 32; j++)
        {
            a.OutImm(0xBF, 0x00 + j).OutImm(0xBF, 0x42);                                           // write address $0200 + j
            a.Db(0x3E, 0x55).OutN(0xBE);                                                           // the data write
            for (int n = 0; n < j; n++) a.Db(0x00);
            a.OutImm(0xBF, 0x00 + j).OutImm(0xBF, 0x43);                                           // the next address: $0300 + j
        }
        a.OutImm(0xBF, 0xC0).OutImm(0xBF, 0x81);                                                   // keep (or turn) the display on until the end of the frame...
        a.Label("v").InN(0x7E).Db(0xFE, 0xC0).JrNz("v");                                           // ...wait for the vertical blank
        a.OutImm(0xBF, 0x80).OutImm(0xBF, 0x81);                                                   // display off: reads see no slot
        a.Db(0x21, 0x00, 0xC1);                                                                    // LD HL,$C100
        foreach (int baseAddr in new[] { 0x0200, 0x0300 })
        {
            a.OutImm(0xBF, baseAddr & 0xFF).OutImm(0xBF, baseAddr >> 8);                           // read address
            a.Db(0x06, 32);                                                                        // LD B,32
            string l = "rd" + baseAddr;
            a.Label(l).InN(0xBE).Db(0x77, 0x23).Djnz(l);                                           // IN A,($BE); LD (HL),A; INC HL; DJNZ
        }
        a.Label("done").Jr("done");
        return SmsProg.Rom(a.Assemble(), null, null, 0x100);
    }

    // ------------------------------------------------------------------------------------------------ two VRAM writes S cycles apart
    // Trial t (0..47): the address is set to $0200 + 2 t, then $11 and $22 are written to the data port S cycles apart (S = 12 + 4 n + extra, n = t / 4 and extra by t % 4: S = 12 .. 59 in one-cycle steps, using OUT (C),r), each
    // trial begun at another line of the picture. The two bytes are read back into $C100 + 2 t: "$11 $22" both landed, "$11 $00" the second was lost, "$00 $22" the first was lost.
    public static byte[] DoubleWrite(bool displayOn, int lineOffset = 0)
    {
        var a = new Z80Asm(0x100);
        a.Db(0xF3, 0xED, 0x56, 0x31, 0xF0, 0xDF);
        foreach (var (reg, val) in new[] { (0, 0x04), (1, 0x80), (2, 0xFF), (5, 0xFF), (7, 0x00), (8, 0x00), (9, 0x00), (10, 0xFF) }) a.OutImm(0xBF, val).OutImm(0xBF, 0x80 | reg);
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x40).Db(0x11, 0x00, 0x08);
        a.Label("clr").Db(0xAF).OutN(0xBE).Db(0x1B, 0x7A, 0xB3).JrNz("clr");                       // zero $0000-$07FF
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x7F).OutImm(0xBE, 0xD0);
        if (displayOn) a.OutImm(0xBF, 0xC0).OutImm(0xBF, 0x81);
        a.Db(0x0E, 0xBE, 0x16, 0x11, 0x1E, 0x22);                                                  // LD C,$BE; LD D,$11; LD E,$22 (OUT (C),D / OUT (C),E write the two values)
        a.Db(0x26, 12 + lineOffset);                                                               // LD H,12 + offset: the line the first trial starts at (+3 per trial)
        for (int t = 0; t < 48; t++)
        {
            int n = t / 4, e = t % 4, addr = 0x200 + 2 * t;
            a.OutImm(0xBF, addr & 0xFF).OutImm(0xBF, 0x40 | (addr >> 8));                         // write address
            a.Label("p" + t).InN(0x7E).Db(0xBC).JrNz("p" + t);                                     // IN A,($7E); CP H; JR NZ: wait for the line
            a.Db(0x7C, 0xC6, 3, 0x67);                                                             // LD A,H; ADD A,3; LD H,A
            a.Db(0xED, 0x51);                                                                      // OUT (C),D: the first write
            for (int k = 0; k < n; k++) a.Db(0x00);
            a.Bytes(new byte[][] { Array.Empty<byte>(), new byte[] { 0xED, 0x57 }, new byte[] { 0x33 }, new byte[] { 0x3E, 0x00 } }[e]);   // nothing; LD A,I (9); INC SP (6); LD A,0 (7)
            a.Db(0xED, 0x59);                                                                      // OUT (C),E: the second, 12 + 4 n + extra cycles later
        }
        a.OutImm(0xBF, 0x80).OutImm(0xBF, 0x81);                                                   // display off before the read-back
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x02);                                                   // read address $0200
        a.Db(0x21, 0x00, 0xC1, 0x06, 96);                                                          // LD HL,$C100; LD B,96
        a.Label("rd").InN(0xBE).Db(0x77, 0x23).Djnz("rd");
        a.Label("done").Jr("done");
        return SmsProg.Rom(a.Assemble(), null, null, 0x100);
    }

    public const int SweepRows = 24;

    public static byte[] WriteSweep(int shift, int extraIndex)
    {
        var a = new Z80Asm(0x100);
        a.Db(0xF3, 0xED, 0x56, 0x31, 0xF0, 0xDF);
        foreach (var (reg, val) in new[] { (0, 0x04), (1, 0x80), (2, 0xFF), (5, 0xFF), (7, 0x00), (8, 0x00), (9, 0x00), (10, 0xFF) }) a.OutImm(0xBF, val).OutImm(0xBF, 0x80 | reg);
        // colour RAM: entry 0 black, entry 1 white
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0xC0).OutImm(0xBE, 0x00).OutImm(0xBE, 0x3F);
        // tiles 0..31 (VRAM $0000-$03FF) all zero: nothing is left over from the power-on contents
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x40).Db(0x11, 0x00, 0x04);
        a.Label("clr").Db(0xAF).OutN(0xBE).Db(0x1B, 0x7A, 0xB3).JrNz("clr");                       // XOR A; OUT; DEC DE; LD A,D; OR E; JR NZ
        // sprite table: Y = $D0 ends the list
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x7F).OutImm(0xBE, 0xD0);
        // name table at $3800: 32 columns x 28 rows of 2 bytes; the entry of row r is tile 1 + r (rows past the picture: tile 0)
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x78);
        a.Db(0x16, 0x00);                                                                          // LD D,0 (row)
        a.Label("rowloop");
        a.Db(0x0E, 0x20);                                                                          // LD C,32 (columns)
        a.Label("colloop");
        a.Db(0x7A, 0x3C).OutN(0xBE).OutImm(0xBE, 0x00);                                            // LD A,D; INC A; OUT ($BE),A; LD A,0; OUT ($BE),A  (tile 1 + row, high byte 0)
        a.Db(0x0D).JrNz("colloop");                                                                // DEC C; JR NZ
        a.Db(0x14, 0x7A, 0xFE, 28).JrNz("rowloop");                                                // INC D; LD A,D; CP 28; JR NZ
        a.OutImm(0xBF, 0xC0).OutImm(0xBF, 0x81);                                                   // R1 = $C0: display on
        // sync to the start of a frame: wait for the V counter to reach 192 (the lines before it are the picture; the sweep starts at line 7 of the next one)
        a.Label("sync").InN(0x7E).Db(0xFE, 0xC0).JrNz("sync");
        a.Label("sync2").InN(0x7E).Db(0xFE, 0x02).JrNz("sync2");
        for (int i = 1; i < SweepRows; i++)
        {
            int tile = 1 + i, addr = tile * 32, line = 8 * i - 1;
            a.OutImm(0xBF, addr & 0xFF).OutImm(0xBF, 0x40 | (addr >> 8));                         // VRAM write address: row 0, plane 0 of the tile
            a.Label("w" + i).InN(0x7E).Db(0xFE, line).JrNz("w" + i);                               // wait for line 8 i - 1
            int k = 4 * i + shift;
            for (int n = 0; n < k; n++) a.Db(0x00);
            a.Bytes(Extras[extraIndex]);
            a.Db(0x3E, 0xFF).OutN(0xBE);                                                           // LD A,$FF; OUT ($BE),A: the write
        }
        a.Label("done").Jr("done");
        return SmsProg.Rom(a.Assemble(), null, null, 0x100);
    }

    public static string SweepName(int shift, int extra) => $"vram-wsweep-{shift}{extra}.sms";

    /// <summary>
    /// <c>--smstest --vram-double &lt;dir&gt;</c>: the double-write probes (vram-double-on-0..2) run here, with the oracle's work RAM image (<c>dir/&lt;program&gt;_f14.ram.bin</c>) beside: per trial the spacing of the two writes, the line and
    /// master clock of each write as this emulator sees it, and the two bytes read back in the oracle and here.
    /// </summary>
    public static int DoubleReport(SmsSuiteContext ctx, string dir)
    {
        int differ = 0, total = 0;
        Console.WriteLine("prog t S  w1(line,mc) w2(line,mc)  oracle  mine");
        for (int off = 0; off < 3; off++)
        {
            string name = $"vram-double-on-{off}";
            var s = ctx.Session(DoubleWrite(true, off), name + ".sms");
            var w = new List<(int Line, int Mc, byte Val)>();
            s.Board.Vdp.DataWriteHook = (code, addr, value, line, mc) => { if (code == 1 && (value == 0x11 || value == 0x22) && addr >= 0x200 && addr < 0x260) w.Add((line, mc, value)); };
            for (int f = 0; f < 14; f++) s.RunFrame();
            byte[] mine = s.Board.Ram;
            string path = Path.Combine(dir, name + "_f14.ram.bin");
            byte[]? theirs = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var ex = new[] { 0, 9, 6, 7 };
            for (int t = 0; t < 48; t++)
            {
                int S = 12 + 4 * (t / 4) + ex[t % 4];
                var a = w.Count > 2 * t ? w[2 * t] : default; var b = w.Count > 2 * t + 1 ? w[2 * t + 1] : default;
                string o = theirs == null ? "-" : $"{theirs[0x100 + 2 * t]:X2} {theirs[0x101 + 2 * t]:X2}", m = $"{mine[0x100 + 2 * t]:X2} {mine[0x101 + 2 * t]:X2}";
                total++; if (o != m) differ++;
                Console.WriteLine($"{off} {t,2} {S,2}  ({a.Line,3},{a.Mc,3}) ({b.Line,3},{b.Mc,3})  {o}  {m}{(o != m ? "  <--" : "")}");
            }
        }
        Console.WriteLine($"{differ} of {total} trials differ");
        return 0;
    }

    private static int FirstLit(uint[] px, int width, int y)
    {
        for (int x = 0; x < width; x++) if ((px[y * width + x] & 0xFFFFFF) != 0) return x;
        return 256;
    }

    /// <summary>
    /// <c>--smstest --vram-sweep &lt;dir&gt;</c>: runs the 16 write-sweep programs here and reads the oracle's picture of each (<c>dir/&lt;program&gt;_f8.png</c>, taken with mesen-capture.ps1 -Tag &lt;program&gt; -Frames 8 -Overscan 0). Per row of each
    /// program: when the data write reached the chip (master clocks after the start of the row's first line; negative = before it) and where the white began on that line in the oracle's picture and in this one.
    /// </summary>
    public static int Sweep(SmsSuiteContext ctx, string dir)
    {
        var rows = new List<(int Rel, int Oracle, int Mine, string Name, int I)>();
        for (int sh = 0; sh < 4; sh++)
            for (int e = 0; e < 4; e++)
            {
                string name = SweepName(sh, e).Replace(".sms", "");
                var s = ctx.Session(WriteSweep(sh, e), name + ".sms");
                var writes = new Dictionary<int, int>();
                s.Board.Vdp.DataWriteHook = (code, addr, value, line, mc) => { if (code == 1 && value == 0xFF && addr % 32 == 0 && addr / 32 is >= 2 and <= SweepRows) writes[addr / 32 - 1] = (line - 8 * (addr / 32 - 1)) * 684 + mc; };
                for (int f = 0; f < 8; f++) s.RunFrame();
                string png = Path.Combine(dir, name + "_f8.png");
                uint[]? theirs = File.Exists(png) ? SmsPng.Load(png, out _, out _, 192) : null;
                for (int i = 1; i < SweepRows; i++)
                {
                    if (!writes.TryGetValue(i, out int rel)) continue;
                    rows.Add((rel, theirs == null ? -1 : FirstLit(theirs, 256, 8 * i), FirstLit(s.Frame, 256, 8 * i), name, i));
                }
            }
        Console.WriteLine("rel_mc  oracle_x  mine_x  program row");
        foreach (var r in rows.OrderBy(r => r.Rel)) Console.WriteLine($"{r.Rel,6}  {r.Oracle,8}  {r.Mine,6}  {r.Name} {r.I}{(r.Oracle != r.Mine ? "   <-- differs" : "")}");
        Console.WriteLine($"{rows.Count(r => r.Oracle != r.Mine)} of {rows.Count} rows differ");
        return 0;
    }

    public static int Write(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "vram-read-sweep.sms"), ReadSweep());
        File.WriteAllBytes(Path.Combine(dir, "vram-read-sweep-off.sms"), ReadSweep(displayOn: false));
        File.WriteAllBytes(Path.Combine(dir, "vram-read-fine.sms"), ReadSweep(fine: true));
        for (int sh = 0; sh < 4; sh++) for (int e = 0; e < 4; e++) File.WriteAllBytes(Path.Combine(dir, SweepName(sh, e)), WriteSweep(sh, e));
        File.WriteAllBytes(Path.Combine(dir, "vram-double-on.sms"), DoubleWrite(true));
        for (int off = 0; off < 3; off++) File.WriteAllBytes(Path.Combine(dir, $"vram-double-on-{off}.sms"), DoubleWrite(true, off));
        File.WriteAllBytes(Path.Combine(dir, "vram-double-off.sms"), DoubleWrite(false));
        File.WriteAllBytes(Path.Combine(dir, "vram-misdirect-on.sms"), MisdirectProbe(true));
        File.WriteAllBytes(Path.Combine(dir, "vram-misdirect-off.sms"), MisdirectProbe(false));
        Console.WriteLine($"wrote VDP access probes to {dir}");
        return 0;
    }
}
