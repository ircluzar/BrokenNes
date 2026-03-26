// Corruption Slop WebModule - Overlay automation
(() => {
  const api = window.webapi;
  const STOP_NAME = 'Corruption Slop';
  const BASE_INTENSITY = 420;
  const INTERMISSION_CHANCE = 0.15;
  const INTERMISSION_MIN_CYCLES = 10;
  const INTERMISSION_GUARANTEE_AFTER = 25;
  let isRunning = false;
  let loopToken = 0;
  let stopRequested = false;
  let baseStateId = null;
  let cachedBackgrounds = null;
  let cachedNullProviders = null;
  let originalRomPath = null;
  let cycleCount = 0;
  let intensityPercent = 50;
  let knobDragState = null;
  let intensitySyncTimer = null;

  document.addEventListener('DOMContentLoaded', () => {
    if (!api) {
      console.error('[CorruptionSlop] webapi helper not loaded');
      return;
    }

    const stopSquare = document.getElementById('stopSquare');
    if (stopSquare) {
      stopSquare.addEventListener('click', stopAndExit);
    }

    const funnyKnob = document.getElementById('funnyKnob');
    if (funnyKnob) {
      setupFunnyKnob(funnyKnob);
    }

    start();
  });

  async function start() {
    const valid = await ensureGameRunning();
    if (!valid) {
      await goToEmulator();
      return;
    }

    try {
      await api.ui.hideMenu();
    } catch (error) {
      console.warn('[CorruptionSlop] Failed to hide menu:', error);
    }

    try {
      await api.rtc.setAutoCorrupt(false);
    } catch (error) {
      console.warn('[CorruptionSlop] Failed to disable auto-corrupt:', error);
    }

    const configured = await configureRtc();
    if (!configured) {
      await goToEmulator();
      return;
    }

    originalRomPath = await captureCurrentRomPath();

    const baseReady = await createBaseState();
    if (!baseReady) {
      await goToEmulator();
      return;
    }

    cycleCount = 0;

    isRunning = true;
    loopToken += 1;
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

      const intensity = await api.rtc.setIntensity(getScaledIntensity(intensityPercent));
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
        console.log('[CorruptionSlop] Intermission triggered');
        const intermissionOk = await runIntermission();
        if (!intermissionOk) {
          console.warn('[CorruptionSlop] Intermission failed, continuing cycle');
        } else {
          cycleCount = 0;
        }
      }

      const ok = await runCycle();
      if (!ok) {
        break;
      }

      cycleCount += 1;

      const delayMs = randomInt(420, 4200);
      await sleep(delayMs);
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
    if (background) {
      await api.emulator.setBackground(background);
    }

    const provider = await getRandomNullProvider();
    if (provider) {
      await api.emulator.setNullProvider(provider);
    }

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
    stopRequested = true;
    isRunning = false;
    loopToken += 1;

    try {
      await api.rtc.setAutoCorrupt(false);
    } catch (error) {
      console.warn('[CorruptionSlop] Failed to disable auto-corrupt on stop:', error);
    }

    await sleep(50);

    if (baseStateId) {
      try {
        await api.gh.loadBase(baseStateId);
      } catch (error) {
        console.warn('[CorruptionSlop] Failed to reload base state on stop:', error);
      }
    }

    try {
      await api.ui.showMenu();
    } catch (error) {
      console.warn('[CorruptionSlop] Failed to show menu:', error);
    }

    await goToEmulator();
  }

  async function goToEmulator() {
    try {
      await api.navigation.goToEmulator();
    } catch (error) {
      console.error('[CorruptionSlop] Failed to return to emulator:', error);
    }
  }

  function sleep(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
  }

  function shouldIntermission() {
    if (cycleCount < INTERMISSION_MIN_CYCLES) {
      return false;
    }

    if (cycleCount > INTERMISSION_GUARANTEE_AFTER) {
      return true;
    }

    return Math.random() < INTERMISSION_CHANCE;
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

  function randomInt(min, max) {
    return Math.floor(Math.random() * (max - min + 1)) + min;
  }

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

  function setupFunnyKnob(funnyKnob) {
    intensityPercent = parsePercent(funnyKnob?.dataset?.value);
    setFunnyKnobVisuals(funnyKnob, intensityPercent);

    funnyKnob.addEventListener('pointerdown', onFunnyKnobPointerDown);
    funnyKnob.addEventListener('wheel', onFunnyKnobWheel, { passive: false });
    funnyKnob.addEventListener('keydown', onFunnyKnobKeyDown);
  }

  function onFunnyKnobPointerDown(event) {
    if (!event || event.button !== 0) return;
    const funnyKnob = event.currentTarget;
    if (!funnyKnob) return;

    event.preventDefault();
    funnyKnob.focus();
    funnyKnob.setPointerCapture(event.pointerId);
    knobDragState = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startY: event.clientY,
      startPercent: intensityPercent
    };

    funnyKnob.addEventListener('pointermove', onFunnyKnobPointerMove);
    funnyKnob.addEventListener('pointerup', onFunnyKnobPointerUp);
    funnyKnob.addEventListener('lostpointercapture', onFunnyKnobPointerUp);
  }

  function onFunnyKnobPointerMove(event) {
    if (!knobDragState || event.pointerId !== knobDragState.pointerId) return;

    event.preventDefault();
    const dy = knobDragState.startY - event.clientY;
    const dx = event.clientX - knobDragState.startX;
    const nextPercent = knobDragState.startPercent + (dy + (dx * 0.35)) * 0.35;
    onFunnyKnobInput(nextPercent);
  }

  function onFunnyKnobPointerUp(event) {
    const funnyKnob = event.currentTarget;
    if (!funnyKnob) return;

    funnyKnob.removeEventListener('pointermove', onFunnyKnobPointerMove);
    funnyKnob.removeEventListener('pointerup', onFunnyKnobPointerUp);
    funnyKnob.removeEventListener('lostpointercapture', onFunnyKnobPointerUp);
    knobDragState = null;
  }

  function onFunnyKnobWheel(event) {
    if (!event) return;
    event.preventDefault();

    const step = event.shiftKey ? 10 : 2;
    const direction = event.deltaY < 0 ? 1 : -1;
    onFunnyKnobInput(intensityPercent + (direction * step));
  }

  function onFunnyKnobKeyDown(event) {
    if (!event) return;
    const key = event.key;
    let nextPercent = intensityPercent;

    if (key === 'ArrowUp' || key === 'ArrowRight') {
      nextPercent += event.shiftKey ? 10 : 1;
    } else if (key === 'ArrowDown' || key === 'ArrowLeft') {
      nextPercent -= event.shiftKey ? 10 : 1;
    } else if (key === 'PageUp') {
      nextPercent += 10;
    } else if (key === 'PageDown') {
      nextPercent -= 10;
    } else if (key === 'Home') {
      nextPercent = 0;
    } else if (key === 'End') {
      nextPercent = 100;
    } else {
      return;
    }

    event.preventDefault();
    onFunnyKnobInput(nextPercent);
  }

  function onFunnyKnobInput(value) {
    intensityPercent = parsePercent(value);

    const funnyKnob = document.getElementById('funnyKnob');
    if (funnyKnob) {
      setFunnyKnobVisuals(funnyKnob, intensityPercent);
    }

    scheduleIntensitySync();
  }

  function setFunnyKnobVisuals(funnyKnob, percent) {
    const p = parsePercent(percent);
    const rounded = Math.round(p);
    const angle = Math.round(-140 + (p / 100) * 280);
    const tempLabel = getAestheticTemperatureLabel(p);

    funnyKnob.dataset.value = String(rounded);
    funnyKnob.style.setProperty('--knob-angle', `${angle}deg`);
    funnyKnob.setAttribute('aria-valuenow', String(rounded));
    funnyKnob.setAttribute('aria-valuetext', tempLabel);

    const valueNode = document.getElementById('funnyValue');
    if (valueNode) {
      valueNode.textContent = tempLabel;
    }
  }

  function getAestheticTemperatureLabel(percent) {
    const p = parsePercent(percent);

    if (p >= 40 && p <= 60) {
      return 'Mild\nTemp';
    }

    if (p < 40) {
      const cold = Math.round(((40 - p) / 40) * 100);
      return `${cold}%\nCold`;
    }

    const hot = Math.round(((p - 60) / 40) * 100);
    return `${hot}%\nHot`;
  }

  function scheduleIntensitySync() {
    if (intensitySyncTimer) {
      clearTimeout(intensitySyncTimer);
    }

    intensitySyncTimer = setTimeout(async () => {
      intensitySyncTimer = null;
      try {
        await api.rtc.setIntensity(getScaledIntensity(intensityPercent));
      } catch (error) {
        console.warn('[CorruptionSlop] Failed to update intensity from knob:', error);
      }
    }, 60);
  }

  function parsePercent(value) {
    const parsed = Number.parseFloat(value);
    if (!Number.isFinite(parsed)) {
      return 50;
    }

    return Math.max(0, Math.min(100, parsed));
  }

  function getScaledIntensity(percent) {
    const p = parsePercent(percent);
    const multiplier = p <= 50
      ? 0.05 + (p / 50) * 0.95
      : 1 + ((p - 50) / 50) * 2;

    return Math.max(1, Math.round(BASE_INTENSITY * multiplier));
  }
})();
