using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;

namespace BrokenNes.Mcp;

/// <summary>
/// The agent play loop (Great Compatibility Catalog P0, scope items 4 and 5; docs/projects/compat-catalog-plan.md): play_step (pads held for some
/// frames or until a condition, then a small picture, the watched RAM, the frame and the lag in ONE call), the session's recording (start, checkpoints,
/// stop: a .bnm movie with its marks), and the live reference (a lock-stepped Mesen or BizHawk on the same ROM, fed the same pads, its picture and
/// a difference score returned beside BrokenNes's). docs/projects/compat-agent-playbook.md is the agents' manual.
/// </summary>
internal sealed partial class HeadlessSession
{
    private RefEmu? reference;
    private long refSyncedAt = -1;                  // the BrokenNes frame the reference was last brought to (-1: never)
    private List<(string name, long addr)> refRam = new();
    private bool recording;
    private long recStartFrame;
    private string? recPath;
    private readonly List<MovieMark> recMarks = new();
    private readonly List<(long frame, double score)> refScores = new();   // the step scores since the reference opened / the recording started
    private int refHighRun;                                                 // consecutive steps with score > 0.12

    /// <summary>median / max of the step scores, computed here so catalog records never hold an agent's guess.</summary>
    private JsonObject RefStats()
    {
        var j = new JsonObject { ["steps"] = refScores.Count, ["highRun"] = refHighRun };
        if (refScores.Count == 0) return j;
        var s = refScores.Select(x => x.score).OrderBy(x => x).ToList();
        double med = s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        var mx = refScores.OrderByDescending(x => x.score).First();
        j["median"] = Math.Round(med, 4); j["max"] = Math.Round(mx.score, 4); j["maxAtFrame"] = mx.frame;
        j["over012"] = refScores.Count(x => x.score > 0.12);
        j["offset"] = reference?.Offset;
        j["jankHint"] = refHighRun >= 3 ? "score > 0.12 for 3+ steps in a row: compare and describe" : null;
        return j;
    }

    private void CloseAgentLoop()
    {
        try { reference?.Dispose(); } catch { }
        reference = null;
    }

    /// <summary>The marks of the recording go into every movie this session saves (MovieSave).</summary>
    private void AddRecordingMarks(Movie m)
    {
        foreach (var k in recMarks) m.Marks.Add(k);
        if (recording || recMarks.Count > 0) m.Comments.Add($"recorded with play_step / record (BrokenNes MCP); recording started at frame {recStartFrame}");
    }

    /// <summary>sha1 of the ROM file, and of the file without a 16-byte iNES header (NES); the catalog checks its record's sha1 against these.</summary>
    private (string file, string? noHeader) RomSha1()
    {
        byte[] b = File.ReadAllBytes(romPath);
        if (romPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            try { using var z = System.IO.Compression.ZipFile.OpenRead(romPath); var e = z.Entries.OrderByDescending(x => x.Length).First(); using var ms = new MemoryStream(); e.Open().CopyTo(ms); b = ms.ToArray(); } catch { }
        static string H(ReadOnlySpan<byte> d) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(d)).ToLowerInvariant();
        string? nh = b.Length > 16 && b[0] == (byte)'N' && b[1] == (byte)'E' && b[2] == (byte)'S' && b[3] == 0x1A ? H(b.AsSpan(16)) : null;
        return (H(b), nh);
    }

    private long LagCount() => checks?.Frames.Count(f => f.Lag) ?? 0;
    private int CheckFrames() => checks?.Frames.Count ?? 0;

    private Pic CurrentPic() { var (px, w, h) = Picture(); return new Pic(px, w, h); }

    // ================================================================================================ play_step

