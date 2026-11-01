using System;
using System.IO;
using System.Linq;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// <c>--wms-test</c>'s milestone-4 checks (docs/projects/widemess-spec.md section 2): fast CPU, the 1 MB work RAM, the WideMess map and fast DMA, each against its test ROM
/// (WideMess/testroms/cpu, built into WideMess/testroms/bin) and a negative control. The ROMs write their measurements to work RAM ($7E:0100); these checks read them, and look at
/// the machine's memories themselves where they can.
/// </summary>
internal static partial class WideMessCli
{
    // The spec's timing model: a frame is 262 lines of 1,364 master clocks, of which the DRAM refresh takes 40 a line in both modes.
    private const int FrameClocks = 1364 * 262, RefreshClocks = 40 * 262;
    // speed.asm's loops, counted access by access (spec section 2: ROM and work RAM 8 clocks at SNES speed with SlowROM, internal cycles 6, the I/O register $4214 6; every
    // CPU cycle 2 clocks in fast mode except the I/O register, which keeps its 6):
    //   loop A = inc dp (16-bit) + lda dp (16-bit) + beq (taken): 6 ROM, 6 work RAM, 2 internal          -> 108 / 28
    //   loop B = loop A + lda $4214 (16-bit): + 3 ROM, 2 I/O                                             -> 144 / 46
    // and the NMI handler that ends each frame's count (entry, rep, pha, sep, lda $4210, rep, inc, pla, rti): 16 ROM, 16 work RAM, 11 internal, 1 I/O -> 328 / 92
    //   loop A from FastROM (MEMSEL = 1, bank $80): its 6 ROM cycles at 6 clocks                       -> 96 / 28
    private const int LoopA = 108, LoopAFast = 28, LoopB = 144, LoopBFast = 46, LoopAFastRom = 96, Nmi = 328, NmiFast = 92;
    private static double PredictedCount(int loop, int nmi) => (FrameClocks - RefreshClocks - nmi) / (double)loop;
    // general DMA's bytes in one vblank (lines 225-261, less the refresh): 8 or 4 clocks a byte
    private const int VblankClocks = 1364 * 37 - 40 * 37;

