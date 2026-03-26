// TimeJump Web Module
// Automated time-based savestate capture

// State
let isRunning = false;
const intervalSeconds = 1.0; // Fixed 1 second interval
let captureCount = 0;
let startTime = null;
let nextCaptureTime = null;
let captureIntervalId = null;
let availableStatesCount = 0; // Track backend's available states
let isCapturing = false; // Track if a capture is currently in progress

// Level tracking
let currentLevel = 0;
const levelDurationMs = 15000; // 15 seconds per level
let levelStartTime = null;
let levelProgressInterval = null;
let autoCorruptEnabled = false;
const stateProgressByHash = new Map();

// DOM Elements
const elements = {
  // Panels
  startPanel: null,
  runningPanel: null,
  
  // Start panel
  btnStart: null,
  welcomeMessage: null,
  errorMessage: null,
  
  // Running panel
  stateGrid: null,
  btnJump: null,
  btnStop: null,
  btnReset: null,
  levelDisplay: null,
  levelProgressBar: null,
  levelProgressText: null,
  timelineStatus: null,
  
  // Toast
  toast: null
};

// ==================== Initialization ====================

document.addEventListener('DOMContentLoaded', () => {
  console.log('[TimeJump] Initializing...');
  
  initializeElements();
  attachEventListeners();
  setupWebmoduleButtons();
  
  console.log('[TimeJump] Ready');
});

function initializeElements() {
  // Panels
  elements.startPanel = document.getElementById('startPanel');
  elements.runningPanel = document.getElementById('runningPanel');
  
  // Start panel
  elements.btnStart = document.getElementById('btnStart');
  elements.welcomeMessage = document.getElementById('welcomeMessage');
  elements.errorMessage = document.getElementById('errorMessage');
  
  // Running panel
  elements.runningInterval = document.getElementById('runningInterval');
  elements.stateGrid = document.getElementById('stateGrid');
  elements.btnJump = document.getElementById('btnJump');
  elements.btnStop = document.getElementById('btnStop');
  elements.btnReset = document.getElementById('btnReset');
  elements.levelDisplay = document.getElementById('levelDisplay');
  elements.levelProgressBar = document.getElementById('levelProgressBar');
  elements.levelProgressText = document.getElementById('levelProgressText');
  elements.timelineStatus = document.getElementById('timelineStatus');
  
  // Toast
  elements.toast = document.getElementById('toast');
}

function attachEventListeners() {
  elements.btnStart.addEventListener('click', handleStartClick);
  elements.btnJump.addEventListener('click', performJump);
  elements.btnStop.addEventListener('click', stopTimeJump);
  elements.btnReset.addEventListener('click', resetTimeJump);
}

// ==================== UI Updates ====================

function showPanel(panelName) {
  elements.startPanel.classList.toggle('active', panelName === 'start');
  elements.runningPanel.classList.toggle('active', panelName === 'running');
}

function updateJumpButtonState() {
  if (elements.btnJump) {
    elements.btnJump.disabled = availableStatesCount === 0;
    console.log('[TimeJump] Jump button state:', availableStatesCount > 0 ? 'enabled' : 'disabled', '(available:', availableStatesCount + ')');
  }
}

function addStateBlock(stateHash, thumbnailBase64) {
  const snapshot = getCurrentProgressSnapshot();
  const block = document.createElement('div');
  block.className = 'tj-state-block';
  block.dataset.hash = stateHash; // Store the actual state hash
  block.title = `State: ${stateHash.substring(0, 8)}... | L${snapshot.level} ${snapshot.progressPercent.toFixed(1)}%`; // Show hash/progress on hover
  
  // Set the thumbnail as background if available
  if (thumbnailBase64) {
    block.style.backgroundImage = `url(data:image/png;base64,${thumbnailBase64})`;
  }
  
  // Add click handler for state query
  block.addEventListener('click', () => performStateQuery(stateHash));
  
  elements.stateGrid.appendChild(block);
  
  // Recalculate grid layout to fit all blocks
  updateGridLayout();
}