    public ToolOut PlayStep(string[] buttons, string[] buttons2, int frames, string? until, int maxFrames, double scale, List<string> watch, bool refPicture)
    {
        if (frames < 0) throw new ToolError("frames is 0 or more");
        var sw = Stopwatch.StartNew();
        var p1 = ParseButtons(buttons);
        var p2 = ParseButtons(buttons2);
        long before = Interlocked.Read(ref framesStarted);
        BranchHistory();
        var notes = new JsonArray();
        // the reference was left behind by another tool's frames (run, input, ...): it catches up with no pads (said in the result)
        if (reference != null && refSyncedAt >= 0 && refSyncedAt != before)
        {
            notes.Add($"frames {refSyncedAt}-{before} ran outside play_step: the reference caught up with no pads pressed (it may be out of step now; use play_step only while a reference is open)");
            reference.RunTo(before + reference.Offset, PadButtons.None, PadButtons.None, Array.Empty<long>(), false);
        }
        long lagBefore = LagCount();
        int ckBefore = CheckFrames();
        lock (padLock) { held[0] = p1; held[1] = p2; }
        string stoppedBy = "frames";
        RunOutcome o = RunOutcome.Frames;
        // try/finally: a bad `until` (ToolError) or a run exception must not leave the pads held for the next tool's frames
        Pic? minePrev = null;
        bool wantPrev = reference != null;
        try {
        if (until is { Length: > 0 })
        {
            var d = Dbg;
            int budget = Math.Clamp(maxFrames, 1, 36_000);
            stoppedBy = "budget";
            for (int i = 0; i < budget; i++)
            {
                if (wantPrev && i > 0) { EnsureBetweenFrames(); minePrev = CurrentPic(); }
                o = RunFrames(1);
                if (o != RunOutcome.Frames) { stoppedBy = o.ToString().ToLowerInvariant(); break; }
                long v;
                try { v = d.Evaluate(until); }
                catch (Exception e) when (e is FormatException or ArgumentException) { throw new ToolError($"until: {e.Message}"); }
                if (v != 0) { stoppedBy = "condition"; break; }
            }
        }
        else
        {
            int n = Math.Clamp(frames, 0, 36_000);
            if (wantPrev && n >= 2)
            {
                o = RunFrames(n - 1);
                if (o == RunOutcome.Frames) { EnsureBetweenFrames(); minePrev = CurrentPic(); o = RunFrames(1); }
            }
            else o = RunFrames(n);
            if (o != RunOutcome.Frames) stoppedBy = o.ToString().ToLowerInvariant();
        }
        } finally { lock (padLock) { held[0] = PadButtons.None; held[1] = PadButtons.None; } }
        long ran = Interlocked.Read(ref framesStarted) - before;

        var j = new JsonObject
        {
            ["frame"] = Interlocked.Read(ref framesStarted), ["framesRun"] = ran, ["stoppedBy"] = stoppedBy, ["pressed"] = p1.ToString(),
        };
        if (p2 != PadButtons.None) j["pressed2"] = p2.ToString();
        if (o == RunOutcome.Break && D?.LastBreak is { } b) j["break"] = BreakJson(b);
        if (o == RunOutcome.Error) j["error"] = R?.Error?.Message;
        // lag: from the checks' profiler (a frame where vblank came before the game reached its frame wait)
        if (checks != null)
        {
            long lag = LagCount() - lagBefore;
            j["lagFrames"] = lag;
            j["lagging"] = lag > 0;
            j["framesProfiled"] = CheckFrames() - ckBefore;
        }
        else j["lagging"] = null;
        // RAM: the watches the session has (watch tool) and the ones asked for here
        var vals = new JsonObject();
        foreach (var w in watch)
        {
            string name = w, expr = w;
            int eq = w.IndexOf('=');
            if (eq > 0) { name = w[..eq].Trim(); expr = w[(eq + 1)..].Trim(); }
            try { vals[name] = D != null ? D.Evaluate(expr) : null; }
            catch (Exception e) { vals[name] = "error: " + e.Message; }
        }
        if (watch.Count > 0) j["ram"] = vals;
        if (watches.Count > 0) j["watches"] = WatchValues();

        var outp = new ToolOut(j);
        Pic? mine = null;
        if (scale > 0 || reference != null)
        {
            EnsureBetweenFrames();
            mine = CurrentPic();
            j["hash"] = Png.Stats(mine.Px, mine.Px.Length).hash;
            if (scale > 0) outp.Pngs.Add(mine.Png(scale));
        }
        if (reference != null)
        {
            long t0 = sw.ElapsedMilliseconds;
            var addrs = refRam.Select(r => r.addr).ToList();
            long target = Interlocked.Read(ref framesStarted) + reference.Offset;
            Pic? rpPrev = null;
            if (ran >= 2 && target - 1 > reference.Frame) rpPrev = reference.RunTo(target - 1, p1, p2, Array.Empty<long>(), true).pic;
            var (rf, rv, rp) = reference.RunTo(target, p1, p2, addrs, true);
            refSyncedAt = Interlocked.Read(ref framesStarted);
            var rj = new JsonObject { ["emulator"] = reference.Emulator, ["frame"] = rf, ["offset"] = reference.Offset };
            if (rp != null && mine != null)
            {
                var d = PicDiff.StepScore(mine, minePrev, rp, rpPrev);
                rj["diff"] = d;
                double sc = (double)d["score"]!;
                if (ran > 0) { refScores.Add((Interlocked.Read(ref framesStarted), sc)); refHighRun = sc > 0.12 ? refHighRun + 1 : 0; }
                rj["stats"] = RefStats();
                if (refPicture && scale > 0) outp.Pngs.Add(rp.Png(scale));
            }
            if (refRam.Count > 0)
            {
                var ram = new JsonObject();
                int mismatches = 0;
                for (int i = 0; i < refRam.Count; i++)
                {
                    long? ours = null;
                    try { ours = D?.Evaluate($"[${refRam[i].addr:X}]"); } catch { }
                    long theirs = i < rv.Length ? rv[i] : -1;
                    if (ours != theirs) mismatches++;
                    ram[refRam[i].name] = new JsonObject { ["brokennes"] = ours, ["reference"] = theirs };
                }
                rj["ram"] = ram;
                rj["ramMismatches"] = mismatches;
            }
            rj["ms"] = sw.ElapsedMilliseconds - t0;
            j["reference"] = rj;
            if (refPicture && scale > 0) j["pictures"] = "the first image is BrokenNes, the second the reference";
        }
        if (notes.Count > 0) j["notes"] = notes;
        j["ms"] = sw.ElapsedMilliseconds;
        return outp;
    }

