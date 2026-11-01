using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes.Workshop.Debug;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Checks;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>
/// T4's tests (docs/projects/brokendev-timeflow-plan.md, "T4 as built"), groups of <c>--timeflow-test</c>: <c>search</c> (every condition kind on
/// the search test game: the planted event found exactly, its near miss not reported, a condition that never holds giving 0 hits; the user's
/// example "enemy alive and not visible"; the expected frames come from a plain run of the game read directly, never from the search code),
/// <c>sindex</c> (the access counts in the index; a timeline without coverage / access counts searched by re-runs to the same hits),
/// <c>sconsoles</c> (the SNES and Game Boy test ROMs; a Master System timeline searched by state / picture / sound, its breakpoints refused
/// until T0).
/// </summary>
internal static partial class TimeflowTestCli
{
    /// <summary>What a plain run (no debugger, no Timeflow) of a game shows after each frame: RAM bytes, the picture, the sound's peak, a channel.</summary>
    private sealed class Truth
    {
        public readonly List<byte[]> Ram = new();
        public readonly List<ulong> Picture = new();
        public readonly List<int> Red = new();
        public readonly List<float> Peak = new();
        public readonly List<bool> Pulse1 = new();
        public uint RedRgb;
    }

    private static Truth PlainTruth(string rom, long frames)
    {
        var t = new Truth();
        using var m = MovieMachine.Open(rom, null, "", "fceux", false, true);   // the debugger present, nothing armed (the channel probe needs it)
        var nes = m.Host.Nes!;
        var pics = new List<uint[]>();
        for (long f = 0; f < frames; f++)
        {
            if (f == 0) DrainNes(nes);
            m.Frame(PadButtons.None, PadButtons.None);
            var ram = new byte[0x800]; for (int i = 0; i < ram.Length; i++) ram[i] = nes.PeekSystemRam(i);
            t.Ram.Add(ram);
            t.Picture.Add(m.Picture());
            var (px, _, _) = DebugPictures.Frame(nes);
            pics.Add(px);
            float peak = 0; foreach (float s in DrainNes(nes)) peak = Math.Max(peak, Math.Abs(s));
            t.Peak.Add(peak);
            t.Pulse1.Add(DebugSound.Channels(m.Host.Machine, nes, out _).First(c => c.Name == "Pulse 1").On);
        }
        // the enemy's colour: the one non-black colour of a frame where it is alive and drawn
        var counts = new Dictionary<uint, int>();
        foreach (var px in pics) foreach (uint p in px) { uint c = p & 0xFFFFFF; counts[c] = counts.GetValueOrDefault(c) + 1; }
        var black = counts.OrderByDescending(kv => kv.Value).First().Key;
        t.RedRgb = counts.Where(kv => kv.Key != black).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        foreach (var px in pics) t.Red.Add(px.Count(p => (p & 0xFFFFFF) == t.RedRgb));
        return t;
    }

    private static float[] DrainNes(NesEmulator.NES nes)
    {
        if (nes.DebugBus?.ActiveAPU is NesEmulator.APU_FIX fix) { int q = fix.GetQueuedSampleCount(); return q > 0 ? fix.GetAudioSamples(q) : Array.Empty<float>(); }
        return Array.Empty<float>();
    }

    /// <summary>Records the search test game with the full index (coverage, access counts, checks) or without it.</summary>
    private static string RecordEnemy(string rom, string mlb, long frames, bool index)
    {
        using var m = MovieMachine.Open(rom, null, "", "fceux", false, true, new[] { mlb });
        var o = new TimeflowOptions { Rom = m.RomFile, RomName = m.RomName, Cores = m.Cores, PowerOn = m.PowerOn, Source = "test", PowerOnStart = true, FullCheckEvery = 2 };
        FrameCoverage? cov = null; FrameAccesses? acc = null;
        if (index)
        {
            var d = m.Host.Dbg;
            cov = FrameCoverage.Attach(d); o.Coverage = cov;
            acc = FrameAccesses.Attach(d); o.Accesses = acc;
            o.Checks = CheckRun.Attach(d, new CheckOptions { Profile = false });
            o.VarNames = new() { "alive=[enemy_alive]" };
            o.Vars = () => new[] { d.Evaluate("[enemy_alive]") };
        }
        var rec = TimeflowRecorder.Start(TimeflowMachines.SourceFor(m), o);
        for (long f = 0; f < frames && rec.Running; f++) { rec.BeforeFrame(PadButtons.None, PadButtons.None); m.Frame(PadButtons.None, PadButtons.None); rec.AfterFrame(); }
        string dir = rec.Stop();
        cov?.Detach(); acc?.Detach();
        return dir;
    }

