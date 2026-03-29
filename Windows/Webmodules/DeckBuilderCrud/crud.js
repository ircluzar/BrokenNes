// crud.js - Database Editor for BrokenNes (WebModule port from Blazor)

(function() {
  'use strict';

  const ROM_STORAGE_DB_NAME = 'nesStorage';
  const ROM_STORAGE_DB_VERSION = 1;
  const ROM_STORAGE_STORE = 'roms';
  const LEGACY_ROM_KEY_PREFIX = 'rom_';

  // State
  let activeTab = 'games';
  let games = [];
  let achievements = [];
  let cards = [];
  let metaRows = [];
  let levels = [];
  let gameOptions = [];
  let cardOptions = [];
  let metaOptionsByGameTitle = {};
  let gameIdByLabel = {};
  let gameLabelById = {};
  let cardLabelById = {};
  let autoSeedEnabled = true;
  let columnVisibility = {
    id: false,
    commonName: true,
    status: true,
    note: true,
    romKey: false,
    achievementCount: true,
    installedRom: true,
    deckEligible: true,
    system: false,
    builtIn: true,
    size: false
  };

  const STATUS_OPTIONS = ['Nothing', 'Broken', 'Jank', 'Works'];
  const CARD_TYPE_OPTIONS = ['Reserved', 'Random', 'Last'];

  // Initialize on page load
  window.addEventListener('DOMContentLoaded', init);

  async function init() {
    try {
      // Start pixel background animation
      if (window.homePixelBgEnsure) {
        window.homePixelBgEnsure();
      }

      // Initialize continueDb
      if (!window.continueDb) {
        console.error('[CRUD] continueDb is not available');
        alert('Database system not available. Please check console.');
        return;
      }

      await window.continueDb.open();

      // Load column preferences
      loadColumnPreferences();

      // Load auto-seed setting
      autoSeedEnabled = await window.continueDb.getAutoSeedEnabled();
      updateAutoReloadButton();

      // Setup UI event listeners
      setupEventListeners();

      // Load initial tab
      await loadGames();

      console.log('[CRUD] Initialized successfully');
    } catch (error) {
      console.error('[CRUD] Initialization error:', error);
      alert('Initialization failed: ' + error.message);
    }
  }

  function setupEventListeners() {
    // Tab switching
    document.querySelectorAll('.tab-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        const tab = btn.dataset.tab;
        switchTab(tab);
      });
    });

    // Toolbar buttons
    document.getElementById('btnExportJson').addEventListener('click', exportJson);
    document.getElementById('btnImportJson').addEventListener('click', triggerImportDialog);
    document.getElementById('crud-import').addEventListener('change', importJson);
    document.getElementById('btnAutoReload').addEventListener('click', toggleAutoReload);
    document.getElementById('btnColumnPicker').addEventListener('click', toggleColumnPicker);

    // Column checkboxes
    document.getElementById('colId').addEventListener('change', (e) => updateColumn('id', e.target.checked));
    document.getElementById('colCommonName').addEventListener('change', (e) => updateColumn('commonName', e.target.checked));
    document.getElementById('colStatus').addEventListener('change', (e) => updateColumn('status', e.target.checked));
    document.getElementById('colNote').addEventListener('change', (e) => updateColumn('note', e.target.checked));
    document.getElementById('colRomKey').addEventListener('change', (e) => updateColumn('romKey', e.target.checked));
    document.getElementById('colAchievementCount').addEventListener('change', (e) => updateColumn('achievementCount', e.target.checked));
    document.getElementById('colInstalledRom').addEventListener('change', (e) => updateColumn('installedRom', e.target.checked));
    document.getElementById('colDeckEligible').addEventListener('change', (e) => updateColumn('deckEligible', e.target.checked));
    document.getElementById('colSystem').addEventListener('change', (e) => updateColumn('system', e.target.checked));
    document.getElementById('colBuiltIn').addEventListener('change', (e) => updateColumn('builtIn', e.target.checked));
    document.getElementById('colSize').addEventListener('change', (e) => updateColumn('size', e.target.checked));

    // Games tab
    document.getElementById('btnRefreshGames').addEventListener('click', loadGames);

    // Achievements tab
    document.getElementById('btnRefreshAchievements').addEventListener('click', loadAchievements);
    document.getElementById('btnAddAchievement').addEventListener('click', addAchievementRow);

    // Cards tab
    document.getElementById('btnRefreshCards').addEventListener('click', refreshCards);

    // Meta tab
    document.getElementById('btnRefreshMeta').addEventListener('click', loadMeta);
    document.getElementById('btnTestAllFormulas').addEventListener('click', testAllFormulas);
    document.getElementById('metaFilterTitle').addEventListener('change', (e) => {
      loadMeta();
    });

    // Levels tab
    document.getElementById('btnRefreshLevels').addEventListener('click', loadLevels);
    document.getElementById('btnAddLevel').addEventListener('click', addLevelRow);

    // Update column checkboxes from stored preferences
    document.getElementById('colId').checked = columnVisibility.id;
    document.getElementById('colCommonName').checked = columnVisibility.commonName;
    document.getElementById('colStatus').checked = columnVisibility.status;
    document.getElementById('colNote').checked = columnVisibility.note;
    document.getElementById('colRomKey').checked = columnVisibility.romKey;
    document.getElementById('colAchievementCount').checked = columnVisibility.achievementCount;
    document.getElementById('colInstalledRom').checked = columnVisibility.installedRom;
    document.getElementById('colDeckEligible').checked = columnVisibility.deckEligible;
    document.getElementById('colSystem').checked = columnVisibility.system;
    document.getElementById('colBuiltIn').checked = columnVisibility.builtIn;
    document.getElementById('colSize').checked = columnVisibility.size;
  }

  function switchTab(tab) {
    activeTab = tab;

    // Update tab buttons
    document.querySelectorAll('.tab-btn').forEach(btn => {
      btn.classList.toggle('active', btn.dataset.tab === tab);
    });

    // Update tab content
    document.querySelectorAll('.tab-content').forEach(content => {
      content.classList.toggle('active', content.id === `tab-${tab}`);
    });

    // Load data for the active tab
    if (tab === 'games') loadGames();
    else if (tab === 'achievements') loadAchievements();
    else if (tab === 'cards') refreshCards();
    else if (tab === 'meta') loadMeta();
    else if (tab === 'levels') loadLevels();
  }

  // ===== COLUMN MANAGEMENT =====
  function loadColumnPreferences() {
    try {
      const json = localStorage.getItem('crud:gamesCols');
      if (json) {
        const loaded = JSON.parse(json);
        columnVisibility = {
          id: loaded.id || false,
          commonName: loaded.commonName !== false,
          status: loaded.status !== false,
          note: loaded.note !== false,
          romKey: loaded.romKey || false,
          achievementCount: loaded.achievementCount !== false,
          installedRom: loaded.installedRom !== false,
          deckEligible: loaded.deckEligible !== false,
          system: loaded.system || false,
          builtIn: loaded.builtIn !== false,
          size: loaded.size || false
        };
      }
    } catch (error) {
      console.warn('[CRUD] Failed to load column preferences:', error);
    }
  }

  function updateColumn(name, value) {
    columnVisibility[name] = value;
    saveColumnPreferences();
    if (activeTab === 'games') loadGames();
  }

  function saveColumnPreferences() {
    try {
      localStorage.setItem('crud:gamesCols', JSON.stringify(columnVisibility));
    } catch (error) {
      console.warn('[CRUD] Failed to save column preferences:', error);
    }
  }

  function toggleColumnPicker() {
    const picker = document.getElementById('columnPicker');
    picker.style.display = picker.style.display === 'none' ? 'block' : 'none';
  }

  // ===== TOOLBAR ACTIONS =====
  async function exportJson() {
    try {
      await window.continueDb.exportAllToDownload();
    } catch (error) {
      console.error('[CRUD] Export failed:', error);
      alert('Export failed: ' + error.message);
    }
  }

  function triggerImportDialog() {
    document.getElementById('crud-import').click();
  }

  async function importJson() {
    try {
      await window.continueDb.importFromFileInput();
      await loadGames();
      if (activeTab === 'achievements') await loadAchievements();
      if (activeTab === 'levels') await loadLevels();
    } catch (error) {
      console.error('[CRUD] Import failed:', error);
      alert('Import failed: ' + error.message);
    }
  }

  async function toggleAutoReload() {
    autoSeedEnabled = !autoSeedEnabled;
    try {
      await window.continueDb.setAutoSeedEnabled(autoSeedEnabled);
      updateAutoReloadButton();
    } catch (error) {
      console.error('[CRUD] Toggle auto-reload failed:', error);
    }
  }

  function updateAutoReloadButton() {
    const btn = document.getElementById('btnAutoReload');
    btn.classList.toggle('active', autoSeedEnabled);
  }

  // ===== GAMES TAB =====
  async function loadGames() {
    if (activeTab !== 'games') return;

    const container = document.getElementById('gamesGrid');
    container.innerHTML = '<div class="grid-empty">Loading…</div>';

    try {
      await window.continueDb.open();
      const [arr, achArr, storedRoms] = await Promise.all([
        window.continueDb.getAll('games'),
        window.continueDb.getAll('achievements'),
        getStoredRoms()
      ]);
      arr.sort((a, b) => getDisplayTitle(a).localeCompare(getDisplayTitle(b)));

      const achievementsByGameId = new Map();
      achArr.forEach(achievement => {
        const gameId = achievement && achievement.gameId;
        if (!gameId) {
          return;
        }

        if (!achievementsByGameId.has(gameId)) {
          achievementsByGameId.set(gameId, []);
        }

        achievementsByGameId.get(gameId).push(achievement);
      });

      const installedRomKeys = new Set(
        (Array.isArray(storedRoms) ? storedRoms : [])
          .map(rom => normalizeRomStorageName(rom && rom.name))
          .filter(Boolean)
      );

      games = arr.map(g => ({
        ...g,
        title: getDisplayTitle(g),
        commonName: getCommonName(g),
        status: g.status || 'Nothing',
        note: getGameNote(g),
        romKey: getRomKey(g),
        system: getSystemLabel(g),
        linkedAchievementCount: (achievementsByGameId.get(g.id) || []).length,
        installedRom: installedRomKeys.has(normalizeRomStorageName(getRomKey(g))),
        deckEligible: isDeckEligible(g, achievementsByGameId, installedRomKeys),
        deckEligibilityReason: getDeckEligibilityReason(g, achievementsByGameId, installedRomKeys),
        isEditing: false
      }));

      renderGames();
    } catch (error) {
      console.error('[CRUD] Load games failed:', error);
      container.innerHTML = '<div class="grid-empty">Failed to load games.</div>';
    }
  }

  function renderGames() {
    const container = document.getElementById('gamesGrid');

    if (games.length === 0) {
      container.innerHTML = '<div class="grid-empty">No records yet.</div>';
      return;
    }

    const cols = [];
    if (columnVisibility.id) cols.push('ID');
    cols.push('Title');
    if (columnVisibility.commonName) cols.push('Common Name');
    if (columnVisibility.status) cols.push('Status');
    if (columnVisibility.note) cols.push('Note');
    if (columnVisibility.romKey) cols.push('ROM Key');
    if (columnVisibility.achievementCount) cols.push('Linked Achievements');
    if (columnVisibility.installedRom) cols.push('ROM Installed');
    if (columnVisibility.deckEligible) cols.push('Shown In Deck');
    if (columnVisibility.system) cols.push('System');
    if (columnVisibility.builtIn) cols.push('Built-in');
    if (columnVisibility.size) cols.push('Size');
    cols.push('Actions');

    const colCount = cols.length;
    const gridTemplateColumns = `repeat(${colCount}, minmax(0, 1fr))`;

    let html = `<div class="grid-head" style="grid-template-columns: ${gridTemplateColumns};">`;
    cols.forEach(col => {
      html += `<span>${col}</span>`;
    });
    html += '</div>';

    games.forEach((g, index) => {
      html += `<div class="grid-row" style="grid-template-columns: ${gridTemplateColumns};" data-index="${index}">`;

      if (columnVisibility.id) {
        html += `<span title="${escapeHtml(g.id || '')}">${escapeHtml(g.id || '')}</span>`;
      }

      // Title
      if (g.isEditing) {
        html += `<span><input type="text" class="edit-title" value="${escapeHtml(g.title || '')}" /></span>`;
      } else {
        html += `<span>${escapeHtml(g.title || '')}</span>`;
      }

      // Common Name
      if (columnVisibility.commonName) {
        const commonName = getCommonName(g);
        if (g.isEditing) {
          html += `<span><input type="text" class="edit-commonName" value="${escapeHtml(commonName)}" /></span>`;
        } else {
          html += `<span>${escapeHtml(commonName)}</span>`;
        }
      }

      // Status
      if (columnVisibility.status) {
        if (g.isEditing) {
          html += `<span><select class="edit-status">`;
          STATUS_OPTIONS.forEach(s => {
            const selected = s === g.status ? 'selected' : '';
            html += `<option value="${s}" ${selected}>${s}</option>`;
          });
          html += `</select></span>`;
        } else {
          html += `<span>${escapeHtml(g.status || 'Nothing')}</span>`;
        }
      }

      // Note
      if (columnVisibility.note) {
        if (g.isEditing) {
          html += `<span><input type="text" class="edit-note" value="${escapeHtml(g.note || '')}" /></span>`;
        } else {
          html += `<span>${escapeHtml(g.note || '')}</span>`;
        }
      }

      // ROM Key
      if (columnVisibility.romKey) {
        html += `<span class="grid-compact" title="${escapeHtml(g.romKey || '')}">${escapeHtml(g.romKey || '')}</span>`;
      }

      // Linked Achievements
      if (columnVisibility.achievementCount) {
        html += `<span>${g.linkedAchievementCount || 0}</span>`;
      }

      // ROM Installed
      if (columnVisibility.installedRom) {
        html += `<span><span class="grid-chip ${g.installedRom ? 'grid-chip-positive' : 'grid-chip-negative'}">${g.installedRom ? 'Yes' : 'No'}</span></span>`;
      }

      // Deck Eligible
      if (columnVisibility.deckEligible) {
        html += `<span title="${escapeHtml(g.deckEligibilityReason || '')}"><span class="grid-chip ${g.deckEligible ? 'grid-chip-positive' : 'grid-chip-negative'}">${g.deckEligible ? 'Yes' : 'No'}</span></span>`;
      }

      // System
      if (columnVisibility.system) {
        html += `<span>${escapeHtml(g.system || 'NES')}</span>`;
      }

      // Built-in
      if (columnVisibility.builtIn) {
        html += `<span>${g.builtIn ? 'Yes' : 'No'}</span>`;
      }

      // Size
      if (columnVisibility.size) {
        html += `<span>${g.size !== undefined ? g.size : '-'}</span>`;
      }

      // Actions
      html += `<span class="action-btns">`;
      if (!g.isEditing) {
        html += `<button class="btn-edit-game" data-index="${index}">Edit</button>`;
        html += `<button class="btn-delete-game" data-index="${index}">Delete</button>`;
      } else {
        html += `<button class="btn-save-game" data-index="${index}">Save</button>`;
        html += `<button class="btn-cancel-game" data-index="${index}">Cancel</button>`;
      }
      html += `</span>`;

      html += '</div>';
    });

    container.innerHTML = html;

    // Attach event listeners
    container.querySelectorAll('.btn-edit-game').forEach(btn => {
      btn.addEventListener('click', () => beginEditGame(parseInt(btn.dataset.index)));
    });
    container.querySelectorAll('.btn-delete-game').forEach(btn => {
      btn.addEventListener('click', () => deleteGame(parseInt(btn.dataset.index)));
    });
    container.querySelectorAll('.btn-save-game').forEach(btn => {
      btn.addEventListener('click', () => saveGame(parseInt(btn.dataset.index)));
    });
    container.querySelectorAll('.btn-cancel-game').forEach(btn => {
      btn.addEventListener('click', () => cancelEditGame());
    });
  }

  function getCommonName(g) {
    if (typeof g.commonName === 'string' && g.commonName.trim()) return g.commonName.trim();
    if (typeof g.title === 'string' && g.title.trim()) return g.title.trim();
    if (typeof g.name === 'string' && g.name.trim()) return g.name.trim();
    return getRomKey(g);
  }

  function getDisplayTitle(g) {
    if (typeof g.title === 'string' && g.title.trim()) return g.title.trim();
    if (typeof g.name === 'string' && g.name.trim()) return g.name.trim();
    return getRomKey(g);
  }

  function getGameNote(g) {
    if (typeof g.note === 'string' && g.note.trim()) return g.note.trim();
    if (typeof g.notes === 'string' && g.notes.trim()) return g.notes.trim();
    return '';
  }

  function getRomKey(g) {
    if (typeof g.romKey === 'string' && g.romKey.trim()) return g.romKey.trim();
    if (typeof g.name === 'string' && g.name.trim()) return g.name.trim();
    return '';
  }

  function getSystemLabel(g) {
    const raw = typeof g.system === 'string' && g.system.trim()
      ? g.system
      : (typeof g.platform === 'string' && g.platform.trim() ? g.platform : 'nes');
    return String(raw).toUpperCase();
  }

  function normalizeRomStorageName(value) {
    return typeof value === 'string' && value.trim()
      ? value.trim().toLowerCase()
      : '';
  }

  function isDeckEligible(game, achievementsByGameId, installedRomKeys) {
    return getDeckEligibilityReason(game, achievementsByGameId, installedRomKeys) === 'Shown in Deck/Continue.';
  }

  function getDeckEligibilityReason(game, achievementsByGameId, installedRomKeys) {
    const romKey = getRomKey(game);
    const normalizedRomKey = normalizeRomStorageName(romKey);
    const linkedAchievementCount = (achievementsByGameId.get(game && game.id) || []).length;

    if (!normalizedRomKey) {
      return 'Missing ROM key.';
    }

    if (!installedRomKeys.has(normalizedRomKey)) {
      return 'ROM is not installed in storage.';
    }

    if (linkedAchievementCount === 0) {
      return 'No linked achievements.';
    }

    return 'Shown in Deck/Continue.';
  }

  function beginEditGame(index) {
    games[index].isEditing = true;
    renderGames();
  }

  function cancelEditGame() {
    loadGames();
  }

  async function saveGame(index) {
    const row = document.querySelector(`.grid-row[data-index="${index}"]`);
    const g = games[index];

    if (row) {
      const titleInput = row.querySelector('.edit-title');
      const commonNameInput = row.querySelector('.edit-commonName');
      const statusSelect = row.querySelector('.edit-status');
      const noteInput = row.querySelector('.edit-note');

      if (titleInput) g.title = titleInput.value;
      if (commonNameInput) g.commonName = commonNameInput.value;
      if (statusSelect) g.status = statusSelect.value;
      if (noteInput) g.note = noteInput.value;
    }

    try {
      const rec = {
        id: g.id,
        title: g.title || '',
        commonName: g.commonName || '',
        status: g.status || 'Nothing',
        note: g.note || '',
        notes: g.note || '',
        system: g.system || 'nes',
        platform: g.platform || g.system || 'nes',
        name: g.name || g.romKey || g.title || '',
        romKey: g.romKey,
        headerSignature: g.headerSignature,
        builtIn: g.builtIn,
        size: g.size,
        createdAt: g.createdAt
      };

      await window.continueDb.open();
      await window.continueDb.put('games', rec);
      await loadGames();
    } catch (error) {
      console.error('[CRUD] Save game failed:', error);
      alert('Save failed: ' + error.message);
    }
  }

  async function deleteGame(index) {
    const g = games[index];
    const commonName = getCommonName(g);

    if (!confirm(`Delete game '${commonName}' (ID: ${g.id})? This cannot be undone.`)) {
      return;
    }

    try {
      await window.continueDb.open();
      await window.continueDb.delete('games', g.id);
      await loadGames();
    } catch (error) {
      console.error('[CRUD] Delete game failed:', error);
      alert('Delete failed: ' + error.message);
    }
  }

  // ===== ACHIEVEMENTS TAB =====
  async function loadAchievements() {
    if (activeTab !== 'achievements') return;

    const container = document.getElementById('achievementsGrid');
    container.innerHTML = '<div class="grid-empty">Loading…</div>';

    try {
      await window.continueDb.open();

      // Load games for dropdown
      const gamesArr = await window.continueDb.getAll('games');
      gamesArr.sort((a, b) => (a.title || '').localeCompare(b.title || ''));
      gameOptions = gamesArr
        .filter(g => g.id)
        .map(g => ({
          id: g.id,
          label: getCommonName(g)
        }));

      // Build lookup maps
      gameIdByLabel = {};
      gameLabelById = {};
      gameOptions.forEach(opt => {
        gameIdByLabel[opt.label] = opt.id;
        gameLabelById[opt.id] = opt.label;
      });

      // Load achievements
      const arr = await window.continueDb.getAll('achievements');
      achievements = arr.map(a => ({
        ...a,
        id: a.id || '',
        gameId: a.gameId || '',
        title: a.title || '',
        requirements: a.requirements || [],
        metaAchievementName: a.metaAchievementName || '',
        isEditing: false
      }));

      achievements.sort((a, b) => (a.id || '').localeCompare(b.id || ''));

      renderAchievements();
    } catch (error) {
      console.error('[CRUD] Load achievements failed:', error);
      container.innerHTML = '<div class="grid-empty">Failed to load achievements.</div>';
    }
  }

  function renderAchievements() {
    const container = document.getElementById('achievementsGrid');

    if (achievements.length === 0) {
      container.innerHTML = '<div class="grid-empty">No records yet.</div>';
      return;
    }

    const gridTemplateColumns = '1fr 1fr 1fr 2fr 1fr';

    let html = `<div class="grid-head" style="grid-template-columns: ${gridTemplateColumns};">
      <span>ID</span>
      <span>Game</span>
      <span>Title</span>
      <span>Meta Achievement</span>
      <span>Controls</span>
    </div>`;

    achievements.forEach((a, index) => {
      html += `<div class="grid-row" style="grid-template-columns: ${gridTemplateColumns};" data-index="${index}">`;

      // ID
      if (a.isEditing) {
        html += `<span class="id-cell"><input type="text" class="id-input edit-ach-id" value="${escapeHtml(a.id)}" /></span>`;
      } else {
        html += `<span class="id-cell"><span class="id-text" title="${escapeHtml(a.id)}">${escapeHtml(a.id)}</span></span>`;
      }

      // Game
      if (a.isEditing) {
        html += `<span><select class="edit-ach-game">`;
        html += `<option value="">Select Game…</option>`;
        gameOptions.forEach(g => {
          const selected = g.id === a.gameId ? 'selected' : '';
          html += `<option value="${escapeHtml(g.id)}" ${selected}>${escapeHtml(g.label)}</option>`;
        });
        html += `</select></span>`;
      } else {
        html += `<span>${escapeHtml(getGameLabel(a.gameId))}</span>`;
      }

      // Title
      if (a.isEditing) {
        html += `<span><input type="text" class="edit-ach-title" value="${escapeHtml(a.title)}" /></span>`;
      } else {
        html += `<span>${escapeHtml(a.title)}</span>`;
      }

      // Meta Achievement
      html += `<span class="meta-cell">`;
      if (a.isEditing) {
        const gameTitle = getGameLabel(a.gameId);
        html += `<select class="meta-select edit-ach-meta" data-game="${escapeHtml(gameTitle)}">`;
        html += `<option value="">Select Meta Achievement…</option>`;
        const metaOpts = getMetaOptionsForGame(gameTitle);
        metaOpts.forEach(opt => {
          const selected = opt.name === a.metaAchievementName ? 'selected' : '';
          html += `<option value="${escapeHtml(opt.name)}" ${selected} title="${escapeHtml(opt.name)}">${escapeHtml(opt.name)}</option>`;
        });
        html += `</select>`;
      } else {
        const metaName = a.metaAchievementName || '-';
        html += `<span class="meta-text" title="${escapeHtml(metaName)}">${escapeHtml(metaName)}</span>`;
      }
      html += `</span>`;

      // Controls
      html += `<span class="action-btns">`;
      if (!a.isEditing) {
        html += `<button class="btn-edit-ach" data-index="${index}">Edit</button>`;
        html += `<button class="btn-delete-ach" data-index="${index}">Delete</button>`;
      } else {
        html += `<button class="btn-save-ach" data-index="${index}">Save</button>`;
        html += `<button class="btn-cancel-ach" data-index="${index}">Cancel</button>`;
      }
      html += `</span>`;

      html += '</div>';
    });

    container.innerHTML = html;

    // Attach event listeners
    container.querySelectorAll('.btn-edit-ach').forEach(btn => {
      btn.addEventListener('click', () => beginEditAchievement(parseInt(btn.dataset.index)));
    });
    container.querySelectorAll('.btn-delete-ach').forEach(btn => {
      btn.addEventListener('click', () => deleteAchievement(parseInt(btn.dataset.index)));
    });
    container.querySelectorAll('.btn-save-ach').forEach(btn => {
      btn.addEventListener('click', () => saveAchievement(parseInt(btn.dataset.index)));
    });
    container.querySelectorAll('.btn-cancel-ach').forEach(btn => {
      btn.addEventListener('click', () => cancelEditAchievement());
    });
  }

  function getGameLabel(gameId) {
    if (!gameId) return '-';
    return gameLabelById[gameId] || gameId;
  }

  function getMetaOptionsForGame(gameTitle) {
    if (!gameTitle || !metaOptionsByGameTitle[gameTitle]) {
      // Try to load meta options asynchronously
      ensureMetaOptionsForGame(gameTitle);
      return [];
    }
    return metaOptionsByGameTitle[gameTitle];
  }

  async function ensureMetaOptionsForGame(gameTitle) {
    if (!gameTitle || metaOptionsByGameTitle[gameTitle]) return;

    try {
      // Load meta_games.json and find achievements for this game
      const response = await fetch('../shared/models/meta_games.json');
      if (!response.ok) {
        console.warn('[CRUD] Failed to load meta_games.json:', response.status);
        metaOptionsByGameTitle[gameTitle] = [];
        return;
      }
      
      const metaGames = await response.json();
      const game = metaGames.find(g => g.Title === gameTitle);
      
      if (game && game.Achievements) {
        metaOptionsByGameTitle[gameTitle] = game.Achievements.map(ach => ({
          name: ach.Description || '',
          formula: ach.Formula || ''
        })).sort((a, b) => a.name.localeCompare(b.name));
      } else {
        metaOptionsByGameTitle[gameTitle] = [];
      }
    } catch (error) {
      console.warn('[CRUD] Failed to load meta options for game:', gameTitle, error);
      metaOptionsByGameTitle[gameTitle] = [];
    }
  }

  function addAchievementRow() {
    const nextId = suggestAchievementId();
    achievements.push({
      id: nextId,
      gameId: gameOptions.length > 0 ? gameOptions[0].id : '',
      title: '',
      requirements: [],
      metaAchievementName: '',
      isEditing: true
    });
    renderAchievements();
  }

  function suggestAchievementId() {
    const baseId = 'ach_';
    let i = 1;
    const existing = new Set(achievements.map(a => a.id));
    while (existing.has(baseId + i)) i++;
    return baseId + i;
  }

  function beginEditAchievement(index) {
    achievements[index].isEditing = true;
    const a = achievements[index];
    const gameTitle = getGameLabel(a.gameId);
    ensureMetaOptionsForGame(gameTitle);
    renderAchievements();
  }

  function cancelEditAchievement() {
    loadAchievements();
  }

  async function saveAchievement(index) {
    const row = document.querySelector(`.grid-row[data-index="${index}"]`);
    const a = achievements[index];

    if (row) {
      const idInput = row.querySelector('.edit-ach-id');
      const gameSelect = row.querySelector('.edit-ach-game');
      const titleInput = row.querySelector('.edit-ach-title');
      const metaSelect = row.querySelector('.edit-ach-meta');

      if (idInput) a.id = idInput.value;
      if (gameSelect) a.gameId = gameSelect.value;
      if (titleInput) a.title = titleInput.value;
      if (metaSelect) a.metaAchievementName = metaSelect.value;
    }

    try {
      const rec = {
        id: a.id || '',
        gameId: a.gameId || '',
        title: a.title || '',
        requirements: (a.requirements || []).filter(s => s),
        metaAchievementName: a.metaAchievementName || ''
      };

      await window.continueDb.open();
      await window.continueDb.put('achievements', rec);
      await loadAchievements();
    } catch (error) {
      console.error('[CRUD] Save achievement failed:', error);
      alert('Save failed: ' + error.message);
    }
  }

  async function deleteAchievement(index) {
    const a = achievements[index];

    if (!confirm(`Delete achievement '${a.id}'? This cannot be undone.`)) {
      return;
    }

    try {
      await window.continueDb.open();
      await window.continueDb.delete('achievements', a.id);
      await loadAchievements();
    } catch (error) {
      console.error('[CRUD] Delete achievement failed:', error);
      alert('Delete failed: ' + error.message);
    }
  }

  // ===== CARDS TAB =====
  async function refreshCards() {
    if (activeTab !== 'cards') return;

    const container = document.getElementById('cardsGrid');
    container.innerHTML = '<div class="grid-empty">Loading…</div>';

    try {
      // Note: In the Blazor version, this scans reflection for cores
      // In the WebModule version, we'll just load persisted cards from the DB
      // A full implementation would need a C# backend endpoint to scan cores

      await window.continueDb.open();
      const arr = await window.continueDb.getAll('cards');

      cards = arr.map(c => ({
        ...c,
        id: c.id || '',
        domain: c.domain || '',
        coreId: c.coreId || '',
        name: c.name || '',
        category: c.category || '',
        performance: c.performance || 0,
        rating: c.rating || 0,
        type: c.type || 'Last',
        note: c.note || '',
        isEditing: false
      }));

      cards.sort((a, b) => {
        const domainCmp = a.domain.localeCompare(b.domain);
        if (domainCmp !== 0) return domainCmp;
        return a.name.localeCompare(b.name);
      });

      renderCards();
    } catch (error) {
      console.error('[CRUD] Load cards failed:', error);
      container.innerHTML = '<div class="grid-empty">Failed to load cards.</div>';
    }
  }

  function renderCards() {
    const container = document.getElementById('cardsGrid');

    if (cards.length === 0) {
      container.innerHTML = '<div class="grid-empty">No records yet.</div>';
      return;
    }

    const gridTemplateColumns = 'repeat(10, minmax(0, 1fr))';

    let html = `<div class="grid-head" style="grid-template-columns: ${gridTemplateColumns};">
      <span>ID</span>
      <span>Type</span>
      <span>Note</span>
      <span>Domain</span>
      <span>Core Id</span>
      <span>Name</span>
      <span>Category</span>
      <span>Perf</span>
      <span>Rating</span>
      <span>Actions</span>
    </div>`;

    cards.forEach((c, index) => {
      html += `<div class="grid-row" style="grid-template-columns: ${gridTemplateColumns};" data-index="${index}">`;

      // ID
      html += `<span title="${escapeHtml(c.id)}">${escapeHtml(c.id)}</span>`;

      // Type
      if (c.isEditing) {
        html += `<span><select class="edit-card-type">`;
        CARD_TYPE_OPTIONS.forEach(t => {
          const selected = t === c.type ? 'selected' : '';
          html += `<option value="${t}" ${selected}>${t}</option>`;
        });
        html += `</select></span>`;
      } else {
        html += `<span>${escapeHtml(c.type)}</span>`;
      }

      // Note
      if (c.isEditing) {
        html += `<span><input type="text" class="edit-card-note" value="${escapeHtml(c.note || '')}" /></span>`;
      } else {
        html += `<span>${escapeHtml(c.note || '')}</span>`;
      }

      // Domain, Core Id, Name, Category, Perf, Rating (read-only)
      html += `<span>${escapeHtml(c.domain)}</span>`;
      html += `<span>${escapeHtml(c.coreId)}</span>`;
      html += `<span>${escapeHtml(c.name)}</span>`;
      html += `<span>${escapeHtml(c.category || '-')}</span>`;
      html += `<span>${c.performance}</span>`;
      html += `<span>${c.rating}</span>`;

      // Actions
      html += `<span class="action-btns">`;
      if (!c.isEditing) {
        html += `<button class="btn-edit-card" data-index="${index}">Edit</button>`;
        html += `<button class="btn-delete-card" data-index="${index}">Delete</button>`;
      } else {
        html += `<button class="btn-save-card" data-index="${index}">Save</button>`;
        html += `<button class="btn-cancel-card" data-index="${index}">Cancel</button>`;
      }
      html += `</span>`;

      html += '</div>';
    });

    container.innerHTML = html;

    // Attach event listeners
    container.querySelectorAll('.btn-edit-card').forEach(btn => {
      btn.addEventListener('click', () => beginEditCard(parseInt(btn.dataset.index)));
    });
    container.querySelectorAll('.btn-delete-card').forEach(btn => {
      btn.addEventListener('click', () => deleteCard(parseInt(btn.dataset.index)));
    });
    container.querySelectorAll('.btn-save-card').forEach(btn => {
      btn.addEventListener('click', () => saveCard(parseInt(btn.dataset.index)));
    });
    container.querySelectorAll('.btn-cancel-card').forEach(btn => {
      btn.addEventListener('click', () => cancelEditCard());
    });
  }

  function beginEditCard(index) {
    cards[index].isEditing = true;
    renderCards();
  }

  function cancelEditCard() {
    refreshCards();
  }

  async function saveCard(index) {
    const row = document.querySelector(`.grid-row[data-index="${index}"]`);
    const c = cards[index];

    if (row) {
      const typeSelect = row.querySelector('.edit-card-type');
      const noteInput = row.querySelector('.edit-card-note');

      if (typeSelect) c.type = typeSelect.value;
      if (noteInput) c.note = noteInput.value;
    }

    try {
      const rec = {
        id: c.id,
        type: c.type || 'Last',
        note: c.note || ''
      };

      await window.continueDb.open();
      await window.continueDb.put('cards', rec);
      await refreshCards();
    } catch (error) {
      console.error('[CRUD] Save card failed:', error);
      alert('Save failed: ' + error.message);
    }
  }

  async function deleteCard(index) {
    const c = cards[index];

    if (!confirm(`Delete card '${c.id}'? This cannot be undone.`)) {
      return;
    }

    try {
      await window.continueDb.open();
      await window.continueDb.delete('cards', c.id);
      await refreshCards();
    } catch (error) {
      console.error('[CRUD] Delete card failed:', error);
      alert('Delete failed: ' + error.message);
    }
  }

  // ===== META TAB =====
  async function loadMeta() {
    if (activeTab !== 'meta') return;

    const container = document.getElementById('metaGrid');
    container.innerHTML = '<div class="grid-empty">Loading…</div>';

    try {
      // Load meta_games.json
      const filterTitle = document.getElementById('metaFilterTitle').value.trim();
      
      const response = await fetch('../shared/models/meta_games.json');
      if (!response.ok) {
        throw new Error(`Failed to load meta_games.json: ${response.status}`);
      }
      
      const metaGames = await response.json();
      
      // Build gameIdByLabel and gameLabelById if not already loaded
      await ensureGamesIndex();
      
      // Flatten the data structure
      const allRows = [];
      for (const game of metaGames) {
        const gameTitle = game.Title || '';
        if (filterTitle && gameTitle !== filterTitle) continue;
        
        const achievements = game.Achievements || [];
        for (const ach of achievements) {
          allRows.push({
            game: gameTitle,
            description: ach.Description || '',
            formula: ach.Formula || '',
            testSucceeded: false,
            testFailed: false,
            approved: false
          });
        }
      }
      
      // Sort by game title then achievement description
      allRows.sort((a, b) => {
        const gameCmp = a.game.localeCompare(b.game);
        if (gameCmp !== 0) return gameCmp;
        return a.description.localeCompare(b.description);
      });
      
      metaRows = allRows;
      
      // Check which achievements are approved (exist in achievements DB)
      await window.continueDb.open();
      const achArr = await window.continueDb.getAll('achievements');
      const approvedSet = new Set();
      
      for (const a of achArr) {
        const gid = a.gameId || '';
        const metaName = a.metaAchievementName || '';
        if (!gid || !metaName) continue;
        
        if (gameLabelById[gid]) {
          const title = gameLabelById[gid];
          approvedSet.add(title + '||' + metaName);
        }
      }
      
      // Mark approved achievements
      for (const r of metaRows) {
        r.approved = approvedSet.has(r.game + '||' + r.description);
      }
      
      if (metaRows.length === 0) {
        container.innerHTML = '<div class="grid-empty">No meta achievements found' + (filterTitle ? ' for this game' : '') + '.</div>';
      } else {
        renderMeta();
      }
    } catch (error) {
      console.error('[CRUD] Load meta failed:', error);
      container.innerHTML = `<div class="grid-empty">Failed to load meta achievements: ${error.message}</div>`;
    }
  }

  async function ensureGamesIndex() {
    if (Object.keys(gameIdByLabel).length > 0 && Object.keys(gameLabelById).length > 0) return;
    
    try {
      await window.continueDb.open();
      const gamesArr = await window.continueDb.getAll('games');
      gamesArr.sort((a, b) => (a.title || '').localeCompare(b.title || ''));
      
      gameIdByLabel = {};
      gameLabelById = {};
      
      for (const g of gamesArr) {
        if (!g.id) continue;
        const label = getCommonName(g);
        gameIdByLabel[label] = g.id;
        gameLabelById[g.id] = label;
      }
    } catch (error) {
      console.warn('[CRUD] Failed to build games index:', error);
    }
  }

  function renderMeta() {
    const container = document.getElementById('metaGrid');

    if (metaRows.length === 0) {
      // Already handled in loadMeta
      return;
    }

    const gridTemplateColumns = '1fr 1fr 2fr 0.6fr';

    let html = `<div class="grid-head" style="grid-template-columns: ${gridTemplateColumns};">
      <span>Game</span>
      <span>Achievement</span>
      <span>Formula</span>
      <span>Approved</span>
    </div>`;

    metaRows.forEach((r, index) => {
      const testClass = r.testSucceeded ? 'meta-test-success' : (r.testFailed ? 'meta-test-failure' : '');

      html += `<div class="grid-row" style="grid-template-columns: ${gridTemplateColumns};" data-index="${index}">`;
      html += `<span>${escapeHtml(r.game)}</span>`;
      html += `<span class="${testClass}" style="cursor:pointer;" title="Click to test just this achievement">${escapeHtml(r.description)}</span>`;
      html += `<span class="meta-formula">${escapeHtml(r.formula)}</span>`;
      html += `<span><input type="checkbox" class="meta-approved" data-index="${index}" ${r.approved ? 'checked' : ''} /></span>`;
      html += '</div>';
    });

    container.innerHTML = html;

    // Attach event listeners
    container.querySelectorAll('.meta-approved').forEach(checkbox => {
      checkbox.addEventListener('change', (e) => {
        const index = parseInt(e.target.dataset.index);
        toggleMetaApproved(index, e.target.checked);
      });
    });
  }

  async function toggleMetaApproved(index, approved) {
    const r = metaRows[index];

    // Find the game ID for this game title
    if (!gameIdByLabel[r.game]) {
      alert(`Game not found for title '${r.game}'.`);
      return;
    }

    const gameId = gameIdByLabel[r.game];

    if (approved) {
      await upsertAchievementByLink(gameId, r.description);
    } else {
      await deleteAchievementByLink(gameId, r.description);
    }

    r.approved = approved;
  }

  async function upsertAchievementByLink(gameId, metaName) {
    try {
      await window.continueDb.open();
      const arr = await window.continueDb.getAll('achievements');
      const existing = arr.find(a => a.gameId === gameId && a.metaAchievementName === metaName);

      const id = existing ? existing.id : slugifyId(`ach_${gameId}_${metaName}`);

      const rec = {
        id: id,
        gameId: gameId,
        title: metaName,
        requirements: [],
        metaAchievementName: metaName
      };

      await window.continueDb.put('achievements', rec);
    } catch (error) {
      console.error('[CRUD] Upsert achievement by link failed:', error);
    }
  }

  async function deleteAchievementByLink(gameId, metaName) {
    try {
      await window.continueDb.open();
      const arr = await window.continueDb.getAll('achievements');
      const existing = arr.find(a => a.gameId === gameId && a.metaAchievementName === metaName);

      if (existing) {
        await window.continueDb.delete('achievements', existing.id);
      }
    } catch (error) {
      console.error('[CRUD] Delete achievement by link failed:', error);
    }
  }

  function slugifyId(s) {
    if (!s) return 'ach_empty';
    return s.toLowerCase().replace(/[^a-z0-9_-]/g, '_');
  }

  async function testAllFormulas() {
    alert('Formula testing requires NES emulator and parser (not available in WebModule).');
  }

  // ===== LEVELS TAB =====
  async function loadLevels() {
    if (activeTab !== 'levels') return;

    const container = document.getElementById('levelsGrid');
    container.innerHTML = '<div class="grid-empty">Loading…</div>';

    try {
      await window.continueDb.open();

      // Build card options if not already loaded
      if (cardOptions.length === 0) {
        const cardsArr = await window.continueDb.getAll('cards');
        cardOptions = cardsArr
          .map(c => ({
            id: `${c.domain}_${c.coreId}`,
            label: `${c.domain}_${c.coreId}`
          }))
          .sort((a, b) => a.label.localeCompare(b.label));

        cardLabelById = {};
        cardOptions.forEach(opt => {
          cardLabelById[opt.id] = opt.label;
        });
      }

      const arr = await window.continueDb.getAll('levels');
      arr.sort((a, b) => (a.index || 0) - (b.index || 0));

      levels = arr.map(l => ({
        ...l,
        index: l.index || 1,
        requiredCards: l.requiredCards || [],
        requiredStars: l.requiredStars || 0,
        cardChallenge: l.cardChallenge || '',
        message: l.message || '',
        isEditing: false
      }));

      renderLevels();
    } catch (error) {
      console.error('[CRUD] Load levels failed:', error);
      container.innerHTML = '<div class="grid-empty">Failed to load levels.</div>';
    }
  }

  function renderLevels() {
    const container = document.getElementById('levelsGrid');

    if (levels.length === 0) {
      container.innerHTML = '<div class="grid-empty">No records yet.</div>';
      return;
    }

    const gridTemplateColumns = 'repeat(6, minmax(0, 1fr))';

    let html = `<div class="grid-head" style="grid-template-columns: ${gridTemplateColumns};">
      <span>Index</span>
      <span>Required Cards</span>
      <span>Required Stars</span>
      <span>Card Challenge</span>
      <span>Message</span>
      <span>Actions</span>
    </div>`;

    levels.forEach((l, index) => {
      html += `<div class="grid-row" style="grid-template-columns: ${gridTemplateColumns};" data-index="${index}">`;

      // Index
      if (l.isEditing) {
        html += `<span><input type="number" class="edit-level-index" value="${l.index}" /></span>`;
      } else {
        html += `<span>${l.index}</span>`;
      }

      // Required Cards
      if (l.isEditing) {
        html += `<span><div class="card-slots">`;
        for (let i = 0; i < 5; i++) {
          const value = l.requiredCards[i] || '';
          html += `<select class="edit-level-card-${i}">`;
          html += `<option value="">None</option>`;
          cardOptions.forEach(opt => {
            const selected = opt.id === value ? 'selected' : '';
            html += `<option value="${escapeHtml(opt.id)}" ${selected}>${escapeHtml(opt.label)}</option>`;
          });
          html += `</select>`;
        }
        html += `</div></span>`;
      } else {
        html += `<span>${formatRequiredCards(l)}</span>`;
      }

      // Required Stars
      if (l.isEditing) {
        html += `<span><input type="number" class="edit-level-stars" value="${l.requiredStars}" /></span>`;
      } else {
        html += `<span>${l.requiredStars}</span>`;
      }

      // Card Challenge
      if (l.isEditing) {
        html += `<span><input type="text" class="edit-level-challenge" value="${escapeHtml(l.cardChallenge || '')}" /></span>`;
      } else {
        html += `<span>${escapeHtml(l.cardChallenge || '-')}</span>`;
      }

      // Message
      if (l.isEditing) {
        html += `<span><input type="text" class="edit-level-message" value="${escapeHtml(l.message || '')}" /></span>`;
      } else {
        html += `<span style="white-space: pre-wrap; word-break: break-word; overflow-wrap: anywhere;">${escapeHtml(l.message || '-')}</span>`;
      }

      // Actions
      html += `<span class="action-btns">`;
      if (!l.isEditing) {
        html += `<button class="btn-edit-level" data-index="${index}">Edit</button>`;
        html += `<button class="btn-delete-level" data-index="${index}">Delete</button>`;
      } else {
        html += `<button class="btn-save-level" data-index="${index}">Save</button>`;
        html += `<button class="btn-cancel-level" data-index="${index}">Cancel</button>`;
      }
      html += `</span>`;

      html += '</div>';
    });

    container.innerHTML = html;

    // Attach event listeners
    container.querySelectorAll('.btn-edit-level').forEach(btn => {
      btn.addEventListener('click', () => beginEditLevel(parseInt(btn.dataset.index)));
    });
    container.querySelectorAll('.btn-delete-level').forEach(btn => {
      btn.addEventListener('click', () => deleteLevel(parseInt(btn.dataset.index)));
    });
    container.querySelectorAll('.btn-save-level').forEach(btn => {
      btn.addEventListener('click', () => saveLevel(parseInt(btn.dataset.index)));
    });
    container.querySelectorAll('.btn-cancel-level').forEach(btn => {
      btn.addEventListener('click', () => cancelEditLevel());
    });
  }

  function formatRequiredCards(l) {
    if (!l.requiredCards || l.requiredCards.length === 0) return '0';

    const labels = l.requiredCards
      .filter(id => id)
      .map(id => cardLabelById[id] || id);

    if (labels.length === 0) return '0';
    return labels.join(', ');
  }

  function addLevelRow() {
    let nextIndex = 1;
    if (levels.length > 0) {
      nextIndex = Math.max(...levels.map(l => l.index)) + 1;
    }

    levels.push({
      index: nextIndex,
      requiredCards: [],
      requiredStars: 0,
      cardChallenge: '',
      message: '',
      isEditing: true
    });

    renderLevels();
  }

  function beginEditLevel(index) {
    levels[index].isEditing = true;
    renderLevels();
  }

  function cancelEditLevel() {
    loadLevels();
  }

  async function saveLevel(index) {
    const row = document.querySelector(`.grid-row[data-index="${index}"]`);
    const l = levels[index];

    if (row) {
      const indexInput = row.querySelector('.edit-level-index');
      const starsInput = row.querySelector('.edit-level-stars');
      const challengeInput = row.querySelector('.edit-level-challenge');
      const messageInput = row.querySelector('.edit-level-message');

      if (indexInput) l.index = parseInt(indexInput.value) || 1;
      if (starsInput) l.requiredStars = parseInt(starsInput.value) || 0;
      if (challengeInput) l.cardChallenge = challengeInput.value;
      if (messageInput) l.message = messageInput.value;

      // Card slots
      const cardSlots = [];
      for (let i = 0; i < 5; i++) {
        const select = row.querySelector(`.edit-level-card-${i}`);
        if (select && select.value) {
          cardSlots.push(select.value);
        }
      }
      l.requiredCards = cardSlots;
    }

    try {
      const rec = {
        index: Math.max(1, l.index),
        requiredCards: l.requiredCards.filter(s => s).slice(0, 5),
        requiredStars: Math.max(0, l.requiredStars),
        cardChallenge: l.cardChallenge || '',
        message: l.message || ''
      };

      await window.continueDb.open();
      await window.continueDb.put('levels', rec);
      await loadLevels();
    } catch (error) {
      console.error('[CRUD] Save level failed:', error);
      alert('Save failed: ' + error.message);
    }
  }

  async function deleteLevel(index) {
    const l = levels[index];

    if (!confirm(`Delete level #${l.index}? This cannot be undone.`)) {
      return;
    }

    try {
      await window.continueDb.open();
      await window.continueDb.delete('levels', l.index);
      await loadLevels();
    } catch (error) {
      console.error('[CRUD] Delete level failed:', error);
      alert('Delete failed: ' + error.message);
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

        const store = await openRomStorageStore('readwrite');
        await new Promise((resolve, reject) => {
          const request = store.put({ name, base64 });
          request.onsuccess = () => resolve();
          request.onerror = () => reject(request.error);
        });
      }
    } catch (error) {
      console.warn('[CRUD] Failed to migrate legacy ROM storage:', error);
    }
  }

  async function getStoredRomsFromIndexedDb(runMigration = true) {
    if (runMigration) {
      await migrateLegacyLocalStorageRoms();
    }

    try {
      const store = await openRomStorageStore('readonly');
      return await new Promise((resolve, reject) => {
        const request = store.getAll();
        request.onsuccess = () => resolve(Array.isArray(request.result) ? request.result : []);
        request.onerror = () => reject(request.error);
      });
    } catch (error) {
      console.warn('[CRUD] IndexedDB ROM storage unavailable:', error);
      return getStoredRomsFromLegacyLocalStorage();
    }
  }

  function getStoredRomsFromLegacyLocalStorage() {
    if (!window.localStorage) {
      return [];
    }

    const roms = [];
    for (let index = 0; index < localStorage.length; index++) {
      const key = localStorage.key(index);
      if (!key || !key.startsWith(LEGACY_ROM_KEY_PREFIX)) {
        continue;
      }

      const name = key.substring(LEGACY_ROM_KEY_PREFIX.length);
      const base64 = localStorage.getItem(key);
      if (name && base64) {
        roms.push({ name, base64 });
      }
    }

    return roms;
  }

  async function getStoredRoms() {
    if (window.nesInterop && typeof window.nesInterop.getStoredRoms === 'function') {
      return window.nesInterop.getStoredRoms();
    }

    return getStoredRomsFromIndexedDb();
  }

  // ===== UTILITY FUNCTIONS =====
  function escapeHtml(text) {
    const map = {
      '&': '&amp;',
      '<': '&lt;',
      '>': '&gt;',
      '"': '&quot;',
      "'": '&#039;'
    };
    return String(text).replace(/[&<>"']/g, m => map[m]);
  }

  // Expose API for debugging
  window.crudModule = {
    getGames: () => games,
    getAchievements: () => achievements,
    getCards: () => cards,
    getMetaRows: () => metaRows,
    getLevels: () => levels,
    reload: init
  };
})();
