using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-run &lt;rom&gt; [--frames N] [--region ntsc|pal] [--model 1|2|3] [--apu id] [--input "60:Start,90:Right+A,120:"] [--every N] [--png out.png] [--palette 0..3] [--trace out.txt]
/// [--trace-pc] [--trace-reads] [--trace-from N] [--trace-frames N] [--hash-at 60,120] [--pic-hash-at 60,120] [--png-at 60,120 --png-dir d] [--vram-png out.png] [--plane-png out.png] [--ram-out ram.bin] [--vdp-out dir] [--wav out.wav] [--vgm out.vgm] [--sound-report] [--alloc-probe] [--force] [--quiet]</c>: runs a Genesis ROM headless on <see cref="BOARD_MD"/> and reports what the machine ended up in: the 68000, the VDP registers,
/// VRAM, CRAM and VSRAM (with hashes you can compare between runs), the Z80 and the sound unit, the pads. <c>--png</c> writes the picture of the last frame (the VDP's output at its real size), <c>--png-at 60,120 --png-dir d</c> one picture per listed frame
/// (<c>d000060.png</c>), <c>--pic-hash-at 60,120</c> prints the picture's hash at those frames, <c>--vram-png</c> the VDP's VRAM/CRAM debug view and <c>--plane-png</c> a dump of the two scroll planes' first 40 by 28 cells (no scroll, window, sprites or priority). <c>--ram-out</c> writes the 64 KB of work RAM (test ROMs keep their results there), <c>--trace-reads</c> adds the 68000's reads outside ROM and RAM to a trace, <c>--force</c> runs a ROM without the SEGA header as a Genesis one.
/// <c>--trace</c> writes a line per VDP, Z80-area, I/O, BUSREQ, RESET and PSG write (and per instruction with <c>--trace-pc</c>), tagged with frame and line, in a form two runs can be diffed in.
/// Exit code 0 = ran every frame without a halt or a 68000 address error; 1 = the CPU halted or took an address error (listed); 2 = usage or file error.
/// </summary>
internal static class MdRunCli
{
    public static int Run(string[] args)
    {
        string? rom = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : M68kTestCli.Arg(args, "--rom");
        if (rom == null) { Console.WriteLine("usage: --md-run <rom> [--frames N] [--region ntsc|pal] [--model 1|2|3] [--apu id] [--input script] [--every N] [--png out.png] [--palette n] [--trace out.txt] [--trace-pc] [--trace-reads] [--trace-from N] [--trace-frames N] [--hash-at list] [--pic-hash-at list] [--png-at list --png-dir dir] [--vram-png out.png] [--plane-png out.png] [--ram-out ram.bin] [--vdp-out dir] [--force] [--quiet]"); return 2; }
        int frames = int.TryParse(M68kTestCli.Arg(args, "--frames"), out int f) ? f : 120;
        int every = int.TryParse(M68kTestCli.Arg(args, "--every"), out int ev) ? ev : 0;
        int palette = int.TryParse(M68kTestCli.Arg(args, "--palette"), out int pal) ? pal : 0;
        bool quiet = M68kTestCli.Flag(args, "--quiet");
        string? png = M68kTestCli.Arg(args, "--png"), tracePath = M68kTestCli.Arg(args, "--trace");
        bool tracePc = M68kTestCli.Flag(args, "--trace-pc");
        int traceFrames = int.TryParse(M68kTestCli.Arg(args, "--trace-frames"), out int tf) ? tf : int.MaxValue;
        int traceFrom = int.TryParse(M68kTestCli.Arg(args, "--trace-from"), out int tfr) ? tfr : 0;
        var hashAt = new HashSet<int>((M68kTestCli.Arg(args, "--hash-at") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse));
        var picHashAt = new HashSet<int>((M68kTestCli.Arg(args, "--pic-hash-at") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse));
        var pngAt = new HashSet<int>((M68kTestCli.Arg(args, "--png-at") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse));
        string? pngDir = M68kTestCli.Arg(args, "--png-dir");
        if (pngDir != null) Directory.CreateDirectory(pngDir);
        // --sheet out.png --sheet-at 100,200,300: a contact sheet of the picture at those frames (4 across, half size, frame numbers on them), for looking at a whole run at once
        string? sheetPath = M68kTestCli.Arg(args, "--sheet");
        var sheetAt = new HashSet<int>((M68kTestCli.Arg(args, "--sheet-at") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse));
        var sheetFrames = new List<(int frame, Bitmap bmp)>();
        string? padScript = M68kTestCli.Arg(args, "--pad-script")?.ToLowerInvariant();
        string apu = M68kTestCli.Arg(args, "--apu") ?? "MD";
        RegionChoice? region = M68kTestCli.Arg(args, "--region")?.ToLowerInvariant() switch { "ntsc" => RegionChoice.Ntsc, "pal" => RegionChoice.Pal, _ => null };
        GenesisModelChoice? model = M68kTestCli.Arg(args, "--model") switch { "1" => GenesisModelChoice.Model1, "2" => GenesisModelChoice.Model2, "3" => GenesisModelChoice.Model3, _ => null };
        var inputs = ParseInput(M68kTestCli.Arg(args, "--input"));

        byte[] data;
        try { data = File.ReadAllBytes(rom); }
        catch (Exception e) { Console.WriteLine($"cannot read {rom}: {e.Message}"); return 2; }
        if (SegaRomFormat.Detect(data, rom) != ConsoleKind.Genesis && !M68kTestCli.Flag(args, "--force")) { Console.WriteLine($"{rom} is not a Genesis ROM (no SEGA header at $100); --force runs it as one anyway (a headerless test ROM; the console itself does not check the header before the TMSS)"); return 2; }

        MdSession session;
        try { session = new MdSession(data, "M68K", "MD", apu, region, model); }
        catch (Exception e) { Console.WriteLine($"cannot start: {e.Message}"); return 2; }
        session.DebugPalette = palette;
        string? vramPng = M68kTestCli.Arg(args, "--vram-png");
        var board = session.Board;
        string? wavOut = M68kTestCli.Arg(args, "--wav"), vgmOut = M68kTestCli.Arg(args, "--vgm");
        var audio = wavOut != null || vgmOut != null || M68kTestCli.Flag(args, "--sound-report") ? new MdAudioCapture(board) : null;      // B2b: collects the sound (WAV, VGM, measurements)
        Console.WriteLine($"== {Path.GetFileName(rom)}: {session.Description}");
        Console.WriteLine($"   header: '{board.Cart.OverseasTitle}' serial {board.Cart.Serial} regions '{board.Cart.Regions}' rom {board.Cart.RomLength:N0} bytes, checksum header {board.Cart.HeaderChecksum:X4} computed {board.Cart.ComputedChecksum:X4}, sram {(board.Cart.HasSramHeader ? $"${board.Cart.SramStart:X6}-${board.Cart.SramEnd:X6} ({board.Cart.Sram.Length} bytes)" : "none")}");

        StreamWriter? trace = null;
        // --cart-trace out.txt: only the 68000's accesses to $200000-$3FFFFF (the I2C EEPROM and mapper area), small enough to read for a whole run
        string? cartTracePath = M68kTestCli.Arg(args, "--cart-trace");
        StreamWriter? cartTrace = null;
        if (cartTracePath != null)
        {
            cartTrace = new StreamWriter(cartTracePath, false, new UTF8Encoding(false));
            board.CartTrace = s => cartTrace.WriteLine($"f{board.Frames} l{board.Vdp.Line} {s}");
        }
        if (tracePath != null)
        {
            trace = new StreamWriter(tracePath, false, new UTF8Encoding(false));
            board.WriteTrace = s => { if (board.Frames >= traceFrom && board.Frames < traceFrames) trace.WriteLine($"f{board.Frames} l{board.Vdp.Line} {s}"); };
            board.Vdp.Trace = s => { if (board.Frames >= traceFrom && board.Frames < traceFrames) trace.WriteLine($"f{board.Frames} l{board.Vdp.Line}   vdp {s}"); };
            if (M68kTestCli.Flag(args, "--trace-reads")) board.ReadTrace = s => { if (board.Frames >= traceFrom && board.Frames < traceFrames) trace.WriteLine($"f{board.Frames} l{board.Vdp.Line} {s}"); };
            if (M68kTestCli.Flag(args, "--trace-cart")) board.CartTrace = s => { if (board.Frames >= traceFrom && board.Frames < traceFrames) trace.WriteLine($"f{board.Frames} l{board.Vdp.Line} {s}"); };
            if (tracePc) board.InstructionHook = pc => { if (board.Frames >= traceFrom && board.Frames < traceFrames) trace.WriteLine($"f{board.Frames} l{board.Vdp.Line} pc {pc:X6}"); };
            if (board.Sound is MdSoundUnit unit)
            {
                unit.YmWriteHook = (reg, v, part) => { if (board.Frames >= traceFrom && board.Frames < traceFrames) trace.WriteLine($"f{board.Frames} l{board.Vdp.Line}   ym{part} {reg:X2}={v:X2}"); };
            }
        }

        if (M68kTestCli.Flag(args, "--alloc-probe"))
        {
            // where the bytes go: the board's frame, the picture copy, the sound drain
            long aBoard = 0, aCopy = 0, aDrain = 0; int counted = 0;
            var scratch = new uint[MdVdp.PictureStride * MdVdp.PictureMaxHeight];
            for (int i = 0; i < frames; i++)
            {
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                board.RunFrame();
                long a1 = GC.GetAllocatedBytesForCurrentThread();
                board.Vdp.CopyPicture(scratch);
                long a2 = GC.GetAllocatedBytesForCurrentThread();
                DrainSamples(session);
                long a3 = GC.GetAllocatedBytesForCurrentThread();
                if (i >= frames / 2) { aBoard += a1 - a0; aCopy += a2 - a1; aDrain += a3 - a2; counted++; }
            }
            Console.WriteLine($"   bytes per frame: board.RunFrame {aBoard / (double)Math.Max(1, counted):F1}, picture copy {aCopy / (double)Math.Max(1, counted):F1}, sound drain {aDrain / (double)Math.Max(1, counted):F1}");
            return 0;
        }
        // --save-in file: start with this battery image (SRAM or EEPROM bytes, as the session saves them); --save-out file: write the image at the end
        string? saveIn = M68kTestCli.Arg(args, "--save-in"), saveOut = M68kTestCli.Arg(args, "--save-out");
        if (saveIn != null) { try { session.ImportSave(File.ReadAllBytes(saveIn)); Console.WriteLine($"   loaded {saveIn} into the battery memory"); } catch (Exception e) { Console.WriteLine($"   cannot load {saveIn}: {e.Message}"); return 2; } }
        var sw = Stopwatch.StartNew();
        PadButtons pad = PadButtons.None;
        long halts = 0;
        long allocAt = 0; int allocFrom = frames / 2;
        for (int i = 0; i < frames; i++)
        {
            if (i == allocFrom) allocAt = GC.GetAllocatedBytesForCurrentThread();
            if (padScript != null) pad = MdSweepCli.PadAt(padScript, i);       // the sweep's scripts (default, mash, start, none), for looking at what the sweep saw
            if (inputs.TryGetValue(i, out var b)) pad = b;
            session.SetPad(0, pad);
            session.RunFrame();
            // sound is produced by the board's own clock; drain it so the PSG's buffer does not grow
            if (audio != null) audio.Drain(session); else DrainSamples(session);
            if (board.Cpu.Halted) { halts++; break; }
            if (every > 0 && (i + 1) % every == 0 && !quiet) Console.WriteLine($"   frame {i + 1,5}: {Summary(board)}");
            if (hashAt.Contains(i + 1)) Console.WriteLine($"   frame {i + 1,5} hash: {StateHash(board):X16}");
            if (picHashAt.Contains(i + 1)) Console.WriteLine($"   frame {i + 1,5} picture {session.FrameWidth}x{session.FrameHeight} hash: {PicHash(session):X16}");
            if (pngAt.Contains(i + 1) && pngDir != null) WritePng(session, Path.Combine(pngDir, $"f{i + 1:D6}.png"));
            if (sheetPath != null && sheetAt.Contains(i + 1)) sheetFrames.Add((i + 1, ToBitmap(session.Frame, session.FrameWidth, session.FrameHeight)));
        }
        sw.Stop();
        if (sheetPath != null && sheetFrames.Count > 0)
        {
            try { WriteSheet(sheetFrames, sheetPath); Console.WriteLine($"   wrote contact sheet {sheetPath} ({sheetFrames.Count} frames)"); }
            catch (Exception e) { Console.WriteLine($"   sheet failed: {e.Message}"); }
            foreach (var (_, bmp) in sheetFrames) bmp.Dispose();
        }
        trace?.Dispose();
        cartTrace?.Dispose();

        if (frames > 2) Console.WriteLine($"   allocated {(GC.GetAllocatedBytesForCurrentThread() - allocAt) / (double)Math.Max(1, frames - allocFrom):F1} bytes per frame over the second half of the run");
        Console.WriteLine($"   ran {board.Frames} frames in {sw.Elapsed.TotalSeconds:F2} s ({board.Frames / Math.Max(0.001, sw.Elapsed.TotalSeconds):F0} frames/s, {board.Cpu.Instructions:N0} 68000 instructions, {board.Cpu.Cycles:N0} clocks)");
        Console.WriteLine($"   {Summary(board)}");
        Console.WriteLine($"   state hash {StateHash(board):X16} | vram {Hash(board.Vdp.Vram):X16} cram {Hash(board.Vdp.Cram):X16} vsram {Hash(board.Vdp.Vsram):X16} regs {Hash(board.Vdp.Reg):X16}");
        if (!quiet) Dump(board);
        if (board.Cart.Eeprom is { } ee) Console.WriteLine($"   EEPROM {ee.Chip} ({board.Cart.EepromWiring}): starts {ee.Starts}, stops {ee.Stops}, bytes in {ee.BytesIn}, out {ee.BytesOut}, committed writes {ee.Commits}, device bytes [{string.Join(" ", Enumerable.Range(0, 256).Where(k => ee.DeviceHistogram[k] > 0).Select(k => $"{k:X2}x{ee.DeviceHistogram[k]}"))}], highest address {ee.MaxAddress}, non-$FF bytes {ee.Data.Count(x => x != 0xFF)}/{ee.Data.Length}, hash {Hash(ee.Data):X16}");
        else if (board.Cart.EepromUnsupported != null) Console.WriteLine($"   EEPROM: {board.Cart.EepromUnsupported}");
        if (saveOut != null && session.HasBattery) { File.WriteAllBytes(saveOut, session.ExportSave()); Console.WriteLine($"   wrote the battery image ({session.ExportSave().Length} bytes, hash {Hash(session.ExportSave()):X16}) to {saveOut}"); }
        audio?.Finish(wavOut, vgmOut, Path.GetFileNameWithoutExtension(rom));
        if (png != null)
        {
            try { WritePng(session, png); Console.WriteLine($"   wrote {png} ({session.FrameWidth}x{session.FrameHeight})"); }
            catch (Exception e) { Console.WriteLine($"   png failed: {e.Message}"); }
        }
        if (vramPng != null)
        {
            try { session.DebugView = true; session.DebugPalette = palette; board.Vdp.RenderDebugView(session.Frame, palette); WritePngFrom(session.Frame, MdSession.DebugWidth, MdSession.DebugHeight, vramPng); session.DebugView = false; Console.WriteLine($"   wrote {vramPng} (VRAM tiles in palette line {palette} and the 64 CRAM colours)"); }
            catch (Exception e) { Console.WriteLine($"   png failed: {e.Message}"); }
        }
        string? vdpOut = M68kTestCli.Arg(args, "--vdp-out");
        if (vdpOut != null)
        {
            try
            {
                Directory.CreateDirectory(vdpOut);
                File.WriteAllBytes(Path.Combine(vdpOut, "vram.bin"), board.Vdp.Vram);
                var cr = new byte[128]; for (int i = 0; i < 64; i++) { cr[i * 2] = (byte)(board.Vdp.Cram[i] >> 8); cr[i * 2 + 1] = (byte)board.Vdp.Cram[i]; }
                File.WriteAllBytes(Path.Combine(vdpOut, "cram.bin"), cr);
                var vs = new byte[128]; for (int i = 0; i < 64; i++) { vs[i * 2] = (byte)(board.Vdp.Vsram[i] >> 8); vs[i * 2 + 1] = (byte)board.Vdp.Vsram[i]; }
                File.WriteAllBytes(Path.Combine(vdpOut, "vsram.bin"), vs);
                File.WriteAllBytes(Path.Combine(vdpOut, "regs.bin"), board.Vdp.Reg.Take(24).ToArray());
                Console.WriteLine($"   wrote vram.bin, cram.bin, vsram.bin and regs.bin to {vdpOut}");
            }
            catch (Exception e) { Console.WriteLine($"   vdp dump failed: {e.Message}"); }
        }
        string? ramOut = M68kTestCli.Arg(args, "--ram-out");
        if (ramOut != null)
        {
            try { File.WriteAllBytes(ramOut, board.Ram); Console.WriteLine($"   wrote the 64 KB of work RAM to {ramOut}"); }
            catch (Exception e) { Console.WriteLine($"   ram dump failed: {e.Message}"); }
        }
        string? planePng = M68kTestCli.Arg(args, "--plane-png");
        if (planePng != null)
        {
            var img = new uint[320 * 224];
            board.Vdp.RenderPlaneDump(img);
            try { WritePngFrom(img, 320, 224, planePng); Console.WriteLine($"   wrote {planePng} (the two planes' first 40x28 cells, no scroll, no sprites: a debugging dump, not a renderer)"); }
            catch (Exception e) { Console.WriteLine($"   png failed: {e.Message}"); }
        }
        if (board.Cpu.Halted) Console.WriteLine("   RESULT: the 68000 HALTED (a double fault)");
        Console.WriteLine($"   address errors: {board.Cpu.AddressErrorCount}, halted: {board.Cpu.Halted}");
        return board.Cpu.Halted || board.Cpu.AddressErrorCount > 0 ? 1 : 0;
    }

