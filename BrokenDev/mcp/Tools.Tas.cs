using System.Text.Json.Nodes;
using BrokenNes.Workshop.Tas;

namespace BrokenNes.Mcp;

/// <summary>
/// TAS workline T1 (docs/projects/tas-format.md): run a TAS chapter / run file, and branch from a frame of one. Thin calls onto the Workshop
/// runner (Workshop/Tas): the same code as `Workshop --tas run|branch`, in this process, headless, deterministic, NES and N64 (others: T7).
/// </summary>
internal static partial class Tools
{
    private static void TasTools(Action<string, string, JsonObject, Func<Args, ToolOut>, bool> add)
    {
        add("tas_run",
            "Runs a TAS (docs/projects/tas-format.md): a run file (*.tasrun.json: chapters with start power-on | previous | state:FILE, script, goal, expect) or a bare script " +
            "(.tas/.txt in the N64-compat script language, or an exact per-frame .tlog). Writes checkpoints (video/audio/RAM hashes, blank check, PNGs, end states) to out " +
            "and returns the report: per chapter frames + goal, checkpoints, determinism (twice: run again in a fresh machine and compare), expect (the run file's expected checkpoints). " +
            "NES and N64. Heavy on N64 (minutes): prefer the fleet for long N64 runs.",
            Schema(("path", P("string", "the run file or script"), true),
                   ("rom", P("string", "the ROM (default: the run file's rom / BROKENNES_TAS_ROM)"), false),
                   ("console", P("string", "a bare script's console: nes, n64 (default: from the ROM)"), false),
                   ("start", P("string", "a bare script's start: power-on (default) or state:FILE"), false),
                   ("out", P("string", "the results folder (default: a new folder under BROKENNES_TAS_OUT or temp)"), false),
                   ("twice", P("boolean", "the determinism check (default true)"), false),
                   ("only", P("string", "stop after this chapter"), false),
                   ("bless", P("boolean", "write this run's checkpoints into the run file's expect blocks (default false)"), false),
                   ("pictures", P("boolean", "return the last checkpoint's picture (default true)"), false)),
            a => Wrap(() =>
            {
                var run = TasCli.LoadRun(a.Req("path"), a.Str("console") ?? "", a.Str("rom") ?? "", a.Str("start") ?? "power-on");
                var o = new TasRunOptions { Twice = a.Bool("twice", true), Only = a.Str("only"), OutDir = Path.GetFullPath(a.Str("out") ?? Path.Combine(Path.GetTempPath(), "brokennes-tas", run.Chapters[0].Id + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"))) };
                var rep = TasRunner.Run(run, o);
                if (a.Bool("bless", false)) TasRunner.Bless(run, rep);
                rep.Remove("log"); rep["out"] = o.OutDir;
                var r = new ToolOut(rep);
                if (a.Bool("pictures", true) && rep["checkpoints"]!.AsArray().LastOrDefault(c => c!["file"] != null)?["file"] is JsonNode f && File.Exists(Path.Combine(o.OutDir, (string)f!)))
                    r.Pngs.Add(File.ReadAllBytes(Path.Combine(o.OutDir, (string)f!)));
                return r;
            }), true);

        add("tas_branch",
            "Branches a TAS at a frame: runs the run file (or script) up to frame N of a chapter, saves the state there, and writes out/branch.tasrun.json, a run of one chapter " +
            "that starts from that state and plays either your new script (tail: script lines) or, without tail, the rest of the original chapter as an exact per-frame log. " +
            "Then tas_run the branch file to see where it leads. Returns the branch file, the state and the picture hash at the branch point.",
            Schema(("path", P("string", "the run file or script"), true),
                   ("chapter", P("string", "the chapter id (a bare script: its file name without extension)"), true),
                   ("frame", P("integer", "frames into the chapter"), true),
                   ("out", P("string", "the branch folder"), true),
                   ("tail", P("string[]", "script lines to play from the branch point (default: the rest of the original)"), false),
                   ("rom", P("string", "the ROM"), false), ("console", P("string", "a bare script's console"), false)),
            a => Wrap(() =>
            {
                var run = TasCli.LoadRun(a.Req("path"), a.Str("console") ?? "", a.Str("rom") ?? "", "power-on");
                var tail = a.Has("tail") ? a.List("tail").ToArray() : null;
                return new ToolOut(TasCli.Branch(run, a.Req("chapter"), (int)(a.Long("frame") ?? 0), a.Req("out"), tail, new TasRunOptions()));
            }), false);
    }
}
