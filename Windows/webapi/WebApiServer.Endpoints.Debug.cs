using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NesEmulator;
using NesEmulator.Debugging;
using NesEmulator.Systems;

namespace BrokenNes.Windows.WebApi
{
    /// <summary>
    /// The debugger over HTTP (BrokenDev milestone D1; the live MCP sessions of D3 build on these routes). Reading is always allowed:
    /// state, registers, call stack, trace, breakpoints, hits, symbols, disassembly, memory by spec or symbol, expressions. Control
    /// (attach, breakpoints, pause, continue, step, run to, register writes, symbol files, trace files) needs <c>enableDebuggerControl</c>
    /// in the config; without it those routes answer 403 and say why.
    /// </summary>
    public partial class WebApiServer
    {
        /// <summary>The running console session (SNES, Game Boy, ...), or null while the NES runs (set by the main form).</summary>
        public Func<IConsoleSession?>? GetConsoleSession { get; set; }
        /// <summary>Whether the config allows the debugger's control routes.</summary>
        public Func<bool>? DebuggerControlEnabled { get; set; }
        public DebuggerHub Debuggers { get; } = new DebuggerHub();

        /// <summary>Detach the debugger and let a parked emulation go (the main form calls it before it stops the emulation or changes the game).</summary>
        public void ReleaseDebugger() => Debuggers.Release();

        private object? DebugTarget() => (object?)GetConsoleSession?.Invoke() ?? _getNes();

        private bool DebugControlAllowed => DebuggerControlEnabled?.Invoke() == true;

        private static IResult ControlRefused() => Results.Json(new
        {
            success = false,
            error = "debugger control is off: set \"enableDebuggerControl\": true in the BrokenNes config (BrokenDev's live sessions ask the user first). Reading the debugger's state is always allowed.",
        }, statusCode: 403);

        private static string H(uint v, int digits) => "$" + v.ToString(digits >= 6 ? "X6" : "X4");

        private static object CpuView(Debugger? d, DebugCpu c)
        {
            var regs = new Dictionary<string, string>();
            foreach (var r in c.RegisterNames.Distinct()) if (c.TryGetRegister(r, out long v)) regs[r] = "$" + v.ToString("X");
            return new
            {
                id = c.Id, name = c.Name, pc = c.PC, pcHex = H(c.PC, c.AddressDigits), sp = c.SP,
                symbol = d?.SymbolAt(c, c.PC), registers = regs,
                spaces = c.Spaces.Select(s => new { s.Name, s.Size, s.Description, aliases = s.Aliases }),
            };
        }

        private static object BreakView(BreakInfo? b) => b == null ? null! : new
        {
            b.Reason, cpu = b.CpuId, pc = b.Pc, b.PcSymbol, b.BreakpointId, kind = b.Kind.ToString(), b.Address, b.Value, source = b.Source.ToString(), b.Channel,
            accessPc = b.AccessPc, b.Frame, b.Scanline, b.Dot, b.Cycle, text = b.Text,
        };

        private static object BpView(Breakpoint b) => new
        {
            b.Id, cpu = b.CpuId, kind = Breakpoint.KindText(b.Kind), b.Spec, b.Resolved, b.Condition, hitMode = b.HitMode.ToString(), b.HitTarget, b.Hits,
            b.Tracepoint, log = b.LogFormat, b.Enabled, text = b.ToString(),
        };

        public sealed class BreakpointRequest
        {
            public string? Spec { get; set; }
            public string? Kind { get; set; }        // exec | read | write | rw
            public string? Cpu { get; set; }
            public string? Condition { get; set; }
            public string? Hits { get; set; }        // "3", ">=3", "==3", "%3"
            public bool Tracepoint { get; set; }
            public string? Log { get; set; }
        }

        public sealed class StepRequest
        {
            public string? Mode { get; set; }        // into | over | out | scanline | frame
            public string? Cpu { get; set; }
            public string? To { get; set; }          // run-to: an address spec or symbol
            public long? Frame { get; set; }         // run-to a frame
            public int? Scanline { get; set; }       // run-to a line
            public int? WaitMs { get; set; }         // wait this long for the break (default 5000; 0: return at once)
        }

        public sealed class RegisterRequest { public string? Cpu { get; set; } public string? Name { get; set; } public long Value { get; set; } }
        public sealed class PathRequest { public string? Path { get; set; } }

        public sealed class TraceFileRequest
        {
            public string? Path { get; set; }
            public string? Cpu { get; set; }
            public string? Function { get; set; }
            public List<string>? Ranges { get; set; }
            public long? FromFrame { get; set; }
            public long? ToFrame { get; set; }
        }

