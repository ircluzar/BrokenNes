using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using NesEmulator;
using NesEmulator.Snes;

namespace BrokenNes.Workshop.MixLab;

/// <summary>
/// MIX LAB CLI (exploration only).
///   --mixlab list
///   --mixlab nes --rom game.nes [--cpu ID] [--ppu ID] [--apu ID] [--snes-cpu SFC] [--snes-ppu SFC] [--nes-front FIX]
///                [--frames N] [--png-at a,b,...] [--input "f:A+B,f:"] [--out-dir D] [--tag T]
///   --mixlab snes2nes --rom game.sfc --nes-ppu FIX,LQ,... [--frames N] [--png-at ...] [--input ...] [--out-dir D]
/// </summary>
internal static class MixLabCli
{
    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        if (args.Length < 2) { Console.Error.WriteLine("usage: --mixlab list|nes|snes2nes ..."); return 2; }
        var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 2; i + 1 < args.Length; i += 2) o[args[i].TrimStart('-')] = args[i + 1];
        string Opt(string k, string d) => o.TryGetValue(k, out var v) ? v : d;
        try
        {
            switch (args[1].ToLowerInvariant())
            {
                case "list":
                    Console.WriteLine("NES CPU: " + string.Join(" ", CoreRegistry.CpuIds));
                    Console.WriteLine("NES PPU: " + string.Join(" ", CoreRegistry.PpuIds));
                    Console.WriteLine("NES APU: " + string.Join(" ", CoreRegistry.ApuIds));
                    Console.WriteLine("SNES CPU: " + string.Join(" ", SnesCores.CpuIds));
                    Console.WriteLine("SNES PPU: " + string.Join(" ", SnesCores.PpuIds));
                    return 0;
                case "nes": return RunNes(Opt);
                case "snes2nes": return RunSnes2Nes(Opt);
                case "ppuprobe": return PpuProbe(Opt);
                case "sweep": return MixSweep.Run(Opt);
                case "snescpu": return RunSnesCpu(Opt);
                case "snesaudio": return MixAudioCli.SnesAudio(Opt);
                case "audiocmp": return MixAudioCli.AudioCmp(Opt);
                case "gbaudio": return GbMixCli.Audio(Opt);
                case "gbpic": return GbMixCli.Picture(Opt);
                case "nes2gb": return GbMixCli.NesOnGb(Opt);
                case "snes2gb": return GbMixCli.SnesOnGb(Opt);
                case "gbcpu": return GbMixCli.CpuSpeed(Opt);
                case "gbcart": return GbMixCli.NesCart(Opt);
                case "imgstat": return GbMixCli.ImgStat(Opt);
                case "sheet": return Sheet(Opt("items", ""), Opt("out", "sheet.png"), int.Parse(Opt("scale", "2")));
                default: Console.Error.WriteLine("unknown mode"); return 2;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 5; }
    }

    private static HashSet<int> Frames(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();

    // ------------------------------------------------------------------ NES game, any core mix
    private static int RunNes(Func<string, string, string> opt)
    {
        string rom = opt("rom", ""), outDir = opt("out-dir", "."), tag = opt("tag", "mix");
        MixConfig.SnesCpu = opt("snes-cpu", "SFC"); MixConfig.SnesPpu = opt("snes-ppu", "SFC"); MixConfig.NesFrontPpu = opt("nes-front", "FIX"); MixConfig.SnesApu = opt("snes-apu", "SFC"); MixConfig.NesFrontApu = opt("nes-front-apu", "FIX"); MixConfig.GbPpuModel = opt("gb-model", "dmg"); MixConfig.ExtSpriteLimit = int.Parse(opt("ext-sprites", "1024")); MixConfig.ExtSpritesPerLine = int.Parse(opt("ext-sprites-per-line", "34")); { var rs = opt("gb-res", "256x240").Split('x'); MixConfig.GbHiResWidth = int.Parse(rs[0]); MixConfig.GbHiResHeight = int.Parse(rs[1]); } { var cr = opt("gb-crop", "48,48").Split(','); MixConfig.GbCropX = int.Parse(cr[0]); MixConfig.GbCropY = int.Parse(cr[1]); } MixConfig.GbApu = opt("gb-apu", "GB");
        int frames = int.Parse(opt("frames", "600"));
        if (opt("snes-fastpaths", "1") == "0") NesEmulator.Snes.PPU_SFC.FastPaths = false;
        var pngAt = Frames(opt("png-at", frames.ToString()));
        var script = ParseNesInput(opt("input", ""));
        Directory.CreateDirectory(outDir);

        var nes = new NES { RomName = Path.GetFileName(rom) };
        nes.LoadROM(File.ReadAllBytes(rom));
        foreach (var (kind, id, set, get) in new (string, string, Func<string, bool>, Func<string>)[] {
            ("CPU", opt("cpu", "FIX"), nes.SetCpuCore, nes.GetCpuCoreId), ("PPU", opt("ppu", "FIX"), nes.SetPpuCore, nes.GetPpuCoreId), ("APU", opt("apu", "FIX"), nes.SetApuCore, nes.GetApuCoreId) })
        {
            set(id);
            if (!get().EndsWith("_" + id, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine($"{kind} core {id} not applied (got {get()})"); return 3; }
        }
        var bus = (Bus)typeof(NES).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(nes)!;
        // Bridge PPUs wrap PPU_FIX, and the board only runs PPU_FIX timing precisely when the PPU *is* PPU_FIX: the wrapped
        // game then corrupts (CHR-RAM, MMC3 splits). SpeedConfig has an opt-in for other PPU cores - turn it on for them.
        if (opt("precise", "auto") is var pr && (pr == "1" || (pr == "auto" && bus.ppu is PPU_SNES or PPU_DMG)))
        { bus.SpeedConfig.CpuCyclePrecisePpu = true; bus.SpeedConfig.NtscAccurateFrameRate = true; }
        var held = new bool[8]; int crashFrame = -1;
        string wavPath = opt("wav", ""); var pcm = new List<short>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 0; f < frames; f++)
        {
            if (script.TryGetValue(f, out var h)) held = h;
            nes.SetInputs(held, null);
            nes.RunFrame();
            if (wavPath != "") foreach (var x in nes.GetAudioBuffer()) pcm.Add((short)Math.Clamp(x * 32767f, -32768, 32767));
            if (pngAt.Contains(f + 1)) SaveRgba(nes.GetFrameBuffer(), 256, 240, Path.Combine(outDir, $"{tag}_f{f + 1:D5}.png"));
            if (nes.IsCrashed()) { crashFrame = f; break; }
        }
        Console.WriteLine($"{tag}: {Path.GetFileName(rom)} cpu={nes.GetCpuCoreId()} ppu={nes.GetPpuCoreId()} apu={nes.GetApuCoreId()} frames={frames} {sw.Elapsed.TotalSeconds:F1}s" +
            (crashFrame >= 0 ? $" CRASHED at frame {crashFrame}: {nes.GetCrashInfo()}" : ""));
        if (wavPath != "") { MixAudioCli.WriteWav(wavPath, pcm.ToArray(), nes.GetAudioSampleRate()); Console.WriteLine($"  wav: {pcm.Count:N0} samples at {nes.GetAudioSampleRate()} Hz"); }
        if (bus.ActiveAPU is APU_SNES asn) Console.WriteLine($"  APU_SNES: {asn.Status} dsp-writes={asn.DspWrites}");
        if (bus.cpu is CPU_SM83 sm) Console.WriteLine($"  SM83: instructions={sm.InstructionsRun:N0} interrupts-through-NES-vectors={sm.Interrupts:N0} lock-ups-as-NOP={sm.Unlocks:N0} PC=${sm.Inner.PC:X4}");
        if (bus.cpu is CPU_SNES cs)
        {
            var c = cs.Inner;
            Console.WriteLine($"  65816: rescued-ops={cs.Rescued:N0} irqs={cs.IrqsTaken:N0} latch={CPU_SNES.LatchIrq} instructions={cs.Instructions:N0} E={c.E} P=${c.P:X2} PC=${c.PC:X4} stopped={c.Stopped}" + (cs.StoppedAtInstruction >= 0 ? $" (STP after {cs.StoppedAtInstruction:N0} instructions)" : ""));
        }
        return crashFrame >= 0 ? 1 : 0;
    }

    internal static Dictionary<int, bool[]> ParseNesInput(string s)
    {
        string[] names = { "A", "B", "SELECT", "START", "UP", "DOWN", "LEFT", "RIGHT" };
        var d = new Dictionary<int, bool[]>();
        foreach (var step in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = step.Split(':'); var held = new bool[8];
            foreach (var b in parts[1].Split('+', StringSplitOptions.RemoveEmptyEntries)) held[Array.IndexOf(names, b.Trim().ToUpperInvariant())] = true;
            d[int.Parse(parts[0])] = held;
        }
        return d;
    }

    // ------------------------------------------------------------------ SNES game, downgraded onto NES PPUs
    private static int RunSnes2Nes(Func<string, string, string> opt)
    {
        MixConfig.GbPpuModel = opt("gb-model", "dmg"); MixConfig.ExtSpriteLimit = int.Parse(opt("ext-sprites", "1024")); MixConfig.ExtSpritesPerLine = int.Parse(opt("ext-sprites-per-line", "34")); { var rs = opt("gb-res", "256x240").Split('x'); MixConfig.GbHiResWidth = int.Parse(rs[0]); MixConfig.GbHiResHeight = int.Parse(rs[1]); } { var cr = opt("gb-crop", "48,48").Split(','); MixConfig.GbCropX = int.Parse(cr[0]); MixConfig.GbCropY = int.Parse(cr[1]); }
        string rom = opt("rom", ""), outDir = opt("out-dir", "."), tag = opt("tag", Path.GetFileNameWithoutExtension(rom));
        int frames = int.Parse(opt("frames", "600"));
        if (opt("snes-fastpaths", "1") == "0") NesEmulator.Snes.PPU_SFC.FastPaths = false;
        var pngAt = Frames(opt("png-at", frames.ToString()));
        var ids = opt("nes-ppu", "FIX").Split(',', StringSplitOptions.RemoveEmptyEntries);
        Directory.CreateDirectory(outDir);
        var script = SnesRunCli.ParseInput(opt("input", ""));
        var board = new BOARD_SFC(SnesCartridge.Load(File.ReadAllBytes(rom)), SnesApuChoice.Create("SFC"));
        var snesPpu = SnesCores.Wrap(board.Ppu);
        var downs = ids.ToDictionary(id => id, id => new SnesToNes(id));
        ushort held = 0;
        var audio = new short[8192];
        for (int f = 1; f <= frames; f++)
        {
            if (script.TryGetValue(f - 1, out var h)) held = h;
            board.Pads[0] = held;
            SnesPpuSnapshot? mid = null;
            board.InstructionHook = pngAt.Contains(f) ? _ => { if (mid == null && board.Scanline == 112) mid = snesPpu.Snapshot(); } : null;
            board.RunFrame();
            while (board.Apu.ReadSamples(audio) > 0) { }
            if (!pngAt.Contains(f)) continue;
            SaveArgb(snesPpu.FrameBuffer, 256, snesPpu.VisibleHeight, Path.Combine(outDir, $"{tag}_f{f:D5}_snes.png"));
            foreach (var (id, down) in downs)
            {
                var fb = down.Render(snesPpu, mid);
                SaveRgba(fb, 256, 240, Path.Combine(outDir, $"{tag}_f{f:D5}_nes-{id}.png"));
                Console.WriteLine($"f{f} {id}: {down.Last} | mid={(mid != null ? "line112" : "END-OF-FRAME")} inidisp=${(mid ?? snesPpu.Snapshot()).Inidisp:X2}");
            }
        }
        return 0;
    }

    // ------------------------------------------------------------------ PPU_SFC in isolation: mode 0, one known tile everywhere
    private static int PpuProbe(Func<string, string, string> opt)
    {
        var p = SnesCores.CreatePpu(opt("snes-ppu", "SFC"));
        int vofs = int.Parse(opt("vofs", "0")), perLine = int.Parse(opt("perline", "0"));
        p.WriteRegister(0x00, 0x80); p.WriteRegister(0x05, 0x00); p.WriteRegister(0x07, 0x03); p.WriteRegister(0x0B, 0x02);
        p.WriteRegister(0x2C, 0x01); p.WriteRegister(0x33, 0x04);
        // tile 1 at word 0x2000+8: row r = colour (r % 4) across the whole row: plane0 = r&1, plane1 = r&2
        for (int r = 0; r < 8; r++) p.Vram[0x2008 + r] = (ushort)(((r & 1) != 0 ? 0xFF : 0) | ((r & 2) != 0 ? 0xFF00 : 0));
        for (int i = 0; i < 0x1000; i++) p.Vram[i] = 1;
        p.Cgram[0] = 0; p.Cgram[1] = 0x001F; p.Cgram[2] = 0x03E0; p.Cgram[3] = 0x7C00;
        p.InvalidateCaches();
        p.WriteRegister(0x00, 0x0F); p.BeginFrame();
        for (int line = 1; line <= 20; line++)
        {
            if (perLine == 1 || line == 1) { p.WriteRegister(0x0E, (byte)vofs); p.WriteRegister(0x0E, (byte)(vofs >> 8)); p.WriteRegister(0x0D, 0); p.WriteRegister(0x0D, 0); }
            p.RenderLine(line);
            uint px = p.FrameBuffer[(line - 1) * 256 + 5];
            Console.WriteLine($"line {line,2} row {line - 1,2}: expect colour {(line + vofs) % 8 % 4}  got {px:X8}");
        }
        return 0;
    }

    // ------------------------------------------------------------------ SNES game with a downgraded CPU
    //   mode=normal   : the 65816 as is
    //   mode=emu      : "an 8-bit CPU" - the 65816 pinned in emulation mode (every XCE into native mode is undone)
    //   mode=nesspeed : the 65816 with every memory access at 12 master clocks (the NES's 1.79 MHz); internal cycles unchanged
    private static int RunSnesCpu(Func<string, string, string> opt)
    {
        string rom = opt("rom", ""), outDir = opt("out-dir", "."), mode = opt("mode", "normal"), tag = opt("tag", Path.GetFileNameWithoutExtension(rom) + "-" + mode);
        int frames = int.Parse(opt("frames", "600"));
        var pngAt = Frames(opt("png-at", frames.ToString()));
        Directory.CreateDirectory(outDir);
        var script = SnesRunCli.ParseInput(opt("input", ""));
        var board = new BOARD_SFC(SnesCartridge.Load(File.ReadAllBytes(rom)), SnesApuChoice.Create("SFC"));
        if (mode == "nesspeed")
        {
            var fi = typeof(BOARD_SFC).GetField("pages", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var arr = (Array)fi.GetValue(board)!;
            var speed = arr.GetType().GetElementType()!.GetField("Speed")!;
            for (int i = 0; i < arr.Length; i++) { object p = arr.GetValue(i)!; speed.SetValue(p, 12); arr.SetValue(p, i); }
        }
        long forced = 0;
        if (mode == "emu") board.InstructionHook = c => { if (!c.E) { c.E = true; c.P |= 0x30; c.X &= 0xFF; c.Y &= 0xFF; c.S = (ushort)(0x0100 | (c.S & 0xFF)); forced++; } };
        // mode=8bit: native decoding kept (so the code stays aligned) but the datapath is 8 bits: the upper byte of A, X
        // and Y is lost after every instruction, as if only the low half of each register existed.
        if (mode == "8bit") board.InstructionHook = c => { if ((c.A | c.X | c.Y) > 0xFF) forced++; c.A &= 0xFF; c.X &= 0xFF; c.Y &= 0xFF; };
        var audio = new short[8192]; ushort held = 0; var ppu = SnesCores.Wrap(board.Ppu);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 1; f <= frames; f++)
        {
            if (script.TryGetValue(f - 1, out var h)) held = h;
            board.Pads[0] = held;
            board.RunFrame();
            while (board.Apu.ReadSamples(audio) > 0) { }
            if (pngAt.Contains(f)) SaveArgb(ppu.FrameBuffer, 256, ppu.VisibleHeight, Path.Combine(outDir, $"{tag}_f{f:D5}.png"));
            if (board.Cpu.Stopped) { Console.WriteLine($"{tag}: CPU stopped (STP) at frame {f}"); break; }
        }
        var cpu = board.Cpu;
        Console.WriteLine($"{tag}: frames={board.FrameCount} nmis={board.NmiCount} instructions={cpu.InstructionCount:N0} PC=${cpu.PBR:X2}:{cpu.PC:X4} E={cpu.E} forced-back-to-emulation={forced:N0} forcedBlank={board.Ppu.ForcedBlank} {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    // ------------------------------------------------------------------ side-by-side sheets: items = "path|label;path|label"
    private static int Sheet(string items, string outPath, int scale)
    {
        var parts = items.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('|')).ToList();
        var imgs = parts.Select(p => Image.FromFile(p[0])).ToList();
        int gap = 8, top = 22, w = imgs.Sum(i => i.Width * scale) + gap * (imgs.Count - 1), h = imgs.Max(i => i.Height * scale) + top;
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(22, 22, 26));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            using var font = new Font("Consolas", 10);
            int x = 0;
            for (int i = 0; i < imgs.Count; i++)
            {
                g.DrawImage(imgs[i], new Rectangle(x, top, imgs[i].Width * scale, imgs[i].Height * scale));
                g.DrawString(parts[i].Length > 1 ? parts[i][1] : "", font, Brushes.Gainsboro, x + 2, 3);
                x += imgs[i].Width * scale + gap;
            }
        }
        foreach (var i in imgs) i.Dispose();
        bmp.Save(outPath, ImageFormat.Png);
        return 0;
    }

    // ------------------------------------------------------------------ PNG helpers
    internal static void SaveRgba(byte[] rgba, int w, int h, string path)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            bmp.SetPixel(x, y, Color.FromArgb(255, rgba[i], rgba[i + 1], rgba[i + 2]));
        }
        bmp.Save(path, ImageFormat.Png);
    }

    internal static void SaveArgb(uint[] argb, int w, int h, string path)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) bmp.SetPixel(x, y, Color.FromArgb((int)(argb[y * w + x] | 0xFF000000u)));
        bmp.Save(path, ImageFormat.Png);
    }
}
