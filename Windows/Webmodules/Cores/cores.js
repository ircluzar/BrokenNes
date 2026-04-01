// Cores WebModule - Display unlocked cores with grouping/sorting/filtering
(function() {
  const api = window.webapi;
  const cardSvgCache = new Map();
  let cardFontReadyPromise = null;

  function normalizeCardDomain(domain) {
    return String(domain || '').trim().toUpperCase();
  }

  function normalizeCardId(id) {
    return String(id || '').trim();
  }

  function escapeHtml(value) {
    return String(value || '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function applySvgRenderQuality(rootEl) {
    const svg = rootEl?.querySelector('svg');
    if (!svg) {
      return false;
    }

    svg.classList.add('core-card-svg');
    if (!svg.hasAttribute('preserveAspectRatio')) {
      svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
    }
    if (!svg.hasAttribute('shape-rendering')) {
      svg.setAttribute('shape-rendering', 'geometricPrecision');
    }
    if (!svg.hasAttribute('text-rendering')) {
      svg.setAttribute('text-rendering', 'geometricPrecision');
    }
    if (!svg.hasAttribute('color-rendering')) {
      svg.setAttribute('color-rendering', 'optimizeQuality');
    }

    return true;
  }

  async function ensureCardFontReady() {
    if (!document.fonts?.load) {
      return;
    }

    cardFontReadyPromise ??= Promise.race([
      Promise.all([
        document.fonts.load("12px 'Press Start 2P'"),
        document.fonts.ready
      ]),
      new Promise(resolve => window.setTimeout(resolve, 1500))
    ]).catch(error => {
      console.warn('[Cores] Card font preload failed:', error);
    });

    await cardFontReadyPromise;
  }

  async function getCoreSvgMarkup(domain, id) {
    const normalizedDomain = normalizeCardDomain(domain);
    const normalizedId = normalizeCardId(id);
    if (!normalizedDomain || !normalizedId || !api?.card?.getSvg) {
      return '';
    }

    const cacheKey = `${normalizedDomain}:${normalizedId}`;
    if (cardSvgCache.has(cacheKey)) {
      return cardSvgCache.get(cacheKey);
    }

    try {
      const result = await api.card.getSvg(normalizedDomain, normalizedId);
      const markup = result?.success && result?.text ? result.text : '';
      cardSvgCache.set(cacheKey, markup);
      return markup;
    } catch (error) {
      console.warn(`[Cores] Failed to load SVG for ${normalizedDomain}/${normalizedId}:`, error);
      cardSvgCache.set(cacheKey, '');
      return '';
    }
  }

  async function renderCardMarkupInto(hostEl, item) {
    if (!hostEl || !item) {
      return;
    }

    const requestKey = `${item.key}:${Date.now()}:${Math.random().toString(36).slice(2, 8)}`;
    hostEl.dataset.requestKey = requestKey;
    hostEl.innerHTML = '<div class="card-loading">Loading...</div>';

    const svgMarkup = await getCoreSvgMarkup(item.domain, item.id);
    if (hostEl.dataset.requestKey !== requestKey) {
      return;
    }

    if (svgMarkup) {
      await ensureCardFontReady();
    }

    if (hostEl.dataset.requestKey !== requestKey) {
      return;
    }

    hostEl.innerHTML = svgMarkup || `<div class="card-fallback-label">${escapeHtml(item.displayName)}</div>`;
    applySvgRenderQuality(hostEl);
  }
  
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
      window.location.href = '../Home/index.html?skipHW=1';
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
      // Load game save using shared gameSave module (no more API call!)
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
      const data = api?.cores?.list ? await api.cores.list() : null;
      const roster = api?.progression?.getRoster ? await api.progression.getRoster() : null;
      const authoredCatalog = api?.card?.getCatalog ? await api.card.getCatalog() : null;
      const authoredCardIndex = createAuthoredCardIndex(authoredCatalog);
      console.log('Cores metadata from API:', data);
      console.log('Owned sets - CPU:', ownedCpu.size, 'PPU:', ownedPpu.size, 'APU:', ownedApu.size);
      
      // Filter to owned cores only
      allItems = [];
      
      if (data?.cpu) {
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
      
      if (data?.ppu) {
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
      
      if (data?.apu) {
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
      
      if (data?.clock) {
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
      
      if (data?.shader) {
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

      if (roster && roster.success !== false) {
        (Array.isArray(roster.webmodules) ? roster.webmodules : []).forEach(module => {
          if (!module?.unlocked || !module?.id) {
            return;
          }

          const authored = getAuthoredCardMeta(authoredCardIndex, 'WEBMODULE', module.id);

          allItems.push({
            domain: 'WEBMODULE',
            id: module.id,
            shortName: authored?.shortName || buildShortName(module.id),
            displayName: authored?.displayName || module.title || prettifyName(module.id),
            description: authored?.description || module.description || 'BrokenNes webmodule unlock.',
            performance: 0,
            rating: authored?.rating ?? getProgressionRating('WEBMODULE', module.id, module),
            category: authored?.category || module.displayMode || 'Webmodule',
            key: `WEBMODULE:${module.id}`
          });
        });

        (Array.isArray(roster.backgrounds) ? roster.backgrounds : []).forEach(entry => {
          if (!entry?.unlocked || !entry?.id) {
            return;
          }

          const authored = getAuthoredCardMeta(authoredCardIndex, 'BACKGROUND', entry.id);

          allItems.push({
            domain: 'BACKGROUND',
            id: entry.id,
            shortName: authored?.shortName || buildShortName(entry.id),
            displayName: authored?.displayName || entry.id,
            description: authored?.description || buildBackgroundDescription(entry.id),
            performance: 0,
            rating: authored?.rating ?? getProgressionRating('BACKGROUND', entry.id, entry),
            category: authored?.category || 'Background',
            key: `BACKGROUND:${entry.id}`
          });
        });

        (Array.isArray(roster.nullProviders) ? roster.nullProviders : []).forEach(entry => {
          if (!entry?.unlocked || !entry?.id) {
            return;
          }

          const authored = getAuthoredCardMeta(authoredCardIndex, 'NULLPROVIDER', entry.id);

          allItems.push({
            domain: 'NULLPROVIDER',
            id: entry.id,
            shortName: authored?.shortName || buildShortName(entry.id),
            displayName: authored?.displayName || entry.id,
            description: authored?.description || `Crash-visualizer unlock: ${prettifyName(entry.id)}.`,
            performance: 0,
            rating: authored?.rating ?? getProgressionRating('NULLPROVIDER', entry.id, entry),
            category: authored?.category || 'Null Provider',
            key: `NULLPROVIDER:${entry.id}`
          });
        });

        (Array.isArray(roster.features) ? roster.features : []).forEach(entry => {
          if (!entry?.unlocked || !entry?.id) {
            return;
          }

          const authored = getAuthoredCardMeta(authoredCardIndex, 'FEATURE', entry.id);

          allItems.push({
            domain: 'FEATURE',
            id: entry.id,
            shortName: authored?.shortName || buildShortName(entry.id),
            displayName: authored?.displayName || prettifyName(entry.id),
            description: authored?.description || `BrokenNes feature unlock: ${prettifyName(entry.id)}.`,
            performance: 0,
            rating: authored?.rating ?? getProgressionRating('FEATURE', entry.id, entry),
            category: authored?.category || 'Feature',
            key: `FEATURE:${entry.id}`
          });
        });
      }
    } catch (error) {
      console.error('Error loading cores:', error);
    }
  }

  function prettifyName(value) {
    return String(value || '')
      .replace(/[_-]+/g, ' ')
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .trim() || 'Unknown';
  }

  function buildShortName(value) {
    const compact = String(value || '').replace(/[^a-z0-9]/gi, '').toUpperCase();
    if (!compact) {
      return 'CARD';
    }
    return compact.length <= 4 ? compact : compact.slice(0, 4);
  }

  function normalizeCatalogDomain(domain) {
    return String(domain || '').trim().replace(/\s+/g, '').toUpperCase();
  }

  function normalizeCatalogId(domain, id) {
    const normalizedDomain = normalizeCatalogDomain(domain);
    const trimmed = String(id || '').trim();

    if (normalizedDomain === 'BACKGROUND') {
      if (/^(gradient|gradient \(default\)|staticgradient)$/i.test(trimmed)) {
        return 'Gradient (Default)';
      }
      if (/^(black|none|none \(black\))$/i.test(trimmed)) {
        return 'None (Black)';
      }
    }

    return trimmed;
  }

  function buildCatalogKey(domain, id) {
    return `${normalizeCatalogDomain(domain)}:${normalizeCatalogId(domain, id)}`;
  }

  function createAuthoredCardIndex(catalogResult) {
    const index = new Map();
    const cards = Array.isArray(catalogResult?.cards) ? catalogResult.cards : [];
    cards.forEach(card => {
      if (!card?.domain || !card?.id) {
        return;
      }
      index.set(buildCatalogKey(card.domain, card.id), card);
    });
    return index;
  }

  function getAuthoredCardMeta(index, domain, id) {
    if (!(index instanceof Map)) {
      return null;
    }
    return index.get(buildCatalogKey(domain, id)) || null;
  }

  function buildBackgroundDescription(id) {
    if (id === 'Gradient (Default)') {
      return 'Default menu background with a clean static gradient.';
    }
    if (id === 'None (Black)') {
      return 'Pure black backdrop for minimal presentation.';
    }
    return `Procedural renderer background: ${prettifyName(id)}.`;
  }

  function getProgressionRating(domain, id, meta) {
    const normalizedId = String(id || '').toUpperCase();
    switch (domain) {
      case 'WEBMODULE':
        if (['GLITCHHARVESTER', 'TIMEJUMP', 'IMAGINEBUG'].includes(normalizedId)) return 5;
        if (['DECKBUILDER', 'CONTINUE', 'CORRUPTIONSLOP'].includes(normalizedId)) return 4;
        return meta?.displayMode === 'Overlay' ? 4 : 3;
      case 'BACKGROUND':
        if (normalizedId === 'GRADIENT (DEFAULT)') return 2;
        if (normalizedId === 'NONE (BLACK)') return 1;
        return 3;
      case 'NULLPROVIDER':
        if (normalizedId === 'STATIC' || normalizedId === 'VOID') return 1;
        return 3;
      case 'FEATURE':
        if (['SAVESTATES', 'RTC', 'GH', 'IMAGINE'].includes(normalizedId)) return 5;
        if (normalizedId === 'DEBUG') return 4;
        return 3;
      default:
        return 2;
    }
  }
  
  async function loadGameSave() {
    try {
      // Use shared gameSave module instead of API
      if (window.gameSave && typeof window.gameSave.load === 'function') {
        return await window.gameSave.load();
      } else {
        console.error('[Cores] gameSave module not available');
        // Return default empty save
        return {
          ownedCpuIds: ['FMC'],
          ownedPpuIds: ['FMC'],
          ownedApuIds: ['FMC'],
          ownedClockIds: ['FMC'],
          ownedShaderIds: ['PX']
        };
      }
    } catch (error) {
      console.error('Error loading game save:', error);
      return {
        ownedCpuIds: ['FMC'],
        ownedPpuIds: ['FMC'],
        ownedApuIds: ['FMC'],
        ownedClockIds: ['FMC'],
        ownedShaderIds: ['PX']
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
        { title: 'Shaders', items: allItems.filter(i => i.domain === 'SHADER') },
        { title: 'Modules', items: allItems.filter(i => i.domain === 'WEBMODULE') },
        { title: 'Features', items: allItems.filter(i => i.domain === 'FEATURE') },
        { title: 'Backgrounds', items: allItems.filter(i => i.domain === 'BACKGROUND') },
        { title: 'Null Providers', items: allItems.filter(i => i.domain === 'NULLPROVIDER') }
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

    container.querySelectorAll('.core-card').forEach(card => {
      const key = card.getAttribute('data-key');
      const item = allItems.find(i => i.key === key);
      if (!item) {
        return;
      }

      const host = card.querySelector('.core-card-art');
      if (host) {
        void renderCardMarkupInto(host, item);
      }
    });
  }
  
  function renderListView(container) {
    if (groupBy === 'Type') {
      const sections = [
        { title: 'CPU', items: allItems.filter(i => i.domain === 'CPU') },
        { title: 'PPU', items: allItems.filter(i => i.domain === 'PPU') },
        { title: 'APU', items: allItems.filter(i => i.domain === 'APU') },
        { title: 'Clock', items: allItems.filter(i => i.domain === 'CLOCK') },
        { title: 'Shaders', items: allItems.filter(i => i.domain === 'SHADER') },
        { title: 'Modules', items: allItems.filter(i => i.domain === 'WEBMODULE') },
        { title: 'Features', items: allItems.filter(i => i.domain === 'FEATURE') },
        { title: 'Backgrounds', items: allItems.filter(i => i.domain === 'BACKGROUND') },
        { title: 'Null Providers', items: allItems.filter(i => i.domain === 'NULLPROVIDER') }
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
    return `
      <div class="core-card" data-key="${item.key}">
        <div class="core-card-art" role="img" aria-label="${escapeHtml(item.displayName)}">
          <div class="card-loading">Loading...</div>
        </div>
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
    
    content.innerHTML = '<div class="card-loading">Loading...</div>';
    void renderCardMarkupInto(content, item);
    
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
