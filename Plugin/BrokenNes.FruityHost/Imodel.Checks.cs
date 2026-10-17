// Round 6 (imodel): the check list, the mutant runs, and the two ways to run them: `FruityHost imodel-test [--only id,id] [--no-mutants]` (no plugin DLL needed) and the `imodel` check of `selftest`.
using System.Diagnostics;
using BrokenNes2;
using BrokenNes2.Instruments;

namespace BrokenNes.FruityHost;

internal static partial class ImodelChecks
{
    public sealed record Check(string Id, string What, Action<ImodelReport> Run);

    public static readonly Check[] All =
    [
        new("schema", "every kind's schema is coherent: unique ids, ranges that fit their bit widths, a one-line explanation, a reason wherever a field is marked as not audible", Schema),
        new("registers", "reading: made-up snapshots encoded by independent code (hand-written register values) give the expected value in every field of every kind", Registers),
        new("roundtrip", "read -> write unchanged -> identical bytes, for every instrument of the made-up snapshots", r => RoundTrip(r, ImodelFixtures.Synthetic())),
        new("roundtrip-real", "the same on real games from the library (SNES, Genesis, Game Boy, Game Boy Color, Game Gear, NES)", r => RealOnly(r, f => RoundTrip(r, f))),
        new("edit-readback", "every field set to several values, one at a time, written and read back equal, nothing else moved; every blob inverted and read back", r => EditReadback(r, ImodelFixtures.Synthetic(), true)),
        new("edit-readback-real", "the same on real games", r => RealOnly(r, f => EditReadback(r, f, false))),
        new("boundaries", "empty and oversized blobs, no instruments, out-of-range values, wrong console or kind, ROM instruments, strength 0 and above 1, a full history", Boundaries),
        new("prng", "the PRNG equals an independent xoshiro256** / splitmix64 reference, and is uniform", Prng_),
        new("determinism", "the same seeds give the same corruption, different seeds give different ones, and fixed scripts give recorded bytes", Determinism),
        new("locks", "locked fields and blobs never change under the random tools, a fully locked instrument says so, unlocking restores the tools", Locks),
        new("history", "undo, redo, go-to and a replay from the clean instrument agree at every step; the saved history reads back", History),
        new("restore", "the sound memory depends on the instrument's state, not on the path to it: ten operations per instrument written one after the other equal the direct write, and undoing all of them gives back the original bytes (made-up snapshots and real games)", r => { Restore(r, ImodelFixtures.Synthetic(), 99); RealOnly(r, f => Restore(r, f, 6)); }),
        new("codec", "an instrument, a history and an overlay read back equal; every truncation and every flipped bit is refused with a reason; a newer version, an unknown kind and a value outside its range are refused", Codec),
        new("structure", "keeping the structure leaves BRR well-formed with the loop pointer inside the sample; without it the strength decides how often it breaks; a broken sample still plays", Structure),
        new("audible", "rendered PCM through the existing samplers: a field marked audible changes the sound, a field marked as having no effect leaves the PCM bit-identical", Audible_),
        new("neutral", "a snapshot without an instrument edit plays bit for bit as before the SNES overlay existed (made-up snapshots and real games)", Neutral),
        new("alloc", "the SNES sampler, with and without an overlay, allocates nothing across notes, updates and blocks", Alloc),
        new("real-corrupt", "all three tools on real instruments: legal, writable, readable back, and mostly audible", RealCorrupt),
    ];

    private static void RealOnly(ImodelReport r, Action<IEnumerable<ImodelFixture>> body)
    {
        var real = ImodelFixtures.Real();
        if (ImodelFixtures.RealMissing.Count > 0) r.Warn("not tested, game not available: " + string.Join("; ", ImodelFixtures.RealMissing));
        if (real.Count == 0) { r.Warn("no real game available: nothing was tested"); return; }
        body(real);
    }

    // ---- mutants: a deliberate bug per verifier, which that verifier must catch ----

    public sealed record Mutant(string Name, int Id, string CatchingCheck, Action<ImodelReport> Run);

    private static Action<ImodelReport> Synth(Action<ImodelReport, IEnumerable<ImodelFixture>> body) => r => body(r, ImodelFixtures.Synthetic());

