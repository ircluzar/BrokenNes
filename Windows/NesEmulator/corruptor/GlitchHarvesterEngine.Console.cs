using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BrokenNes.CorruptorModels;
using NesEmulator.Corruption;
using NesEmulator.Systems;

namespace BrokenNes
{
    /// <summary>
    /// What the Glitch Harvester needs from the app that runs a console session (SNES, Game Boy, Master System, Game Gear, Genesis): the farm of the running game, a way to run work between its frames, and a way to start a stockpile entry on it
    /// (which, for a replay from power-on, builds a new machine and puts it in the old one's place). The desktop app implements it; the engine holds the lists.
    /// </summary>
    public interface IConsoleGhHost
    {
        /// <summary>The farm of the running console game, or null when no console game runs (the NES has its own path).</summary>
        ConsoleFarm? Farm { get; }
        /// <summary>Runs <paramref name="work"/> between frames of the running game, on the thread that owns the machine, and returns its result.</summary>
        T BetweenFrames<T>(Func<T> work);
        /// <summary>Starts the entry on the running game (see <see cref="AnchorStart"/>): loads a saved state or applies at once into the running machine, or replays from power-on and installs the new machine. Never silent: the result says why not.</summary>
        StartResult StartEntry(StockpileEntry entry, bool allowOtherGame);
    }

    public partial class GlitchHarvesterEngine
    {
        // ================= console sessions: the same lists, with anchors in place of the NES's saved state =================

        /// <summary>Takes the running console game's position as a base: its saved state where the console has states, otherwise the replay from power-on that reaches it. Refuses (with the reason) when neither is possible.</summary>
        public HarvesterBaseState AddBaseState(IConsoleGhHost host, string? name = null)
        {
            var farm = host.Farm ?? throw new InvalidOperationException("no console game is running");
            var cap = host.BetweenFrames(() => farm.Capture());
            if (cap.Anchor == null) throw new InvalidOperationException(cap.Reason);
            var baseName = string.IsNullOrWhiteSpace(name) ? $"Base {BaseStates.Count + 1}" : name.Trim();
            var b = new HarvesterBaseState { Name = baseName, Anchor = cap.Anchor, Game = farm.Game(), Machine = farm.Machine };
            BaseStates.Add(b);
            SelectedBaseId = b.Id;
            return b;
        }

        /// <summary>Puts the machine back where a base was (a saved state is loaded; a replay is run and the new machine installed).</summary>
        public StartResult LoadBase(IConsoleGhHost host, string id)
        {
            var b = GetBaseState(id) ?? throw new InvalidOperationException($"Base state with ID {id} not found");
            if (b.Anchor == null) return new StartResult { Ok = false, Message = $"'{b.Name}' was taken on the NES; it cannot be loaded into a console game" };
            return host.StartEntry(new StockpileEntry { Name = b.Name, Game = b.Game ?? new GameRef(), Machine = b.Machine ?? new MachineSpec(), Anchor = b.Anchor }, false);
        }

        /// <summary>
        /// Corrupt and stash on a console game: (when "load on operation" is on and a base is selected) put the machine back at the base; say where the machine is; blast it with the corruptor's settings (the selected domains, the intensity, the blast type);
        /// keep the position, the game and the writes as a stash entry. Throws with the reason when the position cannot be stored (nothing is blasted then).
        /// </summary>
        public HarvestEntry CorruptAndStash(IConsoleGhHost host, string? baseStateId = null)
        {
            var farm = host.Farm ?? throw new InvalidOperationException("no console game is running");
            var baseState = baseStateId != null ? GetBaseState(baseStateId) : GetSelectedBaseState();
            if (baseStateId != null && baseState == null) throw new InvalidOperationException($"Base state with ID {baseStateId} not found");
            if (LoadOnOperation && baseState != null)
            {
                var loaded = LoadBase(host, baseState.Id);
                if (!loaded.Ok) throw new InvalidOperationException(loaded.Message);
            }
            if (_corruptor.BlastType.StartsWith("IMAGINE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Imagine predicts NES code: it works on the NES only");
            var selected = _corruptor.MemoryDomains.Where(d => d.Selected).Select(d => d.Key).ToList();
            if (selected.Count == 0) throw new InvalidOperationException("no memory domain is selected");
            long seed = Random.Shared.NextInt64();
            return host.BetweenFrames(() =>
            {
                var cap = farm.Capture();
                if (cap.Anchor == null) throw new InvalidOperationException(cap.Reason);
                var type = BlastGenerator.Parse(_corruptor.BlastType);
                var report = farm.Blast(selected, _corruptor.CorruptIntensity, type, seed, false, out var writes);
                if (report.Applied == 0) throw new InvalidOperationException("no write landed: " + (report.Problems.Count > 0 ? report.Problems[0] : "nothing was selected that this game has"));
                var entry = new HarvestEntry
                {
                    Name = $"Stash {++_stashCounter}", BaseStateId = baseState?.Id ?? string.Empty, Anchor = cap.Anchor,
                    Game = farm.Game(), Machine = farm.Machine,
                    Source = new BlastSource { Blast = _corruptor.BlastType, Intensity = _corruptor.CorruptIntensity, Seed = seed, Domains = selected, SizeModel = "true" },
                    Writes = writes.Select(w => new BlastInstruction { Domain = w.Domain, Address = w.Address, Value = w.Value, Was = w.Was, At = w.At }).ToList(),
                };
                Stash.Add(entry);
                return entry;
            });
        }

        /// <summary>Starts a stash or stockpile entry on the running console game. An entry made on the NES (it carries a NES saved state) is refused.</summary>
        public StartResult ReplayEntry(IConsoleGhHost host, HarvestEntry entry, bool allowOtherGame = false)
        {
            if (entry.Anchor == null && entry.State.Length > 0)
                return new StartResult { Ok = false, Message = $"'{entry.Name}' was made on the NES; it cannot be started on a console game" };
            return host.StartEntry(GhStockpileMap.ToFile(entry, BaseStates), allowOtherGame);
        }

        /// <summary>Blasts the running console game once with the corruptor's settings (the RTC tab's Manual Blast), without storing anything. Returns the report.</summary>
        public ApplyReport LiveBlast(IConsoleGhHost host)
        {
            var farm = host.Farm ?? throw new InvalidOperationException("no console game is running");
            if (_corruptor.BlastType.StartsWith("IMAGINE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Imagine predicts NES code: it works on the NES only");
            var selected = _corruptor.MemoryDomains.Where(d => d.Selected).Select(d => d.Key).ToList();
            long seed = Random.Shared.NextInt64();
            var type = BlastGenerator.Parse(_corruptor.BlastType);
            return host.BetweenFrames(() => farm.Blast(selected, _corruptor.CorruptIntensity, type, seed, false, out _));
        }
    }
}
