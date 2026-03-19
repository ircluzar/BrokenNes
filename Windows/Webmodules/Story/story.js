// story.js - Story mode orchestration for WinForms overlay

(function() {
  'use strict';

  let gameSave = null;
  let savedShaderState = null; // Store original shader state to restore later

  // Tiny chainable scheduler: supports sync and async Do steps
  // API: Do(fn|asyncFn), Wait(seconds), Start(), Reset()
  let queue = [];
  let idx = 0;
  let started = false;

  function Do(fn) {
    queue.push({ t: 'do', fn: fn });
    return api;
  }

  function Wait(seconds) {
    const ms = Math.max(0, (seconds || 0) * 1000);
    queue.push({ t: 'wait', ms: ms });
    return api;
  }

  function _run() {
    if (idx >= queue.length) return;
    const it = queue[idx++];
    if (it.t === 'do') {
      try {
        const res = it.fn && it.fn();
        if (res && typeof res.then === 'function') {
          res.then(() => _run()).catch(() => _run());
        } else {
          _run();
        }
      } catch (_) {
        _run();
      }
    } else {
      setTimeout(_run, it.ms);
    }
  }

  function Start() {
    if (started) return;
    started = true;
    _run();
  }

  function Reset() {
    idx = 0;
    started = false;
  }

  const api = { Do, Wait, Start, Reset };

  // Helper to set subtitle text
  function setSubtitle(text) {
    try {
      const host = document.getElementById('storySubtitles');
      if (host) {
        host.textContent = text || '';
      }
    } catch (e) {
      console.error('[Story] Failed to set subtitle:', e);
    }
  }

  // Helper to narrate text using TTS
  function narrate(text) {
    try {
      setSubtitle(text);
      
      // Try to use speak.js if available
      if (window.speakit) {
        window.speakit(text);
      } else if (window.speak) {
        window.speak(text, { speed: 125, variant: 'croak', voiceName: 'en-us' });
      } else {
        console.warn('[Story] TTS not available');
      }
    } catch (e) {
      console.error('[Story] Failed to narrate:', e);
    }
  }

  // Helper to load a narration ROM (page1.nes, page2.nes, etc.)
  async function loadNarrationRom(name) {
    try {
      if (!window.webapi || !window.webapi.emulator || !window.webapi.emulator.loadBuiltInRom) {
        console.error('[Story] webapi.emulator.loadBuiltInRom not available');
        return false;
      }

      const result = await window.webapi.emulator.loadBuiltInRom(name, true); // preserveShader = true
      
      if (result && result.success) {
        console.log(`[Story] Successfully loaded ${name}`);
        return true;
      } else {
        console.error(`[Story] Failed to load ${name}:`, result?.error || 'Unknown error');
        return false;
      }
    } catch (e) {
      console.error(`[Story] Error loading ${name}:`, e);
      return false;
    }
  }

  // Initialize and start the story
  async function init() {
    try {
      console.log('[Story] Initializing story mode...');

      // CRITICAL: Switch to Overlay mode so the emulator is visible behind the transparent WebView
      // This is necessary when navigating directly from another webmodule (e.g., Home)
      try {
        if (window.webapi && window.webapi.navigation && window.webapi.navigation.goToOverlay) {
          const result = await window.webapi.navigation.goToOverlay();
          if (result && result.success) {
            console.log('[Story] Switched to Overlay mode');
          } else {
            console.log('[Story] Overlay mode switch returned:', result);
          }
        }
      } catch (e) {
        console.warn('[Story] Failed to switch to Overlay mode:', e);
      }

      // CRITICAL: Resume emulation if it was paused (e.g., when navigating from Home webmodule)
      // Story mode needs the emulator running to display the NES page ROMs
      try {
        if (window.webapi && window.webapi.emulator && window.webapi.emulator.resume) {
          const result = await window.webapi.emulator.resume();
          if (result && result.success) {
            console.log('[Story] Emulation resumed successfully');
          } else {
            console.log('[Story] Emulation resume returned:', result);
          }
        }
      } catch (e) {
        console.warn('[Story] Failed to resume emulation:', e);
      }

      // Set crash behavior to ignore errors for story mode
      try {
        if (window.webapi && window.webapi.rtc && window.webapi.rtc.setCrashBehavior) {
          await window.webapi.rtc.setCrashBehavior('IgnoreErrors');
          console.log('[Story] Set crash behavior to IgnoreErrors');
        }
      } catch (e) {
        console.error('[Story] Failed to set crash behavior:', e);
      }

      // Save current shader state and switch to TV (CRT) shader for story mode
      try {
        if (window.webapi && window.webapi.shader) {
          const currentState = await window.webapi.shader.getCurrent();
          if (currentState && currentState.success) {
            savedShaderState = {
              shader: currentState.shader,
              enabled: currentState.enabled
            };
            console.log('[Story] Saved shader state:', savedShaderState);
          }

          // Switch to TV (CRT) shader
          await window.webapi.shader.setShader('TV', 'story-cutscene');
          console.log('[Story] Switched to TV (CRT) shader');

          // Enable shaders
          await window.webapi.shader.enable();
          console.log('[Story] Enabled shaders');
        }
      } catch (e) {
        console.error('[Story] Failed to set shader:', e);
      }

      // Load game save
      try {
        if (window.gameSave && typeof window.gameSave.load === 'function') {
          gameSave = await window.gameSave.load();
          console.log('[Story] Game save loaded:', gameSave);
        }
      } catch (e) {
        console.error('[Story] Failed to load game save:', e);
        gameSave = {};
      }

      // Ensure speak.js is loaded
      await loadSpeak();

      // Preload meSpeak to avoid first-line delay
      try {
        if (window.speakPreload) {
          window.speakPreload({ voiceName: 'en-us' });
        }
      } catch (e) {
        console.warn('[Story] Failed to preload TTS:', e);
      }

      // Start background story music immediately (loop with gentle fade-in)
      try {
        if (window.webapi && window.webapi.audio && window.webapi.audio.requestMusic) {
          await window.webapi.audio.requestMusic('Story.mp3', true, 800);
          console.log('[Story] Music started');
        } else {
          console.warn('[Story] Audio API not available');
        }
      } catch (e) {
        console.warn('[Story] Failed to start music:', e);
      }

      // Build the story sequence
      buildStorySequence();

      // Start the story after a brief delay
      setTimeout(() => {
        console.log('[Story] Starting story sequence...');
        Start();
      }, 1000);

    } catch (e) {
      console.error('[Story] Initialization failed:', e);
    }
  }

  // Load speak.js dynamically
  function loadSpeak() {
    return new Promise((resolve) => {
      try {
        if (window.speak) {
          resolve();
          return;
        }

        const script = document.createElement('script');
        script.src = '../shared/speak.js';
        script.async = true;
        script.onload = () => resolve();
        script.onerror = () => resolve(); // Continue even if speak.js fails
        document.head.appendChild(script);
      } catch (e) {
        resolve(); // Continue even if loading fails
      }
    });
  }

  // Build the story sequence using the scheduler
  function buildStorySequence() {
    // Clear subtitle initially
    Do(() => setSubtitle(' '))
      .Wait(2)
      // Page 1
      .Do(async () => await loadNarrationRom('page1.nes'))
      .Do(() => narrate('All that little Jimmy wanted was a functional video game console.'))
      .Wait(6)
      // Page 2
      .Do(async () => await loadNarrationRom('page2.nes'))
      .Do(() => narrate('But his mom would keep buying him janky clones instead.'))
      .Wait(6)
      // Page 3
      .Do(async () => await loadNarrationRom('page3.nes'))
      .Do(() => narrate('So little Jimmy broke them all into parts.'))
      .Wait(6)
      // Page 4
      .Do(async () => await loadNarrationRom('page4.nes'))
      .Do(() => narrate('And now, he is ready to build his ultimate console.'))
      .Wait(5)
      // Page 5 (final)
      .Do(async () => await loadNarrationRom('page5.nes'))
      .Wait(7)
      // End: Fade out and navigate to Continue
      .Do(() => finishStory());
  }

  // Finish the story and navigate to Continue
  async function finishStory() {
    try {
      console.log('[Story] Finishing story...');

      // Restore original shader state
      try {
        if (savedShaderState && window.webapi && window.webapi.shader) {
          console.log('[Story] Restoring shader state:', savedShaderState);
          
          // Restore shader
          if (savedShaderState.shader) {
            await window.webapi.shader.setShader(savedShaderState.shader, 'story-cutscene');
            console.log('[Story] Restored shader:', savedShaderState.shader);
          }

          // Restore enabled state
          if (savedShaderState.enabled) {
            await window.webapi.shader.enable();
            console.log('[Story] Restored shaders enabled');
          } else {
            await window.webapi.shader.disable();
            console.log('[Story] Restored shaders disabled');
          }
        }
      } catch (e) {
        console.error('[Story] Failed to restore shader:', e);
      }

      // Clear subtitle
      setSubtitle('');

      // Mark story as seen
      if (gameSave) {
        gameSave.SeenStory = true;
        try {
          if (window.gameSave && window.gameSave.save) {
            await window.gameSave.save(gameSave);
            console.log('[Story] Marked story as seen');
          }
        } catch (e) {
          console.error('[Story] Failed to save:', e);
        }
      }

      // Fade to black
      const overlay = document.getElementById('storyFadeOverlay');
      if (overlay) {
        overlay.style.opacity = '1';
      }

      // Wait for fade
      await new Promise(resolve => setTimeout(resolve, 650));

      // Navigate to Continue module
      try {
        window.location.href = '../Continue/index.html';
      } catch (e) {
        console.error('[Story] Failed to navigate:', e);
      }
    } catch (e) {
      console.error('[Story] Failed to finish story:', e);
    }
  }

  // Start when DOM is ready
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }

  // Expose API for debugging
  window.storyMode = {
    api,
    setSubtitle,
    narrate,
    loadNarrationRom,
    getGameSave: () => gameSave
  };

})();
