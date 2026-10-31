using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.JSInterop;
using NesEmulator.Debugging;
using NesEmulator.Systems;

namespace BrokenNes
{
    /// <summary>
    /// The Lite debug bridge (BrokenDev D8, docs/projects/brokendev-debugging-plan.md "D8 spec: the pages on Lite and on a phone", case 1):
    /// <c>window.brokenNesDebug.get(path)</c> answers the BrokenDev pages' read-only GET routes in the page itself, with the same JSON the
    /// desktop's HTTP API gives. Read only: no debugger is attached and no hook armed (a debug machine is only a view of the running game), and
    /// nothing of it runs until a page calls it. No breakpoints, events or symbols (Lite's single thread cannot park).
    /// </summary>
    public partial class Emulator
    {
        private object? bridgeGame; private object? bridgeBus; private DebugMachine? bridgeMachine; private string? bridgeWhy;

        private object? BridgeGame => (object?)session ?? nes;

        private DebugMachine? BridgeMachine()
        {
            var g = BridgeGame;
            var bus = (g as NesEmulator.NES)?.DebugBus;
            if (!ReferenceEquals(g, bridgeGame) || !ReferenceEquals(bus, bridgeBus))
            {
                bridgeGame = g; bridgeBus = bus; bridgeMachine = null; bridgeWhy = null;
                try
                {
                    bridgeMachine = g switch { NesEmulator.NES n when n.DebugBus != null => DebugMachines.For(n), IConsoleSession s => DebugMachines.For(s), _ => null };
                    if (bridgeMachine == null) bridgeWhy = "no game is running";
                }
                catch (Exception e) { bridgeWhy = e.Message; }
            }
            return bridgeMachine;
        }

        private static string H(uint v, int digits) => "$" + v.ToString(digits >= 6 ? "X6" : "X4");

        /// <summary>One read-only route, as the desktop API answers it: "/api/debug/state", "/api/debug/disasm?count=16", "/api/debug/memory?spec=...",
        /// "/api/memory/domains", "/api/memory/peek-range?domain=&amp;address=&amp;length=", "/api/debug/palettes", "/api/debug/tiles?...",
        /// "/api/debug/map?...", "/api/debug/sprites", "/api/debug/sound", "/api/debug/callstack", "/api/debug/events".</summary>
        [JSInvokable]
        public string DebugJson(string path)
        {
            try { return Route(path).ToJsonString(); }
            catch (Exception e) { return new JsonObject { ["success"] = false, ["error"] = e.GetType().Name + ": " + e.Message }.ToJsonString(); }
        }

