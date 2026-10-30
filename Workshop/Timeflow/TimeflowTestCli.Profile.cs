using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokenNes.Workshop.Debug;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>
/// T3's tests (docs/projects/brokendev-timeflow-plan.md, "T3 as built"), groups of <c>--timeflow-test</c>:
/// <c>profile</c> (the planted bug's cause found exactly by the queries; the fixed build the negative control), <c>psync</c> (a forced desync
/// is found at its frame; without it in sync), <c>pstore</c> (kept profiles: the same answers as the stream, the size limit, the TTL, the cap,
/// the session's end, a crash-killed session's profile removed by the next sweep, nothing left behind), <c>pconsoles</c> (the SNES with its
/// SPC700 and the Game Boy profiled; the Sega consoles refused until T0, never an empty profile), <c>pmeasure</c> (not in the default list:
/// the cost and bytes a frame on the user's games).
/// </summary>
internal static partial class TimeflowTestCli
{
    private static JsonObject Q(JsonObject run, int i) => (JsonObject)((JsonArray)run["queries"]!)[i]!;
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string S(JsonNode? n) => n == null ? "null" : n is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : n.ToJsonString(Relaxed);

    private static (string Dir, string Mlb, Asm Asm, string Rom) PlantedTimeline(string dir, bool fixedBuild, long frames)
    {
        var (rom, asm, mlb) = TimeflowProfileRoms.Corrupting(fixedBuild);
        string name = fixedBuild ? "corrupt_fixed" : "corrupt";
        string romPath = DebugTestRoms.Write(dir, name + ".nes", rom);
        string mlbPath = Path.Combine(dir, name + ".mlb");
        File.WriteAllText(mlbPath, mlb);
        var (tdir, _) = Record(romPath, frames, null, "", full: false);
        return (tdir, mlbPath, asm, romPath);
    }

    /// <summary>The first frame after which RAM[addr] is no longer <paramref name="was"/> (a plain run, no debugger), and the value then.</summary>
    private static (long Frame, byte Value) FirstChange(string rom, ushort addr, byte was, long frames)
    {
        using var m = Open(rom);
        for (long f = 0; f < frames; f++)
        {
            m.Frame(PadButtons.None, PadButtons.None);
            if (f > 2 && m.Host.Nes!.PeekSystemRam(addr) != was) return (f, m.Host.Nes.PeekSystemRam(addr));
        }
        return (-1, 0);
    }

    // ------------------------------------------------------------------------------------------------ profile: the planted bug

