// The x86 PC in BrokenNes's MCP server as a machine of its own (Windows 98 presets, .n98 packages, any PcMachinePreset), with an x86 debugger:
// exec / port / interrupt breakpoints, step into / over / out, run until, registers (segments, CRs, EFLAGS, mode), 16/32-bit disassembly,
// linear (paged) + physical memory, watches, an EBP-chain call stack, the trace ring, and a port I/O log + interrupt log (debug_port).
// Modelled on HeadlessSession.N64.cs: the debugger (I386.Debug.cs) is hung on the CPU only while something needs it (a breakpoint, a step,
// a log, open call_stack:true); then every instruction is interpreted. With nothing armed the CPU runs exactly as without the MCP.
//
// Reuse (the nano98 install tools): X86HeadlessSession.Open(...) opens a file; X86HeadlessSession.Wrap(id, pc, name, path) puts a PcSession
// made elsewhere under the MCP tools (screenshot, input keys / mouse, states, the debugger); Sessions.Add(it) then hands it out.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using NesEmulator;
using NesEmulator.Systems;
using NesEmulator.X86;

namespace BrokenNes.Mcp;

internal sealed class X86HeadlessSession : Session
{
    public override string Kind => "headless";
    public override string ConsoleKey => "x86";

    private readonly PcSession pc;
    private readonly string name, path;
    private readonly bool keepDebug;
    private X86Debug? dbg;
    private long framesDone;
    private bool midFrame;
    private PadButtons held;
    private readonly SortedDictionary<long, PadButtons> schedule = new();
    private readonly Dictionary<string, (byte[] state, long frames)> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> watches = new();
    private readonly short[] sbuf = new short[65536];
    private List<short>? rec;
    private (string domain, int width, List<int> cand, byte[] prev)? search;
    private sealed record Bp(int Id, string Kind, uint At, string Text) { public long Hits; }
    private readonly List<Bp> bps = new();
    private int nextBp = 1;
    private int tempBp = -1;

    private PcMachine M => pc.Machine;
    private I386 C => pc.Machine.Cpu;

    private X86HeadlessSession(string id, PcSession pc, string name, string path, bool keepDebug)
    {
        Id = id; this.pc = pc; this.name = name; this.path = path; this.keepDebug = keepDebug;
        if (keepDebug) DebugOn();
    }