        private JsonNode Route(string path)
        {
            int qi = path.IndexOf('?');
            string route = qi < 0 ? path : path[..qi];
            var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (qi >= 0)
                foreach (var kv in path[(qi + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int e = kv.IndexOf('=');
                    q[Uri.UnescapeDataString(e < 0 ? kv : kv[..e])] = e < 0 ? "" : Uri.UnescapeDataString(kv[(e + 1)..]);
                }
            string? Q(string k) => q.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
            int I(string k, int d) => int.TryParse(Q(k), out int v) ? v : d;
            var m = BridgeMachine();
            if (m == null) return Fail(bridgeWhy ?? "no game is running");
            var g = BridgeGame;
            var c0 = m.Main;
            switch (route)
            {
                case "/api/debug/state":
                {
                    var cpus = new JsonArray();
                    foreach (var c in m.Cpus) cpus.Add(Cpu(c));
                    return new JsonObject
                    {
                        ["success"] = true, ["console"] = m.Console, ["attached"] = false, ["armed"] = false, ["parked"] = false, ["controlEnabled"] = false, ["lite"] = true,
                        ["frame"] = m.Frame, ["scanline"] = m.Scanline, ["dot"] = m.Dot, ["cpus"] = cpus, ["symbols"] = 0,
                    };
                }
                case "/api/debug/registers":
                {
                    var c = m.Cpu(Q("cpu") ?? "main");
                    return c == null ? Fail($"no CPU '{Q("cpu")}'") : new JsonObject { ["success"] = true, ["cpu"] = Cpu(c) };
                }
                case "/api/debug/callstack":
                    return new JsonObject { ["success"] = true, ["attached"] = false, ["frames"] = new JsonArray(), ["note"] = "Lite has no debugger attached (its single thread cannot park): no call stack" };
                case "/api/debug/events":
                    return new JsonObject { ["success"] = false, ["attached"] = false, ["recording"] = false, ["console"] = m.Console, ["error"] = "Lite has no Event View: the event recording is the desktop app's (Emulator > Debug Windows > Event View)" };
                case "/api/debug/disasm":
                {
                    var c = m.Cpu(Q("cpu") ?? "main") ?? c0;
                    uint pc = c.PC;
                    if (Q("address") is { } a && DebugExpr.TryParseNumber(a.StartsWith("$") || a.StartsWith("0x") ? a : "$" + a, out long av)) pc = (uint)av;
                    var lines = new JsonArray();
                    for (int i = 0; i < Math.Clamp(I("count", 16), 1, 512); i++)
                    {
                        uint bytes = c.FetchBytes(pc);
                        var ic = c.Classify(bytes);
                        var b = c.InstructionBytes(pc, ic.Length);
                        lines.Add(new JsonObject { ["address"] = pc, ["hex"] = H(pc, c.AddressDigits), ["bytes"] = string.Join(" ", b.Select(x => x.ToString("X2"))), ["text"] = c.Disassembler.Format(pc, b, c.Context, null), ["label"] = null });
                        pc = c.NextAddress(pc, b.Length);
                    }
                    return new JsonObject { ["success"] = true, ["cpu"] = c.Id, ["lines"] = lines };
                }
                case "/api/debug/memory":
                {
                    var c = m.Cpu(Q("cpu") ?? "main") ?? c0;
                    var ranges = Debugger.ResolveSpec(c, new SymbolTable(), Q("spec") ?? "$0", AccessKind.Read, out string resolved);
                    var r0 = ranges[0];
                    int len = Math.Clamp(I("length", (int)Math.Min(256, r0.End - r0.Start + 1)), 1, 65536);
                    var data = new JsonArray();
                    for (int i = 0; i < len; i++) data.Add((int)c.PeekSpace(r0.Space, r0.Start + (uint)i));
                    return new JsonObject { ["success"] = true, ["spec"] = Q("spec"), ["resolved"] = resolved, ["space"] = c.Spaces.First(s => s.Id == r0.Space).Name, ["offset"] = r0.Start, ["data"] = data };
                }
                case "/api/memory/domains":
                {
                    var arr = new JsonArray();
                    foreach (var d in m.Domains()) arr.Add(new JsonObject { ["name"] = d.Id, ["size"] = d.Size, ["description"] = d.Label, ["writable"] = false, ["unavailable"] = d.Unavailable });
                    return new JsonObject { ["success"] = true, ["console"] = m.Console, ["domains"] = arr };
                }
                case "/api/memory/peek-range":
                {
                    var d = m.Domains().FirstOrDefault(x => x.Id.Equals(Q("domain") ?? "", StringComparison.OrdinalIgnoreCase) && x.Unavailable == null);
                    if (d == null) return Fail($"no memory called '{Q("domain")}' (it has: {string.Join(", ", m.Domains().Select(x => x.Id))})");
                    int at = I("address", 0), len = Math.Clamp(I("length", 16), 1, 65536);
                    var data = new JsonArray();
                    for (int i = 0; i < len && at + i < d.Size; i++) data.Add((int)d.Peek(at + i));
                    return new JsonObject { ["success"] = true, ["domain"] = d.Id, ["address"] = at, ["data"] = data };
                }
                case "/api/debug/palettes":
                {
                    var arr = new JsonArray();
                    foreach (var (label, line, colours) in DebugPictures.Palettes(m)) arr.Add(new JsonObject { ["label"] = label, ["line"] = line, ["colours"] = new JsonArray(colours.Select(x => (JsonNode)JsonValue.Create("#" + (x & 0xFFFFFF).ToString("X6"))!).ToArray()) });
                    return new JsonObject { ["success"] = true, ["console"] = m.Console, ["palettes"] = arr };
                }
                case "/api/debug/tiles":
                {
                    var t = DebugPictures.Tiles(m.Domains(), DebugPictures.Family(m.Console), Q("source"), I("bpp", 4), I("palette", -1), I("start", 0), Math.Clamp(I("count", 256), 1, 1024));
                    return new JsonObject { ["success"] = true, ["console"] = m.Console, ["what"] = t.What, ["colours"] = t.Colours, ["width"] = t.Width, ["height"] = t.Height, ["tiles"] = t.Tiles, ["tileBytes"] = t.TileBytes, ["columns"] = t.Columns, ["start"] = t.Start, ["argb"] = B64(t.Pixels) };
                }
                case "/api/debug/map":
                {
                    var layers = DebugPictures.MapLayers(m.Console);
                    var v = DebugPictures.Map(m, g, Q("layer") ?? layers.FirstOrDefault() ?? "");
                    int cx = Math.Clamp(I("cellX", 0), 0, v.CellsX - 1), cy = Math.Clamp(I("cellY", 0), 0, v.CellsY - 1);
                    var cell = v.Cell(cx, cy);
                    return new JsonObject
                    {
                        ["success"] = true, ["console"] = m.Console, ["layers"] = new JsonArray(layers.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()), ["what"] = v.What,
                        ["width"] = v.Width, ["height"] = v.Height, ["cellsX"] = v.CellsX, ["cellsY"] = v.CellsY, ["cellSize"] = v.CellSize,
                        ["view"] = new JsonObject { ["x"] = v.ViewX, ["y"] = v.ViewY, ["w"] = v.ViewW, ["h"] = v.ViewH },
                        ["cell"] = new JsonObject { ["x"] = cx, ["y"] = cy, ["tile"] = cell.tile, ["palette"] = cell.palette, ["flags"] = cell.flags }, ["argb"] = B64(v.Pixels),
                    };
                }
                case "/api/debug/sprites":
                {
                    var list = DebugPictures.Sprites(m, out string note);
                    var arr = new JsonArray();
                    foreach (var s in list) arr.Add(new JsonObject { ["index"] = s.Index, ["x"] = s.X, ["y"] = s.Y, ["width"] = s.Width, ["height"] = s.Height, ["tile"] = s.Tile, ["palette"] = s.Palette, ["text"] = s.ToString() });
                    return new JsonObject { ["success"] = true, ["console"] = m.Console, ["note"] = note, ["count"] = list.Count, ["sprites"] = arr };
                }
                case "/api/debug/sound":
                {
                    var ch = DebugSound.Channels(m, g, out string what);
                    var arr = new JsonArray();
                    foreach (var c in ch) arr.Add(new JsonObject { ["name"] = c.Name, ["on"] = c.On, ["level"] = c.Level, ["registers"] = c.Text, ["line"] = c.ToString() });
                    return new JsonObject { ["success"] = true, ["console"] = m.Console, ["what"] = what, ["mute"] = DebugSound.Mute(g), ["channels"] = arr };
                }
                default:
                    return Fail($"the Lite bridge answers the read-only debug routes only; not '{route}'");
            }
        }

        private static JsonObject Fail(string why) => new() { ["success"] = false, ["error"] = why };

        private static string B64(uint[] px) { var b = new byte[px.Length * 4]; Buffer.BlockCopy(px, 0, b, 0, b.Length); return Convert.ToBase64String(b); }

        private static JsonObject Cpu(DebugCpu c)
        {
            var regs = new JsonObject();
            foreach (var r in c.RegisterNames.Distinct()) if (c.TryGetRegister(r, out long v)) regs[r] = "$" + v.ToString("X");
            return new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["pc"] = c.PC, ["pcHex"] = H(c.PC, c.AddressDigits), ["sp"] = c.SP, ["symbol"] = null, ["registers"] = regs };
        }
    }
}
