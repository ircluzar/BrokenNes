using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>
/// <c>--timeflow measure</c>: T1's measurements for one game, from which the keyframe interval N, the page size and the compression are chosen
/// (docs/projects/brokendev-timeflow-plan.md, "T1 as built"): the state's size and what taking it costs, the frames per second off and
/// recording (the real recorder, interleaved runs), and the bytes a frame for every page size, keyframe interval and compression on the same
/// frames, with what restoring a frame costs.
/// </summary>
internal static class TimeflowMeasure
{
    public static int Run(Func<string, string, string> Opt, Func<string, List<string>> All, Func<string, bool> Has)
    {
        string rom = Opt("rom", "");
        if (rom.Length == 0) { Console.Error.WriteLine("measure needs --rom"); return 2; }
        int warm = int.Parse(Opt("warm", "600"), CultureInfo.InvariantCulture), frames = int.Parse(Opt("frames", "600"), CultureInfo.InvariantCulture);
        int runs = int.Parse(Opt("runs", "3"), CultureInfo.InvariantCulture);
        string console = Opt("console", ""), cores = Opt("cores", "");
        string label = Opt("label", Path.GetFileName(rom));
        var input = TimeflowInput.Parse(Opt("input", "auto:1"), Opt("input2", ""), Opt("movie", "") is { Length: > 0 } mp ? mp : null);
        MovieMachine Open() => MovieMachine.Open(rom, console.Length > 0 ? console : null, cores, "fceux", false, false);

        // 1. the raw states of `frames` frames after `warm` frames of play (kept in memory), and what taking them costs
        var states = new List<TimeflowState>(frames);
        double captureMs = 0, worstCapture = 0, ownMs = 0;
        string consoleKey, coresRun;
        using (var m = Open())
        {
            consoleKey = m.ConsoleKey; coresRun = m.Cores;
            var src = TimeflowMachines.SourceFor(m);
            for (int f = 0; f < warm; f++) { var (a, b) = input.At(f); m.Frame(a, b); }
            for (int f = 0; f < frames; f++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var st = src.Capture();
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                captureMs += ms; worstCapture = Math.Max(worstCapture, ms);
                states.Add(st);
                var (a, b) = input.At(warm + f); m.Frame(a, b);
            }
            // the machine's own state alone (the NES's JSON text; a session's payload): the part of the capture Timeflow cannot make cheaper
            var ow = Stopwatch.StartNew();
            for (int k = 0; k < 60; k++) { if (m.Host.Nes != null) _ = m.Host.Nes.SaveState(); else _ = m.Host.Session!.SaveStatePayload(); }
            ownMs = ow.Elapsed.TotalMilliseconds / 60;
        }
        var first = states[0];
        Console.WriteLine($"{label}: {consoleKey} {coresRun}, state {first.Bytes} bytes (skeleton {first.Skeleton.Length}, {first.Sections.Length} section(s): {string.Join(", ", first.Names.Zip(first.Sections, (n, s) => $"{n} {s.Length}").Take(8))}{(first.Sections.Length > 8 ? ", ..." : "")}), taking it {captureMs / frames:0.000} ms a frame (worst {worstCapture:0.00}; the machine's own state alone {ownMs:0.000} ms)");
        int layoutChanges = 0; for (int i = 1; i < states.Count; i++) if (!states[i].SameLayout(states[i - 1])) layoutChanges++;

        // 2. the bytes a frame: every page size and keyframe interval with Brotli 1, then the compressions at N 60, page 256
        var grid = new JsonArray();
        int[] pages = { 64, 128, 256, 512, 1024, 4096 };
        int[] keys = { 30, 60, 120, 300 };
        Console.WriteLine($"  bytes a frame (Brotli 1; encode + compress ms a frame; restore ms for a frame in the middle of a block)");
        Console.WriteLine($"  {"N \\ page",-9}" + string.Concat(pages.Select(p => $"{p,22}")));
        foreach (int n in keys)
        {
            string line = $"  {n,-9}";
            foreach (int p in pages)
            {
                var r = Encode(states, n, p, TimelineCompression.Brotli, 1);
                grid.Add(new JsonObject { ["keyEvery"] = n, ["page"] = p, ["codec"] = "brotli:1", ["bytesPerFrame"] = Math.Round(r.Bytes, 1), ["rawPerFrame"] = Math.Round(r.Raw, 1), ["encodeMs"] = Math.Round(r.EncodeMs, 3), ["restoreMs"] = Math.Round(r.RestoreMs, 2) });
                line += $"{r.Bytes,10:0} ({r.EncodeMs,4:0.00}|{r.RestoreMs,5:0.0})";
            }
            Console.WriteLine(line);
        }
        Console.WriteLine($"  the same with Brotli 5:");
        foreach (int n in new[] { 30, 60, 120 })
        {
            string line = $"  {n,-9}";
            foreach (int p in pages)
            {
                var r = Encode(states, n, p, TimelineCompression.Brotli, 5);
                grid.Add(new JsonObject { ["keyEvery"] = n, ["page"] = p, ["codec"] = "brotli:5", ["bytesPerFrame"] = Math.Round(r.Bytes, 1), ["rawPerFrame"] = Math.Round(r.Raw, 1), ["encodeMs"] = Math.Round(r.EncodeMs, 3), ["restoreMs"] = Math.Round(r.RestoreMs, 2) });
                line += $"{r.Bytes,10:0} ({r.EncodeMs,4:0.00}|{r.RestoreMs,5:0.0})";
            }
            Console.WriteLine(line);
        }
        var codecs = new JsonArray();
        Console.WriteLine("  compressions at N 60, page 256:");
        foreach (var (how, lvl, name) in new[] { (TimelineCompression.None, 0, "none"), (TimelineCompression.Deflate, 0, "deflate:fastest"), (TimelineCompression.Deflate, 1, "deflate:optimal"),
                                                  (TimelineCompression.Brotli, 0, "brotli:0"), (TimelineCompression.Brotli, 1, "brotli:1"), (TimelineCompression.Brotli, 3, "brotli:3"), (TimelineCompression.Brotli, 5, "brotli:5") })
        {
            var r = Encode(states, 60, 256, how, lvl);
            codecs.Add(new JsonObject { ["codec"] = name, ["bytesPerFrame"] = Math.Round(r.Bytes, 1), ["encodeMs"] = Math.Round(r.EncodeMs, 3), ["restoreMs"] = Math.Round(r.RestoreMs, 2) });
            Console.WriteLine($"    {name,-16} {r.Bytes,10:0} bytes a frame, {r.EncodeMs:0.000} ms a frame to encode, {r.RestoreMs:0.00} ms to restore");
        }

        // 3. the frame rate: off against recording (the real recorder writing to a scratch game folder), interleaved, best of `runs`
        string scratch = Path.Combine(Path.GetTempPath(), "brokennes-timeflow-measure-" + Environment.ProcessId);
        double bestOff = 0, bestOn = 0; double onBytes = 0; double onCapture = 0, onWorst = 0; string recCodec = "";
        try
        {
            for (int run = 0; run < runs; run++)
            {
                foreach (bool on in new[] { false, true })
                {
                    using var m = Open();
                    for (int f = 0; f < warm; f++) { var (a, b) = input.At(f); m.Frame(a, b); }
                    TimeflowRecorder? rec = null;
                    if (on)
                    {
                        var o = TimeflowCli.ParseCodec(new TimeflowOptions { Rom = m.RomFile, RomName = m.RomName, Cores = m.Cores, Source = "measure", GameDir = Path.Combine(scratch, "run" + run) }, Opt("codec", ""));
                        if (int.TryParse(Opt("key-every", ""), out int ke)) o.KeyEvery = ke;
                        if (int.TryParse(Opt("page", ""), out int pg)) o.PageSize = pg;
                        recCodec = $"N {o.KeyEvery}, page {o.PageSize}, {o.Compression.ToString().ToLowerInvariant()} {o.Level}";
                        rec = TimeflowRecorder.Start(TimeflowMachines.SourceFor(m), o);
                    }
                    var sw = Stopwatch.StartNew();
                    for (int f = 0; f < frames; f++)
                    {
                        var (a, b) = input.At(warm + f);
                        rec?.BeforeFrame(a, b);
                        m.Frame(a, b);
                        rec?.AfterFrame();
                    }
                    double fps = frames / sw.Elapsed.TotalSeconds;
                    if (rec != null)
                    {
                        string d = rec.Stop();
                        if (rec.StopReason != null) Console.WriteLine($"  the recording stopped: {rec.StopReason}");
                        onBytes = TimeflowStore.DirBytes(d) / (double)Math.Max(1, rec.FramesWritten);
                        onCapture = rec.CaptureMsAverage; onWorst = Math.Max(onWorst, rec.CaptureMsMax);
                        bestOn = Math.Max(bestOn, fps);
                    }
                    else bestOff = Math.Max(bestOff, fps);
                }
            }
        }
        finally { try { Directory.Delete(scratch, true); } catch (IOException) { } }
        Console.WriteLine($"  frame rate: {bestOff:0} fps off, {bestOn:0} fps recording ({(bestOff > 0 ? 100.0 * (bestOff - bestOn) / bestOff : 0):0.0}% slower; the emulation thread spends {onCapture:0.000} ms a frame on the recording, worst {onWorst:0.00}); {onBytes:0} bytes a frame on disk (the real files, {recCodec})");
        var j = new JsonObject
        {
            ["label"] = label, ["rom"] = Path.GetFileName(rom), ["console"] = consoleKey, ["cores"] = coresRun, ["warm"] = warm, ["frames"] = frames,
            ["stateBytes"] = first.Bytes, ["skeletonBytes"] = first.Skeleton.Length, ["sections"] = new JsonArray(first.Names.Zip(first.Sections, (n, s) => (JsonNode)$"{n} {s.Length}").ToArray()),
            ["layoutChanges"] = layoutChanges, ["captureMs"] = Math.Round(captureMs / frames, 3), ["captureWorstMs"] = Math.Round(worstCapture, 2), ["ownStateMs"] = Math.Round(ownMs, 3),
            ["fpsOff"] = Math.Round(bestOff, 1), ["fpsRecording"] = Math.Round(bestOn, 1), ["recordingMsPerFrame"] = Math.Round(onCapture, 3), ["recordingWorstMs"] = Math.Round(onWorst, 2),
            ["diskBytesPerFrame"] = Math.Round(onBytes, 1), ["recordingCodec"] = recCodec, ["grid"] = grid, ["codecs"] = codecs,
        };
        if (Opt("json", "") is { Length: > 0 } outJson) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outJson))!); File.WriteAllText(outJson, j.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); }
        return 0;
    }

    private sealed record EncodeResult(double Bytes, double Raw, double EncodeMs, double RestoreMs);

    /// <summary>The frames encoded as the writer does (blocks of N, page deltas, the compression) without files: bytes and time a frame, and the cost of restoring frames in the middle of blocks.</summary>
    private static EncodeResult Encode(List<TimeflowState> states, int n, int page, TimelineCompression how, int level)
    {
        long packedTotal = 0, rawTotal = 0;
        var sw = Stopwatch.StartNew();
        var blocks = new List<(byte[] Packed, int Raw, int Count)>();
        var ms = new MemoryStream(1 << 20);
        var scratch = new byte[page];
        TimeflowState? prev = null;
        int inBlock = 0;
        void Flush()
        {
            if (inBlock == 0) return;
            var raw = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            var packed = TimelineCodec.Compress(raw, how, level);
            blocks.Add((packed, raw.Length, inBlock));
            packedTotal += packed.Length; rawTotal += raw.Length;
            ms.SetLength(0); inBlock = 0;
        }
        foreach (var st in states)
        {
            if (inBlock == 0 || prev == null || !st.SameLayout(prev)) TimelineCodec.WriteKey(ms, st);
            else TimelineCodec.WriteDelta(ms, st, prev, page, scratch);
            prev = st; inBlock++;
            if (inBlock >= n) Flush();
        }
        Flush();
        double encode = sw.Elapsed.TotalMilliseconds / states.Count;
        // restoring: a frame half-way into each of a few blocks (decompress the block, apply the deltas up to it)
        var rsw = Stopwatch.StartNew();
        int restored = 0;
        foreach (var (packed, raw, count) in blocks.Where(b => b.Count == n).Take(4))
        {
            var bytes = TimelineCodec.Decompress(packed, raw, how);
            var st = new TimeflowState(); int at = 0;
            for (int i = 0; i <= count / 2; i++) TimelineCodec.Read(bytes, ref at, st, page);
            restored++;
        }
        double restore = restored > 0 ? rsw.Elapsed.TotalMilliseconds / restored : 0;
        return new EncodeResult(packedTotal / (double)states.Count, rawTotal / (double)states.Count, encode, restore);
    }
}