    private static bool TestCpuMemory(string dir)
    {
        string speed = Path.Combine(dir, "speed.wms"), speedNo = Path.Combine(dir, "speed_nounlock.wms"), speedSfc = Path.Combine(dir, "speed.sfc"), bigmap = Path.Combine(dir, "bigmap.wms");
        foreach (var f in new[] { speed, speedNo, speedSfc, bigmap }) if (!File.Exists(f)) { Console.Error.WriteLine($"missing test ROM {f} (build WideMess/testroms/cpu/build.ps1)"); return false; }

        Console.WriteLine("M4.1 Fast CPU and fast DMA (speed.wms, a LoROM game)");
        var on = SpeedRun(ConsoleKind.WideMess, File.ReadAllBytes(speed));
        if (on != null)
        {
            Check(on.Id == 0x57 && on.SpeedRead == 1, "the game unlocked WideMess and reads WMSPEED back", $"WMID {on.Id:X2}, WMSPEED {on.SpeedRead:X2}");
            double pa = PredictedCount(LoopA, Nmi), paf = PredictedCount(LoopAFast, NmiFast), pb = PredictedCount(LoopB, Nmi), pbf = PredictedCount(LoopBFast, NmiFast);
            Check(Near(on.A, pa, 0.005) && Near(on.B, pb, 0.005), "SNES speed: loops A and B run the iterations the SNES timing predicts (within 0.5%)", $"A {on.A} (predicted {pa:F0}), B {on.B} (predicted {pb:F0})");
            Check(Near(on.AFast, paf, 0.005) && Near(on.BFast, pbf, 0.005), "fast mode: 2 master clocks a cycle, the I/O register at its SNES 6 (within 0.5%)", $"A {on.AFast} (predicted {paf:F0}), B {on.BFast} (predicted {pbf:F0})");
            double ra = on.AFast / (double)on.A, rb = on.BFast / (double)on.B;
            Check(Near(ra, (double)LoopA / LoopAFast, 0.005) && Near(rb, (double)LoopB / LoopBFast, 0.005), "the speed-ups are the spec's: loop A 108/28 = 3.86x, loop B (one I/O read) 144/46 = 3.13x", $"A {ra:F3}x, B {rb:F3}x");
            double pr = PredictedCount(LoopAFastRom, Nmi);
            Check(Near(on.AFastRom, pr, 0.005) && Math.Abs(on.AFastRomFast - on.AFast) <= 1, "MEMSEL: from FastROM loop A takes 96 clocks at SNES speed, and in fast mode MEMSEL changes nothing (28 either way)",
                  $"FastROM {on.AFastRom} (predicted {pr:F0}), fast {on.AFastRomFast} vs {on.AFast}");
            int ideal = VblankClocks / 8, idealFast = VblankClocks / 4;
            Check(on.Dma <= ideal + 512 && on.Dma >= ideal - 1536, $"general DMA moves about 6 KB a vblank (8 clocks a byte: {ideal:N0} fit)", $"{on.Dma:N0} bytes in 512-byte transfers");
            Check(on.DmaFast <= idealFast + 512 && on.DmaFast >= idealFast - 2560 && on.DmaFast >= 1.8 * on.Dma, $"fast DMA moves about 12 KB a vblank (4 clocks a byte: {idealFast:N0} fit), nearly twice as much", $"{on.DmaFast:N0} bytes, {on.DmaFast / (double)on.Dma:F2}x");
            Check(Math.Abs(on.DmaFastCpu - on.Dma) <= 512, "fast CPU alone leaves DMA at 8 clocks a byte (only the CPU's own cycles between transfers speed up)", $"{on.DmaFastCpu:N0} bytes");
            Check(on.LoRomRam == 0, "a LoROM cartridge has no work RAM at $70-$7D (5 probes)", $"{on.LoRomRam} found");
            Check(on.PortWrap == 1, "on a LoROM cartridge the WRAM port keeps the SNES's 17 bits, unlocked ($2183 = $02 writes bank $7E)");
        }

        Console.WriteLine("M4.2 Negative controls: the speed ROM without the unlock, and on a SNES");
        var off = SpeedRun(ConsoleKind.WideMess, File.ReadAllBytes(speedNo));
        if (off != null)
        {
            Check(off.Id == 0x42 && off.SpeedRead == 0x42, "locked: WMID and WMSPEED read open bus", $"{off.Id:X2} {off.SpeedRead:X2}");
            Check(Math.Abs(off.AFast - off.A) <= 1 && Math.Abs(off.BFast - off.B) <= 1 && Math.Abs(off.AFastRomFast - off.AFastRom) <= 1, "locked: WMSPEED changes nothing (SNES timing)",
                  $"A {off.A}/{off.AFast}, B {off.B}/{off.BFast}, FastROM {off.AFastRom}/{off.AFastRomFast}");
            Check(Near(off.AFastRom, PredictedCount(LoopAFastRom, Nmi), 0.005), "locked: FastROM works as on a SNES (the control's FastROM count is the 96-clock one, so the fast-mode check above can fail)", $"{off.AFastRom}");
            Check(off.DmaFast == off.Dma, "locked: WMDMA changes nothing", $"{off.Dma:N0} / {off.DmaFast:N0} bytes");
            if (on != null) Check(Math.Abs(off.A - on.A) <= 1 && Math.Abs(off.B - on.B) <= 1 && off.Dma == on.Dma, "locked, the SNES-speed halves equal the unlocked game's within one iteration (the unlock alone changes no timing)");
            // the very same ROM image (the control's payload) on the SNES console: the locked WideMess must measure exactly what a SNES does
            var same = SpeedRun(ConsoleKind.Snes, WmsFile.Payload(File.ReadAllBytes(speedNo), out _));
            if (same != null) Check(same == off, "the control's image on the SNES console measures exactly what the locked WideMess measured");
        }
        var snes = SpeedRun(ConsoleKind.Snes, File.ReadAllBytes(speedSfc));
        if (snes != null)
            Check(snes.Id == 0x42 && Math.Abs(snes.AFast - snes.A) <= 1 && Math.Abs(snes.BFast - snes.B) <= 1 && Math.Abs(snes.AFastRomFast - snes.AFastRom) <= 1 && snes.DmaFast == snes.Dma && snes.DmaFastCpu == snes.Dma,
                  "the WideMess game's SNES image on a SNES: no WideMess (WMID open bus), both halves of every measurement equal", $"A {snes.A}/{snes.AFast}, B {snes.B}/{snes.BFast}, DMA {snes.Dma:N0}/{snes.DmaFast:N0}/{snes.DmaFastCpu:N0}");

        byte[] big = File.ReadAllBytes(bigmap);
        var bigHeader = WmsFile.Read(big);
        byte[] image = big.AsSpan(WmsFile.HeaderSize).ToArray();
        Console.WriteLine($"M4.3 The WideMess map (bigmap.wms: {image.Length / (1 << 20)} MB of ROM, {bigHeader.SramSize / 1024} KB of battery RAM)");
        using (var s = Session(ConsoleKind.WideMess, big))
        {
            var r = BigRun(s);
            if (r != null)
            {
                var w = s.Board.Wram;
                Check(s.Board.Cart.WideMessMap && s.Board.WramHigh?.Length == BOARD_SFC.WramHighBytes && r[0x01] == 0x57, "map 2: the cartridge has the WideMess map and the board 896 KB of work RAM above the SNES's 128 KB");
                Check(r[0x02] == 3 && r[0x03] == 1, "before the unlock: work RAM at $70/$77/$7D (the map is the cartridge's), and the WRAM port has 17 bits", $"{r[0x02]} of 3 found, port -> {(r[0x03] == 1 ? "$7E" : r[0x03] == 2 ? "$70" : "?")}");
                Check(r[0x04] == 2, "after the unlock the port takes 20 bits: work-RAM address $2:1000 is $70:1000", $"port -> {(r[0x04] == 1 ? "$7E" : r[0x04] == 2 ? "$70" : "?")}");
                int pattern = Word(r, 0x06), own = PatternErrors(s);
                Check(pattern == 0 && own == 0, "a pattern over all of $70-$7F (3,584 bytes) reads back, by the game and in the machine's memory", $"game {pattern}, machine {own} mismatches");
                Check(Word(r, 0x08) == 0 && r[0x0A] == 3 && r[0x0B] == 1, "the port: 256 bytes across $71:FF80-$72:007F, its wrap ($FFFFF then $00000 = $7D:FFFF, $7E:0000), the A bus read through it",
                      $"crossing {Word(r, 0x08)} mismatches, wrap bits {r[0x0A]}, A bus -> port {r[0x0B]}");
                Check(r[0x0C] == 3, "$00:0000-1FFF mirrors $7E:0000-1FFF, not $70", $"bits {r[0x0C]}");
                bool cgOk = Enumerable.Range(0, 256).All(i => s.Ppu.Cgram[i] == (((2 * i) ^ 0xC5) & 0x7F | ((((2 * i + 1) ^ 0xC5) & 0x7F) << 8)));
                Check(Word(r, 0x0E) == 0 && cgOk, "DMA reads the new work RAM ($75 -> colour RAM) and writes it (colour RAM -> $76), 512 bytes each way", $"{Word(r, 0x0E)} mismatches, colour RAM {(cgOk ? "right" : "WRONG")}");
                Check(r[0x10] == 0 && r[0x12] == 1, "battery RAM: 32 distinct banks of 8 KB at $20-$3F:6000-7FFF (256 KB), mirrored at $A0-$BF", $"{r[0x10]} bank mismatches, mirror {r[0x12]}");
                int hiA = Ones(w, 0x200, 64), hiC = Ones(w, 0x240, 64), loC = Ones(w, 0x280, 64), midA = Ones(w, 0x2C0, 48);
                Check(hiA == 64 && hiC == 64, "all 64 ROM banks (4 MB) at $C0-$FF: each bank's markers at :0000 and :C000", $"{hiA} and {hiC} of 64");
                Check(loC == 64 && midA == 48, "the mirrors: $00-$3F:8000-FFFF (64 upper halves) and $40-$6F (48 banks)", $"{loC} of 64, {midA} of 48");
                // after the checks the game turns fast mode on and counts its idle loop each frame into $7B, and writes each frame's number through the 20-bit port at $7C:1000 + frame
                var hi = s.Board.WramHigh!;
                int frame = w[0x16] | w[0x17] << 8;
                int last = Word(hi, (0x7B - 0x70) << 16 | 2 * ((frame - 1) & 0xFF));
                Check(s.Board.FastCpu && last > 15000, "then fast mode: its idle loop (76 clocks at SNES speed, 20 fast) runs about 17,000 times a frame", $"{last:N0} in the last frame");
                bool viaPort = frame >= 2 && Enumerable.Range(0, frame).All(f => hi[(0x7C - 0x70) << 16 | 0x1000 + f] == f);
                Check(viaPort, "and each frame's number reaches $7C:1000 + frame through the 20-bit port (work-RAM address $E1000 + frame)", $"{frame} frames");
                Check(s.HasBattery && s.ExportSave().Length == 256 * 1024 && r[0x11] == 1, "the battery RAM is the game's save (HasBattery, 256 KB exported), first boot", $"boot count {r[0x11]}");
                byte[] save = s.ExportSave();
                using var again = Session(ConsoleKind.WideMess, big);
                again.ImportSave(save);
                var r2 = BigRun(again);
                Check(r2 != null && r2[0x11] == 2, "the save brought back into a new session: the game counts its second boot", $"boot count {(r2 != null ? r2[0x11] : -1)}");
            }
        }

        Console.WriteLine("M4.4 Save states carry the WideMess map's work RAM, the 20-bit port and fast mode");
        using (var s = Session(ConsoleKind.WideMess, big))
        {
            Run(s, 100);
            byte[] state = s.SaveState();
            Run(s, 30);
            ulong a = MemoryHash(s);
            s.LoadState(state);
            bool fast = s.Board.FastCpu;
            Run(s, 30);
            ulong b = MemoryHash(s);
            Check(fast && a == b, "save at frame 100, run 30, load, run 30: the same work RAM (128 KB + 896 KB), battery RAM and per-frame loop counts", $"{state.Length / 1024} KB state, fast mode after the load: {fast}");
            // the control: a state that lost fast mode is told apart (the per-frame counts in $7B differ)
            s.LoadState(state);
            s.Board.WideMess!.WriteCpu(0x4225, 0);
            Run(s, 30);
            Check(MemoryHash(s) != a, "control: the same state with fast mode switched off runs differently (so the check above can fail)");
        }

        Console.WriteLine("M4.5 Negative controls of the WideMess map");
        using (var s = Session(ConsoleKind.WideMess, WmsFile.Pack(image[..(2 << 20)], "WideMess big map cut to 2 MB", 2, 16, bigHeader.SramSize, true)))
        {
            var r = BigRun(s);
            if (r != null)
            {
                var w = s.Board.Wram;
                bool low = Enumerable.Range(0, 32).All(n => w[0x200 + n] == 1 && w[0x240 + n] == 1 && w[0x280 + n] == 1 && w[0x2C0 + n] == 1);
                bool high = Enumerable.Range(32, 32).All(n => w[0x200 + n] == 0 && w[0x240 + n] == 0 && w[0x280 + n] == 0) && Enumerable.Range(32, 16).All(n => w[0x2C0 + n] == 0);
                Check(low && high, "the same game cut to 2 MB: banks 0-31 are found, banks 32-63 are not (they mirror 0-31), so the 4 MB check can fail");
            }
        }
        using (var s = Session(ConsoleKind.WideMess, WmsFile.Pack(image, "WideMess big map on HiROM", 1, 0)))
        {
            var r = BigRun(s);
            if (r != null)
                Check(s.Board.WramHigh == null && r[0x02] == 0 && r[0x04] == 1 && Word(r, 0x06) >= 14 * 224 && Word(r, 0x08) == 0xFFFF,
                      "the same image on the HiROM map: $70-$7D are ROM (no work RAM), the port stays 17 bits after the unlock, the pattern fails there",
                      $"RAM {r[0x02]} of 3, port -> {(r[0x04] == 1 ? "$7E" : "$70")}, pattern {Word(r, 0x06)} mismatches");
        }
        Refused(WmsFile.Pack(image, "x", 0, 16), "the 4 MB HiROM image labelled LoROM (the map disagrees with the image)", "but the image is a HiROM image");
        Refused(WmsFile.Pack(image.Concat(new byte[0x10000]).ToArray(), "x", 2, 16), "a WideMess-map game over 4 MB", "4 MB");
        Refused(WmsFile.Pack(image[..(image.Length - 0x8000)], "x", 2, 16), "a WideMess-map ROM that is not whole 64 KB banks", "64 KB banks");
        Refused(WmsFile.Pack(image, "x", 2, 16, 512 * 1024, true), "WideMess-map battery RAM over 256 KB", "256 KB");
        Refused(WmsFile.Pack(image, "x", 2, 16, 3000, true), "WideMess-map battery RAM that is not a power of two", "power of two");
        Refused(WmsFile.Pack(image, "x", 2, 16, 0, true), "a battery without battery RAM", "battery");
        Refused(WmsFile.Pack(image[..0x8000], "x", 2, 16), "a 32 KB image on the WideMess map (less than one bank)", "64 KB");

        Console.WriteLine("M4.6 No allocation per frame on the new paths");
        long speedBytes = AllocatedOver(File.ReadAllBytes(speed), 40), bigBytes = AllocatedOver(big, 60);
        Check(speedBytes == 0 && bigBytes == 0, "the speed ROM's 40 frames (mode switches, fast DMA, the port) and the big map's 60 (its checks, then fast mode and the 20-bit port every frame) allocate nothing",
              $"{speedBytes} and {bigBytes} bytes");
        return true;
    }