    private static void ProfileBug(string dir)
    {
        var (tdir, mlb, asm, rom) = PlantedTimeline(dir, false, 300);
        var (bugFrame, bugValue) = FirstChange(rom, TimeflowProfileRoms.Hp, 5, 300);
        Check("profile: the planted game's hp is corrupted (a plain run, no debugger)", bugFrame > 0, $"hp 5 -> ${bugValue:X2} after frame {bugFrame}");
        uint store = asm["ft_store"];
        var req = new ProfileRequest
        {
            Timeline = tdir, From = 20, To = 60, Symbols = new() { mlb },
            Queries = new() { "writes hp", "last-write hp before 60", "became hp == 5", "became hp != 5", "histogram", "between fill done", "vars hp", "writes $0850", "writes idx limit 2", "summary", $"events {bugFrame}", "trace frame 30 count 4" },
        };
        var sw = Stopwatch.StartNew();
        var j = TimeflowProfiling.Run(req);
        Check("profile: the span re-ran in sync with the recording", j["inSync"]!.GetValue<bool>() && (long)j["framesProfiled"]! == 41, $"{j["framesProfiled"]} frames from keyframe {j["keyframe"]}, {S(j["instructions"])} instructions, {j["accesses"]} accesses, {S(j["cost"])} ({sw.Elapsed.TotalSeconds:0.0} s)");
        var w = Q(j, 0);
        var hit = (w["shown"] as JsonArray)?.FirstOrDefault() as JsonObject;
        Check("profile: 'writes hp' finds exactly one write: the corrupting STA in fill_table, its frame, PC and value", (long)w["total"]! == 1 && hit != null && S(hit["in"]) == "ft_store" && S(hit["pc"]) == $"${store:X4}" && (long)hit["frame"]! == bugFrame && S(hit["value"]) == $"${bugValue:X2}" && S(hit["instruction"]).StartsWith("STA"),
              $"total {w["total"]}; {S(hit)}");
        Check("profile: the write's call stack is fill_table called from update", hit != null && hit["stack"] is JsonArray st && st.Count >= 1 && S(st[0]).StartsWith("fill_table <- update"), S(hit?["stack"]));
        Check("profile: 'writes hp' by function names fill_table alone", S(w["byFunction"]) == "{\"fill_table\":1}", S(w["byFunction"]));
        var lw = (JsonObject)Q(j, 1)["last"]!;
        Check("profile: 'last-write hp before 60' is that write", lw != null && S(lw["in"]) == "ft_store" && (long)lw["frame"]! == bugFrame && S(lw["registers"]).Contains("X:20"), S(lw));
        var became = Q(j, 3);
        Check("profile: 'became hp != 5' names the corrupting instruction", became["made"] is JsonObject bm && S(bm["in"]) == "ft_store" && (long)bm["frame"]! == bugFrame && S(bm["old"]) == "$05", S(became));
        Check("profile: 'became hp == 5' says it already held and did not become so again", Q(j, 2)["made"] == null && S(Q(j, 2)["note"]).Contains("already"), S(Q(j, 2)["note"]));
        var hist = Q(j, 4);
        Check("profile: the histogram counts fill_table and update", S(hist["top"]).Contains("\"fill_table\"") && S(hist["top"]).Contains("\"update\""), $"{hist["instructions"]} instructions, {hist["functions"]} functions");
        var btw = Q(j, 5);
        Check("profile: 'between fill done' (two debug-port markers) is fill_table's run", (long)btw["instructions"]! > 0 && S(btw["functions"]).Contains("fill_table") && btw["toAt"] != null, $"{btw["instructions"]} instructions from #{btw["fromAt"]} to #{btw["toAt"]}: {S(btw["functions"])}");
        var vars = Q(j, 6);
        Check("profile: 'vars hp': one change, 5 -> the frame counter, by ft_store", (long)vars["changes"]! == 1 && S(vars["shown"]).Contains("\"$05\"") && S(vars["shown"]).Contains("ft_store"), S(vars["shown"]));
        Check("profile: a mirror ($0850) finds the same write", (long)Q(j, 7)["total"]! == 1 && S(Q(j, 7)["shown"]).Contains("ft_store"), $"total {Q(j, 7)["total"]}");
        Check("profile: 'writes idx' counts every frame's STX idx (41), shows the limit (2)", (long)Q(j, 8)["total"]! == 41 && ((JsonArray)Q(j, 8)["shown"]!).Count == 2, $"total {Q(j, 8)["total"]}, {S(Q(j, 8)["more"])}");
        Check("profile: the summary counts 41 frames", (long)Q(j, 9)["frames"]! == 41, S(Q(j, 9)["mainPerFrame"]));
        Check("profile: the event view of the bug's frame has its NMI and the debug port's register writes", S(Q(j, 10)["lines"]).Contains("interrupt NMI") && S(Q(j, 10)["lines"]).Contains("write $401D"), $"{Q(j, 10)["registerWrites"]} register writes, {Q(j, 10)["interrupts"]} interrupts");
        Check("profile: 'trace frame 30 count 4' gives 4 lines with Mesen's columns", Q(j, 11)["lines"] is JsonArray tl && tl.Count == 4 && S(tl[0]).Contains(" A:") && S(tl[0]).Contains("Fr:30"), S(Q(j, 11)["lines"]?[0]));
        // the call stack and the trace at the write's position (a second re-run: the positions are the same every time)
        long at = (long)hit!["at"]!;
        var j2 = TimeflowProfiling.Run(new ProfileRequest { Timeline = tdir, From = 20, To = 60, Symbols = new() { mlb }, Queries = new() { $"stack {at}", $"trace {at} before 3 after 1", "when [hp] != 5" } });
        var stk = Q(j2, 0);
        Check("profile: 'stack N' at the write: fill_table <- update, outermost NMI-less main loop (the same position in a second re-run)", S(stk["instruction"]?["in"]) == "ft_store" && S(stk["instruction"]?["stack"]).Contains("fill_table <- update"), S(stk["instruction"]?["stack"]));
        var tr = Q(j2, 1);
        Check("profile: 'trace N before 3 after 1' marks the STA", tr["lines"] is JsonArray tl2 && tl2.Count == 5 && S(tl2[3]).StartsWith("> ") && S(tl2[3]).Contains("STA"), S(tr["lines"]?[3]));
        var wh = Q(j2, 2);
        Check("profile: 'when [hp] != 5' (an expression on the running machine) is made true by the STA", S(wh["madeTrueBy"]?["in"]) == "ft_store", S(wh["madeTrueBy"]?["line"]));
        // the refusals
        Check("profile (negative): an unknown query is refused", Throws(() => ProfileQuery.Parse("explode hp"), out string m1) && m1.Contains("unknown query"), m1);
        var bad = TimeflowProfiling.Run(new ProfileRequest { Timeline = tdir, From = 20, To = 22, Symbols = new() { mlb }, Queries = new() { "writes no_such_symbol" } });
        Check("profile (negative): an unknown symbol is an error in the answer, never an empty list", Q(bad, 0)["error"] != null && Q(bad, 0)["total"] == null, S(Q(bad, 0)["error"]));
        Check("profile (negative): a span outside the timeline is refused", Throws(() => TimeflowProfiling.Run(new ProfileRequest { Timeline = tdir, From = 250, To = 400 }), out string m2) && m2.Contains("not all in timeline"), m2);
        // the negative control: the fixed build under the same queries
        var (fdir, fmlb, _, from) = PlantedTimeline(dir, true, 300);
        Check("profile (negative): the fixed build's hp is never changed (a plain run)", FirstChange(from, TimeflowProfileRoms.Hp, 5, 300).Frame < 0, "hp stays 5");
        var jf = TimeflowProfiling.Run(new ProfileRequest { Timeline = fdir, From = 20, To = 60, Symbols = new() { fmlb }, Queries = new() { "writes hp", "last-write hp before 60", "became hp != 5", "vars hp", "writes idx" } });
        Check("profile (negative): the fixed build: no write to hp, no last write, never became != 5, no change", (long)Q(jf, 0)["total"]! == 0 && Q(jf, 1)["last"] == null && Q(jf, 2)["made"] == null && (long)Q(jf, 3)["changes"]! == 0,
              $"writes {Q(jf, 0)["total"]}, last {S(Q(jf, 1)["last"])}, became {S(Q(jf, 2)["made"])}");
        Check("profile (negative): the fixed build still writes idx every frame (the queries see its writes)", (long)Q(jf, 4)["total"]! == 41, $"{Q(jf, 4)["total"]}");
    }

