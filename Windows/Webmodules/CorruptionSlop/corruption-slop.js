// Corruption Slop WebModule - Overlay automation, on the controller standard (shared/controls, docs/projects/controller-standard-plan.md).
// The loop: load the entry state, blast random bit flips, wait, repeat; now and then an "intermission" (a new background and null
// provider, the built-in page, then the game again from its entry state). Every number the loop uses is a parameter on the faceplate.
(() => {
  const api = window.webapi;
  const STOP_NAME = 'Corruption Slop';
  const STORE = 'corruptionSlop.params.v1';

  let isRunning = false;
  let loopToken = 0;
  let stopRequested = false;
  let baseStateId = null;
  let cachedBackgrounds = null;
  let cachedNullProviders = null;
  let originalRomPath = null;
  let cycleCount = 0;
  let blastCount = 0;
  let intensitySyncTimer = null;
  let panel = null;

  // ------------------------------------------------------------------ the parameters (defaults = the values the script had built in)
  const saved = (() => { try { return JSON.parse(localStorage.getItem(STORE) || '{}') || {}; } catch { return {}; } })();
  const values = {};
  function persist() { try { localStorage.setItem(STORE, JSON.stringify(values)); } catch { /* private window: values just do not stick */ } }
  function def(d) {
    const v = saved[d.id];
    values[d.id] = v === undefined || v === null ? d.default : v;
    return BNControls.param(Object.assign(d, { get: () => values[d.id], set: (x) => { values[d.id] = x; persist(); } }));
  }
  const P = {
    intensity: def({ id: 'intensity', label: 'Adjust until funny', min: 0, max: 100, default: 50, step: 1, unit: '%', format: temperatureLabel }),
    base: def({ id: 'base', label: 'Base intensity', short: 'Base', min: 50, max: 2000, default: 420, step: 1, taper: 'log', format: (v) => `${Math.round(v)} flips` }),
    paceMin: def({ id: 'paceMin', label: 'Fastest pause', short: 'Fast', min: 100, max: 10000, default: 420, step: 10, taper: 'log', unit: 'ms', format: ms }),
    paceMax: def({ id: 'paceMax', label: 'Slowest pause', short: 'Slow', min: 100, max: 10000, default: 4200, step: 10, taper: 'log', unit: 'ms', format: ms }),
    chance: def({ id: 'chance', label: 'Intermission chance', short: 'Interm.', min: 0, max: 100, default: 15, step: 1, unit: '%' }),
    minCycles: def({ id: 'minCycles', label: 'Cycles before an intermission', short: 'Min cyc', min: 1, max: 100, default: 10, step: 1, format: (v) => `${Math.round(v)} cycles` }),
    guarantee: def({ id: 'guarantee', label: 'Intermission certain after', short: 'Certain',
      steps: [{ value: 0, label: 'never' }, { value: 15, label: '15' }, { value: 25, label: '25' }, { value: 50, label: '50' }, { value: 100, label: '100' }], default: 25 }),
  };
  function ms(v) { return v >= 1000 ? `${(v / 1000).toFixed(v >= 10000 ? 0 : 1)} s` : `${Math.round(v)} ms`; }

  const LAYOUT = {
    title: 'Corruption Slop', cols: 6, rows: 3, accent: '#e0523b',
    sections: [{ title: 'Blast', x: 0, y: 0, w: 3, h: 2 }, { title: 'Intermission', x: 3, y: 0, w: 2, h: 2 }],
    controls: [
      { type: 'knob', param: 'intensity', x: 0, y: 0, w: 2, h: 2 },
      { type: 'knob', param: 'base', x: 2, y: 0 },
      { type: 'knob', param: 'paceMin', x: 2, y: 1, label: 'Fast' },
      { type: 'knob', param: 'chance', x: 3, y: 0 },
      { type: 'knob', param: 'minCycles', x: 4, y: 0 },
      { type: 'knob', param: 'guarantee', x: 3, y: 1 },
      { type: 'knob', param: 'paceMax', x: 4, y: 1, label: 'Slow' },
      { type: 'button', id: 'stop', label: 'Stop', glyph: '■', danger: true, x: 5, y: 0 },
      { type: 'led', id: 'blast', label: 'Blast', x: 5, y: 1 },
      { type: 'display', id: 'status', text: 'starting', label: 'Status', x: 0, y: 2, w: 3, live: true },
      { type: 'display', id: 'count', text: '0 cycles', label: 'Blasts', x: 3, y: 2, w: 3 },
    ],
  };

  document.addEventListener('DOMContentLoaded', () => {
    if (!api) { console.error('[CorruptionSlop] webapi helper not loaded'); return; }
    if (!window.BNControls) { console.error('[CorruptionSlop] controller library not loaded'); return; }
    panel = BNControls.createPanel(document.getElementById('faceplate'), LAYOUT, Object.values(P), {
      onChange: (id) => {
        // the pause range keeps its order: moving one end past the other drags it along
        if (id === 'paceMin' && values.paceMin > values.paceMax) panel.set('paceMax', values.paceMin);
        if (id === 'paceMax' && values.paceMax < values.paceMin) panel.set('paceMin', values.paceMax);
        if (id === 'intensity' || id === 'base') scheduleIntensitySync();
      },
      onPress: (id) => { if (id === 'stop') stopAndExit(); },
    });
    start();
  });

  function setStatus(t) { panel?.setText('status', t); }
  function setCount() { panel?.setText('count', `${blastCount} blasts · ${cycleCount} cyc`); }

  async function start() {
    const valid = await ensureGameRunning();
    if (!valid) { await goToEmulator(); return; }

    try { await api.ui.hideMenu(); } catch (error) { console.warn('[CorruptionSlop] Failed to hide menu:', error); }
    try { await api.rtc.setAutoCorrupt(false); } catch (error) { console.warn('[CorruptionSlop] Failed to disable auto-corrupt:', error); }

    setStatus('setting up');
    const configured = await configureRtc();
    if (!configured) { await goToEmulator(); return; }

    originalRomPath = await captureCurrentRomPath();

    const baseReady = await createBaseState();
    if (!baseReady) { await goToEmulator(); return; }

    cycleCount = 0; blastCount = 0; setCount();
    isRunning = true;
    loopToken += 1;
    setStatus('slopping');
    runLoop(loopToken).catch(error => {
      console.error('[CorruptionSlop] Loop error:', error);
      stopAndExit();
    });
  }

  async function ensureGameRunning() {
    try {
      const result = await api.timejump.validateRom();
      return !!result?.valid;
    } catch (error) {
      console.error('[CorruptionSlop] Failed to validate ROM:', error);
      return false;
    }
  }

  async function configureRtc() {
    try {
      const blastType = await api.rtc.setBlastType('BITFLIP');
      if (!blastType?.success) return false;

      const intensity = await api.rtc.setIntensity(getScaledIntensity());
      if (!intensity?.success) return false;

      const domains = await api.rtc.getDomains();
      if (!domains?.success) return false;

      const allDomainKeys = (domains.domains || []).map(d => d.key);
      const select = await api.rtc.setDomainSelection(allDomainKeys);
      return !!select?.success;
    } catch (error) {
      console.error('[CorruptionSlop] RTC configuration failed:', error);
      return false;
    }
  }

  async function createBaseState() {
    try {
      const response = await api.gh.addBaseState(STOP_NAME);
      if (!response?.success) return false;
      const base = response.base || response.baseState || response.state || response.result || response.entry || response;
      baseStateId = base?.id || base?.Id || response.id || response.baseId;
      return !!baseStateId;
    } catch (error) {
      console.error('[CorruptionSlop] Failed to create base state:', error);
      return false;
    }
  }

  async function runLoop(token) {
    while (isRunning && token === loopToken) {
      if (shouldIntermission()) {
        setStatus('intermission');
        const intermissionOk = await runIntermission();
        if (!intermissionOk) console.warn('[CorruptionSlop] Intermission failed, continuing cycle');
        else cycleCount = 0;
        if (stopRequested) break;
        setStatus('slopping');
      }

      const ok = await runCycle();
      if (!ok) break;

      cycleCount += 1; blastCount += 1;
      panel?.flash('blast', 140);
      setCount();

      const lo = Math.min(values.paceMin, values.paceMax), hi = Math.max(values.paceMin, values.paceMax);
      await sleep(randomInt(Math.round(lo), Math.round(hi)));
    }
  }

  async function runCycle() {
    if (!baseStateId || stopRequested) return false;
    const loaded = await api.gh.loadBase(baseStateId);
    if (!loaded?.success || stopRequested) return false;
    const blasted = await api.rtc.blast();
    return !!blasted?.success && !stopRequested;
  }

  async function runIntermission() {
    if (!baseStateId || stopRequested) return false;

    const closed = await api.emulator.closeRom();
    if (!closed?.success || stopRequested) return false;

    const background = await getRandomBackground();
    if (background) await api.emulator.setBackground(background);

    const provider = await getRandomNullProvider();
    if (provider) await api.emulator.setNullProvider(provider);

    await sleep(666);
    if (stopRequested) return false;

    const pageLoaded = await api.emulator.loadBuiltInRom('page5.nes', true);
    if (!pageLoaded?.success || stopRequested) return false;

    await sleep(2200);
    if (stopRequested) return false;

    if (originalRomPath) {
      const reloaded = await api.emulator.loadRom(originalRomPath);
      if (!reloaded?.success || stopRequested) return false;
      await sleep(150);
    }

    const restored = await api.gh.loadBase(baseStateId);
    return !!restored?.success && !stopRequested;
  }

  async function stopAndExit() {
    if (stopRequested) return;
    stopRequested = true;
    isRunning = false;
    loopToken += 1;
    setStatus('stopping');

    try { await api.rtc.setAutoCorrupt(false); } catch (error) { console.warn('[CorruptionSlop] Failed to disable auto-corrupt on stop:', error); }
    await sleep(50);
    if (baseStateId) {
      try { await api.gh.loadBase(baseStateId); } catch (error) { console.warn('[CorruptionSlop] Failed to reload base state on stop:', error); }
    }
    try { await api.ui.showMenu(); } catch (error) { console.warn('[CorruptionSlop] Failed to show menu:', error); }
    await goToEmulator();
  }

  async function goToEmulator() {
    try { await api.navigation.goToEmulator(); } catch (error) { console.error('[CorruptionSlop] Failed to return to emulator:', error); }
  }

  function sleep(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

  function shouldIntermission() {
    if (cycleCount < values.minCycles) return false;
    if (values.guarantee > 0 && cycleCount > values.guarantee) return true;
    return Math.random() < values.chance / 100;
  }

  async function getRandomBackground() {
    if (!cachedBackgrounds) {
      const result = await api.emulator.getBackgrounds();
      if (!result?.success) return null;
      cachedBackgrounds = result.backgrounds || [];
    }
    if (!cachedBackgrounds.length) return null;
    return cachedBackgrounds[randomInt(0, cachedBackgrounds.length - 1)];
  }

  async function getRandomNullProvider() {
    if (!cachedNullProviders) {
      const result = await api.emulator.getNullProviders();
      if (!result?.success) return null;
      cachedNullProviders = result.providers || [];
    }
    if (!cachedNullProviders.length) return null;
    return cachedNullProviders[randomInt(0, cachedNullProviders.length - 1)];
  }

  function randomInt(min, max) { return Math.floor(Math.random() * (max - min + 1)) + min; }

  async function captureCurrentRomPath() {
    try {
      const result = await api.emulator.getCurrentRom();
      if (!result?.success) return null;
      return result.path || null;
    } catch (error) {
      console.warn('[CorruptionSlop] Failed to read current ROM path:', error);
      return null;
    }
  }

  // The big knob's read-out, as the old knob had it: mild in the middle, colder below, hotter above.
  function temperatureLabel(percent) {
    const p = Math.max(0, Math.min(100, Number(percent) || 0));
    if (p >= 40 && p <= 60) return 'Mild';
    if (p < 40) return `${Math.round(((40 - p) / 40) * 100)}% cold`;
    return `${Math.round(((p - 60) / 40) * 100)}% hot`;
  }

  function scheduleIntensitySync() {
    if (intensitySyncTimer) clearTimeout(intensitySyncTimer);
    intensitySyncTimer = setTimeout(async () => {
      intensitySyncTimer = null;
      try { await api.rtc.setIntensity(getScaledIntensity()); } catch (error) { console.warn('[CorruptionSlop] Failed to update intensity from knob:', error); }
    }, 60);
  }

  // 50 % = the base intensity; 0 % = 5 % of it; 100 % = three times it (the old knob's curve).
  function getScaledIntensity() {
    const p = Math.max(0, Math.min(100, values.intensity));
    const multiplier = p <= 50 ? 0.05 + (p / 50) * 0.95 : 1 + ((p - 50) / 50) * 2;
    return Math.max(1, Math.round(values.base * multiplier));
  }

  // for tests
  window.corruptionSlop = { values, getScaledIntensity, shouldIntermission: () => shouldIntermission(), get panel() { return panel; } };
})();
