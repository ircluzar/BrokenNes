// gameSave.js - Centralized game save management for all webmodules
// Provides consistent save structure and unified access across Home, Options, Story, Continue, and Cores pages

(function() {
  'use strict';

  const STORAGE_KEY = 'brokenNesGameSave';
  const VOLUME_KEY = 'brokenNesAudioVolumes';

  // Default save structure - use this as the canonical format
  function createDefaultSave() {
    return {
      Level: 1,
      Achievements: [],
      // Use flat arrays with consistent property names (camelCase with Ids suffix)
      ownedCpuIds: ['FMC'],
      ownedPpuIds: ['FMC'],
      ownedApuIds: ['FMC'],
      ownedClockIds: ['FMC'],
      ownedShaderIds: ['PX'],
      UnlockedFeatures: {
        Savestates: false,
        RTC: false,
        GH: false,
        Imagine: false,
        Debug: false
      },
      SeenStory: false,
      UnderConstructionAcknowledged: false
    };
  }

  // Migrate old save format to new format
  function migrateSave(save) {
    if (!save) return createDefaultSave();

    // Check if save uses old OwnedCores format
    if (save.OwnedCores && !save.ownedCpuIds) {
      const migrated = { ...save };
      
      // Convert OwnedCores.CPU to ownedCpuIds
      migrated.ownedCpuIds = save.OwnedCores.CPU || ['FMC'];
      migrated.ownedPpuIds = save.OwnedCores.PPU || ['FMC'];
      migrated.ownedApuIds = save.OwnedCores.APU || ['FMC'];
      migrated.ownedClockIds = save.OwnedCores.Clock || ['FMC'];
      migrated.ownedShaderIds = save.OwnedCores.Shader || ['PX'];
      
      // Remove old format
      delete migrated.OwnedCores;
      
      console.log('[gameSave] Migrated save from OwnedCores format to ownedXxxIds format');
      return migrated;
    }

    // Ensure all required properties exist
    const merged = {
      ...createDefaultSave(),
      ...save
    };

    return merged;
  }

  const gameSave = {
    /**
     * Load game save from storage
     * @returns {Promise<Object>} - Game save object
     */
    async load() {
      try {
        let save = null;
        
        if (window.storage && typeof window.storage.load === 'function') {
          save = await window.storage.load(STORAGE_KEY);
        } else {
          const data = localStorage.getItem(STORAGE_KEY);
          if (data) {
            save = JSON.parse(data);
          }
        }

        // Migrate and merge with defaults
        const migratedSave = migrateSave(save);
        
        return migratedSave;
      } catch (error) {
        console.error('[gameSave] Load error:', error);
        return createDefaultSave();
      }
    },

    /**
     * Save game save to storage
     * @param {Object} save - Game save object
     * @returns {Promise<boolean>} - Success status
     */
    async save(save) {
      try {
        // Ensure the save uses the correct format
        const validatedSave = migrateSave(save);
        
        if (window.storage && typeof window.storage.save === 'function') {
          return await window.storage.save(STORAGE_KEY, validatedSave);
        } else {
          localStorage.setItem(STORAGE_KEY, JSON.stringify(validatedSave));
          return true;
        }
      } catch (error) {
        console.error('[gameSave] Save error:', error);
        return false;
      }
    },

    /**
     * Reset save to defaults
     * @returns {Promise<Object>} - New default save
     */
    async reset() {
      const defaultSave = createDefaultSave();
      await this.save(defaultSave);
      return defaultSave;
    },

    /**
     * Unlock all cores (for debugging/cheats)
     * @param {Object} save - Current save object
     * @returns {Object} - Updated save object
     */
    unlockAllCores(save) {
      const updated = { ...save };
      
      // These must match the actual core class suffixes (CPU_FMC -> FMC, etc.)
      updated.ownedCpuIds = ['FMC', 'LOW', 'LW2', 'SPD', 'EIL', 'Z80'];
      updated.ownedPpuIds = ['FMC', 'LOW', 'LQ', 'SPD', 'BFR', 'CUBE', 'CUBEX', 'EIL'];
      updated.ownedApuIds = ['FMC', 'LOW', 'LQ', 'LQ2', 'QLOW', 'QLQ', 'QLQ2', 'QN', 'SPD', 'SPD2', 'WF', 'EIL', 'MNES'];
      // Clock cores: CLOCK_FMC, CLOCK_TRB, CLOCK_CLR
      updated.ownedClockIds = ['FMC', 'TRB', 'CLR'];
      // ShaderType enum from NesShaderManager.cs (Windows/HLSL shaders)
      updated.ownedShaderIds = [
        'PX', 'BLD', 'BUMP', 'CCC', 'CNMA', 'CRY', 'CRZ', 'DOT', 'EXE',
        'HUE', 'LAT', 'LCD', 'LSD', 'MSH', 'MUSK', 'RF', 'RGBX', 'SPK', 'TRI',
        'TTF', 'TV', 'VHS', 'WARM', 'WTR'
      ];

      if (!updated.UnlockedFeatures) {
        updated.UnlockedFeatures = {};
      }
      updated.UnlockedFeatures.Savestates = true;
      updated.UnlockedFeatures.RTC = true;
      updated.UnlockedFeatures.GH = true;
      updated.UnlockedFeatures.Imagine = true;
      updated.UnlockedFeatures.Debug = true;

      return updated;
    },

    /**
     * Count total owned cores
     * @param {Object} save - Game save object
     * @returns {number} - Total number of owned cores
     */
    countOwnedCores(save) {
      if (!save) return 0;
      
      const cpu = (save.ownedCpuIds || []).length;
      const ppu = (save.ownedPpuIds || []).length;
      const apu = (save.ownedApuIds || []).length;
      const clock = (save.ownedClockIds || []).length;
      const shader = (save.ownedShaderIds || []).length;
      
      return cpu + ppu + apu + clock + shader;
    },

    /**
     * Check if a specific core is owned
     * @param {Object} save - Game save object
     * @param {string} type - Core type (cpu, ppu, apu, clock, shader)
     * @param {string} id - Core ID
     * @returns {boolean} - Whether core is owned
     */
    hasCore(save, type, id) {
      if (!save) return false;
      
      const normalizedType = type.toLowerCase();
      const normalizedId = id.toUpperCase();
      const key = `owned${type.charAt(0).toUpperCase() + normalizedType.slice(1)}Ids`;
      
      return (save[key] || []).some(coreId => coreId.toUpperCase() === normalizedId);
    },

    /**
     * Add a core to the save
     * @param {Object} save - Game save object
     * @param {string} type - Core type (cpu, ppu, apu, clock, shader)
     * @param {string} id - Core ID
     * @returns {Object} - Updated save object
     */
    addCore(save, type, id) {
      const updated = { ...save };
      const normalizedType = type.toLowerCase();
      const key = `owned${type.charAt(0).toUpperCase() + normalizedType.slice(1)}Ids`;
      
      if (!updated[key]) {
        updated[key] = [];
      }
      
      if (!this.hasCore(updated, type, id)) {
        updated[key] = [...updated[key], id];
      }
      
      return updated;
    },

    /**
     * Load audio volumes
     * @returns {Promise<Object>} - Volume settings
     */
    async loadVolumes() {
      try {
        let data = null;
        
        if (window.storage && typeof window.storage.load === 'function') {
          data = await window.storage.load(VOLUME_KEY);
        } else {
          const stored = localStorage.getItem(VOLUME_KEY);
          if (stored) {
            data = JSON.parse(stored);
          }
        }

        return {
          master: 1.0,
          music: 0.42,
          sfx: 0.8,
          ...data
        };
      } catch (error) {
        console.error('[gameSave] Load volumes error:', error);
        return {
          master: 1.0,
          music: 0.42,
          sfx: 0.8
        };
      }
    },

    /**
     * Save audio volumes
     * @param {Object} volumes - Volume settings
     * @returns {Promise<boolean>} - Success status
     */
    async saveVolumes(volumes) {
      try {
        if (window.storage && typeof window.storage.save === 'function') {
          return await window.storage.save(VOLUME_KEY, volumes);
        } else {
          localStorage.setItem(VOLUME_KEY, JSON.stringify(volumes));
          return true;
        }
      } catch (error) {
        console.error('[gameSave] Save volumes error:', error);
        return false;
      }
    }
  };

  // Expose to window
  window.gameSave = gameSave;
})();
