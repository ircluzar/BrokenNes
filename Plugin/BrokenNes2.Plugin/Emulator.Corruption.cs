// Corruption on an emulator (see Corruption.cs for the data and the project section): open a stockpile, say which of its corruptions fit the loaded game and what starting each costs, start one, take it away
// again, and keep it through a saved project.
//
// Starting builds a NEW game on a worker thread, as loading a game does: a saved state is loaded into a fresh machine, or the game is replayed from power-on with the pads it was handed and the corruption
// writes it received, and the writes of the entry land at the end. The game that was running goes on until the new one is ready (progress is shown, and Cancel stops it); the new game replaces it between
// frames (ApplyPending), and with "freeze at once" Instrument Runaway is switched on at that moment, so the instruments captured are exactly the corrupted ones. An entry whose anchor is "none" is applied
// to the running game, between frames. Nothing here runs on the audio thread except the recording of the pad (an array write).
using BrokenNes.Fruity;
using NesEmulator.Corruption;
using NesEmulator.Systems;

namespace BrokenNes2;

public sealed partial class Emulator
{
    internal CorruptionPanel Corruption { get; } = new();
    private volatile byte[]? carriedCorruption;      // a project's corruption section while its game is not yet the running one: a save made now writes it back as it came

    // ---- the stockpile file ----

    /// <summary>Reads a stockpile file (on a worker thread; the window shows "reading..." and then the list, or why not). Never throws.</summary>
    internal void OpenStockpile(string path)
    {
        Corruption.Reading = true;
        Task.Run(() =>
        {
            try
            {
                var r = StockpileJson.LoadFile(path);
                var problems = new List<string>(r.Refusals);
                problems.AddRange(r.Warnings);
                Corruption.View = new StockpileView(r.Stockpile, path, problems, r.Legacy);
                // the first corruption that fits the loaded game is the one selected
                var fits = CorruptionRows();
                Corruption.Selected = r.Stockpile is { Entries.Count: > 0 } ? Math.Max(0, fits.ToList().FindIndex(x => x.CanStart)) : -1;
                string name = System.IO.Path.GetFileName(path);
                if (r.Stockpile == null) Corruption.SetResult($"could not open {name}: {string.Join("; ", r.Refusals)}", false);
                else Corruption.SetResult($"opened {name}: {r.Stockpile.Entries.Count} corruption{(r.Stockpile.Entries.Count == 1 ? "" : "s")}" + (r.Refusals.Count > 0 ? $"; {r.Refusals.Count} refused: {r.Refusals[0]}" : ""), r.Refusals.Count == 0);
            }
            catch (Exception e)
            {
                Diag.Log("opening a stockpile failed: " + e);
                Corruption.SetResult($"could not open {System.IO.Path.GetFileName(path)}: {e.Message}", false);
            }
            finally { Corruption.Reading = false; }
        });
    }

    /// <summary>Closes the open stockpile (the started corruption, if any, stays).</summary>
    internal void CloseStockpile() { Corruption.View = null; Corruption.Selected = -1; }

    // ---- which corruptions fit ----

    /// <summary>The plugin's own machine for a console: the CPU and the sound unit its Sound chip setting gives (a stockpile entry made on another sound unit is refused, never swapped in).</summary>
    private (string cpu, string apu) OwnMachine(ConsoleKind kind) => (CoreCatalog.Default(kind, CoreSlot.Cpu), SessionGame.ApuFor(kind, Cx.Chips[Math.Clamp(Chip, 0, Cx.Chips.Count - 1)]));

    /// <summary>The list for the window: every entry of the open stockpile against the game that is loaded.</summary>
    internal IReadOnlyList<CorrRow> CorruptionRows()
    {
        var view = Corruption.View;
        if (view?.File == null) return [];
        var current = Volatile.Read(ref pendingGame) ?? game;          // a game that is built and waiting for its turn is the game the user has loaded
        var g = Mode == 1 ? current as SessionGame : null;
        string sha = g?.Farm?.Game().Sha1 ?? "";
        var rows = new List<CorrRow>(view.File.Entries.Count);
        foreach (var e in view.File.Entries) rows.Add(Evaluate(e, view.File, g, sha, current));
        return rows;
    }