    /// <summary>True when open_rom should come here: a .n98 package, console win98, or a machine preset named.</summary>
    public static bool Claims(string path, string? consoleKey, string? machine)
    {
        string k = (consoleKey ?? "").Trim().ToLowerInvariant();
        if (k is "win98" or "windows98") return true;
        if (!string.IsNullOrWhiteSpace(machine)) return true;
        return Path.GetExtension(path).Equals(".n98", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens a disk image / DOS program / .n98 package on a machine preset (default WIN98-P166 for a hard disk, the classic PC otherwise).</summary>
    public static X86HeadlessSession Open(string id, string file, string? machine, string? hdd2, string? cd, string? statePath, string cpu, string ppu, string apu, bool debug)
    {
        if (!File.Exists(file)) throw new ToolError($"no disk / package at '{file}'");
        byte[] img = file.EndsWith(".n98", StringComparison.OrdinalIgnoreCase) ? File.ReadAllBytes(file) : DiskImage.ReadImageFile(file);
        string mk = string.IsNullOrWhiteSpace(machine) ? (img.Length > 2949120 ? "WIN98-P166" : PcMachinePreset.ClassicKey) : machine!.Trim();
        if (mk.Equals("classic", StringComparison.OrdinalIgnoreCase)) mk = PcMachinePreset.ClassicKey;
        if (!PcMachinePreset.Known(mk)) throw new ToolError($"no x86 machine preset '{mk}' (e.g. WIN98-P166, WIN98-P166-NANO, WIN98-P2, DOS-486, classic)");
        Consoles.X86Enabled = true;
        PcSession s;
        try
        {
            s = new PcSession(img, Path.GetFileName(file), apu.Length > 0 ? apu : "ADLIB", cpu.Length > 0 ? cpu : "I386", ppu.Length > 0 ? ppu : "VGA", null, mk);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or FileNotFoundException or IOException) { throw new ToolError(e.Message); }
        if (!string.IsNullOrWhiteSpace(hdd2))
        {
            if (!File.Exists(hdd2)) throw new ToolError($"no second disk at '{hdd2}'");
            if (s.Machine.Ata == null) throw new ToolError($"machine {mk} has no IDE: no second hard disk");
            s.Machine.Hdd[1] = new DiskImage(DiskImage.ReadImageFile(hdd2!), false);
        }
        if (!string.IsNullOrWhiteSpace(cd))
        {
            if (!File.Exists(cd)) throw new ToolError($"no CD image at '{cd}'");
            s.InsertCd(CdImage.Open(cd!));
        }
        var h = new X86HeadlessSession(id, s, Path.GetFileName(file), Path.GetFullPath(file), debug);
        if (!string.IsNullOrWhiteSpace(statePath))
        {
            if (!File.Exists(statePath)) throw new ToolError($"no state at '{statePath}'");
            s.Prestart();
            var st = File.ReadAllBytes(statePath!);
            try { s.LoadState(st); }
            catch (Exception) { try { s.Machine.LoadState(st); s.Machine.Ata?.Secondary.ClearMediaChanged(0); } catch (Exception e) { throw new ToolError($"the state does not load on this machine: {e.Message}"); } }
        }
        return h;
    }

    /// <summary>Wraps a PcSession made elsewhere (the nano98 install tools) as an MCP session.</summary>
    public static X86HeadlessSession Wrap(string id, PcSession pc, string name, string path, bool debug = false) => new(id, pc, name, path, debug);

    // ================================================================================================ debugger on / off

    private void DebugOn()
    {
        if (dbg != null) return;
        dbg = new X86Debug();
        RebuildBps();
        C.Debug = dbg;
    }

    private void DebugOffIfIdle()
    {
        if (dbg == null || keepDebug || dbg.Stop || bps.Count > 0 || dbg.LogPorts || dbg.LogInts || midFrame) return;
        C.Debug = null; dbg = null;
    }

    private void RebuildBps()
    {
        if (dbg == null) return;
        dbg.Exec.Clear(); Array.Clear(dbg.Ports); Array.Clear(dbg.Vectors); dbg.PortCount = 0; dbg.VectorCount = 0;
        foreach (var b in bps)
            switch (b.Kind)
            {
                case "exec": dbg.Exec.Add(b.At); break;
                case "port_read": dbg.Ports[b.At] |= 1; dbg.PortCount++; break;
                case "port_write": dbg.Ports[b.At] |= 2; dbg.PortCount++; break;
                case "port_rw": dbg.Ports[b.At] |= 3; dbg.PortCount++; break;
                case "int": dbg.Vectors[b.At] |= 1; dbg.VectorCount++; break;
                case "irq": dbg.Vectors[b.At] |= 2; dbg.VectorCount++; break;
                case "exception": dbg.Vectors[b.At] |= 4; dbg.VectorCount++; break;
            }
    }

    private bool Parked => dbg?.Stop == true;

    // ================================================================================================ describe / status / close

    public override JsonObject Describe() => new()
    {
        ["session"] = Id, ["kind"] = Kind, ["console"] = ConsoleKey, ["consoleName"] = "x86 PC",
        ["rom"] = name, ["romPath"] = path, ["title"] = pc.Title, ["machine"] = pc.MachineKey, ["cores"] = $"{pc.Machine.Cpu.Model.Name}/{pc.PpuId}/{pc.ApuId}",
        ["description"] = pc.Description, ["gameId"] = pc.GameId, ["seat"] = pc.Seat != null ? pc.Seat.Title : null,
        ["debugger"] = true, ["debuggerOn"] = dbg != null, ["frame"] = framesDone, ["mouse"] = pc.HasMouse,
        ["cpus"] = J.Arr(new JsonNode[] { new JsonObject { ["id"] = "main", ["name"] = "x86 CPU", ["spaces"] = J.Strs(new[] { "linear", "phys" }) } }),
        ["domains"] = J.Strs(new[] { "linear", "phys" }), ["checks"] = false, ["states"] = true,
        ["limitations"] = J.Strs(Limitations),
    };

    private static readonly string[] Limitations =
    {
        "the debugger turns on with a breakpoint, a step, run_until, a debug_port log or open_rom call_stack:true; then every instruction is interpreted (no compiled blocks: slower); with nothing armed it is off and the CPU runs as without the MCP",
        "breakpoints: exec (cs:eip, seg:off, a linear address), port:N (kind read / write / rw: IN / OUT, string I/O too), int:N (software INT n), irq:N and exception:N (by vector); no memory watchpoints, conditions or hit filters",
        "the call stack is the EBP chain (best effort: code built without frame pointers shows nothing useful)",
        "no symbols for Windows binaries; no layers, tiles, sprites, D4 checks, step back, movies or Timeflow",
        "input: keys (x86boot syntax: text, {enter}, {alt+f4}, {f5}, {win}, {wait:N}) and mouse \"dx,dy[,buttons]\"; the pad buttons press the arrow / Ctrl / Alt / Enter keys",
    };

    public override JsonObject Status()
    {
        var j = Describe();
        j["framesRun"] = framesDone; j["parked"] = Parked; j["midFrame"] = midFrame;
        if (Parked) j["break"] = StopJson();
        j["held"] = held.ToString();
        j["stopped"] = M.Stopped;
        j["seatStatus"] = pc.SeatStatus;
        j["mode"] = ModeName();
        if (watches.Count > 0) j["watches"] = WatchValues();
        j["savedStates"] = J.Strs(states.Keys);
        return j;
    }

    public override void Close() { try { C.Debug = null; } catch { } try { pc.Dispose(); } catch { } }

    // ================================================================================================ running

    private void DrainAudio()
    {
        int n;
        while ((n = pc.ReadSamples(sbuf)) > 0) { var r = rec; if (r != null) for (int i = 0; i < n; i++) r.Add(sbuf[i]); }
    }

    private void Resume()
    {
        if (dbg == null || !dbg.Stop) return;
        dbg.Stop = false; dbg.Reason = "";
        dbg.SkipLin = C.SegBase[I386.SEG_CS] + C.NextIp;   // a breakpoint here does not stop again at once
    }

    /// <summary>Runs one frame (or the rest of one); false when the debugger stopped inside it.</summary>
    private bool OneFrame()
    {
        if (!midFrame)
        {
            while (schedule.Count > 0) { var f = schedule.First(); if (f.Key > framesDone) break; held = f.Value; schedule.Remove(f.Key); }
            pc.SetPad(0, held);
        }
        pc.RunFrame();
        DrainAudio();
        if (dbg is { Stop: true }) { midFrame = true; if (tempBp < 0) CountHit(); return false; }
        midFrame = false; framesDone++;
        return true;
    }

    private void CountHit()
    {
        if (dbg == null) return;
        var e = dbg.StopEvent;
        foreach (var b in bps)
            if ((b.Kind == "exec" && e.Kind == "exec" && b.At == e.Value) || (b.Kind.StartsWith("port") && e.Kind is "in" or "out" && b.At == (uint)e.Number)
                || (b.Kind == e.Kind && b.At == (uint)e.Number)) b.Hits++;
    }

    private string RunFrames(long n)
    {
        Resume();
        n = Math.Clamp(n, 0, 1_000_000);
        var t0 = DateTime.UtcNow;
        for (long f = 0; f < n; f++)
        {
            if (M.Stopped != null) return "stopped";
            if (!OneFrame()) return "break";
            if ((DateTime.UtcNow - t0).TotalSeconds > 300) return "timeout";
        }
        return "frames";
    }

    private JsonObject RunResult(string outcome, long before)
    {
        DebugOffIfIdle();
        var j = new JsonObject { ["outcome"] = outcome, ["frame"] = framesDone, ["framesRun"] = framesDone - before, ["parked"] = Parked };
        if (Parked) j["break"] = StopJson();
        if (outcome == "stopped") j["stopped"] = M.Stopped;
        if (outcome == "timeout") j["note"] = "the call's time limit (300 s) passed: the machine stopped where it was (run again to go on)";
        if (watches.Count > 0) j["watches"] = WatchValues();
        return j;
    }

    private JsonObject StopJson()
    {
        var e = dbg!.StopEvent;
        var j = new JsonObject { ["reason"] = dbg.Reason, ["cs"] = H4(C.SegSel[I386.SEG_CS]), ["eip"] = H8(C.NextIp), ["pc"] = Here(), ["cycle"] = C.Cycle };
        if (e.Kind is "in" or "out") { j["port"] = H4((uint)e.Number); j["value"] = H8(e.Value); j["bits"] = e.Bits; j["accessPc"] = $"{H4(e.Cs)}:{H8(e.Eip)}"; }
        if (e.Kind is "int" or "irq" or "exception") { j["vector"] = "$" + e.Number.ToString("X2"); j[e.Kind == "exception" ? "error" : "eax"] = H8(e.Value); }
        var bp = bps.FirstOrDefault(b => (b.Kind == "exec" && e.Kind == "exec" && b.At == e.Value) || (b.Kind.StartsWith("port") && e.Kind is "in" or "out" && b.At == (uint)e.Number) || (b.Kind == e.Kind && b.At == (uint)e.Number));
        if (bp != null && e.Kind != "step") j["breakpoint"] = bp.Id;
        j["instruction"] = DisLine(C.SegBase[I386.SEG_CS] + C.NextIp, C.NextIp, !C.Code16).text;
        return j;
    }

    public override JsonObject Run(long frames, List<PadStep>? script)
    {
        long before = framesDone;
        if (script != null) Schedule(script);
        return RunResult(RunFrames(frames), before);
    }

    public override JsonObject RunUntil(string? spec, string kind, string? condition, long? frame, int? scanline, long maxFrames, string cpu)
    {
        long before = framesDone;
        if (scanline != null) throw new ToolError("run_until scanline is not offered on the x86 PC (use frame or an address)");
        if (!string.IsNullOrWhiteSpace(condition)) throw new ToolError("conditions are not offered on the x86 debugger (MVP): use a breakpoint, then registers / evaluate");
        if (frame is long f)
        {
            if (f < framesDone) throw new ToolError($"frame {f} has passed (now {framesDone}); no step back on the x86 PC");
            return RunResult(RunFrames(f - framesDone), before);
        }
        if (string.IsNullOrWhiteSpace(spec)) throw new ToolError("run_until needs spec (an address, port:N, int:N ...) or frame");
        int id = AddBp(spec!, kind);
        tempBp = id;
        try
        {
            var r = RunResult(RunFrames(Math.Clamp(maxFrames, 1, 100_000)), before);
            return r;
        }
        finally { tempBp = -1; RemoveBp(id); DebugOffIfIdle(); }
    }

    public override JsonObject Pause()
    {
        DebugOn();
        return new JsonObject { ["parked"] = Parked, ["note"] = "headless sessions are always still between calls; the debugger is now on, so step goes from here", ["pc"] = Here() };
    }

    public override JsonObject Continue() => Run(600, null);

    public override JsonObject Reset()
    {
        pc.Reset(); midFrame = false;
        if (dbg != null) { dbg.Stop = false; dbg.TrCount = 0; }
        return Status();
    }

    // ================================================================================================ input

    private void Schedule(List<PadStep> script)
    {
        foreach (var s in script) schedule[framesDone + s.Frame] = Pads.Parse(s.Buttons, ConsoleKey);
    }

    public override JsonObject Input(string mode, string[] buttons, int frames, int player, List<PadStep>? script, long? runFrames)
    {
        if (player != 0) throw new ToolError("the x86 PC has one keyboard (player 1)");
        long before = framesDone;
        switch (mode)
        {
            case "hold": held = Pads.Parse(buttons, ConsoleKey); return Status();
            case "release": held = PadButtons.None; return Status();
            case "press":
            {
                var b = Pads.Parse(buttons, ConsoleKey);
                schedule[framesDone] = b; schedule[framesDone + Math.Clamp(frames, 1, 600)] = held;
                return RunResult(RunFrames(Math.Clamp(frames, 1, 600) + 1), before);
            }
            case "script":
            {
                if (script == null || script.Count == 0) throw new ToolError("mode script needs script");
                Schedule(script);
                return RunResult(RunFrames(runFrames ?? script.Max(s => s.Frame) + 1), before);
            }
            default: throw new ToolError("mode is press, hold, release or script");
        }
    }

    private static readonly Dictionary<char, (byte code, bool shift)> KeyMap = BuildKeys();
    private static Dictionary<char, (byte, bool)> BuildKeys()
    {
        var k = new Dictionary<char, (byte, bool)>();
        void Row(string lo, string hi, byte first) { for (int i = 0; i < lo.Length; i++) { k[lo[i]] = ((byte)(first + i), false); if (hi[i] != ' ') k[hi[i]] = ((byte)(first + i), true); } }
        Row("1234567890-=", "!@#$%^&*()_+", 0x02);
        Row("qwertyuiop[]", "QWERTYUIOP{}", 0x10);
        Row("asdfghjkl;'`", "ASDFGHJKL:\"~", 0x1e);
        Row("\\zxcvbnm,./", "|ZXCVBNM<>?", 0x2b);
        k[' '] = (0x39, false); k['\n'] = (0x1c, false); k['\t'] = (0x0f, false);
        return k;
    }

    private static byte[] TokenKey(string k)
    {
        if (k.Length > 1 && k[0] == 'f' && int.TryParse(k[1..], out int fn) && fn is >= 1 and <= 12) return new[] { (byte)(fn <= 10 ? 0x3a + fn : 0x57 + fn - 11) };
        return k switch
        {
            "win" => new byte[] { 0xe0, 0x5b }, "menu" => new byte[] { 0xe0, 0x5d }, "esc" => new byte[] { 0x01 }, "tab" => new byte[] { 0x0f },
            "enter" => new byte[] { 0x1c }, "space" => new byte[] { 0x39 }, "bs" or "backspace" => new byte[] { 0x0e }, "alt" => new byte[] { 0x38 },
            "ctrl" => new byte[] { 0x1d }, "shift" => new byte[] { 0x2a },
            "up" => new byte[] { 0xe0, 0x48 }, "down" => new byte[] { 0xe0, 0x50 }, "left" => new byte[] { 0xe0, 0x4b }, "right" => new byte[] { 0xe0, 0x4d },
            "home" => new byte[] { 0xe0, 0x47 }, "end" => new byte[] { 0xe0, 0x4f }, "pgup" => new byte[] { 0xe0, 0x49 }, "pgdn" => new byte[] { 0xe0, 0x51 },
            "del" => new byte[] { 0xe0, 0x53 }, "ins" => new byte[] { 0xe0, 0x52 },
            _ when k.Length == 1 && KeyMap.TryGetValue(k[0], out var kk) => new[] { kk.code },
            _ => throw new ToolError($"key '{k}' unknown (letters, f1-f12, enter esc tab space bs alt ctrl shift win menu up down left right home end pgup pgdn del ins)"),
        };
    }

    private void KeyBytes(params byte[] b) => M.Kbc.KeyBytes(b);
    private static byte[] Brk(byte[] k) { var c = (byte[])k.Clone(); c[^1] |= 0x80; return c; }

    /// <summary>Types keys (x86boot syntax: text, {enter}, {alt+f4}, {ctrl+esc}, {wait:N}, {{ for a brace), each press and release a frame apart.</summary>
    public JsonObject Keys(string text)
    {
        long before = framesDone;
        Resume();
        bool F() => OneFrame();
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '{' && i + 1 < text.Length && text[i + 1] == '{') { i++; }
            else if (ch == '{' && text.IndexOf('}', i) is int ce and > 0)
            {
                string tok = text[(i + 1)..ce].ToLowerInvariant(); i = ce;
                if (tok.StartsWith("wait:")) { int n = int.Parse(tok[5..], CultureInfo.InvariantCulture); for (int q = 0; q < n; q++) if (!F()) return RunResult("break", before); continue; }
                var parts = tok.Split('+');
                var mods = parts[..^1].Select(TokenKey).ToList();
                var key = TokenKey(parts[^1]);
                foreach (var m in mods) KeyBytes(m);
                KeyBytes(key); if (!F()) return RunResult("break", before);
                KeyBytes(Brk(key));
                for (int q = mods.Count - 1; q >= 0; q--) KeyBytes(Brk(mods[q]));
                if (!F()) return RunResult("break", before);
                continue;
            }
            if (!KeyMap.TryGetValue(ch, out var k)) throw new ToolError($"cannot type '{ch}' (use {{token}} for special keys)");
            if (k.shift) KeyBytes(0x2a);
            KeyBytes(k.code); if (!F()) return RunResult("break", before);
            KeyBytes((byte)(k.code | 0x80));
            if (k.shift) KeyBytes(0xaa);
            if (!F()) return RunResult("break", before);
        }
        return RunResult("frames", before);
    }

