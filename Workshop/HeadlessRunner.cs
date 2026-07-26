using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrokenNes.Windows.Rendering;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// Headless entry point sharing the exact same linked cores as WorkshopForm - no window, no
/// WinForms message loop. NES.RunFrame()/GetFrameBuffer() are pure computation (verified: no
/// System.Windows.Forms/SharpDX/wall-clock dependency anywhere in the call path), so this needs
/// nothing special to run without a GUI. This is the technical basis for later automated
/// accuracy-suite runs (e.g. AccuracyCoin) - load a ROM, pick cores, run N frames, capture
/// state, no human required.
///
/// Usage: BrokenNes.Workshop.exe --headless --rom path.nes [--frames N] [--cpu ID] [--ppu ID]
///        [--apu ID] [--out results.json] [--strict]
/// </summary>
internal static class HeadlessRunner
{
    public static int Run(string[] args)
    {
        string? romPath = null, cpu = null, ppu = null, apu = null, outPath = null, screenshotPath = null;
        int frames = 60;
        bool strict = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rom": romPath = args[++i]; break;
                case "--frames": frames = int.Parse(args[++i]); break;
                case "--cpu": cpu = args[++i]; break;
                case "--ppu": ppu = args[++i]; break;
                case "--apu": apu = args[++i]; break;
                case "--out": outPath = args[++i]; break;
                case "--strict": strict = true; break;
                case "--screenshot": screenshotPath = args[++i]; break;
            }
        }

        if (romPath == null)
        {
            Console.Error.WriteLine("Usage: --headless --rom <path.nes> [--frames N] [--cpu ID] [--ppu ID] [--apu ID] [--out results.json] [--strict]");
            return 2;
        }

        try
        {
            var nes = new NES { RomName = Path.GetFileName(romPath) };
            nes.LoadROM(File.ReadAllBytes(romPath));

            if (cpu != null && !nes.SetCpuCore(cpu)) { Console.Error.WriteLine($"Unknown CPU core: {cpu}"); return 3; }
            if (ppu != null && !nes.SetPpuCore(ppu)) { Console.Error.WriteLine($"Unknown PPU core: {ppu}"); return 3; }
            if (apu != null && !nes.SetApuCore(apu)) { Console.Error.WriteLine($"Unknown APU core: {apu}"); return 3; }

            // Bus.SpeedConfig ships several accuracy-for-speed shortcuts on by default
            // (approximate OAM DMA timing, idle-loop skip, blank-scanline skip, ...), applied
            // identically under every core combination. An accuracy run needs these off, or
            // failures get misattributed to whichever core happened to be selected.
            if (strict)
            {
                var cfg = nes.GetSpeedConfig();
                if (cfg != null)
                {
                    cfg.CpuFastOamDmaStall = false;
                    cfg.CpuIdleLoopDetect = false;
                    cfg.CpuIdleLoopSkip = false;
                    cfg.CpuIdleLoopSkipApuStatus = false;
                    cfg.CpuAdaptiveBatching = false;
                    cfg.PpuSkipBlankScanlines = false;
                    cfg.PpuUnsafeScanline = false;
                    cfg.PpuDeferAttributeFetch = false;
                }
            }

            for (int i = 0; i < frames; i++)
            {
                nes.RunFrame();
                if (nes.IsCrashed()) break;
            }

            var fb = nes.GetFrameBuffer();
            string frameHash = Convert.ToHexString(SHA256.HashData(fb));
            var regs = nes.GetCpuRegs();

            // Reuses the exact same GDI+ conversion WorkshopForm.Present() uses to draw the
            // live view, so this also serves as a way to visually spot-check a core/ROM
            // combination without the interactive UI - handy for accuracy work later.
            if (screenshotPath != null && fb.Length == 256 * 240 * 4)
            {
                using var db = new DirectBitmap(256, 240);
                db.CopyFromBytes(fb);
                using var bmp = db.ToBitmap();
                bmp.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
            }

            var result = new HeadlessResult(
                Rom: Path.GetFileName(romPath),
                FramesRequested: frames,
                Cpu: nes.GetCpuCoreId(),
                Ppu: nes.GetPpuCoreId(),
                Apu: nes.GetApuCoreId(),
                Strict: strict,
                Crashed: nes.IsCrashed(),
                CrashInfo: nes.IsCrashed() ? nes.GetCrashInfo() : null,
                FrameHashSha256: frameHash,
                Pc: regs.PC, A: regs.A, X: regs.X, Y: regs.Y, P: regs.P, Sp: regs.SP);

            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            if (outPath != null) File.WriteAllText(outPath, json, Encoding.UTF8);
            Console.WriteLine(json);
            return nes.IsCrashed() ? 1 : 0;
        }
        catch (Cartridge.UnsupportedMapperException ex)
        {
            Console.Error.WriteLine($"Unsupported mapper {ex.MapperId} ({ex.MapperName}).");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 5;
        }
    }
}

internal sealed record HeadlessResult(
    string Rom, int FramesRequested, string Cpu, string Ppu, string Apu, bool Strict,
    bool Crashed, string? CrashInfo, string FrameHashSha256,
    ushort Pc, byte A, byte X, byte Y, byte P, ushort Sp);
