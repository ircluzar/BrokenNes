using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BrokenNes.Windows.Rendering;
using BrokenNes.Workshop.Tas;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// Phase 1 of the TAS/ML-integration port (see project memory / the published architecture plan
/// for the full three-phase source system this mirrors): read-only playback of an FM2 TAS movie
/// against a ROM, driving the exact same linked cores as the interactive UI and the other Workshop
/// harnesses. No recording, no self-play, no bulk-dump format yet - those are later phases.
///
/// Usage: BrokenNes.Workshop.exe --playmovie --movie path.fm2 --rom path.nes
///        [--cpu ID --ppu ID --apu ID] [--max-frames N] [--strict] [--out result.json]
///        [--screenshot out.png]
/// </summary>
internal static class TasCli
{
    public static int Run(string[] args)
    {
        string? moviePath = null, romPath = null, outPath = null, screenshotPath = null;
        string? cpu = null, ppu = null, apu = null;
        int maxFrames = int.MaxValue;
        bool strict = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--movie": moviePath = args[++i]; break;
                case "--rom": romPath = args[++i]; break;
                case "--cpu": cpu = args[++i]; break;
                case "--ppu": ppu = args[++i]; break;
                case "--apu": apu = args[++i]; break;
                case "--max-frames": maxFrames = int.Parse(args[++i]); break;
                case "--strict": strict = true; break;
                case "--out": outPath = args[++i]; break;
                case "--screenshot": screenshotPath = args[++i]; break;
            }
        }

        if (moviePath == null || romPath == null)
        {
            Console.Error.WriteLine("Usage: --playmovie --movie <path.fm2> --rom <path.nes> [--cpu ID --ppu ID --apu ID] [--max-frames N] [--strict] [--out result.json] [--screenshot out.png]");
            return 2;
        }

        Fm2Header header;
        System.Collections.Generic.IReadOnlyList<Fm2Frame> frames;
        try
        {
            (header, frames) = Fm2Movie.Load(moviePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to parse movie: {ex.Message}");
            return 3;
        }

        byte[] romBytes;
        try { romBytes = File.ReadAllBytes(romPath); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to read ROM: {ex.Message}"); return 2; }

        try
        {
            var nes = new NES { RomName = Path.GetFileName(romPath) };
            nes.LoadROM(romBytes);

            if (cpu != null && !nes.SetCpuCore(cpu)) { Console.Error.WriteLine($"Unknown CPU core: {cpu}"); return 3; }
            if (ppu != null && !nes.SetPpuCore(ppu)) { Console.Error.WriteLine($"Unknown PPU core: {ppu}"); return 3; }
            if (apu != null && !nes.SetApuCore(apu)) { Console.Error.WriteLine($"Unknown APU core: {apu}"); return 3; }

            // Same rationale as HeadlessRunner's --strict: without this, a global Bus-level speed
            // shortcut could get misattributed as a "the movie desynced" playback bug.
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

            int framesToPlay = Math.Min(frames.Count, maxFrames);
            int framesPlayed = 0;
            bool crashedMidMovie = false;
            for (; framesPlayed < framesToPlay; framesPlayed++)
            {
                var frame = frames[framesPlayed];
                if (frame.Reset) nes.Reset();
                nes.SetInputs(frame.P1, frame.P2);
                nes.RunFrame();
                if (nes.IsCrashed()) { crashedMidMovie = true; break; }
            }

            var fb = nes.GetFrameBuffer();
            string frameHash = Convert.ToHexString(SHA256.HashData(fb));
            var regs = nes.GetCpuRegs();

            if (screenshotPath != null && fb.Length == 256 * 240 * 4)
            {
                using var db = new DirectBitmap(256, 240);
                db.CopyFromBytes(fb);
                using var bmp = db.ToBitmap();
                bmp.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
            }

            var result = new TasPlaybackResult(
                Movie: Path.GetFileName(moviePath),
                Rom: Path.GetFileName(romPath),
                MovieRomFilename: header.RomFilename,
                MovieEmuVersion: header.EmuVersion,
                MovieRerecordCount: header.RerecordCount,
                MovieFrameCount: frames.Count,
                Cpu: nes.GetCpuCoreId(),
                Ppu: nes.GetPpuCoreId(),
                Apu: nes.GetApuCoreId(),
                Strict: strict,
                FramesPlayed: framesPlayed,
                CompletedMovie: framesPlayed >= frames.Count,
                Crashed: nes.IsCrashed(),
                CrashedMidMovie: crashedMidMovie,
                CrashInfo: nes.IsCrashed() ? nes.GetCrashInfo() : null,
                FrameHashSha256: frameHash,
                Pc: regs.PC, A: regs.A, X: regs.X, Y: regs.Y, P: regs.P, Sp: regs.SP);

            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            if (outPath != null) File.WriteAllText(outPath, json);
            Console.WriteLine(json);
            return nes.IsCrashed() ? 1 : 0;
        }
        catch (Cartridge.UnsupportedMapperException ume)
        {
            Console.Error.WriteLine($"Unsupported mapper {ume.MapperId} ({ume.MapperName}).");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 5;
        }
    }
}

internal sealed record TasPlaybackResult(
    string Movie, string Rom, string MovieRomFilename, int MovieEmuVersion, int MovieRerecordCount, int MovieFrameCount,
    string Cpu, string Ppu, string Apu, bool Strict,
    int FramesPlayed, bool CompletedMovie, bool Crashed, bool CrashedMidMovie, string? CrashInfo,
    string FrameHashSha256, ushort Pc, byte A, byte X, byte Y, byte P, ushort Sp);
