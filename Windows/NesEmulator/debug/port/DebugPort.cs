using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace NesEmulator.Debugging.Port;

/// <summary>What a debug-port message is.</summary>
public enum PortEventKind { Print, Assert, Marker, TimerStart, TimerStop, Break, Error }

/// <summary>One message of the debug port: what, when (frame, scanline, cycle) and where (the PC of the instruction that sent it, its symbol).</summary>
public sealed class PortEvent
{
    /// <summary>1, 2, 3...: the order the port saw them in (for "the events since").</summary>
    public long Seq { get; init; }
    public PortEventKind Kind { get; init; }
    public string Text { get; init; } = "";
    public long Frame { get; init; }
    public int Scanline { get; init; }
    public long Cycle { get; init; }
    public uint Pc { get; init; }
    public string? PcSymbol { get; init; }
    /// <summary>The PC's text in the console's digits ("$C123", "$80A0F2").</summary>
    public string PcText { get; init; } = "";

    public string KindName => Name(Kind);
    public string Place => PcSymbol != null ? $"{PcSymbol} ({PcText})" : PcText;

    public static string Name(PortEventKind k) => k switch
    {
        PortEventKind.Print => "print", PortEventKind.Assert => "assert", PortEventKind.Marker => "marker",
        PortEventKind.TimerStart => "timer-start", PortEventKind.TimerStop => "timer-stop", PortEventKind.Break => "break", _ => "error",
    };

    public override string ToString() => $"[port] frame {Frame} {KindName}: {Text} (at {Place})";

    public JsonObject Json() => new()
    {
        ["seq"] = Seq, ["kind"] = KindName, ["text"] = Text, ["frame"] = Frame, ["scanline"] = Scanline, ["cycle"] = Cycle,
        ["pc"] = PcText, ["pcSymbol"] = PcSymbol,
    };
}

/// <summary>A named timer: calls, cycles per call (total, average, best, worst) and per frame (the worst frame's sum).</summary>
public sealed class PortTimer
{
    public PortTimer(string name) { Name = name; }
    public string Name { get; }
    public long Calls, Total, Best = long.MaxValue, Worst, WorstFrame = -1;
    /// <summary>The most cycles one frame spent in the timer (all its calls that frame), and that frame.</summary>
    public long FrameWorst, FrameWorstFrame = -1;
    /// <summary>Stops without a start, starts while already running (the earlier start is dropped).</summary>
    public long Unmatched;
    internal long started = -1, frameNow = -1, frameSum;
    public bool Running => started >= 0;
    public double Average => Calls > 0 ? (double)Total / Calls : 0;
    /// <summary>Frames that had at least one call.</summary>
    public long FramesWithCalls;

    internal void Stop(long cycle, long frame)
    {
        long d = cycle - started;
        started = -1;
        if (d < 0) d = 0;
        Calls++; Total += d;
        if (d < Best) Best = d;
        if (d > Worst) { Worst = d; WorstFrame = frame; }
        if (frame != frameNow) { frameNow = frame; frameSum = 0; FramesWithCalls++; }
        frameSum += d;
        if (frameSum > FrameWorst) { FrameWorst = frameSum; FrameWorstFrame = frame; }
    }

    public JsonObject Json() => new()
    {
        ["name"] = Name, ["calls"] = Calls, ["total"] = Total, ["average"] = Math.Round(Average, 1), ["best"] = Calls > 0 ? Best : 0, ["worst"] = Worst,
        ["worstFrame"] = WorstFrame, ["frameWorst"] = FrameWorst, ["frameWorstFrame"] = FrameWorstFrame, ["framesWithCalls"] = FramesWithCalls,
        ["unmatched"] = Unmatched, ["running"] = Running,
    };
}

