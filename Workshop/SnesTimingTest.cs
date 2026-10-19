using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// <c>--snes-timing-test</c>: how much CPU time HDMA leaves the 65816, measured with programs of our own and compared with Mesen 2.
///
/// Each probe is a 32 KB LoROM (<see cref="SnesProgs.Rom"/>) that enables a set of HDMA channels and then, every frame, counts busy-loop
/// iterations from the end of vblank to the start of the next one (lines 0-224, where HDMA runs) and stores the count at work RAM
/// $0020/$0021 ($0022 counts frames). The count is the CPU time HDMA did not take: one iteration is about 112 master clocks, so a
/// per-line HDMA charge that is 8 clocks too small per channel shows as tens of iterations per frame.
///
/// The expected counts were measured on Mesen 2.1.1 (the SuperVRUN package's copy, SNES RAM zeroed, frame skipping off) with
/// <c>Workshop/mesen/snes-hdma-timing.lua</c> on the ROMs <c>--write-roms DIR</c> writes; how to re-measure is in that script's header.
/// A probe passes when BrokenNes' mean count over frames 21-40 is within <see cref="Tolerance"/> iterations of Mesen's.
///
///   --snes-timing-test [--write-roms DIR] [--frames N]
/// Exit code: 0 every probe agrees with Mesen | 1 a probe disagrees | 2 usage.
/// </summary>
internal static class SnesTimingTest
{
    /// <summary>
    /// Iterations of slack (one iteration is about 112 master clocks). After the HDMA fix of 2026-10-18 the probes land within +2 / -1 / -3
    /// iterations of Mesen: at most about 1.5 clocks per line, the size of the DMA/CPU clock-domain alignment Mesen applies at every HDMA
    /// start (BrokenNes does not model it). The defect this guards against was 8 clocks per channel per line: 16 iterations for one channel,
    /// 79 for five.
    /// </summary>
    private const double Tolerance = 4.0;

    /// <summary>(name, HDMAEN mask, repeat-mode table, Mesen's mean iterations per frame over frames 21-40).</summary>
    private static readonly (string Name, byte Mask, bool Repeat, Body Body, double Mesen, string? Known)[] Probes =
    {
        // the loop bodies alone (no HDMA): the CPU's own cycle costs
        ("native-incdp",   0x00, false, Body.NativeIncDp, 2656.0, null),
        ("native-inx",     0x00, false, Body.NativeInx,   4512.0, null),
        ("emu-inx",        0x00, false, Body.EmuInx,      3379.9, null),
        ("emu-incdp",      0x00, false, Body.EmuIncDp,    2610.0,
            "in 6502 emulation mode Mesen spends 2 more master clocks on a read-modify-write (the modify cycle as a write of the old value, 8 clocks to WRAM, " +
            "not an internal cycle of 6); CPU_SFC.ModifyMemory always uses an internal cycle. Native mode agrees (native-incdp). Writing the old value back " +
            "in emulation mode was measured exact here (2610) but changed nothing in Project Nested's games, so it was left for the SNES core (docs/projects/vrun-resume.md)"),
        // HDMA on top of the native loop: one channel / five channels (Nested's $7A) with 127-line entries (mostly no transfer,
        // no reload: only the per-line overhead), and five channels transferring every line (repeat mode)
        ("hdma1-long",     0x02, false, Body.NativeIncDp, 2602.0, null),
        ("hdma5-long",     0x7A, false, Body.NativeIncDp, 2540.0, null),
        ("hdma5-repeat",   0x7A, true,  Body.NativeIncDp, 2462.0, null),
        // general-purpose DMA at the start of the counted window (ROM -> $2180 WRAM port, into $7F:0000): one 2 KB transfer, and 64
        // transfers of 32 bytes (the same bytes, dominated by the per-transfer overhead)
        ("dma-1x2048",     0x00, false, Body.NativeIncDpDma2048, 2506.0, null),
        ("dma-64x32",      0x00, false, Body.NativeIncDpDma64x32, 2362.0,
            "Mesen spends about 8.75 more master clocks per general-purpose DMA: the DMA unit starts on the next multiple of 8 master clocks " +
            "and the CPU then waits back to a whole CPU cycle; BOARD_SFC.RunDma charges neither. Adding both was measured exact here (2362) but moved " +
            "6 more UAT goldens (Super VRUN, Super Mario World, Kirby Super Star) without changing any of VRUN's pictures against Mesen, so it was " +
            "left for the SNES core (docs/projects/vrun-resume.md). One large transfer agrees (dma-1x2048)"),
    };

