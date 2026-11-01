// Round 5 (corr, milestone C4): corruptions from a stockpile in the plugin, end to end.
//
// What the user does: farm a corruption in BrokenNes (the desktop app saves it into a stockpile file), open that file in the plugin's Corruption window, pick the corruption, Start it on the game that is loaded,
// take its instruments with Instrument Runaway, and save the FL project. This test does each of those with the same code the desktop uses to farm (the shared corruption library: a console game run by a farm that records
// the pad and the writes, a stockpile entry, a stockpile file), then drives the real plugin DLL through its real editor with real mouse messages. Tiny games of our own stand in for real ones (the repository carries none):
// a synthetic SNES game with a real SPC700 instrument in audio RAM, and counter games for the Game Boy / Color, Master System, Game Gear and Genesis.
//
// Proofs: the machine the plugin built from the entry equals the machine the farm had (memory hashes); a corruption of a SNES instrument changes the sound that Instrument Runaway plays and the sound survives a saved
// project; a project saved with a started corruption comes back as the same machine (SNES: rebuilt by replaying its recorded history; the others: their saved state), after the same frames equal to a game that never left;
// everything that cannot work says so on screen; nothing allocates on the audio thread while a corruption starts.
using System.Runtime.InteropServices;
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using NesEmulator.Corruption;
using NesEmulator.Systems;
using SynthSnes = BrokenNes.Workshop.Corruption.SynthSnesRom;
using SpcSnapshot = BrokenNes2.SpcSnapshot;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    // test hooks of the plugin (Bn2Plugin.TestDispatcher) and of the editor (Bn2Editor.Test)
    private const int HkCorOpen = 100, HkCorBusy = 101, HkCorCount = 102, HkCorRow = 103, HkCorSelect = 104, HkCorFreeze = 105, HkCorStart = 106, HkCorCancel = 107, HkCorRemove = 108, HkCorText = 109,
        HkCorGood = 110, HkCorStamp = 111, HkCorApplied = 112, HkCorFrames = 113, HkCorPoisoned = 114, HkAramHash = 115, HkWramHash = 116, HkSnapHash = 117, HkAramPeek = 118, HkAramPoke = 119;
    private const int EdCorOpen = 140, EdCorNote = 141, EdCorRows = 142, EdCorScroll = 143, EdCorPick = 144;
    // control ids of the Corruption window
    private const int IdCor = 500, IdCorClose = 501, IdCorOpenBtn = 502, IdCorStart = 503, IdCorCancel = 504, IdCorRemove = 505, IdCorFreeze = 506, IdCorRow = 520;
    private const int PadA = 1 << 4;

    private sealed record CorrFamily(string Name, int Console, string Chip, ConsoleKind Kind, string Ext, byte[] Rom, int Before, int After, CorruptionWrite[] Writes, string Work, bool Aram, Func<int, int>? Pad = null, bool Replay = false);

    /// <summary>What farming a family's corruption left: the files, the entry, and what the farm's own machine looked like at each step (the reference the plugin must match).</summary>
    private sealed record CorrMade(string Rom, string Stock, StockpileEntry Entry, long Work0, long Aram0, long AramClean, long WorkEnd, long AramEnd, long WorkClean, int FarmFrames);

    private static long CorFnv(ReadOnlySpan<byte> b) { ulong h = 1469598103934665603UL; foreach (byte x in b) h = (h ^ x) * 1099511628211UL; return (long)(h | 1); }

    /// <summary>A console game run the way the desktop app farms it: a session, and a farm that records the pad and the writes.</summary>
    private sealed class CorrRig : IDisposable
    {
        public readonly IConsoleSession S;
        public readonly ConsoleFarm Farm = new();
        public readonly MachineSpec Spec;
        public readonly string FileName;
        public readonly byte[] Rom;
        private readonly short[] buf = new short[1 << 16];

        public CorrRig(ConsoleKind kind, byte[] rom, string fileName)
        {
            Rom = rom; FileName = fileName;
            Spec = new MachineSpec { Cpu = CoreCatalog.Default(kind, CoreSlot.Cpu), Ppu = CoreCatalog.Default(kind, CoreSlot.Ppu), Apu = CoreCatalog.Default(kind, CoreSlot.Apu) };
            S = ConsoleSessions.Create(kind, (byte[])rom.Clone(), Spec.Cpu, Spec.Ppu, Spec.Apu, null, fileName);
            Farm.Attach(S, rom, fileName, Spec, true);
        }

        public void Run(int frames, Func<int, int>? pad = null)
        {
            for (int i = 0; i < frames; i++)
            {
                int p = pad?.Invoke(Farm.Frames) ?? 0;
                Farm.OnFrame((ushort)p, 0);
                S.SetPad(0, (PadButtons)p);
                S.RunFrame();
                while (S.ReadSamples(buf) > 0) { }
            }
        }

        public long Hash(string domain)
        {
            var d = MachineDomains.Find(MachineDomains.For(S), domain) ?? throw new InvalidOperationException("the game has no memory called " + domain);
            var b = new byte[d.Size];
            for (int i = 0; i < b.Length; i++) b[i] = d.Peek(i);
            return CorFnv(b);
        }

        public void Dispose() => S.Dispose();
    }

    private static CorrFamily[] CorFamilies() =>
    [
        // the SNES: the synthetic game (a real SPC700 program uploaded through the boot loader, a one-note instrument in audio RAM at $0500); the pad is held for a while so its history has something in it
        new("SNES", 2, "SNES", ConsoleKind.Snes, ".sfc", SynthSnes.Build(), 150, 40,
            [new("ARAM", 0x0500, 0xB3), new("ARAM", 0x0502, 0x11), new("WRAM", 0x1F0, 0x5A)], "WRAM", true, f => f is >= 100 and < 112 ? PadA : 0),
        // the same game as a console that cannot keep its state: the stockpile entry is the replay from power-on, the plugin reports no state, so the project carries the game's history (every console has states now; this keeps that path tested)
        new("SNES (replay)", 2, "SNES", ConsoleKind.Snes, ".sfc", SynthSnes.Build(), 150, 40,
            [new("ARAM", 0x0500, 0xB3), new("ARAM", 0x0502, 0x11), new("WRAM", 0x1F0, 0x5A)], "WRAM", true, f => f is >= 100 and < 112 ? PadA : 0, Replay: true),
        new("Game Boy", 1, "DMG", ConsoleKind.GameBoy, ".gb", MakeGbRom(counter: true), 60, 40, [new("WRAM", 0, 0xA5), new("WRAM", 0x1F0, 0x5A)], "WRAM", false),
        new("Game Boy Color", 1, "DMG", ConsoleKind.GameBoyColor, ".gbc", MakeGbRom(counter: true, color: true), 60, 40, [new("WRAM", 0, 0xA5), new("WRAM", 0x1F0, 0x5A)], "WRAM", false),
        new("Master System", 3, "SMS", ConsoleKind.MasterSystem, ".sms", MakeSmsCounterRom(), 60, 40, [new("RAM", 0, 0xA5), new("RAM", 0x1F0, 0x5A)], "RAM", false),
        new("Game Gear", 3, "SMS", ConsoleKind.GameGear, ".gg", MakeSmsCounterRom(gameGear: true), 60, 40, [new("RAM", 0, 0xA5), new("RAM", 0x1F0, 0x5A)], "RAM", false),
        new("Genesis", 4, "MD", ConsoleKind.Genesis, ".md", MakeMdRom(fm: true, dac: false, counter: true), 60, 40, [new("RAM", 0, 0xA5), new("RAM", 0x1F0, 0x5A)], "RAM", false),
    ];

    private static void CorPick(PluginInstance p, string path)
    {
        var ptr = Marshal.StringToHGlobalUni(path);
        try { p.Test(EdCorPick, 0, ptr); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static void CorOpenDirect(PluginInstance p, string path)
    {
        var ptr = Marshal.StringToHGlobalUni(path);
        try { p.Test(HkCorOpen, 0, ptr); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>One of the panel's texts: 0 the last result, 1 the progress, 2 why the selected row cannot start, 3 the selected row's name, 4 the stockpile's problems.</summary>
    private static string CorText(PluginInstance p, int field)
    {
        int n = (int)p.Test(HkCorText, field << 16, 0);
        if (n <= 0) return "";
        int take = Math.Min(n, 0xFFFF);
        var buf = new char[take];
        fixed (char* c = buf) p.Test(HkCorText, (field << 16) | take, (nint)c);
        return new string(buf);
    }

    /// <summary>Pixels clearly lighter than the window's dark panels (the faint text of the empty-list message and of notes is dimmer than BrightPixels' threshold).</summary>
    private static int InkPixels(byte[] bgra, int w, int x0, int y0, int rw, int rh)
    {
        int n = 0;
        for (int y = y0; y < y0 + rh; y++)
            for (int x = x0; x < x0 + rw; x++)
            {
                int o = (y * w + x) * 4;
                if (o + 2 >= bgra.Length) continue;
                if ((bgra[o] + bgra[o + 1] + bgra[o + 2]) / 3 > 70) n++;
            }
        return n;
    }

    private static bool CorWait(Func<bool> done, int ms = 30000)
    {
        long until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until) { if (done()) return true; Win32Host.Pump(15); }
        return done();
    }

    private static bool CorIdle(PluginInstance p) => (p.Test(HkCorBusy) & 3) == 0;

    /// <summary>Runs a stretch of frames the way a player would: the A button for the first <paramref name="held"/>, then nothing.</summary>
    private static void CorPlay(PluginInstance p, int frames, int held = 12)
    {
        held = Math.Min(held, frames);
        p.Test(44, 0, PadA); p.Test(HkRun, 0, held);
        p.Test(44, 0, 0); p.Test(HkRun, 0, frames - held);
    }

    // ------------------------------------------------------------------------------------------------

    private void CorruptionTest(T t)
    {
        string dir = Path.Combine(Path.GetTempPath(), "bn2_corr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using (var h = lib.Create()) h.Test(120, 0, 1);              // the editor does not run the games to keep their pictures moving: the test counts frames
        try
        {
            CorrMade? snes = null, replay = null;
            foreach (var f in CorFamilies())
            {
                var made = CorrFarm(t, dir, f);
                if (made == null) continue;
                if (f.Replay) { replay = made; continue; }
                if (f.Aram) snes = made;
                CorrWindowFlow(t, f, made);
                CorrRemove(t, f, made);
            }
            if (snes != null)
            {
                var f = CorFamilies()[0];
                CorrSnesInstruments(t, f, snes);
                CorrSnesProject(t, f, snes);
                CorrAllocation(t, f, snes);
                CorrScrolling(t, dir, f, snes);
                CorrCancel(t, dir, f, snes);
                CorrMessages(t, dir, f, snes);
            }
            // the same flows for a console that cannot keep its state: the entry is the replay from power-on, the project carries the game's history
            if (replay != null)
            {
                var f = CorFamilies().First(x => x.Replay);
                using (var h = lib.Create()) h.Test(121, 0, 1);
                try
                {
                    CorrWindowFlow(t, f, replay);
                    CorrRemove(t, f, replay);
                    CorrSnesInstruments(t, f, replay);
                    CorrSnesProject(t, f, replay);
                    CorrAllocation(t, f, replay);
                }
                finally { using var h = lib.Create(); h.Test(121, 0, 0); }
            }
            CorrGbWave(t, dir);
            CorrRealSmw(t);
        }
        finally
        {
            using (var h = lib.Create()) h.Test(120, 0, 0);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // farming: the same calls the desktop app makes

    private CorrMade? CorrFarm(T t, string dir, CorrFamily f)
    {
        string fileName = "game" + f.Ext;
        string rom = Path.Combine(dir, f.Name.Replace(' ', '_') + f.Ext);
        File.WriteAllBytes(rom, f.Rom);
        string stock = Path.Combine(dir, f.Name.Replace(' ', '_') + Stockpile.FileSuffix);

        using var origin = new CorrRig(f.Kind, f.Rom, fileName);
        origin.Run(f.Before, f.Pad);
        var cap = origin.Farm.Capture();
        t.Expect(cap.Anchor != null, $"{f.Name}: the farm cannot say where the game is ({cap.Reason})");
        if (cap.Anchor == null) return null;
        long aramClean = f.Aram ? origin.Hash("ARAM") : 0, workClean = origin.Hash(f.Work);
        var report = CorruptionApplier.Apply(origin.Farm.Domains(), f.Writes, null, 0);
        origin.Farm.NoteWrites(f.Writes);
        t.Expect(report.Applied == f.Writes.Length, $"{f.Name}: the farm applied {report.Applied} of {f.Writes.Length} writes ({report.Describe()})");
        long work0 = origin.Hash(f.Work), aram0 = f.Aram ? origin.Hash("ARAM") : 0;
        t.Expect(work0 != workClean, $"{f.Name}: the corruption did not change the game's work RAM");
        t.Expect(!f.Aram || aram0 != aramClean, $"{f.Name}: the corruption did not change the audio RAM");
        int farmFrames = origin.Farm.Frames;

        // the machine that never left: the same game, the same corruption, played on with the same pad
        origin.Run(f.After, fr => fr - f.Before < 12 ? PadA : 0);
        long workEnd = origin.Hash(f.Work), aramEnd = f.Aram ? origin.Hash("ARAM") : 0;

        var game = origin.Farm.Game();
        // a console with save states describes the place as a saved machine with the replay recipe beside it; the replay family keeps only the recipe
        var anchor = f.Replay ? (cap.Anchor.Kind == AnchorKind.Boot ? cap.Anchor : cap.Anchor.Alt) : cap.Anchor;
        t.Expect(anchor != null, $"{f.Name}: the farm has no {(f.Replay ? "replay recipe" : "anchor")} for this game");
        if (anchor == null) return null;
        t.Expect(f.Replay || f.Kind != ConsoleKind.Snes || anchor.Kind == AnchorKind.State, $"{f.Name}: the SNES has save states, so the farm should have stored a saved state (it stored {anchor.Kind})");
        var entry = new StockpileEntry
        {
            Name = $"{f.Name} instrument takeover", Description = $"farmed by the plugin's self-test: {f.Writes.Length} writes after {f.Before} frames", Tags = { "selftest", f.Name },
            Game = game, Machine = origin.Spec, Anchor = anchor, Writes = f.Writes.ToList(),
        };
        var otherGame = new StockpileEntry
        {
            Name = "made on another game", Game = new GameRef { Console = game.Console, Sha1 = new string('0', 40), Title = "Another Game", File = "another.rom" },
            Machine = origin.Spec, Anchor = anchor, Writes = f.Writes.ToList(),
        };
        var otherConsole = new StockpileEntry
        {
            Name = "made on another console", Game = new GameRef { Console = NesEmulator.Systems.Consoles.Key(f.Kind == ConsoleKind.Genesis ? ConsoleKind.Snes : ConsoleKind.Genesis), Sha1 = new string('1', 40), Title = "Elsewhere", File = "elsewhere.rom" },
            Machine = origin.Spec, Anchor = anchor, Writes = f.Writes.ToList(),
        };
        var sp = new Stockpile { Name = "plugin self-test", Writer = "BrokenNes2 self-test" };
        sp.Entries.Add(entry); sp.Entries.Add(otherGame); sp.Entries.Add(otherConsole);
        StockpileJson.SaveFile(stock, sp);
        t.Note($"{f.Name}: farmed {f.Writes.Length} writes at frame {farmFrames}; the stockpile is {new FileInfo(stock).Length:N0} bytes ({(anchor.Kind == AnchorKind.State ? "saved state" + (anchor.Alt != null ? " + replay recipe" : "") : "replay from power-on")})");
        return new CorrMade(rom, stock, entry, work0, aram0, aramClean, workEnd, aramEnd, workClean, farmFrames);
    }

    // ------------------------------------------------------------------------------------------------
    // the window, start, the machine, the project

    /// <summary>"Remove corruption": the game goes back to the place the corruption was made, without its writes (the farm's clean machine), and the plugin no longer says a corruption is on.</summary>
    private void CorrRemove(T t, CorrFamily f, CorrMade m)
    {
        string name = f.Name + " remove";
        using var p = SegaGame(m.Rom, f.Console, f.Chip);
        p.Test(HkRun, 0, 25);
        nint parent = Win32Host.CreateParent();
        try
        {
            p.ShowEditor(parent);
            for (int k = 0; k < 3; k++) Win32Host.Pump(40);
            ClickControl(t, p, IdCor, name + ": Corruption button");
            CorPick(p, m.Stock);
            ClickControl(t, p, IdCorOpenBtn, name + ": Open stockpile");
            t.Expect(CorWait(() => CorIdle(p)) && p.Test(HkCorCount) == 3, $"{name}: the stockpile did not open");
            ClickControl(t, p, IdCorRow, name + ": row 0");
            ClickControl(t, p, IdCorStart, name + ": Start");
            t.Expect(CorWait(() => CorIdle(p), 90000) && p.Test(HkCorGood) == 1 && p.Test(HkWramHash) == m.Work0, $"{name}: the start said '{CorText(p, 0)}'");
            Win32Host.Pump(80);                                  // (the window repaints: Cancel gives way to Remove)
            ClickControl(t, p, IdCorRemove, name + ": Remove corruption");
            t.Expect(CorWait(() => CorIdle(p), 90000), $"{name}: removing never finished");
            string said = CorText(p, 0);
            t.Expect(p.Test(HkCorGood) == 1 && said.Contains("without its writes"), $"{name}: removing said '{said}'");
            t.Expect(p.Test(HkCorApplied) == 0, $"{name}: the plugin still says a corruption is on after it was removed");
            t.Expect(p.Test(HkWramHash) == m.WorkClean && p.Test(HkWramHash) != m.Work0, $"{name}: the work RAM is not the farm's clean work RAM");
            if (f.Aram) t.Expect(p.Test(HkAramHash) == m.AramClean && m.AramClean != m.Aram0, $"{name}: the audio RAM is not the farm's clean audio RAM");
            t.Expect(p.Test(HkCorFrames) == m.FarmFrames, $"{name}: the game is at frame {p.Test(HkCorFrames)}, expected {m.FarmFrames}");
            ClickControl(t, p, IdCorClose, name + ": Close");
            p.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); }
    }

    private void CorrWindowFlow(T t, CorrFamily f, CorrMade m)
    {
        string name = f.Name;
        byte[] project;
        long snap, work, aram, whole, frames, farm;
        bool state;
        using (var p = SegaGame(m.Rom, f.Console, f.Chip))
        {
            p.Test(HkRun, 0, 25);
            state = p.Test(89) == 1;                      // whether this console's game keeps its whole machine in a project (all but the SNES, until the snes-state work merges)
            long cleanWork = p.Test(HkWramHash);
            t.Expect(cleanWork != 0 && cleanWork != m.Work0, $"{name}: the game that is loaded already looks like the corrupted one");
            nint parent = Win32Host.CreateParent();
            try
            {
                p.ShowEditor(parent);
                for (int k = 0; k < 3; k++) Win32Host.Pump(40);
                var hwnd = p.EditorHandle;
                t.Expect(p.Test(30, IdCor) != 0, $"{name}: there is no Corruption button");
                if (!ClickControl(t, p, IdCor, name + ": Corruption button")) return;
                t.Expect(p.Test(EdCorOpen) == 1, $"{name}: the Corruption window did not open");
                t.Expect(p.Test(30, 4) == 0 && p.Test(30, 80) == 0, $"{name}: controls behind the Corruption window are still reachable");
                t.Expect(p.Test(HkCorCount) == -1, $"{name}: a stockpile is open before one was chosen");
                var shot = Win32Host.CapturePixels(hwnd, out int w, out int h);
                t.Expect(InkPixels(shot, w, 100, 222, 700, 110) > 300, $"{name}: the empty window shows no text telling the user what to do");

                // Start with nothing chosen says so
                ClickControl(t, p, IdCorStart, name + ": Start with no stockpile");
                t.Expect(p.Test(HkCorGood) == 0 && CorText(p, 0).Contains("pick a corruption"), $"{name}: Start with no stockpile said '{CorText(p, 0)}'");

                // open the stockpile through the Open button
                CorPick(p, m.Stock);
                ClickControl(t, p, IdCorOpenBtn, name + ": Open stockpile");
                t.Expect(CorWait(() => CorIdle(p)), $"{name}: the stockpile was never read");
                t.Expect(p.Test(HkCorCount) == 3, $"{name}: the list has {p.Test(HkCorCount)} corruptions, expected 3 ({CorText(p, 0)})");
                t.Expect(CorText(p, 0).StartsWith("opened ") && p.Test(HkCorGood) == 1, $"{name}: opening said '{CorText(p, 0)}'");
                Win32Host.Pump(60);
                t.Expect(p.Test(EdCorRows) == 3 && p.Test(30, IdCorRow) != 0 && p.Test(30, IdCorRow + 2) != 0, $"{name}: the three rows are not on screen ({p.Test(EdCorRows)} rows)");
                long fit0 = p.Test(HkCorRow, 0), fit1 = p.Test(HkCorRow, 1), fit2 = p.Test(HkCorRow, 2);
                t.Expect(fit0 == 1, $"{name}: the entry made on this game should fit (flags {fit0:X})");
                t.Expect(fit1 == 0x100, $"{name}: an entry made on another game should say so (flags {fit1:X})");
                t.Expect(fit2 == 0x200, $"{name}: an entry made on another console should say so (flags {fit2:X})");

                // the two that do not fit are refused with their reason on screen, and the game is not touched
                long beforeRefusals = p.Test(HkWramHash);
                foreach (var (row, rowName, words) in new[] { (1, "made on another game", "not the game that is loaded"), (2, "made on another console", "made for the") })
                {
                    ClickControl(t, p, IdCorRow + row, $"{name}: row {row}");
                    t.Expect(CorText(p, 3) == rowName, $"{name}: clicking row {row} selected '{CorText(p, 3)}'");
                    t.Expect(CorText(p, 2).Contains(words), $"{name}: the list says '{CorText(p, 2)}' about row {row}");
                    ClickControl(t, p, IdCorStart, $"{name}: Start on row {row}");
                    Win32Host.Pump(60);
                    t.Expect(CorIdle(p) && p.Test(HkCorGood) == 0 && CorText(p, 0).StartsWith("cannot start") && CorText(p, 0).Contains(words), $"{name}: starting row {row} said '{CorText(p, 0)}'");
                    t.Expect(p.Test(HkCorApplied) == 0 && p.Test(HkWramHash) == beforeRefusals, $"{name}: a refused corruption changed the game");
                }

                // the one that fits
                ClickControl(t, p, IdCorRow, $"{name}: row 0");
                t.Expect(CorText(p, 3) == m.Entry.Name && CorText(p, 2) == "", $"{name}: the fitting row reads '{CorText(p, 3)}' / '{CorText(p, 2)}'");
                ClickControl(t, p, IdCorStart, $"{name}: Start");
                t.Expect(CorWait(() => CorIdle(p), 90000), $"{name}: the start never finished ({CorText(p, 1)})");
                string said = CorText(p, 0);
                t.Expect(p.Test(HkCorGood) == 1 && said.StartsWith("started"), $"{name}: starting said '{said}'");
                t.Expect(p.Test(HkCorApplied) == 1, $"{name}: the plugin does not record a started corruption");
                t.Expect(said.Contains(m.Entry.Anchor.Kind == AnchorKind.Boot ? $"replayed {m.FarmFrames:N0} frames" : "loaded its saved machine"), $"{name}: the start said '{said}'");

                // the machine the plugin built is the machine the farm had
                t.Expect(p.Test(HkWramHash) == m.Work0, $"{name}: after the start the work RAM is not the farm's corrupted work RAM");
                if (f.Aram) t.Expect(p.Test(HkAramHash) == m.Aram0 && m.Aram0 != m.AramClean, $"{name}: after the start the audio RAM is not the farm's corrupted audio RAM");
                t.Expect(p.Test(HkCorFrames) == m.FarmFrames && p.Test(HkCorPoisoned) == 0, $"{name}: the plugin's game is at frame {p.Test(HkCorFrames)} (the farm: {m.FarmFrames}), poisoned {p.Test(HkCorPoisoned)}");
                t.Expect(p.Test(11) == m.FarmFrames, $"{name}: the game reports {p.Test(11)} frames run, expected {m.FarmFrames}");
                CorPlay(p, f.After);
                t.Expect(p.Test(HkWramHash) == m.WorkEnd, $"{name}: {f.After} frames later the work RAM differs from the farm's own game");
                if (f.Aram) t.Expect(p.Test(HkAramHash) == m.AramEnd, $"{name}: {f.After} frames later the audio RAM differs from the farm's own game");

                // closing the window leaves the result on the picture for a while, in words and pixels
                ClickControl(t, p, IdCorClose, $"{name}: Close");
                t.Expect(p.Test(EdCorOpen) == 0, $"{name}: the Corruption window did not close");
                Win32Host.Pump(80);
                t.Expect(p.Test(EdCorNote) == 1, $"{name}: the result is not shown over the picture");
                shot = Win32Host.CapturePixels(hwnd, out w, out h);
                t.Expect(InkPixels(shot, w, 40, 72, 470, 40) > 200, $"{name}: the note over the picture has no text");

                // save with Instrument Runaway on
                p.Test(47, 0, 1);
                t.Expect(p.Test(45) == 1, $"{name}: Runaway did not start on the corrupted game");
                snap = p.Test(HkSnapHash); work = p.Test(HkWramHash); aram = p.Test(HkAramHash); whole = p.Test(84); frames = p.Test(11); farm = p.Test(HkCorFrames);
                project = p.SaveState();
                p.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }
        }
        int crp = FindSection(project, "CRP1"), gam = FindSection(project, "GAM1");
        t.Expect(crp >= 0, $"{name}: the project has no corruption section");
        if (crp < 0) return;
        t.Expect(!state || gam >= 0, $"{name}: the project has no game-state section");
        if (f.Aram) t.Expect(snap != 0 && snap == m.AramEnd, $"{name}: the instrument snapshot is not the corrupted audio RAM");

        // the game that never left: the same start and frames, Runaway switched on and off where the project was saved, then the same frames again
        long workCtl, aramCtl, wholeCtl, framesCtl;
        using (var c = SegaGame(m.Rom, f.Console, f.Chip))
        {
            c.Test(HkRun, 0, 25);
            CorOpenDirect(c, m.Stock);
            CorWait(() => CorIdle(c));
            c.Test(HkCorSelect, 0, 0);
            t.Expect(c.Test(HkCorStart) == 0 && CorWait(() => CorIdle(c), 90000) && c.Test(HkCorGood) == 1, $"{name}: the control's start said '{CorText(c, 0)}'");
            CorPlay(c, f.After);
            c.Test(47, 0, 1); c.Test(47, 0, 0);
            CorPlay(c, f.After);
            workCtl = c.Test(HkWramHash); aramCtl = c.Test(HkAramHash); wholeCtl = c.Test(84); framesCtl = c.Test(11);
            t.Expect(workCtl != work, $"{name}: the game did not move on after Runaway was released (so the comparison below proves nothing)");
        }

        // the project comes back
        using (var q = Reopen(project))
        {
            t.Expect(q.Test(HkCorApplied) == 1, $"{name}: the reopened project does not know a corruption was started");
            t.Expect(q.Test(45) == 1, $"{name}: the project was saved in Runaway and came back with it {q.Test(45)}");
            t.Expect(q.Test(HkWramHash) == work && (!f.Aram || q.Test(HkAramHash) == aram), $"{name}: the machine that came back is not the machine that was saved");
            t.Expect(q.Test(11) == frames && (state || q.Test(HkCorFrames) == farm), $"{name}: the game is at frame {q.Test(11)} (farm {q.Test(HkCorFrames)}), it was saved at {frames} (farm {farm})");
            if (state) t.Expect(q.Test(84) == whole, $"{name}: the whole machine that came back is not the saved one");
            if (f.Aram) t.Expect(q.Test(HkSnapHash) == snap, $"{name}: the instruments that came back are not the saved ones");
            // a machine put back from a saved state cannot be described as a replay from power-on (the farm says so); a game rebuilt by replaying can
            t.Expect(q.Test(HkCorPoisoned) == (state ? 1 : 0), $"{name}: the reopened game's replay record is {(q.Test(HkCorPoisoned) == 1 ? "marked unusable" : "usable")}, expected {(state ? "unusable" : "usable")}");
            q.Test(47, 0, 0);
            CorPlay(q, f.After);
            t.Expect(q.Test(11) == framesCtl, $"{name}: {q.Test(11)} frames after the same run, the game that never left has {framesCtl}");
            t.Expect(q.Test(HkWramHash) == workCtl, $"{name}: after the same frames the work RAM differs from the game that never left");
            if (f.Aram) t.Expect(q.Test(HkAramHash) == aramCtl, $"{name}: after the same frames the audio RAM differs from the game that never left");
            if (state) t.Expect(q.Test(84) == wholeCtl, $"{name}: after the same frames the machine differs from the game that never left");
        }
        t.Note($"{name}: the project is {project.Length:N0} bytes, the corruption section {BitConverter.ToInt32(project, crp + 4):N0} of them" + (gam >= 0 ? $", the game-state section {BitConverter.ToInt32(project, gam + 4):N0}" : ""));
    }

    // ------------------------------------------------------------------------------------------------
    // the SNES: the instrument itself

    /// <summary>A note played through Instrument Runaway's picker (the first instrument of the game), on an instance already in Runaway.</summary>
    private float[] CorNote(PluginInstance p)
    {
        var m = new Mixer { SampleRate = 48000 };
        var audio = Mono(m.Run(p, One(60, 0, 0.5)));
        return audio[(m.Log[0].StartSample + 6000)..(m.Log[0].EndSample - 3000)];
    }

    private static double CorHarmonics(float[] x, double hz) => (Goertzel(x, 48000, hz * 3, 0, x.Length) + Goertzel(x, 48000, hz * 5, 0, x.Length)) / Math.Max(1e-9, Goertzel(x, 48000, hz, 0, x.Length));

    private void CorrSnesInstruments(T t, CorrFamily f, CorrMade m)
    {
        // the clean game in Instrument Runaway: the game's own instrument
        float[] clean;
        long cleanSnap;
        using (var c = SegaGame(m.Rom, 2, "SNES"))
        {
            c.Test(HkRun, 0, m.FarmFrames);
            c.Test(47, 0, 1);
            t.Expect(c.Test(48) == 1, "the clean SNES game did not enter sampler mode");
            cleanSnap = c.Test(HkSnapHash);
            clean = CorNote(c);
        }
        t.Expect(cleanSnap != 0, "the clean game's instrument snapshot is empty");
        double cleanRms = Audio.Rms(clean, 0, clean.Length), cleanFreq = Audio.Frequency(clean, 48000, 0, clean.Length);
        t.Expect(cleanRms > 0.005, $"the clean game's instrument is silent (rms {cleanRms:0.0000})");

        // the corruption started from the stockpile with "freeze" ticked: Instrument Runaway is on the moment the corrupted game exists, so the instruments captured are the corrupted ones
        byte[] project;
        long snap;
        double rms;
        using (var p = SegaGame(m.Rom, 2, "SNES"))
        {
            p.Test(HkRun, 0, 25);
            nint parent = Win32Host.CreateParent();
            try
            {
                p.ShowEditor(parent);
                for (int k = 0; k < 3; k++) Win32Host.Pump(40);
                ClickControl(t, p, IdCor, "SNES instruments: Corruption button");
                CorPick(p, m.Stock);
                ClickControl(t, p, IdCorOpenBtn, "SNES instruments: Open stockpile");
                t.Expect(CorWait(() => CorIdle(p)) && p.Test(HkCorCount) == 3, "SNES instruments: the stockpile did not open");
                ClickControl(t, p, IdCorRow, "SNES instruments: row 0");
                p.Test(122, 0, 0);                                      // round 6: play for 0 s = freeze at once, the old behaviour this check is about (the recipes check covers playing first)
                ClickControl(t, p, IdCorFreeze, "SNES instruments: freeze");
                ClickControl(t, p, IdCorStart, "SNES instruments: Start");
                t.Expect(CorWait(() => CorIdle(p), 90000), "SNES instruments: the start never finished");
                t.Expect(p.Test(HkCorGood) == 1 && CorText(p, 0).Contains("Instrument Runaway is on"), $"SNES instruments: starting with freeze said '{CorText(p, 0)}'");
                t.Expect(p.Test(45) == 1 && p.Test(48) == 1, $"SNES instruments: Runaway {p.Test(45)}, sampler mode {p.Test(48)}: the game should be frozen with its instruments live");
                snap = p.Test(HkSnapHash);
                t.Expect(snap == m.Aram0, "SNES instruments: the instrument snapshot is not the farm's corrupted audio RAM");
                t.Expect(snap != cleanSnap, "SNES instruments: the corrupted instrument snapshot equals the clean one");
                t.Expect(p.Test(HkCorFrames) == m.FarmFrames, $"SNES instruments: the frozen game is at frame {p.Test(HkCorFrames)}");
                ClickControl(t, p, IdCorClose, "SNES instruments: Close");
                p.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }

            // the sound: the same note through the same picker is another sound
            var corrupt = CorNote(p);
            rms = Audio.Rms(corrupt, 0, corrupt.Length);
            double freq = Audio.Frequency(corrupt, 48000, 0, corrupt.Length);
            double ratio = rms / Math.Max(1e-9, cleanRms);
            t.Expect(Math.Abs(ratio - 1) > 0.25, $"SNES instruments: the corrupted instrument sounds as loud as the clean one (rms {rms:0.0000} against {cleanRms:0.0000})");
            t.Expect(rms > 0.005 && corrupt.All(float.IsFinite), $"SNES instruments: the corrupted instrument is silent or not finite (rms {rms:0.0000})");
            t.Note($"SNES instrument: clean {cleanFreq:0.0} Hz at rms {cleanRms:0.0000}, corrupted {freq:0.0} Hz at rms {rms:0.0000} ({ratio:0.00}x); odd harmonics against the fundamental {CorHarmonics(clean, cleanFreq):0.000} -> {CorHarmonics(corrupt, freq):0.000}");
            project = p.SaveState();
        }

        // and it survives the project: the same instrument, the same sound, after closing and opening FL's project (the first instance is gone, so this is a new emulator that has to rebuild the game)
        t.Expect(FindSection(project, "SPC1") >= 0 && FindSection(project, "CRP1") >= 0, "SNES instruments: the project lacks its instrument or corruption section");
        using var q = Reopen(project);
        t.Expect(q.Test(45) == 1 && q.Test(48) == 1 && q.Test(HkCorApplied) == 1, $"SNES instruments: after reopening Runaway {q.Test(45)}, sampler {q.Test(48)}, corruption {q.Test(HkCorApplied)}");
        t.Expect(q.Test(HkSnapHash) == snap, "SNES instruments: the instruments that came back are not the corrupted ones");
        var again = CorNote(q);
        double rms2 = Audio.Rms(again, 0, again.Length);
        t.Expect(Math.Abs(rms2 - rms) < rms * 0.02, $"SNES instruments: the reopened instrument has rms {rms2:0.0000}, it was {rms:0.0000}");
    }

    /// <summary>The history of a SNES game that was corrupted: writes made after the start are replayed, a reset is a new game, and what cannot be kept is said.</summary>
    private void CorrSnesProject(T t, CorrFamily f, CorrMade m)
    {
        // a write made to the audio RAM after the start comes back at the same frame: through the game-state section when the console keeps its state, through the game's recorded history when it cannot
        string tag = "SNES " + (f.Replay ? "history" : "project");
        bool state;
        using (var probe = SegaGame(m.Rom, 2, "SNES")) state = probe.Test(89) == 1;
        t.Expect(state == !f.Replay, $"{tag}: the SNES reports {(state ? "a state" : "no state")} where {(f.Replay ? "none" : "one")} was expected");
        byte[] project;
        long aramBefore, framesBefore;
        using (var p = SegaGame(m.Rom, 2, "SNES"))
        {
            p.Test(HkRun, 0, 25);
            CorOpenDirect(p, m.Stock);
            t.Expect(CorWait(() => CorIdle(p)) && p.Test(HkCorCount) == 3, $"{tag}: the stockpile did not open");
            p.Test(HkCorSelect, 0, 0);
            t.Expect(p.Test(HkCorStart) == 0, $"{tag}: the start was refused");
            t.Expect(CorWait(() => CorIdle(p), 90000) && p.Test(HkCorGood) == 1, $"{tag}: the start said '{CorText(p, 0)}'");
            CorPlay(p, 20);
            t.Expect(p.Test(HkAramPoke, 0x0600, 0x77) == 1, $"{tag}: the audio RAM could not be written");
            CorPlay(p, 10);
            t.Expect(p.Test(HkAramPeek, 0x0600) == 0x77, $"{tag}: the write did not stay");
            aramBefore = p.Test(HkAramHash);
            framesBefore = p.Test(11);
            project = p.SaveState();
            t.Expect(framesBefore == m.FarmFrames + 30, $"{tag}: the game is at frame {framesBefore}, expected {m.FarmFrames + 30}");
            int g1 = FindSection(project, "GAM1");
            t.Expect(g1 >= 0 && BitConverter.ToInt32(project, g1 + 4) > (state ? 200 : 0), $"{tag}: the game-state section is missing or too small to hold a machine");
        }
        using (var q = Reopen(project))
        {
            t.Expect(q.Test(HkAramPeek, 0x0600) == 0x77, $"{tag}: the byte written after the start did not come back (it reads {q.Test(HkAramPeek, 0x0600)})");
            t.Expect(q.Test(HkAramHash) == aramBefore, $"{tag}: the audio RAM of the reopened game is not the saved one");
            t.Expect(q.Test(11) == framesBefore, $"{tag}: the reopened game is at frame {q.Test(11)}, it was saved at {framesBefore}");
            t.Expect(state || CorText(q, 0).Contains("rebuilt by replaying"), $"{tag}: the reopened project said '{CorText(q, 0)}'");
            t.Expect(q.Test(HkCorApplied) == 1, $"{tag}: the reopened project does not know a corruption was started");
            // Reset Console is a new game: the corruption is gone, and the plugin says it is not there
            q.Test(43);
            t.Expect(CorWait(() => q.Test(9) == 1) && q.Test(HkCorApplied) == 0, $"{tag}: after Reset Console the plugin still thinks a corruption is on (applied {q.Test(HkCorApplied)})");
            q.Test(HkRun, 0, 5);
            t.Expect(q.Test(HkAramPeek, 0x0600) != 0x77 && q.Test(HkAramHash) != aramBefore, $"{tag}: Reset Console kept the corrupted audio RAM");
        }

        // a console that keeps its state, whose state the project could not hold (too big): the game's recorded history carries it instead, and the reopened game is rebuilt by replaying it
        if (state)
        {
            byte[] big; long bigAram, bigFrames;
            try
            {
                using var pb = SegaGame(m.Rom, 2, "SNES");
                pb.Test(HkRun, 0, 25);
                CorOpenDirect(pb, m.Stock);
                CorWait(() => CorIdle(pb));
                pb.Test(HkCorSelect, 0, 0);
                pb.Test(HkCorStart);
                t.Expect(CorWait(() => CorIdle(pb), 90000) && pb.Test(HkCorGood) == 1, $"{tag}: the start for the too-big case said '{CorText(pb, 0)}'");
                CorPlay(pb, 20);
                pb.Test(HkAramPoke, 0x0610, 0x5C);
                CorPlay(pb, 10);
                bigAram = pb.Test(HkAramHash); bigFrames = pb.Test(11);
                pb.Test(87, 0, 300);                                       // the biggest game-state section a project may hold: smaller than this machine
                big = pb.SaveState();
                t.Expect(NoteOf(pb) is { } note && note.Contains("too big"), $"{tag}: saving a state too big for the project said '{NoteOf(pb)}'");
            }
            finally { using var reset = lib.Create(); reset.Test(87, 0, 0); }
            int bg = FindSection(big, "GAM1");
            t.Expect(bg >= 0 && BitConverter.ToInt32(big, bg + 4) < 400 && FindSection(big, "CRP1") >= 0, $"{tag}: the too-big project should hold a small marker section and the corruption section");
            using var qb = Reopen(big);
            t.Expect(CorText(qb, 0).Contains("rebuilt by replaying") && qb.Test(HkCorApplied) == 1, $"{tag}: a project whose machine was too big said '{CorText(qb, 0)}' (applied {qb.Test(HkCorApplied)})");
            t.Expect(qb.Test(HkAramHash) == bigAram && qb.Test(11) == bigFrames && qb.Test(HkAramPeek, 0x0610) == 0x5C, $"{tag}: the game rebuilt from its history is not the saved one (frame {qb.Test(11)}, saved {bigFrames})");
        }

        // damaged, cut-short and absurd corruption sections: the plugin says so and does not pretend
        byte[] ok;
        using (var p2 = SegaGame(m.Rom, 2, "SNES"))
        {
            p2.Test(HkRun, 0, 25);
            CorOpenDirect(p2, m.Stock);
            CorWait(() => CorIdle(p2));
            p2.Test(HkCorSelect, 0, 0);
            p2.Test(HkCorStart);
            CorWait(() => CorIdle(p2), 90000);
            p2.Test(HkAramPoke, 0x0600, 0x66);
            CorPlay(p2, 10);
            ok = p2.SaveState();
        }
        int crp = FindSection(ok, "CRP1");
        if (crp < 0) { t.Expect(false, $"{tag}: the project has no corruption section"); return; }
        int mid = crp + 8 + BitConverter.ToInt32(ok, crp + 4) / 2;
        var damaged = (byte[])ok.Clone();
        damaged[mid] ^= 0x5A;                                                    // one flipped byte inside the section (a checksum covers it)
        using (var d = Reopen(damaged))
        {
            string said = CorText(d, 0);
            t.Expect(d.Test(HkCorGood) == 0 && (said.Contains("corruption record") || said.Contains("could not be rebuilt")), $"{tag}: a damaged corruption section said '{said}'");
            t.Expect(d.Test(HkCorApplied) == 0, $"{tag}: a damaged corruption section still counts as a started corruption");
            // the machine itself: with a saved state it comes back from the game-state section whatever happened to the corruption record; with only the history it cannot
            t.Expect((d.Test(HkAramPeek, 0x0600) == 0x66) == state, $"{tag}: the machine {(state ? "should still come back from the game-state section" : "should not come back from a damaged history")} (byte {d.Test(HkAramPeek, 0x0600)})");
        }
        using (var tr = Reopen(ok[..mid]))                                       // cut off in the middle of the section
        {
            t.Expect(tr.Test(HkCorGood) == 0 && CorText(tr, 0).Length > 0 && tr.Test(HkCorApplied) == 0, $"{tag}: a cut-short corruption section said '{CorText(tr, 0)}'");
        }
        var huge = (byte[])ok.Clone();
        BitConverter.GetBytes(0x7FFFFFF0).CopyTo(huge, crp + 4);                 // a section that claims 2 GB
        using (var hg = Reopen(huge))
        {
            t.Expect(hg.Test(HkCorGood) == 0 && CorText(hg, 0).Length > 0 && hg.Test(HkCorApplied) == 0 && hg.Test(12) == 0, $"{tag}: a section that claims 2 GB said '{CorText(hg, 0)}'");
        }
        using (var old = Reopen(ok[..crp]))                                      // an older project (before this section existed) loads exactly as before
        {
            t.Expect(old.Test(HkCorApplied) == 0 && CorText(old, 0) == "", $"{tag}: a project without the section said '{CorText(old, 0)}'");
        }
    }

    /// <summary>The audio thread keeps rendering the game that was running while the new one is built, and takes the new one between frames: nothing it does allocates.</summary>
    private void CorrAllocation(T t, CorrFamily f, CorrMade m)
    {
        using var p = SegaGame(m.Rom, 2, "SNES");
        RenderBlocks(p, 0.5);
        CorOpenDirect(p, m.Stock);
        CorWait(() => CorIdle(p));
        p.Test(HkCorSelect, 0, 0);
        long alloc = 0; int blocks = 0;
        long Render(double seconds)
        {
            long a0 = AllocStart(p);
            RenderBlocks(p, seconds);
            blocks++;
            return p.Test(4) - a0;
        }
        t.Expect(p.Test(HkCorStart) == 0, "allocation: the start was refused");
        long begin = Environment.TickCount64;
        while (!CorIdle(p) && Environment.TickCount64 - begin < 90000) { alloc += Render(0.02); Win32Host.Pump(2); }
        t.Expect(p.Test(HkCorGood) == 1, $"allocation: the start said '{CorText(p, 0)}'");
        for (int i = 0; i < 40; i++) alloc += Render(0.1);
        t.Expect(p.Test(HkCorApplied) == 1 && p.Test(HkCorFrames) > m.FarmFrames, "allocation: the new game was not the one rendered afterwards");
        t.Expect(alloc == 0, $"{alloc:N0} bytes allocated by the plugin on the audio thread while a corruption was starting and its game ran (must be 0)");
        t.Note($"allocation on the audio thread while a corruption starts and then plays 4 s: {alloc} B over {blocks} render calls");
    }

    /// <summary>A stockpile longer than the window shows: the first corruption that fits is selected and scrolled into view, the wheel scrolls, a click picks the row under the mouse.</summary>
    private void CorrScrolling(T t, string dir, CorrFamily f, CorrMade m)
    {
        var sp = new Stockpile { Name = "long", Writer = "BrokenNes2 self-test" };
        for (int i = 0; i < 12; i++)
        {
            var e = new StockpileEntry { Name = $"entry {i + 1:00}", Game = m.Entry.Game, Machine = m.Entry.Machine, Anchor = m.Entry.Anchor, Writes = m.Entry.Writes.ToList() };
            if (i < 5) e = new StockpileEntry { Name = e.Name, Game = new GameRef { Console = e.Game.Console, Sha1 = new string('0', 40), Title = "Another Game" }, Machine = e.Machine, Anchor = e.Anchor, Writes = e.Writes };
            sp.Entries.Add(e);
        }
        string path = Path.Combine(dir, "long" + Stockpile.FileSuffix);
        StockpileJson.SaveFile(path, sp);
        using var p = SegaGame(m.Rom, 2, "SNES");
        p.Test(HkRun, 0, 10);
        nint parent = Win32Host.CreateParent();
        try
        {
            p.ShowEditor(parent);
            for (int k = 0; k < 3; k++) Win32Host.Pump(40);
            var hwnd = p.EditorHandle;
            ClickControl(t, p, IdCor, "scrolling: Corruption button");
            CorPick(p, path);
            ClickControl(t, p, IdCorOpenBtn, "scrolling: Open stockpile");
            t.Expect(CorWait(() => CorIdle(p)) && p.Test(HkCorCount) == 12, $"scrolling: the long stockpile did not open ({CorText(p, 0)})");
            Win32Host.Pump(80);
            t.Expect(CorText(p, 3) == "entry 06", $"scrolling: opening selected '{CorText(p, 3)}', expected the first one that fits, entry 06");
            t.Expect(p.Test(EdCorScroll) == 1, $"scrolling: the list starts at row {p.Test(EdCorScroll)}, expected 1 (so that entry 06 is the last one shown)");
            t.Expect(p.Test(30, IdCorRow + 4) != 0 && p.Test(30, IdCorRow + 5) == 0, "scrolling: the list should show five rows");
            Win32Host.Wheel(hwnd, -3, 300, 280);
            Win32Host.Pump(60);
            t.Expect(p.Test(EdCorScroll) == 4, $"scrolling: three notches of the wheel left the list at row {p.Test(EdCorScroll)}, expected 4");
            Win32Host.Wheel(hwnd, -30, 300, 280);
            Win32Host.Pump(60);
            t.Expect(p.Test(EdCorScroll) == 7, $"scrolling: the wheel went past the end of the list (row {p.Test(EdCorScroll)}, the last page starts at 7)");
            ClickControl(t, p, IdCorRow + 1, "scrolling: second visible row");
            t.Expect(CorText(p, 3) == "entry 09", $"scrolling: clicking the second visible row of the last page picked '{CorText(p, 3)}', expected entry 09");
            Win32Host.Wheel(hwnd, 30, 300, 280);
            Win32Host.Pump(60);
            t.Expect(p.Test(EdCorScroll) == 0, $"scrolling: the wheel went above the start of the list (row {p.Test(EdCorScroll)})");
            ClickControl(t, p, IdCorClose, "scrolling: Close");
            p.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); }
    }

    /// <summary>A long replay can be stopped from the window: the game that was running goes on, and the window says what happened.</summary>
    private void CorrCancel(T t, string dir, CorrFamily f, CorrMade m)
    {
        var slow = new StockpileEntry
        {
            Name = "a very long replay", Game = m.Entry.Game, Machine = m.Entry.Machine,
            Anchor = new Anchor { Kind = AnchorKind.Boot, Frames = 36000 }, Writes = m.Entry.Writes.ToList(),
        };
        var tooLong = new StockpileEntry
        {
            Name = "past the replay limit", Game = m.Entry.Game, Machine = m.Entry.Machine,
            Anchor = new Anchor { Kind = AnchorKind.Boot, Frames = 50000 }, Writes = m.Entry.Writes.ToList(),
        };
        var sp = new Stockpile { Name = "cancel", Writer = "BrokenNes2 self-test" };
        sp.Entries.Add(slow); sp.Entries.Add(tooLong);
        string path = Path.Combine(dir, "cancel" + Stockpile.FileSuffix);
        StockpileJson.SaveFile(path, sp);

        using var p = SegaGame(m.Rom, 2, "SNES");
        p.Test(HkRun, 0, 40);
        long framesBefore = p.Test(11), aramBefore = p.Test(HkAramHash);
        nint parent = Win32Host.CreateParent();
        try
        {
            p.ShowEditor(parent);
            for (int k = 0; k < 3; k++) Win32Host.Pump(40);
            ClickControl(t, p, IdCor, "cancel: Corruption button");
            CorPick(p, path);
            ClickControl(t, p, IdCorOpenBtn, "cancel: Open stockpile");
            t.Expect(CorWait(() => CorIdle(p)) && p.Test(HkCorCount) == 2, $"cancel: the stockpile did not open ({CorText(p, 0)})");

            // past the limit: refused before any work, with the limit in the message
            ClickControl(t, p, IdCorRow + 1, "cancel: row 1");
            ClickControl(t, p, IdCorStart, "cancel: Start past the limit");
            t.Expect(CorWait(() => CorIdle(p), 60000), "cancel: the over-long replay never finished");
            t.Expect(p.Test(HkCorGood) == 0 && CorText(p, 0).Contains("36,000"), $"cancel: a replay past the limit said '{CorText(p, 0)}'");
            t.Expect(p.Test(HkCorApplied) == 0, "cancel: a refused replay counts as started");

            // the longest allowed replay, stopped by the Cancel button
            ClickControl(t, p, IdCorRow, "cancel: row 0");
            ClickControl(t, p, IdCorStart, "cancel: Start the long replay");
            t.Expect(CorWait(() => p.Test(HkCorBusy) == 2 && CorText(p, 1).Contains("replaying"), 20000), $"cancel: no progress was shown while replaying (status '{CorText(p, 1)}', busy {p.Test(HkCorBusy)})");
            string progress = CorText(p, 1);
            t.Expect(p.Test(30, IdCorCancel) != 0, "cancel: the Cancel button is not on screen while a start is running");
            Win32Host.Pump(300);
            t.Expect(CorText(p, 1) != progress || CorText(p, 1).Contains("replaying"), "cancel: the progress does not move");
            ClickControl(t, p, IdCorCancel, "cancel: Cancel");
            t.Expect(CorWait(() => CorIdle(p), 30000), "cancel: the replay did not stop");
            t.Expect(p.Test(HkCorGood) == 0 && CorText(p, 0).Contains("stopped"), $"cancel: stopping said '{CorText(p, 0)}'");
            t.Expect(p.Test(HkCorApplied) == 0 && p.Test(11) == framesBefore && p.Test(HkAramHash) == aramBefore, "cancel: the game that was running was changed by a replay that was stopped");
            ClickControl(t, p, IdCorClose, "cancel: Close");
            p.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); }
    }

    /// <summary>Everything that cannot work says so, in the window and over the picture.</summary>
    private void CorrMessages(T t, string dir, CorrFamily f, CorrMade m)
    {
        // files that are not stockpiles
        string garbage = Path.Combine(dir, "garbage.json"), truncated = Path.Combine(dir, "truncated.json"), wrong = Path.Combine(dir, "wrong.json"), future = Path.Combine(dir, "future.json"), empty = Path.Combine(dir, "empty.json");
        var rng = new Random(7); var junk = new byte[4096]; rng.NextBytes(junk);
        File.WriteAllBytes(garbage, junk);
        string good = File.ReadAllText(m.Stock);
        File.WriteAllText(truncated, good[..(good.Length / 2)]);
        File.WriteAllText(wrong, "{\"format\":\"something-else\",\"version\":2,\"entries\":[]}");
        string newer = System.Text.RegularExpressions.Regex.Replace(good, "\"version\"\\s*:\\s*2", "\"version\": 99");
        t.Expect(newer != good, "messages: the test could not make a newer stockpile");
        File.WriteAllText(future, newer);
        File.WriteAllBytes(empty, []);
        using (var p = SegaGame(m.Rom, 2, "SNES"))
        {
            nint parent = Win32Host.CreateParent();
            try
            {
                p.ShowEditor(parent);
                for (int k = 0; k < 3; k++) Win32Host.Pump(40);
                ClickControl(t, p, IdCor, "messages: Corruption button");
                foreach (var (label, path) in new[] { ("a file that is not there", Path.Combine(dir, "not_there.json")), ("random bytes", garbage), ("a cut-off stockpile", truncated), ("another JSON format", wrong), ("a newer stockpile version", future), ("an empty file", empty) })
                {
                    CorPick(p, path);
                    ClickControl(t, p, IdCorOpenBtn, "messages: Open " + label);
                    t.Expect(CorWait(() => CorIdle(p)), $"messages: opening {label} never finished");
                    string said = CorText(p, 0);
                    t.Expect(p.Test(HkCorGood) == 0 && said.StartsWith("could not open") && said.Length > 30, $"messages: {label} said '{said}'");
                    t.Expect(p.Test(HkCorCount) == -1, $"messages: {label} left a list of {p.Test(HkCorCount)} corruptions");
                    var shot = Win32Host.CapturePixels(p.EditorHandle, out int w, out int h);
                    t.Expect(BrightPixels(shot, w, 94, 588, 700, 44) > 120, $"messages: {label}: the window shows no explanation");
                    ClickControl(t, p, IdCorStart, "messages: Start after " + label);
                    t.Expect(CorText(p, 0).StartsWith("cannot start"), $"messages: Start after {label} said '{CorText(p, 0)}'");
                }
                // a good file after the bad ones works
                CorPick(p, m.Stock);
                ClickControl(t, p, IdCorOpenBtn, "messages: Open the good file");
                t.Expect(CorWait(() => CorIdle(p)) && p.Test(HkCorCount) == 3, "messages: the good stockpile did not open after the bad ones");
                // a machine that is not this emulator's
                ClickControl(t, p, IdCorClose, "messages: Close");
                p.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }
        }

        // an entry made with another sound unit is refused, never swapped in
        var odd = new StockpileEntry
        {
            Name = "made with another sound unit", Game = m.Entry.Game, Anchor = m.Entry.Anchor, Writes = m.Entry.Writes.ToList(),
            Machine = new MachineSpec { Cpu = m.Entry.Machine.Cpu, Ppu = m.Entry.Machine.Ppu, Apu = "APU_ELSEWHERE" },
        };
        var oddFile = new Stockpile { Name = "odd", Writer = "self-test" };
        oddFile.Entries.Add(odd);
        string oddPath = Path.Combine(dir, "odd" + Stockpile.FileSuffix);
        StockpileJson.SaveFile(oddPath, oddFile);
        using (var p = SegaGame(m.Rom, 2, "SNES"))
        {
            p.Test(HkRun, 0, 10);
            CorOpenDirect(p, oddPath);
            CorWait(() => CorIdle(p));
            t.Expect(p.Test(HkCorRow, 0) == 0x400, $"messages: an entry for another sound unit has flags {p.Test(HkCorRow, 0):X}");
            p.Test(HkCorSelect, 0, 0);
            t.Expect(CorText(p, 2).Contains("sound unit") && p.Test(HkCorStart) == 1 && CorText(p, 0).Contains("sound unit"), $"messages: another sound unit said '{CorText(p, 2)}' / '{CorText(p, 0)}'");
        }

        // Direct mode: no game to start it on
        using (var d = lib.Create())
        {
            CorOpenDirect(d, m.Stock);
            CorWait(() => CorIdle(d));
            t.Expect(d.Test(HkCorRow, 0) == 0x300 && CorText(d, 2).Contains("ROM mode"), $"messages: in Direct mode the entry has flags {d.Test(HkCorRow, 0):X} and says '{CorText(d, 2)}'");
            d.Test(HkCorSelect, 0, 0);
            t.Expect(d.Test(HkCorStart) == 1 && d.Test(HkCorApplied) == 0, "messages: a corruption started in Direct mode");
        }

        // a NES game: the plugin starts corruptions on the console games and says so
        using (var n = RomPlugin())
        {
            CorOpenDirect(n, m.Stock);
            CorWait(() => CorIdle(n));
            t.Expect(n.Test(HkCorRow, 0) == 0x300 && CorText(n, 2).Contains("NES"), $"messages: on a NES game the entry has flags {n.Test(HkCorRow, 0):X} and says '{CorText(n, 2)}'");
        }

        // a game of another console is loaded: the entry is for the SNES
        string gb = Path.Combine(dir, "other.gb");
        File.WriteAllBytes(gb, MakeGbRom(counter: true));
        using (var g = SegaGame(gb, 1, "DMG"))
        {
            CorOpenDirect(g, m.Stock);
            CorWait(() => CorIdle(g));
            g.Test(HkCorSelect, 0, 0);
            long flags = g.Test(HkCorRow, 0);
            t.Expect((flags & 1) == 0 && (flags >> 8) == 2 && CorText(g, 2).Contains("made for the"), $"messages: a Game Boy game against a SNES entry: flags {flags:X}, '{CorText(g, 2)}'");
            t.Expect(g.Test(HkCorStart) == 1 && g.Test(HkCorApplied) == 0, "messages: a SNES corruption started on a Game Boy game");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // the Game Boy's wave RAM: an instrument of a state console

    private void CorrGbWave(T t, string dir)
    {
        string rom = Path.Combine(dir, "tone.gb");
        var romBytes = MakeGbRom(tone: true);
        File.WriteAllBytes(rom, romBytes);
        // a triangle where the game put a square
        var wave = new CorruptionWrite[16];
        for (int i = 0; i < 16; i++)
        {
            int Nib(int k) => k < 16 ? k : 31 - k;
            wave[i] = new CorruptionWrite("WAVE", i, (byte)((Nib(2 * i) << 4) | Nib(2 * i + 1)));
        }
        string stock = Path.Combine(dir, "wave" + Stockpile.FileSuffix);
        using (var origin = new CorrRig(ConsoleKind.GameBoy, romBytes, "tone.gb"))
        {
            origin.Run(60);
            var cap = origin.Farm.Capture();
            t.Expect(cap.Anchor != null, $"Game Boy wave: the farm cannot say where the game is ({cap.Reason})");
            if (cap.Anchor == null) return;
            var sp = new Stockpile { Name = "wave", Writer = "BrokenNes2 self-test" };
            sp.Entries.Add(new StockpileEntry { Name = "a triangle wave RAM", Game = origin.Farm.Game(), Machine = origin.Spec, Anchor = cap.Anchor, Writes = wave.ToList() });
            StockpileJson.SaveFile(stock, sp);
        }

        float[] Wave(bool corrupt)
        {
            using var p = SegaGame(rom, 1, "DMG");
            p.Test(HkRun, 0, 60);
            if (corrupt)
            {
                CorOpenDirect(p, stock);
                CorWait(() => CorIdle(p));
                p.Test(HkCorSelect, 0, 0);
                p.Test(HkCorFreeze, 0, 1); p.Test(122, 0, 0);
                t.Expect(p.Test(HkCorStart) == 0, "Game Boy wave: the start was refused");
                t.Expect(CorWait(() => CorIdle(p), 60000) && p.Test(HkCorGood) == 1, $"Game Boy wave: the start said '{CorText(p, 0)}'");
                t.Expect(p.Test(45) == 1 && p.Test(50) == 1, $"Game Boy wave: Runaway {p.Test(45)}, Game Boy sampler {p.Test(50)}");
            }
            else p.Test(47, 0, 1);
            p.SetParam(PChannel, 3);                                       // the wave channel plays the game's own wave RAM
            var m = new Mixer { SampleRate = 48000 };
            var audio = Mono(m.Run(p, One(69, 0, 0.5)));
            return audio[(m.Log[0].StartSample + 6000)..(m.Log[0].EndSample - 3000)];
        }
        var clean = Wave(false);
        var corrupt = Wave(true);
        double cleanH = CorHarmonics(clean, 440), corruptH = CorHarmonics(corrupt, 440);
        t.Expect(Audio.Rms(clean, 0, clean.Length) > 0.005 && Audio.Rms(corrupt, 0, corrupt.Length) > 0.005, "Game Boy wave: the wave channel is silent");
        t.Expect(cleanH > corruptH * 2, $"Game Boy wave: the corrupted wave RAM should be a softer, triangle sound (odd harmonics against the fundamental: clean {cleanH:0.000}, corrupted {corruptH:0.000})");
        t.Note($"Game Boy wave channel: the game's square wave has odd harmonics at {cleanH:0.000} of the fundamental, the corrupted triangle at {corruptH:0.000}");
    }

    // ------------------------------------------------------------------------------------------------
    // a real game, when one is named (nothing of it is in the repository): Super Mario World's instruments, corrupted in a stockpile, taken over in the plugin

    private void CorrRealSmw(T t)
    {
        string? path = Environment.GetEnvironmentVariable("BROKENNES_TEST_SMW");
        if (string.IsNullOrEmpty(path)) path = @"C:\Users\philt\OneDrive\Documents\PROJECTS\!!! Experiments\AI SMW Hacker\smw.smc";
        if (!File.Exists(path)) { t.Note("(no Super Mario World ROM at " + path + " and BROKENNES_TEST_SMW is not set: the real-game corruption check was skipped)"); return; }
        string dir = Path.Combine(Path.GetTempPath(), "bn2_smw_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var rom = RomDetect.Unwrap(File.ReadAllBytes(path), path, out _);
            using var origin = new CorrRig(ConsoleKind.Snes, rom, Path.GetFileName(path));
            origin.Run(900);                                                         // the title screen, music playing
            var cap = origin.Farm.Capture();
            t.Expect(cap.Anchor != null, $"SMW: the farm cannot say where the game is ({cap.Reason})");
            if (cap.Anchor == null) return;
            var domains = origin.Farm.Domains();
            var aramDomain = MachineDomains.Find(domains, "ARAM")!; var dspDomain = MachineDomains.Find(domains, "DSP")!;
            var aramBytes = new byte[SpcSnapshot.AramSize]; for (int i = 0; i < aramBytes.Length; i++) aramBytes[i] = aramDomain.Peek(i);
            var dspBytes = new byte[SpcSnapshot.DspSize]; for (int i = 0; i < dspBytes.Length; i++) dspBytes[i] = dspDomain.Peek(i);
            var list = new SpcSnapshot(aramBytes, dspBytes).Instruments();
            t.Expect(list.Count > 0, "SMW: the instrument list of the title screen is empty");
            if (list.Count == 0) return;
            var pick = list.OrderByDescending(i => i.Sounding).ThenByDescending(i => i.Blocks).First();
            var writes = new List<CorruptionWrite>();
            for (int b = 1; b < Math.Min(pick.Blocks, 40); b++)
                for (int k = 1; k <= 8; k++)
                {
                    int a = pick.Start + b * 9 + k;
                    writes.Add(new CorruptionWrite("ARAM", a, (byte)(aramBytes[a] ^ 0xFF), aramBytes[a]));
                }
            long aramClean = origin.Hash("ARAM");
            CorruptionApplier.Apply(domains, writes, null, 0);
            origin.Farm.NoteWrites(writes);
            long aramCorrupt = origin.Hash("ARAM"), wramAtStart = origin.Hash("WRAM");
            var sp = new Stockpile { Name = "smw", Writer = "BrokenNes2 self-test" };
            sp.Entries.Add(new StockpileEntry { Name = "SMW instrument", Game = origin.Farm.Game(), Machine = origin.Spec, Anchor = cap.Anchor, Writes = writes });
            string stock = Path.Combine(dir, "smw" + Stockpile.FileSuffix);
            StockpileJson.SaveFile(stock, sp);

            // the clean game in Instrument Runaway, and the corrupted one started from the stockpile with the freeze tick
            float[] clean; long cleanSnap;
            using (var c = SegaGame(path, 2, "SNES"))
            {
                c.Test(HkRun, 0, 900);
                c.Test(47, 0, 1);
                cleanSnap = c.Test(HkSnapHash);
                c.SetParam(PInstrument, pick.Slot);
                clean = CorNote(c);
            }
            t.Expect(cleanSnap == aramClean, "SMW: the plugin's clean game at frame 900 is not the farm's clean game (audio RAM differs)");
            byte[] project; long snap, aram, wram, farm;
            float[] corrupt; double secs;
            using (var p = SegaGame(path, 2, "SNES"))
            {
                CorOpenDirect(p, stock);
                t.Expect(CorWait(() => CorIdle(p)) && p.Test(HkCorCount) == 1, $"SMW: the stockpile did not open ({CorText(p, 0)})");
                p.Test(HkCorSelect, 0, 0); p.Test(HkCorFreeze, 0, 1); p.Test(122, 0, 0);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                t.Expect(p.Test(HkCorStart) == 0, $"SMW: the start was refused ({CorText(p, 0)})");
                t.Expect(CorWait(() => CorIdle(p), 180000) && p.Test(HkCorGood) == 1, $"SMW: the start said '{CorText(p, 0)}'");
                secs = sw.Elapsed.TotalSeconds;
                t.Expect(p.Test(45) == 1 && p.Test(48) == 1, $"SMW: Runaway {p.Test(45)}, sampler {p.Test(48)}");
                snap = p.Test(HkSnapHash); aram = p.Test(HkAramHash); wram = p.Test(HkWramHash); farm = p.Test(HkCorFrames);
                t.Expect(snap == aramCorrupt && snap != cleanSnap, "SMW: the plugin's instrument snapshot is not the farm's corrupted audio RAM");
                t.Expect(wram == wramAtStart && farm == 900 && p.Test(11) == 900, $"SMW: the work RAM or the frame count (900) differ from the farm's: frame {farm} / {p.Test(11)}");
                p.SetParam(PInstrument, pick.Slot);
                corrupt = CorNote(p);
                project = p.SaveState();
            }
            double rmsClean = Audio.Rms(clean, 0, clean.Length), rmsBad = Audio.Rms(corrupt, 0, corrupt.Length);
            double corr = Math.Abs(Correlation(clean, corrupt, 0));
            t.Expect(rmsClean > 0.001 && rmsBad > 0.001 && corrupt.All(float.IsFinite), $"SMW: an instrument is silent or not finite (rms {rmsClean:0.0000} / {rmsBad:0.0000})");
            t.Expect(corr < 0.9, $"SMW: the corrupted instrument (slot {pick.Slot}) sounds like the clean one (correlation {corr:0.000})");
            t.Note($"SMW (real game): slot {pick.Slot} at ${pick.Start:X4}, {pick.Blocks} blocks, {writes.Count} bytes inverted; the clean note has rms {rmsClean:0.0000}, the corrupted {rmsBad:0.0000}, correlation {corr:0.000}; starting replayed 900 frames in {secs:0.0} s; the project is {project.Length:N0} bytes");

            // the project: a fresh plugin rebuilds the corrupted game by replaying its history and has the same instruments and machine
            using var q = Reopen(project);
            t.Expect(q.Test(HkCorApplied) == 1 && q.Test(45) == 1, $"SMW: after reopening the corruption is {q.Test(HkCorApplied)}, Runaway {q.Test(45)}");
            t.Expect(q.Test(HkSnapHash) == snap && q.Test(HkAramHash) == aram && q.Test(HkWramHash) == wram && q.Test(11) == 900, $"SMW: the game that came back from the project is not the corrupted game that was saved (frame {q.Test(11)})");
            q.SetParam(PInstrument, pick.Slot);
            var again = CorNote(q);
            t.Expect(Math.Abs(Audio.Rms(again, 0, again.Length) - rmsBad) < rmsBad * 0.02, "SMW: the reopened corrupted instrument does not sound as it did");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