/// <summary>
/// The BrokenDev debug port (milestone D7 of docs/projects/brokendev-debugging-plan.md, "D7 settled"): print, assert, named timers, markers
/// and breaks that a homebrew game sends, on the NES ($401C-$401F), the SNES and WideMess ($4230-$423F) and the Game Boy (the bgb / Emulicious
/// <c>ld d,d</c> messages and the <c>ld b,b</c> breakpoint). <see cref="Attach"/> puts the port's listener on the machine (a field that is
/// otherwise null: with the port off the machine is exactly as before); the port only listens, it never answers a read or changes the machine,
/// so a game runs frame-identically with it on. Events go to <see cref="Log"/>, <see cref="Event"/>, the kept lists and the timers.
/// </summary>
public sealed class DebugPort : IDisposable
{
    public DebugMachine Machine { get; }
    public Debugger? Debugger { get; }
    private readonly DebugCpu cpu;
    private readonly string family;   // nes, snes, gb

    public DebugPort(DebugMachine machine, Debugger? debugger = null)
    {
        Machine = machine; Debugger = debugger; cpu = machine.Main;
        family = machine switch { NesDebugMachine => "nes", SnesDebugMachine => "snes", GbDebugMachine => "gb", _ => throw new NotSupportedException($"the debug port does not cover the {machine.Console}") };
    }

    /// <summary>The port's registers on this console, in words.</summary>
    public string Where => family switch
    {
        "nes" => "$401C-$401F (DATA, COMMAND, TIMER START, TIMER STOP)",
        "snes" => "$4230-$423F of banks $00-$3F / $80-$BF (DATA $4230-$4231, COMMAND $4232, TIMER START $4234, TIMER STOP $4236)",
        _ => "the bgb / Emulicious debug messages (ld d,d; jr; dw $6464) and the ld b,b breakpoint",
    };

    /// <summary>The timers' unit: the machine's cycle (as the D4 profiler counts).</summary>
    public string CycleUnit => family switch { "nes" => "CPU cycles", "snes" => "master clocks", _ => "M-cycles" };

    // ================================================================================================ attach

    private Bus? nesBus;
    private Snes.BOARD_SFC? snesBoard;
    private Gb.BOARD_GB? gbBoard;
    private Action<ushort, byte>? nesMine;
    private Action<uint, byte>? snesMine;
    private Action<ushort>? gbMine;
    public bool Attached { get; private set; }

    /// <summary>Starts listening. NES: on the bus the NES has now (a ROM load makes a new one: attach again). Several ports may listen to
    /// one machine at once (a session's own and the checks'): each adds itself to the machine's listener and removes only itself.</summary>
    public void Attach()
    {
        if (Attached) Detach();
        switch (Machine)
        {
            case NesDebugMachine n:
                nesBus = n.Nes.DebugBus ?? throw new InvalidOperationException("the NES has no ROM loaded");
                nesMine = OnNesWrite;
                nesBus.DebugPortWrite += nesMine;
                break;
            case SnesDebugMachine s:
                snesBoard = s.Board;
                snesMine = OnSnesWrite;
                snesBoard.DebugPortWrite += snesMine;
                break;
            case GbDebugMachine g:
                gbBoard = g.Board;
                gbMine = OnGbInstruction;
                gbBoard.InstructionHook += gbMine;   // after any hook the host had (a multicast delegate: the host's own stays)
                break;
        }
        Attached = true;
    }

    /// <summary>Stops listening: with no other listener the machine is exactly as without the port again (the fields are null).</summary>
    public void Detach()
    {
        if (nesBus != null) { nesBus.DebugPortWrite -= nesMine; nesBus = null; nesMine = null; }
        if (snesBoard != null) { snesBoard.DebugPortWrite -= snesMine; snesBoard = null; snesMine = null; }
        if (gbBoard != null) { gbBoard.InstructionHook -= gbMine; gbBoard = null; gbMine = null; }
        Attached = false;
    }

    public void Dispose() => Detach();

    // ================================================================================================ what the host sees

    /// <summary>Each event as a line ("[port] frame 12 print: room 3 (at load_room ($C123))"); prints only when <see cref="LogPrints"/>.</summary>
    public TextWriter? Log { get; set; }
    public bool LogPrints { get; set; } = true;
    /// <summary>An assert parks the debugger (when there is one) with the message: what an interactive session wants. Off in headless runs.</summary>
    public bool BreakOnAssert { get; set; }
    /// <summary>The break command (and a bare <c>ld b,b</c>) parks the debugger.</summary>
    public bool BreakOnBreak { get; set; }
    /// <summary>Called on the emulation thread for every event.</summary>
    public event Action<PortEvent>? Event;
    /// <summary>How many recent events are kept (asserts and markers are kept apart, up to 1000 each).</summary>
    public int Keep { get; set; } = 4096;

