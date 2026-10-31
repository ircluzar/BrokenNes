using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes.Workshop.Sega;
using NesEmulator.Debugging;
using NesEmulator.Sega;
using NesEmulator.Systems;
using Debugger = NesEmulator.Debugging.Debugger;

namespace BrokenNes.Workshop.Debug;

/// <summary>
/// <c>--debug-test --only sega</c>: the debugger on the Sega CPUs (BrokenDev Timeflow T0). The test ROMs of <see cref="SegaDebugTestRoms"/> on the Master System, the Game Gear and
/// the Genesis (its 68000 and its Z80): a breakpoint at the exact instruction, a planted write through a mirror caught by a watch, step over / out across calls and
/// interrupts, the call stack through interrupts, the trace against an independent count (the boards' own instruction hooks, the 68000's instruction counter, the Z80's
/// loop arithmetic), the disassemblers against the cores (every opcode's length as the CPU runs it) and known code, frame identity with the debugger attached and
/// breaking; each with a negative control.
/// </summary>
internal static partial class DebugTestCli
{
    // ================================================================================================================================ Master System / Game Gear
    private static void MasterSystem(string dir, bool gg)
    {
        string tag = gg ? "GG" : "SMS";
        var kind = gg ? ConsoleKind.GameGear : ConsoleKind.MasterSystem;
        string ext = gg ? ".gg" : ".sms";
        var (rom, a, sym) = SegaDebugTestRoms.MasterSystem(gg);
        DebugTestRoms.Write(dir, "debugtest" + ext, rom);
        string symPath = DebugTestRoms.Write(dir, $"debugtest_{(gg ? "gg" : "sms")}.sym", sym);
        DebugHost Open() { var h = DebugHost.Open(rom, "debugtest" + ext, kind); h.Dbg.Symbols.Load(symPath, gg ? "gg" : "sms"); return h; }
        BOARD_SMS Board(DebugHost h) => ((SmsDebugMachine)h.Machine).Board;

        using (var h = Open())
        {
            Check($"{tag}: the debugger opens the board", h.Machine.Console == (gg ? "gg" : "sms") && h.Machine.Cpus.Count == 1 && h.Machine.Main.Name == "Z80", $"{h.Machine.Console}, {h.Machine.Cpus.Count} CPU, {h.Machine.Main.Name}");
            var bp = h.Dbg.Add("sub2", AccessKind.Exec);
            var o = h.Runner.Run(10);
            var b = h.Dbg.LastBreak;
            Check($"{tag}: exec breakpoint by symbol stops at its PC", o == RunOutcome.Break && b?.Pc == a["sub2"] && bp.Hits == 1 && b?.PcSymbol == "sub2", $"outcome {o}, {b}, hits {bp.Hits}");
            var st = h.Dbg.CallStack();
            Check($"{tag}: call stack from CALL", st.Count == 2 && st[0].Kind == "CALL" && st[0].CallerPc == a["call_sub1"] && st[0].ReturnPc == a["after_call"] && st[0].Target == a["sub1"]
                                                && st[1].Target == a["sub2"] && st[1].CallerPc == a["sub1"] && st[1].ReturnPc == a["sub1_ret"] && st[1].TargetSymbol == "sub2",
                  string.Join(" | ", st));
            var tail = h.Dbg.TraceTail(3);
            Check($"{tag}: trace ring holds the last instructions", tail.Length == 3 && tail[0].Pc == a["call_sub1"] && tail[1].Pc == a["sub1"] && tail[2].Pc == a["sub2"], string.Join(", ", tail.Select(t => t.Pc.ToString("X4"))));
            string line = h.Dbg.FormatTrace(tail[1]);
            Check($"{tag}: trace line with symbols and the Z80's registers", line.Contains("CALL sub2") && line.Contains(" A:") && line.Contains(" IX:") && line.Contains(" SP:DF") && line.Contains("Fr:0"), line);
            h.Dbg.TrySetRegisterForTest("A", 0x42);
            h.Dbg.TrySetRegisterForTest("IX", 0x1234);
            bool ra = h.Machine.Main.TryGetRegister("A", out long av), rx = h.Machine.Main.TryGetRegister("IX", out long ix);
            Check($"{tag}: registers are read and written", ra && av == 0x42 && rx && ix == 0x1234 && !h.Machine.Main.TryGetRegister("Q", out _),
                  $"A = {av:X2}, IX = {ix:X4}");
            h.Dbg.Remove(bp.Id);
            o = h.Runner.Step(() => h.Dbg.StepOut());
            Check($"{tag}: step out of sub2 lands after its CALL", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["sub1_ret"] && h.Dbg.CallStack().Count == 1, $"{h.Dbg.LastBreak}");
            o = h.Runner.Step(() => h.Dbg.StepOut());
            Check($"{tag}: step out again", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["after_call"] && h.Dbg.CallStack().Count == 0, $"{h.Dbg.LastBreak}");
            o = h.Runner.Run(3);
            Check($"{tag}: removed breakpoint no longer stops", o == RunOutcome.Frames, $"outcome {o}");
        }

        // step over a CALL, many times: an interrupt taken during some of them changes nothing
        using (var h = Open())
        {
            var at = h.Dbg.Add("call_sub1", AccessKind.Exec);
            int ok = 0, withIrq = 0, n = 0;
            string bad = "";
            for (; n < 400; n++)
            {
                var o = h.Runner.Run(10);
                if (o != RunOutcome.Break || h.Dbg.LastBreak?.Pc != a["call_sub1"]) { bad = $"run {n}: {o} {h.Dbg.LastBreak}"; break; }
                h.Dbg.SetEnabled(at.Id, false);
                byte irq0 = h.Machine.Main.Peek(0xC100);
                o = h.Runner.Step(() => h.Dbg.StepOver());
                h.Dbg.SetEnabled(at.Id, true);
                if (o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["after_call"] && h.Dbg.LastBreak.Reason == "step-over") ok++;
                else if (bad == "") bad = $"step {n}: {o} {h.Dbg.LastBreak}";
                if (h.Machine.Main.Peek(0xC100) != irq0) withIrq++;
            }
            Check($"{tag}: step over CALL lands after it, interrupts or not", ok == n && n == 400 && withIrq > 0, $"{ok} of {n} at after_call, {withIrq} with a frame interrupt inside; {bad}");
            h.Dbg.Remove(at.Id);
            var at2 = h.Dbg.Add("call_sub1", AccessKind.Exec);
            h.Runner.Run(10); h.Dbg.Remove(at2.Id);
            var o2 = h.Runner.Step(() => h.Dbg.StepInto());
            Check($"{tag} negative: step into the same CALL lands in sub1", o2 == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["sub1"], $"{h.Dbg.LastBreak}");
        }

        // the call stack through the frame interrupt, and step out of the handler
        using (var h = Open())
        {
            var irq = h.Dbg.Add("irq", AccessKind.Exec);
            List<CallFrame> st = new();
            int tries = 0;
            for (; tries < 20; tries++)
            {
                var o = h.Runner.Run(5);
                st = h.Dbg.CallStack().ToList();
                if (o == RunOutcome.Break && st.Count >= 3) break;
            }
            var top = st.LastOrDefault();
            Check($"{tag}: the call stack through the frame interrupt (IRQ over CALL over CALL)", st.Count == 3 && top?.Kind == "IRQ" && top.Target == 0x38 && st[1].Target == a["sub2"] && st[0].Target == a["sub1"]
                                                                                              && top.ReturnPc >= a["sub2"] && top.ReturnPc <= a["sub2_ret"],
                  $"after {tries + 1} breaks: {string.Join(" | ", st)}");
            h.Dbg.Remove(irq.Id);
            uint back = top?.ReturnPc ?? 0;
            var o2 = h.Runner.Step(() => h.Dbg.StepOut());
            Check($"{tag}: step out of the interrupt handler returns where it interrupted", o2 == RunOutcome.Break && h.Dbg.LastBreak?.Pc == back && h.Dbg.CallStack().Count == 2, $"{h.Dbg.LastBreak} (expected ${back:X4})");
        }

        // a planted write through the work RAM mirror; ports by their mirrors; VRAM through the data port
        using (var h = Open())
        {
            var w = h.Dbg.Add("$C011", AccessKind.Write);
            var neg = h.Dbg.Add("$C012", AccessKind.ReadWrite);
            var o = h.Runner.Run(10);
            var b = h.Dbg.LastBreak;
            Check($"{tag}: a watch on $C011 catches the write through $E011", o == RunOutcome.Break && b?.BreakpointId == w.Id && b.Address == 0xE011 && b.AccessPc == a["mirror_write"] && b.Pc == a["port_write"], $"{b}");
            h.Dbg.Remove(w.Id);
            var w2 = h.Dbg.Add("$E011", AccessKind.Write);
            o = h.Runner.Run(10);
            Check($"{tag}: a mirror address in the spec folds", o == RunOutcome.Break && h.Dbg.LastBreak?.BreakpointId == w2.Id && w2.Resolved == "cpu:$C011", $"{h.Dbg.LastBreak} ({w2.Resolved})");
            h.Dbg.Remove(w2.Id);
            Check($"{tag} negative: the byte beside it stays silent", neg.Hits == 0, $"$C012: {neg.Hits} hits");
            var pw = h.Dbg.Add("io:$BE", AccessKind.Write);
            o = h.Runner.Run(10);
            b = h.Dbg.LastBreak;
            Check($"{tag}: a watch on the data port $BE catches OUT ($80) (a mirror)", o == RunOutcome.Break && b?.BreakpointId == pw.Id && (b.Address & 0xFF) == 0x80 && b.AccessPc == a["port_write"], $"{b}");
            h.Dbg.Remove(pw.Id);
            var vw = h.Dbg.Add("vram:$3800-$3FFF", AccessKind.Write, condition: "value == [counter]");
            var vneg = h.Dbg.Add("vram:$0000-$37FF", AccessKind.Write);
            o = h.Runner.Run(10);
            b = h.Dbg.LastBreak;
            Check($"{tag}: a VRAM watch sees the data port's write (the VDP's address register)", o == RunOutcome.Break && b?.BreakpointId == vw.Id && b.AccessPc == a["port_write"], $"{b}");
            o = h.Runner.Run(10);
            Check($"{tag} negative: VRAM the program never writes stays silent", vneg.Hits == 0, $"{vneg.Hits} hits on $0000-$37FF");
            h.Dbg.Remove(vw.Id); h.Dbg.Remove(vneg.Id);
            var c = h.Dbg.Add("after_call", AccessKind.Exec, condition: "[counter] == 3 && SP == $DFF0");
            o = h.Runner.Run(30);
            Check($"{tag}: condition with a symbol and a register", o == RunOutcome.Break && h.Dbg.ReadVariable("counter")[0] == 3, $"{h.Dbg.LastBreak}");
        }

        // banked symbols: the function in bank 2 breaks; the same address in bank 1 never does
        using (var h = Open())
        {
            var b2 = h.Dbg.Add("bank2_func", AccessKind.Exec);
            var b1 = h.Dbg.Add("bank1_func", AccessKind.Exec);
            var any = h.Dbg.Add("$4000", AccessKind.Exec, tracepoint: true);
            var o = h.Runner.Run(10);
            Check($"{tag}: a banked symbol breaks in its bank", o == RunOutcome.Break && h.Dbg.LastBreak?.BreakpointId == b2.Id && b2.Resolved == "rom:$8000", $"{h.Dbg.LastBreak} ({b2.Resolved})");
            o = h.Runner.Run(10);
            Check($"{tag} negative: the same address in another bank never breaks", b1.Hits == 0 && any.Hits >= 2 && b1.Resolved == "rom:$4000", $"bank 1: {b1.Hits} hits, any bank: {any.Hits}");
        }

        // the trace against the board's own instruction hook: the same instructions, in the same order
        using (var h = Open())
        {
            var board = Board(h);
            var pcs = new List<ushort>();
            long hooked = 0;
            var mark = h.Dbg.Add("loop", AccessKind.Exec);
            var o = h.Runner.Run(10);
            h.Dbg.Remove(mark.Id);
            board.InstructionHook = pc => { hooked++; pcs.Add(pc); };
            string path = Path.Combine(dir, $"trace_{tag}.txt");
            h.Dbg.StartTraceFile(path, new Debugger.TraceFileOptions());
            long exec0 = h.Dbg.InstructionCount();
            byte irq0 = h.Machine.Main.Peek(0xC100);
            o = h.Runner.Run(5);
            var mark2 = h.Dbg.Add("loop", AccessKind.Exec);
            o = h.Runner.Run(5);
            h.Dbg.Remove(mark2.Id);
            long lines = h.Dbg.StopTraceFile();
            board.InstructionHook = null;
            int irqs = (byte)(h.Machine.Main.Peek(0xC100) - irq0);
            long executed = h.Dbg.InstructionCount() - exec0;
            var tail = h.Dbg.TraceTail(4000);
            bool same = tail.Length == 4000 && pcs.Count >= 4000 && tail.Select(t => (ushort)t.Pc).SequenceEqual(pcs.Skip(pcs.Count - 4000));
            Check($"{tag}: the trace equals the board's own instruction hook (count and order)", o == RunOutcome.Break && lines == hooked && executed + irqs == hooked && same && irqs > 0,
                  $"{lines} trace lines, {hooked} hook calls, {executed} instructions + {irqs} interrupts, last 4000 PCs {(same ? "equal" : "differ")}");
            bool shifted = tail.Select(t => (ushort)t.Pc).SequenceEqual(pcs.Skip(pcs.Count - 4001).Take(4000));
            Check($"{tag} negative: the hook's sequence one instruction earlier differs", !shifted, "the comparison is sensitive to a shift of one");
        }

        // the disassembler: known code, and walking the program by lengths lands on every label
        using (var h = Open())
        {
            var c = h.Machine.Main;
            string Dis(string label) { uint p = a[label]; var ic = c.Classify(c.FetchBytes(p)); return c.Disassembler.Format(p, c.InstructionBytes(p, ic.Length), c.Context, x => h.Dbg.SymbolAt(c, x) is { } s && !s.Contains('+') ? s : null); }
            var lines = new List<string>();
            uint pc = a["known"];
            while (pc < a["known_end"]) { var ic = c.Classify(c.FetchBytes(pc)); lines.Add(c.Disassembler.Format(pc, c.InstructionBytes(pc, ic.Length), c.Context, x => h.Dbg.SymbolAt(c, x) is { } s && !s.Contains('+') ? s : null)); pc += (uint)ic.Length; }
            var want = new[] { "LD (IX+$05),$7F", "BIT 0,(IY-$02)", "LDIR", "LD ($1234),BC", "SRL A", "LD A,IXH", "JP (IY)", "JP main" };
            Check($"{tag}: disassembly of known code", lines.SequenceEqual(want) && pc == a["known_end"], string.Join(" ; ", lines));
            Check($"{tag}: disassembly names call targets", Dis("call_sub1") == "CALL sub1" && Dis("sub2_loop") == "DJNZ sub2_loop" && Dis("mirror_write") == "LD (mirror_target),A" && Dis("port_write") == "OUT ($80),A",
                  $"{Dis("call_sub1")} / {Dis("sub2_loop")} / {Dis("mirror_write")} / {Dis("port_write")}");
            var labels = new[] { "main", "loop", "store_turns", "call_sub1", "after_call", "mirror_write", "port_write", "call_banked", "sub1", "sub1_ret", "sub2", "sub2_loop", "sub2_ret", "known", "known_end" };
            pc = a["loop"]; var hit = new HashSet<string>();
            while (pc <= a["known_end"]) { foreach (var l in labels) if (a[l] == pc) hit.Add(l); if (pc == a["known_end"]) break; pc += (uint)Math.Max(1, c.Classify(c.FetchBytes(pc)).Length); }
            Check($"{tag}: walking the program by instruction lengths lands on every label", labels.Skip(1).All(hit.Contains), $"{hit.Count} of {labels.Length - 1}: missing {string.Join(", ", labels.Skip(1).Where(l => !hit.Contains(l)))}");
            byte saved = c.Peek(a["known"] + 2);
            c.PokeSpace(1, a["known"] + 2, 0x06);
            string changed = Dis("known");
            c.PokeSpace(1, a["known"] + 2, saved);
            Check($"{tag} negative: another displacement byte reads differently", changed == "LD (IX+$06),$7F" && Dis("known") == "LD (IX+$05),$7F", changed);
        }

        // frame identity: attached, watching everything and breaking, against no debugger at all
        using (var plain = DebugHost.Open(rom, "debugtest" + ext, kind, debugger: false))
        using (var h = Open())
        {
            plain.RunPlain(120);
            h.Dbg.Log = null;
            h.Dbg.Add("$0000-$FFFF", AccessKind.ReadWrite, tracepoint: true);
            h.Dbg.Add("io:$00-$FF", AccessKind.ReadWrite, tracepoint: true);
            h.Dbg.Add("sub2", AccessKind.Exec, hits: "%40");
            int breaks = 0;
            while (h.Runner.FramesDone < 120) { if (h.Runner.Run(120 - h.Runner.FramesDone) == RunOutcome.Break) breaks++; }
            Check($"{tag}: attached and breaking, frame-identical to no debugger", h.PictureHash() == plain.PictureHash() && h.RamHash() == plain.RamHash() && breaks > 3,
                  $"picture {h.PictureHash():X16} vs {plain.PictureHash():X16}, RAM {h.RamHash():X16} vs {plain.RamHash():X16}, {breaks} breaks");
            var board = Board(h);
            h.Dbg.Detach();
            Check($"{tag} detached: no hook left", board.DebugHook == null && board.DebugTap == null, "the board's debug hook and tap are null");
        }
    }

