using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Debugging;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Debug;

/// <summary>
/// <c>--debug --rom &lt;file&gt; [--console nes|snes|widemess|gb|gbc] [--cpu/--ppu/--apu id] [--sym &lt;file&gt;]... [--do "cmd; cmd; ..."] [--script &lt;file&gt;]</c>
/// The headless debugger: load a ROM, set breakpoints and watches, run, print the hits, step, trace. Commands (one per line in a script,
/// or separated by ';' in --do; '#' starts a comment):
/// <code>
///   break &lt;spec&gt; [if &lt;cond&gt;] [hits &lt;n|==n|%n&gt;]          execute breakpoint
///   watch &lt;spec&gt; [r|w|rw] [if &lt;cond&gt;] [hits ...]          read / write watch (default rw)
///   log &lt;spec&gt; [x|r|w|rw] "&lt;text with {expr}&gt;" [if &lt;cond&gt;]  tracepoint: logs, keeps running
///   delete &lt;id&gt; | delete all | list | cpu &lt;main|spc&gt;
///   run [frames]                 run until a break or the frames are done (default 600)
///   step [n] | over | out | scanline | frame | runto &lt;spec&gt; | tofr &lt;frame&gt; | toline &lt;line&gt; | pause
///   regs | stack | trace [n] | mem &lt;spec&gt; [len] | print &lt;expr&gt; | var &lt;name&gt; | set &lt;reg|var&gt; &lt;value&gt; | hits [n] | where
///   pad &lt;Btn+Btn|none&gt;           pad 1 holds these buttons from now on (Right, A, ...), for the next runs and steps
///   history [every] [keep]       (D5) before the first run: a state every N frames (default 60, the last 30) and every frame's input
///   back [n] | back frames &lt;n&gt;    (D5) step back n instructions (to the exact one), or to the start of the n-th frame before now
///   enhance &lt;fastcpu|widescreen|noflicker|sound&gt; &lt;on|off&gt;   the WideMess console's enhancements for a SNES game (spec 6.1)
///   tracefile &lt;path&gt; [fn &lt;symbol&gt;] [range &lt;spec&gt;] [frames &lt;a-b&gt;] | endtrace | sym &lt;file&gt; | domains | limits | detach
/// </code>
/// Specs: <c>$8000</c>, <c>$8000-$80FF</c>, <c>7E:0010</c>, <c>wram:$10</c>, <c>vram:$2000-$27FF</c>, <c>spc:$0200</c>, <c>player_hp</c>, <c>player_hp+1</c>.
/// </summary>
internal static class DebugRunCli
{
    public static int Run(string[] args)
    {
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string romPath = Opt("rom", "");
        // --bug <bundle>: a bug capture (D6) opened in the debugger: its ROM, console, cores and start state, its input preloaded as the
        // history (so 'run' replays it and 'back' steps back through it), its symbols
        Bugs.BugBundleReader? bug = Opt("bug", "") is { Length: > 0 } bp ? Bugs.BugBundleReader.Open(bp) : null;
        NesEmulator.Debugging.Movies.Movie? bugMovie = null;
        if (bug != null) { bugMovie = bug.LoadMovie(); if (romPath == "") romPath = bug.Rom(null, out _); }
        if (romPath == "") { Console.Error.WriteLine("usage: --debug --rom <file> | --bug <bundle> [--console x] [--sym file]... [--do \"cmds\"] [--script file]"); return 2; }
        byte[] raw = File.ReadAllBytes(romPath);
        byte[] rom = RomDetect.Unwrap(raw, romPath, out string inner);
        ConsoleKind? console = Opt("console", "") is { Length: > 0 } c ? Consoles.FromKey(c) : bugMovie != null ? Movies.MovieMachine.ParseConsole(bugMovie.Console) : null;
        string[] bc = bugMovie != null && bugMovie.Cores.Split('/') is { Length: 3 } cs ? cs : new[] { "", "", "" };
        using var h = DebugHost.Open(rom, inner, console, cpu: Opt("cpu", bc[0]), ppu: Opt("ppu", bc[1]), apu: Opt("apu", bc[2]),
                                     powerOn: bugMovie?.Console == "nes" ? bugMovie.PowerOn : "fceux", ntsc: bugMovie?.NtscTiming ?? false);
        h.Dbg.Log = Console.Out;
        h.Dbg.TrackCallStack = true;   // an interactive session: the call stack from the first instruction
        if (bug != null && bugMovie != null)
        {
            bool fromState = bugMovie.Start == NesEmulator.Debugging.Movies.MovieStart.State;
            if (fromState) { h.LoadState(bugMovie.StartState!); h.Dbg.TrackCallStack = false; h.Dbg.TrackCallStack = true; }   // the call stack starts again at the state
            h.EnableHistory(60, 30, fromState ? bugMovie.StartState : null).Preload(bugMovie);
            if (Array.IndexOf(args, "--sym") < 0)
                foreach (var s in bug.Symbols()) Console.WriteLine($"symbols: {h.Dbg.Symbols.Load(s, h.Machine.Console)} from {s} (the bundle's)");
            Console.WriteLine($"bug {bug.Id}: {bug.S("note")} ({bug.S("history", "why")}); {bugMovie.Frames} frames from its {(fromState ? "start state" : "power-on")}, cores {bugMovie.Cores}");
        }
        Console.WriteLine($"debug: {h.Machine.Console} {Path.GetFileName(romPath)}; CPUs {string.Join(", ", h.Machine.Cpus.Select(x => $"{x.Id} ({x.Name})"))}");
        foreach (var l in h.Machine.Limitations) Console.WriteLine("note: " + l);
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--sym") Console.WriteLine($"symbols: {h.Dbg.Symbols.Load(args[i + 1], h.Machine.Console)} from {args[i + 1]}");
        var lines = new List<string>();
        if (Opt("script", "") is { Length: > 0 } sp) lines.AddRange(File.ReadAllLines(sp));
        if (Opt("do", "") is { Length: > 0 } d) lines.AddRange(SplitCommands(d));
        if (lines.Count == 0 && bugMovie != null) lines.AddRange(new[] { $"run {bugMovie.Frames}", "where", "regs", "stack" });
        if (lines.Count == 0) lines.Add("run 600");
        var shell = new DebugShell(h);
        foreach (var raw0 in lines)
        {
            string line = raw0.Trim();
            int hash = line.IndexOf('#');
            if (hash >= 0 && !InQuotes(line, hash)) line = line[..hash].Trim();
            if (line.Length == 0) continue;
            Console.WriteLine("> " + line);
            try { if (!shell.Execute(line)) break; }
            catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException) { Console.WriteLine("error: " + e.Message); shell.Errors++; }
        }
        if (bugMovie != null && h.History is { } hh)
        {
            Console.WriteLine(hh.OffCourseFrame != null
                ? $"bug replay: OFF COURSE after {hh.OffCourseFrame} frames ({hh.OffCourseWhat}); {hh.KeysMatched} of {hh.KeysChecked} keyframes matched"
                : $"bug replay: in sync, {hh.KeysMatched} of {hh.KeysChecked} keyframes matched ({hh.FrameIndex} of {bugMovie.Frames} frames run)");
            if (hh.OffCourseFrame != null) shell.Errors++;
        }
        return shell.Errors == 0 ? 0 : 1;
    }

    private static bool InQuotes(string s, int at) => s[..at].Count(ch => ch == '"') % 2 == 1;

    private static IEnumerable<string> SplitCommands(string s)
    {
        var sb = new StringBuilder();
        bool q = false;
        foreach (char ch in s)
        {
            if (ch == '"') q = !q;
            if (ch == ';' && !q) { yield return sb.ToString(); sb.Clear(); }
            else sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}

/// <summary>The command interpreter of <c>--debug</c> (the MCP server of milestone D3 maps its tools onto the same calls).</summary>
internal sealed class DebugShell
{
    private readonly DebugHost h;
    private string cpu = "main";
    public int Errors;
    public DebugShell(DebugHost host) { h = host; }
    private Debugger D => h.Dbg;
    private DebugCpu Cpu => h.Machine.Cpu(cpu)!;

    public bool Execute(string line)
    {
        var words = Tokens(line);
        string cmd = words[0].ToLowerInvariant();
        string Rest(int from) => string.Join(" ", words.Skip(from));
        switch (cmd)
        {
            case "break": case "b": Add(words, AccessKind.Exec, false); break;
            case "watch": case "w":
            {
                var kind = words.Count > 2 && words[2].ToLowerInvariant() is "r" or "w" or "rw" ? Kind(words[2]) : AccessKind.ReadWrite;
                Add(words, kind, false);
                break;
            }
            case "log": Add(words, AccessKind.Exec, true); break;
            case "delete":
                if (words.Count > 1 && words[1] == "all") D.ClearBreakpoints();
                else if (words.Count > 1) Console.WriteLine(D.Remove(int.Parse(words[1].TrimStart('#'), CultureInfo.InvariantCulture)) ? "deleted" : "no such breakpoint");
                break;
            case "list": foreach (var b in D.Breakpoints) Console.WriteLine("  " + b); break;
            case "cpu": cpu = words[1].ToLowerInvariant(); if (h.Machine.Cpu(cpu) == null) throw new ArgumentException($"no CPU '{cpu}'"); break;
            case "run": Report(h.Runner.Run(words.Count > 1 ? long.Parse(words[1], CultureInfo.InvariantCulture) : 600)); break;
            case "step": case "s":
            {
                int n = words.Count > 1 ? int.Parse(words[1], CultureInfo.InvariantCulture) : 1;
                for (int i = 0; i < n; i++) { var o = h.Runner.Step(() => D.StepInto(cpu)); if (o != RunOutcome.Break) { Report(o); break; } if (i == n - 1 || n <= 16) Report(o, brief: i < n - 1); }
                break;
            }
            case "over": Report(h.Runner.Step(() => D.StepOver(cpu))); break;
            case "out": Report(h.Runner.Step(() => D.StepOut(cpu))); break;
            case "scanline": Report(h.Runner.Step(D.StepScanline)); break;
            case "frame": Report(h.Runner.Step(D.StepFrame)); break;
            case "runto": Report(h.Runner.Step(() => D.RunTo(Rest(1), cpu))); break;
            case "tofr": Report(h.Runner.Step(() => D.RunToFrame(long.Parse(words[1], CultureInfo.InvariantCulture)), guardFrames: long.Parse(words[1], CultureInfo.InvariantCulture) + 2)); break;
            case "toline": Report(h.Runner.Step(() => D.RunToScanline(int.Parse(words[1], CultureInfo.InvariantCulture)))); break;
            case "pause": Report(h.Runner.Step(D.Pause)); break;
            case "pad":
            {
                // pad Right+A | pad none: the buttons pad 1 holds from now on (the next run or step plays with them)
                var held = words.Count < 2 || words[1].Equals("none", StringComparison.OrdinalIgnoreCase) ? new List<string>() : words[1].Split('+', StringSplitOptions.RemoveEmptyEntries).ToList();
                h.SetPad(held);
                Console.WriteLine($"  pad 1 holds {(held.Count == 0 ? "nothing" : string.Join("+", held))}");
                break;
            }
            case "history":
            {
                // history [every] [keep]: from now on a state every <every> frames (the last <keep>) and every frame's input, for 'back'
                int every = words.Count > 1 ? int.Parse(words[1], CultureInfo.InvariantCulture) : 60, keep = words.Count > 2 ? int.Parse(words[2], CultureInfo.InvariantCulture) : 30;
                if (h.Dbg.IsParked || h.Runner.FramesDone > 0) throw new InvalidOperationException("turn the history on before the first run (it starts at power-on)");
                h.EnableHistory(every, keep);
                Console.WriteLine($"  history on: a state every {every} frames, the last {keep} kept");
                break;
            }
            case "back":
            {
                // back [n] | back frames <n>: step back n instructions (default 1), or to the start of the n-th frame before now
                var hist = h.History ?? throw new InvalidOperationException("no history: give 'history' before the first run");
                bool frames = words.Count > 1 && words[1].Equals("frames", StringComparison.OrdinalIgnoreCase);
                long n = words.Count > (frames ? 2 : 1) ? long.Parse(words[frames ? 2 : 1], CultureInfo.InvariantCulture) : 1;
                var r = frames ? hist.BackFrames(h.Runner, n, D.IsParked, h.LoadState, h.EnsureBetweenFrames) : hist.BackInstructions(h.Runner, n, h.LoadState, h.EnsureBetweenFrames);
                Console.WriteLine($"  back {(frames ? n + " frame(s)" : n + " instruction(s)")}: from frame {r.FromFrame}{(r.FromInstruction >= 0 ? $" instruction {r.FromInstruction}" : "")} to frame {r.ToFrame}{(r.ToInstruction >= 0 ? $" instruction {r.ToInstruction}" : "")} (the state of frame {r.SnapshotFrame}, {r.FramesReplayed} frame(s) replayed){(r.Note.Length > 0 ? "; " + r.Note : "")}");
                if (D.IsParked) Report(RunOutcome.Break);
                break;
            }
            case "enhance":
            {
                // enhance fastcpu|widescreen|noflicker|sound on|off: a WideMess console's enhancements for the SNES game (spec 6.1), switched while parked
                if (h.Session is not SnesSession ss || words.Count < 3) throw new ArgumentException("enhance <fastcpu|widescreen|noflicker|sound> <on|off>, on the WideMess console");
                bool on = words[2].Equals("on", StringComparison.OrdinalIgnoreCase);
                string? why = words[1].ToLowerInvariant() switch
                {
                    "fastcpu" => ss.SetEnhancements(fastCpu: on), "widescreen" => ss.SetEnhancements(widescreen: on),
                    "noflicker" => ss.SetEnhancements(noFlicker: on), "sound" => ss.SetEnhancements(betterSound: on),
                    _ => throw new ArgumentException($"unknown enhancement '{words[1]}'"),
                };
                Console.WriteLine($"  {words[1]} {(why == null ? (on ? "on" : "off") : "refused: " + why)}");
                break;
            }
            case "regs": Regs(); break;
            case "where": Console.WriteLine("  " + (D.LastBreak?.ToString() ?? "(not stopped)")); break;
            case "stack":
            {
                var st = D.CallStack(cpu);
                if (st.Count == 0) Console.WriteLine("  (empty)");
                for (int i = st.Count - 1; i >= 0; i--) Console.WriteLine($"  #{st.Count - 1 - i} {st[i]}");
                break;
            }
            case "trace":
            {
                int n = words.Count > 1 ? int.Parse(words[1], CultureInfo.InvariantCulture) : 20;
                foreach (var r in D.TraceTail(n, cpu)) Console.WriteLine("  " + D.FormatTrace(r, cpu));
                break;
            }
            case "mem":
            {
                var ranges = D.Resolve(Cpu, words[1], AccessKind.Read, out string resolved);
                int len = words.Count > 2 ? int.Parse(words[2], CultureInfo.InvariantCulture) : (int)Math.Min(256, ranges[0].End - ranges[0].Start + 1);
                var sp = Cpu.Spaces.First(s => s.Id == ranges[0].Space);
                Console.WriteLine($"  {resolved}");
                for (int row = 0; row < len; row += 16)
                {
                    var sb = new StringBuilder($"  {sp.Name}:{ranges[0].Start + (uint)row:X6} ");
                    for (int i = row; i < Math.Min(len, row + 16); i++) sb.Append(' ').Append(Cpu.PeekSpace(sp.Id, ranges[0].Start + (uint)i).ToString("X2"));
                    Console.WriteLine(sb.ToString());
                }
                break;
            }
            case "print": case "p": { long v = D.Evaluate(Rest(1), cpu); Console.WriteLine($"  {v} (${v:X})"); break; }
            case "var":
            {
                var b = D.ReadVariable(words[1], cpu);
                long v = 0; for (int i = b.Length - 1; i >= 0 && i < 8; i--) v = v << 8 | b[i];
                Console.WriteLine($"  {words[1]} = {string.Join(" ", b.Select(x => x.ToString("X2")))} ({v})");
                break;
            }
            case "set":
            {
                long v = D.Evaluate(Rest(2), cpu);
                if (Cpu.TrySetRegister(words[1], v)) Console.WriteLine($"  {words[1].ToUpperInvariant()} = ${v:X}");
                else { D.WriteVariable(words[1], v, cpu); Console.WriteLine($"  {words[1]} = ${v:X}"); }
                break;
            }
            case "hits":
            {
                int n = words.Count > 1 ? int.Parse(words[1], CultureInfo.InvariantCulture) : 20;
                var all = D.Hits;
                foreach (var x in all.Skip(Math.Max(0, all.Count - n))) Console.WriteLine("  " + x);
                Console.WriteLine($"  ({D.TotalHits} in all)");
                break;
            }
            case "tracefile":
            {
                var o = new Debugger.TraceFileOptions { Cpu = cpu };
                for (int i = 2; i < words.Count - 1; i++)
                {
                    switch (words[i].ToLowerInvariant())
                    {
                        case "fn": o.Function = words[++i]; break;
                        case "range": o.Ranges.Add(words[++i]); break;
                        case "bank": o.Bank = int.Parse(words[++i], NumberStyles.HexNumber, CultureInfo.InvariantCulture); break;
                        case "frames": { var p = words[++i].Split('-'); o.FromFrame = long.Parse(p[0], CultureInfo.InvariantCulture); o.ToFrame = p.Length > 1 ? long.Parse(p[1], CultureInfo.InvariantCulture) : long.MaxValue; break; }
                    }
                }
                D.StartTraceFile(words[1], o);
                Console.WriteLine($"  tracing to {words[1]}");
                break;
            }
            case "endtrace": Console.WriteLine($"  {D.StopTraceFile()} lines written"); break;
            case "sym": Console.WriteLine($"  {D.Symbols.Load(Rest(1), h.Machine.Console)} symbols"); break;
            case "domains": foreach (var dm in h.Machine.Domains()) Console.WriteLine($"  {dm.Id,-8} {dm.Size,9} {dm.Label}{(dm.Unavailable != null ? " (" + dm.Unavailable + ")" : "")}"); break;
            case "limits": foreach (var l in h.Machine.Limitations) Console.WriteLine("  " + l); break;
            case "detach": D.Detach(); Console.WriteLine($"  detached (hooks armed: {D.IsArmed})"); break;
            case "quit": case "exit": return false;
            default: throw new ArgumentException($"unknown command '{cmd}'");
        }
        return true;
    }

    private static AccessKind Kind(string k) => k.ToLowerInvariant() switch { "x" => AccessKind.Exec, "r" => AccessKind.Read, "w" => AccessKind.Write, _ => AccessKind.ReadWrite };

    private void Add(List<string> w, AccessKind kind, bool trace)
    {
        if (w.Count < 2) throw new ArgumentException("an address is needed");
        string spec = w[1];
        string? cond = null, hits = null, log = null;
        for (int i = 2; i < w.Count; i++)
        {
            string t = w[i];
            if (t == "if") { cond = string.Join(" ", w.Skip(i + 1).TakeWhile(x => x != "hits")); i += cond.Split(' ').Length; continue; }
            if (t == "hits" && i + 1 < w.Count) { hits = w[++i]; continue; }
            if (trace && t.StartsWith("\"")) { log = t.Trim('"'); continue; }
            if (t.ToLowerInvariant() is "x" or "r" or "w" or "rw") kind = Kind(t);
        }
        var bp = D.Add(spec, kind, cond, hits, trace, trace ? log ?? "" : null, cpu);
        if (trace && log == null) bp = Replace(bp);
        Console.WriteLine("  " + bp);
    }

    private Breakpoint Replace(Breakpoint bp)
    {
        // a tracepoint without text logs the default line (address, value, symbol, PC, frame, line)
        D.Remove(bp.Id);
        return D.Add(bp.Spec, bp.Kind, bp.Condition, null, true, null, bp.CpuId);
    }

    private void Regs()
    {
        var c = Cpu;
        var sb = new StringBuilder("  ");
        foreach (var r in c.RegisterNames.Distinct()) if (c.TryGetRegister(r, out long v)) sb.Append(r).Append('=').Append(v.ToString("X")).Append(' ');
        Console.WriteLine(sb.ToString());
        var at = D.SymbolAt(c, c.PC);
        Console.WriteLine($"  {c.FormatAddress(c.PC)}{(at != null ? " (" + at + ")" : "")}  frame {h.Machine.Frame} line {h.Machine.Scanline} dot {h.Machine.Dot}");
    }

    private void Report(RunOutcome o, bool brief = false)
    {
        if (o == RunOutcome.Break)
        {
            Console.WriteLine("  " + D.LastBreak);
            if (!brief)
            {
                var t = D.TraceTail(1, D.LastBreak?.CpuId ?? cpu);
                if (t.Length > 0) Console.WriteLine("  last: " + D.FormatTrace(t[0], D.LastBreak?.CpuId ?? cpu));
            }
        }
        else if (o == RunOutcome.Error) Console.WriteLine("  emulation error: " + h.Runner.Error?.Message);
        else Console.WriteLine($"  {o.ToString().ToLowerInvariant()} (frame {h.Machine.Frame}, {h.Runner.FramesDone} run)");
    }

    private static List<string> Tokens(string line)
    {
        var o = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        foreach (char ch in line)
        {
            if (ch == '"') { q = !q; sb.Append(ch); continue; }
            if (char.IsWhiteSpace(ch) && !q) { if (sb.Length > 0) { o.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(ch);
        }
        if (sb.Length > 0) o.Add(sb.ToString());
        return o;
    }
}
