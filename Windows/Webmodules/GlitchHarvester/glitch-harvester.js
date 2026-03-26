// Glitch Harvester Web Module
// ID-based state machine implementation

// State
let selectedBaseId = null;
let selectedStashId = null;
let selectedStockpileId = null;
let currentRenameId = null;

// Web API
const api = window.webapi;

// RTC State
let rtcAutoCorruptEnabled = false;
let selectedDomains = [];
let crashPollingInterval = null;
let currentCrashBehavior = 'IgnoreErrors';
let imagineBugUnlocked = false;
let imagineStateInitialized = false;

// DOM Elements
const elements = {
  // RTC
  chkAutoCorrupt: null,
  blastTypeSelect: null,
  intensity: null,
  intensityValue: null,
  btnManualBlast: null,
  btnLetItRip: null,
  domainList: null,
  crashBehavior: null,
  crashStatus: null,
  
  // Stash intensity
  stashIntensity: null,
  stashIntensityValue: null,
  
  // Base states
  baseNameInput: null,
  btnAddBase: null,
  baseList: null,
  btnLoadBase: null,
  btnDeleteBase: null,
  
  // Stash
  chkLoadOnOperation: null,
  chkLoadOnClick: null,
  stashList: null,
  btnBlast: null,
  btnReplayStash: null,
  btnKeep: null,
  btnClearStash: null,
  
  // Stockpile
  stockpileList: null,
  btnReplayStock: null,
  btnRenameStock: null,
  btnDeleteStock: null,
  btnExport: null,
  fileImport: null,
  
  // Imagine
  imagineFlavor: null,
  imagineEpoch: null,
  btnLoadModel: null,
  imagineBytesToPredict: null,
  imagineTemperature: null,
  imagineTemperatureValue: null,
  imagineTopK: null,
  btnImagineBug: null,
  imagineStatus: null,

  // Targeted Imagine
  chkTargetedImagine: null,
  chkLoadOnImagine: null,
  targetModeSelect: null,
  targetModeRow: null,
  singleScanlineSelector: null,
  scanlineRangeSelector: null,
  targetScanline: null,
  targetScanlineValue: null,
  rangeStart: null,
  rangeStartValue: null,
  rangeEnd: null,
  rangeEndValue: null,
  rangeCount: null,
  frameMapCanvas: null,
  targetStatusText: null,
  lastCaptureInfo: null,
  
  // Rename Modal
  renameModal: null,
  renameInput: null,
  btnRenameCancel: null,
  btnRenameConfirm: null,
  
  // Confirm Modal
  confirmModal: null,
  confirmTitle: null,
  confirmMessage: null,
  btnConfirmCancel: null,
  btnConfirmOk: null,
  
  // Toast
  toast: null
};

// Confirmation callback
let confirmCallback = null;

// Initialize
document.addEventListener('DOMContentLoaded', async () => {
  console.log('[GH] DOM Content Loaded - Initializing Glitch Harvester');
  if (!api) {
    console.error('[GH] webapi helper not loaded');
    showToast('Web API not available', 'error');
    return;
  }
  initializeElements();
  console.log('[GH] Elements initialized');

  await syncImagineUnlockState();
  console.log('[GH] Imagine unlock state loaded:', imagineBugUnlocked);

  attachEventListeners();
  console.log('[GH] Event listeners attached');
  
  // Initialize RTC state
  await loadRTCState();
  console.log('[GH] RTC state loading...');
  
  if (imagineBugUnlocked && !imagineStateInitialized) {
    await loadImagineState();
    imagineStateInitialized = true;
    console.log('[GH] Imagine state loading...');
  }
  
  await refreshAll();
  console.log('[GH] Initial refresh triggered');
  
  // Auto-refresh disabled for debugging
  // setInterval(refreshAll, 3000);
  console.log('[GH] Auto-refresh DISABLED for debugging');
});

async function syncImagineUnlockState() {
  let unlocked = false;

  try {
    const progressionData = await api.progression.getState();
    const unlockedWebmodules = Array.isArray(progressionData?.unlockedWebmodules)
      ? progressionData.unlockedWebmodules
      : [];

    unlocked = unlockedWebmodules.some(id =>
      String(id).trim().toLowerCase() === 'imaginebug'
    );
  } catch (error) {
    console.warn('[GH] Failed to load progression state for Imagine unlock check:', error);
  }

  const unlockedChanged = imagineBugUnlocked !== unlocked;
  imagineBugUnlocked = unlocked;
  applyImagineUnlockUi(unlocked);

  if (unlockedChanged && unlocked && !imagineStateInitialized) {
    await loadImagineState();
    imagineStateInitialized = true;
  }
}

function applyImagineUnlockUi(unlocked) {
  const imagineTab = document.querySelector('.gh-tab[data-tab="imagine"]');
  const imagineSection = document.querySelector('.gh-section[data-section="imagine"]');

  if (imagineTab) {
    imagineTab.style.display = unlocked ? '' : 'none';
  }

  if (imagineSection) {
    imagineSection.style.display = unlocked ? '' : 'none';
  }

  ensureImagineCrashBehaviorOption(unlocked);

  if (!unlocked) {
    const activeTab = document.querySelector('.gh-tab.active');
    if (activeTab?.dataset?.tab === 'imagine') {
      switchTab('rtc');
    }
  }
}

function ensureImagineCrashBehaviorOption(unlocked) {
  if (!elements.crashBehavior) return;

  let imagineFixOption = elements.crashBehavior.querySelector('option[value="ImagineFix"]');
  if (unlocked) {
    if (!imagineFixOption) {
      imagineFixOption = document.createElement('option');
      imagineFixOption.value = 'ImagineFix';
      imagineFixOption.textContent = 'Imagine Fix';
      elements.crashBehavior.appendChild(imagineFixOption);
    }
    return;
  }

  if (imagineFixOption) {
    imagineFixOption.remove();
  }

  if (elements.crashBehavior.value === 'ImagineFix') {
    elements.crashBehavior.value = 'IgnoreErrors';
  }
}