    private static SearchReport Find(string cond, string tdir, string mlb, bool coarse = false, long? from = null, long? to = null) =>
        TimeflowSearching.Run(new SearchRequest { Condition = cond, Timelines = new() { tdir }, Symbols = new() { mlb }, MaxHits = 100000, CoarseOnly = coarse, From = from, To = to }).Report;

    private static string Show(IEnumerable<long> fs) { var l = fs.ToList(); return l.Count == 0 ? "none" : $"{l.Count}: " + string.Join(",", l.Take(10)) + (l.Count > 10 ? ",..." : ""); }

    private static string Cost(SearchReport r)
    {
        var c = r.Clauses.Select(x => $"'{x.Text}' no {x.No} maybe {x.Maybe} yes {x.Yes}, false candidates {x.MaybeChecked - x.MaybeHeld} of {x.MaybeChecked}{(x.Contradicted > 0 ? $", CONTRADICTED {x.Contradicted}" : "")}");
        return $"coarse {r.CoarseFrames} frames -> {r.Candidates} candidates, {r.ReRunFrames} re-run, missed {r.Timelines.Sum(t => t.CoarseMissed)}; " + string.Join("; ", c) +
               (r.Timelines.Any(t => t.OffFrame != null) ? $"; OFF at {r.Timelines.First(t => t.OffFrame != null).OffFrame}: {r.Timelines.First(t => t.OffFrame != null).OffWhat}" : "") +
               (r.Timelines.Any(t => t.Skipped != null) ? $"; skipped: {r.Timelines.First(t => t.Skipped != null).Skipped}" : "");
    }

    /// <summary>A search's hit frames equal the expected ones exactly, with no coarse error, no contradiction, no desync.</summary>
    private static void Exactly(string name, SearchReport r, IEnumerable<long> expected, Func<SearchHit, bool>? eachHit = null, string extra = "")
    {
        var want = expected.ToHashSet();
        var got = r.Hits.Select(h => h.Frame).ToHashSet();
        // a frame-level condition hits a frame once (a breakpoint may fire more than once in a frame)
        bool once = r.Clauses.Any(c => c.Family is "code" or "memory") || r.Hits.Count == got.Count;
        bool ok = got.SetEquals(want) && once && r.Timelines.All(t => t.CoarseMissed == 0 && t.OffFrame == null && t.Skipped == null) && r.Clauses.All(c => c.Contradicted == 0) && (eachHit == null || r.Hits.All(eachHit));
        Check(name, ok, $"hits {Show(got.OrderBy(x => x))}; expected {Show(want.OrderBy(x => x))}" + (got.SetEquals(want) ? "" : $"; missing {Show(want.Except(got).OrderBy(x => x))}, extra {Show(got.Except(want).OrderBy(x => x))}") + $" | {Cost(r)}{extra}");
    }

    // ------------------------------------------------------------------------------------------------ search: every condition kind