    private readonly Queue<PortEvent> recent = new();
    private readonly List<PortEvent> asserts = new(), markers = new();
    private readonly long[] counts = new long[7];
    private readonly Dictionary<string, PortTimer> timers = new(StringComparer.Ordinal);
    private readonly string?[] timerNames = new string?[256];
    private long seq;
    private readonly object sync = new();

    public long Writes { get; private set; }
    public long EventCount { get { lock (sync) return seq; } }
    public long Count(PortEventKind k) { lock (sync) return counts[(int)k]; }
    public IReadOnlyList<PortEvent> Events { get { lock (sync) return recent.ToArray(); } }
    public IReadOnlyList<PortEvent> Asserts { get { lock (sync) return asserts.ToArray(); } }
    public IReadOnlyList<PortEvent> Markers { get { lock (sync) return markers.ToArray(); } }
    public IReadOnlyList<PortEvent> Since(long afterSeq) { lock (sync) return recent.Where(e => e.Seq > afterSeq).ToArray(); }
    public IReadOnlyList<PortTimer> Timers { get { lock (sync) return timers.Values.OrderByDescending(t => t.Total).ToArray(); } }
    public PortTimer? Timer(string name) { lock (sync) return timers.TryGetValue(name, out var t) ? t : null; }

    /// <summary>Forgets every event and timer (a new run on the same machine).</summary>
    public void Clear()
    {
        lock (sync) { recent.Clear(); asserts.Clear(); markers.Clear(); Array.Clear(counts); timers.Clear(); seq = 0; }
        buf.Clear(); Writes = 0;
    }

    public JsonObject Json(int lastEvents = 200) => new()
    {
        ["console"] = Machine.Console, ["where"] = Where, ["cycleUnit"] = CycleUnit, ["writes"] = Writes,
        ["counts"] = new JsonObject
        {
            ["print"] = Count(PortEventKind.Print), ["assert"] = Count(PortEventKind.Assert), ["marker"] = Count(PortEventKind.Marker),
            ["timerStart"] = Count(PortEventKind.TimerStart), ["timerStop"] = Count(PortEventKind.TimerStop), ["break"] = Count(PortEventKind.Break), ["error"] = Count(PortEventKind.Error),
        },
        ["asserts"] = new JsonArray(Asserts.Select(e => (JsonNode)e.Json()).ToArray()),
        ["markers"] = new JsonArray(Markers.Select(e => (JsonNode)e.Json()).ToArray()),
        ["timers"] = new JsonArray(Timers.Select(t => (JsonNode)t.Json()).ToArray()),
        ["events"] = new JsonArray(Events.TakeLast(lastEvents).Select(e => (JsonNode)e.Json()).ToArray()),
    };

    /// <summary>The timers as text lines (Workshop, the profile).</summary>
    public IEnumerable<string> TimerText()
    {
        var list = Timers;
        if (list.Count == 0) yield break;
        yield return $"{"timer (" + CycleUnit + ")",-32} {"calls",8} {"average",10} {"best",9} {"worst",9} {"worst frame",12} {"per frame max",14}";
        foreach (var t in list)
            yield return $"{(t.Name.Length > 32 ? t.Name[..32] : t.Name),-32} {t.Calls,8} {t.Average,10:F0} {(t.Calls > 0 ? t.Best : 0),9} {t.Worst,9} {t.WorstFrame,12} {t.FrameWorst,14}"
                         + (t.Unmatched > 0 ? $"  ({t.Unmatched} unmatched)" : "") + (t.Running ? "  (running)" : "");
    }

    // ================================================================================================ events

