using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Movies;

namespace BrokenNes.Workshop.Movies;

/// <summary>One side of a divergence: a ROM (a build of the game), its symbols, and optionally other cores or frame timing.</summary>
internal sealed record DivergeSide(string Rom, List<string> Symbols, string? Cores, bool? Ntsc, string Label);

/// <summary>
/// The divergence finder (milestone D5): one movie played on two runs (two builds, two cores, two frame timings) in lockstep, frame by frame,
/// gives the first frame whose picture or work RAM differs; that frame is then played again on both with the trace on, giving the first
/// instruction where the main CPU's runs part (registers, the instruction, the byte it read), and the variables that differ after it by
/// symbol. Against another emulator's trace (a BrokenDev compat result: the game's variables every frame) it gives the first frame where a
/// variable differs, with the frame shift that lines the two boots up.
/// </summary>
internal static class MovieDiverge
{
    private const int SnapEvery = 60;
    private const int RingSize = 1 << 18;

    public static JsonObject Run(Movie mv, DivergeSide a, DivergeSide b, long frames, int contextLines = 4)
    {
        using var ma = MovieMachine.OpenFor(mv, a.Rom, true, a.Symbols, a.Cores, a.Ntsc);
        using var mb = MovieMachine.OpenFor(mv, b.Rom, true, b.Symbols, b.Cores, b.Ntsc);
        var j = new JsonObject
        {
            ["movie"] = $"{mv.RomName} {mv.Frames} frames", ["a"] = Describe(ma, a), ["b"] = Describe(mb, b),
        };
        long end = frames > 0 ? Math.Min(frames, mv.Frames) : mv.Frames;
        var snaps = new List<(long frame, byte[] sa, byte[] sb)>();
        var cur = new MoviePlayer.Cursor(mv);
        long first = -1; string what = "";
        for (long f = 0; f < end; f++)
        {
            if (f % SnapEvery == 0) snaps.Add((f, ma.Host.SaveState(), mb.Host.SaveState()));
            cur.At(f);
            ma.Frame(cur.P1, cur.P2); mb.Frame(cur.P1, cur.P2);
            if (ma.Crashed || mb.Crashed)
            {
                first = f; what = ma.Crashed && mb.Crashed ? "both crashed" : ma.Crashed ? $"a crashed: {ma.CrashInfo}" : $"b crashed: {mb.CrashInfo}";
                break;
            }
            ulong pa = ma.Picture(), pb = mb.Picture(), ra = ma.Ram(), rb = mb.Ram();
            if (pa != pb || ra != rb)
            {
                first = f;
                what = string.Join(" and ", new[] { pa != pb ? "the picture" : null, ra != rb ? "the work RAM" : null }.Where(x => x != null));
                break;
            }
        }
        j["framesCompared"] = first < 0 ? end : first + 1;
        if (first < 0)
        {
            j["diverged"] = false;
            j["summary"] = $"no divergence: the picture and the work RAM are the same after every one of the {end} frames";
            return j;
        }
        j["diverged"] = true;
        j["frame"] = first;
        // where the game was (the debug port's markers, D7): the last marker each run gave before the frame, and the first one they disagree on
        static string? LastMark(MovieMachine m, long upTo)
        {
            var k = m.MarksSeen.Where(x => x.Frame <= upTo).ToList();
            return k.Count == 0 ? null : $"'{k[^1].Text}' at frame {k[^1].Frame}";
        }
        j["lastMarker"] = new JsonObject { ["a"] = LastMark(ma, first), ["b"] = LastMark(mb, first) };
        {
            var ka = ma.MarksSeen.Where(k => k.Frame <= first).ToList(); var kb = mb.MarksSeen.Where(k => k.Frame <= first).ToList();
            int i = 0;
            while (i < ka.Count && i < kb.Count && ka[i] == kb[i]) i++;
            if (i < ka.Count || i < kb.Count)
                j["markersDiffer"] = $"marker {i + 1}: a {(i < ka.Count ? $"'{ka[i].Text}' at frame {ka[i].Frame}" : "none")}, b {(i < kb.Count ? $"'{kb[i].Text}' at frame {kb[i].Frame}" : "none")}";
        }
        j["framesRunWhenFound"] = first + 1;
        j["what"] = what;
        if (what.Contains("crashed")) { j["summary"] = $"frame {first}: {what}"; return j; }

        // the frame again, traced, from the newest snapshot before it
        var s = snaps.Last(x => x.frame <= first);
        ma.Host.LoadState(s.sa); mb.Host.LoadState(s.sb);
        for (long f = s.frame; f < first; f++) { cur.At(f); ma.Frame(cur.P1, cur.P2); mb.Frame(cur.P1, cur.P2); }
        var before = (a: SymbolDiff.Capture(ma.Host.Machine.Main), b: SymbolDiff.Capture(mb.Host.Machine.Main));
        foreach (var m in new[] { ma, mb }) { m.Host.Dbg.TraceRingSize = RingSize; m.Host.Dbg.TraceRing = true; }
        cur.At(first);
        ma.Frame(cur.P1, cur.P2); mb.Frame(cur.P1, cur.P2);
        bool again = ma.Picture() != mb.Picture() || ma.Ram() != mb.Ram();
        if (!again) j["note"] = "the frame did not differ again when replayed from the snapshot (a run that is not deterministic): the instruction search below is on the replay";
        var ta = ma.Host.Dbg.TraceTail(RingSize); var tb = mb.Host.Dbg.TraceTail(RingSize);
        j["instructionsA"] = ta.Length; j["instructionsB"] = tb.Length;
        if (ta.Length >= RingSize || tb.Length >= RingSize) j["traceNote"] = $"the frame ran more than {RingSize} instructions: only its last {RingSize} are compared";
        int n = Math.Min(ta.Length, tb.Length), at = -1, timingAt = -1;
        for (int i = 0; i < n; i++)
        {
            if (!SameData(ta[i], tb[i])) { at = i; break; }
            if (timingAt < 0 && (ta[i].Scanline != tb[i].Scanline || ta[i].Dot != tb[i].Dot)) timingAt = i;
        }
        if (at < 0 && ta.Length != tb.Length) at = n;
        var da = ma.Host.Dbg; var db = mb.Host.Dbg;
        JsonArray Ctx(TraceRecord[] t, Debugger d, int i)
        {
            var arr = new JsonArray();
            for (int k = Math.Max(0, i - contextLines); k <= Math.Min(t.Length - 1, i); k++) arr.Add($"#{k} " + d.FormatTrace(t[k]));
            return arr;
        }
        if (at >= 0)
        {
            var ins = new JsonObject { ["index"] = at, ["a"] = Ctx(ta, da, at), ["b"] = Ctx(tb, db, at) };
            if (at < ta.Length) ins["pcSymbolA"] = da.SymbolAt(ma.Host.Machine.Main, ta[at].Pc);
            if (at < tb.Length) ins["pcSymbolB"] = db.SymbolAt(mb.Host.Machine.Main, tb[at].Pc);
            if (at == n) ins["why"] = $"one side ran more instructions in the frame ({ta.Length} against {tb.Length}): the same {n}, then one side went on";
            j["firstInstruction"] = ins;
        }
        else if (timingAt >= 0)
            j["firstTiming"] = new JsonObject
            {
                ["index"] = timingAt, ["why"] = "the main CPU ran the same instructions with the same values, but at a different place on the screen (scanline / dot) from here: a timing difference",
                ["a"] = Ctx(ta, da, timingAt), ["b"] = Ctx(tb, db, timingAt),
            };
        else j["cpuSame"] = $"the main CPU ran the same {n} instructions, with the same registers and values, at the same scanlines and dots: the difference comes from elsewhere (the picture or sound chip, a DMA, another CPU)";
        // the other CPUs (the SNES's SPC700)
        foreach (var c in ma.Host.Machine.Cpus.Skip(1))
        {
            var xa = da.TraceTail(RingSize, c.Id); var xb = db.TraceTail(RingSize, c.Id);
            int m2 = Math.Min(xa.Length, xb.Length), k2 = -1;
            for (int i = 0; i < m2; i++) if (!SameData(xa[i], xb[i])) { k2 = i; break; }
            if (k2 < 0 && xa.Length != xb.Length) k2 = m2;
            j["cpu_" + c.Id] = k2 < 0 ? $"same {m2} instructions" : $"first difference at instruction {k2}: a {(k2 < xa.Length ? da.FormatTrace(xa[k2], c.Id) : "(ended)")} | b {(k2 < xb.Length ? db.FormatTrace(xb[k2], c.Id) : "(ended)")}";
        }
        foreach (var m in new[] { ma, mb }) m.Host.Dbg.TraceRing = false;
        // the variables, by symbol: at the start of the frame (where the work RAM still agreed) and after it
        var after = (a: SymbolDiff.Capture(ma.Host.Machine.Main), b: SymbolDiff.Capture(mb.Host.Machine.Main));
        var dBefore = SymbolDiff.Compare(ma.Host.Machine.Main, da.Symbols, before.a, mb.Host.Machine.Main, db.Symbols, before.b);
        var dAfter = SymbolDiff.Compare(ma.Host.Machine.Main, da.Symbols, after.a, mb.Host.Machine.Main, db.Symbols, after.b);
        j["stateBefore"] = DiffJson(dBefore);
        j["stateAfter"] = DiffJson(dAfter);
        string where = at >= 0 && at < ta.Length ? $"; the main CPU parts at instruction {at} of the frame (a {ta[at].Pc:X4}{(da.SymbolAt(ma.Host.Machine.Main, ta[at].Pc) is { } sy ? " " + sy : "")})"
                     : timingAt >= 0 ? $"; the same instructions, timing differs from instruction {timingAt}" : "; the main CPU ran the same instructions";
        j["summary"] = $"first difference in frame {first} (after {first + 1} frames run): {what}{where}; {dAfter.VariablesDiffering} variable(s) differ after the frame";
        return j;
    }

