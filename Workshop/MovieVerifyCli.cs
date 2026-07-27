using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BrokenNes.Workshop.Tas;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// UAT for the self-play "continuous best-of-all-segments" movie export (SelfPlayManager.ExportFm2):
/// replays the exported .fm2 on a fresh NES instance, using the exact cores recorded in its
/// "path.witness.json" sidecar, computing the same per-frame FrameWitness hash the live self-play
/// session recorded, and compares frame-by-frame. This proves the export reproduces the live
/// session's actual emulator state (RAM + CPU regs + PPU state + framebuffer) frame for frame - not
/// just "the movie replays without crashing" - catching any dropped frame, duplicated frame, or
/// off-by-one at a truncation boundary. A frame-count mismatch alone (movie vs witness) is reported
/// as a failure even if every compared frame's hash matches, since that's exactly what a lost or
/// extra frame looks like.
///
/// Usage: --verify-selfplay-movie --movie path.fm2 --rom path.nes [--witness path.fm2.witness.json]
///        [--out result.json]
/// </summary>
internal static class MovieVerifyCli
{
    public static int Run(string[] args)
    {
        string? moviePath = null, romPath = null, witnessPath = null, outPath = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--movie": moviePath = args[++i]; break;
                case "--rom": romPath = args[++i]; break;
                case "--witness": witnessPath = args[++i]; break;
                case "--out": outPath = args[++i]; break;
            }
        }

        if (moviePath == null || romPath == null)
        {
            Console.Error.WriteLine("Usage: --verify-selfplay-movie --movie <path.fm2> --rom <path.nes> [--witness path.fm2.witness.json] [--out result.json]");
            return 2;
        }
        witnessPath ??= moviePath + ".witness.json";

        WitnessSidecar? sidecar;
        try
        {
            sidecar = JsonSerializer.Deserialize<WitnessSidecar>(File.ReadAllText(witnessPath));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read witness sidecar '{witnessPath}': {ex.Message}");
            return 2;
        }
        if (sidecar == null)
        {
            Console.Error.WriteLine($"Witness sidecar '{witnessPath}' parsed to null.");
            return 2;
        }

        Fm2Header header;
        IReadOnlyList<Fm2Frame> frames;
        try { (header, frames) = Fm2Movie.Load(moviePath); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to parse movie: {ex.Message}"); return 3; }

        byte[] romBytes;
        try { romBytes = File.ReadAllBytes(romPath); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to read ROM: {ex.Message}"); return 2; }

        try
        {
            var nes = new NES { RomName = Path.GetFileName(romPath) };
            nes.LoadROM(romBytes);
            // Sidecar stores the full type name (e.g. "CPU_SPD", from NES.GetCpuCoreId()) but
            // SetCpuCore/SetPpuCore/SetApuCore take the reflection "suffix id" (e.g. "SPD").
            if (sidecar.Cpu != null && !nes.SetCpuCore(CoreRegistry.ExtractSuffix(sidecar.Cpu, "CPU_"))) { Console.Error.WriteLine($"Unknown CPU core in sidecar: {sidecar.Cpu}"); return 3; }
            if (sidecar.Ppu != null && !nes.SetPpuCore(CoreRegistry.ExtractSuffix(sidecar.Ppu, "PPU_"))) { Console.Error.WriteLine($"Unknown PPU core in sidecar: {sidecar.Ppu}"); return 3; }
            if (sidecar.Apu != null && !nes.SetApuCore(CoreRegistry.ExtractSuffix(sidecar.Apu, "APU_"))) { Console.Error.WriteLine($"Unknown APU core in sidecar: {sidecar.Apu}"); return 3; }

            var romMd5 = nes.ComputeRomMd5();
            bool romMd5Mismatch = sidecar.RomMd5Hex != null &&
                !string.Equals(Convert.ToHexString(romMd5).ToLowerInvariant(), sidecar.RomMd5Hex, StringComparison.OrdinalIgnoreCase);

            var expected = sidecar.Frames ?? new List<FrameWitnessBreakdown>();
            int framesToCompare = Math.Min(frames.Count, expected.Count);
            int firstMismatchFrame = -1;
            FrameWitnessBreakdown? firstMismatchExpected = null, firstMismatchActual = null;
            List<string>? firstMismatchComponents = null;
            int mismatchedFrameCount = 0;
            int lastMismatchFrame = -1;
            int framesCompared = 0;
            bool crashed = false;
            for (; framesCompared < framesToCompare; framesCompared++)
            {
                var frame = frames[framesCompared];
                if (frame.Reset) nes.Reset();
                nes.SetInputs(frame.P1, frame.P2);
                nes.RunFrame();
                if (nes.IsCrashed()) { crashed = true; break; }

                var actual = FrameWitness.ComputeBreakdown(nes);
                var exp = expected[framesCompared];
                if (actual != exp)
                {
                    mismatchedFrameCount++;
                    lastMismatchFrame = framesCompared;
                    if (firstMismatchFrame < 0)
                    {
                        firstMismatchFrame = framesCompared;
                        firstMismatchExpected = exp;
                        firstMismatchActual = actual;
                        firstMismatchComponents = new List<string>();
                        if (actual.Ram != exp.Ram) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.Ram));
                        if (actual.CpuRegs != exp.CpuRegs) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.CpuRegs));
                        if (actual.PpuRegs != exp.PpuRegs) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.PpuRegs));
                        if (actual.Oam != exp.Oam) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.Oam));
                        if (actual.Palette != exp.Palette) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.Palette));
                        if (actual.Nametables != exp.Nametables) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.Nametables));
                        if (actual.Framebuffer != exp.Framebuffer) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.Framebuffer));
                    }
                }
            }

            bool frameCountMismatch = frames.Count != expected.Count;
            bool pass = !romMd5Mismatch && !crashed && !frameCountMismatch && firstMismatchFrame < 0;

            var result = new MovieVerifyResult(
                Movie: Path.GetFileName(moviePath),
                Rom: Path.GetFileName(romPath),
                Cpu: nes.GetCpuCoreId(), Ppu: nes.GetPpuCoreId(), Apu: nes.GetApuCoreId(),
                RomMd5Mismatch: romMd5Mismatch,
                MovieFrameCount: frames.Count,
                WitnessFrameCount: expected.Count,
                FrameCountMismatch: frameCountMismatch,
                FramesCompared: framesCompared,
                Crashed: crashed,
                CrashInfo: crashed ? nes.GetCrashInfo() : null,
                FirstMismatchFrame: firstMismatchFrame,
                FirstMismatchComponents: firstMismatchComponents,
                FirstMismatchExpected: firstMismatchExpected,
                FirstMismatchActual: firstMismatchActual,
                MismatchedFrameCount: mismatchedFrameCount,
                LastMismatchFrame: lastMismatchFrame,
                Pass: pass);

            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            if (outPath != null) File.WriteAllText(outPath, json);
            Console.WriteLine(json);
            return pass ? 0 : 1;
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

internal sealed record MovieVerifyResult(
    string Movie, string Rom, string Cpu, string Ppu, string Apu,
    bool RomMd5Mismatch, int MovieFrameCount, int WitnessFrameCount, bool FrameCountMismatch,
    int FramesCompared, bool Crashed, string? CrashInfo, int FirstMismatchFrame,
    List<string>? FirstMismatchComponents, FrameWitnessBreakdown? FirstMismatchExpected, FrameWitnessBreakdown? FirstMismatchActual,
    int MismatchedFrameCount, int LastMismatchFrame,
    bool Pass);
