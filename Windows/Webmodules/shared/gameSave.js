// gameSave.js - Centralized game save management for all webmodules
// Provides consistent save structure and unified access across Home, Options, Story, Continue, and Cores pages

(function() {
  'use strict';

  const STORAGE_KEY = 'game_save_v1';
  const LEGACY_STORAGE_KEY = 'brokenNesGameSave';
  const VOLUME_KEY = 'brokenNesAudioVolumes';

  // Default save structure - use this as the canonical format
  function createDefaultSave() {
    return {
      Level: 1,
      LevelCleared: false,
      Achievements: [],
      // Use flat arrays with consistent property names (camelCase with Ids suffix)
      ownedCpuIds: ['FMC'],
      ownedPpuIds: ['FMC'],
      ownedApuIds: ['FMC'],
      ownedClockIds: ['FMC'],
      ownedShaderIds: ['PX'],
      SavestatesUnlocked: false,
      RtcUnlocked: false,
      GhUnlocked: false,
      ImagineUnlocked: false,
      DebugUnlocked: false,
      PreferredCpuId: 'FMC',
      PreferredPpuId: 'FMC',
      PreferredApuId: 'FMC',
      PreferredShaderId: 'PX',
      Preferences: {
        CPU: 'FMC',
        PPU: 'FMC',
        APU: 'FMC',
        Shader: 'PX'
      },
      UnlockedFeatures: {
        Savestates: false,
        RTC: false,
        GH: false,
        Imagine: false,
        Debug: false
      },
      PendingDeckContinue: false,
      PendingDeckContinueRom: null,
      PendingDeckContinueTitle: null,
      PendingDeckContinueAtUtc: null,
      ContinueSlots: {},
      SeenStory: false,
      UnderConstructionAcknowledged: false,
      AllCoresUnlockedCongrats: false
    };
  }

  function normalizeContinueSlotKey(romKey) {
    return typeof romKey === 'string' && romKey.trim()
      ? romKey.trim().toLowerCase()
      : '';
  }

  function normalizeContinueSlots(slots, legacySave) {
    const normalized = {};
    const source = slots && typeof slots === 'object' ? slots : {};

    Object.entries(source).forEach(([key, value]) => {
      const slotRomKey = value && typeof value.romKey === 'string' && value.romKey.trim()
        ? value.romKey.trim()
        : value && typeof value.RomKey === 'string' && value.RomKey.trim()
          ? value.RomKey.trim()
          : '';
      const romKey = slotRomKey || (typeof key === 'string' ? key : '');
      const normalizedKey = normalizeContinueSlotKey(romKey || key);
      if (!normalizedKey) {
        return;
      }

      const slotTitle = value && typeof value.title === 'string' && value.title.trim()
        ? value.title
        : value && typeof value.Title === 'string' && value.Title.trim()
          ? value.Title
          : '';

      const slotUpdatedAtUtc = value && value.updatedAtUtc
        ? value.updatedAtUtc
        : value && value.UpdatedAtUtc
          ? value.UpdatedAtUtc
          : null;

      const slotPreviewImagePath = value && value.previewImagePath
        ? value.previewImagePath
        : value && value.PreviewImagePath
          ? value.PreviewImagePath
          : null;

      normalized[normalizedKey] = {
        romKey,
        title: slotTitle
          ? slotTitle
          : romKey,
        updatedAtUtc: slotUpdatedAtUtc,
        previewImagePath: slotPreviewImagePath
      };
    });

    const legacyKey = normalizeContinueSlotKey(legacySave?.PendingDeckContinueRom);
    if (legacySave?.PendingDeckContinue && legacyKey && !normalized[legacyKey]) {
      normalized[legacyKey] = {
        romKey: legacySave.PendingDeckContinueRom,
        title: legacySave.PendingDeckContinueTitle || legacySave.PendingDeckContinueRom,
        updatedAtUtc: legacySave.PendingDeckContinueAtUtc || null,
        previewImagePath: null
      };
    }

    return normalized;
  }

  function getLatestContinueSlot(slots) {
    const values = Object.values(slots || {});
    if (values.length === 0) {
      return null;
    }

    values.sort((left, right) => {
      const leftTime = left && left.updatedAtUtc ? Date.parse(left.updatedAtUtc) : 0;
      const rightTime = right && right.updatedAtUtc ? Date.parse(right.updatedAtUtc) : 0;
      if (rightTime !== leftTime) {
        return rightTime - leftTime;
      }

      const leftKey = (left && left.romKey) || '';
      const rightKey = (right && right.romKey) || '';
      return leftKey.localeCompare(rightKey);
    });

    return values[0] || null;
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
    const defaults = createDefaultSave();
    const preferences = save.Preferences || {};
    const merged = {
      ...defaults,
      ...save,
      UnlockedFeatures: {
        ...defaults.UnlockedFeatures,
        ...(save.UnlockedFeatures || {})
      }
    };

    merged.SavestatesUnlocked = Boolean(save.SavestatesUnlocked || merged.UnlockedFeatures.Savestates);
    merged.RtcUnlocked = Boolean(save.RtcUnlocked || merged.UnlockedFeatures.RTC);
    merged.GhUnlocked = Boolean(save.GhUnlocked || merged.UnlockedFeatures.GH);
    merged.ImagineUnlocked = Boolean(save.ImagineUnlocked || merged.UnlockedFeatures.Imagine);
    merged.DebugUnlocked = Boolean(save.DebugUnlocked || merged.UnlockedFeatures.Debug);

    merged.UnlockedFeatures = {
      Savestates: merged.SavestatesUnlocked,
      RTC: merged.RtcUnlocked,
      GH: merged.GhUnlocked,
      Imagine: merged.ImagineUnlocked,
      Debug: merged.DebugUnlocked
    };

    merged.PreferredCpuId = merged.PreferredCpuId || preferences.CPU || defaults.PreferredCpuId;
    merged.PreferredPpuId = merged.PreferredPpuId || preferences.PPU || defaults.PreferredPpuId;
    merged.PreferredApuId = merged.PreferredApuId || preferences.APU || defaults.PreferredApuId;
    merged.PreferredShaderId = merged.PreferredShaderId || preferences.Shader || preferences.SHADER || defaults.PreferredShaderId;
    merged.Preferences = {
      ...(preferences || {}),
      CPU: merged.PreferredCpuId,
      PPU: merged.PreferredPpuId,
      APU: merged.PreferredApuId,
      Shader: merged.PreferredShaderId,
      SHADER: merged.PreferredShaderId
    };
    merged.AllCoresUnlockedCongrats = Boolean(save.AllCoresUnlockedCongrats);

    merged.ContinueSlots = normalizeContinueSlots(save?.ContinueSlots, merged);

    const latestContinueSlot = getLatestContinueSlot(merged.ContinueSlots);
    if (latestContinueSlot) {
      merged.PendingDeckContinue = true;
      merged.PendingDeckContinueRom = latestContinueSlot.romKey;
      merged.PendingDeckContinueTitle = latestContinueSlot.title || latestContinueSlot.romKey;
      merged.PendingDeckContinueAtUtc = latestContinueSlot.updatedAtUtc || null;
    } else {
      merged.PendingDeckContinue = false;
      merged.PendingDeckContinueRom = null;
      merged.PendingDeckContinueTitle = null;
      merged.PendingDeckContinueAtUtc = null;
    }

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

        if (window.nesInterop && typeof window.nesInterop.idbGetItem === 'function') {
          const data = await window.nesInterop.idbGetItem(STORAGE_KEY);
          if (data) {
            save = JSON.parse(data);
          }
        }

        if (!save) {
          const legacyData = localStorage.getItem(LEGACY_STORAGE_KEY) || localStorage.getItem(STORAGE_KEY);
          if (legacyData) {
            save = JSON.parse(legacyData);
            try {
              if (window.nesInterop && typeof window.nesInterop.idbSetItem === 'function') {
                await window.nesInterop.idbSetItem(STORAGE_KEY, JSON.stringify(save));
              }
              localStorage.removeItem(LEGACY_STORAGE_KEY);
            } catch (migrationError) {
              console.warn('[gameSave] Legacy save migration warning:', migrationError);
            }
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

        if (window.nesInterop && typeof window.nesInterop.idbSetItem === 'function') {
          await window.nesInterop.idbSetItem(STORAGE_KEY, JSON.stringify(validatedSave));
          try {
            localStorage.removeItem(LEGACY_STORAGE_KEY);
          } catch {
            // ignore legacy cleanup failures
          }
          return true;
        }

        localStorage.setItem(STORAGE_KEY, JSON.stringify(validatedSave));
        return true;
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