    private static readonly short[] drainBuffer = new short[8192];

    private static void DrainSamples(MdSession s)
    {
        while (s.ReadSamples(drainBuffer) > 0) { }
    }

    private static string Summary(BOARD_MD b)
    {
        var v = b.Vdp;
        int vramNonZero = v.Vram.Count(x => x != 0), cramNonZero = v.Cram.Count(x => x != 0);
        return $"68000 pc={b.Cpu.Pc - 4:X6} sr={b.Cpu.ReadSr():X4} a7={b.Cpu.A[7]:X8} | vdp line {v.Line} {(v.H40 ? "H40" : "H32")} {(v.V30 ? "V30" : "V28")} display={(v.DisplayEnabled ? "on" : "off")} vint={(v.VintEnabled ? "on" : "off")} hint={(v.HintEnabled ? "on" : "off")} dma={(v.DmaEnabled ? "on" : "off")} | " +
               $"vram {vramNonZero:N0}/65536 bytes set, cram {cramNonZero}/64 colours set | {b.Sound.Describe()}";
    }

    private static void Dump(BOARD_MD b)
    {
        var v = b.Vdp;
        Console.WriteLine("   VDP registers: " + string.Join(" ", Enumerable.Range(0, 24).Select(i => $"{i:X2}={v.Reg[i]:X2}")));
        Console.WriteLine($"   plane A ${(v.Reg[2] & 0x38) << 10:X4}, window ${(v.Reg[3] & 0x3E) << 10:X4}, plane B ${(v.Reg[4] & 7) << 13:X4}, sprites ${(v.Reg[5] & 0x7E) << 9:X4}, hscroll ${(v.Reg[13] & 0x3F) << 10:X4}, plane size {v.Reg[16]:X2}, bg colour {v.Reg[7]:X2}, scroll mode {v.Reg[11]:X2}, mode {v.Reg[12]:X2}, auto-increment {v.Reg[15]}");
        for (int line = 0; line < 4; line++)
            Console.WriteLine($"   CRAM line {line}: " + string.Join(" ", Enumerable.Range(0, 16).Select(i => $"{v.Cram[line * 16 + i]:X3}")));
        Console.WriteLine($"   VSRAM: {string.Join(" ", Enumerable.Range(0, 8).Select(i => $"{v.Vsram[i]:X3}"))} ...");
        // how much of VRAM looks like data: the highest non-zero offset and the busiest 4 KB blocks
        int last = Array.FindLastIndex(v.Vram, x => x != 0);
        Console.WriteLine($"   VRAM: highest non-zero byte ${last:X4}; non-zero bytes per 4 KB block: " + string.Join(" ", Enumerable.Range(0, 16).Select(k => v.Vram.Skip(k * 4096).Take(4096).Count(x => x != 0))));
        Console.WriteLine($"   VDP: data writes {v.DataWrites:N0}, control writes {v.ControlWrites:N0}, FIFO stalls {v.FifoStalls:N0} ({v.FifoStallClocks:N0} master clocks), DMA words {v.DmaWords:N0}");
        Console.WriteLine($"   bus: {b.BusReads:N0} reads, {b.BusWrites:N0} writes, {b.OpenBusReads:N0} open-bus reads | {b.Pads.Describe()}");
        if (b.Sound is MdSoundUnit u)
            Console.WriteLine($"   sound unit: z80 ticks {u.Z80Ticks:N0}, bus requests {u.BusRequests}, bank reads {u.BankReads:N0}, psg writes {u.PsgWrites}, ym writes {u.YmWrites}, z80 ram non-zero {u.Ram.Count(x => x != 0)}/8192");
    }

