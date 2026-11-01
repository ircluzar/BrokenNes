using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BrokenNes.CorruptorModels;
using NesEmulator;
using NesEmulator.Corruption;
using StockpileFile = NesEmulator.Corruption.Stockpile;

namespace BrokenNes
{
    /// <summary>
    /// Headless Glitch Harvester engine that manages base states, stash, and stockpile.
    /// This class is completely detached from UI and can be used by both WinForms and Web API.
    /// </summary>
    public partial class GlitchHarvesterEngine
    {
        // State collections
        public List<HarvesterBaseState> BaseStates { get; } = new();
        public List<HarvestEntry> Stash { get; } = new();
        public List<HarvestEntry> Stockpile { get; } = new();
        
        // Configuration
        public string SelectedBaseId { get; set; } = string.Empty;
        public bool LoadOnOperation { get; set; } = true;
        
        // Counters for auto-naming
        private int _stashCounter = 0;
        private int _stockpileCounter = 0;
        
        // Reference to corruptor for blast generation
        private readonly Corruptor _corruptor;
        
        public GlitchHarvesterEngine(Corruptor corruptor)
        {
            _corruptor = corruptor ?? throw new ArgumentNullException(nameof(corruptor));
        }
        
        // Helper property
        public bool HasSelectedBase => BaseStates.Any(b => b.Id == SelectedBaseId);
        
        /// <summary>
        /// Add a new base state from the current NES state
        /// </summary>
        public async Task<HarvesterBaseState> AddBaseStateAsync(NES nes, string? name = null)
        {
            if (nes == null)
                throw new ArgumentNullException(nameof(nes));

            // Must use the async capture path, not the blocking CaptureAtomicSnapshot wrapper: on
            // single-threaded WASM, GetAwaiter().GetResult() blocks the only thread that could ever
            // service the snapshot request (RunFrame runs on that same thread via the JS rAF loop),
            // so the sync wrapper deadlocks and always times out.
            var stateJson = await nes.CaptureAtomicSnapshotAsync(2000);
            if (string.IsNullOrEmpty(stateJson))
                throw new InvalidOperationException("Failed to capture atomic NES state");
            
            var baseName = string.IsNullOrWhiteSpace(name) 
                ? $"Base {BaseStates.Count + 1}" 
                : name.Trim();
            
            var baseState = new HarvesterBaseState 
            { 
                Name = baseName, 
                State = stateJson 
            };
            
            BaseStates.Add(baseState);
            SelectedBaseId = baseState.Id;
            
            return baseState;
        }
        
        /// <summary>
        /// Get a base state by ID
        /// </summary>
        public HarvesterBaseState? GetBaseState(string id)
        {
            return BaseStates.FirstOrDefault(b => b.Id == id);
        }
        
        /// <summary>
        /// Get all base states
        /// </summary>
        public List<HarvesterBaseState> GetAllBaseStates()
        {
            return BaseStates.ToList();
        }
        
        /// <summary>
        /// Get the currently selected base state
        /// </summary>
        public HarvesterBaseState? GetSelectedBaseState()
        {
            return GetBaseState(SelectedBaseId);
        }
        
        /// <summary>
        /// Select a base state by ID
        /// </summary>
        public void SelectBaseState(string id)
        {
            if (BaseStates.Any(b => b.Id == id))
                SelectedBaseId = id;
            else
                throw new ArgumentException($"Base state with ID '{id}' not found");
        }
        
        /// <summary>
        /// Load the selected base state into the NES
        /// </summary>
        public void LoadSelectedBase(NES nes)
        {
            if (nes == null)
                throw new ArgumentNullException(nameof(nes));
            
            var baseState = GetSelectedBaseState();
            if (baseState == null)
                throw new InvalidOperationException("No base state selected");
            
            nes.LoadState(baseState.State);
        }
        
