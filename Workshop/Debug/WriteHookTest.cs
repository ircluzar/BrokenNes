using System;
using System.Linq;
using NesEmulator.Debugging;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Debug;

// The write hooks (Debugger.WriteHooks.cs): every action on the NES test ROM with its negative controls, the no-change proofs (a log hook,
// and hooks removed, leave the machine frame-identical to no debugger; the same hooks give the same run), then block / rewrite / break
// on each other console whose CPU writes the hooks reach.
internal static partial class DebugTestCli
{
    private static void WriteHooks(string dir)
    {
        var (rom, a, mlb) = DebugTestRoms.Nes();
        string mlbPath = DebugTestRoms.Write(dir, "debugtest.mlb", mlb);
        DebugHost Open() { var h = DebugHost.Open(rom, "debugtest.nes"); h.Dbg.Symbols.Load(mlbPath, "nes"); h.Dbg.Log = null; return h; }

        // log: frame, PC, old -> new, the call stack (xval is written inside sub1 -> sub2)
        using (var h = Open())
        {
            var hk = h.Dbg.AddWriteHook("counter", WriteHookAction.Log);
            var hx = h.Dbg.AddWriteHook("xval", WriteHookAction.Log);
            var o = h.Runner.Run(3);
            var log = h.Dbg.HookLog();
            var c = log.Where(r => r.HookId == hk.Id).ToList();
            var x = log.Where(r => r.HookId == hx.Id).ToList();
            bool steps = c.Count > 10 && c.All(r => r.Pc == a["inc10"] && r.Landed == r.Written) && c.Skip(1).All(r => r.Written == (byte)(r.Old + 1) || r.Written == r.Old);
            Check("hooks: log records PC and old -> new, the write lands", o == RunOutcome.Frames && steps && hk.Hits == c.Count, $"{c.Count} records, last: {c.LastOrDefault()?.Text}");
            Check("hooks: log carries the call stack", x.Count > 0 && x[^1].Stack.Contains("sub2") && x[^1].Stack.Contains("sub1") && x[^1].Pc == a["sub2"] + 2, x.LastOrDefault()?.Text ?? "none");
        }

        // break: stops with the write NOT applied; a step lets it land
        using (var h = Open())
        {
            var hb = h.Dbg.AddWriteHook("mirror_target", WriteHookAction.Break);
            var o = h.Runner.Run(10);
            var b = h.Dbg.LastBreak;
            byte now = h.Machine.Main.Peek(0x12);
            Check("hooks: break stops before the write lands", o == RunOutcome.Break && b?.Reason == "write-hook" && b.BreakpointId == hb.Id && b.Address == 0x0812 && b.AccessPc == a["sta_mirror"] && now != b.Value,
                  $"{b}; RAM $12 = ${now:X2}");
            o = h.Runner.Step(() => h.Dbg.StepInto());
            Check("hooks: going on lets the held write land", o == RunOutcome.Break && h.Machine.Main.Peek(0x12) == b!.Value, $"RAM $12 = ${h.Machine.Main.Peek(0x12):X2} (held ${b.Value:X2})");
            h.Dbg.HookBreaksOff = true;
            o = h.Runner.Run(2);
            h.Dbg.HookBreaksOff = false;
            Check("hooks: HookBreaksOff lets the frame run through", o == RunOutcome.Frames, o.ToString());
        }

        // a condition: break only on the value asked for
        using (var h = Open())
        {
            h.Dbg.AddWriteHook("mirror_target", WriteHookAction.Break, condition: "value == $80");
            var o = h.Runner.Run(600);
            Check("hooks: a conditional break fires on its value only", o == RunOutcome.Break && h.Dbg.LastBreak?.Value == 0x80 && h.Machine.Main.Peek(0x12) == 0x7F, $"{h.Dbg.LastBreak}");
        }

        // rewrite: AND mask (the SMB1 $00FC example's shape), XOR, a forced value
        using (var h = Open())
        {
            var hr = h.Dbg.AddWriteHook("mirror_target", WriteHookAction.Rewrite, and: 0x0F);
            h.Runner.Run(20);
            var log = h.Dbg.HookLog();
            Check("hooks: rewrite AND masks every write", log.Count > 10 && log.All(r => r.Landed == (r.Written & 0x0F)) && h.Machine.Main.Peek(0x12) <= 0x0F && hr.Changed > 0,
                  $"{log.Count} records, {hr.Changed} changed, RAM $12 = ${h.Machine.Main.Peek(0x12):X2}, counter ${h.Machine.Main.Peek(0x10):X2}");
            h.Dbg.ClearWriteHooks();
            h.Dbg.ClearHookLog();
            h.Dbg.AddWriteHook("mirror_target", WriteHookAction.Rewrite, xor: 0xFF);
            h.Runner.Run(5);
            var lx = h.Dbg.HookLog();
            Check("hooks: rewrite XOR", lx.Count > 0 && lx.All(r => r.Landed == (byte)~r.Written), lx.LastOrDefault()?.Text ?? "none");
            h.Dbg.ClearWriteHooks();
            h.Dbg.AddWriteHook("mirror_target", WriteHookAction.Rewrite, value: 0x5A, quiet: true);
            long before = h.Dbg.WriteHookRecords;
            h.Runner.Run(5);
            Check("hooks: rewrite to a forced value (quiet: no log)", h.Machine.Main.Peek(0x12) == 0x5A && h.Dbg.WriteHookRecords == before, $"RAM $12 = ${h.Machine.Main.Peek(0x12):X2}");
        }

        // block: the counter never moves; negative control: it moves without the hook
        using (var h = Open())
        {
            h.Runner.Run(2);
            byte c0 = h.Machine.Main.Peek(0x10);
            h.Runner.Run(2);
            byte c1 = h.Machine.Main.Peek(0x10);
            var hb = h.Dbg.AddWriteHook("counter", WriteHookAction.Block);
            h.Runner.Run(10);
            byte c2 = h.Machine.Main.Peek(0x10);
            Check("hooks: block drops the writes (negative control: it counts without)", c0 != c1 && c2 == c1 && hb.Hits > 10 && h.Dbg.HookLog().All(r => r.Landed < 0), $"{c0:X2} -> {c1:X2} free, then {c2:X2} after 10 blocked frames ({hb.Hits} writes)");
            h.Dbg.RemoveWriteHook(hb.Id);
            h.Runner.Run(2);
            Check("hooks: removed, the counter counts again", h.Machine.Main.Peek(0x10) != c2, $"${h.Machine.Main.Peek(0x10):X2}");
        }

        // freeze: in place at once, held against every write
        using (var h = Open())
        {
            h.Runner.Run(2);
            h.Dbg.AddWriteHook("counter", WriteHookAction.Freeze, value: 0x42);
            byte at = h.Machine.Main.Peek(0x10);
            h.Runner.Run(10);
            Check("hooks: freeze holds the value (put in place when added)", at == 0x42 && h.Machine.Main.Peek(0x10) == 0x42 && h.Machine.Main.Peek(0x12) == 0x42, $"added: ${at:X2}; after 10 frames counter ${h.Machine.Main.Peek(0x10):X2}, mirror ${h.Machine.Main.Peek(0x12):X2}");
            h.Machine.Main.Poke(0x10, 0x00);
            h.Dbg.ApplyFreezes();
            Check("hooks: ApplyFreezes puts the value back (after a state load)", h.Machine.Main.Peek(0x10) == 0x42, $"${h.Machine.Main.Peek(0x10):X2}");
        }

        // order: hooks run in the order added, each on the value the one before left; a block ends the chain
        using (var h = Open())
        {
            h.Dbg.AddWriteHook("mirror_target", WriteHookAction.Rewrite, or: 0x80);
            h.Dbg.AddWriteHook("mirror_target", WriteHookAction.Rewrite, and: 0xF0);
            h.Runner.Run(5);
            var log = h.Dbg.HookLog();
            var pairs = log.Chunk(2).Where(p => p.Length == 2).ToList();
            Check("hooks: a chain sees the value the hook before left", pairs.Count > 0 && pairs.All(p => p[1].Written == p[0].Landed && p[1].Landed == ((p[0].Written | 0x80) & 0xF0)), pairs.LastOrDefault()?[1].Text ?? "none");
        }

        // refusals
        using (var h = Open())
        {
            bool r1 = Throws(() => h.Dbg.AddWriteHook("counter", WriteHookAction.Freeze));
            bool r2 = Throws(() => h.Dbg.AddWriteHook("counter", WriteHookAction.Rewrite));
            bool r3 = Throws(() => h.Dbg.AddWriteHook("no_such_symbol", WriteHookAction.Log));
            Check("hooks: NEGATIVE: a freeze without a value, a rewrite without masks, an unknown symbol are refused", r1 && r2 && r3 && h.Dbg.WriteHooks.Count == 0, $"{r1} {r2} {r3}");
        }

        // no change: a log hook over all RAM runs frame-identical to no debugger; removed, no hook is left; the same hooks give the same run
        using (var plain = DebugHost.Open(rom, "debugtest.nes", debugger: false))
        using (var h = Open())
        {
            plain.RunPlain(120);
            h.Dbg.AddWriteHook("$0000-$07FF", WriteHookAction.Log, quiet: true);
            h.Dbg.AddWriteHook("$2000-$401F", WriteHookAction.Log, quiet: true);
            h.Runner.Run(120);
            Check("hooks: log hooks leave the machine frame-identical to no debugger", h.PictureHash() == plain.PictureHash() && h.RamHash() == plain.RamHash() && h.Dbg.WriteHookRecords > 1000,
                  $"picture {h.PictureHash():X16} vs {plain.PictureHash():X16}, RAM {h.RamHash():X16} vs {plain.RamHash():X16}, {h.Dbg.WriteHookRecords} records");
            h.Dbg.ClearWriteHooks();
            Check("hooks: cleared, no tap or hook is left", h.Nes!.DebugHook == null && h.Nes.DebugBus!.DebugTap == null && !h.Dbg.IsArmed, $"armed {h.Dbg.IsArmed}");
            h.Runner.Run(30); plain.RunPlain(30);
            Check("hooks: cleared, still frame-identical", h.PictureHash() == plain.PictureHash() && h.RamHash() == plain.RamHash(), $"{h.RamHash():X16}");
        }
        using (var h1 = Open())
        using (var h2 = Open())
        {
            foreach (var h in new[] { h1, h2 }) { h.Dbg.AddWriteHook("mirror_target", WriteHookAction.Rewrite, and: 0x3C); h.Dbg.AddWriteHook("frames", WriteHookAction.Block, condition: "value & 1"); }
            h1.Runner.Run(90); h2.Runner.Run(90);
            Check("hooks: the same hooks give the same run (deterministic)", h1.RamHash() == h2.RamHash() && h1.PictureHash() == h2.PictureHash() && h1.Dbg.WriteHookRecords == h2.Dbg.WriteHookRecords,
                  $"RAM {h1.RamHash():X16} / {h2.RamHash():X16}, {h1.Dbg.WriteHookRecords} records each");
        }

        // the other consoles: block, a forced rewrite, break-before on their counters
        void Other(string tag, Func<DebugHost> open, string spec, uint addr, string cpu = "main", int frames = 10)
        {
            using (var h = open())
            {
                var c = h.Machine.Cpu(cpu)!;
                h.Runner.Run(frames);
                byte c0 = c.Peek(addr);
                h.Runner.Run(frames);
                byte c1 = c.Peek(addr);
                var hb = h.Dbg.AddWriteHook(spec, WriteHookAction.Block, cpu: cpu);
                h.Runner.Run(frames);
                byte c2 = c.Peek(addr);
                Check($"{tag}: hooks block (negative control: it counts without)", c0 != c1 && c1 == c2 && hb.Hits > 0, $"{c0:X2} -> {c1:X2} free, {c2:X2} blocked ({hb.Hits} writes)");
                h.Dbg.ClearWriteHooks();
                h.Dbg.AddWriteHook(spec, WriteHookAction.Rewrite, value: 0x5A, cpu: cpu);
                h.Runner.Run(frames);
                Check($"{tag}: hooks rewrite to a forced value", c.Peek(addr) == 0x5A, $"${c.Peek(addr):X2}");
                h.Dbg.ClearWriteHooks();
                h.Dbg.AddWriteHook(spec, WriteHookAction.Break, cpu: cpu);
                var o = h.Runner.Run(frames * 4);
                var b = h.Dbg.LastBreak;
                byte held = c.Peek(addr);
                Check($"{tag}: hooks break before the write lands", o == RunOutcome.Break && b?.Reason == "write-hook" && b.CpuId == c.Id && held != b.Value, $"{b}; memory ${held:X2}");
                h.Dbg.ClearWriteHooks();
                h.Runner.Run(1);
                Check($"{tag}: the held write landed when it went on", c.Peek(addr) != held || c.Peek(addr) == b!.Value, $"${c.Peek(addr):X2}");
                h.Runner.Run(1);
                Check($"{tag}: hooks cleared, nothing armed", !h.Dbg.IsArmed, $"armed {h.Dbg.IsArmed}");
            }
        }

        var (sr, _, _, ssym) = DebugTestRoms.Snes();
        string sPath = DebugTestRoms.Write(dir, "debugtest.sym", ssym);
        Other("SNES 65816", () => { var h = DebugHost.Open(sr, "debugtest.sfc", ConsoleKind.Snes); h.Dbg.Symbols.Load(sPath, "snes"); return h; }, "counter", 0x000010);
        var (gr, _, gsym, _) = DebugTestRoms.GameBoy();
        string gPath = DebugTestRoms.Write(dir, "debugtest_gb.sym", gsym);
        Other("Game Boy", () => { var h = DebugHost.Open(gr, "debugtest.gb", ConsoleKind.GameBoy); h.Dbg.Symbols.Load(gPath, "gb"); return h; }, "counter", 0xC010);
        var (mr, _, msym) = SegaDebugTestRoms.MasterSystem(false);
        string mPath = DebugTestRoms.Write(dir, "debugtest_sms.sym", msym);
        Other("Master System", () => { var h = DebugHost.Open(mr, "debugtest.sms", ConsoleKind.MasterSystem); h.Dbg.Symbols.Load(mPath, "sms"); return h; }, "counter", 0xC010);
        var (gg, _, ggsym) = SegaDebugTestRoms.MasterSystem(true);
        Other("Game Gear", () => DebugHost.Open(gg, "debugtest.gg", ConsoleKind.GameGear), "$C010", 0xC010);
        var (md, _, _, _) = SegaDebugTestRoms.Genesis();
        Other("Genesis 68000", () => DebugHost.Open(md, "debugtest.md", ConsoleKind.Genesis), "$FF0001", 0xFF0001);
        Other("Genesis Z80", () => DebugHost.Open(md, "debugtest.md", ConsoleKind.Genesis), "$1200", 0x1200, cpu: "z80", frames: 6);

        // a CPU the hooks do not reach is refused, never quietly ignored
        using (var h = DebugHost.Open(sr, "debugtest.sfc", ConsoleKind.Snes))
            Check("hooks: NEGATIVE: the SPC700 (writes not tapped) is refused", Throws(() => h.Dbg.AddWriteHook("$00F4", WriteHookAction.Log, cpu: "spc")), "refused");
    }

    private static bool Throws(Action a) { try { a(); return false; } catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException) { return true; } }
}
