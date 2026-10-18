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
        new("direct", "NesDirect = Direct mode's old arithmetic; every period reachable by a key and a bend", Direct),
        new("capture", "every write the bus reported kept, DMC samples copied, nothing allocated per write, losses counted, the saved form refuses damage", Capture),
        new("transcribe", "real games and corrupted ones transcribed and replayed from the MIDI bytes through the presets' register code: every value exact or reported", Transcribe),
        new("smf", "the NES file read back by an independent Python parser and by VRUN's", Smf),
        new("section", "REC1 version 2 round trip and refusals", Section),
        new("live", "live MIDI out: every transcribed note struck live at the same moment and key, every note ended, nothing allocated", Live),
        new("vrun-song", "songs converted by VRUN's converter, played by its engine, recorded: the transcription = VRUN's decoder model frame by frame; the tempo = the MIDI file's", VrunSong),
        new("match", "FL's import-and-Replace with NES tracks: every track found from its own notes, the duty tracks of duty-changing notes told apart by FL's velocity", Match),
    ];

    private static Action<bool> Rec(int code) => on => RecMutants.Active = on ? code : 0;

    public static readonly Mutant[] Mutants =
    [
        new("the model ignores the hardware sweep", "model", on => NesModelMutants.Active = on ? NesModelMutants.SweepIgnored : 0),
        new("the model's envelope decays two steps at a time", "model", on => NesModelMutants.Active = on ? NesModelMutants.EnvelopeTwice : 0),
        new("pitch bends one register step off", "direct", Rec(NesTranscribeMutants.BendOffByOne)),
        new("every 1,000th write lost without a word", "capture", Rec(NesRecMutants.CaptureLosesWrites)),
        new("controller 7 one value past its interval", "transcribe", Rec(NesTranscribeMutants.VolumeOffByOne)),
        new("pitch bends one register step off (in a transcription)", "transcribe", Rec(NesTranscribeMutants.BendOffByOne)),
        new("the duty tracks' presets all at 50%", "transcribe", Rec(NesTranscribeMutants.DutyDropped)),
        new("note starts quantised to the 16th grid", "transcribe", Rec(NesTranscribeMutants.NotesQuantised)),
        new("live MIDI out sends nothing", "live", Rec(NesLiveMutants.Silent)),
        new("live note-offs lost", "live", Rec(NesLiveMutants.NoteOffLost)),
        new("the copies of a duty-changing note keep the same velocity", "match", Rec(NesTranscribeMutants.CopiesNotDistinct)),
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

    /// <summary>`FruityHost nesrec-dump vrun|&lt;roster name&gt; [seconds] [park frames] [script]`: a recording's facts, tracks and first notes (a development aid).</summary>
    public static int Dump(string[] args)
    {
        string which = args.Length > 1 ? args[1] : "vrun";
        double secs = args.Length > 2 ? double.Parse(args[2]) : 20;
        int park = args.Length > 3 ? int.Parse(args[3]) : 300;
        string script = args.Length > 4 ? args[4] : "300:start,4";
        var g = which == "vrun" ? NesRecGame.Vrun() : NesRecGame.Find(which, script);
        if (g == null) { Console.WriteLine("no ROM"); return 2; }
        g.Run(park);
        var s = g.Record((int)(secs * NesAudioMachine.FramesPerSecond), which);
        var sw = Stopwatch.StartNew();
        var tr = NesTranscribe.Run(s, new NesOptions { Name = which });
        Console.WriteLine($"{g.Name}: {s.Seconds:0.0} s, {s.Count} entries ({g.TapWrites} writes on the bus), {s.Dmc.Length} DMC copies, transcribed in {sw.ElapsedMilliseconds} ms; {tr.Notes.Count} notes");
        foreach (var f in tr.Facts) Console.WriteLine("  " + f);
        if (args.Contains("--short"))
            foreach (var n in NesNotes.Extract(s, out _).Where(n => n.End - n.Start < NesTranscribe.MinHold && n.Channel != 4).Take(20))
                Console.WriteLine($"  short: ch {n.Channel} {n.StartSeconds:0.00000}-{n.End / NesSession.CpuHz:0.00000} s ({n.End - n.Start} cycles) {(n.Struck ? "struck" : "became")} end {n.EndedBy}; periods {string.Join(" ", n.Periods.Select(v => v.V.ToString("X3")))}; vols {string.Join(" ", n.Vols.Select(v => v.V))}");
        foreach (var t in tr.Tracks)
        {
            Console.WriteLine($"  track {t.Index + 1} '{t.Name}': {t.Notes.Count} notes, ch {t.MidiChannel + 1}, bend {t.BendRange}, pitch {t.PitchExact}/{t.PitchValues}, vol {t.VolExact}/{t.VolValues}, gated {t.Gated}{(t.PresetProblem != null ? ", preset: " + t.PresetProblem : "")}{(t.CannotPlay != null ? ", " + t.CannotPlay : "")}");
            foreach (int ni in t.Notes.Take(args.Contains("--all") ? 100000 : 6))
            {
                var n = tr.Notes[ni];
                Console.WriteLine($"     {n.StartSeconds,8:0.000}-{n.End / NesSession.CpuHz:0.000} s {(n.Struck ? "struck" : n.BeforeStart ? "before" : "became")} end {n.EndedBy}; periods {string.Join(" ", n.Periods.Take(8).Select(v => v.V.ToString("X3")))}{(n.Periods.Count > 8 ? $" (+{n.Periods.Count - 8})" : "")}; vols {string.Join(" ", n.Vols.Take(12).Select(v => v.V))}{(n.Vols.Count > 12 ? $" (+{n.Vols.Count - 12})" : "")}; duties {string.Join(" ", n.Duties.Select(v => v.V))}; env {n.EnvelopeSteps} sweep {n.SweepSteps}");
            }
        }
        return 0;
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
