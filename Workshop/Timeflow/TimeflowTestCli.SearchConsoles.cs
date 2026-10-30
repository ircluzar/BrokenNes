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
            var (tdir, _) = Record(rom, N, console, "auto:2", full: false);
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
                r = Go("exec $0004");
                Check("sconsoles: Master System: a code breakpoint waits for T0 (the timeline skipped, saying so)", r.TotalHits == 0 && r.Timelines[0].Skipped?.Contains("T0") == true, r.Timelines[0].Skipped ?? "not skipped");
                r = Go("state [$C000] == 1");
                string why = r.Timelines[0].Skipped ?? "not skipped";
                Check("sconsoles: Master System: a CPU address needs the debugger (T0): name a memory domain (the timeline skipped, never guessed)", r.TotalHits == 0 && why.Contains("domain") && r.ReRunFrames == 0, why);
            }
        }
    }
}
