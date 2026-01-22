// home.js - Main Menu logic (standalone webmodule)

(function() {
  'use strict';

  // Storage keys
  const STORAGE_KEY = 'brokenNesGameSave';

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
      if (!gameSave.UnderConstructionAcknowledged) {
        showUnderConstructionModal();
      } else if (!skipHW) {
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
            CPU: [],
            PPU: [],
            APU: [],
            Shader: []
          },
          UnderConstructionAcknowledged: false,
          SeenStory: false
        };
      }
    } catch (error) {
      console.error('[Home] Load save error:', error);
      gameSave = {
        Level: 1,
        Achievements: [],
        OwnedCores: {
          CPU: [],
          PPU: [],
          APU: [],
          Shader: []
        },
        UnderConstructionAcknowledged: false,
        SeenStory: false
      };
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
      console.error('[Home] Save error:', error);
    }
  }

  function setupEventListeners() {
    // Under Construction OK button
    const underConstructionOk = document.getElementById('underConstructionOk');
    if (underConstructionOk) {
      underConstructionOk.addEventListener('click', onUnderConstructionOk);
    }

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

  function showUnderConstructionModal() {
    const modal = document.getElementById('underConstructionModal');
    if (modal) {
      modal.style.display = 'flex';
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
      // Initialize audio context
      if (window.music) {
        // Play title music
        window.music.play('assets/music/TitleScreen.mp3', { 
          loop: true, 
          fadeInMs: 800 
        }).catch(err => {
          console.warn('[Home] Music play failed:', err);
        });

        window.music.setLocalVolume(0.5);
      }
    } catch (error) {
      console.error('[Home] Audio init error:', error);
    }
  }

  async function onUnderConstructionOk() {
    // Hide modal
    const modal = document.getElementById('underConstructionModal');
    if (modal) {
      modal.style.display = 'none';
    }

    // Save acknowledgment
    gameSave.UnderConstructionAcknowledged = true;
    await saveGameSave();

    // Show health warning next
    showHealthWarningModal();
  }

  async function onHealthWarningOk() {
    // Play plate sound effect
    try {
      const plateAudio = document.getElementById('plateWav');
      if (plateAudio) {
        plateAudio.play().catch(err => {
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

  function onEmulatorClick() {
    // For web module, we can't launch the actual emulator
    // Show a placeholder message or link back to main app
    alert('The BrokenNes Emulator requires the full application. This is a standalone web module demo.');
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
