// AchievementsRuntime - Overlay Mode Script
(function () {
  'use strict';

  const api = window.webapi;
  const lib = window.achievementsLib;
  const WORKFLOW_LAUNCH_KEY = 'brokenNes.workflow.launch';
  const WORKFLOW_RETURN_KEY = 'brokenNes.workflow.return';
  const WORKFLOW_ROM_CACHE_KEY = 'brokenNes.workflow.rom';
  const MAX_VISIBLE_ACHIEVEMENTS = 6;
  let achievementsList = [];
  let isInitialized = false;
  let launchPayload = null;
  let workflowResolved = false;

  // DOM Elements
  const achievementsOverlay = document.querySelector('.achievements-overlay');
  const returnToDeckButton = document.getElementById('returnToDeckButton');

  // Monitoring state
  let isMonitoring = false;
  let monitoringInterval = null;
  let knownUnlockedAchievements = new Set();

  // Modal queue management
  const MODAL_DISPLAY_DURATION = 3000; // milliseconds (configurable)
  let modalQueue = [];
  let isModalDisplaying = false;
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

    const wasResolved = workflowResolved;
    workflowResolved = true;
    stopMonitoring();

    try {
      if (!options.checkpointAlreadyCaptured) {
        const saveResult = await api.emulator.saveContinueState();
        if (!saveResult || saveResult.success === false) {
          throw new Error(saveResult?.error || 'Failed to save continue state');
        }
      }

      try {
        await api.emulator.pause();
      } catch (error) {
        console.warn('[AchievementsRuntime] Failed to pause before return:', error);
      }

      const save = await markTrustedContinueState();

      writePayload(WORKFLOW_RETURN_KEY, {
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
        createdAt: new Date().toISOString()
      });

      await api.navigation.goToWeb();
      window.location.href = '../Continue/index.html';
    } catch (error) {
      workflowResolved = wasResolved;
      console.error('[AchievementsRuntime] Failed to return to Continue:', error);
      achievementsOverlay.innerHTML = `<div class="empty-state">${lib.escapeHtml(error?.message || 'Failed to return to Continue')}</div>`;
    }
  }

  function handleGlobalKeydown(event) {
    if (event.key !== 'Escape' || event.defaultPrevented) {
      return;
    }

    event.preventDefault();
    void returnToContinue();
  }

  // Initialize on page load
  function init() {
    document.addEventListener('keydown', handleGlobalKeydown);
    returnToDeckButton?.addEventListener('click', () => {
      void returnToContinue();
    });
    autoInitialize();
  }

  // Auto-initialize achievements on page load
  async function autoInitialize() {
    achievementsOverlay.innerHTML = '<div class="loading">Loading...</div>';
    launchPayload = readPayload(WORKFLOW_LAUNCH_KEY);

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

      const romPayload = readPayload(WORKFLOW_ROM_CACHE_KEY);
      const romResult = romPayload && romPayload.base64
        ? await api.emulator.loadRomBase64(romPayload.name || launchPayload.romKey, romPayload.base64)
        : await api.emulator.loadRomKey(launchPayload.romKey);
      if (!romResult || romResult.success === false) {
        const errorMessage = romResult?.error || 'Unable to load selected ROM';
        achievementsOverlay.innerHTML = `<div class="empty-state">${lib.escapeHtml(errorMessage)}</div>`;
        return false;
      }

      if (!isContinueLaunch && launchPayload.cores) {
        await api.cores.apply({
          cpuId: launchPayload.cores.cpuId,
          ppuId: launchPayload.cores.ppuId,
          apuId: launchPayload.cores.apuId
        });

        if (launchPayload.cores.shaderId) {
          await api.shader.setShader(launchPayload.cores.shaderId);
        }
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

    const unlockedAchievements = await getVisibleUnlockedAchievements(achievements);

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
      const save = await window.gameSave.load();
      const firstClear = !Boolean(save.LevelCleared);
      save.LevelCleared = true;
      await window.gameSave.save(save);

      try {
        await api.emulator.pause();
      } catch (error) {
        console.warn('[AchievementsRuntime] Failed to pause after unlock:', error);
      }

      const modalPromise = displayAchievementModal(title);
      const sfxPromise = lib.playRandomVictorySfx();
      await Promise.allSettled([modalPromise, sfxPromise]);

      await returnToContinue({
        achievementId,
        achievementTitle: title,
        showArrival: true,
        firstClear,
        checkpointAlreadyCaptured: Boolean(options.checkpointCaptured),
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
