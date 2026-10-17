using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes;
using BrokenNes.CorruptorModels;
using NesEmulator;
using NesEmulator.Corruption;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Corruption;

/// <summary>
/// The Glitch Harvester engine over the stockpile library, headless: the farm on a console game (the way the desktop app uses it), a stockpile file out and back in, and the NES path unchanged.
/// </summary>
internal static class CorruptEngineChecks
{
    /// <summary>The desktop's part, in a test: one SNES game run frame by frame on this thread, a farm fed with its pads, and replayed machines installed in place of the running one.</summary>
    private sealed class TestHost : IConsoleGhHost
    {
        public IConsoleSession Session;
        public readonly ConsoleFarm Farm = new();
        private readonly byte[] rom;
        private readonly short[] buf = new short[32768];
        private ulong audio = 1469598103934665603UL;
        public int Installed;

        public TestHost(byte[] rom, string apu = "SFC")
        {
            this.rom = rom;
            Session = ConsoleSessions.Create(ConsoleKind.Snes, (byte[])rom.Clone(), "SFC", "SFC", apu, null, "synth.sfc");
            Farm.Attach(Session, rom, "synth.sfc", new MachineSpec { Cpu = "SFC", Ppu = "SFC", Apu = apu }, coresAreOwn: apu == "SFC", notOwnReason: "the sound unit is not the SNES's own");
        }

        public ConsoleFarm? FarmOrNull => Farm;
        ConsoleFarm? IConsoleGhHost.Farm => Farm;
        public T BetweenFrames<T>(Func<T> work) => work();
        public StartResult StartEntry(StockpileEntry entry, bool allowOtherGame) =>
            Farm.Start(entry, null, new StartOptions { AllowOtherGame = allowOtherGame }, s => { Session.Dispose(); Session = s; Installed++; return true; });

        public void Run(int frames, Func<int, PadButtons>? pad = null)
        {
            for (int i = 0; i < frames; i++)
            {
                var p = pad?.Invoke(Farm.Frames) ?? PadButtons.None;
                Farm.OnFrame((ushort)p, 0);
                Session.SetPad(0, p);
                Session.RunFrame();
                int n;
                while ((n = Session.ReadSamples(buf)) > 0) for (int k = 0; k < n; k++) audio = (audio ^ (ushort)buf[k]) * 1099511628211UL;
            }
        }

        public ulong Aram() => Hash.Fnv(((SnesSession)Session).Spc!.Aram);
        public ulong Dsp() => Hash.Fnv(((SnesSession)Session).Spc!.Dsp.Regs);
        public ulong Wram() => Hash.Fnv(((SnesSession)Session).Board.Wram);
        public ulong Audio => audio;
        public void ResetAudio() => audio = 1469598103934665603UL;
    }

    private static Corruptor MakeCorruptor(IConsoleSession s, params string[] selected)
    {
        var c = new Corruptor();
        foreach (var d in MachineDomains.For(s))
            c.MemoryDomains.Add(new DomainSel { Key = d.Id, Label = d.Label, Size = d.Size, Selected = selected.Contains(d.Id) });
        return c;
    }

