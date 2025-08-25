// continue-db.js — global IndexedDB helper for BrokenNes
// Provides: window.continueDb with open/get/put/delete/clear, export/import, and startup seeding from default-db.json
(function () {
  const DB_NAME = 'continue-db';
  const DB_VERSION = 1;
  const DEFAULT_SEED_URL = './models/default-db.json';
  const AUTO_SEED_KEY = 'continueDb:autoSeedEnabled';

  let _dbPromise = null;

  function open() {
    if (_dbPromise) return _dbPromise;
    _dbPromise = new Promise((resolve, reject) => {
      const req = indexedDB.open(DB_NAME, DB_VERSION);
      req.onupgradeneeded = (e) => {
        const db = req.result;
        // Create object stores if missing
        if (!db.objectStoreNames.contains('games')) {
          db.createObjectStore('games', { keyPath: 'id' });
        }
        if (!db.objectStoreNames.contains('achievements')) {
          const store = db.createObjectStore('achievements', { keyPath: 'id' });
          try { store.createIndex('by_gameId', 'gameId', { unique: false }); } catch {}
        }
        if (!db.objectStoreNames.contains('cards')) {
          db.createObjectStore('cards', { keyPath: 'id' });
        }
        if (!db.objectStoreNames.contains('levels')) {
          db.createObjectStore('levels', { keyPath: 'index' });
        }
        if (!db.objectStoreNames.contains('save')) {
          db.createObjectStore('save', { keyPath: 'id' });
        }
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error || new Error('IndexedDB open error'));
    });
    return _dbPromise;
  }

  function tx(db, store, mode = 'readonly') {
    return db.transaction(store, mode).objectStore(store);
  }

  async function getAll(store) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const r = tx(db, store, 'readonly').getAll();
      r.onsuccess = () => resolve(r.result || []);
      r.onerror = () => reject(r.error);
    });
  }

  async function get(store, key) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const r = tx(db, store, 'readonly').get(key);
      r.onsuccess = () => resolve(r.result);
      r.onerror = () => reject(r.error);
    });
  }

  async function put(store, value) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const r = tx(db, store, 'readwrite').put(value);
      r.onsuccess = () => resolve(r.result);
      r.onerror = () => reject(r.error);
    });
  }

  async function del(store, key) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const r = tx(db, store, 'readwrite').delete(key);
      r.onsuccess = () => resolve();
      r.onerror = () => reject(r.error);
    });
  }

  async function clear(store) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const r = tx(db, store, 'readwrite').clear();
      r.onsuccess = () => resolve();
      r.onerror = () => reject(r.error);
    });
  }

  async function putMany(store, items) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const t = db.transaction(store, 'readwrite');
      const s = t.objectStore(store);
      for (const it of items || []) s.put(it);
      t.oncomplete = () => resolve();
      t.onerror = () => reject(t.error);
    });
  }

  function nowIso() {
    try { return new Date().toISOString(); } catch { return '' }
  }

  async function exportAll() {
    const [games, achievements, cards, levels, save] = await Promise.all([
      getAll('games'), getAll('achievements'), getAll('cards'), getAll('levels'), getAll('save')
    ]);
    return {
      format: 'continue-db/v1',
      version: 1,
      exportedAt: nowIso(),
      data: { games, achievements, cards, levels, save }
    };
  }

  function downloadJson(obj, filename = 'continue-db.json') {
    const blob = new Blob([JSON.stringify(obj, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
  }

  async function exportAllToDownload() {
    const payload = await exportAll();
    downloadJson(payload, `continue-db-${Date.now()}.json`);
  }

  function isValidPayload(payload) {
    return payload && payload.format === 'continue-db/v1' && payload.data && typeof payload.data === 'object';
  }

  async function importAll(payload, { replace = true } = {}) {
    if (!isValidPayload(payload)) throw new Error('Invalid continue-db payload');
    const { games = [], achievements = [], cards = [], levels = [], save = [] } = payload.data || {};
    if (replace) {
      await Promise.all(['games', 'achievements', 'cards', 'levels', 'save'].map(clear));
    }
    await Promise.all([
      putMany('games', games),
      putMany('achievements', achievements),
      putMany('cards', cards),
      putMany('levels', levels),
      putMany('save', save)
    ]);
  }

  async function importFromFileInput() {
    // Attempt to find the CRUD toolbar file input or any file input as fallback
    const input = document.querySelector('.crud-toolbar input[type=file]') || document.querySelector('input[type=file]');
    if (!input || !input.files || input.files.length === 0) throw new Error('No file selected');
    const file = input.files[0];
    const text = await file.text();
    const payload = JSON.parse(text);
    await importAll(payload, { replace: true });
  }

  async function isEmpty() {
    // Check if all primary stores are empty
    const [g, a, c, l] = await Promise.all([
      getAll('games'), getAll('achievements'), getAll('cards'), getAll('levels')
    ]);
    return (g.length + a.length + c.length + l.length) === 0;
  }

  async function seedIfEmpty(url = DEFAULT_SEED_URL) {
    // Legacy helper retained for authoring mode; not used by default startup path
    try {
      await open();
      if (!(await isEmpty())) return false;
      const res = await fetch(url, { cache: 'no-cache' });
      if (!res.ok) throw new Error('Seed fetch failed: ' + res.status);
      const payload = await res.json();
      await importAll(payload, { replace: true });
      return true;
    } catch (e) {
      console.warn('[continueDb] seedIfEmpty failed:', e);
      return false;
    }
  }

  async function loadDefaultAtStartup(url = DEFAULT_SEED_URL) {
    // Always load the bundled default DB at startup (treat as immutable game data)
    try {
      await open();
      const res = await fetch(url, { cache: 'no-cache' });
      if (!res.ok) throw new Error('Default DB fetch failed: ' + res.status);
      const payload = await res.json();
      await importAll(payload, { replace: true });
      return true;
    } catch (e) {
      console.warn('[continueDb] loadDefaultAtStartup failed:', e);
      return false;
    }
  }

  async function clearAll() {
    await Promise.all(['games', 'achievements', 'cards', 'levels', 'save'].map(clear));
  }

  // Auto-seed toggle helpers (persisted in localStorage)
  function getAutoSeedEnabled() {
    try {
      const v = localStorage.getItem(AUTO_SEED_KEY);
      if (v === null) return true; // default ON (current behavior)
      // Accept 'true'/'false' or '1'/'0'
      return v === 'true' || v === '1';
    } catch { return true; }
  }

  function setAutoSeedEnabled(value) {
    try {
      localStorage.setItem(AUTO_SEED_KEY, value ? 'true' : 'false');
    } catch {}
  }

  // Expose API
  window.continueDb = {
    open,
    getAll,
    get,
    put,
    delete: del,
    clear,
    putMany,
    exportAll,
    exportAllToDownload,
    importAll,
    importFromFileInput,
  seedIfEmpty,
  loadDefaultAtStartup,
    clearAll,
  getAutoSeedEnabled,
  setAutoSeedEnabled,
    _meta: { DB_NAME, DB_VERSION }
  };

  // Kick off seeding early in app startup
  // Fire-and-forget; the app reads data after seed finishes or works with empty DB otherwise.
  try {
    if (getAutoSeedEnabled()) {
      loadDefaultAtStartup();
    }
  } catch {
    try { loadDefaultAtStartup(); } catch {}
  }
})();
