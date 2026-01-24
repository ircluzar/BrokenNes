// Hex Editor Web Module

const API_BASE = 'http://127.0.0.1:42067';
const BYTES_PER_ROW = 16;
const ROW_HEIGHT = 18;
const SCROLL_BUFFER_ROWS = 8;

const elements = {
  domainSelect: null,
  domainMeta: null,
  btnRefresh: null,
  btnReloadDomains: null,
  autoRefresh: null,
  refreshRate: null,
  statusText: null
};

let domains = [];
let currentDomain = null;
let domainSize = 0;
let rowCount = 0;
let table = null;
let refreshTimer = null;
let refreshInFlight = false;
let pendingRefresh = false;
let lastRequested = { start: -1, end: -1 };
let scrollDebounce = null;
let editInProgress = false;
let lastDomainFetchTime = 0;
const DOMAIN_REFRESH_INTERVAL = 5000; // Refresh domains every 5 seconds if needed

// Track recently edited cells to prevent refresh from overwriting them
const recentEdits = new Map(); // key: "rowIndex:field" -> { value, timestamp }
const EDIT_PROTECTION_MS = 5000; // Protect edited cells for 5 seconds (increased for debugging)

// Heatmap tracking: monitors how often each visible cell's value changes
// key: "rowIndex:colIndex" -> { lastValue, changeCount, totalCount }
const heatmapStats = new Map();
let heatmapVisibleRange = { start: -1, end: -1 };

const hexFields = Array.from({ length: BYTES_PER_ROW }, (_, i) => `b${i}`);

document.addEventListener('DOMContentLoaded', () => {
  initializeElements();
  initializeTable();
  attachEventListeners();
  loadDomains();
  updateAutoRefresh();
});

function initializeElements() {
  elements.domainSelect = document.getElementById('domainSelect');
  elements.domainMeta = document.getElementById('domainMeta');
  elements.btnRefresh = document.getElementById('btnRefresh');
  elements.btnReloadDomains = document.getElementById('btnReloadDomains');
  elements.autoRefresh = document.getElementById('autoRefresh');
  elements.refreshRate = document.getElementById('refreshRate');
  elements.statusText = document.getElementById('statusText');
}

function attachEventListeners() {
  elements.domainSelect.addEventListener('change', onDomainChanged);
  elements.btnRefresh.addEventListener('click', () => refreshVisible(true));
  elements.btnReloadDomains.addEventListener('click', () => reloadDomains());
  elements.autoRefresh.addEventListener('change', updateAutoRefresh);
  elements.refreshRate.addEventListener('change', updateAutoRefresh);
}

function initializeTable() {
  const columns = [
    {
      title: 'Addr',
      field: 'address',
      width: 52,
      headerSort: false,
      formatter: (cell) => cell.getValue() ?? ''
    },
    ...hexFields.map((field, index) => ({
      title: index.toString(16).toUpperCase().padStart(2, '0'),
      field,
      width: 20,
      minWidth: 20,
      hozAlign: 'center',
      headerHozAlign: 'center',
      headerSort: false,
      cssClass: 'hex-cell-editable',
      formatter: (cell) => formatByteCell(cell.getValue()),
      editor: hexByteEditor
    })),
    {
      title: 'ASCII',
      field: 'ascii',
      widthGrow: 2,
      headerSort: false,
      formatter: (cell) => cell.getValue() ?? ''
    }
  ];

  table = new Tabulator('#hexGrid', {
    height: '100%',
    layout: 'fitColumns',
    index: 'rowIndex',
    rowHeight: ROW_HEIGHT,
    selectableRange: true,
    selectableRangeMode: 'click',
    selectableRangeClearCells: true,
    selectableRangeCheck: (cell) => cell.getColumn().getField().startsWith('b'),
    clipboard: true,
    clipboardCopySelector: 'range',
    clipboardCopyConfig: {
      rowHeaders: false,
      columnHeaders: false
    },
    editOnKeyPress: true,
    editTriggerEvent: 'click',
    columns
  });

  table.on('cellEditing', handleCellEditStart);
  table.on('cellEditCancelled', handleCellEditEnd);
  table.on('cellEdited', handleCellEdited);

  table.on('tableBuilt', () => {
    const holder = document.querySelector('#hexGrid .tabulator-tableholder');
    if (!holder) {
      return;
    }

    holder.addEventListener('scroll', () => {
      // Cancel any active edit when scrolling to prevent addRange errors
      // and to allow memory refresh to continue
      if (editInProgress) {
        try {
          const activeElement = document.activeElement;
          if (activeElement && activeElement.tagName === 'INPUT' && activeElement.closest('.tabulator')) {
            activeElement.blur();
          }
        } catch (e) {
          // Ignore errors during edit cancellation
        }
        // Force clear editInProgress - if user scrolled away, the edit is effectively cancelled
        editInProgress = false;
      }
      
      if (scrollDebounce) {
        clearTimeout(scrollDebounce);
      }
      scrollDebounce = setTimeout(() => refreshVisible(false), 80);
    });

    holder.addEventListener('paste', handlePaste);
  });
}

