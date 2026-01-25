// story.js - Story page logic (standalone webmodule)

(function() {
  'use strict';

  // State
  let gameSave = null;

  // Initialize on page load
  window.addEventListener('DOMContentLoaded', init);

  async function init() {
    try {
      // Start pixel background
      if (window.homePixelBgEnsure) {
        window.homePixelBgEnsure();
      }

      // Initialize audio
      initAudio();

      // Load game save
      await loadGameSave();

      // Setup event listeners
      setupEventListeners();

      // Update status
      updateStatus();

      // Perform fade-in transition
      performFadeTransition();
    } catch (error) {
      console.error('[Story] Initialization error:', error);
    }
  }

  function initAudio() {
    try {
      console.log('[Story] Initializing audio');
      // Play Story music via audio engine
      if (window.webapi?.audio?.requestMusic) {
        console.log('[Story] Requesting Story.mp3');
        window.webapi.audio.requestMusic('Story.mp3', true, 800).then(() => {
          console.log('[Story] Music request sent successfully');
        }).catch(err => {
          console.warn('[Story] Music request failed:', err);
        });
      } else {
        console.error('[Story] webapi.audio.requestMusic not available');
      }
    } catch (error) {
      console.warn('[Story] Audio init error:', error);
    }
  }

  async function loadGameSave() {
    try {
      if (window.gameSave && typeof window.gameSave.load === 'function') {
        gameSave = await window.gameSave.load();
      } else {
        console.error('[Story] gameSave module not available');
        gameSave = null;
      }
    } catch (error) {
      console.error('[Story] Load save error:', error);
      gameSave = null;
    }
  }

  async function saveGameSave() {
    try {
      if (window.gameSave && typeof window.gameSave.save === 'function') {
        await window.gameSave.save(gameSave);
      } else {
        console.error('[Story] gameSave module not available for saving');
      }
    } catch (error) {
      console.error('[Story] Save error:', error);
    }
  }

  function setupEventListeners() {
    const btnMarkViewed = document.getElementById('btnMarkViewed');
    if (btnMarkViewed) {
      btnMarkViewed.addEventListener('click', onMarkViewed);
    }

    const btnContinue = document.getElementById('btnContinue');
    if (btnContinue) {
      btnContinue.addEventListener('click', onContinue);
    }
  }

  function updateStatus() {
    const statusText = document.getElementById('statusText');
    if (statusText) {
      if (gameSave && gameSave.SeenStory) {
        statusText.textContent = 'You have already viewed the story introduction.';
      } else {
        statusText.textContent = 'This is your first time viewing the story.';
      }
    }
  }

  function performFadeTransition() {
    // Fade in from black
    const overlay = document.getElementById('storyFadeOverlay');
    if (overlay) {
      // Start with opacity 1
      overlay.style.opacity = '1';
      // Fade out after a short delay
      setTimeout(() => {
        overlay.style.opacity = '0';
      }, 100);
    }
  }

  async function onMarkViewed() {
    try {
      gameSave.SeenStory = true;
      await saveGameSave();
      updateStatus();
      
      const statusText = document.getElementById('statusText');
      if (statusText) {
        statusText.textContent = 'Story marked as viewed! You can now access the Deck Builder directly.';
        statusText.style.color = '#ff5a26';
      }
    } catch (error) {
      console.error('[Story] Mark viewed error:', error);
    }
  }

  async function onContinue() {
    try {
      // Ensure story is marked as seen
      if (!gameSave.SeenStory) {
        gameSave.SeenStory = true;
        await saveGameSave();
      }

      // Fade out before navigating
      const overlay = document.getElementById('storyFadeOverlay');
      if (overlay) {
        overlay.style.pointerEvents = 'all';
        overlay.style.opacity = '1';
      }

      // Wait for fade
      await new Promise(resolve => setTimeout(resolve, 600));

      // Navigate to deck builder
      window.location.href = '../DeckBuilder/index.html';
    } catch (error) {
      console.error('[Story] Continue error:', error);
      window.location.href = '../DeckBuilder/index.html';
    }
  }

  // Expose API for debugging
  window.storyPage = {
    getGameSave: () => gameSave,
    reload: init
  };
})();
