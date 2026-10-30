using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BrokenNes.Workshop.Debug;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>
/// <c>--timeflow-test [--only form,codec,consoles,damage,stop,cap,off,branch,places,index,apu,profile,psync,pstore,pconsoles,pmeasure] [--long N] [--pframes N] [--keep DIR]</c>: milestones T1, T2 and T3 (TimeflowTestCli.Profile.cs)
/// (docs/projects/brokendev-timeflow-plan.md). Every check has a negative control. The user's games run when they are on this machine (a
/// missing one is NOT RUN, never a pass). BROKENDEV_HOME points at a scratch folder for the whole run (nothing is written to the real one).
/// </summary>
internal static partial class TimeflowTestCli
{
    private static int pass, fail, notRun;
    private static readonly List<string> failures = new();

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
        if (ok) pass++; else { fail++; failures.Add(name + ": " + detail); }
    }

    private static void NotRun(string name, string why) { Console.WriteLine($"  NOT RUN  {name}: {why}"); notRun++; }

    private static bool Throws(Action a, out string message)
    {
        try { a(); message = "no error"; return false; }
        catch (Exception e) { message = e.Message; return true; }
    }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        var only = Opt("only", "form,codec,consoles,damage,stop,cap,off,branch,places,index,index-sega,apu,profile,psync,pstore,pconsoles").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int longFrames = int.Parse(Opt("long", "1800"));
        string dir = Opt("keep", Path.Combine(Path.GetTempPath(), "brokennes-timeflow-test-" + Environment.ProcessId));
        Directory.CreateDirectory(dir);
        string home = Path.Combine(dir, "home");
        string? oldHome = Environment.GetEnvironmentVariable("BROKENDEV_HOME");
        Environment.SetEnvironmentVariable("BROKENDEV_HOME", home);
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"Timeflow tests in {dir} (BROKENDEV_HOME = {home})");
        var (planted, _) = MovieTestRoms.PlantedGame(MovieTestRoms.ParseSettings("version 1\nstart Start\nfloor 1\nbonus 1\n"));
        string nes = DebugTestRoms.Write(dir, "planted.nes", planted);
        string snes = DebugTestRoms.Write(dir, "debugtest.sfc", DebugTestRoms.Snes().rom);
        string gb = DebugTestRoms.Write(dir, "debugtest.gb", DebugTestRoms.GameBoy().rom);
        void Group(string key, string title, Action body)
        {
            if (!only.Contains(key)) return;
            Console.WriteLine($"[{key}] {title}");
            try { body(); }
            catch (Exception e) { Check($"{key}: the group ran", false, $"{e.GetType().Name}: {e.Message}"); }
        }
        try
        {
            Group("form", "the ROM-less NES form: split and joined back byte for byte", () => Form(nes));
            Group("codec", "page deltas: every frame decodes back to the recorded state", () => Codec());
            Group("consoles", $"restore equals record over {longFrames} frames on every console, loaded into a machine, replayed in sync", () => Consoles(dir, nes, snes, gb, longFrames));
            Group("damage", "a damaged block or delta is found, never restored", () => Damage(nes));
            Group("stop", "a restore-equals-record mismatch while recording stops it with the reason", () => Stop(nes));
            Group("cap", "the cap retires the oldest, never a kept one", () => Cap(nes, dir));
            Group("off", "recording off: nothing written, and recording leaves the game frame-identical", () => Off(nes, snes, gb, home));
            Group("branch", "rewind and resume: jump, replay in sync, a branch with new input, the tree", () => Branch(nes, snes));
            Group("places", "where timelines go (never %APPDATA%\\BrokenNes)", () => Places(dir));
            Group("index", "the per-frame index: watched variables, the frame's coverage, changed bytes per domain, findings", () => Index(dir, planted));
            Group("index-sega", "the per-frame index on the Sega consoles (T0): watched variables and the frame's coverage on the Master System, Game Gear and Genesis", () => IndexSega(dir));
            Group("apu", "APU_FIX's state carries its frame sequencer's step (found by Timeflow: a load kept the step of the machine loaded into)", () => ApuStep(nes));
            Group("profile", "T3: a profiled span's queries find the planted corruption exactly (the fixed build: nothing)", () => ProfileBug(dir));
            Group("psync", "T3: a profile's re-run stays in sync with the recording; a forced desync is found at its frame", () => ProfileSync(dir));
            Group("pstore", "T3: kept profiles: the stream's answers, the size limit, the TTL, the cap, the session's end, a crashed session, nothing left", () => ProfileStoreGroup(dir, home));
            Group("pconsoles", "T3: the SNES (with its SPC700) and the Game Boy profiled; the Sega consoles refused until T0", () => ProfileConsoles(dir, snes, gb));
            Group("pmeasure", "T3: the cost (x slower than real time), the bytes a stored frame and the stored queries' speed, per console", () => ProfileMeasure(dir, nes, snes, gb, int.Parse(Opt("pframes", "120"))));
        }
        finally
        {
            TimelineWriter.TamperForTest = null;
            Environment.SetEnvironmentVariable("BROKENDEV_HOME", oldHome);
        }
        Console.WriteLine($"timeflow tests: {pass} passed, {fail} failed, {notRun} not run ({sw.Elapsed.TotalSeconds:0} s)");
        foreach (var f in failures) Console.WriteLine("  FAILED " + f);
        if (!args.Contains("--keep")) try { Directory.Delete(dir, true); } catch (IOException) { }
        return fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static MovieMachine Open(string rom, string? console = null) => MovieMachine.Open(rom, console, "", "fceux", false, false);

    /// <summary>Records <paramref name="frames"/> frames of a ROM with a seeded player into the scratch store (every frame checked when <paramref name="full"/>).</summary>
    private static (string Dir, TimeflowRecorder Rec) Record(string rom, long frames, string? console = null, string input = "auto:3", bool full = true, Action<TimeflowOptions>? tweak = null, Action<MovieMachine, long>? afterFrame = null)
    {
        using var m = Open(rom, console);
        var o = new TimeflowOptions { Rom = m.RomFile, RomName = m.RomName, Cores = m.Cores, PowerOn = m.PowerOn, Source = "test", PowerOnStart = true, FullCheckEvery = full ? 1 : 8 };
        tweak?.Invoke(o);
        var rec = TimeflowRecorder.Start(TimeflowMachines.SourceFor(m), o);
        var inp = TimeflowInput.Parse(input, "", null);
        for (long f = 0; f < frames && rec.Running; f++)
        {
            var (a, b) = inp.At(f);
            rec.BeforeFrame(a, b); m.Frame(a, b); rec.AfterFrame();
            afterFrame?.Invoke(m, f);
        }
        return (rec.Stop(), rec);
    }

    // ------------------------------------------------------------------------------------------------ form

    private static void Form(string nes)
    {
        using var m = Open(nes);
        for (int f = 0; f < 120; f++) m.Frame(f % 60 == 10 ? PadButtons.Start : PadButtons.None, PadButtons.None);
        string json = m.Host.Nes!.SaveState();
        var st = NesStateForm.Split(json);
        string back = NesStateForm.Join(st);
        Check("form: join(split(state)) is the state (romPath blank)", back == NesStateForm.BlankRomPath(json), $"{json.Length} chars, {st.Sections.Length} sections ({string.Join(", ", st.Names.Zip(st.Sections, (n, s) => $"{n} {s.Length}"))}), skeleton {st.Skeleton.Length} bytes");
        Check("form: the big arrays left the text", st.Skeleton.Length < json.Length / 3 && st.Sections.Any(s => s.Length >= 2048), $"skeleton {st.Skeleton.Length} of {json.Length}");
        Check("form: the ROM is not in the state", st.Sections.All(s => s.Length < m.RomFile.Length || !s.AsSpan().SequenceEqual(m.RomFile.AsSpan(16, Math.Min(s.Length, m.RomFile.Length - 16)))), "no section is the PRG ROM");
        // negative control: a base64-looking run that does not encode back to itself stays in the text (and the text still comes back)
        // ("...AB=": the last character carries bits the 2 bytes do not use, so it decodes but does not encode back to itself)
        string odd = "{\"x\":\"" + new string('A', 85) + "AB=\",\"ram\":\"" + Convert.ToBase64String(new byte[96]) + "\"}";
        var so = NesStateForm.Split(odd);
        Check("form (negative): a run that is not canonical base64 stays text", so.Sections.Length == 1 && NesStateForm.Join(so) == odd, $"{so.Sections.Length} section(s) taken out of 2 runs");
        Check("form (negative): a 0x01 in the text is refused", Throws(() => NesStateForm.Split("{\"a\":\"\u0001\"}"), out string msg), msg);
        // the same state from another folder hashes the same (romPath is blanked), a later state differs
        var st2 = NesStateForm.Split(json.Replace("\"romPath\":\"", "\"romPath\":\"C:\\\\elsewhere\\\\"));
        m.Frame(PadButtons.None, PadButtons.None);
        var st3 = NesStateForm.Split(m.Host.Nes.SaveState());
        Check("form: the ROM's folder does not change the hash; a frame later does", st2.Hash() == st.Hash() && st3.Hash() != st.Hash(), $"{st.Hash():x16} / {st2.Hash():x16} / {st3.Hash():x16}");
    }

    // ------------------------------------------------------------------------------------------------ codec

    private static void Codec()
    {
        var rnd = new Random(7);
        var states = new List<TimeflowState>();
        var s = new TimeflowState { Skeleton = Encoding.UTF8.GetBytes("regs 0"), Sections = new[] { new byte[5000], new byte[300] }, Names = new[] { "a", "b" } };
        for (int f = 0; f < 50; f++)
        {
            var n = s.Clone();
            n.Skeleton = Encoding.UTF8.GetBytes($"regs {f} {new string('x', f % 7)}");
            for (int k = 0; k < rnd.Next(0, 6); k++) n.Sections[rnd.Next(2)][rnd.Next(300)] ^= (byte)rnd.Next(1, 256);
            if (f == 30) n.Sections = new[] { n.Sections[0], new byte[400] };   // the layout changes: a keyframe
            if (f == 31) n.Sections[0][4999] = 0x55;                             // the last, shorter page
            states.Add(n); s = n;
        }
        foreach (int page in new[] { 64, 256, 4096 })
        {
            var ms = new MemoryStream();
            TimeflowState? prev = null; int keys = 0;
            foreach (var st in states)
            {
                if (prev == null || !st.SameLayout(prev)) { TimelineCodec.WriteKey(ms, st); keys++; }
                else TimelineCodec.WriteDelta(ms, st, prev, page, new byte[page]);
                prev = st;
            }
            var raw = TimelineCodec.Decompress(TimelineCodec.Compress(ms.ToArray(), TimelineCompression.Brotli, 1), (int)ms.Length, TimelineCompression.Brotli);
            var d = new TimeflowState(); int at = 0, same = 0;
            foreach (var st in states) { TimelineCodec.Read(raw, ref at, d, page); if (d.Hash() == st.Hash()) same++; }
            Check($"codec: page {page}: every frame decodes to its state", same == states.Count && keys == 2, $"{same} of {states.Count}, {keys} keyframes (the first and the layout change), {ms.Length} bytes raw");
        }
        // negative control: a delta applied on the wrong frame does not give the state
        var ms2 = new MemoryStream();
        TimelineCodec.WriteDelta(ms2, states[5], states[4], 256, new byte[256]);
        var wrong = states[2].Clone(); int a2 = 0;
        TimelineCodec.Read(ms2.ToArray(), ref a2, wrong, 256);
        Check("codec (negative): a delta on another frame gives another state", wrong.Hash() != states[5].Hash(), "differs");
        foreach (var (how, lvl) in new[] { (TimelineCompression.None, 0), (TimelineCompression.Deflate, 0), (TimelineCompression.Brotli, 5) })
        {
            var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("timeflow ", 500)));
            Check($"codec: {how} {lvl} round trip", TimelineCodec.Decompress(TimelineCodec.Compress(data, how, lvl), data.Length, how).AsSpan().SequenceEqual(data), "same bytes");
        }
    }

    // ------------------------------------------------------------------------------------------------ every console

    private static void Consoles(string dir, string nes, string snes, string gb, int frames)
    {
        string home = Environment.GetEnvironmentVariable("BROKENDEV_HOME") is { Length: > 0 } h0 ? h0 : "";
        string real = @"P:\BrokenDev\env";
        var roms = new List<(string Name, string? Path, string? Console)>
        {
            ("NES test game", nes, null), ("SNES test ROM", snes, null), ("Game Boy test ROM", gb, null),
            ("VRUN (NES)", Path.Combine(real, "out", "vrun-nes", "vrun_game.nes"), "nes"),
            ("Super VRUN (SNES)", Path.Combine(real, "out", "supervrun", "supervrun.sfc"), "snes"),
            ("Super Mario World (SNES)", @"X:\EMULATION\Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc", "snes"),
            ("WideMess sample", FindUp("WideMess", "testroms", "bin", "devkit_sample.wms"), "wms"),
            ("Vaults of Obsolete Code (GBC)", Path.Combine(real, "out", "vrun-voc", "vrun_voc.gbc"), "gbc"),
            ("Sonic 2 (Master System)", @"X:\EMULATION\Sega Master System\Sonic The Hedgehog 2 (Europe) (v1.1).sms", "sms"),
            ("Sonic (Game Gear)", @"X:\EMULATION\Sega Game Gear\Sonic The Hedgehog (U) (V1.1) [!].gg", "gg"),
            ("Sonic (Genesis)", @"X:\EMULATION\Sega Genesis\Sonic the Hedgehog (JUE) [!].bin", "md"),
        };
        foreach (var (name, path, console) in roms)
        {
            if (path == null || !File.Exists(path)) { NotRun($"consoles: {name}", $"not on this machine ({path})"); continue; }
            var (tdir, rec) = Record(path, frames, console, "auto:5", full: true);
            using var rd = new TimelineReader(tdir);
            Check($"consoles: {name}: recorded, every frame checked while recording", rec.StopReason == null && rd.Frames == frames && rd.Meta.VerifyFailures == 0 && rd.Meta.VerifiedFrames == frames,
                  $"{rd.Frames} frames, {rd.Meta.VerifiedFrames} checked, {rd.Meta.VerifyFailures} failed{(rec.StopReason != null ? ", stopped: " + rec.StopReason : "")}, {TimeflowStore.DirBytes(tdir) / Math.Max(1, rd.Frames)} bytes a frame");
            // every frame restored from the files (keyframe + deltas) hashes as recorded
            int okAll = 0; string firstBad = "";
            for (long f = rd.StartFrame; f < rd.EndFrame; f++)
            {
                try { rd.State(f); okAll++; }
                catch (InvalidDataException e) { if (firstBad.Length == 0) firstBad = e.Message; }
            }
            Check($"consoles: {name}: every frame restored from the files equals the record", okAll == frames, $"{okAll} of {frames}{(firstBad.Length > 0 ? ": " + firstBad : "")}");
            // loaded into a fresh machine: its own state hashes as recorded; replayed from there to the end in sync
            using var pb = new TimeflowPlayback(tdir);
            var picks = new[] { 0L, frames / 3, frames / 2 + 7, frames - 61 }.Where(f => f >= 0 && f < frames).Distinct().ToArray();
            int loaded = 0; string loadBad = "";
            foreach (long f in picks)
            {
                using var mm = TimeflowMachines.OpenFor(tdir, pb.Meta, false);
                var src = TimeflowMachines.SourceFor(mm);
                try { pb.Jump(src, f); loaded++; } catch (InvalidDataException e) { if (loadBad.Length == 0) loadBad = e.Message; }
            }
            Check($"consoles: {name}: loaded into a fresh machine, its state is the recorded one", loaded == picks.Length, $"{loaded} of {picks.Length} frames ({string.Join(", ", picks)}){(loadBad.Length > 0 ? ": " + loadBad : "")}");
            long from = frames / 2 + 7;
            using (var mm = TimeflowMachines.OpenFor(tdir, pb.Meta, false))
            {
                var src = TimeflowMachines.SourceFor(mm);
                pb.Jump(src, from);
                var r = pb.Replay(src, from, rd.EndFrame, (a, b) => mm.Frame(a, b));
                Check($"consoles: {name}: replayed from frame {from} to the end, in sync", r.InSync && r.FramesRun == rd.EndFrame - from, $"{r.FramesRun} frames, {r.Checked} checks{(r.OffFrame != null ? $", off at {r.OffFrame} ({r.OffWhat})" : "")}");
            }
            // negative control: the same replay with one input changed (Start held from the jump) leaves the recording
            using (var mm = TimeflowMachines.OpenFor(tdir, pb.Meta, false))
            {
                var src = TimeflowMachines.SourceFor(mm);
                pb.Jump(src, from);
                var r = pb.Replay(src, from, rd.EndFrame, (a, b) => mm.Frame(a | PadButtons.Start | PadButtons.Right | PadButtons.A, b));
                if (r.InSync)
                {
                    // a test ROM that ignores input: the control is a work-RAM byte changed after the jump instead
                    pb.Jump(src, from);
                    var d = mm.Host.Domains().First(x => x.Kind == NesEmulator.Corruption.DomainKind.Work && x.Size > 0x40);
                    d.Poke(0x3F, (byte)(d.Peek(0x3F) ^ 0x5A));
                    var r2 = pb.Replay(src, from, rd.EndFrame, (a, b) => mm.Frame(a, b));
                    Check($"consoles: {name} (negative): a changed machine goes off the recording", !r2.InSync, r2.OffFrame != null ? $"the game ignores input here; one RAM byte changed after the jump: off at frame {r2.OffFrame} ({r2.OffWhat})" : "stayed in sync");
                }
                else Check($"consoles: {name} (negative): other input goes off the recording", true, $"off at frame {r.OffFrame} ({r.OffWhat})");
            }
        }
    }

    private static string? FindUp(params string[] parts)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (; d != null; d = d.Parent) { string p = Path.Combine(new[] { d.FullName }.Concat(parts).ToArray()); if (File.Exists(p)) return p; }
        d = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (; d != null; d = d.Parent) { string p = Path.Combine(new[] { d.FullName }.Concat(parts).ToArray()); if (File.Exists(p)) return p; }
        return null;
    }

    // ------------------------------------------------------------------------------------------------ damage

    private static void Damage(string nes)
    {
        var (tdir, _) = Record(nes, 300);
        using (var rd = new TimelineReader(tdir))
            Check("damage: the clean timeline restores every frame", Enumerable.Range(0, 300).All(f => { try { rd.State(f); return true; } catch (InvalidDataException) { return false; } }), "300 of 300");
        string frames = Path.Combine(tdir, "frames.tfb"), blocks = Path.Combine(tdir, "blocks.tfi");
        byte[] fb = File.ReadAllBytes(frames), bt = File.ReadAllBytes(blocks);
        var b2 = TimelineBlock.Read(bt.AsSpan(2 * TimelineBlock.Size));
        // 1. a flipped byte on disk: the block's checksum
        var bad = (byte[])fb.Clone(); bad[b2.Offset + b2.Packed / 2] ^= 0x40;
        File.WriteAllBytes(frames, bad);
        using (var rd = new TimelineReader(tdir))
        {
            Check("damage: a flipped byte in a block is found by its checksum", Throws(() => rd.State(b2.FirstFrame + 5), out string m1) && m1.Contains("damaged"), m1);
            Check("damage (negative): the other blocks still restore", Throws(() => rd.State(10), out string m0) == false, "frame 10 restores");
        }
        // 2. a delta changed and the block rebuilt with a matching checksum and raw hash: the state's hash finds it from that frame on
        using var meta0 = new TimelineReader(tdir);
        var m = meta0.Meta;
        meta0.Dispose();
        byte[] raw = TimelineCodec.Decompress(fb.AsSpan((int)b2.Offset, b2.Packed), b2.Raw, m.Compression);
        // find frame 10 of the block's record and flip a byte inside its last page data
        int at = 0; var st = new TimeflowState();
        int[] starts = new int[b2.Count + 1];
        for (int i = 0; i < b2.Count; i++) { starts[i] = at; TimelineCodec.Read(raw, ref at, st, m.PageSize); }
        starts[b2.Count] = at;
        int target = 10;
        // a delta with at least one page: flip its last byte (inside the XOR data of its last page)
        while (target < b2.Count - 1 && starts[target + 1] - starts[target] < 20) target++;
        raw[starts[target + 1] - 1] ^= 0x01;
        byte[] repacked = TimelineCodec.Compress(raw, m.Compression, m.Level);
        var nb = b2; nb.Offset = fb.Length; nb.Packed = repacked.Length; nb.Crc = TimelineCodec.Crc32(repacked); nb.RawHash = TimeflowHash.Of(raw);
        File.WriteAllBytes(frames, fb.Concat(repacked).ToArray());
        var bt2 = (byte[])bt.Clone(); nb.Write(bt2.AsSpan(2 * TimelineBlock.Size)); File.WriteAllBytes(blocks, bt2);
        using (var rd = new TimelineReader(tdir))
        {
            long hit = b2.FirstFrame + target;
            Check("damage: a changed delta (checksums made to match) is found by the state's hash", Throws(() => rd.State(hit), out string m2) && m2.Contains("delta is damaged"), m2);
            Check("damage (negative): the frames before it in the block restore", !Throws(() => rd.State(hit - 1), out _), $"frame {hit - 1} restores");
            Check("damage: the frames after it are refused too", Throws(() => rd.State(Math.Min(hit + 3, b2.FirstFrame + b2.Count - 1)), out string m3), m3);
        }
    }

    // ------------------------------------------------------------------------------------------------ stop

    private static void Stop(string nes)
    {
        int blocks = 0;
        TimelineWriter.TamperForTest = (packed, raw, meta) =>
        {
            if (++blocks != 3) return packed;
            // a damaged encoder: one delta byte of the third block is off (the bytes no longer decode to the recorded states)
            var r = (byte[])raw.Clone(); r[r.Length - 1] ^= 0x10;
            return TimelineCodec.Compress(r, meta.Compression, meta.Level);
        };
        string? told = null;
        TimeflowRecorder rec;
        using (var m = Open(nes))
        {
            var o = new TimeflowOptions { Rom = m.RomFile, RomName = m.RomName, Cores = m.Cores, Source = "test", FullCheckEvery = 1 };
            rec = TimeflowRecorder.Start(TimeflowMachines.SourceFor(m), o);
            rec.Stopped += r => told = r;
            for (int f = 0; f < 600 && rec.Running; f++) { rec.BeforeFrame(PadButtons.None, PadButtons.None); m.Frame(PadButtons.None, PadButtons.None); rec.AfterFrame(); System.Threading.Thread.Sleep(f % 30 == 0 ? 2 : 0); }
            // the worker may still be on block 3: wait for it to stop the recording
            for (int i = 0; i < 200 && rec.Running; i++) System.Threading.Thread.Sleep(10);
        }
        TimelineWriter.TamperForTest = null;
        string dir = rec.Stop();
        var meta2 = TimelineMeta.Load(dir);
        Check("stop: the mismatch stopped the recording", !rec.Running && rec.StopReason != null && rec.StopReason.Contains("restore does not equal record"), rec.StopReason ?? "kept recording");
        Check("stop: the host was told, and the timeline says why", told != null && meta2.Status == "stopped" && meta2.StopReason == rec.StopReason, $"status {meta2.Status}: {meta2.StopReason}");
        Check("stop: the blocks before it are kept and restore", meta2.Frames == 120 && !Throws(() => { using var rd = new TimelineReader(dir); rd.State(119); }, out _), $"{meta2.Frames} frames kept");
        // negative control: the same recording without the fault runs to the end
        var (d2, r2) = Record(nes, 600, input: "");
        Check("stop (negative): without the fault it records all 600 frames", r2.StopReason == null && TimelineMeta.Load(d2).Frames == 600, $"{TimelineMeta.Load(d2).Frames} frames");
        // a machine that refuses a state: the recording does not start, and says why

        Check("stop: a machine without states is refused with the reason", Throws(() => TimeflowRecorder.Start(TimeflowSourceNoState(), new TimeflowOptions { Rom = new byte[16], RomName = "x.nes" }), out string why) && why.Contains("cannot record"), why);
    }

    private static TimeflowSource TimeflowSourceNoState()
    {
        // a Game Boy session on the 65816 CPU has no state (the session says so): the recorder must refuse it
        string tmp = Path.Combine(Path.GetTempPath(), "brokennes-timeflow-nostate.gb");
        File.WriteAllBytes(tmp, DebugTestRoms.GameBoy().rom);
        var s = ConsoleSessions.Create(ConsoleKind.GameBoy, File.ReadAllBytes(tmp), CoreCatalog.Resolve(ConsoleKind.GameBoy, CoreSlot.Cpu, "65816"), CoreCatalog.Resolve(ConsoleKind.GameBoy, CoreSlot.Ppu, ""), CoreCatalog.Resolve(ConsoleKind.GameBoy, CoreSlot.Apu, ""), null, "nostate.gb");
        return TimeflowSource.For(s);
    }

    // ------------------------------------------------------------------------------------------------ cap

    private static void Cap(string nes, string dir)
    {
        string g = Path.Combine(dir, "capgame");
        Directory.CreateDirectory(g);
        var ids = new List<string>();
        void One(Action<TimeflowOptions>? t = null) { var (d, r) = Record(nes, 240, input: "auto:9", tweak: o => { o.GameDir = g; t?.Invoke(o); }); ids.Add(r.Id); System.Threading.Thread.Sleep(20); }
        One(); One(); One();
        long each = TimeflowStore.List(g).Max(t => t.Bytes);
        long romBytes = TimeflowStore.DirBytes(Path.Combine(g, "roms"));
        TimeflowStore.SetKeep(TimeflowStore.List(g).First(t => t.Meta.Id == ids[0]).Dir, true);   // the oldest is kept
        // a cap for about three timelines: the fourth retires the oldest that is not kept (the second)
        long cap = romBytes + each * 3 + each / 2;
        TimeflowStore.SetCap(g, cap);
        One();
        var left = TimeflowStore.List(g).Select(t => t.Meta.Id).ToList();
        Check("cap: the oldest unkept timeline retired", !left.Contains(ids[1]) && left.Contains(ids[2]) && left.Contains(ids[3]), $"left {string.Join(", ", left)}; retired {ids[1]}");
        Check("cap: the kept one (the oldest of all) stays", left.Contains(ids[0]), ids[0]);
        Check("cap: the folder is within its cap", TimeflowStore.DirBytes(g) <= cap, $"{TimeflowStore.DirBytes(g)} of {cap}");
        // negative control: with a big cap nothing retires
        TimeflowStore.SetCap(g, 1L << 40);
        One();
        Check("cap (negative): under the cap nothing retires", TimeflowStore.List(g).Count == left.Count + 1, $"{TimeflowStore.List(g).Count} timelines");
        // every one kept and the cap below the folder: a new recording is refused with the reason
        foreach (var t in TimeflowStore.List(g)) TimeflowStore.SetKeep(t.Dir, true);
        TimeflowStore.SetCap(g, romBytes + each);
        Check("cap: all kept and over the cap: a recording is refused, with the reason", Throws(() => One(), out string why) && why.Contains("keep"), why);
        Check("cap: and no kept timeline was retired", TimeflowStore.List(g).Count == left.Count + 1, $"{TimeflowStore.List(g).Count} timelines");
        // a recording that alone outgrows the cap stops, with the reason
        foreach (var t in TimeflowStore.List(g)) Directory.Delete(t.Dir, true);
        TimeflowStore.SetCap(g, romBytes + each / 2);
        var (d5, r5) = Record(nes, 600, input: "auto:9", tweak: o => o.GameDir = g);
        Check("cap: a timeline that alone reaches the cap stops, saying so", r5.StopReason != null && r5.StopReason.Contains("cap"), r5.StopReason ?? "not stopped");
        Check("cap: what it recorded before is kept", TimelineMeta.Load(d5).Frames > 0 && TimelineMeta.Load(d5).Status == "stopped", $"{TimelineMeta.Load(d5).Frames} frames, {TimelineMeta.Load(d5).Status}");
    }

    // ------------------------------------------------------------------------------------------------ off

    private static void Off(string nes, string snes, string gb, string home)
    {
        string root = Path.Combine(home, "timeflow");
        foreach (var (name, rom) in new[] { ("NES", nes), ("SNES", snes), ("Game Boy", gb) })
        {
            var inp = TimeflowInput.Parse("auto:4", "", null);
            var plain = new List<ulong>();
            long before = TimeflowStore.DirBytes(root);
            int filesBefore = Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length : 0;
            using (var m = Open(rom))
                for (int f = 0; f < 400; f++) { var (a, b) = inp.At(f); m.Frame(a, b); plain.Add(m.Picture() ^ m.Ram() * 31); }
            int filesAfter = Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length : 0;
            Check($"off: {name}: running without a recorder writes nothing", filesAfter == filesBefore && TimeflowStore.DirBytes(root) == before, $"{filesAfter - filesBefore} new files");
            var rec = new List<ulong>();
            Record(rom, 400, input: "auto:4", afterFrame: (m, f) => rec.Add(m.Picture() ^ m.Ram() * 31));
            int same = plain.Zip(rec).Count(p => p.First == p.Second);
            Check($"off: {name}: recording leaves the game frame-identical", same == 400, $"{same} of 400 frames (picture and RAM) the same");
            // negative control: the comparison sees a change: one work-RAM byte changed at frame 200 differs from there
            var moved = new List<ulong>();
            using (var m = Open(rom))
                for (int f = 0; f < 400; f++)
                {
                    var (a, b) = inp.At(f); m.Frame(a, b);
                    if (f == 200) { var d = m.Host.Domains().First(x => x.Kind == NesEmulator.Corruption.DomainKind.Work && x.Size > 0x40); d.Poke(0x3F, (byte)(d.Peek(0x3F) ^ 0x5A)); }
                    moved.Add(m.Picture() ^ m.Ram() * 31);
                }
            int firstDiff = plain.Zip(moved).Select((p, i) => p.First != p.Second ? i : -1).FirstOrDefault(i => i >= 0, -1);
            Check($"off: {name} (negative): a RAM byte changed at frame 200 is seen", firstDiff == 200, $"first difference at frame {firstDiff}");
        }
    }

    // ------------------------------------------------------------------------------------------------ branch (T2)

    private static void Branch(string nes, string snes)
    {
        foreach (var (name, rom) in new[] { ("NES", nes), ("SNES", snes) })
        {
            var (tdir, _) = Record(rom, 600, input: "auto:6");
            using var pb = new TimeflowPlayback(tdir);
            long f = 333;
            // jump: a fresh machine at frame 333 holds the recorded state
            using var m = TimeflowMachines.OpenFor(tdir, pb.Meta, false);
            var src = TimeflowMachines.SourceFor(m);
            pb.Jump(src, f);
            Check($"branch: {name}: jump to {f}", src.Capture().Hash() == pb.Reader.Row(f).State, "the machine's state is the recorded one");
            // a live branch: new input from 333, recorded with the parent and frame
            var o = pb.BranchOptions(f, TimeflowStore.RomOf(tdir, pb.Meta)); o.Source = "test"; o.FullCheckEvery = 1;
            var rec = TimeflowRecorder.Start(src, o);
            for (int k = 0; k < 120; k++) { rec.BeforeFrame(PadButtons.Left | PadButtons.B, PadButtons.None); m.Frame(PadButtons.Left | PadButtons.B, PadButtons.None); rec.AfterFrame(); }
            string bdir = rec.Stop();
            using var br = new TimelineReader(bdir);
            Check($"branch: {name}: the branch names its parent and frame, its frames start there", br.Meta.ParentId == pb.Meta.Id && br.Meta.ParentFrame == f && br.StartFrame == f && br.Frames == 120, $"parent {br.Meta.ParentId} at {br.Meta.ParentFrame}, frames {br.StartFrame}-{br.EndFrame - 1}");
            Check($"branch: {name}: the branch's first frame is the parent's frame", br.Row(f).State == pb.Reader.Row(f).State, $"{br.Row(f).State:x16}");
            Check($"branch: {name} (negative): its later frames are its own", br.Row(f + 60).State != pb.Reader.Row(f + 60).State, "the new input made another future");
            // the parent is unchanged by the branch: a replay from 333 is still in sync
            using var m2 = TimeflowMachines.OpenFor(tdir, pb.Meta, false);
            var s2 = TimeflowMachines.SourceFor(m2);
            pb.Jump(s2, f);
            var r = pb.Replay(s2, f, pb.Reader.EndFrame, (a, b) => m2.Frame(a, b));
            Check($"branch: {name}: the parent replays in sync after the branch", r.InSync && r.FramesRun == pb.Reader.EndFrame - f, $"{r.FramesRun} frames{(r.OffFrame != null ? $", off at {r.OffFrame} ({r.OffWhat})" : "")}{(r.PictureHeld ? " (the first picture after the jump was the held one, not compared)" : "")}");
            // the tree lists the branch under its parent
            var tree = TimeflowPlayback.Tree(TimeflowStore.List(TimeflowStore.GameDirOf(tdir))).ToList();
            int pi = tree.FindIndex(t => t.T.Meta.Id == pb.Meta.Id), bi = tree.FindIndex(t => t.T.Meta.Id == br.Meta.Id);
            Check($"branch: {name}: the listing shows the branch under its parent", pi >= 0 && bi > pi && tree[bi].Depth == tree[pi].Depth + 1, $"parent at {pi}, branch at {bi} depth {(bi >= 0 ? tree[bi].Depth : -1)}");
            // a jump into another ROM's timeline is refused (T5 is hotpatch)
            using var other = Open(rom == nes ? snes : nes);
            Check($"branch: {name} (negative): a jump on another ROM is refused", Throws(() => pb.CheckMachine(TimeflowMachines.SourceFor(other), other.RomSha256), out string why), why);
            Check($"branch: {name} (negative): a frame outside the timeline is refused", Throws(() => pb.Jump(src, pb.Reader.EndFrame + 5), out string w2), w2);
        }
    }

    // ------------------------------------------------------------------------------------------------ index

    private static void Index(string dir, byte[] planted)
    {
        var (_, mlb) = MovieTestRoms.PlantedGame(MovieTestRoms.ParseSettings("version 1\nstart Start\nfloor 1\nbonus 1\n"));
        string rom = DebugTestRoms.Write(dir, "indexed.nes", planted), sym = DebugTestRoms.Write(dir, "indexed.mlb", mlb);
        string Rec(bool extras)
        {
            using var m = MovieMachine.Open(rom, null, "", "fceux", false, true, new[] { sym });
            var o = new TimeflowOptions { Rom = m.RomFile, RomName = m.RomName, Cores = m.Cores, Source = "test", PowerOnStart = true };
            FrameCoverage? cov = null;
            if (extras)
            {
                var d = m.Host.Dbg;
                o.VarNames = new() { "app_state=[app_state]", "lives=[lives]" };
                o.Vars = () => new[] { d.Evaluate("[app_state]"), d.Evaluate("[lives]") };
                cov = FrameCoverage.Attach(d); o.Coverage = cov;
                o.Checks = NesEmulator.Debugging.Checks.CheckRun.Attach(d, new NesEmulator.Debugging.Checks.CheckOptions { Profile = false });
            }
            var rec = TimeflowRecorder.Start(TimeflowMachines.SourceFor(m), o);
            var inp = TimeflowInput.Parse("60:Start,66:,200:A,260:", "", null);
            for (long f = 0; f < 400; f++) { var (a, b) = inp.At(f); rec.BeforeFrame(a, b); m.Frame(a, b); rec.AfterFrame(); }
            cov?.Detach();
            return rec.Stop();
        }
        string withX = Rec(true), without = Rec(false);
        using var rd = new TimelineReader(withX);
        using var r0 = new TimelineReader(without);
        var e10 = rd.Extras(10); var e300 = rd.Extras(300);
        Check("index: the watched variables by frame", rd.Meta.Vars.Count == 2 && e10.Vars.Length == 2 && e10.Vars[0] == 0 && e300.Vars[0] == 1, $"app_state {e10.Vars.ElementAtOrDefault(0)} at frame 10, {e300.Vars.ElementAtOrDefault(0)} at 300; lives {e300.Vars.ElementAtOrDefault(1)}");
        Check("index: the frame's coverage (ROM ranges run)", e300.Coverage.Count > 0 && e300.Coverage.All(c => c.Length > 0), $"{e300.Coverage.Count} ranges at frame 300, {e300.Coverage.Sum(c => c.Length)} ROM bytes");
        int ramIdx = rd.Meta.Domains.FindIndex(d => d.Id == "RAM");
        Check("index: the changed bytes and pages of the work RAM", ramIdx >= 0 && e300.Domains[ramIdx].ChangedBytes > 0 && e300.Domains[ramIdx].Pages.Length > 0, $"RAM: {e300.Domains[ramIdx].ChangedBytes} bytes in pages {string.Join(",", e300.Domains[ramIdx].Pages)} at frame 300");
        var all = Enumerable.Range(0, 400).Select(f => rd.Extras(f)).ToList();
        int findingFrames = all.Count(x => x.Findings.Count > 0);
        Check("index: the checks' findings, each in the frame it was first seen", all.Sum(x => x.Findings.Count) == all.SelectMany(x => x.Findings).Distinct().Count(), $"{all.Sum(x => x.Findings.Count)} finding(s) in {findingFrames} frame(s): {string.Join("; ", all.SelectMany(x => x.Findings).Take(3))}");
        var n300 = r0.Extras(300);
        Check("index (negative): without them, no variables, no coverage, no findings (the changed bytes stay)", n300.Vars.Length == 0 && n300.Coverage.Count == 0 && n300.Findings.Count == 0 && n300.Domains[ramIdx].ChangedBytes == e300.Domains[ramIdx].ChangedBytes, $"vars {n300.Vars.Length}, coverage {n300.Coverage.Count}, RAM changed {n300.Domains[ramIdx].ChangedBytes}");
        Check("index: the debugger's hooks leave the recording frame-identical", Enumerable.Range(0, 400).All(f => rd.Row(f).State == r0.Row(f).State && rd.Row(f).Picture == r0.Row(f).Picture), "every frame's state and picture hash the same with and without coverage and checks");
    }

    // ------------------------------------------------------------------------------------------------ index-sega

    /// <summary>The debugger's test ROMs of the Sega consoles (Workshop/Debug/DebugTestRomsSega.cs) recorded with the index's extras: the watched variables follow the game,
    /// the frame's coverage holds the code the frame ran (the main loop's subroutines, the bank-2 function at its ROM offset; on the Genesis the 68000's ROM), and the
    /// recording is frame-identical with and without them (negative: without them, none).</summary>
    private static void IndexSega(string dir)
    {
        var (smsRom, smsAsm, smsSym) = BrokenNes.Workshop.Debug.SegaDebugTestRoms.MasterSystem(false);
        var (ggRom, _, ggSym) = BrokenNes.Workshop.Debug.SegaDebugTestRoms.MasterSystem(true);
        var (mdRom, mdAsm, _, mdNm) = BrokenNes.Workshop.Debug.SegaDebugTestRoms.Genesis();
        var cases = new (string tag, string rom, string sym, string console, string[] vars, uint covered, uint coveredLen, uint banked)[]
        {
            ("sms", DebugTestRoms.Write(dir, "indexed.sms", smsRom), DebugTestRoms.Write(dir, "indexed_sms.sym", smsSym), "sms", new[] { "counter=[counter]", "turns={turns}" }, smsAsm["sub2"], smsAsm["sub2_ret"] - smsAsm["sub2"] + 1, 0x8000),
            ("gg", DebugTestRoms.Write(dir, "indexed.gg", ggRom), DebugTestRoms.Write(dir, "indexed_gg.sym", ggSym), "gg", new[] { "counter=[counter]", "turns={turns}" }, smsAsm["sub2"], smsAsm["sub2_ret"] - smsAsm["sub2"] + 1, 0x8000),
            ("md", DebugTestRoms.Write(dir, "indexed.md", mdRom), DebugTestRoms.Write(dir, "indexed_md.txt", mdNm), "md", new[] { "counter=[$FF0001]", "vints=[$FF0007]" }, mdAsm["sub2"], mdAsm["sub2_ret"] - mdAsm["sub2"] + 2, uint.MaxValue),
        };
        foreach (var (tag, rom, sym, console, vars, covered, coveredLen, banked) in cases)
        {
            string Rec(bool extras)
            {
                using var m = MovieMachine.Open(rom, console, "", "fceux", false, true, new[] { sym });
                var o = new TimeflowOptions { Rom = m.RomFile, RomName = m.RomName, Cores = m.Cores, Source = "test", PowerOnStart = true };
                FrameCoverage? cov = null;
                if (extras)
                {
                    var d = m.Host.Dbg;
                    o.VarNames = vars.ToList();
                    var exprs = vars.Select(v => v[(v.IndexOf('=') + 1)..]).ToArray();
                    o.Vars = () => exprs.Select(e => d.Evaluate(e)).ToArray();
                    cov = FrameCoverage.Attach(d); o.Coverage = cov;
                }
                var rec = TimeflowRecorder.Start(TimeflowMachines.SourceFor(m), o);
                for (long f = 0; f < 240; f++) { rec.BeforeFrame(PadButtons.None, PadButtons.None); m.Frame(PadButtons.None, PadButtons.None); rec.AfterFrame(); }
                cov?.Detach();
                return rec.Stop();
            }
            string withX = Rec(true), without = Rec(false);
            using var rd = new TimelineReader(withX);
            using var r0 = new TimelineReader(without);
            var e20 = rd.Extras(20); var e200 = rd.Extras(200);
            Check($"index-sega {tag}: the watched variables by frame", rd.Meta.Vars.Count == 2 && e20.Vars.Length == 2 && e200.Vars.Length == 2 && e200.Vars[1] != e20.Vars[1],
                  $"{vars[0]} {e20.Vars.ElementAtOrDefault(0)} at frame 20, {e200.Vars.ElementAtOrDefault(0)} at 200; {vars[1]} {e200.Vars.ElementAtOrDefault(1)}");
            bool Covers(List<(uint Start, uint Length)> c, uint at, uint len) => c.Any(r => r.Start <= at && r.Start + r.Length >= at + len);
            Check($"index-sega {tag}: the frame's coverage holds the code the frame ran", Covers(e200.Coverage, covered, coveredLen) && (banked == uint.MaxValue || Covers(e200.Coverage, banked, 6)),
                  $"{e200.Coverage.Count} ROM ranges at frame 200: {string.Join(", ", e200.Coverage.Take(6).Select(r => $"${r.Start:X}+{r.Length}"))}; sub2 at ${covered:X}{(banked != uint.MaxValue ? $", bank 2 at ${banked:X}" : "")}");
            Check($"index-sega {tag} (negative): code that never runs is not in the coverage", !Covers(e200.Coverage, banked == uint.MaxValue ? mdAsm["known"] : 0x4000, 2),
                  banked == uint.MaxValue ? "the never-run known stretch is not covered" : "bank 1's function (ROM $4000) is not covered");
            var n200 = r0.Extras(200);
            Check($"index-sega {tag} (negative): without them, no variables and no coverage", n200.Vars.Length == 0 && n200.Coverage.Count == 0, $"vars {n200.Vars.Length}, coverage {n200.Coverage.Count}");
            Check($"index-sega {tag}: the debugger's hooks leave the recording frame-identical", Enumerable.Range(0, 240).All(f => rd.Row(f).State == r0.Row(f).State && rd.Row(f).Picture == r0.Row(f).Picture), "every frame's state and picture hash the same with and without the extras");
        }
    }

    // ------------------------------------------------------------------------------------------------ apu

    private static string Field(string json, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json, "\\\\\"" + name + "\\\\\":(-?\\d+)");
        return m.Success ? m.Groups[1].Value : "?";
    }

    private static void ApuStep(string nes)
    {
        foreach (int at in new[] { 100, 150, 233 })
        {
            using var a = Open(nes);
            for (int f = 0; f < at; f++) a.Frame(f % 60 == 10 ? PadButtons.Start : PadButtons.None, PadButtons.None);
            string saved = a.Host.Nes!.SaveState();
            string step = Field(saved, "frameStep"), next = Field(saved, "nextFrameEvent"), cyc = Field(saved, "frameCycle");
            using var b = Open(nes);
            b.Host.Nes!.LoadState(saved);
            string again = b.Host.Nes.SaveState();
            Check($"apu: frame {at}: the step is saved and comes back", step != "?" && Field(again, "frameStep") == step && Field(again, "nextFrameEvent") == next, $"frameCycle {cyc}: step {step}, next event {next}; loaded: {Field(again, "frameStep")}, {Field(again, "nextFrameEvent")}");
            // an older state without the two fields: the step is derived from frameCycle and comes out the same
            string old = System.Text.RegularExpressions.Regex.Replace(saved, ",\\\\\"frameStep\\\\\":-?\\d+,\\\\\"nextFrameEvent\\\\\":-?\\d+", "");
            using var c = Open(nes);
            c.Host.Nes!.LoadState(old);
            string derived = c.Host.Nes.SaveState();
            Check($"apu: frame {at} (an older state): the step derived from frameCycle is the saved one", !old.Contains("frameStep") && Field(derived, "frameStep") == step && Field(derived, "nextFrameEvent") == next, $"derived {Field(derived, "frameStep")}, {Field(derived, "nextFrameEvent")}");
        }
        // negative control: a fresh machine's step (what a load used to leave) differs from a saved mid-frame-sequence one
        using var fresh = Open(nes);
        fresh.Frame(PadButtons.None, PadButtons.None);
        using var mid = Open(nes);
        for (int f = 0; f < 150; f++) mid.Frame(PadButtons.None, PadButtons.None);
        Check("apu (negative): the step of another machine is not the saved one", Field(fresh.Host.Nes!.SaveState(), "frameCycle") != Field(mid.Host.Nes!.SaveState(), "frameCycle"), $"fresh frameCycle {Field(fresh.Host.Nes.SaveState(), "frameCycle")}, saved {Field(mid.Host.Nes.SaveState(), "frameCycle")}");
    }

    // ------------------------------------------------------------------------------------------------ places

    private static void Places(string dir)
    {
        string root = TimeflowStore.Root();
        Check("places: the root is BROKENDEV_HOME\\timeflow", root.Equals(Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("BROKENDEV_HOME")!, "timeflow")), StringComparison.OrdinalIgnoreCase), root);
        string g = TimeflowStore.GameDir(new string('a', 64), "My Game.nes", out string project);
        Check("places: a ROM of no project goes to _roms\\<name>-<sha>", project == "" && g.EndsWith(Path.Combine("_roms", "My_Game-aaaaaaaaaaaa")), g);
        // a ROM that is a project's build (its sha256 in a build record) goes to the project's folder
        string home = Environment.GetEnvironmentVariable("BROKENDEV_HOME")!;
        string sha = new string('b', 64);
        Directory.CreateDirectory(Path.Combine(home, "out", "mygame"));
        File.WriteAllText(Path.Combine(home, "out", "mygame", "build.json"), $"{{\"ok\":true,\"project\":\"mygame\",\"outputs\":[{{\"id\":\"rom\",\"sha256\":\"{sha}\"}}]}}");
        string pg = TimeflowStore.GameDir(sha, "game.nes", out string p2);
        Check("places: a project's build goes to timeflow\\<project>", p2 == "mygame" && pg == Path.Combine(root, "mygame"), pg);
        string app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes", "dev");
        string? old = Environment.GetEnvironmentVariable("BROKENDEV_HOME");
        Environment.SetEnvironmentVariable("BROKENDEV_HOME", app);
        Check("places (negative): a BROKENDEV_HOME under %APPDATA%\\BrokenNes is refused", Throws(() => TimeflowStore.Root(), out string why), why);
        Environment.SetEnvironmentVariable("BROKENDEV_HOME", old);
        Check("places: nothing was made under %APPDATA%\\BrokenNes\\dev", !Directory.Exists(app), app);
    }
}