    // ================================================================================================ recording

    public JsonObject Record(string action, string? path, string? text, string? state, long? atFrame = null)
    {
        var h = history ?? throw new ToolError(historyNote);
        switch (action)
        {
            case "start":
                recording = true; recStartFrame = h.FrameIndex; recPath = path is { Length: > 0 } ? path : null; recMarks.Clear();
                refScores.Clear(); refHighRun = 0;
                recMarks.Add(new MovieMark(h.FrameIndex, "recording start" + (text is { Length: > 0 } ? ": " + text : "")));
                return new JsonObject
                {
                    ["recording"] = true, ["frame"] = h.FrameIndex, ["path"] = recPath,
                    ["note"] = "the movie keeps the session's whole input from its start (power-on for a fresh session), so it replays from power-on and exports to bk2 / fm2; the start is a mark in it",
                };
            case "mark":
            {
                if (!recording) throw new ToolError("no recording: record action start first");
                string t = text is { Length: > 0 } ? text : $"checkpoint {recMarks.Count}";
                long mf = h.FrameIndex;
                if (atFrame is long af)
                {
                    if (af < recStartFrame || af > h.FrameIndex) throw new ToolError($"frame {af}: a mark goes between the recording's start ({recStartFrame}) and now ({h.FrameIndex})");
                    if (state is { Length: > 0 } && af != h.FrameIndex) throw new ToolError("state saves the state now: leave frame out with state");
                    mf = af;
                }
                recMarks.Add(new MovieMark(mf, t));
                recMarks.Sort((x, y) => x.Frame.CompareTo(y.Frame));
                var j = new JsonObject { ["mark"] = t, ["frame"] = mf, ["now"] = h.FrameIndex, ["marks"] = recMarks.Count };
                if (state is { Length: > 0 }) { SaveState(state); j["state"] = state; }
                return j;
            }
            case "stop":
            case "save":
            {
                if (!recording && recMarks.Count == 0) throw new ToolError("no recording: record action start first");
                recMarks.Add(new MovieMark(h.FrameIndex, "recording end" + (text is { Length: > 0 } ? ": " + text : "")));
                var j = MovieSave(path is { Length: > 0 } ? path : recPath);
                j["marks"] = J.Arr(recMarks.Select(m => (JsonNode)new JsonObject { ["frame"] = m.Frame, ["text"] = m.Text }));
                if (reference != null || refScores.Count > 0)
                {
                    // the reference summary the catalog record takes (catalog.py update --tas reads <movie>.ref.json): computed here, not by the agent
                    var rs = RefStats();
                    rs["emulator"] = reference?.Emulator == "bizhawk" ? "BizHawk 2.11.1" : "Mesen 2.1.1";
                    rs["frames"] = h.FrameIndex; rs["rom"] = romPath; var (fs1, hs1) = RomSha1(); rs["romSha1"] = fs1; rs["romSha1NoHeader"] = hs1;
                    j["reference"] = rs;
                    if (j["file"]?.GetValue<string>() is { Length: > 0 } mp)
                        try { File.WriteAllText(mp + ".ref.json", rs.ToJsonString()); j["referenceSummary"] = mp + ".ref.json"; } catch (Exception e) { j["referenceSummaryError"] = e.Message; }
                }
                if (action == "stop") { recording = false; recMarks.Clear(); }
                else recMarks.RemoveAt(recMarks.Count - 1);
                return j;
            }
            case "status":
                return new JsonObject
                {
                    ["recording"] = recording, ["startFrame"] = recStartFrame, ["frame"] = h.FrameIndex, ["path"] = recPath,
                    ["marks"] = J.Arr(recMarks.Select(m => (JsonNode)new JsonObject { ["frame"] = m.Frame, ["text"] = m.Text })),
                };
            default: throw new ToolError("action is start, mark, save, stop or status");
        }
    }