        /// <summary>
        /// Load a specific base state by ID into the NES
        /// </summary>
        public void LoadBaseState(NES nes, string id)
        {
            if (nes == null)
                throw new ArgumentNullException(nameof(nes));
            
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("Base state ID cannot be null or empty", nameof(id));
            
            var baseState = GetBaseState(id);
            if (baseState == null)
                throw new InvalidOperationException($"Base state with ID '{id}' not found");
            
            nes.LoadState(baseState.State);
        }
        
        /// <summary>
        /// Delete a base state by ID
        /// </summary>
        public void DeleteBaseState(string id)
        {
            var baseState = GetBaseState(id);
            if (baseState == null)
                return;
            
            BaseStates.Remove(baseState);
            
            // Update selection if deleted base was selected
            if (SelectedBaseId == id)
            {
                SelectedBaseId = BaseStates.Any() ? BaseStates.Last().Id : string.Empty;
            }
        }
        
        /// <summary>
        /// Delete the currently selected base state
        /// </summary>
        public void DeleteSelectedBaseState()
        {
            DeleteBaseState(SelectedBaseId);
        }
        
        /// <summary>
        /// Corrupt and stash: Load base, apply corruption, save to stash
        /// </summary>
        public HarvestEntry CorruptAndStash(NES nes)
        {
            if (nes == null)
                throw new ArgumentNullException(nameof(nes));
            
            var baseState = GetSelectedBaseState();
            if (baseState == null)
                throw new InvalidOperationException("No base state selected");
            
            // Load base state if configured to do so
            if (LoadOnOperation)
            {
                nes.LoadState(baseState.State);
            }
            
            // Generate corruption writes
            var writes = _corruptor.GenerateBlastLayer(_corruptor.CorruptIntensity);
            
            // Capture the exact pre-corruption state for perfect replayability
            var capturedState = nes.CaptureAtomicSnapshot(2000);
            if (string.IsNullOrEmpty(capturedState))
                throw new InvalidOperationException("Failed to capture atomic NES state");
            
            // Apply corruption
            _corruptor.ApplyBlastLayer(writes, nes);
            
            // Create stash entry
            var entry = new HarvestEntry
            {
                Name = $"Stash {++_stashCounter}",
                BaseStateId = baseState.Id,
                State = capturedState,
                Writes = writes
            };
            
            Stash.Add(entry);
            
            return entry;
        }
        
        /// <summary>
        /// Corrupt and stash using a specific base state ID
        /// </summary>
        public HarvestEntry CorruptAndStash(NES nes, string baseStateId)
        {
            if (nes == null)
                throw new ArgumentNullException(nameof(nes));
            
            if (string.IsNullOrEmpty(baseStateId))
                throw new ArgumentException("Base state ID cannot be null or empty", nameof(baseStateId));
            
            var baseState = GetBaseState(baseStateId);
            if (baseState == null)
                throw new InvalidOperationException($"Base state with ID {baseStateId} not found");
            
            // Load base state if configured to do so
            if (LoadOnOperation)
            {
                nes.LoadState(baseState.State);
            }
            
            // Generate corruption writes
            var writes = _corruptor.GenerateBlastLayer(_corruptor.CorruptIntensity);
            
            // Capture the exact pre-corruption state for perfect replayability
            var capturedState = nes.CaptureAtomicSnapshot(2000);
            if (string.IsNullOrEmpty(capturedState))
                throw new InvalidOperationException("Failed to capture atomic NES state");
            
            // Apply corruption
            _corruptor.ApplyBlastLayer(writes, nes);
            
            // Create stash entry
            var entry = new HarvestEntry
            {
                Name = $"Stash {++_stashCounter}",
                BaseStateId = baseState.Id,
                State = capturedState,
                Writes = writes
            };
            
            Stash.Add(entry);
            
            return entry;
        }
        
        /// <summary>
        /// Get all stash entries
        /// </summary>
        public List<HarvestEntry> GetStash()
        {
            return Stash.ToList();
        }
        
        /// <summary>
        /// Get a stash entry by ID
        /// </summary>
        public HarvestEntry? GetStashEntry(string id)
        {
            return Stash.FirstOrDefault(e => e.Id == id);
        }
        
