// shared/webapi.js - Web API helper for BrokenNes webmodules
(function () {
  'use strict';

  function resolveDefaultBaseUrl() {
    const configuredBaseUrl = window.WEBAPI_BASE || window.__WEBAPI_BASE;
    if (configuredBaseUrl) {
      return configuredBaseUrl.toString().trim();
    }

    return 'http://127.0.0.1:42067';
  }

  const DEFAULT_BASE_URL = resolveDefaultBaseUrl();
  let baseUrl = DEFAULT_BASE_URL.replace(/\/$/, '');
  let defaultTimeoutMs = 15000;

  function setBaseUrl(url) {
    baseUrl = (url || '').toString().trim().replace(/\/$/, '');
  }

  function getBaseUrl() {
    return baseUrl;
  }

  function setDefaultTimeoutMs(timeoutMs) {
    if (Number.isFinite(timeoutMs) && timeoutMs > 0) {
      defaultTimeoutMs = timeoutMs;
    }
  }

  function buildUrl(path) {
    if (!path) return baseUrl || '';
    if (/^https?:\/\//i.test(path)) return path;
    if (!baseUrl) return path;
    const trimmedBase = baseUrl.replace(/\/$/, '');
    let normalizedPath = path.startsWith('/') ? path : `/${path}`;

    if (trimmedBase.endsWith('/api') && normalizedPath.startsWith('/api')) {
      normalizedPath = normalizedPath.replace(/^\/api/, '');
      if (!normalizedPath) {
        normalizedPath = '';
      }
    }

    return `${trimmedBase}${normalizedPath}`;
  }

  function getAlternateBaseUrl() {
    const normalizedBaseUrl = (baseUrl || '').replace(/\/$/, '');
    const proxyBaseUrl = window.location?.protocol === 'https:' && window.location?.hostname === 'app.brokennes'
      ? `${window.location.origin}/api`
      : '';
    const directBaseUrl = 'http://127.0.0.1:42067';

    if (normalizedBaseUrl === directBaseUrl && proxyBaseUrl) {
      return proxyBaseUrl;
    }

    if (proxyBaseUrl && normalizedBaseUrl === proxyBaseUrl) {
      return directBaseUrl;
    }

    return '';
  }

  async function executeFetch(url, fetchOptions) {
    return fetch(url, fetchOptions);
  }

  async function request(path, options = {}) {
    const {
      responseType = 'json',
      cacheBust = false,
      timeoutMs = defaultTimeoutMs,
      json,
      headers,
      noCache,
      ...rest
    } = options;

    let url = buildUrl(path);
    const urlObj = new URL(url, window.location.href);
    if (cacheBust) {
      urlObj.searchParams.set('_t', Date.now().toString());
    }

    const controller = timeoutMs ? new AbortController() : null;
    const timeoutId = timeoutMs
      ? setTimeout(() => controller.abort(), timeoutMs)
      : null;

    const finalHeaders = { ...(headers || {}) };
    let body = rest.body;

    if (json !== undefined) {
      if (!finalHeaders['Content-Type']) {
        finalHeaders['Content-Type'] = 'application/json';
      }
      body = JSON.stringify(json);
    }

    const fetchOptions = {
      ...rest,
      headers: finalHeaders,
      body,
      signal: controller ? controller.signal : undefined
    };

    if (noCache || cacheBust) {
      fetchOptions.cache = 'no-store';
    }

    try {
      let response;

      try {
        response = await executeFetch(urlObj.toString(), fetchOptions);
      } catch (error) {
        const alternateBaseUrl = !/^https?:\/\//i.test(path || '') ? getAlternateBaseUrl() : '';
        if (!alternateBaseUrl) {
          throw error;
        }

        const alternateUrl = new URL(buildUrl(path).replace(baseUrl.replace(/\/$/, ''), alternateBaseUrl.replace(/\/$/, '')), window.location.href);
        if (cacheBust) {
          alternateUrl.searchParams.set('_t', Date.now().toString());
        }

        response = await executeFetch(alternateUrl.toString(), fetchOptions);
      }

      if (responseType === 'raw') {
        return response;
      }

      if (responseType === 'text') {
        const text = await response.text();
        if (!response.ok) {
          return { success: false, status: response.status, error: text || response.statusText, text };
        }
        return { success: true, status: response.status, text };
      }

      const contentType = response.headers.get('content-type') || '';
      let data = null;

      if (contentType.includes('application/json')) {
        data = await response.json();
      } else {
        const text = await response.text();
        data = { success: response.ok, message: text, text };
      }

      if (!response.ok) {
        if (data && typeof data === 'object' && data.success === false) {
          return data;
        }
        return { success: false, status: response.status, error: data?.error || response.statusText };
      }

      return data;
    } catch (error) {
      return { success: false, error: error?.message || 'Network error' };
    } finally {
      if (timeoutId) {
        clearTimeout(timeoutId);
      }
    }
  }

  const webapi = {
    setBaseUrl,
    getBaseUrl,
    setDefaultTimeoutMs,
    request,
    requestJson: (path, options = {}) => request(path, { ...options, responseType: 'json' }),
    requestText: (path, options = {}) => request(path, { ...options, responseType: 'text' }),

    health: () => request('/api/health'),

    memory: {
      getDomains: () => request('/api/memory/domains'),
      getDomainSize: (domainName) => request(`/api/memory/domain/${encodeURIComponent(domainName)}/size`),
      peek: (domain, address) =>
        request(`/api/memory/peek?domain=${encodeURIComponent(domain)}&address=${address}`),
      poke: (domain, address, value) =>
        request('/api/memory/poke', { method: 'POST', json: { Domain: domain, Address: address, Value: value } }),
      peekRange: (domain, address, length) =>
        request(`/api/memory/peek-range?domain=${encodeURIComponent(domain)}&address=${address}&length=${length}`),
      pokeRange: (domain, address, data) =>
        request('/api/memory/poke-range', { method: 'POST', json: { Domain: domain, Address: address, Data: data } })
    },

    cpu: {
      getRegisters: () => request('/api/cpu/registers'),
      setRegisters: (registers) => request('/api/cpu/registers', { method: 'POST', json: registers }),
      getCore: () => request('/api/cpu/core'),
      getCores: () => request('/api/cpu/cores'),
      getState: () => request('/api/cpu/state')
    },

    ppu: {
      getCore: () => request('/api/ppu/core'),
      getCores: () => request('/api/ppu/cores'),
      getFramebuffer: () => request('/api/ppu/framebuffer'),
      getState: () => request('/api/ppu/state'),
      getOam: () => request('/api/ppu/oam')
    },

    apu: {
      getCore: () => request('/api/apu/core'),
      getCores: () => request('/api/apu/cores'),
      getChannels: () => request('/api/apu/channels'),
      setChannelEnableMask: (channelMask) => request('/api/apu/channels/enable', { method: 'POST', json: { channelMask } })
    },

    rtc: {
      getDomains: () => request('/api/rtc/domains'),
      setDomainSelection: (selectedDomains) =>
        request('/api/rtc/domains/selection', { method: 'POST', json: { selectedDomains } }),
      getIntensity: () => request('/api/rtc/intensity'),
      setIntensity: (intensity) => request('/api/rtc/intensity', { method: 'POST', json: { intensity } }),
      getBlastType: () => request('/api/rtc/blast-type'),
      setBlastType: (blastType) => request('/api/rtc/blast-type', { method: 'POST', json: { blastType } }),
      blast: () => request('/api/rtc/blast', { method: 'POST' }),
      getAutoCorrupt: () => request('/api/rtc/auto-corrupt'),
      setAutoCorrupt: (enabled) => request('/api/rtc/auto-corrupt', { method: 'POST', json: { enabled } }),
      letItRip: () => request('/api/rtc/let-it-rip', { method: 'POST' }),
      getCrashBehavior: () => request('/api/rtc/crash-behavior'),
      setCrashBehavior: (behavior) => request('/api/rtc/crash-behavior', { method: 'POST', json: { Behavior: behavior } }),
      getStubbornMode: () => request('/api/rtc/stubborn-mode'),
      setStubbornMode: (enabled) => request('/api/rtc/stubborn-mode', { method: 'POST', json: { enabled } }),
      getLastBlast: () => request('/api/rtc/last-blast')
    },

    gh: {
      getBaseStates: () => request('/api/gh/base-states'),
      addBaseState: (name) => request('/api/gh/base-state', { method: 'POST', json: { name } }),
      selectBaseState: (id) => request(`/api/gh/base-state/${encodeURIComponent(id)}/select`, { method: 'POST', json: {} }),
      selectBase: (id) => request('/api/gh/select-base', { method: 'POST', json: { id } }),
      loadBase: (id) => request('/api/gh/load-base', { method: 'POST', json: id ? { id } : {} }),
      deleteBaseState: (id) => request(`/api/gh/base-state/${encodeURIComponent(id)}`, { method: 'DELETE' }),
      getLoadOnOperation: () => request('/api/gh/load-on-operation'),
      setLoadOnOperation: (enabled) => request('/api/gh/load-on-operation', { method: 'POST', json: { enabled } }),
      corruptAndStash: (id) => request('/api/gh/corrupt-and-stash', { method: 'POST', json: id ? { id } : {} }),
      getStash: () => request('/api/gh/stash'),
      replayStash: (id) => request(`/api/gh/stash/${encodeURIComponent(id)}/replay`, { method: 'POST', json: {} }),
      promoteStash: (id) => request(`/api/gh/stash/${encodeURIComponent(id)}/promote`, { method: 'POST', json: {} }),
      deleteStash: (id) => request(`/api/gh/stash/${encodeURIComponent(id)}`, { method: 'DELETE' }),
      clearStash: () => request('/api/gh/stash', { method: 'DELETE' }),
      getStockpile: () => request('/api/gh/stockpile'),
      replayStockpile: (id) => request(`/api/gh/stockpile/${encodeURIComponent(id)}/replay`, { method: 'POST', json: {} }),
      renameStockpile: (id, name) => request(`/api/gh/stockpile/${encodeURIComponent(id)}/rename`, { method: 'PUT', json: { name } }),
      deleteStockpile: (id) => request(`/api/gh/stockpile/${encodeURIComponent(id)}`, { method: 'DELETE' }),
      exportStockpile: () => request('/api/gh/stockpile/export'),
      importStockpile: (json) => request('/api/gh/stockpile/import', { method: 'POST', json: { json } })
    },

    imagine: {
      isModelLoaded: () => request('/api/imagine/model-loaded'),
      getEpoch: () => request('/api/imagine/epoch'),
      setEpoch: (epoch) => request('/api/imagine/epoch', { method: 'POST', json: { epoch } }),
      loadModel: (epoch) => request('/api/imagine/load-model', { method: 'POST', json: { epoch } }),
      getGenerationParams: () => request('/api/imagine/generation-params'),
      setGenerationParams: (params) => request('/api/imagine/generation-params', { method: 'POST', json: params }),
      freezeAndFetch: () => request('/api/imagine/freeze-and-fetch', { method: 'POST' }),
      getCpuSnapshot: () => request('/api/imagine/cpu-snapshot'),
      runPrediction: () => request('/api/imagine/run-prediction', { method: 'POST' }),
      applyPatch: (pc, bytes) => request('/api/imagine/apply-patch', { method: 'POST', json: { pc, bytes } }),
      imagineABug: (loadOnImagine = false) => request('/api/imagine/imagine-a-bug', { 
        method: 'POST',
        json: { loadOnImagine }
      }),
      imagineTargetedBug: (config, loadOnImagine = false) => request('/api/imagine/imagine-targeted-bug', {
        method: 'POST',
        json: {
          loadOnImagine,
          mode: config?.mode || 'SingleScanline',
          targetScanline: config?.targetScanline ?? 120,
          rangeStart: config?.rangeStart ?? 0,
          rangeEnd: config?.rangeEnd ?? 239
        }
      }),
      getPredictedBytes: () => request('/api/imagine/predicted-bytes'),
      getLastError: () => request('/api/imagine/last-error'),
      setTargetedMode: (enabled, config) => request('/api/imagine/set-targeted-mode', {
        method: 'POST',
        json: {
          enabled: enabled,
          mode: config?.mode || 'SingleScanline',
          targetScanline: config?.targetScanline ?? 120,
          rangeStart: config?.rangeStart ?? 0,
          rangeEnd: config?.rangeEnd ?? 239
        }
      }),
      getTargetedStatus: () => request('/api/imagine/targeted-status')
    },

    achievements: {
      init: (options) => request('/api/achievements/init', { method: 'POST', json: options }),
      getList: () => request('/api/achievements/list'),
      getState: (id) => request(`/api/achievements/state/${encodeURIComponent(id)}`),
      getProgress: (id) => request(`/api/achievements/progress/${encodeURIComponent(id)}`),
      getConditions: (id) => request(`/api/achievements/conditions/${encodeURIComponent(id)}`),
      forceComplete: (id) => request('/api/achievements/force-complete', { method: 'POST', json: { id } }),
      evaluateFrame: () => request('/api/achievements/evaluate-frame', { method: 'POST' }),
      reset: () => request('/api/achievements/reset', { method: 'POST' })
    },

    progression: {
      getState: () => request('/api/progression'),
      getRoster: () => request('/api/progression/roster'),
      unlockEverything: () => request('/api/progression/unlock-everything', { method: 'POST' }),
      claimPending: () => request('/api/progression/claim-pending', { method: 'POST' }),
      acknowledge: (rewardIds) => request('/api/progression/acknowledge', {
        method: 'POST',
        json: { rewardIds: Array.isArray(rewardIds) ? rewardIds : [] }
      }),
      equipBackground: (name) => request('/api/progression/equip-background', { method: 'POST', json: { name } }),
      equipNullProvider: (name) => request('/api/progression/equip-null-provider', { method: 'POST', json: { name } })
    },

    navigation: {
      goToEmulator: () => request('/api/navigation/go-to-emulator', { method: 'POST' }),
      goToOverlay: () => request('/api/navigation/go-to-overlay', { method: 'POST' }),
      goToWidget: () => request('/api/navigation/go-to-widget', { method: 'POST' }),
      goToWeb: () => request('/api/navigation/go-to-web', { method: 'POST' })
    },

    ui: {
      closeMenus: () => request('/api/ui/close-menus', { method: 'POST' }),
      toggleFullscreen: () => request('/api/ui/toggle-fullscreen', { method: 'POST' }),
      hideMenu: () => request('/api/ui/hide-menu', { method: 'POST' }),
      showMenu: () => request('/api/ui/show-menu', { method: 'POST' }),
      openControllerConfig: (playerNumber) => request(`/api/ui/controller/${encodeURIComponent(playerNumber)}/config`, { method: 'POST' })
    },

    cores: {
      list: () => request('/api/cores'),
      apply: ({ cpuId, ppuId, apuId, overrideReason = null } = {}) => request('/api/cores/apply', {
        method: 'POST',
        json: { cpuId, ppuId, apuId, overrideReason }
      })
    },

    card: {
      getUrl: (domain, id) => buildUrl(`/api/card/${encodeURIComponent(domain)}/${encodeURIComponent(id)}`),
      getSvg: (domain, id) => request('/api/card/' + encodeURIComponent(domain) + '/' + encodeURIComponent(id), { responseType: 'text' }),
      getCatalog: () => request('/api/card/catalog')
    },

    audio: {
      playMusic: (filename, loop = true) => request('/api/audio/music/play', { method: 'POST', json: { filename, loop } }),
      requestMusic: (filename, loop = true, fadeDurationMs = 1000) => request('/api/audio/music/request', { method: 'POST', json: { filename, loop, fadeDurationMs } }),
      stopMusic: (fadeDurationMs = 1000) => request('/api/audio/music/stop', { method: 'POST', json: { fadeDurationMs } }),
      playSfx: (filename) => request('/api/audio/sfx/play', { method: 'POST', json: { filename } }),
      getVolume: () => request('/api/audio/volume'),
      setVolume: (musicVolume, sfxVolume) => request('/api/audio/volume', { method: 'POST', json: { musicVolume, sfxVolume } }),
      getStatus: () => request('/api/audio/status'),
      getCurrentMusic: () => request('/api/audio/music/current'),
      listMusic: () => request('/api/audio/music/list'),
      listSfx: () => request('/api/audio/sfx/list')
    },

    emulator: {
      loadBuiltInRom: (filename, preserveShader = false) => request('/api/emulator/load-builtin-rom', { method: 'POST', json: { filename, preserveShader } }),
      pause: () => request('/api/emulator/pause', { method: 'POST' }),
      resume: () => request('/api/emulator/resume', { method: 'POST' }),
      closeRom: () => request('/api/emulator/close-rom', { method: 'POST' }),
      getBackgrounds: () => request('/api/emulator/backgrounds'),
      setBackground: (name) => request('/api/emulator/background', { method: 'POST', json: { name } }),
      getNullProviders: () => request('/api/emulator/null-providers'),
      setNullProvider: (name) => request('/api/emulator/null-provider', { method: 'POST', json: { name } }),
      getCurrentRom: () => request('/api/emulator/current-rom'),
      loadRom: (path) => request('/api/emulator/load-rom', { method: 'POST', json: { path } }),
      loadRomKey: (romKey) => request('/api/emulator/load-rom-key', { method: 'POST', json: { romKey } }),
      loadRomBase64: (name, base64) => request('/api/emulator/load-rom-base64', { method: 'POST', json: { name, base64 } }),
      saveContinueState: () => request('/api/emulator/save-continue-state', { method: 'POST' }),
      quickSaveState: () => request('/api/emulator/quick-save-state', { method: 'POST' }),
      quickLoadState: () => request('/api/emulator/quick-load-state', { method: 'POST' }),
      loadContinueState: (expectedRomName = null) => request('/api/emulator/load-continue-state', {
        method: 'POST',
        json: { expectedRomName }
      })
    },

    shader: {
      getCurrent: () => request('/api/shader/current'),
      setShader: (shaderName, overrideReason = null) => request('/api/shader/set', {
        method: 'POST',
        json: { shaderName, overrideReason }
      }),
      enable: () => request('/api/shader/enable', { method: 'POST' }),
      disable: () => request('/api/shader/disable', { method: 'POST' })
    },

    timejump: {
      validateRom: () => request('/api/timejump/validate-rom'),
      capture: () => request('/api/timejump/capture', { method: 'POST', timeoutMs: 30000 }),
      jump: () => request('/api/timejump/jump', { method: 'POST' }),
      query: (hash) => request('/api/timejump/query', { method: 'POST', json: { hash } }),
      reset: () => request('/api/timejump/reset', { method: 'POST' })
    },
    
    input: {
      pollButtonEvent: () => request('/api/input/button-event')
    }
  };
  
  // Webmodule button event system (X/Y buttons)
  let inputPollInterval = null;
  
  /**
   * Start polling for X/Y button events and dispatch them as custom events
   * Webmodules should call this during initialization to receive button events
   */
  webapi.input.startPolling = function(intervalMs = 50) {
    if (inputPollInterval) {
      console.warn('[WebAPI] Input polling already started');
      return;
    }
    
    console.log('[WebAPI] Starting input polling for X/Y buttons');
    
    inputPollInterval = setInterval(async () => {
      try {
        const result = await webapi.input.pollButtonEvent();
        
        if (result.success && result.hasEvent) {
          const eventName = result.eventType === 'pressed' ? 'buttonPressed' : 'buttonReleased';
          const button = result.button; // "X" or "Y"
          
          // Dispatch as custom event
          const customEvent = new CustomEvent(eventName, {
            detail: { button }
          });
          window.dispatchEvent(customEvent);
          
          // Also dispatch a combined event for convenience
          const genericEvent = new CustomEvent('webmoduleButton', {
            detail: {
              button,
              pressed: result.eventType === 'pressed'
            }
          });
          window.dispatchEvent(genericEvent);
          
          console.log(`[WebAPI] Button ${button} ${result.eventType}`);
        }
      } catch (error) {
        // Silently ignore polling errors to avoid console spam
      }
    }, intervalMs);
  };
  
  /**
   * Stop polling for button events
   */
  webapi.input.stopPolling = function() {
    if (inputPollInterval) {
      clearInterval(inputPollInterval);
      inputPollInterval = null;
      console.log('[WebAPI] Stopped input polling');
    }
  };
  
  // Clean up polling when page unloads
  window.addEventListener('beforeunload', () => {
    webapi.input.stopPolling();
  });

  window.webapi = webapi;
})();