    // ================================================================================================ the reference

    public ToolOut Reference(string action, string emulator, long? offset, List<string> ram, int alignFrames, int alignSearch, double scale, bool advance = false)
    {
        switch (action)
        {
            case "open":
            {
                if (reference != null) throw new ToolError("a reference is open: close it first");
                var sw = Stopwatch.StartNew();
                EnsureBetweenFrames();
                reference = RefEmu.Open(emulator, ConsoleKey, romPath, Folder, emulator == "bizhawk" || (emulator is "" or "auto" && ConsoleKey == "md") ? 300_000 : 90_000);
                SetRefRam(ram);
                long now = Interlocked.Read(ref framesStarted);
                reference.Offset = offset ?? 0;
                var j = new JsonObject { ["opened"] = reference.Describe(), ["ms"] = sw.ElapsedMilliseconds };
                // a session already past power-on: the reference catches up with no pads (its frames before are no-input frames)
                if (now + reference.Offset > reference.Frame)
                {
                    reference.RunTo(now + reference.Offset, PadButtons.None, PadButtons.None, Array.Empty<long>(), false);
                    if (now > 0) j["note"] = $"BrokenNes was at frame {now}: the reference ran to {now + reference.Offset} with no pads (open the reference before playing for a faithful comparison)";
                }
                else if (reference.Frame > now + reference.Offset)
                {
                    // Mesen has already done a frame when its script says hello: offset 0 then put every pad one frame out of step (the NES
                    // pilot's Battletoads / M.C. Kids drift, 2026-11-15). The offset takes the reference's head start unless one was given.
                    if (offset == null) { reference.Offset = reference.Frame - now; j["note"] = $"the reference had already done {reference.Frame} frames at its start: offset set to {reference.Offset}"; }
                    else j["note"] = $"the reference had already done {reference.Frame} frames at its start; BrokenNes is at {now}: the offset is at least {reference.Frame - now} until align or offset changes it";
                }
                refSyncedAt = now;
                refScores.Clear(); refHighRun = 0;
                if (alignFrames > 0) j["align"] = Align(alignFrames, alignSearch, advance);
                j["reference"] = reference.Describe();
                return j;
            }
            case "align":
                if (reference == null) throw new ToolError("no reference: action open first");
                return Align(Math.Max(30, alignFrames), alignSearch, advance);
            case "offset":
            {
                if (reference == null) throw new ToolError("no reference: action open first");
                if (offset is not long off) throw new ToolError("offset is needed");
                EnsureBetweenFrames();
                long now = Interlocked.Read(ref framesStarted);
                var j = new JsonObject { ["offsetBefore"] = reference.Offset, ["brokennesFrameBefore"] = now, ["referenceFrameBefore"] = reference.Frame };
                long behind = reference.Frame - (now + off);
                if (behind > 0)
                {
                    // the reference cannot go back: BrokenNes runs forward instead (no pads), which puts unplayed frames in the movie
                    GuardAdvance(behind, advance, "offset " + off);
                    lock (padLock) { held[0] = PadButtons.None; held[1] = PadButtons.None; }
                    RunFrames(behind);
                    j["brokennesAdvanced"] = behind;
                    j["did"] = $"the reference cannot go back, so BrokenNes ran {behind} frames with no pads (frame {now} -> {now + behind}) to make the offset {off}";
                    if (recording) recMarks.Add(new MovieMark(history?.FrameIndex ?? 0, $"reference offset {off}: BrokenNes advanced {behind} frames with no pads"));
                }
                else
                {
                    j["brokennesAdvanced"] = 0;
                    j["did"] = -behind > 0 ? $"offset set; the reference runs {-behind} frames with no pads at the next step to catch up" : "offset set";
                }
                reference.Offset = off;
                refSyncedAt = Interlocked.Read(ref framesStarted);
                j["offset"] = off; j["brokennesFrame"] = refSyncedAt; j["referenceFrame"] = reference.Frame;
                return j;
            }
            case "ram":
                if (reference == null) throw new ToolError("no reference: action open first");
                SetRefRam(ram);
                return new JsonObject { ["ram"] = J.Strs(refRam.Select(r => $"{r.name}=${r.addr:X}")) };
            case "compare":
            {
                if (reference == null) throw new ToolError("no reference: action open first");
                EnsureBetweenFrames();
                var mine = CurrentPic();
                var (rf, _, rp) = reference.RunTo(Interlocked.Read(ref framesStarted) + reference.Offset, PadButtons.None, PadButtons.None, Array.Empty<long>(), true);
                refSyncedAt = Interlocked.Read(ref framesStarted);
                var o = new ToolOut(new JsonObject { ["frame"] = Interlocked.Read(ref framesStarted), ["referenceFrame"] = rf, ["diff"] = PicDiff.StepScore(mine, null, rp!, null), ["pictures"] = "BrokenNes, then the reference" });
                o.Pngs.Add(mine.Png(scale <= 0 ? 1 : scale)); o.Pngs.Add(rp!.Png(scale <= 0 ? 1 : scale));
                return o;
            }
            case "status":
                return reference?.Describe() ?? new JsonObject { ["reference"] = null };
            case "close":
            {
                if (reference == null) return new JsonObject { ["closed"] = false };
                var d = reference.Describe();
                CloseAgentLoop();
                refSyncedAt = -1;
                return new JsonObject { ["closed"] = true, ["was"] = d };
            }
            default: throw new ToolError("action is open, align, offset, ram, compare, status or close");
        }
    }

