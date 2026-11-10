using System;
using System.IO;
using System.Linq;
using NesEmulator.Debugging.Movies;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>The pointer port's checks (docs/projects/widemess-spec.md section 10), in <c>--wms-test</c>: the test ROM WideMess/testroms/pointer reads the registers from inside, with its
/// negative controls (no unlock; a SNES); the host mapping (scaling, letterbox, outside = not present); save states, movies and Timeflow carry the pointer.</summary>
internal static partial class WideMessCli
{
    private static bool PointerTests(string dir)
    {
        string rom = Path.Combine(dir, "pointer.wms"), noUnlock = Path.Combine(dir, "pointer_nounlock.wms"), snes = Path.Combine(dir, "pointer.sfc");
        if (!File.Exists(rom) || !File.Exists(noUnlock) || !File.Exists(snes)) { Console.Error.WriteLine($"missing the pointer test ROMs in {dir} (WideMess/testroms/pointer/build.ps1)"); return false; }

        Console.WriteLine("PT1. The pointer port, read by the game (pointer.wms)");
        using (var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(rom)))
        {
            var w = s.Board.Wram;
            Run(s, 10);
            Check(w[0x108] == 0x57 && (w[0x109] & 0x40) != 0, "unlocked, WMFEAT bit 6 (the pointer port) set", $"WMID {w[0x108]:X2} WMFEAT {w[0x109]:X2}");
            Check(w[0x100] == 0 && w[0x101] == 0 && w[0x102] == 0 && w[0x103] == 0, "no host: the port reads idle (not present, 0,0)", $"{w[0x100]:X2} {w[0x101]:X2} {w[0x102]:X2} {w[0x103]:X2}");
            s.SetPointer(new WmsPointer(345, 200, true, false, false, WmsPointerDevice.Mouse)); Run(s, 1);
            Check(w[0x100] == (345 & 0xFF) && w[0x101] == 1 && w[0x102] == 200 && w[0x103] == 0x01, "hover at 345,200: X low / high, Y, PRESENT only", $"{w[0x100]:X2} {w[0x101]:X2} {w[0x102]:X2} {w[0x103]:X2}");
            Check(w[0x104] == 0x03, "WMPTRMOVE: moved and PRESENT changed", $"{w[0x104]:X2}");
            Check(w.AsSpan(0x100, 5).SequenceEqual(w.AsSpan(0x110, 5)), "the main loop's reads all through the frame agree with the NMI's (latched once a frame)");
            Run(s, 1);
            Check(w[0x103] == 0x01 && w[0x104] == 0, "a still pointer: no move bits", $"{w[0x103]:X2} {w[0x104]:X2}");
            int clicksBefore = w[0x122];
            s.SetPointer(new WmsPointer(345, 200, true, true, false, WmsPointerDevice.Touch)); Run(s, 1);
            Check(w[0x103] == (0x01 | 0x02 | 0x08 | 0x40), "a touch goes down: PRESENT, LEFT, LEFT-down edge, device touch", $"{w[0x103]:X2}");
            Run(s, 1);
            Check(w[0x103] == (0x01 | 0x02 | 0x40), "held: no edge the next frame", $"{w[0x103]:X2}");
            s.SetPointer(new WmsPointer(345, 200, false, false, false, WmsPointerDevice.Touch)); Run(s, 1);
            Check(w[0x103] == (0x10 | 0x40) && w[0x100] == (345 & 0xFF) && w[0x102] == 200, "the finger lifts: not present, the LEFT-up edge, the last X / Y kept", $"{w[0x103]:X2}");
            Check(w[0x122] - clicksBefore == 1, "one click counted by the game", $"{w[0x122] - clicksBefore}");
            s.SetPointer(new WmsPointer(10, 20, true, false, true, WmsPointerDevice.Pen)); Run(s, 1);
            Check(w[0x103] == (0x01 | 0x04 | 0x20 | 0x80), "a pen's barrel button: RIGHT, RIGHT-down edge, device pen", $"{w[0x103]:X2}");
            s.SetPointer(new WmsPointer(0, 0, false, true, true, WmsPointerDevice.Agent)); Run(s, 1);
            Check((w[0x103] & 0x07) == 0 && w[0x103] >> 6 == 3, "not present holds no buttons (normalised); device agent", $"{w[0x103]:X2}");

            // a save state keeps the latch: the next frame's edges are the same after a load
            s.SetPointer(new WmsPointer(200, 100, true, true, false, WmsPointerDevice.Mouse)); Run(s, 1);
            byte[] st = s.SaveState();
            Run(s, 1); byte a = w[0x103];
            s.LoadState(st); Run(s, 1); byte b = w[0x103];
            Check(a == b && a == (0x01 | 0x02), "save state: held after the load as before (no false LEFT-down edge)", $"{a:X2} / {b:X2}");
            s.LoadState(st);
            Check(s.WideMess!.Pointer == new WmsPointer(200, 100, true, true, false, WmsPointerDevice.Mouse), "the state holds the latched pointer", s.WideMess.Pointer.ToString());
        }