async function loadDomains() {
  const result = await apiCall('/api/memory/domains');
  if (!result.success) {
    elements.statusText.textContent = result.error || 'Failed to load memory domains.';
    return;
  }

  lastDomainFetchTime = Date.now();
  domains = result.domains || [];
  elements.domainSelect.innerHTML = '';

  if (domains.length === 0) {
    elements.domainMeta.textContent = 'Load a ROM to inspect memory.';
    elements.statusText.textContent = 'Load a ROM to inspect memory.';
    return;
  }

  domains.forEach((domain) => {
    const option = document.createElement('option');
    option.value = domain.name;
    option.textContent = `${domain.name} (${domain.size} bytes)`;
    option.dataset.size = domain.size;
    elements.domainSelect.appendChild(option);
  });

  elements.domainSelect.selectedIndex = 0;
  await onDomainChanged();
}

async function reloadDomains() {
  // Preserve the current domain selection if possible
  const previousDomain = currentDomain;
  
  const result = await apiCall('/api/memory/domains');
  if (!result.success) {
    elements.statusText.textContent = 'Domains may be stale. ' + (result.error || 'Failed to reload.');
    return;
  }

  lastDomainFetchTime = Date.now();
  domains = result.domains || [];
  elements.domainSelect.innerHTML = '';

  if (domains.length === 0) {
    elements.domainMeta.textContent = 'Load a ROM to inspect memory.';
    elements.statusText.textContent = 'Load a ROM to inspect memory.';
    currentDomain = null;
    domainSize = 0;
    return;
  }

  domains.forEach((domain) => {
    const option = document.createElement('option');
    option.value = domain.name;
    option.textContent = `${domain.name} (${domain.size} bytes)`;
    option.dataset.size = domain.size;
    elements.domainSelect.appendChild(option);
  });

  // Try to restore previous selection
  const matchingIndex = domains.findIndex(d => d.name === previousDomain);
  if (matchingIndex >= 0) {
    elements.domainSelect.selectedIndex = matchingIndex;
  } else {
    elements.domainSelect.selectedIndex = 0;
  }
  
  await onDomainChanged();
  elements.statusText.textContent = 'Domains refreshed.';
}

async function onDomainChanged() {
  const selected = elements.domainSelect.value;
  if (!selected) {
    return;
  }

  const option = elements.domainSelect.selectedOptions[0];
  domainSize = Number(option?.dataset?.size ?? 0);
  currentDomain = selected;
  rowCount = Math.ceil(domainSize / BYTES_PER_ROW);
  
  console.log(`[HexEditor] Domain changed to "${currentDomain}", size=${domainSize}, rowCount=${rowCount}`);

  // Clear edit protections when switching domains - they're no longer valid
  recentEdits.clear();
  
  // Clear heatmap stats when domain changes
  heatmapStats.clear();
  heatmapVisibleRange = { start: -1, end: -1 };

  // Reset scroll position to avoid confusion
  const holder = document.querySelector('#hexGrid .tabulator-tableholder');
  if (holder) {
    holder.scrollTop = 0;
  }

  updateDomainMeta();
  buildEmptyRows();

  await refreshVisible(true);
}

function updateDomainMeta() {
  if (!currentDomain) {
    elements.domainMeta.textContent = 'Waiting for emulator…';
    return;
  }

  elements.domainMeta.textContent = `${currentDomain} • ${domainSize} bytes`;
}

function buildEmptyRows() {
  const rows = new Array(rowCount).fill(null).map((_, rowIndex) => {
    const address = rowIndex * BYTES_PER_ROW;
    const row = {
      rowIndex,
      address: `0x${address.toString(16).toUpperCase().padStart(6, '0')}`,
      ascii: ''
    };

    hexFields.forEach((field) => {
      row[field] = null;
    });

    return row;
  });

  table.replaceData(rows);
  lastRequested = { start: -1, end: -1 };
}

