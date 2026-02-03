(function () {
  'use strict';

  const STORAGE_BASE_ID = 'imagineBug.baseId';

  const state = {
    baseId: null,
    range: null,
    drawing: false,
    busy: false,
    modelLoading: false,
    phase: 'idle'
  };

  const elements = {
    btnPrimary: null,
    iconCrosshair: null,
    iconRetry: null,
    btnMenu: null,
    drawOverlay: null,
    drawCanvas: null
  };

  let drawContext = null;
  let drawStart = null;
  let drawCurrent = null;

  window.addEventListener('DOMContentLoaded', init);
  window.addEventListener('resize', resizeCanvas);

  async function init() {
    bindElements();
    attachEvents();
    resizeCanvas();

    await ensureOverlayMode();
    await loadStoredBase();

    updateUI();
  }

  function bindElements() {
    elements.btnPrimary = document.getElementById('btnPrimary');
    elements.iconCrosshair = document.getElementById('iconCrosshair');
    elements.iconRetry = document.getElementById('iconRetry');
    elements.btnMenu = document.getElementById('btnMenu');
    elements.drawOverlay = document.getElementById('drawOverlay');
    elements.drawCanvas = document.getElementById('drawCanvas');
    drawContext = elements.drawCanvas?.getContext('2d');
  }

  function attachEvents() {
    elements.btnPrimary?.addEventListener('click', onPrimaryAction);
    elements.btnMenu?.addEventListener('click', async () => {
      try {
        if (window.webapi?.navigation?.goToWidget) {
          await window.webapi.navigation.goToWidget();
        }
      } catch (error) {
        console.warn('[ImagineBug] Failed to switch to widget mode:', error);
      }
      window.location.href = '../GlitchHarvester/index.html';
    });

    if (elements.drawCanvas) {
      elements.drawCanvas.addEventListener('pointerdown', onDrawStart);
      elements.drawCanvas.addEventListener('pointermove', onDrawMove);
      elements.drawCanvas.addEventListener('pointerup', onDrawEnd);
      elements.drawCanvas.addEventListener('pointerleave', onDrawEnd);
    }
  }

  async function ensureOverlayMode() {
    try {
      if (window.webapi?.navigation?.goToOverlay) {
        await window.webapi.navigation.goToOverlay();
      }
    } catch (error) {
      console.warn('[ImagineBug] Failed to switch to overlay mode:', error);
    }
  }

  async function loadStoredBase() {
    const stored = localStorage.getItem(STORAGE_BASE_ID);
    if (!stored) {
      updateUI();
      return;
    }

    try {
      const data = await window.webapi.gh.getBaseStates();
      if (data.success) {
        const found = (data.baseStates || []).find(b => b.id === stored || b.Id === stored);
        if (found) {
          state.baseId = found.id || found.Id;
        } else {
          localStorage.removeItem(STORAGE_BASE_ID);
        }
      }
    } catch (error) {
      console.warn('[ImagineBug] Failed to load base states:', error);
    }

    state.phase = 'idle';
    updateUI();
  }

  function updateUI() {
    if (elements.btnPrimary) {
      elements.btnPrimary.disabled = state.busy;
      elements.btnPrimary.setAttribute(
        'aria-label',
        state.phase === 'idle' ? 'Create Savestate' : 'Retry'
      );
    }
    if (elements.iconCrosshair) {
      elements.iconCrosshair.classList.toggle('hidden', state.phase !== 'idle');
    }
    if (elements.iconRetry) {
      elements.iconRetry.classList.toggle('hidden', state.phase === 'idle');
    }
  }

  function setBusy(isBusy) {
    state.busy = isBusy;
    updateUI();
  }

  async function onPrimaryAction() {
    if (state.busy) return;

    if (state.phase === 'idle') {
      await createSavestate();
      return;
    }

    await resetImagine();
  }

  async function createSavestate() {
    if (state.busy) return;
    setBusy(true);

    try {
      if (state.baseId) {
        await window.webapi.gh.deleteBaseState(state.baseId);
      }

      const name = `ImagineBug ${new Date().toLocaleString()}`;
      const data = await window.webapi.gh.addBaseState(name);
      const base = data.base || data.baseState || data.state || data.result || data.baseState;
      const baseId = base?.id || base?.Id;

      if (!data.success || !baseId) {
        console.warn('[ImagineBug] Failed to create savestate:', data.error);
        return;
      }

      state.baseId = baseId;
      localStorage.setItem(STORAGE_BASE_ID, baseId);
      await window.webapi.gh.selectBase(baseId);
      await window.webapi.emulator.pause();

      state.range = null;
      state.phase = 'idle';
      showDrawOverlay();
    } catch (error) {
      console.warn('[ImagineBug] Error creating savestate:', error);
    } finally {
      setBusy(false);
    }
  }

  async function resetImagine() {
    if (state.busy) return;
    setBusy(true);

    try {
      if (state.baseId) {
        await window.webapi.emulator.pause();
        await window.webapi.gh.selectBase(state.baseId);
        await window.webapi.gh.loadBase(state.baseId);
        await new Promise(resolve => setTimeout(resolve, 50));
      }
      state.range = null;
      state.phase = 'idle';
      hideDrawOverlay();
      await window.webapi.imagine.setTargetedMode(false, null);
      await window.webapi.emulator.resume();
    } catch (error) {
      console.warn('[ImagineBug] Reset failed:', error);
    } finally {
      setBusy(false);
    }
  }

  function showDrawOverlay() {
    if (elements.drawOverlay) {
      elements.drawOverlay.classList.remove('hidden');
    }
    clearCanvas();
    drawStart = null;
    drawCurrent = null;
  }

  function hideDrawOverlay() {
    if (elements.drawOverlay) {
      elements.drawOverlay.classList.add('hidden');
    }
    clearCanvas();
  }

  function resizeCanvas() {
    if (!elements.drawCanvas || !drawContext) return;

    const dpr = window.devicePixelRatio || 1;
    const width = window.innerWidth;
    const height = window.innerHeight;

    elements.drawCanvas.width = Math.floor(width * dpr);
    elements.drawCanvas.height = Math.floor(height * dpr);
    elements.drawCanvas.style.width = `${width}px`;
    elements.drawCanvas.style.height = `${height}px`;

    drawContext.setTransform(dpr, 0, 0, dpr, 0, 0);
    clearCanvas();
  }

  function clearCanvas() {
    if (!drawContext || !elements.drawCanvas) return;
    drawContext.clearRect(0, 0, elements.drawCanvas.width, elements.drawCanvas.height);
  }

  function onDrawStart(event) {
    if (state.busy) return;
    state.drawing = true;
    drawStart = { x: event.clientX, y: event.clientY };
    drawCurrent = { ...drawStart };
    drawSelection();
  }

  function onDrawMove(event) {
    if (!state.drawing) return;
    drawCurrent = { x: event.clientX, y: event.clientY };
    drawSelection();
  }

  function onDrawEnd() {
    if (!state.drawing || !drawStart || !drawCurrent) return;
    state.drawing = false;

    const height = window.innerHeight || 1;
    const minY = Math.max(0, Math.min(drawStart.y, drawCurrent.y));
    const maxY = Math.min(height, Math.max(drawStart.y, drawCurrent.y));

    const totalLines = 240;
    const startLine = clamp(Math.floor((minY / height) * (totalLines - 1)), 0, totalLines - 1);
    const endLine = clamp(Math.floor((maxY / height) * (totalLines - 1)), 0, totalLines - 1);

    const range = {
      start: Math.min(startLine, endLine),
      end: Math.max(startLine, endLine)
    };

    state.range = range;
    hideDrawOverlay();
    configureTargetedImagine(range);
  }

  function drawSelection() {
    if (!drawContext || !drawStart || !drawCurrent) return;
    clearCanvas();

    const x = Math.min(drawStart.x, drawCurrent.x);
    const y = Math.min(drawStart.y, drawCurrent.y);
    const w = Math.abs(drawStart.x - drawCurrent.x);
    const h = Math.abs(drawStart.y - drawCurrent.y);

    drawContext.fillStyle = 'rgba(111, 178, 255, 0.2)';
    drawContext.strokeStyle = 'rgba(111, 178, 255, 0.9)';
    drawContext.lineWidth = 2;
    drawContext.fillRect(x, y, w, h);
    drawContext.strokeRect(x, y, w, h);
  }

  async function configureTargetedImagine(range) {
    if (!range) return;

    try {
      await window.webapi.imagine.setTargetedMode(true, {
        mode: 'ScanlineRange',
        rangeStart: range.start,
        rangeEnd: range.end,
        targetScanline: range.start
      });

      await window.webapi.emulator.resume();
      await imagineBug();
    } catch (error) {
      console.warn('[ImagineBug] Target setup failed:', error);
    }
  }

  async function ensureModelLoaded() {
    if (state.modelLoading) return false;

    try {
      const modelData = await window.webapi.imagine.isModelLoaded();
      if (modelData.success && modelData.modelLoaded) {
        return true;
      }

      state.modelLoading = true;

      let epoch = 1;
      const epochData = await window.webapi.imagine.getEpoch();
      if (epochData.success && Number.isFinite(epochData.epoch)) {
        epoch = epochData.epoch;
      }

      const loadData = await window.webapi.imagine.loadModel(epoch);
      if (loadData.success) {
        return true;
      }

      console.warn('[ImagineBug] Model load failed:', loadData.error);
      return false;
    } catch (error) {
      console.warn('[ImagineBug] Model load error:', error);
      return false;
    } finally {
      state.modelLoading = false;
    }
  }

  async function imagineBug() {
    if (!state.range || !state.baseId || state.busy) return;
    setBusy(true);

    try {
      const modelReady = await ensureModelLoaded();
      if (!modelReady) {
        return;
      }

      await window.webapi.gh.selectBase(state.baseId);

      const data = await window.webapi.imagine.imagineTargetedBug({
        mode: 'ScanlineRange',
        rangeStart: state.range.start,
        rangeEnd: state.range.end,
        targetScanline: state.range.start
      }, true);

      if (data.success) {
        state.phase = 'ready';
      } else {
        console.warn('[ImagineBug] Imagine failed:', data.error);
      }
    } catch (error) {
      console.warn('[ImagineBug] Imagine error:', error);
    } finally {
      setBusy(false);
    }
  }

  function clamp(value, min, max) {
    return Math.max(min, Math.min(max, value));
  }
})();