function initializeElements() {
  // RTC
  elements.chkAutoCorrupt = document.getElementById('chkAutoCorrupt');
  elements.blastTypeSelect = document.getElementById('blastTypeSelect');
  elements.intensity = document.getElementById('intensity');
  elements.intensityValue = document.getElementById('intensityValue');
  elements.btnManualBlast = document.getElementById('btnManualBlast');
  elements.btnLetItRip = document.getElementById('btnLetItRip');
  elements.domainList = document.getElementById('domainList');
  elements.crashBehavior = document.getElementById('crashBehavior');
  elements.crashStatus = document.getElementById('crashStatus');
  
  // Stash intensity
  elements.stashIntensity = document.getElementById('stashIntensity');
  elements.stashIntensityValue = document.getElementById('stashIntensityValue');
  
  // Base states
  elements.baseNameInput = document.getElementById('baseNameInput');
  elements.btnAddBase = document.getElementById('btnAddBase');
  elements.baseList = document.getElementById('baseList');
  elements.btnLoadBase = document.getElementById('btnLoadBase');
  elements.btnDeleteBase = document.getElementById('btnDeleteBase');
  
  // Stash
  elements.chkLoadOnOperation = document.getElementById('chkLoadOnOperation');
  elements.chkLoadOnClick = document.getElementById('chkLoadOnClick');
  elements.stashList = document.getElementById('stashList');
  elements.btnBlast = document.getElementById('btnBlast');
  elements.btnReplayStash = document.getElementById('btnReplayStash');
  elements.btnKeep = document.getElementById('btnKeep');
  elements.btnClearStash = document.getElementById('btnClearStash');
  
  // Stockpile
  elements.stockpileList = document.getElementById('stockpileList');
  elements.btnReplayStock = document.getElementById('btnReplayStock');
  elements.btnRenameStock = document.getElementById('btnRenameStock');
  elements.btnDeleteStock = document.getElementById('btnDeleteStock');
  elements.btnExport = document.getElementById('btnExport');
  elements.fileImport = document.getElementById('fileImport');
  
  // Imagine
  elements.imagineFlavor = document.getElementById('imagineFlavor');
  elements.imagineEpoch = document.getElementById('imagineEpoch');
  elements.btnLoadModel = document.getElementById('btnLoadModel');
  elements.imagineBytesToPredict = document.getElementById('imagineBytesToPredict');
  elements.imagineTemperature = document.getElementById('imagineTemperature');
  elements.imagineTemperatureValue = document.getElementById('imagineTemperatureValue');
  elements.imagineTopK = document.getElementById('imagineTopK');
  elements.btnImagineBug = document.getElementById('btnImagineBug');
  elements.imagineStatus = document.getElementById('imagineStatus');

  // Targeted Imagine
  elements.chkTargetedImagine = document.getElementById('chkTargetedImagine');
  elements.chkLoadOnImagine = document.getElementById('chkLoadOnImagine');
  elements.targetModeSelect = document.getElementById('targetModeSelect');
  elements.targetModeRow = document.getElementById('targetModeRow');
  elements.singleScanlineSelector = document.getElementById('singleScanlineSelector');
  elements.scanlineRangeSelector = document.getElementById('scanlineRangeSelector');
  elements.targetScanline = document.getElementById('targetScanline');
  elements.targetScanlineValue = document.getElementById('targetScanlineValue');
  elements.rangeStart = document.getElementById('rangeStart');
  elements.rangeStartValue = document.getElementById('rangeStartValue');
  elements.rangeEnd = document.getElementById('rangeEnd');
  elements.rangeEndValue = document.getElementById('rangeEndValue');
  elements.rangeCount = document.getElementById('rangeCount');
  elements.frameMapCanvas = document.getElementById('frameMapCanvas');
  elements.targetStatusText = document.getElementById('targetStatusText');
  elements.lastCaptureInfo = document.getElementById('lastCaptureInfo');
  elements.targetedImagineStatus = document.getElementById('targetedImagineStatus');
  
  // Rename Modal
  elements.renameModal = document.getElementById('renameModal');
  elements.renameInput = document.getElementById('renameInput');
  elements.btnRenameCancel = document.getElementById('btnRenameCancel');
  elements.btnRenameConfirm = document.getElementById('btnRenameConfirm');
  
  // Confirm Modal
  elements.confirmModal = document.getElementById('confirmModal');
  elements.confirmTitle = document.getElementById('confirmTitle');
  elements.confirmMessage = document.getElementById('confirmMessage');
  elements.btnConfirmCancel = document.getElementById('btnConfirmCancel');
  elements.btnConfirmOk = document.getElementById('btnConfirmOk');
  
  // Toast
  elements.toast = document.getElementById('toast');
}

function attachEventListeners() {
  // Tabs
  document.querySelectorAll('.gh-tab').forEach(tab => {
    tab.addEventListener('click', () => switchTab(tab.dataset.tab));
  });
  
  // RTC
  if (elements.chkAutoCorrupt) elements.chkAutoCorrupt.addEventListener('change', toggleAutoCorrupt);
  if (elements.blastTypeSelect) elements.blastTypeSelect.addEventListener('change', updateBlastType);
  if (elements.intensity) {
    elements.intensity.addEventListener('input', () => {
      if (elements.intensityValue) elements.intensityValue.textContent = elements.intensity.value;
      // Sync stash intensity with RTC intensity
      if (elements.stashIntensity) {
        elements.stashIntensity.value = elements.intensity.value;
        if (elements.stashIntensityValue) elements.stashIntensityValue.textContent = elements.intensity.value;
      }
    });
    elements.intensity.addEventListener('change', updateIntensity);
  }
  if (elements.btnManualBlast) elements.btnManualBlast.addEventListener('click', rtcManualBlast);
  if (elements.btnLetItRip) elements.btnLetItRip.addEventListener('click', rtcLetItRip);
  if (elements.crashBehavior) elements.crashBehavior.addEventListener('change', updateCrashBehavior);
  
  // Stash intensity (synced with RTC intensity)
  if (elements.stashIntensity) {
    elements.stashIntensity.addEventListener('input', () => {
      if (elements.stashIntensityValue) elements.stashIntensityValue.textContent = elements.stashIntensity.value;
      // Sync RTC intensity with stash intensity
      if (elements.intensity) {
        elements.intensity.value = elements.stashIntensity.value;
        if (elements.intensityValue) elements.intensityValue.textContent = elements.stashIntensity.value;
      }
    });
    elements.stashIntensity.addEventListener('change', updateIntensity);
  }
  
  // Base states
  if (elements.btnAddBase) elements.btnAddBase.addEventListener('click', addBase);
  if (elements.btnLoadBase) elements.btnLoadBase.addEventListener('click', loadBase);
  if (elements.btnDeleteBase) elements.btnDeleteBase.addEventListener('click', deleteBase);
  
  // Stash
  if (elements.chkLoadOnOperation) elements.chkLoadOnOperation.addEventListener('change', toggleLoadOnOperation);
  if (elements.btnBlast) elements.btnBlast.addEventListener('click', blast);
  if (elements.btnReplayStash) elements.btnReplayStash.addEventListener('click', replayStash);
  if (elements.btnKeep) elements.btnKeep.addEventListener('click', promoteToStockpile);
  if (elements.btnClearStash) elements.btnClearStash.addEventListener('click', clearStash);
  
  // Stockpile
  if (elements.btnReplayStock) elements.btnReplayStock.addEventListener('click', replayStockpile);
  if (elements.btnRenameStock) elements.btnRenameStock.addEventListener('click', showRenameModal);
  if (elements.btnDeleteStock) elements.btnDeleteStock.addEventListener('click', deleteStockpile);
  if (elements.btnExport) elements.btnExport.addEventListener('click', exportStockpile);
  if (elements.fileImport) elements.fileImport.addEventListener('change', importStockpile);
  
  // Imagine
  if (elements.btnLoadModel) elements.btnLoadModel.addEventListener('click', imagineLoadModel);
  if (elements.imagineTemperature) {
    elements.imagineTemperature.addEventListener('input', () => {
      // Temperature slider goes from 0-150, convert to 0.0-1.0 for display
      const tempValue = (parseFloat(elements.imagineTemperature.value) / 150.0).toFixed(2);
      if (elements.imagineTemperatureValue) elements.imagineTemperatureValue.textContent = tempValue;
      saveImagineConfig();
    });
  }
  if (elements.btnImagineBug) elements.btnImagineBug.addEventListener('click', imagineAutoBug);
  if (elements.imagineEpoch) elements.imagineEpoch.addEventListener('change', saveImagineConfig);
  if (elements.imagineBytesToPredict) elements.imagineBytesToPredict.addEventListener('change', saveImagineConfig);
  if (elements.imagineTopK) elements.imagineTopK.addEventListener('change', saveImagineConfig);

  // Targeted Imagine
  if (elements.chkTargetedImagine) {
    elements.chkTargetedImagine.addEventListener('change', toggleTargetedImagine);
  }
  if (elements.targetModeSelect) {
    elements.targetModeSelect.addEventListener('change', () => {
      updateTargetMode();
    });
  }
  if (elements.targetScanline) {
    elements.targetScanline.addEventListener('input', () => {
      elements.targetScanlineValue.textContent = elements.targetScanline.value;
      drawFrameMap();
    });
  }
  if (elements.rangeStart) {
    elements.rangeStart.addEventListener('input', () => {
      elements.rangeStartValue.textContent = elements.rangeStart.value;
      updateRangeCount();
      drawFrameMap();
    });
  }
  if (elements.rangeEnd) {
    elements.rangeEnd.addEventListener('input', () => {
      elements.rangeEndValue.textContent = elements.rangeEnd.value;
      updateRangeCount();
      drawFrameMap();
    });
  }
  if (elements.frameMapCanvas) {
    elements.frameMapCanvas.addEventListener('click', onFrameMapClick);
  }
  
  // Rename Modal
  if (elements.btnRenameCancel) elements.btnRenameCancel.addEventListener('click', hideRenameModal);
  if (elements.btnRenameConfirm) elements.btnRenameConfirm.addEventListener('click', confirmRename);
  if (elements.renameModal) {
    elements.renameModal.addEventListener('click', (e) => {
      if (e.target === elements.renameModal) hideRenameModal();
    });
  }
  
  // Confirm Modal
  if (elements.btnConfirmCancel) elements.btnConfirmCancel.addEventListener('click', hideConfirmModal);
  if (elements.btnConfirmOk) elements.btnConfirmOk.addEventListener('click', handleConfirmOk);
  if (elements.confirmModal) {
    elements.confirmModal.addEventListener('click', (e) => {
      if (e.target === elements.confirmModal) hideConfirmModal();
    });
  }
  
  // Keyboard shortcuts
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      hideRenameModal();
      hideConfirmModal();
    }
  });

  window.addEventListener('focus', () => {
    syncImagineUnlockState().catch(error => {
      console.warn('[GH] Failed to refresh Imagine unlock state on focus:', error);
    });
  });

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) return;
    syncImagineUnlockState().catch(error => {
      console.warn('[GH] Failed to refresh Imagine unlock state after visibility change:', error);
    });
  });
}