function updateGridLayout() {
  if (!elements.stateGrid) return;
  
  const blocks = elements.stateGrid.querySelectorAll('.tj-state-block:not(.burning):not(.loaded)');
  const blockCount = blocks.length;
  
  if (blockCount === 0) return;
  
  // Get the visualization container dimensions
  const visualization = elements.stateGrid.closest('.tj-visualization');
  if (!visualization) return;
  
  const containerWidth = visualization.clientWidth - 32; // Account for padding
  const containerHeight = visualization.clientHeight - 48; // Account for padding and label space
  
  // Define base size and minimum size
  const baseSize = 52;
  const minSize = 24;
  const gap = 6;
  
  // Calculate optimal grid layout
  let blockSize = baseSize;
  let cols = 1;
  let rows = 1;
  
  // Try different block sizes starting from base size down to minimum
  for (let size = baseSize; size >= minSize; size -= 2) {
    // Calculate how many columns we can fit
    const maxCols = Math.floor((containerWidth + gap) / (size + gap));
    if (maxCols < 1) continue;
    
    // Calculate how many rows we need
    const neededRows = Math.ceil(blockCount / maxCols);
    
    // Calculate total height needed
    const totalHeight = neededRows * size + (neededRows - 1) * gap;
    
    // If this fits, use it
    if (totalHeight <= containerHeight) {
      blockSize = size;
      cols = maxCols;
      rows = neededRows;
      break;
    }
  }
  
  // Update grid template
  elements.stateGrid.style.gridTemplateColumns = `repeat(${cols}, ${blockSize}px)`;
  elements.stateGrid.style.gap = `${gap}px`;
  
  // Update all block sizes
  blocks.forEach(block => {
    block.style.width = `${blockSize}px`;
    block.style.height = `${blockSize * 0.92}px`; // Maintain aspect ratio
  });
  
  console.log(`[TimeJump] Grid layout: ${blockCount} blocks, ${cols}x${rows}, ${blockSize}px each`);
}

function removeStateBlocksByHash(hashes, loadedHash) {
  if (!hashes || hashes.length === 0) return;
  
  hashes.forEach((hash, index) => {
    // Find the block with this specific hash
    const block = elements.stateGrid.querySelector(`.tj-state-block[data-hash="${hash}"]`);
    
    if (block && !block.classList.contains('burning')) {
      const isLoaded = hash === loadedHash;
      
      setTimeout(() => {
        // Apply different class based on whether this was the loaded state
        block.classList.add(isLoaded ? 'loaded' : 'burning');
        setTimeout(() => {
          block.remove();
          stateProgressByHash.delete(hash);
          // Recalculate grid layout after removal
          updateGridLayout();
        }, 500); // Match animation duration
      }, index * 50); // Stagger the burning animation
    }
  });
}

function showToast(message, type = 'info') {
  elements.toast.textContent = message;
  elements.toast.className = `tj-toast show ${type}`;
  
  setTimeout(() => {
    elements.toast.classList.remove('show');
  }, 3000);
}

function getCurrentProgressSnapshot() {
  if (!levelStartTime) {
    return {
      level: currentLevel,
      progressPercent: 0,
      totalProgress: currentLevel
    };
  }

  const elapsed = Math.max(0, Date.now() - levelStartTime);
  const rawProgress = (elapsed / levelDurationMs) * 100;
  const progressPercent = Math.max(0, Math.min(rawProgress, 99.9));

  return {
    level: currentLevel,
    progressPercent,
    totalProgress: currentLevel + (progressPercent / 100)
  };
}

function setProgressFromSnapshot(snapshot, reason = 'current run') {
  if (!snapshot) return;

  currentLevel = snapshot.level;
  const elapsedForLevel = (snapshot.progressPercent / 100) * levelDurationMs;
  levelStartTime = Date.now() - elapsedForLevel;

  updateLevelDisplay();
  updateLevelProgress();

  if (elements.timelineStatus) {
    elements.timelineStatus.textContent = `Timeline anchor: ${reason}`;
  }
}

