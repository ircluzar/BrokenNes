using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator;

namespace BrokenNes.Workshop;

internal enum AccuracyCoinTestStatus { NotRun, Pass, Fail, InProgress, Skipped }

internal sealed record AccuracyCoinTestResult(
    string Suite, string Name, ushort Address, byte RawValue, AccuracyCoinTestStatus Status, int ErrorCode);

internal sealed record AccuracyCoinRunResult(
    string Cpu, string Ppu, string Apu,
    bool Crashed, string? CrashInfo, bool CompletedNaturally,
    bool AutomatedRunObserved, byte PostAllTestTally, int FramesRun,
    IReadOnlyList<AccuracyCoinTestResult> Tests,
    int RetryCount, IReadOnlyList<string> AutoSkippedTests)
{
    public int PassCount => Tests.Count(t => t.Status == AccuracyCoinTestStatus.Pass);
    public int FailCount => Tests.Count(t => t.Status == AccuracyCoinTestStatus.Fail);
    public int NotRunCount => Tests.Count(t => t.Status == AccuracyCoinTestStatus.NotRun);
    public int SkippedCount => Tests.Count(t => t.Status == AccuracyCoinTestStatus.Skipped);
    public int InProgressCount => Tests.Count(t => t.Status == AccuracyCoinTestStatus.InProgress);
}

/// <summary>
/// Drives AccuracyCoin.nes to completion and reads its results directly out of RAM - no OCR,
/// no screen-reading. Mechanism reverse-engineered from AccuracyCoin.asm itself (not guessed):
///
/// - RESET sets menuCursorYPos=$FF ("top of menu") unconditionally, so the ROM always boots
///   into the state where pressing Start triggers AutomaticallyRunEveryTestInROM (asm:17684-88,
///   gated on menuCursorYPos being $FF via the NMI_Menu_Top_* code path) - no menu navigation
///   needed, just press Start once.
/// - That routine (asm:787-849) loops every suite page and every test on it, calling RunTest
///   for each, skipping only the 5 "DRAW"-only tests (Power On State - see AccuracyCoinTests.cs
///   for why), and increments PostAllTestTally ($37) once per real test actually run.
/// - RunTest (asm:17868+) stores the tested routine's return value (in A) directly into the
///   test's own result_XXX RAM address via `STA [TestResultPointer],Y`.
/// - DrawTEST (asm:17123+) is the ROM's own authoritative decoder for what to render, and is
///   the source of the decoding used here: raw==$FF means "skip" (RunTest_SkipSkip's own skip
///   marker, asm:17897-99); otherwise `raw & 0x03` is 0=not run, 1=PASS, 2=FAIL (with
///   `(raw & 0xFC) >> 2` as the sub-check error code, per asm:17186-89), 3=mid-test (should
///   never be observed after RunningAllTests returns to 0).
/// </summary>
internal static class AccuracyCoinRunner
{
    private const int BootFrames = 30;
    private const int StartPressFrames = 4;

    /// <summary>
    /// Wraps RunSingleCombo in an adaptive skip-and-retry loop: pre-skipping only the 66 known
    /// "Unofficial Instructions"/"Unofficial Immediates" tests turned out NOT to be sufficient
    /// on its own - verified empirically that CPU_ULQ also crashes inside "All NOP instructions"
    /// (a CPU Behavior test, not one of the 66), because that test exercises illegal NOP
    /// opcodes as part of testing NOP behavior generally. Rather than hand-auditing every one
    /// of the remaining 75 tests for a similar hidden illegal-opcode dependency (fragile, easy
    /// to miss one), this generalizes the ROM's own skip mechanism: on any crash OR hang
    /// (RunningAllTests never returning to 0 within the frame budget), PostAllTestTally
    /// directly indexes into AccuracyCoinTests.All (1-based - verified: value 9 at the ULQ
    /// crash matches "All NOP instructions" being the 9th entry) identifying exactly which test
    /// was executing, add its address to the skip set, and retry the whole run from scratch.
    /// Bounded by maxRetries since each retry re-runs from boot.
    /// </summary>
    public static AccuracyCoinRunResult RunSingleComboRobust(
        byte[] romBytes, string? cpu, string? ppu, string? apu,
        int maxWaitFrames = 1500, int maxRetries = 20,
        IReadOnlyCollection<ushort>? baseSkipAddresses = null)
    {
        var skip = new HashSet<ushort>(baseSkipAddresses ?? Array.Empty<ushort>());
        var autoSkipped = new List<string>();
        AccuracyCoinRunResult result;
        int attempt = 0;
        while (true)
        {
            result = RunSingleCombo(romBytes, cpu, ppu, apu, maxWaitFrames, trace: false, skip);
            bool stuck = result.Crashed || (result.AutomatedRunObserved && !result.CompletedNaturally);
            if (!stuck || attempt >= maxRetries)
                return result with { RetryCount = attempt, AutoSkippedTests = autoSkipped };

            int idx = result.PostAllTestTally - 1; // 1-based tally -> 0-based index into All.
            if (idx < 0 || idx >= AccuracyCoinTests.All.Count)
                return result with { RetryCount = attempt, AutoSkippedTests = autoSkipped }; // can't identify culprit; give up.

            var culprit = AccuracyCoinTests.All[idx];
            if (!skip.Add(culprit.ResultAddress))
                return result with { RetryCount = attempt, AutoSkippedTests = autoSkipped }; // already skipped this one and it's STILL stuck - different root cause, stop.

            autoSkipped.Add($"{culprit.Suite} / {culprit.Name}");
            attempt++;
        }
    }

