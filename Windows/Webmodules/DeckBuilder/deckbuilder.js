// deckbuilder.js - Deck Builder main logic (no emulator, standalone webmodule)

(function() {
  'use strict';

  // State
  let gameSave = null;

  // Initialize on page load
  window.addEventListener('DOMContentLoaded', init);

  async function init() {
    try {
      // Start pixel background animation
      if (window.homePixelBgEnsure) {
        window.homePixelBgEnsure();
      }

      // Load game save data
      await loadGameSave();

      // Update UI with save data
      updateUI();

      // Play background music randomly
      playRandomMusic();
    } catch (error) {
      console.error('[DeckBuilder] Initialization error:', error);
    }
  }

  async function loadGameSave() {
    try {
      if (window.gameSave && typeof window.gameSave.load === 'function') {
        gameSave = await window.gameSave.load();
      } else {
        console.error('[DeckBuilder] gameSave module not available');
        gameSave = null;
      }
    } catch (error) {
      console.error('[DeckBuilder] Load save error:', error);
      gameSave = null;
    }
  }

  function updateUI() {
    try {
      // Calculate stats
      const level = Math.max(1, gameSave.Level || 1);
      const stars = (gameSave.Achievements || []).length;
      const ownedCores = window.gameSave ? window.gameSave.countOwnedCores(gameSave) : 0;
      const totalCores = estimateTotalCores();

      // Update DOM elements
      const ownedCoresEl = document.getElementById('ownedCores');
      const achievementStarsEl = document.getElementById('achievementStars');
      const progressLevelEl = document.getElementById('progressLevel');

      if (ownedCoresEl) {
        ownedCoresEl.textContent = `${ownedCores}/${totalCores}`;
      }

      if (achievementStarsEl) {
        achievementStarsEl.textContent = stars.toString();
      }

      if (progressLevelEl) {
        progressLevelEl.textContent = `Level ${level}`;
      }
    } catch (error) {
      console.error('[DeckBuilder] Update UI error:', error);
    }
  }

  function estimateTotalCores() {
    // Rough estimate of total cores available in the game
    // This would ideally come from a manifest or be calculated from game data
    return 100; // Placeholder value
  }

  function playRandomMusic() {
    try {
      // Play TitleScreen music for DeckBuilder page
      const musicTrack = 'TitleScreen.mp3';

      if (window.webapi?.audio?.requestMusic) {
        window.webapi.audio.requestMusic(musicTrack, true, 800).catch(error => {
          console.warn('[DeckBuilder] Music request error (may be blocked by browser):', error);
        });
      }
    } catch (error) {
      console.error('[DeckBuilder] Music playback error:', error);
    }
  }

  // Expose API for debugging
  window.deckBuilder = {
    getGameSave: () => gameSave,
    reload: init
  };
})();
