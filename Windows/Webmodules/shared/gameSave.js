// gameSave.js - Centralized game save management for all webmodules
// Provides consistent save structure and unified access across Home, Options, Story, Continue, and Cores pages

(function() {
  'use strict';

  const STORAGE_KEY = 'game_save_v1';
  const LEGACY_STORAGE_KEY = 'brokenNesGameSave';
  const VOLUME_KEY = 'brokenNesAudioVolumes';
  const DEFAULT_UNLOCKED_WEBMODULES = ['Home', 'Continue', 'DeckBuilder', 'Cores', 'Options', 'Story', 'RomManager', 'HexEditor'];
  const DEFAULT_UNLOCKED_BACKGROUNDS = ['Gradient (Default)', 'None (Black)'];
  const DEFAULT_UNLOCKED_NULL_PROVIDERS = ['Static', 'Void'];
  const PROGRESSION_MILESTONE_MODULES = ['GlitchHarvester', 'TimeJump', 'CorruptionSlop', 'ImagineBug'];

  function normalizeBackgroundId(value) {
    const trimmed = typeof value === 'string' ? value.trim() : '';
    if (!trimmed) {
      return '';
    }

    if (/^(gradient|gradient \(default\)|staticgradient)$/i.test(trimmed)) {
      return 'Gradient (Default)';
    }

    if (/^(black|none|none \(black\))$/i.test(trimmed)) {
      return 'None (Black)';
    }

    return trimmed;
  }

  function normalizeStringList(values, defaults, normalizer) {
    const result = [];
    const normalizeValue = typeof normalizer === 'function'
      ? normalizer
      : value => (typeof value === 'string' ? value.trim() : '');

    function append(source) {
      (source || []).forEach(value => {
        if (typeof value !== 'string') {
          return;
        }

        const trimmed = normalizeValue(value);
        if (!trimmed) {
          return;
        }

        if (!result.some(existing => existing.toLowerCase() === trimmed.toLowerCase())) {
          result.push(trimmed);
        }
      });
    }

    append(defaults);
    append(values);
    return result;
  }

  function normalizePreferredValue(value, unlockedValues, fallback, normalizer) {
    const normalizeValue = typeof normalizer === 'function'
      ? normalizer
      : entry => (typeof entry === 'string' ? entry.trim() : '');

    if (typeof value === 'string') {
      const normalizedValue = normalizeValue(value);
      const match = (unlockedValues || []).find(entry => entry.toLowerCase() === normalizedValue.toLowerCase());
      if (match) {
        return match;
      }
    }

    const normalizedFallback = normalizeValue(fallback);
    const fallbackMatch = (unlockedValues || []).find(entry => entry.toLowerCase() === String(normalizedFallback).toLowerCase());
    if (fallbackMatch) {
      return fallbackMatch;
    }

    return (unlockedValues || [normalizedFallback])[0] || normalizedFallback;
  }

  function normalizePendingUnlocks(pendingUnlocks) {
    return (Array.isArray(pendingUnlocks) ? pendingUnlocks : []).map(bundle => ({
      id: typeof bundle?.id === 'string' && bundle.id.trim() ? bundle.id.trim() : `reward-${Date.now()}-${Math.random().toString(16).slice(2)}`,
      source: typeof bundle?.source === 'string' ? bundle.source : '',
      achievementId: typeof bundle?.achievementId === 'string' ? bundle.achievementId : null,
      levelIndex: Number.isFinite(bundle?.levelIndex) ? bundle.levelIndex : null,
      createdAtUtc: bundle?.createdAtUtc || new Date().toISOString(),
      presented: Boolean(bundle?.presented),
      items: (Array.isArray(bundle?.items) ? bundle.items : []).map(item => ({
        id: typeof item?.id === 'string' ? item.id : '',
        type: typeof item?.type === 'string' ? item.type : '',
        title: typeof item?.title === 'string' ? item.title : null,
        subtitle: typeof item?.subtitle === 'string' ? item.subtitle : null,
        description: typeof item?.description === 'string' ? item.description : null,
        canEquip: Boolean(item?.canEquip),
        isEquipped: Boolean(item?.isEquipped),
        equipAction: typeof item?.equipAction === 'string' ? item.equipAction : null
      }))
    }));
  }

  function mergePendingUnlocks(primary, secondary) {
    const merged = [];
    const seen = new Set();

    function append(source) {
      normalizePendingUnlocks(source).forEach(bundle => {
        const key = String(bundle.id || '').trim().toLowerCase();
        if (key && seen.has(key)) {
          return;
        }

        if (key) {
          seen.add(key);
        }

        merged.push(bundle);
      });
    }

    append(primary);
    append(secondary);
    return merged;
  }

  function mergeSaveSnapshots(primary, secondary) {
    const left = migrateSave(primary);
    const right = migrateSave(secondary);

    const merged = {
      ...left,
      ...right,
      Level: Math.max(Number(left.Level) || 1, Number(right.Level) || 1),
      LevelCleared: Boolean(left.LevelCleared || right.LevelCleared),
      Achievements: normalizeStringList(right.Achievements, left.Achievements),
      ownedCpuIds: normalizeStringList(right.ownedCpuIds || right.OwnedCpuIds, left.ownedCpuIds || left.OwnedCpuIds),
      ownedPpuIds: normalizeStringList(right.ownedPpuIds || right.OwnedPpuIds, left.ownedPpuIds || left.OwnedPpuIds),
      ownedApuIds: normalizeStringList(right.ownedApuIds || right.OwnedApuIds, left.ownedApuIds || left.OwnedApuIds),
      ownedClockIds: normalizeStringList(right.ownedClockIds || right.OwnedClockIds, left.ownedClockIds || left.OwnedClockIds),
      ownedShaderIds: normalizeStringList(right.ownedShaderIds || right.OwnedShaderIds, left.ownedShaderIds || left.OwnedShaderIds),
      SavestatesUnlocked: Boolean(left.SavestatesUnlocked || right.SavestatesUnlocked),
      RtcUnlocked: Boolean(left.RtcUnlocked || right.RtcUnlocked),
      GhUnlocked: Boolean(left.GhUnlocked || right.GhUnlocked),
      ImagineUnlocked: Boolean(left.ImagineUnlocked || right.ImagineUnlocked),
      DebugUnlocked: Boolean(left.DebugUnlocked || right.DebugUnlocked),
      SeenStory: Boolean(left.SeenStory || right.SeenStory),
      UnlockedWebmodules: normalizeStringList(right.UnlockedWebmodules, left.UnlockedWebmodules),
      UnlockedBackgrounds: normalizeStringList(right.UnlockedBackgrounds, left.UnlockedBackgrounds, normalizeBackgroundId),
      UnlockedNullProviders: normalizeStringList(right.UnlockedNullProviders, left.UnlockedNullProviders),
      PendingUnlocks: mergePendingUnlocks(left.PendingUnlocks, right.PendingUnlocks),
      ContinueSlots: {
        ...(left.ContinueSlots || {}),
        ...(right.ContinueSlots || {})
      },
      PendingDeckContinue: Boolean(left.PendingDeckContinue || right.PendingDeckContinue),
      PendingDeckContinueRom: right.PendingDeckContinueRom || left.PendingDeckContinueRom || null,
      PendingDeckContinueTitle: right.PendingDeckContinueTitle || left.PendingDeckContinueTitle || null,
      PendingDeckContinueAtUtc: right.PendingDeckContinueAtUtc || left.PendingDeckContinueAtUtc || null,
      UnderConstructionAcknowledged: Boolean(left.UnderConstructionAcknowledged || right.UnderConstructionAcknowledged),
      AllCoresUnlockedCongrats: Boolean(left.AllCoresUnlockedCongrats || right.AllCoresUnlockedCongrats)
    };

    const mergedPreferences = {
      ...(left.Preferences || {}),
      ...(right.Preferences || {})
    };

    merged.PreferredCpuId = right.PreferredCpuId || left.PreferredCpuId || mergedPreferences.CPU || 'FMC';
    merged.PreferredPpuId = right.PreferredPpuId || left.PreferredPpuId || mergedPreferences.PPU || 'FMC';
    merged.PreferredApuId = right.PreferredApuId || left.PreferredApuId || mergedPreferences.APU || 'FMC';
    merged.PreferredShaderId = right.PreferredShaderId || left.PreferredShaderId || mergedPreferences.Shader || mergedPreferences.SHADER || 'PX';
    merged.PreferredBackgroundId = right.PreferredBackgroundId || left.PreferredBackgroundId || 'Gradient (Default)';
    merged.PreferredNullProviderId = right.PreferredNullProviderId || left.PreferredNullProviderId || 'Static';

    return migrateSave(merged);
  }

  function addUniqueValue(list, value, normalizer) {
    if (typeof value !== 'string') {
      return;
    }

    const normalizeValue = typeof normalizer === 'function'
      ? normalizer
      : entry => (typeof entry === 'string' ? entry.trim() : '');
    const normalized = normalizeValue(value);
    if (!normalized) {
      return;
    }

    if (!Array.isArray(list)) {
      return;
    }

    if (!list.some(existing => String(existing).toLowerCase() === normalized.toLowerCase())) {
      list.push(normalized);
    }
  }

  function pickFirstDefined(...values) {
    for (const value of values) {
      if (value !== undefined) {
        return value;
      }
    }

    return undefined;
  }

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
      UnlockedWebmodules: DEFAULT_UNLOCKED_WEBMODULES.slice(),
      UnlockedBackgrounds: DEFAULT_UNLOCKED_BACKGROUNDS.slice(),
      UnlockedNullProviders: DEFAULT_UNLOCKED_NULL_PROVIDERS.slice(),
      PreferredBackgroundId: 'Gradient (Default)',
      PreferredNullProviderId: 'Static',
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
      SelectedRomKey: null,
      ContinueSlots: {},
      PendingUnlocks: [],
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

    const normalizedSave = {
      ...save,
      Level: pickFirstDefined(save.Level, save.level),
      LevelCleared: pickFirstDefined(save.LevelCleared, save.levelCleared),
      Achievements: pickFirstDefined(save.Achievements, save.achievements),
      SavestatesUnlocked: pickFirstDefined(save.SavestatesUnlocked, save.savestatesUnlocked),
      RtcUnlocked: pickFirstDefined(save.RtcUnlocked, save.rtcUnlocked),
      GhUnlocked: pickFirstDefined(save.GhUnlocked, save.ghUnlocked),
      ImagineUnlocked: pickFirstDefined(save.ImagineUnlocked, save.imagineUnlocked),
      DebugUnlocked: pickFirstDefined(save.DebugUnlocked, save.debugUnlocked),
      SeenStory: pickFirstDefined(save.SeenStory, save.seenStory),
      PreferredCpuId: pickFirstDefined(save.PreferredCpuId, save.preferredCpuId),
      PreferredPpuId: pickFirstDefined(save.PreferredPpuId, save.preferredPpuId),
      PreferredApuId: pickFirstDefined(save.PreferredApuId, save.preferredApuId),
      PreferredShaderId: pickFirstDefined(save.PreferredShaderId, save.preferredShaderId),
      UnlockedWebmodules: pickFirstDefined(save.UnlockedWebmodules, save.unlockedWebmodules),
      UnlockedBackgrounds: pickFirstDefined(save.UnlockedBackgrounds, save.unlockedBackgrounds),
      UnlockedNullProviders: pickFirstDefined(save.UnlockedNullProviders, save.unlockedNullProviders),
      PreferredBackgroundId: pickFirstDefined(save.PreferredBackgroundId, save.preferredBackgroundId),
      PreferredNullProviderId: pickFirstDefined(save.PreferredNullProviderId, save.preferredNullProviderId),
      PendingUnlocks: pickFirstDefined(save.PendingUnlocks, save.pendingUnlocks),
      PendingDeckContinue: pickFirstDefined(save.PendingDeckContinue, save.pendingDeckContinue),
      PendingDeckContinueRom: pickFirstDefined(save.PendingDeckContinueRom, save.pendingDeckContinueRom),
      PendingDeckContinueTitle: pickFirstDefined(save.PendingDeckContinueTitle, save.pendingDeckContinueTitle),
      PendingDeckContinueAtUtc: pickFirstDefined(save.PendingDeckContinueAtUtc, save.pendingDeckContinueAtUtc),
      SelectedRomKey: pickFirstDefined(save.SelectedRomKey, save.selectedRomKey),
      ContinueSlots: pickFirstDefined(save.ContinueSlots, save.continueSlots),
      UnderConstructionAcknowledged: pickFirstDefined(save.UnderConstructionAcknowledged, save.underConstructionAcknowledged),
      AllCoresUnlockedCongrats: pickFirstDefined(save.AllCoresUnlockedCongrats, save.allCoresUnlockedCongrats),
      MasqueradeRomToGameId: pickFirstDefined(save.MasqueradeRomToGameId, save.masqueradeRomToGameId),
      ownedCpuIds: pickFirstDefined(save.ownedCpuIds, save.OwnedCpuIds),
      ownedPpuIds: pickFirstDefined(save.ownedPpuIds, save.OwnedPpuIds),
      ownedApuIds: pickFirstDefined(save.ownedApuIds, save.OwnedApuIds),
      ownedClockIds: pickFirstDefined(save.ownedClockIds, save.OwnedClockIds),
      ownedShaderIds: pickFirstDefined(save.ownedShaderIds, save.OwnedShaderIds)
    };

    delete normalizedSave.level;
    delete normalizedSave.levelCleared;
    delete normalizedSave.achievements;
    delete normalizedSave.savestatesUnlocked;
    delete normalizedSave.rtcUnlocked;
    delete normalizedSave.ghUnlocked;
    delete normalizedSave.imagineUnlocked;
    delete normalizedSave.debugUnlocked;
    delete normalizedSave.seenStory;
    delete normalizedSave.preferredCpuId;
    delete normalizedSave.preferredPpuId;
    delete normalizedSave.preferredApuId;
    delete normalizedSave.preferredShaderId;
    delete normalizedSave.unlockedWebmodules;
    delete normalizedSave.unlockedBackgrounds;
    delete normalizedSave.unlockedNullProviders;
    delete normalizedSave.preferredBackgroundId;
    delete normalizedSave.preferredNullProviderId;
    delete normalizedSave.pendingUnlocks;
    delete normalizedSave.pendingDeckContinue;
    delete normalizedSave.pendingDeckContinueRom;
    delete normalizedSave.pendingDeckContinueTitle;
    delete normalizedSave.pendingDeckContinueAtUtc;
    delete normalizedSave.selectedRomKey;
    delete normalizedSave.continueSlots;
    delete normalizedSave.underConstructionAcknowledged;
    delete normalizedSave.allCoresUnlockedCongrats;
    delete normalizedSave.masqueradeRomToGameId;
    delete normalizedSave.OwnedCpuIds;
    delete normalizedSave.OwnedPpuIds;
    delete normalizedSave.OwnedApuIds;
    delete normalizedSave.OwnedClockIds;
    delete normalizedSave.OwnedShaderIds;

    // Ensure all required properties exist
    const defaults = createDefaultSave();
    const preferences = normalizedSave.Preferences || {};
    const merged = {
      ...defaults,
      ...normalizedSave,
      UnlockedFeatures: {
        ...defaults.UnlockedFeatures,
        ...(normalizedSave.UnlockedFeatures || {})
      }
    };

    merged.SavestatesUnlocked = Boolean(normalizedSave.SavestatesUnlocked || merged.UnlockedFeatures.Savestates);
    merged.RtcUnlocked = Boolean(normalizedSave.RtcUnlocked || merged.UnlockedFeatures.RTC);
    merged.GhUnlocked = Boolean(normalizedSave.GhUnlocked || merged.UnlockedFeatures.GH);
    merged.ImagineUnlocked = Boolean(normalizedSave.ImagineUnlocked || merged.UnlockedFeatures.Imagine);
    merged.DebugUnlocked = Boolean(normalizedSave.DebugUnlocked || merged.UnlockedFeatures.Debug);

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
    merged.UnlockedWebmodules = normalizeStringList(normalizedSave.UnlockedWebmodules, DEFAULT_UNLOCKED_WEBMODULES);
    merged.UnlockedBackgrounds = normalizeStringList(normalizedSave.UnlockedBackgrounds, DEFAULT_UNLOCKED_BACKGROUNDS, normalizeBackgroundId);
    merged.UnlockedNullProviders = normalizeStringList(normalizedSave.UnlockedNullProviders, DEFAULT_UNLOCKED_NULL_PROVIDERS);
    merged.PreferredBackgroundId = normalizePreferredValue(normalizedSave.PreferredBackgroundId, merged.UnlockedBackgrounds, defaults.PreferredBackgroundId, normalizeBackgroundId);
    merged.PreferredNullProviderId = normalizePreferredValue(normalizedSave.PreferredNullProviderId, merged.UnlockedNullProviders, defaults.PreferredNullProviderId);
    merged.PendingUnlocks = normalizePendingUnlocks(normalizedSave.PendingUnlocks);
    merged.Preferences = {
      ...(preferences || {}),
      CPU: merged.PreferredCpuId,
      PPU: merged.PreferredPpuId,
      APU: merged.PreferredApuId,
      Shader: merged.PreferredShaderId,
      SHADER: merged.PreferredShaderId
    };
    merged.AllCoresUnlockedCongrats = Boolean(normalizedSave.AllCoresUnlockedCongrats);

    merged.ContinueSlots = normalizeContinueSlots(normalizedSave?.ContinueSlots, merged);

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

  async function persistLocalCopy(save) {
    if (window.nesInterop && typeof window.nesInterop.idbSetItem === 'function') {
      await window.nesInterop.idbSetItem(STORAGE_KEY, JSON.stringify(save));
    } else {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(save));
    }

    try {
      localStorage.removeItem(LEGACY_STORAGE_KEY);
    } catch {
      // ignore legacy cleanup failures
    }
  }

  async function loadFromApi() {
    if (!window.webapi || typeof window.webapi.request !== 'function') {
      return null;
    }

    const response = await window.webapi.request('/api/save', { cacheBust: true, noCache: true });
    if (!response || response.success === false) {
      return null;
    }

    return response;
  }

  async function loadLocalSnapshot() {
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

    return save;
  }

  async function saveToApi(save) {
    if (!window.webapi || typeof window.webapi.request !== 'function') {
      return null;
    }

    const response = await window.webapi.request('/api/save', {
      method: 'POST',
      json: save,
      noCache: true
    });

    if (!response || response.success === false) {
      return null;
    }

    return response;
  }

  async function resetViaApi() {
    if (!window.webapi || typeof window.webapi.request !== 'function') {
      throw new Error('Web API is not available');
    }

    const response = await window.webapi.request('/api/save/reset', {
      method: 'POST',
      noCache: true
    });

    if (!response || response.success === false) {
      throw new Error(response?.error || 'Failed to reset canonical save');
    }

    return response;
  }

  const gameSave = {
    /**
     * Load game save from storage
     * @returns {Promise<Object>} - Game save object
     */
    async load() {
      try {
        const apiSave = await loadFromApi();
        const localSave = await loadLocalSnapshot();

        if (apiSave && localSave) {
          const mergedSave = mergeSaveSnapshots(apiSave, localSave);
          await persistLocalCopy(mergedSave);
          const syncedApiSave = await saveToApi(mergedSave);
          return migrateSave(syncedApiSave || mergedSave);
        }

        if (apiSave) {
          const migratedApiSave = migrateSave(apiSave);
          await persistLocalCopy(migratedApiSave);
          return migratedApiSave;
        }

        return migrateSave(localSave);
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

        await persistLocalCopy(validatedSave);

        const savedApiCopy = await saveToApi(validatedSave);
        if (savedApiCopy) {
          const canonicalSave = migrateSave(savedApiCopy);
          await persistLocalCopy(canonicalSave);
          return true;
        }

        console.warn('[gameSave] API save unavailable, keeping local snapshot until next merge');
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
      const resetSave = migrateSave(await resetViaApi());
      await persistLocalCopy(resetSave);
      return resetSave;
    },

    /**
     * Unlock all cores (for debugging/cheats)
     * @param {Object} save - Current save object
     * @returns {Object} - Updated save object
     */
    unlockAllCores(save) {
      const updated = migrateSave({ ...save });
      
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

      updated.SavestatesUnlocked = true;
      updated.RtcUnlocked = true;
      updated.GhUnlocked = true;
      updated.ImagineUnlocked = true;
      updated.DebugUnlocked = true;

      updated.UnlockedWebmodules = normalizeStringList(
        [...(updated.UnlockedWebmodules || []), ...PROGRESSION_MILESTONE_MODULES],
        DEFAULT_UNLOCKED_WEBMODULES
      );
      updated.PendingUnlocks = [];

      return updated;
    },

    unlockFeature(save, featureName) {
      const updated = migrateSave({ ...save });
      const feature = String(featureName || '').trim().toLowerCase();

      switch (feature) {
        case 'savestates':
          updated.SavestatesUnlocked = true;
          addUniqueValue(updated.UnlockedWebmodules, 'TimeJump');
          break;
        case 'rtc':
          updated.RtcUnlocked = true;
          addUniqueValue(updated.UnlockedWebmodules, 'GlitchHarvester');
          break;
        case 'gh':
          updated.GhUnlocked = true;
          addUniqueValue(updated.UnlockedWebmodules, 'GlitchHarvester');
          break;
        case 'imagine':
          updated.ImagineUnlocked = true;
          addUniqueValue(updated.UnlockedWebmodules, 'ImagineBug');
          break;
        case 'debug':
          updated.DebugUnlocked = true;
          break;
      }

      updated.UnlockedWebmodules = normalizeStringList(updated.UnlockedWebmodules, DEFAULT_UNLOCKED_WEBMODULES);
      return updated;
    },

    /**
     * Count total owned cores
     * @param {Object} save - Game save object
     * @returns {number} - Total number of owned cores
     */
    countOwnedCores(save) {
      if (!save) return 0;

      function countUnique(values) {
        return new Set((Array.isArray(values) ? values : [])
          .filter(value => typeof value === 'string' && value.trim())
          .map(value => value.trim().toUpperCase())).size;
      }
      
      const cpu = (save.ownedCpuIds || []).length;
      const ppu = (save.ownedPpuIds || []).length;
      const apu = (save.ownedApuIds || []).length;
      const clock = (save.ownedClockIds || []).length;
      const shader = (save.ownedShaderIds || []).length;
      const webmodules = countUnique(save.UnlockedWebmodules);
      const backgrounds = countUnique(save.UnlockedBackgrounds);
      const nullProviders = countUnique(save.UnlockedNullProviders);
      
      return cpu + ppu + apu + clock + shader + webmodules + backgrounds + nullProviders;
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