        private void RegisterDebugEndpoints(WebApplication app)
        {
            // ---------------------------------------------------------------- reading (always allowed)

            // GET /api/debug/state - the machine, its CPUs and registers, whether a debugger is attached / armed / parked, the last break, the breakpoints
            app.MapGet("/api/debug/state", () =>
            {
                var m = Debuggers.Machine(DebugTarget(), out string? why);
                if (m == null) return Results.BadRequest(new { success = false, error = why });
                var d = Debuggers.Current(DebugTarget());
                return Results.Ok(new
                {
                    success = true, console = m.Console, attached = d != null, armed = d?.IsArmed ?? false, parked = d?.IsParked ?? false,
                    controlEnabled = DebugControlAllowed, frame = m.Frame, scanline = m.Scanline, dot = m.Dot, cycle = m.Cycle,
                    cpus = m.Cpus.Select(c => CpuView(d, c)),
                    lastBreak = d == null ? null : BreakView(d.LastBreak),
                    breakpoints = d?.Breakpoints.Select(BpView) ?? Enumerable.Empty<object>(),
                    symbolFiles = d?.Symbols.Files ?? new List<string>(), symbols = d?.Symbols.All.Count ?? 0,
                    limitations = m.Limitations,
                });
            });

            // GET /api/debug/registers?cpu=main
            app.MapGet("/api/debug/registers", (string? cpu) =>
            {
                var m = Debuggers.Machine(DebugTarget(), out string? why);
                if (m == null) return Results.BadRequest(new { success = false, error = why });
                var c = m.Cpu(cpu ?? "main");
                return c == null ? Results.BadRequest(new { success = false, error = $"no CPU '{cpu}'" }) : Results.Ok(new { success = true, cpu = CpuView(Debuggers.Current(DebugTarget()), c) });
            });

            // GET /api/debug/callstack?cpu=main - outermost first (tracked while a debugger is attached)
            app.MapGet("/api/debug/callstack", (string? cpu) =>
            {
                var d = Debuggers.Current(DebugTarget());
                if (d == null) return Results.Ok(new { success = true, attached = false, frames = Array.Empty<object>() });
                try
                {
                    var st = d.CallStack(cpu ?? "main");
                    return Results.Ok(new { success = true, attached = true, frames = st.Select(f => new { f.Kind, callerPc = f.CallerPc, f.CallerSymbol, target = f.Target, f.TargetSymbol, returnPc = f.ReturnPc, f.Frame, text = f.ToString() }) });
                }
                catch (ArgumentException e) { return Results.BadRequest(new { success = false, error = e.Message }); }
            });

            // GET /api/debug/trace?cpu=main&count=64 - the last instructions (Mesen's trace logger columns)
            app.MapGet("/api/debug/trace", (string? cpu, int? count) =>
            {
                var d = Debuggers.Current(DebugTarget());
                if (d == null) return Results.Ok(new { success = true, attached = false, lines = Array.Empty<string>() });
                try
                {
                    var t = d.TraceTail(Math.Clamp(count ?? 64, 1, 4096), cpu ?? "main");
                    return Results.Ok(new { success = true, attached = true, lines = t.Select(r => d.FormatTrace(r, cpu ?? "main")) });
                }
                catch (ArgumentException e) { return Results.BadRequest(new { success = false, error = e.Message }); }
            });

            // GET /api/debug/breakpoints and /api/debug/hits?count=100
            app.MapGet("/api/debug/breakpoints", () =>
            {
                var d = Debuggers.Current(DebugTarget());
                return Results.Ok(new { success = true, attached = d != null, breakpoints = d?.Breakpoints.Select(BpView) ?? Enumerable.Empty<object>() });
            });
            app.MapGet("/api/debug/hits", (int? count) =>
            {
                var d = Debuggers.Current(DebugTarget());
                if (d == null) return Results.Ok(new { success = true, attached = false, total = 0L, hits = Array.Empty<object>() });
                var all = d.Hits;
                int n = Math.Clamp(count ?? 100, 1, 4096);
                return Results.Ok(new
                {
                    success = true, attached = true, total = d.TotalHits,
                    hits = all.Skip(Math.Max(0, all.Count - n)).Select(x => new { x.BreakpointId, cpu = x.CpuId, kind = x.Kind.ToString(), x.Address, x.Value, source = x.Source.ToString(), x.Channel, x.Pc, x.Frame, x.Scanline, x.HitNumber, x.Logged, x.Text }),
                });
            });

            // GET /api/debug/wait?timeoutMs=5000 - waits for the emulation to park (a break, a step's end); answers with the state
            app.MapGet("/api/debug/wait", async (int? timeoutMs) =>
            {
                var d = Debuggers.Current(DebugTarget());
                if (d == null) return Results.Ok(new { success = true, attached = false, parked = false });
                bool parked = await Task.Run(() => d.WaitForPark(Math.Clamp(timeoutMs ?? 5000, 0, 120_000)));
                return Results.Ok(new { success = true, attached = true, parked, lastBreak = BreakView(d.LastBreak) });
            });

            // GET /api/debug/symbol?name=player_hp | ?cpu=main&address=$8000 - a symbol's place and size, or the label at an address
            app.MapGet("/api/debug/symbol", (string? name, string? address, string? cpu) =>
            {
                var d = Debuggers.Current(DebugTarget());
                if (d == null) return Results.Ok(new { success = false, attached = false, error = "no debugger attached (symbols are loaded into one)" });
                var c = d.Machine.Cpu(cpu ?? "main");
                if (c == null) return Results.BadRequest(new { success = false, error = $"no CPU '{cpu}'" });
                if (name != null)
                {
                    if (!d.Symbols.TryFind(name, out var s, out string? err)) return Results.Ok(new { success = false, error = err });
                    bool bound = c.BindSymbol(s, out var key);
                    return Results.Ok(new { success = true, s.Name, s.Domain, s.Address, s.Bank, s.Size, s.SizeKnown, s.File, space = bound ? c.Spaces.FirstOrDefault(x => x.Id == key.Space)?.Name : null, offset = bound ? key.Offset : 0 });
                }
                if (address != null && DebugExpr.TryParseNumber(address.StartsWith("$") || address.StartsWith("0x") ? address : "$" + address, out long a))
                    return Results.Ok(new { success = true, address = a, label = d.SymbolAt(c, (uint)a) });
                return Results.BadRequest(new { success = false, error = "name or address is required" });
            });

            // GET /api/debug/memory?cpu=main&spec=wram:$10&length=16 - memory by spec (address, bank:address, space:address) or symbol, without side effects
            app.MapGet("/api/debug/memory", (string? cpu, string spec, int? length) =>
            {
                var m = Debuggers.Machine(DebugTarget(), out string? why);
                if (m == null) return Results.BadRequest(new { success = false, error = why });
                var c = m.Cpu(cpu ?? "main");
                if (c == null) return Results.BadRequest(new { success = false, error = $"no CPU '{cpu}'" });
                try
                {
                    var d = Debuggers.Current(DebugTarget());
                    // a spec resolves against the attached debugger's symbols; without one, plain addresses only
                    var ranges = Debugger.ResolveSpec(c, d?.Symbols ?? new SymbolTable(), spec, AccessKind.Read, out string resolved);
                    var r0 = ranges[0];
                    int len = Math.Clamp(length ?? (int)Math.Min(256, r0.End - r0.Start + 1), 1, 65536);
                    var data = new int[len];
                    for (int i = 0; i < len; i++) data[i] = c.PeekSpace(r0.Space, r0.Start + (uint)i);
                    return Results.Ok(new { success = true, spec, resolved, space = c.Spaces.First(s => s.Id == r0.Space).Name, offset = r0.Start, data });
                }
                catch (Exception e) when (e is FormatException or ArgumentException) { return Results.BadRequest(new { success = false, error = e.Message }); }
            });

            // GET /api/debug/disasm?cpu=main&address=$8000&count=16 - disassembly from memory, with symbols (default: from the PC)
            app.MapGet("/api/debug/disasm", (string? cpu, string? address, int? count) =>
            {
                var m = Debuggers.Machine(DebugTarget(), out string? why);
                if (m == null) return Results.BadRequest(new { success = false, error = why });
                var c = m.Cpu(cpu ?? "main");
                if (c == null) return Results.BadRequest(new { success = false, error = $"no CPU '{cpu}'" });
                var d = Debuggers.Current(DebugTarget());
                uint pc = c.PC;
                if (address != null && DebugExpr.TryParseNumber(address.StartsWith("$") || address.StartsWith("0x") ? address : "$" + address, out long a)) pc = (uint)a;
                var lines = new List<object>();
                for (int i = 0; i < Math.Clamp(count ?? 16, 1, 512); i++)
                {
                    uint bytes = c.FetchBytes(pc);
                    var ic = c.Classify(bytes);
                    var b = new byte[Math.Max(1, ic.Length)];
                    for (int k = 0; k < b.Length; k++) b[k] = (byte)(bytes >> (8 * k));
                    string text = c.Disassembler.Format(pc, b, c.Context, d == null ? null : x => { var s = d.SymbolAt(c, x); return s != null && !s.Contains('+') ? s : null; });
                    lines.Add(new { address = pc, hex = H(pc, c.AddressDigits), bytes = string.Join(" ", b.Select(x => x.ToString("X2"))), text, label = d?.SymbolAt(c, pc) });
                    pc = c.NextAddress(pc, Math.Max(1, ic.Length));
                }
                return Results.Ok(new { success = true, cpu = c.Id, lines });
            });

            // GET /api/debug/eval?expr=[player_hp]+1&cpu=main
            app.MapGet("/api/debug/eval", (string expr, string? cpu) =>
            {
                var d = Debuggers.Current(DebugTarget());
                if (d == null) return Results.Ok(new { success = false, attached = false, error = "no debugger attached (expressions resolve symbols through it)" });
                try { long v = d.Evaluate(expr, cpu ?? "main"); return Results.Ok(new { success = true, expr, value = v, hex = "$" + v.ToString("X") }); }
                catch (Exception e) when (e is FormatException or ArgumentException) { return Results.BadRequest(new { success = false, error = e.Message }); }
            });

            // ---------------------------------------------------------------- control (enableDebuggerControl)

            app.MapPost("/api/debug/attach", () =>
            {
                if (!DebugControlAllowed) return ControlRefused();
                try { var d = Debuggers.Attach(DebugTarget()); return Results.Ok(new { success = true, console = d.Machine.Console, armed = d.IsArmed, limitations = d.Machine.Limitations }); }
                catch (Exception e) { return Results.BadRequest(new { success = false, error = e.Message }); }
            });

            app.MapPost("/api/debug/detach", () =>
            {
                if (!DebugControlAllowed) return ControlRefused();
                return Results.Ok(new { success = true, detached = Debuggers.Detach() });
            });

            app.MapPost("/api/debug/symbols", async (HttpContext ctx) =>
            {
                if (!DebugControlAllowed) return ControlRefused();
                var body = await ctx.Request.ReadFromJsonAsync<PathRequest>();
                if (string.IsNullOrWhiteSpace(body?.Path)) return Results.BadRequest(new { success = false, error = "path is required" });
                try
                {
                    var d = Debuggers.Attach(DebugTarget());
                    int n = d.Symbols.Load(body.Path, d.Machine.Console);
                    return Results.Ok(new { success = true, loaded = n, total = d.Symbols.All.Count });
                }
                catch (Exception e) { return Results.BadRequest(new { success = false, error = e.Message }); }
            });

            app.MapPost("/api/debug/breakpoints", async (HttpContext ctx) =>
            {
                if (!DebugControlAllowed) return ControlRefused();
                var b = await ctx.Request.ReadFromJsonAsync<BreakpointRequest>();
                if (string.IsNullOrWhiteSpace(b?.Spec)) return Results.BadRequest(new { success = false, error = "spec is required (an address, range, bank:address, space:address or symbol)" });
                var kind = (b.Kind ?? "exec").ToLowerInvariant() switch
                {
                    "exec" or "x" or "execute" => AccessKind.Exec, "read" or "r" => AccessKind.Read, "write" or "w" => AccessKind.Write, "rw" or "access" or "readwrite" => AccessKind.ReadWrite,
                    _ => AccessKind.None,
                };
                if (kind == AccessKind.None) return Results.BadRequest(new { success = false, error = "kind is exec, read, write or rw" });
                try
                {
                    var d = Debuggers.Attach(DebugTarget());
                    var bp = d.Add(b.Spec, kind, b.Condition, b.Hits, b.Tracepoint, b.Tracepoint ? b.Log : null, b.Cpu ?? "main");
                    return Results.Ok(new { success = true, breakpoint = BpView(bp) });
                }
                catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException) { return Results.BadRequest(new { success = false, error = e.Message }); }
            });