// ==================== Real-Time Corruptor ====================

async function loadRTCState() {
  console.log('[RTC] Loading RTC state...');

  ensureImagineCrashBehaviorOption(imagineBugUnlocked);
  
  // Load domains
  await refreshDomains();
  
  // Load current settings
  const autoCorruptData = await api.rtc.getAutoCorrupt();
  if (autoCorruptData.success) {
    rtcAutoCorruptEnabled = autoCorruptData.autoCorrupt;
    elements.chkAutoCorrupt.checked = rtcAutoCorruptEnabled;
  }
  
  const intensityData = await api.rtc.getIntensity();
  if (intensityData.success) {
    elements.intensity.value = intensityData.intensity;
    elements.intensityValue.textContent = intensityData.intensity;
  }
  
  const blastTypeData = await api.rtc.getBlastType();
  if (blastTypeData.success) {
    elements.blastTypeSelect.value = blastTypeData.blastType;
  }
  
  const crashData = await api.rtc.getCrashBehavior();
  if (crashData.success) {
    const effectiveCrashBehavior = (crashData.crashBehavior === 'ImagineFix' && !imagineBugUnlocked)
      ? 'IgnoreErrors'
      : crashData.crashBehavior;

    if (effectiveCrashBehavior !== crashData.crashBehavior) {
      await api.rtc.setCrashBehavior(effectiveCrashBehavior);
    }

    console.log('[RTC] Crash behavior from API:', effectiveCrashBehavior);
    if (elements.crashBehavior) {
      elements.crashBehavior.value = effectiveCrashBehavior;
      console.log('[RTC] Crash behavior dropdown value set to:', elements.crashBehavior.value);
    } else {
      console.error('[RTC] Crash behavior dropdown element is null!');
    }
    currentCrashBehavior = effectiveCrashBehavior;
    updateCrashStatus(crashData.crashed, effectiveCrashBehavior);
    startCrashPolling(effectiveCrashBehavior);
  } else {
    console.error('[RTC] Failed to load crash behavior:', crashData.error);
  }
}

async function refreshDomains() {
  console.log('[RTC] Refreshing memory domains...');
  
  const data = await api.rtc.getDomains();
  
  if (!data.success) {
    elements.domainList.innerHTML = '<div class="gh-empty">Failed to load domains</div>';
    return;
  }
  
  const domains = data.domains || [];
  
  if (domains.length === 0) {
    elements.domainList.innerHTML = '<div class="gh-empty">No domains available</div>';
    return;
  }
  
  elements.domainList.innerHTML = domains.map(domain => `
    <div class="gh-domain-item">
      <label>
        <input type="checkbox" 
               data-domain="${escapeHtml(domain.key)}" 
               ${domain.selected ? 'checked' : ''}
               onchange="toggleDomain('${escapeHtml(domain.key)}', this.checked)">
        <span>${escapeHtml(domain.name)} (${formatSize(domain.size)})</span>
      </label>
    </div>
  `).join('');
  
  // Update selected domains list
  selectedDomains = domains.filter(d => d.selected).map(d => d.key);
}

async function toggleDomain(domainKey, selected) {
  console.log('[RTC] Toggling domain:', domainKey, selected);
  
  if (selected) {
    if (!selectedDomains.includes(domainKey)) {
      selectedDomains.push(domainKey);
    }
  } else {
    selectedDomains = selectedDomains.filter(d => d !== domainKey);
  }
  
  const data = await api.rtc.setDomainSelection(selectedDomains);
  
  if (data.success) {
    console.log('[RTC] Domain selection updated');
  } else {
    showToast(data.error || 'Failed to update domain selection', 'error');
  }
}

async function toggleAutoCorrupt() {
  const enabled = elements.chkAutoCorrupt.checked;
  console.log('[RTC] Auto-corrupt toggled:', enabled);
  
  const data = await api.rtc.setAutoCorrupt(enabled);
  
  if (data.success) {
    rtcAutoCorruptEnabled = enabled;
    showToast(`Auto-corrupt ${enabled ? 'enabled' : 'disabled'}`, enabled ? 'success' : 'info');
  } else {
    showToast(data.error || 'Failed to toggle auto-corrupt', 'error');
    elements.chkAutoCorrupt.checked = !enabled;
  }
}

async function updateBlastType() {
  const blastType = elements.blastTypeSelect.value;
  console.log('[RTC] Updating blast type to:', blastType);
  
  const data = await api.rtc.setBlastType(blastType);
  
  if (data.success) {
    console.log('[RTC] Blast type updated');
  } else {
    showToast(data.error || 'Failed to update blast type', 'error');
  }
}

async function updateIntensity() {
  const intensity = parseInt(elements.intensity.value);
  console.log('[RTC] Updating intensity to:', intensity);
  
  const data = await api.rtc.setIntensity(intensity);
  
  if (data.success) {
    console.log('[RTC] Intensity updated');
  } else {
    showToast(data.error || 'Failed to update intensity', 'error');
  }
}

async function rtcManualBlast() {
  console.log('[RTC] Manual blast triggered');
  
  const data = await api.rtc.blast();
  
  if (data.success) {
    showToast(`Blast executed (${data.writesApplied || 1} writes)`, 'success');
  } else {
    showToast(data.error || 'Manual blast failed', 'error');
  }
}

async function rtcLetItRip() {
  console.log('[RTC] Let It Rip activated!');
  
  const data = await api.rtc.letItRip();
  
  if (data.success) {
    showToast('Let It Rip! 🔥', 'success');
    
    // Update UI to reflect the changes
    elements.intensity.value = data.intensity;
    elements.intensityValue.textContent = data.intensity;
    elements.chkAutoCorrupt.checked = data.autoCorrupt;
    rtcAutoCorruptEnabled = data.autoCorrupt;
    
    // Refresh domains to show selection
    await refreshDomains();
  } else {
    showToast(data.error || 'Failed to apply Let It Rip', 'error');
  }
}

async function updateCrashBehavior() {
  const behavior = elements.crashBehavior.value;
  console.log('[RTC] Updating crash behavior to:', behavior);
  
  const data = await api.rtc.setCrashBehavior(behavior);
  
  if (data.success) {
    console.log('[RTC] Crash behavior updated');
    // Update the crash status display with the new behavior
    const crashData = await api.rtc.getCrashBehavior();
    if (crashData.success) {
      currentCrashBehavior = crashData.crashBehavior;
      updateCrashStatus(crashData.crashed, crashData.crashBehavior);
      startCrashPolling(crashData.crashBehavior);
    }
  } else {
    showToast(data.error || 'Failed to update crash behavior', 'error');
  }
}