    private CorrRow Evaluate(StockpileEntry e, Stockpile file, SessionGame? g, string sha, IGame? current)
    {
        var anchor = file.Resolve(e.Anchor);
        string cost = anchor == null ? "" : anchor.Kind switch
        {
            AnchorKind.Boot => $"replays {anchor.Frames:N0} frames from power-on (about {Math.Max(1, anchor.Frames / 300.0):0} s)",
            AnchorKind.State => anchor.Alt is { Kind: AnchorKind.Boot } ? "loads its saved machine (a replay is the alternative)" : "loads its saved machine",
            _ => "applies to the game as it is",
        };
        CorrRow Row(Fit fit, string why) => new() { Entry = e, Fit = fit, Why = why, Cost = cost };
        if (anchor == null) return Row(Fit.CannotHere, "it points to a shared base the file does not contain");
        if (Mode != 1) return Row(Fit.NoGame, "switch the emulator to ROM mode and load the game this was made on");
        if (g == null) return Row(Fit.NoGame, current == null ? "no game is loaded: load the game this was made on" : "the running game is a NES game; the plugin starts corruptions on SNES, Game Boy, Master System, Game Gear and Genesis games");
        string title = e.Game.Title.Length > 0 ? $"\"{e.Game.Title}\"" : e.Game.File.Length > 0 ? e.Game.File : "another game";
        if (e.Game.Console.Length > 0 && !RomDetect.Runs(g.Kind, Consoles.FromKey(e.Game.Console)))
            return Row(Fit.OtherConsole, $"made for the {Consoles.DisplayName(Consoles.FromKey(e.Game.Console))}, not the {Consoles.DisplayName(g.Kind)} that is loaded");
        if (e.Game.Sha1.Length > 0 && !string.Equals(e.Game.Sha1, sha, StringComparison.OrdinalIgnoreCase))
            return Row(Fit.OtherGame, $"made on {title}, not the game that is loaded ({g.FileName}): load that game first");
        var (cpu, apu) = OwnMachine(g.Kind);
        if (e.Machine.Cpu.Length > 0 && !e.Machine.Cpu.Equals(cpu, StringComparison.OrdinalIgnoreCase))
            return Row(Fit.CannotHere, $"made with the CPU '{e.Machine.Cpu}'; the plugin runs the {Consoles.DisplayName(g.Kind)}'s own ('{cpu}')");
        if (e.Machine.Apu.Length > 0 && !e.Machine.Apu.Equals(apu, StringComparison.OrdinalIgnoreCase))
            return Row(Fit.CannotHere, $"made with the sound unit '{e.Machine.Apu}'; this emulator's Sound chip setting gives '{apu}': change the Sound chip to match");
        if (e.Game.Sha1.Length == 0) return new CorrRow { Entry = e, Fit = Fit.Fits, Cost = cost + " (it does not say which game it was made on: applied to this one)" };
        return Row(Fit.Fits, "");
    }

    // ---- starting ----

    /// <summary>Starts the selected entry (index into <see cref="CorruptionRows"/>). Returns why it did not begin, or null when it did (the outcome arrives in <see cref="CorruptionPanel.Result"/>).</summary>
    internal string? StartCorruption(int index, bool withWrites = true)
    {
        var view = Corruption.View;
        var rows = CorruptionRows();
        if (view?.File == null || index < 0 || index >= rows.Count) return Fail("pick a corruption from the list first");
        var row = rows[index];
        if (!row.CanStart) return Fail(row.Why);
        if (Corruption.Busy) return Fail("another start is still running");
        var g = (Volatile.Read(ref pendingGame) ?? game) as SessionGame;
        if (g?.Farm == null) return Fail("no console game is running");
        Corruption.CancelRequested = false;
        Corruption.Busy = true;
        Corruption.Status = "starting...";
        bool freeze = Corruption.Freeze && withWrites;
        var entry = withWrites ? row.Entry : new StockpileEntry { Id = row.Entry.Id, Name = row.Entry.Name + " (without its writes)", Game = row.Entry.Game, Machine = row.Entry.Machine, Anchor = row.Entry.Anchor };
        var file = view.File; string path = view.Path;
        Task.Run(() =>
        {
            try { RunCorruptionStart(g, entry, row.Entry, file, path, freeze, withWrites); }
            catch (Exception e)
            {
                Diag.Log("starting a corruption failed: " + e);
                Corruption.SetResult($"could not start '{entry.Name}': {e.Message}", false);
            }
            finally { Corruption.Busy = false; Corruption.Status = ""; }
        });
        return null;

        string Fail(string why) { Corruption.SetResult(why.Length > 0 ? $"cannot start: {why}" : "cannot start", false); return why; }
    }