    public static readonly Mutant[] Mutants =
    [
        new("locks ignored", InstrumentMutants.IgnoreLocks, "locks", Locks),
        new("the seed is ignored (everything uses seed 0)", InstrumentMutants.SeedIgnored, "determinism", Determinism),
        new("a bit flip may leave its field's width", InstrumentMutants.FlipOutOfWidth, "determinism", Determinism),
        new("the seed is not stored with the operation", InstrumentMutants.SeedNotStored, "history", History),
        new("undo leaves a byte of the undone change", InstrumentMutants.UndoLeavesBit, "history", History),
        new("replay drops the last operation", InstrumentMutants.ReplayDropsLast, "history", History),
        new("a wide register field loses its top bit on the way in", InstrumentMutants.RoundTripLosesBit, "edit-readback", Synth((r, f) => EditReadback(r, f, true))),
        new("the loop pointer is not written", InstrumentMutants.WriteSkipsLoop, "edit-readback", Synth((r, f) => EditReadback(r, f, true))),
        new("YM2612 operators 2 and 3 are exchanged on the way in", InstrumentMutants.OpnSwapsOperators, "edit-readback", Synth((r, f) => EditReadback(r, f, true))),
        new("the Game Boy wave RAM's nibbles are swapped on the way in", InstrumentMutants.GbWaveNibbleSwap, "edit-readback", Synth((r, f) => EditReadback(r, f, true))),
        new("an edit that is undone leaves an envelope entry behind", InstrumentMutants.WriteLeavesTrace, "restore", r => Restore(r, ImodelFixtures.Synthetic(), 99)),
        new("keep-structure lets BRR headers break", InstrumentMutants.BrrHeaderBroken, "structure", Structure),
        new("keep-structure lets the loop pointer leave the sample", InstrumentMutants.LoopOutsideSample, "structure", Structure),
        new("a damaged checksum is not noticed", InstrumentMutants.CrcNotChecked, "codec", Codec),
    ];

    /// <summary>Mutants that are not a switch in the library but a change in what the check is fed.</summary>
    public sealed record TestMutant(string Name, string CatchingCheck, Action<ImodelReport> Run, Action Setup, Action Teardown);

    public static readonly TestMutant[] TestMutants =
    [
        new("the sampler ignores the overlay (every edit of an envelope, tuning, volume or flag is inaudible)", "audible",
            r => Probe(r, ImodelFixtures.Synthetic().First(x => x.Name == "snes-samples"), 0, "snes slot 0"), () => StripOverlay = true, () => StripOverlay = false),
        new("a field that is audible is declared not to be (YM2612 operator 1 total level)", "audible",
            r => Probe(r, ImodelFixtures.Synthetic().First(x => x.Name == "opn"), 0, "opn patch 0", (i, f) => f.Id == "op1.tl"), () => ClassifyOverride = (i, f) => f.Id == "op1.tl" ? Audible.No : null, () => ClassifyOverride = null),
        new("a field that is not audible is declared to be (YM2612 pan)", "audible",
            r => Probe(r, ImodelFixtures.Synthetic().First(x => x.Name == "opn"), 0, "opn patch 0", (i, f) => f.Id == "pan.l"), () => ClassifyOverride = (i, f) => f.Id == "pan.l" ? Audible.Yes : null, () => ClassifyOverride = null),
        new("a clean snapshot is played through a stray overlay", "neutral", Neutral, () => ImodelData.LeakOverlay = true, () => ImodelData.LeakOverlay = false),
        new("the sampler allocates a little on every block", "alloc", AllocWithLeak, () => { }, () => { }),
    ];

    private static void AllocWithLeak(ImodelReport r)
    {
        var s = new SnesSampler(ImodelData.SnesWithSamples(), 48000);
        var buf = new float[512];
        s.NoteOn(0, 0, SnesSampler.PitchFor(0), 90, 90); s.Render(buf, 256);
        long before = GC.GetAllocatedBytesForCurrentThread();
        object? keep = null;
        for (int i = 0; i < 100; i++) { s.Render(buf, 256); keep = new byte[24]; }
        long used = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(keep);
        r.Expect(used == 0, $"the SNES sampler allocated {used} bytes in 100 blocks");
    }

    // ---- running ----