    // the record keeps 4 bytes from the PC; only the instruction's own (Length) are the instruction
    private static uint OwnBytes(in TraceRecord r) => r.Length >= 4 ? r.Bytes : r.Bytes & ((1u << (8 * Math.Max(1, (int)r.Length))) - 1);

    private static bool SameData(in TraceRecord x, in TraceRecord y) =>
        x.Pc == y.Pc && x.Length == y.Length && OwnBytes(x) == OwnBytes(y) && x.Kind == y.Kind && x.R0 == y.R0 && x.R1 == y.R1 && x.R2 == y.R2 && x.R3 == y.R3 && x.R4 == y.R4
        && x.P == y.P && x.R5 == y.R5 && x.R6 == y.R6 && x.Ea == y.Ea && x.EaValue == y.EaValue;

    private static JsonObject Describe(MovieMachine m, DivergeSide s) => new()
    {
        ["label"] = s.Label, ["rom"] = m.RomPath, ["romSha256"] = m.RomSha256, ["cores"] = m.Cores, ["ntsc"] = m.PowerOnNtsc, ["symbols"] = m.Host.Dbg?.Symbols.All.Count ?? 0,
    };

    public static JsonObject DiffJson(SymbolDiff.Result r) => new()
    {
        ["variablesDiffering"] = r.VariablesDiffering, ["symbols"] = r.Symbols,
        ["variables"] = new JsonArray(r.Variables.Select(v => (JsonNode)new JsonObject
        {
            ["variable"] = v.Name, ["at"] = v.Place, ["a"] = string.Join(" ", v.A.Select(x => x.ToString("X2"))), ["b"] = string.Join(" ", v.B.Select(x => x.ToString("X2"))),
            ["valueA"] = v.ValueA, ["valueB"] = v.ValueB,
        }).ToArray()),
        ["spaces"] = new JsonArray(r.Spaces.Select(s => (JsonNode)new JsonObject { ["space"] = s.Space, ["bytesDiffering"] = s.Differing, ["withoutSymbol"] = s.WithoutSymbol, ["first"] = new JsonArray(s.FirstWithoutSymbol.Select(x => (JsonNode)x).ToArray()) }).ToArray()),
    };

