// Cores WebModule - Display unlocked cores with grouping/sorting/filtering
(function() {
  const API_BASE = 'http://localhost:42067/api';
  
  // State
  let groupBy = 'None'; // None, Type, Category, Rating
  let sortBy = 'None';  // None, Alphabetical, Performance, Rating
  let sortDescending = false;
  let viewMode = 'Cards'; // Cards, List
  let allItems = [];
  let selectedCard = null;
  let modalClosing = false;
  
  // Initialize on load
  document.addEventListener('DOMContentLoaded', async () => {
    // Initialize pixel background
    if (window.homePixelBgEnsure) {
      window.homePixelBgEnsure();
    }
    
    // Load owned cores from save
    await loadCores();
    
    // Set up event listeners
    document.getElementById('groupBy').addEventListener('change', (e) => {
      groupBy = e.target.value;
      render();
    });
    
    document.getElementById('sortBy').addEventListener('change', (e) => {
      sortBy = e.target.value;
      // Set sensible default direction
      if (sortBy === 'Performance' || sortBy === 'Rating') {
        sortDescending = true;
      } else {
        sortDescending = false;
      }
      render();
    });
    
    document.getElementById('viewMode').addEventListener('change', (e) => {
      viewMode = e.target.value;
      render();
    });
    
    document.getElementById('flipOrder').addEventListener('click', () => {
      sortDescending = !sortDescending;
      render();
    });
    
    document.getElementById('returnBtn').addEventListener('click', (e) => {
      e.preventDefault();
      window.history.back();
    });
    
    // Modal backdrop click to close
    document.getElementById('modalBackdrop').addEventListener('click', (e) => {
      if (e.target.id === 'modalBackdrop' || e.target.id === 'modalContent' || e.target.closest('svg')) {
        closeCard();
      }
    });
    
    // Initial render
    render();
  });
  
  async function loadCores() {
    try {
      // Load game save to get owned cores
      const save = await loadGameSave();
      console.log('Game save loaded:', save);
      console.log('Raw owned CPU IDs:', save.ownedCpuIds);
      console.log('Raw owned PPU IDs:', save.ownedPpuIds);
      console.log('Raw owned APU IDs:', save.ownedApuIds);
      const ownedCpu = new Set((save.ownedCpuIds || []).map(id => id.toUpperCase()));
      const ownedPpu = new Set((save.ownedPpuIds || []).map(id => id.toUpperCase()));
      const ownedApu = new Set((save.ownedApuIds || []).map(id => id.toUpperCase()));
      const ownedClock = new Set((save.ownedClockIds || []).map(id => id.toUpperCase()));
      const ownedShader = new Set((save.ownedShaderIds || []).map(id => id.toUpperCase()));
      console.log('Owned CPU set:', Array.from(ownedCpu));
      console.log('Owned PPU set:', Array.from(ownedPpu));
      console.log('Owned APU set:', Array.from(ownedApu));
      
      // Fetch all core metadata from API
      const response = await fetch(`${API_BASE}/cores`);
      const data = await response.json();
      console.log('Cores metadata from API:', data);
      console.log('Owned sets - CPU:', ownedCpu.size, 'PPU:', ownedPpu.size, 'APU:', ownedApu.size);
      
      // Filter to owned cores only
      allItems = [];
      
      if (data.cpu) {
        data.cpu.forEach(core => {
          if (ownedCpu.has(core.id.toUpperCase())) {
            allItems.push({
              domain: 'CPU',
              id: core.id,
              shortName: core.id,
              displayName: core.name || core.id,
              description: core.description || '',
              performance: core.performance || 0,
              rating: Math.max(0, Math.min(5, core.rating || 0)),
              category: core.category || 'Uncategorized',
              key: `CPU:${core.id}`
            });
          }
        });
      }
      
      if (data.ppu) {
        data.ppu.forEach(core => {
          if (ownedPpu.has(core.id.toUpperCase())) {
            allItems.push({
              domain: 'PPU',
              id: core.id,
              shortName: core.id,
              displayName: core.name || core.id,
              description: core.description || '',
              performance: core.performance || 0,
              rating: Math.max(0, Math.min(5, core.rating || 0)),
              category: core.category || 'Uncategorized',
              key: `PPU:${core.id}`
            });
          }
        });
      }
      
      if (data.apu) {
        data.apu.forEach(core => {
          if (ownedApu.has(core.id.toUpperCase())) {
            allItems.push({
              domain: 'APU',
              id: core.id,
              shortName: core.id,
              displayName: core.name || core.id,
              description: core.description || '',
              performance: core.performance || 0,
              rating: Math.max(0, Math.min(5, core.rating || 0)),
              category: core.category || 'Uncategorized',
              key: `APU:${core.id}`
            });
          }
        });
      }
      
      if (data.clock) {
        data.clock.forEach(core => {
          if (ownedClock.has(core.id.toUpperCase())) {
            allItems.push({
              domain: 'CLOCK',
              id: core.id,
              shortName: core.id,
              displayName: core.name || core.id,
              description: core.description || '',
              performance: core.performance || 0,
              rating: Math.max(0, Math.min(5, core.rating || 0)),
              category: core.category || 'Uncategorized',
              key: `CLOCK:${core.id}`
            });
          }
        });
      }
      
      if (data.shader) {
        data.shader.forEach(core => {
          if (ownedShader.has(core.id.toUpperCase())) {
            allItems.push({
              domain: 'SHADER',
              id: core.id,
              shortName: core.id,
              displayName: core.name || core.id,
              description: core.description || '',
              performance: core.performance || 0,
              rating: Math.max(0, Math.min(5, core.rating || 0)),
              category: core.category || 'Uncategorized',
              key: `SHADER:${core.id}`
            });
          }
        });
      }
    } catch (error) {
      console.error('Error loading cores:', error);
    }
  }
  
  async function loadGameSave() {
    try {
      const response = await fetch(`${API_BASE}/save`);
      if (!response.ok) {
        // Return default empty save
        return {
          OwnedCpuIds: [],
          OwnedPpuIds: [],
          OwnedApuIds: [],
          OwnedClockIds: [],
          OwnedShaderIds: []
        };
      }
      return await response.json();
    } catch (error) {
      console.error('Error loading game save:', error);
      return {
        ownedCpuIds: [],
        ownedPpuIds: [],
        ownedApuIds: [],
        ownedClockIds: [],
        ownedShaderIds: []
      };
    }
  }
  
  function getSorted(items) {
    let list = [...items];
    
    if (sortBy === 'Alphabetical') {
      list.sort((a, b) => a.displayName.localeCompare(b.displayName, undefined, { sensitivity: 'base' }));
    } else if (sortBy === 'Performance') {
      list.sort((a, b) => {
        if (a.performance !== b.performance) return a.performance - b.performance;
        return a.displayName.localeCompare(b.displayName, undefined, { sensitivity: 'base' });
      });
    } else if (sortBy === 'Rating') {
      list.sort((a, b) => {
        if (a.rating !== b.rating) return a.rating - b.rating;
        return a.displayName.localeCompare(b.displayName, undefined, { sensitivity: 'base' });
      });
    }
    
    if (sortBy !== 'None' && sortDescending) {
      list.reverse();
    } else if (sortBy === 'None' && sortDescending) {
      list.reverse();
    }
    
    return list;
  }
  
  function render() {
    const container = document.getElementById('coresContainer');
    
    if (viewMode === 'List') {
      renderListView(container);
    } else {
      renderCardsView(container);
    }
  }
  
  function renderCardsView(container) {
    if (groupBy === 'Type') {
      const sections = [
        { title: 'CPU', items: allItems.filter(i => i.domain === 'CPU') },
        { title: 'PPU', items: allItems.filter(i => i.domain === 'PPU') },
        { title: 'APU', items: allItems.filter(i => i.domain === 'APU') },
        { title: 'Clock', items: allItems.filter(i => i.domain === 'CLOCK') },
        { title: 'Shaders', items: allItems.filter(i => i.domain === 'SHADER') }
      ];
      
      container.innerHTML = '<div class="card-sections">' +
        sections
          .filter(sec => sec.items.length > 0)
          .map(sec => `
            <section class="card-section">
              <h3 class="opt-h3">${sec.title}</h3>
              <div class="card-grid">
                ${getSorted(sec.items).map(item => renderCard(item)).join('')}
              </div>
            </section>
          `).join('') +
        '</div>';
    } else if (groupBy === 'Category') {
      const categoryMap = {};
      allItems.forEach(item => {
        const cat = item.category || 'Uncategorized';
        if (!categoryMap[cat]) categoryMap[cat] = [];
        categoryMap[cat].push(item);
      });
      
      const categories = Object.keys(categoryMap).sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));
      
      container.innerHTML = '<div class="card-sections">' +
        categories.map(cat => `
          <section class="card-section">
            <h3 class="opt-h3">${cat}</h3>
            <div class="card-grid">
              ${getSorted(categoryMap[cat]).map(item => renderCard(item)).join('')}
            </div>
          </section>
        `).join('') +
        '</div>';
    } else if (groupBy === 'Rating') {
      const buckets = [5, 4, 3, 2, 1, 0].map(r => ({
        title: r === 1 ? '1 STAR' : `${r} STARS`,
        items: allItems.filter(i => i.rating === r)
      }));
      
      container.innerHTML = '<div class="card-sections">' +
        buckets
          .filter(bucket => bucket.items.length > 0)
          .map(bucket => `
            <section class="card-section">
              <h3 class="opt-h3">${bucket.title}</h3>
              <div class="card-grid">
                ${getSorted(bucket.items).map(item => renderCard(item)).join('')}
              </div>
            </section>
          `).join('') +
        '</div>';
    } else {
      // No grouping
      container.innerHTML = `
        <section class="card-section">
          <div class="card-grid">
            ${getSorted(allItems).map(item => renderCard(item)).join('')}
          </div>
        </section>
      `;
    }
    
    // Add click handlers to cards
    container.querySelectorAll('.core-card').forEach(card => {
      card.addEventListener('click', () => {
        const key = card.getAttribute('data-key');
        const item = allItems.find(i => i.key === key);
        if (item) openCard(item);
      });
    });
  }
  
  function renderListView(container) {
    if (groupBy === 'Type') {
      const sections = [
        { title: 'CPU', items: allItems.filter(i => i.domain === 'CPU') },
        { title: 'PPU', items: allItems.filter(i => i.domain === 'PPU') },
        { title: 'APU', items: allItems.filter(i => i.domain === 'APU') },
        { title: 'Clock', items: allItems.filter(i => i.domain === 'CLOCK') },
        { title: 'Shaders', items: allItems.filter(i => i.domain === 'SHADER') }
      ];
      
      container.innerHTML = '<div class="list-sections">' +
        sections
          .filter(sec => sec.items.length > 0)
          .map(sec => `
            <section class="list-section">
              <h3 class="opt-h3">${sec.title}</h3>
              <div class="list-grid">
                ${getSorted(sec.items).map(item => renderRow(item)).join('')}
              </div>
            </section>
          `).join('') +
        '</div>';
    } else if (groupBy === 'Category') {
      const categoryMap = {};
      allItems.forEach(item => {
        const cat = item.category || 'Uncategorized';
        if (!categoryMap[cat]) categoryMap[cat] = [];
        categoryMap[cat].push(item);
      });
      
      const categories = Object.keys(categoryMap).sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));
      
      container.innerHTML = '<div class="list-sections">' +
        categories.map(cat => `
          <section class="list-section">
            <h3 class="opt-h3">${cat}</h3>
            <div class="list-grid">
              ${getSorted(categoryMap[cat]).map(item => renderRow(item)).join('')}
            </div>
          </section>
        `).join('') +
        '</div>';
    } else if (groupBy === 'Rating') {
      const buckets = [5, 4, 3, 2, 1, 0].map(r => ({
        title: r === 1 ? '1 STAR' : `${r} STARS`,
        items: allItems.filter(i => i.rating === r)
      }));
      
      container.innerHTML = '<div class="list-sections">' +
        buckets
          .filter(bucket => bucket.items.length > 0)
          .map(bucket => `
            <section class="list-section">
              <h3 class="opt-h3">${bucket.title}</h3>
              <div class="list-grid">
                ${getSorted(bucket.items).map(item => renderRow(item)).join('')}
              </div>
            </section>
          `).join('') +
        '</div>';
    } else {
      // No grouping
      container.innerHTML = `
        <section class="list-section">
          <div class="list-grid">
            ${getSorted(allItems).map(item => renderRow(item)).join('')}
          </div>
        </section>
      `;
    }
    
    // Add click handlers to rows
    container.querySelectorAll('.core-row').forEach(row => {
      row.addEventListener('click', () => {
        const key = row.getAttribute('data-key');
        const item = allItems.find(i => i.key === key);
        if (item) openCard(item);
      });
    });
  }
  
  function renderCard(item) {
    const cardUrl = `${API_BASE}/card/${item.domain.toLowerCase()}/${encodeURIComponent(item.id)}`;
    return `
      <div class="core-card" data-key="${item.key}">
        <img src="${cardUrl}" alt="${item.displayName}" style="width:100%;height:auto;display:block;">
      </div>
    `;
  }
  
  function renderRow(item) {
    const perfClass = item.performance > 0 ? 'perf-pos' : (item.performance < 0 ? 'perf-neg' : '');
    const stars = renderStars(item.rating);
    const category = item.category || item.domain;
    
    return `
      <button type="button" class="core-row" data-key="${item.key}">
        <span class="row-left">
          <span class="pill pill-${item.domain.toLowerCase()}">${item.domain}</span>
        </span>
        <span class="row-main">${item.displayName}</span>
        <span class="row-meta">
          <span class="meta-badge">${category}</span>
          <span class="meta-perf ${perfClass}" title="Performance">${item.performance}</span>
          <span class="meta-stars" title="Rating">${stars}</span>
        </span>
      </button>
    `;
  }
  
  function renderStars(rating) {
    const filled = '\u2605';
    const empty = '\u2606';
    const r = Math.max(0, Math.min(5, rating));
    return filled.repeat(r) + empty.repeat(5 - r);
  }
  
  async function openCard(item) {
    modalClosing = false;
    selectedCard = item;
    
    const backdrop = document.getElementById('modalBackdrop');
    const content = document.getElementById('modalContent');
    
    const cardUrl = `${API_BASE}/card/${item.domain.toLowerCase()}/${encodeURIComponent(item.id)}`;
    
    content.innerHTML = `<img src="${cardUrl}" alt="${item.displayName}" style="width:min(98vw,calc(98vh * 0.7059));max-width:98vw;height:auto;max-height:98vh;display:block;aspect-ratio:240/340;">`;
    
    backdrop.style.display = 'flex';
    backdrop.classList.remove('closing');
    backdrop.classList.add('opening');
    content.classList.remove('closing');
    content.classList.add('opening');
  }
  
  function closeCard() {
    if (!selectedCard) return;
    
    modalClosing = true;
    const backdrop = document.getElementById('modalBackdrop');
    const content = document.getElementById('modalContent');
    
    backdrop.classList.remove('opening');
    backdrop.classList.add('closing');
    content.classList.remove('opening');
    content.classList.add('closing');
    
    setTimeout(() => {
      backdrop.style.display = 'none';
      content.innerHTML = '';
      selectedCard = null;
      modalClosing = false;
    }, 180);
  }
})();
