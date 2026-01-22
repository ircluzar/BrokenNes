// deckbuilder.js - Deck Builder main logic (no emulator, standalone webmodule)

(function() {
  'use strict';

  // Storage keys
  const STORAGE_KEY = 'brokenNesGameSave';

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

      // Initialize audio system
      initAudio();

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

  function initAudio() {
    try {
      // Initialize the music library if available
      if (window.music && typeof window.music.setLocalVolume === 'function') {
        window.music.setLocalVolume(0.5); // Set default volume
      }
    } catch (error) {
      console.error('[DeckBuilder] Audio init error:', error);
    }
  }

  async function loadGameSave() {
    try {
      // Use storage.js if available, otherwise fallback to localStorage
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
          }
        };
      }
    } catch (error) {
      console.error('[DeckBuilder] Load save error:', error);
      // Use default save
      gameSave = {
        Level: 1,
        Achievements: [],
        OwnedCores: {
          CPU: [],
          PPU: [],
          APU: [],
          Shader: []
        }
      };
    }
  }

  function updateUI() {
    try {
      // Calculate stats
      const level = Math.max(1, gameSave.Level || 1);
      const stars = (gameSave.Achievements || []).length;
      const ownedCores = countOwnedCores(gameSave);
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

  function countOwnedCores(save) {
    let count = 0;
    try {
      if (save && save.OwnedCores) {
        const cores = save.OwnedCores;
        if (cores.CPU) count += cores.CPU.length;
        if (cores.PPU) count += cores.PPU.length;
        if (cores.APU) count += cores.APU.length;
        if (cores.Shader) count += cores.Shader.length;
      }
    } catch (error) {
      console.error('[DeckBuilder] Count cores error:', error);
    }
    return count;
  }

  function estimateTotalCores() {
    // Rough estimate of total cores available in the game
    // This would ideally come from a manifest or be calculated from game data
    return 100; // Placeholder value
  }

  function playRandomMusic() {
    try {
      // DeckBuilder music tracks (1-4)
      const tracks = [
        'assets/music/DeckBuilder1.mp3',
        'assets/music/DeckBuilder2.mp3',
        'assets/music/DeckBuilder3.mp3',
        'assets/music/DeckBuilder4.mp3'
      ];

      const randomTrack = tracks[Math.floor(Math.random() * tracks.length)];

      if (window.music && typeof window.music.play === 'function') {
        window.music.play(randomTrack, {
          loop: true,
          fadeInMs: 800
        }).catch(error => {
          console.warn('[DeckBuilder] Music play error (may be blocked by browser):', error);
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