    /// <summary>The counting loop: 6502 emulation mode or native mode, with a read-modify-write INC dp or a register INX; the Dma bodies
    /// run general-purpose DMA first, then the native INC dp loop.</summary>
    public enum Body { EmuIncDp, EmuInx, NativeInx, NativeIncDp, NativeIncDpDma2048, NativeIncDpDma64x32 }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        int frames = int.Parse(Opt("frames", "40"));
        string writeRoms = Opt("write-roms", "");
        if (writeRoms != "")
        {
            Directory.CreateDirectory(writeRoms);
            foreach (var p in Probes) File.WriteAllBytes(Path.Combine(writeRoms, $"hdma-probe-{p.Name}.sfc"), ProbeRom(p.Mask, p.Repeat, p.Body));
            Console.WriteLine($"wrote {Probes.Length} probe ROMs to {writeRoms}");
        }
        int failures = 0;
        if (args.Any(a => a.Equals("--list", StringComparison.OrdinalIgnoreCase))) { foreach (var p in Probes) Console.WriteLine(p.Name); return 0; }
        foreach (var p in Probes)
        {
            var counts = Measure(ProbeRom(p.Mask, p.Repeat, p.Body), frames);
            double mean = counts.Count > 0 ? counts.Average() : double.NaN;
            string got = $"{mean:F1} (min {counts.DefaultIfEmpty().Min()}, max {counts.DefaultIfEmpty().Max()}, {counts.Count} frames)";
            if (double.IsNaN(p.Mesen)) { Console.WriteLine($"INFO {p.Name,-14} BrokenNes {got}; no Mesen value recorded"); continue; }
            bool ok = Math.Abs(mean - p.Mesen) <= Tolerance;
            string verdict = ok ? "PASS" : p.Known != null ? "KNOWN" : "FAIL";
            if (!ok && p.Known == null) failures++;
            Console.WriteLine($"{verdict} {p.Name,-14} BrokenNes {got}, Mesen {p.Mesen:F1}, difference {mean - p.Mesen:+0.0;-0.0}");
            if (!ok && p.Known != null) Console.WriteLine($"      known difference, not fixed: {p.Known}");
            if (ok && p.Known != null) Console.WriteLine($"      the known difference is gone: update this probe ({p.Known})");
        }
        Console.WriteLine(failures == 0 ? "snes-timing-test: PASS" : $"snes-timing-test: {failures} probe(s) disagree with Mesen");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The counts the program stored for frames 21..frames (the first frames settle: HDMA is armed at the first line 0 after the enable).</summary>
    private static List<int> Measure(byte[] rom, int frames)
    {
        using var s = (SnesSession)ConsoleSessions.Create(ConsoleKind.Snes, rom, "SFC", "SFC", "SFC", null, "hdma-probe.sfc");
        var counts = new List<int>();
        int lastFrameNo = -1;
        for (int f = 1; f <= frames; f++)
        {
            s.RunFrame();
            int frameNo = s.Board.Wram[0x22];
            if (f > 20 && frameNo != lastFrameNo) counts.Add(s.Board.Wram[0x20] | s.Board.Wram[0x21] << 8);
            lastFrameNo = frameNo;
        }
        return counts;
    }

