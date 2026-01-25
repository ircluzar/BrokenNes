// VoiceTest - Interactive speak.js testing interface
(function() {
  'use strict';

  // DOM Elements
  const textInput = document.getElementById('textInput');
  const speedSlider = document.getElementById('speedSlider');
  const speedValue = document.getElementById('speedValue');
  const pitchSlider = document.getElementById('pitchSlider');
  const pitchValue = document.getElementById('pitchValue');
  const volumeSlider = document.getElementById('volumeSlider');
  const volumeValue = document.getElementById('volumeValue');
  const variantSelect = document.getElementById('variantSelect');
  const variantValue = document.getElementById('variantValue');
  const voiceSelect = document.getElementById('voiceSelect');
  const voiceValue = document.getElementById('voiceValue');
  const speakBtn = document.getElementById('speakBtn');
  const resetBtn = document.getElementById('resetBtn');
  const stopBtn = document.getElementById('stopBtn');
  const status = document.getElementById('status');

  // Default values
  const defaults = {
    speed: 125,
    pitch: 1.0,
    volume: 1.0,
    variant: 'croak',
    voice: 'en-us'
  };

  // Update value displays
  function updateDisplays() {
    speedValue.textContent = speedSlider.value;
    pitchValue.textContent = parseFloat(pitchSlider.value).toFixed(1);
    volumeValue.textContent = parseFloat(volumeSlider.value).toFixed(1);
    variantValue.textContent = variantSelect.value;
    voiceValue.textContent = voiceSelect.value;
  }

  // Get current configuration
  function getCurrentConfig() {
    return {
      speed: parseInt(speedSlider.value),
      pitch: parseFloat(pitchSlider.value),
      volume: parseFloat(volumeSlider.value),
      variant: variantSelect.value,
      voiceName: voiceSelect.value
    };
  }

  // Set status message
  function setStatus(message, type = 'info') {
    status.textContent = message;
    status.className = 'status-text status-' + type;
  }

  // Speak button handler
  function handleSpeak() {
    const text = textInput.value.trim();
    
    if (!text) {
      setStatus('Please enter some text to speak', 'error');
      return;
    }

    if (!window.speak) {
      setStatus('speak.js is not loaded', 'error');
      return;
    }

    const config = getCurrentConfig();
    setStatus(`Speaking with config: speed=${config.speed}, pitch=${config.pitch}, volume=${config.volume}, variant=${config.variant}, voice=${config.voiceName}`, 'info');
    
    try {
      window.speak(text, config);
      setStatus('Speaking...', 'success');
      
      // Estimate duration and update status after
      const estimatedDuration = (text.length / 10) * (125 / config.speed) * 1000;
      setTimeout(() => {
        setStatus('Ready', 'success');
      }, estimatedDuration);
    } catch (error) {
      console.error('Speak error:', error);
      setStatus('Error: ' + error.message, 'error');
    }
  }

  // Reset button handler
  function handleReset() {
    speedSlider.value = defaults.speed;
    pitchSlider.value = defaults.pitch;
    volumeSlider.value = defaults.volume;
    variantSelect.value = defaults.variant;
    voiceSelect.value = defaults.voice;
    updateDisplays();
    setStatus('Reset to default values', 'info');
  }

  // Stop button handler (attempt to stop audio)
  function handleStop() {
    try {
      // Try to stop meSpeak if available
      if (window.meSpeak && typeof window.meSpeak.stop === 'function') {
        window.meSpeak.stop();
        setStatus('Stopped', 'info');
      } else {
        // Try to pause/reset audio context as a fallback
        if (window.nesAudioCtx) {
          try {
            window.nesAudioCtx.suspend();
            setTimeout(() => window.nesAudioCtx.resume(), 100);
            setStatus('Audio interrupted', 'info');
          } catch (e) {
            setStatus('Stop not fully supported', 'warning');
          }
        } else {
          setStatus('Stop not fully supported', 'warning');
        }
      }
    } catch (error) {
      console.error('Stop error:', error);
      setStatus('Could not stop playback', 'error');
    }
  }

  // Event Listeners
  speedSlider.addEventListener('input', updateDisplays);
  pitchSlider.addEventListener('input', updateDisplays);
  volumeSlider.addEventListener('input', updateDisplays);
  variantSelect.addEventListener('change', updateDisplays);
  voiceSelect.addEventListener('change', updateDisplays);
  
  speakBtn.addEventListener('click', handleSpeak);
  resetBtn.addEventListener('click', handleReset);
  stopBtn.addEventListener('click', handleStop);

  // Allow Enter key in textarea with Ctrl to speak
  textInput.addEventListener('keydown', (e) => {
    if (e.ctrlKey && e.key === 'Enter') {
      e.preventDefault();
      handleSpeak();
    }
  });

  // Initialize
  updateDisplays();
  
  // Preload speak.js when ready
  if (window.speakPreload) {
    window.speakPreload({ voiceName: defaults.voice });
    setStatus('Loading TTS library...', 'info');
    
    // Check if loaded after a delay
    setTimeout(() => {
      if (window.meSpeak) {
        setStatus('Ready - TTS library loaded', 'success');
      } else {
        setStatus('Ready - TTS will load on first use', 'info');
      }
    }, 2000);
  } else {
    setStatus('Ready - Waiting for speak.js', 'info');
  }

  // Debug mode
  window.DEBUG_TTS = true;

  console.log('VoiceTest initialized');
})();
