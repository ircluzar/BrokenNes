// shared/webapi.js - Web API helper for BrokenNes webmodules
(function () {
  'use strict';

  const DEFAULT_BASE_URL = (window.WEBAPI_BASE || window.__WEBAPI_BASE || 'http://127.0.0.1:42067').toString().trim();
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
      const response = await fetch(urlObj.toString(), fetchOptions);

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
      getChannels: () => request('/api/apu/channels')
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
      loadBase: () => request('/api/gh/load-base', { method: 'POST', json: {} }),
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
      imagineABug: () => request('/api/imagine/imagine-a-bug', { method: 'POST' }),
      getPredictedBytes: () => request('/api/imagine/predicted-bytes'),
      getLastError: () => request('/api/imagine/last-error')
    },

    achievements: {
      init: (options) => request('/api/achievements/init', { method: 'POST', json: options }),
      getList: () => request('/api/achievements/list'),
      getState: (id) => request(`/api/achievements/state/${encodeURIComponent(id)}`),
      getProgress: (id) => request(`/api/achievements/progress/${encodeURIComponent(id)}`),
      getConditions: (id) => request(`/api/achievements/conditions/${encodeURIComponent(id)}`),
      forceComplete: (id) => request('/api/achievements/force-complete', { method: 'POST', json: { id } }),
      evaluateFrame: () => request('/api/achievements/evaluate-frame', { method: 'POST' })
    },

    navigation: {
      goToEmulator: () => request('/api/navigation/go-to-emulator', { method: 'POST' }),
      goToOverlay: () => request('/api/navigation/go-to-overlay', { method: 'POST' })
    },

    ui: {
      closeMenus: () => request('/api/ui/close-menus', { method: 'POST' }),
      toggleFullscreen: () => request('/api/ui/toggle-fullscreen', { method: 'POST' })
    },

    cores: {
      list: () => request('/api/cores')
    },

    card: {
      getUrl: (domain, id) => buildUrl(`/api/card/${encodeURIComponent(domain)}/${encodeURIComponent(id)}`),
      getSvg: (domain, id) => request('/api/card/' + encodeURIComponent(domain) + '/' + encodeURIComponent(id), { responseType: 'text' })
    },

    audio: {
      playMusic: (filename, loop = true) => request('/api/audio/music/play', { method: 'POST', json: { filename, loop } }),
      requestMusic: (filename, loop = true, fadeDurationMs = 1000) => request('/api/audio/music/request', { method: 'POST', json: { filename, loop, fadeDurationMs } }),
      stopMusic: (fadeDurationMs = 1000) => request('/api/audio/music/stop', { method: 'POST', json: { fadeDurationMs } }),
      playSfx: (filename) => request('/api/audio/sfx/play', { method: 'POST', json: { filename } }),
      getVolume: () => request('/api/audio/volume'),
      setVolume: (musicVolume, sfxVolume) => request('/api/audio/volume', { method: 'POST', json: { musicVolume, sfxVolume } }),
      getStatus: () => request('/api/audio/status'),
      listMusic: () => request('/api/audio/music/list'),
      listSfx: () => request('/api/audio/sfx/list')
    },

    emulator: {
      loadBuiltInRom: (filename, preserveShader = false) => request('/api/emulator/load-builtin-rom', { method: 'POST', json: { filename, preserveShader } }),
      resume: () => request('/api/emulator/resume', { method: 'POST' })
    },

    shader: {
      getCurrent: () => request('/api/shader/current'),
      setShader: (shaderName) => request('/api/shader/set', { method: 'POST', json: { shaderName } }),
      enable: () => request('/api/shader/enable', { method: 'POST' }),
      disable: () => request('/api/shader/disable', { method: 'POST' })
    }
  };

  window.webapi = webapi;
})();