    private static ulong PicHash(MdSession s)
    {
        ulong h = 14695981039346656037UL;
        int n = s.FrameWidth * s.FrameHeight;
        for (int i = 0; i < n; i++) { h ^= s.Frame[i]; h *= 1099511628211UL; }
        return h ^ (ulong)s.FrameWidth << 40 ^ (ulong)s.FrameHeight << 52;
    }

    private static ulong Hash(byte[] d) { ulong h = 14695981039346656037UL; foreach (byte x in d) { h ^= x; h *= 1099511628211UL; } return h; }
    private static ulong Hash(ushort[] d) { ulong h = 14695981039346656037UL; foreach (ushort x in d) { h ^= (byte)x; h *= 1099511628211UL; h ^= (byte)(x >> 8); h *= 1099511628211UL; } return h; }
    private static ulong StateHash(BOARD_MD b) => Hash(b.Vdp.Vram) ^ Hash(b.Vdp.Cram) * 3 ^ Hash(b.Vdp.Vsram) * 5 ^ Hash(b.Vdp.Reg) * 7 ^ Hash(b.Ram) * 11;

    private static Dictionary<int, PadButtons> ParseInput(string? script)
    {
        var map = new Dictionary<int, PadButtons>();
        if (string.IsNullOrWhiteSpace(script)) return map;
        foreach (var part in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            if (colon < 0 || !int.TryParse(part[..colon], out int frame)) continue;
            PadButtons b = PadButtons.None;
            foreach (var name in part[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Enum.TryParse<PadButtons>(name, true, out var one)) b |= one;
            map[frame] = b;
        }
        return map;
    }

    private static Bitmap ToBitmap(uint[] pixels, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var ints = new int[width];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) ints[x] = (int)pixels[y * width + x];
                System.Runtime.InteropServices.Marshal.Copy(ints, 0, data.Scan0 + y * data.Stride, width);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    private static void WriteSheet(List<(int frame, Bitmap bmp)> frames, string path)
    {
        const int cols = 4, cw = 192, ch = 136;      // a cell: 320x224 at 0.6, with a little room for the label
        int rows = (frames.Count + cols - 1) / cols;
        using var sheet = new Bitmap(cols * cw, rows * ch, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(32, 32, 32));
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        using var font = new Font("Consolas", 8f);
        for (int k = 0; k < frames.Count; k++)
        {
            int cx = k % cols * cw, cy = k / cols * ch;
            g.DrawImage(frames[k].bmp, new Rectangle(cx + 1, cy + 11, cw - 2, ch - 12));
            g.DrawString($"f{frames[k].frame}", font, Brushes.Yellow, cx + 2, cy);
        }
        sheet.Save(path, ImageFormat.Png);
    }

    private static void WritePng(MdSession s, string path) => WritePngFrom(s.Frame, s.FrameWidth, s.FrameHeight, path);

    private static void WritePngFrom(uint[] pixels, int width, int height, string path)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var ints = new int[width];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) ints[x] = (int)pixels[y * width + x];
                System.Runtime.InteropServices.Marshal.Copy(ints, 0, data.Scan0 + y * data.Stride, width);
            }
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(path, ImageFormat.Png);
    }
}