function updateTimelineShift(previousSnapshot, nextSnapshot) {
  if (!elements.timelineStatus || !previousSnapshot || !nextSnapshot) return;

  const delta = nextSnapshot.totalProgress - previousSnapshot.totalProgress;

  if (Math.abs(delta) < 0.01) {
    elements.timelineStatus.textContent = 'Timeline anchor: same progression point';
    return;
  }

  const direction = delta > 0 ? 'future' : 'past';
  const magnitude = Math.abs(delta).toFixed(2);
  elements.timelineStatus.textContent = `Timeline anchor: ${direction} (${magnitude} levels from previous)`;
}

// ==================== Level Progression ====================

function startLevelProgression() {
  console.log('[TimeJump] Starting level progression...');
  
  currentLevel = 0;
  levelStartTime = Date.now();
  if (elements.timelineStatus) {
    elements.timelineStatus.textContent = 'Timeline anchor: current run';
  }
  updateLevelDisplay();
  updateLevelProgress();
  
  // Update progress bar every 100ms for smooth animation
  levelProgressInterval = setInterval(updateLevelProgress, 100);
}

function stopLevelProgression() {
  console.log('[TimeJump] Stopping level progression...');
  
  if (levelProgressInterval) {
    clearInterval(levelProgressInterval);
    levelProgressInterval = null;
  }
  
  currentLevel = 0;
  levelStartTime = null;

  if (elements.levelProgressText) {
    elements.levelProgressText.textContent = '0.0%';
  }

  if (elements.timelineStatus) {
    elements.timelineStatus.textContent = 'Timeline anchor: current run';
  }
}

function updateLevelProgress() {
  if (!levelStartTime) return;
  
  const elapsed = Date.now() - levelStartTime;
  const progress = Math.min((elapsed / levelDurationMs) * 100, 100);
  
  // Update progress bar
  if (elements.levelProgressBar) {
    elements.levelProgressBar.style.width = `${progress}%`;
  }

  if (elements.levelProgressText) {
    elements.levelProgressText.textContent = `${progress.toFixed(1)}%`;
  }
  
  // Check if level should increase
  if (progress >= 100) {
    levelUp();
  }
}

function updateLevelDisplay() {
  const snapshot = getCurrentProgressSnapshot();

  if (elements.levelDisplay) {
    elements.levelDisplay.textContent = `Level ${currentLevel}`;
  }

  if (elements.levelProgressText) {
    elements.levelProgressText.textContent = `${snapshot.progressPercent.toFixed(1)}%`;
  }
}

async function levelUp() {
  currentLevel++;
  levelStartTime = Date.now();
  
  console.log('[TimeJump] Level up! New level:', currentLevel);
  updateLevelDisplay();
  
  // Reset progress bar
  if (elements.levelProgressBar) {
    elements.levelProgressBar.style.width = '0%';
  }

  if (elements.levelProgressText) {
    elements.levelProgressText.textContent = '0.0%';
  }
  
  showToast(`Level ${currentLevel}!`, 'success');
  
  // Start auto-corruption on level 1
  if (currentLevel === 1) {
    await startAutoCorruption();
  }
}

// ==================== Auto-Corruption ====================

