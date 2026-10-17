using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// Save states through the shared session contract (<see cref="IConsoleSession.SaveState"/> / <see cref="IConsoleSession.LoadState"/>, the container in <c>systems/ConsoleStates.cs</c>) for the Master System and Game Gear
/// sessions. Four questions:
/// <list type="number">
/// <item><b>Is a round trip invisible?</b> Run, save, run, load into a fresh session, run: the picture of every frame, every audio sample, the work RAM, the Z80 and the cycle count are those of the session that carried on, and the
/// picture right after the load (before a frame runs) is the one that was saved. Saved at frames 4 to 12 of a program that writes the VDP during the picture, so a write still waiting for its access slot at the end of the frame is in play.</item>
/// <item><b>Is anything left out?</b> A fresh session that has loaded a state is compared with the session that saved it field by field (reflection over the board, the VDP, the cartridge, the Z80 and the sound unit): a field that
/// differs is state nobody saves. The comparison is shown able to fail by comparing two sessions that did not take part in a round trip.</item>
/// <item><b>Does it refuse what does not belong?</b> A stock-unit state does not load into the FM unit, one game's state does not load into another game, a Master System state does not load into a Game Gear, a PAL state into an NTSC
/// session, and a damaged, cut or foreign file is refused, each with its reason, and the machine is left as it was (its own state before and after is the same bytes).</item>
/// <item><b>Does a session without states say so?</b> The default of the contract throws <see cref="NotSupportedException"/> with a reason; nothing silently does nothing.</item>
/// </list>
/// </summary>
internal static class SessionStateSuite
{
    private static byte[] Hash(SmsSession s) => System.Security.Cryptography.SHA1.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight)));

    private sealed record Trace(List<string> Hashes, List<short> Audio, byte[] Ram, ushort Pc, long Cycles, byte[] Vram, byte[] Cram);

    private static Trace Play(SmsSession s, Dictionary<int, PadButtons> script, int from, int frames)
    {
        var hashes = new List<string>(); var audio = new List<short>(); var buf = new short[16384];
        PadButtons held = 0;
        foreach (var kv in script.Where(k => k.Key <= from).OrderBy(k => k.Key)) held = kv.Value;
        for (int f = from; f < from + frames; f++)
        {
            if (script.TryGetValue(f, out var h)) held = h;
            s.SetPad(0, held);
            s.RunFrame();
            hashes.Add(Convert.ToHexString(Hash(s)));
            int n; while ((n = s.ReadSamples(buf)) > 0) for (int i = 0; i < n; i++) audio.Add(buf[i]);
        }
        return new Trace(hashes, audio, (byte[])s.Board.Ram.Clone(), s.Board.Cpu.PC, s.Board.Cycles, (byte[])s.Board.Vdp.Vram.Clone(), Enumerable.Range(0, 64).Select(i => s.Board.Vdp.CramByte(i)).ToArray());
    }

    /// <summary>
    /// A program that writes one byte to VRAM at the very end of the picture, every frame at a different moment (the delay after the V counter reaches line 191 grows by 13 Z80 cycles a frame, from 13 to 247): the write
    /// lands waiting for its access slot past the end of the line, so some frames end with it still queued in the chip. A state saved then has to carry it.
    /// </summary>
    private static byte[] PendingWriteProbe()
    {
        var a = new Z80Asm(0x100);
        a.Db(0xF3, 0xED, 0x56, 0x31, 0xF0, 0xDF);
        foreach (var (reg, val) in new[] { (0, 0x04), (1, 0x80), (2, 0xFF), (5, 0xFF), (7, 0x00), (8, 0x00), (9, 0x00), (10, 0xFF) }) a.OutImm(0xBF, val).OutImm(0xBF, 0x80 | reg);
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x7F).OutImm(0xBE, 0xD0);                                 // sprite table end
        a.OutImm(0xBF, 0xC0).OutImm(0xBF, 0x81);                                                    // display on
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x44);                                                    // VRAM write address $0400
        a.Db(0x16, 0x01);                                                                           // LD D,1
        a.Label("frame");
        a.Label("w1").InN(0x7E).Db(0xFE, 190).JrNz("w1");                                           // line 190
        a.Label("w2").InN(0x7E).Db(0xFE, 191).JrNz("w2");                                           // line 191
        a.Db(0x42);                                                                                 // LD B,D
        a.Label("dl").Djnz("dl");                                                                   // 13 cycles per count
        a.Db(0x7A).OutN(0xBE);                                                                      // LD A,D; OUT ($BE),A: the write
        a.Db(0x14, 0x7A, 0xFE, 20).JrNz("keep").Db(0x16, 0x01);                                     // INC D; LD A,D; CP 20; JR NZ; LD D,1
        a.Label("keep");
        a.Label("w3").InN(0x7E).Db(0xFE, 191).JrZ("w3");                                            // until the line has moved on
        a.Jr("frame");
        return SmsProg.Rom(a.Assemble(), null, null, 0x100);
    }

    private sealed record Case(string Name, Func<SmsSuiteContext, SmsSession> Make, string Pad, int[] SaveAt, int Window);

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        var cases = new List<Case>
        {
            new("Master System scrolling picture program", c => c.Session(SmokeProgs.Picture(false), "state.sms"), "0:Right", new[] { 40 }, 40),
            new("Game Gear scrolling picture program", c => c.Session(SmokeProgs.Picture(true), "state.gg", ConsoleKind.GameGear), "0:Right", new[] { 40 }, 40),
            new("VDP writes during the picture (double-write probe), saved at each of frames 4 to 12", c => c.Session(VdpSlotProbes.DoubleWrite(true), "state.sms"), "", Enumerable.Range(4, 9).ToArray(), 4),
            new("VDP write sweep through the lines, saved at each of frames 3 to 9", c => c.Session(VdpSlotProbes.WriteSweep(1, 2), "state.sms"), "", Enumerable.Range(3, 7).ToArray(), 4),
            new("a VRAM write at the very end of the picture, at another moment each frame (still queued in the chip when some frames end), saved at each of frames 4 to 44", c => c.Session(PendingWriteProbe(), "state.sms"), "", Enumerable.Range(4, 41).ToArray(), 3),
            new("SMS-FM with nine FM channels sounding", c => c.Session(FmProbes.NineChannels(out _), "state.sms", ConsoleKind.MasterSystem, null, "SMS-FM"), "", new[] { 30 }, 40),
        };
        if (ctx.RomDir is { } dir)
            foreach (var (console, file) in new[] { ("sms", "Astro Warrior (USA).sms"), ("sms", "Shinobi (USA, Europe).sms"), ("sms", "Phantasy Star (USA, Europe) (v1.3).sms"), ("gg", "Columns (U) [!].gg"), ("gg", "Fantasy Zone Gear (JU) [!].gg") })
            {
                string path = Path.Combine(dir, console, file);
                if (!File.Exists(path)) continue;
                bool isGg = console == "gg";
                cases.Add(new($"{file} (scripted play-through)", c => { byte[] rom = SmsRomIo.Load(path, out string inner); return c.Session(rom, inner, isGg ? ConsoleKind.GameGear : ConsoleKind.MasterSystem); }, isGg ? GoldenSuite.PadGg : GoldenSuite.PadSms, new[] { 420, 421 }, 60));
            }

        // ---- 1. the round trip, through the session contract
        foreach (var c in cases)
        {
            var script = SmsRomIo.ParseInput(c.Pad);
            int badPicture = 0, badAudio = 0, badMachine = 0, badImmediate = 0, firstBad = -1;
            long bytes = 0;
            foreach (int at in c.SaveAt)
            {
                var a = c.Make(ctx);
                Play(a, script, 0, at);
                byte[] state = a.SaveState(); bytes = state.Length;
                byte[] pictureAtSave = Hash(a);
                var carried = Play(a, script, at, c.Window);
                var b = c.Make(ctx);
                Play(b, script, 0, 1);                                              // the loading session has run before: a loader that replaces too little shows it
                b.LoadState(state);
                if (ctx.Poison == "sessionstate") b.Board.Vdp.Vram[0x123] ^= 0x5A;   // the poison: one VRAM byte the loader "forgot"
                if (!Hash(b).AsSpan().SequenceEqual(pictureAtSave)) badImmediate++;
                var restored = Play(b, script, at, c.Window);
                int firstPicture = Enumerable.Range(0, c.Window).FirstOrDefault(i => carried.Hashes[i] != restored.Hashes[i], -1);
                if (firstPicture >= 0) { badPicture++; if (firstBad < 0) firstBad = at; }
                if (!carried.Audio.SequenceEqual(restored.Audio)) badAudio++;
                if (!(carried.Ram.AsSpan().SequenceEqual(restored.Ram) && carried.Pc == restored.Pc && carried.Cycles == restored.Cycles && carried.Vram.AsSpan().SequenceEqual(restored.Vram) && carried.Cram.AsSpan().SequenceEqual(restored.Cram))) badMachine++;
            }
            string where = c.SaveAt.Length > 1 ? $" at {c.SaveAt.Length} save points" : "";
            yield return new SmsCheck($"{c.Name}: the picture right after loading is the saved one{where}", badImmediate == 0, badImmediate == 0 ? $"state {bytes} bytes" : $"{badImmediate} of {c.SaveAt.Length} differ");
            yield return new SmsCheck($"{c.Name}: {c.Window} frames after the load are the picture of the session that carried on{where}", badPicture == 0, badPicture == 0 ? "" : $"{badPicture} of {c.SaveAt.Length} differ, first save point frame {firstBad}");
            yield return new SmsCheck($"{c.Name}: the audio is the same sample for sample{where}", badAudio == 0, badAudio == 0 ? "" : $"{badAudio} of {c.SaveAt.Length} differ");
            yield return new SmsCheck($"{c.Name}: work RAM, VRAM, colour RAM, Z80 PC and cycle count are the same at the window's end{where}", badMachine == 0, badMachine == 0 ? "" : $"{badMachine} of {c.SaveAt.Length} differ");
        }

        // ---- 2. nothing left out: field by field
        {
            var a = ctx.Session(VdpSlotProbes.DoubleWrite(true), "fields.sms");
            for (int i = 0; i < 9; i++) a.RunFrame();
            byte[] state = a.SaveState();
            var b = ctx.Session(VdpSlotProbes.DoubleWrite(true), "fields.sms");
            for (int i = 0; i < 3; i++) b.RunFrame();
            b.LoadState(state);
            var diffs = DeepCompare.Differences(a.Board, b.Board);
            if (ctx.Poison == "sessionstate") diffs.Add("(poisoned)");
            yield return new SmsCheck("every field of the board, the VDP, the cartridge, the Z80 and the sound unit is what the saved session had after the load (reflection over " + DeepCompare.LastCount + " fields)", diffs.Count == 0, diffs.Count == 0 ? "" : string.Join("; ", diffs.Take(12)));

            // the comparison can fail: two sessions that never saw each other's state differ, and it says where
            var c = ctx.Session(VdpSlotProbes.DoubleWrite(true), "fields.sms");
            for (int i = 0; i < 5; i++) c.RunFrame();
            var other = DeepCompare.Differences(a.Board, c.Board);
            yield return new SmsCheck("the field comparison reports differences between sessions at different points (its own proof of failure)", other.Count > 5, $"{other.Count} differences, e.g. {string.Join(", ", other.Take(3))}");
        }

        // ---- 3. refusals, with the reason, and the machine left as it was
        var sms = ctx.Session(SmokeProgs.Picture(false), "a.sms"); for (int i = 0; i < 20; i++) sms.RunFrame();
        byte[] smsState = sms.SaveState();
        var smsFm = ctx.Session(SmokeProgs.Picture(false), "a.sms", ConsoleKind.MasterSystem, null, "SMS-FM"); for (int i = 0; i < 5; i++) smsFm.RunFrame();
        var other2 = ctx.Session(SmokeProgs.Battery(false), "b.sms"); for (int i = 0; i < 5; i++) other2.RunFrame();
        var gg = ctx.Session(SmokeProgs.Picture(true), "a.gg", ConsoleKind.GameGear); for (int i = 0; i < 5; i++) gg.RunFrame();
        var pal = ctx.Session(SmokeProgs.Picture(false), "a.sms", ConsoleKind.MasterSystem, RegionChoice.Pal); for (int i = 0; i < 5; i++) pal.RunFrame();

        SmsCheck Refused(string name, IConsoleSession target, byte[] state, params string[] mustSay)
        {
            string before = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(target.SaveState()));
            string message;
            try { target.LoadState(state); message = ""; }
            catch (InvalidDataException e) { message = e.Message; }
            string after = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(target.SaveState()));
            bool says = message.Length > 0 && mustSay.All(w => message.Contains(w, StringComparison.OrdinalIgnoreCase));
            return new SmsCheck(name, says && before == after, message.Length == 0 ? "NOT REFUSED" : (before != after ? "refused, but the machine changed: " : "") + message);
        }

        yield return Refused("a stock-sound-unit state is refused by an FM-unit session, naming the units", smsFm, smsState, "sound unit", "SMS-FM", "SMS");
        byte[] fmState = smsFm.SaveState();
        yield return Refused("an FM-unit state is refused by a stock-unit session", sms, fmState, "sound unit");
        yield return Refused("one game's state is refused by another game", other2, smsState, "belongs to");
        yield return Refused("a Master System state is refused by a Game Gear", gg, smsState, "Master System", "Game Gear");
        yield return Refused("an NTSC state is refused by a PAL session", pal, smsState, "timing");
        byte[] flipped = (byte[])smsState.Clone(); flipped[flipped.Length - 5] ^= 0x40;
        var healthy = ctx.Session(SmokeProgs.Picture(false), "a.sms"); for (int i = 0; i < 3; i++) healthy.RunFrame();
        yield return Refused("a state with a flipped byte is refused as damaged", healthy, flipped, "damaged");
        yield return Refused("a cut-short state is refused", healthy, smsState.AsSpan(0, smsState.Length / 2).ToArray(), "cut short");
        yield return Refused("a file that is not a state is refused", healthy, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, "not a BrokenNes console state");
        // the same machine's own state still loads after all those refusals
        bool again; try { healthy.LoadState(smsState); again = true; } catch (Exception) { again = false; }
        yield return new SmsCheck("the session that refused all of them still loads a good state", again);

        // ---- 3b. the same contract on the Game Boy (its board already had a state; the session now carries it in the shared container), and the SNES saying plainly that it has none
        foreach (var c in GameBoyChecks(ctx)) yield return c;

        // ---- 4. a session without states says so
        yield return new SmsCheck("a session that has no state support says so (contract default: NotSupportedException with a reason, SupportsState false)", NoStateSession.Check(), "");
        yield return new SmsCheck("the Master System and Game Gear sessions report SupportsState", sms.SupportsState && gg.SupportsState);
        foreach (var c in SnesChecks()) yield return c;
    }

    // ------------------------------------------------------------------------------------------------ the Game Boy
    private static IEnumerable<SmsCheck> GameBoyChecks(SmsSuiteContext ctx)
    {
        string dir = Environment.GetEnvironmentVariable("BROKENNES_GB_ROMS") is { Length: > 0 } e ? e : @"X:\EMULATION\GameBoy";
        var games = new[] { "Super Mario Land (W) (V1.1) [!].zip", "Legend of Zelda, The - Link's Awakening (U) (V1.2) [!].zip" };
        bool any = false;
        foreach (var file in games)
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) continue;
            any = true;
            byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(path), file, out string inner);
            IConsoleSession Make(string cpu = "GB", string apu = "GB") => ConsoleSessions.Create(ConsoleKind.GameBoy, rom, cpu, "GB", apu, null, inner);
            var padScript = SmsRomIo.ParseInput("100:Start,110:,200:A,210:,300:Right,400:A+Right,420:Right,500:Down,600:Start,610:");
            (List<string> Hashes, List<short> Audio) Go(IConsoleSession s, int from, int frames)
            {
                var hashes = new List<string>(); var audio = new List<short>(); var buf = new short[16384];
                PadButtons held = 0;
                foreach (var kv in padScript.Where(k => k.Key <= from).OrderBy(k => k.Key)) held = kv.Value;
                for (int f = from; f < from + frames; f++)
                {
                    if (padScript.TryGetValue(f, out var h)) held = h;
                    s.SetPad(0, held); s.RunFrame();
                    hashes.Add(GoldenSuite.Hash(s));
                    int n; while ((n = s.ReadSamples(buf)) > 0) for (int i = 0; i < n; i++) audio.Add(buf[i]);
                }
                return (hashes, audio);
            }
            int bad = 0, badAudio = 0; long size = 0;
            foreach (int at in new[] { 300, 301, 450 })
            {
                var a = Make(); Go(a, 0, at);
                byte[] state = a.SaveState(); size = state.Length;
                var carried = Go(a, at, 60);
                var b = Make(); Go(b, 0, 2);
                b.LoadState(state);
                if (ctx.Poison == "sessionstate") b.Reset();                       // the poison: the loading session starts over instead of restoring
                var restored = Go(b, at, 60);
                if (!carried.Hashes.SequenceEqual(restored.Hashes)) bad++;
                if (!carried.Audio.SequenceEqual(restored.Audio)) badAudio++;
            }
            yield return new SmsCheck($"Game Boy {inner}: 60 frames after a state load are the picture of the session that carried on (3 save points)", bad == 0, bad == 0 ? $"state {size} bytes" : $"{bad} of 3 differ");
            yield return new SmsCheck($"Game Boy {inner}: the audio is the same sample for sample (3 save points)", badAudio == 0, badAudio == 0 ? "" : $"{badAudio} of 3 differ");
            // refusals
            var g = Make(); Go(g, 0, 20); byte[] gs = g.SaveState();
            string msg = ""; try { Make(apu: "GBS").LoadState(gs); } catch (InvalidDataException ex) { msg = ex.Message; }
            yield return new SmsCheck($"Game Boy {inner}: a state of the stock sound unit is refused by a session on the 8-channel unit, naming them", msg.Contains("sound unit") && msg.Contains("GBS"), msg);
            msg = ""; try { Make("65816").SaveState(); } catch (NotSupportedException ex) { msg = ex.Message; }
            yield return new SmsCheck($"Game Boy {inner}: a session with the 65816 in the SM83's place says it has no states instead of saving half a machine", msg.Contains("65816"), msg);
            msg = ""; try { ConsoleSessions.Create(ConsoleKind.GameBoy, rom, "GB", "GB", "GB", null, inner).LoadState(SmsSession_DifferentGame()); } catch (InvalidDataException ex) { msg = ex.Message; }
            yield return new SmsCheck($"Game Boy {inner}: a Master System state is refused", msg.Contains("Master System"), msg);
        }
        if (!any) yield return SmsCheck.Skipped("Game Boy state round trip", $"no Game Boy ROM under {dir} (set BROKENNES_GB_ROMS): not run");
    }

    /// <summary>
    /// The SNES session has save states (round 5): the old assertion here was that it says it has none. It is turned round so that it fails if <see cref="IConsoleSession.SupportsState"/> ever goes false again (a missing forward in a wrapper, a
    /// refactor): the session must report support, return a state that names the SNES, and a fresh session must load it and give the same state back. The full SNES proof (11 games, every coprocessor, every field) is <c>--snes-state-test</c>.
    /// </summary>
    private static IEnumerable<SmsCheck> SnesChecks()
    {
        string dir = Environment.GetEnvironmentVariable("BROKENNES_SNES_ROMS") is { Length: > 0 } e ? e : @"X:\EMULATION\Super Nintendo";
        string? zip = null;
        if (Directory.Exists(dir))
        {
            zip = Directory.EnumerateFiles(dir, "Super Mario World (U) [!].smc", SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".sfc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".smc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        }
        if (zip == null) { yield return SmsCheck.Skipped("SNES session supports save states", "no SNES ROM found (set BROKENNES_SNES_ROMS)"); yield break; }
        IConsoleSession? s = null, fresh = null; string why = "";
        try
        {
            byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(zip), Path.GetFileName(zip), out string inner);
            s = ConsoleSessions.Create(ConsoleKind.Snes, rom, "SFC", "SFC", "SFC", null, inner);
            fresh = ConsoleSessions.Create(ConsoleKind.Snes, rom, "SFC", "SFC", "SFC", null, inner);
        }
        catch (Exception ex) { why = ex.Message; }
        if (s == null || fresh == null) { yield return SmsCheck.Skipped("SNES session supports save states", "could not build a SNES session from " + Path.GetFileName(zip) + ": " + why); yield break; }
        for (int i = 0; i < 30; i++) s.RunFrame();
        fresh.RunFrame();
        string detail = ""; bool ok = s.SupportsState;
        if (!ok) detail = "SupportsState is false: " + s.StateUnsupportedReason;
        else
        {
            try
            {
                byte[] state = s.SaveState();
                var info = ConsoleStateFile.Peek(state);
                fresh.LoadState(state);
                ok = info.Console == ConsoleKind.Snes && fresh.SaveState().AsSpan().SequenceEqual(state);
                detail = ok ? $"state {state.Length / 1024} KB, header {string.Join(",", info.Units.Select(kv => kv.Key + "=" + kv.Value))}" : "the state did not load back to the same machine";
            }
            catch (Exception ex) { ok = false; detail = ex.GetType().Name + ": " + ex.Message; }
        }
        yield return new SmsCheck("the SNES session supports save states: SupportsState is true, SaveState returns a SNES state and a fresh session loads it and gives the same state back (fails if SupportsState goes false again)", ok, detail);
        s.Dispose(); fresh.Dispose();
    }

    private static byte[] SmsSession_DifferentGame()
    {
        var s = new SmsSession(SmokeProgs.Picture(false), ConsoleKind.MasterSystem, "Z80", "SMS", "SMS", "x.sms");
        s.RunFrame();
        return s.SaveState();
    }

    /// <summary>A session that implements none of the state members: the contract's defaults answer.</summary>
    private sealed class NoStateSession : IConsoleSession
    {
        public ConsoleKind Console => ConsoleKind.Snes; public string Title => "x"; public string Description => "x"; public double FramesPerSecond => 60;
        public void SetPad(int player, PadButtons buttons) { } public void RunFrame() { } public void Reset() { }
        public uint[] Frame => new uint[1]; public int FrameWidth => 1; public int FrameHeight => 1; public int DisplayWidth => 1; public int SampleRate => 44100;
        public int ReadSamples(short[] buffer) => 0; public bool HasBattery => false; public byte[] ExportSave() => Array.Empty<byte>(); public void ImportSave(byte[] data) { }
        public string GameId => "x"; public bool TrySwapCore(CoreSlot slot, string id) => false; public void Dispose() { }

        public static bool Check()
        {
            IConsoleSession s = new NoStateSession();
            bool saveThrows = false, loadThrows = false;
            try { s.SaveState(); } catch (NotSupportedException e) { saveThrows = e.Message.Length > 10; }
            try { s.LoadState(Array.Empty<byte>()); } catch (NotSupportedException e) { loadThrows = e.Message.Length > 10; }
            return !s.SupportsState && s.StateUnsupportedReason.Length > 10 && saveThrows && loadThrows;
        }
    }
}