    private void SetRefRam(List<string> ram)
    {
        var l = new List<(string, long)>();
        foreach (var r in ram)
        {
            string name = r, spec = r;
            int eq = r.IndexOf('=');
            if (eq > 0) { name = r[..eq].Trim(); spec = r[(eq + 1)..].Trim(); }
            spec = spec.Replace(":", "").TrimStart('$');
            if (spec.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) spec = spec[2..];
            if (!long.TryParse(spec, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long a)) throw new ToolError($"ram '{r}': name=$address (a CPU bus address in hex: $075A, 7E:0DBF, $FFFE10)");
            l.Add((name, a));
        }
        refRam = l;
    }

    /// <summary>
    /// The boot offset: both machines run <paramref name="frames"/> frames with no pads, picture by picture; the offset d (the reference's frame =
    /// BrokenNes's + d) whose pictures match best over the frames both have is kept. Runs the frames (the session's frame moves on by that many).
    /// </summary>
    private void GuardAdvance(long frames, bool advance, string what)
    {
        if (recording && !advance)
            throw new ToolError($"{what} would run BrokenNes {frames} frames with no pads during the recording (frames you never looked at, in the movie): nothing was done. " +
                "Keep the offset, or pass advance:true to do it anyway (it is reported and marked in the movie).");
    }

