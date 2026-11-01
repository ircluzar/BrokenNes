using System;
using System.IO;
using System.Text.Json;
using BrokenNes.Windows.Rendering;
using BrokenNes.Workshop.Tas.SelfPlay;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// Phase 4 of the TAS/ML-integration port: live self-play against the source project's Python
/// inference server (scripts/nesreflex_inference_server.py, protocol v2, unmodified). Currently
/// SMB1-only (game_id 133) - see Tas/SelfPlay/SelfPlayManager.cs for the full port of the
/// checkpoint/event-detection system this drives.
///
/// Usage: BrokenNes.Workshop.exe --selfplay --rom path.nes [--cpu ID --ppu ID --apu ID]
///        [--pipe-name nesreflex_inference] [--frames N] [--checkpoint-dir dir] [--seed N]
///        [--out result.json] [--screenshot-every N --screenshot-dir dir] [--log-every N]
/// </summary>
internal static class SelfPlayCli
{
    public static int Run(string[] args)
    {
        string? romPath = null, checkpointDir = null, outPath = null, screenshotDir = null, movieOutPath = null, autoExportDir = null;
        string? cpu = null, ppu = null, apu = null;
        string pipeName = "nesreflex_inference";
        int maxFrames = 3600;
        int screenshotEvery = 0;
        int logEvery = 300;
        int? seed = null;
        int autoStartFrame = -1;
        int autoExportEveryFrames = 3600;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rom": romPath = args[++i]; break;
                case "--cpu": cpu = args[++i]; break;
                case "--ppu": ppu = args[++i]; break;
                case "--apu": apu = args[++i]; break;
                case "--pipe-name": pipeName = args[++i]; break;
                case "--frames": maxFrames = int.Parse(args[++i]); break;
                case "--checkpoint-dir": checkpointDir = args[++i]; break;
                case "--seed": seed = int.Parse(args[++i]); break;
                case "--out": outPath = args[++i]; break;
                case "--screenshot-every": screenshotEvery = int.Parse(args[++i]); break;
                case "--screenshot-dir": screenshotDir = args[++i]; break;
                case "--log-every": logEvery = int.Parse(args[++i]); break;
                case "--auto-start-frame": autoStartFrame = int.Parse(args[++i]); break;
                case "--movie-out": movieOutPath = args[++i]; break;
                case "--auto-export-dir": autoExportDir = args[++i]; break;
                case "--auto-export-every-frames": autoExportEveryFrames = int.Parse(args[++i]); break;
            }
        }

        if (romPath == null)
        {
            Console.Error.WriteLine("Usage: --selfplay --rom <path.nes> [--cpu ID --ppu ID --apu ID] [--pipe-name name] [--frames N] [--checkpoint-dir dir] [--seed N] [--out result.json] [--screenshot-every N --screenshot-dir dir] [--log-every N] [--auto-start-frame N] [--movie-out path.fm2] [--auto-export-dir dir --auto-export-every-frames N]");
            return 2;
        }
        checkpointDir ??= Path.Combine(Path.GetTempPath(), "brokennes_selfplay_checkpoints");

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

            var manager = new SelfPlayManager(new SelfPlayConfig(), checkpointDir, seed: seed);
            bool connected = manager.ConnectPipe(pipeName);
            Console.Error.WriteLine(connected
                ? $"[selfplay] Connected to pipe '{pipeName}'."
                : $"[selfplay] Could not connect to pipe '{pipeName}' - make sure the Python inference server is running (scripts/nesreflex_inference_server.py --protocol v2). Falling back to a no-op policy for unconnected frames.");

            if (autoExportDir != null)
            {
                manager.EnableAutoExport(autoExportDir, nes.ComputeRomMd5(), Path.GetFileNameWithoutExtension(romPath), autoExportEveryFrames);
                Console.Error.WriteLine($"[selfplay] Auto-export enabled -> '{autoExportDir}' (on Game Over, and every {autoExportEveryFrames} frames).");
            }

            if (screenshotDir != null) Directory.CreateDirectory(screenshotDir);

            int frame = 0;
            for (; frame < maxFrames; frame++)
            {
                // Manual/human-equivalent Start press, since the SMB1 profile always blocks the
                // model from pressing Start itself (see SelfPlayManager's constructor comment) -
                // mirrors a human operator kicking off the session via the interactive UI.
                bool[]? userOverride = null;
                if (autoStartFrame >= 0 && frame >= autoStartFrame && frame < autoStartFrame + 4)
                {
                    userOverride = new bool[8];
                    userOverride[3] = true; // Start
                }
                var buttons = manager.ComputeFrameInput(nes, userOverride);
                nes.SetInputs(buttons, null);
                nes.RunFrame();
                if (nes.IsCrashed()) { Console.Error.WriteLine($"[selfplay] Crashed at frame {frame}: {nes.GetCrashInfo()}"); break; }
                manager.OnFrameComplete(nes);

                if (logEvery > 0 && frame % logEvery == 0)
                {
                    Console.Error.WriteLine(
                        $"[selfplay] frame={frame} connected={manager.IsPipeConnected} style={manager.CurrentStyle} " +
                        $"temp={manager.CurrentTemperature:F2} topK={manager.CurrentTopK} checkpoints={manager.Checkpoints.Count} " +
                        $"created={manager.CheckpointsCreated} reloads={manager.ReloadCount} blockedStart={manager.BlockedStartCount} autoStarts={manager.AutoStartPressCount}");
                }
                if (screenshotEvery > 0 && screenshotDir != null && frame % screenshotEvery == 0)
                {
                    var fb = nes.GetFrameBuffer();
                    if (fb.Length == 256 * 240 * 4)
                    {
                        using var db = new DirectBitmap(256, 240);
                        db.CopyFromBytes(fb);
                        using var bmp = db.ToBitmap();
                        bmp.Save(Path.Combine(screenshotDir, $"frame_{frame:D6}.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
            }

            if (movieOutPath != null)
            {
                manager.ExportFm2(movieOutPath, nes.ComputeRomMd5(), Path.GetFileNameWithoutExtension(romPath), nes.GetCpuCoreId(), nes.GetPpuCoreId(), nes.GetApuCoreId());
                Console.Error.WriteLine($"[selfplay] Wrote continuous movie ({manager.InputLog.Count} frames, {manager.ReloadCount} reload(s) pruned) to '{movieOutPath}'.");
            }

            var result = new SelfPlayResult(
                Rom: Path.GetFileName(romPath),
                Cpu: nes.GetCpuCoreId(), Ppu: nes.GetPpuCoreId(), Apu: nes.GetApuCoreId(),
                PipeName: pipeName, EverConnected: connected, ConnectedAtEnd: manager.IsPipeConnected,
                FramesRun: frame, Crashed: nes.IsCrashed(), CrashInfo: nes.IsCrashed() ? nes.GetCrashInfo() : null,
                CheckpointsCreated: manager.CheckpointsCreated, ReloadCount: manager.ReloadCount,
                BlockedStartCount: manager.BlockedStartCount, FinalCheckpointStackSize: manager.Checkpoints.Count,
                MovieFramesExported: movieOutPath != null ? manager.InputLog.Count : null,
                AutoExportCount: manager.AutoExportCount, LastAutoExportPath: manager.LastAutoExportPath);

            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            if (outPath != null) File.WriteAllText(outPath, json);
            Console.WriteLine(json);
            manager.DisconnectPipe();
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

internal sealed record SelfPlayResult(
    string Rom, string Cpu, string Ppu, string Apu, string PipeName, bool EverConnected, bool ConnectedAtEnd,
    int FramesRun, bool Crashed, string? CrashInfo,
    int CheckpointsCreated, int ReloadCount, int BlockedStartCount, int FinalCheckpointStackSize,
    int? MovieFramesExported, int AutoExportCount, string? LastAutoExportPath);
