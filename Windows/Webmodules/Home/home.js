// home.js - Main Menu logic (standalone webmodule)

(function() {
  'use strict';

  const api = window.webapi;

  // State
  let gameSave = null;
  let audioInitialized = false;

  // Initialize on page load
  window.addEventListener('DOMContentLoaded', init);

  async function init() {
    try {
      // Load game save data
      await loadGameSave();

      // Check URL parameters
      const params = new URLSearchParams(window.location.search);
      const skipHW = params.has('skipHW');

      // Show appropriate modal
      if (!skipHW) {
        showHealthWarningModal();
      } else {
        showMainMenu();
      }

      // Setup event listeners
      setupEventListeners();
    } catch (error) {
      console.error('[Home] Initialization error:', error);
      // Show main menu anyway
      showMainMenu();
      setupEventListeners();
    }
  }

  async function loadGameSave() {
    try {
      if (window.gameSave && typeof window.gameSave.load === 'function') {
        gameSave = await window.gameSave.load();
      } else {
        console.error('[Home] gameSave module not available');
        gameSave = null;
      }
    } catch (error) {
      console.error('[Home] Load save error:', error);
      gameSave = null;
    }
  }

  async function saveGameSave() {
    try {
      if (window.gameSave && typeof window.gameSave.save === 'function') {
        await window.gameSave.save(gameSave);
      } else {
        console.error('[Home] gameSave module not available for saving');
      }
    } catch (error) {
      console.error('[Home] Save error:', error);
    }
  }

  function setupEventListeners() {
    // Health Warning OK button
    const healthWarningOk = document.getElementById('healthWarningOk');
    if (healthWarningOk) {
      healthWarningOk.addEventListener('click', onHealthWarningOk);
    }

    // Main menu buttons
    const btnDeckBuilder = document.getElementById('btnDeckBuilder');
    if (btnDeckBuilder) {
      btnDeckBuilder.addEventListener('click', onDeckBuilderClick);
    }

    const btnEmulator = document.getElementById('btnEmulator');
    if (btnEmulator) {
      btnEmulator.addEventListener('click', onEmulatorClick);
    }

    const btnOptions = document.getElementById('btnOptions');
    if (btnOptions) {
      btnOptions.addEventListener('click', onOptionsClick);
    }

    const btnAbout = document.getElementById('btnAbout');
    if (btnAbout) {
      btnAbout.addEventListener('click', onAboutClick);
    }

    // About close button
    const aboutClose = document.getElementById('aboutClose');
    if (aboutClose) {
      aboutClose.addEventListener('click', onAboutClose);
    }

    // Modal backdrop clicks
    const aboutModal = document.getElementById('aboutModal');
    if (aboutModal) {
      aboutModal.addEventListener('click', (e) => {
        if (e.target === aboutModal) {
          onAboutClose();
        }
      });
    }
  }

  function showHealthWarningModal() {
    const modal = document.getElementById('healthWarningModal');
    if (modal) {
      modal.style.display = 'flex';
    }
  }

  function showMainMenu() {
    const hero = document.getElementById('homeHero');
    if (hero) {
      hero.style.display = 'flex';
    }

    // Initialize audio and start title music
    if (!audioInitialized) {
      initializeAudio();
      audioInitialized = true;
    }

    // Start pixel background
    if (window.homePixelBgEnsure) {
      window.homePixelBgEnsure();
      // Delayed retry in case DOM wasn't ready
      setTimeout(() => {
        if (window.homePixelBgEnsure) {
          window.homePixelBgEnsure();
        }
      }, 350);
    }
  }

  function initializeAudio() {
    try {
      // Request title music via audio engine
      if (api?.audio?.requestMusic) {
        api.audio.requestMusic('TitleScreen.mp3', true, 800).catch(err => {
          console.warn('[Home] Music request failed:', err);
        });
      }
    } catch (error) {
      console.error('[Home] Audio init error:', error);
    }
  }

  async function onHealthWarningOk() {
    // Play plate sound effect via audio engine
    try {
      if (api?.audio?.playSfx) {
        api.audio.playSfx('plates.m4a').catch(err => {
          console.warn('[Home] Plate SFX play failed:', err);
        });
      }
    } catch (error) {
      console.warn('[Home] Plate SFX error:', error);
    }

    // Hide modal
    const modal = document.getElementById('healthWarningModal');
    if (modal) {
      modal.style.display = 'none';
    }

    // Show main menu
    showMainMenu();
  }

  async function onDeckBuilderClick() {
    try {
      // Check if story has been seen
      if (!gameSave.SeenStory) {
        // Route to story first
        window.location.href = '../Story/index.html';
      } else {
        // Go directly to deck builder
        window.location.href = '../DeckBuilder/index.html';
      }
    } catch (error) {
      console.error('[Home] Deck builder nav error:', error);
      window.location.href = '../DeckBuilder/index.html';
    }
  }

  async function onEmulatorClick() {
    try {
      // Stop music via audio engine (await to ensure it completes first)
      if (api?.audio?.stopMusic) {
        await api.audio.stopMusic(0).catch(err => {
          console.warn('[Home] Music stop failed:', err);
        });
      }

      // Stop pixel background
      if (window.homePixelBg && window.homePixelBg.stop) {
        window.homePixelBg.stop();
      }

      // Call the API to switch to emulator mode
      if (!api?.navigation?.goToEmulator) {
        throw new Error('webapi helper not loaded');
      }

      // Note: The server may shut down during this call depending on configuration,
      // so a fetch error is actually expected and considered success
      try {
        const data = await api.navigation.goToEmulator();

        if (data?.success) {
          console.log('[Home] Switched to emulator mode:', data);
        } else if (data?.error) {
          //console.error('[Home] Failed to switch to emulator mode:', data.error);
          //alert('Failed to switch to emulator mode: ' + data.error);
        }
      } catch (fetchError) {
        // Network/fetch errors are expected when the server shuts down during mode switch
        // This is actually a successful scenario - the emulator mode was activated
        console.log('[Home] Emulator mode activated (server closed connection as expected)');
      }
    } catch (error) {
      console.error('[Home] Emulator switch error:', error);
      //alert('Could not connect to BrokenNes API. Make sure the application is running.');
    }
  }

  function onOptionsClick() {
    window.location.href = '../Options/index.html';
  }

  function onAboutClick() {
    const modal = document.getElementById('aboutModal');
    if (modal) {
      modal.style.display = 'flex';
    }
  }

  function onAboutClose() {
    const modal = document.getElementById('aboutModal');
    if (modal) {
      modal.style.display = 'none';
    }
  }

  // Expose API for debugging
  window.homeMenu = {
    getGameSave: () => gameSave,
    reload: init
  };
})();
