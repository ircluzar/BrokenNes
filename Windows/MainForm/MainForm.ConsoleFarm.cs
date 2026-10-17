using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using BrokenNes.CorruptorModels;
using NesEmulator.Corruption;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    /// <summary>
    /// The Glitch Harvester on console games (SNES, Game Boy, Master System, Game Gear, Genesis): the farm that records the running game's pads and corruption writes so a position can be stored and started again (in this
    /// app or in the FL plugin), the memory domains of the running machine in the RTC tab, a blast on a session, and the stockpile file's dialogs. The logic lives in NesEmulator.Corruption (ConsoleFarm) and the
    /// Glitch Harvester engine; this is the app's side of it: the thread rules, the machine swap and the player's save file kept safe.
    /// </summary>
    public partial class MainForm
    {
        private readonly ConsoleFarm farm = new();
        private ConsoleGhHost? ghHost;
        private int farmGeneration;
        private volatile string farmStatus = "";
        private volatile bool farmBusy;
        private volatile string farmLastMessage = "";

        /// <summary>The Glitch Harvester's way into the running console game (null-farm when none runs).</summary>
        internal IConsoleGhHost ConsoleGh => ghHost ??= new ConsoleGhHost(this);

        internal ConsoleFarm Farm => farm;
        internal string FarmStatusText => farmStatus;
        internal bool FarmBusy => farmBusy;
        internal string FarmLastMessage => farmLastMessage;

        // ------------------------------------------------------------------ the game starts and stops

        /// <summary>A console game has started: record from its power-on, and show its memories in the RTC tab.</summary>
        private void FarmAttach(IConsoleSession s, byte[] rom, string romName)
        {
            Interlocked.Increment(ref farmGeneration);
            bool own = CoresAreOwn(s.Console, out string why);
            farm.Attach(s, rom, romName, MachineNow(s), own, why);
            BuildSessionDomains();
        }

        private void FarmDetach()
        {
            Interlocked.Increment(ref farmGeneration);
            farm.Detach();
        }

        /// <summary>The cores the running game is on, as a stockpile records them.</summary>
        private MachineSpec MachineNow(IConsoleSession s)
        {
            var m = new MachineSpec
            {
                Cpu = SelectedCore(s.Console, CoreSlot.Cpu), Ppu = SelectedCore(s.Console, CoreSlot.Ppu), Apu = SelectedCore(s.Console, CoreSlot.Apu),
                Note = s.Description,
            };
            if (SegaMachineOf(s) is { } sega) { m.Region = sega.region; m.Model = sega.model; }
            return m;
        }

        /// <summary>A replay from power-on is exact only on the console's own CPU and sound unit (the picture chip is a view).</summary>
        private bool CoresAreOwn(ConsoleKind console, out string why)
        {
            why = "";
            if (!SelectedCore(console, CoreSlot.Cpu).Equals(CoreCatalog.Default(console, CoreSlot.Cpu), StringComparison.OrdinalIgnoreCase))
            { why = $"the CPU is not the {Consoles.DisplayName(console)}'s own"; return false; }
            if (!SelectedCore(console, CoreSlot.Apu).Equals(CoreCatalog.Default(console, CoreSlot.Apu), StringComparison.OrdinalIgnoreCase))
            { why = $"the sound unit is not the {Consoles.DisplayName(console)}'s own"; return false; }
            return true;
        }

        /// <summary>The RTC tab's domain list for the running console game: its memories at their true sizes. A choice made for a memory the new list also has is kept.</summary>
        private void BuildSessionDomains()
        {
            var s = session;
            if (s == null) return;
            var domains = MachineDomains.For(s);
            lock (corruptorLock)
            {
                var before = corruptor.MemoryDomains.ToDictionary(d => d.Key, d => d.Selected);
                bool any = before.Count > 0 && domains.Any(d => before.ContainsKey(d.Id));
                corruptor.MemoryDomains.Clear();
                foreach (var d in domains)
                {
                    corruptor.MemoryDomains.Add(new DomainSel
                    {
                        Key = d.Id, Label = d.Unavailable != null ? d.Label + " - not available: " + d.Unavailable : d.Label, Size = d.Size,
                        Selected = d.Unavailable == null && (any ? before.GetValueOrDefault(d.Id) : d.Id is "WRAM" or "RAM"),
                    });
                }
            }
            NotifyCorruptorChanged();
        }

        // ------------------------------------------------------------------ one frame

        /// <summary>Per frame, with the emulation lock held and before the frame runs: the pads about to be handed to the game.</summary>
        private void FarmBeforeFrame(PadButtons p1, PadButtons p2)
        {
            if (farm.Attached) farm.OnFrame((ushort)p1, (ushort)p2);
        }

        /// <summary>Per frame, with the emulation lock held and after the frame ran: the RTC tab's auto-corrupt.</summary>
        private void FarmAfterFrame()
        {
            if (!farm.Attached || !corruptor.AutoCorrupt)
            {
                autoCorruptFrameCounter = 0;
                return;
            }
            if (++autoCorruptFrameCounter < AutoCorruptFrameInterval) return;
            autoCorruptFrameCounter = 0;
            try { SessionBlast(); }
            catch (Exception ex) { Console.WriteLine($"Auto-corrupt error: {ex.Message}"); }
        }

        /// <summary>One blast on the running console game with the corruptor's settings. Call with the emulation lock held (between frames).</summary>
        private ApplyReport SessionBlast()
        {
            List<string> selected;
            int intensity; string blast;
            lock (corruptorLock)
            {
                selected = corruptor.MemoryDomains.Where(d => d.Selected).Select(d => d.Key).ToList();
                intensity = corruptor.CorruptIntensity; blast = corruptor.BlastType;
            }
            if (blast.StartsWith("IMAGINE", StringComparison.OrdinalIgnoreCase))
            {
                corruptor.LastBlastInfo = "Imagine predicts NES code: it works on the NES only";
                return new ApplyReport();
            }
            var report = farm.Blast(selected, intensity, BlastGenerator.Parse(blast), Random.Shared.NextInt64(), false, out _);
            corruptor.LastBlastInfo = $"{blast}: {report.Describe()} over {selected.Count} domain(s)";
            if (report.Applied > 0 && farm.SaveRamTouched) Console.WriteLine("[Corruption] the game's save RAM was written: it will not be saved over the player's save");
            return report;
        }

        // ------------------------------------------------------------------ things that break a replay from power-on

        private void FarmPoison(string why) { if (farm.Attached) lock (emulationLock) farm.Poison(why); }

        private void FarmCoresChanged(IConsoleSession s)
        {
            bool own = CoresAreOwn(s.Console, out string why);
            lock (emulationLock) farm.SetCores(MachineNow(s), own, why);
        }

        // ------------------------------------------------------------------ the engine's way in

        private sealed class ConsoleGhHost : IConsoleGhHost
        {
            private readonly MainForm form;
            public ConsoleGhHost(MainForm form) { this.form = form; }

            public ConsoleFarm? Farm => form.session != null && form.farm.Attached ? form.farm : null;

            public T BetweenFrames<T>(Func<T> work) { lock (form.emulationLock) return work(); }

            public StartResult StartEntry(StockpileEntry entry, bool allowOtherGame) => form.StartFarmEntry(entry, allowOtherGame);
        }

        /// <summary>
        /// Starts a stockpile entry on the running console game. Held under the emulation lock from the first byte to the last: a replay builds a private machine and swaps it in, and the recording is rebased on it before the
        /// next frame can run, so no frame is ever run on a machine the recording does not know. (The old game freezes for the length of a replay; the status text says how far it is.)
        /// </summary>
        private StartResult StartFarmEntry(StockpileEntry entry, bool allowOtherGame)
        {
            if (farmBusy) return new StartResult { Ok = false, Message = "another start is still running" };
            int generation = Volatile.Read(ref farmGeneration);
            farmBusy = true;
            try
            {
                var options = new StartOptions
                {
                    AllowOtherGame = allowOtherGame,
                    Status = t => farmStatus = t,
                    SessionFactory = FarmSessionFactory,
                };
                StartResult r;
                lock (emulationLock)
                {
                    if (session == null || !farm.Attached) return new StartResult { Ok = false, Message = "no console game is running" };
                    r = farm.Start(entry, null, options, ns =>
                    {
                        if (generation != Volatile.Read(ref farmGeneration)) return false;   // the game was changed while the replay ran
                        var old = session;
                        session = ns;
                        sessionFrames = 0;
                        try { old?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[Corruption] disposing the replaced machine: {ex.Message}"); }
                        return true;
                    });
                }
                farmLastMessage = r.Message + (r.Warnings.Count > 0 ? " | " + string.Join(" | ", r.Warnings) : "");
                if (r.Ok)
                {
                    BuildSessionDomains();                                  // the new machine's memories (an HLE sound unit has no ARAM, a replay may not)
                    BeginInvoke(new Action(() => { UpdateConsoleTitle(); }));
                    Console.WriteLine("[Corruption] " + farmLastMessage);
                }
                return r;
            }
            catch (Exception ex)
            {
                farmLastMessage = "could not start the entry: " + ex.Message;
                return new StartResult { Ok = false, Message = farmLastMessage };
            }
            finally { farmBusy = false; farmStatus = ""; }
        }

        /// <summary>The machine a replay is built as: the entry's own CPU and sound unit (they decide what the machine does), the app's current picture choice (a view), the app's firmware lookup.</summary>
        private IConsoleSession FarmSessionFactory(ConsoleKind console, byte[] romCopy, MachineSpec m, string fileName)
        {
            string Own(CoreSlot slot, string stored)
            {
                if (stored.Length == 0) return CoreCatalog.Default(console, slot);
                if (!CoreCatalog.Options(console, slot).Any(o => o.Id.Equals(stored, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"it was made with the {slot} '{stored}', which this build does not offer for the {Consoles.DisplayName(console)}");
                return stored;
            }
            return ConsoleSessions.Create(console, romCopy, Own(CoreSlot.Cpu, m.Cpu), SelectedCore(console, CoreSlot.Ppu), Own(CoreSlot.Apu, m.Apu),
                                          names => FindSnesFirmware(currentRomPath, names), fileName, SegaOptionsFromConfig());
        }

        // ------------------------------------------------------------------ what the HTTP API reports

        internal object FarmInfo()
        {
            var s = session;
            if (s == null || !farm.Attached)
                return new { active = false, console = Consoles.Key(SelectedConsole), mode = "nes", message = "the NES keeps its own saved states; console farming is for the SNES, Game Boy, Master System, Game Gear and Genesis" };
            string how, reason = "";
            if (s.SupportsState) how = "state";
            else if (farm.PoisonReason == null) how = "boot";
            else { how = "none"; reason = $"the {Consoles.DisplayName(s.Console)} has no save states and the game cannot be replayed from power-on: {farm.PoisonReason}"; }
            return new
            {
                active = true, console = Consoles.Key(s.Console), consoleName = Consoles.DisplayName(s.Console), game = s.Title, file = farm.FileName,
                sha1 = farm.Game().Sha1,
                positions = how,      // how a position is stored: "state" (a saved machine), "boot" (a replay from power-on), "none" (cannot be stored now)
                reason,
                replayRecipe = farm.PoisonReason == null,        // whether the replay from power-on can be stored beside (or instead of) a saved machine
                replayReason = farm.PoisonReason ?? "",
                frames = farm.Frames, recordedWrites = farm.RecordedWrites, replayCapFrames = new StartOptions().MaxReplayFrames,
                saveRamProtected = farm.SaveRamTouched,
                busy = farmBusy, status = farmStatus, last = farmLastMessage,
                domains = farm.Domains().Select(d => new { id = d.Id, label = d.Label, size = d.Size, kind = d.Kind.ToString().ToLowerInvariant(), writable = d.Writable, unavailable = d.Unavailable }),
            };
        }

        // ------------------------------------------------------------------ the stockpile file's dialogs

        /// <summary>The folder stockpiles are kept in by default.</summary>
        internal static string StockpileFolder => Path.Combine(AppDataDir, "Stockpiles");

        /// <summary>Asks the player for a stockpile file (on the UI thread). Null when they cancel.</summary>
        internal string? PickStockpilePath(bool save)
        {
            string? picked = null;
            Invoke(new Action(() =>
            {
                Directory.CreateDirectory(StockpileFolder);
                FileDialog dialog = save
                    ? new SaveFileDialog { DefaultExt = "json", AddExtension = true, FileName = "stockpile" + Stockpile.FileSuffix, OverwritePrompt = true }
                    : new OpenFileDialog { CheckFileExists = true };
                using (dialog)
                {
                    dialog.Title = save ? "Save the stockpile" : "Open a stockpile";
                    dialog.Filter = "Stockpiles (*.json)|*.json|All files (*.*)|*.*";
                    dialog.InitialDirectory = corruptor.GlitchHarvester.StockpilePath is { } p && Path.GetDirectoryName(p) is { } d ? d : StockpileFolder;
                    if (dialog.ShowDialog(this) == DialogResult.OK) picked = dialog.FileName;
                }
            }));
            return picked;
        }
    }
}