async function startAutoCorruption() {
  console.log('[TimeJump] Starting auto-corruption...');
  
  try {
    // First, get available memory domains
    const domainsResult = await window.webapi.rtc.getDomains();
    
    if (!domainsResult.success || !domainsResult.domains) {
      console.error('[TimeJump] Failed to get memory domains:', domainsResult.error);
      showToast('Failed to get memory domains', 'error');
      return;
    }
    
    // Filter for the domains we want to target
    const availableDomains = domainsResult.domains;
    const targetDomainNames = ['PRG RAM', 'CHR', 'System RAM'];
    const targetDomains = [];
    
    console.log('[TimeJump] Available domains:', availableDomains);
    
    // Find domains that match our target names (case-insensitive, partial match)
    for (const targetName of targetDomainNames) {
      const domain = availableDomains.find(d => 
        d.name && d.name.toLowerCase().includes(targetName.toLowerCase())
      );
      
      if (domain) {
        targetDomains.push(domain.key); // Use domain key for selection
        console.log('[TimeJump] Found target domain:', domain.name, '(key:', domain.key + ')');
      } else {
        console.log('[TimeJump] Domain not found:', targetName);
      }
    }
    
    if (targetDomains.length === 0) {
      console.warn('[TimeJump] No target domains found, skipping auto-corruption');
      showToast('No compatible memory domains for corruption', 'warning');
      return;
    }
    
    // Set domain selection
    const selectionResult = await window.webapi.rtc.setDomainSelection(targetDomains);
    
    if (!selectionResult.success) {
      console.error('[TimeJump] Failed to set domain selection:', selectionResult.error);
      showToast('Failed to configure domains', 'error');
      return;
    }
    
    console.log('[TimeJump] Domains configured:', targetDomains);
    
    // Set blast type to BITFLIP
    const blastTypeResult = await window.webapi.rtc.setBlastType('BITFLIP');
    
    if (!blastTypeResult.success) {
      console.error('[TimeJump] Failed to set blast type:', blastTypeResult.error);
      showToast('Failed to set blast type', 'error');
      return;
    }
    
    console.log('[TimeJump] Blast type set to BITFLIP');
    
    // Set intensity to 1
    const intensityResult = await window.webapi.rtc.setIntensity(1);
    
    if (!intensityResult.success) {
      console.error('[TimeJump] Failed to set intensity:', intensityResult.error);
      showToast('Failed to set intensity', 'error');
      return;
    }
    
    console.log('[TimeJump] Intensity set to 1');
    
    // Enable auto-corrupt
    const enableResult = await window.webapi.rtc.setAutoCorrupt(true);
    
    if (!enableResult.success) {
      console.error('[TimeJump] Failed to enable auto-corrupt:', enableResult.error);
      showToast('Failed to enable auto-corrupt', 'error');
      return;
    }
    
    autoCorruptEnabled = true;
    console.log('[TimeJump] Auto-corruption enabled successfully');
    showToast('Auto-corruption activated!', 'success');
    
  } catch (error) {
    console.error('[TimeJump] Error starting auto-corruption:', error);
    showToast('Error starting auto-corruption', 'error');
  }
}

async function stopAutoCorruption() {
  if (!autoCorruptEnabled) {
    console.log('[TimeJump] Auto-corruption is not enabled, nothing to stop');
    return;
  }
  
  console.log('[TimeJump] Stopping auto-corruption...');
  
  try {
    const result = await window.webapi.rtc.setAutoCorrupt(false);
    
    if (!result.success) {
      console.error('[TimeJump] Failed to disable auto-corrupt:', result.error);
      showToast('Failed to disable auto-corrupt', 'error');
      return;
    }
    
    autoCorruptEnabled = false;
    console.log('[TimeJump] Auto-corruption disabled successfully');
    
  } catch (error) {
    console.error('[TimeJump] Error stopping auto-corruption:', error);
    showToast('Error stopping auto-corruption', 'error');
  }
}

// ==================== Core Functionality ====================

async function handleStartClick() {
  console.log('[TimeJump] Start button clicked, validating ROM...');
  
  // Validate that a ROM is loaded and it's not a test ROM
  const validation = await validateRomLoaded();
  if (!validation.valid) {
    showError(validation.error);
    return;
  }
  
  startTimeJump();
}

async function validateRomLoaded() {
  try {
    const result = await window.webapi.timejump.validateRom();
    return result;
  } catch (error) {
    console.error('[TimeJump] Failed to validate ROM:', error);
    return { valid: false, error: 'Failed to validate ROM state' };
  }
}

