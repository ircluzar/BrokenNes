using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrokenNes.Mcp;

/// <summary>
/// <c>BrokenNes.Mcp.exe --agentloop-test --rom &lt;file&gt; [--rom ...] [--ref] [--steps N] [--out dir]</c>: the agent play loop's test (Great
/// Compatibility Catalog P0). For each ROM, in this process through the same tool handlers MCP calls: open_rom (checks on, for the lag),
/// optionally the reference (open + align), N play_step calls with a scripted "agent" (Start, then right / jump), round-trip time per step,
/// record start / mark / stop, then the movie replayed in a new session (open_movie) must be in sync and carry the marks. One JSON line per ROM.
/// </summary>
internal static class AgentLoopTest
{
    public static int Run(string[] args)
    {
        var roms = new List<string>();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--rom") roms.Add(args[i + 1]);
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        bool withRef = args.Contains("--ref");
        int steps = int.Parse(Opt("steps", "60"));
        string outDir = Path.GetFullPath(Opt("out", Path.Combine(Path.GetTempPath(), "bn-agentloop-test")));
        Directory.CreateDirectory(outDir);
        var tools = Tools.All().ToDictionary(t => t.Name);
        ToolOut Call(string name, object a) => tools[name].Run(Args.From(a));
        int failures = 0;
        foreach (var rom in roms)
        {
            var row = new JsonObject { ["rom"] = Path.GetFileName(rom) };
            string? id = null;
            try
            {
                var open = Call("open_rom", new { path = rom, checks = !args.Contains("--nochecks") });
                id = (string)open.Json["session"]!;
                row["console"] = open.Json["console"]?.DeepClone();
                if (withRef)
                {
                    var r = Call("reference", new { session = id, action = "open", align_frames = 240, align_search = 40 });
                    row["refOpen"] = r.Json["ms"]?.DeepClone();
                    row["align"] = r.Json["align"]?.DeepClone();
                }
                Call("record", new { session = id, action = "start", text = "agent loop test" });
                var ms = new List<long>();
                var scores = new List<double>();
                long lag = 0;
                int pngBytes = 0;
                var rng = new Random(7);
                for (int s = 0; s < steps; s++)
                {
                    // a crude agent: Start every 8th step for the first 40, then mostly right with jumps
                    string[] b = s < 40 ? (s % 8 == 4 ? new[] { "Start" } : Array.Empty<string>())
                                       : (rng.Next(3) == 0 ? new[] { "Right", "A" } : new[] { "Right" });
                    var sw = Stopwatch.StartNew();
                    var o = Call("play_step", new { session = id, buttons = b, frames = 20, scale = 0.5, ram = new[] { "f=frame" } });
                    ms.Add(sw.ElapsedMilliseconds);
                    pngBytes += o.Pngs.Sum(p => p.Length);
                    lag += (long?)o.Json["lagFrames"] ?? 0;
                    if (o.Json["reference"]?["diff"]?["score"] is JsonNode sc) scores.Add((double)sc);
                    if (s == steps / 2) Call("record", new { session = id, action = "mark", text = "halfway", state = "half" });
                    if (s == steps - 1)
                    {
                        File.WriteAllBytes(Path.Combine(outDir, Path.GetFileNameWithoutExtension(rom) + "-bn.png"), o.Pngs[0]);
                        if (withRef)
                        {
                            var c = Call("reference", new { session = id, action = "compare", scale = 1 });
                            File.WriteAllBytes(Path.Combine(outDir, Path.GetFileNameWithoutExtension(rom) + "-ref.png"), c.Pngs[1]);
                            row["finalDiff"] = c.Json["diff"]?.DeepClone();
                        }
                    }
                }
                ms.Sort();
                row["steps"] = steps;
                row["stepMsMedian"] = ms[ms.Count / 2];
                row["stepMsMax"] = ms[^1];
                row["pngBytesPerStep"] = pngBytes / Math.Max(1, steps);
                row["lagFrames"] = lag;
                if (scores.Count > 0)
                {
                    row["refScoreMedian"] = scores.OrderBy(x => x).ElementAt(scores.Count / 2);
                    row["refScoreMax"] = scores.Max();
                    row["refSameSteps"] = scores.Count(x => x <= 0.02);
                    row["refCloseSteps"] = scores.Count(x => x <= 0.12);
                }
                string movie = Path.Combine(outDir, Path.GetFileNameWithoutExtension(rom) + ".bnm");
                var saved = Call("record", new { session = id, action = "stop", path = movie });
                row["movieFrames"] = saved.Json["frames"]?.DeepClone();
                var m = NesEmulator.Debugging.Movies.Movie.Load(movie);
                row["movieMarks"] = m.Marks.Count;
                var play = Call("open_movie", new { movie, rom });
                row["replayInSync"] = play.Json["inSync"]?.DeepClone();
                row["replayKeyframes"] = $"{play.Json["keyframesMatched"]}/{play.Json["keyframesChecked"]}";
                Call("close_session", new { session = (string)play.Json["session"]! });
                bool ok = (bool?)play.Json["inSync"] == true && m.Marks.Count >= 3;
                row["ok"] = ok;
                if (!ok) failures++;
            }
            catch (Exception e)
            {
                row["ok"] = false; row["error"] = e.Message; failures++;
            }
            finally
            {
                if (id != null) try { Call("close_session", new { session = id }); } catch { }
            }
            Console.WriteLine(row.ToJsonString());
        }
        return failures == 0 ? 0 : 1;
    }
}