        /// <summary>
        /// Replay a stash entry
        /// </summary>
        public void ReplayStashEntry(NES nes, string id)
        {
            var entry = GetStashEntry(id);
            if (entry == null)
                throw new ArgumentException($"Stash entry with ID '{id}' not found");
            
            ReplayEntry(nes, entry);
        }
        
        /// <summary>
        /// Promote a stash entry to stockpile
        /// </summary>
        public HarvestEntry PromoteToStockpile(string id)
        {
            var entry = GetStashEntry(id);
            if (entry == null)
                throw new ArgumentException($"Stash entry with ID '{id}' not found");
            
            Stash.Remove(entry);
            entry.Name = $"Entry {++_stockpileCounter}";
            Stockpile.Add(entry);
            StockpileDirty = true;
            
            return entry;
        }
        
        /// <summary>
        /// Delete a stash entry
        /// </summary>
        public void DeleteStashEntry(string id)
        {
            var entry = GetStashEntry(id);
            if (entry != null)
                Stash.Remove(entry);
        }
        
        /// <summary>
        /// Clear all stash entries
        /// </summary>
        public void ClearStash()
        {
            Stash.Clear();
        }
        
        /// <summary>
        /// Get all stockpile entries
        /// </summary>
        public List<HarvestEntry> GetStockpile()
        {
            return Stockpile.ToList();
        }
        
        /// <summary>
        /// Get a stockpile entry by ID
        /// </summary>
        public HarvestEntry? GetStockpileEntry(string id)
        {
            return Stockpile.FirstOrDefault(e => e.Id == id);
        }
        
        /// <summary>
        /// Replay a stockpile entry
        /// </summary>
        public void ReplayStockpileEntry(NES nes, string id)
        {
            var entry = GetStockpileEntry(id);
            if (entry == null)
                throw new ArgumentException($"Stockpile entry with ID '{id}' not found");
            
            ReplayEntry(nes, entry);
        }
        
        /// <summary>
        /// Rename a stockpile entry
        /// </summary>
        public void RenameStockpileEntry(string id, string newName)
        {
            var entry = GetStockpileEntry(id);
            if (entry == null)
                throw new ArgumentException($"Stockpile entry with ID '{id}' not found");
            
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("Name cannot be empty", nameof(newName));
            
            entry.Name = newName.Trim();
            StockpileDirty = true;
        }

        /// <summary>Gives a stockpile entry a description (free text for the user; empty clears it).</summary>
        public void DescribeStockpileEntry(string id, string description)
        {
            var entry = GetStockpileEntry(id);
            if (entry == null)
                throw new ArgumentException($"Stockpile entry with ID '{id}' not found");
            entry.Description = (description ?? string.Empty).Trim();
            if (entry.Description.Length > 65536) entry.Description = entry.Description[..65536];
            StockpileDirty = true;
        }
        
        /// <summary>
        /// Delete a stockpile entry
        /// </summary>
        public void DeleteStockpileEntry(string id)
        {
            var entry = GetStockpileEntry(id);
            if (entry != null)
            {
                Stockpile.Remove(entry);
                StockpileDirty = true;
            }
        }
        
        // ---------------------------------------------------------------- the stockpile file (NesEmulator.Corruption, format v2)

        /// <summary>The file the stockpile was last opened from or saved to (null: none yet).</summary>
        public string? StockpilePath { get; private set; }
        /// <summary>A name for the stockpile file (written into it).</summary>
        public string StockpileName { get; set; } = string.Empty;
        /// <summary>The stockpile changed since it was last saved to or opened from a file.</summary>
        public bool StockpileDirty { get; private set; }

        /// <summary>The stockpile as a v2 stockpile object (every entry mapped to the file's entry; shared bases are not used by this engine).</summary>
        public StockpileFile ToStockpileFile()
        {
            var file = new StockpileFile { Name = StockpileName, Writer = "Glitch Harvester", Created = DateTime.UtcNow };
            foreach (var e in Stockpile) file.Entries.Add(GhStockpileMap.ToFile(e, BaseStates));
            return file;
        }

