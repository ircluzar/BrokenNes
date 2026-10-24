using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// <c>--wms-test</c>'s TC checks: the WideMess test cartridge (WideMess/testcart, built into WideMess/testroms/bin/widemess-testcart.wms). The cartridge judges itself and keeps its
/// results in work RAM ($7E:1000, WideMess/testcart/README.md); these checks read that table, measure from outside what it claims (the speed counts against spec 2's model, the
/// voices it names in the DSP, the layers it draws), navigate its menu with the pad to every picture page and compare each with the picture the model of the spec drew for it, and
/// run the negative controls: the cartridge's SNES image on the SNES console, and the cartridge with WMFEAT masked by its own ROM byte.
/// </summary>
internal static partial class WideMessCli
{
    private const int TcState = 0x1007, TcScreen = 0x100A, TcStatus = 0x1020, TcChecks = 0x1028, TcFails = 0x1030, TcMaskOffset = 0xFF87;
    private static readonly int[] TcExpectedChecks = { 3, 5, 0, 0, 38, 12, 45, 6 };

    /// <summary><c>--wms-testcart [--roms DIR] [--reference-paths]</c>: the TC checks alone (<c>--wms-test</c> runs them after milestone 5's). Exit 0 when every check passes.</summary>
    public static int TestCartTest(string[] args)
    {
        RomTestCli.EnsureConsole();
        Consoles.WideMessEnabled = true;
        string dir = Opt(args, "roms", Path.Combine(AppContext.BaseDirectory, "WideMess", "testroms", "bin"));
        fails = passes = 0;
        if (args.Any(a => a.Equals("--reference-paths", StringComparison.OrdinalIgnoreCase)))
        {
            PPU_SFC.FastPaths = DSP_SFC.FastPaths = BOARD_SFC.FastPaths = false;
            Console.WriteLine("(reference paths: the SNES chips' fast paths are off)");
        }
        if (!TestCartTests(dir)) return 2;
        Console.WriteLine($"{passes} passed, {fails} failed");
        return fails == 0 ? 0 : 1;
    }