    public sealed record CheckOutcome(string Id, string What, bool Passed, double Seconds, ImodelReport Report);

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
            log?.Invoke($"  {(o.Passed ? (r.Warnings.Count > 0 ? "WARN" : "PASS") : "FAIL")}  {c.Id,-20} {r.Assertions,7} assertions  [{o.Seconds:0.0}s]" + (r.Notes.Count > 0 ? "\n        " + string.Join("\n        ", r.Notes) : "") + (r.Warnings.Count > 0 ? "\n        warning: " + string.Join("\n        warning: ", r.Warnings) : "") + (r.Failures.Count > 0 ? "\n        FAILED: " + string.Join("\n        FAILED: ", r.Failures.Take(8)) + (r.Failures.Count > 8 ? $"\n        ... and {r.Failures.Count - 8} more" : "") : ""));
        }
        return list;
    }

    /// <summary>Runs each mutant: sets the bug, runs the check meant to catch it, and requires failures. Returns the lines to print and how many were not caught.</summary>
    public static (List<string> Lines, int Missed) RunMutants(string? only)
    {
        var lines = new List<string>();
        int missed = 0;
        foreach (var m in Mutants)
        {
            if (only != null && !only.Split(',').Contains(m.CatchingCheck)) continue;
            var r = new ImodelReport();
            InstrumentMutants.Active = m.Id;
            try { m.Run(r); }
            catch (Exception e) { r.Failures.Add($"exception {e.GetType().Name}: {e.Message}"); }
            finally { InstrumentMutants.Active = 0; }
            bool caught = r.Failures.Count > 0;
            if (!caught) missed++;
            lines.Add($"  {(caught ? "caught" : "MISSED")}  [{m.CatchingCheck}] {m.Name}: {r.Failures.Count} failing assertions" + (caught ? $", e.g. {Trim(r.Failures[0])}" : ""));
        }
        foreach (var m in TestMutants)
        {
            if (only != null && !only.Split(',').Contains(m.CatchingCheck)) continue;
            var r = new ImodelReport();
            m.Setup();
            try { m.Run(r); }
            catch (Exception e) { r.Failures.Add($"exception {e.GetType().Name}: {e.Message}"); }
            finally { m.Teardown(); }
            bool caught = r.Failures.Count > 0;
            if (!caught) missed++;
            lines.Add($"  {(caught ? "caught" : "MISSED")}  [{m.CatchingCheck}] {m.Name}: {r.Failures.Count} failing assertions" + (caught ? $", e.g. {Trim(r.Failures[0])}" : ""));
        }
        return (lines, missed);
    }

    private static string Trim(string s) => s.Length <= 170 ? s : s[..167] + "...";

    /// <summary>`FruityHost imodel-fields`: every kind's fields as markdown tables (the field tables of docs/projects/round6-imodel.md are made with this).</summary>
    public static int PrintFields()
    {
        var seen = new HashSet<InstrumentKind>();
        foreach (var fx in ImodelFixtures.Synthetic())
            foreach (var inst in Src(fx.Snapshot).List())
            {
                if (!seen.Add(inst.Kind)) continue;
                Console.WriteLine($"### {inst.Kind} ({inst.Console})");
                Console.WriteLine();
                Console.WriteLine("| id | name | kind | range | played by the sampler | what it does |");
                Console.WriteLine("|---|---|---|---|---|---|");
                foreach (var f in inst.Fields)
                    Console.WriteLine($"| `{f.Id}` | {f.Name} | {f.Kind} | {(f.Kind == FieldKind.Enum ? string.Join(" / ", f.Names!) : $"{f.Min}..{f.Max}")} | {(f.Audible == Audible.Yes ? "yes" + (f.AudibleNote != null ? " (" + f.AudibleNote + ")" : "") : "**no**: " + f.AudibleNote)} | {f.Explanation} |");
                foreach (var b in inst.Blobs)
                    Console.WriteLine($"| `{b.Id}` (blob) | {b.Name} | {b.Kind} | {b.MinLength}..{b.MaxLength} bytes | {(b.Audible == Audible.Yes ? "yes" : "**no**: " + b.AudibleNote)} | {b.Explanation} |");
                Console.WriteLine();
            }
        return 0;
    }

    /// <summary>`FruityHost imodel-test [--only id,id] [--no-mutants]`</summary>
    public static int Main(string[] args)
    {
        string? only = null;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--only") only = args[i + 1];
        bool mutants = !args.Contains("--no-mutants");
        Console.WriteLine("BrokenNes.FruityHost imodel-test (the instrument model, corruptor and writers; no plugin DLL involved)");
        var results = RunChecks(only, Console.WriteLine);
        int failed = results.Count(o => !o.Passed);
        int missed = 0;
        if (mutants)
        {
            Console.WriteLine("mutants (each must be caught by the check named):");
            var (lines, m) = RunMutants(only);
            foreach (var l in lines) Console.WriteLine(l);
            missed = m;
        }
        Console.WriteLine($"{results.Count - failed} checks passed, {failed} failed, {results.Sum(o => o.Report.Assertions)} assertions; {missed} mutants missed");
        return failed == 0 && missed == 0 ? 0 : 1;
    }
}

public sealed unsafe partial class SelfTest
{
    /// <summary>The instrument model's checks as one check of the suite (they need no plugin, but belong to the plugin's certification): all of them, then every mutant must be caught.</summary>
    private void Imodel(T t)
    {
        var results = ImodelChecks.RunChecks(null);
        foreach (var o in results)
        {
            foreach (var f in o.Report.Failures.Take(3)) t.Expect(false, $"[{o.Id}] {f}");
            if (o.Report.Failures.Count > 3) t.Expect(false, $"[{o.Id}] ... and {o.Report.Failures.Count - 3} more");
            foreach (var w in o.Report.Warnings) t.Warn(false, $"[{o.Id}] {w}");
        }
        t.Note($"{results.Count} checks, {results.Sum(o => o.Report.Assertions)} assertions");
        ImodelDll(t);                                  // the same instruments through the real plugin DLL: edits played by its samplers, kept in a project
        var (lines, missed) = ImodelChecks.RunMutants(null);
        t.Expect(missed == 0, $"{missed} mutants were not caught: " + string.Join("; ", lines.Where(l => l.Contains("MISSED"))));
        t.Note($"{lines.Count - missed} of {lines.Count} deliberate bugs caught");
    }
}