    // ------------------------------------------------------------------------------------------------ psync: a forced desync

    private static void ProfileSync(string dir)
    {
        var (tdir, mlb, _, _) = PlantedTimeline(dir, false, 200);
        var off = TimeflowProfiling.Run(new ProfileRequest
        {
            Timeline = tdir, From = 30, To = 120, Symbols = new() { mlb }, Queries = new() { "summary" },
            BeforeFrame = (m, f) => { if (f == 75) m.Host.Nes!.PokeSystemRam(TimeflowProfileRoms.Idx, 0x07); },
        });
        Check("psync: a RAM byte changed before frame 75 of the re-run is found at frame 75 (the profile is not the recorded game from there)", !off["inSync"]!.GetValue<bool>() && (long)off["offFrame"]! == 75 && S(off["offWhat"]).Contains("RAM"), $"off at {off["offFrame"]}: {off["offWhat"]}");
        Check("psync: the queries over an out-of-sync re-run carry the warning", S(Q(off, 0)["warning"]).Contains("out of sync at frame 75"), S(Q(off, 0)["warning"]));
        Check("psync: the re-run stopped at the off frame", (long)off["framesProfiled"]! == 75 - 30 + 1, $"{off["framesProfiled"]} frames profiled");
        var ok = TimeflowProfiling.Run(new ProfileRequest { Timeline = tdir, From = 30, To = 120, Symbols = new() { mlb }, Queries = new() { "summary" } });
        Check("psync (negative): the same re-run without the change stays in sync to the end", ok["inSync"]!.GetValue<bool>() && (long)ok["framesProfiled"]! == 91, $"{ok["framesProfiled"]} frames, in sync {ok["inSync"]}");
        // a keyframe's whole-state check: a change that leaves the work RAM alone (an APU register) is found at the next keyframe
        string why = "";
        var whole = TimeflowProfiling.Run(new ProfileRequest
        {
            Timeline = tdir, From = 30, To = 130, Queries = new() { "summary" },
            BeforeFrame = (m, f) => { if (f == 100) why = PokeUnseen(m); },
        });
        if (why.Length == 0) NotRun("psync: a change only the whole state sees", "the planted cartridge has no PRG RAM to change");
        else Check("psync: a change outside the work RAM and the picture (PRG RAM) is found at the next keyframe's whole-state check", !whole["inSync"]!.GetValue<bool>() && S(whole["offWhat"]).Contains("whole state") && (long)whole["offFrame"]! == 120,
                   $"{why}; off at {whole["offFrame"]}: {whole["offWhat"]}");
    }

