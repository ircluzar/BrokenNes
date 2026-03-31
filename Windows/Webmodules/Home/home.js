// home.js - Main Menu logic (standalone webmodule)

(function() {
  'use strict';

  const api = window.webapi;

  // State
  let gameSave = null;
  let audioInitialized = false;
  let menuFadeUnlockTimer = null;

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

    const btnRomManager = document.getElementById('btnRomManager');
    if (btnRomManager) {
      btnRomManager.addEventListener('click', onRomManagerClick);
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

    const storyCharacterCancel = document.getElementById('storyCharacterCancel');
    if (storyCharacterCancel) {
      storyCharacterCancel.addEventListener('click', hideStoryCharacterModal);
    }

    const storyCharacterModal = document.getElementById('storyCharacterModal');
    if (storyCharacterModal) {
      storyCharacterModal.addEventListener('click', (e) => {
        if (e.target === storyCharacterModal) {
          hideStoryCharacterModal();
        }
      });
    }

    populateStoryCharacterGrid();
  }

  function populateStoryCharacterGrid() {
    const grid = document.getElementById('storyCharacterGrid');
    if (!grid || !window.storyActors || typeof window.storyActors.renderCharacterCards !== 'function') {
      return;
    }

    window.storyActors.renderCharacterCards(grid, (actor) => {
      hideStoryCharacterModal();
      window.location.href = window.storyActors.buildStoryUrl(actor.id);
    });
  }

  function showStoryCharacterModal() {
    const modal = document.getElementById('storyCharacterModal');
    if (modal) {
      modal.style.display = 'flex';
    }
  }

  function hideStoryCharacterModal() {
    const modal = document.getElementById('storyCharacterModal');
    if (modal) {
      modal.style.display = 'none';
    }
  }

  function showHealthWarningModal() {
    const modal = document.getElementById('healthWarningModal');
    if (modal) {
      modal.style.display = 'flex';
    }
  }

  function restartMenuFadeSequence(hero) {
    const seqItems = Array.from(hero.querySelectorAll('.fade-seq'));
    if (seqItems.length === 0) {
      return;
    }

    // Prevent hover/focus interactions while elements are still fading in.
    hero.classList.add('menu-fade-lock');
    if (menuFadeUnlockTimer) {
      clearTimeout(menuFadeUnlockTimer);
      menuFadeUnlockTimer = null;
    }

    // Restart animations so each menu reveal is consistently ordered.
    seqItems.forEach((item) => {
      item.style.animation = 'none';
    });

    void hero.offsetHeight;

    const orderedItems = seqItems.slice().sort((a, b) => {
      const rectA = a.getBoundingClientRect();
      const rectB = b.getBoundingClientRect();
      const topDelta = rectA.top - rectB.top;
      return Math.abs(topDelta) > 1 ? topDelta : rectA.left - rectB.left;
    });

    const baseDelay = 0.15;
    const stepDelay = 0.12;
    const fadeDuration = 0.75;
    orderedItems.forEach((item, index) => {
      item.style.animation = '';
      item.style.animationDelay = `${baseDelay + (stepDelay * index)}s`;
    });

    const totalSequenceTimeMs = ((baseDelay + (stepDelay * (orderedItems.length - 1)) + fadeDuration) * 1000) + 80;
    menuFadeUnlockTimer = setTimeout(() => {
      hero.classList.remove('menu-fade-lock');
      menuFadeUnlockTimer = null;
    }, totalSequenceTimeMs);
  }

  function showMainMenu() {
    const hero = document.getElementById('homeHero');
    if (hero) {
      hero.style.display = 'flex';
      restartMenuFadeSequence(hero);
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
        showStoryCharacterModal();
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

  function onRomManagerClick() {
    window.location.href = '../RomManager/index.html';
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