    private static bool TestCartTests(string dir)
    {
        string cart = Path.Combine(dir, "widemess-testcart.wms"), sfc = Path.Combine(dir, "widemess-testcart.sfc"), json = Path.Combine(dir, "widemess-testcart.json");
        foreach (var f in new[] { cart, sfc, json })
            if (!File.Exists(f)) { Console.Error.WriteLine($"missing {f} (build WideMess/testcart/build.ps1)"); return false; }
        byte[] wms = File.ReadAllBytes(cart);
        byte[] image = WmsFile.Payload(wms, out var header);

        Console.WriteLine("TC.1 The test cartridge: its run-all (SELECT held at power-on), read from its results table and measured from outside");
        using (var s = Session(ConsoleKind.WideMess, wms))
        {
            var w = s.Board.Wram;
            var voicesAlone = new HashSet<int>(); int mostAtOnce = 0, audible = 0, frames = 0;
            for (int f = 1; f <= 4000 && w[TcState] != 3; f++, frames++)
            {
                s.SetPad(0, f <= 30 ? PadButtons.Select : PadButtons.None);
                int peak = RunFramePeak(s);
                if (w[TcScreen] == 6 && peak > 66) audible++;
                if (w[TcScreen] == 6 && w[0x120D] == 2 && s.Board.Apu is APU_SFC apu)       // test 5, its voices one at a time: which ones the DSP itself shows sounding
                {
                    var on = SoundingVoices(apu.Dsp);
                    foreach (int v in on) voicesAlone.Add(v);
                    mostAtOnce = Math.Max(mostAtOnce, on.Count);
                }
            }
            Check(w[TcState] == 3 && w[0x100B] == 1, "the run-all finishes by itself (state 3, one run) and the summary shows", $"{frames} frames, screen {w[TcScreen]}");
            Check(w[0x1000] == 'W' && w[0x1001] == 'M' && w[0x1002] == 'T' && w[0x1003] == 'C' && w[0x1004] == 1, "the results table: magic WMTC, version 1");
            string status = new string(Enumerable.Range(0, 8).Select(i => (char)w[TcStatus + i]).ToArray());
            Check(status == "PPVVPPPP", "each test's status: 1, 2, 5, 6, 7, 8 passed; 3 and 4 (pictures alone) EYE", status);
            var counts = Enumerable.Range(0, 8).Select(i => (int)w[TcChecks + i]).ToArray();
            int failed = Enumerable.Range(0, 8).Sum(i => w[TcFails + i]);
            Check(counts.SequenceEqual(TcExpectedChecks) && failed == 0, "109 self-checks made (3 + 5 + 38 + 12 + 45 + 6), none failed", $"{string.Join(" ", counts)}, {failed} failed");
            Check(w[0x1010] == 0x57 && w[0x1011] == 1 && w[0x1013] == WmsExtension.Features && w[0x1016] == 0x42 && w[0x1017] == 0x42 && w[0x1018] == 0x42 && w[0x1019] == 1,
                  "test 8 read WMID $57, WMVER 1, WMFEAT as the console has it, open bus $42 before the unlock and after a broken sequence, the reserved registers 0",
                  $"WMID {w[0x1010]:X2} WMVER {w[0x1011]} WMFEAT {w[0x1013]:X2}, before {w[0x1016]:X2} {w[0x1017]:X2} {w[0x1018]:X2}, reserved {w[0x1019]}");
            // test 6's counts, against spec 2's model computed here (the cartridge's NMI: its jmp (nmijump), 3 ROM and 2 work-RAM cycles, then speed.asm's 328 / 92)
            int Wd(int a) => w[a] | w[a + 1] << 8;
            const int CartNmi = 328 + 40, CartNmiFast = 92 + 10;
            var spd = new (string name, int at, int loop, int nmi)[] { ("A", 0x1240, LoopA, CartNmi), ("A fast", 0x1242, LoopAFast, CartNmiFast), ("B", 0x1244, LoopB, CartNmi),
                                                                         ("B fast", 0x1246, LoopBFast, CartNmiFast), ("FastROM A", 0x1248, LoopAFastRom, CartNmi), ("FastROM A fast", 0x124A, LoopAFast, CartNmiFast) };
            Check(spd.All(p => Near(Wd(p.at), PredictedCount(p.loop, p.nmi), 0.005)), "test 6's loop counts are spec 2's cycle model's, within 0.5% (computed here, not by the cartridge)",
                  string.Join(", ", spd.Select(p => $"{p.name} {Wd(p.at)} ({PredictedCount(p.loop, p.nmi):F0})")));
            int dma = Wd(0x124C), dmaFast = Wd(0x124E), dmaCpu = Wd(0x1250);
            Check(dma >= VblankClocks / 8 - 1536 && dma <= VblankClocks / 8 + 512 && dmaFast >= 1.8 * dma && Math.Abs(dmaCpu - dma) <= 512,
                  "test 6's DMA bytes a vblank: about 6 KB, nearly twice with fast DMA, as normal with fast CPU alone", $"{dma:N0} / {dmaFast:N0} / {dmaCpu:N0}");
            var t7 = w.AsSpan(0x1280, 46).ToArray();
            Check(t7.Select((b, i) => i == 24 ? b == 'S' : b == 'P').All(x => x), "test 7: every memory item passed (sound RAM bank 0, the SPC700's, skipped)",
                  new string(t7.Select(b => (char)b).ToArray()));
            Check(w[0x1140] == 0x40 && w[0x1141] == 0x80 && w[0x1142] == 0 && w[0x1143] == 0 && w[0x1144] == 0,
                  "test 2 read WMOBJSTAT $40 / $80 / $00 / $00 / $00 for 72 sprites, 240 slivers, 64 sprites, 128 slivers, none", string.Join(" ", w.AsSpan(0x1140, 5).ToArray().Select(b => $"{b:X2}")));
            Check(voicesAlone.Count == 32 && mostAtOnce <= 2, "test 5's 32 voices, one after another: the DSP itself shows each of the 32 sounding, never more than one more alongside",
                  $"{voicesAlone.Count} seen, at most {mostAtOnce} at once");
            Check(audible > 150, "test 5 is heard (the audio is not silent while it plays)", $"{audible} frames");
            Check(w[0x1200] == 0x57 && w.AsSpan(0x1201, 8).ToArray().All(b => b == 0xFF) && w[0x1209] == 1 && w[0x120A] == 1 && w[0x120B] == 1,
                  "test 5's results: WID $57, all 32 voices alone and together, the echo off and on, the PCM address");
        }

        Console.WriteLine("TC.2 Every picture page against the model's picture (the menu and the pages driven by the pad; live pages SELECT to still)");
        using (var doc = JsonDocument.Parse(File.ReadAllText(json)))
        {
            foreach (var group in doc.RootElement.GetProperty("pages").EnumerateArray().GroupBy(p => p.GetProperty("test").GetInt32()))
            {
                using var s = Session(ConsoleKind.WideMess, wms);
                int test = group.Key;
                if (!OpenTest(s, test)) { Check(false, $"test {test} opens from the menu", $"screen {s.Board.Wram[TcScreen]}"); continue; }
                int block = 0x1100 + (test - 1) * 0x40 + 0x10;
                foreach (var p in group)
                {
                    int index = p.GetProperty("index").GetInt32(), number = p.GetProperty("page").GetInt32();
                    string name = p.GetProperty("name").GetString()!;
                    if (index > 0) Tap(s, PadButtons.Right);
                    if (!WaitRam(s, block, (byte)number)) { Check(false, $"test {test}: page {name} shows", $"page {s.Board.Wram[block]}"); continue; }
                    if (p.GetProperty("live").GetBoolean())
                    {
                        Run(s, 30);
                        uint[] a = Snapshot(s); Run(s, 8); uint[] b = Snapshot(s);
                        Check(!a.SequenceEqual(b), $"test {test}: {name} is live (it moves within 8 frames)");
                        Tap(s, PadButtons.Select);
                        WaitRam(s, block + 1, 1);
                    }
                    Run(s, 3);
                    ComparePicture(s, dir, p.GetProperty("picture").GetString()!, $"test {test}: {name}{(p.GetProperty("live").GetBoolean() ? " (still)" : "")}, the model's picture");
                    foreach (var v in p.GetProperty("variants").EnumerateArray())
                    {
                        Tap(s, PadButtons.Select);
                        WaitRam(s, block + 2, (byte)v.GetProperty("select").GetInt32());
                        Run(s, 3);
                        ComparePicture(s, dir, v.GetProperty("picture").GetString()!, $"test {test}: {name}, variant {v.GetProperty("name").GetString()}");
                    }
                }
            }
        }
        // test 1, measured from outside: 400 x 224, the eight layers each drawing their bank's colours (milestone 1's measure), between the SNES picture's bands
        using (var s = Session(ConsoleKind.WideMess, wms))
        {
            if (OpenTest(s, 1))
            {
                Run(s, 10);
                var ppu = s.WideMessPpu!;
                Check(s.FrameWidth == 400 && s.FrameHeight == 224, "test 1 is 400 x 224", $"{s.FrameWidth} x {s.FrameHeight}");
                uint[] f0 = Snapshot(s);
                bool bands = Enumerable.Range(0, 24).All(y => Enumerable.Range(0, 72).All(x => f0[y * 400 + x] == 0xFF000000u)) && Enumerable.Range(24, 184).Any(y => f0[y * 400] != 0xFF000000u);
                Check(bands, "its top band is the SNES picture (256 wide, black either side) and the lines below it are 400 wide (WMCTRL by HDMA, line by line)");
                var counts = new int[8];
                for (int layer = 0; layer < 8; layer++)
                {
                    ppu.WriteWide(0xC0, 0x03); ppu.WriteWide(0xC1, (byte)(1 << layer));
                    for (int line = 1; line <= 224; line++) ppu.RenderLine(line);
                    var seen = new HashSet<uint>();
                    for (int y = 24; y < 208; y++) for (int x = 0; x < 400; x++) seen.Add(ppu.WideBuffer[y * 400 + x]);
                    counts[layer] = seen.Count - 1;
                }
                ppu.WriteWide(0xC1, 0xFF);
                Check(counts.All(c => c >= 240), "each of the 8 layers alone draws (nearly) all the colours of its bank (its 255, less the legend's 15 in bank 0)", string.Join(" ", counts));
                Run(s, 1);
                Check(!Snapshot(s).SequenceEqual(f0), "the layers scroll");
                Tap(s, PadButtons.A);       // layer 1 off
                Run(s, 2);
                Check(s.WideMessPpu!.MainLayers == 0xFE && s.Board.Wram[0x1106] == 0xFE, "A switches the chosen layer off (WMTM $FE, the results table says so)", $"WMTM {s.WideMessPpu.MainLayers:X2}");
            }
            else Check(false, "test 1 opens from the menu");
        }

        Console.WriteLine("TC.3 Negative controls: the cartridge's SNES image on a SNES; the cartridge with WMFEAT masked by its own ROM byte ($FF87)");
        using (var s = Session(ConsoleKind.Snes, File.ReadAllBytes(sfc)))
        {
            Run(s, 60);
            var w = s.Board.Wram;
            var colours = Snapshot(s).Distinct().Count();
            Check(s.WideMess == null && w[TcState] == 4 && w[0x1010] == 0x42 && w[0x100C] == 0, "on a SNES it finds no WideMess (WMID open bus $42, state 4) and keeps the table", $"state {w[TcState]}, WMID {w[0x1010]:X2}");
            Check(s.FrameWidth == 256 && colours >= 4 && Enumerable.Range(0, 8).All(i => w[TcStatus + i] == 0), "it shows its 'this is a WideMess cartridge' screen (the SNES picture, text in 4+ colours) and runs no test", $"{s.FrameWidth} wide, {colours} colours");
        }
        Check(image.Length == 512 * 1024 && File.ReadAllBytes(sfc).SequenceEqual(image) && header!.Map == 2 && header.Required == 0, "the .wms is the SNES image on the WideMess map (512 KB), needing no feature (it reads WMFEAT itself)");
        foreach (var (mask, want, what) in new[] { ((byte)0xFB, "PPVVNPPP", "no sound+ (bit 2): test 5 not available"), ((byte)0x01, "PNNNNNPP", "the picture alone (bit 0): tests 2-6 not available, 7 tests what there is") })
        {
            byte[] masked = (byte[])image.Clone(); masked[TcMaskOffset] = mask;
            using var s = Session(ConsoleKind.WideMess, WmsFile.Pack(masked, "WideMess test cartridge, masked", 2, 0));
            var w = s.Board.Wram;
            for (int f = 1; f <= 4000 && w[TcState] != 3; f++) { s.SetPad(0, f <= 30 ? PadButtons.Select : PadButtons.None); RunFramePeak(s); }
            string status = new string(Enumerable.Range(0, 8).Select(i => (char)w[TcStatus + i]).ToArray());
            int failed = Enumerable.Range(0, 8).Sum(i => w[TcFails + i]);
            Check(w[0x1014] == (WmsExtension.Features & mask) && status == want && failed == 0, $"WMFEAT AND ${mask:X2}, {what}, the rest pass", $"features used {w[0x1014]:X2}, {status}, {failed} failed");
            if (mask == 0xFB)
            {
                Run(s, 30);                                    // the summary up
                Tap(s, PadButtons.B);                          // to the menu
                WaitRam(s, TcScreen, 1);
                Run(s, 30);
                for (int k = 0; k < 4; k++) Tap(s, PadButtons.Down);
                Tap(s, PadButtons.A);
                WaitRam(s, TcScreen, 12);
                Check(s.Board.Wram[TcScreen] == 12 && s.Board.Cpu.PBR == 0, "opening test 5 then shows its 'not available' screen (screen 12) and plays nothing", $"screen {s.Board.Wram[TcScreen]}");
            }
        }
        {
            byte[] masked = (byte[])image.Clone(); masked[TcMaskOffset] = 0x00;
            using var s = Session(ConsoleKind.WideMess, WmsFile.Pack(masked, "WideMess test cartridge, masked", 2, 0));
            Run(s, 60);
            Check(s.Board.Wram[TcState] == 5 && s.FrameWidth == 256, "WMFEAT AND $00 (no picture): it says so on the SNES picture (state 5) and runs nothing", $"state {s.Board.Wram[TcState]}, {s.FrameWidth} wide");
        }
        return true;
    }