function updateCrashStatus(crashed, behavior) {
  if (!behavior || behavior === 'RedScreen') {
    // Only show crashed/running status for RedScreen mode
    if (crashed) {
      elements.crashStatus.textContent = '⚠️ CRASHED';
      elements.crashStatus.className = 'gh-crash-status crashed';
    } else {
      elements.crashStatus.textContent = '✓ Running';
      elements.crashStatus.className = 'gh-crash-status running';
    }
  } else if (behavior === 'IgnoreErrors') {
    // Show gray idle state for IgnoreErrors
    elements.crashStatus.textContent = '⚙️ Ignoring Errors';
    elements.crashStatus.className = 'gh-crash-status ignore-errors';
  } else if (behavior === 'ImagineFix') {
    // Show purple idle state for ImagineFix
    elements.crashStatus.textContent = '🔮 Imagine Fix';
    elements.crashStatus.className = 'gh-crash-status imagine-fix';
  }
}

function startCrashPolling(behavior) {
  // Stop any existing polling
  stopCrashPolling();
  
  // Only poll for RedScreen mode
  if (behavior === 'RedScreen') {
    console.log('[RTC] Starting crash polling for RedScreen mode');
    crashPollingInterval = setInterval(async () => {
      const crashData = await api.rtc.getCrashBehavior();
      if (crashData.success && crashData.crashBehavior === currentCrashBehavior) {
        updateCrashStatus(crashData.crashed, crashData.crashBehavior);
      }
    }, 500); // Poll every 500ms
  } else {
    console.log('[RTC] Crash polling disabled for', behavior, 'mode');
  }
}

function stopCrashPolling() {
  if (crashPollingInterval) {
    console.log('[RTC] Stopping crash polling');
    clearInterval(crashPollingInterval);
    crashPollingInterval = null;
  }
}

function formatSize(bytes) {
  if (bytes < 1024) return `${bytes}B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)}KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)}MB`;
}

// ==================== Tab Switching ====================

function switchTab(tabName) {
  if (tabName === 'imagine' && !imagineBugUnlocked) {
    tabName = 'rtc';
  }

  console.log('[GH] Switching to tab:', tabName);
  
  // Update tab buttons
  document.querySelectorAll('.gh-tab').forEach(tab => {
    tab.classList.toggle('active', tab.dataset.tab === tabName);
  });
  
  // Update sections
  document.querySelectorAll('.gh-section').forEach(section => {
    section.classList.toggle('active', section.dataset.section === tabName);
  });
}

// ==================== Base States ====================

async function refreshBases(autoScroll = false) {
  console.log('[GH] Refreshing base states...');
  const data = await api.gh.getBaseStates();
  console.log('[GH] Base states API response:', data);
  
  if (!data.success) {
    console.error('[GH] Failed to load base states');
    elements.baseList.innerHTML = '<div class="gh-empty">Failed to load base states</div>';
    return;
  }
  
  const bases = data.baseStates || [];
  console.log('[GH] Number of base states:', bases.length);
  
  if (bases.length === 0) {
    console.log('[GH] No base states found');
    elements.baseList.innerHTML = '<div class="gh-empty">No base states yet. Add one to begin.</div>';
    updateBaseButtons();
    return;
  }
  
  console.log('[GH] Processing base states:', bases);
  elements.baseList.innerHTML = bases.map((base, index) => {
    console.log(`[GH] Base ${index}:`, base);
    console.log(`[GH] Base ${index} keys:`, Object.keys(base));
    console.log(`[GH] Base ${index} full object:`, JSON.stringify(base, null, 2));
    console.log(`[GH]   id: ${base.id}`);
    console.log(`[GH]   name: ${base.name}`);
    console.log(`[GH]   created: ${base.created}`);
    
    // Parse date safely
    let dateStr = 'Unknown date';
    if (base.created) {
      const date = new Date(base.created);
      if (!isNaN(date.getTime())) {
        dateStr = date.toLocaleString();
      } else {
        console.warn(`[GH] Invalid date for base ${base.name}:`, base.created);
        dateStr = base.created;
      }
    } else {
      console.warn(`[GH] No created field for base ${base.name}`);
    }
    
    return `
    <div class="gh-item ${base.id === selectedBaseId ? 'selected' : ''}" 
         data-id="${base.id}"
         onclick="selectBase('${base.id}')">
      <div class="gh-item-info">
        <div class="gh-item-name">${escapeHtml(base.name)}</div>
        <div class="gh-item-meta">${dateStr}</div>
      </div>
    </div>
  `;
  }).join('');
  
  console.log('[GH] Base list rendered');
  updateBaseButtons();
  
  // Enable blast button if a base is selected
  elements.btnBlast.disabled = selectedBaseId === null;
  console.log('[GH] Blast button enabled:', selectedBaseId !== null);
  
  // Auto-scroll to bottom if requested (when new item is added)
  if (autoScroll && bases.length > 0) {
    elements.baseList.scrollTop = elements.baseList.scrollHeight;
  }
}

function selectBase(id) {
  console.log('[GH] selectBase called with ID:', id);
  
  if (!id || id === 'undefined') {
    console.error('[GH] selectBase called with invalid ID:', id);
    return;
  }
  
  selectedBaseId = id;
  console.log('[GH] selectedBaseId set to:', selectedBaseId);
  
  // Update UI immediately
  document.querySelectorAll('#baseList .gh-item').forEach(item => {
    item.classList.toggle('selected', item.dataset.id === id);
  });
  
  updateBaseButtons();
  
  // Note: Not calling select API since we pass ID directly to load-base
  // The select endpoint may not be implemented on the server
  elements.btnBlast.disabled = false;
  console.log('[GH] Base state selected (ready to blast or load)');
}

function updateBaseButtons() {
  const hasSelection = selectedBaseId !== null;
  elements.btnLoadBase.disabled = !hasSelection;
  elements.btnDeleteBase.disabled = !hasSelection;
}

async function addBase() {
  const name = elements.baseNameInput.value.trim();
  console.log('[GH] addBase called with name:', name);
  
  if (!name) {
    console.warn('[GH] No name provided for base state');
    showToast('Please enter a name for the base state', 'error');
    return;
  }
  
  console.log('[GH] Creating base state...');
  const data = await api.gh.addBaseState(name);
  
  console.log('[GH] Create base state response:', data);
  console.log('[GH] Response keys:', Object.keys(data));
  console.log('[GH] Response full JSON:', JSON.stringify(data, null, 2));
  
  if (data.success) {
    // Try different possible property names
    const baseState = data.base || data.baseState || data.state || data.result;
    console.log('[GH] Base state created successfully:', baseState);
    console.log('[GH] baseState keys:', baseState ? Object.keys(baseState) : 'baseState is undefined');
    console.log('[GH] baseState full JSON:', JSON.stringify(baseState, null, 2));
    showToast(`Base state "${name}" created`, 'success');
    elements.baseNameInput.value = '';
    selectedBaseId = baseState?.id;
    console.log('[GH] Selected base ID set to:', selectedBaseId);
    await refreshBases(true); // Auto-scroll to bottom when new item is added
  } else {
    console.error('[GH] Failed to create base state:', data.error);
    showToast(data.error || 'Failed to create base state', 'error');
  }
}

async function loadBase() {
  console.log('[GH] loadBase called, selectedBaseId:', selectedBaseId);
  
  if (!selectedBaseId) {
    console.warn('[GH] No base state selected');
    showToast('Please select a base state first', 'error');
    return;
  }
  
  console.log('[GH] Loading base state with ID:', selectedBaseId);
  const data = await api.gh.loadBase(selectedBaseId);
  
  console.log('[GH] Load base response:', data);
  
  if (data.success) {
    showToast(`Base state loaded: ${selectedBaseId.substring(0, 8)}...`, 'success');
    console.log('[GH] Base state loaded successfully');
  } else {
    console.error('[GH] Failed to load base state:', data.error);
    showToast(data.error || 'Failed to load base state', 'error');
  }
}