            app.MapDelete("/api/debug/breakpoints/{id:int}", (int id) =>
            {
                if (!DebugControlAllowed) return ControlRefused();
                var d = Debuggers.Current(DebugTarget());
                return Results.Ok(new { success = d != null && d.Remove(id) });
            });

            app.MapDelete("/api/debug/breakpoints", () =>
            {
                if (!DebugControlAllowed) return ControlRefused();
                Debuggers.Current(DebugTarget())?.ClearBreakpoints();
                return Results.Ok(new { success = true });
            });

            app.MapPost("/api/debug/pause", async (int? waitMs) =>
            {
                if (!DebugControlAllowed) return ControlRefused();
                try
                {
                    var d = Debuggers.Attach(DebugTarget());
                    d.Pause();
                    bool parked = await Task.Run(() => d.WaitForPark(Math.Clamp(waitMs ?? 5000, 0, 120_000)));
                    return Results.Ok(new { success = true, parked, lastBreak = BreakView(d.LastBreak) });
                }
                catch (Exception e) { return Results.BadRequest(new { success = false, error = e.Message }); }
            });

            app.MapPost("/api/debug/continue", () =>
            {
                if (!DebugControlAllowed) return ControlRefused();
                var d = Debuggers.Current(DebugTarget());
                d?.Continue();
                return Results.Ok(new { success = d != null });
            });

