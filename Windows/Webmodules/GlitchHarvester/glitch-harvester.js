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
document.addEventListener('DOMContentLoaded', () => {
  console.log('[GH] DOM Content Loaded - Initializing Glitch Harvester');
  if (!api) {
    console.error('[GH] webapi helper not loaded');
    showToast('Web API not available', 'error');
    return;
  }
  initializeElements();
  console.log('[GH] Elements initialized');
  attachEventListeners();
  console.log('[GH] Event listeners attached');
  
  // Initialize RTC state
  loadRTCState();
  console.log('[GH] RTC state loading...');
  
  // Initialize Imagine state
  loadImagineState();
  console.log('[GH] Imagine state loading...');
  
  refreshAll();
  console.log('[GH] Initial refresh triggered');
  
  // Auto-refresh disabled for debugging
  // setInterval(refreshAll, 3000);
  console.log('[GH] Auto-refresh DISABLED for debugging');
});

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
  elements.chkAutoCorrupt.addEventListener('change', toggleAutoCorrupt);
  elements.blastTypeSelect.addEventListener('change', updateBlastType);
  elements.intensity.addEventListener('input', () => {
    elements.intensityValue.textContent = elements.intensity.value;
    // Sync stash intensity with RTC intensity
    elements.stashIntensity.value = elements.intensity.value;
    elements.stashIntensityValue.textContent = elements.intensity.value;
  });
  elements.intensity.addEventListener('change', updateIntensity);
  elements.btnManualBlast.addEventListener('click', rtcManualBlast);
  elements.btnLetItRip.addEventListener('click', rtcLetItRip);
  elements.crashBehavior.addEventListener('change', updateCrashBehavior);
  
  // Stash intensity (synced with RTC intensity)
  elements.stashIntensity.addEventListener('input', () => {
    elements.stashIntensityValue.textContent = elements.stashIntensity.value;
    // Sync RTC intensity with stash intensity
    elements.intensity.value = elements.stashIntensity.value;
    elements.intensityValue.textContent = elements.stashIntensity.value;
  });
  elements.stashIntensity.addEventListener('change', updateIntensity);
  
  // Base states
  elements.btnAddBase.addEventListener('click', addBase);
  elements.btnLoadBase.addEventListener('click', loadBase);
  elements.btnDeleteBase.addEventListener('click', deleteBase);
  
  // Stash
  elements.chkLoadOnOperation.addEventListener('change', toggleLoadOnOperation);
  elements.btnBlast.addEventListener('click', blast);
  elements.btnReplayStash.addEventListener('click', replayStash);
  elements.btnKeep.addEventListener('click', promoteToStockpile);
  elements.btnClearStash.addEventListener('click', clearStash);
  
  // Stockpile
  elements.btnReplayStock.addEventListener('click', replayStockpile);
  elements.btnRenameStock.addEventListener('click', showRenameModal);
  elements.btnDeleteStock.addEventListener('click', deleteStockpile);
  elements.btnExport.addEventListener('click', exportStockpile);
  elements.fileImport.addEventListener('change', importStockpile);
  
  // Imagine
  elements.btnLoadModel.addEventListener('click', imagineLoadModel);
  elements.imagineTemperature.addEventListener('input', () => {
    // Temperature slider goes from 0-150, convert to 0.0-1.0 for display
    const tempValue = (parseFloat(elements.imagineTemperature.value) / 150.0).toFixed(2);
    elements.imagineTemperatureValue.textContent = tempValue;
  });
  elements.btnImagineBug.addEventListener('click', imagineAutoBug);
  
  // Rename Modal
  elements.btnRenameCancel.addEventListener('click', hideRenameModal);
  elements.btnRenameConfirm.addEventListener('click', confirmRename);
  elements.renameModal.addEventListener('click', (e) => {
    if (e.target === elements.renameModal) hideRenameModal();
  });
  
  // Confirm Modal
  elements.btnConfirmCancel.addEventListener('click', hideConfirmModal);
  elements.btnConfirmOk.addEventListener('click', handleConfirmOk);
  elements.confirmModal.addEventListener('click', (e) => {
    if (e.target === elements.confirmModal) hideConfirmModal();
  });
  
  // Keyboard shortcuts
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      hideRenameModal();
      hideConfirmModal();
    }
  });
}

// ==================== Real-Time Corruptor ====================

async function loadRTCState() {
  console.log('[RTC] Loading RTC state...');
  
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
    console.log('[RTC] Crash behavior from API:', crashData.crashBehavior);
    if (elements.crashBehavior) {
      elements.crashBehavior.value = crashData.crashBehavior;
      console.log('[RTC] Crash behavior dropdown value set to:', elements.crashBehavior.value);
    } else {
      console.error('[RTC] Crash behavior dropdown element is null!');
    }
    currentCrashBehavior = crashData.crashBehavior;
    updateCrashStatus(crashData.crashed, crashData.crashBehavior);
    startCrashPolling(crashData.crashBehavior);
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

function getRandomFlavorText() {
  return IMAGINE_FLAVOR_TEXTS[Math.floor(Math.random() * IMAGINE_FLAVOR_TEXTS.length)];
}

async function imagineLoadModel() {
  console.log('[Imagine] Load Model clicked');
  const epoch = parseInt(elements.imagineEpoch.value);
  
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
    // First, update generation parameters from UI
    await imagineUpdateParams();
    
    // Call the imagine-a-bug API
    const data = await api.imagine.imagineABug();
    
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
    elements.btnImagineBug.disabled = false;
  }
}

// Load Imagine state on initialization
async function loadImagineState() {
  console.log('[Imagine] Loading Imagine state...');
  
  // Set random flavor text once on page load
  elements.imagineFlavor.textContent = getRandomFlavorText();
  
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
    
    // Load generation params
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
  } catch (error) {
    console.error('[Imagine] Error loading Imagine state:', error);
    elements.imagineStatus.textContent = 'Status: Error loading state';
    elements.imagineStatus.style.color = 'var(--red)';
  }
}
