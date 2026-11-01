using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes.Workshop.Debug;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Timeflow;

internal static partial class TimeflowTestCli
{
    // ------------------------------------------------------------------------------------------------ hotpatch (T5)

    /// <summary>A recording on a machine with the debugger, these symbols and (optionally) the search index (coverage, access counts).</summary>
    private static string RecordIndexed(string rom, string? console, long frames, string input, List<string> syms, bool index = true)
    {
        using var m = MovieMachine.Open(rom, console, "", "fceux", false, true, syms);
        var o = new TimeflowOptions { Rom = m.RomFile, RomName = m.RomName, Cores = m.Cores, PowerOn = m.PowerOn, Source = "test", PowerOnStart = true, FullCheckEvery = 2 };
        if (index) { o.Coverage = FrameCoverage.Attach(m.Host.Dbg); o.Accesses = FrameAccesses.Attach(m.Host.Dbg); }
        var rec = TimeflowRecorder.Start(TimeflowMachines.SourceFor(m), o);
        var inp = TimeflowInput.Parse(input, "", null);
        for (long f = 0; f < frames && rec.Running; f++) { var (a, b) = inp.At(f); rec.BeforeFrame(a, b); m.Frame(a, b); rec.AfterFrame(); }
        return rec.Stop();
    }

    private static int TimelineCount(string timelineDir) => TimeflowStore.List(TimeflowStore.GameDirOf(timelineDir)).Count;

    /// <summary>Where each frame of a timeline starts (its PC's symbol on the main CPU), read by jumping a machine there.</summary>
    private static string? PcSymbolAt(string dir, long frame, List<string> syms)
    {
        using var pb = new TimeflowPlayback(dir);
        using var m = TimeflowHotpatching.Open(dir, pb.Meta, null, syms);
        pb.Jump(m.Source, frame);
        return m.Debugger.SymbolAt(m.Machine.Main, m.Machine.Main.PC);
    }