            // POST /api/debug/step {"mode": "into|over|out|scanline|frame", "cpu": "main"} or {"to": "player_hit"} / {"frame": 600} / {"scanline": 100}
            app.MapPost("/api/debug/step", async (HttpContext ctx) =>
            {
                if (!DebugControlAllowed) return ControlRefused();
                var s = await ctx.Request.ReadFromJsonAsync<StepRequest>() ?? new StepRequest();
                try
                {
                    var d = Debuggers.Attach(DebugTarget());
                    string cpu = s.Cpu ?? "main";
                    long before = d.ParkCount;
                    if (s.To != null) d.RunTo(s.To, cpu);
                    else if (s.Frame is long f) d.RunToFrame(f);
                    else if (s.Scanline is int l) d.RunToScanline(l);
                    else switch ((s.Mode ?? "into").ToLowerInvariant())
                    {
                        case "into": d.StepInto(cpu); break;
                        case "over": d.StepOver(cpu); break;
                        case "out": d.StepOut(cpu); break;
                        case "scanline": d.StepScanline(); break;
                        case "frame": d.StepFrame(); break;
                        default: return Results.BadRequest(new { success = false, error = "mode is into, over, out, scanline or frame" });
                    }
                    int wait = Math.Clamp(s.WaitMs ?? 5000, 0, 120_000);
                    bool parked = await Task.Run(() => { var end = Environment.TickCount64 + wait; while (Environment.TickCount64 < end) { if (d.ParkCount != before && d.IsParked) return true; d.WaitForPark(50); } return d.ParkCount != before && d.IsParked; });
                    return Results.Ok(new { success = true, parked, lastBreak = BreakView(d.LastBreak) });
                }
                catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException) { return Results.BadRequest(new { success = false, error = e.Message }); }
            });

