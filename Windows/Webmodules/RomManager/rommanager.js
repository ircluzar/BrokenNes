// rommanager.js - ROM Manager main logic for WinForms webmodule

(function() {
  'use strict';

  const DEFAULT_DB_URL = '../shared/models/default-db.json';
  const ROM_STORAGE_DB_NAME = 'nesStorage';
  const ROM_STORAGE_DB_VERSION = 1;
  const ROM_STORAGE_STORE = 'roms';
  const LEGACY_ROM_KEY_PREFIX = 'rom_';

  // State
  let allRoms = [];
  let selectedRomKey = null;
  let filterCompatibleOnly = true;
  let searchQuery = '';
  let pendingImports = [];
  let continueDb = null;
  let romToDelete = null;
  let catalogAchievements = [];
  let savedAchievementIds = new Set();
  let masqueradeMap = {};

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
      romManagerLayout: document.getElementById('romManagerLayout'),
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
      detailMasqueradeStatus: document.getElementById('detailMasqueradeStatus'),
      forceImportBtn: document.getElementById('forceImportBtn'),
      showAchievementsBtn: document.getElementById('showAchievementsBtn'),
      forceImportFileInput: document.getElementById('forceImportFileInput'),
      
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
      cancelDeleteBtn: document.getElementById('cancelDeleteBtn'),

      // Achievements modal
      achievementsModal: document.getElementById('achievementsModal'),
      achievementsSummary: document.getElementById('achievementsSummary'),
      achievementsList: document.getElementById('achievementsList'),
      closeAchievementsBtn: document.getElementById('closeAchievementsBtn')
    };

    if (elements.filterCheckbox) {
      elements.filterCheckbox.checked = filterCompatibleOnly;
    }

    updateLayoutState(false);
  }

  function updateLayoutState(hasDetails) {
    if (!elements.romManagerLayout) {
      return;
    }

    elements.romManagerLayout.classList.toggle('has-details', hasDetails);
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
    elements.forceImportBtn.addEventListener('click', openForceImportDialog);
    elements.showAchievementsBtn.addEventListener('click', openAchievementsModal);
    elements.forceImportFileInput.addEventListener('change', handleForceImportFileSelect);

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

    // Achievements modal
    elements.closeAchievementsBtn.addEventListener('click', closeAchievementsModal);
    elements.achievementsModal.addEventListener('click', (e) => {
      if (e.target === elements.achievementsModal) closeAchievementsModal();
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

      const { games, achievements, source } = await loadCatalogRecords();
      catalogAchievements = Array.isArray(achievements) ? achievements : [];
      console.log(`[RomManager] Loaded catalog from ${source}:`, {
        games: games.length,
        achievements: achievements.length
      });

      const save = await loadGameSaveSnapshot();
      savedAchievementIds = new Set(Array.isArray(save?.Achievements) ? save.Achievements : []);
      masqueradeMap = normalizeMasqueradeMap(save?.MasqueradeRomToGameId);

      // Build achievement count map
      const achCountMap = {};
      achievements.forEach(ach => {
        const gid = normalizeValue(ach?.gameId);
        achCountMap[gid] = (achCountMap[gid] || 0) + 1;
      });

      const unlockedCountByGameId = buildUnlockedCountByGameId(achievements, savedAchievementIds);

      // Build ROM list combining storage and database info
      allRoms = [];

      // Add known games
      games.forEach(game => {
        const romKey = game.romKey || game.name || '';
        const hasRom = romKeys.includes(romKey);
        const achCount = achCountMap[game.id] || 0;
        const unlockedCount = unlockedCountByGameId[game.id] || 0;
        
        allRoms.push({
          id: game.id,
          title: game.commonName || game.title || game.name || romKey,
          subtitle: game.subtitle || '',
          romKey: romKey,
          system: game.system || game.platform || 'NES',
          present: hasRom,
          compatible: achCount > 0,
          achievements: achCount,
          achievementsTotal: achCount,
          achievementsUnlocked: Math.min(unlockedCount, achCount),
          notes: game.notes || game.note || '',
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
            achievementsTotal: 0,
            achievementsUnlocked: 0,
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

  async function loadCatalogRecords() {
    let games = [];
    let achievements = [];

    if (continueDb && typeof continueDb.getAll === 'function') {
      try {
        const [dbGames, dbAchievements] = await Promise.all([
          continueDb.getAll('games'),
          continueDb.getAll('achievements')
        ]);

        games = Array.isArray(dbGames) ? dbGames : [];
        achievements = Array.isArray(dbAchievements) ? dbAchievements : [];

        if (games.length > 0 || achievements.length > 0) {
          return { games, achievements, source: 'continueDb' };
        }

        console.warn('[RomManager] continueDb returned an empty catalog, falling back to bundled default DB');
      } catch (error) {
        console.error('[RomManager] Error loading catalog from continueDb:', error);
      }
    }

    try {
      const response = await fetch(DEFAULT_DB_URL, { cache: 'no-cache' });
      if (!response.ok) {
        throw new Error(`Default DB fetch failed with status ${response.status}`);
      }

      const payload = await response.json();
      const data = payload && typeof payload === 'object' ? payload.data || {} : {};
      games = Array.isArray(data.games) ? data.games : [];
      achievements = Array.isArray(data.achievements) ? data.achievements : [];

      return { games, achievements, source: 'default-db.json' };
    } catch (error) {
      console.error('[RomManager] Error loading fallback default DB:', error);
      return { games: [], achievements: [], source: 'none' };
    }
  }

  async function openRomStorageStore(mode) {
    return new Promise((resolve, reject) => {
      if (!window.indexedDB) {
        reject(new Error('IndexedDB not available'));
        return;
      }

      const request = indexedDB.open(ROM_STORAGE_DB_NAME, ROM_STORAGE_DB_VERSION);
      request.onupgradeneeded = () => {
        const db = request.result;
        if (!db.objectStoreNames.contains('kv')) {
          db.createObjectStore('kv');
        }
        if (!db.objectStoreNames.contains(ROM_STORAGE_STORE)) {
          db.createObjectStore(ROM_STORAGE_STORE, { keyPath: 'name' });
        }
      };
      request.onsuccess = () => {
        const db = request.result;
        try {
          resolve(db.transaction(ROM_STORAGE_STORE, mode).objectStore(ROM_STORAGE_STORE));
        } catch (error) {
          reject(error);
        }
      };
      request.onerror = () => reject(request.error || new Error('IndexedDB open error'));
    });
  }

  async function migrateLegacyLocalStorageRoms() {
    try {
      if (!window.localStorage) {
        return;
      }

      const legacyKeys = [];
      for (let index = 0; index < localStorage.length; index++) {
        const key = localStorage.key(index);
        if (key && key.startsWith(LEGACY_ROM_KEY_PREFIX)) {
          legacyKeys.push(key);
        }
      }

      if (legacyKeys.length === 0) {
        return;
      }

      const existing = await getStoredRomsFromIndexedDb(false);
      const existingNames = new Set(existing.map(rom => rom.name));

      for (const key of legacyKeys) {
        const base64 = localStorage.getItem(key);
        const name = key.substring(LEGACY_ROM_KEY_PREFIX.length);
        if (!base64 || !name || existingNames.has(name)) {
          continue;
        }

        await saveRomToIndexedDb(name, base64);
        try {
          localStorage.removeItem(key);
        } catch {
          // ignore legacy cleanup failures
        }
      }
    } catch (error) {
      console.warn('[RomManager] Legacy ROM migration failed:', error);
    }
  }

  async function getStoredRomsFromIndexedDb(runMigration = true) {
    try {
      if (runMigration) {
        await migrateLegacyLocalStorageRoms();
      }

      const store = await openRomStorageStore('readonly');
      return await new Promise((resolve, reject) => {
        if ('getAll' in store) {
          const request = store.getAll();
          request.onsuccess = () => resolve(request.result || []);
          request.onerror = () => reject(request.error);
          return;
        }

        const records = [];
        const request = store.openCursor();
        request.onsuccess = (event) => {
          const cursor = event.target.result;
          if (cursor) {
            records.push(cursor.value);
            cursor.continue();
          } else {
            resolve(records);
          }
        };
        request.onerror = () => reject(request.error);
      });
    } catch (error) {
      console.warn('[RomManager] IndexedDB ROM read failed, checking legacy localStorage:', error);
      return getStoredRomsFromLegacyLocalStorage();
    }
  }

  function getStoredRomsFromLegacyLocalStorage() {
    try {
      if (!window.localStorage) {
        return [];
      }

      const records = [];
      for (let index = 0; index < localStorage.length; index++) {
        const key = localStorage.key(index);
        if (!key || !key.startsWith(LEGACY_ROM_KEY_PREFIX)) {
          continue;
        }

        const base64 = localStorage.getItem(key);
        if (!base64) {
          continue;
        }

        records.push({
          name: key.substring(LEGACY_ROM_KEY_PREFIX.length),
          base64
        });
      }

      return records;
    } catch (error) {
      console.warn('[RomManager] Legacy ROM fallback read failed:', error);
      return [];
    }
  }

  async function getStoredRoms() {
    if (window.nesInterop && typeof window.nesInterop.getStoredRoms === 'function') {
      return window.nesInterop.getStoredRoms();
    }

    return getStoredRomsFromIndexedDb();
  }

  async function saveRomToIndexedDb(name, base64) {
    const store = await openRomStorageStore('readwrite');
    return new Promise((resolve, reject) => {
      const request = store.put({ name, base64 });
      request.onsuccess = () => resolve();
      request.onerror = () => reject(request.error);
    });
  }

  async function saveStoredRom(name, base64) {
    if (window.nesInterop && typeof window.nesInterop.saveRom === 'function') {
      await window.nesInterop.saveRom(name, base64);
      return;
    }

    try {
      await saveRomToIndexedDb(name, base64);
    } catch (error) {
      console.warn('[RomManager] IndexedDB ROM save failed, falling back to localStorage:', error);
      if (!window.localStorage) {
        throw error;
      }
      localStorage.setItem(`${LEGACY_ROM_KEY_PREFIX}${name}`, base64);
    }
  }

  async function removeStoredRom(name) {
    if (window.nesInterop && typeof window.nesInterop.removeStoredRom === 'function') {
      await window.nesInterop.removeStoredRom(name);
      return;
    }

    try {
      const store = await openRomStorageStore('readwrite');
      await new Promise((resolve, reject) => {
        const request = store.delete(name);
        request.onsuccess = () => resolve();
        request.onerror = () => reject(request.error);
      });
    } catch (error) {
      console.warn('[RomManager] IndexedDB ROM delete failed, falling back to localStorage:', error);
    }

    try {
      localStorage.removeItem(`${LEGACY_ROM_KEY_PREFIX}${name}`);
    } catch {
      // ignore localStorage cleanup failures
    }
  }

  async function getAllRomKeys() {
    try {
      const roms = await getStoredRoms();
      return roms.map(rom => rom.name);
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
      const roms = await getStoredRoms();
      const rom = roms.find(r => r.name === romKey);
      if (rom && rom.base64) {
        const bin = atob(rom.base64);
        return new Uint8Array(bin.length).map((_, i) => bin.charCodeAt(i));
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
      // Default mode is an import catalog: show all challenge-compatible games,
      // even when the ROM has not been imported yet.
      if (filterCompatibleOnly) {
        if (!rom.compatible) {
          return false;
        }
      }
      
      // Filter by search
      if (searchQuery && !rom.title.toLowerCase().includes(searchQuery)) {
        return false;
      }
      
      return true;
    });

    // Sort for discoverability: compatible games first, then installed ones,
    // then by star count and title. Unknown imported ROMs sink to the bottom.
    filtered.sort((a, b) => {
      if (a.compatible !== b.compatible) return b.compatible ? 1 : -1;
      if (a.present !== b.present) return b.present ? 1 : -1;
      if (a.achievementsTotal !== b.achievementsTotal) return b.achievementsTotal - a.achievementsTotal;
      if (a.unknown !== b.unknown) return a.unknown ? 1 : -1;
      return a.title.localeCompare(b.title);
    });

    if (filtered.length === 0) {
      container.innerHTML = filterCompatibleOnly
        ? '<div class="rom-empty small-note">No challenge-compatible ROMs were found in continueDb.</div>'
        : '<div class="rom-empty small-note">No ROMs found matching criteria.</div>';
      return;
    }

    // Build table
    let html = '<div class="rom-table" role="table" aria-label="ROM list">';
    html += '<div class="rom-thead" role="rowgroup">';
    html += '<div class="rom-tr rom-th" role="row">';
    html += '<div role="columnheader">Title</div>';
    html += '<div role="columnheader">System</div>';
    html += '<div role="columnheader" title="Compatible challenges found in continueDb">Stars</div>';
    html += '</div></div>';
    html += '<div class="rom-tbody" role="rowgroup">';

    filtered.forEach(rom => {
      const selected = rom.romKey === selectedRomKey ? 'selected' : '';
      const installed = rom.present ? 'installed' : '';
      const notPresent = !rom.present ? 'not-present' : '';
      const availabilityLine = rom.present
        ? '<div class="rom-subtitle rom-availability">Installed</div>'
        : '<div class="rom-subtitle rom-availability">Not imported yet</div>';
      const starClass = rom.achievementsTotal > 0 ? 'rom-star-count' : 'rom-star-count zero';
      const starsText = rom.present
        ? `${rom.achievementsUnlocked}/${rom.achievementsTotal}`
        : `${rom.achievementsTotal}`;
      
      html += `<button type="button" class="rom-tr rom-td ${selected} ${installed} ${notPresent}" 
                role="row" data-rom-key="${escapeHtml(rom.romKey)}">`;
      html += `<div role="cell">`;
      html += `<div class="rom-title">${escapeHtml(rom.title)}</div>`;
      if (rom.subtitle) {
        html += `<div class="rom-subtitle">${escapeHtml(rom.subtitle)}</div>`;
      }
      html += availabilityLine;
      html += `</div>`;
      html += `<div role="cell">${escapeHtml(rom.system)}</div>`;
      html += `<div role="cell"><span class="${starClass}">${starsText}</span></div>`;
      html += `</button>`;
    });

    html += '</div></div>';
    container.innerHTML = html;

    // Attach click handlers
    container.querySelectorAll('.rom-td').forEach(btn => {
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
    const isMasqueraded = isRomMasqueraded(rom.romKey, rom.id);
    elements.detailTitle.textContent = rom.title;
    elements.detailFileName.textContent = rom.romKey;
    elements.detailSize.textContent = rom.present ? formatSize(rom.size) : 'Not imported';
    elements.detailSystem.textContent = rom.system;
    elements.detailGameId.textContent = rom.id;
    elements.detailAchievements.textContent = rom.present
      ? `${rom.achievementsUnlocked}/${rom.achievementsTotal} stars`
      : `${rom.achievementsTotal} stars`;
    elements.detailMasqueradeStatus.textContent = isMasqueraded ? 'Masquaraded' : 'Standard';
    elements.detailMasqueradeStatus.classList.toggle('rom-status-masqueraded', isMasqueraded);
    elements.detailMasqueradeStatus.classList.toggle('rom-status-standard', !isMasqueraded);
    elements.deleteBtn.disabled = !rom.present;
    elements.deleteBtn.title = rom.present ? 'Delete imported ROM' : 'This ROM is not installed yet';
    
    elements.romDetailsSection.style.display = 'block';
    updateLayoutState(true);
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
      
      await saveStoredRom(item.name, base64);
      
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
      const deletedRomKey = romToDelete.romKey;
      console.log('[RomManager] Deleting ROM:', deletedRomKey);
      await removeStoredRom(deletedRomKey);
      await setMasqueradeStatus(deletedRomKey, null);
      
      console.log('[RomManager] ROM deleted successfully');
      closeDeleteModal();
      
      // Clear selection if deleted ROM was selected
      if (selectedRomKey === deletedRomKey) {
        selectedRomKey = null;
        elements.romDetailsSection.style.display = 'none';
        updateLayoutState(false);
      }
      
      // Reload and refresh
      await loadRoms();
      renderRomList();
      
    } catch (error) {
      console.error('[RomManager] Delete error:', error);
      alert('Error deleting ROM. See console for details.');
    }
  }

  async function loadGameSaveSnapshot() {
    try {
      if (!window.gameSave || typeof window.gameSave.load !== 'function') {
        return null;
      }

      return await window.gameSave.load();
    } catch (error) {
      console.warn('[RomManager] Failed to load game save:', error);
      return null;
    }
  }

  function normalizeMasqueradeMap(value) {
    if (!value || typeof value !== 'object') {
      return {};
    }

    const map = {};
    Object.entries(value).forEach(([key, gameId]) => {
      const normalizedKey = normalizeValue(key);
      const normalizedGameId = normalizeValue(gameId);
      if (!normalizedKey || !normalizedGameId) {
        return;
      }

      map[normalizedKey.toLowerCase()] = normalizedGameId;
    });

    return map;
  }

  function normalizeValue(value) {
    return typeof value === 'string' ? value.trim() : '';
  }

  function buildUnlockedCountByGameId(achievements, unlockedIds) {
    const unlockedCount = {};
    const unlockedSet = unlockedIds instanceof Set ? unlockedIds : new Set();

    (Array.isArray(achievements) ? achievements : []).forEach(achievement => {
      const gameId = normalizeValue(achievement?.gameId);
      const achievementId = normalizeValue(achievement?.id || achievement?.achievementId);
      if (!gameId || !achievementId) {
        return;
      }

      if (unlockedSet.has(achievementId)) {
        unlockedCount[gameId] = (unlockedCount[gameId] || 0) + 1;
      }
    });

    return unlockedCount;
  }

  function isRomMasqueraded(romKey, gameId) {
    const normalizedKey = normalizeValue(romKey).toLowerCase();
    const mappedGameId = masqueradeMap[normalizedKey];
    return Boolean(mappedGameId && normalizeValue(mappedGameId) === normalizeValue(gameId));
  }

  async function setMasqueradeStatus(romKey, gameId) {
    try {
      if (!window.gameSave || typeof window.gameSave.load !== 'function' || typeof window.gameSave.save !== 'function') {
        return false;
      }

      const normalizedKey = normalizeValue(romKey);
      if (!normalizedKey) {
        return false;
      }

      const save = await window.gameSave.load();
      save.MasqueradeRomToGameId = normalizeMasqueradeMap(save.MasqueradeRomToGameId);

      if (gameId) {
        save.MasqueradeRomToGameId[normalizedKey.toLowerCase()] = gameId;
      } else {
        delete save.MasqueradeRomToGameId[normalizedKey.toLowerCase()];
      }

      const ok = await window.gameSave.save(save);
      if (ok) {
        masqueradeMap = normalizeMasqueradeMap(save.MasqueradeRomToGameId);
      }

      return ok;
    } catch (error) {
      console.error('[RomManager] Failed to update masquerade status:', error);
      return false;
    }
  }

  function openForceImportDialog() {
    if (!selectedRomKey) {
      return;
    }

    if (elements.forceImportFileInput) {
      elements.forceImportFileInput.value = '';
      elements.forceImportFileInput.click();
    }
  }

  async function handleForceImportFileSelect(e) {
    const file = (e.target?.files || [])[0];
    e.target.value = '';
    if (!file || !selectedRomKey) {
      return;
    }

    const rom = allRoms.find(entry => entry.romKey === selectedRomKey);
    if (!rom) {
      return;
    }

    try {
      const base64 = await readFileAsBase64(file);
      await saveStoredRom(rom.romKey, base64);
      await setMasqueradeStatus(rom.romKey, rom.id);

      await loadRoms();
      const reselected = allRoms.find(entry => entry.romKey === rom.romKey);
      if (reselected) {
        selectedRomKey = reselected.romKey;
        displayRomDetails(reselected);
      }
      renderRomList();
    } catch (error) {
      console.error('[RomManager] Force import failed:', error);
      alert('Error force importing ROM. See console for details.');
    }
  }

  function openAchievementsModal() {
    if (!selectedRomKey) {
      return;
    }

    const rom = allRoms.find(entry => entry.romKey === selectedRomKey);
    if (!rom) {
      return;
    }

    const mappedGameId = masqueradeMap[normalizeValue(rom.romKey).toLowerCase()];
    const targetGameId = normalizeValue(mappedGameId) || rom.id;
    const list = catalogAchievements.filter(achievement => normalizeValue(achievement?.gameId) === targetGameId);
    const unlockedSet = savedAchievementIds instanceof Set ? savedAchievementIds : new Set();
    const unlockedCount = list.reduce((count, achievement) => {
      const achievementId = normalizeValue(achievement?.id || achievement?.achievementId);
      return achievementId && unlockedSet.has(achievementId) ? count + 1 : count;
    }, 0);

    elements.achievementsSummary.textContent = `${unlockedCount}/${list.length} unlocked`;

    if (list.length === 0) {
      elements.achievementsList.innerHTML = '<div class="small-note">No achievements found for this game.</div>';
    } else {
      let html = '';
      list.forEach(achievement => {
        const title = normalizeValue(achievement?.title) || normalizeValue(achievement?.name) || 'Untitled achievement';
        const achievementId = normalizeValue(achievement?.id || achievement?.achievementId);
        const unlocked = Boolean(achievementId && unlockedSet.has(achievementId));
        html += `<div class="achievement-row ${unlocked ? 'unlocked' : 'locked'}">`;
        html += `<div class="achievement-title">${escapeHtml(title)}</div>`;
        html += `<div class="achievement-state">${unlocked ? 'Unlocked' : 'Locked'}</div>`;
        html += '</div>';
      });

      elements.achievementsList.innerHTML = html;
    }

    elements.achievementsModal.style.display = 'flex';
  }

  function closeAchievementsModal() {
    elements.achievementsModal.style.display = 'none';
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
