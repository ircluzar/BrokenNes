// Corruption Slop WebModule - Overlay automation
(() => {
  const api = window.webapi;
  const STOP_NAME = 'Corruption Slop';
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

  document.addEventListener('DOMContentLoaded', () => {
    if (!api) {
      console.error('[CorruptionSlop] webapi helper not loaded');
      return;
    }

    const stopSquare = document.getElementById('stopSquare');
    if (stopSquare) {
      stopSquare.addEventListener('click', stopAndExit);
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

      const intensity = await api.rtc.setIntensity(420);
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
})();