function getVisibleRange() {
  const holder = document.querySelector('#hexGrid .tabulator-tableholder');
  if (!holder) return null;

  const scrollTop = holder.scrollTop;
  const viewportHeight = holder.clientHeight;
  const firstRow = Math.max(0, Math.floor(scrollTop / ROW_HEIGHT));
  const visibleRows = Math.ceil(viewportHeight / ROW_HEIGHT);
  const start = Math.max(0, firstRow - SCROLL_BUFFER_ROWS);
  const end = Math.min(rowCount - 1, firstRow + visibleRows + SCROLL_BUFFER_ROWS);

  return { start, end };
}

async function refreshVisible(force) {
  if (!currentDomain || domainSize <= 0) {
    console.log(`[HexEditor] refreshVisible skipped: domain="${currentDomain}", size=${domainSize}`);
    return;
  }

  // Don't refresh while editing - it can overwrite user input
  if (editInProgress) {
    console.log(`[HexEditor] refreshVisible skipped: edit in progress`);
    return;
  }

  const range = getVisibleRange();
  if (!range) {
    console.log(`[HexEditor] refreshVisible skipped: no visible range`);
    return;
  }

  if (!force && range.start === lastRequested.start && range.end === lastRequested.end) {
    // Same range, skip (not an error, just optimization)
    return;
  }

  if (refreshInFlight) {
    console.log(`[HexEditor] refreshVisible queued: another refresh in progress`);
    pendingRefresh = true;
    return;
  }

  refreshInFlight = true;
  lastRequested = range;
  console.log(`[HexEditor] refreshVisible fetching rows ${range.start}-${range.end}`);

  try {
    await fetchRange(range.start, range.end);
  } catch (err) {
    console.error(`[HexEditor] refreshVisible error:`, err);
    // Check if domains might be stale (e.g., game restart)
    if (Date.now() - lastDomainFetchTime > DOMAIN_REFRESH_INTERVAL) {
      await reloadDomains();
    }
  } finally {
    refreshInFlight = false;
    if (pendingRefresh) {
      pendingRefresh = false;
      console.log(`[HexEditor] Processing pending refresh`);
      refreshVisible(true);
    }
  }
}

async function fetchRange(startRow, endRow) {
  const startAddress = startRow * BYTES_PER_ROW;
  const length = Math.min(domainSize - startAddress, (endRow - startRow + 1) * BYTES_PER_ROW);
  if (length <= 0) return;

  const url = `/api/memory/peek-range?domain=${encodeURIComponent(currentDomain)}&address=${startAddress}&length=${length}`;
  console.log(`[HexEditor] Fetching: ${url}`);
  
  const result = await apiCall(url);
  
  // Debug: log the raw response
  console.log(`[HexEditor] peek-range response for ${currentDomain}@${startAddress}:`, 
    `domain="${result.domain}", address=${result.address}, length=${result.length}`,
    `first 8 bytes:`, result.data?.slice(0, 8));
  
  if (!result.success) {
    elements.statusText.textContent = result.error || 'Failed to read memory range.';
    throw new Error(result.error || 'Failed to read memory range');
  }

  const data = result.data || [];
  const updates = [];
  const now = Date.now();
  
  // Clean up expired edit protections
  for (const [key, edit] of recentEdits.entries()) {
    if (now - edit.timestamp > EDIT_PROTECTION_MS) {
      recentEdits.delete(key);
    }
  }
  
  // Heatmap: clear stats for cells that are no longer in the visible range
  if (heatmapVisibleRange.start !== startRow || heatmapVisibleRange.end !== endRow) {
    for (const key of heatmapStats.keys()) {
      const [rowStr] = key.split(':');
      const row = parseInt(rowStr, 10);
      if (row < startRow || row > endRow) {
        heatmapStats.delete(key);
      }
    }
    heatmapVisibleRange = { start: startRow, end: endRow };
  }

  for (let row = startRow; row <= endRow; row += 1) {
    const rowStart = (row - startRow) * BYTES_PER_ROW;
    const rowData = {
      rowIndex: row
    };

    let ascii = '';

    for (let col = 0; col < BYTES_PER_ROW; col += 1) {
      const byteIndex = rowStart + col;
      const fetchedValue = byteIndex < data.length ? data[byteIndex] : null;
      const field = hexFields[col];
      
      // Check if this cell was recently edited - if so, keep the edited value
      // Include domain in key to prevent cross-domain pollution
      const editKey = `${currentDomain}:${row}:${field}`;
      const recentEdit = recentEdits.get(editKey);
      let value;
      
      if (recentEdit && now - recentEdit.timestamp < EDIT_PROTECTION_MS) {
        // Use the recently-edited value instead of fetched value
        value = recentEdit.value;
        // Log when protection is active and values differ
        if (fetchedValue !== recentEdit.value) {
          console.log(`[HexEditor] Protected ${editKey}: showing ${recentEdit.value.toString(16).toUpperCase()} instead of fetched ${fetchedValue?.toString(16).toUpperCase() ?? 'null'}`);
        }
      } else {
        value = fetchedValue;
      }
      
      // Heatmap: track value changes for this cell
      const heatmapKey = `${row}:${col}`;
      let stats = heatmapStats.get(heatmapKey);
      if (!stats) {
        // First time seeing this cell - initialize stats
        stats = { lastValue: value, changeCount: 0, totalCount: 0 };
        heatmapStats.set(heatmapKey, stats);
      } else {
        // Compare to previous value
        stats.totalCount++;
        if (value !== null && stats.lastValue !== null && value !== stats.lastValue) {
          stats.changeCount++;
        }
        stats.lastValue = value;
      }
      
      rowData[field] = value;

      if (value === null || value === undefined) {
        ascii += ' ';
      } else if (value >= 32 && value <= 126) {
        ascii += String.fromCharCode(value);
      } else {
        ascii += '.';
      }
    }

    rowData.ascii = ascii;
    updates.push(rowData);
  }

  table.updateData(updates);
  
  // Apply heatmap styling to visible cells
  applyHeatmapStyles(startRow, endRow);
  elements.statusText.textContent = `Showing ${endRow - startRow + 1} rows • ${length} bytes @ 0x${startAddress.toString(16).toUpperCase()}`;
}