async function deleteBase() {
  if (!selectedBaseId) return;
  
  showConfirm(
    'Delete Base State',
    'Are you sure you want to delete this base state?',
    async () => {
      console.log('[GH] Deleting base state:', selectedBaseId);
      const data = await api.gh.deleteBaseState(selectedBaseId);
      
      console.log('[GH] Delete base response:', data);
      
      if (data.success) {
        showToast('Base state deleted', 'success');
        console.log('[GH] Base state deleted successfully');
        selectedBaseId = null;
        await refreshBases();
      } else {
        console.error('[GH] Failed to delete base state:', data.error);
        showToast(data.error || 'Failed to delete base state', 'error');
      }
    }
  );
}

// ==================== Stash ====================

async function refreshStash(autoScroll = false) {
  const data = await api.gh.getStash();
  
  if (!data.success) {
    elements.stashList.innerHTML = '<div class="gh-empty">Failed to load stash</div>';
    return;
  }
  
  const stash = data.stash || [];
  
  if (stash.length === 0) {
    elements.stashList.innerHTML = '<div class="gh-empty">Corruptions appear here when you blast.</div>';
    updateStashButtons();
    return;
  }
  
  elements.stashList.innerHTML = stash.map(entry => `
    <div class="gh-item ${entry.id === selectedStashId ? 'selected' : ''}" 
         data-id="${entry.id}"
         onclick="selectStash('${entry.id}')">
      <div class="gh-item-info">
        <div class="gh-item-name">${escapeHtml(entry.name)}</div>
        <div class="gh-item-meta">${entry.writes?.length || 0} writes • ${new Date(entry.created).toLocaleString()}</div>
      </div>
    </div>
  `).join('');
  
  updateStashButtons();
  
  // Auto-scroll to bottom if requested (when new item is added)
  if (autoScroll && stash.length > 0) {
    elements.stashList.scrollTop = elements.stashList.scrollHeight;
  }
}

async function selectStash(id) {
  if (!id || id === 'undefined') {
    console.error('selectStash called with invalid ID:', id);
    return;
  }
  
  selectedStashId = id;
  
  // Update UI immediately
  document.querySelectorAll('#stashList .gh-item').forEach(item => {
    item.classList.toggle('selected', item.dataset.id === id);
  });
  
  updateStashButtons();

  if (elements.chkLoadOnClick && elements.chkLoadOnClick.checked) {
    await replayStash();
  }
}

function updateStashButtons() {
  const hasSelection = selectedStashId !== null;
  const hasAny = document.querySelectorAll('#stashList .gh-item').length > 0;
  
  elements.btnReplayStash.disabled = !hasSelection;
  elements.btnKeep.disabled = !hasSelection;
  elements.btnClearStash.disabled = !hasAny;
}

async function toggleLoadOnOperation() {
  const enabled = elements.chkLoadOnOperation.checked;
  
  const data = await api.gh.setLoadOnOperation(enabled);
  
  if (data.success) {
    showToast(`Load on operation ${enabled ? 'enabled' : 'disabled'}`);
  }
}

async function blast() {
  console.log('[GH] Blast called - creating corruption');
  console.log('[GH] Using base state ID:', selectedBaseId);
  
  if (!selectedBaseId) {
    console.error('[GH] No base state selected for blast');
    showToast('Please select a base state first', 'error');
    return;
  }
  
  const data = await api.gh.corruptAndStash(selectedBaseId);
  
  console.log('[GH] Blast response:', data);
  
  if (data.success) {
    console.log('[GH] Corruption created:', data.entry);
    showToast('Corruption created and added to stash', 'success');
    selectedStashId = data.entry?.id;
    console.log('[GH] Selected stash ID set to:', selectedStashId);
    await refreshStash(true); // Auto-scroll to bottom when new item is added
  } else {
    console.error('[GH] Blast failed:', data.error);
    showToast(data.error || 'Failed to create corruption', 'error');
  }
}

async function replayStash() {
  if (!selectedStashId) return;
  
  const data = await api.gh.replayStash(selectedStashId);
  
  if (data.success) {
    showToast('Stash entry replayed', 'success');
  } else {
    showToast(data.error || 'Failed to replay stash entry', 'error');
  }
}

async function promoteToStockpile() {
  if (!selectedStashId) return;
  
  const data = await api.gh.promoteStash(selectedStashId);
  
  if (data.success) {
    showToast('Entry promoted to stockpile', 'success');
    selectedStockpileId = data.entry?.Id;
    selectedStashId = null;
    await refreshStash();
    await refreshStockpile(true); // Auto-scroll to bottom when new item is added
  } else {
    showToast(data.error || 'Failed to promote entry', 'error');
  }
}

async function clearStash() {
  showConfirm(
    'Clear Stash',
    'Clear all stash entries? This cannot be undone.',
    async () => {
      const data = await api.gh.clearStash();
      
      if (data.success) {
        showToast('Stash cleared', 'success');
        selectedStashId = null;
        await refreshStash();
      } else {
        showToast(data.error || 'Failed to clear stash', 'error');
      }
    }
  );
}

// ==================== Stockpile ====================

async function refreshStockpile(autoScroll = false) {
  const data = await api.gh.getStockpile();
  
  if (!data.success) {
    elements.stockpileList.innerHTML = '<div class="gh-empty">Failed to load stockpile</div>';
    return;
  }
  
  const stockpile = data.stockpile || [];
  
  if (stockpile.length === 0) {
    elements.stockpileList.innerHTML = '<div class="gh-empty">Promoted entries are saved here permanently.</div>';
    updateStockpileButtons();
    return;
  }
  
  elements.stockpileList.innerHTML = stockpile.map(entry => `
    <div class="gh-item ${entry.id === selectedStockpileId ? 'selected' : ''}" 
         data-id="${entry.id}"
         onclick="selectStockpile('${entry.id}')">
      <div class="gh-item-info">
        <div class="gh-item-name">${escapeHtml(entry.name)}</div>
        <div class="gh-item-meta">${entry.writes?.length || 0} writes • ${new Date(entry.created).toLocaleString()}</div>
      </div>
    </div>
  `).join('');
  
  updateStockpileButtons();
  
  // Auto-scroll to bottom if requested (when new item is added)
  if (autoScroll && stockpile.length > 0) {
    elements.stockpileList.scrollTop = elements.stockpileList.scrollHeight;
  }
}

function selectStockpile(id) {
  if (!id || id === 'undefined') {
    console.error('selectStockpile called with invalid ID:', id);
    return;
  }
  
  selectedStockpileId = id;
  
  // Update UI immediately
  document.querySelectorAll('#stockpileList .gh-item').forEach(item => {
    item.classList.toggle('selected', item.dataset.id === id);
  });
  
  updateStockpileButtons();
}

function updateStockpileButtons() {
  const hasSelection = selectedStockpileId !== null;
  const hasAny = document.querySelectorAll('#stockpileList .gh-item').length > 0;
  
  elements.btnReplayStock.disabled = !hasSelection;
  elements.btnRenameStock.disabled = !hasSelection;
  elements.btnDeleteStock.disabled = !hasSelection;
  elements.btnExport.disabled = !hasAny;
}

async function replayStockpile() {
  if (!selectedStockpileId) return;
  
  const data = await api.gh.replayStockpile(selectedStockpileId);
  
  if (data.success) {
    showToast('Stockpile entry replayed', 'success');
  } else {
    showToast(data.error || 'Failed to replay stockpile entry', 'error');
  }
}

function showRenameModal() {
  if (!selectedStockpileId) return;
  
  currentRenameId = selectedStockpileId;
  
  // Get current name
  const selectedItem = document.querySelector(`#stockpileList .gh-item[data-id="${selectedStockpileId}"]`);
  const currentName = selectedItem?.querySelector('.gh-item-name')?.textContent || '';
  
  elements.renameInput.value = currentName;
  elements.renameModal.style.display = 'flex';
  elements.renameInput.focus();
  elements.renameInput.select();
}

function hideRenameModal() {
  elements.renameModal.style.display = 'none';
  currentRenameId = null;
}

