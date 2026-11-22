using System.Text.Json.Nodes;
using BrokenNes.Workshop.Debug;

namespace BrokenNes.Mcp;

// The x86 PC's MCP checks (HeadlessSession.X86.cs): a boot-sector floppy in --client-test (fast, no disks needed), and
// --x86-test --hdd C.img [--hdd2 D.img] [--state st.bin] [--machine WIN98-P166] [--frames N]: Windows 98 headless with the debugger.
internal static partial class ClientTest
{
    /// <summary>A boot-sector floppy for the classic PC: cli; mov al,41h; mov dx,0EF0h; out dx,al; mov ax,1234h; l: inc ax; jmp l.</summary>
    private static string X86BootFloppy(string dir)
    {
        var img = new byte[1474560];
        byte[] code = { 0xFA, 0xB0, 0x41, 0xBA, 0xF0, 0x0E, 0xEE, 0xB8, 0x34, 0x12, 0x40, 0xEB, 0xFD };
        code.CopyTo(img, 0); img[510] = 0x55; img[511] = 0xAA;
        return DebugTestRoms.Write(dir, "x86boot.img", img);
    }

    private static async Task X86BootSector(string dir)
    {
        string f = X86BootFloppy(dir);
        // negative control first: no breakpoint, the debugger stays off and the frames all run
        var o0 = await Call("open_rom", new { path = f, machine = "classic" });
        string h0 = S(o0["session"]);
        var r0 = await Call("run", new { session = h0, frames = 120 });
        var g0 = await Call("registers", new { session = h0 });
        var s0 = await Call("status", new { session = h0 });
        Check("x86_open_run_debugger_off", !o0.IsError && S(o0["console"]) == "x86" && S(r0["outcome"]) == "frames" && L(r0["framesRun"]) == 120 && S(s0["debuggerOn"]) == "false",
            $"{S(o0["description"])}: {r0}");

        var o = await Call("open_rom", new { path = f, machine = "classic" });
        string h = S(o["session"]);
        var bp = await Call("breakpoint_add", new { session = h, spec = "port:0EF0", kind = "write" });
        var r = await Call("run", new { session = h, frames = 600 });
        Check("x86_port_breakpoint", !bp.IsError && S(r["outcome"]) == "break" && S(r["break"]?["reason"]) == "port write" && S(r["break"]?["port"]) == "$0EF0" && S(r["break"]?["value"]) == "$00000041"
            && S(r["break"]?["pc"]) == "$0000:$7C07", $"{bp} / {r}");
        await Call("breakpoint_remove", new { session = h, all = true });
        var bx = await Call("breakpoint_add", new { session = h, spec = "0:7C0A" });
        var rx = await Call("run", new { session = h, frames = 10 });
        var rg = await Call("registers", new { session = h });
        Check("x86_exec_breakpoint_registers", !bx.IsError && S(rx["break"]?["reason"]) == "breakpoint" && S(rx["break"]?["pc"]) == "$0000:$7C0A" && S(rg["mode"]) == "real"
            && S(rg["registers"]?["eax"]).EndsWith("1234") && S(rg["segments"]?["cs"]?["sel"]) == "$0000" && S(rg["instruction"]) == "inc ax", $"{bx} / {rx} / {rg}");
        await Call("breakpoint_remove", new { session = h, all = true });
        var st = await Call("step", new { session = h, mode = "into", count = 2 });
        var ev = await Call("evaluate", new { session = h, expr = "ax" });
        Check("x86_step", S(st["break"]?["pc"]) == "$0000:$7C0A" && L(ev["value"]) == 0x1235, $"{st} / ax={S(ev["hex"])}");
        var ds = await Call("disassemble", new { session = h, address = "0:7C00", count = 7 });
        var txt = ds["lines"]?.AsArray().Select(l => S(l?["text"])).ToList() ?? new();
        var want = new[] { "cli", "mov al, 0x41", "mov dx, 0xef0", "out dx, al", "mov ax, 0x1234", "inc ax", "jmp short 0x7c0a" };
        Check("x86_disassemble_16", txt.SequenceEqual(want) && S(ds["bits"]) == "16", string.Join(" | ", txt));
        var ds32 = await Call("disassemble", new { session = h, address = "0:7C07", count = 1, cpu = "32" });
        Check("x86_disassemble_32", S(ds32["bits"]) == "32" && S(ds32["lines"]?[0]?["text"]) == "mov eax, 0xeb401234", ds32.ToString());
        var mp = await Call("read_memory", new { session = h, spec = "phys:7C00", length = 4 });
        var ml = await Call("read_memory", new { session = h, spec = "cs:7C00", length = 4 });
        var mbad = await Call("read_memory", new { session = h, spec = "phys:0xA0000", length = 4 });
        Check("x86_read_memory", S(mp["hex"]) == "FA B0 41 BA" && S(ml["hex"]) == "FA B0 41 BA" && mbad.IsError, $"{mp} / {ml} / {mbad}");
        var wm = await Call("write_memory", new { session = h, spec = "phys:600", data = "AB CD" });
        var rm = await Call("evaluate", new { session = h, expr = "w:phys:600" });
        Check("x86_write_memory", L(rm["value"]) == 0xCDAB, $"{wm} / {rm}");
        var tr = await Call("trace", new { session = h, count = 2 });
        Check("x86_trace", (tr["lines"]?.AsArray().Count ?? 0) == 2 && S(tr["lines"]?[1]?["text"]).StartsWith("jmp"), tr.ToString());
        var sv = await Call("save_state", new { session = h, name = "mid" });
        Check("x86_state_refused_mid_frame", sv.IsError, sv.ToString());
        var cs = await Call("call_stack", new { session = h });
        Check("x86_call_stack", !cs.IsError && cs["frames"] != null, cs.ToString());
        await Call("run", new { session = h, frames = 2 });
        var sv2 = await Call("save_state", new { session = h, name = "a" });
        var ld = await Call("load_state", new { session = h, name = "a" });
        var shot = await Call("screenshot", new { session = h });
        Check("x86_states_screenshot", !sv2.IsError && !ld.IsError && shot.Images.Count == 1 && IsPng(shot.Images[0]), $"{sv2} / {shot}");
        var off = await Call("status", new { session = h });
        Check("x86_debugger_off_when_idle", S(off["debuggerOn"]) == "false", S(off["debuggerOn"]));
        // the debugger on (a breakpoint nothing reaches) gives the same machine as off: 120 frames from power-on in both
        var o2 = await Call("open_rom", new { path = f, machine = "classic" });
        string h2 = S(o2["session"]);
        await Call("breakpoint_add", new { session = h2, spec = "0:0500" });
        await Call("run", new { session = h2, frames = 120 });
        var g2 = await Call("registers", new { session = h2 });
        Check("x86_debugger_on_same_machine", S(g2["registers"]?["eax"]) == S(g0["registers"]?["eax"]) && L(g2["cycle"]) == L(g0["cycle"]),
            $"on {S(g2["registers"]?["eax"])} cycle {S(g2["cycle"])}, off {S(g0["registers"]?["eax"])} cycle {S(g0["cycle"])}");
        foreach (var x in new[] { h0, h, h2 }) await Call("close_session", new { session = x }, log: false);
    }

