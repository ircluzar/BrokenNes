using System.Text.Json.Nodes;
using NesEmulator.X86;

namespace BrokenNes.Mcp;

/// <summary>
/// nano98 install flow for agents (docs/projects/nano98-format.md "Agent recipe"): one install seat per MCP server, headless (the same
/// NesEmulator.X86.N98Install as the desktop wizard, the HTTP API /api/n98/install/ACTION and Workshop --n98install). Heavy: Windows 98 runs here.
/// </summary>
internal static partial class Tools
{
    private static N98Install? n98;
    private static readonly object n98Gate = new();

    private static void N98InstallTools(Action<string, string, JsonObject, Func<Args, ToolOut>, bool> add)
    {
        add("n98_install",
            "nano98 install flow: the user's own Windows game disc -> a .n98 package that loads instantly in BrokenNes2. One headless install seat per server. Actions: " +
            "machines | drives | setups {disc} | rip {drive, out} (read a disc in the PC's CD/DVD drive to an ISO; a read error stops it: copy protection is never worked around) | " +
            "make-winfiles {iso} (the Windows 98 SE files disc from the user's Windows 98 SE ISO, made once) | winfiles-status | " +
            "start {disc, machine, sound, layers[], settle} (boots the install seat: Windows on the nano98 base, blank D:, the disc in E:) | status | " +
            "runsetup | run {path, args, dir} | keys {text} (keys and mouse: {enter} {tab} {alt+n} {at:x,y} absolute pointer {click} {wait:N} ...) | disc {path|null} | winfiles (Windows asks for its CD) | " +
            "frames {count} (run the machine; returns status) | wait {until: ready|off|exit, frames} | explorer | restart | poweroff | programs (after poweroff: the .EXEs on D:\\GAME) | " +
            "capture {title, exe (or auto), out, args, layer_id, saves[]} | capture-layer {id, out} (settle mode) | close. " +
            "Install the game to D:\\GAME. Look at the screen with n98_install_screenshot after each step. Coordinates are the guest screen's pixels (640x480 setups).",
            Schema(("action", P("string", "the action"), true), ("args", P("object", "the action's arguments (see the description)"), false)),
            a =>
            {
                string action = a.Req("action");
                var body = a.Has("args") ? JsonNode.Parse(a.Raw("args"))!.AsObject() : new JsonObject();
                lock (n98Gate)
                {
                    switch (action)
                    {
                        case "start":
                            {
                                n98?.Dispose(); n98 = null;
                                var o = new N98Install.InstallOptions
                                {
                                    Disc = body["disc"]?.GetValue<string>(), Machine = body["machine"]?.GetValue<string>() ?? "win98-p166-nano", Sound = body["sound"]?.GetValue<string>() ?? "SB16_D072", DriveMb = body["drive_mb"]?.GetValue<int>() ?? N98Seat.StandardDriveMb,
                                    Settle = body["settle"]?.GetValue<bool>() ?? false, Layers = body["layers"] is JsonArray la ? la.Select(x => x!.GetValue<string>()).ToArray() : Array.Empty<string>(),
                                };
                                n98 = N98Install.Start(o);
                                long end = n98.Frames + 60000;
                                while (!n98.ShellReady && n98.Frames < end) n98.RunFrame();
                                var st = n98.Status(); st["ok"] = n98.ShellReady; return st;
                            }
                        case "close": n98?.Dispose(); n98 = null; return new JsonObject { ["ok"] = true };
                        case "frames":
                            {
                                var ins = n98 ?? throw new ToolError("no install seat: action start first");
                                int n = body["count"]?.GetValue<int>() ?? 600;
                                for (int i = 0; i < n; i++) ins.RunFrame();
                                var st = ins.Status(); st["ok"] = true; return st;
                            }
                        case "wait":
                            {
                                var ins = n98 ?? throw new ToolError("no install seat: action start first");
                                string until = body["until"]?.GetValue<string>() ?? "ready";
                                long end = ins.Frames + (body["frames"]?.GetValue<long>() ?? 60000);
                                Func<bool> done = until switch { "off" => () => ins.PoweredOff, "exit" or "ready" => () => ins.Phase == "ready", _ => throw new ToolError("until: ready, off or exit") };
                                while (!done() && ins.Frames < end) ins.RunFrame();
                                var st = ins.Status(); st["ok"] = done(); return st;
                            }
                    }
                    if (!(action == "setups" && body["disc"] == null) && N98Install.DoStatic(action, body) is { } r) return r;
                    var s = n98 ?? throw new ToolError("no install seat: action start first");
                    try
                    {
                        var res = s.Do(action, body);
                        if (action is "run" or "runsetup" or "disc" or "winfiles" or "explorer" or "restart") for (int i = 0; i < 8; i++) s.RunFrame();   // (the shell takes requests within a quarter second)
                        if (action == "keys") { long end = s.Frames + 200000; while (s.InputPending > 0 && s.Frames < end) s.RunFrame(); res = s.Status(); res["ok"] = true; }
                        if (action == "poweroff") { long end = s.Frames + 30000; while (!s.PoweredOff && s.Frames < end) s.RunFrame(); res = s.Status(); res["ok"] = s.PoweredOff; }
                        return res;
                    }
                    catch (Exception e) when (e is InvalidOperationException or ArgumentException or FileNotFoundException or IOException) { throw new ToolError(e.Message); }
                }
            }, false);

        add("n98_install_screenshot", "The install seat's screen as an image (PNG).", Schema(("scale", P("integer", "1 (default) or 2"), false)),
            a =>
            {
                lock (n98Gate)
                {
                    var s = (n98 ?? throw new ToolError("no install seat: n98_install action start first")).Session;
                    int w = s.FrameWidth, h = s.FrameHeight;
                    var o = new ToolOut(new JsonObject { ["width"] = w, ["height"] = h, ["phase"] = n98.Phase });
                    o.Pngs.Add(Png.FromArgb(s.Frame.AsSpan(0, w * h).ToArray(), w, h, Math.Clamp(a.Int("scale", 1), 1, 2)));
                    return o;
                }
            }, true);
    }
}
