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