    private void Emit(PortEventKind kind, string text, uint pc)
    {
        PortEvent e;
        string? sym = Debugger?.SymbolAt(cpu, pc);
        lock (sync)
        {
            e = new PortEvent
            {
                Seq = ++seq, Kind = kind, Text = text, Frame = Machine.Frame, Scanline = Machine.Scanline, Cycle = Machine.Cycle, Pc = pc,
                PcSymbol = sym, PcText = cpu.FormatAddress(pc),
            };
            counts[(int)kind]++;
            recent.Enqueue(e);
            while (recent.Count > Math.Max(1, Keep)) recent.Dequeue();
            if (kind == PortEventKind.Assert && asserts.Count < 1000) asserts.Add(e);
            if (kind == PortEventKind.Marker && markers.Count < 1000) markers.Add(e);
        }
        if (Log != null && (kind != PortEventKind.Print || LogPrints) && kind is not (PortEventKind.TimerStart or PortEventKind.TimerStop)) Log.WriteLine(e.ToString());
        Event?.Invoke(e);
        var d = Debugger;
        if (d != null && ((kind == PortEventKind.Assert && BreakOnAssert) || (kind == PortEventKind.Break && BreakOnBreak)))
            d.Pause(kind == PortEventKind.Assert ? "assert" : "port-break", kind == PortEventKind.Assert ? $"assert failed: {text} (at {e.Place}, frame {e.Frame})" : $"debug-port break: {text} (at {e.Place}, frame {e.Frame})");
    }

    private void TimerStart(string name, uint pc)
    {
        long c = Machine.Cycle;
        lock (sync)
        {
            if (!timers.TryGetValue(name, out var t)) timers[name] = t = new PortTimer(name);
            if (t.Running) t.Unmatched++;
            t.started = c;
            counts[(int)PortEventKind.TimerStart]++;
        }
    }

    private void TimerStop(string name, uint pc)
    {
        long c = Machine.Cycle, f = Machine.Frame;
        bool unmatched = false;
        lock (sync)
        {
            if (!timers.TryGetValue(name, out var t)) timers[name] = t = new PortTimer(name);
            if (!t.Running) { t.Unmatched++; unmatched = true; }
            else t.Stop(c, f);
            counts[(int)PortEventKind.TimerStop]++;
        }
        if (unmatched) Emit(PortEventKind.Error, $"timer '{name}' stopped without a start", pc);
    }

    private string TimerName(int id) => timerNames[id] ?? (id == 0 ? "timer 0" : "timer " + id.ToString(CultureInfo.InvariantCulture));

    // ================================================================================================ NES and SNES: the register protocol

    private readonly List<byte> buf = new(256);
    private bool overflowed;
    private const int MaxBuffer = 255;

    private void OnNesWrite(ushort address, byte value)
    {
        Writes++;
        switch (address)
        {
            case 0x401C: Data(value); break;
            case 0x401D: Command(value, StorePc(address)); break;
            case 0x401E: TimerStart(TimerName(value), StorePc(address)); break;
            case 0x401F: TimerStop(TimerName(value), StorePc(address)); break;
        }
    }

    private void OnSnesWrite(uint address, byte value)
    {
        Writes++;
        switch (address & 0xFFFF)
        {
            case 0x4230: case 0x4231: Data(value); break;
            case 0x4232: Command(value, StorePc(address)); break;
            case 0x4234: TimerStart(TimerName(value), StorePc(address)); break;
            case 0x4236: TimerStop(TimerName(value), StorePc(address)); break;
            case 0x4233: case 0x4235: case 0x4237: break;   // the high byte of a 16-bit store
            default: Emit(PortEventKind.Error, $"a write to the reserved debug-port register ${address & 0xFFFF:X4} (value ${value:X2})", StorePc(address)); break;
        }
    }

    private void Data(byte value)
    {
        if (buf.Count >= MaxBuffer) { overflowed = true; return; }
        buf.Add(value);
    }

