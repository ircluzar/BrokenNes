using System.Text.Json.Nodes;

namespace BrokenNes.Mcp;

/// <summary>
/// The agent play loop's tools (Great Compatibility Catalog P0, scope items 4 and 5): play_step, record, reference. The agents' manual is
/// docs/projects/compat-agent-playbook.md.
/// </summary>
internal static partial class Tools
{
    private static HeadlessSession Headless(Session s) => s as HeadlessSession ?? throw new ToolError("this tool drives a headless session (open_rom / open_project), not a live one");

    private static void AgentLoopTools(Action<string, string, JsonObject, Func<Args, ToolOut>, bool> add)
    {
        add("play_step",
            "The agent play loop's one call (headless): holds the pads for some frames (or until a condition is true, within a frame budget), releases them, and returns " +
            "a small picture (PNG), the RAM values asked for, the frame number and whether the game lagged (lag needs the session opened with checks:true). " +
            "With a reference open (the reference tool), the reference emulator gets the same pads for the same frames and its difference score comes back too " +
            "(and its picture, with ref_picture). The pads are released after each step: a held button is pressed again by the next step (a press of one frame then frames:1). " +
            "Typical: frames 15-30 per decision, scale 0.5.",
            Schema(S(),
                ("buttons", P("string[]", "pad 1's buttons held for the step (Right, A, Start...); empty: none"), false),
                ("buttons2", P("string[]", "pad 2's buttons"), false),
                ("frames", P("integer", "frames to hold them (default 15; 0 = only look)"), false),
                ("until", P("string", "stop as soon as this expression is non-zero, checked after every frame ([$0E] == 8, [lives] < 3, frame >= 900): frames is then ignored"), false),
                ("max_frames", P("integer", "with until: the frame budget (default 600)"), false),
                ("scale", P("number", "the picture: 0.5 (default: half size), 1, 2, or 0 for none"), false),
                ("ram", P("string[]", "RAM to read after the step: expressions, optionally named (lives=[$075A], x={$0086}, [7E:0DBF])"), false),
                ("ref_picture", P("boolean", "with a reference open: also return the reference's picture (default false: only its score)"), false),
                ("milestones", P("string[]", "AutoTAS milestones checked after every frame (id=expression, e.g. map1=[$0727]==0): the step stops at the first true one and says which (stoppedBy milestone, milestone.id); without until, frames is the budget"), false),
                ("milestone_file", P("string", "AutoTAS: a milestones.json ({\"milestones\":[{\"id\",\"until\"}]}) whose conditions are checked like milestones"), false)),
            a => On(a, s => Headless(s).PlayStep(a.List("buttons").ToArray(), a.List("buttons2").ToArray(), a.Int("frames", 15), a.Str("until"), a.Int("max_frames", 600),
                a.Double("scale") ?? 0.5, a.List("ram"), a.Bool("ref_picture", false), HeadlessSession.Milestones(a.List("milestones"), a.Str("milestone_file")))), false);

        add("record",
            "Recording a headless session as a BrokenNes movie (.bnm) for the catalog: start (from where the session is; the movie still holds the input from power-on, so it replays " +
            "and exports to bk2 / fm2), mark (a checkpoint: a mark in the movie at this frame, optionally a named save state too), save (writes the movie so far, keeps recording), " +
            "stop (writes it and ends), status. The movie's marks are the checkpoints.",
            Schema(S(),
                ("action", P("string", "start, mark, save, stop or status", Enum("start", "mark", "save", "stop", "status")), true),
                ("path", P("string", "start / save / stop: the movie file (default: the session's folder)"), false),
                ("text", P("string", "the mark's text (start / mark / stop)"), false),
                ("state", P("string", "mark: also save a state under this name (load_state goes back to it)"), false),
                ("state_path", P("string", "mark: also write the state to this file (an AutoTAS state: a chapter's milestone state; load_state path loads it)"), false)),
            a => On(a, s => (ToolOut)Headless(s).Record(a.Req("action").ToLowerInvariant(), a.Str("path"), a.Str("text"), a.Str("state"), a.Str("state_path"))), false);

        add("reference",
            "The live reference for a headless session: another emulator on the same ROM, lock-stepped with play_step (it gets the same pads for the same frames and waits between steps). " +
            "Mesen 2 for NES, SNES, Game Boy / Color, Master System, Game Gear; BizHawk Genplus-gx for the Genesis (emulator auto). " +
            "open (starts it: Mesen ~2 s, BizHawk up to a few minutes on a busy machine; align_frames > 0 finds the boot offset at once), align (both run frames with no pads, the offset whose pictures match best is kept), " +
            "offset (set it by hand: reference frame = BrokenNes frame + offset), ram (CPU addresses compared on both after every step: lives=$075A), " +
            "compare (both pictures now and the score), status, close. Open it at power-on, before playing; while it is open, drive the session with play_step only.",
            Schema(S(),
                ("action", P("string", "open, align, offset, ram, compare, status or close", Enum("open", "align", "offset", "ram", "compare", "status", "close")), true),
                ("emulator", P("string", "open: auto (default), mesen or bizhawk"), false),
                ("offset", P("integer", "open / offset: the reference's frame minus BrokenNes's"), false),
                ("ram", P("string[]", "open / ram: name=$address pairs read on both machines after every step"), false),
                ("align_frames", P("integer", "open / align: frames of no-pad boot to compare (open: default 0 = no align; align: default 180)"), false),
                ("align_search", P("integer", "open / align: offsets tried each way (default 60; the Game Boy boots ~40-60 frames apart on Mesen)"), false),
                ("scale", P("number", "compare: the pictures' scale (default 1; 0.5 half)"), false)),
            a => On(a, s =>
            {
                string act = a.Req("action").ToLowerInvariant();
                return Headless(s).Reference(act, a.Str("emulator") ?? "auto", a.Long("offset"), a.List("ram"), a.Int("align_frames", act == "align" ? 180 : 0), a.Int("align_search", 60), a.Double("scale") ?? 1);
            }), false);
    }
}
