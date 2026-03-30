// AchievementsRuntime - Overlay Mode Script
(function () {
  'use strict';

  const api = window.webapi;
  const lib = window.achievementsLib;
  const CONTINUE_DB_NAME = 'continue-db';
  const CONTINUE_DB_VERSION = 1;
  const DEFAULT_DB_URL = '../shared/models/default-db.json';
  const ACHIEVEMENT_DIFFICULTY_ORDER = ['Easy', 'Hard', 'Insane'];
  const WORKFLOW_LAUNCH_KEY = 'brokenNes.workflow.launch';
  const WORKFLOW_RETURN_KEY = 'brokenNes.workflow.return';
  const WORKFLOW_ROM_CACHE_KEY = 'brokenNes.workflow.rom';
  const NULL_PROVIDER_INTERMISSION_MS = 1337;
  const MAX_VISIBLE_ACHIEVEMENTS = 6;
  const DEBUG_UNLOCK_CLICK_THRESHOLD = 5;
  let achievementsList = [];
  let isInitialized = false;
  let launchPayload = null;
  let workflowResolved = false;
  let activeAchievementGameId = '';
  let activeAchievementRomKey = '';
  let achievementCatalogContext = null;
  let achievementCatalogContextPromise = null;

  // DOM Elements
  const achievementsOverlay = document.querySelector('.achievements-overlay');
  const returnToDeckButton = document.getElementById('returnToDeckButton');
  const showAchievementsButton = document.getElementById('showAchievementsButton');
  const achievementsListModal = document.getElementById('achievementsListModal');
  const achievementsListSummary = document.getElementById('achievementsListSummary');
  const achievementsListContainer = document.getElementById('achievementsListContainer');
  const closeAchievementsListButton = document.getElementById('closeAchievementsListButton');

  // Monitoring state
  let isMonitoring = false;
  let monitoringInterval = null;
  let knownUnlockedAchievements = new Set();

  // Modal queue management
  const MODAL_DISPLAY_DURATION = 3000; // milliseconds (configurable)
  let modalQueue = [];
  let isModalDisplaying = false;
  let debugUnlockClickState = {
    achievementId: null,
    count: 0
  };
  const achievementModal = document.getElementById('achievementModal');
  const modalAchievementName = document.getElementById('modalAchievementName');
  const modalProgressBar = document.getElementById('modalProgressBar');

  function readPayload(key) {
    try {
      const raw = localStorage.getItem(key);
      return raw ? JSON.parse(raw) : null;
    } catch (error) {
      console.warn('[AchievementsRuntime] Failed to read payload:', error);
      return null;
    }
  }

  function writePayload(key, payload) {
    try {
      localStorage.setItem(key, JSON.stringify(payload));
      return true;
    } catch (error) {
      console.warn('[AchievementsRuntime] Failed to write payload:', error);
      return false;
    }
  }

  function clearPayload(key) {
    try {
      localStorage.removeItem(key);
    } catch (error) {
      console.warn('[AchievementsRuntime] Failed to clear payload:', error);
    }
  }

  function readLaunchPayloadFromQuery() {
    try {
      const params = new URLSearchParams(window.location.search || '');
      const romKeyRaw = params.get('romKey');
      if (!romKeyRaw || !romKeyRaw.trim()) {
        return null;
      }

      const romKey = romKeyRaw.trim();
      const modeRaw = (params.get('mode') || 'continue').trim().toLowerCase();
      const mode = modeRaw === 'stage' ? 'stage' : 'continue';
      const titleRaw = params.get('title');
      const title = titleRaw && titleRaw.trim() ? titleRaw.trim() : romKey;

      return {
        mode,
        romKey,
        title,
        createdAt: new Date().toISOString(),
        cores: null
      };
    } catch (error) {
      console.warn('[AchievementsRuntime] Failed to parse launch payload from query:', error);
      return null;
    }
  }

  function normalizeValue(value) {
    return typeof value === 'string' && value.trim()
      ? value.trim()
      : '';
  }

  function normalizeLowerValue(value) {
    const normalized = normalizeValue(value);
    return normalized ? normalized.toLowerCase() : '';
  }

  function normalizeAchievementDifficulty(value) {
    return ACHIEVEMENT_DIFFICULTY_ORDER.includes(value) ? value : 'Easy';
  }

  async function openContinueDbStore(storeName) {
    return new Promise((resolve, reject) => {
      if (!window.indexedDB) {
        reject(new Error('IndexedDB unavailable'));
        return;
      }

      const request = indexedDB.open(CONTINUE_DB_NAME, CONTINUE_DB_VERSION);
      request.onupgradeneeded = () => {
        const db = request.result;
        if (!db.objectStoreNames.contains('games')) {
          db.createObjectStore('games', { keyPath: 'id' });
        }
        if (!db.objectStoreNames.contains('achievements')) {
          db.createObjectStore('achievements', { keyPath: 'id' });
        }
      };
      request.onsuccess = () => {
        try {
          resolve(request.result.transaction(storeName, 'readonly').objectStore(storeName));
        } catch (error) {
          reject(error);
        }
      };
      request.onerror = () => reject(request.error || new Error('IndexedDB open error'));
    });
  }

  async function getAllContinueDbRecords(storeName) {
    try {
      const store = await openContinueDbStore(storeName);
      return await new Promise((resolve, reject) => {
        const request = store.getAll();
        request.onsuccess = () => resolve(Array.isArray(request.result) ? request.result : []);
        request.onerror = () => reject(request.error || new Error(`Failed to read ${storeName}`));
      });
    } catch (error) {
      console.warn(`[AchievementsRuntime] Failed to read ${storeName} from continue-db:`, error);
      return [];
    }
  }

  async function loadCatalogRecords() {
    const [dbGames, dbAchievements] = await Promise.all([
      getAllContinueDbRecords('games'),
      getAllContinueDbRecords('achievements')
    ]);

    if (dbGames.length > 0 || dbAchievements.length > 0) {
      return { games: dbGames, achievements: dbAchievements };
    }

    try {
      const response = await fetch(DEFAULT_DB_URL, { cache: 'no-cache' });
      if (!response.ok) {
        throw new Error(`Default DB fetch failed with status ${response.status}`);
      }

      const payload = await response.json();
      const data = payload && typeof payload === 'object' ? payload.data || {} : {};
      return {
        games: Array.isArray(data.games) ? data.games : [],
        achievements: Array.isArray(data.achievements) ? data.achievements : []
      };
    } catch (error) {
      console.warn('[AchievementsRuntime] Failed to load fallback catalog:', error);
      return { games: [], achievements: [] };
    }
  }

  function buildAchievementCatalogContext(records) {
    const games = Array.isArray(records?.games) ? records.games : [];
    const achievements = Array.isArray(records?.achievements) ? records.achievements : [];
    const normalizedGameId = normalizeValue(activeAchievementGameId);
    const normalizedRomKey = normalizeLowerValue(activeAchievementRomKey || launchPayload?.romKey);

    let gameId = normalizedGameId;
    if (!gameId && normalizedRomKey) {
      const matchedGame = games.find(game => normalizeLowerValue(game?.romKey || game?.name) === normalizedRomKey);
      gameId = normalizeValue(matchedGame?.id);
    }

    const visibleCatalogAchievements = gameId
      ? achievements.filter(achievement => normalizeValue(achievement?.gameId) === gameId)
      : [];
    const achievementsById = new Map();
    const achievementsByTitle = new Map();

    visibleCatalogAchievements.forEach(achievement => {
      const id = normalizeValue(achievement?.id);
      const title = normalizeLowerValue(achievement?.title || achievement?.metaAchievementName);
      if (id && !achievementsById.has(id)) {
        achievementsById.set(id, achievement);
      }
      if (title && !achievementsByTitle.has(title)) {
        achievementsByTitle.set(title, achievement);
      }
    });

    return {
      gameId,
      achievements: visibleCatalogAchievements,
      achievementsById,
      achievementsByTitle
    };
  }

  async function ensureAchievementCatalogContext(forceReload = false) {
    if (!forceReload && achievementCatalogContext) {
      return achievementCatalogContext;
    }

    if (!forceReload && achievementCatalogContextPromise) {
      return achievementCatalogContextPromise;
    }

    achievementCatalogContextPromise = (async () => {
      const records = await loadCatalogRecords();
      achievementCatalogContext = buildAchievementCatalogContext(records);
      return achievementCatalogContext;
    })();

    try {
      return await achievementCatalogContextPromise;
    } finally {
      achievementCatalogContextPromise = null;
    }
  }

  function getCatalogAchievementForRuntime(achievement, catalogContext) {
    const achievementId = normalizeValue(lib.getAchievementId(achievement));
    if (achievementId && catalogContext.achievementsById.has(achievementId)) {
      return catalogContext.achievementsById.get(achievementId);
    }

    const titleKey = normalizeLowerValue(lib.getAchievementTitle(achievement) || lib.getAchievementDescription(achievement));
    return titleKey ? catalogContext.achievementsByTitle.get(titleKey) || null : null;
  }

  async function getVisibleAchievements(achievements) {
    const list = Array.isArray(achievements) ? achievements : [];
    const catalogContext = await ensureAchievementCatalogContext();
    const shouldFilterByCatalog = catalogContext.achievementsById.size > 0 || catalogContext.achievementsByTitle.size > 0;

    const visibleAchievements = list
      .map(achievement => {
        const catalogAchievement = getCatalogAchievementForRuntime(achievement, catalogContext);
        if (shouldFilterByCatalog && !catalogAchievement) {
          return null;
        }

        return {
          achievement,
          difficulty: normalizeAchievementDifficulty(catalogAchievement?.difficulty),
          title: lib.getAchievementTitle(achievement) || 'Untitled achievement',
          description: lib.getAchievementDescription(achievement),
          completed: lib.isAchievementCompleted(achievement),
          id: normalizeValue(lib.getAchievementId(achievement))
        };
      })
      .filter(Boolean);

    visibleAchievements.sort((left, right) => {
      const difficultyCmp = ACHIEVEMENT_DIFFICULTY_ORDER.indexOf(left.difficulty) - ACHIEVEMENT_DIFFICULTY_ORDER.indexOf(right.difficulty);
      if (difficultyCmp !== 0) {
        return difficultyCmp;
      }

      if (left.completed !== right.completed) {
        return left.completed ? 1 : -1;
      }

      return left.title.localeCompare(right.title, undefined, { sensitivity: 'base' });
    });

    return visibleAchievements;
  }

  async function getPreferredNullProviderId() {
    try {
      const save = await window.gameSave.load();
      const preferred = save?.PreferredNullProviderId;
      return typeof preferred === 'string' && preferred.trim() ? preferred.trim() : 'Static';
    } catch (error) {
      console.warn('[AchievementsRuntime] Failed to read preferred null provider:', error);
      return 'Static';
    }
  }

  async function runNullProviderIntermission() {
    const mode = String(launchPayload?.mode || '').toLowerCase();
    if (mode !== 'continue' && mode !== 'stage') {
      return;
    }

    const preferredNullProviderId = await getPreferredNullProviderId();

    try {
      // Null providers render on the test ROM path, so switch there for the intermission.
      await api.emulator.closeRom();
    } catch (error) {
      console.warn('[AchievementsRuntime] Failed to switch to test ROM for null provider intermission:', error);
    }

    try {
      const setResult = await api.emulator.setNullProvider(preferredNullProviderId);
      if (!setResult || setResult.success === false) {
        console.warn('[AchievementsRuntime] Failed to apply null provider intermission:', setResult?.error || preferredNullProviderId);
      }
    } catch (error) {
      console.warn('[AchievementsRuntime] Null provider intermission setup failed:', error);
    }

    try {
      await api.emulator.resume();
    } catch (error) {
      console.warn('[AchievementsRuntime] Failed to resume for null provider intermission:', error);
    }

    await new Promise(resolve => setTimeout(resolve, NULL_PROVIDER_INTERMISSION_MS));

    try {
      await api.emulator.pause();
    } catch (error) {
      console.warn('[AchievementsRuntime] Failed to pause after null provider intermission:', error);
    }
  }

  function normalizeContinueSlotKey(romKey) {
    return typeof romKey === 'string' && romKey.trim()
      ? romKey.trim().toLowerCase()
      : '';
  }

  async function markTrustedContinueState() {
    if (!launchPayload?.romKey) {
      return null;
    }

    const save = await window.gameSave.load();
    const normalizedRomKey = normalizeContinueSlotKey(launchPayload.romKey);
    const timestamp = new Date().toISOString();

    save.PendingDeckContinue = true;
    save.PendingDeckContinueRom = launchPayload.romKey;
    save.PendingDeckContinueTitle = launchPayload.title || launchPayload.romKey;
    save.PendingDeckContinueAtUtc = timestamp;
    save.ContinueSlots = save.ContinueSlots && typeof save.ContinueSlots === 'object'
      ? save.ContinueSlots
      : {};

    if (normalizedRomKey) {
      save.ContinueSlots[normalizedRomKey] = {
        romKey: launchPayload.romKey,
        title: launchPayload.title || launchPayload.romKey,
        updatedAtUtc: timestamp,
        previewImagePath: null
      };
    }

    await window.gameSave.save(save);
    return save;
  }

  async function returnToContinue(options = {}) {
    if (workflowResolved && !options.force) {
      return;
    }

    workflowResolved = true;
    stopMonitoring();

    let checkpointCaptured = Boolean(options.checkpointAlreadyCaptured);
    let continueSaveError = null;
    let save = null;

    if (!checkpointCaptured) {
      try {
        const saveResult = await api.emulator.saveContinueState();
        checkpointCaptured = Boolean(saveResult && saveResult.success !== false);
        if (!checkpointCaptured) {
          continueSaveError = saveResult?.error || 'Failed to save continue state';
          console.warn('[AchievementsRuntime] Continue-state capture failed. Returning anyway and keeping existing checkpoint:', continueSaveError);
        }
      } catch (error) {
        continueSaveError = error?.message || 'Failed to save continue state';
        console.warn('[AchievementsRuntime] Continue-state capture threw. Returning anyway and keeping existing checkpoint:', error);
      }
    }

    try {
      await api.emulator.pause();
    } catch (error) {
      console.warn('[AchievementsRuntime] Failed to pause before return:', error);
    }

    if (checkpointCaptured) {
      try {
        save = await markTrustedContinueState();
      } catch (error) {
        console.warn('[AchievementsRuntime] Failed to mark trusted continue state:', error);
      }
    }

    const payloadWritten = writePayload(WORKFLOW_RETURN_KEY, {
      achievementId: options.achievementId || null,
      achievementTitle: options.achievementTitle || null,
      showArrival: Boolean(options.showArrival || options.achievementId || options.achievementTitle || options.firstClear),
      romKey: launchPayload?.romKey || null,
      title: launchPayload?.title || launchPayload?.romKey || null,
      previousLevel: Number.isFinite(launchPayload?.level) ? launchPayload.level : (save?.Level || 1),
      previousStarCount: Number.isFinite(launchPayload?.previousStarCount)
        ? launchPayload.previousStarCount
        : Math.max(0, (save?.Achievements || []).length),
      firstClear: Boolean(options.firstClear),
      continueCheckpointCaptured: checkpointCaptured,
      continueCheckpointError: continueSaveError,
      createdAt: new Date().toISOString()
    });

    if (!payloadWritten) {
      console.warn('[AchievementsRuntime] Failed to persist return payload; navigating anyway.');
    }

    try {
      await api.navigation.goToWeb();
    } catch (error) {
      console.warn('[AchievementsRuntime] Failed to switch to web host before return:', error);
    }

    window.location.href = '../Continue/index.html';
  }

  async function ensureCheckpointCapturedBeforeModal(initiallyCaptured) {
    if (initiallyCaptured) {
      return { checkpointCaptured: true, continueSaveError: null };
    }

    try {
      const saveResult = await api.emulator.saveContinueState();
      const checkpointCaptured = Boolean(saveResult && saveResult.success !== false);
      const continueSaveError = checkpointCaptured
        ? null
        : (saveResult?.error || 'Failed to save continue state');
      if (continueSaveError) {
        console.warn('[AchievementsRuntime] Continue-state pre-capture failed after unlock:', continueSaveError);
      }

      return { checkpointCaptured, continueSaveError };
    } catch (error) {
      const continueSaveError = error?.message || 'Failed to save continue state';
      console.warn('[AchievementsRuntime] Continue-state pre-capture threw after unlock:', error);
      return { checkpointCaptured: false, continueSaveError };
    }
  }

  function handleGlobalKeydown(event) {
    if (event.defaultPrevented) {
      return;
    }

    if (event.code === 'KeyO') {
      event.preventDefault();
      void triggerDebugQuickStateAction('load');
      return;
    }

    if (event.code === 'KeyP') {
      event.preventDefault();
      void triggerDebugQuickStateAction('save');
      return;
    }

    if (event.key !== 'Escape') {
      return;
    }

    if (isAchievementsListModalOpen()) {
      event.preventDefault();
      closeAchievementsModal();
      return;
    }

    event.preventDefault();
    void returnToContinue();
  }

  async function triggerDebugQuickStateAction(action) {
    try {
      const result = action === 'save'
        ? await api.emulator.quickSaveState()
        : await api.emulator.quickLoadState();

      if (!result || result.success !== true) {
        const reason = result?.error || 'Disabled or unavailable';
        console.debug(`[AchievementsRuntime] Debug ${action} ignored: ${reason}`);
      }
    } catch (error) {
      console.warn(`[AchievementsRuntime] Debug ${action} failed:`, error);
    }
  }

  function isAchievementsListModalOpen() {
    return achievementsListModal && achievementsListModal.style.display !== 'none';
  }

  async function ensureAchievementsLoaded() {
    if (achievementsList.length > 0) {
      return achievementsList;
    }

    if (!isInitialized) {
      return [];
    }

    try {
      const result = await api.achievements.getList();
      if (result.success && Array.isArray(result.achievements)) {
        achievementsList = result.achievements;
      }
    } catch (error) {
      console.warn('[AchievementsRuntime] Failed to load achievements list for modal:', error);
    }

    return achievementsList;
  }

  async function openAchievementsModal() {
    const list = await ensureAchievementsLoaded();
    await renderAchievementsModal(list);
    if (achievementsListModal) {
      achievementsListModal.style.display = 'flex';
    }
  }

  function closeAchievementsModal() {
    resetDebugUnlockClickState();
    if (achievementsListModal) {
      achievementsListModal.style.display = 'none';
    }
  }

  function resetDebugUnlockClickState() {
    debugUnlockClickState = {
      achievementId: null,
      count: 0
    };
  }

  function registerDebugUnlockClick(achievementId) {
    if (!achievementId) {
      resetDebugUnlockClickState();
      return 0;
    }

    if (debugUnlockClickState.achievementId !== achievementId) {
      resetDebugUnlockClickState();
      debugUnlockClickState.achievementId = achievementId;
      debugUnlockClickState.count = 1;
    } else {
      debugUnlockClickState.count += 1;
    }

    return debugUnlockClickState.count;
  }

  async function handleAchievementListRowClick(event) {
    const row = event.target.closest('.achievements-list-row');
    if (!row || !achievementsListContainer || !achievementsListContainer.contains(row)) {
      return;
    }

    const achievementId = row.dataset.achievementId;
    if (!achievementId) {
      return;
    }

    const achievement = achievementsList.find(a => lib.getAchievementId(a) === achievementId);
    if (!achievement || lib.isAchievementCompleted(achievement)) {
      resetDebugUnlockClickState();
      return;
    }

    const clickCount = registerDebugUnlockClick(achievementId);
    if (clickCount < DEBUG_UNLOCK_CLICK_THRESHOLD) {
      return;
    }

    resetDebugUnlockClickState();

    try {
      const forceResult = await api.achievements.forceComplete(achievementId);
      if (!forceResult || forceResult.success === false) {
        console.warn('[AchievementsRuntime] Debug unlock failed:', forceResult?.error || achievementId);
        return;
      }

      knownUnlockedAchievements.add(achievementId);
      await refreshAchievementsList();
      closeAchievementsModal();
      await handleAchievementUnlock(achievementId, { checkpointCaptured: false });
    } catch (error) {
      console.warn('[AchievementsRuntime] Debug unlock failed with exception:', error);
    }
  }

  async function renderAchievementsModal(achievements) {
    if (!achievementsListSummary || !achievementsListContainer) {
      return;
    }

    const visibleAchievements = await getVisibleAchievements(achievements);
    const unlockedCount = visibleAchievements.reduce((count, achievement) => (
      achievement.completed ? count + 1 : count
    ), 0);

    achievementsListSummary.textContent = `${unlockedCount}/${visibleAchievements.length} unlocked`;

    if (visibleAchievements.length === 0) {
      achievementsListContainer.innerHTML = '<div class="achievements-list-empty">No achievements found for this game.</div>';
      return;
    }

    const groupedAchievements = ACHIEVEMENT_DIFFICULTY_ORDER.map(difficulty => ({
      difficulty,
      achievements: visibleAchievements.filter(achievement => achievement.difficulty === difficulty)
    })).filter(group => group.achievements.length > 0);

    let html = '';
    groupedAchievements.forEach(group => {
      const groupUnlockedCount = group.achievements.filter(achievement => achievement.completed).length;
      html += `<section class="achievements-list-group achievements-list-group-${group.difficulty.toLowerCase()}">`;
      html += '<div class="achievements-list-group-header">';
      html += `<span class="achievements-list-group-title">${lib.escapeHtml(group.difficulty)}</span>`;
      html += `<span class="achievements-list-group-progress">${groupUnlockedCount}/${group.achievements.length}</span>`;
      html += '</div>';
      html += '<div class="achievements-list-group-items">';

      group.achievements.forEach(item => {
        html += `<div class="achievements-list-row ${item.completed ? 'unlocked' : 'locked'}" data-achievement-id="${lib.escapeHtml(item.id)}">`;
        html += `<div class="achievements-list-row-title">${lib.escapeHtml(item.title)}</div>`;
        html += `<div class="achievements-list-row-state">${item.completed ? 'Unlocked' : 'Locked'}</div>`;
        html += '</div>';
      });

      html += '</div>';
      html += '</section>';
    });

    achievementsListContainer.innerHTML = html;
  }

  // Initialize on page load
  function init() {
    document.addEventListener('keydown', handleGlobalKeydown);
    returnToDeckButton?.addEventListener('click', () => {
      void returnToContinue();
    });
    showAchievementsButton?.addEventListener('click', () => {
      void openAchievementsModal();
    });
    closeAchievementsListButton?.addEventListener('click', closeAchievementsModal);
    achievementsListContainer?.addEventListener('click', (event) => {
      void handleAchievementListRowClick(event);
    });
    achievementsListModal?.addEventListener('click', (event) => {
      if (event.target === achievementsListModal) {
        closeAchievementsModal();
      }
    });
    autoInitialize();
  }

  // Auto-initialize achievements on page load
  async function autoInitialize() {
    achievementsOverlay.innerHTML = '<div class="loading">Loading...</div>';
    launchPayload = readPayload(WORKFLOW_LAUNCH_KEY);

    if (!launchPayload || !launchPayload.romKey) {
      const queryPayload = readLaunchPayloadFromQuery();
      if (queryPayload && queryPayload.romKey) {
        launchPayload = queryPayload;
        writePayload(WORKFLOW_LAUNCH_KEY, queryPayload);
      }
    }

    if (!launchPayload || !launchPayload.romKey) {
      achievementsOverlay.innerHTML = '<div class="empty-state">No launch payload found.</div>';
      return;
    }
    
    // Small delay to let UI render
    await new Promise(resolve => setTimeout(resolve, 100));
    const booted = await bootstrapRuntime();
    if (!booted) {
      return;
    }
    await initializeAchievements();
  }

  async function bootstrapRuntime() {
    try {
      await api.navigation.goToOverlay();
      const isContinueLaunch = launchPayload.mode === 'continue';

      await runNullProviderIntermission();

      const romPayload = readPayload(WORKFLOW_ROM_CACHE_KEY);
      const romResult = romPayload && romPayload.base64
        ? await api.emulator.loadRomBase64(romPayload.name || launchPayload.romKey, romPayload.base64)
        : await api.emulator.loadRomKey(launchPayload.romKey);
      if (!romResult || romResult.success === false) {
        const errorMessage = romResult?.error || 'Unable to load selected ROM';
        achievementsOverlay.innerHTML = `<div class="empty-state">${lib.escapeHtml(errorMessage)}</div>`;
        return false;
      }

      if (isContinueLaunch) {
        const loadStateResult = await api.emulator.loadContinueState(launchPayload.romKey);
        if (!loadStateResult || loadStateResult.success === false) {
          const errorMessage = loadStateResult?.error || `Continue state not found for ${launchPayload.romKey}`;
          console.warn('[AchievementsRuntime] Continue-state load failed:', errorMessage);
          achievementsOverlay.innerHTML = `<div class="empty-state">${lib.escapeHtml(errorMessage)}</div>`;
          return false;
        }
      }

      if (launchPayload.cores) {
        await api.cores.apply({
          cpuId: launchPayload.cores.cpuId,
          ppuId: launchPayload.cores.ppuId,
          apuId: launchPayload.cores.apuId,
          overrideReason: 'deck-enforced'
        });

        if (launchPayload.cores.shaderId) {
          await api.shader.setShader(launchPayload.cores.shaderId, 'deck-enforced');
        }
      }

      await api.emulator.resume();
      clearPayload(WORKFLOW_LAUNCH_KEY);
      clearPayload(WORKFLOW_ROM_CACHE_KEY);
      return true;
    } catch (error) {
      console.error('[AchievementsRuntime] Bootstrap failed:', error);
      achievementsOverlay.innerHTML = `<div class="empty-state">${lib.escapeHtml(error?.message || 'Runtime bootstrap failed')}</div>`;
      return false;
    }
  }

  // Initialize achievements
  async function initializeAchievements() {
    try {
      const completedIds = await lib.getSavedAchievements();
      const result = await api.achievements.init({ completedIds });

      if (result.success) {
        isInitialized = true;
        activeAchievementGameId = normalizeValue(result.gameId);
        activeAchievementRomKey = normalizeValue(result.romKey || launchPayload?.romKey);
        achievementCatalogContext = null;
        achievementCatalogContextPromise = null;
        
        // Automatically load the achievements list after initialization
        await refreshAchievementsList();
        
        // Track currently unlocked achievements
        initializeKnownUnlocked();
        
        // Auto-start monitoring
        startMonitoring();
      } else {
        // Display the actual error message from the server
        const errorMsg = result.error || 'Failed to initialize';
        console.error('Achievement initialization failed:', errorMsg);
        achievementsOverlay.innerHTML = `<div class="empty-state">${lib.escapeHtml(errorMsg)}</div>`;
      }
    } catch (error) {
      console.error('Achievement initialization error:', error);
      const errorMsg = error?.message || 'Error loading';
      achievementsOverlay.innerHTML = `<div class="empty-state">${lib.escapeHtml(errorMsg)}</div>`;
    }
  }

  // Refresh achievements list
  async function refreshAchievementsList() {
    if (!isInitialized) {
      return;
    }

    try {
      const result = await api.achievements.getList();

      if (result.success && result.achievements) {
        achievementsList = result.achievements;
        
        if (achievementsList.length === 0) {
          achievementsOverlay.innerHTML = '<div class="empty-state">No achievements</div>';
        } else {
          await displayAchievements(achievementsList);
        }
      } else {
        achievementsOverlay.innerHTML = '<div class="empty-state">Load failed</div>';
      }
    } catch (error) {
      achievementsOverlay.innerHTML = '<div class="empty-state">Error</div>';
      console.error('Error loading achievements:', error);
    }
  }

  async function getVisibleUnlockedAchievements(achievements) {
    const unlockedById = new Map(
      achievements
        .filter(achievement => lib.isAchievementCompleted(achievement))
        .map(achievement => [lib.getAchievementId(achievement), achievement])
    );

    if (unlockedById.size === 0) {
      return [];
    }

    const save = await window.gameSave.load();
    const savedIds = Array.isArray(save?.Achievements) ? save.Achievements : [];
    const orderedAchievements = [];

    savedIds.forEach(id => {
      const achievement = unlockedById.get(id);
      if (!achievement) {
        return;
      }

      orderedAchievements.push(achievement);
      unlockedById.delete(id);
    });

    unlockedById.forEach(achievement => {
      orderedAchievements.push(achievement);
    });

    return orderedAchievements.slice(-MAX_VISIBLE_ACHIEVEMENTS);
  }

  // Display achievements in the overlay
  async function displayAchievements(achievements) {
    achievementsOverlay.innerHTML = '';

    const visibleAchievements = await getVisibleAchievements(achievements);
    const unlockedAchievements = await getVisibleUnlockedAchievements(visibleAchievements.map(item => item.achievement));

    if (unlockedAchievements.length === 0) {
      achievementsOverlay.innerHTML = '<div class="empty-state">No achievements unlocked yet</div>';
      return;
    }

    unlockedAchievements.forEach(achievement => {
      const card = createAchievementCard(achievement);
      achievementsOverlay.appendChild(card);
    });
  }

  // Create achievement card element (two-line version)
  function createAchievementCard(achievement) {
    const isCompleted = lib.isAchievementCompleted(achievement);
    const card = document.createElement('div');
    card.className = `achievement-card ${isCompleted ? 'completed' : 'locked'}`;
    
    const icon = isCompleted ? '✓' : '○';
    const title = lib.getAchievementTitle(achievement);
    const description = lib.getAchievementDescription(achievement);

    card.innerHTML = `
      <div class="achievement-header">
        <span class="achievement-icon">${icon}</span>
        <span class="achievement-title">${lib.escapeHtml(title)}</span>
      </div>
      <div class="achievement-description">${lib.escapeHtml(description)}</div>
    `;

    return card;
  }

  // Initialize known unlocked achievements
  function initializeKnownUnlocked() {
    knownUnlockedAchievements.clear();
    achievementsList.forEach(achievement => {
      if (lib.isAchievementCompleted(achievement)) {
        const id = lib.getAchievementId(achievement);
        knownUnlockedAchievements.add(id);
      }
    });
  }

  // Start monitoring for achievement unlocks
  function startMonitoring() {
    isMonitoring = true;

    // Poll for achievement updates every 100ms (10 times per second)
    monitoringInterval = setInterval(async () => {
      try {
        await evaluateAchievementFrame();
      } catch (error) {
        console.error('Error during monitoring:', error);
      }
    }, 100);
  }

  // Stop monitoring
  function stopMonitoring() {
    isMonitoring = false;
    if (monitoringInterval) {
      clearInterval(monitoringInterval);
      monitoringInterval = null;
    }
  }

  // Evaluate current frame for achievements
  async function evaluateAchievementFrame() {
    try {
      const result = await api.achievements.evaluateFrame();
      
      if (result.success && result.unlockedThisFrame && result.unlockedThisFrame.length > 0) {
        // New achievements unlocked!
        for (const achievementId of result.unlockedThisFrame) {
          if (!knownUnlockedAchievements.has(achievementId)) {
            knownUnlockedAchievements.add(achievementId);
            await handleAchievementUnlock(achievementId, {
              checkpointCaptured: Boolean(result.continueCheckpointCaptured)
            });
          }
        }
        
        // Refresh the list to show updated states
        await refreshAchievementsList();
      }
    } catch (error) {
      console.error('Error evaluating achievement frame:', error);
    }
  }

  // Handle achievement unlock event
  async function handleAchievementUnlock(achievementId, options = {}) {
    if (workflowResolved) {
      return;
    }

    workflowResolved = true;
    stopMonitoring();

    console.log(`Achievement unlocked: ${achievementId}`);
    
    // Find the achievement details
    const achievement = achievementsList.find(a => lib.getAchievementId(a) === achievementId);
    const title = achievement ? lib.getAchievementTitle(achievement) : achievementId;
    
    console.log(`🎉 Achievement Unlocked: ${title}`);
    
    // Save the achievement to game save
    await lib.saveAchievement(achievementId);

    try {
      const checkpointResult = await ensureCheckpointCapturedBeforeModal(Boolean(options.checkpointCaptured));
      const save = await window.gameSave.load();
      const firstClear = !Boolean(save.LevelCleared);
      save.LevelCleared = true;
      await window.gameSave.save(save);

      const modalPromise = displayAchievementModal(title);
      const sfxPromise = lib.playRandomVictorySfx();
      await Promise.allSettled([modalPromise, sfxPromise]);

      await returnToContinue({
        achievementId,
        achievementTitle: title,
        showArrival: true,
        firstClear,
        checkpointAlreadyCaptured: checkpointResult.checkpointCaptured,
        force: true
      });
    } catch (error) {
      console.error('[AchievementsRuntime] Failed to resolve unlock workflow:', error);
      achievementsOverlay.innerHTML = `<div class="empty-state">${lib.escapeHtml(error?.message || 'Failed to return to Continue')}</div>`;
    }
  }

  // Queue an achievement modal for display
  function queueAchievementModal(achievementTitle) {
    modalQueue.push(achievementTitle);
    
    // If no modal is currently displaying, start processing the queue
    if (!isModalDisplaying) {
      processModalQueue();
    }
  }

  // Process the modal queue
  async function processModalQueue() {
    if (modalQueue.length === 0) {
      isModalDisplaying = false;
      return;
    }

    isModalDisplaying = true;
    const achievementTitle = modalQueue.shift();
    
    // Display the modal
    await displayAchievementModal(achievementTitle);
    
    // Process the next item in the queue
    processModalQueue();
  }

  // Display achievement unlock modal with progress bar
  function displayAchievementModal(achievementTitle) {
    return new Promise((resolve) => {
      // Set the achievement name
      modalAchievementName.textContent = achievementTitle;
      
      // Reset progress bar
      modalProgressBar.style.width = '0%';
      modalProgressBar.style.transition = 'none';
      
      // Show the modal
      achievementModal.style.display = 'block';
      
      // Force reflow to ensure the transition works
      void modalProgressBar.offsetWidth;
      
      // Animate progress bar
      modalProgressBar.style.transition = `width ${MODAL_DISPLAY_DURATION}ms linear`;
      modalProgressBar.style.width = '100%';
      
      // Hide modal after duration
      setTimeout(() => {
        achievementModal.style.display = 'none';
        resolve();
      }, MODAL_DISPLAY_DURATION);
    });
  }

  // Start the app
  document.addEventListener('DOMContentLoaded', init);
})();
