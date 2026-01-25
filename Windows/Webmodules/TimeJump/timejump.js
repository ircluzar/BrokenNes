// TimeJump Web Module
// Automated time-based savestate capture

// State
let isRunning = false;
let intervalSeconds = 2.0;
let captureCount = 0;
let startTime = null;
let nextCaptureTime = null;
let captureIntervalId = null;
let availableStatesCount = 0; // Track backend's available states

// DOM Elements
const elements = {
  // Panels
  startPanel: null,
  runningPanel: null,
  
  // Start panel
  intervalInput: null,
  btnStart: null,
  
  // Running panel
  stateGrid: null,
  btnJump: null,
  btnStop: null,
  
  // Toast
  toast: null
};

// ==================== Initialization ====================

document.addEventListener('DOMContentLoaded', () => {
  console.log('[TimeJump] Initializing...');
  
  initializeElements();
  attachEventListeners();
  
  console.log('[TimeJump] Ready');
});

function initializeElements() {
  // Panels
  elements.startPanel = document.getElementById('startPanel');
  elements.runningPanel = document.getElementById('runningPanel');
  
  // Start panel
  elements.intervalInput = document.getElementById('intervalInput');
  elements.btnStart = document.getElementById('btnStart');
  
  // Running panel
  elements.runningInterval = document.getElementById('runningInterval');
  elements.stateGrid = document.getElementById('stateGrid');
  elements.btnJump = document.getElementById('btnJump');
  elements.btnStop = document.getElementById('btnStop');
  
  // Toast
  elements.toast = document.getElementById('toast');
}

function attachEventListeners() {
  elements.btnStart.addEventListener('click', startTimeJump);
  elements.btnJump.addEventListener('click', performJump);
  elements.btnStop.addEventListener('click', stopTimeJump);
  elements.intervalInput.addEventListener('input', updateIntervalValue);
}

// ==================== UI Updates ====================

function showPanel(panelName) {
  elements.startPanel.classList.toggle('active', panelName === 'start');
  elements.runningPanel.classList.toggle('active', panelName === 'running');
}

function updateIntervalValue() {
  intervalSeconds = parseFloat(elements.intervalInput.value);
  if (isNaN(intervalSeconds) || intervalSeconds < 0.1) {
    intervalSeconds = 0.1;
    elements.intervalInput.value = 0.1;
  }
}

function updateJumpButtonState() {
  if (elements.btnJump) {
    elements.btnJump.disabled = availableStatesCount === 0;
    console.log('[TimeJump] Jump button state:', availableStatesCount > 0 ? 'enabled' : 'disabled', '(available:', availableStatesCount + ')');
  }
}

function addStateBlock(stateHash, thumbnailBase64) {
  const block = document.createElement('div');
  block.className = 'tj-state-block';
  block.dataset.hash = stateHash; // Store the actual state hash
  block.title = `State: ${stateHash.substring(0, 8)}...`; // Show hash on hover
  
  // Set the thumbnail as background if available
  if (thumbnailBase64) {
    block.style.backgroundImage = `url(data:image/png;base64,${thumbnailBase64})`;
  }
  
  elements.stateGrid.appendChild(block);
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

// ==================== Core Functionality ====================

async function startTimeJump() {
  console.log('[TimeJump] Starting with interval:', intervalSeconds);
  
  // Validate interval
  updateIntervalValue();
  
  // Reset state
  captureCount = 0;
  availableStatesCount = 0;
  startTime = Date.now();
  nextCaptureTime = startTime + (intervalSeconds * 1000);
  isRunning = true;
  
  // Update UI
  showPanel('running');
  
  // Clear any existing blocks
  elements.stateGrid.innerHTML = '';
  
  // Initialize button state
  updateJumpButtonState();
  
  // Start capture interval
  captureIntervalId = setInterval(captureState, intervalSeconds * 1000);
  
  showToast('TimeJump started', 'success');
  
  // Capture initial state immediately
  await captureState();
}

function stopTimeJump() {
  console.log('[TimeJump] Stopping...');
  
  // Clear interval
  if (captureIntervalId) {
    clearInterval(captureIntervalId);
    captureIntervalId = null;
  }
  
  // Reset state
  isRunning = false;
  startTime = null;
  nextCaptureTime = null;
  
  // Update UI
  showPanel('start');
  
  showToast(`TimeJump stopped. Captured ${captureCount} states.`, 'info');
}

async function captureState() {
  if (!isRunning) return;
  
  console.log('[TimeJump] Capturing state...');
  
  try {
    // Call API to capture current state
    const result = await apiCall('/api/timejump/capture', { method: 'POST' });
    
    if (result.success) {
      captureCount++;
      nextCaptureTime = Date.now() + (intervalSeconds * 1000);
      
      // Sync with backend state
      availableStatesCount = result.availableStates;
      
      // Add a visual block for the captured state with its hash and thumbnail
      addStateBlock(result.stateHash, result.thumbnail);
      
      // Update jump button state
      updateJumpButtonState();
      
      console.log('[TimeJump] State captured successfully. Hash:', result.stateHash.substring(0, 8) + '...');
      console.log('[TimeJump] Available states:', result.availableStates);
    } else {
      console.error('[TimeJump] Failed to capture state:', result.error);
      showToast('Failed to capture state: ' + result.error, 'error');
    }
  } catch (error) {
    console.error('[TimeJump] Failed to capture state:', error);
    showToast('Failed to capture state', 'error');
  }
}

async function performJump() {
  console.log('[TimeJump] Performing jump...');
  
  try {
    // Call API to perform time jump
    const result = await apiCall('/api/timejump/jump', { method: 'POST' });
    
    if (result.success) {
      console.log('[TimeJump] Jump successful!');
      console.log('[TimeJump] Loaded hash:', result.loadedHash);
      console.log('[TimeJump] Burned hashes:', result.burnedHashes);
      
      // Sync with backend state
      availableStatesCount = result.availableStates;
      
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

// ==================== API Calls ====================

async function apiCall(endpoint, options = {}) {
  console.log('[TimeJump API] Calling:', endpoint, options.method || 'GET');
  
  if (!window.webapi) {
    const error = 'webapi helper not loaded';
    console.error('[TimeJump API] Error:', error);
    showToast(`API Error: ${error}`, 'error');
    return { success: false, error };
  }
  
  try {
    const data = await window.webapi.request(endpoint, options);
    console.log('[TimeJump API] Response:', data);
    return data;
  } catch (error) {
    console.error('[TimeJump API] Error:', error);
    showToast(`API Error: ${error.message}`, 'error');
    return { success: false, error: error.message };
  }
}