/// <summary>Reflection over two object graphs of the same shape: the paths of the fields that differ. Used to find state a save leaves out.</summary>
internal static class DeepCompare
{
    public static int LastCount;

    public static List<string> Differences(object a, object b)
    {
        var diffs = new List<string>();
        LastCount = 0;
        Walk(a, b, a.GetType().Name, diffs, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        return diffs;
    }

    private static void Walk(object? a, object? b, string path, List<string> diffs, HashSet<object> seen, int depth)
    {
        if (diffs.Count > 400 || depth > 12) return;
        if (a == null || b == null) { if (!ReferenceEquals(a, b)) diffs.Add(path + " (null vs not)"); return; }
        var t = a.GetType();
        if (t != b.GetType()) { diffs.Add(path + " (type)"); return; }
        if (typeof(Delegate).IsAssignableFrom(t) || t.IsPointer || t == typeof(System.Reflection.Pointer)) return;
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal))
        {
            LastCount++;
            if (!a.Equals(b)) diffs.Add($"{path} ({a} vs {b})");
            return;
        }
        if (t.IsArray)
        {
            var aa = (Array)a; var ba = (Array)b;
            if (aa.Length != ba.Length) { diffs.Add(path + " (length)"); return; }
            var elem = t.GetElementType()!;
            if (elem.IsPrimitive)
            {
                LastCount++;
                for (int i = 0; i < aa.Length; i++) if (!aa.GetValue(i)!.Equals(ba.GetValue(i))) { diffs.Add($"{path}[{i}] ({aa.GetValue(i)} vs {ba.GetValue(i)})"); break; }
                return;
            }
            for (int i = 0; i < aa.Length; i++) Walk(aa.GetValue(i), ba.GetValue(i), $"{path}[{i}]", diffs, seen, depth + 1);
            return;
        }
        if (!t.IsValueType && !seen.Add(a)) return;
        if (t.Namespace == null || !t.Namespace.StartsWith("NesEmulator") && !t.Namespace.StartsWith("BrokenNes")) return;   // framework types (lists, streams) are not machine state here
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
            Walk(f.GetValue(a), f.GetValue(b), path + "." + f.Name.Trim('<', '>').Replace("k__BackingField", ""), diffs, seen, depth + 1);
        }
    }
}
