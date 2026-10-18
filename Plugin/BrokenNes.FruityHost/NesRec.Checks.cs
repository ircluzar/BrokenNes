// Round 8 (record-nes): the checks of the NES recording that need no plugin DLL: `FruityHost nesrec-test [--only id,id] [--no-mutants] [--seconds n]`, and the first half of `selftest --only record-nes`.
//
//   model      the model of the chip (Record/NesApuModel.cs) against the chip itself (APU_FIX): random register writes at random moments (every register, $4015 and $4017 included) and real
//              games (each picked by the survey for a feature: envelopes, length counters, sweeps, duty changes, the linear counter, DMC), compared at the end of every frame on every field
//              the chip's state probe has; a recording's starting state saved and read back
using System.Diagnostics;
using BrokenNes2.Record;
using NesEmulator;
using NesEmulator.Plugin;

namespace BrokenNes.FruityHost;

internal static partial class NesRecChecks
{
    public sealed record Check(string Id, string What, Action<ImodelReport> Run);
    public sealed record CheckOutcome(string Id, string What, bool Passed, double Seconds, ImodelReport Report);
    public sealed record Mutant(string Name, string CatchingCheck, Action<bool> Switch);

    public static double GameSeconds = 30;

    public static readonly Check[] All =
    [
        new("model", "the chip model follows APU_FIX exactly: random writes, and real games compared at every frame's end", Model),
    ];

    public static readonly Mutant[] Mutants =
    [
        new("the model ignores the hardware sweep", "model", on => NesModelMutants.Active = on ? NesModelMutants.SweepIgnored : 0),
        new("the model's envelope decays two steps at a time", "model", on => NesModelMutants.Active = on ? NesModelMutants.EnvelopeTwice : 0),
    ];

    // =====================================================================================================================================================================

    public static void Model(ImodelReport r)
    {
        // 1. random writes on a bare chip: every register, at random clocks, compared after each step of random length (and the saved form read back on the way)
        var rnd = new Random(8);
        int compared = 0, differ = 0;
        string? first = null;
        for (int run = 0; run < 40; run++)
        {
            var chip = new NesApuInstrument("FIX", 44100);
            var apu = (APU_FIX)chip.Apu;
            long clock = 0;
            var m = NesApuModel.From(apu.ProbeControl(), clock);
            if (run == 0)
            {
                var back = NesApuModel.Load(m.Save());
                r.Expect(back != null && back.Save().AsSpan().SequenceEqual(m.Save()), "a model's saved state does not read back to itself");
                var bad = m.Save(); bad[4] = 99;
                r.Expect(NesApuModel.Load(bad) == null, "a saved state with a volume of 99 was read");
            }
            for (int step = 0; step < 4000; step++)
            {
                long dt = rnd.Next(4) switch { 0 => rnd.Next(1, 40), 1 => rnd.Next(1, 3000), 2 => rnd.Next(3000, 20000), _ => rnd.Next(1, 8000) };
                chip.Advance(dt);
                clock += dt;
                while (m.StepToNextEvent(clock)) { }
                if (rnd.Next(3) > 0)
                {
                    int reg = rnd.Next(10) switch { 0 => 0x15, 1 => rnd.Next(20) == 0 ? 0x17 : 0x15, _ => rnd.Next(0x10) };
                    if (reg is 0x09 or 0x0D) reg = 0x0E;
                    int v = rnd.Next(256);
                    if (reg == 0x15) v |= rnd.Next(4) == 0 ? 0 : 0x0F;                 // (mostly enabled, so the channels have something to do)
                    if (reg == 0x17) v &= 0xC0;
                    chip.Write(reg, (byte)v);
                    m.Write(reg, v);
                }
                compared++;
                var why = m.Differs(apu.ProbeApuState());
                if (why != null) { differ++; first ??= $"run {run}, step {step} (clock {clock}): {why}"; }
            }
        }
        r.Expect(differ == 0, $"random writes: the model differs from APU_FIX in {differ} of {compared} comparisons; first: {first}");
        r.Note($"random writes: {compared:N0} comparisons on 40 bare chips, the model equal to APU_FIX in all of them");

        // 2. real games: the model takes the game chip's state between two frames, then follows the game's writes; compared at every frame's end
        int games = 0;
        foreach (var pick in NesRecGames.Model.Select(p => (Fragment: p.Fragment, Script: p.Script, Park: p.Park)).Prepend((Fragment: "VRUN", Script: "", Park: 120)))
        {
            var g = pick.Fragment == "VRUN" ? NesRecGame.Vrun() : NesRecGame.Find(pick.Fragment, pick.Script);
            if (g == null) { r.Warn($"NOT RUN: {pick.Fragment} is not in {NesRecGame.Roster}"); continue; }
            g.Run(pick.Park);
            var apu = g.Apu;
            var m = NesApuModel.From(apu.ProbeControl(), g.M.ApuClock);
            int writes = 0, frames = 0, bad = 0;
            string? why1 = null;
            g.M.Nes.ApuWriteTap = (addr, v, clock) =>
            {
                while (m.StepToNextEvent(clock)) { }
                m.Write(addr - 0x4000, v);
                writes++;
            };
            int n = (int)(GameSeconds * NesAudioMachine.FramesPerSecond);
            for (int f = 0; f < n && !g.M.Crashed; f++)
            {
                g.RunFrame();
                while (m.StepToNextEvent(g.M.ApuClock)) { }
                frames++;
                var why = m.Differs(apu.ProbeApuState());
                if (why != null) { bad++; why1 ??= $"frame {g.Frame}: {why}"; }
            }
            g.M.Nes.ApuWriteTap = null;
            games++;
            r.Expect(bad == 0 && frames > 0, $"{g.Name}: the model differs from the game's chip at {bad} of {frames} frame ends ({writes:N0} writes); first: {why1}");
            r.Note($"{g.Name}: {frames} frames, {writes:N0} writes, the model equal to the chip at every frame's end");
        }
        if (games == 0) r.Warn("NOT RUN: no game of the roster is on this machine (the random writes ran)");
    }