function showError(message) {
  if (elements.errorMessage) {
    elements.errorMessage.textContent = message;
    elements.errorMessage.style.display = 'block';
  }
  showToast(message, 'error');
}

function hideError() {
  if (elements.errorMessage) {
    elements.errorMessage.style.display = 'none';
  }
}

async function startTimeJump() {
  console.log('[TimeJump] Starting with interval:', intervalSeconds);
  
  hideError();
  
  // Reset TimeJump system (clears states, resets game)
  try {
    const result = await window.webapi.timejump.reset();
    if (!result.success) {
      console.error('[TimeJump] Failed to reset before starting:', result.error);
      showToast('Failed to prepare TimeJump: ' + result.error, 'error');
      return;
    }
    console.log('[TimeJump] Reset complete, ready to start');
  } catch (error) {
    console.error('[TimeJump] Reset error before starting:', error);
    showToast('Failed to prepare TimeJump', 'error');
    return;
  }
  
  // Reset state
  captureCount = 0;
  availableStatesCount = 0;
  currentLevel = 0;
  stateProgressByHash.clear();
  startTime = Date.now();
  nextCaptureTime = startTime + (intervalSeconds * 1000);
  isRunning = true;
  
  // Reset level display
  if (elements.levelDisplay) {
    elements.levelDisplay.textContent = 'Level 0';
  }
  if (elements.levelProgressBar) {
    elements.levelProgressBar.style.width = '0%';
  }
  
  // Hide the mainform menu bar
  try {
    await window.webapi.ui.hideMenu();
    console.log('[TimeJump] Menu bar hidden');
  } catch (error) {
    console.error('[TimeJump] Failed to hide menu bar:', error);
  }
  
  // Update UI
  showPanel('running');
  
  // Clear any existing blocks
  elements.stateGrid.innerHTML = '';
  
  // Initialize button state
  updateJumpButtonState();
  
  // Start capture interval
  captureIntervalId = setInterval(captureState, intervalSeconds * 1000);
  
  // Start level progression
  startLevelProgression();
  
  showToast('TimeJump started', 'success');
  
  // Capture initial state immediately
  await captureState();
}

async function stopTimeJump() {
  console.log('[TimeJump] Stopping...');
  
  // Clear interval
  if (captureIntervalId) {
    clearInterval(captureIntervalId);
    captureIntervalId = null;
  }
  
  // Stop level progression
  stopLevelProgression();
  
  // Stop auto-corruption if enabled
  await stopAutoCorruption();
  
  // Reset state
  isRunning = false;
  startTime = null;
  nextCaptureTime = null;
  
  // Show the mainform menu bar
  try {
    await window.webapi.ui.showMenu();
    console.log('[TimeJump] Menu bar shown');
  } catch (error) {
    console.error('[TimeJump] Failed to show menu bar:', error);
  }
  
  // Update UI
  showPanel('start');
  
  showToast(`TimeJump stopped. Captured ${captureCount} states.`, 'info');
}