    // ------------------------------------------------------------------------------------------------ against another emulator's trace

    /// <summary>
    /// The movie on BrokenNes against a BrokenDev compat run of another emulator (<paramref name="compatDir"/> holds its spec.txt and
    /// result.txt: the game's variables after every frame). The two boots are lined up by the frame shift (within <paramref name="maxShift"/>)
    /// that matches the most frames before the first difference; the result names that shift, the first differing frame and the variables.
    /// Instruction level needs both sides' instruction traces, which another emulator's compat run does not have: this stops at the frame.
    /// </summary>
    public static JsonObject Against(Movie mv, DivergeSide a, string compatDir, int maxShift)
    {
        string spec = Path.Combine(compatDir, "spec.txt"), result = Path.Combine(compatDir, "result.txt");
        if (!File.Exists(spec) || !File.Exists(result)) throw new FileNotFoundException($"{compatDir} is not a BrokenDev compat run (it needs spec.txt and result.txt)");
        var reads = new List<(string name, string domain, int addr)>();
        foreach (var line in File.ReadAllLines(spec))
        {
            var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 4 && p[0] == "read") reads.Add((p[1], p[2], Convert.ToInt32(p[3], 16)));
        }
        string emu = "?"; string[] names = Array.Empty<string>();
        var theirs = new Dictionary<long, int[]>();
        foreach (var line in File.ReadAllLines(result))
        {
            if (line.StartsWith("emu ")) emu = line[4..];
            else if (line.StartsWith("reads ")) names = line[6..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            else if (line.StartsWith("t "))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                theirs[long.Parse(p[1], CultureInfo.InvariantCulture)] = p.Skip(2).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            }
        }
        if (reads.Count == 0 || names.Length != reads.Count) throw new InvalidDataException($"{compatDir}: the spec's reads ({reads.Count}) and the result's ({names.Length}) do not line up");
        using var ma = MovieMachine.OpenFor(mv, a.Rom, false, null, a.Cores, a.Ntsc);
        var domains = ma.Host.Domains();
        NesEmulator.Corruption.IMemoryDomain Dom(string d) => (ma.ConsoleKey, d) switch
        {
            ("nes", "ram") => domains.First(x => x.Id == "RAM"),
            (_, "wram") => domains.First(x => x.Id == "WRAM"),
            (_, "hram") => domains.First(x => x.Id == "HRAM"),
            (_, "cartram") => domains.First(x => x.Kind == NesEmulator.Corruption.DomainKind.CartRam),
            _ => throw new InvalidDataException($"read domain '{d}' is not one of the {ma.ConsoleKey}'s"),
        };
        var doms = reads.Select(r => Dom(r.domain)).ToArray();
        long end = Math.Min(mv.Frames, theirs.Count == 0 ? 0 : theirs.Keys.Max());
        var ours = new List<int[]>();
        var cur = new MoviePlayer.Cursor(mv);
        for (long f = 0; f < end; f++)
        {
            cur.At(f); ma.Frame(cur.P1, cur.P2);
            if (ma.Crashed) break;
            ours.Add(reads.Select((r, i) => (int)doms[i].Peek(r.addr)).ToArray());
        }
        // the shift that lines the boots up: ours after n frames = theirs after n + shift
        int bestShift = 0; long bestFirst = -1, bestScore = -1;
        foreach (int sh in Enumerable.Range(0, 2 * maxShift + 1).Select(i => i % 2 == 0 ? i / 2 : -(i + 1) / 2))   // 0, -1, 1, -2, 2...: a tie keeps the smaller shift
        {
            // the score: frames that agree before the first that does not (frames the other run does not have are not counted)
            long firstBad = -1, matched = 0;
            for (int i = 0; i < ours.Count; i++)
            {
                if (!theirs.TryGetValue(i + 1 + sh, out var t)) continue;
                if (!t.SequenceEqual(ours[i])) { firstBad = i + 1; break; }
                matched++;
            }
            long score = firstBad < 0 ? long.MaxValue / 2 + matched : matched;
            if (score > bestScore) { bestScore = score; bestShift = sh; bestFirst = firstBad; }
        }
        var j = new JsonObject { ["movie"] = $"{mv.RomName} {mv.Frames} frames", ["a"] = Describe(ma, a), ["other"] = emu, ["compat"] = compatDir, ["variables"] = new JsonArray(names.Select(x => (JsonNode)x).ToArray()), ["shift"] = bestShift, ["framesCompared"] = ours.Count };
        j["shiftMeaning"] = bestShift == 0 ? "the boots line up frame for frame" : $"BrokenNes after n frames is the other emulator after n {(bestShift > 0 ? "+" : "-")} {Math.Abs(bestShift)}";
        if (bestFirst < 0)
        {
            j["diverged"] = false;
            j["summary"] = $"no divergence from {emu}: every variable agrees over {ours.Count} frames (shift {bestShift})";
            return j;
        }
        var o = ours[(int)bestFirst - 1]; var th = theirs[bestFirst + bestShift];
        var diff = new JsonArray();
        for (int i = 0; i < names.Length; i++) if (o[i] != th[i]) diff.Add(new JsonObject { ["variable"] = names[i], ["brokennes"] = o[i], ["other"] = th[i] });
        j["diverged"] = true; j["frame"] = bestFirst - 1; j["framesRunWhenFound"] = bestFirst; j["differing"] = diff;
        j["summary"] = $"first difference from {emu} after {bestFirst} frames (shift {bestShift}): {string.Join(", ", diff.Select(d => $"{d!["variable"]} BrokenNes {d["brokennes"]} / other {d["other"]}"))}";
        j["note"] = "the other emulator's compat run records the game's variables, not its instructions: the instruction-level search needs both runs on BrokenNes (two builds, two cores)";
        return j;
    }
}
