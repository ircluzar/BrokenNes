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
/// [--trace-pc] [--trace-frames N] [--hash-at 60,120] [--quiet]</c>: runs a Genesis ROM headless on <see cref="BOARD_MD"/> and reports what the machine ended up in: the 68000, the VDP registers,
/// VRAM, CRAM and VSRAM (with hashes you can compare between runs), the Z80 and the sound unit, the pads. There is no picture renderer yet, so <c>--png</c> writes the VDP's VRAM/CRAM debug view.
/// <c>--trace</c> writes a line per VDP, Z80-area, I/O, BUSREQ, RESET and PSG write (and per instruction with <c>--trace-pc</c>), tagged with frame and line, in a form two runs can be diffed in.
/// Exit code 0 = ran every frame without a halt or a 68000 address error; 1 = the CPU halted or took an address error (listed); 2 = usage or file error.
/// </summary>
internal static class MdRunCli
{
    public static int Run(string[] args)
    {
        string? rom = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : M68kTestCli.Arg(args, "--rom");
        if (rom == null) { Console.WriteLine("usage: --md-run <rom> [--frames N] [--region ntsc|pal] [--model 1|2|3] [--apu id] [--input script] [--every N] [--png out.png] [--palette n] [--trace out.txt] [--trace-pc] [--trace-frames N] [--hash-at list] [--quiet]"); return 2; }
        int frames = int.TryParse(M68kTestCli.Arg(args, "--frames"), out int f) ? f : 120;
        int every = int.TryParse(M68kTestCli.Arg(args, "--every"), out int ev) ? ev : 0;
        int palette = int.TryParse(M68kTestCli.Arg(args, "--palette"), out int pal) ? pal : 0;
        bool quiet = M68kTestCli.Flag(args, "--quiet");
        string? png = M68kTestCli.Arg(args, "--png"), tracePath = M68kTestCli.Arg(args, "--trace");
        bool tracePc = M68kTestCli.Flag(args, "--trace-pc");
        int traceFrames = int.TryParse(M68kTestCli.Arg(args, "--trace-frames"), out int tf) ? tf : int.MaxValue;
        var hashAt = new HashSet<int>((M68kTestCli.Arg(args, "--hash-at") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse));
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
        var board = session.Board;
        Console.WriteLine($"== {Path.GetFileName(rom)}: {session.Description}");
        Console.WriteLine($"   header: '{board.Cart.OverseasTitle}' serial {board.Cart.Serial} regions '{board.Cart.Regions}' rom {board.Cart.RomLength:N0} bytes, checksum header {board.Cart.HeaderChecksum:X4} computed {board.Cart.ComputedChecksum:X4}, sram {(board.Cart.HasSramHeader ? $"${board.Cart.SramStart:X6}-${board.Cart.SramEnd:X6} ({board.Cart.Sram.Length} bytes)" : "none")}");

        StreamWriter? trace = null;
        if (tracePath != null)
        {
            trace = new StreamWriter(tracePath, false, new UTF8Encoding(false));
            board.WriteTrace = s => { if (board.Frames < traceFrames) trace.WriteLine($"f{board.Frames} l{board.Vdp.Line} {s}"); };
            board.Vdp.Trace = s => { if (board.Frames < traceFrames) trace.WriteLine($"f{board.Frames} l{board.Vdp.Line}   vdp {s}"); };
            if (M68kTestCli.Flag(args, "--trace-reads")) board.ReadTrace = s => { if (board.Frames < traceFrames) trace.WriteLine($"f{board.Frames} l{board.Vdp.Line} {s}"); };
            if (tracePc) board.InstructionHook = pc => { if (board.Frames < traceFrames) trace.WriteLine($"f{board.Frames} l{board.Vdp.Line} pc {pc:X6}"); };
            if (board.Sound is MdSoundUnit unit)
            {
                unit.YmWriteHook = (reg, v, part) => { if (board.Frames < traceFrames) trace.WriteLine($"f{board.Frames} l{board.Vdp.Line}   ym{part} {reg:X2}={v:X2}"); };
            }
        }

        var sw = Stopwatch.StartNew();
        PadButtons pad = PadButtons.None;
        long halts = 0;
        for (int i = 0; i < frames; i++)
        {
            if (inputs.TryGetValue(i, out var b)) pad = b;
            session.SetPad(0, pad);
            session.RunFrame();
            // sound is produced by the board's own clock; drain it so the PSG's buffer does not grow
            DrainSamples(session);
            if (board.Cpu.Halted) { halts++; break; }
            if (every > 0 && (i + 1) % every == 0 && !quiet) Console.WriteLine($"   frame {i + 1,5}: {Summary(board)}");
            if (hashAt.Contains(i + 1)) Console.WriteLine($"   frame {i + 1,5} hash: {StateHash(board):X16}");
        }
        sw.Stop();
        trace?.Dispose();

        Console.WriteLine($"   ran {board.Frames} frames in {sw.Elapsed.TotalSeconds:F2} s ({board.Frames / Math.Max(0.001, sw.Elapsed.TotalSeconds):F0} frames/s, {board.Cpu.Instructions:N0} 68000 instructions, {board.Cpu.Cycles:N0} clocks)");
        Console.WriteLine($"   {Summary(board)}");
        Console.WriteLine($"   state hash {StateHash(board):X16} | vram {Hash(board.Vdp.Vram):X16} cram {Hash(board.Vdp.Cram):X16} vsram {Hash(board.Vdp.Vsram):X16} regs {Hash(board.Vdp.Reg):X16}");
        if (!quiet) Dump(board);
        if (png != null)
        {
            try { WritePng(session, png); Console.WriteLine($"   wrote {png} (VRAM tiles in palette line {palette} and the 64 CRAM colours; there is no picture renderer yet)"); }
            catch (Exception e) { Console.WriteLine($"   png failed: {e.Message}"); }
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

    private static void DrainSamples(MdSession s)
    {
        var buf = new short[8192];
        while (s.ReadSamples(buf) > 0) { }
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
