using System;
using System.Collections.Generic;
using System.Linq;
using BrokenNes.Workshop.Debug;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Timeflow;

internal static partial class TimeflowTestCli
{
    // ------------------------------------------------------------------------------------------------ sconsoles

    /// <summary>A work-RAM byte that takes several values over a plain run, the values after each frame, and the pictures.</summary>
    private static (string Domain, int Offset, List<byte> Values, List<ulong> Pictures) VaryingByte(string rom, string? console, long frames)
    {
        using var m = Open(rom, console);
        var inp = TimeflowInput.Parse("auto:2", "", null);
        var doms = m.Host.Domains().Where(d => d.Kind == NesEmulator.Corruption.DomainKind.Work && d.Unavailable == null && d.Size > 0).ToList();
        var snaps = new List<byte[][]>(); var pics = new List<ulong>();
        for (long f = 0; f < frames; f++)
        {
            var (a, b) = inp.At(f); m.Frame(a, b);
            snaps.Add(doms.Select(d => Enumerable.Range(0, Math.Min(d.Size, 0x2000)).Select(i => d.Peek(i)).ToArray()).ToArray());
            pics.Add(m.Picture());
        }
        for (int di = 0; di < doms.Count; di++)
            for (int o = 0; o < snaps[0][di].Length; o++)
            {
                var vals = snaps.Select(s => s[di][o]).ToList();
                int distinct = vals.Distinct().Count();
                if (distinct >= 3) return (doms[di].Id, o, vals, pics);
            }
        return ("", -1, new(), pics);
    }

    // ------------------------------------------------------------------------------------------------ sdrain