    /// <summary>Changes the cartridge's PRG RAM ($6000): not in the work-RAM hash, never drawn: only a keyframe's whole state sees it.</summary>
    private static string PokeUnseen(MovieMachine m)
    {
        var d = m.Host.Domains().FirstOrDefault(x => x.Id == "PRGRAM" && x.Size >= 0x100);
        if (d == null) return "";
        d.Poke(0x10, (byte)(d.Peek(0x10) ^ 0xA5));
        return $"PRG RAM $6010 changed before frame 100";
    }

    // ------------------------------------------------------------------------------------------------ pstore: kept profiles

    private static void ProfileStoreGroup(string dir, string home)
    {
        var (tdir, mlb, _, _) = PlantedTimeline(dir, false, 300);
        string gameDir = TimeflowStore.GameDirOf(tdir);
        string pdir = ProfileStore.ProfilesDir(gameDir);
        var queries = new List<string> { "writes hp", "histogram", "vars", "events 34", "summary", "between fill done", "trace frame 40 count 6" };
        var streamed = TimeflowProfiling.Run(new ProfileRequest { Timeline = tdir, From = 20, To = 80, Symbols = new() { mlb }, Queries = queries });
        Check("pstore (negative): a streaming profile writes nothing", !Directory.Exists(pdir) || !Directory.EnumerateFileSystemEntries(pdir).Any(), streamed["stored"]!.ToString());
        long timelinesBytes = TimeflowStore.GameBytes(gameDir);
        var kept = TimeflowProfiling.Run(new ProfileRequest { Timeline = tdir, From = 20, To = 80, Symbols = new() { mlb }, Queries = queries, Store = true });
        var p = (JsonObject)kept["profile"]!;
        string kdir = S(p["dir"]);
        Check("pstore: a kept profile is in <game>\\profiles\\<id> with every frame", Directory.Exists(kdir) && kdir.StartsWith(pdir, StringComparison.OrdinalIgnoreCase) && S(p["status"]) == "complete" && (long)p["framesStored"]! == 61,
              $"{p["id"]}: {p["framesStored"]} frames, {p["bytes"]} bytes ({p["bytesPerFrame"]} a frame; raw {p["rawBytes"]}), expires {p["expires"]}");
        Check("pstore: profiles are outside the game's cap (the timelines' bytes unchanged)", TimeflowStore.GameBytes(gameDir) == timelinesBytes, $"{TimeflowStore.GameBytes(gameDir)} = {timelinesBytes}");
        var back = TimeflowProfiling.Query(S(p["id"]), queries, new());
        int same = 0; var diffs = new List<string>();
        for (int i = 0; i < queries.Count; i++)
        {
            string a = Q(streamed, i).ToJsonString(), b = Q(back, i).ToJsonString();
            if (a == b) same++; else diffs.Add($"{queries[i]}: stream {a[..Math.Min(160, a.Length)]} / kept {b[..Math.Min(160, b.Length)]}");
        }
        Check("pstore: the kept profile answers every query exactly as the stream did", same == queries.Count, $"{same} of {queries.Count} identical; read back in {back["queryMs"]} ms ({back["recordsPerSecond"]} records/s){string.Concat(diffs.Take(2).Select(d => "; " + d))}");
        var when = TimeflowProfiling.Query(S(p["id"]), new() { "when [hp] != 5" }, new());
        Check("pstore (negative): 'when' over a kept profile is refused (it needs the running machine), said", S(Q(when, 0)["error"]).Contains("re-run"), S(Q(when, 0)["error"]));
        // damage: a flipped byte in a frame's records is found by its checksum
        string recs = Path.Combine(kdir, "records.bin");
        var bytes = File.ReadAllBytes(recs);
        bytes[bytes.Length / 2] ^= 0x40;
        File.WriteAllBytes(recs, bytes);
        Check("pstore (negative): a damaged kept profile is refused with the frame, never answered", Throws(() => TimeflowProfiling.Query(S(p["id"]), new() { "summary" }, new()), out string dm) && dm.Contains("damaged"), dm);
        bytes[bytes.Length / 2] ^= 0x40;
        File.WriteAllBytes(recs, bytes);

        // the size limit
        var small = TimeflowProfiling.Run(new ProfileRequest { Timeline = tdir, From = 20, To = 80, Symbols = new() { mlb }, Queries = new() { "writes hp" }, Store = true, LimitBytes = 300 * 1024 });
        var sp = (JsonObject)small["profile"]!;
        Check("pstore: the size limit stops the store, says where, and keeps the files within it", S(sp["status"]) == "truncated" && S(sp["reason"]).Contains("size limit") && TimeflowStore.DirBytes(S(sp["dir"])) <= 300 * 1024 + 8192 && (long)sp["framesStored"]! is > 0 and < 61,
              $"{sp["framesStored"]} frames stored, {TimeflowStore.DirBytes(S(sp["dir"]))} bytes: {sp["reason"]}");
        Check("pstore: past the size limit the streaming queries still saw every frame", (long)Q(small, 0)["total"]! == 2, $"writes hp total {Q(small, 0)["total"]}");
        var smallBack = TimeflowProfiling.Query(S(sp["id"]), new() { "summary" }, new());
        Check("pstore: a truncated profile answers over the frames it holds", (long)Q(smallBack, 0)["frames"]! == (long)sp["framesStored"]!, $"{Q(smallBack, 0)["frames"]} frames");

        // the time to live
        var meta = ProfileMeta.Load(S(sp["dir"]));
        meta.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
        meta.Save(S(sp["dir"]));
        var swept = ProfileStore.Sweep(gameDir);
        Check("pstore: an expired profile is deleted by the next sweep, with the reason", !Directory.Exists(S(sp["dir"])) && swept.Any(s => s.Id == S(sp["id"]) && s.Why.Contains("expired")), string.Join("; ", swept.Select(s => $"{s.Id}: {s.Why}")));
        Check("pstore (negative): an unexpired profile of a live session stays", Directory.Exists(kdir), kdir);

        // the folder's cap: the least recently used unkept one goes first, a kept one stays
        var keptOne = TimeflowProfiling.Run(new ProfileRequest { Timeline = tdir, From = 40, To = 60, Queries = new(), Store = true, Keep = true });
        string keptDir = S(keptOne["profile"]!["dir"]);
        long capNow = TimeflowStore.DirBytes(pdir) + 1024;
        TimeflowStore.SetCap(gameDir, TimeflowStore.DefaultCapBytes);
        File.WriteAllText(Path.Combine(gameDir, "timeflow.json"), JsonSerializer.Serialize(new { capBytes = TimeflowStore.DefaultCapBytes, profilesCapBytes = capNow }));
        var third = TimeflowProfiling.Run(new ProfileRequest { Timeline = tdir, From = 20, To = 70, Queries = new(), Store = true });
        string thirdDir = S(third["profile"]!["dir"]);
        var after = ProfileStore.Sweep(gameDir);
        Check("pstore: over the profiles cap, the oldest unkept profile retires; the kept one and the newest stay", !Directory.Exists(kdir) && Directory.Exists(keptDir) && Directory.Exists(thirdDir) && TimeflowStore.DirBytes(pdir) <= capNow + 64 * 1024,
              $"cap {capNow}, now {TimeflowStore.DirBytes(pdir)}; first {(Directory.Exists(kdir) ? "still there" : "retired")}, kept {(Directory.Exists(keptDir) ? "stays" : "GONE")}");
        File.Delete(Path.Combine(gameDir, "timeflow.json"));

        // the session's end: this process's unkept profiles go, the kept one stays (and is still deleted at its TTL)
        int ended = ProfileStore.EndSession();
        Check("pstore: the session's end deletes this session's unkept profiles, keeps the kept one", !Directory.Exists(thirdDir) && Directory.Exists(keptDir) && ended >= 1, $"{ended} deleted at the session's end; kept {(Directory.Exists(keptDir) ? "stays" : "GONE")}");
        var km = ProfileMeta.Load(keptDir); km.ExpiresUtc = DateTime.UtcNow.AddSeconds(-1); km.Save(keptDir);
        ProfileStore.Sweep(gameDir);
        Check("pstore: a kept profile is still deleted when its time to live passes", !Directory.Exists(keptDir), keptDir);

        // another process: a normal end leaves nothing; a crash-killed one's profile is removed by the next sweep (never while it lives)
        string exe = Environment.ProcessPath ?? "";
        if (!exe.EndsWith("BrokenNes.Workshop.exe", StringComparison.OrdinalIgnoreCase)) { NotRun("pstore: other sessions", $"the test runs in {exe}, not the Workshop exe"); return; }
        int before = Directory.Exists(pdir) ? Directory.GetDirectories(pdir).Length : 0;
        var (code, outText) = Child(exe, home, $"--timeflow profile --timeline \"{tdir}\" --from 20 --to 60 --store --json", hold: false, out _);
        int afterNormal = Directory.Exists(pdir) ? Directory.GetDirectories(pdir).Length : 0;
        Check("pstore: a Workshop session that kept a profile and ended normally left nothing behind", code == 0 && afterNormal == before && outText.Contains("\"framesStored\": 41"), $"exit {code}, {afterNormal - before} profile folder(s) left");
        var (code2, out2) = Child(exe, home, $"--timeflow profile --timeline \"{tdir}\" --from 20 --to 60 --store --json --hold-ms 120000", hold: true, out string heldDir);
        Check("pstore: a session killed while it held a profile (a crash) leaves its folder", heldDir.Length > 0 && Directory.Exists(heldDir), $"child killed (exit {code2}); {heldDir}");
        // negative control first: while a live process owns a profile, a sweep leaves it
        if (heldDir.Length > 0)
        {
            var swept2 = ProfileStore.Sweep(gameDir);
            Check("pstore: the next sweep removes the crashed session's profile, saying why", !Directory.Exists(heldDir) && swept2.Any(s => s.Why.Contains("is gone")), string.Join("; ", swept2.Select(s => $"{s.Id}: {s.Why}")));
        }
        var (_, _, liveDir, liveProc) = ChildHeld(exe, home, $"--timeflow profile --timeline \"{tdir}\" --from 20 --to 40 --store --json --hold-ms 120000");
        if (liveProc != null)
        {
            var swept3 = ProfileStore.Sweep(gameDir);
            Check("pstore (negative): a sweep never removes the profile of a session that still runs", Directory.Exists(liveDir) && swept3.All(s => !liveDir.EndsWith(s.Id)), liveDir);
            try { liveProc.Kill(); liveProc.WaitForExit(10_000); } catch (InvalidOperationException) { }
            liveProc.Dispose();
            ProfileStore.Sweep(gameDir);
        }
        // nothing left
        ProfileStore.EndSession();
        ProfileStore.Sweep(gameDir);
        int left = Directory.Exists(pdir) ? Directory.GetDirectories(pdir).Length : 0;
        Check("pstore: nothing is left behind (no profile folder, the profiles folder itself removed when empty)", left == 0 && !Directory.Exists(pdir), $"{left} left; {(Directory.Exists(pdir) ? "profiles\\ still there" : "profiles\\ gone")}");
    }