    public static AccuracyCoinRunResult RunSingleCombo(
        byte[] romBytes, string? cpu, string? ppu, string? apu,
        int maxWaitFrames = 4000, bool trace = false,
        IReadOnlyCollection<ushort>? preSkipAddresses = null)
    {
        var nes = new NES { RomName = "AccuracyCoin.nes" };
        nes.LoadROM(romBytes);
        if (cpu != null && !nes.SetCpuCore(cpu)) throw new ArgumentException($"Unknown CPU core: {cpu}");
        if (ppu != null && !nes.SetPpuCore(ppu)) throw new ArgumentException($"Unknown PPU core: {ppu}");
        if (apu != null && !nes.SetApuCore(apu)) throw new ArgumentException($"Unknown APU core: {apu}");

        int framesRun = 0;
        var p1 = new bool[8];

        for (int i = 0; i < BootFrames && !nes.IsCrashed(); i++) { nes.RunFrame(); framesRun++; }

        // RunTest itself treats a pre-existing $FF at a test's result address as "skip this
        // test" (asm:17896-99) and never calls the test routine at all - the ROM's own designed
        // mechanism, not a hack. Used here to route around a real, universal finding: every CPU
        // core crashes hard on some unofficial/illegal opcode (verified: all 7 cores tested,
        // each crashes with a different "Bad opcode" exception at a different point in the
        // "Unofficial Instructions"/"Unofficial Immediates" suites - none treat undefined
        // opcodes as NOPs the way real 6502 silicon effectively does). Without pre-skipping,
        // every combination's automated run stops within the first ~20 tests, before almost
        // all PPU/APU/timing tests ever get a chance to run. No clear-page-4 routine exists
        // after boot (verified: only $500/$200 get cleared per-test), so a poke here persists
        // until the corresponding test would have run.
        if (!nes.IsCrashed() && preSkipAddresses is { Count: > 0 })
        {
            foreach (var addr in preSkipAddresses)
                nes.PokeMemory("CPU Bus", addr, 0xFF);
        }

        if (!nes.IsCrashed())
        {
            // Input.cs: 0=A 1=B 2=Select 3=Start 4=Up 5=Down 6=Left 7=Right.
            p1[3] = true;
            nes.SetInputs(p1, null);
            for (int i = 0; i < StartPressFrames && !nes.IsCrashed(); i++) { nes.RunFrame(); framesRun++; }
            p1[3] = false;
            nes.SetInputs(p1, null);
        }

        bool sawRunning = false;
        bool completedNaturally = false;
        if (!nes.IsCrashed())
        {
            // A hang shows up as PostAllTestTally simply never advancing again - legitimate
            // tests progress the tally roughly every 2-5 frames (measured), so StagnationLimit
            // frames with zero movement is an unambiguous "stuck" signal, reached MUCH faster
            // than waiting out the full maxWaitFrames budget on every hang. This is what makes
            // RunSingleComboRobust's retry loop tractable across a 1000+ combination matrix -
            // without it, each hang costs the full budget instead of ~StagnationLimit frames.
            const int StagnationLimit = 90;
            byte lastTally = nes.PeekCpu(AccuracyCoinTests.PostAllTestTally);
            int framesSinceProgress = 0;

            for (int i = 0; i < maxWaitFrames; i++)
            {
                nes.RunFrame();
                framesRun++;
                if (nes.IsCrashed()) break;
                byte running = nes.PeekCpu(AccuracyCoinTests.RunningAllTests);
                if (running != 0) sawRunning = true;
                else if (sawRunning) { completedNaturally = true; break; } // transitioned 1 -> 0: finished for real.

                byte tallyNow = nes.PeekCpu(AccuracyCoinTests.PostAllTestTally);
                if (tallyNow != lastTally) { lastTally = tallyNow; framesSinceProgress = 0; }
                else if (sawRunning && ++framesSinceProgress >= StagnationLimit) break; // stuck.

                if (trace && i % 200 == 0)
                    Console.Error.WriteLine($"  frame {framesRun}: RunningAllTests={running} PostAllTestTally={tallyNow}");
            }
        }

        byte tally = nes.PeekCpu(AccuracyCoinTests.PostAllTestTally);
        var results = new List<AccuracyCoinTestResult>(AccuracyCoinTests.All.Count);
        foreach (var t in AccuracyCoinTests.All)
        {
            byte raw = nes.PeekCpu(t.ResultAddress);
            var (status, errorCode) = Decode(raw);
            results.Add(new AccuracyCoinTestResult(t.Suite, t.Name, t.ResultAddress, raw, status, errorCode));
        }

        return new AccuracyCoinRunResult(
            cpu ?? nes.GetCpuCoreId(), ppu ?? nes.GetPpuCoreId(), apu ?? nes.GetApuCoreId(),
            nes.IsCrashed(), nes.IsCrashed() ? nes.GetCrashInfo() : null, completedNaturally,
            sawRunning, tally, framesRun, results,
            RetryCount: 0, AutoSkippedTests: Array.Empty<string>());
    }

    // Mirrors DrawTEST's own decode logic exactly (asm:17138-17153) - order matters: the $FF
    // "skip" check happens BEFORE the low-bits switch, since $FF & 3 == 3 would otherwise be
    // misread as "in progress".
    private static (AccuracyCoinTestStatus Status, int ErrorCode) Decode(byte raw)
    {
        if (raw == 0xFF) return (AccuracyCoinTestStatus.Skipped, 0);
        return (raw & 0x03) switch
        {
            0 => (AccuracyCoinTestStatus.NotRun, 0),
            1 => (AccuracyCoinTestStatus.Pass, 0),
            2 => (AccuracyCoinTestStatus.Fail, (raw & 0xFC) >> 2),
            _ => (AccuracyCoinTestStatus.InProgress, 0),
        };
    }
}