    /// <summary>The PS/2 mouse: "dx,dy[,buttons]" relative motion (x right, y down; buttons bit 0 left, 1 right, 2 middle), or "click:dx,dy" (move, then a left click).</summary>
    public JsonObject Mouse(string spec)
    {
        if (!pc.HasMouse) throw new ToolError("this machine has no PS/2 mouse (the Windows 98 presets do)");
        long before = framesDone;
        Resume();
        bool click = spec.StartsWith("click:", StringComparison.OrdinalIgnoreCase);
        var p = (click ? spec[6..] : spec).Split(',', StringSplitOptions.TrimEntries);
        if (p.Length < 2 || !int.TryParse(p[0], out int dx) || !int.TryParse(p[1], out int dy)) throw new ToolError("mouse is \"dx,dy[,buttons]\" or \"click:dx,dy\"");
        int btn = p.Length > 2 && int.TryParse(p[2], out int bb) ? bb : 0;
        var steps = click ? new[] { (dx, dy, 0), (0, 0, 1), (0, 0, 0) } : new[] { (dx, dy, btn) };
        foreach (var (x, y, b) in steps)
        {
            M.Kbc.MouseInput(x, y, b);
            if (!OneFrame() || !OneFrame()) return RunResult("break", before);
        }
        return RunResult("frames", before);
    }

    // ================================================================================================ memory

    private uint LinToPhys(uint la) => M.LinToPhys(la);