async function resetTimeJump() {
  console.log('[TimeJump] Resetting...');
  
  // Temporarily pause capturing during reset
  const wasRunning = isRunning;
  isRunning = false;
  
  // Wait for any in-flight capture to complete
  while (isCapturing) {
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  
  try {
    // Stop level progression
    stopLevelProgression();
    
    // Stop auto-corruption if enabled
    await stopAutoCorruption();
    
    // Call API to reset (clears states, resets game, goes back to level 0)
    const result = await window.webapi.timejump.reset();
    
    if (result.success) {
      // Clear the state grid
      elements.stateGrid.innerHTML = '';
      
      // Reset local state
      captureCount = 0;
      availableStatesCount = 0;
      currentLevel = 0;
      stateProgressByHash.clear();
      
      // Reset level display
      if (elements.levelDisplay) {
        elements.levelDisplay.textContent = 'Level 0';
      }
      if (elements.levelProgressBar) {
        elements.levelProgressBar.style.width = '0%';
      }
      
      // Update button states
      updateJumpButtonState();
      
      // Resume if we were running
      if (wasRunning) {
        isRunning = true;
        
        // Restart level progression
        startLevelProgression();
        
        // Resume capturing
        startTime = Date.now();
        nextCaptureTime = startTime + (intervalSeconds * 1000);
        
        // Capture first state
        await captureState();
      }
      
      showToast('TimeJump reset! Starting fresh...', 'success');
    } else {
      // Restore running state on failure
      isRunning = wasRunning;
      console.error('[TimeJump] Reset failed:', result.error);
      showToast('Failed to reset: ' + result.error, 'error');
    }
  } catch (error) {
    // Restore running state on error
    isRunning = wasRunning;
    console.error('[TimeJump] Reset error:', error);
    showToast('Failed to reset TimeJump', 'error');
  }
}

async function captureState() {
  if (!isRunning) return;
  
  // Skip if a capture is already in progress
  if (isCapturing) {
    console.log('[TimeJump] Skipping capture - previous capture still in progress');
    return;
  }
  
  console.log('[TimeJump] Capturing state...');
  isCapturing = true;
  
  try {
    // Call API to capture current state with increased timeout (30 seconds)
    const result = await window.webapi.timejump.capture();
    
    if (result.success) {
      captureCount++;
      nextCaptureTime = Date.now() + (intervalSeconds * 1000);
      
      // Sync with backend state
      availableStatesCount = result.availableStates;
      
      // Add a visual block for the captured state with its hash and thumbnail
      addStateBlock(result.stateHash, result.thumbnail);

      // Persist progress metadata for this state so jumps restore timeline context
      stateProgressByHash.set(result.stateHash, getCurrentProgressSnapshot());
      
      // Update jump button state
      updateJumpButtonState();
      
      console.log('[TimeJump] State captured successfully. Hash:', result.stateHash.substring(0, 8) + '...');
      console.log('[TimeJump] Available states:', result.availableStates);
    } else {
      console.error('[TimeJump] Failed to capture state:', result.error);
      // Don't show toast for skipped captures (when backend is busy)
      if (result.error !== 'Failed to capture state') {
        showToast('Failed to capture state: ' + result.error, 'error');
      }
    }
  } catch (error) {
    console.error('[TimeJump] Failed to capture state:', error);
    // Don't show toast for timeout/abort errors
    if (!error.message?.includes('abort')) {
      showToast('Failed to capture state', 'error');
    }
  } finally {
    isCapturing = false;
  }
}

async function performJump() {
  console.log('[TimeJump] Performing jump...');
  const previousSnapshot = getCurrentProgressSnapshot();
  
  try {
    // Call API to perform time jump
    const result = await window.webapi.timejump.jump();
    
    if (result.success) {
      console.log('[TimeJump] Jump successful!');
      console.log('[TimeJump] Loaded hash:', result.loadedHash);
      console.log('[TimeJump] Burned hashes:', result.burnedHashes);
      
      // Sync with backend state
      availableStatesCount = result.availableStates;

      // Restore level/progress from loaded state if we have a snapshot
      const loadedSnapshot = stateProgressByHash.get(result.loadedHash);
      if (loadedSnapshot) {
        setProgressFromSnapshot(loadedSnapshot, `jumped to L${loadedSnapshot.level} ${loadedSnapshot.progressPercent.toFixed(1)}%`);
        updateTimelineShift(previousSnapshot, loadedSnapshot);
      } else if (elements.timelineStatus) {
        elements.timelineStatus.textContent = 'Timeline anchor: state has no progress metadata';
      }
      
      // Remove the specific blocks that correspond to the burned states
      removeStateBlocksByHash(result.burnedHashes, result.loadedHash);
      
      // Update jump button state
      updateJumpButtonState();
      
      showToast(`Jumped through time! Burned ${result.burnedHashes.length} states`, 'success');
      return true;
    } else {
      console.error('[TimeJump] Jump failed:', result.error);
      showToast('Jump failed: ' + result.error, 'error');
      return false;
    }
  } catch (error) {
    console.error('[TimeJump] Failed to perform jump:', error);
    showToast('Failed to perform jump', 'error');
    return false;
  }
}

async function performStateQuery(queryHash) {
  console.log('[TimeJump] Querying state:', queryHash.substring(0, 8) + '...');
  const previousSnapshot = getCurrentProgressSnapshot();
  
  try {
    // Call API to query a specific state (loads random from top 3, burns top 8)
    const result = await window.webapi.timejump.query(queryHash);
    
    if (result.success) {
      console.log('[TimeJump] Query successful!');
      console.log('[TimeJump] Loaded hash:', result.loadedHash);
      console.log('[TimeJump] Burned hashes (top 8):', result.burnedHashes);
      
      // Sync with backend state
      availableStatesCount = result.availableStates;

      // Restore level/progress from loaded state if we have a snapshot
      const loadedSnapshot = stateProgressByHash.get(result.loadedHash);
      if (loadedSnapshot) {
        setProgressFromSnapshot(loadedSnapshot, `queried L${loadedSnapshot.level} ${loadedSnapshot.progressPercent.toFixed(1)}%`);
        updateTimelineShift(previousSnapshot, loadedSnapshot);
      } else if (elements.timelineStatus) {
        elements.timelineStatus.textContent = 'Timeline anchor: state has no progress metadata';
      }
      
      // Remove the specific blocks that correspond to the burned states
      removeStateBlocksByHash(result.burnedHashes, result.loadedHash);
      
      // Update jump button state
      updateJumpButtonState();
      
      showToast(`State query loaded! Burned ${result.burnedHashes.length} states`, 'success');
      return true;
    } else {
      console.error('[TimeJump] Query failed:', result.error);
      showToast('Query failed: ' + result.error, 'error');
      return false;
    }
  } catch (error) {
    console.error('[TimeJump] Failed to query state:', error);
    showToast('Failed to query state', 'error');
    return false;
  }
}

// ==================== Webmodule Button Handling (X/Y) ====================

function setupWebmoduleButtons() {
  // Start polling for X/Y button events
  if (window.webapi && window.webapi.input && window.webapi.input.startPolling) {
    window.webapi.input.startPolling(50);
    
    // Listen for button press events
    window.addEventListener('buttonPressed', (event) => {
      const button = event.detail.button;
      console.log(`[TimeJump] Webmodule button ${button} pressed`);
      
      if (button === 'X') {
        handleXButton();
      } else if (button === 'Y') {
        handleYButton();
      }
    });
    
    console.log('[TimeJump] Webmodule button handlers registered (X=Jump, Y=Reset)');
  } else {
    console.warn('[TimeJump] Webmodule input API not available');
  }
}

function handleXButton() {
  // X button = Perform Jump (if running and states available)
  if (isRunning && availableStatesCount > 0) {
    console.log('[TimeJump] X button -> Performing jump');
    pressJumpButton();
  } else if (!isRunning) {
    console.log('[TimeJump] X button ignored - TimeJump not running');
  } else {
    console.log('[TimeJump] X button ignored - No states available');
  }
}

function pressJumpButton() {
  if (!elements.btnJump) {
    performJump();
    return;
  }

  if (elements.btnJump.disabled) {
    console.log('[TimeJump] Jump button press ignored - disabled');
    return;
  }

  elements.btnJump.classList.add('tj-btn-pressed');
  elements.btnJump.click();

  setTimeout(() => {
    elements.btnJump.classList.remove('tj-btn-pressed');
  }, 120);
}

function handleYButton() {
  // Y button = Reset (if running)
  if (isRunning) {
    console.log('[TimeJump] Y button -> Resetting');
    resetTimeJump();
  } else {
    console.log('[TimeJump] Y button ignored - TimeJump not running');
  }
}


