using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// The 50 Hz machine, headless: the video standard changes the line count (313 lines of 228 Z80 cycles), the Z80 clock (3.547 MHz, so the PSG and the YM2413 play lower), the frame rate (49.70 Hz) and the V counter's
/// jump; nothing in the program or the cartridge changes. Checked: the cycles of a frame, the PSG's and the FM chip's pitch against the PAL clock, and FluBBa's VDP test (55 checks of data port, counters,
/// interrupts and sprite flags) run as a PAL console. The oracle's PAL result bytes of the same program are the numbers in <see cref="OraclePalVdptest"/>; PAL games are compared with the oracle frame by frame in the roster (Micro Machines, Streets of Rage II, Bubble Bobble).
/// </summary>
internal static class PalSuite
{
    // the VDP test's result bytes for tests 1..60 when the oracle (Mesen 2.1.1, region PAL) runs it with Button 1 pressed at frames 300, 700, 1100 (the same input as the NTSC run); 0 = ok, 255 = error; test 21 leaves 1 for a PAL V counter
    // (measured with the capture script: -Region Pal, work RAM at frame 1600; the NTSC run of the same script has 2 at test 21 and the same 255 at test 46)
    internal static readonly byte[] OraclePalVdptest =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    };

    private static byte[] ToneRom() => SmsProg.Rom(SmsProg.Cat(SmsProg.Prolog, SmsProg.Out((0x7F, 0x80), (0x7F, 0x10), (0x7F, 0x90)), SmsProg.Halt));

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        foreach (var (name, pal) in new[] { ("Bubble Bobble (Europe).sms", true), ("Alex Kidd in Miracle World (USA, Europe).sms", false) })
        {
            var s = ctx.Session(ToneRom(), name);
            for (int f = 0; f < 4; f++) s.RunFrame();
            long c0 = s.Board.Cycles;
            for (int f = 0; f < 20; f++) s.RunFrame();
            long perFrame = (s.Board.Cycles - c0) / 20;
            long want = (pal ? 313 : 262) * 228L;
            if (ctx.Poison == "pal") want = 262 * 228L;
            yield return new SmsCheck($"\"{name}\": a frame is {want} Z80 cycles ({(pal ? 313 : 262)} lines of 228)", perFrame == want, $"{perFrame} cycles");

            double clock = (pal ? 53203424.0 : 53693175.0) / 15;
            var (l, _) = PsgSuite.Capture(s);
            double f0 = PsgSuite.Frequency(l, s.SampleRate), wantHz = clock / (32.0 * 256);
            yield return new SmsCheck($"\"{name}\": the PSG tone of period $100 is {wantHz:F2} Hz ({(pal ? "PAL" : "NTSC")} Z80 clock {clock:F0} Hz)", Math.Abs(f0 - wantHz) < wantHz * 0.004, $"measured {f0:F2} Hz");

            var fm = ctx.Session(FmProbes.Tone(1, 290, 4), name, ConsoleKind.MasterSystem, null, "SMS-FM");
            var (fl, _) = PsgSuite.Capture(fm);
            double fw = 290.0 * 16 * (clock / 72) / 524288.0, ff = PsgSuite.Frequency(fl, fm.SampleRate);
            yield return new SmsCheck($"\"{name}\": the YM2413's tone (F-number 290, block 4) is {fw:F2} Hz (the chip runs from the Z80 clock)", Math.Abs(ff - fw) < fw * 0.004, $"measured {ff:F2} Hz");
        }

        // FluBBa's VDP test as a PAL console
        string rom = ctx.TestRom("sms-roms", "smsvdptest-1.31", "VDPTEST.sms");
        if (!File.Exists(rom)) { yield return SmsCheck.Skipped("VDP test as a PAL console", "VDPTEST.sms not found under " + ctx.Tools); yield break; }
        var results = RunVdptest(ctx, rom, ctx.Poison == "pal" ? RegionChoice.Ntsc : RegionChoice.Pal);
        // test #46 (a sprite parked off the picture still collides) is the one the oracle fails and the hardware passes (FluBBa's test; see the vdptest suite): here it must pass, every other byte must equal the oracle's
        var differing = Enumerable.Range(0, 60).Where(i => i != 45 && results[i] != OraclePalVdptest[i]).Select(i => i + 1).ToArray();
        yield return new SmsCheck("the VDP test run as a PAL console: every result byte equals the oracle's PAL run (V counter table of test #21 included), except #46 which the oracle fails", differing.Length == 0 && results[45] == 0, $"bytes differing from the oracle: {(differing.Length == 0 ? "none" : string.Join(",", differing))}; test 46 byte {results[45]}");
    }

    private static byte[] RunVdptest(SmsSuiteContext ctx, string rom, RegionChoice region)
    {
        var s = ctx.Session(rom, "VDPTEST.sms", ConsoleKind.MasterSystem, region);
        for (int f = 0; f < 1600; f++)
        {
            s.SetPad(0, f is >= 300 and < 305 or >= 700 and < 705 or >= 1100 and < 1105 ? PadButtons.A : 0);
            s.RunFrame();
        }
        var r = new byte[60];
        Array.Copy(s.Board.Ram, 0x900, r, 0, 60);
        return r;
    }

    /// <summary>--pal-vdptest: prints the 60 result bytes of the VDP test as a PAL console (to compare with the oracle's, whose bytes go into <see cref="OraclePalVdptest"/>).</summary>
    public static int Print(SmsSuiteContext ctx)
    {
        string rom = ctx.TestRom("sms-roms", "smsvdptest-1.31", "VDPTEST.sms");
        var r = RunVdptest(ctx, rom, RegionChoice.Pal);
        Console.WriteLine(string.Join(",", r.Select(b => b.ToString())));
        return 0;
    }
}
