using System;
using System.Collections.Generic;
using System.Linq;
using BrokenNes.CorruptorModels;
using NesEmulator;

namespace BrokenNes
{
    /// <summary>
    /// Headless Glitch Harvester engine that manages base states, stash, and stockpile.
    /// This class is completely detached from UI and can be used by both WinForms and Web API.
    /// </summary>
    public class GlitchHarvesterEngine
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
        public HarvesterBaseState AddBaseState(NES nes, string? name = null)
        {
            if (nes == null)
                throw new ArgumentNullException(nameof(nes));
            
            var stateJson = nes.SaveState();
            if (string.IsNullOrEmpty(stateJson))
                throw new InvalidOperationException("Failed to save NES state");
            
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
            var capturedState = nes.SaveState();
            
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
        }
        
        /// <summary>
        /// Delete a stockpile entry
        /// </summary>
        public void DeleteStockpileEntry(string id)
        {
            var entry = GetStockpileEntry(id);
            if (entry != null)
                Stockpile.Remove(entry);
        }
        
        /// <summary>
        /// Export stockpile as JSON
        /// </summary>
        public string ExportStockpile()
        {
            var exportData = Stockpile.Select(e => new
            {
                e.Id,
                e.Name,
                e.BaseStateId,
                e.Created,
                e.Writes,
                e.State
            }).ToList();
            
            return System.Text.Json.JsonSerializer.Serialize(exportData, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        
        /// <summary>
        /// Import stockpile from JSON
        /// </summary>
        public void ImportStockpile(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON cannot be empty", nameof(json));
            
            try
            {
                var imported = System.Text.Json.JsonSerializer.Deserialize<List<HarvestEntry>>(json);
                if (imported == null)
                    throw new InvalidOperationException("Failed to deserialize stockpile");
                
                foreach (var entry in imported)
                {
                    // Ensure unique IDs
                    entry.Id = Guid.NewGuid().ToString();
                    Stockpile.Add(entry);
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new ArgumentException($"Invalid JSON format: {ex.Message}", nameof(json), ex);
            }
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
