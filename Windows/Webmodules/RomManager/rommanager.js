// rommanager.js - ROM Manager main logic for WinForms webmodule

(function() {
  'use strict';

  // State
  let allRoms = [];
  let selectedRomKey = null;
  let filterCompatibleOnly = false;
  let searchQuery = '';
  let pendingImports = [];
  let continueDb = null;
  let romToDelete = null;

  // DOM Elements (cached after init)
  let elements = {};

  // Initialize on page load
  window.addEventListener('DOMContentLoaded', init);

  async function init() {
    try {
      console.log('[RomManager] Initializing...');

      // Start pixel background animation
      if (window.homePixelBgEnsure) {
        window.homePixelBgEnsure();
      }

      // Cache DOM elements
      cacheElements();

      // Set up event listeners
      setupEventListeners();

      // Open database connection
      await openDatabase();

      // Load ROMs from storage
      await loadRoms();

      // Render ROM list
      renderRomList();

      // Play background music
      playBackgroundMusic();

      console.log('[RomManager] Initialization complete');
    } catch (error) {
      console.error('[RomManager] Initialization error:', error);
    }
  }

  function cacheElements() {
    elements = {
      filterCheckbox: document.getElementById('filterCompatibleOnly'),
      searchInput: document.getElementById('searchInput'),
      importBtn: document.getElementById('importBtn'),
      romListContainer: document.getElementById('romListContainer'),
      romDetailsSection: document.getElementById('romDetailsSection'),
      deleteBtn: document.getElementById('deleteBtn'),
      
      // Details
      detailTitle: document.getElementById('detailTitle'),
      detailFileName: document.getElementById('detailFileName'),
      detailSize: document.getElementById('detailSize'),
      detailSystem: document.getElementById('detailSystem'),
      detailGameId: document.getElementById('detailGameId'),
      detailAchievements: document.getElementById('detailAchievements'),
      detailCompatible: document.getElementById('detailCompatible'),
      
      // Import modal
      importModal: document.getElementById('importModal'),
      browseBtn: document.getElementById('browseBtn'),
      fileInput: document.getElementById('fileInput'),
      dropArea: document.getElementById('dropArea'),
      pendingList: document.getElementById('pendingList'),
      confirmImportBtn: document.getElementById('confirmImportBtn'),
      cancelImportBtn: document.getElementById('cancelImportBtn'),
      
      // Delete modal
      deleteModal: document.getElementById('deleteModal'),
      deleteMessage: document.getElementById('deleteMessage'),
      confirmDeleteBtn: document.getElementById('confirmDeleteBtn'),
      cancelDeleteBtn: document.getElementById('cancelDeleteBtn')
    };
  }

  function setupEventListeners() {
    // Filter and search
    elements.filterCheckbox.addEventListener('change', (e) => {
      filterCompatibleOnly = e.target.checked;
      renderRomList();
    });
    
    elements.searchInput.addEventListener('input', (e) => {
      searchQuery = e.target.value.toLowerCase();
      renderRomList();
    });

    // Import button
    elements.importBtn.addEventListener('click', openImportModal);

    // Delete button
    elements.deleteBtn.addEventListener('click', openDeleteModal);

    // Import modal
    elements.browseBtn.addEventListener('click', () => elements.fileInput.click());
    elements.fileInput.addEventListener('change', handleFileSelect);
    elements.confirmImportBtn.addEventListener('click', confirmImport);
    elements.cancelImportBtn.addEventListener('click', closeImportModal);
    elements.importModal.addEventListener('click', (e) => {
      if (e.target === elements.importModal) closeImportModal();
    });

    // Delete modal
    elements.confirmDeleteBtn.addEventListener('click', confirmDelete);
    elements.cancelDeleteBtn.addEventListener('click', closeDeleteModal);
    elements.deleteModal.addEventListener('click', (e) => {
      if (e.target === elements.deleteModal) closeDeleteModal();
    });

    // Drag and drop
    setupDragAndDrop();
  }

  function setupDragAndDrop() {
    const dropArea = elements.dropArea;
    
    ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(eventName => {
      dropArea.addEventListener(eventName, preventDefaults, false);
    });

    function preventDefaults(e) {
      e.preventDefault();
      e.stopPropagation();
    }

    ['dragenter', 'dragover'].forEach(eventName => {
      dropArea.addEventListener(eventName, () => {
        dropArea.classList.add('drag-over');
      }, false);
    });

    ['dragleave', 'drop'].forEach(eventName => {
      dropArea.addEventListener(eventName, () => {
        dropArea.classList.remove('drag-over');
      }, false);
    });

    dropArea.addEventListener('drop', handleDrop, false);
  }

  async function openDatabase() {
    try {
      // Access continueDb from shared storage
      if (window.continueDb && typeof window.continueDb.open === 'function') {
        await window.continueDb.open();
        continueDb = window.continueDb;
        console.log('[RomManager] Database opened successfully');
      } else {
        console.warn('[RomManager] continueDb not available - some features may be limited');
        // ROM Manager can still work without continueDb for basic ROM storage
      }
    } catch (error) {
      console.error('[RomManager] Database open error:', error);
    }
  }

  async function loadRoms() {
    try {
      console.log('[RomManager] Loading ROMs...');
      
      // Get all ROM keys from nesStorage
      const romKeys = await getAllRomKeys();
      console.log('[RomManager] Found ROM keys:', romKeys);

      // Get all games from continueDb
      let games = [];
      if (continueDb && typeof continueDb.getAll === 'function') {
        try {
          games = await continueDb.getAll('games');
          console.log('[RomManager] Loaded games from DB:', games.length);
        } catch (error) {
          console.error('[RomManager] Error loading games:', error);
        }
      }

      // Get achievements for compatibility check
      let achievements = [];
      if (continueDb && typeof continueDb.getAll === 'function') {
        try {
          achievements = await continueDb.getAll('achievements');
          console.log('[RomManager] Loaded achievements:', achievements.length);
        } catch (error) {
          console.error('[RomManager] Error loading achievements:', error);
        }
      }

      // Build achievement count map
      const achCountMap = {};
      achievements.forEach(ach => {
        const gid = ach.gameId || '';
        achCountMap[gid] = (achCountMap[gid] || 0) + 1;
      });

      // Build ROM list combining storage and database info
      allRoms = [];

      // Add known games
      games.forEach(game => {
        const romKey = game.romKey || game.name || '';
        const hasRom = romKeys.includes(romKey);
        const achCount = achCountMap[game.id] || 0;
        
        allRoms.push({
          id: game.id,
          title: game.title || game.commonName || game.name || romKey,
          subtitle: game.subtitle || '',
          romKey: romKey,
          system: game.system || game.platform || 'NES',
          present: hasRom,
          compatible: achCount > 0,
          achievements: achCount,
          notes: game.notes || '',
          size: 0,
          unknown: false
        });
      });

      // Add unknown ROMs (present in storage but not in DB)
      const knownRomKeys = new Set(games.map(g => g.romKey || g.name || '').filter(k => k));
      romKeys.forEach(romKey => {
        if (!knownRomKeys.has(romKey)) {
          allRoms.push({
            id: romKey,
            title: romKey.replace(/\.nes$/i, ''),
            subtitle: '',
            romKey: romKey,
            system: 'NES',
            present: true,
            compatible: false,
            achievements: 0,
            notes: 'Unknown ROM (not in database)',
            size: 0,
            unknown: true
          });
        }
      });

      // Load sizes for present ROMs
      await loadRomSizes();

      console.log('[RomManager] Total ROMs loaded:', allRoms.length);
    } catch (error) {
      console.error('[RomManager] Load ROMs error:', error);
      allRoms = [];
    }
  }

  async function getAllRomKeys() {
    try {
      // Use nesInterop.getStoredRoms() which returns array of {name, base64}
      if (window.nesInterop && typeof window.nesInterop.getStoredRoms === 'function') {
        const roms = await window.nesInterop.getStoredRoms();
        return roms.map(rom => rom.name);
      }
      
      return [];
    } catch (error) {
      console.error('[RomManager] Error getting ROM keys:', error);
      return [];
    }
  }

  async function loadRomSizes() {
    for (let rom of allRoms) {
      if (rom.present && rom.romKey) {
        try {
          const romData = await getRomData(rom.romKey);
          if (romData) {
            rom.size = romData.length || 0;
          }
        } catch (error) {
          console.error(`[RomManager] Error loading size for ${rom.romKey}:`, error);
        }
      }
    }
  }

  async function getRomData(romKey) {
    try {
      // getStoredRoms returns {name, base64}, find the matching one
      if (window.nesInterop && typeof window.nesInterop.getStoredRoms === 'function') {
        const roms = await window.nesInterop.getStoredRoms();
        const rom = roms.find(r => r.name === romKey);
        if (rom && rom.base64) {
          // Decode base64 to get size
          const bin = atob(rom.base64);
          return new Uint8Array(bin.length).map((_, i) => bin.charCodeAt(i));
        }
      }
      
      return null;
    } catch (error) {
      console.error(`[RomManager] Error getting ROM data for ${romKey}:`, error);
      return null;
    }
  }

  function renderRomList() {
    const container = elements.romListContainer;
    
    // Filter ROMs
    let filtered = allRoms.filter(rom => {
      // Filter by compatibility
      if (filterCompatibleOnly && !rom.compatible && !rom.unknown) {
        return false;
      }
      
      // Filter by search
      if (searchQuery && !rom.title.toLowerCase().includes(searchQuery)) {
        return false;
      }
      
      return true;
    });

    // Sort: unknown first (if filter off), then present, then compatible, then alphabetically
    filtered.sort((a, b) => {
      if (!filterCompatibleOnly) {
        if (a.unknown !== b.unknown) return b.unknown ? 1 : -1;
      }
      if (a.present !== b.present) return b.present ? 1 : -1;
      if (a.compatible !== b.compatible) return b.compatible ? 1 : -1;
      return a.title.localeCompare(b.title);
    });

    if (filtered.length === 0) {
      container.innerHTML = '<div class="rom-empty small-note">No ROMs found matching criteria.</div>';
      return;
    }

    // Build table
    let html = '<div class="rom-table" role="table" aria-label="ROM list">';
    html += '<div class="rom-thead" role="rowgroup">';
    html += '<div class="rom-tr rom-th" role="row">';
    html += '<div role="columnheader">Title</div>';
    html += '<div role="columnheader">System</div>';
    html += '<div role="columnheader">Compatible</div>';
    html += '<div role="columnheader">Stars</div>';
    html += '</div></div>';
    html += '<div class="rom-tbody" role="rowgroup">';

    filtered.forEach(rom => {
      const selected = rom.romKey === selectedRomKey ? 'selected' : '';
      const disabled = !rom.present ? 'disabled' : '';
      const compatChip = rom.unknown ? 
        '<span class="chip unknown">Unknown</span>' :
        (rom.compatible ? '<span class="chip ok">Yes</span>' : '<span class="chip no">No</span>');
      
      html += `<button type="button" class="rom-tr rom-td ${selected} ${disabled}" 
                role="row" data-rom-key="${escapeHtml(rom.romKey)}" 
                ${disabled ? 'disabled' : ''}>`;
      html += `<div role="cell">`;
      html += `<div class="rom-title">${escapeHtml(rom.title)}</div>`;
      if (rom.subtitle) {
        html += `<div class="rom-subtitle">${escapeHtml(rom.subtitle)}</div>`;
      }
      html += `</div>`;
      html += `<div role="cell">${escapeHtml(rom.system)}</div>`;
      html += `<div role="cell">${compatChip}</div>`;
      html += `<div role="cell">${rom.achievements}</div>`;
      html += `</button>`;
    });

    html += '</div></div>';
    container.innerHTML = html;

    // Attach click handlers
    container.querySelectorAll('.rom-td:not(.disabled)').forEach(btn => {
      btn.addEventListener('click', () => {
        const romKey = btn.getAttribute('data-rom-key');
        selectRom(romKey);
      });
    });
  }

  function selectRom(romKey) {
    selectedRomKey = romKey;
    const rom = allRoms.find(r => r.romKey === romKey);
    
    if (rom) {
      displayRomDetails(rom);
      renderRomList(); // Re-render to show selection
    }
  }

  function displayRomDetails(rom) {
    elements.detailTitle.textContent = rom.title;
    elements.detailFileName.textContent = rom.romKey;
    elements.detailSize.textContent = formatSize(rom.size);
    elements.detailSystem.textContent = rom.system;
    elements.detailGameId.textContent = rom.id;
    elements.detailAchievements.textContent = rom.achievements > 0 ? 
      `${rom.achievements} available` : 'None';
    elements.detailCompatible.textContent = rom.unknown ? 
      'Unknown' : (rom.compatible ? 'Yes' : 'No');
    
    elements.romDetailsSection.style.display = 'block';
  }

  function formatSize(bytes) {
    if (bytes === 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB'];
    let size = bytes;
    let unitIndex = 0;
    while (size >= 1024 && unitIndex < units.length - 1) {
      size /= 1024;
      unitIndex++;
    }
    return `${size.toFixed(1)} ${units[unitIndex]}`;
  }

  function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
  }

  // Import Modal Functions
  function openImportModal() {
    pendingImports = [];
    renderPendingImports();
    elements.importModal.style.display = 'flex';
  }

  function closeImportModal() {
    elements.importModal.style.display = 'none';
    pendingImports = [];
  }

  function handleFileSelect(e) {
    const files = Array.from(e.target.files || []);
    addFilesToPending(files);
    e.target.value = ''; // Reset input
  }

  function handleDrop(e) {
    const dt = e.dataTransfer;
    const files = Array.from(dt.files || []);
    addFilesToPending(files);
  }

  function addFilesToPending(files) {
    const nesFiles = files.filter(f => f.name.toLowerCase().endsWith('.nes'));
    
    nesFiles.forEach(file => {
      // Check if already in pending
      if (pendingImports.find(p => p.name === file.name)) {
        return;
      }
      
      pendingImports.push({
        name: file.name,
        size: file.size,
        file: file
      });
    });
    
    renderPendingImports();
  }

  function renderPendingImports() {
    const container = elements.pendingList;
    
    if (pendingImports.length === 0) {
      container.innerHTML = '<div class="small-note">No files added yet.</div>';
      elements.confirmImportBtn.disabled = true;
      return;
    }
    
    let html = '<ul class="import-list">';
    pendingImports.forEach(item => {
      html += `<li>${escapeHtml(item.name)} <span class="small-note">(${formatSize(item.size)})</span></li>`;
    });
    html += '</ul>';
    
    container.innerHTML = html;
    elements.confirmImportBtn.disabled = false;
  }

  async function confirmImport() {
    if (pendingImports.length === 0) return;
    
    try {
      console.log('[RomManager] Importing', pendingImports.length, 'ROMs...');
      
      for (let item of pendingImports) {
        await importRom(item);
      }
      
      console.log('[RomManager] Import complete');
      closeImportModal();
      
      // Reload ROMs and refresh display
      await loadRoms();
      renderRomList();
      
    } catch (error) {
      console.error('[RomManager] Import error:', error);
      alert('Error importing ROMs. See console for details.');
    }
  }

  async function importRom(item) {
    try {
      // Read file as base64
      const base64 = await readFileAsBase64(item.file);
      
      // Save to nesStorage using nesInterop
      if (window.nesInterop && typeof window.nesInterop.saveRom === 'function') {
        await window.nesInterop.saveRom(item.name, base64);
      } else {
        throw new Error('nesInterop not available');
      }
      
      // Add to continueDb if not present
      if (continueDb) {
        await addRomToDatabase(item.name, base64);
      }
      
      console.log('[RomManager] Imported:', item.name);
    } catch (error) {
      console.error(`[RomManager] Error importing ${item.name}:`, error);
      throw error;
    }
  }

  function readFileAsBase64(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => {
        const base64 = reader.result.split(',')[1];
        resolve(base64);
      };
      reader.onerror = () => reject(reader.error);
      reader.readAsDataURL(file);
    });
  }

  async function addRomToDatabase(name, base64) {
    try {
      // Parse ROM to get game ID
      const bin = atob(base64);
      const size = bin.length;
      const title = name.replace(/\.nes$/i, '');
      
      // Build Uint8Array
      const bytes = new Uint8Array(size);
      for (let i = 0; i < size; i++) {
        bytes[i] = bin.charCodeAt(i) & 0xFF;
      }
      
      // Compute game ID from PRG+CHR
      const gameId = await computeGameId(bytes);
      
      // Check if game already exists
      let game = await continueDb.get('games', gameId);
      
      if (!game) {
        // Add new game record
        game = {
          id: gameId,
          title: title,
          name: name,
          romKey: name,
          system: 'NES',
          platform: 'NES',
          commonName: title,
          notes: 'Imported via ROM Manager'
        };
        
        await continueDb.put('games', game);
        console.log('[RomManager] Added game to database:', gameId);
      } else {
        // Update romKey if not set
        if (!game.romKey) {
          game.romKey = name;
          await continueDb.put('games', game);
        }
      }
    } catch (error) {
      console.error('[RomManager] Error adding ROM to database:', error);
    }
  }

  async function computeGameId(bytes) {
    try {
      // Parse iNES header
      if (bytes.length < 16) return `unknown_${Date.now()}`;
      if (!(bytes[0] === 0x4E && bytes[1] === 0x45 && bytes[2] === 0x53 && bytes[3] === 0x1A)) {
        return `unknown_${Date.now()}`;
      }
      
      const prg16 = bytes[4];
      const chr8 = bytes[5];
      const f6 = bytes[6];
      const hasTrainer = (f6 & 0x04) !== 0;
      
      let offset = 16 + (hasTrainer ? 512 : 0);
      let prgBytes = prg16 * 16384;
      let chrBytes = chr8 * 8192;
      
      if (offset + prgBytes + chrBytes > bytes.length) {
        prgBytes = Math.max(0, Math.min(prgBytes, bytes.length - offset));
        chrBytes = Math.max(0, Math.min(chrBytes, bytes.length - offset - prgBytes));
      }
      
      const prg = bytes.slice(offset, offset + prgBytes);
      const chr = bytes.slice(offset + prgBytes, offset + prgBytes + chrBytes);
      
      // Concatenate PRG+CHR
      const concat = new Uint8Array(prg.length + chr.length);
      concat.set(prg, 0);
      if (chr.length > 0) {
        concat.set(chr, prg.length);
      }
      
      // Compute SHA-1
      const hashBuffer = await crypto.subtle.digest('SHA-1', concat);
      const hashArray = new Uint8Array(hashBuffer);
      const hashHex = Array.from(hashArray).map(b => b.toString(16).padStart(2, '0')).join('');
      
      return `nes_${hashHex}`;
    } catch (error) {
      console.error('[RomManager] Error computing game ID:', error);
      return `unknown_${Date.now()}`;
    }
  }

  // Delete Modal Functions
  function openDeleteModal() {
    if (!selectedRomKey) return;
    
    const rom = allRoms.find(r => r.romKey === selectedRomKey);
    if (!rom) return;
    
    romToDelete = rom;
    elements.deleteMessage.textContent = `Are you sure you want to delete "${rom.title}"?`;
    elements.deleteModal.style.display = 'flex';
  }

  function closeDeleteModal() {
    elements.deleteModal.style.display = 'none';
    romToDelete = null;
  }

  async function confirmDelete() {
    if (!romToDelete) return;
    
    try {
      console.log('[RomManager] Deleting ROM:', romToDelete.romKey);
       using nesInterop
      if (window.nesInterop && typeof window.nesInterop.removeStoredRom === 'function') {
        await window.nesInterop.removeStoredRom(romToDelete.romKey);
      } else {
        throw new Error('nesInterop not available'
        await window.nesStorage.removeItem(romToDelete.romKey);
      }
      
      console.log('[RomManager] ROM deleted successfully');
      closeDeleteModal();
      
      // Clear selection if deleted ROM was selected
      if (selectedRomKey === romToDelete.romKey) {
        selectedRomKey = null;
        elements.romDetailsSection.style.display = 'none';
      }
      
      // Reload and refresh
      await loadRoms();
      renderRomList();
      
    } catch (error) {
      console.error('[RomManager] Delete error:', error);
      alert('Error deleting ROM. See console for details.');
    }
  }

  function playBackgroundMusic() {
    try {
      const musicTrack = 'RomManager.mp3';
      
      if (window.webapi?.audio?.requestMusic) {
        window.webapi.audio.requestMusic(musicTrack, true, 800).catch(error => {
          console.warn('[RomManager] Music request error (track may not exist):', error);
          // Fallback to another track
          window.webapi.audio.requestMusic('TitleScreen.mp3', true, 800).catch(err => {
            console.warn('[RomManager] Fallback music also failed:', err);
          });
        });
      }
    } catch (error) {
      console.error('[RomManager] Music playback error:', error);
    }
  }

  // Expose API for debugging
  window.romManager = {
    getRoms: () => allRoms,
    getSelectedRom: () => allRoms.find(r => r.romKey === selectedRomKey),
    reload: init
  };
})();
