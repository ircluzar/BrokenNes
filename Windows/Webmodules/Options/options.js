// options.js - Options menu logic (standalone webmodule)

(function() {
  'use strict';

  const OPTIONS_MUSIC_FADE_MS = 800;
  const OPTIONS_TITLE_TRACK = 'TitleScreen.mp3';
  const OPTIONS_CREDITS_TRACK = 'BrokeEverythingAgain.mp3';

  // State
  let gameSave = null;
  let volumes = {
    master: 1.0,
    music: 0.42,
    sfx: 0.8
  };

  // Initialize on page load
  window.addEventListener('DOMContentLoaded', init);

  async function init() {
    try {
      // Load game save and volumes
      await loadGameSave();
      await loadVolumes();

      // Start pixel background
      if (window.homePixelBgEnsure) {
        window.homePixelBgEnsure();
      }

      // Initialize audio (play title screen music)
      initAudio();

      // Setup event listeners
      setupEventListeners();

      // Update UI with loaded volumes
      updateVolumeUI();
    } catch (error) {
      console.error('[Options] Initialization error:', error);
    }
  }

  function initAudio() {
    try {
      // Request title screen music via audio engine
      if (window.webapi?.audio?.requestMusic) {
        window.webapi.audio.requestMusic(OPTIONS_TITLE_TRACK, true, OPTIONS_MUSIC_FADE_MS).catch(err => {
          console.warn('[Options] Music request failed:', err);
        });
      }
    } catch (error) {
      console.warn('[Options] Audio init error:', error);
    }
  }

  function requestOptionsMusic(filename) {
    if (!filename || !window.webapi?.audio?.requestMusic) {
      return Promise.resolve(false);
    }

    return window.webapi.audio.requestMusic(filename, true, OPTIONS_MUSIC_FADE_MS).then(() => true).catch(error => {
      console.warn(`[Options] Music request failed for '${filename}':`, error);
      return false;
    });
  }

  async function loadGameSave() {
    try {
      if (window.gameSave && typeof window.gameSave.load === 'function') {
        gameSave = await window.gameSave.load();
      } else {
        console.error('[Options] gameSave module not available');
        gameSave = null;
      }
    } catch (error) {
      console.error('[Options] Load save error:', error);
      gameSave = null;
    }
  }

  async function loadVolumes() {
    try {
      if (window.gameSave && typeof window.gameSave.loadVolumes === 'function') {
        volumes = await window.gameSave.loadVolumes();
      } else {
        console.error('[Options] gameSave module not available for volumes');
      }
    } catch (error) {
      console.error('[Options] Load volumes error:', error);
    }
  }

  async function saveGameSave() {
    try {
      if (window.gameSave && typeof window.gameSave.save === 'function') {
        await window.gameSave.save(gameSave);
      } else {
        console.error('[Options] gameSave module not available for saving');
      }
    } catch (error) {
      console.error('[Options] Save error:', error);
    }
  }

  async function saveVolumes() {
    try {
      if (window.gameSave && typeof window.gameSave.saveVolumes === 'function') {
        await window.gameSave.saveVolumes(volumes);
      } else {
        console.error('[Options] gameSave module not available for saving volumes');
      }
    } catch (error) {
      console.error('[Options] Save volumes error:', error);
    }
  }

  function setupEventListeners() {
    // Volume sliders
    const masterVolume = document.getElementById('masterVolume');
    const musicVolume = document.getElementById('musicVolume');
    const sfxVolume = document.getElementById('sfxVolume');

    if (masterVolume) {
      masterVolume.addEventListener('input', (e) => onVolumeChange('master', e.target.value));
    }
    if (musicVolume) {
      musicVolume.addEventListener('input', (e) => onVolumeChange('music', e.target.value));
    }
    if (sfxVolume) {
      sfxVolume.addEventListener('input', (e) => onVolumeChange('sfx', e.target.value));
    }

    // Action buttons
    const btnClearSave = document.getElementById('btnClearSave');
    if (btnClearSave) {
      btnClearSave.addEventListener('click', onClearSave);
    }

    const btnUnlockAll = document.getElementById('btnUnlockAll');
    if (btnUnlockAll) {
      btnUnlockAll.addEventListener('click', onUnlockAll);
    }

    const btnOpenCredits = document.getElementById('btnOpenCredits');
    if (btnOpenCredits) {
      btnOpenCredits.addEventListener('click', () => {
        void openCreditsModal();
      });
    }

    // Feature unlock buttons
    const btnUnlockSavestates = document.getElementById('btnUnlockSavestates');
    if (btnUnlockSavestates) {
      btnUnlockSavestates.addEventListener('click', () => onUnlockFeature('Savestates'));
    }

    const btnUnlockRtc = document.getElementById('btnUnlockRtc');
    if (btnUnlockRtc) {
      btnUnlockRtc.addEventListener('click', () => onUnlockFeature('RTC'));
    }

    const btnUnlockGh = document.getElementById('btnUnlockGh');
    if (btnUnlockGh) {
      btnUnlockGh.addEventListener('click', () => onUnlockFeature('GH'));
    }

    const btnUnlockImagine = document.getElementById('btnUnlockImagine');
    if (btnUnlockImagine) {
      btnUnlockImagine.addEventListener('click', () => onUnlockFeature('Imagine'));
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

    // Modal buttons
    const optModalOk = document.getElementById('optModalOk');
    if (optModalOk) {
      optModalOk.addEventListener('click', closeModal);
    }

    const optModal = document.getElementById('optModal');
    if (optModal) {
      optModal.addEventListener('click', (e) => {
        if (e.target === optModal) {
          closeModal();
        }
      });
      optModal.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
          closeModal();
        }
      });
    }

    const creditsCloseBtn = document.getElementById('creditsCloseBtn');
    if (creditsCloseBtn) {
      creditsCloseBtn.addEventListener('click', () => {
        void closeCreditsModal();
      });
    }

    const creditsModal = document.getElementById('creditsModal');
    if (creditsModal) {
      creditsModal.addEventListener('click', (e) => {
        if (e.target === creditsModal) {
          void closeCreditsModal();
        }
      });
      creditsModal.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
          void closeCreditsModal();
        }
      });
    }
  }

  function isCreditsModalOpen() {
    return document.getElementById('creditsModal')?.style.display === 'flex';
  }

  function updateVolumeUI() {
    const masterVolume = document.getElementById('masterVolume');
    const musicVolume = document.getElementById('musicVolume');
    const sfxVolume = document.getElementById('sfxVolume');
    const masterValue = document.getElementById('masterValue');
    const musicValue = document.getElementById('musicValue');
    const sfxValue = document.getElementById('sfxValue');

    if (masterVolume && masterValue) {
      const val = Math.round(volumes.master * 100);
      masterVolume.value = val;
      masterValue.textContent = val;
    }
    if (musicVolume && musicValue) {
      const val = Math.round(volumes.music * 100);
      musicVolume.value = val;
      musicValue.textContent = val;
    }
    if (sfxVolume && sfxValue) {
      const val = Math.round(volumes.sfx * 100);
      sfxVolume.value = val;
      sfxValue.textContent = val;
    }
  }

  async function onVolumeChange(kind, value) {
    const pct = parseInt(value);
    const normalized = Math.max(0, Math.min(100, pct)) / 100.0;
    
    volumes[kind] = normalized;
    
    // Update display
    const valueEl = document.getElementById(`${kind}Value`);
    if (valueEl) {
      valueEl.textContent = pct;
    }

    // Apply volume via audio engine API
    if (window.webapi?.audio?.setVolume) {
      try {
        await window.webapi.audio.setVolume(volumes.music, volumes.sfx);
      } catch (error) {
        console.warn('[Options] Audio volume set error:', error);
      }
    }

    // Save volumes
    await saveVolumes();
  }

  async function onClearSave() {
    // Reset save to defaults using shared module
    if (window.gameSave && typeof window.gameSave.reset === 'function') {
      try {
        gameSave = await window.gameSave.reset();
        localStorage.removeItem('bn_emulator_warning_accepted');
        showModal('Save Edit', 'DeckBuilder game save reset to defaults');
      } catch (error) {
        console.error('[Options] Clear save failed:', error);
        showModal('Error', error?.message || 'Unable to reset canonical save');
      }
    } else {
      showModal('Error', 'Unable to reset save - gameSave module not available');
    }
  }

  async function onUnlockAll() {
    try {
      if (window.webapi?.progression?.unlockEverything) {
        await window.webapi.progression.unlockEverything();
        await loadGameSave();
        showModal('Save Edit', 'Everything has been unlocked using the canonical progression roster.');
        return;
      }
    } catch (error) {
      console.warn('[Options] Native unlock-everything API failed, falling back to local unlock:', error);
    }

    // Fallback path for environments where the native endpoint is unavailable.
    if (window.gameSave && typeof window.gameSave.unlockAllCores === 'function') {
      gameSave = window.gameSave.unlockAllCores(gameSave);

      try {
        const roster = await window.webapi?.progression?.getRoster?.();
        if (Array.isArray(roster?.backgrounds)) {
          gameSave.UnlockedBackgrounds = roster.backgrounds
            .map(entry => entry?.id)
            .filter(id => typeof id === 'string' && id.trim());
        }
        if (Array.isArray(roster?.nullProviders)) {
          gameSave.UnlockedNullProviders = roster.nullProviders
            .map(entry => entry?.id)
            .filter(id => typeof id === 'string' && id.trim());
        }
      } catch (error) {
        console.warn('[Options] Failed to load progression roster for unlock-all:', error);
      }

      await saveGameSave();
      showModal('Save Edit', 'All cores, milestone modules, backgrounds, null providers, and advanced features unlocked in your DeckBuilder save.');
      return;
    }

    showModal('Error', 'Unable to unlock cores - gameSave module not available');
  }

  async function onUnlockFeature(feature) {
    if (window.gameSave && typeof window.gameSave.unlockFeature === 'function') {
      gameSave = window.gameSave.unlockFeature(gameSave, feature);
      await saveGameSave();
      showModal('Save Edit', `${feature} unlocked.`);
      return;
    }

    showModal('Error', 'Unable to unlock feature - gameSave module not available');
  }

  async function openControllerConfigForPlayer(playerNumber) {
    if (!window.webapi?.ui?.openControllerConfig) {
      console.warn('[Options] openControllerConfig UI endpoint is unavailable');
      return;
    }

    const result = await window.webapi.ui.openControllerConfig(playerNumber);
    if (!result || result.success === false) {
      console.warn(`[Options] Failed to open controller config for player ${playerNumber}:`, result?.error || 'unknown error');
    }
  }

  function showModal(title, message) {
    const modal = document.getElementById('optModal');
    const titleEl = document.getElementById('optModalTitle');
    const msgEl = document.getElementById('optModalMsg');

    if (titleEl) {
      titleEl.textContent = title;
    }
    if (msgEl) {
      msgEl.textContent = message;
    }
    if (modal) {
      modal.style.display = 'flex';
      // Focus OK button
      setTimeout(() => {
        const okBtn = document.getElementById('optModalOk');
        if (okBtn) {
          okBtn.focus();
        }
      }, 100);
    }
  }

  function closeModal() {
    const modal = document.getElementById('optModal');
    if (modal) {
      modal.style.display = 'none';
    }
  }

  async function openCreditsModal() {
    const modal = document.getElementById('creditsModal');
    if (!modal || isCreditsModalOpen()) {
      return;
    }

    modal.style.display = 'flex';
    const closeBtn = document.getElementById('creditsCloseBtn');
    setTimeout(() => {
      if (closeBtn) {
        closeBtn.focus();
      }
    }, 100);

    await requestOptionsMusic(OPTIONS_CREDITS_TRACK);
  }

  async function closeCreditsModal() {
    const modal = document.getElementById('creditsModal');
    if (!modal) {
      return;
    }

    modal.style.display = 'none';
    await requestOptionsMusic(OPTIONS_TITLE_TRACK);
  }

  // Expose API for debugging
  window.optionsMenu = {
    getGameSave: () => gameSave,
    getVolumes: () => volumes,
    reload: init
  };
})();