    // =====================================================================================================================================================================

    public static List<CheckOutcome> RunChecks(string? only, Action<string>? log = null)
    {
        var list = new List<CheckOutcome>();
        foreach (var c in All)
        {
            if (only != null && !only.Split(',').Contains(c.Id)) continue;
            var r = new ImodelReport();
            var sw = Stopwatch.StartNew();
            try { c.Run(r); }
            catch (Exception e) { r.Failures.Add($"exception {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            var o = new CheckOutcome(c.Id, c.What, r.Failures.Count == 0, sw.Elapsed.TotalSeconds, r);
            list.Add(o);
            bool notRun = r.Assertions == 0 && r.Warnings.Any(w => w.StartsWith("NOT RUN"));
            string verdict = !o.Passed ? "FAIL" : notRun ? "NOT RUN" : r.Warnings.Count > 0 ? "WARN" : "PASS";
            log?.Invoke($"  {verdict,-7} {c.Id,-11} {r.Assertions,7} assertions  [{o.Seconds:0.0}s]" + (r.Notes.Count > 0 ? "\n        " + string.Join("\n        ", r.Notes) : "") + (r.Warnings.Count > 0 ? "\n        warning: " + string.Join("\n        warning: ", r.Warnings) : "") + (r.Failures.Count > 0 ? "\n        FAILED: " + string.Join("\n        FAILED: ", r.Failures.Take(10)) + (r.Failures.Count > 10 ? $"\n        ... and {r.Failures.Count - 10} more" : "") : ""));
        }
        return list;
    }

    public static (List<string> Lines, int Missed) RunMutants(string? only)
    {
        var lines = new List<string>();
        int missed = 0;
        double keep = GameSeconds;
        GameSeconds = Math.Min(GameSeconds, 8);                  // (a deliberate bug shows within seconds: the mutant passes do not need the full takes)
        try
        {
            foreach (var mu in Mutants)
            {
                if (only != null && !only.Split(',').Contains(mu.CatchingCheck)) continue;
                var check = All.First(c => c.Id == mu.CatchingCheck);
                var r = new ImodelReport();
                mu.Switch(true);
                try { check.Run(r); }
                catch (Exception e) { r.Failures.Add($"exception {e.GetType().Name}: {e.Message}"); }
                finally { mu.Switch(false); }
                bool caught = r.Failures.Count > 0;
                if (!caught) missed++;
                lines.Add($"  {(caught ? "caught" : "MISSED")}  [{mu.CatchingCheck}] {mu.Name}: {r.Failures.Count} failing assertions" + (caught ? $", e.g. {(r.Failures[0].Length > 200 ? r.Failures[0][..197] + "..." : r.Failures[0])}" : ""));
            }
        }
        finally { GameSeconds = keep; }
        return (lines, missed);
    }

    public static int Cli(string[] args)
    {
        string? only = null;
        for (int i = 0; i < args.Length - 1; i++) { if (args[i] == "--only") only = args[i + 1]; if (args[i] == "--seconds") GameSeconds = double.Parse(args[i + 1]); }
        bool mutants = !args.Contains("--no-mutants");
        Console.WriteLine("BrokenNes.FruityHost nesrec-test (round 8: recording a NES game's music; no plugin DLL involved)");
        var results = RunChecks(only, Console.WriteLine);
        int failed = results.Count(o => !o.Passed), missed = 0;
        if (mutants)
        {
            Console.WriteLine("deliberate bugs (each must be caught by the check named):");
            var (lines, m) = RunMutants(only);
            foreach (var l in lines) Console.WriteLine(l);
            missed = m;
        }
        int notRun = results.Count(o => o.Report.Assertions == 0 && o.Report.Warnings.Any(w => w.StartsWith("NOT RUN")));
        Console.WriteLine($"{results.Count - failed - notRun} checks passed, {notRun} NOT RUN, {failed} failed, {results.Sum(o => o.Report.Assertions)} assertions; {missed} deliberate bugs missed");
        return failed == 0 && missed == 0 ? 0 : 1;
    }
}