    // ================================================================================================================================ Genesis
    private static void Genesis(string dir)
    {
        var (rom, m, z, nm) = SegaDebugTestRoms.Genesis();
        DebugTestRoms.Write(dir, "debugtest.md", rom);
        string nmPath = DebugTestRoms.Write(dir, "debugtest_md_symbols.txt", nm);
        DebugHost Open() { var h = DebugHost.Open(rom, "debugtest.md", ConsoleKind.Genesis); h.Dbg.Symbols.Load(nmPath, "md"); return h; }
        BOARD_MD Board(DebugHost h) => ((MdDebugMachine)h.Machine).Board;
        string Z(string label) => "$" + z[label].ToString("X4");

        using (var h = Open())
        {
            Check("Genesis: the debugger opens the 68000 and its Z80", h.Machine.Console == "md" && h.Machine.Cpus.Count == 2 && h.Machine.Main.Name == "68000" && h.Machine.Cpu("z80") != null,
                  $"{h.Machine.Console}: {string.Join(", ", h.Machine.Cpus.Select(c => c.Id + " " + c.Name))}");
            var bp = h.Dbg.Add("sub2", AccessKind.Exec);
            var o = h.Runner.Run(10);
            var b = h.Dbg.LastBreak;
            Check("68000: exec breakpoint by an nm symbol stops at its PC", o == RunOutcome.Break && b?.Pc == m["sub2"] && bp.Hits == 1 && b?.PcSymbol == "sub2", $"outcome {o}, {b}");
            var st = h.Dbg.CallStack();
            Check("68000: call stack from JSR and BSR", st.Count == 2 && st[0].Kind == "JSR" && st[0].CallerPc == m["call_sub1"] && st[0].ReturnPc == m["after_call"] && st[0].Target == m["sub1"]
                                                    && st[1].Kind == "BSR" && st[1].Target == m["sub2"] && st[1].ReturnPc == m["sub1_ret"],
                  string.Join(" | ", st));
            var tail = h.Dbg.TraceTail(3);
            Check("68000: trace ring holds the last instructions", tail.Length == 3 && tail[0].Pc == m["call_sub1"] && tail[1].Pc == m["sub1"] && tail[2].Pc == m["sub2"], string.Join(", ", tail.Select(t => t.Pc.ToString("X6"))));
            string line = h.Dbg.FormatTrace(tail[0]);
            Check("68000: trace line with symbols and all the registers", line.StartsWith("0002") && line.Contains("JSR sub1") && line.Contains("D0:") && line.Contains("A6:") && line.Contains("SP:00FFFE00") && line.Contains("SR:2"), line);
            h.Dbg.TrySetRegisterForTest("D3", 0x12345678);
            bool rd = h.Machine.Main.TryGetRegister("D3", out long d3), rs = h.Machine.Main.TryGetRegister("SR", out long sr);
            Check("68000: registers are read and written", rd && d3 == 0x12345678 && rs && (sr & 0x2000) != 0, $"D3 = {d3:X8}, SR = {sr:X4}");
            h.Dbg.Remove(bp.Id);
            o = h.Runner.Step(() => h.Dbg.StepOut());
            Check("68000: step out of sub2 lands after its BSR", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == m["sub1_ret"] && h.Dbg.CallStack().Count == 1, $"{h.Dbg.LastBreak}");
            o = h.Runner.Step(() => h.Dbg.StepOut());
            Check("68000: step out again lands after the JSR", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == m["after_call"] && h.Dbg.CallStack().Count == 0, $"{h.Dbg.LastBreak}");
            // PC set: the prefetch is refilled, the next instruction is the one there
            h.Dbg.TrySetRegisterForTest("PC", m["call_sub1"]);
            o = h.Runner.Step(() => h.Dbg.StepInto());
            Check("68000: PC set, then step into the JSR there", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == m["sub1"], $"{h.Dbg.LastBreak}");
        }

        using (var h = Open())
        {
            var at = h.Dbg.Add("call_sub1", AccessKind.Exec);
            int ok = 0, withIrq = 0, n = 0;
            string bad = "";
            for (; n < 300; n++)
            {
                var o = h.Runner.Run(10);
                if (o != RunOutcome.Break || h.Dbg.LastBreak?.Pc != m["call_sub1"]) { bad = $"run {n}: {o} {h.Dbg.LastBreak}"; break; }
                h.Dbg.SetEnabled(at.Id, false);
                long v0 = h.Dbg.Evaluate("[$FF0007]");
                o = h.Runner.Step(() => h.Dbg.StepOver());
                h.Dbg.SetEnabled(at.Id, true);
                if (o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == m["after_call"]) ok++;
                else if (bad == "") bad = $"step {n}: {o} {h.Dbg.LastBreak}";
                if (h.Dbg.Evaluate("[$FF0007]") != v0) withIrq++;
            }
            Check("68000: step over JSR lands after it, vertical interrupts or not", ok == n && n == 300 && withIrq > 0, $"{ok} of {n}, {withIrq} with a vertical interrupt inside; {bad}");
        }

        using (var h = Open())
        {
            var vi = h.Dbg.Add("vint", AccessKind.Exec);
            List<CallFrame> st = new();
            int tries = 0;
            for (; tries < 40; tries++)
            {
                var o = h.Runner.Run(5);
                st = h.Dbg.CallStack().ToList();
                if (o == RunOutcome.Break && st.Count >= 3) break;
            }
            var top = st.LastOrDefault();
            Check("68000: the call stack through the vertical interrupt (VINT over BSR over JSR)", st.Count == 3 && top?.Kind == "VINT" && top.Target == m["vint"] && st[1].Target == m["sub2"] && st[0].Target == m["sub1"]
                                                                                                      && top.ReturnPc >= m["sub2"] && top.ReturnPc <= m["sub2_ret"],
                  $"after {tries + 1} breaks: {string.Join(" | ", st)}");
            h.Dbg.Remove(vi.Id);
            uint back = top?.ReturnPc ?? 0;
            var o2 = h.Runner.Step(() => h.Dbg.StepOut());
            Check("68000: step out of the interrupt handler returns where it interrupted", o2 == RunOutcome.Break && h.Dbg.LastBreak?.Pc == back && h.Dbg.CallStack().Count == 2, $"{h.Dbg.LastBreak} (expected ${back:X6})");
        }

        using (var h = Open())
        {
            var w = h.Dbg.Add("mirror_target", AccessKind.Write);
            var neg = h.Dbg.Add("$FF0012", AccessKind.ReadWrite);
            var o = h.Runner.Run(10);
            var b = h.Dbg.LastBreak;
            Check("68000: a watch on a work RAM variable catches the write through its mirror $E00010", o == RunOutcome.Break && b?.BreakpointId == w.Id && b.Address == 0xE00010 && b.AccessPc == m["mirror_write"] && w.Resolved == "ram:$10-$11",
                  $"{b} ({w.Resolved})");
            h.Dbg.Remove(w.Id);
            var w2 = h.Dbg.Add("ram:$11", AccessKind.Write);
            o = h.Runner.Run(10);
            Check("68000: the low byte of the word, by its RAM offset", o == RunOutcome.Break && h.Dbg.LastBreak?.BreakpointId == w2.Id && h.Dbg.LastBreak.Address == 0xE00011, $"{h.Dbg.LastBreak}");
            h.Dbg.Remove(w2.Id);
            var w3 = h.Dbg.Add("$E00010", AccessKind.Write);
            Check("68000: a mirror address in the spec folds", w3.Resolved == "cpu:$FF0010", w3.Resolved);
            h.Dbg.Remove(w3.Id);
            o = h.Runner.Run(10);
            Check("68000 negative: the word beside it stays silent", neg.Hits == 0, $"$FF0012: {neg.Hits} hits");
        }

        // the Z80: breakpoints, its call stack, its interrupt, the bank window and the 68000's writes into its RAM
        using (var h = Open())
        {
            var zw = h.Dbg.Add("zram:$0005", AccessKind.Write, cpu: "z80");
            var o = h.Runner.Run(5);
            var b = h.Dbg.LastBreak;
            Check("Z80: a watch on its RAM sees the 68000 load it through $A00000 (source OtherCpu)", o == RunOutcome.Break && b?.CpuId == "z80" && b.Source == AccessSource.OtherCpu && b.Value == rom[m["zcode"] + 5],
                  $"{b}");
            h.Dbg.Remove(zw.Id);
            var zs = h.Dbg.Add(Z("zsub"), AccessKind.Exec, cpu: "z80");
            o = h.Runner.Run(10);
            b = h.Dbg.LastBreak;
            var st = h.Dbg.CallStack("z80");
            Check("Z80: exec breakpoint and its call stack", o == RunOutcome.Break && b?.CpuId == "z80" && b.Pc == z["zsub"] && st.Count >= 1 && st[^1].Kind == "CALL" && st[^1].CallerPc == z["zcall"] && st[^1].ReturnPc == z["zafter"],
                  $"{b}; {string.Join(" | ", st)}");
            h.Dbg.Remove(zs.Id);
            o = h.Runner.Step(() => h.Dbg.StepOut("z80"));
            Check("Z80: step out of zsub", o == RunOutcome.Break && h.Dbg.LastBreak?.CpuId == "z80" && h.Dbg.LastBreak.Pc == z["zafter"], $"{h.Dbg.LastBreak}");
            var zc = h.Dbg.Add(Z("zcall"), AccessKind.Exec, cpu: "z80");
            h.Runner.Run(10); h.Dbg.Remove(zc.Id);
            o = h.Runner.Step(() => h.Dbg.StepOver("z80"));
            Check("Z80: step over CALL", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == z["zafter"], $"{h.Dbg.LastBreak}");
            var bw = h.Dbg.Add("m68k:$000100", AccessKind.Read, cpu: "z80");
            o = h.Runner.Run(10);
            b = h.Dbg.LastBreak;
            Check("Z80: a watch on the 68000's ROM through the bank window", o == RunOutcome.Break && b?.BreakpointId == bw.Id && b.Address == 0x8100 && b.AccessPc == z["zsub"] && b.Value == (byte)'S', $"{b}");
            h.Dbg.Remove(bw.Id);
            var mr = h.Dbg.Add("rom:$100", AccessKind.Read);
            var mrCpu = h.Dbg.Add("rom:$100", AccessKind.Read, tracepoint: true, condition: "source == 0");
            o = h.Runner.Run(10);
            b = h.Dbg.LastBreak;
            Check("68000: a ROM watch sees the Z80's bank-window read (source OtherCpu)", o == RunOutcome.Break && b?.CpuId == "main" && b.BreakpointId == mr.Id && b.Source == AccessSource.OtherCpu && b.Address == 0x000100, $"{b}");
            Check("68000 negative: the 68000 itself never reads it", mrCpu.Hits == 0, $"{mrCpu.Hits} reads by the 68000");
            h.Dbg.Remove(mr.Id); h.Dbg.Remove(mrCpu.Id);
            var zi = h.Dbg.Add("$0038", AccessKind.Exec, cpu: "z80");
            o = h.Runner.Run(10);
            st = h.Dbg.CallStack("z80");
            Check("Z80: its interrupt is a frame of the call stack", o == RunOutcome.Break && h.Dbg.LastBreak?.CpuId == "z80" && st.Count > 0 && st[^1].Kind == "IRQ" && st[^1].Target == 0x38, string.Join(" | ", st));
            h.Dbg.Remove(zi.Id);
            o = h.Runner.Step(() => h.Dbg.StepOut("z80"));
            Check("Z80: step out of its interrupt handler", o == RunOutcome.Break && h.Dbg.LastBreak?.CpuId == "z80" && h.Dbg.LastBreak.Pc == st.LastOrDefault()?.ReturnPc, $"{h.Dbg.LastBreak}");
        }

        // the trace against independent counts: the 68000's own instruction counter and the board's hook; the Z80's loop arithmetic
        using (var h = Open())
        {
            var board = Board(h);
            var mark = h.Dbg.Add("loop", AccessKind.Exec);
            var o = h.Runner.Run(10);
            h.Dbg.Remove(mark.Id);
            long hooked = 0; var pcs = new List<uint>();
            board.InstructionHook = pc => { hooked++; pcs.Add(pc & 0xFFFFFF); };
            long instr0 = board.Cpu.Instructions;
            string path = Path.Combine(dir, "trace_md.txt");
            h.Dbg.StartTraceFile(path, new Debugger.TraceFileOptions());
            o = h.Runner.Run(4);
            var mark2 = h.Dbg.Add("loop", AccessKind.Exec);
            o = h.Runner.Run(5);
            h.Dbg.Remove(mark2.Id);
            long lines = h.Dbg.StopTraceFile();
            board.InstructionHook = null;
            long counted = board.Cpu.Instructions - instr0;
            var tail = h.Dbg.TraceTail(3000);
            bool same = tail.Length == 3000 && tail.Select(t => t.Pc).SequenceEqual(pcs.Skip(pcs.Count - 3000));
            Check("68000: the trace equals the CPU's own instruction counter and the board's hook", o == RunOutcome.Break && lines == counted && lines == hooked && same,
                  $"{lines} trace lines, {counted} counted by the 68000, {hooked} hook calls, last 3000 PCs {(same ? "equal" : "differ")}");

            var zl = h.Dbg.Add(Z("zloop"), AccessKind.Exec, cpu: "z80");
            o = h.Runner.Run(10);
            var zc = h.Machine.Cpu("z80")!;
            int Hl() => zc.PeekSpace(MdZ80.SpaceZram, 0x1000) | zc.PeekSpace(MdZ80.SpaceZram, 0x1001) << 8;
            int turns0 = Hl(); byte irq0 = zc.PeekSpace(MdZ80.SpaceZram, 0x1200);
            h.Dbg.SetEnabled(zl.Id, false);
            string zpath = Path.Combine(dir, "trace_md_z80.txt");
            h.Dbg.StartTraceFile(zpath, new Debugger.TraceFileOptions { Cpu = "z80" });
            o = h.Runner.Run(3);
            h.Dbg.SetEnabled(zl.Id, true);
            o = h.Runner.Run(5);
            long zlines = h.Dbg.StopTraceFile();
            // both breaks are at zloop before its INC HL: the file holds the six other instructions of the first turn, the whole turns between, and the INC of the last (recorded as it broke)
            int dTurns = Hl() - turns0;
            int dIrq = (byte)(zc.PeekSpace(MdZ80.SpaceZram, 0x1200) - irq0);
            Check("Z80: the trace equals its loop arithmetic (7 instructions a turn, 8 records an interrupt)", o == RunOutcome.Break && h.Dbg.LastBreak?.CpuId == "z80" && zlines == 7L * dTurns + 8L * dIrq && dTurns > 100 && dIrq > 0,
                  $"{zlines} trace lines, {dTurns} turns, {dIrq} interrupts: {7L * dTurns + 8L * dIrq} expected");
        }

        // the disassembler: known code and the program walked by lengths
        using (var h = Open())
        {
            var c = h.Machine.Main;
            string Line(uint p) { var ic = c.Classify(c.FetchBytes(p)); return c.Disassembler.Format(p, c.InstructionBytes(p, ic.Length), c.Context, x => h.Dbg.SymbolAt(c, x) is { } s && !s.Contains('+') ? s : null); }
            var lines = new List<string>();
            uint pc = m["known"];
            while (pc < m["known_end"]) { lines.Add(Line(pc)); pc += (uint)c.Classify(c.FetchBytes(pc)).Length; }
            var want = new[] { "MOVE.L $FF1234,D0", "MOVEM.L D0-D1/A0-A1,-(A7)", "MOVEM.L (A7)+,D0-D1/A0-A1", "CMPI.W #$1234,D0", "MOVE.W $2(A0,D1.W),D0", "LSL.W #2,D0", "TRAP #1" };
            Check("68000: disassembly of known code", lines.SequenceEqual(want) && pc == m["known_end"], string.Join(" ; ", lines));
            Check("68000: disassembly names targets", Line(m["call_sub1"]) == "JSR sub1" && Line(m["sub1"]) == "BSR.S sub2" && Line(m["sub2_loop"]) == "DBRA D1,sub2_loop" && Line(m["mirror_write"]) == "MOVE.W counter,mirror_target",
                  $"{Line(m["call_sub1"])} / {Line(m["sub1"])} / {Line(m["sub2_loop"])} / {Line(m["mirror_write"])}");
            // a trace record keeps four bytes: the 68000's longer instructions are read back from memory
            var mark = h.Dbg.Add("mirror_write", AccessKind.Exec);
            h.Runner.Run(5); h.Dbg.Remove(mark.Id);
            h.Runner.Step(() => h.Dbg.StepInto());
            string tl = h.Dbg.FormatTrace(h.Dbg.TraceTail(2)[0]);   // the newest record is the instruction the step stopped at
            Check("68000: a trace line of a ten-byte instruction", tl.Contains("MOVE.W counter,mirror_target"), tl);
            var labels = new[] { "loop", "call_sub1", "after_call", "mirror_write", "sub1", "sub1_ret", "sub2", "sub2_loop", "sub2_ret", "vint", "hint", "trap_stub", "known", "known_end" };
            pc = m["start"]; var hit = new HashSet<string>();
            while (pc <= m["known_end"]) { foreach (var l in labels) if (m[l] == pc) hit.Add(l); if (pc == m["known_end"]) break; pc += (uint)Math.Max(2, c.Classify(c.FetchBytes(pc)).Length); }
            Check("68000: walking the program by instruction lengths lands on every label", labels.All(hit.Contains), $"{hit.Count} of {labels.Length}: missing {string.Join(", ", labels.Where(l => !hit.Contains(l)))}");
            var zc = h.Machine.Cpu("z80")!;
            string zl = zc.Disassembler.Format(z["zsub"], zc.InstructionBytes(z["zsub"], 3), zc.Context, null);
            Check("Z80 (sound): disassembly of its program in its RAM", zl == "LD A,($8100)", zl);
        }

        using (var plain = DebugHost.Open(rom, "debugtest.md", ConsoleKind.Genesis, debugger: false))
        using (var h = Open())
        {
            plain.RunPlain(120);
            h.Dbg.Log = null;
            h.Dbg.Add("$000000-$FFFFFF", AccessKind.ReadWrite, tracepoint: true);
            h.Dbg.Add("$0000-$FFFF", AccessKind.ReadWrite, tracepoint: true, cpu: "z80");
            h.Dbg.Add("sub2", AccessKind.Exec, hits: "%40");
            h.Dbg.Add(Z("zsub"), AccessKind.Exec, hits: "%500", cpu: "z80");
            int breaks = 0;
            while (h.Runner.FramesDone < 120) { if (h.Runner.Run(120 - h.Runner.FramesDone) == RunOutcome.Break) breaks++; }
            Check("Genesis: attached (both CPUs watched) and breaking, frame-identical to no debugger", h.PictureHash() == plain.PictureHash() && h.RamHash() == plain.RamHash() && breaks > 3,
                  $"picture {h.PictureHash():X16} vs {plain.PictureHash():X16}, RAM {h.RamHash():X16} vs {plain.RamHash():X16}, {breaks} breaks");
            var board = Board(h);
            var unit = (MdSoundUnit)board.Sound;
            h.Dbg.Detach();
            Check("Genesis detached: no hook left", board.DebugHook == null && board.DebugTap == null && unit.DebugHook == null && unit.DebugTap == null, "the board's and the sound unit's hooks and taps are null");
        }
    }