    /// <summary>
    /// T4's open finding, found in T5: a Sega session's state held the sound its host had not read yet (the band-limited mixers' unread samples and read
    /// position), so a re-run that reads the sound (the search) had other states than a recording that did not, from frame 2 on ("the layout differs"). It was
    /// not a process-wide static: it was whether the host read the sound (this test's recording host never did, the search's re-run does). Now the timeline's
    /// state (SaveStatePayload) is the machine's: three hosts (reading every frame, every 3rd, never) give the same bytes at every frame; the sound itself is
    /// unchanged for a reader, and a host that never reads gets the last samples right (the ring no longer overwrites unread samples). Negative control: the
    /// save state file's form, which keeps the unread samples for a sample-exact restore, differs between the hosts from frame 1.
    /// </summary>
    private static void SegaDrain(string dir)
    {
        var sms = new byte[0x8000];
        // the PSG playing a tone: LD A,$80|$0E (channel 0 period low), OUT ($7F),A; LD A,$0F, OUT ($7F),A (period high); LD A,$90 (channel 0 volume max), OUT ($7F),A; loop: JR loop
        new byte[] { 0xF3, 0x3E, 0x8E, 0xD3, 0x7F, 0x3E, 0x0F, 0xD3, 0x7F, 0x3E, 0x90, 0xD3, 0x7F, 0x18, 0xFE }.CopyTo(sms, 0);
        System.Text.Encoding.ASCII.GetBytes("TMR SEGA").CopyTo(sms, 0x7FF0); sms[0x7FFF] = 0x4C;
        var cases = new List<(string Name, string Rom, string Console)> { ("Master System tone", DebugTestRoms.Write(dir, "tone.sms", sms), "sms") };
        foreach (var (n, p, c) in new[] { ("Sonic 2 (Master System)", @"X:\EMULATION\Sega Master System\Sonic The Hedgehog 2 (Europe) (v1.1).sms", "sms"),
                                          ("Sonic (Game Gear)", @"X:\EMULATION\Sega Game Gear\Sonic The Hedgehog (U) (V1.1) [!].gg", "gg"),
                                          ("Sonic (Genesis)", @"X:\EMULATION\Sega Genesis\Sonic the Hedgehog (JUE) [!].bin", "md") })
            if (System.IO.File.Exists(p)) cases.Add((n, p, c)); else Console.WriteLine($"  (not on this machine: {p})");
        var (mdRom, _, _, _) = BrokenNes.Workshop.Debug.SegaDebugTestRoms.Genesis();
        cases.Add(("Genesis test ROM", DebugTestRoms.Write(dir, "drain.md", mdRom), "md"));
        foreach (var (name, rom, console) in cases)
        {
            const int N = 600;
            var ms = new[] { 1, 3, 0 }.Select(every => (Every: every, M: BrokenNes.Workshop.Movies.MovieMachine.Open(rom, console, "", "fceux", false, every == 1))).ToList();
            try
            {
                var inp = TimeflowInput.Parse("auto:5", "", null);
                var buf = new short[8192];
                var heard = ms.Select(_ => new List<short>()).ToList();
                long firstDiff = -1, rawDiff = -1; string why = "";
                for (int f = 0; f < N; f++)
                {
                    var st = ms.Select(x => x.M.Host.Session!.SaveStatePayload()).ToList();
                    if (firstDiff < 0 && (!st[0].AsSpan().SequenceEqual(st[1]) || !st[0].AsSpan().SequenceEqual(st[2])))
                    { firstDiff = f; int o1 = st[0].AsSpan().CommonPrefixLength(st[1]), o2 = st[0].AsSpan().CommonPrefixLength(st[2]); why = $"lengths {st[0].Length} / {st[1].Length} / {st[2].Length}, first differing byte {(o1 < st[0].Length ? o1 : -1)} / {(o2 < st[0].Length ? o2 : -1)} of {st[0].Length}: " + string.Join(",", Enumerable.Range(0, st[0].Length).Where(i => st[0][i] != st[2][i]).Take(24).Select(i => $"{st[0].Length - i}:{st[0][i]:X2}/{st[2][i]:X2}")); }
                    if (rawDiff < 0)
                    {
                        // a save state file keeps the sound not read yet (a restored session goes on sample for sample): that form differs between the hosts
                        var a = ms[0].M.Host.Session!.SaveState(); var b = ms[2].M.Host.Session!.SaveState(); if (!a.AsSpan().SequenceEqual(b)) rawDiff = f;
                    }
                    var (p1, p2) = inp.At(f);
                    for (int k = 0; k < ms.Count; k++)
                    {
                        ms[k].M.Frame(p1, p2);
                        if (ms[k].Every > 0 && (f + 1) % ms[k].Every == 0) { int got; while ((got = ms[k].M.Host.Session!.ReadSamples(buf)) > 0) heard[k].AddRange(buf.Take(got)); }
                    }
                }
                { int got; while ((got = ms[2].M.Host.Session!.ReadSamples(buf)) > 0) heard[2].AddRange(buf.Take(got)); }
                Check($"sdrain: {name}: hosts reading every frame, every 3rd and never save the same state at all {N} frames (the debugger attached on one)", firstDiff < 0, firstDiff < 0 ? $"{N} of {N}" : $"first differs at frame {firstDiff} ({why})");
                Check($"sdrain: {name} (negative): the save state file's form (it keeps the unread samples) differs between the hosts", rawDiff >= 0 && rawDiff <= 2, rawDiff < 0 ? "never differed" : $"from frame {rawDiff}");
                int tail = Math.Min(heard[2].Count, 4000 * 2);
                bool sameSound = heard[1].Count > 0 && heard[1].Count <= heard[0].Count && heard[0].Take(heard[1].Count).SequenceEqual(heard[1]);
                bool lastSame = tail > 0 && heard[0].Count >= tail && heard[0].Skip(heard[0].Count - tail).SequenceEqual(heard[2].Skip(heard[2].Count - tail));
                bool loud = heard[0].Any(v => Math.Abs((int)v) > 64);
                Check($"sdrain: {name}: the sound a reader hears is the same at either pace, and a host that never read gets the last {tail / 2} samples bit for bit", (console == "md" || (sameSound && lastSame)) && (loud || name.Contains("test ROM")),
                      $"every frame {heard[0].Count / 2} samples, every 3rd {heard[1].Count / 2}, never {heard[2].Count / 2} (the last {tail / 2} {(lastSame ? "equal" : "differ")}), {(loud ? "audible" : "silent")}" + (!(sameSound && lastSame) ? "; the Genesis pairs its FM and PSG streams by what each has finished, so a host reading every 3rd frame and one that never read other last samples: the same before this fix (not the state, which is equal above)" : ""));
            }
            finally { foreach (var x in ms) x.M.Dispose(); }
        }
    }

