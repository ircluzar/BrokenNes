using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BrokenNes.Workshop.Tas;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// Focused diagnostic for root-causing a self-play movie desync (see MovieVerifyCli /
/// project_tas_ml_integration memory): is NES.SaveState()/LoadState() itself lossy, independent of
/// anything self-play-specific? Plays two identical NES instances with the exact same deterministic
/// input sequence; instance B additionally does a SaveState+LoadState round trip partway through
/// (zero frames elapse between save and load - the purest possible test of the serialization
/// itself), then compares EVERY subsequent frame (not just a single checkpoint at the end, which is
/// too coarse to catch a transient desync that resolves itself within the continuation window - the
/// exact failure mode MovieVerifyCli found in real self-play movies).
///
/// Usage: --diag-savestate-roundtrip --rom path.nes [--cpu ID --ppu ID --apu ID]
///        [--warmup-frames N] [--continue-frames N]
/// </summary>
internal static class SaveStateRoundtripDiagCli
{
    public static int Run(string[] args)
    {
        string? romPath = null, cpu = null, ppu = null, apu = null;
        int warmupFrames = 300, continueFrames = 300;
        bool strict = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rom": romPath = args[++i]; break;
                case "--cpu": cpu = args[++i]; break;
                case "--ppu": ppu = args[++i]; break;
                case "--apu": apu = args[++i]; break;
                case "--warmup-frames": warmupFrames = int.Parse(args[++i]); break;
                case "--continue-frames": continueFrames = int.Parse(args[++i]); break;
                case "--strict": strict = true; break;
            }
        }
        if (romPath == null)
        {
            Console.Error.WriteLine("Usage: --diag-savestate-roundtrip --rom <path.nes> [--cpu ID --ppu ID --apu ID] [--warmup-frames N] [--continue-frames N] [--strict]");
            return 2;
        }

        byte[] romBytes = File.ReadAllBytes(romPath);

        // A deterministic "active play" input pattern (Start briefly, then walk right holding B,
        // jumping periodically) instead of no-input at all, to exercise real physics/scrolling/
        // collision state as well as idle-loop polling.
        const int Start = 3, A = 0, B = 1, Right = 7;
        bool[] InputForFrame(int i)
        {
            var input = new bool[8];
            if (i is >= 10 and < 14) input[Start] = true;
            else if (i >= 20)
            {
                input[Right] = true;
                input[B] = true;
                if (i % 45 < 12) input[A] = true;
            }
            return input;
        }

        NES MakeNes()
        {
            var nes = new NES { RomName = Path.GetFileName(romPath) };
            nes.LoadROM(romBytes);
            if (cpu != null) nes.SetCpuCore(cpu);
            if (ppu != null) nes.SetPpuCore(ppu);
            if (apu != null) nes.SetApuCore(apu);
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
            return nes;
        }

        // Path A: continuous play, no savestate involved at all. Records a breakdown at every
        // continuation frame so path B can be diffed frame-by-frame, not just at the end.
        var nesA = MakeNes();
        for (int i = 0; i < warmupFrames; i++) { nesA.SetInputs(InputForFrame(i), null); nesA.RunFrame(); }
        var breakdownAAtWarmup = FrameWitness.ComputeBreakdown(nesA);
        var aPerFrame = new List<FrameWitnessBreakdown>(continueFrames);
        for (int i = 0; i < continueFrames; i++)
        {
            nesA.SetInputs(InputForFrame(warmupFrames + i), null);
            nesA.RunFrame();
            aPerFrame.Add(FrameWitness.ComputeBreakdown(nesA));
        }

        // Path B: identical warmup, then an in-place SaveState+LoadState round trip (zero frames
        // elapse between save and load), then the identical continuation, also recorded per frame.
        var nesB = MakeNes();
        for (int i = 0; i < warmupFrames; i++) { nesB.SetInputs(InputForFrame(i), null); nesB.RunFrame(); }
        string json = nesB.SaveState();
        nesB.LoadState(json);
        var breakdownBAfterRoundtrip = FrameWitness.ComputeBreakdown(nesB);
        int firstMismatchFrame = -1, lastMismatchFrame = -1, mismatchedFrameCount = 0;
        List<string>? firstMismatchComponents = null;
        for (int i = 0; i < continueFrames; i++)
        {
            nesB.SetInputs(InputForFrame(warmupFrames + i), null);
            nesB.RunFrame();
            var actual = FrameWitness.ComputeBreakdown(nesB);
            var expected = aPerFrame[i];
            if (actual != expected)
            {
                mismatchedFrameCount++;
                lastMismatchFrame = i;
                if (firstMismatchFrame < 0)
                {
                    firstMismatchFrame = i;
                    firstMismatchComponents = new List<string>();
                    if (actual.Ram != expected.Ram) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.Ram));
                    if (actual.CpuRegs != expected.CpuRegs) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.CpuRegs));
                    if (actual.PpuRegs != expected.PpuRegs) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.PpuRegs));
                    if (actual.Oam != expected.Oam) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.Oam));
                    if (actual.Palette != expected.Palette) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.Palette));
                    if (actual.Nametables != expected.Nametables) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.Nametables));
                    if (actual.Framebuffer != expected.Framebuffer) firstMismatchComponents.Add(nameof(FrameWitnessBreakdown.Framebuffer));
                }
            }
        }

        bool immediateRoundtripLossy = breakdownAAtWarmup != breakdownBAfterRoundtrip;
        var immediateComponentDiff = new
        {
            Ram = breakdownAAtWarmup.Ram != breakdownBAfterRoundtrip.Ram,
            CpuRegs = breakdownAAtWarmup.CpuRegs != breakdownBAfterRoundtrip.CpuRegs,
            PpuRegs = breakdownAAtWarmup.PpuRegs != breakdownBAfterRoundtrip.PpuRegs,
            Oam = breakdownAAtWarmup.Oam != breakdownBAfterRoundtrip.Oam,
            Palette = breakdownAAtWarmup.Palette != breakdownBAfterRoundtrip.Palette,
            Nametables = breakdownAAtWarmup.Nametables != breakdownBAfterRoundtrip.Nametables,
            Framebuffer = breakdownAAtWarmup.Framebuffer != breakdownBAfterRoundtrip.Framebuffer,
        };

        var result = new
        {
            Rom = Path.GetFileName(romPath),
            Cpu = nesA.GetCpuCoreId(),
            Ppu = nesA.GetPpuCoreId(),
            Apu = nesA.GetApuCoreId(),
            WarmupFrames = warmupFrames,
            ContinueFrames = continueFrames,
            ImmediateRoundtripLossy = immediateRoundtripLossy,
            ImmediateComponentsDiffering = immediateRoundtripLossy ? immediateComponentDiff : null,
            FirstMismatchFrameDuringContinuation = firstMismatchFrame,
            FirstMismatchComponents = firstMismatchComponents,
            LastMismatchFrameDuringContinuation = lastMismatchFrame,
            MismatchedFrameCount = mismatchedFrameCount,
            ConvergesBeforeEnd = mismatchedFrameCount > 0 && lastMismatchFrame < continueFrames - 1,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return (immediateRoundtripLossy || mismatchedFrameCount > 0) ? 1 : 0;
    }
}