function handleCellEditStart() {
  editInProgress = true;
}

function handleCellEditEnd() {
  editInProgress = false;
}

function applyHeatmapStyles(startRow, endRow) {
  // Apply red background intensity based on change frequency
  // We need to find the actual DOM cells and style them
  for (let row = startRow; row <= endRow; row++) {
    const tabulatorRow = table.getRow(row);
    if (!tabulatorRow) continue;
    
    const rowElement = tabulatorRow.getElement();
    if (!rowElement) continue;
    
    for (let col = 0; col < BYTES_PER_ROW; col++) {
      const heatmapKey = `${row}:${col}`;
      const stats = heatmapStats.get(heatmapKey);
      
      const field = hexFields[col];
      const cell = tabulatorRow.getCell(field);
      if (!cell) continue;
      
      const cellElement = cell.getElement();
      if (!cellElement) continue;
      
      if (!stats || stats.totalCount === 0) {
        // No data yet, clear any heatmap styling
        cellElement.style.removeProperty('--heatmap-intensity');
        cellElement.classList.remove('heatmap-active');
        continue;
      }
      
      // Calculate change frequency (0 to 1)
      const changeRatio = stats.changeCount / stats.totalCount;
      
      if (changeRatio > 0) {
        // Apply intensity: more changes = more red
        // Use a scale that makes even moderate activity visible
        // Cap at 0.8 opacity to keep text readable
        const intensity = Math.min(0.8, changeRatio);
        cellElement.style.setProperty('--heatmap-intensity', intensity);
        cellElement.classList.add('heatmap-active');
      } else {
        cellElement.style.removeProperty('--heatmap-intensity');
        cellElement.classList.remove('heatmap-active');
      }
    }
  }
}