    private static void Hotpatch(string dir)
    {
        string Save(string build)
        {
            var (rom, _, mlb) = TimeflowHotpatchRoms.Game(build);
            string p = DebugTestRoms.Write(dir, $"t5-{build}.nes", rom);
            File.WriteAllText(Path.ChangeExtension(p, ".mlb"), mlb);
            return p;
        }
        string planted = Save("planted"), fixedRom = Save("fixed"), moved = Save("moved");
        var symP = new List<string> { Path.ChangeExtension(planted, ".mlb") };
        var symF = new List<string> { Path.ChangeExtension(fixedRom, ".mlb") };
        var symM = new List<string> { Path.ChangeExtension(moved, ".mlb") };
        const long N = 200;
        string tdir = RecordIndexed(planted, null, N, "auto:1", symP);
        string tid = Path.GetFileName(tdir);

        // ---- find it with search (T4 with the new "by"): the corruption's writes by fill_table, never by check_hp (which only reads hp)
        var found = TimeflowSearching.Run(new SearchRequest { Condition = "write hp by fill_table", Timelines = new() { tdir }, Symbols = symP, MaxHits = 100 }).Report;
        long first = found.Hits.Count > 0 ? found.Hits.Min(h => h.Frame) : -1;
        Check("hotpatch: search finds the planted write: write hp by fill_table (the new 'by' clause), at ft_store", found.TotalHits >= 2 && found.Hits.All(h => h.In == "ft_store" && h.Address!.Contains("hp")) && first > 20,
              $"{found.TotalHits} hit(s), frames {string.Join(",", found.Hits.Select(h => h.Frame))}");
        var nearBy = TimeflowSearching.Run(new SearchRequest { Condition = "write hp by check_hp", Timelines = new() { tdir }, Symbols = symP, MaxHits = 100 }).Report;
        var readBy = TimeflowSearching.Run(new SearchRequest { Condition = "read hp by check_hp", Timelines = new() { tdir }, Symbols = symP, MaxHits = 1000 }).Report;
        Check("hotpatch (near miss): write hp by check_hp: 0 hits, while read hp by check_hp fires (it reads hp every frame)", nearBy.TotalHits == 0 && readBy.TotalHits >= N - 5, $"writes {nearBy.TotalHits}, reads {readBy.TotalHits}");
        Check("hotpatch (negative): by an unknown function is refused with the reason", Throws(() => TimeflowSearching.Run(new SearchRequest { Condition = "write hp by nowhere", Timelines = new() { tdir }, Symbols = symP }), out string byErr) && byErr.Contains("nowhere"), byErr);

        HotpatchReport Go(HotpatchArgs x) { x.Timeline = tdir; return TimeflowHotpatching.Run(x).Report; }
        const string Verdict = "never write hp by fill_table";

        // ---- the byte patch of the CPX operand at frame 10 (idx is still below $10): the verdict passes on the patched future, fails on the old one
        int before = TimelineCount(tdir);
        var r = Go(new HotpatchArgs { Frame = 10, Patches = new() { "ft_limit+1=21>10" }, Symbols = symP, Verdicts = new() { Verdict } });
        Check("hotpatch: the byte patch of the CPX operand (ft_limit+1=21>10) applied at frame 10, at its ROM and file offsets", r.Refused == null && r.Applied.Count == 1 && r.Applied[0].Resolved.Contains("prg") && r.Applied[0].FileOffset == 16 + r.Applied[0].RomOffset,
              r.Refused ?? string.Join("; ", r.Applied.Select(a => $"{a.Text} {a.Resolved} file {a.FileOffset}")));
        var v = r.Verdicts.FirstOrDefault();
        Check($"hotpatch: the verdict '{Verdict}' PASSES on the patched future (frames 10-{N - 1})", v != null && v.Pass && v.Decided, v?.Evidence ?? "no verdict");
        Check("hotpatch (negative): without the patch (the old future, the same frames) the same verdict FAILS, with the write as evidence", v?.Old != null && !v.Old.Pass && v.Old.Decided && v.Old.Evidence.Contains("ft_store"), v?.Old?.Evidence ?? "not evaluated");
        var bm = r.BranchDir != null ? TimelineMeta.Load(r.BranchDir) : null;
        Check("hotpatch: recorded as a branch with its patches (parent, frame, the patched ROM's sha256, the base ROM)", bm != null && bm.ParentId == tid && bm.ParentFrame == 10 && bm.Hotpatch == "bytes" && bm.Patches?.SequenceEqual(new[] { "ft_limit+1=21>10" }) == true
              && bm.RomSha256 == r.PatchedRomSha256 && bm.BaseRomSha256 != null && bm.BaseRomSha256 != bm.RomSha256 && TimelineCount(tdir) == before + 1,
              bm == null ? "no branch" : $"{bm.Id}: parent {bm.ParentId} at {bm.ParentFrame}, {bm.Hotpatch} {string.Join(";", bm.Patches ?? new())}, rom {bm.RomSha256[..12]} from {bm.BaseRomSha256?[..12]}");
        if (bm != null)
        {
            // reproducible: the branch's own stored ROM is the patched one, so a jump into it later proves equal, and its replay stays in sync
            using var pb = new TimeflowPlayback(r.BranchDir!);
            using var m = TimeflowHotpatching.Open(r.BranchDir!, pb.Meta, null, symP);
            bool jumped = !Throws(() => pb.Jump(m.Source, 60), out string jmsg);
            var rr = jumped ? pb.Replay(m.Source, 60, pb.Reader.EndFrame, (a, b) => m.Run(a, b)) : null;
            byte patchedByte = m.Machine.Main.PeekSpace(m.Machine.Main.FindSpace("prg")!.Id, r.Applied[0].RomOffset);
            Check("hotpatch: the branch is reproducible: a jump into it (its stored, patched ROM) proves equal and replays in sync to its end", jumped && rr!.InSync && patchedByte == 0x10, jumped ? $"replay {rr!.FramesRun} frames, in sync {rr.InSync}, ROM byte ${patchedByte:X2}" : jmsg);
        }
        var d = r.Divergence;
        Check("hotpatch: the divergence: the CPUs part at frame 10 at the patched CPX (ft_limit); the RAM first differs when the fixed ring wraps (idx back to 0), before the planted write's frame, idx the variable that differs",
              d?["diverged"]?.GetValue<bool>() == true && d["firstInstruction"]?["frame"]?.GetValue<long>() == 10 && d["firstInstruction"]?["pcSymbolOld"]?.ToString() == "ft_limit"
              && d["frame"]?.GetValue<long>() is long df && df > 10 && df < first && (d["variablesAfter"]?["variables"]?.AsArray().Any(x => x!["variable"]!.ToString() == "idx") ?? false),
              d?["summary"]?.ToString() ?? "no divergence report");

        // ---- the near miss: the same patch at frame 20 is too late (idx is already past $10, so it still runs to $20): the verdict fails, honestly
        var late = Go(new HotpatchArgs { Frame = 20, Patches = new() { "ft_limit+1=21>10" }, Symbols = symP, Verdicts = new() { Verdict }, VerdictOnOld = false });
        Check("hotpatch (near miss): the same patch at frame 20 is too late (idx already past $10 in the restored RAM): the verdict FAILS on the patched future", late.Refused == null && late.Verdicts.Count == 1 && !late.Verdicts[0].Pass && late.Verdicts[0].Decided, late.Verdicts.FirstOrDefault()?.Evidence ?? late.Refused ?? "");

        // ---- refusals: wrong old bytes (nothing applied, no branch), a patch on RAM
        before = TimelineCount(tdir);
        var wrong = Go(new HotpatchArgs { Frame = 10, Patches = new() { "ft_store=95 30>95 31", "ft_limit+1=10>21" }, Symbols = symP, Verdicts = new() { Verdict } });
        Check("hotpatch (negative): a patch whose old bytes do not match is refused, with the bytes there, and nothing is applied (the other patch neither) and no branch made",
              wrong.Refused != null && wrong.Refused.Contains("holds 21, not 10") && wrong.Refused.Contains("nothing applied") && wrong.Applied.Count == 0 && !wrong.Resumed && TimelineCount(tdir) == before, wrong.Refused ?? "not refused");
        var ram = Go(new HotpatchArgs { Frame = 10, Patches = new() { "hp=05>06" }, Symbols = symP });
        Check("hotpatch (negative): a byte patch on RAM is refused (a patch changes the ROM)", ram.Refused?.Contains("not ROM") == true, ram.Refused ?? "not refused");
        Check("hotpatch (negative): a malformed patch is refused before anything runs", Throws(() => Go(new HotpatchArgs { Frame = 10, Patches = new() { "ft_limit+1=21>1000" }, Symbols = symP }), out string mal) && mal.Contains("as many"), mal);

        // ---- a patch that changes nothing (the same bytes): no divergence (the divergence finder's negative control)
        var same = Go(new HotpatchArgs { Frame = 10, To = 80, Patches = new() { "ft_limit+1=21>21" }, Symbols = symP, Branch = false });
        Check("hotpatch (negative): a patch that writes the bytes already there: no divergence, the CPUs ran the same instructions", same.Refused == null && same.Divergence?["diverged"]?.GetValue<bool>() == false, same.Divergence?["summary"]?.ToString() ?? same.Refused ?? "");

        // ---- rebuild-resume: the fixed build (every routine where it was) at frame 10: safe, the verdict passes
        var rb = Go(new HotpatchArgs { Frame = 10, NewRom = fixedRom, Symbols = symP, NewSymbols = symF, Verdicts = new() { Verdict, "query writes hp expect total == 0", "always state [hp] == 5" } });
        Check("hotpatch: rebuild-resume with the fixed build at frame 10: the symbol-map check is safe (the PC and the stack map to the same functions)", rb.Refused == null && rb.Check?.Safe == true && rb.Resumed, rb.Refused ?? rb.Check?.Why ?? "");
        Check("hotpatch: rebuild-resume: every verdict passes on the new build's future (never ..., query writes hp expect total == 0, always state [hp] == 5)", rb.VerdictsPass && rb.Verdicts.Count == 3,
              string.Join(" | ", rb.Verdicts.Select(x => $"{x.Test}: {(x.Pass ? "PASS" : "FAIL")} ({x.Evidence})")));
        Check("hotpatch (negative): ... and each fails on the old future (the planted build)", rb.Verdicts.All(x => x.Old != null && !x.Old.Pass),
              string.Join(" | ", rb.Verdicts.Select(x => $"{x.Test}: {(x.Old?.Pass == true ? "PASS" : "FAIL")} ({x.Old?.Evidence})")));
        var rbm = rb.BranchDir != null ? TimelineMeta.Load(rb.BranchDir) : null;
        Check("hotpatch: the rebuild's branch keeps the new build's ROM (its sha256) and says it is a rebuild", rbm?.Hotpatch == "rebuild" && rbm.RomSha256 == TimeflowStore.Sha256(File.ReadAllBytes(fixedRom)), rbm == null ? "no branch" : $"{rbm.Hotpatch} {rbm.RomSha256[..12]}");

        // ---- the verdict promoted into a project's verify scenarios, by an explicit command only: verify plays it on a build (the fixed one passes, the planted one fails)
        if (rb.Branch != null)
        {
            string man = Path.Combine(dir, "manifests");
            Directory.CreateDirectory(Path.Combine(man, "t5game"));
            File.WriteAllText(Path.Combine(man, "t5game", "project.json"), "{}");
            Check("hotpatch: nothing is promoted by itself (no verdicts folder after the hotpatches)", !Directory.Exists(Path.Combine(man, "t5game", "verdicts")), "");
            var pj = TimeflowVerdictPromote.Promote(rb.Branch, Verdict, man, "t5game", "hp-never-by-fill-table", "the CPX fix keeps fill_table off hp", null, null);
            string pfile = pj["file"]!.ToString();
            Check("hotpatch: verdict-promote writes the test, its frames and the pads from power-on (the parent's before the branch) beside the manifest",
                  File.Exists(pfile) && pj["from"]!.GetValue<long>() == 10 && pj["to"]!.GetValue<long>() == N - 1 && pj["input"]!.ToString().Length > 0, $"{pfile}: frames {pj["from"]}-{pj["to"]}, input {pj["input"]!.ToString().Length} chars");
            int all0 = TimeflowStore.GameDirs().Sum(g => TimeflowStore.List(g).Count);
            var good = TimeflowVerdictPromote.RunPromoted(Path.Combine(man, "t5game"), "nes", fixedRom, symF, "", out bool gok);
            var bad = TimeflowVerdictPromote.RunPromoted(Path.Combine(man, "t5game"), "nes", planted, symP, "", out bool pok);
            Check("hotpatch: verify plays the promoted verdict on the fixed build: PASS", gok && good?.Count == 1 && good[0]!["pass"]!.GetValue<bool>(), good?[0]?["evidence"]?.ToString() ?? "none");
            Check("hotpatch (negative): ... and on the planted build: FAIL, with the write", !pok && bad?[0]?["pass"]?.GetValue<bool>() == false && bad[0]!["evidence"]!.ToString().Contains("ft_store"), bad?[0]?["evidence"]?.ToString() ?? "none");
            Check("hotpatch (negative): promoting the same name twice, or without a why, is refused",
                  Throws(() => TimeflowVerdictPromote.Promote(rb.Branch, Verdict, man, "t5game", "hp-never-by-fill-table", "again", null, null), out string twice) && twice.Contains("already")
                  && Throws(() => TimeflowVerdictPromote.Promote(rb.Branch, Verdict, man, "t5game", "other", "", null, null), out string nowhy) && nowhy.Contains("why"), twice);
            int all1 = TimeflowStore.GameDirs().Sum(g => TimeflowStore.List(g).Count);
            Check("hotpatch: verify's recordings are its own scratch ones (nothing added to the store, the scratch folder gone)", all0 == all1, $"{all0} timelines before, {all1} after");
        }

        // ---- rebuild-resume with a build that moved the code a frame starts in: refused with the reason, the nearest safe point offered, resuming there works
        long inSlow = -1;
        for (long f = 2; f < 40 && inSlow < 0; f++) if (PcSymbolAt(tdir, f, symP)?.StartsWith("slow") == true) inSlow = f;
        Check("hotpatch: the test game has frames that start inside slow (a routine longer than a frame)", inSlow > 0, inSlow > 0 ? $"frame {inSlow}: {PcSymbolAt(tdir, inSlow, symP)}" : "none in frames 2-39");
        if (inSlow > 0)
        {
            var mv = Go(new HotpatchArgs { Frame = inSlow, NewRom = moved, Symbols = symP, NewSymbols = symM, Safe = new() { "main_loop" }, Verdicts = new() { Verdict } });
            Check("hotpatch (negative): rebuild-resume where the frame starts in code the new build moved is refused, naming the PC's function in both builds",
                  mv.Refused != null && !mv.Resumed && mv.Check?.Safe == false && mv.Refused.Contains("slow") && mv.Refused.Contains("in the new build"), mv.Refused ?? "not refused");
            var sp = mv.SafePoint;
            Check("hotpatch: ... and the nearest safe resume point is offered: the next frame start in main_loop (the manifest's safe list), after it",
                  sp != null && sp.Frame > inSlow && sp.Function == "main_loop" && sp.Safe && PcSymbolAt(tdir, sp.Frame, symP)?.StartsWith("main_loop") == true, sp == null ? "none offered" : $"frame {sp.Frame} in {sp.Function}");
            var mv2 = Go(new HotpatchArgs { Frame = inSlow, NewRom = moved, Symbols = symP, NewSymbols = symM, Safe = new() { "main_loop" }, AtSafe = true, Verdicts = new() { Verdict } });
            Check("hotpatch: resuming at the offered safe point (asked for) runs the moved build and its verdict passes", mv2.Resumed && mv2.ResumedAt == sp?.Frame && mv2.VerdictsPass, mv2.Refused ?? $"resumed at {mv2.ResumedAt}: {mv2.VerdictSummary}");
            var fx = Go(new HotpatchArgs { Frame = inSlow, NewRom = fixedRom, Symbols = symP, NewSymbols = symF, Branch = false });
            Check("hotpatch (negative): the same frame with the fixed build (nothing moved) is safe", fx.Refused == null && fx.Check?.Safe == true, fx.Refused ?? "");
            Check("hotpatch (negative): a rebuild without the new build's symbols is refused before anything runs", Throws(() => Go(new HotpatchArgs { Frame = inSlow, NewRom = moved, Symbols = symP }), out string noSym) && noSym.Contains("symbols"), noSym);
        }

        // ---- the verdict on its own (any timeline): the forms, and an undecidable one is never a pass
        var (vr, _) = TimeflowHotpatching.Verdict(tid, new() { "sometime write hp by fill_table", "always state [hp] == 5", "query writes hp expect total >= 2" }, 10, N - 1, symP);
        Check("hotpatch: verdicts over the recording itself: sometime (the write) PASS, always state [hp] == 5 FAIL (it is overwritten), the query >= 2 PASS",
              vr.Count == 3 && vr[0].Pass && !vr[1].Pass && vr[1].Evidence.Contains("does not match") && vr[2].Pass, string.Join(" | ", vr.Select(x => $"{x.Test}: {(x.Pass ? "PASS" : "FAIL")} ({x.Evidence})")));
        Check("hotpatch (negative): a verdict that is not one of the forms is refused", Throws(() => VerdictTest.Parse("maybe write hp"), out string vmsg) && vmsg.Contains("not a verdict"), vmsg);
        Check("hotpatch (negative): always with a breakpoint anchor is refused (firings are not frames)", Throws(() => VerdictTest.Parse("always write hp"), out string amsg) && amsg.Contains("frame-level"), amsg);
        if (bm != null)
        {
            var rec = TimeflowHotpatching.DivergeRecorded(bm.Id, symP, symP);
            Check("hotpatch: diverge on a recorded branch: the recordings' first differing frame (the run's) and, in lockstep, the patched instruction", rec["frame"]?.GetValue<long>() == d?["frame"]?.GetValue<long>() && rec["lockstep"]?["firstInstruction"]?["pcSymbolOld"]?.ToString() == "ft_limit", rec["summary"] + " | " + rec["lockstep"]?["summary"]);
        }
    }

