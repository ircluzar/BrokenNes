using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NesEmulator.Debugging.Timeflow;

/// <summary>
/// A streaming question over a profile (T3): it sees every record of the span as it happens (or as a kept profile is read back) and keeps
/// only its answer, a small JSON object. <see cref="Parse"/> reads the query language:
/// <code>
///   writes &lt;spec&gt; [limit N] [cpu C]          every write to an address, range or symbol (all its mirrors; DMA too), who made it
///   reads &lt;spec&gt; | accesses &lt;spec&gt;          the same for reads, or both
///   last-write &lt;spec&gt; [before F[:CYCLE] | before cycle C]   the last write before a frame (or a cycle of it)
///   stack &lt;N&gt;                              the call stack at instruction #N (innermost first)
///   between &lt;a&gt; &lt;b&gt;                        what ran between two markers (debug-port marker text, #N, or frame:F)
///   became &lt;name&gt; &lt;op&gt; &lt;value&gt;            the instruction that made a register or a variable == / != / &lt; / &gt; a value
///   when &lt;expression&gt;                      the first instruction after which a debugger expression holds (a re-run only)
///   histogram [cpu C] [top N]                instructions (exclusive, inclusive), cycles and calls per function
///   events &lt;F&gt;[-&lt;G&gt;] [limit N]              chip events of a frame: register writes by scanline / dot, DMA runs, interrupts (D8's view)
///   trace &lt;N&gt; [before K] [after K] | trace frame &lt;F&gt; [count K]   trace lines (Mesen's columns) around #N or from a frame's start
///   vars [name] [limit N]                    the named variables' changes (old, new, the instruction that wrote it)
///   summary                                  counts: frames, instructions by CPU, accesses, calls, the busiest frame
/// </code>
/// </summary>
public abstract class ProfileQuery : ProfileSink
{
    public string Text { get; private set; } = "";
    protected ProfileContext Ctx = null!;
    protected string? Error;
    public override void Begin(ProfileContext ctx) { Ctx = ctx; try { Setup(); } catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException) { Error = e.Message; } }
    protected virtual void Setup() { }
    public override bool Done => Error != null || IsDone;
    protected virtual bool IsDone => false;

    /// <summary>The answer (or the error, said).</summary>
    public JsonObject Result()
    {
        var j = new JsonObject { ["query"] = Text };
        if (Ctx == null) { j["error"] = "the query saw no records (the span was not profiled)"; return j; }
        if (Error != null) { j["error"] = Error; return j; }
        Answer(j);
        if (Ctx.OffFrame is long off) j["warning"] = $"the re-run went out of sync at frame {off} ({Ctx.OffWhat}): what follows that frame is not the recorded game";
        return j;
    }

    protected abstract void Answer(JsonObject j);

    // ------------------------------------------------------------------------------------------------ parsing

    public static readonly string[] Kinds = { "writes", "reads", "accesses", "last-write", "stack", "between", "became", "when", "histogram", "events", "trace", "vars", "summary" };

    /// <summary>Reads one query (see the class comment). Throws <see cref="FormatException"/> with what is wrong.</summary>
    public static ProfileQuery Parse(string text)
    {
        var t = Tokens(text);
        if (t.Count == 0) throw new FormatException("an empty query (one of: " + string.Join(", ", Kinds) + ")");
        string verb = t[0].ToLowerInvariant();
        var rest = t.Skip(1).ToList();
        string? Take(string key)
        {
            int i = rest.FindIndex(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= rest.Count) return null;
            string v = rest[i + 1]; rest.RemoveRange(i, 2); return v;
        }
        int Int(string key, int dflt) { string? v = Take(key); return v == null ? dflt : (int)Num(v); }
        string cpu = Take("cpu") ?? "main";
        ProfileQuery q = verb switch
        {
            "writes" or "reads" or "accesses" => new AccessQuery { Kind = verb, Limit = Int("limit", 20), CpuName = cpu },
            "last-write" or "lastwrite" => new LastWriteQuery { CpuName = cpu },
            "stack" => new StackQuery(),
            "between" => new BetweenQuery { Limit = Int("limit", 20) },
            "became" => new BecameQuery { CpuName = cpu },
            "when" => new WhenQuery(),
            "histogram" or "functions" => new HistogramQuery { CpuName = cpu, Top = Int("top", 25) },
            "events" => new EventsQuery { Limit = Int("limit", 200) },
            "trace" => new TraceQuery { Before = Int("before", 10), After = Int("after", 10), Count = Int("count", 40), CpuName = Take("cpu") ?? cpu },
            "vars" or "variables" => new VarsQuery { Limit = Int("limit", 50) },
            "summary" => new SummaryQuery(),
            _ => throw new FormatException($"unknown query '{t[0]}' (one of: {string.Join(", ", Kinds)})"),
        };
        q.Text = text.Trim();
        q.Args = rest;
        q.Init();
        return q;
    }

    protected List<string> Args = new();
    protected virtual void Init() { }

    private static List<string> Tokens(string s)
    {
        var l = new List<string>();
        foreach (Match m in Regex.Matches(s ?? "", "\"([^\"]*)\"|'([^']*)'|(\\S+)"))
            l.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
        return l;
    }

    /// <summary>$1F, 0x1F, %1010 or 31.</summary>
    public static long Num(string v)
    {
        v = v.Trim();
        if (v.StartsWith("$")) return long.Parse(v[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (v.StartsWith("%")) return Convert.ToInt64(v[1..], 2);
        return long.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    protected string NeedArg(int i, string what) => i < Args.Count ? Args[i] : throw new FormatException($"'{Text.Split(' ')[0]}' needs {what}");

    // ------------------------------------------------------------------------------------------------ shared pieces

    /// <summary>Each CPU's call stack, followed from the records (a frame's snapshot puts it right).</summary>
    protected sealed class Stacks
    {
        private readonly List<ProfCall>[] s;
        public Stacks(int n) { s = Enumerable.Range(0, n).Select(_ => new List<ProfCall>()).ToArray(); }
        public void Reset(int cpu, IReadOnlyList<ProfCall> frames) { s[cpu].Clear(); s[cpu].AddRange(frames); }
        public void On(in ProfCall c)
        {
            var l = s[c.Cpu];
            if (!c.Return) { l.Add(c); return; }
            for (int i = l.Count - 1; i >= 0; i--)
                if (l[i].Target == c.Target && l[i].ReturnPc == c.ReturnPc && l[i].CallerPc == c.CallerPc) { l.RemoveAt(i); return; }
        }
        public IReadOnlyList<ProfCall> Of(int cpu) => s[cpu];
        public int Depth(int cpu) => s[cpu].Count;
    }

    protected JsonArray StackJson(IReadOnlyList<ProfCall> frames)
    {
        var a = new JsonArray();
        for (int i = frames.Count - 1; i >= 0; i--)
        {
            var f = frames[i];
            string target = Ctx.SymbolAtAddress(f.Cpu, f.Target) ?? Ctx.Address(f.Cpu, f.Target);
            string caller = Ctx.SymbolAtAddress(f.Cpu, f.CallerPc) ?? Ctx.Address(f.Cpu, f.CallerPc);
            a.Add($"{target} <- {caller} ({f.Kind}, frame {f.Frame})");
        }
        return a;
    }

    /// <summary>The function an instruction runs in: the innermost call frame's target (its symbol, or its address), else (outside any call
    /// the profile knows) the code symbol containing the PC, else its 256-byte page. Labels inside a function count as the function.</summary>
    protected string FunctionNow(in ProfInstr i, Stacks st)
    {
        var s = st.Of(i.Cpu);
        if (s.Count > 0) return FunctionAt(i.Cpu, s[^1].Target);
        return Ctx.FunctionOf(in i) ?? "(no symbol) page " + Ctx.Address(i.Cpu, i.R.Pc & ~0xFFu);
    }

    protected string FunctionAt(int cpu, uint target)
    {
        string? s = Ctx.SymbolAtAddress(cpu, target);
        if (s == null) return Ctx.Address(cpu, target);
        int plus = s.IndexOf("+$", StringComparison.Ordinal);
        return plus > 0 ? s[..plus] : s;
    }

    protected string StackNote => $"call frames entered before frame {Ctx.StackKnownFrom} (where the re-run began) are not known";

    /// <summary>The last instruction record of each CPU (an access's instruction, its text).</summary>
    protected sealed class LastInstr
    {
        public readonly ProfInstr[] Of;
        public LastInstr(int n) { Of = new ProfInstr[n]; }
    }

    protected JsonObject InstrJson(in ProfInstr i, bool line = false)
    {
        var o = new JsonObject
        {
            ["at"] = i.Seq, ["frame"] = i.Frame, ["scanline"] = (int)i.R.Scanline, ["dot"] = (int)i.R.Dot, ["cycle"] = i.R.Cycle, ["cpu"] = Ctx.CpuId(i.Cpu),
            ["pc"] = Ctx.Address(i.Cpu, i.R.Pc), ["in"] = Ctx.SymbolOf(in i), ["instruction"] = Ctx.Disasm(in i),
            ["registers"] = Ctx.Cpu(i.Cpu).FormatRegisters(in i.R),
        };
        if (line) o["line"] = Ctx.TraceLine(in i);
        return o;
    }

    protected static string Hex(long v) => v is >= 0 and <= 0xFF ? "$" + v.ToString("X2") : v is >= 0 and <= 0xFFFF ? "$" + v.ToString("X4") : "$" + v.ToString("X");
}

// ================================================================================================================================ accesses

/// <summary>writes / reads / accesses &lt;spec&gt;: every matching access, the first <see cref="Limit"/> in full, all counted by function.</summary>
public sealed class AccessQuery : ProfileQuery
{
    public string Kind = "writes";
    public int Limit = 20;
    public string CpuName = "main";
    private string spec = "";
    private int cpu;
    private List<SpaceRange> ranges = new();
    private string resolved = "";
    private long total, reads, writes;
    private readonly JsonArray shown = new();
    private readonly Dictionary<string, long> byFunction = new();
    private readonly Dictionary<string, long> byValue = new();
    private LastInstr last = null!;
    private Stacks stacks = null!;

    protected override void Init() => spec = string.Join(" ", Args).Trim() is { Length: > 0 } s ? s : throw new FormatException($"'{Kind}' needs an address, range or symbol");

    protected override void Setup()
    {
        cpu = Ctx.CpuIndex(CpuName);
        var kind = Kind == "reads" ? AccessKind.Read : Kind == "writes" ? AccessKind.Write : AccessKind.ReadWrite;
        ranges = Debugger.ResolveSpec(Ctx.Cpu(cpu), Ctx.Debugger.Symbols, spec, kind, out resolved);
        last = new LastInstr(Ctx.Machine.Cpus.Count);
        stacks = new Stacks(Ctx.Machine.Cpus.Count);
    }

    public override void OnStack(int c, IReadOnlyList<ProfCall> frames) { if (Error == null) stacks.Reset(c, frames); }
    public override void OnCall(in ProfCall c) { if (Error == null) stacks.On(in c); }
    public override void OnInstr(in ProfInstr i) { if (Error == null) last.Of[i.Cpu] = i; }

    public override void OnAccess(in ProfAccess a)
    {
        if (Error != null || a.Cpu != cpu) return;
        if (Kind == "writes" && !a.Write || Kind == "reads" && a.Write) return;
        bool m = false;
        Span<SpaceKey> kb = stackalloc SpaceKey[4];
        var keys = a.Keys(kb);
        foreach (var k in keys) { foreach (var r in ranges) if (r.Contains(k)) { m = true; break; } if (m) break; }
        if (!m) return;
        total++;
        if (a.Write) writes++; else reads++;
        ref var ins = ref last.Of[a.Cpu];
        string fn = a.Source != AccessSource.Cpu ? a.Source.ToString() + (a.Channel >= 0 ? " ch" + a.Channel : "") + " (started in " + FunctionNow(in ins, stacks) + ")"
                                                  : FunctionNow(in ins, stacks);
        byFunction[fn] = byFunction.GetValueOrDefault(fn) + 1;
        if (a.Write && byValue.Count < 64) byValue[Hex(a.Value)] = byValue.GetValueOrDefault(Hex(a.Value)) + 1;
        if (shown.Count < Limit)
        {
            var o = new JsonObject
            {
                ["at"] = a.Instr, ["frame"] = a.Frame, ["scanline"] = (int)a.Scanline, ["dot"] = (int)a.Dot, ["cycle"] = a.Cycle,
                ["kind"] = a.Write ? "write" : "read", ["address"] = Ctx.Address(a.Cpu, a.Address), ["target"] = Ctx.SymbolOfTarget(in a), ["value"] = Hex(a.Value),
                ["by"] = a.Source == AccessSource.Cpu ? "cpu" : a.Source.ToString() + (a.Channel >= 0 ? " ch" + a.Channel : ""),
                ["pc"] = Ctx.Address(a.Cpu, a.Pc), ["in"] = Ctx.SymbolOf(in ins), ["instruction"] = ins.Seq == a.Instr ? Ctx.Disasm(in ins) : null,
                ["stack"] = StackJson(stacks.Of(a.Cpu)),
            };
            shown.Add(o);
        }
    }

    protected override void Answer(JsonObject j)
    {
        j["spec"] = spec; j["resolved"] = resolved; j["total"] = total;
        if (Kind == "accesses") { j["reads"] = reads; j["writes"] = writes; }
        j["byFunction"] = new JsonObject(byFunction.OrderByDescending(kv => kv.Value).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        if (Kind != "reads") j["values"] = new JsonObject(byValue.OrderByDescending(kv => kv.Value).Take(16).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        j["shown"] = shown;
        if (total > shown.Count) j["more"] = $"{total - shown.Count} more (limit {Limit})";
        j["stackNote"] = StackNote;
    }
}

/// <summary>last-write &lt;spec&gt; [before F[:CYCLE] | before cycle C]: the last write before a frame's start (or a cycle).</summary>
public sealed class LastWriteQuery : ProfileQuery
{
    public string CpuName = "main";
    private string spec = "";
    private long beforeFrame = long.MaxValue, beforeCycle = long.MaxValue;
    private int cpu;
    private List<SpaceRange> ranges = new();
    private string resolved = "";
    private JsonObject? hit;
    private long count;
    private LastInstr last = null!;
    private Stacks stacks = null!;
    private bool past;

    protected override void Init()
    {
        int b = Args.FindIndex(a => a.Equals("before", StringComparison.OrdinalIgnoreCase));
        var specParts = b >= 0 ? Args.Take(b).ToList() : Args;
        spec = string.Join(" ", specParts).Trim();
        if (spec.Length == 0) throw new FormatException("'last-write' needs an address, range or symbol");
        if (b >= 0)
        {
            string w = NeedArg(b + 1, "a frame or 'cycle C' after 'before'");
            if (w.Equals("cycle", StringComparison.OrdinalIgnoreCase)) beforeCycle = Num(NeedArg(b + 2, "a cycle"));
            else if (w.Contains(':')) { var p = w.Split(':'); beforeFrame = Num(p[0]); beforeCycle = Num(p[1]); }
            else beforeFrame = Num(w);
        }
    }

    public override long LastFrameNeeded => beforeFrame == long.MaxValue ? long.MaxValue : beforeCycle == long.MaxValue ? beforeFrame - 1 : beforeFrame;

    protected override void Setup()
    {
        cpu = Ctx.CpuIndex(CpuName);
        ranges = Debugger.ResolveSpec(Ctx.Cpu(cpu), Ctx.Debugger.Symbols, spec, AccessKind.Write, out resolved);
        last = new LastInstr(Ctx.Machine.Cpus.Count);
        stacks = new Stacks(Ctx.Machine.Cpus.Count);
        if (beforeFrame != long.MaxValue && beforeFrame <= Ctx.From && beforeCycle == long.MaxValue)
            throw new ArgumentException($"'before {beforeFrame}' is at or before the span's first frame {Ctx.From}: profile a span that starts earlier");
    }

    protected override bool IsDone => past;
    public override void OnStack(int c, IReadOnlyList<ProfCall> frames) { if (Error == null) stacks.Reset(c, frames); }
    public override void OnCall(in ProfCall c) { if (Error == null) stacks.On(in c); }
    public override void OnInstr(in ProfInstr i) { if (Error == null) last.Of[i.Cpu] = i; }
    public override void OnFrame(in ProfFrame f) { if (f.Frame >= beforeFrame && beforeCycle == long.MaxValue) past = true; if (f.Frame > beforeFrame) past = true; }

    public override void OnAccess(in ProfAccess a)
    {
        if (Error != null || past || !a.Write || a.Cpu != cpu) return;
        if (a.Frame > beforeFrame || a.Frame == beforeFrame && (beforeCycle == long.MaxValue || a.Cycle >= beforeCycle)) return;
        if (beforeFrame == long.MaxValue && beforeCycle != long.MaxValue && a.Cycle >= beforeCycle) { past = true; return; }
        bool m = false;
        Span<SpaceKey> kb = stackalloc SpaceKey[4];
        foreach (var k in a.Keys(kb)) { foreach (var r in ranges) if (r.Contains(k)) { m = true; break; } if (m) break; }
        if (!m) return;
        count++;
        ref var ins = ref last.Of[a.Cpu];
        hit = new JsonObject
        {
            ["at"] = a.Instr, ["frame"] = a.Frame, ["scanline"] = (int)a.Scanline, ["dot"] = (int)a.Dot, ["cycle"] = a.Cycle,
            ["address"] = Ctx.Address(a.Cpu, a.Address), ["target"] = Ctx.SymbolOfTarget(in a), ["value"] = Hex(a.Value),
            ["by"] = a.Source == AccessSource.Cpu ? "cpu" : a.Source.ToString() + (a.Channel >= 0 ? " ch" + a.Channel : ""),
            ["pc"] = Ctx.Address(a.Cpu, a.Pc), ["in"] = Ctx.SymbolOf(in ins), ["instruction"] = ins.Seq == a.Instr ? Ctx.Disasm(in ins) : null,
            ["registers"] = ins.Seq == a.Instr ? Ctx.Cpu(a.Cpu).FormatRegisters(in ins.R) : null,
            ["stack"] = StackJson(stacks.Of(a.Cpu)),
        };
    }

    protected override void Answer(JsonObject j)
    {
        j["spec"] = spec; j["resolved"] = resolved;
        j["before"] = beforeFrame == long.MaxValue && beforeCycle == long.MaxValue ? "the span's end" : beforeFrame == long.MaxValue ? $"cycle {beforeCycle}" : beforeCycle == long.MaxValue ? $"frame {beforeFrame}" : $"frame {beforeFrame} cycle {beforeCycle}";
        j["writesSeen"] = count;
        j["last"] = hit;
        if (hit == null) j["note"] = $"no write to {spec} in the profiled frames {Ctx.From}-{Math.Min(Ctx.To, beforeFrame)} before that point";
        j["stackNote"] = StackNote;
    }
}

// ================================================================================================================================ the call stack

/// <summary>stack &lt;N&gt;: the call stack at instruction #N.</summary>
public sealed class StackQuery : ProfileQuery
{
    private long at;
    private Stacks stacks = null!;
    private JsonObject? found;
    protected override void Init() => at = Num(NeedArg(0, "an instruction position (#N from another answer)").TrimStart('#'));
    protected override void Setup() => stacks = new Stacks(Ctx.Machine.Cpus.Count);
    protected override bool IsDone => found != null;
    public override void OnStack(int c, IReadOnlyList<ProfCall> frames) { if (Error == null && found == null) stacks.Reset(c, frames); }
    public override void OnCall(in ProfCall c) { if (Error == null && found == null) stacks.On(in c); }
    public override void OnInstr(in ProfInstr i)
    {
        if (Error != null || found != null || i.Seq != at) return;
        found = InstrJson(in i, true);
        found["stack"] = StackJson(stacks.Of(i.Cpu));
        found["depth"] = stacks.Depth(i.Cpu);
    }
    protected override void Answer(JsonObject j)
    {
        j["at"] = at;
        if (found == null) { j["error"] = $"instruction #{at} is not in the profiled span"; return; }
        j["instruction"] = found;
        j["stackNote"] = StackNote;
    }
}

// ================================================================================================================================ between markers

/// <summary>between &lt;a&gt; &lt;b&gt;: what ran from one marker (debug-port marker text, #N, frame:F) to the next other one.</summary>
public sealed class BetweenQuery : ProfileQuery
{
    public int Limit = 20;
    private string a = "", b = "";
    private bool inside, closed;
    private long startSeq = -1, endSeq = -1, startFrame, endFrame;
    private long startCycle = -1, lastCycle;
    private readonly Dictionary<string, long> byFn = new();
    private readonly Dictionary<string, long> byCpu = new();
    private long instrs, writes, calls;
    private readonly JsonArray vars = new();
    private readonly List<string> markers = new();

    private Stacks stacks = null!;
    protected override void Init() { a = NeedArg(0, "two markers"); b = NeedArg(1, "two markers"); }
    protected override void Setup() => stacks = new Stacks(Ctx.Machine.Cpus.Count);
    protected override bool IsDone => closed;
    public override void OnStack(int c, IReadOnlyList<ProfCall> frames) => stacks.Reset(c, frames);

    private static bool IsSeq(string m, out long s) { s = 0; return m.StartsWith('#') && long.TryParse(m[1..], out s); }
    private static bool IsFrame(string m, out long f) { f = 0; return m.StartsWith("frame:", StringComparison.OrdinalIgnoreCase) && long.TryParse(m[6..], out f); }

    private void Open(long seq, long frame) { if (inside || closed) return; inside = true; startSeq = seq; startFrame = frame; }
    private void Close(long seq, long frame) { if (!inside || closed) return; inside = false; closed = true; endSeq = seq; endFrame = frame; }

    public override void OnFrame(in ProfFrame f)
    {
        if (IsFrame(a, out long fa) && f.Frame == fa) Open(f.Seq, f.Frame);
        else if (IsFrame(b, out long fb) && f.Frame == fb) Close(f.Seq, f.Frame);
    }

    public override void OnMarker(in ProfMarker m)
    {
        if (inside) markers.Add($"frame {m.Frame} #{m.Seq}: {m.Text}");
        if (!inside && m.Text == a) Open(m.Seq, m.Frame);
        else if (inside && m.Text == b) Close(m.Seq, m.Frame);
    }

    public override void OnInstr(in ProfInstr i)
    {
        if (IsSeq(a, out long sa) && i.Seq == sa) Open(i.Seq, i.Frame);
        if (IsSeq(b, out long sb) && i.Seq == sb) Close(i.Seq, i.Frame);
        if (!inside) return;
        if (i.R.Kind != 0) return;
        instrs++;
        string cpu = Ctx.CpuId(i.Cpu);
        byCpu[cpu] = byCpu.GetValueOrDefault(cpu) + 1;
        string fn = (Ctx.Machine.Cpus.Count > 1 ? cpu + ":" : "") + FunctionNow(in i, stacks);
        byFn[fn] = byFn.GetValueOrDefault(fn) + 1;
        if (i.Cpu == 0) { if (startCycle < 0) startCycle = i.R.Cycle; lastCycle = i.R.Cycle; }
    }

    public override void OnAccess(in ProfAccess x) { if (inside && x.Write) writes++; }
    public override void OnCall(in ProfCall c) { stacks.On(in c); if (inside && !c.Return) calls++; }
    public override void OnVar(in ProfVar v)
    {
        if (!inside || vars.Count >= Limit) return;
        var info = Ctx.Vars[v.Index];
        vars.Add($"{info.Name}{(v.Byte >= 0 ? $"+{v.Byte}" : "")}: {Hex(v.Old)} -> {Hex(v.New)} (frame {v.Frame}, #{v.Instr}, {Ctx.SymbolAtAddress(v.Cpu, v.Pc) ?? Ctx.Address(v.Cpu, v.Pc)})");
    }

    protected override void Answer(JsonObject j)
    {
        j["from"] = a; j["to"] = b;
        if (startSeq < 0) { j["error"] = $"'{a}' was not seen in the profiled span (markers are the debug port's marker texts, #N an instruction, frame:F a frame's start)"; return; }
        j["fromAt"] = startSeq; j["fromFrame"] = startFrame;
        j["toAt"] = closed ? endSeq : null; j["toFrame"] = closed ? endFrame : null;
        if (!closed) j["note"] = $"'{b}' was not seen after '{a}': counted to the span's end";
        j["instructions"] = instrs;
        j["byCpu"] = new JsonObject(byCpu.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        j["cycles"] = startCycle >= 0 ? lastCycle - startCycle : 0;
        j["calls"] = calls; j["writes"] = writes;
        j["functions"] = new JsonObject(byFn.OrderByDescending(kv => kv.Value).Take(Limit).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        if (markers.Count > 0) j["markersInside"] = new JsonArray(markers.Take(Limit).Select(m => (JsonNode)m).ToArray());
        if (vars.Count > 0) j["variables"] = vars;
    }
}

// ================================================================================================================================ became / when

/// <summary>became &lt;name&gt; &lt;op&gt; &lt;value&gt;: the first instruction that made a register or a named variable satisfy a comparison.</summary>
public sealed class BecameQuery : ProfileQuery
{
    public string CpuName = "main";
    private string name = "", op = "==";
    private long value;
    private int cpu, varIndex = -1;
    private bool? before;
    private ProfInstr prev; private bool havePrev;
    private JsonObject? hit;
    private string already = "";

    protected override void Init()
    {
        var m = Regex.Match(string.Join(" ", Args), @"^\s*(\[?[A-Za-z_@.][\w@.]*\]?)\s*(==|!=|<=|>=|=|<|>)\s*(\S+)\s*$");
        if (!m.Success) throw new FormatException("'became' needs <register or variable> <op> <value>, e.g. 'became A == 3' or 'became hp = 0'");
        name = m.Groups[1].Value.Trim('[', ']'); op = m.Groups[2].Value == "=" ? "==" : m.Groups[2].Value; value = Num(m.Groups[3].Value);
    }

    private bool Holds(long v) => op switch { "==" => v == value, "!=" => v != value, "<" => v < value, "<=" => v <= value, ">" => v > value, _ => v >= value };

    protected override void Setup()
    {
        cpu = Ctx.CpuIndex(CpuName);
        for (int i = 0; i < Ctx.Vars.Count; i++) if (Ctx.Vars[i].Name.Equals(name, StringComparison.Ordinal)) { varIndex = i; break; }
        if (varIndex < 0) for (int i = 0; i < Ctx.Vars.Count; i++) if (Ctx.Vars[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { varIndex = i; break; }
        if (varIndex >= 0 && Ctx.Vars[varIndex].Size > 4) throw new ArgumentException($"'{name}' is {Ctx.Vars[varIndex].Size} bytes: 'became' compares variables of 1 to 4 bytes (use 'writes {name}+N')");
        if (varIndex < 0 && !Ctx.RegisterNames(cpu).Any(r => r.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"'{name}' is neither a register of the {Ctx.Cpu(cpu).Name} ({string.Join(", ", Ctx.RegisterNames(cpu))}) nor a variable the profile follows (a data symbol; {Ctx.Vars.Count} followed)");
    }

    protected override bool IsDone => hit != null;

    public override void OnInstr(in ProfInstr i)
    {
        if (Error != null || hit != null || varIndex >= 0 || i.Cpu != cpu) return;
        if (!Ctx.TryRegister(in i, name, out long v)) return;
        bool now = Holds(v);
        if (before == null) { before = now; if (now) already = $"{name} = {Hex(v)} already holds at the span's first instruction of the {Ctx.Cpu(cpu).Name} (#{i.Seq}, frame {i.Frame})"; }
        else if (now && before == false)
        {
            hit = havePrev ? InstrJson(in prev, true) : new JsonObject();
            hit["value"] = Hex(v);
            hit["seenAt"] = i.Seq;
        }
        before = now; prev = i; havePrev = true;
    }

    public override void OnVar(in ProfVar v)
    {
        if (Error != null || hit != null || v.Index != varIndex || v.Byte >= 0) return;
        bool was = Holds(v.Old), now = Holds(v.New);
        if (before == null && was) { already = $"{name} = {Hex(v.Old)} already holds when the profile first saw it change (frame {v.Frame})"; before = true; }
        if (now && !was)
        {
            hit = new JsonObject
            {
                ["at"] = v.Instr, ["frame"] = v.Frame, ["cycle"] = v.Cycle, ["cpu"] = Ctx.CpuId(v.Cpu), ["pc"] = Ctx.Address(v.Cpu, v.Pc),
                ["in"] = Ctx.SymbolAtAddress(v.Cpu, v.Pc), ["old"] = Hex(v.Old), ["new"] = Hex(v.New),
            };
        }
        before = now;
    }

    protected override void Answer(JsonObject j)
    {
        j["what"] = $"{name} {op} {Hex(value)}"; j["kind"] = varIndex >= 0 ? "variable" : "register";
        j["made"] = hit;
        if (hit == null) j["note"] = already.Length > 0 ? already + ", and it never became so again from false in the span" : "it never became so in the profiled span";
        else if (already.Length > 0) j["note"] = already;
    }
}

/// <summary>when &lt;expression&gt;: the first instruction boundary at which a debugger expression holds (evaluated on the running machine).</summary>
public sealed class WhenQuery : ProfileQuery
{
    private string expr = "";
    private DebugExpr? e;
    private readonly ExprContext ec = new();
    private bool? before;
    private ProfInstr prev; private bool havePrev;
    private JsonObject? hit;
    protected override void Init() => expr = string.Join(" ", Args).Trim() is { Length: > 0 } s ? s : throw new FormatException("'when' needs an expression ([hp] == 0 && A == 3)");
    protected override void Setup()
    {
        if (!Ctx.Live) throw new InvalidOperationException("'when' evaluates an expression on the running machine: it needs a re-run of the span (timeflow profile), not a kept profile");
        e = DebugExpr.Compile(expr, Ctx.Cpu(0));
    }
    protected override bool IsDone => hit != null;
    public override void OnInstr(in ProfInstr i)
    {
        if (Error != null || hit != null || i.Cpu != 0) return;
        ec.Address = i.R.Pc;
        bool now = e!.IsTrue(ec);
        if (before == null) before = now;
        else if (now && before == false) { hit = havePrev ? InstrJson(in prev, true) : new JsonObject(); hit["seenAt"] = i.Seq; hit["seenFrame"] = i.Frame; }
        before = now; prev = i; havePrev = true;
    }
    protected override void Answer(JsonObject j)
    {
        j["expression"] = expr;
        j["madeTrueBy"] = hit;
        if (hit == null) j["note"] = before == true ? "it held from the span's first instruction and never became true again from false" : "it never held in the profiled span";
    }
}

// ================================================================================================================================ histogram

/// <summary>histogram: per function (the code symbol containing the PC, else the 256-byte page), instructions exclusive and inclusive, cycles, calls.</summary>
public sealed class HistogramQuery : ProfileQuery
{
    public string CpuName = "main";
    public int Top = 25;
    private int cpu;
    private readonly Dictionary<string, long> excl = new(), incl = new(), cyc = new(), calls = new();
    private long count, lastCycle = -1;
    private string? lastFn;
    private readonly List<(string fn, long start)> open = new();
    private readonly Dictionary<string, int> onStack = new();
    private bool seeded;

    protected override void Setup() => cpu = Ctx.CpuIndex(CpuName);

    private string Fn(uint target)
    {
        string? s = Ctx.SymbolAtAddress(cpu, target);
        if (s == null) return Ctx.Address(cpu, target);
        int plus = s.IndexOf("+$", StringComparison.Ordinal);
        return plus > 0 ? s[..plus] : s;
    }

    private void Push(string fn) { open.Add((fn, count)); onStack[fn] = onStack.GetValueOrDefault(fn) + 1; }
    private void Pop(int i)
    {
        var (fn, start) = open[i];
        open.RemoveAt(i);
        int d = onStack[fn] - 1; onStack[fn] = d;
        if (d == 0) incl[fn] = incl.GetValueOrDefault(fn) + (count - start);
    }

    public override void OnStack(int c, IReadOnlyList<ProfCall> frames)
    {
        if (Error != null || c != cpu || seeded) return;
        seeded = true;
        foreach (var f in frames) Push(Fn(f.Target));
    }

    public override void OnCall(in ProfCall c)
    {
        if (Error != null || c.Cpu != cpu) return;
        string fn = Fn(c.Target);
        if (!c.Return) { Push(fn); calls[fn] = calls.GetValueOrDefault(fn) + 1; return; }
        for (int i = open.Count - 1; i >= 0; i--) if (open[i].fn == fn) { Pop(i); break; }
    }

    public override void OnInstr(in ProfInstr i)
    {
        if (Error != null || i.Cpu != cpu || i.R.Kind != 0) return;
        if (lastFn != null && lastCycle >= 0) cyc[lastFn] = cyc.GetValueOrDefault(lastFn) + (i.R.Cycle - lastCycle);
        string fn = open.Count > 0 ? open[^1].fn : Ctx.FunctionOf(in i) ?? "(no symbol) page " + Ctx.Address(cpu, i.R.Pc & ~0xFFu);
        excl[fn] = excl.GetValueOrDefault(fn) + 1;
        count++;
        lastFn = fn; lastCycle = i.R.Cycle;
    }

    public override void End() { while (open.Count > 0) Pop(open.Count - 1); }

    protected override void Answer(JsonObject j)
    {
        j["cpu"] = Ctx.Cpu(cpu).Name; j["instructions"] = count; j["functions"] = excl.Count;
        j["cycleUnit"] = Ctx.Console == "nes" ? "CPU cycles" : Ctx.Console is "gb" or "gbc" ? "M-cycles" : Ctx.Console is "sms" or "gg" ? "Z80 T-states" : "master clocks";
        var rows = new JsonArray();
        foreach (var kv in excl.OrderByDescending(kv => kv.Value).Take(Top))
            rows.Add(new JsonObject
            {
                ["function"] = kv.Key, ["instructions"] = kv.Value, ["percent"] = Math.Round(100.0 * kv.Value / Math.Max(1, count), 2),
                ["inclusive"] = incl.TryGetValue(kv.Key, out long inc) ? inc : null, ["cycles"] = cyc.GetValueOrDefault(kv.Key), ["calls"] = calls.GetValueOrDefault(kv.Key),
            });
        j["top"] = rows;
        if (excl.Count > Top) j["more"] = $"{excl.Count - Top} more functions";
        j["note"] = "exclusive: instructions whose PC is in the function; inclusive: from each call's first instruction to its return (a call still running at the span's end counts to the end); a frame entered before the span counts from the span's start";
    }
}

// ================================================================================================================================ events (D8)

/// <summary>The video and sound chips' registers of a console family (the event view's register writes).</summary>
public static class ChipRegisters
{
    public static string Family(string console) => console switch { "nes" => "nes", "snes" or "widemess" => "snes", "gb" or "gbc" => "gb", "sms" or "gg" => "sms", _ => console };

    public static bool IsRegister(string family, uint a) => family switch
    {
        "nes" => a >= 0x2000 && a < 0x4020,
        "snes" => ((a >> 16) & 0x40) == 0 && ((a & 0xFFFF) is >= 0x2100 and < 0x2200 or >= 0x4200 and < 0x4380),
        "gb" => a >= 0xFF00 && a < 0xFF80 || a == 0xFFFF,
        "sms" => a >= 0x10000,                                                       // the Z80's I/O ports (VDP, PSG, pads) at $10000 + the port (T0)
        "md" => a is >= 0xA10000 and < 0xA14004 || a is >= 0xC00000 and < 0xE00000,  // the I/O chip, the Z80's bus and reset, TMSS, the VDP and the PSG
        _ => false,
    };
}

/// <summary>events &lt;F&gt;[-&lt;G&gt;]: the chip events of frames (register writes with scanline and dot, DMA runs as one line with their count, interrupts).</summary>
public sealed class EventsQuery : ProfileQuery
{
    public int Limit = 200;
    private long from, to;
    private string family = "";
    private readonly JsonArray lines = new();
    private long writes, dmaRuns, interrupts, dropped;
    private string? runKey; private int runCount; private string runHead = "";
    private bool past;

    protected override void Init()
    {
        string a = NeedArg(0, "a frame (or F-G)");
        if (a.Contains('-')) { var p = a.Split('-'); from = Num(p[0]); to = Num(p[1]); } else from = to = Num(a);
    }

    public override long LastFrameNeeded => to;
    protected override bool IsDone => past;
    protected override void Setup() { family = ChipRegisters.Family(Ctx.Machine.Console); if (to < Ctx.From || from > Ctx.To) throw new ArgumentException($"frames {from}-{to} are not in the profiled span {Ctx.From}-{Ctx.To}"); }

    private void Add(string s) { if (lines.Count < Limit) lines.Add(s); else dropped++; }
    private void Flush() { if (runKey == null) return; Add(runCount > 1 ? $"{runHead} x{runCount}" : runHead); runKey = null; runCount = 0; }

    public override void OnFrame(in ProfFrame f) { Flush(); if (f.Frame > to) past = true; }

    public override void OnInstr(in ProfInstr i)
    {
        if (Error != null || i.Frame < from || i.Frame > to || i.R.Kind == 0) return;
        Flush();
        interrupts++;
        Add($"frame {i.Frame} line {i.R.Scanline,3} dot {i.R.Dot,3}  {Ctx.CpuId(i.Cpu)} interrupt {(i.R.Kind == 1 ? "NMI" : i.R.Kind == 2 ? "IRQ" : "entry")} at {Ctx.Address(i.Cpu, i.R.Pc)}  #{i.Seq}");
    }

    public override void OnAccess(in ProfAccess a)
    {
        if (Error != null || !a.Write || a.Frame < from || a.Frame > to || a.Cpu != 0) return;
        bool dma = a.Source != AccessSource.Cpu, reg = ChipRegisters.IsRegister(family, a.Address);
        if (!dma && !reg) return;
        string where = $"frame {a.Frame} line {a.Scanline,3} dot {a.Dot,3}";
        string r = Ctx.Address(a.Cpu, a.Address);
        if (dma)
        {
            string key = $"{a.Source}:{a.Channel}:{(reg ? r : "mem")}";
            if (key == runKey) { runCount++; return; }
            Flush();
            dmaRuns++;
            runKey = key; runCount = 1;
            runHead = $"{where}  {a.Source}{(a.Channel >= 0 ? " ch" + a.Channel : "")} -> {(reg ? r : r + " (memory)")} first {Hex(a.Value)}  #{a.Instr}";
            return;
        }
        Flush();
        writes++;
        Add($"{where}  write {r}{(Ctx.SymbolOfTarget(in a) is { } s ? " (" + s + ")" : "")} = {Hex(a.Value)}  (pc {Ctx.Address(a.Cpu, a.Pc)})  #{a.Instr}");
    }

    public override void End() => Flush();

    protected override void Answer(JsonObject j)
    {
        j["frames"] = from == to ? $"{from}" : $"{from}-{to}";
        j["registerWrites"] = writes; j["dmaRuns"] = dmaRuns; j["interrupts"] = interrupts;
        j["lines"] = lines;
        if (dropped > 0) j["more"] = $"{dropped} more lines (limit {Limit})";
    }
}

// ================================================================================================================================ trace

/// <summary>trace &lt;N&gt; [before K] [after K]: trace lines around #N; trace frame &lt;F&gt; [count K]: the first K of a frame.</summary>
public sealed class TraceQuery : ProfileQuery
{
    public int Before = 10, After = 10, Count = 40;
    public string CpuName = "main";
    private long at = -1, frame = -1;
    private bool allCpus;
    private int cpu;
    private readonly Queue<string> ring = new();
    private readonly JsonArray lines = new();
    private int after = -1;
    private bool done;

    protected override void Init()
    {
        string a = NeedArg(0, "#N or 'frame F'");
        if (a.Equals("frame", StringComparison.OrdinalIgnoreCase)) frame = Num(NeedArg(1, "a frame"));
        else at = Num(a.TrimStart('#'));
        allCpus = Args.Any(x => x.Equals("all", StringComparison.OrdinalIgnoreCase));
    }

    protected override void Setup() => cpu = Ctx.CpuIndex(CpuName);
    protected override bool IsDone => done;
    public override long LastFrameNeeded => frame >= 0 ? frame : long.MaxValue;

    public override void OnInstr(in ProfInstr i)
    {
        if (Error != null || done) return;
        if (!allCpus && i.Cpu != cpu && i.Seq != at) return;
        if (frame >= 0)
        {
            if (i.Frame < frame) return;
            if (i.Frame > frame || lines.Count >= Count) { done = true; return; }
            lines.Add(Ctx.TraceLine(in i));
            return;
        }
        if (after >= 0)
        {
            if (after >= After) { done = true; return; }
            lines.Add(Ctx.TraceLine(in i)); after++;
            return;
        }
        if (i.Seq == at)
        {
            foreach (var l in ring) lines.Add(l);
            lines.Add("> " + Ctx.TraceLine(in i));
            after = 0;
            if (After == 0) done = true;
            return;
        }
        if (Before > 0)
        {
            ring.Enqueue(Ctx.TraceLine(in i));
            while (ring.Count > Before) ring.Dequeue();
        }
    }

    protected override void Answer(JsonObject j)
    {
        j["around"] = at >= 0 ? $"#{at}" : $"frame {frame}";
        if (lines.Count == 0) { j["error"] = at >= 0 ? $"instruction #{at} is not in the profiled span" : $"frame {frame} is not in the profiled span"; return; }
        j["columns"] = "PC, instruction, [address] = value, registers, V scanline, H dot, Fr frame, Cyc cycle, #position (as Mesen's trace logger, then the profile's own)";
        j["lines"] = lines;
    }
}

// ================================================================================================================================ variables, summary

/// <summary>vars [name]: the followed variables' changes.</summary>
public sealed class VarsQuery : ProfileQuery
{
    public int Limit = 50;
    private string? name;
    private int only = -1;
    private readonly JsonArray shown = new();
    private readonly Dictionary<string, long> counts = new();
    private long total;
    protected override void Init() { if (Args.Count > 0) name = Args[0]; }
    protected override void Setup()
    {
        if (name == null) return;
        for (int i = 0; i < Ctx.Vars.Count; i++) if (Ctx.Vars[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { only = i; break; }
        if (only < 0) throw new ArgumentException($"'{name}' is not a variable the profile follows ({Ctx.Vars.Count} followed: data symbols of the loaded symbol files)");
    }
    public override void OnVar(in ProfVar v)
    {
        if (Error != null || only >= 0 && v.Index != only) return;
        total++;
        var info = Ctx.Vars[v.Index];
        string n = info.Name + (v.Byte >= 0 ? $"+{v.Byte}" : "");
        counts[info.Name] = counts.GetValueOrDefault(info.Name) + 1;
        if (shown.Count < Limit)
            shown.Add(new JsonObject
            {
                ["var"] = n, ["old"] = Hex(v.Old), ["new"] = Hex(v.New), ["frame"] = v.Frame, ["at"] = v.Instr, ["cycle"] = v.Cycle,
                ["pc"] = Ctx.Address(v.Cpu, v.Pc), ["in"] = Ctx.SymbolAtAddress(v.Cpu, v.Pc),
            });
    }
    protected override void Answer(JsonObject j)
    {
        j["followed"] = Ctx.Vars.Count; j["changes"] = total;
        j["byVariable"] = new JsonObject(counts.OrderByDescending(kv => kv.Value).Take(40).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        j["shown"] = shown;
        if (total > shown.Count) j["more"] = $"{total - shown.Count} more (limit {Limit})";
        if (Ctx.Vars.Count == 0) j["note"] = "no variables were followed: load the game's symbols (a data symbol is a variable)";
    }
}

/// <summary>summary: what the span holds.</summary>
public sealed class SummaryQuery : ProfileQuery
{
    private long frames, interrupts, reads, writes, dma, calls, vars, markers;
    private readonly Dictionary<string, long> byCpu = new();
    private readonly Dictionary<long, long> perFrame = new();
    public override void OnFrame(in ProfFrame f) => frames++;
    public override void OnInstr(in ProfInstr i)
    {
        if (i.R.Kind != 0) { interrupts++; return; }
        string c = Ctx.CpuId(i.Cpu);
        byCpu[c] = byCpu.GetValueOrDefault(c) + 1;
        if (i.Cpu == 0) perFrame[i.Frame] = perFrame.GetValueOrDefault(i.Frame) + 1;
    }
    public override void OnAccess(in ProfAccess a) { if (a.Source != AccessSource.Cpu) dma++; else if (a.Write) writes++; else reads++; }
    public override void OnCall(in ProfCall c) { if (!c.Return) calls++; }
    public override void OnVar(in ProfVar v) => vars++;
    public override void OnMarker(in ProfMarker m) => markers++;
    protected override void Answer(JsonObject j)
    {
        j["frames"] = frames; j["span"] = $"{Ctx.From}-{Ctx.To}";
        j["instructions"] = new JsonObject(byCpu.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        j["interrupts"] = interrupts; j["reads"] = reads; j["writes"] = writes; j["dmaBytes"] = dma; j["calls"] = calls; j["variableChanges"] = vars; j["markers"] = markers;
        if (perFrame.Count > 0)
        {
            var busiest = perFrame.OrderByDescending(kv => kv.Value).First();
            j["mainPerFrame"] = new JsonObject { ["min"] = perFrame.Values.Min(), ["average"] = Math.Round(perFrame.Values.Average(), 1), ["max"] = busiest.Value, ["busiestFrame"] = busiest.Key };
        }
        j["cpus"] = new JsonArray(Ctx.Machine.Cpus.Select(c => (JsonNode)$"{c.Id} ({c.Name})").ToArray());
    }
}