    public static void Run(Checks c, byte[] rom)
    {
        Func<int, PadButtons> pad = f => f is >= 60 and < 90 ? PadButtons.A : PadButtons.None;

        // ---- a base, then corrupt-and-stash with "load on operation" off: the current position, blasted
        var host = new TestHost(rom);
        host.Run(120, pad);
        var corruptor = MakeCorruptor(host.Session, "ARAM");
        corruptor.CorruptIntensity = 8; corruptor.BlastType = "BITFLIP";
        var engine = corruptor.GlitchHarvester;
        engine.LoadOnOperation = false;
        var b1 = engine.AddBaseState(host, "synth at 120");
        c.Ok("a base on a console game is the replay from power-on that reaches it (the SNES has no save state)",
             b1.Anchor is { Kind: AnchorKind.Boot, Frames: 120 } && b1.Anchor.Inputs == "60:A,90:" && b1.State.Length == 0 && b1.Game!.Sha1 == AnchorStart.Sha1Hex(rom) && engine.SelectedBaseId == b1.Id, b1.Anchor?.Inputs);
        ulong aramBefore = host.Aram();
        var e1 = engine.CorruptAndStash(host);
        c.Ok("corrupt-and-stash blasts the running game: 8 BITFLIP writes in the audio RAM, each recording the byte that was there", e1.Writes.Count == 8 && e1.Writes.All(w => w.Domain == "ARAM" && w.Was >= 0 && System.Numerics.BitOperations.PopCount((uint)(w.Value ^ w.Was)) == 1), $"{e1.Writes.Count} writes");
        c.Ok("the audio RAM really changed (it differs from before the blast)", host.Aram() != aramBefore);
        c.Ok("the entry's anchor is the position BEFORE the writes (a boot recipe of 120 frames with the recorded pad, no events yet) and it names the game and the machine",
             e1.Anchor is { Kind: AnchorKind.Boot, Frames: 120, Events.Count: 0 } && e1.Anchor.Inputs == "60:A,90:" && e1.Game!.Console == "snes" && e1.Machine!.Apu == "SFC" && e1.Source is { Blast: "BITFLIP", Intensity: 8, SizeModel: "true" });
        c.Ok("it is in the stash with a name", engine.Stash.Count == 1 && e1.Name == "Stash 1");
        ulong afterAram = host.Aram(), afterWram = host.Wram(), afterDsp = host.Dsp();

        // ---- keep it, save the file, read it in a fresh engine, start it on a different machine
        engine.PromoteToStockpile(e1.Id);
        engine.DescribeStockpileEntry(e1.Id, "flipped bits in the instrument");
        engine.RenameStockpileEntry(e1.Id, "Drum growl");
        string dir = Path.Combine(Path.GetTempPath(), "bn-corrupt-engine-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "farm" + Stockpile.FileSuffix);
        c.Ok("the stockpile is dirty until saved", engine.StockpileDirty);
        engine.SaveStockpileFile(path);
        c.Ok("SaveStockpileFile writes the v2 file and the stockpile is no longer dirty", File.Exists(path) && !engine.StockpileDirty && engine.StockpilePath == path && new FileInfo(path).Length < 4000, $"{new FileInfo(path).Length:N0} bytes");

        var fresh = new TestHost(rom);
        fresh.Run(37, f => PadButtons.Start);                   // a different machine, somewhere else in time
        var engine2 = MakeCorruptor(fresh.Session, "ARAM").GlitchHarvester;
        ulong preAram = fresh.Aram();
        var imp = engine2.OpenStockpileFile(path);
        c.Ok("a fresh engine opens the file: one entry, name and description kept, nothing refused", imp.Added == 1 && imp.Refusals.Count == 0 && engine2.Stockpile.Count == 1 && engine2.Stockpile[0].Name == "Drum growl" && engine2.Stockpile[0].Description == "flipped bits in the instrument" && engine2.Stockpile[0].Id == e1.Id);
        var started = engine2.ReplayEntry(fresh, engine2.Stockpile[0]);
        c.Ok("starting the entry replays 120 frames from power-on and installs the new machine in place of the running one", started.Ok && started.SessionIsNew && started.FramesReplayed == 120 && fresh.Installed == 1, started.Message);
        c.Ok("**the machine reached is exactly the one that was corrupted: work RAM, audio RAM and S-DSP registers equal**", fresh.Wram() == afterWram && fresh.Aram() == afterAram && fresh.Dsp() == afterDsp, $"ARAM {fresh.Aram():X16} vs {afterAram:X16}");
        c.Control("the machine the replay replaced (37 frames of Start held) was a different machine, so equal hashes mean the replay did it", preAram != afterAram);
        host.ResetAudio(); fresh.ResetAudio();
        host.Run(60, pad); fresh.Run(60, pad);
        c.Ok("...and it goes on the same way: 60 more frames give the same sound, work RAM and audio RAM", host.Audio == fresh.Audio && host.Aram() == fresh.Aram() && host.Wram() == fresh.Wram());
        c.Ok("the recording continues from the replayed machine (frames 180, the pad history, the entry's writes remembered as an event)", fresh.Farm.Frames == 180 && fresh.Farm.RecordedWrites == 8 && fresh.Farm.PoisonReason == null);

        // ---- the position after more play and a second blast is stored with the first blast inside it (events)
        var corr3 = MakeCorruptor(fresh.Session, "ARAM"); corr3.CorruptIntensity = 4; corr3.BlastType = "RANDOM";
        corr3.GlitchHarvester.LoadOnOperation = false;
        var e2 = corr3.GlitchHarvester.CorruptAndStash(fresh);
        c.Ok("a second blast on the replayed machine stores a recipe that carries the first blast as an event at frame 120",
             e2.Anchor is { Kind: AnchorKind.Boot, Frames: 180 } a2 && a2.Events.Count == 1 && a2.Events[0].Frame == 120 && a2.Events[0].Writes.Count == 8 && e2.Writes.Count == 4);
        ulong a2Aram = fresh.Aram(), a2Wram = fresh.Wram();
        var third = new TestHost(rom); third.Run(5);
        var corr4 = MakeCorruptor(third.Session, "ARAM").GlitchHarvester;
        corr4.Stockpile.Add(e2);
        var s2 = corr4.ReplayEntry(third, e2);
        c.Ok("and starting that entry on a third machine reaches the machine with both blasts in it (work RAM and audio RAM equal)", s2.Ok && third.Aram() == a2Aram && third.Wram() == a2Wram, s2.Message);

        // ---- live blast (the RTC tab's Manual Blast) is remembered too
        var live = new TestHost(rom); live.Run(50, pad);
        var corr5 = MakeCorruptor(live.Session, "WRAM", "ARAM"); corr5.CorruptIntensity = 20; corr5.BlastType = "RANDOM";
        var rep = corr5.GlitchHarvester.LiveBlast(live);
        live.Run(25, pad);
        var b5 = corr5.GlitchHarvester.AddBaseState(live, "after a live blast");
        c.Ok("a live blast is applied (20 writes) and remembered: the base taken later carries it as an event", rep.Applied == 20 && b5.Anchor!.Events.Count == 1 && b5.Anchor.Events[0].Frame == 50 && b5.Anchor.Frames == 75, rep.Describe());
        var back = new TestHost(rom); back.Run(9);
        var r5 = corr5.GlitchHarvester.LoadBase(back, b5.Id);
        c.Ok("loading that base on another machine reaches the live-blasted machine exactly (work RAM, audio RAM, S-DSP)", r5.Ok && back.Wram() == live.Wram() && back.Aram() == live.Aram() && back.Dsp() == live.Dsp(), r5.Message);

        // ---- load on operation: the base is loaded, then blasted; two stashes from one base share its anchor
        var host6 = new TestHost(rom); host6.Run(100, pad);
        var corr6 = MakeCorruptor(host6.Session, "ARAM"); corr6.CorruptIntensity = 3;
        var en6 = corr6.GlitchHarvester;
        var base6 = en6.AddBaseState(host6, "base");
        host6.Run(200, pad);                                   // the game moves on
        var x1 = en6.CorruptAndStash(host6); var x2 = en6.CorruptAndStash(host6);
        c.Ok("with 'load on operation' on, each blast starts from the base: both entries have the base's anchor (100 frames) and different writes",
             x1.Anchor!.Frames == 100 && x2.Anchor!.Frames == 100 && x1.Anchor.Inputs == base6.Anchor!.Inputs && !x1.Writes.SequenceEqual(x2.Writes, new WriteEq()) && host6.Installed == 2, $"installed {host6.Installed}");

        // ---- refusals say why
        var host7 = new TestHost(rom); host7.Run(20);
        host7.Farm.Poison("the game was reset");
        var en7 = MakeCorruptor(host7.Session, "ARAM").GlitchHarvester;
        var ex7 = Assert.Throws<InvalidOperationException>(() => en7.AddBaseState(host7));
        c.Ok("after a reset a position cannot be stored, and the message says why and what to do", ex7 != null && ex7.Message.Contains("reset") && ex7.Message.Contains("Restart the game"), ex7?.Message ?? "");
        var host8 = new TestHost(rom, apu: "HLE"); host8.Run(5);
        var en8 = MakeCorruptor(host8.Session, "ARAM", "WRAM").GlitchHarvester;
        var ex8 = Assert.Throws<InvalidOperationException>(() => en8.AddBaseState(host8));
        c.Ok("under a sound unit that is not the console's own no replay is promised: refused with the reason", ex8 != null && ex8.Message.Contains("not the SNES's own"), ex8?.Message ?? "");
        var host9 = new TestHost(rom); host9.Run(5);
        var corr9 = MakeCorruptor(host9.Session); // nothing selected
        c.Ok("a blast with no domain selected applies nothing and says so", corr9.GlitchHarvester.LiveBlast(host9) is { Applied: 0 });
        var nesEntry = new HarvestEntry { Name = "from the NES", State = "{\"romHash\":\"abc\"}" };
        var rn = engine.ReplayEntry(host9, nesEntry);
        c.Ok("an entry made on the NES is refused on a console game, with the reason", !rn.Ok && rn.Message.Contains("NES"), rn.Message);
        var other = new TestHost(rom); other.Run(3);
        var foreign = new HarvestEntry { Name = "other game", Anchor = new Anchor { Kind = AnchorKind.Boot, Frames = 3 }, Game = new GameRef { Console = "snes", Sha1 = "00ff", Title = "ANOTHER GAME" } };
        var rf = engine.ReplayEntry(other, foreign);
        c.Ok("an entry made on another game is refused, naming it (and nothing about the running game changed)", !rf.Ok && rf.Message.Contains("ANOTHER GAME") && other.Installed == 0, rf.Message);

        // ---- older files through the engine
        var legacy = MakeCorruptor(host9.Session).GlitchHarvester;
        string lite = "[{\"Id\":\"22222222-2222-2222-2222-222222222222\",\"Name\":\"From Lite\",\"BaseStateId\":\"b\",\"Created\":\"2026-09-02T10:00:00Z\",\"Writes\":[{\"Domain\":\"CHR\",\"Address\":7,\"Value\":9}],\"BaseState\":\"{\\\"romHash\\\":\\\"ab\\\"}\"}]";
        var li = legacy.ImportStockpile(lite);
        c.Ok("Lite's older export (BaseState) imports through the engine with its machine state, and the id is kept", li.Added == 1 && li.Legacy && legacy.Stockpile[0].State.Contains("romHash") && legacy.Stockpile[0].Id == "22222222-2222-2222-2222-222222222222");
        var again = legacy.ImportStockpile(lite);
        c.Ok("importing the same entry again gives it a new id (no two entries share one)", again.Added == 1 && legacy.Stockpile.Count == 2 && legacy.Stockpile[0].Id != legacy.Stockpile[1].Id);
        string exported = legacy.ExportStockpile();
        var rr = StockpileJson.Read(exported);
        c.Ok("the export is the v2 file (reads back with no older-file warning)", rr.Ok && !rr.Legacy && rr.Stockpile!.Entries.Count == 2 && rr.Stockpile.Entries[0].Anchor.Kind == AnchorKind.State && rr.Stockpile.Entries[0].Anchor.StateFormat == "nes-json");
        var bad = Assert.Throws<ArgumentException>(() => legacy.ImportStockpile("{ not json"));
        c.Ok("a file that cannot be read is refused with the reason, and the stockpile is untouched", bad != null && bad.Message.Contains("not valid JSON") && legacy.Stockpile.Count == 2);
    }

    private sealed class WriteEq : IEqualityComparer<BlastInstruction>
    {
        public bool Equals(BlastInstruction? a, BlastInstruction? b) => a != null && b != null && a.Domain == b.Domain && a.Address == b.Address && a.Value == b.Value;
        public int GetHashCode(BlastInstruction x) => HashCode.Combine(x.Domain, x.Address, x.Value);
    }

    private static class Assert
    {
        public static T? Throws<T>(Func<object> f) where T : Exception
        {
            try { f(); return null; } catch (T e) { return e; }
        }
        public static T? Throws<T>(Action f) where T : Exception
        {
            try { f(); return null; } catch (T e) { return e; }
        }
    }

    // ===================================================================================================== the NES path

    public static void Nes(Checks c)
    {
        string romPath = FindStoryRom();
        if (romPath == "") { c.Skip("the NES path of the engine", "no NES test ROM found (run from the repository, or set BROKENNES_TEST_NES)"); return; }
        var rom = File.ReadAllBytes(romPath);
        NES Make() { var n = new NES { RomName = Path.GetFileName(romPath) }; n.LoadROM(rom); n.SetCpuCore("FIX"); n.SetPpuCore("FIX"); n.SetApuCore("FIX"); return n; }
        ulong Picture(NES n) => Hash.Fnv(n.GetFrameBuffer());

        var nes = Make();
        for (int i = 0; i < 90; i++) nes.RunFrame();
        string state = nes.SaveState();
        var corruptor = new Corruptor();
        var engine = corruptor.GlitchHarvester;
        var entry = new HarvestEntry { Name = "Entry 1", BaseStateId = "", State = state, Writes = { new BlastInstruction { Domain = "RAM", Address = 16, Value = 255 }, new BlastInstruction { Domain = "RAM", Address = 17, Value = 7 } } };
        engine.Stockpile.Add(entry);

        // the engine's own replay (unchanged code) on a second NES, for reference
        var viaEngine = Make();
        engine.ReplayStockpileEntry(viaEngine, entry.Id);
        c.Ok("(NES) the engine's own replay loads the saved state and applies the writes", viaEngine.PeekSystemRam(16) == 255 && viaEngine.PeekSystemRam(17) == 7);

        string json = engine.ExportStockpile();
        var re = StockpileJson.Read(json);
        c.Ok("(NES) the export is a v2 file whose entry holds the NES saved state under the 'nes-json' format, the game's hash and file name", re.Ok && re.Stockpile!.Entries.Count == 1 && re.Stockpile.Entries[0].Anchor is { Kind: AnchorKind.State, StateFormat: "nes-json" } && re.Stockpile.Entries[0].Game.Sha256.Length == 64 && re.Stockpile.Entries[0].Game.File == Path.GetFileName(romPath), re.Stockpile?.Entries[0].Game.Sha256);

        var engine2 = new Corruptor().GlitchHarvester;
        var imp = engine2.ImportStockpile(json);
        var back = engine2.Stockpile[0];
        c.Ok("(NES) it imports into another engine with the very same state text and writes", imp.Added == 1 && back.State == state && back.Writes.Count == 2 && back.Writes[0].Domain == "RAM" && back.Writes[0].Address == 16 && back.Writes[0].Value == 255);
        var viaImport = Make();
        engine2.ReplayStockpileEntry(viaImport, back.Id);
        c.Ok("(NES) replaying the imported entry gives the machine the original entry gives (picture and RAM equal, then the same 30 frames later)",
             Picture(viaImport) == Picture(viaEngine) && viaImport.PeekSystemRam(16) == 255 && RunAndHash(viaImport, 30) == RunAndHash(viaEngine, 30));
        // control: a state that is not the entry's gives another machine
        var other = Make();
        for (int i = 0; i < 20; i++) other.RunFrame();
        c.Control("(NES) a machine that did not load the entry's state is a different machine", Picture(other) != Picture(viaImport) || other.PeekSystemRam(16) != 255);

        // the true sizes of the NES domains (the library) and the guessed ones (the app)
        var d = MachineDomains.For(nes);
        c.Ok("(NES) the library's NES domains have true sizes: RAM 2048, PRG the cartridge's, CHR the cartridge's", d.Single(x => x.Id == "RAM").Size == 2048 && d.Single(x => x.Id == "PRG").Size > 4096 && d.Single(x => x.Id == "CHR").Size > 0, string.Join(" ", d.Select(x => $"{x.Id}={x.Size}")));
    }

    private static ulong RunAndHash(NES n, int frames)
    {
        for (int i = 0; i < frames; i++) n.RunFrame();
        return Hash.Fnv(n.GetFrameBuffer());
    }

    private static string FindStoryRom()
    {
        string dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++, dir = Path.GetDirectoryName(dir)!)
        {
            string p = Path.Combine(dir, "Windows", "Data", "story", "page1_binty.nes");
            if (File.Exists(p)) return p;
        }
        string env = Environment.GetEnvironmentVariable("BROKENNES_TEST_NES") ?? "";
        return File.Exists(env) ? env : "";
    }
}
