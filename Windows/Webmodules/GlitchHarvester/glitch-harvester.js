// Glitch Harvester Web Module
// ID-based state machine implementation

const API_BASE = 'http://127.0.0.1:42067';

// State
let selectedBaseId = null;
let selectedStashId = null;
let selectedStockpileId = null;
let currentRenameId = null;

// DOM Elements
const elements = {
  // Base states
  baseNameInput: null,
  btnAddBase: null,
  baseList: null,
  btnLoadBase: null,
  btnDeleteBase: null,
  
  // Stash
  chkLoadOnOperation: null,
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
  initializeElements();
  console.log('[GH] Elements initialized');
  attachEventListeners();
  console.log('[GH] Event listeners attached');
  refreshAll();
  console.log('[GH] Initial refresh triggered');
  
  // Auto-refresh disabled for debugging
  // setInterval(refreshAll, 3000);
  console.log('[GH] Auto-refresh DISABLED for debugging');
});

function initializeElements() {
  // Base states
  elements.baseNameInput = document.getElementById('baseNameInput');
  elements.btnAddBase = document.getElementById('btnAddBase');
  elements.baseList = document.getElementById('baseList');
  elements.btnLoadBase = document.getElementById('btnLoadBase');
  elements.btnDeleteBase = document.getElementById('btnDeleteBase');
  
  // Stash
  elements.chkLoadOnOperation = document.getElementById('chkLoadOnOperation');
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

// ==================== API Calls ====================

async function apiCall(endpoint, options = {}) {
  console.log('[GH API] Calling:', endpoint, options.method || 'GET');
  if (options.body) {
    console.log('[GH API] Request body:', options.body);
  }
  
  try {
    const response = await fetch(`${API_BASE}${endpoint}`, options);
    console.log('[GH API] Response status:', response.status, response.statusText);
    
    // Check if response has content
    const contentType = response.headers.get('content-type');
    if (!contentType || !contentType.includes('application/json')) {
      console.error('[GH API] Response is not JSON, content-type:', contentType);
      return { 
        success: false, 
        error: `Server returned ${response.status}: ${response.statusText}` 
      };
    }
    
    const data = await response.json();
    console.log('[GH API] Response data:', data);
    return data;
  } catch (error) {
    console.error('[GH API] Error:', error);
    showToast(`API Error: ${error.message}`, 'error');
    return { success: false, error: error.message };
  }
}

// ==================== Base States ====================

async function refreshBases() {
  console.log('[GH] Refreshing base states...');
  const data = await apiCall('/api/gh/base-states');
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
  const data = await apiCall('/api/gh/base-state', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name })
  });
  
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
    await refreshBases();
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
  const data = await apiCall(`/api/gh/load-base`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ id: selectedBaseId })
  });
  
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
      const data = await apiCall(`/api/gh/base-state/${selectedBaseId}`, {
        method: 'DELETE'
      });
      
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

async function refreshStash() {
  const data = await apiCall('/api/gh/stash');
  
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
}

function selectStash(id) {
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
  
  const data = await apiCall('/api/gh/load-on-operation', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ enabled })
  });
  
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
  
  const data = await apiCall('/api/gh/corrupt-and-stash', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ id: selectedBaseId })
  });
  
  console.log('[GH] Blast response:', data);
  
  if (data.success) {
    console.log('[GH] Corruption created:', data.entry);
    showToast('Corruption created and added to stash', 'success');
    selectedStashId = data.entry?.id;
    console.log('[GH] Selected stash ID set to:', selectedStashId);
    await refreshStash();
  } else {
    console.error('[GH] Blast failed:', data.error);
    showToast(data.error || 'Failed to create corruption', 'error');
  }
}

async function replayStash() {
  if (!selectedStashId) return;
  
  const data = await apiCall(`/api/gh/stash/${selectedStashId}/replay`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({})
  });
  
  if (data.success) {
    showToast('Stash entry replayed', 'success');
  } else {
    showToast(data.error || 'Failed to replay stash entry', 'error');
  }
}

async function promoteToStockpile() {
  if (!selectedStashId) return;
  
  const data = await apiCall(`/api/gh/stash/${selectedStashId}/promote`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({})
  });
  
  if (data.success) {
    showToast('Entry promoted to stockpile', 'success');
    selectedStockpileId = data.entry?.Id;
    selectedStashId = null;
    await refreshStash();
    await refreshStockpile();
  } else {
    showToast(data.error || 'Failed to promote entry', 'error');
  }
}

async function clearStash() {
  if (!confirm('Clear all stash entries?')) return;
  
  const data = await apiCall('/api/gh/stash', {
    method: 'DELETE'
  });
  
  if (data.success) {
    showToast('Stash cleared', 'success');
    selectedStashId = null;
    await refreshStash();
  } else {
    showToast(data.error || 'Failed to clear stash', 'error');
  }
}

// ==================== Stockpile ====================

async function refreshStockpile() {
  const data = await apiCall('/api/gh/stockpile');
  
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
  
  const data = await apiCall(`/api/gh/stockpile/${selectedStockpileId}/replay`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({})
  });
  
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
  
  const data = await apiCall(`/api/gh/stockpile/${currentRenameId}/rename`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name: newName })
  });
  
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
      const data = await apiCall(`/api/gh/stockpile/${selectedStockpileId}`, {
        method: 'DELETE'
      });
      
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
  const data = await apiCall('/api/gh/stockpile/export');
  
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
    
    const data = await apiCall('/api/gh/stockpile/import', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ json: text })
    });
    
    if (data.success) {
      showToast(`Imported ${data.imported || 0} entries`, 'success');
      await refreshStockpile();
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
  hideConfirmModal();
  if (confirmCallback) {
    confirmCallback();
  }
}