        /// <summary>
        /// Export stockpile as JSON: the v2 stockpile file (see NesEmulator.Corruption.StockpileJson). The older export was a bare list of { Id, Name, BaseStateId, Created, Writes, State };
        /// <see cref="ImportStockpile"/> still reads it.
        /// </summary>
        public string ExportStockpile() => StockpileJson.Write(ToStockpileFile());

        /// <summary>What reading a stockpile file added, and what it left out and why.</summary>
        public sealed class StockpileImportResult
        {
            public int Added { get; set; }
            public bool Legacy { get; set; }
            public List<string> Refusals { get; } = new();
            public List<string> Warnings { get; } = new();
            public string Summary => $"{Added} entr{(Added == 1 ? "y" : "ies")} added" + (Refusals.Count > 0 ? $", {Refusals.Count} refused" : "");
        }

        /// <summary>
        /// Import stockpile from JSON (the v2 file, or the older bare list). Entries keep their ids unless one is already in the stockpile (then it gets a new one). A file that cannot be read at all
        /// throws <see cref="ArgumentException"/> with the reasons; entries refused one by one are listed in the result, never dropped silently.
        /// </summary>
        public StockpileImportResult ImportStockpile(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON cannot be empty", nameof(json));
            return AddFromFile(StockpileJson.Read(json), replace: false);
        }

        private StockpileImportResult AddFromFile(StockpileReadResult read, bool replace)
        {
            var result = new StockpileImportResult { Legacy = read.Legacy };
            result.Refusals.AddRange(read.Refusals);
            result.Warnings.AddRange(read.Warnings);
            if (read.Stockpile == null)
                throw new ArgumentException(read.Refusals.Count > 0 ? string.Join("; ", read.Refusals) : "this is not a stockpile");
            if (replace) Stockpile.Clear();
            var ids = new HashSet<string>(Stockpile.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var fe in read.Stockpile.Entries)
            {
                var e = GhStockpileMap.FromFile(fe, read.Stockpile);
                if (!ids.Add(e.Id)) { e.Id = Guid.NewGuid().ToString(); ids.Add(e.Id); }
                Stockpile.Add(e);
                result.Added++;
            }
            if (replace && read.Stockpile.Name.Length > 0) StockpileName = read.Stockpile.Name;
            _stockpileCounter = Math.Max(_stockpileCounter, Stockpile.Count);
            StockpileDirty = !replace || result.Refusals.Count > 0;
            return result;
        }

        /// <summary>Saves the stockpile to a file (atomically). Throws on a failure to write; the stockpile in memory is untouched.</summary>
        public void SaveStockpileFile(string path)
        {
            StockpileJson.SaveFile(path, ToStockpileFile());
            StockpilePath = path;
            StockpileDirty = false;
        }

        /// <summary>Opens a stockpile file: its entries replace the stockpile in memory (or, with <paramref name="replace"/> false, join it).</summary>
        public StockpileImportResult OpenStockpileFile(string path, bool replace = true)
        {
            var read = StockpileJson.LoadFile(path);
            var result = AddFromFile(read, replace);
            if (replace) { StockpilePath = path; StockpileDirty = result.Refusals.Count > 0; }
            return result;
        }

        /// <summary>
        /// Common replay logic for both stash and stockpile
        /// </summary>
        private void ReplayEntry(NES nes, HarvestEntry entry)
        {
            if (nes == null)
                throw new ArgumentNullException(nameof(nes));
            
            // Use bundled state if available, otherwise fall back to base state
            string? stateToLoad = null;
            
            if (!string.IsNullOrEmpty(entry.State))
            {
                stateToLoad = entry.State;
            }
            else
            {
                var baseState = GetBaseState(entry.BaseStateId);
                if (baseState != null)
                    stateToLoad = baseState.State;
            }
            
            if (stateToLoad == null)
                throw new InvalidOperationException("No state available to load for this entry");
            
            // Load the state
            nes.LoadState(stateToLoad);
            
            // Apply the corruption writes
            _corruptor.ApplyBlastLayer(entry.Writes, nes);
        }
    }
}
