// options.js - Options menu logic (standalone webmodule)

(function() {
  'use strict';

  // Storage keys
  const STORAGE_KEY = 'brokenNesGameSave';
  const VOLUME_KEY = 'brokenNesAudioVolumes';

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

      // Setup event listeners
      setupEventListeners();

      // Update UI with loaded volumes
      updateVolumeUI();
    } catch (error) {
      console.error('[Options] Initialization error:', error);
    }
  }

  async function loadGameSave() {
    try {
      if (window.storage && typeof window.storage.load === 'function') {
        gameSave = await window.storage.load(STORAGE_KEY);
      } else {
        const data = localStorage.getItem(STORAGE_KEY);
        if (data) {
          gameSave = JSON.parse(data);
        }
      }

      // Initialize default save if none exists
      if (!gameSave) {
        gameSave = {
          Level: 1,
          Achievements: [],
          OwnedCores: {
            CPU: ['FMC'],
            PPU: ['FMC'],
            APU: ['FMC'],
            Clock: ['FMC'],
            Shader: ['PX']
          },
          UnlockedFeatures: {
            Savestates: false,
            RTC: false,
            GH: false,
            Imagine: false,
            Debug: false
          }
        };
      }
    } catch (error) {
      console.error('[Options] Load save error:', error);
      gameSave = {
        Level: 1,
        Achievements: [],
        OwnedCores: { CPU: ['FMC'], PPU: ['FMC'], APU: ['FMC'], Clock: ['FMC'], Shader: ['PX'] },
        UnlockedFeatures: { Savestates: false, RTC: false, GH: false, Imagine: false, Debug: false }
      };
    }
  }

  async function loadVolumes() {
    try {
      let data;
      if (window.storage && typeof window.storage.load === 'function') {
        data = await window.storage.load(VOLUME_KEY);
      } else {
        const stored = localStorage.getItem(VOLUME_KEY);
        if (stored) {
          data = JSON.parse(stored);
        }
      }

      if (data) {
        volumes = { ...volumes, ...data };
      }
    } catch (error) {
      console.error('[Options] Load volumes error:', error);
    }
  }

  async function saveGameSave() {
    try {
      if (window.storage && typeof window.storage.save === 'function') {
        await window.storage.save(STORAGE_KEY, gameSave);
      } else {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(gameSave));
      }
    } catch (error) {
      console.error('[Options] Save error:', error);
    }
  }

  async function saveVolumes() {
    try {
      if (window.storage && typeof window.storage.save === 'function') {
        await window.storage.save(VOLUME_KEY, volumes);
      } else {
        localStorage.setItem(VOLUME_KEY, JSON.stringify(volumes));
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

    // Apply volume if music library is available
    if (kind === 'music' && window.music) {
      try {
        window.music.setLocalVolume(normalized);
      } catch (error) {
        console.warn('[Options] Music volume set error:', error);
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
    // Reset save to defaults
    gameSave = {
      Level: 1,
      Achievements: [],
      OwnedCores: {
        CPU: ['FMC'],
        PPU: ['FMC'],
        APU: ['FMC'],
        Clock: ['FMC'],
        Shader: ['PX']
      },
      UnlockedFeatures: {
        Savestates: false,
        RTC: false,
        GH: false,
        Imagine: false,
        Debug: false
      },
      UnderConstructionAcknowledged: gameSave.UnderConstructionAcknowledged,
      SeenStory: gameSave.SeenStory
    };
    
    await saveGameSave();
    showModal('Save Edit', 'DeckBuilder game save reset to defaults');
  }

  async function onUnlockAll() {
    // Unlock all cores
    const allCPU = ['FMC', 'EXE', 'LAT', 'QUK', 'HAW'];
    const allPPU = ['FMC', 'LOW', 'MED', 'HI', 'OPT'];
    const allAPU = ['FMC', 'LOW', 'MED', 'HI'];
    const allClock = ['FMC', 'STD', 'OPT'];
    const allShader = ['PX', '16B', 'BLD', 'BUMP', 'CCC', 'CNMA', 'CRY', 'CRZ', 'DOT', 'EXE', 
                       'HUE', 'LAT', 'LCD', 'LSD', 'MSH', 'MUSK', 'RF', 'RGBX', 'SPK', 'TRI', 
                       'TTF', 'TV', 'VHS', 'WARM', 'WTR'];

    gameSave.OwnedCores = {
      CPU: allCPU,
      PPU: allPPU,
      APU: allAPU,
      Clock: allClock,
      Shader: allShader
    };

    if (!gameSave.UnlockedFeatures) {
      gameSave.UnlockedFeatures = {};
    }
    gameSave.UnlockedFeatures.Savestates = true;
    gameSave.UnlockedFeatures.RTC = true;
    gameSave.UnlockedFeatures.GH = true;
    gameSave.UnlockedFeatures.Imagine = true;
    gameSave.UnlockedFeatures.Debug = true;

    await saveGameSave();
    showModal('Save Edit', 'All cores and advanced features unlocked in your DeckBuilder save.');
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