    /// <summary>Managed bytes this thread allocates while a fresh session runs <paramref name="frames"/> frames (its first frame, which sets the session up, is not counted).</summary>
    private static long AllocatedOver(byte[] rom, int frames)
    {
        using var s = Session(ConsoleKind.WideMess, rom);
        Run(s, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(s, frames);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private sealed record SpeedResult(byte Id, byte SpeedRead, int A, int AFast, int B, int BFast, int Dma, int DmaFast, int DmaFastCpu, byte LoRomRam, byte PortWrap, int AFastRom, int AFastRomFast);

    /// <summary>Runs speed.asm until it says it is done ($7E:01FF = $A5) and reads its results.</summary>
    private static SpeedResult? SpeedRun(ConsoleKind console, byte[] rom)
    {
        using var s = Session(console, rom);
        var w = s.Board.Wram;
        for (int f = 0; f < 120 && w[0x1FF] != 0xA5; f++) Run(s, 1);
        if (w[0x1FF] != 0xA5) { Check(false, $"the speed ROM finishes on the {Consoles.DisplayName(console)}", "it did not reach its end in 120 frames"); return null; }
        int Wd(int o) => w[0x100 + o] | w[0x101 + o] << 8;
        var r = new SpeedResult(w[0x100], w[0x101], Wd(0x02), Wd(0x04), Wd(0x06), Wd(0x08), Wd(0x0A), Wd(0x0C), Wd(0x0E), w[0x110], w[0x111], Wd(0x12), Wd(0x14));
        Console.WriteLine($"     {Consoles.DisplayName(console)}: loop A {r.A} / {r.AFast} fast (FastROM {r.AFastRom} / {r.AFastRomFast} fast), loop B {r.B} / {r.BFast} fast, DMA {r.Dma:N0} / {r.DmaFast:N0} fast DMA / {r.DmaFastCpu:N0} fast CPU");
        return r;
    }

    /// <summary>Runs bigmap.asm until it says it is done and returns its results block ($7E:0100-01FF).</summary>
    private static byte[]? BigRun(SnesSession s)
    {
        var w = s.Board.Wram;
        for (int f = 0; f < 120 && w[0x1FF] != 0xA5; f++) Run(s, 1);
        if (w[0x1FF] != 0xA5) { Check(false, "the big-map ROM finishes", "it did not reach its end in 120 frames"); return null; }
        Run(s, 3);   // a few frames of the idle loop (fast mode, the per-frame counts)
        return w.AsSpan(0x100, 0x100).ToArray();
    }

    /// <summary>The pattern bigmap.asm writes over $70-$7F, checked in the machine's memories: [b:hh00] = b ^ hh ^ $3C for hh = $20-$FF.</summary>
    private static int PatternErrors(SnesSession s)
    {
        var hi = s.Board.WramHigh!; var w = s.Board.Wram;
        int bad = 0;
        for (int b = 0x70; b < 0x80; b++)
            for (int hh = 0x20; hh < 0x100; hh++)
            {
                int off = hh << 8;
                byte v = b < 0x7E ? hi[(b - 0x70) << 16 | off] : w[(b - 0x7E) << 16 | off];
                if (v != (byte)(b ^ hh ^ 0x3C)) bad++;
            }
        return bad;
    }

    private static ulong MemoryHash(SnesSession s)
    {
        ulong h = 14695981039346656037UL;
        foreach (var arr in new[] { s.Board.Wram, s.Board.WramHigh ?? Array.Empty<byte>(), s.Board.Cart.Sram })
            foreach (byte v in arr) h = (h ^ v) * 1099511628211UL;
        return h;
    }

    private static int Word(byte[] a, int o) => a[o] | a[o + 1] << 8;
    private static int Ones(byte[] a, int o, int n) { int c = 0; for (int i = 0; i < n; i++) if (a[o + i] == 1) c++; return c; }
    private static bool Near(double got, double want, double tol) => Math.Abs(got / want - 1) <= tol;
}
