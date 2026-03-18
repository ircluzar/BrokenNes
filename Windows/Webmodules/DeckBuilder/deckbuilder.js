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
      await updateUI();

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

  async function updateUI() {
    try {
      // Calculate stats
      const level = Math.max(1, gameSave.Level || 1);
      const stars = (gameSave.Achievements || []).length;
      const ownedCores = window.gameSave ? window.gameSave.countOwnedCores(gameSave) : 0;
      const totalCores = await getTotalCores(ownedCores);

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

  async function getTotalCores(fallbackValue) {
    try {
      const result = await window.webapi?.cores?.list?.();
      if (result && typeof result === 'object') {
        const total = ['cpu', 'ppu', 'apu', 'clock', 'shader']
          .map(key => Array.isArray(result[key]) ? result[key].length : 0)
          .reduce((sum, count) => sum + count, 0);

        if (Number.isFinite(total) && total > 0) {
          return total;
        }
      }
    } catch (error) {
      console.warn('[DeckBuilder] Total core fetch failed:', error);
    }

    return Number.isFinite(fallbackValue) ? fallbackValue : 0;
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