    /// <summary>A physical byte without side effects (-1: the VGA window or nothing there).</summary>
    private int PeekPhys(uint a)
    {
        if (a is >= 0xa0000 and < 0xc0000) return -1;
        if (a < C.MemSize) return C.Mem[a];
        if (M.S3 != null && M.S3.InLfb(a)) return M.Phys8(a);
        return a >= 0xfff00000 || a is >= 0xc0000 and < 0x100000 ? M.Phys8(a) : -1;
    }
    private int PeekLin(uint la) { uint p = LinToPhys(la); return p == 0xffffffff ? -1 : PeekPhys(p); }

    private uint DescBase(uint sel)
    {
        uint tb = (sel & 4) != 0 ? C.SegBase[I386.SEG_LDT] : C.GdtBase, at = tb + (sel & ~7u);
        int b2 = PeekLin(at + 2), b3 = PeekLin(at + 3), b4 = PeekLin(at + 4), b7 = PeekLin(at + 7);
        if ((b2 | b3 | b4 | b7) < 0) throw new ToolError($"selector {H4(sel)}: its descriptor is not readable");
        return (uint)(b2 | (b3 << 8) | (b4 << 16) | (b7 << 24));
    }

    private static readonly string[] SegNames = { "es", "cs", "ss", "ds", "fs", "gs" };

    /// <summary>A segment's base: a register name (cs ds ...) is its current base; a number is a selector (real / V86: *16).</summary>
    private uint SegBaseOf(string s)
    {
        int r = Array.IndexOf(SegNames, s.Trim().ToLowerInvariant());
        if (r >= 0) return C.SegBase[r];
        uint v = (uint)Num.Parse(s, "segment");
        if ((C.Cr0 & 1) == 0 || (C.Flags & I386.VM) != 0) return v << 4;
        for (int i = 0; i < 6; i++) if (C.SegSel[i] == v) return C.SegBase[i];
        return DescBase(v);
    }

    /// <summary>A spec to (domain, address, length): phys:ADDR, lin:ADDR, SEG:OFF (cs:eip, 0x10:401000, 1234:5678), a register (eip, esp ...), a number (linear), with -END or +LEN.</summary>
    private (string dom, uint addr, int? len) Spec(string spec)
    {
        spec = spec.Trim();
        int? len = null;
        int dash = spec.IndexOf('-', 1), plus = spec.LastIndexOf('+');
        string head = spec;
        string? endS = null;
        if (dash > 0) { head = spec[..dash]; endS = spec[(dash + 1)..]; }
        else if (plus > 0 && !spec[..plus].EndsWith(':')) { head = spec[..plus]; len = (int)Num.Parse(spec[(plus + 1)..], "length"); }
        string dom = "linear"; uint a;
        int colon = head.IndexOf(':');
        if (colon > 0)
        {
            string l = head[..colon].Trim().ToLowerInvariant(), r = head[(colon + 1)..];
            if (l is "phys" or "p" or "ram" or "physical") { dom = "phys"; a = Val(r); }
            else if (l is "lin" or "l" or "linear") a = Val(r);
            else a = SegBaseOf(l) + Val(r, true);
        }
        else a = Val(head);
        if (endS != null) { uint e = colon > 0 && head[..colon].Trim().ToLowerInvariant() is not ("phys" or "p" or "ram" or "physical" or "lin" or "l" or "linear") ? SegBaseOf(head[..colon]) + Val(endS) : Val(endS); if (e < a) throw new ToolError($"'{spec}': the range ends before it starts"); len = (int)Math.Min(e - a + 1, 1 << 20); }
        return (dom, a, len);
    }

    /// <summary>A register or a number ($hex, 0xhex, decimal; bare hex digits with a letter are hex; <paramref name="hex"/>: an offset after SEG:, always hex).</summary>
    private uint Val(string s, bool hex = false)
    {
        s = s.Trim();
        if (TryReg(s, out long v)) return (uint)v;
        bool pre = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith('$');
        return (uint)Num.Parse(!pre && s.Length > 0 && s.All(Uri.IsHexDigit) && (hex || s.Any(char.IsLetter)) ? "$" + s : s, "address");
    }