    /// <summary>
    /// The probe: HDMA channels in <paramref name="mask"/> write one byte per transfer to $2126 (WH0, harmless) from a table at $00:9000.
    /// Non-repeat tables are three entries of 127 lines (one transfer each, then 126 lines with no transfer and no reload: the case where
    /// only the per-line overhead is paid); repeat tables are two entries of 127 lines that transfer every line.
    /// </summary>
    public static byte[] ProbeRom(byte mask, bool repeat, Body body = Body.EmuIncDp)
    {
        var code = new List<byte>();
        int Pc() => 0x8000 + code.Count;
        void B(params byte[] b) => code.AddRange(b);
        void Branch(byte op, int target) { code.Add(op); code.Add((byte)(target - (Pc() + 1))); }
        B(0x78);                                          // SEI
        B(0xA9, 0x0F, 0x8D, 0x00, 0x21);                  // LDA #$0F ; STA $2100
        B(0x9C, 0x00, 0x42);                              // STZ $4200   (no NMI, no auto joypad read)
        for (int ch = 0; ch < 8; ch++)
        {
            if ((mask & (1 << ch)) == 0) continue;
            byte r = (byte)(ch << 4);
            B(0xA9, 0x00, 0x8D, r, 0x43);                 // DMAP: mode 0 (one byte), direct, A->B
            B(0xA9, 0x26, 0x8D, (byte)(r | 1), 0x43);     // BBAD: $2126
            B(0xA9, 0x00, 0x8D, (byte)(r | 2), 0x43);     // table $9000
            B(0xA9, 0x90, 0x8D, (byte)(r | 3), 0x43);
            B(0xA9, 0x00, 0x8D, (byte)(r | 4), 0x43);     // bank $00
        }
        B(0xA9, mask, 0x8D, 0x0C, 0x42);                  // LDA #mask ; STA $420C
        B(0x64, 0x22);                                    // STZ $22
        int wait = Pc();
        B(0x2C, 0x12, 0x42); Branch(0x10, wait);          // L1: BIT $4212 ; BPL L1   (wait for vblank)
        int end = Pc();
        B(0x2C, 0x12, 0x42); Branch(0x30, end);           // L2: BIT $4212 ; BMI L2   (wait for its end: line 0)
        bool dma = body is Body.NativeIncDpDma2048 or Body.NativeIncDpDma64x32;
        bool native = body is Body.NativeInx or Body.NativeIncDp || dma;
        if (native) B(0x18, 0xFB, 0xC2, 0x10, 0xE2, 0x20);   // CLC ; XCE ; REP #$10 (16-bit X/Y) ; SEP #$20 (8-bit A)
        B(0x64, 0x10, 0x64, 0x11);                        // STZ $10 ; STZ $11
        if (dma)
        {
            B(0x9C, 0x81, 0x21, 0x9C, 0x82, 0x21, 0xA9, 0x01, 0x8D, 0x83, 0x21);   // STZ $2181 ; STZ $2182 ; LDA #1 ; STA $2183  (WMADD $7F:0000)
            B(0x9C, 0x00, 0x43, 0xA9, 0x80, 0x8D, 0x01, 0x43);                     // STZ $4300 (A->B, mode 0) ; LDA #$80 ; STA $4301 ($2180)
            int n = body == Body.NativeIncDpDma2048 ? 1 : 64, size = body == Body.NativeIncDpDma2048 ? 0x800 : 0x20;
            B(0xA0, (byte)n, 0x00);                                                // LDY #n
            int dl = Pc();
            B(0xA2, 0x00, 0x90, 0x8E, 0x02, 0x43, 0x9C, 0x04, 0x43);               // LDX #$9000 ; STX $4302 ; STZ $4304
            B(0xA2, (byte)size, (byte)(size >> 8), 0x8E, 0x05, 0x43);             // LDX #size ; STX $4305
            B(0xA9, 0x01, 0x8D, 0x0B, 0x42);                                       // LDA #1 ; STA $420B
            B(0x88); Branch(0xD0, dl);                                             // DEY ; BNE
        }
        if (body is Body.EmuInx or Body.NativeInx) B(0xA2, 0x00); // LDX #0 (8-bit) ...
        if (body == Body.NativeInx) B(0x00);               // ... or LDX #$0000 (16-bit: the operand's high byte)
        int loop = Pc();
        switch (body)
        {
            case Body.EmuIncDp:
            case Body.NativeIncDp:
            case Body.NativeIncDpDma2048:
            case Body.NativeIncDpDma64x32:
                B(0xE6, 0x10, 0xD0, 0x02, 0xE6, 0x11);    // L3: INC $10 ; BNE +2 ; INC $11
                break;
            case Body.EmuInx:
                B(0xE8, 0xD0, 0x02, 0xE6, 0x11);          // L3: INX ; BNE +2 ; INC $11
                break;
            case Body.NativeInx:
                B(0xE8);                                  // L3: INX (16-bit)
                break;
        }
        B(0x2C, 0x12, 0x42); Branch(0x10, loop);          // BIT $4212 ; BPL L3       (count until vblank)
        if (body is Body.EmuInx or Body.NativeInx) B(0x86, 0x10);   // STX $10 (8 or 16 bits)
        B(0xA5, 0x10, 0x85, 0x20, 0xA5, 0x11, 0x85, 0x21, 0xE6, 0x22);   // $20/$21 = count ; INC $22
        if (native) B(0x38, 0xFB);                         // SEC ; XCE (back to emulation mode for the waits)
        B(0x4C, (byte)wait, (byte)(wait >> 8));          // JMP L1
        var table = new List<byte>();
        if (repeat) { for (int e = 0; e < 2; e++) { table.Add(0xFF); for (int i = 0; i < 127; i++) table.Add((byte)i); } }
        else { for (int e = 0; e < 3; e++) { table.Add(0x7F); table.Add(0x00); } }
        table.Add(0x00);
        var all = new byte[0x1000 + table.Count];
        code.CopyTo(all);
        table.CopyTo(all, 0x1000);
        return SnesProgs.Rom(all);
    }
}