            app.MapPost("/api/debug/registers", async (HttpContext ctx) =>
            {
                if (!DebugControlAllowed) return ControlRefused();
                var r = await ctx.Request.ReadFromJsonAsync<RegisterRequest>();
                var m = Debuggers.Machine(DebugTarget(), out string? why);
                var c = m?.Cpu(r?.Cpu ?? "main");
                if (c == null || r?.Name == null) return Results.BadRequest(new { success = false, error = why ?? "cpu and name are required" });
                ExternalChange?.Invoke($"the {c.Id} register {r.Name} was written through the debugger");
                return Results.Ok(new { success = c.TrySetRegister(r.Name, r.Value), cpu = CpuView(Debuggers.Current(DebugTarget()), c) });
            });

            app.MapPost("/api/debug/trace-file", async (HttpContext ctx) =>
            {
                if (!DebugControlAllowed) return ControlRefused();
                var t = await ctx.Request.ReadFromJsonAsync<TraceFileRequest>();
                if (string.IsNullOrWhiteSpace(t?.Path)) return Results.BadRequest(new { success = false, error = "path is required" });
                try
                {
                    var d = Debuggers.Attach(DebugTarget());
                    d.StartTraceFile(t.Path, new Debugger.TraceFileOptions { Cpu = t.Cpu ?? "main", Function = t.Function, Ranges = t.Ranges ?? new List<string>(), FromFrame = t.FromFrame ?? 0, ToFrame = t.ToFrame ?? long.MaxValue });
                    return Results.Ok(new { success = true, path = t.Path });
                }
                catch (Exception e) { return Results.BadRequest(new { success = false, error = e.Message }); }
            });

            app.MapDelete("/api/debug/trace-file", () =>
            {
                if (!DebugControlAllowed) return ControlRefused();
                var d = Debuggers.Current(DebugTarget());
                return Results.Ok(new { success = d != null, lines = d?.StopTraceFile() ?? 0 });
            });
        }
    }
}