    private static async Task X86Win98Suite(string hdd, string hdd2, string state, string machine, int frames)
    {
        if (!File.Exists(hdd)) { Check("x86_win98_disk", false, $"no --hdd '{hdd}'"); return; }
        if (frames <= 0) frames = state.Length > 0 ? 70 : 9000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var o = await Call("open_rom", new { path = hdd, machine, hdd2 = hdd2.Length > 0 ? hdd2 : null, state = state.Length > 0 ? state : null });
        string h = S(o["session"]);
        Check("x86_win98_open", !o.IsError && S(o["machine"]).StartsWith("WIN98", StringComparison.OrdinalIgnoreCase), o.ToString());
        var r = await Call("run", new { session = h, frames });
        var shot = await Call("screenshot", new { session = h });
        Check("x86_win98_desktop", S(r["outcome"]) == "frames" && L(shot["colours"]) >= 8 && L(shot["width"]) >= 640, $"{frames} frames in {sw.Elapsed.TotalSeconds:F0} s; {shot}");
        if (shot.Images.Count > 0) File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "x86_win98_desktop.png"), shot.Images[0]);
        var rg0 = await Call("registers", new { session = h });
        Check("x86_win98_registers", S(rg0["mode"]) is "protected32" or "protected16" or "v86" && S(rg0["control"]?["paging"]) == "true",
            $"{S(rg0["mode"])} {S(rg0["pc"])} cr0 {S(rg0["control"]?["cr0"])} cr3 {S(rg0["control"]?["cr3"])}: {S(rg0["instruction"])}");
        // the keyboard controller: a key press makes Windows read port 60h
        var bp = await Call("breakpoint_add", new { session = h, spec = "port:60", kind = "read" });
        var rk = await Call("input", new { session = h, keys = "{shift}" });
        var rb = S(rk["outcome"]) == "break" ? rk : await Call("run", new { session = h, frames = 60 });
        var brk = rb["break"];
        Check("x86_win98_port_breakpoint", !bp.IsError && S(brk?["reason"]) == "port read" && S(brk?["port"]) == "$0060", rb.ToString());
        var rg = await Call("registers", new { session = h });
        var ds = await Call("disassemble", new { session = h, count = 4 });
        var mem = await Call("read_memory", new { session = h, spec = "cs:eip", length = 8 });
        string first = S(ds["lines"]?[0]?["bytes"]).Replace(" ", "");
        Check("x86_win98_disasm_memory", !ds.IsError && !mem.IsError && first.Length > 0 && S(mem["hex"]).Replace(" ", "").StartsWith(first) && !S(ds["lines"]?[0]?["text"]).StartsWith("db"),
            $"{S(rg["mode"])} {S(rg["pc"])}: {string.Join(" | ", ds["lines"]?.AsArray().Select(l => S(l?["text"])) ?? Array.Empty<string>())}; mem {S(mem["hex"])} phys {S(mem["physical"])}");
        await Call("breakpoint_remove", new { session = h, all = true });
        var s1 = await Call("step", new { session = h, mode = "into", count = 3 });
        Check("x86_win98_step", S(s1["break"]?["reason"]) == "step", s1.ToString());
        var cs = await Call("call_stack", new { session = h });
        Check("x86_win98_call_stack", !cs.IsError, $"depth {S(cs["depth"])}: {cs}");
        await Call("run", new { session = h, frames = 1 });
        await Call("debug_port", new { session = h, action = "start" });
        await Call("run", new { session = h, frames = 3 });
        var ev = await Call("debug_port", new { session = h, action = "events", limit = 20 });
        var evs = ev["events"]?.AsArray() ?? new JsonArray();
        var all = await Call("debug_port", new { session = h, action = "events", limit = 4096 });
        Check("x86_win98_logs", (all["events"]?.AsArray().Any(e => S(e?["kind"]) == "irq") ?? false) && evs.Count > 0, $"{evs.Count} events, e.g. {(evs.Count > 0 ? evs[0]!.ToJsonString() : "")}");
        await Call("debug_port", new { session = h, action = "stop" });
        var offst = await Call("status", new { session = h });
        Check("x86_win98_debugger_off", S(offst["debuggerOn"]) == "false", S(offst["debuggerOn"]));
        await Call("close_session", new { session = h }, log: false);
    }
}
