// options.js - Options menu logic (standalone webmodule)

(function() {
  'use strict';

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
        window.webapi.audio.requestMusic('TitleScreen.mp3', true, 800).catch(err => {
          console.warn('[Options] Music request failed:', err);
        });
      }
    } catch (error) {
      console.warn('[Options] Audio init error:', error);
    }
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
    const btnRestoreCores = document.getElementById('btnRestoreCores');
    if (btnRestoreCores) {
      btnRestoreCores.addEventListener('click', onRestoreCores);
    }

    const btnClearSave = document.getElementById('btnClearSave');
    if (btnClearSave) {
      btnClearSave.addEventListener('click', onClearSave);
    }

    const btnUnlockAll = document.getElementById('btnUnlockAll');
    if (btnUnlockAll) {
      btnUnlockAll.addEventListener('click', onUnlockAll);
    }

    const btnToggleFeatures = document.getElementById('btnToggleFeatures');
    if (btnToggleFeatures) {
      btnToggleFeatures.addEventListener('click', onToggleFeatures);
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

    const btnUnlockDebug = document.getElementById('btnUnlockDebug');
    if (btnUnlockDebug) {
      btnUnlockDebug.addEventListener('click', () => onUnlockFeature('Debug'));
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
    if (window.webapi?.request) {
      try {
        await window.webapi.request('/api/audio/volume', {
          method: 'POST',
          json: {
            musicVolume: volumes.music,
            sfxVolume: volumes.sfx
          }
        });
      } catch (error) {
        console.warn('[Options] Audio volume set error:', error);
      }
    }

    // Save volumes
    await saveVolumes();
  }

  async function onRestoreCores() {
    // In web module, we simulate this by showing message
    // In full app, this would use IndexedDB
    showModal('Core Preferences', 'Core preferences have been reset to default (FMC).');
  }

  async function onClearSave() {
    // Reset save to defaults using shared module
    if (window.gameSave && typeof window.gameSave.reset === 'function') {
      gameSave = await window.gameSave.reset();
      showModal('Save Edit', 'DeckBuilder game save reset to defaults');
    } else {
      showModal('Error', 'Unable to reset save - gameSave module not available');
    }
  }

  async function onUnlockAll() {
    // Use shared gameSave module to unlock everything
    if (window.gameSave && typeof window.gameSave.unlockAllCores === 'function') {
      gameSave = window.gameSave.unlockAllCores(gameSave);
      await saveGameSave();
      showModal('Save Edit', 'All cores and advanced features unlocked in your DeckBuilder save.');
    } else {
      showModal('Error', 'Unable to unlock cores - gameSave module not available');
    }
  }

  function onToggleFeatures() {
    const section = document.getElementById('featureUnlocks');
    if (section) {
      section.style.display = section.style.display === 'none' ? 'block' : 'none';
    }
  }

  async function onUnlockFeature(feature) {
    if (!gameSave.UnlockedFeatures) {
      gameSave.UnlockedFeatures = {};
    }
    gameSave.UnlockedFeatures[feature] = true;
    await saveGameSave();
    showModal('Save Edit', `${feature} unlocked.`);
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

  // Expose API for debugging
  window.optionsMenu = {
    getGameSave: () => gameSave,
    getVolumes: () => volumes,
    reload: init
  };
})();