async function handleCellEdited(cell) {
  // Keep editInProgress = true until the entire operation completes
  // to prevent auto-refresh from overwriting our changes
  
  const field = cell.getColumn().getField();
  if (!field.startsWith('b')) {
    editInProgress = false;
    return;
  }

  const row = cell.getRow().getData();
  const rowIndex = row.rowIndex;
  const colIndex = Number(field.substring(1));
  const address = rowIndex * BYTES_PER_ROW + colIndex;

  const value = normalizeByte(cell.getValue());
  if (value === null) {
    cell.restoreOldValue();
    editInProgress = false;
    return;
  }

  try {
    // Record this edit to protect it from being overwritten by refresh
    // Include domain in key to prevent cross-domain pollution
    const editKey = `${currentDomain}:${rowIndex}:${field}`;
    recentEdits.set(editKey, { value, timestamp: Date.now() });
    
    // Write the byte - server now returns before/after values for diagnosis
    const writeResult = await writeByte(address, value);
    if (!writeResult.success) {
      recentEdits.delete(editKey);
      cell.restoreOldValue();
      return;
    }
    
    // Use the server's immediate read-back for verification
    const { beforeValue, afterValue, verified } = writeResult;
    
    // Log the poke result for debugging
    console.log(`[HexEditor] Poke ${currentDomain}@0x${address.toString(16)}: wrote=0x${value.toString(16)}, before=0x${beforeValue?.toString(16)}, after=0x${afterValue?.toString(16)}, verified=${verified}`);
    
    // Only update protection if the write was verified
    // If not verified, keep showing what the user typed
    if (verified) {
      recentEdits.set(editKey, { value: afterValue, timestamp: Date.now() });
    } else {
      // Keep the user's intended value in protection, don't overwrite with server's afterValue
      recentEdits.set(editKey, { value: value, timestamp: Date.now() });
    }
    
    if (!verified) {
      // Value didn't persist immediately - show diagnostic info
      elements.statusText.textContent = `Poke 0x${value.toString(16).toUpperCase()} @ 0x${address.toString(16).toUpperCase()}: before=${beforeValue?.toString(16)?.toUpperCase()}, after=${afterValue?.toString(16)?.toUpperCase()} (NOT VERIFIED)`;
    } else {
      elements.statusText.textContent = `Wrote 0x${value.toString(16).toUpperCase()} to ${currentDomain} @ 0x${address.toString(16).toUpperCase()} ✓`;
    }
    
    // Update the cell with what the user typed (not what server says, since server may be buggy)
    cell.getRow().update({ [field]: value });
    
    updateRowAscii(rowIndex);
  } finally {
    // Only now allow auto-refresh to resume
    editInProgress = false;
  }
}

function updateRowAscii(rowIndex) {
  const row = table.getRow(rowIndex);
  if (!row) return;

  const data = row.getData();
  let ascii = '';

  hexFields.forEach((field) => {
    const value = data[field];
    if (value === null || value === undefined) {
      ascii += ' ';
    } else if (value >= 32 && value <= 126) {
      ascii += String.fromCharCode(value);
    } else {
      ascii += '.';
    }
  });

  row.update({ ascii });
}

async function writeByte(address, value) {
  if (!currentDomain) return { success: false };

  const result = await apiCall('/api/memory/poke', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      Domain: currentDomain,
      Address: address,
      Value: value
    })
  });

  if (!result.success) {
    elements.statusText.textContent = result.error || 'Failed to write memory.';
    return { success: false };
  }
  
  // Return the verification info from the server
  return {
    success: true,
    beforeValue: result.beforeValue,
    afterValue: result.afterValue,
    verified: result.verified
  };
}



function updateAutoRefresh() {
  if (refreshTimer) {
    clearInterval(refreshTimer);
    refreshTimer = null;
  }

  if (!elements.autoRefresh.checked) {
    return;
  }

  const fps = Number(elements.refreshRate.value) || 2;
  const interval = Math.max(200, Math.floor(1000 / fps));
  refreshTimer = setInterval(() => refreshVisible(true), interval);
}

function formatByteCell(value) {
  if (value === null || value === undefined) {
    return '--';
  }

  const normalized = Number(value) & 0xFF;
  return normalized.toString(16).toUpperCase().padStart(2, '0');
}

function hexByteEditor(cell, onRendered, success, cancel) {
  const input = document.createElement('input');
  input.type = 'text';
  input.maxLength = 2;
  input.className = 'hex-input';
  input.value = formatByteCell(cell.getValue()).replace('--', '');
  
  let submitted = false;

  onRendered(() => {
    // Wrap in try-catch to handle cases where element was removed from DOM
    try {
      if (document.body.contains(input)) {
        input.focus();
        input.select();
      }
    } catch (e) {
      // Element not in document, cancel the edit
      cancel();
    }
  });

  function submit() {
    if (submitted) return;
    submitted = true;
    
    const value = normalizeByte(input.value);
    if (value === null) {
      cancel();
      return;
    }
    success(value);
  }

  input.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      submit();
    }
    if (event.key === 'Escape') {
      submitted = true;
      cancel();
    }
  });

  input.addEventListener('blur', () => {
    // Small delay to allow for click-triggered blur vs scroll-triggered blur
    setTimeout(() => {
      if (!submitted && document.body.contains(input)) {
        submit();
      } else if (!submitted) {
        // Element was removed from DOM (due to scroll), just cancel
        cancel();
      }
    }, 10);
  });
  
  return input;
}