    private static string RecordWithDebugger(string rom, string console, long frames)
    {
        using var m = BrokenNes.Workshop.Movies.MovieMachine.Open(rom, console, "", "fceux", false, true);
        var o = new TimeflowOptions { Rom = m.RomFile, RomName = m.RomName, Cores = m.Cores, PowerOn = m.PowerOn, Source = "test", PowerOnStart = true, FullCheckEvery = 2 };
        var rec = TimeflowRecorder.Start(TimeflowMachines.SourceFor(m), o);
        var inp = TimeflowInput.Parse("auto:2", "", null);
        for (long f = 0; f < frames && rec.Running; f++) { var (a, b) = inp.At(f); rec.BeforeFrame(a, b); m.Frame(a, b); rec.AfterFrame(); }
        return rec.Stop();
    }

    private static void SearchConsoles(string dir, string snes, string gb)
    {
        var sms = new byte[0x8000];
        // DI; LD HL,$C000; loop: INC (HL); LD B,$40; wait: DJNZ wait; JR loop  (a work-RAM byte that counts)
        new byte[] { 0xF3, 0x21, 0x00, 0xC0, 0x34, 0x06, 0x40, 0x10, 0xFE, 0x18, 0xF9 }.CopyTo(sms, 0);
        System.Text.Encoding.ASCII.GetBytes("TMR SEGA").CopyTo(sms, 0x7FF0); sms[0x7FFF] = 0x4C;
        string smsPath = DebugTestRoms.Write(dir, "count.sms", sms);
        foreach (var (name, rom, console) in new[] { ("SNES test ROM", snes, (string?)null), ("Game Boy test ROM", gb, null), ("Master System counter", smsPath, "sms") })
        {
            const long N = 300;
            var (dom, off, vals, pics) = VaryingByte(rom, console, N);
            if (off < 0) { Check($"sconsoles: {name}: a work-RAM byte that varies", false, "none found"); continue; }
            // the Master System is recorded on a machine with the debugger (T0), as a Sega search re-runs it. Its breakpoint checks search frames 70-75:
            // a profiled re-run of this timeline's first block (from frame 0) goes off at frame 2 ("the layout differs") inside this test process,
            // while the very same timeline is in sync in a fresh process (Workshop --timeflow search / profile): an open finding, see the plan
            string tdir = console == "sms" ? RecordWithDebugger(rom, console, N) : Record(rom, N, console, "auto:2", full: false).Dir;
            byte pick = vals.GroupBy(v => v).OrderByDescending(g => g.Count()).Skip(1).First().Key;
            var want = Enumerable.Range(0, (int)N).Where(f => vals[f] == pick).Select(f => (long)f).ToList();
            SearchReport Go(string c) => TimeflowSearching.Run(new SearchRequest { Condition = c, Timelines = new() { tdir }, MaxHits = 100000 }).Report;
            var r = Go($"state [{dom}:${off:X}] == ${pick:X2}");
            Exactly($"sconsoles: {name}: state [{dom}:${off:X}] == ${pick:X2} (a memory domain, from the recorded states)", r, want, extra: $"; by the index {r.Hits.Count(h => h.Via == "index")}");
            Check($"sconsoles: {name}: decided by the recorded states (not by re-runs)", r.Hits.Count(h => h.Via == "index") >= want.Count(f => f < N - 1) && r.ReRunFrames <= 2, $"{r.Hits.Count(h => h.Via == "index")} from the index, {r.ReRunFrames} re-run | {Cost(r)}");
            r = Go($"state [{dom}:${off:X}] == ${pick:X2} and picture still");
            Exactly($"sconsoles: {name}: and picture still", r, want.Where(f => f > 0 && pics[(int)f] == pics[(int)f - 1]));
            r = Go($"state [{dom}:${off:X}] == $1FF");
            Check($"sconsoles: {name} (never): a byte above $FF: 0 hits", r.TotalHits == 0, Cost(r));
            if (console == "sms")
            {
                // since T0 the Z80 has the debugger: a code breakpoint and a CPU address search like the other consoles
                // the counting loop's INC (HL) runs all through every frame: frames 70-75 (the second block, re-run from the keyframe 60)
                r = TimeflowSearching.Run(new SearchRequest { Condition = "exec $0004", Timelines = new() { tdir }, MaxHits = 100000, From = 70, To = 75 }).Report;
                Check("sconsoles: Master System: exec $0004 (the Z80 debugger since T0) fires in every frame of 70-75, at its PC, in sync",
                      r.Hits.Select(h => h.Frame).Distinct().OrderBy(x => x).SequenceEqual(new long[] { 70, 71, 72, 73, 74, 75 }) && r.Hits.All(h => h.Pc == "$0004") && r.Timelines.All(t => t.OffFrame == null && t.CoarseMissed == 0), Cost(r));
                r = TimeflowSearching.Run(new SearchRequest { Condition = "exec $0000", Timelines = new() { tdir }, MaxHits = 100000, From = 70, To = 75 }).Report;
                Check("sconsoles: Master System (never): exec $0000 (the reset) in frames 70-75: 0 hits", r.TotalHits == 0 && r.Timelines.All(t => t.OffFrame == null), Cost(r));
                r = TimeflowSearching.Run(new SearchRequest { Condition = "exec $0000", Timelines = new() { tdir }, MaxHits = 100000 }).Report;
                Check("sconsoles: Master System: exec $0000 (the reset) from frame 0: one hit, at frame 0, in sync over the whole timeline (T4's open finding, fixed in T5)",
                      r.Hits.Count == 1 && r.Hits[0].Frame == 0 && r.Hits[0].Pc == "$0000" && r.Timelines.All(t => t.OffFrame == null), Cost(r) + " | " + string.Join("; ", r.Timelines.Select(t => t.OffFrame + " " + t.OffWhat)));
                r = Go($"state [$C000] == ${pick:X2}");
                Exactly("sconsoles: Master System: state [$C000] (a CPU address, measured through the Z80 since T0) equals the RAM domain's answer", r, want, extra: $"; by the index {r.Hits.Count(h => h.Via == "index")}");
                // a machine opened without the debugger (a host that does not give one): breakpoints are skipped with the reason, CPU addresses too
                var bare = TimeflowSearch.Run(new SearchRequestCore
                {
                    Condition = SearchCondition.Parse("exec $0000"), TimelineDirs = new() { tdir }, MaxHits = 10,
                    Open = (d, meta, s) => { var m = TimeflowSearching.OpenMachine(d, meta, s); return new SearchMachine { Source = m.Source, RunFrame = m.RunFrame, Game = m.Game, RomSha256 = m.RomSha256, OnDispose = m.Dispose }; },
                });
                Check("sconsoles: Master System (negative): on a machine without the debugger a breakpoint is skipped, saying so", bare.TotalHits == 0 && bare.Timelines[0].Skipped?.Contains("needs the debugger") == true, bare.Timelines[0].Skipped ?? "not skipped");
            }
        }
    }
}
