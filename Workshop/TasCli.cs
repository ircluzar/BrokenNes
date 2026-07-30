using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BrokenNes.Windows.Rendering;
using BrokenNes.Workshop.Tas;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// TAS/ML-integration port (see project memory / the published architecture plan for the full
/// three-phase source system this mirrors), driving the exact same linked cores as the interactive
/// UI and the other Workshop harnesses:
///   Phase 1 - read-only FM2 playback (always active for --playmovie).
///   Phase 2 - FM2 recording: --record-out re-emits exactly what was played back as a new,
///     independently-parseable FM2 file (Fm2Writer). This is deliberately a replay-and-re-record
///     round trip rather than a live/human recording surface - see Fm2Writer's class doc for why
///     that's sufficient to prove the writer is spec-correct.
///   Phase 3 - bulk RAM/PPU-state extraction: --dump-out writes the same "nesreflex-raw-v2" binary
///     trace format the source project's FCEUX fork produces (NesReflexDumpWriter), plus a JSON
///     sidecar, so the existing Python training pipeline needs zero changes to accept
///     BrokenNes-produced dumps.
/// Not yet implemented: live self-play/checkpoint harness (phase 4).
///
/// Usage: BrokenNes.Workshop.exe --playmovie --movie path.fm2 --rom path.nes
///        [--cpu ID --ppu ID --apu ID] [--max-frames N] [--strict] [--out result.json]
///        [--screenshot out.png] [--record-out copy.fm2] [--dump-out trace.raw]
/// </summary>
internal static class TasCli
{
    public static int Run(string[] args)
    {
        string? moviePath = null, romPath = null, outPath = null, screenshotPath = null, recordOutPath = null, dumpOutPath = null;
        string? cpu = null, ppu = null, apu = null;
        int maxFrames = int.MaxValue;
        bool strict = false, ntscFrameRate = false;

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
                case "--ntsc-frame-rate": ntscFrameRate = true; break;
                case "--out": outPath = args[++i]; break;
                case "--screenshot": screenshotPath = args[++i]; break;
                case "--record-out": recordOutPath = args[++i]; break;
                case "--dump-out": dumpOutPath = args[++i]; break;
            }
        }

        if (moviePath == null || romPath == null)
        {
            Console.Error.WriteLine("Usage: --playmovie --movie <path.fm2> --rom <path.nes> [--cpu ID --ppu ID --apu ID] [--max-frames N] [--strict] [--ntsc-frame-rate] [--out result.json] [--screenshot out.png] [--record-out copy.fm2] [--dump-out trace.raw]");
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

            // Opt-in FCEUX-parity frame timing (see Bus.SpeedConfig.NtscAccurateFrameRate) - off by
            // default everywhere else in the emulator; movie replay/export is exactly the path that
            // needs it for .fm2 portability.
            if (ntscFrameRate)
            {
                var speedCfg = nes.GetSpeedConfig();
                if (speedCfg != null) speedCfg.NtscAccurateFrameRate = true;
            }

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

            Fm2Writer? recorder = null;
            if (recordOutPath != null)
            {
                recorder = new Fm2Writer
                {
                    EmuVersion = header.EmuVersion,
                    RerecordCount = header.RerecordCount,
                    PalFlag = header.PalFlag,
                    Fourscore = header.Fourscore,
                    Port0 = header.Port0,
                    Port1 = header.Port1,
                    Port2 = header.Port2,
                    RomFilename = Path.GetFileNameWithoutExtension(romPath),
                    RomChecksumMd5 = nes.ComputeRomMd5(),
                };
            }
            NesReflexDumpWriter? dumper = dumpOutPath != null ? new NesReflexDumpWriter(dumpOutPath) : null;

            int framesToPlay = Math.Min(frames.Count, maxFrames);
            int framesPlayed = 0;
            bool crashedMidMovie = false;
            try
            {
                for (; framesPlayed < framesToPlay; framesPlayed++)
                {
                    var frame = frames[framesPlayed];
                    if (frame.Reset) nes.Reset();
                    nes.SetInputs(frame.P1, frame.P2);
                    nes.RunFrame();
                    recorder?.RecordFrame(frame.Reset, frame.P1, frame.P2);
                    dumper?.WriteFrame(nes, framesPlayed, (byte)(frame.Reset ? 1 : 0));
                    if (nes.IsCrashed()) { crashedMidMovie = true; break; }
                }
            }
            finally
            {
                dumper?.Dispose();
            }
            if (recorder != null) recorder.Save(recordOutPath!);
            if (dumper != null) WriteDumpSidecar(dumpOutPath!, romPath, moviePath, nes, maxFrames == int.MaxValue ? 0 : maxFrames, dumper.FramesWritten,
                crashedMidMovie ? "crashed" : framesPlayed >= frames.Count ? "movie_finished" : "max_frames");

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
                Pc: regs.PC, A: regs.A, X: regs.X, Y: regs.Y, P: regs.P, Sp: regs.SP,
                RecordedMovie: recordOutPath != null ? Path.GetFileName(recordOutPath) : null,
                RecordedFrameCount: recorder?.FrameCount,
                Dump: dumpOutPath != null ? Path.GetFileName(dumpOutPath) : null,
                DumpFramesWritten: dumper?.FramesWritten);

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

    /// <summary>Mirrors nesreflex_dump.cpp's WriteSidecar() field-for-field (see
    /// NesReflexDumpWriter's class doc), so the JSON sidecar format matches exactly what the
    /// existing Python extraction pipeline already expects to find next to a .raw file.</summary>
    private static void WriteDumpSidecar(string dumpPath, string romPath, string moviePath, NES nes, int maxFrames, int framesWritten, string exitReason)
    {
        var md5 = nes.ComputeRomMd5();
        var sidecar = new
        {
            format = "nesreflex-raw-v2",
            record_layout = 2,
            record_fields = "ram,ppu_regs,ppu_scroll,ppu_vram_addr,ppu_scanline_dot,oam,palram,ntaram,screen_hash",
            dump_path = dumpPath,
            rom_path = romPath,
            movie_path = moviePath,
            rom_md5_hex = Convert.ToHexString(md5).ToLowerInvariant(),
            max_frames = maxFrames,
            frames_written = framesWritten,
            exit_reason = exitReason,
        };
        File.WriteAllText(dumpPath + ".json", JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed record TasPlaybackResult(
    string Movie, string Rom, string MovieRomFilename, int MovieEmuVersion, int MovieRerecordCount, int MovieFrameCount,
    string Cpu, string Ppu, string Apu, bool Strict,
    int FramesPlayed, bool CompletedMovie, bool Crashed, bool CrashedMidMovie, string? CrashInfo,
    string FrameHashSha256, ushort Pc, byte A, byte X, byte Y, byte P, ushort Sp,
    string? RecordedMovie, int? RecordedFrameCount, string? Dump, int? DumpFramesWritten);