    private static void SearchKinds(string dir)
    {
        var (rom, asm, mlb) = TimeflowSearchRoms.EnemyGame();
        string romPath = DebugTestRoms.Write(dir, "enemy.nes", rom), mlbPath = DebugTestRoms.Write(dir, "enemy.mlb", mlb);
        const long N = 600;
        var t = PlainTruth(romPath, N);
        string tdir = RecordEnemy(romPath, mlbPath, N, index: true);
        using (var rd = new TimelineReader(tdir))
            Check("search: the recording is the plain run frame for frame (picture and RAM hashes)", Enumerable.Range(0, (int)N).All(f => rd.Row(f).Picture == t.Picture[f]), $"{N} frames, the index has coverage {rd.Meta.Coverage}, accesses {rd.Meta.Accesses}, checks {rd.Meta.Checks}");
        var frames = Enumerable.Range(0, (int)N).Select(i => (long)i).ToList();
        byte R(long f, int a) => t.Ram[(int)f][a];
        bool Alive(long f) => R(f, TimeflowSearchRoms.Alive) == 1;
        bool Changed(long f, int a) => f > 0 && R(f, a) != R(f - 1, a);
        // ground truth from the game's own counters (read from the plain run's RAM, not through the search)
        var hidden = frames.Where(f => Changed(f, TimeflowSearchRoms.HideCount)).ToList();                  // hide_enemy ran (it counts)
        var planted = hidden;                                                                                   // bug_mark $5A, secret read: in hide_enemy
        var dead = frames.Where(f => !Alive(f)).ToList();
        var ct = frames.ToDictionary(f => f, f => (int)R(f, TimeflowSearchRoms.FrameCt));
        var nearMiss = frames.Where(f => !Alive(f) && (ct[f] & 0x3F) is >= 0x20 and < 0x28 && f > 0 && ct[f] != ct[f - 1]).ToList();
        // frame 0 is the reset: its clear loop writes every byte of pages 4 and 5 and, STA abs,X, reads each first (the 6502's dummy read)
        var sameWrites = frames.Where(f => f > 0 && ct[f] != ct[f - 1] && (ct[f] & 15) == 0).Prepend(0L).ToList();
        var plantedReads = planted.Prepend(0L).ToList();
        Check("search: the test game plays as planned (the plain run)", planted.Count >= 16 && nearMiss.Count >= 8 && sameWrites.Count >= 30 && t.RedRgb != 0 && R(N - 1, TimeflowSearchRoms.SameByte) == 7,
              $"hide_enemy ran in {Show(planted)}; the near miss (dead, same window) {Show(nearMiss)}; same_byte written in {sameWrites.Count} frames; the enemy colour #{t.RedRgb:X6}");
        uint hideAt = asm["hide_enemy"];

        // ---- code breakpoints
        var r = Find("exec hide_enemy", tdir, mlbPath);
        Exactly("search: code: exec hide_enemy finds exactly the frames it ran, at its first instruction", r, planted,
                h => h.Pc == $"${hideAt:X4}" && h.Instruction == "LDA #$F0" && h.In == "hide_enemy" && h.Cycle > 0 && h.Stack.Count > 0 && h.Stack[0].StartsWith("hide_enemy <- update"));
        Check("search: code: the coverage made the candidates exactly those frames (no false candidate)", r.Candidates == planted.Count && r.Clauses[0].Coarse.Contains("coverage"), $"{r.Candidates} candidates; {r.Clauses[0].Coarse}");
        r = Find("exec hide_enemy_dead", tdir, mlbPath);
        Check("search: code (near miss): the neighbouring routine's frames are the dead ones, none of the planted", r.Hits.Select(h => h.Frame).All(f => !Alive(f)) && !r.Hits.Any(h => planted.Contains(h.Frame)) && r.TotalHits > 0, $"{r.TotalHits} hits, all dead frames");
        r = Find("exec never_called", tdir, mlbPath);
        Check("search: code (never): exec never_called: 0 hits, 0 candidates, nothing re-run", r.TotalHits == 0 && r.Candidates == 0 && r.ReRunFrames == 0, Cost(r));
        r = Find("exec hide_enemy if [enemy_alive] == 0", tdir, mlbPath);
        Check("search: code (a condition that never holds): exec hide_enemy if [enemy_alive] == 0: 0 hits (its candidates re-run and rejected)", r.TotalHits == 0 && r.Candidates == planted.Count && r.Clauses[0].MaybeChecked == planted.Count && r.Clauses[0].MaybeHeld == 0, Cost(r));

        // ---- memory breakpoints
        r = Find("write bug_mark if value == $5A", tdir, mlbPath);
        Exactly("search: memory: write bug_mark if value == $5A finds the planted writes", r, planted, h => h.Kind == "write" && h.Value == "$5A" && h.Address!.StartsWith("$0420") && h.Instruction == "STA bug_mark");
        Check("search: memory (near miss): the $A5 writes to the same address were candidates (its page was written) and were rejected", r.Candidates == planted.Count + nearMiss.Count + 1 && r.Clauses[0].MaybeChecked - r.Clauses[0].MaybeHeld == nearMiss.Count + 1,
              $"{r.Candidates} candidates = {planted.Count} planted + {nearMiss.Count} near misses + the reset's frame 0; false candidates {r.Clauses[0].MaybeChecked - r.Clauses[0].MaybeHeld}");
        r = Find("write same_byte", tdir, mlbPath);
        Exactly("search: memory: write same_byte finds writes that never change the byte (the access counts, not the changed bytes)", r, sameWrites, h => h.Value == (h.Frame == 0 ? "$00" : "$07"));
        r = Find("read secret", tdir, mlbPath);
        Exactly("search: memory: read secret finds the planted reads (and the reset's dummy read in frame 0)", r, plantedReads, h => h.Kind == "read" && (h.Instruction == "LDA secret" || h.Frame == 0));
        r = Find("write never", tdir, mlbPath);
        Check("search: memory (never): write never: 0 hits, 0 candidates", r.TotalHits == 0 && r.Candidates == 0, Cost(r));
        r = Find("write $0400-$04FF if value == $99", tdir, mlbPath);
        Check("search: memory (never): a value never written: 0 hits", r.TotalHits == 0, Cost(r));

        // ---- game state
        var stateWant = frames.Where(f => Alive(f) && R(f, TimeflowSearchRoms.EnemyY) == 0xF0).ToList();
        r = Find("state [enemy_alive] == 1 && [enemy_y] == $F0", tdir, mlbPath);
        Exactly("search: state: alive with the sprite off screen, exact from the recorded states", r, stateWant);
        Check("search: state: decided by the index alone but the last frame (no re-run but it)", r.Hits.Count(h => h.Via == "index") >= stateWant.Count - 1 && r.ReRunFrames <= 2, $"{r.Hits.Count(h => h.Via == "index")} from the index, {r.ReRunFrames} frame(s) re-run");
        r = Find("state alive == 1 && [enemy_y] == 100", tdir, mlbPath);
        Exactly("search: state (near miss and a watched variable): alive and drawn: none of the planted frames", r, frames.Where(f => Alive(f) && R(f, TimeflowSearchRoms.EnemyY) == 100));
        r = Find("state [enemy_alive] == 7", tdir, mlbPath);
        Check("search: state (never): [enemy_alive] == 7: 0 hits", r.TotalHits == 0 && r.Candidates <= 1, Cost(r));
        Check("search: state: a register is refused (a state is memory at a frame's end)", Throws(() => Find("state A == 3", tdir, mlbPath), out string why), why);

        // ---- findings
        long firstUninit = frames.First(f => ct[f] == 0x50);
        r = Find("finding ram-uninit", tdir, mlbPath);
        Exactly("search: finding: the uninitialised read is found in its frame (first seen once)", r, new[] { firstUninit }, h => h.Via == "index" && h.Context.Any(c => c.Contains("$0300")));
        r = Find("finding stack-overflow", tdir, mlbPath);
        Check("search: finding (never): stack-overflow: 0 hits", r.TotalHits == 0, Cost(r));

        // ---- picture
        var changedWant = frames.Where(f => f > 0 && t.Picture[(int)f] != t.Picture[(int)f - 1]).ToList();
        r = Find("picture changed", tdir, mlbPath);
        Exactly("search: picture: picture changed, exact from the index (no re-run)", r, changedWant, h => h.Via == "index");
        string red = $"#{t.RedRgb:X6}";
        var redWant = frames.Where(f => t.Red[(int)f] > 0).ToList();
        r = Find($"color {red}", tdir, mlbPath);
        Exactly($"search: picture: color {red} (the enemy drawn), re-rendered once per distinct picture", r, redWant, extra: $"; distinct pictures {t.Picture.Distinct().Count()}");
        r = Find($"color {red} in 0 0 100 240", tdir, mlbPath);
        Check("search: picture (near miss): the same colour in a region left of the enemy: 0 hits", r.TotalHits == 0 && r.Clauses[0].MaybeChecked > 0, Cost(r));
        r = Find("color #123456", tdir, mlbPath);
        Check("search: picture (never): a colour never on screen: 0 hits", r.TotalHits == 0, Cost(r));

        // ---- the user's example: enemy alive and not visible (game state + picture)
        var userWant = frames.Where(f => Alive(f) && t.Red[(int)f] == 0).ToList();
        int nearAliveVisible = frames.Count(f => Alive(f) && t.Red[(int)f] > 0), nearDeadInvisible = frames.Count(f => !Alive(f) && t.Red[(int)f] == 0);
        r = Find($"state [enemy_alive] != 0 and not color {red}", tdir, mlbPath);
        Exactly("search: the user's example, enemy alive and not visible (state + picture): exactly the planted frames", r, userWant,
                extra: $"; near misses not reported: {nearAliveVisible} alive and visible, {nearDeadInvisible} dead and not visible");
        // the picture lags the state by two frames (the sprite's Y reaches OAM at the next vblank's DMA, then is drawn): alive-and-hidden also holds in the
        // two frames after hide_enemy stops and the two after the enemy comes alive again; the planted frames are all in it
        bool Lag(long f) => f < 4 || planted.Contains(f - 1) || planted.Contains(f - 2) || Alive(f) && (!Alive(f - 1) || !Alive(f - 2));
        Check("search: the user's example: every planted frame from its window's third on is a hit (the sprite still shows for two); the others are the picture's two-frame lag",
              planted.Where(f => planted.Contains(f - 2)).All(userWant.Contains) && userWant.All(f => planted.Contains(f) || Lag(f)),
              $"{userWant.Count} hit frames: {planted.Count} planted, {userWant.Count(f => !planted.Contains(f))} lag frames ({Show(userWant.Where(f => !planted.Contains(f)))})");

        // ---- sound
        var loudWant = frames.Where(f => t.Peak[(int)f] > 0.05f).ToList();
        r = Find("sound above 0.05", tdir, mlbPath);
        Exactly("search: sound: sound above 0.05 (pulse 1 playing)", r, loudWant, extra: $"; peaks in those frames {loudWant.Select(f => t.Peak[(int)f]).DefaultIfEmpty().Min():0.000}-{loudWant.Select(f => t.Peak[(int)f]).DefaultIfEmpty().Max():0.000}, else at most {frames.Except(loudWant).Select(f => t.Peak[(int)f]).DefaultIfEmpty().Max():0.000}");
        var pulseWant = frames.Where(f => t.Pulse1[(int)f]).ToList();
        r = Find("channel \"Pulse 1\" playing", tdir, mlbPath);
        Exactly("search: sound: channel \"Pulse 1\" playing (the channel monitor at the frame's end)", r, pulseWant);
        r = Find("sound above 0.99", tdir, mlbPath);
        Check("search: sound (never): a peak above 0.99: 0 hits", r.TotalHits == 0, Cost(r));
        Check("search: sound: an unknown channel is refused, naming the channels", Throws(() => Find("channel Pulse 9 playing", tdir, mlbPath), out string w2) && w2.Contains("Pulse 1"), w2);

        // ---- combinations: a breakpoint filtered by a state, by a negated breakpoint
        r = Find("write bug_mark and state [enemy_alive] == 0", tdir, mlbPath);
        Exactly("search: combined: write bug_mark while dead is the near miss only (and the reset's clear in frame 0, dead too)", r, nearMiss.Prepend(0L), h => h.Value == (h.Frame == 0 ? "$00" : "$A5"));
        r = Find("exec update and not exec hide_enemy and state [enemy_alive] == 1 and not color " + red, tdir, mlbPath);
        Check("search: combined: alive and not drawn but hide_enemy did not run (the picture's lag frames only)", r.Hits.All(h => !planted.Contains(h.Frame)) && r.Hits.Select(h => h.Frame).ToHashSet().SetEquals(userWant.Except(planted)), $"{Show(r.Hits.Select(h => h.Frame).Distinct())} | {Cost(r)}");
        Check("search: an unknown clause is refused with the language", Throws(() => Find("enemy alive", tdir, mlbPath), out string w3) && w3.Contains("exec"), w3);

        // ---- the hit limit and coarse only
        r = TimeflowSearching.Run(new SearchRequest { Condition = "exec update", Timelines = new() { tdir }, Symbols = new() { mlbPath }, MaxHits = 5 }).Report;
        Check("search: the hit limit stops the exact phase and says how many candidates were not checked", r.Hits.Count == 5 && r.HitLimitReached && r.CandidatesNotChecked > 0 && r.FirstHitMs > 0, $"{r.Hits.Count} hits, {r.CandidatesNotChecked} candidates not checked, first hit after {r.FirstHitMs:0} ms");
        r = Find("exec hide_enemy", tdir, mlbPath, coarse: true);
        Check("search: coarse only: the candidates, nothing re-run", r.TotalHits == 0 && r.Candidates == planted.Count && r.ReRunFrames == 0 && r.Timelines[0].Notes.Any(n => n.Contains("not checked")), Cost(r));

        // ---- a timeline without coverage / access counts: every frame a candidate, the same hits by re-runs
        string bare = RecordEnemy(romPath, mlbPath, N, index: false);
        r = Find("exec hide_enemy", bare, mlbPath);
        Exactly("search: no coverage in the index: every frame re-run, the same hits", r, planted, extra: r.Timelines[0].Notes.Any(n => n.Contains("without coverage")) ? "; said: recorded without coverage" : "; NOT SAID");
        r = Find("write same_byte", bare, mlbPath);
        Exactly("search: no access counts in the index: the same writes by re-runs", r, sameWrites);
        Check("search: no checks in the index: a finding search skips the timeline, saying why", Find("finding ram-uninit", bare, mlbPath).Timelines[0].Skipped?.Contains("without the hardware checks") == true, "skipped with the reason");
        // ---- a range of frames
        long mid = planted[planted.Count / 2];
        r = Find("exec hide_enemy", tdir, mlbPath, from: mid, to: mid + 3);
        Exactly("search: from / to: only the frames asked", r, planted.Where(f => f >= mid && f <= mid + 3));
    }
}