function normalizeByte(value) {
  if (value === null || value === undefined) {
    return null;
  }

  if (typeof value === 'number' && Number.isFinite(value)) {
    return Math.max(0, Math.min(255, Math.round(value)));
  }

  if (typeof value !== 'string') {
    return null;
  }

  let text = value.trim();
  if (!text) return null;

  if (text.startsWith('0x') || text.startsWith('0X')) {
    text = text.slice(2);
  }

  if (!/^[0-9a-fA-F]+$/.test(text)) {
    return null;
  }

  const parsed = parseInt(text, 16);
  if (Number.isNaN(parsed)) return null;

  return Math.max(0, Math.min(255, parsed));
}

function parseAddress(text) {
  let value = text.trim();
  if (value.startsWith('0x') || value.startsWith('0X')) {
    value = value.slice(2);
    if (!/^[0-9a-fA-F]+$/.test(value)) return null;
    return parseInt(value, 16);
  }

  if (/^[0-9]+$/.test(value)) {
    return parseInt(value, 10);
  }

  if (/^[0-9a-fA-F]+$/.test(value)) {
    return parseInt(value, 16);
  }

  return null;
}

function handlePaste(event) {
  const selectedCells = table.getSelectedCells();
  if (!selectedCells || selectedCells.length === 0) {
    return;
  }

  const clipboardText = event.clipboardData?.getData('text');
  if (!clipboardText) {
    return;
  }

  const targetCell = selectedCells[0];
  const startField = targetCell.getColumn().getField();
  if (!startField.startsWith('b')) {
    return;
  }

  const startRowIndex = targetCell.getRow().getData().rowIndex;
  const startColIndex = Number(startField.substring(1));

  const bytes = parseHexGrid(clipboardText);
  if (bytes.length === 0) {
    return;
  }

  event.preventDefault();

  const updates = [];
  let address = startRowIndex * BYTES_PER_ROW + startColIndex;
  const maxAddress = domainSize - 1;
  const clampedBytes = bytes.filter((_, idx) => address + idx <= maxAddress);

  if (clampedBytes.length === 0) {
    return;
  }

  writeRange(address, clampedBytes);

  for (let i = 0; i < clampedBytes.length; i += 1) {
    const absoluteAddress = address + i;
    const rowIndex = Math.floor(absoluteAddress / BYTES_PER_ROW);
    const colIndex = absoluteAddress % BYTES_PER_ROW;
    const field = hexFields[colIndex];
    updates.push({ rowIndex, [field]: clampedBytes[i] });
  }

  table.updateData(updates);
  updateRowAsciiRange(updates.map(update => update.rowIndex));
}

function parseHexGrid(text) {
  const cleaned = text.replace(/[^0-9a-fA-F\s]/g, ' ');
  const parts = cleaned.split(/\s+/).filter(Boolean);
  const bytes = [];

  parts.forEach((part) => {
    const value = normalizeByte(part);
    if (value !== null) {
      bytes.push(value);
    }
  });

  return bytes;
}

async function writeRange(address, data) {
  if (!currentDomain || data.length === 0) {
    return;
  }

  const result = await apiCall('/api/memory/poke-range', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      Domain: currentDomain,
      Address: address,
      Data: data
    })
  });

  if (!result.success) {
    elements.statusText.textContent = result.error || 'Failed to write memory range.';
  }
}

function updateRowAsciiRange(rowIndexes) {
  const unique = Array.from(new Set(rowIndexes));
  unique.forEach((rowIndex) => updateRowAscii(rowIndex));
}

async function apiCall(endpoint, options = {}) {
  try {
    // Add cache-busting to prevent stale data
    const url = new URL(`${API_BASE}${endpoint}`);
    url.searchParams.set('_t', Date.now());
    
    const response = await fetch(url.toString(), {
      ...options,
      cache: 'no-store'  // Disable HTTP caching
    });
    if (!response.ok) {
      return { success: false, error: `HTTP ${response.status}` };
    }
    return await response.json();
  } catch (err) {
    return { success: false, error: err.message || 'Network error' };
  }
}