    /// <summary>Byte patches on every console: a CPU-address patch at the frame's PC (resolved through the banks mapped), applied, recorded, reproducible.</summary>
    private static void HotpatchConsoles(string dir, string snes, string gb)
    {
        var sms = new byte[0x8000];
        new byte[] { 0xF3, 0x21, 0x00, 0xC0, 0x34, 0x06, 0x40, 0x10, 0xFE, 0x18, 0xF9 }.CopyTo(sms, 0);   // the counter (see sconsoles)
        System.Text.Encoding.ASCII.GetBytes("TMR SEGA").CopyTo(sms, 0x7FF0); sms[0x7FFF] = 0x4C;
        string smsPath = DebugTestRoms.Write(dir, "t5count.sms", sms);
        var (md, _, _, _) = SegaDebugTestRoms.Genesis();
        string mdPath = DebugTestRoms.Write(dir, "t5.md", md);
        var (nesRom, _) = MovieTestRoms.PlantedGame(MovieTestRoms.ParseSettings("version 1\nstart Start\nfloor 1\nbonus 1\n"));
        string nesPath = DebugTestRoms.Write(dir, "t5c.nes", nesRom);
        foreach (var (name, rom, console) in new[] { ("NES", nesPath, (string?)null), ("SNES test ROM", snes, null), ("Game Boy test ROM", gb, null), ("Master System counter", smsPath, "sms"), ("Genesis test ROM", mdPath, "md") })
        {
            string t = RecordIndexed(rom, console, 120, "auto:2", new(), index: false);
            // the PC's own byte at frame 40, by CPU address (the banks mapped there), as a no-op patch (the same byte): it must resolve, apply, record and replay as before
            uint pc; byte at;
            using (var pb = new TimeflowPlayback(t))
            using (var m = TimeflowHotpatching.Open(t, pb.Meta, null, new List<string>()))
            {
                pb.Jump(m.Source, 40);
                pc = m.Machine.Main.PC; at = m.Machine.Main.Peek(pc);
            }
            string addr = "$" + pc.ToString("X");
            var r = TimeflowHotpatching.Run(new HotpatchArgs { Timeline = t, Frame = 40, Patches = new() { $"{addr}={at:X2}>{at:X2}" } }).Report;
            Check($"hconsoles: {name}: a patch at a CPU address (the PC at frame 40, {addr}) resolves through the banks to a ROM and a file offset, applies, records a branch",
                  r.Refused == null && r.Applied.Count == 1 && r.Applied[0].Resolved.Contains("rom") && r.Branch != null, r.Refused ?? string.Join("; ", r.Applied.Select(a => $"{a.Resolved} file {a.FileOffset}")));
            Check($"hconsoles: {name} (negative): the same bytes change nothing: no divergence over the 80 frames replayed", r.Divergence?["diverged"]?.GetValue<bool>() == false, r.Divergence?["summary"]?.ToString() ?? "");
            var w = TimeflowHotpatching.Run(new HotpatchArgs { Timeline = t, Frame = 40, Patches = new() { $"{addr}={(byte)(at ^ 0xFF):X2}>{at:X2}" } }).Report;
            Check($"hconsoles: {name} (negative): wrong old bytes are refused", w.Refused?.Contains("nothing applied") == true, w.Refused ?? "not refused");
        }
        // a real change on the Master System: the counter's delay LD B,$40 -> $20 makes it count faster: the future diverges, in the work RAM, at the patched instruction
        string ts = RecordIndexed(smsPath, "sms", 120, "auto:2", new(), index: false);
        var ch = TimeflowHotpatching.Run(new HotpatchArgs { Timeline = ts, Frame = 30, Patches = new() { "$0006=40>20" } }).Report;
        Check("hconsoles: Master System: LD B,$40 -> $20 at $0006: the future diverges in the work RAM, the CPUs part at frame 30", ch.Refused == null && ch.Divergence?["diverged"]?.GetValue<bool>() == true && ch.Divergence["firstInstruction"]?["frame"]?.GetValue<long>() == 30,
              ch.Refused ?? ch.Divergence?["summary"]?.ToString() ?? "");
    }
}
