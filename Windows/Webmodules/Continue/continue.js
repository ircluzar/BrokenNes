// continue.js - Continue/Deck Builder logic (standalone webmodule)

(function() {
  'use strict';

  const DEFAULT_DB_URL = '../shared/models/default-db.json';
  const ROM_STORAGE_DB_NAME = 'nesStorage';
  const ROM_STORAGE_DB_VERSION = 1;
  const ROM_STORAGE_STORE = 'roms';
  const LEGACY_ROM_KEY_PREFIX = 'rom_';
  const WORKFLOW_LAUNCH_KEY = 'brokenNes.workflow.launch';
  const WORKFLOW_RETURN_KEY = 'brokenNes.workflow.return';
  const WORKFLOW_ROM_CACHE_KEY = 'brokenNes.workflow.rom';
  const UI_SFX = {
    firstClear: 'SFX01.mp3',
    levelAdvance: 'SFX09.mp3',
    modalOpen: 'SFX04.mp3',
    select: 'SFX03.mp3',
    toggle: 'SFX02.mp3',
    launch: 'SFX07.mp3'
  };
  const OWNED_KEY_BY_DOMAIN = {
    CPU: 'ownedCpuIds',
    PPU: 'ownedPpuIds',
    APU: 'ownedApuIds',
    CLOCK: 'ownedClockIds',
    SHADER: 'ownedShaderIds'
  };
  const PREFERRED_KEY_BY_DOMAIN = {
    CPU: 'PreferredCpuId',
    PPU: 'PreferredPpuId',
    APU: 'PreferredApuId',
    SHADER: 'PreferredShaderId'
  };
  const PREFERENCE_NAME_BY_DOMAIN = {
    CPU: 'CPU',
    PPU: 'PPU',
    APU: 'APU',
    SHADER: 'Shader'
  };
  const STARTER_CORE_BY_DOMAIN = {
    CPU: 'FMC',
    PPU: 'FMC',
    APU: 'FMC',
    CLOCK: 'FMC',
    SHADER: 'PX'
  };

  // State
  let gameSave = null;
  let currentLevel = 1;
  let stars = 0;
  let requiredStars = 5;
  let levelCleared = false;
  
  // Deck state
  let selectedCpu = null;
  let selectedPpu = null;
  let selectedApu = null;
  let selectedShader = null;
  
  let enforcedCpu = null;
  let enforcedPpu = null;
  let enforcedApu = null;
  let enforcedShader = null;
  
  // ROM state
  let selectedGameId = null;
  let romRows = [];
  let gameAchievements = [];
  let cartridgeCollapsed = false;
  let levelRecord = null;
  let arrivalInProgress = false;
  
  const coreCatalog = {
    CPU: [],
    PPU: [],
    APU: [],
    CLOCK: [],
    SHADER: []
  };
  const fallbackCoreData = {
    CPU: ['FMC', 'LOW', 'LW2', 'SPD', 'EIL', 'Z80'],
    PPU: ['FMC', 'LOW', 'LQ', 'SPD', 'BFR', 'CUBE', 'CUBEX', 'EIL'],
    APU: ['FMC', 'LOW', 'LQ', 'LQ2', 'QLOW', 'QLQ', 'QLQ2', 'QN', 'SPD', 'SPD2', 'WF', 'EIL', 'MNES'],
    CLOCK: ['FMC', 'TRB', 'CLR'],
    SHADER: ['PX', '16B', 'BLD', 'BUMP', 'CCC', 'CNMA', 'CRY', 'CRZ', 'DOT', 'EXE', 'HUE', 'LAT', 'LCD', 'LSD', 'MSH', 'MUSK', 'RF', 'RGBX', 'SPK', 'TRI', 'TTF', 'TV', 'VHS', 'WARM', 'WTR']
  };
  const coreLookup = new Map();
  const cardRecordLookup = new Map();
  const cardSvgCache = new Map();
  let progressionRoster = null;
  let currentPreviewToken = 0;
  let cardRecords = [];
  let rewardModalState = {
    items: [],
    featureUnlocks: [],
    showCongrats: false,
    showAllCores: false,
    pendingRewardIds: [],
    title: 'New Cards Unlocked',
    kicker: 'Level Intermission',
    copy: '',
    autoCloseTimer: 0
  };

  // Initialize on page load
  window.addEventListener('DOMContentLoaded', init);

  async function init() {
    try {
      // Start pixel background
      if (window.homePixelBgEnsure) {
        window.homePixelBgEnsure();
      }

      // Load game save first (needed for currentLevel)
      await loadGameSave();

      // Load live core metadata and level rules before first render.
      await loadCoreCatalog();
      await loadCardRecords();

      // Load level data
      await loadLevel();

      // Initialize ROM list
      await initializeRomList();

      restoreSelectedGame();

      await consumeWorkflowReturn();

      if (document.getElementById('unlockModal')?.style.display !== 'flex') {
        await presentPendingUnlocks();
      }

      // Update UI
      updateUI();

      // Initialize audio after level is loaded
      initAudio();

      // Setup event listeners
      setupEventListeners();
    } catch (error) {
      console.error('[Continue] Initialization error:', error);
    }
  }

  function initAudio() {
    try {
      console.log('[Continue] Initializing audio, currentLevel:', currentLevel);
      // Select DeckBuilder music based on current level (1-4)
      // If level > 4, use modulo to cycle through tracks
      const trackNumber = ((currentLevel - 1) % 4) + 1;
      const musicTrack = `DeckBuilder${trackNumber}.mp3`;
      console.log('[Continue] Requesting music:', musicTrack);
      
      if (window.webapi?.audio?.requestMusic) {
        window.webapi.audio.requestMusic(musicTrack, true, 800).then(() => {
          console.log('[Continue] Music request sent successfully:', musicTrack);
        }).catch(err => {
          console.warn('[Continue] Music request failed:', err);
        });
      } else {
        console.error('[Continue] webapi.audio.requestMusic not available');
      }
    } catch (error) {
      console.warn('[Continue] Audio init error:', error);
    }
  }

  function readWorkflowPayload(key) {
    try {
      const raw = localStorage.getItem(key);
      return raw ? JSON.parse(raw) : null;
    } catch (error) {
      console.warn('[Continue] Failed to read workflow payload:', error);
      return null;
    }
  }

  function writeWorkflowPayload(key, payload) {
    try {
      localStorage.setItem(key, JSON.stringify(payload));
      return true;
    } catch (error) {
      console.warn('[Continue] Failed to write workflow payload:', error);
      return false;
    }
  }

  function clearWorkflowPayload(key) {
    try {
      localStorage.removeItem(key);
    } catch (error) {
      console.warn('[Continue] Failed to clear workflow payload:', error);
    }
  }

  function playUiSfx(filename, options = {}) {
    if (!filename || !window.webapi?.audio?.playSfx) {
      return Promise.resolve(false);
    }

    const key = options.key || filename;
    const cooldownMs = Number.isFinite(options.cooldownMs) ? options.cooldownMs : 90;
    playUiSfx.lastPlayedAt ??= new Map();

    const now = performance.now();
    const lastPlayedAt = playUiSfx.lastPlayedAt.get(key) || 0;
    if (now - lastPlayedAt < cooldownMs) {
      return Promise.resolve(false);
    }

    playUiSfx.lastPlayedAt.set(key, now);
    return window.webapi.audio.playSfx(filename).then(() => true).catch(error => {
      console.warn(`[Continue] Failed to play SFX ${filename}:`, error);
      return false;
    });
  }

  function restoreSelectedGame() {
    if (selectedGameId && romRows.some(row => row.id === selectedGameId)) {
      return;
    }

    selectedGameId = null;
  }

  function getContinueSlots() {
    const slots = gameSave?.ContinueSlots;
    return slots && typeof slots === 'object' ? slots : {};
  }

  function getContinueSlotForRom(romKey) {
    const normalizedKey = normalizeRomStorageName(romKey);
    if (!normalizedKey) {
      return null;
    }

    return getContinueSlots()[normalizedKey] || null;
  }

  function hasTrustedContinueSelected() {
    const selectedGame = romRows.find(row => row.id === selectedGameId);
    if (!selectedGame) {
      return false;
    }

    return Boolean(getContinueSlotForRom(selectedGame.romKey));
  }

  function buildLaunchPayload(mode) {
    const selectedGame = romRows.find(row => row.id === selectedGameId);
    if (!selectedGame) {
      return null;
    }

    return {
      mode,
      level: currentLevel,
      previousStarCount: stars,
      romKey: selectedGame.romKey,
      gameId: selectedGame.id,
      title: selectedGame.title,
      subtitle: selectedGame.subtitle || '',
      createdAt: new Date().toISOString(),
      cores: {
        cpuId: enforcedCpu || selectedCpu,
        ppuId: enforcedPpu || selectedPpu,
        apuId: enforcedApu || selectedApu,
        shaderId: enforcedShader || selectedShader
      }
    };
  }

  async function getStoredRomByName(romName) {
    const normalizedTarget = normalizeRomStorageName(romName);
    const roms = await getStoredRoms();
    if (!Array.isArray(roms)) {
      return null;
    }

    return roms.find(rom => normalizeRomStorageName(rom && rom.name) === normalizedTarget) || null;
  }

  function setArrivalText(id, value) {
    const element = document.getElementById(id);
    if (element) {
      element.textContent = value;
    }
  }

  function showArrivalOverlay() {
    const overlay = document.getElementById('arrivalOverlay');
    if (overlay) {
      overlay.hidden = false;
    }
  }

  function hideArrivalOverlay() {
    const overlay = document.getElementById('arrivalOverlay');
    if (overlay) {
      overlay.hidden = true;
    }
  }

  function sleep(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
  }

  async function animateNumber(id, from, to, durationMs) {
    const element = document.getElementById(id);
    if (!element) {
      return;
    }

    if (from === to) {
      element.textContent = String(to);
      return;
    }

    const start = performance.now();
    return new Promise(resolve => {
      function step(now) {
        const progress = Math.min(1, (now - start) / durationMs);
        const value = Math.round(from + ((to - from) * progress));
        element.textContent = String(value);
        if (progress < 1) {
          requestAnimationFrame(step);
          return;
        }

        resolve();
      }

      requestAnimationFrame(step);
    });
  }

  async function consumeWorkflowReturn() {
    const payload = readWorkflowPayload(WORKFLOW_RETURN_KEY);
    if (!payload) {
      return;
    }

    clearWorkflowPayload(WORKFLOW_RETURN_KEY);

    if (payload.romKey) {
      const match = romRows.find(row => normalizeRomStorageName(row.romKey) === normalizeRomStorageName(payload.romKey));
      if (match) {
        selectGame(match.id, { silent: true });
      }
    }

    const shouldShowArrival = payload.showArrival === true
      || Boolean(payload.achievementId || payload.achievementTitle || payload.firstClear);
    if (!shouldShowArrival) {
      return;
    }

    await runArrivalSequence(payload);
  }

  async function runArrivalSequence(payload) {
    arrivalInProgress = true;
    showArrivalOverlay();

    const previousStars = Number.isFinite(payload.previousStarCount) ? payload.previousStarCount : Math.max(0, stars - 1);
    const previousLevel = Number.isFinite(payload.previousLevel) ? payload.previousLevel : currentLevel;
    const achievementTitle = payload.achievementTitle || payload.title || 'Achievement unlocked';

    setArrivalText('arrivalTitle', achievementTitle);
    setArrivalText('arrivalStatus', payload.firstClear ? 'Deck challenge cleared. Banking your star count…' : 'Star count updated. Returning to deck builder…');
    setArrivalText('arrivalStarsValue', String(previousStars));
    setArrivalText('arrivalLevelValue', String(previousLevel));
    setArrivalText('arrivalReveal', payload.firstClear ? 'First clear confirmed for this level.' : 'Achievement saved to your collection.');

    if (payload.firstClear) {
      void playUiSfx(UI_SFX.firstClear, { key: 'arrival-first-clear', cooldownMs: 300 });
    }

    await sleep(180);
    await animateNumber('arrivalStarsValue', previousStars, stars, 900);

    let levelAdvanceResult = null;

    if (levelCleared && stars >= requiredStars) {
      setArrivalText('arrivalStatus', 'Threshold reached. Unlocking the next level…');
      await sleep(500);
      levelAdvanceResult = await advanceLevel({ silent: true, deferPresentation: true });
      if (levelAdvanceResult?.advanced) {
        setArrivalText('arrivalLevelValue', `${previousLevel} -> ${currentLevel}`);
        setArrivalText('arrivalReveal', levelRecord?.cardChallenge || `Level ${currentLevel} unlocked`);
      }
    } else {
      setArrivalText('arrivalLevelValue', String(currentLevel));
      setArrivalText('arrivalReveal', levelRecord?.cardChallenge || `Level ${currentLevel}`);
    }

    await sleep(1300);
    hideArrivalOverlay();
    arrivalInProgress = false;
    const pendingBundles = await claimPendingUnlockBundles();
    if (levelAdvanceResult?.advanced || pendingBundles.length > 0) {
      await presentLevelRewards(levelAdvanceResult?.rewards, {
        pendingBundles,
        kicker: 'Achievement Return',
        title: levelAdvanceResult?.advanced ? 'Run Rewards Ready' : 'Achievement Rewards Ready',
        copy: levelAdvanceResult?.advanced
          ? 'Level rewards and queued unlocks are ready. Inspect the cards, equip what you want now, then continue the run.'
          : 'Queued rewards are ready. Inspect the cards, equip what you want now, then continue the run.'
      });
    }
    updateUI();
  }

  async function loadGameSave() {
    try {
      if (window.gameSave && typeof window.gameSave.load === 'function') {
        gameSave = await window.gameSave.load();
      } else {
        console.error('[Continue] gameSave module not available');
        gameSave = null;
      }

      if (!gameSave) {
        gameSave = await window.gameSave.load();
      }

      // Load state from save
      currentLevel = gameSave.Level || 1;
      stars = (gameSave.Achievements || []).length;
      levelCleared = gameSave.LevelCleared || false;

      applySavedSelections(gameSave);
    } catch (error) {
      console.error('[Continue] Load save error:', error);
      gameSave = null;
    }
  }

  function chooseSavedCore(domain, preferredId, ownedIds, fallbackId) {
    const owned = Array.isArray(ownedIds) ? ownedIds : [];
    const normalizedPreferred = normalizeCoreId(preferredId);
    if (normalizedPreferred && owned.some(id => normalizeCoreId(id) === normalizedPreferred)) {
      return normalizedPreferred;
    }

    const normalizedFallback = normalizeCoreId(fallbackId);
    if (normalizedFallback && owned.some(id => normalizeCoreId(id) === normalizedFallback)) {
      return normalizedFallback;
    }

    if (owned.length > 0) {
      return normalizeCoreId(owned[0]);
    }

    const fallbackList = fallbackCoreData[domain] || [];
    return fallbackList.length > 0 ? fallbackList[0] : normalizedFallback;
  }

  function normalizeCoreId(value) {
    return typeof value === 'string' && value.trim() ? value.trim().toUpperCase() : null;
  }

  async function loadCoreCatalog() {
    coreLookup.clear();
    Object.keys(coreCatalog).forEach(domain => {
      coreCatalog[domain] = [];
    });

    if (!window.webapi?.cores?.list) {
      return;
    }

    try {
      const data = await window.webapi.cores.list();
      if (!data || data.success === false) {
        console.warn('[Continue] Failed to load core catalog:', data?.error || 'Unknown error');
        return;
      }

      hydrateCoreCatalog('CPU', data.cpu);
      hydrateCoreCatalog('PPU', data.ppu);
      hydrateCoreCatalog('APU', data.apu);
      hydrateCoreCatalog('CLOCK', data.clock);
      hydrateCoreCatalog('SHADER', data.shader);
    } catch (error) {
      console.warn('[Continue] Core catalog unavailable:', error);
    }
  }

  function hydrateCoreCatalog(domain, items) {
    const normalized = Array.isArray(items)
      ? items.map(item => ({
          id: normalizeCoreId(item?.id),
          name: item?.name || item?.displayName || item?.id || '',
          description: item?.description || '',
          rating: Number.isFinite(item?.rating) ? item.rating : 0,
          category: item?.category || ''
        })).filter(item => item.id)
      : [];

    coreCatalog[domain] = normalized;
    normalized.forEach(item => {
      coreLookup.set(`${domain}:${item.id}`, item);
    });
  }

  async function loadCardRecords() {
    cardRecords = [];
    cardRecordLookup.clear();

    if (!window.continueDb?.getAll) {
      return;
    }

    try {
      await window.continueDb.open();
      const records = await window.continueDb.getAll('cards');
      if (!Array.isArray(records)) {
        return;
      }

      cardRecords = records.map(record => {
        const compositeId = typeof record?.id === 'string' ? record.id.trim() : '';
        if (!compositeId) {
          return null;
        }

        const parts = compositeId.split('_', 2);
        if (parts.length !== 2) {
          return null;
        }

        const domain = parts[0].toUpperCase();
        const id = normalizeCoreId(parts[1]);
        if (!OWNED_KEY_BY_DOMAIN[domain] || !id) {
          return null;
        }

        const normalized = {
          domain,
          id,
          type: typeof record?.type === 'string' ? record.type.trim().toUpperCase() : '',
          note: typeof record?.note === 'string' ? record.note.trim() : ''
        };

        cardRecordLookup.set(`${domain}:${id}`, normalized);
        return normalized;
      }).filter(Boolean);
    } catch (error) {
      console.warn('[Continue] Failed to load card records:', error);
    }
  }

  async function getProgressionRoster(forceRefresh = false) {
    if (!forceRefresh && progressionRoster) {
      return progressionRoster;
    }

    if (!window.webapi?.progression?.getRoster) {
      return null;
    }

    try {
      const result = await window.webapi.progression.getRoster();
      progressionRoster = result && result.success !== false ? result : null;
      return progressionRoster;
    } catch (error) {
      console.warn('[Continue] Failed to load progression roster:', error);
      progressionRoster = null;
      return null;
    }
  }

  function countUnlockedEntries(values) {
    const seen = new Set();
    (Array.isArray(values) ? values : []).forEach(value => {
      if (typeof value !== 'string') {
        return;
      }

      const normalizedValue = value.trim().toUpperCase();
      if (!normalizedValue) {
        return;
      }

      seen.add(normalizedValue);
    });

    return seen.size;
  }

  function normalizeOwnedArray(values, fallback = []) {
    const seen = new Set();
    const normalized = [];
    const source = Array.isArray(values) ? values : fallback;

    source.forEach(value => {
      const normalizedValue = normalizeCoreId(value);
      if (!normalizedValue || seen.has(normalizedValue)) {
        return;
      }

      seen.add(normalizedValue);
      normalized.push(normalizedValue);
    });

    fallback.forEach(value => {
      const normalizedValue = normalizeCoreId(value);
      if (!normalizedValue || seen.has(normalizedValue)) {
        return;
      }

      seen.add(normalizedValue);
      normalized.push(normalizedValue);
    });

    return normalized;
  }

  function syncGameSaveCompatFields(save) {
    if (!save || typeof save !== 'object') {
      return save;
    }

    save.ownedCpuIds = normalizeOwnedArray(save.ownedCpuIds, ['FMC']);
    save.ownedPpuIds = normalizeOwnedArray(save.ownedPpuIds, ['FMC']);
    save.ownedApuIds = normalizeOwnedArray(save.ownedApuIds, ['FMC']);
    save.ownedClockIds = normalizeOwnedArray(save.ownedClockIds, ['FMC']);
    save.ownedShaderIds = normalizeOwnedArray(save.ownedShaderIds, ['PX']);

    const preferences = save.Preferences && typeof save.Preferences === 'object'
      ? { ...save.Preferences }
      : {};

    save.PreferredCpuId = normalizeCoreId(save.PreferredCpuId || preferences.CPU || 'FMC') || 'FMC';
    save.PreferredPpuId = normalizeCoreId(save.PreferredPpuId || preferences.PPU || 'FMC') || 'FMC';
    save.PreferredApuId = normalizeCoreId(save.PreferredApuId || preferences.APU || 'FMC') || 'FMC';
    save.PreferredShaderId = normalizeCoreId(save.PreferredShaderId || preferences.Shader || preferences.SHADER || 'PX') || 'PX';

    const unlockedFeatures = save.UnlockedFeatures && typeof save.UnlockedFeatures === 'object'
      ? { ...save.UnlockedFeatures }
      : {};

    save.SavestatesUnlocked = Boolean(save.SavestatesUnlocked || unlockedFeatures.Savestates);
    save.RtcUnlocked = Boolean(save.RtcUnlocked || unlockedFeatures.RTC);
    save.GhUnlocked = Boolean(save.GhUnlocked || unlockedFeatures.GH);
    save.ImagineUnlocked = Boolean(save.ImagineUnlocked || unlockedFeatures.Imagine);
    save.DebugUnlocked = Boolean(save.DebugUnlocked || unlockedFeatures.Debug);
    save.AllCoresUnlockedCongrats = Boolean(save.AllCoresUnlockedCongrats);

    save.UnlockedFeatures = {
      Savestates: save.SavestatesUnlocked,
      RTC: save.RtcUnlocked,
      GH: save.GhUnlocked,
      Imagine: save.ImagineUnlocked,
      Debug: save.DebugUnlocked
    };

    save.Preferences = {
      ...preferences,
      CPU: save.PreferredCpuId,
      PPU: save.PreferredPpuId,
      APU: save.PreferredApuId,
      Shader: save.PreferredShaderId,
      SHADER: save.PreferredShaderId
    };

    return save;
  }

  function applySavedSelections(save) {
    syncGameSaveCompatFields(save);
    selectedCpu = chooseSavedCore('CPU', save.PreferredCpuId || save.Preferences?.CPU, save.ownedCpuIds, 'FMC');
    selectedPpu = chooseSavedCore('PPU', save.PreferredPpuId || save.Preferences?.PPU, save.ownedPpuIds, 'FMC');
    selectedApu = chooseSavedCore('APU', save.PreferredApuId || save.Preferences?.APU, save.ownedApuIds, 'FMC');
    selectedShader = chooseSavedCore('SHADER', save.PreferredShaderId || save.Preferences?.Shader || save.Preferences?.SHADER, save.ownedShaderIds, 'PX');
  }

  async function saveGameSave() {
    try {
      if (window.gameSave && typeof window.gameSave.save === 'function') {
        syncGameSaveCompatFields(gameSave);
        await window.gameSave.save(gameSave);
      } else {
        console.error('[Continue] gameSave module not available for saving');
      }
    } catch (error) {
      console.error('[Continue] Save error:', error);
    }
  }

  async function loadLevel() {
    levelRecord = await getLevelRecord(currentLevel);
    requiredStars = Number.isFinite(levelRecord?.requiredStars) ? levelRecord.requiredStars : 5;
    if (currentLevel >= 17) {
      requiredStars = Math.max(0, (currentLevel - 6) * 2);
    }
    
    // Set enforced cores
    enforcedCpu = null;
    enforcedPpu = null;
    enforcedApu = null;
    enforcedShader = null;

    const enforced = [];
    for (const raw of levelRecord?.requiredCards || []) {
      if (typeof raw !== 'string' || !raw.trim()) {
        continue;
      }

      const parts = raw.split('_', 2);
      if (parts.length !== 2) {
        continue;
      }

      const domain = parts[0].toUpperCase();
      const id = normalizeCoreId(parts[1]);
      if (!id) {
        continue;
      }

      switch (domain) {
        case 'CPU':
          enforcedCpu = id;
          break;
        case 'PPU':
          enforcedPpu = id;
          break;
        case 'APU':
          enforcedApu = id;
          break;
        case 'SHADER':
          enforcedShader = id;
          break;
        default:
          break;
      }

      if (domain === 'CLOCK') {
        continue;
      }

      enforced.push({
        domain,
        id,
        label: `${domain}_${id}`
      });
    }
    
    // Update UI elements
    document.getElementById('levelChip').textContent = currentLevel;
    document.getElementById('levelTitle').textContent = levelRecord?.cardChallenge || `Level ${currentLevel}`;
    
    const messageEl = document.getElementById('levelMessage');
    if (levelRecord?.message) {
      messageEl.textContent = levelRecord.message;
      messageEl.style.display = 'block';
    } else {
      messageEl.textContent = '';
      messageEl.style.display = 'none';
    }
    
    // Update enforced cards display
    const enforcedCardsEl = document.getElementById('enforcedCards');
    enforcedCardsEl.innerHTML = '<span class="small-note">Enforced:</span>';
    
    if (enforced.length === 0) {
      enforcedCardsEl.innerHTML += '<span class="small-note">None</span>';
    } else {
      enforced.forEach(e => {
        const chip = document.createElement('button');
        chip.type = 'button';
        chip.className = 'enf-chip';
        chip.textContent = e.label;
        chip.style.borderColor = getCoreColor(e.domain);
        chip.title = `Preview ${e.label}`;
        chip.addEventListener('click', () => {
          openCardPreview(e.domain, e.id, {
            title: e.label,
            subtitle: 'Enforced by level'
          });
        });
        enforcedCardsEl.appendChild(chip);
      });
    }
    
    // Update level status
    const statusEl = document.getElementById('levelStatus');
    if (levelCleared) {
      statusEl.className = 'status-chip cleared';
      statusEl.textContent = 'Cleared';
    } else {
      statusEl.className = 'status-chip not-cleared';
      statusEl.textContent = 'Not Cleared';
    }
  }

  async function getLevelRecord(index) {
    if (!window.continueDb?.get) {
      return null;
    }

    try {
      await window.continueDb.open();
      return await window.continueDb.get('levels', index);
    } catch (error) {
      console.warn('[Continue] Failed to load level from continueDb:', error);
      return null;
    }
  }

  function getCoreColor(domain) {
    const colors = {
      CPU: '#ff5a26',
      PPU: '#10b981',
      APU: '#3b82f6',
      SHADER: '#f59e0b'
    };
    return colors[domain] || '#fff';
  }

  async function getCoreSvgMarkup(domain, core) {
    const normalizedDomain = String(domain || '').toUpperCase();
    const normalizedCore = normalizeCoreId(core);
    if (!normalizedDomain || !normalizedCore || !window.webapi?.card?.getSvg) {
      return '';
    }

    const cacheKey = `${normalizedDomain}:${normalizedCore}`;
    if (cardSvgCache.has(cacheKey)) {
      return cardSvgCache.get(cacheKey);
    }

    try {
      const result = await window.webapi.card.getSvg(normalizedDomain, normalizedCore);
      const markup = result?.success && result?.text ? result.text : '';
      cardSvgCache.set(cacheKey, markup);
      return markup;
    } catch (error) {
      console.warn(`[Continue] Failed to load SVG for ${normalizedDomain}/${normalizedCore}:`, error);
      cardSvgCache.set(cacheKey, '');
      return '';
    }
  }

  function applySvgRenderQuality(rootEl) {
    const svg = rootEl?.querySelector('svg');
    if (!svg) {
      return false;
    }

    svg.classList.add('core-card-svg');
    if (!svg.hasAttribute('preserveAspectRatio')) {
      svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
    }
    if (!svg.hasAttribute('shape-rendering')) {
      svg.setAttribute('shape-rendering', 'geometricPrecision');
    }
    if (!svg.hasAttribute('text-rendering')) {
      svg.setAttribute('text-rendering', 'geometricPrecision');
    }
    if (!svg.hasAttribute('color-rendering')) {
      svg.setAttribute('color-rendering', 'optimizeQuality');
    }

    return true;
  }

  async function initializeRomList() {
    try {
      romRows = await loadInstalledRomRows();
    } catch (error) {
      console.error('[Continue] Failed to initialize ROM list:', error);
      romRows = [];
    }

    if (selectedGameId && !romRows.some(row => row.id === selectedGameId)) {
      selectedGameId = null;
    }

    renderRomList();
    updateGameInfo();
    updateAchievements();
    updateStartButton();
  }

  async function loadInstalledRomRows() {
    const storedRoms = await getStoredRoms();
    const storedKeys = new Set(
      (Array.isArray(storedRoms) ? storedRoms : [])
        .map(rom => normalizeRomStorageName(rom && rom.name))
        .filter(Boolean)
    );

    if (storedKeys.size === 0) {
      return [];
    }

    const { games, achievements } = await loadCatalogRecords();
    const achievementsByGameId = new Map();

    achievements.forEach(achievement => {
      const gameId = achievement && achievement.gameId;
      if (!gameId) {
        return;
      }

      if (!achievementsByGameId.has(gameId)) {
        achievementsByGameId.set(gameId, []);
      }

      achievementsByGameId.get(gameId).push(achievement);
    });

    const savedAchievementIds = new Set(Array.isArray(gameSave?.Achievements) ? gameSave.Achievements : []);

    return games
      .map(game => {
        const romKey = game?.romKey || game?.name || '';
        const normalizedRomKey = normalizeRomStorageName(romKey);
        const achievementList = achievementsByGameId.get(game?.id) || [];

        if (!normalizedRomKey || !storedKeys.has(normalizedRomKey) || achievementList.length === 0) {
          return null;
        }

        const achCompleted = achievementList.reduce((count, achievement) => (
          savedAchievementIds.has(achievement.id) ? count + 1 : count
        ), 0);

        return {
          id: game.id,
          title: game.commonName || game.title || game.name || romKey,
          subtitle: game.title && game.commonName && game.title !== game.commonName ? game.title : '',
          system: String(game.system || game.platform || 'NES').toUpperCase(),
          romKey,
          note: typeof game.note === 'string' ? game.note.trim() : '',
          status: game.status || 'Unknown',
          achTotal: achievementList.length,
          achCompleted,
          achievements: achievementList.slice().sort((left, right) => {
            const leftTitle = (left?.title || left?.metaAchievementName || left?.id || '').toLowerCase();
            const rightTitle = (right?.title || right?.metaAchievementName || right?.id || '').toLowerCase();
            return leftTitle.localeCompare(rightTitle);
          })
        };
      })
      .filter(Boolean)
      .sort((left, right) => {
        if (right.achTotal !== left.achTotal) {
          return right.achTotal - left.achTotal;
        }
        return left.title.localeCompare(right.title);
      });
  }

  async function loadCatalogRecords() {
    let games = [];
    let achievements = [];

    if (window.continueDb && typeof window.continueDb.getAll === 'function') {
      try {
        await window.continueDb.open();
        const [dbGames, dbAchievements] = await Promise.all([
          window.continueDb.getAll('games'),
          window.continueDb.getAll('achievements')
        ]);

        games = Array.isArray(dbGames) ? dbGames : [];
        achievements = Array.isArray(dbAchievements) ? dbAchievements : [];

        if (games.length > 0 || achievements.length > 0) {
          return { games, achievements };
        }
      } catch (error) {
        console.warn('[Continue] Failed to load catalog from continueDb:', error);
      }
    }

    try {
      const response = await fetch(DEFAULT_DB_URL, { cache: 'no-cache' });
      if (!response.ok) {
        throw new Error(`Default DB fetch failed with status ${response.status}`);
      }

      const payload = await response.json();
      const data = payload && typeof payload === 'object' ? payload.data || {} : {};
      games = Array.isArray(data.games) ? data.games : [];
      achievements = Array.isArray(data.achievements) ? data.achievements : [];
    } catch (error) {
      console.error('[Continue] Failed to load fallback catalog:', error);
    }

    return { games, achievements };
  }

  function normalizeRomStorageName(value) {
    return typeof value === 'string' && value.trim()
      ? value.trim().toLowerCase()
      : '';
  }

  async function openRomStorageStore(mode) {
    return new Promise((resolve, reject) => {
      if (!window.indexedDB) {
        reject(new Error('IndexedDB not available'));
        return;
      }

      const request = indexedDB.open(ROM_STORAGE_DB_NAME, ROM_STORAGE_DB_VERSION);
      request.onupgradeneeded = () => {
        const db = request.result;
        if (!db.objectStoreNames.contains('kv')) {
          db.createObjectStore('kv');
        }
        if (!db.objectStoreNames.contains(ROM_STORAGE_STORE)) {
          db.createObjectStore(ROM_STORAGE_STORE, { keyPath: 'name' });
        }
      };
      request.onsuccess = () => {
        const db = request.result;
        try {
          resolve(db.transaction(ROM_STORAGE_STORE, mode).objectStore(ROM_STORAGE_STORE));
        } catch (error) {
          reject(error);
        }
      };
      request.onerror = () => reject(request.error || new Error('IndexedDB open error'));
    });
  }

  async function migrateLegacyLocalStorageRoms() {
    try {
      if (!window.localStorage) {
        return;
      }

      const legacyKeys = [];
      for (let index = 0; index < localStorage.length; index++) {
        const key = localStorage.key(index);
        if (key && key.startsWith(LEGACY_ROM_KEY_PREFIX)) {
          legacyKeys.push(key);
        }
      }

      if (legacyKeys.length === 0) {
        return;
      }

      const existing = await getStoredRomsFromIndexedDb(false);
      const existingNames = new Set(existing.map(rom => rom.name));

      for (const key of legacyKeys) {
        const base64 = localStorage.getItem(key);
        const name = key.substring(LEGACY_ROM_KEY_PREFIX.length);
        if (!base64 || !name || existingNames.has(name)) {
          continue;
        }

        const store = await openRomStorageStore('readwrite');
        await new Promise((resolve, reject) => {
          const request = store.put({ name, base64 });
          request.onsuccess = () => resolve();
          request.onerror = () => reject(request.error);
        });
      }
    } catch (error) {
      console.warn('[Continue] Failed to migrate legacy ROM storage:', error);
    }
  }

  async function getStoredRomsFromIndexedDb(runMigration = true) {
    if (runMigration) {
      await migrateLegacyLocalStorageRoms();
    }

    try {
      const store = await openRomStorageStore('readonly');
      return await new Promise((resolve, reject) => {
        const request = store.getAll();
        request.onsuccess = () => resolve(Array.isArray(request.result) ? request.result : []);
        request.onerror = () => reject(request.error);
      });
    } catch (error) {
      console.warn('[Continue] IndexedDB ROM storage unavailable:', error);
      return getStoredRomsFromLegacyLocalStorage();
    }
  }

  function getStoredRomsFromLegacyLocalStorage() {
    if (!window.localStorage) {
      return [];
    }

    const roms = [];
    for (let index = 0; index < localStorage.length; index++) {
      const key = localStorage.key(index);
      if (!key || !key.startsWith(LEGACY_ROM_KEY_PREFIX)) {
        continue;
      }

      const name = key.substring(LEGACY_ROM_KEY_PREFIX.length);
      const base64 = localStorage.getItem(key);
      if (name && base64) {
        roms.push({ name, base64 });
      }
    }

    return roms;
  }

  async function getStoredRoms() {
    if (window.nesInterop && typeof window.nesInterop.getStoredRoms === 'function') {
      return window.nesInterop.getStoredRoms();
    }

    return getStoredRomsFromIndexedDb();
  }

  function renderRomList() {
    const tbody = document.getElementById('romTbody');
    const emptyEl = document.getElementById('romEmpty');
    const tableEl = document.getElementById('romTable');

    const filtered = romRows;
    
    if (filtered.length === 0) {
      emptyEl.style.display = 'block';
      tableEl.style.display = 'none';
      return;
    }
    
    emptyEl.style.display = 'none';
    tableEl.style.display = 'block';
    
    // Render rows
    tbody.innerHTML = '';
    filtered.forEach(r => {
      const row = document.createElement('button');
      row.type = 'button';
      row.className = 'rom-tr rom-td';
      if (selectedGameId === r.id) {
        row.classList.add('selected');
      }
      row.dataset.gameId = String(r.id);
      row.setAttribute('role', 'row');
      
      row.innerHTML = `
        <div class="c-title" role="cell">
          <div class="rom-title">${escapeHtml(r.title)}</div>
          ${r.subtitle ? `<div class="rom-subtitle">${escapeHtml(r.subtitle)}</div>` : ''}
        </div>
        <div class="c-ach rom-stars" role="cell">${r.achCompleted}/${r.achTotal}</div>
      `;
      
      row.addEventListener('click', () => selectGame(r.id));
      tbody.appendChild(row);
    });

    requestAnimationFrame(() => {
      ensureSelectedRomInView();
    });
  }

  function ensureSelectedRomInView() {
    if (!selectedGameId) {
      return;
    }

    const tbody = document.getElementById('romTbody');
    if (!tbody) {
      return;
    }

    const selectedRow = tbody.querySelector(`.rom-td.selected[data-game-id="${CSS.escape(String(selectedGameId))}"]`);
    if (!selectedRow) {
      return;
    }

    selectedRow.scrollIntoView({
      block: 'nearest',
      inline: 'nearest'
    });
  }

  function selectGame(gameId, options = {}) {
    const previousGameId = selectedGameId;
    selectedGameId = gameId;
    if (!options.silent && previousGameId !== gameId) {
      void playUiSfx(UI_SFX.select, { key: 'select-game', cooldownMs: 70 });
    }
    renderRomList();
    updateGameInfo();
    updateAchievements();
    updateSelectionDependentUi();
    updateStartButton();
  }

  function updateGameInfo() {
    const infoEl = document.getElementById('gameInfo');
    
    if (!selectedGameId) {
      infoEl.innerHTML = '<div class="small-note">Select an installed game to view details.</div>';
      return;
    }
    
    const game = romRows.find(r => r.id === selectedGameId);
    if (!game) return;
    const statusClass = `status-${String(game.status || '').toLowerCase()}`;
    const noteMarkup = game.note
      ? `
        <div class="game-note" role="note" aria-label="Compatibility note">
          <div class="game-note-label">Note</div>
          <div class="game-note-copy">${escapeHtml(game.note)}</div>
        </div>
      `
      : '';
    
    infoEl.innerHTML = `
      <div class="game-grid">
        <div class="game-row">
          <div class="label small-note">Title</div>
          <div class="value">${escapeHtml(game.title)}</div>
        </div>
        <div class="game-row">
          <div class="label small-note">ROM</div>
          <div class="value">${escapeHtml(game.romKey)}</div>
        </div>
        <div class="game-row">
          <div class="label small-note">System</div>
          <div class="value">${escapeHtml(game.system)}</div>
        </div>
        <div class="game-row">
          <div class="label small-note">Status</div>
          <div class="value ${statusClass}">${escapeHtml(game.status)}</div>
        </div>
        <div class="game-row">
          <div class="label small-note">Stars</div>
          <div class="value">${game.achCompleted}/${game.achTotal} completed</div>
        </div>
      </div>
      ${noteMarkup}
    `;
  }

  function updateAchievements() {
    const achBox = document.getElementById('achBox');
    
    if (!selectedGameId) {
      achBox.innerHTML = '<div class="small-note">Select an installed cartridge to view achievements.</div>';
      return;
    }
    
    const game = romRows.find(r => r.id === selectedGameId);
    if (!game || !Array.isArray(game.achievements) || game.achievements.length === 0) {
      achBox.innerHTML = '<div class="small-note">This cartridge has no achievement data.</div>';
      return;
    }

    const savedAchievementIds = new Set(Array.isArray(gameSave?.Achievements) ? gameSave.Achievements : []);
    const achievements = game.achievements.map(achievement => ({
      id: achievement.id,
      title: achievement.title || achievement.metaAchievementName || achievement.id,
      description: achievement.description || achievement.metaAchievementName || '',
      completed: savedAchievementIds.has(achievement.id)
    }));

    const completedCount = achievements.filter(achievement => achievement.completed).length;
    
    achBox.innerHTML = `
      <div class="ach-summary">
        <span class="small-note">${escapeHtml(game.title)}</span>
        <strong>${completedCount}/${achievements.length}</strong>
        <span class="small-note">completed</span>
      </div>
      <ul class="ach-list">
        ${achievements.map(a => `
          <li class="ach-item ${a.completed ? 'done' : 'todo'}">
            <span class="ach-check">${a.completed ? '▣' : '▢'}</span>
            <span class="ach-main">
              <span class="ach-title">${escapeHtml(a.title)}</span>
              ${a.description && a.description !== a.title ? `<span class="ach-desc small-note">${escapeHtml(a.description)}</span>` : ''}
            </span>
          </li>
        `).join('')}
      </ul>
    `;
  }

  function updateSelectionDependentUi() {
    const hasGameSelected = selectedGameId !== null;
    const achievementsSection = document.getElementById('achievementsSection');
    const startBtn = document.getElementById('startBtn');
    const resetBtn = document.getElementById('resetBtn');

    if (achievementsSection) {
      achievementsSection.hidden = !hasGameSelected;
    }

    if (startBtn) {
      startBtn.hidden = !hasGameSelected;
    }

    if (resetBtn) {
      resetBtn.hidden = !hasGameSelected;
    }
  }

  function updateUI() {
    // Update stars display
    document.getElementById('starsDisplay').textContent = `${stars}/${requiredStars}`;
    
    // Update progress button
    const btnProgress = document.getElementById('btnProgress');
    const canAdvance = levelCleared && stars >= requiredStars;
    btnProgress.disabled = !canAdvance;
    
    // Update core slots
    updateCoreSlot('cpu', selectedCpu, enforcedCpu);
    updateCoreSlot('ppu', selectedPpu, enforcedPpu);
    updateCoreSlot('apu', selectedApu, enforcedApu);
    updateCoreSlot('shader', selectedShader, enforcedShader);
    
    updateSelectionDependentUi();
    updateStartButton();
  }

  function updateCoreSlot(slotName, selected, enforced) {
    const slotEl = document.getElementById(`${slotName}Slot`);
    const emptyEl = document.getElementById(`${slotName}Empty`);
    const cardEl = document.getElementById(`${slotName}Card`);
    
    const core = enforced || selected;
    const domain = slotName.toUpperCase();
    const isLocked = Boolean(enforced);

    if (slotEl) {
      slotEl.classList.toggle('locked', isLocked);
    }
    
    if (core) {
      emptyEl.style.display = 'none';
      cardEl.style.display = 'block';

      const requestKey = `${domain}:${core}:${isLocked ? 'locked' : 'selectable'}`;
      cardEl.dataset.requestKey = requestKey;
      cardEl.innerHTML = '<div class="card-loading">Loading...</div>';

      getCoreSvgMarkup(domain, core).then(svgMarkup => {
        if (cardEl.dataset.requestKey !== requestKey) {
          return;
        }
        renderSlotCard(cardEl, slotName, domain, core, svgMarkup, isLocked);
      });
    } else {
      emptyEl.style.display = 'flex';
      cardEl.style.display = 'none';
      delete cardEl.dataset.requestKey;
      cardEl.innerHTML = '';
    }
  }

  function renderSlotCard(cardEl, slotName, domain, core, svgMarkup, isLocked) {
    const body = svgMarkup
      ? svgMarkup
      : `<div class="slot-label">${escapeHtml(domain)}_${escapeHtml(core)}</div>`;

    cardEl.innerHTML = `<div class="card-wrap${svgMarkup ? '' : ' card-wrap-fallback'}">${body}</div>`;

    const cardWrap = cardEl.querySelector('.card-wrap');
    if (!cardWrap) {
      return;
    }

    applySvgRenderQuality(cardWrap);

    if (isLocked) {
      cardWrap.appendChild(createLockedOverlay());
      cardWrap.style.cursor = 'zoom-in';
      cardWrap.appendChild(createCardActionButton(`Preview ${domain} core`, () => {
        openCardPreview(domain, core, {
          title: `${domain}_${core}`,
          subtitle: 'Enforced by level'
        });
      }));
      return;
    }

    cardWrap.style.cursor = 'pointer';
    cardWrap.appendChild(createCardActionButton(`Select ${domain} core`, () => openCorePicker(slotName)));
  }

  function createCardActionButton(label, onClick) {
    const cover = document.createElement('button');
    cover.type = 'button';
    cover.className = 'card-cover-btn';
    cover.setAttribute('aria-label', label);
    cover.addEventListener('click', (event) => {
      event.preventDefault();
      event.stopPropagation();
      onClick();
    });
    return cover;
  }

  function createLockedOverlay() {
    const overlay = document.createElement('div');
    overlay.className = 'card-enforced-overlay';
    overlay.title = 'Enforced by level';
    overlay.setAttribute('aria-hidden', 'true');
    overlay.innerHTML = `
      <svg viewBox="0 0 64 64" class="card-lock-icon">
        <rect x="16" y="28" width="32" height="28" rx="5" ry="5"></rect>
        <path d="M22 28 V20 a10 10 0 0 1 20 0 v8" fill="none" stroke-linecap="round" stroke-linejoin="round"></path>
      </svg>
      <div class="enforced-text">Enforced</div>
    `;
    return overlay;
  }

  function escapeHtml(value) {
    return String(value || '').replace(/[&<>"']/g, (char) => ({
      '&': '&amp;',
      '<': '&lt;',
      '>': '&gt;',
      '"': '&quot;',
      "'": '&#39;'
    }[char]));
  }

  function setStartButtonText(title) {
    const titleEl = document.getElementById('startBtnTitle');

    if (titleEl) {
      titleEl.textContent = title;
    }
  }

  function buildContinuePreviewUrl(romKey, cacheKey) {
    if (typeof romKey !== 'string' || !romKey.trim()) {
      return '';
    }

    const baseUrl = typeof window.webapi?.getBaseUrl === 'function'
      ? window.webapi.getBaseUrl()
      : 'http://127.0.0.1:42067';
    const requestUrl = new URL('/api/save/continue-preview', `${String(baseUrl || 'http://127.0.0.1:42067').replace(/\/$/, '')}/`);
    requestUrl.searchParams.set('romKey', romKey);
    requestUrl.searchParams.set('_t', cacheKey || String(Date.now()));
    return requestUrl.toString();
  }

  function updateStartButtonArtwork() {
    const startBtn = document.getElementById('startBtn');
    if (!startBtn) {
      return;
    }

    const selectedGame = romRows.find(row => row.id === selectedGameId);
    const slot = selectedGame ? getContinueSlotForRom(selectedGame.romKey) : null;
    const previewUrl = slot && selectedGame
      ? buildContinuePreviewUrl(selectedGame.romKey, slot.updatedAtUtc || slot.previewImagePath || '')
      : '';

    if (previewUrl) {
      startBtn.style.setProperty('--start-btn-bg', `url("${previewUrl}")`);
      startBtn.classList.add('has-art');
      return;
    }

    startBtn.style.removeProperty('--start-btn-bg');
    startBtn.classList.remove('has-art');
  }

  function updateResetButton() {
    const resetBtn = document.getElementById('resetBtn');
    if (!resetBtn) {
      return;
    }

    const hasValidBuild = (enforcedCpu || selectedCpu) && (enforcedPpu || selectedPpu) && (enforcedApu || selectedApu) && (enforcedShader || selectedShader);
    const hasGameSelected = selectedGameId !== null;
    const canReset = hasValidBuild && hasGameSelected;

    if (canReset) {
      resetBtn.disabled = false;
      resetBtn.className = 'opt-link reset-btn unlocked';
      resetBtn.title = 'Restart the emulator and resume achievement hunting from the beginning';
      return;
    }

    resetBtn.disabled = true;
    resetBtn.className = 'opt-link reset-btn locked';
    resetBtn.title = 'Build valid and select an installed game';
  }

  function updateStartButton() {
    const startBtn = document.getElementById('startBtn');
    if (!startBtn) {
      return;
    }

    const hasValidBuild = (enforcedCpu || selectedCpu) && (enforcedPpu || selectedPpu) && (enforcedApu || selectedApu) && (enforcedShader || selectedShader);
    const hasGameSelected = selectedGameId !== null;
    const canStart = hasValidBuild && hasGameSelected;
    const canContinue = canStart && hasTrustedContinueSelected();
    
    if (canStart) {
      startBtn.disabled = false;
      startBtn.className = 'opt-link start-btn unlocked';
      startBtn.title = canContinue ? 'Resume from the last trusted checkpoint' : 'Start the game';
      setStartButtonText(canContinue ? 'CONTINUE GAME' : 'START GAME');
    } else {
      startBtn.disabled = true;
      startBtn.className = 'opt-link start-btn locked';
      startBtn.title = 'Build valid and select an installed game';
      setStartButtonText('START GAME');
    }

    updateStartButtonArtwork();
    updateResetButton();
  }

  function setupEventListeners() {
    // Cartridge toggle
    const cartridgeToggle = document.getElementById('cartridgeToggle');
    if (cartridgeToggle) {
      cartridgeToggle.addEventListener('click', toggleCartridge);
    }
    
    const openRomManagerBtn = document.getElementById('openRomManagerBtn');
    if (openRomManagerBtn) {
      openRomManagerBtn.addEventListener('click', () => {
        void playUiSfx(UI_SFX.toggle, { key: 'open-rom-manager', cooldownMs: 120 });
        window.location.href = '../RomManager/index.html';
      });
    }
    
    // Core slot clicks
    ['cpu', 'ppu', 'apu', 'shader'].forEach(slotName => {
      const emptyEl = document.getElementById(`${slotName}Empty`);
      const cardEl = document.getElementById(`${slotName}Card`);
      
      if (emptyEl) {
        emptyEl.addEventListener('click', () => openCorePicker(slotName));
      }
      if (cardEl) {
        cardEl.addEventListener('click', () => {
          // Only allow picking if not enforced
          const enforced = slotName === 'cpu' ? enforcedCpu :
                          slotName === 'ppu' ? enforcedPpu :
                          slotName === 'apu' ? enforcedApu :
                          enforcedShader;
          if (!enforced) {
            openCorePicker(slotName);
          }
        });
      }
    });
    
    // Progress button
    const btnProgress = document.getElementById('btnProgress');
    if (btnProgress) {
      btnProgress.addEventListener('click', advanceLevel);
    }
    
    // Start button
    const startBtn = document.getElementById('startBtn');
    if (startBtn) {
      startBtn.addEventListener('click', startGame);
    }

    const resetBtn = document.getElementById('resetBtn');
    if (resetBtn) {
      resetBtn.addEventListener('click', resetGame);
    }

    const returnLink = document.getElementById('returnLink');
    if (returnLink) {
      returnLink.addEventListener('click', () => {
        void playUiSfx(UI_SFX.toggle, { key: 'return-link', cooldownMs: 120 });
      });
    }

    const controller1Btn = document.getElementById('controller1Btn');
    if (controller1Btn) {
      controller1Btn.addEventListener('click', () => {
        void openControllerConfigForPlayer(1);
      });
    }

    const controller2Btn = document.getElementById('controller2Btn');
    if (controller2Btn) {
      controller2Btn.addEventListener('click', () => {
        void openControllerConfigForPlayer(2);
      });
    }
    
    // Picker modal
    const pickerClose = document.getElementById('pickerClose');
    if (pickerClose) {
      pickerClose.addEventListener('click', closePicker);
    }
    
    const pickerModal = document.getElementById('pickerModal');
    if (pickerModal) {
      pickerModal.addEventListener('click', (e) => {
        if (e.target === pickerModal) {
          closePicker();
        }
      });
    }

    const previewClose = document.getElementById('cardPreviewClose');
    if (previewClose) {
      previewClose.addEventListener('click', closeCardPreview);
    }

    const previewModal = document.getElementById('cardPreviewModal');
    if (previewModal) {
      previewModal.addEventListener('click', (e) => {
        if (e.target === previewModal) {
          closeCardPreview();
        }
      });
    }

    const unlockModal = document.getElementById('unlockModal');
    if (unlockModal) {
      unlockModal.addEventListener('click', (event) => {
        if (event.target === unlockModal) {
          void closeUnlockModal();
        }
      });
    }

    const unlockReturnBtn = document.getElementById('unlockReturnBtn');
    if (unlockReturnBtn) {
      unlockReturnBtn.addEventListener('click', () => {
        void closeUnlockModal();
      });
    }

    const unlockEquipAllBtn = document.getElementById('unlockEquipAllBtn');
    if (unlockEquipAllBtn) {
      unlockEquipAllBtn.addEventListener('click', () => {
        void equipAllRewardItems();
      });
    }

    document.addEventListener('keydown', (event) => {
      if (event.key !== 'Escape') {
        return;
      }

      const previewOpen = document.getElementById('cardPreviewModal')?.style.display === 'flex';
      if (previewOpen) {
        closeCardPreview();
        return;
      }

        const unlockOpen = document.getElementById('unlockModal')?.style.display === 'flex';
        if (unlockOpen) {
            void closeUnlockModal();
          return;
        }

      const pickerOpen = document.getElementById('pickerModal')?.style.display === 'flex';
      if (pickerOpen) {
        closePicker();
      }
    });
  }

  async function openControllerConfigForPlayer(playerNumber) {
    if (!window.webapi?.ui?.openControllerConfig) {
      console.warn('[Continue] openControllerConfig UI endpoint is unavailable');
      return;
    }

    void playUiSfx(UI_SFX.select, { key: `controller-port-${playerNumber}`, cooldownMs: 90 });

    const result = await window.webapi.ui.openControllerConfig(playerNumber);
    if (!result || result.success === false) {
      console.warn(`[Continue] Failed to open controller config for player ${playerNumber}:`, result?.error || 'unknown error');
    }
  }

  function toggleCartridge() {
    void playUiSfx(UI_SFX.toggle, { key: 'toggle-cartridge', cooldownMs: 120 });
    cartridgeCollapsed = !cartridgeCollapsed;
    const panel = document.getElementById('cartridgePanel');
    const toggle = document.getElementById('cartridgeToggle');
    const icon = toggle.querySelector('.toggle-ico');
    
    if (cartridgeCollapsed) {
      panel.style.display = 'none';
      icon.textContent = '▸';
      toggle.setAttribute('aria-expanded', 'false');
    } else {
      panel.style.display = 'flex';
      icon.textContent = '▾';
      toggle.setAttribute('aria-expanded', 'true');
    }
  }

  let currentPickerSlot = null;
  let currentPickerPreviewToken = 0;

  async function openCardPreview(domain, core, options = {}) {
    const modal = document.getElementById('cardPreviewModal');
    const title = document.getElementById('cardPreviewTitle');
    const subtitle = document.getElementById('cardPreviewSubtitle');
    const stage = document.getElementById('cardPreviewStage');
    if (!modal || !title || !subtitle || !stage) {
      return;
    }

    void playUiSfx(UI_SFX.modalOpen, { key: 'card-preview-open', cooldownMs: 120 });

    const label = `${domain}_${core}`;
    title.textContent = options.title || label;
    subtitle.textContent = options.subtitle || '';
    subtitle.style.display = subtitle.textContent ? 'block' : 'none';

    modal.style.display = 'flex';
    stage.innerHTML = '<div class="card-loading">Loading...</div>';

    const previewToken = String(++currentPreviewToken);
    stage.dataset.previewToken = previewToken;

    if (options.previewMarkup) {
      renderCardPreview(stage, domain, core, '', options);
      return;
    }

    const svgMarkup = await getCoreSvgMarkup(domain, core);
    if (stage.dataset.previewToken !== previewToken) {
      return;
    }

    renderCardPreview(stage, domain, core, svgMarkup, options);
  }

  function renderCardPreview(stageEl, domain, core, svgMarkup, options = {}) {
    if (options.previewMarkup) {
      stageEl.innerHTML = `<div class="card-preview-wrap card-wrap-generic">${options.previewMarkup}</div>`;
      return;
    }

    const body = svgMarkup
      ? svgMarkup
      : `<div class="slot-label">${escapeHtml(domain)}_${escapeHtml(core)}</div>`;

    stageEl.innerHTML = `<div class="card-preview-wrap${svgMarkup ? '' : ' card-wrap-fallback'}">${body}</div>`;
    applySvgRenderQuality(stageEl);
  }

  function closeCardPreview() {
    const modal = document.getElementById('cardPreviewModal');
    const stage = document.getElementById('cardPreviewStage');
    if (!modal || !stage) {
      return;
    }

    modal.style.display = 'none';
    stage.dataset.previewToken = '';
    stage.innerHTML = '';
  }

  function clearRewardAutoCloseTimer() {
    if (rewardModalState.autoCloseTimer) {
      clearTimeout(rewardModalState.autoCloseTimer);
      rewardModalState.autoCloseTimer = 0;
    }
  }

  async function closeUnlockModal() {
    const modal = document.getElementById('unlockModal');
    const grid = document.getElementById('unlockGrid');
    const notices = document.getElementById('unlockNotices');
    const empty = document.getElementById('unlockEmpty');
    if (!modal || !grid || !notices || !empty) {
      return;
    }

    const pendingRewardIds = Array.isArray(rewardModalState.pendingRewardIds)
      ? rewardModalState.pendingRewardIds.slice()
      : [];

    if (pendingRewardIds.length > 0) {
      try {
        await acknowledgePendingUnlockBundles(pendingRewardIds);
      } catch (error) {
        console.warn('[Continue] Failed to acknowledge pending rewards:', error);
      }
    }

    clearRewardAutoCloseTimer();
    modal.style.display = 'none';
    grid.innerHTML = '';
    notices.innerHTML = '';
    notices.hidden = true;
    empty.hidden = true;
    rewardModalState = {
      items: [],
      featureUnlocks: [],
      showCongrats: false,
      showAllCores: false,
      pendingRewardIds: [],
      title: 'New Cards Unlocked',
      kicker: 'Level Intermission',
      copy: '',
      autoCloseTimer: 0
    };
  }

  function getRewardDomainLabel(domain) {
    switch (String(domain || '').toUpperCase()) {
      case 'WEBMODULE':
        return 'Module';
      case 'BACKGROUND':
          return 'BG';
      case 'NULLPROVIDER':
          return 'NULL';
      case 'FEATURE':
        return 'Feature';
      default:
        return String(domain || '').toUpperCase();
    }
  }

  function buildGenericRewardFallback(item) {
    return `
      <div class="unlock-card-visual unlock-card-fallback">
        <div class="unlock-card-fallback-domain">${escapeHtml(getRewardDomainLabel(item.domain))}</div>
        <div class="unlock-card-fallback-title">${escapeHtml(item.name || item.id)}</div>
      </div>
    `;
  }

  function buildRewardPreviewMarkup(item) {
    return `
      <div class="card-preview-generic">
        <div class="card-preview-generic-domain">${escapeHtml(getRewardDomainLabel(item.domain))}</div>
        <div class="card-preview-generic-title">${escapeHtml(item.name || item.id)}</div>
        ${item.subtitle ? `<div class="card-preview-generic-subtitle">${escapeHtml(item.subtitle)}</div>` : ''}
        ${item.description ? `<div class="card-preview-generic-description">${escapeHtml(item.description)}</div>` : ''}
      </div>
    `;
  }

  function normalizePendingRewardItem(bundleId, item) {
    const itemType = String(item?.type || '').trim();
    const normalizedDomain = itemType.toUpperCase();
    const canEquip = Boolean(item?.canEquip)
      && (item?.equipAction === 'equip-background' || item?.equipAction === 'equip-null-provider');
    const name = String(item?.title || item?.id || '').trim();

    return {
      bundleId,
      domain: normalizedDomain,
      id: String(item?.id || '').trim(),
      name,
      subtitle: typeof item?.subtitle === 'string' ? item.subtitle : '',
      description: typeof item?.description === 'string' ? item.description : '',
      svgMarkup: '',
      equipable: canEquip,
      equipped: Boolean(item?.isEquipped),
      equipAction: typeof item?.equipAction === 'string' ? item.equipAction : '',
      previewMarkup: buildRewardPreviewMarkup({
        domain: normalizedDomain,
        id: item?.id,
        name,
        subtitle: typeof item?.subtitle === 'string' ? item.subtitle : '',
        description: typeof item?.description === 'string' ? item.description : ''
      })
    };
  }

  async function hydratePendingRewardItem(bundleId, item) {
    const normalized = normalizePendingRewardItem(bundleId, item);
    const svgMarkup = await getCoreSvgMarkup(normalized.domain, normalized.id);
    return {
      ...normalized,
      svgMarkup,
      previewMarkup: svgMarkup ? '' : normalized.previewMarkup
    };
  }

  async function claimPendingUnlockBundles() {
    if (!window.webapi?.progression?.claimPending) {
      return [];
    }

    try {
      const result = await window.webapi.progression.claimPending();
      return Array.isArray(result?.pendingUnlocks) ? result.pendingUnlocks : [];
    } catch (error) {
      console.warn('[Continue] Failed to claim pending unlocks:', error);
      return [];
    }
  }

  async function acknowledgePendingUnlockBundles(rewardIds) {
    if (!window.webapi?.progression?.acknowledge || !Array.isArray(rewardIds) || rewardIds.length === 0) {
      return false;
    }

    const result = await window.webapi.progression.acknowledge(rewardIds);
    return Boolean(result?.success);
  }

  async function presentPendingUnlocks() {
    const pendingBundles = await claimPendingUnlockBundles();
    if (pendingBundles.length === 0) {
      return false;
    }

    return presentLevelRewards(null, {
      pendingBundles,
      kicker: 'Unlock Inbox',
      title: 'Queued Rewards Ready',
      copy: 'Queued rewards are waiting in your deck. Inspect the cards, equip what you want now, then return to the build.'
    });
  }

  function getOwnedCoreIdsByDomain(save, domain) {
    const key = OWNED_KEY_BY_DOMAIN[domain];
    if (!key) {
      return [];
    }

    return normalizeOwnedArray(save?.[key], []);
  }

  function addOwnedCore(save, domain, id) {
    const key = OWNED_KEY_BY_DOMAIN[domain];
    const normalizedId = normalizeCoreId(id);
    if (!key || !normalizedId) {
      return false;
    }

    const owned = getOwnedCoreIdsByDomain(save, domain);
    if (owned.includes(normalizedId)) {
      save[key] = owned;
      return false;
    }

    owned.push(normalizedId);
    save[key] = owned;
    return true;
  }

  function countOwnedCores(save) {
    const legacyCoreCount = Object.keys(OWNED_KEY_BY_DOMAIN).reduce((count, domain) => {
      return count + getOwnedCoreIdsByDomain(save, domain).length;
    }, 0);

    return legacyCoreCount
      + countUnlockedEntries(save?.UnlockedWebmodules)
      + countUnlockedEntries(save?.UnlockedBackgrounds)
      + countUnlockedEntries(save?.UnlockedNullProviders);
  }

  async function getTotalCoreCount() {
    const legacyCoreCount = ['CPU', 'PPU', 'APU', 'CLOCK', 'SHADER'].reduce((count, domain) => {
      return count + getCoreOptions(domain).length;
    }, 0);

    const roster = await getProgressionRoster();
    if (!roster) {
      return legacyCoreCount;
    }

    const webmoduleCount = Array.isArray(roster.webmodules) ? roster.webmodules.length : 0;
    const backgroundCount = Array.isArray(roster.backgrounds) ? roster.backgrounds.length : 0;
    const nullProviderCount = Array.isArray(roster.nullProviders) ? roster.nullProviders.length : 0;
    return legacyCoreCount + webmoduleCount + backgroundCount + nullProviderCount;
  }

  function dedupeRewardPairs(items) {
    const seen = new Set();
    return (Array.isArray(items) ? items : []).filter(item => {
      const domain = String(item?.domain || '').toUpperCase();
      const id = normalizeCoreId(item?.id);
      const key = `${domain}:${id}`;
      if (!OWNED_KEY_BY_DOMAIN[domain] || !id || seen.has(key)) {
        return false;
      }

      seen.add(key);
      item.domain = domain;
      item.id = id;
      return true;
    });
  }

  function shuffleItems(items) {
    const result = Array.isArray(items) ? items.slice() : [];
    for (let index = result.length - 1; index > 0; index--) {
      const swapIndex = Math.floor(Math.random() * (index + 1));
      const temp = result[index];
      result[index] = result[swapIndex];
      result[swapIndex] = temp;
    }
    return result;
  }

  function pickSubset(items, minimum, maximum) {
    if (!Array.isArray(items) || items.length === 0) {
      return [];
    }

    const upper = Math.min(maximum, items.length);
    const lower = Math.min(minimum, upper);
    const count = upper <= lower
      ? upper
      : lower + Math.floor(Math.random() * (upper - lower + 1));
    return shuffleItems(items).slice(0, count);
  }

  async function pickRandomBonusCards(save) {
    const lastCandidates = [];
    const randomCandidates = [];

    cardRecords.forEach(record => {
      if (!record || !['CPU', 'PPU', 'APU', 'SHADER'].includes(record.domain)) {
        return;
      }

      const owned = getOwnedCoreIdsByDomain(save, record.domain);
      if (owned.includes(record.id)) {
        return;
      }

      if (record.type === 'LAST') {
        lastCandidates.push({ domain: record.domain, id: record.id });
        return;
      }

      if (record.type === 'RANDOM' || !record.type) {
        randomCandidates.push({ domain: record.domain, id: record.id });
      }
    });

    const result = pickSubset(lastCandidates, 2, 3);

    if (result.length < 2 && randomCandidates.length > 0) {
      const needed = 2 - result.length;
      const filler = shuffleItems(randomCandidates).filter(candidate => {
        return !result.some(item => item.domain === candidate.domain && item.id === candidate.id);
      });
      result.push(...filler.slice(0, needed));
    }

    if (result.length < 2) {
      const fallback = [];
      ['CPU', 'PPU', 'APU', 'SHADER'].forEach(domain => {
        const starterId = STARTER_CORE_BY_DOMAIN[domain];
        const owned = new Set(getOwnedCoreIdsByDomain(save, domain));
        getCoreOptions(domain).forEach(core => {
          if (!core?.id || core.id === starterId || owned.has(core.id)) {
            return;
          }

          fallback.push({ domain, id: core.id });
        });
      });

      const filler = shuffleItems(fallback).filter(candidate => {
        return !result.some(item => item.domain === candidate.domain && item.id === candidate.id);
      });
      result.push(...filler.slice(0, Math.max(0, 2 - result.length)));
      if (result.length === 2 && filler.length > 2) {
        result.push(filler[2]);
      }
    }

    return dedupeRewardPairs(result).slice(0, 3);
  }

  function getCurrentLevelRewardPairs() {
    const pairs = [];
    for (const raw of levelRecord?.requiredCards || []) {
      if (typeof raw !== 'string' || !raw.trim()) {
        continue;
      }

      const parts = raw.split('_', 2);
      if (parts.length !== 2) {
        continue;
      }

      const domain = parts[0].toUpperCase();
      const id = normalizeCoreId(parts[1]);
      if (!OWNED_KEY_BY_DOMAIN[domain] || !id) {
        continue;
      }

      pairs.push({ domain, id });
    }

    return dedupeRewardPairs(pairs);
  }

  function getFeatureUnlocksForLevel(save, previousLevel) {
    const unlocked = [];

    switch (previousLevel) {
      case 4:
        unlocked.push('RTC + Glitch Harvester added to your module roster.');
        break;
      case 8:
        unlocked.push('Time Jump added to your module roster.');
        break;
      case 12:
        unlocked.push('Corruption Slop added to your module roster.');
        break;
      case 16:
        unlocked.push('ImagineBug added to your module roster.');
        break;
      default:
        break;
    }

    return unlocked;
  }

  async function buildLevelRewards(previousLevel) {
    const rewardPairs = getCurrentLevelRewardPairs();
    const anyCoreEnforced = rewardPairs.some(pair => ['CPU', 'PPU', 'APU', 'SHADER'].includes(pair.domain));
    const alwaysBonusPack = previousLevel >= 21;

    if (!anyCoreEnforced || alwaysBonusPack) {
      rewardPairs.push(...await pickRandomBonusCards(gameSave));
    }

    if (previousLevel === 16) {
      rewardPairs.push({ domain: 'CLOCK', id: 'CLR' });
      rewardPairs.push({ domain: 'CLOCK', id: 'TRB' });
    }

    const newlyUnlocked = [];
    dedupeRewardPairs(rewardPairs).forEach(pair => {
      if (addOwnedCore(gameSave, pair.domain, pair.id)) {
        newlyUnlocked.push(pair);
      }
    });

    const featureUnlocks = getFeatureUnlocksForLevel(gameSave, previousLevel);

    let showAllCores = false;
    const totalCoreCount = await getTotalCoreCount();
    if (totalCoreCount > 0 && countOwnedCores(gameSave) >= totalCoreCount && !gameSave.AllCoresUnlockedCongrats) {
      gameSave.AllCoresUnlockedCongrats = true;
      showAllCores = true;
    }

    gameSave.Level = previousLevel + 1;
    gameSave.LevelCleared = false;
    syncGameSaveCompatFields(gameSave);

    return {
      newlyUnlocked,
      featureUnlocks,
      showCongrats: previousLevel === 16,
      showAllCores
    };
  }

  function isEquipableDomain(domain) {
    return ['CPU', 'PPU', 'APU', 'SHADER'].includes(String(domain || '').toUpperCase());
  }

  function getSelectedCoreForDomain(domain) {
    switch (domain) {
      case 'CPU':
        return selectedCpu;
      case 'PPU':
        return selectedPpu;
      case 'APU':
        return selectedApu;
      case 'SHADER':
        return selectedShader;
      default:
        return null;
    }
  }

  function setSelectedCoreForDomain(domain, coreId) {
    if (domain === 'CPU') selectedCpu = coreId;
    else if (domain === 'PPU') selectedPpu = coreId;
    else if (domain === 'APU') selectedApu = coreId;
    else if (domain === 'SHADER') selectedShader = coreId;
  }

  function getRewardNoticeLines(rewards) {
    const notices = [];
    (rewards?.featureUnlocks || []).forEach(item => notices.push(item));
    if (rewards?.showCongrats) {
      notices.push('Every level completion now guarantees a fresh core pack.');
    }
    if (rewards?.showAllCores) {
      notices.push('All cores unlocked. The entire library is now in your deck.');
    }
    return notices;
  }

  function allRewardItemsEquipped() {
    const equipableItems = rewardModalState.items.filter(item => item.equipable);
    return equipableItems.length > 0 && equipableItems.every(item => item.equipped);
  }

  function scheduleRewardAutoCloseIfComplete() {
    if (!allRewardItemsEquipped()) {
      return;
    }

    clearRewardAutoCloseTimer();
    rewardModalState.autoCloseTimer = setTimeout(() => {
      void closeUnlockModal();
    }, 260);
  }

  function renderRewardModal() {
    const modal = document.getElementById('unlockModal');
    const grid = document.getElementById('unlockGrid');
    const notices = document.getElementById('unlockNotices');
    const empty = document.getElementById('unlockEmpty');
    const copy = document.getElementById('unlockCopy');
    const kicker = document.getElementById('unlockKicker');
    const title = document.getElementById('unlockTitle');
    const equipAllBtn = document.getElementById('unlockEquipAllBtn');
    if (!modal || !grid || !notices || !empty || !copy || !equipAllBtn || !kicker || !title) {
      return;
    }

    const rewardCount = rewardModalState.items.length;
    const equipableCount = rewardModalState.items.filter(item => item.equipable).length;
    kicker.textContent = rewardModalState.kicker || 'Level Intermission';
    title.textContent = rewardModalState.title || 'New Cards Unlocked';
    copy.textContent = rewardModalState.copy || (rewardCount > 0
      ? 'Click a card to inspect it, equip what you want now, or route everything into the build at once.'
      : 'Deck systems updated. Confirm the reward state and head back into the run.');
    equipAllBtn.textContent = equipableCount > 0 ? 'Equip All' : 'Acknowledge';

    const noticeLines = getRewardNoticeLines(rewardModalState);
    notices.hidden = noticeLines.length === 0;
    notices.innerHTML = noticeLines.map(line => (
      `<div class="unlock-notice">${escapeHtml(line)}</div>`
    )).join('');

    empty.hidden = rewardCount > 0;
    grid.innerHTML = rewardCount > 0
      ? rewardModalState.items.map((item, index) => {
          const showSubtitle = item.subtitle && !['BACKGROUND', 'NULLPROVIDER'].includes(String(item.domain || '').toUpperCase());
          const buttonLabel = item.equipable
            ? (item.equipped ? 'Equipped' : 'Equip')
            : 'Passive Unlock';
          const buttonDisabled = item.equipable ? '' : 'disabled';
          const cardBody = item.svgMarkup
            ? `<div class="unlock-card-visual">${item.svgMarkup}</div>`
            : buildGenericRewardFallback(item);

          return `
            <article class="unlock-card${item.equipped ? ' equipped' : ''}${item.equipable ? '' : ' passive'}" style="--unlock-delay:${index * 85}ms" data-unlock-index="${index}">
              <div class="unlock-card-shell">
                <div class="unlock-card-label">
                  <span class="unlock-card-id">${escapeHtml(item.name)}</span>
                  <span class="unlock-card-domain">${escapeHtml(getRewardDomainLabel(item.domain))}</span>
                </div>
                <div class="unlock-card-stage">${cardBody}</div>
                ${showSubtitle ? `<div class="unlock-card-subtitle">${escapeHtml(item.subtitle)}</div>` : ''}
                ${item.description ? `<div class="unlock-card-description">${escapeHtml(item.description)}</div>` : ''}
                <div class="unlock-card-actions">
                  <button type="button" class="unlock-card-equip" data-unlock-equip="${index}" ${buttonDisabled}>${escapeHtml(buttonLabel)}</button>
                </div>
              </div>
            </article>
          `;
        }).join('')
      : '';

    grid.querySelectorAll('.unlock-card-visual').forEach(node => applySvgRenderQuality(node));
    grid.querySelectorAll('[data-unlock-index]').forEach(card => {
      card.addEventListener('click', () => {
        const index = Number.parseInt(card.getAttribute('data-unlock-index') || '-1', 10);
        const item = rewardModalState.items[index];
        if (!item) {
          return;
        }

        openCardPreview(item.domain, item.id, {
          title: item.name,
          subtitle: item.subtitle || `Unlocked ${getRewardDomainLabel(item.domain)} reward`,
          previewMarkup: item.previewMarkup || ''
        });
      });
    });
    grid.querySelectorAll('[data-unlock-equip]').forEach(button => {
      button.addEventListener('click', (event) => {
        event.preventDefault();
        event.stopPropagation();
        const index = Number.parseInt(button.getAttribute('data-unlock-equip') || '-1', 10);
        if (!Number.isFinite(index) || index < 0) {
          return;
        }
        void equipRewardItem(index);
      });
    });

    modal.style.display = 'flex';
  }

  async function presentLevelRewards(rewards, options = {}) {
    const pendingBundles = Array.isArray(options.pendingBundles) ? options.pendingBundles : [];
    const shouldOpen = (rewards?.newlyUnlocked?.length || 0) > 0
      || (rewards?.featureUnlocks?.length || 0) > 0
      || rewards?.showCongrats
      || rewards?.showAllCores
      || pendingBundles.length > 0;
    if (!shouldOpen) {
      return false;
    }

    clearRewardAutoCloseTimer();
    void playUiSfx(UI_SFX.modalOpen, { key: 'reward-modal-open', cooldownMs: 120 });

    const items = await Promise.all((rewards?.newlyUnlocked || []).map(async pair => {
      const core = coreLookup.get(`${pair.domain}:${pair.id}`);
      const svgMarkup = await getCoreSvgMarkup(pair.domain, pair.id);
      return {
        domain: pair.domain,
        id: pair.id,
        name: core?.name || `${pair.domain}_${pair.id}`,
        svgMarkup,
        equipable: isEquipableDomain(pair.domain),
        equipped: isEquipableDomain(pair.domain) && getSelectedCoreForDomain(pair.domain) === pair.id
      };
    }));

    const pendingItems = (await Promise.all(pendingBundles.flatMap(bundle => {
      const bundleId = String(bundle?.id || '').trim();
      return (Array.isArray(bundle?.items) ? bundle.items : [])
        .map(item => hydratePendingRewardItem(bundleId, item));
    }))).filter(item => item.id && item.name);

    const pendingRewardIds = pendingBundles
      .map(bundle => String(bundle?.id || '').trim())
      .filter(Boolean);

    rewardModalState = {
      items: [...items, ...pendingItems],
      featureUnlocks: Array.isArray(rewards?.featureUnlocks) ? rewards.featureUnlocks.slice() : [],
      showCongrats: Boolean(rewards?.showCongrats),
      showAllCores: Boolean(rewards?.showAllCores),
      pendingRewardIds,
      title: options.title || (pendingItems.length > 0 ? 'New Rewards Ready' : 'New Cards Unlocked'),
      kicker: options.kicker || (pendingItems.length > 0 ? 'Unlock Inbox' : 'Level Intermission'),
      copy: options.copy || '',
      autoCloseTimer: 0
    };

    renderRewardModal();
    scheduleRewardAutoCloseIfComplete();
    return true;
  }

  async function persistRewardSelections(indices) {
    const targetIndices = Array.isArray(indices) ? indices : [];
    const updatedItems = rewardModalState.items.slice();
    let changed = false;

    for (const index of targetIndices) {
      const item = updatedItems[index];
      if (!item || !item.equipable) {
        continue;
      }

      updatedItems.forEach((entry, entryIndex) => {
        if (entryIndex === index) {
          return;
        }

        const sameCoreDomain = ['CPU', 'PPU', 'APU', 'SHADER'].includes(item.domain)
          && entry.domain === item.domain;
        const sameEquipAction = item.equipAction && entry.equipAction === item.equipAction;
        if (sameCoreDomain || sameEquipAction) {
          entry.equipped = false;
        }
      });

      changed = true;
      item.equipped = true;
      if (['CPU', 'PPU', 'APU', 'SHADER'].includes(item.domain)) {
        setSelectedCoreForDomain(item.domain, item.id);
        const preferredKey = PREFERRED_KEY_BY_DOMAIN[item.domain];
        const preferenceName = PREFERENCE_NAME_BY_DOMAIN[item.domain];
        if (!gameSave.Preferences || typeof gameSave.Preferences !== 'object') {
          gameSave.Preferences = {};
        }
        if (preferredKey) {
          gameSave[preferredKey] = item.id;
        }
        if (preferenceName) {
          gameSave.Preferences[preferenceName] = item.id;
          if (item.domain === 'SHADER') {
            gameSave.Preferences.SHADER = item.id;
          }
        }
        continue;
      }

      if (item.equipAction === 'equip-background') {
        const result = await window.webapi?.progression?.equipBackground?.(item.id);
        if (result?.success === false) {
          throw new Error(result.error || 'Failed to equip background');
        }
        gameSave.PreferredBackgroundId = item.id;
        continue;
      }

      if (item.equipAction === 'equip-null-provider') {
        const result = await window.webapi?.progression?.equipNullProvider?.(item.id);
        if (result?.success === false) {
          throw new Error(result.error || 'Failed to equip null provider');
        }
        gameSave.PreferredNullProviderId = item.id;
      }
    }

    if (!changed) {
      await closeUnlockModal();
      return false;
    }

    rewardModalState.items = updatedItems;
    syncGameSaveCompatFields(gameSave);
    if (updatedItems.some(item => ['CPU', 'PPU', 'APU', 'SHADER'].includes(item.domain) && item.equipped)) {
      await saveGameSave();
    }
    updateUI();
    renderRewardModal();
    scheduleRewardAutoCloseIfComplete();
    return true;
  }

  async function equipRewardItem(index) {
    const item = rewardModalState.items[index];
    if (!item || !item.equipable) {
      return;
    }

    if (item.equipped) {
      scheduleRewardAutoCloseIfComplete();
      return;
    }

    void playUiSfx(UI_SFX.select, { key: `reward-equip:${item.domain}:${item.id}`, cooldownMs: 70 });
    await persistRewardSelections([index]);
  }

  async function equipAllRewardItems() {
    const indices = rewardModalState.items
      .map((item, index) => ({ item, index }))
      .filter(entry => entry.item.equipable && !entry.item.equipped)
      .map(entry => entry.index);

    if (indices.length > 0) {
      void playUiSfx(UI_SFX.select, { key: 'reward-equip-all', cooldownMs: 120 });
      await persistRewardSelections(indices);
    }

    await closeUnlockModal();
  }

  function openCorePicker(slotName) {
    if (isSlotLocked(slotName)) {
      return;
    }

    void playUiSfx(UI_SFX.modalOpen, { key: `picker-open:${slotName}`, cooldownMs: 120 });

    currentPickerSlot = slotName;
    
    const modal = document.getElementById('pickerModal');
    const title = document.getElementById('pickerTitle');
    const list = document.getElementById('pickerList');
    const previewStage = document.getElementById('pickerPreviewStage');
    const previewTitle = document.getElementById('pickerPreviewTitle');
    const previewSubtitle = document.getElementById('pickerPreviewSubtitle');

    if (!modal || !title || !list || !previewStage || !previewTitle || !previewSubtitle) {
      return;
    }
    
    const slotType = slotName.toUpperCase();
    title.textContent = `Select ${slotType}`;
    
    // Show only owned cores in this picker.
    const owned = new Set(getOwnedCoreIds(slotType));
    const availableCores = getCoreOptions(slotType).filter(core => owned.has(core.id));
    const currentSelectedId = getSelectedCoreBySlot(slotName);
    
    list.innerHTML = '';
    previewTitle.textContent = 'Hover a core';
    previewSubtitle.textContent = '';
    previewStage.innerHTML = '<div class="card-loading">Hover a core to preview its card.</div>';

    if (availableCores.length === 0) {
      list.innerHTML = '<div class="picker-empty">No cores available for this slot yet.</div>';
      modal.style.display = 'flex';
      return;
    }

    availableCores.forEach(core => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'picker-core-item';
      btn.dataset.coreId = core.id;
      const rating = normalizeCoreRating(core.rating);
      btn.innerHTML = `
        <span class="picker-core-top">
          <span class="picker-core-id">${escapeHtml(core.id)}</span>
          <span class="picker-core-rating rating-tier-${rating}" title="Rating">${renderRatingStars(rating)}</span>
        </span>
        <span class="picker-core-name">${escapeHtml(core.name || core.id)}</span>
      `;
      btn.title = core.name || core.id;

      const activatePreview = () => {
        setPickerActiveItem(list, core.id);
        void updatePickerPreview(slotType, core, {
          stageEl: previewStage,
          titleEl: previewTitle,
          subtitleEl: previewSubtitle
        });
      };

      btn.addEventListener('mouseenter', activatePreview);
      btn.addEventListener('focus', activatePreview);
      btn.addEventListener('click', () => selectCore(slotName, core.id));
      
      list.appendChild(btn);
    });

    const firstCore = availableCores.find(core => core.id === currentSelectedId) || availableCores[0];
    if (firstCore) {
      setPickerActiveItem(list, firstCore.id);
      void updatePickerPreview(slotType, firstCore, {
        stageEl: previewStage,
        titleEl: previewTitle,
        subtitleEl: previewSubtitle
      });
    }
    
    modal.style.display = 'flex';
  }

  function getSelectedCoreBySlot(slotName) {
    if (slotName === 'cpu') return selectedCpu;
    if (slotName === 'ppu') return selectedPpu;
    if (slotName === 'apu') return selectedApu;
    if (slotName === 'shader') return selectedShader;
    return null;
  }

  function normalizeCoreRating(value) {
    const numeric = Number.isFinite(value) ? value : Number(value);
    if (!Number.isFinite(numeric)) {
      return 0;
    }

    return Math.max(0, Math.min(5, Math.round(numeric)));
  }

  function renderRatingStars(rating) {
    const filled = '\u2605';
    const empty = '\u2606';
    return filled.repeat(rating) + empty.repeat(5 - rating);
  }

  function setPickerActiveItem(listEl, coreId) {
    listEl.querySelectorAll('.picker-core-item').forEach(node => {
      if (node.dataset.coreId === coreId) {
        node.classList.add('active');
      } else {
        node.classList.remove('active');
      }
    });
  }

  async function updatePickerPreview(domain, core, refs) {
    const stageEl = refs?.stageEl;
    const titleEl = refs?.titleEl;
    const subtitleEl = refs?.subtitleEl;
    if (!stageEl || !titleEl || !subtitleEl || !core?.id) {
      return;
    }

    titleEl.textContent = `${domain}_${core.id}`;
    subtitleEl.textContent = core.name || '';

    stageEl.innerHTML = '<div class="card-loading">Loading...</div>';
    const previewToken = String(++currentPickerPreviewToken);
    stageEl.dataset.previewToken = previewToken;

    const svgMarkup = await getCoreSvgMarkup(domain, core.id);
    if (stageEl.dataset.previewToken !== previewToken) {
      return;
    }

    renderCardPreview(stageEl, domain, core.id, svgMarkup);
  }

  function isSlotLocked(slotName) {
    switch (slotName) {
      case 'cpu':
        return Boolean(enforcedCpu);
      case 'ppu':
        return Boolean(enforcedPpu);
      case 'apu':
        return Boolean(enforcedApu);
      case 'shader':
        return Boolean(enforcedShader);
      default:
        return false;
    }
  }

  function getOwnedCoreIds(domain) {
    switch (domain) {
      case 'CPU':
        return (gameSave?.ownedCpuIds || []).map(id => normalizeCoreId(id)).filter(Boolean);
      case 'PPU':
        return (gameSave?.ownedPpuIds || []).map(id => normalizeCoreId(id)).filter(Boolean);
      case 'APU':
        return (gameSave?.ownedApuIds || []).map(id => normalizeCoreId(id)).filter(Boolean);
      case 'SHADER':
        return (gameSave?.ownedShaderIds || []).map(id => normalizeCoreId(id)).filter(Boolean);
      default:
        return [];
    }
  }

  function getCoreOptions(domain) {
    if (coreCatalog[domain] && coreCatalog[domain].length > 0) {
      return coreCatalog[domain];
    }

    return (fallbackCoreData[domain] || []).map(id => ({
      id,
      name: id,
      description: '',
      rating: 0,
      category: ''
    }));
  }

  function selectCore(slotName, coreId) {
    const previousCoreId = slotName === 'cpu' ? selectedCpu :
      slotName === 'ppu' ? selectedPpu :
      slotName === 'apu' ? selectedApu :
      selectedShader;

    // Update selection
    if (slotName === 'cpu') selectedCpu = coreId;
    else if (slotName === 'ppu') selectedPpu = coreId;
    else if (slotName === 'apu') selectedApu = coreId;
    else if (slotName === 'shader') selectedShader = coreId;
    
    // Save preference
    if (!gameSave.Preferences) gameSave.Preferences = {};
    if (slotName === 'shader') {
      gameSave.Preferences.Shader = coreId;
      gameSave.Preferences.SHADER = coreId;
    } else {
      gameSave.Preferences[slotName.toUpperCase()] = coreId;
    }
    if (slotName === 'cpu') gameSave.PreferredCpuId = coreId;
    else if (slotName === 'ppu') gameSave.PreferredPpuId = coreId;
    else if (slotName === 'apu') gameSave.PreferredApuId = coreId;
    else if (slotName === 'shader') gameSave.PreferredShaderId = coreId;
    void saveGameSave();

    if (previousCoreId !== coreId) {
      void playUiSfx(UI_SFX.select, { key: `select-core:${slotName}`, cooldownMs: 70 });
    }
    
    // Update UI
    updateUI();
    closePicker();
  }

  function closePicker() {
    const modal = document.getElementById('pickerModal');
    const stage = document.getElementById('pickerPreviewStage');
    const list = document.getElementById('pickerList');
    const title = document.getElementById('pickerPreviewTitle');
    const subtitle = document.getElementById('pickerPreviewSubtitle');
    if (stage) {
      stage.dataset.previewToken = '';
      stage.innerHTML = '';
    }
    if (list) {
      list.innerHTML = '';
    }
    if (title) {
      title.textContent = 'Hover a core';
    }
    if (subtitle) {
      subtitle.textContent = '';
    }
    modal.style.display = 'none';
    currentPickerSlot = null;
  }

  async function advanceLevel(options = {}) {
    if (!levelCleared || stars < requiredStars) {
      return { advanced: false, rewards: null };
    }

    void playUiSfx(UI_SFX.levelAdvance, { key: 'level-advance', cooldownMs: 250 });

    const previousLevel = currentLevel;
    const rewards = await buildLevelRewards(previousLevel);

    currentLevel = gameSave.Level;
    levelCleared = false;

    await saveGameSave();
    await loadGameSave();

    await loadLevel();
    applySavedSelections(gameSave);
    updateUI();

    if (!options.deferPresentation) {
      const pendingBundles = await claimPendingUnlockBundles();
      await presentLevelRewards(rewards, {
        pendingBundles,
        kicker: 'Level Intermission',
        title: pendingBundles.length > 0 ? 'Level Rewards Ready' : 'New Cards Unlocked',
        copy: pendingBundles.length > 0
          ? 'Level rewards and queued unlocks are ready. Inspect the cards, equip what you want now, then return to the build.'
          : ''
      });
    }

    return { advanced: true, rewards };
  }

  async function launchSelectedGame(mode) {
    const actionBtn = mode === 'stage'
      ? document.getElementById('resetBtn')
      : document.getElementById('startBtn');
    if (!actionBtn || actionBtn.disabled || arrivalInProgress) {
      return;
    }

    const payload = buildLaunchPayload(mode);
    if (!payload) {
      return;
    }

    actionBtn.disabled = true;

    try {
      const romRecord = await getStoredRomByName(payload.romKey);
      if (!romRecord || !romRecord.base64) {
        throw new Error(`Selected ROM is not available in storage: ${payload.romKey}`);
      }

      writeWorkflowPayload(WORKFLOW_LAUNCH_KEY, payload);
      writeWorkflowPayload(WORKFLOW_ROM_CACHE_KEY, {
        name: romRecord.name,
        base64: romRecord.base64
      });
      if (window.webapi?.audio?.stopMusic) {
        await window.webapi.audio.stopMusic(350);
      }
      window.location.href = '../AchievementsRuntime/index.html';
    } catch (error) {
      console.error('[Continue] Failed to start game:', error);
      actionBtn.disabled = false;
      updateStartButton();
    }
  }

  async function startGame() {
    const mode = hasTrustedContinueSelected() ? 'continue' : 'stage';
    return launchSelectedGame(mode);
  }

  async function resetGame() {
    return launchSelectedGame('stage');
  }

  // Expose API for debugging
  window.continueBuilder = {
    getGameSave: () => gameSave,
    getState: () => ({
      currentLevel,
      stars,
      selectedCpu,
      selectedPpu,
      selectedApu,
      selectedShader,
      selectedGameId
    }),
    reload: init
  };
})();