    /// <summary>One frame; the loudest sample of its audio.</summary>
    private static int RunFramePeak(IConsoleSession s)
    {
        s.RunFrame();
        int n, peak = 0;
        while ((n = s.ReadSamples(audio)) > 0) for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs((int)audio[i]));
        return peak;
    }

    /// <summary>The voices whose envelope (ENVX) is above 0 right now: the S-DSP's 0-7 and, through the second page, 8-31 (WBANK put back after).</summary>
    private static List<int> SoundingVoices(DSP_SFC dsp)
    {
        var on = new List<int>();
        for (int v = 0; v < 8; v++) if (dsp.Regs[v * 16 + 8] != 0) on.Add(v);
        if (!WideSoundOn(dsp)) return on;
        byte bank = (byte)WideRead(dsp, 0x8C);
        for (int b = 0; b < 3; b++)
        {
            WideWrite(dsp, 0x8C, (byte)b);
            for (int v = 0; v < 8; v++) if (WideRead(dsp, 0x80 + v * 16 + 8) != 0) on.Add(8 + 8 * b + v);
        }
        WideWrite(dsp, 0x8C, bank);
        return on;
    }

    /// <summary>A button pressed for 2 frames, then 4 frames with nothing.</summary>
    private static void Tap(SnesSession s, PadButtons b)
    {
        s.SetPad(0, b); Run(s, 2);
        s.SetPad(0, PadButtons.None); Run(s, 4);
    }

    /// <summary>Frames until work RAM <paramref name="at"/> holds <paramref name="value"/> (10 seconds at most).</summary>
    private static bool WaitRam(SnesSession s, int at, byte value)
    {
        for (int f = 0; f < 600 && s.Board.Wram[at] != value; f++) Run(s, 1);
        return s.Board.Wram[at] == value;
    }

    /// <summary>From power-on: the title, START, the menu, DOWN (test - 1) times, A; until the test's screen is up and its checks are done.</summary>
    private static bool OpenTest(SnesSession s, int test)
    {
        Run(s, 20);
        Tap(s, PadButtons.Start);
        if (!WaitRam(s, TcScreen, 1)) return false;
        Run(s, 30);                                                                              // the menu set up
        for (int k = 1; k < test; k++) Tap(s, PadButtons.Down);
        Tap(s, PadButtons.A);
        if (!WaitRam(s, TcScreen, (byte)(test + 1))) return false;
        for (int f = 0; f < 900 && s.Board.Wram[TcStatus + test - 1] == 'R'; f++) Run(s, 1);   // its self-checks (test 2's) run first
        Run(s, 30);                                                                              // and its page set up again after them
        return true;
    }
}