    private int ReadDom(string dom, uint a, byte[] buf)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            int v = dom == "phys" ? PeekPhys(a + (uint)i) : PeekLin(a + (uint)i);
            if (v < 0) return i;
            buf[i] = (byte)v;
        }
        return buf.Length;
    }

    public override JsonObject Domains() => new()
    {
        ["console"] = ConsoleKey,
        ["domains"] = J.Arr(new JsonNode[]
        {
            new JsonObject { ["id"] = "linear", ["label"] = "linear (through the page tables when CR0.PG)", ["size"] = 1L << 32, ["writable"] = true },
            new JsonObject { ["id"] = "phys", ["label"] = "physical (RAM, BIOS ROM, the S3 frame buffer; not the VGA window A0000-BFFFF)", ["size"] = (long)C.MemSize, ["writable"] = true },
        }),
        ["specs"] = "lin:ADDR or a number (linear), phys:ADDR, SEG:OFF (cs:eip, ds:esi, 0x28:c0001000, real-mode 1234:5678), registers inside (ss:esp); ranges A-B or A+LEN",
    };

    public override JsonObject ReadMemory(string? spec, string? domain, long? address, int? length, string cpu)
    {
        string dom; uint a; int? l0;
        if (!string.IsNullOrWhiteSpace(spec)) (dom, a, l0) = Spec(spec!);
        else if (domain != null && address != null) { dom = domain.ToLowerInvariant() is "phys" or "ram" or "physical" ? "phys" : "linear"; a = (uint)address.Value; l0 = null; }
        else throw new ToolError("read_memory needs spec, or domain + address");
        int len = Math.Clamp(length ?? l0 ?? 16, 1, 65536);
        var buf = new byte[len];
        int n = ReadDom(dom, a, buf);
        if (n == 0) throw new ToolError($"{dom}:{H8(a)} cannot be read (not present in the page tables, or the VGA window / nothing there)");
        var j = new JsonObject { ["domain"] = dom, ["address"] = H8(a), ["length"] = n, ["hex"] = Num.HexBytes(buf.AsSpan(0, n)) };
        if (dom == "linear") j["physical"] = LinToPhys(a) is uint p && p != 0xffffffff ? H8(p) : null;
        if (n <= 8) { ulong v = 0; for (int i = n - 1; i >= 0; i--) v = (v << 8) | buf[i]; j["value"] = (long)v; }
        if (n < len) j["note"] = $"{len - n} bytes past what is mapped were not read";
        return j;
    }

    public override JsonObject WriteMemory(string? spec, string? domain, long? address, byte[] data, string cpu)
    {
        if (data.Length == 0) throw new ToolError("data is needed");
        string dom; uint a;
        if (!string.IsNullOrWhiteSpace(spec)) (dom, a, _) = Spec(spec!);
        else if (domain != null && address != null) { dom = domain.ToLowerInvariant() is "phys" or "ram" or "physical" ? "phys" : "linear"; a = (uint)address.Value; }
        else throw new ToolError("write_memory needs spec, or domain + address");
        var before = new byte[data.Length]; int nb = ReadDom(dom, a, before);
        int n = 0;
        for (; n < data.Length; n++)
        {
            uint p = dom == "phys" ? a + (uint)n : LinToPhys(a + (uint)n);
            if (p == 0xffffffff || PeekPhys(p) < 0) break;
            M.PhysW8(p, data[n]);
        }
        if (n == 0) throw new ToolError($"{dom}:{H8(a)} cannot be written");
        var after = new byte[n]; ReadDom(dom, a, after);
        return new JsonObject { ["domain"] = dom, ["address"] = H8(a), ["written"] = n, ["before"] = Num.HexBytes(before.AsSpan(0, Math.Min(nb, n))), ["after"] = Num.HexBytes(after), ["note"] = "code written drops the compiled blocks made from it" };
    }

    public override JsonObject SearchMemory(string domain, string op, long? value, int width, bool fresh, int show)
    {
        if (width is not (1 or 2 or 4)) throw new ToolError("width is 1, 2 or 4 (little-endian)");
        int n = (int)Math.Min(C.MemSize, 256u << 20);
        var mem = new byte[n];
        Array.Copy(C.Mem, mem, n);
        Array.Clear(mem, 0xa0000, 0x20000);
        long Vv(byte[] m, int at) { long v = 0; for (int i = width - 1; i >= 0; i--) v = (v << 8) | m[at + i]; return v; }
        bool first = fresh || search == null || search.Value.width != width;
        IEnumerable<int> from = first ? Enumerable.Range(0, (n - width) / width + 1).Select(i => i * width) : search!.Value.cand;
        byte[]? prev = first ? null : search!.Value.prev;
        Func<int, bool> keep = op switch
        {
            "eq" or "==" => a => Vv(mem, a) == (value ?? throw new ToolError("op eq needs value")),
            "ne" or "!=" => a => Vv(mem, a) != (value ?? throw new ToolError("op ne needs value")),
            "lt" or "<" => a => Vv(mem, a) < (value ?? throw new ToolError("op lt needs value")),
            "gt" or ">" => a => Vv(mem, a) > (value ?? throw new ToolError("op gt needs value")),
            "changed" => a => prev == null ? throw new ToolError("changed compares with the last search; search once first") : Vv(mem, a) != Vv(prev, a),
            "unchanged" or "same" => a => prev == null ? throw new ToolError("unchanged compares with the last search; search once first") : Vv(mem, a) == Vv(prev, a),
            "increased" or "inc" => a => prev == null ? throw new ToolError("increased compares with the last search") : Vv(mem, a) > Vv(prev, a),
            "decreased" or "dec" => a => prev == null ? throw new ToolError("decreased compares with the last search") : Vv(mem, a) < Vv(prev, a),
            "all" or "start" => _ => true,
            _ => throw new ToolError("op is eq, ne, lt, gt, changed, unchanged, increased, decreased or all"),
        };
        var cand = from.Where(keep).ToList();
        search = ("phys", width, cand, mem);
        return new JsonObject
        {
            ["domain"] = "phys", ["width"] = width, ["candidates"] = cand.Count, ["fresh"] = first,
            ["matches"] = J.Arr(cand.Take(Math.Clamp(show, 0, 500)).Select(a => (JsonNode)new JsonObject { ["address"] = H8((uint)a), ["value"] = Vv(mem, a) })),
            ["note"] = "searches physical RAM (the guest's linear addresses map onto it through the page tables)",
        };
    }

    // ================================================================================================ breakpoints

    private uint ExecLinear(string spec)
    {
        var (dom, a, _) = Spec(spec);
        if (dom == "phys") throw new ToolError("an exec breakpoint is on a linear address or seg:off (code runs at linear addresses)");
        return a;
    }

    private int AddBp(string spec, string kind)
    {
        spec = spec.Trim();
        string k = kind.Trim().ToLowerInvariant();
        string low = spec.ToLowerInvariant();
        Bp b;
        if (low.StartsWith("port:"))
        {
            uint port = (uint)Num.Parse(spec[5..].Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) || spec[5..].Trim().StartsWith('$') ? spec[5..].Trim() : "$" + spec[5..].Trim(), "port");
            if (port > 0xffff) throw new ToolError("a port is 0 to FFFF");
            string pk = k switch { "read" or "r" => "port_read", "write" or "w" => "port_write", "rw" or "exec" or "" => "port_rw", _ => throw new ToolError("a port breakpoint's kind is read (IN), write (OUT) or rw") };
            b = new Bp(nextBp++, pk, port, $"{pk.Replace('_', ' ')} {H4(port)}");
        }
        else if (low.StartsWith("int:") || low.StartsWith("irq:") || low.StartsWith("exception:") || low.StartsWith("exc:"))
        {
            int c = spec.IndexOf(':');
            string vk = low[..c] == "exc" ? "exception" : low[..c];
            uint v = (uint)Num.Parse(spec[(c + 1)..].Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) || spec[(c + 1)..].Trim().StartsWith('$') ? spec[(c + 1)..].Trim() : (vk == "exception" ? spec[(c + 1)..].Trim() : "$" + spec[(c + 1)..].Trim()), "vector");
            if (v > 255) throw new ToolError("a vector is 0 to FF");
            b = new Bp(nextBp++, vk, v, $"{vk} ${v:X2}");
        }
        else
        {
            if (k is not ("exec" or "x" or "execute" or "")) throw new ToolError("memory read / write watchpoints are not offered on the x86 debugger (MVP): exec, port:N (read / write / rw), int:N, irq:N, exception:N");
            uint lin = ExecLinear(spec);
            b = new Bp(nextBp++, "exec", lin, $"exec {spec} (linear {H8(lin)})");
        }
        bps.Add(b);
        DebugOn(); RebuildBps();
        return b.Id;
    }

    private bool RemoveBp(int id) { int n = bps.RemoveAll(b => b.Id == id); RebuildBps(); return n > 0; }

    public override JsonObject AddBreakpoint(string spec, string kind, string? condition, string? hits, bool tracepoint, string? log, string cpu)
    {
        if (!string.IsNullOrWhiteSpace(condition) || !string.IsNullOrWhiteSpace(hits)) throw new ToolError("conditions and hit filters are not offered on the x86 debugger (MVP)");
        if (tracepoint || !string.IsNullOrWhiteSpace(log)) throw new ToolError("tracepoints are not offered on the x86 debugger; use trace or debug_port (the port / interrupt logs)");
        int id = AddBp(spec, kind);
        return new JsonObject { ["id"] = id, ["text"] = bps.First(b => b.Id == id).Text, ["note"] = "the x86 debugger is on now: the CPU runs interpreted (slower) until the last breakpoint is removed" };
    }

    public override JsonObject ListBreakpoints(int hits) => new()
    {
        ["breakpoints"] = J.Arr(bps.Select(b => (JsonNode)new JsonObject { ["id"] = b.Id, ["kind"] = b.Kind, ["address"] = b.Kind == "exec" ? H8(b.At) : H4(b.At), ["text"] = b.Text, ["hits"] = b.Hits })),
        ["debuggerOn"] = dbg != null, ["lastStop"] = Parked ? StopJson() : null,
    };

    public override JsonObject RemoveBreakpoint(int? id, bool all)
    {
        int n;
        if (all) { n = bps.Count; bps.Clear(); RebuildBps(); }
        else if (id is int i) { if (!RemoveBp(i)) throw new ToolError($"no breakpoint {i}"); n = 1; }
        else throw new ToolError("give id, or all:true");
        DebugOffIfIdle();
        return new JsonObject { ["removed"] = n, ["left"] = bps.Count, ["debuggerOn"] = dbg != null };
    }

    // ================================================================================================ expressions, watches

    private static readonly string[] Gpr32 = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };
    private static readonly string[] Gpr16 = { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" };
    private static readonly string[] Gpr8 = { "al", "cl", "dl", "bl", "ah", "ch", "dh", "bh" };

    private bool TryReg(string n, out long v)
    {
        n = n.Trim().ToLowerInvariant();
        int i;
        if ((i = Array.IndexOf(Gpr32, n)) >= 0) { v = C.R[i]; return true; }
        if ((i = Array.IndexOf(Gpr16, n)) >= 0) { v = C.R[i] & 0xffff; return true; }
        if ((i = Array.IndexOf(Gpr8, n)) >= 0) { v = (C.R[i & 3] >> (i >= 4 ? 8 : 0)) & 0xff; return true; }
        switch (n)
        {
            case "eip" or "ip" or "pc": v = C.NextIp; return true;
            case "eflags" or "flags": v = C.PeekFlags(); return true;
            case "cr0": v = C.Cr0; return true; case "cr2": v = C.Cr2; return true; case "cr3": v = C.Cr3; return true; case "cr4": v = C.Cr4; return true;
            case "cpl": v = C.Cpl; return true; case "cycle": v = C.Cycle; return true; case "frame": v = framesDone; return true;
        }
        if ((i = Array.IndexOf(SegNames, n)) >= 0) { v = C.SegSel[i]; return true; }
        v = 0; return false;
    }

    private long Eval(string expr)
    {
        string e = expr.Trim();
        if (e.Length > 2 && ((e[0] == '[' && e[^1] == ']') || (e[0] == '{' && e[^1] == '}'))) return ReadLe(e[1..^1], e[0] == '[' ? 1 : 4);
        if (e.StartsWith("w:", StringComparison.OrdinalIgnoreCase)) return ReadLe(e[2..], 2);
        if (TryReg(e, out long v)) return v;
        if (e.Length > 0 && (char.IsDigit(e[0]) || e[0] is '$' or '-' or '%')) return Num.Parse(e);
        throw new ToolError($"'{expr}': x86 expressions are a register (eax, ax, al, eip, esp, eflags, cr0, cs ...), [spec] a byte, w:spec a word, {{spec}} a dword (little-endian), frame, cycle or a number; no operators");
    }

    private long ReadLe(string spec, int width)
    {
        var (d, a, _) = Spec(spec);
        var b = new byte[width];
        if (ReadDom(d, a, b) != width) throw new ToolError($"'{spec}' cannot be read now");
        long v = 0; for (int i = width - 1; i >= 0; i--) v = (v << 8) | b[i]; return v;
    }

    public override JsonObject Evaluate(string expr, string cpu) { long v = Eval(expr); return new JsonObject { ["expr"] = expr, ["value"] = v, ["hex"] = "$" + v.ToString("X") }; }

    private JsonArray WatchValues() => J.Arr(watches.Select(w =>
    {
        try { long v = Eval(w); return (JsonNode)new JsonObject { ["expr"] = w, ["value"] = v, ["hex"] = "$" + v.ToString("X") }; }
        catch (ToolError e) { return new JsonObject { ["expr"] = w, ["error"] = e.Message }; }
    }));

    public override JsonObject Watch(string action, string? expr, int? index)
    {
        switch (action)
        {
            case "add": if (string.IsNullOrWhiteSpace(expr)) throw new ToolError("expr is needed"); Eval(expr!); watches.Add(expr!); break;
            case "remove":
                if (index is int i && i >= 0 && i < watches.Count) watches.RemoveAt(i);
                else if (expr == null || !watches.Remove(expr)) throw new ToolError("remove needs index or the expr");
                break;
            case "clear": watches.Clear(); break;
            case "list": break;
            default: throw new ToolError("action is add, remove, clear or list");
        }
        return new JsonObject { ["watches"] = WatchValues() };
    }

    // ================================================================================================ stepping, registers, stack, trace, disassembly

    private JsonObject StepResult(long before)
    {
        var j = new JsonObject { ["outcome"] = Parked ? "break" : "frames", ["frame"] = framesDone, ["framesRun"] = framesDone - before, ["parked"] = Parked };
        if (Parked) j["break"] = StopJson();
        if (watches.Count > 0) j["watches"] = WatchValues();
        return j;
    }

    private void RunToStop(long maxFrames)
    {
        Resume();
        for (long f = 0; f < maxFrames && M.Stopped == null; f++) if (!OneFrame()) return;
    }

    public override JsonObject Step(string mode, int count, string cpu, string? to, long? frame, int? scanline)
    {
        long before = framesDone;
        if (scanline != null) throw new ToolError("step to a scanline is not offered on the x86 PC");
        if (frame != null) return RunUntil(null, "exec", null, frame, null, 1_000_000, cpu);
        if (!string.IsNullOrWhiteSpace(to)) return RunUntil(to, "exec", null, null, null, 600, cpu);
        count = Math.Clamp(count, 1, 1_000_000);
        DebugOn();
        switch (mode)
        {
            case "into" or "instruction" or "in" or "":
                Resume(); dbg!.StepBudget = count;
                RunToStop(600); dbg.StepBudget = 0;
                break;
            case "over":
                for (int i = 0; i < count; i++)
                {
                    uint lin = C.SegBase[I386.SEG_CS] + C.NextIp;
                    var (len, text) = DisLine(lin, C.NextIp, !C.Code16);
                    if (len > 0 && (text.StartsWith("call") || text.StartsWith("int") || text.StartsWith("rep") || text.StartsWith("loop")))
                    {
                        int id = AddTemp(lin + (uint)len);
                        try { RunToStop(600); } finally { RemoveBp(id); tempBp = -1; }
                        if (dbg!.StopEvent.Kind != "exec" || dbg.StopEvent.Value != lin + (uint)len) break;
                        dbg.Reason = "step over";
                    }
                    else { Resume(); dbg!.StepBudget = 1; RunToStop(600); dbg.StepBudget = 0; }
                    if (!Parked) break;
                }
                break;
            case "out":
            {
                uint ret = Frames(1).FirstOrDefault().ret;
                if (ret == 0) throw new ToolError("step out needs a return address: the EBP chain gives none here (no frame pointer)");
                uint lin = C.SegBase[I386.SEG_CS] + ret;
                int id = AddTemp(lin);
                try { RunToStop(600); } finally { RemoveBp(id); tempBp = -1; }
                if (Parked && dbg!.StopEvent.Kind == "exec" && dbg.StopEvent.Value == lin) dbg.Reason = "step out";
                break;
            }
            default: throw new ToolError($"mode '{mode}' is not offered on the x86 PC (into, over or out)");
        }
        var r = StepResult(before);
        DebugOffIfIdle();
        return r;
    }

    private int AddTemp(uint lin)
    {
        var b = new Bp(nextBp++, "exec", lin, "temp");
        bps.Add(b); RebuildBps(); tempBp = b.Id; return b.Id;
    }

    private string ModeName() => (C.Cr0 & 1) == 0 ? "real" : (C.Flags & I386.VM) != 0 ? "v86" : C.Code16 ? "protected16" : "protected32";

    public override JsonObject Registers(string cpu)
    {
        var regs = new JsonObject();
        for (int i = 0; i < 8; i++) regs[Gpr32[i]] = H8(C.R[i]);
        regs["eip"] = H8(C.NextIp);
        uint fl = C.PeekFlags();
        regs["eflags"] = H8(fl);
        var segs = new JsonObject();
        for (int i = 0; i < 6; i++) segs[SegNames[i]] = new JsonObject { ["sel"] = H4(C.SegSel[i]), ["base"] = H8(C.SegBase[i]), ["limit"] = H8(C.SegLimit[i]) };
        string[] fn = { "CF", "", "PF", "", "AF", "", "ZF", "SF", "TF", "IF", "DF", "OF" };
        var set = new List<string>();
        for (int b = 0; b < fn.Length; b++) if (fn[b].Length > 0 && (fl & (1u << b)) != 0) set.Add(fn[b]);
        if ((fl & I386.VM) != 0) set.Add("VM");
        if ((fl & I386.NT) != 0) set.Add("NT");
        return new JsonObject
        {
            ["cpu"] = "main", ["pc"] = Here(), ["mode"] = ModeName(), ["cpl"] = C.Cpl, ["iopl"] = (int)((fl >> 12) & 3), ["flags"] = string.Join(" ", set),
            ["instruction"] = DisLine(C.SegBase[I386.SEG_CS] + C.NextIp, C.NextIp, !C.Code16).text,
            ["registers"] = regs, ["segments"] = segs,
            ["control"] = new JsonObject { ["cr0"] = H8(C.Cr0), ["cr2"] = H8(C.Cr2), ["cr3"] = H8(C.Cr3), ["cr4"] = H8(C.Cr4), ["paging"] = (C.Cr0 & 0x80000000) != 0 },
            ["tables"] = new JsonObject { ["gdt"] = $"{H8(C.GdtBase)}/{H4(C.GdtLimit)}", ["idt"] = $"{H8(C.IdtBase)}/{H4(C.IdtLimit)}", ["ldt"] = H4(C.SegSel[I386.SEG_LDT]), ["tr"] = H4(C.SegSel[I386.SEG_TR]) },
            ["halted"] = C.Halt, ["cycle"] = C.Cycle, ["frame"] = framesDone, ["parked"] = Parked,
        };
    }

    public override JsonObject SetRegister(string cpu, string nm, long value)
    {
        string n = nm.Trim().ToLowerInvariant(); uint v = (uint)value;
        if (!TryReg(n, out long old)) throw new ToolError($"no register '{nm}' (eax..edi, ax.., al.., eip, eflags)");
        int i;
        if ((i = Array.IndexOf(Gpr32, n)) >= 0) C.R[i] = v;
        else if ((i = Array.IndexOf(Gpr16, n)) >= 0) C.R[i] = (C.R[i] & 0xffff0000) | (v & 0xffff);
        else if ((i = Array.IndexOf(Gpr8, n)) >= 0) { int sh = i >= 4 ? 8 : 0; C.R[i & 3] = (C.R[i & 3] & ~(0xffu << sh)) | ((v & 0xff) << sh); }
        else if (n is "eip" or "ip" or "pc") { C.NextIp = v; C.Ip = v; }
        else if (n is "eflags" or "flags") { C.GetFlags(); C.Flags = (v & C.FlagsMask) | 2; }
        else throw new ToolError($"'{nm}' is read-only here (segments and control registers are not written by the debugger)");
        TryReg(n, out long now);
        return new JsonObject { ["name"] = n, ["before"] = "$" + old.ToString("X"), ["after"] = "$" + now.ToString("X") };
    }

    /// <summary>The EBP chain: (frame pointer, return EIP) pairs, innermost first.</summary>
    private List<(uint bp, uint ret)> Frames(int max)
    {
        var l = new List<(uint, uint)>();
        bool w32 = !C.Code16;
        uint ssb = C.SegBase[I386.SEG_SS], bp = w32 ? C.R[5] : C.R[5] & 0xffff;
        var seen = new HashSet<uint>();
        for (int k = 0; k < max && bp != 0 && seen.Add(bp); k++)
        {
            var b = new byte[w32 ? 8 : 4];
            if (ReadDom("linear", ssb + bp, b) != b.Length) break;
            uint next = w32 ? BitConverter.ToUInt32(b, 0) : BitConverter.ToUInt16(b, 0), ret = w32 ? BitConverter.ToUInt32(b, 4) : BitConverter.ToUInt16(b, 2);
            l.Add((bp, ret));
            if (next <= bp) break;
            bp = next;
        }
        return l;
    }

    public override JsonObject CallStack(string cpu)
    {
        var f = Frames(64);
        uint csb = C.SegBase[I386.SEG_CS];
        return new JsonObject
        {
            ["cpu"] = "main", ["depth"] = f.Count, ["pc"] = Here(),
            ["frames"] = J.Arr(f.Select((x, i) => (JsonNode)new JsonObject
            {
                ["depth"] = i, ["kind"] = "frame", ["ebp"] = H8(x.bp), ["returnsTo"] = H8(x.ret), ["returnsToLinear"] = H8(csb + x.ret),
                ["call"] = PrevCall(csb + x.ret, x.ret),
            })),
            ["note"] = "the EBP chain, innermost first (best effort: return addresses assume the same code segment; frames built without EBP are missed)",
        };
    }

    /// <summary>The call instruction just before a return address, if one decodes there.</summary>
    private string? PrevCall(uint lin, uint eip)
    {
        foreach (int back in new[] { 5, 6, 2, 3, 7, 4 })
        {
            var (len, t) = DisLine(lin - (uint)back, eip - (uint)back, !C.Code16);
            if (len == back && t.StartsWith("call")) return t;
        }
        return null;
    }

    public override JsonObject Trace(int count, string cpu)
    {
        if (dbg == null || dbg.TrCount == 0) throw new ToolError("the trace ring fills while the debugger is on (a breakpoint, a step, open_rom call_stack:true); nothing recorded yet");
        int ring = dbg.TrCs.Length; count = (int)Math.Clamp(count, 1, Math.Min(dbg.TrCount, ring));
        var lines = new JsonArray();
        for (long k = dbg.TrCount - count; k < dbg.TrCount; k++)
        {
            int t = (int)(k % ring);
            var (_, text) = DisLine(dbg.TrLin[t], dbg.TrEip[t], dbg.TrMode[t] == 3);
            lines.Add(new JsonObject { ["cs"] = H4(dbg.TrCs[t]), ["eip"] = H8(dbg.TrEip[t]), ["linear"] = H8(dbg.TrLin[t]), ["mode"] = new[] { "real", "v86", "p16", "p32" }[dbg.TrMode[t]], ["text"] = text });
        }
        return new JsonObject { ["count"] = count, ["recorded"] = dbg.TrCount, ["lines"] = lines, ["note"] = "oldest first; the text is decoded from memory as it is now" };
    }

    public override JsonObject TraceFrame(long frame, string cpu, int lines, string? function, List<string> ranges) =>
        throw new ToolError("trace_frame is not offered on the x86 PC (MVP); use a breakpoint, step and trace");

    private (int len, string text) DisLine(uint lin, uint eip, bool c32) => X86Disasm.Decode(PeekLin, lin, eip, c32);

    public override JsonObject Disassemble(string? address, int count, string cpu)
    {
        bool c32 = !C.Code16;
        if (cpu is "16") c32 = false; else if (cpu is "32") c32 = true;
        uint lin, eip;
        if (string.IsNullOrWhiteSpace(address) || address.Trim().Equals("pc", StringComparison.OrdinalIgnoreCase)) { eip = C.NextIp; lin = C.SegBase[I386.SEG_CS] + eip; }
        else
        {
            string s = address.Trim(); int colon = s.IndexOf(':');
            if (colon > 0 && !s[..colon].Equals("lin", StringComparison.OrdinalIgnoreCase) && !s[..colon].Equals("linear", StringComparison.OrdinalIgnoreCase))
            { uint b = SegBaseOf(s[..colon]); eip = Val(s[(colon + 1)..]); lin = b + eip; }
            else { lin = Val(colon > 0 ? s[(colon + 1)..] : s); eip = lin - C.SegBase[I386.SEG_CS]; }
        }
        var arr = new JsonArray();
        for (int i = 0; i < Math.Clamp(count, 1, 500); i++)
        {
            var (len, text) = DisLine(lin, eip, c32);
            var bytes = new byte[Math.Max(len, 0)]; ReadDom("linear", lin, bytes);
            arr.Add(new JsonObject { ["linear"] = H8(lin), ["eip"] = c32 ? H8(eip) : H4(eip & 0xffff), ["bytes"] = Num.HexBytes(bytes), ["text"] = text, ["here"] = lin == C.SegBase[I386.SEG_CS] + C.NextIp ? true : null });
            if (len <= 0) break;
            lin += (uint)len; eip += (uint)len;
        }
        return new JsonObject { ["bits"] = c32 ? 32 : 16, ["mode"] = ModeName(), ["lines"] = arr };
    }

    // ================================================================================================ picture, sound, states

    public override ToolOut Screenshot(int scale)
    {
        int w = pc.FrameWidth, h = pc.FrameHeight;
        var (hash, colours) = Png.Stats(pc.Frame, w * h);
        var o = new ToolOut(new JsonObject { ["width"] = w, ["height"] = h, ["displayWidth"] = pc.DisplayWidth, ["hash"] = hash, ["colours"] = colours, ["frame"] = framesDone, ["midFrame"] = midFrame });
        o.Pngs.Add(Png.FromArgb(pc.Frame, w, h, Math.Clamp(scale, 1, 4)));
        return o;
    }

    public override JsonObject RecordSound(double seconds, bool wav)
    {
        long before = framesDone;
        rec = new List<short>();
        string outcome;
        try { outcome = RunFrames((long)Math.Ceiling(Math.Clamp(seconds, 0.1, 60) * pc.FramesPerSecond)); }
        finally { }
        var samples = rec.ToArray(); rec = null;
        var j = RunResult(outcome, before);
        j["samples"] = samples.Length; j["rate"] = pc.SampleRate;
        if (wav)
        {
            string dir = Path.Combine(Path.GetTempPath(), "brokennes-mcp"); Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, $"x86-{Environment.ProcessId}-{framesDone}.wav");
            using (var w = new BinaryWriter(File.Create(f)))
            {
                int ch = 2, rate = pc.SampleRate, bytes = samples.Length * 2;
                w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(rate); w.Write(rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
                w.Write("data"u8); w.Write(bytes); foreach (var s in samples) w.Write(s);
            }
            j["wav"] = f;
        }
        return j;
    }

    public override JsonObject SaveState(string nm)
    {
        if (midFrame) throw new ToolError("the machine is stopped inside a frame (a break or a step): run on to the end of the frame first (run 1), then save");
        states[nm] = (pc.SaveState(), framesDone);
        return new JsonObject { ["saved"] = nm, ["frame"] = framesDone, ["bytes"] = states[nm].state.Length, ["states"] = J.Strs(states.Keys) };
    }

    public override JsonObject LoadState(string nm)
    {
        if (!states.TryGetValue(nm, out var s)) throw new ToolError($"no state '{nm}' (saved: {string.Join(", ", states.Keys)})");
        try { pc.LoadState(s.state); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { throw new ToolError(e.Message); }
        framesDone = s.frames; midFrame = false; schedule.Clear();
        if (dbg != null) { dbg.Stop = false; dbg.TrCount = 0; }
        return Status();
    }

    // ================================================================================================ the port / interrupt logs (debug_port)

    public override JsonObject DebugPort(string action, long since, int limit, List<string> kinds)
    {
        bool ports = kinds.Count == 0 || kinds.Any(k => k.StartsWith("port", StringComparison.OrdinalIgnoreCase) || k is "in" or "out");
        bool ints = kinds.Count == 0 || kinds.Any(k => k.StartsWith("int", StringComparison.OrdinalIgnoreCase) || k is "irq" or "exception");
        switch (action)
        {
            case "start" or "on":
                DebugOn(); if (ports) dbg!.LogPorts = true; if (ints) dbg!.LogInts = true;
                return new JsonObject { ["logging"] = J.Strs(new[] { dbg!.LogPorts ? "ports" : null, dbg.LogInts ? "interrupts" : null }.Where(x => x != null)!), ["note"] = "the debugger is on while a log is (interpreted CPU); action stop turns it off" };
            case "stop" or "off":
                if (dbg != null) { if (ports) dbg.LogPorts = false; if (ints) dbg.LogInts = false; }
                DebugOffIfIdle();
                return new JsonObject { ["logging"] = dbg == null ? new JsonArray() : J.Strs(new[] { dbg.LogPorts ? "ports" : null, dbg.LogInts ? "interrupts" : null }.Where(x => x != null)!) };
            case "clear":
                if (dbg != null) { dbg.PortLog.Clear(); dbg.IntLog.Clear(); }
                return new JsonObject { ["cleared"] = true };
            case "events" or "status":
            {
                if (dbg == null) return new JsonObject { ["events"] = new JsonArray(), ["note"] = "no log running: debug_port action start (kinds ports / interrupts) first" };
                var ev = new List<X86DebugEvent>();
                if (ports) ev.AddRange(dbg.PortLog);
                if (ints) ev.AddRange(dbg.IntLog);
                var sel = ev.Where(e => e.Cycle > since).OrderBy(e => e.Cycle).ToList();
                if (action == "status") return new JsonObject { ["portEvents"] = dbg.PortEvents, ["intEvents"] = dbg.IntEvents, ["kept"] = ev.Count, ["logPorts"] = dbg.LogPorts, ["logInts"] = dbg.LogInts };
                return new JsonObject
                {
                    ["events"] = J.Arr(sel.TakeLast(Math.Clamp(limit, 1, 4096)).Select(e => (JsonNode)new JsonObject
                    {
                        ["cycle"] = e.Cycle, ["kind"] = e.Kind, ["pc"] = $"{H4(e.Cs)}:{H8(e.Eip)}",
                        [e.Kind is "in" or "out" ? "port" : "vector"] = e.Kind is "in" or "out" ? H4((uint)e.Number) : "$" + e.Number.ToString("X2"),
                        ["value"] = H8(e.Value), ["bits"] = e.Kind is "in" or "out" ? e.Bits : null,
                    })),
                    ["note"] = "since is a CPU cycle (instruction count); the logs keep the newest 4096 of each",
                };
            }
            default: throw new ToolError("on the x86 PC, debug_port is the port I/O and interrupt logs: action start / stop (kinds ports, interrupts), events (since a cycle), status, clear");
        }
    }

    // ================================================================================================ what the x86 PC does not offer

    public override ToolOut Layers(List<string> layers, int scale) => throw new ToolError("the x86 PC has no picture layers (take a screenshot)");
    public override ToolOut Tiles(string? source, int bpp, int palette, long start, int count, int scale) => throw new ToolError("the x86 PC has no tile VRAM");
    public override ToolOut Sprites(bool draw, int scale) => throw new ToolError("the x86 PC has no sprites");
    public override JsonObject Symbols(string? load, string? find, long? address, string? prefix, string cpu, int limit) => throw new ToolError("no symbols for the x86 PC (MVP: addresses only)");
    public override JsonObject DiffStates(string a, string b, int limit) => throw new ToolError("state diffs are not offered on the x86 PC; use search_memory changed");
    public override JsonObject Checks(string action, List<string> off, List<string> wait, long from, int top) => throw new ToolError("no D4 hardware checks for the x86 PC");

    // ================================================================================================ helpers

    private string Here() => $"{H4(C.SegSel[I386.SEG_CS])}:{(C.Code16 ? H4(C.NextIp & 0xffff) : H8(C.NextIp))}";
    private static string H8(uint v) => "$" + v.ToString("X8");
    private static string H4(uint v) => "$" + v.ToString("X4");
}