async function confirmRename() {
  if (!currentRenameId) return;
  
  const newName = elements.renameInput.value.trim();
  
  if (!newName) {
    showToast('Please enter a name', 'error');
    return;
  }
  
  const data = await api.gh.renameStockpile(currentRenameId, newName);
  
  if (data.success) {
    showToast('Entry renamed', 'success');
    hideRenameModal();
    await refreshStockpile();
  } else {
    showToast(data.error || 'Failed to rename entry', 'error');
  }
}

async function deleteStockpile() {
  if (!selectedStockpileId) return;
  
  showConfirm(
    'Delete Stockpile Entry',
    'Are you sure you want to delete this stockpile entry?',
    async () => {
      console.log('[GH] Deleting stockpile entry:', selectedStockpileId);
      const data = await api.gh.deleteStockpile(selectedStockpileId);
      
      console.log('[GH] Delete stockpile response:', data);
      
      if (data.success) {
        showToast('Stockpile entry deleted', 'success');
        console.log('[GH] Stockpile entry deleted successfully');
        selectedStockpileId = null;
        await refreshStockpile();
      } else {
        console.error('[GH] Failed to delete stockpile entry:', data.error);
        showToast(data.error || 'Failed to delete stockpile entry', 'error');
      }
    }
  );
}

async function exportStockpile() {
  const data = await api.gh.exportStockpile();
  
  if (data.success && data.json) {
    // Create download
    const blob = new Blob([data.json], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `stockpile_${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
    
    showToast('Stockpile exported', 'success');
  } else {
    showToast(data.error || 'Failed to export stockpile', 'error');
  }
}

async function importStockpile(event) {
  const file = event.target.files[0];
  if (!file) return;
  
  try {
    const text = await file.text();
    
    const data = await api.gh.importStockpile(text);
    
    if (data.success) {
      showToast(`Imported ${data.imported || 0} entries`, 'success');
      await refreshStockpile(true); // Auto-scroll to bottom when items are imported
    } else {
      showToast(data.error || 'Failed to import stockpile', 'error');
    }
  } catch (error) {
    showToast(`Import error: ${error.message}`, 'error');
  }
  
  // Reset file input
  event.target.value = '';
}

// ==================== Refresh All ====================

async function refreshAll() {
  await Promise.all([
    refreshBases(),
    refreshStash(),
    refreshStockpile()
  ]);
}

// ==================== Utilities ====================

function showToast(message, type = 'info') {
  elements.toast.textContent = message;
  elements.toast.className = 'gh-toast show';
  
  if (type === 'error') {
    elements.toast.classList.add('error');
  } else if (type === 'success') {
    elements.toast.classList.add('success');
  }
  
  setTimeout(() => {
    elements.toast.classList.remove('show');
  }, 3000);
}

function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text;
  return div.innerHTML;
}

// ==================== Confirmation Modal ====================

function showConfirm(title, message, onConfirm) {
  console.log('[GH] Showing confirmation modal:', title, message);
  elements.confirmTitle.textContent = title;
  elements.confirmMessage.textContent = message;
  confirmCallback = onConfirm;
  elements.confirmModal.style.display = 'flex';
}

function hideConfirmModal() {
  console.log('[GH] Hiding confirmation modal');
  elements.confirmModal.style.display = 'none';
  confirmCallback = null;
}

function handleConfirmOk() {
  console.log('[GH] Confirmation accepted');
  const callback = confirmCallback;
  hideConfirmModal();
  if (callback) {
    callback();
  }
}

// Expose functions that are called from inline HTML
window.selectBase = selectBase;
window.selectStash = selectStash;
window.selectStockpile = selectStockpile;
window.toggleDomain = toggleDomain;

// ==================== Imagine (ML Byte Prediction) ====================

const IMAGINE_FLAVOR_TEXTS = [
  "imagine training a model of 6502 code and then forcing the nes cpu to run predicted bytes for some reason",
  "what if we trained an AI on 6502 assembly and made the NES execute whatever it predicts",
  "picture this: a neural net learns 6502 opcodes, then we just... run its predictions on real hardware",
  "so we teach a model what 6502 looks like and convince the CPU to execute its hallucinations",
  "train a machine learning model on processor instructions then force those predicted bytes into the NES for reasons",
  "imagine spending epochs teaching AI about 6502 just to inject its predictions into a running console",
  "what happens when you train a model on assembly code and pipe its output straight into the CPU",
  "we basically taught an AI to dream in 6502 and now we're making the NES live that dream",
  "train neural network on old game code → force predicted bytes into processor → chaos",
  "an AI learned 6502 patterns and now we're feeding its imagination directly to the CPU because why not"
];

const IMAGINE_CONFIG_KEY = 'glitchHarvester.imagineConfig';

function getRandomFlavorText() {
  return IMAGINE_FLAVOR_TEXTS[Math.floor(Math.random() * IMAGINE_FLAVOR_TEXTS.length)];
}

function getImagineConfigFromUI() {
  return {
    epoch: elements.imagineEpoch ? parseInt(elements.imagineEpoch.value) : null,
    bytesToPredict: elements.imagineBytesToPredict ? parseInt(elements.imagineBytesToPredict.value) : null,
    temperatureUi: elements.imagineTemperature ? parseInt(elements.imagineTemperature.value) : null,
    topK: elements.imagineTopK ? parseInt(elements.imagineTopK.value) : null
  };
}

function saveImagineConfig() {
  try {
    const config = getImagineConfigFromUI();
    localStorage.setItem(IMAGINE_CONFIG_KEY, JSON.stringify(config));
  } catch (error) {
    console.warn('[Imagine] Failed to save config:', error);
  }
}

function loadImagineConfig() {
  try {
    const raw = localStorage.getItem(IMAGINE_CONFIG_KEY);
    if (!raw) return null;
    return JSON.parse(raw);
  } catch (error) {
    console.warn('[Imagine] Failed to load config:', error);
    return null;
  }
}

function applyImagineConfig(config) {
  if (!config) return;
  if (Number.isFinite(config.epoch) && elements.imagineEpoch) {
    elements.imagineEpoch.value = config.epoch;
  }
  if (Number.isFinite(config.bytesToPredict) && elements.imagineBytesToPredict) {
    elements.imagineBytesToPredict.value = config.bytesToPredict;
  }
  if (Number.isFinite(config.temperatureUi) && elements.imagineTemperature) {
    elements.imagineTemperature.value = config.temperatureUi;
    if (elements.imagineTemperatureValue) {
      elements.imagineTemperatureValue.textContent = (config.temperatureUi / 150.0).toFixed(2);
    }
  }
  if (Number.isFinite(config.topK) && elements.imagineTopK) {
    elements.imagineTopK.value = config.topK;
  }
}

async function ensureImagineModelLoaded() {
  const modelState = await api.imagine.isModelLoaded();
  if (modelState.success && modelState.modelLoaded) return true;

  const saved = loadImagineConfig();
  const epoch = Number.isFinite(saved?.epoch) ? saved.epoch : parseInt(elements.imagineEpoch?.value ?? '0');
  if (!Number.isFinite(epoch) || epoch <= 0) {
    return false;
  }

  elements.imagineStatus.textContent = `Status: Loading model epoch ${epoch}...`;
  elements.imagineStatus.style.color = 'var(--yellow)';
  elements.btnLoadModel.disabled = true;

  try {
    const data = await api.imagine.loadModel(epoch);
    if (data.success) {
      elements.imagineStatus.textContent = `Status: Model epoch ${epoch} loaded`;
      elements.imagineStatus.style.color = 'var(--green)';
      showToast(`Model epoch ${epoch} loaded successfully`, 'success');
      await imagineUpdateParams();
      return true;
    }
    elements.imagineStatus.textContent = `Status: Failed to load model - ${data.error || 'Unknown error'}`;
    elements.imagineStatus.style.color = 'var(--red)';
    showToast(data.error || 'Failed to load model', 'error');
    return false;
  } catch (error) {
    elements.imagineStatus.textContent = `Status: Error - ${error.message}`;
    elements.imagineStatus.style.color = 'var(--red)';
    showToast(`Error loading model: ${error.message}`, 'error');
    return false;
  } finally {
    elements.btnLoadModel.disabled = false;
  }
}

async function imagineLoadModel() {
  console.log('[Imagine] Load Model clicked');
  const epoch = parseInt(elements.imagineEpoch.value);

  saveImagineConfig();
  
  elements.imagineStatus.textContent = `Status: Loading model epoch ${epoch}...`;
  elements.imagineStatus.style.color = 'var(--yellow)';
  elements.btnLoadModel.disabled = true;
  
  try {
    const data = await api.imagine.loadModel(epoch);
    
    if (data.success) {
      elements.imagineStatus.textContent = `Status: Model epoch ${epoch} loaded`;
      elements.imagineStatus.style.color = 'var(--green)';
      showToast(`Model epoch ${epoch} loaded successfully`, 'success');
      console.log('[Imagine] Model loaded successfully:', data);
      
      // Update generation params after model load
      await imagineUpdateParams();
    } else {
      elements.imagineStatus.textContent = `Status: Failed to load model - ${data.error || 'Unknown error'}`;
      elements.imagineStatus.style.color = 'var(--red)';
      showToast(data.error || 'Failed to load model', 'error');
      console.error('[Imagine] Model load failed:', data.error);
    }
  } catch (error) {
    elements.imagineStatus.textContent = `Status: Error - ${error.message}`;
    elements.imagineStatus.style.color = 'var(--red)';
    showToast(`Error loading model: ${error.message}`, 'error');
    console.error('[Imagine] Model load error:', error);
  } finally {
    elements.btnLoadModel.disabled = false;
  }
}

async function imagineUpdateParams() {
  console.log('[Imagine] Updating generation parameters');
  
  const bytesToGenerate = parseInt(elements.imagineBytesToPredict.value);
  // Temperature is stored as 0-150 in UI, but API expects 0.0-1.0
  // So we divide by 150 to normalize
  const temperatureRaw = parseInt(elements.imagineTemperature.value);
  const temperature = temperatureRaw / 150.0;
  const topK = parseInt(elements.imagineTopK.value);

  saveImagineConfig();
  
  try {
    const data = await api.imagine.setGenerationParams({
      bytesToGenerate,
      temperature,
      topK
    });
    
    if (data.success) {
      console.log('[Imagine] Generation params updated:', {
        bytesToGenerate,
        temperature: temperature.toFixed(3),
        topK
      });
    } else {
      console.error('[Imagine] Failed to update generation params:', data.error);
    }
  } catch (error) {
    console.error('[Imagine] Error updating generation params:', error);
  }
}

async function imagineAutoBug() {
  console.log('[Imagine] Imagine a Bug clicked');
  
  elements.imagineStatus.textContent = 'Status: Imagining a bug...';
  elements.imagineStatus.style.color = 'var(--yellow)';
  elements.btnImagineBug.disabled = true;
  
  try {
    const modelReady = await ensureImagineModelLoaded();
    if (!modelReady) {
      elements.imagineStatus.textContent = 'Status: No model loaded - set a valid epoch';
      elements.imagineStatus.style.color = 'var(--red)';
      showToast('No model loaded - set a valid epoch', 'error');
      return;
    }

    // First, update generation parameters from UI
    await imagineUpdateParams();
    
    // Check if targeted mode is enabled
    const isTargeted = elements.chkTargetedImagine && elements.chkTargetedImagine.checked;
    const loadOnImagine = elements.chkLoadOnImagine && elements.chkLoadOnImagine.checked;
    
    let data;
    if (isTargeted) {
      // Use targeted imagine with scanline config
      const config = getTargetConfig();
      data = await api.imagine.imagineTargetedBug(config, loadOnImagine);
    } else {
      // Use normal inter-frame imagine
      data = await api.imagine.imagineABug(loadOnImagine);
    }
    
    if (data.success) {
      // Format predicted bytes for display
      const bytesArray = Array.isArray(data.predictedBytes) 
        ? data.predictedBytes 
        : (data.predictedBytes ? Object.values(data.predictedBytes) : []);
      
      const bytesHex = bytesArray.map(b => {
        const hex = b.toString(16).toUpperCase().padStart(2, '0');
        return `0x${hex}`;
      }).join(' ');
      
      elements.imagineStatus.textContent = `Status: Bug imagined! [${bytesHex}]`;
      elements.imagineStatus.style.color = 'var(--green)';
      showToast(`Bug imagined: ${bytesArray.length} bytes predicted`, 'success');
      console.log('[Imagine] Bug imagined successfully:', data);
      console.log('[Imagine] Predicted bytes:', bytesHex);
    } else {
      elements.imagineStatus.textContent = `Status: Failed - ${data.error || 'Unknown error'}`;
      elements.imagineStatus.style.color = 'var(--red)';
      showToast(data.error || 'Failed to imagine a bug', 'error');
      console.error('[Imagine] Imagine a bug failed:', data.error);
    }
  } catch (error) {
    elements.imagineStatus.textContent = `Status: Error - ${error.message}`;
    elements.imagineStatus.style.color = 'var(--red)';
    showToast(`Error: ${error.message}`, 'error');
    console.error('[Imagine] Imagine a bug error:', error);
  } finally {
    try { await refreshTargetedStatus(); } catch { }
    elements.btnImagineBug.disabled = false;
  }
}

// ==================== Targeted Imagine ====================

let targetedUpdateTimer = null;

async function toggleTargetedImagine() {
  const enabled = elements.chkTargetedImagine.checked;
  console.log('[Imagine] Targeted mode UI:', enabled);

  if (elements.targetModeRow) {
    elements.targetModeRow.style.display = enabled ? 'flex' : 'none';
  }
  
  if (elements.targetedImagineStatus) {
    elements.targetedImagineStatus.style.display = enabled ? 'block' : 'none';
  }

  if (enabled) {
    updateTargetMode();
    updateRangeCount();
    drawFrameMap();
  } else {
    if (elements.singleScanlineSelector) elements.singleScanlineSelector.style.display = 'none';
    if (elements.scanlineRangeSelector) elements.scanlineRangeSelector.style.display = 'none';
  }

  try {
    await api.imagine.setTargetedMode(enabled, enabled ? getTargetConfig() : null);
  } catch (error) {
    console.error('[Imagine] Failed to toggle targeted mode:', error);
  }
}

function updateTargetMode() {
  if (!elements.targetModeSelect) return;

  if (!elements.chkTargetedImagine || !elements.chkTargetedImagine.checked) {
    if (elements.singleScanlineSelector) elements.singleScanlineSelector.style.display = 'none';
    if (elements.scanlineRangeSelector) elements.scanlineRangeSelector.style.display = 'none';
    return;
  }
  
  const mode = elements.targetModeSelect.value;
  console.log('[Imagine] Target mode:', mode);

  if (elements.singleScanlineSelector) {
    elements.singleScanlineSelector.style.display = mode === 'SingleScanline' ? 'block' : 'none';
  }
  if (elements.scanlineRangeSelector) {
    elements.scanlineRangeSelector.style.display = mode === 'ScanlineRange' ? 'block' : 'none';
  }

  if (!elements.rangeStart || !elements.rangeEnd || !elements.rangeCount) return;

  drawFrameMap();
}

function updateRangeCount() {
  const start = parseInt(elements.rangeStart.value);
  const end = parseInt(elements.rangeEnd.value);
  const count = Math.abs(end - start) + 1;
  elements.rangeCount.textContent = count;
}

function drawFrameMap() {
  const canvas = elements.frameMapCanvas;
  if (!canvas) return;

  const ctx = canvas.getContext('2d');
  const width = canvas.width;
  const height = canvas.height;

  ctx.fillStyle = '#000';
  ctx.fillRect(0, 0, width, height);

  const lineHeight = height / 262;

  for (let i = 0; i < 262; i++) {
    const y = i * lineHeight;

    if (i <= 239) {
      ctx.fillStyle = '#4CAF50';
    } else if (i === 240) {
      ctx.fillStyle = '#FF9800';
    } else {
      ctx.fillStyle = '#2196F3';
    }

    ctx.fillRect(0, y, width, lineHeight);
  }

  ctx.fillStyle = 'rgba(255, 0, 255, 0.5)';

  const mode = elements.targetModeSelect.value;

  if (mode === 'SingleScanline') {
    const line = parseInt(elements.targetScanline.value);
    const y = line * lineHeight;
    ctx.fillRect(0, y, width, lineHeight * 2);
  } else if (mode === 'ScanlineRange') {
    const start = parseInt(elements.rangeStart.value);
    const end = parseInt(elements.rangeEnd.value);
    const y1 = Math.min(start, end) * lineHeight;
    const y2 = Math.max(start, end) * lineHeight;
    ctx.fillRect(0, y1, width, (y2 - y1) + lineHeight);
  } else if (mode === 'ActiveRender') {
    ctx.fillRect(0, 0, width, 240 * lineHeight);
  } else if (mode === 'VBlankPeriod') {
    ctx.fillRect(0, 241 * lineHeight, width, 21 * lineHeight);
  } else if (mode === 'FullFrame') {
    ctx.fillRect(0, 0, width, height);
  }

  ctx.fillStyle = '#fff';
  ctx.font = '10px monospace';
  ctx.fillText('0', 5, 10);
  ctx.fillText('120', 5, 120 * lineHeight + 10);
  ctx.fillText('239', 5, 239 * lineHeight + 10);
  ctx.fillText('240', 5, 240 * lineHeight + 10);
  ctx.fillText('241', 5, 241 * lineHeight + 10);
  ctx.fillText('261', 5, 261 * lineHeight + 10);
}

function onFrameMapClick(event) {
  const canvas = elements.frameMapCanvas;
  const rect = canvas.getBoundingClientRect();
  const y = event.clientY - rect.top;
  const scanline = Math.floor((y / rect.height) * 262);

  console.log('[Imagine] Frame map clicked at scanline', scanline);

  elements.targetScanline.value = scanline;
  elements.targetScanlineValue.textContent = scanline;
  elements.targetModeSelect.value = 'SingleScanline';
  updateTargetMode();
}

function getTargetConfig() {
  const mode = elements.targetModeSelect.value;
  return {
    mode: mode,
    targetScanline: parseInt(elements.targetScanline.value),
    rangeStart: parseInt(elements.rangeStart.value),
    rangeEnd: parseInt(elements.rangeEnd.value),
    enabled: elements.chkTargetedImagine.checked
  };
}

function scheduleTargetedUpdate() {
  if (!elements.chkTargetedImagine.checked) return;
  if (targetedUpdateTimer) clearTimeout(targetedUpdateTimer);
  targetedUpdateTimer = setTimeout(() => {
    try {
      updateTargetMode();
      updateRangeCount();
      drawFrameMap();
    } catch (error) {
      console.error('[Imagine] Failed to update targeted UI:', error);
    }
  }, 150);
}

async function refreshTargetedStatus() {
  try {
    const data = await api.imagine.getTargetedStatus();
    applyTargetedStatus(data);
  } catch (error) {
    console.error('[Imagine] Failed to load targeted status:', error);
  }
}

function applyTargetedStatus(data) {
  if (!data || !data.success) return;

  const enabled = (data.enabled !== undefined)
    ? !!data.enabled
    : (data.config?.enabled !== undefined)
      ? !!data.config.enabled
      : !!(elements.chkTargetedImagine && elements.chkTargetedImagine.checked);

  if (elements.chkTargetedImagine) elements.chkTargetedImagine.checked = enabled;
  if (elements.targetModeRow) elements.targetModeRow.style.display = enabled ? 'flex' : 'none';
  if (elements.targetedImagineStatus) elements.targetedImagineStatus.style.display = enabled ? 'block' : 'none';

  if (data.config) {
    if (data.config.mode && elements.targetModeSelect) elements.targetModeSelect.value = data.config.mode;
    if (Number.isFinite(data.config.targetScanline)) {
      if (elements.targetScanline) elements.targetScanline.value = data.config.targetScanline;
      if (elements.targetScanlineValue) elements.targetScanlineValue.textContent = data.config.targetScanline;
    }
    if (Number.isFinite(data.config.rangeStart)) {
      if (elements.rangeStart) elements.rangeStart.value = data.config.rangeStart;
      if (elements.rangeStartValue) elements.rangeStartValue.textContent = data.config.rangeStart;
    }
    if (Number.isFinite(data.config.rangeEnd)) {
      if (elements.rangeEnd) elements.rangeEnd.value = data.config.rangeEnd;
      if (elements.rangeEndValue) elements.rangeEndValue.textContent = data.config.rangeEnd;
    }
  }

  updateTargetMode();
  updateRangeCount();
  drawFrameMap();

  if (elements.targetStatusText) {
    elements.targetStatusText.textContent = enabled ? 'Enabled' : 'Disabled';
  }

  const capture = data.lastCapture;
  if (elements.lastCaptureInfo) {
    if (capture && Number.isFinite(capture.scanline)) {
      const pc = capture.pc != null ? capture.pc : capture.PC;
      const pcHex = pc != null ? pc.toString(16).toUpperCase().padStart(4, '0') : '----';
      const phase = capture.framePhase || capture.FramePhase || '';
      elements.lastCaptureInfo.textContent = `SL ${capture.scanline} ${phase} PC=$${pcHex}`;
    } else {
      elements.lastCaptureInfo.textContent = 'None';
    }
  }
}

// Load Imagine state on initialization
async function loadImagineState() {
  console.log('[Imagine] Loading Imagine state...');
  
  // Set random flavor text once on page load
  elements.imagineFlavor.textContent = getRandomFlavorText();

  const savedConfig = loadImagineConfig();
  if (savedConfig) {
    applyImagineConfig(savedConfig);
  }
  
  try {
    // Check if model is loaded
    const modelData = await api.imagine.isModelLoaded();
    if (modelData.success && modelData.modelLoaded) {
      elements.imagineStatus.textContent = 'Status: Model loaded and ready';
      elements.imagineStatus.style.color = 'var(--green)';
      console.log('[Imagine] Model is loaded');
    } else {
      elements.imagineStatus.textContent = 'Status: No model loaded - click "Load Model"';
      elements.imagineStatus.style.color = 'var(--gray)';
      console.log('[Imagine] No model loaded');
    }
    
    // Load current epoch
    const epochData = await api.imagine.getEpoch();
    if (epochData.success && epochData.epoch != null) {
      elements.imagineEpoch.value = epochData.epoch;
      console.log('[Imagine] Current epoch:', epochData.epoch);
    }
    
    // Load generation params (only if no saved config is present)
    if (!savedConfig) {
      const paramsData = await api.imagine.getGenerationParams();
      if (paramsData.success) {
        if (paramsData.bytesToGenerate != null) {
          elements.imagineBytesToPredict.value = paramsData.bytesToGenerate;
        }
        if (paramsData.temperature != null) {
          // Convert from 0.0-1.0 to 0-150 for UI
          const tempUI = Math.round(paramsData.temperature * 150);
          elements.imagineTemperature.value = tempUI;
          elements.imagineTemperatureValue.textContent = paramsData.temperature.toFixed(2);
        }
        if (paramsData.topK != null) {
          elements.imagineTopK.value = paramsData.topK;
        }
        console.log('[Imagine] Generation params loaded:', paramsData);
      }
    } else {
      await imagineUpdateParams();
    }

    await refreshTargetedStatus();
  } catch (error) {
    console.error('[Imagine] Error loading Imagine state:', error);
    elements.imagineStatus.textContent = 'Status: Error loading state';
    elements.imagineStatus.style.color = 'var(--red)';
  }
}