    // ================================================================================================================================ the disassemblers against the cores
    /// <summary>Every Z80 opcode (base, CB, ED, DD, FD, DD CB, FD CB) run once on the core: the program counter moves by the length the disassembler gives, for every instruction that does not jump.</summary>
    private static void Z80Lengths()
    {
        var host = new Z80Host();
        var cpu = new Z80<FlatBus>(new FlatBus { Host = host });
        int tested = 0, wrong = 0, wrongNeg = 0; var bad = new List<string>();
        var dis = DisasmZ80.Instance;
        IEnumerable<byte[]> Forms()
        {
            for (int op = 0; op < 256; op++) if (op is not (0xCB or 0xED or 0xDD or 0xFD)) yield return new[] { (byte)op };
            for (int op = 0; op < 256; op++) { yield return new byte[] { 0xCB, (byte)op }; yield return new byte[] { 0xED, (byte)op }; }
            foreach (byte p in new byte[] { 0xDD, 0xFD })
            {
                for (int op = 0; op < 256; op++) if (op is not (0xCB or 0xDD or 0xFD or 0xED)) yield return new[] { p, (byte)op };
                for (int op = 0; op < 256; op++) yield return new byte[] { p, 0xCB, 0x02, (byte)op };
            }
        }
        foreach (var form in Forms())
        {
            Array.Clear(host.Mem);
            Array.Copy(form, 0, host.Mem, 0x1000, form.Length);
            for (int i = form.Length; i < 8; i++) host.Mem[0x1000 + i] = 0x00;
            var ic = dis.Classify(form[0], form.Length > 1 ? form[1] : (byte)0, default);
            if (ic.Flow != FlowKind.None) continue;
            cpu.Reset();
            // a block instruction that repeats moves the program counter back: one turn (BC = 1, B = 1) does not repeat
            cpu.PC = 0x1000; cpu.SP = 0x8000; cpu.BC = (ushort)(form.Length == 2 && form[0] == 0xED && (form[1] & 0xF6) == 0xB0 ? 0x0001 : 0x0101); cpu.HL = 0x4000; cpu.DE = 0x5000; cpu.IX = 0x6000; cpu.IY = 0x7000;
            cpu.ExecuteInstruction();
            int moved = (ushort)(cpu.PC - 0x1000);
            tested++;
            if (moved != ic.Length) { wrong++; if (bad.Count < 8) bad.Add($"{Convert.ToHexString(form)}: core {moved}, disassembler {ic.Length} ({dis.Format(0x1000, form, default, null)})"); }
            if (moved != ic.Length + 1) wrongNeg++;
        }
        Check("Z80: every non-jumping opcode's length equals the core's", wrong == 0 && tested > 1400, $"{tested} forms run, {wrong} differ {string.Join("; ", bad)}");
        Check("Z80 negative: lengths one byte longer would differ everywhere", wrongNeg == tested, $"{wrongNeg} of {tested}");
    }