    private void Command(byte cmd, uint pc)
    {
        var data = buf.ToArray();
        bool over = overflowed;
        buf.Clear(); overflowed = false;
        if (over) Emit(PortEventKind.Error, $"the message passed {MaxBuffer} bytes (cut there)", pc);
        bool pointer = (cmd & 0x80) != 0;
        int op = cmd & 0x7F;
        int at = 0;
        string text = "";
        if (op is >= 1 and <= 5)
        {
            if (pointer)
            {
                int n = family == "snes" ? 3 : 2;
                if (data.Length < n) { Emit(PortEventKind.Error, $"command ${cmd:X2} wants a {n}-byte pointer, got {data.Length} bytes", pc); return; }
                uint p = (uint)(data[0] | data[1] << 8 | (n == 3 ? data[2] << 16 : 0));
                text = ReadText(p, family == "snes");
                at = n;
            }
            else
            {
                int z = Array.IndexOf(data, (byte)0);
                text = Latin(data, 0, z < 0 ? data.Length : z);
                at = z < 0 ? data.Length : z + 1;
            }
        }
        switch (op)
        {
            case 0x01: Emit(PortEventKind.Print, Format(text, data, at, pc), pc); break;
            case 0x02: Emit(PortEventKind.Assert, Format(text, data, at, pc), pc); break;
            case 0x03: Emit(PortEventKind.Marker, Format(text, data, at, pc), pc); break;
            case 0x04: TimerStart(text, pc); break;
            case 0x05: TimerStop(text, pc); break;
            case 0x06: Emit(PortEventKind.Break, data.Length > 0 ? Latin(data, 0, data.Length) : "break", pc); break;
            case 0x07:
            {
                if (data.Length < 2) { Emit(PortEventKind.Error, "name timer wants the timer's number and a name", pc); break; }
                string name;
                if (pointer)
                {
                    int n = family == "snes" ? 3 : 2;
                    if (data.Length < 1 + n) { Emit(PortEventKind.Error, $"name timer (pointer) wants the timer's number and a {n}-byte pointer", pc); break; }
                    name = ReadText((uint)(data[1] | data[2] << 8 | (n == 3 ? data[3] << 16 : 0)), family == "snes");
                }
                else { int z = Array.IndexOf(data, (byte)0, 1); name = Latin(data, 1, (z < 0 ? data.Length : z) - 1); }
                timerNames[data[0]] = name;
                break;
            }
            case 0x08: break;   // clear: the buffer is already empty
            default: Emit(PortEventKind.Error, $"unknown debug-port command ${cmd:X2} ({data.Length} data bytes)", pc); break;
        }
    }