    private static (int Code, string Out) Child(string exe, string home, string args, bool hold, out string heldDir)
    {
        heldDir = "";
        if (hold)
        {
            var (code, text, dir, p) = ChildHeld(exe, home, args);
            heldDir = dir;
            if (p != null) { try { p.Kill(); p.WaitForExit(10_000); code = p.ExitCode; } catch (InvalidOperationException) { } p.Dispose(); }
            return (code, text);
        }
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["BROKENDEV_HOME"] = home;
        using var proc = Process.Start(psi)!;
        string o = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);
        return (proc.ExitCode, o);
    }

    /// <summary>Starts a Workshop child that profiles and then holds (its profile exists); returns once it says HOLDING.</summary>
    private static (int Code, string Out, string Dir, Process? Proc) ChildHeld(string exe, string home, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["BROKENDEV_HOME"] = home;
        var proc = Process.Start(psi)!;
        var sb = new System.Text.StringBuilder();
        string dir = "";
        var end = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < end)
        {
            string? line = proc.StandardOutput.ReadLine();
            if (line == null) break;
            sb.AppendLine(line);
            if (line.StartsWith("HOLDING "))
            {
                var parts = line.Split(' ', 3);
                dir = parts.Length == 3 ? parts[2].Trim() : "";
                return (0, sb.ToString(), dir, proc);
            }
        }
        proc.WaitForExit(5000);
        return (proc.HasExited ? proc.ExitCode : -1, sb.ToString(), dir, null);
    }

    // ------------------------------------------------------------------------------------------------ pconsoles

    private static void ProfileConsoles(string dir, string snes, string gb)
    {
        foreach (var (name, rom, console) in new[] { ("SNES test ROM", snes, (string?)null), ("Game Boy test ROM", gb, null) })
        {
            var (tdir, _) = Record(rom, 240, console, "auto:2", full: false);
            var j = TimeflowProfiling.Run(new ProfileRequest { Timeline = tdir, From = 100, To = 130, Queries = new() { "summary", "histogram", "events 110 limit 20", "writes $0000-$FFFF limit 1" }, Store = true });
            var ins = (JsonObject)j["instructions"]!;
            bool everyCpu = ins.All(kv => kv.Value!.GetValue<long>() > 0);
            Check($"pconsoles: {name}: profiled in sync, every debuggable CPU's instructions ({string.Join(", ", ins.Select(kv => kv.Key))})", j["inSync"]!.GetValue<bool>() && everyCpu && (long)j["accesses"]! > 0,
                  $"{S(ins)} instructions, {j["accesses"]} accesses, {S(j["cost"])}, {j["profile"]!["bytesPerFrame"]} bytes a stored frame");
            Check($"pconsoles: {name}: the summary and the histogram answer", (long)Q(j, 0)["frames"]! == 31 && (long)Q(j, 1)["instructions"]! > 0, $"{Q(j, 1)["functions"]} functions");
            ProfileStore.Delete(S(j["profile"]!["dir"]));
        }
        // the Sega consoles: no debugger until T0, so no profile (refused with the reason, never an empty one)
        var sms = new byte[0x8000];
        sms[0] = 0xF3; sms[1] = 0xED; sms[2] = 0x56; sms[0x7FE0] = 0xC3; sms[0x7FE1] = 0x03; sms[0x7FE2] = 0x00;
        System.Text.Encoding.ASCII.GetBytes("TMR SEGA").CopyTo(sms, 0x7FF0); sms[0x7FFF] = 0x4C;
        string smsPath = DebugTestRoms.Write(dir, "idle.sms", sms);
        var (sdir, _) = Record(smsPath, 120, "sms", "", full: false);
        bool refused = Throws(() => TimeflowProfiling.Run(new ProfileRequest { Timeline = sdir, From = 10, To = 20, Queries = new() { "summary" } }), out string why);
        Check("pconsoles: a Master System timeline is refused for profiling until T0 (said, never an empty profile)", refused && why.Contains("T0"), why);
        string pd = ProfileStore.ProfilesDir(TimeflowStore.GameDirOf(sdir));
        Check("pconsoles (negative): the refusal wrote nothing", !Directory.Exists(pd), pd);
    }

    // ------------------------------------------------------------------------------------------------ pmeasure (not in the default list)

    private static void ProfileMeasure(string dir, string nes, string snes, string gb, int frames)
    {
        string real = @"P:\BrokenDev\env";
        var roms = new List<(string Name, string? Path, string? Console)>
        {
            ("NES planted game", nes, null), ("SNES test ROM", snes, null), ("Game Boy test ROM", gb, null),
            ("VRUN (NES)", Path.Combine(real, "out", "vrun-nes", "vrun_game.nes"), "nes"),
            ("Super VRUN (SNES)", Path.Combine(real, "out", "supervrun", "supervrun.sfc"), "snes"),
            ("Super Mario World (SNES)", @"X:\EMULATION\Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc", "snes"),
            ("WideMess sample", FindUp("WideMess", "testroms", "bin", "devkit_sample.wms"), "wms"),
            ("Vaults of Obsolete Code (GBC)", Path.Combine(real, "out", "vrun-voc", "vrun_voc.gbc"), "gbc"),
            ("Link's Awakening DX (GBC)", @"X:\EMULATION\Nintendo Game Boy Color\Legend of Zelda, The - Link's Awakening DX (USA, Europe) (Rev 2) (SGB Enhanced) (GB Compatible).gbc", "gbc"),
        };
        foreach (var (name, path, console) in roms)
        {
            if (path == null || !File.Exists(path)) { NotRun($"pmeasure: {name}", $"not on this machine ({path})"); continue; }
            var syms = new[] { ".mlb", ".sym", ".noi" }.Select(e => Path.ChangeExtension(path, e)).Where(File.Exists).ToList();
            try
            {
                var j = TimeflowProfiling.MeasureOne(path, console, frames, 300, "auto:1", syms);
                Check($"pmeasure: {name}", j["inSync"]!.GetValue<bool>(),
                      $"real {j["realFps"]} fps; plain replay {j["plainReplayFps"]} fps; profiled streaming {j["streamingFps"]} fps = {j["streamingTimesSlower"]}x slower than real time; stored {j["storedFps"]} fps ({j["storedTimesSlower"]}x); " +
                      $"{j["instructionsPerFrame"]} instructions + {j["accessesPerFrame"]} accesses a frame; {j["storedBytesPerFrame"]} bytes a stored frame (raw {j["rawBytesPerFrame"]}); stored query {j["storedQueryMs"]} ms ({j["storedRecordsPerSecond"]} records/s){(syms.Count > 0 ? "; symbols " + string.Join(", ", syms.Select(Path.GetFileName)) : "")}");
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or ArgumentException) { Check($"pmeasure: {name}", false, e.Message); }
        }
    }
}
