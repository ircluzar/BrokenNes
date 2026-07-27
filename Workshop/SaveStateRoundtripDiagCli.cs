using System;
using System.IO;
using System.Text.Json;
using BrokenNes.Workshop.Tas;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// Focused diagnostic for one specific hypothesis raised while root-causing a self-play movie
/// desync (see MovieVerifyCli / project_tas_ml_integration memory): is NES.SaveState()/LoadState()
/// itself lossy, independent of anything self-play-specific? Plays two identical NES instances with
/// the exact same deterministic input sequence; instance B additionally does a SaveState+LoadState
/// round trip partway through (with zero frames elapsing between save and load - the purest
/// possible test of the serialization itself). If A and B still diverge, the savestate format is
/// missing some piece of emulator state.
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
            }
        }
        if (romPath == null)
        {
            Console.Error.WriteLine("Usage: --diag-savestate-roundtrip --rom <path.nes> [--cpu ID --ppu ID --apu ID] [--warmup-frames N] [--continue-frames N]");
            return 2;
        }

        byte[] romBytes = File.ReadAllBytes(romPath);

        // A deterministic "active play" input pattern (Start briefly, then walk right holding B,
        // jumping periodically) instead of no-input at all - an earlier idle-at-title-screen run of
        // this exact diagnostic self-corrected after continuing, so this exercises real physics/
        // scrolling/collision state to see if that's what a plain idle test misses.
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
            return nes;
        }

        // Path A: continuous play, no savestate involved at all.
        var nesA = MakeNes();
        for (int i = 0; i < warmupFrames; i++) { nesA.SetInputs(InputForFrame(i), null); nesA.RunFrame(); }
        uint hashAAtWarmup = FrameWitness.Compute(nesA);
        var breakdownAAtWarmup = FrameWitness.ComputeBreakdown(nesA);
        for (int i = 0; i < continueFrames; i++) { nesA.SetInputs(InputForFrame(warmupFrames + i), null); nesA.RunFrame(); }
        uint hashAFinal = FrameWitness.Compute(nesA);

        // Path B: identical warmup, then an in-place SaveState+LoadState round trip (zero frames
        // elapse between save and load), then the identical continuation.
        var nesB = MakeNes();
        for (int i = 0; i < warmupFrames; i++) { nesB.SetInputs(InputForFrame(i), null); nesB.RunFrame(); }
        string json = nesB.SaveState();
        nesB.LoadState(json);
        uint hashBAfterRoundtrip = FrameWitness.Compute(nesB);
        var breakdownBAfterRoundtrip = FrameWitness.ComputeBreakdown(nesB);
        for (int i = 0; i < continueFrames; i++) { nesB.SetInputs(InputForFrame(warmupFrames + i), null); nesB.RunFrame(); }
        uint hashBFinal = FrameWitness.Compute(nesB);

        bool immediateRoundtripLossy = hashAAtWarmup != hashBAfterRoundtrip;
        bool divergesAfterContinuing = hashAFinal != hashBFinal;

        var componentDiff = new
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
            HashAtWarmup_NoRoundtrip = hashAAtWarmup,
            HashAtWarmup_AfterSaveLoadRoundtrip = hashBAfterRoundtrip,
            ImmediateRoundtripLossy = immediateRoundtripLossy,
            ComponentsDiffering = immediateRoundtripLossy ? componentDiff : null,
            HashFinal_NoRoundtrip = hashAFinal,
            HashFinal_WithRoundtrip = hashBFinal,
            DivergesAfterContinuing = divergesAfterContinuing,
            Conclusion = immediateRoundtripLossy
                ? "SaveState/LoadState is lossy immediately - some emulator state isn't captured/restored."
                : divergesAfterContinuing
                    ? "SaveState/LoadState round-trips perfectly by itself, but the two paths diverge after continuing play - something about resuming from a load behaves differently than continuous execution."
                    : "No divergence detected - SaveState/LoadState round-trips losslessly for this test.",
        };

        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return (immediateRoundtripLossy || divergesAfterContinuing) ? 1 : 0;
    }
}
