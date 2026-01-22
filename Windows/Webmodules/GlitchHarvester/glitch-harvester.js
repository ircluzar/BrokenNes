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
  
  // Modal
  renameModal: null,
  renameInput: null,
  btnRenameCancel: null,
  btnRenameConfirm: null,
  
  // Toast
  toast: null
};

// Initialize
document.addEventListener('DOMContentLoaded', () => {
  initializeElements();
  attachEventListeners();
  refreshAll();
  
  // Auto-refresh every 3 seconds
  setInterval(refreshAll, 3000);
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
  
  // Modal
  elements.renameModal = document.getElementById('renameModal');
  elements.renameInput = document.getElementById('renameInput');
  elements.btnRenameCancel = document.getElementById('btnRenameCancel');
  elements.btnRenameConfirm = document.getElementById('btnRenameConfirm');
  
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
  
  // Modal
  elements.btnRenameCancel.addEventListener('click', hideRenameModal);
  elements.btnRenameConfirm.addEventListener('click', confirmRename);
  elements.renameModal.addEventListener('click', (e) => {
    if (e.target === elements.renameModal) hideRenameModal();
  });
  
  // Keyboard shortcuts
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      hideRenameModal();
    }
  });
}

// ==================== API Calls ====================

async function apiCall(endpoint, options = {}) {
  try {
    const response = await fetch(`${API_BASE}${endpoint}`, options);
    const data = await response.json();
    return data;
  } catch (error) {
    showToast(`API Error: ${error.message}`, 'error');
    return { success: false, error: error.message };
  }
}

// ==================== Base States ====================

async function refreshBases() {
  const data = await apiCall('/api/gh/base-states');
  
  if (!data.success) {
    elements.baseList.innerHTML = '<div class="gh-empty">Failed to load base states</div>';
    return;
  }
  
  const bases = data.baseStates || [];
  
  if (bases.length === 0) {
    elements.baseList.innerHTML = '<div class="gh-empty">No base states yet. Add one to begin.</div>';
    updateBaseButtons();
    return;
  }
  
  elements.baseList.innerHTML = bases.map(base => `
    <div class="gh-item ${base.Id === selectedBaseId ? 'selected' : ''}" 
         data-id="${base.Id}"
         onclick="selectBase('${base.Id}')">
      <div class="gh-item-info">
        <div class="gh-item-name">${escapeHtml(base.Name)}</div>
        <div class="gh-item-meta">${new Date(base.Created).toLocaleString()}</div>
      </div>
    </div>
  `).join('');
  
  updateBaseButtons();
  
  // Enable blast button if a base is selected
  const hasSelectedBase = bases.some(b => b.Id === data.selectedBaseId);
  elements.btnBlast.disabled = !hasSelectedBase;
}

function selectBase(id) {
  selectedBaseId = id;
  
  // Update UI immediately
  document.querySelectorAll('#baseList .gh-item').forEach(item => {
    item.classList.toggle('selected', item.dataset.id === id);
  });
  
  updateBaseButtons();
  
  // Call API to select
  apiCall(`/api/gh/base-state/${id}/select`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({})
  }).then(data => {
    if (data.success) {
      showToast('Base state selected');
      elements.btnBlast.disabled = false;
    }
  });
}

function updateBaseButtons() {
  const hasSelection = selectedBaseId !== null;
  elements.btnLoadBase.disabled = !hasSelection;
  elements.btnDeleteBase.disabled = !hasSelection;
}

async function addBase() {
  const name = elements.baseNameInput.value.trim();
  
  if (!name) {
    showToast('Please enter a name for the base state', 'error');
    return;
  }
  
  const data = await apiCall('/api/gh/base-state', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name })
  });
  
  if (data.success) {
    showToast(`Base state "${name}" created`, 'success');
    elements.baseNameInput.value = '';
    selectedBaseId = data.base?.Id;
    await refreshBases();
  } else {
    showToast(data.error || 'Failed to create base state', 'error');
  }
}

async function loadBase() {
  if (!selectedBaseId) return;
  
  const data = await apiCall(`/api/gh/base-state/${selectedBaseId}/load`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({})
  });
  
  if (data.success) {
    showToast('Base state loaded', 'success');
  } else {
    showToast(data.error || 'Failed to load base state', 'error');
  }
}

async function deleteBase() {
  if (!selectedBaseId) return;
  
  if (!confirm('Delete this base state?')) return;
  
  const data = await apiCall(`/api/gh/base-state/${selectedBaseId}`, {
    method: 'DELETE'
  });
  
  if (data.success) {
    showToast('Base state deleted', 'success');
    selectedBaseId = null;
    await refreshBases();
  } else {
    showToast(data.error || 'Failed to delete base state', 'error');
  }
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
    <div class="gh-item ${entry.Id === selectedStashId ? 'selected' : ''}" 
         data-id="${entry.Id}"
         onclick="selectStash('${entry.Id}')">
      <div class="gh-item-info">
        <div class="gh-item-name">${escapeHtml(entry.Name)}</div>
        <div class="gh-item-meta">${entry.Writes?.length || 0} writes • ${new Date(entry.Created).toLocaleString()}</div>
      </div>
    </div>
  `).join('');
  
  updateStashButtons();
}

function selectStash(id) {
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
  const data = await apiCall('/api/gh/corrupt-and-stash', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({})
  });
  
  if (data.success) {
    showToast('Corruption created and added to stash', 'success');
    selectedStashId = data.entry?.Id;
    await refreshStash();
  } else {
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
    <div class="gh-item ${entry.Id === selectedStockpileId ? 'selected' : ''}" 
         data-id="${entry.Id}"
         onclick="selectStockpile('${entry.Id}')">
      <div class="gh-item-info">
        <div class="gh-item-name">${escapeHtml(entry.Name)}</div>
        <div class="gh-item-meta">${entry.Writes?.length || 0} writes • ${new Date(entry.Created).toLocaleString()}</div>
      </div>
    </div>
  `).join('');
  
  updateStockpileButtons();
}

function selectStockpile(id) {
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
  
  if (!confirm('Delete this stockpile entry?')) return;
  
  const data = await apiCall(`/api/gh/stockpile/${selectedStockpileId}`, {
    method: 'DELETE'
  });
  
  if (data.success) {
    showToast('Stockpile entry deleted', 'success');
    selectedStockpileId = null;
    await refreshStockpile();
  } else {
    showToast(data.error || 'Failed to delete stockpile entry', 'error');
  }
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