    internal void CancelCorruption() => Corruption.CancelRequested = true;

    /// <summary>The CPU/sound factory a replayed or loaded game is built with: the console's own CPU and the sound unit this emulator's chip setting gives; an entry made on another is refused with the reason.</summary>
    private IConsoleSession PluginSession(ConsoleKind kind, byte[] romCopy, MachineSpec m, string fileName, DacRecorder dac)
    {
        var (cpu, apu) = OwnMachine(kind);
        if (m.Cpu.Length > 0 && !m.Cpu.Equals(cpu, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"it was made with the CPU '{m.Cpu}', the plugin runs '{cpu}'");
        if (m.Apu.Length > 0 && !m.Apu.Equals(apu, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"it was made with the sound unit '{m.Apu}', this emulator's Sound chip gives '{apu}'");
        var s = ConsoleSessions.Create(kind, romCopy, cpu, CoreCatalog.Default(kind, CoreSlot.Ppu), apu, null, fileName);
        // the Genesis DAC's writes are kept from the first frame of a replay (Instrument Runaway finds the sounds in them)
        var inner = s is NesEmulator.Mix.SegaPictureSession w ? w.Inner : s;
        if (inner is NesEmulator.Sega.MdSession md && md.Board.Sound is NesEmulator.Sega.MdSoundUnit unit) unit.YmPortTap = dac.OnWrite;
        return s;
    }

    private void RunCorruptionStart(SessionGame g, StockpileEntry entry, StockpileEntry original, Stockpile file, string stockpilePath, bool freeze, bool withWrites)
    {
        var kind = g.Kind;
        int chipIdx = Chip;
        var chip = Cx.Chips[chipIdx];
        var anchor = file.Resolve(entry.Anchor)!;
        string label = $"'{original.Name}'";
        var dac = new DacRecorder();

        // an entry with no anchor lands in the game that is running, between frames
        if (anchor.Kind == AnchorKind.None && anchor.Alt == null)
        {
            lock (romLock)
            {
                ApplyPending();
                if (!ReferenceEquals(game, g)) { Corruption.SetResult("the game changed while the start was waiting; nothing was applied", false); return; }
                var domains = MachineDomains.For(g.Session);
                var report = CorruptionApplier.Apply(domains, entry.Writes, null, 0);
                g.Farm!.NoteWrites(entry.Writes.Where(w => w.At == 0).ToList());
                g.RestartListening();                          // the recipes are to be those of the game with the corruption on it
                if (report.Requested > 0 && report.Applied == 0) { Corruption.SetResult($"{label} could not be applied: {(report.Problems.Count > 0 ? report.Problems[0] : report.Describe())}", false); return; }
                Corruption.Applied = Describe(original, stockpilePath, g.FramesRun);
                Corruption.SetResult($"started {label}: applied to the game as it is; {report.Describe()}" + (report.Problems.Count > 0 ? " | " + report.Problems[0] : ""), true);
            }
            if (freeze) FreezeAfterPlaying(g, label, Corruption.PlayFor);
            return;
        }

        var options = new StartOptions
        {
            Status = t => Corruption.Status = t,
            Cancelled = () => Corruption.CancelRequested,
            SessionFactory = (k, copy, m, f) => PluginSession(k, copy, m, f, dac),
        };
        var r = AnchorStart.Start(entry, file, g.PristineRom, g.FileName, kind, null, options);
        if (!r.Ok || r.Session == null || r.AnchorUsed == null)
        {
            Corruption.SetResult(r.Cancelled ? $"stopped; {label} was not started and the game goes on" : r.Message, false);
            return;
        }
        // the new machine becomes the game (between frames), and Instrument Runaway starts at once when asked
        var ng = new SessionGame(g.PristineRom, kind, chip, EmulatorHub.HostRate, g.FileName, r.Session, r.AnchorUsed, entry.Writes, dac) { ChipIndex = chipIdx };
        if (Runaway) SetRunaway(false);
        Interlocked.Increment(ref loadGeneration);          // a load that was still being built is stale now
        Interlocked.Exchange(ref pendingGame, ng)?.Dispose();
        lock (romLock) ApplyPending();
        if (!ReferenceEquals(game, ng)) { Corruption.SetResult($"{label} was started but another game took the emulator's place at once", false); return; }
        Corruption.Applied = withWrites ? Describe(original, stockpilePath, ng.FramesRun) : null;
        string warn = r.Warnings.Count > 0 ? " | " + string.Join(" | ", r.Warnings) : "";
        string msg = withWrites ? r.Message : $"restarted the game at the place {label} was made, without its writes ({(r.Used == AnchorKind.Boot ? $"replayed {r.FramesReplayed:N0} frames" : "loaded its saved machine")})";
        if (freeze) msg += "; " + FreezeAfterPlaying(ng, label, Corruption.PlayFor);
        Corruption.SetResult(msg + warn, true);
    }

    /// <summary>With the freeze tick: Instrument Runaway switches on at once (<paramref name="seconds"/> 0: only the corrupted game's sound memory is captured, nothing of what its sound driver does, because it has not run yet),
    /// or the corrupted game plays for that many seconds first and Runaway switches on by itself, with recipes learned from what its sound driver did in that time. Returns the sentence for the result line.</summary>
    private string FreezeAfterPlaying(SessionGame ng, string label, int seconds)
    {
        if (seconds <= 0)
        {
            SetRunaway(true);
            return "Instrument Runaway is on: the game is frozen with the corrupted sound memory. It did not play first, so nothing of what its sound driver does was learned: choose 'Play for' above 0 s to learn that too";
        }
        long target = ng.FramesRun + (long)Math.Round(seconds * ng.FramesPerSecond);
        Corruption.Listening = $"the corrupted game is playing; Instrument Runaway switches on by itself after {seconds} s";
        Task.Run(() => WaitThenFreeze(ng, label, target, seconds));
        return $"the corrupted game plays for {seconds} s, then Instrument Runaway switches on by itself and keeps what the game's sound driver did";
    }

    private void WaitThenFreeze(SessionGame ng, string label, long targetFrame, int seconds)
    {
        try
        {
            long lastFrames = ng.FramesRun, lastProgress = Environment.TickCount64;
            while (ng.FramesRun < targetFrame)
            {
                if (!ReferenceEquals(Volatile.Read(ref game), ng))
                {
                    Corruption.SetResult($"{label}: another game took the emulator's place before the {seconds} s were over; Instrument Runaway was not switched on", false);
                    return;
                }
                if (ng.FramesRun != lastFrames) { lastFrames = ng.FramesRun; lastProgress = Environment.TickCount64; }
                else if (Environment.TickCount64 - lastProgress > 10_000)
                {
                    Corruption.SetResult($"{label}: the game is not running (FL is stopped and the plugin window is closed), so there is nothing to listen to; start the playback or press Instrument Runaway when you are ready", false);
                    return;
                }
                Corruption.Listening = $"the corrupted game is playing: {Math.Max(0, (targetFrame - ng.FramesRun) / Math.Max(1.0, ng.FramesPerSecond)):0} s left, then Instrument Runaway switches on";
                Thread.Sleep(50);
            }
            SetRunaway(true);
            string note = Snapshot?.ListenNote ?? "";
            Corruption.SetResult($"Instrument Runaway is on after {seconds} s of the corrupted game" + (note.Length > 0 ? ". " + note : ""), Snapshot?.Recipes.Length > 0 || Snapshot == null);
        }
        catch (Exception e)
        {
            Diag.Log("freezing after the corrupted game played failed: " + e);
            Corruption.SetResult($"could not switch Instrument Runaway on: {e.Message}", false);
        }
        finally { Corruption.Listening = ""; }
    }

    private static AppliedCorruption Describe(StockpileEntry e, string path, long frame) => new(path, e.Id, e.Name, e.Description, e.Summary(), e.ContentHash(), frame);

    /// <summary>"Remove corruption": restarts the game at the place the selected entry was made, without its writes. The game's own state since then is not kept.</summary>
    internal string? RemoveCorruption(int index) => StartCorruption(index, withWrites: false);

    // ---- keeping it through a project ----

    /// <summary>The corruption section for a project save (the payload; null: nothing was started on this game, or nothing to say).</summary>
    internal byte[]? CaptureCorruptionSection()
    {
        if (Mode != 1) return null;
        bool frozen; AppliedCorruption? applied;
        StockpileEntry? history = null; string note = "";
        lock (romLock)
        {
            if (loading || Volatile.Read(ref pendingGame) != null) return carriedCorruption;
            var g = game as SessionGame;
            if (g?.Farm == null) return carriedCorruption;
            applied = Corruption.Applied;
            if (applied == null) return carriedCorruption;
            frozen = Runaway;
            if (g.StateSupported && !stateNoteIsSave) note = "the game's machine is in the game-state section";
            else
            {
                // a console with no saved state, or one whose state the project could not hold (too big, refused): the place of the game as a replay recipe (the pads it was handed and the corruption writes made to it).
                // A console with states describes itself by a saved state with the recipe beside it; the recipe is what is wanted here.
                var cap = g.Farm.Capture();
                var boot = cap.Anchor is { Kind: AnchorKind.Boot } direct ? direct : cap.Anchor?.Alt is { Kind: AnchorKind.Boot } alt ? alt : null;
                if (boot != null)
                    history = new StockpileEntry { Name = "project history", Game = g.Farm.Game(), Machine = new MachineSpec { Cpu = OwnMachine(g.Kind).cpu, Apu = OwnMachine(g.Kind).apu }, Anchor = boot };
                else note = cap.Reason.Length > 0 ? cap.Reason : "the game's place cannot be replayed";
            }
        }
        var built = CorruptionSection.Build(applied, frozen, history, note);
        if (built == null)
        {
            Corruption.SetResult("the corrupted game's history is too long to keep in the project; the project will hold the instruments (if saved in Instrument Runaway) but not the game's place", false);
            built = CorruptionSection.Build(applied, frozen, null, "the game's history was too long to keep");
        }
        return built;
    }

    /// <summary>On the loading thread, for a project that carried a corruption section: when the machine did not come back with a saved state, rebuild the game by replaying its history. The new game, or null when the section holds
    /// no history, the machine came back another way, or the replay failed (the reason goes to the window).</summary>
    private SessionGame? RebuildFromHistory(SessionGame fresh, RestoreRequest request, Chip chip, int chipIdx)
    {
        var cs = request.Corruption;
        if (cs == null || cs.Problem != null) return null;
        Corruption.Applied = cs.Applied;
        if (request.Section is { HasState: true } && fresh.StateSupported) return null;          // the game-state section brings the corrupted machine back
        if (cs.History?.Anchor is not { Kind: AnchorKind.Boot } boot)
        {
            if (cs.Note.Length > 0 && !fresh.StateSupported) Corruption.SetProblem($"The project says '{cs.Applied.EntryName}' was started, but the game's place was not kept ({cs.Note}); the game starts from power-on");
            return null;
        }
        try
        {
            var dac = new DacRecorder();
            var options = new StartOptions { Status = t => { Corruption.Status = t; romStatus = $"rebuilding the corrupted game: {t}"; }, SessionFactory = (k, copy, m, f) => PluginSession(k, copy, m, f, dac) };
            var r = AnchorStart.Start(cs.History, null, fresh.PristineRom, fresh.FileName, fresh.Kind, null, options);
            if (!r.Ok || r.Session == null || r.AnchorUsed == null)
            {
                Corruption.SetProblem($"The project's corrupted game could not be rebuilt ({r.Message}); the game starts from power-on");
                return null;
            }
            var ng = new SessionGame(fresh.PristineRom, fresh.Kind, chip, EmulatorHub.HostRate, fresh.FileName, r.Session, r.AnchorUsed, null, dac) { ChipIndex = chipIdx };
            Corruption.SetResult($"The corrupted game was rebuilt by replaying {r.FramesReplayed:N0} frames ('{cs.Applied.EntryName}')", true);
            return ng;
        }
        catch (Exception e)
        {
            Diag.Log("rebuilding the corrupted game failed: " + e);
            Corruption.SetProblem($"The project's corrupted game could not be rebuilt ({e.Message}); the game starts from power-on");
            return null;
        }
        finally { Corruption.Status = ""; }
    }

    // ---- test hooks ----

    /// <summary>A hash of a memory of the running console game (a domain id: ARAM, WRAM, RAM, ...); 0 with no game or no such memory.</summary>
    internal long DomainHashForTests(string id)
    {
        lock (romLock)
        {
            ApplyPending();
            if (game is not SessionGame g) return 0;
            var d = MachineDomains.Find(MachineDomains.For(g.Session), id);
            if (d == null || d.Unavailable != null) return 0;
            ulong h = 1469598103934665603UL;
            for (int i = 0; i < d.Size; i++) h = (h ^ d.Peek(i)) * 1099511628211UL;
            return (long)(h | 1);
        }
    }

    /// <summary>Chip writes the running SNES game's log holds (all since it began or was last restarted); -1 with no SNES game.</summary>
    internal long SpcLogCountForTests() { lock (romLock) { ApplyPending(); return (game as SessionGame)?.SpcLog?.Count ?? -1; } }

    /// <summary>Frames the running console game's farm has recorded; -1 with no console game.</summary>
    internal long CorruptionFarmFrames() { lock (romLock) { ApplyPending(); return (game as SessionGame)?.Farm?.Frames ?? -1; } }

    /// <summary>Whether the farm can no longer describe the running game by a replay from power-on (a reset, a swap, a write it did not see).</summary>
    internal bool CorruptionFarmPoisoned() { lock (romLock) { ApplyPending(); return (game as SessionGame)?.Farm?.PoisonReason != null; } }

    /// <summary>A hash of the audio RAM the Instrument Runaway snapshot holds (0: no snapshot).</summary>
    internal long SnapshotHashForTests()
    {
        var snap = Snapshot;
        if (snap == null) return 0;
        ulong h = 1469598103934665603UL;
        foreach (byte b in snap.Aram) h = (h ^ b) * 1099511628211UL;
        return (long)(h | 1);
    }

    /// <summary>Writes a byte of a memory of the running console game as a corruption made through the plugin would be (between frames; the farm is told, so the game's history holds it and a saved project replays it). False with no game or outside the memory.</summary>
    internal bool PokeDomainForTests(string id, int address, byte value)
    {
        lock (romLock)
        {
            ApplyPending();
            if (game is not SessionGame g) return false;
            var d = MachineDomains.Find(MachineDomains.For(g.Session), id);
            if (d == null || d.Unavailable != null || (uint)address >= (uint)d.Size) return false;
            d.Poke(address, value);
            g.Farm?.NoteWrites(new[] { new CorruptionWrite(id, address, value) });
            return true;
        }
    }

    /// <summary>A byte of a memory of the running console game; -1 with no game or outside it.</summary>
    internal int PeekDomainForTests(string id, int address)
    {
        lock (romLock)
        {
            ApplyPending();
            if (game is not SessionGame g) return -1;
            var d = MachineDomains.Find(MachineDomains.For(g.Session), id);
            return d == null || d.Unavailable != null || (uint)address >= (uint)d.Size ? -1 : d.Peek(address);
        }
    }
}