    private string ReadText(uint address, bool long24)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 255; i++)
        {
            uint a = long24 ? (address & 0xFF0000) | ((address + (uint)i) & 0xFFFF) : (uint)((address + i) & 0xFFFF);
            byte b = cpu.Peek(a);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static string Latin(byte[] d, int start, int count)
    {
        var sb = new StringBuilder(Math.Max(0, count));
        for (int i = start; i < start + count && i < d.Length; i++) sb.Append((char)d[i]);
        return sb.ToString();
    }

    /// <summary>The PC of the store that wrote the port: the CPU is past it when the write lands, so the store is looked for just before
    /// (an absolute or long store of that address; else the next instruction's PC is given).</summary>
    private uint StorePc(uint target)
    {
        uint next = cpu.PC;
        ushort t = (ushort)target;
        if (family == "nes")
        {
            uint s = (next - 3) & 0xFFFF;
            byte op = cpu.Peek(s);
            if (op is 0x8D or 0x8E or 0x8C or 0x9D or 0x99 && (cpu.Peek((s + 1) & 0xFFFF) | cpu.Peek((s + 2) & 0xFFFF) << 8) == t) return s;
            return next;
        }
        uint bank = next & 0xFF0000, pc16 = next & 0xFFFF;
        uint s4 = bank | ((pc16 - 4) & 0xFFFF);
        if (cpu.Peek(s4) is 0x8F or 0x9F && (cpu.Peek(bank | ((pc16 - 3) & 0xFFFF)) | cpu.Peek(bank | ((pc16 - 2) & 0xFFFF)) << 8) == t) return s4;
        uint s3 = bank | ((pc16 - 3) & 0xFFFF);
        if (cpu.Peek(s3) is 0x8D or 0x8E or 0x8C or 0x9C or 0x9D or 0x99 or 0x9E && (cpu.Peek(bank | ((pc16 - 2) & 0xFFFF)) | cpu.Peek(bank | ((pc16 - 1) & 0xFFFF)) << 8) == t) return s3;
        return next;
    }

    // ================================================================================================ the % fields

    /// <summary>
    /// The printf subset the libraries use: <c>%%</c>; <c>%c %hu %hd %hx %hX</c> one byte; <c>%u %d %x %X</c> two bytes (low first);
    /// <c>%lu %ld %lx %lX</c> four; a width and <c>0</c> padding before the letter (<c>%02hx</c>, <c>%5u</c>). A value the text wants but
    /// the message does not carry is shown as <c>?</c> and is an error event.
    /// </summary>
    private string Format(string text, byte[] args, int at, uint pc)
    {
        if (text.IndexOf('%') < 0)
        {
            if (at < args.Length) Emit(PortEventKind.Error, $"{args.Length - at} value byte(s) after a text with no % fields: \"{text}\"", pc);
            return text;
        }
        var sb = new StringBuilder();
        bool missing = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch != '%' || i + 1 >= text.Length) { sb.Append(ch); continue; }
            int j = i + 1;
            if (text[j] == '%') { sb.Append('%'); i = j; continue; }
            bool zero = false; int width = 0;
            if (text[j] == '0') { zero = true; j++; }
            while (j < text.Length && char.IsDigit(text[j])) { width = width * 10 + (text[j] - '0'); j++; }
            int size = 2;
            if (j < text.Length && text[j] == 'h') { size = 1; j++; }
            else if (j < text.Length && text[j] == 'l') { size = 4; j++; }
            if (j >= text.Length) { sb.Append(text, i, text.Length - i); break; }
            char conv = text[j];
            if (conv == 'c') size = 1;
            if (conv is not ('u' or 'd' or 'x' or 'X' or 'c')) { sb.Append(text, i, j - i + 1); i = j; continue; }
            if (at + size > args.Length) { sb.Append('?'); missing = true; i = j; continue; }
            long v = 0;
            for (int k = 0; k < size; k++) v |= (long)args[at + k] << (8 * k);
            at += size;
            if (conv == 'd') v = size == 1 ? (sbyte)v : size == 2 ? (short)v : (int)v;
            string s = conv switch
            {
                'c' => ((char)v).ToString(),
                'x' => v.ToString("x", CultureInfo.InvariantCulture),
                'X' => v.ToString("X", CultureInfo.InvariantCulture),
                _ => v.ToString(CultureInfo.InvariantCulture),
            };
            if (s.Length < width) s = zero && conv != 'c' ? (s.StartsWith('-') ? "-" + s[1..].PadLeft(width - 1, '0') : s.PadLeft(width, '0')) : s.PadLeft(width);
            sb.Append(s);
            i = j;
        }
        if (missing) Emit(PortEventKind.Error, $"the message \"{text}\" wants more values than it carries", pc);
        return sb.ToString();
    }

    // ================================================================================================ Game Boy: the bgb / Emulicious messages

    private Gb.CPU_GB GbCpu => (Gb.CPU_GB)gbBoard!.Core;
    private long gbZeroClks;
    private uint lastAssertPc = uint.MaxValue; private long lastAssertCycle = -1;

    private void OnGbInstruction(ushort pc)
    {
        var b = gbBoard;
        if (b == null) return;
        byte op = b.Peek(pc);
        if (op != 0x52 && op != 0x40) return;
        if (!cpu.WillExecute(out _)) return;   // halted, or an interrupt is taken first: the instruction runs later
        if (op == 0x40)
        {
            Writes++;
            // the assert macro's own ld b,b right after its message is part of the assert
            if (lastAssertCycle >= 0 && Machine.Cycle - lastAssertCycle < 64 && pc > lastAssertPc && pc - lastAssertPc < 600) { lastAssertCycle = -1; return; }
            Emit(PortEventKind.Break, "ld b,b", pc);
            return;
        }
        if (b.Peek((ushort)(pc + 1)) != 0x18 || b.Peek((ushort)(pc + 3)) != 0x64 || b.Peek((ushort)(pc + 4)) != 0x64) return;   // a plain ld d,d
        Writes++;
        int end = (ushort)(pc + 3 + (sbyte)b.Peek((ushort)(pc + 2)));
        int type = b.Peek((ushort)(pc + 5)) | b.Peek((ushort)(pc + 6)) << 8;
        string text;
        if (type == 0x0000)
        {
            var sb = new StringBuilder();
            for (int a = pc + 7; a < end && sb.Length < 512; a++) sb.Append((char)b.Peek((ushort)a));
            text = Expand(sb.ToString(), pc);
        }
        else if (type == 0x0200)
        {
            var c = GbCpu;
            text = GbPrintf(c.HL, c.DE, pc);
        }
        else { Emit(PortEventKind.Error, $"a debug message of type ${type:X4} (BrokenNes reads $0000 inline and $0200 printf)", pc); return; }
        Dispatch(text, pc);
    }

    /// <summary>The library's tags on the Game Boy (the message's text says what it is).</summary>
    private void Dispatch(string text, uint pc)
    {
        if (text.StartsWith("@assert", StringComparison.Ordinal)) { lastAssertPc = pc; lastAssertCycle = Machine.Cycle; Emit(PortEventKind.Assert, Rest(text, 7), pc); }
        else if (text.StartsWith("@mark", StringComparison.Ordinal)) Emit(PortEventKind.Marker, Rest(text, 5), pc);
        else if (text.StartsWith("@timer+", StringComparison.Ordinal)) TimerStart(TimerTag(text), pc);
        else if (text.StartsWith("@timer-", StringComparison.Ordinal)) TimerStop(TimerTag(text), pc);
        else if (text.StartsWith("@break", StringComparison.Ordinal)) Emit(PortEventKind.Break, Rest(text, 6) is { Length: > 0 } r ? r : "break", pc);
        else Emit(PortEventKind.Print, text, pc);
    }

    private static string Rest(string t, int n) => t.Length > n ? t[n..].Trim() : "";

    /// <summary>"@timer+ draw_hud 0" (the expanded %ZEROCLKS%) or "@timer- draw_hud 1234": the name is the first word.</summary>
    private static string TimerTag(string t)
    {
        string r = Rest(t, 7);
        int sp = r.IndexOf(' ');
        return sp < 0 ? r : r[..sp];
    }

    /// <summary>bgb's %expression% fields: registers (shown in hex), ZEROCLKS / LASTCLKS / TOTALCLKS (in bgb's clock: a NOP in double speed),
    /// ROMBANK, numbers, + and -, and (expr) for a byte of memory. Anything else is shown as written and is an error event.</summary>
    private string Expand(string s, uint pc)
    {
        if (s.IndexOf('%') < 0) return s;
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            int a = s.IndexOf('%', i);
            if (a < 0) { sb.Append(s, i, s.Length - i); break; }
            int z = s.IndexOf('%', a + 1);
            if (z < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, a - i);
            string expr = s.Substring(a + 1, z - a - 1);
            if (expr.Length == 0) sb.Append('%');
            else if (TryEval(expr, out long v, out bool hex)) sb.Append(hex ? "$" + v.ToString(v > 0xFF ? "X4" : "X2", CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture));
            else { sb.Append('%').Append(expr).Append('%'); Emit(PortEventKind.Error, $"unknown field %{expr}% in a debug message", pc); }
            i = z + 1;
        }
        return sb.ToString();
    }

    private long GbClocks => gbBoard!.CycleCount * (gbBoard.DoubleSpeed ? 1 : 2);

    private bool TryEval(string expr, out long value, out bool hex)
    {
        value = 0; hex = false;
        int p = 0;
        bool ok = Sum(expr.Replace(" ", ""), ref p, out value, out hex);
        return ok && p == expr.Replace(" ", "").Length;
    }

    private bool Sum(string e, ref int p, out long v, out bool hex)
    {
        v = 0; hex = false;
        int sign = 1; bool first = true;
        while (p < e.Length)
        {
            if (e[p] == '+') { sign = 1; p++; }
            else if (e[p] == '-') { sign = -1; p++; }
            else if (!first) break;
            if (!Term(e, ref p, out long t, out bool th)) return false;
            if (first) hex = th; else hex &= th;
            v += sign * t; sign = 1; first = false;
            if (p < e.Length && e[p] == ')') break;
        }
        return !first;
    }

    private bool Term(string e, ref int p, out long v, out bool hex)
    {
        v = 0; hex = false;
        if (p >= e.Length) return false;
        if (e[p] == '(')
        {
            p++;
            if (!Sum(e, ref p, out long a, out _) || p >= e.Length || e[p] != ')') return false;
            p++;
            v = gbBoard!.Peek((ushort)a); hex = true;
            return true;
        }
        if (e[p] == '$')
        {
            int s = ++p;
            while (p < e.Length && Uri.IsHexDigit(e[p])) p++;
            if (p == s) return false;
            v = long.Parse(e[s..p], NumberStyles.HexNumber, CultureInfo.InvariantCulture); hex = true;
            return true;
        }
        if (char.IsDigit(e[p]))
        {
            int s = p;
            while (p < e.Length && char.IsDigit(e[p])) p++;
            v = long.Parse(e[s..p], CultureInfo.InvariantCulture);
            return true;
        }
        int s2 = p;
        while (p < e.Length && char.IsLetterOrDigit(e[p])) p++;
        string name = e[s2..p].ToUpperInvariant();
        switch (name)
        {
            case "ZEROCLKS": gbZeroClks = GbClocks; v = 0; return true;
            case "LASTCLKS": v = GbClocks - gbZeroClks; return true;
            case "TOTALCLKS": v = GbClocks; return true;
            case "ROMBANK": { int i = gbBoard!.Cart.DebugRomIndex(0x4000); v = i < 0 ? 0 : i / 0x4000; return true; }
            case "ZERO" or "ZF" or "Z": v = (GbCpu.F & 0x80) != 0 ? 1 : 0; return true;
            case "CARRY" or "CY": v = (GbCpu.F & 0x10) != 0 ? 1 : 0; return true;
            case "IME": v = GbCpu.Ime ? 1 : 0; return true;
        }
        if (name.Length > 0 && cpu.TryGetRegister(name, out v)) { hex = true; return true; }
        return false;
    }

    /// <summary>GBDK's EMU_printf (Emulicious's printf message): the format at HL, the arguments at DE as SDCC pushes them: %hx %hu %hd one
    /// byte, %c %u %d %x two (a char is promoted), %s a two-byte pointer.</summary>
    private string GbPrintf(ushort fmtAt, ushort argsAt, uint pc)
    {
        var b = gbBoard!;
        var f = new StringBuilder();
        for (int i = 0; i < 255; i++) { byte c = b.Peek((ushort)(fmtAt + i)); if (c == 0) break; f.Append((char)c); }
        string fmt = f.ToString();
        var sb = new StringBuilder();
        int at = argsAt;
        for (int i = 0; i < fmt.Length; i++)
        {
            char ch = fmt[i];
            if (ch != '%' || i + 1 >= fmt.Length) { sb.Append(ch); continue; }
            int j = i + 1;
            if (fmt[j] == '%') { sb.Append('%'); i = j; continue; }
            bool zero = false; int width = 0;
            if (fmt[j] == '0') { zero = true; j++; }
            while (j < fmt.Length && char.IsDigit(fmt[j])) { width = width * 10 + (fmt[j] - '0'); j++; }
            bool h = j < fmt.Length && fmt[j] == 'h';
            if (h) j++;
            if (j >= fmt.Length) break;
            char conv = fmt[j];
            string s;
            if (conv == 's')
            {
                int p = b.Peek((ushort)at) | b.Peek((ushort)(at + 1)) << 8; at += 2;
                var t = new StringBuilder();
                for (int k = 0; k < 255; k++) { byte c = b.Peek((ushort)(p + k)); if (c == 0) break; t.Append((char)c); }
                s = t.ToString();
            }
            else if (conv is 'u' or 'd' or 'x' or 'X' or 'c')
            {
                int size = h && conv != 'c' ? 1 : 2;
                long v = b.Peek((ushort)at) | (size == 2 ? b.Peek((ushort)(at + 1)) << 8 : 0); at += size;
                if (conv == 'd') v = size == 1 ? (sbyte)v : (short)v;
                s = conv switch { 'c' => ((char)(v & 0xFF)).ToString(), 'x' => v.ToString("x"), 'X' => v.ToString("X"), _ => v.ToString(CultureInfo.InvariantCulture) };
            }
            else { sb.Append(fmt, i, j - i + 1); i = j; continue; }
            if (s.Length < width) s = zero ? s.PadLeft(width, '0') : s.PadLeft(width);
            sb.Append(s);
            i = j;
        }
        return sb.ToString();
    }
}