        Console.WriteLine("PT2. Negative controls: no unlock, and a SNES");
        using (var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(noUnlock)))
        {
            var w = s.Board.Wram;
            s.SetPointer(new WmsPointer(123, 45, true, true, false, WmsPointerDevice.Mouse)); Run(s, 5);
            Check(s.WideMess is { Unlocked: false } && w[0x102] != 45 && (w[0x103] & 1) == 0, "locked: the registers are a SNES's (the pointer never shows)", $"Y read {w[0x102]:X2}, ST {w[0x103]:X2}");
        }
        using (var s = Session(ConsoleKind.Snes, File.ReadAllBytes(snes)))
        {
            Check(s is IPointerSession { HasPointer: false }, "the SNES console has no pointer port");
            var w = s.Board.Wram;
            s.SetPointer(new WmsPointer(123, 45, true, true, false, WmsPointerDevice.Mouse)); Run(s, 5);
            Check(w[0x108] != 0x57 && w[0x102] != 45, "a SNES: no WideMess, no pointer", $"WMID {w[0x108]:X2} Y {w[0x102]:X2}");
        }

        Console.WriteLine("PT3. The hosts' mapping (window coordinates to console pixels)");
        // a 1280 x 720 window, the 400 x 224 picture letterboxed to 1280 x 716.8 at top 1.6
        var p = WmsPointer.FromHost(640, 360, 0, 1.6, 1280, 716.8, 400, 224, false, false, WmsPointerDevice.Mouse);
        Check(p is { X: 200, Y: 112, Present: true }, "the window's centre is the picture's centre", p.ToString());
        p = WmsPointer.FromHost(1279.9, 718.3, 0, 1.6, 1280, 716.8, 400, 224, true, false, WmsPointerDevice.Mouse);
        Check(p is { X: 399, Y: 223, Present: true, Left: true }, "the bottom-right pixel", p.ToString());
        p = WmsPointer.FromHost(640, 0.5, 0, 1.6, 1280, 716.8, 400, 224, true, false, WmsPointerDevice.Mouse);
        Check(p is { Present: false, Left: false, Y: 0 }, "in the letterbox above the picture: not present, no button", p.ToString());
        p = WmsPointer.FromHost(100, 300, 160, 0, 960, 720, 400, 224, false, false, WmsPointerDevice.Touch);
        Check(p is { Present: false, X: 0 }, "left of a pillarboxed picture: not present", p.ToString());
        p = WmsPointer.FromHost(160 + 960 * 0.75, 720 * 0.25, 160, 0, 960, 720, 400, 224, false, false, WmsPointerDevice.Touch);
        Check(p is { X: 300, Y: 56, Present: true, Device: WmsPointerDevice.Touch }, "a stretched picture (960 x 720 for 400 x 224) maps by each axis", p.ToString());

        Console.WriteLine("PT4. Recordings carry the pointer");
        var pk = new WmsPointer(399, 223, true, true, true, WmsPointerDevice.Agent);
        Check(WmsPointer.Unpack(pk.Pack()) == pk && WmsPointer.Idle.Pack() == 0, "the 32-bit packing round-trips (idle = 0)");
        var mv = new Movie { Console = "wms", Frames = 4 };
        mv.SetPads(0, PadButtons.None, PadButtons.None);
        mv.SetPointer(0, 0); mv.SetPointer(1, pk.Pack()); mv.SetPointer(2, pk.Pack()); mv.SetPointer(3, 0);
        var mv2 = Movie.Parse(mv.ToText());
        Check(mv2.Pointers.Count == 2 && mv2.PointerAt(2) == pk.Pack() && mv2.PointerAt(3) == 0 && mv2.PointerAt(0) == 0, "a movie's pointer lines ('p frame packed') round-trip", $"{mv2.Pointers.Count} changes");
        var ex = new TimelineExtras { Pointer = pk.Pack(), Vars = new long[] { 7 } };
        var ex2 = TimelineExtras.Decode(ex.Encode());
        Check(ex2.Pointer == pk.Pack() && ex2.Vars.SequenceEqual(new long[] { 7 }), "a Timeflow frame's extras carry the pointer (a tagged part)");
        Check(TimelineExtras.Decode(new TimelineExtras().Encode()).Pointer == 0, "an idle frame writes nothing and reads idle");

        // the history logs the pointer: its movie replayed on a fresh machine gives the same game
        using (var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(rom)))
        {
            var h = new DebugHistory(() => s.SaveState(), null, 10, 10, 0);
            var w = s.Board.Wram;
            for (int f = 0; f < 30; f++)
            {
                var live = new WmsPointer(f * 10, f, true, f % 7 == 3, false, WmsPointerDevice.Mouse);
                h.BeforeFrame(PadButtons.None, PadButtons.None, live.Pack(), out uint use);
                s.SetPointer(WmsPointer.Unpack(use)); Run(s, 1); h.AfterFrame();
            }
            int clicks = w[0x122] | w[0x123] << 8;
            var m = h.ToMovie();
            Check(m.Pointers.Count >= 29 && WmsPointer.Unpack(m.PointerAt(13)).X == 130, "the history's movie holds every frame's pointer", $"{m.Pointers.Count} changes");
            using var r = Session(ConsoleKind.WideMess, File.ReadAllBytes(rom));
            for (int f = 0; f < 30; f++) { r.SetPointer(WmsPointer.Unpack(m.PointerAt(f))); Run(r, 1); }
            Check(clicks == 4 && (r.Board.Wram[0x122] | r.Board.Wram[0x123] << 8) == clicks && r.Board.Wram.AsSpan(0x100, 0x30).SequenceEqual(w.AsSpan(0x100, 0x30)), "replaying the movie's pointer gives the same game", $"{clicks} clicks");
        }
        return true;
    }
}
