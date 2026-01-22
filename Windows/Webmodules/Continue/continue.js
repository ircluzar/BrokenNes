// continue.js - Continue/Deck Builder logic (standalone webmodule)

(function() {
  'use strict';

  // Storage keys
  const STORAGE_KEY = 'brokenNesGameSave';

  // State
  let gameSave = null;
  let currentLevel = 1;
  let stars = 0;
  let requiredStars = 5;
  let levelCleared = false;
  
  // Deck state
  let selectedCpu = null;
  let selectedPpu = null;
  let selectedApu = null;
  let selectedShader = null;
  
  let enforcedCpu = null;
  let enforcedPpu = null;
  let enforcedApu = null;
  let enforcedShader = null;
  
  // ROM state
  let selectedGameId = null;
  let romRows = [];
  let gameAchievements = [];
  let cartridgeCollapsed = false;
  
  // Core data
  const coreData = {
    CPU: ['FMC', 'EXE', 'LAT', 'QUK', 'HAW'],
    PPU: ['FMC', 'LOW', 'MED', 'HI', 'OPT'],
    APU: ['FMC', 'LOW', 'MED', 'HI'],
    Shader: ['PX', '16B', 'BLD', 'BUMP', 'CCC', 'CNMA', 'CRY', 'CRZ', 'DOT', 'EXE', 
             'HUE', 'LAT', 'LCD', 'LSD', 'MSH', 'MUSK', 'RF', 'RGBX', 'SPK', 'TRI', 
             'TTF', 'TV', 'VHS', 'WARM', 'WTR']
  };
  
  // Sample game database (simplified)
  const gamesDb = [
    { id: 'smb', title: 'Super Mario Bros.', compat: true, achTotal: 12 },
    { id: 'zelda', title: 'The Legend of Zelda', compat: true, achTotal: 15 },
    { id: 'metroid', title: 'Metroid', compat: true, achTotal: 10 },
    { id: 'megaman', title: 'Mega Man', compat: true, achTotal: 8 },
    { id: 'castlevania', title: 'Castlevania', compat: true, achTotal: 9 }
  ];

  // Level progression data
  const levelData = {
    1: {
      title: 'Tutorial',
      message: 'Welcome to BrokenNes! Build your first console.',
      requiredStars: 5,
      enforced: {}
    },
    2: {
      title: 'CPU Focus',
      message: 'Experiment with different CPU cores.',
      requiredStars: 12,
      enforced: { CPU: 'EXE' }
    },
    3: {
      title: 'Visual Arts',
      message: 'Try different PPU and shader combinations.',
      requiredStars: 20,
      enforced: { PPU: 'LOW' }
    }
  };

  // Initialize on page load
  window.addEventListener('DOMContentLoaded', init);

  async function init() {
    try {
      // Start pixel background
      if (window.homePixelBgEnsure) {
        window.homePixelBgEnsure();
      }

      // Initialize audio
      initAudio();

      // Load game save
      await loadGameSave();

      // Setup event listeners
      setupEventListeners();

      // Load level data
      loadLevel();

      // Initialize ROM list
      initializeRomList();

      // Update UI
      updateUI();
    } catch (error) {
      console.error('[Continue] Initialization error:', error);
    }
  }

  function initAudio() {
    try {
      // Play deck builder music
      if (window.music) {
        const tracks = [
          'assets/music/DeckBuilder1.mp3',
          'assets/music/DeckBuilder2.mp3',
          'assets/music/DeckBuilder3.mp3',
          'assets/music/DeckBuilder4.mp3'
        ];
        const randomTrack = tracks[Math.floor(Math.random() * tracks.length)];
        
        window.music.play(randomTrack, { 
          loop: true, 
          fadeInMs: 800 
        }).catch(err => {
          console.warn('[Continue] Music play failed:', err);
        });

        window.music.setLocalVolume(0.4);
      }
    } catch (error) {
      console.warn('[Continue] Audio init error:', error);
    }
  }

  async function loadGameSave() {
    try {
      if (window.storage && typeof window.storage.load === 'function') {
        gameSave = await window.storage.load(STORAGE_KEY);
      } else {
        const data = localStorage.getItem(STORAGE_KEY);
        if (data) {
          gameSave = JSON.parse(data);
        }
      }

      // Initialize default save if none exists
      if (!gameSave) {
        gameSave = {
          Level: 1,
          Achievements: [],
          OwnedCores: {
            CPU: ['FMC'],
            PPU: ['FMC'],
            APU: ['FMC'],
            Clock: ['FMC'],
            Shader: ['PX']
          },
          LevelCleared: false,
          Preferences: {
            CPU: 'FMC',
            PPU: 'FMC',
            APU: 'FMC',
            Shader: 'PX'
          }
        };
      }

      // Load state from save
      currentLevel = gameSave.Level || 1;
      stars = (gameSave.Achievements || []).length;
      levelCleared = gameSave.LevelCleared || false;
      
      // Apply preferences
      selectedCpu = gameSave.Preferences?.CPU || 'FMC';
      selectedPpu = gameSave.Preferences?.PPU || 'FMC';
      selectedApu = gameSave.Preferences?.APU || 'FMC';
      selectedShader = gameSave.Preferences?.Shader || 'PX';
    } catch (error) {
      console.error('[Continue] Load save error:', error);
      gameSave = {
        Level: 1,
        Achievements: [],
        OwnedCores: { CPU: ['FMC'], PPU: ['FMC'], APU: ['FMC'], Clock: ['FMC'], Shader: ['PX'] },
        LevelCleared: false,
        Preferences: { CPU: 'FMC', PPU: 'FMC', APU: 'FMC', Shader: 'PX' }
      };
    }
  }

  async function saveGameSave() {
    try {
      if (window.storage && typeof window.storage.save === 'function') {
        await window.storage.save(STORAGE_KEY, gameSave);
      } else {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(gameSave));
      }
    } catch (error) {
      console.error('[Continue] Save error:', error);
    }
  }

  function loadLevel() {
    const level = levelData[currentLevel] || levelData[1];
    requiredStars = level.requiredStars;
    
    // Set enforced cores
    enforcedCpu = level.enforced?.CPU || null;
    enforcedPpu = level.enforced?.PPU || null;
    enforcedApu = level.enforced?.APU || null;
    enforcedShader = level.enforced?.Shader || null;
    
    // Update UI elements
    document.getElementById('levelChip').textContent = currentLevel;
    document.getElementById('levelTitle').textContent = level.title;
    
    const messageEl = document.getElementById('levelMessage');
    if (level.message) {
      messageEl.textContent = level.message;
      messageEl.style.display = 'block';
    } else {
      messageEl.style.display = 'none';
    }
    
    // Update enforced cards display
    const enforcedCardsEl = document.getElementById('enforcedCards');
    enforcedCardsEl.innerHTML = '<span class="small-note">Enforced:</span>';
    
    const enforced = [];
    if (enforcedCpu) enforced.push({ domain: 'CPU', id: enforcedCpu, label: enforcedCpu });
    if (enforcedPpu) enforced.push({ domain: 'PPU', id: enforcedPpu, label: enforcedPpu });
    if (enforcedApu) enforced.push({ domain: 'APU', id: enforcedApu, label: enforcedApu });
    if (enforcedShader) enforced.push({ domain: 'Shader', id: enforcedShader, label: enforcedShader });
    
    if (enforced.length === 0) {
      enforcedCardsEl.innerHTML += '<span class="small-note">None</span>';
    } else {
      enforced.forEach(e => {
        const chip = document.createElement('button');
        chip.className = 'enf-chip';
        chip.textContent = e.label;
        chip.style.borderColor = getCoreColor(e.domain);
        enforcedCardsEl.appendChild(chip);
      });
    }
    
    // Update level status
    const statusEl = document.getElementById('levelStatus');
    if (levelCleared) {
      statusEl.className = 'status-chip cleared';
      statusEl.textContent = 'Cleared';
    } else {
      statusEl.className = 'status-chip not-cleared';
      statusEl.textContent = 'Not Cleared';
    }
  }

  function getCoreColor(domain) {
    const colors = {
      CPU: '#ff5a26',
      PPU: '#10b981',
      APU: '#3b82f6',
      Shader: '#a855f7'
    };
    return colors[domain] || '#fff';
  }

  function initializeRomList() {
    // Build ROM rows from games DB
    romRows = gamesDb.map(g => {
      const achCompleted = (gameSave.Achievements || []).filter(a => a.startsWith(g.id + '_')).length;
      return {
        id: g.id,
        title: g.title,
        compat: g.compat,
        achTotal: g.achTotal,
        achCompleted: achCompleted
      };
    });
    
    renderRomList();
  }

  function renderRomList() {
    const tbody = document.getElementById('romTbody');
    const emptyEl = document.getElementById('romEmpty');
    const tableEl = document.getElementById('romTable');
    
    const filterCompatible = document.getElementById('filterCompatible').checked;
    const searchText = document.getElementById('romSearch').value.toLowerCase();
    
    // Filter
    const filtered = romRows.filter(r => {
      if (filterCompatible && !r.compat) return false;
      if (searchText && !r.title.toLowerCase().includes(searchText)) return false;
      return true;
    });
    
    if (filtered.length === 0) {
      emptyEl.style.display = 'block';
      tableEl.style.display = 'none';
      return;
    }
    
    emptyEl.style.display = 'none';
    tableEl.style.display = 'block';
    
    // Render rows
    tbody.innerHTML = '';
    filtered.forEach(r => {
      const row = document.createElement('button');
      row.type = 'button';
      row.className = 'rom-tr rom-td';
      if (selectedGameId === r.id) {
        row.classList.add('selected');
      }
      row.setAttribute('role', 'row');
      
      row.innerHTML = `
        <div class="c-title" role="cell">
          <div class="rom-title">${r.title}</div>
        </div>
        <div class="c-compat" role="cell">
          <span class="chip ${r.compat ? 'ok' : 'no'}">${r.compat ? 'Yes' : 'No'}</span>
        </div>
        <div class="c-ach" role="cell">${r.achCompleted}/${r.achTotal}</div>
      `;
      
      row.addEventListener('click', () => selectGame(r.id));
      tbody.appendChild(row);
    });
  }

  function selectGame(gameId) {
    selectedGameId = gameId;
    renderRomList();
    updateGameInfo();
    updateAchievements();
    updateStartButton();
  }

  function updateGameInfo() {
    const infoEl = document.getElementById('gameInfo');
    
    if (!selectedGameId) {
      infoEl.innerHTML = '<div class="small-note">Select a game to view details.</div>';
      return;
    }
    
    const game = romRows.find(r => r.id === selectedGameId);
    if (!game) return;
    
    infoEl.innerHTML = `
      <div class="game-grid">
        <div class="game-row">
          <div class="label small-note">Title</div>
          <div class="value">${game.title}</div>
        </div>
        <div class="game-row">
          <div class="label small-note">Compatible</div>
          <div class="value">${game.compat ? 'Yes' : 'No'}</div>
        </div>
        <div class="game-row">
          <div class="label small-note">Achievements</div>
          <div class="value">${game.achCompleted}/${game.achTotal} completed</div>
        </div>
      </div>
    `;
  }

  function updateAchievements() {
    const achBox = document.getElementById('achBox');
    
    if (!selectedGameId) {
      achBox.innerHTML = '<div class="small-note">Select a compatible game to view achievements.</div>';
      return;
    }
    
    const game = romRows.find(r => r.id === selectedGameId);
    if (!game || !game.compat) {
      achBox.innerHTML = '<div class="small-note">This game is not compatible or has no achievements.</div>';
      return;
    }
    
    // Generate sample achievements
    const achievements = [];
    for (let i = 1; i <= game.achTotal; i++) {
      const achId = `${game.id}_ach${i}`;
      const completed = (gameSave.Achievements || []).includes(achId);
      achievements.push({
        id: achId,
        title: `Achievement ${i}`,
        description: `Complete objective ${i} in ${game.title}`,
        completed: completed
      });
    }
    
    const completedCount = achievements.filter(a => a.completed).length;
    
    achBox.innerHTML = `
      <div class="ach-summary">
        <span class="small-note">${game.title}</span>
        <strong>${completedCount}/${achievements.length}</strong>
        <span class="small-note">completed</span>
      </div>
      <ul class="ach-list">
        ${achievements.map(a => `
          <li class="ach-item ${a.completed ? 'done' : 'todo'}">
            <span class="ach-check">${a.completed ? '▣' : '▢'}</span>
            <span class="ach-title">${a.title}</span>
            <span class="ach-desc small-note">${a.description}</span>
          </li>
        `).join('')}
      </ul>
    `;
  }

  function updateUI() {
    // Update stars display
    document.getElementById('starsDisplay').textContent = `${stars}/${requiredStars}`;
    
    // Update progress button
    const btnProgress = document.getElementById('btnProgress');
    const canAdvance = levelCleared && stars >= requiredStars;
    btnProgress.disabled = !canAdvance;
    
    // Update core slots
    updateCoreSlot('cpu', selectedCpu, enforcedCpu);
    updateCoreSlot('ppu', selectedPpu, enforcedPpu);
    updateCoreSlot('apu', selectedApu, enforcedApu);
    updateCoreSlot('shader', selectedShader, enforcedShader);
    
    updateStartButton();
  }

  function updateCoreSlot(slotName, selected, enforced) {
    const emptyEl = document.getElementById(`${slotName}Empty`);
    const cardEl = document.getElementById(`${slotName}Card`);
    const labelEl = document.getElementById(`${slotName}Label`);
    
    const core = enforced || selected;
    
    if (core) {
      emptyEl.style.display = 'none';
      cardEl.style.display = 'block';
      labelEl.textContent = core;
      
      // Change appearance if enforced
      if (enforced) {
        cardEl.style.opacity = '0.6';
        cardEl.title = 'Enforced';
      } else {
        cardEl.style.opacity = '1';
        cardEl.title = 'Click to change';
      }
    } else {
      emptyEl.style.display = 'flex';
      cardEl.style.display = 'none';
    }
  }

  function updateStartButton() {
    const startBtn = document.getElementById('startBtn');
    const hasValidBuild = selectedCpu && selectedPpu && selectedApu && selectedShader;
    const hasGameSelected = selectedGameId !== null;
    const canStart = hasValidBuild && hasGameSelected;
    
    if (canStart) {
      startBtn.disabled = false;
      startBtn.className = 'opt-link start-btn unlocked';
      startBtn.title = 'Start the game';
    } else {
      startBtn.disabled = true;
      startBtn.className = 'opt-link start-btn locked';
      startBtn.title = 'Build valid and game with achievements required';
    }
  }

  function setupEventListeners() {
    // Cartridge toggle
    const cartridgeToggle = document.getElementById('cartridgeToggle');
    if (cartridgeToggle) {
      cartridgeToggle.addEventListener('click', toggleCartridge);
    }
    
    // Filter checkbox
    const filterCompatible = document.getElementById('filterCompatible');
    if (filterCompatible) {
      filterCompatible.addEventListener('change', renderRomList);
    }
    
    // Search input
    const romSearch = document.getElementById('romSearch');
    if (romSearch) {
      romSearch.addEventListener('input', renderRomList);
    }
    
    // Core slot clicks
    ['cpu', 'ppu', 'apu', 'shader'].forEach(slotName => {
      const emptyEl = document.getElementById(`${slotName}Empty`);
      const cardEl = document.getElementById(`${slotName}Card`);
      
      if (emptyEl) {
        emptyEl.addEventListener('click', () => openCorePicker(slotName));
      }
      if (cardEl) {
        cardEl.addEventListener('click', () => {
          // Only allow picking if not enforced
          const enforced = slotName === 'cpu' ? enforcedCpu :
                          slotName === 'ppu' ? enforcedPpu :
                          slotName === 'apu' ? enforcedApu :
                          enforcedShader;
          if (!enforced) {
            openCorePicker(slotName);
          }
        });
      }
    });
    
    // Progress button
    const btnProgress = document.getElementById('btnProgress');
    if (btnProgress) {
      btnProgress.addEventListener('click', advanceLevel);
    }
    
    // Start button
    const startBtn = document.getElementById('startBtn');
    if (startBtn) {
      startBtn.addEventListener('click', startGame);
    }
    
    // Picker modal
    const pickerClose = document.getElementById('pickerClose');
    if (pickerClose) {
      pickerClose.addEventListener('click', closePicker);
    }
    
    const pickerModal = document.getElementById('pickerModal');
    if (pickerModal) {
      pickerModal.addEventListener('click', (e) => {
        if (e.target === pickerModal) {
          closePicker();
        }
      });
    }
  }

  function toggleCartridge() {
    cartridgeCollapsed = !cartridgeCollapsed;
    const panel = document.getElementById('cartridgePanel');
    const toggle = document.getElementById('cartridgeToggle');
    const icon = toggle.querySelector('.toggle-ico');
    
    if (cartridgeCollapsed) {
      panel.style.display = 'none';
      icon.textContent = '▸';
      toggle.setAttribute('aria-expanded', 'false');
    } else {
      panel.style.display = 'flex';
      icon.textContent = '▾';
      toggle.setAttribute('aria-expanded', 'true');
    }
  }

  let currentPickerSlot = null;

  function openCorePicker(slotName) {
    currentPickerSlot = slotName;
    
    const modal = document.getElementById('pickerModal');
    const title = document.getElementById('pickerTitle');
    const grid = document.getElementById('pickerGrid');
    
    const slotType = slotName.toUpperCase();
    title.textContent = `Select ${slotType}`;
    
    // Get owned cores for this type
    const owned = gameSave.OwnedCores?.[slotType] || [];
    const allCores = coreData[slotType] || [];
    
    // Render options
    grid.innerHTML = '';
    allCores.forEach(coreId => {
      const isOwned = owned.includes(coreId);
      const btn = document.createElement('button');
      btn.className = 'picker-option';
      btn.textContent = coreId;
      
      if (!isOwned) {
        btn.disabled = true;
        btn.classList.add('locked');
        btn.title = 'Not unlocked';
      } else {
        btn.addEventListener('click', () => selectCore(slotName, coreId));
      }
      
      grid.appendChild(btn);
    });
    
    modal.style.display = 'flex';
  }

  function selectCore(slotName, coreId) {
    // Update selection
    if (slotName === 'cpu') selectedCpu = coreId;
    else if (slotName === 'ppu') selectedPpu = coreId;
    else if (slotName === 'apu') selectedApu = coreId;
    else if (slotName === 'shader') selectedShader = coreId;
    
    // Save preference
    if (!gameSave.Preferences) gameSave.Preferences = {};
    gameSave.Preferences[slotName.toUpperCase()] = coreId;
    saveGameSave();
    
    // Update UI
    updateUI();
    closePicker();
  }

  function closePicker() {
    const modal = document.getElementById('pickerModal');
    modal.style.display = 'none';
    currentPickerSlot = null;
  }

  async function advanceLevel() {
    if (!levelCleared || stars < requiredStars) {
      return;
    }
    
    currentLevel++;
    gameSave.Level = currentLevel;
    gameSave.LevelCleared = false;
    levelCleared = false;
    
    await saveGameSave();
    
    loadLevel();
    updateUI();
    
    alert(`Advanced to Level ${currentLevel}!`);
  }

  function startGame() {
    // In web module, just show a message
    alert('Game starting! In the full application, this would launch the emulator with your selected configuration.');
  }

  // Expose API for debugging
  window.continueBuilder = {
    getGameSave: () => gameSave,
    getState: () => ({
      currentLevel,
      stars,
      selectedCpu,
      selectedPpu,
      selectedApu,
      selectedShader,
      selectedGameId
    }),
    reload: init
  };
})();