    private JsonObject Align(int frames, int search, bool advance)
    {
        var r = reference!;
        frames = Math.Clamp(frames, 30, 1200);
        search = Math.Clamp(search, 0, 300);
        var sw = Stopwatch.StartNew();
        EnsureBetweenFrames();
        long start = Interlocked.Read(ref framesStarted);
        long offBefore = r.Offset;
        GuardAdvance(frames, advance, "align");
        if (r.Frame > start + r.Offset) r.Offset = r.Frame - start;
        long rStart = r.Frame;
        lock (padLock) { held[0] = PadButtons.None; held[1] = PadButtons.None; }
        var mine = new List<Pic>();
        var theirs = new List<Pic>();
        // BrokenNes frames start+1 .. start+frames; the reference's rStart+1 .. rStart+frames+search
        for (int i = 0; i < frames; i++) { RunFrames(1); mine.Add(CurrentPic().Half()); }
        for (int i = 0; i < frames + search; i++) { var (_, _, p) = r.RunTo(rStart + i + 1, PadButtons.None, PadButtons.None, Array.Empty<long>(), true, half: true); theirs.Add(p!); }
        // pictures of different sizes (a Game Gear screen inside Mesen's 256x240 frame): the reference's are cropped once, where the last
        // BrokenNes picture fits best, so the offset search compares like sizes (a placement search per pair would take minutes)
        var m0 = mine[^1];
        if (theirs.Count > 0 && theirs[0].W >= m0.W && theirs[0].H >= m0.H && (theirs[0].W != m0.W || theirs[0].H != m0.H))
        {
            var fit = PicDiff.Compare(m0, theirs[Math.Min(theirs.Count - 1, mine.Count - 1 + (int)(rStart - start))]);
            if (fit["dx"] is JsonNode dxn && fit["dy"] is JsonNode dyn)
                theirs = theirs.Select(t => PicDiff.Crop(t, m0, (int)dxn, (int)dyn)).ToList();
        }
        double best = double.MaxValue; long bestD = 0;
        var curve = new JsonArray();
        // mine[i] is BrokenNes frame start+1+i; theirs[k] is reference frame rStart+1+k; offset d = (rStart+1+k) - (start+1+i)
        for (long d = rStart - start - search; d <= rStart - start + search; d++)
        {
            double sum = 0; int n = 0;
            for (int i = 0; i < mine.Count; i++)
            {
                long k = start + i - rStart + d;
                if (k < 0 || k >= theirs.Count) continue;
                if (i % 3 != 0) continue;   // every third frame: enough, and fast
                sum += PicDiff.Score(mine[i], theirs[(int)k]); n++;
            }
            if (n < Math.Min(10, mine.Count / 6)) continue;
            double avg = sum / n;
            if (avg < best - 1e-9) { best = avg; bestD = d; }
            if ((d - (rStart - start)) % 5 == 0) curve.Add($"{d}:{avg:F3}");
        }
        long nowMine = Interlocked.Read(ref framesStarted);
        long extra = 0;
        if (nowMine + bestD < r.Frame)
        {
            // the reference is ahead of where the best offset wants it: BrokenNes runs on (no pads) to catch up
            extra = r.Frame - bestD - nowMine;
            RunFrames(extra);
            nowMine += extra;
        }
        long advanced = Interlocked.Read(ref framesStarted) - start;
        if (recording) recMarks.Add(new MovieMark(history?.FrameIndex ?? 0, $"reference align: BrokenNes advanced {advanced} frames with no pads, offset {offBefore} -> {bestD}"));
        r.Offset = bestD;
        r.RunTo(nowMine + bestD, PadButtons.None, PadButtons.None, Array.Empty<long>(), false);
        refSyncedAt = Interlocked.Read(ref framesStarted);
        return new JsonObject
        {
            ["offset"] = bestD, ["meanScore"] = Math.Round(best, 4), ["framesCompared"] = frames, ["searched"] = $"{rStart - start - search}..{rStart - start + search}",
            ["curve"] = curve, ["frame"] = Interlocked.Read(ref framesStarted), ["referenceFrame"] = r.Frame, ["ms"] = sw.ElapsedMilliseconds,
            ["offsetBefore"] = offBefore, ["brokennesFrameBefore"] = start, ["brokennesAdvanced"] = advanced,
            ["did"] = $"BrokenNes ran {advanced} frames with no pads (frame {start} -> {start + advanced}; {extra} of them so the offset could move back); offset {offBefore} -> {bestD}",
            ["note"] = best > 0.12 ? "no offset makes the boot pictures match well: the two emulators may draw this boot differently (check with reference compare)" : "reference frame = BrokenNes frame + offset from here",
        };
    }
}
