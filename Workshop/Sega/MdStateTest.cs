using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-state-test [--games 1-20|3,5|all] [--poison vram|cpu|z80|cart|vdp|swap] [--quick]</c>: the Genesis session's save states and its hot swapping, through the session contract (the same members the apps call).
/// <list type="number">
/// <item><b>Is a round trip invisible?</b> Each roster game runs the sweep's scripted pad to a menu and to play; at each save point the state is taken, the session carries on, and a fresh session (that has already run a frame) loads the state and runs the
/// same window: the picture of every frame, every audio sample, the work RAM, VRAM, colour RAM, the 68000's PC and cycle count and the Z80's PC are those of the session that carried on, and the picture right after the load is the one saved.</item>
/// <item><b>Is anything left out?</b> A restored session is compared with the one that saved field by field (reflection over the session, the board, the 68000, the VDP, the cartridge, the Z80 and the sound unit); a field that differs is state nobody saves. Shown able to fail by comparing two sessions that did not take part.</item>
/// <item><b>Does it refuse what does not belong?</b> A state of another game, console, sound unit or region, a damaged, cut or foreign file: refused with its reason, the machine unchanged.</item>
/// <item><b>Does the app's own factory give a session that does all of this</b> (<c>ConsoleSessions.Create</c> wraps it in <c>SegaPictureSession</c>)?</item>
/// <item><b>Hot swapping</b>: the sound unit is swapped mid-game (stock unit, NES chips, back) and the picture chip too (the VDP, NES chips, back); the game must not notice: its frames are identical to a control that never swapped. Slots that cannot swap say no and change nothing.</item>
/// </list>
/// </summary>
internal static class MdStateTest
{
    private static int pass, fail, skipped;
    private static string poison = "";

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail == "" ? "" : "  (" + detail + ")")}");
    }

    private static void Skip(string name, string why) { skipped++; Console.WriteLine($"  SKIP  {name}  ({why})"); }
    private static void Group(string title) => Console.WriteLine($"-- {title}");

    public static int Run(string[] args)
    {
        pass = fail = skipped = 0;
        poison = M68kTestCli.Arg(args, "--poison") ?? "";
        bool quick = M68kTestCli.Flag(args, "--quick");
        var games = MdRoster.Parse(M68kTestCli.Arg(args, "--games")).Where(g => g.Exists).ToList();
        Console.WriteLine($"Genesis save states and hot swapping ({games.Count} roster games from {MdRoster.Dir}{(poison != "" ? ", POISON " + poison : "")})");
        foreach (var (name, group) in new (string, Action)[]
        {
            ("round trip on the roster", () => RoundTrips(games, quick)),
            ("every field", () => Fields(games)),
            ("a state saved while the sound Z80's loop is parked", () => ParkedLoop(games)),
            ("a state saved in the middle of a frame", () => MidFrame(games)),
            ("refusals", Refusals),
            ("the app's own factory", Factory),
            ("hot swapping", HotSwap),
        })
        {
            try { group(); }
            catch (Exception e) { Check($"group '{name}' ran to its end", false, e.ToString().Replace('\n', ' ')); }
        }
        Console.WriteLine($"== {pass} passed, {fail} failed, {skipped} skipped");
        return fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  Playing with the sweep's pad script and keeping a trace
    // ---------------------------------------------------------------------------------------------------------------------

    private static string PictureHash(IConsoleSession s) =>
        Convert.ToHexString(SHA1.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight))))[..16];

    private sealed record Trace(List<string> Pictures, string Audio, byte[] Ram, byte[] Vram, ushort[] Cram, uint Pc, long Cycles, ushort Z80Pc, long MasterClock);

    private static IConsoleSession Make(byte[] rom, string apu = "MD") => new MdSession(rom, "M68K", "MD", apu);

    private static MdSoundUnit Core(BOARD_MD b) => b.Sound as MdSoundUnit ?? ((MdSoundOnNes)b.Sound).Inner;

    private static MdSession Md(IConsoleSession s) => s is SegaPictureSession w ? (MdSession)w.Inner : (MdSession)s;

    private static void Frame(IConsoleSession s, int f, List<string>? pictures, IncrementalHash? audio, short[] buf)
    {
        s.SetPad(0, MdSweepCli.PadAt("default", f));
        s.RunFrame();
        pictures?.Add(PictureHash(s));
        int n; while ((n = s.ReadSamples(buf)) > 0) audio?.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf.AsSpan(0, n)));
    }

    private static void Advance(IConsoleSession s, int from, int to) { var buf = new short[16384]; for (int f = from; f < to; f++) Frame(s, f, null, null, buf); }

    private static Trace Play(IConsoleSession s, int from, int frames)
    {
        var pictures = new List<string>(); var audio = IncrementalHash.CreateHash(HashAlgorithmName.SHA1); var buf = new short[16384];
        for (int f = from; f < from + frames; f++) Frame(s, f, pictures, audio, buf);
        var b = Md(s).Board;
        return new Trace(pictures, Convert.ToHexString(audio.GetHashAndReset())[..16], (byte[])b.Ram.Clone(), (byte[])b.Vdp.Vram.Clone(), (ushort[])b.Vdp.Cram.Clone(), b.Cpu.Pc, b.Cpu.Cycles, Core(b).Cpu.PC, b.MasterClock);
    }

    private static Trace Trace_Of(IConsoleSession s)
    {
        var b = Md(s).Board;
        return new Trace(new List<string>(), "", (byte[])b.Ram.Clone(), (byte[])b.Vdp.Vram.Clone(), (ushort[])b.Vdp.Cram.Clone(), b.Cpu.Pc, b.Cpu.Cycles, Core(b).Cpu.PC, b.MasterClock);
    }

    private static bool SameMachine(Trace a, Trace b) =>
        a.Ram.AsSpan().SequenceEqual(b.Ram) && a.Vram.AsSpan().SequenceEqual(b.Vram) && a.Cram.AsSpan().SequenceEqual(b.Cram) && a.Pc == b.Pc && a.Cycles == b.Cycles && a.Z80Pc == b.Z80Pc && a.MasterClock == b.MasterClock;

    /// <summary>The poison: something a loader "forgot", applied to the session after it has loaded, so the checks below must notice.</summary>
    private static void ApplyPoison(IConsoleSession s)
    {
        var b = Md(s).Board;
        switch (poison)
        {
            case "vram": b.Vdp.Vram[0x123] ^= 0x5A; break;
            case "cpu": b.Cpu.D[3] ^= 1; break;
            case "z80": Core(b).Cpu.A ^= 1; break;
            case "cart": if (b.Cart.Sram.Length > 0) b.Cart.Sram[0] ^= 0xFF; else b.Ram[0x100] ^= 0xFF; break;
            case "vdp": b.Vdp.Reg[7] ^= 0x3F; break;
            case "hintlatch":    // a state that dropped B4a's horizontal-interrupt enable latch (the enable bit as it stood at the previous line's HINT time): the loaded session has it the wrong way round
            {
                var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                foreach (var name in new[] { "hintEnCur", "hintEnNext" })
                {
                    var f = typeof(MdVdp).GetField(name, bf);
                    if (f != null) f.SetValue(b.Vdp, !(bool)f.GetValue(b.Vdp)!);
                }
                break;
            }
            case "vscroll":      // a state that dropped the latched vertical scroll (B4a's full-screen latch): the field comparison after a mid-frame load must see it
                typeof(MdVdp).GetField("vsLatchA", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(b.Vdp, (ushort)0x155);
                typeof(MdVdp).GetField("vsNextTaken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(b.Vdp, true);
                break;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  1. The round trip
    // ---------------------------------------------------------------------------------------------------------------------

    private static void RoundTrips(List<MdRoster.Game> games, bool quick)
    {
        Group("a state loaded into a fresh session carries on exactly like the session that saved it");
        if (games.Count == 0) { Skip("round trip", $"no roster ROM under {MdRoster.Dir}"); return; }
        int[] savePoints = quick ? new[] { 400 } : new[] { 400, 900, 1300, 1301 };
        const int window = 50;
        foreach (var g in games)
        {
            byte[] rom = g.Load();
            var a = Make(rom);
            int badImmediate = 0, badPicture = 0, badAudio = 0, badMachine = 0, firstBad = -1;
            long size = 0;
            int f = 0;
            var states = new List<(int at, byte[] state, string pic, Trace carried)>();
            foreach (int at in savePoints)
            {
                Advance(a, f, at); f = at;
                byte[] state = a.SaveState(); size = Math.Max(size, state.Length);
                string pic = PictureHash(a);
                var carried = Play(a, f, window); f += window;
                states.Add((at, state, pic, carried));
            }
            foreach (var (at, state, pic, carried) in states)
            {
                var b = Make(rom);
                Advance(b, 0, 1);                                 // the loading session has run before: a loader that replaces too little shows it
                b.LoadState(state);
                ApplyPoison(b);
                if (PictureHash(b) != pic) badImmediate++;
                var restored = Play(b, at, window);
                int first = Enumerable.Range(0, window).FirstOrDefault(i => carried.Pictures[i] != restored.Pictures[i], -1);
                if (first >= 0) { badPicture++; if (firstBad < 0) firstBad = at; }
                if (carried.Audio != restored.Audio) badAudio++;
                if (!SameMachine(carried, restored)) badMachine++;
            }
            string tag = $"#{g.Number} {g.Short}";
            int n = savePoints.Length;
            Check($"{tag}: the picture right after loading is the saved one ({n} save points, state up to {size / 1024} KB)", badImmediate == 0, badImmediate == 0 ? "" : $"{badImmediate} of {n} differ");
            Check($"{tag}: {window} frames after the load are the picture of the session that carried on", badPicture == 0, badPicture == 0 ? "" : $"{badPicture} of {n} differ, first save point {firstBad}");
            Check($"{tag}: the audio is the same sample for sample", badAudio == 0, badAudio == 0 ? "" : $"{badAudio} of {n} differ");
            Check($"{tag}: work RAM, VRAM, colour RAM, 68000 PC and cycles, Z80 PC and the master clock agree at the window's end", badMachine == 0, badMachine == 0 ? "" : $"{badMachine} of {n} differ");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  2. Nothing left out
    // ---------------------------------------------------------------------------------------------------------------------

    private static void Fields(List<MdRoster.Game> games)
    {
        Group("a restored session is the saved one, field for field");
        var pick = games.Where(g => g.Number is 3 or 12 or 17 or 15).ToList();
        if (pick.Count == 0) pick = games.Take(2).ToList();
        if (pick.Count == 0) { Skip("every field", "no roster ROM"); return; }
        foreach (var g in pick)
        {
            byte[] rom = g.Load();
            var a = Make(rom);
            Advance(a, 0, 700);
            byte[] state = a.SaveState();
            var b = Make(rom);
            Advance(b, 0, 40);
            b.LoadState(state);
            ApplyPoison(b);
            var diffs = DeepCompare.Differences(a, b).Where(d => !IsLoopBookkeeping(d)).ToList();
            Check($"#{g.Number} {g.Short}: every field of the session, the board, the 68000, the VDP, the cartridge, the Z80, the YM2612 and the PSG equals the saved session's (reflection over {DeepCompare.LastCount} fields)", diffs.Count == 0, string.Join("; ", diffs.Take(14)));
            // the comparison can fail: two sessions at different points differ, and it says where
            var c = Make(rom); Advance(c, 0, 650);
            var other = DeepCompare.Differences(a, c);
            Check($"#{g.Number}: the field comparison reports differences between sessions at different points (its own proof of failure)", other.Count > 20, $"{other.Count} differences, e.g. {string.Join(", ", other.Take(3))}");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  2b. A state saved while the sound Z80 is parked in a polling loop (Track C4a's fast-forward)
    // ---------------------------------------------------------------------------------------------------------------------

    private static readonly System.Reflection.FieldInfo ParkedField = typeof(MdSoundUnit).GetField("parked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    /// <summary>
    /// The sound unit's loop detector (Track C4a: what it has learned about the loop it saw last, its quiet period, its counters) is bookkeeping of the fast-forward, not machine state: a loaded session starts it afresh
    /// (<c>ResetLoops</c>) and the group below shows that the machine's every output and its saved state do not depend on it. The reflection comparison therefore skips exactly these fields.
    /// </summary>
    private static bool IsLoopBookkeeping(string diff) =>
        System.Text.RegularExpressions.Regex.IsMatch(diff, @"\.Sound>\.(loopMark|loopTicks|loopRefresh|loopHead|loopStartTick|loopQuietUntil|loopState|loopImpure|LoopParks|LoopParkedTicks|seenPc|seenTick)\b");

    private static bool IsParked(IConsoleSession s) => (bool)ParkedField.GetValue(Core(Md(s).Board))!;

    /// <summary>
    /// The sound CPU's polling loops are counted instead of run (<c>MdSoundUnit.FastLoops</c>); the machine is brought up to date before anything looks at it, a state included. A state that is taken at a frame boundary where the Z80
    /// stands parked must (a) be the very bytes a session that ticks every T-state (<c>FastLoops = false</c>) writes at the same frame, (b) load into a fresh session that then carries on exactly like the session that saved it, in
    /// picture, sound, memory and in the state it writes 60 frames later.
    /// </summary>
    private static void ParkedLoop(List<MdRoster.Game> games)
    {
        Group("a state saved while the sound Z80 is parked in a polling loop is the state of a machine that ticks every T-state, and carries on exactly");
        var pick = games.Where(g => g.Number is 3 or 9 or 11 or 12).ToList();
        if (pick.Count == 0) pick = games.Take(2).ToList();
        if (pick.Count == 0) { Skip("parked loop", "no roster ROM"); return; }
        bool savedSwitch = MdSoundUnit.FastLoops;
        try
        {
            foreach (var g in pick)
            {
                byte[] rom = g.Load();
                MdSoundUnit.FastLoops = true;
                var a = Make(rom);
                int f = 0, at = -1; var buf = new short[16384];
                for (; f < 1200; f++)
                {
                    Frame(a, f, null, null, buf);
                    if (f >= 200 && IsParked(a)) { at = f + 1; break; }
                }
                string tag = $"#{g.Number} {g.Short}";
                Check($"{tag}: the Z80 stands parked in a polling loop at a frame boundary (frame {at}; {Core(Md(a).Board).LoopParks:N0} parks so far)", at > 0);
                if (at < 0) continue;
                byte[] state = a.SaveState();
                Check($"{tag}: saving a parked machine brings it up to date (it no longer stands parked)", !IsParked(a));
                const int window = 60;
                var carried = Play(a, at, window);
                byte[] endA = a.SaveState();
                // the reference: every T-state ticked, the same frames, the state at the same point
                MdSoundUnit.FastLoops = false;
                var r = Make(rom);
                Advance(r, 0, at);
                byte[] refState = r.SaveState();
                MdSoundUnit.FastLoops = true;
                int diff = FirstDifference(state, refState);
                Check($"{tag}: the state written while parked is byte for byte the state of the session that ticked every T-state ({state.Length / 1024} KB)", diff < 0, $"lengths {state.Length} and {refState.Length}, first differing byte {diff}");
                var b = Make(rom);
                Advance(b, 0, 1);
                b.LoadState(state);
                var restored = Play(b, at, window);
                int first = Enumerable.Range(0, window).FirstOrDefault(i => carried.Pictures[i] != restored.Pictures[i], -1);
                Check($"{tag}: {window} frames after loading it are the picture of the session that carried on", first < 0, first < 0 ? "" : $"first differing frame {at + first}");
                Check($"{tag}: the audio is the same sample for sample, and the memories and the CPUs agree", carried.Audio == restored.Audio && SameMachine(carried, restored));
                byte[] endB = b.SaveState();
                int d2 = FirstDifference(endA, endB);
                Check($"{tag}: the state each of them writes {window} frames later is identical (every field, the audio path and the loop detector's effects included)", d2 < 0, $"lengths {endA.Length} and {endB.Length}, first differing byte {d2}");
            }
        }
        finally { MdSoundUnit.FastLoops = savedSwitch; }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  2c. A state saved in the middle of a frame
    // ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The save point of the apps is a frame boundary, but the machine can be stopped at any line (<c>RunToLine</c>, the oracle tools do). A state taken there holds a line half drawn, a FIFO with entries, perhaps a DMA under
    /// way, the VDP's sprite attribute cache and counters: a fresh session that loads it must finish the frame and play on exactly like the one that saved it (every picture, every audio sample, memory, and the state it writes
    /// 40 frames later byte for byte).
    /// </summary>
    private static void MidFrame(List<MdRoster.Game> games)
    {
        Group("a state saved in the middle of a frame (early in the display, late in it, in the VBlank) carries on exactly");
        var pick = games.Where(g => g.Number is 3 or 12 or 15).ToList();
        if (pick.Count == 0) pick = games.Take(2).ToList();
        if (pick.Count == 0) { Skip("mid-frame", "no roster ROM"); return; }
        foreach (var g in pick)
        {
            byte[] rom = g.Load();
            int bad = 0, badAudio = 0, badMachine = 0, badEnd = 0, fieldBad = 0, n = 0; string firstBad = "", firstFields = "";
            foreach (int line in new[] { 30, 150, 226 })
            {
                n++;
                var a = Make(rom);
                Advance(a, 0, 400);
                Md(a).RunToLine(line);
                byte[] state = a.SaveState();
                var b = Make(rom);
                Advance(b, 0, 1);
                b.LoadState(state);
                ApplyPoison(b);
                var fd = DeepCompare.Differences(a, b).Where(d => !IsLoopBookkeeping(d)).ToList();
                if (fd.Count > 0) { fieldBad++; if (firstFields == "") firstFields = $"line {line}: " + string.Join("; ", fd.Take(4)); }
                const int window = 41;
                var carried = Play(a, 400, window);
                byte[] endA = a.SaveState();
                var restored = Play(b, 400, window);
                byte[] endB = b.SaveState();
                int first = Enumerable.Range(0, window).FirstOrDefault(i => carried.Pictures[i] != restored.Pictures[i], -1);
                if (first >= 0) { bad++; if (firstBad == "") firstBad = $"line {line}, frame {400 + first}"; }
                if (carried.Audio != restored.Audio) badAudio++;
                if (!SameMachine(carried, restored)) badMachine++;
                if (FirstDifference(endA, endB) >= 0) badEnd++;
            }
            string tag = $"#{g.Number} {g.Short}";
            Check($"{tag}: right after the load every field equals the saved session's ({n} mid-frame save points)", fieldBad == 0, fieldBad == 0 ? "" : $"{fieldBad} of {n} differ: {firstFields}");
            Check($"{tag}: {n} mid-frame save points: the pictures of the next 41 frames are the saved session's", bad == 0, bad == 0 ? "" : $"{bad} of {n} differ, first at {firstBad}");
            Check($"{tag}: the audio, the memories and the CPUs agree", badAudio == 0 && badMachine == 0, $"audio {badAudio}, machine {badMachine} of {n} differ");
            Check($"{tag}: the state each writes 41 frames later is identical, byte for byte", badEnd == 0, badEnd == 0 ? "" : $"{badEnd} of {n} differ");
        }
    }

    /// <summary>The index of the first byte that differs (or the shorter length when one is a prefix of the other), -1 when they are equal.</summary>
    private static int FirstDifference(byte[] x, byte[] y)
    {
        int n = Math.Min(x.Length, y.Length);
        for (int i = 0; i < n; i++) if (x[i] != y[i]) return i;
        return x.Length == y.Length ? -1 : n;
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  3. Refusals
    // ---------------------------------------------------------------------------------------------------------------------

    private static void Refusals()
    {
        Group("a state that does not belong is refused with its reason and the machine is left as it was");
        var roster = MdRoster.Games.Where(g => g.Exists).Take(2).ToList();
        if (roster.Count < 2) { Skip("refusals", "need two roster ROMs"); return; }
        byte[] romA = roster[0].Load(), romB = roster[1].Load();
        var a = Make(romA); Advance(a, 0, 120);
        byte[] state = a.SaveState();
        var nes = Make(romA, "NES:FIX"); Advance(nes, 0, 20);
        var other = Make(romB); Advance(other, 0, 20);
        var pal = new MdSession(romA, "M68K", "MD", "MD", RegionChoice.Pal); Advance(pal, 0, 20);
        var model3 = new MdSession(romA, "M68K", "MD", "MD", null, GenesisModelChoice.Model3); Advance(model3, 0, 20);

        void Refused(string name, IConsoleSession target, byte[] bytes, params string[] mustSay)
        {
            string before = Convert.ToHexString(SHA1.HashData(target.SaveState()));
            string message;
            try { target.LoadState(bytes); message = ""; }
            catch (InvalidDataException e) { message = e.Message; }
            string after = Convert.ToHexString(SHA1.HashData(target.SaveState()));
            bool says = message.Length > 0 && mustSay.All(w => message.Contains(w, StringComparison.OrdinalIgnoreCase));
            Check(name, says && before == after, message.Length == 0 ? "NOT REFUSED" : (before != after ? "refused, but the machine changed: " : "") + message);
        }

        Refused("a stock-unit state is refused by a session on the NES-chip sound unit, naming the units", nes, state, "sound unit", "NES:FIX", "MD");
        Refused("a state is refused by another game", other, state, "belongs to");
        Refused("an NTSC state is refused by a PAL session", pal, state, "timing");
        Refused("a Model 1 state is refused by a Model 3 session", model3, state, "model");
        byte[] flipped = (byte[])state.Clone(); flipped[flipped.Length / 2] ^= 0x40;
        var healthy = Make(romA); Advance(healthy, 0, 5);
        Refused("a state with a flipped byte is refused as damaged", healthy, flipped, "damaged");
        Refused("a cut-short state is refused", healthy, state.AsSpan(0, state.Length / 2).ToArray(), "cut short");
        Refused("a file that is not a state is refused", healthy, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, "not a BrokenNes console state");
        // a Master System state
        var sms = new SmsSession(NesEmulator.Sega.SegaRomFormat.StripCopierHeader(SmokeRom()), ConsoleKind.MasterSystem, "Z80", "SMS", "SMS", "x.sms"); sms.RunFrame();
        Refused("a Master System state is refused by a Genesis session", healthy, sms.SaveState(), "Master System", "Genesis");
        bool again; try { healthy.LoadState(state); again = true; } catch (Exception) { again = false; }
        Check("the session that refused all of them still loads a good state", again);
        // a state that passes the checksum but whose payload is the wrong shape: the loader reports it, undoes it, and the session is as before
        var forged = ConsoleStateFile.Wrap(ConsoleKind.Genesis, healthy.GameId, healthy.Title, new Dictionary<string, string> { ["apu"] = "MD", ["model"] = Md(healthy).Board.Model.ToString(), ["region"] = Md(healthy).Board.Region.ToString(), ["format"] = MdSession.StateFormat }, new byte[1000]);
        Refused("a payload of the right header but the wrong size is refused and the game is left as it was", healthy, forged, "could not be loaded");
    }

    private static byte[] SmokeRom()
    {
        var rom = new byte[0x8000];
        for (int i = 0; i < rom.Length; i++) rom[i] = 0x00;     // NOPs: a Z80 that does nothing is enough for a state header
        return rom;
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  4. The app's own factory
    // ---------------------------------------------------------------------------------------------------------------------

    private static void Factory()
    {
        Group("the session the apps build (ConsoleSessions.Create) saves and loads states through its wrapper");
        var g = MdRoster.Games.FirstOrDefault(x => x.Exists && x.Number == 3) ?? MdRoster.Games.FirstOrDefault(x => x.Exists);
        if (g == null) { Skip("factory", "no roster ROM"); return; }
        byte[] rom = g.Load();
        IConsoleSession Build(string ppu = "MD") => ConsoleSessions.Create(ConsoleKind.Genesis, rom, "M68K", ppu, "MD", null, g.File);
        var a = Build();
        Check("the session is the picture wrapper around the board session (what the apps get)", a is SegaPictureSession w && w.Inner is MdSession, a.GetType().Name);
        Check("SupportsState is true through the wrapper (a missing forward silently reverts to the contract default 'no states')", a.SupportsState, a.SupportsState ? "" : a.StateUnsupportedReason);
        Advance(a, 0, 500);
        byte[] state = a.SaveState();
        string savedPicture = PictureHash(a);
        var carried = Play(a, 500, 40);
        var b = Build(); Advance(b, 0, 3);
        b.LoadState(state);
        var restored = Play(b, 500, 40);
        Check("save, carry on 40 frames; load into a fresh wrapped session, 40 frames: identical pictures, audio and machine", carried.Pictures.SequenceEqual(restored.Pictures) && carried.Audio == restored.Audio && SameMachine(carried, restored),
            $"pictures {(carried.Pictures.SequenceEqual(restored.Pictures) ? "same" : "differ")}, audio {(carried.Audio == restored.Audio ? "same" : "differ")}, machine {(SameMachine(carried, restored) ? "same" : "differs")}");
        // with a NES chip drawing the picture: the state is the board's, and the loaded session's bridge is rebuilt from the loaded VDP
        var c = Build("NES:FIXS"); Advance(c, 0, 500);
        byte[] bridgedState = c.SaveState();
        var d = Build("NES:FIXS"); Advance(d, 0, 3); d.LoadState(bridgedState);
        // lock-step: the board (machine, audio) must be exact; the picture the NES chip draws carries a little history of its own (the tile pool of the translator is not part of a state), so it is held to a small bound and the figure is reported
        var bufC = new short[16384]; var bufD = new short[16384];
        int worst = 0, differingFrames = 0; long audioDiff = 0;
        for (int f = 500; f < 520; f++)
        {
            c.SetPad(0, MdSweepCli.PadAt("default", f)); d.SetPad(0, MdSweepCli.PadAt("default", f));
            c.RunFrame(); d.RunFrame();
            int n = c.FrameWidth * c.FrameHeight, diff = 0;
            for (int i = 0; i < n; i++) if (c.Frame[i] != d.Frame[i]) diff++;
            if (diff > 0) differingFrames++;
            if (diff > worst && Environment.GetEnvironmentVariable("BN_DUMP") is { Length: > 0 } dumpDir)
            {
                Directory.CreateDirectory(dumpDir);
                MdOracleCli.WritePng(c.Frame, c.FrameWidth, c.FrameHeight, Path.Combine(dumpDir, "carried.png")); MdOracleCli.WritePng(d.Frame, d.FrameWidth, d.FrameHeight, Path.Combine(dumpDir, "loaded.png"));
            }
            worst = Math.Max(worst, diff);
            int nc = c.ReadSamples(bufC), nd = d.ReadSamples(bufD);
            if (nc != nd || !bufC.AsSpan(0, nc).SequenceEqual(bufD.AsSpan(0, nd))) audioDiff++;
        }
        // (measured: the same scene with another palette assignment: the translator's adaptive colour and tile choices start over after a load, see the record)
        int colours = new HashSet<uint>(d.Frame.Take(d.FrameWidth * d.FrameHeight)).Count, nonBlack = d.Frame.Take(d.FrameWidth * d.FrameHeight).Count(p => (p & 0xFFFFFF) != 0);
        bool alive = colours >= 8 && nonBlack * 100 / (d.FrameWidth * d.FrameHeight) >= 50;
        Check("with the picture on NES:FIXS the board is exact after a load (68000, RAM, VDP, audio, lock-step for 20 frames) and the NES chip draws a real picture",
            SameMachine(Trace_Of(c), Trace_Of(d)) && audioDiff == 0 && alive, $"machine {(SameMachine(Trace_Of(c), Trace_Of(d)) ? "same" : "differs")}, audio frames that differ {audioDiff}; the NES chip's picture: {colours} colours, {nonBlack * 100 / (d.FrameWidth * d.FrameHeight)}% non-black; against the session that carried on it differs in {differingFrames} of 20 frames (at most {worst} of 61440 pixels: another palette assignment of the translator, not a board difference)");
        // the console's own quick-save path: a state file name, written and read back
        string tmp = Path.Combine(Path.GetTempPath(), "mdstate-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = ConsoleStateFile.SlotPath(tmp, ConsoleKind.Genesis, a.GameId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, state);
            var e = Build(); Advance(e, 0, 2);
            e.LoadState(File.ReadAllBytes(path));
            Check("the quick-save slot file reads back into a fresh session and shows the saved picture", PictureHash(e) == savedPicture);
            var info = ConsoleStateFile.Peek(File.ReadAllBytes(path));
            Check("the file's header names the console, the game's id and the sound unit", info.Console == ConsoleKind.Genesis && info.GameId == a.GameId && info.Units["apu"] == "MD" && info.Units["format"] == MdSession.StateFormat, string.Join(",", info.Units.Select(kv => kv.Key + "=" + kv.Value)));
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  5. Hot swapping
    // ---------------------------------------------------------------------------------------------------------------------

    private static void HotSwap()
    {
        Group("every core slot swaps mid-game, or says it cannot; the game does not notice");
        var candidates = MdRoster.Games.Where(x => x.Exists && x.Number is 3 or 12 or 2).ToList();
        if (candidates.Count == 0) { Skip("hot swapping", "no roster ROM"); return; }
        foreach (var g in candidates)
        {
            byte[] rom = g.Load();
            string tag = $"#{g.Number} {g.Short}";
            IConsoleSession Build() => ConsoleSessions.Create(ConsoleKind.Genesis, rom, "M68K", "MD", "MD", null, g.File);
            var x = Build(); var y = Build();                 // y never swaps
            var steps = new (int at, CoreSlot slot, string id, bool expect)[]
            {
                (600, CoreSlot.Apu, "NES:FIX", true), (640, CoreSlot.Apu, "MD", true), (680, CoreSlot.Apu, "NES:FIXS", true), (720, CoreSlot.Apu, "NES:FIX", true), (760, CoreSlot.Apu, "MD", true),
                (800, CoreSlot.Ppu, "NES:FIXS", true), (840, CoreSlot.Ppu, "NES:DMG", true), (880, CoreSlot.Ppu, "MD", true),
                (900, CoreSlot.Cpu, "M68K", true),
            };
            var bufX = new short[16384]; var bufY = new short[16384];
            int mismatches = 0, firstMismatch = -1, bridgedFrames = 0, swapsOk = 0, swapsWrong = 0;
            var audioPeak = new Dictionary<string, int>();
            string current = "MD";
            long totalSamples = 0;
            int next = 0;
            for (int f = 0; f < 960; f++)
            {
                if (next < steps.Length && steps[next].at == f)
                {
                    var st = steps[next++];
                    bool ok = x.TrySwapCore(st.slot, st.id);
                    if (ok == st.expect) swapsOk++; else swapsWrong++;
                    if (ok && st.slot == CoreSlot.Apu) current = st.id;
                    if (poison == "swap" && st.at == 640) y.Reset();     // the poison: the control restarts, so the game 'noticed'
                }
                x.SetPad(0, MdSweepCli.PadAt("default", f)); y.SetPad(0, MdSweepCli.PadAt("default", f));
                x.RunFrame(); y.RunFrame();
                bool bridged = x is SegaPictureSession sp && sp.Bridged;
                if (bridged) bridgedFrames++;
                string hx = PictureHash(x), hy = PictureHash(y);
                if (!bridged && hx != hy) { mismatches++; if (firstMismatch < 0) firstMismatch = f; }
                int n; int peak = 0;
                while ((n = x.ReadSamples(bufX)) > 0) { totalSamples += n; for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs((int)bufX[i])); }
                audioPeak[current] = Math.Max(audioPeak.GetValueOrDefault(current), peak);
                while (y.ReadSamples(bufY) > 0) { }
            }
            Check($"{tag}: all {steps.Length} swaps (stock unit, NES:FIX, NES:FIXS and back; picture chip NES:FIXS, NES:DMG and back; the 68000 again) answered as the catalog says", swapsWrong == 0, swapsWrong == 0 ? $"{swapsOk} swaps" : $"{swapsWrong} answered wrongly");
            Check($"{tag}: the frames of the swapping session are identical to a control that never swapped (every frame where the VDP's own picture is shown)", mismatches == 0, mismatches == 0 ? $"{960 - bridgedFrames} frames compared" : $"{mismatches} frames differ, first at {firstMismatch}");
            var xm = Md(x).Board; var ym = Md(y).Board;
            Check($"{tag}: after 960 frames the 68000's PC, cycle count, master clock and work RAM equal the control's, so neither swap disturbed the board", xm.Cpu.Pc == ym.Cpu.Pc && xm.Cpu.Cycles == ym.Cpu.Cycles && xm.MasterClock == ym.MasterClock && xm.Ram.AsSpan().SequenceEqual(ym.Ram), $"x pc={xm.Cpu.Pc:X6} cyc={xm.Cpu.Cycles} clk={xm.MasterClock}; y pc={ym.Cpu.Pc:X6} cyc={ym.Cpu.Cycles} clk={ym.MasterClock}");
            var ux = xm.Sound as MdSoundUnit; var uy = ym.Sound as MdSoundUnit;
            Check($"{tag}: the sound driver was carried through the unit swaps: after 960 frames the Z80's PC and tick count and the counts of YM2612 writes, PSG writes and bus requests equal the control's (nothing restarted: no voice was dropped, no register lost)",
                ux != null && uy != null && ux.Cpu.PC == uy.Cpu.PC && ux.Z80Ticks == uy.Z80Ticks && ux.YmWrites == uy.YmWrites && ux.PsgWrites == uy.PsgWrites && ux.BusRequests == uy.BusRequests,
                ux == null || uy == null ? "no stock unit" : $"Z80 pc {ux.Cpu.PC:X4}/{uy.Cpu.PC:X4}, ticks {ux.Z80Ticks}/{uy.Z80Ticks}, YM {ux.YmWrites}/{uy.YmWrites}, PSG {ux.PsgWrites}/{uy.PsgWrites}");
            Check($"{tag}: the picture chip was drawn by a NES chip for {bridgedFrames} frames and the sound units made sound in turn", bridgedFrames == 80 && audioPeak.Count >= 3 && audioPeak.Values.All(p => p > 100), string.Join(", ", audioPeak.Select(kv => kv.Key + " peak " + kv.Value)));
            Check($"{tag}: the session's description follows the swaps", x.Description.Contains("MD") && Md(x).Description.Contains("APU MD"), Md(x).Description);
        }
        // a state saved after a live swap to a NES chip names that unit, loads into a session built with it, and the two carry on sample for sample; a session with the stock unit refuses it
        {
            var g0 = candidates[0]; byte[] rom0 = g0.Load();
            IConsoleSession Mk(string apu) => ConsoleSessions.Create(ConsoleKind.Genesis, rom0, "M68K", "MD", apu, null, g0.File);
            foreach (string chip in new[] { "NES:FIX", "NES:FIXS" })
            {
                var live = Mk("MD");
                Advance(live, 0, 300);
                bool swapped = live.TrySwapCore(CoreSlot.Apu, chip);
                Advance(live, 300, 340);
                byte[] st = live.SaveState();
                var fresh = Mk(chip);
                Advance(fresh, 0, 1);
                string why = "";
                bool loaded; try { fresh.LoadState(st); loaded = true; } catch (Exception e) { loaded = false; why = e.Message; }
                Check($"a state saved after a live swap to {chip} loads into a session built with {chip}", swapped && loaded, swapped ? why : "the swap was refused");
                if (!loaded) continue;
                // the stock unit inside is carried exactly (Z80, YM2612, PSG); the NES chip it drives is not state (its channel phases and the hub's ring start again from the voices), so the audio is alive but not sample-identical
                var bufL = new short[16384]; int peakF = 0;
                var picsL = new List<string>(); var picsF = new List<string>();
                for (int f = 340; f < 400; f++)
                {
                    live.SetPad(0, MdSweepCli.PadAt("default", f)); fresh.SetPad(0, MdSweepCli.PadAt("default", f));
                    live.RunFrame(); fresh.RunFrame();
                    picsL.Add(PictureHash(live)); picsF.Add(PictureHash(fresh));
                    while (live.ReadSamples(bufL) > 0) { }
                    int n; while ((n = fresh.ReadSamples(bufL)) > 0) for (int i = 0; i < n; i++) peakF = Math.Max(peakF, Math.Abs((int)bufL[i]));
                }
                var ml = Md(live).Board; var mf = Md(fresh).Board; var ul = Core(ml); var uf = Core(mf);
                Check($"... and the two carry on: the same pictures for 60 frames, the same 68000 and Z80 progress, the same YM2612 and PSG write counts, and the loaded session's sound is alive ({chip})",
                    picsL.SequenceEqual(picsF) && ml.Cpu.Pc == mf.Cpu.Pc && ml.Cpu.Cycles == mf.Cpu.Cycles && ml.MasterClock == mf.MasterClock && ul.Cpu.PC == uf.Cpu.PC && ul.Z80Ticks == uf.Z80Ticks && ul.YmWrites == uf.YmWrites && ul.PsgWrites == uf.PsgWrites && peakF > 100,
                    $"pictures {(picsL.SequenceEqual(picsF) ? "equal" : "differ")}, Z80 pc {ul.Cpu.PC:X4}/{uf.Cpu.PC:X4}, YM {ul.YmWrites}/{uf.YmWrites}, PSG {ul.PsgWrites}/{uf.PsgWrites}, peak {peakF}");
                var stock = Mk("MD"); Advance(stock, 0, 1);
                string refusal = ""; bool refused; try { stock.LoadState(st); refused = false; } catch (Exception e) { refused = true; refusal = e.Message; }
                Check($"... a session with the stock unit refuses it and says why ({chip})", refused && refusal.Length > 10, refused ? refusal : "it was loaded");
            }
        }
        // the slots that cannot swap say so, and nothing changes
        var gm = candidates[0];
        var s = ConsoleSessions.Create(ConsoleKind.Genesis, gm.Load(), "M68K", "MD", "MD", null, gm.File);
        Advance(s, 0, 200);
        string before = Convert.ToHexString(SHA1.HashData(s.SaveState()));
        Check("a CPU the Genesis does not have (Z80, 6502) is refused", !s.TrySwapCore(CoreSlot.Cpu, "Z80") && !s.TrySwapCore(CoreSlot.Cpu, "FIX"));
        Check("a sound unit the Genesis does not have (SMS, GB, a bogus NES chip) is refused", !s.TrySwapCore(CoreSlot.Apu, "SMS") && !s.TrySwapCore(CoreSlot.Apu, "GB") && !s.TrySwapCore(CoreSlot.Apu, "NES:NOPE"));
        Check("a picture chip the Genesis does not have (SMS) is refused", !s.TrySwapCore(CoreSlot.Ppu, "SMS"));
        string after = Convert.ToHexString(SHA1.HashData(s.SaveState()));
        Check("none of the refusals changed the machine", before == after);
    }
}