    /// <summary>Every 68000 opcode word the core binds, run once on a flat machine with extension words that keep every address even: the program counter moves by the length
    /// the disassembler gives, for every instruction that neither jumps nor takes an exception.</summary>
    private static void M68kLengths()
    {
        var fm = new M68kFlatMachine();
        var cpu = new M68k<M68kFlatBus>(new M68kFlatBus { M = fm });
        int tested = 0, wrong = 0, wrongNeg = 0, illegal = 0; var bad = new List<string>();
        var dis = DisasmM68k.Instance;
        for (int op = 0; op < 0x10000; op++)
        {
            var ic = dis.Classify((byte)(op >> 8), (byte)op, default);
            string text = dis.Format(0x1000, new byte[] { (byte)(op >> 8), (byte)op, 0x00, 0x10, 0x00, 0x10, 0x00, 0x10, 0x00, 0x10 }, default, null);
            if (text.StartsWith("DC.W") || text.StartsWith("LINE") || text == "ILLEGAL") { illegal++; continue; }
            if (ic.Flow != FlowKind.None || text.StartsWith("STOP") || text.StartsWith("CHK") || text.StartsWith("DIV")) continue;
            Array.Clear(fm.Ram);
            fm.Load(0x1000, (ushort)op, 0x0010, 0x0010, 0x0010, 0x0010, 0x0010);
            for (int v = 2; v < 64; v++) fm.Long(v * 4, 0x0F00);   // every exception lands at $0F00
            cpu.Power();
            for (int i = 0; i < 8; i++) { cpu.D[i] = 2; cpu.A[i] = 0x2000; }
            cpu.SpOther = 0x3000;
            cpu.Pc = 0x1004; cpu.Ir = (ushort)op; cpu.Irc = 0x0010;
            long exc = cpu.ExceptionCounts.Sum();
            cpu.Step();
            if (cpu.ExceptionCounts.Sum() != exc) continue;   // privilege, address error, ...: the program counter is the handler's
            int moved = (int)(cpu.Pc - 4 - 0x1000);
            tested++;
            if (moved != ic.Length) { wrong++; if (bad.Count < 8) bad.Add($"{op:X4} {text}: core {moved}, disassembler {ic.Length}"); }
            if (moved != ic.Length + 2) wrongNeg++;
        }
        Check("68000: every non-jumping opcode's length equals the core's", wrong == 0 && tested > 40000, $"{tested} opcode words run, {illegal} illegal skipped, {wrong} differ {string.Join("; ", bad)}");
        Check("68000 negative: lengths one word longer would differ everywhere", wrongNeg == tested, $"{wrongNeg} of {tested}");
    }

