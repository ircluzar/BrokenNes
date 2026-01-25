// AudioTest - Test interface for audio engine
(function() {
  'use strict';

  // DOM Elements
  const nowPlaying = document.getElementById('nowPlaying');
  const playbackStatus = document.getElementById('playbackStatus');
  const musicVolumeSlider = document.getElementById('musicVolumeSlider');
  const musicVolumeValue = document.getElementById('musicVolumeValue');
  const sfxVolumeSlider = document.getElementById('sfxVolumeSlider');
  const sfxVolumeValue = document.getElementById('sfxVolumeValue');
  const fadeSlider = document.getElementById('fadeSlider');
  const fadeValue = document.getElementById('fadeValue');
  const loopCheckbox = document.getElementById('loopCheckbox');
  const musicList = document.getElementById('musicList');
  const sfxList = document.getElementById('sfxList');
  const stopMusicBtn = document.getElementById('stopMusicBtn');

  // State
  let musicFiles = [];
  let sfxFiles = [];
  let statusUpdateInterval = null;

  // Initialize
  async function init() {
    setupVolumeControls();
    setupFadeControl();
    await loadAudioFiles();
    startStatusUpdates();
  }

  // Setup volume controls
  function setupVolumeControls() {
    musicVolumeSlider.addEventListener('input', () => {
      const percent = parseInt(musicVolumeSlider.value);
      musicVolumeValue.textContent = percent + '%';
      updateVolume();
    });

    sfxVolumeSlider.addEventListener('input', () => {
      const percent = parseInt(sfxVolumeSlider.value);
      sfxVolumeValue.textContent = percent + '%';
      updateVolume();
    });
  }

  // Setup fade duration control
  function setupFadeControl() {
    fadeSlider.addEventListener('input', () => {
      fadeValue.textContent = fadeSlider.value + 'ms';
    });
  }

  // Update volume via API
  async function updateVolume() {
    try {
      const musicVolume = parseInt(musicVolumeSlider.value) / 100;
      const sfxVolume = parseInt(sfxVolumeSlider.value) / 100;
      
      const result = await window.webapi.request('/api/audio/volume', {
        method: 'POST',
        json: {
          musicVolume,
          sfxVolume
        }
      });

      if (!result.success) {
        console.error('Failed to set volume:', result.error);
      }
    } catch (error) {
      console.error('Error setting volume:', error);
    }
  }

  // Load available audio files
  async function loadAudioFiles() {
    try {
      // Load music files
      const musicResult = await window.webapi.request('/api/audio/music/list');
      if (musicResult.success) {
        musicFiles = musicResult.files || [];
        renderMusicList();
      } else {
        musicList.innerHTML = '<div class="error">Failed to load music files</div>';
      }

      // Load SFX files
      const sfxResult = await window.webapi.request('/api/audio/sfx/list');
      if (sfxResult.success) {
        sfxFiles = sfxResult.files || [];
        renderSfxList();
      } else {
        sfxList.innerHTML = '<div class="error">Failed to load SFX files</div>';
      }
    } catch (error) {
      console.error('Error loading audio files:', error);
      musicList.innerHTML = '<div class="error">Error: ' + error.message + '</div>';
      sfxList.innerHTML = '<div class="error">Error: ' + error.message + '</div>';
    }
  }

  // Render music file list
  function renderMusicList() {
    if (musicFiles.length === 0) {
      musicList.innerHTML = '<div class="empty">No music files found</div>';
      return;
    }

    musicList.innerHTML = musicFiles.map(file => `
      <div class="file-item">
        <span class="file-name">${escapeHtml(file)}</span>
        <div class="file-actions">
          <button class="btn btn-small btn-primary" onclick="window.audioTest.playMusic('${escapeHtml(file)}')">
            ▶️ Play
          </button>
          <button class="btn btn-small btn-secondary" onclick="window.audioTest.requestMusic('${escapeHtml(file)}')">
            🔀 Request
          </button>
        </div>
      </div>
    `).join('');
  }

  // Render SFX file list
  function renderSfxList() {
    if (sfxFiles.length === 0) {
      sfxList.innerHTML = '<div class="empty">No SFX files found</div>';
      return;
    }

    sfxList.innerHTML = sfxFiles.map(file => `
      <div class="file-item">
        <span class="file-name">${escapeHtml(file)}</span>
        <div class="file-actions">
          <button class="btn btn-small btn-primary" onclick="window.audioTest.playSfx('${escapeHtml(file)}')">
            🔊 Play
          </button>
        </div>
      </div>
    `).join('');
  }

  // Play music directly
  async function playMusic(filename) {
    try {
      playbackStatus.textContent = 'Loading...';
      playbackStatus.className = 'value status-loading';
      
      const loop = loopCheckbox.checked;
      const result = await window.webapi.request('/api/audio/music/play', {
        method: 'POST',
        json: {
          filename,
          loop
        }
      });

      if (result.success) {
        nowPlaying.textContent = filename;
        playbackStatus.textContent = 'Playing';
        playbackStatus.className = 'value status-playing';
      } else {
        playbackStatus.textContent = 'Error';
        playbackStatus.className = 'value status-error';
        alert('Failed to play music: ' + result.error);
      }
    } catch (error) {
      console.error('Error playing music:', error);
      playbackStatus.textContent = 'Error';
      playbackStatus.className = 'value status-error';
      alert('Error: ' + error.message);
    }
  }

  // Request music with crossfade
  async function requestMusic(filename) {
    try {
      playbackStatus.textContent = 'Crossfading...';
      playbackStatus.className = 'value status-loading';
      
      const loop = loopCheckbox.checked;
      const fadeDurationMs = parseInt(fadeSlider.value);
      
      const result = await window.webapi.request('/api/audio/music/request', {
        method: 'POST',
        json: {
          filename,
          loop,
          fadeDurationMs
        }
      });

      if (result.success) {
        // Status will be updated by the polling
        setTimeout(updateStatus, fadeDurationMs + 100);
      } else {
        playbackStatus.textContent = 'Error';
        playbackStatus.className = 'value status-error';
        alert('Failed to request music: ' + result.error);
      }
    } catch (error) {
      console.error('Error requesting music:', error);
      playbackStatus.textContent = 'Error';
      playbackStatus.className = 'value status-error';
      alert('Error: ' + error.message);
    }
  }

  // Stop music
  async function stopMusic() {
    try {
      playbackStatus.textContent = 'Stopping...';
      playbackStatus.className = 'value status-loading';
      
      const fadeDurationMs = parseInt(fadeSlider.value);
      const result = await window.webapi.request('/api/audio/music/stop', {
        method: 'POST',
        json: {
          fadeDurationMs
        }
      });

      if (result.success) {
        setTimeout(() => {
          nowPlaying.textContent = 'None';
          playbackStatus.textContent = 'Idle';
          playbackStatus.className = 'value';
        }, fadeDurationMs + 100);
      } else {
        playbackStatus.textContent = 'Error';
        playbackStatus.className = 'value status-error';
        alert('Failed to stop music: ' + result.error);
      }
    } catch (error) {
      console.error('Error stopping music:', error);
      playbackStatus.textContent = 'Error';
      playbackStatus.className = 'value status-error';
      alert('Error: ' + error.message);
    }
  }

  // Play SFX
  async function playSfx(filename) {
    try {
      const result = await window.webapi.request('/api/audio/sfx/play', {
        method: 'POST',
        json: {
          filename
        }
      });

      if (!result.success) {
        alert('Failed to play SFX: ' + result.error);
      }
      // SFX plays without changing status
    } catch (error) {
      console.error('Error playing SFX:', error);
      alert('Error: ' + error.message);
    }
  }

  // Update current status
  async function updateStatus() {
    try {
      const result = await window.webapi.request('/api/audio/music/current');
      if (result.success) {
        if (result.currentFile) {
          nowPlaying.textContent = result.currentFile;
          if (result.isPlaying) {
            playbackStatus.textContent = 'Playing';
            playbackStatus.className = 'value status-playing';
          } else {
            playbackStatus.textContent = 'Paused';
            playbackStatus.className = 'value status-paused';
          }
        } else {
          nowPlaying.textContent = 'None';
          playbackStatus.textContent = 'Idle';
          playbackStatus.className = 'value';
        }
      }
    } catch (error) {
      // Silently fail status updates
      console.error('Error updating status:', error);
    }
  }

  // Start periodic status updates
  function startStatusUpdates() {
    updateStatus();
    statusUpdateInterval = setInterval(updateStatus, 1000);
  }

  // Escape HTML to prevent XSS
  function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
  }

  // Event listeners
  stopMusicBtn.addEventListener('click', stopMusic);

  // Expose API for onclick handlers
  window.audioTest = {
    playMusic,
    requestMusic,
    playSfx
  };

  // Initialize on load
  init();

  console.log('AudioTest initialized');
})();