    /// <summary>The real Sega games when they are on this machine: the debugger attached (watching everything, tracing) is frame-identical to no debugger; on the Genesis the
    /// VDP's DMA reads are seen by a work RAM watch.</summary>
    private static void SegaGames()
    {
        var games = new (string name, string path, ConsoleKind kind)[]
        {
            ("Sonic 2 (Master System)", @"X:\EMULATION\Sega Master System\Sonic The Hedgehog 2 (Europe) (v1.1).sms", ConsoleKind.MasterSystem),
            ("Sonic (Game Gear)", @"X:\EMULATION\Sega Game Gear\Sonic The Hedgehog (U) (V1.1) [!].gg", ConsoleKind.GameGear),
            ("Sonic (Genesis)", @"X:\EMULATION\Sega Genesis\Sonic the Hedgehog (JUE) [!].bin", ConsoleKind.Genesis),
        };
        foreach (var (name, path, kind) in games)
        {
            if (!File.Exists(path)) { NotRun(name, $"{path} is not on this machine"); continue; }
            var rom = File.ReadAllBytes(path);
            using var plain = DebugHost.Open(rom, Path.GetFileName(path), kind, debugger: false);
            using var h = DebugHost.Open(rom, Path.GetFileName(path), kind);
            h.Dbg.Log = null;
            var all = h.Dbg.Add(kind == ConsoleKind.Genesis ? "$000000-$FFFFFF" : "$0000-$FFFF", AccessKind.ReadWrite, tracepoint: true);
            var dma = kind == ConsoleKind.Genesis ? h.Dbg.Add("$000000-$FFFFFF", AccessKind.Read, tracepoint: true, condition: "source == 1") : null;
            var zAll = kind == ConsoleKind.Genesis ? h.Dbg.Add("$0000-$FFFF", AccessKind.ReadWrite, tracepoint: true, cpu: "z80") : null;
            h.Dbg.TraceRing = true;
            h.Runner.Run(600);
            plain.RunPlain(600);
            Check($"{name}: debugger attached (watches on every address, the trace on), frame-identical to no debugger", h.PictureHash() == plain.PictureHash() && h.RamHash() == plain.RamHash() && all.Hits > 100000,
                  $"600 frames, picture {h.PictureHash():X16} vs {plain.PictureHash():X16}, {all.Hits} accesses watched{(zAll != null ? $", {zAll.Hits} by the Z80" : "")}");
            if (dma != null) Check($"{name}: a watch sees the VDP's DMA reads (source Dma)", dma.Hits > 1000, $"{dma.Hits} bytes read by DMA");
        }
    }
}
